using System;
using System.Collections.Generic;

namespace RegisterGeometry
{
    /// <summary>
    ///     Result of analysing the topology of one wall group. The shape is
    ///     derived deterministically from the number of legs and the count of
    ///     endpoint joins:
    ///     <list type="bullet">
    ///         <item>1 leg, 2 free ends: <see cref="WallShape.Planar"/>.</item>
    ///         <item>2 legs joined at 1 corner: <see cref="WallShape.L"/>.</item>
    ///         <item>3 legs joined at 2 corners: <see cref="WallShape.UorT"/>.</item>
    ///         <item>4 legs joined at 4 corners (closed loop): <see cref="WallShape.Box"/>.</item>
    ///         <item>5+ legs or partial closure: <see cref="WallShape.Polygon"/>.</item>
    ///     </list>
    /// </summary>
    public sealed class WallTopology
    {
        public WallTopology(
            int legCount,
            int freeEndCount,
            int cornerCount,
            WallShape shape,
            bool isClosedLoop)
        {
            LegCount = legCount;
            FreeEndCount = freeEndCount;
            CornerCount = cornerCount;
            Shape = shape;
            IsClosedLoop = isClosedLoop;
        }

        public int LegCount { get; }

        public int FreeEndCount { get; }

        public int CornerCount { get; }

        public WallShape Shape { get; }

        public bool IsClosedLoop { get; }

        /// <summary>
        ///     Derive topology from a collection of legs. Inputs may be a group
        ///     of one or many legs; the method inspects endpoints only.
        /// </summary>
        public static WallTopology FromLegs(IReadOnlyList<WallLeg> legs, double toleranceMm)
        {
            if (legs == null || legs.Count == 0)
            {
                return new WallTopology(0, 0, 0, WallShape.Unknown, false);
            }
            if (legs.Count == 1)
            {
                return new WallTopology(1, 2, 0, WallShape.Planar, false);
            }

            // Collect all endpoints, deduplicating close ones.
            var endpoints = new List<Point2>();
            foreach (var leg in legs)
            {
                endpoints.Add(leg.Centreline.Start);
                endpoints.Add(leg.Centreline.End);
            }

            int corners = 0;
            int free = 0;
            bool[] consumed = new bool[endpoints.Count];
            for (int i = 0; i < endpoints.Count; i++)
            {
                if (consumed[i]) continue;
                int matches = 1;
                for (int j = i + 1; j < endpoints.Count; j++)
                {
                    if (consumed[j]) continue;
                    if (endpoints[i].DistanceTo(endpoints[j]) <= toleranceMm)
                    {
                        consumed[j] = true;
                        matches++;
                    }
                }
                consumed[i] = true;
                if (matches >= 2) corners++;
                else free++;
            }

            bool closed = free == 0;
            WallShape shape;
            if (legs.Count == 2 && corners == 1) shape = WallShape.L;
            else if (legs.Count == 3 && corners >= 2) shape = WallShape.UorT;
            else if (legs.Count == 4 && closed) shape = WallShape.Box;
            else if (legs.Count >= 5 || corners >= 3) shape = WallShape.Polygon;
            else shape = WallShape.Unknown;

            return new WallTopology(legs.Count, free, corners, shape, closed);
        }
    }
}
