using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OfficeOpenXml;
using PluginForRevit2022.Core.Clashes;

// Журнал замены ID требует Revit; в этих тестах ID отсутствуют.
namespace PluginForRevit2022.Core.Clashes
{
    internal static class ElementIdJournal
    {
        public static IEnumerable<long> Resolve(string file,long id) { throw new Exception("Unexpected ID lookup in screenshot test"); }
    }
}
class Smoke
{
    static string output;
    static void Check(bool v,string label) { if(!v) throw new Exception(label); }
    static ClashItem Item(string source,string key,string name="Конфликт2") => new ClashItem { Source=source,Key=key,Name=name,Test="Проверка",ChangedAt="2026-11-21 10:00:00",ChangedBy="Test" };
    static ClashItem Reload(string source,string key,string original=null)
    {
        var report=new ClashReport(); report.Files.Add(source);
        var item=Item(source,key); item.ImagePath=original; report.Items.Add(item);
        ClashStatusStore.Load(report);
        Check(report.Warnings.Count==0,string.Join(";",report.Warnings));
        return item;
    }
    static byte[] Image(int w,int h,byte red)
    {
        var pixels=new byte[w*h*4];
        for(int p=0;p<pixels.Length;p+=4) { pixels[p]=60; pixels[p+1]=100; pixels[p+2]=red; pixels[p+3]=255; }
        return ClashScreenshot.Encode(BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,pixels,w*4));
    }
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            output=args[0]; Directory.CreateDirectory(output);
            byte[] landscape=Image(640,360,220), portrait=Image(200,600,40);
            string source=Path.Combine(output,"report.html"); File.WriteAllText(source,"<html></html>");
            string original=Path.Combine(output,"original.png"); File.WriteAllBytes(original,landscape);
            var item=Item(source,"one"); item.ImagePath=original;
            string status=ClashStatusStore.SetScreenshot(item,portrait);
            Check(status==ClashStatusStore.SidecarOf(source),"Shared sidecar not used");
            Check(File.Exists(item.ScreenshotPath),"PNG not saved");
            Check(File.ReadAllBytes(item.ScreenshotPath).SequenceEqual(portrait),"Screenshot changed");
            Check(!File.ReadAllText(status).Contains(output.Replace("\\","\\\\")),"Shared screenshot path is not relative");
            var loaded=Reload(source,"one",original);
            Check(loaded.EffectiveImagePath==item.ScreenshotPath,"Screenshot lost on reopen");
            // Комментарий из старого окна не стирает скрин, добавленный коллегой.
            var stale=Item(source,"one"); stale.Comment="Комментарий коллеги"; stale.ChangedAt="2026-11-21 10:01:00";
            ClashStatusStore.Save(stale);
            Check(Reload(source,"one").ScreenshotPath==item.ScreenshotPath,"Stale status overwrote screenshot");
            ClashStatusStore.SetScreenshot(item,landscape);
            Check(item.Comment=="Комментарий коллеги","Screenshot overwrote newer comment");
            Check(Reload(source,"one").Comment=="Комментарий коллеги","Saved comment lost");
            // Старый JSON без полей картинки загружается как раньше.
            string legacy=Path.Combine(output,"legacy.html"); File.WriteAllText(legacy,"");
            File.WriteAllText(ClashStatusStore.SidecarOf(legacy),"{\"old\":{\"Status\":\"Fixed\",\"Comment\":\"old note\",\"By\":\"User\",\"At\":\"2026-11-20 12:00\"}}");
            var old=Reload(legacy,"old",original);
            Check(old.Status==ClashStatus.Fixed && old.EffectiveImagePath==original,"Legacy sidecar compatibility");
            // Копирование отчёта с JSON и папкой картинок сохраняет относительную ссылку.
            string moved=Path.Combine(output,"moved"); Directory.CreateDirectory(moved);
            string movedSource=Path.Combine(moved,Path.GetFileName(source)); File.Copy(source,movedSource);
            File.Copy(status,ClashStatusStore.SidecarOf(movedSource));
            string movedImages=ClashStatusStore.SidecarOf(movedSource)+".images"; Directory.CreateDirectory(movedImages);
            foreach(string file in Directory.GetFiles(status+".images")) File.Copy(file,Path.Combine(movedImages,Path.GetFileName(file)));
            Check(File.Exists(Reload(movedSource,"one").ScreenshotPath),"Shared screenshot not portable");

            // Только для чтения: скрин пишется в профиль, после удаления старый общий не возвращается.
            string fallback=(string)typeof(ClashStatusStore).GetMethod("FallbackOf",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{source});
            Check(!File.Exists(fallback),"Test fallback already exists");
            try
            {
                File.SetAttributes(status,FileAttributes.ReadOnly);
                Check(ClashStatusStore.SetScreenshot(item,portrait)==fallback,"Read-only fallback failed");
                Check(Reload(source,"one").ScreenshotPath==item.ScreenshotPath,"Fallback screenshot not loaded");
                ClashStatusStore.SetScreenshot(item,null);
                Check(Reload(source,"one",original).EffectiveImagePath==original,"Deleted screenshot resurrected from shared state");
            }
            finally
            {
                File.SetAttributes(status,FileAttributes.Normal);
                if(File.Exists(fallback)) File.Delete(fallback);
                if(Directory.Exists(fallback+".images"))
                {
                    foreach(string file in Directory.GetFiles(fallback+".images")) File.Delete(file);
                    Directory.Delete(fallback+".images");
                }
            }
            // Удаление общего скрина и повторная вставка.
            ClashStatusStore.SetScreenshot(item,null);
            Check(Reload(source,"one",original).EffectiveImagePath==original,"Original image not restored");
            ClashStatusStore.SetScreenshot(item,portrait);
            Console.WriteLine("PASS: PNG encoding, persistence, sharing, replace/remove, stale edits, read-only fallback and legacy JSON");

            var fixedItem=Item(source,"fixed","Конфликт1"); fixedItem.Status=ClashStatus.Fixed; fixedItem.ImagePath=original;
            var noImage=Item(source,"none","Конфликт10");
            var missing=Item(source,"missing","Конфликт11"); missing.ScreenshotPath=Path.Combine(output,"missing.png");
            var broken=Item(source,"broken","Конфликт12"); broken.ImagePath=Path.Combine(output,"broken.png"); File.WriteAllText(broken.ImagePath,"bad image");
            string xlsx=Path.Combine(output,"screenshots.xlsx");
            var items=new List<ClashItem>{noImage,fixedItem,missing,item,broken};
            ClashExcel.Write(items,xlsx);
            using(var book=new ExcelPackage(new FileInfo(xlsx)))
            {
                var open=book.Workbook.Worksheets["Коллизии"]; var done=book.Workbook.Worksheets["Исправлено"];
                Check(open.Cells[1,19].Text=="Скриншот","Screenshot header");
                Check(open.Cells[2,5].Text=="Конфликт2" && open.Cells[3,5].Text=="Конфликт10","Natural order");
                Check(open.Drawings.Count==1 && done.Drawings.Count==1,"Pictures on both status sheets");
                Check(open.Drawings[0].From.Row==1 && open.Drawings[0].From.Column==18,"Picture belongs to wrong row/column");
                Check(open.Row(2).Height>100 && open.Row(3).Height<open.Row(2).Height,"Thumbnail row height");
                Check(open.Cells[4,19].Text=="Изображение не найдено","Missing screenshot not reported");
                Check(open.Cells[5,19].Text.StartsWith("Не удалось загрузить изображение"),"Corrupt screenshot not reported");
                Check(book.Workbook.Worksheets["Сводка"].Drawings.Count==0,"Unexpected summary image");
            }
            using(var archive=ZipFile.OpenRead(xlsx))
            {
                var media=archive.Entries.Where(e=>e.FullName.StartsWith("xl/media/")).ToList();
                Check(media.Count==2,"Embedded image count");
                var bytes=new List<byte[]>();
                foreach(var entry in media) using(var stream=entry.Open()) using(var memory=new MemoryStream()) { stream.CopyTo(memory); bytes.Add(memory.ToArray()); }
                Check(bytes.Any(b=>b.SequenceEqual(portrait)) && bytes.Any(b=>b.SequenceEqual(landscape)),"Images are linked or changed instead of embedded");
            }
            string locked=Path.Combine(output,"locked.xlsx"); File.WriteAllText(locked,"keep");
            using(var file=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                try { ClashExcel.Write(items,locked); throw new Exception("Locked file overwritten"); }
                catch(IOException) { }
            }
            Check(File.ReadAllText(locked)=="keep","Previous report damaged");
            Console.WriteLine("PASS: embedded images, row binding, both status sheets, missing/corrupt images and locked output");
            Console.WriteLine("Output: "+xlsx);
            return 0;
        }
        catch(Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
