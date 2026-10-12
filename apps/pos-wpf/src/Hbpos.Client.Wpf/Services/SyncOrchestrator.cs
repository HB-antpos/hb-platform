using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;

namespace Hbpos.Client.Wpf.Services;

internal sealed class SyncOrchestrator
{
    private const string OrderEntityType = "Order";
    private const string LinklySettlementEntityType = "LinklySettlement";

    private readonly IShellSyncCenterService _shellSyncCenterService;
    private readonly IOrderUploadExecutionService _orderUploadExecutionService;
    private readonly ILinklySettlementUploadQueueReader? _linklySettlementUploadQueueReader;
    private readonly ILinklySettlementUploadExecutionService? _linklySettlementUploadExecutionService;
    private readonly ILocalizationService _localization;
    private readonly Action<string>? _setStatusMessage;
    private readonly Action<int>? _onPendingSyncCountChanged;
    private readonly Func<int>? _getPendingSyncCount;
    private readonly Action? _refreshShell;
    private readonly Action<string>? _notifyPropertyChanged;
    private ClientLogOutboxStore? _logOutboxStore;
    private OperationAuditUploadService? _operationAuditUploadService;
    private DeviceAuthorizationState? _deviceAuthorizationState;
    private SyncQueueOverview _orderOverview = new(0, 0, 0, null);
    private LinklySettlementUploadOverview _settlementOverview = new(0, 0, 0, null);
    private IReadOnlyList<LinklySettlementUploadQueueItem> _settlementQueueItems = [];
    private string? _settlementLoadFailureMessage;
    private bool _isAutoSyncRetrying;
    // 中文注释：同步中心刷新序号。本地读取移到线程池后，15 秒定时、收款完成、同步中心按钮等入口的刷新可能并发，
    // 先发起的读取可能后返回；每段界面状态只应用比已写回结果更新的读取，避免旧快照盖掉新快照。
    // 序号在发起时递增；_applied* 只在调用方上下文（生产中即 UI 线程）读写。
    private long _syncCenterRefreshSequence;
    private long _appliedOrderRefreshSequence;
    private long _appliedAuditRefreshSequence;

    public SyncOrchestrator(
        IShellSyncCenterService shellSyncCenterService,
        IOrderUploadExecutionService orderUploadExecutionService,
        ILocalizationService localization,
        Action<string>? setStatusMessage = null,
        Action<int>? onPendingSyncCountChanged = null,
        Func<int>? getPendingSyncCount = null,
        Action? refreshShell = null,
        Action<string>? notifyPropertyChanged = null,
        ClientLogOutboxStore? logOutboxStore = null,
        OperationAuditUploadService? operationAuditUploadService = null,
        DeviceAuthorizationState? deviceAuthorizationState = null,
        ILinklySettlementUploadQueueReader? linklySettlementUploadQueueReader = null,
        ILinklySettlementUploadExecutionService? linklySettlementUploadExecutionService = null)
    {
        _shellSyncCenterService = shellSyncCenterService;
        _orderUploadExecutionService = orderUploadExecutionService;
        _linklySettlementUploadQueueReader = linklySettlementUploadQueueReader;
        _linklySettlementUploadExecutionService = linklySettlementUploadExecutionService;
        _localization = localization;
        _setStatusMessage = setStatusMessage;
        _onPendingSyncCountChanged = onPendingSyncCountChanged;
        _getPendingSyncCount = getPendingSyncCount;
        _refreshShell = refreshShell;
        _notifyPropertyChanged = notifyPropertyChanged;
        _logOutboxStore = logOutboxStore;
        _operationAuditUploadService = operationAuditUploadService;
        _deviceAuthorizationState = deviceAuthorizationState;

        ToggleSyncCenterCommand = new AsyncRelayCommand(ToggleSyncCenterAsync);
        RetrySyncOrderCommand = new AsyncRelayCommand<SyncQueueListItem?>(RetrySyncOrderAsync, CanRetrySyncEntity);
        RetryAllSyncOrdersCommand = new AsyncRelayCommand(RetryAllSyncOrdersAsync, CanRetryAllSyncOrders);
        RetrySelectedSyncOrdersCommand = new AsyncRelayCommand(RetrySelectedSyncOrdersAsync);
        SelectAllSyncOrdersCommand = new RelayCommand(SelectAllSyncOrders);
        RetrySelectedAuditLogsCommand = new AsyncRelayCommand(RetrySelectedAuditLogsAsync);
        SelectAllAuditLogsCommand = new RelayCommand(SelectAllAuditLogs);
    }

    // ---- State properties ----

    public ObservableCollection<SyncQueueListItem> SyncCenterOrders { get; } = [];

    public ObservableCollection<OperationAuditQueueListItem> SyncCenterAuditLogs { get; } = [];

    public bool IsSyncCenterExpanded { get; set; }

    public string SyncCenterDetailTitle { get; set; } = string.Empty;

    public string LastOrderSyncErrorText { get; set; } = string.Empty;

    public string LastAuditSyncErrorText { get; set; } = string.Empty;

    public string PendingSyncText { get; set; } = string.Empty;

    public string OrderSyncStatusText { get; set; } = string.Empty;

    public int PendingUploadCount { get; set; }

    public int FailedUploadCount { get; set; }

    public int SyncingOrderCount { get; set; }

    public bool IsOrderSyncRetrying { get; set; }

    // ---- Commands ----

    public IAsyncRelayCommand ToggleSyncCenterCommand { get; }

    public IAsyncRelayCommand<SyncQueueListItem?> RetrySyncOrderCommand { get; }

    public IAsyncRelayCommand RetryAllSyncOrdersCommand { get; }

    public IAsyncRelayCommand RetrySelectedSyncOrdersCommand { get; }

    public IRelayCommand SelectAllSyncOrdersCommand { get; }

    public IAsyncRelayCommand RetrySelectedAuditLogsCommand { get; }

    public IRelayCommand SelectAllAuditLogsCommand { get; }

    // ---- Public methods ----

    public async Task RefreshPendingSyncAsync(CancellationToken cancellationToken = default)
    {
        // 中文注释：15 秒联网探测、收款完成、同步中心按钮都在 Dispatcher 线程调用这里，而 Microsoft.Data.Sqlite 的
        // *Async 实际同步执行。订单快照、结算队列、审计 outbox 三项本地读取整体放进线程池：后台委托只调用仓储/读取器，
        // 不读写任何绑定集合、属性或回调；await 回到调用方上下文（UI 线程）后再一次性写回界面。
        var refreshSequence = Interlocked.Increment(ref _syncCenterRefreshSequence);
        var auditSource = CaptureAuditSource();
        var loaded = await Task.Run(async () =>
        {
            var orderSnapshot = await _shellSyncCenterService.GetSnapshotAsync(cancellationToken);
            var settlement = await LoadSettlementUploadsSafelyAsync(cancellationToken);
            var auditLogs = await LoadAuditLogsSafelyAsync(auditSource);
            return new SyncCenterLoadResult(orderSnapshot, settlement, auditLogs);
        });

        if (refreshSequence > _appliedOrderRefreshSequence)
        {
            _appliedOrderRefreshSequence = refreshSequence;
            ApplySettlementUploads(loaded.Settlement);
            ApplySyncCenterSnapshot(loaded.OrderSnapshot);
        }

        ApplyAuditLogs(refreshSequence, loaded.AuditLogs);
    }

    public async Task TryAutoRetryPendingAsync(CancellationToken cancellationToken)
    {
        if (_isAutoSyncRetrying || IsOrderSyncRetrying)
        {
            return;
        }

        _isAutoSyncRetrying = true;
        RefreshSyncRetryCommandStates();
        try
        {
            await RefreshPendingSyncAsync(cancellationToken);
            if (PendingUploadCount + FailedUploadCount == 0)
            {
                return;
            }

            ConsoleLog.Write("UploadSync", "auto retry pending start");
            // 中文注释：自动重试的上传执行（读待传队列、HTTP 上传、回写上传状态）整体放进线程池。上传服务内部没有
            // ConfigureAwait(false)，从 Dispatcher 直接调用时 HTTP 返回后的 SQLite 写入会回到 UI 线程。该委托只调用两个
            // 上传执行服务并汇总计数，不碰绑定属性；_isAutoSyncRetrying 与命令状态仍在 await 回来后的调用方线程修改。
            var result = await Task.Run(() => ExecutePendingUploadsAsync(cancellationToken));
            await RefreshPendingSyncAsync(cancellationToken);
            ConsoleLog.Write(
                "UploadSync",
                $"auto retry pending completed attempted={result.AttemptedCount} uploaded={result.UploadedCount} failed={result.FailedCount}");
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Write("UploadSync", "auto retry pending canceled");
            throw;
        }
        catch (Exception ex)
        {
            ConsoleLog.Write("UploadSync", $"auto retry pending failed error={ex.GetType().Name} message={ex.Message}");
            try
            {
                await RefreshPendingSyncAsync();
            }
            catch (Exception refreshEx) when (refreshEx is not OperationCanceledException)
            {
                ConsoleLog.Write(
                    "UploadSync",
                    $"auto retry pending refresh failed error={refreshEx.GetType().Name} message={refreshEx.Message}");
            }
        }
        finally
        {
            _isAutoSyncRetrying = false;
            RefreshSyncRetryCommandStates();
        }
    }

    public void ConfigureAudit(
        ClientLogOutboxStore logOutboxStore,
        OperationAuditUploadService operationAuditUploadService,
        DeviceAuthorizationState deviceAuthorizationState)
    {
        _logOutboxStore = logOutboxStore;
        _operationAuditUploadService = operationAuditUploadService;
        _deviceAuthorizationState = deviceAuthorizationState;
    }

    public void RefreshLocalizedText()
    {
        PendingSyncText = string.Format(
            _localization.CurrentCulture,
            _localization.T("pos.status.pendingSync"),
            GetPendingSyncCount());
        _notifyPropertyChanged?.Invoke(nameof(PendingSyncText));
        OrderSyncStatusText = string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.sync.orderStatus"),
            PendingUploadCount,
            FailedUploadCount,
            SyncingOrderCount);
        _notifyPropertyChanged?.Invoke(nameof(OrderSyncStatusText));
        SyncCenterDetailTitle = string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.sync.detailTitle"),
            SyncCenterOrders.Count);
        _notifyPropertyChanged?.Invoke(nameof(SyncCenterDetailTitle));
        LastOrderSyncErrorText = BuildLastUploadErrorText();
        _notifyPropertyChanged?.Invoke(nameof(LastOrderSyncErrorText));
    }

    public void ApplySyncCenterSnapshot(ShellSyncCenterSnapshot snapshot)
    {
        _orderOverview = snapshot.Overview;
        PendingUploadCount = snapshot.Overview.PendingCount + _settlementOverview.PendingCount;
        _notifyPropertyChanged?.Invoke(nameof(PendingUploadCount));
        FailedUploadCount = snapshot.Overview.FailedCount + _settlementOverview.FailedCount;
        _notifyPropertyChanged?.Invoke(nameof(FailedUploadCount));
        SyncingOrderCount = snapshot.Overview.SyncingCount + _settlementOverview.UploadingCount;
        _notifyPropertyChanged?.Invoke(nameof(SyncingOrderCount));
        LastOrderSyncErrorText = BuildLastUploadErrorText();
        _notifyPropertyChanged?.Invoke(nameof(LastOrderSyncErrorText));
        SyncCenterOrders.Clear();
        foreach (var item in snapshot.ActiveItems)
        {
            SyncCenterOrders.Add(item);
        }
        foreach (var item in _settlementQueueItems.Select(MapSettlementQueueItem))
        {
            SyncCenterOrders.Add(item);
        }
        _notifyPropertyChanged?.Invoke(nameof(SyncCenterOrders));
        _onPendingSyncCountChanged?.Invoke(PendingUploadCount);
        RefreshSyncRetryCommandStates();
        _refreshShell?.Invoke();
    }

    public void RefreshSyncRetryCommandStates()
    {
        RetrySyncOrderCommand.NotifyCanExecuteChanged();
        RetryAllSyncOrdersCommand.NotifyCanExecuteChanged();
    }

    // ---- Private helpers ----

    private int GetPendingSyncCount()
    {
        return _getPendingSyncCount?.Invoke() ?? (PendingUploadCount + FailedUploadCount);
    }

    private async Task ToggleSyncCenterAsync()
    {
        if (!IsSyncCenterExpanded)
        {
            await RefreshPendingSyncAsync();
        }

        IsSyncCenterExpanded = !IsSyncCenterExpanded;
        _notifyPropertyChanged?.Invoke(nameof(IsSyncCenterExpanded));
    }

    private async Task RetrySyncOrderAsync(SyncQueueListItem? item)
    {
        if (!CanRetrySyncEntity(item))
        {
            return;
        }

        await ExecuteUploadSyncRetryAsync(
            () => ExecuteOneUploadAsync(item!, CancellationToken.None),
            "shell.sync.retryingOne");
    }

    private async Task RetryAllSyncOrdersAsync()
    {
        await ExecuteUploadSyncRetryAsync(
            () => ExecuteAllUploadsAsync(CancellationToken.None),
            "shell.sync.retryingAll");
    }

    private async Task RetrySelectedSyncOrdersAsync()
    {
        var selected = SyncCenterOrders
            .Where(item => item.Selection.IsSelected && CanRetrySyncEntity(item))
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        await ExecuteUploadSyncRetryAsync(
            () => ExecuteSelectedUploadsAsync(selected, CancellationToken.None),
            "shell.sync.retryingSelected");
    }

    private void SelectAllSyncOrders()
    {
        foreach (var item in SyncCenterOrders.Where(CanRetrySyncEntity))
        {
            item.Selection.IsSelected = true;
        }
    }

    private async Task RefreshAuditLogsSafelyAsync()
    {
        var refreshSequence = Interlocked.Increment(ref _syncCenterRefreshSequence);
        var auditSource = CaptureAuditSource();
        // 中文注释：审计 outbox 读取同样放进线程池，结果回到调用方上下文再写回界面。
        var auditLogs = await Task.Run(() => LoadAuditLogsSafelyAsync(auditSource));
        ApplyAuditLogs(refreshSequence, auditLogs);
    }

    // 在调用方线程取一次审计来源快照，后台读取不再读这两个可被 ConfigureAudit 改写的字段。
    private AuditLogSource CaptureAuditSource() =>
        new(_logOutboxStore, _deviceAuthorizationState?.Current);

    // 仅做本地读取（线程池执行）：不碰绑定集合与属性。
    private static async Task<AuditLogsLoadResult> LoadAuditLogsSafelyAsync(AuditLogSource source)
    {
        if (source.Store is null || source.Scope is null)
        {
            // 没有审计来源时清空列表。
            return new AuditLogsLoadResult([], null);
        }

        try
        {
            var records = await source.Store.ReadOperationForScopeAsync(
                source.Scope.StoreCode,
                source.Scope.DeviceCode,
                200,
                CancellationToken.None);
            return new AuditLogsLoadResult(records, null);
        }
        catch (Exception ex)
        {
            // 审计 outbox 故障不能阻止订单页签刷新或覆盖订单重试结果。
            return new AuditLogsLoadResult([], ex.Message);
        }
    }

    // 在调用方上下文（UI 线程）写回审计列表与错误文案。
    private void ApplyAuditLogs(long refreshSequence, AuditLogsLoadResult auditLogs)
    {
        if (refreshSequence <= _appliedAuditRefreshSequence)
        {
            // 更晚发起的审计读取已写回界面，这次较旧的结果丢弃。
            return;
        }

        _appliedAuditRefreshSequence = refreshSequence;
        if (auditLogs.FailureMessage is null)
        {
            // 中文注释：写回阶段同样兜底。SyncCenterAuditLogs 的 CollectionChanged 订阅方或属性通知订阅方抛错时，
            // 与改为后台读取之前一样只落到审计错误文案，不让整个同步中心刷新失败（收款完成后续步骤、自动重试、
            // 手动重试都依赖刷新正常结束）。
            try
            {
                SyncCenterAuditLogs.Clear();
                foreach (var record in auditLogs.Records)
                {
                    SyncCenterAuditLogs.Add(new OperationAuditQueueListItem(
                        record.EventId,
                        record.State,
                        record.OccurredAtUtc,
                        record.AttemptCount,
                        record.NextAttemptAtUtc,
                        record.LastErrorCode,
                        record.LastErrorMessage));
                }

                _notifyPropertyChanged?.Invoke(nameof(SyncCenterAuditLogs));
                LastAuditSyncErrorText = string.Empty;
            }
            catch (Exception ex)
            {
                LastAuditSyncErrorText = FormatAuditLoadFailure(ex.Message);
            }
        }
        else
        {
            // 读取失败时保留界面上已有的审计列表，只更新错误文案。
            LastAuditSyncErrorText = FormatAuditLoadFailure(auditLogs.FailureMessage);
        }

        _notifyPropertyChanged?.Invoke(nameof(LastAuditSyncErrorText));
    }

    private string FormatAuditLoadFailure(string message) =>
        string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.sync.auditLoadFailed"),
            message);

    // 仅做本地读取（线程池执行）：结果由 ApplySettlementUploads 在调用方上下文写回字段。
    private async Task<SettlementUploadsLoadResult> LoadSettlementUploadsSafelyAsync(CancellationToken cancellationToken)
    {
        if (_linklySettlementUploadQueueReader is null)
        {
            return SettlementUploadsLoadResult.Empty;
        }

        try
        {
            var overviewTask = _linklySettlementUploadQueueReader.GetOverviewAsync(cancellationToken);
            var itemsTask = _linklySettlementUploadQueueReader.GetActiveItemsAsync(cancellationToken: cancellationToken);
            await Task.WhenAll(overviewTask, itemsTask);
            return new SettlementUploadsLoadResult(await overviewTask, await itemsTask, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 结算上传队列独立于订单快照，读取失败时保留订单同步中心可用。
            return SettlementUploadsLoadResult.Empty with { FailureMessage = ex.Message };
        }
    }

    private void ApplySettlementUploads(SettlementUploadsLoadResult settlement)
    {
        _settlementOverview = settlement.Overview;
        _settlementQueueItems = settlement.Items;
        _settlementLoadFailureMessage = settlement.FailureMessage;
    }

    private void SelectAllAuditLogs()
    {
        foreach (var item in SyncCenterAuditLogs)
        {
            item.Selection.IsSelected = true;
        }
    }

    private async Task RetrySelectedAuditLogsAsync()
    {
        if (_logOutboxStore is null)
        {
            return;
        }

        var selected = SyncCenterAuditLogs
            .Where(item => item.Selection.IsSelected)
            .Select(item => item.EventId)
            .ToArray();
        if (selected.Length == 0)
        {
            return;
        }

        var reset = await _logOutboxStore.ResetOperationForRetryAsync(
            selected,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        if (reset > 0)
        {
            _operationAuditUploadService?.RequestUpload();
        }

        await RefreshAuditLogsSafelyAsync();
        _setStatusMessage?.Invoke(string.Format(
            _localization.CurrentCulture,
            _localization.T("shell.sync.auditRetryQueued"),
            reset));
    }

    private async Task ExecuteUploadSyncRetryAsync(
        Func<Task<UploadRetrySummary>> executeAsync,
        string retryingStatusKey)
    {
        if (_isAutoSyncRetrying || IsOrderSyncRetrying)
        {
            return;
        }

        IsOrderSyncRetrying = true;
        _notifyPropertyChanged?.Invoke(nameof(IsOrderSyncRetrying));
        RefreshSyncRetryCommandStates();
        _setStatusMessage?.Invoke(_localization.T(retryingStatusKey));
        try
        {
            var result = await executeAsync();
            await RefreshPendingSyncAsync();
            // 端点切换中断时剩余上传已排队到切换后继续，提示要与普通的"部分失败"区分开。
            _setStatusMessage?.Invoke(string.Format(
                _localization.CurrentCulture,
                _localization.T(result.WasInterrupted ? "shell.sync.retryInterrupted" : "shell.sync.retryCompleted"),
                result.UploadedCount,
                result.FailedCount));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RefreshPendingSyncAsync();
            _setStatusMessage?.Invoke(string.Format(
                _localization.CurrentCulture,
                _localization.T("shell.sync.retryFailed"),
                ex.Message));
        }
        finally
        {
            IsOrderSyncRetrying = false;
            _notifyPropertyChanged?.Invoke(nameof(IsOrderSyncRetrying));
            RefreshSyncRetryCommandStates();
        }
    }

    private bool CanRetrySyncEntity(SyncQueueListItem? item)
    {
        if (_isAutoSyncRetrying || IsOrderSyncRetrying || item is null || !item.CanRetry)
        {
            return false;
        }

        return item.EntityType.Equals(OrderEntityType, StringComparison.OrdinalIgnoreCase) ||
            (item.EntityType.Equals(LinklySettlementEntityType, StringComparison.OrdinalIgnoreCase) &&
             _linklySettlementUploadExecutionService is not null);
    }

    private bool CanRetryAllSyncOrders()
    {
        return !_isAutoSyncRetrying &&
            !IsOrderSyncRetrying &&
            SyncCenterOrders.Any(CanRetrySyncEntity);
    }

    private async Task<UploadRetrySummary> ExecuteOneUploadAsync(
        SyncQueueListItem item,
        CancellationToken cancellationToken)
    {
        if (item.EntityType.Equals(OrderEntityType, StringComparison.OrdinalIgnoreCase))
        {
            return ToSummary(await _orderUploadExecutionService.ExecuteOneAsync(item.EntityId, cancellationToken));
        }

        if (item.EntityType.Equals(LinklySettlementEntityType, StringComparison.OrdinalIgnoreCase) &&
            _linklySettlementUploadExecutionService is not null)
        {
            return ToSummary(await _linklySettlementUploadExecutionService.ExecuteOneAsync(item.EntityId, cancellationToken));
        }

        return UploadRetrySummary.Empty;
    }

    private async Task<UploadRetrySummary> ExecutePendingUploadsAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task<UploadRetrySummary>>
        {
            ExecuteOrderPendingAsync(cancellationToken)
        };
        if (_linklySettlementUploadExecutionService is not null)
        {
            tasks.Add(ExecuteSettlementPendingAsync(cancellationToken));
        }

        return CombineRetryResults(await Task.WhenAll(tasks));
    }

    private async Task<UploadRetrySummary> ExecuteAllUploadsAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<Task<UploadRetrySummary>>
        {
            // 手动"全部重试"覆盖全部待传/失败订单并忽略后台退避时间，与下面的结算队列口径一致。
            ExecuteOrderRetryAllAsync(cancellationToken)
        };
        if (_linklySettlementUploadQueueReader is not null &&
            _linklySettlementUploadExecutionService is not null)
        {
            var settlementIds = (await _linklySettlementUploadQueueReader.GetActiveItemsAsync(
                    int.MaxValue,
                    cancellationToken))
                .Where(item => item.Status is
                    LocalLinklySettlementUploadStatus.Pending or
                    LocalLinklySettlementUploadStatus.Rejected)
                .Select(item => item.SettlementGuid)
                .Distinct()
                .ToArray();
            if (settlementIds.Length > 0)
            {
                // 手动“全部重试”覆盖整个独立结算队列，并忽略后台退避时间。
                tasks.Add(ExecuteSelectedSettlementsAsync(settlementIds, cancellationToken));
            }
        }

        return CombineRetryResults(await Task.WhenAll(tasks));
    }

    private async Task<UploadRetrySummary> ExecuteSelectedUploadsAsync(
        IReadOnlyCollection<SyncQueueListItem> selectedItems,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task<UploadRetrySummary>>();
        var orderIds = selectedItems
            .Where(item => item.EntityType.Equals(OrderEntityType, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.EntityId)
            .Distinct()
            .ToArray();
        if (orderIds.Length > 0)
        {
            tasks.Add(ExecuteSelectedOrdersAsync(orderIds, cancellationToken));
        }

        var settlementIds = selectedItems
            .Where(item => item.EntityType.Equals(LinklySettlementEntityType, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.EntityId)
            .Distinct()
            .ToArray();
        if (settlementIds.Length > 0 && _linklySettlementUploadExecutionService is not null)
        {
            tasks.Add(ExecuteSelectedSettlementsAsync(settlementIds, cancellationToken));
        }

        return tasks.Count == 0
            ? UploadRetrySummary.Empty
            : CombineRetryResults(await Task.WhenAll(tasks));
    }

    // 后台自动上传：只取已到退避时间的订单。
    private async Task<UploadRetrySummary> ExecuteOrderPendingAsync(CancellationToken cancellationToken) =>
        ToSummary(await _orderUploadExecutionService.ExecutePendingAsync(cancellationToken: cancellationToken));

    private async Task<UploadRetrySummary> ExecuteOrderRetryAllAsync(CancellationToken cancellationToken) =>
        ToSummary(await _orderUploadExecutionService.ExecuteAllRetryableAsync(cancellationToken: cancellationToken));

    private async Task<UploadRetrySummary> ExecuteSettlementPendingAsync(CancellationToken cancellationToken) =>
        ToSummary(await _linklySettlementUploadExecutionService!.ExecutePendingAsync(cancellationToken: cancellationToken));

    private async Task<UploadRetrySummary> ExecuteSelectedOrdersAsync(
        IReadOnlyCollection<Guid> orderIds,
        CancellationToken cancellationToken) =>
        ToSummary(await _orderUploadExecutionService.ExecuteSelectedAsync(orderIds, cancellationToken));

    private async Task<UploadRetrySummary> ExecuteSelectedSettlementsAsync(
        IReadOnlyCollection<Guid> settlementIds,
        CancellationToken cancellationToken)
    {
        var results = new List<UploadRetrySummary>(settlementIds.Count);
        foreach (var settlementId in settlementIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ToSummary(await _linklySettlementUploadExecutionService!.ExecuteOneAsync(
                settlementId,
                cancellationToken)));
        }

        return CombineRetryResults(results);
    }

    private string BuildLastUploadErrorText()
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(_orderOverview.LastError))
        {
            errors.Add(_orderOverview.LastError);
        }

        if (!string.IsNullOrWhiteSpace(_settlementOverview.LastError))
        {
            errors.Add(string.Format(
                _localization.CurrentCulture,
                _localization.T("shell.sync.settlementError"),
                _settlementOverview.LastError));
        }

        if (!string.IsNullOrWhiteSpace(_settlementLoadFailureMessage))
        {
            errors.Add(string.Format(
                _localization.CurrentCulture,
                _localization.T("shell.sync.settlementLoadFailed"),
                _settlementLoadFailureMessage));
        }

        return errors.Count == 0
            ? _localization.T("shell.sync.noErrors")
            : string.Join(Environment.NewLine, errors.Distinct(StringComparer.Ordinal));
    }

    private static SyncQueueListItem MapSettlementQueueItem(LinklySettlementUploadQueueItem item)
    {
        var status = item.Status switch
        {
            LocalLinklySettlementUploadStatus.Uploading => "Syncing",
            LocalLinklySettlementUploadStatus.Rejected => "Failed",
            _ => item.Status.ToString()
        };
        var uploadError = !string.IsNullOrWhiteSpace(item.ErrorMessage)
            ? item.ErrorMessage
            : item.ErrorCode;
        var settlementDetails = string.Join(
            " | ",
            new[] { item.ConnectionMode, item.SettlementStatus.ToString(), item.ProviderSubmissionState.ToString() }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        var errorMessage = string.IsNullOrWhiteSpace(uploadError)
            ? settlementDetails
            : $"{settlementDetails} - {uploadError}";

        return new SyncQueueListItem(
            item.SettlementGuid,
            LinklySettlementEntityType,
            status,
            item.CreatedAt,
            item.LastTriedAt,
            errorMessage,
            Amount: null);
    }

    // 被端点切换中断的批次里，未尝试的订单不一定计入失败数；此时"未完成"按尝试数减成功数计。
    private static UploadRetrySummary ToSummary(OrderUploadExecutionResult result) =>
        new(
            result.AttemptedCount,
            result.UploadedCount,
            result.WasInterrupted
                ? Math.Max(result.FailedCount, result.AttemptedCount - result.UploadedCount)
                : result.FailedCount,
            result.WasInterrupted);

    private static UploadRetrySummary ToSummary(LinklySettlementUploadExecutionResult result) =>
        new(
            result.AttemptedCount,
            result.UploadedCount,
            result.FailedCount + result.DeferredCount,
            result.WasInterrupted);

    private static UploadRetrySummary CombineRetryResults(IEnumerable<UploadRetrySummary> results)
    {
        var attempted = 0;
        var uploaded = 0;
        var failed = 0;
        var interrupted = false;
        foreach (var result in results)
        {
            attempted += result.AttemptedCount;
            uploaded += result.UploadedCount;
            failed += result.FailedCount;
            interrupted |= result.WasInterrupted;
        }

        return new UploadRetrySummary(attempted, uploaded, failed, interrupted);
    }

    private sealed record SyncCenterLoadResult(
        ShellSyncCenterSnapshot OrderSnapshot,
        SettlementUploadsLoadResult Settlement,
        AuditLogsLoadResult AuditLogs);

    private sealed record SettlementUploadsLoadResult(
        LinklySettlementUploadOverview Overview,
        IReadOnlyList<LinklySettlementUploadQueueItem> Items,
        string? FailureMessage)
    {
        public static SettlementUploadsLoadResult Empty { get; } =
            new(new LinklySettlementUploadOverview(0, 0, 0, null), [], null);
    }

    private sealed record AuditLogSource(
        ClientLogOutboxStore? Store,
        DeviceAuthorizationContext? Scope);

    // FailureMessage 非空表示读取失败，此时 Records 为空且不覆盖界面上的旧列表。
    private sealed record AuditLogsLoadResult(
        IReadOnlyList<OperationAuditOutboxListItem> Records,
        string? FailureMessage);

    private sealed record UploadRetrySummary(
        int AttemptedCount,
        int UploadedCount,
        int FailedCount,
        bool WasInterrupted = false)
    {
        public static UploadRetrySummary Empty { get; } = new(0, 0, 0);
    }
}
