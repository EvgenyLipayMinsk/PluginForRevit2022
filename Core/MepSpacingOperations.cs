using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Зазоры между линейными инженерными сетями (лотки, кондуиты, трубы, воздуховоды) в проекте и связях. Только чтение.</summary>
    internal static class MepSpacingOperations
    {
        private static readonly Dictionary<string, BuiltInCategory> Categories = new Dictionary<string, BuiltInCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["cable_trays"] = BuiltInCategory.OST_CableTray,
            ["conduits"] = BuiltInCategory.OST_Conduit,
            ["pipes"] = BuiltInCategory.OST_PipeCurves,
            ["ducts"] = BuiltInCategory.OST_DuctCurves,
        };

        private sealed class Run
        {
            public string Key;
            public long Id;
            public string Link;
            public string Kind;
            public XYZ A, B;
            /// <summary>Половина ширины (по горизонтали поперёк оси) и высоты сечения, с изоляцией.</summary>
            public double HalfW, HalfH;
            public XYZ Dir, AxisW, AxisH;
            public double MinX, MaxX;
            public HashSet<string> Neighbours = new HashSet<string>();
        }

        public static Dictionary<string, object> Check(Document doc, double clearanceMm, List<string> kinds, bool includeLinks, bool includeInsulation, double[] minMm, double[] maxMm, int maxResults)
        {
            if (clearanceMm < 0) throw new ArgumentException("clearanceMm не может быть отрицательным.");
            List<string> chosen = kinds != null && kinds.Count > 0 ? kinds : Categories.Keys.ToList();
            foreach (string k in chosen) if (!Categories.ContainsKey(k)) throw new ArgumentException($"Категория «{k}»: cable_trays, conduits, pipes или ducts.");
            Outline region = minMm == null || maxMm == null ? null : new Outline(OpsCommon.FromMm(minMm, "minMm"), OpsCommon.FromMm(maxMm, "maxMm"));

            var runs = new List<Run>();
            var sources = new List<(Document, RevitLinkInstance, Transform)> { (doc, null, Transform.Identity) };
            if (includeLinks) sources.AddRange(LinkScope.LoadedLinks(doc).Select(l => (l.GetLinkDocument(), l, l.GetTotalTransform())));
            foreach (var (source, link, t) in sources)
            {
                string prefix = link == null ? "host" : RevitIds.IdOf(link.Id).ToString();
                foreach (string kind in chosen)
                {
                    foreach (Element e in new FilteredElementCollector(source).OfCategory(Categories[kind]).WhereElementIsNotElementType())
                    {
                        if (!(e.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                        XYZ a = t.OfPoint(line.GetEndPoint(0)), b = t.OfPoint(line.GetEndPoint(1));
                        if (region != null && !region.Contains(a, 0) && !region.Contains(b, 0)) continue;
                        var (halfW, halfH) = HalfSize(e, kind, includeInsulation);
                        XYZ dir = (b - a).Normalize();
                        XYZ axisW = Math.Abs(dir.Z) > 0.99 ? XYZ.BasisX : XYZ.BasisZ.CrossProduct(dir).Normalize();
                        double reach = Math.Max(halfW, halfH);
                        runs.Add(new Run
                        {
                            Key = prefix + ":" + RevitIds.IdOf(e.Id), Id = RevitIds.IdOf(e.Id), Link = link?.Name, Kind = kind, A = a, B = b,
                            HalfW = halfW, HalfH = halfH, Dir = dir, AxisW = axisW, AxisH = dir.CrossProduct(axisW).Normalize(),
                            MinX = Math.Min(a.X, b.X) - reach, MaxX = Math.Max(a.X, b.X) + reach,
                            Neighbours = Neighbours(e, prefix),
                        });
                    }
                }
            }

            double clearance = RevitText.ToFeet(clearanceMm);
            var problems = new List<(double gap, Run x, Run y, XYZ at)>();
            List<Run> sorted = runs.OrderBy(r => r.MinX).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                Run x = sorted[i];
                for (int j = i + 1; j < sorted.Count && sorted[j].MinX <= x.MaxX + clearance; j++)
                {
                    Run y = sorted[j];
                    if (x.Neighbours.Contains(y.Key) || y.Neighbours.Contains(x.Key)) continue; // одна трасса через фитинг
                    var (c1, c2) = ClosestPoints(x.A, x.B, y.A, y.B);
                    double gap = Gap(x, y, c2 - c1);
                    if (gap < clearance) problems.Add((gap, x, y, (c1 + c2) / 2));
                }
            }

            return new Dictionary<string, object>
            {
                ["checked"] = runs.Count,
                ["byKind"] = runs.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count()),
                ["requiredMm"] = clearanceMm,
                ["problems"] = problems.Count,
                ["clashes"] = problems.Count(p => p.gap < 0),
                ["items"] = problems.OrderBy(p => p.gap).Take(maxResults).Select(p => new Dictionary<string, object>
                {
                    ["a"] = p.x.Id, ["aLink"] = p.x.Link, ["aKind"] = p.x.Kind,
                    ["b"] = p.y.Id, ["bLink"] = p.y.Link, ["bKind"] = p.y.Kind,
                    ["clearanceMm"] = RevitText.ToMm(p.gap),
                    ["at"] = OpsCommon.Point(p.at),
                }).ToList(),
                ["note"] = "Зазор между наружными гранями по сечению (ширина и высота отдельно), с изоляцией. Учитываются только прямые участки; " +
                           "участки, соединённые напрямую или через один фитинг, не сравниваются. Для косых пересечений результат приблизительный.",
            };
        }

        /// <summary>Зазор между сечениями: по ширине и по высоте первой трассы, берётся больший (разделяющая ось).</summary>
        private static double Gap(Run x, Run y, XYZ between)
        {
            double Extent(Run r, XYZ axis) => r.HalfW * Math.Abs(r.AxisW.DotProduct(axis)) + r.HalfH * Math.Abs(r.AxisH.DotProduct(axis));
            double gapW = Math.Abs(between.DotProduct(x.AxisW)) - Extent(x, x.AxisW) - Extent(y, x.AxisW);
            double gapH = Math.Abs(between.DotProduct(x.AxisH)) - Extent(x, x.AxisH) - Extent(y, x.AxisH);
            return Math.Max(gapW, gapH);
        }

        private static (double halfW, double halfH) HalfSize(Element e, string kind, bool insulation)
        {
            double P(BuiltInParameter p) => e.get_Parameter(p)?.AsDouble() ?? 0;
            double w, h;
            switch (kind)
            {
                case "cable_trays": w = P(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM); h = P(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM); break;
                case "conduits": w = h = P(BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM); break;
                case "pipes": w = h = P(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER); break;
                default:
                    double diameter = P(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                    if (diameter > 0) { w = h = diameter; }
                    else { w = P(BuiltInParameter.RBS_CURVE_WIDTH_PARAM); h = P(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM); }
                    break;
            }
            double extra = insulation ? P(BuiltInParameter.RBS_REFERENCE_INSULATION_THICKNESS) : 0;
            return (w / 2 + extra, h / 2 + extra);
        }

        /// <summary>Участки, соединённые с этим напрямую или через один фитинг.</summary>
        private static HashSet<string> Neighbours(Element e, string prefix)
        {
            var result = new HashSet<string>();
            if (!(e is MEPCurve curve) || curve.ConnectorManager == null) return result;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    Element owner = r.Owner;
                    if (owner == null || owner.Id == e.Id) continue;
                    if (owner is MEPCurve) result.Add(prefix + ":" + RevitIds.IdOf(owner.Id));
                    else if (owner is FamilyInstance fitting && fitting.MEPModel?.ConnectorManager != null)
                    {
                        foreach (Connector fc in fitting.MEPModel.ConnectorManager.Connectors)
                        {
                            foreach (Connector fr in fc.AllRefs)
                            {
                                if (fr.Owner is MEPCurve other && other.Id != e.Id) result.Add(prefix + ":" + RevitIds.IdOf(other.Id));
                            }
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>Ближайшие точки двух отрезков.</summary>
        private static (XYZ c1, XYZ c2) ClosestPoints(XYZ p1, XYZ q1, XYZ p2, XYZ q2)
        {
            XYZ d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            double a = d1.DotProduct(d1), e = d2.DotProduct(d2), f = d2.DotProduct(r);
            double s, t;
            if (a < 1e-12 && e < 1e-12) { s = t = 0; }
            else if (a < 1e-12) { s = 0; t = Math.Max(0, Math.Min(1, f / e)); }
            else
            {
                double c = d1.DotProduct(r);
                if (e < 1e-12) { t = 0; s = Math.Max(0, Math.Min(1, -c / a)); }
                else
                {
                    double b = d1.DotProduct(d2), denom = a * e - b * b;
                    s = denom > 1e-12 ? Math.Max(0, Math.Min(1, (b * f - c * e) / denom)) : 0;
                    t = (b * s + f) / e;
                    if (t < 0) { t = 0; s = Math.Max(0, Math.Min(1, -c / a)); }
                    else if (t > 1) { t = 1; s = Math.Max(0, Math.Min(1, (b - c) / a)); }
                }
            }
            return (p1 + d1 * s, p2 + d2 * t);
        }
    }
}
