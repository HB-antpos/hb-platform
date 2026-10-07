using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.HeldOrders;
using LocalClaimStatus = Hbpos.Client.Wpf.Models.SharedHeldOrderClaimStatus;
using ServerClaimStatus = Hbpos.Contracts.HeldOrders.SharedHeldOrderClaimStatus;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 取单协调错误。Code 稳定值：CONFLICT/FENCE_CONFLICT/ACTIVATE_CONFLICT/
/// RESTORE_FAILED/NOT_FOUND/INVALID。Message 不含 payload。
/// </summary>
public sealed class SharedHeldOrderCoordinatorException(
    string code,
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
}

public sealed record SharedHeldOrderTakeResult(
    Guid ClaimId,
    Guid HoldGuid,
    bool RestoredToCart);

public sealed record SharedHeldOrderReconcileMismatch(
    Guid ClaimId,
    Guid? HoldGuid,
    string Reason);

public sealed record SharedHeldOrderReconcileResult(
    IReadOnlyList<Guid> RestoredClaimIds,
    IReadOnlyList<Guid> ReconciledPreparedClaimIds,
    IReadOnlyList<SharedHeldOrderReconcileMismatch> Mismatches);

/// <summary>本地 OfflineOrigin 崩溃恢复结果：不访问 API，纯本地 durable 事实。</summary>
public sealed record SharedHeldOrderLocalRecoveryResult(
    IReadOnlyList<Guid> RestoredClaimIds,
    IReadOnlyList<SharedHeldOrderReconcileMismatch> Mismatches);

public interface ISharedHeldOrderCoordinator
{
    /// <summary>
    /// 在线取单：固定顺序 server prepare -> 本地 durable claim/fence -> server activate
    /// -> 本地 Active -> 反向映射恢复购物车。本地 durable 写失败绝不 activate；
    /// cart restore 失败清空 cart 但保留 Active 本地事实，绝不自动 release。
    /// </summary>
    Task<SharedHeldOrderTakeResult> TakeRemoteHoldAsync(
        Guid holdGuid,
        PosSessionState session,
        Guid? claimGuid = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 原设备离线 recall：本地 published/待发布副本可用时创建 OfflineOrigin durable
    /// claim/fence 后恢复购物车，不访问 API；付款绑定由后续 lane 处理。
    /// </summary>
    Task<SharedHeldOrderTakeResult> RecallLocalPublicationAsync(
        Guid localHoldGuid,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// claims/mine 与本地 FindRecoverableClaims 对账：同 claim facts 一致才恢复；
    /// server Active 不自动释放；server Prepared 只按幂等状态保存/等待；终态本地不能重开。
    /// </summary>
    Task<SharedHeldOrderReconcileResult> ReconcileClaimsAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 本地 OfflineOrigin 崩溃恢复：Prepared 本地补激活、Active 恢复购物车，
    /// 全程不访问 API（API 离线不能阻止登录/启动）；RemoteClaim 一律不触碰，
    /// 交给 claims/mine 幂等对账；Active 绝不自动释放。
    /// </summary>
    Task<SharedHeldOrderLocalRecoveryResult> RecoverLocalClaimsAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 当前收银员取消已恢复购物车：RemoteClaim 先由 owner 调服务端 release，
    /// OfflineOrigin 只关闭本地 claim；随后推进本地 Released 并精确清理同一 cart binding。
    /// 任一步失败都保留尚未清理的购物车，禁止直接遗弃 Active claim。
    /// </summary>
    Task ReleaseActiveClaimAsync(
        Guid claimGuid,
        PosSessionState session,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 主管强制释放 durable 方法：先等服务端 force-release 成功，再按 claimGuid
    /// 精确匹配清理 Active 购物车 binding，并把本地 Prepared/Active claim（含已绑定
    /// 订单）推进到 Released；服务端失败或本地清理失败都保留可重试状态。
    /// </summary>
    Task ForceReleaseAsync(
        Guid holdGuid,
        Guid claimGuid,
        string reason,
        PosSessionState session,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// WPF 取单协调器：所有 payload 明文只经过内存（仓库层负责密文落库），
/// 任何异常路径不把 payload 写入日志/异常消息。
/// </summary>
public sealed class SharedHeldOrderCoordinator(
    ISharedHeldOrderApiClient apiClient,
    ISharedHeldOrderRepository repository,
    ISharedHeldOrderReverseMapper reverseMapper,
    PosCartService cart,
    ISharedHeldOrderPublicationGate publicationGate,
    TimeProvider? timeProvider = null) : ISharedHeldOrderCoordinator
{
    private const int PreparedFallbackTtlSeconds = 120;
    private static readonly ISharedHeldOrderCanonicalSerializer CanonicalSerializer =
        new SharedHeldOrderCanonicalJsonSerializer();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public async Task<SharedHeldOrderTakeResult> TakeRemoteHoldAsync(
        Guid holdGuid,
        PosSessionState session,
        Guid? claimGuid = null,
        CancellationToken cancellationToken = default)
    {
        using var mutationLease = await AcquireMutationAsync(cancellationToken);
        EnsureCartEmpty();
        // 下一次 prepare 前先检查本地 durable fence。stale Prepared 仅在
        // claims/mine 不再返回 blocking claim 时幂等推进 Released；服务端仍返回
        // Prepared/Active 都必须保留。任何 open fence 未清空都在 prepare 前停止，
        // 避免创建未跟踪的服务端 claim。
        var stalePreparedClaimIds = await FindStalePreparedRemoteClaimIdsAsync(
            session,
            cancellationToken);
        if (stalePreparedClaimIds.Count > 0)
        {
            var serverClaims = await apiClient.ClaimsMineAsync(cancellationToken);
            var serverBlockingClaimIds = serverClaims
                .Where(claim => claim.Status is ServerClaimStatus.Prepared or ServerClaimStatus.Active)
                .Select(claim => claim.ClaimGuid)
                .ToHashSet();
            await ExpireStalePreparedRemoteClaimsAsync(
                stalePreparedClaimIds,
                serverBlockingClaimIds,
                cancellationToken);
        }

        var remainingOpenClaims = await repository.FindRecoverableClaimsAsync(
            session.StoreCode,
            session.DeviceCode,
            cancellationToken);
        if (remainingOpenClaims.Count > 0)
        {
            throw new SharedHeldOrderCoordinatorException(
                "FENCE_CONFLICT",
                "本机已有未完成的共享挂单 claim，请先恢复或释放后再取单。");
        }

        var nowIso = FormatIso(_timeProvider.GetUtcNow());
        var claimId = claimGuid ?? Guid.NewGuid();
        var prepareKey = $"wpf-prepare:{claimId:D}";
        var prepare = await apiClient.PrepareAsync(
            holdGuid,
            new SharedHeldOrderClaimPrepareRequest(claimId, prepareKey),
            cancellationToken);
        ValidatePrepareResponse(prepare, holdGuid, claimId, session);
        if (prepare.Status is not (ServerClaimStatus.Prepared or ServerClaimStatus.Active))
        {
            // 服务端终态（Released/Completed/Superseded）：本地绝不新建 claim。
            throw new SharedHeldOrderCoordinatorException(
                "CONFLICT",
                "共享挂单 claim 已处于终态，拒绝继续取单。");
        }

        var payload = ToCanonical(prepare.Payload);
        var draft = new SharedHeldOrderClaimDraft(
            claimId,
            holdGuid,
            session.StoreCode,
            session.DeviceCode,
            SharedHeldOrderClaimSource.RemoteClaim,
            prepareKey,
            payload,
            nowIso,
            prepare.ExpiresAtUtc is { } expiresAt
                ? FormatIso(expiresAt)
                : FormatIso(_timeProvider.GetUtcNow().AddSeconds(PreparedFallbackTtlSeconds)));
        // 本地 durable fence 必须先落库；写失败/输家绝不 activate。
        if (!await repository.TrySavePreparedClaimAsync(draft, cancellationToken))
        {
            throw new SharedHeldOrderCoordinatorException(
                "FENCE_CONFLICT",
                "本机已有未完成的共享挂单 claim，拒绝并发取单。");
        }

        // activate 未知结果（超时/网络）时保持本地 Prepared，交给 claims/mine 对账。
        var activateKey = $"wpf-activate:{claimId:D}";
        var activated = await apiClient.ActivateAsync(holdGuid, claimId, cancellationToken);
        ValidateActivateResponse(activated, holdGuid, claimId, session);
        if (!await repository.TryActivateClaimAsync(
                claimId,
                prepareKey,
                activateKey,
                serverRevision: activated.Revision,
                FormatIso(_timeProvider.GetUtcNow()),
                cancellationToken))
        {
            throw new SharedHeldOrderCoordinatorException(
                "ACTIVATE_CONFLICT",
                "本地 claim 激活失败，已保留本地事实等待对账。");
        }

        Restore(payload, session.StoreCode, claimId);
        return new SharedHeldOrderTakeResult(claimId, holdGuid, RestoredToCart: true);
    }

    public Task<SharedHeldOrderTakeResult> RecallLocalPublicationAsync(
        Guid localHoldGuid,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        // 本机离线取回与发布 worker 共用互斥门：必须先等已经发出的 publish
        // 完整收口，避免 OfflineOrigin 成交先到服务端、迟到 publish 再复活挂单。
        return publicationGate.RunExclusiveAsync(
            () => RecallLocalPublicationCoreAsync(localHoldGuid, session, cancellationToken),
            cancellationToken);
    }

    private async Task<SharedHeldOrderTakeResult> RecallLocalPublicationCoreAsync(
        Guid localHoldGuid,
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        using var mutationLease = await AcquireMutationAsync(cancellationToken);
        EnsureCartEmpty();
        var nowIso = FormatIso(_timeProvider.GetUtcNow());
        var publication = await repository.GetPublicationAsync(localHoldGuid, cancellationToken);
        if (publication is null
            || publication.Status is not (
                SharedHeldOrderPublicationStatus.PendingPublish
                or SharedHeldOrderPublicationStatus.Published))
        {
            throw new SharedHeldOrderCoordinatorException(
                "NOT_FOUND",
                "本地没有可恢复的共享挂单副本。");
        }

        var payload = await repository.GetPublicationPayloadAsync(localHoldGuid, cancellationToken)
            ?? throw new SharedHeldOrderCoordinatorException(
                "NOT_FOUND",
                "本地共享挂单副本缺少 payload。");

        var claimId = Guid.NewGuid();
        var prepareKey = $"wpf-offline:{localHoldGuid:D}";
        var draft = new SharedHeldOrderClaimDraft(
            claimId,
            localHoldGuid,
            session.StoreCode,
            session.DeviceCode,
            SharedHeldOrderClaimSource.OfflineOrigin,
            prepareKey,
            payload,
            nowIso,
            ExpiresAtIso: null);
        if (!await repository.TrySavePreparedClaimAsync(draft, cancellationToken))
        {
            throw new SharedHeldOrderCoordinatorException(
                "FENCE_CONFLICT",
                "本机已有未完成的共享挂单 claim，拒绝离线召回。");
        }

        // 离线激活：无服务端 revision，本地 Active 即事实（不访问 API）。
        if (!await repository.TryActivateClaimAsync(
                claimId,
                prepareKey,
                $"wpf-offline-activate:{claimId:D}",
                serverRevision: null,
                nowIso,
                cancellationToken))
        {
            throw new SharedHeldOrderCoordinatorException(
                "ACTIVATE_CONFLICT",
                "离线 claim 本地激活失败。");
        }

        Restore(payload, session.StoreCode, claimId);
        return new SharedHeldOrderTakeResult(claimId, localHoldGuid, RestoredToCart: true);
    }

    public async Task<SharedHeldOrderReconcileResult> ReconcileClaimsAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        using var mutationLease = await AcquireMutationAsync(cancellationToken);
        var nowIso = FormatIso(_timeProvider.GetUtcNow());
        var localClaims = await repository.FindRecoverableClaimsAsync(
            session.StoreCode,
            session.DeviceCode,
            cancellationToken);
        var serverClaims = await apiClient.ClaimsMineAsync(cancellationToken);
        // reconcile 前：只有 claims/mine 不再返回 blocking claim 时，才把可信过期的
        // 本地 RemoteClaim Prepared 幂等推进 Released；Prepared/Active 均为服务端
        // 权威阻塞事实，后者交给下方补激活路径。
        var serverBlockingClaimIds = serverClaims
            .Where(claim => claim.Status is ServerClaimStatus.Prepared or ServerClaimStatus.Active)
            .Select(claim => claim.ClaimGuid)
            .ToHashSet();
        var stalePreparedClaimIds = await FindStalePreparedRemoteClaimIdsAsync(
            session,
            cancellationToken);
        var expiredPreparedClaimIds = await ExpireStalePreparedRemoteClaimsAsync(
            stalePreparedClaimIds,
            serverBlockingClaimIds,
            cancellationToken);
        var duplicateServerClaimIds = serverClaims
            .GroupBy(claim => claim.ClaimGuid)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        var serverClaimIds = serverClaims
            .Select(claim => claim.ClaimGuid)
            .ToHashSet();
        var restored = new List<Guid>();
        var reconciledPrepared = new List<Guid>();
        var mismatches = new List<SharedHeldOrderReconcileMismatch>();

        foreach (var server in serverClaims)
        {
            if (duplicateServerClaimIds.Contains(server.ClaimGuid))
            {
                if (!mismatches.Any(item => item.ClaimId == server.ClaimGuid))
                {
                    mismatches.Add(new SharedHeldOrderReconcileMismatch(
                        server.ClaimGuid,
                        server.HoldGuid,
                        "服务端返回重复 claim 标识，拒绝恢复。"));
                }

                continue;
            }

            if (!RecoveryClaimMatchesSession(server, session))
            {
                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    server.ClaimGuid,
                    server.HoldGuid,
                    "服务端 claim 与当前 store/device/cashier 不一致，拒绝落库或恢复。"));
                continue;
            }

            SharedHeldOrderCanonicalPayload serverPayload;
            try
            {
                serverPayload = ToCanonical(server.Payload);
            }
            catch (SharedHeldOrderCoordinatorException)
            {
                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    server.ClaimGuid,
                    server.HoldGuid,
                    "服务端 claim payload 无效，拒绝落库或恢复。"));
                continue;
            }

            var local = localClaims.FirstOrDefault(claim => claim.ClaimId == server.ClaimGuid);
            if (expiredPreparedClaimIds.Contains(server.ClaimGuid))
            {
                // Prepared/Active 已由 serverBlockingClaimIds 保护，不会走到这里。
                // 若未来接口显式返回终态，本地 Released 与服务端非阻塞事实一致。
                if (server.Status is ServerClaimStatus.Released
                    or ServerClaimStatus.Completed
                    or ServerClaimStatus.Superseded)
                {
                    continue;
                }

                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    server.ClaimGuid,
                    server.HoldGuid,
                    "服务端 claim 与本地已过期 Released 终态矛盾，保留本地事实。"));
                continue;
            }

            if (local is not null && !LocalAndServerFactsMatch(local, server, serverPayload, session))
            {
                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    server.ClaimGuid,
                    server.HoldGuid,
                    "本地与服务端 claim facts 不一致，保留本地事实。"));
                continue;
            }

            if (server.Status == ServerClaimStatus.Prepared)
            {
                if (local is not null)
                {
                    if (local.Status == LocalClaimStatus.Prepared)
                    {
                        // 同 facts：只按幂等状态保存/等待，绝不激活或恢复。
                        reconciledPrepared.Add(server.ClaimGuid);
                    }
                    else
                    {
                        mismatches.Add(new SharedHeldOrderReconcileMismatch(
                            server.ClaimGuid,
                            server.HoldGuid,
                            "本地 claim facts 与服务端 Prepared 不一致，保留本地事实。"));
                    }

                    continue;
                }

                // 服务端 Prepared 且本地无记录：幂等保存为 RemoteClaim 并等待；
                // 不 restore、不 activate。
                if (await repository.TrySavePreparedClaimAsync(
                        new SharedHeldOrderClaimDraft(
                            server.ClaimGuid,
                            server.HoldGuid,
                            session.StoreCode,
                            session.DeviceCode,
                            SharedHeldOrderClaimSource.RemoteClaim,
                            $"reconcile:{server.ClaimGuid:D}",
                            serverPayload,
                            nowIso,
                            server.ExpiresAtUtc is { } expiresAt
                                ? FormatIso(expiresAt)
                                : FormatIso(_timeProvider.GetUtcNow().AddSeconds(PreparedFallbackTtlSeconds))),
                        cancellationToken))
                {
                    reconciledPrepared.Add(server.ClaimGuid);
                }
                else
                {
                    // 崩溃重放：先前 reconcile 已按可信 ExpiresAt 把同一 claim 推进本地
                    // Released（wpf-expired-prepare 键），重放保存必然失败；此时服务端
                    // 仍 Prepared 属时钟偏差，视为已调和，不重开终态。
                    var terminal = await repository.GetClaimAsync(
                        server.ClaimGuid,
                        cancellationToken);
                    if (terminal is not null
                        && terminal.Status == LocalClaimStatus.Released
                        && terminal.HoldGuid == server.HoldGuid
                        && terminal.ReleaseIdempotencyKey?.StartsWith(
                            "wpf-expired-prepare:",
                            StringComparison.Ordinal) == true)
                    {
                        continue;
                    }

                    mismatches.Add(new SharedHeldOrderReconcileMismatch(
                        server.ClaimGuid,
                        server.HoldGuid,
                        "本机 open fence 被其他 claim 占用，无法保存服务端 Prepared。"));
                }

                continue;
            }

            if (server.Status == ServerClaimStatus.Active)
            {
                if (local is null)
                {
                    // 服务端 Active 但本地无 durable 事实：同 facts 不成立，fail-closed，
                    // 绝不自动 release，也绝不伪造本地 Active。
                    mismatches.Add(new SharedHeldOrderReconcileMismatch(
                        server.ClaimGuid,
                        server.HoldGuid,
                        "服务端 Active claim 缺少本地 durable 事实，拒绝恢复。"));
                    continue;
                }

                if (local.Status == LocalClaimStatus.Prepared)
                {
                    // 崩溃窗口：服务端已 Active，本地仍 Prepared —— 补本地激活后恢复。
                    if (!await repository.TryActivateClaimAsync(
                            server.ClaimGuid,
                            local.PrepareIdempotencyKey,
                            $"reconcile-activate:{server.ClaimGuid:D}",
                            server.Revision,
                            nowIso,
                            cancellationToken))
                    {
                        mismatches.Add(new SharedHeldOrderReconcileMismatch(
                            server.ClaimGuid,
                            server.HoldGuid,
                            "本地 Prepared 无法补激活，保留事实等待重试。"));
                        continue;
                    }
                }
                else if (local.Status != LocalClaimStatus.Active)
                {
                    mismatches.Add(new SharedHeldOrderReconcileMismatch(
                        server.ClaimGuid,
                        server.HoldGuid,
                        "本地 claim 状态与服务端 Active 不一致，保留本地事实。"));
                    continue;
                }

                // 同 facts（claim/hold/status 一致）：从本地 durable payload 恢复购物车。
                if (cart.IsEmpty)
                {
                    Restore(local.Payload, session.StoreCode, server.ClaimGuid);
                    restored.Add(server.ClaimGuid);
                }
                else
                {
                    mismatches.Add(new SharedHeldOrderReconcileMismatch(
                        server.ClaimGuid,
                        server.HoldGuid,
                        "购物车非空，跳过恢复；Active 本地事实保留。"));
                }

                continue;
            }

            // 服务端终态（Released/Completed/Superseded）：本地 open claim 保留，
            // 终态本地不能重开，也不自动 release。
            if (local is not null)
            {
                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    server.ClaimGuid,
                    server.HoldGuid,
                    "服务端 claim 已终态而本地仍 open，保留本地事实。"));
            }
        }

        // 本地有但服务端缺失的 open claim：保留本地事实，fail-closed 不恢复。
        foreach (var local in localClaims)
        {
            if (expiredPreparedClaimIds.Contains(local.ClaimId))
            {
                // 本地已按可信 ExpiresAt 推进 Released，服务端缺失属正常过期结果。
                continue;
            }

            if (local.Source == SharedHeldOrderClaimSource.RemoteClaim
                && !serverClaimIds.Contains(local.ClaimId))
            {
                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    local.ClaimId,
                    local.HoldGuid,
                    "服务端没有对应 claim，本地事实保留。"));
            }
        }

        if (mismatches.Count > 0)
        {
            // 对账发现无法恢复的 claim：本地/服务端事实都保留，需要人工核对，每条单独留痕（登录时一次，不会刷屏）。
            foreach (var mismatch in mismatches)
            {
                ConsoleLog.WriteWarning(
                    "HeldOrder",
                    $"shared held order reconcile mismatch claimGuid={mismatch.ClaimId:D} holdGuid={mismatch.HoldGuid?.ToString("D") ?? "<none>"} " +
                    $"reason={mismatch.Reason}",
                    new ApplicationLogContext(
                        TraceId: mismatch.ClaimId.ToString("D"),
                        Properties: new Dictionary<string, object?>
                        {
                            ["storeCode"] = session.StoreCode,
                            ["deviceCode"] = session.DeviceCode,
                            ["operation"] = "reconcile"
                        }));
            }
        }

        return new SharedHeldOrderReconcileResult(restored, reconciledPrepared, mismatches);
    }

    public async Task<SharedHeldOrderLocalRecoveryResult> RecoverLocalClaimsAsync(
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        using var mutationLease = await AcquireMutationAsync(cancellationToken);
        var nowIso = FormatIso(_timeProvider.GetUtcNow());
        var localClaims = await repository.FindRecoverableClaimsAsync(
            session.StoreCode,
            session.DeviceCode,
            cancellationToken);
        var restored = new List<Guid>();
        var mismatches = new List<SharedHeldOrderReconcileMismatch>();

        foreach (var local in localClaims)
        {
            // 远端 claim 一律交给 claims/mine 幂等对账，本地恢复不触碰、不伪造事实。
            if (local.Source != SharedHeldOrderClaimSource.OfflineOrigin)
            {
                continue;
            }

            if (local.Status == LocalClaimStatus.Prepared)
            {
                // 崩溃窗口：离线 claim 已落 fence 未激活；本地补激活（无服务端 revision）。
                if (!await repository.TryActivateClaimAsync(
                        local.ClaimId,
                        local.PrepareIdempotencyKey,
                        $"wpf-offline-activate:{local.ClaimId:D}",
                        serverRevision: null,
                        nowIso,
                        cancellationToken))
                {
                    mismatches.Add(new SharedHeldOrderReconcileMismatch(
                        local.ClaimId,
                        local.HoldGuid,
                        "本地 OfflineOrigin Prepared 无法补激活，保留事实等待重试。"));
                    continue;
                }
            }
            else if (local.Status != LocalClaimStatus.Active)
            {
                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    local.ClaimId,
                    local.HoldGuid,
                    "本地 OfflineOrigin claim 已终态，跳过恢复。"));
                continue;
            }

            // 已绑定订单的 Active（支付/上传已完成）绝不回灌购物车。
            if (local.BoundOrderGuid is not null)
            {
                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    local.ClaimId,
                    local.HoldGuid,
                    "本地 OfflineOrigin claim 已绑定订单，跳过恢复。"));
                continue;
            }

            if (cart.IsEmpty)
            {
                Restore(local.Payload, session.StoreCode, local.ClaimId);
                restored.Add(local.ClaimId);
            }
            else
            {
                mismatches.Add(new SharedHeldOrderReconcileMismatch(
                    local.ClaimId,
                    local.HoldGuid,
                    "购物车非空，跳过恢复；Active 本地事实保留。"));
            }
        }

        return new SharedHeldOrderLocalRecoveryResult(restored, mismatches);
    }

    public async Task ReleaseActiveClaimAsync(
        Guid claimGuid,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        using var mutationLease = await AcquireMutationAsync(cancellationToken);
        var local = await repository.GetClaimAsync(claimGuid, cancellationToken);
        var releaseKey = $"wpf-release:{claimGuid:D}";
        if (local is null)
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "本机没有与购物车匹配的 claim，拒绝普通释放。");
        }

        if (!SameScopeValue(local.StoreCode, session.StoreCode)
            || !SameScopeValue(local.DeviceCode, session.DeviceCode))
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "本地 claim 与本机 store/device 不一致，拒绝普通释放。");
        }

        if (local.Status == LocalClaimStatus.Released
            && string.Equals(local.ReleaseIdempotencyKey, releaseKey, StringComparison.Ordinal))
        {
            // 崩溃窗口：本地 claim 已 Released、购物车尚未来得及清理；重试只做精确收尾。
            if (!cart.ClearSharedHeldOrderClaim(claimGuid))
            {
                throw new SharedHeldOrderCoordinatorException(
                    "FENCE_CONFLICT",
                    "本地 claim 已释放，但购物车 binding 未能精确清理。");
            }

            return;
        }

        if (local.Status != LocalClaimStatus.Active)
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "本机没有与购物车匹配的 Active claim，拒绝普通释放。");
        }

        var cartSnapshot = cart.CreateSnapshot();
        if (cartSnapshot.SharedHeldOrderClaimId != claimGuid)
        {
            throw new SharedHeldOrderCoordinatorException(
                "FENCE_CONFLICT",
                "当前购物车未绑定该 Active claim，拒绝普通释放。");
        }

        if (local.Source == SharedHeldOrderClaimSource.RemoteClaim)
        {
            // 服务端成功是本地 RemoteClaim 清理的前提；网络失败时购物车与本地 fence 原样保留。
            var released = await apiClient.ReleaseAsync(
                local.HoldGuid,
                claimGuid,
                cancellationToken);
            ValidateOwnerReleaseResponse(released, local.HoldGuid, claimGuid, session);
        }

        if (!await repository.TryReleaseClaimAsync(
                claimGuid,
                releaseKey,
                LocalClaimStatus.Active,
                FormatIso(_timeProvider.GetUtcNow()),
                cancellationToken))
        {
            throw new SharedHeldOrderCoordinatorException(
                "RELEASE_FAILED",
                "共享挂单服务端已释放，但本地 claim 未能推进 Released；购物车保留以便重试。");
        }

        if (!cart.ClearSharedHeldOrderClaim(claimGuid))
        {
            throw new SharedHeldOrderCoordinatorException(
                "FENCE_CONFLICT",
                "本地 claim 已释放，但购物车 binding 未能精确清理。");
        }
    }

    public async Task ForceReleaseAsync(
        Guid holdGuid,
        Guid claimGuid,
        string reason,
        PosSessionState session,
        CancellationToken cancellationToken = default)
    {
        using var mutationLease = await AcquireMutationAsync(cancellationToken);
        var normalizedReason = reason?.Trim() ?? string.Empty;
        if (normalizedReason.Length == 0)
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "强制释放原因不能为空。");
        }

        if (normalizedReason.Length > 500)
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "强制释放原因不能超过 500 个字符。");
        }

        var local = await repository.GetClaimAsync(claimGuid, cancellationToken);
        if (local is null
            || local.HoldGuid != holdGuid
            || local.Source != SharedHeldOrderClaimSource.RemoteClaim
            || local.Status is not (LocalClaimStatus.Prepared or LocalClaimStatus.Active))
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "本机没有与请求匹配的 open RemoteClaim，拒绝强制释放。");
        }

        if (!SameScopeValue(local.StoreCode, session.StoreCode)
            || !SameScopeValue(local.DeviceCode, session.DeviceCode))
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "本地 claim 与本机 store/device 不一致，拒绝强制释放。");
        }

        // 服务端 force-release 成功是本地清理的前提；失败时本地 claim/fence/cart 全部保留。
        var released = await apiClient.ForceReleaseAsync(
            holdGuid,
            claimGuid,
            new SharedHeldOrderForceReleaseRequest(normalizedReason),
            cancellationToken);
        ValidateForceReleaseResponse(released, holdGuid, claimGuid, session);

        // 仅 Active 且购物车 binding 精确匹配 claimGuid 时才整单清空；
        // 已清空且无 binding 视为崩溃重试；其它购物车保留且本地 claim 不推进。
        if (local.Status == LocalClaimStatus.Active)
        {
            var cartSnapshot = cart.CreateSnapshot();
            if (cartSnapshot.SharedHeldOrderClaimId == claimGuid)
            {
                if (!cart.ClearSharedHeldOrderClaim(claimGuid))
                {
                    throw new SharedHeldOrderCoordinatorException(
                        "FENCE_CONFLICT",
                        "服务端已释放，但本地 claim 购物车未能精确清理；保留事实等待重试。");
                }
            }
            else if (cartSnapshot.SharedHeldOrderClaimId is not null || !cart.IsEmpty)
            {
                throw new SharedHeldOrderCoordinatorException(
                    "FENCE_CONFLICT",
                    "服务端已释放，但当前购物车属于另一项交易；保留本地 claim 等待重试。");
            }
        }

        var releaseKey = $"wpf-force-release:{claimGuid:D}";
        if (!await repository.TryForceReleaseClaimAsync(
                claimGuid,
                releaseKey,
                local.Status,
                FormatIso(_timeProvider.GetUtcNow()),
                cancellationToken))
        {
            throw new SharedHeldOrderCoordinatorException(
                "RELEASE_FAILED",
                "服务端已强制释放，但本地 claim 清理失败；保留本地事实等待重试。");
        }
    }

    private async Task<IDisposable> AcquireMutationAsync(CancellationToken cancellationToken)
    {
        // Coordinator 在生产 DI 中为 singleton；单进程内所有取单、恢复与释放共享
        // 同一门闩。立即拒绝并发 mutation，确保第二条链路不会越过本地 fence 预检
        // 创建无法追踪的服务端 Prepared claim。
        if (!await _mutationGate.WaitAsync(0, cancellationToken))
        {
            throw new SharedHeldOrderCoordinatorException(
                "FENCE_CONFLICT",
                "另一项共享挂单操作正在进行中，请稍后重试。");
        }

        return new MutationLease(_mutationGate);
    }

    private sealed class MutationLease(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.Release();
            }
        }
    }

    private static void ValidateOwnerReleaseResponse(
        SharedHeldOrderClaimDto response,
        Guid holdGuid,
        Guid claimGuid,
        PosSessionState session)
    {
        if (response.HoldGuid != holdGuid
            || response.ClaimGuid != claimGuid
            || response.Status != ServerClaimStatus.Released
            || response.ForceReleased
            || response.Revision < 0
            || !SameScopeValue(response.StoreCode, session.StoreCode)
            || !SameScopeValue(response.ClaimantDeviceCode, session.DeviceCode))
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "普通释放响应与本地 claim facts 不一致，拒绝清理本地状态。");
        }
    }

    private static void ValidateForceReleaseResponse(
        SharedHeldOrderClaimDto response,
        Guid holdGuid,
        Guid claimGuid,
        PosSessionState session)
    {
        if (response.HoldGuid != holdGuid
            || response.ClaimGuid != claimGuid
            || response.Status != ServerClaimStatus.Released
            || !response.ForceReleased
            || response.Revision < 0
            || !SameScopeValue(response.StoreCode, session.StoreCode)
            || !SameScopeValue(response.ClaimantDeviceCode, session.DeviceCode))
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "服务端 force-release 响应与本机 claim 不一致，拒绝推进本地释放。");
        }
    }

    private static void ValidatePrepareResponse(
        SharedHeldOrderClaimPrepareResponse response,
        Guid holdGuid,
        Guid claimGuid,
        PosSessionState session)
    {
        if (response.HoldGuid != holdGuid
            || response.ClaimGuid != claimGuid
            || response.Revision < 0
            || !SameScopeValue(response.ClaimantDeviceCode, session.DeviceCode)
            || !SameScopeValue(response.ClaimantCashierId, session.CashierId))
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "共享挂单 prepare 响应与请求身份或作用域不一致，拒绝落库。");
        }
    }

    private static void ValidateActivateResponse(
        SharedHeldOrderClaimDto response,
        Guid holdGuid,
        Guid claimGuid,
        PosSessionState session)
    {
        if (response.HoldGuid != holdGuid
            || response.ClaimGuid != claimGuid
            || response.Status != ServerClaimStatus.Active
            || response.Revision < 0
            || !SameScopeValue(response.StoreCode, session.StoreCode)
            || !SameScopeValue(response.ClaimantDeviceCode, session.DeviceCode)
            || !SameScopeValue(response.ClaimantCashierId, session.CashierId))
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "共享挂单 activate 响应与请求身份、状态或作用域不一致，保留本地 Prepared。");
        }
    }

    private static bool RecoveryClaimMatchesSession(
        SharedHeldOrderRecoveryClaimDto server,
        PosSessionState session)
    {
        return server.ClaimGuid != Guid.Empty
            && server.HoldGuid != Guid.Empty
            && server.Revision >= 0
            && server.Status is ServerClaimStatus.Prepared or ServerClaimStatus.Active
            && SameScopeValue(server.StoreCode, session.StoreCode)
            && SameScopeValue(server.ClaimantDeviceCode, session.DeviceCode);
    }

    private static bool LocalAndServerFactsMatch(
        SharedHeldOrderClaimRecovery local,
        SharedHeldOrderRecoveryClaimDto server,
        SharedHeldOrderCanonicalPayload serverPayload,
        PosSessionState session)
    {
        if (local.Source != SharedHeldOrderClaimSource.RemoteClaim
            || local.HoldGuid != server.HoldGuid
            || !SameScopeValue(local.StoreCode, session.StoreCode)
            || !SameScopeValue(local.DeviceCode, session.DeviceCode))
        {
            return false;
        }

        try
        {
            return string.Equals(
                CanonicalSerializer.Serialize(local.Payload),
                CanonicalSerializer.Serialize(serverPayload),
                StringComparison.Ordinal);
        }
        catch (SharedHeldOrderCanonicalValidationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 过期 fence 清理：只处理本地 RemoteClaim Prepared 且可信（可解析、非空）
    /// ExpiresAt 已过时的 claim，CAS 推进 Released 并写 release key；返回已推进的
    /// claim id 集合。Active 永不自动过期；OfflineOrigin 不参与；重复调用幂等；
    /// 服务端 Active 的 claim（崩溃窗口）绝不按过期释放。
    /// </summary>
    private async Task<HashSet<Guid>> FindStalePreparedRemoteClaimIdsAsync(
        PosSessionState session,
        CancellationToken cancellationToken)
    {
        var localClaims = await repository.FindRecoverableClaimsAsync(
            session.StoreCode,
            session.DeviceCode,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var stale = new HashSet<Guid>();
        foreach (var claim in localClaims)
        {
            if (IsTrustedExpiredPrepared(claim, now))
            {
                stale.Add(claim.ClaimId);
            }
        }

        return stale;
    }

    private async Task<HashSet<Guid>> ExpireStalePreparedRemoteClaimsAsync(
        IReadOnlySet<Guid> stalePreparedClaimIds,
        IReadOnlySet<Guid> serverBlockingClaimIds,
        CancellationToken cancellationToken)
    {
        var nowIso = FormatIso(_timeProvider.GetUtcNow());
        var expired = new HashSet<Guid>();
        foreach (var claimId in stalePreparedClaimIds)
        {
            if (serverBlockingClaimIds.Contains(claimId))
            {
                continue;
            }

            if (await repository.TryExpirePreparedRemoteClaimAsync(
                    claimId,
                    $"wpf-expired-prepare:{claimId:D}",
                    nowIso,
                    cancellationToken))
            {
                expired.Add(claimId);
            }
        }

        return expired;
    }

    private static bool IsTrustedExpiredPrepared(
        SharedHeldOrderClaimRecovery claim,
        DateTimeOffset now)
    {
        if (claim.Source != SharedHeldOrderClaimSource.RemoteClaim
            || claim.Status != LocalClaimStatus.Prepared
            || string.IsNullOrWhiteSpace(claim.ExpiresAtIso))
        {
            return false;
        }

        return DateTimeOffset.TryParse(
            claim.ExpiresAtIso,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var expiresAt)
            && expiresAt <= now;
    }

    private static bool SameScopeValue(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left)
            && !string.IsNullOrWhiteSpace(right)
            && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>反向映射 + 共享 sale 恢复；失败清空 cart 但保留 Active 本地事实，绝不 release。</summary>
    private void Restore(
        SharedHeldOrderCanonicalPayload payload,
        string storeCode,
        Guid claimId)
    {
        PosCartSnapshot snapshot;
        try
        {
            snapshot = reverseMapper.Map(payload, storeCode);
        }
        catch (Exception exception) when (
            exception is SharedHeldOrderReverseMappingException
                or InvalidOperationException
                or ArgumentException)
        {
            cart.Clear();
            throw new SharedHeldOrderCoordinatorException(
                "RESTORE_FAILED",
                "共享挂单反向映射失败，购物车已清空；Active 本地事实保留。",
                exception);
        }

        try
        {
            cart.RestoreSharedSaleSnapshot(snapshot with { SharedHeldOrderClaimId = claimId });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or ArgumentException)
        {
            cart.Clear();
            throw new SharedHeldOrderCoordinatorException(
                "RESTORE_FAILED",
                "共享挂单恢复购物车失败，购物车已清空；Active 本地事实保留。",
                exception);
        }
    }

    private void EnsureCartEmpty()
    {
        if (!cart.IsEmpty)
        {
            throw new InvalidOperationException("Cart must be empty before taking a shared held order.");
        }
    }

    /// <summary>
    /// 服务端 SharedSaleCartV1 -> canonical（显式字段映射 + 双端校验，
    /// 未知/越界字段 fail-closed；不依赖 JSON 往返，避免 strict 解析器拒绝 null union 字段）。
    /// </summary>
    private static SharedHeldOrderCanonicalPayload ToCanonical(object cart)
    {
        if (cart is null)
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "服务端 claim 响应缺少 payload。");
        }

        try
        {
            return SharedHeldOrderContractMapper.ToCanonical(cart);
        }
        catch (Exception exception) when (
            exception is SharedHeldOrderCanonicalValidationException
                or SharedSaleCartValidationException)
        {
            throw new SharedHeldOrderCoordinatorException(
                "INVALID",
                "服务端 claim payload 无法通过 canonical 校验。",
                exception);
        }
    }

    private static string FormatIso(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
