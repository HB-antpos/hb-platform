using System.Net;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.PosSettings;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 总部下发收银端设置的客户端：写穿到本机存储、在线锁定 / 离线解锁、离线临时修改的登记与审计、
/// 恢复联网后被总部值覆盖、绕过业务开关页的改动识别、404 退避、回执重试与 400 终止、换店作废快照。
/// 服务会写 ConsoleLog，因此与其他全局日志用例串行。
/// </summary>
[Collection(GlobalLoggingTestCollection.Name)]
public sealed class PosSettingsServiceTests
{
    [Fact]
    public async Task Changed_settings_are_written_through_persisted_and_acked_with_actual_values()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "store"), ("receipt.bankReceipt", false, "global"));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(PosSettingsSyncOutcome.Applied, result.Outcome);
        // 总部值已写进各开关原来的本机存储（消费方继续读原来的地方）。
        Assert.True(harness.Local[PosSettingKeys.ManualCard]);
        Assert.False(harness.Local[PosSettingKeys.BankReceipt]);

        var view = harness.Service.Current;
        var manual = view.Find(PosSettingKeys.ManualCard)!;
        Assert.True(manual.Value);
        Assert.True(manual.IsManaged);
        Assert.True(manual.IsLocked);
        Assert.Equal("store", manual.Source);
        // 总部没设置的项：沿用本机值、不锁定。
        Assert.False(view.Find(PosSettingKeys.Voucher)!.IsManaged);
        Assert.False(view.Find(PosSettingKeys.Voucher)!.IsLocked);

        var saved = await harness.Store.LoadAsync();
        Assert.Equal(harness.Api.Hash, saved.Hash);
        Assert.Equal("S001", saved.StoreCode);

        var ack = Assert.Single(harness.Api.Acks);
        Assert.Equal(harness.Api.Hash, ack.Hash);
        Assert.Equal(0, ack.OfflineOverrideCount);
        // 回执报的是「实际生效值」，且覆盖本端全部 9 项，不只是总部设置了的。
        Assert.Equal(PosSettingCatalog.All.Count, ack.Applied!.Count);
        Assert.True(ack.Applied![PosSettingKeys.ManualCard]);
        Assert.Equal(harness.Api.Hash, (await harness.Store.LoadAsync()).AckedHash);
    }

    [Fact]
    public async Task Unchanged_hash_makes_no_writes_and_no_second_ack()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "global"));
        await harness.Service.SyncNowAsync();
        harness.Writes.Clear();

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(PosSettingsSyncOutcome.UpToDate, result.Outcome);
        // 第二次同步带上了第一次应用的指纹（服务端据此回「没变化」）。
        Assert.Equal(harness.Api.Hash, harness.Api.KnownHashes[1]);
        Assert.Empty(harness.Writes);
        Assert.Single(harness.Api.Acks);
    }

    [Fact]
    public async Task Managed_keys_are_locked_online_and_unmanaged_keys_stay_editable()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "global"));
        await harness.Service.SyncNowAsync();
        harness.Service.ReportConnectivity(true);

        Assert.Equal(
            PosSettingChangeOutcome.Locked,
            await harness.Service.TrySetAsync(PosSettingKeys.ManualCard, false, null));
        Assert.True(harness.Local[PosSettingKeys.ManualCard]);

        Assert.Equal(
            PosSettingChangeOutcome.Applied,
            await harness.Service.TrySetAsync(PosSettingKeys.Voucher, true, null));
        Assert.True(harness.Local[PosSettingKeys.Voucher]);
        // 没下发的项属于普通本机设置：不计覆盖、不写审计。
        Assert.Empty(harness.Audit.Events);
        Assert.Equal(0, (await harness.Store.LoadAsync()).OfflineOverrideCount);
    }

    [Fact]
    public async Task Unknown_key_is_rejected()
    {
        using var harness = new Harness();

        Assert.Equal(
            PosSettingChangeOutcome.UnknownKey,
            await harness.Service.TrySetAsync("no.such.key", true, null));
    }

    [Fact]
    public async Task Offline_unlocks_managed_keys_and_override_is_counted_and_audited()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", false, "store"));
        await harness.Service.SyncNowAsync();
        harness.Service.ReportConnectivity(false);

        Assert.False(harness.Service.Current.Find(PosSettingKeys.ManualCard)!.IsLocked);
        Assert.True(harness.Service.Current.ShowsOfflineBanner);

        var actor = new Hbpos.Contracts.Cashiers.CashierSessionDto(
            "C1", "U1", "Amy", "S001", "DEV1", [], [], ["S001"], false, false, false);
        var outcome = await harness.Service.TrySetAsync(PosSettingKeys.ManualCard, true, actor);

        Assert.Equal(PosSettingChangeOutcome.Applied, outcome);
        Assert.True(harness.Local[PosSettingKeys.ManualCard]);
        var state = harness.Service.Current.Find(PosSettingKeys.ManualCard)!;
        Assert.True(state.Value);
        Assert.True(state.IsOfflineOverride);

        var saved = await harness.Store.LoadAsync();
        Assert.Equal(1, saved.OfflineOverrideCount);
        Assert.True(saved.Overrides[PosSettingKeys.ManualCard]);

        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("POS_SETTING_OFFLINE_OVERRIDE", audit.OperationType);
        Assert.Equal("OFFLINE", audit.ReasonCode);
        Assert.Equal("Amy", audit.CashierName);
        Assert.Equal("payment.manualCard: false -> true (headquarters store=false)", audit.SafeMessage);
    }

    [Fact]
    public async Task Reconnect_sync_restores_headquarters_value_clears_override_and_reports_the_count()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", false, "store"));
        await harness.Service.SyncNowAsync();
        harness.Service.ReportConnectivity(false);
        await harness.Service.TrySetAsync(PosSettingKeys.ManualCard, true, null);
        harness.Api.Acks.Clear();

        harness.Service.ReportConnectivity(true);
        var result = await harness.Service.SyncNowAsync();

        // 指纹没变，但恢复联网后的第一次成功同步仍要把总部值写回去。
        Assert.Equal(PosSettingsSyncOutcome.UpToDate, result.Outcome);
        Assert.False(harness.Local[PosSettingKeys.ManualCard]);
        var state = harness.Service.Current.Find(PosSettingKeys.ManualCard)!;
        Assert.False(state.IsOfflineOverride);
        Assert.True(state.IsLocked);

        var saved = await harness.Store.LoadAsync();
        Assert.Empty(saved.Overrides);
        // 累计次数只增不减，并随回执上报。
        Assert.Equal(1, saved.OfflineOverrideCount);
        var ack = Assert.Single(harness.Api.Acks);
        Assert.Equal(1, ack.OfflineOverrideCount);
        Assert.False(ack.Applied![PosSettingKeys.ManualCard]);
    }

    [Fact]
    public async Task Change_made_outside_the_business_switch_page_is_detected_on_reconnect()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.voucher", false, "global"));
        await harness.Service.SyncNowAsync();
        harness.Service.ReportConnectivity(false);

        // 绕过业务开关页，直接在原来的页面里改了本机存储。
        harness.Local[PosSettingKeys.Voucher] = true;
        harness.Service.ReportConnectivity(true);
        await harness.Service.SyncNowAsync();

        Assert.False(harness.Local[PosSettingKeys.Voucher]);
        Assert.Equal(1, (await harness.Store.LoadAsync()).OfflineOverrideCount);
        var audit = Assert.Single(harness.Audit.Events);
        Assert.Equal("DETECTED", audit.ReasonCode);
        Assert.Null(audit.CashierName);
    }

    [Fact]
    public async Task New_headquarters_value_is_not_mistaken_for_a_local_edit()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.voucher", false, "global"));
        await harness.Service.SyncNowAsync();

        // 总部把它改成开：本机值与新值不同，但与基线（上一版总部值）相同，不算本机改动。
        harness.Api.Server(("payment.voucher", true, "global"));
        await harness.Service.SyncNowAsync();

        Assert.True(harness.Local[PosSettingKeys.Voucher]);
        Assert.Equal(0, (await harness.Store.LoadAsync()).OfflineOverrideCount);
        Assert.Empty(harness.Audit.Events);
    }

    [Fact]
    public async Task Persisted_snapshot_survives_restart_and_keeps_keys_locked_or_unlocked_by_connectivity()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "device"));
        await harness.Service.SyncNowAsync();

        var restarted = harness.NewService();
        await restarted.RefreshAsync();

        // 联网探测还没出结果：按在线处理，先锁着。
        var locked = restarted.Current.Find(PosSettingKeys.ManualCard)!;
        Assert.True(locked.IsManaged);
        Assert.True(locked.IsLocked);
        Assert.Equal("device", locked.Source);

        restarted.ReportConnectivity(false);
        Assert.False(restarted.Current.Find(PosSettingKeys.ManualCard)!.IsLocked);
    }

    [Fact]
    public async Task Locked_state_is_enforced_even_before_the_first_refresh()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "global"));
        await harness.Service.SyncNowAsync();

        // 重启后还没 RefreshAsync 就收到修改请求：必须先读入本机状态再判断，不能当成「没下发」放行。
        var restarted = harness.NewService();
        var outcome = await restarted.TrySetAsync(PosSettingKeys.ManualCard, false, null);

        Assert.Equal(PosSettingChangeOutcome.Locked, outcome);
        Assert.True(harness.Local[PosSettingKeys.ManualCard]);
    }

    [Fact]
    public async Task Cash_movement_is_off_by_default_and_follows_the_headquarters_value()
    {
        using var harness = new Harness();
        await harness.Service.RefreshAsync();
        Assert.False(harness.Service.IsCashMovementEnabled);

        // 服务端缺省值（来源 default）：关。
        harness.Api.Server(("cashMovement.enabled", false, "default"));
        await harness.Service.SyncNowAsync();
        Assert.False(harness.Service.IsCashMovementEnabled);
        Assert.Equal("default", harness.Service.Current.Find(PosSettingKeys.CashMovementEnabled)!.Source);

        harness.Api.Server(("cashMovement.enabled", true, "store"));
        await harness.Service.SyncNowAsync();
        Assert.True(harness.Service.IsCashMovementEnabled);
    }

    [Fact]
    public async Task Not_found_means_server_unsupported_locks_nothing_and_backs_off()
    {
        using var harness = new Harness();
        harness.Api.OnSync = _ => throw new CatalogApiException("missing", HttpStatusCode.NotFound);

        var first = await harness.Service.RunScheduledTickAsync();

        Assert.Equal(PosSettingsSyncOutcome.ServerUnsupported, first!.Outcome);
        Assert.False(harness.Service.Current.HasLockedItems);

        // 退避期内每到点的拍都静默跳过；退避结束后第一拍再试。
        for (var i = 0; i < PosSettingsService.TicksPerRound + 1; i++)
        {
            Assert.Null(await harness.Service.RunScheduledTickAsync());
        }

        Assert.Single(harness.Api.KnownHashes);
        harness.Time.Advance(PosSettingsService.NotFoundBackoff + TimeSpan.FromSeconds(1));
        Assert.NotNull(await harness.Service.RunScheduledTickAsync());
        Assert.Equal(2, harness.Api.KnownHashes.Count);
    }

    [Fact]
    public async Task Scheduled_tick_runs_immediately_then_every_fourth_tick()
    {
        using var harness = new Harness();
        harness.Api.Server();

        Assert.NotNull(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.NotNull(await harness.Service.RunScheduledTickAsync());

        Assert.Equal(2, harness.Api.KnownHashes.Count);
    }

    [Fact]
    public async Task Tick_waits_for_device_authorization()
    {
        using var harness = new Harness();
        harness.Auth.Clear();

        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Empty(harness.Api.KnownHashes);

        harness.Auth.Set(new DeviceAuthorizationContext("DEV1", "S001", "HW", "AUTH"));
        harness.Api.Server();
        Assert.NotNull(await harness.Service.RunScheduledTickAsync());
    }

    [Fact]
    public async Task Invalid_responses_change_nothing()
    {
        using var harness = new Harness();

        // 没有指纹。
        harness.Api.OnSync = _ => Task.FromResult(new PosSettingsSyncDto(true, string.Empty, new Dictionary<string, PosSettingValueDto>()));
        Assert.Equal(PosSettingsSyncOutcome.Discarded, (await harness.Service.SyncNowAsync()).Outcome);

        // Changed 却没有设置内容。
        harness.Api.OnSync = _ => Task.FromResult(new PosSettingsSyncDto(true, "abc", null));
        Assert.Equal(PosSettingsSyncOutcome.Discarded, (await harness.Service.SyncNowAsync()).Outcome);

        Assert.False((await harness.Store.LoadAsync()).HasSnapshot);
        Assert.Empty(harness.Writes);
        Assert.False(harness.Service.Current.HasLockedItems);
    }

    [Fact]
    public async Task Unknown_keys_from_a_newer_server_are_ignored()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "global"), ("future.newFlag", true, "global"));

        await harness.Service.SyncNowAsync();

        Assert.Equal(1, (await harness.Store.LoadAsync()).Values.Count);
        Assert.False(harness.Local.ContainsKey("future.newFlag"));
    }

    [Fact]
    public async Task Failed_write_does_not_persist_the_snapshot_and_is_retried_next_round()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "global"));
        harness.FailWritesFor.Add(PosSettingKeys.ManualCard);

        var failed = await harness.Service.SyncNowAsync();

        Assert.Equal(PosSettingsSyncOutcome.Failed, failed.Outcome);
        Assert.False((await harness.Store.LoadAsync()).HasSnapshot);
        Assert.Empty(harness.Api.Acks);

        harness.FailWritesFor.Clear();
        var retried = await harness.Service.SyncNowAsync();

        Assert.Equal(PosSettingsSyncOutcome.Applied, retried.Outcome);
        Assert.True(harness.Local[PosSettingKeys.ManualCard]);
        Assert.True((await harness.Store.LoadAsync()).HasSnapshot);
    }

    [Fact]
    public async Task Failed_ack_is_retried_on_the_next_round()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "global"));
        harness.Api.OnAck = _ => throw new CatalogApiException("down", HttpStatusCode.ServiceUnavailable);

        var first = await harness.Service.SyncNowAsync();

        // 设置已经生效，只是回执没送到：下一轮补发。
        Assert.Equal(PosSettingsSyncOutcome.Applied, first.Outcome);
        Assert.Equal("ack pending", first.Detail);
        Assert.True(harness.Local[PosSettingKeys.ManualCard]);
        Assert.Null((await harness.Store.LoadAsync()).AckedHash);

        harness.Api.OnAck = null;
        var second = await harness.Service.SyncNowAsync();

        Assert.Equal(PosSettingsSyncOutcome.UpToDate, second.Outcome);
        Assert.Null(second.Detail);
        Assert.Equal(harness.Api.Hash, (await harness.Store.LoadAsync()).AckedHash);
        Assert.Single(harness.Api.Acks);
    }

    [Fact]
    public async Task Rejected_ack_with_400_is_not_retried_in_this_process()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "global"));
        harness.Api.OnAck = _ => throw new CatalogApiException("bad", HttpStatusCode.BadRequest);

        await harness.Service.SyncNowAsync();
        var attemptsAfterReject = harness.Api.AckAttempts;
        await harness.Service.SyncNowAsync();
        await harness.Service.SyncNowAsync();

        // 服务端不认这份回执：本进程内不再对同一份重试，避免每分钟死循环。
        Assert.Equal(1, attemptsAfterReject);
        Assert.Equal(attemptsAfterReject, harness.Api.AckAttempts);
    }

    [Fact]
    public async Task Store_change_discards_the_old_snapshot()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "store"));
        await harness.Service.SyncNowAsync();
        Assert.True(harness.Service.Current.Find(PosSettingKeys.ManualCard)!.IsManaged);

        harness.Auth.Set(new DeviceAuthorizationContext("DEV1", "S002", "HW", "AUTH"));
        harness.Api.OnSync = _ =>
            Task.FromResult(new PosSettingsSyncDto(true, "newhash", new Dictionary<string, PosSettingValueDto>()));

        await harness.Service.SyncNowAsync();

        // 换店后不能带着旧店的指纹去问（断言放在服务外面：服务会把回调里抛出的异常当成同步失败吞掉）。
        Assert.Null(harness.Api.KnownHashes.Last());

        Assert.False(harness.Service.Current.Find(PosSettingKeys.ManualCard)!.IsManaged);
        Assert.Equal("S002", (await harness.Store.LoadAsync()).StoreCode);
    }

    [Fact]
    public async Task Concurrent_sync_requests_share_one_round()
    {
        using var harness = new Harness();
        var release = new TaskCompletionSource();
        harness.Api.OnSync = async _ =>
        {
            await release.Task;
            return new PosSettingsSyncDto(true, "h1", new Dictionary<string, PosSettingValueDto>());
        };

        var first = harness.Service.SyncNowAsync();
        var second = harness.Service.SyncNowAsync();
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Single(harness.Api.KnownHashes);
    }

    [Fact]
    public async Task Changed_event_fires_on_real_changes_only()
    {
        using var harness = new Harness();
        harness.Api.Server(("payment.manualCard", true, "global"));
        var raised = 0;
        harness.Service.Changed += (_, _) => raised++;

        await harness.Service.RefreshAsync();
        var afterFirstRefresh = raised;
        await harness.Service.RefreshAsync();
        // 内容没变的重复刷新不通知：界面一刷新就重读、重读又触发通知会形成死循环。
        Assert.Equal(afterFirstRefresh, raised);

        await harness.Service.SyncNowAsync();
        Assert.True(raised > afterFirstRefresh);

        var beforeOffline = raised;
        harness.Service.ReportConnectivity(false);
        Assert.True(raised > beforeOffline);
        var afterOffline = raised;
        harness.Service.ReportConnectivity(false);
        Assert.Equal(afterOffline, raised);
    }

    // ───────────────────────── 测试替身 ─────────────────────────

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Auth.Set(new DeviceAuthorizationContext("DEV1", "S001", "HW", "AUTH"));
            foreach (var definition in PosSettingCatalog.All)
            {
                // 与真实本机缺省一致：自动重启、订单列表、银行收据默认开，其余默认关。
                Local[definition.Key] = definition.Key is PosSettingKeys.AutoRestart
                    or PosSettingKeys.OrderList
                    or PosSettingKeys.BankReceipt;
            }

            Service = NewService();
        }

        public FakeApi Api { get; } = new();

        public FakeLocalStore Store { get; } = new();

        public DeviceAuthorizationState Auth { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        /// <summary>各开关「真正的本机存储」，键 → 当前值。</summary>
        public Dictionary<string, bool> Local { get; } = new(StringComparer.Ordinal);

        public List<string> Writes { get; } = [];

        public HashSet<string> FailWritesFor { get; } = [];

        public PosSettingsService Service { get; private set; }

        /// <summary>模拟重启：同一份本机存储与开关，重新构造一个服务。</summary>
        public PosSettingsService NewService()
        {
            var bindings = PosSettingCatalog.All
                .Select(definition => (IPosSettingBinding)new FakeBinding(definition.Key, this))
                .ToList();
            return new PosSettingsService(Api, Store, bindings, Auth, Audit, new FakeVersion(), Time);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeBinding(string key, Harness harness) : IPosSettingBinding
    {
        public string Key { get; } = key;

        public Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(harness.Local[Key]);

        public Task WriteAsync(bool value, CancellationToken cancellationToken = default)
        {
            if (harness.FailWritesFor.Contains(Key))
            {
                throw new InvalidOperationException("disk full");
            }

            harness.Writes.Add(Key);
            harness.Local[Key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeApi : IPosSettingsApiClient
    {
        private int _version;

        public List<string?> KnownHashes { get; } = [];

        public List<PosSettingsAckRequest> Acks { get; } = [];

        public int AckAttempts { get; private set; }

        public string Hash { get; private set; } = "h0";

        private Dictionary<string, PosSettingValueDto> Settings { get; set; } = [];

        public Func<string?, Task<PosSettingsSyncDto>>? OnSync { get; set; }

        public Func<PosSettingsAckRequest, Task<PosSettingsAckResultDto>>? OnAck { get; set; }

        /// <summary>设定「服务端当前下发的设置」，指纹随内容变化（内容相同则指纹不变）。</summary>
        public void Server(params (string Key, bool Value, string Source)[] settings)
        {
            var next = settings.ToDictionary(
                item => item.Key,
                item => new PosSettingValueDto(item.Value, item.Source),
                StringComparer.Ordinal);
            var same = next.Count == Settings.Count
                && next.All(pair => Settings.TryGetValue(pair.Key, out var old) && old == pair.Value);
            if (!same)
            {
                _version++;
                Hash = $"h{_version}";
                Settings = next;
            }

            OnSync = known => Task.FromResult(
                string.Equals(known, Hash, StringComparison.Ordinal)
                    ? new PosSettingsSyncDto(false, Hash, null)
                    : new PosSettingsSyncDto(true, Hash, Settings));
        }

        public Task<PosSettingsSyncDto> GetSyncAsync(string? knownHash, CancellationToken cancellationToken = default)
        {
            KnownHashes.Add(knownHash);
            return (OnSync ?? (_ => Task.FromResult(new PosSettingsSyncDto(false, Hash, null))))(knownHash);
        }

        public async Task<PosSettingsAckResultDto> AckAsync(
            PosSettingsAckRequest request,
            CancellationToken cancellationToken = default)
        {
            AckAttempts++;
            if (OnAck is not null)
            {
                await OnAck(request);
            }

            Acks.Add(request);
            return new PosSettingsAckResultDto(request.Hash);
        }
    }

    private sealed class FakeLocalStore : IPosSettingsLocalStore
    {
        private PosSettingsLocalState _state = PosSettingsLocalState.Empty;

        public Task<PosSettingsLocalState> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_state);

        public Task SaveAsync(PosSettingsLocalState state, CancellationToken cancellationToken = default)
        {
            _state = state;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAudit : IOperationAuditLogger
    {
        public List<OperationAuditEventDto> Events { get; } = [];

        public void Record(OperationAuditEventDto auditEvent) => Events.Add(auditEvent);
    }

    private sealed class FakeVersion : IAppVersionProvider
    {
        public string CurrentVersion => "1.0.test";
    }
}
