using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using global::Windows.Graphics.Imaging;
using global::Windows.Media.Ocr;
using global::Windows.Storage;
using global::Windows.Storage.Streams;
using Nashi.Core;

namespace Nashi.Windows;

/// WinRT OcrEngine tabanlı OCR implementasyonu. Hiçbir harici NuGet/kütüphane gerektirmez,
/// tüm işlem Windows üzerinde yerel (offline) olarak gerçekleşir.
public sealed class OcrService : IOcrEngine, IDisposable
{
    private readonly global::Windows.Media.Ocr.OcrEngine? native;
    public bool IsAvailable => native is not null;

    public OcrService()
    {
        try { native = global::Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch { native = null; }
    }

    public async Task<Nashi.Core.OcrResult> RecognizeAsync(string imagePath, string? languageHint = null)
    {
        if (native is null) return new Nashi.Core.OcrResult(false, "OCR bu Windows sürümünde kullanılamıyor. Windows Dil ayarlarından bir OCR dili ekleyin.", Array.Empty<Nashi.Core.OcrLine>());

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await native.RecognizeAsync(bitmap);
            var lines = result.Lines
                .SelectMany(l => l.Words)
                .Select(w => new Nashi.Core.OcrLine(w.Text, (int)w.BoundingRect.X, (int)w.BoundingRect.Y, (int)w.BoundingRect.Width, (int)w.BoundingRect.Height))
                .ToList();
            return new Nashi.Core.OcrResult(true, string.Empty, lines);
        }
        catch (Exception error) { return new Nashi.Core.OcrResult(false, error.Message, Array.Empty<Nashi.Core.OcrLine>()); }
    }

    public void Dispose() { }
}
