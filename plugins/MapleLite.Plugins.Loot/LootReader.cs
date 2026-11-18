using MapleLite.Core.Capture;
using MapleLite.Core.Game;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Loot;

/// <summary>
/// Reads the game's pickup notices ("You have gained mesos (+5800)") from the lower-right of the game picture
/// with the shared OCR. Moved here from the MapleMemory app's loot pass, with the same area, cleanup and
/// de-duplication. When Chat History has published where the chat box is, that area is blanked out so a pickup
/// the game also prints in chat isn't read twice (the chat copy arrives as a ChatMessageEvent instead).
/// </summary>
internal sealed class LootReader
{
    // Notices fade within a few seconds and repeat pickups arrive fractions of a second apart, so read as often
    // as OCR keeps up with. Frames come at up to this rate; each read takes the newest one.
    public const double Fps = 15;

    readonly IFrameSource _frames;
    readonly IOcr _ocr;
    readonly IEventBus _events;
    readonly Action<DateTimeOffset, LootDrop, string> _onPickup;
    readonly CancellationTokenSource _stop = new();
    IDisposable? _sub;
    Task? _loop;
    Snapshot? _latest;
    long _reads;

    sealed record Snapshot(Frame Frame, DateTimeOffset Time);

    readonly ItemNames _names;

    public LootReader(IFrameSource frames, IOcr ocr, IEventBus events, Action<DateTimeOffset, LootDrop, string> onPickup, ItemNames? names = null)
    {
        _frames = frames; _ocr = ocr; _events = events; _onPickup = onPickup; _names = names ?? new ItemNames([]);
    }

    public long Reads => Interlocked.Read(ref _reads);

    volatile Recorder? _recording;
    public bool IsRecording => _recording != null;
    /// <summary>Raised (on the reader's thread) with the folder when a recording finishes.</summary>
    public event Action<string>? RecordingSaved;

    /// <summary>
    /// Saves what the reader sees for <paramref name="seconds"/>: one full game picture, then every notice-area
    /// picture it reads, with what OCR read in each and which rows the tracker counted as new (log.txt).
    /// </summary>
    public string StartRecording(string parent, double seconds)
    {
        var folder = Path.Combine(parent, "recordings", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(folder);
        var rec = new Recorder(folder, TimeSpan.FromSeconds(seconds), _frames, ChatArea, f => RecordingSaved?.Invoke(f));
        _recording = rec;
        return folder;
    }
    public string? Error { get; private set; }

    /// <summary>Adds a line to the recording's log, when one is running.</summary>
    public void Note(string text) => _recording?.Note(text);

    public void Start()
    {
        _loop = Task.Run(Loop);
        _sub = _frames.Subscribe(gf => Interlocked.Exchange(ref _latest, new Snapshot(Crop(gf.Frame, ChatArea(gf.Frame)), gf.Time)), Fps);
    }

    public async Task StopAsync()
    {
        _sub?.Dispose();
        _stop.Cancel();
        if (_loop != null) await _loop.ConfigureAwait(false);
    }

    Region? ChatArea(Frame f) =>
        _events.Last<ChatAreaEvent>() is { } a && a.FrameWidth == f.W && a.FrameHeight == f.H ? a.Area : null;

    /// <summary>The notice area: right 55% of the picture, from 45% down to 93% down, with the chat box blanked.</summary>
    internal static Frame Crop(Frame f, Region? chat)
    {
        var area = new Region(f.W * 45 / 100, f.H * 45 / 100, f.W - f.W * 45 / 100, f.H * 93 / 100 - f.H * 45 / 100);
        var crop = f.Crop(area);
        if (chat is { } c)
        {
            int x0 = Math.Max(c.X, area.X) - area.X, x1 = Math.Min(c.Right, area.Right) - area.X;
            int y0 = Math.Max(c.Y, area.Y) - area.Y, y1 = Math.Min(c.Bottom, area.Bottom) - area.Y;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) crop.Px[y * crop.W + x] = Pixel.Black;
        }
        return crop;
    }

    /// <summary>How bright a row's text is: the mean of its brightest tenth of pixels (text over the scene), 0–255.</summary>
    internal static double Brightness(Frame f, TextBounds box)
    {
        int x0 = Math.Clamp((int)box.Left, 0, f.W), x1 = Math.Clamp((int)Math.Ceiling(box.Right), 0, f.W);
        int y0 = Math.Clamp((int)box.Top, 0, f.H), y1 = Math.Clamp((int)Math.Ceiling(box.Bottom), 0, f.H);
        if (x1 <= x0 || y1 <= y0) return 0;
        Span<int> hist = stackalloc int[256];
        for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) hist[Pixel.Max(f.Px[y * f.W + x])]++;
        int want = Math.Max(1, (x1 - x0) * (y1 - y0) / 10), n = 0; long sum = 0;
        for (int v = 255; v >= 0 && n < want; v--) { int take = Math.Min(hist[v], want - n); n += take; sum += (long)take * v; }
        return (double)sum / n;
    }

    sealed class Recorder
    {
        readonly string _folder;
        readonly DateTimeOffset _until;
        readonly Action<string> _saved;
        readonly StreamWriter _log;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        int _n;

        public Recorder(string folder, TimeSpan length, IFrameSource frames, Func<Frame, Region?> chat, Action<string> saved)
        {
            _folder = folder; _until = DateTimeOffset.Now + length; _saved = saved;
            _log = new StreamWriter(Path.Combine(folder, "log.txt")) { AutoFlush = true };
            var s = frames.Status;
            _log.WriteLine($"Loot recording {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  capture: {s.State} {s.Fps:0.0}/s  {s.Message}");
            if (frames.Latest is { } gf)
            {
                MapleLite.Sdk.FrameFiles.SavePng(gf.Frame, Path.Combine(folder, "full.png"));
                _log.WriteLine($"full picture {gf.Frame.W}x{gf.Frame.H}; chat box blanked: {chat(gf.Frame)?.ToString() ?? "none"}");
            }
            else _log.WriteLine("no game picture yet");
        }

        public bool Done => DateTimeOffset.Now >= _until;

        public void Note(string text) { lock (_log) _log.WriteLine($"{_clock.Elapsed.TotalSeconds,7:0.000}  {text}"); }

        public void Add(Snapshot snap, (string Pass, OcrResult Result)[] passes, List<NoticeLine> fresh)
        {
            int n = ++_n;
            var name = $"read-{n:0000}.png";
            MapleLite.Sdk.FrameFiles.SavePng(snap.Frame, Path.Combine(_folder, name));
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{_clock.Elapsed.TotalSeconds,7:0.000}  {name}  frame {snap.Time:HH:mm:ss.fff}");
            foreach (var (pass, result) in passes)
            {
                sb.AppendLine($"         {pass}: ocr {result.Elapsed.TotalMilliseconds:0} ms, {result.Lines.Count} rows");
                foreach (var l in result.Lines)
                {
                    bool pickup = GameText.Pickup(l.Text) != null;
                    bool isNew = fresh.Any(f => f.Raw == l.Text && Math.Abs(f.Y - l.Box.CenterY) < 0.5);
                    sb.AppendLine($"           {(isNew ? "NEW " : pickup ? "seen" : "    ")} y={l.Box.CenterY,6:0.0} h={l.Box.Height,4:0.0} b={Brightness(snap.Frame, l.Box),3:0}  {l.Text}");
                }
            }
            foreach (var f in fresh) sb.AppendLine($"           => counted {f.Drop?.Summary}");
            lock (_log) _log.Write(sb.ToString());
        }

        public void Finish()
        {
            Note($"done, {_n} reads");
            _log.Dispose();
            _saved(_folder);
        }
    }

    static readonly OcrPreprocessor DarkStrokes = OcrPreprocessor.DarkStrokes(60, 1);
    static readonly OcrPreprocessor OutlinedFill = OcrPreprocessor.OutlinedFill(70, 200, 2);

    /// <summary>
    /// The pickup notices among what OCR read (scenery, names and damage numbers don't parse as pickups), with item
    /// names corrected against the catalog. Where both passes read the same row, the better read is kept: a name the
    /// catalog knows, else the longer text.
    /// </summary>
    internal static List<NoticeLine> Notices(Frame f, IEnumerable<OcrLine> read, ItemNames names)
    {
        var found = new List<(NoticeLine Line, bool Known)>();
        foreach (var l in read)
        {
            if (GameText.Pickup(l.Text) is not { } drop) continue;
            var fixedDrop = names.Fix(drop);
            bool known = drop.Kind != "Item" || fixedDrop.Name != drop.Name || names.Match(drop.Name) != null;
            // A garbled read (usually a fading notice) would count as a new pickup: leave it out.
            if (drop.Kind == "Item" && !names.Trusted(drop.Name, known)) continue;
            var line = new NoticeLine($"{fixedDrop.Kind}|{fixedDrop.Name}|{fixedDrop.Quantity}", l.Box.CenterY, l.Box.Height, Brightness(f, l.Box))
                { Raw = l.Text, Drop = fixedDrop };
            int same = found.FindIndex(x => Math.Abs(x.Line.Y - line.Y) < Math.Max(x.Line.Height, line.Height) * 0.75); // stacked notices sit a full row apart
            if (same < 0) found.Add((line, known));
            else if ((known && !found[same].Known) || (known == found[same].Known && line.Raw.Length > found[same].Line.Raw.Length))
                found[same] = (line, known);
        }
        return found.Select(x => x.Line).ToList();
    }

    async Task Loop()
    {
        var seen = new NoticeTracker();
        while (!_stop.IsCancellationRequested)
        {
            var snapshot = Interlocked.Exchange(ref _latest, null);
            if (snapshot != null)
                try
                {
                    var f = snapshot.Frame;
                    // Notices are white letters with a thin black outline. The first pass keeps only white pixels
                    // that touch black ones, which picks the letters out of dark and busy scenes alike. Over a light
                    // scene the white fill blends in and only the outline shows, so a second pass reads the dark
                    // strokes in the right half, where the notices sit. Both are enlarged 3x: the letters are tiny.
                    var bright = await _ocr.ReadAsync(f, new OcrRequest(f.Bounds)
                    {
                        Preprocess = OutlinedFill,
                        Grouping = OcrGrouping.Rows,
                        Scaling = OcrScaling.Bicubic,
                        Scale = 3,
                        Pad = 6,
                        Cache = false,
                    }, _stop.Token).ConfigureAwait(false);
                    var dark = await _ocr.ReadAsync(f, new OcrRequest(new Region(f.W / 2, 0, f.W - f.W / 2, f.H))
                    {
                        Preprocess = DarkStrokes,
                        Grouping = OcrGrouping.Rows,
                        Scaling = OcrScaling.Bicubic,
                        Scale = 3,
                        Pad = 6,
                        Cache = false,
                    }, _stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _reads);
                    var lines = Notices(f, bright.Lines.Concat(dark.Lines), _names);
                    var fresh = seen.Update(lines);
                    foreach (var line in fresh)
                        if (line.Drop is { } drop) _onPickup(snapshot.Time, drop, line.Raw);
                    _recording?.Add(snapshot, [("outlined letters", bright), ("dark strokes", dark)], fresh);
                    Error = null;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception e) { Error = "Pickup OCR: " + e.Message; _recording?.Note("ERROR " + e); }
            if (_recording is { Done: true } r) { r.Finish(); _recording = null; }
            try { await Task.Delay(snapshot == null ? 15 : 5, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
}
