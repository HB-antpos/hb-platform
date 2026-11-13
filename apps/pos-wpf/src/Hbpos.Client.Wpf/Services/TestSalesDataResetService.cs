using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Wpf.Services;

public interface ITestSalesDataResetService
{
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class TestSalesDataResetService(LocalSqliteStore store) : ITestSalesDataResetService
{
    private static readonly string[] SalesDataTables =
    [
        "LocalCardTransactions",
        "LocalPayments",
        "LocalOrderLines",
        "LocalOrders",
        "LocalOrderInstallments",
        "LocalCashMovements",
        "LocalDailyCloseCashCounts",
        "LocalDailyCloses",
        "SuspendedOrderReturnPaymentCapacities",
        "SuspendedOrderLines",
        "SuspendedOrders",
        "SyncQueue"
    ];

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await store.OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();

        // 仅用于 Debug 测试清理：按子表到父表删除销售数据，保留商品、设备、收银员和配置数据。
        foreach (var tableName in SalesDataTables)
        {
            await DeleteTableRowsAsync(connection, transaction, tableName, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task DeleteTableRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {tableName};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
