using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARBM.CableJournalExporter.Systems.Security.Models
{
    // =====================================================================
    // Унаследованные типы для совместимости
    // Legacy types for compatibility
    // =====================================================================

    /// <summary>
    /// Parsed controller identifier from ARBM_ПС_Имя изделия, e.g. "ARK.1.1".
    /// </summary>
    public class ControllerId
    {
        public string ControllerPrefix { get; set; }   // "ARK"
        public int FloorNumber { get; set; }           // 1
        public int ControllerNumber { get; set; }      // 1
    }

    /// <summary>
    /// Identifier of an expander, e.g. "AE.1.1.1".
    /// </summary>
    public class ExpanderId
    {
        public string Prefix { get; set; } = "AE";
        public int FloorNumber { get; set; }
        public int ControllerNumber { get; set; }
        public int ExpanderNumber { get; set; }

        public override string ToString()
        {
            return $"{Prefix}.{FloorNumber}.{ControllerNumber}.{ExpanderNumber}";
        }
    }

    /// <summary>
    /// Expander that holds additional loop groups beyond the first 8.
    /// </summary>
    public class Expander
    {
        public ExpanderId ExpanderId { get; set; }
        public List<LoopGroup> Loops { get; set; } = new List<LoopGroup>();
    }

    /// <summary>
    /// Parsed device address from ARBM_ПСБ_Адрес устройства, e.g. "1.1".
    /// </summary>
    public class DeviceAddress
    {
        public int ControllerNumber { get; set; }      // 1
        public int LoopNumber { get; set; }            // 1
    }

    /// <summary>
    /// Group of devices belonging to the same loop (шлейф).
    /// </summary>
    public class LoopGroup
    {
        public int LoopNumber { get; set; }
        public List<FamilyInstance> Devices { get; set; } = new List<FamilyInstance>();
    }

    /// <summary>
    /// Controller with its associated loop groups and optional expanders.
    /// </summary>
    public class ControllerWithLoops
    {
        public FamilyInstance Controller { get; set; }
        public ControllerId ControllerId { get; set; }
        public List<LoopGroup> Loops { get; set; } = new List<LoopGroup>();

        /// <summary>
        /// Expanders that hold additional loop groups (beyond the first 8).
        /// </summary>
        public List<Expander> Expanders { get; set; } = new List<Expander>();

        /// <summary>
        /// Indicates whether this controller has any expanders.
        /// </summary>
        public bool HasExpanders()
        {
            return Expanders != null && Expanders.Count > 0;
        }
    }
}
