using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Hooks;
using Microsoft.UI.Reactor.Input;
using Microsoft.UI.Xaml.Controls;

namespace Berth.Hooks;

/// <summary>The search box of a list page: reached through <see cref="Box"/> (Ctrl+F focuses it), and whether it has the focus.</summary>
internal sealed record ListSearch(ElementRef<TextBox> Box, bool Focused, Action<bool> SetFocused);

/// <summary>A list page (containers, images, volumes, networks): the engine state, plus its search box.</summary>
internal abstract class ListPageComponent : EngineStateComponent
{
    /// <summary>The page's search box reference and whether the box has the focus.</summary>
    protected ListSearch UseListSearch()
    {
        var box = this.UseElementRef<TextBox>();
        var (focused, setFocused) = UseState(false);
        return new ListSearch(box, focused, setFocused);
    }
}
