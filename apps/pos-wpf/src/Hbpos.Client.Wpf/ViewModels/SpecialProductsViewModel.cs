using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Converters;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;

namespace Hbpos.Client.Wpf.ViewModels;

public sealed partial class SpecialProductsViewModel : ObservableObject, IScannerInputTarget, IDisposable
{
    public const string PageId = "SpecialProducts";

    private sealed record LastAddInfo(CartLine Line, decimal AddedQuantity, string DisplayName);

    private bool _disposed;

    public string ScannerPageId => PageId;

    // 首页缩略图预热张数（原分页大小）；网格本身已改为可滚动，不再分页。
    private const int ThumbnailPreloadCount = 20;
    // 网格一次最多渲染的卡片数，超出后用「显示更多」追加，避免上千个商品一次性解码缩略图。
    private const int DisplayChunkSize = 100;
    private static readonly TimeSpan ToastAutoHideDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JustAddedHighlightDelay = TimeSpan.FromMilliseconds(1200);
    private static readonly CultureInfo MoneyCulture = CultureInfo.GetCultureInfo("en-AU");
    private static readonly TimeSpan ThumbnailEnableDelay = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan DownloadProgressAutoHideDelay = TimeSpan.FromSeconds(5);

    private readonly ISpecialProductsWorkflowService _workflowService;
    private readonly ILocalizationService _localization;
    private readonly Action _onBack;
    private readonly Action<CartLine>? _onCartLineAdded;
    private readonly IRawScannerService? _rawScannerService;
    private readonly ICashierSessionContext _cashierSessionContext;
    private readonly bool _enforcePermissions;
    private readonly IOperationAuthorizationService? _operationAuthorizationService;
    private readonly PosCartService _cart;
    private readonly IOperationAuditLogger? _operationAuditLogger;
    private readonly object _specialItemsGate = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<IEnumerable<string?>, int, CancellationToken, Task<int>> _thumbnailPreloadAsync;
    private CancellationTokenSource? _downloadProgressHideCts;
    private CancellationTokenSource? _toastHideCts;
    private CancellationTokenSource? _justAddedCts;
    // 卡片按商品编码复用，刷新列表时只更新内容，避免整页重建闪烁。
    private Dictionary<string, SpecialProductCardViewModel> _cardsByProduct = new(StringComparer.OrdinalIgnoreCase);
    private List<SpecialProductCardViewModel> _allCards = [];
    private int _displayLimit = DisplayChunkSize;
    // 最近一次加购（仅这一次可撤销）与「返回收银后需要定位的购物车行」。
    private LastAddInfo? _lastAdd;
    private CartLine? _pendingRevealLine;
    private SellableItemDto? _priceEditorTarget;
    private bool _priceInputIsFresh;
    private CancellationTokenSource? _thumbnailEnableCts;
    private CancellationTokenSource? _searchCts;
    private long _searchVersion;

    [ObservableProperty]
    private PosSessionState _session;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isDownloadProgressVisible;

    [ObservableProperty]
    private bool _isDownloadProgressFailed;

    [ObservableProperty]
    private double _downloadProgressValue;

    [ObservableProperty]
    private string _downloadProgressText = string.Empty;

    [ObservableProperty]
    private string _downloadProgressDetailText = string.Empty;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private bool _areThumbnailsEnabled = true;

    /// <summary>浏览模式的页内筛选（按名称、货号、条码、查码模糊匹配）。</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _isCartFilterActive;

    [ObservableProperty]
    private string _toastText = string.Empty;

    [ObservableProperty]
    private bool _isToastVisible;

    [ObservableProperty]
    private bool _canUndoToast;

    [ObservableProperty]
    private bool _isPriceEditorOpen;

    [ObservableProperty]
    private string _priceEditorInputText = string.Empty;

    [ObservableProperty]
    private string _priceEditorErrorText = string.Empty;

    [ObservableProperty]
    private SellableItemDto? _selectedSpecialItem;

    [ObservableProperty]
    private SellableItemDto? _selectedSearchResult;

    public SpecialProductsViewModel(
        LocalSellableItemIndex priceIndex,
        PosCartService cart,
        ILocalCatalogRepository catalogRepository,
        ISpecialProductService specialProductService,
        PosSessionState session,
        ILocalizationService localization,
        Action onBack,
        Action<CartLine>? onCartLineAdded = null,
        ISpecialProductsWorkflowService? workflowService = null,
        IRawScannerService? rawScannerService = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Func<IEnumerable<string?>, int, CancellationToken, Task<int>>? thumbnailPreloadAsync = null,
        ICashierSessionContext? cashierSessionContext = null,
        bool enforcePermissionsWhenNoCashier = false,
        IOperationAuthorizationService? operationAuthorizationService = null,
        IOperationAuditLogger? operationAuditLogger = null)
    {
        _cart = cart;
        _operationAuditLogger = operationAuditLogger;
        _workflowService = workflowService ?? new SpecialProductsWorkflowService(
            priceIndex,
            cart,
            catalogRepository,
            specialProductService);
        _session = session;
        _localization = localization;
        _onBack = onBack;
        _onCartLineAdded = onCartLineAdded;
        _rawScannerService = rawScannerService;
        _cashierSessionContext = cashierSessionContext ?? new CashierSessionContext();
        _enforcePermissions = enforcePermissionsWhenNoCashier;
        _operationAuthorizationService = operationAuthorizationService;
        if (session.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(session.CashierSession);
        }

        _delayAsync = delayAsync ?? Task.Delay;
        _thumbnailPreloadAsync = thumbnailPreloadAsync ?? ProductThumbnailImageSourceConverter.PreloadAsync;

        BackCommand = new RelayCommand(Back);
        SearchCommand = new AsyncRelayCommand(() => SearchCatalogAsync());
        ClearSearchCommand = new RelayCommand(ClearSearch, () => !string.IsNullOrWhiteSpace(SearchText));
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        DownloadCommand = new AsyncRelayCommand(DownloadSpecialProductsAsync, CanDownloadSpecialProducts);
        ToggleEditModeCommand = new AsyncRelayCommand(ToggleEditModeAsync, () => !IsBusy);
        ShowAllCommand = new RelayCommand(() => SetCartFilter(false));
        ShowInCartCommand = new RelayCommand(() => SetCartFilter(true));
        ShowMoreCommand = new RelayCommand(ShowMore, () => HasMoreToShow);
        UndoAddCommand = new RelayCommand(UndoLastAdd, () => _lastAdd is not null);
        DismissToastCommand = new RelayCommand(HideToast);
        AddToCartCommand = new AsyncRelayCommand<SellableItemDto>(AddToCartAsync);
        SpecialItemCardCommand = new AsyncRelayCommand<SellableItemDto>(HandleSpecialItemCardAsync);
        AddSpecialProductCommand = new AsyncRelayCommand<SellableItemDto>(AddSpecialProductAsync, CanMutateItem);
        RemoveSpecialProductCommand = new AsyncRelayCommand<SellableItemDto>(RemoveSpecialProductAsync, CanMutateItem);
        MoveUpCommand = new AsyncRelayCommand<SellableItemDto>(item => MoveSpecialProductAsync(item, -1, "move-up"), CanMoveUp);
        MoveDownCommand = new AsyncRelayCommand<SellableItemDto>(item => MoveSpecialProductAsync(item, 1, "move-down"), CanMoveDown);
        MoveToFrontCommand = new AsyncRelayCommand<SellableItemDto>(MoveToFrontAsync, CanMoveUp);
        BeginRemoveCommand = new RelayCommand<SellableItemDto>(BeginRemove, CanMutateItem);
        CancelRemoveCommand = new RelayCommand<SellableItemDto>(CancelRemove);
        OpenPriceEditorCommand = new RelayCommand<SellableItemDto>(OpenPriceEditor, CanMutateItem);
        ClosePriceEditorCommand = new RelayCommand(ClosePriceEditor);
        PriceKeyCommand = new RelayCommand<string>(PressPriceKey);
        PriceBackspaceCommand = new RelayCommand(BackspacePrice);
        PriceClearCommand = new RelayCommand(ClearPrice);
        SavePriceCommand = new AsyncRelayCommand(SavePriceAsync, CanSavePrice);

        _localization.CultureChanged += OnCultureChanged;
        _cart.CartChanged += OnCartChanged;
        _rawScannerService?.Subscribe(PageId, OnRawBarcodeScanned);
        StatusMessage = T("specialProducts.status.ready");
        RefreshCartState();
    }

    /// <summary>新增、改价、排序后请求视图把指定商品滚动到可见区域（参数为商品编码）。</summary>
    public event EventHandler<string>? FocusProductRequested;

    public ObservableCollection<SellableItemDto> SpecialItems { get; } = [];

    /// <summary>网格实际显示的卡片（已套用筛选与「显示更多」上限）。</summary>
    public ObservableCollection<SpecialProductCardViewModel> DisplayedCards { get; } = [];

    /// <summary>右侧购物车摘要：最近加入的几行。</summary>
    public ObservableCollection<SpecialCartSummaryLine> CartSummaryLines { get; } = [];

    public ObservableCollection<SellableItemDto> SearchResults { get; } = [];

    public IRelayCommand BackCommand { get; }

    public IAsyncRelayCommand SearchCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand DownloadCommand { get; }

    public IRelayCommand ToggleEditModeCommand { get; }

    public IRelayCommand ShowAllCommand { get; }

    public IRelayCommand ShowInCartCommand { get; }

    public IRelayCommand ShowMoreCommand { get; }

    public IRelayCommand UndoAddCommand { get; }

    public IRelayCommand DismissToastCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> MoveToFrontCommand { get; }

    public IRelayCommand<SellableItemDto> BeginRemoveCommand { get; }

    public IRelayCommand<SellableItemDto> CancelRemoveCommand { get; }

    public IRelayCommand<SellableItemDto> OpenPriceEditorCommand { get; }

    public IRelayCommand ClosePriceEditorCommand { get; }

    public IRelayCommand<string> PriceKeyCommand { get; }

    public IRelayCommand PriceBackspaceCommand { get; }

    public IRelayCommand PriceClearCommand { get; }

    public IAsyncRelayCommand SavePriceCommand { get; }

    public IRelayCommand<SellableItemDto> AddToCartCommand { get; }

    public IRelayCommand<SellableItemDto> SpecialItemCardCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> AddSpecialProductCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> RemoveSpecialProductCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> MoveUpCommand { get; }

    public IAsyncRelayCommand<SellableItemDto> MoveDownCommand { get; }

    public string TitleText => T("specialProducts.title");

    public string SubtitleText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.subtitle"),
        Session.StoreName,
        Session.StoreCode);

    public string BackText => T("specialProducts.back");

    public string SearchPlaceholderText => T("specialProducts.search.placeholder");

    public string SearchButtonText => T("specialProducts.search.action");

    public string ClearSearchText => T("Clear");

    public string RefreshText => T("specialProducts.refresh");

    public string DownloadText => T("specialProducts.download");

    public string EditModeText => T(IsEditMode ? "specialProducts.done" : "specialProducts.edit");

    public string FilterPlaceholderText => T("specialProducts.filter.placeholder");

    public string BrowseHintText => T("specialProducts.hint.browse");

    public string FilterEmptyText => T("specialProducts.filter.empty");

    public string CartTitleText => T("specialProducts.cart.title");

    public string CartTotalLabelText => T("specialProducts.cart.total");

    public string CartEmptyText => T("specialProducts.cart.empty");

    public string CartCheckoutText => T("specialProducts.cart.checkout");

    public string UndoText => T("specialProducts.undo");

    public string CancelText => T("specialProducts.cancel");

    public string EditBannerText => T("specialProducts.edit.banner");

    public string EditBannerDetailText => T("specialProducts.edit.bannerDetail");

    public string MoveToFrontText => T("specialProducts.moveToFront");

    public string EditPriceText => T("specialProducts.editPrice");

    public string AddPanelTitleText => T("specialProducts.addPanel.title");

    public string AddPanelHintText => T("specialProducts.addPanel.hint");

    public string AddPanelFooterText => T("specialProducts.addPanel.footer");

    public string PriceEditorTitleText => T("specialProducts.priceEditor.title");

    public string PriceEditorCurrentLabelText => T("specialProducts.priceEditor.current");

    public string PriceEditorNewLabelText => T("specialProducts.priceEditor.new");

    public string PriceEditorOnlineHintText => T("specialProducts.priceEditor.onlineHint");

    public string PriceEditorManualNoteText => T("specialProducts.priceEditor.manualNote");

    public string PriceEditorSaveText => T("specialProducts.priceEditor.save");

    public string PriceEditorClearText => T("specialProducts.priceEditor.clear");

    public string PriceEditorBackspaceText => T("specialProducts.priceEditor.backspace");

    /// <summary>卡片内「移除 xxx？」的确认提示；同一时间只有一张卡处于确认态。</summary>
    public string RemoveConfirmPromptText { get; private set; } = string.Empty;

    public string PriceEditorProductName => _priceEditorTarget?.DisplayName ?? string.Empty;

    public string PriceEditorCurrentPriceText => _priceEditorTarget is null
        ? string.Empty
        : FormatMoney(_priceEditorTarget.RetailPrice);

    /// <summary>键盘输入区的金额显示（带货币符号）。</summary>
    public string PriceEditorNewPriceText => string.IsNullOrEmpty(PriceEditorInputText)
        ? FormatMoney(0m)
        : "$" + PriceEditorInputText;

    public bool HasPriceEditorError => !string.IsNullOrEmpty(PriceEditorErrorText);

    public string TotalCountText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.countFormat"),
        SpecialItems.Count);

    public string AllChipText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.chip.all"),
        SpecialItems.Count);

    public string InCartChipText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.chip.inCart"),
        InCartCount);

    public string ShowMoreText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.showMore"),
        Math.Max(0, FilteredCardCount - DisplayedCards.Count));

    /// <summary>特殊商品里当前已在购物车的种数，用于「已在购物车」筛选标签。</summary>
    public int InCartCount => _allCards.Count(card => card.IsInCart);

    public bool IsBrowseMode => !IsEditMode;

    public bool IsFilterEmpty => SpecialItems.Count > 0 && DisplayedCards.Count == 0;

    public bool HasMoreToShow => DisplayedCards.Count < FilteredCardCount;

    private int FilteredCardCount => FilterCards().Count();

    // ---- 右侧购物车摘要 ----
    public bool IsCartEmpty => _cart.Lines.Count == 0;

    public bool HasCartLines => !IsCartEmpty;

    public string CartCountText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.cart.count"),
        _cart.Lines.Where(line => !line.IsReturnLine).Sum(line => line.Quantity).ToString("0.##", CultureInfo.InvariantCulture),
        _cart.Lines.Count);

    public string CartTotalText => FormatMoney(_cart.ActualAmount);

    public bool HasMoreCartLines => _cart.Lines.Count > CartSummaryLines.Count;

    public string CartMoreText => string.Format(
        _localization.CurrentCulture,
        T("specialProducts.cart.more"),
        Math.Max(0, _cart.Lines.Count - CartSummaryLines.Count));

    // ---- 网格布局：编辑模式右侧面板更宽，卡片相应加宽以保持整列对齐 ----
    public double CardItemWidth => IsEditMode ? 236d : 196d;

    public double CardItemHeight => 122d;

    public string SelectedPositionText
    {
        get
        {
            var selected = SelectedSpecialItem;
            if (selected is null)
            {
                return T("specialProducts.edit.noSelection");
            }

            var index = SpecialItems.IndexOf(selected);
            return string.Format(
                _localization.CurrentCulture,
                T("specialProducts.edit.selected"),
                selected.DisplayName,
                index + 1);
        }
    }

    public string AddText => T("specialProducts.add");

    public string RemoveText => T("specialProducts.remove");

    public string MoveUpText => T("specialProducts.moveUp");

    public string MoveDownText => T("specialProducts.moveDown");

    public string TapToAddText => T("specialProducts.tapToAdd");

    public string SearchResultsText => T("specialProducts.search.results");

    public string EmptyText => T("specialProducts.empty");

    public string NoSearchResultsText => T("specialProducts.search.empty");

    public string OnlineStateText => T(Session.IsOnline ? "pos.status.online" : "pos.status.offline");

    public bool HasSearchResults => SearchResults.Count > 0;

    public bool IsSpecialListEmpty => SpecialItems.Count == 0;

    public string SelectedSpecialItemText => SelectedSpecialItem?.DisplayName ?? T("specialProducts.edit.noSelection");

    public Task PreloadAsync(CancellationToken cancellationToken = default)
    {
        return PreloadCoreAsync(cancellationToken);
    }

    public async Task PreloadFirstPageThumbnailsAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await PreloadAsync(cancellationToken);

            IReadOnlyList<string?> firstPageImageSources;
            int firstPageItemCount;
            lock (_specialItemsGate)
            {
                var firstPageItems = SpecialItems.Take(ThumbnailPreloadCount).ToArray();
                firstPageItemCount = firstPageItems.Length;
                firstPageImageSources = firstPageItems
                    .Select(item => item.ProductImage)
                    .Where(sourceText => !string.IsNullOrWhiteSpace(sourceText))
                    .ToArray();
            }

            var preloadedCount = firstPageImageSources.Count == 0
                ? 0
                : await _thumbnailPreloadAsync(firstPageImageSources, 72, cancellationToken);
            stopwatch.Stop();
            Log(
                "startup thumbnail preload completed " +
                $"store={Session.StoreCode} " +
                $"items={firstPageItemCount} " +
                $"images={firstPageImageSources.Count} " +
                $"preloaded={preloadedCount} " +
                $"elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            Log($"startup thumbnail preload canceled store={Session.StoreCode} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"startup thumbnail preload failed store={Session.StoreCode} elapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
        }
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        var shouldResetBusy = !IsBusy;
        if (shouldResetBusy)
        {
            IsBusy = true;
        }

        try
        {
            var result = await _workflowService.EnsureLoadedAsync(Session.StoreCode, cancellationToken);
            ApplySpecialItems(result.SpecialItems, resetToFirstPage: SpecialItems.Count == 0);
        }
        catch (Exception ex)
        {
            SetStatusText(string.Format(_localization.CurrentCulture, T("specialProducts.status.loadFailed"), ex.Message));
        }
        finally
        {
            if (shouldResetBusy)
            {
                IsBusy = false;
            }
        }
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return LoadAsyncCore(cancellationToken);
    }

    public void ActivateForEntry()
    {
        IsEditMode = false;
        ClearSearch();
        SelectedSpecialItem = null;
        // 页面被缓存复用：每次进入都回到干净的浏览状态，不保留上次的筛选、提示和改价弹窗。
        FilterText = string.Empty;
        IsCartFilterActive = false;
        ClosePriceEditor();
        HideToast();
        _lastAdd = null;
        _pendingRevealLine = null;
        _displayLimit = DisplayChunkSize;
        UndoAddCommand.NotifyCanExecuteChanged();
        if (DisplayedCards.Count == 0)
        {
            DeferThumbnailLoading();
        }

        RefreshCartState();
        RefreshDisplayedCards();
    }

    private async Task PreloadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _workflowService.PreloadAsync(Session.StoreCode, cancellationToken);
            ApplySpecialItems(result.SpecialItems, resetToFirstPage: true);
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("SpecialProducts", $"preload failed store={Session.StoreCode} error={ex.Message}");
        }
    }

    private async Task LoadAsyncCore(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            Log($"load skipped store={Session.StoreCode} reason=busy forceReload=true");
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _workflowService.LoadAsync(Session.StoreCode, cancellationToken);
            ApplySpecialItems(result.SpecialItems, resetToFirstPage: true);
            SetStatus("specialProducts.status.loaded", SpecialItems.Count);
        }
        catch (Exception ex)
        {
            SetStatusText(string.Format(_localization.CurrentCulture, T("specialProducts.status.loadFailed"), ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSessionChanged(PosSessionState value)
    {
        CancelSearch();
        if (value.CashierSession is not null)
        {
            _cashierSessionContext.SetCurrent(value.CashierSession);
        }

        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(OnlineStateText));
        RefreshCommandStates();
    }

    partial void OnSearchTextChanged(string value)
    {
        CancelSearch();
        ClearSearchCommand.NotifyCanExecuteChanged();
        if (string.IsNullOrWhiteSpace(value))
        {
            SearchResults.Clear();
            SelectedSearchResult = null;
            OnPropertyChanged(nameof(HasSearchResults));
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        RefreshCommandStates();
    }

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(EditModeText));
        OnPropertyChanged(nameof(IsBrowseMode));
        OnPropertyChanged(nameof(CardItemWidth));
        if (!value)
        {
            ClearSearch();
            SelectedSpecialItem = null;
            ClosePriceEditor();
            ClearRemoveConfirmations();
        }
        else
        {
            // 进入编辑模式时关闭购物车筛选与提示，网格回到完整排序视图。
            HideToast();
            if (IsCartFilterActive)
            {
                SetCartFilter(false);
            }
        }

        RefreshCommandStates();
    }

    partial void OnSelectedSpecialItemChanged(SellableItemDto? value)
    {
        OnPropertyChanged(nameof(SelectedSpecialItemText));
        OnPropertyChanged(nameof(SelectedPositionText));
        var selectedKey = NormalizeProductCode(value?.ProductCode);
        foreach (var card in _allCards)
        {
            card.IsSelected = selectedKey.Length > 0 &&
                string.Equals(NormalizeProductCode(card.ProductCode), selectedKey, StringComparison.OrdinalIgnoreCase);
            if (!card.IsSelected)
            {
                card.IsRemoveConfirming = false;
            }
        }

        RefreshCommandStates();
    }

    partial void OnFilterTextChanged(string value)
    {
        _displayLimit = DisplayChunkSize;
        RefreshDisplayedCards();
    }

    partial void OnPriceEditorInputTextChanged(string value)
    {
        OnPropertyChanged(nameof(PriceEditorNewPriceText));
        SavePriceCommand.NotifyCanExecuteChanged();
    }

    partial void OnPriceEditorErrorTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasPriceEditorError));
    }

    partial void OnIsToastVisibleChanged(bool value)
    {
        if (!value)
        {
            CanUndoToast = false;
        }
    }

    public bool ProcessScannerBarcode(string barcode, string devicePath, string source)
    {
        var normalizedBarcode = barcode.Trim();
        if (string.IsNullOrWhiteSpace(normalizedBarcode))
        {
            Log($"operation=scanner store={Session.StoreCode} source={source} device={devicePath} success=false reason=empty-barcode");
            return true;
        }

        if (EmergencyLoginTokenCodec.HasSupportedPrefix(normalizedBarcode))
        {
            // 关键逻辑：业务页只安全吞掉紧急令牌，禁止写入搜索框或进入商品查询链。
            Log($"operation=scanner store={Session.StoreCode} source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} consumed=true searched=false reason=emergency-token");
            return true;
        }

        if (!IsEditMode)
        {
            SetStatus("specialProducts.status.scanRequiresEdit");
            Log($"operation=scanner store={Session.StoreCode} source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} consumed=true searched=false reason=edit-mode-off");
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        SearchText = normalizedBarcode;
        _ = SearchCatalogSafeAsync();
        stopwatch.Stop();
        Log($"operation=scanner store={Session.StoreCode} source={source} device={devicePath} barcodeInfo={BarcodeLogFormatter.FormatBarcodeInfo(normalizedBarcode)} consumed=true searched=true async=true elapsedMs={stopwatch.ElapsedMilliseconds}");
        return true;
    }

    private async Task SearchCatalogAsync(bool scannerSearch = false)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            SearchResults.Clear();
            SelectedSearchResult = null;
            OnPropertyChanged(nameof(HasSearchResults));
            return;
        }

        var storeCode = Session.StoreCode;
        var searchText = SearchText;
        var cancellationSource = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _searchCts, cancellationSource);
        var version = Interlocked.Increment(ref _searchVersion);
        previous?.Cancel();
        try
        {
            var results = await _workflowService.SearchAsync(storeCode, searchText, cancellationSource.Token);
            cancellationSource.Token.ThrowIfCancellationRequested();
            if (_disposed ||
                version != Volatile.Read(ref _searchVersion) ||
                !string.Equals(searchText, SearchText, StringComparison.Ordinal) ||
                !string.Equals(storeCode, Session.StoreCode, StringComparison.Ordinal))
            {
                return;
            }

            SearchResults.ReplaceWith(results.Items);
            SelectedSearchResult = SearchResults.FirstOrDefault();
            OnPropertyChanged(nameof(HasSearchResults));
            if (scannerSearch && SelectedSearchResult is not null)
            {
                SetStatus("specialProducts.status.scanConfirm", SelectedSearchResult.DisplayName);
            }
            else
            {
                SetStatus(results.Items.Count == 0
                    ? "specialProducts.status.noSearchResults"
                    : "specialProducts.status.searchResults",
                    results.Items.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            // 输入、会话或页面已变化，取消旧目录搜索。
        }
        catch (Exception ex)
        {
            Log($"operation=search store={storeCode} success=false error={ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _searchCts, null, cancellationSource);
            cancellationSource.Dispose();
        }
    }

    private async Task SearchCatalogSafeAsync()
    {
        await SearchCatalogAsync(scannerSearch: true);
    }

    private void ClearSearch()
    {
        CancelSearch();
        SearchText = string.Empty;
        SearchResults.Clear();
        SelectedSearchResult = null;
        OnPropertyChanged(nameof(HasSearchResults));
    }

    private async Task HandleSpecialItemCardAsync(SellableItemDto? item)
    {
        if (item is null)
        {
            return;
        }

        if (IsEditMode)
        {
            SelectedSpecialItem = item;
            SetStatus("specialProducts.status.selectedForEdit", item.DisplayName);
            return;
        }

        await AddToCartAsync(item);
    }

    private async Task AddToCartAsync(SellableItemDto? item)
    {
        var itemSnapshot = item;
        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.SpecialProducts.AddToCart, "add-to-cart");
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (itemSnapshot is null)
        {
            Log($"operation=add-to-cart store={Session.StoreCode} success=false reason=null-item totalElapsedMs=0");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var before = OperationAuditEvents.CaptureCart(_cart.Lines);
        var cartAuditRecorded = false;
        try
        {
            var result = _workflowService.AddToCart(itemSnapshot);
            // 特价区直接写购物车，不经过收银台的变更入口，需在此补记加购审计。
            var after = OperationAuditEvents.CaptureCart(_cart.Lines);
            if (OperationAuditEvents.HasChanged(before, after))
            {
                OperationAuditEvents.RecordCartChange(
                    _operationAuditLogger,
                    OperationAuditTypes.CartItemAdd,
                    Session,
                    before,
                    after,
                    reasonCode: "special-products");
            }

            cartAuditRecorded = true;
            // 关键逻辑：加购后留在本页连续点选；仅最近一次加购可撤销，返回收银时再定位到最后加入的购物车行。
            _lastAdd = new LastAddInfo(result.Line, itemSnapshot.QuantityFactor, itemSnapshot.DisplayName);
            _pendingRevealLine = result.Line;
            UndoAddCommand.NotifyCanExecuteChanged();
            SetStatus("specialProducts.status.addedToCart", itemSnapshot.DisplayName);
            ShowToast(string.Format(_localization.CurrentCulture, T("specialProducts.toast.added"), itemSnapshot.DisplayName), canUndo: true);
            HighlightJustAdded(itemSnapshot.ProductCode);
            stopwatch.Stop();
            Log($"operation=add-to-cart store={Session.StoreCode} productCode={itemSnapshot.ProductCode} lookupCode={itemSnapshot.LookupCode} success=true revealRequested={_onCartLineAdded is not null} stayOnPage=true cartLines={result.CartLineCount} totalElapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            // 加购成功后的返回/定位回调异常不能把已成功的加购再记成失败。
            if (!cartAuditRecorded)
            {
                OperationAuditEvents.RecordCartChange(
                    _operationAuditLogger,
                    OperationAuditTypes.CartItemAdd,
                    Session,
                    before,
                    OperationAuditEvents.CaptureCart(_cart.Lines),
                    outcome: "Failed",
                    reasonCode: "special-products",
                    safeMessage: ex.GetType().Name);
            }

            Log($"operation=add-to-cart store={Session.StoreCode} productCode={itemSnapshot.ProductCode} lookupCode={itemSnapshot.LookupCode} success=false totalElapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            throw;
        }
    }

    private async Task DownloadSpecialProductsAsync(CancellationToken cancellationToken)
    {
        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.SpecialProducts.Manage, "download", cancellationToken);
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (IsBusy || !EnsureOnlineMutationAllowed())
        {
            return;
        }

        IsBusy = true;
        var totalStopwatch = Stopwatch.StartNew();
        // 特殊商品列表是否已在价格索引重建之前显示出来（由下面工作流的「列表就绪」回调置位）。
        var listShown = false;
        try
        {
            Log($"operation=download store={Session.StoreCode} stage=start");
            ApplyDownloadProgress(new SpecialProductDownloadProgress(
                Session.StoreCode,
                SpecialProductDownloadProgressStage.Preparing,
                0,
                0,
                0,
                0,
                0,
                0,
                0));

            var progress = new Progress<SpecialProductDownloadProgress>(ApplyDownloadProgress);
            var serviceStopwatch = Stopwatch.StartNew();
            // 关键逻辑：下载写完本地库后，工作流先回调这里，立刻把特殊商品列表显示出来，
            // 不等随后可能要几秒的价格索引整体重建；重建期间 IsBusy 仍为 true，下载、编辑等操作继续禁用，
            // 重建完成（或失败）后才在 finally 复位。
            var result = await _workflowService.DownloadAsync(
                Session.StoreCode,
                cancellationToken,
                progress,
                specialItems =>
                {
                    ApplySpecialItems(specialItems, resetToFirstPage: true);
                    listShown = true;
                });
            serviceStopwatch.Stop();

            var replaceStopwatch = Stopwatch.StartNew();
            if (!listShown)
            {
                // 兜底：工作流没有回调（或回调里应用失败）时，用返回值显示一次。
                ApplySpecialItems(result.SpecialItems, resetToFirstPage: true);
            }

            replaceStopwatch.Stop();

            var searchStopwatch = Stopwatch.StartNew();
            await SearchCatalogAsync();
            searchStopwatch.Stop();
            ApplyDownloadProgress(new SpecialProductDownloadProgress(
                result.DownloadResult.StoreCode,
                SpecialProductDownloadProgressStage.Completed,
                result.DownloadResult.TotalCount,
                result.DownloadResult.DownloadedCount,
                100,
                result.DownloadResult.PageCount,
                result.DownloadResult.UpsertedCount,
                result.DownloadResult.UnmarkedCount,
                0));
            SetStatus(
                "specialProducts.status.downloadCompleted",
                result.DownloadResult.DownloadedCount,
                result.DownloadResult.UnmarkedCount);
            totalStopwatch.Stop();
            Log($"operation=download store={Session.StoreCode} stage=completed listShownEarly={listShown} pages={result.DownloadResult.PageCount} downloaded={result.DownloadResult.DownloadedCount} upserted={result.DownloadResult.UpsertedCount} unmarked={result.DownloadResult.UnmarkedCount} serviceElapsedMs={serviceStopwatch.ElapsedMilliseconds} replaceElapsedMs={replaceStopwatch.ElapsedMilliseconds} searchElapsedMs={searchStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            Log($"operation=download store={Session.StoreCode} stage=failed listShownEarly={listShown} totalElapsedMs={totalStopwatch.ElapsedMilliseconds} error={ex.Message}");
            SetStatusText(string.Format(
                _localization.CurrentCulture,
                T("specialProducts.status.downloadFailed"),
                ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AddSpecialProductAsync(SellableItemDto? item, CancellationToken cancellationToken)
    {
        var itemSnapshot = item;
        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.SpecialProducts.Manage, "add", cancellationToken);
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (itemSnapshot is null || !IsEditMode || !EnsureOnlineMutationAllowed())
        {
            return;
        }

        if (await MarkSpecialProductAsync(itemSnapshot, true, cancellationToken))
        {
            // 标记成功后立即收起扫码候选，避免二次搜索覆盖成功提示或遮挡已更新的特殊商品列表。
            ClearSearch();
        }
    }

    private async Task RemoveSpecialProductAsync(SellableItemDto? item, CancellationToken cancellationToken)
    {
        var itemSnapshot = item;
        using var authorization = await AuthorizeAsync(Permissions.PosTerminal.SpecialProducts.Manage, "remove", cancellationToken);
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (itemSnapshot is null || !IsEditMode || !EnsureOnlineMutationAllowed())
        {
            return;
        }

        if (await MarkSpecialProductAsync(itemSnapshot, false, cancellationToken))
        {
            // 删除成功后仅清除已从列表移除的编辑选中项，保留其他商品的编辑选择。
            if (string.Equals(
                    NormalizeProductCode(SelectedSpecialItem?.ProductCode),
                    NormalizeProductCode(itemSnapshot.ProductCode),
                    StringComparison.OrdinalIgnoreCase))
            {
                SelectedSpecialItem = null;
            }
        }
    }

    private async Task<bool> MarkSpecialProductAsync(
        SellableItemDto item,
        bool isSpecialProduct,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            Log($"operation=mark store={Session.StoreCode} productCode={item.ProductCode} isSpecialProduct={isSpecialProduct} stage=start");
            var serviceStopwatch = Stopwatch.StartNew();
            var result = await _workflowService.MarkSpecialProductAsync(
                Session.StoreCode,
                item.ProductCode,
                isSpecialProduct,
                cancellationToken);
            serviceStopwatch.Stop();

            var replaceStopwatch = Stopwatch.StartNew();
            ApplySpecialItems(
                result.SpecialItems,
                resetToFirstPage: false,
                focusProductCode: isSpecialProduct ? item.ProductCode : null);
            replaceStopwatch.Stop();
            SetStatus(
                isSpecialProduct ? "specialProducts.status.marked" : "specialProducts.status.unmarked",
                item.DisplayName);
            totalStopwatch.Stop();
            Log($"operation=mark store={Session.StoreCode} productCode={item.ProductCode} isSpecialProduct={isSpecialProduct} stage=completed items={result.SpecialItems.Count} serviceElapsedMs={serviceStopwatch.ElapsedMilliseconds} replaceElapsedMs={replaceStopwatch.ElapsedMilliseconds} pageRefreshElapsedMs=0 totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
            return true;
        }
        catch (Exception ex)
        {
            totalStopwatch.Stop();
            Log($"operation=mark store={Session.StoreCode} productCode={item.ProductCode} isSpecialProduct={isSpecialProduct} stage=failed totalElapsedMs={totalStopwatch.ElapsedMilliseconds} error={ex.Message}");
            SetStatusText(string.Format(_localization.CurrentCulture, T("specialProducts.status.markFailed"), ex.Message));
            // 失败或取消时不更新页面集合，让调用方保留当前候选和选中项供收银员重试。
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task MoveToFrontAsync(SellableItemDto? item)
    {
        var index = item is null ? -1 : SpecialItems.IndexOf(item);
        if (index <= 0)
        {
            return;
        }

        await MoveSpecialProductAsync(item, -index, "move-front");
    }

    private async Task MoveSpecialProductAsync(SellableItemDto? item, int delta, string action)
    {
        var itemSnapshot = item;
        using var authorization = await AuthorizeAsync(
            Permissions.PosTerminal.SpecialProducts.Manage,
            action);
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (itemSnapshot is null || IsBusy || !IsEditMode)
        {
            return;
        }

        var currentIndex = SpecialItems.IndexOf(itemSnapshot);
        var nextIndex = currentIndex + delta;
        if (delta == 0 || currentIndex < 0 || nextIndex < 0 || nextIndex >= SpecialItems.Count)
        {
            return;
        }

        var totalStopwatch = Stopwatch.StartNew();
        var saveStopwatch = Stopwatch.StartNew();
        var result = await _workflowService.ReorderAsync(
            Session.StoreCode,
            SpecialItems.ToArray(),
            itemSnapshot.ProductCode,
            delta,
            CancellationToken.None);
        saveStopwatch.Stop();
        if (result is null)
        {
            return;
        }

        var pageStopwatch = Stopwatch.StartNew();
        ApplySpecialItems(result.SpecialItems, resetToFirstPage: false, focusProductCode: result.FocusProductCode);
        pageStopwatch.Stop();
        SetStatus("specialProducts.status.orderSaved");
        RefreshCommandStates();
        totalStopwatch.Stop();
        Log($"operation=move store={Session.StoreCode} productCode={itemSnapshot.ProductCode} fromIndex={currentIndex} toIndex={nextIndex} saveElapsedMs={saveStopwatch.ElapsedMilliseconds} pageRefreshElapsedMs={pageStopwatch.ElapsedMilliseconds} totalElapsedMs={totalStopwatch.ElapsedMilliseconds}");
    }

    private async Task ToggleEditModeAsync()
    {
        var wasEditMode = IsEditMode;
        using var authorization = wasEditMode
            ? null
            : await AuthorizeAsync(Permissions.PosTerminal.SpecialProducts.Manage, "enter-edit-mode");
        if (!wasEditMode && authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization?.Activate();

        IsEditMode = !IsEditMode;
    }

    private Task<ViewModelAuthorizationGrant?> AuthorizeAsync(
        string permissionCode,
        string action,
        CancellationToken cancellationToken = default) =>
        ViewModelOperationAuthorization.AuthorizeAsync(
            _operationAuthorizationService,
            TryRequirePermission,
            permissionCode,
            "special-products",
            action,
            Session,
            cancellationToken);

    private bool TryRequirePermission(string permissionCode)
    {
        if ((!_enforcePermissions && _cashierSessionContext.CurrentSession is null && Session.CashierSession is null) ||
            _cashierSessionContext.RequirePermission(permissionCode, out var message))
        {
            return true;
        }

        SetStatusText(message);
        return false;
    }

    // ================= 卡片、筛选与网格 =================

    /// <summary>按商品编码复用卡片并刷新排序位置；商品本身仍以 SpecialItems 为准。</summary>
    private void RebuildCards()
    {
        var next = new Dictionary<string, SpecialProductCardViewModel>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<SpecialProductCardViewModel>(SpecialItems.Count);
        for (var index = 0; index < SpecialItems.Count; index++)
        {
            var item = SpecialItems[index];
            var key = NormalizeProductCode(item.ProductCode);
            SpecialProductCardViewModel card;
            if (key.Length > 0 && !next.ContainsKey(key) && _cardsByProduct.TryGetValue(key, out var existing))
            {
                existing.Item = item;
                card = existing;
            }
            else
            {
                card = new SpecialProductCardViewModel(item);
            }

            card.Position = index + 1;
            if (key.Length > 0 && !next.ContainsKey(key))
            {
                next[key] = card;
            }

            ordered.Add(card);
        }

        _cardsByProduct = next;
        _allCards = ordered;
        ApplyCartQuantitiesToCards();
        var selectedKey = NormalizeProductCode(SelectedSpecialItem?.ProductCode);
        foreach (var card in _allCards)
        {
            card.IsSelected = selectedKey.Length > 0 &&
                string.Equals(NormalizeProductCode(card.ProductCode), selectedKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    private IEnumerable<SpecialProductCardViewModel> FilterCards()
    {
        IEnumerable<SpecialProductCardViewModel> cards = _allCards;
        if (IsCartFilterActive)
        {
            cards = cards.Where(card => card.IsInCart);
        }

        var text = (FilterText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return cards;
        }

        return cards.Where(card =>
            Contains(card.Item.DisplayName, text) ||
            Contains(card.Item.ItemNumber, text) ||
            Contains(card.Item.LookupCode, text) ||
            Contains(card.Item.Barcode, text) ||
            Contains(card.Item.ProductCode, text));

        static bool Contains(string? source, string value) =>
            source?.Contains(value, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private void RefreshDisplayedCards()
    {
        var next = FilterCards().Take(_displayLimit).ToList();
        // 序列未变（如重新进入页面）时不动集合，避免网格重建、缩略图重新解码闪烁。
        if (!DisplayedCards.SequenceEqual(next))
        {
            DisplayedCards.ReplaceWith(next);
        }

        OnPropertyChanged(nameof(IsSpecialListEmpty));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(HasMoreToShow));
        OnPropertyChanged(nameof(ShowMoreText));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(AllChipText));
        OnPropertyChanged(nameof(InCartChipText));
        OnPropertyChanged(nameof(InCartCount));
        OnPropertyChanged(nameof(SelectedPositionText));
        ShowMoreCommand.NotifyCanExecuteChanged();
        RefreshCommandStates();
    }

    private void SetCartFilter(bool active)
    {
        if (IsCartFilterActive == active)
        {
            return;
        }

        IsCartFilterActive = active;
        _displayLimit = DisplayChunkSize;
        RefreshDisplayedCards();
    }

    private void ShowMore()
    {
        _displayLimit += DisplayChunkSize;
        RefreshDisplayedCards();
    }

    // ================= 购物车联动 =================

    private void OnCartChanged(object? sender, EventArgs e)
    {
        RefreshCartState();
        if (IsCartFilterActive)
        {
            // 「已在购物车」筛选的成员会随购物车变化。
            RefreshDisplayedCards();
        }
    }

    /// <summary>同步卡片数量角标与右侧购物车摘要。</summary>
    private void RefreshCartState()
    {
        ApplyCartQuantitiesToCards();
        CartSummaryLines.ReplaceWith(_cart.Lines
            .Reverse()
            .Take(3)
            .Select(line => new SpecialCartSummaryLine(
                line.DisplayName,
                "× " + line.Quantity.ToString("0.##", CultureInfo.InvariantCulture),
                FormatMoney(line.ActualAmount))));
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(HasCartLines));
        OnPropertyChanged(nameof(CartCountText));
        OnPropertyChanged(nameof(CartTotalText));
        OnPropertyChanged(nameof(HasMoreCartLines));
        OnPropertyChanged(nameof(CartMoreText));
        OnPropertyChanged(nameof(InCartCount));
        OnPropertyChanged(nameof(InCartChipText));
    }

    private void ApplyCartQuantitiesToCards()
    {
        var quantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _cart.Lines.Where(line => !line.IsReturnLine))
        {
            var key = NormalizeProductCode(line.ProductCode);
            if (key.Length == 0)
            {
                continue;
            }

            quantities[key] = quantities.GetValueOrDefault(key) + line.Quantity;
        }

        foreach (var card in _allCards)
        {
            card.CartQuantity = quantities.GetValueOrDefault(NormalizeProductCode(card.ProductCode));
        }
    }

    // ================= 加购提示、撤销与回到收银 =================

    private void ShowToast(string text, bool canUndo)
    {
        CancelToastHide();
        ToastText = text;
        IsToastVisible = true;
        CanUndoToast = canUndo;
        var cts = new CancellationTokenSource();
        _toastHideCts = cts;
        _ = HideToastAfterDelayAsync(cts);
    }

    private async Task HideToastAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await _delayAsync(ToastAutoHideDelay, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(_toastHideCts, cts))
            {
                IsToastVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_toastHideCts, cts))
            {
                _toastHideCts = null;
            }

            cts.Dispose();
        }
    }

    private void HideToast()
    {
        CancelToastHide();
        IsToastVisible = false;
    }

    private void CancelToastHide()
    {
        var cts = Interlocked.Exchange(ref _toastHideCts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private void HighlightJustAdded(string productCode)
    {
        foreach (var card in _allCards)
        {
            card.IsJustAdded = string.Equals(
                NormalizeProductCode(card.ProductCode),
                NormalizeProductCode(productCode),
                StringComparison.OrdinalIgnoreCase);
        }

        var previous = Interlocked.Exchange(ref _justAddedCts, null);
        previous?.Cancel();
        previous?.Dispose();
        var cts = new CancellationTokenSource();
        _justAddedCts = cts;
        _ = ClearJustAddedAfterDelayAsync(cts);
    }

    private async Task ClearJustAddedAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await _delayAsync(JustAddedHighlightDelay, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(_justAddedCts, cts))
            {
                foreach (var card in _allCards)
                {
                    card.IsJustAdded = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_justAddedCts, cts))
            {
                _justAddedCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>撤销最近一次加购：数量大于本次加入量则减回，否则删除整行；并补记审计。</summary>
    private void UndoLastAdd()
    {
        var last = _lastAdd;
        if (last is null)
        {
            return;
        }

        _lastAdd = null;
        UndoAddCommand.NotifyCanExecuteChanged();
        HideToast();

        var before = OperationAuditEvents.CaptureCart(_cart.Lines);
        // 购物车操作与规则在工作流服务里；购物车行已被改动或移除时它返回 Changed=false，无需撤销。
        var result = _workflowService.UndoAddToCart(last.Line, last.AddedQuantity);
        if (!result.Changed)
        {
            return;
        }

        if (result.RemovedLine && ReferenceEquals(_pendingRevealLine, last.Line))
        {
            _pendingRevealLine = null;
        }

        OperationAuditEvents.RecordCartChange(
            _operationAuditLogger,
            result.RemovedLine ? OperationAuditTypes.CartItemRemove : OperationAuditTypes.CartItemQuantityChange,
            Session,
            before,
            OperationAuditEvents.CaptureCart(_cart.Lines),
            reasonCode: "special-products-undo");
        SetStatus("specialProducts.status.addUndone", last.DisplayName);
        Log($"operation=undo-add store={Session.StoreCode} product={last.DisplayName} removedLine={result.RemovedLine}");
    }

    // ================= 编辑模式：移除确认 =================

    private void BeginRemove(SellableItemDto? item)
    {
        if (item is null || !IsEditMode)
        {
            return;
        }

        var key = NormalizeProductCode(item.ProductCode);
        RemoveConfirmPromptText = string.Format(
            _localization.CurrentCulture,
            T("specialProducts.remove.confirm"),
            item.DisplayName);
        OnPropertyChanged(nameof(RemoveConfirmPromptText));
        foreach (var card in _allCards)
        {
            card.IsRemoveConfirming = string.Equals(
                NormalizeProductCode(card.ProductCode),
                key,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private void CancelRemove(SellableItemDto? item)
    {
        ClearRemoveConfirmations();
    }

    private void ClearRemoveConfirmations()
    {
        foreach (var card in _allCards)
        {
            card.IsRemoveConfirming = false;
        }
    }

    // ================= 编辑模式：修改零售价（必须在线）=================

    private void OpenPriceEditor(SellableItemDto? item)
    {
        if (item is null || !IsEditMode || IsBusy)
        {
            return;
        }

        if (!EnsureOnlineMutationAllowed("specialProducts.status.priceOnlineRequired"))
        {
            return;
        }

        _priceEditorTarget = item;
        // 预填当前价；首次按键会整体替换，之后才是追加输入。
        _priceInputIsFresh = true;
        PriceEditorErrorText = string.Empty;
        PriceEditorInputText = item.RetailPrice.ToString("0.00", CultureInfo.InvariantCulture);
        IsPriceEditorOpen = true;
        RaisePriceEditorTargetProperties();
        SavePriceCommand.NotifyCanExecuteChanged();
    }

    private void ClosePriceEditor()
    {
        if (!IsPriceEditorOpen && _priceEditorTarget is null)
        {
            return;
        }

        SavePriceCommand.Cancel();
        IsPriceEditorOpen = false;
        _priceEditorTarget = null;
        PriceEditorInputText = string.Empty;
        PriceEditorErrorText = string.Empty;
        RaisePriceEditorTargetProperties();
        SavePriceCommand.NotifyCanExecuteChanged();
    }

    private void RaisePriceEditorTargetProperties()
    {
        OnPropertyChanged(nameof(PriceEditorProductName));
        OnPropertyChanged(nameof(PriceEditorCurrentPriceText));
    }

    private void PressPriceKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length != 1 || !IsPriceEditorOpen)
        {
            return;
        }

        var current = _priceInputIsFresh ? string.Empty : PriceEditorInputText;
        _priceInputIsFresh = false;
        PriceEditorErrorText = string.Empty;
        var next = AppendPriceKey(current, key[0]);
        if (next is not null)
        {
            PriceEditorInputText = next;
        }
    }

    /// <summary>金额输入规则：整数最多 5 位、小数最多 2 位、只允许一个小数点、去掉前导 0。</summary>
    private static string? AppendPriceKey(string current, char key)
    {
        if (key == '.')
        {
            if (current.Contains('.'))
            {
                return null;
            }

            return current.Length == 0 ? "0." : current + ".";
        }

        if (key is < '0' or > '9')
        {
            return null;
        }

        var dot = current.IndexOf('.');
        if (dot >= 0)
        {
            return current.Length - dot - 1 >= 2 ? null : current + key;
        }

        if (current == "0")
        {
            return key.ToString();
        }

        return current.Length >= 5 ? null : current + key;
    }

    private void BackspacePrice()
    {
        if (!IsPriceEditorOpen)
        {
            return;
        }

        _priceInputIsFresh = false;
        PriceEditorErrorText = string.Empty;
        PriceEditorInputText = PriceEditorInputText.Length <= 1
            ? string.Empty
            : PriceEditorInputText[..^1];
    }

    private void ClearPrice()
    {
        if (!IsPriceEditorOpen)
        {
            return;
        }

        _priceInputIsFresh = false;
        PriceEditorErrorText = string.Empty;
        PriceEditorInputText = string.Empty;
    }

    private bool TryParseEditedPrice(out decimal price)
    {
        price = 0m;
        return decimal.TryParse(
                   PriceEditorInputText,
                   NumberStyles.AllowDecimalPoint,
                   CultureInfo.InvariantCulture,
                   out price) &&
               SpecialProductPriceRules.IsValid(price);
    }

    private bool CanSavePrice()
    {
        // 价格没变就不允许保存：预填的是当前价，必须改动后才可保存。
        return IsPriceEditorOpen &&
            !IsBusy &&
            Session.IsOnline &&
            _priceEditorTarget is not null &&
            TryParseEditedPrice(out var price) &&
            price != _priceEditorTarget.RetailPrice;
    }

    private async Task SavePriceAsync(CancellationToken cancellationToken)
    {
        var target = _priceEditorTarget;
        if (target is null || !IsPriceEditorOpen)
        {
            return;
        }

        using var authorization = await AuthorizeAsync(
            Permissions.PosTerminal.SpecialProducts.Manage,
            "edit-price",
            cancellationToken);
        if (authorization is null)
        {
            return;
        }
        using var authorizationActivation = authorization.Activate();

        if (IsBusy || !EnsureOnlineMutationAllowed("specialProducts.status.priceOnlineRequired"))
        {
            return;
        }

        if (!TryParseEditedPrice(out var newPrice))
        {
            PriceEditorErrorText = T("specialProducts.priceEditor.invalid");
            return;
        }

        var oldPrice = target.RetailPrice;
        if (newPrice == oldPrice)
        {
            PriceEditorErrorText = T("specialProducts.priceEditor.same");
            return;
        }

        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Log($"operation=price store={Session.StoreCode} productCode={target.ProductCode} oldPrice={oldPrice} newPrice={newPrice} stage=start");
            var result = await _workflowService.UpdateSpecialProductPriceAsync(
                Session.StoreCode,
                target.ProductCode,
                newPrice,
                cancellationToken);
            ApplySpecialItems(result.SpecialItems, resetToFirstPage: false, focusProductCode: target.ProductCode);
            OperationAuditEvents.RecordSpecialProductPriceChange(
                _operationAuditLogger, Session, target, oldPrice, newPrice);

            // 购物车行在加入时已固化价格，改价不会回改；如果该商品已在车里，明确告知收银员。
            var inCart = _cart.Lines.Any(line =>
                !line.IsReturnLine &&
                string.Equals(
                    NormalizeProductCode(line.ProductCode),
                    NormalizeProductCode(target.ProductCode),
                    StringComparison.OrdinalIgnoreCase));
            SetStatus(
                inCart ? "specialProducts.status.priceUpdatedInCart" : "specialProducts.status.priceUpdated",
                target.DisplayName,
                FormatMoney(newPrice));
            ClosePriceEditor();
            stopwatch.Stop();
            Log($"operation=price store={Session.StoreCode} productCode={target.ProductCode} oldPrice={oldPrice} newPrice={newPrice} stage=completed inCart={inCart} totalElapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 返回收银或关闭弹窗导致取消：服务端是否已写入未知，保持本地不变，下次刷新以服务端为准。
            Log($"operation=price store={Session.StoreCode} productCode={target.ProductCode} stage=canceled");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Log($"operation=price store={Session.StoreCode} productCode={target.ProductCode} oldPrice={oldPrice} newPrice={newPrice} stage=failed totalElapsedMs={stopwatch.ElapsedMilliseconds} error={ex.Message}");
            OperationAuditEvents.RecordSpecialProductPriceChange(
                _operationAuditLogger, Session, target, oldPrice, newPrice,
                outcome: "Failed", safeMessage: ex.GetType().Name);
            var message = string.Format(_localization.CurrentCulture, T("specialProducts.status.priceFailed"), ex.Message);
            // 弹窗保持打开，收银员可直接重试；失败时本地价格不变。
            PriceEditorErrorText = message;
            SetStatusText(message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatMoney(decimal amount) => amount.ToString("C2", MoneyCulture);

    private bool EnsureOnlineMutationAllowed(string statusKey = "specialProducts.status.onlineRequired")
    {
        if (Session.IsOnline)
        {
            return true;
        }

        SetStatus(statusKey);
        return false;
    }

    private bool CanMutateItem(SellableItemDto? item)
    {
        return item is not null && IsEditMode && Session.IsOnline && !IsBusy;
    }

    private bool CanDownloadSpecialProducts()
    {
        return Session.IsOnline && !IsBusy;
    }

    private bool CanMoveUp(SellableItemDto? item)
    {
        return item is not null && IsEditMode && !IsBusy && SpecialItems.IndexOf(item) > 0;
    }

    private bool CanMoveDown(SellableItemDto? item)
    {
        var index = item is null ? -1 : SpecialItems.IndexOf(item);
        return index >= 0 && IsEditMode && !IsBusy && index < SpecialItems.Count - 1;
    }

    private void RefreshCommandStates()
    {
        DownloadCommand.NotifyCanExecuteChanged();
        ToggleEditModeCommand.NotifyCanExecuteChanged();
        AddSpecialProductCommand.NotifyCanExecuteChanged();
        RemoveSpecialProductCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MoveToFrontCommand.NotifyCanExecuteChanged();
        BeginRemoveCommand.NotifyCanExecuteChanged();
        OpenPriceEditorCommand.NotifyCanExecuteChanged();
        SavePriceCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string key, params object[] args)
    {
        StatusMessage = args.Length == 0
            ? T(key)
            : string.Format(_localization.CurrentCulture, T(key), args);
    }

    private void SetStatusText(string message)
    {
        StatusMessage = message;
    }

    private void ApplySpecialItems(
        IReadOnlyList<SellableItemDto> specialItems,
        bool resetToFirstPage,
        string? focusProductCode = null)
    {
        lock (_specialItemsGate)
        {
            var selectedProductCode = SelectedSpecialItem?.ProductCode;
            SpecialItems.ReplaceWith(specialItems);
            if (resetToFirstPage)
            {
                _displayLimit = DisplayChunkSize;
            }

            if (!string.IsNullOrWhiteSpace(focusProductCode))
            {
                // 新增 / 改价 / 排序后让目标商品落在已渲染范围内，并通知视图滚动到它。
                var focusIndex = SpecialItems
                    .Select((item, index) => new { item.ProductCode, Index = index })
                    .FirstOrDefault(x => string.Equals(
                        NormalizeProductCode(x.ProductCode),
                        NormalizeProductCode(focusProductCode),
                        StringComparison.OrdinalIgnoreCase))
                    ?.Index;
                if (focusIndex.HasValue && focusIndex.Value >= _displayLimit)
                {
                    _displayLimit = focusIndex.Value + 1;
                }
            }

            RebuildCards();
            RefreshDisplayedCards();
            if (!string.IsNullOrWhiteSpace(focusProductCode))
            {
                FocusProductRequested?.Invoke(this, focusProductCode);
            }

            SelectedSpecialItem = string.IsNullOrWhiteSpace(selectedProductCode)
                ? null
                : SpecialItems.FirstOrDefault(item => string.Equals(
                    NormalizeProductCode(item.ProductCode),
                    NormalizeProductCode(selectedProductCode),
                    StringComparison.OrdinalIgnoreCase));
            RefreshCommandStates();
        }
    }

    private void DeferThumbnailLoading()
    {
        _thumbnailEnableCts?.Cancel();
        _thumbnailEnableCts?.Dispose();
        var thumbnailEnableCts = new CancellationTokenSource();
        _thumbnailEnableCts = thumbnailEnableCts;
        AreThumbnailsEnabled = false;
        _ = EnableThumbnailsAfterInitialRenderAsync(thumbnailEnableCts);
    }

    private async Task EnableThumbnailsAfterInitialRenderAsync(CancellationTokenSource thumbnailEnableCts)
    {
        try
        {
            await _delayAsync(ThumbnailEnableDelay, thumbnailEnableCts.Token);
            if (!thumbnailEnableCts.IsCancellationRequested &&
                ReferenceEquals(_thumbnailEnableCts, thumbnailEnableCts))
            {
                AreThumbnailsEnabled = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_thumbnailEnableCts, thumbnailEnableCts))
            {
                _thumbnailEnableCts = null;
                thumbnailEnableCts.Dispose();
            }
        }
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(BackText));
        OnPropertyChanged(nameof(SearchPlaceholderText));
        OnPropertyChanged(nameof(SearchButtonText));
        OnPropertyChanged(nameof(ClearSearchText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(DownloadText));
        OnPropertyChanged(nameof(EditModeText));
        OnPropertyChanged(nameof(FilterPlaceholderText));
        OnPropertyChanged(nameof(BrowseHintText));
        OnPropertyChanged(nameof(FilterEmptyText));
        OnPropertyChanged(nameof(CartTitleText));
        OnPropertyChanged(nameof(CartTotalLabelText));
        OnPropertyChanged(nameof(CartEmptyText));
        OnPropertyChanged(nameof(CartCheckoutText));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(EditBannerText));
        OnPropertyChanged(nameof(EditBannerDetailText));
        OnPropertyChanged(nameof(MoveToFrontText));
        OnPropertyChanged(nameof(EditPriceText));
        OnPropertyChanged(nameof(AddPanelTitleText));
        OnPropertyChanged(nameof(AddPanelHintText));
        OnPropertyChanged(nameof(AddPanelFooterText));
        OnPropertyChanged(nameof(PriceEditorTitleText));
        OnPropertyChanged(nameof(PriceEditorCurrentLabelText));
        OnPropertyChanged(nameof(PriceEditorNewLabelText));
        OnPropertyChanged(nameof(PriceEditorOnlineHintText));
        OnPropertyChanged(nameof(PriceEditorManualNoteText));
        OnPropertyChanged(nameof(PriceEditorSaveText));
        OnPropertyChanged(nameof(PriceEditorClearText));
        OnPropertyChanged(nameof(PriceEditorBackspaceText));
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(AllChipText));
        OnPropertyChanged(nameof(InCartChipText));
        OnPropertyChanged(nameof(ShowMoreText));
        OnPropertyChanged(nameof(CartCountText));
        OnPropertyChanged(nameof(CartMoreText));
        OnPropertyChanged(nameof(SelectedPositionText));
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(RemoveText));
        OnPropertyChanged(nameof(MoveUpText));
        OnPropertyChanged(nameof(MoveDownText));
        OnPropertyChanged(nameof(TapToAddText));
        OnPropertyChanged(nameof(SearchResultsText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(NoSearchResultsText));
        OnPropertyChanged(nameof(OnlineStateText));
        OnPropertyChanged(nameof(SelectedSpecialItemText));
    }

    private string T(string key)
    {
        return _localization.T(key);
    }

    private static void Log(string message)
    {
        ConsoleLog.Write("SpecialProducts", message);
    }

    private void OnRawBarcodeScanned(RawBarcodeScannedEventArgs args)
    {
        ProcessScannerBarcode(args.Barcode, args.DevicePath, "raw");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelSearch();
        _localization.CultureChanged -= OnCultureChanged;
        _cart.CartChanged -= OnCartChanged;
        CancelToastHide();
        var justAddedCts = Interlocked.Exchange(ref _justAddedCts, null);
        justAddedCts?.Cancel();
        justAddedCts?.Dispose();
        CancelDownloadProgressAutoHide();
        _thumbnailEnableCts?.Cancel();
        _thumbnailEnableCts?.Dispose();
        _thumbnailEnableCts = null;
        _rawScannerService?.Unsubscribe(PageId);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        RaiseLocalizedProperties();
    }

    private void Back()
    {
        // 返回 POS 前停止仍在等待服务端的特殊商品请求，避免页面缓存后长期保持忙碌。
        DownloadCommand.Cancel();
        AddSpecialProductCommand.Cancel();
        RemoveSpecialProductCommand.Cancel();
        SavePriceCommand.Cancel();
        CancelSearch();
        ClosePriceEditor();
        HideToast();
        // 本次访问里加入过商品：回到收银台后定位到最后加入的购物车行（与旧「加购即返回」的定位一致）。
        var reveal = _pendingRevealLine;
        _pendingRevealLine = null;
        _lastAdd = null;
        UndoAddCommand.NotifyCanExecuteChanged();
        _onBack();
        if (reveal is not null)
        {
            _onCartLineAdded?.Invoke(reveal);
        }
    }

    private void CancelSearch()
    {
        Interlocked.Increment(ref _searchVersion);
        var cancellationSource = Interlocked.Exchange(ref _searchCts, null);
        cancellationSource?.Cancel();
    }

    private void ApplyDownloadProgress(SpecialProductDownloadProgress progress)
    {
        Log($"operation=download-progress store={progress.StoreCode} stage={progress.Stage} percent={progress.Percent} downloaded={progress.DownloadedCount} total={progress.TotalCount} pages={progress.PageCount} elapsedMs={progress.ElapsedMilliseconds}");
        CancelDownloadProgressAutoHide();
        IsDownloadProgressVisible = true;
        IsDownloadProgressFailed = progress.Stage == SpecialProductDownloadProgressStage.Failed;
        DownloadProgressValue = progress.Percent;

        var titleKey = progress.Stage switch
        {
            SpecialProductDownloadProgressStage.Completed => "specialProducts.download.completed",
            SpecialProductDownloadProgressStage.Failed => "specialProducts.download.failed",
            _ => "specialProducts.download.downloading"
        };
        DownloadProgressText = string.Format(
            _localization.CurrentCulture,
            T(titleKey),
            progress.Percent);

        DownloadProgressDetailText = progress.Stage == SpecialProductDownloadProgressStage.Failed
            ? (progress.ErrorMessage ?? string.Empty)
            : string.Format(
                _localization.CurrentCulture,
                T("specialProducts.download.detail"),
                progress.DownloadedCount,
                progress.TotalCount,
                progress.PageCount,
                progress.UpsertedCount,
                progress.UnmarkedCount,
                FormatElapsed(progress.ElapsedMilliseconds));

        if (progress.Stage == SpecialProductDownloadProgressStage.Completed)
        {
            StartDownloadProgressAutoHide();
        }
    }

    private string FormatElapsed(long elapsedMilliseconds)
    {
        return string.Format(
            _localization.CurrentCulture,
            T("shell.catalogDownload.elapsedSeconds"),
            elapsedMilliseconds / 1000d);
    }

    private static string NormalizeProductCode(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private void StartDownloadProgressAutoHide()
    {
        CancelDownloadProgressAutoHide();
        var cts = new CancellationTokenSource();
        _downloadProgressHideCts = cts;
        _ = HideDownloadProgressAfterDelayAsync(cts);
    }

    private async Task HideDownloadProgressAfterDelayAsync(CancellationTokenSource cts)
    {
        try
        {
            await _delayAsync(DownloadProgressAutoHideDelay, cts.Token);
            if (!cts.IsCancellationRequested && ReferenceEquals(_downloadProgressHideCts, cts))
            {
                IsDownloadProgressVisible = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_downloadProgressHideCts, cts))
            {
                _downloadProgressHideCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelDownloadProgressAutoHide()
    {
        var cts = Interlocked.Exchange(ref _downloadProgressHideCts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }
}
