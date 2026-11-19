using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.Camera.Ui;

/// <summary>A shortcut picker: click it, press the keys. Shows whether the OS accepted it.</summary>
internal sealed class HotkeyBox : StackPanel
{
    readonly IHotkey? _key;
    readonly Action<string?> _save;
    readonly Button _box = new() { MinWidth = 150, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly TextBlock _note = Muted("");
    bool _listening;

    public HotkeyBox(string label, IHotkey? key, Action<string?> save)
    {
        _key = key; _save = save;
        Spacing = 4;
        var clear = Button("Clear", () => Set(null));
        Children.Add(Muted(label));
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _box, clear } });
        Children.Add(_note);
        // While listening the old shortcut is let go: the OS would otherwise take those keys before this box sees them.
        _box.Click += (_, _) => { if (_listening) return; _old = _key?.Gesture; _key?.Change(null); _listening = true; Show(); _box.Focus(); };
        _box.LostFocus += (_, _) => Cancel();
        _box.KeyDown += OnKey;
        Show();
    }

    string? _old;

    void Cancel()
    {
        if (!_listening) return;
        _listening = false;
        _key?.Change(_old);
        Show();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (!_listening) return;
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None) { Cancel(); return; }
        var text = HotkeyText.Format(e.KeyModifiers, e.Key);
        if (text == null) return; // just a modifier so far
        if (HotkeyText.Problem(text) is { } problem) { _note.Text = problem; return; }
        _listening = false;
        Set(text);
    }

    void Set(string? text)
    {
        _key?.Change(text);
        _save(text);
        Show();
    }

    void Show()
    {
        _box.Content = _listening ? "Press the keys…" : _key?.Gesture ?? "None";
        if (_listening) { _note.Text = "Hold Ctrl/Alt/Shift and press a key. Esc cancels."; return; }
        _note.Text = _key == null ? "" : _key.Error ?? (_key.Active ? "Works anywhere, also while the game is in front." : "");
        _note.Themed(TextBlock.ForegroundProperty, _key?.Error != null ? ThemeTokens.Warning : ThemeTokens.TextMuted);
    }
}
