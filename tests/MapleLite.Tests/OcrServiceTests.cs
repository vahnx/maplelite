using MapleLite.Core.Imaging;
using MapleLite.Core.Ocr;
using Xunit;

namespace MapleLite.Tests;

/// <summary>An engine that reports a word at a fixed spot of whatever image it gets, and counts calls.</summary>
sealed class FakeEngine(int maxDim = 2600, int delayMs = 0) : IOcrEngine
{
    public int Calls;
    public Frame? LastImage;
    public string Name => "fake";
    public int MaxImageDimension => maxDim;

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        LastImage = image;
        if (delayMs > 0) await Task.Delay(delayMs, ct);
        // one word whose box is at (30,60) size 90x30 in the enlarged image
        var w = new OcrWord("hello", new TextBounds(30, 60, 90, 30));
        return [new OcrLine("hello", w.Box, [w])];
    }
}

public class OcrServiceTests
{
    static Frame Solid(int w, int h, int color = unchecked((int)0xff202020))
    {
        var f = new Frame(w, h);
        Array.Fill(f.Px, color);
        return f;
    }

    [Fact]
    public async Task Boxes_map_back_to_frame_coordinates()
    {
        var engine = new FakeEngine();
        var ocr = new OcrService(engine);
        var frame = Solid(800, 600);
        var r = await ocr.ReadAsync(frame, new OcrRequest(new Region(100, 200, 300, 100)) { Scale = 3 });
        var box = Assert.Single(r.Lines).Box;
        Assert.Equal(3, r.Scale);
        Assert.Equal(100 + 30 / 3.0, box.X, 3);
        Assert.Equal(200 + 60 / 3.0, box.Y, 3);
        Assert.Equal(30, box.Width, 3);
        Assert.Equal(900, engine.LastImage!.W);
    }

    [Fact]
    public async Task Padding_is_subtracted_when_mapping_back()
    {
        var engine = new FakeEngine();
        var ocr = new OcrService(engine);
        var r = await ocr.ReadAsync(Solid(400, 400), new OcrRequest(new Region(10, 10, 100, 50)) { Scale = 2, Pad = 4 });
        Assert.Equal((100 + 8) * 2, engine.LastImage!.W);
        Assert.Equal(10 + 30 / 2.0 - 4, r.Lines[0].Box.X, 3);
    }

    [Fact]
    public async Task Auto_scale_stays_within_engine_limit()
    {
        var engine = new FakeEngine(maxDim: 1000);
        var ocr = new OcrService(engine);
        var r = await ocr.ReadAsync(Solid(1200, 800), new OcrRequest(new Region(0, 0, 400, 100)));
        Assert.Equal(2, r.Scale);
        var big = await ocr.ReadAsync(Solid(1200, 800), new OcrRequest(new Region(0, 0, 200, 50)));
        Assert.Equal(3, big.Scale); // auto never goes above 3
    }

    [Fact]
    public async Task Region_too_big_for_engine_is_refused()
    {
        var ocr = new OcrService(new FakeEngine(maxDim: 500));
        await Assert.ThrowsAsync<ArgumentException>(() => ocr.ReadAsync(Solid(900, 900), new OcrRequest(new Region(0, 0, 800, 100))));
    }

    [Fact]
    public async Task Same_frame_and_request_is_read_once_and_shared()
    {
        var engine = new FakeEngine(delayMs: 50);
        var ocr = new OcrService(engine);
        var frame = Solid(500, 300);
        var req = new OcrRequest(new Region(0, 0, 200, 100));
        // two "plugins" asking at the same time
        var results = await Task.WhenAll(ocr.ReadAsync(frame, req), ocr.ReadAsync(frame, req));
        Assert.Equal(1, engine.Calls);
        Assert.Contains(results, x => x.FromCache);
        Assert.Equal(1, ocr.Stats.CacheHits);
    }

    [Fact]
    public async Task Different_preprocess_or_frame_is_not_shared()
    {
        var engine = new FakeEngine();
        var ocr = new OcrService(engine);
        var frame = Solid(500, 300);
        var region = new Region(0, 0, 200, 100);
        await ocr.ReadAsync(frame, new OcrRequest(region));
        await ocr.ReadAsync(frame, new OcrRequest(region) { Preprocess = OcrPreprocessor.ChatGlyphs });
        await ocr.ReadAsync(Solid(500, 300), new OcrRequest(region));
        await ocr.ReadAsync(frame, new OcrRequest(region) { Cache = false });
        Assert.Equal(4, engine.Calls);
    }

    [Fact]
    public async Task Region_is_clipped_to_the_frame()
    {
        var engine = new FakeEngine();
        var ocr = new OcrService(engine);
        var r = await ocr.ReadAsync(Solid(300, 200), new OcrRequest(new Region(250, 150, 500, 500)) { Scale = 1 });
        Assert.Equal(new Region(250, 150, 50, 50), r.Region);
        var empty = await ocr.ReadAsync(Solid(300, 200), new OcrRequest(new Region(400, 400, 50, 50)));
        Assert.Empty(empty.Lines);
    }

    [Fact]
    public async Task A_failed_shared_read_reports_the_error_to_every_waiter()
    {
        var ocr = new OcrService(new ThrowingEngine());
        var frame = Solid(300, 200);
        var req = new OcrRequest(new Region(0, 0, 100, 100));
        var a = ocr.ReadAsync(frame, req);
        var b = ocr.ReadAsync(frame, req);
        await Assert.ThrowsAsync<InvalidOperationException>(() => a);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b);
        Assert.Equal(1, ocr.Stats.Failures);
    }

    [Fact]
    public async Task Without_an_engine_reads_say_so()
    {
        var ocr = new OcrService(null);
        Assert.False(ocr.Available);
        await Assert.ThrowsAsync<OcrUnavailableException>(() => ocr.ReadAsync(Solid(100, 100), new Region(0, 0, 50, 50)));
    }

    sealed class ThrowingEngine : IOcrEngine
    {
        public string Name => "throws";
        public int MaxImageDimension => 2600;
        public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, CancellationToken ct)
        {
            await Task.Delay(30, ct);
            throw new InvalidOperationException("engine broke");
        }
    }
}

public class OcrLayoutTests
{
    static OcrLine Line(params (string text, double x, double y)[] words) =>
        OcrLine.FromWords(words.Select(w => new OcrWord(w.text, new TextBounds(w.x, w.y, 40, 12))));

    [Fact]
    public void Rows_merge_words_on_one_line_and_split_different_lines()
    {
        // engine split one chat line in two, and reported a second line
        var lines = new[] { Line(("[12:00]", 0, 100), ("Bob:", 50, 101)), Line(("hi", 100, 99)), Line(("second", 0, 120)) };
        var rows = OcrLayout.GroupRows(lines);
        Assert.Equal(2, rows.Count);
        Assert.Equal("[12:00] Bob: hi", rows[0].Text);
        Assert.Equal("second", rows[1].Text);
    }
}

public class ImageTests
{
    [Fact]
    public void Scale_nearest_repeats_pixels_and_pads_with_edges()
    {
        var f = new Frame(2, 1, [Pixel.Rgb(255, 0, 0), Pixel.Rgb(0, 0, 255)]);
        var s = ImageOps.Scale(f, 2, smooth: false, pad: 1);
        Assert.Equal(8, s.W); Assert.Equal(6, s.H);
        Assert.Equal(Pixel.Rgb(255, 0, 0), s.At(0, 0));   // pad copies the edge
        Assert.Equal(Pixel.Rgb(255, 0, 0), s.At(3, 3));
        Assert.Equal(Pixel.Rgb(0, 0, 255), s.At(4, 3));
        Assert.Equal(Pixel.Rgb(0, 0, 255), s.At(7, 5));
    }

    [Fact]
    public void Crop_rejects_regions_outside_the_frame()
    {
        var f = new Frame(10, 10);
        Assert.Equal(4, f.Crop(new Region(2, 2, 2, 2)).Px.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Crop(new Region(8, 8, 5, 5)));
    }

    [Fact]
    public void Chat_glyphs_turn_bright_letters_black_on_white()
    {
        var f = new Frame(10, 2);
        Array.Fill(f.Px, Pixel.Rgb(40, 40, 50));
        f.Px[3] = Pixel.Rgb(240, 240, 240);
        var o = OcrPreprocessor.ChatGlyphs.Apply(f);
        Assert.Equal(Pixel.Black, o.At(3, 0));
        Assert.Equal(Pixel.White, o.At(4, 0));
        Assert.NotEqual(Pixel.Black, f.At(3, 0)); // the shared frame is untouched
    }

    [Fact]
    public void Black_frames_are_recognized()
    {
        Assert.True(ImageOps.LooksBlack(new Frame(64, 64)));
        var f = new Frame(64, 64); Array.Fill(f.Px, Pixel.Rgb(80, 120, 60));
        Assert.False(ImageOps.LooksBlack(f));
    }
}
