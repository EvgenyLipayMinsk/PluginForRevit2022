using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>ElementId в Revit 2024 стал 64-битным: один код для 2022–2024.</summary>
    internal static class RevitIds
    {
        public static long IdOf(ElementId id)
        {
#if REVIT2024
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId ToElementId(long id)
        {
#if REVIT2024
            return new ElementId(id);
#else
            return new ElementId(checked((int)id));
#endif
        }
    }
}
