namespace BlazorApp.Api.Data.SchemaMigrations;

/// <summary>
/// 分店订单退货字段（迁移 20261021.001-warehouse-order-return-fields）：dbo.WareHouseOrderDetails 一次加三列
/// ReturnReason nvarchar(32) NULL（退货原因码）、ReturnDisposition int NULL（0 回仓可售 / 1 报损）、
/// ReturnNote nvarchar(200) NULL（退货备注）。
/// 只加可空列、不回填任何数据、不加 CHECK 约束（规则放服务层，便于回滚）：存量明细三列为空，
/// 显示、拣货、发票、统计行为与加列前完全一致；只有 AllocQuantity &lt; 0 的退货行才会写入这三列。
/// WareHouseOrderDetails 随基线 20260827.001 建出（SqlSugarContext 的 CodeFirst tableTypes），新列只能走独立版本号迁移；
/// 实体上对应属性写的是 [SugarColumn(IsNullable = true, Length = 32/200)]，长度必须与这里保持一致
/// （CheckCoreAsync 只查账本、不比较实体与库列，一致性靠本迁移的 VerifySql 门禁兜底）。
/// 部署顺序：先执行 --schema=migrate，再执行权限 SQL，再发主 API，再发 POS API
/// （Hbpos.Api 与主 API 共用该实体，且没有 schema 门禁，缺列时整实体查询会直接失败），最后发 Web。错误号区间 53900–53901。
/// </summary>
internal static class WarehouseOrderReturnFieldsSchema
{
    internal const string ApplySql = """
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.WareHouseOrderDetails', N'U') IS NULL
    THROW 53900, N'WareHouseOrderDetails table is missing; apply the HBweb baseline first.', 1;

BEGIN TRANSACTION;
BEGIN TRY
    -- COL_LENGTH 判断保证重复执行是空操作；若 CodeFirst 已抢先建出同名列，则保持原列不动，
    -- 由 VerifySql 校验类型与长度是否符合预期（不符合只能显式修复，运行时绝不自动改写）。
    IF COL_LENGTH(N'dbo.WareHouseOrderDetails', N'ReturnReason') IS NULL
        ALTER TABLE [dbo].[WareHouseOrderDetails] ADD [ReturnReason] nvarchar(32) NULL;
    IF COL_LENGTH(N'dbo.WareHouseOrderDetails', N'ReturnDisposition') IS NULL
        ALTER TABLE [dbo].[WareHouseOrderDetails] ADD [ReturnDisposition] int NULL;
    IF COL_LENGTH(N'dbo.WareHouseOrderDetails', N'ReturnNote') IS NULL
        ALTER TABLE [dbo].[WareHouseOrderDetails] ADD [ReturnNote] nvarchar(200) NULL;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
""";

    // 只读门禁：ReturnReason nvarchar(32)、ReturnDisposition int、ReturnNote nvarchar(200)，全部可空
    // （sys.columns.max_length 按字节计，nvarchar(32) = 64、nvarchar(200) = 400、int = 4）。
    // 签名不符只能通过显式迁移修复，运行时绝不自动改写。
    internal const string VerifySql = """
IF OBJECT_ID(N'dbo.WareHouseOrderDetails', N'U') IS NULL
    THROW 53900, N'WareHouseOrderDetails table is missing.', 1;

IF EXISTS (
    SELECT 1 FROM (VALUES
      (N'ReturnReason', N'nvarchar', 64, 1),
      (N'ReturnDisposition', N'int', 4, 1),
      (N'ReturnNote', N'nvarchar', 400, 1)
    ) expected([column_name], [type_name], [max_length], [is_nullable])
    LEFT JOIN sys.columns c
      ON c.[object_id] = OBJECT_ID(N'dbo.WareHouseOrderDetails') AND c.[name] = expected.[column_name]
    LEFT JOIN sys.types ty ON ty.[user_type_id] = c.[user_type_id]
    WHERE c.[column_id] IS NULL OR ty.[name] <> expected.[type_name]
       OR c.[max_length] <> expected.[max_length]
       OR c.[is_nullable] <> expected.[is_nullable]
)
    THROW 53901, N'WareHouseOrderDetails return columns signature is incompatible.', 1;
""";
}
