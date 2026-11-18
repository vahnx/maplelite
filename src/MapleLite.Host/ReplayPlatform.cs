using MapleLite.Sdk;
using System.Diagnostics;
using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using MapleLite.Core.Platform;

namespace MapleLite.Host;

/// <summary>
/// Serves a saved capture as if it were the game window, with the real OS OCR engine.
/// Lets the host and every plugin be tested without touching the live game.
/// <para>Given one PNG it shows that picture forever. Given a recording folder (see <see cref="Recording"/>) it plays
/// the frames back in real time from the first capture, then holds the last one (or starts over with <c>--loop</c>).</para>
/// </summary>
internal sealed class ReplayPlatform : IPlatform, IGameWindowFinder, IWindowCapture, ICursor, IWindowList
{
    readonly IPlatform _real;
    readonly GameWindow _window;
    readonly string? _folder;
    readonly List<Recording.Entry> _entries = new();
    readonly bool _loop;
    readonly object _lock = new();
    Stopwatch? _clock;
    int _index = -1;
    string? _file;
    Frame _frame;

    public ReplayPlatform(string path, IPlatform real, bool loop = false)
    {
        _real = real;
        _loop = loop;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (Recording.IsRecording(path))
        {
            _folder = path;
            _entries = Recording.ReadIndex(path);
            if (_entries.Count == 0) throw new InvalidDataException($"The recording {name} has no frames.");
            _file = _entries[0].File;
            _frame = FrameFiles.LoadPng(Path.Combine(path, _file));
            _index = 0;
            var info = Recording.ReadInfo(path);
            Notes = [$"Replaying the recording {name}: {_entries.Count} frames over {TimeSpan.FromMilliseconds(_entries[^1].Ms):m\\:ss}{(loop ? ", looping" : "")}.", .. real.Notes];
            _window = Window(name, info?.Scale ?? 1);
        }
        else
        {
            _frame = FrameFiles.LoadPng(path);
            Notes = [$"Replaying {name} instead of the game.", .. real.Notes];
            _window = Window(name, 1);
        }
    }

    GameWindow Window(string title, double scale)
    {
        var r = new Region(0, 0, _frame.W, _frame.H);
        return new GameWindow(1, 0, "replay", title, r, r, WindowMode.Windowed, scale);
    }

    public string Name => _real.Name + " (replay)";
    public IGameWindowFinder Finder => this;
    public IWindowCapture Capture => this;
    public ICursor Cursor => this;
    public IWindowList Windows => this;
    public IOcrEngine? Ocr => _real.Ocr;
    public IClipboardImage? Clipboard => _real.Clipboard;
    public PermissionState ScreenCapturePermission() => PermissionState.NotRequired;
    public IReadOnlyList<string> Notes { get; }

    public GameWindow? Find(GameTarget target) => _window;
    public GameWindow? Refresh(GameWindow window) => _window;
    public IReadOnlyList<WindowCandidate> ListCandidates() => [new(_window.Handle, 0, "replay", _window.Title)];
    public IReadOnlyList<WindowCandidate> TopLevelWindows() => ListCandidates();
    // An unchanged picture is the same frame object, so repeat OCR reads come from the cache.
    public Task<Frame> CaptureClientAsync(GameWindow window, CancellationToken ct) => Task.FromResult(Current());
    public Task<Frame> CaptureWindowAsync(nint handle, CancellationToken ct) => Task.FromResult(Current());
    public (int X, int Y)? PositionIn(GameWindow window) => null;

    Frame Current()
    {
        if (_folder == null) return _frame;
        lock (_lock)
        {
            _clock ??= Stopwatch.StartNew();
            double length = _entries[^1].Ms, at = _clock.Elapsed.TotalMilliseconds;
            if (_loop && length > 0) at %= length + 200;
            int i = Math.Max(_index, 0);
            if (at < _entries[i].Ms) i = 0; // looped back to the start
            while (i + 1 < _entries.Count && _entries[i + 1].Ms <= at) i++;
            if (i != _index)
            {
                _index = i;
                var file = _entries[i].File;
                if (file != _file) // frames identical to the one before share a file: keep the same object
                {
                    _frame = FrameFiles.LoadPng(Path.Combine(_folder, file));
                    _file = file;
                }
            }
            return _frame;
        }
    }

    public void Dispose() => _real.Dispose();
}
