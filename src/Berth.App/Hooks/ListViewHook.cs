using System.Collections.Immutable;
using Berth.Presentation;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml.Controls;

namespace Berth.Hooks;

/// <summary>
/// What each list shows apart from its rows (search, filter, sort, collapsed groups), kept by route for as long as Berth runs. A list
/// page left in the navigation cache keeps its own; this is for one the cache dropped (every container opened takes a place in it),
/// which starts again from what it showed. Checked rows are not kept: a page built again starts with none checked. UI thread only.
/// </summary>
internal static class ListViewStore
{
    private static readonly Dictionary<AppRoute, object> Views = [];

    /// <summary>What <paramref name="route"/>'s list showed last, or <paramref name="initial"/> when it has not been shown.</summary>
    public static T Get<T>(AppRoute route, T initial) where T : notnull =>
        Views.TryGetValue(route, out var view) && view is T kept ? kept : initial;

    public static void Set<T>(AppRoute route, T view) where T : notnull => Views[route] = view;
}

/// <summary>
/// The view state of the images, volumes or networks list: its filter (which holds the search) and its sort. Keep it with
/// <see cref="ListPageComponent.UseListView{T}"/>.
/// </summary>
internal sealed record ResourceListView<TFilter, TColumn>(TFilter Filter, Berth.Presentation.ListSort<TColumn> Sort) where TColumn : struct, Enum;

/// <summary>The search box of a list page: reached through <see cref="Box"/> (Ctrl+F focuses it), and whether it has the focus.</summary>
internal sealed record ListSearch(ElementRef<TextBox> Box, bool Focused, Action<bool> SetFocused);

/// <summary>A list page (containers, images, volumes, networks): the engine state, plus its view state kept by route.</summary>
internal abstract class ListPageComponent : EngineStateComponent
{
    /// <summary>
    /// The list's view state (a record of its search, filter, sort, collapsed groups): the last one <paramref name="route"/> showed, or
    /// <paramref name="initial"/>. Every change is kept in <see cref="ListViewStore"/>, so a page mounted again starts from it.
    /// </summary>
    protected (T View, Action<Func<T, T>> Update) UseListView<T>(AppRoute route, T initial) where T : notnull
    {
        var (view, update) = UseReducer(ListViewStore.Get(route, initial));
        UseEffect(() => ListViewStore.Set(route, view), view);
        return (view, update);
    }

    /// <summary>
    /// The checked rows of a list (view state, not kept by route) and the action that checks (true) or unchecks keys. A row that is no
    /// longer in <paramref name="all"/> (the whole list, a memoized instance) cannot stay checked.
    /// </summary>
    protected (ImmutableHashSet<string> Checked, Action<IEnumerable<string>, bool> SetChecked) UseCheckedKeys<TRow>(IReadOnlyList<TRow> all)
        where TRow : ISelectableRow
    {
        var (checkedKeys, updateChecked) = UseReducer(ImmutableHashSet<string>.Empty);
        UseEffect(
            () =>
            {
                var existing = all.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
                updateChecked(keys => keys.All(existing.Contains) ? keys : keys.Intersect(existing));
            },
            all);
        return (checkedKeys, (keys, value) => updateChecked(current => value ? current.Union(keys) : current.Except(keys)));
    }

    /// <summary>The page's search box reference and whether the box has the focus.</summary>
    protected ListSearch UseListSearch()
    {
        var box = this.UseElementRef<TextBox>();
        var (focused, setFocused) = UseState(false);
        return new ListSearch(box, focused, setFocused);
    }
}
