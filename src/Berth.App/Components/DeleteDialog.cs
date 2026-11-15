using System.Collections.Immutable;
using Berth.Presentation;
using Berth.Session;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Xaml.Controls;
using static Microsoft.UI.Reactor.Factories;

namespace Berth.Components;

/// <summary>What the delete confirmation is about: the ids asked for and the text that names them.</summary>
internal sealed record DeleteRequest(ImmutableArray<string> Ids, string Text)
{
    /// <summary>A request to delete <paramref name="rows"/>, named as <see cref="BulkSelection.DeleteText"/> names them.</summary>
    public static DeleteRequest For(IReadOnlyList<ContainerRow> rows) => new([.. rows.Select(r => r.Id)], BulkSelection.DeleteText(rows));
}

/// <summary>The confirmation before containers are deleted, shared by the containers page and a container's detail page.</summary>
internal static class DeleteDialog
{
    private static readonly ContainerFilter NoFilter = new("", RunningOnly: false);

    /// <summary>Always in the tree; open while there is a <paramref name="request"/>. Cancel is the default button.</summary>
    public static Element Render(DeleteRequest? request, Action onDone) =>
        ContentDialog("Delete container?", Body(request?.Text ?? "").TextWrapping(), "Delete") with
        {
            IsOpen = request is not null,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            OnClosed = result =>
            {
                if (result == ContentDialogResult.Primary && request is not null)
                    Confirm(request.Ids);
                onDone();
            },
        };

    /// <summary>
    /// Removes the confirmed containers as they are now, read from the current session when the user confirms: rows that are gone
    /// or became pending while the dialog was open are skipped, running ones are stopped first (force), and nothing is sent when
    /// the connection was lost meanwhile.
    /// </summary>
    private static void Confirm(ImmutableArray<string> ids)
    {
        var session = AppServices.Current.Session;
        var state = session.State;
        var rows = ComposeGrouping.Build(state.Containers, state.Pending, NoFilter).SelectMany(g => g.Rows);
        var removal = BulkSelection.Removal(rows, ids, state.Status == ConnectionStatus.Connected);
        if (!removal.Ids.IsEmpty)
            session.Dispatch(new ActionRequested(removal.Ids, ContainerAction.Remove, removal.Force));
    }
}
