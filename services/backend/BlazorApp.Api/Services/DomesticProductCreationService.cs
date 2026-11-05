using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SqlSugar;
using ZXing;
using ZXing.Common;
using ZXing.ImageSharp;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 国内商品货号条码批量创建服务实现
    /// </summary>
    public class DomesticProductCreationService : IDomesticProductCreationService
    {
        private readonly SqlSugarContext _context;
        private readonly ItemBarcodeService _itemBarcodeService;
        private readonly ILogger<DomesticProductCreationService> _logger;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;

        public DomesticProductCreationService(
            SqlSugarContext context,
            ItemBarcodeService itemBarcodeService,
            ILogger<DomesticProductCreationService> logger,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService
        )
        {
            _context = context;
            _itemBarcodeService = itemBarcodeService;
            _logger = logger;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
        }

        /// <summary>
        /// 在调用方已经开启的业务事务内记录批量创建服务的统一快照历史。
        /// </summary>
        private async Task RecordDomesticProductChangesAsync(
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
            IEnumerable<string> productCodes,
            string action,
            Guid batchGuid,
            string? sourceReference = null
        )
        {
            var normalizedCodes = productCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (normalizedCodes.Count == 0)
            {
                return;
            }

            var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(normalizedCodes);
            var actorName = ResolveActorName();
            var actorGuid = _currentUserService.GetCurrentUserGuid();
            await _changeHistoryService.RecordChangesAsync(
                beforeSnapshots,
                afterSnapshots,
                new WarehouseProductChangeHistoryContextDto
                {
                    Action = action,
                    Source = "DomesticProductCreation",
                    SourceReference = sourceReference,
                    BatchGuid = batchGuid,
                    ActorUserGuid = string.IsNullOrWhiteSpace(actorGuid) ? null : actorGuid,
                    ActorName = actorName,
                    ActorType = string.Equals(actorName, "System", StringComparison.OrdinalIgnoreCase)
                        ? "System"
                        : "User",
                    OccurredAtUtc = DateTime.UtcNow,
                }
            );
        }

        /// <summary>
        /// 从服务端请求上下文解析操作人；无 HTTP 用户时才回退到后台 System。
        /// </summary>
        private string ResolveActorName()
        {
            var actorName = _currentUserService.GetCurrentUsername();
            var actorGuid = _currentUserService.GetCurrentUserGuid();
            if (
                !string.IsNullOrWhiteSpace(actorGuid)
                && (
                    string.IsNullOrWhiteSpace(actorName)
                    || string.Equals(actorName, "System", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return actorGuid;
            }

            return string.IsNullOrWhiteSpace(actorName) ? "System" : actorName;
        }

        /// <summary>
        /// 批量创建国内商品
        /// </summary>
        public async Task<ApiResponse<CreateDomesticProductBatchResponse>> CreateBatchAsync(
            CreateDomesticProductBatchRequest request
        )
        {
            try
            {
                // 在生成批次号、分配套装列表和访问数据库前计算展开量，防止异常套数耗尽内存。
                const int maxCreatedItems = 10_000;
                long expandedItemCount = 0;
                foreach (var item in request.Items)
                {
                    expandedItemCount += string.IsNullOrEmpty(item.ParentItemNumber) && item.ProductType == 1
                        ? (long)Math.Max(item.CreateCount.GetValueOrDefault(1), 1) * (1L + item.SubItems.Count)
                        : 1;
                    if (expandedItemCount > maxCreatedItems)
                    {
                        return ApiResponse<CreateDomesticProductBatchResponse>.Error(
                            "单批商品及套装子项合计不能超过 10000 条，请拆分批次创建",
                            "CREATE_BATCH_LIMIT_EXCEEDED");
                    }
                }

                var batchNumber = await GenerateBatchNumberAsync();
                var auditBatchGuid = Guid.NewGuid();
                var currentUser = ResolveActorName();
                var now = DateTime.UtcNow;

                var supplier = await _context.ChinaSupplierDb.GetFirstAsync(x =>
                    x.SupplierCode == request.SupplierCode
                );
                var supplierName = supplier?.SupplierName;

                var mainItems = request
                    .Items.Where(i => string.IsNullOrEmpty(i.ParentItemNumber))
                    .ToList();
                var subItems = request
                    .Items.Where(i => !string.IsNullOrEmpty(i.ParentItemNumber))
                    .ToList();

                var normalItems = mainItems.Where(i => i.ProductType != 1).ToList();
                var setItems = mainItems.Where(i => i.ProductType == 1).ToList();

                var allProducts = new List<DomesticProduct>();
                var allSetProducts = new List<DomesticSetProduct>();
                var allLogs = new List<DomesticProductCreationLog>();
                var responseItems = new List<BatchCreatedItemDto>();

                if (normalItems.Any())
                {
                    var codes = await _itemBarcodeService.GenerateBatchItemNumbersAndBarcodesAsync(
                        request.SupplierCode,
                        ProductTypeEnum.Normal,
                        normalItems.Count,
                        request.PrefixName
                    );

                    for (var i = 0; i < normalItems.Count && i < codes.Count; i++)
                    {
                        var item = normalItems[i];
                        var (itemNumber, barcode) = codes[i];
                        var productCode = Guid.NewGuid().ToString();

                        allProducts.Add(
                            new DomesticProduct
                            {
                                ProductCode = productCode,
                                SupplierCode = request.SupplierCode,
                                ProductName = item.ProductName,
                                HBProductNo = itemNumber,
                                Barcode = barcode,
                                ProductType = 0,
                                OEMPrice = item.PrivateLabelPrice,
                                IsActive = true,
                                CreatedAt = now,
                                CreatedBy = currentUser,
                                UpdatedAt = now,
                                UpdatedBy = currentUser,
                            }
                        );

                        allLogs.Add(
                            new DomesticProductCreationLog
                            {
                                LogId = Guid.NewGuid().ToString(),
                                ProductCode = productCode,
                                SupplierCode = request.SupplierCode,
                                SupplierName = supplierName,
                                HBProductNo = itemNumber,
                                Barcode = barcode,
                                ProductName = item.ProductName,
                                PrefixCode = request.PrefixCode,
                                PrefixName = request.PrefixName,
                                CreationType = "Batch",
                                BatchNumber = batchNumber,
                            }
                        );

                        responseItems.Add(
                            new BatchCreatedItemDto
                            {
                                ProductCode = productCode,
                                HBProductNo = itemNumber,
                                Barcode = barcode,
                                ProductName = item.ProductName ?? "",
                                ProductType = 0,
                                PrivateLabelPrice = item.PrivateLabelPrice,
                                SubItems = new List<SubItemDto>(),
                            }
                        );
                    }
                }

                var nestedSubItemsBySetProductCode = new Dictionary<
                    string,
                    List<CreateBatchItemDto>
                >();
                var expandedSetItemCount = 0;

                if (setItems.Any())
                {
                    var expandedSetItems = setItems
                        .SelectMany(item =>
                            Enumerable
                                .Range(0, Math.Max(item.CreateCount.GetValueOrDefault(1), 1))
                                .Select(_ => item)
                        )
                        .ToList();
                    expandedSetItemCount = expandedSetItems.Count;

                    var codes = await _itemBarcodeService.GenerateBatchItemNumbersAndBarcodesAsync(
                        request.SupplierCode,
                        ProductTypeEnum.Set,
                        expandedSetItems.Count,
                        request.PrefixName
                    );

                    for (var i = 0; i < expandedSetItems.Count && i < codes.Count; i++)
                    {
                        var item = expandedSetItems[i];
                        var (itemNumber, barcode) = codes[i];
                        var productCode = Guid.NewGuid().ToString();

                        allProducts.Add(
                            new DomesticProduct
                            {
                                ProductCode = productCode,
                                SupplierCode = request.SupplierCode,
                                ProductName = item.ProductName,
                                HBProductNo = itemNumber,
                                Barcode = barcode,
                                ProductType = 1,
                                OEMPrice = item.PrivateLabelPrice,
                                IsActive = true,
                                CreatedAt = now,
                                CreatedBy = currentUser,
                                UpdatedAt = now,
                                UpdatedBy = currentUser,
                            }
                        );

                        allSetProducts.Add(
                            new DomesticSetProduct
                            {
                                SetProductCode = Guid.NewGuid().ToString(),
                                ProductCode = productCode,
                                ProductNo = itemNumber,
                                SetProductNo = itemNumber,
                                SetBarcode = barcode,
                                OEMPrice = item.PrivateLabelPrice,
                                DomesticPrice = item.SetPrice,
                            }
                        );

                        allLogs.Add(
                            new DomesticProductCreationLog
                            {
                                LogId = Guid.NewGuid().ToString(),
                                ProductCode = productCode,
                                SupplierCode = request.SupplierCode,
                                SupplierName = supplierName,
                                HBProductNo = itemNumber,
                                Barcode = barcode,
                                ProductName = item.ProductName,
                                PrefixCode = request.PrefixCode,
                                PrefixName = request.PrefixName,
                                CreationType = "Batch",
                                BatchNumber = batchNumber,
                            }
                        );

                        var createdItem = new BatchCreatedItemDto
                        {
                            ProductCode = productCode,
                            HBProductNo = itemNumber,
                            Barcode = barcode,
                            ProductName = item.ProductName ?? "",
                            ProductType = 1,
                            PrivateLabelPrice = item.PrivateLabelPrice,
                            SetQuantity = item.SetQuantity,
                            SetPrice = item.SetPrice,
                            SubItems = new List<SubItemDto>(),
                        };
                        responseItems.Add(createdItem);

                        // 嵌套子项按真实父商品编码暂存，后续用真实父货号生成子货号条码
                        if (item.SubItems.Any())
                        {
                            nestedSubItemsBySetProductCode[productCode] = item.SubItems.ToList();
                        }
                    }
                }

                var setSubItemGroups = new List<(
                    BatchCreatedItemDto createdItem,
                    List<CreateBatchItemDto> relatedSubItems
                )>();
                foreach (var createdItem in responseItems.Where(x => x.ProductType == 1))
                {
                    var hasNestedSubItems = nestedSubItemsBySetProductCode.TryGetValue(
                        createdItem.ProductCode,
                        out var nestedSubItems
                    );
                    var relatedSubItems = hasNestedSubItems
                        ? nestedSubItems!
                        : subItems.Where(s => s.ParentItemNumber == createdItem.HBProductNo).ToList();

                    if (!relatedSubItems.Any())
                        continue;

                    setSubItemGroups.Add((createdItem, relatedSubItems));
                }

                var subCodeGroups = setSubItemGroups.Any()
                    ? await _itemBarcodeService.GenerateBatchSetItemGroupsAndBarcodesAsync(
                        setSubItemGroups
                            .Select(group =>
                                (group.createdItem.HBProductNo, group.relatedSubItems.Count)
                            )
                            .ToList()
                    )
                    : new List<List<(string itemNumber, string barcode)>>();

                for (var groupIndex = 0; groupIndex < setSubItemGroups.Count; groupIndex++)
                {
                    var (createdItem, relatedSubItems) = setSubItemGroups[groupIndex];
                    var subCodes = subCodeGroups[groupIndex];
                    for (var i = 0; i < relatedSubItems.Count && i < subCodes.Count; i++)
                    {
                        var subItem = relatedSubItems[i];
                        var (subItemNumber, subBarcode) = subCodes[i];
                        var subProductName = subItem.SubItemProductName ?? subItem.ProductName;
                        var subSetProductCode = Guid.NewGuid().ToString();

                        allSetProducts.Add(
                            new DomesticSetProduct
                            {
                                // 套装子项不再创建独立主档，货号和条码由关系表唯一承载。
                                SetProductCode = subSetProductCode,
                                ProductCode = createdItem.ProductCode,
                                ProductNo = createdItem.HBProductNo,
                                SetProductNo = subItemNumber,
                                // 子项没有独立主档，名称必须由关系行持久化。
                                SetProductName = subProductName,
                                SetBarcode = subBarcode,
                                OEMPrice = subItem.PrivateLabelPrice,
                            }
                        );

                        allLogs.Add(
                            new DomesticProductCreationLog
                            {
                                LogId = Guid.NewGuid().ToString(),
                                // 保持 ProductCode 的 DomesticProduct 导航语义，子项用 HBProductNo 定位关系行。
                                ProductCode = createdItem.ProductCode,
                                SupplierCode = request.SupplierCode,
                                SupplierName = supplierName,
                                HBProductNo = subItemNumber,
                                Barcode = subBarcode,
                                ProductName = subProductName,
                                PrefixCode = request.PrefixCode,
                                PrefixName = request.PrefixName,
                                CreationType = "Batch",
                                BatchNumber = batchNumber,
                                Remark = $"Parent: {createdItem.HBProductNo}",
                            }
                        );

                        createdItem.SubItems.Add(
                            new SubItemDto
                            {
                                // 批次编辑接口以此主键精确定位子项关系记录。
                                ProductCode = subSetProductCode,
                                HBProductNo = subItemNumber,
                                Barcode = subBarcode,
                                ProductName = subProductName ?? "",
                                PrivateLabelPrice = subItem.PrivateLabelPrice,
                            }
                        );
                    }
                }

                // 三类记录必须由同一 SqlSugarClient 的事务提交，避免日志失败后留下孤儿主档或关系行。
                _context.Db.Ado.BeginTran();
                try
                {
                    // 只对 DomesticProduct 主档取快照；套装子项仅存在于关系表，明确排除。
                    var productCodes = allProducts.Select(product => product.ProductCode).ToList();
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        productCodes
                    );

                    if (allProducts.Any())
                        await _context
                            .Db.Fastest<DomesticProduct>()
                            .AS("DomesticProduct")
                            .PageSize(500)
                            .BulkCopyAsync(allProducts);
                    if (allSetProducts.Any())
                        await _context
                            .Db.Fastest<DomesticSetProduct>()
                            .AS("DomesticSetProduct")
                            .PageSize(500)
                            .BulkCopyAsync(allSetProducts);
                    if (allLogs.Any())
                        await _context
                            .Db.Fastest<DomesticProductCreationLog>()
                            .AS("DomesticProductCreationLog")
                            .PageSize(500)
                            .BulkCopyAsync(allLogs);

                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        productCodes,
                        "Create",
                        auditBatchGuid,
                        batchNumber
                    );

                    _context.Db.Ado.CommitTran();
                }
                catch
                {
                    _context.Db.Ado.RollbackTran();
                    throw;
                }

                var response = new CreateDomesticProductBatchResponse
                {
                    BatchNumber = batchNumber,
                    Items = responseItems,
                    TotalCreated = allLogs.Count,
                    NormalProductCount = normalItems.Count,
                    SetProductCount = expandedSetItemCount,
                };

                return ApiResponse<CreateDomesticProductBatchResponse>.OK(response, "批量创建成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建国内商品失败");
                return ApiResponse<CreateDomesticProductBatchResponse>.Error(
                    "批量创建失败: " + ex.Message,
                    "CREATE_BATCH_ERROR"
                );
            }
        }

        /// <summary>
        /// 创建单个商品
        /// </summary>
        private async Task<BatchCreatedItemDto?> CreateSingleProductAsync(
            CreateBatchItemDto item,
            CreateDomesticProductBatchRequest request,
            string batchNumber,
            string? supplierName
        )
        {
            try
            {
                var productType =
                    item.ProductType == 1
                        ? BlazorApp.Shared.DTOs.ProductTypeEnum.Set
                        : BlazorApp.Shared.DTOs.ProductTypeEnum.Normal;

                // 生成货号和条码
                var (itemNumber, barcode) =
                    await _itemBarcodeService.GenerateItemNumberAndBarcodeAsync(
                        request.SupplierCode,
                        productType,
                        item.ProductType == 1 ? null : request.PrefixName
                    );

                // 创建 DomesticProduct
                var domesticProduct = new DomesticProduct
                {
                    ProductCode = Guid.NewGuid().ToString(),
                    SupplierCode = request.SupplierCode,
                    ProductName = item.ProductName,
                    HBProductNo = itemNumber,
                    Barcode = barcode,
                    ProductType = item.ProductType,
                    OEMPrice = item.PrivateLabelPrice,
                    IsActive = true,
                };

                await _context.DomesticProductDb.InsertAsync(domesticProduct);

                // 如果是套装商品，创建 DomesticSetProduct
                if (item.ProductType == 1)
                {
                    var setProduct = new DomesticSetProduct
                    {
                        SetProductCode = Guid.NewGuid().ToString(),
                        ProductCode = domesticProduct.ProductCode,
                        ProductNo = itemNumber,
                        SetProductNo = itemNumber,
                        SetBarcode = barcode,
                        OEMPrice = item.PrivateLabelPrice,
                        DomesticPrice = item.SetPrice,
                    };
                    await _context.DomesticSetProductDb.InsertAsync(setProduct);
                }

                // 创建创建日志
                var creationLog = new DomesticProductCreationLog
                {
                    LogId = Guid.NewGuid().ToString(),
                    ProductCode = domesticProduct.ProductCode,
                    SupplierCode = request.SupplierCode,
                    SupplierName = supplierName,
                    HBProductNo = itemNumber,
                    Barcode = barcode,
                    ProductName = item.ProductName,
                    PrefixCode = request.PrefixCode,
                    PrefixName = request.PrefixName,
                    CreationType = "Batch",
                    BatchNumber = batchNumber,
                };
                await _context.DomesticProductCreationLogDb.InsertAsync(creationLog);

                return new BatchCreatedItemDto
                {
                    ProductCode = domesticProduct.ProductCode,
                    HBProductNo = itemNumber,
                    Barcode = barcode,
                    ProductName = item.ProductName ?? "",
                    ProductType = item.ProductType,
                    PrivateLabelPrice = item.PrivateLabelPrice,
                    SetQuantity = item.SetQuantity,
                    SetPrice = item.SetPrice,
                    SubItems = new List<SubItemDto>(),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建商品失败: {ProductName}", item.ProductName);
                return null;
            }
        }

        /// <summary>
        /// 创建套装子商品
        /// </summary>
        private async Task<SubItemDto?> CreateSubItemAsync(
            CreateBatchItemDto item,
            CreateDomesticProductBatchRequest request,
            string batchNumber,
            string? supplierName,
            string parentItemNumber,
            string parentProductCode
        )
        {
            try
            {
                // 生成套装子商品货号和条码
                var (itemNumber, barcode) =
                    await _itemBarcodeService.GenerateSetItemNumberAndBarcodeAsync(
                        parentItemNumber,
                        BlazorApp.Shared.DTOs.ProductTypeEnum.Set
                    );

                // 创建 DomesticSetProduct
                var setProduct = new DomesticSetProduct
                {
                    SetProductCode = Guid.NewGuid().ToString(),
                    ProductCode = parentProductCode,
                    ProductNo = parentItemNumber, // 关联到父商品货号
                    SetProductNo = itemNumber,
                    // 单条创建同样由关系行承载子项名称，和批量路径保持一致。
                    SetProductName = item.SubItemProductName ?? item.ProductName,
                    SetBarcode = barcode,
                    OEMPrice = item.PrivateLabelPrice,
                };
                await _context.DomesticSetProductDb.InsertAsync(setProduct);

                // 创建创建日志
                var creationLog = new DomesticProductCreationLog
                {
                    LogId = Guid.NewGuid().ToString(),
                    // 日志导航仍指向父 DomesticProduct，子项通过 HBProductNo 对应关系表。
                    ProductCode = parentProductCode,
                    SupplierCode = request.SupplierCode,
                    SupplierName = supplierName,
                    HBProductNo = itemNumber,
                    Barcode = barcode,
                    ProductName = item.SubItemProductName ?? item.ProductName,
                    PrefixCode = request.PrefixCode,
                    PrefixName = request.PrefixName,
                    CreationType = "Batch",
                    BatchNumber = batchNumber,
                    Remark = $"Parent: {parentItemNumber}",
                };
                await _context.DomesticProductCreationLogDb.InsertAsync(creationLog);

                return new SubItemDto
                {
                    ProductCode = setProduct.SetProductCode,
                    HBProductNo = itemNumber,
                    Barcode = barcode,
                    ProductName = item.SubItemProductName ?? item.ProductName ?? "",
                    PrivateLabelPrice = item.PrivateLabelPrice,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建套装子商品失败: {ProductName}", item.ProductName);
                return null;
            }
        }

        /// <summary>
        /// 生成批次号
        /// </summary>
        private async Task<string> GenerateBatchNumberAsync()
        {
            var dateStr = DateTime.Now.ToString("yyyyMMdd");
            var prefix = $"B{dateStr}";

            // 获取当天最大的批次号
            var existingBatches = await _context
                .DomesticProductCreationLogDb.GetListAsync(x =>
                    x.BatchNumber != null && x.BatchNumber.StartsWith(prefix)
                )
                .ContinueWith(t => t.Result.Select(x => x.BatchNumber).ToList());

            int maxSeq = 0;
            foreach (var batch in existingBatches)
            {
                if (batch != null && batch.Length > prefix.Length)
                {
                    var seqStr = batch.Substring(prefix.Length);
                    if (int.TryParse(seqStr, out int seq))
                    {
                        if (seq > maxSeq)
                            maxSeq = seq;
                    }
                }
            }

            return $"{prefix}{(maxSeq + 1):D3}";
        }

        /// <summary>
        /// 获取批次列表（分页）
        /// </summary>
        public Task<ApiResponse<PagedResult<DomesticProductBatchDto>>> GetBatchListAsync(
            int page = 1,
            int pageSize = 20,
            string? supplierCode = null,
            DateTime? startDate = null,
            DateTime? endDate = null
        )
        {
            try
            {
                var query = _context.DomesticProductCreationLogDb.GetList().AsQueryable();

                // 按批次号分组
                var batchGroups = query
                    .Where(x => x.BatchNumber != null)
                    .Where(x => supplierCode == null || x.SupplierCode == supplierCode)
                    .Where(x => startDate == null || x.CreatedAt >= startDate)
                    .Where(x => endDate == null || x.CreatedAt <= endDate)
                    .GroupBy(x => x.BatchNumber)
                    .Select(g => new
                    {
                        BatchNumber = g.Key,
                        SupplierCode = g.First().SupplierCode,
                        SupplierName = g.First().SupplierName,
                        CreatedTime = g.Min(x => x.CreatedAt),
                        NormalProductCount = g.Count(x =>
                            x.Product != null
                            && x.Product.ProductType == 0
                            && (x.Remark == null || !x.Remark.StartsWith("Parent:"))
                        ),
                        SetProductCount = g.Count(x =>
                            x.Product != null && x.Product.ProductType == 1
                            && (x.Remark == null || !x.Remark.StartsWith("Parent:"))
                        ),
                        TotalCount = g.Count(),
                        Remark = g.First().Remark,
                    })
                    .OrderByDescending(x => x.CreatedTime)
                    .ToList();

                // 分页
                var total = batchGroups.Count;
                var pagedData = batchGroups
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .Select(x => new DomesticProductBatchDto
                    {
                        BatchNumber = x.BatchNumber ?? "",
                        SupplierCode = x.SupplierCode,
                        SupplierName = x.SupplierName,
                        CreatedTime = x.CreatedTime,
                        NormalProductCount = x.NormalProductCount,
                        SetProductCount = x.SetProductCount,
                        TotalCount = x.TotalCount,
                        Remark = x.Remark,
                    })
                    .ToList();

                var result = new PagedResult<DomesticProductBatchDto>
                {
                    Items = pagedData,
                    Total = total,
                    Page = page,
                    PageSize = pageSize,
                };

                return Task.FromResult(
                    ApiResponse<PagedResult<DomesticProductBatchDto>>.OK(
                        result,
                        "获取批次列表成功"
                    )
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取批次列表失败");
                return Task.FromResult(
                    ApiResponse<PagedResult<DomesticProductBatchDto>>.Error(
                        "获取批次列表失败: " + ex.Message,
                        "GET_BATCH_LIST_ERROR"
                    )
                );
            }
        }

        /// <summary>
        /// 获取批次详情
        /// </summary>
        public async Task<ApiResponse<DomesticProductBatchDetailDto>> GetBatchDetailAsync(
            string batchNumber
        )
        {
            try
            {
                var logs = await _context.DomesticProductCreationLogDb.GetListAsync(x =>
                    x.BatchNumber == batchNumber
                );

                if (logs == null || !logs.Any())
                {
                    return ApiResponse<DomesticProductBatchDetailDto>.Error(
                        "批次不存在",
                        "BATCH_NOT_FOUND"
                    );
                }

                var firstLog = logs.First();
                var productCodes = logs
                    .Select(log => log.ProductCode)
                    .Where(productCode => !string.IsNullOrWhiteSpace(productCode))
                    .Distinct()
                    .ToList();
                var products = productCodes.Any()
                    ? await _context.DomesticProductDb.GetListAsync(product =>
                        productCodes.Contains(product.ProductCode)
                    )
                    : new List<DomesticProduct>();
                var setProducts = productCodes.Any()
                    ? await _context.DomesticSetProductDb.GetListAsync(setProduct =>
                        productCodes.Contains(setProduct.ProductCode)
                    )
                    : new List<DomesticSetProduct>();
                var productsByProductCode = products
                    .GroupBy(product => product.ProductCode)
                    .ToDictionary(group => group.Key, group => group.First());
                var logsByHBProductNo = logs
                    .Where(log => !string.IsNullOrWhiteSpace(log.HBProductNo))
                    .GroupBy(log => log.HBProductNo)
                    .ToDictionary(group => group.Key, group => group.First());
                var setSubItemsByParentAndProductNo = setProducts
                    .Where(setProduct => setProduct.ProductNo != setProduct.SetProductNo)
                    .GroupBy(setProduct => (setProduct.ProductCode, setProduct.SetProductNo))
                    .ToDictionary(group => group.Key, group => group.First());
                var setQuantityByProductCode = setProducts
                    .Where(setProduct => setProduct.ProductNo != setProduct.SetProductNo)
                    .GroupBy(setProduct => setProduct.ProductCode)
                    .ToDictionary(group => group.Key, group => group.Count());
                var setPriceByProductCode = setProducts
                    .Where(setProduct => setProduct.ProductNo == setProduct.SetProductNo)
                    .GroupBy(setProduct => setProduct.ProductCode)
                    .ToDictionary(group => group.Key, group => group.First().DomesticPrice);
                var normalCount = 0;
                var setCount = 0;

                var items = new List<BatchDetailItemDto>();

                foreach (var log in logs)
                {
                    productsByProductCode.TryGetValue(log.ProductCode, out var product);
                    var productType = product?.ProductType ?? 0;

                    // 子项日志的 ProductCode 保持指向父商品，具体子项由 HBProductNo 对应关系表。
                    var isSubItem = IsSetSubItemLog(log);
                    string? parentProductCode = null;
                    string? parentHBProductNo = null;
                    DomesticSetProduct? setSubItem = null;

                    if (isSubItem)
                    {
                        // 查找父商品
                        var parentItemNumber = log.Remark!.Replace("Parent:", "").Trim();
                        logsByHBProductNo.TryGetValue(parentItemNumber, out var parentLog);
                        parentProductCode = parentLog?.ProductCode ?? log.ProductCode;
                        parentHBProductNo = parentLog?.HBProductNo ?? parentItemNumber;

                        // 仅新数据的子日志与父项共享 ProductCode，才能改由关系表承担子项标识和价格。
                        if (parentLog != null && log.ProductCode == parentLog.ProductCode)
                        {
                            setSubItemsByParentAndProductNo.TryGetValue(
                                (parentProductCode, log.HBProductNo),
                                out setSubItem
                            );
                        }
                    }

                    if (productType == 0 && !isSubItem)
                        normalCount++;
                    else if (productType == 1 && !isSubItem)
                        setCount++;

                    items.Add(
                        new BatchDetailItemDto
                        {
                            // 子项向编辑接口暴露关系主键，避免与共享的父 ProductCode 混淆。
                            ProductCode = isSubItem
                                ? setSubItem?.SetProductCode ?? log.ProductCode
                                : log.ProductCode,
                            HBProductNo = log.HBProductNo,
                            Barcode = log.Barcode,
                            // 新子项优先使用关系行名称；历史行缺失时兼容日志和旧主档名称。
                            ProductName = isSubItem
                                ? setSubItem?.SetProductName
                                    ?? log.ProductName
                                    ?? product?.ProductName
                                    ?? ""
                                : log.ProductName ?? product?.ProductName ?? "",
                            ProductType = isSubItem ? 2 : productType, // 2 = SetSubItem
                            PrivateLabelPrice = isSubItem
                                ? setSubItem?.OEMPrice ?? product?.OEMPrice
                                : product?.OEMPrice,
                            SetQuantity =
                                productType == 1
                                && !isSubItem
                                && setQuantityByProductCode.TryGetValue(
                                    log.ProductCode,
                                    out var setQuantity
                                )
                                    ? setQuantity
                                    : null,
                            SetPrice =
                                productType == 1
                                && !isSubItem
                                && setPriceByProductCode.TryGetValue(log.ProductCode, out var setPrice)
                                    ? setPrice
                                    : null,
                            ParentProductCode = parentProductCode,
                            ParentHBProductNo = parentHBProductNo,
                        }
                    );
                }

                var result = new DomesticProductBatchDetailDto
                {
                    BatchNumber = batchNumber,
                    SupplierCode = firstLog.SupplierCode,
                    SupplierName = firstLog.SupplierName,
                    CreatedTime = logs.Min(x => x.CreatedAt),
                    Remark = firstLog.Remark,
                    NormalProductCount = normalCount,
                    SetProductCount = setCount,
                    Items = items,
                };

                return ApiResponse<DomesticProductBatchDetailDto>.OK(result, "获取批次详情成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取批次详情失败: {BatchNumber}", batchNumber);
                return ApiResponse<DomesticProductBatchDetailDto>.Error(
                    "获取批次详情失败: " + ex.Message,
                    "GET_BATCH_DETAIL_ERROR"
                );
            }
        }

        /// <summary>
        /// 获取套装数量
        /// </summary>
        private async Task<int?> GetSetQuantityAsync(string productCode)
        {
            try
            {
                var setProducts = await _context.DomesticSetProductDb.GetListAsync(x =>
                    x.ProductCode == productCode && x.ProductNo != x.SetProductNo
                );
                return setProducts?.Count;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSetSubItemLog(DomesticProductCreationLog log) =>
            !string.IsNullOrEmpty(log.Remark) && log.Remark.StartsWith("Parent:");

        /// <summary>
        /// 获取套装价格
        /// </summary>
        private async Task<decimal?> GetSetPriceAsync(string productCode)
        {
            try
            {
                var setProduct = await _context.DomesticSetProductDb.GetFirstAsync(x =>
                    x.ProductCode == productCode && x.ProductNo == x.SetProductNo
                );
                return setProduct?.DomesticPrice;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 导出批次创建结果
        /// </summary>
        public async Task<ApiResponse<DomesticProductBatchExportFileDto>> ExportBatchAsync(
            string batchNumber
        )
        {
            try
            {
                var detailResult = await GetBatchDetailAsync(batchNumber);
                if (!detailResult.Success || detailResult.Data == null)
                {
                    return ApiResponse<DomesticProductBatchExportFileDto>.Error(
                        detailResult.Message ?? "批次不存在",
                        detailResult.ErrorCode ?? "BATCH_NOT_FOUND"
                    );
                }

                var detail = detailResult.Data;
                // 导出文件不经过 JSON 遮罩：没有「查看国内供应商名称」权限的账号，文件里只写供应商代码。
                var exportSupplierName = DomesticSupplierInfoMasking.IsNameVisibleInCurrentAction
                    ? detail.SupplierName
                    : null;
                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("批次明细");

                worksheet.Cell(1, 1).Value = "批次号";
                worksheet.Cell(1, 2).Value = detail.BatchNumber;
                worksheet.Cell(1, 3).Value = "供应商";
                worksheet.Cell(1, 4).Value = string.IsNullOrWhiteSpace(exportSupplierName)
                    ? detail.SupplierCode
                    : $"{detail.SupplierCode} - {exportSupplierName}";
                worksheet.Cell(2, 1).Value = "创建时间";
                worksheet.Cell(2, 2).Value = detail.CreatedTime.ToString("yyyy-MM-dd HH:mm:ss");
                worksheet.Cell(2, 3).Value = "总数量";
                worksheet.Cell(2, 4).Value = detail.Items.Count;

                var headerRow = 4;
                var headers = new[]
                {
                    "批次号",
                    "供应商",
                    "类型",
                    "父套装货号",
                    "货号",
                    "条码",
                    "商品名称",
                    "零售价",
                    "套装数量",
                    "套装价格",
                    "条码图片",
                };

                for (var i = 0; i < headers.Length; i++)
                {
                    worksheet.Cell(headerRow, i + 1).Value = headers[i];
                }

                var headerRange = worksheet.Range(headerRow, 1, headerRow, headers.Length);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                var supplierText = string.IsNullOrWhiteSpace(exportSupplierName)
                    ? detail.SupplierCode
                    : $"{detail.SupplierCode} - {exportSupplierName}";
                var sortedItems = OrderBatchDetailItemsForExport(detail.Items);

                var row = headerRow + 1;
                foreach (var item in sortedItems)
                {
                    worksheet.Cell(row, 1).Value = detail.BatchNumber;
                    worksheet.Cell(row, 2).Value = supplierText;
                    worksheet.Cell(row, 3).Value = GetProductTypeLabel(item.ProductType);
                    worksheet.Cell(row, 4).Value =
                        item.ProductType == 2 ? item.ParentHBProductNo ?? "" : "";
                    worksheet.Cell(row, 5).Value = item.HBProductNo;
                    worksheet.Cell(row, 6).Style.NumberFormat.Format = "@";
                    worksheet.Cell(row, 6).Value = item.Barcode ?? "";
                    worksheet.Cell(row, 7).Value = item.ProductName ?? "";
                    worksheet.Cell(row, 8).Value = item.PrivateLabelPrice;
                    worksheet.Cell(row, 9).Value = item.SetQuantity;
                    worksheet.Cell(row, 10).Value = item.SetPrice;

                    var barcodeImage = GenerateBarcodeImagePng(item.Barcode);
                    if (barcodeImage != null)
                    {
                        // 导出时把图片直接嵌入条码图片列，方便仓库/采购直接扫码核对。
                        using var imageStream = new MemoryStream(barcodeImage);
                        worksheet
                            .AddPicture(imageStream, $"Barcode_{row}")
                            .MoveTo(worksheet.Cell(row, 11))
                            .WithSize(180, 45);
                        worksheet.Row(row).Height = 45;
                    }

                    row++;
                }

                worksheet.Columns().AdjustToContents();
                worksheet.Column(11).Width = 28;
                worksheet.SheetView.FreezeRows(headerRow);

                using var stream = new MemoryStream();
                workbook.SaveAs(stream);

                return ApiResponse<DomesticProductBatchExportFileDto>.OK(
                    new DomesticProductBatchExportFileDto
                    {
                        Content = stream.ToArray(),
                        FileName = $"domestic-product-batch-{batchNumber}.xlsx",
                    },
                    "导出成功"
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导出批次创建结果失败: {BatchNumber}", batchNumber);
                return ApiResponse<DomesticProductBatchExportFileDto>.Error(
                    "导出失败: " + ex.Message,
                    "EXPORT_BATCH_ERROR"
                );
            }
        }

        private static string GetProductTypeLabel(int productType)
        {
            return productType switch
            {
                1 => "套装",
                2 => "套装子项",
                _ => "普通",
            };
        }

        private static List<BatchDetailItemDto> OrderBatchDetailItemsForExport(
            IEnumerable<BatchDetailItemDto> items
        )
        {
            var normalItems = items
                .Where(item => item.ProductType == 0)
                .OrderBy(item => item.HBProductNo)
                .ThenBy(item => item.Barcode)
                .ToList();
            var setItems = items
                .Where(item => item.ProductType == 1)
                .OrderBy(item => item.HBProductNo)
                .ThenBy(item => item.Barcode)
                .ToList();
            var subItemsByParent = items
                .Where(item => item.ProductType == 2 && !string.IsNullOrWhiteSpace(item.ParentHBProductNo))
                .GroupBy(item => item.ParentHBProductNo!.Trim())
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderBy(item => item.HBProductNo)
                        .ThenBy(item => item.Barcode)
                        .ToList()
                );

            var orderedItems = new List<BatchDetailItemDto>();
            var groupedSubItems = new HashSet<BatchDetailItemDto>();
            foreach (var setItem in setItems)
            {
                orderedItems.Add(setItem);
                if (
                    !string.IsNullOrWhiteSpace(setItem.HBProductNo)
                    && subItemsByParent.TryGetValue(setItem.HBProductNo.Trim(), out var subItems)
                )
                {
                    orderedItems.AddRange(subItems);
                    foreach (var subItem in subItems)
                    {
                        groupedSubItems.Add(subItem);
                    }
                }
            }

            var unmatchedSubItems = items
                .Where(item => item.ProductType == 2 && !groupedSubItems.Contains(item))
                .OrderBy(item => item.ParentHBProductNo)
                .ThenBy(item => item.HBProductNo)
                .ThenBy(item => item.Barcode)
                .ToList();
            orderedItems.AddRange(unmatchedSubItems);
            orderedItems.AddRange(normalItems);
            return orderedItems;
        }

        private static byte[]? GenerateBarcodeImagePng(string? barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode))
                return null;

            var writer = new ZXing.ImageSharp.BarcodeWriter<Rgba32>
            {
                Format = BarcodeFormat.CODE_128,
                Options = new EncodingOptions
                {
                    Width = 220,
                    Height = 60,
                    Margin = 4,
                },
            };
            using var image = writer.Write(barcode);
            using var stream = new MemoryStream();
            image.Save(stream, PngFormat.Instance);
            return stream.ToArray();
        }

        /// <summary>
        /// 批量更新私牌价格
        /// </summary>
        public async Task<ApiResponse<object>> UpdatePrivateLabelPriceAsync(
            string batchNumber,
            UpdatePrivateLabelPriceRequest request
        )
        {
            try
            {
                // 验证批次是否存在
                var logs = await _context.DomesticProductCreationLogDb.GetListAsync(x =>
                    x.BatchNumber == batchNumber
                );

                if (logs == null || !logs.Any())
                {
                    return ApiResponse<object>.Error("批次不存在", "BATCH_NOT_FOUND");
                }

                // 先完整校验归属再写入，避免请求混入其他批次商品时出现部分更新。
                var batchProductCodes = logs
                    .Select(log => log.ProductCode)
                    .Where(productCode => !string.IsNullOrWhiteSpace(productCode))
                    .ToHashSet();
                var batchSubItemLogsByHBProductNo = logs
                    .Where(IsSetSubItemLog)
                    .Where(log => !string.IsNullOrWhiteSpace(log.HBProductNo))
                    .GroupBy(log => log.HBProductNo)
                    .ToDictionary(group => group.Key, group => group.First());
                var itemsToUpdate = new List<(UpdatePriceItemDto Item, DomesticSetProduct? SetSubItem)>();

                foreach (var item in request.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.ProductCode))
                    {
                        return ApiResponse<object>.Error("商品编码不能为空", "VALIDATION_ERROR");
                    }

                    if (batchProductCodes.Contains(item.ProductCode))
                    {
                        itemsToUpdate.Add((item, null));
                        continue;
                    }

                    // 新子项使用关系主键，除主键命中外还必须验证其父商品和子项日志均属于当前批次。
                    var setSubItem = await _context.DomesticSetProductDb.GetByIdAsync(
                        item.ProductCode
                    );
                    if (
                        setSubItem == null
                        || setSubItem.ProductNo == setSubItem.SetProductNo
                        || !batchSubItemLogsByHBProductNo.TryGetValue(
                            setSubItem.SetProductNo,
                            out var subItemLog
                        )
                        || subItemLog.ProductCode != setSubItem.ProductCode
                    )
                    {
                        return ApiResponse<object>.Error("商品不属于该批次", "VALIDATION_ERROR");
                    }

                    itemsToUpdate.Add((item, setSubItem));
                }

                var updatedCount = 0;
                var auditBatchGuid = Guid.NewGuid();
                var currentUser = ResolveActorName();
                var updatedAt = DateTime.UtcNow;
                var parentProductCodes = itemsToUpdate
                    .Where(item => item.SetSubItem == null)
                    .Select(item => item.Item.ProductCode)
                    .ToList();
                _context.Db.Ado.BeginTran();
                try
                {
                    // 子项只更新 DomesticSetProduct；仅对实际更新 DomesticProduct 的父项审计。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        parentProductCodes
                    );
                    foreach (var (item, setSubItem) in itemsToUpdate)
                    {
                        if (setSubItem != null)
                        {
                            setSubItem.OEMPrice = item.PrivateLabelPrice;
                            setSubItem.UpdatedAt = updatedAt;
                            setSubItem.UpdatedBy = currentUser;
                            await _context.DomesticSetProductDb.UpdateAsync(setSubItem);
                            updatedCount++;
                            continue;
                        }

                        // 父项和历史子项仍通过 DomesticProduct 主键更新。
                        var product = await _context.DomesticProductDb.GetByIdAsync(
                            item.ProductCode
                        );
                        if (product != null)
                        {
                            product.OEMPrice = item.PrivateLabelPrice;
                            product.UpdatedAt = updatedAt;
                            product.UpdatedBy = currentUser;
                            await _context.DomesticProductDb.UpdateAsync(product);

                            await UpdateSetProductOemPricesAsync(product, item.PrivateLabelPrice);

                            updatedCount++;
                        }
                    }

                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        parentProductCodes,
                        "BatchUpdate",
                        auditBatchGuid,
                        batchNumber
                    );

                    _context.Db.Ado.CommitTran();
                }
                catch
                {
                    _context.Db.Ado.RollbackTran();
                    throw;
                }

                return ApiResponse<object>.CreateSuccess($"成功更新 {updatedCount} 个商品的价格");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新私牌价格失败: {BatchNumber}", batchNumber);
                return ApiResponse<object>.Error(
                    "批量更新私牌价格失败: " + ex.Message,
                    "UPDATE_PRICE_ERROR"
                );
            }
        }

        /// <summary>
        /// 更新批次明细商品名称和零售价
        /// </summary>
        public async Task<ApiResponse<object>> UpdateBatchItemsAsync(
            string batchNumber,
            UpdateBatchItemsRequest request
        )
        {
            try
            {
                var logs = await _context.DomesticProductCreationLogDb.GetListAsync(x =>
                    x.BatchNumber == batchNumber
                );

                if (logs == null || !logs.Any())
                {
                    return ApiResponse<object>.Error("批次不存在", "BATCH_NOT_FOUND");
                }

                // 新子项与父项共享 ProductCode，必须以货号定位日志，不能再以 ProductCode 建唯一字典。
                var logsByHBProductNo = logs
                    .Where(log => !string.IsNullOrWhiteSpace(log.HBProductNo))
                    .GroupBy(log => log.HBProductNo)
                    .ToDictionary(group => group.Key, group => group.First());
                var itemsToUpdate = new List<
                    (UpdateBatchItemDto Item, DomesticProductCreationLog Log, DomesticSetProduct? SetSubItem)
                >();

                foreach (var item in request.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.ProductCode))
                    {
                        return ApiResponse<object>.Error("商品编码不能为空", "VALIDATION_ERROR");
                    }

                    if (item.PrivateLabelPrice.HasValue && item.PrivateLabelPrice.Value < 0)
                    {
                        return ApiResponse<object>.Error("零售价不能为负数", "VALIDATION_ERROR");
                    }

                    var setSubItem = await _context.DomesticSetProductDb.GetByIdAsync(
                        item.ProductCode
                    );
                    DomesticProductCreationLog? log;
                    if (
                        setSubItem != null
                        && setSubItem.ProductNo != setSubItem.SetProductNo
                    )
                    {
                        logsByHBProductNo.TryGetValue(setSubItem.SetProductNo, out log);
                        if (
                            log == null
                            || log.ProductCode != setSubItem.ProductCode
                            || !IsSetSubItemLog(log)
                        )
                        {
                            return ApiResponse<object>.Error("商品不属于该批次", "VALIDATION_ERROR");
                        }
                    }
                    else
                    {
                        // 父项使用 DomesticProduct 主键；历史子项仍可用其原 DomesticProduct 主键更新。
                        log = logs.FirstOrDefault(log =>
                            log.ProductCode == item.ProductCode && !IsSetSubItemLog(log)
                        ) ?? logs.FirstOrDefault(log => log.ProductCode == item.ProductCode);
                        if (log == null)
                        {
                            return ApiResponse<object>.Error(
                                "商品不属于该批次",
                                "VALIDATION_ERROR"
                            );
                        }
                    }

                    itemsToUpdate.Add((item, log, setSubItem));
                }

                // 商品、关系行和创建日志必须在同一客户端事务中写入，任何一步失败都回滚整批。
                var updatedCount = 0;
                var auditBatchGuid = Guid.NewGuid();
                var currentUser = ResolveActorName();
                var updatedAt = DateTime.UtcNow;
                var parentProductCodes = itemsToUpdate
                    .Where(item =>
                        item.SetSubItem == null
                        || item.SetSubItem.ProductNo == item.SetSubItem.SetProductNo
                    )
                    .Select(item => item.Log.ProductCode)
                    .ToList();
                _context.Db.Ado.BeginTran();
                try
                {
                    // 套装子项不拥有 DomesticProduct 主档，不生成统一快照历史。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        parentProductCodes
                    );
                    foreach (var (item, log, setSubItem) in itemsToUpdate)
                    {
                        var productName = item.ProductName ?? "";
                        if (setSubItem != null && setSubItem.ProductNo != setSubItem.SetProductNo)
                        {
                            setSubItem.SetProductName = productName;
                            setSubItem.OEMPrice = item.PrivateLabelPrice;
                            setSubItem.UpdatedAt = updatedAt;
                            setSubItem.UpdatedBy = currentUser;
                            await _context.DomesticSetProductDb.UpdateAsync(setSubItem);
                            updatedCount++;
                        }
                        else
                        {
                            var product = await _context.DomesticProductDb.GetByIdAsync(
                                log.ProductCode
                            );
                            if (product != null)
                            {
                                product.ProductName = productName;
                                product.OEMPrice = item.PrivateLabelPrice;
                                product.UpdatedAt = updatedAt;
                                product.UpdatedBy = currentUser;
                                await _context.DomesticProductDb.UpdateAsync(product);

                                await UpdateSetProductOemPricesAsync(product, item.PrivateLabelPrice);

                                updatedCount++;
                            }
                        }

                        log.ProductName = productName;
                        log.UpdatedAt = updatedAt;
                        log.UpdatedBy = currentUser;
                        await _context.DomesticProductCreationLogDb.UpdateAsync(log);
                    }

                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        parentProductCodes,
                        "BatchUpdate",
                        auditBatchGuid,
                        batchNumber
                    );

                    _context.Db.Ado.CommitTran();
                }
                catch
                {
                    _context.Db.Ado.RollbackTran();
                    throw;
                }

                return ApiResponse<object>.CreateSuccess($"成功更新 {updatedCount} 个商品");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新批次明细失败: {BatchNumber}", batchNumber);
                return ApiResponse<object>.Error(
                    "更新批次明细失败: " + ex.Message,
                    "UPDATE_BATCH_ITEMS_ERROR"
                );
            }
        }

        /// <summary>
        /// 更新商品对应的套装明细零售价，子项按小货号反查父套装明细
        /// </summary>
        private async Task UpdateSetProductOemPricesAsync(
            DomesticProduct product,
            decimal? privateLabelPrice
        )
        {
            var setProducts = await _context.DomesticSetProductDb.GetListAsync(x =>
                // 父套装调价只同步父自关联行；历史子主档仍通过子货号精确回写对应关系。
                (x.ProductCode == product.ProductCode && x.ProductNo == x.SetProductNo)
                || x.SetProductNo == product.HBProductNo
            );
            foreach (var setProduct in setProducts)
            {
                setProduct.OEMPrice = privateLabelPrice;
                await _context.DomesticSetProductDb.UpdateAsync(setProduct);
            }
        }
    }
}
