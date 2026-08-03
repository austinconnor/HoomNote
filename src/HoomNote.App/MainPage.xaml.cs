using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
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
using Windows.Globalization.NumberFormatting;
using Windows.UI;
using Windows.UI.Core;
using System.Text.Json;
using HoomNote.Infrastructure.Serialization;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace HoomNote_App;

public sealed partial class MainPage : Page
{
    private const string CanvasClipboardFormat = "application/x-hoomnote-canvas-objects+json";
    private const string NotebookTabDataFormat = "application/x-hoomnote-notebook-tab";
    private const string LibraryNotebookDragPrefix = "notebook:";
    private const string LibraryFolderDragPrefix = "folder:";
    private sealed record FolderDisplay(Guid? Id, string Name, string Color)
    {
        public override string ToString() => Name;
    }

    private sealed record BatchImportSource(string SourcePath, string RelativeFolder);

    private sealed record BatchImportOptions(
        bool CombineIntoOneNotebook,
        string CombinedNotebookName,
        IReadOnlyList<int>? PageIndexes,
        double Margin,
        int RotationDegrees);

    private sealed class LibraryTreeEntry(
        Guid? folderId,
        DocumentSummary? document,
        string name,
        string color,
        string? metadata,
        int depth)
    {
        public Guid? FolderId { get; } = folderId;
        public DocumentSummary? Document { get; } = document;
        public string Name { get; } = name;
        public string Metadata { get; } = metadata ?? string.Empty;
        public int Depth { get; } = Math.Max(0, depth);
        public IReadOnlyList<int> AncestorGuides { get; } =
            Enumerable.Range(0, Math.Max(0, depth)).ToArray();
        public Thickness GuideMargin { get; } =
            new(-24 * Math.Max(0, depth), 0, 0, 0);
        public SolidColorBrush Brush { get; } = new(ParseColor(color));
        public bool IsFolder => FolderId is not null && Document is null;
        public bool IsContainer => Document is null;
        public bool CanDrag => Document is not null || FolderId is not null;
        public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";
    }

    private sealed class HomeNotebookCard : INotifyPropertyChanged
    {
        private BitmapImage? _thumbnail;
        private bool _isLoading;

        public HomeNotebookCard(DocumentSummary document, bool shouldLoadThumbnail = true)
        {
            DocumentId = document.Id;
            Title = document.Title;
            Metadata =
                $"{document.PageCount} {(document.PageCount == 1 ? "page" : "pages")} • {document.UpdatedAt.LocalDateTime:g}";
            AccentBrush = new SolidColorBrush(ParseColor(document.Color));
            ShouldLoadThumbnail = shouldLoadThumbnail;
            _isLoading = shouldLoadThumbnail;
        }

        public HomeNotebookCard(
            NotebookFolderPreference folder,
            int childFolderCount,
            int notebookCount,
            string? thumbnailAssetHash)
        {
            FolderId = folder.Id;
            FolderThumbnailAssetHash = thumbnailAssetHash;
            Title = folder.Name;
            Metadata = $"{childFolderCount} {(childFolderCount == 1 ? "folder" : "folders")} • " +
                       $"{notebookCount} {(notebookCount == 1 ? "notebook" : "notebooks")}";
            AccentBrush = new SolidColorBrush(ParseColor(folder.Color));
            ShouldLoadThumbnail = !string.IsNullOrWhiteSpace(thumbnailAssetHash);
            _isLoading = ShouldLoadThumbnail;
        }

        public Guid? DocumentId { get; }
        public Guid? FolderId { get; }
        public Guid Id => DocumentId ?? FolderId ?? Guid.Empty;
        public bool IsFolder => FolderId is not null;
        public Visibility FolderMenuVisibility => IsFolder ? Visibility.Visible : Visibility.Collapsed;
        public string? FolderThumbnailAssetHash { get; }
        public bool HasCustomThumbnail => !string.IsNullOrWhiteSpace(FolderThumbnailAssetHash);
        public Stretch ThumbnailStretch => IsFolder ? Stretch.UniformToFill : Stretch.Uniform;
        public bool ShouldLoadThumbnail { get; }
        public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";
        public string Title { get; }
        public string Metadata { get; }
        public SolidColorBrush AccentBrush { get; }
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (ReferenceEquals(_thumbnail, value)) return;
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading == value) return;
                _isLoading = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class HomeNotebookGroup(string key) : ObservableCollection<HomeNotebookCard>
    {
        public string Key { get; } = key;
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
        Eyedropper,
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
    private readonly ObservableCollection<HomeNotebookGroup> _homeLibraryGroups = [];
    private Guid? _homeFolderId;
    private readonly Dictionary<Guid, CommandHistory> _documentHistories = [];
    private CommandHistory _history = new();
    private SpatialIndex _spatialIndex = new();
    private readonly Dictionary<Guid, HoomNoteDocument> _openDocumentCache = [];
    private readonly LinkedList<Guid> _openDocumentLru = [];
    private readonly Dictionary<Guid, int> _openDocumentPointCounts = [];
    private const int OpenDocumentCacheLimit = 2;
    // Keep the two notebooks a user is actively switching between even when both contain dense
    // Samsung ink. The previous 400k ceiling evicted a 596k-point notebook immediately, forcing
    // another full JSON parse on every return navigation.
    private const int OpenDocumentCachePointBudget = 1_000_000;
    private const int ToolbarPresetLimit = 50;
    private readonly Dictionary<Guid, SpatialIndex> _pageSpatialIndexCache = [];
    private readonly LinkedList<Guid> _pageSpatialIndexLru = [];
    private const int PageSpatialIndexCacheLimit = 2;
    private readonly HashSet<Guid> _visibleObjectIds = [];
    private readonly List<CanvasObject> _visibleObjects = [];
    private CancellationTokenSource? _spatialIndexBuildCancellation;
    private readonly PdfPreviewCache _pdfPreview = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _documentLoadGate = new(1, 1);
    private CancellationTokenSource? _documentLoadCancellation;
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
    private readonly ConcurrentDictionary<string, byte> _queuedImageLoadRequests =
        new(StringComparer.OrdinalIgnoreCase);
    private int _queuedPdfLoadRequest;
    private readonly HashSet<string> _failedImageLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<Guid>> _imageWaitingPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _imagePagesNeedingRefresh = [];
    private readonly SemaphoreSlim _imageDecodeGate = new(1, 1);
    private int _imageLoadGeneration;
    private readonly Dictionary<Guid, BitmapImage> _pageThumbnailCache = [];
    private readonly LinkedList<Guid> _pageThumbnailLru = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _pageThumbnailLoads = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _homeThumbnailLoads = [];
    private readonly HashSet<Guid> _dirtyHomeThumbnailDocumentIds = [];
    private readonly SemaphoreSlim _homeThumbnailLoadGate = new(1, 1);
    private readonly Dictionary<Guid, CancellationTokenSource> _homeThumbnailRefreshCancellations = [];
    private readonly Dictionary<Guid, Task> _homeThumbnailRefreshTasks = [];
    private const int PageThumbnailCacheLimit = 24;
    private const int PageThumbnailMaxWidth = 96;
    private const int PageThumbnailMaxHeight = 116;
    private const int HomeThumbnailMaxWidth = 320;
    private const int HomeThumbnailMaxHeight = 400;
    private const int PageThumbnailRefreshDelayMs = 400;
    private int _thumbnailPriorityGeneration;
    private readonly List<CanvasCachedGeometry> _liveInkGeometryChunks = [];
    private int _liveInkChunkStart;
    private const int LiveInkChunkSize = 64;
    private sealed record StrokeGeometryCacheEntry(
        InkStrokeObject Stroke,
        CanvasGeometry Geometry,
        Color Color,
        bool IsCenterline,
        float Width);
    private sealed record PageRenderState(
        NotePage? Page,
        double Zoom,
        Vector2 Pan,
        double Width,
        double Height,
        float Dpi,
        bool InteractionActive,
        int EditVersion);
    private sealed record AdjacentPagePreview(
        Guid PageId,
        SizeD PageSize,
        CanvasBitmap Bitmap);

    private readonly struct CanvasBlendScope(CanvasDrawingSession session, CanvasBlend previous) : IDisposable
    {
        public void Dispose() => session.Blend = previous;
    }
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
    private readonly CanvasTextFormat _pageNumberTextFormat = new()
    {
        FontFamily = "Segoe UI Variable Text",
        FontSize = 10,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center
    };
    private readonly Dictionary<Guid, Guid> _tabPageSelections = [];
    private IReadOnlyList<Guid>? _pageDragOrder;
    private readonly List<CanvasObject> _selectedObjects = [];
    private readonly List<RecognizedTextRegion> _selectedTextRegions = [];
    private PointD? _textSelectionAnchor;
    private RectD? _textSelectionDragBounds;
    private readonly Dictionary<Guid, CanvasObject> _multiTransformPreviews = [];
    private readonly HashSet<Guid> _selectionTransformOriginalIds = [];
    private RectD? _selectionTransformSourceBounds;
    private readonly Dictionary<Guid, CanvasObject> _styleBrushOriginals = [];
    // The committed page is rendered by CanvasAnimatedControl on its dedicated game-loop
    // thread. UI/input mutations publish immutable page/viewport state through InvalidateCanvas.
    // GPU resources are created, drawn, and disposed while holding this renderer-owned gate.
    private readonly object _pageRenderGate = new();
    private int _pageRenderInvalidationRequested;
    private int _strokeGeometryClearRequested;
    private int _navigationTileClearRequested;
    private int _erasePreviewCommitVersion = -1;
    private int _erasePreviewRetireQueued;
    private int _transformPreviewCommitVersion = -1;
    private int _transformPreviewRetireQueued;
    private int _inkPreviewCommitVersion = -1;
    private int _inkPreviewRetireQueued;
    private readonly List<(CanvasObject Object, int Version)> _pendingInkCommitPreviews = [];
    private double _canvasWidth = 1;
    private double _canvasHeight = 1;
    private float _canvasDpi = 96;
    private PageRenderState _publishedPageRenderState =
        new(null, 1, Vector2.Zero, 1, 1, 96, false, 0);
    private NotePage? _publishedPageSnapshot;
    private int _publishedPageEditVersion = -1;
    private readonly MenuFlyout _notebookContextMenu = new();
    private readonly MenuFlyout _folderContextMenu = new();
    private readonly MenuFlyout _pageContextMenu = new();
    private readonly MenuFlyout _canvasContextMenu = new();
    private MenuFlyoutItem _canvasCutMenuItem = null!;
    private MenuFlyoutItem _canvasCopyMenuItem = null!;
    private MenuFlyoutItem _canvasPasteMenuItem = null!;
    private PointD? _canvasContextPastePoint;
    private CanvasCommandList? _pageRenderCache;
    private CanvasRenderTarget? _lowZoomPageRaster;
    private Guid? _lowZoomPageRasterPageId;
    private readonly Dictionary<Guid, AdjacentPagePreview> _notebookPagePreviews = [];
    private readonly Dictionary<Guid, Task> _notebookPagePreviewLoads = [];
    private readonly HashSet<Guid> _notebookPagePreviewRefreshPending = [];
    private Guid? _preloadedFallbackPageId;
    private CancellationTokenSource? _notebookPagePreviewCancellation;
    private int _notebookPagePreviewGeneration;
    private int _notebookPagePreviewLongEdge = 1536;
    private readonly Dictionary<(int X, int Y), CanvasRenderTarget> _navigationTiles = [];
    private readonly LinkedList<(int X, int Y)> _navigationTileLru = [];
    private readonly Dictionary<(int X, int Y), LinkedListNode<(int X, int Y)>> _navigationTileLruNodes = [];
    private readonly HashSet<(int X, int Y)> _visibleNavigationTileKeys = [];
    private Guid? _navigationTilePageId;
    private double _navigationTileScale;
    private long _navigationTileBytes;
    private Guid? _pageRenderCachePageId;
    private readonly HashSet<Guid> _pageRenderCacheObjectIds = [];
    private readonly List<CanvasObject> _pageRenderOverlays = [];
    private readonly List<CanvasCommandList> _pageRenderOverlayBatches = [];
    private readonly ConcurrentQueue<(Guid PageId, CanvasObject Object)> _pendingPageRenderAppends = new();
    // Keep recent strokes as cached geometry while the user writes. Compiling every pen-up into
    // a command list created a visible hitch between letters; batching amortizes that work.
    private const int OverlayBatchSize = 8;
    private const int OverlayBatchCompactionThreshold = 8;
    private const int NavigationTilePixels = 320;
    private const int NavigationTileGutterPixels = 2;
    private const long NavigationTileByteBudget = 32L * 1024 * 1024;
    private const int NavigationTileObjectThreshold = 256;
    private const float ContinuousPageGap = 28;
    private const int AdjacentPagePreviewLongEdge = 1536;
    private const long NotebookPagePreviewByteBudget = 256L * 1024 * 1024;
    private const int NotebookPagePreviewLookAhead = 5;

    private SqliteDocumentRepository? _repository;
    private MainWindow? _hostWindow;
    private Guid? _startupDocumentId;
    private bool _isPrimaryWindow;
    private Window? HostWindow => _hostWindow ?? App.MainAppWindow;
    private ContentAddressedAssetStore? _assetStore;
    private HoomNotePackageService? _packageService;
    private VectorExportService? _vectorExportService;
    private DocumentImportService? _importService;
    // Kept as dormant migration hooks for documents that already contain recognized
    // metadata. No recognition service is instantiated or scheduled in this release.
    private IHandwritingRecognitionService? _recognizer = null;
    private WindowsPageOcrService? _pageOcr = null;
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
    private readonly DispatcherQueueTimer _zoomIndicatorTimer;
    private Storyboard? _zoomIndicatorFade;
    private readonly HashSet<Guid> _pendingThumbnailRefreshPageIds = [];
    private EditorTool _activeTool = EditorTool.Select;
    private ShapeKind _selectedShapeKind = ShapeKind.Square;
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
    private bool _touchPageScrollActive;
    private double _touchPageScrollAnchorY;
    private bool _zoomNavigationActive;
    private bool _wheelZoomAnimating;
    private bool _wheelScrollAnimating;
    private bool _viewportFramePumpActive;
    private double _wheelZoomTarget = 1;
    private double _wheelZoomStart = 1;
    private Point _wheelZoomAnchorScreen;
    private PointD _wheelZoomAnchorPage;
    private long _wheelZoomAnimationStarted;
    private float _wheelScrollVelocity;
    private long _wheelScrollTimestamp;
    private long _lastPenInteractionTimestamp;
    private long _lastNativeTouchTimestamp;
    private int _pointerClassificationLogCount;
    private long _lastInkMovementTimestamp;
    private double _zoom = 1;
    private double _minimumZoom = 0.08;
    private double _maximumZoom = 8;
    private bool _syncingZoomLimits;
    private bool _cornerZoomDragging;
    private uint _cornerZoomPointerId;
    private Point _cornerZoomStart;
    private double _cornerZoomStartLevel;
    private Point _cornerZoomAnchorScreen;
    private PointD _cornerZoomAnchorPage;
    private bool _fitPending = true;
    private bool _isPointerDown;
    private bool _penActive;
    private bool _gestureAllowsTextSelection;
    private bool _loading;
    private bool _isUnloading;
    private bool _updatingTabs;
    private bool _syncingInkColor;
    private bool _syncingInkWidth;
    private bool _applyingToolbarPreset;
    private Guid? _activeToolbarPresetId;
    private Guid? _activeStylePresetId;
    private bool _syncingTemporaryGridSize;
    private bool _syncingTextEditor;
    private bool _requiresFullSave;
    private int _fullSaveVersion;
    private bool _hasUnsavedChanges;
    private int _editVersion;
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
    private HashSet<Guid>? _libraryDragExpandedSnapshot;
    private long _suppressLibraryTapUntil;
    private bool _rebuildingFolderTree;
    private Guid? _recognitionPageId;
    private DocumentSummary? _notebookContextTarget;
    private FolderDisplay? _folderContextTarget;
    private NotePage? _pageContextTarget;
    private MenuFlyoutItem _renameFolderMenuItem = null!;
    private MenuFlyoutItem _deleteFolderMenuItem = null!;
    private MenuFlyoutItem _newSubfolderMenuItem = null!;
    private MenuFlyoutItem _removeNotebookFolderMenuItem = null!;
    private string _inkColor = "#111111";
    private string _penColor = "#111111";
    private string _highlighterColor = "#FFFF00";
    private EditorTool _colorTool = EditorTool.Pen;
    private float? _presetOpacity;
    private float? _presetSmoothing;
    private long _lastTextEditorCloseTimestamp;
    private Color? _pendingTextColor;
    private Guid? _pendingTextColorObjectId;
    private bool _styleToolPickMode = true;
    private bool _syncingStyleTool;
    private bool _syncingEraserSize;
    private string _styleToolColor = "#111111";
    private float _styleToolWidth = 2.4f;
    private float _styleBrushSize = 36f;
    private double _eraserSize = 12;
    private PointD? _styleBrushPoint;
    private ToolTip? _openToolTip;
    private CancellationTokenSource? _toolTipCloseCancellation;
    private long _lastPasteTimestamp;
    private Guid? _draggedPresetId;
    private readonly SemaphoreSlim _pasteGate = new(1, 1);
    private readonly Dictionary<ColumnDefinition, CancellationTokenSource> _sidebarAnimations = [];
    private readonly HashSet<Guid> _pageOcrIndexedThisSession = [];
    private readonly HashSet<Guid> _semanticTextLoads = [];
    private readonly List<RectD> _searchFlashBounds = [];
    private long _searchFlashStarted;
    private long _lastSlowFrameLogTimestamp;
    private int _frameNavigationTileBuilds;
    private string _frameRenderMode = "none";
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
        if (Resources["HomeLibraryGroupsSource"] is CollectionViewSource homeSource)
            homeSource.Source = _homeLibraryGroups;
        // XAML assigns NumberBox.Value and ColorPicker.Color while the rest of the page is
        // still being constructed. Subscribing in markup lets those assignments invoke our
        // synchronization handlers before the inspector controls exist, which aborts startup.
        QuickStrokeWidthBox.ValueChanged += OnQuickStrokeWidthChanged;
        QuickStrokeWidthBox.NumberFormatter = new DecimalFormatter
        {
            IntegerDigits = 1,
            FractionDigits = 1,
            IsGrouped = false
        };
        QuickInkColorPicker.ColorChanged += OnQuickInkColorChanged;
        TemporaryGridSizeSlider.ValueChanged += OnTemporaryGridSizeChanged;
        TemporaryGridSizeNumberBox.ValueChanged += OnTemporaryGridSizeNumberChanged;
        StyleBrushSizeSlider.ValueChanged += OnStyleBrushSizeChanged;
        StyleBrushSizeNumberBox.ValueChanged += OnStyleBrushSizeNumberChanged;
        StyleStrokeWidthSlider.ValueChanged += OnStyleStrokeWidthChanged;
        StyleStrokeWidthNumberBox.ValueChanged += OnStyleStrokeWidthNumberChanged;
        EraserSizeSlider.ValueChanged += OnEraserSizeChanged;
        EraserSizeNumberBox.ValueChanged += OnEraserSizeNumberChanged;
        MinimumZoomNumberBox.ValueChanged += OnMinimumZoomChanged;
        MaximumZoomNumberBox.ValueChanged += OnMaximumZoomChanged;
        ShapePicker.SelectionChanged += OnInspectorShapeChanged;
        VersionText.Text = DisplayVersion();
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
        // Rendering remains off the interactive Win2D device, so a short idle delay keeps the
        // preview responsive without starting work in the middle of an active pointer gesture.
        _thumbnailRefreshTimer.Interval = TimeSpan.FromMilliseconds(PageThumbnailRefreshDelayMs);
        _thumbnailRefreshTimer.IsRepeating = false;
        _thumbnailRefreshTimer.Tick += OnThumbnailRefreshTimerTick;
        _navigationSettleTimer = DispatcherQueue.CreateTimer();
        _navigationSettleTimer.Interval = TimeSpan.FromMilliseconds(180);
        _navigationSettleTimer.IsRepeating = false;
        _navigationSettleTimer.Tick += OnNavigationSettleTick;
        _zoomIndicatorTimer = DispatcherQueue.CreateTimer();
        _zoomIndicatorTimer.Interval = TimeSpan.FromMilliseconds(850);
        _zoomIndicatorTimer.IsRepeating = false;
        _zoomIndicatorTimer.Tick += OnZoomIndicatorTimerTick;
        AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnGlobalKeyDown), handledEventsToo: true);
        DiagnosticsLog.Info("main.constructor.binding_page_list");
        PageList.ItemsSource = _pages;
        DiagnosticsLog.Info("main.constructor.binding_search_results");
        SearchResultsList.ItemsSource = _searchResults;
        DiagnosticsLog.Info("main.constructor.bindings_complete");
        BuildContextMenus();
        SetInkColor(_inkColor, rememberForTool: false);
        _pdfPreview.PreviewAvailable += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            InvalidatePageRenderCache();
            InvalidateCanvas();
        });
    }

    private static string DisplayVersion()
    {
        var version = typeof(MainPage).Assembly.GetName().Version;
        return version is null
            ? string.Empty
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not MainPageNavigationContext context) return;
        _hostWindow = context.HostWindow;
        _startupDocumentId = context.InitialDocumentId;
        _isPrimaryWindow = context.IsPrimary;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DiagnosticsLog.Info("main.loaded_start");
        if (_hostWindow is { NativeTouchSource: { } nativeTouch })
        {
            nativeTouch.Frame -= OnNativeTouchFrame;
            nativeTouch.Frame += OnNativeTouchFrame;
        }
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoomNote");
            Directory.CreateDirectory(root);
            _userSettingsStore = new LocalUserSettingsStore(Path.Combine(root, "settings.json"));
            _userPreferences = await App.LoadSharedUserPreferencesAsync(_userSettingsStore);
            var repairedFolders = NotebookFolderHierarchy.RepairInvalidParents(_userPreferences.NotebookFolders);
            if (repairedFolders.Count > 0)
            {
                DiagnosticsLog.Warning("folder.invalid_parents_repaired",
                    ("count", repairedFolders.Count));
                await App.SaveSharedUserPreferencesAsync(_userSettingsStore, _userPreferences);
            }
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
            if (migratedInkPresets)
                await App.SaveSharedUserPreferencesAsync(_userSettingsStore, _userPreferences);
            _penColor = IsValidHexColor(_userPreferences.PenColor) ? _userPreferences.PenColor.ToUpperInvariant() : "#111111";
            _highlighterColor = IsValidHexColor(_userPreferences.HighlighterColor) ? _userPreferences.HighlighterColor.ToUpperInvariant() : "#FFFF00";
            HighlighterStraightCheckBox.IsChecked = _userPreferences.HighlighterStraightLine;
            _expandedFolderIds.Clear();
            foreach (var value in _userPreferences.ExpandedFolderIds)
                if (Guid.TryParse(value, out var folderId))
                    _expandedFolderIds.Add(folderId);
            _temporaryGridSize = Math.Clamp(_userPreferences.TemporaryGridSize, 8, 128);
            _syncingTemporaryGridSize = true;
            TemporaryGridSizeSlider.Value = _temporaryGridSize;
            TemporaryGridSizeNumberBox.Value = _temporaryGridSize;
            _syncingTemporaryGridSize = false;
            _styleBrushSize = (float)Math.Clamp(_userPreferences.StyleBrushSize, 8, 120);
            _eraserSize = Math.Clamp(_userPreferences.EraserSize, 4, 96);
            _syncingEraserSize = true;
            EraserSizeSlider.Value = _eraserSize;
            EraserSizeNumberBox.Value = _eraserSize;
            EraserSizeText.Text = $"{_eraserSize:0}";
            _syncingEraserSize = false;
            ScaleStrokeWidthsToggle.IsOn = _userPreferences.ScaleStrokeWidthsOnTransform;
            var storedMinimumZoom = NormalizeZoomPercent(_userPreferences.MinimumZoomPercent, 8);
            var storedMaximumZoom = NormalizeZoomPercent(_userPreferences.MaximumZoomPercent, 800);
            _minimumZoom = Math.Min(storedMinimumZoom, storedMaximumZoom) / 100d;
            _maximumZoom = Math.Max(storedMinimumZoom, storedMaximumZoom) / 100d;
            _syncingZoomLimits = true;
            MinimumZoomNumberBox.Value = _minimumZoom * 100d;
            MaximumZoomNumberBox.Value = _maximumZoom * 100d;
            _syncingZoomLimits = false;
            SetInkColor(_penColor, rememberForTool: false);
            RebuildPresetToolbar();
            RebuildFolderTree();
            _assetStore = new ContentAddressedAssetStore(Path.Combine(root, "assets"));
            _pageThumbnailRenderer = new PageThumbnailRenderer(_assetStore);
            _repository = new SqliteDocumentRepository(Path.Combine(root, "library.db"));
            await _repository.InitializeAsync();
            _packageService = new HoomNotePackageService(_assetStore);
            _vectorExportService = new VectorExportService(_assetStore);
            var workerPath = Path.Combine(AppContext.BaseDirectory, "HoomNote.Import.Worker.exe");
            _importService = new DocumentImportService(_assetStore,
                new SlideWorkerConverter(workerPath, Path.Combine(root, "import-temp")));
            await RefreshLibraryAsync();
            ConfigureTransientToolTips(this);
            StatusText.Text = "Ready • autosave enabled";
            DiagnosticsLog.Info("main.loaded_complete", ("notebooks", _allDocuments.Count),
                ("log_directory", DiagnosticsLog.LogDirectory));
            if (_isPrimaryWindow)
                _ = UpdateService.CheckForUpdatesAsync(XamlRoot, manual: false, PrepareForUpdateRestartAsync);
        }
        catch (Exception exception)
        {
            ShowError("HoomNote could not initialize its local library.", exception);
        }
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isUnloading = true;
        DiagnosticsLog.Info("main.unloading", ("unsaved", _hasUnsavedChanges),
            ("document_open", _document is not null));
        _saveTimer.Stop();
        if (_hostWindow is { NativeTouchSource: { } nativeTouch })
            nativeTouch.Frame -= OnNativeTouchFrame;
        _searchDebounce?.Cancel();
        _searchLocateCancellation?.Cancel();
        _recognitionTimer.Stop();
        _thumbnailRefreshTimer.Stop();
        _navigationSettleTimer.Stop();
        _zoomIndicatorTimer.Stop();
        _zoomIndicatorFade?.Stop();
        _wheelZoomAnimating = false;
        StopViewportFramePump();
        StopTouchInertia(resumeBackgroundWork: false);
        _touchPoints.Clear();
        ResetTouchPageScroll();
        _isPointerDown = false;
        _penActive = false;
        DrawingSurface.ReleasePointerCaptures();
        foreach (var cancellation in _pageThumbnailLoads.Values) cancellation.Cancel();
        _pageThumbnailLoads.Clear();
        foreach (var cancellation in _homeThumbnailLoads.Values) cancellation.Cancel();
        _homeThumbnailLoads.Clear();
        foreach (var cancellation in _homeThumbnailRefreshCancellations.Values)
            cancellation.Cancel();
        _documentLoadCancellation?.Cancel();
        _notebookPagePreviewCancellation?.Cancel();
        _notebookPagePreviewCancellation = null;
        _pageThumbnailCache.Clear();
        _pageThumbnailLru.Clear();
        _settingsSaveDebounce?.Cancel();
        _handwritingIndexCancellation?.Cancel();
        _incrementalRecognitionCancellation?.Cancel();
        _toolTipCloseCancellation?.Cancel();
        _spatialIndexBuildCancellation?.Cancel();
        foreach (var animation in _sidebarAnimations.Values) animation.Cancel();
        await _documentLoadGate.WaitAsync();
        _documentLoadGate.Release();
        if (_document is not null) await SaveNowAsync();
        await Task.WhenAll(_homeThumbnailRefreshTasks.Values.Distinct());
        if (_userSettingsStore is not null) await SaveUserPreferencesAsync();
        if (_repository is not null) await _repository.DisposeAsync();
        PageSurface.RemoveFromVisualTree();
        DrawingSurface.RemoveFromVisualTree();
        lock (_pageRenderGate)
        {
            DisposeNotebookPagePreviewsCore();
            InvalidatePageRenderCacheCore();
            ClearStrokeGeometryCacheCore();
            ClearImageBitmapCacheCore();
        }
        ClearLiveInkGeometryCache();
        _pageNumberTextFormat.Dispose();
        _roundInkStrokeStyle.Dispose();
        _pdfPreview.Dispose();
        DiagnosticsLog.Info("main.unloaded");
    }

    private async Task RefreshLibraryAsync(Guid? preferredDocumentId = null)
    {
        if (_repository is null) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var summaries = await _repository.ListAsync();
        _allDocuments.Clear();
        _allDocuments.AddRange(summaries);
        ApplyFolderFilter(preferredDocumentId);
        RebuildHomeLibrary();
        if (_allDocuments.Count > 0)
        {
            var explicitlyPreferred = preferredDocumentId is { } preferredId
                ? _allDocuments.FirstOrDefault(item => item.Id == preferredId)
                : null;
            var requested = _startupDocumentId is { } requestedId
                ? _allDocuments.FirstOrDefault(item => item.Id == requestedId)
                : null;
            var current = _document is null
                ? null
                : _allDocuments.FirstOrDefault(item => item.Id == _document.Id);
            var preferred = explicitlyPreferred ?? requested ?? current;
            _startupDocumentId = null;
            if (preferred is not null && (_document is null || _document.Id != preferred.Id))
                await LoadDocumentAsync(preferred.Id);
            if (preferred is not null) SelectLibraryDocument(preferred.Id);
        }
        UpdateEmptyState();
        DiagnosticsLog.Info("library.refresh_completed",
            ("documents", _allDocuments.Count),
            ("elapsed_ms", MillisecondsSince(started)));
    }

    private void ApplyFolderFilter(Guid? preferredDocumentId = null)
    {
        var documents = _allDocuments
            .OrderBy(document => document.Title, NotebookTitleComparer.Instance)
            .ThenBy(document => document.Id)
            .ToList();
        _loading = true;
        _documents.Clear();
        foreach (var document in documents)
        {
            _documents.Add(document with { Color = EffectiveDocumentColor(document.Id) });
        }
        _loading = false;
        RebuildFolderTree(_selectedFolderId, preferredDocumentId);
        RefreshNotebookTabHeaders();
        UpdateLibrarySummary();
        UpdateFolderActions();
    }

    private void RebuildHomeLibrary()
    {
        foreach (var cancellation in _homeThumbnailLoads.Values) cancellation.Cancel();
        _homeThumbnailLoads.Clear();
        _homeLibraryGroups.Clear();

        var layout = HomeLibraryOrdering.Build(
            _allDocuments,
            _userPreferences.NotebookFolders,
            _userPreferences.DocumentFolders,
            _homeFolderId);
        _homeFolderId = layout.CurrentFolderId;
        var currentFolder = layout.CurrentFolderId is { } currentFolderId
            ? _userPreferences.NotebookFolders.FirstOrDefault(folder => folder.Id == currentFolderId)
            : null;
        HomeLibraryTitle.Text = currentFolder?.Name ?? "Your library";
        HomeLibrarySubtitle.Text = currentFolder is null
            ? "Browse folders or open any notebook."
            : "Open a subfolder or choose a notebook.";
        HomeLibraryBackButton.Visibility = currentFolder is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        var childFolders = layout.ChildFolders;
        if (childFolders.Count > 0)
        {
            var group = new HomeNotebookGroup("Folders");
            foreach (var folder in childFolders)
            {
                var childFolderCount = _userPreferences.NotebookFolders.Count(item =>
                    item.ParentId == folder.Id);
                var notebookCount = _allDocuments.Count(document =>
                    DocumentFolderId(document.Id) == folder.Id);
                _userPreferences.FolderThumbnails.TryGetValue(
                    folder.Id.ToString("D"), out var thumbnailAssetHash);
                group.Add(new HomeNotebookCard(
                    folder, childFolderCount, notebookCount, thumbnailAssetHash));
            }
            _homeLibraryGroups.Add(group);
        }

        if (layout.Documents.Count > 0)
        {
            var group = new HomeNotebookGroup("All notebooks");
            foreach (var document in layout.Documents)
                group.Add(new HomeNotebookCard(
                    document with { Color = EffectiveDocumentColor(document.Id) }));
            _homeLibraryGroups.Add(group);
        }
    }

    private void OnHomeNotebookContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not HomeNotebookCard card ||
            !card.ShouldLoadThumbnail || card.Thumbnail is not null ||
            _homeThumbnailLoads.ContainsKey(card.Id)) return;
        var cancellation = new CancellationTokenSource();
        _homeThumbnailLoads[card.Id] = cancellation;
        _ = LoadHomeNotebookThumbnailAsync(card, cancellation);
    }

    private async Task LoadHomeNotebookThumbnailAsync(
        HomeNotebookCard card,
        CancellationTokenSource cancellation)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            if (card.FolderId is { } folderId &&
                card.FolderThumbnailAssetHash is { Length: > 0 } folderAssetHash)
            {
                if (_assetStore is null) return;
                await _homeThumbnailLoadGate.WaitAsync(cancellation.Token);
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(
                        _assetStore.GetPath(folderAssetHash));
                    using var stream = await file.OpenReadAsync();
                    var bitmap = new BitmapImage
                    {
                        DecodePixelWidth = HomeThumbnailMaxWidth
                    };
                    await bitmap.SetSourceAsync(stream);
                    cancellation.Token.ThrowIfCancellationRequested();
                    card.Thumbnail = bitmap;
                    card.IsLoading = false;
                    DiagnosticsLog.Info("folder.thumbnail_ready",
                        ("folder_id", folderId),
                        ("elapsed_ms", MillisecondsSince(started)));
                }
                finally
                {
                    _homeThumbnailLoadGate.Release();
                }
                return;
            }
            if (_repository is null || _pageThumbnailRenderer is null ||
                card.DocumentId is not { } documentId) return;
            await _homeThumbnailLoadGate.WaitAsync(cancellation.Token);
            try
            {
                var bytes = await _repository.LoadCachedHomeThumbnailAsync(
                    documentId, cancellation.Token);
                var source = "persisted";
                if (bytes is null)
                {
                    var fromMemory = _openDocumentCache.TryGetValue(documentId, out var openDocument);
                    var page = fromMemory
                        ? openDocument!.Pages.FirstOrDefault()
                        : await _repository.LoadFirstPageAsync(documentId, cancellation.Token);
                    if (page is null)
                    {
                        card.IsLoading = false;
                        DiagnosticsLog.Info("home.thumbnail_unavailable",
                            ("document_id", documentId),
                            ("reason", "no_pages"));
                        return;
                    }
                    source = fromMemory ? "memory" : "database";
                    // Dense native notebooks can have first-page JSON records tens of MB in
                    // size. Render them once in the background, then persist the small PNG so
                    // later library visits never repeat deserialization or vector rendering.
                    var size = ThumbnailSize(page, HomeThumbnailMaxWidth, HomeThumbnailMaxHeight);
                    bytes = await _pageThumbnailRenderer.RenderAsync(
                        page, size.Width, size.Height, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    try
                    {
                        await _repository.SaveCachedHomeThumbnailAsync(
                            documentId, page, bytes, cancellation.Token);
                    }
                    catch (Exception exception) when (!cancellation.IsCancellationRequested)
                    {
                        // A transient cache-write failure must not hide a thumbnail that was
                        // already rendered successfully for this library visit.
                        DiagnosticsLog.Warning("home.thumbnail_cache_save_failed",
                            ("document_id", documentId), ("error", exception.Message));
                    }
                }
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
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                cancellation.Token.ThrowIfCancellationRequested();
                card.Thumbnail = bitmap;
                card.IsLoading = false;
                DiagnosticsLog.Info("home.thumbnail_ready",
                    ("document_id", documentId),
                    ("source", source),
                    ("elapsed_ms", MillisecondsSince(started)));
            }
            finally
            {
                _homeThumbnailLoadGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            card.IsLoading = false;
            DiagnosticsLog.Warning("home.thumbnail_failed",
                ("document_id", card.Id), ("error", exception.Message));
        }
        finally
        {
            if (_homeThumbnailLoads.TryGetValue(card.Id, out var active) &&
                ReferenceEquals(active, cancellation))
                _homeThumbnailLoads.Remove(card.Id);
            cancellation.Dispose();
        }
    }

    private void ScheduleHomeThumbnailCacheRefresh(HoomNoteDocument document)
    {
        if (_isUnloading || _repository is null || _pageThumbnailRenderer is null ||
            document.Pages.FirstOrDefault() is not { } firstPage)
            return;

        if (_homeThumbnailRefreshCancellations.Remove(document.Id, out var previousCancellation))
            previousCancellation.Cancel();
        var cancellation = new CancellationTokenSource();
        _homeThumbnailRefreshCancellations[document.Id] = cancellation;
        var pageSnapshot = firstPage with { Objects = firstPage.Objects.ToList() };
        var previousRefresh = _homeThumbnailRefreshTasks.GetValueOrDefault(
            document.Id, Task.CompletedTask);
        _homeThumbnailRefreshTasks[document.Id] = RefreshHomeThumbnailCacheAfterAsync(
            previousRefresh, document.Id, pageSnapshot, cancellation);
    }

    private async Task RefreshHomeThumbnailCacheAfterAsync(
        Task previousRefresh,
        Guid documentId,
        NotePage page,
        CancellationTokenSource cancellation)
    {
        await previousRefresh;
        await RefreshHomeThumbnailCacheAsync(documentId, page, cancellation);
    }

    private async Task RefreshHomeThumbnailCacheAsync(
        Guid documentId,
        NotePage page,
        CancellationTokenSource cancellation)
    {
        var lockTaken = false;
        try
        {
            // Keep cover generation entirely outside active writing and the autosave window.
            // Returning to the library later can then decode a tiny persisted PNG immediately.
            await Task.Delay(1_200, cancellation.Token);
            if (_repository is null || _pageThumbnailRenderer is null) return;
            await _homeThumbnailLoadGate.WaitAsync(cancellation.Token);
            lockTaken = true;
            if (await _repository.LoadCachedHomeThumbnailAsync(documentId, cancellation.Token) is not null)
                return;
            var size = ThumbnailSize(page, HomeThumbnailMaxWidth, HomeThumbnailMaxHeight);
            var bytes = await _pageThumbnailRenderer.RenderAsync(
                page, size.Width, size.Height, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            await _repository.SaveCachedHomeThumbnailAsync(
                documentId, page, bytes, cancellation.Token);
            DiagnosticsLog.Info("home.thumbnail_cache_refreshed",
                ("document_id", documentId), ("bytes", bytes.Length));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Warning("home.thumbnail_cache_refresh_failed",
                ("document_id", documentId), ("error", exception.Message));
        }
        finally
        {
            if (lockTaken) _homeThumbnailLoadGate.Release();
            if (_homeThumbnailRefreshCancellations.TryGetValue(documentId, out var active) &&
                ReferenceEquals(active, cancellation))
                _homeThumbnailRefreshCancellations.Remove(documentId);
            cancellation.Dispose();
        }
    }

    private async void OnHomeNotebookClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not HomeNotebookCard card) return;
        if (card.FolderId is { } folderId)
        {
            _homeFolderId = folderId;
            RebuildHomeLibrary();
            return;
        }
        if (card.DocumentId is not { } documentId) return;
        await LoadDocumentAsync(documentId);
        SelectLibraryDocument(documentId);
    }

    private void OnFolderThumbnailMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HomeNotebookCard { FolderId: not null } card } button)
            return;
        var flyout = new MenuFlyout();
        var upload = new MenuFlyoutItem
        {
            Text = card.HasCustomThumbnail ? "Replace thumbnail…" : "Upload thumbnail…",
            Icon = new FontIcon { Glyph = "\uE91B" },
            Tag = card
        };
        upload.Click += OnUploadFolderThumbnailClick;
        flyout.Items.Add(upload);
        if (card.HasCustomThumbnail)
        {
            var remove = new MenuFlyoutItem
            {
                Text = "Remove thumbnail",
                Icon = new FontIcon { Glyph = "\uE74D" },
                Tag = card
            };
            remove.Click += OnRemoveFolderThumbnailClick;
            flyout.Items.Add(remove);
        }
        flyout.ShowAt(button);
    }

    private async void OnUploadFolderThumbnailClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem
            {
                Tag: HomeNotebookCard { FolderId: { } folderId }
            } || _assetStore is null || HostWindow is not { } hostWindow)
            return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(hostWindow));
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
                picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            await using var input = File.OpenRead(file.Path);
            var assetHash = await _assetStore.AddAsync(input, Path.GetExtension(file.Path));
            _userPreferences.FolderThumbnails[folderId.ToString("D")] = assetHash;
            await PersistUserPreferencesAsync("Updated folder thumbnail");
            RebuildHomeLibrary();
        }
        catch (Exception exception)
        {
            ShowError("The folder thumbnail could not be updated.", exception);
        }
    }

    private async void OnRemoveFolderThumbnailClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem
            {
                Tag: HomeNotebookCard { FolderId: { } folderId }
            })
            return;
        _userPreferences.FolderThumbnails.Remove(folderId.ToString("D"));
        await PersistUserPreferencesAsync("Removed folder thumbnail");
        RebuildHomeLibrary();
    }

    private void OnHomeLibraryBackClick(object sender, RoutedEventArgs e)
    {
        if (_homeFolderId is not { } folderId) return;
        _homeFolderId = _userPreferences.NotebookFolders
            .FirstOrDefault(folder => folder.Id == folderId)?.ParentId;
        RebuildHomeLibrary();
    }

    private string EffectiveDocumentColor(Guid documentId)
    {
        var documentKey = documentId.ToString("D");
        if (_userPreferences.DocumentColors.TryGetValue(documentKey, out var explicitColor) &&
            IsValidHexColor(explicitColor))
            return explicitColor;
        var folderId = DocumentFolderId(documentId);
        var folderColor = folderId is { } id
            ? _userPreferences.NotebookFolders.FirstOrDefault(folder => folder.Id == id)?.Color
            : null;
        return IsValidHexColor(folderColor ?? string.Empty) ? folderColor! : "#4BAEFF";
    }

    private void RebuildFolderTree(Guid? preferredFolderId = null, Guid? preferredDocumentId = null)
    {
        if (_libraryDragExpandedSnapshot is not null)
        {
            // TreeView can auto-expand a recycled row while completing a drag. Rebuild from
            // the state captured at drag start, then explicitly expand only the destination
            // ancestry below.
            _expandedFolderIds.Clear();
            _expandedFolderIds.UnionWith(_libraryDragExpandedSnapshot);
        }
        else if (FolderTree.RootNodes.Count > 0)
        {
            _expandedFolderIds.Clear();
            foreach (var existingRoot in FolderTree.RootNodes) CaptureExpandedFolders(existingRoot);
        }
        _rebuildingFolderTree = true;
        var rebuildVersion = ++_folderTreeRebuildVersion;
        FolderTree.RootNodes.Clear();
        foreach (var document in _documents.Where(document => DocumentFolderId(document.Id) is null))
            FolderTree.RootNodes.Add(BuildNotebookNode(document, 0));
        var folderIds = _userPreferences.NotebookFolders.Select(folder => folder.Id).ToHashSet();
        var renderedFolders = new HashSet<Guid>();
        foreach (var folder in _userPreferences.NotebookFolders
                     .Where(item => item.ParentId is null || !folderIds.Contains(item.ParentId.Value))
                     .OrderBy(item => item.Name))
        {
            FolderTree.RootNodes.Add(BuildFolderNode(folder, renderedFolders, [], 0));
        }
        foreach (var folder in _userPreferences.NotebookFolders.Where(item => !renderedFolders.Contains(item.Id)).OrderBy(item => item.Name))
            FolderTree.RootNodes.Add(BuildFolderNode(folder, renderedFolders, [], 0));

        LibraryEmptyText.Visibility = FolderTree.RootNodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var desiredFolder = preferredFolderId ?? _selectedFolderId;
        var desiredDocument = preferredDocumentId;
        var nodeToSelect = desiredDocument is { } documentId
            ? FindDocumentNode(FolderTree.RootNodes, documentId)
            : desiredFolder is { } folderId ? FindFolderNode(FolderTree.RootNodes, folderId) : null;
        if (nodeToSelect is not null && !IsTreeNodeVisible(nodeToSelect))
            nodeToSelect = null;
        // WinUI can fault in native TreeView code if SelectedNode is assigned in the
        // same call stack that replaced RootNodes. Restore it after the tree has attached.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (rebuildVersion != _folderTreeRebuildVersion) return;
            try
            {
                if (nodeToSelect is not null) FolderTree.SelectedNode = nodeToSelect;
            }
            catch (Exception exception)
            {
                DiagnosticsLog.Error("folder.selection_restore_failed", exception,
                    ("folder_count", _userPreferences.NotebookFolders.Count));
            }
            finally
            {
                _rebuildingFolderTree = false;
            }
        });
        UpdateFolderActions();
    }

    private void CaptureExpandedFolders(TreeViewNode node)
    {
        CaptureExpandedFolders(node, _expandedFolderIds);
    }

    private static void CaptureExpandedFolders(TreeViewNode node, ISet<Guid> destination)
    {
        if (GetLibraryEntry(node) is { } entry)
        {
            if (entry.FolderId is { } folderId && node.IsExpanded) destination.Add(folderId);
        }
        foreach (var child in node.Children)
            CaptureExpandedFolders(child, destination);
    }

    private TreeViewNode BuildFolderNode(
        NotebookFolderPreference folder,
        HashSet<Guid> rendered,
        HashSet<Guid> ancestry,
        int depth)
    {
        rendered.Add(folder.Id);
        ancestry.Add(folder.Id);
        var entry = new LibraryTreeEntry(folder.Id, null, folder.Name, folder.Color,
            _documents.Count(document => DocumentFolderId(document.Id) == folder.Id).ToString(), depth);
        var node = new TreeViewNode
        {
            Content = entry,
            IsExpanded = _expandedFolderIds.Contains(folder.Id)
        };
        foreach (var child in _userPreferences.NotebookFolders
                     .Where(item => item.ParentId == folder.Id && !ancestry.Contains(item.Id) && !rendered.Contains(item.Id))
                     .OrderBy(item => item.Name))
        {
            node.Children.Add(BuildFolderNode(child, rendered, ancestry, depth + 1));
        }
        foreach (var document in _documents.Where(document => DocumentFolderId(document.Id) == folder.Id))
            node.Children.Add(BuildNotebookNode(document, depth + 1));
        ancestry.Remove(folder.Id);
        return node;
    }

    private static TreeViewNode BuildNotebookNode(DocumentSummary document, int depth)
    {
        var entry = new LibraryTreeEntry(
            null, document, document.Title, document.Color, $"{document.PageCount}p", depth);
        return new TreeViewNode
        {
            Content = entry
        };
    }

    private void AttachNewFolderNode(NotebookFolderPreference folder)
    {
        var depth = NotebookFolderHierarchy.GetDepth(_userPreferences.NotebookFolders, folder.Id);
        var node = BuildFolderNode(folder, [], [], depth);
        if (folder.ParentId is { } parentId &&
            FindFolderNode(FolderTree.RootNodes, parentId) is { } parentNode)
        {
            InsertFolderNode(parentNode.Children, node, foldersBeforeDocuments: true);
            parentNode.IsExpanded = true;
        }
        else
        {
            InsertFolderNode(FolderTree.RootNodes, node, foldersBeforeDocuments: false);
        }
        LibraryEmptyText.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() =>
        {
            try { FolderTree.SelectedNode = node; }
            catch (Exception exception)
            {
                DiagnosticsLog.Error("folder.selection_restore_failed", exception,
                    ("folder_count", _userPreferences.NotebookFolders.Count));
            }
        });
    }

    private static void InsertFolderNode(IList<TreeViewNode> nodes, TreeViewNode folderNode,
        bool foldersBeforeDocuments)
    {
        var folderName = GetLibraryEntry(folderNode)?.Name ?? string.Empty;
        var insertionIndex = nodes.Count;
        for (var index = 0; index < nodes.Count; index++)
        {
            var entry = GetLibraryEntry(nodes[index]);
            if (entry is null) continue;
            if (entry.Document is not null)
            {
                if (foldersBeforeDocuments)
                {
                    insertionIndex = index;
                    break;
                }
                continue;
            }
            if (string.Compare(entry.Name, folderName, StringComparison.CurrentCultureIgnoreCase) <= 0) continue;
            insertionIndex = index;
            break;
        }
        nodes.Insert(insertionIndex, folderNode);
    }

    private bool RefreshFolderNodeContent(Guid folderId)
    {
        var folder = _userPreferences.NotebookFolders.FirstOrDefault(item => item.Id == folderId);
        var node = FindFolderNode(FolderTree.RootNodes, folderId);
        if (folder is null || node is null) return false;
        var depth = (GetLibraryEntry(node)?.Depth)
                    ?? NotebookFolderHierarchy.GetDepth(_userPreferences.NotebookFolders, folder.Id);
        node.Content = new LibraryTreeEntry(folder.Id, null, folder.Name, folder.Color,
            _documents.Count(document => DocumentFolderId(document.Id) == folder.Id).ToString(), depth);
        return true;
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

    private static bool IsTreeNodeVisible(TreeViewNode node)
    {
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            if (!ancestor.IsExpanded) return false;
        return true;
    }

    private void SelectLibraryDocument(Guid documentId, bool revealInLibrary = false)
    {
        var node = FindDocumentNode(FolderTree.RootNodes, documentId);
        if (node is null) return;
        if (revealInLibrary)
        {
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
                ancestor.IsExpanded = true;
        }
        else if (!IsTreeNodeVisible(node))
        {
            return;
        }
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

    private void OnFolderTreeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        _ = sender;
        if (_rebuildingFolderTree) return;
        if (GetLibraryEntry(args.Node)?.FolderId is not { } folderId) return;
        _expandedFolderIds.Add(folderId);
        PersistExpandedFolderState();
    }

    private void OnFolderTreeCollapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        _ = sender;
        if (_rebuildingFolderTree) return;
        if (GetLibraryEntry(args.Node)?.FolderId is not { } folderId) return;
        _expandedFolderIds.Remove(folderId);
        PersistExpandedFolderState();
    }

    private void PersistExpandedFolderState()
    {
        _userPreferences = _userPreferences with
        {
            ExpandedFolderIds = _expandedFolderIds
                .OrderBy(id => id)
                .Select(id => id.ToString("D"))
                .ToList()
        };
        ScheduleUserPreferencesSave();
    }

    private async void OnLibraryRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (System.Diagnostics.Stopwatch.GetTimestamp() < _suppressLibraryTapUntil)
        {
            e.Handled = true;
            return;
        }
        _libraryDragExpandedSnapshot = null;
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
                : null;
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
        if (sender is not FrameworkElement { Tag: LibraryTreeEntry entry }) return;
        CaptureLibraryDragState();
        args.AllowedOperations = DataPackageOperation.Move;
        if (entry.Document is { } document)
            args.Data.SetText(LibraryNotebookDragPrefix + document.Id.ToString("D"));
        else if (entry.FolderId is { } folderId)
            args.Data.SetText(LibraryFolderDragPrefix + folderId.ToString("D"));
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
        SuppressLibraryTap();
        var value = await e.DataView.GetTextAsync();
        if (!TryParseLibraryDragPayload(value, out var kind, out var itemId))
        {
            DiagnosticsLog.Warning("folder.drop_rejected", ("reason", "invalid_payload"));
            return;
        }
        DiagnosticsLog.Info("folder.drop_received", ("to_folder", target.FolderId is not null));
        QueueLibraryDrop(kind, itemId, target.FolderId);
    }

    private void OnNotebookDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not DocumentSummary document) return;
        CaptureLibraryDragState();
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
        e.Handled = true;
        SuppressLibraryTap();
        var value = await e.DataView.GetTextAsync();
        if (!TryParseLibraryDragPayload(value, out var kind, out var itemId)) return;
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        var node = container is null ? null : FolderTree.NodeFromContainer(container);
        var target = GetLibraryEntry(node);
        var targetFolderId = target?.FolderId;
        if (target?.Document is not null)
            targetFolderId = GetLibraryEntry(node?.Parent)?.FolderId;
        else if (container is not null && target?.IsContainer != true)
            return;
        QueueLibraryDrop(kind, itemId, targetFolderId);
    }

    private void CaptureLibraryDragState()
    {
        _libraryDragExpandedSnapshot = [];
        foreach (var root in FolderTree.RootNodes)
            CaptureExpandedFolders(root, _libraryDragExpandedSnapshot);
        SuppressLibraryTap();
    }

    private void SuppressLibraryTap(int milliseconds = 750)
    {
        var duration = (long)Math.Ceiling(
            milliseconds * System.Diagnostics.Stopwatch.Frequency / 1000d);
        _suppressLibraryTapUntil = Math.Max(
            _suppressLibraryTapUntil,
            System.Diagnostics.Stopwatch.GetTimestamp() + duration);
    }

    private void QueueLibraryDrop(LibraryDragKind kind, Guid itemId, Guid? targetFolderId)
    {
        // Let WinUI finish Drop/Tapped routing before replacing TreeView.RootNodes. Rebuilding
        // inside the Drop stack can recycle the source row under the release pointer and toggle
        // an unrelated sibling.
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (kind == LibraryDragKind.Folder)
                    await MoveFolderToFolderAsync(itemId, targetFolderId);
                else
                    await MoveNotebookToFolderAsync(itemId, targetFolderId);
            }
            catch (Exception exception)
            {
                ShowError("The library item could not be moved.", exception);
            }
            finally
            {
                SuppressLibraryTap();
                _libraryDragExpandedSnapshot = null;
            }
        });
    }

    private enum LibraryDragKind
    {
        Notebook,
        Folder
    }

    private static bool TryParseLibraryDragPayload(
        string value,
        out LibraryDragKind kind,
        out Guid itemId)
    {
        if (value.StartsWith(LibraryFolderDragPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(value[LibraryFolderDragPrefix.Length..], out itemId))
        {
            kind = LibraryDragKind.Folder;
            return true;
        }
        if (value.StartsWith(LibraryNotebookDragPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(value[LibraryNotebookDragPrefix.Length..], out itemId))
        {
            kind = LibraryDragKind.Notebook;
            return true;
        }
        // Preserve compatibility with the older notebook drag payload.
        kind = LibraryDragKind.Notebook;
        return Guid.TryParse(value, out itemId);
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
        var name = new TextBox
        {
            Header = parentId is null ? "Folder name" : "Subfolder name",
            MaxLength = LibraryNamePolicy.MaxLength
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = parentId is null ? "New folder" : "New subfolder",
            Content = name,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var normalizedName = LibraryNamePolicy.Normalize(name.Text);
        if (normalizedName is null) return;
        DiagnosticsLog.Info("folder.create_started", ("is_subfolder", parentId is not null));
        var created = new NotebookFolderPreference
        {
            ParentId = parentId, Name = normalizedName
        };
        _userPreferences.NotebookFolders.Add(created);
        _selectedFolderId = created.Id;
        AttachNewFolderNode(created);
        UpdateLibrarySummary();
        UpdateFolderActions();
        await PersistUserPreferencesAsync("Created notebook folder");
        DiagnosticsLog.Info("folder.created", ("is_subfolder", parentId is not null),
            ("parent_id", parentId?.ToString("D") ?? "root"));
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
        await PersistUserPreferencesAsync(folderId is null ? "Moved notebook to top level" : "Moved notebook to folder");
        DiagnosticsLog.Info("folder.notebook_moved", ("to_folder", folderId is not null));
    }

    private async Task MoveFolderToFolderAsync(Guid folderId, Guid? parentId)
    {
        var index = _userPreferences.NotebookFolders.FindIndex(folder => folder.Id == folderId);
        if (index < 0)
        {
            DiagnosticsLog.Warning("folder.move_rejected", ("reason", "missing_source_folder"));
            return;
        }
        if (parentId is not null &&
            _userPreferences.NotebookFolders.All(folder => folder.Id != parentId))
        {
            DiagnosticsLog.Warning("folder.move_rejected", ("reason", "missing_parent_folder"));
            return;
        }
        if (NotebookFolderHierarchy.WouldCreateCycle(
                _userPreferences.NotebookFolders, folderId, parentId))
        {
            DiagnosticsLog.Warning("folder.move_rejected", ("reason", "hierarchy_cycle"));
            StatusText.Text = "A folder cannot be moved inside itself";
            return;
        }

        var source = _userPreferences.NotebookFolders[index];
        if (source.ParentId == parentId) return;
        _userPreferences.NotebookFolders[index] = source with { ParentId = parentId };
        _selectedFolderId = folderId;
        RebuildFolderTree(folderId);
        await PersistUserPreferencesAsync(
            parentId is null ? "Moved folder to top level" : "Moved folder");
        DiagnosticsLog.Info("folder.moved",
            ("folder_id", folderId.ToString("D")),
            ("parent_id", parentId?.ToString("D") ?? "root"));
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
        _removeNotebookFolderMenuItem = AddMenuItem(_notebookContextMenu, "Move to top level", "\uE8F1",
            OnRemoveNotebookFolderContextClick);
        _notebookContextMenu.Items.Add(CreateColorMenu("Notebook color", OnNotebookColorClick));
        _notebookContextMenu.Items.Add(new MenuFlyoutSeparator());
        AddMenuItem(_notebookContextMenu, "Delete notebook", "\uE74D", OnDeleteNotebookContextClick);

        _newSubfolderMenuItem = AddMenuItem(_folderContextMenu, "New subfolder", "\uE8F4", OnNewSubfolderContextClick);
        _renameFolderMenuItem = AddMenuItem(_folderContextMenu, "Rename folder", "\uE8AC", OnRenameFolderContextClick);
        _folderContextMenu.Items.Add(CreateColorMenu("Folder color", OnFolderColorClick));
        _folderContextMenu.Items.Add(new MenuFlyoutSeparator());
        _deleteFolderMenuItem = AddMenuItem(_folderContextMenu, "Delete folder", "\uE74D", OnDeleteFolderContextClick);

        AddMenuItem(_pageContextMenu, "Delete page", "\uE74D", OnDeletePageContextClick);

        _canvasCutMenuItem = AddMenuItem(_canvasContextMenu, "Cut", "\uE8C6", OnCanvasCutContextClick);
        _canvasCopyMenuItem = AddMenuItem(_canvasContextMenu, "Copy", "\uE8B0", OnCanvasCopyContextClick);
        _canvasPasteMenuItem = AddMenuItem(_canvasContextMenu, "Paste here", "\uE77F", OnCanvasPasteContextClick);
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
        ApplyFolderFilter();
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

    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_readMode || _page is null) return;
        var screenPoint = e.GetPosition(DrawingSurface);
        var pagePoint = ClampPointToPage(ScreenToPage(screenPoint));
        _canvasContextPastePoint = pagePoint;

        var hit = FindCanvasObjectAt(pagePoint);
        if (hit is not null && SelectedCanvasObjects().All(item => item.Id != hit.Id))
            SelectSingleObject(hit);

        var hasSelection = _selectedTextRegions.Count > 0 || SelectedCanvasObjects().Count > 0;
        _canvasCutMenuItem.IsEnabled = hasSelection;
        _canvasCopyMenuItem.IsEnabled = hasSelection;
        _canvasPasteMenuItem.IsEnabled = ClipboardMayContainPasteData();
        _canvasContextMenu.ShowAt(DrawingSurface, screenPoint);
        e.Handled = true;
    }

    private CanvasObject? FindCanvasObjectAt(PointD point)
    {
        if (_page is null) return null;
        var tolerance = 10 / Math.Max(_zoom, 0.08);
        var candidates = _spatialIndex.Query(new RectD(
            point.X - tolerance,
            point.Y - tolerance,
            tolerance * 2,
            tolerance * 2));
        if (candidates.Count == 0) candidates = _page.Objects;
        return candidates
            .Where(item => !item.IsHidden && StrokeGeometry.HitTest(item, point, tolerance))
            .OrderByDescending(item => item.ZIndex)
            .FirstOrDefault();
    }

    private bool ClipboardMayContainPasteData()
    {
        if (!string.IsNullOrWhiteSpace(_internalClipboard)) return true;
        try
        {
            var view = Clipboard.GetContent();
            return view.Contains(CanvasClipboardFormat) ||
                   view.Contains(StandardDataFormats.Bitmap) ||
                   view.Contains(StandardDataFormats.StorageItems) ||
                   view.Contains(StandardDataFormats.Text);
        }
        catch
        {
            return false;
        }
    }

    private void OnCanvasCutContextClick(object sender, RoutedEventArgs e) => OnCutClick(sender, e);

    private void OnCanvasCopyContextClick(object sender, RoutedEventArgs e) => CopySelectionToClipboard();

    private async void OnCanvasPasteContextClick(object sender, RoutedEventArgs e) =>
        await PasteSelectionAsync(_canvasContextPastePoint);

    private void OnFolderTreeRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var container = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        var node = container is null ? null : FolderTree.NodeFromContainer(container);
        if (GetLibraryEntry(node) is not { } entry) return;
        if (entry.Document is { } document)
        {
            _notebookContextTarget = document;
            _removeNotebookFolderMenuItem.IsEnabled = DocumentFolderId(document.Id) is not null;
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
                _hostWindow?.UpdateNotebookTitle(name);
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
        if (!RefreshFolderNodeContent(folderId)) RebuildFolderTree(_selectedFolderId);
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
        _userPreferences.FolderThumbnails.Remove(folderId.ToString("D"));
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
        var input = new TextBox
        {
            Header = header,
            Text = currentValue,
            MaxLength = LibraryNamePolicy.MaxLength
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return LibraryNamePolicy.Normalize(input.Text);
    }

    private async Task LoadDocumentAsync(Guid id, Guid? pageId = null)
    {
        if (_repository is null) return;
        if (_document?.Id == id)
        {
            if (pageId is { } selectedPageId &&
                _document.Pages.FirstOrDefault(page => page.Id == selectedPageId) is { } selectedPage)
                PageList.SelectedItem = selectedPage;
            return;
        }

        _documentLoadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _documentLoadCancellation = cancellation;
        var lockTaken = false;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var homeCancellation in _homeThumbnailLoads.Values) homeCancellation.Cancel();
        _homeThumbnailLoads.Clear();
        try
        {
            await _documentLoadGate.WaitAsync(cancellation.Token);
            lockTaken = true;
            await SaveNowAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (_document is not null) CacheOpenDocument(_document);
            var fromCache = _openDocumentCache.TryGetValue(id, out var cached);
            var loaded = cached ?? await _repository.LoadAsync(id, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (loaded is null) return;
            if (!_documentHistories.TryGetValue(id, out var history))
                _documentHistories[id] = history = new CommandHistory();
            _history = history;
            _document = loaded;
            var inkPointCount = CountInkPoints(loaded);
            CacheOpenDocument(loaded, inkPointCount);
            UpdateFolderActions();
            _pendingInkAppends.Clear();
            _requiresFullSave = false;
            _hasUnsavedChanges = false;
            NotebookTitle.Text = loaded.Title;
            _hostWindow?.UpdateNotebookTitle(loaded.Title);
            EnsureNotebookTab(loaded.Id, loaded.Title);
            _loading = true;
            _pages.Clear();
            foreach (var page in loaded.Pages) _pages.Add(page);
            _loading = false;
            var selectedIndex = pageId is null ? 0 : loaded.Pages.FindIndex(page => page.Id == pageId);
            selectedIndex = Math.Max(0, selectedIndex);
            var selectedPage = loaded.Pages.ElementAtOrDefault(selectedIndex);
            StartNotebookPagePreviewPreload(loaded, selectedPage);
            PageList.SelectedIndex = selectedIndex;
            SelectPage(selectedPage);
            ScheduleHomeThumbnailCacheRefresh(loaded);
            var elapsed = MillisecondsSince(started);
            DiagnosticsLog.Info("document.load_completed",
                ("document_id", id),
                ("source", fromCache ? "cache" : "database"),
                ("pages", loaded.Pages.Count),
                ("ink_points", inkPointCount),
                ("elapsed_ms", elapsed));
        }
        catch (OperationCanceledException)
        {
            DiagnosticsLog.Info("document.load_cancelled",
                ("document_id", id),
                ("elapsed_ms", MillisecondsSince(started)));
        }
        finally
        {
            if (lockTaken) _documentLoadGate.Release();
            if (ReferenceEquals(_documentLoadCancellation, cancellation))
                _documentLoadCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SeedPageIndexState(HoomNoteDocument document)
    {
        foreach (var page in document.Pages)
        {
            var hasPersistedIndex = !string.IsNullOrWhiteSpace(page.RecognizedText) ||
                                    page.RecognizedRegions.Count > 0;
            var hasIndexableContent = page.ImportedLayer is not null ||
                                      page.Objects.Any(item =>
                                          item is ImageObject { IsHidden: false } ||
                                          item is InkStrokeObject
                                          {
                                              IsHidden: false,
                                              Style.Tool: not InkToolKind.Highlighter,
                                              Points.Count: > 1
                                          });
            if (hasPersistedIndex || !hasIndexableContent)
                _pageOcrIndexedThisSession.Add(page.Id);
        }
    }

    private void EnsureNotebookTab(Guid id, string title)
    {
        var existing = NotebookTabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(item => item.Tag is Guid tabId && tabId == id);
        if (existing is null)
        {
            existing = new TabViewItem { Tag = id, IsClosable = true };
            existing.PointerPressed += OnNotebookTabPointerPressed;
            ConfigureNotebookTabContextMenu(existing, id);
            NotebookTabs.TabItems.Add(existing);
        }
        ApplyNotebookTabAppearance(existing, id, title);
        ConfigureNotebookTabContextMenu(existing, id);
        _updatingTabs = true;
        NotebookTabs.SelectedItem = existing;
        _updatingTabs = false;
    }

    private void RefreshNotebookTabHeaders()
    {
        foreach (var tab in NotebookTabs.TabItems.OfType<TabViewItem>())
        {
            if (tab.Tag is not Guid documentId) continue;
            var title = _allDocuments.FirstOrDefault(document => document.Id == documentId)?.Title ??
                        (_document?.Id == documentId ? _document.Title : "Notebook");
            ApplyNotebookTabAppearance(tab, documentId, title);
        }
    }

    private void ApplyNotebookTabAppearance(TabViewItem tab, Guid documentId, string title)
    {
        var color = ParseColor(EffectiveDocumentColor(documentId));
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(color)
        });
        header.Children.Add(new TextBlock
        {
            Text = title,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        tab.Header = header;
        tab.Background = new SolidColorBrush(Color.FromArgb(36, color.R, color.G, color.B));
        tab.BorderBrush = new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B));
        ToolTipService.SetToolTip(tab, title);
    }

    private void OnNotebookTabPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not TabViewItem { Tag: Guid documentId } tab ||
            !ReferenceEquals(NotebookTabs.SelectedItem, tab) ||
            _document?.Id != documentId) return;
        var point = e.GetCurrentPoint(tab);
        if (point.PointerDeviceType == PointerDeviceType.Mouse &&
            !point.Properties.IsLeftButtonPressed) return;
        SelectLibraryDocument(documentId, revealInLibrary: true);
    }

    internal bool ContainsNotebookTab(Guid documentId) =>
        NotebookTabs.TabItems.OfType<TabViewItem>()
            .Any(item => item.Tag is Guid id && id == documentId);

    private void ConfigureNotebookTabContextMenu(TabViewItem tab, Guid documentId)
    {
        var flyout = new MenuFlyout();
        var action = new MenuFlyoutItem
        {
            Text = _isPrimaryWindow ? "Open in new window" : "Move to main window",
            Tag = documentId,
            Icon = new FontIcon { Glyph = "\uE8A7" }
        };
        if (_isPrimaryWindow) action.Click += OnOpenTabInNewWindowClick;
        else action.Click += OnMoveTabToMainWindowClick;
        flyout.Items.Add(action);
        tab.ContextFlyout = flyout;
    }

    private void OnNotebookTabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (args.Tab?.Tag is not Guid documentId)
        {
            args.Cancel = true;
            return;
        }

        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetData(NotebookTabDataFormat, documentId.ToString("D"));
    }

    private async void OnNotebookTabDroppedOutside(TabView sender, TabViewTabDroppedOutsideEventArgs args)
    {
        if (args.Tab?.Tag is not Guid documentId) return;
        try
        {
            await DetachNotebookAsync(documentId);
        }
        catch (Exception exception)
        {
            ShowError("The notebook could not be opened in a new window.", exception);
        }
    }

    private void OnNotebookTabStripDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(NotebookTabDataFormat)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
    }

    private async void OnNotebookTabStripDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(NotebookTabDataFormat)) return;
        try
        {
            var raw = await e.DataView.GetDataAsync(NotebookTabDataFormat) as string;
            if (!Guid.TryParse(raw, out var documentId)) return;
            var source = App.FindPageHostingNotebook(documentId, this);
            if (source is null || ReferenceEquals(source, this)) return;

            await source.PrepareNotebookTransferAsync(documentId);
            await OpenTransferredNotebookAsync(documentId);
            await source.RemoveNotebookTabAfterTransferAsync(documentId);
            e.AcceptedOperation = DataPackageOperation.Move;
            e.Handled = true;
        }
        catch (Exception exception)
        {
            ShowError("The notebook could not be moved into this window.", exception);
        }
    }

    private async void OnOpenTabInNewWindowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Guid documentId }) return;
        try
        {
            await DetachNotebookAsync(documentId);
        }
        catch (Exception exception)
        {
            ShowError("The notebook could not be opened in a new window.", exception);
        }
    }

    private async void OnMoveTabToMainWindowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Guid documentId }) return;
        try
        {
            var primaryWindow = App.PrimaryWindow;
            var primaryPage = primaryWindow?.MainPage;
            if (primaryPage is null || ReferenceEquals(primaryPage, this)) return;

            await PrepareNotebookTransferAsync(documentId);
            await primaryPage.OpenTransferredNotebookAsync(documentId);
            await RemoveNotebookTabAfterTransferAsync(documentId);
            primaryWindow!.Activate();
        }
        catch (Exception exception)
        {
            ShowError("The notebook could not be moved to the main window.", exception);
        }
    }

    private async Task DetachNotebookAsync(Guid documentId)
    {
        await PrepareNotebookTransferAsync(documentId);
        App.OpenDetachedNotebookWindow(documentId);
        await RemoveNotebookTabAfterTransferAsync(documentId);
    }

    private async Task PrepareNotebookTransferAsync(Guid documentId)
    {
        if (_document?.Id == documentId)
            await SaveNowAsync();
    }

    internal async Task OpenTransferredNotebookAsync(Guid documentId)
    {
        if (_repository is null) return;
        await LoadDocumentAsync(documentId,
            _tabPageSelections.GetValueOrDefault(documentId) is { } pageId && pageId != Guid.Empty
                ? pageId
                : null);
        SelectLibraryDocument(documentId);
        _hostWindow?.Activate();
    }

    private async Task RemoveNotebookTabAfterTransferAsync(Guid documentId)
    {
        var tab = NotebookTabs.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(item => item.Tag is Guid id && id == documentId);
        if (tab is null) return;

        var wasSelected = ReferenceEquals(NotebookTabs.SelectedItem, tab);
        _updatingTabs = true;
        NotebookTabs.TabItems.Remove(tab);
        _updatingTabs = false;
        _tabPageSelections.Remove(documentId);
        if (!wasSelected)
        {
            RemoveCachedDocument(documentId);
            return;
        }

        if (NotebookTabs.TabItems.OfType<TabViewItem>()
                .FirstOrDefault(item => item.Tag is Guid) is { Tag: Guid nextDocumentId } nextTab)
        {
            _updatingTabs = true;
            NotebookTabs.SelectedItem = nextTab;
            _updatingTabs = false;
            await LoadDocumentAsync(nextDocumentId);
            RemoveCachedDocument(documentId);
            SelectLibraryDocument(nextDocumentId);
            return;
        }

        ClearCurrentNotebook(documentId);
        if (!_isPrimaryWindow) _hostWindow?.Close();
    }

    private void ClearCurrentNotebook(Guid documentId)
    {
        ResetNotebookPagePreviews();
        _document = null;
        _page = null;
        _pages.Clear();
        _documentHistories.Remove(documentId);
        RemoveCachedDocument(documentId);
        NotebookTitle.Text = "No notebook";
        _hostWindow?.UpdateNotebookTitle(null);
        SelectPage(null);
        UpdateFolderActions();
        RebuildHomeLibrary();
        UpdateEmptyState();
    }

    private async void OnHomeLibraryButtonClick(object sender, RoutedEventArgs e)
    {
        _homeFolderId = null;
        if (_document is null)
        {
            RebuildHomeLibrary();
            UpdateEmptyState();
            StatusText.Text = "Home library";
            return;
        }

        try
        {
            CommitOrDiscardTextEditor();
            await SaveNowAsync();
            if (_document is null) return;
            if (_page is not null) _tabPageSelections[_document.Id] = _page.Id;

            _updatingTabs = true;
            NotebookTabs.SelectedItem = null;
            _updatingTabs = false;
            _document = null;
            _page = null;
            _pages.Clear();
            NotebookTitle.Text = "No notebook";
            _hostWindow?.UpdateNotebookTitle(null);
            SelectPage(null);
            UpdateFolderActions();
            await RefreshLibraryAsync();
            StatusText.Text = "Home library";
        }
        catch (Exception exception)
        {
            ShowError("The home library could not be opened.", exception);
        }
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
        if (args.Item is not TabViewItem { Tag: Guid closingId } tab) return;
        var wasSelected = ReferenceEquals(sender.SelectedItem, tab);
        if (!wasSelected)
        {
            sender.TabItems.Remove(tab);
            _tabPageSelections.Remove(closingId);
            RemoveCachedDocument(closingId);
            return;
        }

        var nextTab = sender.TabItems.OfType<TabViewItem>()
            .FirstOrDefault(item => !ReferenceEquals(item, tab) && item.Tag is Guid);
        var saveTask = SaveNowAsync();
        _updatingTabs = true;
        sender.TabItems.Remove(tab);
        if (nextTab is not null) sender.SelectedItem = nextTab;
        _updatingTabs = false;
        _tabPageSelections.Remove(closingId);
        StatusText.Text = "Closing notebook…";
        await Task.Yield();
        try
        {
            await saveTask;
            if (nextTab?.Tag is Guid nextDocumentId)
            {
                await LoadDocumentAsync(nextDocumentId,
                    _tabPageSelections.GetValueOrDefault(nextDocumentId) is { } pageId &&
                    pageId != Guid.Empty ? pageId : null);
                RemoveCachedDocument(closingId);
                SelectLibraryDocument(nextDocumentId);
            }
            else
            {
                ClearCurrentNotebook(closingId);
                StatusText.Text = "No notebook open";
            }
        }
        catch (Exception exception)
        {
            ShowError("The notebook could not be closed cleanly.", exception);
        }
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
        // The game-loop renderer owns retained GPU resources. Page switches publish an
        // invalidation instead of moving render targets on the UI thread.
        InvalidatePageRenderCache();
        _searchLocateCancellation?.Cancel();
        _searchFlashBounds.Clear();
        _searchFlashStarted = 0;
        _eraseDirtyRegions.Clear();
        Volatile.Write(ref _erasePreviewCommitVersion, -1);
        Volatile.Write(ref _transformPreviewCommitVersion, -1);
        _pendingInkCommitPreviews.Clear();
        Volatile.Write(ref _inkPreviewCommitVersion, -1);
        _page = page;
        // Retain bounded stroke geometry across page switches. Object ids are document-unique,
        // replacements self-invalidate by reference, and the LRU/point budgets prune old pages.
        // Clearing here made every return to a dense page rebuild its vector geometry.
        _selectedObject = null;
        _selectedObjects.Clear();
        ClearTextSelection();
        _multiTransformPreviews.Clear();
        _transformPreview = null;
        _selectionTransformOriginalIds.Clear();
        _selectionTransformSourceBounds = null;
        _fitPending = true;
        _pan = Vector2.Zero;
        PrepareSpatialIndex(page);
        SyncTemplatePicker();
        UpdateSelectionUi();
        UpdateLayerUi();
        DeletePageButton.IsEnabled = page is not null;
        BeginPdfPreviewLoad();
        // Preserve native PDF text selection/copy. This extracts embedded text and is not OCR.
        BeginSemanticTextLoad(page);
        RequestVisiblePagePreviews(page);
        if (page is not null)
        {
            lock (_pageRenderGate)
                if (_notebookPagePreviews.ContainsKey(page.Id))
                    BeginNavigationSettle();
        }
        UpdateEmptyState();
        InvalidateCanvas();
    }

    private void ResetNotebookPagePreviews()
    {
        var cancellation = _notebookPagePreviewCancellation;
        _notebookPagePreviewCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        _notebookPagePreviewGeneration++;
        _notebookPagePreviewLoads.Clear();
        _notebookPagePreviewRefreshPending.Clear();
        lock (_pageRenderGate) DisposeNotebookPagePreviewsCore();
    }

    private void StartNotebookPagePreviewPreload(
        HoomNoteDocument document,
        NotePage? selectedPage)
    {
        ResetNotebookPagePreviews();
        if (document.Kind == DocumentKind.InfiniteCanvas || _pageThumbnailRenderer is null ||
            document.Pages.Count == 0) return;

        var generation = _notebookPagePreviewGeneration;
        var cancellation = _notebookPagePreviewCancellation = new CancellationTokenSource();
        _notebookPagePreviewLongEdge = Math.Clamp(
            (int)Math.Floor(Math.Sqrt(
                NotebookPagePreviewByteBudget / (4d * document.Pages.Count))),
            512,
            AdjacentPagePreviewLongEdge);
        var selectedIndex = selectedPage is null
            ? 0
            : Math.Max(0, document.Pages.FindIndex(page => page.Id == selectedPage.Id));
        var orderedPages = document.Pages
            .Select((page, index) => (Page: page, Index: index))
            .OrderBy(item => Math.Abs(item.Index - selectedIndex))
            .ThenBy(item => item.Index)
            .Select(item => item.Page)
            .ToArray();
        _ = PreloadNotebookPagePreviewsAsync(
            document.Id, orderedPages, generation, cancellation.Token);
    }

    private async Task PreloadNotebookPagePreviewsAsync(
        Guid documentId,
        IReadOnlyList<NotePage> pages,
        int generation,
        CancellationToken cancellationToken)
    {
        var loadedCount = 0;
        try
        {
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await EnsureNotebookPagePreviewAsync(page, generation, cancellationToken);
                loadedCount++;
            }
            if (_document?.Id == documentId && generation == _notebookPagePreviewGeneration)
            {
                DiagnosticsLog.Info("page.previews_ready",
                    ("document_id", documentId), ("pages", loadedCount));
                StatusText.Text = "Ready • pages preloaded";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Warning("page.preview_preload_failed",
                ("document_id", documentId), ("error", exception.Message));
        }
    }

    private void RequestVisiblePagePreviews(NotePage? page)
    {
        if (_document is null || page is null ||
            _document.Kind == DocumentKind.InfiniteCanvas || _pageThumbnailRenderer is null)
            return;
        if (_notebookPagePreviewCancellation is null)
            StartNotebookPagePreviewPreload(_document, page);

        var generation = _notebookPagePreviewGeneration;
        var token = _notebookPagePreviewCancellation?.Token ?? CancellationToken.None;
        var index = _document.Pages.FindIndex(item => item.Id == page.Id);
        if (index < 0) return;
        for (var distance = 0; distance <= NotebookPagePreviewLookAhead; distance++)
        {
            var previous = index - distance;
            var next = index + distance;
            if (previous >= 0)
                _ = EnsureNotebookPagePreviewAsync(_document.Pages[previous], generation, token);
            if (distance > 0 && next < _document.Pages.Count)
                _ = EnsureNotebookPagePreviewAsync(_document.Pages[next], generation, token);
        }
    }

    private Task EnsureNotebookPagePreviewAsync(
        NotePage page,
        int generation,
        CancellationToken cancellationToken,
        bool refresh = false)
    {
        lock (_pageRenderGate)
            if (!refresh && _notebookPagePreviews.ContainsKey(page.Id))
                return Task.CompletedTask;
        if (_notebookPagePreviewLoads.TryGetValue(page.Id, out var existing))
        {
            if (refresh) _notebookPagePreviewRefreshPending.Add(page.Id);
            return existing;
        }
        var task = LoadNotebookPagePreviewAsync(page, generation, cancellationToken);
        _notebookPagePreviewLoads[page.Id] = task;
        return task;
    }

    private async Task LoadNotebookPagePreviewAsync(
        NotePage page,
        int generation,
        CancellationToken cancellationToken)
    {
        AdjacentPagePreview? preview = null;
        try
        {
            preview = await RenderNotebookPagePreviewAsync(page, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (preview is null || generation != _notebookPagePreviewGeneration) return;
            lock (_pageRenderGate)
            {
                if (generation != _notebookPagePreviewGeneration) return;
                if (_notebookPagePreviews.Remove(page.Id, out var previous))
                    previous.Bitmap.Dispose();
                _notebookPagePreviews[page.Id] = preview;
                preview = null;
            }
            InvalidateCanvas();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Warning("page.preview_failed",
                ("page_id", page.Id), ("error", exception.Message));
        }
        finally
        {
            preview?.Bitmap.Dispose();
            _notebookPagePreviewLoads.Remove(page.Id);
            if (_notebookPagePreviewRefreshPending.Remove(page.Id) &&
                generation == _notebookPagePreviewGeneration && !cancellationToken.IsCancellationRequested)
                _ = EnsureNotebookPagePreviewAsync(page, generation, cancellationToken, refresh: true);
        }
    }

    private async Task<AdjacentPagePreview?> RenderNotebookPagePreviewAsync(
        NotePage? page,
        CancellationToken cancellationToken)
    {
        if (page is null || _pageThumbnailRenderer is null) return null;
        var scale = _notebookPagePreviewLongEdge /
                    Math.Max(1d, Math.Max(page.Size.Width, page.Size.Height));
        var pixelWidth = Math.Max(1, (int)Math.Round(page.Size.Width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(page.Size.Height * scale));
        var bytes = await _pageThumbnailRenderer.RenderAsync(
            page, pixelWidth, pixelHeight, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = await CanvasBitmap.LoadAsync(PageSurface.Device, stream);
        cancellationToken.ThrowIfCancellationRequested();
        return new AdjacentPagePreview(page.Id, page.Size, bitmap);
    }

    private void DisposeNotebookPagePreviewsCore()
    {
        foreach (var preview in _notebookPagePreviews.Values)
            preview.Bitmap.Dispose();
        _notebookPagePreviews.Clear();
    }

    private void BeginSemanticTextLoad(NotePage? page)
    {
        var document = _document;
        if (document is null || page?.ImportedLayer is not { } imported || _assetStore is null ||
            page.RecognizedRegions.Any(region =>
                string.Equals(region.Source, "Pdf", StringComparison.OrdinalIgnoreCase)) ||
            !_semanticTextLoads.Add(page.Id)) return;
        _ = LoadSemanticTextAsync(document, page, imported);
    }

    private async Task LoadSemanticTextAsync(
        HoomNoteDocument document,
        NotePage page,
        ImportedDocumentLayer imported)
    {
        try
        {
            if (_assetStore is null) return;
            var path = _assetStore.GetPath(imported.AssetHash);
            var regions = await Task.Run(() => PdfSemanticTextExtractor.ExtractPage(
                path, imported.SourcePageIndex, page.Size, imported.Transform));
            if (regions.Count == 0) return;
            var existing = page.RecognizedRegions.Where(region =>
                !string.Equals(region.Source, "Pdf", StringComparison.OrdinalIgnoreCase));
            var merged = MergeRecognizedRegions(existing, regions);
            var sourceText = SelectedRegionText(regions);
            var recognizedText = string.Join(Environment.NewLine,
                new[] { sourceText, page.RecognizedText }
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            await PersistRecognizedTextAsync(document, page, recognizedText, merged, CancellationToken.None);
            if (_page?.Id == page.Id) InvalidateCanvas();
        }
        catch (Exception exception)
        {
            DiagnosticsLog.Error("pdf.semantic_text_failed", exception,
                ("page", page.Id), ("source_page", imported.SourcePageIndex));
        }
        finally
        {
            _semanticTextLoads.Remove(page.Id);
        }
    }

    private void BeginPdfPreviewLoad(NotePage? requestedPage = null)
    {
        var layer = (requestedPage ?? _page)?.ImportedLayer;
        if (layer is null || _assetStore is null) return;
        _ = LoadPdfPreviewAsync(_assetStore.GetPath(layer.AssetHash), layer.SourcePageIndex);
    }

    private void RequestPdfPreviewLoad(NotePage page)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            BeginPdfPreviewLoad(page);
            return;
        }
        if (Interlocked.Exchange(ref _queuedPdfLoadRequest, 1) != 0) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _queuedPdfLoadRequest, 0);
            BeginPdfPreviewLoad(page);
        });
    }

    private async Task LoadPdfPreviewAsync(string path, int pageIndex)
    {
        try
        {
            await _pdfPreview.EnsureLoadedAsync(path, pageIndex);
            DispatcherQueue.TryEnqueue(InvalidateCanvas);
        }
        catch (Exception exception)
        {
            DispatcherQueue.TryEnqueue(() => ShowError("The imported page preview could not be rendered.", exception));
        }
    }

    private void OnDrawingSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _canvasWidth = Math.Max(1, e.NewSize.Width);
        _canvasHeight = Math.Max(1, e.NewSize.Height);
        _canvasDpi = DrawingSurface.Dpi;
        InvalidateCanvas();
    }

    private void EnsureFitViewport()
    {
        if (!_fitPending || _page is null || _canvasWidth <= 0 || _canvasHeight <= 0) return;
        _zoom = Math.Min(1, Math.Min(
            (_canvasWidth - 96) / _page.Size.Width,
            (_canvasHeight - 128) / _page.Size.Height));
        _zoom = Math.Clamp(_zoom, _minimumZoom, _maximumZoom);
        _fitPending = false;
        UpdateZoomText();
    }

    private void InvalidateCanvas()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(InvalidateCanvas);
            return;
        }

        EnsureFitViewport();
        ClampHorizontalPan();
        _canvasDpi = DrawingSurface.Dpi;
        PublishPageRenderState();
        // Invalidate is nonblocking. The committed page consumes the latest published viewport
        // on the Win2D game-loop thread while the UI-thread overlay remains responsive.
        PageSurface.Invalidate();
        DrawingSurface.Invalidate();
    }

    private void InvalidateInteractionOverlay()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(InvalidateInteractionOverlay);
            return;
        }
        DrawingSurface.Invalidate();
    }

    private void PublishPageRenderState()
    {
        if (_page is null)
        {
            _publishedPageSnapshot = null;
            _publishedPageEditVersion = _editVersion;
        }
        else if (_publishedPageSnapshot?.Id != _page.Id || _publishedPageEditVersion != _editVersion)
        {
            // Canvas objects and their point arrays are immutable after command commit. Copying
            // only the page shell/list gives the render thread a stable scene in O(object count)
            // per edit, never per pointer frame and never O(sample count).
            _publishedPageSnapshot = _page with
            {
                Template = _page.Template with { },
                ImportedLayer = _page.ImportedLayer is null ? null : _page.ImportedLayer with { },
                Objects = _page.Objects.ToList()
            };
            _publishedPageEditVersion = _editVersion;
        }

        // Ink, eraser, selection, and style gestures render exclusively on the
        // interaction overlay. Only actual viewport navigation may select the
        // low-memory navigation snapshot for the committed page.
        var interactionActive = _isPointerDown ||
                                _touchPoints.Count > 0 ||
                                _touchInertiaActive || _wheelZoomAnimating || _wheelScrollAnimating ||
                                _zoomNavigationActive;
        Volatile.Write(ref _publishedPageRenderState, new PageRenderState(
            _publishedPageSnapshot,
            _zoom,
            _pan,
            _canvasWidth,
            _canvasHeight,
            _canvasDpi,
            interactionActive,
            _publishedPageEditVersion));
    }

    private void OnCanvasCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        // Both surfaces share one device. The overlay owns only ephemeral interaction resources;
        // committed page resources are rebuilt by the dedicated game-loop surface.
        ClearLiveInkGeometryCache();
    }

    private void OnPageSurfaceCreateResources(
        CanvasAnimatedControl sender,
        CanvasCreateResourcesEventArgs args)
    {
        lock (_pageRenderGate)
        {
            Interlocked.Exchange(ref _pageRenderInvalidationRequested, 0);
            Interlocked.Exchange(ref _strokeGeometryClearRequested, 0);
            Interlocked.Exchange(ref _navigationTileClearRequested, 0);
            InvalidatePageRenderCacheCore();
            ClearStrokeGeometryCacheCore();
            ClearImageBitmapCacheCore();
        }
    }

    private void OnPageSurfaceDraw(
        ICanvasAnimatedControl sender,
        CanvasAnimatedDrawEventArgs args)
    {
        lock (_pageRenderGate)
        {
            ApplyPendingPageRenderInvalidations();
            var state = Volatile.Read(ref _publishedPageRenderState);
            if (state.Page is not { } page) return;
            DrainPageRenderAppends(page.Id);
            var frameStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _frameStrokeGeometryBuilds = 0;
            _frameNavigationTileBuilds = 0;
            _frameRenderMode = "none";

            var drawingSession = args.DrawingSession;
            drawingSession.Transform = Matrix3x2.Identity;
            DrawAdjacentPagePreviews(drawingSession, page, state);
            drawingSession.Transform = PageTransform(
                page, state.Zoom, state.Pan, state.Width, state.Height);
            if (ShouldDrawInteractiveViewport(page, state))
            {
                _frameRenderMode = "viewport-vectors";
                DrawInteractiveViewport(drawingSession, page, state);
            }
            else
            {
                DrawCachedPage(PageSurface.Device, drawingSession, page, state);
            }
            RecordCanvasFrame(frameStarted);
            RequestErasePreviewRetire(state.EditVersion);
            RequestTransformPreviewRetire(state.EditVersion);
            RequestInkPreviewRetire(state.EditVersion);
        }
    }

    private void DrawAdjacentPagePreviews(
        CanvasDrawingSession drawingSession,
        NotePage page,
        PageRenderState state)
    {
        if (_document is not { Kind: not DocumentKind.InfiniteCanvas } document) return;
        var index = document.Pages.FindIndex(candidate => candidate.Id == page.Id);
        if (index < 0) return;
        var viewport = new SizeD(state.Width, state.Height);
        var currentBounds = ContinuousPageLayout.CurrentBounds(
            page.Size, state.Zoom, state.Pan.X, state.Pan.Y, viewport);
        if (index > 0 &&
            _notebookPagePreviews.TryGetValue(document.Pages[index - 1].Id, out var previous))
        {
            var bounds = ContinuousPageLayout.AdjacentBounds(
                currentBounds, previous.PageSize, state.Zoom, state.Pan.X, viewport,
                aboveCurrentPage: true, ContinuousPageGap);
            DrawAdjacentPagePreview(
                drawingSession, document.Pages[index - 1], previous, bounds, state);
        }
        if (index + 1 < document.Pages.Count &&
            _notebookPagePreviews.TryGetValue(document.Pages[index + 1].Id, out var next))
        {
            var bounds = ContinuousPageLayout.AdjacentBounds(
                currentBounds, next.PageSize, state.Zoom, state.Pan.X, viewport,
                aboveCurrentPage: false, ContinuousPageGap);
            DrawAdjacentPagePreview(
                drawingSession, document.Pages[index + 1], next, bounds, state);
        }
    }

    private void DrawAdjacentPagePreview(
        CanvasDrawingSession drawingSession,
        NotePage page,
        AdjacentPagePreview preview,
        RectD bounds,
        PageRenderState state)
    {
        if (bounds.Y >= state.Height || bounds.Bottom <= 0) return;
        drawingSession.DrawImage(preview.Bitmap,
            new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        if (!_temporaryGridVisible) return;
        var previousTransform = drawingSession.Transform;
        drawingSession.Transform = Matrix3x2.CreateScale((float)state.Zoom) *
                                   Matrix3x2.CreateTranslation((float)bounds.X, (float)bounds.Y);
        DrawTemporaryGrid(drawingSession, page);
        drawingSession.Transform = previousTransform;
    }

    private void RequestErasePreviewRetire(int renderedEditVersion)
    {
        var targetVersion = Volatile.Read(ref _erasePreviewCommitVersion);
        if (targetVersion < 0 || renderedEditVersion < targetVersion ||
            Interlocked.Exchange(ref _erasePreviewRetireQueued, 1) != 0) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _erasePreviewRetireQueued, 0);
            var currentTarget = Volatile.Read(ref _erasePreviewCommitVersion);
            if (_isPointerDown || currentTarget < 0 || renderedEditVersion < currentTarget) return;
            _eraseDirtyRegions.Clear();
            Volatile.Write(ref _erasePreviewCommitVersion, -1);
            DrawingSurface.Invalidate();
        });
    }

    private void RequestTransformPreviewRetire(int renderedEditVersion)
    {
        var targetVersion = Volatile.Read(ref _transformPreviewCommitVersion);
        if (targetVersion < 0 || renderedEditVersion < targetVersion ||
            Interlocked.Exchange(ref _transformPreviewRetireQueued, 1) != 0) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _transformPreviewRetireQueued, 0);
            var currentTarget = Volatile.Read(ref _transformPreviewCommitVersion);
            if (_isPointerDown || currentTarget < 0 || renderedEditVersion < currentTarget) return;
            ClearTransformPreviewState();
            Volatile.Write(ref _transformPreviewCommitVersion, -1);
            DrawingSurface.Invalidate();
        });
    }

    private void RequestInkPreviewRetire(int renderedEditVersion)
    {
        var targetVersion = Volatile.Read(ref _inkPreviewCommitVersion);
        if (targetVersion < 0 || renderedEditVersion < targetVersion ||
            Interlocked.Exchange(ref _inkPreviewRetireQueued, 1) != 0) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _inkPreviewRetireQueued, 0);
            var currentTarget = Volatile.Read(ref _inkPreviewCommitVersion);
            if (currentTarget < 0 || renderedEditVersion < currentTarget) return;
            _pendingInkCommitPreviews.RemoveAll(item => item.Version <= renderedEditVersion);
            Volatile.Write(ref _inkPreviewCommitVersion,
                _pendingInkCommitPreviews.Count == 0
                    ? -1
                    : _pendingInkCommitPreviews.Max(item => item.Version));
            DrawingSurface.Invalidate();
        });
    }

    private void ApplyPendingPageRenderInvalidations()
    {
        var clearedPage = Interlocked.Exchange(ref _pageRenderInvalidationRequested, 0) != 0;
        if (clearedPage)
            InvalidatePageRenderCacheCore();
        else if (Interlocked.Exchange(ref _navigationTileClearRequested, 0) != 0)
            ClearNavigationTileCacheCore();
        if (Interlocked.Exchange(ref _strokeGeometryClearRequested, 0) != 0)
            ClearStrokeGeometryCacheCore();
    }

    private void DrainPageRenderAppends(Guid pageId)
    {
        while (_pendingPageRenderAppends.TryDequeue(out var pending))
        {
            if (pageId != pending.PageId) continue;
            var canKeepCache =
                ((_pageRenderCache is not null && _pageRenderCachePageId == pending.PageId) ||
                 (_lowZoomPageRaster is not null && _lowZoomPageRasterPageId == pending.PageId) ||
                 (_navigationTilePageId == pending.PageId && _navigationTiles.Count > 0)) &&
                !_pageRenderCacheObjectIds.Contains(pending.Object.Id);
            if (canKeepCache)
            {
                _pageRenderOverlays.Add(pending.Object);
                continue;
            }

            // The document already contains the append. Rebuilding now records the current
            // source of truth, so remaining queued appends for this page need no special case.
            InvalidatePageRenderCacheCore();
            while (_pendingPageRenderAppends.TryPeek(out var next) && next.PageId == pending.PageId)
                _pendingPageRenderAppends.TryDequeue(out _);
            break;
        }
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_page is null) return;
        var drawingSession = args.DrawingSession;
        drawingSession.Transform = PageTransform();
        var styleBrushPreview = _isPointerDown && _gestureTool == EditorTool.Style && !_styleToolPickMode;
        var selectionTransformPreview =
            (_isPointerDown && _gestureTool == EditorTool.Select) ||
            Volatile.Read(ref _transformPreviewCommitVersion) >= 0;

        if (_eraseDirtyRegions.Count > 0 &&
            (_gestureTool is EditorTool.SegmentEraser or EditorTool.StrokeEraser ||
             Volatile.Read(ref _erasePreviewCommitVersion) >= 0))
            DrawRealtimeErasePreview(drawingSession, _page);

        if (styleBrushPreview)
        {
            foreach (var preview in _multiTransformPreviews.Values.OrderBy(item => item.ZIndex))
                DrawObject(drawingSession, preview);
            if (_styleBrushPoint is { } brushPoint)
            {
                var radius = (float)(Math.Max(4, _styleBrushSize / 2f) / Math.Max(_zoom, 0.08));
                drawingSession.FillCircle(
                    (float)brushPoint.X, (float)brushPoint.Y, radius,
                    Color.FromArgb(22, 56, 189, 248));
                drawingSession.DrawCircle(
                    (float)brushPoint.X, (float)brushPoint.Y, radius,
                    Color.FromArgb(230, 119, 215, 255),
                    (float)Math.Max(1, 1.5 / Math.Max(_zoom, 0.08)));
            }
        }

        if (_isPointerDown &&
            _gestureTool is EditorTool.SegmentEraser or EditorTool.StrokeEraser &&
            _eraserPath.Count > 0)
        {
            var point = _eraserPath[^1];
            var radius = (float)EraserRadius();
            drawingSession.FillCircle(
                (float)point.X, (float)point.Y, radius,
                Color.FromArgb(28, 255, 255, 255));
            drawingSession.DrawCircle(
                (float)point.X, (float)point.Y, radius,
                Color.FromArgb(225, 255, 255, 255),
                (float)Math.Max(1, 1.5 / Math.Max(_zoom, 0.08)));
        }

        if (selectionTransformPreview)
            DrawSelectionTransformPreview(drawingSession, _page);

        foreach (var pending in _pendingInkCommitPreviews.OrderBy(item => item.Object.ZIndex))
            if (!pending.Object.IsHidden)
                DrawObject(drawingSession, pending.Object);

        if (_activeInk.Count > 0 && _gestureTool is EditorTool.Pen or EditorTool.Highlighter)
        {
            DrawLiveInk(drawingSession);
            DrawLiveInkPrediction(drawingSession);
        }

        if (_isPointerDown && _gestureTool == EditorTool.Shape && _activeInk.Count > 0)
        {
            var start = _activeInk[0].Position;
            var end = _activeInk[^1].Position;
            var shapeKind = SelectedShapeKind();
            DrawObject(drawingSession, new ShapeObject
            {
                Shape = shapeKind, Bounds = ShapeGeometry.BoundsFromDrag(start, end, shapeKind), StrokeColor = _inkColor,
                StrokeWidth = (float)StrokeWidthSlider.Value, StartPoint = start, EndPoint = end
            });
        }

        DrawSearchFlash(drawingSession);
        DrawTextSelection(drawingSession);

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

        DrawPageNumber(drawingSession);
        UpdateSelectionLockOverlay();
    }

    private void DrawPageNumber(CanvasDrawingSession drawingSession)
    {
        if (_page is null || _document?.Kind == DocumentKind.InfiniteCanvas) return;
        var pageIndex = _pages.IndexOf(_page);
        if (pageIndex < 0) return;

        var anchor = PageToScreen(new PointD(_page.Size.Width / 2d, _page.Size.Height));
        var previous = drawingSession.Transform;
        drawingSession.Transform = Matrix3x2.Identity;
        var color = IsDarkColor(_page.Template.PaperColor)
            ? Color.FromArgb(170, 245, 247, 250)
            : Color.FromArgb(145, 31, 35, 42);
        drawingSession.DrawText(
            $"{pageIndex + 1} of {_pages.Count}",
            new Rect(anchor.X - 55, anchor.Y - 21, 110, 16),
            color,
            _pageNumberTextFormat);
        drawingSession.Transform = previous;
    }

    private void DrawTextSelection(CanvasDrawingSession drawingSession)
    {
        if (_selectedTextRegions.Count == 0 && _textSelectionDragBounds is null) return;
        var fill = Color.FromArgb(82, 74, 155, 255);
        var outline = Color.FromArgb(220, 105, 178, 255);
        foreach (var region in _selectedTextRegions)
        {
            var bounds = region.Bounds;
            drawingSession.FillRectangle((float)bounds.X, (float)bounds.Y,
                (float)bounds.Width, (float)bounds.Height, fill);
        }
        if (_textSelectionDragBounds is { } drag && drag.Width > 0 && drag.Height > 0)
            drawingSession.DrawRectangle((float)drag.X, (float)drag.Y,
                (float)drag.Width, (float)drag.Height, outline,
                (float)(1.25 / Math.Max(_zoom, 0.08)));
    }

    private void DrawSelectionTransformPreview(CanvasDrawingSession drawingSession, NotePage page)
    {
        if (_selectionTransformSourceBounds is not { } sourceBounds ||
            _selectionTransformOriginalIds.Count == 0 ||
            (_multiTransformPreviews.Count == 0 && _transformPreview is null)) return;

        // DrawingSurface sits above the retained PageSurface, so first reconstruct the source
        // region without the objects being transformed. This removes the stale retained copy
        // while preserving paper/PDF content, the temporary grid, and overlapping objects.
        using (drawingSession.CreateLayer(1f,
                   new Rect(sourceBounds.X, sourceBounds.Y, sourceBounds.Width, sourceBounds.Height)))
        {
            DrawPageBackground(drawingSession, page, sourceBounds);
            DrawImportedLayer(drawingSession, page);
            if (_temporaryGridVisible) DrawTemporaryGrid(drawingSession, page, sourceBounds);
            foreach (var canvasObject in _spatialIndex.Query(sourceBounds)
                         .Where(item => !_selectionTransformOriginalIds.Contains(item.Id))
                         .OrderBy(item => item.ZIndex))
            {
                if (!canvasObject.IsHidden) DrawObject(drawingSession, canvasObject);
            }
        }

        foreach (var preview in _multiTransformPreviews.Values.OrderBy(item => item.ZIndex))
            DrawObject(drawingSession, preview);
        if (_transformPreview is not null)
            DrawObject(drawingSession, _transformPreview);
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
        var wheelZoomAnimating = _wheelZoomAnimating;
        var renderMode = _frameRenderMode;
        var visibleObjects = _visibleObjects.Count;
        var navigationTiles = _navigationTiles.Count;
        var navigationTileBuilds = _frameNavigationTileBuilds;
        var navigationTileMb = Math.Round(_navigationTileBytes / 1024d / 1024d, 1);
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
            ("visible_objects", visibleObjects),
            ("render_mode", renderMode),
            ("navigation_tiles", navigationTiles),
            ("navigation_tile_builds", navigationTileBuilds),
            ("navigation_tile_mb", navigationTileMb),
            ("wheel_zoom", wheelZoomAnimating),
            ("active_ink_points", activeInkPoints),
            ("overlay_objects", overlayCount),
            ("overlay_batches", overlayBatchCount)));
    }

    private void DrawCachedPage(CanvasDevice device, CanvasDrawingSession drawingSession, NotePage page,
        PageRenderState state)
    {
        // Normal reading and navigation should be a single textured quad, not a replay of every
        // vector command on every pan frame. Vector objects remain the source of truth and are
        // used again once the user zooms in far enough to benefit from their extra resolution.
        // Every scene revision first records a complete fallback. Tile refinement is never
        // presented against an empty surface or an obsolete pre-undo page.
        var retainedPageReady =
            (_lowZoomPageRaster is not null && _lowZoomPageRasterPageId == page.Id) ||
            (_pageRenderCache is not null && _pageRenderCachePageId == page.Id);
        if (!retainedPageReady && _preloadedFallbackPageId != page.Id &&
            _notebookPagePreviews.TryGetValue(page.Id, out var preloadedPreview))
        {
            drawingSession.DrawImage(preloadedPreview.Bitmap,
                new Rect(0, 0, page.Size.Width, page.Size.Height));
            if (_temporaryGridVisible) DrawTemporaryGrid(drawingSession, page);
            _frameRenderMode = "preloaded-page";
            _preloadedFallbackPageId = page.Id;
            DispatcherQueue.TryEnqueue(() => PageSurface.Invalidate());
            return;
        }

        EnsureLowZoomPageRaster(device, page);
        var useLowZoomRaster = CanUseNavigationSnapshot(page, state);
        if (useLowZoomRaster)
        {
            _frameRenderMode = "page-raster";
        }
        else
        {
            DrawNavigationTiles(device, drawingSession, page, state);
            _frameRenderMode = "native-tiles";
        }
        while (!state.InteractionActive && _pageRenderOverlays.Count >= OverlayBatchSize)
        {
            var batch = new CanvasCommandList(device);
            using (var batchSession = batch.CreateDrawingSession())
            {
                for (var index = 0; index < OverlayBatchSize; index++)
                    DrawObject(
                        batchSession, _pageRenderOverlays[index], cacheInkGeometry: true);
            }
            _pageRenderOverlayBatches.Add(batch);
            _pageRenderOverlays.RemoveRange(0, OverlayBatchSize);
            CompactOverlayBatches(device);
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
        foreach (var batch in _pageRenderOverlayBatches) drawingSession.DrawImage(batch);
        // New pen strokes remain as a tiny overlay until the next structural edit or page
        // switch. This avoids rebuilding a dense imported page immediately after every pen-up.
        foreach (var appended in _pageRenderOverlays)
        {
            if (appended.IsHidden) continue;
            DrawObject(drawingSession, appended, cacheInkGeometry: true);
        }
    }

    private bool ShouldDrawInteractiveViewport(NotePage page, PageRenderState state)
    {
        // Interaction must never replay a dense vector scene. Keep presenting retained content
        // while pan/pinch/wheel state changes; native tiles refine only after the viewport settles.
        if (state.InteractionActive) return false;
        // The retained snapshot is used only while it has enough source pixels for the current
        // monitor. Beyond that point, draw the visible vector scene through the spatial index.
        // This removes the old object-count and zoom-threshold mode switches that caused the
        // renderer to oscillate between three unrelated pipelines at intermediate zoom levels.
        return !CanUseNavigationSnapshot(page, state) && !CanUseNativeNavigationTiles(page, state);
    }

    private static bool CanUseNavigationSnapshot(NotePage page, PageRenderState state)
    {
        if (page.Size.Width <= 0 || page.Size.Height <= 0) return false;
        return RenderScalePolicy.HasNativeDisplayResolution(
            NavigationSnapshotScale(page), state.Zoom, state.Dpi);
    }

    private static bool CanUseNativeNavigationTiles(NotePage page, PageRenderState state) =>
        page.Objects.Count >= NavigationTileObjectThreshold && !state.InteractionActive;

    private void DrawNavigationTiles(CanvasDevice device, CanvasDrawingSession drawingSession, NotePage page,
        PageRenderState state)
    {
        var scale = RenderScalePolicy.ComputeNativeTileScale(state.Zoom, state.Dpi);
        EnsureNavigationTileSet(page, scale);
        var visibleBounds = VisiblePageBounds(page, 0, state);
        if (visibleBounds.Width <= 0 || visibleBounds.Height <= 0) return;

        // Always present the last complete page immediately. Native-resolution tiles replace
        // this progressively, so pan/zoom never waits for a batch of raster builds and never
        // drops to a blank or deliberately blurred interaction surface.
        if (_lowZoomPageRaster is not null && _lowZoomPageRasterPageId == page.Id)
            drawingSession.DrawImage(_lowZoomPageRaster,
                new Rect(0, 0, page.Size.Width, page.Size.Height));
        else if (_pageRenderCache is not null && _pageRenderCachePageId == page.Id)
            drawingSession.DrawImage(_pageRenderCache);

        var fullPixelWidth = Math.Max(1, (int)Math.Ceiling(page.Size.Width * scale));
        var fullPixelHeight = Math.Max(1, (int)Math.Ceiling(page.Size.Height * scale));
        var minimumTileX = Math.Clamp((int)Math.Floor(visibleBounds.Left * scale / NavigationTilePixels),
            0, Math.Max(0, (fullPixelWidth - 1) / NavigationTilePixels));
        var maximumTileX = Math.Clamp((int)Math.Floor(
                Math.Max(visibleBounds.Left, visibleBounds.Right - 0.0001) * scale / NavigationTilePixels),
            minimumTileX, Math.Max(0, (fullPixelWidth - 1) / NavigationTilePixels));
        var minimumTileY = Math.Clamp((int)Math.Floor(visibleBounds.Top * scale / NavigationTilePixels),
            0, Math.Max(0, (fullPixelHeight - 1) / NavigationTilePixels));
        var maximumTileY = Math.Clamp((int)Math.Floor(
                Math.Max(visibleBounds.Top, visibleBounds.Bottom - 0.0001) * scale / NavigationTilePixels),
            minimumTileY, Math.Max(0, (fullPixelHeight - 1) / NavigationTilePixels));

        _visibleNavigationTileKeys.Clear();
        (int X, int Y)? firstMissing = null;
        var readyTileCount = 0;
        for (var tileY = minimumTileY; tileY <= maximumTileY; tileY++)
        for (var tileX = minimumTileX; tileX <= maximumTileX; tileX++)
        {
            var key = (tileX, tileY);
            _visibleNavigationTileKeys.Add(key);
            if (!_navigationTiles.ContainsKey(key))
            {
                firstMissing ??= key;
                continue;
            }
            TouchNavigationTile(key);
            readyTileCount++;
        }

        // A single refinement per settled frame bounds worst-case work. The previous renderer
        // built every visible miss synchronously (24 tiles in one observed 35 ms frame). The
        // refined set remains hidden until it is complete, preventing checkerboard loading.
        if (NavigationRefinementPolicy.TileBuildBudget(state.InteractionActive) > 0 &&
            firstMissing is { } missing)
        {
            GetOrCreateNavigationTile(device, page, missing, fullPixelWidth, fullPixelHeight);
            TouchNavigationTile(missing);
            readyTileCount++;
            if (!NavigationRefinementPolicy.ShouldPresentTiles(
                    _visibleNavigationTileKeys.Count, readyTileCount))
                DispatcherQueue.TryEnqueue(() => PageSurface.Invalidate());
        }

        if (NavigationRefinementPolicy.ShouldPresentTiles(
                _visibleNavigationTileKeys.Count, readyTileCount))
        {
            foreach (var key in _visibleNavigationTileKeys)
                DrawNavigationTile(drawingSession, _navigationTiles[key], key,
                    fullPixelWidth, fullPixelHeight, scale);
        }
        TrimNavigationTiles();
    }

    private static void DrawNavigationTile(
        CanvasDrawingSession drawingSession,
        CanvasRenderTarget tile,
        (int X, int Y) key,
        int fullPixelWidth,
        int fullPixelHeight,
        double scale)
    {
        var metrics = NavigationTileMetrics.Create(
            key.X,
            key.Y,
            NavigationTilePixels,
            fullPixelWidth,
            fullPixelHeight,
            NavigationTileGutterPixels);
        drawingSession.DrawImage(tile, new Rect(
            metrics.RenderPixelLeft / scale,
            metrics.RenderPixelTop / scale,
            metrics.RenderPixelWidth / scale,
            metrics.RenderPixelHeight / scale));
    }

    private void EnsureNavigationTileSet(NotePage page, double scale)
    {
        if (_navigationTilePageId == page.Id && Math.Abs(_navigationTileScale - scale) < 0.0001)
            return;

        ClearNavigationTileCacheCore();
        _navigationTilePageId = page.Id;
        _navigationTileScale = scale;
        _pageRenderCache?.Dispose();
        _pageRenderCache = null;
        if (_pageRenderCachePageId != page.Id || _pageRenderCacheObjectIds.Count == 0)
        {
            _pageRenderCacheObjectIds.Clear();
            _pageRenderCacheObjectIds.UnionWith(page.Objects.Select(item => item.Id));
        }
        _pageRenderCachePageId = page.Id;
    }

    private CanvasRenderTarget GetOrCreateNavigationTile(CanvasDevice device, NotePage page,
        (int X, int Y) key, int fullPixelWidth, int fullPixelHeight)
    {
        if (_navigationTiles.TryGetValue(key, out var existing)) return existing;

        var metrics = NavigationTileMetrics.Create(
            key.X,
            key.Y,
            NavigationTilePixels,
            fullPixelWidth,
            fullPixelHeight,
            NavigationTileGutterPixels);
        var tileBounds = new RectD(
            metrics.RenderPixelLeft / _navigationTileScale,
            metrics.RenderPixelTop / _navigationTileScale,
            metrics.RenderPixelWidth / _navigationTileScale,
            metrics.RenderPixelHeight / _navigationTileScale);
        var tile = new CanvasRenderTarget(device,
            metrics.RenderPixelWidth, metrics.RenderPixelHeight, 96);
        using (var session = tile.CreateDrawingSession())
        {
            session.Clear(Color.FromArgb(0, 0, 0, 0));
            session.Transform = Matrix3x2.CreateTranslation(
                                    (float)-tileBounds.X, (float)-tileBounds.Y) *
                                Matrix3x2.CreateScale((float)_navigationTileScale);

            var queryBounds = tileBounds.Inflate(32);
            if (_spatialIndex.Count == page.Objects.Count)
                _spatialIndex.Query(queryBounds, _visibleObjectIds, _visibleObjects);
            else
            {
                _visibleObjectIds.Clear();
                _visibleObjects.Clear();
                foreach (var canvasObject in page.Objects)
                {
                    if (!StrokeGeometry.GetWorldBounds(canvasObject).Intersects(queryBounds)) continue;
                    _visibleObjectIds.Add(canvasObject.Id);
                    _visibleObjects.Add(canvasObject);
                }
            }
            DrawPageBackground(session, page, tileBounds);
            DrawImportedLayer(session, page);
            if (_temporaryGridVisible) DrawTemporaryGrid(session, page, tileBounds);
            var tileObjects = _visibleObjects.Where(canvasObject =>
                _pageRenderCacheObjectIds.Contains(canvasObject.Id));
            foreach (var canvasObject in
                     CanvasObjectRenderPolicy.VisibleInAuthoredOrder(tileObjects))
                DrawObject(session, canvasObject, cacheInkGeometry: true);
        }

        _navigationTiles[key] = tile;
        _navigationTileBytes += (long)metrics.RenderPixelWidth * metrics.RenderPixelHeight *
                                RenderScalePolicy.BytesPerPixel;
        _frameNavigationTileBuilds++;
        return tile;
    }

    private void TouchNavigationTile((int X, int Y) key)
    {
        if (_navigationTileLruNodes.Remove(key, out var node))
            _navigationTileLru.Remove(node);
        _navigationTileLruNodes[key] = _navigationTileLru.AddFirst(key);
    }

    private void TrimNavigationTiles()
    {
        while (_navigationTileBytes > NavigationTileByteBudget && _navigationTileLru.Last is not null)
        {
            var node = _navigationTileLru.Last;
            while (node is not null && _visibleNavigationTileKeys.Contains(node.Value))
                node = node.Previous;
            if (node is null) break;
            var key = node.Value;
            _navigationTileLru.Remove(node);
            _navigationTileLruNodes.Remove(key);
            if (!_navigationTiles.Remove(key, out var tile)) continue;
            _navigationTileBytes = Math.Max(0, _navigationTileBytes -
                (long)tile.SizeInPixels.Width * tile.SizeInPixels.Height *
                RenderScalePolicy.BytesPerPixel);
            tile.Dispose();
        }
    }

    private void DrawInteractiveViewport(CanvasDrawingSession drawingSession, NotePage page,
        PageRenderState state)
    {
        var visibleBounds = VisiblePageBounds(page, 32 / Math.Max(state.Zoom, 0.08), state);
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
        DrawPageBackground(drawingSession, page, visibleBounds);
        DrawImportedLayer(drawingSession, page);
        if (_temporaryGridVisible) DrawTemporaryGrid(drawingSession, page, visibleBounds);
        foreach (var canvasObject in
                 CanvasObjectRenderPolicy.VisibleInAuthoredOrder(_visibleObjects))
            DrawObject(drawingSession, canvasObject, cacheInkGeometry: true);
    }

    private static RectD VisiblePageBounds(NotePage page, double padding, PageRenderState state)
    {
        if (!Matrix3x2.Invert(PageTransform(
                page, state.Zoom, state.Pan, state.Width, state.Height), out var inverse))
            return new RectD(0, 0, page.Size.Width, page.Size.Height);
        var topLeft = Vector2.Transform(Vector2.Zero, inverse);
        var bottomRight = Vector2.Transform(
            new Vector2((float)state.Width, (float)state.Height), inverse);
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
        ClearNavigationTileCacheCore();
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
        IReadOnlyList<CanvasObject> renderedObjects = usePreviews
            ? page.Objects.Select(canvasObject =>
                    _multiTransformPreviews.TryGetValue(canvasObject.Id, out var multiPreview)
                        ? multiPreview
                        : _transformPreview?.Id == canvasObject.Id ? _transformPreview :
                            _textPreview?.Id == canvasObject.Id ? _textPreview : canvasObject)
                .ToArray()
            : page.Objects;
        DrawPageBackground(drawingSession, page);
        DrawImportedLayer(drawingSession, page);
        // The temporary grid sits above paper/PDF backgrounds but below all authored content.
        // This keeps it useful as a guide without obscuring handwriting.
        if (_temporaryGridVisible) DrawTemporaryGrid(drawingSession, page);
        // Document commands maintain z-order, so sorting this dense list again whenever a
        // cache is recorded is redundant O(n log n) work on the UI thread.
        foreach (var canvasObject in
                 CanvasObjectRenderPolicy.VisibleInAuthoredOrder(renderedObjects))
            DrawObject(drawingSession, canvasObject);
    }

    private void DrawLiveInk(CanvasDrawingSession drawingSession)
    {
        var style = (_gestureInkStyle ?? CurrentInkStyle()).Normalize();
        using var blend = ConfigureInkBlend(
            drawingSession, style, IsDarkColor(_page?.Template.PaperColor ?? "#FFFDF8"), out var color);
        var width = style.Width;
        if (style.Tool == InkToolKind.Highlighter && HighlighterStraightCheckBox.IsChecked == true && _activeInk.Count > 1)
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

    private void DrawLiveInkPrediction(CanvasDrawingSession drawingSession)
    {
        if (_activeInk.Count < 2 || _gestureTool != EditorTool.Pen) return;
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
            // Keep the page clean while this finishes; a transient loading banner flashed on
            // every PDF page switch and added no useful information.
            RequestPdfPreviewLoad(page);
        }
    }

    private void DrawObject(
        CanvasDrawingSession drawingSession,
        CanvasObject canvasObject,
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
        RequestImageLoad(image);
    }

    private void RequestImageLoad(ImageObject image)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            BeginImageLoad(image);
            return;
        }
        if (!_queuedImageLoadRequests.TryAdd(image.AssetHash, 0)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            _queuedImageLoadRequests.TryRemove(image.AssetHash, out _);
            BeginImageLoad(image);
        });
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
                if (_page is not null && _imagePagesNeedingRefresh.Contains(_page.Id))
                {
                    InvalidatePageRenderCache();
                    InvalidateCanvas();
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

    private void DrawInk(
        CanvasDrawingSession drawingSession,
        InkStrokeObject stroke,
        bool cacheGeometry = false)
    {
        if (stroke.Points.Count == 0) return;
        var normalizedStyle = stroke.Style.Normalize();
        using var blend = ConfigureInkBlend(
            drawingSession, normalizedStyle,
            IsDarkColor(_page?.Template.PaperColor ?? "#FFFDF8"), out var color);
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

    private static CanvasBlendScope ConfigureInkBlend(
        CanvasDrawingSession drawingSession,
        InkStyle style,
        bool darkSurface,
        out Color color)
    {
        var previous = drawingSession.Blend;
        if (style.Tool != InkToolKind.Highlighter)
        {
            color = ParseColor(style.Color, CanvasObjectRenderPolicy.SourceOverOpacity(style));
            return new CanvasBlendScope(drawingSession, previous);
        }

        var source = ParseColor(style.Color);
        if (darkSurface)
        {
            drawingSession.Blend = CanvasBlend.Add;
            color = Color.FromArgb(
                (byte)Math.Round(CanvasObjectRenderPolicy.HighlighterBlendStrength(style) * 255),
                source.R, source.G, source.B);
        }
        else
        {
            drawingSession.Blend = CanvasBlend.Min;
            color = Color.FromArgb(255,
                CanvasObjectRenderPolicy.LightSurfaceHighlighterChannel(source.R, style),
                CanvasObjectRenderPolicy.LightSurfaceHighlighterChannel(source.G, style),
                CanvasObjectRenderPolicy.LightSurfaceHighlighterChannel(source.B, style));
        }
        return new CanvasBlendScope(drawingSession, previous);
    }

    private bool TryDrawCachedInk(CanvasDrawingSession drawingSession, InkStrokeObject stroke, Color color)
    {
        if (stroke.Points.Count < 2) return false;
        if (_strokeGeometryCache.TryGetValue(stroke.Id, out var existing))
        {
            if (ReferenceEquals(existing.Stroke, stroke) && existing.Color.Equals(color))
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
            if (stroke.Points.Count < 2) return null;
            var geometry = ShouldUseCanonicalSmoothCenterline(stroke)
                ? CreateSmoothCenterlineGeometry(resourceCreator, stroke.Points, 0, stroke.Points.Count)
                : CreateCenterlineGeometry(resourceCreator, StrokeOutlineBuilder.FitCenterline(stroke));
            return new StrokeGeometryCacheEntry(
                stroke,
                geometry,
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

        var centerline = ShouldUseCanonicalSmoothCenterline(stroke)
            ? stroke.Points
            : StrokeOutlineBuilder.FitCenterline(stroke);
        if (centerline.Count == 0) return;
        if (centerline.Count == 1)
        {
            var point = centerline[0];
            drawingSession.FillCircle((float)point.X, (float)point.Y, width / 2f, color);
            return;
        }

        using var geometry = ShouldUseCanonicalSmoothCenterline(stroke)
            ? CreateSmoothCenterlineGeometry(drawingSession, centerline, 0, centerline.Count)
            : CreateCenterlineGeometry(drawingSession, centerline);
        drawingSession.DrawGeometry(geometry, color, width, _roundInkStrokeStyle);
    }

    private static bool ShouldUseCanonicalSmoothCenterline(InkStrokeObject stroke) =>
        !stroke.Style.PreserveSourceGeometry && stroke.Style.Smoothing > 0;

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
            case ShapeKind.Circle:
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
            case ShapeKind.Star:
                var starPoints = ShapeGeometry.StarPoints(bounds);
                for (var index = 0; index < starPoints.Count; index++)
                {
                    var next = starPoints[(index + 1) % starPoints.Count];
                    drawingSession.DrawLine(starPoints[index].ToVector2(), next.ToVector2(), color,
                        shape.StrokeWidth, _roundInkStrokeStyle);
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
        StopWheelScrollAnimation(resumeBackgroundWork: false);
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
        if (_gestureTool != EditorTool.Pan && !TryActivateVisiblePageAt(point.Position))
        {
            _isPointerDown = false;
            _penActive = false;
            ResumeBackgroundRecognition();
            ResumeThumbnailRefresh();
            e.Handled = true;
            return;
        }
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
        var contact = point.Properties.ContactRect;
        _gestureAllowsTextSelection = TouchInputPolicy.CanSelectText(
            point.PointerDeviceType == PointerDeviceType.Pen,
            point.PointerDeviceType == PointerDeviceType.Mouse,
            e.IsGenerated,
            NativePointerClassifier.IsTouch(point.PointerId),
            contact.Width > 0.5 && contact.Height > 0.5);
        if (_gestureTool is EditorTool.Lasso or EditorTool.BoxSelect && SelectionContainsInteraction(_gestureStart))
            _gestureTool = EditorTool.Select;
        _activeInk.Clear();
        _lastInkMovementTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        ClearLiveInkGeometryCache();
        _eraserPath.Clear();
        _eraseDirtyRegions.Clear();
        Volatile.Write(ref _erasePreviewCommitVersion, -1);
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
                BeginSelectionGesture(_gestureStart, _gestureAllowsTextSelection);
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
            case EditorTool.Eyedropper:
                var pickedInk = FindInkStrokeAt(_gestureStart);
                EndPointer(e);
                if (pickedInk is null)
                {
                    StatusText.Text = "No ink stroke here";
                    return;
                }
                ActivateTool(EditorTool.Pen);
                SetInkColor(pickedInk.Style.Color);
                StrokeWidthSlider.Value = Math.Clamp(
                    Math.Round(StrokeGeometry.EffectiveWorldWidth(pickedInk), 1),
                    StrokeWidthSlider.Minimum,
                    StrokeWidthSlider.Maximum);
                StatusText.Text =
                    $"Pen matched • {pickedInk.Style.Color.ToUpperInvariant()} • {StrokeWidthSlider.Value:0.#} pt";
                return;
        }

        e.Handled = true;
        if (_gestureTool == EditorTool.Pan) InvalidateCanvas();
        else InvalidateInteractionOverlay();
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
                if (TryContinueToAdjacentPage())
                {
                    _panStart = _pan;
                    _screenStart = current.Position;
                }
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
                var multiDelta = CreateSelectionTransform(
                    _transformHandle,
                    CombinedBounds(_multiTransformOriginals),
                    _gestureStart,
                    multiCurrent,
                    multiPreserveAspect,
                    baseRotation: 0);
                _multiTransformPreviews.Clear();
                foreach (var original in _multiTransformOriginals)
                    _multiTransformPreviews[original.Id] =
                        ApplySelectionTransform(original, multiDelta);
                redraw = true;
                break;
            case EditorTool.Select when _textSelectionAnchor is { } textAnchor:
                var textCurrent = ScreenToPage(current.Position);
                _textSelectionDragBounds = NormalizeRect(textAnchor, textCurrent);
                UpdateTextRegionSelection(textCurrent);
                redraw = true;
                break;
            case EditorTool.Select when _transformOriginal is not null && _transformHandle != TransformHandle.None:
                var currentPage = ScreenToPage(current.Position);
                var singlePreserveAspect = IsCornerHandle(_transformHandle) && !IsShiftDown();
                var delta = CreateSelectionTransform(
                    _transformHandle,
                    StrokeGeometry.GetWorldBounds(_transformOriginal),
                    _gestureStart,
                    currentPage,
                    singlePreserveAspect,
                    TransformRotation(_transformOriginal.Transform));
                _transformPreview = ApplySelectionTransform(_transformOriginal, delta);
                redraw = true;
                break;
        }

        e.Handled = true;
        if (redraw)
        {
            if (_gestureTool == EditorTool.Pan) InvalidateCanvas();
            else InvalidateInteractionOverlay();
        }
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
            case EditorTool.Select when _textSelectionAnchor is not null:
                UpdateTextRegionSelection(ScreenToPage(current.Position), finalize: true);
                _textSelectionAnchor = null;
                _textSelectionDragBounds = null;
                StatusText.Text = _selectedTextRegions.Count == 0
                    ? "No text selected"
                    : $"Selected {_selectedTextRegions.Count} text region(s) • Ctrl+C to copy";
                UpdateSelectionUi();
                break;
            case EditorTool.Select when _multiTransformOriginals is { Count: > 1 } && _multiTransformPreviews.Count > 0:
                var after = _multiTransformOriginals.Select(item => _multiTransformPreviews[item.Id]).ToArray();
                _history.Execute(new ReplaceObjectsCommand(_page.Id, _multiTransformOriginals, after,
                    "Transform selection"), _document);
                _selectedObjects.Clear();
                _selectedObjects.AddRange(after);
                OnDocumentChanged(recognizeInk: after.Any(item => item is InkStrokeObject));
                Volatile.Write(ref _transformPreviewCommitVersion, _editVersion);
                break;
            case EditorTool.Select when _transformOriginal is not null && _transformPreview is not null:
                _history.Execute(new ReplaceObjectsCommand(_page.Id, [_transformOriginal], [_transformPreview], "Transform object"), _document);
                _selectedObject = _transformPreview;
                _selectedObjects.Clear();
                _selectedObjects.Add(_transformPreview);
                OnDocumentChanged(recognizeInk: false);
                Volatile.Write(ref _transformPreviewCommitVersion, _editVersion);
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
        _textSelectionAnchor = null;
        _textSelectionDragBounds = null;
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
        var retainErasePreview = _eraseDirtyRegions.Count > 0 &&
                                 Volatile.Read(ref _erasePreviewCommitVersion) >= 0;
        var retainTransformPreview = _selectionTransformOriginalIds.Count > 0 &&
                                     Volatile.Read(ref _transformPreviewCommitVersion) >= 0;
        _isPointerDown = false;
        _penActive = false;
        if (releaseCapture) DrawingSurface.ReleasePointerCapture(e.Pointer);
        _activeInk.Clear();
        ClearLiveInkGeometryCache();
        _eraserPath.Clear();
        if (!retainErasePreview) _eraseDirtyRegions.Clear();
        _transformOriginal = null;
        _multiTransformOriginals = null;
        if (!retainTransformPreview) ClearTransformPreviewState();
        _styleBrushOriginals.Clear();
        _styleBrushPoint = null;
        _eraseSnapshot = null;
        _transformHandle = TransformHandle.None;
        _gestureInkStyle = null;
        _gestureAllowsTextSelection = false;
        _gestureScreenToPageValid = false;
        e.Handled = true;
        InvalidateCanvas();
        ResumeBackgroundRecognition();
        ResumeThumbnailRefresh();
    }

    private void ClearTransformPreviewState()
    {
        _transformPreview = null;
        _multiTransformPreviews.Clear();
        _selectionTransformOriginalIds.Clear();
        _selectionTransformSourceBounds = null;
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
        StopWheelScrollAnimation(resumeBackgroundWork: false);

        var firstTouch = _touchPoints.Count == 0;
        _touchPoints[point.PointerId] = point.Position;
        if (firstTouch)
        {
            _touchPageScrollActive = false;
        }
        else
        {
            // A second finger always means pinch. It must never change pages.
            _touchPageScrollActive = false;
        }
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
        PointerStatus.Text = _touchPageScrollActive
            ? "Touch • swipe edge to change page"
            : _touchPoints.Count > 1 ? "Touch • pinch to zoom" : "Touch • drag to pan";
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
        var wasPageScroll = _touchPageScrollActive;
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
                    if (_touchPoints.Count == 0)
                    {
                        _touchPageScrollActive = false;
                    }
                    else
                    {
                        _touchPageScrollActive = false;
                    }
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
            if (_touchPoints.Count > 1) _touchPageScrollActive = false;
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
        InvalidateCanvas();
        if (wasPageScroll)
        {
            ResetTouchPageScroll();
            ResumeBackgroundRecognition();
            ResumeThumbnailRefresh();
            return;
        }
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
        var wasPageScroll = _touchPageScrollActive;
        var removed = _touchPoints.Remove(e.Pointer.PointerId);
        if (releaseCapture) DrawingSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        if (!removed) return;

        if (_touchPoints.Count > 0)
        {
            if (_touchPoints.Count > 1) _touchPageScrollActive = false;
            // Preserve the last centroid velocity while fingers lift one at a time so a
            // two-finger pan still transitions naturally into inertia.
            RebaseTouchGesture(resetVelocity: false);
            PointerStatus.Text = _touchPoints.Count > 1 ? "Touch • pinch to zoom" : "Touch • drag to pan";
            return;
        }

        PointerStatus.Text = "Windows Ink";
        InvalidateCanvas();
        if (wasPageScroll)
        {
            ResetTouchPageScroll();
            ResumeBackgroundRecognition();
            ResumeThumbnailRefresh();
            return;
        }
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
        ResetTouchPageScroll();
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
        if (_touchPageScrollActive && _touchPoints.Count == 1)
        {
            ApplyTouchPageScroll(centroid);
            return;
        }
        UpdateTouchVelocity(centroid);
        if (_touchPoints.Count == 1)
        {
            var viewport = TouchViewportMath.Pan(
                _zoom,
                _touchStartPan,
                new PointD(_touchStartCentroid.X, _touchStartCentroid.Y),
                new PointD(centroid.X, centroid.Y));
            _pan = viewport.Pan;
            if (TryContinueToAdjacentPage())
                RebaseTouchGesture(resetVelocity: false);
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
                new SizeD(DrawingSurface.ActualWidth, DrawingSurface.ActualHeight),
                _minimumZoom,
                _maximumZoom);
            _zoom = viewport.Zoom;
            _pan = viewport.Pan;
            UpdateZoomText(showIndicator: true);
        }
        _fitPending = false;
        InvalidateCanvas();
    }

    private void ApplyTouchPageScroll(Point centroid)
    {
        var steps = TouchInputPolicy.PageScrollSteps(centroid.Y - _touchPageScrollAnchorY);
        if (steps == 0) return;
        var currentIndex = PageList.SelectedIndex;
        if (currentIndex < 0 || _pages.Count == 0) return;
        var targetIndex = Math.Clamp(currentIndex + steps, 0, _pages.Count - 1);
        if (targetIndex == currentIndex)
        {
            _touchPageScrollAnchorY = centroid.Y;
            return;
        }

        PageList.SelectedIndex = targetIndex;
        PageList.ScrollIntoView(_pages[targetIndex]);
        _touchPageScrollAnchorY -= steps * TouchInputPolicy.PageScrollStepDistance;
        _touchVelocity = Vector2.Zero;
        _touchGestureMoved = false;
        PointerStatus.Text = $"Page {targetIndex + 1} of {_pages.Count}";
    }

    private bool TryContinueToAdjacentPage()
    {
        if (_document is null || _page is null || _document.Kind == DocumentKind.InfiniteCanvas ||
            _pages.Count < 2) return false;
        var currentIndex = _pages.IndexOf(_page);
        if (currentIndex < 0) return false;

        var oldPage = _page;
        var oldZoom = _zoom;
        var oldPan = _pan;
        var oldOffset = PageOffset();
        var oldTop = oldOffset.Y;
        var oldBottom = oldTop + (float)(oldPage.Size.Height * oldZoom);
        const float pageGap = 28;
        var targetIndex = oldBottom < 0
            ? currentIndex + 1
            : oldTop > _canvasHeight
                ? currentIndex - 1
                : currentIndex;
        if (targetIndex < 0 || targetIndex >= _pages.Count || targetIndex == currentIndex) return false;

        var target = _pages[targetIndex];
        var desiredTop = targetIndex > currentIndex
            ? oldBottom + pageGap
            : oldTop - pageGap - (float)(target.Size.Height * oldZoom);
        SwitchToVisiblePage(target, desiredTop, oldZoom, oldPan.X);
        return true;
    }

    private bool TryActivateVisiblePageAt(Point screenPoint)
    {
        if (_document is null || _page is null ||
            _document.Kind == DocumentKind.InfiniteCanvas) return true;
        var currentIndex = _document.Pages.FindIndex(page => page.Id == _page.Id);
        if (currentIndex < 0) return false;
        var viewport = new SizeD(_canvasWidth, _canvasHeight);
        var currentBounds = ContinuousPageLayout.CurrentBounds(
            _page.Size, _zoom, _pan.X, _pan.Y, viewport);
        RectD? previousBounds = null;
        RectD? nextBounds = null;
        if (currentIndex > 0)
            previousBounds = ContinuousPageLayout.AdjacentBounds(
                currentBounds, _document.Pages[currentIndex - 1].Size, _zoom, _pan.X,
                viewport, aboveCurrentPage: true, ContinuousPageGap);
        if (currentIndex + 1 < _document.Pages.Count)
            nextBounds = ContinuousPageLayout.AdjacentBounds(
                currentBounds, _document.Pages[currentIndex + 1].Size, _zoom, _pan.X,
                viewport, aboveCurrentPage: false, ContinuousPageGap);

        var slot = ContinuousPageLayout.HitTest(
            new PointD(screenPoint.X, screenPoint.Y), currentBounds, previousBounds, nextBounds);
        if (slot is null) return false;
        if (slot == ContinuousPageSlot.Current) return true;
        var targetIndex = currentIndex + (int)slot.Value;
        var targetBounds = slot == ContinuousPageSlot.Previous ? previousBounds : nextBounds;
        if (targetBounds is null || targetIndex < 0 || targetIndex >= _document.Pages.Count)
            return false;
        SwitchToVisiblePage(
            _document.Pages[targetIndex], targetBounds.Value.Y, _zoom, _pan.X);
        return true;
    }

    private void SwitchToVisiblePage(
        NotePage target,
        double targetTop,
        double preservedZoom,
        float preservedPanX)
    {
        if (_document is null || _page?.Id == target.Id) return;
        var previousPage = _page;
        var previousSelection = PageList.SelectedItem as NotePage;
        var targetIndex = _document.Pages.FindIndex(page => page.Id == target.Id);
        if (targetIndex < 0) return;
        // A just-edited page becomes a bitmap-backed neighbor after this switch. Start its
        // refresh before changing focus so fast cross-page handwriting never gets stranded
        // behind the normal thumbnail debounce.
        if (previousPage is not null && _pendingThumbnailRefreshPageIds.Contains(previousPage.Id) &&
            _notebookPagePreviewCancellation is { } previewCancellation)
            _ = EnsureNotebookPagePreviewAsync(
                previousPage,
                _notebookPagePreviewGeneration,
                previewCancellation.Token,
                refresh: true);
        _loading = true;
        PageList.SelectedItem = target;
        _loading = false;
        if (previousSelection is not null &&
            PageList.ContainerFromItem(previousSelection) is ListViewItem previousContainer)
            UpdatePageThumbnailContainer(previousSelection, previousContainer);
        if (PageList.ContainerFromItem(target) is ListViewItem targetContainer)
            UpdatePageThumbnailContainer(target, targetContainer);
        _tabPageSelections[_document.Id] = target.Id;
        SelectPage(target);
        _zoom = preservedZoom;
        _pan = new Vector2(
            preservedPanX,
            (float)ContinuousPageLayout.PanYForPageTop(
                targetTop, target.Size, preservedZoom, _canvasHeight));
        ClampHorizontalPan();
        _fitPending = false;
        PageList.ScrollIntoView(target);
        PointerStatus.Text = $"Page {targetIndex + 1} of {_pages.Count}";
        BeginNavigationSettle();
        InvalidateCanvas();
    }

    private void ResetTouchPageScroll()
    {
        _touchPageScrollActive = false;
        _touchPageScrollAnchorY = 0;
        _touchVelocity = Vector2.Zero;
        _touchGestureMoved = false;
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
        TryContinueToAdjacentPage();
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
        if (_isPointerDown || _cornerZoomDragging || _touchPoints.Count > 0 ||
            _touchInertiaActive || _wheelZoomAnimating || _wheelScrollAnimating)
        {
            sender.Start();
            return;
        }
        if (!_zoomNavigationActive) return;
        _zoomNavigationActive = false;
        InvalidateCanvas();
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
        UpdateZoomText(showIndicator: true);

        if (nextZoom == _wheelZoomTarget)
            StopWheelZoomAnimation(resumeBackgroundWork: true);
        return true;
    }

    private bool AdvanceWheelScroll()
    {
        if (!_wheelScrollAnimating || _page is null || _isPointerDown || _touchPoints.Count > 0)
        {
            StopWheelScrollAnimation(resumeBackgroundWork: true);
            return false;
        }
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsedSeconds = Math.Clamp(
            (now - _wheelScrollTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency,
            0.001,
            0.05);
        _wheelScrollTimestamp = now;
        _pan.Y += _wheelScrollVelocity * (float)elapsedSeconds;
        TryContinueToAdjacentPage();
        _wheelScrollVelocity *= (float)Math.Exp(-11 * elapsedSeconds);
        if (Math.Abs(_wheelScrollVelocity) < 18)
            StopWheelScrollAnimation(resumeBackgroundWork: true);
        return true;
    }

    private void ApplyZoomAtAnchor(double zoom, PointD pageAnchor, Point screenAnchor)
    {
        _zoom = Math.Clamp(zoom, _minimumZoom, _maximumZoom);
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

    private void StopWheelScrollAnimation(bool resumeBackgroundWork)
    {
        if (!_wheelScrollAnimating)
        {
            _wheelScrollVelocity = 0;
            return;
        }
        _wheelScrollAnimating = false;
        _wheelScrollVelocity = 0;
        BeginNavigationSettle();
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
        var delta = pointer.Properties.MouseWheelDelta;
        if (delta == 0) return;
        if (_readMode || !IsControlDown())
        {
            StopWheelZoomAnimation(resumeBackgroundWork: false);
            PauseBackgroundRecognition();
            PauseThumbnailRefresh();
            BeginNavigationSettle();
            _pan.Y += delta * 0.13f;
            if (!_wheelScrollAnimating)
            {
                _wheelScrollAnimating = true;
                _wheelScrollTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                EnsureViewportFramePump();
            }
            _wheelScrollVelocity = Math.Clamp(
                _wheelScrollVelocity + delta * 5.7f, -5_000, 5_000);
            _fitPending = false;
            TryContinueToAdjacentPage();
            e.Handled = true;
            InvalidateCanvas();
            return;
        }

        StopWheelScrollAnimation(resumeBackgroundWork: false);
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
        _wheelZoomTarget = Math.Clamp(_wheelZoomTarget * multiplier, _minimumZoom, _maximumZoom);
        // Apply part of the delta synchronously so the canvas never trails a physical wheel
        // notch, then animate only the small remainder over a fixed, non-queueing interval.
        var immediateZoom = _zoom * Math.Exp(
            Math.Log(_wheelZoomTarget / Math.Max(_zoom, 0.0001)) * 0.42);
        ApplyZoomAtAnchor(immediateZoom, _wheelZoomAnchorPage, _wheelZoomAnchorScreen);
        _wheelZoomStart = _zoom;
        _wheelZoomAnimationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateZoomText(showIndicator: true);
        InvalidateCanvas();
        e.Handled = true;
    }

    private void OnCornerZoomPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_page is null || sender is not UIElement element) return;
        var pointer = e.GetCurrentPoint(DrawingSurface);
        if (pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            !pointer.Properties.IsLeftButtonPressed) return;

        StopTouchInertia(resumeBackgroundWork: false);
        StopWheelZoomAnimation(resumeBackgroundWork: false);
        _cornerZoomDragging = true;
        _cornerZoomPointerId = e.Pointer.PointerId;
        _cornerZoomStart = pointer.Position;
        _cornerZoomStartLevel = _zoom;
        _cornerZoomAnchorScreen = new Point(_canvasWidth / 2d, _canvasHeight / 2d);
        _cornerZoomAnchorPage = ScreenToPage(_cornerZoomAnchorScreen);
        _fitPending = false;
        element.CapturePointer(e.Pointer);
        BeginNavigationSettle();
        PauseBackgroundRecognition();
        PauseThumbnailRefresh();
        PointerStatus.Text = "Drag zoom";
        e.Handled = true;
    }

    private void OnCornerZoomPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_cornerZoomDragging || e.Pointer.PointerId != _cornerZoomPointerId) return;
        var current = e.GetCurrentPoint(DrawingSurface).Position;
        var drag = current.X - _cornerZoomStart.X - (current.Y - _cornerZoomStart.Y);
        var target = _cornerZoomStartLevel * Math.Pow(2, drag / 180d);
        ApplyZoomAtAnchor(target, _cornerZoomAnchorPage, _cornerZoomAnchorScreen);
        UpdateZoomText(showIndicator: true);
        InvalidateCanvas();
        e.Handled = true;
    }

    private void OnCornerZoomPointerReleased(object sender, PointerRoutedEventArgs e) =>
        EndCornerZoomGesture(sender as UIElement, e, releaseCapture: true);

    private void OnCornerZoomPointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndCornerZoomGesture(sender as UIElement, e, releaseCapture: true);

    private void OnCornerZoomPointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndCornerZoomGesture(sender as UIElement, e, releaseCapture: false);

    private void EndCornerZoomGesture(
        UIElement? element,
        PointerRoutedEventArgs e,
        bool releaseCapture)
    {
        if (!_cornerZoomDragging || e.Pointer.PointerId != _cornerZoomPointerId) return;
        _cornerZoomDragging = false;
        if (releaseCapture) element?.ReleasePointerCapture(e.Pointer);
        BeginNavigationSettle();
        ResumeBackgroundRecognition();
        ResumeThumbnailRefresh();
        PointerStatus.Text = "Windows Ink";
        e.Handled = true;
    }

    private void BeginSelectionGesture(PointD point, bool allowTextSelection)
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
                PrepareSelectionTransformSource(_multiTransformOriginals);
                ClearTextSelection();
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
                PrepareSelectionTransformSource([_transformOriginal]);
                ClearTextSelection();
                return;
            }
        }

        var tolerance = 10 / _zoom;
        _selectedObject = _spatialIndex.Query(new RectD(point.X - tolerance, point.Y - tolerance, tolerance * 2, tolerance * 2))
            .Where(item => (!item.IsLocked || item is ImageObject or ShapeObject) &&
                           StrokeGeometry.HitTest(item, point, tolerance))
            .OrderByDescending(item => item.ZIndex)
            .FirstOrDefault();
        _selectedObjects.Clear();
        if (_selectedObject is not null) _selectedObjects.Add(_selectedObject);
        if (_selectedObject is not null)
        {
            ClearTextSelection();
        }
        else if (allowTextSelection && _page?.RecognizedRegions.Count > 0)
        {
            _textSelectionAnchor = point;
            _textSelectionDragBounds = new RectD(point.X, point.Y, 0, 0);
            _selectedTextRegions.Clear();
        }
        else
        {
            ClearTextSelection();
        }
        _transformHandle = _selectedObject is null or { IsLocked: true } ? TransformHandle.None : TransformHandle.Move;
        _transformOriginal = _selectedObject is { IsLocked: false } ? _selectedObject : null;
        if (_transformOriginal is not null) PrepareSelectionTransformSource([_transformOriginal]);
        else
        {
            _selectionTransformOriginalIds.Clear();
            _selectionTransformSourceBounds = null;
        }
        UpdateSelectionUi();
    }

    private void UpdateTextRegionSelection(PointD current, bool finalize = false)
    {
        if (_page is null || _textSelectionAnchor is not { } anchor) return;
        var bounds = NormalizeRect(anchor, current);
        var clickTolerance = 6 / Math.Max(_zoom, 0.08);
        _selectedTextRegions.Clear();
        if (bounds.Width <= clickTolerance && bounds.Height <= clickTolerance)
        {
            if (!finalize) return;
            var hitArea = new RectD(current.X - clickTolerance, current.Y - clickTolerance,
                clickTolerance * 2, clickTolerance * 2);
            var hit = _page.RecognizedRegions
                .Where(region => region.Bounds.Intersects(hitArea))
                .OrderBy(region => Math.Abs(region.Bounds.Center.X - current.X) +
                                   Math.Abs(region.Bounds.Center.Y - current.Y))
                .FirstOrDefault();
            if (hit is not null) _selectedTextRegions.Add(hit);
            return;
        }

        foreach (var region in _page.RecognizedRegions)
            if (region.Bounds.Intersects(bounds))
                _selectedTextRegions.Add(region);
    }

    private void ClearTextSelection()
    {
        _selectedTextRegions.Clear();
        _textSelectionAnchor = null;
        _textSelectionDragBounds = null;
    }

    private void PrepareSelectionTransformSource(IReadOnlyCollection<CanvasObject> originals)
    {
        _selectionTransformOriginalIds.Clear();
        foreach (var original in originals)
            _selectionTransformOriginalIds.Add(original.Id);
        _selectionTransformSourceBounds = originals.Count == 0
            ? null
            : CombinedBounds(originals).Inflate(Math.Max(2, 3 / Math.Max(_zoom, 0.08)));
    }

    private static Transform2D CreateSelectionTransform(
        TransformHandle handle,
        RectD originalBounds,
        PointD start,
        PointD current,
        bool preserveAspect,
        double baseRotation)
    {
        if (handle != TransformHandle.Rotate)
            return SelectionTransformer.CreateTransform(
                handle, originalBounds, start, current, preserveAspect);

        var center = originalBounds.Center;
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var currentAngle = Math.Atan2(current.Y - center.Y, current.X - center.X);
        var delta = currentAngle - startAngle;
        var total = baseRotation + delta;
        const double quarterTurn = Math.PI / 2d;
        const double magneticThreshold = 7d * Math.PI / 180d;
        var nearest = Math.Round(total / quarterTurn) * quarterTurn;
        if (Math.Abs(NormalizeAngle(total - nearest)) <= magneticThreshold)
            delta = nearest - baseRotation;
        return Transform2D.Rotation(delta, center);
    }

    private static double TransformRotation(Transform2D transform) =>
        Math.Atan2(transform.M12, transform.M11);

    private static double NormalizeAngle(double angle)
    {
        while (angle > Math.PI) angle -= Math.PI * 2;
        while (angle < -Math.PI) angle += Math.PI * 2;
        return angle;
    }

    private CanvasObject ApplySelectionTransform(CanvasObject original, Transform2D delta)
    {
        var combined = original.Transform.Then(delta);
        if (_userPreferences.ScaleStrokeWidthsOnTransform)
            return original with { Transform = combined };

        var oldScale = EffectiveTransformScale(original.Transform);
        var newScale = EffectiveTransformScale(combined);
        if (newScale <= 0.0001) return original with { Transform = combined };
        return original switch
        {
            InkStrokeObject ink => ink with
            {
                Transform = combined,
                Style = ink.Style with
                {
                    Width = (float)Math.Clamp(
                        ink.Style.Normalize().Width * oldScale / newScale, 0.1, 96)
                }
            },
            ShapeObject shape => shape with
            {
                Transform = combined,
                StrokeWidth = (float)Math.Clamp(
                    shape.StrokeWidth * oldScale / newScale, 0.1, 96)
            },
            _ => original with { Transform = combined }
        };
    }

    private static double EffectiveTransformScale(Transform2D transform)
    {
        var determinant = Math.Abs(
            transform.M11 * transform.M22 - transform.M12 * transform.M21);
        return double.IsFinite(determinant) && determinant > 0
            ? Math.Sqrt(determinant)
            : 1;
    }

    private InkStrokeObject? FindInkStrokeAt(PointD point)
    {
        if (_page is null) return null;
        var tolerance = 10 / Math.Max(_zoom, 0.08);
        IEnumerable<CanvasObject> candidates = _spatialIndex.Count == _page.Objects.Count
            ? _spatialIndex.Query(new RectD(
                point.X - tolerance,
                point.Y - tolerance,
                tolerance * 2,
                tolerance * 2))
            : _page.Objects;
        return candidates
            .OfType<InkStrokeObject>()
            .Where(stroke => !stroke.IsHidden &&
                             StrokeGeometry.HitTest(stroke, point, tolerance))
            .OrderByDescending(stroke => stroke.ZIndex)
            .FirstOrDefault();
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
            if (!CanonicalInkPolicy.ShouldAccept(last, pagePoint, force)) return false;
        }
        var sample = new InkPoint(pagePoint.X, pagePoint.Y, 0.65f, 0, 0, (long)pointer.Timestamp);
        var endpointOnly = _gestureTool is EditorTool.Shape or EditorTool.BoxSelect ||
                           (_gestureTool == EditorTool.Highlighter && HighlighterStraightCheckBox.IsChecked == true);
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
            RetainInkCommitPreview(shape, _editVersion + 1);
            OnDocumentChanged(recognizeInk: false, appendedObject: shape);
            return;
        }
        var style = _gestureInkStyle ?? CurrentInkStyle();
        var points = _gestureTool == EditorTool.Highlighter && HighlighterStraightCheckBox.IsChecked == true && _activeInk.Count > 1
            ? new List<InkPoint> { _activeInk[0], SnapHighlighterEnd(_activeInk[0], _activeInk[^1]) }
            : _activeInk.ToList();
        var stroke = new InkStrokeObject
        {
            Points = points,
            Style = style,
            ZIndex = NextZIndex()
        };
        _history.Execute(new AddObjectCommand(_page.Id, stroke), _document);
        RetainInkCommitPreview(stroke, _editVersion + 1);
        OnDocumentChanged(recognizeInk: true, appendedObject: stroke);
    }

    private void RetainInkCommitPreview(CanvasObject canvasObject, int commitVersion)
    {
        _pendingInkCommitPreviews.Add((canvasObject, commitVersion));
        Volatile.Write(ref _inkPreviewCommitVersion, commitVersion);
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
        var shapeKind = SelectedShapeKind();
        var shape = new ShapeObject
        {
            Shape = shapeKind,
            Bounds = ShapeGeometry.BoundsFromDrag(_activeInk[0].Position, _activeInk[^1].Position, shapeKind),
            StartPoint = _activeInk[0].Position,
            EndPoint = _activeInk[^1].Position,
            StrokeColor = _inkColor,
            StrokeWidth = (float)StrokeWidthSlider.Value,
            ZIndex = NextZIndex()
        };
        _history.Execute(new AddObjectCommand(_page.Id, shape), _document);
        RetainInkCommitPreview(shape, _editVersion + 1);
        OnDocumentChanged(recognizeInk: false, appendedObject: shape);
    }

    private void ApplyRealtimeErase()
    {
        if (_page is null || _eraserPath.Count == 0) return;
        var last = _eraserPath[^1];
        IReadOnlyList<PointD> recentPath = _eraserPath.Count > 1 ? [_eraserPath[^2], last] : [last];
        var radius = EraserRadius();
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
        InvalidateInteractionOverlay();
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
            DrawPageBackground(drawingSession, page, region);
            DrawImportedLayer(drawingSession, page);
            if (_temporaryGridVisible) DrawTemporaryGrid(drawingSession, page, region);
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
        if (_eraseDirtyRegions.Count > 0)
            Volatile.Write(ref _erasePreviewCommitVersion, _editVersion);
    }

    private void RestoreEraseSnapshot()
    {
        if (_page is null || _eraseSnapshot is null) return;
        _page.Objects.Clear();
        _page.Objects.AddRange(_eraseSnapshot);
        _spatialIndex.Rebuild(_page.Objects);
        _eraseDirtyRegions.Clear();
        Volatile.Write(ref _erasePreviewCommitVersion, -1);
        InvalidatePageRenderCache();
        InvalidateCanvas();
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
                return LassoSelection.Intersects(item, points);
            }).ToArray();
        _selectedObjects.Clear();
        _selectedObjects.AddRange(selected);
        _selectedObject = selected.Length == 1 ? selected[0] : null;
        ClearTextSelection();
        UpdateSelectionUi();
    }

    private void CommitSegmentErase()
    {
        if (_document is null || _page is null || _eraserPath.Count == 0) return;
        var before = new List<CanvasObject>();
        var after = new List<CanvasObject>();
        foreach (var stroke in _page.Objects.OfType<InkStrokeObject>().Where(stroke => !stroke.IsLocked))
        {
            var fragments = SegmentEraser.Erase(stroke, _eraserPath, EraserRadius());
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
            _eraserPath.Any(point => StrokeGeometry.HitTest(item, point, EraserRadius()))).ToArray();
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
        InvalidateCanvas();
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
        InvalidateCanvas();
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
        ClearTextSelection();
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

    private void OnDocumentChanged(
        bool recognizeInk,
        CanvasObject? appendedObject = null,
        IEnumerable<Guid>? affectedPageIds = null)
    {
        _ = recognizeInk;
        if (_page is not null) _page.UpdatedAt = DateTimeOffset.UtcNow;
        if (_document is not null && _page?.Id == _document.Pages.FirstOrDefault()?.Id)
        {
            _dirtyHomeThumbnailDocumentIds.Add(_document.Id);
            if (_homeThumbnailRefreshCancellations.TryGetValue(
                    _document.Id, out var thumbnailRefresh))
                thumbnailRefresh.Cancel();
        }
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
        if (appendOnly)
            _pendingPageRenderAppends.Enqueue((_page!.Id, appendedObject!));
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
        InvalidateCanvas();
        ScheduleSave(affectedPageIds);
    }

    private void ScheduleSave(IEnumerable<Guid>? affectedPageIds = null)
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        if (affectedPageIds is null)
        {
            if (_page is not null) _pendingThumbnailRefreshPageIds.Add(_page.Id);
        }
        else
        {
            foreach (var pageId in affectedPageIds) _pendingThumbnailRefreshPageIds.Add(pageId);
        }
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
        var pageIds = _pendingThumbnailRefreshPageIds.ToArray();
        _pendingThumbnailRefreshPageIds.Clear();
        foreach (var pageId in pageIds)
        {
            var page = _document?.Pages.FirstOrDefault(item => item.Id == pageId);
            if (page is null) continue;
            if (_notebookPagePreviewCancellation is { } previewCancellation)
                _ = EnsureNotebookPagePreviewAsync(
                    page,
                    _notebookPagePreviewGeneration,
                    previewCancellation.Token,
                    refresh: true);
            if (PageSidebar.Visibility == Visibility.Visible && PageColumn.Width.Value > 0)
                RequestPageThumbnail(page, refresh: true, prioritize: true);
            else
                // When the rail is collapsed, invalidate without paying the rendering cost.
                // Container realization will request the preview when the user opens the rail.
                InvalidatePageThumbnail(page.Id);
        }
    }

    private void PauseThumbnailRefresh()
    {
        _thumbnailRefreshTimer.Stop();
        if (_page is null || !_pageThumbnailLoads.TryGetValue(_page.Id, out var load)) return;
        _pendingThumbnailRefreshPageIds.Add(_page.Id);
        load.Cancel();
    }

    private void ResumeThumbnailRefresh()
    {
        if (_isPointerDown || _pendingThumbnailRefreshPageIds.Count == 0) return;
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
            if (editVersion == _editVersion)
            {
                _hasUnsavedChanges = false;
                if (_dirtyHomeThumbnailDocumentIds.Remove(document.Id))
                    ScheduleHomeThumbnailCacheRefresh(document);
            }
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
            if (_handwritingIndexDocumentId == document.Id &&
                _handwritingIndexCancellation is { IsCancellationRequested: false }) return;
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
                    var sourcePdfRegions = page.RecognizedRegions.Where(region =>
                        string.Equals(region.Source, "Pdf", StringComparison.OrdinalIgnoreCase)).ToArray();
                    var recognizedParts = new List<string>();
                    if (sourcePdfRegions.Length > 0)
                        AddUniqueRecognizedText(recognizedParts, SelectedRegionText(sourcePdfRegions));
                    var recognizedRegions = new List<RecognizedTextRegion>(sourcePdfRegions);
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
                    // Yield both CPU and package power between pages. A notebook index is
                    // background maintenance, not a reason to sustain boost clocks.
                    await Task.Delay(250, cancellationToken);
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

    private void RequestPageThumbnail(NotePage page, bool refresh = false, bool prioritize = false)
    {
        if (_pageThumbnailRenderer is null || (!refresh && _pageThumbnailCache.ContainsKey(page.Id))) return;
        var priorityGeneration = 0;
        if (prioritize)
        {
            priorityGeneration = ++_thumbnailPriorityGeneration;
            CancelPageThumbnailLoadsExcept(page.Id);
        }
        if (_pageThumbnailLoads.Remove(page.Id, out var previous))
            previous.Cancel();
        var cancellation = new CancellationTokenSource();
        _pageThumbnailLoads[page.Id] = cancellation;
        _ = LoadPageThumbnailAsync(page, cancellation, priorityGeneration);
    }

    private async Task LoadPageThumbnailAsync(NotePage page, CancellationTokenSource cancellation,
        int priorityGeneration = 0)
    {
        try
        {
            if (_pageThumbnailRenderer is null) return;
            var thumbnailSize = PageThumbnailSize(page);
            var bytes = await _pageThumbnailRenderer.RenderAsync(page, thumbnailSize.Width,
                thumbnailSize.Height, cancellation.Token);
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
                DecodePixelWidth = thumbnailSize.Width,
                DecodePixelHeight = thumbnailSize.Height
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
                _pageThumbnailLoads.Remove(page.Id);
            cancellation.Dispose();
            if (priorityGeneration != 0 && priorityGeneration == _thumbnailPriorityGeneration)
                QueueMissingVisiblePageThumbnails(page.Id);
        }
    }

    private void CancelPageThumbnailLoadsExcept(Guid pageId)
    {
        foreach (var (loadedPageId, cancellation) in _pageThumbnailLoads.ToArray())
        {
            if (loadedPageId == pageId) continue;
            _pageThumbnailLoads.Remove(loadedPageId);
            cancellation.Cancel();
        }
    }

    private void QueueMissingVisiblePageThumbnails(Guid priorityPageId)
    {
        if (PageSidebar.Visibility != Visibility.Visible || PageColumn.Width.Value <= 0) return;
        foreach (var page in _pages)
        {
            if (page.Id == priorityPageId || _pageThumbnailCache.ContainsKey(page.Id) ||
                PageList.ContainerFromItem(page) is not ListViewItem)
                continue;
            RequestPageThumbnail(page);
        }
    }

    private void UpdatePageThumbnailContainer(NotePage page, ListViewItem container)
    {
        var isSelected = ReferenceEquals(PageList.SelectedItem, page);
        container.Background = new SolidColorBrush(isSelected
            ? Color.FromArgb(42, 56, 189, 248)
            : Color.FromArgb(0, 0, 0, 0));
        // The thumbnail frame carries selection. Avoid a second full-row outline, which can
        // linger on recycled containers and visually collide with the scrollbar.
        container.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        container.BorderThickness = new Thickness(0);
        if (container.ContentTemplateRoot is not FrameworkElement root) return;
        var frame = root.FindName("PageThumbnailFrame") as Border;
        var image = root.FindName("PageThumbnailImage") as Image;
        var loading = root.FindName("PageThumbnailLoading") as ProgressRing;
        if (root.FindName("PageTitleText") is TextBlock title)
        {
            title.Text = page.Title;
            title.Foreground = new SolidColorBrush(isSelected
                ? Color.FromArgb(255, 238, 248, 255)
                : Color.FromArgb(255, 166, 171, 181));
            title.FontWeight = isSelected
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
        }
        ToolTipService.SetToolTip(root, page.Title);
        var thumbnailSize = PageThumbnailSize(page);
        if (frame is not null)
        {
            frame.Width = thumbnailSize.Width;
            frame.Height = thumbnailSize.Height;
            frame.BorderBrush = new SolidColorBrush(isSelected
                ? Color.FromArgb(255, 56, 189, 248)
                : Color.FromArgb(255, 52, 54, 58));
            frame.BorderThickness = new Thickness(isSelected ? 2 : 1);
        }
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

    private static (int Width, int Height) PageThumbnailSize(NotePage page)
        => ThumbnailSize(page, PageThumbnailMaxWidth, PageThumbnailMaxHeight);

    private static (int Width, int Height) ThumbnailSize(NotePage page, int maxWidth, int maxHeight)
    {
        var pageWidth = Math.Max(1, page.Size.Width);
        var pageHeight = Math.Max(1, page.Size.Height);
        var scale = Math.Min(maxWidth / pageWidth, maxHeight / pageHeight);
        return (
            Math.Max(1, (int)Math.Round(pageWidth * scale)),
            Math.Max(1, (int)Math.Round(pageHeight * scale)));
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
        if (selected is not null && !_pageThumbnailCache.ContainsKey(selected.Id))
            RequestPageThumbnail(selected, prioritize: true);
        SelectPage(selected);
        foreach (var page in e.RemovedItems.OfType<NotePage>())
            if (PageList.ContainerFromItem(page) is ListViewItem container)
                UpdatePageThumbnailContainer(page, container);
        foreach (var page in e.AddedItems.OfType<NotePage>())
            if (PageList.ContainerFromItem(page) is ListViewItem container)
                UpdatePageThumbnailContainer(page, container);
    }

    private void OnPageDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _pageDragOrder = _document?.Pages.Select(page => page.Id).ToArray();
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnPageDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_document is null || _pageDragOrder is null) return;
        var before = _pageDragOrder;
        _pageDragOrder = null;
        var after = _pages.Select(page => page.Id).ToArray();
        if (before.SequenceEqual(after)) return;

        _history.Execute(new ReorderPagesCommand(before, after), _document);
        RenumberAutomaticPages();
        RefreshRealizedPageLabels();
        MarkFullDocumentDirty();
        StatusText.Text = "Pages reordered";
    }

    private async void OnNewNotebookClick(object sender, RoutedEventArgs e) =>
        await CreateDocumentAsync(DocumentKind.PagedNotebook, "Untitled notebook");

    private async void OnNewCanvasClick(object sender, RoutedEventArgs e) =>
        await CreateDocumentAsync(DocumentKind.InfiniteCanvas, "Untitled canvas");

    private async Task CreateDocumentAsync(DocumentKind kind, string title)
    {
        if (_repository is null) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var destinationFolderId = _selectedFolderId;
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
        else
        {
            var page = new NotePage
            {
                Title = "Page 1",
                Template = CreatePageTemplate(defaultKind, _userPreferences.DefaultPageColor)
            };
            document.Pages.Add(page);
            document.Sections[0].PageIds.Add(page.Id);
        }
        await _repository.SaveAsync(document);
        CacheOpenDocument(document, 0);
        ScheduleHomeThumbnailCacheRefresh(document);
        if (destinationFolderId is { } folderId)
        {
            _userPreferences.DocumentFolders[document.Id.ToString("D")] = folderId.ToString("D");
            await PersistUserPreferencesAsync("Created notebook in folder");
        }
        _selectedFolderId = destinationFolderId;
        await RefreshLibraryAsync(document.Id);
        DiagnosticsLog.Info("document.create_completed",
            ("document_id", document.Id),
            ("kind", kind),
            ("elapsed_ms", MillisecondsSince(started)));
    }

    private void OnAddPageClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var currentIndex = _page is null
            ? _document.Pages.Count - 1
            : _document.Pages.FindIndex(item => item.Id == _page.Id);
        var insertIndex = Math.Clamp(currentIndex + 1, 0, _document.Pages.Count);
        var page = new NotePage
        {
            Title = $"Page {_document.Pages.Count + 1}",
            Template = CreatePageTemplate(_document.Settings.DefaultPageTemplateKind,
                _document.Settings.DefaultPaperColor)
        };
        _history.Execute(new AddPageCommand(
            page, insertIndex, _document.Sections.FirstOrDefault()?.Id), _document);
        _pages.Insert(insertIndex, page);
        RenumberAutomaticPages();
        RefreshRealizedPageLabels();
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
        InvalidatePageRenderCache();
        InvalidateCanvas();
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
        RenumberAutomaticPages();
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
        var narrowToolbar = e.NewSize.Width < 760;
        ToolbarOverflowActionsButton.Visibility = Visibility.Visible;
        AutosavedStatusBadge.Visibility = narrowToolbar ? Visibility.Collapsed : Visibility.Visible;
        NotebookTabs.Margin = new Thickness(40, 0, narrowToolbar ? 6 : 100, 0);
        PresetScrollViewer.MinWidth = narrowToolbar ? 72 : 120;

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
        if (!_applyingToolbarPreset) SetActiveToolbarPreset(null);
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
        QuickInkSettingsTitle.Text = _colorTool == EditorTool.Highlighter
            ? "Highlighter settings"
            : "Pen settings";
        HighlighterStraightCheckBox.Visibility = _colorTool == EditorTool.Highlighter
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveCurrentInkPresetMenuItem.Text = _colorTool == EditorTool.Highlighter
            ? "Save highlighter"
            : "Save pen";
        ShapeChoiceButton.Visibility = tool == EditorTool.Shape ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(QuickInkSettingsButton,
            _colorTool == EditorTool.Highlighter ? "Highlighter size and color" : "Pen size and color");
        foreach (var toggle in ToolButtons.Children.OfType<ToggleButton>()
                     .Where(toggle => toggle.Tag is string))
            toggle.IsChecked = selected is not null ? toggle == selected : string.Equals(toggle.Tag as string, tool.ToString(), StringComparison.Ordinal);
        MoreToolsButton.Background = tool is EditorTool.SegmentEraser or EditorTool.Text or
            EditorTool.Shape or EditorTool.BoxSelect
            ? new SolidColorBrush(Color.FromArgb(90, 56, 189, 248))
            : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        StyleBrushSettingsButton.Visibility = tool == EditorTool.Style
            ? Visibility.Visible
            : Visibility.Collapsed;
        EraserSettingsButton.Visibility = tool is EditorTool.SegmentEraser or EditorTool.StrokeEraser
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (tool == EditorTool.Style)
        {
            var activePreset = _activeStylePresetId is { } activeId
                ? _userPreferences.ToolbarPresets.FirstOrDefault(preset =>
                    preset.Id == activeId && IsPenPreset(preset))
                : null;
            activePreset ??= _userPreferences.ToolbarPresets.FirstOrDefault(IsPenPreset);
            if (activePreset is not null) ApplyStylePreset(activePreset, showStatus: false);
            else
            {
                _activeStylePresetId = null;
                _styleToolPickMode = true;
                _styleToolColor = _penColor;
                _styleToolWidth = (float)StrokeWidthSlider.Value;
            }
            UpdateStyleToolUi();
            DispatcherQueue.TryEnqueue(() =>
                StyleBrushSettingsButton.Flyout?.ShowAt(StyleBrushSettingsButton));
            StatusText.Text = activePreset is null
                ? "Style brush • save a pen preset or sample an object"
                : "Style brush • drag over objects to apply the selected preset";
        }
        else StatusText.Text = tool.ToString();
    }

    private void OnShapeChoiceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem { Tag: string value } ||
            !Enum.TryParse<ShapeKind>(value, out var shape)) return;
        SetSelectedShapeKind(shape, syncInspector: true);
    }

    private void OnInspectorShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ShapePicker.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !Enum.TryParse<ShapeKind>(value, out var shape)) return;
        SetSelectedShapeKind(shape, syncInspector: false);
    }

    private void SetSelectedShapeKind(ShapeKind shape, bool syncInspector)
    {
        _selectedShapeKind = shape;
        ShapeChoiceLabel.Text = ShapeDisplayName(shape);
        ToolTipService.SetToolTip(ShapeChoiceButton, $"Shape: {ShapeDisplayName(shape)}");
        foreach (var menuItem in ShapeChoiceFlyout.Items.OfType<RadioMenuFlyoutItem>())
            menuItem.IsChecked = menuItem.Tag is string value &&
                                 string.Equals(value, shape.ToString(), StringComparison.Ordinal);

        if (!syncInspector) return;
        var inspectorItem = ShapePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag is string value &&
                                    string.Equals(value, shape.ToString(), StringComparison.Ordinal));
        if (inspectorItem is not null && !ReferenceEquals(ShapePicker.SelectedItem, inspectorItem))
            ShapePicker.SelectedItem = inspectorItem;
    }

    private static string ShapeDisplayName(ShapeKind shape) => shape switch
    {
        ShapeKind.RoundedRectangle => "Rounded",
        _ => shape.ToString()
    };

    private void OnStyleModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string mode }) return;
        _styleToolPickMode = mode == "Pick";
        UpdateStyleToolUi();
        StatusText.Text = _styleToolPickMode
            ? "Style tool • click an object to pick its style"
            : "Style tool • click objects to apply the chosen style";
    }

    private void OnStyleBrushFlyoutOpening(object sender, object e) =>
        RebuildStylePresetPicker();

    private void OnStylePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Guid id }) return;
        var preset = _userPreferences.ToolbarPresets.FirstOrDefault(item =>
            item.Id == id && IsPenPreset(item));
        if (preset is null) return;
        ApplyStylePreset(preset, showStatus: true);
    }

    private void ApplyStylePreset(ToolbarPresetPreference preset, bool showStatus)
    {
        _activeStylePresetId = preset.Id;
        _styleToolColor = preset.Color.ToUpperInvariant();
        _styleToolWidth = (float)Math.Clamp(preset.Width, 0.1, 48);
        _styleToolPickMode = false;
        UpdateStyleToolUi();
        RebuildStylePresetPicker();
        if (showStatus)
            StatusText.Text =
                $"Style brush • {preset.Color} • {preset.Width:0.#} pt • drag to apply";
    }

    private void OnStyleBrushSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingStyleTool) return;
        SetStyleBrushSize(e.NewValue);
    }

    private void OnStyleBrushSizeNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingStyleTool) return;
        SetStyleBrushSize(double.IsFinite(args.NewValue) ? args.NewValue : _styleBrushSize);
    }

    private void OnStyleStrokeWidthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingStyleTool) return;
        SetStyleStrokeWidth(e.NewValue);
    }

    private void OnStyleStrokeWidthNumberChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_syncingStyleTool) return;
        SetStyleStrokeWidth(double.IsFinite(args.NewValue) ? args.NewValue : _styleToolWidth);
    }

    private void SetStyleStrokeWidth(double requestedWidth)
    {
        _styleToolWidth = (float)Math.Clamp(Math.Round(requestedWidth, 1), 0.1, 48);
        _activeStylePresetId = null;
        _styleToolPickMode = false;
        UpdateStyleToolUi();
        RebuildStylePresetPicker();
        StatusText.Text =
            $"Style brush • {_styleToolColor} • {_styleToolWidth:0.#} pt • drag to apply";
    }

    private void SetStyleBrushSize(double requestedSize)
    {
        _styleBrushSize = (float)Math.Clamp(Math.Round(requestedSize), 8, 120);
        _syncingStyleTool = true;
        StyleBrushSizeSlider.Value = _styleBrushSize;
        StyleBrushSizeNumberBox.Value = _styleBrushSize;
        _syncingStyleTool = false;
        ScheduleUserPreferencesSave();
        if (_styleBrushPoint is not null) InvalidateInteractionOverlay();
    }

    private void OnEraserSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingEraserSize) return;
        SetEraserSize(e.NewValue);
    }

    private void OnEraserSizeNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingEraserSize) return;
        SetEraserSize(double.IsFinite(args.NewValue) ? args.NewValue : _eraserSize);
    }

    private void SetEraserSize(double requestedSize)
    {
        _eraserSize = Math.Clamp(Math.Round(requestedSize), 4, 96);
        _syncingEraserSize = true;
        EraserSizeSlider.Value = _eraserSize;
        EraserSizeNumberBox.Value = _eraserSize;
        EraserSizeText.Text = $"{_eraserSize:0}";
        _syncingEraserSize = false;
        ScheduleUserPreferencesSave();
        if (_eraserPath.Count > 0) InvalidateInteractionOverlay();
    }

    private double EraserRadius() => Math.Max(2, _eraserSize / 2d);

    private void OnScaleStrokeWidthsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _userPreferences = _userPreferences with
        {
            ScaleStrokeWidthsOnTransform = ScaleStrokeWidthsToggle.IsOn
        };
        ScheduleUserPreferencesSave();
    }

    private void UpdateStyleToolUi()
    {
        _syncingStyleTool = true;
        StyleBrushSizeSlider.Value = _styleBrushSize;
        StyleBrushSizeNumberBox.Value = _styleBrushSize;
        StyleStrokeWidthSlider.Value = _styleToolWidth;
        StyleStrokeWidthNumberBox.Value = _styleToolWidth;
        StyleBrushColorSwatch.Background = new SolidColorBrush(ParseColor(_styleToolColor));
        StyleBrushPresetSummary.Text = _activeStylePresetId is null
            ? $"Current style • {_styleToolColor} • {_styleToolWidth:0.#} pt"
            : $"Preset • {_styleToolColor} • {_styleToolWidth:0.#} pt";
        _syncingStyleTool = false;
        UpdateStyleToolModeButtons();
    }

    private void UpdateStyleToolModeButtons()
    {
        StylePickModeButton.IsChecked = _styleToolPickMode;
        StyleApplyModeButton.IsChecked = !_styleToolPickMode;
    }

    private static bool IsPenPreset(ToolbarPresetPreference preset) =>
        string.Equals(preset.Tool, nameof(EditorTool.Pen), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(preset.Tool, "Pencil", StringComparison.OrdinalIgnoreCase);

    private void RebuildStylePresetPicker()
    {
        if (StylePresetButtons is null) return;
        StylePresetButtons.Children.Clear();
        var presets = _userPreferences.ToolbarPresets.Where(IsPenPreset).ToArray();
        if (presets.Length == 0)
        {
            StylePresetButtons.Children.Add(new TextBlock
            {
                Text = "No saved pen presets yet. Save a pen from the toolbar first.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["HoomNoteMutedTextBrush"]
            });
            return;
        }

        foreach (var preset in presets)
        {
            var row = new Grid { ColumnSpacing = 9 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = new SolidColorBrush(ParseColor(preset.Color)),
                Stroke = new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)),
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center
            });
            var label = new TextBlock
            {
                Text = preset.Color,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var width = new TextBlock
            {
                Text = $"{preset.Width:0.#} pt",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["HoomNoteMutedTextBrush"],
                FontSize = 10
            };
            Grid.SetColumn(width, 2);
            row.Children.Add(width);
            var button = new Button
            {
                Tag = preset.Id,
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(9, 6, 9, 6),
                Background = new SolidColorBrush(
                    preset.Id == _activeStylePresetId
                        ? Color.FromArgb(70, 56, 189, 248)
                        : Color.FromArgb(0, 0, 0, 0))
            };
            button.Click += OnStylePresetClick;
            StylePresetButtons.Children.Add(button);
        }
    }

    private void OnTemporaryGridToolbarClick(object sender, RoutedEventArgs e) =>
        SetTemporaryGridVisible(TemporaryGridToolbarButton.IsChecked == true);

    private void SetTemporaryGridVisible(bool visible)
    {
        _temporaryGridVisible = visible;
        if (TemporaryGridToolbarButton.IsChecked != visible)
            TemporaryGridToolbarButton.IsChecked = visible;
        InvalidatePageRenderCache();
        InvalidateCanvas();
    }

    private void OnTemporaryGridSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTemporaryGridSize) return;
        SetTemporaryGridSize(e.NewValue);
    }

    private void OnTemporaryGridSizeNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingTemporaryGridSize) return;
        SetTemporaryGridSize(double.IsFinite(args.NewValue) ? args.NewValue : _temporaryGridSize);
    }

    private void SetTemporaryGridSize(double requestedSize)
    {
        var size = Math.Clamp(Math.Round(requestedSize * 2) / 2, 8, 128);
        _temporaryGridSize = size;
        _syncingTemporaryGridSize = true;
        TemporaryGridSizeSlider.Value = size;
        TemporaryGridSizeNumberBox.Value = size;
        _syncingTemporaryGridSize = false;
        ScheduleUserPreferencesSave();
        if (!_temporaryGridVisible) return;
        InvalidatePageRenderCache();
        InvalidateCanvas();
    }

    private void OnMinimumZoomChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingZoomLimits) return;
        var minimum = NormalizeZoomPercent(args.NewValue, _minimumZoom * 100d);
        var maximum = _maximumZoom * 100d;
        if (minimum > maximum) maximum = minimum;
        SetZoomLimits(minimum, maximum);
    }

    private void OnMaximumZoomChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingZoomLimits) return;
        var minimum = _minimumZoom * 100d;
        var maximum = NormalizeZoomPercent(args.NewValue, _maximumZoom * 100d);
        if (maximum < minimum) minimum = maximum;
        SetZoomLimits(minimum, maximum);
    }

    private void SetZoomLimits(double minimumPercent, double maximumPercent)
    {
        minimumPercent = NormalizeZoomPercent(minimumPercent, 8);
        maximumPercent = NormalizeZoomPercent(maximumPercent, 800);
        if (minimumPercent > maximumPercent)
            (minimumPercent, maximumPercent) = (maximumPercent, minimumPercent);

        var center = new Point(_canvasWidth / 2d, _canvasHeight / 2d);
        var pageAnchor = _page is null ? default : ScreenToPage(center);
        _minimumZoom = minimumPercent / 100d;
        _maximumZoom = maximumPercent / 100d;
        _syncingZoomLimits = true;
        MinimumZoomNumberBox.Value = minimumPercent;
        MaximumZoomNumberBox.Value = maximumPercent;
        _syncingZoomLimits = false;

        var constrainedZoom = Math.Clamp(_zoom, _minimumZoom, _maximumZoom);
        if (Math.Abs(constrainedZoom - _zoom) > 0.0001)
        {
            StopWheelZoomAnimation(resumeBackgroundWork: false);
            if (_page is null) _zoom = constrainedZoom;
            else ApplyZoomAtAnchor(constrainedZoom, pageAnchor, center);
            UpdateZoomText(showIndicator: true);
            InvalidateCanvas();
        }
        ScheduleUserPreferencesSave();
        StatusText.Text = $"Zoom range • {minimumPercent:0}%–{maximumPercent:0}%";
    }

    private void OnInkSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (StrokeWidthSlider is null) return;
        if (_syncingInkWidth) return;
        SetInkWidth(e.NewValue);
    }

    private void OnQuickStrokeWidthChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingInkWidth || StrokeWidthSlider is null) return;
        SetInkWidth(double.IsFinite(args.NewValue) ? args.NewValue : StrokeWidthSlider.Value);
    }

    private void OnInkSizePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string rawSize } &&
            double.TryParse(rawSize, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var size))
            SetInkWidth(size);
    }

    private void SetInkWidth(double requestedWidth)
    {
        var value = Math.Clamp(Math.Round(requestedWidth, 1), 0.1, 24);
        _syncingInkWidth = true;
        if (StrokeWidthSlider is not null && Math.Abs(StrokeWidthSlider.Value - value) > 0.0001)
            StrokeWidthSlider.Value = value;
        if (QuickStrokeWidthBox is not null && Math.Abs(QuickStrokeWidthBox.Value - value) > 0.0001)
            QuickStrokeWidthBox.Value = value;
        _syncingInkWidth = false;
        var text = $"{value:0.0}";
        if (QuickInkWidthText is not null) QuickInkWidthText.Text = text;
        if (_applyingToolbarPreset || _activeToolbarPresetId is not { } presetId) return;
        var presetIndex = _userPreferences.ToolbarPresets.FindIndex(preset => preset.Id == presetId);
        if (presetIndex < 0)
        {
            SetActiveToolbarPreset(null);
            return;
        }
        var preset = _userPreferences.ToolbarPresets[presetIndex];
        if (Math.Abs(preset.Width - value) < 0.0001) return;
        _userPreferences.ToolbarPresets[presetIndex] = preset with { Width = value };
        ScheduleUserPreferencesSave();
        StatusText.Text = $"Updated saved {preset.Tool.ToLowerInvariant()} size • {value:0.0} pt";
    }

    private void OnQuickInkColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingInkColor) return;
        if (!_applyingToolbarPreset) SetActiveToolbarPreset(null);
        SetInkColor($"#{args.NewColor.R:X2}{args.NewColor.G:X2}{args.NewColor.B:X2}");
    }

    private void OnHighlighterStraightToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var straightLine = HighlighterStraightCheckBox.IsChecked == true;
        _userPreferences = _userPreferences with { HighlighterStraightLine = straightLine };
        if (!_applyingToolbarPreset && _activeToolbarPresetId is { } presetId)
        {
            var presetIndex = _userPreferences.ToolbarPresets.FindIndex(preset => preset.Id == presetId);
            if (presetIndex >= 0 &&
                string.Equals(_userPreferences.ToolbarPresets[presetIndex].Tool,
                    nameof(EditorTool.Highlighter), StringComparison.OrdinalIgnoreCase))
            {
                _userPreferences.ToolbarPresets[presetIndex] =
                    _userPreferences.ToolbarPresets[presetIndex] with { StraightLine = straightLine };
                RebuildPresetToolbar();
                SetActiveToolbarPreset(presetId);
                StatusText.Text = straightLine
                    ? "Updated saved highlighter • straight line"
                    : "Updated saved highlighter • freeform";
            }
        }
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
            ClearTextSelection();
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
            ToolbarRow.Height = new GridLength(44);
            TabsRow.Height = new GridLength(28);
            FooterRow.Height = new GridLength(0);
            EditorOverlay.IsHitTestVisible = true;
        }
        _readMode = enabled;
        ReadModeExitButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
        InvalidateCanvas();
    }

    private void UpdateEmptyState()
    {
        CornerZoomTab.Visibility = _page is null ? Visibility.Collapsed : Visibility.Visible;
        if (_readMode)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            HomeLibrary.Visibility = Visibility.Collapsed;
            return;
        }
        if (_document is null)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            HomeLibrary.Visibility = Visibility.Visible;
            return;
        }
        HomeLibrary.Visibility = Visibility.Collapsed;
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
        SetTemporaryGridVisible(!_temporaryGridVisible);
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
                ClearTextSelection();
                _transformPreview = null;
                _multiTransformPreviews.Clear();
                UpdateSelectionUi();
                InvalidateCanvas();
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
            if (!_readMode) SetTemporaryGridVisible(!_temporaryGridVisible);
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
        InvalidateCanvas();
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

    private async void OnSaveCurrentInkPresetClick(object sender, RoutedEventArgs e) =>
        await AddToolbarPresetAsync(_colorTool == EditorTool.Highlighter
            ? EditorTool.Highlighter
            : EditorTool.Pen);

    private async Task AddToolbarPresetAsync(EditorTool tool)
    {
        if (_userPreferences.ToolbarPresets.Count >= ToolbarPresetLimit)
        {
            ImportInfo.Title = "Toolbar is full";
            ImportInfo.Message =
                $"You can save up to {ToolbarPresetLimit} pen and highlighter presets. " +
                "Right-click a custom preset to remove it.";
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
                Opacity = InkStyle.DefaultHighlighterOpacity,
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
            StraightLine = tool == EditorTool.Highlighter && HighlighterStraightCheckBox.IsChecked == true
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
                    Width = 20, Height = 10, CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(ParseColor(preset.Color)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    BorderThickness = new Thickness(1)
                }
                : new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 18, Height = 18,
                    Fill = new SolidColorBrush(ParseColor(preset.Color)),
                    Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)),
                    StrokeThickness = 1
                };
            var content = new Grid();
            var grip = new Border
            {
                Tag = preset.Id,
                CanDrag = true,
                Width = 7,
                Height = 26,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(4),
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
                Width = 28,
                Height = 32,
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(6),
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
        RebuildStylePresetPicker();
    }

    private void SetActiveToolbarPreset(Guid? presetId)
    {
        _activeToolbarPresetId = presetId;
        if (presetId is not { } id)
        {
            ActivePresetSaveText.Visibility = Visibility.Collapsed;
            return;
        }
        var preset = _userPreferences.ToolbarPresets.FirstOrDefault(item => item.Id == id);
        if (preset is null)
        {
            _activeToolbarPresetId = null;
            ActivePresetSaveText.Visibility = Visibility.Collapsed;
            return;
        }
        ActivePresetSaveText.Text =
            $"{preset.Tool} preset selected • size and stroke mode changes save automatically.";
        ActivePresetSaveText.Visibility = Visibility.Visible;
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
        _applyingToolbarPreset = true;
        try
        {
            ActivateTool(tool);
            _presetOpacity = (float)Math.Clamp(preset.Opacity, 0.05, 1);
            _presetSmoothing = (float)Math.Clamp(preset.Smoothing, 0, 1);
            SetInkColor(preset.Color);
            SetInkWidth(preset.Width);
            if (tool == EditorTool.Highlighter) HighlighterStraightCheckBox.IsChecked = preset.StraightLine;
        }
        finally
        {
            _applyingToolbarPreset = false;
        }
        SetActiveToolbarPreset(id);
        StatusText.Text = $"{preset.Tool} preset selected • settings save automatically";
    }

    private async void OnRemoveToolbarPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: Guid id }) return;
        _userPreferences.ToolbarPresets.RemoveAll(item => item.Id == id);
        if (_activeToolbarPresetId == id) SetActiveToolbarPreset(null);
        if (_activeStylePresetId == id) _activeStylePresetId = null;
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
        if (!_history.Undo(_document)) return;
        if (!pageIds.SequenceEqual(_document.Pages.Select(page => page.Id)))
        {
            RenumberAutomaticPages();
            SyncPageCollection(_page?.Id);
        }
        RebindSelectionAfterHistoryChange();
        OnDocumentChanged(recognizeInk: true, affectedPageIds: _history.LastAffectedPageIds);
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        var pageIds = _document.Pages.Select(page => page.Id).ToArray();
        if (!_history.Redo(_document)) return;
        if (!pageIds.SequenceEqual(_document.Pages.Select(page => page.Id)))
        {
            RenumberAutomaticPages();
            SyncPageCollection(_page?.Id);
        }
        RebindSelectionAfterHistoryChange();
        OnDocumentChanged(recognizeInk: true, affectedPageIds: _history.LastAffectedPageIds);
    }

    private void RebindSelectionAfterHistoryChange()
    {
        var selectedIds = (_selectedObjects.Count > 0
                ? _selectedObjects.Select(item => item.Id)
                : _selectedObject is null ? [] : [_selectedObject.Id])
            .ToArray();
        ClearTransformPreviewState();
        Volatile.Write(ref _transformPreviewCommitVersion, -1);
        _transformOriginal = null;
        _multiTransformOriginals = null;
        _transformHandle = TransformHandle.None;
        _selectedObjects.Clear();
        if (_page is not null)
            _selectedObjects.AddRange(SelectionRebinder.Rebind(selectedIds, _page.Objects));
        _selectedObject = _selectedObjects.Count == 1 ? _selectedObjects[0] : null;
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
        var hostWindow = HostWindow;
        if (_document is null || _page is null || _assetStore is null || hostWindow is null) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(hostWindow));
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

    private async Task AddImageAssetAsync(
        string assetHash,
        string displayName,
        PointD? placementCenter = null)
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
        var center = placementCenter is { } requested
            ? ClampPointToPage(requested)
            : new PointD(_page.Size.Width / 2d, _page.Size.Height / 2d);
        var left = Math.Clamp(center.X - width / 2d, 0, Math.Max(0, _page.Size.Width - width));
        var top = Math.Clamp(center.Y - height / 2d, 0, Math.Max(0, _page.Size.Height - height));
        var image = new ImageObject
        {
            AssetHash = assetHash,
            AltText = Path.GetFileNameWithoutExtension(displayName),
            Bounds = new RectD(left, top, width, height),
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
        if (_document is null || _page is null || _selectedObject is not { } selected ||
            selected is not ImageObject and not ShapeObject) return;
        var updated = selected with { IsLocked = !selected.IsLocked };
        var objectName = selected is ShapeObject ? "shape" : "image";
        _history.Execute(new ReplaceObjectsCommand(_page.Id, [selected], [updated],
            updated.IsLocked ? $"Lock {objectName}" : $"Unlock {objectName}"), _document);
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
        if (_selectedTextRegions.Count > 0)
        {
            var text = SelectedRegionText();
            if (string.IsNullOrWhiteSpace(text)) return;
            var textPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            textPackage.SetText(text);
            Clipboard.SetContent(textPackage);
            StatusText.Text = $"Copied {_selectedTextRegions.Count} text region(s)";
            return;
        }
        var selected = SelectedCanvasObjects();
        if (selected.Count == 0) return;
        _internalClipboard = JsonSerializer.Serialize(selected, HoomNoteJson.Options);
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetData(CanvasClipboardFormat, _internalClipboard);
        var plainText = string.Join(Environment.NewLine,
            selected.OfType<RichTextObject>()
                .Select(item => item.Content.PlainText)
                .Where(item => !string.IsNullOrWhiteSpace(item)));
        if (!string.IsNullOrWhiteSpace(plainText)) package.SetText(plainText);
        Clipboard.SetContent(package);
        StatusText.Text = $"Copied {selected.Count} object(s)";
    }

    private string SelectedRegionText() => SelectedRegionText(_selectedTextRegions);

    private static string SelectedRegionText(IReadOnlyCollection<RecognizedTextRegion> selectedRegions)
    {
        if (selectedRegions.Count == 0) return string.Empty;
        var ordered = selectedRegions
            .OrderBy(region => region.Bounds.Top)
            .ThenBy(region => region.Bounds.Left)
            .ToArray();
        var lines = new List<List<RecognizedTextRegion>>();
        foreach (var region in ordered)
        {
            var line = lines.LastOrDefault();
            var lineHeight = line?.Max(item => item.Bounds.Height) ?? 0;
            if (line is null ||
                Math.Abs(line.Average(item => item.Bounds.Center.Y) - region.Bounds.Center.Y) >
                Math.Max(3, Math.Max(lineHeight, region.Bounds.Height) * 0.65))
            {
                lines.Add([region]);
            }
            else
            {
                line.Add(region);
            }
        }
        return string.Join(Environment.NewLine, lines.Select(line =>
            string.Join(' ', line.OrderBy(region => region.Bounds.Left)
                .Select(region => region.Text.Trim())
                .Where(text => text.Length > 0))));
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopySelectionToClipboard();

    private void OnCutClick(object sender, RoutedEventArgs e)
    {
        if (_selectedTextRegions.Count > 0)
        {
            CopySelectionToClipboard();
            StatusText.Text = "Copied source text • imported PDF text is read-only";
            return;
        }
        if (SelectedCanvasObjects().Count == 0) return;
        CopySelectionToClipboard();
        OnDeleteClick(sender, e);
    }

    private async void OnPasteClick(object sender, RoutedEventArgs e) => await PasteSelectionAsync();

    private async Task PasteSelectionAsync(PointD? targetPoint = null)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastPasteTimestamp != 0 &&
            (now - _lastPasteTimestamp) * 1000d / System.Diagnostics.Stopwatch.Frequency < 300) return;
        if (!await _pasteGate.WaitAsync(0)) return;
        _lastPasteTimestamp = now;
        try
        {
            await PasteSelectionCoreAsync(targetPoint);
        }
        finally
        {
            _pasteGate.Release();
        }
    }

    private async Task PasteSelectionCoreAsync(PointD? targetPoint)
    {
        if (_document is null || _page is null || _assetStore is null) return;
        string? json = null;
        try
        {
            var view = Clipboard.GetContent();
            if (view.Contains(CanvasClipboardFormat) && await view.GetDataAsync(CanvasClipboardFormat) is string clipboardJson)
                json = clipboardJson;
            else if (await TryPasteImageAsync(view, targetPoint)) return;
            else if (view.Contains(StandardDataFormats.Text))
            {
                var text = await view.GetTextAsync();
                if (!string.IsNullOrWhiteSpace(text)) PasteTextAt(text, targetPoint);
                return;
            }
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
        var translation = new PointD(24, 24);
        if (targetPoint is { } requestedTarget)
        {
            var sourceBounds = CombinedBounds(source);
            var target = ClampPointToPage(requestedTarget);
            translation = new PointD(
                target.X - sourceBounds.Center.X,
                target.Y - sourceBounds.Center.Y);
        }
        var pasted = source.Select((item, index) =>
        {
            CanvasObject clone = item with
            {
                Id = idMap[item.Id],
                IsLocked = false,
                Transform = item.Transform.Then(Transform2D.Translation(translation.X, translation.Y)),
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

    private void PasteTextAt(string text, PointD? targetPoint)
    {
        if (_document is null || _page is null) return;
        var target = ClampPointToPage(targetPoint ??
            new PointD(_page.Size.Width / 2d, _page.Size.Height / 2d));
        var width = Math.Min(360, Math.Max(180, _page.Size.Width * 0.55));
        var height = 150d;
        var left = Math.Clamp(target.X - width / 2d, 0, Math.Max(0, _page.Size.Width - width));
        var top = Math.Clamp(target.Y - 20, 0, Math.Max(0, _page.Size.Height - height));
        var pastedText = new RichTextObject
        {
            Bounds = new RectD(left, top, width, height),
            Content = CreateTextDocument(text, DefaultTextColor()),
            ZIndex = NextZIndex()
        };
        _history.Execute(new AddObjectCommand(_page.Id, pastedText), _document);
        SelectSingleObject(pastedText);
        OnDocumentChanged(recognizeInk: false, appendedObject: pastedText);
        ActivateTool(EditorTool.Select);
        StatusText.Text = "Pasted text";
    }

    private async Task<bool> TryPasteImageAsync(DataPackageView view, PointD? targetPoint)
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
                await AddImageAssetAsync(assetHash, file.Name, targetPoint);
                StatusText.Text = $"Pasted {file.Name}";
                return true;
            }
        }
        if (!view.Contains(StandardDataFormats.Bitmap)) return false;
        var reference = await view.GetBitmapAsync();
        using var randomAccess = await reference.OpenReadAsync();
        await using var inputStream = randomAccess.AsStreamForRead();
        var hash = await _assetStore.AddAsync(inputStream, ".png");
        await AddImageAssetAsync(hash, "Pasted image.png", targetPoint);
        StatusText.Text = "Pasted image";
        return true;
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var hostWindow = HostWindow;
        if (_importService is null || _repository is null || hostWindow is null) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(hostWindow));
            picker.FileTypeFilter.Add(".pdf");
            picker.FileTypeFilter.Add(".ppt");
            picker.FileTypeFilter.Add(".pptx");
            picker.FileTypeFilter.Add(".sdocx");
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;
            if (files.Count > 1)
            {
                var options = await ShowBatchImportOptionsAsync(
                    files.Select(file => file.Path).ToArray());
                if (options is null) return;
                await ImportDocumentsBatchAsync(
                    files.Select(file => new BatchImportSource(file.Path, string.Empty)).ToArray(),
                    rootFolderName: null,
                    options);
                return;
            }

            var file = files[0];
            StatusText.Text = "Importing document…";
            var request = await ShowImportOptionsAsync(file.Path);
            if (request is null) return;
            if (_document is null)
            {
                await ImportDocumentsBatchAsync(
                    [new BatchImportSource(file.Path, string.Empty)],
                    rootFolderName: null,
                    new BatchImportOptions(
                        false,
                        Path.GetFileNameWithoutExtension(file.Path),
                        request.PageIndexes,
                        request.Margin,
                        request.RotationDegrees));
                return;
            }
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
                InvalidateCanvas();
            }
            ImportInfo.Title = $"Imported {result.DisplayName}";
            ImportInfo.Message = result.Warnings.Count == 0
                ? $"{result.Pages.Count} page(s) are ready for annotation."
                : string.Join(" ", result.Warnings);
            ImportInfo.Severity = result.Warnings.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            ImportInfo.IsOpen = true;
            OnDocumentChanged(recognizeInk: false);
            await SaveNowAsync();
        }
        catch (Exception exception) { ShowError("Import failed.", exception); }
    }

    private async void OnImportSamsungFilesClick(object sender, RoutedEventArgs e)
    {
        var hostWindow = HostWindow;
        if (_importService is null || _repository is null || hostWindow is null) return;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(hostWindow));
            picker.FileTypeFilter.Add(".sdocx");
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0) return;
            var sources = files
                .Select(file => new BatchImportSource(file.Path, string.Empty))
                .ToArray();
            var options = await ShowBatchImportOptionsAsync(
                sources.Select(source => source.SourcePath).ToArray());
            if (options is null) return;
            await ImportDocumentsBatchAsync(sources, rootFolderName: null, options);
        }
        catch (Exception exception) { ShowError("Samsung Notes files could not be imported.", exception); }
    }

    private async void OnImportSamsungFolderClick(object sender, RoutedEventArgs e)
    {
        var hostWindow = HostWindow;
        if (_importService is null || _repository is null || hostWindow is null) return;
        try
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(hostWindow));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            StatusText.Text = "Scanning Samsung Notes folder…";
            var sources = await Task.Run(() =>
                SamsungNotesBulkImportDiscovery.DiscoverFolder(folder.Path));
            var rootName = Path.GetFileName(folder.Path.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var batchSources = sources
                .Select(source => new BatchImportSource(source.SourcePath, source.RelativeFolder))
                .ToArray();
            if (batchSources.Length == 0)
            {
                ShowNoImportFilesFound("Choose a folder containing exported .sdocx files.");
                return;
            }
            var options = await ShowBatchImportOptionsAsync(
                batchSources.Select(source => source.SourcePath).ToArray());
            if (options is null) return;
            await ImportDocumentsBatchAsync(batchSources,
                string.IsNullOrWhiteSpace(rootName) ? "Samsung Notes import" : rootName,
                options);
        }
        catch (Exception exception) { ShowError("Samsung Notes folder could not be imported.", exception); }
    }

    private async Task ImportDocumentsBatchAsync(
        IReadOnlyList<BatchImportSource> sources,
        string? rootFolderName,
        BatchImportOptions options)
    {
        if (_importService is null || _repository is null) return;
        var uniqueSources = sources
            .Where(source => File.Exists(source.SourcePath))
            .GroupBy(source => Path.GetFullPath(source.SourcePath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (uniqueSources.Length == 0)
        {
            ShowNoImportFilesFound("Choose PDF, PowerPoint, or Samsung Notes files to import.");
            return;
        }

        await SaveNowAsync();
        PauseBackgroundRecognition();
        PauseThumbnailRefresh();
        var importedIds = new List<Guid>();
        var warnings = new List<string>();
        var failures = new List<string>();
        try
        {
            var importRootId = string.IsNullOrWhiteSpace(rootFolderName)
                ? _selectedFolderId
                : EnsureImportFolder(rootFolderName, _selectedFolderId);
            HoomNoteDocument? combinedDocument = options.CombineIntoOneNotebook
                ? HoomNoteDocument.Create(options.CombinedNotebookName, DocumentKind.PagedNotebook)
                : null;
            DateTime? combinedUpdatedAt = null;
            for (var index = 0; index < uniqueSources.Length; index++)
            {
                var source = uniqueSources[index];
                var fileName = Path.GetFileName(source.SourcePath);
                StatusText.Text = $"Importing file {index + 1} of {uniqueSources.Length} • {fileName}";
                try
                {
                    var result = await _importService.ImportAsync(new ImportRequest(
                        source.SourcePath,
                        options.PageIndexes,
                        false,
                        options.Margin,
                        options.RotationDegrees));
                    var document = combinedDocument ?? HoomNoteDocument.Create(
                        Path.GetFileNameWithoutExtension(source.SourcePath), DocumentKind.PagedNotebook);
                    foreach (var page in result.Pages)
                    {
                        var importedPage = combinedDocument is null
                            ? page
                            : page with { Title = $"Page {document.Pages.Count + 1}" };
                        document.Pages.Add(importedPage);
                        document.Sections[0].PageIds.Add(importedPage.Id);
                    }
                    if (combinedDocument is null &&
                        document.Pages.FirstOrDefault() is { } firstPage)
                    {
                        document.Settings = document.Settings with
                        {
                            DefaultPageTemplateKind = firstPage.Template.Kind,
                            DefaultPaperColor = firstPage.Template.PaperColor
                        };
                    }
                    var modified = File.GetLastWriteTimeUtc(source.SourcePath);
                    if (combinedDocument is null)
                    {
                        if (modified > DateTime.MinValue) document.UpdatedAt = modified;
                        await _repository.SaveAsync(document);
                        var destinationFolder = EnsureImportFolderPath(
                            source.RelativeFolder, importRootId);
                        if (destinationFolder is { } folderId)
                            _userPreferences.DocumentFolders[document.Id.ToString("D")] =
                                folderId.ToString("D");
                        _userPreferences.NotebookOrder.Add(document.Id.ToString("D"));
                        importedIds.Add(document.Id);
                    }
                    else if (modified > DateTime.MinValue &&
                             (combinedUpdatedAt is null || modified > combinedUpdatedAt))
                    {
                        combinedUpdatedAt = modified;
                    }
                    warnings.AddRange(result.Warnings.Select(warning => $"{fileName}: {warning}"));
                    DiagnosticsLog.Info("batch_import.file_complete",
                        ("pages", result.Pages.Count),
                        ("combined", combinedDocument is not null));
                }
                catch (Exception exception)
                {
                    failures.Add($"{fileName}: {exception.Message}");
                    DiagnosticsLog.Error("batch_import.file_failed", exception,
                        ("file", fileName));
                }
            }

            if (combinedDocument is { Pages.Count: > 0 })
            {
                if (combinedDocument.Pages[0] is { } firstPage)
                {
                    combinedDocument.Settings = combinedDocument.Settings with
                    {
                        DefaultPageTemplateKind = firstPage.Template.Kind,
                        DefaultPaperColor = firstPage.Template.PaperColor
                    };
                }
                if (combinedUpdatedAt is { } modified) combinedDocument.UpdatedAt = modified;
                await _repository.SaveAsync(combinedDocument);
                if (importRootId is { } folderId)
                    _userPreferences.DocumentFolders[combinedDocument.Id.ToString("D")] =
                        folderId.ToString("D");
                _userPreferences.NotebookOrder.Add(combinedDocument.Id.ToString("D"));
                importedIds.Add(combinedDocument.Id);
            }

            await PersistUserPreferencesAsync(
                options.CombineIntoOneNotebook
                    ? $"Imported {uniqueSources.Length - failures.Count} file(s) into one notebook"
                    : $"Imported {importedIds.Count} notebook(s)");
            await RefreshLibraryAsync();
            if (importedIds.FirstOrDefault() is { } firstId && firstId != Guid.Empty)
            {
                await LoadDocumentAsync(firstId);
                SelectLibraryDocument(firstId);
            }
            var successfulFiles = uniqueSources.Length - failures.Count;
            ImportInfo.Title = options.CombineIntoOneNotebook
                ? failures.Count == 0
                    ? $"Combined {successfulFiles} files into one notebook"
                    : $"Combined {successfulFiles} of {uniqueSources.Length} files"
                : failures.Count == 0
                    ? $"Imported {importedIds.Count} notebook(s)"
                    : $"Imported {importedIds.Count} of {uniqueSources.Length} notebook(s)";
            ImportInfo.Message = failures.Count > 0
                ? string.Join(Environment.NewLine, failures.Take(5))
                : warnings.Count > 0
                    ? string.Join(" ", warnings.Take(5))
                    : options.CombineIntoOneNotebook
                        ? "Every source was appended in the selected order."
                        : "Each source file is now a separate notebook.";
            ImportInfo.Severity = failures.Count > 0
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
            ImportInfo.IsOpen = true;
            StatusText.Text = options.CombineIntoOneNotebook
                ? $"Import complete • {successfulFiles} file(s), one notebook"
                : $"Import complete • {importedIds.Count} notebook(s)";
        }
        finally
        {
            ResumeBackgroundRecognition();
            ResumeThumbnailRefresh();
        }
    }

    private void ShowNoImportFilesFound(string message)
    {
        ImportInfo.Title = "No importable files found";
        ImportInfo.Message = message;
        ImportInfo.Severity = InfoBarSeverity.Informational;
        ImportInfo.IsOpen = true;
        StatusText.Text = "No importable files found";
    }

    private Guid EnsureImportFolder(string name, Guid? parentId)
    {
        var existing = _userPreferences.NotebookFolders.FirstOrDefault(folder =>
            folder.ParentId == parentId &&
            folder.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;
        var created = new NotebookFolderPreference { ParentId = parentId, Name = name };
        _userPreferences.NotebookFolders.Add(created);
        return created.Id;
    }

    private Guid? EnsureImportFolderPath(string relativeFolder, Guid? parentId)
    {
        var current = parentId;
        foreach (var segment in relativeFolder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            current = EnsureImportFolder(segment, current);
        return current;
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var hostWindow = HostWindow;
        if (_document is null || _packageService is null || _vectorExportService is null || hostWindow is null) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = SanitizeFileName(_document.Title) };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(hostWindow));
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

    private async Task<BatchImportOptions?> ShowBatchImportOptionsAsync(
        IReadOnlyList<string> sourcePaths)
    {
        var suggestedName = sourcePaths.Count == 0
            ? "Combined import"
            : Path.GetFileNameWithoutExtension(sourcePaths[0]);
        var combine = new CheckBox
        {
            Content = "Import all files into one notebook",
            IsChecked = false
        };
        var combineDescription = new TextBlock
        {
            Text = "Off by default. Each file will become its own notebook.",
            Foreground = (Brush)Application.Current.Resources["HoomNoteMutedTextBrush"],
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        var combinedName = new TextBox
        {
            Header = "Combined notebook name",
            Text = suggestedName,
            MaxLength = LibraryNamePolicy.MaxLength,
            IsEnabled = false
        };
        combine.Checked += (_, _) =>
        {
            combinedName.IsEnabled = true;
            combineDescription.Text =
                "Files will be appended to one notebook in the order shown below.";
        };
        combine.Unchecked += (_, _) =>
        {
            combinedName.IsEnabled = false;
            combineDescription.Text =
                "Off by default. Each file will become its own notebook.";
        };

        var range = new TextBox
        {
            Header = "Pages from each file",
            PlaceholderText = "All, or 1-3, 7"
        };
        var rotation = new ComboBox
        {
            Header = "Rotate PDF or slide pages",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        foreach (var degrees in new[] { 0, 90, 180, 270 })
            rotation.Items.Add(new ComboBoxItem { Content = $"{degrees}°", Tag = degrees });
        rotation.SelectedIndex = 0;
        var margin = new NumberBox
        {
            Header = "PDF or slide page margin",
            Minimum = 0,
            Maximum = 200,
            Value = 0,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var filePreview = string.Join(Environment.NewLine,
            sourcePaths.Take(6).Select((path, index) =>
                $"{index + 1}. {Path.GetFileName(path)}"));
        if (sourcePaths.Count > 6)
            filePreview += $"{Environment.NewLine}…and {sourcePaths.Count - 6} more";
        var files = new TextBlock
        {
            Text = filePreview,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["HoomNoteMutedTextBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120
        };
        var content = new StackPanel { Spacing = 10, Width = 390 };
        content.Children.Add(new TextBlock
        {
            Text = $"{sourcePaths.Count} files selected",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(files);
        content.Children.Add(combine);
        content.Children.Add(combineDescription);
        content.Children.Add(combinedName);
        content.Children.Add(range);
        content.Children.Add(rotation);
        content.Children.Add(margin);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Import multiple documents",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 540,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var normalizedName = LibraryNamePolicy.Normalize(combinedName.Text)
                             ?? "Combined import";
        return new BatchImportOptions(
            combine.IsChecked == true,
            normalizedName,
            ParsePageRange(range.Text),
            double.IsFinite(margin.Value) ? margin.Value : 0,
            rotation.SelectedItem is ComboBoxItem { Tag: int rotationDegrees }
                ? rotationDegrees
                : 0);
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
        if (_document is not null) content.Children.Add(replace);
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
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
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
            await Task.Delay(100, _searchDebounce.Token);
            var searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            DiagnosticsLog.Info("search.started", ("query_length", query.Length));
            var normalizedQuery = NormalizeSearchText(query);
            var results = _allDocuments
                .Select(summary => (
                    Result: new SearchResult(summary.Id, null, summary.Title, "Notebook",
                        "Notebook name", "notebook title"),
                    Score: NameSearchScore(normalizedQuery, summary.Title)))
                .Concat(_userPreferences.NotebookFolders.Select(folder => (
                    Result: new SearchResult(Guid.Empty, folder.Id, folder.Name, "Folder",
                        "Folder name", "folder title"),
                    Score: NameSearchScore(normalizedQuery, folder.Name))))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Result.DocumentTitle, StringComparer.CurrentCultureIgnoreCase)
                .Take(40)
                .Select(item => item.Result)
                .ToArray();
            _searchResults.Clear();
            foreach (var result in results) _searchResults.Add(result);
            SearchHeading.Text = results.Length == 0 ? "No matches" : $"Search results  {results.Length}";
            SearchHeading.Visibility = Visibility.Visible;
            SearchResultsList.Visibility = Visibility.Visible;
            DiagnosticsLog.Info("search.completed", ("query_length", query.Length),
                ("shown_results", results.Length),
                ("elapsed_ms", Math.Round(MillisecondsSince(searchStarted), 1)));
        }
        catch (OperationCanceledException) { }
    }

    private static int NameSearchScore(string normalizedQuery, string candidate)
    {
        var normalizedCandidate = NormalizeSearchText(candidate);
        if (normalizedQuery.Length == 0 || normalizedCandidate.Length == 0) return 0;
        if (normalizedCandidate == normalizedQuery) return 1_000;
        var containsAt = normalizedCandidate.IndexOf(normalizedQuery, StringComparison.Ordinal);
        if (containsAt >= 0) return 850 - Math.Min(containsAt, 100);
        if (normalizedQuery.Length < 4) return 0;
        var fuzzy = FuzzyScore(normalizedQuery, normalizedCandidate);
        return fuzzy >= 560 ? fuzzy : 0;
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
        DiagnosticsLog.Info("search.result_clicked", ("source", result.Source));
        if (result.Source == "folder title" && result.PageId is { } folderId)
        {
            var node = FindFolderNode(FolderTree.RootNodes, folderId);
            if (node is null) return;
            for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
                ancestor.IsExpanded = true;
            _selectedFolderId = folderId;
            FolderTree.SelectedNode = node;
            if (FolderTree.ContainerFromNode(node) is FrameworkElement container)
                container.StartBringIntoView();
            UpdateLibrarySummary();
            UpdateFolderActions();
            return;
        }

        await LoadDocumentAsync(result.DocumentId);
        SelectLibraryDocument(result.DocumentId, revealInLibrary: true);
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
        InvalidateCanvas();
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
        if (_touchInertiaActive || _wheelZoomAnimating || _wheelScrollAnimating ||
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
        if (_wheelScrollAnimating) redraw |= AdvanceWheelScroll();
        if (_searchFlashBounds.Count > 0 && _searchFlashStarted != 0)
            redraw |= AdvanceSearchFlash();
        if (redraw) InvalidateCanvas();
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
        _handwritingIndexCancellation?.Cancel();
    }

    private void ResumeBackgroundRecognition()
    {
        // Handwriting/OCR indexing is intentionally disabled. Search is limited to
        // notebook and folder names until a future recognition implementation is ready.
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
                Opacity = _presetOpacity ?? InkStyle.DefaultHighlighterOpacity,
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

    private ShapeKind SelectedShapeKind() => _selectedShapeKind;

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
        var quickPickerNeedsUpdate = QuickInkColorPicker is not null && QuickInkColorPicker.Color != parsed;
        if (quickPickerNeedsUpdate)
        {
            _syncingInkColor = true;
            QuickInkColorPicker!.Color = parsed;
            _syncingInkColor = false;
        }
        if (QuickInkColorSwatch is not null) QuickInkColorSwatch.Background = new SolidColorBrush(parsed);
    }

    private async Task SaveUserPreferencesAsync()
    {
        _userPreferences = _userPreferences with
        {
            PenColor = _penColor,
            HighlighterColor = _highlighterColor,
            HighlighterStraightLine = HighlighterStraightCheckBox.IsChecked == true,
            TemporaryGridSize = _temporaryGridSize,
            StyleBrushSize = _styleBrushSize,
            EraserSize = _eraserSize,
            ScaleStrokeWidthsOnTransform = ScaleStrokeWidthsToggle.IsOn,
            MinimumZoomPercent = _minimumZoom * 100d,
            MaximumZoomPercent = _maximumZoom * 100d
        };
        await PersistUserPreferencesAsync("Saved settings");
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
            var repairedFolders = NotebookFolderHierarchy.RepairInvalidParents(
                _userPreferences.NotebookFolders);
            if (repairedFolders.Count > 0)
                DiagnosticsLog.Warning("folder.invalid_parents_repaired_before_save",
                    ("count", repairedFolders.Count));
            await App.SaveSharedUserPreferencesAsync(_userSettingsStore, _userPreferences);
            StatusText.Text = status;
            DiagnosticsLog.Info("settings.saved",
                ("folder_count", _userPreferences.NotebookFolders.Count),
                ("nested_folder_count",
                    _userPreferences.NotebookFolders.Count(folder => folder.ParentId is not null)));
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

    private void RenumberAutomaticPages()
    {
        if (_document is null) return;
        for (var index = 0; index < _document.Pages.Count; index++)
        {
            var page = _document.Pages[index];
            if (!IsAutomaticPageTitle(page.Title)) continue;
            page.Title = $"Page {index + 1}";
        }
    }

    private static bool IsAutomaticPageTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) ||
            !title.StartsWith("Page ", StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(title.AsSpan(5), out _);
    }

    private void RefreshRealizedPageLabels()
    {
        foreach (var page in _pages)
            if (PageList.ContainerFromItem(page) is ListViewItem container)
                UpdatePageThumbnailContainer(page, container);
    }

    private void ClearLiveInkGeometryCache()
    {
        foreach (var geometry in _liveInkGeometryChunks) geometry.Dispose();
        _liveInkGeometryChunks.Clear();
        _liveInkChunkStart = 0;
    }

    private void ClearStrokeGeometryCache()
    {
        Interlocked.Exchange(ref _strokeGeometryClearRequested, 1);
    }

    private void ClearStrokeGeometryCacheCore()
    {
        foreach (var entry in _strokeGeometryCache.Values) entry.Geometry.Dispose();
        _strokeGeometryCache.Clear();
        _strokeGeometryLru.Clear();
        _strokeGeometryLruNodes.Clear();
        _strokeGeometryCachedPoints = 0;
    }

    private void ClearImageBitmapCache()
    {
        lock (_pageRenderGate) ClearImageBitmapCacheCore();
    }

    private void ClearImageBitmapCacheCore()
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
        lock (_pageRenderGate)
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
    }

    private void TouchImageBitmap(string assetHash)
    {
        _imageBitmapLru.Remove(assetHash);
        _imageBitmapLru.AddFirst(assetHash);
    }

    private void CacheOpenDocument(HoomNoteDocument document, int? knownPointCount = null)
    {
        _openDocumentCache[document.Id] = document;
        _openDocumentPointCounts[document.Id] = knownPointCount ?? CountInkPoints(document);
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

    private void InvalidatePageRenderCache()
    {
        Interlocked.Exchange(ref _pageRenderInvalidationRequested, 1);
    }

    private void InvalidatePageRenderCacheCore()
    {
        _preloadedFallbackPageId = null;
        ClearNavigationTileCacheCore();
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

    private void ClearNavigationTileCache()
    {
        Interlocked.Exchange(ref _navigationTileClearRequested, 1);
    }

    private void ClearNavigationTileCacheCore()
    {
        foreach (var tile in _navigationTiles.Values) tile.Dispose();
        _navigationTiles.Clear();
        _navigationTileLru.Clear();
        _navigationTileLruNodes.Clear();
        _visibleNavigationTileKeys.Clear();
        _navigationTilePageId = null;
        _navigationTileScale = 0;
        _navigationTileBytes = 0;
    }

    private Matrix3x2 PageTransform()
    {
        if (_page is null) return Matrix3x2.Identity;
        return PageTransform(_page, _zoom, _pan, _canvasWidth, _canvasHeight);
    }

    private static Matrix3x2 PageTransform(NotePage page, double zoom, Vector2 pan,
        double viewportWidth, double viewportHeight)
    {
        var offset = new Vector2(
            (float)((viewportWidth - page.Size.Width * zoom) / 2d) + pan.X,
            (float)((viewportHeight - page.Size.Height * zoom) / 2d) + pan.Y);
        return Matrix3x2.CreateScale((float)zoom) * Matrix3x2.CreateTranslation(offset);
    }

    private Vector2 PageOffset()
    {
        if (_page is null) return _pan;
        return new Vector2(
            (float)((_canvasWidth - _page.Size.Width * _zoom) / 2d) + _pan.X,
            (float)((_canvasHeight - _page.Size.Height * _zoom) / 2d) + _pan.Y);
    }

    private void ClampHorizontalPan()
    {
        if (_page is null || _canvasWidth <= 0) return;
        var requested = _pan.X;
        var clamped = ViewportPanBounds.ClampHorizontal(
            _page.Size.Width, _zoom, _canvasWidth, requested);
        _pan.X = clamped;
        if (Math.Abs(requested - clamped) <= 0.01f) return;
        if ((requested > clamped && _touchVelocity.X > 0) ||
            (requested < clamped && _touchVelocity.X < 0))
            _touchVelocity.X = 0;
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

    private PointD ClampPointToPage(PointD point)
    {
        if (_page is null) return point;
        return new PointD(
            Math.Clamp(point.X, 0, _page.Size.Width),
            Math.Clamp(point.Y, 0, _page.Size.Height));
    }

    private void UpdateSelectionUi()
    {
        SelectionSummary.Text = _selectedTextRegions.Count > 0
            ? $"{_selectedTextRegions.Count} text region(s) selected"
            : _selectedObjects.Count > 1
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
        SelectionLockButton.Visibility = _selectedObject is ImageObject or ShapeObject
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_selectedObject is ImageObject or ShapeObject)
            SelectionLockButton.Content = _selectedObject.IsLocked ? "Unlock" : "Lock";
        UpdateSelectionLockOverlay();
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
        _activeStylePresetId = null;
        _styleToolPickMode = false;
        UpdateStyleToolUi();
        RebuildStylePresetPicker();
        StatusText.Text = $"Style captured • {_styleToolColor} • {_styleToolWidth:0.#} pt • drag to apply";
    }

    private void ApplyStyleBrushAtPoint(PointD point)
    {
        if (_page is null) return;
        _styleBrushPoint = point;
        var radius = Math.Max(4, _styleBrushSize / 2f) / Math.Max(_zoom, 0.08);
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

    private void UpdateSelectionLockOverlay()
    {
        if (_readMode || _selectedObject is not { } selected ||
            selected is not ImageObject and not ShapeObject || _page is null)
        {
            if (SelectionLockOverlayButton.Visibility != Visibility.Collapsed)
                SelectionLockOverlayButton.Visibility = Visibility.Collapsed;
            return;
        }
        var bounds = StrokeGeometry.GetWorldBounds(selected);
        var anchor = PageToScreen(new PointD(bounds.Right, bounds.Top));
        var left = Math.Clamp(anchor.X + 8, 4, Math.Max(4, DrawingSurface.ActualWidth - 40));
        var top = Math.Clamp(anchor.Y - 18, 4, Math.Max(4, DrawingSurface.ActualHeight - 40));
        if (double.IsNaN(Canvas.GetLeft(SelectionLockOverlayButton)) ||
            Math.Abs(Canvas.GetLeft(SelectionLockOverlayButton) - left) > 0.25)
            Canvas.SetLeft(SelectionLockOverlayButton, left);
        if (double.IsNaN(Canvas.GetTop(SelectionLockOverlayButton)) ||
            Math.Abs(Canvas.GetTop(SelectionLockOverlayButton) - top) > 0.25)
            Canvas.SetTop(SelectionLockOverlayButton, top);
        SelectionLockedOverlayIcon.Visibility = selected.IsLocked
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectionUnlockedOverlayIcon.Visibility = selected.IsLocked
            ? Visibility.Collapsed
            : Visibility.Visible;
        var objectName = selected is ShapeObject ? "Shape" : "Image";
        UpdateTransientToolTip(SelectionLockOverlayButton,
            selected.IsLocked
                ? $"{objectName} is locked • click to unlock"
                : $"{objectName} is unlocked • click to lock");
        if (SelectionLockOverlayButton.Visibility != Visibility.Visible)
            SelectionLockOverlayButton.Visibility = Visibility.Visible;
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

    private void UpdateZoomText(bool showIndicator = false)
    {
        var text = $"{_zoom:P0}";
        ZoomText.Text = text;
        if (!showIndicator) return;
        _zoomIndicatorFade?.Stop();
        _zoomIndicatorFade = null;
        ZoomIndicatorText.Text = text;
        ZoomIndicator.Opacity = 1;
        ZoomIndicator.Visibility = Visibility.Visible;
        _zoomIndicatorTimer.Stop();
        _zoomIndicatorTimer.Start();
    }

    private void OnZoomIndicatorTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        var fade = new Storyboard();
        var opacity = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(opacity, ZoomIndicator);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        fade.Children.Add(opacity);
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_zoomIndicatorFade, fade)) return;
            ZoomIndicator.Visibility = Visibility.Collapsed;
            _zoomIndicatorFade = null;
        };
        _zoomIndicatorFade = fade;
        fade.Begin();
    }

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

    private static double NormalizeZoomPercent(double value, double fallback) =>
        Math.Clamp(Math.Round(double.IsFinite(value) ? value : fallback), 8, 800);

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
