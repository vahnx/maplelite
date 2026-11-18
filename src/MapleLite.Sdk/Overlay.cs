namespace MapleLite.Sdk;

/// <summary>
/// Drawing on top of the game: a separate, click-through MapleLite window laid over the game window, never anything
/// inside the game. Display only, with no input methods. Off until the user turns overlays on in Settings, and it
/// can't show over exclusive fullscreen. Plugins should still show the same information in their own panel.
/// See docs/OVERLAYS.md.
/// </summary>
public interface IOverlay
{
    /// <summary>True when the overlay is on and following the game window, so items will be seen.</summary>
    bool Visible { get; }
    /// <summary>Overlays are turned on in Settings and work on this computer (they may still be hidden right now, e.g. while the game isn't the active window).</summary>
    bool Enabled { get; }
    /// <summary>Why it isn't visible (turned off, game minimized or in exclusive fullscreen, …), or null.</summary>
    string? Status { get; }
    /// <summary>Replaces everything this plugin draws with <paramref name="items"/>. Safe from any thread.</summary>
    void Draw(IReadOnlyList<OverlayItem> items);
    /// <summary>Removes everything this plugin draws.</summary>
    void Clear();
}

public enum OverlayTone { Normal, Accent, Good, Warning, Danger, Muted }

/// <summary>
/// A label at a point in the game picture (the same coordinates as <see cref="MapleLite.Core.Capture.GameFrame"/>'s
/// frame). With <see cref="Ring"/> set (0–1), a small circular timer is drawn around the point, filled that far.
/// </summary>
public sealed record OverlayItem(double X, double Y, string Text, OverlayTone Tone = OverlayTone.Normal, double? Ring = null, double Size = 14);
