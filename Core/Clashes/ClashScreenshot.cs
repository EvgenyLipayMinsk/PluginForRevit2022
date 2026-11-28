using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace PluginForRevit2022.Core.Clashes
{
    internal static class ClashScreenshot
    {
        public static byte[] FromClipboard()
        {
            if (!System.Windows.Clipboard.ContainsImage())
                throw new InvalidOperationException("В буфере обмена нет изображения. Скопируйте скриншот и повторите вставку.");
            return Encode(System.Windows.Clipboard.GetImage());
        }

        public static byte[] Encode(BitmapSource bitmap)
        {
            if (bitmap == null) throw new InvalidOperationException("Не удалось прочитать изображение из буфера обмена.");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }
    }
}
