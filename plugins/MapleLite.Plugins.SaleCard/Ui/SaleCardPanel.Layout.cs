using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using MapleLite.Sdk;
using MapleLite.Sdk.Theming;
using static MapleLite.Sdk.PanelKit;

namespace MapleLite.Plugins.SaleCard;

internal sealed partial class SaleCardPanel
{
    const string LayoutFile = "shop-card-layout";
    const double SplitterSize = 14;
    const double ListMinHeight = 285;
    const double ListMinWidth = 360;

    readonly ShopCardLayout _layout;
    readonly Grid _body = new() { RowDefinitions = new RowDefinitions("Auto,14,Auto") };
    readonly GridSplitter _previewSplitter = Splitter("Resize the item area and card preview");
    Control _previewTools = null!;
    Control _previewActions = null!;

    static bool IsStacked(double width) => width < 860;

    void SaveLayout()
    {
        try { _ctx.Storage.Save(LayoutFile, _layout); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ctx.Log("Could not save the Shop Card layout: " + e.Message);
        }
    }

    void LayoutSections()
    {
        bool stacked = IsStacked(_sections.Bounds.Width);
        var lists = _sections.SelectedIndex switch { 0 => _layout.Selling, 1 => _layout.Buying, _ => null };
        bool bothCollapsed = lists is { AvailableCollapsed: true, SelectedCollapsed: true };
        bool twoStackedLists = stacked && lists is { AvailableCollapsed: false, SelectedCollapsed: false };
        double sectionHeight = twoStackedLists ? 780 : 540;
        double previewHeight = _liveEditorScroll.IsVisible ? 650 : 400;
        bool previewOpen = !_layout.PreviewCollapsed;

        _sections.IsVisible = !_maximized;
        _previewTools.IsVisible = _previewActions.IsVisible = _previewContent.IsVisible = previewOpen;
        Grid.SetRow(_previewBox, _maximized ? 0 : 2);

        bool canResize = !_maximized && !bothCollapsed && previewOpen;
        _previewSplitter.IsVisible = canResize;
        _sections.Height = canResize || bothCollapsed ? double.NaN : sectionHeight;
        _previewBox.Height = _maximized ? 720 : !canResize && previewOpen ? previewHeight : double.NaN;
        var rows = _body.RowDefinitions;
        if (canResize)
        {
            double total = sectionHeight + previewHeight;
            double minSections = twoStackedLists ? 680 : 395;
            double minPreview = _liveEditorScroll.IsVisible && stacked ? 540 : 290;
            double? share = stacked ? _layout.StackedPreviewSplit : _layout.PreviewSplit;
            double top = Math.Clamp(total * (share ?? sectionHeight / total), minSections, total - minPreview);
            _body.Height = total + SplitterSize;
            rows[0].Height = Star(top);
            rows[0].MinHeight = minSections;
            rows[1].Height = new GridLength(SplitterSize);
            rows[2].Height = Star(total - top);
            rows[2].MinHeight = minPreview;
            return;
        }

        _body.Height = double.NaN;
        rows[0].MinHeight = rows[2].MinHeight = 0;
        rows[0].Height = GridLength.Auto;
        rows[1].Height = new GridLength(_maximized ? 0 : SplitterSize);
        rows[2].Height = GridLength.Auto;
    }

    Grid SplitLists(TextBlock availableTitle, Control availableBody, TextBlock selectedTitle, Control selectedBody, ListLayout state)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,0,0"),
            RowDefinitions = new RowDefinitions("*,0,0")
        };
        var splitter = Splitter("Resize the item lists");

        void ToggleAvailable()
        {
            state.AvailableCollapsed = !state.AvailableCollapsed;
            Apply();
            LayoutSections();
            SaveLayout();
        }

        void ToggleSelected()
        {
            state.SelectedCollapsed = !state.SelectedCollapsed;
            Apply();
            LayoutSections();
            SaveLayout();
        }

        var available = Pane(availableTitle, availableTitle.Text!, availableBody, () => state.AvailableCollapsed, ToggleAvailable);
        var selected = Pane(selectedTitle, "On your card", selectedBody, () => state.SelectedCollapsed, ToggleSelected);
        grid.Children.Add(available);
        grid.Children.Add(selected);
        grid.Children.Add(splitter);

        void Apply()
        {
            bool stacked = IsStacked(_sections.Bounds.Width);
            bool bothOpen = !state.AvailableCollapsed && !state.SelectedCollapsed;
            splitter.IsVisible = bothOpen;

            var columns = grid.ColumnDefinitions;
            var rows = grid.RowDefinitions;
            var zero = new GridLength(0);
            var gap = new GridLength(SplitterSize);
            columns[0].Width = stacked ? GridLength.Star : ListLength(state.AvailableCollapsed, state.WidthShare);
            columns[1].Width = stacked ? zero : gap;
            columns[2].Width = stacked ? zero : ListLength(state.SelectedCollapsed, 1 - state.WidthShare);
            columns[0].MinWidth = columns[2].MinWidth = !stacked && bothOpen ? ListMinWidth : 0;
            rows[0].Height = stacked ? ListLength(state.AvailableCollapsed, state.HeightShare) : GridLength.Star;
            rows[1].Height = stacked ? gap : zero;
            rows[2].Height = stacked ? ListLength(state.SelectedCollapsed, 1 - state.HeightShare) : zero;
            rows[0].MinHeight = rows[2].MinHeight = stacked && bothOpen ? ListMinHeight : 0;
        }

        void SetDirection()
        {
            bool stacked = IsStacked(_sections.Bounds.Width);
            splitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
            splitter.Margin = stacked ? new Thickness(1, 4) : new Thickness(4, 1);
            Grid.SetColumn(selected, stacked ? 0 : 2);
            Grid.SetRow(selected, stacked ? 2 : 0);
            Grid.SetColumn(splitter, stacked ? 0 : 1);
            Grid.SetRow(splitter, stacked ? 1 : 0);
            Apply();
        }

        _sections.SizeChanged += (_, e) =>
        {
            if (IsStacked(e.PreviousSize.Width) != IsStacked(e.NewSize.Width)) SetDirection();
        };
        OnSplitterMoved(splitter, () =>
        {
            if (splitter.ResizeDirection == GridResizeDirection.Rows)
                state.HeightShare = Share(grid.RowDefinitions[0].Height, grid.RowDefinitions[2].Height);
            else
                state.WidthShare = Share(grid.ColumnDefinitions[0].Width, grid.ColumnDefinitions[2].Width);
            SaveLayout();
        });
        SetDirection();
        return grid;
    }

    static Border Pane(TextBlock title, string name, Control content, Func<bool> collapsed, Action toggle)
    {
        var dock = new DockPanel();
        var card = Card(dock, 10);
        card.ClipToBounds = true;
        void Update()
        {
            content.IsVisible = !collapsed();
            card.VerticalAlignment = collapsed() ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        }

        var header = CollapseButton(title, name, collapsed, () => { toggle(); Update(); });
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(content);
        Update();
        return card;
    }

    static Button CollapseButton(TextBlock title, string name, Func<bool> collapsed, Action toggle)
    {
        var arrow = Text("", ThemeTokens.TextMuted);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        var header = new Button
        {
            Padding = new Thickness(6, 4),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { arrow, title } }
        };
        void Update()
        {
            arrow.Text = collapsed() ? "▸" : "▾";
            string action = (collapsed() ? "Expand " : "Collapse ") + name;
            AutomationProperties.SetName(header, action);
            ToolTip.SetTip(header, action);
        }
        header.Click += (_, _) => { toggle(); Update(); };
        Update();
        return header;
    }

    static GridSplitter Splitter(string name)
    {
        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Focusable = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(1, 4),
            KeyboardIncrement = 16
        }.Themed(TemplatedControl.BackgroundProperty, ThemeTokens.Border);
        ToolTip.SetTip(splitter, name);
        AutomationProperties.SetName(splitter, name);
        return splitter;
    }

    static void OnSplitterMoved(GridSplitter splitter, Action save)
    {
        splitter.DragCompleted += (_, _) => { if (splitter.IsVisible) save(); };
        splitter.AddHandler(KeyDownEvent, (_, e) =>
        {
            bool resizeKey = splitter.ResizeDirection == GridResizeDirection.Rows
                ? e.Key is Key.Up or Key.Down
                : e.Key is Key.Left or Key.Right;
            if (e.Handled && resizeKey)
                save();
        }, handledEventsToo: true);
    }

    static double Share(GridLength first, GridLength second) => first.Value / (first.Value + second.Value);
    static GridLength Star(double value) => new(value, GridUnitType.Star);
    static GridLength ListLength(bool collapsed, double share) => collapsed ? GridLength.Auto : Star(share);

    sealed class ShopCardLayout
    {
        public double? PreviewSplit { get; set; }
        public double? StackedPreviewSplit { get; set; }
        public bool PreviewCollapsed { get; set; }
        public ListLayout Selling { get; set; } = new();
        public ListLayout Buying { get; set; } = new();
    }

    sealed class ListLayout
    {
        public double WidthShare { get; set; } = 0.5;
        public double HeightShare { get; set; } = 0.5;
        public bool AvailableCollapsed { get; set; }
        public bool SelectedCollapsed { get; set; }
    }
}
