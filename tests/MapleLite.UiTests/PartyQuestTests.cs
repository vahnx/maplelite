using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MapleLite.Core.Capture;
using MapleLite.Plugins.XpTracker;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using Xunit;

namespace MapleLite.UiTests;

public class PartyQuestTests
{
    [AvaloniaFact]
    public async Task Tracker_tabs_filter_quests_and_show_individual_runs()
    {
        var storage = new Storage(Path.Combine(Path.GetTempPath(), "MapleLite-xp-tests", Guid.NewGuid().ToString("N")));
        var log = new XpLog(storage.Folder);
        var runs = new PqRunLog(storage);
        var start = DateTimeOffset.Now.AddDays(-2);
        void Add(string quest, int minutes, int seconds, params long[] amounts)
        {
            var run = runs.Start(quest, start);
            for (int i = 0; i < amounts.Length; i++)
            {
                var gain = new XpGain(start.AddSeconds(i + 10), amounts[i], "PQ fixture", i > 0);
                log.Add(gain);
                if (i > 0) runs.SetSource(run.Id, gain, i == 1 ? PqExpSource.Stage : PqExpSource.Completion);
            }
            runs.End(start.AddMinutes(minutes).AddSeconds(seconds));
            start = start.AddHours(1);
        }
        Add("Kerning City PQ", 13, 40, 16_000);
        Add("Kerning City PQ", 15, 10, 17_200);
        Add("Kerning City PQ", 14, 20, 3_200, 9_200, 6_000);
        Add("Ludibrium PQ", 18, 0, 21_000);
        Add("Ludibrium PQ", 19, 0, 22_200);
        start = DateTimeOffset.Now.AddMinutes(-20);
        Add("Orbis PQ", 7, 0, 9_700);
        start = DateTimeOffset.Now.AddMinutes(-10);
        Add("Orbis PQ", 8, 0, 10_000);
        log.Flush();

        var context = DispatchProxy.Create<IPluginContext, Context>();
        ((Context)(object)context).Storage = storage;
        var tracker = new XpTrackerPlugin();
        await tracker.StartAsync(context);
        var tabs = Assert.IsType<TabControl>(tracker.CreatePanel());
        var window = new Window
        {
            Width = 1440, Height = 1024, Padding = new Thickness(22),
            Content = new ScrollViewer { Content = PanelKit.Stack(12, PanelKit.Text("XP Tracker", size: 26), tabs) },
        }.Themed(Window.BackgroundProperty, ThemeTokens.Background);

        try
        {
            window.Show();
            Assert.Equal("Live", ((TabItem)tabs.SelectedItem!).Header);
            tabs.SelectedIndex = 1;
            await WaitForText(window, "114,500 EXP");
            Assert.Contains("51,600 EXP", Texts(window));
            Assert.Contains("7 runs across 3 PQs", Texts(window));
            Assert.Contains("Run 3 breakdown", Texts(window));
            Assert.Contains("9,200 EXP", Texts(window));
            Assert.Contains("6,000 EXP", Texts(window));
            Capture(window, "party-quests");

            FindButton(window, "Classify reward EXP…").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var dialog = Assert.Single(window.OwnedWindows);
            var source = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.SelectedItem as string == "Completion reward");
            source.SelectedIndex = 2;
            FindButton(dialog, "Save classification").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForText(window, "15,200 EXP");
            Assert.Contains("18,400 EXP", Texts(window));

            var runList = FindList(window, "RunList");
            Click(window, (ListBoxItem)runList.Items[1]!);
            Assert.Contains("Run 2 breakdown", Texts(window));
            Assert.Contains("17,200 EXP", Texts(window));
            var quests = FindList(window, "QuestList");
            Click(window, (ListBoxItem)quests.Items[1]!);
            Assert.Contains("43,200 EXP", Texts(window));
            Assert.Contains("22,200 EXP", Texts(window));

            var range = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.SelectedItem as string == "All time");
            // The fixture is also valid when the last Orbis run crossed midnight.
            range.SelectedIndex = 1;
            Assert.DoesNotContain("51,600 EXP", Texts(window));
            Assert.DoesNotContain("43,200 EXP", Texts(window));
            range.SelectedIndex = 0;
            quests.SelectedIndex = 0;
            window.Width = 360;
            Pump();
            Capture(window, "party-quests-narrow");

            var name = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEditable);
            var startButton = FindButton(window, "Start run");
            name.SelectedItem = "Henesys PQ";
            Assert.Equal("Henesys PQ", name.Text);
            Assert.True(startButton.IsEnabled);
            name.Text = "Custom PQ";
            Assert.True(startButton.IsEnabled);
            startButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForText(window, "Custom PQ");
            FindButton(window, "End run").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForText(window, "Start a run before entering the PQ.");
            var saved = new PqRunLog(storage);
            Assert.Null(saved.Active);
            Assert.Contains(saved.Runs, r => r.Quest == "Custom PQ" && r.Ended.HasValue);

            tabs.SelectedIndex = 2;
            await WaitForText(window, "Browse EXP");
            tabs.SelectedIndex = 0;
            await WaitForText(window, "This session");
        }
        finally
        {
            window.Close();
            await tracker.StopAsync();
        }
    }

    [Fact]
    public void Adjacent_runs_own_each_gain_once_and_classifications_survive_reload()
    {
        var storage = new Storage(Path.GetTempPath());
        var log = new PqRunLog(storage);
        var start = DateTimeOffset.Now;
        var a = log.Start("First PQ", start);
        var boundary = start.AddMinutes(2);
        log.End(boundary);
        log.Start("Second PQ", boundary);
        var monster = new XpGain(start, 70, "PQ");
        var reward = new XpGain(start.AddSeconds(1), 30, "PQ", true);
        var edge = new XpGain(boundary, 10, "PQ", true);
        var before = new XpGain(start.AddSeconds(-1), 99, "Outside");
        log.SetSource(a.Id, reward, PqExpSource.Completion);
        Assert.Throws<InvalidOperationException>(() => log.SetSource(a.Id, edge, PqExpSource.Stage));

        var saved = new PqRunLog(storage);
        var summaries = PqRunLog.Summarize(saved.Runs, [edge, before, reward, monster]);
        Assert.Equal(100, summaries[0].Exp);
        Assert.Equal(70, summaries[0].ExpFrom(PqExpSource.Monster));
        Assert.Equal(30, summaries[0].ExpFrom(PqExpSource.Completion));
        Assert.Equal(10, summaries[1].ExpFrom(PqExpSource.Unclassified));
        Assert.Throws<InvalidOperationException>(() => saved.Start("Overlapping", boundary));
    }

    static IEnumerable<string?> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(c => c.IsEffectivelyVisible).Select(c => c.Text);

    static ListBox FindList(Window window, string name) =>
        window.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == name);

    static Button FindButton(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().Single(c => c.Content as string == text && c.IsEffectivelyVisible);

    static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Pump();
    }

    static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    static async Task WaitForText(Window window, string text)
    {
        for (int i = 0; i < 200; i++)
        {
            Pump();
            if (Texts(window).Contains(text)) return;
            await Task.Delay(20);
        }
        Assert.Contains(text, Texts(window));
    }

    static void Capture(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("MAPLELITE_UI_SCREENSHOTS");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(folder, name + ".png"));
    }

    public class Context : DispatchProxy
    {
        public IPluginStorage Storage = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Storage" => Storage,
            "get_Frames" => new EmptyFrames(),
            "Log" => null,
            _ => throw new NotSupportedException(method.Name),
        };
    }

    sealed class EmptyFrames : IFrameSource, IDisposable
    {
        public CaptureStatus Status => CaptureStatus.Stopped;
        public GameFrame? Latest => null;
        public event Action<CaptureStatus>? StatusChanged { add { } remove { } }
        public IDisposable Subscribe(Action<GameFrame> onFrame, double fps) => this;
        public void Dispose() { }
    }

    sealed class Storage(string folder) : IPluginStorage
    {
        readonly Dictionary<string, string> _values = new();
        public string Folder => folder;
        public T Load<T>(string name) where T : new() => _values.TryGetValue(name, out var json)
            ? JsonSerializer.Deserialize<T>(json) ?? new() : new();
        public void Save<T>(string name, T value) => _values[name] = JsonSerializer.Serialize(value);
    }
}
