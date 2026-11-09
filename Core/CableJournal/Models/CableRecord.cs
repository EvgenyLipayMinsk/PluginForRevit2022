namespace ARBM.CableJournalExporter.Models
{
    /// <summary>
    /// Represents a single cable record extracted from Revit.
    /// Contains all properties required for exporting a cable journal.
    /// </summary>
    public class CableRecord
    {
        /// <summary>
        /// Floor number where the cable starts.
        /// </summary>
        public string StartLevel { get; set; }

        /// <summary>
        /// Starting point of the cable.
        /// </summary>
        public string StartPoint { get; set; }

        /// <summary>
        /// Floor number where the cable ends.
        /// </summary>
        public string EndLevel { get; set; }

        /// <summary>
        /// Ending point of the cable.
        /// </summary>
        public string EndPoint { get; set; }

        /// <summary>
        /// Cable type as defined in Revit.
        /// </summary>
        public string CableType { get; set; }

        /// <summary>
        /// Cable category (signal, power, etc.).
        /// </summary>
        public string CableCategory { get; set; }

        /// <summary>
        /// Purpose or functional designation of the cable line.
        /// </summary>
        public string Purpose { get; set; }

        /// <summary>
        /// Cable length in meters.
        /// </summary>
        public string Length { get; set; }

        /// <summary>
        /// Indicates that the cable length was calculated using a fallback method
        /// instead of the primary route-based algorithm.
        /// </summary>
        public bool IsFallbackForLength { get; set; }

        /// <summary>
        /// Mapping between cable properties and Excel columns.
        /// </summary>
        public ExcelColumnMap ColumnMap { get; set; }
    }

}