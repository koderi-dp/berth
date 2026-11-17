using System.Collections.Immutable;
using Berth.Engine.Models;
using Berth.Presentation;
using Microsoft.UI.Input;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Windows.UI.Core;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>
/// What the remove table dialog calls back: one instance per page, kept across renders, so the dialog's props stay equal while nothing it
/// shows changed; <see cref="RemoveDialog.For"/> points its members at the latest actions.
/// </summary>
internal sealed class RemoveTableHandlers
{
    /// <summary>Remove was clicked with these rows checked (keys of <see cref="RemoveTableRow.Key"/>).</summary>
    public Action<ImmutableHashSet<string>> Confirm { get; set; } = _ => { };

    /// <summary>The dialog closed, by Remove or Cancel.</summary>
    public Action Done { get; set; } = () => { };
}

/// <summary>
/// What the remove table dialog shows: its <paramref name="Title"/>, the <paramref name="Table"/> made when it opened, and the engine's
/// disk use (<paramref name="Usage"/>), whose arrival fills in the sizes.
/// </summary>
internal sealed record RemoveTableDialogProps(string Title, RemoveTable Table, DiskUsage? Usage, RemoveTableHandlers Handlers);

/// <summary>
/// The confirmation of a "Remove unused …" or a delete of several rows: a line saying what this removes, a table of the items (select
/// all, name, two columns per kind, size), each checked to start with, a live line of how many are checked and the space they free, what
/// the engine will refuse, and a Remove button that counts them. The checked rows live here only; closing the dialog forgets them. Space
/// toggles the row with the focus (a click on a row toggles it too), Ctrl+A in the table checks every row. Cancel is the default button.
/// </summary>
internal sealed class RemoveTableDialog : Component<RemoveTableDialogProps>
{
    private const double RowHeight = 48;

    /// <summary>At most this many rows are shown before the table scrolls.</summary>
    private const int VisibleRows = 8;

    /// <summary>The dialog's widest content (WinUI's dialog adds 24 on each side).</summary>
    private const double ContentWidth = 672;

    private enum Column { Select, Name, Second, Third, Size }

    private static readonly ListColumns<Column> WithSize = new(
        ListColumn<Column>.Select(),
        ListColumn<Column>.Of(GridSize.Star()) with { Title = "Name" },
        ListColumn<Column>.Of(GridSize.Px(120)) with { Title = "Second" },
        ListColumn<Column>.Of(GridSize.Px(120)) with { Title = "Third" },
        ListColumn<Column>.Of(GridSize.Px(104)) with { Title = "Size", AlignRight = true });

    private static readonly ListColumns<Column> WithoutSize = new(
        ListColumn<Column>.Select(),
        ListColumn<Column>.Of(GridSize.Star()) with { Title = "Name" },
        ListColumn<Column>.Of(GridSize.Px(120)) with { Title = "Second" },
        ListColumn<Column>.Of(GridSize.Px(120)) with { Title = "Third" });

    private static readonly ListSortView<Column> NoSort = new(Column.Name, false, _ => "", _ => { });

    public override Element Render()
    {
        var (title, opened, usage, handlers) = Props;
        // Sizes come in once the disk use is read; the rows are made again only then, so the list control keeps one list.
        var table = UseMemo(() => opened.WithUsage(usage), opened, (object?)usage ?? opened);
        var (chosen, setChosen) = UseState(opened.AllKeys);
        var (windowWidth, windowHeight) = UseWindowSize();
        var columns = UseMemo(() => Titled(table), table.Kind);
        var now = UseMemo(() => DateTimeOffset.UtcNow, opened);

        void Toggle(string key, bool on) => setChosen(on ? chosen.Add(key) : chosen.Remove(key));
        void All(bool on) => setChosen(on ? table.AllKeys : ImmutableHashSet<string>.Empty);

        var summary = table.Summary(chosen);

        var header = ListTable.Header(columns, compact: false, table.CheckedState(chosen), connected: true, All, NoSort);
        var list = ListView(table.Rows, r => r.Key, (row, _) => Row(row, table, columns, chosen.Contains(row.Key), now, Toggle).WithKey(row.Key))
            .Set(view =>
            {
                view.SelectionMode = ListViewSelectionMode.None;
                view.IsItemClickEnabled = true;
                view.ItemContainerStyle ??= ListTable.RowContainerStyle();
            })
            .ItemClick(row => Toggle(row.Key, !chosen.Contains(row.Key)))
            .OnKeyDown((_, e) =>
            {
                var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
                if (e.Key == VirtualKey.A && ctrl)
                {
                    All(true);
                    e.Handled = true;
                }
            })
            .AutomationId("remove-table")
            .AutomationName("Items to remove")
            // The dialog never outgrows the window: the table gives up rows first (down to three), then the dialog scrolls.
            .MaxHeight(Math.Clamp(windowHeight - 420, 3 * RowHeight, VisibleRows * RowHeight));

        var content = DialogParts.Stack(
            12,
            Body(table.Lead).TextWrapping().AutomationId("confirm-text"),
            DialogParts.Stack(0, header, list).WithKey("table"),
            Body(summary.Line).Foreground(Theme.SecondaryText).TextWrapping().AutomationId("remove-summary").LiveRegion().WithKey("summary"),
            summary.Notes is { } notes ? DialogParts.FieldHint(notes).AutomationId("remove-notes").WithKey("notes") : null);

        return (ContentDialog(title, content.Width(Math.Clamp(windowWidth - 112, 280, ContentWidth)), summary.ConfirmText) with
            {
                IsOpen = true,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                IsPrimaryButtonEnabled = summary.CanConfirm,
                OnClosed = result =>
                {
                    if (result == ContentDialogResult.Primary && !chosen.IsEmpty)
                        handlers.Confirm(chosen);
                    handlers.Done();
                },
            })
            .Wide(ContentWidth + 48);
    }

    /// <summary>The columns with this kind's titles (a static set per kind, so every row lays out in the same widths).</summary>
    private static ListColumns<Column> Titled(RemoveTable table)
    {
        var shape = table.HasSize ? WithSize : WithoutSize;
        return new ListColumns<Column>(
        [
            .. shape.All.Select(c => c.Title switch
            {
                "Second" => c with { Title = table.SecondTitle },
                "Third" => c with { Title = table.ThirdTitle },
                _ => c,
            }),
        ]);
    }

    private static Element Row(RemoveTableRow row, RemoveTable table, ListColumns<Column> columns, bool isChecked, DateTimeOffset now, Action<string, bool> toggle)
    {
        var tip = row.InUseBy is { } users && !row.UntagOnly ? $"{row.FullName}\n{Capital(users)}; the engine will refuse it." : row.FullName;
        Element[] cells =
        [
            ListTable.SelectBox(isChecked, value => toggle(row.Key, value), "Select " + row.Name).Grid(column: 0),
            Widgets.OneLine(Body(row.Name)).ToolTip(tip).VAlign(VerticalAlignment.Center).Margin(0, 0, 12, 0).Grid(column: 1),
            ListTable.CellText(row.Second).Grid(column: 2),
            ListTable.CellText(row.ThirdText(now)).Grid(column: 3),
            .. table.HasSize
                ? (Element[])
                [
                    Body(row.SizeText)
                        .Foreground(row.Size == RemoveSize.Known ? Theme.PrimaryText : Theme.SecondaryText)
                        .ToolTip(row.SizeTip)
                        .TextWrapping(TextWrapping.NoWrap)
                        .HAlign(HorizontalAlignment.Right)
                        .VAlign(VerticalAlignment.Center)
                        .Margin(0, 0, ListTable.NumberCellEndMargin, 0)
                        .Grid(column: 4),
                ]
                : [],
        ];
        return ListTable.Row(columns, compact: false, RowHeight, cells);
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
