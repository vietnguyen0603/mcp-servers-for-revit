using System;
using System.Collections.Generic;

namespace RegisterGeometry
{
    /// <summary>
    ///     Planar curve abstraction. Grids and beam location lines are
    ///     expressed as <see cref="ICurve2"/> so the same geometry primitives
    ///     can serve straight and arc geometry without losing fidelity.
    /// </summary>
    public interface ICurve2
    {
        /// <summary>
        ///     Discriminator for the concrete curve kind. Mirrors the DTO
        ///     enum so callers can serialise without re-deriving.
        /// </summary>
        CurveKind Kind { get; }

        /// <summary>
        ///     Start point of the curve.
        /// </summary>
        Point2 Start { get; }

        /// <summary>
        ///     End point of the curve.
        /// </summary>
        Point2 End { get; }

        /// <summary>
        ///     Centroid / sample point suitable for nearest-grid lookups.
        /// </summary>
        Point2 SamplePoint { get; }

        /// <summary>
        ///     Unit tangent at the curve midpoint, used for direction
        ///     classification. For arcs the tangent points along the arc
        ///     direction at the midpoint.
        /// </summary>
        Vector2 Tangent { get; }

        /// <summary>
        ///     Tight axis-aligned bounding box of the curve.
        /// </summary>
        Bounds Bounds { get; }

        /// <summary>
        ///     Arc length of the curve.
        /// </summary>
        double Length { get; }
    }

    /// <summary>
    ///     Mirrors <c>Models.DataExtraction.Register.CurveKind</c> so the
    ///     geometry primitives can be serialised by handlers without depending
    ///     on the DTO assembly.
    /// </summary>
    public enum CurveKind
    {
        Line = 0,
        Arc = 1,
    }
}
