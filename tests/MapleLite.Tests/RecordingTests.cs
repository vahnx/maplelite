using MapleLite.Core.Imaging;
using MapleLite.Sdk;
using Xunit;

namespace MapleLite.Tests;

public class RecordingTests
{
    static Frame Solid(int color)
    {
        var f = new Frame(8, 6);
        Array.Fill(f.Px, color);
        return f;
    }

    [Fact]
    public async Task Frames_round_trip_with_their_timings_and_repeats_are_saved_once()
    {
        var folder = Path.Combine(Path.GetTempPath(), "maplelite-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = DateTimeOffset.Now;
            var red = Solid(Pixel.Rgb(200, 10, 10));
            var w = new Recording.Writer(folder, new Recording.Info(start, 5, "MapleStory", "windowed", 1));
            Assert.True(w.Add(red, start));
            Assert.True(w.Add(Solid(Pixel.Rgb(200, 10, 10)), start.AddMilliseconds(200))); // same picture, new object
            Assert.True(w.Add(Solid(Pixel.Rgb(10, 200, 10)), start.AddMilliseconds(400)));
            await w.DisposeAsync();

            Assert.True(Recording.IsRecording(folder));
            var index = Recording.ReadIndex(folder);
            Assert.Equal([0, 200, 400], index.Select(e => e.Ms));
            Assert.Equal(index[0].File, index[1].File);
            Assert.NotEqual(index[1].File, index[2].File);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(folder, Recording.FramesDir)).Length);

            var back = FrameFiles.LoadPng(Path.Combine(folder, index[0].File));
            Assert.Equal(red.Px, back.Px);

            var info = Recording.ReadInfo(folder)!;
            Assert.Equal(3, info.Frames);
            Assert.Equal(2, info.Saved);
            Assert.Equal("MapleStory", info.WindowTitle);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
