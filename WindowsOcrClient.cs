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

            // Windows OCR occasionally reports a speaker caption and the
            // sentence below it as one logical OcrLine. Reconstruct physical
            // rows from the word rectangles so a caption can never enlarge
            // the sentence overlay merely because the OCR engine grouped it.
            foreach (var row in SplitIntoPhysicalRows(words
                         .Select(word => new OcrWordBox(
                             new RectangleF(
                                 (float)word.BoundingRect.X,
                                 (float)word.BoundingRect.Y,
                                 (float)word.BoundingRect.Width,
                                 (float)word.BoundingRect.Height),
                             word.Text))
                         .ToArray()))
            {
                var ordered = row.OrderBy(word => word.Bounds.Left).ToArray();
                var text = string.Join(" ", ordered.Select(word => word.Text)).Trim();
                if (text.Length < 2)
                    continue;

                var left = ordered.Min(word => word.Bounds.Left);
                var top = ordered.Min(word => word.Bounds.Top);
                var right = ordered.Max(word => word.Bounds.Right);
                var bottom = ordered.Max(word => word.Bounds.Bottom);
                lines.Add(new RecognizedLine(
                    RectangleF.FromLTRB(left, top, right, bottom),
                    text));
            }
        }

        return lines;
    }

    private static IReadOnlyList<IReadOnlyList<OcrWordBox>> SplitIntoPhysicalRows(
        IReadOnlyList<OcrWordBox> words)
    {
        var rows = new List<List<OcrWordBox>>();
        foreach (var word in words.OrderBy(word => VerticalMiddle(word.Bounds)).ThenBy(word => word.Bounds.Left))
        {
            var bestRow = rows
                .Select(row => new
                {
                    Row = row,
                    Difference = Math.Abs(
                        VerticalMiddle(Union(row.Select(item => item.Bounds))) -
                        VerticalMiddle(word.Bounds))
                })
                .Where(candidate =>
                {
                    var rowBounds = Union(candidate.Row.Select(item => item.Bounds));
                    var tolerance = Math.Max(4, Math.Min(rowBounds.Height, word.Bounds.Height) * 0.55f);
                    return candidate.Difference <= tolerance;
                })
                .OrderBy(candidate => candidate.Difference)
                .FirstOrDefault();

            if (bestRow is null)
                rows.Add([word]);
            else
                bestRow.Row.Add(word);
        }

        return rows;
    }

    private static float VerticalMiddle(RectangleF bounds) =>
        bounds.Top + bounds.Height / 2;

    private static RectangleF Union(IEnumerable<RectangleF> rectangles)
    {
        var values = rectangles.ToArray();
        return RectangleF.FromLTRB(
            values.Min(value => value.Left),
            values.Min(value => value.Top),
            values.Max(value => value.Right),
            values.Max(value => value.Bottom));
    }

    private sealed record OcrWordBox(RectangleF Bounds, string Text);
}
