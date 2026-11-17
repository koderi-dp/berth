using Berth.Components;
using Berth.Connections;
using Berth.Engine;
using Berth.Hooks;
using Berth.Presentation;
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
/// The dialog that adds or edits a connection: name, address, TLS files or the SSH sign-in and saved host keys, and a test of the
/// connection. It opens when it is mounted;
/// the page mounts it for one edit and removes it when <see cref="ConnectionEditorProps.OnDone"/> is called. The rules live in
/// <see cref="ConnectionDraft"/>; this is the view over it. Its text boxes are uncontrolled (<see cref="Widgets.TextField"/>): each
/// starts from the draft when it is mounted and reports every change to it.
/// </summary>
internal sealed class ConnectionEditor : EngineStateComponent<ConnectionEditorProps>
{
    /// <summary>
    /// The outcome of a connection test: running (no message yet), or finished with the tester's message, and for SSH why it stopped
    /// (an unknown host key is offered for acceptance here).
    /// </summary>
    private sealed record TestState(bool Running, bool Ok, string Message, SshProblem? Ssh = null);

    private static readonly TestState Running = new(Running: true, Ok: false, "");

    private static readonly string[] SignInChoices = ["Password", "Key file"];

    public override Element Render()
    {
        var editing = Props.Editing;
        var (draft, updateDraft) = UseReducer(editing is null ? ConnectionDraft.Empty : ConnectionDraft.From(editing));
        var (test, setTest) = UseState<TestState?>(null);
        var running = UseRef<CancellationTokenSource?>(null);
        // Which time the commands were filled in from outside their boxes ("Use the WSL default"): their boxes are keyed on it.
        var (commandsFilled, setCommandsFilled) = UseState(0);
        // The WSL distributions, read once engine control is first turned on; null until they are.
        var (distros, setDistros) = UseState<string[]?>(null);
        var (distro, setDistro) = UseState(0);
        // The password or passphrase typed for this edit: in a ref, never in state; written to Credential Manager on Save, then cleared.
        var typedSecret = UseRef("");
        // Which time the key file was picked: its box is keyed on it, so the picked path shows.
        var (keyPicked, setKeyPicked) = UseState(0);
        var secretKind = draft.SignIn == SshSignIn.KeyFile ? SecretKind.Passphrase : SecretKind.Password;
        // A secret typed for one sign-in is never kept for the other: switching mounts an empty box.
        UseEffect(() => typedSecret.Current = "", draft.SignIn);
        var secretSaved = UseMemo(() => editing is not null && editing.Kind == TransportKind.Ssh && AppServices.Current.Secrets.IsSaved(editing.Id, secretKind), editing?.Id, secretKind);

        // Closing the dialog unmounts the editor: a test still running is cancelled.
        UseEffect(() => () => CancelTest(running));

        UseEffect(
            () =>
            {
                if (draft.EngineControlOn && distros is null)
                    _ = LoadDistrosAsync(setDistros);
            },
            draft.EngineControlOn);

        // Saving the active connection replaces its session; while its start or stop command runs, that waits.
        _ = UseEngineState();
        var commandRuns = AppServices.Current.EngineCommandRunsFor(editing?.Id);

        var id = editing?.Id ?? "";
        var canBuild = draft.TryBuild(id, out var built, out var buildError);
        // Kept secrets and host keys belong to the old address: said before Save, dropped on Save, never used by Test.
        var addressChanged = canBuild && SshEdit.AddressChanged(editing, built);
        var addressError = draft.AddressError;
        // Only the TLS errors go under the TLS files: the name's is not repeated (Save is disabled), the address and the commands have their own.
        var tlsError = !canBuild && buildError is ConnectionDraft.CaMissing or ConnectionDraft.ClientPairIncomplete ? buildError : null;

        void Edit(Func<ConnectionDraft, ConnectionDraft> change)
        {
            CancelTest(running);
            setTest(null);
            updateDraft(change);
        }

        void TestDraft(ConnectionDraft tested)
        {
            if (tested.TryBuild(id, out var profile, out _))
                _ = TestAsync(profile, new EditorSecrets(SshEdit.MayUseSavedSecret(editing, profile) ? id : "", secretKind, typedSecret.Current), running, setTest);
        }

        void StartTest() => TestDraft(draft);

        // The test met a host key never seen: accepted here, it goes into the draft (saved with the connection) and the test runs again.
        void AcceptTestedKey(HostKey key)
        {
            var accepted = draft with { KnownHosts = new SshOptions("", SshSignIn.Password, null, draft.KnownHosts).Accept(key).KnownHosts };
            updateDraft(_ => accepted);
            TestDraft(accepted);
        }

        void SecretChanged(string value)
        {
            typedSecret.Current = value;
            CancelTest(running);
            setTest(null);
        }

        async Task PickKeyAsync()
        {
            var picked = await PickFileAsync(new FilePickerOptions(CommitButtonText: "Use this key") { Title = "Choose the private key file" });
            if (picked?.Path is { Length: > 0 } path)
            {
                Edit(d => d with { SshKeyPath = path });
                setKeyPicked(keyPicked + 1);
            }
        }

        // Keyed, so a part that appears or goes never moves another into its slot (which would mount that one's box again). The
        // address is keyed on TLS too: turning TLS on or off can change the address (the default port), which only a box mounted
        // again shows, since an uncontrolled box is never written over.
        var content = (FlexColumn(
                Widgets.TextField(draft.Name, value => Edit(d => d with { Name = value }), header: "Name").WithKey("name"),
                AddressField(draft.Address, addressError, value => Edit(d => d with { Address = value })).WithKey(draft.UseTls ? "address-tls" : "address"),
                draft.IsNamedPipe || draft.IsSsh
                    ? null
                    : ToggleSwitch(draft.UseTls, on => Edit(d => d.WithTls(on)), header: "Use TLS").AutomationName("Use TLS").WithKey("tls"),
                draft.UseTls && !draft.IsNamedPipe && !draft.IsSsh ? TlsFields(draft, tlsError, Edit).WithKey("tls-files") : null,
                draft.IsSsh
                    ? SshFields(draft, buildError == ConnectionDraft.KeyFileMissing ? buildError : null, secretSaved, keyPicked, Edit, SecretChanged, () => _ = PickKeyAsync())
                        .WithKey("ssh-" + draft.SignIn)
                    : null,
                draft.IsSsh ? SavedKeys(draft, key => Edit(d => d.WithoutHostKey(key))).WithKey("ssh-keys") : null,
                addressChanged
                    ? Caption(SshEdit.AddressChangedNote).TextWrapping().Foreground(Theme.SystemCaution).AutomationId("ssh-address-changed").WithKey("address-changed")
                    : null,
                TestRow(test, canBuild, StartTest, AcceptTestedKey).WithKey("test"),
                commandRuns ? ErrorText(EngineCommandRuns).WithKey("command-runs") : null,
                draft.EngineControlAvailable
                    ? ToggleSwitch(draft.EngineControlOn, on => Edit(d => d with { EngineControlOn = on }), header: "Engine control")
                        .AutomationName("Engine control")
                        .AutomationId("engine-control")
                        .WithKey("engine-control")
                    : Caption(NoEngineControlOverSsh).TextWrapping().Foreground(Theme.SecondaryText).AutomationId("engine-control-ssh").WithKey("engine-control-ssh"),
                draft.EngineControlOn && draft.EngineControlAvailable
                    ? EngineControlFields(
                            draft,
                            commandsFilled,
                            distros,
                            distro,
                            setDistro,
                            Edit,
                            control =>
                            {
                                Edit(d => d.WithEngineControl(control));
                                setCommandsFilled(commandsFilled + 1);
                            })
                        .WithKey("engine-commands")
                    : null)
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
            IsPrimaryButtonEnabled = canBuild && !commandRuns,
            OnClosed = result =>
            {
                CancelTest(running);
                var secret = typedSecret.Current;
                typedSecret.Current = "";
                if (result == ContentDialogResult.Primary && !AppServices.Current.EngineCommandRunsFor(editing?.Id) && draft.TryBuild(editing?.Id ?? NewId(), out var profile, out _))
                {
                    var save = SshEdit.Save(editing, profile);
                    foreach (var kind in save.DeleteSecrets)
                        AppServices.Current.Secrets.Delete(profile.Id, kind);
                    var secretKept = profile.Kind == TransportKind.Ssh && secret.Length > 0 && SaveSecret(profile.Id, secretKind, secret);
                    Props.OnSave(save.Profile);
                    if (secretKept)
                        AppServices.Current.SecretChanged(profile.Id);
                }

                Props.OnDone();
            },
        };
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private const string NoEngineControlOverSsh =
        "Engine control is not available for SSH connections: its commands would run on this computer, not on the remote host.";

    /// <summary>Writes the typed secret to Credential Manager; a failure is logged in fixed words and the secret is not kept.</summary>
    private static bool SaveSecret(string id, SecretKind kind, string secret)
    {
        try
        {
            AppServices.Current.Secrets.Set(id, kind, secret);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("saving a secret in Credential Manager failed", ex);
            return false;
        }
    }

    /// <summary>
    /// The SSH sign-in: password or key file; the password box, or the key file (typed or picked) and its passphrase box. A secret
    /// box is masked and starts empty; when one is kept in Credential Manager it says so, and leaving it empty keeps it.
    /// </summary>
    private static Element SshFields(
        ConnectionDraft draft,
        string? keyError,
        bool secretSaved,
        int keyPicked,
        Action<Func<ConnectionDraft, ConnectionDraft>> edit,
        Action<string> onSecret,
        Action onPickKey)
    {
        var byKey = draft.SignIn == SshSignIn.KeyFile;
        var secretLabel = byKey ? "Passphrase" : "Password";
        var secretHint = secretSaved
            ? $"A {secretLabel.ToLowerInvariant()} is kept in Windows Credential Manager. Leave this empty to keep it, or type a new one."
            : byKey
                ? "Only for a key file protected by a passphrase. Saved in Windows Credential Manager, never in Berth's settings."
                : "Saved in Windows Credential Manager, never in Berth's settings. Leave it empty to be asked when Berth connects.";

        var keyRow = byKey
            ? (FlexRow(
                    Widgets.TextField(draft.SshKeyPath, value => edit(d => d with { SshKeyPath = value }), placeholder: "Path of the private key file", header: "Private key file")
                        .AutomationId("ssh-key-path")
                        .Flex(grow: 1, shrink: 1, basis: 0)
                        .WithKey("key-" + keyPicked),
                    Button("Browse…", onPickKey).AutomationId("ssh-key-browse").VAlign(Microsoft.UI.Xaml.VerticalAlignment.Bottom).Flex(shrink: 0))
                with
                {
                    ColumnGap = 8,
                    AlignItems = FlexAlign.End,
                })
            : null;

        return DialogParts.Stack(
            12,
            (RadioButtons(SignInChoices, byKey ? 1 : 0, index => edit(d => d with { SignIn = index == 1 ? SshSignIn.KeyFile : SshSignIn.Password })) with
            {
                Header = "Sign in with",
            }).AutomationId("ssh-sign-in").WithKey("sign-in"),
            keyRow?.WithKey("key-file"),
            keyError is null ? null : ErrorText(keyError).WithKey("key-error"),
            PasswordBox(default, onSecret)
                .Header(secretLabel)
                .AutomationName(secretLabel)
                .AutomationId("ssh-secret")
                .WithKey("secret"),
            DialogParts.FieldHint(secretHint).WithKey("secret-hint"));
    }

    /// <summary>The host keys this connection accepted, each with Remove: the one way past a changed key.</summary>
    private static Element SavedKeys(ConnectionDraft draft, Action<HostKey> onRemove)
    {
        if (draft.KnownHosts.IsEmpty)
            return DialogParts.FieldHint("No host key accepted yet. The first connection shows the host's key and asks.").AutomationId("ssh-keys-none");

        return DialogParts.Stack(
            8,
            [
                Widgets.StrongText("Saved host keys"),
                .. draft.KnownHosts.Select((key, index) =>
                    (FlexRow(
                            (FlexColumn(
                                    Caption(SshPrompts.HostName(key.Host, key.Port)).Foreground(Theme.SecondaryText),
                                    Widgets.OneLine(TextBlock(SshPrompts.KeyLine(key)).FontFamily("Cascadia Mono, Consolas")).ToolTip(SshPrompts.KeyLine(key)))
                                with
                                {
                                    RowGap = 2,
                                })
                                .Flex(grow: 1, shrink: 1, basis: 0),
                            Button("Remove", () => onRemove(key)).AutomationId("remove-host-key-" + index).AutomationName("Remove the key of " + key.Host).Flex(shrink: 0))
                        with
                        {
                            AlignItems = FlexAlign.Center,
                            ColumnGap = 8,
                        })
                    .WithKey(key.Host + ":" + key.Port + ":" + key.Fingerprint)),
            ]).AutomationId("ssh-keys");
    }

    private static Element AddressField(string address, string? error, Action<string> onChanged) =>
        (FlexColumn(
                Widgets.TextField(address, onChanged, placeholder: "tcp://host:2375, npipe:////./pipe/docker_engine or ssh://user@host", header: "Address"),
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

    private const string CommandsNote =
        "Berth runs these commands on this computer as you, without a shell, to start and stop this connection's engine. A program " +
        "named without a folder is looked for in Berth's folder, the Windows folders and PATH. A .bat or .cmd file needs cmd.exe /c.";

    private const string EngineCommandRuns = "Wait for the engine command to finish.";

    /// <summary>
    /// The start and stop commands with their errors, the note on how they run, and "Use the WSL default" with the distribution picker.
    /// The boxes are keyed on <paramref name="filled"/>, so the default, written from outside them, shows.
    /// </summary>
    private static Element EngineControlFields(
        ConnectionDraft draft,
        int filled,
        string[]? distros,
        int distro,
        Action<int> setDistro,
        Action<Func<ConnectionDraft, ConnectionDraft>> edit,
        Action<EngineControl> useDefault)
    {
        // Shown once the box has text: an empty box is already explained by the disabled Save and its placeholder.
        var startError = draft.StartCommand.Trim().Length > 0 ? draft.StartCommandError : null;
        var stopError = draft.StopCommand.Trim().Length > 0 ? draft.StopCommandError : null;

        return (FlexColumn(
                Caption(CommandsNote).TextWrapping().Foreground(Theme.SecondaryText),
                Widgets.TextField(draft.StartCommand, value => edit(d => d with { StartCommand = value }), placeholder: "Program and arguments", header: "Start command")
                    .AutomationId("engine-start-command")
                    .WithKey("start-" + filled),
                startError is null ? null : ErrorText(startError).WithKey("start-error"),
                Widgets.TextField(draft.StopCommand, value => edit(d => d with { StopCommand = value }), placeholder: "Program and arguments", header: "Stop command")
                    .AutomationId("engine-stop-command")
                    .WithKey("stop-" + filled),
                stopError is null ? null : ErrorText(stopError).WithKey("stop-error"),
                WslDefaultRow(distros, distro, setDistro, useDefault).WithKey("wsl-default"))
            with
            {
                RowGap = 8,
            });
    }

    /// <summary>The distribution picker and "Use the WSL default"; while WSL is asked, a note; with no distribution, a note why.</summary>
    private static Element WslDefaultRow(string[]? distros, int distro, Action<int> setDistro, Action<EngineControl> useDefault)
    {
        if (distros is null)
            return Caption("Looking for WSL distributions…").Foreground(Theme.SecondaryText);
        if (distros.Length == 0)
            return Caption("No WSL distribution was found, so there is no WSL default. Type the commands instead.").TextWrapping().Foreground(Theme.SecondaryText);

        var chosen = Math.Clamp(distro, 0, distros.Length - 1);
        return (FlexRow(
                ComboBox(distros, chosen, setDistro).AutomationName("WSL distribution").AutomationId("wsl-distro").Width(200).Flex(shrink: 0),
                Button("Use the WSL default", () => useDefault(EngineControl.WslDefault(distros[chosen]))).AutomationId("wsl-default").Flex(shrink: 0))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 8,
            });
    }

    /// <summary>Reads the WSL distributions off the UI thread and hands them over on it.</summary>
    private static async Task LoadDistrosAsync(Action<string[]?> setDistros)
    {
        var distros = await Task.Run(WslList.LoadAsync);
        setDistros([.. distros]);
    }

    /// <summary>
    /// The Test button, a progress ring while the test runs, then the tester's message as it gave it. An SSH host whose key was never
    /// seen shows the key and "Accept key" under it.
    /// </summary>
    private static Element TestRow(TestState? test, bool canTest, Action onTest, Action<HostKey> onAccept)
    {
        Element? outcome = test switch
        {
            null => null,
            { Running: true } => ProgressRing().Size(20, 20).AutomationName("Testing the connection"),
            { Ok: true } => Message(test.Message, Theme.SystemSuccess),
            { Ssh: { Failure: SshFailure.HostKeyUnknown, Offered: not null } } => Message("The host's key is not known yet. Check it below.", Theme.SecondaryText),
            _ => Message(test.Message, Theme.SystemCritical),
        };

        var row = (FlexRow(
                Button("Test", onTest).IsEnabled(canTest && test is not { Running: true }).AutomationId("test-connection").Flex(shrink: 0),
                outcome?.Flex(grow: 1, shrink: 1, basis: 0))
            with
            {
                AlignItems = FlexAlign.Center,
                ColumnGap = 12,
            });
        if (test?.Ssh is not { Failure: SshFailure.HostKeyUnknown, Offered: { } offered })
            return row;

        var question = SshPrompts.Question(offered);
        return DialogParts.Stack(
            8,
            row,
            Body(question.Intro).TextWrapping(),
            Components.SshPromptButton.KeyBox(question.KeyType + " key", question.Fingerprint, "test-host-key"),
            DialogParts.FieldHint(question.Advice),
            Button("Accept key", () => onAccept(offered)).AutomationId("test-accept-key").HAlign(Microsoft.UI.Xaml.HorizontalAlignment.Left));
    }

    private static Element Message(string text, ThemeRef color) =>
        Body(text).TextWrapping().IsTextSelectionEnabled().Foreground(color).LiveRegion(AutomationLiveSetting.Polite);

    private static Element ErrorText(string text) =>
        Caption(text).TextWrapping().Foreground(Theme.SystemCritical);

    /// <summary>
    /// Runs the test for <paramref name="profile"/> and shows its message, unless the test was cancelled (an edit, a second test or
    /// closing the dialog) before it finished. Runs on the UI thread; the awaits come back to it.
    /// </summary>
    private static async Task TestAsync(ConnectionProfile profile, ISecretStore secrets, Ref<CancellationTokenSource?> running, Action<TestState?> setTest)
    {
        CancelTest(running);
        using var source = new CancellationTokenSource();
        running.Current = source;
        setTest(Running);

        TestState? outcome;
        try
        {
            var (ok, message, ssh) = await ConnectionTester.TestAsync(profile, secrets, source.Token);
            outcome = new TestState(Running: false, ok, message, ssh);
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

    /// <summary>
    /// The secrets a test of the edited connection reads: the one typed in the editor for this test only, otherwise the one kept in
    /// Credential Manager for the connection being edited (<paramref name="id"/> empty when its address changed: none then). Nothing
    /// is written.
    /// </summary>
    private sealed class EditorSecrets(string id, SecretKind typedKind, string typed) : ISecretStore
    {
        public string? Get(string connectionId, SecretKind kind)
        {
            if (kind == typedKind && typed.Length > 0)
                return typed;
            return id.Length > 0 ? AppServices.Current.Secrets.Get(id, kind) : null;
        }

        public void Set(string connectionId, SecretKind kind, string secret) => throw new NotSupportedException("A test keeps no secret.");

        public void Delete(string connectionId, SecretKind kind) => throw new NotSupportedException("A test keeps no secret.");
    }
}
