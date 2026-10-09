using static Hbpos.Contracts.Linkly.LinklyCloudBackendStatusConstants;

namespace Hbpos.Api.Services;

/// <summary>
/// 后端会话“终态保护”的唯一实现：SQL MERGE（SqlSugarLinklyCloudBackendAsyncRepository.UpsertSessionSql）、
/// InMemory 仓储和服务层 ShouldKeepFinalResultSession 共用同一份语义。
/// 此前 InMemory 只拦“Completed 被非 Completed 覆盖”，与 SQL 不一致，导致 SQL 才有的拦截（例如
/// target 的 ResponseCode 为 NULL 时拒绝补写）单测永远发现不了。
/// </summary>
internal static class LinklyCloudBackendSessionWriteGuard
{
    public static bool IsProtectedFinalResult(LinklyCloudBackendSessionRecord session)
    {
        return string.Equals(session.Status, StatusCompleted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.Status, StatusCancelled, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 主管已结案，或客户端已 ack 的失败/未提交会话：客户端已据此作出业务决定（放行付款、重刷或记为未收款），
    /// 之后到达的 Linkly 结果不得再改写 Status，只能进入 LateFinal* 独立字段并告警。
    /// </summary>
    public static bool IsOperatorSettled(LinklyCloudBackendSessionRecord session)
    {
        if (string.Equals(session.Status, StatusSupervisorResolved, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return session.ClientAcknowledgedAt is not null &&
            (string.Equals(session.Status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(session.Status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>true 表示 <paramref name="existing"/> 必须原样保留，不接受 <paramref name="incoming"/> 的写入。</summary>
    public static bool ShouldRejectWrite(
        LinklyCloudBackendSessionRecord? existing,
        LinklyCloudBackendSessionRecord incoming)
    {
        if (existing is null)
        {
            return false;
        }

        if (IsOperatorSettled(existing) &&
            !string.Equals(existing.Status, incoming.Status, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IsProtectedFinalResult(existing))
        {
            return false;
        }

        if (!IsProtectedFinalResult(incoming))
        {
            return true;
        }

        // 已完成会话只保护交易结果证据：existing 没有的证据（TransactionSuccess、ResponseCode、ResponseText、
        // SettlementData 为空）允许迟到的回调/官方 GET 补写，已有的证据不得被不同的值覆盖。
        var transactionSuccessConflict =
            existing.TransactionSuccess is not null &&
            existing.TransactionSuccess != incoming.TransactionSuccess;
        var operationSuccessConflict =
            existing.OperationSuccess is not null &&
            existing.OperationSuccess != incoming.OperationSuccess;
        return !SameOperationType(existing.OperationType, incoming.OperationType) ||
            !Same(existing.TxnRef, incoming.TxnRef) ||
            transactionSuccessConflict ||
            operationSuccessConflict ||
            HasDifferentEvidence(existing.SettlementData, incoming.SettlementData) ||
            HasDifferentEvidence(existing.ResponseCode, incoming.ResponseCode) ||
            HasDifferentEvidence(existing.ResponseText, incoming.ResponseText);
    }

    private static bool HasDifferentEvidence(string? existing, string? incoming)
    {
        return Normalize(existing) is not null && !Same(existing, incoming);
    }

    private static bool SameOperationType(string? left, string? right)
    {
        return string.Equals(
            Normalize(left) ?? "Transaction",
            Normalize(right) ?? "Transaction",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool Same(string? left, string? right)
    {
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
