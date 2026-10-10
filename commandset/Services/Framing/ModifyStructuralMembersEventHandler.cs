using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.Framing
{
    /// <summary>
    ///     Edits placed structural framing and columns selected by filter: type swap,
    ///     structural usage, beam offsets/justification/reference level, column
    ///     base/top levels and offsets, comments and mark. Changes that do not apply
    ///     to a member's category (e.g. topLevel on a beam) are reported as skipped.
    ///     One sub-transaction per member; dryRun only lists what would change.
    /// </summary>
    public class ModifyStructuralMembersEventHandler : JsonParameterEventHandler
    {
        public override string GetName() => "Modify Structural Members";

        protected override AIResult<object> Run(UIDocument uiDoc, JObject parameters)
        {
            var doc = uiDoc.Document;
            var set = parameters["set"] as JObject;
            if (set == null || !set.Properties().Any())
                throw new ArgumentException("'set' needs at least one change.");

            var members = StructuralMemberSelector.Select(doc, parameters["filter"] as JObject);
            var maxElements = parameters.Value<int?>("maxElements") ?? 5000;
            if (members.Count > maxElements)
                throw new ArgumentException($"{members.Count} members match, more than maxElements ({maxElements}); narrow the filter.");

            var lookups = new Dictionary<bool, FramingLookup>
            {
                [true] = new FramingLookup(doc, BuiltInCategory.OST_StructuralColumns, "Structural Columns"),
                [false] = new FramingLookup(doc, BuiltInCategory.OST_StructuralFraming, "Structural Framing")
            };
            var dryRun = parameters.Value<bool?>("dryRun") ?? false;
            var results = dryRun
                ? members.Select((m, index) => new JObject { ["index"] = index, ["success"] = true, ["id"] = m.Id.GetValue(), ["type"] = m.Symbol.FamilyName + ": " + m.Symbol.Name, ["comments"] = m.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() }).ToList()
                : DocumentationUtils.RunBatch(doc, "MCP: Modify Structural Members", new JArray(Enumerable.Range(0, members.Count)),
                    token => Apply(doc, members[token.Value<int>()], set, lookups[StructuralMemberSelector.IsColumn(members[token.Value<int>()])]));

            var summary = new JObject
            {
                ["dryRun"] = dryRun,
                ["matched"] = members.Count,
                ["succeeded"] = results.Count(r => r.Value<bool>("success")),
                ["failed"] = results.Count(r => !r.Value<bool>("success")),
                ["results"] = new JArray(results)
            };
            return Ok($"{(dryRun ? "Would modify" : "Modified")} {summary["succeeded"]} of {members.Count} structural members.", summary);
        }

        private static object Apply(Document doc, FamilyInstance member, JObject set, FramingLookup lookup)
        {
            var isColumn = StructuralMemberSelector.IsColumn(member);
            var changed = new List<string>();
            var skipped = new List<string>();
            var warnings = new List<string>();

            if (set["typeId"] != null || set["familyName"] != null)
            {
                var symbol = lookup.ResolveType(set);
                if (!symbol.IsActive)
                    symbol.Activate();
                if (member.Symbol.Id != symbol.Id)
                {
                    member.Symbol = symbol;
                    changed.Add("type");
                }
            }

            if (set["structuralUsage"] != null)
            {
                if (isColumn)
                {
                    skipped.Add("structuralUsage");
                }
                else
                {
                    var usage = CreateBeamsEventHandler.ParseUsage(set.Value<string>("structuralUsage"));
                    if (usage.HasValue && member.StructuralUsage != usage.Value)
                    {
                        member.StructuralUsage = usage.Value;
                        changed.Add("structuralUsage");
                    }
                }
            }

            var framingOnly = new[] { "referenceLevel", "startOffset", "endOffset", "zJustification", "yJustification" };
            var columnOnly = new[] { "baseLevel", "baseOffset", "topLevel", "topOffset" };
            skipped.AddRange((isColumn ? framingOnly : columnOnly).Where(k => set[k] != null));

            if (isColumn)
            {
                SetLevel(member, set, "baseLevel", BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, lookup, changed, warnings);
                SetLevel(member, set, "topLevel", BuiltInParameter.FAMILY_TOP_LEVEL_PARAM, lookup, changed, warnings);
                SetMm(member, set, "baseOffset", BuiltInParameter.FAMILY_BASE_LEVEL_OFFSET_PARAM, changed, warnings);
                SetMm(member, set, "topOffset", BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM, changed, warnings);
            }
            else
            {
                SetLevel(member, set, "referenceLevel", BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, lookup, changed, warnings);
                SetMm(member, set, "startOffset", BuiltInParameter.STRUCTURAL_BEAM_END0_ELEVATION, changed, warnings);
                // endOffset defaults to startOffset, matching create_beams
                var end = set["endOffset"] != null ? set : new JObject { ["endOffset"] = set["startOffset"] };
                SetMm(member, end, "endOffset", BuiltInParameter.STRUCTURAL_BEAM_END1_ELEVATION, changed, warnings);
                if (set["zJustification"] != null)
                    SetValue(member, BuiltInParameter.Z_JUSTIFICATION, p => p.Set((int)CreateBeamsEventHandler.ParseZJustification(set.Value<string>("zJustification"))), "zJustification", changed, warnings);
                var y = CreateBeamsEventHandler.ParseYJustification(set.Value<string>("yJustification"));
                if (y.HasValue)
                    SetValue(member, BuiltInParameter.Y_JUSTIFICATION, p => p.Set((int)y.Value), "yJustification", changed, warnings);
            }

            if (set["mark"] != null)
                SetValue(member, BuiltInParameter.ALL_MODEL_MARK, p => p.Set(set["mark"].ToString()), "mark", changed, warnings);
            if (set["comments"] != null)
                SetValue(member, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, p => p.Set(set.Value<string>("comments")), "comments", changed, warnings);

            var result = new JObject
            {
                ["id"] = member.Id.GetValue(),
                ["type"] = member.Symbol.FamilyName + ": " + member.Symbol.Name,
                ["changed"] = new JArray(changed)
            };
            if (skipped.Count > 0)
                result["skipped"] = new JArray(skipped);
            if (warnings.Count > 0)
                result["warnings"] = new JArray(warnings);
            return result;
        }

        private static void SetLevel(FamilyInstance member, JObject set, string key, BuiltInParameter id, FramingLookup lookup, List<string> changed, List<string> warnings)
        {
            if (set[key] == null)
                return;
            var level = lookup.ResolveLevel(set[key], key);
            SetValue(member, id, p => p.Set(level.Id), key, changed, warnings);
        }

        private static void SetMm(FamilyInstance member, JObject set, string key, BuiltInParameter id, List<string> changed, List<string> warnings)
        {
            var value = FramingLookup.ReadMm(set, key);
            if (value.HasValue)
                SetValue(member, id, p => p.Set(value.Value), key, changed, warnings);
        }

        private static void SetValue(FamilyInstance member, BuiltInParameter id, Action<Parameter> write, string label, List<string> changed, List<string> warnings)
        {
            var before = warnings.Count;
            FramingLookup.SetParameter(member, id, write, label, warnings);
            if (warnings.Count == before)
                changed.Add(label);
        }
    }
}
