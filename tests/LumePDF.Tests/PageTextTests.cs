using System;
using System.Collections.Generic;
using System.Text;
using LumePDF.Pdf;
using Xunit;

namespace LumePDF.Tests;

public class PageTextTests
{
    const double CharW = 0.01, LineH = 0.02, LineStep = 0.03, Left = 0.1, Top = 0.1;

    /// <summary>
    /// A page laid out like PDFium reports it: characters 0.01 wide on lines 0.02 tall, with a
    /// generated "\r\n" (no box) between lines. Boxes are upright, as PageText keeps them.
    /// </summary>
    static PageText Lines(int rotation, params string[] lines)
    {
        var text = new StringBuilder();
        var boxes = new List<NormRect>();
        for (int l = 0; l < lines.Length; l++)
        {
            if (l > 0)
            {
                text.Append("\r\n");
                boxes.Add(default);
                boxes.Add(default);
            }
            for (int c = 0; c < lines[l].Length; c++)
            {
                text.Append(lines[l][c]);
                boxes.Add(new NormRect(Left + c * CharW, Top + l * LineStep, CharW, LineH));
            }
        }
        return new PageText(text.ToString(), boxes.ToArray(), rotation, 1);
    }

    static PageText Lines(params string[] lines) => Lines(0, lines);

    /// <summary>A displayed point inside character <paramref name="col"/> of a line, at a fraction of its width.</summary>
    static (double X, double Y) At(int line, double col, int rotation = 0) =>
        PageText.ToDisplay(Left + col * CharW, Top + line * LineStep + LineH / 2, rotation);

    static PageText Plain(string text) => new(text, [], 0, 1);

    // ───────────── Rotation ─────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Upright_and_display_coordinates_convert_both_ways(int rotation)
    {
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.2, 0.7), (1.0, 0.5), (0.33, 1.0) })
        {
            var (dx, dy) = PageText.ToDisplay(x, y, rotation);
            var (ux, uy) = PageText.ToUpright(dx, dy, rotation);
            Assert.Equal(x, ux, 12);
            Assert.Equal(y, uy, 12);
        }
    }

    [Theory]
    [InlineData(0, 0.0, 0.0)]
    [InlineData(1, 1.0, 0.0)] // turned clockwise: the top-left corner goes top-right
    [InlineData(2, 1.0, 1.0)]
    [InlineData(3, 0.0, 1.0)]
    public void Rotation_moves_the_upright_top_left_corner_clockwise(int rotation, double x, double y) =>
        Assert.Equal((x, y), PageText.ToDisplay(0, 0, rotation));

    [Fact]
    public void Rectangles_rotate_with_the_page()
    {
        var upright = new NormRect(0.1, 0.2, 0.3, 0.1);
        AssertRect(new NormRect(0.7, 0.1, 0.1, 0.3), PageText.ToDisplay(upright, 1));
        AssertRect(new NormRect(0.6, 0.7, 0.3, 0.1), PageText.ToDisplay(upright, 2));
        AssertRect(new NormRect(0.2, 0.6, 0.1, 0.3), PageText.ToDisplay(upright, 3));
        for (int r = 0; r < 4; r++)
            AssertRect(upright, PageText.ToUpright(PageText.ToDisplay(upright, r), r));
    }

    static void AssertRect(NormRect expected, NormRect actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Width, actual.Width, 9);
        Assert.Equal(expected.Height, actual.Height, 9);
    }

    // ───────────── Hit testing ─────────────

    [Fact]
    public void The_caret_goes_before_or_after_the_character_pointed_at()
    {
        var page = Lines("abc", "def");
        Assert.Equal(1, page.CaretAt(At(0, 1.2).X, At(0, 1.2).Y));
        Assert.Equal(2, page.CaretAt(At(0, 1.8).X, At(0, 1.8).Y));
        Assert.Equal(6, page.CaretAt(At(1, 1.2).X, At(1, 1.2).Y)); // "abc\r\nd|ef"
    }

    [Fact]
    public void Beside_a_line_the_caret_goes_to_its_start_or_end()
    {
        var page = Lines("abc", "defgh");
        Assert.Equal(3, page.CaretAt(0.9, At(0, 0).Y)); // before the generated line break
        Assert.Equal(0, page.CaretAt(0.0, At(0, 0).Y));
        Assert.Equal(10, page.CaretAt(0.95, At(1, 0).Y));
        Assert.Equal(5, page.CaretAt(0.01, At(1, 0).Y));
    }

    [Fact]
    public void Above_all_text_is_the_start_of_the_page_and_below_it_the_end()
    {
        var page = Lines("abc", "def");
        Assert.Equal(0, page.CaretAt(0.5, 0.01));
        Assert.Equal(page.Length, page.CaretAt(0.5, 0.99));
        Assert.Equal(page.Length, page.CaretAt(0.5, 1.5)); // below the page, in the gap to the next one
        Assert.Equal(0, page.CaretAt(0.5, -0.2));
    }

    [Fact]
    public void Between_lines_the_nearer_line_wins()
    {
        var page = Lines("abc", "def");
        double gapTop = Top + LineH, gapBottom = Top + LineStep;
        Assert.InRange(page.CaretAt(Left + 0.2 * CharW, gapTop + 0.001), 0, 3);
        Assert.InRange(page.CaretAt(Left + 0.2 * CharW, gapBottom - 0.001), 5, 8);
    }

    [Fact]
    public void On_a_line_shared_by_two_columns_the_closer_column_wins()
    {
        // "ab" in a left column and "xy" in a right one, on the same baseline.
        var boxes = new[]
        {
            new NormRect(0.10, 0.1, 0.01, 0.02), new NormRect(0.11, 0.1, 0.01, 0.02), default, default,
            new NormRect(0.60, 0.1, 0.01, 0.02), new NormRect(0.61, 0.1, 0.01, 0.02),
        };
        var page = new PageText("ab\r\nxy", boxes, 0, 1);
        Assert.Equal(2, page.CaretAt(0.2, 0.11));
        Assert.Equal(4, page.CaretAt(0.55, 0.11));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Hit_testing_follows_the_page_rotation(int rotation)
    {
        var page = Lines(rotation, "abc", "def");
        var (x, y) = At(1, 2.7, rotation);
        Assert.Equal(8, page.CaretAt(x, y));
        Assert.Equal(7, page.CharAt(x, y));
        Assert.True(page.IsOverText(x, y));
        // Past the end of the first line, in display space.
        var (ex, ey) = PageText.ToDisplay(0.95, At(0, 0).Y, rotation);
        Assert.Equal(3, page.CaretAt(ex, ey));
    }

    [Fact]
    public void Only_points_on_text_are_over_text()
    {
        var page = Lines("abc");
        Assert.True(page.IsOverText(At(0, 1.5).X, At(0, 1.5).Y));
        Assert.Equal(1, page.CharAt(At(0, 1.5).X, At(0, 1.5).Y));
        Assert.False(page.IsOverText(0.5, 0.5));
        Assert.Equal(-1, page.CharAt(0.5, 0.5));
        Assert.False(page.IsOverText(0.02, At(0, 0).Y));
    }

    // ───────────── Words and lines ─────────────

    [Theory]
    [InlineData("il gatto nero", 4, "gatto")]
    [InlineData("il gatto nero", 0, "il")]
    [InlineData("l'albero dell’anno", 3, "l'albero")]
    [InlineData("l'albero dell’anno", 12, "dell’anno")]
    [InlineData("pi vale 3.14, circa", 9, "3.14")]
    [InlineData("pari a 1,5 litri", 8, "1,5")]
    [InlineData("fine. Poi", 4, ".")]
    [InlineData("a   b", 2, "   ")]
    [InlineData("snake_case ok", 2, "snake_case")]
    [InlineData("caffè e più", 3, "caffè")]
    public void A_double_click_picks_the_word_around_a_character(string text, int index, string word)
    {
        var page = Plain(text);
        var (start, end) = page.WordAt(index);
        Assert.Equal(word, text[start..end]);
    }

    [Fact]
    public void Words_stop_at_line_breaks_and_bounds_are_safe()
    {
        var page = Plain("uno\r\ndue");
        var (s, e) = page.WordAt(5);
        Assert.Equal("due", page.Text[s..e]);
        Assert.Equal((0, 0), page.WordAt(-3));
        Assert.Equal((8, 8), page.WordAt(42));
        Assert.Equal((0, 0), PageText.Empty.WordAt(0));
    }

    [Fact]
    public void A_triple_click_picks_the_line_without_its_break()
    {
        var page = Plain("prima riga\r\nseconda riga\r\nterza");
        Assert.Equal("seconda riga", page.Text[page.LineAt(15).Start..page.LineAt(15).End]);
        Assert.Equal("prima riga", page.Text[page.LineAt(0).Start..page.LineAt(0).End]);
        Assert.Equal("terza", page.Text[page.LineAt(page.Length - 1).Start..page.LineAt(page.Length - 1).End]);
    }

    // ───────────── Highlight rectangles ─────────────

    [Fact]
    public void A_selection_highlights_one_rectangle_per_line_spaces_included()
    {
        var page = Lines("ab cd", "efg");
        var rects = page.GetRects(0, page.Length);
        Assert.Equal(2, rects.Count);
        AssertRect(new NormRect(Left, Top, 5 * CharW, LineH), rects[0]);
        AssertRect(new NormRect(Left, Top + LineStep, 3 * CharW, LineH), rects[1]);
    }

    [Fact]
    public void Partial_lines_highlight_only_their_characters()
    {
        var page = Lines("ab cd", "efg");
        var rects = page.GetRects(3, 8); // "cd\r\nef"
        Assert.Equal(2, rects.Count);
        AssertRect(new NormRect(Left + 3 * CharW, Top, 2 * CharW, LineH), rects[0]);
        AssertRect(new NormRect(Left, Top + LineStep, 1 * CharW, LineH), rects[1]);
    }

    [Fact]
    public void Highlights_are_given_on_the_page_as_displayed()
    {
        var page = Lines(1, "abcd");
        var rect = Assert.Single(page.GetRects(0, 4));
        AssertRect(PageText.ToDisplay(new NormRect(Left, Top, 4 * CharW, LineH), 1), rect);
        Assert.True(rect.Height > rect.Width); // the line runs downward on a page turned clockwise
    }

    [Fact]
    public void No_highlight_without_selected_characters()
    {
        var page = Lines("abc");
        Assert.Empty(page.GetRects(1, 1));
        Assert.Empty(page.GetRects(2, 1));
        Assert.Empty(PageText.Empty.GetRects(0, 10));
    }

    // ───────────── Copied text ─────────────

    [Fact]
    public void Copied_text_uses_windows_line_breaks_without_trailing_spaces()
    {
        var page = Plain("uno  \r\ndue\ntre\rquattro");
        Assert.Equal("uno\r\ndue\r\ntre\r\nquattro", page.GetText(0, page.Length));
    }

    [Fact]
    public void Control_characters_are_dropped_but_tabs_kept()
    {
        var page = Plain("a\u0000b\u0002c\td￾e\u0085f");
        Assert.Equal("abc\tdef", page.GetText(0, page.Length));
    }

    [Fact]
    public void A_range_ending_inside_a_line_break_still_gives_one_break()
    {
        var page = Plain("ab\r\ncd");
        Assert.Equal("ab\r\n", page.GetText(0, 3));
        Assert.Equal("\r\ncd", page.GetText(3, 6));
    }

    [Fact]
    public void Text_ranges_are_clamped()
    {
        var page = Plain("abc");
        Assert.Equal("abc", page.GetText(-5, 100));
        Assert.Equal("", page.GetText(2, 2));
        Assert.Equal("", page.GetText(3, 1));
    }

    [Fact]
    public void A_page_without_text_has_nothing_to_offer()
    {
        var empty = PageText.Empty;
        Assert.Equal(0, empty.Length);
        Assert.False(empty.HasText);
        Assert.Equal(0, empty.CaretAt(0.5, 0.5));
        Assert.Equal(-1, empty.CharAt(0.5, 0.5));
        Assert.False(empty.IsOverText(0.5, 0.5));
        Assert.Equal("", empty.GetText(0, 10));
        Assert.False(Plain(" \r\n\t ").HasText);
        Assert.True(Plain(" x ").HasText);
    }

    [Fact]
    public void Boxes_must_match_the_characters() =>
        Assert.Throws<ArgumentException>(() => new PageText("abc", [default], 0, 1));
}
