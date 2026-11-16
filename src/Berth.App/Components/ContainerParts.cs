using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Berth.Text;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using static Microsoft.UI.Reactor.Factories;
using MenuEntry = Microsoft.UI.Reactor.Core.MenuFlyoutItemBase;

namespace Berth.Components;

/// <summary>
/// What a row of the containers table and the header of a container's detail page draw alike: the action buttons and menus built
/// from <see cref="RowActions"/>, the state mark, port links, and the actions that stay on this machine (copy, open in browser).
/// </summary>
internal static class ContainerParts
{
    /// <summary>
    /// The buttons: the actions that are buttons at this width (the main one in the accent colour), the "more" menu with the rest
    /// when there is any, a divider and Delete. AutomationIds are the action and <paramref name="name"/>: <c>stop-web-1</c>,
    /// <c>more-web-1</c>.
    /// </summary>
    public static Element ActionButtons(RowActions actions, bool compact, string name, Action<RowAction> run)
    {
        var buttons = actions.Buttons(compact)
            .Select(a => Widgets.IconButton(Glyph(a.Kind), a.Label, () => run(a), color: a.IsMain ? Primary(a.IsEnabled) : null)
                .AutomationId(AutomationPrefix(a.Kind) + name)
                .IsEnabled(a.IsEnabled));

        var menu = actions.MoreMenu(compact);
        var more = menu.Count == 0
            ? null
            : Widgets.IconButton(Glyphs.More, "More actions", null)
                .AutomationId("more-" + name)
                .WithFlyout(MenuItems(MenuEntries(menu, run)))
                .IsEnabled(menu.Any(s => s.Any(a => a.IsEnabled)));

        var delete = actions.Delete;
        var deleteButton = Widgets.IconButton(Glyphs.Delete, delete.Label, () => run(delete), color: delete.IsEnabled ? Theme.SystemCritical : null)
            .AutomationId("delete-" + name)
            .IsEnabled(delete.IsEnabled);

        return (FlexRow([.. buttons, more, ActionDivider(), deleteButton]) with
            {
                AlignItems = FlexAlign.Center,
                JustifyContent = FlexJustify.FlexEnd,
                ColumnGap = 4,
            })
            .VAlign(VerticalAlignment.Center);
    }

    /// <summary>The right-click menu: every action in order, its sections apart.</summary>
    public static Element ContextMenu(RowActions actions, Action<RowAction> run) =>
        MenuItems(MenuEntries(actions.Sections, run));

    /// <summary>
    /// Carries out the actions that stay on this machine for <paramref name="row"/>: opening the first published port, copying its
    /// name or ID, and copying its <c>docker run</c> command: from <paramref name="details"/> when the container has been read (its
    /// detail page), otherwise the engine session reads it first and hands the command over (<see cref="RunCommandRequested"/>; a
    /// failure shows in the error bar). Any other action goes to <paramref name="other"/>.
    /// </summary>
    public static void Run(RowAction action, ContainerRow row, RowActions actions, Action<RowAction> other, ContainerDetails? details = null)
    {
        if (action.RunCommandShell is { } shell)
        {
            if (details is not null)
                CopyToClipboard(DockerRunCommand.Build(details, shell));
            else
                AppServices.Current.Session.Dispatch(new RunCommandRequested(row.Id, shell));
            return;
        }

        switch (action.Kind)
        {
            case RowActionKind.OpenInBrowser when actions.BrowserUrl is { } url:
                OpenInBrowser(url);
                break;
            case RowActionKind.CopyName:
                CopyToClipboard(row.Name);
                break;
            case RowActionKind.CopyId:
                CopyToClipboard(row.Id);
                break;
            default:
                other(action);
                break;
        }
    }

    /// <summary>Running: the success colour; paused or restarting: caution; stopped: secondary text.</summary>
    public static ThemeRef StateColor(RowState state) => state switch
    {
        RowState.Running => Theme.SystemSuccess,
        RowState.Paused or RowState.Restarting => Theme.SystemCaution,
        _ => Theme.SecondaryText,
    };

    /// <summary>The state as a shape in the state's colour (so it does not rest on colour alone), or a ring while an action runs.</summary>
    public static Element StateMark(StateShape shape, ThemeRef color, string label, bool busy) =>
        busy
            ? ProgressRing().Size(16, 16).AutomationName("Working").HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center)
            : Widgets.StateMark(shape, color).AutomationName(label).ToolTip(label).HAlign(HorizontalAlignment.Left).VAlign(VerticalAlignment.Center);

    /// <summary>A port: a link that opens it when it has a URL, else its text.</summary>
    public static Element PortLink(PortLink port) =>
        port.Url is { } url
            ? HyperlinkButton(port.Text, url).Padding(4, 2).ToolTip(url.ToString())
            : Widgets.OneLine(Body(port.Text));

    /// <summary>The first <paramref name="shown"/> ports (see <see cref="PortLink(Presentation.PortLink)"/>), then "+n" for the rest, listed in its tooltip.</summary>
    public static FlexElement PortList(IReadOnlyList<PortLink> ports, int shown)
    {
        var rest = ports.Count > shown
            ? Caption("+" + (ports.Count - shown)).Foreground(Theme.SecondaryText).ToolTip(string.Join(", ", ports.Skip(shown).Select(p => p.Text)))
            : null;

        return FlexRow([.. ports.Take(shown).Select(PortLink), rest]) with
        {
            AlignItems = FlexAlign.Center,
            ColumnGap = 4,
        };
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard. When that fails (another program holds the clipboard) the failure is logged by its
    /// exception type, never with the text, and the error bar says so.
    /// </summary>
    public static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            AppLog.Error("copying to the clipboard failed", ex);
            AppServices.Current.Session.Dispatch(new ClipboardCopyFailed());
        }
    }

    private static MenuEntry[] MenuEntries(IReadOnlyList<IReadOnlyList<RowAction>> sections, Action<RowAction> run) =>
    [
        .. sections.SelectMany((section, i) => (MenuEntry[])
        [
            .. i > 0 ? (MenuEntry[])[MenuSeparator()] : [],
            .. section.Select(a => MenuItem(a.Label, () => run(a)) with
            {
                IconElement = Widgets.MenuGlyph(Glyph(a.Kind)),
                IsEnabled = a.IsEnabled,
                Description = a.Description,
            }),
        ]),
    ];

    /// <summary>The glyph of an action, the same on its button and in the menus (the solid Stop: the outline one looks like a checkbox).</summary>
    private static string Glyph(RowActionKind kind) => kind switch
    {
        RowActionKind.ViewDetails => Glyphs.Details,
        RowActionKind.Start => Glyphs.Play,
        RowActionKind.Stop => Glyphs.Stop,
        RowActionKind.Restart => Glyphs.Restart,
        RowActionKind.Pause => Glyphs.Pause,
        RowActionKind.Resume => Glyphs.Resume,
        RowActionKind.Delete => Glyphs.Delete,
        RowActionKind.OpenInBrowser => Glyphs.Globe,
        RowActionKind.CopyRunPowerShell or RowActionKind.CopyRunBash => Glyphs.CommandPrompt,
        RowActionKind.Run => Glyphs.Play,
        RowActionKind.PullAgain => Glyphs.Download,
        RowActionKind.Inspect => Glyphs.Code,
        _ => Glyphs.Copy,
    };

    private static string AutomationPrefix(RowActionKind kind) => kind.ToString().ToLowerInvariant() + "-";

    /// <summary>The accent colour for the main action while it can act; none while it cannot, so the disabled look shows.</summary>
    private static ThemeRef? Primary(bool canAct) => canAct ? Theme.AccentText : null;

    /// <summary>Sets Delete, in red, apart from the actions that lose nothing.</summary>
    private static Element ActionDivider() =>
        Border(Empty()).Background(Theme.DividerStroke).Size(1, 20).Margin(4, 0, 4, 0).Flex(shrink: 0);

    /// <summary>Opens a published port in the default browser. Fire and forget: nothing waits on the browser.</summary>
    private static void OpenInBrowser(Uri url) => _ = Windows.System.Launcher.LaunchUriAsync(url);
}
