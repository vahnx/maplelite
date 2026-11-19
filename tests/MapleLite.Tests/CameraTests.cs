using Avalonia.Input;
using MapleLite.Core.Imaging;
using MapleLite.Plugins.Camera.Capture;
using MapleLite.Plugins.Camera.Imaging;
using MapleLite.Plugins.Camera.Model;
using MapleLite.Plugins.Camera.Video;
using MapleLite.Sdk;
using SkiaSharp;
using Xunit;

namespace MapleLite.Tests;

public class CameraTests
{
    static Frame Solid(int w, int h, int color) { var f = new Frame(w, h); Array.Fill(f.Px, color); return f; }

    static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "maplelite-cam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Every frame of a GIF as Skia shows it (with earlier frames underneath, as players do).</summary>
    static List<SKBitmap> DecodeGif(byte[] gif)
    {
        using var data = SKData.CreateCopy(gif);
        using var codec = SKCodec.Create(data);
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var frames = new List<SKBitmap>();
        for (int i = 0; i < codec.FrameCount; i++)
        {
            var bmp = new SKBitmap(info);
            if (i > 0) frames[i - 1].CopyTo(bmp);
            var result = codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(i, i - 1));
            Assert.Equal(SKCodecResult.Success, result);
            frames.Add(bmp);
        }
        return frames;
    }

    [Fact]
    public void Gif_stores_only_changes_and_merges_still_frames()
    {
        var red = Pixel.Rgb(200, 30, 30);
        var a = Solid(40, 30, red);
        var b = Solid(40, 30, red);
        for (int y = 10; y < 20; y++) for (int x = 5; x < 15; x++) b.Px[y * 40 + x] = Pixel.Rgb(40, 200, 60);
        using var ms = new MemoryStream();
        using (var gif = new GifEncoder(ms, 40, 30, dither: false))
        {
            gif.Add(a, 100);
            gif.Add(b, 100);
            gif.Add(b.Clone(), 100); // unchanged: folded into the previous frame's time
            Assert.Equal(2, gif.Frames);
        }
        var frames = DecodeGif(ms.ToArray());
        Assert.Equal(2, frames.Count);
        var c0 = frames[0].GetPixel(30, 25); var c1 = frames[1].GetPixel(30, 25); var g = frames[1].GetPixel(8, 12);
        Assert.InRange(c0.Red, 190, 210); Assert.InRange(c0.Green, 20, 40);
        Assert.InRange(c1.Red, 190, 210); // unchanged area still shows through
        Assert.InRange(g.Green, 190, 210); Assert.InRange(g.Red, 30, 50);
    }

    [Fact]
    public void Gif_round_trips_a_busy_picture_exactly_when_it_has_few_colors()
    {
        // 255 colors scattered over 60,000 pixels: the LZW table fills and resets many times.
        var colors = Enumerable.Range(0, 255).Select(i => Pixel.Rgb((i * 37) % 32 * 8, (i * 11) % 32 * 8, i % 8 * 32)).Distinct().ToArray();
        var f = new Frame(300, 200);
        var rng = new Random(7);
        for (int i = 0; i < f.Px.Length; i++) f.Px[i] = colors[rng.Next(colors.Length)];
        using var ms = new MemoryStream();
        using (var gif = new GifEncoder(ms, 300, 200, dither: false)) gif.Add(f, 50);
        var bmp = DecodeGif(ms.ToArray()).Single();
        for (int i = 0; i < f.Px.Length; i += 97)
        {
            var c = bmp.GetPixel(i % 300, i / 300);
            Assert.Equal((Pixel.R(f.Px[i]), Pixel.G(f.Px[i]), Pixel.B(f.Px[i])), (c.Red, c.Green, c.Blue));
        }
    }

    [Fact]
    public void Color_matrices_combine_in_order()
    {
        var twice = ColorMatrix.Invert.Then(ColorMatrix.Invert);
        Assert.True(twice.IsIdentity);
        var bw = ColorMatrix.ForLook(Look.BlackWhite);
        // a pure red becomes a grey: every row gives the same value
        float r = bw.M[0], g = bw.M[5], b = bw.M[10];
        Assert.Equal(r, g, 4); Assert.Equal(g, b, 4);
        Assert.True(ColorMatrix.ForLook(Look.Vivid).Mix(0).IsIdentity);
    }

    [Fact]
    public void Looks_and_adjustments_change_the_picture_as_expected()
    {
        var src = new RenderSource(Solid(64, 36, Pixel.Rgb(200, 60, 40)), DateTimeOffset.Now);
        int Mid(Edit e) => EditRenderer.Render(src, e).Picture.At(32, 18);
        var grey = Mid(new Edit { Look = Look.BlackWhite });
        Assert.InRange(Pixel.Chroma(grey), 0, 2);
        // +0.5 brightness adds 0.15 of full scale (about 38 levels): proves Skia reads matrix offsets as 0–1
        Assert.InRange(Pixel.G(Mid(new Edit { Brightness = 0.5 })), 90, 106);
        Assert.Equal(Pixel.Rgb(200, 60, 40), Mid(new Edit()));
    }

    [Fact]
    public void Frames_crop_and_preview_scale_line_up_with_the_export()
    {
        var pic = Solid(400, 200, Pixel.Rgb(30, 90, 160));
        var edit = new Edit
        {
            Frame = FrameStyle.Polaroid, Caption = "Henesys", Crop = new CropBox(100, 20, 200, 160),
            Texts = { new TextMark { Text = "Hi", X = 200, Y = 100, Size = 30 } },
        };
        var full = EditRenderer.Render(new RenderSource(pic, DateTimeOffset.Now), edit);
        Assert.Equal(EditRenderer.OutputSize(400, 200, edit), (full.Picture.W, full.Picture.H));
        Assert.True(full.Picture.H > 160 && full.Picture.W > 200); // the frame adds room, bottom most of all
        // the text is centred where it was put, in original pixels, and maps back through the layout
        var tb = full.TextBounds[0];
        Assert.InRange(tb.MidX, 195, 205);
        var (sx, sy) = full.Layout.ToSource(full.Layout.ToOutput(200, 100).X, full.Layout.ToOutput(200, 100).Y);
        Assert.Equal(200, sx, 3); Assert.Equal(100, sy, 3);

        // A half-size preview from a half-size picture: same layout, half the pixels.
        var small = FrameFiles.Resize(pic, 200, 100);
        var preview = EditRenderer.Render(new RenderSource(small, DateTimeOffset.Now, PictureScale: 0.5), edit, 0.5);
        Assert.InRange(preview.Picture.W, full.Picture.W / 2 - 2, full.Picture.W / 2 + 2);
        Assert.Equal(tb.MidX, preview.TextBounds[0].MidX, 1);
        Assert.Equal(edit.Crop, preview.Layout.Crop);
    }

    [Fact]
    public void Every_look_and_frame_renders()
    {
        var src = new RenderSource(Solid(120, 80, Pixel.Rgb(120, 140, 160)), DateTimeOffset.Now,
            [new OverlayItem(60, 40, "3.2", OverlayTone.Warning, 0.5)]);
        foreach (var look in Enum.GetValues<Look>())
            foreach (var frame in Enum.GetValues<FrameStyle>())
            {
                var e = new Edit { Look = look, Frame = frame, Stamp = true, Overlays = true, Vignette = 0.5, Caption = "x",
                    Texts = { new TextMark { Text = "a\nb", X = 60, Y = 40, Style = (TextStyle)((int)look % 4) } },
                    Strokes = { new Stroke { Points = { 1, 1, 50, 50, 90, 10 } } } };
                var r = EditRenderer.Render(src, e);
                Assert.True(r.Picture.W >= 120 && r.Picture.H >= 80);
            }
    }

    [Fact]
    public void Edits_survive_saving()
    {
        var e = new Edit { Look = Look.Sepia, Crop = new CropBox(1, 2, 3, 4), Texts = { new TextMark { Text = "yo", Style = TextStyle.Bubble } } };
        var back = Edit.FromJson(e.ToJson())!;
        Assert.Equal(Look.Sepia, back.Look);
        Assert.Equal(e.Crop, back.Crop);
        Assert.Equal(TextStyle.Bubble, back.Texts[0].Style);
        Assert.True(new Edit().IsPlain);
        Assert.False(back.IsPlain);
    }

    [Theory]
    [InlineData("ctrl+alt+s", "Ctrl+Alt+S")]
    [InlineData("Shift+F9", "Shift+F9")]
    [InlineData("Ctrl+5", "Ctrl+5")]
    [InlineData("Ctrl+PageUp", "Ctrl+PageUp")]
    public void Hotkey_text_normalizes(string input, string expected) => Assert.Equal(expected, HotkeyText.Normalize(input));

    [Fact]
    public void Hotkeys_that_would_eat_typing_are_refused()
    {
        Assert.NotNull(HotkeyText.Problem("S"));
        Assert.NotNull(HotkeyText.Problem("Shift+S"));
        Assert.Null(HotkeyText.Problem("Ctrl+Alt+S"));
        Assert.Null(HotkeyText.Problem("F9"));
        Assert.Null(HotkeyText.Format(KeyModifiers.Control, Key.LeftCtrl));
        Assert.Equal("Ctrl+Alt+R", HotkeyText.Format(KeyModifiers.Control | KeyModifiers.Alt, Key.R));
        Assert.Null(HotkeyText.Normalize("Ctrl+A+B"));
    }

    [Fact]
    public async Task Replay_keeps_only_the_last_seconds()
    {
        var buffer = new ReplayBuffer(1);
        var t0 = DateTimeOffset.Now;
        for (int i = 0; i < 30; i++)
        {
            buffer.Add(Solid(32, 18, Pixel.Rgb(i * 8, 0, 0)), t0.AddMilliseconds(i * 100), null);
            await Task.Delay(15); // let the encoders keep up
        }
        for (int i = 0; i < 50 && buffer.Count == 0; i++) await Task.Delay(20);
        await Task.Delay(200);
        var held = buffer.Snapshot();
        Assert.NotEmpty(held);
        Assert.True((held[^1].Time - held[0].Time).TotalSeconds <= 1.01);
        Assert.True(held.Zip(held.Skip(1)).All(p => p.First.Time <= p.Second.Time));
    }

    [Fact]
    public async Task A_replay_without_ffmpeg_becomes_frames_then_a_gif()
    {
        var root = TempDir();
        try
        {
            var library = new Library(Path.Combine(root, "pics"), Path.Combine(root, "data"));
            var t0 = DateTimeOffset.Now;
            var entries = Enumerable.Range(0, 10).Select(i =>
            {
                var f = Solid(64, 36, Pixel.Rgb(20, 20, 20));
                for (int y = 10; y < 20; y++) for (int x = i * 5; x < i * 5 + 8; x++) f.Px[y * 64 + x] = Pixel.Rgb(250, 200, 40);
                return new ReplayBuffer.Entry(t0.AddMilliseconds(i * 100), FrameFiles.EncodeJpeg(f, 92), 64, 36, null);
            }).ToList();
            var folder = await ClipRecorder.SaveReplayAsync(library, entries, 10, null, VideoQuality.High, "MapleStory", 1);
            Assert.EndsWith(Library.FramesSuffix, folder);
            Assert.True(Recording.IsRecording(folder));
            Assert.Contains(library.List(), m => m.Kind == MediaKind.Frames && m.Path == folder);

            var clip = await ClipSource.OpenAsync(folder, null);
            Assert.Equal((64, 36), (clip.W, clip.H));
            Assert.InRange(clip.Seconds, 0.9, 1.1);

            var gifPath = library.EditedPath(folder, ".gif");
            Assert.EndsWith("(edited).gif", gifPath);
            var edit = new Edit { Look = Look.Sepia, Frame = FrameStyle.Film };
            await Exporter.ExportClipAsync(clip, null, [], edit, new ClipExport(ClipFormat.Gif, 0, clip.Seconds, 10, 0, VideoQuality.High), gifPath, null);
            var frames = DecodeGif(File.ReadAllBytes(gifPath));
            Assert.InRange(frames.Count, 8, 10);
            Assert.Equal(EditRenderer.OutputSize(64, 36, edit), (frames[0].Width, frames[0].Height));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Long_recordings_without_ffmpeg_write_jpeg_frames()
    {
        var root = TempDir();
        try
        {
            var library = new Library(root, Path.Combine(root, "data"));
            var start = DateTimeOffset.Now;
            var rec = new ClipRecorder(library, start, 32, 18, 10, null, VideoQuality.High, null, 1);
            for (int i = 0; i < 5; i++) rec.Add(Solid(32, 18, Pixel.Rgb(i * 40, 10, 10)), start.AddMilliseconds(i * 100), [new OverlayItem(1, 1, "x")]);
            await rec.DisposeAsync();
            Assert.Equal(5, rec.Frames);
            Assert.Single(rec.OverlayTimeline); // the same items five times: kept once
            Assert.All(Directory.GetFiles(Path.Combine(rec.Path, Recording.FramesDir)), f => Assert.EndsWith(".jpg", f));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- with ffmpeg (skipped quietly where it isn't installed) ----

    /// <summary>A picture whose color says which frame it is (frame i: red = 5i), with a moving block.</summary>
    static Frame Numbered(int i, int w = 320, int h = 180)
    {
        var f = Solid(w, h, Pixel.Rgb(i * 5 % 256, 80, 160));
        for (int y = 20; y < 60; y++) for (int x = i * 3 % (w - 40); x < i * 3 % (w - 40) + 40; x++) f.Px[y * w + x] = Pixel.Rgb(250, 250, 40);
        return f;
    }

    static async Task<List<Frame>> ReadAll(string ffmpeg, string path)
    {
        var clip = await ClipSource.OpenAsync(path, ffmpeg);
        var list = new List<Frame>();
        await foreach (var (_, f) in clip.ReadAsync(0, clip.Seconds + 1, clip.Fps, clip.W, clip.H)) list.Add(f);
        return list;
    }

    [Fact]
    public async Task Lossless_recording_keeps_every_frame_exactly()
    {
        if (Ffmpeg.Find(null) is not { } ff) return;
        var root = TempDir();
        try
        {
            var library = new Library(root, Path.Combine(root, "data"));
            var start = DateTimeOffset.Now;
            var rec = new ClipRecorder(library, start, 320, 180, 30, ff, VideoQuality.Lossless, null, 1);
            // a steady 30/s stream: frame i at exactly i × 33.33 ms (rounding down once dropped frame 1 and shifted the rest)
            for (int i = 0; i < 45; i++) { rec.Add(Numbered(i), start.AddMilliseconds(i * 1000.0 / 30), null); await Task.Delay(5); }
            await rec.DisposeAsync();
            Assert.Null(rec.Error);
            Assert.Equal(0, rec.Dropped);
            var back = await ReadAll(ff, rec.Path);
            Assert.Equal(45, back.Count);
            for (int i = 0; i < 45; i += 11) Assert.Equal(Numbered(i).Px, back[i].Px);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Segment_replay_saves_up_to_the_moment_and_again()
    {
        if (Ffmpeg.Find(null) is not { } ff) return;
        var root = TempDir();
        try
        {
            var library = new Library(Path.Combine(root, "pics"), Path.Combine(root, "data"));
            await using var replay = new SegmentReplay(ff, Path.Combine(root, "cache"), seconds: 3, fps: 15, VideoQuality.Lossless, _ => { });
            var t0 = DateTimeOffset.Now;
            int n = 0;
            async Task Feed(int count)
            {
                for (int k = 0; k < count; k++, n++) { replay.Add(Numbered(n), t0.AddMilliseconds(n * 1000.0 / 15), null); await Task.Delay(n < 3 ? 400 : 8); }
            }
            await Feed(120); // 8 s of game at 15/s; the replay keeps about 3

            var first = await replay.SaveAsync(library);
            Assert.NotNull(first);
            Assert.EndsWith(".mp4", first.Path);
            Assert.InRange(first.Seconds, 3, 5.5); // whole 2 s pieces covering the last 3 s
            var frames = await ReadAll(ff, first.Path);
            Assert.InRange(frames.Count, 40, 85);
            // the clip ends with the last picture added before saving (lossless, so its color is exact)
            Assert.Equal(Numbered(n - 1).Px, frames[^1].Px);

            await Feed(60); // keeps going after a save, in a new run
            var second = await replay.SaveAsync(library);
            Assert.NotNull(second);
            Assert.NotEqual(first.Path, second.Path);
            var again = await ReadAll(ff, second.Path);
            Assert.Equal(Numbered(n - 1).Px, again[^1].Px);
            Assert.True(replay.Held <= TimeSpan.FromSeconds(3.01));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Two_replays_sharing_a_cache_folder_leave_each_other_alone()
    {
        var root = TempDir();
        try
        {
            var cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(Path.Combine(cache, "p999999-1")); // left by a process that's gone
            File.WriteAllText(Path.Combine(cache, "p999999-1", "piece00000.ts"), "");
            await using var a = new SegmentReplay("ffmpeg", cache, 5, 15, VideoQuality.High, _ => { });
            var mine = Directory.GetDirectories(cache).Single();
            File.WriteAllText(Path.Combine(mine, "piece00000.ts"), "");
            await using var b = new SegmentReplay("ffmpeg", cache, 5, 15, VideoQuality.High, _ => { }); // e.g. a second MapleLite
            Assert.Equal(2, Directory.GetDirectories(cache).Length); // the stale folder went, a's stayed
            Assert.True(File.Exists(Path.Combine(mine, "piece00000.ts")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void New_names_never_overwrite()
    {
        var root = TempDir();
        try
        {
            var library = new Library(root, Path.Combine(root, "data"));
            var t = new DateTimeOffset(2026, 11, 19, 21, 4, 33, TimeSpan.Zero);
            var a = library.NewPath(t, ".png");
            File.WriteAllText(a, "");
            var b = library.NewPath(t, ".png");
            Assert.NotEqual(a, b);
            Assert.EndsWith("MapleLite 2026-11-19 21-04-33 2.png", b);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Overlay_items_paint_into_pictures()
    {
        using var bmp = new SKBitmap(100, 60);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        OverlayPainter.Draw(canvas, [new OverlayItem(50, 20, "", OverlayTone.Good, 1), new OverlayItem(50, 45, "now", OverlayTone.Good)]);
        canvas.Flush();
        // the ring's arc is green at its top
        var top = bmp.GetPixel(50, 20 - 11);
        Assert.True(top.Green > top.Red && top.Green > 100);
    }
}
