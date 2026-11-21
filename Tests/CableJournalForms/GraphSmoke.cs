using System;
using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Services;

// Числовая геометрия для проверки алгоритма вне процесса Revit.
// Сам Graph извлекается runner-ом из производственного исходника без изменений.
public sealed class XYZ
{
    public double X, Y, Z;
    public XYZ(double x, double y, double z) { X=x; Y=y; Z=z; }
    public double DistanceTo(XYZ p) { return Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z)); }
}
class GraphSmoke
{
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static void Route(RouteLengthCalculator.Graph g, RouteLengthCalculator.Graph.Node a, RouteLengthCalculator.Graph.Node b, double concealed, double tray)
    {
        double length=g.FindShortestPath(a,b);
        Check(Math.Abs(length-concealed-tray)<1e-9, "Shortest distance changed");
        Check(Math.Abs(g.LastPathInstallation.Concealed-concealed)<1e-9, "Concealed path length");
        Check(Math.Abs(g.LastPathInstallation.Tray-tray)<1e-9, "Tray path length");
    }
    static int Main()
    {
        try
        {
            var g=new RouteLengthCalculator.Graph();
            var a=g.AddNode(new XYZ(0,0,0));
            var b=g.AddNode(new XYZ(3,0,0));
            var c=g.AddNode(new XYZ(3,4,0));
            g.AddEdge(a,b,CableInstallation.Concealed,101);
            var tray=g.AddEdge(b,c,CableInstallation.Tray,202);
            Route(g,a,c,3,4);
            Check(g.LastPathTrace.Count==2 && g.LastPathTrace[0].RouteElementId==101 && g.LastPathTrace[1].RouteElementId==202,"Trace source IDs/order");
            Check(Math.Abs(g.LastPathTrace[1].LengthM-4*0.3048)<1e-9,"Trace metres");
            Route(g,c,a,3,4);
            Check(g.LastPathTrace[0].RouteElementId==202 && g.LastPathTrace[0].FromMm[1]==4*304.8,"Reverse trace direction");
            Route(g,b,c,0,4);
            Route(g,a,a,0,0);
            // Разбиение существующего участка, как при добавлении промежуточного узла.
            g.RemoveEdge(tray);
            var middle=g.AddNode(new XYZ(3,2,0));
            g.AddEdge(b,middle,tray.Installation,tray.RouteElementId);
            g.AddEdge(middle,c,tray.Installation,tray.RouteElementId);
            Route(g,a,c,3,4);
            // Более короткий альтернативный путь должен заменить и длину, и её состав.
            var direct=g.AddEdge(a,c,CableInstallation.Tray);
            Route(g,a,c,0,5);
            g.RemoveEdge(direct);
            var disconnected=g.AddNode(new XYZ(20,0,0));
            Check(double.IsPositiveInfinity(g.FindShortestPath(a,disconnected)), "Disconnected route");
            Check(g.LastPathInstallation==null, "Stale installation after unreachable route");
            Check(g.LastPathTrace.Count==0,"Stale path trace");
            var bridge=g.ConnectComponents(c,disconnected);
            Check(!double.IsInfinity(g.FindShortestPath(a,disconnected)), "Bridge route");
            Check(Math.Abs(g.LastPathInstallation.Concealed+g.LastPathInstallation.Tray-g.FindShortestPath(a,disconnected))<1e-9, "Bridge sum");
            g.RemoveEdge(bridge);
            Route(g,a,c,3,4);
            Console.WriteLine("PASS: shortest path installation lengths, reverse, split, alternate path, disconnected, bridge");
            return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
