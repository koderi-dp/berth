using Berth.Components;
using Berth.Connections;
using Berth.Engine;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>What the connection editor opens with and reports back.</summary>
/// <param name="Editing">The connection to edit, or null to add one.</param>
/// <param name="OnSave">Called with the connection to save when the user chooses Save.</param>
/// <param name="OnDone">Called when the dialog has closed, saved or not.</param>
internal sealed record ConnectionEditorProps(ConnectionProfile? Editing, Action<ConnectionProfile> OnSave, Action OnDone);

/// <summary>
/// The dialog that adds or edits a connection: name, address, TLS files and a test of the connection. It opens when it is mounted;
/// the page mounts it for one edit and removes it when <see cref="ConnectionEditorProps.OnDone"/> is called. The rules live in
/// <see cref="ConnectionDraft"/>; this is the view over it. Its text boxes are uncontrolled (<see cref="Widgets.TextField"/>): each
/// starts from the draft when it is mounted and reports every change to it.
/// </summary>
internal sealed class ConnectionEditor : Component<ConnectionEditorProps>
{
    /// <summary>The outcome of a connection test: running (no message yet), or finished with the tester's message.</summary>
    private sealed record TestState(bool Running, bool Ok, string Message);

    private static readonly TestState Running = new(Running: true, Ok: false, "");

    public override Element Render()
    {
        var editing = Props.Editing;
        var (draft, updateDraft) = UseReducer(editing is null ? ConnectionDraft.Empty : ConnectionDraft.From(editing));
        var (test, setTest) = UseState<TestState?>(null);
        var running = UseRef<CancellationTokenSource?>(null);

        // Closing the dialog unmounts the editor: a test still running is cancelled.
        UseEffect(() => () => CancelTest(running));

        var id = editing?.Id ?? "";
        var canBuild = draft.TryBuild(id, out _, out var buildError);
        var addressError = draft.AddressError;
        // The name and address errors are not repeated: Save is disabled and the address has its own message.
        var tlsError = !canBuild && addressError is null && buildError != ConnectionDraft.NameMissing ? buildError : null;

        void Edit(Func<ConnectionDraft, ConnectionDraft> change)
        {
            CancelTest(running);
            setTest(null);
            updateDraft(change);
        }

        void StartTest()
        {
            if (draft.TryBuild(id, out var profile, out _))
                _ = TestAsync(profile, running, setTest);
        }

        // Keyed, so a part that appears or goes never moves another into its slot (which would mount that one's box again). The
        // address is keyed on TLS too: turning TLS on or off can change the address (the default port), which only a box mounted
        // again shows, since an uncontrolled box is never written over.
        var content = (FlexColumn(
                Widgets.TextField(draft.Name, value => Edit(d => d with { Name = value }), header: "Name").WithKey("name"),
                AddressField(draft.Address, addressError, value => Edit(d => d with { Address = value })).WithKey(draft.UseTls ? "address-tls" : "address"),
                draft.IsNamedPipe
                    ? null
                    : ToggleSwitch(draft.UseTls, on => Edit(d => d.WithTls(on)), header: "Use TLS").AutomationName("Use TLS").WithKey("tls"),
                draft.UseTls && !draft.IsNamedPipe ? TlsFields(draft, tlsError, Edit).WithKey("tls-files") : null,
                TestRow(test, canBuild, StartTest).WithKey("test"))
            with
            {
                RowGap = 12,
            })
            .MinWidth(400);

        return ContentDialog(editing is null ? "Add connection" : "Edit connection", content, "Save") with
        {
            IsOpen = true,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = canBuild,
            OnClosed = result =>
            {
                CancelTest(running);
                if (result == ContentDialogResult.Primary && draft.TryBuild(editing?.Id ?? NewId(), out var profile, out _))
                    Props.OnSave(profile);
                Props.OnDone();
            },
        };
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static Element AddressField(string address, string? error, Action<string> onChanged) =>
        (FlexColumn(
                Widgets.TextField(address, onChanged, placeholder: "tcp://host:2375 or npipe:////./pipe/docker_engine", header: "Address"),
                error is null ? null : ErrorText(error))
            with
            {
                RowGap = 4,
            });

    private static Element TlsFields(ConnectionDraft draft, string? error, Action<Func<ConnectionDraft, ConnectionDraft>> edit) =>
        (FlexColumn(
                Widgets.TextField(draft.CaPath, value => edit(d => d with { CaPath = value }), placeholder: @"C:\certs\ca.pem", header: "CA certificate"),
                Widgets.TextField(draft.CertPath, value => edit(d => d with { CertPath = value }), placeholder: "Optional", header: "Client certificate"),
                Widgets.TextField(draft.KeyPath, value => edit(d => d with { KeyPath = value }), placeholder: "Optional", header: "Client key"),
                error is null ? null : ErrorText(error))
            with
            {
                RowGap = 12,
            });

    /// <summary>The Test button, a progress ring while the test runs, then the tester's message as it gave it.</summary>
    private static Element TestRow(TestState? test, bool canTest, Action onTest)
    {
        Element? outcome = test switch
        {
            null => null,
            { Running: true } => ProgressRing().Size(20, 20).AutomationName("Testing the connection"),
            { Ok: true } => Message(test.Message, Theme.SystemSuccess),
            _ => Message(test.Message, Theme.SystemCritical),
        };

        return (FlexRow(
                Button("Test", onTest).IsEnabled(canTest && test is not { Running: true }).Flex(shrink: 0),
                outcome?.Flex(grow: 1, shrink: 1, basis: 0))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 12,
            });
    }

    private static Element Message(string text, ThemeRef color) =>
        Body(text).TextWrapping().IsTextSelectionEnabled().Foreground(color).LiveRegion(AutomationLiveSetting.Polite);

    private static Element ErrorText(string text) =>
        Caption(text).TextWrapping().Foreground(Theme.SystemCritical);

    /// <summary>
    /// Runs the test for <paramref name="profile"/> and shows its message, unless the test was cancelled (an edit, a second test or
    /// closing the dialog) before it finished. Runs on the UI thread; the awaits come back to it.
    /// </summary>
    private static async Task TestAsync(ConnectionProfile profile, Ref<CancellationTokenSource?> running, Action<TestState?> setTest)
    {
        CancelTest(running);
        using var source = new CancellationTokenSource();
        running.Current = source;
        setTest(Running);

        TestState? outcome;
        try
        {
            var (ok, message) = await ConnectionTester.TestAsync(profile, source.Token);
            outcome = new TestState(Running: false, ok, message);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            outcome = null;
        }
        catch (Exception ex)
        {
            // Not an engine answer (a certificate file that cannot be read, for one): its message is still what the user needs.
            outcome = new TestState(Running: false, Ok: false, ex.Message);
        }

        if (ReferenceEquals(running.Current, source))
            running.Current = null;
        if (!source.IsCancellationRequested)
            setTest(outcome);
    }

    private static void CancelTest(Ref<CancellationTokenSource?> running)
    {
        running.Current?.Cancel();
        running.Current = null;
    }
}
