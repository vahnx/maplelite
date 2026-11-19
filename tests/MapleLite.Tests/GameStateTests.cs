using MapleLite.Core.Game;
using Xunit;

namespace MapleLite.Tests;

public class GameStateTests
{
    [Fact]
    public void The_first_name_read_is_taken_and_a_change_needs_two_reads()
    {
        var g = new GameWatcher(null!, null!);
        Assert.Equal("Cheesy", g.TakeIgn("Cheesy"));
        Assert.Equal("Cheesy", g.TakeIgn("Chcesy"));
        Assert.Equal("Cheesy", g.TakeIgn(null));
        Assert.Equal("Cheesy", g.TakeIgn("Frog"));
        Assert.Equal("Frog", g.TakeIgn("Frog"));
    }

    [Fact]
    public void Nothing_is_known_before_the_first_picture()
    {
        IGameState g = new GameWatcher(null!, null!);
        Assert.Null(g.Map); Assert.Null(g.Location); Assert.Null(g.Channel); Assert.Null(g.Hp); Assert.Null(g.Minimap);
    }
}
