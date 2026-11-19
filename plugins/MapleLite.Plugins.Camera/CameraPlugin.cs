using Avalonia.Controls;
using MapleLite.Core.Capture;
using MapleLite.Core.Imaging;
using MapleLite.Plugins.Camera.Capture;
using MapleLite.Plugins.Camera.Model;
using MapleLite.Plugins.Camera.Ui;
using MapleLite.Plugins.Camera.Video;
using MapleLite.Sdk;

namespace MapleLite.Plugins.Camera;

public sealed class CameraSettings
{
    /// <summary>Where captures go; null is Pictures/MapleLite.</summary>
    public string? Folder { get; set; }
    /// <summary>The ffmpeg file chosen by hand; null searches the usual places.</summary>
    public string? Ffmpeg { get; set; }

    public string? SnapshotKey { get; set; } = "Ctrl+Alt+S";
    public string? ReplayKey { get; set; } = "Ctrl+Alt+R";
    public string? RecordKey { get; set; } = "Ctrl+Alt+V";

    /// <summary>Off by default: while on, the replay encodes (or holds) the game picture the whole time MapleLite runs.</summary>
    public bool ReplayOn { get; set; }
    public int ReplaySeconds { get; set; } = 15;
    public double ReplayFps { get; set; } = 15;
    public double RecordFps { get; set; } = 30;
    public VideoQuality Quality { get; set; } = VideoQuality.High;

    /// <summary>New captures start with other plugins' overlay items switched on in the editor.</summary>
    public bool IncludeOverlays { get; set; }
    /// <summary>Show "● REC" and "Saved" over the game (needs overlays turned on in Settings).</summary>
    public bool ShowOnGame { get; set; } = true;
    public bool CopySnapshots { get; set; }
}

/// <summary>
/// Camera: snapshots, instant replay (save the last few seconds) and recordings of any length, from the host's
/// shared capture loop, so MapleLite's own overlay is never in them unless asked for. An editor adds looks,
/// adjustments, frames, text, drawing, crop and a date stamp, and exports PNG, JPEG, MP4 (with ffmpeg) or GIF.
/// </summary>
public sealed class CameraPlugin : IPlugin
{
    public PluginManifest Manifest { get; } = new("camera", "Camera", "0.1.0-alpha", "CheesyFrog Technologies",
        "Snapshots, instant replay and recordings of the game, with filters, frames, text and drawing.");

    internal IPluginContext Ctx { get; private set; } = null!;
    internal CameraSettings Settings { get; private set; } = new();
    internal Library Library { get; private set; } = null!;
    /// <summary>The instant replay, or null while it's off.</summary>
    internal IInstantReplay? Replay { get; private set; }
    internal ClipRecorder? Recorder { get; private set; }
    internal IHotkey? SnapshotKey, ReplayKey, RecordKey;
    /// <summary>Something changed that the panel shows (recording state, a new capture, settings).</summary>
    internal event Action? Changed;
    /// <summary>A capture was saved; the panel's gallery refreshes.</summary>
    internal event Action<string>? Saved;

    IDisposable? _replaySub, _recSub;
    Timer? _badge;
    string? _toast; DateTimeOffset _toastUntil;
    string? _ffmpeg; bool _ffmpegLooked;
    int _savingReplay;
    readonly SemaphoreSlim _recLock = new(1, 1);

    public Task StartAsync(IPluginContext context)
    {
        Ctx = context;
        Settings = context.Storage.Load<CameraSettings>("settings");
        Library = new Library(Settings.Folder ?? Library.DefaultFolder(), context.Storage.Folder);
        ApplyReplay();
        SnapshotKey = context.Hotkeys.Register("Snapshot", Settings.SnapshotKey, () => _ = SnapshotAsync());
        ReplayKey = context.Hotkeys.Register("Save instant replay", Settings.ReplayKey, () => _ = SaveReplayAsync());
        RecordKey = context.Hotkeys.Register("Start or stop recording", Settings.RecordKey, () => _ = ToggleRecordingAsync());
        _badge = new Timer(_ => DrawBadge(), null, 500, 250);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _badge?.Dispose(); _badge = null;
        _replaySub?.Dispose(); _replaySub = null;
        if (Recorder != null) await StopRecordingAsync();
        if (Replay is { } r) { Replay = null; await r.DisposeAsync(); }
        Ctx?.Overlay.Clear();
    }

    public Control? CreatePanel() => new CameraPanel(this);

    internal void SaveSettings() { Ctx.Storage.Save("settings", Settings); Changed?.Invoke(); }
    /// <summary>Tells the gallery a new file is there (exports from the editor).</summary>
    internal void AnnounceSaved(string path) => Saved?.Invoke(path);

    internal string? FfmpegPath
    {
        get { if (!_ffmpegLooked) { _ffmpeg = Ffmpeg.Find(Settings.Ffmpeg); _ffmpegLooked = true; } return _ffmpeg; }
    }
    internal void LookForFfmpegAgain()
    {
        var before = _ffmpeg;
        _ffmpegLooked = false;
        if (FfmpegPath != before) ApplyReplay(); // found (or lost) ffmpeg: switch replay kind
        Changed?.Invoke();
    }

    /// <summary>
    /// Starts the instant replay again with the current settings (anything held is dropped): on disk through ffmpeg
    /// when there is one, else in memory.
    /// </summary>
    internal void ApplyReplay()
    {
        _replaySub?.Dispose(); _replaySub = null;
        if (Replay is { } old) { Replay = null; _ = old.DisposeAsync().AsTask(); }
        if (!Settings.ReplayOn) { Changed?.Invoke(); return; }
        double seconds = Math.Clamp(Settings.ReplaySeconds, 5, 120);
        var replay = FfmpegPath is { } ff
            ? new SegmentReplay(ff, Path.Combine(Ctx.Storage.Folder, "replay-cache"), seconds, Settings.ReplayFps, Settings.Quality, m => Ctx.Log(m))
            : (IInstantReplay)new MemoryReplay(seconds, Settings.ReplayFps);
        Replay = replay;
        _replaySub = Ctx.Frames.Subscribe(gf => replay.Add(gf.Frame, gf.Time, Ctx.Overlay.Everything()), Settings.ReplayFps);
        Ctx.Log($"instant replay: {seconds:0} s at {Settings.ReplayFps:0}/s, {(replay.OnDisk ? $"MP4 pieces on disk ({Settings.Quality})" : "JPEGs in memory")}");
        Changed?.Invoke();
    }

    bool HaveGame(out GameFrame gf)
    {
        gf = Ctx.Frames.Latest!;
        if (Ctx.Frames.Status.State == CaptureState.Capturing && gf != null) return true;
        Toast("No game picture yet");
        Ctx.Notify("No game picture to capture yet. " + Ctx.Frames.Status.Message);
        return false;
    }

    MediaInfo NewInfo(GameFrame gf, double fps = 0, double seconds = 0) => new()
    {
        Taken = gf.Time, W = gf.Frame.W, H = gf.Frame.H, Fps = fps, Seconds = seconds,
        GameScale = gf.Window.Scale, Map = Ctx.Map.Map,
        Edit = Settings.IncludeOverlays ? new Edit { Overlays = true } : null,
    };

    /// <summary>Saves the latest game picture as a lossless PNG.</summary>
    internal async Task<string?> SnapshotAsync()
    {
        try
        {
            if (!HaveGame(out var gf)) return null;
            var overlays = Ctx.Overlay.Everything();
            var path = Library.NewPath(gf.Time, ".png");
            await Task.Run(() =>
            {
                FrameFiles.SavePng(gf.Frame, path);
                var info = NewInfo(gf);
                info.Overlays = overlays.Count > 0 ? overlays.ToList() : null;
                Library.SaveInfo(path, info);
                WriteThumb(path, gf.Frame);
            });
            if (Settings.CopySnapshots) await Ctx.CopyImageAsync(gf.Frame);
            Ctx.Log("snapshot " + path);
            Toast("Snapshot saved");
            Ctx.Notify($"Snapshot saved: {Path.GetFileName(path)}");
            Saved?.Invoke(path);
            return path;
        }
        catch (Exception e)
        {
            Ctx.Log("snapshot failed: " + e);
            Ctx.Notify("Couldn't save the snapshot: " + e.Message);
            return null;
        }
    }

    internal bool SavingReplay => Volatile.Read(ref _savingReplay) != 0;

    /// <summary>Saves what the instant replay holds (the last N seconds).</summary>
    internal async Task<string?> SaveReplayAsync()
    {
        if (!Settings.ReplayOn || Replay is not { } replay) { Ctx.Notify("Instant replay is off. Turn it on in the Camera page."); return null; }
        if (Interlocked.Exchange(ref _savingReplay, 1) == 1) return null;
        Changed?.Invoke();
        try
        {
            Toast("Saving replay…", 10);
            double scale = Ctx.Frames.Latest?.Window.Scale ?? 1;
            var saved = await Task.Run(() => replay.SaveAsync(Library));
            if (saved == null) { Toast("Replay is empty"); Ctx.Notify("The instant replay is still empty: it fills while the game is captured."); return null; }
            var path = saved.Path;
            var seconds = saved.Seconds;
            var info = new MediaInfo
            {
                Taken = saved.Start, W = saved.W, H = saved.H, Fps = Settings.ReplayFps, Seconds = seconds, GameScale = scale, Map = Ctx.Map.Map,
                Edit = Settings.IncludeOverlays ? new Edit { Overlays = true } : null,
            };
            Library.SaveInfo(path, info);
            Library.SaveOverlays(path, saved.Overlays);
            if (saved.Last is { } last) await Task.Run(() => WriteThumb(path, last));
            Ctx.Log($"replay saved {path} ({seconds:0.0} s, {(replay.OnDisk ? "joined MP4 pieces" : "JPEG frames")}, {replay.Skipped} pictures skipped so far)");
            Toast("Replay saved");
            Ctx.Notify($"Saved the last {seconds:0} s: {Path.GetFileName(path)}");
            Saved?.Invoke(path);
            return path;
        }
        catch (Exception e)
        {
            Ctx.Log("replay save failed: " + e);
            Toast("Replay not saved");
            Ctx.Notify("Couldn't save the replay: " + e.Message);
            return null;
        }
        finally { Volatile.Write(ref _savingReplay, 0); Changed?.Invoke(); }
    }

    internal async Task ToggleRecordingAsync()
    {
        if (Recorder != null) await StopRecordingAsync();
        else await StartRecordingAsync();
    }

    internal async Task StartRecordingAsync()
    {
        await _recLock.WaitAsync();
        try
        {
            if (Recorder != null || !HaveGame(out var gf)) return;
            var rec = new ClipRecorder(Library, DateTimeOffset.Now, gf.Frame.W, gf.Frame.H, Settings.RecordFps, FfmpegPath, Settings.Quality, gf.Window.Title, gf.Window.Scale);
            Recorder = rec;
            _recSub = Ctx.Frames.Subscribe(f => rec.Add(f.Frame, f.Time, Ctx.Overlay.Everything()), Settings.RecordFps);
            Ctx.Log($"recording to {rec.Path} at {Settings.RecordFps:0}/s ({(rec.IsMp4 ? "MP4 " + Settings.Quality : "JPEG frames")})");
            Toast("Recording");
        }
        catch (Exception e)
        {
            Ctx.Log("recording failed to start: " + e);
            Ctx.Notify("Couldn't start recording: " + e.Message);
            Recorder = null;
        }
        finally { _recLock.Release(); Changed?.Invoke(); }
    }

    internal async Task StopRecordingAsync()
    {
        await _recLock.WaitAsync();
        try
        {
            if (Recorder is not { } rec) return;
            _recSub?.Dispose(); _recSub = null;
            Recorder = null;
            Changed?.Invoke();
            var seconds = rec.Elapsed.TotalSeconds;
            await rec.DisposeAsync();
            Library.SaveInfo(rec.Path, new MediaInfo
            {
                Taken = rec.Started, W = rec.W, H = rec.H, Fps = Settings.RecordFps, Seconds = seconds,
                GameScale = Ctx.Frames.Latest?.Window.Scale ?? 1, Map = Ctx.Map.Map,
                Edit = Settings.IncludeOverlays ? new Edit { Overlays = true } : null,
            });
            Library.SaveOverlays(rec.Path, rec.OverlayTimeline);
            if (Ctx.Frames.Latest is { } last) WriteThumb(rec.Path, last.Frame);
            var note = rec.Error != null ? $" (ffmpeg reported: {rec.Error})" : rec.Dropped > 0 ? $" ({rec.Dropped} pictures skipped to keep up)" : "";
            Ctx.Log($"recording saved {rec.Path}: {rec.Frames} frames, {seconds:0.0} s{note}");
            Toast(rec.Error == null ? "Recording saved" : "Recording problem");
            Ctx.Notify($"Recording saved: {Path.GetFileName(rec.Path)} ({TimeSpan.FromSeconds(seconds):m\\:ss}){note}");
            Saved?.Invoke(rec.Path);
        }
        catch (Exception e)
        {
            Ctx.Log("recording failed to finish: " + e);
            Ctx.Notify("The recording didn't finish cleanly: " + e.Message);
        }
        finally { _recLock.Release(); Changed?.Invoke(); }
    }

    internal void WriteThumb(string media, Frame frame)
    {
        try
        {
            int w = Math.Min(360, frame.W), h = Math.Max(1, frame.H * w / frame.W);
            File.WriteAllBytes(Library.ThumbPath(media), FrameFiles.EncodeJpeg(FrameFiles.Resize(frame, w, h), 85, fullColor: false));
        }
        catch (Exception e) { Ctx.Log("thumbnail failed: " + e.Message); }
    }

    // ---- over the game: a recording badge and short "saved" notes ----

    void Toast(string text, double seconds = 2) { _toast = text; _toastUntil = DateTimeOffset.Now.AddSeconds(seconds); }

    void DrawBadge()
    {
        try
        {
            if (!Settings.ShowOnGame || Ctx.Frames.Latest is not { } gf) { Ctx.Overlay.Clear(); return; }
            var items = new List<OverlayItem>();
            if (Recorder is { } rec)
            {
                var t = rec.Elapsed;
                bool blink = t.Milliseconds < 600;
                items.Add(new OverlayItem(gf.Frame.W - 74, 22, $"{(blink ? "●" : "○")} REC {(int)t.TotalMinutes}:{t.Seconds:00}", OverlayTone.Danger));
            }
            if (_toast != null && DateTimeOffset.Now < _toastUntil) items.Add(new OverlayItem(gf.Frame.W / 2.0, 34, "Camera: " + _toast, OverlayTone.Good, null, 15));
            if (items.Count == 0) Ctx.Overlay.Clear(); else Ctx.Overlay.Draw(items);
        }
        catch (Exception e) { Ctx.Log("badge failed: " + e.Message); }
    }
}
