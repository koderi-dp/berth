using System.Diagnostics;
using Berth.Components;
using Berth.Connections;
using Berth.Hooks;
using Berth.Settings;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Pages;

/// <summary>The saved connections, with add, edit, test and delete; and the way to the log folder.</summary>
internal sealed class SettingsPage : Component
{
    private const string LastConnection = "Berth needs at least one connection.";
    private const string SaveFailed = "The settings could not be saved. Your change applies until Berth closes.";

    /// <summary>An open connection editor: <paramref name="Editing"/> is the connection being edited, or null when adding one.</summary>
    private sealed record EditorRequest(ConnectionProfile? Editing);

    public override Element Render()
    {
        var settings = UseExternalStore(SettingsHook.Subscribe, SettingsHook.Snapshot);
        var (editor, setEditor) = UseState<EditorRequest?>(null);
        var (deleting, setDeleting) = UseState<ConnectionProfile?>(null);
        var (saveFailed, setSaveFailed) = UseState(false);

        void Apply(Func<AppSettings, AppSettings> change)
        {
            if (!AppServices.Current.UpdateSettings(change))
                setSaveFailed(true);
        }

        var editorElement = editor is null
            ? null
            : Component<ConnectionEditor, ConnectionEditorProps>(new ConnectionEditorProps(
                editor.Editing,
                profile => Apply(current => editor.Editing is null ? ConnectionEdits.Add(current, profile) : ConnectionEdits.Replace(current, profile)),
                () => setEditor(null)));

        var content = (FlexColumn(
                Widgets.PageTitle("Settings").HeadingLevel(AutomationHeadingLevel.Level1),
                saveFailed
                    ? (InfoBar(message: SaveFailed) with { IsOpen = true }).Error().IsClosable().Closed(() => setSaveFailed(false))
                    : null,
                Section(
                    "Connections",
                    ConnectionList(settings, profile => setEditor(new EditorRequest(profile)), setDeleting),
                    Button("Add connection", () => setEditor(new EditorRequest(null))).HAlign(HorizontalAlignment.Left)),
                Section(
                    "Windows",
                    ToggleSwitch(
                            settings.Tray.KeepRunningWhenClosed,
                            on => Apply(current => current with { Tray = current.Tray with { KeepRunningWhenClosed = on } }),
                            header: "Keep running in the tray when the window is closed")
                        .AutomationName("Keep running in the tray when the window is closed")
                        .AutomationId("keep-running-in-tray"),
                    Component<StartWithWindowsSwitch>()),
                Section(
                    "Diagnostics",
                    Button("Open log folder", OpenLogFolder).HAlign(HorizontalAlignment.Left)),
                editorElement,
                DeleteDialog(deleting, id => { if (!AppServices.Current.DeleteConnection(id)) setSaveFailed(true); }, () => setDeleting(null)))
            with
            {
                RowGap = 24,
            })
            .FlexPadding(24, 16, 24, 24);

        return ScrollView(content).Flex(grow: 1, basis: 0);
    }

    private static Element Section(string title, params Element?[] children) =>
        (FlexColumn([Widgets.SectionTitle(title).HeadingLevel(AutomationHeadingLevel.Level2), .. children]) with
        {
            RowGap = 12,
        });

    private static Element ConnectionList(AppSettings settings, Action<ConnectionProfile> onEdit, Action<ConnectionProfile> onDelete)
    {
        var isLast = settings.Connections.Count <= 1;
        return (FlexColumn([.. settings.Connections.Select(profile =>
                ConnectionRow(profile, profile.Id == settings.ActiveConnectionId, isLast, onEdit, onDelete).WithKey(profile.Id))])
            with
            {
                RowGap = 4,
            });
    }

    /// <summary>One connection: its name and address, the "Active" badge on the active one, and its Edit and Delete buttons.</summary>
    private static Element ConnectionRow(ConnectionProfile profile, bool active, bool isLast, Action<ConnectionProfile> onEdit, Action<ConnectionProfile> onDelete)
    {
        var address = EngineAddress.Format(profile);

        var text = (FlexColumn(
                Widgets.OneLine(Widgets.StrongText(profile.Name)).ToolTip(profile.Name),
                Widgets.OneLine(Caption(address)).Foreground(Theme.SecondaryText).ToolTip(address))
            with
            {
                RowGap = 2,
            })
            .Flex(grow: 1, shrink: 1, basis: 0);

        var badge = active
            ? Border(Caption("Active").Foreground(Theme.Ref("TextOnAccentFillColorPrimaryBrush")))
                .Background(Theme.Accent)
                .CornerRadius(4)
                .Padding(8, 2)
                .Flex(shrink: 0)
            : null;

        var edit = Widgets.IconButton(Glyphs.Edit, "Edit", () => onEdit(profile)).AutomationId("edit-" + profile.Name).Flex(shrink: 0);

        var delete = Widgets.IconButton(Glyphs.Delete, "Delete", () => onDelete(profile)).AutomationId("delete-" + profile.Name);
        // A disabled button shows no tooltip, so the reason sits on a wrapper the pointer reaches through the button.
        Element deleteSlot = isLast
            ? Border(delete.IsEnabled(false).HelpText(LastConnection))
                .Background(Theme.Ref("SubtleFillColorTransparentBrush"))
                .ToolTip(LastConnection)
            : delete;

        return Card(
                (FlexRow(text, badge, edit, deleteSlot.Flex(shrink: 0)) with
                {
                    AlignItems = FlexAlign.Center,
                    ColumnGap = 8,
                }))
            .Padding(16, 8, 8, 8);
    }

    /// <summary>The confirmation before a connection is deleted; it is always in the tree and opens while there is one to delete.</summary>
    private static Element DeleteDialog(ConnectionProfile? profile, Action<string> onDelete, Action onDone) =>
        ContentDialog("Delete connection", Body(profile is null ? "" : $"Delete the connection {profile.Name}?").TextWrapping(), "Delete") with
        {
            IsOpen = profile is not null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result =>
            {
                if (result == ContentDialogResult.Primary && profile is not null)
                    onDelete(profile.Id);
                onDone();
            },
        };

    private static void OpenLogFolder()
    {
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(LocalData.Directory);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Error("opening the log folder failed", ex);
        }
    }
}
