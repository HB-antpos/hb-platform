using SqlSugar;

namespace BlazorApp.Api.Tests;

/// <summary>
/// SQLite 测试库里建 WarehouseProductChangeHistory：long 自增主键不能用 CodeFirst.InitTables 建，按实体列手写。
/// 进货单列表/明细会查该表判定「本单新品」，相关夹具都要建它。
/// </summary>
internal static class SqliteWarehouseProductChangeHistoryTable
{
    public static void Create(ISqlSugarClient db)
    {
        db.Ado.ExecuteCommand(
            """
            CREATE TABLE IF NOT EXISTS WarehouseProductChangeHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                EventGuid TEXT NOT NULL,
                ProductCode TEXT NOT NULL,
                Action TEXT NOT NULL,
                Source TEXT NOT NULL,
                SourceReference TEXT NULL,
                BatchGuid TEXT NULL,
                ActorUserGuid TEXT NULL,
                ActorName TEXT NOT NULL,
                ActorType TEXT NOT NULL,
                OccurredAtUtc TEXT NOT NULL,
                ChangesJson TEXT NOT NULL
            )
            """
        );
    }
}
