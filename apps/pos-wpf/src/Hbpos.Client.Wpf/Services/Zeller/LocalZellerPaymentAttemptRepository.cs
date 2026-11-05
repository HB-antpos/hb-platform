using System.Collections.Concurrent;
using System.Globalization;
using Hbpos.Client.Wpf.Models;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services.Zeller;

/// <summary>Zeller 尝试记录的状态。语义见「Zeller 直连接入方案」的状态机一节。</summary>
public enum ZellerAttemptStatus
{
    /// <summary>记录、reference、订单草稿已落库，还没调用 SDK。</summary>
    Pending,

    /// <summary>即将或已经调用 SDK。从这里开始不能证明「没扣款」。</summary>
    Submitted,

    /// <summary>批准：金额、reference 都对得上，且只有一笔。</summary>
    Approved,

    /// <summary>银行明确拒绝。</summary>
    Declined,

    /// <summary>未付款：取消、超时、刷卡机没连上并经查询确认，或请求根本没发出。</summary>
    Canceled,

    /// <summary>结果未知：自动重查或恢复中心处理。</summary>
    Unknown,

    /// <summary>批准后订单已经落盘。</summary>
    OrderCompleted
}

public enum ZellerAttemptOperation
{
    Sale,
    Refund
}

/// <summary>一次 Zeller 扣款或退款尝试的本地记录。付款前先落库，崩溃后靠它按 reference 找回交易。</summary>
public sealed record LocalZellerPaymentAttempt(
    Guid AttemptGuid,
    ZellerAttemptOperation OperationKind,
    string Reference,
    int AmountCents,
    ZellerAttemptStatus Status,
    string OrderDraftJson,
    string StoreCode,
    string DeviceCode,
    string CashierId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string Currency { get; init; } = "AUD";

    /// <summary>乐观并发版本，每次成功更新加一。</summary>
    public long Version { get; init; }

    public Guid? OperationGuid { get; init; }

    /// <summary>
    /// 退款的业务键，标识「这一笔退货要退回原卡」这件事。同一个键同时只能有一笔进行中或结果未知的退款，
    /// 防止结果未知时收银员再点一次退款而退两次。扣款为空。
    /// </summary>
    public string? RefundBusinessKey { get; init; }

    public ZellerFailureKind FailureKind { get; init; }

    public ZellerUnknownReason UnknownReason { get; init; }

    /// <summary>调用期间收到过「请刷卡」事件：请求已经到达刷卡机。</summary>
    public bool ReachedTerminal { get; init; }

    /// <summary>调用期间收到过「处理中」事件：钱可能已经扣了。</summary>
    public bool ProcessingStarted { get; init; }

    public string? TransactionUuid { get; init; }

    /// <summary>退款时原扣款的 transactionUuid。</summary>
    public string? OriginalTransactionUuid { get; init; }

    public string? ResponseCode { get; init; }

    public string? ResponseText { get; init; }

    /// <summary>
    /// 扣款或退款调用以错误结束时 SDK 给的错误类型（ZellerErrorType 的名字），调用返回交易时为空。
    /// 和 ResponseText 分开存：ResponseText 是交易的响应文字，会进订单和恢复补单；这里只用于排查。
    /// </summary>
    public string? CallErrorType { get; init; }

    /// <summary>SDK 返回的错误原文（或网关、异常给的原因），例如「取消了上一次遗留的调用」和「收银员取消」的区别只在这里看得出来。</summary>
    public string? CallErrorMessage { get; init; }

    public string? ApprovalCode { get; init; }

    public string? Rrn { get; init; }

    public string? PanMasked { get; init; }

    public string? Scheme { get; init; }

    public string? Bin { get; init; }

    public string? MerchantId { get; init; }

    public string? TerminalId { get; init; }

    public string? ReceiptText { get; init; }

    public string? ReceiptLink { get; init; }

    /// <summary>刷卡机给的交易时间。刷卡机时钟比电脑快十几到三十秒，只用于显示，不用于匹配。</summary>
    public DateTimeOffset? TransactionTimestamp { get; init; }

    /// <summary>自动重查进行了多少轮。</summary>
    public int VerificationRounds { get; init; }

    /// <summary>按未付款收尾后，什么时候再复核一次。</summary>
    public DateTimeOffset? LateRecheckDueAt { get; init; }

    public DateTimeOffset? LateRecheckedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>主管最近一次的决定（确认已付款、确认未付款、继续等待）。没有主管处理过时为空。</summary>
    public ZellerSupervisorDecision? SupervisorDecision { get; init; }

    public string? SupervisorNote { get; init; }

    /// <summary>主管填写的付款参考号（例如 RRN 或批准码），选填。</summary>
    public string? SupervisorFinancialReference { get; init; }

    public string? SupervisorOperatorId { get; init; }

    public string? SupervisorOperatorName { get; init; }

    public DateTimeOffset? SupervisorDecidedAt { get; init; }

    /// <summary>把一笔 Zeller 交易的字段写进记录。查询回来的交易没有小票原文和批准码，已有的值不会被空值覆盖。</summary>
    public LocalZellerPaymentAttempt WithTransaction(ZellerTransaction transaction) => this with
    {
        TransactionUuid = transaction.TransactionUuid ?? TransactionUuid,
        ResponseCode = transaction.ResponseCode ?? ResponseCode,
        ResponseText = transaction.ResponseText ?? ResponseText,
        ApprovalCode = NullIfBlank(transaction.ApprovalCode) ?? ApprovalCode,
        Rrn = transaction.Rrn ?? Rrn,
        PanMasked = transaction.PanMasked ?? PanMasked,
        Scheme = transaction.Scheme ?? Scheme,
        Bin = transaction.Bin ?? Bin,
        MerchantId = transaction.MerchantId ?? MerchantId,
        TerminalId = transaction.TerminalId ?? TerminalId,
        ReceiptText = NullIfBlank(transaction.ReceiptText) ?? ReceiptText,
        ReceiptLink = NullIfBlank(transaction.ReceiptLink) ?? ReceiptLink,
        TransactionTimestamp = transaction.Timestamp ?? TransactionTimestamp
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>允许的状态转换。不在表里的转换是程序错误，仓库会拒绝。</summary>
public static class ZellerAttemptTransitions
{
    public static bool IsAllowed(ZellerAttemptStatus from, ZellerAttemptStatus to) => (from, to) switch
    {
        (ZellerAttemptStatus.Pending, ZellerAttemptStatus.Submitted or ZellerAttemptStatus.Canceled) => true,
        (ZellerAttemptStatus.Submitted, ZellerAttemptStatus.Submitted or ZellerAttemptStatus.Approved or ZellerAttemptStatus.Declined
            or ZellerAttemptStatus.Canceled or ZellerAttemptStatus.Unknown) => true,
        (ZellerAttemptStatus.Unknown, ZellerAttemptStatus.Unknown or ZellerAttemptStatus.Approved or ZellerAttemptStatus.Declined
            or ZellerAttemptStatus.Canceled) => true,

        // 未付款收尾后复核：记下复核时间，或者发现迟到的批准而转回结果未知。
        (ZellerAttemptStatus.Canceled, ZellerAttemptStatus.Canceled or ZellerAttemptStatus.Unknown) => true,
        (ZellerAttemptStatus.Approved, ZellerAttemptStatus.OrderCompleted) => true,
        _ => false
    };
}

public interface ILocalZellerPaymentAttemptRepository
{
    Task CreateAsync(LocalZellerPaymentAttempt attempt, CancellationToken cancellationToken = default);

    Task<LocalZellerPaymentAttempt?> GetAsync(Guid attemptGuid, CancellationToken cancellationToken = default);

    /// <summary>
    /// 新建一条带业务键的退款尝试；同一个业务键已经有进行中或结果未知的退款时，不新建，返回那一条。
    /// 调用方比较返回记录的 AttemptGuid 就知道是不是自己这一条。
    /// </summary>
    Task<LocalZellerPaymentAttempt> CreateOrGetOpenRefundAsync(
        LocalZellerPaymentAttempt attempt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按版本号做乐观并发更新。成功返回落库后的记录（版本号已加一）；记录已被别人改过时返回 null。
    /// 不允许的状态转换、或者试图改动不可变字段，会抛出异常。
    /// </summary>
    Task<LocalZellerPaymentAttempt?> TryUpdateAsync(
        LocalZellerPaymentAttempt current,
        LocalZellerPaymentAttempt next,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 和 <see cref="TryUpdateAsync"/> 一样的乐观锁更新，同时在同一个事务里写一条主管结案日志（LocalFinancialSupervisorResolutions）。
    /// 更新没有生效（版本已变）时日志也不写，返回 null。
    /// </summary>
    Task<LocalZellerPaymentAttempt?> TryUpdateWithJournalAsync(
        LocalZellerPaymentAttempt current,
        LocalZellerPaymentAttempt next,
        LocalFinancialSupervisorResolution journal,
        CancellationToken cancellationToken = default);

    /// <summary>这台收银机最近的尝试（不分状态，最新的在前），恢复中心的历史页用。</summary>
    Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListRecentAsync(
        string storeCode,
        string deviceCode,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>这台收银机上还没有结论的尝试：已发出但没有结果，或结果未知。</summary>
    Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListOpenAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default);

    /// <summary>这台收银机上处于指定状态的尝试，最近更新的在前。</summary>
    Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListByStatusAsync(
        string storeCode,
        string deviceCode,
        IReadOnlyCollection<ZellerAttemptStatus> statuses,
        CancellationToken cancellationToken = default);

    /// <summary>到期但还没复核的未付款记录。程序重启后用它补做复核。</summary>
    Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListLateRechecksDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>Zeller 尝试表的建表语句。</summary>
/// <remarks>
/// 中文注释：Zeller 还没接入收款流程，所以先由仓库在首次使用时自己建表，不改动 LocalSchemaService。
/// 接入收款流程时把 Statements 挂到 LocalSchemaService 的初始化里。
/// </remarks>
public static class ZellerPaymentAttemptSchema
{
    public static readonly IReadOnlyList<string> Statements =
    [
        """
        CREATE TABLE IF NOT EXISTS LocalZellerPaymentAttempts (
            AttemptGuid TEXT PRIMARY KEY,
            OperationKind TEXT NOT NULL,
            Reference TEXT NOT NULL,
            AmountCents INTEGER NOT NULL,
            Currency TEXT NOT NULL,
            Status TEXT NOT NULL,
            Version INTEGER NOT NULL DEFAULT 0,
            OperationGuid TEXT NULL,
            RefundBusinessKey TEXT NULL,
            FailureKind TEXT NOT NULL DEFAULT 'None',
            UnknownReason TEXT NOT NULL DEFAULT 'None',
            ReachedTerminal INTEGER NOT NULL DEFAULT 0,
            ProcessingStarted INTEGER NOT NULL DEFAULT 0,
            TransactionUuid TEXT NULL,
            OriginalTransactionUuid TEXT NULL,
            ResponseCode TEXT NULL,
            ResponseText TEXT NULL,
            CallErrorType TEXT NULL,
            CallErrorMessage TEXT NULL,
            ApprovalCode TEXT NULL,
            Rrn TEXT NULL,
            PanMasked TEXT NULL,
            Scheme TEXT NULL,
            Bin TEXT NULL,
            MerchantId TEXT NULL,
            TerminalId TEXT NULL,
            ReceiptText TEXT NULL,
            ReceiptLink TEXT NULL,
            TransactionTimestamp TEXT NULL,
            OrderDraftJson TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            VerificationRounds INTEGER NOT NULL DEFAULT 0,
            LateRecheckDueAt TEXT NULL,
            LateRecheckedAt TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            CompletedAt TEXT NULL,
            SupervisorDecision TEXT NULL,
            SupervisorNote TEXT NULL,
            SupervisorFinancialReference TEXT NULL,
            SupervisorOperatorId TEXT NULL,
            SupervisorOperatorName TEXT NULL,
            SupervisorDecidedAt TEXT NULL
        );
        """,
        // 中文注释：reference 是找回交易的唯一线索，绝不允许两条尝试共用。
        "CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalZellerPaymentAttempts_Reference ON LocalZellerPaymentAttempts (Reference);",
        "CREATE INDEX IF NOT EXISTS IX_LocalZellerPaymentAttempts_Open ON LocalZellerPaymentAttempts (StoreCode, DeviceCode, Status, UpdatedAt DESC);",
        // 中文注释：同一笔退货同时只能有一笔进行中或结果未知的退款。
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalZellerPaymentAttempts_OpenRefundBusinessKey
        ON LocalZellerPaymentAttempts (RefundBusinessKey)
        WHERE OperationKind = 'Refund' AND RefundBusinessKey IS NOT NULL AND Status IN ('Pending', 'Submitted', 'Unknown');
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalZellerPaymentAttempts_LateRecheck
        ON LocalZellerPaymentAttempts (LateRecheckDueAt)
        WHERE Status = 'Canceled' AND LateRecheckDueAt IS NOT NULL AND LateRecheckedAt IS NULL;
        """
    ];

    /// <summary>建表之后补加的列。已经建过表的机器（试点、本机调试）靠这里补上。</summary>
    public static readonly IReadOnlyList<(string Name, string Definition)> AddedColumns =
    [
        ("CallErrorType", "TEXT NULL"),
        ("CallErrorMessage", "TEXT NULL")
    ];

    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        foreach (var statement in Statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(LocalZellerPaymentAttempts);";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var (name, definition) in AddedColumns)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE LocalZellerPaymentAttempts ADD COLUMN {name} {definition};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

public sealed class LocalZellerPaymentAttemptRepository(LocalSqliteStore store) : ILocalZellerPaymentAttemptRepository
{
    private const string Columns = """
        AttemptGuid, OperationKind, Reference, AmountCents, Currency, Status, Version, OperationGuid, RefundBusinessKey,
        FailureKind, UnknownReason, ReachedTerminal, ProcessingStarted,
        TransactionUuid, OriginalTransactionUuid, ResponseCode, ResponseText, CallErrorType, CallErrorMessage, ApprovalCode, Rrn,
        PanMasked, Scheme, Bin, MerchantId, TerminalId, ReceiptText, ReceiptLink, TransactionTimestamp,
        OrderDraftJson, StoreCode, DeviceCode, CashierId, VerificationRounds,
        LateRecheckDueAt, LateRecheckedAt, CreatedAt, UpdatedAt, CompletedAt,
        SupervisorDecision, SupervisorNote, SupervisorFinancialReference,
        SupervisorOperatorId, SupervisorOperatorName, SupervisorDecidedAt
        """;

    // 中文注释：本地库可能随服务器地址切换到另一个文件，所以按文件路径记录「已建表」。
    private readonly ConcurrentDictionary<string, bool> _schemaReady = new(StringComparer.OrdinalIgnoreCase);

    public async Task CreateAsync(LocalZellerPaymentAttempt attempt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        Validate(attempt);
        await using var connection = await OpenAsync(cancellationToken);
        await InsertAsync(connection, attempt, cancellationToken);
    }

    public async Task<LocalZellerPaymentAttempt> CreateOrGetOpenRefundAsync(
        LocalZellerPaymentAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        Validate(attempt);
        if (attempt.OperationKind != ZellerAttemptOperation.Refund || string.IsNullOrWhiteSpace(attempt.RefundBusinessKey))
        {
            throw new ArgumentException("Only a refund with a business key can be deduplicated.", nameof(attempt));
        }

        await using var connection = await OpenAsync(cancellationToken);
        if (await FindOpenRefundAsync(connection, attempt.RefundBusinessKey, cancellationToken) is { } existing)
        {
            return existing;
        }

        try
        {
            await InsertAsync(connection, attempt, cancellationToken);
            return attempt with { Version = 0 };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // 中文注释：两边同时为同一笔退货建退款，唯一索引只放进一条，另一边读回那一条。
            var winner = await FindOpenRefundAsync(connection, attempt.RefundBusinessKey, cancellationToken);
            if (winner is null)
            {
                throw;
            }

            return winner;
        }
    }

    private static async Task<LocalZellerPaymentAttempt?> FindOpenRefundAsync(
        SqliteConnection connection,
        string refundBusinessKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM LocalZellerPaymentAttempts
            WHERE OperationKind = 'Refund' AND RefundBusinessKey = $RefundBusinessKey
              AND Status IN ('Pending', 'Submitted', 'Unknown')
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$RefundBusinessKey", refundBusinessKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    private static async Task InsertAsync(SqliteConnection connection, LocalZellerPaymentAttempt attempt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO LocalZellerPaymentAttempts ({Columns})
            VALUES (
                $AttemptGuid, $OperationKind, $Reference, $AmountCents, $Currency, $Status, $Version, $OperationGuid, $RefundBusinessKey,
                $FailureKind, $UnknownReason, $ReachedTerminal, $ProcessingStarted,
                $TransactionUuid, $OriginalTransactionUuid, $ResponseCode, $ResponseText, $CallErrorType, $CallErrorMessage, $ApprovalCode, $Rrn,
                $PanMasked, $Scheme, $Bin, $MerchantId, $TerminalId, $ReceiptText, $ReceiptLink, $TransactionTimestamp,
                $OrderDraftJson, $StoreCode, $DeviceCode, $CashierId, $VerificationRounds,
                $LateRecheckDueAt, $LateRecheckedAt, $CreatedAt, $UpdatedAt, $CompletedAt,
                $SupervisorDecision, $SupervisorNote, $SupervisorFinancialReference,
                $SupervisorOperatorId, $SupervisorOperatorName, $SupervisorDecidedAt
            );
            """;
        AddParameters(command, attempt with { Version = 0 });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LocalZellerPaymentAttempt?> GetAsync(Guid attemptGuid, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM LocalZellerPaymentAttempts WHERE AttemptGuid = $AttemptGuid;";
        command.Parameters.AddWithValue("$AttemptGuid", attemptGuid.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public Task<LocalZellerPaymentAttempt?> TryUpdateAsync(
        LocalZellerPaymentAttempt current,
        LocalZellerPaymentAttempt next,
        CancellationToken cancellationToken = default) =>
        TryUpdateCoreAsync(current, next, journal: null, cancellationToken);

    public Task<LocalZellerPaymentAttempt?> TryUpdateWithJournalAsync(
        LocalZellerPaymentAttempt current,
        LocalZellerPaymentAttempt next,
        LocalFinancialSupervisorResolution journal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return TryUpdateCoreAsync(current, next, journal, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListRecentAsync(
        string storeCode,
        string deviceCode,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM LocalZellerPaymentAttempts
            WHERE StoreCode = $StoreCode AND DeviceCode = $DeviceCode
            ORDER BY UpdatedAt DESC, CreatedAt DESC
            LIMIT $Limit;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        command.Parameters.AddWithValue("$Limit", limit);
        return await ReadAllAsync(command, cancellationToken);
    }

    private async Task<LocalZellerPaymentAttempt?> TryUpdateCoreAsync(
        LocalZellerPaymentAttempt current,
        LocalZellerPaymentAttempt next,
        LocalFinancialSupervisorResolution? journal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        EnsureImmutableFieldsUnchanged(current, next);
        if (!ZellerAttemptTransitions.IsAllowed(current.Status, next.Status))
        {
            throw new InvalidOperationException($"Zeller attempt {current.AttemptGuid} cannot move from {current.Status} to {next.Status}.");
        }

        var persisted = next with { Version = current.Version + 1 };
        await using var connection = await OpenAsync(cancellationToken);
        // 中文注释：带日志时更新和日志在同一个事务里，要么都写进去，要么都不写（和 Square 的主管结案一致）。
        await using var transaction = journal is null ? null : (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE LocalZellerPaymentAttempts SET
                Status = $Status,
                Version = $Version,
                FailureKind = $FailureKind,
                UnknownReason = $UnknownReason,
                ReachedTerminal = $ReachedTerminal,
                ProcessingStarted = $ProcessingStarted,
                TransactionUuid = $TransactionUuid,
                OriginalTransactionUuid = $OriginalTransactionUuid,
                ResponseCode = $ResponseCode,
                ResponseText = $ResponseText,
                CallErrorType = $CallErrorType,
                CallErrorMessage = $CallErrorMessage,
                ApprovalCode = $ApprovalCode,
                Rrn = $Rrn,
                PanMasked = $PanMasked,
                Scheme = $Scheme,
                Bin = $Bin,
                MerchantId = $MerchantId,
                TerminalId = $TerminalId,
                ReceiptText = $ReceiptText,
                ReceiptLink = $ReceiptLink,
                TransactionTimestamp = $TransactionTimestamp,
                VerificationRounds = $VerificationRounds,
                LateRecheckDueAt = $LateRecheckDueAt,
                LateRecheckedAt = $LateRecheckedAt,
                UpdatedAt = $UpdatedAt,
                CompletedAt = $CompletedAt,
                SupervisorDecision = $SupervisorDecision,
                SupervisorNote = $SupervisorNote,
                SupervisorFinancialReference = $SupervisorFinancialReference,
                SupervisorOperatorId = $SupervisorOperatorId,
                SupervisorOperatorName = $SupervisorOperatorName,
                SupervisorDecidedAt = $SupervisorDecidedAt
            WHERE AttemptGuid = $AttemptGuid
              AND Status = $ExpectedStatus
              AND Version = $ExpectedVersion;
            """;
        AddParameters(command, persisted);
        command.Parameters.AddWithValue("$ExpectedStatus", current.Status.ToString());
        command.Parameters.AddWithValue("$ExpectedVersion", current.Version);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            return null;
        }

        if (transaction is not null)
        {
            await LocalFinancialSupervisorResolutionRepository.InsertAsync(connection, transaction, journal!, cancellationToken);
            await transaction.CommitAsync(CancellationToken.None);
        }

        return persisted;
    }

    public Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListOpenAsync(
        string storeCode,
        string deviceCode,
        CancellationToken cancellationToken = default) =>
        ListByStatusAsync(storeCode, deviceCode, [ZellerAttemptStatus.Submitted, ZellerAttemptStatus.Unknown], cancellationToken);

    public async Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListByStatusAsync(
        string storeCode,
        string deviceCode,
        IReadOnlyCollection<ZellerAttemptStatus> statuses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Count == 0)
        {
            return [];
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var names = statuses.Distinct().Select((status, index) =>
        {
            var name = $"$Status{index}";
            command.Parameters.AddWithValue(name, status.ToString());
            return name;
        }).ToArray();
        command.CommandText = $"""
            SELECT {Columns} FROM LocalZellerPaymentAttempts
            WHERE StoreCode = $StoreCode AND DeviceCode = $DeviceCode
              AND Status IN ({string.Join(", ", names)})
            ORDER BY UpdatedAt DESC, CreatedAt DESC;
            """;
        command.Parameters.AddWithValue("$StoreCode", storeCode);
        command.Parameters.AddWithValue("$DeviceCode", deviceCode);
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<LocalZellerPaymentAttempt>> ListLateRechecksDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM LocalZellerPaymentAttempts
            WHERE Status = 'Canceled' AND LateRecheckDueAt IS NOT NULL AND LateRecheckedAt IS NULL
              AND LateRecheckDueAt <= $Now
            ORDER BY LateRecheckDueAt;
            """;
        command.Parameters.AddWithValue("$Now", FormatTime(now));
        return await ReadAllAsync(command, cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await store.OpenConnectionAsync(cancellationToken);
        try
        {
            var path = connection.DataSource ?? string.Empty;
            if (!_schemaReady.ContainsKey(path))
            {
                await ZellerPaymentAttemptSchema.EnsureAsync(connection, cancellationToken);
                _schemaReady[path] = true;
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<IReadOnlyList<LocalZellerPaymentAttempt>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<LocalZellerPaymentAttempt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    private static void Validate(LocalZellerPaymentAttempt attempt)
    {
        if (attempt.AttemptGuid == Guid.Empty)
        {
            throw new ArgumentException("Zeller attempt identity is required.", nameof(attempt));
        }

        if (attempt.AmountCents <= 0)
        {
            throw new ArgumentException("Zeller attempt amount must be positive.", nameof(attempt));
        }

        if (string.IsNullOrWhiteSpace(attempt.Reference))
        {
            throw new ArgumentException("Zeller attempt reference is required.", nameof(attempt));
        }
    }

    private static void EnsureImmutableFieldsUnchanged(LocalZellerPaymentAttempt current, LocalZellerPaymentAttempt next)
    {
        if (current.AttemptGuid != next.AttemptGuid ||
            current.OperationKind != next.OperationKind ||
            !string.Equals(current.Reference, next.Reference, StringComparison.Ordinal) ||
            current.AmountCents != next.AmountCents ||
            !string.Equals(current.Currency, next.Currency, StringComparison.Ordinal) ||
            !string.Equals(current.OrderDraftJson, next.OrderDraftJson, StringComparison.Ordinal) ||
            !string.Equals(current.StoreCode, next.StoreCode, StringComparison.Ordinal) ||
            !string.Equals(current.DeviceCode, next.DeviceCode, StringComparison.Ordinal) ||
            !string.Equals(current.CashierId, next.CashierId, StringComparison.Ordinal) ||
            current.OperationGuid != next.OperationGuid ||
            !string.Equals(current.RefundBusinessKey, next.RefundBusinessKey, StringComparison.Ordinal) ||
            !string.Equals(current.OriginalTransactionUuid, next.OriginalTransactionUuid, StringComparison.Ordinal) ||
            current.CreatedAt != next.CreatedAt)
        {
            throw new ArgumentException("Only the outcome fields of a Zeller attempt can change.", nameof(next));
        }
    }

    private static void AddParameters(SqliteCommand command, LocalZellerPaymentAttempt attempt)
    {
        command.Parameters.AddWithValue("$AttemptGuid", attempt.AttemptGuid.ToString("D"));
        command.Parameters.AddWithValue("$OperationKind", attempt.OperationKind.ToString());
        command.Parameters.AddWithValue("$Reference", attempt.Reference);
        command.Parameters.AddWithValue("$AmountCents", attempt.AmountCents);
        command.Parameters.AddWithValue("$Currency", attempt.Currency);
        command.Parameters.AddWithValue("$Status", attempt.Status.ToString());
        command.Parameters.AddWithValue("$Version", attempt.Version);
        command.Parameters.AddWithValue("$OperationGuid", (object?)attempt.OperationGuid?.ToString("D") ?? DBNull.Value);
        AddText(command, "$RefundBusinessKey", attempt.RefundBusinessKey);
        command.Parameters.AddWithValue("$FailureKind", attempt.FailureKind.ToString());
        command.Parameters.AddWithValue("$UnknownReason", attempt.UnknownReason.ToString());
        command.Parameters.AddWithValue("$ReachedTerminal", attempt.ReachedTerminal ? 1 : 0);
        command.Parameters.AddWithValue("$ProcessingStarted", attempt.ProcessingStarted ? 1 : 0);
        AddText(command, "$TransactionUuid", attempt.TransactionUuid);
        AddText(command, "$OriginalTransactionUuid", attempt.OriginalTransactionUuid);
        AddText(command, "$ResponseCode", attempt.ResponseCode);
        AddText(command, "$ResponseText", attempt.ResponseText);
        AddText(command, "$CallErrorType", attempt.CallErrorType);
        AddText(command, "$CallErrorMessage", attempt.CallErrorMessage);
        AddText(command, "$ApprovalCode", attempt.ApprovalCode);
        AddText(command, "$Rrn", attempt.Rrn);
        AddText(command, "$PanMasked", attempt.PanMasked);
        AddText(command, "$Scheme", attempt.Scheme);
        AddText(command, "$Bin", attempt.Bin);
        AddText(command, "$MerchantId", attempt.MerchantId);
        AddText(command, "$TerminalId", attempt.TerminalId);
        AddText(command, "$ReceiptText", attempt.ReceiptText);
        AddText(command, "$ReceiptLink", attempt.ReceiptLink);
        AddText(command, "$TransactionTimestamp", FormatTime(attempt.TransactionTimestamp));
        command.Parameters.AddWithValue("$OrderDraftJson", attempt.OrderDraftJson);
        command.Parameters.AddWithValue("$StoreCode", attempt.StoreCode);
        command.Parameters.AddWithValue("$DeviceCode", attempt.DeviceCode);
        command.Parameters.AddWithValue("$CashierId", attempt.CashierId);
        command.Parameters.AddWithValue("$VerificationRounds", attempt.VerificationRounds);
        AddText(command, "$LateRecheckDueAt", FormatTime(attempt.LateRecheckDueAt));
        AddText(command, "$LateRecheckedAt", FormatTime(attempt.LateRecheckedAt));
        command.Parameters.AddWithValue("$CreatedAt", FormatTime(attempt.CreatedAt));
        command.Parameters.AddWithValue("$UpdatedAt", FormatTime(attempt.UpdatedAt));
        AddText(command, "$CompletedAt", FormatTime(attempt.CompletedAt));
        AddText(command, "$SupervisorDecision", attempt.SupervisorDecision?.ToString());
        AddText(command, "$SupervisorNote", attempt.SupervisorNote);
        AddText(command, "$SupervisorFinancialReference", attempt.SupervisorFinancialReference);
        AddText(command, "$SupervisorOperatorId", attempt.SupervisorOperatorId);
        AddText(command, "$SupervisorOperatorName", attempt.SupervisorOperatorName);
        AddText(command, "$SupervisorDecidedAt", FormatTime(attempt.SupervisorDecidedAt));
    }

    private static void AddText(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);

    private static LocalZellerPaymentAttempt Read(SqliteDataReader reader)
    {
        string? Text(string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        long Integer(string column) => reader.GetInt64(reader.GetOrdinal(column));

        return new LocalZellerPaymentAttempt(
            Guid.Parse(Text("AttemptGuid")!),
            ParseEnum(Text("OperationKind"), ZellerAttemptOperation.Sale),
            Text("Reference")!,
            checked((int)Integer("AmountCents")),
            ParseEnum(Text("Status"), ZellerAttemptStatus.Unknown),
            Text("OrderDraftJson")!,
            Text("StoreCode")!,
            Text("DeviceCode")!,
            Text("CashierId")!,
            ParseTime(Text("CreatedAt"))!.Value,
            ParseTime(Text("UpdatedAt"))!.Value)
        {
            Currency = Text("Currency")!,
            Version = Integer("Version"),
            OperationGuid = Text("OperationGuid") is { } operationGuid ? Guid.Parse(operationGuid) : null,
            RefundBusinessKey = Text("RefundBusinessKey"),

            // 中文注释：读到不认识的值时按最保守的一侧处理。
            FailureKind = ParseEnum(Text("FailureKind"), ZellerFailureKind.ResultUnknown),
            UnknownReason = ParseEnum(Text("UnknownReason"), ZellerUnknownReason.SdkError),
            ReachedTerminal = Integer("ReachedTerminal") != 0,
            ProcessingStarted = Integer("ProcessingStarted") != 0,
            TransactionUuid = Text("TransactionUuid"),
            OriginalTransactionUuid = Text("OriginalTransactionUuid"),
            ResponseCode = Text("ResponseCode"),
            ResponseText = Text("ResponseText"),
            CallErrorType = Text("CallErrorType"),
            CallErrorMessage = Text("CallErrorMessage"),
            ApprovalCode = Text("ApprovalCode"),
            Rrn = Text("Rrn"),
            PanMasked = Text("PanMasked"),
            Scheme = Text("Scheme"),
            Bin = Text("Bin"),
            MerchantId = Text("MerchantId"),
            TerminalId = Text("TerminalId"),
            ReceiptText = Text("ReceiptText"),
            ReceiptLink = Text("ReceiptLink"),
            TransactionTimestamp = ParseTime(Text("TransactionTimestamp")),
            VerificationRounds = checked((int)Integer("VerificationRounds")),
            LateRecheckDueAt = ParseTime(Text("LateRecheckDueAt")),
            LateRecheckedAt = ParseTime(Text("LateRecheckedAt")),
            CompletedAt = ParseTime(Text("CompletedAt")),
            SupervisorDecision = Text("SupervisorDecision") is { } decision &&
                Enum.TryParse<ZellerSupervisorDecision>(decision, ignoreCase: false, out var parsedDecision) &&
                Enum.IsDefined(parsedDecision)
                    ? parsedDecision
                    : null,
            SupervisorNote = Text("SupervisorNote"),
            SupervisorFinancialReference = Text("SupervisorFinancialReference"),
            SupervisorOperatorId = Text("SupervisorOperatorId"),
            SupervisorOperatorName = Text("SupervisorOperatorName"),
            SupervisorDecidedAt = ParseTime(Text("SupervisorDecidedAt"))
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    // 中文注释：统一存成 UTC 的往返格式，字符串比较即时间比较（复核到期查询依赖这一点）。
    private static string FormatTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static string? FormatTime(DateTimeOffset? value) => value is { } time ? FormatTime(time) : null;

    private static DateTimeOffset? ParseTime(string? value) =>
        value is null
            ? null
            : DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
