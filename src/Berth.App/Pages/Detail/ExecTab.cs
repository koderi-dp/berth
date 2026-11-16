using System.Collections.Immutable;
using System.Globalization;
using Berth.Components;
using Berth.Engine.Models;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Detail;

/// <summary>What the Exec tab shows.</summary>
/// <param name="ContainerId">The container the shell runs in.</param>
/// <param name="Name">The container's name, for the terminal's accessible name.</param>
/// <param name="Detail">
/// The page's detail state, kept by the page as long as the container's details, its shell and whether it was removed are the same:
/// a batch of log lines does not render the tab again.
/// </param>
/// <param name="Waiting">The page waits for the engine (<see cref="DetailPage.Waiting"/>).</param>
/// <param name="Connected">The engine is connected: the Start and Resume buttons act.</param>
/// <param name="ActionPending">An action of the container's row is under way: the Start and Resume buttons show a ring.</param>
/// <param name="ShellChoice">The chosen entry of <see cref="ExecCommand.Choices"/>, kept by the page.</param>
/// <param name="CustomCommand">The custom command's text, kept by the page.</param>
internal sealed record ExecTabProps(
    string ContainerId,
    string Name,
    DetailState Detail,
    bool Waiting,
    bool Connected,
    bool ActionPending,
    int ShellChoice,
    string CustomCommand,
    Action<int> SetShellChoice,
    Action<string> SetCustomCommand);

/// <summary>
/// The "Exec" tab of a container's detail page: a terminal connected to a shell in the container. A toolbar with the shell picker and
/// the terminal's size, a line for the shell's state, and the terminal filling the rest. A fixed shell (sh, bash, ash) starts by
/// itself when the tab is shown; a custom command runs only when the user runs it. The shell ends when the tab is left. What the tab shows and says is decided in <see cref="ExecView"/>; the bytes travel through a
/// <see cref="TerminalBridge"/> and never pass through the state.
/// </summary>
internal sealed class ExecTab : Component<ExecTabProps>
{
    private static readonly string[] ShellLabels = [.. ExecCommand.Choices.Select(c => c.Label)];

    // The picker's entry for sh, chosen by "Start sh".
    private static readonly int ShIndex = ExecCommand.Choices.ToList().FindIndex(c => !c.IsCustom && c.Command.SequenceEqual(ExecView.ShCommand));

    // Built once: a grid handed new definitions on every render is updated on every render.
    private static readonly GridSize[] PageColumns = [GridSize.Star()];

    // The toolbar, the state's line, the terminal, the note about dropped input.
    private static readonly GridSize[] PageRows = [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto];

    // The shell picker, the custom command, Run, the size, and the external terminal's buttons.
    private static readonly GridSize[] ToolbarColumns = [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto];

    // The controls, the custom command's error, and why the external terminal is not offered.
    private static readonly GridSize[] ToolbarRows = [GridSize.Px(32), GridSize.Auto, GridSize.Auto];

    /// <summary>Below this window width the external terminal's button shows its glyph alone.</summary>
    private const double ExternalTextMinWidth = 760;

    private const string ExternalLabel = "Open in external terminal";
    private const string CopyLabel = "Copy the docker exec command (PowerShell)";

    public override Element Render()
    {
        var props = Props;
        var startupDark = UseIsDarkTheme();
        var highContrast = UseHighContrast();
        var (themeDark, setThemeDark) = UseState<bool?>(null);
        var (startedOnce, setStartedOnce) = UseState(false);
        var (terminalStopped, setTerminalStopped) = UseState(false);
        var (size, setSize) = UseState<TerminalSize?>(null);
        var (webViewError, setWebViewError) = UseState<string?>(null);
        var (note, setNote) = UseState<ExecInputNote?>(null);
        var (customError, setCustomError) = UseState<string?>(null);
        var link = UseMemo(() => new ExecLink(DispatcherQueue.GetForCurrentThread()), Array.Empty<object>());
        var (tools, setTools) = UseState<ExternalTools?>(null);
        var wide = UseBreakpoint(ExternalTextMinWidth);

        var dark = themeDark ?? startupDark;
        var colors = UseMemo(() => Colors(dark, highContrast), dark, highContrast);
        var exec = props.Detail.Exec;
        var view = ExecView.For(props.Detail, props.Waiting, startedOnce, terminalStopped);
        var choice = ExecCommand.Choices[Math.Clamp(props.ShellChoice, 0, ExecCommand.Choices.Count - 1)];

        // What the terminal's handlers read when they run: the latest of each.
        link.ContainerId = props.ContainerId;
        link.Generation = exec.Generation;
        link.AcceptsInput = view.AcceptsInput;
        link.Colors = colors;
        link.SetSize = setSize;
        link.SetThemeDark = setThemeDark;
        link.SetWebViewError = setWebViewError;
        link.UpdateNote = setNote;
        link.StartedOnce = startedOnce;
        link.SetStartedOnce = setStartedOnce;
        link.Note = note;
        var connection = AppServices.Current.ActiveConnectionProfile;
        var externalCommand = ExternalCommandOf(choice, props.CustomCommand);
        var external = UseMemo(
            () => ExternalCommand.For(connection, props.ContainerId, props.Name, externalCommand, tools),
            connection,
            props.ContainerId,
            props.Name,
            string.Join('\0', externalCommand),
            (object?)tools ?? "not looked up");
        link.External = external;
        link.OnPageLost = () =>
        {
            link.Session?.StopExec();
            setTerminalStopped(true);
        };

        void Start(ImmutableArray<string> command)
        {
            if (link.Size is not { } at || link.Session is not { } session)
                return;
            setTerminalStopped(false);
            setStartedOnce(true);
            setNote(null);
            link.LastCommand = command;
            if (session.State.Exec.IsActive)
                session.StopExec();
            session.StartExec(command, at);
        }

        ImmutableArray<string>? ChosenCommand(ShellChoice shell, string custom)
        {
            if (!shell.IsCustom)
                return shell.Command;
            var parsed = ExecCommand.Parse(custom);
            setCustomError(parsed.Error);
            return parsed.Error is null ? parsed.Command : null;
        }

        void StartChosen(ShellChoice shell, string custom)
        {
            if (ChosenCommand(shell, custom) is { } command)
                Start(command);
        }

        // The shell follows the page's detail session: attached now, and again to each new one (after a reconnect); detached and
        // ended when the tab is left.
        UseEffect(
            () =>
            {
                void Follow() => link.Follow(AppServices.Current.Detail);
                AppServices.Current.DetailChanged += Follow;
                Follow();
                return () =>
                {
                    AppServices.Current.DetailChanged -= Follow;
                    link.Leave();
                };
            },
            Array.Empty<object>());

        // A fixed shell starts by itself once the terminal is ready, when the tab is shown on a running container; a custom command
        // waits for Run.
        UseEffect(
            () =>
            {
                if (size is null)
                    return;
                var custom = choice.IsCustom;
#if DEBUG
                // BERTH_EXEC_COMMAND: the tab it opened runs that command once, as if Run had been pressed.
                if (custom && ExecView.ShouldStartByItself(view, startedOnce, custom: false) && DebugStart.TakeExecCommandRun())
                    custom = false;
#endif
                if (ExecView.ShouldStartByItself(view, startedOnce, custom))
                    StartChosen(choice, props.CustomCommand);
            },
            view.Kind,
            size is not null,
            startedOnce);

        // Each new shell starts on a cleared screen; typing is on from Starting (so replies to its first queries are not lost) while
        // a shell starts or runs, and the terminal takes the focus once it runs.
        UseEffect(
            () =>
            {
                if (view.Kind == ExecViewKind.Starting)
                    link.Bridge.Begin(exec.Generation);
                link.Handle?.Post(TerminalProtocol.Enabled(view.AcceptsInput));
                if (view.Kind == ExecViewKind.Running)
                    link.Handle?.Focus();
            },
            exec.Generation,
            view.Kind);

        UseEffect(() => link.Handle?.Post(TerminalProtocol.Theme(colors)), colors);

        // Whether docker and Windows Terminal are installed: looked up once, off the UI thread.
        UseEffect(
            () =>
            {
                var ui = DispatcherQueue.GetForCurrentThread();
                var mounted = true;
                _ = Task.Run(ExternalTools.Find).ContinueWith(
                    found => ui.TryEnqueue(() =>
                    {
                        if (mounted)
                            setTools(found.Result);
                    }),
                    TaskContinuationOptions.OnlyOnRanToCompletion);
                return () => mounted = false;
            },
            Array.Empty<object>());

        // A terminal taken off the screen (the container stopped, the page waits) is gone: the next one starts a shell only once it
        // has said ready, so nothing is sent to the closed one.
        var terminalShown = view.ShowTerminal && webViewError is null;
        UseEffect(
            () =>
            {
                if (!terminalShown)
                    link.TerminalGone();
            },
            terminalShown);

        var toolbar = view.ShowTerminal || webViewError is not null
            ? Toolbar(view, choice, props, size, customError, OnChoose, text => StartChosen(choice, text ?? props.CustomCommand), external, wide, link)
            : null;

        void OnChoose(int index)
        {
            props.SetShellChoice(index);
            setCustomError(null);
            var shell = ExecCommand.Choices[index];
            if (!shell.IsCustom && view.ShowTerminal)
                Start(shell.Command);
        }

        // Starting or resuming the container from here opens a terminal once it runs: how the last shell ended is cleared, and the
        // tab starts a shell by itself again.
        void BeforeContainerAction()
        {
            link.Session?.StopExec();
            setStartedOnce(false);
            setTerminalStopped(false);
        }

        if (!view.ShowTerminal)
            return Placeholder(view, props, BeforeContainerAction);

        void StartAgain()
        {
            if (view.Button == ExecButton.StartSh)
            {
                // The shell was not in the image: sh is chosen in the picker too, so the next start is sh as well.
                if (ShIndex >= 0)
                    props.SetShellChoice(ShIndex);
                Start(ExecView.ShCommand);
                return;
            }

            Start(link.LastCommand is { IsDefaultOrEmpty: false } last ? last : ChosenCommand(choice, props.CustomCommand) ?? []);
        }

        Element terminal = webViewError is not null
            ? Widgets.RetryBar("The terminal could not start: " + webViewError, null).VAlign(VerticalAlignment.Top).AutomationId("exec-webview-error")
            : Border(Component<TerminalView, TerminalViewProps>(
                    new(props.Name, colors.Background, link.OnMessage, link.OnReady, link.OnFailed, link.PageLost)))
                .Background(colors.Background)
                .BorderBrush(Theme.CardStroke)
                .BorderThickness(1)
                .CornerRadius(8)
                .Padding(8);

        var noteText = ExecInputNotes.Shown(note, exec);
        return Grid(
                PageColumns,
                PageRows,
                toolbar?.Grid(row: 0, column: 0).Margin(0, 0, 0, 8).WithKey("toolbar"),
                StatusLine(view, StartAgain)?.Grid(row: 1, column: 0).Margin(0, 0, 0, 8).WithKey("status"),
                terminal.Grid(row: 2, column: 0).WithKey("terminal"),
                noteText is null
                    ? null
                    : (InfoBar(message: noteText) with { IsOpen = true })
                        .Informational()
                        .IsClosable()
                        .Closed(() => setNote(null))
                        .AutomationId("exec-input-note")
                        .Grid(row: 3, column: 0)
                        .Margin(0, 8, 0, 0)
                        .WithKey("note"))
            .Margin(0, 0, 0, 16);
    }

    /// <summary>
    /// The row above the terminal: the shell picker (disabled while a shell starts), the custom command with Run when Custom is
    /// chosen, and the terminal's size. The external terminal's buttons go in the last column.
    /// </summary>
    private static Element Toolbar(
        ExecViewModel view,
        ShellChoice choice,
        ExecTabProps props,
        TerminalSize? size,
        string? customError,
        Action<int> choose,
        Action<string?> runCustom,
        ExternalCommand external,
        bool wide,
        ExecLink link)
    {
        var picker = ComboBox(ShellLabels, props.ShellChoice, choose)
            .Width(192)
            .IsEnabled(view.Kind != ExecViewKind.Starting)
            .AutomationName("Shell")
            .ToolTip("Shell")
            .AutomationId("exec-shell")
            .VAlign(VerticalAlignment.Center)
            .Grid(row: 0, column: 0);

        Element? custom = null;
        Element? run = null;
        Element? error = null;
        if (choice.IsCustom)
        {
            custom = Widgets.TextField(props.CustomCommand, props.SetCustomCommand, "/bin/zsh -l")
                .AutomationName("Custom command")
                .AutomationId("exec-custom")
                .OnKeyDown((sender, e) =>
                {
                    if (e.Key == VirtualKey.Enter)
                    {
                        // The box's own text: the last change may not have reached the page's state yet.
                        e.Handled = true;
                        runCustom((sender as TextBox)?.Text);
                    }
                })
                .VAlign(VerticalAlignment.Center)
                .Margin(8, 0, 0, 0)
                .Grid(row: 0, column: 1);
            run = Button("Run", () => runCustom(null))
                .AutomationId("exec-run")
                .IsEnabled(view.Kind != ExecViewKind.Starting)
                .VAlign(VerticalAlignment.Center)
                .Margin(8, 0, 0, 0)
                .Grid(row: 0, column: 2);
            if (customError is not null)
            {
                error = Caption(customError)
                    .Foreground(Theme.SystemCritical)
                    .AutomationId("exec-custom-error")
                    .Margin(8, 4, 0, 0)
                    .Grid(row: 1, column: 1, columnSpan: 2);
            }
        }

        var caption = size is { } s
            ? Caption(ExecView.SizeText(s))
                .Foreground(Theme.SecondaryText)
                .AutomationName(ExecView.SizeName(s))
                .AutomationId("exec-size")
                .VAlign(VerticalAlignment.Center)
                .Margin(16, 0, 0, 0)
                .Grid(row: 0, column: 3)
            : null;

        var why = external.Refusal;
        var outside = HStack(
                4,
                ExternalButton(wide, why is null && external.Plan?.First is not null, link.OpenExternal),
                Widgets.IconButton(Glyphs.Copy, CopyLabel, link.CopyCommand)
                    .AutomationId("exec-copy-command")
                    .IsEnabled(external.CopyText is not null))
            .VAlign(VerticalAlignment.Center)
            .Margin(8, 0, 0, 0)
            .Grid(row: 0, column: 4);
        var refusal = why is null
            ? null
            : Caption(why)
                .Foreground(Theme.SecondaryText)
                .TextWrapping(TextWrapping.Wrap)
                .HAlign(HorizontalAlignment.Right)
                .AutomationId("exec-external-refusal")
                .Margin(0, 4, 0, 0)
                .Grid(row: 2, column: 0, columnSpan: 5);

        return Grid(ToolbarColumns, ToolbarRows, picker, custom, run, error, caption, outside, refusal);
    }

    /// <summary>"Open in external terminal": its glyph and text, or below <see cref="ExternalTextMinWidth"/> its glyph alone.</summary>
    private static Element ExternalButton(bool wide, bool enabled, Action open)
    {
        Element content = wide
            ? HStack(8, Widgets.Glyph(Glyphs.OpenInNewWindow, 16), TextBlock(ExternalLabel).VAlign(VerticalAlignment.Center))
            : Widgets.Glyph(Glyphs.OpenInNewWindow, 16);
        return Button(content, open)
            .SubtleButton()
            .AutomationName(ExternalLabel)
            .ToolTip(ExternalLabel)
            .AutomationId("exec-external")
            .IsEnabled(enabled);
    }

    /// <summary>The command the external terminal runs: the picker's current one, sh while a custom command does not parse.</summary>
    private static ImmutableArray<string> ExternalCommandOf(ShellChoice choice, string custom)
    {
        if (!choice.IsCustom)
            return choice.Command;
        var parsed = ExecCommand.Parse(custom);
        return parsed.Error is null ? parsed.Command : ExecView.ShCommand;
    }

    /// <summary>
    /// The line between the toolbar and the terminal: a ring while the shell starts, an informational bar with Start again once it
    /// ended or its connection was lost, an error bar with the engine's words when it failed; nothing while it is ready or running.
    /// </summary>
    private static Element? StatusLine(ExecViewModel view, Action startAgain)
    {
        switch (view.Kind)
        {
            case ExecViewKind.Starting:
                return HStack(
                        8,
                        ProgressRing().Size(16, 16).AutomationName("Starting"),
                        Body(view.Text ?? "").Foreground(Theme.SecondaryText).VAlign(VerticalAlignment.Center))
                    .AutomationId("exec-status")
                    .Height(32);
            case ExecViewKind.Ended or ExecViewKind.Lost:
                return Bar(view, isError: false, startAgain, "exec-ended", "exec-start-again");
            case ExecViewKind.Failed:
                return Bar(view, isError: true, startAgain, "exec-failed", "exec-retry");
            default:
                return null;
        }
    }

    /// <summary>An open bar that cannot be closed, with "Start again" (or "Start sh") as its button.</summary>
    private static Element Bar(ExecViewModel view, bool isError, Action startAgain, string id, string buttonId)
    {
        var bar = (InfoBar(message: view.Text ?? "") with
            {
                IsOpen = true,
                ActionButtonContent = ExecView.ButtonLabel(view.Button),
                OnActionButtonClick = startAgain,
            })
            .IsClosable(false)
            .Set(b =>
            {
                if (b.ActionButton is { } button)
                    AutomationProperties.SetAutomationId(button, buttonId);
            })
            .AutomationId(id);
        return isError ? bar.Error() : bar.Informational();
    }

    /// <summary>
    /// No terminal: the container is not running, paused, restarting, or the page waits for the engine. The sentence in the middle of
    /// the tab, as the Stats tab's, with Start or Resume under it where it helps; the button shows a ring while the container's
    /// action is under way.
    /// </summary>
    private static Element Placeholder(ExecViewModel view, ExecTabProps props, Action beforeAction)
    {
        var id = view.Kind switch
        {
            ExecViewKind.NotRunning => "exec-not-running",
            ExecViewKind.Paused => "exec-paused",
            ExecViewKind.Restarting => "exec-restarting",
            _ => "exec-waiting",
        };
        var text = Body(view.Text ?? "").Foreground(Theme.SecondaryText).HAlign(HorizontalAlignment.Center).AutomationId(id);

        Element? button = view.Button switch
        {
            ExecButton.StartContainer => ActionButton("Start", "exec-start-container", ContainerAction.Start, props, beforeAction),
            ExecButton.ResumeContainer => ActionButton("Resume", "exec-resume-container", ContainerAction.Unpause, props, beforeAction),
            _ => null,
        };

        return VStack(12, text, button?.HAlign(HorizontalAlignment.Center))
            .HAlign(HorizontalAlignment.Center)
            .VAlign(VerticalAlignment.Center)
            .Margin(0, 0, 0, 16);
    }

    private static Element ActionButton(string label, string id, ContainerAction action, ExecTabProps props, Action beforeAction)
    {
        Element content = props.ActionPending
            ? HStack(8, ProgressRing().Size(16, 16), TextBlock(label))
            : TextBlock(label);
        var containerId = props.ContainerId;
        return Button(content, () =>
            {
                beforeAction();
                AppServices.Current.Session.Dispatch(new ActionRequested([containerId], action));
            })
            .AccentButton()
            .IsEnabled(props.Connected && !props.ActionPending)
            .AutomationName(label)
            .AutomationId(id);
    }

    /// <summary>
    /// The terminal's colours from Presentation, over the card fill the app's other cards use (blended over the theme's solid
    /// background, so xterm and the area around it are one solid colour; the area has the Stats cards' edge and corners) and primary
    /// text; under a Windows contrast theme, over the
    /// system's window colours.
    /// </summary>
    internal static TerminalColors Colors(bool dark, bool highContrast)
    {
        if (highContrast && SystemColor("SystemColorWindowColor") is { } window && SystemColor("SystemColorWindowTextColor") is { } text)
            return TerminalTheme.For(dark, highContrast: true, Hex(window), Hex(text));

        var background = ColorOf("CardBackgroundFillColorDefaultBrush", dark, ColorOf("SolidBackgroundFillColorBaseBrush", dark, null));
        var foreground = ColorOf("TextFillColorPrimaryBrush", dark, background);
        return TerminalTheme.For(dark, highContrast: false, Hex(background), Hex(foreground));
    }

    private static Windows.UI.Color? SystemColor(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Windows.UI.Color color ? color : null;

    /// <summary>A theme brush's colour, its transparency blended over <paramref name="under"/> when given.</summary>
    private static Windows.UI.Color ColorOf(string key, bool dark, Windows.UI.Color? under)
    {
        var color = ThemeRef.Resolve(key, dark) is SolidColorBrush brush ? brush.Color : Windows.UI.Color.FromArgb(0xff, 0x80, 0x80, 0x80);
        if (under is not { } back || color.A == 0xff)
            return color;
        var a = color.A / 255.0;
        return Windows.UI.Color.FromArgb(0xff, Mix(color.R, back.R), Mix(color.G, back.G), Mix(color.B, back.B));

        byte Mix(byte top, byte bottom) => (byte)Math.Round((top * a) + (bottom * (1 - a)));
    }

    private static string Hex(Windows.UI.Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}");
}
