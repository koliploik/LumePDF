using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Lieve.Viewer;

/// <summary>
/// Vertical, centered, virtualizing layout for pages of known size. Unlike StackLayout it
/// never estimates: every page offset is exact, so "go to page" and zoom anchoring are precise.
/// </summary>
public sealed partial class PageLayout : VirtualizingLayout
{
    public const double Padding = 16;
    public const double Spacing = 12;

    double[] _tops = [];
    double[] _widths = [];
    double[] _heights = [];
    int _first = -1, _last = -1;

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

    public void SetViewportWidth(double width)
    {
        if (Math.Abs(width - _viewportWidth) < 0.5)
            return;
        _viewportWidth = width;
        InvalidateMeasure();
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
        InvalidateMeasure();
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

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        int n = Math.Min(context.ItemCount, _tops.Length);
        if (n == 0)
        {
            RecycleRange(context, _first, _last, 0, -1);
            _first = _last = -1;
            return new Size(0, 0);
        }

        var r = context.RealizationRect;
        int first = Math.Clamp(IndexAt(r.Top), 0, n - 1);
        int last = Math.Clamp(IndexAt(r.Bottom), first, n - 1);

        RecycleRange(context, _first, _last, first, last);
        for (int i = first; i <= last; i++)
            context.GetOrCreateElementAt(i).Measure(new Size(_widths[i], _heights[i]));

        _first = first;
        _last = last;
        return new Size(ExtentWidth, ContentHeight);
    }

    void RecycleRange(VirtualizingLayoutContext context, int oldFirst, int oldLast, int keepFirst, int keepLast)
    {
        if (oldFirst < 0)
            return;
        int max = Math.Min(oldLast, context.ItemCount - 1);
        for (int i = oldFirst; i <= max; i++)
            if (i < keepFirst || i > keepLast)
                context.RecycleElement(context.GetOrCreateElementAt(i));
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        if (_first < 0)
            return finalSize;
        for (int i = _first; i <= _last && i < context.ItemCount; i++)
        {
            context.GetOrCreateElementAt(i).Arrange(new Rect(LeftOf(i), _tops[i], _widths[i], _heights[i]));
        }
        return finalSize;
    }

    protected override void OnItemsChangedCore(VirtualizingLayoutContext context, object source, NotifyCollectionChangedEventArgs args)
    {
        // The repeater drops its elements on a reset; start realizing from scratch.
        _first = _last = -1;
        InvalidateMeasure();
    }
}
