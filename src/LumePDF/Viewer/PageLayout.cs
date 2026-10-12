using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace LumePDF.Viewer;

/// <summary>
/// Vertical, centered, virtualizing layout for pages of known size. Unlike StackLayout it
/// never estimates: every page offset comes exact from <see cref="PageGeometry"/>, so
/// "go to page" and zoom anchoring are precise.
/// </summary>
public sealed partial class PageLayout(PageGeometry geometry) : VirtualizingLayout
{
    int _first = -1, _last = -1;

    public void SetViewportWidth(double width)
    {
        if (geometry.SetViewportWidth(width))
            InvalidateMeasure();
    }

    /// <param name="scale">DIPs per PDF point.</param>
    public void SetSizes(IReadOnlyList<(double Width, double Height)> pages, double scale)
    {
        geometry.SetSizes(pages, scale);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        int n = Math.Min(context.ItemCount, geometry.Count);
        if (n == 0)
        {
            RecycleRange(context, _first, _last, 0, -1);
            _first = _last = -1;
            return new Size(0, 0);
        }

        var r = context.RealizationRect;
        int first = Math.Clamp(geometry.IndexAt(r.Top), 0, n - 1);
        int last = Math.Clamp(geometry.IndexAt(r.Bottom), first, n - 1);

        RecycleRange(context, _first, _last, first, last);
        for (int i = first; i <= last; i++)
            context.GetOrCreateElementAt(i).Measure(new Size(geometry.WidthOf(i), geometry.HeightOf(i)));

        _first = first;
        _last = last;
        return new Size(geometry.ExtentWidth, geometry.ContentHeight);
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
        for (int i = _first; i <= _last && i < context.ItemCount && i < geometry.Count; i++)
        {
            context.GetOrCreateElementAt(i).Arrange(
                new Rect(geometry.LeftOf(i), geometry.TopOf(i), geometry.WidthOf(i), geometry.HeightOf(i)));
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
