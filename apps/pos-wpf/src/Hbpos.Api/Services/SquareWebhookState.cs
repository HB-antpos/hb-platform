using Hbpos.Api.Data;
using Microsoft.Data.SqlClient;
using SqlSugar;

namespace Hbpos.Api.Services;

public sealed class SquareWebhookOptions
{
    public string? WebhookSignatureKey { get; set; }

    public Dictionary<string, string?> WebhookSignatureKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? WebhookNotificationUrl { get; set; }

    public Dictionary<string, string?> WebhookNotificationUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetSignatureKey(string environment)
    {
        if (WebhookSignatureKeys.TryGetValue(environment, out var environmentKey) &&
            !string.IsNullOrWhiteSpace(environmentKey))
        {
            return environmentKey.Trim();
        }

        return string.IsNullOrWhiteSpace(WebhookSignatureKey)
            ? null
            : WebhookSignatureKey.Trim();
    }

    public string? GetNotificationUrl(string environment)
    {
        if (WebhookNotificationUrls.TryGetValue(environment, out var environmentUrl) &&
            !string.IsNullOrWhiteSpace(environmentUrl))
        {
            return environmentUrl.Trim();
        }

        return string.IsNullOrWhiteSpace(WebhookNotificationUrl)
            ? null
            : WebhookNotificationUrl.Trim();
    }
}

public interface ISquareWebhookSchemaInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface ISquareWebhookSchemaSqlExecutor
{
    Task ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

public sealed class SqlSugarSquareWebhookSchemaInitializer(
    ISquareWebhookSchemaSqlExecutor sqlExecutor) : ISquareWebhookSchemaInitializer
{
    // Square webhook 事件和 checkout 状态都存到 POSM，便于去重和后续状态追踪。
    internal const string EnsureCheckoutSessionTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareCheckoutSession]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareCheckoutSession] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_SquareCheckoutSession] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [CheckoutId] NVARCHAR(128) NOT NULL,
                [Status] NVARCHAR(64) NOT NULL,
                [Amount] BIGINT NULL,
                [Currency] NVARCHAR(16) NULL,
                [DeviceId] NVARCHAR(128) NULL,
                [LocationId] NVARCHAR(128) NULL,
                [OriginStoreCode] NVARCHAR(50) NULL,
                [OriginDeviceCode] NVARCHAR(128) NULL,
                [PaymentId] NVARCHAR(128) NULL,
                [PaymentIdsJson] NVARCHAR(MAX) NULL,
                [RawCheckoutJson] NVARCHAR(MAX) NOT NULL,
                [LastEventId] NVARCHAR(128) NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareCheckoutSession_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_SquareCheckoutSession_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox'))
            );
        END;

        -- 兼容已存在的会话表，发布时只做幂等加列，不重建也不改写历史数据。
        IF OBJECT_ID(N'[dbo].[POSM_SquareCheckoutSession]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_SquareCheckoutSession', N'OriginStoreCode') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_SquareCheckoutSession]
                ADD [OriginStoreCode] NVARCHAR(50) NULL;
        END;

        IF OBJECT_ID(N'[dbo].[POSM_SquareCheckoutSession]', N'U') IS NOT NULL
           AND COL_LENGTH(N'dbo.POSM_SquareCheckoutSession', N'OriginDeviceCode') IS NULL
        BEGIN
            ALTER TABLE [dbo].[POSM_SquareCheckoutSession]
                ADD [OriginDeviceCode] NVARCHAR(128) NULL;
        END;
        """;

    internal const string EnsureWebhookEventTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareWebhookEvent]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareWebhookEvent] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_SquareWebhookEvent] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [EventId] NVARCHAR(128) NOT NULL,
                [EventType] NVARCHAR(128) NOT NULL,
                [PayloadJson] NVARCHAR(MAX) NOT NULL,
                [ReceivedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareWebhookEvent_ReceivedAt] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_SquareWebhookEvent_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox'))
            );
        END;
        """;

    // refund.updated 通知落库：退款可能 PENDING 数小时到 14 天，Square 实时查询失败时用它兜底。
    internal const string EnsureRefundTableSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareRefund]', N'U') IS NULL
        BEGIN
            CREATE TABLE [dbo].[POSM_SquareRefund] (
                [Id] BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT [PK_POSM_SquareRefund] PRIMARY KEY,
                [Environment] NVARCHAR(32) NOT NULL,
                [RefundId] NVARCHAR(128) NOT NULL,
                [Status] NVARCHAR(64) NOT NULL,
                [PaymentId] NVARCHAR(128) NULL,
                [Amount] BIGINT NULL,
                [Currency] NVARCHAR(16) NULL,
                [RawRefundJson] NVARCHAR(MAX) NOT NULL,
                [LastEventId] NVARCHAR(128) NULL,
                [UpdatedAt] DATETIME2(7) NOT NULL CONSTRAINT [DF_POSM_SquareRefund_UpdatedAt] DEFAULT (SYSUTCDATETIME()),
                CONSTRAINT [CK_POSM_SquareRefund_Environment] CHECK ([Environment] IN (N'Production', N'Sandbox'))
            );
        END;
        """;

    internal const string EnsureIndexesSql = """
        IF OBJECT_ID(N'[dbo].[POSM_SquareCheckoutSession]', N'U') IS NOT NULL
           AND NOT EXISTS (
               SELECT 1
               FROM sys.indexes
               WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_SquareCheckoutSession]', N'U')
                 AND [name] = N'UX_POSM_SquareCheckoutSession_Environment_CheckoutId')
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_SquareCheckoutSession_Environment_CheckoutId]
                ON [dbo].[POSM_SquareCheckoutSession] ([Environment], [CheckoutId]);
        END;

        IF OBJECT_ID(N'[dbo].[POSM_SquareWebhookEvent]', N'U') IS NOT NULL
           AND NOT EXISTS (
               SELECT 1
               FROM sys.indexes
               WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_SquareWebhookEvent]', N'U')
                 AND [name] = N'UX_POSM_SquareWebhookEvent_Environment_EventId')
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_SquareWebhookEvent_Environment_EventId]
                ON [dbo].[POSM_SquareWebhookEvent] ([Environment], [EventId]);
        END;

        IF OBJECT_ID(N'[dbo].[POSM_SquareRefund]', N'U') IS NOT NULL
           AND NOT EXISTS (
               SELECT 1
               FROM sys.indexes
               WHERE [object_id] = OBJECT_ID(N'[dbo].[POSM_SquareRefund]', N'U')
                 AND [name] = N'UX_POSM_SquareRefund_Environment_RefundId')
        BEGIN
            CREATE UNIQUE INDEX [UX_POSM_SquareRefund_Environment_RefundId]
                ON [dbo].[POSM_SquareRefund] ([Environment], [RefundId]);
        END;
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await sqlExecutor.ExecuteAsync(EnsureCheckoutSessionTableSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureWebhookEventTableSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureRefundTableSql, cancellationToken);
        await sqlExecutor.ExecuteAsync(EnsureIndexesSql, cancellationToken);
    }
}

public sealed class SqlSugarSquareWebhookSchemaSqlExecutor(
    HbposSqlSugarContext dbContext) : ISquareWebhookSchemaSqlExecutor
{
    public Task ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(sql);
    }
}

public interface ISquareCheckoutSessionRepository
{
    Task<bool> TryAddWebhookEventAsync(
        SquareWebhookEventRecord webhookEvent,
        CancellationToken cancellationToken);

    Task UpsertCheckoutSessionAsync(
        SquareCheckoutSessionRecord session,
        CancellationToken cancellationToken);

    Task<bool> RefreshCheckoutSessionFromLookupAsync(
        SquareCheckoutSessionRecord session,
        string originStoreCode,
        string originDeviceCode,
        CancellationToken cancellationToken);

    Task<SquareCheckoutSessionRecord?> BindCheckoutOriginAsync(
        string environment,
        string checkoutId,
        string originStoreCode,
        string originDeviceCode,
        CancellationToken cancellationToken);

    Task<SquareCheckoutSessionRecord?> GetCheckoutSessionAsync(
        string environment,
        string checkoutId,
        CancellationToken cancellationToken);

    Task<SquareCheckoutSessionRecord?> GetCheckoutSessionByPaymentIdAsync(
        string environment,
        string paymentId,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarSquareCheckoutSessionRepository(
    HbposSqlSugarContext dbContext) : ISquareCheckoutSessionRepository
{
    private const string TryAddWebhookEventSql = """
        IF NOT EXISTS (
            SELECT 1
            FROM [dbo].[POSM_SquareWebhookEvent]
            WHERE [Environment] = @Environment
              AND [EventId] = @EventId
        )
        BEGIN
            INSERT INTO [dbo].[POSM_SquareWebhookEvent] (
                [Environment], [EventId], [EventType], [PayloadJson], [ReceivedAt]
            )
            VALUES (
                @Environment, @EventId, @EventType, @PayloadJson, @ReceivedAt
            );
        END;
        """;

    private const string UpsertCheckoutSessionSql = """
        MERGE [dbo].[POSM_SquareCheckoutSession] WITH (HOLDLOCK) AS target
        USING (
            SELECT @Environment AS [Environment], @CheckoutId AS [CheckoutId]
        ) AS source
        ON target.[Environment] = source.[Environment]
           AND target.[CheckoutId] = source.[CheckoutId]
        -- 只允许更“新”的事件覆盖；并且已落库的终态不能被后续非终态回退。
        WHEN MATCHED
             AND @UpdatedAt >= target.[UpdatedAt]
             AND (
                 -- Square 使用 CANCELED；历史/兼容路径可能写入 CANCELLED，两个都按终态处理。
                 target.[Status] NOT IN (N'COMPLETED', N'CANCELED', N'CANCELLED', N'FAILED')
                 OR @Status IN (N'COMPLETED', N'CANCELED', N'CANCELLED', N'FAILED')
             ) THEN
            UPDATE SET
                [Status] = @Status,
                [Amount] = @Amount,
                [Currency] = @Currency,
                [DeviceId] = @DeviceId,
                [LocationId] = @LocationId,
                -- webhook 不携带 POS 来源；空值不得覆盖创建 checkout 时保存的设备边界。
                [OriginStoreCode] = COALESCE(target.[OriginStoreCode], @OriginStoreCode),
                [OriginDeviceCode] = COALESCE(target.[OriginDeviceCode], @OriginDeviceCode),
                [PaymentId] = @PaymentId,
                [PaymentIdsJson] = @PaymentIdsJson,
                [RawCheckoutJson] = @RawCheckoutJson,
                [LastEventId] = @LastEventId,
                [UpdatedAt] = @UpdatedAt
        WHEN NOT MATCHED THEN
            INSERT (
                [Environment], [CheckoutId], [Status], [Amount], [Currency], [DeviceId], [LocationId],
                [OriginStoreCode], [OriginDeviceCode],
                [PaymentId], [PaymentIdsJson], [RawCheckoutJson], [LastEventId], [UpdatedAt]
            )
            VALUES (
                @Environment, @CheckoutId, @Status, @Amount, @Currency, @DeviceId, @LocationId,
                @OriginStoreCode, @OriginDeviceCode,
                @PaymentId, @PaymentIdsJson, @RawCheckoutJson, @LastEventId, @UpdatedAt
            );
        """;

    private const string RefreshCheckoutSessionFromLookupSql = """
        UPDATE target WITH (UPDLOCK, HOLDLOCK)
        SET
            [Status] = CASE
                WHEN accepted.[Value] = 1
                    THEN COALESCE(NULLIF(@Status, N''), target.[Status])
                ELSE target.[Status]
            END,
            [Amount] = CASE
                WHEN accepted.[Value] = 1 THEN COALESCE(@Amount, target.[Amount])
                ELSE target.[Amount]
            END,
            [Currency] = CASE
                WHEN accepted.[Value] = 1 THEN COALESCE(NULLIF(@Currency, N''), target.[Currency])
                ELSE target.[Currency]
            END,
            [DeviceId] = CASE
                WHEN accepted.[Value] = 1 THEN COALESCE(NULLIF(@DeviceId, N''), target.[DeviceId])
                ELSE target.[DeviceId]
            END,
            [LocationId] = CASE
                WHEN accepted.[Value] = 1 THEN COALESCE(NULLIF(@LocationId, N''), target.[LocationId])
                ELSE target.[LocationId]
            END,
            [PaymentId] = COALESCE(NULLIF(target.[PaymentId], N''), NULLIF(@PaymentId, N'')),
            [PaymentIdsJson] = COALESCE(
                (
                    SELECT N'[' + STRING_AGG(
                        N'"' + STRING_ESCAPE(ids.[value], 'json') + N'"',
                        N','
                    ) + N']'
                    FROM (
                        SELECT CONVERT(nvarchar(4000), existing.[value]) AS [value]
                        FROM OPENJSON(CASE
                            WHEN ISJSON(target.[PaymentIdsJson]) = 1 THEN target.[PaymentIdsJson]
                            ELSE N'[]'
                        END) existing
                        WHERE NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), existing.[value]))), N'') IS NOT NULL
                        UNION
                        SELECT CONVERT(nvarchar(4000), incoming.[value]) AS [value]
                        FROM OPENJSON(CASE
                            WHEN ISJSON(@PaymentIdsJson) = 1 THEN @PaymentIdsJson
                            ELSE N'[]'
                        END) incoming
                        WHERE NULLIF(LTRIM(RTRIM(CONVERT(nvarchar(4000), incoming.[value]))), N'') IS NOT NULL
                        UNION
                        SELECT @PaymentId
                        WHERE NULLIF(LTRIM(RTRIM(@PaymentId)), N'') IS NOT NULL
                    ) ids
                ),
                target.[PaymentIdsJson]
            ),
            -- lookup 只能刷新公开状态；webhook 原始证据与事件游标必须原样保留。
            [UpdatedAt] = CASE
                WHEN accepted.[Value] = 1 THEN @UpdatedAt
                ELSE target.[UpdatedAt]
            END
        FROM [dbo].[POSM_SquareCheckoutSession] target
        CROSS APPLY (VALUES (
            CASE
                WHEN @UpdatedAt >= target.[UpdatedAt]
                     AND (
                         target.[Status] NOT IN (N'COMPLETED', N'CANCELED', N'CANCELLED', N'FAILED')
                         OR @Status IN (N'COMPLETED', N'CANCELED', N'CANCELLED', N'FAILED')
                     )
                    THEN 1
                ELSE 0
            END
        )) accepted([Value])
        WHERE target.[Environment] = @Environment
          AND target.[CheckoutId] = @CheckoutId
          AND target.[OriginStoreCode] = @OriginStoreCode
          AND target.[OriginDeviceCode] = @OriginDeviceCode;
        """;

    public async Task<bool> TryAddWebhookEventAsync(
        SquareWebhookEventRecord webhookEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            var affected = await dbContext.PosmDb.Ado.ExecuteCommandAsync(
                TryAddWebhookEventSql,
                new SugarParameter("@Environment", webhookEvent.Environment),
                new SugarParameter("@EventId", webhookEvent.EventId),
                new SugarParameter("@EventType", webhookEvent.EventType),
                new SugarParameter("@PayloadJson", webhookEvent.PayloadJson),
                new SugarParameter("@ReceivedAt", webhookEvent.ReceivedAt.UtcDateTime));
            return affected > 0;
        }
        catch (Exception ex) when (SquareWebhookSqlErrorClassifier.IsUniqueConstraintViolation(ex))
        {
            return false;
        }
    }

    public Task UpsertCheckoutSessionAsync(
        SquareCheckoutSessionRecord session,
        CancellationToken cancellationToken)
    {
        return UpsertCheckoutSessionCoreAsync(session, retryOnUniqueConflict: true);
    }

    public async Task<bool> RefreshCheckoutSessionFromLookupAsync(
        SquareCheckoutSessionRecord session,
        string originStoreCode,
        string originDeviceCode,
        CancellationToken cancellationToken)
    {
        var affected = await dbContext.PosmDb.Ado.ExecuteCommandAsync(
            RefreshCheckoutSessionFromLookupSql,
            new SugarParameter("@Environment", session.Environment),
            new SugarParameter("@CheckoutId", session.CheckoutId),
            new SugarParameter("@Status", session.Status),
            new SugarParameter("@Amount", session.Amount),
            new SugarParameter("@Currency", session.Currency),
            new SugarParameter("@DeviceId", session.DeviceId),
            new SugarParameter("@LocationId", session.LocationId),
            new SugarParameter("@PaymentId", session.PaymentId),
            new SugarParameter("@PaymentIdsJson", session.PaymentIdsJson),
            new SugarParameter("@UpdatedAt", session.UpdatedAt.UtcDateTime),
            new SugarParameter("@OriginStoreCode", originStoreCode),
            new SugarParameter("@OriginDeviceCode", originDeviceCode));
        return affected == 1;
    }

    public async Task<SquareCheckoutSessionRecord?> BindCheckoutOriginAsync(
        string environment,
        string checkoutId,
        string originStoreCode,
        string originDeviceCode,
        CancellationToken cancellationToken)
    {
        const string sql = """
            -- 来源只允许首次补齐；后续相同 checkout 的重试不能把会话转移到另一台 POS。
            UPDATE [dbo].[POSM_SquareCheckoutSession]
            SET [OriginStoreCode] = COALESCE([OriginStoreCode], @OriginStoreCode),
                [OriginDeviceCode] = COALESCE([OriginDeviceCode], @OriginDeviceCode)
            WHERE [Environment] = @Environment
              AND [CheckoutId] = @CheckoutId;

            SELECT TOP 1
                [Id], [Environment], [CheckoutId], [Status], [Amount], [Currency], [DeviceId], [LocationId],
                [OriginStoreCode], [OriginDeviceCode],
                [PaymentId], [PaymentIdsJson], [RawCheckoutJson], [LastEventId], [UpdatedAt]
            FROM [dbo].[POSM_SquareCheckoutSession]
            WHERE [Environment] = @Environment
              AND [CheckoutId] = @CheckoutId;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<SquareCheckoutSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@CheckoutId", checkoutId),
            new SugarParameter("@OriginStoreCode", originStoreCode),
            new SugarParameter("@OriginDeviceCode", originDeviceCode));
    }

    private async Task UpsertCheckoutSessionCoreAsync(
        SquareCheckoutSessionRecord session,
        bool retryOnUniqueConflict)
    {
        try
        {
            await dbContext.PosmDb.Ado.ExecuteCommandAsync(
                UpsertCheckoutSessionSql,
                new SugarParameter("@Environment", session.Environment),
                new SugarParameter("@CheckoutId", session.CheckoutId),
                new SugarParameter("@Status", session.Status),
                new SugarParameter("@Amount", session.Amount),
                new SugarParameter("@Currency", session.Currency),
                new SugarParameter("@DeviceId", session.DeviceId),
                new SugarParameter("@LocationId", session.LocationId),
                new SugarParameter("@OriginStoreCode", session.OriginStoreCode),
                new SugarParameter("@OriginDeviceCode", session.OriginDeviceCode),
                new SugarParameter("@PaymentId", session.PaymentId),
                new SugarParameter("@PaymentIdsJson", session.PaymentIdsJson),
                new SugarParameter("@RawCheckoutJson", session.RawCheckoutJson),
                new SugarParameter("@LastEventId", session.LastEventId),
                new SugarParameter("@UpdatedAt", session.UpdatedAt.UtcDateTime));
        }
        catch (Exception ex) when (retryOnUniqueConflict && SquareWebhookSqlErrorClassifier.IsUniqueConstraintViolation(ex))
        {
            // 并发首写同一个 checkout 时可能仍撞唯一键；重试一次会进入 MATCHED 分支完成状态更新。
            await UpsertCheckoutSessionCoreAsync(session, retryOnUniqueConflict: false);
        }
    }

    public async Task<SquareCheckoutSessionRecord?> GetCheckoutSessionAsync(
        string environment,
        string checkoutId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [CheckoutId], [Status], [Amount], [Currency], [DeviceId], [LocationId],
                [OriginStoreCode], [OriginDeviceCode],
                [PaymentId], [PaymentIdsJson], [RawCheckoutJson], [LastEventId], [UpdatedAt]
            FROM [dbo].[POSM_SquareCheckoutSession]
            WHERE [Environment] = @Environment
              AND [CheckoutId] = @CheckoutId
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<SquareCheckoutSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@CheckoutId", checkoutId));
    }

    public async Task<SquareCheckoutSessionRecord?> GetCheckoutSessionByPaymentIdAsync(
        string environment,
        string paymentId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [CheckoutId], [Status], [Amount], [Currency], [DeviceId], [LocationId],
                [OriginStoreCode], [OriginDeviceCode],
                [PaymentId], [PaymentIdsJson], [RawCheckoutJson], [LastEventId], [UpdatedAt]
            FROM [dbo].[POSM_SquareCheckoutSession]
            WHERE [Environment] = @Environment
              AND (
                  [PaymentId] = @PaymentId
                  OR EXISTS (
                      SELECT 1
                      FROM OPENJSON(CASE WHEN ISJSON([PaymentIdsJson]) = 1 THEN [PaymentIdsJson] ELSE N'[]' END)
                      WHERE [value] = @PaymentId
                  )
              )
            ORDER BY [UpdatedAt] DESC, [Id] DESC;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<SquareCheckoutSessionRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@PaymentId", paymentId));
    }
}

public interface ISquareRefundRepository
{
    Task UpsertRefundAsync(SquareRefundRecord refund, CancellationToken cancellationToken);

    Task<SquareRefundRecord?> GetRefundAsync(
        string environment,
        string refundId,
        CancellationToken cancellationToken);
}

public sealed class SqlSugarSquareRefundRepository(HbposSqlSugarContext dbContext) : ISquareRefundRepository
{
    private const string UpsertRefundSql = """
        MERGE [dbo].[POSM_SquareRefund] WITH (HOLDLOCK) AS target
        USING (
            SELECT @Environment AS [Environment], @RefundId AS [RefundId]
        ) AS source
        ON target.[Environment] = source.[Environment]
           AND target.[RefundId] = source.[RefundId]
        -- 只允许更“新”的事件覆盖；已落库的终态（COMPLETED/REJECTED/FAILED/CANCELED）不能被后续非终态回退。
        WHEN MATCHED
             AND @UpdatedAt >= target.[UpdatedAt]
             AND (
                 target.[Status] NOT IN (N'COMPLETED', N'REJECTED', N'FAILED', N'CANCELED')
                 OR @Status IN (N'COMPLETED', N'REJECTED', N'FAILED', N'CANCELED')
             ) THEN
            UPDATE SET
                [Status] = @Status,
                [PaymentId] = COALESCE(@PaymentId, target.[PaymentId]),
                [Amount] = COALESCE(@Amount, target.[Amount]),
                [Currency] = COALESCE(@Currency, target.[Currency]),
                [RawRefundJson] = @RawRefundJson,
                [LastEventId] = @LastEventId,
                [UpdatedAt] = @UpdatedAt
        WHEN NOT MATCHED THEN
            INSERT ([Environment], [RefundId], [Status], [PaymentId], [Amount], [Currency], [RawRefundJson], [LastEventId], [UpdatedAt])
            VALUES (@Environment, @RefundId, @Status, @PaymentId, @Amount, @Currency, @RawRefundJson, @LastEventId, @UpdatedAt);
        """;

    public async Task UpsertRefundAsync(SquareRefundRecord refund, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteUpsertAsync(refund);
        }
        catch (Exception ex) when (SquareWebhookSqlErrorClassifier.IsUniqueConstraintViolation(ex))
        {
            // 并发首写同一笔退款时可能撞唯一键；重试一次会进入 MATCHED 分支完成状态更新。
            await ExecuteUpsertAsync(refund);
        }
    }

    public async Task<SquareRefundRecord?> GetRefundAsync(
        string environment,
        string refundId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                [Id], [Environment], [RefundId], [Status], [PaymentId], [Amount], [Currency],
                [RawRefundJson], [LastEventId], [UpdatedAt]
            FROM [dbo].[POSM_SquareRefund]
            WHERE [Environment] = @Environment
              AND [RefundId] = @RefundId;
            """;

        return await dbContext.PosmDb.Ado.SqlQuerySingleAsync<SquareRefundRecord>(
            sql,
            new SugarParameter("@Environment", environment),
            new SugarParameter("@RefundId", refundId));
    }

    private Task ExecuteUpsertAsync(SquareRefundRecord refund)
    {
        return dbContext.PosmDb.Ado.ExecuteCommandAsync(
            UpsertRefundSql,
            new SugarParameter("@Environment", refund.Environment),
            new SugarParameter("@RefundId", refund.RefundId),
            new SugarParameter("@Status", refund.Status),
            new SugarParameter("@PaymentId", refund.PaymentId),
            new SugarParameter("@Amount", refund.Amount),
            new SugarParameter("@Currency", refund.Currency),
            new SugarParameter("@RawRefundJson", refund.RawRefundJson),
            new SugarParameter("@LastEventId", refund.LastEventId),
            new SugarParameter("@UpdatedAt", refund.UpdatedAt.UtcDateTime));
    }
}

public sealed class SquareRefundRecord
{
    public long Id { get; set; }

    public string Environment { get; set; } = string.Empty;

    public string RefundId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? PaymentId { get; set; }

    public long? Amount { get; set; }

    public string? Currency { get; set; }

    public string RawRefundJson { get; set; } = string.Empty;

    public string? LastEventId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal static class SquareWebhookSqlErrorClassifier
{
    // 统一归类 webhook event 的唯一键冲突，便于 repository 走幂等去重路径。
    public static bool IsUniqueConstraintViolation(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            // 只信任 SQL Server 错误号，避免普通异常文本包含 UNIQUE 时被误判为幂等冲突。
            if (current is SqlException { Number: 2601 or 2627 })
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class SquareCheckoutSessionRecord
{
    public long Id { get; set; }

    public string Environment { get; set; } = string.Empty;

    public string CheckoutId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public long? Amount { get; set; }

    public string? Currency { get; set; }

    public string? DeviceId { get; set; }

    public string? LocationId { get; set; }

    public string? OriginStoreCode { get; set; }

    public string? OriginDeviceCode { get; set; }

    public string? PaymentId { get; set; }

    public string? PaymentIdsJson { get; set; }

    public string RawCheckoutJson { get; set; } = string.Empty;

    public string? LastEventId { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SquareWebhookEventRecord
{
    public long Id { get; set; }

    public string Environment { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }
}
