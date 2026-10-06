using System.Reflection;
using System.Runtime.CompilerServices;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 本地成本事务使用独立 SQL Server 数据库，HQ 使用独立 SQLite 文件。
/// 验证真实 applock 和业务入口的组合，不以 SQLite 的无锁分支代替并发验收。
/// </summary>
public sealed class LocalSupplierInvoiceHqConcurrencySqlServerTests
{
    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 已有六个单品二十八店_无关商品持锁时连续更新成功且字段准确()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-single", 6, 28);
        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(blocker, ["UNRELATED"]);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var scope = fixture.OpenService();
                var response = await scope.Service.UpdateHqProductsAsync("invoice-single", request, null, "tester", 500);
                Assert.True(response.Success, response.Message);
                Assert.Equal(168, response.Data!.HqPurchasePricesUpdated);
                Assert.Equal(0, response.Data.Failed);
                Assert.Equal(0, response.Data.HbwebCreated);
            }
            Assert.Equal(6, await fixture.Local.Queryable<Product>().CountAsync());
            Assert.Equal(168, await fixture.Hq.Queryable<DIC_商品零售价表>().CountAsync());
            var prices = await fixture.Hq.Queryable<DIC_商品零售价表>().ToListAsync();
            Assert.All(prices, price => Assert.Equal(7m, price.H进货价));
            Assert.All(prices, price => Assert.Equal(11m, price.H分店零售价));
            Assert.All(prices, price => Assert.True(price.H是否自动定价));
            var details = await fixture.Local.Queryable<StoreLocalSupplierInvoiceDetails>().ToListAsync();
            Assert.All(details, detail => Assert.Equal(5m, detail.LastPurchasePrice));
            Assert.All(await fixture.Local.Queryable<Product>().ToListAsync(), product => Assert.Equal(5m, product.PurchasePrice));
        }
        finally { await blocker.Ado.RollbackTranAsync(); }
    }

    private static UpdateToStorePricesFields NewProductUpdateFields() => new()
    {
        UpdatePurchasePrice = true,
        UpdateRetailPrice = true,
        UpdateIsAutoPricing = true,
    };

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 真实分店价格入口同商品锁冲突时返回专用码且零写入_释放后同请求成功()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-store-price-busy", 1, 1);
        var dto = new UpdateToStorePricesRequest
        {
            InvoiceGuid = "invoice-store-price-busy",
            DetailGuids = request.DetailGuids.ToList(),
            TargetStoreCodes = request.TargetStoreCodes.ToList(),
            UpdateFields = NewProductUpdateFields(),
        };
        var beforeProduct = await fixture.Local.Queryable<Product>()
            .SingleAsync(product => product.ProductCode == "P0");
        var beforePrices = await fixture.Local.Queryable<StoreRetailPrice>()
            .Where(price => price.ProductCode == "P0")
            .ToListAsync();

        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(blocker, ["P0"]);
        try
        {
            using var scope = fixture.OpenService();
            var failed = await scope.ReactService.UpdateDetailsToStorePricesAsync(
                dto,
                "tester",
                100);

            Assert.False(failed.Success);
            Assert.Equal("STORE_UPDATE_COST_LOCK_BUSY", failed.Code);
            var zero = Assert.IsType<UpdateToStorePricesResultDto>(failed.Details);
            Assert.Equal(0, zero.Inserted);
            Assert.Equal(0, zero.Updated);
            Assert.Equal(0, zero.UpdatedPurchasePrices);
            Assert.Equal(0, zero.Skipped);
            Assert.Equal(0, zero.Failed);
            Assert.Empty(zero.Errors);
            Assert.Null(scope.Local.Ado.Transaction);
            Assert.Equal(beforeProduct.PurchasePrice, (await fixture.Local.Queryable<Product>()
                .SingleAsync(product => product.ProductCode == "P0")).PurchasePrice);
            Assert.Equal(beforePrices.Count, await fixture.Local.Queryable<StoreRetailPrice>()
                .Where(price => price.ProductCode == "P0")
                .CountAsync());

            await blocker.Ado.RollbackTranAsync();
            var succeeded = await scope.ReactService.UpdateDetailsToStorePricesAsync(
                dto,
                "tester",
                100);
            Assert.True(succeeded.Success, succeeded.Message);
            Assert.Equal(1, succeeded.Data!.Inserted);
            Assert.Equal(1, succeeded.Data.UpdatedPurchasePrices);
            Assert.Equal(0, succeeded.Data.Failed);
        }
        finally
        {
            if (blocker.Ado.Transaction != null)
                await blocker.Ado.RollbackTranAsync();
        }

        var product = await fixture.Local.Queryable<Product>()
            .SingleAsync(item => item.ProductCode == "P0");
        Assert.Equal(7m, product.PurchasePrice);
        var price = await fixture.Local.Queryable<StoreRetailPrice>()
            .SingleAsync(item => item.ProductCode == "P0" && item.StoreCode == "S00");
        Assert.Equal(7m, price.PurchasePrice);
        Assert.Equal(11m, price.StoreRetailPriceValue);
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 真实分店价格批量锁第二商品失败时回滚首商品_第三连接可重新取得首锁()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-store-price-batch-busy", 2, 1);
        var dto = new UpdateToStorePricesRequest
        {
            InvoiceGuid = "invoice-store-price-batch-busy",
            DetailGuids = request.DetailGuids.ToList(),
            TargetStoreCodes = request.TargetStoreCodes.ToList(),
            UpdateFields = NewProductUpdateFields(),
        };

        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(blocker, ["P1"]);
        try
        {
            using var scope = fixture.OpenService();
            var failed = await scope.ReactService.UpdateDetailsToStorePricesAsync(
                dto,
                "tester",
                100);
            Assert.False(failed.Success);
            Assert.Equal("STORE_UPDATE_COST_LOCK_BUSY", failed.Code);
            Assert.IsType<UpdateToStorePricesResultDto>(failed.Details);
            Assert.Null(scope.Local.Ado.Transaction);
            Assert.Equal(0, await fixture.Local.Queryable<StoreRetailPrice>().CountAsync());
            Assert.All(
                await fixture.Local.Queryable<Product>().OrderBy(product => product.ProductCode).ToListAsync(),
                product => Assert.Equal(5m, product.PurchasePrice));

            using var third = fixture.OpenLocal();
            await third.Ado.BeginTranAsync();
            try
            {
                await SetChildPurchasePriceMutationLock.AcquireProductsWithinBudgetAsync(
                    third,
                    ["P0"],
                    0);
            }
            finally
            {
                await third.Ado.RollbackTranAsync();
            }

            await blocker.Ado.RollbackTranAsync();
            var succeeded = await scope.ReactService.UpdateDetailsToStorePricesAsync(
                dto,
                "tester",
                100);
            Assert.True(succeeded.Success, succeeded.Message);
            Assert.Equal(2, succeeded.Data!.Inserted);
            Assert.Equal(2, succeeded.Data.UpdatedPurchasePrices);
            Assert.Equal(0, succeeded.Data.Failed);
        }
        finally
        {
            if (blocker.Ado.Transaction != null)
                await blocker.Ado.RollbackTranAsync();
        }

        Assert.Equal(2, await fixture.Local.Queryable<StoreRetailPrice>().CountAsync());
        Assert.All(
            await fixture.Local.Queryable<Product>().ToListAsync(),
            product => Assert.Equal(7m, product.PurchasePrice));
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 同商品锁超时_确认回滚且HQ零写入_释放后重试成功()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-busy", 1, 1);
        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(blocker, ["P0"]);
        using (var scope = fixture.OpenService())
        {
            var failed = await scope.Service.UpdateHqProductsAsync("invoice-busy", request, null, "tester", 100);
            Assert.False(failed.Success);
            Assert.Equal("HQ_UPDATE_COST_LOCK_BUSY", failed.Code);
            Assert.Null(scope.Local.Ado.Transaction);
            var failedResult = Assert.IsType<UpdateHqProductsResult>(failed.Details);
            Assert.Equal(1, failedResult.Total);
            Assert.Equal(0, failedResult.HqCreated);
            Assert.Equal(0, failedResult.HqPurchasePricesUpdated);
            Assert.Equal(0, await fixture.Hq.Queryable<DIC_商品信息字典表>().CountAsync());
        }
        await blocker.Ado.RollbackTranAsync();
        using var retryScope = fixture.OpenService();
        var succeeded = await retryScope.Service.UpdateHqProductsAsync("invoice-busy", request, null, "tester", 100);
        Assert.True(succeeded.Success, succeeded.Message);
        Assert.Equal(1, succeeded.Data!.HqPurchasePricesUpdated);
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 真实锁首次超时后_同一后台任务自动重试且重复提交复用任务()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-job", 1, 1);
        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(blocker, ["P0"]);
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var scope = fixture.OpenService();
            if (Interlocked.Increment(ref attempts) == 2) secondAttempt.TrySetResult();
            return scope;
        });
        services.AddScoped<ILocalSupplierInvoiceHqProductSyncService>(provider => provider.GetRequiredService<ServiceScope>().Service);
        using var provider = services.BuildServiceProvider();
        var jobs = new LocalSupplierInvoiceBatchUpdateJobService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LocalSupplierInvoiceBatchUpdateJobService>.Instance);
        var started = await jobs.StartUpdateHqProductsJobAsync("invoice-job", request, "tester");
        try
        {
            // 第一轮必须经过真实 SQL Server 的 10 秒锁等待并安全回滚，才会创建第二个 scope。
            await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var running = await jobs.GetUpdateHqProductsJobAsync(started.JobId);
            Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running, running!.Status);
            var duplicate = await jobs.StartUpdateHqProductsJobAsync("invoice-job", request, "tester");
            Assert.Equal(started.JobId, duplicate.JobId);
            Assert.Equal(started.OperationId, duplicate.OperationId);
            Assert.True(duplicate.IsDuplicateRequest);
            Assert.Equal(0, await fixture.Hq.Queryable<DIC_商品信息字典表>().CountAsync());
        }
        finally { await blocker.Ado.RollbackTranAsync(); }

        LocalSupplierInvoiceUpdateHqProductsJobDto? completed = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            completed = await jobs.GetUpdateHqProductsJobAsync(started.JobId);
            if (completed!.Status != LocalSupplierInvoiceBatchUpdateJobStatusConstants.Running) break;
            await Task.Delay(25);
        }
        Assert.Equal(LocalSupplierInvoiceBatchUpdateJobStatusConstants.Succeeded, completed!.Status);
        Assert.Equal(2, attempts);
        Assert.Equal(1, completed.Result!.Total);
        Assert.Equal(1, completed.Result.HqCreated);
        Assert.Equal(1, completed.Result.HqPurchasePricesUpdated);
        Assert.Equal(0, completed.Result.Failed);
        Assert.Equal(7m, (await fixture.Hq.Queryable<DIC_商品零售价表>().SingleAsync()).H进货价);
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 获锁期间商品匹配变化_释放商品锁后回退全局并复用新身份()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-change", 1, 1);
        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(blocker, ["P0"]);
        var acquiringProduct = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scope = fixture.OpenService();
        scope.Local.Aop.OnLogExecuting = (sql, parameters) =>
        {
            if (sql.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase)
                && parameters.Any(parameter => Convert.ToString(parameter.Value) == "HB:SetChildPurchasePrice:Product:P0"))
                acquiringProduct.TrySetResult();
        };
        var update = scope.Service.UpdateHqProductsAsync("invoice-change", request, null, "tester", 5_000);
        await acquiringProduct.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // 原身份在其商品锁内删除，替代身份保持相同供应商/货号/条码。
        await blocker.Updateable<Product>().SetColumns(product => product.IsDeleted == true)
            .Where(product => product.ProductCode == "P0").ExecuteCommandAsync();
        await blocker.Insertable(Fixture.Product("P-REPLACEMENT", "ITEM0", "9300000000000")).ExecuteCommandAsync();
        await blocker.Ado.CommitTranAsync();
        var result = await update.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.Data!.HbwebCreated);
        Assert.Equal("P-REPLACEMENT", (await fixture.Hq.Queryable<DIC_商品信息字典表>().SingleAsync()).H商品编码);
        Assert.Equal(1, await fixture.Local.Queryable<Product>().Where(product => !product.IsDeleted).CountAsync());
        Assert.Null(scope.Local.Ado.Transaction);
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 新商品等待全局锁后重新匹配_另一个请求先建好时不得重复创建()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-new", 0, 1);
        await fixture.Local.Insertable(Fixture.Detail("invoice-new", "D0", null, "ITEM0", "9300000000000")).ExecuteCommandAsync();
        request.DetailGuids = ["D0"];
        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireAllAsync(blocker);
        using var scope = fixture.OpenService();
        var waitingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Local.Aop.OnLogExecuting = (sql, parameters) =>
        {
            if (sql.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase)
                && parameters.Any(parameter => Convert.ToString(parameter.Value) == "HB:SetChildPurchasePrice:Gate"))
                waitingGate.TrySetResult();
        };
        var update = scope.Service.UpdateHqProductsAsync("invoice-new", request, null, "tester", 5_000);
        await waitingGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await blocker.Insertable(Fixture.Product("P-CREATED-FIRST", "ITEM0", "9300000000000")).ExecuteCommandAsync();
        await blocker.Ado.CommitTranAsync();
        var result = await update.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.Success, result.Message);
        Assert.Equal(0, result.Data!.HbwebCreated);
        Assert.Equal(1, await fixture.Local.Queryable<Product>().CountAsync());
        Assert.Equal(1, await fixture.Hq.Queryable<DIC_商品信息字典表>().CountAsync());
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 同批重复新货号条码明细_只创建一个本地商品且价格不重复()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-duplicate-batch", 0, 28);
        await fixture.Local.Insertable(new[]
        {
            Fixture.Detail("invoice-duplicate-batch", "D0", null, "ITEM-DUP", "9300000000100"),
            Fixture.Detail("invoice-duplicate-batch", "D1", null, "ITEM-DUP", "9300000000100"),
        }).ExecuteCommandAsync();
        request.DetailGuids = ["D0", "D1"];
        request.UpdateFields = NewProductUpdateFields();

        using var scope = fixture.OpenService();
        var response = await scope.Service.UpdateHqProductsAsync(
            "invoice-duplicate-batch", request, null, "tester", 500
        );

        Assert.True(response.Success, response.Message);
        var result = Assert.IsType<UpdateHqProductsResult>(response.Data);
        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.HbwebCreated);
        Assert.Equal(1, result.HqCreated);
        Assert.Equal(56, result.Updated);
        Assert.Equal(56, result.HqPurchasePricesUpdated);
        Assert.Equal(56, result.HqRetailPricesUpdated);
        Assert.Equal(56, result.HqAutoPricingUpdated);
        Assert.Equal(0, result.Failed);

        var products = await fixture.Local.Queryable<Product>().ToListAsync();
        var product = Assert.Single(products);
        Assert.Equal("ITEM-DUP", product.ItemNumber);
        Assert.Equal("9300000000100", product.Barcode);
        Assert.Equal(7m, product.PurchasePrice);
        Assert.Equal(11m, product.RetailPrice);

        var localPrices = await fixture.Local.Queryable<StoreRetailPrice>().ToListAsync();
        Assert.Equal(28, localPrices.Count);
        Assert.All(localPrices, price =>
        {
            Assert.Equal(product.ProductCode, price.ProductCode);
            Assert.Equal(7m, price.PurchasePrice);
            Assert.Equal(11m, price.StoreRetailPriceValue);
        });

        // 两行（含复用首行新建商品的 D1）都必须回填编码，且不改动「上次进货价」等其他明细字段。
        var details = await fixture.Local.Queryable<StoreLocalSupplierInvoiceDetails>()
            .Where(detail => detail.InvoiceGUID == "invoice-duplicate-batch")
            .OrderBy(detail => detail.DetailGUID)
            .ToListAsync();
        Assert.Equal(2, details.Count);
        Assert.All(details, detail =>
        {
            Assert.Equal(product.ProductCode, detail.ProductCode);
            Assert.Equal("S00" + product.ProductCode, detail.StoreProductCode);
            Assert.Equal(5m, detail.LastPurchasePrice);
        });

        var hqProduct = Assert.Single(await fixture.Hq.Queryable<DIC_商品信息字典表>().ToListAsync());
        Assert.Equal(product.ProductCode, hqProduct.H商品编码);
        var hqPrices = await fixture.Hq.Queryable<DIC_商品零售价表>().ToListAsync();
        Assert.Equal(28, hqPrices.Count);
        Assert.All(hqPrices, price =>
        {
            Assert.Equal(product.ProductCode, price.H商品编码);
            Assert.Equal(7m, price.H进货价);
            Assert.Equal(11m, price.H分店零售价);
        });
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 两张单据并发同一新商品_最终只创建一个本地商品()
    {
        await using var fixture = await Fixture.CreateAsync();
        var firstRequest = await fixture.SeedAsync("invoice-concurrent-a", 0, 28);
        await fixture.Local.Insertable(Fixture.Invoice("invoice-concurrent-b")).ExecuteCommandAsync();
        await fixture.Local.Insertable(new[]
        {
            Fixture.Detail("invoice-concurrent-a", "D-A", null, "ITEM-CONCURRENT", "9300000000200"),
            Fixture.Detail("invoice-concurrent-b", "D-B", null, "ITEM-CONCURRENT", "9300000000200"),
        }).ExecuteCommandAsync();
        firstRequest.DetailGuids = ["D-A"];
        firstRequest.UpdateFields = NewProductUpdateFields();
        var secondRequest = new UpdateHqProductsRequest
        {
            DetailGuids = ["D-B"],
            TargetStoreCodes = firstRequest.TargetStoreCodes,
            UpdateFields = NewProductUpdateFields(),
        };

        using var firstScope = fixture.OpenService();
        using var secondScope = fixture.OpenService();
        using var releaseFirstPreparation = new ManualResetEventSlim(false);
        using var releaseSecondHq = new ManualResetEventSlim(false);
        var firstGateAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaitingForGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ApiResponse<UpdateHqProductsResult>>? firstTask = null;
        Task<ApiResponse<UpdateHqProductsResult>>? secondTask = null;

        firstScope.Local.Aop.OnLogExecuted = (sql, parameters) =>
        {
            if (sql.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase)
                && parameters.Any(parameter => Convert.ToString(parameter.Value) == "HB:SetChildPurchasePrice:Gate")
                && parameters.Any(parameter => Convert.ToString(parameter.Value) == "Update")
                && firstGateAcquired.TrySetResult())
            {
                // 第一请求已持有身份总闸但尚未写入/提交本地商品，先让第二请求进入等待。
                if (!releaseFirstPreparation.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("等待放行第一请求本地事务超时");
            }
        };
        secondScope.Local.Aop.OnLogExecuting = (sql, parameters) =>
        {
            if (sql.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase)
                && parameters.Any(parameter => Convert.ToString(parameter.Value) == "HB:SetChildPurchasePrice:Gate")
                && parameters.Any(parameter => Convert.ToString(parameter.Value) == "Update"))
            {
                secondWaitingForGate.TrySetResult();
            }
        };
        secondScope.Hq.Aop.OnLogExecuting = (_, _) =>
        {
            // 两次本地提交的竞态是本测试目标；HQ SQLite 写入顺序化，避免混入独立 HQ 并发问题。
            if (firstTask is { IsCompleted: false }
                && !releaseSecondHq.Wait(TimeSpan.FromSeconds(20)))
            {
                throw new TimeoutException("等待第一请求 HQ 写入完成超时");
            }
        };

        try
        {
            firstTask = firstScope.Service.UpdateHqProductsAsync(
                "invoice-concurrent-a", firstRequest, null, "tester", 5_000
            );
            await firstGateAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));

            secondTask = secondScope.Service.UpdateHqProductsAsync(
                "invoice-concurrent-b", secondRequest, null, "tester", 5_000
            );
            await secondWaitingForGate.Task.WaitAsync(TimeSpan.FromSeconds(10));

            releaseFirstPreparation.Set();
            var firstResponse = await firstTask;
            releaseSecondHq.Set();
            var secondResponse = await secondTask;

            Assert.True(firstResponse.Success, firstResponse.Message);
            Assert.True(secondResponse.Success, secondResponse.Message);
            Assert.Equal(1, firstResponse.Data!.HbwebCreated + secondResponse.Data!.HbwebCreated);
            Assert.Equal(1, firstResponse.Data.HqCreated + secondResponse.Data.HqCreated);
            Assert.Equal(56, firstResponse.Data.HqPurchasePricesUpdated + secondResponse.Data.HqPurchasePricesUpdated);
            Assert.Equal(0, firstResponse.Data.Failed + secondResponse.Data.Failed);

            var products = await fixture.Local.Queryable<Product>().ToListAsync();
            var product = Assert.Single(products);
            Assert.Equal("ITEM-CONCURRENT", product.ItemNumber);
            Assert.Equal("9300000000200", product.Barcode);
            Assert.Equal(7m, product.PurchasePrice);
            Assert.Equal(11m, product.RetailPrice);
            Assert.Equal(28, await fixture.Local.Queryable<StoreRetailPrice>()
                .Where(price => price.ProductCode == product.ProductCode).CountAsync());
            Assert.Equal(1, await fixture.Hq.Queryable<DIC_商品信息字典表>()
                .Where(hqProduct => hqProduct.H商品编码 == product.ProductCode).CountAsync());
            Assert.Equal(28, await fixture.Hq.Queryable<DIC_商品零售价表>()
                .Where(price => price.H商品编码 == product.ProductCode).CountAsync());
        }
        finally
        {
            releaseFirstPreparation.Set();
            releaseSecondHq.Set();
            if (firstTask != null) await firstTask;
            if (secondTask != null) await secondTask;
        }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 全局维护独占锁期间新建商品应锁忙且本地与HQ均零写入()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-new-global-exclusive", 0, 1);
        await fixture.Local.Insertable(Fixture.Detail(
            "invoice-new-global-exclusive", "D0", null, "ITEM-GLOBAL", "9300000000300"
        )).ExecuteCommandAsync();
        request.DetailGuids = ["D0"];

        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireAllAsync(blocker);
        try
        {
            using var scope = fixture.OpenService();
            var response = await scope.Service.UpdateHqProductsAsync(
                "invoice-new-global-exclusive", request, null, "tester", 500
            );

            Assert.False(response.Success);
            Assert.Equal("HQ_UPDATE_COST_LOCK_BUSY", response.Code);
            var result = Assert.IsType<UpdateHqProductsResult>(response.Details);
            Assert.Equal(1, result.Total);
            Assert.Equal(0, result.HbwebCreated);
            Assert.Equal(0, result.HqCreated);
            Assert.Equal(0, result.Updated);
            Assert.Equal(0, result.HqPurchasePricesUpdated);
            Assert.Null(scope.Local.Ado.Transaction);
            Assert.Equal(0, await fixture.Local.Queryable<Product>().CountAsync());
            Assert.Equal(0, await fixture.Local.Queryable<StoreRetailPrice>().CountAsync());
            Assert.Equal(0, await fixture.Hq.Queryable<DIC_商品信息字典表>().CountAsync());
            Assert.Equal(0, await fixture.Hq.Queryable<DIC_商品零售价表>().CountAsync());
        }
        finally { await blocker.Ado.RollbackTranAsync(); }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 新商品GateU被另一身份写入阻塞_释放后复用先建商品()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-new-identity-writer", 0, 1);
        await fixture.Local.Insertable(Fixture.Detail(
            "invoice-new-identity-writer", "D0", null, "ITEM-IDENTITY", "9300000000400"
        )).ExecuteCommandAsync();
        request.DetailGuids = ["D0"];

        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductIdentitiesWithinBudgetAsync(
            blocker, ["P-CREATED-FIRST"], 5_000
        );
        using var scope = fixture.OpenService();
        var waitingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.Local.Aop.OnLogExecuting = (sql, parameters) =>
        {
            if (sql.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase)
                && parameters.Any(parameter => Convert.ToString(parameter.Value) == "HB:SetChildPurchasePrice:Gate")
                && parameters.Any(parameter => Convert.ToString(parameter.Value) == "Update"))
            {
                waitingGate.TrySetResult();
            }
        };
        var update = scope.Service.UpdateHqProductsAsync(
            "invoice-new-identity-writer", request, null, "tester", 5_000
        );
        await waitingGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await blocker.Insertable(Fixture.Product("P-CREATED-FIRST", "ITEM-IDENTITY", "9300000000400"))
            .ExecuteCommandAsync();
        // 另一身份完成新商品写入时应同时留下本地分店价格投影；本请求只负责复用该身份。
        await blocker.Insertable(new StoreRetailPrice
        {
            UUID = "price-P-CREATED-FIRST",
            StoreCode = "S00",
            ProductCode = "P-CREATED-FIRST",
            StoreProductCode = "S00P-CREATED-FIRST",
            SupplierCode = "SUP",
            PurchasePrice = 7m,
            StoreRetailPriceValue = 11m,
            IsActive = true,
            IsAutoPricing = true,
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await blocker.Ado.CommitTranAsync();

        var response = await update.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(response.Success, response.Message);
        Assert.Equal(0, response.Data!.HbwebCreated);
        Assert.Equal(1, response.Data.HqCreated);
        Assert.Equal(1, response.Data.HqPurchasePricesUpdated);
        Assert.Equal(1, await fixture.Local.Queryable<Product>()
            .Where(product => product.ProductCode == "P-CREATED-FIRST").CountAsync());
        Assert.Equal(1, await fixture.Hq.Queryable<DIC_商品信息字典表>()
            .Where(product => product.H商品编码 == "P-CREATED-FIRST").CountAsync());
        Assert.Equal(1, await fixture.Local.Queryable<StoreRetailPrice>()
            .Where(price => price.ProductCode == "P-CREATED-FIRST").CountAsync());
        Assert.Equal(1, await fixture.Hq.Queryable<DIC_商品零售价表>()
            .Where(price => price.H商品编码 == "P-CREATED-FIRST").CountAsync());
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 新建两个单品_无关商品持成本锁时应完成两商品二十八店成本零售价写入()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-new-unrelated-lock", 0, 28);
        await fixture.Local.Insertable(new[]
        {
            // 两条明细都没有 ProductCode，必须由本次业务调用创建本地商品。
            Fixture.Detail("invoice-new-unrelated-lock", "D0", null, "ITEM0", "9300000000000"),
            Fixture.Detail("invoice-new-unrelated-lock", "D1", null, "ITEM1", "9300000000001"),
        }).ExecuteCommandAsync();
        request.DetailGuids = ["D0", "D1"];
        request.UpdateFields = new UpdateToStorePricesFields
        {
            UpdatePurchasePrice = true,
            UpdateRetailPrice = true,
            UpdateIsAutoPricing = true,
        };

        using var blocker = fixture.OpenLocal();
        await blocker.Ado.BeginTranAsync();
        await SetChildPurchasePriceMutationLock.AcquireProductsAsync(blocker, ["UNRELATED"]);
        try
        {
            // 无关商品锁必须保持到业务返回，才能证明新商品路径没有被它错误阻塞。
            using var scope = fixture.OpenService();
            var response = await scope.Service.UpdateHqProductsAsync(
                "invoice-new-unrelated-lock", request, null, "tester", 500
            );

            Assert.True(response.Success, response.Message);
            var result = Assert.IsType<UpdateHqProductsResult>(response.Data);
            Assert.Equal(2, result.Total);
            Assert.Equal(2, result.HbwebCreated);
            Assert.Equal(2, result.HqCreated);
            Assert.Equal(56, result.Updated);
            Assert.Equal(56, result.HqPurchasePricesUpdated);
            Assert.Equal(56, result.HqRetailPricesUpdated);
            Assert.Equal(56, result.HqAutoPricingUpdated);
            Assert.Equal(0, result.Failed);

            var details = await fixture.Local.Queryable<StoreLocalSupplierInvoiceDetails>()
                .Where(detail => detail.InvoiceGUID == "invoice-new-unrelated-lock")
                .OrderBy(detail => detail.DetailGUID)
                .ToListAsync();
            Assert.Equal(2, details.Count);
            Assert.Equal(new[] { "D0", "D1" }, details.Select(detail => detail.DetailGUID).ToArray());
            var products = await fixture.Local.Queryable<Product>()
                .OrderBy(product => product.ItemNumber)
                .ToListAsync();
            Assert.Equal(2, products.Count);
            Assert.Equal(new[] { "ITEM0", "ITEM1" }, products.Select(product => product.ItemNumber).ToArray());
            var productCodeByItem = products.ToDictionary(product => product.ItemNumber!, product => product.ProductCode);
            Assert.All(details, detail =>
            {
                // 本次新建的商品只回填两列编码；进货价、上次进货价、零售价等明细字段不回写。
                Assert.Equal(productCodeByItem[detail.ItemNumber!], detail.ProductCode);
                Assert.Equal("S00" + detail.ProductCode, detail.StoreProductCode);
                Assert.Equal(7m, detail.PurchasePrice);
                Assert.Equal(5m, detail.LastPurchasePrice);
                Assert.Equal(11m, detail.RetailPrice);
            });
            Assert.All(products, product =>
            {
                Assert.Equal("SUP", product.LocalSupplierCode);
                Assert.Equal(7m, product.PurchasePrice);
                Assert.Equal(11m, product.RetailPrice);
                Assert.True(product.IsAutoPricing);
                Assert.True(product.IsActive);
                Assert.False(product.IsDeleted);
            });

            var localPrices = await fixture.Local.Queryable<StoreRetailPrice>().ToListAsync();
            Assert.Equal(56, localPrices.Count);
            Assert.Equal(28, localPrices.Select(price => price.StoreCode).Distinct().Count());
            Assert.Equal(2, localPrices.Select(price => price.ProductCode).Distinct().Count());
            Assert.All(localPrices, price =>
            {
                Assert.Equal("SUP", price.SupplierCode);
                Assert.Equal(7m, price.PurchasePrice);
                Assert.Equal(11m, price.StoreRetailPriceValue);
                Assert.True(price.IsAutoPricing);
                Assert.True(price.IsActive);
                Assert.False(price.IsDeleted);
            });
            Assert.All(localPrices.GroupBy(price => price.ProductCode), group => Assert.Equal(28, group.Count()));

            var hqProducts = await fixture.Hq.Queryable<DIC_商品信息字典表>().ToListAsync();
            Assert.Equal(2, hqProducts.Count);
            Assert.Equal(new[] { "ITEM0", "ITEM1" }, hqProducts.OrderBy(product => product.H货号)
                .Select(product => product.H货号).ToArray());
            Assert.All(hqProducts, product =>
            {
                Assert.Equal("SUP", product.H供货商编码);
                Assert.Equal(7m, product.H进货价);
                Assert.Equal(11m, product.H零售价);
                Assert.True(product.H是否自动定价);
                Assert.True(product.H使用状态);
            });

            var hqPrices = await fixture.Hq.Queryable<DIC_商品零售价表>().ToListAsync();
            Assert.Equal(56, hqPrices.Count);
            Assert.Equal(28, hqPrices.Select(price => price.H分店代码).Distinct().Count());
            Assert.Equal(2, hqPrices.Select(price => price.H商品编码).Distinct().Count());
            Assert.All(hqPrices, price =>
            {
                Assert.Equal("SUP", price.H供应商编码);
                Assert.Equal(7m, price.H进货价);
                Assert.Equal(11m, price.H分店零售价);
                Assert.True(price.H是否自动定价);
                Assert.True(price.H使用状态);
            });
            Assert.All(hqPrices.GroupBy(price => price.H商品编码), group => Assert.Equal(28, group.Count()));
        }
        finally { await blocker.Ado.RollbackTranAsync(); }
    }

    [SetChildPurchasePriceSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task 锁取消_不得标成可重试锁冲突()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = await fixture.SeedAsync("invoice-cancel", 1, 1);
        using var scope = fixture.OpenService();
        scope.Local.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("sp_getapplock", StringComparison.OrdinalIgnoreCase))
                throw new OperationCanceledException("cancelled by test");
        };
        var cancelled = await scope.Service.UpdateHqProductsAsync("invoice-cancel", request, null, "tester", 100);
        Assert.False(cancelled.Success);
        Assert.NotEqual("HQ_UPDATE_COST_LOCK_BUSY", cancelled.Code);
        Assert.Null(scope.Local.Ado.Transaction);
        Assert.Equal(0, await fixture.Hq.Queryable<DIC_商品信息字典表>().CountAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _adminConnection;
        private readonly string _databaseName = "HbInvoiceHqConcurrency_" + Guid.NewGuid().ToString("N");
        private readonly string _hqFile = Path.Combine(Path.GetTempPath(), "hb-invoice-hq-" + Guid.NewGuid().ToString("N") + ".db");
        private string _localConnection = string.Empty;
        internal SqlSugarClient Local { get; private set; } = null!;
        internal SqlSugarClient Hq { get; private set; } = null!;

        private Fixture(string adminConnection) => _adminConnection = adminConnection;

        internal static async Task<Fixture> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable("SET_CHILD_PURCHASE_PRICE_SQLSERVER_TEST_CONNECTION");
            Assert.False(string.IsNullOrWhiteSpace(configured));
            var dataSource = new SqlConnectionStringBuilder(configured!).DataSource.Trim();
            Assert.True(
                new[] { "127.0.0.1,14337", "localhost,14337", "127.0.0.1,1433", "localhost,1433" }
                    .Contains(dataSource, StringComparer.OrdinalIgnoreCase),
                $"SQL Server 集成测试只允许本地 Docker，实际 DataSource={dataSource}");
            var fixture = new Fixture(configured!);
            using var admin = new SqlConnection(fixture._adminConnection);
            await admin.OpenAsync();
            using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{fixture._databaseName}]";
            await create.ExecuteNonQueryAsync();
            fixture._localConnection = new SqlConnectionStringBuilder(fixture._adminConnection) { InitialCatalog = fixture._databaseName }.ConnectionString;
            fixture.Local = fixture.OpenLocal();
            fixture.Hq = fixture.OpenHq();
            try
            {
                fixture.Local.CodeFirst.InitTables(typeof(Store), typeof(Product), typeof(WarehouseProduct), typeof(DomesticProduct),
                    typeof(StoreRetailPrice), typeof(ProductSetCode), typeof(StoreMultiCodeProduct),
                    typeof(StoreLocalSupplierInvoice), typeof(StoreLocalSupplierInvoiceDetails));
                fixture.Hq.CodeFirst.InitTables(typeof(DIC_商品信息字典表), typeof(DIC_商品零售价表), typeof(DIC_一品多码表), typeof(DIC_分店一品多码表));
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal SqlSugarClient OpenLocal() => new(new ConnectionConfig
        {
            ConnectionString = _localConnection, DbType = SqlSugar.DbType.SqlServer,
            IsAutoCloseConnection = false, InitKeyType = InitKeyType.Attribute,
        });
        private SqlSugarClient OpenHq() => new(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_hqFile}", DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false, InitKeyType = InitKeyType.Attribute,
        });
        internal ServiceScope OpenService() => new(OpenLocal(), OpenHq());

        internal static StoreLocalSupplierInvoice Invoice(string invoiceGuid) => WithDates(new StoreLocalSupplierInvoice
        {
            InvoiceGUID = invoiceGuid,
            StoreCode = "S00",
            SupplierCode = "SUP",
            InvoiceNo = invoiceGuid,
            IsDeleted = false,
        });

        internal async Task<UpdateHqProductsRequest> SeedAsync(string invoiceGuid, int productCount, int storeCount)
        {
            var storeCodes = Enumerable.Range(0, storeCount).Select(index => $"S{index:00}").ToList();
            await Local.Insertable(storeCodes.Select(code => WithDates(new Store
            {
                StoreGUID = "store-" + code, StoreCode = code, StoreName = code, IsActive = true, IsDeleted = false,
            })).ToList()).ExecuteCommandAsync();
            await Local.Insertable(WithDates(new StoreLocalSupplierInvoice
            {
                InvoiceGUID = invoiceGuid, StoreCode = storeCodes[0], SupplierCode = "SUP", InvoiceNo = invoiceGuid, IsDeleted = false,
            })).ExecuteCommandAsync();
            var detailGuids = new List<string>();
            for (var index = 0; index < productCount; index++)
            {
                var productCode = "P" + index;
                var barcode = (9300000000000L + index).ToString();
                await Local.Insertable(Product(productCode, "ITEM" + index, barcode)).ExecuteCommandAsync();
                var detailGuid = "D" + index;
                await Local.Insertable(Detail(invoiceGuid, detailGuid, productCode, "ITEM" + index, barcode)).ExecuteCommandAsync();
                detailGuids.Add(detailGuid);
            }
            return new UpdateHqProductsRequest
            {
                DetailGuids = detailGuids, TargetStoreCodes = storeCodes,
                UpdateFields = new UpdateToStorePricesFields { UpdatePurchasePrice = true },
            };
        }
        internal static Product Product(string code, string item, string barcode) => WithDates(new Product
        {
            UUID = code, ProductCode = code, LocalSupplierCode = "SUP", ItemNumber = item, Barcode = barcode,
            ProductName = item, ProductType = 0, PurchasePrice = 5m, RetailPrice = 11m,
            IsAutoPricing = true, IsActive = true, IsDeleted = false,
        });
        internal static StoreLocalSupplierInvoiceDetails Detail(string invoice, string guid, string? code, string item, string barcode) => WithDates(new StoreLocalSupplierInvoiceDetails
        {
            InvoiceGUID = invoice, DetailGUID = guid, StoreCode = "S00", SupplierCode = "SUP", ProductCode = code,
            ItemNumber = item, Barcode = barcode, ProductName = item, PurchasePrice = 7m, LastPurchasePrice = 5m,
            RetailPrice = 11m, Quantity = 12, AutoPricing = true, IsDeleted = false,
        });
        private static T WithDates<T>(T value)
        {
            foreach (var property in typeof(T).GetProperties().Where(property => property.PropertyType == typeof(DateTime) && property.CanWrite))
                if ((DateTime)property.GetValue(value)! == default) property.SetValue(value, DateTime.UtcNow);
            return value;
        }
        public async ValueTask DisposeAsync()
        {
            Local?.Dispose(); Hq?.Dispose();
            using var admin = new SqlConnection(_adminConnection);
            await admin.OpenAsync();
            using var cleanup = admin.CreateCommand();
            // 名称仅由本 fixture 生成；仅清理此独立测试数据库。
            if (!System.Text.RegularExpressions.Regex.IsMatch(_databaseName, "^HbInvoiceHqConcurrency_[a-f0-9]{32}$"))
                throw new InvalidOperationException("Invalid test database name");
            cleanup.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]";
            await cleanup.ExecuteNonQueryAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(_hqFile);
        }
    }

    private sealed class ServiceScope : IDisposable
    {
        internal SqlSugarClient Local { get; }
        internal SqlSugarClient Hq { get; }
        internal LocalSupplierInvoiceHqProductSyncService Service { get; }
        internal LocalSupplierInvoicesReactService ReactService { get; }
        internal ServiceScope(SqlSugarClient local, SqlSugarClient hq)
        {
            Local = local; Hq = hq;
            Service = new LocalSupplierInvoiceHqProductSyncService(Context<SqlSugarContext>(local), Context<HqSqlSugarContext>(hq),
                NullLogger<LocalSupplierInvoiceHqProductSyncService>.Instance, WarehouseProductChangeHistoryTestDouble.CreateNoop());
            var autoPricing = new Mock<IAutoPricingService>();
            autoPricing.Setup(service => service.GetAllActiveStrategiesAsync())
                .ReturnsAsync(new List<PricingStrategy>());
            ReactService = new LocalSupplierInvoicesReactService(
                Context<SqlSugarContext>(local),
                Context<HqSqlSugarContext>(hq),
                Mock.Of<IMapper>(),
                NullLogger<LocalSupplierInvoicesReactService>.Instance,
                autoPricing.Object,
                WarehouseProductChangeHistoryTestDouble.CreateNoop());
        }
        private static T Context<T>(ISqlSugarClient db)
        {
            var context = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            typeof(T).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
            return context;
        }
        public void Dispose() { Local.Dispose(); Hq.Dispose(); }
    }
}
