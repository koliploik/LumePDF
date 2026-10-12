using System;
using System.Collections.Generic;

namespace LumePDF.Viewer;

/// <summary>A point relative to a page: X and Y are fractions of its width and height (0..1 inside it).</summary>
public readonly record struct PagePoint(int Page, double X, double Y);

/// <summary>
/// Exact position and size of every page, in DIPs, plus the conversions between content
/// coordinates and pages. Plain math with no UI dependency, so it can be unit tested:
/// PageLayout realizes the pages from it, the window maps scroll and pointer positions through it.
/// </summary>
public sealed class PageGeometry
{
    public const double Padding = 16;
    public const double Spacing = 12;

    double[] _tops = [];
    double[] _widths = [];
    double[] _heights = [];
    double _viewportWidth;

    public double ContentWidth { get; private set; }
    public double ContentHeight { get; private set; }
    public int Count => _tops.Length;

    public double TopOf(int i) => _tops[i];
    public double WidthOf(int i) => _widths[i];
    public double HeightOf(int i) => _heights[i];

    /// <summary>Total width: the widest page, but never less than the viewport.</summary>
    public double ExtentWidth => Math.Max(ContentWidth, _viewportWidth);

    /// <summary>Pages that fit are centered in the viewport; wider ones start at the padding and scroll.</summary>
    public double LeftOf(int i) => Math.Max(Padding, Math.Round((_viewportWidth - _widths[i]) / 2));

    /// <returns>Whether the width changed enough to need a new layout pass.</returns>
    public bool SetViewportWidth(double width)
    {
        if (Math.Abs(width - _viewportWidth) < 0.5)
            return false;
        _viewportWidth = width;
        return true;
    }

    /// <param name="scale">DIPs per PDF point.</param>
    public void SetSizes(IReadOnlyList<(double Width, double Height)> pages, double scale)
    {
        int n = pages.Count;
        if (_tops.Length != n)
        {
            _tops = new double[n];
            _widths = new double[n];
            _heights = new double[n];
        }

        double y = Padding, maxWidth = 0;
        for (int i = 0; i < n; i++)
        {
            _widths[i] = Math.Max(1, Math.Round(pages[i].Width * scale));
            _heights[i] = Math.Max(1, Math.Round(pages[i].Height * scale));
            _tops[i] = y;
            y += _heights[i] + Spacing;
            maxWidth = Math.Max(maxWidth, _widths[i]);
        }
        ContentWidth = n == 0 ? 0 : maxWidth + 2 * Padding;
        ContentHeight = n == 0 ? 0 : y - Spacing + Padding;
    }

    /// <summary>Index of the page containing (or closest above) the vertical content offset.</summary>
    public int IndexAt(double y)
    {
        int lo = 0, hi = _tops.Length - 1;
        if (hi < 0)
            return -1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_tops[mid] <= y) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Vertical offset that shows a page at the top of the view: the very top of the document for the first one.</summary>
    public double OffsetOf(int index) => Count == 0 || index <= 0 ? 0 : _tops[Math.Min(index, Count - 1)] - Spacing / 2;

    /// <summary>
    /// Where a newly opened document starts: always the top-left corner, or the top of the page
    /// explicitly asked for (--page). Nothing from previous sessions or documents plays a part.
    /// </summary>
    public (double X, double Y) OpeningOffset(int page = 0) => (0, OffsetOf(page));

    /// <summary>
    /// The page under a content point, as fractions of its size. Points between pages belong to
    /// the nearer one and points outside a page get fractions below 0 or above 1, so dragging a
    /// selection through gaps and margins still lands on the closest text.
    /// </summary>
    public PagePoint PointAt(double x, double y)
    {
        int page = IndexAt(y);
        if (page < 0)
            return new PagePoint(-1, 0, 0);
        if (page + 1 < Count && y - (_tops[page] + _heights[page]) > _tops[page + 1] - y)
            page++;
        return new PagePoint(page, (x - LeftOf(page)) / _widths[page], (y - _tops[page]) / _heights[page]);
    }

    public (double X, double Y) ContentPointOf(PagePoint p) =>
        (LeftOf(p.Page) + p.X * _widths[p.Page], _tops[p.Page] + p.Y * _heights[p.Page]);

    /// <summary>
    /// Captures the content under a viewport point so that, after the page sizes change, the
    /// view can be scrolled to bring it back under the same point (see <see cref="OffsetFor"/>).
    /// </summary>
    /// <param name="keepTop">A view resting at the top stays at the top instead.</param>
    public ZoomAnchor AnchorAt(double offsetX, double offsetY, double viewX, double viewY, bool keepTop)
    {
        double y = offsetY + viewY;
        int page = Math.Max(0, IndexAt(y));
        // Pages narrower than the view stay centered, so the horizontal anchor is relative to the page too.
        var point = new PagePoint(page, (offsetX + viewX - LeftOf(page)) / _widths[page], (y - _tops[page]) / _heights[page]);
        return new ZoomAnchor(point, viewX, viewY, keepTop && offsetY < 0.5);
    }

    /// <summary>Scroll offsets that put the anchored content back under its viewport point.</summary>
    public (double X, double Y) OffsetFor(ZoomAnchor anchor, double viewportWidth)
    {
        var (x, y) = ContentPointOf(anchor.Point);
        x = Math.Clamp(x - anchor.ViewX, 0, Math.Max(0, ExtentWidth - viewportWidth));
        return (x, anchor.AtTop ? 0 : Math.Max(0, y - anchor.ViewY));
    }
}

public readonly record struct ZoomAnchor(PagePoint Point, double ViewX, double ViewY, bool AtTop);
