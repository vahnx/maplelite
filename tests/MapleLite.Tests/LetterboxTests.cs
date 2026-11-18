using MapleLite.Core.Imaging;
using Xunit;

namespace MapleLite.Tests;

public class LetterboxTests
{
    static readonly int Black = Pixel.Rgb(0, 0, 0), Scene = Pixel.Rgb(60, 90, 40), Bar = Pixel.Rgb(80, 80, 90);

    /// <summary>A frame of <paramref name="w"/>x<paramref name="h"/> with the game drawn at <paramref name="game"/> and black around it.</summary>
    static Frame Make(int w, int h, Region game, Func<int, int, int>? paint = null)
    {
        var px = new int[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = x >= game.X && x < game.X + game.Width && y >= game.Y && y < game.Y + game.Height
                    ? paint?.Invoke(x - game.X, y - game.Y) ?? Scene : Black;
        return new Frame(w, h, px);
    }

    [Fact]
    public void Sixteen_by_nine_game_on_a_sixteen_by_ten_monitor_loses_its_bars()
    {
        // what a 1680x1050 monitor shows in borderless fullscreen: 53 black rows above and below
        var f = Make(1680, 1050, new Region(0, 53, 1680, 944));
        Assert.Equal(new Region(0, 53, 1680, 944), ImageOps.Letterbox(f));
    }

    [Fact]
    public void Uneven_bars_are_each_cut()
    {
        var f = Make(1680, 1050, new Region(0, 52, 1680, 945));
        Assert.Equal(new Region(0, 52, 1680, 945), ImageOps.Letterbox(f));
    }

    [Fact]
    public void Pillarbox_bars_left_and_right_are_cut()
    {
        var f = Make(1920, 1080, new Region(240, 0, 1440, 1080));
        Assert.Equal(new Region(240, 0, 1440, 1080), ImageOps.Letterbox(f));
    }

    [Fact]
    public void A_dark_sky_in_a_window_is_not_a_bar()
    {
        // windowed: the top of the map is black night sky, but the status bar at the bottom never is
        var f = Make(1366, 768, new Region(0, 0, 1366, 768), (x, y) => y < 120 ? Black : y > 700 ? Bar : Scene);
        Assert.Equal(f.Bounds, ImageOps.Letterbox(f));
    }

    [Fact]
    public void A_dark_sky_inside_letterbox_bars_only_cuts_the_bars()
    {
        var f = Make(1680, 1050, new Region(0, 53, 1680, 944), (x, y) => y < 150 ? Black : y > 880 ? Bar : Scene);
        Assert.Equal(new Region(0, 53, 1680, 944), ImageOps.Letterbox(f));
    }
}
