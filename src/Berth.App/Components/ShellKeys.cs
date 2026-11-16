using Microsoft.UI.Reactor.Navigation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Berth.Components;

/// <summary>
/// What the page shown does with the app's keyboard shortcuts. A null action is a key the page has no use for right now (none while
/// one of its dialogs is open, no Delete while nothing checked can be deleted), so the key goes on to the control that has the focus.
/// </summary>
/// <param name="Search">Ctrl+F: focus the page's search.</param>
/// <param name="Reload">F5: read again what the page shows.</param>
/// <param name="Delete">Delete: ask to delete what is selected (the page confirms first).</param>
/// <param name="Escape">Escape: the page's own use of it (on a container's page: the log search, else back to the list).</param>
/// <param name="Back">Alt+Left: back to where the page came from; without one the app goes back in its history.</param>
internal sealed record PageKeys(Action? Search = null, Action? Reload = null, Action? Delete = null, Action? Escape = null, Action? Back = null);

/// <summary>
/// The app's keyboard shortcuts, at the window root so they work wherever the focus is (the sidebar, the title bar, the page), and
/// handed to the page shown. Each page registers its <see cref="PageKeys"/> under its route every time it renders, so the actions
/// act on what it shows now.
/// </summary>
/// <remarks>
/// One set of accelerators, made once when the root mounts: Reactor's <c>CommandHost</c> makes its accelerators again on every render
/// and takes a key even when its command is disabled, so an inner host's disabled Escape kept the page's Escape from ever running.
/// A key is taken only when the page acts on it; it is left alone while the focus is outside the window's content (a dialog, a menu,
/// a drop-down) and, for Delete, while it is in a text box, where Delete deletes a character.
/// </remarks>
internal static class ShellKeys
{
    private static readonly Dictionary<AppRoute, PageKeys> Pages = [];

    /// <summary>
    /// Makes <paramref name="keys"/> the shortcuts of <paramref name="route"/>'s page; returns the action that takes them back (use it
    /// as the cleanup of the effect that registers them).
    /// </summary>
    public static Action Register(AppRoute route, PageKeys keys)
    {
        Pages[route] = keys;
        return () =>
        {
            if (Pages.TryGetValue(route, out var current) && ReferenceEquals(current, keys))
                Pages.Remove(route);
        };
    }

    /// <summary>Puts the shortcuts on <paramref name="root"/>, the window's content, for the page that <paramref name="nav"/> shows.</summary>
    public static void Attach(FrameworkElement root, NavigationHandle<AppRoute> nav)
    {
        root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        Add(VirtualKey.F, VirtualKeyModifiers.Control, keys => keys.Search);
        Add(VirtualKey.F5, VirtualKeyModifiers.None, keys => keys.Reload);
        Add(VirtualKey.Delete, VirtualKeyModifiers.None, keys => keys.Delete, notInTextBox: true);
        Add(VirtualKey.Escape, VirtualKeyModifiers.None, keys => keys.Escape);
        Add(VirtualKey.Left, VirtualKeyModifiers.Menu, keys => keys.Back ?? (nav.CanGoBack ? () => nav.GoBack() : null));

        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Func<PageKeys, Action?> action, bool notInTextBox = false)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                if (!FocusAllows(root, notInTextBox))
                    return;
                var keys = Pages.GetValueOrDefault(nav.CurrentRoute) ?? new PageKeys();
                if (action(keys) is { } act)
                {
                    e.Handled = true;
                    act();
                }
            };
            root.KeyboardAccelerators.Add(accelerator);
        }
    }

    /// <summary>
    /// Whether a shortcut may act: the focus is in the window's content (not in a dialog, menu or drop-down, which sit in popups outside
    /// it) and, when <paramref name="notInTextBox"/>, not in a box that edits text.
    /// </summary>
    private static bool FocusAllows(FrameworkElement root, bool notInTextBox)
    {
        if (root.XamlRoot is not { } xamlRoot || FocusManager.GetFocusedElement(xamlRoot) is not DependencyObject focused)
            return true;
        if (notInTextBox && focused is TextBox or PasswordBox or RichEditBox)
            return false;
        for (var node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, root))
                return true;
        }

        return false;
    }
}
