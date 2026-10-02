using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Lieve.Pdf;
using Lieve.Viewer;
using Microsoft.UI;
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

namespace Lieve;

public sealed partial class MainWindow : Window
{
    const double PointsToDip = 96.0 / 72.0;
    const double MinZoom = 0.1, MaxZoom = 5.0;
    const double MaxRenderPixels = 16_000_000;
    static readonly double[] ZoomSteps = [0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5];

    enum FitMode { None = 0, Width = 1, Page = 2 }

    readonly PageLayout _layout = new();
    readonly Dictionary<int, PageView> _realized = [];
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _rerenderTimer;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;

    PdfDocument? _doc;
    double _zoom = 1; // 1 = actual size
    FitMode _fit = FitMode.Width;
    double _rasterScale = 1;
    int _currentPage;
    Size _fittedViewport;
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

    readonly Dictionary<TreeViewNode, int> _outlinePages = [];

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Lieve.ico"));
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

        Root.Loaded += Root_Loaded;
        Root.SizeChanged += (_, _) => AdaptHeader();
        Root.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        Closed += (_, _) =>
        {
            SaveRecent();
            _searchCts?.Cancel();
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
        ZoomButton.MinWidth = 96;
        foreach (var e in new UIElement[] { ZoomOutButton, ZoomInButton, Sep1, Sep2, PageCountText, PrevHit, NextHit })
            e.Visibility = Visibility.Visible;

        double available = Root.ActualWidth - Header.Padding.Left - LeftTools.ActualWidth
            - CaptionSpace.Width.Value - 48 /* drag area */ - 3 * Header.ColumnSpacing;
        Action[] compact =
        [
            () => SearchBox.Width = 170,
            () => Sep1.Visibility = Sep2.Visibility = Visibility.Collapsed,
            () => ZoomOutButton.Visibility = ZoomInButton.Visibility = Visibility.Collapsed,
            () => { SearchBox.Width = 130; ZoomButton.MinWidth = 64; },
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

    /// <param name="page">Zero-based page to show; negative restores the last position.</param>
    public async Task OpenFileAsync(string path, string? password = null, int page = -1)
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

        var recent = Recents.Get(path);
        _fit = recent != null ? (FitMode)recent.Fit : FitMode.Width;
        _zoom = recent?.Zoom ?? 1;

        string name = System.IO.Path.GetFileName(path);
        Title = $"{name} – Lieve";
        TitleText.Text = name;
        PageCountText.Text = $"/ {doc.PageCount}";
        Welcome.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;
        DocTools.Visibility = Visibility.Visible;
        AdaptHeader();
        Scroller.UpdateLayout();
        _layout.SetViewportWidth(Scroller.ActualWidth);

        ApplyLayout();
        Pages.ItemsSource = Enumerable.Range(0, doc.PageCount).ToList();
        Scroller.UpdateLayout();
        GoToPage(Math.Clamp(page >= 0 ? page : recent?.Page ?? 0, 0, doc.PageCount - 1));
        UpdatePageIndicator();
        SaveRecent();
        Scroller.Focus(FocusState.Programmatic);

        await LoadOutlineAsync(doc);
    }

    void CloseDocument()
    {
        if (_doc == null)
            return;
        SaveRecent();
        ClearSearch();
        Pages.ItemsSource = null;
        _realized.Clear();
        _doc.Dispose();
        _doc = null;
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
            Recents.Update(_doc.FilePath, _currentPage, _zoom, (int)_fit);
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
        double w = _layout.WidthOf(index) * _rasterScale;
        double h = _layout.HeightOf(index) * _rasterScale;
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
        if (_doc == null || view.Index < 0 || view.Index >= _layout.Count)
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
        double viewW = Math.Max(1, Scroller.ActualWidth - 2 * PageLayout.Padding - 1);
        double viewH = Math.Max(1, Scroller.ActualHeight - 2 * PageLayout.Padding);
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
            DrawHighlights(view);
        _rerenderTimer.Stop();
        _rerenderTimer.Start();
    }

    /// <summary>Changes zoom keeping the content under <paramref name="anchor"/> (viewport coords) still.</summary>
    void SetZoom(FitMode fit, double zoom, Point? anchor = null)
    {
        if (_doc == null || _layout.Count == 0)
            return;

        var a = anchor ?? new Point(Scroller.ViewportWidth / 2, Scroller.ViewportHeight / 2);
        double contentY = CurrentY + a.Y;
        int page = Math.Max(0, _layout.IndexAt(contentY));
        double fy = (contentY - _layout.TopOf(page)) / _layout.HeightOf(page);
        // Horizontal anchor relative to that page: pages narrower than the view stay centered.
        double fx = (Scroller.HorizontalOffset + a.X - _layout.LeftOf(page)) / _layout.WidthOf(page);

        _fit = fit;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyLayout();
        Pages.InvalidateMeasure();
        Scroller.UpdateLayout();

        double y = _layout.TopOf(page) + fy * _layout.HeightOf(page) - a.Y;
        double x = _layout.LeftOf(page) + fx * _layout.WidthOf(page) - a.X;
        x = Math.Clamp(x, 0, Math.Max(0, _layout.ExtentWidth - Scroller.ViewportWidth));
        ScrollTo(x, Math.Max(0, y));
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
        if (_doc != null && _fit != FitMode.None && e.NewSize != _fittedViewport)
            SetZoom(_fit, _zoom);
    }

    // ───────────────────────────── Navigation ─────────────────────────────

    void GoToPage(int index)
    {
        if (_doc == null || _layout.Count == 0)
            return;
        index = Math.Clamp(index, 0, _layout.Count - 1);
        double y = index == 0 ? 0 : _layout.TopOf(index) - PageLayout.Spacing / 2;
        ScrollTo(null, y);
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

    double MaxScrollY => Math.Max(0, _layout.ContentHeight - Scroller.ViewportHeight);

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
    }

    void UpdateCurrentPage()
    {
        if (_doc == null || _layout.Count == 0)
            return;
        int page = Math.Max(0, _layout.IndexAt(CurrentY + Scroller.ViewportHeight * 0.35));
        if (page != _currentPage)
        {
            _currentPage = page;
            UpdatePageIndicator();
            _saveTimer.Stop();
            _saveTimer.Start();
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
        SearchStatus.Text = "Ricerca…";

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
        var r = hit.Rects[0];
        double top = _layout.TopOf(hit.Page) + r.Y * _layout.HeightOf(hit.Page);
        double bottom = top + r.Height * _layout.HeightOf(hit.Page);
        double viewTop = CurrentY, viewBottom = viewTop + Scroller.ViewportHeight;
        double? y = top < viewTop + 24 || bottom > viewBottom - 24 ? top - Scroller.ViewportHeight / 3 : null;

        double? x = null;
        if (Scroller.ExtentWidth > Scroller.ViewportWidth)
        {
            double left = _layout.LeftOf(hit.Page) + r.X * _layout.WidthOf(hit.Page);
            double right = left + r.Width * _layout.WidthOf(hit.Page);
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
        double w = _layout.WidthOf(view.Index), h = _layout.HeightOf(view.Index);
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

    // ───────────────────────────── Shortcuts ─────────────────────────────

    void RegisterShortcuts()
    {
        const VirtualKeyModifiers Ctrl = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers Shift = VirtualKeyModifiers.Shift;
        const VirtualKeyModifiers None = VirtualKeyModifiers.None;

        Add(VirtualKey.O, Ctrl, () => _ = PickAndOpenAsync());
        Add(VirtualKey.F, Ctrl, () =>
        {
            if (_doc == null) return;
            SearchBox.Focus(FocusState.Keyboard);
            SearchBox.SelectAll();
        });
        Add(VirtualKey.F3, None, () => MoveHit(+1));
        Add(VirtualKey.F3, Shift, () => MoveHit(-1));
        Add(VirtualKey.G, Ctrl, () =>
        {
            if (_doc == null) return;
            PageBox.Focus(FocusState.Keyboard);
            PageBox.SelectAll();
        });
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
            Title = "Lieve";
            TitleText.Text = "Lieve";
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
