using System.Net;
using BlazorApp.Shared.Constants;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.PosSettings;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 收银端设置下发的客户端：按指纹轻量轮询 → 校验 → 把总部下发值「写穿」到各开关原来的本机存储 → 回执；
/// 在线时总部设置了的项锁定，离线（联网探测失败）时解锁、允许临时修改，恢复联网后以总部值为准。
///
/// 设计要点：
/// 1. 各开关仍然只存在原来的本机存储里（见 <see cref="PosSettingBindings"/>），消费方不用改；本服务只负责「把总部值写进去」。
/// 2. 总部值的快照（含来源）单独持久化，离线重启后依然知道哪些项是总部设置的、来自哪一层。
/// 3. 「实际生效值」永远以本机存储读到的值为准（回执、界面都用它），不是快照里的值。
/// 4. 恢复联网后的第一次成功同步、以及每次启动后的第一次成功同步，都会把总部值重新写一遍，并用上一版快照做基线，
///    识别「没经过业务开关页、直接在原来的页面里改过」的项，计入离线修改次数（审计 ReasonCode=DETECTED）。
/// 所有失败只记日志、不弹窗，下一轮重试；日志只含键名与结果。
/// </summary>
public sealed class PosSettingsService : IPosSettingsService
{
    /// <summary>每 4 个 15 秒拍同步一轮，约 60 秒。</summary>
    public const int TicksPerRound = 4;

    /// <summary>服务端未部署接口（404）时的退避时长。</summary>
    public static readonly TimeSpan NotFoundBackoff = TimeSpan.FromMinutes(10);

    /// <summary>定时同步一轮的总预算：同步挂在连接探测流程里，网络异常时不能长时间占住它。</summary>
    public static readonly TimeSpan ScheduledRoundBudget = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan FailureLogInterval = TimeSpan.FromMinutes(10);
    private const string LogCategory = "PosSettings";

    private readonly IPosSettingsApiClient _apiClient;
    private readonly IPosSettingsLocalStore _store;
    private readonly IReadOnlyList<IPosSettingBinding> _bindings;
    private readonly HashSet<string> _knownKeys;
    private readonly DeviceAuthorizationState _authorization;
    private readonly IOperationAuditLogger? _audit;
    private readonly IAppVersionProvider? _versionProvider;
    private readonly TimeProvider _timeProvider;

    // 串行化「读状态 → 改本机存储 → 落盘」这类多步异步操作；同步、离线修改、刷新共用同一把。
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    // 保护下面这些小的内存标志；只在极短的同步代码里持有，绝不在持有期间 await。
    private readonly object _gate = new();

    private bool _loaded;
    private volatile PosSettingsLocalState _state = PosSettingsLocalState.Empty;
    private volatile IReadOnlyDictionary<string, bool> _actual = new Dictionary<string, bool>();
    private volatile PosSettingsView _view = PosSettingsView.Empty;
    private bool _viewReady;
    private bool? _online;

    // 启动后的第一次同步成功、以及每次离线之后的第一次同步成功，都要把总部值重新核对写一遍。
    private bool _needsReapply = true;

    // 已应用但回执还没成功（回执失败后下一轮补发）。
    private bool _ackDirty;
    private string? _rejectedAckSignature;

    private Task<PosSettingsSyncResult>? _inFlight;
    private bool _needsImmediateRound = true;
    private int _ticksSinceRound;
    private string? _lastTickStoreCode;
    private DateTimeOffset _backoffUntil = DateTimeOffset.MinValue;
    private string? _lastFailureSignature;
    private DateTimeOffset _lastFailureLoggedAt;

    public PosSettingsService(
        IPosSettingsApiClient apiClient,
        IPosSettingsLocalStore store,
        IReadOnlyList<IPosSettingBinding> bindings,
        DeviceAuthorizationState authorization,
        IOperationAuditLogger? audit = null,
        IAppVersionProvider? versionProvider = null,
        TimeProvider? timeProvider = null)
    {
        _apiClient = apiClient;
        _store = store;
        _bindings = bindings;
        _knownKeys = bindings.Select(binding => binding.Key).ToHashSet(StringComparer.Ordinal);
        _authorization = authorization;
        _audit = audit;
        _versionProvider = versionProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler? Changed;

    public PosSettingsView Current => _view;

    public bool IsCashMovementEnabled =>
        _view.Find(PosSettingKeys.CashMovementEnabled)?.Value ?? false;

    // ───────────────────────── 读取 / 刷新 ─────────────────────────

    public async Task<PosSettingsView> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(CurrentStoreCode(), cancellationToken);
            await ReadActualAsync(cancellationToken);
        }
        finally
        {
            _stateGate.Release();
        }

        if (RebuildView())
        {
            RaiseChanged();
        }

        return _view;
    }

    // ───────────────────────── 本机修改 ─────────────────────────

    public async Task<PosSettingChangeOutcome> TrySetAsync(
        string key,
        bool value,
        CashierSessionDto? actor,
        CancellationToken cancellationToken = default)
    {
        if (!_knownKeys.Contains(key))
        {
            return PosSettingChangeOutcome.UnknownKey;
        }

        var binding = _bindings.First(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        PosSettingChangeOutcome outcome;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            // 必须先把本机状态读进来再判断是否被锁：否则启动早期（状态还没加载）会把总部锁定的项当成「没下发」放行。
            await EnsureLoadedAsync(CurrentStoreCode(), cancellationToken);
            var managed = _state.Values.TryGetValue(key, out var stored);
            if (managed && IsOnlineForLock())
            {
                return PosSettingChangeOutcome.Locked;
            }

            try
            {
                var previous = await binding.ReadAsync(cancellationToken);
                if (previous != value)
                {
                    await binding.WriteAsync(value, cancellationToken);
                    SetActual(key, value);

                    if (managed)
                    {
                        // 离线临时修改：登记覆盖、累计次数、写审计。恢复联网同步成功后会被总部值覆盖。
                        var state = _state;
                        var overrides = new Dictionary<string, bool>(state.Overrides, StringComparer.Ordinal)
                        {
                            [key] = value
                        };
                        var next = state with
                        {
                            Overrides = overrides,
                            OfflineOverrideCount = state.OfflineOverrideCount + 1
                        };
                        await _store.SaveAsync(next, cancellationToken);
                        _state = next;
                        RecordOverrideAudit(actor, "OFFLINE", key, previous, value, stored!);
                    }
                }

                outcome = PosSettingChangeOutcome.Applied;
            }
            catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
            {
                ConsoleLog.Write(LogCategory, $"set failed key={key} error={ex.GetType().Name}");
                outcome = PosSettingChangeOutcome.Failed;
            }
        }
        finally
        {
            _stateGate.Release();
        }

        if (RebuildView())
        {
            RaiseChanged();
        }

        return outcome;
    }

    // ───────────────────────── 联网状态 ─────────────────────────

    public void ReportConnectivity(bool isOnline)
    {
        bool changed;
        lock (_gate)
        {
            changed = _online != isOnline;
            _online = isOnline;
            if (!isOnline)
            {
                // 离线期间本机可能被改过：恢复联网后的第一次成功同步要把总部值重新核对写一遍。
                _needsReapply = true;
            }
        }

        if (changed && RebuildView())
        {
            RaiseChanged();
        }
    }

    // ───────────────────────── 同步 ─────────────────────────

    public Task<PosSettingsSyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        return RunExclusiveAsync(budget: null, cancellationToken);
    }

    public async Task<PosSettingsSyncResult?> RunScheduledTickAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var storeCode = CurrentStoreCode();
        if (storeCode is null)
        {
            // 设备认证尚未就绪：不消耗「就绪后立即同步一次」的机会，认证完成后的第一拍再同步。
            return null;
        }

        lock (_gate)
        {
            if (!string.Equals(_lastTickStoreCode, storeCode, StringComparison.Ordinal))
            {
                // 第一次见到门店或设备换店：下一拍立即同步，不等 4 拍。
                _lastTickStoreCode = storeCode;
                _needsImmediateRound = true;
            }

            _ticksSinceRound++;
            var due = _needsImmediateRound || _ticksSinceRound >= TicksPerRound;
            if (!due)
            {
                return null;
            }

            if (_timeProvider.GetUtcNow() < _backoffUntil)
            {
                // 404 退避期内静默跳过（保留「到点」状态，退避结束后的第一拍就会同步），不刷日志。
                return null;
            }

            _needsImmediateRound = false;
            _ticksSinceRound = 0;
        }

        return await RunExclusiveAsync(ScheduledRoundBudget, cancellationToken);
    }

    /// <summary>
    /// 同一时刻只允许一个同步在飞：已有进行中的同步时复用它的结果，不再发第二个请求。
    /// 调用方自己的取消只影响自己的等待，不会取消被复用的那一轮。
    /// </summary>
    private async Task<PosSettingsSyncResult> RunExclusiveAsync(TimeSpan? budget, CancellationToken cancellationToken)
    {
        TaskCompletionSource<PosSettingsSyncResult>? owned = null;
        Task<PosSettingsSyncResult> shared;
        lock (_gate)
        {
            if (_inFlight is { } existing)
            {
                shared = existing;
            }
            else
            {
                owned = new TaskCompletionSource<PosSettingsSyncResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight = owned.Task;
                shared = owned.Task;
            }
        }

        if (owned is not null)
        {
            PosSettingsSyncResult? result = null;
            Exception? failure = null;
            try
            {
                result = await ExecuteRoundAsync(budget, cancellationToken);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // 先清掉「在飞」标记再完成任务：等待者醒来后立刻再发起同步时，不能复用到这个已结束的结果。
            lock (_gate)
            {
                _inFlight = null;
            }

            if (failure is OperationCanceledException canceled)
            {
                owned.TrySetCanceled(canceled.CancellationToken);
            }
            else if (failure is not null)
            {
                owned.TrySetException(failure);
            }
            else
            {
                owned.TrySetResult(result!);
            }
        }

        return await shared.WaitAsync(cancellationToken);
    }

    private async Task<PosSettingsSyncResult> ExecuteRoundAsync(TimeSpan? budget, CancellationToken cancellationToken)
    {
        using var budgetSource = budget is { } limit ? new CancellationTokenSource(limit, _timeProvider) : null;
        using var linked = budgetSource is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budgetSource.Token);
        var token = linked?.Token ?? cancellationToken;

        try
        {
            return await RunRoundAsync(token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 调用方没有取消，说明是本轮预算耗尽：当作失败，下一轮重试。
            return Report(PosSettingsSyncOutcome.Failed, "sync round timed out");
        }
    }

    private async Task<PosSettingsSyncResult> RunRoundAsync(CancellationToken cancellationToken)
    {
        var storeCode = CurrentStoreCode();
        if (storeCode is null)
        {
            return new PosSettingsSyncResult(PosSettingsSyncOutcome.NotReady, "device authorization is not ready");
        }

        string? knownHash;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedAsync(storeCode, cancellationToken);
            knownHash = _state.Hash;
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            return Report(PosSettingsSyncOutcome.Failed, $"read local state failed: {ex.GetType().Name}");
        }
        finally
        {
            _stateGate.Release();
        }

        PosSettingsSyncDto sync;
        try
        {
            sync = await _apiClient.GetSyncAsync(knownHash, cancellationToken);
        }
        catch (Exception ex) when (IsCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (CatalogApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // 服务端还没部署该接口：视为「暂不支持」，每 10 分钟再试一次；本机各开关沿用自己的值，不锁定任何项。
            lock (_gate)
            {
                _backoffUntil = _timeProvider.GetUtcNow() + NotFoundBackoff;
            }

            return Report(PosSettingsSyncOutcome.ServerUnsupported, "sync endpoint not found (HTTP 404), backing off");
        }
        catch (Exception ex)
        {
            return Report(PosSettingsSyncOutcome.Failed, $"sync request failed: {ex.Message}");
        }

        lock (_gate)
        {
            // 接口可用了：恢复 60 秒节奏。
            _backoffUntil = DateTimeOffset.MinValue;
        }

        // 没有指纹的响应无法比较新旧、也无法回执，按无效响应丢弃，绝不改动本机任何设置。
        if (string.IsNullOrWhiteSpace(sync.Hash))
        {
            return Report(PosSettingsSyncOutcome.Discarded, "sync response has no hash");
        }

        // 请求期间设备换了店：这是旧店的设置，丢弃。
        if (!string.Equals(CurrentStoreCode(), storeCode, StringComparison.Ordinal))
        {
            return Report(PosSettingsSyncOutcome.Discarded, "device store changed during sync");
        }

        Dictionary<string, PosSettingStoredValue>? incoming = null;
        if (sync.Changed)
        {
            incoming = ParseSettings(sync.Settings);
            if (incoming is null)
            {
                return Report(PosSettingsSyncOutcome.Discarded, "sync response is changed but has no settings");
            }
        }

        PosSettingsSyncResult? failed = null;
        PosSettingsLocalState applied;
        IReadOnlyDictionary<string, bool> actualAfter;
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            (failed, applied, actualAfter) = await ApplyRoundAsync(storeCode, sync, incoming, cancellationToken);
        }
        finally
        {
            _stateGate.Release();
        }

        if (RebuildView())
        {
            RaiseChanged();
        }

        if (failed is not null)
        {
            return failed;
        }

        ClearFailureState();
        if (sync.Changed)
        {
            ConsoleLog.Write(LogCategory, $"applied store={storeCode} managedKeys={applied.Values.Count}");
        }

        var ackPending = false;
        bool ackNeeded;
        lock (_gate)
        {
            ackNeeded = _ackDirty
                || !string.Equals(applied.Hash, applied.AckedHash, StringComparison.Ordinal)
                || applied.OfflineOverrideCount != applied.AckedOverrideCount;
        }

        if (ackNeeded)
        {
            ackPending = !await TryAckAsync(applied, actualAfter, cancellationToken);
        }

        return new PosSettingsSyncResult(
            sync.Changed ? PosSettingsSyncOutcome.Applied : PosSettingsSyncOutcome.UpToDate,
            ackPending ? "ack pending" : null);
    }

    /// <summary>
    /// 把本轮响应应用到本机（调用方已持有 <see cref="_stateGate"/>）。
    /// 返回 (失败结果, 应用后的状态, 应用后的实际值)；失败结果非 null 表示本轮没有落盘快照，下一轮重试。
    /// </summary>
    private async Task<(PosSettingsSyncResult? Failed, PosSettingsLocalState State, IReadOnlyDictionary<string, bool> Actual)>
        ApplyRoundAsync(
            string storeCode,
            PosSettingsSyncDto sync,
            Dictionary<string, PosSettingStoredValue>? incoming,
            CancellationToken cancellationToken)
    {
        var state = _state;

        // 协议约定指纹不同必然 Changed=true；两者矛盾说明响应不可信，不动本机设置。
        if (!sync.Changed && !string.Equals(sync.Hash, state.Hash, StringComparison.Ordinal))
        {
            return (
                Report(PosSettingsSyncOutcome.Failed, "sync response is inconsistent (changed=false but hash differs)"),
                state,
                _actual);
        }

        bool needReapply;
        lock (_gate)
        {
            needReapply = _needsReapply;
        }

        needReapply |= sync.Changed || state.Overrides.Count > 0;

        var previous = state.Values;
        IReadOnlyDictionary<string, PosSettingStoredValue> target = sync.Changed ? incoming! : previous;

        // 以各开关本机存储里真实的值为准（可能被离线期间在原来的页面里改过）。
        await ReadActualAsync(cancellationToken);
        var actual = new Dictionary<string, bool>(_actual, StringComparer.Ordinal);
        var detected = 0;

        if (needReapply)
        {
            foreach (var binding in _bindings)
            {
                if (!target.TryGetValue(binding.Key, out var wanted))
                {
                    // 总部没设置这一项（或已清除）：本机沿用自己的值，不动它。
                    continue;
                }

                var current = actual.TryGetValue(binding.Key, out var read) && read;

                // 基线 = 上一版已应用的总部值。本机值与基线不同、又没登记过覆盖 = 绕过业务开关页直接改的。
                if (previous.TryGetValue(binding.Key, out var baseline)
                    && current != baseline.Value
                    && !state.Overrides.ContainsKey(binding.Key))
                {
                    detected++;
                    RecordOverrideAudit(null, "DETECTED", binding.Key, baseline.Value, current, baseline);
                }

                if (current == wanted.Value)
                {
                    continue;
                }

                try
                {
                    await binding.WriteAsync(wanted.Value, cancellationToken);
                    actual[binding.Key] = wanted.Value;
                }
                catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
                {
                    // 任何一项写失败都不落盘快照：下一轮仍会重新核对并重试，不会留下「快照说已应用、本机其实没写」的状态。
                    _actual = actual;
                    return (
                        Report(PosSettingsSyncOutcome.Failed, $"write setting failed key={binding.Key} error={ex.GetType().Name}"),
                        state,
                        actual);
                }
            }
        }

        var next = state with
        {
            StoreCode = storeCode,
            Hash = sync.Changed ? sync.Hash : state.Hash,
            Values = target,
            SyncedAtUtc = _timeProvider.GetUtcNow(),
            Overrides = new Dictionary<string, bool>(),
            OfflineOverrideCount = state.OfflineOverrideCount + detected
        };

        // 稳态（指纹没变、没有要核对的）只更新内存里的「最近同步时间」，不每分钟写一次 SQLite。
        if (needReapply || sync.Changed)
        {
            await _store.SaveAsync(next, cancellationToken);
        }

        _state = next;
        _actual = actual;
        lock (_gate)
        {
            if (needReapply)
            {
                _needsReapply = false;
                _ackDirty = true;
            }
        }

        return (null, next, actual);
    }

    /// <summary>
    /// 回执：已应用的指纹、各开关此刻的实际值、累计离线修改次数。返回 true 表示服务端已接受。
    /// 400 说明服务端不认这份回执：本进程内不再对同一份重试，避免死循环；其它失败下一轮重试。
    /// </summary>
    private async Task<bool> TryAckAsync(
        PosSettingsLocalState state,
        IReadOnlyDictionary<string, bool> actual,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Hash))
        {
            return true;
        }

        var signature = $"{state.Hash}|{state.OfflineOverrideCount}";
        lock (_gate)
        {
            if (string.Equals(_rejectedAckSignature, signature, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // 回执只报本端支持的项（键集合就是本机绑定的那 9 项）。
        var applied = _bindings
            .Where(binding => actual.ContainsKey(binding.Key))
            .ToDictionary(binding => binding.Key, binding => actual[binding.Key], StringComparer.Ordinal);

        try
        {
            await _apiClient.AckAsync(
                new PosSettingsAckRequest(
                    state.Hash,
                    applied,
                    _versionProvider?.CurrentVersion,
                    state.OfflineOverrideCount),
                cancellationToken);
        }
        catch (Exception ex) when (IsCancellation(ex, cancellationToken))
        {
            throw;
        }
        catch (CatalogApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            lock (_gate)
            {
                _rejectedAckSignature = signature;
            }

            ConsoleLog.Write(
                LogCategory,
                $"ack rejected code={ex.ErrorCode ?? "-"}; will not retry this ack in this process");
            return false;
        }
        catch (Exception ex)
        {
            Report(PosSettingsSyncOutcome.Failed, $"ack request failed: {ex.Message}");
            return false;
        }

        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            // 回执期间如果快照已被替换（又同步到新指纹），不能把旧回执记在新指纹头上。
            if (string.Equals(_state.Hash, state.Hash, StringComparison.Ordinal))
            {
                var acked = _state with
                {
                    AckedHash = state.Hash,
                    AckedOverrideCount = state.OfflineOverrideCount
                };
                await _store.SaveAsync(acked, cancellationToken);
                _state = acked;
            }
        }
        catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
        {
            Report(PosSettingsSyncOutcome.Failed, $"record ack failed: {ex.GetType().Name}");
            return false;
        }
        finally
        {
            _stateGate.Release();
        }

        lock (_gate)
        {
            _ackDirty = false;
        }

        ConsoleLog.Write(LogCategory, $"acked overrideCount={state.OfflineOverrideCount}");
        return true;
    }

    // ───────────────────────── 内部工具 ─────────────────────────

    /// <summary>
    /// 校验并整理响应里的设置：只保留本机认识的键（比本版本新的服务端可能加了新键，直接忽略）；
    /// 来源缺失按 global。Settings 为 null 表示响应不完整（返回 null 让调用方丢弃整份响应）。
    /// </summary>
    private Dictionary<string, PosSettingStoredValue>? ParseSettings(
        IReadOnlyDictionary<string, PosSettingValueDto>? settings)
    {
        if (settings is null)
        {
            return null;
        }

        var result = new Dictionary<string, PosSettingStoredValue>(StringComparer.Ordinal);
        foreach (var (key, dto) in settings)
        {
            if (!_knownKeys.Contains(key) || dto is null)
            {
                continue;
            }

            var source = string.IsNullOrWhiteSpace(dto.Source) ? PosSettingScopes.Global : dto.Source.Trim();
            result[key] = new PosSettingStoredValue(dto.Value, source);
        }

        return result;
    }

    /// <summary>调用方必须已持有 <see cref="_stateGate"/>。首次调用从本机读入状态；门店变了就作废旧店的快照。</summary>
    private async Task EnsureLoadedAsync(string? storeCode, CancellationToken cancellationToken)
    {
        if (!_loaded)
        {
            _state = await _store.LoadAsync(cancellationToken);
            _loaded = true;
        }

        if (storeCode is not null && !string.Equals(_state.StoreCode, storeCode, StringComparison.Ordinal))
        {
            // 换店（或首次绑定）：旧店的快照、覆盖记录、累计次数都不属于这家店，一并作废；本机各开关保持当前值。
            _state = _state.ForStore(storeCode);
            lock (_gate)
            {
                _needsReapply = true;
            }
        }
    }

    /// <summary>调用方必须已持有 <see cref="_stateGate"/>。把各开关真正的本机值读一遍；单项读失败时沿用上一次的值。</summary>
    private async Task ReadActualAsync(CancellationToken cancellationToken)
    {
        var next = new Dictionary<string, bool>(StringComparer.Ordinal);
        var previous = _actual;
        foreach (var binding in _bindings)
        {
            try
            {
                next[binding.Key] = await binding.ReadAsync(cancellationToken);
            }
            catch (Exception ex) when (!IsCancellation(ex, cancellationToken))
            {
                ConsoleLog.Write(LogCategory, $"read failed key={binding.Key} error={ex.GetType().Name}");
                if (previous.TryGetValue(binding.Key, out var old))
                {
                    next[binding.Key] = old;
                }
            }
        }

        _actual = next;
        lock (_gate)
        {
            _viewReady = true;
        }
    }

    private void SetActual(string key, bool value)
    {
        var next = new Dictionary<string, bool>(_actual, StringComparer.Ordinal) { [key] = value };
        _actual = next;
    }

    private bool IsOnlineForLock()
    {
        lock (_gate)
        {
            // 联网探测还没出结果时按在线：宁可先锁着，也不要在启动的头几秒让收银员改到总部锁定的项。
            return _online ?? true;
        }
    }

    /// <summary>按当前内存状态重建 <see cref="Current"/>，返回内容是否变了（没变就不必通知界面）。</summary>
    private bool RebuildView()
    {
        lock (_gate)
        {
            if (!_viewReady)
            {
                return false;
            }

            var online = _online ?? true;
            var state = _state;
            var actual = _actual;
            var items = new List<PosSettingState>(_bindings.Count);
            foreach (var binding in _bindings)
            {
                var value = actual.TryGetValue(binding.Key, out var read) && read;
                var managed = state.Values.TryGetValue(binding.Key, out var stored);
                items.Add(new PosSettingState(
                    binding.Key,
                    value,
                    managed ? stored!.Source : null,
                    managed,
                    IsLocked: managed && online,
                    IsOfflineOverride: managed && value != stored!.Value));
            }

            var next = new PosSettingsView(items, online, state.SyncedAtUtc);
            var changed = !ViewsEqual(_view, next);
            _view = next;
            return changed;
        }
    }

    private static bool ViewsEqual(PosSettingsView left, PosSettingsView right)
    {
        return left.IsOnline == right.IsOnline
            && left.LastSyncedAtUtc == right.LastSyncedAtUtc
            && left.Items.Count == right.Items.Count
            && left.Items.Zip(right.Items).All(pair => pair.First == pair.Second);
    }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // 订阅方（界面刷新）出错不能影响同步与修改的结果。
                ConsoleLog.Write(LogCategory, $"changed handler failed error={ex.GetType().Name}");
            }
        }
    }

    private void RecordOverrideAudit(
        CashierSessionDto? actor,
        string reasonCode,
        string key,
        bool previous,
        bool current,
        PosSettingStoredValue managed)
    {
        if (_audit is null)
        {
            return;
        }

        try
        {
            OperationAuditEvents.RecordActorAction(
                _audit,
                OperationAuditTypes.PosSettingOfflineOverride,
                "Succeeded",
                actor,
                reasonCode,
                $"{key}: {Flag(previous)} -> {Flag(current)} (headquarters {managed.Source}={Flag(managed.Value)})");
        }
        catch (Exception ex)
        {
            // 审计写入失败不能影响收银员改开关，也不能让同步失败。
            ConsoleLog.Write(LogCategory, $"audit failed key={key} error={ex.GetType().Name}");
        }
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private string? CurrentStoreCode()
    {
        // 与小票资料同步使用同一个来源（设备认证上下文里的门店代码）。
        return _authorization.Current?.StoreCode is { Length: > 0 } code ? code : null;
    }

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken)
    {
        return exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }

    private void ClearFailureState()
    {
        lock (_gate)
        {
            _lastFailureSignature = null;
        }
    }

    /// <summary>
    /// 记录失败并生成结果。同样的失败连续出现时只记第一条，之后每 10 分钟最多再记一条，
    /// 避免离线或服务端异常时每 60 秒刷一行日志。
    /// </summary>
    private PosSettingsSyncResult Report(PosSettingsSyncOutcome outcome, string detail)
    {
        var signature = $"{outcome}:{detail}";
        var now = _timeProvider.GetUtcNow();
        bool shouldLog;
        lock (_gate)
        {
            shouldLog = !string.Equals(signature, _lastFailureSignature, StringComparison.Ordinal)
                || now - _lastFailureLoggedAt >= FailureLogInterval;
            if (shouldLog)
            {
                _lastFailureSignature = signature;
                _lastFailureLoggedAt = now;
            }
        }

        if (shouldLog)
        {
            ConsoleLog.Write(LogCategory, $"sync {outcome}: {detail}");
        }

        return new PosSettingsSyncResult(outcome, detail);
    }
}
