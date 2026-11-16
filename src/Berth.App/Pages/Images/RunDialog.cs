using System.Collections.Immutable;
using Berth.Components;
using Berth.Hooks;
using Berth.Presentation;
using Berth.Session;
using Berth.State;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages.Images;

/// <param name="Image">What the container is created from: the tag ("alpine:3.19"), or the image ID of an untagged image.</param>
/// <param name="ImageId">The image read for its exposed ports.</param>
/// <param name="OnDone">Called once the dialog has closed, with the container to open (the one started, or the one the user chose to look at), or null.</param>
internal sealed record RunDialogProps(string Image, string ImageId, Action<string?> OnDone);

/// <summary>
/// "Run a new container" from one image: the image under the title, the container's name, a host port box per port the image exposes
/// (read from the engine when the dialog opens), volume and environment rows with add and remove, Cancel and Run. Each field's error is
/// under it and Run is disabled while there is one. Run creates and starts the container: on success the dialog closes and the app opens
/// the container's page; a refused create or start shows the engine's message here. The fields' rules are <see cref="RunDraft"/>; the
/// text boxes are uncontrolled (<see cref="Widgets.TextField"/>), keyed by row so a removed row never hands its text to the next.
/// Mounted for one use: it opens when mounted.
/// </summary>
internal sealed class RunDialog : EngineStateComponent<RunDialogProps>
{
    private const double FieldGap = 8;
    private static readonly GridSize[] PairColumns = [GridSize.Star(), GridSize.Star(), GridSize.Px(40)];
    private static readonly GridSize[] PortColumns = [GridSize.Px(120), GridSize.Px(160), GridSize.Star()];
    private static readonly GridSize[] OneRow = [GridSize.Auto];

    public override Element Render()
    {
        var image = Props.Image;
        var imageId = Props.ImageId;
        var state = UseEngineState();
        var (draft, updateDraft) = UseReducer(RunDraft.For(image, []).AddVolume().AddEnv());
        // A stable key per volume and environment row, in the draft's order.
        var (volumeKeys, updateVolumeKeys) = UseReducer(ImmutableList.Create(0));
        var (envKeys, updateEnvKeys) = UseReducer(ImmutableList.Create(0));
        var nextKey = UseRef(1);
        // The request of the last Run (0 before the first) and the session it went to.
        var (sentTo, setSentTo) = UseState((Request: 0, Session: 0));
        var (open, setOpen) = UseState(true);
        var target = UseRef<string?>(null);
        // From the click until the run has ended, so a second click or Enter before the dialog renders again sends nothing.
        var asked = UseRef(false);
        var outcomeView = this.UseElementRef<FrameworkElement>();

        var connected = state.Status == ConnectionStatus.Connected;
        var inspect = state.ImageInspect is { } read && read.Id == imageId ? read : null;
        var inspection = inspect?.Inspection;
        // The session's entry for this dialog's request only, which is always answered. A switch of connection replaces the session,
        // and the new one never heard of the request: it is interrupted.
        var request = sentTo.Request;
        var run = request == 0 ? null
            : sentTo.Session != AppServices.Current.SessionNumber ? new ContainerRun(request, image, RunStatus.Interrupted, null, null)
            : state.Run is { } current && current.RequestId == request ? current : null;

        // Read the image when the dialog opens, and again after the connection changed (which forgets the read).
        var unread = inspect is null;
        UseEffect(
            () =>
            {
                if (connected && unread)
                    AppServices.Current.Session.Dispatch(new ImageInspectRequested(imageId));
            },
            connected,
            unread);

        // The image's ports arrive: one box each. Read again after a reconnect, the same ports keep what was typed in them.
        UseEffect(
            () =>
            {
                if (inspection is not null)
                    updateDraft(d => d.WithExposedPorts(inspection.Details.ExposedPorts));
            },
            (object?)inspection ?? image);

        // What the run did is the last part of a long form: scroll it into view when it changes.
        UseEffect(() => outcomeView.Current?.StartBringIntoView(), run?.Status.ToString() ?? "");

        // Started: close, and the app opens the container.
        var started = run is { Status: RunStatus.Started, ContainerId: { } startedId } ? startedId : null;
        UseEffect(
            () =>
            {
                if (started is not null)
                {
                    target.Current = started;
                    setOpen(false);
                }
            },
            started ?? "");

        var published = UseMemo(() => RunDraft.PublishedPorts(state.Containers), state.Containers);
        var errors = draft.Validate(published);
        if (asked.Current && run is { Status: not RunStatus.InProgress })
            asked.Current = false;
        var canRun = connected && errors.IsValid && started is null && inspect is { IsLoading: false } && !asked.Current;

        void Run()
        {
            if (!canRun || asked.Current)
                return;
            asked.Current = true;
            var session = AppServices.Current.Session;
            if (request != 0)
                session.Dispatch(new RunDismissed(request));
            var next = AppServices.Current.NextRequestId();
            setSentTo((next, AppServices.Current.SessionNumber));
            session.Dispatch(new RunContainerRequested(next, draft.ToRequest()));
        }

        void OpenCreated(string id)
        {
            target.Current = id;
            setOpen(false);
        }

        void AddVolume()
        {
            var key = nextKey.Current++;
            updateDraft(d => d.AddVolume());
            updateVolumeKeys(keys => keys.Add(key));
        }

        void RemoveVolume(int index)
        {
            updateDraft(d => d.RemoveVolume(index));
            updateVolumeKeys(keys => keys.RemoveAt(index));
        }

        void AddEnv()
        {
            var key = nextKey.Current++;
            updateDraft(d => d.AddEnv());
            updateEnvKeys(keys => keys.Add(key));
        }

        void RemoveEnv(int index)
        {
            updateDraft(d => d.RemoveEnv(index));
            updateEnvKeys(keys => keys.RemoveAt(index));
        }

        var content = DialogParts.Stack(
                20,
                Caption(image).Foreground(Theme.SecondaryText).IsTextSelectionEnabled().WithKey("image"),
                NameField(draft.Name, errors.Name, value => updateDraft(d => d with { Name = value })).WithKey("name"),
                Section("Ports", PortsPart(inspect, draft, errors, updateDraft)).WithKey("ports"),
                Section(
                    "Volumes",
                    VolumeRows(draft, errors, volumeKeys, updateDraft, RemoveVolume),
                    AddButton("Add volume", "add-volume", AddVolume),
                    RunDraft.HostPathNoteFor(state.Info) is { } note ? DialogParts.FieldHint(note).AutomationId("host-path-note") : null).WithKey("volumes"),
                Section(
                    "Environment variables",
                    EnvRows(draft, errors, envKeys, updateDraft, RemoveEnv),
                    AddButton("Add variable", "add-env", AddEnv)).WithKey("env"),
                Outcome(run, OpenCreated)?.Ref(outcomeView).WithKey("outcome"))
            .MinWidth(560);

        return (ContentDialog("Run a new container", content, "Run") with
            {
                IsOpen = open,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = canRun,
                OnClosed = _ =>
                {
                    var session = AppServices.Current.Session;
                    session.Dispatch(new ImageInspectDismissed());
                    if (request != 0)
                        session.Dispatch(new RunDismissed(request));
                    Props.OnDone(target.Current);
                },
            })
            .KeepOpenOnPrimary(Run)
            .Wide(680);
    }

    private static Element NameField(string name, string? error, Action<string> onChanged) =>
        DialogParts.Stack(
                4,
                Widgets.TextField(name, onChanged, header: "Container name").AutomationId("run-name").AutomationName("Container name"),
                error is null ? DialogParts.FieldHint("Leave empty for a random name.") : DialogParts.FieldError(error));

    /// <summary>A part of the form under its own heading.</summary>
    private static Element Section(string title, params Element?[] parts) =>
        DialogParts.Stack(FieldGap, [Widgets.StrongText(title), .. parts]);

    /// <summary>
    /// While the image is read, that it is; then a host port box per exposed port, or that it exposes none; a failed read shows the
    /// engine's message and the container can still run, without ports.
    /// </summary>
    private static Element PortsPart(ImageInspect? inspect, RunDraft draft, RunDraftErrors errors, Action<Func<RunDraft, RunDraft>> updateDraft)
    {
        if (inspect is null || inspect.IsLoading)
        {
            return (FlexRow(ProgressRing().Size(16, 16).AutomationName("Reading the image"), Body("Reading the image…").Foreground(Theme.SecondaryText))
                with
                {
                    AlignItems = FlexAlign.Center,
                    ColumnGap = 8,
                }).AutomationId("run-ports-loading");
        }

        if (inspect.Error is { } error)
        {
            return DialogParts.Stack(
                    4,
                    DialogParts.Message(error, Theme.SystemCritical),
                    DialogParts.FieldHint("You can still run it without publishing ports."))
                .AutomationId("run-ports-error");
        }

        if (draft.Ports.IsEmpty)
            return Body("This image exposes no ports.").Foreground(Theme.SecondaryText).AutomationId("run-no-ports");

        var rows = new List<Element>();
        for (var i = 0; i < draft.Ports.Length; i++)
        {
            var index = i;
            var port = draft.Ports[i];
            var label = $"{port.ContainerPort}/{port.Protocol}";
            rows.Add(DialogParts.Stack(
                    4,
                    Grid(
                        PortColumns,
                        OneRow,
                        Body(label).VAlign(VerticalAlignment.Center).Grid(column: 0),
                        Widgets.TextField(port.HostPort, value => updateDraft(d => d.WithHostPort(index, value)), placeholder: "Host port")
                            .AutomationId("run-port-" + label)
                            .AutomationName("Host port for " + label)
                            .Grid(column: 1)),
                    errors.Ports[i] is { } portError ? DialogParts.FieldError(portError) : null)
                .WithKey(label));
        }

        rows.Add(DialogParts.FieldHint("Leave a host port empty to keep that port unpublished, or enter 0 for any free port.").WithKey("hint"));
        return DialogParts.Stack(FieldGap, [.. rows]);
    }

    private static Element VolumeRows(
        RunDraft draft, RunDraftErrors errors, ImmutableList<int> keys, Action<Func<RunDraft, RunDraft>> updateDraft, Action<int> remove)
    {
        var rows = new List<Element>();
        for (var i = 0; i < draft.Volumes.Length; i++)
        {
            var index = i;
            var volume = draft.Volumes[i];
            var number = i + 1;
            rows.Add(PairRow(
                    Widgets.TextField(volume.HostPath, value => updateDraft(d => d.WithVolume(index, d.Volumes[index] with { HostPath = value })), placeholder: "Host path or volume name")
                        .AutomationId($"run-volume-host-{number}")
                        .AutomationName($"Host path {number}"),
                    Widgets.TextField(volume.ContainerPath, value => updateDraft(d => d.WithVolume(index, d.Volumes[index] with { ContainerPath = value })), placeholder: "Container path")
                        .AutomationId($"run-volume-container-{number}")
                        .AutomationName($"Container path {number}"),
                    Widgets.IconButton(Glyphs.Close, "Remove volume " + number, () => remove(index)).AutomationId($"run-volume-remove-{number}"),
                    errors.Volumes[i])
                .WithKey("volume-" + keys[i]));
        }

        return DialogParts.Stack(FieldGap, [.. rows]);
    }

    private static Element EnvRows(
        RunDraft draft, RunDraftErrors errors, ImmutableList<int> keys, Action<Func<RunDraft, RunDraft>> updateDraft, Action<int> remove)
    {
        var rows = new List<Element>();
        for (var i = 0; i < draft.Env.Length; i++)
        {
            var index = i;
            var env = draft.Env[i];
            var number = i + 1;
            rows.Add(PairRow(
                    Widgets.TextField(env.Name, value => updateDraft(d => d.WithEnv(index, d.Env[index] with { Name = value })), placeholder: "Variable")
                        .AutomationId($"run-env-name-{number}")
                        .AutomationName($"Variable {number}"),
                    Widgets.TextField(env.Value, value => updateDraft(d => d.WithEnv(index, d.Env[index] with { Value = value })), placeholder: "Value")
                        .AutomationId($"run-env-value-{number}")
                        .AutomationName($"Value {number}"),
                    Widgets.IconButton(Glyphs.Close, "Remove variable " + number, () => remove(index)).AutomationId($"run-env-remove-{number}"),
                    errors.Env[i])
                .WithKey("env-" + keys[i]));
        }

        return DialogParts.Stack(FieldGap, [.. rows]);
    }

    /// <summary>Two boxes side by side, a remove button, and the row's error under them.</summary>
    private static Element PairRow(Element first, Element second, Element remove, string? error) =>
        DialogParts.Stack(
                4,
                Grid(
                    PairColumns,
                    OneRow,
                    first.Margin(0, 0, FieldGap, 0).Grid(column: 0),
                    second.Margin(0, 0, 4, 0).Grid(column: 1),
                    remove.VAlign(VerticalAlignment.Center).Grid(column: 2)),
                error is null ? null : DialogParts.FieldError(error));

    private static Element AddButton(string label, string automationId, Action onClick) =>
        Button(
                HStack(8, Widgets.Glyph(Glyphs.Add, size: 12).VAlign(VerticalAlignment.Center), Body(label).VAlign(VerticalAlignment.Center)),
                onClick)
            .SubtleButton()
            .AutomationId(automationId)
            .AutomationName(label)
            .HAlign(HorizontalAlignment.Left)
            .Margin(-8, 0, 0, 0);

    /// <summary>
    /// What the run did: a ring while the container is created and started; the engine's message when it was refused; when the
    /// container exists but did not start (or the connection changed after it was created), a link to it.
    /// </summary>
    private static Element? Outcome(ContainerRun? run, Action<string> openCreated)
    {
        if (run is null || RunOutcome.Text(run) is not { } text)
            return null;

        if (run.Status == RunStatus.InProgress)
        {
            return (FlexRow(ProgressRing().Size(16, 16).AutomationName("Running"), Body(text).Foreground(Theme.SecondaryText))
                with
                {
                    AlignItems = FlexAlign.Center,
                    ColumnGap = 8,
                }).AutomationId("run-outcome");
        }

        var link = run.ContainerId is { } id
            ? HyperlinkButton("Open the container", onClick: () => openCreated(id)).AutomationId("run-open-created").Padding(0).HAlign(HorizontalAlignment.Left)
            : null;
        return DialogParts.Stack(4, DialogParts.Message(text, Theme.SystemCritical).AutomationId("run-outcome"), link);
    }
}
