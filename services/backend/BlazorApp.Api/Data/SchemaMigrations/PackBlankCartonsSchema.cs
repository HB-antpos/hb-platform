namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 扫码打包「空箱标」（迁移 20261115.001-pack-blank-cartons）：批量预先创建、预先打印标签的箱。
/// 新表 PackCartonBatch（批次，ClientRequestId 唯一用于幂等重放），PackCarton 新增五个可空列
/// （BatchGUID / BatchSeq / ActivatedAtUtc / LabelPrintCount / LabelPrintedAtUtc）与按 BatchGUID 的过滤索引。
/// 只新增，不改任何既有列、不写任何数据；旧版程序不读不写这些列，回滚镜像不受影响。
/// 回退 = 先回滚主 API 镜像，再 DROP INDEX IX_PackCarton_Batch、DROP 五列、DROP TABLE PackCartonBatch 并移除迁移账本行。
/// 走独立版本号迁移，生产须显式执行 --schema=migrate，且要先于新版主 API 上线；实体不进 SqlSugarContext 的自动建表清单。
/// 错误号 54484–54486 紧接拣货记录所装箱列的 54483，只出现在 VerifySql。
/// </summary>
internal static class PackBlankCartonsSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    -- 幂等：表 / 列 / 索引已存在就跳过，可重复执行。
    IF OBJECT_ID(N'dbo.PackCartonBatch', N'U') IS NULL
    BEGIN
        CREATE TABLE [dbo].[PackCartonBatch]
        (
            [BatchGUID] nvarchar(50) NOT NULL,
            [OwnerCode] nvarchar(50) NOT NULL,
            [Count] int NOT NULL,
            [FirstCartonNo] nvarchar(20) NOT NULL,
            [LastCartonNo] nvarchar(20) NOT NULL,
            [DestinationType] int NOT NULL,
            [DestinationCode] nvarchar(50) NULL,
            [CarrierGUID] nvarchar(32) NULL,
            [ClientRequestId] uniqueidentifier NOT NULL,
            [CreatedAtUtc] datetime2 NOT NULL,
            [CreatedByUserGuid] nvarchar(50) NOT NULL,
            [CreatedByName] nvarchar(100) NOT NULL,
            CONSTRAINT [PK_PackCartonBatch] PRIMARY KEY CLUSTERED ([BatchGUID])
        );
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCartonBatch')
          AND [name] = N'UX_PackCartonBatch_ClientRequestId'
    )
    BEGIN
        -- 同一个请求编号重放时靠它找回已建的批次，不会重复建箱。
        CREATE UNIQUE NONCLUSTERED INDEX [UX_PackCartonBatch_ClientRequestId] ON [dbo].[PackCartonBatch] ([ClientRequestId]);
    END;
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCartonBatch')
          AND [name] = N'IX_PackCartonBatch_Owner_Created'
    )
    BEGIN
        CREATE NONCLUSTERED INDEX [IX_PackCartonBatch_Owner_Created] ON [dbo].[PackCartonBatch] ([OwnerCode], [CreatedAtUtc] DESC);
    END;

    IF COL_LENGTH(N'dbo.PackCarton', N'BatchGUID') IS NULL
    BEGIN
        ALTER TABLE [dbo].[PackCarton] ADD [BatchGUID] nvarchar(50) NULL;
    END;
    IF COL_LENGTH(N'dbo.PackCarton', N'BatchSeq') IS NULL
    BEGIN
        ALTER TABLE [dbo].[PackCarton] ADD [BatchSeq] int NULL;
    END;
    IF COL_LENGTH(N'dbo.PackCarton', N'ActivatedAtUtc') IS NULL
    BEGIN
        ALTER TABLE [dbo].[PackCarton] ADD [ActivatedAtUtc] datetime2 NULL;
    END;
    IF COL_LENGTH(N'dbo.PackCarton', N'LabelPrintCount') IS NULL
    BEGIN
        ALTER TABLE [dbo].[PackCarton] ADD [LabelPrintCount] int NULL;
    END;
    IF COL_LENGTH(N'dbo.PackCarton', N'LabelPrintedAtUtc') IS NULL
    BEGIN
        ALTER TABLE [dbo].[PackCarton] ADD [LabelPrintedAtUtc] datetime2 NULL;
    END;

    -- 新增列与过滤索引放进动态 SQL：同一批里刚 ADD 的列在编译期还不可见。
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [object_id] = OBJECT_ID(N'dbo.PackCarton')
          AND [name] = N'IX_PackCarton_Batch'
    )
    BEGIN
        EXEC (N'CREATE NONCLUSTERED INDEX [IX_PackCarton_Batch] ON [dbo].[PackCarton] ([BatchGUID], [BatchSeq]) WHERE [BatchGUID] IS NOT NULL');
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：批次表与新增列必须存在、类型 / 长度 / 可空一致，批次主键、请求编号唯一索引与 BatchGUID 过滤索引都要在；
    // 缺失或漂移只能由显式迁移修复。nvarchar 的 sys.columns.max_length 是字节数（50 → 100）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.PackCartonBatch', N'U') IS NULL
 OR OBJECT_ID(N'dbo.PackCarton', N'U') IS NULL
    THROW 54484, N'Pack blank cartons tables are missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'PackCartonBatch', N'BatchGUID', N'nvarchar', 100, 0),
      (N'PackCartonBatch', N'OwnerCode', N'nvarchar', 100, 0),
      (N'PackCartonBatch', N'Count', N'int', 0, 0),
      (N'PackCartonBatch', N'FirstCartonNo', N'nvarchar', 40, 0),
      (N'PackCartonBatch', N'LastCartonNo', N'nvarchar', 40, 0),
      (N'PackCartonBatch', N'DestinationType', N'int', 0, 0),
      (N'PackCartonBatch', N'DestinationCode', N'nvarchar', 100, 1),
      (N'PackCartonBatch', N'CarrierGUID', N'nvarchar', 64, 1),
      (N'PackCartonBatch', N'ClientRequestId', N'uniqueidentifier', 0, 0),
      (N'PackCartonBatch', N'CreatedAtUtc', N'datetime2', 0, 0),
      (N'PackCartonBatch', N'CreatedByUserGuid', N'nvarchar', 100, 0),
      (N'PackCartonBatch', N'CreatedByName', N'nvarchar', 200, 0),
      (N'PackCarton', N'BatchGUID', N'nvarchar', 100, 1),
      (N'PackCarton', N'BatchSeq', N'int', 0, 1),
      (N'PackCarton', N'ActivatedAtUtc', N'datetime2', 0, 1),
      (N'PackCarton', N'LabelPrintCount', N'int', 0, 1),
      (N'PackCarton', N'LabelPrintedAtUtc', N'datetime2', 0, 1)
    ) expected([table_name], [column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.tables t ON t.[name] = expected.[table_name] AND SCHEMA_NAME(t.[schema_id]) = N'dbo'
    LEFT JOIN sys.columns c ON c.[object_id] = t.[object_id] AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR (expected.[max_length] <> 0 AND c.[max_length] <> expected.[max_length])
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54485, N'Pack blank cartons column signature is incompatible.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE [parent_object_id] = OBJECT_ID(N'dbo.PackCartonBatch') AND [type] = N'PK')
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.PackCartonBatch')
      AND [name] = N'UX_PackCartonBatch_ClientRequestId' AND [is_unique] = 1
 )
 OR NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'dbo.PackCarton')
      AND [name] = N'IX_PackCarton_Batch'
 )
    THROW 54486, N'Pack blank cartons index signature is incompatible.', 1;
""";
}
