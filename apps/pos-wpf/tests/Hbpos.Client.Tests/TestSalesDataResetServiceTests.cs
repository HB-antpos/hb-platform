using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class TestSalesDataResetServiceTests
{
    private static readonly string[] SalesTables =
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

    private static readonly string[] PreservedTables =
    [
        "LocalSellableItemIndex",
        "DeviceCache",
        "CashierCache",
        "AppSettings"
    ];

    [Fact]
    public async Task ResetAsync_deletes_sales_tables_and_preserves_configuration_tables()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            await SeedDatabaseAsync(store);

            var service = new TestSalesDataResetService(store);
            await service.ResetAsync();

            await using var connection = await store.OpenConnectionAsync();
            foreach (var tableName in SalesTables)
            {
                Assert.Equal(0, await ReadCountAsync(connection, tableName));
            }

            foreach (var tableName in PreservedTables)
            {
                Assert.Equal(1, await ReadCountAsync(connection, tableName));
            }
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static async Task SeedDatabaseAsync(LocalSqliteStore store)
    {
        await using var connection = await store.OpenConnectionAsync();
        await ExecuteAsync(
            connection,
            """
            INSERT INTO LocalOrders
            (OrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SoldAt, TotalAmount, DiscountAmount, ActualAmount, TenderedAmount, ChangeAmount, SyncStatus)
            VALUES ('11111111-1111-1111-1111-111111111111', '1002', 'POS-1', 'C001', 'Alice', '2026-06-02T10:00:00Z', '10', '0', '10', '10', '0', 'Pending');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO LocalOrderLines
            (OrderLineGuid, OrderGuid, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, Quantity, UnitPrice, DiscountAmount, ActualAmount, PriceSource, Kind, ReturnSourceKey, OriginalOrderGuid, OriginalOrderDetailGuid)
            VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'SKU-1', NULL, 'Tea', 'TEA', 'SKU-1', '1', '10', '0', '10', 1, 1, NULL, NULL, NULL);
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO LocalPayments (PaymentGuid, OrderGuid, Method, Amount, Reference, IdempotencyKey)
            VALUES ('33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 1, '10', 'REF-1', 'KEY-1');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO LocalCardTransactions
            (Id, PaymentGuid, OrderGuid, Processor, TxnRef, AuthCode, CardType, CardBin, MaskedCardNumber, MerchantId, ResponseCode, ResponseText, Stan, BankDateTime, Amount, ReceiptText)
            VALUES ('CARD-1', '33333333-3333-3333-3333-333333333333', '11111111-1111-1111-1111-111111111111', 'Linkly', 'TXN-1', 'AUTH', 'VISA', 411111, '****1111', 'M1', '00', 'APPROVED', '123456', '2026-06-02T10:01:00Z', '10', 'receipt');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO LocalOrderInstallments
            (OrderGuid, InstallmentGuid, InstallmentNumber, StoreCode, DeviceCode, CashierId, CashierName, CustomerName, CustomerPhone, CreatedAt, UpdatedAt, TotalAmount, MinimumDownPayment, DownPaymentAmount, PaidAmount, BalanceAmount, Status, LinesJson, PaymentsJson, PickupInfoJson, CancellationInfoJson, Note)
            VALUES ('44444444-4444-4444-4444-444444444444', '55555555-5555-5555-5555-555555555555', 'INS-1', '1002', 'POS-1', 'C001', 'Alice', 'Customer', '0400000000', '2026-06-02T10:00:00Z', '2026-06-02T10:00:00Z', '100', '10', '10', '10', '90', 0, '[]', '[]', NULL, NULL, NULL);
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO LocalDailyCloses
            (DailyCloseGuid, StoreCode, DeviceCode, CashierId, CashierName, BusinessDate, PeriodFrom, PeriodTo, SavedAt, OrderCount, CashSalesAmount, CashRefundAmount, CashNetAmount, CardSalesAmount, CardRefundAmount, CardNetAmount, VoucherSalesAmount, VoucherRefundAmount, VoucherNetAmount, RefundAmount, ReturnQuantity, NoteSubtotal, CoinSubtotal, CountedCashAmount, CashDifference)
            VALUES ('66666666-6666-6666-6666-666666666666', '1002', 'POS-1', 'C001', 'Alice', '2026-06-02', '2026-06-02T00:00:00Z', '2026-06-02T23:59:59Z', '2026-06-02T23:59:59Z', 1, '10', '0', '10', '0', '0', '0', '0', '0', '0', '0', '0', '10', '0', '10', '0');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO LocalDailyCloseCashCounts (DailyCloseGuid, DenominationValue, Label, Kind, Quantity, Amount)
            VALUES ('66666666-6666-6666-6666-666666666666', '10', '$10', 0, 1, '10');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO SuspendedOrders
            (SuspendedOrderGuid, StoreCode, DeviceCode, CashierId, CashierName, SuspendedAt, TotalAmount, DiscountAmount, ActualAmount, Status)
            VALUES ('77777777-7777-7777-7777-777777777777', '1002', 'POS-1', 'C001', 'Alice', '2026-06-02T10:00:00Z', '10', '0', '10', 0);
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO SuspendedOrderLines
            (SuspendedOrderLineGuid, SuspendedOrderGuid, StoreCode, ProductCode, ReferenceCode, DisplayName, LookupCode, ItemNumber, ProductImage, Quantity, UnitPrice, DiscountAmount, DiscountPercent, ActualAmount, PriceSource, PriceSourceLabel, Kind, ReturnSourceKey, OriginalOrderGuid, OriginalOrderDetailGuid, ReturnReason)
            VALUES ('88888888-8888-8888-8888-888888888888', '77777777-7777-7777-7777-777777777777', '1002', 'SKU-1', NULL, 'Tea', 'TEA', 'SKU-1', NULL, '1', '10', '0', NULL, '10', 1, 'StoreRetailPrice', 0, '', NULL, NULL, NULL);
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO SuspendedOrderReturnPaymentCapacities
            (SuspendedOrderGuid, Method, OriginalAmount, RefundedAmount, RemainingAmount, Reference, CardTransactionsJson, OriginalOrderGuid)
            VALUES ('77777777-7777-7777-7777-777777777777', 1, '10', '0', '10', 'REF-1', NULL, '11111111-1111-1111-1111-111111111111');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO SyncQueue (EntityId, EntityType, Status, CreatedAt)
            VALUES ('11111111-1111-1111-1111-111111111111', 'Order', 'Pending', '2026-06-02T10:00:00Z');
            """);

        await SeedPreservedTablesAsync(connection);
    }

    private static async Task SeedPreservedTablesAsync(SqliteConnection connection)
    {
        await ExecuteAsync(
            connection,
            """
            INSERT INTO LocalSellableItemIndex
            (StoreCode, ProductCode, ReferenceCode, DisplayName, LookupCode, LookupCodeNormalized, ItemNumber, Barcode, ProductImage, DiscountRate, IsSpecialProduct, RetailPrice, PriceSource, PriceSourceLabel, QuantityFactor, UpdatedAt, ContentHash, SyncedAt)
            VALUES ('1002', 'SKU-KEEP', NULL, 'Keep Item', 'KEEP', 'KEEP', 'SKU-KEEP', 'KEEP', NULL, NULL, 0, '1', 1, 'StoreRetailPrice', '1', '2026-06-02T10:00:00Z', 'hash', '2026-06-02T10:00:00Z');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO DeviceCache
            (DeviceCode, StoreCode, StoreName, HardwareId, DeviceStatus, IsAllowed, Message, AuthorizationCodeProtected, UpdatedAt)
            VALUES ('POS-1', '1002', 'Main', 'HW-1', 1, 1, NULL, NULL, '2026-06-02T10:00:00Z');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO CashierCache
            (CashierId, CashierName, StoreCode, DeviceCode, RolesJson, UpdatedAt)
            VALUES ('C001', 'Alice', '1002', 'POS-1', '[]', '2026-06-02T10:00:00Z');
            """);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO AppSettings (Key, Value, UpdatedAt)
            VALUES ('settings.keep', 'true', '2026-06-02T10:00:00Z');
            """);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ReadCountAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-test-sales-reset-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
