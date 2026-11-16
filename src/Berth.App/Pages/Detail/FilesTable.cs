using System.Collections.Immutable;
using Berth.Components;
using Berth.Files;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using static Microsoft.UI.Reactor.Factories;
using MenuEntry = Microsoft.UI.Reactor.Core.MenuFlyoutItemBase;

namespace Berth.Pages.Detail;


/// <summary>
/// What the Files table calls back. One instance kept by the tab, so the table's props stay equal while nothing it shows changed; the
/// tab points its members at its latest handlers on every render.
/// </summary>
internal sealed class FilesTableHandlers
{
    /// <summary>The rows selected now (click, Ctrl+click, Shift+arrows).</summary>
    public Action<ImmutableArray<FileRow>> Select { get; set; } = _ => { };

    /// <summary>Opens a folder or a link's target as a folder.</summary>
    public Action<FileRow> Open { get; set; } = _ => { };

    /// <summary>"Download…" for these rows.</summary>
    public Action<ImmutableArray<FileRow>> Download { get; set; } = _ => { };

    /// <summary>"Copy path" for these rows.</summary>
    public Action<ImmutableArray<FileRow>> CopyPath { get; set; } = _ => { };

    /// <summary>Backspace or Alt+Up: the parent folder.</summary>
    public Action Up { get; set; } = () => { };

    /// <summary>A click on a column title.</summary>
    public Action<FileColumn> SortBy { get; set; } = _ => { };

    /// <summary>The rows selected, as the table last told.</summary>
    public ImmutableArray<FileRow> Selected { get; set; } = [];
}

/// <summary>What the Files table shows: the rows (one list per listing), the layout, whether actions can act, and the tab's handlers.</summary>
internal sealed record FilesTableProps(ImmutableArray<FileRow> Rows, bool Compact, bool CanAct, ListSort<FileColumn> Sort, FilesTableHandlers Handlers);

/// <summary>
/// The Files table, on the shared list look: a header (Name, Size, Modified, Mode; Mode only in the wide layout) and a row per entry with
/// a folder, file or link glyph. Name, Size and Modified sort (folders stay first). Rows are selected as in Explorer (click, Ctrl+click, Shift and the arrows); a double click or Enter opens
/// a folder, Backspace or Alt+Up goes to the parent, Ctrl+C copies the selected rows' paths. The right-click menu has Open, Download… and
/// Copy path for the row, or for the selection when the row is in it.
/// </summary>
internal sealed class FilesTable : Component<FilesTableProps>
{
    private const double RowHeight = 40;

    private static readonly ListColumns<FileColumn> Columns = new(
        ListColumn<FileColumn>.Of(GridSize.Px(32)),
        ListColumn<FileColumn>.Of(GridSize.Star()) with { Title = "Name", SortBy = FileColumn.Name },
        ListColumn<FileColumn>.Of(GridSize.Px(96)) with { Title = "Size", SortBy = FileColumn.Size, AlignRight = true },
        ListColumn<FileColumn>.Of(GridSize.Px(148)) with { Title = "Modified", SortBy = FileColumn.Modified },
        ListColumn<FileColumn>.InWideOnly(GridSize.Px(112)) with { Title = "Mode" });


    public override Element Render()
    {
        var (rows, compact, canAct, sort, handlers) = Props;
        var header = ListTable.Header(Columns, compact, checkedState: false, connected: canAct, onCheckAll: _ => { }, ListSortView.Of(sort, c => handlers.SortBy(c)));

        var list = ListView(rows, r => r.Name, (row, _) => Row(row, compact, canAct, handlers).WithKey(row.Name)) with
        {
            SelectionMode = ListViewSelectionMode.Extended,
            OnSelectionChanged = selected =>
            {
                handlers.Selected = [.. selected];
                handlers.Select(handlers.Selected);
            },
        };

        var table = list
            .Set(view => view.ItemContainerStyle ??= RowContainerStyle())
            .AutomationId("files-table")
            .AutomationName("Files")
            .OnDoubleTapped((_, _) =>
            {
                if (handlers.Selected is [{ Opens: true } row])
                    handlers.Open(row);
            })
            .OnKeyDown((_, e) => Key(e, handlers));

        return (FlexColumn(header.Flex(shrink: 0), table.Flex(grow: 1, basis: 0)) with { RowGap = 0 }).Flex(grow: 1, basis: 0);
    }

    private static void Key(KeyRoutedEventArgs e, FilesTableHandlers handlers)
    {
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter when handlers.Selected is [{ Opens: true } row]:
                handlers.Open(row);
                e.Handled = true;
                break;
            case VirtualKey.Back:
            case VirtualKey.Up when alt:
                handlers.Up();
                e.Handled = true;
                break;
            case VirtualKey.C when ctrl && !handlers.Selected.IsEmpty:
                handlers.CopyPath(handlers.Selected);
                e.Handled = true;
                break;
        }
    }

    private static Element Row(FileRow row, bool compact, bool canAct, FilesTableHandlers handlers)
    {
        var (glyph, kind) = row.Kind switch
        {
            FileKind.Folder => (Glyphs.Folder, "Folder"),
            FileKind.Link => (Glyphs.Link, "Link"),
            FileKind.Other => (Glyphs.Special, "Special file"),
            _ => (Glyphs.Document, "File"),
        };

        // The menu acts on the selection when the row is in it, on the row alone otherwise.
        ImmutableArray<FileRow> Targets() => handlers.Selected.Contains(row) ? handlers.Selected : [row];
        MenuEntry[] menu =
        [
            .. row.Opens ? (MenuEntry[])[MenuItem("Open", () => handlers.Open(row)) with { IconElement = Widgets.MenuGlyph(Glyphs.OpenFolder) }] : [],
            MenuItem("Download…", () => handlers.Download(Targets())) with { IconElement = Widgets.MenuGlyph(Glyphs.Download), IsEnabled = canAct },
            MenuItem("Copy path", () => handlers.CopyPath(Targets())) with { IconElement = Widgets.MenuGlyph(Glyphs.Copy) },
        ];

        return ListTable.Row(
                Columns,
                compact,
                RowHeight,
                Widgets.Glyph(glyph, size: 16, color: row.Kind == FileKind.Folder ? Theme.AccentText : Theme.SecondaryText)
                    .AutomationName(kind)
                    .HAlign(HorizontalAlignment.Left)
                    .VAlign(VerticalAlignment.Center)
                    .Grid(column: 0),
                ListTable.CellText(row.Shown).Grid(column: 1),
                Body(row.SizeText)
                    .TextWrapping(TextWrapping.NoWrap)
                    .HAlign(HorizontalAlignment.Right)
                    .VAlign(VerticalAlignment.Center)
                    .Margin(0, 0, ListTable.NumberCellEndMargin, 0)
                    .Grid(column: 2),
                ListTable.CellText(row.ModifiedText).Grid(column: 3),
                ListTable.CellText(row.Mode, Theme.SecondaryText).Grid(column: 4))
            .AutomationName($"{kind} {row.Shown}")
            .WithContextFlyout(MenuItems(menu));
    }

    /// <summary>The list item container stretches its row over the full width and adds no padding, so rows line up with the header.</summary>
    private static Style RowContainerStyle()
    {
        var style = new Style(typeof(ListViewItem));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        return style;
    }
}
