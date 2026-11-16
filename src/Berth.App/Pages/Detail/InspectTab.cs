using Berth.Components;
using Berth.Engine.Models;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>What the Inspect tab is given: the last read of the container and the engine's whole answer, indented.</summary>
/// <remarks>
/// A record: the tab renders again only when these change (a new read), not each time the page does (ten times a second while a log
/// floods). Both compare by reference first, so an unchanged read costs nothing to compare.
/// </remarks>
internal sealed record InspectTabProps(ContainerDetails Details, string RawJson);

/// <summary>
/// The "Inspect" tab of a container's detail page: a row of section buttons (Platform, Cmd, State, ...; only the sections there are)
/// that scroll to their section, each section a two-column table of names and values with a copy button per value, and a "Raw JSON"
/// switch that shows the engine's whole answer instead, with Copy. The sections come from <see cref="InspectSections"/>.
/// </summary>
internal sealed class InspectTab : Component<InspectTabProps>
{
    private const double NameWidth = 280;
    private const double JsonFontSize = 12;

    private static readonly GridSize[] OneColumn = [GridSize.Star()];
    private static readonly GridSize[] ToolbarColumns = [GridSize.Star(), GridSize.Auto, GridSize.Auto];
    private static readonly GridSize[] RowColumns = [GridSize.Px(NameWidth), GridSize.Star(), GridSize.Auto];
    private static readonly GridSize[] OneRow = [GridSize.Auto];

    // The toolbar, then the tables or the JSON.
    private static readonly GridSize[] TabRows = [GridSize.Auto, GridSize.Star()];

    public override Element Render()
    {
        var details = Props.Details;
        var rawJson = Props.RawJson;
        var (raw, setRaw) = UseState(false);
        var scroller = UseMemo(() => new SectionScroller(), Array.Empty<object>());

        // Built once per read of the container; switching to the JSON and back reuses them.
        var items = UseMemo(() => Items(InspectSections.Build(details)), details);
        var tables = UseMemo(() => Tables(items, scroller), items);
        var chips = UseMemo(() => Chips(items, scroller), items);
        var json = UseMemo(() => RawView(rawJson), rawJson);

        var toolbar = Grid(
                ToolbarColumns,
                OneRow,
                raw ? null : chips.Grid(column: 0),
                raw
                    ? Button("Copy", () => ContainerParts.CopyToClipboard(rawJson))
                        .AutomationId("inspect-copy-json")
                        .AutomationName("Copy the JSON")
                        .ToolTip("Copy the JSON")
                        .VAlign(VerticalAlignment.Center)
                        .Margin(16, 0, 16, 0)
                        .Grid(column: 1)
                    : null,
                ToggleSwitch(raw, setRaw, onContent: "Raw JSON", offContent: "Raw JSON")
                    .AutomationId("inspect-raw-toggle")
                    .AutomationName("Raw JSON")
                    .MinWidth(0)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(16, 0, 0, 0)
                    .Grid(column: 2))
            .Margin(0, 0, 0, 12);

        return Grid(OneColumn, TabRows, toolbar.Grid(row: 0), (raw ? json : tables).Grid(row: 1))
            .Margin(0, 0, 0, 16)
            .Flex(grow: 1, basis: 0);
    }

    /// <summary>The sections as one list: a section's title, then its rows.</summary>
    private static IReadOnlyList<Item> Items(IReadOnlyList<InspectSection> sections)
    {
        var items = new List<Item>();
        foreach (var section in sections)
        {
            items.Add(new TitleItem(section.Title, First: items.Count == 0));
            for (var i = 0; i < section.Rows.Count; i++)
                items.Add(new RowItem(section.Title, i, section.Rows[i]));
        }

        return items;
    }

    /// <summary>One button per section that scrolls its title to the top of the tables; they wrap onto more lines when narrow.</summary>
    private static Element Chips(IReadOnlyList<Item> items, SectionScroller scroller) =>
        FlexRow(
            [
                .. items
                    .Select((item, index) => (item, index))
                    .Where(x => x.item is TitleItem)
                    .Select(x =>
                    {
                        var title = ((TitleItem)x.item).Title;
                        return Button(title, () => scroller.Show(x.index))
                            .AutomationId("inspect-chip-" + title)
                            .AutomationName("Go to " + title)
                            .Padding(12, 4, 12, 4);
                    }),
            ]) with
        {
            Wrap = FlexWrap.Wrap,
            ColumnGap = 8,
            RowGap = 8,
            AlignItems = FlexAlign.Center,
        };

    /// <summary>The sections, virtualized: only the rows in view are built, so a container with hundreds of labels stays quick.</summary>
    private static Element Tables(IReadOnlyList<Item> items, SectionScroller scroller) =>
        LazyVStack(items, item => item.Key, (item, _) => item switch
            {
                TitleItem title => Title(title),
                RowItem row => Row(row),
                _ => throw new InvalidOperationException("A new inspect item needs its view here: " + item.GetType().Name),
            })
            .SetRepeater(repeater =>
            {
                scroller.Repeater = repeater;
                repeater.HorizontalAlignment = HorizontalAlignment.Stretch;
                if (repeater.Layout is StackLayout stack)
                    stack.Spacing = 0;
            })
            .AutomationId("inspect-sections")
            .AutomationName("Inspect");

    private static Element Title(TitleItem title) =>
        Widgets.StrongText(title.Title)
            .AutomationId("inspect-section-" + title.Title)
            .Padding(0, title.First ? 0 : 24, 0, 8);

    /// <summary>
    /// A name and its value on one line, each ending in an ellipsis with the whole text in its tooltip; the value is selectable and its
    /// copy button copies all of it.
    /// </summary>
    private static Element Row(RowItem item)
    {
        var (name, value) = item.Row;
        var nameText = Widgets.OneLine(Body(name)).Foreground(Theme.SecondaryText).ToolTip(name).VAlign(VerticalAlignment.Center).Margin(0, 0, 16, 0);
        var valueText = Widgets.OneLine(Body(value))
            .Foreground(Theme.PrimaryText)
            .IsTextSelectionEnabled()
            .VAlign(VerticalAlignment.Center);
        var copy = Widgets.IconButton(Glyphs.Copy, "Copy value", () => ContainerParts.CopyToClipboard(value), size: 12)
            .AutomationId($"inspect-copy-{item.Section}-{name}")
            .AutomationName("Copy " + name)
            .Padding(4)
            .MinWidth(28)
            .MinHeight(28)
            .Margin(8, 0, 0, 0)
            .IsEnabled(value.Length > 0);

        return Border(
                Grid(
                    RowColumns,
                    OneRow,
                    nameText.Grid(column: 0),
                    (value.Length > 0 ? valueText.ToolTip(value) : valueText).Grid(column: 1),
                    copy.Grid(column: 2)))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 0, 1)
            .Padding(12, 4, 4, 4);
    }

    /// <summary>
    /// The engine's whole answer: monospace, selectable, scrollable both ways. A read-only text box: it scrolls and selects across
    /// lines (Ctrl+A, Ctrl+C) and lays out tens of kilobytes quickly.
    /// </summary>
    private static Element RawView(string json) =>
        TextBox(json)
            .IsReadOnly()
            .AcceptsReturn()
            .TextWrapping(TextWrapping.NoWrap)
            .FontFamily("Cascadia Mono, Consolas")
            .FontSize(JsonFontSize)
            .Set(box =>
            {
                box.IsSpellCheckEnabled = false;
                Microsoft.UI.Xaml.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
                Microsoft.UI.Xaml.Controls.ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            })
            .AutomationId("inspect-raw-json")
            .AutomationName("Raw JSON");

    /// <summary>One line of the tables: a section's title or one of its rows.</summary>
    private abstract record Item(string Key);

    private sealed record TitleItem(string Title, bool First) : Item("title:" + Title);

    private sealed record RowItem(string Section, int Index, InspectRow Row) : Item($"row:{Section}:{Index}");

    /// <summary>Scrolls a section's title to the top of the tables. One per tab, kept across renders.</summary>
    private sealed class SectionScroller
    {
        public ItemsRepeater? Repeater { get; set; }

        /// <summary>Brings item <paramref name="index"/> (a section's title) to the top of the view.</summary>
        public void Show(int index)
        {
            if (Repeater?.GetOrCreateElement(index) is not { } element)
                return;
            element.UpdateLayout();
            element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = true });
        }
    }
}
