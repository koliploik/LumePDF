using System.Collections.Generic;
using System.Threading;
using LumePDF.Pdf;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LumePDF.Viewer;

/// <summary>A Grid whose mouse cursor can be set from outside (UIElement.ProtectedCursor is protected).</summary>
public partial class CursorGrid : Grid
{
    public InputCursor? Cursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}

/// <summary>One page on screen: the rendered bitmap plus canvases for search highlights and the text selection.</summary>
public sealed partial class PageView : CursorGrid
{
    public Image Image { get; } = new() { Stretch = Stretch.Fill };
    public Canvas Overlay { get; } = new() { IsHitTestVisible = false };
    public Canvas Selection { get; } = new() { IsHitTestVisible = false };

    public int Index { get; set; } = -1;
    /// <summary>Pixel width of the bitmap currently shown (0 = none).</summary>
    public int RenderedWidth { get; set; }
    public CancellationTokenSource? RenderCts { get; set; }

    static readonly SolidColorBrush PaperBrush = new(Colors.White);

    public PageView()
    {
        Background = PaperBrush;
        BorderThickness = new Thickness(0);
        Children.Add(Image);
        Children.Add(Overlay);
        Children.Add(Selection);
    }

    /// <summary>Shows the selection rectangles (normalized to the page), reusing the shapes already there.</summary>
    public void ShowSelection(IReadOnlyList<NormRect> rects, double width, double height, Brush brush)
    {
        var shapes = Selection.Children;
        while (shapes.Count > rects.Count)
            shapes.RemoveAt(shapes.Count - 1);
        for (int i = 0; i < rects.Count; i++)
        {
            if (i == shapes.Count)
                shapes.Add(new Rectangle());
            var shape = (Rectangle)shapes[i];
            var r = rects[i];
            shape.Fill = brush;
            shape.Width = r.Width * width;
            shape.Height = r.Height * height;
            Canvas.SetLeft(shape, r.X * width);
            Canvas.SetTop(shape, r.Y * height);
        }
    }

    public void Reset()
    {
        RenderCts?.Cancel();
        RenderCts = null;
        Image.Source = null;
        RenderedWidth = 0;
        Overlay.Children.Clear();
        Selection.Children.Clear();
        Cursor = null;
        Index = -1;
    }
}

public sealed partial class PageViewFactory : IElementFactory
{
    readonly Stack<PageView> _pool = new();

    public UIElement GetElement(ElementFactoryGetArgs args) => _pool.Count > 0 ? _pool.Pop() : new PageView();

    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        if (args.Element is PageView view)
            _pool.Push(view);
    }
}
