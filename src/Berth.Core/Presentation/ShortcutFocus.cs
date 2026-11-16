namespace Berth.Presentation;

/// <summary>Where the keyboard focus is when one of the app's shortcuts (Ctrl+F, F5, Delete, Escape, Alt+Left) is pressed.</summary>
/// <param name="InWindowContent">In the window's content, not in a dialog, menu or drop-down (which sit in popups outside it).</param>
/// <param name="InTextBox">In a box that edits text.</param>
/// <param name="InTerminal">In the terminal (a WebView2): every key there belongs to the shell.</param>
public sealed record ShortcutFocus(bool InWindowContent, bool InTextBox, bool InTerminal);

/// <summary>Whether a shortcut acts for the page, given where the focus is.</summary>
public static class ShortcutFocusRule
{
    /// <param name="focus">Where the focus is; null when nothing has it.</param>
    /// <param name="notInTextBox">The key edits text in a text box (Delete), so it is the box's there.</param>
    public static bool Allows(ShortcutFocus? focus, bool notInTextBox) =>
        focus is null || (focus.InWindowContent && !focus.InTerminal && !(notInTextBox && focus.InTextBox));
}
