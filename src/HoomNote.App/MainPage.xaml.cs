using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using HoomNote.Canvas.Geometry;
using HoomNote.Canvas.Interaction;
using HoomNote.Canvas.Rendering;
using HoomNote.Canvas.Spatial;
using HoomNote.Core.Documents;
using HoomNote.Core.Editing;
using HoomNote.Core.Services;
using HoomNote.Infrastructure.Import;
using HoomNote.Infrastructure.Export;
using HoomNote.Infrastructure.Storage;
using HoomNote_App.Services;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using System.Text.Json;
using HoomNote.Infrastructure.Serialization;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace HoomNote_App;

public sealed partial class MainPage : Page
{
    private const string CanvasClipboardFormat = "application/x-hoomnote-canvas-objects+json";
    private sealed class SavedColorItem(string hex)
    {
        public string Hex { get; } = hex;
        public SolidColorBrush Brush { get; } = new(ParseColor(hex));
    }

    private sealed record FolderDisplay(Guid? Id, string Name, string Color)
    {
        public override string ToString() => Name;
    }

    private sealed class LibraryTreeEntry(Guid? folderId, DocumentSummary? document, string name, string color, string? metadata)
    {
        public Guid? FolderId { get; } = folderId;
        public DocumentSummary? Document { get; } = document;
        public string Name { get; } = name;
        public string Metadata { get; } = metadata ?? string.Empty;
        public SolidColorBrush Brush { get; } = new(ParseColor(color));
        public bool IsFolder => FolderId is not null && Document is null;
        public bool IsUnfiled => FolderId is null && Document is null;
        public bool IsContainer => Document is null;
        public bool CanDrag => Document is not null;
        public string Glyph => IsFolder ? "\uE8B7" : IsUnfiled ? "\uE838" : "\uE8A5";
    }

    private sealed class RecognitionLine
    {
        public double Top { get; set; }
        public double Bottom { get; set; }
        public List<(InkStrokeObject Stroke, RectD Bounds)> Strokes { get; } = [];
    }

    private static readonly string[] LibraryColors =
        ["#4BAEFF", "#7C6CFF", "#D85CFF", "#F05D7A", "#FF8A3D", "#F2C94C", "#38B26C", "#35B7B2", "#667085"];

    private enum EditorTool
    {
        Select,
        Style,
        Pen,
        Highlighter,
        SegmentEraser,
        StrokeEraser,
        Text,
        Shape,
        Lasso,
        BoxSelect,
        Pan
    }

    private readonly ObservableCollection<DocumentSummary> _documents = [];
    private readonly List<DocumentSummary> _allDocuments = [];
    private readonly ObservableCollection<NotePage> _pages = [];
    private readonly ObservableCollection<SearchResult> _searchResults = [];
    private readonly ObservableCollection<SavedColorItem> _savedColors = [];
    private readonly Dictionary<Guid, CommandHistory> _documentHistories = [];
    private CommandHistory _history = new();
    private SpatialIndex _spatialIndex = new();
    private readonly Dictionary<Guid, HoomNoteDocument> _openDocumentCache = [];
    private readonly LinkedList<Guid> _openDocumentLru = [];
    private readonly Dictionary<Guid, int> _openDocumentPointCounts = [];
    private const int OpenDocumentCacheLimit = 1;
    private const int OpenDocumentCachePointBudget = 250_000;
    private readonly Dictionary<Guid, SpatialIndex> _pageSpatialIndexCache = [];
    private readonly LinkedList<Guid> _pageSpatialIndexLru = [];
    private const int PageSpatialIndexCacheLimit = 2;
    private readonly HashSet<Guid> _visibleObjectIds = [];
    private readonly List<CanvasObject> _visibleObjects = [];
    private CancellationTokenSource? _spatialIndexBuildCancellation;
    private readonly PdfPreviewCache _pdfPreview = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly SemaphoreSlim _handwritingIndexGate = new(1, 1);
    private readonly List<InkPoint> _activeInk = [];
    private readonly List<PointD> _eraserPath = [];
    private readonly List<RectD> _eraseDirtyRegions = [];
    private readonly List<InkStrokeObject> _pendingRecognitionStrokes = [];
    private readonly List<(Guid PageId, InkStrokeObject Stroke)> _pendingInkAppends = [];
    private readonly Dictionary<string, CanvasBitmap> _imageBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _imageBitmapSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _imageBitmapLru = [];
    private const long ImageBitmapCacheBudget = 24L * 1024 * 1024;
    private long _imageBitmapBytes;
    private readonly HashSet<string> _pendingImageLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedImageLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> _imageWaitingPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _imagePagesNeedingRefresh = [];
    private readonly SemaphoreSlim _imageDecodeGate = new(1, 1);
    private int _imageLoadGeneration;
    private readonly Dictionary<Guid, BitmapImage> _pageThumbnailCache = [];
    private readonly LinkedList<Guid> _pageThumbnailLru = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _pageThumbnailLoads = [];
    private const int PageThumbnailCacheLimit = 24;
    private const int PageThumbnailWidth = 96;
    private const int PageThumbnailHeight = 116;
    private readonly List<CanvasCachedGeometry> _liveInkGeometryChunks = [];
    private int _liveInkChunkStart;
    private CanvasRenderTarget? _liveInkRaster;
    private Guid? _liveInkRasterPageId;
    private int _liveInkRasterPointIndex;
    private double _liveInkRasterWidth;
    private double _liveInkRasterHeight;
    private float _liveInkRasterDpi;
    private Matrix3x2 _liveInkPageToScreen = Matrix3x2.Identity;
    private const int LiveInkChunkSize = 64;
    private const double LiveInkMinimumScreenDistance = 0.65;
    private sealed record StrokeGeometryCacheEntry(
        InkStrokeObject Stroke,
        CanvasGeometry Geometry,
        Color Color,
        bool IsCenterline,
        float Width);
    private readonly Dictionary<Guid, StrokeGeometryCacheEntry> _strokeGeometryCache = [];
    private readonly LinkedList<Guid> _strokeGeometryLru = [];
    private readonly Dictionary<Guid, LinkedListNode<Guid>> _strokeGeometryLruNodes = [];
    private int _strokeGeometryCachedPoints;
    private int _frameStrokeGeometryBuilds;
    // Retain lightweight path geometry rather than CanvasCachedGeometry realizations. Realized
    // geometry consumed hundreds of MB on Samsung pages; reusable paths avoid rebuilding every
    // visible stroke during pan while keeping native memory proportional to actual point data.
    private const int StrokeGeometryCacheLimit = 2_048;
    private const int StrokeGeometryCachePointLimit = 180_000;
    // Creating a retained CanvasGeometry costs the same path construction the raw fallback
    // would perform for that frame, so retain every miss that still fits the memory budget.
    private const int FrameStrokeGeometryBuildLimit = StrokeGeometryCacheLimit;
    private readonly CanvasStrokeStyle _roundInkStrokeStyle = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        DashCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round
    };
    private readonly Dictionary<Guid, Guid> _tabPageSelections = [];
    private readonly List<CanvasObject> _selectedObjects = [];
    private readonly Dictionary<Guid, CanvasObject> _multiTransformPreviews = [];
    private readonly Dictionary<Guid, CanvasObject> _styleBrushOriginals = [];
    private readonly MenuFlyout _notebookContextMenu = new();
    private readonly MenuFlyout _folderContextMenu = new();
    private readonly MenuFlyout _pageContextMenu = new();
    private CanvasCommandList? _pageRenderCache;
    private CanvasRenderTarget? _lowZoomPageRaster;
    private Guid? _lowZoomPageRasterPageId;
    private Guid? _pageRenderCachePageId;
    private readonly HashSet<Guid> _pageRenderCacheObjectIds = [];
    private readonly List<CanvasObject> _pageRenderOverlays = [];
    private readonly List<CanvasCommandList> _pageRenderOverlayBatches = [];
    private readonly Dictionary<Guid, CanvasRenderTarget> _warmPageRenderCaches = [];
    private readonly LinkedList<Guid> _warmPageRenderLru = [];
    private const int WarmPageRenderCacheLimit = 1;
    // Keep recent strokes as cached geometry while the user writes. Compiling every pen-up into
    // a command list created a visible hitch between letters; batching amortizes that work.
    private const int OverlayBatchSize = 8;
    private const int OverlayBatchCompactionThreshold = 8;

    private SqliteDocumentRepository? _repository;
    private ContentAddressedAssetStore? _assetStore;
    private HoomNotePackageService? _packageService;
    private VectorExportService? _vectorExportService;
    private DocumentImportService? _importService;
    private IHandwritingRecognitionService? _recognizer;
    private WindowsPageOcrService? _pageOcr;
    private PageThumbnailRenderer? _pageThumbnailRenderer;
    private LocalUserSettingsStore? _userSettingsStore;
    private UserPreferences _userPreferences = new();
    private HoomNoteDocument? _document;
    private NotePage? _page;
    private CanvasObject? _selectedObject;
    private CanvasObject? _transformOriginal;
    private CanvasObject? _transformPreview;
    private RichTextObject? _textOriginal;
    private RichTextObject? _textPreview;
    private List<CanvasObject>? _multiTransformOriginals;
    private List<CanvasObject>? _eraseSnapshot;
    private readonly DispatcherQueueTimer _saveTimer;
    private CancellationTokenSource? _searchDebounce;
    private CancellationTokenSource? _searchLocateCancellation;
    private readonly DispatcherQueueTimer _recognitionTimer;
    private CancellationTokenSource? _settingsSaveDebounce;
    private CancellationTokenSource? _handwritingIndexCancellation;
    private Task? _handwritingIndexTask;
    private Guid? _handwritingIndexDocumentId;
    private CancellationTokenSource? _incrementalRecognitionCancellation;
    private readonly DispatcherQueueTimer _thumbnailRefreshTimer;
    private readonly DispatcherQueueTimer _navigationSettleTimer;
    private readonly DispatcherQueueTimer _liveInkRasterReleaseTimer;
    private Guid? _pendingThumbnailRefreshPageId;
    private EditorTool _activeTool = EditorTool.Select;
    private EditorTool _gestureTool = EditorTool.Select;
    private InkStyle? _gestureInkStyle;
    private Matrix3x2 _gestureScreenToPage;
    private bool _gestureScreenToPageValid;
    private TransformHandle _transformHandle;
    private PointD _gestureStart;
    private Point _screenStart;
    private Vector2 _pan;
    private Vector2 _panStart;
    private readonly Dictionary<uint, Point> _touchPoints = [];
    private Point _touchStartCentroid;
    private double _touchStartSpread = 1;
    private PointD _touchPageAnchor;
    private double _touchStartZoom = 1;
    private Vector2 _touchStartPan;
    private Point _touchLastCentroid;
    private Vector2 _touchVelocity;
    private long _touchLastMoveTimestamp;
    private long _touchInertiaTimestamp;
    private bool _touchGestureMoved;
    private bool _touchInertiaActive;
    private bool _zoomNavigationActive;
    private bool _wheelZoomAnimating;
    private bool _viewportFramePumpActive;
    private double _wheelZoomTarget = 1;
    private double _wheelZoomStart = 1;
    private Point _wheelZoomAnchorScreen;
    private PointD _wheelZoomAnchorPage;
    private long _wheelZoomAnimationStarted;
    private long _lastPenInteractionTimestamp;
    private long _lastNativeTouchTimestamp;
    private int _pointerClassificationLogCount;
    private long _lastInkMovementTimestamp;
    private double _zoom = 1;
    private bool _fitPending = true;
    private bool _isPointerDown;
    private bool _penActive;
    private bool _loading;
    private bool _updatingTabs;
    private bool _syncingInkColor;
    private bool _syncingTextEditor;
    private bool _requiresFullSave;
    private int _fullSaveVersion;
    private bool _hasUnsavedChanges;
    private int _editVersion;
    private Slider? _scrubSlider;
    private UIElement? _scrubOwner;
    private double _scrubStartX;
    private double _scrubStartValue;
    private string? _internalClipboard;
    private bool _temporaryGridVisible;
    private bool _readMode;
    private bool _libraryWasVisible;
    private bool _pagesWereVisible;
    private bool _inspectorWasVisible;
    private bool _compactLayout;
    private bool _compactLibraryWasVisible;
    private bool _compactPagesWereVisible;
    private bool _compactInspectorWasVisible;
    private double _temporaryGridSize = 32;
    private Guid? _selectedFolderId;
    private int _folderTreeRebuildVersion;
    private readonly HashSet<Guid> _expandedFolderIds = [];
    private bool _hasFolderExpansionState;
    private bool _unfiledExpanded = true;
    private Guid? _recognitionPageId;
    private DocumentSummary? _notebookContextTarget;
    private FolderDisplay? _folderContextTarget;
    private NotePage? _pageContextTarget;
    private MenuFlyoutItem _renameFolderMenuItem = null!;
    private MenuFlyoutItem _deleteFolderMenuItem = null!;
    private MenuFlyoutItem _newSubfolderMenuItem = null!;
    private string _inkColor = "#111111";
    private string _penColor = "#111111";
    private string _highlighterColor = "#FFCE56";
    private EditorTool _colorTool = EditorTool.Pen;
    private float? _presetOpacity;
    private float? _presetSmoothing;
    private long _lastTextEditorCloseTimestamp;
    private Color? _pendingTextColor;
    private Guid? _pendingTextColorObjectId;
    private bool _styleToolPickMode = true;
    private bool _syncingStyleTool;
    private string _styleToolColor = "#111111";
    private float _styleToolWidth = 2.4f;
    private ToolTip? _openToolTip;
    private CancellationTokenSource? _toolTipCloseCancellation;
    private long _lastPasteTimestamp;
    private Guid? _draggedPresetId;
    private readonly SemaphoreSlim _pasteGate = new(1, 1);
    private readonly Dictionary<ColumnDefinition, CancellationTokenSource> _sidebarAnimations = [];
    private readonly HashSet<Guid> _pageOcrIndexedThisSession = [];
    private readonly List<RectD> _searchFlashBounds = [];
    private long _searchFlashStarted;
    private long _lastSlowFrameLogTimestamp;
    private Guid? _pendingSearchFlashPageId;
    private string? _pendingSearchFlashQuery;
    private const double LibraryWidth = 252;
    private const double PageRailWidth = 132;
    private const double InspectorWidth = 300;
    private const double SearchFlashDurationMs = 2_000;
    private const long NavigationSnapshotByteBudget = 24L * 1024 * 1024;
    private const int BackgroundIndexIdleDelayMs = 4_500;
    private const double ShapeSnapTerminalHoldMs = 320;

    public MainPage()
    {
        InitializeComponent();
        PresetScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnPresetScrollWheelChanged), handledEventsToo: true);
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(1_500);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += OnSaveTimerTick;
        _recognitionTimer = DispatcherQueue.CreateTimer();
        _recognitionTimer.Interval = TimeSpan.FromMilliseconds(2_200);
        _recognitionTimer.IsRepeating = false;
        _recognitionTimer.Tick += OnRecognitionTimerTick;
        _thumbnailRefreshTimer = DispatcherQueue.CreateTimer();
        // Thumbnail rendering walks the entire page on a software Win2D device. Give writing
        // and navigation a generous idle window so a dense preview cannot compete with ink.
        _thumbnailRefreshTimer.Interval = TimeSpan.FromMilliseconds(3_000);
        _thumbnailRefreshTimer.IsRepeating = false;
        _thumbnailRefreshTimer.Tick += OnThumbnailRefreshTimerTick;
        _navigationSettleTimer = DispatcherQueue.CreateTimer();
        _navigationSettleTimer.Interval = TimeSpan.FromMilliseconds(180);
        _navigationSettleTimer.IsRepeating = false;
        _navigationSettleTimer.Tick += OnNavigationSettleTick;
        _liveInkRasterReleaseTimer = DispatcherQueue.CreateTimer();
        _liveInkRasterReleaseTimer.Interval = TimeSpan.FromSeconds(2);
        _liveInkRasterReleaseTimer.IsRepeating = false;
        _liveInkRasterReleaseTimer.Tick += (_, _) =>
        {
            if (!_isPointerDown) DisposeLiveInkRaster();
        };
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnGlobalKeyDown), handledEventsToo: true);
        PageList.ItemsSource = _pages;
        SearchResultsList.ItemsSource = _searchResults;
        SavedColorPalette.ItemsSource = _savedColors;
        BuildContextMenus();
        SetInkColor(_inkColor, rememberForTool: false);
        _pdfPreview.PreviewAvailable += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            ClearWarmPageRenderCaches();
            InvalidatePageRenderCache();
            DrawingSurface.Invalidate();
        });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Info("main.loaded_start");
        if (App.MainAppWindow is MainWindow { NativeTouchSource: { } nativeTouch })
        {
            nativeTouch.Frame -= OnNativeTouchFrame;
            nativeTouch.Frame += OnNativeTouchFrame;
        }
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoomNote");
            Directory.CreateDirectory(root);
            _userSettingsStore = new LocalUserSettingsStore(Path.Combine(root, "settings.json"));
            _userPreferences = await _userSettingsStore.LoadAsync();
            var migratedInkPresets = false;
            for (var index = 0; index < _userPreferences.ToolbarPresets.Count; index++)
            {
                var preset = _userPreferences.ToolbarPresets[index];
                if (string.Equals(preset.Tool, nameof(EditorTool.Highlighter), StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(preset.Tool, nameof(EditorTool.Pen), StringComparison.OrdinalIgnoreCase) &&
                    preset.PressureSensitivity == 0 && preset.Smoothing >= 0.9) continue;
                _userPreferences.ToolbarPresets[index] = preset with
                {
                    Tool = nameof(EditorTool.Pen),
                    PressureSensitivity = 0,
                    Smoothing = Math.Max(0.9, preset.Smoothing)
                };
                migratedInkPresets = true;
            }
            if (migratedInkPresets) await _userSettingsStore.SaveAsync(_userPreferences);
            _penColor = IsValidHexColor(_userPreferences.PenColor) ? _userPreferences.PenColor.ToUpperInvariant() : "#111111";
            _highlighterColor = IsValidHexColor(_userPreferences.HighlighterColor) ? _userPreferences.HighlighterColor.ToUpperInvariant() : "#FFCE56";
            HighlighterStraightToggle.IsOn = _userPreferences.HighlighterStraightLine;
            SetInkColor(_penColor, rememberForTool: false);
            LoadSavedColorPalette();
            RebuildPresetToolbar();
            RebuildFolderTree();
            _assetStore = new ContentAddressedAssetStore(Path.Combine(root, "assets"));
            _pageOcr = new WindowsPageOcrService(_assetStore);
            _pageThumbnailRenderer = new PageThumbnailRenderer(_assetStore);
            _repository = new SqliteDocumentRepository(Path.Combine(root, "library.db"));
            await _repository.InitializeAsync();
            _packageService = new HoomNotePackageService(_assetStore);
            _vectorExportService = new VectorExportService(_assetStore);
            var workerPath = Path.Combine(AppContext.BaseDirectory, "HoomNote.Import.Worker.exe");
            _importService = new DocumentImportService(_assetStore,
                new SlideWorkerConverter(workerPath, Path.Combine(root, "import-temp")));
            _recognizer = new WindowsInkRecognitionService();
            await RefreshLibraryAsync();
            ConfigureTransientToolTips(this);
            StatusText.Text = "Ready • autosave enabled";
            DiagnosticsLog.Info("main.loaded_complete", ("notebooks", _allDocuments.Count),
                ("log_directory", DiagnosticsLog.LogDirectory));
            _ = UpdateService.CheckForUpdatesAsync(XamlRoot, manual: false, PrepareForUpdateRestartAsync);
        }
        catch (Exception exception)
        {
            ShowError("HoomNote could not initialize its local library.", exception);
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Info("main.unloading", ("unsaved", _hasUnsavedChanges),
            ("document_open", _document is not null));
        _saveTimer.Stop();
        if (App.MainAppWindow is MainWindow { NativeTouchSource: { } nativeTouch })
            nativeTouch.Frame -= OnNativeTouchFrame;
        _searchDebounce?.Cancel();
        _searchLocateCancellation?.Cancel();
        _recognitionTimer.Stop();
        _thumbnailRefreshTimer.Stop();
        _navigationSettleTimer.Stop();
        _wheelZoomAnimating = false;
        StopViewportFramePump();
        _liveInkRasterReleaseTimer.Stop();
        StopTouchInertia(resumeBackgroundWork: false);
        _touchPoints.Clear();
        _isPointerDown = false;
        _penActive = false;
        DrawingSurface.ReleasePointerCaptures();
        foreach (var cancellation in _pageThumbnailLoads.Values) cancellation.Cancel();
        _pageThumbnailLoads.Clear();
        _pageThumbnailCache.Clear();
        _pageThumbnailLru.Clear();
        _settingsSaveDebounce?.Cancel();
        _handwritingIndexCancellation?.Cancel();
        _incrementalRecognitionCancellation?.Cancel();
        _toolTipCloseCancellation?.Cancel();
        _spatialIndexBuildCancellation?.Cancel();
        foreach (var animation in _sidebarAnimations.Values) animation.Cancel();
        if (_document is not null) await SaveNowAsync();
        if (_userSettingsStore is not null) await SaveUserPreferencesAsync();
        if (_repository is not null) await _repository.DisposeAsync();
        InvalidatePageRenderCache();
        ClearWarmPageRenderCaches();
        ClearLiveInkGeometryCache();
        DisposeLiveInkRaster();
        ClearStrokeGeometryCache();
        ClearImageBitmapCache();
        _roundInkStrokeStyle.Dispose();
        _pdfPreview.Dispose();
        DiagnosticsLog.Info("main.unloaded");
    }

    private async Task RefreshLibraryAsync()
    {
        if (_repository is null) return;
        var summaries = await _repository.ListAsync();
        _allDocuments.Clear();
        _allDocuments.AddRange(summaries);
        ApplyFolderFilter();
        if (_allDocuments.Count > 0)
        {
            var preferred = _document is null ? _allDocuments[0] :
                _allDocuments.FirstOrDefault(item => item.Id == _document.Id) ?? _allDocuments[0];
            if (_document is null || _document.Id != preferred.Id)
                await LoadDocumentAsync(preferred.Id);
            SelectLibraryDocument(preferred.Id);
        }
        UpdateEmptyState();
    }

    private void ApplyFolderFilter()
    {
        var documents = _allDocuments
            .OrderBy(document => NotebookOrderIndex(document.Id))
            .ThenByDescending(document => document.UpdatedAt)
            .ToList();
        _loading = true;
        _documents.Clear();
        foreach (var document in documents)
        {
            var color = _userPreferences.DocumentColors.GetValueOrDefault(document.Id.ToString("D"), "#4BAEFF");
            _documents.Add(document with { Color = color });
        }
        _loading = false;
        RebuildFolderTree(_selectedFolderId, _document?.Id);
        UpdateLibrarySummary();
        UpdateFolderActions();
    }

    private int NotebookOrderIndex(Guid documentId)
    {
        var index = _userPreferences.NotebookOrder.IndexOf(documentId.ToString("D"));
        return index < 0 ? int.MaxValue : index;
    }

    private void RebuildFolderTree(Guid? preferredFolderId = null, Guid? preferredDocumentId = null)
    {
        if (FolderTree.RootNodes.Count > 0)
        {
            _expandedFolderIds.Clear();
            foreach (var existingRoot in FolderTree.RootNodes) CaptureExpandedFolders(existingRoot);
            _hasFolderExpansionState = true;
        }
        var rebuildVersion = ++_folderTreeRebuildVersion;
        FolderTree.RootNodes.Clear();
        var folderIds = _userPreferences.NotebookFolders.Select(folder => folder.Id).ToHashSet();
        var renderedFolders = new HashSet<Guid>();
        foreach (var folder in _userPreferences.NotebookFolders
                     .Where(item => item.ParentId is null || !folderIds.Contains(item.ParentId.Value))
                     .OrderBy(item => item.Name))
        {
            FolderTree.RootNodes.Add(BuildFolderNode(folder, renderedFolders, []));
        }
        foreach (var folder in _userPreferences.NotebookFolders.Where(item => !renderedFolders.Contains(item.Id)).OrderBy(item => item.Name))
            FolderTree.RootNodes.Add(BuildFolderNode(folder, renderedFolders, []));
        var unfiledDocuments = _documents.Where(document => DocumentFolderId(document.Id) is null).ToArray();
        var unfiledEntry = new LibraryTreeEntry(null, null, "Unfiled", "#8B95A7", unfiledDocuments.Length.ToString());
        var unfiledNode = new TreeViewNode
        {
            Content = unfiledEntry,
            IsExpanded = _unfiledExpanded
        };
        foreach (var document in unfiledDocuments) unfiledNode.Children.Add(BuildNotebookNode(document));
        FolderTree.RootNodes.Insert(0, unfiledNode);

        LibraryEmptyText.Visibility = FolderTree.RootNodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var desiredFolder = preferredFolderId ?? _selectedFolderId;
        var desiredDocument = preferredDocumentId ?? _document?.Id;
        var nodeToSelect = desiredDocument is { } documentId
            ? FindDocumentNode(FolderTree.RootNodes, documentId)
            : desiredFolder is { } folderId ? FindFolderNode(FolderTree.RootNodes, folderId) : null;
        for (var ancestor = nodeToSelect?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            ancestor.IsExpanded = true;
        // WinUI can fault in native TreeView code if SelectedNode is assigned in the
        // same call stack that replaced RootNodes. Restore it after the tree has attached.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (rebuildVersion != _folderTreeRebuildVersion || nodeToSelect is null) return;
            try
            {
                FolderTree.SelectedNode = nodeToSelect;
            }
            catch (Exception exception)
            {
                DiagnosticsLog.Error("folder.selection_restore_failed", exception,
                    ("folder_count", _userPreferences.NotebookFolders.Count));
            }
        });
        UpdateFolderActions();
    }

    private void CaptureExpandedFolders(TreeViewNode node)
    {
        if (GetLibraryEntry(node) is { } entry)
        {
            if (entry.FolderId is { } folderId && node.IsExpanded) _expandedFolderIds.Add(folderId);
            else if (entry.IsUnfiled) _unfiledExpanded = node.IsExpanded;
        }
        foreach (var child in node.Children)
            CaptureExpandedFolders(child);
    }

    private TreeViewNode BuildFolderNode(NotebookFolderPreference folder, HashSet<Guid> rendered, HashSet<Guid> ancestry)
    {
        rendered.Add(folder.Id);
        ancestry.Add(folder.Id);
        var entry = new LibraryTreeEntry(folder.Id, null, folder.Name, folder.Color,
            _documents.Count(document => DocumentFolderId(document.Id) == folder.Id).ToString());
        var node = new TreeViewNode
        {
            Content = entry,
            IsExpanded = !_hasFolderExpansionState || _expandedFolderIds.Contains(folder.Id) || _selectedFolderId == folder.Id
        };
        foreach (var child in _userPreferences.NotebookFolders
                     .Where(item => item.ParentId == folder.Id && !ancestry.Contains(item.Id) && !rendered.Contains(item.Id))
                     .OrderBy(item => item.Name))
        {
            node.Children.Add(BuildFolderNode(child, rendered, ancestry));
        }
        foreach (var document in _documents.Where(document => DocumentFolderId(document.Id) == folder.Id))
            node.Children.Add(BuildNotebookNode(document));
        ancestry.Remove(folder.Id);
        return node;
    }

    private TreeViewNode BuildNotebookNode(DocumentSummary document)
    {
        var entry = new LibraryTreeEntry(null, document, document.Title, document.Color, $"{document.PageCount}p");
        return new TreeViewNode
        {
            Content = entry
        };
    }

    private static LibraryTreeEntry? GetLibraryEntry(TreeViewNode? node) =>
        node?.Content as LibraryTreeEntry;

    private Guid? DocumentFolderId(Guid documentId)
    {
        if (!_userPreferences.DocumentFolders.TryGetValue(documentId.ToString("D"), out var value) ||
            !Guid.TryParse(value, out var folderId) ||
            _userPreferences.NotebookFolders.All(folder => folder.Id != folderId)) return null;
        return folderId;
    }

    private static TreeViewNode? FindFolderNode(IList<TreeViewNode> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (GetLibraryEntry(node) is { FolderId: { } nodeId } && nodeId == id) return node;
            if (FindFolderNode(node.Children, id) is { } match) return match;
        }
        return null;
    }

    private static TreeViewNode? FindDocumentNode(IList<TreeViewNode> nodes, Guid id)
    {
        foreach (var node in nodes)
        {
            if (GetLibraryEntry(node)?.Document?.Id == id) return node;
            if (FindDocumentNode(node.Children, id) is { } match) return match;
        }
        return null;
    }

    private void SelectLibraryDocument(Guid documentId)
    {
        var node = FindDocumentNode(FolderTree.RootNodes, documentId);
        if (node is null) return;
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            ancestor.IsExpanded = true;
        var rebuildVersion = _folderTreeRebuildVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (rebuildVersion != _folderTreeRebuildVersion) return;
            try { FolderTree.SelectedNode = node; }
            catch (Exception exception) { DiagnosticsLog.Error("library.document_selection_failed", exception); }
        });
    }

    private void OnFolderSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (GetLibraryEntry(sender.SelectedNode) is not { } entry) return;
        if (entry.IsContainer)
        {
            _selectedFolderId = entry.FolderId;
        }
        else if (entry.Document is { } document)
        {
            _selectedFolderId = DocumentFolderId(document.Id);
        }
        UpdateLibrarySummary();
        UpdateFolderActions();
    }

    private async void OnLibraryRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: LibraryTreeEntry entry }) return;
        if (entry.Document is { } document)
        {
            _selectedFolderId = DocumentFolderId(document.Id);
            if (_document?.Id != document.Id) await LoadDocumentAsync(document.Id);
            SelectLibraryDocument(document.Id);
        }
        else
        {
            var node = entry.FolderId is { } folderId
                ? FindFolderNode(FolderTree.RootNodes, folderId)
                : FolderTree.RootNodes.FirstOrDefault(node => GetLibraryEntry(node)?.IsUnfiled == true);
            if (node is not null)
            {
                // Select the container before collapsing it. Leaving a hidden child selected
                // makes WinUI intermittently recycle the first child container on re-expand.
                try { FolderTree.SelectedNode = node; }
                catch (Exception exception) { DiagnosticsLog.Error("library.container_selection_failed", exception); }
                node.IsExpanded = !node.IsExpanded;
            }
            _selectedFolderId = entry.FolderId;
        }
        UpdateFolderActions();
        e.Handled = true;
    }

    private void OnLibraryNotebookDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: LibraryTreeEntry { Document: { } document } }) return;
        args.AllowedOperations = DataPackageOperation.Move;
        args.Data.SetText(document.Id.ToString("D"));
    }

    private void OnLibraryContainerDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        if (sender is Grid row)
            row.Background = new SolidColorBrush(Color.FromArgb(38, 75, 174, 255));
        e.Handled = true;
    }

    private void OnLibraryContainerDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Grid row)
            row.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
    }

    private async void OnLibraryContainerDrop(object sender, DragEventArgs e)
    {
        if (sender is not Grid { Tag: LibraryTreeEntry { IsContainer: true } target } row ||
            !e.DataView.Contains(StandardDataFormats.Text)) return;
        row.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        e.Handled = true;
        var value = await e.DataView.GetTextAsync();
        if (!Guid.TryParse(value, out var documentId))
        {
            DiagnosticsLog.Warning("folder.drop_rejected", ("reason", "invalid_document_id"));
            return;
        }
        DiagnosticsLog.Info("folder.drop_received", ("to_folder", target.FolderId is not null));
        await MoveNotebookToFolderAsync(documentId, target.FolderId);
    }

    private void OnNotebookDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not DocumentSummary document) return;
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.SetText(document.Id.ToString("D"));
    }

    private async void OnNotebookDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var visibleIds = _documents.Select(document => document.Id.ToString("D")).ToArray();
        _userPreferences.NotebookOrder.RemoveAll(id => visibleIds.Contains(id, StringComparer.OrdinalIgnoreCase));
        _userPreferences.NotebookOrder.InsertRange(0, visibleIds);
        await PersistUserPreferencesAsync("Updated notebook order");
    }

    private void OnFolderTreeDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.Text)) e.AcceptedOperation = DataPackageOperation.Move;
    }

    private async void OnFolderTreeDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.Text)) return;
        var value = await e.DataView.GetTextAsync();
        if (!Guid.TryParse(value, out var documentId)) return;
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        var node = container is null ? null : FolderTree.NodeFromContainer(container);
        var target = GetLibraryEntry(node);
        if (container is not null && target?.IsContainer != true) return;
        await MoveNotebookToFolderAsync(documentId, target?.FolderId);
        e.Handled = true;
    }

    private async void OnNewFolderClick(object sender, RoutedEventArgs e)
    {
        try { await CreateFolderAsync(null); }
        catch (Exception exception) { ShowError("The folder could not be created.", exception); }
    }

    private async Task CreateFolderAsync(Guid? parentId)
    {
        if (parentId is not null && _userPreferences.NotebookFolders.All(folder => folder.Id != parentId))
            parentId = null;
        var name = new TextBox { Header = parentId is null ? "Folder name" : "Subfolder name" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = parentId is null ? "New folder" : "New subfolder",
            Content = name,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        DiagnosticsLog.Info("folder.create_started", ("is_subfolder", parentId is not null));
        var created = new NotebookFolderPreference
        {
            ParentId = parentId, Name = name.Text.Trim()
        };
        _userPreferences.NotebookFolders.Add(created);
        _selectedFolderId = created.Id;
        RebuildFolderTree(created.Id);
        ApplyFolderFilter();
        await PersistUserPreferencesAsync("Created notebook folder");
        DiagnosticsLog.Info("folder.created", ("is_subfolder", parentId is not null));
    }

    private async void OnMoveNotebookToFolderClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        await MoveNotebookToFolderAsync(_document.Id, _selectedFolderId);
    }

    private async Task MoveNotebookToFolderAsync(Guid documentId, Guid? folderId)
    {
        if (folderId is not null && _userPreferences.NotebookFolders.All(folder => folder.Id != folderId))
        {
            DiagnosticsLog.Warning("folder.move_rejected", ("reason", "missing_folder"));
            return;
        }
        var key = documentId.ToString("D");
        if (folderId is { } id) _userPreferences.DocumentFolders[key] = id.ToString("D");
        else _userPreferences.DocumentFolders.Remove(key);
        ApplyFolderFilter();
        SelectLibraryDocument(documentId);
        await PersistUserPreferencesAsync(folderId is null ? "Moved notebook to All notebooks" : "Moved notebook to folder");
        DiagnosticsLog.Info("folder.notebook_moved", ("to_folder", folderId is not null));
    }

    private void UpdateFolderActions() { }

    private void UpdateLibrarySummary()
    {
        NotebookCountText.Text = _documents.Count.ToString();
        LibraryEmptyText.Visibility = FolderTree.RootNodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildContextMenus()
    {
        AddMenuItem(_notebookContextMenu, "Rename notebook", "\uE8AC", OnRenameNotebookContextClick);
        AddMenuItem(_notebookContextMenu, "Remove from folder", "\uE8F1", OnRemoveNotebookFolderContextClick);
        _notebookContextMenu.Items.Add(CreateColorMenu("Notebook color", OnNotebookColorClick));
        _notebookContextMenu.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(_notebookContextMenu, "Delete notebook", "\uE74D", OnDeleteNotebookContextClick);

        _newSubfolderMenuItem = AddMenuItem(_folderContextMenu, "New subfolder", "\uE8F4", OnNewSubfolderContextClick);
        _renameFolderMenuItem = AddMenuItem(_folderContextMenu, "Rename folder", "\uE8AC", OnRenameFolderContextClick);
        _folderContextMenu.Items.Add(CreateColorMenu("Folder color", OnFolderColorClick));
        _folderContextMenu.Items.Add(new MenuFlyoutSeparator());
        _deleteFolderMenuItem = AddMenuItem(_folderContextMenu, "Delete folder", "\uE74D", OnDeleteFolderContextClick);

        AddMenuItem(_pageContextMenu, "Delete page", "\uE74D", OnDeletePageContextClick);
    }

    private static MenuFlyoutItem AddMenuItem(MenuFlyout flyout, string text, string glyph,
        RoutedEventHandler handler)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += handler;
        flyout.Items.Add(item);
        return item;
    }

    private static MenuFlyoutSubItem CreateColorMenu(string text, RoutedEventHandler handler)
    {
        var menu = new MenuFlyoutSubItem { Text = text, Icon = new FontIcon { Glyph = "\uE790" } };
        foreach (var color in LibraryColors)
        {
            var item = new MenuFlyoutItem
            {
                Text = color,
                Tag = color,
                Icon = new FontIcon { Glyph = "\u25CF", Foreground = new SolidColorBrush(ParseColor(color)) }
            };
            item.Click += handler;
            menu.Items.Add(item);
        }
        return menu;
    }

    private async void OnNotebookColorClick(object sender, RoutedEventArgs e)
    {
        if (_notebookContextTarget is null || sender is not MenuFlyoutItem { Tag: string color }) return;
        _userPreferences.DocumentColors[_notebookContextTarget.Id.ToString("D")] = color;
        ApplyFolderFilter();
        await PersistUserPreferencesAsync("Updated notebook color");
    }

    private async void OnFolderColorClick(object sender, RoutedEventArgs e)
    {
        if (_folderContextTarget?.Id is not { } folderId || sender is not MenuFlyoutItem { Tag: string color }) return;
        var index = _userPreferences.NotebookFolders.FindIndex(folder => folder.Id == folderId);
        if (index < 0) return;
        _userPreferences.NotebookFolders[index] = _userPreferences.NotebookFolders[index] with { Color = color };
        RebuildFolderTree(folderId);
        await PersistUserPreferencesAsync("Updated folder color");
    }

    private void OnPageListRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var container = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (container?.Content is not NotePage page) return;
        _pageContextTarget = page;
        _pageContextMenu.ShowAt(container);
        e.Handled = true;
    }

    private void OnFolderTreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        var node = container is null ? null : FolderTree.NodeFromContainer(container);
        if (GetLibraryEntry(node) is not { } entry) return;
        if (entry.Document is { } document)
        {
            _notebookContextTarget = document;
            _notebookContextMenu.ShowAt(container!);
            e.Handled = true;
            return;
        }
        if (entry.FolderId is not { } folderId) return;
        var preference = _userPreferences.NotebookFolders.FirstOrDefault(item => item.Id == folderId);
        if (preference is null) return;
        var folder = new FolderDisplay(preference.Id, preference.Name, preference.Color);
        _folderContextTarget = folder;
        _renameFolderMenuItem.IsEnabled = true;
        _deleteFolderMenuItem.IsEnabled = true;
        _newSubfolderMenuItem.Text = "New subfolder";
        _folderContextMenu.ShowAt(container!);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private async void OnRenameNotebookContextClick(object sender, RoutedEventArgs e)
    {
        if (_notebookContextTarget is not { } target || _repository is null) return;
        var name = await PromptForNameAsync("Rename notebook", "Notebook name", target.Title);
        if (name is null) return;
        _saveTimer.Stop();
        await _saveGate.WaitAsync();
        try
        {
            var document = _document?.Id == target.Id ? _document : await _repository.LoadAsync(target.Id);
            if (document is null) return;
            document.Title = name;
            await _repository.SaveAsync(document);
            if (_document?.Id == target.Id)
            {
                _pendingInkAppends.Clear();
                _requiresFullSave = false;
                _hasUnsavedChanges = false;
                NotebookTitle.Text = name;
                var tab = NotebookTabs.TabItems.OfType<TabViewItem>()
                    .FirstOrDefault(item => item.Tag is Guid id && id == target.Id);
                if (tab is not null) tab.Header = name;
            }
        }
        finally { _saveGate.Release(); }
        await RefreshLibraryAsync();
    }

    private async void OnDeleteNotebookContextClick(object sender, RoutedEventArgs e)
    {
        if (_notebookContextTarget is not { } target || _repository is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete this notebook?",
            Content = $"Delete \u201c{target.Title}\u201d and its {target.PageCount} page(s)? This cannot be undone.",
            PrimaryButtonText = "Delete notebook",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var deletingCurrent = _document?.Id == target.Id;
        _saveTimer.Stop();
        _recognitionTimer.Stop();
        await _saveGate.WaitAsync();
        try
        {
            if (deletingCurrent) _document = null;
            await _repository.DeleteAsync(target.Id);
        }
        finally { _saveGate.Release(); }

        _documentHistories.Remove(target.Id);
        RemoveCachedDocument(target.Id);
        _tabPageSelections.Remove(target.Id);
        _userPreferences.DocumentFolders.Remove(target.Id.ToString("D"));
        _userPreferences.DocumentColors.Remove(target.Id.ToString("D"));
        _userPreferences.NotebookOrder.RemoveAll(id => id.Equals(target.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
        _updatingTabs = true;
        var tab = NotebookTabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(item => item.Tag is Guid id && id == target.Id);
        if (tab is not null) NotebookTabs.TabItems.Remove(tab);
        _updatingTabs = false;
        if (deletingCurrent)
        {
            _page = null;
            _pages.Clear();
            NotebookTitle.Text = "No notebook";
            SelectPage(null);
        }
        await PersistUserPreferencesAsync("Deleted notebook");
        await RefreshLibraryAsync();
    }

    private async void OnMoveNotebookContextClick(object sender, RoutedEventArgs e)
    {
        if (_notebookContextTarget is not { } target) return;
        await MoveNotebookToFolderAsync(target.Id, _selectedFolderId);
    }

    private async void OnRemoveNotebookFolderContextClick(object sender, RoutedEventArgs e)
    {
        if (_notebookContextTarget is not { } target) return;
        await MoveNotebookToFolderAsync(target.Id, null);
    }

    private async void OnNewSubfolderContextClick(object sender, RoutedEventArgs e)
    {
        if (_folderContextTarget is not { } folder) return;
        try { await CreateFolderAsync(folder.Id); }
        catch (Exception exception) { ShowError("The subfolder could not be created.", exception); }
    }

    private async void OnRenameFolderContextClick(object sender, RoutedEventArgs e)
    {
        if (_folderContextTarget is not { Id: { } folderId } folder) return;
        var name = await PromptForNameAsync("Rename folder", "Folder name", folder.Name);
        if (name is null) return;
        var index = _userPreferences.NotebookFolders.FindIndex(item => item.Id == folderId);
        if (index < 0) return;
        _userPreferences.NotebookFolders[index] = _userPreferences.NotebookFolders[index] with { Name = name };
        RebuildFolderTree(_selectedFolderId);
        await PersistUserPreferencesAsync("Renamed folder");
        UpdateLibrarySummary();
    }

    private async void OnDeleteFolderContextClick(object sender, RoutedEventArgs e)
    {
        if (_folderContextTarget is not { Id: { } folderId } folder) return;
        var source = _userPreferences.NotebookFolders.FirstOrDefault(item => item.Id == folderId);
        if (source is null) return;
        var notebookCount = _userPreferences.DocumentFolders.Values.Count(value =>
            Guid.TryParse(value, out var valueId) && valueId == folderId);
        var childCount = _userPreferences.NotebookFolders.Count(item => item.ParentId == folderId);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete \u201c{folder.Name}\u201d?",
            Content = $"The folder will be removed. Its {notebookCount} notebook(s) and {childCount} subfolder(s) will move up one level; no notebooks will be deleted.",
            PrimaryButtonText = "Delete folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var key in _userPreferences.DocumentFolders
                     .Where(pair => Guid.TryParse(pair.Value, out var id) && id == folderId)
                     .Select(pair => pair.Key).ToArray())
        {
            if (source.ParentId is { } parentId) _userPreferences.DocumentFolders[key] = parentId.ToString("D");
            else _userPreferences.DocumentFolders.Remove(key);
        }
        for (var index = 0; index < _userPreferences.NotebookFolders.Count; index++)
        {
            var child = _userPreferences.NotebookFolders[index];
            if (child.ParentId == folderId)
                _userPreferences.NotebookFolders[index] = child with { ParentId = source.ParentId };
        }
        _userPreferences.NotebookFolders.RemoveAll(item => item.Id == folderId);
        if (_selectedFolderId == folderId) _selectedFolderId = source.ParentId;
        RebuildFolderTree(_selectedFolderId);
        ApplyFolderFilter();
        await PersistUserPreferencesAsync("Deleted folder; contents moved up one level");
    }

    private void OnDeletePageContextClick(object sender, RoutedEventArgs e)
    {
        if (_pageContextTarget is not { } page) return;
        PageList.SelectedItem = page;
        OnDeletePageClick(sender, e);
    }

    private async Task<string?> PromptForNameAsync(string title, string header, string currentValue)
    {
        var input = new TextBox { Header = header, Text = currentValue };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text)) return null;
        return input.Text.Trim();
    }

    private async Task LoadDocumentAsync(Guid id, Guid? pageId = null)
    {
        if (_repository is null) return;
        await SaveNowAsync();
        if (_document is not null) CacheOpenDocument(_document);
        var loaded = _openDocumentCache.GetValueOrDefault(id) ?? await _repository.LoadAsync(id);
        if (loaded is null) return;
        if (!_documentHistories.TryGetValue(id, out var history))
            _documentHistories[id] = history = new CommandHistory();
        _history = history;
        _document = loaded;
        CacheOpenDocument(loaded);
        UpdateFolderActions();
        _pendingInkAppends.Clear();
        _requiresFullSave = false;
        _hasUnsavedChanges = false;
        NotebookTitle.Text = loaded.Title;
        EnsureNotebookTab(loaded.Id, loaded.Title);
        _loading = true;
        _pages.Clear();
        foreach (var page in loaded.Pages) _pages.Add(page);
        _loading = false;
        var selectedIndex = pageId is null ? 0 : loaded.Pages.FindIndex(page => page.Id == pageId);
        PageList.SelectedIndex = Math.Max(0, selectedIndex);
        SelectPage(loaded.Pages.ElementAtOrDefault(Math.Max(0, selectedIndex)));
        ScheduleDocumentHandwritingIndex(loaded);
    }

    private void EnsureNotebookTab(Guid id, string title)
    {
        var existing = NotebookTabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(item => item.Tag is Guid tabId && tabId == id);
        if (existing is null)
        {
            existing = new TabViewItem { Header = title, Tag = id, IsClosable = true };
            NotebookTabs.TabItems.Add(existing);
        }
        else existing.Header = title;
        _updatingTabs = true;
        NotebookTabs.SelectedItem = existing;
        _updatingTabs = false;
    }

    private async void OnNotebookTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTabs || _loading || NotebookTabs.SelectedItem is not TabViewItem { Tag: Guid id } ||
            _document?.Id == id) return;
        await LoadDocumentAsync(id, _tabPageSelections.GetValueOrDefault(id) is { } pageId && pageId != Guid.Empty
            ? pageId
            : null);
        SelectLibraryDocument(id);
    }

    private async void OnNotebookTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is not TabViewItem { Tag: Guid closingId } tab || sender.TabItems.Count <= 1) return;
        await SaveNowAsync();
        var wasSelected = ReferenceEquals(sender.SelectedItem, tab);
        sender.TabItems.Remove(tab);
        if (!wasSelected) RemoveCachedDocument(closingId);
        if (wasSelected && sender.TabItems.Count > 0) sender.SelectedItem = sender.TabItems[0];
    }

    private void SelectPage(NotePage? page)
    {
        _navigationSettleTimer.Stop();
        _zoomNavigationActive = false;
        _wheelZoomAnimating = false;
        _wheelZoomTarget = _zoom;
        _recognitionTimer.Stop();
        _incrementalRecognitionCancellation?.Cancel();
        _pendingRecognitionStrokes.Clear();
        _recognitionPageId = page?.Id;
        CommitOrDiscardTextEditor();
        StashCurrentPageRenderCache();
        _searchLocateCancellation?.Cancel();
        _searchFlashBounds.Clear();
        _searchFlashStarted = 0;
        _page = page;
        _liveInkRasterReleaseTimer.Stop();
        DisposeLiveInkRaster();
        ClearStrokeGeometryCache();
        _selectedObject = null;
        _selectedObjects.Clear();
        _multiTransformPreviews.Clear();
        _transformPreview = null;
        _fitPending = true;
        _pan = Vector2.Zero;
        RestoreWarmPageRenderCache(page);
        PrepareSpatialIndex(page);
        SyncTemplatePicker();
        UpdateSelectionUi();
        UpdateLayerUi();
        DeletePageButton.IsEnabled = page is not null;
        BeginPdfPreviewLoad();
        UpdateEmptyState();
        DrawingSurface.Invalidate();
    }

    private void BeginPdfPreviewLoad()
    {
        var layer = _page?.ImportedLayer;
        if (layer is null || _assetStore is null) return;
        _ = LoadPdfPreviewAsync(_assetStore.GetPath(layer.AssetHash), layer.SourcePageIndex);
    }

    private async Task LoadPdfPreviewAsync(string path, int pageIndex)
    {
        try
        {
            await _pdfPreview.EnsureLoadedAsync(path, pageIndex);
            DispatcherQueue.TryEnqueue(() => DrawingSurface.Invalidate());
        }
        catch (Exception exception)
        {
            DispatcherQueue.TryEnqueue(() => ShowError("The imported page preview could not be rendered.", exception));
        }
    }

    private void OnCanvasCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // Device resets invalidate command lists and live-stroke resources.
        ClearWarmPageRenderCaches();
        InvalidatePageRenderCache();
        ClearLiveInkGeometryCache();
        DisposeLiveInkRaster();
        ClearStrokeGeometryCache();
        ClearImageBitmapCache();
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_page is null) return;
        var frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _frameStrokeGeometryBuilds = 0;
        if (_fitPending && sender.ActualWidth > 0 && sender.ActualHeight > 0)
        {
            _zoom = Math.Min(1, Math.Min((sender.ActualWidth - 72) / _page.Size.Width,
                (sender.ActualHeight - 72) / _page.Size.Height));
            _zoom = Math.Max(0.08, _zoom);
            _fitPending = false;
            UpdateZoomText();
        }

        var drawingSession = args.DrawingSession;
        drawingSession.Transform = PageTransform();
        var styleBrushPreview = _isPointerDown && _gestureTool == EditorTool.Style && !_styleToolPickMode;
        if (_transformPreview is null && _textPreview is null &&
            (_multiTransformPreviews.Count == 0 || styleBrushPreview))
        {
            if (ShouldDrawInteractiveViewport(_page))
                DrawInteractiveViewport(drawingSession, _page);
            else
                DrawCachedPage(sender, drawingSession, _page);
        }
        else
            DrawCommittedPage(drawingSession, _page, usePreviews: true);

        if (_isPointerDown && _gestureTool is EditorTool.SegmentEraser or EditorTool.StrokeEraser &&
            _eraseDirtyRegions.Count > 0)
            DrawRealtimeErasePreview(drawingSession, _page);

        if (styleBrushPreview)
            foreach (var preview in _multiTransformPreviews.Values.OrderBy(item => item.ZIndex))
                DrawObject(drawingSession, preview);

        if (_activeInk.Count > 0 && _gestureTool is EditorTool.Pen or EditorTool.Highlighter)
        {
            var useLiveInkRaster = CanUseLiveInkRaster();
            if (useLiveInkRaster)
                UpdateLiveInkRaster();
            if (useLiveInkRaster && _liveInkRaster is not null)
            {
                var liveOpacity = (_gestureInkStyle ?? CurrentInkStyle()).Normalize().Opacity;
                var pageTransform = drawingSession.Transform;
                drawingSession.Transform = Matrix3x2.Identity;
                if (liveOpacity < 0.995f)
                {
                    using var layer = drawingSession.CreateLayer(liveOpacity);
                    drawingSession.DrawImage(_liveInkRaster);
                }
                else
                {
                    drawingSession.DrawImage(_liveInkRaster);
                }
                drawingSession.Transform = pageTransform;
                DrawLiveInkPrediction(drawingSession);
            }
            else
                DrawLiveInk(drawingSession);
        }

        if (_isPointerDown && _gestureTool == EditorTool.Shape && _activeInk.Count > 0)
        {
            var start = _activeInk[0].Position;
            var end = _activeInk[^1].Position;
            DrawObject(drawingSession, new ShapeObject
            {
                Shape = SelectedShapeKind(), Bounds = NormalizeRect(start, end), StrokeColor = _inkColor,
                StrokeWidth = (float)StrokeWidthSlider.Value, StartPoint = start, EndPoint = end
            });
        }

        DrawSearchFlash(drawingSession);

        if (_selectedObjects.Count > 1)
            DrawSelectionBounds(drawingSession, CombinedSelectionBounds());
        else if (_selectedObject is not null)
        {
            var selected = _transformPreview ?? _textPreview ?? _selectedObject;
            if (selected.IsLocked) DrawLockedSelection(drawingSession, selected);
            else DrawSelection(drawingSession, selected);
        }

        if (_isPointerDown && _gestureTool is EditorTool.Lasso or EditorTool.BoxSelect)
            DrawSelectionMarquee(drawingSession);

        UpdateImageLockOverlay();
        RecordCanvasFrame(frameStarted);
    }

    private void RecordCanvasFrame(long frameStarted)
    {
        var elapsedMs = MillisecondsSince(frameStarted);
        if (elapsedMs < 33 || MillisecondsSince(_lastSlowFrameLogTimestamp) < 2_000 || _page is null) return;
        _lastSlowFrameLogTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var zoom = Math.Round(_zoom, 3);
        var objectCount = _page.Objects.Count;
        var pointerDown = _isPointerDown;
        var gesture = _gestureTool;
        var hasRasterCache = _lowZoomPageRaster is not null;
        var hasVectorCache = _pageRenderCache is not null;
        var overlayCount = _pageRenderOverlays.Count;
        var overlayBatchCount = _pageRenderOverlayBatches.Count;
        var cachedStrokeCount = _strokeGeometryCache.Count;
        var cachedStrokePoints = _strokeGeometryCachedPoints;
        var activeInkPoints = _activeInk.Count;
        var liveRasterActive = _liveInkRaster is not null && activeInkPoints > 0;
        var wheelZoomAnimating = _wheelZoomAnimating;
        // Synchronous file I/O from CanvasControl.Draw turns a slow frame into a larger hitch.
        // The logger is bounded to one event every two seconds, so queueing it is inexpensive.
        _ = Task.Run(() => DiagnosticsLog.Warning("render.slow_frame",
            ("elapsed_ms", Math.Round(elapsedMs, 1)),
            ("zoom", zoom),
            ("objects", objectCount),
            ("pointer_down", pointerDown),
            ("gesture", gesture),
            ("raster_cache", hasRasterCache),
            ("vector_cache", hasVectorCache),
            ("cached_strokes", cachedStrokeCount),
            ("cached_stroke_points", cachedStrokePoints),
            ("wheel_zoom", wheelZoomAnimating),
            ("active_ink_points", activeInkPoints),
            ("live_raster", liveRasterActive),
            ("overlay_objects", overlayCount),
            ("overlay_batches", overlayBatchCount)));
    }

    private void DrawCachedPage(CanvasControl sender, CanvasDrawingSession drawingSession, NotePage page)
    {
        // Normal reading and navigation should be a single textured quad, not a replay of every
        // vector command on every pan frame. Vector objects remain the source of truth and are
        // used again once the user zooms in far enough to benefit from their extra resolution.
        var useLowZoomRaster = CanUseNavigationSnapshot(page);
        if (useLowZoomRaster)
        {
            EnsureLowZoomPageRaster(sender.Device, page);
        }
        else if (_pageRenderCache is null || _pageRenderCachePageId != page.Id)
        {
            BuildVectorPageRenderCache(sender.Device, page);
        }
        while (!_isPointerDown && _touchPoints.Count == 0 &&
               _pageRenderOverlays.Count >= OverlayBatchSize)
        {
            var batch = new CanvasCommandList(sender.Device);
            using (var batchSession = batch.CreateDrawingSession())
            {
                for (var index = 0; index < OverlayBatchSize; index++)
                    DrawObject(batchSession, _pageRenderOverlays[index], cacheInkGeometry: true);
            }
            _pageRenderOverlayBatches.Add(batch);
            _pageRenderOverlays.RemoveRange(0, OverlayBatchSize);
            CompactOverlayBatches(sender.Device);
        }
        if (useLowZoomRaster &&
            _pageRenderOverlayBatches.Count * OverlayBatchSize + _pageRenderOverlays.Count >= 12)
            MergeOverlaysIntoLowZoomRaster(page);

        if (useLowZoomRaster)
        {
            if (_lowZoomPageRaster is not null)
                drawingSession.DrawImage(_lowZoomPageRaster,
                    new Rect(0, 0, page.Size.Width, page.Size.Height));
            else if (_pageRenderCache is not null)
                drawingSession.DrawImage(_pageRenderCache);
        }
        else if (_pageRenderCache is not null)
        {
            drawingSession.DrawImage(_pageRenderCache);
        }
        foreach (var batch in _pageRenderOverlayBatches) drawingSession.DrawImage(batch);
        // New pen strokes remain as a tiny overlay until the next structural edit or page
        // switch. This avoids rebuilding a dense imported page immediately after every pen-up.
        foreach (var appended in _pageRenderOverlays)
        {
            if (appended.IsHidden) continue;
            DrawObject(drawingSession, appended, cacheInkGeometry: true);
        }
    }

    private bool ShouldDrawInteractiveViewport(NotePage page)
    {
        // The retained snapshot is used only while it has enough source pixels for the current
        // monitor. Beyond that point, draw the visible vector scene through the spatial index.
        // This removes the old object-count and zoom-threshold mode switches that caused the
        // renderer to oscillate between three unrelated pipelines at intermediate zoom levels.
        return !CanUseNavigationSnapshot(page);
    }

    private bool CanUseNavigationSnapshot(NotePage page)
    {
        if (page.Size.Width <= 0 || page.Size.Height <= 0) return false;
        return RenderScalePolicy.HasNativeDisplayResolution(
            NavigationSnapshotScale(page), _zoom, DrawingSurface.Dpi);
    }

    private void DrawInteractiveViewport(CanvasDrawingSession drawingSession, NotePage page)
    {
        var visibleBounds = VisiblePageBounds(page, 32 / Math.Max(_zoom, 0.08));
        DrawPageBackground(drawingSession, page, visibleBounds);
        DrawImportedLayer(drawingSession, page);
        if (_temporaryGridVisible) DrawTemporaryGrid(drawingSession, page, visibleBounds);

        if (_spatialIndex.Count == page.Objects.Count)
        {
            _spatialIndex.Query(visibleBounds, _visibleObjectIds, _visibleObjects);
        }
        else
        {
            _visibleObjectIds.Clear();
            _visibleObjects.Clear();
            foreach (var canvasObject in page.Objects)
            {
                if (!StrokeGeometry.GetWorldBounds(canvasObject).Intersects(visibleBounds)) continue;
                _visibleObjectIds.Add(canvasObject.Id);
                _visibleObjects.Add(canvasObject);
            }
        }
        PruneStrokeGeometryCacheToViewport();
        foreach (var canvasObject in _visibleObjects)
        {
            if (!canvasObject.IsHidden) DrawObject(drawingSession, canvasObject, cacheInkGeometry: true);
        }
    }

    private RectD VisiblePageBounds(NotePage page, double padding)
    {
        if (!Matrix3x2.Invert(PageTransform(), out var inverse))
            return new RectD(0, 0, page.Size.Width, page.Size.Height);
        var topLeft = Vector2.Transform(Vector2.Zero, inverse);
        var bottomRight = Vector2.Transform(
            new Vector2((float)DrawingSurface.ActualWidth, (float)DrawingSurface.ActualHeight), inverse);
        var left = Math.Max(0, Math.Min(topLeft.X, bottomRight.X) - padding);
        var top = Math.Max(0, Math.Min(topLeft.Y, bottomRight.Y) - padding);
        var right = Math.Min(page.Size.Width, Math.Max(topLeft.X, bottomRight.X) + padding);
        var bottom = Math.Min(page.Size.Height, Math.Max(topLeft.Y, bottomRight.Y) + padding);
        return new RectD(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void CompactOverlayBatches(CanvasDevice device)
    {
        if (_pageRenderOverlayBatches.Count < OverlayBatchCompactionThreshold) return;
        var merged = new CanvasCommandList(device);
        using (var session = merged.CreateDrawingSession())
        {
            foreach (var batch in _pageRenderOverlayBatches) session.DrawImage(batch);
        }
        foreach (var batch in _pageRenderOverlayBatches) batch.Dispose();
        _pageRenderOverlayBatches.Clear();
        _pageRenderOverlayBatches.Add(merged);
    }

    private void EnsureLowZoomPageRaster(CanvasDevice device, NotePage page)
    {
        if (_lowZoomPageRaster is not null && _lowZoomPageRasterPageId == page.Id) return;
        var rasterScale = NavigationSnapshotScale(page);
        var width = Math.Max(1, page.Size.Width * rasterScale);
        var height = Math.Max(1, page.Size.Height * rasterScale);
        var raster = new CanvasRenderTarget(device, (float)width, (float)height, 96);
        using (var session = raster.CreateDrawingSession())
        {
            session.Clear(Color.FromArgb(0, 0, 0, 0));
            session.Transform = Matrix3x2.CreateScale((float)rasterScale);
            DrawCommittedPage(session, page, usePreviews: false);
        }
        _lowZoomPageRaster?.Dispose();
        _lowZoomPageRaster = raster;
        _lowZoomPageRasterPageId = page.Id;
        _pageRenderCache?.Dispose();
        _pageRenderCache = null;
        _pageRenderCachePageId = page.Id;
        foreach (var batch in _pageRenderOverlayBatches) batch.Dispose();
        _pageRenderOverlayBatches.Clear();
        _pageRenderOverlays.Clear();
        _pageRenderCacheObjectIds.Clear();
        _pageRenderCacheObjectIds.UnionWith(page.Objects.Select(item => item.Id));
    }

    private void BuildVectorPageRenderCache(CanvasDevice device, NotePage page)
    {
        if (_lowZoomPageRaster is not null && _lowZoomPageRasterPageId == page.Id &&
            (_pageRenderOverlayBatches.Count > 0 || _pageRenderOverlays.Count > 0))
            MergeOverlaysIntoLowZoomRaster(page);
        var cache = new CanvasCommandList(device);
        using (var session = cache.CreateDrawingSession())
            DrawCommittedPage(session, page, usePreviews: false);
        _pageRenderCache?.Dispose();
        _pageRenderCache = cache;
        _pageRenderCachePageId = page.Id;
        _lowZoomPageRaster?.Dispose();
        _lowZoomPageRaster = null;
        _lowZoomPageRasterPageId = null;
        foreach (var batch in _pageRenderOverlayBatches) batch.Dispose();
        _pageRenderOverlayBatches.Clear();
        _pageRenderOverlays.Clear();
        _pageRenderCacheObjectIds.Clear();
        _pageRenderCacheObjectIds.UnionWith(page.Objects.Select(item => item.Id));
    }

    private void MergeOverlaysIntoLowZoomRaster(NotePage page)
    {
        if (_lowZoomPageRaster is null || _lowZoomPageRasterPageId != page.Id) return;
        if (_pageRenderOverlayBatches.Count == 0 && _pageRenderOverlays.Count == 0) return;
        using (var session = _lowZoomPageRaster.CreateDrawingSession())
        {
            session.Transform = Matrix3x2.CreateScale((float)NavigationSnapshotScale(page));
            foreach (var batch in _pageRenderOverlayBatches) session.DrawImage(batch);
            foreach (var overlay in _pageRenderOverlays)
                if (!overlay.IsHidden) DrawObject(session, overlay);
        }
        foreach (var batch in _pageRenderOverlayBatches) batch.Dispose();
        _pageRenderOverlayBatches.Clear();
        _pageRenderCacheObjectIds.UnionWith(_pageRenderOverlays.Select(item => item.Id));
        _pageRenderOverlays.Clear();
        // A vector command list recorded before these overlays were merged no longer contains
        // the complete page. Rebuild it lazily only after interaction returns to detail mode.
        _pageRenderCache?.Dispose();
        _pageRenderCache = null;
        _pageRenderCachePageId = null;
    }

    private static double NavigationSnapshotScale(NotePage page) =>
        RenderScalePolicy.ComputeSnapshotScale(
            page.Size.Width,
            page.Size.Height,
            NavigationSnapshotByteBudget);

    private void DrawCommittedPage(CanvasDrawingSession drawingSession, NotePage page, bool usePreviews)
    {
        DrawPageBackground(drawingSession, page);
        DrawImportedLayer(drawingSession, page);
        // The temporary grid sits above paper/PDF backgrounds but below all authored content.
        // This keeps it useful as a guide without obscuring handwriting.
        if (_temporaryGridVisible) DrawTemporaryGrid(drawingSession, page);
        // Document commands maintain z-order, so sorting this dense list again whenever a
        // cache is recorded is redundant O(n log n) work on the UI thread.
        foreach (var canvasObject in page.Objects)
        {
            if (canvasObject.IsHidden) continue;
            var rendered = usePreviews
                ? _multiTransformPreviews.TryGetValue(canvasObject.Id, out var multiPreview)
                    ? multiPreview
                    : _transformPreview?.Id == canvasObject.Id ? _transformPreview :
                        _textPreview?.Id == canvasObject.Id ? _textPreview : canvasObject
                : canvasObject;
            DrawObject(drawingSession, rendered);
        }
    }

    private void DrawLiveInk(CanvasDrawingSession drawingSession)
    {
        var style = _gestureInkStyle ?? CurrentInkStyle();
        var color = ParseColor(style.Color, style.Opacity);
        var width = style.Width;
        if (style.Tool == InkToolKind.Highlighter && HighlighterStraightToggle.IsOn && _activeInk.Count > 1)
        {
            var snappedEnd = SnapHighlighterEnd(_activeInk[0], _activeInk[^1]);
            drawingSession.DrawLine(_activeInk[0].Position.ToVector2(), snappedEnd.Position.ToVector2(),
                color, width, _roundInkStrokeStyle);
            return;
        }
        if (_activeInk.Count == 1)
        {
            var point = _activeInk[0];
            drawingSession.FillCircle((float)point.X, (float)point.Y, width / 2f, color);
            return;
        }

        // Opaque handwriting is split into immutable GPU chunks. Each pointer frame rebuilds
        // at most the short tail instead of walking the full stroke as it grows.
        if (style.Tool != InkToolKind.Highlighter)
        {
            while (_activeInk.Count - _liveInkChunkStart >= LiveInkChunkSize)
            {
                using var chunkGeometry = CreateSmoothCenterlineGeometry(drawingSession, _activeInk,
                    _liveInkChunkStart, LiveInkChunkSize);
                _liveInkGeometryChunks.Add(CanvasCachedGeometry.CreateStroke(
                    chunkGeometry, width, _roundInkStrokeStyle, 0.08f));
                _liveInkChunkStart += LiveInkChunkSize - 1;
            }
            foreach (var chunk in _liveInkGeometryChunks)
                drawingSession.DrawCachedGeometry(chunk, color);
        }

        var tailStart = style.Tool == InkToolKind.Highlighter ? 0 : _liveInkChunkStart;
        var tailCount = _activeInk.Count - tailStart;
        if (tailCount < 2) return;
        using var geometry = CreateSmoothCenterlineGeometry(drawingSession, _activeInk, tailStart, tailCount);
        drawingSession.DrawGeometry(geometry, color, width, _roundInkStrokeStyle);
    }

    private bool CanUseLiveInkRaster()
    {
        if (_page is null || _gestureTool is not (EditorTool.Pen or EditorTool.Highlighter)) return false;
        if (_gestureTool == EditorTool.Highlighter && HighlighterStraightToggle.IsOn) return false;
        return DrawingSurface.ActualWidth > 0 && DrawingSurface.ActualHeight > 0;
    }

    private void ResetLiveInkRaster()
    {
        _liveInkRasterPointIndex = 0;
        if (!CanUseLiveInkRaster() || _page is null) return;
        var width = DrawingSurface.ActualWidth;
        var height = DrawingSurface.ActualHeight;
        var dpi = DrawingSurface.Dpi;
        if (_liveInkRaster is null || _liveInkRasterPageId != _page.Id ||
            Math.Abs(_liveInkRasterWidth - width) > 0.5 ||
            Math.Abs(_liveInkRasterHeight - height) > 0.5 ||
            Math.Abs(_liveInkRasterDpi - dpi) > 0.1f)
        {
            DisposeLiveInkRaster();
            _liveInkRaster = new CanvasRenderTarget(DrawingSurface.Device,
                (float)Math.Max(1, width), (float)Math.Max(1, height), dpi);
            _liveInkRasterPageId = _page.Id;
            _liveInkRasterWidth = width;
            _liveInkRasterHeight = height;
            _liveInkRasterDpi = dpi;
        }
        _liveInkPageToScreen = _gestureScreenToPageValid &&
                               Matrix3x2.Invert(_gestureScreenToPage, out var pageToScreen)
            ? pageToScreen
            : PageTransform();
        using var session = _liveInkRaster.CreateDrawingSession();
        session.Clear(Color.FromArgb(0, 0, 0, 0));
    }

    private void UpdateLiveInkRaster()
    {
        if (!CanUseLiveInkRaster() || _page is null || _activeInk.Count == 0) return;
        if (_liveInkRaster is null || _liveInkRasterPageId != _page.Id) ResetLiveInkRaster();
        if (_liveInkRaster is null) return;
        var style = (_gestureInkStyle ?? CurrentInkStyle()).Normalize();
        // Build one opaque mask for the entire live stroke, then apply its opacity once while
        // compositing. This prevents translucent pen/highlighter segments from darkening where
        // the incremental line pieces overlap.
        var color = ParseColor(style.Color);
        using var session = _liveInkRaster.CreateDrawingSession();
        session.Transform = _liveInkPageToScreen;
        if (_liveInkRasterPointIndex == 0)
        {
            var first = _activeInk[0];
            session.FillCircle((float)first.X, (float)first.Y, style.Width / 2f, color);
            _liveInkRasterPointIndex = 1;
        }
        var start = Math.Max(1, _liveInkRasterPointIndex);
        for (var index = start; index < _activeInk.Count; index++)
        {
            var previous = _activeInk[index - 1];
            var current = _activeInk[index];
            session.DrawLine(previous.Position.ToVector2(), current.Position.ToVector2(),
                color, style.Width, _roundInkStrokeStyle);
        }
        _liveInkRasterPointIndex = _activeInk.Count;
    }

    private void DisposeLiveInkRaster()
    {
        _liveInkRaster?.Dispose();
        _liveInkRaster = null;
        _liveInkRasterPageId = null;
        _liveInkRasterPointIndex = 0;
        _liveInkRasterWidth = 0;
        _liveInkRasterHeight = 0;
        _liveInkRasterDpi = 0;
        _liveInkPageToScreen = Matrix3x2.Identity;
    }

    private void DrawLiveInkPrediction(CanvasDrawingSession drawingSession)
    {
        if (_activeInk.Count < 2 || !CanUseLiveInkRaster()) return;
        var style = (_gestureInkStyle ?? CurrentInkStyle()).Normalize();
        if (style.Tool != InkToolKind.Pen || style.Opacity < 0.995f) return;
        var previous = _activeInk[^2];
        var latest = _activeInk[^1];
        var delta = latest.Position.ToVector2() - previous.Position.ToVector2();
        var pageDistance = delta.Length();
        var elapsedMicroseconds = latest.TimestampMicroseconds - previous.TimestampMicroseconds;
        if (pageDistance <= 0.0001f || elapsedMicroseconds <= 0 || elapsedMicroseconds > 100_000) return;

        // Extrapolate only smooth motion. Prediction is suppressed immediately at sharp turns,
        // where overshoot would look worse than one frame of latency.
        var turnConfidence = 1f;
        if (_activeInk.Count >= 3)
        {
            var beforePrevious = _activeInk[^3];
            var priorDelta = previous.Position.ToVector2() - beforePrevious.Position.ToVector2();
            var priorLength = priorDelta.Length();
            if (priorLength > 0.0001f)
            {
                var cosine = Vector2.Dot(Vector2.Normalize(priorDelta), Vector2.Normalize(delta));
                if (cosine <= 0.35f) return;
                turnConfidence = Math.Clamp((cosine - 0.35f) / 0.65f, 0f, 1f);
            }
        }

        const double predictionHorizonMicroseconds = 7_000;
        var factor = (float)Math.Min(1.5, predictionHorizonMicroseconds / elapsedMicroseconds) *
                     turnConfidence;
        var predictedScreenDistance = pageDistance * (float)_zoom * factor;
        if (predictedScreenDistance > 12f) factor *= 12f / predictedScreenDistance;
        if (factor <= 0.02f) return;

        var predicted = latest.Position.ToVector2() + delta * factor;
        drawingSession.DrawLine(latest.Position.ToVector2(), predicted,
            ParseColor(style.Color, style.Opacity), style.Width, _roundInkStrokeStyle);
    }

    private void DrawTemporaryGrid(CanvasDrawingSession drawingSession, NotePage page,
        RectD? visibleBounds = null)
    {
        var spacing = Math.Clamp(_temporaryGridSize, 8, 128);
        var visible = visibleBounds ?? new RectD(0, 0, page.Size.Width, page.Size.Height);
        var color = IsDarkColor(page.Template.PaperColor)
            ? Color.FromArgb(95, 150, 176, 210)
            : Color.FromArgb(70, 62, 96, 140);
        var firstX = Math.Max(0, Math.Floor(visible.Left / spacing) * spacing);
        var firstY = Math.Max(0, Math.Floor(visible.Top / spacing) * spacing);
        for (var x = firstX; x <= Math.Min(page.Size.Width, visible.Right); x += spacing)
            drawingSession.DrawLine((float)x, 0, (float)x, (float)page.Size.Height, color, 0.8f);
        for (var y = firstY; y <= Math.Min(page.Size.Height, visible.Bottom); y += spacing)
            drawingSession.DrawLine(0, (float)y, (float)page.Size.Width, (float)y, color, 0.8f);
    }

    private void DrawPageBackground(CanvasDrawingSession drawingSession, NotePage page,
        RectD? visibleBounds = null)
    {
        var paper = ParseColor(page.Template.PaperColor);
        var line = ParseColor(page.Template.LineColor);
        drawingSession.FillRectangle(0, 0, (float)page.Size.Width, (float)page.Size.Height, paper);
        var spacing = Math.Max(4, page.Template.Spacing);
        var visible = visibleBounds ?? new RectD(0, 0, page.Size.Width, page.Size.Height);
        var firstX = Math.Max(0, Math.Floor(visible.Left / spacing) * spacing);
        var firstY = Math.Max(0, Math.Floor(visible.Top / spacing) * spacing);
        switch (page.Template.Kind)
        {
            case PageTemplateKind.Lined:
                firstY = Math.Max(page.Template.Margin,
                    page.Template.Margin + Math.Floor((visible.Top - page.Template.Margin) / spacing) * spacing);
                for (var y = firstY; y <= Math.Min(page.Size.Height, visible.Bottom); y += spacing)
                    drawingSession.DrawLine((float)page.Template.Margin, (float)y,
                        (float)(page.Size.Width - page.Template.Margin), (float)y, line, (float)page.Template.LineWidth);
                break;
            case PageTemplateKind.Dotted:
                firstX = Math.Max(page.Template.Margin,
                    page.Template.Margin + Math.Floor((visible.Left - page.Template.Margin) / spacing) * spacing);
                firstY = Math.Max(page.Template.Margin,
                    page.Template.Margin + Math.Floor((visible.Top - page.Template.Margin) / spacing) * spacing);
                for (var x = firstX; x <= Math.Min(page.Size.Width - page.Template.Margin, visible.Right); x += spacing)
                for (var y = firstY; y <= Math.Min(page.Size.Height - page.Template.Margin, visible.Bottom); y += spacing)
                    drawingSession.FillCircle((float)x, (float)y, 1.1f, line);
                break;
            case PageTemplateKind.SquareGrid:
            case PageTemplateKind.Graph:
                var graph = page.Template.Kind == PageTemplateKind.Graph;
                var count = 0;
                count = (int)Math.Round(firstX / spacing);
                for (var x = firstX; x <= Math.Min(page.Size.Width, visible.Right); x += spacing, count++)
                    drawingSession.DrawLine((float)x, 0, (float)x, (float)page.Size.Height, line,
                        graph && count % 5 == 0 ? 1.4f : (float)page.Template.LineWidth);
                count = (int)Math.Round(firstY / spacing);
                for (var y = firstY; y <= Math.Min(page.Size.Height, visible.Bottom); y += spacing, count++)
                    drawingSession.DrawLine(0, (float)y, (float)page.Size.Width, (float)y, line,
                        graph && count % 5 == 0 ? 1.4f : (float)page.Template.LineWidth);
                break;
        }
    }

    private void DrawImportedLayer(CanvasDrawingSession drawingSession, NotePage page)
    {
        var layer = page.ImportedLayer;
        if (layer is null || !layer.IsVisible || _assetStore is null) return;
        var path = _assetStore.GetPath(layer.AssetHash);
        var bitmap = _pdfPreview.TryGet(path, layer.SourcePageIndex);
        if (bitmap is not null)
        {
            var previous = drawingSession.Transform;
            drawingSession.Transform = layer.Transform.ToMatrix() * previous;
            drawingSession.DrawImage(bitmap, new Rect(0, 0, page.Size.Width, page.Size.Height));
            drawingSession.Transform = previous;
        }
        else
        {
            // Selection can happen before the async page load or after a device/resource reset.
            // Re-requesting here is cheap (the cache de-duplicates loads) and makes the canvas
            // self-healing instead of relying on an unrelated UI toggle to trigger a redraw.
            BeginPdfPreviewLoad();
            drawingSession.FillRectangle(0, 0, (float)page.Size.Width, 42, Color.FromArgb(210, 37, 43, 54));
            drawingSession.DrawText($"Loading {layer.SourceName} • page {layer.SourcePageIndex + 1}", 18, 12,
                Color.FromArgb(255, 218, 225, 235));
        }
    }

    private void DrawObject(CanvasDrawingSession drawingSession, CanvasObject canvasObject,
        bool cacheInkGeometry = false)
    {
        var previous = drawingSession.Transform;
        drawingSession.Transform = canvasObject.Transform.ToMatrix() * previous;
        switch (canvasObject)
        {
            case InkStrokeObject ink:
                DrawInk(drawingSession, ink, cacheInkGeometry);
                break;
            case RichTextObject text:
                using (var format = CreateTextFormat(text))
                {
                    drawingSession.DrawText(text.Content.PlainText,
                        new Rect(text.Bounds.X, text.Bounds.Y, text.Bounds.Width, text.Bounds.Height),
                        ParseColor(text.Content.Paragraphs.FirstOrDefault()?.Runs.FirstOrDefault()?.Color ?? "#F4F7FB"), format);
                }
                break;
            case ShapeObject shape:
                DrawShape(drawingSession, shape);
                break;
            case ImageObject image:
                DrawImageObject(drawingSession, image);
                break;
        }
        drawingSession.Transform = previous;
    }

    private static CanvasTextFormat CreateTextFormat(RichTextObject text)
    {
        var paragraph = text.Content.Paragraphs.FirstOrDefault();
        var run = paragraph?.Runs.FirstOrDefault();
        var textSize = paragraph?.Kind switch
        {
            ParagraphKind.Heading1 => text.Content.FontSize * 1.65f,
            ParagraphKind.Heading2 => text.Content.FontSize * 1.3f,
            _ => text.Content.FontSize
        };
        return new CanvasTextFormat
        {
            FontFamily = text.Content.FontFamily,
            FontSize = textSize,
            FontWeight = run?.Bold == true ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = run?.Italic == true ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = CanvasWordWrapping.Wrap
        };
    }

    private void DrawImageObject(CanvasDrawingSession drawingSession, ImageObject image)
    {
        if (_imageBitmapCache.TryGetValue(image.AssetHash, out var bitmap))
        {
            TouchImageBitmap(image.AssetHash);
            drawingSession.DrawImage(bitmap, new Rect(image.Bounds.X, image.Bounds.Y,
                image.Bounds.Width, image.Bounds.Height));
            return;
        }

        drawingSession.FillRectangle((float)image.Bounds.X, (float)image.Bounds.Y,
            (float)image.Bounds.Width, (float)image.Bounds.Height, Color.FromArgb(255, 42, 47, 58));
        drawingSession.DrawRectangle((float)image.Bounds.X, (float)image.Bounds.Y,
            (float)image.Bounds.Width, (float)image.Bounds.Height, Color.FromArgb(255, 113, 167, 255), 1);
        drawingSession.DrawText("Loading image…", (float)image.Bounds.X + 12, (float)image.Bounds.Y + 12,
            Color.FromArgb(255, 210, 218, 230));
        BeginImageLoad(image);
    }

    private void BeginImageLoad(ImageObject image)
    {
        var assetHash = image.AssetHash;
        var generation = _imageLoadGeneration;
        var pendingKey = $"{generation}:{assetHash}";
        if (_assetStore is null || string.IsNullOrWhiteSpace(assetHash) ||
            _imageBitmapCache.ContainsKey(assetHash) || _failedImageLoads.Contains(pendingKey)) return;
        if (_page is not null)
        {
            if (!_imageWaitingPages.TryGetValue(assetHash, out var pages))
                _imageWaitingPages[assetHash] = pages = [];
            pages.Add(_page.Id);
        }
        if (!_pendingImageLoads.Add(pendingKey)) return;
        _ = LoadImageBitmapAsync(assetHash, image.Bounds.Width, image.Bounds.Height, generation, pendingKey);
    }

    private async Task LoadImageBitmapAsync(string assetHash, double displayWidth, double displayHeight,
        int generation, string pendingKey)
    {
        try
        {
            if (_assetStore is null) return;
            var loaded = await LoadDownsampledBitmapAsync(assetHash, displayWidth, displayHeight);
            var bitmap = loaded.Bitmap;
            if (generation != _imageLoadGeneration)
            {
                bitmap.Dispose();
                return;
            }
            CacheImageBitmap(assetHash, bitmap);
        }
        catch (Exception exception)
        {
            if (generation == _imageLoadGeneration) _failedImageLoads.Add(pendingKey);
            ShowError("An image could not be rendered.", exception);
        }
        finally
        {
            _pendingImageLoads.Remove(pendingKey);
            if (_imageWaitingPages.Remove(assetHash, out var waitingPages))
                _imagePagesNeedingRefresh.UnionWith(waitingPages);
            if (generation == _imageLoadGeneration &&
                !_pendingImageLoads.Any(key => key.StartsWith($"{generation}:", StringComparison.Ordinal)))
            {
                // Re-record a dense page once after its image batch is ready, rather than once
                // per image. Rebuilding thousands of vector strokes for every decode caused a
                // visible sequence of stalls on Samsung pages with many embedded images.
                foreach (var pageId in _imagePagesNeedingRefresh) RemoveWarmPageRenderCache(pageId);
                if (_page is not null && _imagePagesNeedingRefresh.Contains(_page.Id))
                {
                    InvalidatePageRenderCache();
                    DrawingSurface.Invalidate();
                }
                _imagePagesNeedingRefresh.Clear();
            }
        }
    }

    private async Task<(CanvasBitmap Bitmap, int SourceWidth, int SourceHeight)> LoadDownsampledBitmapAsync(
        string assetHash, double displayWidth, double displayHeight)
    {
        if (_assetStore is null) throw new InvalidOperationException("The asset store is not initialized.");
        await _imageDecodeGate.WaitAsync();
        try
        {
            var source = await CanvasBitmap.LoadAsync(DrawingSurface, _assetStore.GetPath(assetHash));
            var sourceWidth = checked((int)Math.Max(1u, source.SizeInPixels.Width));
            var sourceHeight = checked((int)Math.Max(1u, source.SizeInPixels.Height));
            var desiredLongEdge = Math.Clamp(Math.Max(displayWidth, displayHeight) * 1.5, 256, 1_600);
            var scale = Math.Min(1d, desiredLongEdge / Math.Max(sourceWidth, sourceHeight));
            if (scale >= 0.995) return (source, sourceWidth, sourceHeight);

            var targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            var reduced = new CanvasRenderTarget(DrawingSurface, targetWidth, targetHeight, 96);
            using (var session = reduced.CreateDrawingSession())
            {
                session.Clear(Color.FromArgb(0, 0, 0, 0));
                session.DrawImage(source, new Rect(0, 0, targetWidth, targetHeight));
            }
            source.Dispose();
            return (reduced, sourceWidth, sourceHeight);
        }
        finally
        {
            _imageDecodeGate.Release();
        }
    }

    private void DrawInk(CanvasDrawingSession drawingSession, InkStrokeObject stroke,
        bool cacheGeometry = false)
    {
        if (stroke.Points.Count == 0) return;
        var normalizedStyle = stroke.Style.Normalize();
        var color = ParseColor(normalizedStyle.Color, normalizedStyle.Opacity);
        if (cacheGeometry && TryDrawCachedInk(drawingSession, stroke, color)) return;
        if (StrokeOutlineBuilder.UsesCenterlineStroke(stroke))
        {
            DrawCenterlineInk(drawingSession, stroke, color);
            return;
        }

        var outline = StrokeOutlineBuilder.Build(stroke.Points, stroke.Style);
        if (outline.Contour.Count < 3)
        {
            var point = stroke.Points[0];
            drawingSession.FillCircle((float)point.X, (float)point.Y,
                StrokeOutlineBuilder.EffectiveWidth(point, stroke.Style) / 2f, color);
            return;
        }

        using var geometry = CreateWindingGeometry(drawingSession, outline.Contour);
        drawingSession.FillGeometry(geometry, color);
    }

    private bool TryDrawCachedInk(CanvasDrawingSession drawingSession, InkStrokeObject stroke, Color color)
    {
        if (stroke.Points.Count < 2) return false;
        if (_strokeGeometryCache.TryGetValue(stroke.Id, out var existing))
        {
            if (ReferenceEquals(existing.Stroke, stroke))
            {
                TouchStrokeGeometry(stroke.Id);
                DrawStrokeGeometry(drawingSession, existing);
                return true;
            }
            existing.Geometry.Dispose();
            _strokeGeometryCache.Remove(stroke.Id);
            _strokeGeometryCachedPoints = Math.Max(0,
                _strokeGeometryCachedPoints - existing.Stroke.Points.Count);
            RemoveStrokeGeometryLruNode(stroke.Id);
        }

        // Once the native-memory budget is full, draw misses directly. Evicting a cached stroke
        // here would make a dense visible page rebuild and dispose geometries every frame.
        if (_strokeGeometryCache.Count >= StrokeGeometryCacheLimit ||
            stroke.Points.Count > StrokeGeometryCachePointLimit - _strokeGeometryCachedPoints) return false;
        if (_frameStrokeGeometryBuilds >= FrameStrokeGeometryBuildLimit) return false;
        _frameStrokeGeometryBuilds++;
        var cached = CreateStrokeGeometry(drawingSession, stroke, color);
        if (cached is null) return false;
        CacheStrokeGeometry(cached);
        DrawStrokeGeometry(drawingSession, cached);
        return true;
    }

    private StrokeGeometryCacheEntry? CreateStrokeGeometry(
        ICanvasResourceCreator resourceCreator,
        InkStrokeObject stroke,
        Color color)
    {
        if (StrokeOutlineBuilder.UsesCenterlineStroke(stroke))
        {
            var centerline = StrokeOutlineBuilder.FitCenterline(stroke);
            if (centerline.Count < 2) return null;
            return new StrokeGeometryCacheEntry(
                stroke,
                CreateCenterlineGeometry(resourceCreator, centerline),
                color,
                IsCenterline: true,
                Width: StrokeOutlineBuilder.VectorCenterlineWidth(stroke.Style));
        }

        var outline = StrokeOutlineBuilder.Build(stroke.Points, stroke.Style);
        if (outline.Contour.Count < 3) return null;
        return new StrokeGeometryCacheEntry(
            stroke,
            CreateWindingGeometry(resourceCreator, outline.Contour),
            color,
            IsCenterline: false,
            Width: 0);
    }

    private void DrawStrokeGeometry(CanvasDrawingSession drawingSession, StrokeGeometryCacheEntry entry)
    {
        if (entry.IsCenterline)
            drawingSession.DrawGeometry(entry.Geometry, entry.Color, entry.Width, _roundInkStrokeStyle);
        else
            drawingSession.FillGeometry(entry.Geometry, entry.Color);
    }

    private void CacheStrokeGeometry(StrokeGeometryCacheEntry entry)
    {
        _strokeGeometryCache[entry.Stroke.Id] = entry;
        _strokeGeometryCachedPoints += entry.Stroke.Points.Count;
        var node = _strokeGeometryLru.AddFirst(entry.Stroke.Id);
        _strokeGeometryLruNodes[entry.Stroke.Id] = node;
        while (_strokeGeometryLru.Count > StrokeGeometryCacheLimit)
        {
            var evicted = _strokeGeometryLru.Last!.Value;
            RemoveStrokeGeometryLruNode(evicted);
            if (_strokeGeometryCache.Remove(evicted, out var evictedEntry))
            {
                _strokeGeometryCachedPoints = Math.Max(0,
                    _strokeGeometryCachedPoints - evictedEntry.Stroke.Points.Count);
                evictedEntry.Geometry.Dispose();
            }
        }
    }

    private void PruneStrokeGeometryCacheToViewport()
    {
        if (_strokeGeometryCache.Count < StrokeGeometryCacheLimit * 3 / 4 &&
            _strokeGeometryCachedPoints < StrokeGeometryCachePointLimit * 3 / 4) return;
        foreach (var strokeId in _strokeGeometryCache.Keys
                     .Where(strokeId => !_visibleObjectIds.Contains(strokeId))
                     .ToArray())
        {
            if (!_strokeGeometryCache.Remove(strokeId, out var entry)) continue;
            entry.Geometry.Dispose();
            _strokeGeometryCachedPoints = Math.Max(0,
                _strokeGeometryCachedPoints - entry.Stroke.Points.Count);
            RemoveStrokeGeometryLruNode(strokeId);
        }
    }

    private void TouchStrokeGeometry(Guid strokeId)
    {
        if (!_strokeGeometryLruNodes.TryGetValue(strokeId, out var node)) return;
        _strokeGeometryLru.Remove(node);
        _strokeGeometryLru.AddFirst(node);
    }

    private void RemoveStrokeGeometryLruNode(Guid strokeId)
    {
        if (!_strokeGeometryLruNodes.Remove(strokeId, out var node)) return;
        _strokeGeometryLru.Remove(node);
    }

    private void DrawCenterlineInk(CanvasDrawingSession drawingSession, InkStrokeObject stroke, Color color)
    {
        // Keep width in document space so fine handwriting becomes proportionally thinner as
        // the page is zoomed out. Direct2D handles subpixel antialiasing at the viewport edge.
        var width = StrokeOutlineBuilder.VisibleCenterlineWidth(stroke.Style, _zoom);
        if (stroke.Points.Count == 1)
        {
            var point = stroke.Points[0];
            drawingSession.FillCircle((float)point.X, (float)point.Y, width / 2f, color);
            return;
        }

        var centerline = StrokeOutlineBuilder.FitCenterline(stroke);
        if (centerline.Count == 0) return;
        if (centerline.Count == 1)
        {
            var point = centerline[0];
            drawingSession.FillCircle((float)point.X, (float)point.Y, width / 2f, color);
            return;
        }

        using var geometry = CreateCenterlineGeometry(drawingSession, centerline);
        drawingSession.DrawGeometry(geometry, color, width, _roundInkStrokeStyle);
    }

    private static CanvasGeometry CreateCenterlineGeometry(ICanvasResourceCreator resourceCreator,
        IReadOnlyList<InkPoint> centerline)
        => CreateCenterlineGeometry(resourceCreator, centerline, 0, centerline.Count);

    private static CanvasGeometry CreateCenterlineGeometry(ICanvasResourceCreator resourceCreator,
        IReadOnlyList<InkPoint> centerline, int start, int count)
    {
        using var path = new CanvasPathBuilder(resourceCreator);
        path.BeginFigure(centerline[start].Position.ToVector2());
        var end = start + count;
        for (var index = start + 1; index < end; index++)
            path.AddLine(centerline[index].Position.ToVector2());
        path.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(path);
    }

    private static CanvasGeometry CreateSmoothCenterlineGeometry(ICanvasResourceCreator resourceCreator,
        IReadOnlyList<InkPoint> centerline, int start, int count)
    {
        if (count < 3) return CreateCenterlineGeometry(resourceCreator, centerline, start, count);
        using var path = new CanvasPathBuilder(resourceCreator);
        var current = centerline[start].Position.ToVector2();
        path.BeginFigure(current);
        var end = start + count;
        for (var index = start + 1; index < end - 1; index++)
        {
            var control = centerline[index].Position.ToVector2();
            var next = centerline[index + 1].Position.ToVector2();
            var midpoint = (control + next) * 0.5f;
            path.AddCubicBezier(
                current + (control - current) * (2f / 3f),
                midpoint + (control - midpoint) * (2f / 3f),
                midpoint);
            current = midpoint;
        }
        var finalControl = centerline[end - 2].Position.ToVector2();
        var final = centerline[end - 1].Position.ToVector2();
        path.AddCubicBezier(
            current + (finalControl - current) * (2f / 3f),
            final + (finalControl - final) * (2f / 3f),
            final);
        path.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(path);
    }

    private static CanvasGeometry CreateWindingGeometry(ICanvasResourceCreator resourceCreator,
        IReadOnlyList<PointD> contour)
    {
        using var path = new CanvasPathBuilder(resourceCreator);
        path.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        path.BeginFigure(contour[0].ToVector2());
        for (var index = 1; index < contour.Count; index++) path.AddLine(contour[index].ToVector2());
        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }

    private void DrawShape(CanvasDrawingSession drawingSession, ShapeObject shape)
    {
        var color = ParseColor(shape.StrokeColor);
        var bounds = shape.Bounds;
        switch (shape.Shape)
        {
            case ShapeKind.Ellipse:
                drawingSession.DrawEllipse((float)bounds.Center.X, (float)bounds.Center.Y,
                    (float)(bounds.Width / 2), (float)(bounds.Height / 2), color, shape.StrokeWidth);
                break;
            case ShapeKind.RoundedRectangle:
                drawingSession.DrawRoundedRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width,
                    (float)bounds.Height, 12, 12, color, shape.StrokeWidth);
                break;
            case ShapeKind.Line:
                var lineStart = shape.StartPoint ?? new PointD(bounds.Left, bounds.Top);
                var lineEnd = shape.EndPoint ?? new PointD(bounds.Right, bounds.Bottom);
                drawingSession.DrawLine(lineStart.ToVector2(), lineEnd.ToVector2(), color, shape.StrokeWidth,
                    _roundInkStrokeStyle);
                break;
            case ShapeKind.Arrow:
                var arrowStart = shape.StartPoint ?? new PointD(bounds.Left, bounds.Top);
                var arrowEnd = shape.EndPoint ?? new PointD(bounds.Right, bounds.Bottom);
                drawingSession.DrawLine(arrowStart.ToVector2(), arrowEnd.ToVector2(), color, shape.StrokeWidth);
                var angle = Math.Atan2(arrowEnd.Y - arrowStart.Y, arrowEnd.X - arrowStart.X);
                var length = Vector2.Distance(arrowStart.ToVector2(), arrowEnd.ToVector2());
                var head = Math.Clamp(length * 0.2, 9, 24);
                drawingSession.DrawLine((float)arrowEnd.X, (float)arrowEnd.Y,
                    (float)(arrowEnd.X - Math.Cos(angle - 0.55) * head),
                    (float)(arrowEnd.Y - Math.Sin(angle - 0.55) * head), color, shape.StrokeWidth);
                drawingSession.DrawLine((float)arrowEnd.X, (float)arrowEnd.Y,
                    (float)(arrowEnd.X - Math.Cos(angle + 0.55) * head),
                    (float)(arrowEnd.Y - Math.Sin(angle + 0.55) * head), color, shape.StrokeWidth);
                break;
            case ShapeKind.Triangle:
                drawingSession.DrawLine((float)bounds.Center.X, (float)bounds.Top, (float)bounds.Right,
                    (float)bounds.Bottom, color, shape.StrokeWidth);
                drawingSession.DrawLine((float)bounds.Right, (float)bounds.Bottom, (float)bounds.Left,
                    (float)bounds.Bottom, color, shape.StrokeWidth);
                drawingSession.DrawLine((float)bounds.Left, (float)bounds.Bottom, (float)bounds.Center.X,
                    (float)bounds.Top, color, shape.StrokeWidth);
                break;
            case ShapeKind.Diamond:
                var vertices = new[]
                {
                    bounds.Center with { Y = bounds.Top }, bounds.Center with { X = bounds.Right },
                    bounds.Center with { Y = bounds.Bottom }, bounds.Center with { X = bounds.Left }
                };
                for (var index = 0; index < vertices.Length; index++)
                {
                    var next = vertices[(index + 1) % vertices.Length];
                    drawingSession.DrawLine((float)vertices[index].X, (float)vertices[index].Y,
                        (float)next.X, (float)next.Y, color, shape.StrokeWidth);
                }
                break;
            default:
                drawingSession.DrawRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width,
                    (float)bounds.Height, color, shape.StrokeWidth);
                break;
        }
    }

    private void DrawSelection(CanvasDrawingSession drawingSession, CanvasObject selected)
    {
        DrawSelectionBounds(drawingSession, StrokeGeometry.GetWorldBounds(selected));
    }

    private void DrawLockedSelection(CanvasDrawingSession drawingSession, CanvasObject selected)
    {
        var bounds = StrokeGeometry.GetWorldBounds(selected);
        DrawSelectionBounds(drawingSession, bounds, locked: true);
    }

    private void DrawSelectionBounds(CanvasDrawingSession drawingSession, RectD bounds, bool locked = false)
    {
        var scale = (float)Math.Max(_zoom, 0.01);
        var accent = locked ? Color.FromArgb(255, 255, 166, 64) : Color.FromArgb(255, 75, 174, 255);
        var halo = Color.FromArgb(235, 8, 12, 18);
        var fill = Color.FromArgb(255, 250, 252, 255);
        var outerWidth = 4.5f / scale;
        var innerWidth = 2f / scale;
        drawingSession.DrawRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width,
            (float)bounds.Height, halo, outerWidth);
        drawingSession.DrawRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width,
            (float)bounds.Height, accent, innerWidth);
        var handles = SelectionTransformer.GetHandles(bounds);
        var rotate = handles[TransformHandle.Rotate];
        drawingSession.DrawLine((float)bounds.Center.X, (float)bounds.Top, (float)rotate.X,
            (float)rotate.Y, halo, outerWidth);
        drawingSession.DrawLine((float)bounds.Center.X, (float)bounds.Top, (float)rotate.X,
            (float)rotate.Y, accent, innerWidth);
        var radius = 7.5f / scale;
        var haloRadius = 10f / scale;
        foreach (var (kind, handle) in handles)
        {
            if (kind == TransformHandle.Rotate)
            {
                drawingSession.FillCircle((float)handle.X, (float)handle.Y, haloRadius, halo);
                drawingSession.FillCircle((float)handle.X, (float)handle.Y, radius,
                    locked ? Color.FromArgb(255, 145, 145, 145) : Color.FromArgb(255, 255, 196, 77));
                drawingSession.DrawCircle((float)handle.X, (float)handle.Y, radius, fill, 1.5f / scale);
                continue;
            }

            var outer = new Rect((float)handle.X - haloRadius, (float)handle.Y - haloRadius,
                haloRadius * 2, haloRadius * 2);
            var inner = new Rect((float)handle.X - radius, (float)handle.Y - radius,
                radius * 2, radius * 2);
            drawingSession.FillRectangle(outer, halo);
            drawingSession.FillRectangle(inner, locked ? Color.FromArgb(255, 145, 145, 145) : fill);
            drawingSession.DrawRectangle(inner, accent, innerWidth);
        }
    }

    private void DrawSelectionMarquee(CanvasDrawingSession drawingSession)
    {
        if (_activeInk.Count < 2) return;
        var accent = Color.FromArgb(230, 113, 167, 255);
        if (_gestureTool == EditorTool.BoxSelect)
        {
            var bounds = NormalizeRect(_activeInk[0].Position, _activeInk[^1].Position);
            drawingSession.DrawRectangle((float)bounds.X, (float)bounds.Y, (float)bounds.Width,
                (float)bounds.Height, accent, 1.5f);
            return;
        }
        for (var index = 1; index < _activeInk.Count; index++)
            drawingSession.DrawLine((float)_activeInk[index - 1].X, (float)_activeInk[index - 1].Y,
                (float)_activeInk[index].X, (float)_activeInk[index].Y, accent, 1.5f);
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_page is null) return;
        StopWheelZoomAnimation(resumeBackgroundWork: false);
        _liveInkRasterReleaseTimer.Stop();
        var point = e.GetCurrentPoint(DrawingSurface);
        if (point.PointerDeviceType == PointerDeviceType.Mouse &&
            MillisecondsSince(_lastNativeTouchTimestamp) < 500)
        {
            // WM_TOUCH already owns this contact; swallow the Wacom cursor/click emulation.
            e.Handled = true;
            return;
        }
        if (IsTouchNavigationPointer(e, point))
        {
            // If a driver reports both the raw Touch pointer and its generated Mouse promotion,
            // consume the duplicate rather than interpreting it as a second finger.
            if (point.PointerDeviceType == PointerDeviceType.Mouse && e.IsGenerated &&
                _touchPoints.Count > 0 && !_touchPoints.ContainsKey(point.PointerId))
            {
                e.Handled = true;
                return;
            }
            OnTouchPointerPressed(e, point);
            return;
        }
        StopTouchInertia(resumeBackgroundWork: false);
        if (point.PointerDeviceType == PointerDeviceType.Pen)
        {
            _lastPenInteractionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            CancelTouchGestureForPen();
        }
        else if (_touchPoints.Count > 0)
        {
            // Ignore mouse promotion/synthetic input while Windows is already reporting touch.
            e.Handled = true;
            return;
        }
        if (_isPointerDown) return;
        if (point.PointerDeviceType == PointerDeviceType.Mouse &&
            !point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed &&
            !point.Properties.IsMiddleButtonPressed) return;
        // CanvasControl can leave keyboard focus in search or a native text editor. Explicitly
        // reclaiming it makes routed Ctrl+Z/Ctrl+Y reliable after any canvas interaction.
        DrawingSurface.Focus(FocusState.Pointer);
        PauseBackgroundRecognition();
        PauseThumbnailRefresh();
        _isPointerDown = true;
        _penActive = point.PointerDeviceType == PointerDeviceType.Pen;
        var rightMousePan = point.PointerDeviceType == PointerDeviceType.Mouse && point.Properties.IsRightButtonPressed;
        var middleMousePan = point.PointerDeviceType == PointerDeviceType.Mouse && point.Properties.IsMiddleButtonPressed;
        _gestureTool = _readMode || rightMousePan || middleMousePan ? EditorTool.Pan :
            point.Properties.IsEraser ? EditorTool.StrokeEraser : _activeTool;
        _screenStart = point.Position;
        _panStart = _pan;
        _gestureScreenToPageValid = Matrix3x2.Invert(PageTransform(), out _gestureScreenToPage);
        if (_gestureScreenToPageValid)
        {
            var transformedStart = Vector2.Transform(
                new Vector2((float)point.Position.X, (float)point.Position.Y), _gestureScreenToPage);
            _gestureStart = new PointD(transformedStart.X, transformedStart.Y);
        }
        else _gestureStart = default;
        _gestureInkStyle = _gestureTool is EditorTool.Pen or EditorTool.Highlighter
            ? CurrentInkStyle()
            : null;
        if (_gestureTool is EditorTool.Lasso or EditorTool.BoxSelect && SelectionContainsInteraction(_gestureStart))
            _gestureTool = EditorTool.Select;
        _activeInk.Clear();
        _lastInkMovementTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        ClearLiveInkGeometryCache();
        ResetLiveInkRaster();
        _eraserPath.Clear();
        _eraseDirtyRegions.Clear();
        _eraseSnapshot = _gestureTool is EditorTool.SegmentEraser or EditorTool.StrokeEraser
            ? [.. _page.Objects]
            : null;
        DrawingSurface.CapturePointer(e.Pointer);
        PointerStatus.Text = point.PointerDeviceType switch
        {
            PointerDeviceType.Pen => "Pen",
            _ => rightMousePan || middleMousePan ? "Mouse • drag pan" : "Mouse"
        };

        switch (_gestureTool)
        {
            case EditorTool.Pen:
            case EditorTool.Highlighter:
            case EditorTool.Shape:
            case EditorTool.Lasso:
            case EditorTool.BoxSelect:
                AddPointerSample(point);
                break;
            case EditorTool.SegmentEraser:
            case EditorTool.StrokeEraser:
                _eraserPath.Add(_gestureStart);
                ApplyRealtimeErase();
                break;
            case EditorTool.Text:
                if (TextEditorOverlay.Visibility == Visibility.Visible)
                {
                    CommitOrDiscardTextEditor();
                }
                else if (MillisecondsSince(_lastTextEditorCloseTimestamp) < 250)
                {
                    // The click that moved focus away from the native editor is only a commit.
                }
                else if (FindTextAt(_gestureStart) is { } existingText)
                {
                    SelectSingleObject(existingText);
                    ShowTextEditor(existingText);
                }
                else if (_selectedObject is RichTextObject)
                {
                    _selectedObject = null;
                    _selectedObjects.Clear();
                    UpdateSelectionUi();
                }
                else
                {
                    AddTextAt(_gestureStart);
                }
                EndPointer(e);
                return;
            case EditorTool.Select:
                BeginSelectionGesture(_gestureStart);
                break;
            case EditorTool.Style:
                _styleBrushOriginals.Clear();
                _multiTransformPreviews.Clear();
                if (_styleToolPickMode)
                {
                    PickStyleAtPoint(_gestureStart);
                    EndPointer(e);
                    return;
                }
                ApplyStyleBrushAtPoint(_gestureStart);
                break;
        }

        e.Handled = true;
        DrawingSurface.Invalidate();
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(DrawingSurface);
        if (IsTouchNavigationPointer(e, current))
        {
            OnTouchPointerMoved(e, current);
            return;
        }
        if (current.PointerDeviceType == PointerDeviceType.Pen)
            _lastPenInteractionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_isPointerDown || _page is null) return;
        var redraw = false;
        switch (_gestureTool)
        {
            case EditorTool.Pen:
            case EditorTool.Highlighter:
            case EditorTool.Shape:
            case EditorTool.Lasso:
            case EditorTool.BoxSelect:
            {
                var screenToPage = _gestureScreenToPage;
                if (!_gestureScreenToPageValid && !Matrix3x2.Invert(PageTransform(), out screenToPage)) break;
                var points = e.GetIntermediatePoints(DrawingSurface);
                for (var index = points.Count - 1; index >= 0; index--)
                    redraw |= AddPointerSample(points[index], screenToPage);
                break;
            }
            case EditorTool.SegmentEraser:
            case EditorTool.StrokeEraser:
                _eraserPath.Add(ScreenToPage(current.Position));
                ApplyRealtimeErase();
                redraw = true;
                break;
            case EditorTool.Pan:
                _pan = _panStart + new Vector2((float)(current.Position.X - _screenStart.X),
                    (float)(current.Position.Y - _screenStart.Y));
                redraw = true;
                break;
            case EditorTool.Style:
                if (!Matrix3x2.Invert(PageTransform(), out var styleScreenToPage)) break;
                var stylePoints = e.GetIntermediatePoints(DrawingSurface);
                for (var index = stylePoints.Count - 1; index >= 0; index--)
                {
                    var transformed = Vector2.Transform(new Vector2((float)stylePoints[index].Position.X,
                        (float)stylePoints[index].Position.Y), styleScreenToPage);
                    ApplyStyleBrushAtPoint(new PointD(transformed.X, transformed.Y));
                }
                redraw = true;
                break;
            case EditorTool.Select when _multiTransformOriginals is { Count: > 1 } && _transformHandle != TransformHandle.None:
                var multiCurrent = ScreenToPage(current.Position);
                var multiPreserveAspect = IsCornerHandle(_transformHandle) && !IsShiftDown();
                var multiDelta = SelectionTransformer.CreateTransform(_transformHandle,
                    CombinedBounds(_multiTransformOriginals), _gestureStart, multiCurrent, multiPreserveAspect);
                _multiTransformPreviews.Clear();
                foreach (var original in _multiTransformOriginals)
                    _multiTransformPreviews[original.Id] = original with
                    {
                        Transform = original.Transform.Then(multiDelta)
                    };
                redraw = true;
                break;
            case EditorTool.Select when _transformOriginal is not null && _transformHandle != TransformHandle.None:
                var currentPage = ScreenToPage(current.Position);
                var singlePreserveAspect = IsCornerHandle(_transformHandle) && !IsShiftDown();
                var delta = SelectionTransformer.CreateTransform(_transformHandle,
                    StrokeGeometry.GetWorldBounds(_transformOriginal), _gestureStart, currentPage, singlePreserveAspect);
                _transformPreview = _transformOriginal with { Transform = _transformOriginal.Transform.Then(delta) };
                redraw = true;
                break;
        }

        e.Handled = true;
        if (redraw) DrawingSurface.Invalidate();
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(DrawingSurface);
        if (IsTouchNavigationPointer(e, current))
        {
            EndTouchPointer(e, releaseCapture: true);
            return;
        }
        if (!_isPointerDown || _page is null || _document is null) return;
        if (current.PointerDeviceType == PointerDeviceType.Pen)
            _lastPenInteractionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var deliberateShapeGesture = _gestureTool == EditorTool.Pen &&
                                     MillisecondsSince(_lastInkMovementTimestamp) >=
                                     ShapeSnapTerminalHoldMs;
        if (_gestureTool is EditorTool.Pen or EditorTool.Highlighter or EditorTool.Shape or EditorTool.Lasso or EditorTool.BoxSelect &&
            _gestureScreenToPageValid)
        {
            AddPointerSample(current, _gestureScreenToPage, force: true);
        }
        switch (_gestureTool)
        {
            case EditorTool.Pen:
            case EditorTool.Highlighter:
                CommitInk(deliberateShapeGesture);
                break;
            case EditorTool.Shape:
                CommitShape();
                break;
            case EditorTool.SegmentEraser:
            case EditorTool.StrokeEraser:
                CommitRealtimeErase();
                break;
            case EditorTool.Lasso:
                CommitAreaSelection(lasso: true);
                break;
            case EditorTool.BoxSelect:
                CommitAreaSelection(lasso: false);
                break;
            case EditorTool.Style:
                CommitStyleBrush();
                break;
            case EditorTool.Select when _multiTransformOriginals is { Count: > 1 } && _multiTransformPreviews.Count > 0:
                var after = _multiTransformOriginals.Select(item => _multiTransformPreviews[item.Id]).ToArray();
                _history.Execute(new ReplaceObjectsCommand(_page.Id, _multiTransformOriginals, after,
                    "Transform selection"), _document);
                _selectedObjects.Clear();
                _selectedObjects.AddRange(after);
                _multiTransformPreviews.Clear();
                OnDocumentChanged(recognizeInk: after.Any(item => item is InkStrokeObject));
                break;
            case EditorTool.Select when _transformOriginal is not null && _transformPreview is not null:
                _history.Execute(new ReplaceObjectsCommand(_page.Id, [_transformOriginal], [_transformPreview], "Transform object"), _document);
                _selectedObject = _transformPreview;
                _selectedObjects.Clear();
                _selectedObjects.Add(_transformPreview);
                OnDocumentChanged(recognizeInk: false);
                break;
        }

        EndPointer(e);
    }

    private void OnCanvasPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(DrawingSurface);
        if (IsTouchNavigationPointer(e, current))
        {
            EndTouchPointer(e, releaseCapture: true);
            return;
        }
        if (current.PointerDeviceType == PointerDeviceType.Pen)
            _lastPenInteractionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        RestoreEraseSnapshot();
        EndPointer(e);
    }

    private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        var current = e.GetCurrentPoint(DrawingSurface);
        if (IsTouchNavigationPointer(e, current))
        {
            EndTouchPointer(e, releaseCapture: false);
            return;
        }
        if (!_isPointerDown) return;
        if (current.PointerDeviceType == PointerDeviceType.Pen)
            _lastPenInteractionTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        RestoreEraseSnapshot();
        EndPointer(e, releaseCapture: false);
    }

    private void EndPointer(PointerRoutedEventArgs e, bool releaseCapture = true)
    {
        _isPointerDown = false;
        _penActive = false;
        if (releaseCapture) DrawingSurface.ReleasePointerCapture(e.Pointer);
        _activeInk.Clear();
        ClearLiveInkGeometryCache();
        _liveInkRasterPointIndex = 0;
        if (_liveInkRaster is not null)
        {
            _liveInkRasterReleaseTimer.Stop();
            _liveInkRasterReleaseTimer.Start();
        }
        _eraserPath.Clear();
        _eraseDirtyRegions.Clear();
        _transformOriginal = null;
        _transformPreview = null;
        _multiTransformOriginals = null;
        _multiTransformPreviews.Clear();
        _styleBrushOriginals.Clear();
        _eraseSnapshot = null;
        _transformHandle = TransformHandle.None;
        _gestureInkStyle = null;
        _gestureScreenToPageValid = false;
        e.Handled = true;
        DrawingSurface.Invalidate();
        ResumeBackgroundRecognition();
        ResumeThumbnailRefresh();
    }

    private void OnTouchPointerPressed(PointerRoutedEventArgs e, PointerPoint point)
    {
        // Pen and touch are independent input channels. An active pen gesture always wins, while
        // an active pen gesture rejects palm contacts. Do not gate a genuine finger on
        // TouchConfidence: several tablet drivers report that HID bit inconsistently and then
        // promote the same contact to a generated mouse pointer.
        if (_penActive || _isPointerDown)
        {
            e.Handled = true;
            return;
        }

        StopTouchInertia(resumeBackgroundWork: true);

        var firstTouch = _touchPoints.Count == 0;
        _touchPoints[point.PointerId] = point.Position;
        // Pointer capture improves off-canvas continuation but is not required to recognize a
        // finger. Some touch drivers return false here even though move/release events continue.
        _ = DrawingSurface.CapturePointer(e.Pointer);

        if (firstTouch)
        {
            DrawingSurface.Focus(FocusState.Pointer);
            PauseBackgroundRecognition();
            PauseThumbnailRefresh();
        }
        RebaseTouchGesture();
        PointerStatus.Text = _touchPoints.Count > 1 ? "Touch • pinch to zoom • drag to pan" : "Touch • drag to pan";
        e.Handled = true;
    }

    private bool IsTouchNavigationPointer(PointerRoutedEventArgs e, PointerPoint point)
    {
        if (_touchPoints.ContainsKey(point.PointerId)) return true;
        var contact = point.Properties.ContactRect;
        var nativeTouch = NativePointerClassifier.IsTouch(point.PointerId);
        var isNavigation = TouchInputPolicy.IsNavigationContact(
            point.PointerDeviceType == PointerDeviceType.Touch,
            point.PointerDeviceType == PointerDeviceType.Mouse,
            e.IsGenerated,
            nativeTouch,
            contact.Width > 0.5 && contact.Height > 0.5);
        if (_pointerClassificationLogCount++ < 8)
        {
            var reportedType = point.PointerDeviceType.ToString();
            var generated = e.IsGenerated;
            var contactWidth = Math.Round(contact.Width, 1);
            var contactHeight = Math.Round(contact.Height, 1);
            _ = Task.Run(() => DiagnosticsLog.Info("input.pointer_classified",
                ("reported_type", reportedType),
                ("generated", generated),
                ("native_touch", nativeTouch),
                ("contact_width", contactWidth),
                ("contact_height", contactHeight),
                ("touch_navigation", isNavigation)));
        }
        return isNavigation;
    }

    private void OnNativeTouchFrame(object? sender, NativeTouchFrameEventArgs e)
    {
        _ = sender;
        _lastNativeTouchTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_page is null || _penActive || _isPointerDown || e.Contacts.Count == 0) return;

        var canvasOrigin = DrawingSurface.TransformToVisual(null)
            .TransformPoint(new Point(0, 0));
        var hadTouches = _touchPoints.Count > 0;
        var topologyChanged = false;
        var moved = false;
        var ended = false;

        foreach (var contact in e.Contacts)
        {
            // Keep native WM_TOUCH identifiers disjoint from WinUI Pointer identifiers.
            var pointerId = contact.Id | 0x80000000u;
            var position = new Point(
                contact.ClientX - canvasOrigin.X,
                contact.ClientY - canvasOrigin.Y);
            switch (contact.Action)
            {
                case NativeTouchAction.Down:
                    if (position.X < 0 || position.Y < 0 ||
                        position.X > DrawingSurface.ActualWidth ||
                        position.Y > DrawingSurface.ActualHeight)
                        continue;
                    StopTouchInertia(resumeBackgroundWork: false);
                    _touchPoints[pointerId] = position;
                    topologyChanged = true;
                    break;
                case NativeTouchAction.Move:
                    if (!_touchPoints.ContainsKey(pointerId)) continue;
                    _touchPoints[pointerId] = position;
                    moved = true;
                    break;
                case NativeTouchAction.Up:
                    topologyChanged |= _touchPoints.Remove(pointerId);
                    ended = true;
                    break;
            }
        }

        if (!hadTouches && _touchPoints.Count > 0)
        {
            DrawingSurface.Focus(FocusState.Pointer);
            PauseBackgroundRecognition();
            PauseThumbnailRefresh();
        }

        if (_touchPoints.Count > 0)
        {
            if (topologyChanged)
                RebaseTouchGesture(resetVelocity: true);
            else if (moved)
                ApplyTouchGesture();
            PointerStatus.Text = _touchPoints.Count > 1
                ? "Touch • pinch to zoom"
                : "Touch • drag to pan";
            return;
        }

        if (!ended) return;
        PointerStatus.Text = "Windows Ink";
        DrawingSurface.Invalidate();
        if (!TryStartTouchInertia())
        {
            ResumeBackgroundRecognition();
            ResumeThumbnailRefresh();
        }
    }

    private void OnTouchPointerMoved(PointerRoutedEventArgs e, PointerPoint point)
    {
        if (!_touchPoints.ContainsKey(point.PointerId) || _penActive)
        {
            e.Handled = true;
            return;
        }
        _touchPoints[point.PointerId] = point.Position;
        ApplyTouchGesture();
        e.Handled = true;
    }

    private void EndTouchPointer(PointerRoutedEventArgs e, bool releaseCapture)
    {
        var removed = _touchPoints.Remove(e.Pointer.PointerId);
        if (releaseCapture) DrawingSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (!removed) return;

        if (_touchPoints.Count > 0)
        {
            // Preserve the last centroid velocity while fingers lift one at a time so a
            // two-finger pan still transitions naturally into inertia.
            RebaseTouchGesture(resetVelocity: false);
            PointerStatus.Text = _touchPoints.Count > 1 ? "Touch • pinch to zoom • drag to pan" : "Touch • drag to pan";
            return;
        }

        PointerStatus.Text = "Windows Ink";
        DrawingSurface.Invalidate();
        if (!TryStartTouchInertia())
        {
            ResumeBackgroundRecognition();
            ResumeThumbnailRefresh();
        }
    }

    private void CancelTouchGestureForPen()
    {
        if (_touchPoints.Count == 0) return;
        _touchPoints.Clear();
        DrawingSurface.ReleasePointerCaptures();
        PointerStatus.Text = "Pen";
    }

    private void RebaseTouchGesture(bool resetVelocity = true)
    {
        if (_touchPoints.Count == 0) return;
        _touchStartCentroid = TouchCentroid();
        _touchStartSpread = Math.Max(1, TouchSpread(_touchStartCentroid));
        _touchStartZoom = _zoom;
        _touchStartPan = _pan;
        _touchPageAnchor = ScreenToPage(_touchStartCentroid);
        _touchLastCentroid = _touchStartCentroid;
        _touchLastMoveTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (resetVelocity) _touchVelocity = Vector2.Zero;
    }

    private void ApplyTouchGesture()
    {
        if (_page is null || _touchPoints.Count == 0) return;
        var centroid = TouchCentroid();
        UpdateTouchVelocity(centroid);
        if (_touchPoints.Count == 1)
        {
            var viewport = TouchViewportMath.Pan(
                _zoom,
                _touchStartPan,
                new PointD(_touchStartCentroid.X, _touchStartCentroid.Y),
                new PointD(centroid.X, centroid.Y));
            _pan = viewport.Pan;
        }
        else
        {
            var scale = TouchSpread(centroid) / _touchStartSpread;
            var viewport = TouchViewportMath.PinchOnly(
                _touchStartZoom,
                scale,
                _touchPageAnchor,
                new PointD(_touchStartCentroid.X, _touchStartCentroid.Y),
                new PointD(centroid.X, centroid.Y),
                _page.Size,
                new SizeD(DrawingSurface.ActualWidth, DrawingSurface.ActualHeight));
            _zoom = viewport.Zoom;
            _pan = viewport.Pan;
            UpdateZoomText();
        }
        _fitPending = false;
        DrawingSurface.Invalidate();
    }

    private void UpdateTouchVelocity(Point centroid)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _touchLastMoveTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsedSeconds is > 0.001 and < 0.12)
        {
            var delta = new Vector2((float)(centroid.X - _touchLastCentroid.X),
                (float)(centroid.Y - _touchLastCentroid.Y));
            if (delta.LengthSquared() > 0.09f) _touchGestureMoved = true;
            var instantaneous = delta / (float)elapsedSeconds;
            _touchVelocity = _touchVelocity * 0.62f + instantaneous * 0.38f;
        }
        _touchLastCentroid = centroid;
        _touchLastMoveTimestamp = now;
    }

    private bool TryStartTouchInertia()
    {
        var speed = _touchVelocity.Length();
        if (!_touchGestureMoved || !float.IsFinite(speed) || speed < 110 ||
            MillisecondsSince(_touchLastMoveTimestamp) > 90)
        {
            _touchVelocity = Vector2.Zero;
            _touchGestureMoved = false;
            return false;
        }
        _touchInertiaActive = true;
        _touchInertiaTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        EnsureViewportFramePump();
        return true;
    }

    private bool AdvanceTouchInertia()
    {
        if (!_touchInertiaActive || _page is null || _touchPoints.Count > 0 || _isPointerDown)
        {
            StopTouchInertia(resumeBackgroundWork: true);
            return false;
        }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedSeconds = Math.Clamp(
            (now - _touchInertiaTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency,
            0.001,
            0.05);
        _touchInertiaTimestamp = now;
        _pan += _touchVelocity * (float)elapsedSeconds;
        _touchVelocity *= (float)Math.Exp(-4.6 * elapsedSeconds);
        if (_touchVelocity.LengthSquared() < 24 * 24)
            StopTouchInertia(resumeBackgroundWork: true);
        return true;
    }

    private void StopTouchInertia(bool resumeBackgroundWork)
    {
        if (!_touchInertiaActive) return;
        _touchInertiaActive = false;
        _touchVelocity = Vector2.Zero;
        _touchGestureMoved = false;
        StopViewportFramePumpIfIdle();
        if (!resumeBackgroundWork) return;
        ResumeBackgroundRecognition();
        ResumeThumbnailRefresh();
    }

    private void BeginNavigationSettle()
    {
        _zoomNavigationActive = true;
        _navigationSettleTimer.Stop();
        _navigationSettleTimer.Start();
    }

    private void OnNavigationSettleTick(DispatcherQueueTimer sender, object args)
    {
        if (_isPointerDown || _touchPoints.Count > 0 || _touchInertiaActive || _wheelZoomAnimating)
        {
            sender.Start();
            return;
        }
        if (!_zoomNavigationActive) return;
        _zoomNavigationActive = false;
        DrawingSurface.Invalidate();
    }

    private bool AdvanceWheelZoom()
    {
        if (!_wheelZoomAnimating || _page is null || _isPointerDown || _touchPoints.Count > 0)
        {
            StopWheelZoomAnimation(resumeBackgroundWork: true);
            return false;
        }

        const double durationMs = 80;
        var elapsedMs = MillisecondsSince(_wheelZoomAnimationStarted);
        var progress = Math.Clamp(elapsedMs / durationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        var nextZoom = _wheelZoomStart * Math.Exp(
            Math.Log(_wheelZoomTarget / Math.Max(_wheelZoomStart, 0.0001)) * eased);
        if (progress >= 1) nextZoom = _wheelZoomTarget;
        ApplyZoomAtAnchor(nextZoom, _wheelZoomAnchorPage, _wheelZoomAnchorScreen);
        UpdateZoomText();

        if (nextZoom == _wheelZoomTarget)
            StopWheelZoomAnimation(resumeBackgroundWork: true);
        return true;
    }

    private void ApplyZoomAtAnchor(double zoom, PointD pageAnchor, Point screenAnchor)
    {
        _zoom = Math.Clamp(zoom, 0.08, 8);
        var afterScreen = PageToScreen(pageAnchor);
        _pan += new Vector2(
            (float)(screenAnchor.X - afterScreen.X),
            (float)(screenAnchor.Y - afterScreen.Y));
        _fitPending = false;
    }

    private void StopWheelZoomAnimation(bool resumeBackgroundWork)
    {
        if (!_wheelZoomAnimating)
        {
            _wheelZoomTarget = _zoom;
            _wheelZoomStart = _zoom;
            return;
        }
        _wheelZoomAnimating = false;
        _wheelZoomTarget = _zoom;
        _wheelZoomStart = _zoom;
        StopViewportFramePumpIfIdle();
        if (!resumeBackgroundWork) return;
        ResumeBackgroundRecognition();
        ResumeThumbnailRefresh();
    }

    private Point TouchCentroid()
    {
        double x = 0;
        double y = 0;
        foreach (var point in _touchPoints.Values)
        {
            x += point.X;
            y += point.Y;
        }
        return new Point(x / _touchPoints.Count, y / _touchPoints.Count);
    }

    private double TouchSpread(Point centroid)
    {
        if (_touchPoints.Count < 2) return 1;
        double distance = 0;
        foreach (var point in _touchPoints.Values)
        {
            var deltaX = point.X - centroid.X;
            var deltaY = point.Y - centroid.Y;
            distance += Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }
        return distance / _touchPoints.Count;
    }

    private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_page is null) return;
        StopTouchInertia(resumeBackgroundWork: false);
        var pointer = e.GetCurrentPoint(DrawingSurface);
        if (_readMode)
        {
            StopWheelZoomAnimation(resumeBackgroundWork: false);
            _pan.Y += pointer.Properties.MouseWheelDelta * 0.65f;
            e.Handled = true;
            DrawingSurface.Invalidate();
            ResumeBackgroundRecognition();
            ResumeThumbnailRefresh();
            return;
        }

        var delta = pointer.Properties.MouseWheelDelta;
        if (delta == 0) return;
        BeginNavigationSettle();
        PauseBackgroundRecognition();
        PauseThumbnailRefresh();
        if (!_wheelZoomAnimating)
        {
            _wheelZoomTarget = _zoom;
            _wheelZoomAnimating = true;
            EnsureViewportFramePump();
        }
        _wheelZoomAnchorScreen = pointer.Position;
        _wheelZoomAnchorPage = ScreenToPage(pointer.Position);
        // Preserve the familiar 12% change for a standard 120-unit mouse notch while allowing
        // precision wheels and touchpads to accumulate fractional targets. A short logarithmic
        // animation turns discrete 120-unit wheel notches into continuous, cursor-anchored zoom.
        var multiplier = Math.Pow(1.12, delta / 120d);
        _wheelZoomTarget = Math.Clamp(_wheelZoomTarget * multiplier, 0.08, 8);
        // Apply part of the delta synchronously so the canvas never trails a physical wheel
        // notch, then animate only the small remainder over a fixed, non-queueing interval.
        var immediateZoom = _zoom * Math.Exp(
            Math.Log(_wheelZoomTarget / Math.Max(_zoom, 0.0001)) * 0.42);
        ApplyZoomAtAnchor(immediateZoom, _wheelZoomAnchorPage, _wheelZoomAnchorScreen);
        _wheelZoomStart = _zoom;
        _wheelZoomAnimationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateZoomText();
        DrawingSurface.Invalidate();
        e.Handled = true;
    }

    private void BeginSelectionGesture(PointD point)
    {
        if (_selectedObjects.Count > 1)
        {
            var bounds = CombinedSelectionBounds();
            _transformHandle = SelectionTransformer.HitHandle(bounds, point, 12 / _zoom);
            if (_transformHandle == TransformHandle.None && bounds.Contains(point))
                _transformHandle = TransformHandle.Move;
            if (_transformHandle != TransformHandle.None)
            {
                _multiTransformOriginals = [.. _selectedObjects];
                return;
            }
        }
        if (_selectedObject is { IsLocked: false })
        {
            var bounds = StrokeGeometry.GetWorldBounds(_selectedObject);
            _transformHandle = SelectionTransformer.HitHandle(bounds, point, 12 / _zoom);
            if (_transformHandle == TransformHandle.None && bounds.Contains(point))
                _transformHandle = TransformHandle.Move;
            if (_transformHandle != TransformHandle.None)
            {
                _transformOriginal = _selectedObject;
                return;
            }
        }

        var tolerance = 10 / _zoom;
        _selectedObject = _spatialIndex.Query(new RectD(point.X - tolerance, point.Y - tolerance, tolerance * 2, tolerance * 2))
            .Where(item => (!item.IsLocked || item is ImageObject) && StrokeGeometry.HitTest(item, point, tolerance))
            .OrderByDescending(item => item.ZIndex)
            .FirstOrDefault();
        _selectedObjects.Clear();
        if (_selectedObject is not null) _selectedObjects.Add(_selectedObject);
        _transformHandle = _selectedObject is null or { IsLocked: true } ? TransformHandle.None : TransformHandle.Move;
        _transformOriginal = _selectedObject is { IsLocked: false } ? _selectedObject : null;
        UpdateSelectionUi();
    }

    private bool SelectionContainsInteraction(PointD point)
    {
        var tolerance = 12 / _zoom;
        if (_selectedObjects.Count > 1)
        {
            var bounds = CombinedSelectionBounds();
            return SelectionTransformer.HitHandle(bounds, point, tolerance) != TransformHandle.None ||
                   bounds.Contains(point);
        }
        if (_selectedObject is not { IsLocked: false } selected) return false;
        var selectedBounds = StrokeGeometry.GetWorldBounds(selected);
        return SelectionTransformer.HitHandle(selectedBounds, point, tolerance) != TransformHandle.None ||
               selectedBounds.Contains(point);
    }

    private bool AddPointerSample(PointerPoint pointer)
    {
        if (!Matrix3x2.Invert(PageTransform(), out var screenToPage)) return false;
        return AddPointerSample(pointer, screenToPage);
    }

    private bool AddPointerSample(PointerPoint pointer, Matrix3x2 screenToPage, bool force = false)
    {
        var transformed = Vector2.Transform(
            new Vector2((float)pointer.Position.X, (float)pointer.Position.Y), screenToPage);
        var pagePoint = new PointD(transformed.X, transformed.Y);
        if (_page is null || pagePoint.X < -100 || pagePoint.Y < -100 ||
            pagePoint.X > _page.Size.Width + 100 || pagePoint.Y > _page.Size.Height + 100) return false;
        if (_activeInk.Count > 0)
        {
            var last = _activeInk[^1];
            // Sampling substantially below one screen pixel only increases path construction
            // and persistence work; it cannot add visible detail. This still preserves sharp
            // corners because a fast direction change travels well beyond this threshold.
            var minimumDistance = LiveInkMinimumScreenDistance / Math.Max(_zoom, 0.08);
            var deltaX = last.X - pagePoint.X;
            var deltaY = last.Y - pagePoint.Y;
            if (!force && deltaX * deltaX + deltaY * deltaY < minimumDistance * minimumDistance) return false;
        }
        var sample = new InkPoint(pagePoint.X, pagePoint.Y, 0.65f, 0, 0, (long)pointer.Timestamp);
        var endpointOnly = _gestureTool is EditorTool.Shape or EditorTool.BoxSelect ||
                           (_gestureTool == EditorTool.Highlighter && HighlighterStraightToggle.IsOn);
        if (endpointOnly && _activeInk.Count > 1) _activeInk[^1] = sample;
        else _activeInk.Add(sample);
        if (!force) _lastInkMovementTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        return true;
    }

    private void CommitInk(bool deliberateShapeGesture)
    {
        if (_document is null || _page is null || _activeInk.Count == 0) return;
        if (_gestureTool == EditorTool.Pen && SmartShapesToggle.IsOn &&
            ShapeRecognizer.RecognizeDetailed(_activeInk, deliberateShapeGesture) is { } recognition)
        {
            var shape = new ShapeObject
            {
                Shape = recognition.Kind,
                Bounds = NormalizeRect(
                    new PointD(_activeInk.Min(point => point.X), _activeInk.Min(point => point.Y)),
                    new PointD(_activeInk.Max(point => point.X), _activeInk.Max(point => point.Y))),
                StrokeColor = _inkColor,
                StrokeWidth = (float)StrokeWidthSlider.Value,
                ZIndex = NextZIndex()
            };
            _history.Execute(new AddObjectCommand(_page.Id, shape), _document);
            OnDocumentChanged(recognizeInk: false, appendedObject: shape);
            return;
        }
        var style = _gestureInkStyle ?? CurrentInkStyle();
        var points = _gestureTool == EditorTool.Highlighter && HighlighterStraightToggle.IsOn && _activeInk.Count > 1
            ? new List<InkPoint> { _activeInk[0], SnapHighlighterEnd(_activeInk[0], _activeInk[^1]) }
            : StrokeGeometry.StabilizeForViewport(_activeInk, _zoom, style.Smoothing).ToList();
        var stroke = new InkStrokeObject
        {
            Points = points,
            Style = style,
            ZIndex = NextZIndex()
        };
        _history.Execute(new AddObjectCommand(_page.Id, stroke), _document);
        OnDocumentChanged(recognizeInk: true, appendedObject: stroke);
    }

    private static InkPoint SnapHighlighterEnd(InkPoint start, InkPoint end)
    {
        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        var length = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (length < 0.001) return end;
        const double increment = Math.PI / 4d;
        var angle = Math.Atan2(deltaY, deltaX);
        var snappedAngle = Math.Round(angle / increment) * increment;
        return end with
        {
            X = start.X + Math.Cos(snappedAngle) * length,
            Y = start.Y + Math.Sin(snappedAngle) * length
        };
    }

    private void CommitShape()
    {
        if (_document is null || _page is null || _activeInk.Count < 2) return;
        var shape = new ShapeObject
        {
            Shape = SelectedShapeKind(),
            Bounds = NormalizeRect(_activeInk[0].Position, _activeInk[^1].Position),
            StartPoint = _activeInk[0].Position,
            EndPoint = _activeInk[^1].Position,
            StrokeColor = _inkColor,
            StrokeWidth = (float)StrokeWidthSlider.Value,
            ZIndex = NextZIndex()
        };
        _history.Execute(new AddObjectCommand(_page.Id, shape), _document);
        OnDocumentChanged(recognizeInk: false, appendedObject: shape);
    }

    private void ApplyRealtimeErase()
    {
        if (_page is null || _eraserPath.Count == 0) return;
        var last = _eraserPath[^1];
        IReadOnlyList<PointD> recentPath = _eraserPath.Count > 1 ? [_eraserPath[^2], last] : [last];
        var radius = Math.Max(5, StrokeWidthSlider.Value);
        var queryArea = RectD.FromPoints(recentPath).Inflate(radius + 3);
        var changed = false;
        if (_gestureTool == EditorTool.SegmentEraser)
        {
            var candidates = _spatialIndex.Query(queryArea).OfType<InkStrokeObject>()
                .Where(stroke => !stroke.IsLocked).ToArray();
            foreach (var stroke in candidates)
            {
                var fragments = SegmentEraser.Erase(stroke, recentPath, radius);
                if (fragments.Count == 1 && fragments[0].Id == stroke.Id) continue;
                var objectIndex = _page.Objects.FindIndex(item => item.Id == stroke.Id);
                if (objectIndex < 0) continue;
                _page.Objects.RemoveAt(objectIndex);
                _spatialIndex.Remove(stroke.Id);
                _page.Objects.InsertRange(objectIndex, fragments);
                foreach (var fragment in fragments) _spatialIndex.Add(fragment);
                changed = true;
            }
            if (changed) AddEraseDirtyRegion(queryArea);
        }
        else
        {
            var removed = _spatialIndex.Query(queryArea)
                .Where(item => !item.IsLocked && StrokeGeometry.HitTest(item, last, radius))
                .ToArray();
            if (removed.Length > 0)
            {
                var ids = removed.Select(item => item.Id).ToHashSet();
                foreach (var item in removed)
                {
                    AddEraseDirtyRegion(StrokeGeometry.GetWorldBounds(item).Inflate(2));
                    _spatialIndex.Remove(item.Id);
                }
                _page.Objects.RemoveAll(item => ids.Contains(item.Id));
                changed = true;
            }
        }
        if (!changed) return;
        _selectedObject = null;
        _selectedObjects.Clear();
        DrawingSurface.Invalidate();
    }

    private void AddEraseDirtyRegion(RectD region)
    {
        for (var index = _eraseDirtyRegions.Count - 1; index >= 0; index--)
        {
            var existing = _eraseDirtyRegions[index];
            if (!existing.Inflate(3).Intersects(region)) continue;
            region = new RectD(
                Math.Min(existing.Left, region.Left),
                Math.Min(existing.Top, region.Top),
                Math.Max(existing.Right, region.Right) - Math.Min(existing.Left, region.Left),
                Math.Max(existing.Bottom, region.Bottom) - Math.Min(existing.Top, region.Top));
            _eraseDirtyRegions.RemoveAt(index);
        }
        _eraseDirtyRegions.Add(region);
    }

    private void DrawRealtimeErasePreview(CanvasDrawingSession drawingSession, NotePage page)
    {
        foreach (var region in _eraseDirtyRegions)
        {
            using var layer = drawingSession.CreateLayer(1f,
                new Rect(region.X, region.Y, region.Width, region.Height));
            DrawPageBackground(drawingSession, page);
            DrawImportedLayer(drawingSession, page);
            if (_temporaryGridVisible) DrawTemporaryGrid(drawingSession, page);
            foreach (var canvasObject in _spatialIndex.Query(region))
                if (!canvasObject.IsHidden) DrawObject(drawingSession, canvasObject);
        }
    }

    private void CommitRealtimeErase()
    {
        if (_document is null || _page is null || _eraseSnapshot is null) return;
        var after = _page.Objects.ToArray();
        if (_eraseSnapshot.Select(item => item.Id).SequenceEqual(after.Select(item => item.Id))) return;
        _page.Objects.Clear();
        _page.Objects.AddRange(_eraseSnapshot);
        _history.Execute(new ReplaceObjectsCommand(_page.Id, _eraseSnapshot, after,
            _gestureTool == EditorTool.SegmentEraser ? "Erase ink segments" : "Erase objects"), _document);
        OnDocumentChanged(recognizeInk: true);
    }

    private void RestoreEraseSnapshot()
    {
        if (_page is null || _eraseSnapshot is null) return;
        _page.Objects.Clear();
        _page.Objects.AddRange(_eraseSnapshot);
        _spatialIndex.Rebuild(_page.Objects);
        _eraseDirtyRegions.Clear();
        InvalidatePageRenderCache();
        DrawingSurface.Invalidate();
    }

    private void CommitAreaSelection(bool lasso)
    {
        if (_page is null || _activeInk.Count < 2) return;
        var points = _activeInk.Select(point => point.Position).ToArray();
        var area = RectD.FromPoints(points);
        var selected = _page.Objects.Where(item => !item.IsLocked && !item.IsHidden)
            .Where(item =>
            {
                var bounds = StrokeGeometry.GetWorldBounds(item);
                if (!bounds.Intersects(area)) return false;
                if (!lasso) return true;
                return PointInPolygon(bounds.Center, points) ||
                       PointInPolygon(new PointD(bounds.Left, bounds.Top), points) ||
                       PointInPolygon(new PointD(bounds.Right, bounds.Bottom), points);
            }).ToArray();
        _selectedObjects.Clear();
        _selectedObjects.AddRange(selected);
        _selectedObject = selected.Length == 1 ? selected[0] : null;
        UpdateSelectionUi();
    }

    private static bool PointInPolygon(PointD point, IReadOnlyList<PointD> polygon)
    {
        var inside = false;
        for (int left = 0, right = polygon.Count - 1; left < polygon.Count; right = left++)
        {
            var a = polygon[left];
            var b = polygon[right];
            if ((a.Y > point.Y) == (b.Y > point.Y)) continue;
            var crossing = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (point.X < crossing) inside = !inside;
        }
        return inside;
    }

    private void CommitSegmentErase()
    {
        if (_document is null || _page is null || _eraserPath.Count == 0) return;
        var before = new List<CanvasObject>();
        var after = new List<CanvasObject>();
        foreach (var stroke in _page.Objects.OfType<InkStrokeObject>().Where(stroke => !stroke.IsLocked))
        {
            var fragments = SegmentEraser.Erase(stroke, _eraserPath, Math.Max(4, StrokeWidthSlider.Value));
            if (fragments.Count == 1 && fragments[0].Id == stroke.Id) continue;
            before.Add(stroke);
            after.AddRange(fragments);
        }
        if (before.Count == 0) return;
        _history.Execute(new ReplaceObjectsCommand(_page.Id, before, after, "Erase ink segments"), _document);
        _selectedObject = null;
        OnDocumentChanged(recognizeInk: true);
    }

    private void CommitStrokeErase()
    {
        if (_document is null || _page is null || _eraserPath.Count == 0) return;
        var removed = _page.Objects.Where(item => !item.IsLocked &&
            _eraserPath.Any(point => StrokeGeometry.HitTest(item, point, Math.Max(5, StrokeWidthSlider.Value)))).ToArray();
        if (removed.Length == 0) return;
        _history.Execute(new ReplaceObjectsCommand(_page.Id, removed, [], "Erase strokes"), _document);
        _selectedObject = null;
        OnDocumentChanged(recognizeInk: true);
    }

    private void AddTextAt(PointD point)
    {
        if (_document is null || _page is null) return;
        var text = new RichTextObject
        {
            Bounds = new RectD(point.X, point.Y, 320, 150),
            Content = CreateTextDocument(string.Empty, DefaultTextColor()),
            ZIndex = NextZIndex()
        };
        _history.Execute(new AddObjectCommand(_page.Id, text), _document);
        SelectSingleObject(text);
        ShowTextEditor(text);
        OnDocumentChanged(recognizeInk: false);
    }

    private void ShowTextEditor(RichTextObject text)
    {
        _textOriginal = text;
        _textPreview = text;
        var topLeft = PageToScreen(new PointD(text.Bounds.Left, text.Bounds.Top));
        Canvas.SetLeft(TextEditorOverlay, topLeft.X);
        Canvas.SetTop(TextEditorOverlay, topLeft.Y);
        TextEditorOverlay.Width = Math.Max(120, text.Bounds.Width * _zoom);
        TextEditorOverlay.Height = Math.Max(64, text.Bounds.Height * _zoom);
        TextEditorOverlay.FontSize = text.Content.FontSize * _zoom;
        var runColor = text.Content.Paragraphs.FirstOrDefault()?.Runs.FirstOrDefault()?.Color ?? DefaultTextColor();
        TextEditorOverlay.Foreground = new SolidColorBrush(ParseColor(runColor));
        TextEditorOverlay.Background = new SolidColorBrush(ParseColor(_page?.Template.PaperColor ?? "#FFFDF8", 0.97f));
        _syncingTextEditor = true;
        TextEditorOverlay.Text = text.Content.PlainText;
        _syncingTextEditor = false;
        TextEditorOverlay.Visibility = Visibility.Visible;
        TextEditorOverlay.Focus(FocusState.Programmatic);
        TextEditorOverlay.SelectionStart = TextEditorOverlay.Text.Length;
    }

    private void OnTextEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingTextEditor || _textOriginal is null) return;
        _textPreview = _textOriginal with
        {
            Content = WithPlainText(_textPreview?.Content ?? _textOriginal.Content, TextEditorOverlay.Text)
        };
        DrawingSurface.Invalidate();
    }

    private void OnTextEditorLostFocus(object sender, RoutedEventArgs e)
    {
        CommitOrDiscardTextEditor();
    }

    private void CommitOrDiscardTextEditor()
    {
        if (_textOriginal is null)
        {
            TextEditorOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var original = _textOriginal;
        var preview = _textPreview ?? original;
        TextEditorOverlay.Visibility = Visibility.Collapsed;
        _textOriginal = null;
        _textPreview = null;
        _lastTextEditorCloseTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_document is not null && _page is not null)
        {
            if (string.IsNullOrWhiteSpace(preview.Content.PlainText))
            {
                _history.Execute(new ReplaceObjectsCommand(_page.Id, [original], [], "Remove empty text box"), _document);
                _selectedObject = null;
                _selectedObjects.Clear();
                OnDocumentChanged(recognizeInk: false);
            }
            else if (original.Content != preview.Content)
            {
                _history.Execute(new ReplaceObjectsCommand(_page.Id, [original], [preview], "Edit text"), _document);
                SelectSingleObject(preview);
                OnDocumentChanged(recognizeInk: false);
            }
        }
        UpdateSelectionUi();
        DrawingSurface.Invalidate();
    }

    private RichTextObject? FindTextAt(PointD point)
    {
        var tolerance = 8 / _zoom;
        var candidates = _spatialIndex.Query(new RectD(point.X - tolerance, point.Y - tolerance, tolerance * 2, tolerance * 2));
        if (candidates.Count == 0 && _page is not null) candidates = _page.Objects;
        return candidates
            .OfType<RichTextObject>()
            .Where(text => !text.IsLocked && StrokeGeometry.GetWorldBounds(text).Contains(point))
            .OrderByDescending(text => text.ZIndex)
            .FirstOrDefault();
    }

    private void SelectSingleObject(CanvasObject canvasObject)
    {
        _selectedObject = canvasObject;
        _selectedObjects.Clear();
        _selectedObjects.Add(canvasObject);
        UpdateSelectionUi();
    }

    private static RichTextDocument WithPlainText(RichTextDocument source, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var fallbackParagraph = source.Paragraphs.FirstOrDefault() ?? new RichParagraph();
        var fallbackRun = source.Paragraphs.SelectMany(item => item.Runs).FirstOrDefault() ?? new TextRun();
        return source with
        {
            Paragraphs = lines.Select((line, index) =>
            {
                var paragraph = source.Paragraphs.ElementAtOrDefault(index) ?? fallbackParagraph;
                var run = paragraph.Runs.FirstOrDefault() ?? fallbackRun;
                return paragraph with { Runs = [run with { Text = line }] };
            }).ToList()
        };
    }

    private static RichTextDocument CreateTextDocument(string text, string color)
    {
        var document = RichTextDocument.FromPlainText(text);
        return document with
        {
            Paragraphs = document.Paragraphs.Select(paragraph => paragraph with
            {
                Runs = paragraph.Runs.Select(run => run with { Color = color }).ToList()
            }).ToList()
        };
    }

    private string DefaultTextColor()
    {
        var paper = ParseColor(_page?.Template.PaperColor ?? "#FFFDF8");
        var luminance = (0.2126 * paper.R + 0.7152 * paper.G + 0.0722 * paper.B) / 255d;
        return luminance > 0.52 ? "#20242D" : "#F4F7FB";
    }

    private static double MillisecondsSince(long timestamp) => timestamp == 0
        ? double.PositiveInfinity
        : (System.Diagnostics.Stopwatch.GetTimestamp() - timestamp) * 1000d / System.Diagnostics.Stopwatch.Frequency;

    private void OnDocumentChanged(bool recognizeInk, CanvasObject? appendedObject = null)
    {
        if (_page is not null) _page.UpdatedAt = DateTimeOffset.UtcNow;
        _hasUnsavedChanges = true;
        _editVersion++;
        if (appendedObject is InkStrokeObject appendedInk && _page is not null)
            _pendingInkAppends.Add((_page.Id, appendedInk));
        else
        {
            _requiresFullSave = true;
            _fullSaveVersion++;
        }
        var appendOnly = appendedObject is not null && _page is not null;
        var canKeepCacheForAppend = appendedObject is not null && _page is not null &&
                                    ((_pageRenderCache is not null && _pageRenderCachePageId == _page.Id) ||
                                     (_lowZoomPageRaster is not null && _lowZoomPageRasterPageId == _page.Id)) &&
                                    !_pageRenderCacheObjectIds.Contains(appendedObject.Id);
        if (canKeepCacheForAppend)
            _pageRenderOverlays.Add(appendedObject!);
        else
            InvalidatePageRenderCache();
        if (_page is not null)
        {
            if (appendOnly)
                _spatialIndex.Add(appendedObject!);
            else
            {
                _spatialIndex.Rebuild(_page.Objects);
            }
            if (_spatialIndex.Count == _page.Objects.Count)
            {
                _spatialIndexBuildCancellation?.Cancel();
                _pageSpatialIndexCache[_page.Id] = _spatialIndex;
                TouchSpatialIndex(_page.Id);
            }
        }
        if (!appendOnly) UpdateSelectionUi();
        DrawingSurface.Invalidate();
        ScheduleSave();
        if (appendedObject is ImageObject && _document is not null && _page is not null)
        {
            _pageOcrIndexedThisSession.Remove(_page.Id);
            ScheduleDocumentHandwritingIndex(_document);
        }
        if (recognizeInk) ScheduleRecognition(appendedObject as InkStrokeObject);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        _pendingThumbnailRefreshPageId = _page?.Id;
        _thumbnailRefreshTimer.Stop();
        _thumbnailRefreshTimer.Start();
    }

    private void OnThumbnailRefreshTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isPointerDown)
        {
            sender.Start();
            return;
        }
        var pageId = _pendingThumbnailRefreshPageId;
        var page = pageId is null ? null : _document?.Pages.FirstOrDefault(item => item.Id == pageId);
        _pendingThumbnailRefreshPageId = null;
        if (page is null) return;
        InvalidatePageThumbnail(page.Id);
        // When the rail is collapsed, leave the cache invalidated. Container realization will
        // request the preview if and when the user opens the rail.
        if (PageSidebar.Visibility == Visibility.Visible && PageColumn.Width.Value > 0)
            RequestPageThumbnail(page);
    }

    private void PauseThumbnailRefresh()
    {
        _thumbnailRefreshTimer.Stop();
        if (_page is null || !_pageThumbnailLoads.TryGetValue(_page.Id, out var load)) return;
        _pendingThumbnailRefreshPageId ??= _page.Id;
        load.Cancel();
    }

    private void ResumeThumbnailRefresh()
    {
        if (_isPointerDown || _pendingThumbnailRefreshPageId is null) return;
        _thumbnailRefreshTimer.Stop();
        _thumbnailRefreshTimer.Start();
    }

    private async void OnSaveTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isPointerDown)
        {
            sender.Start();
            return;
        }
        try
        {
            await SaveNowAsync();
        }
        catch (Exception exception)
        {
            ShowError("Autosave failed.", exception);
        }
    }

    private async Task SaveNowAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null || !_hasUnsavedChanges) return;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var document = _document;
            if (document is null || !_hasUnsavedChanges) return;
            var editVersion = _editVersion;
            var fullSaveVersion = _fullSaveVersion;
            var appendSnapshot = _pendingInkAppends.ToArray();
            var appendIds = appendSnapshot.Select(item => item.Stroke.Id).ToHashSet();
            StatusText.Text = "Saving…";
            var performedFullSave = _requiresFullSave;
            if (!performedFullSave && appendSnapshot.Length > 0)
                performedFullSave = !await _repository.SaveInkAppendsAsync(document, appendSnapshot, cancellationToken);
            if (performedFullSave)
                await _repository.SaveAsync(document, cancellationToken);

            _pendingInkAppends.RemoveAll(item => appendIds.Contains(item.Stroke.Id));
            if (performedFullSave && fullSaveVersion == _fullSaveVersion) _requiresFullSave = false;
            if (editVersion == _editVersion) _hasUnsavedChanges = false;
            StatusText.Text = $"Saved {DateTime.Now:t}";
        }
        finally { _saveGate.Release(); }
    }

    private void ScheduleRecognition(InkStrokeObject? appendedStroke)
    {
        // Recognition is incremental. Imported Samsung source ink can contain hundreds of
        // thousands of samples and must never be rebuilt merely because the user added a mark.
        if (_page is null || appendedStroke is null || appendedStroke.Style.PreserveSourceGeometry ||
            appendedStroke.Style.Smoothing <= 0) return;
        if (_recognitionPageId != _page.Id)
        {
            _pendingRecognitionStrokes.Clear();
            _recognitionPageId = _page.Id;
        }
        _pendingRecognitionStrokes.Add(appendedStroke);
        _recognitionTimer.Stop();
        _recognitionTimer.Start();
    }

    private async void OnRecognitionTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isPointerDown)
        {
            sender.Start();
            return;
        }
        var page = _page;
        if (page is null || _recognizer is null || _document is null || _repository is null ||
            _recognitionPageId != page.Id || _pendingRecognitionStrokes.Count == 0) return;
        var strokes = _pendingRecognitionStrokes.ToArray();
        _pendingRecognitionStrokes.Clear();
        var language = _document.Settings.RecognitionLanguage;
        _incrementalRecognitionCancellation?.Cancel();
        var cancellation = _incrementalRecognitionCancellation = new CancellationTokenSource();
        try
        {
            // Windows Ink recognizer/container objects are context-bound WinRT objects. Keep
            // their creation and lifetime on the dispatcher context; RecognizeAsync itself is
            // native asynchronous work and does not block pen or search input.
            var result = await _recognizer.RecognizeAsync(strokes, language, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_recognitionPageId != page.Id) return;
            if (string.IsNullOrWhiteSpace(result.Text) && result.Regions.Count == 0) return;
            var recognized = page.RecognizedText.Contains(result.Text, StringComparison.OrdinalIgnoreCase)
                ? page.RecognizedText
                : string.Join(' ', new[] { page.RecognizedText, result.Text }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var regions = MergeRecognizedRegions(page.RecognizedRegions, result.Regions);
            await PersistRecognizedTextAsync(_document, page, recognized, regions, CancellationToken.None);
            StatusText.Text = "Handwriting indexed";
        }
        catch (OperationCanceledException)
        {
            if (_recognitionPageId == page.Id)
            {
                foreach (var stroke in strokes)
                    if (_pendingRecognitionStrokes.All(item => item.Id != stroke.Id))
                        _pendingRecognitionStrokes.Add(stroke);
                if (!_isPointerDown)
                {
                    _recognitionTimer.Stop();
                    _recognitionTimer.Start();
                }
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Handwriting index unavailable: {exception.Message}";
        }
    }

    private void ScheduleDocumentHandwritingIndex(HoomNoteDocument document)
    {
        if (document.Pages.All(page => _pageOcrIndexedThisSession.Contains(page.Id))) return;
        if (_handwritingIndexTask is { IsCompleted: false })
        {
            if (_handwritingIndexDocumentId == document.Id) return;
            _handwritingIndexCancellation?.Cancel();
        }
        _handwritingIndexCancellation = new CancellationTokenSource();
        _handwritingIndexDocumentId = document.Id;
        _handwritingIndexTask = IndexDocumentHandwritingAsync(document, _handwritingIndexCancellation.Token);
    }

    private async Task IndexDocumentHandwritingAsync(HoomNoteDocument document, CancellationToken cancellationToken)
    {
        if (_repository is null) return;
        try
        {
            await Task.Delay(BackgroundIndexIdleDelayMs, cancellationToken);
            var indexStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            DiagnosticsLog.Info("index.document_started", ("pages", document.Pages.Count));
            await _handwritingIndexGate.WaitAsync(cancellationToken);
            try
            {
                var activePageId = _document?.Id == document.Id ? _page?.Id : null;
                foreach (var page in document.Pages
                             .Where(page => !_pageOcrIndexedThisSession.Contains(page.Id))
                             .OrderByDescending(page => page.Id == activePageId))
                {
                    var pageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    cancellationToken.ThrowIfCancellationRequested();
                    while (_isPointerDown) await Task.Delay(250, cancellationToken);
                    DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Indexing page content • {page.Title}");
                    // A complete page pass is authoritative. Starting from the old text kept
                    // inaccurate legacy guesses in FTS forever and produced irrelevant matches.
                    var recognizedParts = new List<string>();
                    var recognizedRegions = new List<RecognizedTextRegion>();
                    var objectSnapshot = page.Objects.ToArray();
                    var pageIndexInput = await Task.Run(() =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return new
                        {
                            Strokes = objectSnapshot.OfType<InkStrokeObject>()
                                .Where(stroke => !stroke.IsHidden && stroke.Style.Tool != InkToolKind.Highlighter &&
                                                 stroke.Points.Count > 1)
                                .ToArray(),
                            Images = objectSnapshot.OfType<ImageObject>()
                                .Where(image => !image.IsHidden && !string.IsNullOrWhiteSpace(image.AssetHash))
                                .ToArray()
                        };
                    }, cancellationToken);
                    var recognitionBatches = CreateSpatialRecognitionBatches(pageIndexInput.Strokes);
                    DiagnosticsLog.Info("index.page_started", ("strokes", pageIndexInput.Strokes.Length),
                        ("images", pageIndexInput.Images.Length), ("batches", recognitionBatches.Count),
                        ("has_imported_layer", page.ImportedLayer is not null));
                    if (_recognizer is not null)
                    {
                        foreach (var chunk in recognitionBatches)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            while (_isPointerDown) await Task.Delay(250, cancellationToken);
                            var recognized = await _recognizer.RecognizeAsync(chunk,
                                document.Settings.RecognitionLanguage, cancellationToken);
                            AddUniqueRecognizedText(recognizedParts, recognized.Text);
                            recognizedRegions.AddRange(recognized.Regions);
                            await Task.Yield();
                        }
                    }
                    // Printed-content OCR is reserved for PDF/image layers. Handwriting goes
                    // through InkAnalyzer in spatial batches; rasterizing an entire dense ink
                    // page consumed tens of MB and made recognition both slower and less accurate.
                    if (_pageOcr is not null && (page.ImportedLayer is not null ||
                                                  pageIndexInput.Images.Length > 0))
                    {
                        try
                        {
                            var ocrResult = await Task.Run(
                                async () => await _pageOcr.RecognizePageAsync(page, pageIndexInput.Images,
                                    [], document.Settings.RecognitionLanguage, cancellationToken),
                                cancellationToken);
                            AddUniqueRecognizedText(recognizedParts, ocrResult.Text);
                            recognizedRegions.AddRange(ocrResult.Regions);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception)
                        {
                            DiagnosticsLog.Error("index.printed_ocr_failed", exception,
                                ("images", pageIndexInput.Images.Length),
                                ("has_imported_layer", page.ImportedLayer is not null));
                            DispatcherQueue.TryEnqueue(() =>
                                StatusText.Text = $"OCR skipped for {page.Title}: {exception.Message}");
                        }
                    }
                    _pageOcrIndexedThisSession.Add(page.Id);
                    var text = string.Join(Environment.NewLine, recognizedParts).Trim();
                    var regions = MergeRecognizedRegions([], recognizedRegions);
                    await PersistRecognizedTextAsync(document, page, text, regions, cancellationToken);
                    DiagnosticsLog.Info("index.page_completed",
                        ("elapsed_ms", Math.Round(MillisecondsSince(pageStarted), 1)),
                        ("recognized_characters", text.Length), ("regions", regions.Count));
                }
            }
            finally
            {
                _handwritingIndexGate.Release();
            }
            if (_document?.Id == document.Id)
                DispatcherQueue.TryEnqueue(() => StatusText.Text = "Notebook search index ready");
            DiagnosticsLog.Info("index.document_completed",
                ("elapsed_ms", Math.Round(MillisecondsSince(indexStarted), 1)));
        }
        catch (OperationCanceledException)
        {
            DiagnosticsLog.Info("index.document_cancelled");
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("index.document_failed", exception);
            DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Page indexing unavailable: {exception.Message}");
        }
    }

    private static void AddUniqueRecognizedText(List<string> values, string? candidate)
    {
        candidate = candidate?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || values.Any(value =>
                value.Contains(candidate, StringComparison.OrdinalIgnoreCase))) return;
        values.Add(candidate);
    }

    private static IReadOnlyList<InkStrokeObject[]> CreateSpatialRecognitionBatches(
        IReadOnlyList<InkStrokeObject> strokes)
    {
        if (strokes.Count == 0) return [];
        var lines = new List<RecognitionLine>();
        foreach (var entry in strokes.Select(stroke => (Stroke: stroke, Bounds: StrokeGeometry.GetWorldBounds(stroke)))
                     .Where(entry => (entry.Bounds.Width > 0 || entry.Bounds.Height > 0) &&
                                     IsLikelyHandwritingStroke(entry.Bounds))
                     .OrderBy(entry => entry.Bounds.Center.Y)
                     .ThenBy(entry => entry.Bounds.Left))
        {
            var height = Math.Max(1, entry.Bounds.Height);
            var bestLine = lines
                .Select(line =>
                {
                    var overlap = Math.Max(0, Math.Min(line.Bottom, entry.Bounds.Bottom) -
                                              Math.Max(line.Top, entry.Bounds.Top));
                    var lineHeight = Math.Max(1, line.Bottom - line.Top);
                    var overlapRatio = overlap / Math.Min(height, lineHeight);
                    var centerDistance = Math.Abs((line.Top + line.Bottom) / 2d - entry.Bounds.Center.Y);
                    // Separate pen-down strokes often form one glyph (for example the vertical
                    // stem and top bar of an uppercase F). Use the taller stroke as the scale so
                    // those components stay together without collapsing ordinary adjacent lines.
                    var compatible = overlapRatio >= 0.16 || centerDistance <=
                        Math.Max(10, Math.Max(height, lineHeight) * 0.62);
                    return (Line: line, Score: compatible ? overlapRatio * 100 - centerDistance : double.NegativeInfinity);
                })
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            var target = bestLine.Line is not null && double.IsFinite(bestLine.Score)
                ? bestLine.Line
                : new RecognitionLine { Top = entry.Bounds.Top, Bottom = entry.Bounds.Bottom };
            if (!lines.Contains(target)) lines.Add(target);
            target.Top = Math.Min(target.Top, entry.Bounds.Top);
            target.Bottom = Math.Max(target.Bottom, entry.Bounds.Bottom);
            target.Strokes.Add(entry);
        }

        var batches = new List<InkStrokeObject[]>();
        foreach (var line in lines.OrderBy(line => line.Top))
        {
            var ordered = line.Strokes.OrderBy(item => item.Bounds.Left).ToArray();
            if (ordered.Length < 2)
            {
                batches.Add([ordered[0].Stroke]);
                continue;
            }
            var medianHeight = ordered.Select(item => Math.Max(1, item.Bounds.Height))
                .OrderBy(value => value).ElementAt(ordered.Length / 2);
            var gapThreshold = Math.Max(16, medianHeight * 1.8);
            var cluster = new List<(InkStrokeObject Stroke, RectD Bounds)> { ordered[0] };
            for (var index = 1; index < ordered.Length; index++)
            {
                var previousRight = cluster.Max(item => item.Bounds.Right);
                if (ordered[index].Bounds.Left - previousRight > gapThreshold)
                {
                    if (cluster.Count > 0) batches.AddRange(cluster.Select(item => item.Stroke).Chunk(72));
                    cluster = [];
                }
                cluster.Add(ordered[index]);
            }
            if (cluster.Count > 0)
                batches.AddRange(cluster.Select(item => item.Stroke).Chunk(72));
        }
        return batches.Where(batch => batch.Length > 0).ToArray();
    }

    private static bool IsLikelyHandwritingStroke(RectD bounds)
    {
        var width = Math.Max(0.1, bounds.Width);
        var height = Math.Max(0.1, bounds.Height);
        if (width > 120 && height < Math.Max(5, width * 0.035)) return false;
        if (height > 120 && width < Math.Max(5, height * 0.035)) return false;
        return width <= 220 || height <= 160;
    }

    private static List<RecognizedTextRegion> MergeRecognizedRegions(
        IEnumerable<RecognizedTextRegion> existing, IEnumerable<RecognizedTextRegion> additions)
    {
        return existing.Concat(additions)
            .Where(region => !string.IsNullOrWhiteSpace(region.Text) &&
                             region.Bounds.Width > 0 && region.Bounds.Height > 0)
            .GroupBy(region => (NormalizeSearchText(region.Text),
                X: Math.Round(region.Bounds.X, 1), Y: Math.Round(region.Bounds.Y, 1),
                Width: Math.Round(region.Bounds.Width, 1), Height: Math.Round(region.Bounds.Height, 1)))
            .Select(group => group.First())
            .Take(8_000)
            .ToList();
    }

    private async Task PersistRecognizedTextAsync(HoomNoteDocument document, NotePage page, string text,
        IReadOnlyList<RecognizedTextRegion> regions, CancellationToken cancellationToken)
    {
        if (_repository is null) return;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            await _repository.SaveRecognizedTextAsync(document, page, text, regions, cancellationToken);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void OnPageThumbnailContainerContentChanging(ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not NotePage page || args.ItemContainer is not ListViewItem container) return;
        UpdatePageThumbnailContainer(page, container);
        if (!_pageThumbnailCache.ContainsKey(page.Id)) RequestPageThumbnail(page);
    }

    private void RequestPageThumbnail(NotePage page)
    {
        if (_pageThumbnailRenderer is null || _pageThumbnailCache.ContainsKey(page.Id) ||
            _pageThumbnailLoads.ContainsKey(page.Id)) return;
        var cancellation = new CancellationTokenSource();
        _pageThumbnailLoads[page.Id] = cancellation;
        _ = LoadPageThumbnailAsync(page, cancellation);
    }

    private async Task LoadPageThumbnailAsync(NotePage page, CancellationTokenSource cancellation)
    {
        try
        {
            if (_pageThumbnailRenderer is null) return;
            var bytes = await _pageThumbnailRenderer.RenderAsync(page, PageThumbnailWidth,
                PageThumbnailHeight, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = PageThumbnailWidth,
                DecodePixelHeight = PageThumbnailHeight
            };
            await bitmap.SetSourceAsync(stream);
            cancellation.Token.ThrowIfCancellationRequested();
            CachePageThumbnail(page.Id, bitmap);
            if (PageList.ContainerFromItem(page) is ListViewItem container)
                UpdatePageThumbnailContainer(page, container);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Warning("thumbnail.render_failed", ("page_id", page.Id),
                ("error", exception.Message));
            if (PageList.ContainerFromItem(page) is ListViewItem container &&
                container.ContentTemplateRoot is FrameworkElement root &&
                root.FindName("PageThumbnailLoading") is ProgressRing loading)
                loading.IsActive = false;
        }
        finally
        {
            if (_pageThumbnailLoads.TryGetValue(page.Id, out var active) && ReferenceEquals(active, cancellation))
            {
                _pageThumbnailLoads.Remove(page.Id);
                cancellation.Dispose();
            }
        }
    }

    private void UpdatePageThumbnailContainer(NotePage page, ListViewItem container)
    {
        if (container.ContentTemplateRoot is not FrameworkElement root) return;
        var image = root.FindName("PageThumbnailImage") as Image;
        var loading = root.FindName("PageThumbnailLoading") as ProgressRing;
        if (_pageThumbnailCache.TryGetValue(page.Id, out var bitmap))
        {
            if (image is not null) image.Source = bitmap;
            if (loading is not null) loading.IsActive = false;
        }
        else
        {
            if (image is not null) image.Source = null;
            if (loading is not null) loading.IsActive = true;
        }
    }

    private void CachePageThumbnail(Guid pageId, BitmapImage bitmap)
    {
        _pageThumbnailCache[pageId] = bitmap;
        _pageThumbnailLru.Remove(pageId);
        _pageThumbnailLru.AddLast(pageId);
        while (_pageThumbnailLru.Count > PageThumbnailCacheLimit)
        {
            var oldest = _pageThumbnailLru.First!.Value;
            _pageThumbnailLru.RemoveFirst();
            _pageThumbnailCache.Remove(oldest);
        }
    }

    private void InvalidatePageThumbnail(Guid pageId)
    {
        _pageThumbnailCache.Remove(pageId);
        _pageThumbnailLru.Remove(pageId);
        if (_pageThumbnailLoads.Remove(pageId, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        if (_pages.FirstOrDefault(page => page.Id == pageId) is { } page &&
            PageList.ContainerFromItem(page) is ListViewItem container)
            UpdatePageThumbnailContainer(page, container);
    }

    private void OnPageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var selected = PageList.SelectedItem as NotePage;
        if (_document is not null && selected is not null) _tabPageSelections[_document.Id] = selected.Id;
        SelectPage(selected);
    }

    private async void OnNewNotebookClick(object sender, RoutedEventArgs e) =>
        await CreateDocumentAsync(DocumentKind.PagedNotebook, "Untitled notebook");

    private async void OnNewCanvasClick(object sender, RoutedEventArgs e) =>
        await CreateDocumentAsync(DocumentKind.InfiniteCanvas, "Untitled canvas");

    private async Task CreateDocumentAsync(DocumentKind kind, string title)
    {
        if (_repository is null) return;
        var document = HoomNoteDocument.Create(title, kind);
        var defaultKind = Enum.TryParse<PageTemplateKind>(_userPreferences.DefaultPageTemplate, out var parsedDefault)
            ? parsedDefault
            : PageTemplateKind.Lined;
        document.Settings = document.Settings with
        {
            DefaultPageTemplateKind = defaultKind,
            DefaultPaperColor = _userPreferences.DefaultPageColor
        };
        if (kind == DocumentKind.InfiniteCanvas)
        {
            var canvas = new NotePage
            {
                Title = "Canvas",
                Size = new SizeD(8192, 8192),
                Template = PageTemplate.For(PageTemplateKind.Blank) with
                {
                    PaperColor = "#191919", LineColor = "#343434"
                }
            };
            document.Pages.Add(canvas);
            document.Sections[0].PageIds.Add(canvas.Id);
        }
        await _repository.SaveAsync(document);
        if (_selectedFolderId is { } folderId)
        {
            _userPreferences.DocumentFolders[document.Id.ToString("D")] = folderId.ToString("D");
            await PersistUserPreferencesAsync("Created notebook in folder");
        }
        await RefreshLibraryAsync();
        await LoadDocumentAsync(document.Id);
        SelectLibraryDocument(document.Id);
    }

    private void OnAddPageClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var page = new NotePage
        {
            Title = $"Page {_document.Pages.Count + 1}",
            Template = CreatePageTemplate(_document.Settings.DefaultPageTemplateKind,
                _document.Settings.DefaultPaperColor)
        };
        _document.Pages.Add(page);
        _document.Sections.FirstOrDefault()?.PageIds.Add(page.Id);
        _pages.Add(page);
        PageList.SelectedItem = page;
        OnDocumentChanged(recognizeInk: false);
    }

    private async void OnNotebookSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var style = new ComboBox { Header = "Page style", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var optionKind in new[] { PageTemplateKind.Blank, PageTemplateKind.Lined, PageTemplateKind.Dotted,
                     PageTemplateKind.SquareGrid, PageTemplateKind.Graph })
            style.Items.Add(new ComboBoxItem { Tag = optionKind.ToString(), Content = TemplateDisplayName(optionKind) });
        style.SelectedItem = style.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, _document.Settings.DefaultPageTemplateKind.ToString())) ?? style.Items[1];
        var color = new ColorPicker
        {
            Color = ParseColor(_document.Settings.DefaultPaperColor),
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false
        };
        var applyExisting = new CheckBox { Content = "Apply to all existing pages", IsChecked = true };
        var useForNotebook = new CheckBox { Content = "Use for new pages in this notebook", IsChecked = true };
        var makeGlobal = new CheckBox { Content = "Use as the default for new notebooks", IsChecked = false };
        var content = new StackPanel { Spacing = 12, Width = 360 };
        content.Children.Add(style);
        content.Children.Add(new TextBlock { Text = "Page color" });
        content.Children.Add(color);
        content.Children.Add(applyExisting);
        content.Children.Add(useForNotebook);
        content.Children.Add(makeGlobal);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Notebook page settings",
            Content = content,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            style.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<PageTemplateKind>(tag, out var selectedKind)) return;
        var paperColor = $"#{color.Color.R:X2}{color.Color.G:X2}{color.Color.B:X2}";
        if (useForNotebook.IsChecked == true)
        {
            _document.Settings = _document.Settings with
            {
                DefaultPageTemplateKind = selectedKind,
                DefaultPaperColor = paperColor
            };
        }
        if (applyExisting.IsChecked == true)
        {
            foreach (var page in _document.Pages)
            {
                page.Template = CreatePageTemplate(selectedKind, paperColor);
                page.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
        if (makeGlobal.IsChecked == true)
        {
            _userPreferences = _userPreferences with
            {
                DefaultPageTemplate = selectedKind.ToString(),
                DefaultPageColor = paperColor
            };
            await PersistUserPreferencesAsync("Updated default page settings");
        }
        MarkFullDocumentDirty();
        SyncTemplatePicker();
        if (applyExisting.IsChecked == true) ClearWarmPageRenderCaches();
        InvalidatePageRenderCache();
        DrawingSurface.Invalidate();
        StatusText.Text = applyExisting.IsChecked == true ? "Updated all notebook pages" : "Updated notebook defaults";
    }

    private static string TemplateDisplayName(PageTemplateKind kind) => kind switch
    {
        PageTemplateKind.Dotted => "Dotted grid",
        PageTemplateKind.SquareGrid => "Square grid",
        _ => kind.ToString()
    };

    private static PageTemplate CreatePageTemplate(PageTemplateKind kind, string paperColor)
    {
        var lineColor = IsDarkColor(paperColor) ? "#454B57" : PageTemplate.For(kind).LineColor;
        return PageTemplate.For(kind) with { PaperColor = paperColor, LineColor = lineColor };
    }

    private void MarkFullDocumentDirty()
    {
        if (_document is null) return;
        _document.UpdatedAt = DateTimeOffset.UtcNow;
        _hasUnsavedChanges = true;
        _editVersion++;
        _requiresFullSave = true;
        _fullSaveVersion++;
        ScheduleSave();
    }

    private async void OnDeletePageClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _page is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete this page?",
            Content = $"Delete “{_page.Title}” and everything on it? You can undo this action.",
            PrimaryButtonText = "Delete page",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var deletedIndex = _document.Pages.FindIndex(page => page.Id == _page.Id);
        _history.Execute(new DeletePageCommand(_page.Id), _document);
        var nextPage = _document.Pages.Count == 0
            ? null
            : _document.Pages[Math.Clamp(deletedIndex, 0, _document.Pages.Count - 1)];
        SyncPageCollection(nextPage?.Id);
        OnDocumentChanged(recognizeInk: false);
    }

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton selected || selected.Tag is not string tag ||
            !Enum.TryParse<EditorTool>(tag, out var tool)) return;
        ActivateTool(tool, selected);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compactToolbar = e.NewSize.Width < 1_180;
        var narrowToolbar = e.NewSize.Width < 760;
        ToolbarSecondaryActions.Visibility = compactToolbar ? Visibility.Collapsed : Visibility.Visible;
        ToolbarOverflowActionsButton.Visibility = Visibility.Visible;
        AutosavedStatusBadge.Visibility = narrowToolbar ? Visibility.Collapsed : Visibility.Visible;
        NotebookTabs.Margin = new Thickness(8, 0, narrowToolbar ? 8 : 120, 0);
        PresetScrollViewer.MinWidth = narrowToolbar ? 72 : 120;
        PresetScrollViewer.MaxWidth = compactToolbar
            ? narrowToolbar ? 140 : 260
            : 420;

        var compact = e.NewSize.Width < 980 || e.NewSize.Height < 620;
        if (compact == _compactLayout) return;
        _compactLayout = compact;
        if (compact)
        {
            _compactLibraryWasVisible = LibrarySidebar.Visibility == Visibility.Visible && LibraryColumn.Width.Value > 0;
            _compactPagesWereVisible = PageSidebar.Visibility == Visibility.Visible && PageColumn.Width.Value > 0;
            _compactInspectorWasVisible = InspectorSidebar.Visibility == Visibility.Visible && InspectorColumn.Width.Value > 0;
            LibraryColumn.Width = new GridLength(0);
            PageColumn.Width = new GridLength(0);
            InspectorColumn.Width = new GridLength(0);
            LibrarySidebar.Visibility = Visibility.Collapsed;
            PageSidebar.Visibility = Visibility.Collapsed;
            InspectorSidebar.Visibility = Visibility.Collapsed;
            return;
        }

        if (_readMode) return;
        LibrarySidebar.Visibility = _compactLibraryWasVisible ? Visibility.Visible : Visibility.Collapsed;
        PageSidebar.Visibility = _compactPagesWereVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorSidebar.Visibility = _compactInspectorWasVisible ? Visibility.Visible : Visibility.Collapsed;
        LibraryColumn.Width = new GridLength(_compactLibraryWasVisible ? LibraryWidth : 0);
        PageColumn.Width = new GridLength(_compactPagesWereVisible ? PageRailWidth : 0);
        InspectorColumn.Width = new GridLength(_compactInspectorWasVisible ? InspectorWidth : 0);
    }

    private void OnOverflowToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string tag } ||
            !Enum.TryParse<EditorTool>(tag, out var tool)) return;
        ActivateTool(tool);
    }

    private void ActivateTool(EditorTool tool, ToggleButton? selected = null)
    {
        _presetOpacity = null;
        _presetSmoothing = null;
        _activeTool = tool;
        if (tool == EditorTool.Highlighter)
        {
            _colorTool = EditorTool.Highlighter;
            SetInkColor(_highlighterColor, rememberForTool: false);
        }
        else if (tool is EditorTool.Pen or EditorTool.Shape)
        {
            _colorTool = EditorTool.Pen;
            SetInkColor(_penColor, rememberForTool: false);
        }
        InkColorLabel.Text = _colorTool == EditorTool.Highlighter ? "Highlighter color" : "Pen color";
        foreach (var toggle in ToolButtons.Children.OfType<ToggleButton>())
            toggle.IsChecked = selected is not null ? toggle == selected : string.Equals(toggle.Tag as string, tool.ToString(), StringComparison.Ordinal);
        MoreToolsButton.Background = tool is EditorTool.Style or EditorTool.SegmentEraser or EditorTool.Text or
            EditorTool.Shape or EditorTool.BoxSelect
            ? new SolidColorBrush(Color.FromArgb(90, 56, 189, 248))
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        StyleToolPanel.Visibility = tool == EditorTool.Style ? Visibility.Visible : Visibility.Collapsed;
        if (tool == EditorTool.Style)
        {
            if (InspectorSidebar.Visibility != Visibility.Visible || InspectorColumn.Width.Value <= 0)
                _ = AnimateSidebarAsync(InspectorColumn, InspectorSidebar, InspectorWidth, opening: true);
            _styleToolPickMode = true;
            _styleToolColor = _inkColor;
            _styleToolWidth = (float)StrokeWidthSlider.Value;
            UpdateStyleToolUi();
            StatusText.Text = "Style tool • click an object to pick its style";
        }
        else StatusText.Text = tool.ToString();
    }

    private void OnStyleModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string mode }) return;
        _styleToolPickMode = mode == "Pick";
        UpdateStyleToolUi();
        StatusText.Text = _styleToolPickMode
            ? "Style tool • click an object to pick its style"
            : "Style tool • click objects to apply the chosen style";
    }

    private void OnStyleToolColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingStyleTool) return;
        _styleToolColor = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        _styleToolPickMode = false;
        UpdateStyleToolModeButtons();
    }

    private void OnStyleToolWidthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingStyleTool) return;
        _styleToolWidth = (float)e.NewValue;
        _styleToolPickMode = false;
        UpdateStyleToolModeButtons();
    }

    private void UpdateStyleToolUi()
    {
        _syncingStyleTool = true;
        StyleColorPicker.Color = ParseColor(_styleToolColor);
        StyleWidthSlider.Value = _styleToolWidth;
        _syncingStyleTool = false;
        UpdateStyleToolModeButtons();
    }

    private void UpdateStyleToolModeButtons()
    {
        StylePickModeButton.IsChecked = _styleToolPickMode;
        StyleApplyModeButton.IsChecked = !_styleToolPickMode;
    }

    private void OnTemporaryGridToggled(object sender, RoutedEventArgs e)
    {
        _temporaryGridVisible = TemporaryGridToggle.IsOn;
        TemporaryGridSizeSlider.IsEnabled = _temporaryGridVisible;
        ClearWarmPageRenderCaches();
        InvalidatePageRenderCache();
        DrawingSurface.Invalidate();
    }

    private void OnTemporaryGridSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _temporaryGridSize = e.NewValue;
        if (TemporaryGridSizeValue is not null) TemporaryGridSizeValue.Text = $"{e.NewValue:0}";
        if (_temporaryGridVisible)
        {
            ClearWarmPageRenderCaches();
            InvalidatePageRenderCache();
            DrawingSurface.Invalidate();
        }
    }

    private void OnInkSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (StrokeWidthValue is not null) StrokeWidthValue.Text = $"{StrokeWidthSlider.Value:0.#}";
    }

    private void OnSliderReadoutPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } owner) return;
        _scrubSlider = tag switch
        {
            "Grid" => TemporaryGridSizeSlider,
            "Width" => StrokeWidthSlider,
            _ => null
        };
        if (_scrubSlider is null) return;
        _scrubOwner = owner;
        _scrubStartX = e.GetCurrentPoint(this).Position.X;
        _scrubStartValue = _scrubSlider.Value;
        owner.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSliderReadoutPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubSlider is null || _scrubOwner is null) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        var unitsPerPixel = (_scrubSlider.Maximum - _scrubSlider.Minimum) / 180d;
        var raw = _scrubStartValue + (point.Position.X - _scrubStartX) * unitsPerPixel;
        var step = Math.Max(0.0001, _scrubSlider.StepFrequency);
        _scrubSlider.Value = Math.Clamp(Math.Round(raw / step) * step,
            _scrubSlider.Minimum, _scrubSlider.Maximum);
        e.Handled = true;
    }

    private void OnSliderReadoutPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _scrubOwner?.ReleasePointerCapture(e.Pointer);
        _scrubSlider = null;
        _scrubOwner = null;
        e.Handled = true;
    }

    private void OnHighlighterStraightToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _userPreferences = _userPreferences with { HighlighterStraightLine = HighlighterStraightToggle.IsOn };
        ScheduleUserPreferencesSave();
    }

    private async void OnSidebarToggleClick(object sender, RoutedEventArgs e)
    {
        if (_readMode) return;
        if (sender is not FrameworkElement { Tag: string sidebar }) return;
        switch (sidebar)
        {
            case "Library":
                await AnimateSidebarAsync(LibraryColumn, LibrarySidebar, LibraryWidth,
                    LibrarySidebar.Visibility != Visibility.Visible || LibraryColumn.Width.Value <= 0);
                break;
            case "Pages":
                await AnimateSidebarAsync(PageColumn, PageSidebar, PageRailWidth,
                    PageSidebar.Visibility != Visibility.Visible || PageColumn.Width.Value <= 0);
                break;
            case "Inspector":
                await AnimateSidebarAsync(InspectorColumn, InspectorSidebar, InspectorWidth,
                    InspectorSidebar.Visibility != Visibility.Visible || InspectorColumn.Width.Value <= 0);
                break;
        }
    }

    private async Task AnimateSidebarAsync(ColumnDefinition column, FrameworkElement sidebar,
        double expandedWidth, bool opening)
    {
        if (_sidebarAnimations.Remove(column, out var previous)) previous.Cancel();
        var cancellation = new CancellationTokenSource();
        _sidebarAnimations[column] = cancellation;
        var startWidth = column.ActualWidth;
        if (!double.IsFinite(startWidth) || startWidth < 0) startWidth = column.Width.Value;
        var targetWidth = opening ? expandedWidth : 0;
        if (opening)
        {
            sidebar.Visibility = Visibility.Visible;
            sidebar.Opacity = 0;
        }
        try
        {
            const int durationMilliseconds = 170;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d /
                              System.Diagnostics.Stopwatch.Frequency;
                var progress = Math.Clamp(elapsed / durationMilliseconds, 0, 1);
                var eased = 1 - Math.Pow(1 - progress, 3);
                column.Width = new GridLength(startWidth + (targetWidth - startWidth) * eased);
                sidebar.Opacity = opening ? eased : 1 - eased;
                if (progress >= 1) break;
                await Task.Delay(16, cancellation.Token);
            }
            column.Width = new GridLength(targetWidth);
            sidebar.Opacity = 1;
            if (!opening) sidebar.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_sidebarAnimations.GetValueOrDefault(column) == cancellation)
                _sidebarAnimations.Remove(column);
            cancellation.Dispose();
        }
    }

    private void OnReadModeClick(object sender, RoutedEventArgs e) => SetReadMode(!_readMode);

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e) =>
        await UpdateService.CheckForUpdatesAsync(XamlRoot, manual: true, PrepareForUpdateRestartAsync);

    private async Task PrepareForUpdateRestartAsync()
    {
        _saveTimer.Stop();
        await SaveNowAsync();
        if (_userSettingsStore is not null) await SaveUserPreferencesAsync();
        DiagnosticsLog.Shutdown("app_update");
    }

    private void SetReadMode(bool enabled)
    {
        if (enabled == _readMode || enabled && _page is null) return;
        foreach (var animation in _sidebarAnimations.Values.ToArray()) animation.Cancel();
        if (enabled)
        {
            _libraryWasVisible = LibrarySidebar.Visibility == Visibility.Visible;
            _pagesWereVisible = PageSidebar.Visibility == Visibility.Visible;
            _inspectorWasVisible = InspectorSidebar.Visibility == Visibility.Visible;
            LibrarySidebar.Visibility = Visibility.Collapsed;
            PageSidebar.Visibility = Visibility.Collapsed;
            InspectorSidebar.Visibility = Visibility.Collapsed;
            LibraryColumn.Width = new GridLength(0);
            PageColumn.Width = new GridLength(0);
            InspectorColumn.Width = new GridLength(0);
            TopToolbar.Visibility = Visibility.Collapsed;
            NotebookTabBar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            ToolbarRow.Height = new GridLength(0);
            TabsRow.Height = new GridLength(0);
            FooterRow.Height = new GridLength(0);
            TextEditorOverlay.Visibility = Visibility.Collapsed;
            EditorOverlay.IsHitTestVisible = false;
            _selectedObject = null;
            _selectedObjects.Clear();
            UpdateSelectionUi();
        }
        else
        {
            LibrarySidebar.Visibility = _libraryWasVisible ? Visibility.Visible : Visibility.Collapsed;
            PageSidebar.Visibility = _pagesWereVisible ? Visibility.Visible : Visibility.Collapsed;
            InspectorSidebar.Visibility = _inspectorWasVisible ? Visibility.Visible : Visibility.Collapsed;
            LibraryColumn.Width = new GridLength(_libraryWasVisible ? LibraryWidth : 0);
            PageColumn.Width = new GridLength(_pagesWereVisible ? PageRailWidth : 0);
            InspectorColumn.Width = new GridLength(_inspectorWasVisible ? InspectorWidth : 0);
            TopToolbar.Visibility = Visibility.Visible;
            NotebookTabBar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Collapsed;
            ToolbarRow.Height = new GridLength(52);
            TabsRow.Height = new GridLength(42);
            FooterRow.Height = new GridLength(0);
            EditorOverlay.IsHitTestVisible = true;
        }
        _readMode = enabled;
        ReadModeExitButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
        DrawingSurface.Invalidate();
    }

    private void UpdateEmptyState()
    {
        if (_readMode)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }
        if (_document is null)
        {
            EmptyStateTitle.Text = "Create a notebook to begin";
            EmptyStateMessage.Text = "Ink, type, import documents, and keep everything local.";
            EmptyStateAddPageButton.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            return;
        }
        if (_page is null)
        {
            EmptyStateTitle.Text = "This notebook has no pages";
            EmptyStateMessage.Text = "Add a blank page or import a PDF, presentation, or Samsung note.";
            EmptyStateAddPageButton.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Visible;
            return;
        }
        EmptyState.Visibility = Visibility.Collapsed;
    }

    private static bool IsDarkColor(string value)
    {
        var color = ParseColor(value);
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d;
        return luminance < 0.45;
    }

    private bool ShortcutTargetsTextInput() => FocusManager.GetFocusedElement(XamlRoot) is TextBox or AutoSuggestBox or NumberBox;

    private async void OnSaveShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput()) return;
        await SaveNowAsync();
        StatusText.Text = "Saved";
        args.Handled = true;
    }

    private async void OnNewNotebookShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput()) return;
        await CreateDocumentAsync(DocumentKind.PagedNotebook, "Untitled notebook");
        args.Handled = true;
    }

    private void OnImportShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput()) return;
        OnImportClick(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnExportShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput()) return;
        OnExportClick(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnAddPageShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput()) return;
        OnAddPageClick(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnReadModeShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput()) return;
        SetReadMode(!_readMode);
        args.Handled = true;
    }

    private void OnGridShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput() || _readMode) return;
        TemporaryGridToggle.IsOn = !TemporaryGridToggle.IsOn;
        args.Handled = true;
    }

    private void OnDeleteSelectionShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput() || _readMode) return;
        OnDeleteClick(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnCopyShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput() || _readMode) return;
        CopySelectionToClipboard();
        args.Handled = true;
    }

    private void OnCutShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput() || _readMode) return;
        OnCutClick(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private async void OnPasteShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput() || _readMode) return;
        await PasteSelectionAsync();
        args.Handled = true;
    }

    private async void OnGlobalKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (ShortcutTargetsTextInput()) return;
        var controlDown = IsControlDown();
        if (controlDown && args.Key == VirtualKey.S)
        {
            await SaveNowAsync();
            StatusText.Text = "Saved";
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.N)
        {
            await CreateDocumentAsync(DocumentKind.PagedNotebook, "Untitled notebook");
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.O)
        {
            OnImportClick(sender, new RoutedEventArgs());
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.E)
        {
            OnExportClick(sender, new RoutedEventArgs());
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.Enter)
        {
            OnAddPageClick(sender, new RoutedEventArgs());
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.C)
        {
            if (!_readMode) CopySelectionToClipboard();
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.X)
        {
            if (!_readMode) OnCutClick(sender, new RoutedEventArgs());
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.V)
        {
            if (!_readMode) _ = PasteSelectionAsync();
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.Z)
        {
            if (IsShiftDown()) OnRedoClick(sender, new RoutedEventArgs());
            else OnUndoClick(sender, new RoutedEventArgs());
            args.Handled = true;
            return;
        }
        if (controlDown && args.Key == VirtualKey.Y)
        {
            OnRedoClick(sender, new RoutedEventArgs());
            args.Handled = true;
            return;
        }
        if (!controlDown && args.Key == VirtualKey.Escape)
        {
            if (_readMode) SetReadMode(false);
            else
            {
                _selectedObject = null;
                _selectedObjects.Clear();
                _transformPreview = null;
                _multiTransformPreviews.Clear();
                UpdateSelectionUi();
                DrawingSurface.Invalidate();
            }
            args.Handled = true;
            return;
        }
        if (!controlDown && args.Key == VirtualKey.R)
        {
            SetReadMode(!_readMode);
            args.Handled = true;
            return;
        }
        if (!controlDown && args.Key == VirtualKey.G)
        {
            if (!_readMode) TemporaryGridToggle.IsOn = !TemporaryGridToggle.IsOn;
            args.Handled = true;
            return;
        }
        if (!controlDown && args.Key is VirtualKey.V or VirtualKey.P or VirtualKey.E or VirtualKey.T or VirtualKey.H or VirtualKey.L)
        {
            if (!_readMode)
            {
                var tool = args.Key switch
                {
                    VirtualKey.V => EditorTool.Select,
                    VirtualKey.P => EditorTool.Pen,
                    VirtualKey.E => EditorTool.StrokeEraser,
                    VirtualKey.T => EditorTool.Text,
                    VirtualKey.H => EditorTool.Highlighter,
                    VirtualKey.L => EditorTool.Lasso,
                    _ => EditorTool.Select
                };
                ActivateTool(tool);
            }
            args.Handled = true;
            return;
        }
        if (!controlDown && _selectedObject is RichTextObject typedText &&
            args.Key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            ShowTextEditor(typedText);
            var character = (char)('a' + ((int)args.Key - (int)VirtualKey.A));
            TextEditorOverlay.SelectedText = IsShiftDown()
                ? char.ToUpperInvariant(character).ToString()
                : character.ToString();
            args.Handled = true;
            return;
        }
        if (_selectedObject is RichTextObject selectedText && args.Key is VirtualKey.F2 or VirtualKey.Enter)
        {
            ShowTextEditor(selectedText);
            args.Handled = true;
            return;
        }
        if (args.Key != VirtualKey.Delete || _readMode) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (IsDescendantOf(focused, PageList) && _page is not null)
        {
            OnDeletePageClick(sender, new RoutedEventArgs());
            args.Handled = true;
            return;
        }
        if (_selectedObject is null && _selectedObjects.Count == 0) return;
        OnDeleteClick(sender, new RoutedEventArgs());
        args.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_readMode || TextEditorOverlay.Visibility == Visibility.Visible ||
            _selectedObject is not RichTextObject selectedText || IsControlDown()) return;
        var character = (char)args.Character;
        if (character < ' ' && character is not '\r' and not '\n') return;
        ShowTextEditor(selectedText);
        var insertion = character is '\r' or '\n' ? Environment.NewLine : character.ToString();
        TextEditorOverlay.SelectedText = insertion;
        args.Handled = true;
    }

    private static bool IsDescendantOf(DependencyObject? element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void OnEscapeShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_readMode)
        {
            SetReadMode(false);
            args.Handled = true;
            return;
        }
        if (ShortcutTargetsTextInput()) return;
        _selectedObject = null;
        _selectedObjects.Clear();
        _transformPreview = null;
        _multiTransformPreviews.Clear();
        UpdateSelectionUi();
        DrawingSurface.Invalidate();
        args.Handled = true;
    }

    private void OnToolShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutTargetsTextInput() || _readMode) return;
        var tool = sender.Key switch
        {
            VirtualKey.V => EditorTool.Select,
            VirtualKey.P => EditorTool.Pen,
            VirtualKey.E => EditorTool.StrokeEraser,
            VirtualKey.T => EditorTool.Text,
            VirtualKey.H => EditorTool.Highlighter,
            VirtualKey.L => EditorTool.Lasso,
            _ => EditorTool.Select
        };
        ActivateTool(tool);
        args.Handled = true;
    }

    private void OnInkColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingInkColor) return;
        SetInkColor($"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}");
    }

    private async void OnSaveInkColorClick(object sender, RoutedEventArgs e)
    {
        if (_savedColors.Any(item => item.Hex == _inkColor))
        {
            SavedColorPalette.SelectedItem = _savedColors.First(item => item.Hex == _inkColor);
            return;
        }
        if (_savedColors.Count >= 24)
        {
            ImportInfo.Title = "Palette is full";
            ImportInfo.Message = "Remove a saved color before adding another one.";
            ImportInfo.Severity = InfoBarSeverity.Informational;
            ImportInfo.IsOpen = true;
            return;
        }

        var item = new SavedColorItem(_inkColor);
        _savedColors.Add(item);
        SavedColorPalette.SelectedItem = item;
        await SaveUserPreferencesAsync();
    }

    private void OnSavedColorSelected(object sender, SelectionChangedEventArgs e)
    {
        RemoveSavedColorButton.IsEnabled = SavedColorPalette.SelectedItem is SavedColorItem;
        if (SavedColorPalette.SelectedItem is SavedColorItem color) SetInkColor(color.Hex);
    }

    private async void OnRemoveSavedColorClick(object sender, RoutedEventArgs e)
    {
        if (SavedColorPalette.SelectedItem is not SavedColorItem color) return;
        _savedColors.Remove(color);
        RemoveSavedColorButton.IsEnabled = false;
        await SaveUserPreferencesAsync();
    }

    private async void OnAddPenPresetClick(object sender, RoutedEventArgs e) =>
        await AddToolbarPresetAsync(EditorTool.Pen);

    private async void OnAddHighlighterPresetClick(object sender, RoutedEventArgs e) =>
        await AddToolbarPresetAsync(EditorTool.Highlighter);

    private async Task AddToolbarPresetAsync(EditorTool tool)
    {
        if (_userPreferences.ToolbarPresets.Count >= 12)
        {
            ImportInfo.Title = "Toolbar is full";
            ImportInfo.Message = "Right-click a custom pen on the toolbar to remove it.";
            ImportInfo.Severity = InfoBarSeverity.Informational;
            ImportInfo.IsOpen = true;
            return;
        }
        var style = tool == EditorTool.Highlighter
            ? new InkStyle
            {
                Tool = InkToolKind.Highlighter,
                Color = _highlighterColor,
                Width = Math.Max(12, (float)StrokeWidthSlider.Value * 3),
                Opacity = 0.34f,
                PressureEnabled = false,
                PressureSensitivity = 0,
                Smoothing = 0.8f
            }
            : new InkStyle
            {
                Tool = InkToolKind.Pen,
                Color = _penColor,
                Width = (float)StrokeWidthSlider.Value,
                Opacity = _presetOpacity ?? 1f,
                PressureEnabled = false,
                PressureSensitivity = 0,
                Smoothing = _presetSmoothing ?? 0.9f
            };
        _userPreferences.ToolbarPresets.Add(new ToolbarPresetPreference
        {
            Tool = tool.ToString(),
            Color = tool == EditorTool.Highlighter ? _highlighterColor : _penColor,
            Width = StrokeWidthSlider.Value,
            PressureSensitivity = 0,
            Opacity = style.Opacity,
            Smoothing = style.Smoothing,
            StraightLine = tool == EditorTool.Highlighter && HighlighterStraightToggle.IsOn
        });
        RebuildPresetToolbar();
        await PersistUserPreferencesAsync("Saved toolbar preset");
    }

    private void RebuildPresetToolbar()
    {
        PresetToolButtons.Children.Clear();
        foreach (var preset in _userPreferences.ToolbarPresets)
        {
            FrameworkElement swatch = preset.Tool == nameof(EditorTool.Highlighter)
                ? new Border
                {
                    Width = 30, Height = 16, CornerRadius = new CornerRadius(4),
                    Background = new SolidColorBrush(ParseColor(preset.Color)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    BorderThickness = new Thickness(1)
                }
                : new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 28, Height = 28,
                    Fill = new SolidColorBrush(ParseColor(preset.Color)),
                    Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                    StrokeThickness = 1
                };
            var content = new Grid();
            var grip = new Border
            {
                Tag = preset.Id,
                CanDrag = true,
                Width = 10,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
            };
            SetTransientToolTip(grip, "Drag to reorder");
            grip.Tapped += OnPresetGripTapped;
            grip.DragStarting += OnPresetDragStarting;
            grip.DropCompleted += OnPresetDropCompleted;
            content.Children.Add(swatch);
            content.Children.Add(grip);

            var tile = new Border
            {
                Tag = preset.Id,
                Child = content,
                CanDrag = false,
                Width = 40,
                Height = 38,
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var mode = preset.Tool == nameof(EditorTool.Highlighter)
                ? preset.StraightLine ? "straight" : "freeform"
                : "constant width";
            SetTransientToolTip(tile, $"{preset.Tool} • {preset.Color} • {preset.Width:0.#} • {mode}");
            tile.Tapped += OnToolbarPresetTapped;
            var flyout = new MenuFlyout();
            var remove = new MenuFlyoutItem { Text = "Remove preset", Tag = preset.Id };
            remove.Click += OnRemoveToolbarPresetClick;
            flyout.Items.Add(remove);
            tile.ContextFlyout = flyout;
            PresetToolButtons.Children.Add(tile);
        }
    }

    private void OnPresetScrollWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(PresetScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0 || PresetScrollViewer.ScrollableWidth <= 0) return;
        var target = Math.Clamp(PresetScrollViewer.HorizontalOffset - delta * 0.8,
            0, PresetScrollViewer.ScrollableWidth);
        PresetScrollViewer.ChangeView(target, null, null, disableAnimation: false);
        e.Handled = true;
    }

    private static void OnPresetGripTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnPresetDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;
        _draggedPresetId = id;
        args.AllowedOperations = DataPackageOperation.Move;
        args.Data.SetText($"hoomnote-preset:{id:D}");
    }

    private void OnPresetToolbarDragOver(object sender, DragEventArgs e)
    {
        if (_draggedPresetId is null) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
        e.Handled = true;
    }

    private async void OnPresetToolbarDrop(object sender, DragEventArgs e)
    {
        if (_draggedPresetId is not { } sourceId) return;
        var sourceIndex = _userPreferences.ToolbarPresets.FindIndex(item => item.Id == sourceId);
        if (sourceIndex < 0) return;
        var pointerX = e.GetPosition(PresetToolButtons).X;
        var targetIndex = _userPreferences.ToolbarPresets.Count;
        for (var index = 0; index < PresetToolButtons.Children.Count; index++)
        {
            if (PresetToolButtons.Children[index] is not FrameworkElement child) continue;
            var left = child.TransformToVisual(PresetToolButtons).TransformPoint(new Point(0, 0)).X;
            if (pointerX < left + child.ActualWidth / 2)
            {
                targetIndex = index;
                break;
            }
        }
        var moved = _userPreferences.ToolbarPresets[sourceIndex];
        _userPreferences.ToolbarPresets.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex) targetIndex--;
        _userPreferences.ToolbarPresets.Insert(Math.Clamp(targetIndex, 0,
            _userPreferences.ToolbarPresets.Count), moved);
        _draggedPresetId = null;
        RebuildPresetToolbar();
        await PersistUserPreferencesAsync("Reordered toolbar presets");
        DiagnosticsLog.Info("preset.reordered");
        e.Handled = true;
    }

    private void OnPresetDropCompleted(UIElement sender, DropCompletedEventArgs args) => _draggedPresetId = null;

    private void OnToolbarPresetTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;
        var preset = _userPreferences.ToolbarPresets.FirstOrDefault(item => item.Id == id);
        if (preset is null) return;
        var presetTool = string.Equals(preset.Tool, "Pencil", StringComparison.OrdinalIgnoreCase)
            ? nameof(EditorTool.Pen)
            : preset.Tool;
        if (!Enum.TryParse<EditorTool>(presetTool, out var tool)) return;
        ActivateTool(tool);
        _presetOpacity = (float)Math.Clamp(preset.Opacity, 0.05, 1);
        _presetSmoothing = (float)Math.Clamp(preset.Smoothing, 0, 1);
        SetInkColor(preset.Color);
        StrokeWidthSlider.Value = preset.Width;
        if (tool == EditorTool.Highlighter) HighlighterStraightToggle.IsOn = preset.StraightLine;
    }

    private async void OnRemoveToolbarPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Guid id }) return;
        _userPreferences.ToolbarPresets.RemoveAll(item => item.Id == id);
        RebuildPresetToolbar();
        await PersistUserPreferencesAsync("Removed toolbar preset");
    }

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _page is null || TemplatePicker.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<PageTemplateKind>(tag, out var kind)) return;
        var current = _page.Template;
        _page.Template = PageTemplate.For(kind) with
        {
            PaperColor = current.PaperColor,
            LineColor = current.LineColor,
            Margin = current.Margin,
            LineWidth = current.LineWidth
        };
        OnDocumentChanged(recognizeInk: false);
    }

    private void OnPageColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading || _page is null) return;
        var paper = $"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}";
        var dark = IsDarkColor(paper);
        var line = dark ? "#414141" : "#D5DAE2";
        _page.Template = _page.Template with { PaperColor = paper, LineColor = line };
        PageColorSwatch.Background = new SolidColorBrush(args.NewColor);
        if (dark && _penColor == "#111111")
        {
            _penColor = "#F4F4F4";
            if (_colorTool == EditorTool.Pen) SetInkColor(_penColor, rememberForTool: false);
            ScheduleUserPreferencesSave();
        }
        else if (!dark && _penColor == "#F4F4F4")
        {
            _penColor = "#111111";
            if (_colorTool == EditorTool.Pen) SetInkColor(_penColor, rememberForTool: false);
            ScheduleUserPreferencesSave();
        }
        OnDocumentChanged(recognizeInk: false);
    }

    private void SyncTemplatePicker()
    {
        _loading = true;
        if (_page is null)
        {
            TemplatePicker.SelectedItem = null;
            _loading = false;
            return;
        }
        var visibleKind = _page.Template.Kind == PageTemplateKind.DarkPaper
            ? PageTemplateKind.Lined
            : _page.Template.Kind;
        TemplatePicker.SelectedItem = TemplatePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, visibleKind.ToString(), StringComparison.Ordinal));
        var paper = ParseColor(_page.Template.PaperColor);
        PageColorPicker.Color = paper;
        PageColorSwatch.Background = new SolidColorBrush(paper);
        _loading = false;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var pageIds = _document.Pages.Select(page => page.Id).ToArray();
        _history.Undo(_document);
        if (!pageIds.SequenceEqual(_document.Pages.Select(page => page.Id))) SyncPageCollection(_page?.Id);
        OnDocumentChanged(recognizeInk: true);
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var pageIds = _document.Pages.Select(page => page.Id).ToArray();
        _history.Redo(_document);
        if (!pageIds.SequenceEqual(_document.Pages.Select(page => page.Id))) SyncPageCollection(_page?.Id);
        OnDocumentChanged(recognizeInk: true);
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _page is null) return;
        var source = _selectedObjects.Count > 0 ? _selectedObjects :
            _selectedObject is null ? [] : [_selectedObject];
        if (source.Count == 0) return;
        var duplicates = source.Select((item, index) => item with
        {
            Id = Guid.NewGuid(),
            Transform = item.Transform.Then(Transform2D.Translation(24, 24)),
            ZIndex = NextZIndex() + index
        }).ToArray();
        foreach (var duplicate in duplicates)
            _history.Execute(new AddObjectCommand(_page.Id, duplicate), _document);
        _selectedObjects.Clear();
        _selectedObjects.AddRange(duplicates);
        _selectedObject = duplicates.Length == 1 ? duplicates[0] : null;
        OnDocumentChanged(recognizeInk: duplicates.Any(item => item is InkStrokeObject));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _page is null) return;
        var removed = (_selectedObjects.Count > 0 ? _selectedObjects :
            _selectedObject is null ? [] : [_selectedObject]).Where(item => !item.IsLocked).ToArray();
        if (removed.Length == 0) return;
        _history.Execute(new ReplaceObjectsCommand(_page.Id, removed, [], "Delete selection"), _document);
        _selectedObject = null;
        _selectedObjects.Clear();
        OnDocumentChanged(recognizeInk: removed.Any(item => item is InkStrokeObject));
    }

    private async void OnAddImageClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _page is null || _assetStore is null || App.MainAppWindow is null) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
                picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            await using var input = File.OpenRead(file.Path);
            var assetHash = await _assetStore.AddAsync(input, Path.GetExtension(file.Path));
            await AddImageAssetAsync(assetHash, file.Name);
            StatusText.Text = $"Added {file.Name}";
        }
        catch (Exception exception)
        {
            ShowError("The image could not be added.", exception);
        }
    }

    private async Task AddImageAssetAsync(string assetHash, string displayName)
    {
        if (_document is null || _page is null) return;
        var loaded = await LoadDownsampledBitmapAsync(assetHash, _page.Size.Width * 0.72,
            _page.Size.Height * 0.72);
        var bitmap = loaded.Bitmap;
        CacheImageBitmap(assetHash, bitmap);
        var fit = Math.Min(1d, Math.Min(_page.Size.Width * 0.72 / loaded.SourceWidth,
            _page.Size.Height * 0.72 / loaded.SourceHeight));
        var width = loaded.SourceWidth * fit;
        var height = loaded.SourceHeight * fit;
        var image = new ImageObject
        {
            AssetHash = assetHash,
            AltText = Path.GetFileNameWithoutExtension(displayName),
            Bounds = new RectD((_page.Size.Width - width) / 2, (_page.Size.Height - height) / 2, width, height),
            ZIndex = NextZIndex(),
            PreserveAspectRatio = true
        };
        _history.Execute(new AddObjectCommand(_page.Id, image), _document);
        _selectedObject = image;
        _selectedObjects.Clear();
        _selectedObjects.Add(image);
        OnDocumentChanged(recognizeInk: false, appendedObject: image);
        ActivateTool(EditorTool.Select);
    }

    private void OnSelectionLockClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _page is null || _selectedObject is not ImageObject image) return;
        var updated = image with { IsLocked = !image.IsLocked };
        _history.Execute(new ReplaceObjectsCommand(_page.Id, [image], [updated],
            updated.IsLocked ? "Lock image" : "Unlock image"), _document);
        _selectedObject = updated;
        _selectedObjects.Clear();
        _selectedObjects.Add(updated);
        OnDocumentChanged(recognizeInk: false);
    }

    private IReadOnlyList<CanvasObject> SelectedCanvasObjects() => _selectedObjects.Count > 0
        ? _selectedObjects
        : _selectedObject is null ? [] : [_selectedObject];

    private void CopySelectionToClipboard()
    {
        var selected = SelectedCanvasObjects();
        if (selected.Count == 0) return;
        _internalClipboard = JsonSerializer.Serialize(selected, HoomNoteJson.Options);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetData(CanvasClipboardFormat, _internalClipboard);
        Clipboard.SetContent(package);
        StatusText.Text = $"Copied {selected.Count} object(s)";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelectionToClipboard();

    private void OnCutClick(object sender, RoutedEventArgs e)
    {
        if (SelectedCanvasObjects().Count == 0) return;
        CopySelectionToClipboard();
        OnDeleteClick(sender, e);
    }

    private async void OnPasteClick(object sender, RoutedEventArgs e) => await PasteSelectionAsync();

    private async Task PasteSelectionAsync()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastPasteTimestamp != 0 &&
            (now - _lastPasteTimestamp) * 1000d / System.Diagnostics.Stopwatch.Frequency < 300) return;
        if (!await _pasteGate.WaitAsync(0)) return;
        _lastPasteTimestamp = now;
        try
        {
            await PasteSelectionCoreAsync();
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    private async Task PasteSelectionCoreAsync()
    {
        if (_document is null || _page is null || _assetStore is null) return;
        string? json = null;
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(CanvasClipboardFormat) && await view.GetDataAsync(CanvasClipboardFormat) is string clipboardJson)
                json = clipboardJson;
            else if (await TryPasteImageAsync(view)) return;
        }
        catch
        {
            // The in-process copy remains available if another app owns a delayed clipboard item.
            json = _internalClipboard;
        }
        if (string.IsNullOrWhiteSpace(json)) return;
        List<CanvasObject>? source;
        try
        {
            source = JsonSerializer.Deserialize<List<CanvasObject>>(json, HoomNoteJson.Options);
        }
        catch (JsonException)
        {
            return;
        }
        if (source is null || source.Count == 0) return;

        var idMap = source.ToDictionary(item => item.Id, _ => Guid.NewGuid());
        var zIndex = NextZIndex();
        var pasted = source.Select((item, index) =>
        {
            CanvasObject clone = item with
            {
                Id = idMap[item.Id],
                IsLocked = false,
                Transform = item.Transform.Then(Transform2D.Translation(24, 24)),
                ZIndex = zIndex + index
            };
            return clone is GroupObject group
                ? group with { ChildIds = group.ChildIds.Select(id => idMap.GetValueOrDefault(id, id)).ToList() }
                : clone;
        }).ToArray();
        foreach (var item in pasted) _history.Execute(new AddObjectCommand(_page.Id, item), _document);
        _selectedObjects.Clear();
        _selectedObjects.AddRange(pasted);
        _selectedObject = pasted.Length == 1 ? pasted[0] : null;
        OnDocumentChanged(recognizeInk: pasted.Any(item => item is InkStrokeObject));
        StatusText.Text = $"Pasted {pasted.Length} object(s)";
    }

    private async Task<bool> TryPasteImageAsync(DataPackageView view)
    {
        if (_assetStore is null) return false;
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            var files = await view.GetStorageItemsAsync();
            var file = files.OfType<StorageFile>().FirstOrDefault(item =>
                new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }
                    .Contains(Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase));
            if (file is not null)
            {
                await using var input = File.OpenRead(file.Path);
                var assetHash = await _assetStore.AddAsync(input, Path.GetExtension(file.Name));
                await AddImageAssetAsync(assetHash, file.Name);
                StatusText.Text = $"Pasted {file.Name}";
                return true;
            }
        }
        if (!view.Contains(StandardDataFormats.Bitmap)) return false;
        var reference = await view.GetBitmapAsync();
        using var randomAccess = await reference.OpenReadAsync();
        await using var inputStream = randomAccess.AsStreamForRead();
        var hash = await _assetStore.AddAsync(inputStream, ".png");
        await AddImageAssetAsync(hash, "Pasted image.png");
        StatusText.Text = "Pasted image";
        return true;
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _importService is null || App.MainAppWindow is null) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));
            picker.FileTypeFilter.Add(".pdf");
            picker.FileTypeFilter.Add(".ppt");
            picker.FileTypeFilter.Add(".pptx");
            picker.FileTypeFilter.Add(".sdocx");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            StatusText.Text = "Importing document…";
            var request = await ShowImportOptionsAsync(file.Path);
            if (request is null) return;
            var result = await _importService.ImportAsync(request);
            if (request.ReplaceCurrentPages)
            {
                _document.Pages.Clear();
                _document.Sections.FirstOrDefault()?.PageIds.Clear();
                _pages.Clear();
            }
            foreach (var page in result.Pages)
            {
                _document.Pages.Add(page);
                _document.Sections.FirstOrDefault()?.PageIds.Add(page.Id);
                _pages.Add(page);
            }
            if (result.Pages.Count > 0)
            {
                PageList.SelectedItem = result.Pages[0];
                SelectPage(result.Pages[0]);
                BeginPdfPreviewLoad();
                DrawingSurface.Invalidate();
            }
            ImportInfo.Title = $"Imported {result.DisplayName}";
            ImportInfo.Message = result.Warnings.Count == 0
                ? $"{result.Pages.Count} page(s) are ready for annotation."
                : string.Join(" ", result.Warnings);
            ImportInfo.Severity = result.Warnings.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            ImportInfo.IsOpen = true;
            OnDocumentChanged(recognizeInk: false);
            await SaveNowAsync();
            ScheduleDocumentHandwritingIndex(_document);
        }
        catch (Exception exception) { ShowError("Import failed.", exception); }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_document is null || _packageService is null || _vectorExportService is null || App.MainAppWindow is null) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = SanitizeFileName(_document.Title) };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow));
            picker.FileTypeChoices.Add("HoomNote package", [".hoomnote"]);
            picker.FileTypeChoices.Add("Vector PDF", [".pdf"]);
            picker.FileTypeChoices.Add("Scalable vector graphic", [".svg"]);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await SaveNowAsync();
            ExportResult? result = null;
            switch (Path.GetExtension(file.Path).ToLowerInvariant())
            {
                case ".pdf":
                    result = await _vectorExportService.ExportAsync(_document, file.Path, VectorExportFormat.Pdf);
                    break;
                case ".svg":
                    result = await _vectorExportService.ExportAsync(_document, file.Path, VectorExportFormat.Svg);
                    break;
                default:
                    await _packageService.ExportAsync(_document, file.Path);
                    break;
            }
            if (result is { Warnings.Count: > 0 })
            {
                ImportInfo.Title = "Export completed with warnings";
                ImportInfo.Message = string.Join(" ", result.Warnings);
                ImportInfo.Severity = InfoBarSeverity.Warning;
                ImportInfo.IsOpen = true;
            }
            StatusText.Text = $"Exported {file.Name}";
        }
        catch (Exception exception) { ShowError("Export failed.", exception); }
    }

    private async Task<ImportRequest?> ShowImportOptionsAsync(string sourcePath)
    {
        var range = new TextBox { Header = "Pages", PlaceholderText = "All, or 1-3, 7" };
        var rotation = new ComboBox { Header = "Rotate pages", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var degrees in new[] { 0, 90, 180, 270 }) rotation.Items.Add(new ComboBoxItem { Content = $"{degrees}°", Tag = degrees });
        rotation.SelectedIndex = 0;
        var margin = new NumberBox { Header = "Page margin", Minimum = 0, Maximum = 200, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var replace = new CheckBox { Content = "Replace current notebook pages" };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = Path.GetFileName(sourcePath), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(range);
        content.Children.Add(rotation);
        content.Children.Add(margin);
        content.Children.Add(replace);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Import document",
            Content = content,
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var indexes = ParsePageRange(range.Text);
        return new ImportRequest(sourcePath, indexes, replace.IsChecked == true, margin.Value,
            rotation.SelectedItem is ComboBoxItem { Tag: int rotationDegrees } ? rotationDegrees : 0);
    }

    private static IReadOnlyList<int>? ParsePageRange(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        var results = new SortedSet<int>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], out var start) || start < 1) continue;
            var end = bounds.Length == 2 && int.TryParse(bounds[1], out var parsedEnd) ? parsedEnd : start;
            for (var page = start; page <= Math.Min(end, start + 10_000); page++) results.Add(page - 1);
        }
        return results.Count == 0 ? null : results.ToArray();
    }

    private void OnLayerVisibilityClick(object sender, RoutedEventArgs e)
    {
        if (_page?.ImportedLayer is not { } layer) return;
        _page.ImportedLayer = layer with { IsVisible = !layer.IsVisible };
        UpdateLayerUi();
        OnDocumentChanged(recognizeInk: false);
    }

    private void OnLayerLockClick(object sender, RoutedEventArgs e)
    {
        if (_page?.ImportedLayer is not { } layer) return;
        _page.ImportedLayer = layer with { IsLocked = !layer.IsLocked };
        UpdateLayerUi();
        OnDocumentChanged(recognizeInk: false);
    }

    private void OnLayerRotateClick(object sender, RoutedEventArgs e)
    {
        if (_page?.ImportedLayer is not { } layer) return;
        _page.ImportedLayer = layer with
        {
            Transform = layer.Transform.Then(Transform2D.Rotation(Math.PI / 2, _page.Size is var size
                ? new PointD(size.Width / 2, size.Height / 2) : new PointD(408, 528)))
        };
        OnDocumentChanged(recognizeInk: false);
    }

    private void OnLayerResetClick(object sender, RoutedEventArgs e)
    {
        if (_page?.ImportedLayer is not { } layer) return;
        _page.ImportedLayer = layer with { Transform = Transform2D.Identity, IsLocked = true, IsVisible = true };
        UpdateLayerUi();
        OnDocumentChanged(recognizeInk: false);
    }

    private void UpdateLayerUi()
    {
        var layer = _page?.ImportedLayer;
        var enabled = layer is not null;
        LayerVisibilityButton.IsEnabled = enabled;
        LayerLockButton.IsEnabled = enabled;
        LayerRotateButton.IsEnabled = enabled;
        LayerResetButton.IsEnabled = enabled;
        if (layer is null) return;
        LayerVisibilityButton.Content = layer.IsVisible ? "Hide" : "Show";
        LayerLockButton.Content = layer.IsLocked ? "Unlock" : "Lock";
    }

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || _repository is null) return;
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        var query = sender.Text.Trim();
        if (query.Length == 0)
        {
            _searchResults.Clear();
            SearchHeading.Visibility = Visibility.Collapsed;
            SearchResultsList.Visibility = Visibility.Collapsed;
            return;
        }
        try
        {
            await Task.Delay(140, _searchDebounce.Token);
            var searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            DiagnosticsLog.Info("search.started", ("query_length", query.Length));
            IReadOnlyList<SearchResult> indexedResults;
            try
            {
                indexedResults = await _repository.SearchAsync(query, _searchDebounce.Token);
            }
            catch (Exception exception) when (!_searchDebounce.IsCancellationRequested)
            {
                DiagnosticsLog.Error("search.repository_failed", exception, ("query_length", query.Length));
                indexedResults = [];
            }
            var results = indexedResults.Concat(BuildFuzzySearchResults(query))
                .GroupBy(item => (item.DocumentId, item.PageId))
                .Select(group => group.OrderBy(item => item.Source is "fuzzy title" or "fuzzy page title" or "live text" ? 1 : 0)
                    .First())
                .Select(result => (Result: result, Score: SearchResultRelevance(query, result)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Result.Source is not "fuzzy title" and not "fuzzy page title")
                .Take(40)
                .Select(item => item.Result)
                .ToArray();
            _searchResults.Clear();
            foreach (var result in results) _searchResults.Add(result);
            SearchHeading.Text = results.Length == 0 ? "No matches" : $"Search results  {results.Length}";
            SearchHeading.Visibility = Visibility.Visible;
            SearchResultsList.Visibility = Visibility.Visible;
            DiagnosticsLog.Info("search.completed", ("query_length", query.Length),
                ("indexed_results", indexedResults.Count), ("shown_results", results.Length),
                ("elapsed_ms", Math.Round(MillisecondsSince(searchStarted), 1)));
        }
        catch (OperationCanceledException) { }
    }

    private IEnumerable<SearchResult> BuildFuzzySearchResults(string query)
    {
        foreach (var summary in _allDocuments
                     .Select(item => (Item: item, Score: FuzzyScore(query, item.Title)))
                     .Where(item => item.Score > 0)
                     .OrderByDescending(item => item.Score))
        {
            yield return new SearchResult(summary.Item.Id, null, summary.Item.Title, "Notebook",
                "Notebook title", "fuzzy title");
        }

        foreach (var document in _openDocumentCache.Values)
        foreach (var page in document.Pages)
        {
            var typed = string.Join(' ', page.Objects.OfType<RichTextObject>().Select(item => item.Content.PlainText));
            var searchableBody = string.Join(' ', new[] { typed, page.RecognizedText }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var titleScore = FuzzyScore(query, page.Title);
            var bodyScore = FuzzyScore(query, searchableBody);
            if (titleScore <= 0 && bodyScore <= 0) continue;
            yield return new SearchResult(document.Id, page.Id, document.Title, page.Title,
                bodyScore > 0 ? FuzzySearchSnippet(searchableBody, query) : "Page title",
                bodyScore > 0 ? "live text" : "fuzzy page title");
        }
    }

    private static int SearchResultRelevance(string query, SearchResult result)
    {
        var tokens = NormalizeSearchText(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return 0;
        var candidate = string.Join(' ', result.DocumentTitle, result.PageTitle, result.Snippet);
        var tokenScores = tokens.Select(token => FuzzyScore(token, candidate)).ToArray();
        if (tokenScores.Any(score => score <= 0)) return 0;
        var titleBonus = FuzzyScore(query, $"{result.DocumentTitle} {result.PageTitle}") > 0 ? 350 : 0;
        var indexedBonus = result.Source is "fuzzy title" or "fuzzy page title" or "live text" ? 0 : 120;
        return tokenScores.Sum() + titleBonus + indexedBonus;
    }

    private static string FuzzySearchSnippet(string text, string query)
    {
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return SearchSnippet(text, query);
        var best = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => (Line: line, Score: FuzzyScore(query, line)))
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        var candidate = best.Score > 0 ? best.Line : text;
        return candidate.Length <= 140 ? candidate : $"{candidate[..137]}…";
    }

    private static string SearchSnippet(string text, string query)
    {
        var match = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (match < 0) return text.Length <= 140 ? text : $"{text[..137]}…";
        var start = Math.Max(0, match - 52);
        var length = Math.Min(text.Length - start, 140);
        var snippet = text.Substring(start, length).Trim();
        return $"{(start > 0 ? "…" : string.Empty)}{snippet}{(start + length < text.Length ? "…" : string.Empty)}";
    }

    private static int FuzzyScore(string query, string candidate)
    {
        var needle = NormalizeSearchText(query);
        var haystack = NormalizeSearchText(candidate);
        if (needle.Length == 0 || haystack.Length == 0) return 0;
        if (haystack == needle) return 1_000;
        var containsAt = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (containsAt >= 0) return 800 - containsAt;
        var bestDistance = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => EditDistance(needle.Replace(" ", string.Empty, StringComparison.Ordinal), word))
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        var allowed = Math.Max(1, Math.Min(3, needle.Length / 4));
        if (bestDistance <= allowed) return 600 - bestDistance * 40;
        return 0;
    }

    private static string NormalizeSearchText(string value) => string.Join(' ', value.ToLowerInvariant()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
        .Where(token => token.Length > 0));

    private static int EditDistance(string left, string right)
    {
        if (left.Length == 0) return right.Length;
        if (right.Length == 0) return left.Length;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    private async void OnSearchResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResult result) return;
        var query = SearchBox.Text.Trim();
        DiagnosticsLog.Info("search.result_clicked", ("query_length", query.Length),
            ("source", result.Source), ("has_page", result.PageId is not null));
        _searchLocateCancellation?.Cancel();
        _pendingSearchFlashPageId = result.PageId;
        _pendingSearchFlashQuery = query;
        await LoadDocumentAsync(result.DocumentId, result.PageId);
        SelectLibraryDocument(result.DocumentId);
        // PageList can deliver its SelectionChanged callback after document navigation. Resolve
        // and draw the match on the next dispatcher turn so that late selection cleanup cannot
        // immediately erase the highlight.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            ProcessPendingSearchFlash);
    }

    private async void ProcessPendingSearchFlash()
    {
        var pageId = _pendingSearchFlashPageId;
        var query = _pendingSearchFlashQuery;
        if (pageId is null || _page?.Id != pageId || string.IsNullOrWhiteSpace(query)) return;
        _pendingSearchFlashPageId = null;
        _pendingSearchFlashQuery = null;
        if (BeginSearchFlash(query)) return;
        PauseBackgroundRecognition();
        var cancellation = _searchLocateCancellation = new CancellationTokenSource();
        try
        {
            await LocateAndFlashSearchMatchAsync(query, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not locate search match: {exception.Message}";
        }
        finally
        {
            ResumeBackgroundRecognition();
        }
    }

    private bool BeginSearchFlash(string query)
    {
        if (_page is null || string.IsNullOrWhiteSpace(query)) return false;
        var matches = FindTypedMatchBounds(query);
        if (matches.Count == 0)
            matches = FindRecognizedMatchBounds(query, _page.RecognizedRegions);
        if (matches.Count == 0) return false;
        _searchFlashBounds.Clear();
        var pagePadding = Math.Max(3, 6 / Math.Max(_zoom, 0.08));
        _searchFlashBounds.AddRange(matches.Select(bounds => bounds.Inflate(pagePadding)));
        // Start the lifetime from the first frame that can actually display it. Building a dense
        // page texture may take longer than the flash itself on low-end hardware; starting here
        // allowed the highlight to expire before it was ever presented.
        _searchFlashStarted = 0;
        DrawingSurface.Invalidate();
        return true;
    }

    private IReadOnlyList<RectD> FindTypedMatchBounds(string query)
    {
        if (_page is null) return [];
        foreach (var text in _page.Objects.OfType<RichTextObject>()
                     .Where(item => !item.IsHidden)
                     .OrderByDescending(item => FuzzyScore(query, item.Content.PlainText)))
        {
            var content = text.Content.PlainText;
            var span = FindBestTextMatchSpan(content, query);
            if (span is null) continue;
            using var format = CreateTextFormat(text);
            using var layout = new CanvasTextLayout(DrawingSurface.Device, content, format,
                (float)Math.Max(1, text.Bounds.Width), (float)Math.Max(1, text.Bounds.Height));
            var bounds = layout.GetCharacterRegions(span.Value.Start, span.Value.Length)
                .Select(region => region.LayoutBounds)
                .Where(region => region.Width > 0 && region.Height > 0)
                .Select(region => TransformBounds(new RectD(
                    text.Bounds.X + region.X, text.Bounds.Y + region.Y,
                    region.Width, region.Height), text.Transform))
                .ToArray();
            if (bounds.Length > 0) return bounds;
        }
        return [];
    }

    private static (int Start, int Length)? FindBestTextMatchSpan(string text, string query)
    {
        var exact = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0) return (exact, query.Length);
        var bestStart = -1;
        var bestLength = 0;
        var bestScore = 0;
        for (var index = 0; index < text.Length;)
        {
            while (index < text.Length && !char.IsLetterOrDigit(text[index])) index++;
            var start = index;
            while (index < text.Length && char.IsLetterOrDigit(text[index])) index++;
            if (index <= start) continue;
            var score = FuzzyScore(query, text[start..index]);
            if (score <= bestScore) continue;
            bestScore = score;
            bestStart = start;
            bestLength = index - start;
        }
        return bestStart >= 0 && bestScore > 0 ? (bestStart, bestLength) : null;
    }

    private static IReadOnlyList<RectD> FindRecognizedMatchBounds(string query,
        IReadOnlyList<RecognizedTextRegion> regions)
    {
        var tokens = NormalizeSearchText(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || regions.Count == 0) return [];
        if (tokens.Length == 1)
        {
            var best = regions.Select(region => (Region: region,
                    Score: RecognizedTokenScore(tokens[0], region.Text)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            return best.Region is null ? [] : [best.Region.Bounds];
        }

        for (var start = 0; start < regions.Count; start++)
        {
            if (RecognizedTokenScore(tokens[0], regions[start].Text) <= 0) continue;
            var matches = new List<RectD> { regions[start].Bounds };
            var previous = start;
            for (var tokenIndex = 1; tokenIndex < tokens.Length; tokenIndex++)
            {
                var next = -1;
                var upper = Math.Min(regions.Count, previous + 9);
                for (var candidate = previous + 1; candidate < upper; candidate++)
                {
                    var sameLine = Math.Abs(regions[candidate].Bounds.Center.Y - matches[0].Center.Y) <=
                                   Math.Max(10, Math.Max(regions[candidate].Bounds.Height,
                                       matches[0].Height) * 0.9);
                    if (sameLine && RecognizedTokenScore(tokens[tokenIndex], regions[candidate].Text) > 0)
                    {
                        next = candidate;
                        break;
                    }
                }
                if (next < 0) break;
                previous = next;
                matches.Add(regions[next].Bounds);
            }
            if (matches.Count == tokens.Length) return matches;
        }
        return [];
    }

    private static int RecognizedTokenScore(string token, string candidate)
    {
        var normalized = NormalizeSearchText(candidate);
        if (normalized == token) return 1_000;
        if (normalized.Contains(token, StringComparison.Ordinal)) return 800;
        return token.Length >= 4 ? FuzzyScore(token, normalized) : 0;
    }

    private static RectD TransformBounds(RectD bounds, Transform2D transform)
    {
        return RectD.FromPoints([
            transform.Apply(new PointD(bounds.Left, bounds.Top)),
            transform.Apply(new PointD(bounds.Right, bounds.Top)),
            transform.Apply(new PointD(bounds.Right, bounds.Bottom)),
            transform.Apply(new PointD(bounds.Left, bounds.Bottom))
        ]);
    }

    private async Task LocateAndFlashSearchMatchAsync(string query, CancellationToken cancellationToken)
    {
        var page = _page;
        var document = _document;
        if (page is null || document is null) return;
        StatusText.Text = "Locating search term on page…";
        var snapshot = page.Objects.ToArray();
        var strokes = snapshot.OfType<InkStrokeObject>()
            .Where(stroke => !stroke.IsHidden && stroke.Style.Tool != InkToolKind.Highlighter &&
                             stroke.Points.Count > 1)
            .ToArray();
        var additions = new List<RecognizedTextRegion>();
        var recognizedParts = new List<string>();
        if (_recognizer is not null && strokes.Length > 0)
        {
            var batches = await Task.Run(() => CreateSpatialRecognitionBatches(strokes), cancellationToken);
            foreach (var batch in batches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await _recognizer.RecognizeAsync(batch,
                    document.Settings.RecognitionLanguage, cancellationToken);
                AddUniqueRecognizedText(recognizedParts, result.Text);
                additions.AddRange(result.Regions);
                if (FindRecognizedMatchBounds(query, additions).Count > 0) break;
            }
        }

        if (FindRecognizedMatchBounds(query, additions).Count == 0 && _pageOcr is not null &&
            (page.ImportedLayer is not null || snapshot.OfType<ImageObject>().Any()))
        {
            var images = snapshot.OfType<ImageObject>()
                .Where(image => !image.IsHidden && !string.IsNullOrWhiteSpace(image.AssetHash))
                .ToArray();
            var ocr = await Task.Run(
                async () => await _pageOcr.RecognizePageAsync(page, images, [],
                    document.Settings.RecognitionLanguage, cancellationToken), cancellationToken);
            AddUniqueRecognizedText(recognizedParts, ocr.Text);
            additions.AddRange(ocr.Regions);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var mergedRegions = MergeRecognizedRegions(page.RecognizedRegions, additions);
        var recognizedText = page.RecognizedText;
        foreach (var part in recognizedParts)
            if (!recognizedText.Contains(part, StringComparison.OrdinalIgnoreCase))
                recognizedText = string.Join(Environment.NewLine,
                    new[] { recognizedText, part }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (additions.Count > 0)
            await PersistRecognizedTextAsync(document, page, recognizedText, mergedRegions, cancellationToken);
        if (_page?.Id != page.Id) return;
        if (BeginSearchFlash(query))
            StatusText.Text = "Search match located";
        else
            StatusText.Text = "The page is indexed, but this match could not be located precisely";
    }

    private bool AdvanceSearchFlash()
    {
        if (_searchFlashBounds.Count == 0 || _searchFlashStarted == 0) return false;
        if (MillisecondsSince(_searchFlashStarted) < SearchFlashDurationMs) return true;
        _searchFlashBounds.Clear();
        _searchFlashStarted = 0;
        StopViewportFramePumpIfIdle();
        return true;
    }

    private void EnsureViewportFramePump()
    {
        if (_viewportFramePumpActive) return;
        CompositionTarget.Rendering += OnViewportFrame;
        _viewportFramePumpActive = true;
    }

    private void StopViewportFramePump()
    {
        if (!_viewportFramePumpActive) return;
        CompositionTarget.Rendering -= OnViewportFrame;
        _viewportFramePumpActive = false;
    }

    private void StopViewportFramePumpIfIdle()
    {
        if (_touchInertiaActive || _wheelZoomAnimating ||
            (_searchFlashBounds.Count > 0 && _searchFlashStarted != 0))
        {
            return;
        }
        StopViewportFramePump();
    }

    private void OnViewportFrame(object? sender, object args)
    {
        _ = sender;
        _ = args;
        var redraw = false;
        if (_touchInertiaActive) redraw |= AdvanceTouchInertia();
        if (_wheelZoomAnimating) redraw |= AdvanceWheelZoom();
        if (_searchFlashBounds.Count > 0 && _searchFlashStarted != 0)
            redraw |= AdvanceSearchFlash();
        if (redraw) DrawingSurface.Invalidate();
        StopViewportFramePumpIfIdle();
    }

    private void DrawSearchFlash(CanvasDrawingSession drawingSession)
    {
        if (_searchFlashBounds.Count == 0) return;
        if (_searchFlashStarted == 0)
        {
            _searchFlashStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureViewportFramePump();
        }
        var elapsed = MillisecondsSince(_searchFlashStarted);
        var opacity = Math.Clamp(1d - elapsed / SearchFlashDurationMs, 0, 1);
        if (opacity <= 0) return;
        var fill = Color.FromArgb((byte)(125 * opacity), 255, 205, 64);
        var outline = Color.FromArgb((byte)(230 * opacity), 255, 219, 92);
        var outlineWidth = (float)Math.Max(1.5, 2.25 / Math.Max(_zoom, 0.08));
        foreach (var bounds in _searchFlashBounds)
        {
            drawingSession.FillRoundedRectangle((float)bounds.X, (float)bounds.Y,
                (float)bounds.Width, (float)bounds.Height, 4, 4, fill);
            drawingSession.DrawRoundedRectangle((float)bounds.X, (float)bounds.Y,
                (float)bounds.Width, (float)bounds.Height, 4, 4, outline, outlineWidth);
        }
    }

    private void PauseBackgroundRecognition()
    {
        _recognitionTimer.Stop();
        _incrementalRecognitionCancellation?.Cancel();
    }

    private void ResumeBackgroundRecognition()
    {
        if (_isPointerDown) return;
        if (_pendingRecognitionStrokes.Count > 0)
        {
            _recognitionTimer.Stop();
            _recognitionTimer.Start();
        }
        if (_document is not null &&
            _document.Pages.Any(page => !_pageOcrIndexedThisSession.Contains(page.Id)))
            ScheduleDocumentHandwritingIndex(_document);
    }

    private InkStyle CurrentInkStyle()
    {
        return _gestureTool switch
        {
            EditorTool.Highlighter => new InkStyle
            {
                Tool = InkToolKind.Highlighter,
                Color = _highlighterColor,
                Width = Math.Max(12, (float)StrokeWidthSlider.Value * 3),
                Opacity = _presetOpacity ?? 0.34f,
                PressureEnabled = false,
                PressureSensitivity = 0,
                Smoothing = _presetSmoothing ?? 0.8f
            },
            _ => new InkStyle
            {
                Tool = InkToolKind.Pen, Color = _penColor, Width = (float)StrokeWidthSlider.Value,
                Opacity = _presetOpacity ?? 1f, PressureEnabled = false, PressureSensitivity = 0,
                Smoothing = _presetSmoothing ?? 0.9f
            }
        };
    }

    private ShapeKind SelectedShapeKind() =>
        ShapePicker.SelectedItem is ComboBoxItem { Tag: string value } &&
        Enum.TryParse<ShapeKind>(value, out var shape) ? shape : ShapeKind.Rectangle;

    private static bool IsCornerHandle(TransformHandle handle) => handle is TransformHandle.TopLeft or
        TransformHandle.TopRight or TransformHandle.BottomRight or TransformHandle.BottomLeft;

    private static bool IsShiftDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;

    private static bool IsControlDown() =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;

    private int NextZIndex() => _page?.Objects.Count switch
    {
        null or 0 => 0,
        _ => _page.Objects[^1].ZIndex + 1
    };

    private RectD CombinedSelectionBounds()
    {
        var objects = _selectedObjects.Select(item =>
            _multiTransformPreviews.TryGetValue(item.Id, out var preview) ? preview : item);
        return CombinedBounds(objects);
    }

    private static RectD CombinedBounds(IEnumerable<CanvasObject> objects)
    {
        var bounds = objects.Select(StrokeGeometry.GetWorldBounds).ToArray();
        if (bounds.Length == 0) return default;
        return new RectD(bounds.Min(item => item.Left), bounds.Min(item => item.Top),
            bounds.Max(item => item.Right) - bounds.Min(item => item.Left),
            bounds.Max(item => item.Bottom) - bounds.Min(item => item.Top));
    }

    private void SetInkColor(string color, bool rememberForTool = true)
    {
        _inkColor = color.ToUpperInvariant();
        if (rememberForTool)
        {
            if (_colorTool == EditorTool.Highlighter) _highlighterColor = _inkColor;
            else _penColor = _inkColor;
            ScheduleUserPreferencesSave();
        }
        var parsed = ParseColor(_inkColor);
        if (InkColorPicker.Color != parsed)
        {
            _syncingInkColor = true;
            InkColorPicker.Color = parsed;
            _syncingInkColor = false;
        }
        InkColorSwatch.Background = new SolidColorBrush(parsed);
    }

    private void LoadSavedColorPalette()
    {
        _savedColors.Clear();
        foreach (var color in _userPreferences.SavedInkColors
                     .Select(color => color.ToUpperInvariant())
                     .Where(IsValidHexColor)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(24))
            _savedColors.Add(new SavedColorItem(color));
    }

    private async Task SaveUserPreferencesAsync()
    {
        _userPreferences = _userPreferences with
        {
            SavedInkColors = _savedColors.Select(item => item.Hex).ToList(),
            PenColor = _penColor,
            HighlighterColor = _highlighterColor,
            HighlighterStraightLine = HighlighterStraightToggle.IsOn
        };
        await PersistUserPreferencesAsync("Saved ink palette");
    }

    private void ScheduleUserPreferencesSave()
    {
        if (_userSettingsStore is null) return;
        _settingsSaveDebounce?.Cancel();
        _settingsSaveDebounce = new CancellationTokenSource();
        _ = SaveUserPreferencesAfterPauseAsync(_settingsSaveDebounce.Token);
    }

    private async Task SaveUserPreferencesAfterPauseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(300, cancellationToken);
            await SaveUserPreferencesAsync();
        }
        catch (OperationCanceledException) { }
    }

    private async Task PersistUserPreferencesAsync(string status)
    {
        if (_userSettingsStore is null) return;
        await _settingsSaveGate.WaitAsync();
        try
        {
            await _userSettingsStore.SaveAsync(_userPreferences);
            StatusText.Text = status;
        }
        catch (Exception exception)
        {
            ShowError("HoomNote settings could not be saved.", exception);
        }
        finally { _settingsSaveGate.Release(); }
    }

    private void SyncPageCollection(Guid? preferredPageId)
    {
        if (_document is null) return;
        var selected = _document.Pages.FirstOrDefault(page => page.Id == preferredPageId)
                       ?? _document.Pages.FirstOrDefault();
        _loading = true;
        _pages.Clear();
        foreach (var page in _document.Pages) _pages.Add(page);
        PageList.SelectedItem = selected;
        _loading = false;
        SelectPage(selected);
    }

    private void ClearLiveInkGeometryCache()
    {
        foreach (var geometry in _liveInkGeometryChunks) geometry.Dispose();
        _liveInkGeometryChunks.Clear();
        _liveInkChunkStart = 0;
    }

    private void ClearStrokeGeometryCache()
    {
        foreach (var entry in _strokeGeometryCache.Values) entry.Geometry.Dispose();
        _strokeGeometryCache.Clear();
        _strokeGeometryLru.Clear();
        _strokeGeometryLruNodes.Clear();
        _strokeGeometryCachedPoints = 0;
    }

    private void ClearImageBitmapCache()
    {
        _imageLoadGeneration++;
        foreach (var bitmap in _imageBitmapCache.Values) bitmap.Dispose();
        _imageBitmapCache.Clear();
        _imageBitmapSizes.Clear();
        _imageBitmapBytes = 0;
        _imageBitmapLru.Clear();
        _pendingImageLoads.Clear();
        _failedImageLoads.Clear();
        _imageWaitingPages.Clear();
        _imagePagesNeedingRefresh.Clear();
    }

    private void CacheImageBitmap(string assetHash, CanvasBitmap bitmap)
    {
        if (_imageBitmapCache.Remove(assetHash, out var existing))
        {
            _imageBitmapBytes -= _imageBitmapSizes.GetValueOrDefault(assetHash);
            existing.Dispose();
        }
        _imageBitmapLru.Remove(assetHash);
        _imageBitmapSizes.Remove(assetHash);
        _imageBitmapCache[assetHash] = bitmap;
        var byteSize = Math.Max(1L, (long)bitmap.SizeInPixels.Width * bitmap.SizeInPixels.Height * 4L);
        _imageBitmapSizes[assetHash] = byteSize;
        _imageBitmapBytes += byteSize;
        _imageBitmapLru.AddFirst(assetHash);
        while (_imageBitmapBytes > ImageBitmapCacheBudget && _imageBitmapLru.Count > 1)
        {
            var evicted = _imageBitmapLru.Last!.Value;
            _imageBitmapLru.RemoveLast();
            if (_imageBitmapSizes.Remove(evicted, out var evictedBytes)) _imageBitmapBytes -= evictedBytes;
            if (_imageBitmapCache.Remove(evicted, out var evictedBitmap)) evictedBitmap.Dispose();
        }
    }

    private void TouchImageBitmap(string assetHash)
    {
        _imageBitmapLru.Remove(assetHash);
        _imageBitmapLru.AddFirst(assetHash);
    }

    private void CacheOpenDocument(HoomNoteDocument document)
    {
        _openDocumentCache[document.Id] = document;
        _openDocumentPointCounts[document.Id] = CountInkPoints(document);
        _openDocumentLru.Remove(document.Id);
        _openDocumentLru.AddFirst(document.Id);
        while (_openDocumentLru.Count > OpenDocumentCacheLimit ||
               _openDocumentPointCounts.Values.Sum() > OpenDocumentCachePointBudget)
        {
            var node = _openDocumentLru.Last;
            while (node is not null && _document?.Id == node.Value) node = node.Previous;
            if (node is null) break;
            var candidate = node.Value;
            _openDocumentLru.Remove(node);
            _openDocumentCache.Remove(candidate);
            _openDocumentPointCounts.Remove(candidate);
            // Commands retain references to edited strokes. Once a document leaves the bounded
            // hot set, its saved state remains authoritative and the in-memory undo graph must
            // leave with it rather than pinning dense notebooks indefinitely.
            _documentHistories.Remove(candidate);
        }
    }

    private static int CountInkPoints(HoomNoteDocument document)
    {
        long count = 0;
        foreach (var page in document.Pages)
        foreach (var stroke in page.Objects.OfType<InkStrokeObject>())
        {
            count += stroke.Points.Count;
            if (count >= int.MaxValue) return int.MaxValue;
        }
        return (int)count;
    }

    private void RemoveCachedDocument(Guid documentId)
    {
        _openDocumentCache.Remove(documentId);
        _openDocumentPointCounts.Remove(documentId);
        _openDocumentLru.Remove(documentId);
        if (_document?.Id != documentId) _documentHistories.Remove(documentId);
    }

    private void PrepareSpatialIndex(NotePage? page)
    {
        if (_spatialIndexBuildCancellation is { } previousBuild)
        {
            previousBuild.Cancel();
        }
        if (page is null)
        {
            _spatialIndex = new SpatialIndex();
            return;
        }
        if (_pageSpatialIndexCache.TryGetValue(page.Id, out var cached))
        {
            _spatialIndex = cached;
            TouchSpatialIndex(page.Id);
            return;
        }

        // Selection can become available a frame later; rendering and navigation stay instant.
        _spatialIndex = new SpatialIndex();
        var pageId = page.Id;
        var updatedAt = page.UpdatedAt;
        var snapshot = page.Objects.ToArray();
        var cancellation = _spatialIndexBuildCancellation = new CancellationTokenSource();
        _ = Task.Run(() =>
        {
            cancellation.Token.ThrowIfCancellationRequested();
            var index = new SpatialIndex();
            index.Rebuild(snapshot);
            return index;
        }, cancellation.Token).ContinueWith(task => DispatcherQueue.TryEnqueue(() =>
        {
            if (task.IsCanceled || task.IsFaulted || cancellation.IsCancellationRequested) return;
            var currentPage = _document?.Pages.FirstOrDefault(item => item.Id == pageId);
            if (currentPage is null || currentPage.UpdatedAt != updatedAt)
            {
                if (_page?.Id == pageId) PrepareSpatialIndex(currentPage);
                return;
            }
            _pageSpatialIndexCache[pageId] = task.Result;
            TouchSpatialIndex(pageId);
            if (_page?.Id == pageId) _spatialIndex = task.Result;
        }), TaskScheduler.Default);
    }

    private void TouchSpatialIndex(Guid pageId)
    {
        _pageSpatialIndexLru.Remove(pageId);
        _pageSpatialIndexLru.AddFirst(pageId);
        while (_pageSpatialIndexLru.Count > PageSpatialIndexCacheLimit)
        {
            var evicted = _pageSpatialIndexLru.Last!.Value;
            _pageSpatialIndexLru.RemoveLast();
            if (_page?.Id == evicted)
            {
                _pageSpatialIndexLru.AddFirst(evicted);
                continue;
            }
            _pageSpatialIndexCache.Remove(evicted);
        }
    }

    private void StashCurrentPageRenderCache()
    {
        if (_page is not { } page)
        {
            InvalidatePageRenderCache();
            return;
        }
        // Page switching must never synthesize a large snapshot for a page that is already
        // leaving the viewport. Retain an existing snapshot, but let the destination page draw
        // immediately and build its own representation on demand.
        if (_lowZoomPageRaster is null || _lowZoomPageRasterPageId != page.Id)
        {
            InvalidatePageRenderCache();
            return;
        }
        if (_pageRenderOverlayBatches.Count > 0 || _pageRenderOverlays.Count > 0)
            MergeOverlaysIntoLowZoomRaster(page);

        if (_lowZoomPageRaster is null) return;
        var pageId = page.Id;
        if (_warmPageRenderCaches.Remove(pageId, out var previous)) previous.Dispose();
        _warmPageRenderCaches[pageId] = _lowZoomPageRaster;
        _warmPageRenderLru.Remove(pageId);
        _warmPageRenderLru.AddFirst(pageId);
        _lowZoomPageRaster = null;
        _lowZoomPageRasterPageId = null;
        _pageRenderCache?.Dispose();
        _pageRenderCache = null;
        _pageRenderCachePageId = null;
        _pageRenderCacheObjectIds.Clear();
        _pageRenderOverlays.Clear();
        _pageRenderOverlayBatches.Clear();
    }

    private void RestoreWarmPageRenderCache(NotePage? page)
    {
        if (page is not null && _warmPageRenderCaches.Remove(page.Id, out var cache))
        {
            _warmPageRenderLru.Remove(page.Id);
            _lowZoomPageRaster = cache;
            _lowZoomPageRasterPageId = page.Id;
            _pageRenderCache = null;
            _pageRenderCachePageId = page.Id;
            _pageRenderCacheObjectIds.Clear();
            _pageRenderCacheObjectIds.UnionWith(page.Objects.Select(item => item.Id));
        }
        while (_warmPageRenderLru.Count > WarmPageRenderCacheLimit)
        {
            var evicted = _warmPageRenderLru.Last!.Value;
            _warmPageRenderLru.RemoveLast();
            if (_warmPageRenderCaches.Remove(evicted, out var oldCache)) oldCache.Dispose();
        }
    }

    private void ClearWarmPageRenderCaches()
    {
        foreach (var cache in _warmPageRenderCaches.Values) cache.Dispose();
        _warmPageRenderCaches.Clear();
        _warmPageRenderLru.Clear();
    }

    private void RemoveWarmPageRenderCache(Guid pageId)
    {
        _warmPageRenderLru.Remove(pageId);
        if (_warmPageRenderCaches.Remove(pageId, out var cache)) cache.Dispose();
    }

    private void InvalidatePageRenderCache()
    {
        _lowZoomPageRaster?.Dispose();
        _lowZoomPageRaster = null;
        _lowZoomPageRasterPageId = null;
        _pageRenderCache?.Dispose();
        foreach (var batch in _pageRenderOverlayBatches) batch.Dispose();
        _pageRenderOverlayBatches.Clear();
        _pageRenderCache = null;
        _pageRenderCachePageId = null;
        _pageRenderCacheObjectIds.Clear();
        _pageRenderOverlays.Clear();
    }

    private Matrix3x2 PageTransform()
    {
        if (_page is null) return Matrix3x2.Identity;
        var offset = PageOffset();
        return Matrix3x2.CreateScale((float)_zoom) * Matrix3x2.CreateTranslation(offset);
    }

    private Vector2 PageOffset()
    {
        if (_page is null) return _pan;
        return new Vector2(
            (float)((DrawingSurface.ActualWidth - _page.Size.Width * _zoom) / 2d) + _pan.X,
            (float)((DrawingSurface.ActualHeight - _page.Size.Height * _zoom) / 2d) + _pan.Y);
    }

    private PointD ScreenToPage(Point screen)
    {
        if (!Matrix3x2.Invert(PageTransform(), out var inverse)) return default;
        var point = Vector2.Transform(new Vector2((float)screen.X, (float)screen.Y), inverse);
        return new PointD(point.X, point.Y);
    }

    private Point PageToScreen(PointD page)
    {
        var transformed = Vector2.Transform(page.ToVector2(), PageTransform());
        return new Point(transformed.X, transformed.Y);
    }

    private void UpdateSelectionUi()
    {
        SelectionSummary.Text = _selectedObjects.Count > 1
            ? $"{_selectedObjects.Count} objects selected"
            : _selectedObject switch
        {
            InkStrokeObject ink => $"Vector ink • {ink.Points.Count:N0} points",
            RichTextObject text => $"Text box • {text.Content.PlainText.Length:N0} characters",
            ShapeObject shape => $"{shape.Shape} shape",
            ImageObject => "Image",
            GroupObject group => $"Group • {group.ChildIds.Count} objects",
            _ => "Nothing selected"
        };
        var styleSource = _selectedObjects.FirstOrDefault(item => item is InkStrokeObject or ShapeObject)
                          ?? (_selectedObject is InkStrokeObject or ShapeObject ? _selectedObject : null);
        SelectionStylePanel.Visibility = styleSource is null ? Visibility.Collapsed : Visibility.Visible;
        SelectionLockButton.Visibility = _selectedObject is ImageObject ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedObject is ImageObject selectedImage)
            SelectionLockButton.Content = selectedImage.IsLocked ? "Unlock" : "Lock";
        UpdateImageLockOverlay();
        if (styleSource is not null)
        {
            _loading = true;
            var color = styleSource is InkStrokeObject inkStyle ? inkStyle.Style.Color : ((ShapeObject)styleSource).StrokeColor;
            var width = styleSource is InkStrokeObject inkWidth ? inkWidth.Style.Width : ((ShapeObject)styleSource).StrokeWidth;
            SelectionColorPicker.Color = ParseColor(color);
            SelectionWidthSlider.Value = width;
            _loading = false;
        }
        TextFormattingPanel.Visibility = _selectedObject is RichTextObject ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedObject is not RichTextObject richText) return;
        var paragraph = richText.Content.Paragraphs.FirstOrDefault();
        var run = paragraph?.Runs.FirstOrDefault();
        _loading = true;
        ParagraphStylePicker.SelectedItem = ParagraphStylePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, (paragraph?.Kind ?? ParagraphKind.Body).ToString(), StringComparison.Ordinal));
        BoldButton.IsChecked = run?.Bold == true;
        ItalicButton.IsChecked = run?.Italic == true;
        UnderlineButton.IsChecked = run?.Underline == true;
        var textColor = ParseColor(run?.Color ?? DefaultTextColor());
        TextColorPicker.Color = textColor;
        TextColorSwatch.Background = new SolidColorBrush(textColor);
        _loading = false;
    }

    private void OnApplySelectionStyleClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _document is null || _page is null) return;
        var targets = (_selectedObjects.Count > 0 ? _selectedObjects :
            _selectedObject is null ? [] : [_selectedObject])
            .Where(item => item is InkStrokeObject or ShapeObject).ToArray();
        if (targets.Length == 0) return;
        var colorValue = SelectionColorPicker.Color;
        var color = $"#{colorValue.R:X2}{colorValue.G:X2}{colorValue.B:X2}";
        var width = (float)SelectionWidthSlider.Value;
        var updated = targets.Select(item => item switch
        {
            InkStrokeObject ink => ink with
            {
                Style = ink.Style with { Color = color, Width = width, PreserveSourceGeometry = false }
            },
            ShapeObject shape => shape with { StrokeColor = color, StrokeWidth = width },
            _ => item
        }).ToArray();
        _history.Execute(new ReplaceObjectsCommand(_page.Id, targets, updated, "Change selection style"), _document);
        _selectedObjects.Clear();
        _selectedObjects.AddRange(updated);
        _selectedObject = updated.Length == 1 ? updated[0] : null;
        OnDocumentChanged(recognizeInk: updated.Any(item => item is InkStrokeObject));
    }

    private void PickStyleAtPoint(PointD point)
    {
        if (_document is null || _page is null) return;
        var tolerance = 10 / _zoom;
        var target = _spatialIndex.Query(new RectD(point.X - tolerance, point.Y - tolerance,
                tolerance * 2, tolerance * 2))
            .Where(item => !item.IsLocked && item is InkStrokeObject or ShapeObject &&
                           StrokeGeometry.HitTest(item, point, tolerance))
            .OrderByDescending(item => item.ZIndex)
            .FirstOrDefault();
        if (target is null)
        {
            StatusText.Text = "No style-capable object here";
            return;
        }
        (_styleToolColor, _styleToolWidth) = target switch
        {
            InkStrokeObject ink => (ink.Style.Color, ink.Style.Width),
            ShapeObject shape => (shape.StrokeColor, shape.StrokeWidth),
            _ => (_styleToolColor, _styleToolWidth)
        };
        _styleToolPickMode = false;
        UpdateStyleToolUi();
        StatusText.Text = $"Style captured • {_styleToolColor} • {_styleToolWidth:0.#} pt • drag to apply";
    }

    private void ApplyStyleBrushAtPoint(PointD point)
    {
        if (_page is null) return;
        var radius = Math.Max(9, Math.Min(24, _styleToolWidth * 1.35f)) / _zoom;
        var targets = _spatialIndex.Query(new RectD(point.X - radius, point.Y - radius, radius * 2, radius * 2))
            .Where(item => !item.IsLocked && item is InkStrokeObject or ShapeObject &&
                           StrokeGeometry.HitTest(item, point, radius))
            .ToArray();
        foreach (var target in targets)
        {
            if (_styleBrushOriginals.ContainsKey(target.Id)) continue;
            CanvasObject updated = target switch
            {
                InkStrokeObject ink => ink with
                {
                    Style = ink.Style with
                    {
                        Color = _styleToolColor, Width = _styleToolWidth, PreserveSourceGeometry = false
                    }
                },
                ShapeObject shape => shape with
                    { StrokeColor = _styleToolColor, StrokeWidth = _styleToolWidth },
                _ => target
            };
            _styleBrushOriginals[target.Id] = target;
            _multiTransformPreviews[target.Id] = updated;
        }
        if (_styleBrushOriginals.Count > 0)
            StatusText.Text = $"Style brush • {_styleBrushOriginals.Count} object(s)";
    }

    private void CommitStyleBrush()
    {
        if (_document is null || _page is null || _styleBrushOriginals.Count == 0) return;
        var before = _styleBrushOriginals.Values.OrderBy(item => item.ZIndex).ToArray();
        var after = before.Select(item => _multiTransformPreviews[item.Id]).ToArray();
        _history.Execute(new ReplaceObjectsCommand(_page.Id, before, after, "Brush object styles"), _document);
        _selectedObject = null;
        _selectedObjects.Clear();
        OnDocumentChanged(recognizeInk: false);
        StatusText.Text = $"Styled {after.Length} object(s)";
    }

    private void OnTextFormattingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _document is null || _page is null || _selectedObject is not RichTextObject text) return;
        var kind = ParagraphStylePicker.SelectedItem is ComboBoxItem { Tag: string tag } &&
                   Enum.TryParse<ParagraphKind>(tag, out var parsedKind) ? parsedKind : ParagraphKind.Body;
        var content = text.Content with
        {
            Paragraphs = text.Content.Paragraphs.Select(paragraph => paragraph with
            {
                Kind = kind,
                Runs = paragraph.Runs.Select(run => run with
                {
                    Bold = BoldButton.IsChecked == true,
                    Italic = ItalicButton.IsChecked == true,
                    Underline = UnderlineButton.IsChecked == true
                }).ToList()
            }).ToList()
        };
        var updated = text with { Content = content };
        _history.Execute(new ReplaceObjectsCommand(_page.Id, [text], [updated], "Format text"), _document);
        SelectSingleObject(updated);
        OnDocumentChanged(recognizeInk: false);
    }

    private void OnTextColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading || _document is null || _page is null || _selectedObject is not RichTextObject text) return;
        _pendingTextColor = args.NewColor;
        _pendingTextColorObjectId = text.Id;
        TextColorSwatch.Background = new SolidColorBrush(args.NewColor);
    }

    private void OnTextColorFlyoutClosed(object sender, object e)
    {
        if (_pendingTextColor is not { } selectedColor || _pendingTextColorObjectId is not Guid objectId ||
            _document is null || _page is null || _selectedObject is not RichTextObject text || text.Id != objectId)
        {
            _pendingTextColor = null;
            _pendingTextColorObjectId = null;
            return;
        }
        _pendingTextColor = null;
        _pendingTextColorObjectId = null;
        var color = $"#{selectedColor.R:X2}{selectedColor.G:X2}{selectedColor.B:X2}";
        var content = text.Content with
        {
            Paragraphs = text.Content.Paragraphs.Select(paragraph => paragraph with
            {
                Runs = paragraph.Runs.Select(run => run with { Color = color }).ToList()
            }).ToList()
        };
        var updated = text with { Content = content };
        _history.Execute(new ReplaceObjectsCommand(_page.Id, [text], [updated], "Change text color"), _document);
        SelectSingleObject(updated);
        TextColorSwatch.Background = new SolidColorBrush(selectedColor);
        OnDocumentChanged(recognizeInk: false);
    }

    private void UpdateImageLockOverlay()
    {
        if (_readMode || _selectedObject is not ImageObject image || _page is null)
        {
            if (ImageLockOverlayButton.Visibility != Visibility.Collapsed)
                ImageLockOverlayButton.Visibility = Visibility.Collapsed;
            return;
        }
        var bounds = StrokeGeometry.GetWorldBounds(image);
        var anchor = PageToScreen(new PointD(bounds.Right, bounds.Top));
        var left = Math.Clamp(anchor.X + 8, 4, Math.Max(4, DrawingSurface.ActualWidth - 40));
        var top = Math.Clamp(anchor.Y - 18, 4, Math.Max(4, DrawingSurface.ActualHeight - 40));
        if (double.IsNaN(Canvas.GetLeft(ImageLockOverlayButton)) ||
            Math.Abs(Canvas.GetLeft(ImageLockOverlayButton) - left) > 0.25)
            Canvas.SetLeft(ImageLockOverlayButton, left);
        if (double.IsNaN(Canvas.GetTop(ImageLockOverlayButton)) ||
            Math.Abs(Canvas.GetTop(ImageLockOverlayButton) - top) > 0.25)
            Canvas.SetTop(ImageLockOverlayButton, top);
        var glyph = image.IsLocked ? "\uE72E" : "\uE785";
        if (!string.Equals(ImageLockOverlayIcon.Glyph, glyph, StringComparison.Ordinal))
            ImageLockOverlayIcon.Glyph = glyph;
        UpdateTransientToolTip(ImageLockOverlayButton,
            image.IsLocked ? "Image is locked • click to unlock" : "Image is unlocked • click to lock");
        if (ImageLockOverlayButton.Visibility != Visibility.Visible)
            ImageLockOverlayButton.Visibility = Visibility.Visible;
    }

    private void ConfigureTransientToolTips(DependencyObject root)
    {
        if (ToolTipService.GetToolTip(root) is string text) SetTransientToolTip(root, text);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            ConfigureTransientToolTips(VisualTreeHelper.GetChild(root, index));
    }

    private void SetTransientToolTip(DependencyObject target, string content)
    {
        var toolTip = new ToolTip { Content = content };
        toolTip.Opened += OnTransientToolTipOpened;
        toolTip.Closed += OnTransientToolTipClosed;
        ToolTipService.SetToolTip(target, toolTip);
    }

    private void UpdateTransientToolTip(DependencyObject target, string content)
    {
        if (ToolTipService.GetToolTip(target) is ToolTip toolTip)
        {
            if (!string.Equals(toolTip.Content as string, content, StringComparison.Ordinal))
                toolTip.Content = content;
        }
        else
            SetTransientToolTip(target, content);
    }

    private async void OnTransientToolTipOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ToolTip toolTip) return;
        CloseOpenToolTip();
        _openToolTip = toolTip;
        var cancellation = _toolTipCloseCancellation = new CancellationTokenSource();
        try
        {
            await Task.Delay(1_600, cancellation.Token);
            if (ReferenceEquals(_openToolTip, toolTip)) toolTip.IsOpen = false;
        }
        catch (OperationCanceledException) { }
    }

    private void OnTransientToolTipClosed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_openToolTip, sender)) CloseOpenToolTip();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e) => CloseOpenToolTip();

    private void CloseOpenToolTip()
    {
        _toolTipCloseCancellation?.Cancel();
        _toolTipCloseCancellation = null;
        if (_openToolTip is { } toolTip)
        {
            _openToolTip = null;
            toolTip.IsOpen = false;
        }
    }

    private void UpdateZoomText() => ZoomText.Text = $"{_zoom:P0}";

    private void ShowError(string title, Exception exception)
    {
        DiagnosticsLog.Error("ui.error", exception, ("title", title));
        ImportInfo.Title = title;
        ImportInfo.Message = exception.Message;
        ImportInfo.Severity = InfoBarSeverity.Error;
        ImportInfo.IsOpen = true;
        StatusText.Text = title;
    }

    private static RectD NormalizeRect(PointD start, PointD end) => new(
        Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
        Math.Max(1, Math.Abs(end.X - start.X)), Math.Max(1, Math.Abs(end.Y - start.Y)));

    private static Color ParseColor(string value, float opacity = 1)
    {
        var hex = value.TrimStart('#');
        if (hex.Length != 6) return Color.FromArgb((byte)(255 * opacity), 244, 247, 251);
        return Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)),
            Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private static bool IsValidHexColor(string value) =>
        value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit);

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
