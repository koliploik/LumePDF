using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using LumePDF.Pdf;
using LumePDF.Viewer;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI;

namespace LumePDF;

public sealed partial class MainWindow : Window
{
    const double PointsToDip = 96.0 / 72.0;
    const double MinZoom = 0.1, MaxZoom = 5.0;
    const double MaxRenderPixels = 16_000_000;
    static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5];

    enum FitMode { None = 0, Width = 1, Page = 2 }

    readonly PageGeometry _geometry = new();
    readonly PageLayout _layout;
    readonly Dictionary<int, PageView> _realized = [];
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _rerenderTimer;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;

    PdfDocument? _doc;
    double _zoom = 1; // 1 = actual size
    FitMode _fit = FitMode.Width;
    double _rasterScale = 1;
    int _currentPage;
    Size _fittedViewport;
    // Page a just opened document starts at, until the viewport has a size to lay it out in.
    int? _openAt;
    // Where our last ChangeView is heading; ScrollViewer offsets lag until the view settles.
    double? _pendingY;
    double? _pendingX;
    bool _scrollDeferred;

    // Search state
    static readonly SolidColorBrush HitBrush = new(Color.FromArgb(0x66, 0xFF, 0xD4, 0x00));
    static readonly SolidColorBrush CurrentHitBrush = new(Color.FromArgb(0x99, 0xFF, 0x7A, 0x00));
    CancellationTokenSource? _searchCts;
    string _searchQuery = "";
    bool _searchRunning;
    readonly List<SearchHit> _hits = [];
    readonly Dictionary<int, List<int>> _hitsByPage = [];
    int _currentHit = -1;

    // Text selection state
    readonly TextSelection _selection = new();
    // Text layers of the pages around the view, loaded on demand.
    readonly Dictionary<int, Task<PageText>> _texts = [];
    readonly ClickCounter _clicks = new();
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _autoScrollTimer;
    SolidColorBrush? _selectionBrush;
    InputCursor? _textCursor;
    bool _selecting;     // left button held over the pages
    bool _anchored;      // the press that started it has found its text
    int _selectUnit = 1; // 1 characters, 2 words, 3 lines
    int _pressId;
    Point _dragPoint;    // pointer while selecting, in viewport coordinates
    bool? _hasText;      // whether the document has any text; null until known
    CancellationTokenSource? _textScanCts;
    string? _findTerm;

    readonly Dictionary<TreeViewNode, int> _outlinePages = [];

    public MainWindow()
    {
        InitializeComponent();
        _layout = new PageLayout(_geometry);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "LumePDF.ico"));
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        WindowMinSize.Apply(WinRT.Interop.WindowNative.GetWindowHandle(this), 680, 400);
        SizeToWorkArea();

        Pages.ItemTemplate = new PageViewFactory();
        Pages.Layout = _layout;
        Pages.ElementPrepared += Pages_ElementPrepared;
        Pages.ElementClearing += Pages_ElementClearing;
        Scroller.LayoutUpdated += Scroller_LayoutUpdated;

        _rerenderTimer = DispatcherQueue.CreateTimer();
        _rerenderTimer.Interval = TimeSpan.FromMilliseconds(120);
        _rerenderTimer.IsRepeating = false;
        _rerenderTimer.Tick += (_, _) => RerenderVisible();

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(1);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => SaveRecent();

        _autoScrollTimer = DispatcherQueue.CreateTimer();
        _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _autoScrollTimer.IsRepeating = true;
        _autoScrollTimer.Tick += (_, _) => AutoScroll();

        Welcome.ContextFlyout = ContextMenu;
        Root.Loaded += Root_Loaded;
        Root.SizeChanged += (_, _) => AdaptHeader();
        Root.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        Closed += (_, _) =>
        {
            SaveRecent();
            _searchCts?.Cancel();
            _textScanCts?.Cancel();
            _doc?.Dispose();
        };

        RegisterShortcuts();
        ShowWelcome();
    }

    void SizeToWorkArea()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int height = (int)(area.Height * 0.88);
        int width = Math.Min(area.Width - 80, (int)(height * 1.25));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    void Root_Loaded(object sender, RoutedEventArgs e)
    {
        _rasterScale = Root.XamlRoot.RasterizationScale;
        Root.XamlRoot.Changed += (_, _) =>
        {
            UpdateCaptionInset();
            if (Root.XamlRoot.RasterizationScale != _rasterScale)
            {
                _rasterScale = Root.XamlRoot.RasterizationScale;
                _rerenderTimer.Start();
            }
        };
        UpdateCaptionInset();
        UpdateCaptionColors();
    }

    /// <summary>
    /// Keeps the header tools clear of the caption buttons: starting from the full toolbar,
    /// drops the least important pieces one at a time until it fits the space actually left.
    /// </summary>
    void AdaptHeader()
    {
        if (DocTools.Visibility != Visibility.Visible || Root.ActualWidth <= 0)
            return;

        SearchBox.Width = 240;
        foreach (var e in new UIElement[] { ZoomOutButton, ZoomInButton, Sep1, Sep2, PageCountText })
            e.Visibility = Visibility.Visible;
        // Counter and arrows only matter while searching; otherwise their room goes to the title.
        var searching = _searchQuery.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchStatus.Visibility = PrevHit.Visibility = NextHit.Visibility = searching;

        double available = Root.ActualWidth - Header.Padding.Left - LeftTools.ActualWidth
            - CaptionSpace.Width.Value - 48 /* drag area */ - 3 * Header.ColumnSpacing;
        Action[] compact =
        [
            () => SearchBox.Width = 170,
            () => Sep1.Visibility = Sep2.Visibility = Visibility.Collapsed,
            () => ZoomOutButton.Visibility = ZoomInButton.Visibility = Visibility.Collapsed,
            () => SearchBox.Width = 130,
            () => PageCountText.Visibility = Visibility.Collapsed,
            () => PrevHit.Visibility = NextHit.Visibility = Visibility.Collapsed,
            () => SearchBox.Width = 100,
        ];
        foreach (var step in compact)
        {
            DocTools.Measure(new Size(double.PositiveInfinity, 48));
            if (DocTools.DesiredSize.Width <= available)
                break;
            step();
        }
    }

    void UpdateCaptionInset()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        CaptionSpace.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / scale));
        AdaptHeader();
    }

    void UpdateCaptionColors()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var tb = AppWindow.TitleBar;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonHoverForegroundColor = tb.ButtonForegroundColor;
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0);
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x08, 0, 0, 0);
        tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A) : Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
    }

    // ───────────────────────────── Opening ─────────────────────────────

    /// <param name="page">Zero-based page to show (--page); otherwise the document opens at its very top.</param>
    public async Task OpenFileAsync(string path, string? password = null, int page = 0)
    {
        try
        {
            path = System.IO.Path.GetFullPath(path.Trim('"'));
        }
        catch
        {
            await ShowMessageAsync("Percorso non valido", path);
            return;
        }
        if (!File.Exists(path))
        {
            Recents.Remove(path);
            await ShowMessageAsync("File non trovato", path);
            return;
        }

        Busy.IsActive = true;
        PdfDocument doc;
        try
        {
            doc = await PdfDocument.OpenAsync(path, password);
        }
        catch (PdfPasswordException)
        {
            Busy.IsActive = false;
            string? pwd = await AskPasswordAsync(System.IO.Path.GetFileName(path), retry: password != null);
            if (pwd != null)
                await OpenFileAsync(path, pwd, page);
            return;
        }
        catch (Exception ex)
        {
            Busy.IsActive = false;
            await ShowMessageAsync("Impossibile aprire il file", ex.Message);
            return;
        }
        Busy.IsActive = false;

        CloseDocument();
        _doc = doc;

        // Only the zoom is remembered per file: the reading position is not.
        var recent = Recents.Get(path);
        _fit = recent != null ? (FitMode)recent.Fit : FitMode.Width;
        _zoom = recent?.Zoom ?? 1;

        string name = System.IO.Path.GetFileName(path);
        Title = $"{name} – LumePDF";
        TitleText.Text = name;
        PageCountText.Text = $"/ {doc.PageCount}";
        Welcome.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        DocTools.Visibility = Visibility.Visible;
        AdaptHeader();

        // Right after launch the window may not have been laid out yet. Fitting the zoom to a
        // viewport of size zero, then re-fitting around the middle of that view once the real
        // size arrived, used to open documents several pages down: lay out only a sized viewport.
        _openAt = Math.Clamp(page, 0, Math.Max(0, doc.PageCount - 1));
        Scroller.UpdateLayout();
        if (Scroller.ActualWidth > 0 && Scroller.ActualHeight > 0)
            ShowDocument();
        Scroller.Focus(FocusState.Programmatic);

        _ = DetectTextAsync(doc);
        await LoadOutlineAsync(doc);
    }

    /// <summary>Lays out the opened document and scrolls to where it starts.</summary>
    void ShowDocument()
    {
        if (_doc == null || _openAt is not int page)
            return;
        _openAt = null;
        _layout.SetViewportWidth(Scroller.ActualWidth);
        ApplyLayout();
        Pages.ItemsSource = Enumerable.Range(0, _doc.PageCount).ToList();
        Scroller.UpdateLayout();
        var (x, y) = _geometry.OpeningOffset(page);
        ScrollTo(x, y);
        _currentPage = page;
        UpdatePageIndicator();
        SaveRecent();
    }

    void CloseDocument()
    {
        if (_doc == null)
            return;
        SaveRecent();
        ClearSearch();
        _textScanCts?.Cancel();
        _hasText = null;
        EndSelecting();
        _selection.Clear();
        _texts.Clear();
        Pages.ItemsSource = null;
        _realized.Clear();
        _doc.Dispose();
        _doc = null;
        // Nothing of this document's view may carry over to the next one.
        _layout.SetSizes([], 1);
        _openAt = null;
        _currentPage = 0;
        _pendingX = _pendingY = null;
        _scrollDeferred = false;
        OutlineTree.RootNodes.Clear();
        _outlinePages.Clear();
        OutlineToggle.Visibility = Visibility.Collapsed;
        OutlineToggle.IsChecked = false;
        OutlineColumn.Width = new GridLength(0);
    }

    void ShowWelcome()
    {
        var recents = Recents.Existing();
        RecentList.ItemsSource = recents;
        RecentHeader.Visibility = recents.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = Visibility.Visible;
    }

    void SaveRecent()
    {
        if (_doc != null)
            Recents.Update(_doc.FilePath, _zoom, (int)_fit);
    }

    async void Open_Click(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    async Task PickAndOpenAsync()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
        picker.FileTypeFilter.Add(".pdf");
        var result = await picker.PickSingleFileAsync();
        if (result != null)
            await OpenFileAsync(result.Path);
    }

    async void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentFile recent)
            await OpenFileAsync(recent.Path);
    }

    void Root_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Apri";
        }
    }

    async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>()
            .FirstOrDefault(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (file != null)
            await OpenFileAsync(file.Path);
    }

    // ───────────────────────────── Pages & rendering ─────────────────────────────

    void Pages_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        var view = (PageView)args.Element;
        view.Reset();
        view.Index = args.Index;
        _realized[args.Index] = view;
        DrawHighlights(view);
        DrawSelection(view);
        RequestRender(view);
    }

    void Pages_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        var view = (PageView)args.Element;
        if (_realized.TryGetValue(view.Index, out var current) && current == view)
            _realized.Remove(view.Index);
        view.Reset();
    }

    (int Width, int Height) TargetPixels(int index)
    {
        double w = _geometry.WidthOf(index) * _rasterScale;
        double h = _geometry.HeightOf(index) * _rasterScale;
        double area = w * h;
        if (area > MaxRenderPixels)
        {
            double k = Math.Sqrt(MaxRenderPixels / area);
            w *= k;
            h *= k;
        }
        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    void RequestRender(PageView view)
    {
        if (_doc == null || view.Index < 0 || view.Index >= _geometry.Count)
            return;
        view.RenderCts?.Cancel();
        var cts = view.RenderCts = new CancellationTokenSource();
        var (w, h) = TargetPixels(view.Index);
        _ = RenderIntoAsync(view, _doc, view.Index, w, h, cts.Token);
    }

    async Task RenderIntoAsync(PageView view, PdfDocument doc, int index, int width, int height, CancellationToken ct)
    {
        RenderedPage page;
        try
        {
            page = await doc.RenderAsync(index, width, height, ct);
        }
        catch
        {
            return; // cancelled, document closed, or a broken page: keep the blank sheet
        }

        using (page)
        {
            if (ct.IsCancellationRequested || view.Index != index || doc != _doc)
                return;
            var bitmap = new WriteableBitmap(width, height);
            CopyPixels(page, bitmap);
            view.Image.Source = bitmap;
            view.RenderedWidth = width;
        }
    }

    static void CopyPixels(RenderedPage page, WriteableBitmap bitmap)
    {
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(page.Span);
        bitmap.Invalidate();
    }

    void RerenderVisible()
    {
        foreach (var view in _realized.Values)
            if (view.RenderedWidth != TargetPixels(view.Index).Width)
                RequestRender(view);
    }

    // ───────────────────────────── Zoom ─────────────────────────────

    double ComputeZoom()
    {
        if (_doc == null)
            return 1;
        double viewW = Math.Max(1, Scroller.ActualWidth - 2 * PageGeometry.Padding - 1);
        double viewH = Math.Max(1, Scroller.ActualHeight - 2 * PageGeometry.Padding);
        double fitW = viewW / (_doc.TypicalPageWidth * PointsToDip);
        double zoom = _fit switch
        {
            FitMode.Width => fitW,
            FitMode.Page => Math.Min(fitW, viewH / (_doc.TypicalPageHeight * PointsToDip)),
            _ => _zoom,
        };
        return Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    void ApplyLayout()
    {
        if (_doc == null)
            return;
        _zoom = ComputeZoom();
        _fittedViewport = new Size(Scroller.ActualWidth, Scroller.ActualHeight);
        _layout.SetSizes(_doc.PageSizes, _zoom * PointsToDip);
        ZoomText.Text = _fit switch
        {
            FitMode.Width => "Larghezza",
            FitMode.Page => "Pagina",
            _ => $"{Math.Round(_zoom * 100)}%",
        };
        foreach (var view in _realized.Values)
        {
            DrawHighlights(view);
            DrawSelection(view);
        }
        _rerenderTimer.Stop();
        _rerenderTimer.Start();
    }

    /// <summary>Changes zoom keeping the content under <paramref name="anchor"/> (viewport coords) still.</summary>
    /// <param name="keepTop">For re-fitting to a new window size: a view resting at the top stays there.</param>
    void SetZoom(FitMode fit, double zoom, Point? anchor = null, bool keepTop = false)
    {
        if (_doc == null || _geometry.Count == 0)
            return;

        var a = anchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        var keep = _geometry.AnchorAt(Scroller.HorizontalOffset, CurrentY, a.X, a.Y, keepTop);

        _fit = fit;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyLayout();
        Pages.InvalidateMeasure();
        Scroller.UpdateLayout();

        var (x, y) = _geometry.OffsetFor(keep, Scroller.ViewportWidth);
        ScrollTo(x, y);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void ZoomStep(int direction, Point? anchor = null)
    {
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, MaxZoom)
            : ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, MinZoom);
        SetZoom(FitMode.None, next, anchor);
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomStep(+1);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomStep(-1);

    void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as MenuFlyoutItem)?.Tag as string)
        {
            case "w": SetZoom(FitMode.Width, _zoom); break;
            case "p": SetZoom(FitMode.Page, _zoom); break;
            case string s when double.TryParse(s, CultureInfo.InvariantCulture, out double z): SetZoom(FitMode.None, z); break;
        }
    }

    void ScrollContent_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & VirtualKeyModifiers.Control) == 0 || _doc == null)
            return;
        var point = e.GetCurrentPoint(Scroller);
        int delta = point.Properties.MouseWheelDelta;
        double factor = Math.Pow(1.1, delta / 120.0);
        SetZoom(FitMode.None, _zoom * factor, point.Position);
        e.Handled = true;
    }

    void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ScrollContent.MinWidth = e.NewSize.Width;
        ScrollContent.MinHeight = e.NewSize.Height;
        _layout.SetViewportWidth(e.NewSize.Width);
        if (_openAt != null)
        {
            // A document opened before the window had a size: now there is one to lay it out in.
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                DispatcherQueue.TryEnqueue(ShowDocument);
            return;
        }
        if (_doc != null && _fit != FitMode.None && e.NewSize != _fittedViewport)
            SetZoom(_fit, _zoom, keepTop: true);
    }

    // ───────────────────────────── Navigation ─────────────────────────────

    void GoToPage(int index)
    {
        if (_doc == null || _geometry.Count == 0)
            return;
        index = Math.Clamp(index, 0, _geometry.Count - 1);
        ScrollTo(null, _geometry.OffsetOf(index));
        _currentPage = index;
        UpdatePageIndicator();
    }

    void ScrollTo(double? x, double? y)
    {
        _pendingX = x;
        _pendingY = y ?? _pendingY;
        // Right after opening or zooming, the ScrollViewer may not know the new extent yet and
        // would silently clamp the jump: wait for the layout pass that makes the target reachable.
        if (y is double target && target > Scroller.ScrollableHeight + 1 && target <= MaxScrollY + 1)
        {
            _scrollDeferred = true;
            return;
        }
        _scrollDeferred = false;
        Scroller.ChangeView(x, y, null, true);
    }

    double MaxScrollY => Math.Max(0, _geometry.ContentHeight - Scroller.ViewportHeight);

    void Scroller_LayoutUpdated(object? sender, object e)
    {
        if (_scrollDeferred && _pendingY is double y && Scroller.ScrollableHeight + 1 >= Math.Min(y, MaxScrollY))
        {
            _scrollDeferred = false;
            Scroller.ChangeView(_pendingX, y, null, true);
        }
    }

    double CurrentY => _pendingY ?? Scroller.VerticalOffset;

    void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate && !_scrollDeferred)
            _pendingY = null;
        UpdateCurrentPage();
        // While selecting, the content moving under the pointer moves the end of the selection.
        if (_selecting)
            ExtendSelection();
    }

    void UpdateCurrentPage()
    {
        if (_doc == null || _geometry.Count == 0)
            return;
        int page = Math.Max(0, _geometry.IndexAt(CurrentY + Scroller.ViewportHeight * 0.35));
        if (page != _currentPage)
        {
            _currentPage = page;
            UpdatePageIndicator();
        }
    }

    void UpdatePageIndicator()
    {
        if (PageBox.FocusState == FocusState.Unfocused)
            PageBox.Text = (_currentPage + 1).ToString(CultureInfo.CurrentCulture);
    }

    void PageBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            if (int.TryParse(PageBox.Text, out int n))
                GoToPage(n - 1);
            Scroller.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            Scroller.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    void PageBox_LostFocus(object sender, RoutedEventArgs e) =>
        PageBox.Text = (_currentPage + 1).ToString(CultureInfo.CurrentCulture);

    // ───────────────────────────── Outline ─────────────────────────────

    async Task LoadOutlineAsync(PdfDocument doc)
    {
        List<OutlineItem> items;
        try
        {
            items = await doc.GetOutlineAsync();
        }
        catch
        {
            return;
        }
        if (doc != _doc || items.Count == 0)
            return;

        foreach (var item in items)
            OutlineTree.RootNodes.Add(BuildNode(item));
        OutlineToggle.Visibility = Visibility.Visible;
    }

    TreeViewNode BuildNode(OutlineItem item)
    {
        var node = new TreeViewNode { Content = item.Title };
        _outlinePages[node] = item.Page;
        foreach (var child in item.Children)
            node.Children.Add(BuildNode(child));
        return node;
    }

    void OutlineToggle_Click(object sender, RoutedEventArgs e) =>
        OutlineColumn.Width = new GridLength(OutlineToggle.IsChecked == true ? 280 : 0);

    void OutlineTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && _outlinePages.TryGetValue(node, out int page) && page >= 0)
            GoToPage(page);
    }

    // ───────────────────────────── Search ─────────────────────────────

    void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Enter)
        {
            string query = SearchBox.Text.Trim();
            if (query.Length == 0)
                ClearSearch();
            else if (query != _searchQuery)
                StartSearch(query);
            else
                MoveHit(shift ? -1 : +1);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            ClearSearch();
            SearchBox.Text = "";
            Scroller.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    public void Search(string query)
    {
        SearchBox.Text = query;
        StartSearch(query.Trim());
    }

    async void StartSearch(string query)
    {
        var doc = _doc;
        if (doc == null)
            return;

        ClearSearch();
        _searchQuery = query;
        _searchRunning = true;
        var cts = _searchCts = new CancellationTokenSource();
        int startPage = _currentPage;
        UpdateSearchStatus();

        for (int i = 0; i < doc.PageCount; i++)
        {
            List<SearchHit> found;
            try
            {
                found = await doc.SearchPageAsync(i, query, cts.Token);
            }
            catch
            {
                return;
            }
            if (cts.IsCancellationRequested || doc != _doc)
                return;
            if (found.Count == 0)
                continue;

            var indexes = new List<int>(found.Count);
            foreach (var hit in found)
            {
                indexes.Add(_hits.Count);
                _hits.Add(hit);
            }
            _hitsByPage[i] = indexes;
            if (_realized.TryGetValue(i, out var view))
                DrawHighlights(view);

            // Jump to the first match at or after the page the user was reading.
            if (_currentHit < 0 && i >= startPage)
                SelectHit(indexes[0]);
            else
                UpdateSearchStatus();
        }

        _searchRunning = false;
        if (_currentHit < 0 && _hits.Count > 0)
            SelectHit(0);
        UpdateSearchStatus();
    }

    void ClearSearch()
    {
        _searchCts?.Cancel();
        _searchCts = null;
        _searchRunning = false;
        _searchQuery = "";
        _hits.Clear();
        _hitsByPage.Clear();
        _currentHit = -1;
        foreach (var view in _realized.Values)
            view.Overlay.Children.Clear();
        UpdateSearchStatus();
    }

    void UpdateSearchStatus()
    {
        bool any = _hits.Count > 0;
        PrevHit.IsEnabled = NextHit.IsEnabled = any;
        if (_searchQuery.Length == 0)
            SearchStatus.Text = "";
        else if (!any)
            SearchStatus.Text = _searchRunning ? "Ricerca…" : "Nessun risultato";
        else
            SearchStatus.Text = $"{_currentHit + 1} / {_hits.Count}{(_searchRunning ? "+" : "")}";
        AdaptHeader();
    }

    void MoveHit(int direction)
    {
        if (_hits.Count == 0)
            return;
        int next = _currentHit < 0 ? 0 : (_currentHit + direction + _hits.Count) % _hits.Count;
        SelectHit(next);
    }

    void PrevHit_Click(object sender, RoutedEventArgs e) => MoveHit(-1);
    void NextHit_Click(object sender, RoutedEventArgs e) => MoveHit(+1);

    void SelectHit(int index)
    {
        int oldPage = _currentHit >= 0 ? _hits[_currentHit].Page : -1;
        _currentHit = index;
        var hit = _hits[index];
        if (oldPage >= 0 && oldPage != hit.Page && _realized.TryGetValue(oldPage, out var oldView))
            DrawHighlights(oldView);
        if (_realized.TryGetValue(hit.Page, out var view))
            DrawHighlights(view);
        UpdateSearchStatus();
        ScrollToHit(hit);
    }

    void ScrollToHit(SearchHit hit)
    {
        if (hit.Page >= _geometry.Count)
            return; // not laid out yet (opened with --search before the window had a size)
        var r = hit.Rects[0];
        double top = _geometry.TopOf(hit.Page) + r.Y * _geometry.HeightOf(hit.Page);
        double bottom = top + r.Height * _geometry.HeightOf(hit.Page);
        double viewTop = CurrentY, viewBottom = viewTop + Scroller.ViewportHeight;
        double? y = top < viewTop + 24 || bottom > viewBottom - 24 ? top - Scroller.ViewportHeight / 3 : null;

        double? x = null;
        if (Scroller.ExtentWidth > Scroller.ViewportWidth)
        {
            double left = _geometry.LeftOf(hit.Page) + r.X * _geometry.WidthOf(hit.Page);
            double right = left + r.Width * _geometry.WidthOf(hit.Page);
            if (left < Scroller.HorizontalOffset || right > Scroller.HorizontalOffset + Scroller.ViewportWidth)
                x = left - Scroller.ViewportWidth / 3;
        }
        if (x != null || y != null)
            ScrollTo(x is { } xv ? Math.Max(0, xv) : null, y is { } yv ? Math.Max(0, yv) : null);
    }

    void DrawHighlights(PageView view)
    {
        view.Overlay.Children.Clear();
        if (view.Index < 0 || !_hitsByPage.TryGetValue(view.Index, out var indexes))
            return;
        double w = _geometry.WidthOf(view.Index), h = _geometry.HeightOf(view.Index);
        foreach (int i in indexes)
        {
            var brush = i == _currentHit ? CurrentHitBrush : HitBrush;
            foreach (var r in _hits[i].Rects)
            {
                var rect = new Rectangle
                {
                    Width = Math.Max(2, r.Width * w + 2),
                    Height = Math.Max(2, r.Height * h + 2),
                    Fill = brush,
                    RadiusX = 2,
                    RadiusY = 2,
                };
                Canvas.SetLeft(rect, r.X * w - 1);
                Canvas.SetTop(rect, r.Y * h - 1);
                view.Overlay.Children.Add(rect);
            }
        }
    }

    // ───────────────────────────── Text selection ─────────────────────────────

    [LibraryImport("user32.dll")]
    private static partial uint GetDoubleClickTime();

    InputCursor TextCursor => _textCursor ??= InputSystemCursor.Create(InputSystemCursorShape.IBeam);

    SolidColorBrush SelectionBrush => _selectionBrush ??= new SolidColorBrush(
        Application.Current.Resources.TryGetValue("SystemAccentColor", out var c) && c is Color accent
            ? Color.FromArgb(0x60, accent.R, accent.G, accent.B)
            : Color.FromArgb(0x60, 0x00, 0x78, 0xD4));

    /// <summary>The text layer of a page, read once and kept while the page is around.</summary>
    Task<PageText> TextOf(int page)
    {
        if (!_texts.TryGetValue(page, out var task))
        {
            if (_texts.Count >= 16)
                foreach (int old in _texts.Keys.Where(k => !_realized.ContainsKey(k)).ToList())
                    _texts.Remove(old);
            _texts[page] = task = LoadTextAsync(_doc!, page);
        }
        return task;
    }

    async Task<PageText> LoadTextAsync(PdfDocument doc, int page)
    {
        PageText text;
        try
        {
            text = await doc.GetTextAsync(page, layout: true);
        }
        catch
        {
            text = PageText.Empty; // document closed, or a broken page: nothing to select
        }
        if (doc == _doc && text.HasText)
            _hasText = true;
        return text;
    }

    PageText? LoadedText(int page) =>
        _texts.TryGetValue(page, out var task) && task.IsCompletedSuccessfully ? task.Result : null;

    /// <summary>Finds out in the background whether the document has any text; "Seleziona tutto" depends on it.</summary>
    async Task DetectTextAsync(PdfDocument doc)
    {
        var cts = _textScanCts = new CancellationTokenSource();
        for (int i = 0; i < doc.PageCount && _hasText == null; i++)
        {
            bool found;
            try
            {
                found = await doc.HasTextAsync(i, cts.Token);
            }
            catch
            {
                return;
            }
            if (cts.IsCancellationRequested || doc != _doc)
                return;
            if (found)
                _hasText = true;
        }
        _hasText ??= false;
    }

    void ScrollContent_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ScrollContent);
        // Touch and pen keep panning; the right button opens the menu and leaves the selection alone.
        if (_doc == null || _geometry.Count == 0 || e.Pointer.PointerDeviceType != PointerDeviceType.Mouse
            || !point.Properties.IsLeftButtonPressed)
            return;

        Scroller.Focus(FocusState.Pointer);
        var hit = _geometry.PointAt(point.Position.X, point.Position.Y);
        int clicks = _clicks.Press((long)(point.Timestamp / 1000), point.Position.X, point.Position.Y, GetDoubleClickTime(), 4);
        bool extend = clicks == 1 && (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        _selectUnit = clicks;
        _selecting = ScrollContent.CapturePointer(e.Pointer);
        _anchored = false;
        _dragPoint = e.GetCurrentPoint(Scroller).Position;
        ScrollContent.Cursor = TextCursor;
        _ = BeginSelectionAsync(hit, extend, ++_pressId);
        e.Handled = true;
    }

    async Task BeginSelectionAsync(PagePoint hit, bool extend, int press)
    {
        var doc = _doc;
        var text = LoadedText(hit.Page) ?? await TextOf(hit.Page);
        if (doc != _doc || press != _pressId)
            return;
        var (start, end) = UnitAt(hit, text);
        if (extend)
            _selection.ExtendTo(start, end);
        else
            _selection.Begin(start, end);
        _anchored = true;
        foreach (var view in _realized.Values)
            DrawSelection(view);
        ExtendSelection(); // the pointer may have moved while the text was loading
    }

    /// <summary>The character position, word or line (depending on the click count) under a point.</summary>
    (TextPosition Start, TextPosition End) UnitAt(PagePoint hit, PageText text)
    {
        int caret = text.CaretAt(hit.X, hit.Y);
        var (start, end) = (caret, caret);
        if (_selectUnit > 1 && text.CharAt(hit.X, hit.Y) is int i and >= 0)
            (start, end) = _selectUnit == 2 ? text.WordAt(i) : text.LineAt(i);
        return (new(hit.Page, start), new(hit.Page, end));
    }

    void ExtendSelection()
    {
        if (!_selecting || !_anchored || _geometry.Count == 0)
            return;
        var hit = _geometry.PointAt(_dragPoint.X + Scroller.HorizontalOffset, _dragPoint.Y + CurrentY);
        if (LoadedText(hit.Page) is not { } text)
        {
            _ = ExtendWhenLoadedAsync(hit.Page);
            return;
        }
        var (start, end) = UnitAt(hit, text);
        _selection.ExtendTo(start, end);
        foreach (var view in _realized.Values)
            DrawSelection(view);
    }

    async Task ExtendWhenLoadedAsync(int page)
    {
        var doc = _doc;
        await TextOf(page);
        if (doc == _doc)
            ExtendSelection();
    }

    void ScrollContent_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_doc == null || _geometry.Count == 0)
            return;
        if (_selecting)
        {
            _dragPoint = e.GetCurrentPoint(Scroller).Position;
            ExtendSelection();
            if (AutoScrollStep() != default)
                _autoScrollTimer.Start();
        }
        else if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            UpdateTextCursor(e.GetCurrentPoint(ScrollContent).Position);
        }
    }

    void ScrollContent_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_selecting)
            return;
        EndSelecting();
        ScrollContent.ReleasePointerCapture(e.Pointer);
        UpdateTextCursor(e.GetCurrentPoint(ScrollContent).Position);
        e.Handled = true;
    }

    void ScrollContent_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndSelecting();

    void EndSelecting()
    {
        _selecting = false;
        _autoScrollTimer.Stop();
        ScrollContent.Cursor = null;
    }

    /// <summary>I-beam over text, arrow elsewhere, as in a browser.</summary>
    void UpdateTextCursor(Point p)
    {
        var hit = _geometry.PointAt(p.X, p.Y);
        if (hit.Page < 0 || !_realized.TryGetValue(hit.Page, out var view))
            return;
        if (LoadedText(hit.Page) is { } text)
            view.Cursor = text.IsOverText(hit.X, hit.Y) ? TextCursor : null;
        else
            _ = TextOf(hit.Page); // ready by the next move
    }

    // Holding the pointer at or past an edge of the view while selecting scrolls toward it,
    // faster the further out it is.
    (double X, double Y) AutoScrollStep()
    {
        const double Edge = 8;
        static double Speed(double beyond) => beyond <= 0 ? 0 : Math.Min(80, 4 + beyond / 2);
        var p = _dragPoint;
        double dy = Speed(p.Y - (Scroller.ViewportHeight - Edge)) - Speed(Edge - p.Y);
        double dx = Scroller.ScrollableWidth > 0 ? Speed(p.X - (Scroller.ViewportWidth - Edge)) - Speed(Edge - p.X) : 0;
        return (dx, dy);
    }

    void AutoScroll()
    {
        var (dx, dy) = AutoScrollStep();
        if (!_selecting || (dx == 0 && dy == 0))
        {
            _autoScrollTimer.Stop();
            return;
        }
        double? x = dx == 0 ? null : Math.Clamp(Scroller.HorizontalOffset + dx, 0, Scroller.ScrollableWidth);
        ScrollTo(x, Math.Clamp(CurrentY + dy, 0, MaxScrollY));
        ExtendSelection();
    }

    void DrawSelection(PageView view)
    {
        int page = view.Index;
        if (page < 0 || _selection.IsEmpty || page < _selection.Start.Page || page > _selection.End.Page)
        {
            if (view.Selection.Children.Count > 0)
                view.Selection.Children.Clear();
            return;
        }
        if (LoadedText(page) is not { } text)
        {
            _ = RedrawSelectionWhenLoadedAsync(page);
            return;
        }
        IReadOnlyList<NormRect> rects = _selection.RangeOn(page, text.Length) is (int start, int end) ? text.GetRects(start, end) : [];
        view.ShowSelection(rects, _geometry.WidthOf(page), _geometry.HeightOf(page), SelectionBrush);
    }

    async Task RedrawSelectionWhenLoadedAsync(int page)
    {
        var doc = _doc;
        await TextOf(page);
        if (doc == _doc && _realized.TryGetValue(page, out var view))
            DrawSelection(view);
    }

    void SelectAll()
    {
        if (_doc == null || _hasText == false)
            return;
        _selection.SelectAll(_doc.PageCount);
        foreach (var view in _realized.Values)
            DrawSelection(view);
    }

    /// <summary>The selected text if all of its pages are at hand; null otherwise.</summary>
    string? SelectedTextIfLoaded()
    {
        if (_selection.IsEmpty)
            return null;
        var pages = new List<PageText>();
        for (int page = _selection.Start.Page; page <= _selection.End.Page; page++)
        {
            if (LoadedText(page) is not { } text)
                return null;
            pages.Add(text);
        }
        return _selection.GetText(pages);
    }

    async void CopySelection()
    {
        var doc = _doc;
        if (doc == null || _selection.IsEmpty)
            return;
        // The selection may change while far pages are read: copy the one that was asked for.
        var selection = new TextSelection();
        selection.Begin(_selection.Start, _selection.End);
        var pages = new List<PageText>();
        bool slow = selection.End.Page - selection.Start.Page > 20;
        if (slow)
            Busy.IsActive = true;
        try
        {
            for (int page = selection.Start.Page; page <= selection.End.Page; page++)
                pages.Add(LoadedText(page) ?? await doc.GetTextAsync(page, layout: false));
        }
        catch
        {
            return; // document closed meanwhile
        }
        finally
        {
            if (slow)
                Busy.IsActive = false;
        }
        string text = selection.GetText(pages);
        if (doc != _doc || text.Length == 0)
            return;
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush(); // keep it after LumePDF closes
        }
        catch
        {
            // Another program is holding the clipboard: nothing sensible to do.
        }
    }

    // ───────────────────────────── Context menu ─────────────────────────────

    void ContextMenu_Opening(object? sender, object e)
    {
        var state = new PageMenuState(_doc != null, _selection.HasText(LoadedText), _hasText,
            CanZoomIn: _zoom < MaxZoom - 0.001, CanZoomOut: _zoom > MinZoom + 0.001);
        foreach (var item in ContextMenu.Items)
            if (item != OpenItem)
                item.Visibility = state.ShowDocumentItems ? Visibility.Visible : Visibility.Collapsed;
        CopyItem.IsEnabled = state.CanCopy;
        SelectAllItem.IsEnabled = state.CanSelectAll;
        ZoomInItem.IsEnabled = state.CanZoomIn;
        ZoomOutItem.IsEnabled = state.CanZoomOut;
        _findTerm = PageMenuState.SearchTerm(SelectedTextIfLoaded());
        FindItem.Text = PageMenuState.FindLabel(_findTerm);
    }

    void ContextMenu_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as MenuFlyoutItem)?.Tag as string)
        {
            case "copy": CopySelection(); break;
            case "all": SelectAll(); break;
            case "find":
                if (_findTerm != null)
                    Search(_findTerm);
                FocusSearch();
                break;
            case "in": ZoomStep(+1); break;
            case "out": ZoomStep(-1); break;
            case "w": SetZoom(FitMode.Width, _zoom); break;
            case "p": SetZoom(FitMode.Page, _zoom); break;
            case "first": GoToPage(0); break;
            case "last": GoToPage(int.MaxValue); break;
            case "page": FocusPageBox(); break;
            case "open": _ = PickAndOpenAsync(); break;
        }
    }

    // ───────────────────────────── Shortcuts ─────────────────────────────

    void RegisterShortcuts()
    {
        const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers Shift = VirtualKeyModifiers.Shift;
        const VirtualKeyModifiers None = VirtualKeyModifiers.None;

        Add(VirtualKey.O, Ctrl, () => _ = PickAndOpenAsync());
        Add(VirtualKey.F, Ctrl, FocusSearch);
        Add(VirtualKey.F3, None, () => MoveHit(+1));
        Add(VirtualKey.F3, Shift, () => MoveHit(-1));
        Add(VirtualKey.G, Ctrl, FocusPageBox);
        // Ctrl+C and Ctrl+A are handled in Scroller_KeyDown instead: as window-wide accelerators
        // they would win over the search and page boxes' own.
        Add(VirtualKey.Add, Ctrl, () => ZoomStep(+1));
        Add((VirtualKey)187, Ctrl, () => ZoomStep(+1)); // '+' / '=' key
        Add(VirtualKey.Subtract, Ctrl, () => ZoomStep(-1));
        Add((VirtualKey)189, Ctrl, () => ZoomStep(-1)); // '-' key
        Add(VirtualKey.Number0, Ctrl, () => SetZoom(FitMode.None, 1));
        Add(VirtualKey.NumberPad0, Ctrl, () => SetZoom(FitMode.None, 1));
        Add(VirtualKey.Number1, Ctrl, () => SetZoom(FitMode.Width, _zoom));
        Add(VirtualKey.Number2, Ctrl, () => SetZoom(FitMode.Page, _zoom));
        Add(VirtualKey.F11, None, ToggleFullScreen);
        Add(VirtualKey.W, Ctrl, () =>
        {
            CloseDocument();
            Title = "LumePDF";
            TitleText.Text = "LumePDF";
            Scroller.Visibility = Visibility.Collapsed;
            DocTools.Visibility = Visibility.Collapsed;
            ShowWelcome();
        });

        void Add(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) =>
            {
                action();
                args.Handled = true;
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
    }

    /// <summary>Copy and select all, while the document has the focus (clicking on it gives it).</summary>
    void Scroller_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            return;
        if (e.Key == VirtualKey.C)
            CopySelection();
        else if (e.Key == VirtualKey.A)
            SelectAll();
        else
            return;
        e.Handled = true;
    }

    void FocusSearch()
    {
        if (_doc == null) return;
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
    }

    void FocusPageBox()
    {
        if (_doc == null) return;
        PageBox.Focus(FocusState.Keyboard);
        PageBox.SelectAll();
    }

    void ToggleFullScreen()
    {
        bool full = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        AppWindow.SetPresenter(full ? AppWindowPresenterKind.Overlapped : AppWindowPresenterKind.FullScreen);
    }

    // ───────────────────────────── Dialogs ─────────────────────────────

    async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    async Task<string?> AskPasswordAsync(string fileName, bool retry)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = retry ? "Password errata, riprova." : $"«{fileName}» è protetto da password.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = "Documento protetto",
            Content = panel,
            PrimaryButtonText = "Apri",
            CloseButtonText = "Annulla",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }
}
