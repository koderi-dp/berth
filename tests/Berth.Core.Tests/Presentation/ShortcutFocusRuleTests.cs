using Berth.Presentation;

namespace Berth.Tests.Presentation;

public sealed class ShortcutFocusRuleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_shortcut_acts_while_the_terminal_has_the_focus(bool notInTextBox)
    {
        Assert.False(ShortcutFocusRule.Allows(new ShortcutFocus(InWindowContent: true, InTextBox: false, InTerminal: true), notInTextBox));
    }

    [Fact]
    public void Shortcuts_act_in_the_page_and_with_nothing_focused()
    {
        Assert.True(ShortcutFocusRule.Allows(new ShortcutFocus(true, false, false), notInTextBox: true));
        Assert.True(ShortcutFocusRule.Allows(null, notInTextBox: true));
    }

    [Fact]
    public void A_dialog_or_menu_keeps_its_keys()
    {
        Assert.False(ShortcutFocusRule.Allows(new ShortcutFocus(false, false, false), notInTextBox: false));
    }

    [Fact]
    public void Delete_is_a_text_boxs_while_it_has_the_focus()
    {
        Assert.False(ShortcutFocusRule.Allows(new ShortcutFocus(true, true, false), notInTextBox: true));
        Assert.True(ShortcutFocusRule.Allows(new ShortcutFocus(true, true, false), notInTextBox: false));
    }
}
