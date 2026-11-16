using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using Berth.Connections;
using Berth.Text;
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
        OpenExternal = StartExternal;
        CopyCommand = () =>
        {
            if (External?.CopyText is { } text)
                ContainerParts.CopyToClipboard(text);
        };
    }

    /// <summary>"Open in external terminal": starts what <see cref="External"/> plans.</summary>
    public Action OpenExternal { get; }

    /// <summary>"Copy command": the <c>docker exec</c> command for PowerShell.</summary>
    public Action CopyCommand { get; }

    /// <summary>The external terminal's command for the picker's current shell, as last rendered.</summary>
    public ExternalCommand? External { get; set; }

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

    /// <summary>The tab's own "a shell was started since it was shown", as last rendered, and its setter.</summary>
    public bool StartedOnce { get; set; }

    public Action<bool> SetStartedOnce { get; set; } = _ => { };

    // Whether the session followed before the current one had lost its connection when it went.
    private bool _previousLost;

    /// <summary>The detail session the shell runs in; null while none is open for this container.</summary>
    public ContainerDetailSession? Session { get; private set; }

    /// <summary>The terminal page, once it said ready; null before.</summary>
    public TerminalHandle? Handle { get; private set; }

    /// <summary>The terminal's fitted size; null before the page said ready.</summary>
    public TerminalSize? Size { get; private set; }

    /// <summary>
    /// Attaches the bridge to <paramref name="session"/> when it is this container's, and detaches it from the one before. A new
    /// session numbers its shells from 1 again, so the bridge starts over; and unless the old one lost its connection, the tab
    /// starts a shell by itself again (<see cref="ExecView.StartedOnceAfterNewSession"/>).
    /// </summary>
    public void Follow(ContainerDetailSession? session)
    {
        if (session is not null && session.State.ContainerId != ContainerId)
            session = null;
        if (ReferenceEquals(session, Session))
            return;
        if (Session is { } previous)
        {
            previous.AttachExecOutput(null);
            _previousLost = previous.State.ConnectionLost;
        }

        Session = session;
        if (session is null)
            return;
        var output = Bridge.NewSession();
        var startedOnce = ExecView.StartedOnceAfterNewSession(StartedOnce, _previousLost);
        if (startedOnce != StartedOnce)
            SetStartedOnce(startedOnce);
        session.AttachExecOutput(output);
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

    /// <summary>
    /// Starts the planned program; when Windows Terminal is not there after all (<see cref="Win32Exception"/>), the console instead.
    /// A start that fails shows its message in the error bar. The command line is never logged.
    /// </summary>
    private void StartExternal()
    {
        if (External?.Plan is not { First: { } first } plan)
            return;
        try
        {
            Start(first);
        }
        catch (Win32Exception ex) when (plan.Fallback is { } fallback)
        {
            AppLog.Error("starting Windows Terminal failed", ex);
            try
            {
                Start(fallback);
            }
            catch (Exception again)
            {
                Failed(again);
            }
        }
        catch (Exception ex)
        {
            Failed(ex);
        }

        static void Failed(Exception ex)
        {
            AppLog.Error("opening an external terminal failed", ex);
            AppServices.Current.Session.Dispatch(new ExternalTerminalFailed(ex.Message));
        }
    }

    private static void Start(ProcessLaunch launch)
    {
        var info = new ProcessStartInfo(launch.FileName) { UseShellExecute = launch.UseShellExecute };
        foreach (var argument in launch.Arguments)
            info.ArgumentList.Add(argument);
        foreach (var name in launch.ClearedVariables)
            info.Environment.Remove(name);
        using var started = Process.Start(info);
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

/// <summary>Whether <c>docker.exe</c> and <c>wt.exe</c> are on <c>PATH</c>.</summary>
internal sealed record ExternalTools(bool Docker, bool WindowsTerminal)
{
    /// <summary>Looks both up in this process's <c>PATH</c>; touches the disk, so it runs off the UI thread.</summary>
    public static ExternalTools Find()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        return new(
            ExternalTerminal.FindOnPath("docker.exe", path, File.Exists) is not null,
            ExternalTerminal.FindOnPath("wt.exe", path, File.Exists) is not null);
    }
}

/// <summary>
/// The external terminal's command for one shell: the text "Copy command" copies (null when there is none for the connection), what
/// "Open in external terminal" starts (null until the tools were looked up), and why it is not offered.
/// </summary>
internal sealed record ExternalCommand(string? CopyText, ExternalLaunchPlan? Plan, string? Refusal)
{
    public static ExternalCommand For(ConnectionProfile connection, string containerId, string name, ImmutableArray<string> command, ExternalTools? tools)
    {
        if (DockerExecCommand.Unavailable(connection) is { } why)
            return new(null, null, why);

        var arguments = DockerExecCommand.Arguments(connection, containerId, command);
        var text = DockerExecCommand.Text(connection, containerId, command, ShellKind.PowerShell);
        var plan = tools is null ? null : ExternalTerminal.Plan(name.Length > 0 ? name : containerId, arguments, tools.Docker, tools.WindowsTerminal);
        return new(text, plan, plan?.Refusal);
    }
}
