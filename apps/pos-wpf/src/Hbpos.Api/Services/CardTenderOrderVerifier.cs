using Hbpos.Api.Data;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using SqlSugar;

namespace Hbpos.Api.Services;

/// <summary>卡付款对账异常类型。字符串落库，新增类型不需要迁移。</summary>
public static class CardTenderIssueTypes
{
    // 订单入库校验（M32）：卡付款自身的内部一致性。
    public const string NoCardTransactions = "NoCardTransactions";
    public const string CardAmountMismatch = "CardAmountMismatch";
    public const string CardNotApproved = "CardNotApproved";

    // 订单入库校验（M29）：ANZBACKEND 付款与服务端会话事实不符。
    public const string SessionNotFound = "SessionNotFound";
    public const string SessionScopeMismatch = "SessionScopeMismatch";
    public const string SessionNotApproved = "SessionNotApproved";
    public const string SessionAmountMismatch = "SessionAmountMismatch";
    public const string SessionTxnTypeMismatch = "SessionTxnTypeMismatch";
    public const string SessionTxnRefMismatch = "SessionTxnRefMismatch";
    public const string SessionLinkedToOtherOrder = "SessionLinkedToOtherOrder";
}

public static class CardTenderIssueSources
{
    public const string OrderSync = "OrderSync";
    public const string ReconciliationJob = "ReconciliationJob";
}

public static class CardTenderIssueSeverities
{
    public const string Warning = "Warning";
    public const string Error = "Error";
}

/// <summary>一条待登记的卡付款异常。身份由 <see cref="DedupKey"/> 决定，重复登记只刷新最近发现时间。</summary>
public sealed record CardTenderIssue(
    string IssueType,
    string Severity,
    string Source,
    string StoreCode,
    string? DeviceCode,
    string? Environment,
    string? SessionId,
    string? TxnRef,
    string? OrderGuid,
    string? PaymentGuid,
    decimal? Amount,
    string Detail)
{
    public const int DetailMaxLength = 1000;

    public string DedupKey => $"{IssueType}|{OrderGuid}|{PaymentGuid}|{SessionId}";
}

/// <summary>
/// 订单同步时校验卡付款证据的纯函数（不访问数据库）。
/// 服务端不能因校验不过而拒单：钱已经在银行侧扣了，拒单只会让收银端订单永久卡在 Failed，
/// 所以这里只产出异常，由调用方登记并告警。
/// </summary>
public static class CardTenderEvidenceChecker
{
    // 与 WPF 客户端 LinklyApprovalResponseCodes 同口径：00 普通批准，08 签名核验批准，11 Approved VIP。
    // 客户端类是 internal 且不在共享契约内，这里复制一份并在两处注释互相指明，改动时需同步。
    private static readonly string[] LinklyApprovedResponseCodes = ["00", "08", "11"];
    private const string LinklyProcessor = "ANZ";

    public static IReadOnlyList<CardTenderIssue> CheckConsistency(OrderSyncRequest request)
    {
        var issues = new List<CardTenderIssue>();
        foreach (var payment in request.Payments)
        {
            if (payment.Method != PaymentMethodKind.Card || payment.Amount == 0m)
            {
                continue;
            }

            var transactions = payment.CardTransactions ?? [];
            if (transactions.Count == 0)
            {
                issues.Add(Create(
                    request,
                    payment,
                    CardTenderIssueTypes.NoCardTransactions,
                    CardTenderIssueSeverities.Error,
                    "Card payment has no card transaction evidence, so no BankTransaction row can be written."));
                continue;
            }

            // planner 把银行流水金额统一成与 payment 同号的绝对值，这里同样只比较绝对值之和。
            var evidenceTotal = transactions.Sum(transaction => Math.Abs(transaction.Amount));
            var paymentTotal = Math.Abs(payment.Amount);
            if (decimal.Round(evidenceTotal, 2, MidpointRounding.AwayFromZero) !=
                decimal.Round(paymentTotal, 2, MidpointRounding.AwayFromZero))
            {
                issues.Add(Create(
                    request,
                    payment,
                    CardTenderIssueTypes.CardAmountMismatch,
                    CardTenderIssueSeverities.Error,
                    $"Card transaction amounts sum to {evidenceTotal:0.00} but the payment amount is {paymentTotal:0.00}."));
            }

            foreach (var transaction in transactions)
            {
                if (!RequiresApprovalResponseCode(transaction.Processor) ||
                    IsApprovedResponseCode(transaction.ResponseCode))
                {
                    continue;
                }

                issues.Add(Create(
                    request,
                    payment,
                    CardTenderIssueTypes.CardNotApproved,
                    CardTenderIssueSeverities.Error,
                    $"Card transaction txnRef={transaction.TxnRef} responseCode={transaction.ResponseCode ?? "<null>"} is not an approval code.",
                    transaction.TxnRef));
            }
        }

        return issues;
    }

    // 人工确认（Manual）没有银行响应码；Square 的批准状态写在 ResponseText 里且不带 ISO 响应码。
    // 只有 Linkly（ANZ）的交易才有可核对的响应码，其余处理器不做批准码检查，避免误报。
    private static bool RequiresApprovalResponseCode(string? processor)
    {
        return string.Equals(processor?.Trim(), LinklyProcessor, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApprovedResponseCode(string? responseCode)
    {
        var code = responseCode?.Trim();
        return LinklyApprovedResponseCodes.Contains(code, StringComparer.OrdinalIgnoreCase);
    }

    private static CardTenderIssue Create(
        OrderSyncRequest request,
        PaymentSyncDto payment,
        string issueType,
        string severity,
        string detail,
        string? txnRef = null)
    {
        return new CardTenderIssue(
            issueType,
            severity,
            CardTenderIssueSources.OrderSync,
            request.StoreCode,
            request.DeviceCode,
            null,
            null,
            txnRef,
            request.OrderGuid.ToString("D"),
            payment.PaymentGuid.ToString("D"),
            payment.Amount,
            Truncate(detail));
    }

    internal static string Truncate(string detail)
    {
        return detail.Length <= CardTenderIssue.DetailMaxLength
            ? detail
            : detail[..CardTenderIssue.DetailMaxLength];
    }
}

/// <summary>订单同步时用来核对的会话事实（来自 POSM_LinklyCloudBackendSession）。</summary>
public sealed class CardTenderSessionFact
{
    public long Id { get; set; }

    public string Environment { get; set; } = string.Empty;

    public string StoreCode { get; set; } = string.Empty;

    public string DeviceCode { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? TxnRef { get; set; }

    public string? RequestTxnType { get; set; }

    public long? RequestAmountCents { get; set; }

    public bool? TransactionSuccess { get; set; }

    public string? OperationType { get; set; }

    public string? OrderGuid { get; set; }
}

public interface ICardTenderReconciliationRepository
{
    /// <summary>先按订单门店精确查找（走唯一索引前缀）；本店没有时再跨店按会话号查，用来识别串店。</summary>
    Task<IReadOnlyList<CardTenderSessionFact>> FindSessionsAsync(
        string environment,
        string storeCode,
        string sessionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// 只在会话尚未回链时写入订单号；返回写入后会话上的订单号（可能是别的订单）。
    /// </summary>
    Task<string?> TryLinkSessionToOrderAsync(
        long sessionRowId,
        string orderGuid,
        CancellationToken cancellationToken);

    Task UpsertIssuesAsync(
        IReadOnlyList<CardTenderIssue> issues,
        CancellationToken cancellationToken);
}

/// <summary>订单入库后的卡付款核对：内部一致性 + ANZBACKEND 会话核对 + 会话回链订单。</summary>
public interface ICardTenderOrderVerifier
{
    Task VerifyAsync(OrderSyncRequest request);
}

public sealed class CardTenderOrderVerifier(
    ICardTenderReconciliationRepository repository,
    ILogger<CardTenderOrderVerifier>? logger = null) : ICardTenderOrderVerifier
{
    // 订单已提交后才校验：客户端断开不应让核对半途取消（重试会命中 AlreadySynced，不会再校验），
    // 所以使用独立的短超时而不是请求的取消令牌。
    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(15);

    public async Task VerifyAsync(OrderSyncRequest request)
    {
        using var timeout = new CancellationTokenSource(VerificationTimeout);
        try
        {
            var issues = new List<CardTenderIssue>(CardTenderEvidenceChecker.CheckConsistency(request));
            var orderGuid = request.OrderGuid.ToString("D");
            foreach (var payment in request.Payments)
            {
                if (payment.Method != PaymentMethodKind.Card ||
                    !LinklyBackendPaymentReference.TryGetPrintMarker(payment.Reference, out var environment, out var sessionId))
                {
                    continue;
                }

                issues.AddRange(await VerifySessionAsync(
                    request,
                    payment,
                    orderGuid,
                    environment,
                    sessionId,
                    timeout.Token));
            }

            if (issues.Count == 0)
            {
                return;
            }

            foreach (var issue in issues)
            {
                // Warning 级别会进中心日志，运维无需查库也能收到告警。
                logger?.LogWarning(
                    "CardTenderReconciliation issue type={IssueType} severity={Severity} orderGuid={OrderGuid} paymentGuid={PaymentGuid} store={StoreCode} device={DeviceCode} sessionId={SessionId} detail={Detail}",
                    issue.IssueType,
                    issue.Severity,
                    issue.OrderGuid,
                    issue.PaymentGuid,
                    issue.StoreCode,
                    issue.DeviceCode,
                    issue.SessionId,
                    issue.Detail);
            }

            await repository.UpsertIssuesAsync(issues, timeout.Token);
        }
        catch (Exception ex)
        {
            // 核对是旁路能力，任何失败都不能影响已入库订单的同步结果；对账作业会兜底发现遗漏。
            logger?.LogError(
                ex,
                "CardTenderReconciliation verify failed orderGuid={OrderGuid} store={StoreCode} device={DeviceCode}",
                request.OrderGuid,
                request.StoreCode,
                request.DeviceCode);
        }
    }

    private async Task<IReadOnlyList<CardTenderIssue>> VerifySessionAsync(
        OrderSyncRequest request,
        PaymentSyncDto payment,
        string orderGuid,
        string environment,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var issues = new List<CardTenderIssue>();
        CardTenderIssue Issue(string type, string severity, string detail, string? txnRef = null) => new(
            type,
            severity,
            CardTenderIssueSources.OrderSync,
            request.StoreCode,
            request.DeviceCode,
            environment,
            sessionId,
            txnRef,
            orderGuid,
            payment.PaymentGuid.ToString("D"),
            payment.Amount,
            CardTenderEvidenceChecker.Truncate(detail));

        var sessions = await repository.FindSessionsAsync(environment, request.StoreCode, sessionId, cancellationToken);
        if (sessions.Count == 0)
        {
            issues.Add(Issue(
                CardTenderIssueTypes.SessionNotFound,
                CardTenderIssueSeverities.Error,
                "No backend Linkly session matches the ANZBACKEND payment reference."));
            return issues;
        }

        // 理论上 (环境, 会话号) 唯一；同号多行时优先取与订单同店同设备的那条，核对其余事实。
        var session = sessions.FirstOrDefault(candidate =>
                SameStore(candidate, request) &&
                string.Equals(candidate.DeviceCode, request.DeviceCode, StringComparison.OrdinalIgnoreCase))
            ?? sessions.FirstOrDefault(candidate => SameStore(candidate, request))
            ?? sessions[0];

        var sameStore = SameStore(session, request);
        var sameDevice = string.Equals(session.DeviceCode, request.DeviceCode, StringComparison.OrdinalIgnoreCase);
        if (!sameStore || !sameDevice)
        {
            issues.Add(Issue(
                CardTenderIssueTypes.SessionScopeMismatch,
                sameStore ? CardTenderIssueSeverities.Warning : CardTenderIssueSeverities.Error,
                $"Session belongs to store={session.StoreCode} device={session.DeviceCode}, but the order was uploaded by store={request.StoreCode} device={request.DeviceCode}."));
        }

        var approved = string.Equals(session.Status, LinklyCloudBackendStatusConstants.StatusCompleted, StringComparison.OrdinalIgnoreCase) &&
            session.TransactionSuccess == true &&
            string.Equals(session.OperationType ?? "Transaction", "Transaction", StringComparison.OrdinalIgnoreCase);
        if (!approved)
        {
            issues.Add(Issue(
                CardTenderIssueTypes.SessionNotApproved,
                CardTenderIssueSeverities.Error,
                $"Session is not an approved transaction: status={session.Status} transactionSuccess={session.TransactionSuccess?.ToString() ?? "<null>"} operationType={session.OperationType ?? "<null>"}.",
                session.TxnRef));
        }

        var paymentCents = ToCents(Math.Abs(payment.Amount));
        if (session.RequestAmountCents is { } requestCents && requestCents != paymentCents)
        {
            issues.Add(Issue(
                CardTenderIssueTypes.SessionAmountMismatch,
                CardTenderIssueSeverities.Error,
                $"Session requested {requestCents} cents but the payment is {paymentCents} cents.",
                session.TxnRef));
        }

        // P 为购买、R 为退款；符号与会话请求类型相反说明这笔付款不是该会话产生的。
        var txnType = session.RequestTxnType?.Trim();
        if ((string.Equals(txnType, "P", StringComparison.OrdinalIgnoreCase) && payment.Amount < 0m) ||
            (string.Equals(txnType, "R", StringComparison.OrdinalIgnoreCase) && payment.Amount > 0m))
        {
            issues.Add(Issue(
                CardTenderIssueTypes.SessionTxnTypeMismatch,
                CardTenderIssueSeverities.Warning,
                $"Session txnType={txnType} does not match payment amount sign ({payment.Amount:0.00}).",
                session.TxnRef));
        }

        // 恢复路径可能用会话号兜底拼出交易，此时 TxnRef 与会话真实 TxnRef 不一致。
        var evidenceTxnRefs = (payment.CardTransactions ?? [])
            .Select(transaction => transaction.TxnRef?.Trim())
            .Where(txnRef => !string.IsNullOrEmpty(txnRef))
            .ToList();
        if (!string.IsNullOrWhiteSpace(session.TxnRef) &&
            evidenceTxnRefs.Count > 0 &&
            !evidenceTxnRefs.Contains(session.TxnRef.Trim(), StringComparer.Ordinal))
        {
            issues.Add(Issue(
                CardTenderIssueTypes.SessionTxnRefMismatch,
                CardTenderIssueSeverities.Warning,
                $"Session txnRef={session.TxnRef} is not among the uploaded card transactions ({string.Join(",", evidenceTxnRefs)}).",
                session.TxnRef));
        }

        // 只对同店同环境的会话回链：跨店会话回链会让对方门店的「无订单」对账被错误的订单抵消。
        if (sameStore)
        {
            var linkedOrder = await repository.TryLinkSessionToOrderAsync(session.Id, orderGuid, cancellationToken);
            if (!string.IsNullOrWhiteSpace(linkedOrder) &&
                !string.Equals(linkedOrder, orderGuid, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Issue(
                    CardTenderIssueTypes.SessionLinkedToOtherOrder,
                    CardTenderIssueSeverities.Error,
                    $"Session is already linked to order {linkedOrder}; the same card approval appears on two orders.",
                    session.TxnRef));
            }
        }

        return issues;
    }

    private static bool SameStore(CardTenderSessionFact session, OrderSyncRequest request)
    {
        return string.Equals(session.StoreCode?.Trim(), request.StoreCode?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static long ToCents(decimal amount)
    {
        return (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
    }
}

public sealed class SqlSugarCardTenderReconciliationRepository(
    HbposSqlSugarContext dbContext) : ICardTenderReconciliationRepository
{
    private const string SessionColumns = """
        [Id], [Environment], [StoreCode], [DeviceCode], [SessionId], [Status], [TxnRef],
        [RequestTxnType], [RequestAmountCents], [TransactionSuccess], [OperationType], [OrderGuid]
        """;

    internal const string FindSessionsInStoreSql = $"""
        SELECT {SessionColumns}
        FROM [dbo].[POSM_LinklyCloudBackendSession]
        WHERE [Environment] = @Environment
          AND [StoreCode] = @StoreCode
          AND [SessionId] = @SessionId;
        """;

    internal const string FindSessionsAnyStoreSql = $"""
        SELECT {SessionColumns}
        FROM [dbo].[POSM_LinklyCloudBackendSession]
        WHERE [Environment] = @Environment
          AND [SessionId] = @SessionId;
        """;

    internal const string LinkSessionSql = """
        SET NOCOUNT ON;
        UPDATE [dbo].[POSM_LinklyCloudBackendSession]
        SET [OrderGuid] = @OrderGuid
        WHERE [Id] = @Id AND [OrderGuid] IS NULL;
        SELECT [OrderGuid] FROM [dbo].[POSM_LinklyCloudBackendSession] WHERE [Id] = @Id;
        """;

    // 同一问题（DedupKey）只保留一行：再次发现时刷新时间与次数；已人工 Dismissed 的不再重新打开。
    internal const string UpsertIssueSql = """
        MERGE [dbo].[POSM_CardTenderReconciliationIssue] WITH (HOLDLOCK) AS target
        USING (SELECT @DedupKey AS [DedupKey]) AS source
        ON target.[DedupKey] = source.[DedupKey]
        WHEN MATCHED THEN
            UPDATE SET
                [Status] = CASE WHEN target.[Status] = N'Dismissed' THEN target.[Status] ELSE N'Open' END,
                [ResolvedAt] = CASE WHEN target.[Status] = N'Dismissed' THEN target.[ResolvedAt] ELSE NULL END,
                [Detail] = @Detail,
                [Amount] = @Amount,
                [OccurrenceCount] = target.[OccurrenceCount] + 1,
                [LastDetectedAt] = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN
            INSERT ([DedupKey], [IssueType], [Severity], [Source], [StoreCode], [DeviceCode], [Environment],
                    [SessionId], [TxnRef], [OrderGuid], [PaymentGuid], [Amount], [Detail])
            VALUES (@DedupKey, @IssueType, @Severity, @Source, @StoreCode, @DeviceCode, @Environment,
                    @SessionId, @TxnRef, @OrderGuid, @PaymentGuid, @Amount, @Detail);
        """;

    public async Task<IReadOnlyList<CardTenderSessionFact>> FindSessionsAsync(
        string environment,
        string storeCode,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var inStore = await dbContext.PosmDb.Ado.SqlQueryAsync<CardTenderSessionFact>(
            FindSessionsInStoreSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@StoreCode", storeCode),
            new SugarParameter("@SessionId", sessionId));
        if (inStore.Count > 0)
        {
            return inStore;
        }

        return await dbContext.PosmDb.Ado.SqlQueryAsync<CardTenderSessionFact>(
            FindSessionsAnyStoreSql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@SessionId", sessionId));
    }

    public async Task<string?> TryLinkSessionToOrderAsync(
        long sessionRowId,
        string orderGuid,
        CancellationToken cancellationToken)
    {
        var linked = await dbContext.PosmDb.Ado.GetScalarAsync(
            LinkSessionSql,
            new SugarParameter("@Id", sessionRowId),
            new SugarParameter("@OrderGuid", orderGuid));
        return linked is null or DBNull ? null : Convert.ToString(linked);
    }

    public async Task UpsertIssuesAsync(
        IReadOnlyList<CardTenderIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var issue in issues)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await dbContext.PosmDb.Ado.ExecuteCommandAsync(UpsertIssueSql, ToIssueParameters(issue));
        }
    }

    internal static SugarParameter[] ToIssueParameters(CardTenderIssue issue)
    {
        return
        [
            new SugarParameter("@DedupKey", issue.DedupKey),
            new SugarParameter("@IssueType", issue.IssueType),
            new SugarParameter("@Severity", issue.Severity),
            new SugarParameter("@Source", issue.Source),
            new SugarParameter("@StoreCode", issue.StoreCode),
            new SugarParameter("@DeviceCode", issue.DeviceCode),
            new SugarParameter("@Environment", issue.Environment),
            new SugarParameter("@SessionId", issue.SessionId),
            new SugarParameter("@TxnRef", issue.TxnRef),
            new SugarParameter("@OrderGuid", issue.OrderGuid),
            new SugarParameter("@PaymentGuid", issue.PaymentGuid),
            new SugarParameter("@Amount", issue.Amount),
            new SugarParameter("@Detail", issue.Detail)
        ];
    }
}
