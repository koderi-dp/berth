using System.Collections.Immutable;
using Berth.Engine;
using Berth.Engine.Models;

namespace Berth.Tests.Engine;

public sealed class AnsiTextStyleTests
{
    private const string Esc = "\u001b";

    private static TextSpan Span(int start, int length, AnsiColor? fore = null, AnsiColor? back = null, TextEffects effects = TextEffects.None) =>
        new(start, length, new TextStyle(fore ?? AnsiColor.Default, back ?? AnsiColor.Default, effects));

    private static AnsiColor Idx(int i) => AnsiColor.FromIndex(i);

    private static ImmutableArray<TextSpan> SpansOf(string text) => AnsiText.Style(text).Spans;

    [Fact]
    public void A_line_without_codes_is_returned_as_it_is_with_the_shared_empty_array()
    {
        const string text = "plain\ttext ü";

        var (styled, spans) = AnsiText.Style(text);

        Assert.Same(text, styled);
        Assert.True(spans.IsEmpty);
        Assert.True(spans == new LogLine(LogStream.Stdout, null, "x").Spans);
    }

    [Fact]
    public void A_line_with_only_non_style_codes_has_plain_text_and_no_spans()
    {
        var (text, spans) = AnsiText.Style($"a{Esc}[2Kb{Esc}[?25lc{Esc}]0;title\u0007d");

        Assert.Equal("abcd", text);
        Assert.Empty(spans);
    }

    [Fact]
    public void The_text_is_what_strip_gives()
    {
        var raw = $"{Esc}[1;32mINFO{Esc}[0m ready {Esc}[38;5;208mport{Esc}[39m 80\u0007{Esc}[m";

        Assert.Equal(AnsiText.Strip(raw), AnsiText.Style(raw).Text);
    }

    [Fact]
    public void A_colour_covers_the_text_up_to_the_reset()
    {
        var (text, spans) = AnsiText.Style($"a{Esc}[31mred{Esc}[0m b");

        Assert.Equal("ared b", text);
        Assert.Equal<TextSpan>([Span(1, 3, Idx(1))], spans);
    }

    [Fact]
    public void A_style_left_on_runs_to_the_end_of_the_text()
    {
        Assert.Equal<TextSpan>([Span(2, 3, Idx(2))], SpansOf($"ab{Esc}[32mend"));
    }

    [Fact]
    public void An_empty_sgr_is_a_reset()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(1))], SpansOf($"{Esc}[31ma{Esc}[mb"));
    }

    [Fact]
    public void Zero_resets_colours_and_effects()
    {
        Assert.Equal<TextSpan>(
            [Span(0, 1, Idx(1), Idx(4), TextEffects.Bold | TextEffects.Underline)],
            SpansOf($"{Esc}[1;4;31;44ma{Esc}[0mb"));
    }

    [Fact]
    public void Zero_inside_a_sequence_resets_what_came_before_it_and_keeps_what_follows()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(2))], SpansOf($"{Esc}[1;31;0;32ma"));
    }

    [Theory]
    [InlineData(1, TextEffects.Bold)]
    [InlineData(2, TextEffects.Dim)]
    [InlineData(3, TextEffects.Italic)]
    [InlineData(4, TextEffects.Underline)]
    public void An_effect_turns_on(int code, TextEffects effect)
    {
        Assert.Equal<TextSpan>([Span(0, 1, effects: effect)], SpansOf($"{Esc}[{code}ma"));
    }

    [Theory]
    [InlineData(22, 1)]
    [InlineData(22, 2)]
    [InlineData(23, 3)]
    [InlineData(24, 4)]
    public void An_off_code_turns_its_effect_off(int off, int on)
    {
        var spans = SpansOf($"{Esc}[{on}ma{Esc}[{off}mb");

        Assert.Equal(1, Assert.Single(spans).Length);
        Assert.Equal(0, spans[0].Start);
    }

    [Fact]
    public void Normal_intensity_turns_off_bold_and_dim_together_but_keeps_the_others()
    {
        Assert.Equal<TextSpan>(
            [
                Span(0, 1, effects: TextEffects.Bold | TextEffects.Dim | TextEffects.Italic | TextEffects.Underline),
                Span(1, 1, effects: TextEffects.Italic | TextEffects.Underline),
            ],
            SpansOf($"{Esc}[1;2;3;4ma{Esc}[22mb"));
    }

    [Fact]
    public void Italic_off_and_underline_off_each_turn_off_only_their_own_effect()
    {
        Assert.Equal<TextSpan>(
            [
                Span(0, 1, effects: TextEffects.Bold | TextEffects.Italic | TextEffects.Underline),
                Span(1, 1, effects: TextEffects.Bold | TextEffects.Underline),
                Span(2, 1, effects: TextEffects.Bold),
            ],
            SpansOf($"{Esc}[1;3;4ma{Esc}[23mb{Esc}[24mc"));
    }

    [Fact]
    public void Turning_off_an_effect_that_is_not_on_changes_nothing()
    {
        Assert.Empty(SpansOf($"{Esc}[22;23;24mplain"));
    }

    [Theory]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(37, 7)]
    [InlineData(90, 8)]
    [InlineData(91, 9)]
    [InlineData(97, 15)]
    public void A_foreground_code_picks_one_of_the_sixteen_colours(int code, int index)
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(index))], SpansOf($"{Esc}[{code}ma"));
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(42, 2)]
    [InlineData(47, 7)]
    [InlineData(100, 8)]
    [InlineData(104, 12)]
    [InlineData(107, 15)]
    public void A_background_code_picks_one_of_the_sixteen_colours(int code, int index)
    {
        Assert.Equal<TextSpan>([Span(0, 1, back: Idx(index))], SpansOf($"{Esc}[{code}ma"));
    }

    [Fact]
    public void Foreground_and_background_set_together()
    {
        Assert.Equal<TextSpan>([Span(0, 2, Idx(1), Idx(4))], SpansOf($"{Esc}[31;44mab"));
    }

    [Fact]
    public void The_default_foreground_code_keeps_the_background()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(1), Idx(4)), Span(1, 1, back: Idx(4))], SpansOf($"{Esc}[31;44ma{Esc}[39mb"));
    }

    [Fact]
    public void The_default_background_code_keeps_the_foreground()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(1), Idx(4)), Span(1, 1, Idx(1))], SpansOf($"{Esc}[31;44ma{Esc}[49mb"));
    }

    [Fact]
    public void Both_default_codes_end_the_span()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(1), Idx(4))], SpansOf($"{Esc}[31;44ma{Esc}[39;49mb"));
    }

    [Fact]
    public void Colours_from_the_256_palette_for_text_and_background()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(208), Idx(17))], SpansOf($"{Esc}[38;5;208;48;5;17ma"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void The_256_palette_ends_are_accepted(int index)
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(index))], SpansOf($"{Esc}[38;5;{index}ma"));
    }

    [Fact]
    public void A_256_index_over_255_is_ignored_but_its_parameters_are_consumed()
    {
        // The 256 is not read as a code of its own and the 1 after it still applies.
        Assert.Equal<TextSpan>([Span(0, 1, effects: TextEffects.Bold)], SpansOf($"{Esc}[38;5;256;1ma"));
    }

    [Fact]
    public void True_colour_for_text_and_background()
    {
        Assert.Equal<TextSpan>(
            [Span(0, 1, AnsiColor.FromRgb(255, 128, 0), AnsiColor.FromRgb(1, 2, 3))],
            SpansOf($"{Esc}[38;2;255;128;0;48;2;1;2;3ma"));
    }

    [Fact]
    public void A_true_colour_component_over_255_is_ignored_and_the_parameters_after_it_still_apply()
    {
        Assert.Equal<TextSpan>([Span(0, 1, effects: TextEffects.Bold)], SpansOf($"{Esc}[38;2;256;0;0;1ma"));
    }

    [Fact]
    public void A_cut_short_extended_colour_is_ignored()
    {
        Assert.Empty(SpansOf($"{Esc}[38;2;1;2ma"));
        Assert.Empty(SpansOf($"{Esc}[38;5ma"));
        Assert.Empty(SpansOf($"{Esc}[38ma"));
    }

    [Fact]
    public void An_unknown_extended_colour_kind_swallows_the_rest_of_the_sequence()
    {
        Assert.Empty(SpansOf($"{Esc}[38;7;1;31ma"));
    }

    [Fact]
    public void Codes_that_are_not_supported_are_ignored()
    {
        // 5 (blink), 7 (reverse), 9 (strike), 21 and 53 are not styles here; the 1 beside them is.
        Assert.Equal<TextSpan>([Span(0, 1, effects: TextEffects.Bold)], SpansOf($"{Esc}[5;7;9;21;53;1ma"));
        Assert.Empty(SpansOf($"{Esc}[7ma"));
    }

    [Fact]
    public void A_sequence_with_more_than_sixteen_parameters_is_ignored_whole()
    {
        Assert.Empty(SpansOf($"{Esc}[{string.Join(';', Enumerable.Repeat("1", 17))}ma"));
        Assert.Single(SpansOf($"{Esc}[{string.Join(';', Enumerable.Repeat("1", 16))}ma"));
    }

    [Fact]
    public void A_huge_number_does_not_overflow()
    {
        Assert.Empty(SpansOf($"{Esc}[99999999999999999999ma"));
    }

    [Fact]
    public void An_unterminated_sequence_at_the_end_is_dropped_and_changes_nothing()
    {
        var (text, spans) = AnsiText.Style($"ab{Esc}[31");

        Assert.Equal("ab", text);
        Assert.Empty(spans);
        Assert.Equal<TextSpan>([Span(0, 1, Idx(1))], SpansOf($"{Esc}[31ma{Esc}[32"));
    }

    [Fact]
    public void A_malformed_sequence_is_dropped_and_the_character_that_broke_it_is_text()
    {
        // ESC [ 3 space x: the final byte is x, so it ends there and is not an SGR.
        var (text, spans) = AnsiText.Style($"a{Esc}[3 xb");

        Assert.Equal("ab", text);
        Assert.Empty(spans);

        // ESC [ then a control character: the sequence ends before it and the control is then dropped as a control.
        Assert.Equal("ab", AnsiText.Style($"a{Esc}[\u0001b").Text);
    }

    [Fact]
    public void A_sequence_with_a_private_marker_or_a_different_final_is_not_an_sgr()
    {
        Assert.Empty(SpansOf($"{Esc}[?1mx"));
        Assert.Empty(SpansOf($"{Esc}[1;31Hx"));
        Assert.Empty(SpansOf($"{Esc}[31:mx"));
        Assert.Empty(SpansOf($"{Esc}(0x"));
    }

    [Fact]
    public void A_lone_escape_before_a_colour_code_is_dropped()
    {
        Assert.Equal<TextSpan>([Span(1, 1, Idx(1))], SpansOf($"a{Esc}{Esc}[31mb"));
    }

    [Fact]
    public void Spans_are_positions_in_the_plain_text_not_the_raw_text()
    {
        var (text, spans) = AnsiText.Style($"{Esc}[2K{Esc}[1mab\u0007{Esc}[0mc{Esc}[4md");

        Assert.Equal("abcd", text);
        Assert.Equal<TextSpan>([Span(0, 2, effects: TextEffects.Bold), Span(3, 1, effects: TextEffects.Underline)], spans);
    }

    [Fact]
    public void A_style_that_changes_where_no_text_follows_makes_no_span()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(2))], SpansOf($"{Esc}[31m{Esc}[32ma"));
    }

    [Fact]
    public void Adjacent_text_in_different_styles_makes_adjacent_spans()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(1)), Span(1, 1, Idx(2))], SpansOf($"{Esc}[31ma{Esc}[32mb"));
    }

    [Fact]
    public void A_code_that_does_not_change_the_style_does_not_split_the_span()
    {
        Assert.Equal<TextSpan>([Span(0, 4, Idx(1))], SpansOf($"{Esc}[31mab{Esc}[31mcd"));
        Assert.Equal<TextSpan>([Span(0, 4, Idx(1))], SpansOf($"{Esc}[31mab{Esc}[22mcd"));
    }

    [Fact]
    public void A_style_that_changes_and_changes_back_with_no_text_between_merges_into_one_span()
    {
        Assert.Equal<TextSpan>([Span(0, 2, effects: TextEffects.Bold)], SpansOf($"{Esc}[1ma{Esc}[31m{Esc}[39mb"));
    }

    [Fact]
    public void Plain_text_between_two_equal_styles_keeps_them_apart()
    {
        Assert.Equal<TextSpan>([Span(0, 1, Idx(1)), Span(2, 1, Idx(1))], SpansOf($"{Esc}[31ma{Esc}[0mb{Esc}[31mc"));
    }

    [Fact]
    public void Style_does_not_carry_over_from_an_earlier_text()
    {
        _ = AnsiText.Style($"{Esc}[31mred");

        Assert.Equal<TextSpan>([Span(0, 1, effects: TextEffects.Bold)], SpansOf($"{Esc}[1ma"));
        Assert.Empty(SpansOf($"{Esc}[2Kplain"));
    }

    [Fact]
    public void Spans_do_not_overlap_and_stay_in_range()
    {
        var (text, spans) = AnsiText.Style($"{Esc}[1ma{Esc}[31mb{Esc}[44mc{Esc}[0md{Esc}[4me{Esc}[mf");

        Assert.Equal("abcdef", text);
        var end = 0;
        foreach (var span in spans)
        {
            Assert.True(span.Start >= end);
            Assert.True(span.Length > 0);
            Assert.False(span.Style.IsPlain);
            end = span.Start + span.Length;
        }

        Assert.True(end <= text.Length);
    }

    [Fact]
    public void Tabs_stay_in_the_text_and_count_in_the_positions()
    {
        Assert.Equal<TextSpan>([Span(2, 1, Idx(1))], SpansOf($"a\t{Esc}[31mb"));
    }

    [Fact]
    public void Ansi_colour_values_are_kept_apart()
    {
        Assert.True(AnsiColor.Default.IsDefault);
        Assert.True(default(AnsiColor).IsDefault);
        Assert.False(Idx(0).IsDefault);
        Assert.Equal(0, Idx(0).Index);
        Assert.False(Idx(5).IsRgb);
        Assert.True(AnsiColor.FromRgb(0, 0, 0).IsRgb);
        Assert.Equal(0x123456, AnsiColor.FromRgb(0x12, 0x34, 0x56).Rgb);
        Assert.Equal(Idx(255), Idx(999));
        Assert.Equal(Idx(0), Idx(-4));
        Assert.Equal(AnsiColor.FromRgb(255, 0, 0), AnsiColor.FromRgb(300, -1, 0));
    }

    [Fact]
    public void The_plain_style_is_the_default_one()
    {
        Assert.True(TextStyle.Plain.IsPlain);
        Assert.True(default(TextStyle).IsPlain);
        Assert.False(new TextStyle(AnsiColor.Default, AnsiColor.Default, TextEffects.Dim).IsPlain);
    }
}
