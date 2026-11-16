using System.Collections.Immutable;
using Berth.Components;
using Berth.Engine.Models;
using Berth.Presentation;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>What the Bind mounts tab is given: the last read of the container. A record: the tab renders again only when it changes.</summary>
internal sealed record BindMountsTabProps(ContainerDetails Details);

/// <summary>
/// The "Bind mounts" tab of a container's detail page: a table of the container's bind mounts, host path, container path and
/// "Read-only" or "Read-write". A host path that is on this machine opens in File Explorer.
/// </summary>
internal sealed class BindMountsTab : Component<BindMountsTabProps>
{
    private static readonly GridSize[] Columns = [GridSize.Star(3), GridSize.Star(2), GridSize.Px(120)];
    private static readonly GridSize[] OneRow = [GridSize.Auto];
    private static readonly ImmutableHashSet<string> NoPaths = ImmutableHashSet<string>.Empty;

    public override Element Render()
    {
        var details = Props.Details;
        var mounts = UseMemo(() => InspectSections.BindMounts(details), details);
        var (onThisMachine, setOnThisMachine) = UseState(NoPaths);

        // Which host paths exist here, read off the UI thread: a path on a network share can take seconds to answer.
        UseEffect(
            () =>
            {
                var cancelled = false;
                var paths = mounts.Select(m => InspectSections.WindowsPath(m.Source)).OfType<string>().ToArray();
                _ = Task.Run(() =>
                {
                    var found = paths.Where(p => Directory.Exists(p) || File.Exists(p)).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                    ReactorApp.UIDispatcher?.TryEnqueue(() =>
                    {
                        if (!cancelled)
                            setOnThisMachine(found);
                    });
                });
                return () => cancelled = true;
            },
            mounts);

        var table = UseMemo(() => mounts.Count == 0 ? null : Table(mounts, onThisMachine), mounts, onThisMachine);
        if (table is null)
            return Widgets.CenteredNote("This container has no bind mounts.").AutomationId("bind-mounts-empty").Flex(grow: 1, basis: 0);

        return table.Margin(0, 0, 0, 16).Flex(grow: 1, basis: 0);
    }

    private static Element Table(IReadOnlyList<MountDetails> mounts, ImmutableHashSet<string> onThisMachine) =>
        ScrollViewer(
                VStack(
                    0,
                    [
                        Cells(ColumnTitle("Host path"), ColumnTitle("Container path"), ColumnTitle("Access")),
                        .. mounts.Select((mount, index) => Row(mount, index, onThisMachine)),
                    ]))
            // Not sideways: the columns share the width there is, and a long path ends in an ellipsis.
            .HorizontalScrollMode(ScrollMode.Disabled)
            .Set(view => view.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled)
            .AutomationId("bind-mounts")
            .AutomationName("Bind mounts");

    /// <summary>One mount: the host path (a link that opens it in File Explorer when it is on this machine), the container path, the access.</summary>
    private static Element Row(MountDetails mount, int index, ImmutableHashSet<string> onThisMachine)
    {
        var windowsPath = InspectSections.WindowsPath(mount.Source);
        var host = windowsPath is not null && onThisMachine.Contains(windowsPath)
            ? HyperlinkButton(OneLine(Body(mount.Source)), onClick: () => Open(windowsPath))
                .AutomationId($"bind-mount-open-{index}")
                .AutomationName($"Open {windowsPath} in File Explorer")
                .ToolTip($"Open {windowsPath} in File Explorer")
                .Padding(0)
                .HAlign(HorizontalAlignment.Left)
            : (Element)OneLine(Body(mount.Source)).Foreground(Theme.PrimaryText).IsTextSelectionEnabled().ToolTip(mount.Source);

        return Cells(
            host.Margin(0, 0, 16, 0),
            OneLine(Body(mount.Destination)).Foreground(Theme.PrimaryText).IsTextSelectionEnabled().ToolTip(mount.Destination).Margin(0, 0, 16, 0),
            Body(InspectSections.Access(mount)).Foreground(Theme.PrimaryText));
    }

    private static Element Cells(Element hostPath, Element containerPath, Element access) =>
        Border(
                Grid(
                        Columns,
                        OneRow,
                        hostPath.VAlign(VerticalAlignment.Center).Grid(column: 0),
                        containerPath.VAlign(VerticalAlignment.Center).Grid(column: 1),
                        access.VAlign(VerticalAlignment.Center).Grid(column: 2))
                    .MinHeight(40))
            .BorderBrush(Theme.DividerStroke)
            .BorderThickness(0, 0, 0, 1)
            .Padding(12, 0, 12, 0);

    private static Element ColumnTitle(string text) => Caption(text).Foreground(Theme.SecondaryText);

    /// <summary>A folder opens in File Explorer; for a file, the folder it is in. Fire and forget: nothing waits on Explorer.</summary>
    private static void Open(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is not null)
            _ = Windows.System.Launcher.LaunchFolderPathAsync(folder);
    }

    private static TextBlockElement OneLine(TextBlockElement text) =>
        text.TextWrapping(TextWrapping.NoWrap).TextTrimming(TextTrimming.CharacterEllipsis);
}
