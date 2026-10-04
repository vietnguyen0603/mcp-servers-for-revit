using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Modify
{
    /// <summary>
    ///     create_project_parameter: creates shared parameter definitions (in a shared
    ///     parameter file, GUID derived from the name) and binds them to categories as
    ///     instance or type project parameters; idempotent (an existing parameter only
    ///     gains missing categories). action:"list" lists every project parameter.
    ///     Revit's shared parameter file setting is restored afterwards.
    /// </summary>
    public class CreateProjectParameterEventHandler : JsonParameterEventHandler
    {
        private const string DefaultFileGroup = "revit-mcp";

        public override string GetName() => "Create Project Parameter";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var definitions = parameters["parameters"] as JArray;
            var action = parameters.Value<string>("action") ?? (definitions?.Count > 0 ? "create" : "list");

            if (string.Equals(action, "list", StringComparison.OrdinalIgnoreCase))
            {
                var list = ListParameters(doc);
                return Ok($"{list.Count} project parameter(s).", new { parameters = list });
            }

            if (definitions == null || definitions.Count == 0) return Fail("Give 'parameters' to create.");
            if (doc.IsFamilyDocument) return Fail("Project parameters need a project document; this is a family document.");

            var path = parameters.Value<string>("sharedParameterFile");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "revit-mcp", "shared-parameters.txt");
            path = Path.GetFullPath(path.Trim());
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (!File.Exists(path)) File.Create(path).Dispose();

            var app = doc.Application;
            var original = app.SharedParametersFilename;
            List<JObject> results;
            try
            {
                app.SharedParametersFilename = path;
                var file = app.OpenSharedParameterFile()
                           ?? throw new InvalidOperationException($"Revit could not open the shared parameter file '{path}'.");
                results = DocumentationUtils.RunBatch(doc, "MCP: Create Project Parameters", definitions,
                    token => CreateOne(doc, file, token as JObject ?? throw new ArgumentException("Each parameter must be an object.")));
            }
            finally
            {
                try
                {
                    if (!string.Equals(app.SharedParametersFilename, original ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        app.SharedParametersFilename = original ?? string.Empty;
                }
                catch (Exception)
                {
                    // Restoring the user's setting is best effort.
                }
            }

            var summary = (JObject)JObject.FromObject(DocumentationUtils.Summarize(results));
            summary["sharedParameterFile"] = path;
            var created = results.Count(r => r.Value<string>("status") == "created");
            var updated = results.Count(r => r.Value<string>("status") == "updated");
            var unchanged = results.Count(r => r.Value<string>("status") == "unchanged");
            var failed = results.Count(r => !r.Value<bool>("success"));
            return Ok($"Project parameters: {created} created, {updated} updated, {unchanged} unchanged, {failed} failed.", summary);
        }

        // ---------------------------------------------------------------- create

        private static object CreateOne(Document doc, DefinitionFile file, JObject item)
        {
            var app = doc.Application;
            var name = (item.Value<string>("name") ?? string.Empty).Trim();
            if (name.Length == 0) throw new ArgumentException("'name' is required.");
            var dataType = (item.Value<string>("dataType") ?? "text").Trim().ToLowerInvariant();
            var isType = string.Equals(item.Value<string>("binding"), "type", StringComparison.OrdinalIgnoreCase);
            var groupKey = item.Value<string>("group");
            var fileGroupName = item.Value<string>("sharedGroup");
            if (string.IsNullOrWhiteSpace(fileGroupName)) fileGroupName = DefaultFileGroup;
            var warnings = new List<string>();

            // Categories
            var categories = app.Create.NewCategorySet();
            var categoryNames = new List<string>();
            foreach (var token in item["categories"] as JArray ?? throw new ArgumentException("'categories' is required."))
            {
                var category = DocumentationUtils.ResolveCategory(doc, token.Value<string>())
                               ?? throw new ArgumentException($"Category '{token}' not found.");
                if (!category.AllowsBoundParameters)
                    throw new ArgumentException($"Category '{category.Name}' does not accept project parameters.");
                if (!categories.Contains(category))
                {
                    categories.Insert(category);
                    categoryNames.Add(category.Name);
                }
            }

            var existing = FindBinding(doc, name);
            string status;
            InternalDefinition internalDefinition;
            Guid? guid = null;

            if (existing.Key != null)
            {
                internalDefinition = existing.Key;
                var binding = existing.Value;
                var existingIsType = binding is TypeBinding;
                if (existingIsType != isType)
                    warnings.Add($"'{name}' already exists as a {(existingIsType ? "type" : "instance")} parameter; kept that binding.");

                var merged = existingIsType ? (ElementBinding)app.Create.NewTypeBinding() : app.Create.NewInstanceBinding();
                foreach (Category category in binding.Categories) merged.Categories.Insert(category);
                var added = 0;
                foreach (Category category in categories)
                {
                    if (merged.Categories.Contains(category)) continue;
                    merged.Categories.Insert(category);
                    added++;
                }

                var groupChanged = groupKey != null && !SameGroup(GetGroup(internalDefinition), groupKey);
                if (added > 0 || groupChanged)
                {
                    var ok = groupKey != null
                        ? ReInsert(doc, internalDefinition, merged, groupKey)
                        : ReInsert(doc, internalDefinition, merged, null);
                    if (!ok) throw new InvalidOperationException($"Revit refused to update the binding of '{name}'.");
                    status = "updated";
                }
                else
                {
                    status = "unchanged";
                }

                if (doc.GetElement(internalDefinition.Id) is SharedParameterElement sharedElement) guid = sharedElement.GuidValue;
                else warnings.Add($"'{name}' is an existing non-shared project parameter; extended in place.");

                CheckDataType(internalDefinition, dataType, name, warnings);
            }
            else
            {
                var external = GetOrCreateExternal(doc, file, name, dataType, fileGroupName, item, warnings);
                guid = external.GUID;
                var binding = isType ? (ElementBinding)app.Create.NewTypeBinding(categories) : app.Create.NewInstanceBinding(categories);
                var inserted = Insert(doc, external, binding, groupKey ?? "identityData");
                if (!inserted)
                    throw new InvalidOperationException($"Revit refused to bind '{name}' (a parameter with that name or GUID may already exist).");
                status = "created";
                internalDefinition = SharedParameterElement.Lookup(doc, external.GUID)?.GetDefinition()
                                     ?? FindBinding(doc, name).Key;
            }

            var varyToken = item["varyByGroup"];
            if (varyToken != null && varyToken.Type == JTokenType.Boolean && internalDefinition != null)
            {
                if (FindBinding(doc, name).Value is TypeBinding)
                {
                    warnings.Add("varyByGroup ignored for a type parameter.");
                }
                else
                {
                    try
                    {
                        if (internalDefinition.VariesAcrossGroups != varyToken.Value<bool>())
                        {
                            internalDefinition.SetAllowVaryBetweenGroups(doc, varyToken.Value<bool>());
                            if (status == "unchanged") status = "updated";
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"varyByGroup not applied: {ex.Message}");
                    }
                }
            }

            var final = FindBinding(doc, name);
            return new
            {
                name,
                status,
                guid = guid?.ToString(),
                binding = final.Value is TypeBinding ? "type" : "instance",
                dataType = final.Key == null ? dataType : DataTypeLabel(final.Key),
                group = final.Key == null ? null : GroupLabel(final.Key),
                categories = final.Value == null
                    ? categoryNames
                    : final.Value.Categories.Cast<Category>().Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                warnings
            };
        }

        private static ExternalDefinition GetOrCreateExternal(Document doc, DefinitionFile file, string name, string dataType,
            string fileGroupName, JObject item, List<string> warnings)
        {
            foreach (DefinitionGroup group in file.Groups)
            {
                if (group.Definitions.get_Item(name) is ExternalDefinition found)
                {
                    if (!SameDataType(found, dataType))
                        warnings.Add($"The shared parameter file already defines '{name}' with another data type; that definition is used.");
                    return found;
                }
            }

            // A project shared parameter of the same name (not in the file) keeps its GUID.
            var guidText = item.Value<string>("guid");
            Guid guid;
            if (!string.IsNullOrWhiteSpace(guidText)) guid = Guid.Parse(guidText);
            else
            {
                var sharedInProject = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement))
                    .Cast<SharedParameterElement>().FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.Ordinal));
                guid = sharedInProject?.GuidValue ?? StableGuid(name);
            }

            var fileGroup = file.Groups.get_Item(fileGroupName) ?? file.Groups.Create(fileGroupName);
            var options = new ExternalDefinitionCreationOptions(name, SpecFor(dataType))
            {
                GUID = guid,
                Visible = true,
                UserModifiable = true
            };
            var description = item.Value<string>("description");
            if (!string.IsNullOrEmpty(description)) options.Description = description;
            return fileGroup.Definitions.Create(options) as ExternalDefinition
                   ?? throw new InvalidOperationException($"Could not create the shared definition '{name}'.");
        }

        /// <summary>Deterministic GUID per parameter name, so re-runs and other projects agree.</summary>
        internal static Guid StableGuid(string name)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes("revit-mcp/shared-parameter/" + name.Trim().ToLowerInvariant()));
                bytes[7] = (byte)((bytes[7] & 0x0F) | 0x30); // version 3 (name based, MD5)
                bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
                return new Guid(bytes);
            }
        }

        private static KeyValuePair<InternalDefinition, ElementBinding> FindBinding(Document doc, string name)
        {
            var iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                if (iterator.Key is InternalDefinition definition && string.Equals(definition.Name, name, StringComparison.Ordinal))
                    return new KeyValuePair<InternalDefinition, ElementBinding>(definition, iterator.Current as ElementBinding);
            }
            return new KeyValuePair<InternalDefinition, ElementBinding>(null, null);
        }

        // ---------------------------------------------------------------- list

        private static List<object> ListParameters(Document doc)
        {
            var list = new List<KeyValuePair<string, object>>();
            var iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                if (!(iterator.Key is InternalDefinition definition)) continue;
                var binding = iterator.Current as ElementBinding;
                var shared = doc.GetElement(definition.Id) as SharedParameterElement;
                list.Add(new KeyValuePair<string, object>(definition.Name, new
                {
                    name = definition.Name,
                    id = definition.Id.GetValue(),
                    binding = binding is TypeBinding ? "type" : "instance",
                    dataType = DataTypeLabel(definition),
                    group = GroupLabel(definition),
                    shared = shared != null,
                    guid = shared?.GuidValue.ToString(),
                    varyByGroup = definition.VariesAcrossGroups,
                    categories = binding?.Categories.Cast<Category>().Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal).ToList()
                                 ?? new List<string>()
                }));
            }

            return list.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Value).ToList();
        }

        // ---------------------------------------------------------------- version-specific API

#if REVIT2022_OR_GREATER
        private static ForgeTypeId SpecFor(string dataType)
        {
            switch (dataType)
            {
                case "text": return SpecTypeId.String.Text;
                case "length": return SpecTypeId.Length;
                case "integer": return SpecTypeId.Int.Integer;
                case "number": return SpecTypeId.Number;
                case "yesno": return SpecTypeId.Boolean.YesNo;
                case "material": return SpecTypeId.Reference.Material;
                default: throw new ArgumentException($"Unknown dataType '{dataType}' (text, length, integer, number, yesno, material).");
            }
        }

        private static bool SameDataType(Definition definition, string dataType)
        {
            try { return definition.GetDataType() == SpecFor(dataType); }
            catch (Exception) { return true; }
        }

        private static ForgeTypeId GroupFor(string key)
        {
            switch ((key ?? "identityData").Trim().ToLowerInvariant())
            {
                case "identitydata": return GroupTypeId.IdentityData;
                case "text": return GroupTypeId.Text;
                case "constraints": return GroupTypeId.Constraints;
                case "dimensions": return GroupTypeId.Geometry;
                case "data": return GroupTypeId.Data;
                case "general": return GroupTypeId.General;
                case "construction": return GroupTypeId.Construction;
                case "structural": return GroupTypeId.Structural;
                case "materials": return GroupTypeId.Materials;
                case "graphics": return GroupTypeId.Graphics;
                case "phasing": return GroupTypeId.Phasing;
                case "other": return new ForgeTypeId(string.Empty);
                default: throw new ArgumentException($"Unknown group '{key}'.");
            }
        }

        private static ForgeTypeId GetGroup(InternalDefinition definition) => definition.GetGroupTypeId();

        private static bool SameGroup(ForgeTypeId current, string key) => current == GroupFor(key);

        private static bool Insert(Document doc, Definition definition, ElementBinding binding, string groupKey) =>
            doc.ParameterBindings.Insert(definition, binding, GroupFor(groupKey));

        private static bool ReInsert(Document doc, Definition definition, ElementBinding binding, string groupKey) =>
            doc.ParameterBindings.ReInsert(definition, binding,
                groupKey == null ? ((InternalDefinition)definition).GetGroupTypeId() : GroupFor(groupKey));

        private static string DataTypeLabel(Definition definition)
        {
            try
            {
                var spec = definition.GetDataType();
                foreach (var key in new[] { "text", "length", "integer", "number", "yesno", "material" })
                    if (spec == SpecFor(key)) return key;
                return LabelUtils.GetLabelForSpec(spec);
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static string GroupLabel(InternalDefinition definition)
        {
            try
            {
                var group = definition.GetGroupTypeId();
                return group == null || string.IsNullOrEmpty(group.TypeId) ? "Other" : LabelUtils.GetLabelForGroup(group);
            }
            catch (Exception)
            {
                return "Other";
            }
        }
#else
        private static ParameterType SpecFor(string dataType)
        {
            switch (dataType)
            {
                case "text": return ParameterType.Text;
                case "length": return ParameterType.Length;
                case "integer": return ParameterType.Integer;
                case "number": return ParameterType.Number;
                case "yesno": return ParameterType.YesNo;
                case "material": return ParameterType.Material;
                default: throw new ArgumentException($"Unknown dataType '{dataType}' (text, length, integer, number, yesno, material).");
            }
        }

        private static bool SameDataType(Definition definition, string dataType) => definition.ParameterType == SpecFor(dataType);

        private static BuiltInParameterGroup GroupFor(string key)
        {
            switch ((key ?? "identityData").Trim().ToLowerInvariant())
            {
                case "identitydata": return BuiltInParameterGroup.PG_IDENTITY_DATA;
                case "text": return BuiltInParameterGroup.PG_TEXT;
                case "constraints": return BuiltInParameterGroup.PG_CONSTRAINTS;
                case "dimensions": return BuiltInParameterGroup.PG_GEOMETRY;
                case "data": return BuiltInParameterGroup.PG_DATA;
                case "general": return BuiltInParameterGroup.PG_GENERAL;
                case "construction": return BuiltInParameterGroup.PG_CONSTRUCTION;
                case "structural": return BuiltInParameterGroup.PG_STRUCTURAL;
                case "materials": return BuiltInParameterGroup.PG_MATERIALS;
                case "graphics": return BuiltInParameterGroup.PG_GRAPHICS;
                case "phasing": return BuiltInParameterGroup.PG_PHASING;
                case "other": return BuiltInParameterGroup.INVALID;
                default: throw new ArgumentException($"Unknown group '{key}'.");
            }
        }

        private static BuiltInParameterGroup GetGroup(InternalDefinition definition) => definition.ParameterGroup;

        private static bool SameGroup(BuiltInParameterGroup current, string key) => current == GroupFor(key);

        private static bool Insert(Document doc, Definition definition, ElementBinding binding, string groupKey) =>
            doc.ParameterBindings.Insert(definition, binding, GroupFor(groupKey));

        private static bool ReInsert(Document doc, Definition definition, ElementBinding binding, string groupKey) =>
            doc.ParameterBindings.ReInsert(definition, binding,
                groupKey == null ? ((InternalDefinition)definition).ParameterGroup : GroupFor(groupKey));

        private static string DataTypeLabel(Definition definition)
        {
            foreach (var key in new[] { "text", "length", "integer", "number", "yesno", "material" })
                if (definition.ParameterType == SpecFor(key)) return key;
            return definition.ParameterType.ToString();
        }

        private static string GroupLabel(InternalDefinition definition)
        {
            try
            {
                return definition.ParameterGroup == BuiltInParameterGroup.INVALID ? "Other" : LabelUtils.GetLabelFor(definition.ParameterGroup);
            }
            catch (Exception)
            {
                return "Other";
            }
        }
#endif

        private static void CheckDataType(Definition definition, string dataType, string name, List<string> warnings)
        {
            if (!SameDataType(definition, dataType))
                warnings.Add($"'{name}' already exists with data type {DataTypeLabel(definition)}; requested {dataType} was not applied.");
        }
    }
}
