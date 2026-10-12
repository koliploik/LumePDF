using System;
using System.Linq;
using LumePDF.Viewer;
using Xunit;

namespace LumePDF.Tests;

public class PageGeometryTests
{
    const double A4W = 595.28, A4H = 841.89;
    const double PointsToDip = 96.0 / 72.0;

    static PageGeometry A4(int pages, double zoom, double viewport = 1000)
    {
        var g = new PageGeometry();
        g.SetViewportWidth(viewport);
        g.SetSizes(Enumerable.Repeat((A4W, A4H), pages).ToArray(), zoom * PointsToDip);
        return g;
    }

    [Fact]
    public void Pages_are_stacked_exactly_with_padding_and_spacing()
    {
        var g = A4(3, 1);
        double h = Math.Round(A4H * PointsToDip);
        Assert.Equal(PageGeometry.Padding, g.TopOf(0));
        Assert.Equal(PageGeometry.Padding + h + PageGeometry.Spacing, g.TopOf(1));
        Assert.Equal(PageGeometry.Padding + 3 * h + 2 * PageGeometry.Spacing + PageGeometry.Padding, g.ContentHeight);
        Assert.Equal(1, g.IndexAt(g.TopOf(1)));
        Assert.Equal(0, g.IndexAt(g.TopOf(1) - 1));
        Assert.Equal(0, g.IndexAt(0));
    }

    [Fact]
    public void Narrow_pages_are_centered_and_wide_ones_start_at_the_padding()
    {
        var g = A4(1, 1, viewport: 1000);
        Assert.Equal(Math.Round((1000 - g.WidthOf(0)) / 2), g.LeftOf(0));
        g = A4(1, 3, viewport: 1000);
        Assert.Equal(PageGeometry.Padding, g.LeftOf(0));
        Assert.Equal(g.ContentWidth, g.ExtentWidth);
    }

    // ───────────── Opening position (bug: documents opened pages down) ─────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.1)]
    [InlineData(4.0)]
    public void A_document_opens_at_the_top_left_corner(double zoom)
    {
        var g = A4(120, zoom, viewport: 600);
        Assert.Equal((0.0, 0.0), g.OpeningOffset());
        Assert.Equal((0.0, 0.0), g.OpeningOffset(0));
    }

    [Fact]
    public void An_explicit_page_opens_at_that_page_still_at_the_left_edge()
    {
        var g = A4(120, 1);
        var (x, y) = g.OpeningOffset(11);
        Assert.Equal(0, x);
        Assert.Equal(g.TopOf(11) - PageGeometry.Spacing / 2, y);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_negative_page_means_the_top(int page) => Assert.Equal((0.0, 0.0), A4(10, 1).OpeningOffset(page));

    [Fact]
    public void A_page_past_the_end_opens_the_last_one()
    {
        var g = A4(10, 1);
        Assert.Equal(g.OffsetOf(9), g.OpeningOffset(500).Y);
    }

    [Fact]
    public void An_empty_document_opens_at_zero() => Assert.Equal((0.0, 0.0), new PageGeometry().OpeningOffset(3));

    [Fact]
    public void A_view_at_the_top_stays_there_when_a_provisional_layout_gets_its_real_size()
    {
        // What happened at launch: the document was laid out for a viewport of size zero (zoom
        // clamped to 10%), then re-fitted to the real window anchoring the middle of the view,
        // which on the tiny layout was page 4.
        var g = A4(120, 0.1, viewport: 0);
        var anchor = g.AnchorAt(0, 0, viewX: 559, viewY: 425, keepTop: true);
        g.SetViewportWidth(1118);
        g.SetSizes(Enumerable.Repeat((A4W, A4H), 120).ToArray(), 1.367 * PointsToDip);
        Assert.Equal((0.0, 0.0), g.OffsetFor(anchor, 1118));

        // Without keepTop (zooming around the pointer) the same anchor really lands pages down.
        var tiny = A4(120, 0.1, viewport: 0);
        var pointer = tiny.AnchorAt(0, 0, 559, 425, keepTop: false);
        Assert.Equal(3, pointer.Point.Page);
    }

    [Fact]
    public void Zoom_keeps_the_content_under_the_anchor_in_the_middle_of_a_document()
    {
        var g = A4(50, 1);
        double offsetY = g.TopOf(7) + 300;
        var anchor = g.AnchorAt(0, offsetY, viewX: 500, viewY: 200, keepTop: true);
        var before = g.ContentPointOf(anchor.Point);
        g.SetSizes(Enumerable.Repeat((A4W, A4H), 50).ToArray(), 2 * PointsToDip);
        var (x, y) = g.OffsetFor(anchor, 1000);
        var after = g.ContentPointOf(anchor.Point);
        Assert.Equal(7, anchor.Point.Page);
        Assert.False(anchor.AtTop);
        Assert.Equal(after.Y - 200, y, 6);
        Assert.Equal(Math.Clamp(after.X - 500, 0, g.ExtentWidth - 1000), x, 6);
        Assert.True(after.Y > before.Y);
    }

    [Fact]
    public void Zoom_offsets_are_never_negative_nor_past_the_extent()
    {
        var g = A4(2, 3, viewport: 800);
        var anchor = g.AnchorAt(5000, 0, 0, 0, keepTop: false);
        g.SetSizes(Enumerable.Repeat((A4W, A4H), 2).ToArray(), 0.5 * PointsToDip);
        var (x, y) = g.OffsetFor(anchor, 800);
        Assert.Equal(0, x);
        Assert.True(y >= 0);
    }

    // ───────────── Pointer mapping (selection) ─────────────

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(2.5)]
    public void Content_points_map_to_page_fractions_at_any_zoom(double zoom)
    {
        var g = A4(5, zoom);
        var p = new PagePoint(2, 0.25, 0.75);
        var (x, y) = g.ContentPointOf(p);
        var back = g.PointAt(x, y);
        Assert.Equal(2, back.Page);
        Assert.Equal(0.25, back.X, 9);
        Assert.Equal(0.75, back.Y, 9);
        Assert.Equal(g.LeftOf(2) + 0.25 * g.WidthOf(2), x, 9);
    }

    [Fact]
    public void Points_between_pages_belong_to_the_nearer_page()
    {
        var g = A4(3, 1);
        double gapTop = g.TopOf(0) + g.HeightOf(0);
        var upper = g.PointAt(g.LeftOf(0) + 10, gapTop + 2);
        var lower = g.PointAt(g.LeftOf(0) + 10, gapTop + PageGeometry.Spacing - 2);
        Assert.Equal(0, upper.Page);
        Assert.True(upper.Y > 1);
        Assert.Equal(1, lower.Page);
        Assert.True(lower.Y < 0);
    }

    [Fact]
    public void Points_in_the_margins_get_fractions_outside_the_page()
    {
        var g = A4(1, 1, viewport: 1200);
        var left = g.PointAt(0, g.TopOf(0) + 100);
        var above = g.PointAt(g.LeftOf(0) + 5, 0);
        Assert.True(left.X < 0);
        Assert.Equal(0, above.Page);
        Assert.True(above.Y < 0);
    }

    [Fact]
    public void Landscape_pages_use_their_own_size()
    {
        // A page with /Rotate 90 comes with its displayed size: wider than tall.
        var g = new PageGeometry();
        g.SetViewportWidth(1200);
        g.SetSizes([(612, 792), (792, 612)], PointsToDip);
        Assert.True(g.WidthOf(1) > g.HeightOf(1));
        var (x, y) = g.ContentPointOf(new PagePoint(1, 0.5, 0.5));
        Assert.Equal(new PagePoint(1, 0.5, 0.5), g.PointAt(x, y));
    }

    [Fact]
    public void An_empty_layout_has_no_page() => Assert.Equal(-1, new PageGeometry().PointAt(10, 10).Page);
}
