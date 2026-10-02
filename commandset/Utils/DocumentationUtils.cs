using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    ///     Shared helpers for the view, sheet and annotation commands: unit
    ///     conversion, JSON parsing, category lookup and parameter writes.
    ///     All lengths on the wire are millimetres; Revit internal units are feet.
    /// </summary>
    public static class DocumentationUtils
    {
        public const double MmPerFoot = 304.8;

        public static double MmToFeet(double mm) => mm / MmPerFoot;

        public static double FeetToMm(double feet) => Math.Round(feet * MmPerFoot, 3);

        public static object PointToMm(XYZ point) =>
            point == null ? null : new { x = FeetToMm(point.X), y = FeetToMm(point.Y), z = FeetToMm(point.Z) };

        /// <summary>Reads a {x, y, z?} millimetre point and returns it in feet, or null when absent.</summary>
        public static XYZ ReadPointMm(JToken token)
        {
            if (token == null || token.Type != JTokenType.Object)
                return null;

            var x = token.Value<double?>("x");
            var y = token.Value<double?>("y");
            if (x == null || y == null)
                throw new ArgumentException("Point requires numeric 'x' and 'y' (mm).");

            var z = token.Value<double?>("z") ?? 0;
            return new XYZ(MmToFeet(x.Value), MmToFeet(y.Value), MmToFeet(z));
        }

        public static long? ReadId(JToken token, string name)
        {
            var value = token?[name];
            if (value == null || value.Type == JTokenType.Null)
                return null;
            var id = value.Value<long>();
            return id > 0 ? id : (long?)null;
        }

        public static T GetElement<T>(Document doc, long? id) where T : Element
        {
            return id == null ? null : doc.GetElement(id.Value.ToRevitElementId()) as T;
        }

        public static TEnum ParseEnum<TEnum>(string value, TEnum fallback) where TEnum : struct
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            if (Enum.TryParse(value.Trim(), true, out TEnum parsed) && Enum.IsDefined(typeof(TEnum), parsed))
                return parsed;
            throw new ArgumentException(
                $"Invalid value '{value}'. Expected one of: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}.");
        }

        /// <summary>
        ///     Resolves a category from a BuiltInCategory name ("OST_Walls" or
        ///     "Walls") or a localized display name ("Structural Framing").
        /// </summary>
        public static Category ResolveCategory(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var trimmed = name.Trim();
            var enumName = trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) ? trimmed : "OST_" + trimmed;
            if (Enum.TryParse(enumName, true, out BuiltInCategory bic) && Enum.IsDefined(typeof(BuiltInCategory), bic))
            {
                try
                {
                    var category = Category.GetCategory(doc, bic);
                    if (category != null)
                        return category;
                }
                catch (Exception)
                {
                    // Some built-in categories have no Category object; fall through to name lookup.
                }
            }

            foreach (Category category in doc.Settings.Categories)
            {
                if (string.Equals(category.Name, trimmed, StringComparison.OrdinalIgnoreCase))
                    return category;
            }

            return null;
        }

        public static bool IsCategory(Element element, BuiltInCategory category)
        {
            return element?.Category != null && element.Category.Id.GetValue() == (long)category;
        }

        /// <summary>
        ///     Writes a JSON value to the named parameter. Double parameters are
        ///     written with SetValueString, i.e. in the project's display units.
        ///     Returns null on success or an error message.
        /// </summary>
        public static string SetParameterValue(Element element, string name, JToken value)
        {
            var parameter = element.LookupParameter(name);
            if (parameter == null)
                return $"Parameter '{name}' not found.";
            if (parameter.IsReadOnly)
                return $"Parameter '{name}' is read-only.";

            var text = value == null || value.Type == JTokenType.Null
                ? string.Empty
                : value.Type == JTokenType.Float || value.Type == JTokenType.Integer
                    ? value.Value<double>().ToString(CultureInfo.InvariantCulture)
                    : value.ToString();

            try
            {
                bool ok;
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        ok = parameter.Set(text);
                        break;
                    case StorageType.Integer:
                        ok = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                            ? parameter.Set(i)
                            : bool.TryParse(text, out var b) && parameter.Set(b ? 1 : 0);
                        break;
                    case StorageType.Double:
                        ok = parameter.SetValueString(text);
                        break;
                    case StorageType.ElementId:
                        ok = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                             && parameter.Set(id.ToRevitElementId());
                        break;
                    default:
                        ok = false;
                        break;
                }

                return ok ? null : $"Could not set parameter '{name}' to '{text}'.";
            }
            catch (Exception ex)
            {
                return $"Could not set parameter '{name}': {ex.Message}";
            }
        }

        /// <summary>
        ///     Starts a transaction that silently discards warnings so batch
        ///     documentation commands do not block on modeless dialogs.
        /// </summary>
        public static Transaction StartTransaction(Document doc, string name)
        {
            var transaction = new Transaction(doc, name);
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new WarningSwallower());
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            transaction.Start();
            return transaction;
        }

        /// <summary>
        ///     Runs one action per input item inside a single undoable transaction,
        ///     isolating each item in a sub-transaction so one failure does not
        ///     discard the others. Returns one result object per item, in order.
        /// </summary>
        public static List<JObject> RunBatch(Document doc, string transactionName, JArray items,
            Func<JToken, object> action)
        {
            var results = new List<JObject>();
            using (var transaction = StartTransaction(doc, transactionName))
            {
                for (var index = 0; index < items.Count; index++)
                {
                    var subTransaction = new SubTransaction(doc);
                    subTransaction.Start();
                    try
                    {
                        var result = JObject.FromObject(action(items[index]) ?? new object());
                        subTransaction.Commit();
                        result.AddFirst(new JProperty("success", true));
                        result.AddFirst(new JProperty("index", index));
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        if (subTransaction.HasStarted())
                            subTransaction.RollBack();
                        results.Add(new JObject
                        {
                            ["index"] = index,
                            ["success"] = false,
                            ["message"] = ex.Message
                        });
                    }
                }

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    foreach (var result in results)
                    {
                        result["success"] = false;
                        result["message"] = $"Transaction was not committed ({status}).";
                    }
                }
            }

            return results;
        }

        public static object Summarize(List<JObject> results)
        {
            return new
            {
                succeeded = results.Count(r => r.Value<bool>("success")),
                failed = results.Count(r => !r.Value<bool>("success")),
                results
            };
        }

        public static JArray RequireArray(JObject parameters, string name)
        {
            if (parameters[name] is JArray array && array.Count > 0)
                return array;
            throw new ArgumentException($"'{name}' must be a non-empty array.");
        }

        public static string ViewName(Document doc, ElementId viewId)
        {
            return (doc.GetElement(viewId) as View)?.Name;
        }

        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (var failure in failuresAccessor.GetFailureMessages())
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                        failuresAccessor.DeleteWarning(failure);
                }

                return FailureProcessingResult.Continue;
            }
        }
    }
}
