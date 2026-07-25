using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using GTranslate.Translators;

namespace EasyGameTranslator;

public sealed class CaptureCoordinator
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<string> _setStatus;
    private readonly Action<string>? _onFatalError;
    private WindowsOcrClient? _ocr;
    private GameWindowInfo? _targetWindow;
    private ITranslationService _translator = new YandexTranslateService();
    private CancellationTokenSource? _cancellation;
    private TranslationOverlay? _overlay;
    private WindowsGraphicsCaptureService? _capture;
    private Task? _worker;
    private Task? _translationWorker;
    private CancellationTokenSource? _translationCancellation;
    private readonly List<TrackedTranslation> _trackedTranslations = [];
    private string _lastRenderedState = string.Empty;
    private string _lastOcrState = string.Empty;
    private long _frameGeneration;
    private int _emptyOcrScans;

    public CaptureCoordinator(Dispatcher dispatcher, Action<string> setStatus, Action<string>? onFatalError = null)
    {
        _dispatcher = dispatcher;
        _setStatus = setStatus;
        _onFatalError = onFatalError;
    }

    public async Task<bool> StartAsync(GameWindowInfo targetWindow, string language, double fontSize, string? deepLApiKey)
    {
        await StopAsync();
        if (!targetWindow.TryGetBounds(out var initialBounds))
            throw new InvalidOperationException("Выбранное окно уже закрыто или свёрнуто.");
        _targetWindow = targetWindow;
        _overlay = new TranslationOverlay(initialBounds);
        _overlay.SetFontSize(fontSize);
        _overlay.Show();
        _capture = await WindowsGraphicsCaptureService.CreateForWindowAsync(targetWindow.Handle);
        _trackedTranslations.Clear();
        _lastRenderedState = string.Empty;
        _lastOcrState = string.Empty;
        _frameGeneration = 0;
        _emptyOcrScans = 0;
        _cancellation = new CancellationTokenSource();
        _ocr = new WindowsOcrClient(language);
        _translator = new YandexTranslateService();
        _worker = RunLoopAsync(initialBounds, language, _cancellation.Token);
        Report($"Захватывается окно: {targetWindow.Title}. F6 — перезапустить, F8 — остановить.");
        return true;
    }

    public async Task StopAsync()
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            _translationCancellation?.Cancel();
            try { if (_worker is not null) await _worker; }
            catch (OperationCanceledException) { }
            try { if (_translationWorker is not null) await _translationWorker; }
            catch (OperationCanceledException) { }
            _cancellation.Dispose();
            _cancellation = null;
            _worker = null;
        }
        _translationCancellation?.Dispose();
        _translationCancellation = null;
        _translationWorker = null;

        if (_overlay is not null)
        {
            _overlay.Dispose();
            _overlay = null;
        }
        _ocr = null;
        _capture?.Dispose();
        _capture = null;
        _targetWindow = null;
    }

    public void ClearOverlay()
    {
        _trackedTranslations.Clear();
        _lastRenderedState = string.Empty;
        _overlay?.Render([]);
    }

    private async Task RunLoopAsync(System.Drawing.Rectangle screenBounds, string language, CancellationToken token)
    {
        try
        {
            var nextScanUtc = DateTime.UtcNow;
            while (!token.IsCancellationRequested)
            {
                if (!(_targetWindow?.TryGetBounds(out screenBounds) ?? false))
                    throw new InvalidOperationException("Выбранное окно закрыто или свёрнуто.");

                var ocr = _ocr ?? throw new InvalidOperationException("OCR не запущен.");
                var capture = _capture ?? throw new InvalidOperationException("Захват экрана не запущен.");
                using var bitmap = await capture.CaptureFrameAsync(token);
                var rawLines = await ocr.ReadAsync(bitmap, token);
                var lines = MergeDialogueLines(rawLines
                        .Where(line => SourceTextFilter.IsMergeCandidate(line.Text, language))
                        .ToArray())
                    .Where(line => SourceTextFilter.IsTranslatable(line.Text, language))
                    .OrderBy(line => line.Bounds.Top)
                    .ThenBy(line => line.Bounds.Left)
                    .Take(16)
                    .ToArray();
                LogOnly($"OCR Windows: {string.Join(" || ", rawLines.Select(line => line.Text))}");
                LogOnly($"К переводу: {string.Join(" || ", lines.Select(line => line.Text))}");

                if (lines.Length == 0)
                {
                    _lastOcrState = string.Empty;
                    _emptyOcrScans++;
                    if (_emptyOcrScans >= 2)
                    {
                        CancelPendingTranslation();
                        if (_trackedTranslations.Count > 0)
                        {
                            _trackedTranslations.Clear();
                            await RenderTrackedAsync(screenBounds, token);
                        }
                    }
                    Report("Английский текст не найден.");
                    nextScanUtc = await WaitForNextScanAsync(nextScanUtc, token);
                    continue;
                }

                _emptyOcrScans = 0;
                var state = BuildOcrState(lines);
                if (string.Equals(state, _lastOcrState, StringComparison.Ordinal))
                {
                    Report("Текст не изменился.");
                    nextScanUtc = await WaitForNextScanAsync(nextScanUtc, token);
                    continue;
                }

                _lastOcrState = state;
                var generation = Interlocked.Increment(ref _frameGeneration);
                StartLatestTranslation(lines, screenBounds, language, generation, token);
                Report($"Новый текст найден: {lines.Length} блок(а). Перевожу…");
                nextScanUtc = await WaitForNextScanAsync(nextScanUtc, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var message = $"Ошибка перевода: {ex.Message}";
            Report(message);
            _ = _dispatcher.BeginInvoke(() => _onFatalError?.Invoke(message));
        }
    }

    private static async Task<DateTime> WaitForNextScanAsync(DateTime previousScanUtc, CancellationToken token)
    {
        var targetUtc = previousScanUtc + TimeSpan.FromMilliseconds(400);
        var now = DateTime.UtcNow;
        if (targetUtc > now)
            await Task.Delay(targetUtc - now, token);
        else
            targetUtc = now;
        return targetUtc;
    }

    private void StartLatestTranslation(
        IReadOnlyList<RecognizedLine> lines,
        System.Drawing.Rectangle screenBounds,
        string language,
        long generation,
        CancellationToken applicationToken)
    {
        CancelPendingTranslation();
        _translationCancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationToken);
        _translationWorker = TranslateSnapshotAsync(
            lines.ToArray(),
            screenBounds,
            language,
            generation,
            _translationCancellation.Token);
    }

    private void CancelPendingTranslation()
    {
        try { _translationCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task TranslateSnapshotAsync(
        IReadOnlyList<RecognizedLine> lines,
        System.Drawing.Rectangle screenBounds,
        string language,
        long generation,
        CancellationToken token)
    {
        try
        {
            // A short debounce prevents translating every intermediate frame
            // of a typewriter animation. Any newer OCR snapshot cancels it.
            // Stay longer than one capture interval. While a game prints a
            // sentence character-by-character every newer OCR snapshot
            // cancels this task; only the first complete stable snapshot is
            // sent to Yandex and rendered.
            await Task.Delay(560, token);
            var translated = await _translator.TranslateAsync(
                lines.Select(line => line.Text).ToArray(),
                language,
                token);
            token.ThrowIfCancellationRequested();
            if (generation != Interlocked.Read(ref _frameGeneration))
                return;

            var snapshot = lines.Zip(translated, (line, russian) =>
                    string.IsNullOrWhiteSpace(russian)
                        ? null
                        : new TrackedTranslation(line.Bounds, line.Text, russian))
                .Where(item => item is not null)
                .Cast<TrackedTranslation>()
                .ToArray();
            if (snapshot.Length == 0)
                return;

            _trackedTranslations.Clear();
            _trackedTranslations.AddRange(snapshot);
            await RenderTrackedAsync(screenBounds, token);
            Report($"Перевод обновлён: {snapshot.Length} блок(а). Windows OCR.");
        }
        catch (OperationCanceledException)
        {
            // A newer frame superseded this result. Never render stale text.
        }
        catch (Exception ex)
        {
            if (generation == Interlocked.Read(ref _frameGeneration))
                Report($"Яндекс не ответил: {ex.Message}");
        }
    }

    private static string BuildOcrState(IReadOnlyList<RecognizedLine> lines)
        => string.Join('\n', lines.Select(line =>
            Regex.Replace(line.Text.Trim().ToLowerInvariant(), @"\s+", " ")));

    private void Report(string message)
    {
        LogOnly(message);
        _ = _dispatcher.BeginInvoke(() => _setStatus(message));
    }

    private static void LogOnly(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyGameTranslator");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "translator.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    private static System.Drawing.RectangleF Offset(System.Drawing.RectangleF bounds, System.Drawing.Rectangle screenBounds)
        => new(bounds.X + screenBounds.X, bounds.Y + screenBounds.Y, bounds.Width, bounds.Height);

    private void ApplyTranslations(IReadOnlyList<LocalTranslation> translations, IReadOnlyList<RectangleF> changedRegions)
    {
        // A region can change because text is typed letter-by-letter.  Preserve
        // existing cards until the new OCR result is ready, then replace only
        // intersecting cards.  This is the key difference from the old
        // "clear and draw every second" behaviour that caused blinking.
        foreach (var region in changedRegions)
        {
            foreach (var tracked in _trackedTranslations.Where(item => IntersectsWithPadding(item.Bounds, region)).ToArray())
            {
                var incomingReplacement = translations.Any(item => IntersectsWithPadding(item.Bounds, tracked.Bounds));
                tracked.MissingScans = !incomingReplacement && IsSubstantialChange(tracked.Bounds, region)
                    ? 4
                    : tracked.MissingScans + 1;
            }
        }

        foreach (var translation in translations)
        {
            var overlapping = _trackedTranslations.FirstOrDefault(item =>
                OccupiesSameTextSlot(item.Bounds, translation.Bounds));
            if (overlapping is not null &&
                overlapping.Source.Length >= 24 &&
                translation.Source.Length < overlapping.Source.Length * 0.45)
            {
                // Ignore OCR snippets cut from the end of an already translated
                // paragraph by an animated cursor. They were the "for / ber /
                // sing" cards repeatedly flashing in the recording.
                overlapping.MissingScans = 0;
                continue;
            }

            var existing = _trackedTranslations.FirstOrDefault(item =>
                IntersectsWithPadding(item.Bounds, translation.Bounds) &&
                string.Equals(item.Source, translation.Source, StringComparison.Ordinal));
            if (existing is not null)
            {
                existing.Bounds = translation.Bounds;
                existing.Translation = translation.Translation;
                existing.MissingScans = 0;
                continue;
            }

            // Adjacent rows of one paragraph must coexist. The previous
            // padded-intersection rule made the first two rows and the final
            // row replace one another every second.
            _trackedTranslations.RemoveAll(item => OccupiesSameTextSlot(item.Bounds, translation.Bounds));
            _trackedTranslations.Add(new TrackedTranslation(translation.Bounds, translation.Source, translation.Translation));
        }

        _trackedTranslations.RemoveAll(item => item.MissingScans >= 4);
    }

    private bool MarkMissingBlocks(IReadOnlyList<RectangleF> changedRegions, IReadOnlyList<RecognizedLine> rawLines)
    {
        var before = _trackedTranslations.Count;
        foreach (var tracked in _trackedTranslations)
        {
            if (!changedRegions.Any(region => IntersectsWithPadding(tracked.Bounds, region)))
                continue;

            if (rawLines.Any(line => IntersectsWithPadding(tracked.Bounds, line.Bounds)))
            {
                tracked.MissingScans = 0;
                continue;
            }
            tracked.MissingScans = changedRegions.Any(region => IsSubstantialChange(tracked.Bounds, region))
                ? 4
                : tracked.MissingScans + 1;
        }
        // A blinking page icon can produce alternating partial and empty
        // crops. Four confirmations retain a stable card through that cycle;
        // a closed dialogue is still removed after roughly two seconds.
        _trackedTranslations.RemoveAll(item => item.MissingScans >= 4);
        return before != _trackedTranslations.Count;
    }

    private async Task RenderTrackedAsync(System.Drawing.Rectangle screenBounds, CancellationToken token)
    {
        // Every tracked item is already a complete paragraph produced by the
        // line merger. Rendering it directly prevents unrelated nearby UI
        // labels from collapsing into one oversized card.
        var lines = _trackedTranslations
            .OrderBy(item => item.Bounds.Top)
            .ThenBy(item => item.Bounds.Left)
            .Select(item => new TranslatedLine(Offset(item.Bounds, screenBounds), item.Translation))
            .ToArray();
        var state = string.Join("|", lines.Select(item => $"{item.Bounds.X:0}:{item.Bounds.Y:0}:{item.Bounds.Width:0}:{item.Bounds.Height:0}:{item.Text}"));
        if (string.Equals(state, _lastRenderedState, StringComparison.Ordinal))
            return;

        await _dispatcher.InvokeAsync(() =>
        {
            _overlay?.UpdateBounds(screenBounds);
            _overlay?.Render(lines);
        }, DispatcherPriority.Render, token);
        _lastRenderedState = state;
    }

    private static bool IntersectsWithPadding(RectangleF first, RectangleF second)
    {
        first.Inflate(18, 14);
        second.Inflate(18, 14);
        return first.IntersectsWith(second);
    }

    private static bool OccupiesSameTextSlot(RectangleF first, RectangleF second)
    {
        var intersection = RectangleF.Intersect(first, second);
        if (intersection.IsEmpty)
            return false;

        var verticalOverlap = intersection.Height / Math.Max(1, Math.Min(first.Height, second.Height));
        var horizontalOverlap = intersection.Width / Math.Max(1, Math.Min(first.Width, second.Width));
        return verticalOverlap >= 0.45f && horizontalOverlap >= 0.20f;
    }

    private static bool IsSubstantialChange(RectangleF tracked, RectangleF changed)
    {
        var intersection = RectangleF.Intersect(tracked, changed);
        if (intersection.IsEmpty)
            return false;

        var widthCoverage = intersection.Width / Math.Max(1, tracked.Width);
        var heightCoverage = intersection.Height / Math.Max(1, tracked.Height);
        return widthCoverage >= 0.45f && heightCoverage >= 0.25f;
    }

    private static IReadOnlyList<(RectangleF Bounds, string Text)> MergeAdjacentTrackedTranslations(
        IReadOnlyList<TrackedTranslation> translations)
    {
        var groups = new List<List<TrackedTranslation>>();
        foreach (var item in translations.OrderBy(value => value.Bounds.Top).ThenBy(value => value.Bounds.Left))
        {
            var group = groups.LastOrDefault();
            if (group is null || !CanCombineRenderedRows(UnionBounds(group.Select(value => value.Bounds)), item.Bounds))
                groups.Add([item]);
            else
                group.Add(item);
        }

        return groups.Select(group =>
        {
            var ordered = group.OrderBy(item => item.Bounds.Top).ThenBy(item => item.Bounds.Left).ToArray();
            return (
                UnionBounds(ordered.Select(item => item.Bounds)),
                string.Join(" ", ordered.Select(item => item.Translation)));
        }).ToArray();
    }

    private static bool CanCombineRenderedRows(RectangleF previous, RectangleF current)
    {
        var verticalGap = current.Top - previous.Bottom;
        if (verticalGap < -Math.Min(previous.Height, current.Height) * 0.30f ||
            verticalGap > Math.Max(90, Math.Min(previous.Height, current.Height) * 2f))
            return false;

        var horizontalOverlap = Math.Max(
            0,
            Math.Min(previous.Right, current.Right) - Math.Max(previous.Left, current.Left));
        return horizontalOverlap >= Math.Min(previous.Width, current.Width) * 0.20f ||
               Math.Abs(previous.Left - current.Left) <= 160;
    }

    private static RectangleF UnionBounds(IEnumerable<RectangleF> bounds)
    {
        var values = bounds.ToArray();
        var left = values.Min(value => value.Left);
        var top = values.Min(value => value.Top);
        var right = values.Max(value => value.Right);
        var bottom = values.Max(value => value.Bottom);
        return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private static IReadOnlyList<RecognizedLine> MergeDialogueLines(IReadOnlyList<RecognizedLine> lines)
    {
        var rows = new List<List<RecognizedLine>>();
        foreach (var line in lines.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left))
        {
            var row = rows.LastOrDefault();
            if (row is null || !CanContinueRow(row[^1], line))
                rows.Add(new List<RecognizedLine> { line });
            else
                row.Add(line);
        }

        var lineRows = rows.Select(row => MergeBoundsAndText(row)).ToArray();
        var groups = new List<List<RecognizedLine>>();
        foreach (var row in lineRows.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left))
        {
            var group = groups.LastOrDefault();
            if (group is null || !CanContinueBlock(group[^1], row))
                groups.Add(new List<RecognizedLine> { row });
            else
                group.Add(row);
        }

        return groups.Select(group => MergeBoundsAndText(group, orderByLeft: false)).ToArray();
    }

    private static RecognizedLine MergeBoundsAndText(IEnumerable<RecognizedLine> parts, bool orderByLeft = true)
    {
        var values = (orderByLeft
                ? parts.OrderBy(line => line.Bounds.Left)
                : parts.OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left))
            .ToArray();
        var left = values.Min(line => line.Bounds.Left);
        var top = values.Min(line => line.Bounds.Top);
        var right = values.Max(line => line.Bounds.Right);
        var bottom = values.Max(line => line.Bounds.Bottom);
        return new RecognizedLine(new RectangleF(left, top, right - left, bottom - top), string.Join(" ", values.Select(line => line.Text)));
    }

    private static bool CanContinueRow(RecognizedLine previous, RecognizedLine current)
    {
        var previousMiddle = previous.Bounds.Top + previous.Bounds.Height / 2;
        var currentMiddle = current.Bounds.Top + current.Bounds.Height / 2;
        var middleDifference = Math.Abs(currentMiddle - previousMiddle);
        if (middleDifference > Math.Max(8, Math.Min(previous.Bounds.Height, current.Bounds.Height) * 0.65f))
            return false;

        var gap = current.Bounds.Left - previous.Bounds.Right;
        // Pixel fonts may produce boxes that overlap slightly even though they
        // belong to the same printed row.
        return gap >= -50 && gap <= Math.Max(70, Math.Min(previous.Bounds.Height, current.Bounds.Height) * 3.5f);
    }

    private static bool CanContinueBlock(RecognizedLine previous, RecognizedLine current)
    {
        // The compact status HUD is deliberately kept as independent labels.
        // Joining its name, level and HP creates a large misplaced card. Dialog
        // lines in this game are substantially wider.
        if (previous.Bounds.Width < Math.Max(95, previous.Bounds.Height * 2.8f) ||
            current.Bounds.Width < Math.Max(95, current.Bounds.Height * 2.8f))
            return false;
        // Speaker names are commonly printed directly above a paragraph.
        // Keep them separate so "Fisherman Betelo" is not translated as part
        // of the spoken sentence and does not enlarge the dialogue card.
        if (LooksLikeTitle(previous.Text))
            return false;

        var verticalGap = current.Bounds.Top - previous.Bounds.Bottom;
        if (verticalGap < -Math.Min(previous.Bounds.Height, current.Bounds.Height) * 0.4f ||
            verticalGap > Math.Max(30, Math.Min(previous.Bounds.Height, current.Bounds.Height) * 2.8f))
            return false;

        var overlap = Math.Max(0, Math.Min(previous.Bounds.Right, current.Bounds.Right) - Math.Max(previous.Bounds.Left, current.Bounds.Left));
        return overlap >= Math.Min(previous.Bounds.Width, current.Bounds.Width) * 0.25f ||
               Math.Abs(previous.Bounds.Left - current.Bounds.Left) <= 110;
    }

    private static bool LooksLikeTitle(string text)
    {
        var words = Regex.Matches(text, "[A-Za-z]+")
            .Select(match => match.Value)
            .ToArray();
        return words.Length is >= 1 and <= 4 &&
               !text.EndsWith('.') && !text.EndsWith('!') && !text.EndsWith('?') &&
               words.All(word => char.IsUpper(word[0]) && word.Skip(1).All(char.IsLower));
    }

}

public sealed record RecognizedLine(System.Drawing.RectangleF Bounds, string Text);
public sealed record TranslatedLine(System.Drawing.RectangleF Bounds, string Text);
internal sealed record LocalTranslation(System.Drawing.RectangleF Bounds, string Source, string Translation);
internal sealed class TrackedTranslation
{
    public TrackedTranslation(System.Drawing.RectangleF bounds, string source, string translation)
        => (Bounds, Source, Translation) = (bounds, source, translation);

    public System.Drawing.RectangleF Bounds { get; set; }
    public string Source { get; }
    public string Translation { get; set; }
    public int MissingScans { get; set; }
}

internal static partial class SourceTextFilter
{
    private static readonly Regex Cyrillic = CyrillicRegex();
    private static readonly Regex CodeOrPath = CodeOrPathRegex();
    private static readonly Regex CamelCaseIdentifier = CamelCaseIdentifierRegex();
    private static readonly Regex Latin = LatinRegex();
    private static readonly Regex LatinWord = LatinWordRegex();
    private static readonly Regex HudLabel = HudLabelRegex();
    private static readonly Regex ProperName = ProperNameRegex();

    public static bool IsMergeCandidate(string source, string language)
    {
        var text = source.Trim();
        if (text.Length < 2 || CodeOrPath.IsMatch(text) || HudLabel.IsMatch(text) || Cyrillic.IsMatch(text))
            return false;
        if (language.Equals("ja", StringComparison.OrdinalIgnoreCase))
            return text.Any(IsJapaneseCharacter);
        if (Latin.Matches(text).Count < 2)
            return false;
        return !IsDecorativeAllCaps(text);
    }

    public static bool IsTranslatable(string source, string language)
    {
        var text = source.Trim();
        // A full in-game dialog is often a 150–300 character paragraph. The
        // old 120-character guard silently discarded precisely the text that
        // matters most, while leaving only short HUD labels and headings.
        if (text.Length is < 2 or > 600 || CodeOrPath.IsMatch(text) || CamelCaseIdentifier.IsMatch(text) || HudLabel.IsMatch(text))
            return false;

        if (language.Equals("ja", StringComparison.OrdinalIgnoreCase))
            return text.Any(IsJapaneseCharacter);

        // Do not feed already-Russian UI text into an English OCR/translator pipeline.
        if (Cyrillic.IsMatch(text))
            return false;

        var words = LatinWord.Matches(text);
        if (words.Count == 1 && words[0].Length < 7)
            return false;
        var firstLatin = text.FirstOrDefault(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        if (words.Count <= 4 && firstLatin is >= 'a' and <= 'z' && !text.EndsWith('.') && !text.EndsWith('!') && !text.EndsWith('?'))
            return false;

        // Decorative all-caps logos such as "YS CHRONICLES+" and
        // "ANCIENT YS VANISHED OMEN" are static artwork, not dialogue. They
        // created the long unrelated card at the top of the recorded video.
        if (IsDecorativeAllCaps(text))
            return false;

        // Proper names and location headings do not need a Russian overlay and
        // would only cover the game's compact UI.
        return Latin.Matches(text).Count >= 2 && !ProperName.IsMatch(text);
    }

    private static bool IsDecorativeAllCaps(string text)
    {
        var latinLetters = text.Where(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z').ToArray();
        return text.Length <= 60 &&
               latinLetters.Length >= 4 &&
               latinLetters.Count(character => character is >= 'A' and <= 'Z') >= latinLetters.Length * 0.72;
    }

    private static bool IsJapaneseCharacter(char value) =>
        (value >= '\u3040' && value <= '\u30ff') ||
        (value >= '\u3400' && value <= '\u9fff') ||
        (value >= '\uff66' && value <= '\uff9f');

    [GeneratedRegex("[А-Яа-яЁё]")]
    private static partial Regex CyrillicRegex();

    [GeneratedRegex(@"(\.(cs|xaml|json|dll|exe|py|md|txt)\b|[\\/{};<>]|::|=>|^\s*[+-]?\d+\s+[-+]\s*\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeOrPathRegex();

    [GeneratedRegex(@"\b[A-Za-z]{2,}[a-z][A-Z][A-Za-z]*\b")]
    private static partial Regex CamelCaseIdentifierRegex();

    [GeneratedRegex("[A-Za-z]")]
    private static partial Regex LatinRegex();

    [GeneratedRegex("[A-Za-z]+")]
    private static partial Regex LatinWordRegex();

    [GeneratedRegex(@"^\s*(HP|MP|EXP|STR|DEF|ATK|LVL?|LEVEL|GOLD)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HudLabelRegex();

    [GeneratedRegex(@"^[A-Z][a-z]+(?:\s+[A-Z][a-z]+){0,2}$")]
    private static partial Regex ProperNameRegex();
}

public interface ITranslationService
{
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLanguage, CancellationToken token);
}

public sealed class YandexTranslateService : ITranslationService
{
    private readonly YandexTranslator _translator = new();
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLanguage, CancellationToken token)
        => await Task.WhenAll(texts.Select(text => TranslateOneAsync(text, sourceLanguage, token)));

    private async Task<string> TranslateOneAsync(string text, string sourceLanguage, CancellationToken token)
    {
        var key = $"{sourceLanguage}:{text}";
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = await _translator.TranslateAsync(text, "ru", sourceLanguage).WaitAsync(token);
        var value = result.Translation.Trim();
        if (!string.IsNullOrEmpty(value))
            _cache[key] = value;
        return value;
    }
}

public sealed class DeepLTranslateService : ITranslationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _apiKey;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    public DeepLTranslateService(string apiKey) => _apiKey = apiKey;

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string sourceLanguage, CancellationToken token)
    {
        if (texts.Count == 0) return [];
        var missing = texts.Distinct(StringComparer.Ordinal).Where(text => !_cache.ContainsKey(text)).ToArray();
        if (missing.Length == 0)
            return texts.Select(text => _cache[text]).ToArray();

        var data = new List<KeyValuePair<string, string>>
        {
            new("source_lang", sourceLanguage.Equals("ja", StringComparison.OrdinalIgnoreCase) ? "JA" : "EN"),
            new("target_lang", "RU"),
            new("preserve_formatting", "1")
        };
        data.AddRange(missing.Select(text => new KeyValuePair<string, string>("text", text)));
        var endpoint = _apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(data)
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {_apiKey}");
        using var response = await Http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
        var translated = json.RootElement.GetProperty("translations").EnumerateArray()
            .Select(item => item.GetProperty("text").GetString() ?? string.Empty).ToArray();
        for (var index = 0; index < missing.Length; index++)
            _cache.TryAdd(missing[index], translated[index]);
        return texts.Select(text => _cache[text]).ToArray();
    }
}
