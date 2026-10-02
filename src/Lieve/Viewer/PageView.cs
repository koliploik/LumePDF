using System.Collections.Generic;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Lieve.Viewer;

/// <summary>One page on screen: the rendered bitmap plus a canvas for search highlights.</summary>
public sealed partial class PageView : Grid
{
    public Image Image { get; } = new() { Stretch = Stretch.Fill };
    public Canvas Overlay { get; } = new() { IsHitTestVisible = false };

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
    }

    public void Reset()
    {
        RenderCts?.Cancel();
        RenderCts = null;
        Image.Source = null;
        RenderedWidth = 0;
        Overlay.Children.Clear();
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
