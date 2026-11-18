using MapleLite.Core.Imaging;

namespace MapleLite.Sdk;

/// <summary>
/// How plugins share what they read without depending on each other (like RuneLite's event bus).
/// Any plugin can publish or subscribe; event types live here in the SDK so everyone sees the same types.
/// Handlers run on the publisher's thread: keep them short, and use context.OnUi for UI work.
/// </summary>
public interface IEventBus
{
    void Publish<T>(T evt) where T : class;
    /// <summary>Calls <paramref name="handler"/> for every later event of type T. Dispose to stop.</summary>
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
    /// <summary>The most recent event of type T, or null. Handy for state such as where the chat box is.</summary>
    T? Last<T>() where T : class;
}

/// <summary>A chat line a plugin saved. Chat History publishes every message it logs.</summary>
/// <param name="Channel">Whisper, Buddy, Party, Guild, Alliance, General, Megaphone, System, Trade, Fame, Loot or Unknown.</param>
/// <param name="Text">The line as read, including any "[HH:mm]" timestamp.</param>
public sealed record ChatMessageEvent(DateTimeOffset Time, string Channel, string Text, string Source, string PublisherId);

/// <summary>
/// EXP went up (usually a kill). XP Tracker publishes one per rise it sees on the status bar; several kills within
/// one reading (about 0.2 s) arrive as one event.
/// </summary>
public sealed record ExpGainedEvent(DateTimeOffset Time, long Amount, long Exp, double Percent, string PublisherId);

/// <summary>Where the chat box is in the game picture. Chat History publishes it when it finds or you select it.</summary>
public sealed record ChatAreaEvent(Region Area, int FrameWidth, int FrameHeight, string PublisherId);
