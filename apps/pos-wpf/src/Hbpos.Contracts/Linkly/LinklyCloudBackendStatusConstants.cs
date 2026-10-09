namespace Hbpos.Contracts.Linkly;

/// <summary>
/// Linkly Cloud Backend 会话状态与恢复动作常量
/// </summary>
public static class LinklyCloudBackendStatusConstants
{
    public const string StatusPending = "Pending";
    public const string StatusCompleted = "Completed";
    public const string StatusCancelled = "Cancelled";
    public const string StatusFailed = "Failed";
    public const string StatusNotSubmitted = "NotSubmitted";
    public const string StatusTokenRefreshRequired = "TokenRefreshRequired";
    // 主管在 POS 上对"结果未知"的会话作出决定后写入的结案终态，只由带主管结案标记的 ack 产生。
    public const string StatusSupervisorResolved = "SupervisorResolved";

    public const string RecoveryRetry = "Retry";
    public const string RecoveryRefreshToken = "RefreshToken";
    // 结算回调超时后服务端收口：会话已不再占用 POS/终端（IsActive=0），但 Linkly 是否执行过结算未知，
    // 需要 POS 侧查询，或由主管结案（ack 带 supervisorResolved）。只会出现在 Settlement 会话上。
    public const string RecoveryResultUnknown = "ResultUnknown";

    public static bool IsSuccessfulSettlement(bool? operationSuccess, string? responseCode)
    {
        return operationSuccess == true &&
            string.Equals(responseCode?.Trim(), "00", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSettlementFailureStatus(string? status)
    {
        return string.Equals(status, StatusFailed, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, StatusNotSubmitted, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, StatusCancelled, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase);
    }
}
