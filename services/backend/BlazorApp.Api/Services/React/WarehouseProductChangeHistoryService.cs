using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services.React;

public sealed class WarehouseProductChangeHistoryService : IWarehouseProductChangeHistoryService
{
    private const int SnapshotQueryBatchSize = 1000;
    // 每行约 11 个参数；100 行可稳定低于 SQL Server 2100 参数上限。
    private const int HistoryInsertPageSize = 100;

    private static readonly JsonSerializerOptions ChangeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static readonly IReadOnlyList<SnapshotField> SnapshotFields =
    [
        new("productCode", "string", snapshot => snapshot?.ProductCode),
        new(
            "domesticPrice",
            "decimal",
            snapshot => snapshot?.DomesticPrice,
            Sources(
                snapshot => snapshot?.WarehouseSource?.DomesticPrice,
                snapshot => snapshot?.DomesticSource?.DomesticPrice
            )
        ),
        new(
            "importPrice",
            "decimal",
            snapshot => snapshot?.ImportPrice,
            Sources(
                snapshot => snapshot?.WarehouseSource?.ImportPrice,
                snapshot => snapshot?.ProductSource?.ImportPrice,
                snapshot => snapshot?.DomesticSource?.ImportPrice
            )
        ),
        new(
            "retailPrice",
            "decimal",
            snapshot => snapshot?.RetailPrice,
            Sources(
                snapshot => snapshot?.WarehouseSource?.RetailPrice,
                snapshot => snapshot?.ProductSource?.RetailPrice,
                snapshot => snapshot?.DomesticSource?.RetailPrice
            )
        ),
        // 建议折扣存于 ProductSuggestedDiscount 旁表，没有多张镜像表，直接比较有效值。
        new("suggestedDiscountRate", "decimal", snapshot => snapshot?.SuggestedDiscountRate),
        new(
            "localSupplierCode",
            "string",
            snapshot => snapshot?.LocalSupplierCode,
            Sources(snapshot => snapshot?.ProductSource?.LocalSupplierCode)
        ),
        new(
            "domesticSupplierCode",
            "string",
            snapshot => snapshot?.DomesticSupplierCode,
            Sources(snapshot => snapshot?.DomesticSource?.DomesticSupplierCode)
        ),
        new(
            "productName",
            "string",
            snapshot => snapshot?.ProductName,
            Sources(
                snapshot => snapshot?.ProductSource?.ProductName,
                snapshot => snapshot?.DomesticSource?.ProductName
            )
        ),
        new(
            "englishName",
            "string",
            snapshot => snapshot?.EnglishName,
            Sources(
                snapshot => snapshot?.ProductSource?.EnglishName,
                snapshot => snapshot?.DomesticSource?.EnglishName
            )
        ),
        new(
            "itemNumber",
            "string",
            snapshot => snapshot?.ItemNumber,
            Sources(
                snapshot => snapshot?.ProductSource?.ItemNumber,
                snapshot => snapshot?.DomesticSource?.ItemNumber
            )
        ),
        new(
            "barcode",
            "string",
            snapshot => snapshot?.Barcode,
            Sources(
                snapshot => snapshot?.ProductSource?.Barcode,
                snapshot => snapshot?.DomesticSource?.Barcode
            )
        ),
        new(
            "productType",
            "int",
            snapshot => snapshot?.ProductType,
            Sources(
                snapshot => snapshot?.ProductSource?.ProductType,
                snapshot => snapshot?.DomesticSource?.ProductType
            )
        ),
        new(
            "productCategoryGuid",
            "string",
            snapshot => snapshot?.ProductCategoryGuid,
            Sources(snapshot => snapshot?.ProductSource?.ProductCategoryGuid)
        ),
        new(
            "warehouseCategoryGuid",
            "string",
            snapshot => snapshot?.WarehouseCategoryGuid,
            Sources(snapshot => snapshot?.ProductSource?.WarehouseCategoryGuid)
        ),
        new(
            "middlePackageQuantity",
            "int",
            snapshot => snapshot?.MiddlePackageQuantity,
            Sources(snapshot => snapshot?.ProductSource?.MiddlePackageQuantity)
        ),
        new(
            "middlePackQuantity",
            "int",
            snapshot => snapshot?.MiddlePackQuantity,
            Sources(snapshot => snapshot?.DomesticSource?.MiddlePackQuantity)
        ),
        new(
            "packingQuantity",
            "int",
            snapshot => snapshot?.PackingQuantity,
            Sources(
                snapshot => snapshot?.WarehouseSource?.PackingQuantity,
                snapshot => snapshot?.DomesticSource?.PackingQuantity
            )
        ),
        new(
            "volume",
            "decimal",
            snapshot => snapshot?.Volume,
            Sources(
                snapshot => snapshot?.WarehouseSource?.Volume,
                snapshot => snapshot?.DomesticSource?.Volume
            )
        ),
        new(
            "minOrderQuantity",
            "int",
            snapshot => snapshot?.MinOrderQuantity,
            Sources(snapshot => snapshot?.WarehouseSource?.MinOrderQuantity)
        ),
        new(
            "productImage",
            "string",
            snapshot => snapshot?.ProductImage,
            Sources(
                snapshot => snapshot?.ProductSource?.ProductImage,
                snapshot => snapshot?.DomesticSource?.ProductImage
            )
        ),
        new(
            "isAutoPricing",
            "bool",
            snapshot => snapshot?.IsAutoPricing,
            Sources(snapshot => snapshot?.ProductSource?.IsAutoPricing)
        ),
        new(
            "isActive",
            "bool",
            snapshot => snapshot?.IsActive,
            Sources(
                snapshot => snapshot?.WarehouseSource?.IsActive,
                snapshot => snapshot?.ProductSource?.IsActive,
                snapshot => snapshot?.DomesticSource?.IsActive
            )
        ),
    ];

    private readonly SqlSugarContext _context;
    private readonly ILogger<WarehouseProductChangeHistoryService> _logger;
    private readonly ICurrentUserService _currentUserService;
    private readonly IStorePriceUpdateTaskService? _priceTaskService;

    // 会触发分店价格更新通知的字段。
    private static readonly HashSet<string> PriceNotificationFieldKeys = new(StringComparer.Ordinal)
    {
        "retailPrice",
        "suggestedDiscountRate",
    };

    public WarehouseProductChangeHistoryService(
        SqlSugarContext context,
        ILogger<WarehouseProductChangeHistoryService> logger,
        ICurrentUserService currentUserService,
        // 可选：现有测试与工具直接 new 本服务，不应因新增通知功能而被迫改动。
        IStorePriceUpdateTaskService? priceTaskService = null
    )
    {
        _context = context;
        _logger = logger;
        _currentUserService = currentUserService;
        _priceTaskService = priceTaskService;
    }

    public async Task<IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto>> CaptureSnapshotsAsync(
        IEnumerable<string> productCodes,
        CancellationToken cancellationToken = default
    )
    {
        var codes = NormalizeProductCodes(productCodes);
        if (codes.Count == 0)
        {
            return new Dictionary<string, WarehouseProductChangeSnapshotDto>(StringComparer.OrdinalIgnoreCase);
        }

        var warehouseProducts = new List<WarehouseProduct>();
        var products = new List<Product>();
        var domesticProducts = new List<DomesticProduct>();
        foreach (var codeBatch in codes.Chunk(SnapshotQueryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchCodes = codeBatch.ToList();
            warehouseProducts.AddRange(
                await _context.Db
                    .Queryable<WarehouseProduct>()
                    .Where(item => batchCodes.Contains(item.ProductCode))
                    .ToListAsync()
            );
            products.AddRange(
                await _context.Db
                    .Queryable<Product>()
                    .Where(item =>
                        item.ProductCode != null && batchCodes.Contains(item.ProductCode)
                    )
                    .ToListAsync()
            );
            domesticProducts.AddRange(
                await _context.Db
                    .Queryable<DomesticProduct>()
                    .Where(item => batchCodes.Contains(item.ProductCode))
                    .ToListAsync()
            );
        }

        // 审计 after 快照需要看见刚被软删除的行，才能记录 IsActive=true -> false；
        // 同编码同时存在有效与历史行时仍优先有效行，避免旧记录遮蔽当前主档。
        var warehouseByCode = warehouseProducts
            .GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.IsDeleted)
                    .ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt)
                    .First(),
                StringComparer.OrdinalIgnoreCase
            );
        var productByCode = products
            .Where(item => !string.IsNullOrWhiteSpace(item.ProductCode))
            .GroupBy(item => item.ProductCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.IsDeleted)
                    .ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt)
                    .ThenByDescending(item => item.CreatedAt)
                    .First(),
                StringComparer.OrdinalIgnoreCase
            );
        var domesticByCode = domesticProducts
            .GroupBy(item => item.ProductCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => item.IsDeleted)
                    .ThenByDescending(item => item.UpdatedAt ?? item.CreatedAt)
                    .ThenByDescending(item => item.CreatedAt)
                    .First(),
                StringComparer.OrdinalIgnoreCase
            );
        var snapshots = new Dictionary<string, WarehouseProductChangeSnapshotDto>(StringComparer.OrdinalIgnoreCase);
        var suggestedDiscounts = _priceTaskService == null
            ? null
            : await _priceTaskService.GetSuggestedDiscountsAsync(codes, cancellationToken);

        foreach (var code in codes)
        {
            warehouseByCode.TryGetValue(code, out var warehouse);
            productByCode.TryGetValue(code, out var product);
            domesticByCode.TryGetValue(code, out var domestic);
            if (warehouse == null && product == null && domestic == null)
            {
                continue;
            }

            var canonicalCode = warehouse?.ProductCode
                ?? product?.ProductCode
                ?? domestic?.ProductCode
                ?? code;
            decimal? suggestedDiscount = null;
            suggestedDiscounts?.TryGetValue(canonicalCode, out suggestedDiscount);
            snapshots[canonicalCode] = CreateSnapshot(
                canonicalCode,
                warehouse,
                product,
                domestic
            ) with { SuggestedDiscountRate = suggestedDiscount };
        }

        return snapshots;
    }

    public async Task<int> RecordChangesAsync(
        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
        IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> afterSnapshots,
        WarehouseProductChangeHistoryContextDto context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        var codes = beforeSnapshots.Keys
            .Concat(afterSnapshots.Keys)
            .Select(NormalizeProductCode)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (codes.Count == 0)
        {
            return 0;
        }

        // 同一服务端批次可能分页调用本服务。已落过历史的商品必须跳过，避免同一批次重复展示。
        if (context.BatchGuid is Guid batchGuid)
        {
            await AcquireBatchWriteLockAsync(batchGuid);
            var recordedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var codeBatch in codes.Chunk(SnapshotQueryBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchCodes = codeBatch.ToList();
                var existingCodes = await _context.Db
                    .Queryable<WarehouseProductChangeHistory>()
                    .Where(item => item.BatchGuid == batchGuid)
                    .Where(item => batchCodes.Contains(item.ProductCode))
                    .Select(item => item.ProductCode)
                    .ToListAsync();
                recordedCodes.UnionWith(existingCodes);
            }

            codes.RemoveAll(code => recordedCodes.Contains(code));
            if (codes.Count == 0)
            {
                return 0;
            }
        }

        var histories = new List<WarehouseProductChangeHistory>();
        var actor = ResolveActor(context);
        var occurredAtUtc = ToUtc(context.OccurredAtUtc ?? DateTime.UtcNow);
        var changedCodes = new List<string>();
        var priceChangedCodes = new List<string>();
        var unchangedWarehouseSnapshots = new List<WarehouseAuditRestoreSnapshot>();
        foreach (var code in codes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 缺少键表示该侧快照不存在，避免用 nullable 字典值表达缺失商品。
            var before = beforeSnapshots.TryGetValue(code, out var beforeSnapshot)
                ? beforeSnapshot
                : null;
            var after = afterSnapshots.TryGetValue(code, out var afterSnapshot)
                ? afterSnapshot
                : null;
            var changes = BuildChanges(before, after);
            if (changes.Count == 0)
            {
                if (before?.WarehouseProductExists == true && after?.WarehouseProductExists == true)
                {
                    unchangedWarehouseSnapshots.Add(new WarehouseAuditRestoreSnapshot(before, after));
                }
                continue;
            }

            changedCodes.Add(code);
            // 新建商品（before 为空）时各分店价格与仓库一致，不需要通知。
            if (before != null && changes.Any(change => PriceNotificationFieldKeys.Contains(change.FieldKey)))
            {
                priceChangedCodes.Add(code);
            }
            histories.Add(
                new WarehouseProductChangeHistory
                {
                    EventGuid = Guid.NewGuid(),
                    ProductCode = code,
                    Action = before == null && after != null
                        ? "Create"
                        : Normalize(context.Action, "Update", 40),
                    Source = Normalize(context.Source, "Unknown", 80),
                    SourceReference = NormalizeNullable(context.SourceReference, 200),
                    BatchGuid = context.BatchGuid,
                    ActorUserGuid = actor.UserGuid,
                    ActorName = actor.Name,
                    ActorType = actor.Type,
                    OccurredAtUtc = occurredAtUtc,
                    ChangesJson = JsonSerializer.Serialize(changes, ChangeJsonOptions),
                }
            );
        }

        await AlignWarehouseAuditFieldsAsync(
            changedCodes,
            unchangedWarehouseSnapshots,
            occurredAtUtc,
            actor.Name
        );

        if (histories.Count == 0)
        {
            return 0;
        }

        // 不在这里开启或提交事务，调用者可把审计插入放入现有商品写入事务中。
        await _context.Db
            .Insertable(histories)
            .PageSize(HistoryInsertPageSize)
            .ExecuteCommandAsync();
        // 操作人取消勾选「生成价格任务」时只记审计，不触发分店价格更新任务。
        if (!context.SkipPriceTasks)
        {
            await NotifyStorePriceChangesAsync(priceChangedCodes, context, actor.Name, occurredAtUtc, cancellationToken);
        }
        return histories.Count;
    }

    /// <summary>
    /// 所有仓库改价入口的唯一收口：零售价或建议折扣变化后，同步生成/刷新/取消分店价格更新任务。
    /// 通知失败绝不能打断改价本身，因此这里吞掉异常只记日志；移动端列表与定时对账会兜底修正任务状态。
    /// </summary>
    private async Task NotifyStorePriceChangesAsync(
        IReadOnlyCollection<string> productCodes,
        WarehouseProductChangeHistoryContextDto context,
        string actorName,
        DateTime occurredAtUtc,
        CancellationToken cancellationToken
    )
    {
        if (_priceTaskService == null || productCodes.Count == 0)
        {
            return;
        }

        try
        {
            await _priceTaskService.OnWarehousePriceChangedAsync(
                productCodes,
                new PriceTaskInitiator(
                    actorName,
                    Normalize(context.Source, "Unknown", 80),
                    NormalizeNullable(context.SourceReference, 200),
                    occurredAtUtc
                ),
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成分店价格更新任务失败: ProductCount={ProductCount}", productCodes.Count);
        }
    }

    private async Task AcquireBatchWriteLockAsync(Guid batchGuid)
    {
        if (
            _context.Db.CurrentConnectionConfig.DbType != DbType.SqlServer
            || _context.Db.Ado.Transaction == null
        )
        {
            return;
        }

        // 所有生产写入口都在商品事务内调用本服务；按批次串行化“查重+插入”，
        // 避免两个并发执行器同时通过查重后由唯一索引回滚其中一笔业务事务。
        var lockResult = await _context.Db.Ado.SqlQuerySingleAsync<int>(
            """
            DECLARE @Result int;
            EXEC @Result = sys.sp_getapplock
                @Resource = @Resource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 30000;
            SELECT @Result;
            """,
            new SugarParameter(
                "@Resource",
                $"WarehouseProductChangeHistory_Batch_{batchGuid:N}"
            )
        );
        if (lockResult < 0)
        {
            throw new InvalidOperationException("获取仓库商品修改历史批次写锁失败");
        }
    }

    public async Task<WarehouseProductChangeHistoryPageDto> GetChangeHistoryAsync(
        string productCode,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedCode = NormalizeProductCode(productCode);
        if (normalizedCode.Length == 0)
        {
            throw new ArgumentException("商品编码不能为空", nameof(productCode));
        }
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), "页码必须大于等于1");
        }
        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "每页数量必须在1到100之间");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var product = await _context.Db
            .Queryable<Product>()
            .Where(item => !item.IsDeleted)
            .FirstAsync(item => item.ProductCode == normalizedCode);
        var warehouse = await _context.Db
            .Queryable<WarehouseProduct>()
            .Where(item => !item.IsDeleted)
            .FirstAsync(item => item.ProductCode == normalizedCode);
        var domestic = await _context.Db
            .Queryable<DomesticProduct>()
            .Where(item => !item.IsDeleted)
            .FirstAsync(item => item.ProductCode == normalizedCode);

        RefAsync<int> total = 0;
        var rows = await _context.Db
            .Queryable<WarehouseProductChangeHistory>()
            .Where(item => item.ProductCode == normalizedCode)
            .OrderByDescending(item => item.OccurredAtUtc)
            .OrderByDescending(item => item.Id)
            .ToPageListAsync(pageNumber, pageSize, total);

        var events = rows.Select(MapEvent).ToList();
        return new WarehouseProductChangeHistoryPageDto
        {
            ProductSummary = new WarehouseProductChangeHistoryProductSummaryDto
            {
                ProductCode = normalizedCode,
                ItemNumber = product?.ItemNumber ?? domestic?.HBProductNo,
                Barcode = product?.Barcode ?? domestic?.Barcode,
                ProductName = string.IsNullOrWhiteSpace(product?.ProductName)
                    ? domestic?.ProductName
                    : product.ProductName,
                EnglishName = string.IsNullOrWhiteSpace(product?.EnglishName)
                    ? domestic?.EnglishProductName
                    : product.EnglishName,
                LocalSupplierCode = product?.LocalSupplierCode,
                DomesticSupplierCode = domestic?.SupplierCode,
            },
            PageNumber = pageNumber,
            PageSize = pageSize,
            TotalCount = total.Value,
            TotalPages = total.Value == 0 ? 0 : (int)Math.Ceiling(total.Value / (double)pageSize),
            Events = events,
        };
    }

    private WarehouseProductChangeHistoryEventDto MapEvent(WarehouseProductChangeHistory history)
    {
        List<WarehouseProductChangeItemDto> changes;
        try
        {
            changes = JsonSerializer.Deserialize<List<WarehouseProductChangeItemDto>>(
                    history.ChangesJson,
                    ChangeJsonOptions
                ) ?? [];
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "仓库商品修改历史 ChangesJson 无法解析: {EventGuid}", history.EventGuid);
            changes = [];
        }

        return new WarehouseProductChangeHistoryEventDto
        {
            EventGuid = history.EventGuid,
            Action = history.Action,
            Source = history.Source,
            SourceReference = history.SourceReference,
            BatchGuid = history.BatchGuid,
            ActorUserGuid = history.ActorUserGuid,
            ActorName = history.ActorName,
            ActorType = history.ActorType,
            // SQL Server datetime2 读取后 Kind 为 Unspecified，显式恢复 UTC，避免前端按本地时间二次偏移。
            OccurredAtUtc = ToUtc(history.OccurredAtUtc),
            Changes = changes,
        };
    }

    private static WarehouseProductChangeSnapshotDto CreateSnapshot(
        string productCode,
        WarehouseProduct? warehouse,
        Product? product,
        DomesticProduct? domestic
    ) => new()
    {
        ProductCode = productCode,
        WarehouseSource = warehouse == null
            ? null
            : new WarehouseProductChangeSourceValuesDto
            {
                DomesticPrice = warehouse.DomesticPrice,
                ImportPrice = warehouse.ImportPrice,
                RetailPrice = warehouse.OEMPrice,
                PackingQuantity = warehouse.PackingQuantity,
                Volume = warehouse.Volume,
                MinOrderQuantity = warehouse.MinOrderQuantity,
                IsActive = warehouse.IsActive,
            },
        ProductSource = product == null
            ? null
            : new WarehouseProductChangeSourceValuesDto
            {
                ImportPrice = product.PurchasePrice,
                RetailPrice = product.RetailPrice,
                LocalSupplierCode = product.LocalSupplierCode,
                ProductName = product.ProductName,
                EnglishName = product.EnglishName,
                ItemNumber = product.ItemNumber,
                Barcode = product.Barcode,
                ProductType = product.ProductType,
                ProductCategoryGuid = product.ProductCategoryGUID,
                WarehouseCategoryGuid = product.WarehouseCategoryGUID,
                MiddlePackageQuantity = product.MiddlePackageQuantity,
                ProductImage = product.ProductImage,
                IsAutoPricing = product.IsAutoPricing,
                IsActive = product.IsActive,
            },
        DomesticSource = domestic == null
            ? null
            : new WarehouseProductChangeSourceValuesDto
            {
                DomesticPrice = domestic.DomesticPrice,
                ImportPrice = domestic.ImportPrice,
                RetailPrice = domestic.OEMPrice,
                DomesticSupplierCode = domestic.SupplierCode,
                ProductName = domestic.ProductName,
                EnglishName = domestic.EnglishProductName,
                ItemNumber = domestic.HBProductNo,
                Barcode = domestic.Barcode,
                ProductType = domestic.ProductType,
                MiddlePackQuantity = domestic.MiddlePackQuantity,
                PackingQuantity = domestic.PackingQuantity,
                Volume = domestic.UnitVolume,
                ProductImage = domestic.ProductImage,
                IsActive = domestic.IsActive,
            },
        WarehouseProductExists = warehouse != null,
        WarehouseUpdatedAt = warehouse?.UpdatedAt,
        WarehouseUpdatedBy = warehouse?.UpdatedBy,
        DomesticPrice = warehouse?.DomesticPrice ?? domestic?.DomesticPrice,
        ImportPrice = warehouse?.ImportPrice ?? product?.PurchasePrice ?? domestic?.ImportPrice,
        RetailPrice = warehouse?.OEMPrice ?? product?.RetailPrice ?? domestic?.OEMPrice,
        LocalSupplierCode = product?.LocalSupplierCode,
        DomesticSupplierCode = domestic?.SupplierCode,
        ProductName = string.IsNullOrWhiteSpace(product?.ProductName)
            ? domestic?.ProductName
            : product.ProductName,
        EnglishName = string.IsNullOrWhiteSpace(product?.EnglishName)
            ? domestic?.EnglishProductName
            : product.EnglishName,
        ItemNumber = product?.ItemNumber ?? domestic?.HBProductNo,
        Barcode = product?.Barcode ?? domestic?.Barcode,
        ProductType = product?.ProductType ?? domestic?.ProductType,
        ProductCategoryGuid = product?.ProductCategoryGUID,
        WarehouseCategoryGuid = product?.WarehouseCategoryGUID,
        MiddlePackageQuantity = product?.MiddlePackageQuantity,
        MiddlePackQuantity = domestic?.MiddlePackQuantity,
        PackingQuantity = domestic?.PackingQuantity ?? warehouse?.PackingQuantity,
        Volume = warehouse?.Volume ?? domestic?.UnitVolume,
        MinOrderQuantity = warehouse?.MinOrderQuantity,
        ProductImage = product?.ProductImage ?? domestic?.ProductImage,
        IsAutoPricing = product?.IsAutoPricing,
        IsActive = warehouse?.IsActive ?? product?.IsActive ?? domestic?.IsActive,
    };

    private async Task AlignWarehouseAuditFieldsAsync(
        IReadOnlyList<string> changedCodes,
        IReadOnlyList<WarehouseAuditRestoreSnapshot> unchangedSnapshots,
        DateTime occurredAtUtc,
        string actorName
    )
    {
        using var auditScope = SqlSugarAuditScope.PreserveExplicitAuditFields();

        // 有字段差异时，仓库商品列表显示的更新时间/更新人与本次历史事件保持一致。
        foreach (var codeBatch in changedCodes.Chunk(SnapshotQueryBatchSize))
        {
            var codes = codeBatch.ToList();
            await _context.Db.Updateable<WarehouseProduct>()
                .SetColumns(item => new WarehouseProduct
                {
                    UpdatedAt = occurredAtUtc,
                    UpdatedBy = actorName,
                })
                .Where(item => !item.IsDeleted && codes.Contains(item.ProductCode))
                .ExecuteCommandAsync();
        }

        // 业务入口可能在无实际字段变化时也先刷新审计列；此时不产生历史并恢复原值。
        foreach (
            var restoreGroup in unchangedSnapshots.GroupBy(snapshot => new
            {
                BeforeUpdatedAt = snapshot.Before.WarehouseUpdatedAt,
                BeforeUpdatedBy = snapshot.Before.WarehouseUpdatedBy,
                AfterUpdatedAt = snapshot.After.WarehouseUpdatedAt,
                AfterUpdatedBy = snapshot.After.WarehouseUpdatedBy,
            })
        )
        {
            foreach (
                var codeBatch in restoreGroup
                    .Select(snapshot => snapshot.Before.ProductCode)
                    .Chunk(SnapshotQueryBatchSize)
            )
            {
                var codes = codeBatch.ToList();
                if (_context.Db.CurrentConnectionConfig.DbType == DbType.Sqlite)
                {
                    // SQLite 将 DateTime 以文本保存；不同写入路径可能使用等价但格式不同的文本，
                    // SQL 等号会误判。先读取并按 CLR 值筛选；SQLite 写事务会串行化后续更新。
                    var currentAuditRows = await _context.Db.Queryable<WarehouseProduct>()
                        .Where(item => !item.IsDeleted && codes.Contains(item.ProductCode))
                        .Select(item => new WarehouseProduct
                        {
                            ProductCode = item.ProductCode,
                            UpdatedAt = item.UpdatedAt,
                            UpdatedBy = item.UpdatedBy,
                        })
                        .ToListAsync();
                    codes = currentAuditRows
                        .Where(item =>
                            item.UpdatedAt == restoreGroup.Key.AfterUpdatedAt
                            && string.Equals(
                                item.UpdatedBy,
                                restoreGroup.Key.AfterUpdatedBy,
                                StringComparison.Ordinal
                            )
                        )
                        .Select(item => item.ProductCode)
                        .ToList();
                    if (codes.Count == 0)
                    {
                        continue;
                    }
                }

                var restore = _context.Db.Updateable<WarehouseProduct>()
                    .SetColumns(item => new WarehouseProduct
                    {
                        UpdatedAt = restoreGroup.Key.BeforeUpdatedAt,
                        UpdatedBy = restoreGroup.Key.BeforeUpdatedBy,
                    })
                    .Where(item => !item.IsDeleted && codes.Contains(item.ProductCode));
                if (_context.Db.CurrentConnectionConfig.DbType != DbType.Sqlite)
                {
                    // after 快照之后若已有其他事务更新审计列，则放弃恢复，避免覆盖新操作者。
                    if (restoreGroup.Key.AfterUpdatedAt.HasValue)
                    {
                        var afterUpdatedAt = restoreGroup.Key.AfterUpdatedAt.Value;
                        restore = restore.Where(item => item.UpdatedAt == afterUpdatedAt);
                    }
                    else
                    {
                        restore = restore.Where(item => item.UpdatedAt == null);
                    }

                    if (restoreGroup.Key.AfterUpdatedBy != null)
                    {
                        var afterUpdatedBy = restoreGroup.Key.AfterUpdatedBy;
                        restore = restore.Where(item => item.UpdatedBy == afterUpdatedBy);
                    }
                    else
                    {
                        restore = restore.Where(item => item.UpdatedBy == null);
                    }
                }
                await restore.ExecuteCommandAsync();
            }
        }
    }

    private static List<WarehouseProductChangeItemDto> BuildChanges(
        WarehouseProductChangeSnapshotDto? before,
        WarehouseProductChangeSnapshotDto? after
    )
    {
        var changes = new List<WarehouseProductChangeItemDto>();
        foreach (var field in SnapshotFields)
        {
            var fieldChange = ResolveFieldChange(field, before, after);
            if (fieldChange == null)
            {
                continue;
            }

            changes.Add(
                new WarehouseProductChangeItemDto
                {
                    FieldKey = field.Key,
                    ValueType = field.ValueType,
                    BeforeValue = FormatValue(fieldChange.BeforeValue),
                    AfterValue = FormatValue(fieldChange.AfterValue),
                }
            );
        }

        return changes;
    }

    private static FieldValueChange? ResolveFieldChange(
        SnapshotField field,
        WarehouseProductChangeSnapshotDto? before,
        WarehouseProductChangeSnapshotDto? after
    )
    {
        if (
            field.SourceGetters is { Count: > 0 }
            && (HasSourceSnapshots(before) || HasSourceSnapshots(after))
        )
        {
            // 同一语义字段可能在多张镜像表同时联动；逐来源找真实变化，但只返回一条字段差异。
            foreach (var getter in field.SourceGetters)
            {
                var sourceBefore = getter(before);
                var sourceAfter = getter(after);
                if (!EqualsValue(sourceBefore, sourceAfter))
                {
                    return new FieldValueChange(sourceBefore, sourceAfter);
                }
            }

            return null;
        }

        var beforeValue = field.Getter(before);
        var afterValue = field.Getter(after);
        return EqualsValue(beforeValue, afterValue)
            ? null
            : new FieldValueChange(beforeValue, afterValue);
    }

    private static bool HasSourceSnapshots(WarehouseProductChangeSnapshotDto? snapshot) =>
        snapshot?.WarehouseSource != null
        || snapshot?.ProductSource != null
        || snapshot?.DomesticSource != null;

    private static bool EqualsValue(object? left, object? right)
    {
        if (left is string || right is string)
        {
            return string.Equals(left?.ToString(), right?.ToString(), StringComparison.Ordinal);
        }

        return Equals(left, right);
    }

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        bool flag => flag ? "true" : "false",
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static List<string> NormalizeProductCodes(IEnumerable<string> productCodes) => productCodes
        .Where(code => code != null)
        .Select(NormalizeProductCode)
        .Where(code => code.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static string NormalizeProductCode(string? productCode) => productCode?.Trim() ?? string.Empty;

    private static string Normalize(string? value, string fallback, int maxLength = int.MaxValue)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string? NormalizeNullable(string? value, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private (string? UserGuid, string Name, string Type) ResolveActor(
        WarehouseProductChangeHistoryContextDto context
    )
    {
        var currentUserGuid = NormalizeNullable(_currentUserService.GetCurrentUserGuid(), 80);
        var currentUserName = Normalize(_currentUserService.GetCurrentUsername(), "System", 120);
        var explicitUserGuid = NormalizeNullable(context.ActorUserGuid, 80);
        var requestedType = NormalizeNullable(context.ActorType, 30);
        var isExplicitSystem = string.Equals(
            requestedType,
            "System",
            StringComparison.OrdinalIgnoreCase
        );
        var userGuid = isExplicitSystem ? null : explicitUserGuid ?? currentUserGuid;
        var fallbackName = !string.Equals(
            currentUserName,
            "System",
            StringComparison.OrdinalIgnoreCase
        )
            ? currentUserName
            : !string.IsNullOrWhiteSpace(userGuid)
                ? userGuid
                : currentUserName;
        var name = Normalize(context.ActorName, fallbackName, 120);
        // 用户发起的后台任务即使入队时缺少显示名，也不能仅因名称回退成 System 而丢掉 GUID。
        if (
            !isExplicitSystem
            && !string.IsNullOrWhiteSpace(userGuid)
            && string.Equals(name, "System", StringComparison.OrdinalIgnoreCase)
        )
        {
            name = userGuid;
        }
        var type = requestedType
            ?? (string.IsNullOrWhiteSpace(userGuid)
                && string.Equals(name, "System", StringComparison.OrdinalIgnoreCase)
                ? "System"
                : "User");
        return (userGuid, name, type);
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static IReadOnlyList<Func<WarehouseProductChangeSnapshotDto?, object?>> Sources(
        params Func<WarehouseProductChangeSnapshotDto?, object?>[] getters
    ) => getters;

    private sealed record FieldValueChange(object? BeforeValue, object? AfterValue);

    private sealed record WarehouseAuditRestoreSnapshot(
        WarehouseProductChangeSnapshotDto Before,
        WarehouseProductChangeSnapshotDto After
    );

    private sealed record SnapshotField(
        string Key,
        string ValueType,
        Func<WarehouseProductChangeSnapshotDto?, object?> Getter,
        IReadOnlyList<Func<WarehouseProductChangeSnapshotDto?, object?>>? SourceGetters = null
    );
}
