using System;
using System.Linq;
using System.Threading.Tasks;
using LumePDF.Pdf;
using LumePDF.Viewer;
using Xunit;

namespace LumePDF.Tests;

/// <summary>The text layer as PDFium reads it, on the sample and on generated pages.</summary>
public class PdfTextTests
{
    const double PointsToDip = 96.0 / 72.0;
    const string Hello = "BT /F1 24 Tf 72 720 Td (Hello world) Tj ET";

    static Task<PdfDocument> Open(string path) => PdfDocument.OpenAsync(path, null);

    static Task<PageText> Text(PdfDocument doc, int page, bool layout = true) =>
        doc.GetTextAsync(page, layout, TestContext.Current.CancellationToken);

    static (double X, double Y) Center(NormRect r) => (r.X + r.Width / 2, r.Y + r.Height / 2);

    // ───────────── Sample document ─────────────

    [Fact]
    public async Task Sample_text_comes_in_reading_order_with_spaces_and_line_breaks()
    {
        using var doc = await Open(TestPdf.Sample);
        var page = await Text(doc, 0);
        Assert.Equal(0, page.Rotation);
        Assert.True(page.HasText);
        Assert.StartsWith("Pagina 1\r\nipsum dolor sit amet consectetur", page.Text);
        var (start, end) = page.LineAt(10);
        Assert.Equal("ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt", page.Text[start..end]);
    }

    [Fact]
    public async Task Character_boxes_are_where_the_page_draws_them()
    {
        using var doc = await Open(TestPdf.Sample);
        var page = await Text(doc, 0);
        var (w, h) = doc.PageSizes[0];
        // "P" of "Pagina 1": loose box 60..78.7 x 746..777.2 in PDF points (origin bottom-left).
        var p = Assert.Single(page.GetRects(0, 1));
        Assert.Equal(60 / w, p.X, 3);
        Assert.Equal((h - 777.2) / h, p.Y, 3);
        Assert.Equal(18.7 / w, p.Width, 3);
        Assert.Equal(31.2 / h, p.Height, 3);
        // The generated line break after the title has no box.
        Assert.Equal("\r\n", page.Text[8..10]);
        Assert.Empty(page.GetRects(8, 10));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public async Task A_point_on_screen_finds_the_character_under_it_at_any_zoom(double zoom)
    {
        using var doc = await Open(TestPdf.Sample);
        var page = await Text(doc, 0);
        var geometry = new PageGeometry();
        geometry.SetViewportWidth(1200);
        geometry.SetSizes(doc.PageSizes, zoom * PointsToDip);
        int c = page.Text.IndexOf("consectetur", StringComparison.Ordinal);
        var (cx, cy) = Center(page.GetRects(c, c + 1)[0]);
        var (x, y) = geometry.ContentPointOf(new PagePoint(0, cx, cy));
        var hit = geometry.PointAt(x, y);
        Assert.Equal(0, hit.Page);
        Assert.Equal(c, page.CharAt(hit.X, hit.Y));
        Assert.Equal("consectetur", page.Text[page.WordAt(c).Start..page.WordAt(c).End]);
    }

    [Fact]
    public async Task Dragging_from_one_line_into_the_next_copies_both_parts()
    {
        using var doc = await Open(TestPdf.Sample);
        var page = await Text(doc, 0);
        int start = page.Text.IndexOf("ipsum", StringComparison.Ordinal);
        int end = page.Text.IndexOf("amet", page.LineAt(start).End, StringComparison.Ordinal) + 4;
        var selection = new TextSelection();
        selection.Begin(new(0, start), new(0, start));
        selection.ExtendTo(new(0, end), new(0, end));
        Assert.Equal("ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt\r\ndolor sit amet",
            selection.GetText([page]));
        Assert.Equal(2, page.GetRects(start, end).Count);
    }

    [Fact]
    public async Task Copying_across_pages_joins_them_with_one_line_break()
    {
        using var doc = await Open(TestPdf.Sample);
        var pages = new[] { await Text(doc, 0, layout: false), await Text(doc, 1, layout: false) };
        var selection = new TextSelection();
        selection.SelectAll(2);
        string text = selection.GetText(pages);
        Assert.StartsWith("Pagina 1\r\n", text);
        Assert.Contains("\r\nPagina 2\r\n", text);
        Assert.DoesNotContain("\r\n\r\n", text);
        Assert.DoesNotContain(" \r\n", text);
    }

    [Fact]
    public async Task Text_without_layout_is_the_same_text()
    {
        using var doc = await Open(TestPdf.Sample);
        var full = await Text(doc, 3);
        var plain = await Text(doc, 3, layout: false);
        Assert.Equal(full.Text, plain.Text);
        Assert.Empty(plain.GetRects(0, plain.Length));
        Assert.True(await doc.HasTextAsync(3, TestContext.Current.CancellationToken));
    }

    // ───────────── Rotated and cropped pages ─────────────

    // Where a PDF point of a US Letter page ends up on the page as displayed, per /Rotate.
    static (double X, double Y) Displayed(double x, double y, int rotation) => rotation switch
    {
        1 => (y / 792, x / 612),
        2 => ((612 - x) / 612, y / 792),
        3 => ((792 - y) / 792, (612 - x) / 612),
        _ => (x / 612, (792 - y) / 792),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Selection_follows_the_page_rotation(int degrees)
    {
        int rotation = degrees / 90;
        using var doc = await Open(TestPdf.Write(Hello, degrees));
        var page = await Text(doc, 0);
        Assert.Equal("Hello world", page.Text);
        Assert.Equal(rotation, page.Rotation);

        // "H" sits at x 72..89 and around the baseline at y 720 (center about 80.7, 726).
        var h = Assert.Single(page.GetRects(0, 1));
        var (hx, hy) = Center(h);
        var (ex, ey) = Displayed(80.7, 726, rotation);
        Assert.Equal(ex, hx, 1.5e-2);
        Assert.Equal(ey, hy, 1.5e-2);
        var (ux, uy) = Center(PageText.ToUpright(h, rotation));
        Assert.Equal(80.7 / 612, ux, 1.5e-2);
        Assert.Equal((792 - 726) / 792.0, uy, 1.5e-2);

        // From the screen: page laid out with its displayed size, zoomed, back to the character.
        var geometry = new PageGeometry();
        geometry.SetViewportWidth(1400);
        geometry.SetSizes(doc.PageSizes, 1.25 * PointsToDip);
        Assert.Equal(rotation % 2 == 1, geometry.WidthOf(0) > geometry.HeightOf(0));
        var (x, y) = geometry.ContentPointOf(new PagePoint(0, hx, hy));
        var hit = geometry.PointAt(x, y);
        Assert.Equal(0, page.CharAt(hit.X, hit.Y));

        // The selection highlight covers what the search highlights (which PDFium places).
        var word = Assert.Single(page.GetRects(0, 5));
        var search = (await doc.SearchPageAsync(0, "Hello", TestContext.Current.CancellationToken)).Single().Rects.Single();
        var (wx, wy) = Center(word);
        var (sx, sy) = Center(search);
        Assert.Equal(sx, wx, 1.5e-2);
        Assert.Equal(sy, wy, 1.5e-2);
        Assert.Equal(search.Width > search.Height, word.Width > word.Height);
    }

    [Fact]
    public async Task A_page_whose_box_does_not_start_at_zero_maps_from_its_corner()
    {
        using var doc = await Open(TestPdf.Write("BT /F1 24 Tf 172 820 Td (Hello world) Tj ET", mediaBox: "100 100 712 892"));
        var page = await Text(doc, 0);
        var (hx, hy) = Center(page.GetRects(0, 1)[0]);
        Assert.Equal(80.7 / 612, hx, 1.5e-2);
        Assert.Equal((792 - 726) / 792.0, hy, 1.5e-2);
    }

    // ───────────── No text ─────────────

    [Fact]
    public async Task A_page_without_text_selects_and_copies_nothing()
    {
        using var doc = await Open(TestPdf.Write("0.5 g 72 72 468 648 re f"));
        var page = await Text(doc, 0);
        Assert.False(page.HasText);
        Assert.Equal(0, page.Length);
        Assert.False(await doc.HasTextAsync(0, TestContext.Current.CancellationToken));
        Assert.Equal(0, page.CaretAt(0.5, 0.5));
        Assert.False(page.IsOverText(0.5, 0.5));
        var selection = new TextSelection();
        selection.SelectAll(1);
        Assert.Equal("", selection.GetText([page]));
        Assert.Empty(page.GetRects(0, 100));
    }
}
