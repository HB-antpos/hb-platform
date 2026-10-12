using Hbpos.Contracts.Linkly;
using static Hbpos.Contracts.Linkly.LinklyCloudBackendStatusConstants;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// Linkly 后端会话（<see cref="LinklyCloudBackendSessionResponse"/>）「状态名 / 明确批准 / 明确未批准 / 失败归类 / 批准金额核验」的唯一出处。
///
/// 以前实时收款（LinklyBackendTerminalClient）、异常中心恢复（CardPaymentRecoveryService）、
/// ConfiguredCardTerminalClient.RecoverLinklyAsync、新刷卡前接管旧会话（CashPaymentWorkflowService）
/// 各自手写 <c>string.Equals(status.Status, "...")</c>，状态名集合、取消的两种拼写、失败归类文本规则、
/// 终态批准的金额核验序列都有重复实现。这里只收拢**完全等价**的重复实现，口径本身的差异不抹平，
/// 而是各自显式命名，调用方按自己的语义选用：
/// <list type="bullet">
/// <item><description>
/// 「状态名终态」<see cref="IsTerminalStatusName"/>：Completed / Failed / NotSubmitted / Cancelled(Canceled)，只看状态名，
/// 不看结果字段。恢复服务与旧会话接管使用。
/// </description></item>
/// <item><description>
/// 「实时轮询终态」(LinklyBackendTerminalClient.IsFinal)：更严格——Completed 必须结果字段已到位（成败位 / 响应码 / 已核验的批准通知）才算终态，
/// 并额外把「Pending 但已有官方批准证据」视为终态。依赖通知 JSON 解析，因此留在 LinklyBackendTerminalClient。
/// 它与「状态名终态」的差异是已知且被特征化测试钉住的，见 LinklyBackendSessionStatusMatrixTests。
/// </description></item>
/// <item><description>
/// 「明确批准」<see cref="IsApprovedByFlag"/>（Completed 且 TransactionSuccess==true，恢复服务与 RecoverLinklyAsync 使用）比
/// LinklyBackendTerminalClient.IsApprovedFinalTransaction（实时收款与旧会话接管使用，另认批准响应码与已核验批准通知，
/// 并接受 Pending + 官方批准证据）更保守；两者的取舍由使用方决定，这里不合并。
/// </description></item>
/// </list>
/// 所有状态名比较都是 OrdinalIgnoreCase 且<b>不 Trim</b>（与之前的手写比较一致）：带空白的状态名按未知状态处理，即非终态。
/// </summary>
internal static class LinklyBackendSessionClassifier
{
    // Linkly 后端取消成功可能返回英式或美式拼写；Contracts 常量只有英式一种，美式只在客户端兼容。
    private const string StatusCanceledAmerican = "Canceled";

    // ------------------------------------------------------------------
    // 状态名（只看 Status 字符串）
    // ------------------------------------------------------------------

    public static bool IsCompleted(string? status) => IsStatus(status, StatusCompleted);

    public static bool IsPending(string? status) => IsStatus(status, StatusPending);

    public static bool IsNotSubmitted(string? status) => IsStatus(status, StatusNotSubmitted);

    /// <summary>取消：英式 Cancelled 与美式 Canceled 都认，轮询必须在这里停止，避免误报上一笔未完成。</summary>
    public static bool IsCancelled(string? status) =>
        IsStatus(status, StatusCancelled) || IsStatus(status, StatusCanceledAmerican);

    /// <summary>
    /// 仅凭状态名就能确定「没有成功扣款」的终态：Failed / NotSubmitted / Cancelled(Canceled)。
    /// 与 <see cref="LinklyCloudBackendStatusConstants.IsSettlementFailureStatus"/>（结算会话）集合相同，两处由测试保持同步。
    /// </summary>
    public static bool IsFailureStatus(string? status) =>
        IsStatus(status, StatusFailed) || IsNotSubmitted(status) || IsCancelled(status);

    /// <summary>
    /// 「状态名终态」：Completed 或失败类状态名。Completed 是否已带结果字段由调用方另行判断
    /// （见 <see cref="IsApprovedByFlag"/> / <see cref="IsDeclinedOrFailed"/> / <see cref="IsCompletedWithoutSuccessFlag"/>）。
    /// </summary>
    public static bool IsTerminalStatusName(string? status) => IsCompleted(status) || IsFailureStatus(status);

    // ------------------------------------------------------------------
    // 状态名 + 成败位（恢复路径的「明确」口径）
    // ------------------------------------------------------------------

    /// <summary>
    /// 明确批准：Completed 且 TransactionSuccess==true。TransactionSuccess 缺失（老数据 / 摘要会话）不算批准，
    /// 不会因为响应码是 00 或有批准通知就自动认定——这类会话在恢复路径保持「无结论」，由重新查询或主管处理。
    /// </summary>
    public static bool IsApprovedByFlag(LinklyCloudBackendSessionResponse session) =>
        IsCompleted(session.Status) && session.TransactionSuccess == true;

    /// <summary>
    /// 明确未批准：Completed 且 TransactionSuccess==false，或失败类状态名（Failed / NotSubmitted / Cancelled(Canceled)）。
    /// Completed 缺成败位既不是批准也不是未批准（返回 false）。
    /// </summary>
    public static bool IsDeclinedOrFailed(LinklyCloudBackendSessionResponse session) =>
        IsCompleted(session.Status)
            ? session.TransactionSuccess == false
            : IsFailureStatus(session.Status);

    /// <summary>Completed 但没有成败位：resumable 摘要可能只给 active session 概要，需要按 SessionId 再查一次权威状态。</summary>
    public static bool IsCompletedWithoutSuccessFlag(LinklyCloudBackendSessionResponse session) =>
        IsCompleted(session.Status) && session.TransactionSuccess is null;

    // ------------------------------------------------------------------
    // 失败归类
    // ------------------------------------------------------------------

    /// <summary>
    /// 把未批准的终态归类成本地 attempt 状态。只看 "Status ResponseText" 的文本（响应码不参与），
    /// 优先级 TIMEOUT &gt; CANCEL &gt; DECLIN &gt; 其余（Failed）。调用方须自行保证该会话不是批准。
    /// </summary>
    public static LocalCardPaymentAttemptStatus MapFailureOutcome(LinklyCloudBackendSessionResponse session)
    {
        var text = $"{session.Status} {session.ResponseText}".ToUpperInvariant();
        if (text.Contains("TIMEOUT", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.TimedOut;
        }

        if (text.Contains("CANCEL", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.Cancelled;
        }

        if (text.Contains("DECLIN", StringComparison.Ordinal))
        {
            return LocalCardPaymentAttemptStatus.Declined;
        }

        return LocalCardPaymentAttemptStatus.Failed;
    }

    // ------------------------------------------------------------------
    // 批准金额核验
    // ------------------------------------------------------------------

    /// <summary>
    /// 已到终态的批准证据核验：读取权威交易结果（卡交易明细优先，老服务端退回原始 transaction 通知），
    /// 要求终端判定为成功，且引用 / 金额 / 受保护响应码通过 <see cref="LinklyBackendTerminalClient.IsTransactionResultVerified"/>。
    /// 金额按分四舍五入（AwayFromZero）后必须相等，没有额外容差；请求金额取绝对值（退款草稿为负）；
    /// 绝不能用请求 / 草稿金额补造终端批准金额。只比较 AmtPurchase / 卡明细金额，
    /// cash-out、小费、附加费字段既不加也不减。
    /// 无论核验是否通过，<paramref name="result"/> 都是读到的交易结果（调用方在通过后用它构造收款记录）。
    /// </summary>
    public static bool TryReadVerifiedApproval(
        LinklyCloudBackendSessionResponse session,
        decimal expectedAmount,
        string? expectedTxnRef,
        out LinklyCloudTransactionResult result)
    {
        var requestedAmount = Math.Abs(expectedAmount);
        result = LinklyBackendTerminalClient.ReadTransactionResult(
            session,
            requestedAmount,
            string.IsNullOrWhiteSpace(expectedTxnRef) ? string.Empty : expectedTxnRef.Trim());
        return result.Succeeded &&
            LinklyBackendTerminalClient.IsTransactionResultVerified(session, result, requestedAmount);
    }

    private static bool IsStatus(string? status, string expected) =>
        string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
}
