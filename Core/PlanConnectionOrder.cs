using System;
using System.Collections.Generic;
using System.Linq;

namespace PluginForRevit2022.Core
{
    /// <summary>Черновой обход точек от прибора. Сначала обходится текущий этаж.</summary>
    internal static class PlanConnectionOrder
    {
        private const double Tolerance = 300 / 304.8;

        internal sealed class Point
        {
            public int Index;
            public long Level;
            public double X;
            public double Y;
            public double Z;
        }

        internal static List<int> Build(Point start, IEnumerable<Point> devices, IEnumerable<(Point Lower, Point Upper)> transitions,
            out HashSet<int> review)
        {
            var remaining = devices.ToList();
            var order = new List<int>(remaining.Count);
            review = new HashSet<int>();
            var crossings = transitions.SelectMany(pair => new[] { (entry: pair.Lower, exit: pair.Upper), (entry: pair.Upper, exit: pair.Lower) })
                .ToLookup(pair => pair.entry.Level);

            Point current = start;
            while (remaining.Count > 0)
            {
                var sameFloor = remaining.Where(point => point.Level == current.Level).ToList();
                var availableCrossings = crossings[current.Level];
                var candidates = (sameFloor.Count > 0 ? sameFloor : remaining)
                    .Select(point =>
                    {
                        double viaTransition = sameFloor.Count > 0 ? double.PositiveInfinity : availableCrossings
                            .Where(pair => pair.exit.Level == point.Level)
                            .Select(pair => PlanDistance(current, pair.entry) + Math.Abs(pair.entry.Z - pair.exit.Z)
                                + PlanDistance(pair.exit, point))
                            .DefaultIfEmpty(double.PositiveInfinity).Min();
                        return (point, viaTransition, distance: PlanDistance(current, point)
                            + (sameFloor.Count > 0 ? 0 : Math.Abs(current.Z - point.Z)));
                    })
                    .OrderBy(candidate => double.IsInfinity(candidate.viaTransition))
                    .ThenBy(candidate => double.IsInfinity(candidate.viaTransition) ? candidate.distance : candidate.viaTransition)
                    .ThenBy(candidate => candidate.point.Index).Take(2).ToList();

                var next = candidates[0];
                if (sameFloor.Count == 0 || current.Level == -1 ||
                    (candidates.Count > 1 && candidates[1].distance - next.distance <= Tolerance))
                    review.Add(next.point.Index);

                order.Add(next.point.Index);
                remaining.Remove(next.point);
                current = next.point;
            }
            return order;
        }

        private static double PlanDistance(Point first, Point second)
        {
            double x = first.X - second.X;
            double y = first.Y - second.Y;
            return Math.Sqrt(x * x + y * y);
        }
    }
}
