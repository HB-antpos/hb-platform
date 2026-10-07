using System.Globalization;
using System.IO;
using Hbpos.Client.Wpf.Models;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.HeldOrders;

namespace Hbpos.Client.Wpf.Services;

public sealed record SharedHeldOrderPublicationRunResult(
    int EvaluatedOrders,
    int StagedPendingPublish,
    int Blocked,
    int Published,
    int FailedCapability,
    int FailedPublish);

public interface ISharedHeldOrderPublicationWorker
{
    /// <summary>
    /// 一轮后台处理：只评估本店已显式请求共享的 NeedsEvaluation 挂单（映射 ->
    /// PendingPublish/Blocked），再按退避发布所有到期的 PendingPublish。网络不可用/
    /// disabled 只记退避，绝不删除或改变本地挂单。
    /// </summary>
    Task<SharedHeldOrderPublicationRunResult> RunOnceAsync(
        string storeCode,
        string? deviceCode = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 本地挂单后台 evaluator/publisher：NeedsEvaluation -> PendingPublish（密文原子落库）
/// -> Published（远端 revision/time 原子持久化）；失败按现有 backoff 重试；
/// return/open-item 与无法可靠冻结的促销 fail-closed Blocked 并保留原因。
/// 服务端 feature disabled/网络不可用绝不影响本机挂单。
/// </summary>
public sealed class SharedHeldOrderPublicationWorker(
    ISharedHeldOrderRepository repository,
    ISharedHeldOrderMapper mapper,
    ISharedHeldOrderApiClient apiClient,
    ISharedHeldOrderPublicationGate publicationGate,
    Func<SuspendedOrder, IReadOnlyList<CatalogPromotionRuleDto>?>? frozenPromotionRuleProvider = null,
    TimeProvider? timeProvider = null) : ISharedHeldOrderPublicationWorker
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<SharedHeldOrderPublicationRunResult> RunOnceAsync(
        string storeCode,
        string? deviceCode = null,
        CancellationToken cancellationToken = default)
    {
        // 整轮评估/发布持有同一互斥门；删除先落 Blocked 后，后续轮次不会再选中该挂单。
        return publicationGate.RunExclusiveAsync(
            () => RunOnceCoreAsync(storeCode, deviceCode, cancellationToken),
            cancellationToken);
    }

    private async Task<SharedHeldOrderPublicationRunResult> RunOnceCoreAsync(
        string storeCode,
        string? deviceCode,
        CancellationToken cancellationToken)
    {
        var nowIso = FormatIso(_timeProvider.GetUtcNow());
        var evaluated = 0;
        var staged = 0;
        var blocked = 0;
        var published = 0;
        var failedCapability = 0;
        var failedPublish = 0;

        var legacy = await repository.ListLegacyOrdersNeedingEvaluationAsync(
            storeCode,
            deviceCode,
            cancellationToken);
        foreach (var order in legacy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publication = await repository.GetPublicationAsync(
                order.SuspendedOrderGuid,
                cancellationToken);
            if (publication is null || publication.Status != SharedHeldOrderPublicationStatus.NeedsEvaluation)
            {
                // 并发已推进或尚未显式请求共享，跳过本轮；未请求的挂单绝不自动发布。
                continue;
            }

            evaluated++;
            SharedHeldOrderMappingResult mapping;
            try
            {
                mapping = mapper.Map(
                    order,
                    frozenPromotionRuleProvider?.Invoke(order),
                    revision: 1);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or OverflowException
                    or SharedHeldOrderCanonicalValidationException)
            {
                // 单条旧挂单损坏不能中断整个批次；保留本地并写稳定阻断原因，
                // 错误详情不包含商品或 canonical payload。
                if (await repository.TryBlockPublicationAsync(
                        order.SuspendedOrderGuid,
                        publication.Revision,
                        SharedHeldOrderMappingReasons.InvalidSnapshot,
                        "本地挂单快照无法无损转换为共享格式。",
                        nowIso,
                        cancellationToken))
                {
                    blocked++;
                }

                continue;
            }
            if (mapping.IsBlocked)
            {
                // return/open-item/无法可靠冻结的促销：fail-closed Blocked 并保留原因，
                // 本地挂单原样保留，只有显式重新评估才能离开。
                if (await repository.TryBlockPublicationAsync(
                        order.SuspendedOrderGuid,
                        publication.Revision,
                        mapping.Block!.Reason,
                        mapping.Block.Detail ?? string.Empty,
                        nowIso,
                        cancellationToken))
                {
                    blocked++;
                }

                continue;
            }

            if (await repository.TryStagePendingPublishAsync(
                    order.SuspendedOrderGuid,
                    publication.Revision,
                    mapping.Payload!,
                    nowIso,
                    cancellationToken))
            {
                staged++;
            }
        }

        var due = (await repository.ListDuePublicationsAsync(nowIso, cancellationToken))
            .Where(publication =>
                string.Equals(
                    publication.StoreCode,
                    storeCode,
                    StringComparison.OrdinalIgnoreCase)
                && (deviceCode is null
                    || string.Equals(
                        publication.DeviceCode,
                        deviceCode,
                        StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (due.Length == 0)
        {
            return new SharedHeldOrderPublicationRunResult(
                evaluated, staged, blocked, published, failedCapability, failedPublish);
        }

        // 先读 capability：disabled/网络不可用都不发布，只记退避（不阻止本地挂单）。
        var capability = await ReadCapabilityAsync(nowIso, due, cancellationToken);
        if (capability.Gate == CapabilityGate.NotReady)
        {
            return new SharedHeldOrderPublicationRunResult(
                evaluated, staged, blocked, published, failedCapability + due.Length, failedPublish);
        }

        if (capability.Gate == CapabilityGate.Disabled)
        {
            foreach (var publication in due)
            {
                await RecordBackoffAsync(
                    publication,
                    nowIso,
                    "SHARED_HELD_ORDER_DISABLED",
                    "服务端共享挂单功能未启用。",
                    cancellationToken);
            }

            return new SharedHeldOrderPublicationRunResult(
                evaluated, staged, blocked, published, failedCapability + due.Length, failedPublish);
        }

        foreach (var publication in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptPublication = publication;
            try
            {
                var payload = await repository.GetPublicationPayloadAsync(
                    publication.LocalHoldGuid,
                    cancellationToken);
                if (payload is null)
                {
                    throw new InvalidDataException("PendingPublish 缺少可解密 payload。");
                }

                var payloadVersion = attemptPublication.PublicationPayloadVersion;
                if (payloadVersion is null && capability.PreferredPayloadVersion == SharedSaleCartV1Constants.PayloadVersion
                    && payload.PricingState.Lines.Any(line => line.CatalogDiscountBasisPoints > 0))
                {
                    // 商品折扣基线不能无损降级到冻结 V1；保持 PendingPublish，等待后端
                    // preferred 切到 V2，绝不把目录折扣伪装成手工折扣或静默丢弃。
                    await RecordBackoffAsync(
                        publication,
                        nowIso,
                        "SHARED_HELD_ORDER_PREFERRED_VERSION_LOSSY",
                        "服务端首选 V1，当前挂单包含只能由 V2 表达的商品折扣。",
                        cancellationToken);
                    failedCapability++;
                    continue;
                }

                if (payloadVersion is null)
                {
                    var pinnedPublication = await repository.TryPinPublicationPayloadVersionAsync(
                        publication.LocalHoldGuid,
                        publication.Revision,
                        capability.PreferredPayloadVersion,
                        nowIso,
                        cancellationToken);
                    if (pinnedPublication is null)
                    {
                        // 并发已推进状态时不发送陈旧请求，下一轮读取最新 durable publication。
                        continue;
                    }

                    attemptPublication = pinnedPublication;
                    payloadVersion = attemptPublication.PublicationPayloadVersion;
                }

                // 首次发送前已落库的 wire 版本优先于当前 preferred，响应丢失后可安全幂等重放。
                var cart = ToPublishCart(payload, payloadVersion!.Value);
                var request = new SharedHeldOrderPublishRequest(
                    attemptPublication.LocalHoldGuid,
                    attemptPublication.StoreCode,
                    attemptPublication.DeviceCode,
                    cart,
                    IdempotencyKeyFor(attemptPublication.LocalHoldGuid));
                var response = await apiClient.PublishAsync(request, cancellationToken);
                // 发布成功：远端 revision/time 与 Published 状态原子落库（幂等重试同 key）。
                var advanced = await repository.TryAdvancePublicationAsync(
                    attemptPublication.LocalHoldGuid,
                    SharedHeldOrderPublicationStatus.PendingPublish,
                    attemptPublication.Revision,
                    SharedHeldOrderPublicationStatus.Published,
                    nowIso,
                    lastAttemptAtIso: nowIso,
                    remoteRevision: response.Revision,
                    remoteUpdatedAtIso: FormatIso(response.CreatedAtUtc),
                    cancellationToken: cancellationToken);
                if (advanced)
                {
                    published++;
                }
            }
            catch (SharedHeldOrderApiException exception)
            {
                // 发布失败：保持 PendingPublish，RetryCount +1 并退避；本地挂单不动。
                await RecordBackoffAsync(
                    attemptPublication,
                    nowIso,
                    exception.ErrorCode,
                    exception.Message,
                    cancellationToken,
                    exception);
                failedPublish++;
            }
            catch (Exception exception) when (
                exception is InvalidDataException
                    or SharedHeldOrderCanonicalValidationException)
            {
                // 本地 payload 无法构造发布请求：fail-closed Blocked，保留原因。
                await repository.TryAdvancePublicationAsync(
                    attemptPublication.LocalHoldGuid,
                    SharedHeldOrderPublicationStatus.PendingPublish,
                    attemptPublication.Revision,
                    SharedHeldOrderPublicationStatus.Blocked,
                    nowIso,
                    errorCode: "SHARED_HELD_ORDER_INVALID",
                    errorMessage: "本地共享挂单 payload 无法发布，已阻断。",
                    cancellationToken: cancellationToken);
                blocked++;
            }
        }

        return new SharedHeldOrderPublicationRunResult(
            evaluated, staged, blocked, published, failedCapability, failedPublish);
    }

    private async Task<CapabilityReadResult> ReadCapabilityAsync(
        string nowIso,
        IReadOnlyList<SharedHeldOrderPublication> due,
        CancellationToken cancellationToken)
    {
        try
        {
            var capabilities = await apiClient.GetCapabilitiesAsync(cancellationToken);
            if (!capabilities.Enabled)
            {
                return new CapabilityReadResult(CapabilityGate.Disabled, SharedSaleCartV1Constants.PayloadVersion);
            }

            if (capabilities.PayloadVersion != SharedSaleCartV1Constants.PayloadVersion)
            {
                foreach (var publication in due)
                {
                    await RecordBackoffAsync(
                        publication,
                        nowIso,
                        "SHARED_HELD_ORDER_VERSION_MISMATCH",
                        $"服务端 payload 版本 {capabilities.PayloadVersion} 与本地版本不一致。",
                        cancellationToken);
                }

                return new CapabilityReadResult(CapabilityGate.NotReady, SharedSaleCartV1Constants.PayloadVersion);
            }

            var supportedVersions = capabilities.SupportedPayloadVersions
                ?? [capabilities.PayloadVersion];
            var preferredVersion = capabilities.PreferredPayloadVersion;
            if (preferredVersion is not SharedSaleCartV1Constants.PayloadVersion
                    and not SharedSaleCartV2Constants.PayloadVersion
                || !supportedVersions.Contains(preferredVersion))
            {
                foreach (var publication in due)
                {
                    await RecordBackoffAsync(
                        publication,
                        nowIso,
                        "SHARED_HELD_ORDER_VERSION_MISMATCH",
                        "服务端共享挂单首选版本不在受支持版本列表中。",
                        cancellationToken);
                }

                return new CapabilityReadResult(CapabilityGate.NotReady, SharedSaleCartV1Constants.PayloadVersion);
            }

            return new CapabilityReadResult(CapabilityGate.Enabled, preferredVersion);
        }
        catch (SharedHeldOrderApiException exception)
        {
            foreach (var publication in due)
            {
                await RecordBackoffAsync(
                    publication,
                    nowIso,
                    exception.ErrorCode,
                    exception.Message,
                    cancellationToken,
                    exception);
            }

            return new CapabilityReadResult(CapabilityGate.NotReady, SharedSaleCartV1Constants.PayloadVersion);
        }
    }

    /// <summary>失败退避：状态机保持 PendingPublish，仓库内部 RetryCount +1 并计算 NextAttemptAtIso。</summary>
    private async Task RecordBackoffAsync(
        SharedHeldOrderPublication publication,
        string nowIso,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken,
        SharedHeldOrderApiException? exception = null)
    {
        await repository.TryAdvancePublicationAsync(
            publication.LocalHoldGuid,
            SharedHeldOrderPublicationStatus.PendingPublish,
            publication.Revision,
            SharedHeldOrderPublicationStatus.PendingPublish,
            nowIso,
            errorCode: errorCode,
            errorMessage: errorMessage,
            lastAttemptAtIso: nowIso,
            cancellationToken: cancellationToken);
        LogBackoff(publication, errorCode, errorMessage, exception);
    }

    /// <summary>
    /// 发布退避日志按本条挂单的累计失败次数节流：第 1、2、4、8… 次失败记 Warning，其余不记，
    /// 避免服务端长时间不可用时后台 10 秒一轮刷屏。功能未启用属于配置状态，不升级为 Warning。
    /// </summary>
    private static void LogBackoff(
        SharedHeldOrderPublication publication,
        string? errorCode,
        string? errorMessage,
        SharedHeldOrderApiException? exception)
    {
        var attempt = publication.RetryCount + 1;
        if (attempt <= 0 || (attempt & (attempt - 1)) != 0 ||
            exception?.Kind == SharedHeldOrderApiErrorKind.Disabled)
        {
            return;
        }

        ConsoleLog.WriteWarning(
            "HeldOrder",
            $"shared held order publish deferred localHoldGuid={publication.LocalHoldGuid:D} " +
            $"errorCode={errorCode ?? "<null>"} kind={exception?.Kind.ToString() ?? "<local>"} " +
            $"http={(exception is null ? "<none>" : ((int)exception.StatusCode).ToString(CultureInfo.InvariantCulture))} " +
            $"attempt={attempt} message={errorMessage ?? "<null>"}",
            new ApplicationLogContext(
                TraceId: publication.LocalHoldGuid.ToString("D"),
                RequestPath: "api/v1/held-orders",
                RequestMethod: "POST",
                StatusCode: exception is null ? null : (int)exception.StatusCode,
                Properties: new Dictionary<string, object?>
                {
                    ["storeCode"] = publication.StoreCode,
                    ["deviceCode"] = publication.DeviceCode,
                    ["errorCode"] = errorCode,
                    ["attemptCount"] = attempt,
                    ["operation"] = "publish"
                }),
            exception?.InnerException ?? exception);
    }

    /// <summary>本地 canonical -> 服务端首选 wire 版本（显式字段映射）。</summary>
    private static object ToPublishCart(
        SharedHeldOrderCanonicalPayload payload,
        int preferredPayloadVersion)
    {
        return SharedHeldOrderContractMapper.ToContract(payload, preferredPayloadVersion);
    }

    /// <summary>发布幂等键：同一本地挂单恒用同一 key，服务端重复发布按幂等返回。</summary>
    private static string IdempotencyKeyFor(Guid localHoldGuid)
    {
        return localHoldGuid.ToString("D");
    }

    private static string FormatIso(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private enum CapabilityGate
    {
        Enabled,
        Disabled,
        NotReady
    }

    private sealed record CapabilityReadResult(
        CapabilityGate Gate,
        int PreferredPayloadVersion);
}
