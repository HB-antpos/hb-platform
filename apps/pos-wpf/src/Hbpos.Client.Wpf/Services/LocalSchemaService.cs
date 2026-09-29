using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ILocalSchemaService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalSchemaService(LocalSqliteStore store) : ILocalSchemaService
{
    private readonly TaskCompletionSource schemaReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default) =>
        schemaReady.Task.WaitAsync(cancellationToken);

    public void SignalReady() => schemaReady.TrySetResult();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        foreach (var sql in TableStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureLocalSellableItemIndexColumnsAsync(connection, cancellationToken);
        await EnsureDeviceCacheColumnsAsync(connection, cancellationToken);
        await EnsureLocalOrderColumnsAsync(connection, cancellationToken);
        await EnsureLocalOrderLineColumnsAsync(connection, cancellationToken);
        await EnsureLocalPaymentColumnsAsync(connection, cancellationToken);
        await EnsureLocalCardTransactionColumnsAsync(connection, cancellationToken);
        await EnsureLocalCardPaymentAttemptColumnsAsync(connection, cancellationToken);
        await EnsureLocalSquarePaymentAttemptColumnsAsync(connection, cancellationToken);
        await EnsureLocalInstallmentColumnsAsync(connection, cancellationToken);
        await EnsureLocalInstallmentOperationAttemptColumnsAsync(connection, cancellationToken);
        await EnsureSuspendedOrderColumnsAsync(connection, cancellationToken);
        await EnsureSuspendedOrderLineColumnsAsync(connection, cancellationToken);
        await EnsureSuspendedOrderReturnPaymentCapacityColumnsAsync(connection, cancellationToken);
        await EnsureSharedHeldOrderConsumptionColumnsAsync(connection, cancellationToken);
        await EnsureSharedHeldOrderSchemaAsync(connection, cancellationToken);
        await EnsureLinklySettlementUploadColumnsAsync(connection, cancellationToken);

        foreach (var sql in IndexStatements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 进程中断会遗留无法自行重试的 Syncing；启动时仅恢复订单上传状态，不触碰支付记录。
        await using var transaction = connection.BeginTransaction();
        await using var recoveryCommand = connection.CreateCommand();
        recoveryCommand.Transaction = transaction;
        recoveryCommand.CommandText =
            """
            UPDATE LocalOrders
            SET SyncStatus = 'Pending'
            WHERE SyncStatus = 'Syncing'
              AND EXISTS (
                  SELECT 1
                  FROM SyncQueue
                  WHERE EntityType = 'Order'
                    AND Status = 'Syncing'
                    AND EntityId = LocalOrders.OrderGuid
              );

            UPDATE SyncQueue
            SET Status = 'Pending'
            WHERE EntityType = 'Order'
              AND Status = 'Syncing';
            """;
        await recoveryCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureDeviceCacheColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "DeviceCache", cancellationToken);
        if (!columns.Contains("HardwareId"))
        {
            await ExecuteAsync(connection, "ALTER TABLE DeviceCache ADD COLUMN HardwareId TEXT NOT NULL DEFAULT '';", cancellationToken);
        }

        if (!columns.Contains("DeviceStatus"))
        {
            await ExecuteAsync(connection, "ALTER TABLE DeviceCache ADD COLUMN DeviceStatus INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!columns.Contains("Message"))
        {
            await ExecuteAsync(connection, "ALTER TABLE DeviceCache ADD COLUMN Message TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("AuthorizationCodeProtected"))
        {
            await ExecuteAsync(connection, "ALTER TABLE DeviceCache ADD COLUMN AuthorizationCodeProtected TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLocalSellableItemIndexColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalSellableItemIndex", cancellationToken);
        if (!columns.Contains("LookupCodeNormalized"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN LookupCodeNormalized TEXT;", cancellationToken);
        }

        if (!columns.Contains("ContentHash"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN ContentHash TEXT;", cancellationToken);
        }

        if (!columns.Contains("SyncedAt"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN SyncedAt TEXT;", cancellationToken);
        }

        if (!columns.Contains("ProductImage"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN ProductImage TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("DiscountRate"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN DiscountRate TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("IsSpecialProduct"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalSellableItemIndex ADD COLUMN IsSpecialProduct INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        await ExecuteAsync(
            connection,
            """
            UPDATE LocalSellableItemIndex
            SET LookupCodeNormalized = UPPER(TRIM(LookupCode))
            WHERE LookupCodeNormalized IS NULL OR TRIM(LookupCodeNormalized) = '';
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            UPDATE LocalSellableItemIndex
            SET ContentHash =
                StoreCode || '|' ||
                ProductCode || '|' ||
                IFNULL(ReferenceCode, '') || '|' ||
                DisplayName || '|' ||
                LookupCode || '|' ||
                IFNULL(ItemNumber, '') || '|' ||
                IFNULL(Barcode, '') || '|' ||
                RetailPrice || '|' ||
                PriceSource || '|' ||
                PriceSourceLabel || '|' ||
                QuantityFactor || '|' ||
                IFNULL(ProductImage, '') || '|' ||
                IFNULL(DiscountRate, '') || '|' ||
                IsSpecialProduct
            WHERE ContentHash IS NULL OR TRIM(ContentHash) = '';
            """,
            cancellationToken);

        await ExecuteAsync(
            connection,
            """
            UPDATE LocalSellableItemIndex
            SET SyncedAt = COALESCE(UpdatedAt, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            WHERE SyncedAt IS NULL OR TRIM(SyncedAt) = '';
            """,
            cancellationToken);
    }

    private static async Task EnsureLocalOrderColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalOrders", cancellationToken);
        if (!columns.Contains("TenderedAmount"))
        {
            // 对旧版本地库做无损补列，已有订单保持 NULL，避免迁移时改写历史数据。
            await ExecuteAsync(connection, "ALTER TABLE LocalOrders ADD COLUMN TenderedAmount TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("ChangeAmount"))
        {
            // 找零金额仅在本地展示链路使用，允许为空以兼容非现金与历史订单。
            await ExecuteAsync(connection, "ALTER TABLE LocalOrders ADD COLUMN ChangeAmount TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLocalOrderLineColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalOrderLines", cancellationToken);
        if (!columns.Contains("ItemNumber"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalOrderLines ADD COLUMN ItemNumber TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("Kind"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalOrderLines ADD COLUMN Kind INTEGER NOT NULL DEFAULT 1;", cancellationToken);
        }

        if (!columns.Contains("ReturnSourceKey"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalOrderLines ADD COLUMN ReturnSourceKey TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("OriginalOrderGuid"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalOrderLines ADD COLUMN OriginalOrderGuid TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("OriginalOrderDetailGuid"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalOrderLines ADD COLUMN OriginalOrderDetailGuid TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLocalPaymentColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalPayments", cancellationToken);
        if (!columns.Contains("IdempotencyKey"))
        {
            // 为已落库但尚未发券的退款支付保留幂等键，便于后续恢复时继续使用原键。
            await ExecuteAsync(connection, "ALTER TABLE LocalPayments ADD COLUMN IdempotencyKey TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLocalInstallmentColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalOrderInstallments", cancellationToken);
        if (!columns.Contains("CancellationInfoJson"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalOrderInstallments ADD COLUMN CancellationInfoJson TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLocalInstallmentOperationAttemptColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // 旧普通销售记录保持 Sale，避免升级后把既有终端交易误归到分期操作。
        foreach (var table in new[] { "LocalCardPaymentAttempts", "LocalSquarePaymentAttempts" })
        {
            var columns = await ReadColumnNamesAsync(connection, table, cancellationToken);
            if (!columns.Contains("OperationKind"))
            {
                await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN OperationKind TEXT NOT NULL DEFAULT 'Sale';", cancellationToken);
            }

            if (!columns.Contains("OperationGuid"))
            {
                await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN OperationGuid TEXT NULL;", cancellationToken);
            }

            if (!columns.Contains("SubmissionToken"))
            {
                await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN SubmissionToken TEXT NULL;", cancellationToken);
            }

            if (!columns.Contains("RefundBusinessKey"))
            {
                await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN RefundBusinessKey TEXT NULL;", cancellationToken);
            }
        }

        var operationColumns = await ReadColumnNamesAsync(connection, "LocalInstallmentOperations", cancellationToken);
        if (!operationColumns.Contains("ApiClaimToken"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalInstallmentOperations ADD COLUMN ApiClaimToken TEXT NULL;", cancellationToken);
        }

        if (!operationColumns.Contains("ApiClaimedAt"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalInstallmentOperations ADD COLUMN ApiClaimedAt TEXT NULL;", cancellationToken);
        }

        var refundStepColumns = await ReadColumnNamesAsync(connection, "LocalInstallmentRefundSteps", cancellationToken);
        if (!refundStepColumns.Contains("ProviderEnvironment"))
        {
            // Square refundId 只能在创建它的环境查询，升级旧库时以 nullable 列保持兼容。
            await ExecuteAsync(connection, "ALTER TABLE LocalInstallmentRefundSteps ADD COLUMN ProviderEnvironment TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLinklySettlementUploadColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LinklySettlementRecords", cancellationToken);
        if (!columns.Contains("UploadStatus"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN UploadStatus TEXT NOT NULL DEFAULT 'Pending';", cancellationToken);
        }

        if (!columns.Contains("PayloadRevision"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN PayloadRevision INTEGER NOT NULL DEFAULT 1;", cancellationToken);
        }

        if (!columns.Contains("UploadedRevision"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN UploadedRevision INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!columns.Contains("UploadAttemptCount"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN UploadAttemptCount INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!columns.Contains("NextUploadAt"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN NextUploadAt TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("LastUploadAttemptAt"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN LastUploadAttemptAt TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("UploadErrorCode"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN UploadErrorCode TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("UploadErrorMessage"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN UploadErrorMessage TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("UploadedAt"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LinklySettlementRecords ADD COLUMN UploadedAt TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("ProviderSubmissionState"))
        {
            await ExecuteAsync(
                connection,
                "ALTER TABLE LinklySettlementRecords ADD COLUMN ProviderSubmissionState TEXT NOT NULL DEFAULT 'Unknown';",
                cancellationToken);
        }

        await ExecuteAsync(
            connection,
            """
            UPDATE LinklySettlementRecords
            SET UploadStatus = 'Pending',
                PayloadRevision = CASE WHEN PayloadRevision <= 0 THEN 1 ELSE PayloadRevision END,
                UploadedRevision = CASE WHEN UploadedRevision < 0 THEN 0 ELSE UploadedRevision END
            WHERE UploadStatus IS NULL OR TRIM(UploadStatus) = '' OR PayloadRevision <= 0 OR UploadedRevision < 0;
            """,
            cancellationToken);

        // 旧记录只能依据已经持久化的 provider session 保守判定；没有 session 的记录仍保持 Unknown。
        await ExecuteAsync(
            connection,
            """
            UPDATE LinklySettlementRecords
            SET ProviderSubmissionState = 'Submitted'
            WHERE ProviderSessionId IS NOT NULL
              AND TRIM(ProviderSessionId) <> ''
              AND ProviderSubmissionState = 'Unknown';
            """,
            cancellationToken);

        // 旧客户端在 CloudBackendAsync 启动 provider session 前失败时已写入最终 Failed；这类记录可确定未提交。
        await ExecuteAsync(
            connection,
            """
            UPDATE LinklySettlementRecords
            SET ProviderSubmissionState = 'NotSubmitted'
            WHERE ConnectionMode = 'CloudBackendAsync'
              AND Status = 'Failed'
              AND ProviderSessionId IS NULL
              AND ProviderSubmissionState = 'Unknown';
            """,
            cancellationToken);

        // 仅修复已知旧版 CloudBackendAsync 预检失败误报，其他 Rejected/Failed 记录不得自动改写。
        await ExecuteAsync(
            connection,
            """
            UPDATE LinklySettlementRecords
            SET ProviderSubmissionState = 'NotSubmitted',
                UploadStatus = 'Pending',
                PayloadRevision = PayloadRevision + 1,
                UploadAttemptCount = 0,
                NextUploadAt = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                LastUploadAttemptAt = NULL,
                UploadErrorCode = NULL,
                UploadErrorMessage = NULL,
                UploadedAt = NULL
            WHERE ConnectionMode = 'CloudBackendAsync'
              AND Status = 'Failed'
              AND ProviderSessionId IS NULL
              AND UploadStatus = 'Rejected'
              AND UploadErrorCode = 'PROVIDER_SESSION_REQUIRED';
            """,
            cancellationToken);
    }

    private static async Task EnsureSuspendedOrderColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "SuspendedOrders", cancellationToken);
        if (!columns.Contains("FrozenPromotionRulesJson"))
        {
            await ExecuteAsync(
                connection,
                "ALTER TABLE SuspendedOrders ADD COLUMN FrozenPromotionRulesJson TEXT NULL;",
                cancellationToken);
        }
    }

    private static async Task EnsureSuspendedOrderLineColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "SuspendedOrderLines", cancellationToken);
        if (!columns.Contains("DiscountPercent"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN DiscountPercent TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("IsAutomaticPromotionDiscount"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN IsAutomaticPromotionDiscount INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!columns.Contains("DiscountSource"))
        {
            // 旧挂单没有折扣来源时按 None 恢复；新挂单会保存 Manual/Promotion。
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN DiscountSource INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!columns.Contains("Kind"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!columns.Contains("ReturnSourceKey"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN ReturnSourceKey TEXT NOT NULL DEFAULT '';", cancellationToken);
        }

        if (!columns.Contains("OriginalOrderGuid"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN OriginalOrderGuid TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("OriginalOrderDetailGuid"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN OriginalOrderDetailGuid TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("ReturnReason"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderLines ADD COLUMN ReturnReason TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("IsManualPrice"))
        {
            await ExecuteAsync(
                connection,
                "ALTER TABLE SuspendedOrderLines ADD COLUMN IsManualPrice INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }

        if (!columns.Contains("CatalogDiscountBasisPoints"))
        {
            // 旧挂单没有目录折扣基线时按 0 恢复，不把历史折扣误判成 Catalog。
            await ExecuteAsync(
                connection,
                "ALTER TABLE SuspendedOrderLines ADD COLUMN CatalogDiscountBasisPoints INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);
        }
    }

    /// <summary>
    /// 共享挂单 publication 队列与 durable claim/binding 表（只追加、幂等）：
    /// 本地 hold guid 唯一；本地 publication 状态与 iPad M40 对齐
    /// NeedsEvaluation/PendingPublish/Published/Blocked（Blocked 不自动重试）；
    /// Published 必须保存服务端 RemoteRevision/RemoteUpdatedAtIso，其他状态两者为空；
    /// PendingPublish/Published 必须携带非空 payload 密文；
    /// ConsumedAtIso 表示本地挂单已被成交订单消费，之后不可再 recall/评估/发布；
    /// claim 与 iPad SharedHeldOrderClaimSource/State 对齐：必带 HoldGuid 与
    /// Source（RemoteClaim/OfflineOrigin），prepare/activate/release 三把幂等键分离；
    /// SupersedeIdempotencyKey 供服务端 OfflineOrigin 成交调和（Prepared/Active -> Superseded）；
    /// 每 store+device 只允许一个 Prepared/Active fence（partial unique index）；
    /// activate/release 键全局唯一（partial unique index），状态一致性 CHECK 约束
    /// 各终态键与绑定关系；transition trigger 只允许 Prepared/Active 之间合法迁移，
    /// Superseded 保留 ActivateIdempotencyKey（Active 调和）且已绑定订单不可 supersede；
    /// 终态不可重开；payload 仅存密文。订单来源表与本地订单/outbox 同事务写入且不可变。
    /// </summary>
    private static async Task EnsureSharedHeldOrderSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // 旧开发库的 SharedHeldOrderClaims 缺 SupersedeIdempotencyKey（或仅被 ALTER ADD 补列），
        // 旧 CHECK/trigger/index 仍会阻止 Active -> Superseded；必须先事务化重建表。
        await MigrateLegacySharedHeldOrderClaimsAsync(connection, cancellationToken);

        await ExecuteAsync(
            connection,
            $$"""
            CREATE TABLE IF NOT EXISTS SharedHeldOrderPublications (
                LocalHoldGuid TEXT PRIMARY KEY,
                StoreCode TEXT NOT NULL,
                DeviceCode TEXT NOT NULL,
                Status TEXT NOT NULL CHECK (
                    Status IN ('NeedsEvaluation', 'PendingPublish', 'Published', 'Blocked')),
                Revision INTEGER NOT NULL DEFAULT 1 CHECK (Revision >= 1),
                RetryCount INTEGER NOT NULL DEFAULT 0 CHECK (RetryCount >= 0),
                ErrorCode TEXT NULL,
                ErrorMessage TEXT NULL,
                PayloadCiphertext BLOB NULL,
                HeldAtIso TEXT NOT NULL,
                CreatedAtIso TEXT NOT NULL,
                UpdatedAtIso TEXT NOT NULL,
                LastAttemptAtIso TEXT NULL,
                NextAttemptAtIso TEXT NULL,
                RemoteRevision INTEGER NULL,
                RemoteUpdatedAtIso TEXT NULL,
                ShareRequestedAtIso TEXT NULL,
                ConsumedAtIso TEXT NULL,
                PublicationPayloadVersion INTEGER NULL CHECK (
                    PublicationPayloadVersion IS NULL OR PublicationPayloadVersion IN (1, 2)),
                CHECK (TRIM(LocalHoldGuid) <> ''),
                CHECK (TRIM(StoreCode) <> ''),
                CHECK (TRIM(DeviceCode) <> ''),
                CHECK (ShareRequestedAtIso IS NULL OR
                    (TRIM(ShareRequestedAtIso) <> '' AND LENGTH(ShareRequestedAtIso) <= 64)),
                CHECK (
                    (Status IN ('PendingPublish', 'Published')
                        AND PayloadCiphertext IS NOT NULL
                        AND LENGTH(PayloadCiphertext) > 0)
                    OR Status IN ('NeedsEvaluation', 'Blocked')
                ),
                CHECK (
                    (Status = 'Published'
                        AND RemoteRevision IS NOT NULL
                        AND RemoteUpdatedAtIso IS NOT NULL
                        AND RemoteRevision >= 0)
                    OR (Status <> 'Published'
                        AND RemoteRevision IS NULL
                        AND RemoteUpdatedAtIso IS NULL)
                )
            );

            CREATE INDEX IF NOT EXISTS IX_SharedHeldOrderPublications_Due
                ON SharedHeldOrderPublications (Status, NextAttemptAtIso, UpdatedAtIso);

            CREATE TRIGGER IF NOT EXISTS TRG_SharedHeldOrderPublications_ShareRequestGate_Insert
            BEFORE INSERT
            ON SharedHeldOrderPublications
            FOR EACH ROW
            WHEN
                (NEW.ShareRequestedAtIso IS NOT NULL
                    AND (TRIM(NEW.ShareRequestedAtIso) = ''
                         OR LENGTH(NEW.ShareRequestedAtIso) > 64))
                OR
                -- fail-closed：未请求不得进入 PendingPublish/Published/普通 Blocked；
                -- LOCAL_DELETE_PENDING_LOCAL/REMOTE 删除暂存是唯一例外。
                ((NEW.Status IN ('PendingPublish', 'Published')
                    AND NEW.ShareRequestedAtIso IS NULL)
                 OR (NEW.Status = 'Blocked'
                    AND NEW.ShareRequestedAtIso IS NULL
                    AND COALESCE(NEW.ErrorCode, '') NOT IN (
                        'LOCAL_DELETE_PENDING_LOCAL',
                        'LOCAL_DELETE_PENDING_REMOTE')))
            BEGIN
                SELECT RAISE(ABORT, 'SHARED_HELD_ORDER_SHARE_REQUEST_REQUIRED');
            END;

            CREATE TRIGGER IF NOT EXISTS TRG_SharedHeldOrderPublications_ShareRequestGate_Update
            BEFORE UPDATE OF Status, ErrorCode, ShareRequestedAtIso
            ON SharedHeldOrderPublications
            FOR EACH ROW
            WHEN
                (NEW.ShareRequestedAtIso IS NOT NULL
                    AND (TRIM(NEW.ShareRequestedAtIso) = ''
                         OR LENGTH(NEW.ShareRequestedAtIso) > 64))
                OR ((NEW.Status IN ('PendingPublish', 'Published')
                    AND NEW.ShareRequestedAtIso IS NULL)
                 OR (NEW.Status = 'Blocked'
                    AND NEW.ShareRequestedAtIso IS NULL
                    AND COALESCE(NEW.ErrorCode, '') NOT IN (
                        'LOCAL_DELETE_PENDING_LOCAL',
                        'LOCAL_DELETE_PENDING_REMOTE')))
                -- 请求时间一旦非空不可改写或清空。
                OR (OLD.ShareRequestedAtIso IS NOT NULL
                    AND (NEW.ShareRequestedAtIso IS NULL
                         OR NEW.ShareRequestedAtIso <> OLD.ShareRequestedAtIso))
            BEGIN
                SELECT RAISE(ABORT, 'SHARED_HELD_ORDER_SHARE_REQUEST_REQUIRED_OR_IMMUTABLE');
            END;

            {{SharedHeldOrderClaimsTableStatement}}
            {{SharedHeldOrderClaimsIndexTriggerStatements}}

            CREATE TABLE IF NOT EXISTS LocalOrderHeldOrderSources (
                OrderGuid TEXT PRIMARY KEY,
                HoldGuid TEXT NOT NULL,
                ClaimGuid TEXT NULL,
                SourceKind INTEGER NOT NULL CHECK (SourceKind IN (1, 2)),
                CreatedAtIso TEXT NOT NULL,
                CHECK (TRIM(OrderGuid) <> ''),
                CHECK (TRIM(HoldGuid) <> ''),
                CHECK (ClaimGuid IS NULL OR TRIM(ClaimGuid) <> ''),
                CHECK (TRIM(CreatedAtIso) <> ''),
                CHECK (
                    (SourceKind = 1 AND ClaimGuid IS NOT NULL)
                    OR (SourceKind = 2 AND ClaimGuid IS NULL)
                )
            );

            CREATE INDEX IF NOT EXISTS IX_LocalOrderHeldOrderSources_Hold
                ON LocalOrderHeldOrderSources (HoldGuid);

            CREATE TRIGGER IF NOT EXISTS TRG_LocalOrderHeldOrderSources_Immutable
            BEFORE UPDATE ON LocalOrderHeldOrderSources
            FOR EACH ROW
            WHEN NEW.OrderGuid <> OLD.OrderGuid
              OR NEW.HoldGuid <> OLD.HoldGuid
              OR NEW.ClaimGuid IS NOT OLD.ClaimGuid
              OR NEW.SourceKind <> OLD.SourceKind
              OR NEW.CreatedAtIso <> OLD.CreatedAtIso
            BEGIN
                SELECT RAISE(ABORT, 'ORDER_HELD_ORDER_SOURCE_IMMUTABLE');
            END;
            """,
            cancellationToken);
    }

    /// <summary>
    /// SharedHeldOrderClaims 新表 DDL（含 SupersedeIdempotencyKey 与新状态一致性 CHECK）。
    /// 主建库路径与旧库迁移共用同一文本，避免两份 DDL 漂移。
    /// </summary>
    private const string SharedHeldOrderClaimsTableStatement =
        """
        CREATE TABLE IF NOT EXISTS SharedHeldOrderClaims (
            ClaimId TEXT PRIMARY KEY,
            HoldGuid TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            Source TEXT NOT NULL CHECK (
                Source IN ('RemoteClaim', 'OfflineOrigin')),
            Status TEXT NOT NULL CHECK (
                Status IN ('Prepared', 'Active', 'Completed', 'Released', 'Superseded')),
            PrepareIdempotencyKey TEXT NOT NULL UNIQUE,
            ActivateIdempotencyKey TEXT NULL,
            ReleaseIdempotencyKey TEXT NULL,
            SupersedeIdempotencyKey TEXT NULL,
            PayloadCiphertext BLOB NOT NULL,
            ServerRevision INTEGER NULL,
            ExpiresAtIso TEXT NULL,
            BoundOrderGuid TEXT NULL,
            CreatedAtIso TEXT NOT NULL,
            UpdatedAtIso TEXT NOT NULL,
            CHECK (TRIM(ClaimId) <> ''),
            CHECK (TRIM(HoldGuid) <> ''),
            CHECK (TRIM(StoreCode) <> ''),
            CHECK (TRIM(DeviceCode) <> ''),
            CHECK (TRIM(PrepareIdempotencyKey) <> ''),
            CHECK (
                ActivateIdempotencyKey IS NULL
                OR (TRIM(ActivateIdempotencyKey) <> '' AND LENGTH(ActivateIdempotencyKey) > 0)),
            CHECK (
                ReleaseIdempotencyKey IS NULL
                OR (TRIM(ReleaseIdempotencyKey) <> '' AND LENGTH(ReleaseIdempotencyKey) > 0)),
            CHECK (
                SupersedeIdempotencyKey IS NULL
                OR (TRIM(SupersedeIdempotencyKey) <> '' AND LENGTH(SupersedeIdempotencyKey) > 0)),
            CHECK (LENGTH(PayloadCiphertext) > 0),
            CHECK (ServerRevision IS NULL OR ServerRevision >= 0),
            CHECK (
                (Status = 'Prepared'
                    AND ActivateIdempotencyKey IS NULL
                    AND ReleaseIdempotencyKey IS NULL
                    AND BoundOrderGuid IS NULL
                    AND SupersedeIdempotencyKey IS NULL)
             OR (Status = 'Active'
                    AND ActivateIdempotencyKey IS NOT NULL
                    AND ReleaseIdempotencyKey IS NULL
                    AND SupersedeIdempotencyKey IS NULL)
             OR (Status = 'Completed'
                    AND ActivateIdempotencyKey IS NOT NULL
                    AND ReleaseIdempotencyKey IS NOT NULL
                    AND BoundOrderGuid IS NOT NULL
                    AND SupersedeIdempotencyKey IS NULL)
             OR (Status = 'Released'
                    AND ReleaseIdempotencyKey IS NOT NULL
                    AND BoundOrderGuid IS NULL
                    AND SupersedeIdempotencyKey IS NULL)
             OR (Status = 'Superseded'
                    AND ReleaseIdempotencyKey IS NULL
                    AND BoundOrderGuid IS NULL
                    AND SupersedeIdempotencyKey IS NOT NULL)
            )
        );
        """;

    /// <summary>
    /// SharedHeldOrderClaims 新索引与 trigger（fence/幂等键唯一、状态机与首次绑定规则）。
    /// </summary>
    private const string SharedHeldOrderClaimsIndexTriggerStatements =
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_SharedHeldOrderClaims_OpenFence_PerDevice
            ON SharedHeldOrderClaims (StoreCode, DeviceCode)
            WHERE Status IN ('Prepared', 'Active');

        CREATE UNIQUE INDEX IF NOT EXISTS UX_SharedHeldOrderClaims_ActivateKey
            ON SharedHeldOrderClaims (ActivateIdempotencyKey)
            WHERE ActivateIdempotencyKey IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS UX_SharedHeldOrderClaims_ReleaseKey
            ON SharedHeldOrderClaims (ReleaseIdempotencyKey)
            WHERE ReleaseIdempotencyKey IS NOT NULL;

        CREATE UNIQUE INDEX IF NOT EXISTS UX_SharedHeldOrderClaims_SupersedeKey
            ON SharedHeldOrderClaims (SupersedeIdempotencyKey)
            WHERE SupersedeIdempotencyKey IS NOT NULL;

        CREATE INDEX IF NOT EXISTS IX_SharedHeldOrderClaims_MineRecovery
            ON SharedHeldOrderClaims (StoreCode, DeviceCode, Status, UpdatedAtIso);

        CREATE TRIGGER IF NOT EXISTS TRG_SharedHeldOrderClaims_StatusMachine
        BEFORE UPDATE OF Status ON SharedHeldOrderClaims
        FOR EACH ROW
        WHEN NEW.Status <> OLD.Status
        BEGIN
            SELECT CASE
                WHEN OLD.Status = 'Prepared'
                    AND NEW.Status IN ('Active', 'Released')
                THEN 0
                WHEN OLD.Status = 'Prepared'
                    AND NEW.Status = 'Superseded'
                    AND NEW.ActivateIdempotencyKey IS NULL
                    AND NEW.ReleaseIdempotencyKey IS NULL
                    AND NEW.BoundOrderGuid IS NULL
                    AND NEW.SupersedeIdempotencyKey IS NOT NULL
                THEN 0
                WHEN OLD.Status = 'Active'
                    AND NEW.Status IN ('Completed', 'Released')
                THEN 0
                WHEN OLD.Status = 'Active'
                    AND NEW.Status = 'Superseded'
                    AND NEW.ActivateIdempotencyKey IS NOT NULL
                    AND NEW.ActivateIdempotencyKey = OLD.ActivateIdempotencyKey
                    AND NEW.ReleaseIdempotencyKey IS NULL
                    AND NEW.BoundOrderGuid IS NULL
                    AND NEW.SupersedeIdempotencyKey IS NOT NULL
                THEN 0
                ELSE RAISE(ABORT, 'illegal shared held order claim status transition')
            END;
        END;

        CREATE TRIGGER IF NOT EXISTS TRG_SharedHeldOrderClaims_ActiveBindingOnly
        BEFORE UPDATE ON SharedHeldOrderClaims
        FOR EACH ROW
        WHEN OLD.Status = 'Active' AND NEW.Status = 'Active'
        BEGIN
            SELECT CASE
                WHEN NEW.BoundOrderGuid IS NOT NULL AND OLD.BoundOrderGuid IS NULL THEN 0
                ELSE RAISE(ABORT, 'active claim may only change through first bind')
            END;
        END;
        """;

    /// <summary>
    /// 旧开发库 SharedHeldOrderClaims 迁移：缺 SupersedeIdempotencyKey（或仅被 ALTER ADD 补列、
    /// 旧 CHECK/trigger/index 仍在）时，事务化重建表。旧 CHECK 无法在 SQLite 中直接修改，
    /// 只能换名 -> 建新表 -> 全量复制 -> 删旧表 -> 重建索引/trigger；任一步失败整体回滚，
    /// 旧表与数据原样保留。旧 Superseded 行缺新 key 时按 ClaimId 派生稳定 migration-only key，
    /// 不静默丢行；无法满足新 CHECK 的行会使迁移失败并回滚，等待人工清理后重试。
    /// </summary>
    private static async Task MigrateLegacySharedHeldOrderClaimsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "SharedHeldOrderClaims", cancellationToken))
        {
            return;
        }

        // 新表的状态一致性 CHECK 必然包含该标记；旧表即使被 ALTER ADD 补列，
        // sqlite_master 里保存的仍是旧 CREATE TABLE（不含该 CHECK 文本）。
        var currentTableSql = await ReadObjectSqlAsync(
            connection,
            "table",
            "SharedHeldOrderClaims",
            cancellationToken);
        if (currentTableSql is not null
            && currentTableSql.Contains(
                "SupersedeIdempotencyKey IS NOT NULL",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var legacyColumns = await ReadColumnNamesAsync(
            connection,
            "SharedHeldOrderClaims",
            cancellationToken);
        var hasSupersedeKeyColumn = legacyColumns.Contains("SupersedeIdempotencyKey");
        var supersedeExpression = hasSupersedeKeyColumn
            ? """
              CASE WHEN Status = 'Superseded'
                     AND (SupersedeIdempotencyKey IS NULL OR TRIM(SupersedeIdempotencyKey) = '')
                   THEN 'migrated-supersede:' || ClaimId
                   ELSE SupersedeIdempotencyKey
              END
              """
            : """
              CASE WHEN Status = 'Superseded'
                   THEN 'migrated-supersede:' || ClaimId
                   ELSE NULL
              END
              """;

        await using var transaction = connection.BeginTransaction();
        try
        {
            // 1) 旧表先换名，释放真实表名与旧 index/trigger 全局名。
            await ExecuteAsync(
                connection,
                transaction,
                "ALTER TABLE SharedHeldOrderClaims RENAME TO SharedHeldOrderClaims_legacy;",
                cancellationToken);
            // 2) 按新 schema 建表（新 CHECK 立即可校验复制数据）。
            await ExecuteAsync(
                connection,
                transaction,
                SharedHeldOrderClaimsTableStatement,
                cancellationToken);
            // 3) 全量复制：所有列原值保留；旧 Superseded 行缺新 key 时生成稳定 migration-only key。
            //    无法满足新 CHECK 的行 -> SQLite 约束错误 -> 事务回滚，旧表完整保留。
            await ExecuteAsync(
                connection,
                transaction,
                $"""
                INSERT INTO SharedHeldOrderClaims (
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey, SupersedeIdempotencyKey,
                    PayloadCiphertext, ServerRevision, ExpiresAtIso, BoundOrderGuid, CreatedAtIso, UpdatedAtIso)
                SELECT
                    ClaimId, HoldGuid, StoreCode, DeviceCode, Source, Status,
                    PrepareIdempotencyKey, ActivateIdempotencyKey, ReleaseIdempotencyKey, {supersedeExpression},
                    PayloadCiphertext, ServerRevision, ExpiresAtIso, BoundOrderGuid, CreatedAtIso, UpdatedAtIso
                FROM SharedHeldOrderClaims_legacy;
                """,
                cancellationToken);
            // 4) 删旧表（连带旧 index/trigger），避免与新 index/trigger 同名冲突。
            await ExecuteAsync(
                connection,
                transaction,
                "DROP TABLE SharedHeldOrderClaims_legacy;",
                cancellationToken);
            // 5) 在同一事务内重建新索引/trigger，任何唯一约束失败也整体回滚。
            await ExecuteAsync(
                connection,
                transaction,
                SharedHeldOrderClaimsIndexTriggerStatements,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// 兼容早期开发库：publication 消费标记与显式共享请求时间为后补列，旧表通过无损
    /// ALTER 补齐；补齐后立即回填：PendingPublish/Published/Blocked 视为已请求
    /// （请求时间=UpdatedAtIso），NeedsEvaluation 留空（默认不评估发布，等待显式请求）。
    /// 回填必须在触发器创建前完成（EnsureSharedHeldOrderSchemaAsync 内建触发器）。
    /// claim 表的 supersede 幂等键改由事务化表重建迁移（见
    /// MigrateLegacySharedHeldOrderClaimsAsync），此处不再 ALTER ADD，避免留下旧 CHECK/trigger。
    /// </summary>
    private static async Task EnsureSharedHeldOrderConsumptionColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // 首次建库时这些表尚未创建（EnsureSharedHeldOrderSchemaAsync 在其后执行），
        // 仅对已存在的旧表做无损补列。
        if (await TableExistsAsync(connection, "SharedHeldOrderPublications", cancellationToken))
        {
            var publicationColumns = await ReadColumnNamesAsync(
                connection,
                "SharedHeldOrderPublications",
                cancellationToken);
            if (!publicationColumns.Contains("ConsumedAtIso"))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE SharedHeldOrderPublications ADD COLUMN ConsumedAtIso TEXT NULL;",
                    cancellationToken);
            }

            if (!publicationColumns.Contains("ShareRequestedAtIso"))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE SharedHeldOrderPublications ADD COLUMN ShareRequestedAtIso TEXT NULL;",
                    cancellationToken);
            }

            if (!publicationColumns.Contains("PublicationPayloadVersion"))
            {
                await ExecuteAsync(
                    connection,
                    "ALTER TABLE SharedHeldOrderPublications ADD COLUMN PublicationPayloadVersion INTEGER NULL CHECK (PublicationPayloadVersion IS NULL OR PublicationPayloadVersion IN (1, 2));",
                    cancellationToken);
            }

            // 幂等修复 ALTER 成功但回填前进程中断的数据库：旧已发布/待发布/阻断行
            // 按 UpdatedAtIso 回填请求时间；NeedsEvaluation 始终留空等待显式请求。
            await ExecuteAsync(
                connection,
                """
                UPDATE SharedHeldOrderPublications
                SET ShareRequestedAtIso = COALESCE(UpdatedAtIso, CreatedAtIso)
                WHERE ShareRequestedAtIso IS NULL
                  AND Status IN ('PendingPublish', 'Published', 'Blocked');
                """,
                cancellationToken);
        }
    }

    private static async Task EnsureLocalCardTransactionColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalCardTransactions", cancellationToken);
        if (!columns.Contains("RefundReference"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalCardTransactions ADD COLUMN RefundReference TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLocalCardPaymentAttemptColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalCardPaymentAttempts", cancellationToken);
        if (!columns.Contains("AcknowledgedAt"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalCardPaymentAttempts ADD COLUMN AcknowledgedAt TEXT NULL;", cancellationToken);
        }

        if (!columns.Contains("RecoveryPhase"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalCardPaymentAttempts ADD COLUMN RecoveryPhase TEXT NOT NULL DEFAULT 'None';", cancellationToken);
        }

        if (!columns.Contains("RecoveryTargetStatus"))
        {
            await ExecuteAsync(connection, "ALTER TABLE LocalCardPaymentAttempts ADD COLUMN RecoveryTargetStatus TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureLocalSquarePaymentAttemptColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "LocalSquarePaymentAttempts", cancellationToken);
        if (columns.Count == 0)
        {
            return;
        }

        var expectedColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CheckoutId"] = "TEXT NULL",
            ["IdempotencyKey"] = "TEXT NOT NULL DEFAULT ''",
            ["DeviceId"] = "TEXT NOT NULL DEFAULT ''",
            ["LocationId"] = "TEXT NOT NULL DEFAULT ''",
            ["Environment"] = "TEXT NOT NULL DEFAULT ''",
            ["Amount"] = "TEXT NOT NULL DEFAULT '0'",
            ["AmountCents"] = "INTEGER NOT NULL DEFAULT 0",
            ["Currency"] = "TEXT NOT NULL DEFAULT 'AUD'",
            ["Status"] = "TEXT NOT NULL DEFAULT 'Pending'",
            ["CheckoutStatus"] = "TEXT NULL",
            ["CancelReason"] = "TEXT NULL",
            ["OrderDraftJson"] = "TEXT NOT NULL DEFAULT ''",
            ["StoreCode"] = "TEXT NOT NULL DEFAULT ''",
            ["DeviceCode"] = "TEXT NOT NULL DEFAULT ''",
            ["CashierId"] = "TEXT NOT NULL DEFAULT ''",
            ["PaymentId"] = "TEXT NULL",
            ["PaymentStatus"] = "TEXT NULL",
            ["ResponseCode"] = "TEXT NULL",
            ["ResponseText"] = "TEXT NULL",
            ["CreatedAt"] = "TEXT NOT NULL DEFAULT ''",
            ["UpdatedAt"] = "TEXT NOT NULL DEFAULT ''",
            ["CompletedAt"] = "TEXT NULL",
            ["OrderCompletedAt"] = "TEXT NULL",
            ["ResolvedAt"] = "TEXT NULL",
            ["RecoveryPhase"] = "TEXT NOT NULL DEFAULT 'None'",
            ["RecoveryTargetStatus"] = "TEXT NULL",
            ["SupervisorFinancialReference"] = "TEXT NULL"
        };

        foreach (var (columnName, definition) in expectedColumns)
        {
            if (!columns.Contains(columnName))
            {
                await ExecuteAsync(connection, $"ALTER TABLE LocalSquarePaymentAttempts ADD COLUMN {columnName} {definition};", cancellationToken);
            }
        }
    }

    private static async Task EnsureSuspendedOrderReturnPaymentCapacityColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await ReadColumnNamesAsync(connection, "SuspendedOrderReturnPaymentCapacities", cancellationToken);
        if (!columns.Contains("OriginalOrderGuid"))
        {
            await ExecuteAsync(connection, "ALTER TABLE SuspendedOrderReturnPaymentCapacities ADD COLUMN OriginalOrderGuid TEXT NULL;", cancellationToken);
        }
    }

    private static async Task<string?> ReadObjectSqlAsync(
        SqliteConnection connection,
        string objectType,
        string objectName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT sql
            FROM sqlite_master
            WHERE type = $ObjectType AND name = $ObjectName;
            """;
        command.Parameters.AddWithValue("$ObjectType", objectType);
        command.Parameters.AddWithValue("$ObjectName", objectName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    private static async Task<HashSet<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $TableName;
            """;
        command.Parameters.AddWithValue("$TableName", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static readonly string[] TableStatements =
    [
        """
        CREATE TABLE IF NOT EXISTS DeviceCache (
            DeviceCode TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            StoreName TEXT NOT NULL,
            HardwareId TEXT NOT NULL DEFAULT '',
            DeviceStatus INTEGER NOT NULL DEFAULT 0,
            IsAllowed INTEGER NOT NULL,
            Message TEXT NULL,
            AuthorizationCodeProtected TEXT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS CashierCache (
            CashierId TEXT PRIMARY KEY,
            CashierName TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            RolesJson TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalSellableItemIndex (
            StoreCode TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            ReferenceCode TEXT NULL,
            DisplayName TEXT NOT NULL,
            LookupCode TEXT NOT NULL,
            LookupCodeNormalized TEXT NOT NULL,
            ItemNumber TEXT NULL,
            Barcode TEXT NULL,
            ProductImage TEXT NULL,
            DiscountRate TEXT NULL,
            IsSpecialProduct INTEGER NOT NULL DEFAULT 0,
            RetailPrice TEXT NOT NULL,
            PriceSource INTEGER NOT NULL,
            PriceSourceLabel TEXT NOT NULL,
            QuantityFactor TEXT NOT NULL,
            UpdatedAt TEXT NULL,
            ContentHash TEXT NOT NULL,
            SyncedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalOrders (
            OrderGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            CashierName TEXT NOT NULL,
            SoldAt TEXT NOT NULL,
            TotalAmount TEXT NOT NULL,
            DiscountAmount TEXT NOT NULL,
            ActualAmount TEXT NOT NULL,
            TenderedAmount TEXT NULL,
            ChangeAmount TEXT NULL,
            SyncStatus TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalOrderLines (
            OrderLineGuid TEXT PRIMARY KEY,
            OrderGuid TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            ReferenceCode TEXT NULL,
            DisplayName TEXT NOT NULL,
            LookupCode TEXT NOT NULL,
            ItemNumber TEXT NULL,
            Quantity TEXT NOT NULL,
            UnitPrice TEXT NOT NULL,
            DiscountAmount TEXT NOT NULL,
            ActualAmount TEXT NOT NULL,
            PriceSource INTEGER NOT NULL,
            Kind INTEGER NOT NULL DEFAULT 1,
            ReturnSourceKey TEXT NULL,
            OriginalOrderGuid TEXT NULL,
            OriginalOrderDetailGuid TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalPayments (
            PaymentGuid TEXT PRIMARY KEY,
            OrderGuid TEXT NOT NULL,
            Method INTEGER NOT NULL,
            Amount TEXT NOT NULL,
            Reference TEXT NULL,
            IdempotencyKey TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalCardTransactions (
            Id TEXT PRIMARY KEY,
            PaymentGuid TEXT NOT NULL,
            OrderGuid TEXT NOT NULL,
            Processor TEXT NOT NULL,
            TxnRef TEXT NULL,
            AuthCode TEXT NULL,
            CardType TEXT NULL,
            CardBin INTEGER NULL,
            MaskedCardNumber TEXT NULL,
            MerchantId TEXT NULL,
            ResponseCode TEXT NULL,
            ResponseText TEXT NULL,
            Stan TEXT NULL,
            BankDateTime TEXT NULL,
            Amount TEXT NOT NULL,
            ReceiptText TEXT NULL,
            RefundReference TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalCardPaymentAttempts (
            AttemptGuid TEXT PRIMARY KEY,
            SessionId TEXT NULL,
            TxnRef TEXT NULL,
            Processor TEXT NOT NULL,
            Environment TEXT NOT NULL,
            ConnectionMode TEXT NOT NULL,
            TxnType TEXT NOT NULL,
            Amount TEXT NOT NULL,
            Status TEXT NOT NULL,
            OrderDraftJson TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            ResponseCode TEXT NULL,
            ResponseText TEXT NULL,
            PaymentReference TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            CompletedAt TEXT NULL,
            AcknowledgedAt TEXT NULL,
            OperationKind TEXT NOT NULL DEFAULT 'Sale',
            OperationGuid TEXT NULL,
            SubmissionToken TEXT NULL,
            RefundBusinessKey TEXT NULL,
            RecoveryPhase TEXT NOT NULL DEFAULT 'None',
            RecoveryTargetStatus TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalSquarePaymentAttempts (
            AttemptGuid TEXT PRIMARY KEY,
            CheckoutId TEXT NULL,
            IdempotencyKey TEXT NOT NULL,
            DeviceId TEXT NOT NULL,
            LocationId TEXT NOT NULL,
            Environment TEXT NOT NULL,
            Amount TEXT NOT NULL,
            AmountCents INTEGER NOT NULL,
            Currency TEXT NOT NULL,
            Status TEXT NOT NULL,
            CheckoutStatus TEXT NULL,
            CancelReason TEXT NULL,
            OrderDraftJson TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            PaymentId TEXT NULL,
            PaymentStatus TEXT NULL,
            ResponseCode TEXT NULL,
            ResponseText TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            CompletedAt TEXT NULL,
            OrderCompletedAt TEXT NULL,
            ResolvedAt TEXT NULL,
            OperationKind TEXT NOT NULL DEFAULT 'Sale',
            OperationGuid TEXT NULL,
            SubmissionToken TEXT NULL,
            RefundBusinessKey TEXT NULL,
            RecoveryPhase TEXT NOT NULL DEFAULT 'None',
            RecoveryTargetStatus TEXT NULL,
            SupervisorFinancialReference TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalInstallmentOperations (
            OperationGuid TEXT PRIMARY KEY,
            Kind TEXT NOT NULL,
            InstallmentGuid TEXT NOT NULL,
            PaymentGuid TEXT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            IdempotencyKey TEXT NOT NULL,
            RequestJson TEXT NOT NULL,
            State TEXT NOT NULL,
            TerminalAttemptGuid TEXT NULL,
            TerminalProcessor TEXT NULL,
            ResponseJson TEXT NULL,
            FailureMessage TEXT NULL,
            ApiClaimToken TEXT NULL,
            ApiClaimedAt TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalInstallmentRefundSteps (
            RefundStepGuid TEXT PRIMARY KEY,
            OperationGuid TEXT NOT NULL,
            OriginalPaymentGuid TEXT NOT NULL,
            Method INTEGER NOT NULL,
            Amount TEXT NOT NULL,
            OriginalReference TEXT NULL,
            IdempotencyKey TEXT NOT NULL,
            State TEXT NOT NULL,
            RefundReference TEXT NULL,
            ProviderEnvironment TEXT NULL,
            CardTransactionsJson TEXT NULL,
            FailureMessage TEXT NULL,
            SupervisorDecision TEXT NULL,
            SupervisorUserId TEXT NULL,
            SupervisorReason TEXT NULL,
            SupervisorEvidence TEXT NULL,
            ResolvedAt TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (OperationGuid) REFERENCES LocalInstallmentOperations(OperationGuid)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalFinancialSupervisorResolutions (
            ResolutionGuid TEXT PRIMARY KEY,
            Target TEXT NOT NULL,
            Processor TEXT NOT NULL,
            Environment TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            AttemptGuid TEXT NULL,
            RefundStepGuid TEXT NULL,
            OperationGuid TEXT NULL,
            SessionId TEXT NULL,
            Decision TEXT NOT NULL,
            OperatorCashierId TEXT NOT NULL,
            OperatorUserGuid TEXT NULL,
            OperatorName TEXT NULL,
            Reason TEXT NOT NULL,
            Evidence TEXT NULL,
            FinancialReference TEXT NULL,
            RetryReference TEXT NULL,
            ResolvedAt TEXT NOT NULL,
            AuditEventId TEXT NOT NULL,
            AuditPayloadJson TEXT NOT NULL,
            AuditPersistedAt TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalOrderInstallments (
            OrderGuid TEXT PRIMARY KEY,
            InstallmentGuid TEXT NOT NULL,
            InstallmentNumber TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            CashierName TEXT NOT NULL,
            CustomerName TEXT NOT NULL,
            CustomerPhone TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            TotalAmount TEXT NOT NULL,
            MinimumDownPayment TEXT NOT NULL,
            DownPaymentAmount TEXT NOT NULL,
            PaidAmount TEXT NOT NULL,
            BalanceAmount TEXT NOT NULL,
            Status INTEGER NOT NULL,
              LinesJson TEXT NOT NULL,
              PaymentsJson TEXT NOT NULL,
              PickupInfoJson TEXT NULL,
              CancellationInfoJson TEXT NULL,
              Note TEXT NULL
          );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalDailyCloses (
            DailyCloseGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            CashierName TEXT NOT NULL,
            BusinessDate TEXT NOT NULL,
            PeriodFrom TEXT NOT NULL,
            PeriodTo TEXT NOT NULL,
            SavedAt TEXT NOT NULL,
            OrderCount INTEGER NOT NULL,
            CashSalesAmount TEXT NOT NULL,
            CashRefundAmount TEXT NOT NULL,
            CashNetAmount TEXT NOT NULL,
            CardSalesAmount TEXT NOT NULL,
            CardRefundAmount TEXT NOT NULL,
            CardNetAmount TEXT NOT NULL,
            VoucherSalesAmount TEXT NOT NULL,
            VoucherRefundAmount TEXT NOT NULL,
            VoucherNetAmount TEXT NOT NULL,
            RefundAmount TEXT NOT NULL,
            ReturnQuantity TEXT NOT NULL,
            NoteSubtotal TEXT NOT NULL,
            CoinSubtotal TEXT NOT NULL,
            CountedCashAmount TEXT NOT NULL,
            CashDifference TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalDailyCloseCashCounts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            DailyCloseGuid TEXT NOT NULL,
            DenominationValue TEXT NOT NULL,
            Label TEXT NOT NULL,
            Kind INTEGER NOT NULL,
            Quantity INTEGER NOT NULL,
            Amount TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LinklySettlementRecords (
            SettlementGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            BusinessDate TEXT NOT NULL,
            ConnectionMode TEXT NOT NULL,
            Environment TEXT NOT NULL,
            ProviderSessionId TEXT NULL,
            Status TEXT NOT NULL,
            ResponseCode TEXT NULL,
            ResponseText TEXT NULL,
            SettlementData TEXT NULL,
            ReceiptTextsJson TEXT NOT NULL,
            RequestedAt TEXT NOT NULL,
            CompletedAt TEXT NULL,
            FirstPrintedAt TEXT NULL,
            LastPrintedAt TEXT NULL,
            PrintCount INTEGER NOT NULL DEFAULT 0,
            LastPrintError TEXT NULL,
            UploadStatus TEXT NOT NULL DEFAULT 'Pending',
            PayloadRevision INTEGER NOT NULL DEFAULT 1,
            UploadedRevision INTEGER NOT NULL DEFAULT 0,
            UploadAttemptCount INTEGER NOT NULL DEFAULT 0,
            NextUploadAt TEXT NULL,
            LastUploadAttemptAt TEXT NULL,
            UploadErrorCode TEXT NULL,
            UploadErrorMessage TEXT NULL,
            UploadedAt TEXT NULL,
            ProviderSubmissionState TEXT NOT NULL DEFAULT 'Unknown'
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS SyncQueue (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            EntityId TEXT NOT NULL,
            EntityType TEXT NOT NULL,
            Status TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            LastTriedAt TEXT NULL,
            ErrorMessage TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalShifts (
            ShiftGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            OpenedAt TEXT NOT NULL,
            ClosedAt TEXT NULL,
            OpeningCash TEXT NOT NULL,
            ClosingCash TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS AppSettings (
            Key TEXT PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalSpecialProductSortOrder (
            StoreCode TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            SortOrder INTEGER NOT NULL,
            UpdatedAt TEXT NOT NULL,
            PRIMARY KEY (StoreCode, ProductCode)
        );
        """,
        """
        -- 本地商品目录对应的服务端目录版本（sync-plan 协议的增量基准）。与目录数据在同一事务里写入，
        -- 没有记录表示本地数据不对应任何已知版本，下次同步走全量。
        CREATE TABLE IF NOT EXISTS LocalCatalogSyncState (
            StoreCode TEXT NOT NULL PRIMARY KEY,
            CatalogVersion TEXT NOT NULL,
            ItemCount INTEGER NOT NULL,
            UpdatedAt TEXT NOT NULL
        );
        """,
        """
        -- 同一查询码对应多个不同商品的全部候选（服务端 code-conflicts 下发，按门店整体替换）。
        -- 主目录表对查询码唯一，备选商品只能放这里；列与 LocalSellableItemIndex 一致以复用读写映射。
        CREATE TABLE IF NOT EXISTS LocalSellableItemCodeConflict (
            StoreCode TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            ReferenceCode TEXT NULL,
            DisplayName TEXT NOT NULL,
            LookupCode TEXT NOT NULL,
            LookupCodeNormalized TEXT NOT NULL,
            ItemNumber TEXT NULL,
            Barcode TEXT NULL,
            ProductImage TEXT NULL,
            DiscountRate TEXT NULL,
            IsSpecialProduct INTEGER NOT NULL DEFAULT 0,
            RetailPrice TEXT NOT NULL,
            PriceSource INTEGER NOT NULL,
            PriceSourceLabel TEXT NOT NULL,
            QuantityFactor TEXT NOT NULL,
            UpdatedAt TEXT NULL,
            ContentHash TEXT NOT NULL,
            SyncedAt TEXT NOT NULL,
            SortOrder INTEGER NOT NULL,
            PRIMARY KEY (StoreCode, LookupCodeNormalized, ProductCode)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalPromotionRules (
            StoreCode TEXT NOT NULL,
            PromotionId TEXT NOT NULL,
            Name TEXT NOT NULL,
            IsExclusive INTEGER NOT NULL,
            Priority INTEGER NOT NULL,
            ApplyQuantity INTEGER NOT NULL,
            FixedPrice TEXT NOT NULL,
            MaxApplicationsPerOrder INTEGER NULL,
            EffectiveStart TEXT NOT NULL,
            EffectiveEnd TEXT NOT NULL,
            UpdatedAt TEXT NULL,
            SyncedAt TEXT NOT NULL,
            PRIMARY KEY (StoreCode, PromotionId)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalPromotions (
            StoreCode TEXT NOT NULL,
            PromotionId TEXT NOT NULL,
            Name TEXT NOT NULL,
            IsExclusive INTEGER NOT NULL,
            Priority INTEGER NOT NULL,
            ApplyQuantity INTEGER NOT NULL,
            FixedPrice TEXT NOT NULL,
            MaxApplicationsPerOrder INTEGER NULL,
            EffectiveStart TEXT NOT NULL,
            EffectiveEnd TEXT NOT NULL,
            SyncedAt TEXT NOT NULL,
            PRIMARY KEY (StoreCode, PromotionId)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS LocalPromotionProducts (
            StoreCode TEXT NOT NULL,
            PromotionId TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            UnitWeight INTEGER NOT NULL,
            PRIMARY KEY (StoreCode, PromotionId, ProductCode)
        );
        """,
        """
        -- LocalPromotions 的商品明细。此前 promotion 链路误用了 catalog 链路的
        -- LocalPromotionProducts，两条链路各自“按门店全删再全插”，在同一次同步里互相清空。
        CREATE TABLE IF NOT EXISTS LocalPromotionRuleProducts (
            StoreCode TEXT NOT NULL,
            PromotionId TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            UnitWeight INTEGER NOT NULL,
            PRIMARY KEY (StoreCode, PromotionId, ProductCode)
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS SuspendedOrders (
            SuspendedOrderGuid TEXT PRIMARY KEY,
            StoreCode TEXT NOT NULL,
            DeviceCode TEXT NOT NULL,
            CashierId TEXT NOT NULL,
            CashierName TEXT NOT NULL,
            SuspendedAt TEXT NOT NULL,
            TotalAmount TEXT NOT NULL,
            DiscountAmount TEXT NOT NULL,
            ActualAmount TEXT NOT NULL,
            Status INTEGER NOT NULL,
            FrozenPromotionRulesJson TEXT NULL
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS SuspendedOrderLines (
            SuspendedOrderLineGuid TEXT PRIMARY KEY,
            SuspendedOrderGuid TEXT NOT NULL,
            StoreCode TEXT NOT NULL,
            ProductCode TEXT NOT NULL,
            ReferenceCode TEXT NULL,
            DisplayName TEXT NOT NULL,
            LookupCode TEXT NOT NULL,
            ItemNumber TEXT NULL,
            ProductImage TEXT NULL,
            Quantity TEXT NOT NULL,
            UnitPrice TEXT NOT NULL,
            DiscountAmount TEXT NOT NULL,
            DiscountPercent TEXT NULL,
            IsAutomaticPromotionDiscount INTEGER NOT NULL DEFAULT 0,
            DiscountSource INTEGER NOT NULL DEFAULT 0,
            ActualAmount TEXT NOT NULL,
            PriceSource INTEGER NOT NULL,
            PriceSourceLabel TEXT NOT NULL,
            Kind INTEGER NOT NULL DEFAULT 0,
            ReturnSourceKey TEXT NOT NULL DEFAULT '',
            OriginalOrderGuid TEXT NULL,
            OriginalOrderDetailGuid TEXT NULL,
            ReturnReason TEXT NULL,
            IsManualPrice INTEGER NOT NULL DEFAULT 0,
            CatalogDiscountBasisPoints INTEGER NOT NULL DEFAULT 0
        );
        """,
        """
        CREATE TABLE IF NOT EXISTS SuspendedOrderReturnPaymentCapacities (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SuspendedOrderGuid TEXT NOT NULL,
            Method INTEGER NOT NULL,
            OriginalAmount TEXT NOT NULL,
            RefundedAmount TEXT NOT NULL,
            RemainingAmount TEXT NOT NULL,
            Reference TEXT NULL,
            CardTransactionsJson TEXT NULL,
            OriginalOrderGuid TEXT NULL
        );
        """
    ];

    private static readonly string[] IndexStatements =
    [
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalSellableItemIndex_Store_LookupCodeNormalized
        ON LocalSellableItemIndex (StoreCode, LookupCodeNormalized);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalSellableItemIndex_Lookup
        ON LocalSellableItemIndex (StoreCode, LookupCode, Barcode, ItemNumber);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalSellableItemIndex_Store_Special_Product
        ON LocalSellableItemIndex (StoreCode, IsSpecialProduct, ProductCode);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalPromotionProducts_Store_Product
        ON LocalPromotionProducts (StoreCode, ProductCode);
        """,
        """
        -- 注意：本索引名中的 LocalPromotions 与它实际所在的 LocalPromotionRules 不符，
        -- 属历史命名瑕疵。此处不改名，避免为删除旧索引引入迁移步骤。
        CREATE INDEX IF NOT EXISTS IX_LocalPromotions_Store_EffectiveRange
        ON LocalPromotionRules (StoreCode, EffectiveStart, EffectiveEnd);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalPromotionsTable_Store_EffectiveRange
        ON LocalPromotions (StoreCode, EffectiveStart, EffectiveEnd);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalPromotionRuleProducts_Store_ProductCode
        ON LocalPromotionRuleProducts (StoreCode, ProductCode);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalPromotionProducts_Store_ProductCode
        ON LocalPromotionProducts (StoreCode, ProductCode);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalOrderLines_OrderGuid_ItemNumber_LookupCode
        ON LocalOrderLines (OrderGuid, ItemNumber, LookupCode);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalCardTransactions_PaymentGuid
        ON LocalCardTransactions (PaymentGuid);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalCardTransactions_OrderGuid
        ON LocalCardTransactions (OrderGuid);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalCardPaymentAttempts_RecoverLatest
        ON LocalCardPaymentAttempts (StoreCode, DeviceCode, CashierId, Environment, Status, UpdatedAt DESC, CreatedAt DESC);
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalCardPaymentAttempts_OpenRefundBusinessKey
        ON LocalCardPaymentAttempts (RefundBusinessKey)
        WHERE OperationKind = 'Refund'
          AND RefundBusinessKey IS NOT NULL
          AND Status NOT IN ('Declined', 'TimedOut', 'Cancelled', 'Failed', 'OrderCompleted', 'Abandoned');
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalCardPaymentAttempts_ActiveSession
        ON LocalCardPaymentAttempts (Environment, StoreCode, DeviceCode, SessionId)
        WHERE OperationKind = 'ActiveSession'
          AND SessionId IS NOT NULL;
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalSquarePaymentAttempts_RecoverLatest
        ON LocalSquarePaymentAttempts (StoreCode, DeviceCode, CashierId, Environment, Status, UpdatedAt DESC, CreatedAt DESC);
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalSquarePaymentAttempts_OpenRefundBusinessKey
        ON LocalSquarePaymentAttempts (RefundBusinessKey)
        WHERE OperationKind = 'Refund'
          AND RefundBusinessKey IS NOT NULL
          AND Status NOT IN ('Canceled', 'TimedOut', 'Failed', 'OrderCompleted', 'Abandoned');
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalSquarePaymentAttempts_CheckoutId
        ON LocalSquarePaymentAttempts (CheckoutId);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalInstallmentOperations_Recover
        ON LocalInstallmentOperations (StoreCode, State, UpdatedAt, CreatedAt);
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalInstallmentOperations_Kind_Installment_Payment_Idempotency
        ON LocalInstallmentOperations (Kind, InstallmentGuid, PaymentGuid, IdempotencyKey);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalInstallmentRefundSteps_Operation_State
        ON LocalInstallmentRefundSteps (OperationGuid, State, UpdatedAt);
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalFinancialSupervisorResolutions_AuditEventId
        ON LocalFinancialSupervisorResolutions (AuditEventId);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalFinancialSupervisorResolutions_Attempt_ResolvedAt
        ON LocalFinancialSupervisorResolutions (AttemptGuid, ResolvedAt);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalFinancialSupervisorResolutions_RefundStep_ResolvedAt
        ON LocalFinancialSupervisorResolutions (RefundStepGuid, ResolvedAt);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalFinancialSupervisorResolutions_AuditPending
        ON LocalFinancialSupervisorResolutions (AuditPersistedAt, ResolvedAt);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalOrders_Store_Device_SoldAt
        ON LocalOrders (StoreCode, DeviceCode, SoldAt);
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalOrderInstallments_InstallmentGuid
        ON LocalOrderInstallments (InstallmentGuid);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalOrderInstallments_Store_Status_CreatedAt
        ON LocalOrderInstallments (StoreCode, Status, CreatedAt DESC);
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LocalOrderInstallments_InstallmentNumber
        ON LocalOrderInstallments (InstallmentNumber);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalDailyCloses_Store_Device_BusinessDate_SavedAt
        ON LocalDailyCloses (StoreCode, DeviceCode, BusinessDate, SavedAt DESC);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LocalDailyCloseCashCounts_DailyCloseGuid
        ON LocalDailyCloseCashCounts (DailyCloseGuid);
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LinklySettlementRecords_ProviderSessionId
        ON LinklySettlementRecords (ProviderSessionId)
        WHERE ProviderSessionId IS NOT NULL;
        """,
        """
        CREATE UNIQUE INDEX IF NOT EXISTS UX_LinklySettlementRecords_Store_Device_BusinessDate_Unresolved
        ON LinklySettlementRecords (StoreCode, DeviceCode, BusinessDate)
        WHERE Status IN ('Pending', 'Unknown');
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LinklySettlementRecords_Store_Device_BusinessDate_RequestedAt
        ON LinklySettlementRecords (StoreCode, DeviceCode, BusinessDate, RequestedAt DESC);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_LinklySettlementRecords_UploadDue
        ON LinklySettlementRecords (UploadStatus, NextUploadAt, RequestedAt);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_SuspendedOrders_Store_Status_SuspendedAt
        ON SuspendedOrders (StoreCode, Status, SuspendedAt);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_SuspendedOrderLines_Order_ItemNumber_LookupCode
        ON SuspendedOrderLines (SuspendedOrderGuid, ItemNumber, LookupCode);
        """,
        """
        CREATE INDEX IF NOT EXISTS IX_SuspendedOrderReturnPaymentCapacities_Order
        ON SuspendedOrderReturnPaymentCapacities (SuspendedOrderGuid);
        """
    ];
}
