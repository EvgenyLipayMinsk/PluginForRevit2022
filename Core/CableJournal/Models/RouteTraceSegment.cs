namespace ARBM.CableJournalExporter.Models
{
    public sealed class RouteTraceSegment
    {
        public long RouteElementId { get; set; }
        public double LengthM { get; set; }
        public string Installation { get; set; }
        public double[] FromMm { get; set; }
        public double[] ToMm { get; set; }
    }
}
