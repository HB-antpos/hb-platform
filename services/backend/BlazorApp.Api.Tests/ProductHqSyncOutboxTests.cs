using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Data.SchemaMigrations;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using BlazorApp.Shared.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class ProductHqSyncSqlServerFactAttribute : FactAttribute
{
    private const string ConnectionEnvironmentVariable = "HB_TEST_SQLSERVER_CONNECTION";

    public ProductHqSyncSqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable)))
        {
            Skip = $"未配置 {ConnectionEnvironmentVariable}，跳过真实 SQL Server 商品 HQ 执行锁验证。";
        }
    }
}

public sealed class ProductHqSyncOutboxTests : IDisposable
{
    private static readonly DateTime UtcNow = new(2026, 9, 3, 1, 2, 3, DateTimeKind.Utc);
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"product-hq-outbox-{Guid.NewGuid():N}.db"
    );
    private readonly SqlSugarClient _db;
    private readonly ProductHqSyncOutboxOptions _options = new()
    {
        LeaseSeconds = 60,
        BaseRetryDelaySeconds = 5,
        MaxRetryDelaySeconds = 60,
    };

    public ProductHqSyncOutboxTests()
    {
        _db = CreateDb();
        _db.CodeFirst.InitTables(typeof(ProductHqSyncOutbox), typeof(UserStore), typeof(Store));
    }

    [Fact]
    public async Task HQ商品执行锁_大小写同码互斥_释放后可重取()
    {
        var productCode = $"product-{Guid.NewGuid():N}";
        await using var first = await ProductHqMutationExecutionLock.AcquireAsync(
            _db,
            new[] { productCode.ToLowerInvariant() }
        );

        Assert.NotNull(first);
        Assert.Equal(
            ProductHqMutationExecutionLock.GetResourceKey(productCode),
            ProductHqMutationExecutionLock.GetResourceKey($"  {productCode.ToUpperInvariant()}  ")
        );
        Assert.Null(
            await ProductHqMutationExecutionLock.AcquireAsync(
                _db,
                new[] { productCode.ToUpperInvariant() }
            )
        );

        await first.DisposeAsync();
        await using var reacquired = await ProductHqMutationExecutionLock.AcquireAsync(
            _db,
            new[] { productCode }
        );
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task HQ商品执行锁_不同商品可并行_批量局部繁忙会释放先前锁()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var productA = $"a-{prefix}";
        var productB = $"b-{prefix}";
        await using var first = await ProductHqMutationExecutionLock.AcquireAsync(_db, new[] { productA });
        await using var differentProduct = await ProductHqMutationExecutionLock.AcquireAsync(
            _db,
            new[] { productB }
        );
        Assert.NotNull(first);
        Assert.NotNull(differentProduct);
        Assert.NotEqual(
            ProductHqMutationExecutionLock.GetResourceKey(productA),
            ProductHqMutationExecutionLock.GetResourceKey(productB)
        );

        await first.DisposeAsync();
        await differentProduct.DisposeAsync();
        await using var occupiedB = await ProductHqMutationExecutionLock.AcquireAsync(_db, new[] { productB });
        Assert.NotNull(occupiedB);
        Assert.Null(
            await ProductHqMutationExecutionLock.AcquireAsync(_db, new[] { productA, productB })
        );

        // 批量请求先得到 A 后在 B 上繁忙，必须释放 A，不能遗留无主互斥。
        await using var productAAfterPartialFailure = await ProductHqMutationExecutionLock.AcquireAsync(
            _db,
            new[] { productA }
        );
        Assert.NotNull(productAAfterPartialFailure);
    }

    [Fact]
    public void HQ商品写路径_推送与窄投影都调用共享执行锁()
    {
        var repositoryRoot = FindRepositoryRoot();
        var pushSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "services/backend/BlazorApp.Api/Services/React/ProductHqSyncService.cs"
            )
        );
        var projectionSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "services/backend/BlazorApp.Api/Services/React/ProductMaintenanceHqProjectionWriter.cs"
            )
        );

        Assert.Contains("ProductHqMutationExecutionLock.AcquireAsync", pushSource, StringComparison.Ordinal);
        Assert.Contains("ProductHqMutationExecutionLock.AcquireAsync", projectionSource, StringComparison.Ordinal);
    }

    [ProductHqSyncSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task SQLServer_HQ商品执行锁_独立连接同商品互斥且释放后可重取()
    {
        var connectionString = Environment.GetEnvironmentVariable("HB_TEST_SQLSERVER_CONNECTION")!;
        using var firstDb = CreateSqlServerDb(connectionString);
        using var secondDb = CreateSqlServerDb(connectionString);
        var productCode = $"lock-{Guid.NewGuid():N}";

        await using var first = await ProductHqMutationExecutionLock.AcquireAsync(
            firstDb,
            new[] { productCode }
        );
        Assert.NotNull(first);
        Assert.Null(
            await ProductHqMutationExecutionLock.AcquireAsync(
                secondDb,
                new[] { productCode.ToLowerInvariant() }
            )
        );

        await first.DisposeAsync();
        await using var reacquired = await ProductHqMutationExecutionLock.AcquireAsync(
            secondDb,
            new[] { productCode }
        );
        Assert.NotNull(reacquired);
    }

    [Fact]
    public async Task 入队复用调用方事务_回滚商品事务时不留下孤立任务()
    {
        var queue = CreateQueue();
        await _db.Ado.BeginTranAsync();

        await queue.EnqueueAsync(_db, Request("operation-rollback", "product-1"));
        await _db.Ado.RollbackTranAsync();

        Assert.Equal(0, await _db.Queryable<ProductHqSyncOutbox>().CountAsync());
    }

    [Fact]
    public async Task 重复OperationKey返回原任务_不会重复插入()
    {
        var queue = CreateQueue();
        var request = Request("operation-idempotent", "product-1");

        var first = await queue.EnqueueAsync(_db, request);
        var second = await queue.EnqueueAsync(_db, request);

        Assert.Equal(first.OutboxId, second.OutboxId);
        Assert.False(first.WasDuplicate);
        Assert.True(second.WasDuplicate);
        Assert.Equal(1, await _db.Queryable<ProductHqSyncOutbox>().CountAsync());
    }

    [Fact]
    public async Task 入队与WorkItem保留业务标识原始大小写()
    {
        const string productCode = "abcdef01-2345-6789-abcd-ef0123456789";
        var request = Request("operation-preserve-case", productCode) with
        {
            TargetStoreCodes = new List<string> { "store-a" },
            AuthorizedStoreCodes = new List<string> { "store-a" },
            Tombstones = new List<ProductHqSyncOutboxTombstoneDto>
            {
                new("store-clearance", "store-a", "clearance-1"),
            },
        };

        var enqueued = await CreateQueue().EnqueueAsync(_db, request);

        var row = await _db.Queryable<ProductHqSyncOutbox>()
            .SingleAsync(item => item.Id == enqueued.OutboxId);
        var workItem = ProductHqSyncOutboxQueue.ToWorkItem(row);
        var access = await CreateQueue().GetAccessDescriptorAsync(request.OperationKey);
        Assert.Equal(productCode, row.ProductCode);
        Assert.Equal(productCode, workItem.ProductCode);
        Assert.Equal("stores:store-a", row.ScopeKey);
        Assert.Equal(new[] { "store-a" }, workItem.TargetStoreCodes);
        Assert.Equal("store-a", workItem.Tombstones.Single().StoreCode);
        Assert.Equal(new[] { "store-a" }, access!.AuthorizedStoreCodes);
    }

    [Fact]
    public async Task 同商品同Scope合并为最新完整投影_旧任务标记Superseded()
    {
        var queue = CreateQueue();
        var firstRequest = Request("operation-old", "product-1") with
        {
            TargetStoreCodes = new List<string> { " 12 ", "02" },
            FieldMask = new List<string> { "productName" },
            PayloadJson = "{\"version\":1}",
            Tombstones = new List<ProductHqSyncOutboxTombstoneDto>
            {
                new("store-multi-code", "12", "old-code"),
            },
        };
        var secondRequest = Request("operation-new", "product-1") with
        {
            TargetStoreCodes = new List<string> { "02", "12", "02" },
            FieldMask = new List<string> { "storeRetailPrice" },
            PayloadJson = "{\"version\":2}",
            Tombstones = new List<ProductHqSyncOutboxTombstoneDto>
            {
                new("store-clearance", "02", "clearance-1"),
            },
        };

        var first = await queue.EnqueueAsync(_db, firstRequest);
        var second = await queue.EnqueueAsync(_db, secondRequest);

        var rows = await _db.Queryable<ProductHqSyncOutbox>().OrderBy(item => item.CreatedAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(ProductHqSyncOutboxStatuses.Superseded, rows[0].Status);
        Assert.Equal(second.OutboxId, rows[0].SupersededById);
        Assert.Equal(ProductHqSyncOutboxStatuses.Pending, rows[1].Status);
        Assert.Equal(new[] { first.OutboxId }, second.SupersededOutboxIds);

        var workItem = ProductHqSyncOutboxQueue.ToWorkItem(rows[1]);
        Assert.Equal("stores:02,12", workItem.ScopeKey);
        Assert.Equal(new[] { "productName", "storeRetailPrice" }, workItem.FieldMask);
        Assert.Equal("{\"version\":2}", workItem.PayloadJson);
        Assert.Equal(2, workItem.Tombstones.Count);
    }

    [Fact]
    public async Task 逆序OccurredAt仍按成功入队顺序保留最新Mutation为Active后继()
    {
        var queue = CreateQueue();
        var first = await queue.EnqueueAsync(
            _db,
            Request("operation-newer-clock", "product-1") with
            {
                OccurredAtUtc = UtcNow.AddHours(1),
                PayloadJson = "{\"version\":1}",
            }
        );

        var second = await queue.EnqueueAsync(
            _db,
            Request("operation-late-arrival", "product-1") with
            {
                OccurredAtUtc = UtcNow.AddHours(-1),
                PayloadJson = "{\"version\":2}",
            }
        );

        var firstRow = await _db.Queryable<ProductHqSyncOutbox>()
            .FirstAsync(item => item.Id == first.OutboxId);
        var secondRow = await _db.Queryable<ProductHqSyncOutbox>()
            .FirstAsync(item => item.Id == second.OutboxId);
        Assert.Equal(ProductHqSyncOutboxStatuses.Superseded, firstRow.Status);
        Assert.Equal(second.OutboxId, firstRow.SupersededById);
        Assert.Equal(ProductHqSyncOutboxStatuses.Pending, secondRow.Status);
        Assert.Equal("{\"version\":2}", secondRow.PayloadJson);
    }

    [Fact]
    public async Task Processing任务不可被后继Supersede或清除租约()
    {
        var queue = CreateQueue();
        var first = await queue.EnqueueAsync(_db, Request("operation-processing", "product-1"));
        var claimed = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
            _db,
            "instance-a",
            UtcNow,
            _options
        );
        Assert.NotNull(claimed);

        var successor = await queue.EnqueueAsync(
            _db,
            Request("operation-successor", "product-1") with
            {
                PayloadJson = "{\"version\":2}",
            }
        );

        var processing = await _db.Queryable<ProductHqSyncOutbox>()
            .FirstAsync(item => item.Id == first.OutboxId);
        var pending = await _db.Queryable<ProductHqSyncOutbox>()
            .FirstAsync(item => item.Id == successor.OutboxId);
        Assert.Equal(ProductHqSyncOutboxStatuses.Processing, processing.Status);
        Assert.Equal(claimed!.LeaseOwner, processing.LeaseOwner);
        Assert.Equal(claimed.LeaseToken, processing.LeaseToken);
        Assert.NotNull(processing.LeaseExpiresAtUtc);
        Assert.Equal(ProductHqSyncOutboxStatuses.Pending, pending.Status);
        Assert.Empty(successor.SupersededOutboxIds);
    }

    [Fact]
    public async Task Processing租约未过期不可重复领取_过期后由新实例恢复()
    {
        var queue = CreateQueue();
        var enqueued = await queue.EnqueueAsync(_db, Request("operation-lease", "product-1"));

        var first = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
            _db,
            "instance-a",
            UtcNow,
            _options
        );
        var duplicate = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
            _db,
            "instance-b",
            UtcNow.AddSeconds(30),
            _options
        );
        var recovered = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
            _db,
            "instance-b",
            UtcNow.AddSeconds(61),
            _options
        );

        Assert.NotNull(first);
        Assert.Equal(enqueued.OutboxId, first!.OutboxId);
        Assert.Equal(1, first.AttemptCount);
        Assert.Null(duplicate);
        Assert.NotNull(recovered);
        Assert.Equal(enqueued.OutboxId, recovered!.OutboxId);
        Assert.Equal(2, recovered.AttemptCount);
    }

    [Fact]
    public async Task 双Worker重叠Scope在旧租约过期时仍禁止HQ并发和晚提交()
    {
        var now = DateTime.UtcNow;
        var queue = new ProductHqSyncOutboxQueue(
            _db,
            Options.Create(_options),
            new FixedTimeProvider(now.AddMinutes(-5))
        );
        var first = await queue.EnqueueAsync(
            _db,
            Request("operation-all", "product-serial") with { TargetStoreCodes = null }
        );
        var executor = new BlockingRecordingExecutor();
        using var services = CreateWorkerServices(executor);
        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        var workerA = new ProductHqSyncOutboxWorker(
            scopeFactory,
            Options.Create(_options),
            NullLogger<ProductHqSyncOutboxWorker>.Instance
        );
        var workerB = new ProductHqSyncOutboxWorker(
            scopeFactory,
            Options.Create(_options),
            NullLogger<ProductHqSyncOutboxWorker>.Instance
        );

        var firstRun = InvokeProcessNextAsync(workerA);
        await executor.FirstStarted.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var successor = await queue.EnqueueAsync(
            _db,
            Request("operation-store", "product-serial") with
            {
                TargetStoreCodes = new List<string> { "S1" },
            }
        );
        await _db.Updateable<ProductHqSyncOutbox>()
            .SetColumns(item => item.LeaseExpiresAtUtc == now.AddMinutes(-1))
            .Where(item => item.Id == first.OutboxId)
            .ExecuteCommandAsync();

        var secondRun = InvokeProcessNextAsync(workerB);
        var secondEnteredBeforeFirstFinished =
            await Task.WhenAny(executor.SecondStarted.Task, Task.Delay(300))
            == executor.SecondStarted.Task;
        executor.ReleaseFirst.TrySetResult();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.False(secondEnteredBeforeFirstFinished);
        var rowsAfterFirst = await _db.Queryable<ProductHqSyncOutbox>().ToListAsync();
        Assert.Equal(
            ProductHqSyncOutboxStatuses.Succeeded,
            rowsAfterFirst.Single(item => item.Id == first.OutboxId).Status
        );
        Assert.Equal(
            ProductHqSyncOutboxStatuses.Pending,
            rowsAfterFirst.Single(item => item.Id == successor.OutboxId).Status
        );

        Assert.True(await InvokeProcessNextAsync(workerB));
        Assert.Equal(new[] { "operation-all", "operation-store" }, executor.OperationKeys);
    }

    [Fact]
    public async Task 二十个被前序阻塞的后继不会饿死窗口外已到期商品()
    {
        var rows = new List<ProductHqSyncOutbox>();
        var createdAt = UtcNow.AddMinutes(-10);
        for (var index = 0; index < 20; index++)
        {
            var productCode = $"BLOCKED-{index:00}";
            rows.Add(
                OutboxRow(
                    $"blocked-prior-{index:00}",
                    productCode,
                    "all",
                    ProductHqSyncOutboxStatuses.Retrying,
                    createdAt.AddTicks(index * 2),
                    UtcNow.AddHours(1),
                    Guid.Parse($"00000000-0000-0000-0000-{index * 2 + 1:000000000000}")
                )
            );
            rows.Add(
                OutboxRow(
                    $"blocked-successor-{index:00}",
                    productCode,
                    "stores:S1",
                    ProductHqSyncOutboxStatuses.Pending,
                    createdAt.AddTicks(index * 2 + 1),
                    UtcNow.AddMinutes(-1),
                    Guid.Parse($"00000000-0000-0000-0000-{index * 2 + 2:000000000000}")
                )
            );
        }
        rows.Add(
            OutboxRow(
                "ready-operation",
                "READY-PRODUCT",
                "all",
                ProductHqSyncOutboxStatuses.Pending,
                createdAt.AddMinutes(1),
                UtcNow.AddMinutes(-1)
            )
        );
        await _db.Insertable(rows).ExecuteCommandAsync();

        var claimed = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
            _db,
            "instance-ready",
            UtcNow,
            new ProductHqSyncOutboxOptions { ClaimBatchSize = 20 }
        );

        Assert.NotNull(claimed);
        Assert.Equal("ready-operation", claimed!.OperationKey);
    }

    [Fact]
    public async Task 首个领取窗口在查询后被并发占用_继续翻页领取后续商品()
    {
        var createdAt = UtcNow.AddMinutes(-10);
        var rows = Enumerable.Range(0, 20)
            .Select(index =>
                OutboxRow(
                    $"contended-operation-{index:00}",
                    $"CONTENDED-PRODUCT-{index:00}",
                    "all",
                    ProductHqSyncOutboxStatuses.Pending,
                    createdAt,
                    UtcNow.AddMinutes(-1),
                    Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}")
                )
            )
            .Append(
                OutboxRow(
                    "ready-after-window",
                    "READY-AFTER-WINDOW",
                    "all",
                    ProductHqSyncOutboxStatuses.Pending,
                    createdAt,
                    UtcNow.AddMinutes(-1),
                    Guid.Parse("00000000-0000-0000-0000-000000000021")
                )
            )
            .ToList();
        await _db.Insertable(rows).ExecuteCommandAsync();

        using var competingDb = CreateIndependentDb();
        var competingLeaseToken = Guid.NewGuid();
        var competingLeaseExpiresAt = UtcNow.AddHours(1);
        var selectCount = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (
                !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("ProductHqSyncOutbox", StringComparison.Ordinal)
                || Interlocked.Increment(ref selectCount) != 2
            )
            {
                return;
            }

            // 首个窗口已被读出后，模拟其他实例抢先领取该窗口中的全部商品。
            competingDb.Updateable<ProductHqSyncOutbox>()
                .SetColumns(item => item.Status == ProductHqSyncOutboxStatuses.Processing)
                .SetColumns(item => item.LeaseOwner == "competing-worker")
                .SetColumns(item => item.LeaseToken == competingLeaseToken)
                .SetColumns(item => item.LeaseExpiresAtUtc == competingLeaseExpiresAt)
                .Where(item => item.ProductCode.StartsWith("CONTENDED-PRODUCT-"))
                .ExecuteCommand();
        };

        ProductHqSyncOutboxWorkItemDto? claimed;
        try
        {
            claimed = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
                _db,
                "instance-ready",
                UtcNow,
                new ProductHqSyncOutboxOptions { ClaimBatchSize = 20 }
            );
        }
        finally
        {
            _db.Aop.OnLogExecuting = null;
        }

        Assert.NotNull(claimed);
        Assert.Equal("ready-after-window", claimed!.OperationKey);
    }

    [Fact]
    public async Task 已领取任务的持久化Json损坏_安全阻断并继续领取下一任务()
    {
        var createdAt = UtcNow.AddMinutes(-10);
        var poison = OutboxRow(
            "poison-operation",
            "POISON-PRODUCT",
            "all",
            ProductHqSyncOutboxStatuses.Pending,
            createdAt,
            UtcNow.AddMinutes(-1)
        );
        poison.FieldMaskJson = "{not-json";
        var ready = OutboxRow(
            "ready-after-poison",
            "READY-AFTER-POISON",
            "all",
            ProductHqSyncOutboxStatuses.Pending,
            createdAt.AddMinutes(1),
            UtcNow.AddMinutes(-1)
        );
        await _db.Insertable(new[] { poison, ready }).ExecuteCommandAsync();

        var claimed = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
            _db,
            "instance-ready",
            UtcNow,
            new ProductHqSyncOutboxOptions { ClaimBatchSize = 20 }
        );

        Assert.NotNull(claimed);
        Assert.Equal("ready-after-poison", claimed!.OperationKey);
        var poisonedRow = await _db.Queryable<ProductHqSyncOutbox>()
            .SingleAsync(item => item.Id == poison.Id);
        Assert.Equal(ProductHqSyncOutboxStatuses.Blocked, poisonedRow.Status);
        Assert.Equal("PRODUCT_HQ_SYNC_OUTBOX_PAYLOAD_INVALID", poisonedRow.LastErrorCode);
        Assert.Equal("HQ 同步任务数据无效，需要人工处理", poisonedRow.LastErrorMessage);
        Assert.Null(poisonedRow.LeaseOwner);
        Assert.Null(poisonedRow.LeaseToken);
        Assert.Null(poisonedRow.LeaseExpiresAtUtc);
        Assert.Equal(UtcNow, poisonedRow.CompletedAtUtc);
    }

    [Fact]
    public async Task 旧FencingToken不能覆盖租约恢复后的新执行结果()
    {
        var queue = CreateQueue();
        await queue.EnqueueAsync(_db, Request("operation-fencing", "product-1"));
        var first = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
            _db,
            "instance-a",
            UtcNow,
            _options
        );
        var recovered = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
            _db,
            "instance-b",
            UtcNow.AddSeconds(61),
            _options
        );

        Assert.NotNull(first);
        Assert.NotNull(recovered);
        Assert.False(
            await ProductHqSyncOutboxWorker.ApplyResultAsync(
                _db,
                first!,
                ProductHqSyncOutboxExecutionResult.Succeeded(),
                UtcNow.AddSeconds(62),
                _options
            )
        );
        Assert.True(
            await ProductHqSyncOutboxWorker.ApplyResultAsync(
                _db,
                recovered!,
                ProductHqSyncOutboxExecutionResult.Succeeded(),
                UtcNow.AddSeconds(63),
                _options
            )
        );
        Assert.Equal(
            ProductHqSyncOutboxStatuses.Succeeded,
            (await _db.Queryable<ProductHqSyncOutbox>().SingleAsync()).Status
        );
    }

    [Fact]
    public async Task Retryable结果按指数退避_超过旧尝试上限也不丢任务()
    {
        var queue = CreateQueue();
        await queue.EnqueueAsync(_db, Request("operation-retry", "product-1"));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var now = UtcNow.AddMinutes(attempt);
            var claimed = await ProductHqSyncOutboxWorker.TryClaimNextAsync(
                _db,
                $"instance-{attempt}",
                now,
                _options
            );
            Assert.NotNull(claimed);
            await ProductHqSyncOutboxWorker.ApplyResultAsync(
                _db,
                claimed!,
                ProductHqSyncOutboxExecutionResult.Retryable("HQ_UNAVAILABLE", "总部暂时不可用"),
                now,
                _options
            );

            var row = await _db.Queryable<ProductHqSyncOutbox>().SingleAsync();
            Assert.Equal(ProductHqSyncOutboxStatuses.Retrying, row.Status);
            Assert.Equal(now.AddSeconds(5 * (1 << (attempt - 1))), row.NextAttemptAtUtc);
            Assert.Null(row.CompletedAtUtc);
            Assert.Equal("HQ_UNAVAILABLE", row.LastErrorCode);
            Assert.DoesNotContain("Exception", row.LastErrorMessage ?? string.Empty);
        }
    }

    [Fact]
    public async Task Retryable超过旧尝试上限仍保留为Retrying_退避最多一小时()
    {
        var queue = CreateQueue();
        var enqueued = await queue.EnqueueAsync(
            _db,
            Request("operation-unbounded-retry", "product-1")
        );
        var leaseToken = Guid.NewGuid();
        var leaseExpiresAt = UtcNow.AddMinutes(1);
        await _db.Updateable<ProductHqSyncOutbox>()
            .SetColumns(item => item.Status == ProductHqSyncOutboxStatuses.Processing)
            .SetColumns(item => item.AttemptCount == 50)
            .SetColumns(item => item.LeaseOwner == "instance-a")
            .SetColumns(item => item.LeaseToken == leaseToken)
            .SetColumns(item => item.LeaseExpiresAtUtc == leaseExpiresAt)
            .Where(item => item.Id == enqueued.OutboxId)
            .ExecuteCommandAsync();
        var row = await _db.Queryable<ProductHqSyncOutbox>().SingleAsync();
        var options = new ProductHqSyncOutboxOptions
        {
            BaseRetryDelaySeconds = 5,
            MaxRetryDelaySeconds = 7_200,
        };

        Assert.True(
            await ProductHqSyncOutboxWorker.ApplyResultAsync(
                _db,
                ProductHqSyncOutboxQueue.ToWorkItem(row),
                ProductHqSyncOutboxExecutionResult.Retryable(
                    "System.Exception: password=secret\r\n",
                    "HQ 暂时不可用"
                ),
                UtcNow,
                options
            )
        );

        row = await _db.Queryable<ProductHqSyncOutbox>().SingleAsync();
        Assert.Equal(ProductHqSyncOutboxStatuses.Retrying, row.Status);
        Assert.Equal(UtcNow.AddHours(1), row.NextAttemptAtUtc);
        Assert.Equal("PRODUCT_HQ_SYNC_FAILED", row.LastErrorCode);
    }

    [Fact]
    public async Task Worker关闭时不创建Scope也不认领任务()
    {
        var scopeFactory = new Mock<IServiceScopeFactory>(MockBehavior.Strict);
        var worker = new ProductHqSyncOutboxWorker(
            scopeFactory.Object,
            Options.Create(new ProductHqSyncOutboxOptions { Enabled = false }),
            NullLogger<ProductHqSyncOutboxWorker>.Instance
        );

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        scopeFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Blocked任务可人工重试_清理租约并重置尝试次数()
    {
        var queue = CreateQueue();
        var enqueued = await queue.EnqueueAsync(_db, Request("operation-blocked", "product-1"));
        var completedAt = UtcNow;
        await _db.Updateable<ProductHqSyncOutbox>()
            .SetColumns(item => item.Status == ProductHqSyncOutboxStatuses.Blocked)
            .SetColumns(item => item.AttemptCount == 3)
            .SetColumns(item => item.CompletedAtUtc == completedAt)
            .Where(item => item.Id == enqueued.OutboxId)
            .ExecuteCommandAsync();

        var retried = await queue.RetryAsync("operation-blocked", "admin");

        Assert.NotNull(retried);
        Assert.Equal(ProductHqSyncOutboxStatuses.Retrying, retried!.Status);
        Assert.Equal(0, retried.AttemptCount);
        var row = await _db.Queryable<ProductHqSyncOutbox>().SingleAsync();
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAtUtc);
        Assert.Null(row.CompletedAtUtc);
    }

    [Fact]
    public async Task 独立Schema迁移器_SQLite重复执行仍只有一张表和唯一OperationKey索引()
    {
        _db.DbMaintenance.DropTable<ProductHqSyncOutbox>();

        await ProductHqSyncOutboxSchemaMigrator.EnsureAsync(
            _db,
            NullLogger.Instance
        );
        await ProductHqSyncOutboxSchemaMigrator.EnsureAsync(
            _db,
            NullLogger.Instance
        );

        var tableCount = await _db.Ado.GetIntAsync(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'ProductHqSyncOutbox'"
        );
        var indexCount = await _db.Ado.GetIntAsync(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = 'UX_ProductHqSyncOutbox_OperationKey'"
        );
        Assert.Equal(1, tableCount);
        Assert.Equal(1, indexCount);
    }

    [Theory]
    [InlineData("UX_ProductHqSyncOutbox_OperationKey")]
    [InlineData("IX_ProductHqSyncOutbox_Due")]
    [InlineData("IX_ProductHqSyncOutbox_ProductScope")]
    public async Task 独立Schema验证_SQLite缺少任一必要索引均失败(string indexName)
    {
        await ProductHqSyncOutboxSchemaMigrator.EnsureAsync(_db, NullLogger.Instance);
        await _db.Ado.ExecuteCommandAsync($"DROP INDEX \"{indexName}\"");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductHqSyncOutboxSchemaMigrator.VerifyAsync(_db)
        );

        Assert.Contains(indexName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 独立Schema迁移器_SQLite内存库AutoClose时同一连接完成建表和索引()
    {
        using var db = new SqlSugarClient(
            new ConnectionConfig
            {
                ConnectionString = "Data Source=:memory:",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );

        await ProductHqSyncOutboxSchemaMigrator.EnsureAsync(db, NullLogger.Instance);
        await ProductHqSyncOutboxSchemaMigrator.VerifyAsync(db);

        Assert.Equal(
            1,
            await db.Ado.GetIntAsync(
                "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'ProductHqSyncOutbox'"
            )
        );
        Assert.Equal(
            3,
            await db.Ado.GetIntAsync(
                "SELECT COUNT(1) FROM pragma_index_list('ProductHqSyncOutbox') "
                    + "WHERE name IN ('UX_ProductHqSyncOutbox_OperationKey', 'IX_ProductHqSyncOutbox_Due', 'IX_ProductHqSyncOutbox_ProductScope')"
            )
        );
    }

    [Theory]
    [InlineData(
        "IX_ProductHqSyncOutbox_ProductScope",
        "CREATE INDEX \"IX_ProductHqSyncOutbox_ProductScope\" ON \"ProductHqSyncOutbox\"(\"ProductCode\", \"ScopeKey\", \"OccurredAtUtc\" ASC)"
    )]
    [InlineData(
        "IX_ProductHqSyncOutbox_Due",
        "CREATE INDEX \"IX_ProductHqSyncOutbox_Due\" ON \"ProductHqSyncOutbox\"(\"NextAttemptAtUtc\", \"Status\", \"LeaseExpiresAtUtc\", \"CreatedAt\")"
    )]
    [InlineData(
        "UX_ProductHqSyncOutbox_OperationKey",
        "CREATE INDEX \"UX_ProductHqSyncOutbox_OperationKey\" ON \"ProductHqSyncOutbox\"(\"OperationKey\")"
    )]
    public async Task 独立Schema验证_SQLite索引方向列序或Unique错误均失败(
        string indexName,
        string replacementSql
    )
    {
        await ProductHqSyncOutboxSchemaMigrator.EnsureAsync(_db, NullLogger.Instance);
        await _db.Ado.ExecuteCommandAsync($"DROP INDEX \"{indexName}\"");
        await _db.Ado.ExecuteCommandAsync(replacementSql);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProductHqSyncOutboxSchemaMigrator.VerifyAsync(_db)
        );

        Assert.Contains(indexName, error.Message, StringComparison.Ordinal);
    }

    [ProductHqSyncSqlServerFact]
    [Trait("Category", "SQL")]
    public async Task SQLServer商品执行锁_租约过期仍跨连接互斥且释放后不残留()
    {
        var connectionString = Environment.GetEnvironmentVariable("HB_TEST_SQLSERVER_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        using var firstDb = CreateSqlServerDb(connectionString!);
        using var secondDb = CreateSqlServerDb(connectionString!);
        var productCode = $"HQ-LOCK-{Guid.NewGuid():N}";
        using var cancellation = new CancellationTokenSource();
        var firstLock = await ProductHqSyncProductExecutionLock.TryAcquireAsync(
            firstDb,
            productCode,
            cancellation.Token
        );
        Assert.NotNull(firstLock);
        try
        {
            var expiredLeaseAt = DateTime.UtcNow.AddSeconds(-1);
            Assert.True(expiredLeaseAt <= DateTime.UtcNow);
            cancellation.Cancel();

            var competingLock = await ProductHqSyncProductExecutionLock.TryAcquireAsync(
                secondDb,
                productCode.ToLowerInvariant(),
                CancellationToken.None
            );
            Assert.Null(competingLock);
        }
        finally
        {
            await firstLock!.DisposeAsync();
        }

        var recoveredLock = await ProductHqSyncProductExecutionLock.TryAcquireAsync(
            secondDb,
            productCode,
            CancellationToken.None
        );
        Assert.NotNull(recoveredLock);
        await recoveredLock!.DisposeAsync();

        var noResidualLock = await ProductHqSyncProductExecutionLock.TryAcquireAsync(
            firstDb,
            productCode,
            CancellationToken.None
        );
        Assert.NotNull(noResidualLock);
        await noResidualLock!.DisposeAsync();
    }

    [Fact]
    public void 独立Schema迁移器_SQLServer契约包含租约令牌状态约束和领取索引()
    {
        Assert.Contains("[LeaseToken] uniqueidentifier NULL", ProductHqSyncOutboxSchemaMigrator.SqlServerApplySql);
        Assert.Contains("[RequestedByUserGuid] nvarchar(80) NULL", ProductHqSyncOutboxSchemaMigrator.SqlServerApplySql);
        Assert.Contains("[RequestedByDeviceId] nvarchar(200) NULL", ProductHqSyncOutboxSchemaMigrator.SqlServerApplySql);
        Assert.Contains("[AuthorizedStoreCodesJson] nvarchar(max) NOT NULL", ProductHqSyncOutboxSchemaMigrator.SqlServerApplySql);
        Assert.Contains("THROW 51081", ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql);
        Assert.Contains("CK_ProductHqSyncOutbox_Status", ProductHqSyncOutboxSchemaMigrator.SqlServerApplySql);
        Assert.Contains("IX_ProductHqSyncOutbox_Due", ProductHqSyncOutboxSchemaMigrator.SqlServerApplySql);
        Assert.Contains("IX_ProductHqSyncOutbox_ProductScope", ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql);
        Assert.Contains("[is_unique] = 0", ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql);
        Assert.Contains("[key_ordinal] = 4", ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql);
        Assert.Contains("UX_ProductHqSyncOutbox_OperationKey", ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql);
        Assert.Contains("[key_ordinal] = 3", ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql);
        Assert.Contains("[is_descending_key] = 1", ProductHqSyncOutboxSchemaMigrator.SqlServerVerifySql);
        Assert.DoesNotContain("INSERT INTO [dbo].[ProductHqSyncOutbox]", ProductHqSyncOutboxSchemaMigrator.SqlServerApplySql);
    }

    [Fact]
    public void VersionedSchema账本包含独立ProductHqOutbox步骤()
    {
        Assert.Contains(
            SchemaMigrationCoordinator.MainMigrationSteps,
            step => step.MigrationId == SchemaMigrationCoordinator.ProductHqSyncOutboxMigrationId
        );
    }

    [Fact]
    public void 状态控制器复用移动端维护路由且只返回公开Operation状态()
    {
        var route = typeof(ProductHqSyncOperationsController)
            .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
            .Cast<RouteAttribute>()
            .Single();
        Assert.Equal("api/react/v1/store-product-maintenance/hq-sync", route.Template);
        Assert.Empty(
            typeof(ProductHqSyncOperationsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .Cast<AuthorizeAttribute>()
        );
        Assert.Single(
            typeof(ProductHqSyncOperationsController)
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true)
                .Cast<AllowAnonymousAttribute>()
        );
        Assert.Equal(
            "{operationId}",
            typeof(ProductHqSyncOperationsController)
                .GetMethod(nameof(ProductHqSyncOperationsController.GetStatus))!
                .GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
                .Cast<HttpGetAttribute>()
                .Single()
                .Template
        );
        Assert.Equal(
            "{operationId}/retry",
            typeof(ProductHqSyncOperationsController)
                .GetMethod(nameof(ProductHqSyncOperationsController.Retry))!
                .GetCustomAttributes(typeof(HttpPostAttribute), inherit: true)
                .Cast<HttpPostAttribute>()
                .Single()
                .Template
        );
        var publicProperties = typeof(ProductHqSyncOperationStatusDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "OperationId",
                "Status",
                "ProductCode",
                "StoreCode",
                "AttemptCount",
                "NextAttemptAt",
                "Retryable",
                "ErrorCode",
                "Message",
            },
            publicProperties
        );
        Assert.DoesNotContain("OutboxId", publicProperties);
    }

    [Fact]
    public async Task 入队持久化原用户或设备并供状态授权读取()
    {
        var queue = CreateQueue();
        var userRequest = Request("operation-user", "product-1") with
        {
            RequestedByUserGuid = " user-guid-1 ",
            TargetStoreCodes = new List<string> { "s01" },
            AuthorizedStoreCodes = new List<string> { "s01", "s03" },
        };
        var deviceRequest = Request("operation-device", "product-2") with
        {
            RequestedByUserGuid = null,
            RequestedByDeviceId = " device-001 ",
            TargetStoreCodes = new List<string> { "s02" },
            AuthorizedStoreCodes = new List<string> { "s02" },
        };

        await queue.EnqueueAsync(_db, userRequest);
        await queue.EnqueueAsync(_db, deviceRequest);

        var user = await queue.GetAccessDescriptorAsync("operation-user");
        var device = await queue.GetAccessDescriptorAsync("operation-device");
        Assert.Equal("user-guid-1", user!.RequestedByUserGuid);
        Assert.Null(user.RequestedByDeviceId);
        Assert.Equal(new[] { "s01" }, user.TargetStoreCodes);
        Assert.Equal(new[] { "s01", "s03" }, user.AuthorizedStoreCodes);
        Assert.Equal("device-001", device!.RequestedByDeviceId);
        Assert.Null(device.RequestedByUserGuid);
        Assert.Equal(new[] { "s02" }, device.TargetStoreCodes);
        Assert.Equal(new[] { "s02" }, device.AuthorizedStoreCodes);
    }

    [Fact]
    public void 状态授权要求原Actor和当前分店Scope并按操作类型选择权限()
    {
        var userOperation = new ProductHqSyncOutboxAccessDescriptor
        {
            OperationKind = ProductMaintenanceHqOperationKinds.StorePriceUpdated,
            RequestedByUserGuid = "user-guid-1",
            TargetStoreCodes = new[] { "S01" },
            AuthorizedStoreCodes = new[] { "S01" },
        };
        var deviceOperation = new ProductHqSyncOutboxAccessDescriptor
        {
            OperationKind = ProductMaintenanceHqOperationKinds.ProductCreated,
            RequestedByDeviceId = "device-001",
            TargetStoreCodes = Array.Empty<string>(),
            AuthorizedStoreCodes = new[] { "S02" },
        };
        var crossStoreProjectionOperation = new ProductHqSyncOutboxAccessDescriptor
        {
            OperationKind = ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            RequestedByUserGuid = "user-guid-1",
            TargetStoreCodes = new[] { "S01", "S02" },
            AuthorizedStoreCodes = new[] { "S01" },
        };
        var elevatedOperationWithoutStoreSnapshot = new ProductHqSyncOutboxAccessDescriptor
        {
            OperationKind = ProductMaintenanceHqOperationKinds.StorePriceUpdated,
            RequestedByUserGuid = "global-user",
            TargetStoreCodes = new[] { "S01", "S02" },
            AuthorizedStoreCodes = null,
        };

        Assert.Equal(
            Permissions.StoreProducts.Create,
            ProductHqSyncOperationsController.RequiredPermission(
                ProductMaintenanceHqOperationKinds.ProductCreated
            )
        );
        Assert.Equal(
            Permissions.StoreProducts.Edit,
            ProductHqSyncOperationsController.RequiredPermission(
                ProductMaintenanceHqOperationKinds.StorePriceUpdated
            )
        );
        Assert.True(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                userOperation,
                "user-guid-1",
                null,
                new[] { "S01" },
                hasGlobalStoreScope: false
            )
        );
        Assert.False(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                userOperation,
                "user-guid-2",
                null,
                new[] { "S01" },
                hasGlobalStoreScope: false
            )
        );
        Assert.False(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                userOperation,
                "user-guid-1",
                null,
                new[] { "S02" },
                hasGlobalStoreScope: false
            )
        );
        Assert.True(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                deviceOperation,
                null,
                "device-001",
                new[] { "S02" },
                hasGlobalStoreScope: false
            )
        );
        Assert.False(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                deviceOperation,
                null,
                "device-002",
                new[] { "S02" },
                hasGlobalStoreScope: false
            )
        );
        Assert.True(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                crossStoreProjectionOperation,
                "user-guid-1",
                null,
                new[] { "S01" },
                hasGlobalStoreScope: false
            )
        );
        Assert.False(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                crossStoreProjectionOperation,
                "user-guid-2",
                null,
                new[] { "S01" },
                hasGlobalStoreScope: false
            )
        );
        Assert.False(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                crossStoreProjectionOperation,
                "user-guid-1",
                null,
                new[] { "S02" },
                hasGlobalStoreScope: false
            )
        );
        Assert.True(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                elevatedOperationWithoutStoreSnapshot,
                "global-user",
                null,
                allowedStoreCodes: null,
                hasGlobalStoreScope: true
            )
        );
        Assert.False(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                elevatedOperationWithoutStoreSnapshot,
                "other-user",
                null,
                allowedStoreCodes: null,
                hasGlobalStoreScope: true
            )
        );
        Assert.False(
            ProductHqSyncOperationsController.HasActorAndStoreScope(
                elevatedOperationWithoutStoreSnapshot,
                "global-user",
                null,
                allowedStoreCodes: null,
                hasGlobalStoreScope: false
            )
        );
    }

    [Fact]
    public async Task 登录用户状态查询同时校验Edit权限_原用户和当前分店Scope()
    {
        await _db.Insertable(new Store
        {
            StoreGUID = "store-guid-1",
            StoreCode = "S01",
            StoreName = "Store 01",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "user-store-1",
            UserGUID = "user-guid-1",
            StoreGUID = "store-guid-1",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var descriptor = Descriptor(
            "operation-user-status",
            ProductMaintenanceHqOperationKinds.StorePriceUpdated,
            requestedByUserGuid: "user-guid-1",
            authorizedStoreCodes: new[] { "S01" }
        );
        var queue = new Mock<IProductHqSyncOutboxQueue>(MockBehavior.Strict);
        queue.Setup(item => item.GetAccessDescriptorAsync(
                descriptor.Operation.OperationId,
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(descriptor);
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(item => item.GetCurrentUserGuid()).Returns("user-guid-1");
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(item => item.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                null,
                Permissions.StoreProducts.Edit
            ))
            .ReturnsAsync(AuthorizationResult.Success());
        var controller = CreateStatusController(
            queue.Object,
            currentUser.Object,
            authorization.Object,
            Mock.Of<IDeviceRegistrationService>(),
            Mock.Of<IMapper>()
        );
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-guid-1"),
                    new Claim(ClaimTypes.Name, "tester"),
                },
                "test"
            )
        );

        var response = await controller.GetStatus(
            descriptor.Operation.OperationId,
            CancellationToken.None
        );

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ApiResponse<ProductHqSyncOperationStatusDto>>(ok.Value);
        Assert.Same(descriptor.Operation, body.Data);
        authorization.VerifyAll();
    }

    [Fact]
    public async Task 登录原用户可查询并重试授权店内发起的跨店后台投影()
    {
        await _db.Insertable(new Store
        {
            StoreGUID = "store-guid-cross-user",
            StoreCode = "S01",
            StoreName = "Store 01",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        await _db.Insertable(new UserStore
        {
            UserStoreGUID = "user-store-cross-user",
            UserGUID = "user-guid-cross",
            StoreGUID = "store-guid-cross-user",
            IsDeleted = false,
        }).ExecuteCommandAsync();
        var descriptor = Descriptor(
            "operation-user-cross-store",
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            requestedByUserGuid: "user-guid-cross",
            authorizedStoreCodes: new[] { "S01" },
            targetStoreCodes: new[] { "S01", "S02" },
            operationStatus: ProductHqSyncOutboxStatuses.Blocked
        );
        var retried = new ProductHqSyncOperationStatusDto
        {
            OperationId = descriptor.Operation.OperationId,
            Status = ProductHqSyncOutboxStatuses.Pending,
            ProductCode = descriptor.Operation.ProductCode,
            Retryable = true,
        };
        var queue = new Mock<IProductHqSyncOutboxQueue>(MockBehavior.Strict);
        queue.Setup(item => item.GetAccessDescriptorAsync(
                descriptor.Operation.OperationId,
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(descriptor);
        queue.Setup(item => item.RetryAsync(
                descriptor.Operation.OperationId,
                "tester",
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(retried);
        var currentUser = new Mock<ICurrentUserService>(MockBehavior.Strict);
        currentUser.Setup(item => item.GetCurrentUserGuid()).Returns("user-guid-cross");
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(item => item.AuthorizeAsync(
                It.IsAny<ClaimsPrincipal>(),
                null,
                Permissions.StoreProducts.Edit
            ))
            .ReturnsAsync(AuthorizationResult.Success());
        var controller = CreateStatusController(
            queue.Object,
            currentUser.Object,
            authorization.Object,
            Mock.Of<IDeviceRegistrationService>(),
            Mock.Of<IMapper>()
        );
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "user-guid-cross"),
                    new Claim(ClaimTypes.Name, "tester"),
                },
                "test"
            )
        );

        var statusResponse = await controller.GetStatus(
            descriptor.Operation.OperationId,
            CancellationToken.None
        );
        var retryResponse = await controller.Retry(
            descriptor.Operation.OperationId,
            CancellationToken.None
        );

        Assert.IsType<OkObjectResult>(statusResponse.Result);
        var retryOk = Assert.IsType<OkObjectResult>(retryResponse.Result);
        var retryBody = Assert.IsType<ApiResponse<ProductHqSyncOperationStatusDto>>(retryOk.Value);
        Assert.Same(retried, retryBody.Data);
        queue.Verify(item => item.GetAccessDescriptorAsync(
            descriptor.Operation.OperationId,
            It.IsAny<CancellationToken>()
        ), Times.Exactly(2));
        queue.VerifyAll();
        currentUser.Verify(item => item.GetCurrentUserGuid(), Times.Exactly(2));
        authorization.Verify(item => item.AuthorizeAsync(
            It.IsAny<ClaimsPrincipal>(),
            null,
            Permissions.StoreProducts.Edit
        ), Times.Exactly(2));
    }

    [Fact]
    public async Task 匿名设备状态查询重新校验授权码_原设备和当前绑定分店()
    {
        var descriptor = Descriptor(
            "operation-device-status",
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            requestedByDeviceId: "device-001",
            authorizedStoreCodes: new[] { "S02" }
        );
        var queue = new Mock<IProductHqSyncOutboxQueue>(MockBehavior.Strict);
        queue.Setup(item => item.GetAccessDescriptorAsync(
                descriptor.Operation.OperationId,
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(descriptor);
        var deviceService = new Mock<IDeviceRegistrationService>(MockBehavior.Strict);
        deviceService.Setup(item => item.ValidateDeviceAuthCodeAsync("device-001", "auth-001"))
            .ReturnsAsync(true);
        deviceService.Setup(item => item.GetDeviceByHardwareIdAsync("device-001"))
            .ReturnsAsync(new POSM_设备注册信息表());
        var mapper = new Mock<IMapper>(MockBehavior.Strict);
        mapper.Setup(item => item.Map<DeviceDataDto>(It.IsAny<object>()))
            .Returns(new DeviceDataDto
            {
                HardwareId = "device-001",
                Status = 1,
                StoreCode = "S02",
            });
        var controller = CreateStatusController(
            queue.Object,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IAuthorizationService>(),
            deviceService.Object,
            mapper.Object
        );
        controller.ControllerContext.HttpContext.Request.Headers["X-Device-Id"] = "device-001";
        controller.ControllerContext.HttpContext.Request.Headers["X-Auth-Code"] = "auth-001";

        var response = await controller.GetStatus(
            descriptor.Operation.OperationId,
            CancellationToken.None
        );

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ApiResponse<ProductHqSyncOperationStatusDto>>(ok.Value);
        Assert.Same(descriptor.Operation, body.Data);
        deviceService.VerifyAll();
        mapper.VerifyAll();
    }

    [Fact]
    public async Task 匿名原设备可查询并重试绑定店内发起的跨店后台投影()
    {
        var descriptor = Descriptor(
            "operation-device-cross-store",
            ProductMaintenanceHqOperationKinds.ProductCodesUpdated,
            requestedByDeviceId: "device-cross",
            authorizedStoreCodes: new[] { "S01" },
            targetStoreCodes: new[] { "S01", "S02" },
            operationStatus: ProductHqSyncOutboxStatuses.Blocked
        );
        var retried = new ProductHqSyncOperationStatusDto
        {
            OperationId = descriptor.Operation.OperationId,
            Status = ProductHqSyncOutboxStatuses.Pending,
            ProductCode = descriptor.Operation.ProductCode,
            Retryable = true,
        };
        var queue = new Mock<IProductHqSyncOutboxQueue>(MockBehavior.Strict);
        queue.Setup(item => item.GetAccessDescriptorAsync(
                descriptor.Operation.OperationId,
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(descriptor);
        queue.Setup(item => item.RetryAsync(
                descriptor.Operation.OperationId,
                "device:device-cross",
                It.IsAny<CancellationToken>()
            ))
            .ReturnsAsync(retried);
        var deviceService = new Mock<IDeviceRegistrationService>(MockBehavior.Strict);
        deviceService.Setup(item => item.ValidateDeviceAuthCodeAsync("device-cross", "auth-cross"))
            .ReturnsAsync(true);
        deviceService.Setup(item => item.GetDeviceByHardwareIdAsync("device-cross"))
            .ReturnsAsync(new POSM_设备注册信息表());
        var mapper = new Mock<IMapper>(MockBehavior.Strict);
        mapper.Setup(item => item.Map<DeviceDataDto>(It.IsAny<object>()))
            .Returns(new DeviceDataDto
            {
                HardwareId = "device-cross",
                Status = 1,
                StoreCode = "S01",
            });
        var controller = CreateStatusController(
            queue.Object,
            Mock.Of<ICurrentUserService>(),
            Mock.Of<IAuthorizationService>(),
            deviceService.Object,
            mapper.Object
        );
        controller.ControllerContext.HttpContext.Request.Headers["X-Device-Id"] = "device-cross";
        controller.ControllerContext.HttpContext.Request.Headers["X-Auth-Code"] = "auth-cross";

        var statusResponse = await controller.GetStatus(
            descriptor.Operation.OperationId,
            CancellationToken.None
        );
        var retryResponse = await controller.Retry(
            descriptor.Operation.OperationId,
            CancellationToken.None
        );

        Assert.IsType<OkObjectResult>(statusResponse.Result);
        var retryOk = Assert.IsType<OkObjectResult>(retryResponse.Result);
        var retryBody = Assert.IsType<ApiResponse<ProductHqSyncOperationStatusDto>>(retryOk.Value);
        Assert.Same(retried, retryBody.Data);
        queue.Verify(item => item.GetAccessDescriptorAsync(
            descriptor.Operation.OperationId,
            It.IsAny<CancellationToken>()
        ), Times.Exactly(2));
        queue.VerifyAll();
        deviceService.Verify(item => item.ValidateDeviceAuthCodeAsync(
            "device-cross",
            "auth-cross"
        ), Times.Exactly(2));
        deviceService.Verify(item => item.GetDeviceByHardwareIdAsync("device-cross"), Times.Exactly(2));
        mapper.Verify(item => item.Map<DeviceDataDto>(It.IsAny<object>()), Times.Exactly(2));
    }

    private ServiceProvider CreateWorkerServices(IProductHqSyncOutboxExecutor executor)
    {
        var services = new ServiceCollection();
        services.AddScoped<ISqlSugarClient>(_ => CreateIndependentDb());
        services.AddScoped(serviceProvider =>
            CreateSqlSugarContext(serviceProvider.GetRequiredService<ISqlSugarClient>())
        );
        services.AddSingleton(executor);
        return services.BuildServiceProvider();
    }

    private static Task<bool> InvokeProcessNextAsync(ProductHqSyncOutboxWorker worker) =>
        (Task<bool>)
            typeof(ProductHqSyncOutboxWorker)
                .GetMethod("ProcessNextAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(worker, new object[] { CancellationToken.None })!;

    private ProductHqSyncOutboxQueue CreateQueue() =>
        new(_db, Options.Create(_options), new FixedTimeProvider(UtcNow));

    private ProductHqSyncOperationsController CreateStatusController(
        IProductHqSyncOutboxQueue queue,
        ICurrentUserService currentUserService,
        IAuthorizationService authorizationService,
        IDeviceRegistrationService deviceRegistrationService,
        IMapper mapper
    ) =>
        new(
            queue,
            currentUserService,
            authorizationService,
            deviceRegistrationService,
            mapper,
            CreateSqlSugarContext(_db),
            NullLogger<ProductHqSyncOperationsController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static ProductHqSyncOutboxAccessDescriptor Descriptor(
        string operationId,
        string operationKind,
        string? requestedByUserGuid = null,
        string? requestedByDeviceId = null,
        IReadOnlyList<string>? authorizedStoreCodes = null,
        IReadOnlyList<string>? targetStoreCodes = null,
        string operationStatus = ProductHqSyncOutboxStatuses.Pending
    ) =>
        new()
        {
            Operation = new ProductHqSyncOperationStatusDto
            {
                OperationId = operationId,
                Status = operationStatus,
                ProductCode = "P001",
                AttemptCount = 0,
                Retryable = true,
                Message = "等待同步到 HQ",
            },
            OperationKind = operationKind,
            RequestedByUserGuid = requestedByUserGuid,
            RequestedByDeviceId = requestedByDeviceId,
            AuthorizedStoreCodes = authorizedStoreCodes,
            TargetStoreCodes = targetStoreCodes ?? authorizedStoreCodes,
        };

    private static SqlSugarContext CreateSqlSugarContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(
            typeof(SqlSugarContext)
        );
        var dbField = typeof(SqlSugarContext).GetField(
            "_db",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        dbField!.SetValue(context, db);
        return context;
    }

    private static ProductHqSyncOutboxEnqueueRequest Request(string operationKey, string productCode) =>
        new()
        {
            OperationKey = operationKey,
            OperationKind = "upsert",
            ProductCode = productCode,
            Source = "tests",
            RequestedByUserGuid = "test-user-guid",
            OccurredAtUtc = UtcNow,
            TargetStoreCodes = null,
            FieldMask = new List<string> { "productName" },
            PayloadJson = "{}",
        };

    private static ProductHqSyncOutbox OutboxRow(
        string operationKey,
        string productCode,
        string scopeKey,
        string status,
        DateTime createdAt,
        DateTime nextAttemptAt,
        Guid? id = null
    ) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            OperationKey = operationKey,
            OperationKind = "upsert",
            ProductCode = productCode,
            ScopeKey = scopeKey,
            TargetStoreCodesJson = "null",
            AuthorizedStoreCodesJson = "null",
            FieldMaskJson = "[]",
            PayloadJson = "{}",
            TombstonesJson = "[]",
            Source = "tests",
            RequestedByUserGuid = "test-user-guid",
            Status = status,
            OccurredAtUtc = createdAt,
            NextAttemptAtUtc = nextAttemptAt,
            CreatedAt = createdAt,
            CreatedBy = "tests",
            UpdatedAt = createdAt,
            UpdatedBy = "tests",
            IsDeleted = false,
        };

    private SqlSugarClient CreateDb() =>
        new(
            new ConnectionConfig
            {
                ConnectionString = $"Data Source={_dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );

    private SqlSugarClient CreateIndependentDb() =>
        new(
            new ConnectionConfig
            {
                ConnectionString = $"Data Source={_dbPath}",
                DbType = DbType.Sqlite,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (
            directory != null
            && (
                !Directory.Exists(Path.Combine(directory.FullName, "apps"))
                || !Directory.Exists(Path.Combine(directory.FullName, "services"))
            )
        )
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("无法定位 hb-platform 仓库根目录。");
    }

    private static SqlSugarClient CreateSqlServerDb(string connectionString) =>
        new(
            new ConnectionConfig
            {
                ConnectionString = connectionString,
                DbType = DbType.SqlServer,
                IsAutoCloseConnection = true,
                InitKeyType = InitKeyType.Attribute,
            }
        );

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class BlockingRecordingExecutor : IProductHqSyncOutboxExecutor
    {
        private readonly Lock _gate = new();
        private readonly List<string> _operationKeys = new();
        private int _executionCount;

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> OperationKeys
        {
            get
            {
                lock (_gate)
                {
                    return _operationKeys.ToArray();
                }
            }
        }

        public async Task<ProductHqSyncOutboxExecutionResult> ExecuteAsync(
            ProductHqSyncOutboxWorkItemDto workItem,
            CancellationToken cancellationToken = default
        )
        {
            lock (_gate)
            {
                _operationKeys.Add(workItem.OperationKey);
            }

            if (Interlocked.Increment(ref _executionCount) == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondStarted.TrySetResult();
            }

            return ProductHqSyncOutboxExecutionResult.Succeeded();
        }
    }
}
