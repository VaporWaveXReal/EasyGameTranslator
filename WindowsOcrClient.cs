using System.Drawing;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace EasyGameTranslator;

/// <summary>
/// Lightweight OCR built into Windows 10. It scans a clean in-memory frame and
/// returns complete physical lines with their source coordinates.
/// </summary>
public sealed class WindowsOcrClient
{
    private readonly OcrEngine _engine;

    public WindowsOcrClient(string language)
    {
        var tag = language.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : language;
        _engine = OcrEngine.TryCreateFromLanguage(new Language(tag))
            ?? throw new InvalidOperationException(
                "В Windows не установлен английский пакет распознавания текста. " +
                "Добавьте English в Параметры → Время и язык → Язык.");
    }

    public async Task<IReadOnlyList<RecognizedLine>> ReadAsync(
        SoftwareBitmap bitmap,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await _engine.RecognizeAsync(bitmap);
        token.ThrowIfCancellationRequested();

        var lines = new List<RecognizedLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = line.Words;
            if (words.Count == 0)
                continue;

            var left = words.Min(word => word.BoundingRect.X);
            var top = words.Min(word => word.BoundingRect.Y);
            var right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
            var bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
            var text = line.Text.Trim();
            if (text.Length < 2)
                continue;

            lines.Add(new RecognizedLine(
                new RectangleF((float)left, (float)top, (float)(right - left), (float)(bottom - top)),
                text));
        }

        return lines;
    }
}
