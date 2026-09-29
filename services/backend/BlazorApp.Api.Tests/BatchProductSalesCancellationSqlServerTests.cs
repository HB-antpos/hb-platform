using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Models;
using SqlSugar;
using System.Diagnostics;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed partial class BatchProductSalesAnalysisSqlServerIntegrationTests
{
    [BatchSalesSqlServerFact]
    public async Task CancellationReaders_SQLServer执行中查询收到取消且恢复外层令牌()
    {
        await PrepareAnalysisSchemaAsync();
        var day = new DateTime(2026, 9, 15);
        var statistics = new BatchProductSalesStatisticReader(_catalog!);
        var snapshots = new BatchProductSalesDiscountSnapshotReader(_catalog);
        using var outer = new CancellationTokenSource();
        _catalog.Ado.CancellationToken = outer.Token;

        try
        {
            await AssertBlockedQueryCanCancelAsync(
                typeof(ProductStoreDailySalesStatistic),
                "ProductStoreDailySalesStatistic",
                token => statistics.ReadAsync("P1", [day], ["S1"], token));
            Assert.Equal(outer.Token, _catalog.Ado.CancellationToken);

            await AssertBlockedQueryCanCancelAsync(
                typeof(BatchProductSalesDiscountRefreshState),
                "BatchProductSalesDiscountRefreshState",
                token => snapshots.ReadManyAsync(["P1"], [day], ["S1"], [], token));
            Assert.Equal(outer.Token, _catalog.Ado.CancellationToken);
        }
        finally
        {
            _catalog.Aop.OnLogExecuting = null;
            _catalog.Ado.RemoveCancellationToken();
        }
    }

    [BatchSalesSqlServerFact]
    public async Task CancellationReaders_SQLServer查询期间使用请求令牌并在正常取消异常后恢复外层令牌()
    {
        await PrepareAnalysisSchemaAsync();
        var day = new DateTime(2026, 9, 15);
        await _catalog!.Insertable(new ProductStoreDailySalesStatistic
        {
            Date = day, ProductCode = "P1", BranchCode = "S1", TotalQuantity = 1, TotalAmount = 10m,
        }).ExecuteCommandAsync();

        var statistics = new BatchProductSalesStatisticReader(_catalog);
        var snapshots = new BatchProductSalesDiscountSnapshotReader(_catalog);
        using var outer = new CancellationTokenSource();
        using var request = new CancellationTokenSource();
        _catalog.Ado.CancellationToken = outer.Token;
        var observedRequestToken = false;
        _catalog.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase)
                || sql.Contains("BatchProductSalesDiscount", StringComparison.OrdinalIgnoreCase))
                observedRequestToken |= _catalog.Ado.CancellationToken == request.Token;
        };

        try
        {
            var rows = await statistics.ReadAsync("P1", [day], ["S1"], request.Token);
            await snapshots.ReadManyAsync(["P1"], [day], ["S1"], rows, request.Token);
            Assert.True(observedRequestToken);
            Assert.Equal(outer.Token, _catalog.Ado.CancellationToken);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => statistics.ReadAsync("P1", [day], ["S1"], cancelled.Token));
            Assert.Equal(outer.Token, _catalog.Ado.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshots.ReadManyAsync(["P1"], [day], ["S1"], rows, cancelled.Token));
            Assert.Equal(outer.Token, _catalog.Ado.CancellationToken);

            _catalog.Aop.OnLogExecuting = (sql, _) =>
            {
                if (sql.Contains("ProductStoreDailySalesStatistic", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("simulated reader query failure");
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => statistics.ReadAsync("P1", [day], ["S1"], request.Token));
            Assert.Equal(outer.Token, _catalog.Ado.CancellationToken);

            _catalog.Aop.OnLogExecuting = (sql, _) =>
            {
                if (sql.Contains("BatchProductSalesDiscountRefreshState", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("simulated snapshot reader query failure");
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.ReadManyAsync(["P1"], [day], ["S1"], rows, request.Token));
            Assert.Equal(outer.Token, _catalog.Ado.CancellationToken);
        }
        finally
        {
            _catalog.Aop.OnLogExecuting = null;
            _catalog.Ado.RemoveCancellationToken();
        }
    }

    private async Task AssertBlockedQueryCanCancelAsync(Type entityType, string tableName, Func<CancellationToken, Task> read)
    {
        using var blocker = Client(WithDatabase(_master!, CatalogName));
        await blocker.Ado.BeginTranAsync();
        try
        {
            await blocker.Ado.ExecuteCommandAsync($"SELECT 1 FROM {Quote(_catalog!.EntityMaintenance.GetTableName(entityType))} WITH (TABLOCKX, HOLDLOCK)");
            var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _catalog.Aop.OnLogExecuting = (sql, _) =>
            {
                if (sql.Contains($"FROM [{tableName}]", StringComparison.OrdinalIgnoreCase))
                    queryStarted.TrySetResult();
            };
            using var cancellation = new CancellationTokenSource();
            var reading = read(cancellation.Token);
            await queryStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
            var elapsed = Stopwatch.StartNew();
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"取消被锁住的 {tableName} 查询耗时 {elapsed.ElapsedMilliseconds}ms");
        }
        finally
        {
            _catalog!.Aop.OnLogExecuting = null;
            await blocker.Ado.RollbackTranAsync();
        }
    }
}
