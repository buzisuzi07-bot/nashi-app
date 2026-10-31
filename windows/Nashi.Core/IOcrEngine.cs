using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Nashi.Core;

/// OCR (Optical Character Recognition) — platform-agnostic arayüz.
/// Windows implementasyonu WinRT OcrEngine kullanır (harici NuGet yok, işlem yerel/offline).
public interface IOcrEngine
{
    /// Bu platformda OCR kullanılabilir mi?
    bool IsAvailable { get; }

    /// Bir görüntüdeki metni okur. Dil ipucu (ISO 639-1, örn. "tr", "en") isteğe bağlıdır.
    Task<OcrResult> RecognizeAsync(string imagePath, string? languageHint = null);
}

/// OCR tek bir satırın metni ve konumu.
public sealed record OcrLine(string Text, int X, int Y, int Width, int Height);

/// OCR sonucu.
public sealed record OcrResult(bool Success, string Error, IReadOnlyList<OcrLine> Lines)
{
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
    public int LineCount => Lines.Count;
}
