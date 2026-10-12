using System.Collections.Generic;
using LumePDF.Pdf;
using LumePDF.Viewer;
using Xunit;

namespace LumePDF.Tests;

public class TextSelectionTests
{
    static PageText Plain(string text) => new(text, [], 0, 1);

    static TextSelection Select(int startPage, int start, int endPage, int end)
    {
        var s = new TextSelection();
        s.Begin(new(startPage, start), new(startPage, start));
        s.ExtendTo(new(endPage, end), new(endPage, end));
        return s;
    }

    [Fact]
    public void Positions_order_by_page_then_index()
    {
        Assert.True(new TextPosition(0, 99) < new TextPosition(1, 0));
        Assert.True(new TextPosition(2, 3) > new TextPosition(2, 1));
        Assert.True(new TextPosition(2, 3) <= new TextPosition(2, 3));
        Assert.True(new TextPosition(1, TextPosition.PageEnd) < new TextPosition(2, 0));
    }

    [Fact]
    public void A_click_is_an_empty_selection_that_anchors_the_next_drag()
    {
        var s = new TextSelection();
        s.Begin(new(0, 5), new(0, 5));
        Assert.True(s.IsEmpty);
        s.ExtendTo(new(0, 9), new(0, 9));
        Assert.Equal((new TextPosition(0, 5), new TextPosition(0, 9)), (s.Start, s.End));
    }

    [Fact]
    public void Dragging_backwards_selects_from_the_pointer_to_the_anchor()
    {
        var s = Select(3, 10, 1, 4);
        Assert.Equal(new TextPosition(1, 4), s.Start);
        Assert.Equal(new TextPosition(3, 10), s.End);
    }

    [Fact]
    public void A_double_click_drag_extends_by_whole_words_keeping_the_first_one()
    {
        var s = new TextSelection();
        s.Begin(new(0, 10), new(0, 15)); // the word double-clicked
        s.ExtendTo(new(0, 20), new(0, 24)); // word under the pointer, after it
        Assert.Equal((new TextPosition(0, 10), new TextPosition(0, 24)), (s.Start, s.End));
        s.ExtendTo(new(0, 2), new(0, 6)); // and before it
        Assert.Equal((new TextPosition(0, 2), new TextPosition(0, 15)), (s.Start, s.End));
        s.ExtendTo(new(0, 11), new(0, 11)); // back inside it
        Assert.Equal((new TextPosition(0, 10), new TextPosition(0, 15)), (s.Start, s.End));
    }

    [Fact]
    public void Each_page_gets_its_share_of_the_selection()
    {
        var s = Select(1, 5, 3, 7);
        Assert.Null(s.RangeOn(0, 100));
        Assert.Equal((5, 100), s.RangeOn(1, 100));
        Assert.Equal((0, 40), s.RangeOn(2, 40));
        Assert.Equal((0, 7), s.RangeOn(3, 100));
        Assert.Null(s.RangeOn(4, 100));
        Assert.Null(s.RangeOn(2, 0)); // a page without text
        Assert.Equal((0, 3), s.RangeOn(3, 3)); // index past a shorter text
    }

    [Fact]
    public void Select_all_covers_every_page_whatever_its_length()
    {
        var s = new TextSelection();
        s.SelectAll(3);
        Assert.Equal((0, 50), s.RangeOn(0, 50));
        Assert.Equal((0, 12), s.RangeOn(2, 12));
        Assert.Null(s.RangeOn(3, 12));
        s.SelectAll(0);
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Clear_empties_the_selection()
    {
        var s = Select(0, 1, 2, 3);
        s.Clear();
        Assert.True(s.IsEmpty);
        Assert.Null(s.RangeOn(0, 10));
    }

    // ───────────── Copied text ─────────────

    [Fact]
    public void Text_across_pages_keeps_order_and_puts_a_line_break_between_pages()
    {
        var pages = new List<PageText> { Plain("Pagina 1\r\nfine della prima  "), Plain("Pagina 2\r\ninizio"), Plain("Pagina 3") };
        var s = Select(0, 10, 2, 6);
        Assert.Equal("fine della prima\r\nPagina 2\r\ninizio\r\nPagina", s.GetText(pages));
    }

    [Fact]
    public void Pages_without_text_add_no_blank_lines()
    {
        var pages = new List<PageText> { Plain("uno"), PageText.Empty, Plain("tre") };
        var s = new TextSelection();
        s.SelectAll(3);
        Assert.Equal("uno\r\ntre", s.GetText(pages));
    }

    [Fact]
    public void A_page_ending_with_a_line_break_gets_no_extra_one()
    {
        var pages = new List<PageText> { Plain("uno\r\n"), Plain("due") };
        var s = new TextSelection();
        s.SelectAll(2);
        Assert.Equal("uno\r\ndue", s.GetText(pages));
    }

    [Fact]
    public void An_empty_selection_copies_nothing()
    {
        var s = new TextSelection();
        Assert.Equal("", s.GetText([Plain("abc")]));
        s.Begin(new(0, 2), new(0, 2));
        Assert.Equal("", s.GetText([Plain("abc")]));
        s.SelectAll(1);
        Assert.Equal("", s.GetText([PageText.Empty]));
    }

    [Fact]
    public void Whether_the_selection_has_text_is_unknown_until_its_pages_are_read()
    {
        var s = Select(0, 0, 1, 2);
        var loaded = new Dictionary<int, PageText> { [0] = Plain("   ") };
        Assert.Null(s.HasText(p => loaded.GetValueOrDefault(p)));
        loaded[1] = Plain("ab");
        Assert.True(s.HasText(p => loaded.GetValueOrDefault(p)));
        loaded[1] = Plain("  ");
        Assert.False(s.HasText(p => loaded.GetValueOrDefault(p)));
        Assert.False(new TextSelection().HasText(_ => Plain("abc")));
    }

    // ───────────── Click counting ─────────────

    [Fact]
    public void Quick_clicks_on_the_same_spot_count_up_to_three_then_start_over()
    {
        var c = new ClickCounter();
        Assert.Equal(1, c.Press(1000, 10, 10, 500, 4));
        Assert.Equal(2, c.Press(1200, 11, 10, 500, 4));
        Assert.Equal(3, c.Press(1400, 12, 11, 500, 4));
        Assert.Equal(1, c.Press(1600, 12, 11, 500, 4));
    }

    [Fact]
    public void Slow_or_distant_clicks_start_a_new_series()
    {
        var c = new ClickCounter();
        c.Press(1000, 10, 10, 500, 4);
        Assert.Equal(1, c.Press(1600, 10, 10, 500, 4));
        Assert.Equal(1, c.Press(1700, 30, 10, 500, 4));
        Assert.Equal(2, c.Press(1800, 30, 10, 500, 4));
    }
}
