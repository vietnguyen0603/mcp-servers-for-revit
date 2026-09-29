using System;
using System.Collections.Generic;
using System.Linq;

namespace RegisterGeometry
{
    /// <summary>
    ///     A cluster of unit directions resolved to a common axis family.
    ///     Membership is greedy: a direction joins the first existing cluster
    ///     whose representative is within tolerance, otherwise it forms a new
    ///     cluster.
    /// </summary>
    public sealed class DirectionCluster
    {
        private readonly List<int> _members;
        private Vector2 _sum;

        public DirectionCluster(AxisFamily family, Vector2 representative, int seedIndex)
        {
            Family = family;
            Representative = representative;
            SeedIndex = seedIndex;
            _members = new List<int> { seedIndex };
            _sum = representative;
        }

        public AxisFamily Family { get; private set; }

        /// <summary>
        ///     Seed representative direction. Updated as members are added.
        /// </summary>
        public Vector2 Representative { get; private set; }

        public int SeedIndex { get; }

        public IReadOnlyList<int> Members => _members;

        public int Count => _members.Count;

        /// <summary>
        ///     Try to absorb <paramref name="direction"/>. Returns true when the
        ///     angle to the current representative is within tolerance.
        /// </summary>
        public bool TryAbsorb(Vector2 direction, int sourceIndex, double toleranceRadians)
        {
            var normalized = direction.Normalized();
            if (normalized == Vector2.Zero) return false;
            double angle = DirectionClassifier.AcuteAngle(Representative, normalized);
            if (angle > toleranceRadians) return false;

            _sum += normalized;
            var newRep = _sum.Normalized();
            if (newRep != Vector2.Zero)
            {
                Representative = newRep;
            }
            _members.Add(sourceIndex);
            return true;
        }
    }

    /// <summary>
    ///     Clusters a sequence of directions, preserving input order in each
    ///     cluster and returning clusters sorted by first-occurrence index.
    /// </summary>
    public static class DirectionClusterer
    {
        public static IReadOnlyList<DirectionCluster> Cluster(
            IReadOnlyList<Vector2> directions,
            ToleranceSettings tolerance)
        {
            if (directions == null) throw new ArgumentNullException(nameof(directions));
            var clusters = new List<DirectionCluster>();
            var classifier = new DirectionClassifier(tolerance);

            for (int i = 0; i < directions.Count; i++)
            {
                var dir = directions[i].Normalized();
                if (dir == Vector2.Zero) continue;

                DirectionCluster? target = null;
                foreach (var cluster in clusters)
                {
                    if (cluster.TryAbsorb(dir, i, tolerance.AngularToleranceRadians))
                    {
                        target = cluster;
                        break;
                    }
                }
                if (target == null)
                {
                    var family = classifier.Classify(dir);
                    clusters.Add(new DirectionCluster(family, dir, i));
                }
            }

            return clusters;
        }

        /// <summary>
        ///     Convenience overload returning one (family, representative, members)
        ///     tuple per cluster for callers that want a value-typed shape.
        /// </summary>
        public static IEnumerable<(AxisFamily Family, Vector2 Representative, IReadOnlyList<int> Members)>
            ClusterAsTuples(IReadOnlyList<Vector2> directions, ToleranceSettings tolerance)
        {
            return Cluster(directions, tolerance).Select(c => (c.Family, c.Representative, (IReadOnlyList<int>)c.Members));
        }
    }
}
