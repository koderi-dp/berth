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
using Windows.ApplicationModel.DataTransfer;
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
/// the terminal's size, a line for the shell's state, and the terminal filling the rest. The shell starts by itself when the tab is
/// shown and ends when it is left. What the tab shows and says is decided in <see cref="ExecView"/>; the bytes travel through a
/// <see cref="TerminalBridge"/> and never pass through the state.
/// </summary>
internal sealed class ExecTab : Component<ExecTabProps>
{
    private const string StartAgainLabel = "Start again";

    private static readonly string[] ShellLabels = [.. ExecCommand.Choices.Select(c => c.Label)];

    // Built once: a grid handed new definitions on every render is updated on every render.
    private static readonly GridSize[] PageColumns = [GridSize.Star()];

    // The toolbar, the state's line, the terminal, the note about dropped input.
    private static readonly GridSize[] PageRows = [GridSize.Auto, GridSize.Auto, GridSize.Star(), GridSize.Auto];

    // The shell picker, the custom command, Run, the size, and the slot for the external terminal's buttons.
    private static readonly GridSize[] ToolbarColumns = [GridSize.Auto, GridSize.Star(), GridSize.Auto, GridSize.Auto, GridSize.Auto];
    private static readonly GridSize[] ToolbarRows = [GridSize.Px(32), GridSize.Auto];

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
        link.Note = note;
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

        // A shell starts by itself once the terminal is ready, when the tab is shown on a running container.
        UseEffect(
            () =>
            {
                if (size is not null && ExecView.ShouldStartByItself(view, startedOnce))
                    StartChosen(choice, props.CustomCommand);
            },
            view.Kind,
            size is not null,
            startedOnce);

        // Each new shell starts on a cleared screen; typing is on only while it runs, and the terminal takes the focus then.
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
            ? Toolbar(view, choice, props, size, customError, OnChoose, () => StartChosen(choice, props.CustomCommand))
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

        var startAgain = () => Start(link.LastCommand is { IsDefaultOrEmpty: false } last ? last : ChosenCommand(choice, props.CustomCommand) ?? []);
        Element terminal = webViewError is not null
            ? Widgets.RetryBar("The terminal could not start: " + webViewError, null).VAlign(VerticalAlignment.Top).AutomationId("exec-webview-error")
            : Border(Component<TerminalView, TerminalViewProps>(
                    new(props.Name, colors.Background, link.OnMessage, link.OnReady, link.OnFailed, link.PageLost)))
                .Background(colors.Background)
                .BorderBrush(Theme.CardStroke)
                .BorderThickness(1)
                .CornerRadius(4)
                .Padding(8);

        var noteText = ExecInputNotes.Shown(note, exec);
        return Grid(
                PageColumns,
                PageRows,
                toolbar?.Grid(row: 0, column: 0).Margin(0, 0, 0, 8).WithKey("toolbar"),
                StatusLine(view, startAgain)?.Grid(row: 1, column: 0).Margin(0, 0, 0, 8).WithKey("status"),
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
        Action runCustom)
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
                .OnKeyDown((_, e) =>
                {
                    if (e.Key == VirtualKey.Enter)
                    {
                        e.Handled = true;
                        runCustom();
                    }
                })
                .VAlign(VerticalAlignment.Center)
                .Margin(8, 0, 0, 0)
                .Grid(row: 0, column: 1);
            run = Button("Run", runCustom)
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

        return Grid(ToolbarColumns, ToolbarRows, picker, custom, run, error, caption);
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
                return Bar(view.Text ?? "", isError: false, startAgain, "exec-ended", "exec-start-again");
            case ExecViewKind.Failed:
                return Bar(view.Text ?? "", isError: true, startAgain, "exec-failed", "exec-retry");
            default:
                return null;
        }
    }

    /// <summary>An open bar that cannot be closed, with "Start again" as its button.</summary>
    private static Element Bar(string text, bool isError, Action startAgain, string id, string buttonId)
    {
        var bar = (InfoBar(message: text) with
            {
                IsOpen = true,
                ActionButtonContent = StartAgainLabel,
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
    /// No terminal: the container is not running, paused, restarting, or the page waits for the engine. The sentence near the top,
    /// with Start or Resume where it helps; the button shows a ring while the container's action is under way.
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
            .VAlign(VerticalAlignment.Top)
            .Margin(0, 48, 0, 16);
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
    /// The terminal's colours from Presentation, over the theme's solid background and primary text; under a Windows contrast theme,
    /// over the system's window colours.
    /// </summary>
    internal static TerminalColors Colors(bool dark, bool highContrast)
    {
        if (highContrast && SystemColor("SystemColorWindowColor") is { } window && SystemColor("SystemColorWindowTextColor") is { } text)
            return TerminalTheme.For(dark, highContrast: true, Hex(window), Hex(text));

        var background = ColorOf("SolidBackgroundFillColorBaseBrush", dark, null);
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

/// <summary>
/// What the Exec tab's terminal handlers act on: the detail session the shell runs in, the terminal page, the bridge between them and
/// the latest of what the tab rendered. One per mounted tab; its handlers are the same delegates on every render. UI thread only.
/// </summary>
internal sealed class ExecLink
{
    public ExecLink(DispatcherQueue ui)
    {
        Bridge = new TerminalBridge(ui);
        OnMessage = Received;
        OnReady = Ready;
        OnFailed = message => SetWebViewError(message);
        PageLost = () => OnPageLost();
    }

    public TerminalBridge Bridge { get; }

    public Action<TerminalMessage> OnMessage { get; }

    public Action<TerminalHandle> OnReady { get; }

    public Action<string> OnFailed { get; }

    public Action PageLost { get; }

    public string ContainerId { get; set; } = "";

    public int Generation { get; set; }

    public bool AcceptsInput { get; set; }

    public TerminalColors? Colors { get; set; }

    public ExecInputNote? Note { get; set; }

    public ImmutableArray<string> LastCommand { get; set; }

    public Action<TerminalSize?> SetSize { get; set; } = _ => { };

    public Action<bool?> SetThemeDark { get; set; } = _ => { };

    public Action<string?> SetWebViewError { get; set; } = _ => { };

    public Action<ExecInputNote?> UpdateNote { get; set; } = _ => { };

    public Action OnPageLost { get; set; } = () => { };

    /// <summary>The detail session the shell runs in; null while none is open for this container.</summary>
    public ContainerDetailSession? Session { get; private set; }

    /// <summary>The terminal page, once it said ready; null before.</summary>
    public TerminalHandle? Handle { get; private set; }

    /// <summary>The terminal's fitted size; null before the page said ready.</summary>
    public TerminalSize? Size { get; private set; }

    /// <summary>Attaches the bridge to <paramref name="session"/> when it is this container's, and detaches it from the one before.</summary>
    public void Follow(ContainerDetailSession? session)
    {
        if (session is not null && session.State.ContainerId != ContainerId)
            session = null;
        if (ReferenceEquals(session, Session))
            return;
        Session?.AttachExecOutput(null);
        Session = session;
        session?.AttachExecOutput(Bridge);
    }

    /// <summary>The tab is left: the shell ends and its output goes nowhere.</summary>
    public void Leave()
    {
        if (Session is { } session)
        {
            session.StopExec();
            session.AttachExecOutput(null);
        }

        Session = null;
        if (Handle is { } handle)
            Bridge.Disconnect(handle);
        Handle = null;
    }

    /// <summary>The terminal page is off the screen: its handle is dropped and its size forgotten until the next one is ready.</summary>
    public void TerminalGone()
    {
        if (Handle is { } handle)
            Bridge.Disconnect(handle);
        Handle = null;
        Size = null;
        SetSize(null);
    }

    private void Ready(TerminalHandle handle)
    {
        if (Handle is { } previous && previous != handle)
            Bridge.Disconnect(previous);
        Handle = handle;
        Bridge.Connect(handle);
        SetThemeDark(handle.IsDark);
        handle.ThemeChanged = dark => SetThemeDark(dark);
        if (Colors is { } colors)
            handle.Post(TerminalProtocol.Theme(colors));
        handle.Post(TerminalProtocol.Enabled(AcceptsInput));
        if (AcceptsInput)
            handle.Focus();
    }

    private void Received(TerminalMessage message)
    {
        switch (message)
        {
            case TerminalMessage.Ready ready:
                SetTerminalSize(ready.Size);
                break;
            case TerminalMessage.Resize resize:
                SetTerminalSize(resize.Size);
                Session?.ResizeExec(resize.Size);
                break;
            case TerminalMessage.Input input when Session is { } session:
                var outcome = session.SendExecInput(Generation, input.Bytes);
                var next = ExecInputNotes.AfterInput(Note, outcome, Generation);
                if (next != Note)
                {
                    Note = next;
                    UpdateNote(next);
                }

                break;
            case TerminalMessage.Ack ack:
                Bridge.Acked(ack.Bytes);
                break;
            case TerminalMessage.Copy copy:
                ContainerParts.CopyToClipboard(copy.Text);
                break;
            case TerminalMessage.Paste:
                _ = PasteAsync();
                break;
            case TerminalMessage.Leave:
                FocusTabStrip();
                break;
        }
    }

    private void SetTerminalSize(TerminalSize size)
    {
        Size = size;
        SetSize(size);
    }

    /// <summary>The clipboard's text goes to the page, which pastes it as one piece; nothing when it holds no text or too much.</summary>
    private async Task PasteAsync()
    {
        string text;
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return;
            text = await content.GetTextAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("reading the clipboard failed", ex);
            return;
        }

        if (text.Length == 0 || Handle is not { } handle)
            return;
        if (!ExecInputNotes.PasteFits(text))
        {
            Note = ExecInputNotes.PasteTooLarge(Generation);
            UpdateNote(Note);
            return;
        }

        handle.Post(TerminalProtocol.PasteText(text));
    }

    /// <summary>F6 in the terminal: the keyboard focus goes to the tab strip's selected tab.</summary>
    private void FocusTabStrip()
    {
        if (Handle?.XamlRoot?.Content is not DependencyObject root || Find(root, "detail-tabs") is not SelectorBar bar)
            return;
        var target = (Control?)bar.SelectedItem ?? bar;
        target.Focus(FocusState.Keyboard);
    }

    private static DependencyObject? Find(DependencyObject node, string automationId)
    {
        if (AutomationProperties.GetAutomationId(node) == automationId)
            return node;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (Find(VisualTreeHelper.GetChild(node, i), automationId) is { } found)
                return found;
        }

        return null;
    }
}
