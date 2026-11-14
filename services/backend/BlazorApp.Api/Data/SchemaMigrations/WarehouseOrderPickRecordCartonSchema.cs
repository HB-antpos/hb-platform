namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 仓库拣货记录加「所装箱」列（迁移 20261114.002-pick-record-carton）：WarehouseOrderPickRecord.CartonGUID nvarchar(50) NULL。
/// 边拣边装时每条拣货记录同时记下同步装进的箱；同一请求编号重放时靠它回显上次装的箱，不会再装一次。
/// 只新增一个可空列，不改任何既有列、不写任何数据；旧版程序不读不写这一列，回滚镜像不受影响。
/// 回退 = 先回滚主 API 镜像，再 ALTER TABLE ... DROP COLUMN CartonGUID 并移除迁移账本行。
/// 走独立版本号迁移，生产须显式执行 --schema=migrate，且要先于新版主 API 上线；实体不进 SqlSugarContext 的自动建表清单。
/// 错误号 54483 紧接扫码打包的 54480–54482，只出现在 VerifySql。
/// </summary>
internal static class WarehouseOrderPickRecordCartonSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

BEGIN TRANSACTION;
BEGIN TRY
    -- 幂等：列已存在就跳过，可重复执行。
    IF COL_LENGTH(N'dbo.WarehouseOrderPickRecord', N'CartonGUID') IS NULL
    BEGIN
        ALTER TABLE [dbo].[WarehouseOrderPickRecord] ADD [CartonGUID] nvarchar(50) NULL;
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：列必须存在、nvarchar(50)（sys.columns.max_length 为字节数 100）、可空；缺列或类型漂移都只能由显式迁移修复。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.WarehouseOrderPickRecord', N'U') IS NULL
 OR NOT EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[object_id] = OBJECT_ID(N'dbo.WarehouseOrderPickRecord')
      AND c.[name] = N'CartonGUID'
      AND ty.[name] = N'nvarchar'
      AND c.[max_length] = 100
      AND c.[is_nullable] = 1
 )
    THROW 54483, N'Warehouse order pick record carton column is missing or incompatible.', 1;
""";
}
