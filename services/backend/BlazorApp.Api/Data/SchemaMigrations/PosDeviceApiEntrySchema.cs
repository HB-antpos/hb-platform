namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 设备实际走的 POS API 入口回显：POSM 库 dbo.POSM_设备注册信息表 新增
/// LastApiEntry nvarchar(16) NULL（edge / aws / direct，由 POS API 读源站 nginx 传来的 X-HB-Entry 回写）与
/// LastApiEntryAt datetime2 NULL（最近写入时间，UTC）。只用于后台设备管理页展示，不参与鉴权。
/// 只加可空列，历史行不受影响；POSM 既有迁移已登记，新列须走独立版本号迁移，
/// 生产须显式执行 --schema=migrate，且要先于新版主 API 上线（共享设备实体会查询这两列；Hbpos.Api 启动时也会幂等补列）。
/// 错误号 54111（列签名不兼容）；设备表不存在时 Apply / Verify 均为空操作。
/// </summary>
internal static class PosDeviceApiEntrySchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

-- 全新的空 POSM 库里设备表不是由基线创建的（生产库里早已存在）：表不存在时本迁移是空操作，
-- 之后设备表由实体 CodeFirst / Hbpos.Api 启动补列创建时已经带这两列。
IF OBJECT_ID(N'dbo.POSM_设备注册信息表', N'U') IS NULL
    RETURN;

BEGIN TRANSACTION;
BEGIN TRY
    IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'LastApiEntry') IS NULL
        ALTER TABLE [dbo].[POSM_设备注册信息表] ADD [LastApiEntry] nvarchar(16) NULL;
    IF COL_LENGTH(N'dbo.POSM_设备注册信息表', N'LastApiEntryAt') IS NULL
        ALTER TABLE [dbo].[POSM_设备注册信息表] ADD [LastApiEntryAt] datetime2 NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：两列都必须存在且类型 / 可空性精确匹配（nvarchar(16) 的 max_length 为 32 字节）。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.POSM_设备注册信息表', N'U') IS NULL
    RETURN;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'LastApiEntry', N'nvarchar', 32, 1),
      (N'LastApiEntryAt', N'datetime2', 8, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c ON c.[object_id] = OBJECT_ID(N'dbo.POSM_设备注册信息表') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 54111, N'POSM device API entry column signature is incompatible.', 1;
""";
}
