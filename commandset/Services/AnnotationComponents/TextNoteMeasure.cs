using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Services.AnnotationComponents
{
    /// <summary>
    ///     Text note extents for create_text_note (measure / fitWidth) and
    ///     measure_text_notes. TextElement.Width/Height are paper feet; model
    ///     sizes are paper x view scale (sheets 1:1). The unwrapped size comes
    ///     from a temporary unwrapped copy of the note (same type, alignment and
    ///     formatted text) that is deleted again, so callers must have a
    ///     transaction open.
    /// </summary>
    internal static class TextNoteMeasure
    {
        /// <summary>Paper size of the note's text without wrapping (feet).</summary>
        public struct Unwrapped
        {
            public double Width;
            public double Height;
        }

        public static double ScaleFactor(View view)
        {
            return view == null || view is ViewSheet || view.Scale <= 0 ? 1 : view.Scale;
        }

        /// <summary>Number of paragraphs (hard line breaks) in the note's text.</summary>
        public static int ParagraphCount(TextNote note)
        {
            var text = (note.Text ?? string.Empty).Replace("\r\n", "\r").Replace('\n', '\r').TrimEnd('\r');
            return text.Length == 0 ? 1 : text.Split('\r').Length;
        }

        /// <summary>
        ///     Unwrapped paper size. A note without wrapping is its own measure; otherwise a
        ///     temporary unwrapped copy is created, measured and deleted (needs an open transaction).
        /// </summary>
        public static Unwrapped MeasureUnwrapped(Document doc, TextNote note)
        {
            if (!note.IsTextWrappingActive)
                return new Unwrapped { Width = note.Width, Height = note.Height };

            var options = new TextNoteOptions(note.GetTypeId())
            {
                HorizontalAlignment = note.HorizontalAlignment
            };
            var temp = TextNote.Create(doc, note.OwnerViewId, note.Coord, note.Text, options);
            try
            {
                temp.SetFormattedText(note.GetFormattedText());
                doc.Regenerate();
                return new Unwrapped { Width = temp.Width, Height = temp.Height };
            }
            finally
            {
                doc.Delete(temp.Id);
            }
        }

        /// <summary>
        ///     Sets the note's wrap width to its unwrapped width (plus a hair), widening in small
        ///     steps if Revit still wraps. Returns the width set (paper feet).
        /// </summary>
        public static double FitWidth(Document doc, TextNote note)
        {
            doc.Regenerate();
            var natural = MeasureUnwrapped(doc, note);
            var lineHeight = natural.Height / Math.Max(1, ParagraphCount(note));
            var min = note.GetMinimumAllowedWidth();
            var max = note.GetMaximumAllowedWidth();
            var pad = DocumentationUtils.MmToFeet(0.1);
            var target = Math.Min(max, Math.Max(min, natural.Width + pad));
            note.Width = target;
            doc.Regenerate();
            for (var i = 0; i < 8 && note.Height > natural.Height + lineHeight * 0.25 && target < max; i++)
            {
                target = Math.Min(max, target * 1.02 + pad);
                note.Width = target;
                doc.Regenerate();
            }

            return target;
        }

        /// <summary>Measured extents of a regenerated note; <paramref name="unwrapped" /> null skips the wrap analysis.</summary>
        public static object Describe(Document doc, TextNote note, Unwrapped? unwrapped)
        {
            var view = doc.GetElement(note.OwnerViewId) as View;
            var factor = ScaleFactor(view);
            var paperWidth = note.Width;
            var paperHeight = note.Height;
            var paragraphs = ParagraphCount(note);

            int? lineCount = null;
            bool? wrapped = null;
            double? unwrappedWidth = null;
            if (unwrapped is Unwrapped natural && natural.Height > 1e-9)
            {
                var lineHeight = natural.Height / paragraphs;
                lineCount = Math.Max(1, (int)Math.Round(paperHeight / lineHeight));
                wrapped = paperHeight > natural.Height + lineHeight * 0.25;
                unwrappedWidth = natural.Width;
            }

            return new
            {
                textNoteId = note.Id.GetValue(),
                viewId = note.OwnerViewId.GetValue(),
                viewScale = factor,
                textNoteTypeId = note.GetTypeId().GetValue(),
                paper = new { width = DocumentationUtils.FeetToMm(paperWidth), height = DocumentationUtils.FeetToMm(paperHeight) },
                model = new
                {
                    width = DocumentationUtils.FeetToMm(paperWidth * factor),
                    height = DocumentationUtils.FeetToMm(paperHeight * factor)
                },
                wrappingActive = note.IsTextWrappingActive,
                widthSetting = note.IsTextWrappingActive ? DocumentationUtils.FeetToMm(paperWidth) : (double?)null,
                minimumWidth = DocumentationUtils.FeetToMm(note.GetMinimumAllowedWidth()),
                unwrappedWidth = unwrappedWidth == null ? (double?)null : DocumentationUtils.FeetToMm(unwrappedWidth.Value),
                paragraphCount = paragraphs,
                lineCount,
                wrapped,
                rotationDegrees = Math.Round(RotationDegrees(note, view), 3),
                bbox = BoundingBox(note, view),
                box = Box(note, factor)
            };
        }

        private static double RotationDegrees(TextNote note, View view)
        {
            if (view == null)
                return 0;
            var baseDir = note.BaseDirection;
            var angle = Math.Atan2(baseDir.DotProduct(view.UpDirection), baseDir.DotProduct(view.RightDirection)) * 180 / Math.PI;
            return Math.Abs(angle) < 1e-9 ? 0 : angle;
        }

        private static object BoundingBox(TextNote note, View view)
        {
            var box = note.get_BoundingBox(view);
            if (box == null)
                return null;
            var min = box.Min;
            var max = box.Max;
            double width = 0, height = 0;
            if (view != null)
            {
                // Extents along the view's right/up directions over the 8 box corners.
                var corners = new List<XYZ>();
                foreach (var x in new[] { min.X, max.X })
                foreach (var y in new[] { min.Y, max.Y })
                foreach (var z in new[] { min.Z, max.Z })
                    corners.Add(new XYZ(x, y, z));
                var across = corners.Select(c => c.DotProduct(view.RightDirection)).ToList();
                var up = corners.Select(c => c.DotProduct(view.UpDirection)).ToList();
                width = across.Max() - across.Min();
                height = up.Max() - up.Min();
            }

            return new
            {
                min = DocumentationUtils.PointToMm(min),
                max = DocumentationUtils.PointToMm(max),
                width = DocumentationUtils.FeetToMm(width),
                height = DocumentationUtils.FeetToMm(height)
            };
        }

        /// <summary>Text box corners (model mm) from Coord, the alignments and the base/up directions.</summary>
        private static object Box(TextNote note, double factor)
        {
            var baseDir = note.BaseDirection.Normalize();
            var upDir = note.UpDirection.Normalize();
            var width = note.Width * factor;
            var height = note.Height * factor;
            double alongX;
            switch (note.HorizontalAlignment)
            {
                case HorizontalTextAlignment.Center:
                    alongX = 0.5;
                    break;
                case HorizontalTextAlignment.Right:
                    alongX = 1;
                    break;
                default:
                    alongX = 0;
                    break;
            }

            double alongY;
            switch (note.VerticalAlignment)
            {
                case VerticalTextAlignment.Middle:
                    alongY = 0.5;
                    break;
                case VerticalTextAlignment.Bottom:
                    alongY = 1;
                    break;
                default:
                    alongY = 0;
                    break;
            }

            var topLeft = note.Coord - baseDir * (width * alongX) + upDir * (height * alongY);
            var topRight = topLeft + baseDir * width;
            var bottomLeft = topLeft - upDir * height;
            var bottomRight = bottomLeft + baseDir * width;
            return new
            {
                topLeft = DocumentationUtils.PointToMm(topLeft),
                topRight = DocumentationUtils.PointToMm(topRight),
                bottomRight = DocumentationUtils.PointToMm(bottomRight),
                bottomLeft = DocumentationUtils.PointToMm(bottomLeft),
                center = DocumentationUtils.PointToMm((topLeft + bottomRight) / 2)
            };
        }
    }
}
