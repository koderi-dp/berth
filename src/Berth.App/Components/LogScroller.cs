using Berth.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Berth.Components;

/// <summary>
/// The list's scroll viewer: whether the view follows the newest line, kept from its own scrolling (see <see cref="LogView.Follows"/>),
/// scrolling to the bottom while it does, and bringing a line into view. One per tab, kept across renders.
/// </summary>
internal sealed class LogScroller
{
    private ScrollViewer? _view;
    private double _offset;

    public bool Following { get; private set; } = true;

    public Action<bool>? OnFollowingChanged { get; set; }

    public ItemsRepeater? Repeater { get; set; }

    /// <summary>Wrapped lines fit the width; unwrapped ones scroll sideways. Content is not anchored: following decides where the view goes.</summary>
    public static void Configure(ScrollViewer view, bool wrap)
    {
        view.HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled;
        view.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        view.VerticalAnchorRatio = double.NaN;
    }

    public void Attach(ScrollViewer view)
    {
        if (ReferenceEquals(view, _view))
            return;
        _view?.ViewChanged -= OnViewChanged;
        _view = view;
        _offset = view.VerticalOffset;
        view.ViewChanged += OnViewChanged;
    }

    /// <summary>Stops following: the view stays where it is as lines arrive.</summary>
    public void StopFollowing() => SetFollowing(false);

    /// <summary>Follows again, from the newest line.</summary>
    public void Follow()
    {
        SetFollowing(true);
        KeepAtBottom();
    }

    public void KeepAtBottom()
    {
        if (!Following || _view is not { } view)
            return;
        view.UpdateLayout();
        view.ChangeView(null, view.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>Scrolls the 0-based <paramref name="line"/> into the middle of the view.</summary>
    public void BringIntoView(int line)
    {
        if (Repeater?.GetOrCreateElement(line) is not { } element)
            return;
        element.UpdateLayout();
        element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = false });
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_view is not { } view)
            return;
        var follows = LogView.Follows(Following, _offset, view.VerticalOffset, view.ScrollableHeight);
        _offset = view.VerticalOffset;
        SetFollowing(follows);
    }

    private void SetFollowing(bool value)
    {
        if (value == Following)
            return;
        Following = value;
        OnFollowingChanged?.Invoke(value);
    }
}
