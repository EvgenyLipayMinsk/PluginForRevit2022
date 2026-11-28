using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PluginForRevit2022.Core.Clashes
{
    internal static class ClashReportComparison
    {
        internal sealed class Result
        {
            public readonly List<ClashItem> Added = new List<ClashItem>();
            public readonly List<ClashItem> Removed = new List<ClashItem>();
            public readonly List<(ClashItem before, ClashItem after)> Common = new List<(ClashItem, ClashItem)>();
            public readonly List<string> Ambiguous = new List<string>();
        }

        public static Result Compare(IList<ClashItem> before, IList<ClashItem> after, bool matchElements)
        {
            var result = new Result();
            var oldLeft = new HashSet<ClashItem>(before);
            var newLeft = new HashSet<ClashItem>(after);
            Match(i => i.Test + "|" + i.Key);
            if (matchElements) Match(PairKey);
            result.Removed.AddRange(before.Where(oldLeft.Contains));
            result.Added.AddRange(after.Where(newLeft.Contains));
            return result;

            void Match(Func<ClashItem, string> key)
            {
                var left = oldLeft.Where(i => key(i) != null).GroupBy(key).ToDictionary(g => g.Key, g => g.ToList());
                var right = newLeft.Where(i => key(i) != null).GroupBy(key).ToDictionary(g => g.Key, g => g.ToList());
                foreach (string k in left.Keys.Intersect(right.Keys).OrderBy(x => x))
                {
                    if (left[k].Count != 1 || right[k].Count != 1) { result.Ambiguous.Add(k); continue; }
                    ClashItem a = left[k][0], b = right[k][0];
                    // При наличии ID одинаковое название не должно сопоставить другую пару элементов.
                    if (PairKey(a) != null && PairKey(b) != null && PairKey(a) != PairKey(b)) continue;
                    oldLeft.Remove(a); newLeft.Remove(b); result.Common.Add((a, b));
                }
            }
        }

        private static string PairKey(ClashItem i)
        {
            string Side(ClashSide side) => side.Id == null || string.IsNullOrWhiteSpace(side.File) ? null :
                Path.GetFileNameWithoutExtension(side.File).ToUpperInvariant() + ":" + side.Id;
            string a = Side(i.Item1), b = Side(i.Item2);
            if (a == null || b == null) return null;
            return i.Test + "|" + (string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a);
        }
    }
}
