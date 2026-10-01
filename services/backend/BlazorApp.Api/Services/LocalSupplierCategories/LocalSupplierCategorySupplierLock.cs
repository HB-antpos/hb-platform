using SqlSugar;

namespace BlazorApp.Api.Services.LocalSupplierCategories;

/// <summary>
/// 同一供应商的分类写入（采集、重新解析、切换促销）在 SQL Server 上用事务级应用锁串行，
/// 避免并发重算对同一商品插入归属时撞主键。必须在调用方事务内调用；SQLite 测试库跳过。
/// </summary>
public static class LocalSupplierCategorySupplierLock
{
    private const int LockTimeoutMilliseconds = 10000;

    public static async Task AcquireAsync(ISqlSugarClient db, string supplierCode)
    {
        if (db.CurrentConnectionConfig.DbType != DbType.SqlServer)
        {
            return;
        }

        var result = await db.Ado.GetIntAsync(
            """
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock
                @Resource = @LockResource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = @LockTimeout;
            SELECT @Result;
            """,
            new SugarParameter("@LockResource", $"HB:LocalSupplierCategory:{supplierCode}"),
            new SugarParameter("@LockTimeout", LockTimeoutMilliseconds)
        );
        if (result < 0)
        {
            throw new LocalSupplierCategoryBusyException();
        }
    }
}
