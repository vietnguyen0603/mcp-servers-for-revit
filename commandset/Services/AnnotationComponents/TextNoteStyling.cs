using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Leaders, leader attachments and FormattedText styling for text notes,
    ///     shared by create_text_note and modify_annotations. Points on the wire are
    ///     millimetres ({x, y, z?}) and are projected onto the note's view plane.
    /// </summary>
    internal static class TextNoteStyling
    {
        /// <summary>Applies leaderLeftAttachment / leaderRightAttachment when present.</summary>
        public static void ApplyAttachments(TextNote note, JToken item)
        {
            var left = item?.Value<string>("leaderLeftAttachment");
            if (!string.IsNullOrWhiteSpace(left))
                note.LeaderLeftAttachment = DocumentationUtils.ParseEnum(left, LeaderAtachement.TopLine);
            var right = item?.Value<string>("leaderRightAttachment");
            if (!string.IsNullOrWhiteSpace(right))
                note.LeaderRightAttachment = DocumentationUtils.ParseEnum(right, LeaderAtachement.TopLine);
        }

        /// <summary>
        ///     Adds the leaders in <paramref name="leaders" /> ([{end, elbow?, side?, shape?}]).
        ///     Side defaults to the side of the text facing the end point. A straight leader
        ///     without an elbow gets a horizontal shoulder level with the leader anchor
        ///     (the first text line for TopLine attachment).
        /// </summary>
        public static void AddLeaders(Document doc, TextNote note, View view, JToken leaders)
        {
            if (leaders == null || leaders.Type == JTokenType.Null)
                return;
            if (!(leaders is JArray array))
                throw new ArgumentException("'leaders' must be an array of {end, elbow?, side?, shape?}.");
            if (array.Count == 0)
                return;

            doc.Regenerate();
            var scale = view is ViewSheet || view.Scale <= 0 ? 1 : view.Scale;
            var baseDir = note.BaseDirection.Normalize();
            var upDir = note.UpDirection.Normalize();
            var width = note.Width * scale;
            var height = note.Height * scale;
            var textSize = note.TextNoteType?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0;
            if (textSize <= 0)
                textSize = DocumentationUtils.MmToFeet(2.5);
            var lineHeight = textSize * scale;

            // Text box edges along the base direction, relative to Coord (which sits on the top edge).
            double leftEdge, rightEdge;
            switch (note.HorizontalAlignment)
            {
                case HorizontalTextAlignment.Center:
                    leftEdge = -width / 2;
                    rightEdge = width / 2;
                    break;
                case HorizontalTextAlignment.Right:
                    leftEdge = -width;
                    rightEdge = 0;
                    break;
                default:
                    leftEdge = 0;
                    rightEdge = width;
                    break;
            }

            var coord = note.Coord;
            var center = coord + baseDir * ((leftEdge + rightEdge) / 2) - upDir * (height / 2);

            for (var i = 0; i < array.Count; i++)
            {
                var spec = array[i];
                var end = DetailGeometry.ReadPoint(view, spec["end"], $"leaders[{i}].end");

                var sideText = spec.Value<string>("side");
                bool right;
                if (string.IsNullOrWhiteSpace(sideText))
                    right = (end - center).DotProduct(baseDir) >= 0;
                else if (string.Equals(sideText.Trim(), "Right", StringComparison.OrdinalIgnoreCase))
                    right = true;
                else if (string.Equals(sideText.Trim(), "Left", StringComparison.OrdinalIgnoreCase))
                    right = false;
                else
                    throw new ArgumentException($"leaders[{i}].side must be 'Left' or 'Right'.");

                var shape = spec.Value<string>("shape");
                bool arc;
                if (string.IsNullOrWhiteSpace(shape) || string.Equals(shape.Trim(), "Straight", StringComparison.OrdinalIgnoreCase))
                    arc = false;
                else if (string.Equals(shape.Trim(), "Arc", StringComparison.OrdinalIgnoreCase))
                    arc = true;
                else
                    throw new ArgumentException($"leaders[{i}].shape must be 'Straight' or 'Arc'.");

                var leaderType = arc
                    ? right ? TextNoteLeaderTypes.TNLT_ARC_R : TextNoteLeaderTypes.TNLT_ARC_L
                    : right ? TextNoteLeaderTypes.TNLT_STRAIGHT_R : TextNoteLeaderTypes.TNLT_STRAIGHT_L;
                var leader = note.AddLeader(leaderType);
                leader.End = end;
                if (arc)
                    continue;

                var elbowToken = spec["elbow"];
                if (elbowToken != null && elbowToken.Type != JTokenType.Null)
                {
                    leader.Elbow = DetailGeometry.ReadPoint(view, elbowToken, $"leaders[{i}].elbow");
                    continue;
                }

                // Default elbow: horizontal shoulder out from the anchor on the leader's side.
                var attachment = right ? note.LeaderRightAttachment : note.LeaderLeftAttachment;
                var estimatedAnchor = coord + baseDir * (right ? rightEdge : leftEdge) - upDir * (attachment switch
                {
                    LeaderAtachement.Midpoint => height / 2,
                    LeaderAtachement.BottomLine => Math.Max(height - lineHeight / 2, lineHeight / 2),
                    _ => lineHeight / 2
                });
                XYZ anchor;
                try
                {
                    anchor = leader.Anchor ?? estimatedAnchor;
                }
                catch (Exception)
                {
                    anchor = estimatedAnchor;
                }

                var direction = right ? baseDir : -baseDir;
                var shoulder = Math.Max(2 * textSize, DocumentationUtils.MmToFeet(3)) * scale;
                var reach = (end - anchor).DotProduct(direction);
                if (reach > 1e-6)
                    shoulder = Math.Min(shoulder, reach / 2);
                leader.Elbow = anchor + direction * shoulder;
            }
        }

        /// <summary>
        ///     Applies {bold?, italic?, underline?, allCaps?, list?, headingLines?} to the whole note via
        ///     FormattedText. headingLines bolds and underlines the first N paragraphs; a list type is
        ///     then applied only to the paragraphs after the heading.
        /// </summary>
        public static void ApplyFormat(TextNote note, JToken format)
        {
            if (format == null || format.Type == JTokenType.Null)
                return;
            if (format.Type != JTokenType.Object)
                throw new ArgumentException("'format' must be an object.");

            var formatted = note.GetFormattedText();
            var all = formatted.AsTextRange();
            if (all.Length == 0)
                return;

            if (format.Value<bool?>("bold") is bool bold)
                formatted.SetBoldStatus(bold);
            if (format.Value<bool?>("italic") is bool italic)
                formatted.SetItalicStatus(italic);
            if (format.Value<bool?>("underline") is bool underline)
                formatted.SetUnderlineStatus(underline);
            if (format.Value<bool?>("allCaps") is bool allCaps)
                formatted.SetAllCapsStatus(allCaps);

            var paragraphs = Paragraphs(formatted.GetPlainText());
            var headingLines = Math.Max(0, format.Value<int?>("headingLines") ?? 0);
            headingLines = Math.Min(headingLines, paragraphs.Count);
            if (headingLines > 0)
            {
                var heading = Span(paragraphs, 0, headingLines);
                if (heading.Length > 0)
                {
                    formatted.SetBoldStatus(heading, true);
                    formatted.SetUnderlineStatus(heading, true);
                }
            }

            var listText = format.Value<string>("list");
            if (!string.IsNullOrWhiteSpace(listText))
            {
                var listType = DocumentationUtils.ParseEnum(listText, ListType.None);
                if (listType == ListType.Mixed)
                    throw new ArgumentException("format.list cannot be 'Mixed'.");
                if (headingLines > 0)
                {
                    formatted.SetListType(Span(paragraphs, 0, headingLines), ListType.None);
                    if (headingLines < paragraphs.Count)
                    {
                        var body = Span(paragraphs, headingLines, paragraphs.Count - headingLines);
                        if (body.Length > 0)
                            formatted.SetListType(body, listType);
                    }
                }
                else
                {
                    formatted.SetListType(all, listType);
                }
            }

            note.SetFormattedText(formatted);
        }

        /// <summary>(start, length) of each non-empty paragraph, separated by '\r' or '\n'.</summary>
        private static List<(int Start, int Length)> Paragraphs(string text)
        {
            var result = new List<(int, int)>();
            var start = 0;
            for (var i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\r' && text[i] != '\n')
                    continue;
                if (i > start)
                    result.Add((start, i - start));
                start = i + 1;
            }

            return result;
        }

        private static TextRange Span(List<(int Start, int Length)> paragraphs, int first, int count)
        {
            var start = paragraphs[first].Start;
            var last = paragraphs[first + count - 1];
            return new TextRange(start, last.Start + last.Length - start);
        }

        /// <summary>The TextNote behind an element, or a clear error for other element types.</summary>
        public static TextNote RequireTextNote(Element element, string action)
        {
            return element as TextNote
                   ?? throw new ArgumentException(
                       $"{action} applies to text notes only; element {element.Id.GetValue()} is " +
                       $"{element.Category?.Name ?? element.GetType().Name}.");
        }
    }
}
