using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.Core.Clashes;
using PluginForRevit2022.Mcp;
using static PluginForRevit2022.Core.ClashReportFileOperations;
namespace PluginForRevit2022.Core
{
    internal static class ClashViewCaptureOperations
    {
        public static object Capture(UIApplication app, McpArgs args)
        {
            Document doc = app?.ActiveUIDocument?.Document ?? throw new InvalidOperationException("Откройте проект.");
            View view = args.String("view") == null ? doc.ActiveView : ViewOperations.ResolveView(doc, args.String("view"));
            if (view == null || view.IsTemplate || !(view is View3D || view is ViewPlan || view is ViewSection || view is ViewSheet || view is ViewDrafting))
                throw new ArgumentException("Выберите графический вид, который можно экспортировать в изображение.");
            var files = args.StringList("reportPaths");
            ClashItem item = files.Count == 0 ? null : Find(Read(files), args.String("key"));
            string target = args.String("savePath") == null ? null : DiagnosticFiles.Target(args.String("savePath"), ".png", args.Bool("overwrite") ?? false);
            if (item == null && target == null) throw new ArgumentException("Укажите reportPaths и key либо savePath для PNG.");
            bool dry = args.Bool("dryRun") ?? true;
            var result = new Dictionary<string, object> { ["dryRun"] = dry, ["view"] = view.Name, ["key"] = item?.Key, ["savePath"] = target, ["saved"] = false };
            if (dry) return result;
            string folder = Path.Combine(Path.GetTempPath(), "AerBIM-Capture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, "capture"),
                    HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150, ZoomType = ZoomFitType.FitToPage,
                    PixelSize = Math.Max(320, Math.Min(4096, args.Int("pixelSize") ?? 1600)),
                };
                options.SetViewsAndSheets(new List<ElementId> { view.Id });
                using (ViewSession.Open(doc, view)) doc.ExportImage(options);
                var images = Directory.GetFiles(folder, "*.png");
                if (images.Length != 1) throw new IOException("Revit не создал единственный PNG выбранного вида.");
                byte[] png = File.ReadAllBytes(images[0]);
                if (target != null) DiagnosticFiles.Write(target, png, args.Bool("overwrite") ?? false);
                if (item != null) { ClashStatusStore.SetScreenshot(item, png); result["screenshotPath"] = item.ScreenshotPath; }
                result["saved"] = true;
                return result;
            }
            finally
            {
                foreach (string file in Directory.GetFiles(folder)) File.Delete(file);
                Directory.Delete(folder);
            }
        }
    }
}
