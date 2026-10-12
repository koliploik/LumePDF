using LumePDF.Viewer;
using Xunit;

namespace LumePDF.Tests;

public class PageMenuTests
{
    [Fact]
    public void Without_a_document_only_open_is_offered()
    {
        var state = new PageMenuState(false, true, true, true, true);
        Assert.False(state.ShowDocumentItems);
        Assert.False(state.CanCopy);
        Assert.False(state.CanSelectAll);
    }

    [Theory]
    // selection has text, document has text → copy, select all
    [InlineData(true, true, true, true)]
    [InlineData(false, true, false, true)]   // empty selection (a click)
    [InlineData(null, true, true, true)]     // selection over pages not read yet, in a text document
    [InlineData(null, null, false, false)]   // nothing known yet
    [InlineData(null, false, false, false)]  // scanned document after Ctrl+A
    [InlineData(false, false, false, false)] // scanned document
    [InlineData(false, null, false, false)]  // still checking whether there is text
    public void Copy_needs_selected_text_and_select_all_needs_text(bool? selection, bool? document, bool copy, bool selectAll)
    {
        var state = new PageMenuState(true, selection, document, true, true);
        Assert.True(state.ShowDocumentItems);
        Assert.Equal(copy, state.CanCopy);
        Assert.Equal(selectAll, state.CanSelectAll);
    }

    [Theory]
    [InlineData("consectetur", "consectetur")]
    [InlineData("  due   parole \t", "due parole")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("riga uno\r\nriga due", null)]
    [InlineData("a\nb", null)]
    public void The_selection_becomes_a_search_query_only_when_it_makes_one(string? selected, string? term) =>
        Assert.Equal(term, PageMenuState.SearchTerm(selected));

    [Fact]
    public void Very_long_selections_make_no_query()
    {
        Assert.Null(PageMenuState.SearchTerm(new string('x', 81)));
        Assert.Equal(new string('x', 80), PageMenuState.SearchTerm(new string('x', 80)));
    }

    [Fact]
    public void The_find_item_shows_the_query()
    {
        Assert.Equal("Cerca…", PageMenuState.FindLabel(null));
        Assert.Equal("Cerca «consectetur»", PageMenuState.FindLabel("consectetur"));
        string label = PageMenuState.FindLabel("una frase piuttosto lunga da cercare");
        Assert.StartsWith("Cerca «una frase piuttosto", label);
        Assert.EndsWith("…»", label);
        Assert.True(label.Length < 34);
    }
}
