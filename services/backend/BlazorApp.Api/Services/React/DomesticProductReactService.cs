using System.Linq.Expressions;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// React 国内商品服务（文件名以 ReactService 结尾）
    /// 当前为适配器实现，委托给原有 IDomesticProductService。
    /// </summary>
    public class DomesticProductReactService : IDomesticProductReactService
    {
        private readonly SqlSugarContext _context;
        private readonly HBSalesSqlSugarContext _hbSalesContext;
        private readonly IMapper _mapper;
        private readonly ILogger<DomesticProductReactService> _logger;
        private readonly ItemBarcodeService _itemBarcodeService;
        private readonly HqSqlSugarContext _hqContext;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;
        private const string HqProductNameSupplierCode = "200";
        private const int HqProductNameQueryChunkSize = 1000;
        private const int HqProductNameUpdateChunkSize = 200;

        public DomesticProductReactService(
            SqlSugarContext context,
            HBSalesSqlSugarContext hbSalesContext,
            IMapper mapper,
            ILogger<DomesticProductReactService> logger,
            ItemBarcodeService itemBarcodeService,
            HqSqlSugarContext hqContext,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService
        )
        {
            _context = context;
            _hbSalesContext = hbSalesContext;
            _mapper = mapper;
            _logger = logger;
            _itemBarcodeService = itemBarcodeService;
            _hqContext = hqContext;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
        }

        /// <summary>
        /// 在调用方已经开启的业务事务内记录国内商品统一快照历史。
        /// </summary>
        private async Task RecordDomesticProductChangesAsync(
            IReadOnlyDictionary<string, WarehouseProductChangeSnapshotDto> beforeSnapshots,
            IEnumerable<string> productCodes,
            string action,
            string source,
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
                    Source = source,
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

        // ==================== React React-Data-Grid 专用方法 ====================
        public async Task<GridResponseDto<DomesticProductDto>> GetGridDataAsync(
            GridRequestDto request
        )
        {
            try
            {
                var db = _context.Db;

                // 主查询：筛选条件（删除标记 + 关键词 + 列筛选）统一由 BuildGridFilteredQuery 构造
                var query = BuildGridFilteredQuery(db, request);

                // 排序
                if (request.SortModel != null && request.SortModel.Any())
                {
                    query = ApplyAgGridSorts(query, request.SortModel);
                }
                else
                {
                    query = query.OrderBy(p => p.UpdatedAt, OrderByType.Desc);
                }

                // 统计总数：重新构造一份与主查询完全相同的筛选条件（不带排序）。
                // 总数必须与主查询共用同一个构造方法，否则任一侧新增筛选而另一侧遗漏，
                // 就会出现 total 与实际可翻页条数不一致（分页多出空页、导出数量偏大）。
                var total = await BuildGridFilteredQuery(db, request).CountAsync();

                // 分页查询
                var items = await query
                    .Select(
                        (p, s) =>
                            new DomesticProductDto
                            {
                                ProductCode = p.ProductCode,
                                SupplierCode = p.SupplierCode,
                                SupplierName = SqlFunc.IsNull(s.SupplierName, string.Empty),
                                ProductName = p.ProductName,
                                EnglishProductName = p.EnglishProductName,
                                HBProductNo = p.HBProductNo,
                                Barcode = p.Barcode,
                                ProductSpecification = p.ProductSpecification,
                                ProductType = p.ProductType,
                                DomesticPrice = p.DomesticPrice,
                                OEMPrice = p.OEMPrice,
                                ImportPrice = p.ImportPrice,
                                PackingQuantity = p.PackingQuantity,
                                UnitVolume = p.UnitVolume,
                                MiddlePackQuantity = p.MiddlePackQuantity,
                                PackingSize = null,
                                Material = null,
                                Remarks = null,
                                ProductImage = p.ProductImage,
                                IsActive = p.IsActive,
                                CreatedAt = p.CreatedAt,
                                UpdatedAt = p.UpdatedAt,
                            }
                    )
                    .Skip(request.StartRow)
                    .Take(request.PageSize)
                    .ToListAsync();

                foreach (var item in items)
                {
                    item.ProductImage = ProductImageUrlHelper.EnsureImageUrl(
                        item.ProductImage,
                        item.HBProductNo ?? item.ProductCode
                    );
                }

                // 批量加载套装数量信息
                if (items.Any())
                {
                    var setProductCodes = items
                        .Where(x => x.ProductType > 0)
                        .Select(x => x.ProductCode)
                        .ToList();
                    if (setProductCodes.Any())
                    {
                        var setItemCounts = await db.Queryable<DomesticSetProduct>()
                            .Where(x => setProductCodes.Contains(x.ProductCode))
                            .GroupBy(x => x.ProductCode)
                            .Select(g => new
                            {
                                ProductCode = g.ProductCode,
                                Count = SqlFunc.AggregateCount(1),
                            })
                            .ToListAsync();

                        var setCountDict = setItemCounts.ToDictionary(
                            x => x.ProductCode,
                            x => x.Count
                        );
                        foreach (var item in items.Where(x => x.ProductType > 0))
                        {
                            if (setCountDict.TryGetValue(item.ProductCode, out var count))
                            {
                                item.SetProducts = Enumerable
                                    .Repeat(new DomesticSetProductDto(), count)
                                    .ToList();
                            }
                        }
                    }
                }

                _logger.LogInformation(
                    "React-Data-Grid查询成功: 总数={Total}, 返回={Count}",
                    total,
                    items.Count
                );
                return GridResponseDto<DomesticProductDto>.OK(items, total);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Grid 查询失败");
                return GridResponseDto<DomesticProductDto>.Error("查询失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 构造 grid 的筛选查询：未删除 + 全局关键词（OR）+ 列筛选（AND）。
        /// 主查询与总数查询都必须通过本方法取得查询，保证二者筛选条件逐项一致；
        /// 排序与分页不在这里处理，由调用方按需追加。
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> BuildGridFilteredQuery(
            ISqlSugarClient db,
            GridRequestDto request
        )
        {
            var query = db.Queryable<DomesticProduct>()
                .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                .Where(p => !p.IsDeleted);

            // 全局搜索（OR）
            if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
            {
                var keyword = request.GlobalSearch.Trim();
                query = query.Where(
                    (p, s) =>
                        (p.ProductName != null && p.ProductName.Contains(keyword))
                        || (p.HBProductNo != null && p.HBProductNo.Contains(keyword))
                        || (p.Barcode != null && p.Barcode.Contains(keyword))
                        || (
                            p.EnglishProductName != null
                            && p.EnglishProductName.Contains(keyword)
                        )
                        || (s.SupplierName != null && s.SupplierName.Contains(keyword))
                        || (p.SupplierCode != null && p.SupplierCode.Contains(keyword))
                );
            }

            // 列过滤（AND）
            if (request.FilterModel != null && request.FilterModel.Any())
            {
                query = ApplyAgGridFilters(query, request.FilterModel);
            }

            return query;
        }

        // 过滤与排序辅助方法
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyAgGridFilters(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            Dictionary<string, FilterModelDto> filterModel
        )
        {
            foreach (var filter in filterModel)
            {
                var columnId = filter.Key;
                var filterConfig = filter.Value;
                if (filterConfig == null || filterConfig.FilterType == null)
                    continue;

                switch (filterConfig.FilterType.ToLower())
                {
                    case "text":
                        query = ApplyTextFilter(query, columnId, filterConfig);
                        break;
                    case "number":
                        query = ApplyNumberFilter(query, columnId, filterConfig);
                        break;
                    case "set":
                        query = ApplySetFilter(query, columnId, filterConfig);
                        break;
                }
            }
            return query;
        }

        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyTextFilter(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string columnId,
            FilterModelDto filter
        )
        {
            if (filter.Filter == null)
                return query;

            var filterValue = filter.Filter ?? string.Empty;
            var operation = filter.Type?.ToLower() ?? "contains";

            switch (columnId)
            {
                case "supplierCode":
                    return ApplyTextOperationDirect(
                        query,
                        operation,
                        filterValue,
                        p => p.SupplierCode
                    );
                case "name":
                    return ApplyTextOperationDirect(
                        query,
                        operation,
                        filterValue,
                        p => p.ProductName
                    );
                case "nameEn":
                    return ApplyTextOperationDirect(
                        query,
                        operation,
                        filterValue,
                        p => p.EnglishProductName
                    );
                case "itemNumber":
                    return ApplyTextOperationDirect(
                        query,
                        operation,
                        filterValue,
                        p => p.HBProductNo
                    );
                case "barcode":
                    return ApplyTextOperationDirect(query, operation, filterValue, p => p.Barcode);
                case "supplierName":
                    return query.Where(
                        (p, s) => ApplyTextCondition(s.SupplierName, filter.Type, filterValue)
                    );
                default:
                    return query;
            }
        }

        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyTextOperationDirect(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string operation,
            string value,
            Expression<Func<DomesticProduct, string?>> fieldSelector
        )
        {
            var param = fieldSelector.Parameters[0];
            var member = fieldSelector.Body;

            return operation switch
            {
                "equals" => query.Where(
                    Expression.Lambda<Func<DomesticProduct, bool>>(
                        Expression.Equal(member, Expression.Constant(value, typeof(string))),
                        param
                    )
                ),
                "notequal" => query.Where(
                    Expression.Lambda<Func<DomesticProduct, bool>>(
                        Expression.NotEqual(member, Expression.Constant(value, typeof(string))),
                        param
                    )
                ),
                "contains" => query.Where(
                    Expression.Lambda<Func<DomesticProduct, bool>>(
                        Expression.AndAlso(
                            Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                            Expression.Call(
                                member,
                                typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                                Expression.Constant(value)
                            )
                        ),
                        param
                    )
                ),
                "notcontains" => query.Where(
                    Expression.Lambda<Func<DomesticProduct, bool>>(
                        Expression.OrElse(
                            Expression.Equal(member, Expression.Constant(null, typeof(string))),
                            Expression.Not(
                                Expression.Call(
                                    member,
                                    typeof(string).GetMethod("Contains", new[] { typeof(string) })!,
                                    Expression.Constant(value)
                                )
                            )
                        ),
                        param
                    )
                ),
                "startswith" => query.Where(
                    Expression.Lambda<Func<DomesticProduct, bool>>(
                        Expression.AndAlso(
                            Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                            Expression.Call(
                                member,
                                typeof(string).GetMethod("StartsWith", new[] { typeof(string) })!,
                                Expression.Constant(value)
                            )
                        ),
                        param
                    )
                ),
                "endswith" => query.Where(
                    Expression.Lambda<Func<DomesticProduct, bool>>(
                        Expression.AndAlso(
                            Expression.NotEqual(member, Expression.Constant(null, typeof(string))),
                            Expression.Call(
                                member,
                                typeof(string).GetMethod("EndsWith", new[] { typeof(string) })!,
                                Expression.Constant(value)
                            )
                        ),
                        param
                    )
                ),
                "blank" => query.Where(
                    Expression.Lambda<Func<DomesticProduct, bool>>(
                        Expression.Call(typeof(string), "IsNullOrEmpty", null, member),
                        param
                    )
                ),
                "notblank" => query.Where(
                    Expression.Lambda<Func<DomesticProduct, bool>>(
                        Expression.Not(
                            Expression.Call(typeof(string), "IsNullOrEmpty", null, member)
                        ),
                        param
                    )
                ),
                _ => query,
            };
        }

        private bool ApplyTextCondition(string? fieldValue, string? operation, string value)
        {
            return operation?.ToLower() switch
            {
                "equals" => fieldValue == value,
                "notequal" => fieldValue != value,
                "contains" => fieldValue?.Contains(value) ?? false,
                "notcontains" => !(fieldValue?.Contains(value) ?? false),
                "startswith" => fieldValue?.StartsWith(value) ?? false,
                "endswith" => fieldValue?.EndsWith(value) ?? false,
                "blank" => string.IsNullOrEmpty(fieldValue),
                "notblank" => !string.IsNullOrEmpty(fieldValue),
                _ => true,
            };
        }

        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyNumberFilter(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string columnId,
            FilterModelDto filter
        )
        {
            if (filter.Filter == null)
                return query;

            decimal? filterValue = null;
            if (decimal.TryParse(filter.Filter, out var parsed))
            {
                filterValue = parsed;
            }

            if (!filterValue.HasValue)
                return query;

            return columnId switch
            {
                "domesticPrice" => ApplyNumberOperation(
                    query,
                    filter.Type,
                    p => p.DomesticPrice,
                    filterValue.Value,
                    filter.FilterTo
                ),
                "labelPrice" => ApplyNumberOperation(
                    query,
                    filter.Type,
                    p => p.OEMPrice,
                    filterValue.Value,
                    filter.FilterTo
                ),
                "oemPrice" => ApplyNumberOperation(
                    query,
                    filter.Type,
                    p => p.OEMPrice,
                    filterValue.Value,
                    filter.FilterTo
                ),
                "importPrice" => ApplyNumberOperation(
                    query,
                    filter.Type,
                    p => p.ImportPrice,
                    filterValue.Value,
                    filter.FilterTo
                ),
                "packingQty" => ApplyIntNumberOperation(
                    query,
                    filter.Type,
                    p => p.PackingQuantity,
                    (int)filterValue.Value,
                    filter.FilterTo
                ),
                "volume" => ApplyNumberOperation(
                    query,
                    filter.Type,
                    p => p.UnitVolume,
                    filterValue.Value,
                    filter.FilterTo
                ),
                "middlePackQty" => ApplyNumberOperation(
                    query,
                    filter.Type,
                    p => p.MiddlePackQuantity,
                    filterValue.Value,
                    filter.FilterTo
                ),
                _ => query,
            };
        }

        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyNumberOperation(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string? operation,
            Expression<Func<DomesticProduct, decimal?>> fieldSelector,
            decimal value,
            object? filterTo
        )
        {
            var parameter = fieldSelector.Parameters[0];
            var member = fieldSelector.Body;
            var constantValue = Expression.Convert(Expression.Constant(value), typeof(decimal?));

            Expression? condition = operation?.ToLower() switch
            {
                "equals" => Expression.Equal(member, constantValue),
                "notequal" => Expression.NotEqual(member, constantValue),
                "lessthan" => Expression.LessThan(member, constantValue),
                "lessthanorequal" => Expression.LessThanOrEqual(member, constantValue),
                "greaterthan" => Expression.GreaterThan(member, constantValue),
                "greaterthanorequal" => Expression.GreaterThanOrEqual(member, constantValue),
                "inrange" => filterTo != null
                && decimal.TryParse(filterTo.ToString(), out var toValue)
                    ? Expression.AndAlso(
                        Expression.GreaterThanOrEqual(member, constantValue),
                        Expression.LessThanOrEqual(
                            member,
                            Expression.Convert(Expression.Constant(toValue), typeof(decimal?))
                        )
                    )
                    : null,
                _ => null,
            };

            if (condition == null)
                return query;

            var lambda = Expression.Lambda<Func<DomesticProduct, bool>>(condition, parameter);
            return query.Where(lambda);
        }

        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyIntNumberOperation(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string? operation,
            Expression<Func<DomesticProduct, int?>> fieldSelector,
            int value,
            object? filterTo
        )
        {
            var parameter = fieldSelector.Parameters[0];
            var member = fieldSelector.Body;
            var constantValue = Expression.Convert(Expression.Constant(value), typeof(int?));

            Expression? condition = operation?.ToLower() switch
            {
                "equals" => Expression.Equal(member, constantValue),
                "notequal" => Expression.NotEqual(member, constantValue),
                "lessthan" => Expression.LessThan(member, constantValue),
                "lessthanorequal" => Expression.LessThanOrEqual(member, constantValue),
                "greaterthan" => Expression.GreaterThan(member, constantValue),
                "greaterthanorequal" => Expression.GreaterThanOrEqual(member, constantValue),
                "inrange" => filterTo != null && int.TryParse(filterTo.ToString(), out var toValue)
                    ? Expression.AndAlso(
                        Expression.GreaterThanOrEqual(member, constantValue),
                        Expression.LessThanOrEqual(
                            member,
                            Expression.Convert(Expression.Constant(toValue), typeof(int?))
                        )
                    )
                    : null,
                _ => null,
            };

            if (condition == null)
                return query;

            var lambda = Expression.Lambda<Func<DomesticProduct, bool>>(condition, parameter);
            return query.Where(lambda);
        }

        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplySetFilter(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string columnId,
            FilterModelDto filter
        )
        {
            if (filter.Values == null || !filter.Values.Any())
                return query;

            if (columnId == "productType")
            {
                var typeNames = filter.Values;
                return query.Where(p => typeNames.Contains(p.ProductType.ToString()));
            }

            // 启用状态：前端按 isActive 发送 ["true"] / ["false"]。
            // 两个值都勾选（或都无法识别）等同于不筛选；此前后端没有这个分支，
            // 状态筛选在列表与 total 里都被静默忽略。
            if (columnId == "isActive")
            {
                var wantedStates = filter
                    .Values.Select(ParseActiveState)
                    .Where(state => state.HasValue)
                    .Select(state => state!.Value)
                    .Distinct()
                    .ToList();
                if (wantedStates.Count != 1)
                    return query;

                var wantedActive = wantedStates[0];
                return query.Where(p => p.IsActive == wantedActive);
            }

            return query;
        }

        /// <summary>
        /// 把 set 筛选里的状态字符串解析为布尔；无法识别返回 null。
        /// </summary>
        private static bool? ParseActiveState(string? value)
        {
            var text = value?.Trim();
            if (bool.TryParse(text, out var parsed))
                return parsed;
            return text switch
            {
                "1" => true,
                "0" => false,
                _ => null,
            };
        }

        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyAgGridSorts(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            List<SortModelDto> sortModel
        )
        {
            if (!sortModel.Any())
                return query;

            var firstSort = sortModel.First();
            var isAsc = firstSort.Sort.ToLower() == "asc";

            query = firstSort.ColId switch
            {
                "supplierCode" => query.OrderBy(
                    p => p.SupplierCode,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "supplierName" => query.OrderBy(
                    (p, s) => s.SupplierName,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "name" => query.OrderBy(
                    p => p.ProductName,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "nameEn" => query.OrderBy(
                    p => p.EnglishProductName,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "itemNumber" => query.OrderBy(
                    p =>
                        SqlFunc.ToInt32(
                            SqlFunc.Substring(p.HBProductNo, SqlFunc.Length(p.HBProductNo) - 2, 3)
                        ),
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "barcode" => query.OrderBy(
                    p => p.Barcode,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "productType" => query.OrderBy(
                    p => p.ProductType,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "domesticPrice" => query.OrderBy(
                    p => p.DomesticPrice,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "labelPrice" => query.OrderBy(
                    p => p.OEMPrice,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "importPrice" => query.OrderBy(
                    p => p.ImportPrice,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "packingQty" => query.OrderBy(
                    p => p.PackingQuantity,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "volume" => query.OrderBy(
                    p => p.UnitVolume,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "middlePackQty" => query.OrderBy(
                    p => p.MiddlePackQuantity,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "createdAt" => query.OrderBy(
                    p => p.CreatedAt,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                "updatedAt" => query.OrderBy(
                    p => p.UpdatedAt,
                    isAsc ? OrderByType.Asc : OrderByType.Desc
                ),
                _ => query.OrderBy(p => p.UpdatedAt, OrderByType.Desc),
            };

            return query;
        }

        // ==================== 批量验证/创建/检测/导入确认 ====================
        public async Task<ApiResponse<object>> BatchValidateProductsAsync(
            BatchCreateDomesticProductDto dto
        )
        {
            try
            {
                var db = _context.Db;
                var validProducts = new List<object>();
                var invalidProducts = new List<object>();

                var supplier = await db.Queryable<ChinaSupplier>()
                    .Where(s => s.SupplierCode == dto.SupplierCode && !s.IsDeleted)
                    .FirstAsync();
                if (supplier == null)
                {
                    return ApiResponse<object>.Error("供应商不存在", "SUPPLIER_NOT_FOUND");
                }

                if (!string.IsNullOrWhiteSpace(dto.PrefixCode))
                {
                    var prefix = await db.Queryable<ProductPrefixCode>()
                        .Where(p =>
                            p.PrefixCode == dto.PrefixCode
                            && p.SupplierCode == dto.SupplierCode
                            && !p.IsDeleted
                        )
                        .FirstAsync();
                    if (prefix == null)
                    {
                        return ApiResponse<object>.Error(
                            "前缀不存在或不属于该供应商",
                            "PREFIX_NOT_FOUND"
                        );
                    }
                }

                var existingProductNames = await db.Queryable<DomesticProduct>()
                    .Where(p => p.SupplierCode == dto.SupplierCode && !p.IsDeleted)
                    .Select(p => p.ProductName)
                    .ToListAsync();

                var existingNameSet = new HashSet<string>(
                    existingProductNames.Where(n => !string.IsNullOrWhiteSpace(n))!,
                    StringComparer.OrdinalIgnoreCase
                );

                for (int i = 0; i < dto.Products.Count; i++)
                {
                    var product = dto.Products[i];
                    var errors = new Dictionary<string, List<string>>();
                    var rowNumber = i + 1;

                    if (string.IsNullOrWhiteSpace(product.ProductName))
                    {
                        errors.Add("productName", new List<string> { "商品名称不能为空" });
                    }
                    else if (product.ProductName.Length > 200)
                    {
                        errors.Add(
                            "productName",
                            new List<string> { "商品名称长度不能超过200字符" }
                        );
                    }
                    else if (existingNameSet.Contains(product.ProductName))
                    {
                        errors.Add("productName", new List<string> { "商品名称已存在" });
                    }

                    if (product.DomesticPrice.HasValue)
                    {
                        if (product.DomesticPrice.Value < 0)
                            errors.Add(
                                "domesticPrice",
                                new List<string> { "国内价格必须为非负数" }
                            );
                    }
                    if (product.OEMPrice.HasValue)
                    {
                        if (product.OEMPrice.Value < 0)
                            errors.Add("oemPrice", new List<string> { "零售价必须为非负数" });
                    }
                    if (product.PackingQuantity.HasValue)
                    {
                        if (product.PackingQuantity.Value <= 0)
                            errors.Add(
                                "packingQuantity",
                                new List<string> { "装箱数必须为正整数" }
                            );
                    }
                    if (product.UnitVolume.HasValue)
                    {
                        if (product.UnitVolume.Value < 0)
                            errors.Add("unitVolume", new List<string> { "单件体积必须为非负数" });
                    }
                    if (product.MiddlePackQuantity.HasValue)
                    {
                        if (product.MiddlePackQuantity.Value <= 0)
                            errors.Add(
                                "middlePackQuantity",
                                new List<string> { "中包数必须为正整数" }
                            );
                    }

                    if (errors.Any())
                    {
                        invalidProducts.Add(
                            new
                            {
                                rowNumber,
                                productName = product.ProductName,
                                errors,
                            }
                        );
                    }
                    else
                    {
                        validProducts.Add(new { rowNumber, productName = product.ProductName });
                    }
                }

                _logger.LogInformation(
                    "批量验证完成: 有效{Valid}件, 无效{Invalid}件",
                    validProducts.Count,
                    invalidProducts.Count
                );
                return ApiResponse<object>.OK(new { validProducts, invalidProducts });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量验证商品失败");
                return ApiResponse<object>.Error("批量验证商品失败", "BATCH_VALIDATE_ERROR");
            }
        }

        public async Task<ApiResponse<List<DomesticProductDto>>> BatchCreateDomesticProductsAsync(
            BatchCreateDomesticProductDto dto
        )
        {
            try
            {
                var db = _context.Db;

                var supplier = await db.Queryable<ChinaSupplier>()
                    .Where(s => s.SupplierCode == dto.SupplierCode && !s.IsDeleted)
                    .FirstAsync();
                if (supplier == null)
                {
                    return ApiResponse<List<DomesticProductDto>>.Error(
                        "供应商不存在",
                        "SUPPLIER_NOT_FOUND"
                    );
                }

                string? prefixName = null;
                if (!string.IsNullOrWhiteSpace(dto.PrefixCode))
                {
                    var prefix = await db.Queryable<ProductPrefixCode>()
                        .Where(p =>
                            p.PrefixCode == dto.PrefixCode
                            && p.SupplierCode == dto.SupplierCode
                            && !p.IsDeleted
                        )
                        .FirstAsync();
                    if (prefix == null)
                    {
                        return ApiResponse<List<DomesticProductDto>>.Error(
                            "前缀不存在或不属于该供应商",
                            "PREFIX_NOT_FOUND"
                        );
                    }
                    prefixName = prefix.PrefixName;
                }

                var productType = (ProductTypeEnum)(
                    dto.Products.FirstOrDefault()?.ProductType ?? 0
                );
                var itemNumberBarcodeList =
                    await _itemBarcodeService.GenerateBatchItemNumbersAndBarcodesAsync(
                        dto.SupplierCode,
                        productType,
                        dto.Products.Count,
                        prefixName
                    );

                var products = new List<DomesticProduct>();
                var now = DateTime.Now;
                var currentUser = ResolveActorName();
                for (int i = 0; i < dto.Products.Count; i++)
                {
                    var productItem = dto.Products[i];
                    var product = _mapper.Map<DomesticProduct>(productItem);
                    product.ProductCode = UuidHelper.GenerateUuid7();
                    product.SupplierCode = dto.SupplierCode;
                    product.HBProductNo = itemNumberBarcodeList[i].itemNumber;
                    product.Barcode = itemNumberBarcodeList[i].barcode;

                    if (
                        string.IsNullOrWhiteSpace(product.ProductImage)
                        && !string.IsNullOrWhiteSpace(product.HBProductNo)
                        && !product.HBProductNo.StartsWith(
                            "http://",
                            StringComparison.OrdinalIgnoreCase
                        )
                        && !product.HBProductNo.StartsWith(
                            "https://",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        product.ProductImage =
                            $"https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/{product.HBProductNo}.jpg";
                    }

                    product.IsActive = true;
                    product.CreatedAt = now;
                    product.UpdatedAt = now;
                    product.CreatedBy = currentUser;
                    product.UpdatedBy = currentUser;
                    products.Add(product);
                }

                var auditBatchGuid = Guid.NewGuid();
                await db.Ado.BeginTranAsync();
                try
                {
                    var productCodes = products.Select(product => product.ProductCode).ToList();
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        productCodes
                    );
                    await BatchOperationHelper.BatchInsertAsync(
                        db,
                        products,
                        BatchOperationHelper.GetRecommendedBatchSize(products.Count, 2)
                    );

                    ProductPrefixCode? prefix = null;
                    if (!string.IsNullOrWhiteSpace(dto.PrefixCode))
                    {
                        prefix = await db.Queryable<ProductPrefixCode>()
                            .Where(p => p.PrefixCode == dto.PrefixCode && !p.IsDeleted)
                            .FirstAsync();
                    }

                    var batchNumber = UuidHelper.GenerateUuid7();
                    var creationLogs = new List<DomesticProductCreationLog>();
                    foreach (var product in products)
                    {
                        var log = new DomesticProductCreationLog
                        {
                            LogId = UuidHelper.GenerateUuid7(),
                            ProductCode = product.ProductCode,
                            SupplierCode = dto.SupplierCode,
                            SupplierName = supplier.SupplierName,
                            HBProductNo = product.HBProductNo ?? string.Empty,
                            Barcode = product.Barcode,
                            ProductName = product.ProductName,
                            PrefixCode = dto.PrefixCode,
                            PrefixName = prefix?.PrefixName,
                            CreationType = "Batch",
                            BatchNumber = batchNumber,
                            CreatedBy = currentUser,
                            CreatedAt = now,
                        };
                        creationLogs.Add(log);
                    }

                    await BatchOperationHelper.BatchInsertAsync(
                        db,
                        creationLogs,
                        BatchOperationHelper.GetRecommendedBatchSize(creationLogs.Count, 2)
                    );
                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        productCodes,
                        "Create",
                        "DomesticProductBatch",
                        auditBatchGuid,
                        batchNumber
                    );
                    await db.Ado.CommitTranAsync();

                    var productDtos = _mapper.Map<List<DomesticProductDto>>(products);
                    foreach (var productDto in productDtos)
                        productDto.SupplierName = supplier.SupplierName;
                    _logger.LogInformation(
                        "批量创建国内商品成功，SupplierCode: {SupplierCode}, Count: {Count}, BatchNumber: {BatchNumber}",
                        dto.SupplierCode,
                        products.Count,
                        batchNumber
                    );
                    return ApiResponse<List<DomesticProductDto>>.OK(productDtos);
                }
                catch (Exception ex)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(ex, "批量创建商品事务失败");
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建国内商品失败");
                return ApiResponse<List<DomesticProductDto>>.Error(
                    "批量创建国内商品失败",
                    "BATCH_CREATE_PRODUCTS_ERROR"
                );
            }
        }

        public async Task<
            ApiResponse<List<BatchProductDetectionResultDto>>
        > BatchDetectProductsAsync(BatchProductDetectionDto dto)
        {
            try
            {
                var db = _context.Db;
                if (dto.Products == null || !dto.Products.Any())
                {
                    return ApiResponse<List<BatchProductDetectionResultDto>>.Error(
                        "检测商品列表不能为空",
                        "EMPTY_PRODUCT_LIST"
                    );
                }
                var supplier = await db.Queryable<ChinaSupplier>()
                    .Where(s => s.SupplierCode == dto.SupplierCode && !s.IsDeleted)
                    .FirstAsync();
                if (supplier == null)
                {
                    return ApiResponse<List<BatchProductDetectionResultDto>>.Error(
                        "供应商不存在",
                        "SUPPLIER_NOT_FOUND"
                    );
                }

                var results = new List<BatchProductDetectionResultDto>();
                var hbProductNos = dto
                    .Products.Where(p => !string.IsNullOrWhiteSpace(p.HBProductNo))
                    .Select(p => p.HBProductNo!)
                    .Distinct()
                    .ToList();
                var existingProducts = await db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where(
                        (p, s) =>
                            p.SupplierCode == dto.SupplierCode
                            && hbProductNos.Contains(p.HBProductNo!)
                            && !p.IsDeleted
                    )
                    .Select(
                        (p, s) =>
                            new DomesticProduct
                            {
                                ProductCode = p.ProductCode,
                                SupplierCode = p.SupplierCode,
                                ProductName = p.ProductName,
                                EnglishProductName = p.EnglishProductName,
                                HBProductNo = p.HBProductNo,
                                Barcode = p.Barcode,
                                ProductSpecification = p.ProductSpecification,
                                ProductType = p.ProductType,
                                DomesticPrice = p.DomesticPrice,
                                OEMPrice = p.OEMPrice,
                                ImportPrice = p.ImportPrice,
                                PackingQuantity = p.PackingQuantity,
                                UnitVolume = p.UnitVolume,
                                MiddlePackQuantity = p.MiddlePackQuantity,
                                ProductImage = p.ProductImage,
                                IsActive = p.IsActive,
                                CreatedAt = p.CreatedAt,
                                UpdatedAt = p.UpdatedAt,
                                Supplier = new ChinaSupplier { SupplierName = s.SupplierName },
                            }
                    )
                    .ToListAsync();

                // 收集需要更新图片的商品
                var productsToUpdateImage = new Dictionary<string, string>();

                // 检测数据库重复货号（同一货号对应多个商品）
                var hbProductNoGroups = existingProducts
                    .GroupBy(p => p.HBProductNo)
                    .Where(g => g.Count() > 1)
                    .ToDictionary(g => g.Key!, g => g.Select(p => p.ProductCode).ToList());

                // 记录哪些货号有重复（用于日志）
                if (hbProductNoGroups.Any())
                {
                    _logger.LogWarning(
                        "检测到数据库中存在重复货号: {DuplicateHBNos}",
                        string.Join(
                            ", ",
                            hbProductNoGroups.Select(g => $"{g.Key}({g.Value.Count}个)")
                        )
                    );
                }

                foreach (var inputProduct in dto.Products)
                {
                    var result = new BatchProductDetectionResultDto
                    {
                        InputData = inputProduct,
                        SupplierCode = dto.SupplierCode,
                        SupplierName = supplier.SupplierName,
                    };

                    DomesticProduct? existingProduct = null;
                    if (!string.IsNullOrWhiteSpace(inputProduct.HBProductNo))
                    {
                        existingProduct = existingProducts.FirstOrDefault(p =>
                            p.HBProductNo == inputProduct.HBProductNo
                        );
                    }

                    if (existingProduct != null)
                    {
                        result.IsNewProduct = false;
                        result.ExistingData = _mapper.Map<DomesticProductDto>(existingProduct);
                        result.ExistingData.SupplierName = supplier.SupplierName;

                        // 检测该货号是否在数据库中有重复（2个以上商品使用同一货号）
                        if (
                            !string.IsNullOrWhiteSpace(inputProduct.HBProductNo)
                            && hbProductNoGroups.TryGetValue(
                                inputProduct.HBProductNo,
                                out var duplicateCodes
                            )
                        )
                        {
                            result.HasDuplicateInDatabase = true;
                            result.DuplicateProductCodes = duplicateCodes;
                        }

                        // 自动生成图片 URL（如果 ProductImage 为空且有 HBProductNo）
                        if (
                            string.IsNullOrWhiteSpace(result.ExistingData.ProductImage)
                            && !string.IsNullOrWhiteSpace(result.ExistingData.HBProductNo)
                        )
                        {
                            var imageUrl =
                                $"https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/{result.ExistingData.HBProductNo}.jpg";
                            result.ExistingData.ProductImage = imageUrl;

                            // 收集需要更新的商品
                            productsToUpdateImage[existingProduct.ProductCode] = imageUrl;
                        }

                        result.HasChanges = CheckForChanges(inputProduct, existingProduct);
                        result.ChangeList = GetChangeList(inputProduct, existingProduct);
                    }
                    else
                    {
                        result.IsNewProduct = true;
                        result.ExistingData = null;
                        result.HasChanges = false;
                        result.ChangeList = new List<string>();
                    }
                    results.Add(result);
                }

                // 批量更新图片到数据库
                if (productsToUpdateImage.Any())
                {
                    var auditBatchGuid = Guid.NewGuid();
                    var currentUser = ResolveActorName();
                    await db.Ado.BeginTranAsync();
                    try
                    {
                        var productCodes = productsToUpdateImage.Keys.ToList();
                        var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            productCodes
                        );
                        foreach (var update in productsToUpdateImage)
                        {
                            await db.Updateable<DomesticProduct>()
                                .SetColumns(p => p.ProductImage == update.Value)
                                .SetColumns(p => p.UpdatedAt == DateTime.Now)
                                .SetColumns(p => p.UpdatedBy == currentUser)
                                .Where(p => p.ProductCode == update.Key)
                                .ExecuteCommandAsync();
                        }
                        await RecordDomesticProductChangesAsync(
                            beforeSnapshots,
                            productCodes,
                            "Update",
                            "DomesticProduct",
                            auditBatchGuid,
                            "BatchDetect"
                        );
                        await db.Ado.CommitTranAsync();
                    }
                    catch
                    {
                        await db.Ado.RollbackTranAsync();
                        throw;
                    }
                    _logger.LogInformation(
                        "批量更新商品图片完成，数量: {Count}",
                        productsToUpdateImage.Count
                    );
                }

                _logger.LogInformation(
                    "批量检测商品完成，SupplierCode: {SupplierCode}, 检测数量: {Count}, 新商品: {NewCount}, 已存在: {ExistingCount}",
                    dto.SupplierCode,
                    results.Count,
                    results.Count(r => r.IsNewProduct),
                    results.Count(r => !r.IsNewProduct)
                );
                return ApiResponse<List<BatchProductDetectionResultDto>>.OK(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量检测商品失败");
                return ApiResponse<List<BatchProductDetectionResultDto>>.Error(
                    "批量检测商品失败",
                    "BATCH_DETECT_PRODUCTS_ERROR"
                );
            }
        }

        /// <summary>
        /// 批量创建和更新国内商品（React专用）
        /// 支持同时创建新商品和更新现有商品
        /// </summary>
        public async Task<
            ApiResponse<BatchProductOperationResultDto>
        > BatchCreateAndUpdateProductsAsync(BatchProductOperationDto dto)
        {
            try
            {
                var db = _context.Db;
                var result = new BatchProductOperationResultDto
                {
                    CreatedProducts = new List<DomesticProductDto>(),
                    UpdatedProducts = new List<DomesticProductDto>(),
                    Errors = new List<string>(),
                };

                // 验证供应商是否存在
                var supplier = await db.Queryable<ChinaSupplier>()
                    .Where(s => s.SupplierCode == dto.SupplierCode && !s.IsDeleted)
                    .FirstAsync();
                if (supplier == null)
                {
                    return ApiResponse<BatchProductOperationResultDto>.Error(
                        "供应商不存在",
                        "SUPPLIER_NOT_FOUND"
                    );
                }

                var now = DateTime.Now;
                var currentUser = ResolveActorName();
                var auditBatchGuid = Guid.NewGuid();
                var auditProductCodes = (dto.UpdateProducts ?? new List<BatchProductUpdateDto>())
                    .Select(item => item.ProductCode)
                    .ToList();
                // 开启事务
                await db.Ado.BeginTranAsync();
                try
                {
                    // 更新项先取 before；新建项的编码在事务内生成后加入同一批次审计。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        auditProductCodes
                    );
                    // 处理新建商品
                    if (dto.NewProducts?.Any() == true)
                    {
                        var newProducts = new List<DomesticProduct>();

                        var needGenerateProducts = dto
                            .NewProducts.Where(p =>
                                string.IsNullOrWhiteSpace(p.HBProductNo)
                                && string.IsNullOrWhiteSpace(p.Barcode)
                            )
                            .ToList();

                        List<(string itemNumber, string barcode)>? itemNumberBarcodeList = null;
                        int generateIndex = 0;

                        if (needGenerateProducts.Any())
                        {
                            itemNumberBarcodeList =
                                await _itemBarcodeService.GenerateBatchItemNumbersAndBarcodesAsync(
                                    dto.SupplierCode,
                                    ProductTypeEnum.Normal,
                                    needGenerateProducts.Count
                                );
                        }

                        foreach (var newProductDto in dto.NewProducts)
                        {
                            try
                            {
                                var product = _mapper.Map<DomesticProduct>(newProductDto);
                                product.ProductCode = UuidHelper.GenerateUuid7();
                                product.SupplierCode = dto.SupplierCode;

                                if (
                                    string.IsNullOrWhiteSpace(product.HBProductNo)
                                    && string.IsNullOrWhiteSpace(product.Barcode)
                                )
                                {
                                    if (itemNumberBarcodeList != null)
                                    {
                                        var (itemNumber, barcode) = itemNumberBarcodeList[
                                            generateIndex++
                                        ];
                                        product.HBProductNo = itemNumber;
                                        product.Barcode = barcode;
                                    }
                                }

                                if (
                                    string.IsNullOrWhiteSpace(product.ProductImage)
                                    && !string.IsNullOrWhiteSpace(product.HBProductNo)
                                    && !product.HBProductNo.StartsWith(
                                        "http://",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    && !product.HBProductNo.StartsWith(
                                        "https://",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                {
                                    product.ProductImage =
                                        $"https://hotbargain-yw-2023-1300114625.cos.ap-shanghai.myqcloud.com/YW200/{product.HBProductNo}.jpg";
                                }

                                product.IsActive = true;
                                product.CreatedAt = now;
                                product.UpdatedAt = now;
                                product.CreatedBy = currentUser;
                                product.UpdatedBy = currentUser;
                                newProducts.Add(product);

                                var productDto = _mapper.Map<DomesticProductDto>(product);
                                productDto.SupplierName = supplier.SupplierName;
                                result.CreatedProducts.Add(productDto);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(
                                    ex,
                                    "创建单个商品失败: {ProductName}",
                                    newProductDto.ProductName
                                );
                                result.Errors.Add(
                                    $"创建商品失败: {newProductDto.ProductName} - {ex.Message}"
                                );
                            }
                        }

                        // 批量插入新商品
                        if (newProducts.Any())
                        {
                            await db.Insertable(newProducts).ExecuteCommandAsync();
                        }
                    }

                    // 处理更新商品
                    if (dto.UpdateProducts?.Any() == true)
                    {
                        // 获取所有要更新的商品编码
                        var productCodes = dto.UpdateProducts.Select(u => u.ProductCode).ToList();
                        // 批量查询现有商品
                        var existingProducts = await db.Queryable<DomesticProduct>()
                            .Where(p => productCodes.Contains(p.ProductCode) && !p.IsDeleted)
                            .ToListAsync();

                        // 使用字典提高查找效率
                        var existingProductDict = existingProducts.ToDictionary(p => p.ProductCode);

                        // 收集需要更新的条形码
                        var barcodesToUpdate = dto
                            .UpdateProducts.Where(u => !string.IsNullOrWhiteSpace(u.Barcode))
                            .Select(u => u.Barcode!)
                            .ToList();

                        // 批量检查条形码是否重复
                        var duplicateBarcodes = await db.Queryable<DomesticProduct>()
                            .Where(p =>
                                barcodesToUpdate.Contains(p.Barcode!)
                                && !p.IsDeleted
                                && !productCodes.Contains(p.ProductCode)
                            )
                            .Select(p => p.Barcode)
                            .ToListAsync();

                        // 收集需要批量更新的商品
                        var productsToUpdate = new List<DomesticProduct>();

                        foreach (var updateDto in dto.UpdateProducts)
                        {
                            try
                            {
                                // 检查商品是否存在
                                if (
                                    !existingProductDict.TryGetValue(
                                        updateDto.ProductCode,
                                        out var existingProduct
                                    )
                                )
                                {
                                    result.Errors.Add(
                                        $"更新商品失败: 商品不存在 {updateDto.ProductCode}"
                                    );
                                    continue;
                                }

                                var hasChanges = false;
                                var changedFields = new List<string>();

                                // 更新商品名称
                                if (
                                    !string.IsNullOrWhiteSpace(updateDto.ProductName)
                                    && !string.Equals(
                                        updateDto.ProductName,
                                        existingProduct.ProductName
                                    )
                                )
                                {
                                    existingProduct.ProductName = updateDto.ProductName;
                                    hasChanges = true;
                                    changedFields.Add("ProductName");
                                }
                                // 更新商品英文名称
                                if (
                                    !string.IsNullOrWhiteSpace(updateDto.EnglishProductName)
                                    && !string.Equals(
                                        updateDto.EnglishProductName,
                                        existingProduct.EnglishProductName
                                    )
                                )
                                {
                                    existingProduct.EnglishProductName =
                                        updateDto.EnglishProductName;
                                    hasChanges = true;
                                    changedFields.Add("EnglishProductName");
                                }

                                // 更新条形码
                                if (
                                    !string.IsNullOrWhiteSpace(updateDto.Barcode)
                                    && !string.Equals(updateDto.Barcode, existingProduct.Barcode)
                                )
                                {
                                    if (duplicateBarcodes.Contains(updateDto.Barcode))
                                    {
                                        result.Errors.Add(
                                            $"更新商品失败: 条形码重复 {updateDto.Barcode} (ProductCode: {updateDto.ProductCode})"
                                        );
                                    }
                                    else
                                    {
                                        existingProduct.Barcode = updateDto.Barcode;
                                        hasChanges = true;
                                        changedFields.Add("Barcode");
                                    }
                                }

                                // 更新国内价格
                                if (
                                    updateDto.DomesticPrice.HasValue
                                    && existingProduct.DomesticPrice != updateDto.DomesticPrice
                                )
                                {
                                    if (updateDto.DomesticPrice.Value < 0)
                                    {
                                        result.Errors.Add(
                                            $"更新商品失败: 国内价格不能为负 (ProductCode: {updateDto.ProductCode})"
                                        );
                                    }
                                    else
                                    {
                                        existingProduct.DomesticPrice = updateDto.DomesticPrice;
                                        hasChanges = true;
                                        changedFields.Add("DomesticPrice");
                                    }
                                }
                                // 更新零售价
                                if (
                                    updateDto.OEMPrice.HasValue
                                    && existingProduct.OEMPrice != updateDto.OEMPrice
                                )
                                {
                                    if (updateDto.OEMPrice.Value < 0)
                                    {
                                        result.Errors.Add(
                                            $"更新商品失败: 零售价不能为负 (ProductCode: {updateDto.ProductCode})"
                                        );
                                    }
                                    else
                                    {
                                        existingProduct.OEMPrice = updateDto.OEMPrice;
                                        hasChanges = true;
                                        changedFields.Add("OEMPrice");
                                    }
                                }
                                // 更新单件装箱数
                                if (
                                    updateDto.PackingQuantity.HasValue
                                    && existingProduct.PackingQuantity != updateDto.PackingQuantity
                                )
                                {
                                    if (updateDto.PackingQuantity.Value <= 0)
                                    {
                                        result.Errors.Add(
                                            $"更新商品失败: 单件装箱数必须大于0 (ProductCode: {updateDto.ProductCode})"
                                        );
                                    }
                                    else
                                    {
                                        existingProduct.PackingQuantity = updateDto.PackingQuantity;
                                        hasChanges = true;
                                        changedFields.Add("PackingQuantity");
                                    }
                                }
                                // 更新单件体积
                                if (
                                    updateDto.UnitVolume.HasValue
                                    && existingProduct.UnitVolume != updateDto.UnitVolume
                                )
                                {
                                    if (updateDto.UnitVolume.Value < 0)
                                    {
                                        result.Errors.Add(
                                            $"更新商品失败: 单件体积不能为负 (ProductCode: {updateDto.ProductCode})"
                                        );
                                    }
                                    else
                                    {
                                        existingProduct.UnitVolume = updateDto.UnitVolume;
                                        hasChanges = true;
                                        changedFields.Add("UnitVolume");
                                    }
                                }
                                // 更新中包数量
                                if (
                                    updateDto.MiddlePackQuantity.HasValue
                                    && existingProduct.MiddlePackQuantity
                                        != updateDto.MiddlePackQuantity
                                )
                                {
                                    if (updateDto.MiddlePackQuantity.Value <= 0)
                                    {
                                        result.Errors.Add(
                                            $"更新商品失败: 中包数量必须大于0 (ProductCode: {updateDto.ProductCode})"
                                        );
                                    }
                                    else
                                    {
                                        existingProduct.MiddlePackQuantity =
                                            updateDto.MiddlePackQuantity;
                                        hasChanges = true;
                                        changedFields.Add("MiddlePackQuantity");
                                    }
                                }

                                // 如果有变更，添加到批量更新列表
                                if (hasChanges)
                                {
                                    existingProduct.UpdatedAt = now;
                                    existingProduct.UpdatedBy = currentUser;
                                    productsToUpdate.Add(existingProduct);

                                    result.UpdatedChanges.Add(
                                        new ProductChangeInfo
                                        {
                                            ProductCode = existingProduct.ProductCode,
                                            ChangeList = changedFields,
                                        }
                                    );
                                }
                                else
                                {
                                    _logger.LogInformation(
                                        "跳过更新（无变更）: {ProductCode}",
                                        updateDto.ProductCode
                                    );
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(
                                    ex,
                                    "更新单个商品失败: {ProductCode}",
                                    updateDto.ProductCode
                                );
                                // 参数校验在上方显式分支中继续；数据库或未知异常必须回滚整批。
                                throw;
                            }
                        }

                        // 批量更新到数据库
                        if (productsToUpdate.Any())
                        {
                            await db.Updateable(productsToUpdate).ExecuteCommandAsync();
                            foreach (var product in productsToUpdate)
                            {
                                var productDto = _mapper.Map<DomesticProductDto>(product);
                                productDto.SupplierName = supplier.SupplierName;
                                result.UpdatedProducts.Add(productDto);
                            }
                        }
                    }

                    auditProductCodes.AddRange(
                        result.CreatedProducts
                            .Select(product => product.ProductCode)
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                    );
                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        auditProductCodes,
                        "BatchUpdate",
                        "DomesticProductBatch",
                        auditBatchGuid
                    );

                    // 提交事务
                    await db.Ado.CommitTranAsync();
                    _logger.LogInformation(
                        "批量操作商品完成，SupplierCode: {SupplierCode}, 新建: {CreatedCount}, 更新: {UpdatedCount}, 错误: {ErrorCount}",
                        dto.SupplierCode,
                        result.CreatedProducts.Count,
                        result.UpdatedProducts.Count,
                        result.Errors.Count
                    );
                    return ApiResponse<BatchProductOperationResultDto>.OK(result);
                }
                catch
                {
                    // 回滚事务
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量操作商品失败");
                return ApiResponse<BatchProductOperationResultDto>.Error(
                    "批量操作商品失败",
                    "BATCH_OPERATION_PRODUCTS_ERROR"
                );
            }
        }

        /// <summary>
        /// 更新单个国内商品（React专用）
        /// </summary>
        public async Task<ApiResponse<DomesticProductDto>> UpdateDomesticProductAsync(
            string productCode,
            UpdateDomesticProductDto dto
        )
        {
            try
            {
                var db = _context.Db;
                // 查询商品
                var product = await db.Queryable<DomesticProduct>()
                    .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                    .FirstAsync();
                if (product == null)
                {
                    return ApiResponse<DomesticProductDto>.Error("商品不存在", "PRODUCT_NOT_FOUND");
                }
                var auditBatchGuid = Guid.NewGuid();
                ChinaSupplier? supplier = null;
                await db.Ado.BeginTranAsync();
                try
                {
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        new[] { productCode }
                    );
                    // 映射更新字段
                    _mapper.Map(dto, product);
                    product.UpdatedAt = DateTime.Now;
                    product.UpdatedBy = ResolveActorName();
                    // 更新到数据库
                    await db.Updateable(product).ExecuteCommandAsync();
                    // 查询供应商信息
                    supplier = await db.Queryable<ChinaSupplier>()
                        .Where(s => s.SupplierCode == product.SupplierCode)
                        .FirstAsync();
                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        new[] { productCode },
                        "Update",
                        "DomesticProduct",
                        auditBatchGuid
                    );
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
                var productDto = _mapper.Map<DomesticProductDto>(product);
                productDto.SupplierName = supplier?.SupplierName;
                _logger.LogInformation("更新国内商品成功，ProductCode: {ProductCode}", productCode);
                return ApiResponse<DomesticProductDto>.OK(productDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新国内商品失败，ProductCode: {ProductCode}", productCode);
                return ApiResponse<DomesticProductDto>.Error(
                    "更新国内商品失败",
                    "UPDATE_PRODUCT_ERROR"
                );
            }
        }

        /// <summary>
        /// 批量更新国内商品（React专用）
        /// 使用批量数据库操作，提高更新效率
        /// </summary>
        public async Task<
            ApiResponse<BatchProductOperationResultDto>
        > BatchUpdateDomesticProductsAsync(BatchUpdateDomesticProductsDto dto)
        {
            try
            {
                var db = _context.Db;
                var result = new BatchProductOperationResultDto
                {
                    CreatedProducts = new List<DomesticProductDto>(),
                    UpdatedProducts = new List<DomesticProductDto>(),
                    Errors = new List<string>(),
                };

                // 验证商品列表不为空
                if (dto.Products?.Any() != true)
                {
                    return ApiResponse<BatchProductOperationResultDto>.Error(
                        "商品列表不能为空",
                        "NO_PRODUCTS_TO_UPDATE"
                    );
                }

                // 获取所有要更新的商品编码
                var productCodes = dto.Products.Select(u => u.ProductCode).ToList();
                // 批量查询现有商品
                var existingProducts = await db.Queryable<DomesticProduct>()
                    .Where(p => productCodes.Contains(p.ProductCode) && !p.IsDeleted)
                    .ToListAsync();

                // 使用字典提高查找效率
                var existingProductDict = existingProducts.ToDictionary(p => p.ProductCode);

                // 收集需要更新的条形码
                var barcodesToUpdate = dto
                    .Products.Where(u => !string.IsNullOrWhiteSpace(u.Barcode))
                    .Select(u => u.Barcode!)
                    .ToList();

                // 批量检查条形码是否重复
                var duplicateBarcodes = await db.Queryable<DomesticProduct>()
                    .Where(p =>
                        barcodesToUpdate.Contains(p.Barcode!)
                        && !p.IsDeleted
                        && !productCodes.Contains(p.ProductCode)
                    )
                    .Select(p => p.Barcode)
                    .ToListAsync();

                // 收集需要批量更新的商品
                var productsToUpdate = new List<DomesticProduct>();
                var now = DateTime.Now;
                var currentUser = ResolveActorName();

                // 遍历每个更新请求
                foreach (var updateDto in dto.Products)
                {
                    try
                    {
                        // 检查商品是否存在
                        if (
                            !existingProductDict.TryGetValue(
                                updateDto.ProductCode,
                                out var existingProduct
                            )
                        )
                        {
                            result.Errors.Add($"更新商品失败: 商品不存在 {updateDto.ProductCode}");
                            continue;
                        }

                        var hasChanges = false;
                        var changedFields = new List<string>();

                        // 更新商品名称
                        if (
                            !string.IsNullOrWhiteSpace(updateDto.ProductName)
                            && !string.Equals(updateDto.ProductName, existingProduct.ProductName)
                        )
                        {
                            existingProduct.ProductName = updateDto.ProductName;
                            hasChanges = true;
                            changedFields.Add("ProductName");
                        }
                        // 更新商品英文名称
                        if (
                            !string.IsNullOrWhiteSpace(updateDto.EnglishProductName)
                            && !string.Equals(
                                updateDto.EnglishProductName,
                                existingProduct.EnglishProductName
                            )
                        )
                        {
                            existingProduct.EnglishProductName = updateDto.EnglishProductName;
                            hasChanges = true;
                            changedFields.Add("EnglishProductName");
                        }

                        // 更新条形码
                        if (
                            !string.IsNullOrWhiteSpace(updateDto.Barcode)
                            && !string.Equals(updateDto.Barcode, existingProduct.Barcode)
                        )
                        {
                            if (duplicateBarcodes.Contains(updateDto.Barcode))
                            {
                                result.Errors.Add(
                                    $"更新商品失败: 条形码重复 {updateDto.Barcode} (ProductCode: {updateDto.ProductCode})"
                                );
                            }
                            else
                            {
                                existingProduct.Barcode = updateDto.Barcode;
                                hasChanges = true;
                                changedFields.Add("Barcode");
                            }
                        }

                        // 更新国内价格
                        if (
                            updateDto.DomesticPrice.HasValue
                            && existingProduct.DomesticPrice != updateDto.DomesticPrice
                        )
                        {
                            if (updateDto.DomesticPrice.Value < 0)
                            {
                                result.Errors.Add(
                                    $"更新商品失败: 国内价格不能为负 (ProductCode: {updateDto.ProductCode})"
                                );
                            }
                            else
                            {
                                existingProduct.DomesticPrice = updateDto.DomesticPrice;
                                hasChanges = true;
                                changedFields.Add("DomesticPrice");
                            }
                        }
                        // 更新零售价
                        if (
                            updateDto.OEMPrice.HasValue
                            && existingProduct.OEMPrice != updateDto.OEMPrice
                        )
                        {
                            if (updateDto.OEMPrice.Value < 0)
                            {
                                result.Errors.Add(
                                    $"更新商品失败: 零售价不能为负 (ProductCode: {updateDto.ProductCode})"
                                );
                            }
                            else
                            {
                                existingProduct.OEMPrice = updateDto.OEMPrice;
                                hasChanges = true;
                                changedFields.Add("OEMPrice");
                            }
                        }
                        // 更新单件装箱数
                        if (
                            updateDto.PackingQuantity.HasValue
                            && existingProduct.PackingQuantity != updateDto.PackingQuantity
                        )
                        {
                            if (updateDto.PackingQuantity.Value <= 0)
                            {
                                result.Errors.Add(
                                    $"更新商品失败: 单件装箱数必须大于0 (ProductCode: {updateDto.ProductCode})"
                                );
                            }
                            else
                            {
                                existingProduct.PackingQuantity = updateDto.PackingQuantity;
                                hasChanges = true;
                                changedFields.Add("PackingQuantity");
                            }
                        }
                        // 更新单件体积
                        if (
                            updateDto.UnitVolume.HasValue
                            && existingProduct.UnitVolume != updateDto.UnitVolume
                        )
                        {
                            if (updateDto.UnitVolume.Value < 0)
                            {
                                result.Errors.Add(
                                    $"更新商品失败: 单件体积不能为负 (ProductCode: {updateDto.ProductCode})"
                                );
                            }
                            else
                            {
                                existingProduct.UnitVolume = updateDto.UnitVolume;
                                hasChanges = true;
                                changedFields.Add("UnitVolume");
                            }
                        }
                        // 更新中包数量
                        if (
                            updateDto.MiddlePackQuantity.HasValue
                            && existingProduct.MiddlePackQuantity != updateDto.MiddlePackQuantity
                        )
                        {
                            if (updateDto.MiddlePackQuantity.Value <= 0)
                            {
                                result.Errors.Add(
                                    $"更新商品失败: 中包数量必须大于0 (ProductCode: {updateDto.ProductCode})"
                                );
                            }
                            else
                            {
                                existingProduct.MiddlePackQuantity = updateDto.MiddlePackQuantity;
                                hasChanges = true;
                                changedFields.Add("MiddlePackQuantity");
                            }
                        }
                        // 更新商品图片
                        if (
                            !string.IsNullOrWhiteSpace(updateDto.ProductImage)
                            && existingProduct.ProductImage != updateDto.ProductImage
                        )
                        {
                            existingProduct.ProductImage = updateDto.ProductImage;
                            hasChanges = true;
                            changedFields.Add("ProductImage");
                        }

                        // 如果有变更，添加到批量更新列表
                        if (hasChanges)
                        {
                            existingProduct.UpdatedAt = now;
                            existingProduct.UpdatedBy = currentUser;
                            productsToUpdate.Add(existingProduct);

                            result.UpdatedChanges.Add(
                                new ProductChangeInfo
                                {
                                    ProductCode = existingProduct.ProductCode,
                                    ChangeList = changedFields,
                                }
                            );
                        }
                        else
                        {
                            _logger.LogInformation(
                                "跳过更新（无变更）: {ProductCode}",
                                updateDto.ProductCode
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "更新单个商品失败: {ProductCode}",
                            updateDto.ProductCode
                        );
                        // 参数校验在上方显式分支中继续；数据库或未知异常必须回滚整批。
                        throw;
                    }
                }

                // 批量更新到数据库
                if (productsToUpdate.Any())
                {
                    var auditBatchGuid = Guid.NewGuid();
                    await db.Ado.BeginTranAsync();
                    try
                    {
                        var updatedProductCodes = productsToUpdate
                            .Select(product => product.ProductCode)
                            .ToList();
                        var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            updatedProductCodes
                        );
                        await db.Updateable(productsToUpdate).ExecuteCommandAsync();

                        // 查询供应商信息用于填充返回结果
                        var supplierCodes = productsToUpdate
                            .Select(p => p.SupplierCode)
                            .Where(code => !string.IsNullOrWhiteSpace(code))
                            .Select(code => code!)
                            .Distinct()
                            .ToList();
                        var suppliers = await db.Queryable<ChinaSupplier>()
                            .Where(s => s.SupplierCode != null && supplierCodes.Contains(s.SupplierCode))
                            .ToListAsync();
                        var supplierDict = suppliers
                            .Where(s => !string.IsNullOrWhiteSpace(s.SupplierCode))
                            .GroupBy(s => s.SupplierCode!)
                            .ToDictionary(g => g.Key, g => g.First());

                        // 构建返回的更新商品列表
                        foreach (var product in productsToUpdate)
                        {
                            var productDto = _mapper.Map<DomesticProductDto>(product);
                            if (
                                !string.IsNullOrWhiteSpace(product.SupplierCode)
                                && supplierDict.TryGetValue(product.SupplierCode, out var supplier)
                            )
                            {
                                productDto.SupplierName = supplier.SupplierName;
                            }
                            result.UpdatedProducts.Add(productDto);
                        }

                        await RecordDomesticProductChangesAsync(
                            beforeSnapshots,
                            updatedProductCodes,
                            "BatchUpdate",
                            "DomesticProductBatch",
                            auditBatchGuid
                        );
                        await db.Ado.CommitTranAsync();
                    }
                    catch
                    {
                        await db.Ado.RollbackTranAsync();
                        throw;
                    }
                }

                _logger.LogInformation(
                    "批量更新商品完成，更新: {UpdatedCount}, 错误: {ErrorCount}",
                    result.UpdatedProducts.Count,
                    result.Errors.Count
                );
                return ApiResponse<BatchProductOperationResultDto>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新商品失败");
                return ApiResponse<BatchProductOperationResultDto>.Error(
                    "批量更新商品失败",
                    "BATCH_UPDATE_PRODUCTS_ERROR"
                );
            }
        }

        public async Task<ApiResponse<BatchUpdateHbwebProductNamesResultDto>> BatchUpdateHbwebProductNamesAsync(
            BatchUpdateHbwebProductNamesDto dto
        )
        {
            var result = new BatchUpdateHbwebProductNamesResultDto();

            try
            {
                var normalizedItems = (dto.Products ?? new List<HbwebProductNameUpdateItemDto>())
                    .Select(item => new HbwebProductNameUpdateItemDto
                    {
                        SupplierCode = item?.SupplierCode?.Trim() ?? string.Empty,
                        ItemNumber = item?.ItemNumber?.Trim() ?? string.Empty,
                        ProductName = item?.ProductName?.Trim() ?? string.Empty,
                    })
                    .ToList();

                if (!normalizedItems.Any())
                {
                    return ApiResponse<BatchUpdateHbwebProductNamesResultDto>.Error(
                        "商品列表不能为空",
                        "NO_HBWEB_PRODUCT_NAMES",
                        result
                    );
                }

                var invalidItemErrors = new List<string>();
                foreach (var item in normalizedItems)
                {
                    // 统一在服务层做写主表前校验，避免 ApiController 提前返回导致前端拿不到明细错误。
                    if (string.IsNullOrWhiteSpace(item.SupplierCode))
                    {
                        invalidItemErrors.Add($"供应商代码不能为空: /{item.ItemNumber}");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(item.ItemNumber))
                    {
                        invalidItemErrors.Add($"货号不能为空: {item.SupplierCode}/");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(item.ProductName))
                    {
                        invalidItemErrors.Add($"商品名称不能为空: {item.SupplierCode}/{item.ItemNumber}");
                        continue;
                    }

                    if (item.ItemNumber.Length > 50)
                    {
                        invalidItemErrors.Add($"货号不能超过50个字符: {item.SupplierCode}/{item.ItemNumber}");
                    }

                    if (item.SupplierCode.Length > 50)
                    {
                        invalidItemErrors.Add($"供应商代码不能超过50个字符: {item.SupplierCode}/{item.ItemNumber}");
                    }

                    if (item.ProductName.Length > 200)
                    {
                        invalidItemErrors.Add($"商品名称不能超过200个字符: {item.SupplierCode}/{item.ItemNumber}");
                    }
                }

                if (invalidItemErrors.Any())
                {
                    result.Errors.AddRange(invalidItemErrors);
                    return ApiResponse<BatchUpdateHbwebProductNamesResultDto>.Error(
                        "存在无效供应商代码、货号或商品名称，请先修正后再更新",
                        "INVALID_HBWEB_PRODUCT_NAMES",
                        result
                    );
                }

                var duplicateRequestConflicts = normalizedItems
                    .GroupBy(
                        item => $"{item.SupplierCode}\u001f{item.ItemNumber}",
                        StringComparer.OrdinalIgnoreCase
                    )
                    .Where(group => group.Select(item => item.ProductName).Distinct(StringComparer.Ordinal).Count() > 1)
                    .Select(group => group.First())
                    .ToList();

                if (duplicateRequestConflicts.Any())
                {
                    result.Errors.AddRange(duplicateRequestConflicts.Select(item => $"同一供应商/货号存在多个商品名称: {item.SupplierCode}/{item.ItemNumber}"));
                    return ApiResponse<BatchUpdateHbwebProductNamesResultDto>.Error(
                        "同一供应商/货号存在多个商品名称，请先修正后再更新",
                        "DUPLICATE_ITEM_NUMBER_NAMES",
                        result
                    );
                }

                if (dto.SyncToHq)
                {
                    var hqRequestConflicts = normalizedItems
                        .GroupBy(item => item.ItemNumber, StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.Select(item => item.ProductName).Distinct(StringComparer.Ordinal).Count() > 1)
                        .Select(group => group.Key)
                        .ToList();
                    if (hqRequestConflicts.Any())
                    {
                        result.Errors.AddRange(hqRequestConflicts.Select(itemNumber => $"HQ 同一货号跨供应商存在多个商品名称: {itemNumber}"));
                        return ApiResponse<BatchUpdateHbwebProductNamesResultDto>.Error(
                            "HQ 同一货号跨供应商存在多个商品名称，请先修正后再更新",
                            "DUPLICATE_HQ_ITEM_NUMBER_NAMES",
                            result
                        );
                    }
                }

                // 同一供应商和货号的同名请求只写一次，避免重复行造成重复更新。
                var updates = normalizedItems
                    .GroupBy(
                        item => $"{item.SupplierCode}\u001f{item.ItemNumber}",
                        StringComparer.OrdinalIgnoreCase
                    )
                    .Select(group => group.First())
                    .ToList();
                var supplierCodes = normalizedItems
                    .Select(item => item.SupplierCode)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var itemNumbers = normalizedItems
                    .Select(item => item.ItemNumber)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var db = _context.Db;
                var products = await db.Queryable<Product>()
                    .Where(product =>
                        product.LocalSupplierCode != null
                        && supplierCodes.Contains(product.LocalSupplierCode)
                        && product.ItemNumber != null
                        && itemNumbers.Contains(product.ItemNumber)
                        && !product.IsDeleted
                    )
                    .ToListAsync();
                var productGroups = products
                    .GroupBy(
                        product => $"{product.LocalSupplierCode?.Trim() ?? string.Empty}\u001f{product.ItemNumber?.Trim() ?? string.Empty}",
                        StringComparer.OrdinalIgnoreCase
                    )
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToList(),
                        StringComparer.OrdinalIgnoreCase
                    );
                var productsToUpdate = new List<Product>();

                foreach (var update in updates)
                {
                    var supplierItemKey = $"{update.SupplierCode}\u001f{update.ItemNumber}";
                    if (!productGroups.TryGetValue(supplierItemKey, out var matchedProducts))
                    {
                        result.MissingItemNumbers.Add(update.ItemNumber);
                        continue;
                    }

                    if (matchedProducts.Count > 1)
                    {
                        result.Errors.Add($"HBweb 商品主表供应商/货号重复，已跳过: {update.SupplierCode}/{update.ItemNumber}");
                        continue;
                    }

                    var product = matchedProducts[0];
                    if (string.Equals(product.ProductName?.Trim(), update.ProductName, StringComparison.Ordinal))
                    {
                        result.UnchangedCount++;
                        continue;
                    }

                    // 只写 Product.ProductName，保留英文名、价格、条码、图片和分店价格不变。
                    product.ProductName = update.ProductName;
                    product.UpdatedAt = DateTime.Now;
                    product.UpdatedBy = ResolveActorName();
                    productsToUpdate.Add(product);
                }

                if (productsToUpdate.Any())
                {
                    var auditBatchGuid = Guid.NewGuid();
                    var productCodes = productsToUpdate
                        .Select(product => product.ProductCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                        .ToList();
                    await db.Ado.BeginTranAsync();
                    try
                    {
                        var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            productCodes
                        );
                        await db.Updateable(productsToUpdate)
                            // 只允许写商品名称和审计字段，避免整实体更新覆盖价格、条码、图片等主表字段。
                            .UpdateColumns(product => new { product.ProductName, product.UpdatedAt, product.UpdatedBy })
                            .ExecuteCommandAsync();
                        await RecordDomesticProductChangesAsync(
                            beforeSnapshots,
                            productCodes,
                            "BatchUpdate",
                            "DomesticProduct",
                            auditBatchGuid,
                            "BatchProductNames"
                        );
                        await db.Ado.CommitTranAsync();
                    }
                    catch (Exception ex)
                    {
                        // 本地主表事务失败时必须立即停止，避免把未落库的名称继续同步到 HQ。
                        await db.Ado.RollbackTranAsync();
                        _logger.LogError(ex, "批量更新 HBweb 商品主表商品名称事务失败");
                        return ApiResponse<BatchUpdateHbwebProductNamesResultDto>.Error(
                            "批量更新 HBweb 商品主表商品名称失败",
                            "BATCH_UPDATE_HBWEB_PRODUCT_NAMES_ERROR",
                            result
                        );
                    }
                }

                result.UpdatedCount = productsToUpdate.Count;

                if (dto.SyncToHq)
                {
                    var hqResult = new HqProductNameSyncResultDto { Success = true };
                    result.HqSyncResult = hqResult;
                    var hqUpdates = updates
                        .GroupBy(item => item.ItemNumber, StringComparer.OrdinalIgnoreCase)
                        .Select(group => group.First())
                        .ToList();
                    var hqItemNumbers = hqUpdates.Select(item => item.ItemNumber).ToList();

                    try
                    {
                        // HBweb 已在独立事务中提交；HQ 固定匹配供货商 200 和货号，单独开启 HQ 事务。
                        var hqDb = _hqContext.Db;
                        var hqProducts = new List<DIC_商品信息字典表>();

                        // 单个 IN 最多 1000 个参数，避免触碰 SQL Server 参数上限；H货号 保持裸列比较以利用索引。
                        foreach (var itemNumberChunk in hqItemNumbers.Chunk(HqProductNameQueryChunkSize))
                        {
                            var queryItemNumbers = itemNumberChunk.ToList();
                            var hqProductChunk = await hqDb.Queryable<DIC_商品信息字典表>()
                                .Where(product =>
                                    product.H供货商编码 == HqProductNameSupplierCode
                                    && product.H货号 != null
                                    && queryItemNumbers.Contains(product.H货号)
                                )
                                .Select(product => new DIC_商品信息字典表
                                {
                                    ID = product.ID,
                                    H供货商编码 = product.H供货商编码,
                                    H货号 = product.H货号,
                                    H商品名称 = product.H商品名称,
                                })
                                .ToListAsync();
                            hqProducts.AddRange(hqProductChunk);
                        }
                        var hqProductGroups = hqProducts
                            .GroupBy(
                                product => $"{product.H供货商编码?.Trim() ?? string.Empty}\u001f{product.H货号?.Trim() ?? string.Empty}",
                                StringComparer.OrdinalIgnoreCase
                            )
                            .ToDictionary(
                                group => group.Key,
                                group => group.ToList(),
                                StringComparer.OrdinalIgnoreCase
                            );
                        var hqProductsToUpdate = new List<DIC_商品信息字典表>();
                        var hqModifiedAt = DateTime.Now;

                        foreach (var update in hqUpdates)
                        {
                            var hqSupplierItemKey = $"{HqProductNameSupplierCode}\u001f{update.ItemNumber}";
                            if (!hqProductGroups.TryGetValue(hqSupplierItemKey, out var matchedHqProducts))
                            {
                                hqResult.MissingItemNumbers.Add(update.ItemNumber);
                                continue;
                            }

                            if (matchedHqProducts.Count > 1)
                            {
                                hqResult.Errors.Add($"HQ 商品字典供应商/货号重复，已跳过: {HqProductNameSupplierCode}/{update.ItemNumber}");
                                continue;
                            }

                            var hqProduct = matchedHqProducts[0];
                            if (string.Equals(hqProduct.H商品名称?.Trim(), update.ProductName, StringComparison.Ordinal))
                            {
                                hqResult.UnchangedCount++;
                                continue;
                            }

                            hqProduct.H商品名称 = update.ProductName;
                            hqProduct.FGC_LastModifier = "HBweb";
                            hqProduct.FGC_LastModifyDate = hqModifiedAt;
                            hqProductsToUpdate.Add(hqProduct);
                        }

                        if (hqProductsToUpdate.Any())
                        {
                            var hqTransactionResult = await hqDb.Ado.UseTranAsync(async () =>
                            {
                                // 每块最多写 200 个实体，但所有写块共用同一个 HQ 事务，任一块失败则整体回滚。
                                foreach (var hqProductChunk in hqProductsToUpdate.Chunk(HqProductNameUpdateChunkSize))
                                {
                                    await hqDb.Updateable(hqProductChunk.ToList())
                                        // 跨库同步严格限定为商品名称和两个审计字段，禁止覆盖大写名、价格、条码、图片等字段。
                                        .UpdateColumns(product => new
                                        {
                                            product.H商品名称,
                                            product.FGC_LastModifier,
                                            product.FGC_LastModifyDate,
                                        })
                                        .ExecuteCommandAsync();
                                }
                            });

                            if (!hqTransactionResult.IsSuccess)
                            {
                                throw new InvalidOperationException(
                                    hqTransactionResult.ErrorException?.Message ?? "HQ 商品名称更新事务失败",
                                    hqTransactionResult.ErrorException
                                );
                            }
                        }

                        hqResult.UpdatedCount = hqProductsToUpdate.Count;
                    }
                    catch (Exception ex)
                    {
                        hqResult.Success = false;
                        hqResult.Errors.Add("HQ 商品名称同步失败，请稍后重试");
                        _logger.LogError(ex, "HQ 商品名称同步失败，HBweb 更新已提交");

                        var partialFailure = ApiResponse<BatchUpdateHbwebProductNamesResultDto>.Error(
                            "HBweb 商品名称已更新，但 HQ 商品名称同步失败",
                            "HQ_PRODUCT_NAME_SYNC_FAILED",
                            result
                        );
                        // 同时保留 Data 和 Details，确保服务调用方与控制器都能取得 HBweb 已完成的部分结果。
                        partialFailure.Data = result;
                        return partialFailure;
                    }
                }

                var message = $"HBweb 商品主表名称更新完成：更新{result.UpdatedCount}条，无变化{result.UnchangedCount}条，未找到{result.MissingItemNumbers.Count}条";
                if (result.Errors.Any())
                {
                    message += $"，跳过{result.Errors.Count}条";
                }

                return ApiResponse<BatchUpdateHbwebProductNamesResultDto>.OK(result, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新 HBweb 商品主表商品名称失败");
                return ApiResponse<BatchUpdateHbwebProductNamesResultDto>.Error(
                    "批量更新 HBweb 商品主表商品名称失败",
                    "BATCH_UPDATE_HBWEB_PRODUCT_NAMES_ERROR",
                    result
                );
            }
        }

        public async Task<ApiResponse<bool>> BatchDeleteAsync(List<string> productCodes)
        {
            try
            {
                if (productCodes == null || !productCodes.Any())
                {
                    return ApiResponse<bool>.Error("请选择要删除的商品", "NO_ITEMS_SELECTED");
                }
                var db = _context.Db;
                var currentUser = ResolveActorName();
                var result = await db.Updateable<DomesticProduct>()
                    .SetColumns(p => p.IsDeleted == true)
                    .SetColumns(p => p.UpdatedAt == DateTime.Now)
                    .SetColumns(p => p.UpdatedBy == currentUser)
                    .Where(p => productCodes.Contains(p.ProductCode))
                    .ExecuteCommandAsync();
                _logger.LogInformation("批量删除商品成功: {Count} 件", result);
                return ApiResponse<bool>.OK(true, $"成功删除 {result} 件商品");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除商品失败");
                return ApiResponse<bool>.Error("删除失败", "BATCH_DELETE_ERROR");
            }
        }

        public async Task<ApiResponse<List<DomesticSetProductDto>>> GetSetItemsAsync(
            string productCode
        )
        {
            try
            {
                var db = _context.Db;
                var setProduct = await db.Queryable<DomesticProduct>()
                    .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                    .FirstAsync();
                if (setProduct == null)
                {
                    return ApiResponse<List<DomesticSetProductDto>>.Error(
                        "商品不存在",
                        "PRODUCT_NOT_FOUND"
                    );
                }
                if (setProduct.ProductType != 1)
                {
                    return ApiResponse<List<DomesticSetProductDto>>.Error(
                        "该商品不是套装商品",
                        "NOT_SET_PRODUCT"
                    );
                }
                var items = await db.Queryable<DomesticSetProduct>()
                    .Where(sp =>
                        sp.ProductCode == productCode
                        && !sp.IsDeleted
                        // 历史子项的 ProductNo 可能为空，此时按父商品货号判断，避免放行代表主商品的关系行。
                        && (
                            (sp.ProductNo != null && sp.ProductNo != sp.SetProductNo)
                            || (
                                sp.ProductNo == null
                                && setProduct.HBProductNo != sp.SetProductNo
                            )
                        )
                    )
                    .Select(sp => new DomesticSetProductDto
                    {
                        SetProductCode = sp.SetProductCode,
                        ProductCode = sp.ProductCode,
                        ProductNo = sp.ProductNo,
                        SetProductNo = sp.SetProductNo,
                        SetBarcode = sp.SetBarcode,
                        DomesticPrice = sp.DomesticPrice,
                        OEMPrice = sp.OEMPrice,
                        ImportPrice = sp.ImportPrice,
                        Remarks = sp.Remarks,
                        CreatedAt = sp.CreatedAt,
                        UpdatedAt = sp.UpdatedAt,
                        CreatedBy = sp.CreatedBy,
                        UpdatedBy = sp.UpdatedBy,
                        IsDeleted = sp.IsDeleted,
                    })
                    .ToListAsync();
                return ApiResponse<List<DomesticSetProductDto>>.OK(items);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取套装商品信息失败: ProductCode={ProductCode}",
                    productCode
                );
                return ApiResponse<List<DomesticSetProductDto>>.Error(
                    "获取失败",
                    "GET_SET_ITEMS_ERROR"
                );
            }
        }

        public async Task<ApiResponse<bool>> UpdateSetItemsAsync(
            string productCode,
            List<SetItemUpdateDto> items
        )
        {
            try
            {
                var db = _context.Db;
                var product = await db.Queryable<DomesticProduct>()
                    .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                    .FirstAsync();
                if (product == null)
                {
                    return ApiResponse<bool>.Error("商品不存在", "PRODUCT_NOT_FOUND");
                }
                if (product.ProductType != 1)
                {
                    return ApiResponse<bool>.Error("该商品不是套装商品", "NOT_SET_PRODUCT");
                }

                var existingItems = await db.Queryable<DomesticSetProduct>()
                    .Where(sp =>
                        sp.ProductCode == productCode
                        && !sp.IsDeleted
                        // 与读取规则保持一致，确保历史空 ProductNo 子项能被更新且不会误处理主商品关系行。
                        && (
                            (sp.ProductNo != null && sp.ProductNo != sp.SetProductNo)
                            || (sp.ProductNo == null && product.HBProductNo != sp.SetProductNo)
                        )
                    )
                    .ToListAsync();
                var existingItemsDict = existingItems.ToDictionary(x => x.SetProductCode);

                var currentUser = ResolveActorName();
                var now = DateTime.UtcNow;

                var requestedSetProductCodes = new HashSet<string>();
                var itemsToUpdate = new List<DomesticSetProduct>();
                var itemsToInsert = new List<DomesticSetProduct>();

                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.SetProductCode))
                    {
                        requestedSetProductCodes.Add(item.SetProductCode);
                        if (
                            existingItemsDict.TryGetValue(item.SetProductCode, out var existingItem)
                        )
                        {
                            existingItem.SetProductNo =
                                item.SetProductNo ?? existingItem.SetProductNo;
                            existingItem.SetBarcode = item.SetBarcode;
                            existingItem.DomesticPrice = item.DomesticPrice;
                            existingItem.ImportPrice = item.ImportPrice;
                            existingItem.OEMPrice = item.OEMPrice;
                            existingItem.UpdatedAt = now;
                            existingItem.UpdatedBy = currentUser;
                            itemsToUpdate.Add(existingItem);
                        }
                    }
                    else
                    {
                        string setProductNo = item.SetProductNo ?? string.Empty;
                        string? setBarcode = item.SetBarcode;
                        if (
                            string.IsNullOrWhiteSpace(setProductNo)
                            || string.IsNullOrWhiteSpace(setBarcode)
                        )
                        {
                            var productType = (ProductTypeEnum)product.ProductType;
                            var (newItemNumber, newBarcode) =
                                await _itemBarcodeService.GenerateSetItemNumberAndBarcodeAsync(
                                    product.HBProductNo ?? string.Empty,
                                    productType
                                );
                            if (string.IsNullOrWhiteSpace(setProductNo))
                                setProductNo = newItemNumber;
                            if (string.IsNullOrWhiteSpace(setBarcode))
                                setBarcode = newBarcode;
                        }

                        var newItem = new DomesticSetProduct
                        {
                            SetProductCode = UuidHelper.GenerateUuid7(),
                            ProductCode = productCode,
                            ProductNo = product.HBProductNo,
                            SetProductNo = setProductNo,
                            SetBarcode = setBarcode,
                            DomesticPrice = item.DomesticPrice,
                            ImportPrice = item.ImportPrice,
                            OEMPrice = item.OEMPrice,
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = currentUser,
                            UpdatedBy = currentUser,
                            IsDeleted = false,
                        };
                        itemsToInsert.Add(newItem);
                        requestedSetProductCodes.Add(newItem.SetProductCode);
                    }
                }

                var itemsToDelete = existingItems
                    .Where(x => !requestedSetProductCodes.Contains(x.SetProductCode))
                    .ToList();
                foreach (var item in itemsToDelete)
                {
                    item.IsDeleted = true;
                    item.UpdatedAt = now;
                    item.UpdatedBy = currentUser;
                }

                await db.Ado.BeginTranAsync();
                try
                {
                    if (itemsToUpdate.Any())
                        await db.Updateable(itemsToUpdate).ExecuteCommandAsync();
                    if (itemsToInsert.Any())
                        await db.Insertable(itemsToInsert).ExecuteCommandAsync();
                    if (itemsToDelete.Any())
                        await db.Updateable(itemsToDelete)
                            .UpdateColumns(x => new
                            {
                                x.IsDeleted,
                                x.UpdatedAt,
                                x.UpdatedBy,
                            })
                            .ExecuteCommandAsync();
                    await db.Ado.CommitTranAsync();
                    _logger.LogInformation(
                        "套装商品更新成功: ProductCode={ProductCode}, Updated={UpdateCount}, Inserted={InsertCount}, Deleted={DeleteCount}",
                        productCode,
                        itemsToUpdate.Count,
                        itemsToInsert.Count,
                        itemsToDelete.Count
                    );
                    return ApiResponse<bool>.OK(true, "保存成功");
                }
                catch (Exception ex)
                {
                    await db.Ado.RollbackTranAsync();
                    _logger.LogError(
                        ex,
                        "套装商品更新事务失败: ProductCode={ProductCode}",
                        productCode
                    );
                    return ApiResponse<bool>.Error("保存失败，请重试", "UPDATE_TRANSACTION_ERROR");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "更新套装商品信息失败: ProductCode={ProductCode}",
                    productCode
                );
                return ApiResponse<bool>.Error("保存失败", "UPDATE_SET_ITEMS_ERROR");
            }
        }

        public async Task<ApiResponse<BatchCreateSetProductsResultDto>> BatchCreateSetProductsAsync(
            BatchCreateSetProductsDto dto
        )
        {
            try
            {
                var db = _context.Db;
                var result = new BatchCreateSetProductsResultDto();
                var createdProducts = new List<DomesticProductDto>();
                var errors = new List<string>();
                var totalSetItems = 0;

                var supplier = await db.Queryable<ChinaSupplier>()
                    .FirstAsync(s => s.SupplierCode == dto.SupplierCode);
                if (supplier == null)
                {
                    return ApiResponse<BatchCreateSetProductsResultDto>.Error(
                        $"供应商 {dto.SupplierCode} 不存在",
                        "SUPPLIER_NOT_FOUND"
                    );
                }

                if (dto.SetPrices.Count != dto.SetType)
                {
                    return ApiResponse<BatchCreateSetProductsResultDto>.Error(
                        $"套装价格数量({dto.SetPrices.Count})与套装规格({dto.SetType})不匹配",
                        "PRICE_COUNT_MISMATCH"
                    );
                }

                var existingBarcodes = await db.Queryable<DomesticProduct>()
                    .Where(p =>
                        p.SupplierCode == dto.SupplierCode && p.Barcode != null && !p.IsDeleted
                    )
                    .Select(p => p.Barcode!)
                    .ToListAsync();
                var existingSetBarcodes = await db.Queryable<DomesticSetProduct>()
                    .LeftJoin<DomesticProduct>((sp, p) => sp.ProductCode == p.ProductCode)
                    .Where(
                        (sp, p) =>
                            p.SupplierCode == dto.SupplierCode
                            && sp.SetBarcode != null
                            && !sp.IsDeleted
                    )
                    .Select((sp, p) => sp.SetBarcode!)
                    .ToListAsync();
                existingBarcodes.AddRange(existingSetBarcodes);

                string? prefixName = null;
                if (!string.IsNullOrWhiteSpace(dto.PrefixCode))
                {
                    var prefix = await db.Queryable<ProductPrefixCode>()
                        .Where(p =>
                            p.PrefixCode == dto.PrefixCode
                            && p.SupplierCode == dto.SupplierCode
                            && !p.IsDeleted
                        )
                        .FirstAsync();
                    if (prefix == null)
                    {
                        return ApiResponse<BatchCreateSetProductsResultDto>.Error(
                            "前缀不存在或不属于该供应商",
                            "PREFIX_NOT_FOUND"
                        );
                    }
                    prefixName = prefix.PrefixName;
                }

                try
                {
                    db.Ado.BeginTran();
                    var auditBatchGuid = Guid.NewGuid();
                    var currentUser = ResolveActorName();
                    var now = DateTime.UtcNow;

                    var productNoBarcodeList =
                        await _itemBarcodeService.GenerateBatchItemNumbersAndBarcodesAsync(
                            dto.SupplierCode,
                            ProductTypeEnum.Normal,
                            dto.Products.Count,
                            prefixName
                        );

                    var allDomesticProducts = new List<DomesticProduct>();
                    var allSetProducts = new List<DomesticSetProduct>();
                    var allCreationLogs = new List<DomesticProductCreationLog>();

                    foreach (var product in dto.Products)
                    {
                        try
                        {
                            var index = dto.Products.IndexOf(product);
                            var productNo = productNoBarcodeList[index].itemNumber;
                            var barcode = productNoBarcodeList[index].barcode;

                            var productCode = Guid.NewGuid().ToString();
                            var domesticProduct = new DomesticProduct
                            {
                                ProductCode = productCode,
                                SupplierCode = dto.SupplierCode,
                                ProductName = product.ProductName,
                                EnglishProductName = product.EnglishProductName,
                                ProductSpecification = product.ProductSpecification,
                                HBProductNo = productNo,
                                Barcode = barcode,
                                ProductType = 1,
                                IsActive = true,
                                IsDeleted = false,
                                CreatedAt = now,
                                CreatedBy = currentUser,
                                UpdatedAt = now,
                                UpdatedBy = currentUser,
                            };
                            allDomesticProducts.Add(domesticProduct);

                            var setItemNumberBarcodeList =
                                await _itemBarcodeService.GenerateBatchSetItemNumbersAndBarcodesAsync(
                                    productNo,
                                    ProductTypeEnum.Set,
                                    dto.SetType
                                );

                            for (int i = 0; i < dto.SetType; i++)
                            {
                                var setPriceItem = dto.SetPrices[i];
                                var setProductNo = setItemNumberBarcodeList[i].itemNumber;
                                var setBarcode = setItemNumberBarcodeList[i].barcode;
                                var setProduct = new DomesticSetProduct
                                {
                                    SetProductCode = Guid.NewGuid().ToString(),
                                    ProductCode = productCode,
                                    ProductNo = productNo,
                                    SetProductNo = setProductNo,
                                    SetBarcode = setBarcode,
                                    DomesticPrice = setPriceItem.DomesticPrice,
                                    ImportPrice = setPriceItem.ImportPrice,
                                    OEMPrice = setPriceItem.OEMPrice,
                                    IsDeleted = false,
                                    CreatedAt = DateTime.Now,
                                };
                                allSetProducts.Add(setProduct);
                            }

                            var creationLog = new DomesticProductCreationLog
                            {
                                LogId = Guid.NewGuid().ToString(),
                                ProductCode = productCode,
                                SupplierCode = dto.SupplierCode,
                                ProductName = product.ProductName,
                                HBProductNo = productNo,
                                PrefixCode = dto.PrefixCode,
                                CreationType = "BatchSetProducts",
                                Remark = $"套装商品批量创建，套装规格：套{dto.SetType}",
                                CreatedAt = DateTime.Now,
                            };
                            allCreationLogs.Add(creationLog);

                            var productDto = _mapper.Map<DomesticProductDto>(domesticProduct);
                            createdProducts.Add(productDto);
                            totalSetItems += dto.SetType;
                            _logger.LogInformation(
                                "成功创建套装商品: {ProductName}, 货号: {ProductNo}, 套装数: {SetCount}",
                                product.ProductName,
                                productNo,
                                dto.SetType
                            );
                        }
                        catch (ArgumentException ex)
                        {
                            _logger.LogWarning(
                                ex,
                                "创建商品失败: {ProductName}",
                                product.ProductName
                            );
                            errors.Add($"创建商品 '{product.ProductName}' 失败: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "批量创建套装商品时数据库或系统异常");
                            throw;
                        }
                    }

                    var auditProductCodes = allDomesticProducts
                        .Select(product => product.ProductCode)
                        .ToList();
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        auditProductCodes
                    );

                    if (allDomesticProducts.Any())
                        await db.Insertable(allDomesticProducts).ExecuteCommandAsync();
                    if (allSetProducts.Any())
                        await db.Insertable(allSetProducts).ExecuteCommandAsync();
                    if (allCreationLogs.Any())
                        await db.Insertable(allCreationLogs).ExecuteCommandAsync();

                    // 套装子项只写 DomesticSetProduct，不进入 DomesticProduct 历史。
                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        auditProductCodes,
                        "Create",
                        "DomesticProductBatch",
                        auditBatchGuid
                    );

                    db.Ado.CommitTran();

                    result.CreatedProducts = createdProducts;
                    result.SuccessCount = createdProducts.Count;
                    result.FailureCount = dto.Products.Count - createdProducts.Count;
                    result.TotalSetItems = totalSetItems;
                    result.Errors = errors;

                    _logger.LogInformation(
                        "批量创建套装商品完成: 成功{SuccessCount}个, 失败{FailureCount}个, 总套装明细{TotalSetItems}个",
                        result.SuccessCount,
                        result.FailureCount,
                        result.TotalSetItems
                    );
                    return new ApiResponse<BatchCreateSetProductsResultDto>
                    {
                        Success = true,
                        Data = result,
                        Message =
                            $"批量创建完成：成功{result.SuccessCount}个商品，共{result.TotalSetItems}个套装明细",
                    };
                }
                catch (Exception ex)
                {
                    db.Ado.RollbackTran();
                    _logger.LogError(ex, "批量创建套装商品事务失败");
                    return ApiResponse<BatchCreateSetProductsResultDto>.Error(
                        $"批量创建失败: {ex.Message}",
                        "BATCH_CREATE_ERROR"
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建套装商品失败");
                return ApiResponse<BatchCreateSetProductsResultDto>.Error(
                    "批量创建套装商品失败",
                    "BATCH_CREATE_SET_PRODUCTS_ERROR"
                );
            }
        }

        // ============== 私有辅助方法（检测变更、生成货号/条码） ==============
        private bool CheckForChanges(
            BatchProductInputDto inputProduct,
            DomesticProduct existingProduct
        )
        {
            if (
                !string.IsNullOrWhiteSpace(inputProduct.ProductName)
                && inputProduct.ProductName != existingProduct.ProductName
            )
                return true;
            if (
                !string.IsNullOrWhiteSpace(inputProduct.EnglishProductName)
                && inputProduct.EnglishProductName != existingProduct.EnglishProductName
            )
                return true;
            if (
                !string.IsNullOrWhiteSpace(inputProduct.Barcode)
                && inputProduct.Barcode != existingProduct.Barcode
            )
                return true;
            if (
                inputProduct.DomesticPrice.HasValue
                && inputProduct.DomesticPrice != existingProduct.DomesticPrice
            )
                return true;
            if (inputProduct.OEMPrice.HasValue && inputProduct.OEMPrice != existingProduct.OEMPrice)
                return true;
            if (
                inputProduct.PackingQuantity.HasValue
                && inputProduct.PackingQuantity != existingProduct.PackingQuantity
            )
                return true;
            if (
                inputProduct.UnitVolume.HasValue
                && inputProduct.UnitVolume != existingProduct.UnitVolume
            )
                return true;
            if (
                inputProduct.MiddlePackQuantity.HasValue
                && inputProduct.MiddlePackQuantity != existingProduct.MiddlePackQuantity
            )
                return true;
            return false;
        }

        private List<string> GetChangeList(
            BatchProductInputDto inputProduct,
            DomesticProduct existingProduct
        )
        {
            var changes = new List<string>();
            if (
                !string.IsNullOrWhiteSpace(inputProduct.ProductName)
                && inputProduct.ProductName != existingProduct.ProductName
            )
                changes.Add("ProductName");
            if (
                !string.IsNullOrWhiteSpace(inputProduct.EnglishProductName)
                && inputProduct.EnglishProductName != existingProduct.EnglishProductName
            )
                changes.Add("EnglishProductName");
            if (
                !string.IsNullOrWhiteSpace(inputProduct.Barcode)
                && inputProduct.Barcode != existingProduct.Barcode
            )
                changes.Add("Barcode");
            if (
                inputProduct.DomesticPrice.HasValue
                && inputProduct.DomesticPrice != existingProduct.DomesticPrice
            )
                changes.Add("DomesticPrice");
            if (inputProduct.OEMPrice.HasValue && inputProduct.OEMPrice != existingProduct.OEMPrice)
                changes.Add("OEMPrice");
            if (
                inputProduct.PackingQuantity.HasValue
                && inputProduct.PackingQuantity != existingProduct.PackingQuantity
            )
                changes.Add("PackingQuantity");
            if (
                inputProduct.UnitVolume.HasValue
                && inputProduct.UnitVolume != existingProduct.UnitVolume
            )
                changes.Add("UnitVolume");
            if (
                inputProduct.MiddlePackQuantity.HasValue
                && inputProduct.MiddlePackQuantity != existingProduct.MiddlePackQuantity
            )
                changes.Add("MiddlePackQuantity");
            return changes;
        }

        public async Task<ApiResponse<SyncResult>> SyncSelectedToHBSalesAsync(
            List<string> productCodes,
            bool includeImage = false
        )
        {
            try
            {
                var result = new SyncResult { StartTime = DateTime.Now };

                if (productCodes == null || !productCodes.Any())
                {
                    return ApiResponse<SyncResult>.Error("请选择要同步的商品", "NO_PRODUCTS");
                }

                _logger.LogInformation(
                    "[HBSalesSync] 开始同步商品到HBSales，数量: {Count}",
                    productCodes.Count
                );

                var products = await _context
                    .Db.Queryable<DomesticProduct>()
                    .Where(p => productCodes.Contains(p.ProductCode) && !p.IsDeleted)
                    .ToListAsync();

                if (!products.Any())
                {
                    return ApiResponse<SyncResult>.Error(
                        "未找到有效的商品数据",
                        "NO_VALID_PRODUCTS"
                    );
                }

                var successCount = 0;
                var failCount = 0;
                var addedCount = 0;
                var updatedCount = 0;
                var errors = new List<string>();

                try
                {
                    var hbSalesDb = _hbSalesContext.Db;
                    var syncProductCodes = products
                        .Select(p => p.ProductCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                        .ToList();

                    // 检查HBSales中存在的商品
                    var existingProducts = await hbSalesDb
                        .Queryable<BlazorApp.Shared.Models.HqEntities.CPT_DIC_商品信息字典表>()
                        .Where(x => x.商品编码 != null && syncProductCodes.Contains(x.商品编码))
                        .Select(x => x.商品编码)
                        .ToListAsync();

                    var existingCodes = existingProducts
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                        .ToHashSet();
                    var notExistingCodes = syncProductCodes
                        .Where(code => !existingCodes.Contains(code))
                        .ToList();

                    // 插入不存在的商品
                    if (notExistingCodes.Any())
                    {
                        var productsToInsert = products
                            .Where(p => notExistingCodes.Contains(p.ProductCode))
                            .Select(
                                p => new BlazorApp.Shared.Models.HqEntities.CPT_DIC_商品信息字典表
                                {
                                    HGUID = Guid.NewGuid().ToString(),
                                    商品编码 = p.ProductCode,
                                    中文名称 = p.ProductName,
                                    英文名称 = p.EnglishProductName,
                                    供应商编码 = p.SupplierCode,
                                    HB货号 = p.HBProductNo,
                                    条形码 = p.Barcode,
                                    规格 = p.ProductSpecification,
                                    商品类型 = p.ProductType,
                                    国内价格 = p.DomesticPrice,
                                    进口价格 = p.ImportPrice,
                                    贴牌价格 = p.OEMPrice,
                                    单件装箱数 = p.PackingQuantity,
                                    单件体积 = p.UnitVolume,
                                    中包数量 = p.MiddlePackQuantity,
                                    商品图片 = includeImage ? p.ProductImage : null,
                                    FGC_Creator = "HBweb",
                                    FGC_CreateDate = DateTime.Now,
                                    FGC_LastModifier = "HBweb",
                                    FGC_LastModifyDate = DateTime.Now,
                                    FGC_UpdateHelp = Guid.NewGuid().ToString(),
                                }
                            )
                            .ToList();

                        var insertResult = await hbSalesDb
                            .Insertable(productsToInsert)
                            .ExecuteCommandAsync();
                        addedCount = insertResult;

                        _logger.LogInformation(
                            "[HBSalesSync] 批量插入新商品，插入数量: {Count}",
                            insertResult
                        );
                    }

                    // 批量更新存在的商品
                    var productsToUpdate = products
                        .Where(p => existingCodes.Contains(p.ProductCode))
                        .ToList();

                    if (productsToUpdate.Any())
                    {
                        // 批量更新（使用ExecuteCommandWithSql进行原生SQL批量更新）
                        var updateSqlParts = new List<string>();
                        var sqlParams = new List<SugarParameter>();

                        // 供应商编码
                        var supplierCodes = productsToUpdate
                            .Where(p => !string.IsNullOrWhiteSpace(p.SupplierCode))
                            .ToList();
                        if (supplierCodes.Any())
                        {
                            var cases = string.Join(
                                "",
                                supplierCodes.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN '{SqlLiteral(p.SupplierCode!)}' "
                                )
                            );
                            updateSqlParts.Add(
                                $"[供应商编码] = CASE [商品编码] {cases}ELSE [供应商编码] END"
                            );
                        }

                        // 中文名称
                        var productNames = productsToUpdate
                            .Where(p => !string.IsNullOrWhiteSpace(p.ProductName))
                            .ToList();
                        if (productNames.Any())
                        {
                            var cases = string.Join(
                                "",
                                productNames.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN N'{SqlLiteral(p.ProductName!)}' "
                                )
                            );
                            updateSqlParts.Add(
                                $"[中文名称] = CASE [商品编码] {cases}ELSE [中文名称] END"
                            );
                        }

                        // 英文名称
                        var englishNames = productsToUpdate
                            .Where(p => !string.IsNullOrWhiteSpace(p.EnglishProductName))
                            .ToList();
                        if (englishNames.Any())
                        {
                            var cases = string.Join(
                                "",
                                englishNames.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN N'{SqlLiteral(p.EnglishProductName!)}' "
                                )
                            );
                            updateSqlParts.Add(
                                $"[英文名称] = CASE [商品编码] {cases}ELSE [英文名称] END"
                            );
                        }

                        // HB货号
                        var hbProductNos = productsToUpdate
                            .Where(p => !string.IsNullOrWhiteSpace(p.HBProductNo))
                            .ToList();
                        if (hbProductNos.Any())
                        {
                            var cases = string.Join(
                                "",
                                hbProductNos.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN '{SqlLiteral(p.HBProductNo!)}' "
                                )
                            );
                            updateSqlParts.Add(
                                $"[HB货号] = CASE [商品编码] {cases}ELSE [HB货号] END"
                            );
                        }

                        // 条形码
                        var barcodes = productsToUpdate
                            .Where(p => !string.IsNullOrWhiteSpace(p.Barcode))
                            .ToList();
                        if (barcodes.Any())
                        {
                            var cases = string.Join(
                                "",
                                barcodes.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN '{SqlLiteral(p.Barcode!)}' "
                                )
                            );
                            updateSqlParts.Add(
                                $"[条形码] = CASE [商品编码] {cases}ELSE [条形码] END"
                            );
                        }

                        // 规格
                        var specs = productsToUpdate
                            .Where(p => !string.IsNullOrWhiteSpace(p.ProductSpecification))
                            .ToList();
                        if (specs.Any())
                        {
                            var cases = string.Join(
                                "",
                                specs.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN N'{SqlLiteral(p.ProductSpecification!)}' "
                                )
                            );
                            updateSqlParts.Add($"[规格] = CASE [商品编码] {cases}ELSE [规格] END");
                        }

                        // 商品类型
                        var productTypes = productsToUpdate.Where(p => p.ProductType > 0).ToList();
                        if (productTypes.Any())
                        {
                            var cases = string.Join(
                                "",
                                productTypes.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN {p.ProductType} "
                                )
                            );
                            updateSqlParts.Add(
                                $"[商品类型] = CASE [商品编码] {cases}ELSE [商品类型] END"
                            );
                        }

                        // 国内价格
                        var domesticPrices = productsToUpdate
                            .Where(p => p.DomesticPrice.HasValue)
                            .ToList();
                        if (domesticPrices.Any())
                        {
                            var cases = string.Join(
                                "",
                                domesticPrices.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN {p.DomesticPrice.GetValueOrDefault()} "
                                )
                            );
                            updateSqlParts.Add(
                                $"[国内价格] = CASE [商品编码] {cases}ELSE [国内价格] END"
                            );
                        }

                        // 零售价
                        var oemPrices = productsToUpdate.Where(p => p.OEMPrice.HasValue).ToList();
                        if (oemPrices.Any())
                        {
                            var cases = string.Join(
                                "",
                                oemPrices.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN {p.OEMPrice.GetValueOrDefault()} "
                                )
                            );
                            updateSqlParts.Add(
                                $"[贴牌价格] = CASE [商品编码] {cases}ELSE [贴牌价格] END"
                            );
                        }

                        // 进口价格
                        var importPrices = productsToUpdate
                            .Where(p => p.ImportPrice.HasValue)
                            .ToList();
                        if (importPrices.Any())
                        {
                            var cases = string.Join(
                                "",
                                importPrices.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN {p.ImportPrice.GetValueOrDefault()} "
                                )
                            );
                            updateSqlParts.Add(
                                $"[进口价格] = CASE [商品编码] {cases}ELSE [进口价格] END"
                            );
                        }

                        // 单件装箱数
                        var packingQuantities = productsToUpdate
                            .Where(p => p.PackingQuantity.HasValue)
                            .ToList();
                        if (packingQuantities.Any())
                        {
                            var cases = string.Join(
                                "",
                                packingQuantities.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN {p.PackingQuantity.GetValueOrDefault()} "
                                )
                            );
                            updateSqlParts.Add(
                                $"[单件装箱数] = CASE [商品编码] {cases}ELSE [单件装箱数] END"
                            );
                        }

                        // 单件体积
                        var unitVolumes = productsToUpdate
                            .Where(p => p.UnitVolume.HasValue)
                            .ToList();
                        if (unitVolumes.Any())
                        {
                            var cases = string.Join(
                                "",
                                unitVolumes.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN {p.UnitVolume.GetValueOrDefault()} "
                                )
                            );
                            updateSqlParts.Add(
                                $"[单件体积] = CASE [商品编码] {cases}ELSE [单件体积] END"
                            );
                        }

                        // 中包数量
                        var middlePackQuantities = productsToUpdate
                            .Where(p => p.MiddlePackQuantity.HasValue)
                            .ToList();
                        if (middlePackQuantities.Any())
                        {
                            var cases = string.Join(
                                "",
                                middlePackQuantities.Select(p =>
                                    $"WHEN '{SqlLiteral(p.ProductCode)}' THEN {p.MiddlePackQuantity.GetValueOrDefault()} "
                                )
                            );
                            updateSqlParts.Add(
                                $"[中包数量] = CASE [商品编码] {cases}ELSE [中包数量] END"
                            );
                        }

                        // 商品图片（仅当 includeImage=true 时更新）
                        if (includeImage)
                        {
                            var productImages = productsToUpdate
                                .Where(p => !string.IsNullOrWhiteSpace(p.ProductImage))
                                .ToList();
                            if (productImages.Any())
                            {
                                var cases = string.Join(
                                    "",
                                    productImages.Select(p =>
                                        $"WHEN '{SqlLiteral(p.ProductCode)}' THEN '{SqlLiteral(p.ProductImage!)}' "
                                    )
                                );
                                updateSqlParts.Add(
                                    $"[商品图片] = CASE [商品编码] {cases}ELSE [商品图片] END"
                                );
                            }
                        }

                        // 审计字段更新
                        updateSqlParts.Add(
                            $"[FGC_LastModifier] = 'HBweb', [FGC_LastModifyDate] = GETDATE()"
                        );
                        updateSqlParts.Add(
                            "[HGUID] = CASE WHEN [HGUID] IS NULL OR LTRIM(RTRIM([HGUID])) = '' THEN CONVERT(nvarchar(36), NEWID()) ELSE [HGUID] END"
                        );
                        updateSqlParts.Add(
                            "[FGC_UpdateHelp] = CASE WHEN [FGC_UpdateHelp] IS NULL OR LTRIM(RTRIM([FGC_UpdateHelp])) = '' THEN CONVERT(nvarchar(36), NEWID()) ELSE [FGC_UpdateHelp] END"
                        );

                        if (updateSqlParts.Any())
                        {
                            var codesStr = string.Join(
                                "','",
                                productsToUpdate.Select(p => SqlLiteral(p.ProductCode))
                            );
                            var sql =
                                $"UPDATE [CPT_DIC_商品信息字典表] SET {string.Join(", ", updateSqlParts)} WHERE [商品编码] IN ('{codesStr}')";

                            var updateResult = await hbSalesDb.Ado.ExecuteCommandAsync(
                                sql,
                                sqlParams.ToArray()
                            );

                            updatedCount = updateResult;

                            _logger.LogInformation(
                                "[HBSalesSync] 批量更新完成，更新数量: {Count}",
                                updateResult
                            );
                        }
                    }

                    var setSyncResult = await SyncSetItemsToHBSalesAsync(
                        products,
                        hbSalesDb
                    );
                    errors.AddRange(setSyncResult.Warnings);
                    failCount += setSyncResult.ErrorCount;
                    if (!string.IsNullOrWhiteSpace(setSyncResult.Details))
                    {
                        result.Details = setSyncResult.Details;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[HBSalesSync] 批量同步异常");
                    throw;
                }

                result.AddedCount = addedCount;
                result.UpdatedCount = updatedCount;
                result.ErrorCount = failCount;
                result.IsSuccess = failCount == 0;
                result.Message =
                    failCount == 0
                        ? $"同步成功：新增 {addedCount} 条，更新 {updatedCount} 条"
                        : $"同步完成：新增 {addedCount} 条，更新 {updatedCount} 条，失败 {failCount} 条";
                if (!string.IsNullOrWhiteSpace(result.Details))
                {
                    result.Message = $"{result.Message}；{result.Details}";
                }
                result.EndTime = DateTime.Now;
                result.Duration = result.EndTime - result.StartTime;

                _logger.LogInformation(
                    "[HBSalesSync] 同步完成：成功{Success}条，失败{Fail}条，耗时{Seconds:F1}s",
                    successCount,
                    failCount,
                    result.Duration.TotalSeconds
                );

                return ApiResponse<SyncResult>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[HBSalesSync] 同步商品到HBSales异常");
                return ApiResponse<SyncResult>.Error("同步失败", "SYNC_ERROR");
            }
        }

        private async Task<HBSalesSetItemSyncResult> SyncSetItemsToHBSalesAsync(
            List<DomesticProduct> products,
            SqlSugarScope hbSalesDb
        )
        {
            var result = new HBSalesSetItemSyncResult();
            var setProductCodes = products
                .Where(p => p.ProductType == 1 && !string.IsNullOrWhiteSpace(p.ProductCode))
                .Select(p => p.ProductCode)
                .Distinct()
                .ToList();

            if (!setProductCodes.Any())
            {
                return result;
            }

            var setItems = await _context.Db.Queryable<DomesticSetProduct>()
                .Where(x =>
                    setProductCodes.Contains(x.ProductCode)
                    && x.ProductNo != x.SetProductNo
                    && !x.IsDeleted
                )
                .ToListAsync();

            var setItemProductCodes = setItems.Select(x => x.ProductCode.Trim()).ToHashSet();
            foreach (var productCode in setProductCodes.Where(code => !setItemProductCodes.Contains(code)))
            {
                result.SkippedCount++;
                result.Warnings.Add($"套装商品 {productCode} 没有本地套装明细");
            }

            var validSetItems = new List<DomesticSetProduct>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in setItems)
            {
                if (string.IsNullOrWhiteSpace(item.SetBarcode))
                {
                    result.SkippedCount++;
                    result.Warnings.Add($"套装商品 {item.ProductCode} 的小货号 {item.SetProductNo} 缺少套装条码");
                    continue;
                }

                var key = BuildSetItemKey(item.ProductCode, item.SetBarcode);
                if (!seenKeys.Add(key))
                {
                    result.SkippedCount++;
                    result.Warnings.Add($"套装商品 {item.ProductCode} 的套装条码 {item.SetBarcode} 重复，已跳过重复明细");
                    continue;
                }

                validSetItems.Add(item);
            }

            if (!validSetItems.Any())
            {
                result.RefreshDetails();
                return result;
            }

            var validProductCodes = validSetItems
                .Select(x => x.ProductCode.Trim())
                .Distinct()
                .ToList();

            // 先按商品编码取候选，再用标准化后的“商品编码+条形码”在内存里精确匹配，避免条码空格导致重复新增。
            var existingRows = await hbSalesDb.Queryable<CPT_DIC_商品套装信息表>()
                .Where(x =>
                    x.商品编码 != null
                    && x.条形码 != null
                    && validProductCodes.Contains(x.商品编码)
                )
                .ToListAsync();

            var existingMap = existingRows
                .GroupBy(x => BuildSetItemKey(x.商品编码, x.条形码), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.ID).First(), StringComparer.OrdinalIgnoreCase);

            foreach (var item in validSetItems)
            {
                try
                {
                    var key = BuildSetItemKey(item.ProductCode, item.SetBarcode);
                    if (existingMap.TryGetValue(key, out var existing))
                    {
                        existing.商品小货号 = item.SetProductNo?.Trim();
                        existing.国内价格 = item.DomesticPrice;
                        existing.进口价格 = item.ImportPrice;
                        existing.贴牌价格 = item.OEMPrice;
                        existing.备注 = item.Remarks;
                        existing.使用状态 = 1;

                        var updated = await hbSalesDb.Updateable(existing)
                            .UpdateColumns(x => new
                            {
                                x.商品小货号,
                                x.国内价格,
                                x.进口价格,
                                x.贴牌价格,
                                x.备注,
                                x.使用状态,
                            })
                            .WhereColumns(x => x.ID)
                            .ExecuteCommandAsync();
                        result.UpdatedCount += updated;
                    }
                    else
                    {
                        var newRow = new CPT_DIC_商品套装信息表
                        {
                            HGUID = item.SetProductCode,
                            商品编码 = item.ProductCode.Trim(),
                            商品小货号 = item.SetProductNo?.Trim(),
                            条形码 = item.SetBarcode?.Trim(),
                            国内价格 = item.DomesticPrice,
                            进口价格 = item.ImportPrice,
                            贴牌价格 = item.OEMPrice,
                            备注 = item.Remarks,
                            使用状态 = 1,
                        };

                        var inserted = await hbSalesDb.Insertable(newRow).ExecuteCommandAsync();
                        result.AddedCount += inserted;
                    }
                }
                catch (Exception ex)
                {
                    result.ErrorCount++;
                    result.Warnings.Add($"套装商品 {item.ProductCode} 条码 {item.SetBarcode} 同步失败：{ex.Message}");
                    _logger.LogWarning(
                        ex,
                        "[HBSalesSync] 套装明细同步失败: ProductCode={ProductCode}, SetBarcode={SetBarcode}",
                        item.ProductCode,
                        item.SetBarcode
                    );
                }
            }

            result.RefreshDetails();
            _logger.LogInformation(
                "[HBSalesSync] 套装明细同步完成：新增{Added}，更新{Updated}，跳过{Skipped}，失败{Errors}",
                result.AddedCount,
                result.UpdatedCount,
                result.SkippedCount,
                result.ErrorCount
            );

            return result;
        }

        private static string BuildSetItemKey(string? productCode, string? setBarcode)
        {
            return $"{productCode?.Trim() ?? string.Empty}\u001F{setBarcode?.Trim() ?? string.Empty}";
        }

        private sealed class HBSalesSetItemSyncResult
        {
            public int AddedCount { get; set; }
            public int UpdatedCount { get; set; }
            public int SkippedCount { get; set; }
            public int ErrorCount { get; set; }
            public string Details { get; private set; } = string.Empty;
            public List<string> Warnings { get; } = new();

            public void RefreshDetails()
            {
                if (AddedCount == 0 && UpdatedCount == 0 && SkippedCount == 0 && ErrorCount == 0)
                {
                    Details = string.Empty;
                    return;
                }

                Details = $"套装明细新增 {AddedCount} 条，更新 {UpdatedCount} 条，跳过 {SkippedCount} 条，失败 {ErrorCount} 条";
                if (Warnings.Any())
                {
                    Details = $"{Details}；{string.Join("；", Warnings)}";
                }
            }
        }

        public async Task<ApiResponse<SyncResult>> SendProductsToHqAsync(List<string> productCodes)
        {
            try
            {
                var result = new SyncResult { StartTime = DateTime.Now };

                if (productCodes == null || !productCodes.Any())
                    return ApiResponse<SyncResult>.Error("请选择要发送的商品", "NO_PRODUCTS");

                _logger.LogInformation(
                    "[SendToHq] 开始发送商品到HQ，数量: {Count}",
                    productCodes.Count
                );

                var hbSalesSyncResult = await SyncSelectedToHBSalesAsync(
                    productCodes,
                    includeImage: true
                );
                if (!hbSalesSyncResult.Success)
                {
                    return ApiResponse<SyncResult>.Error(
                        $"发送HQ前同步HBSales失败: {hbSalesSyncResult.Message}",
                        hbSalesSyncResult.ErrorCode ?? "HBSALES_SYNC_FAILED",
                        hbSalesSyncResult.Details
                    );
                }

                var products = await _context
                    .Db.Queryable<DomesticProduct>()
                    .Where(p => productCodes.Contains(p.ProductCode) && !p.IsDeleted)
                    .ToListAsync();

                var validProducts = products
                    .Where(p => p.ImportPrice.HasValue && p.OEMPrice.HasValue)
                    .ToList();

                if (!validProducts.Any())
                    return ApiResponse<SyncResult>.Error(
                        "选中的商品中没有同时具备进口价格和零售价的商品",
                        "NO_VALID_PRODUCTS"
                    );

                var hqDb = _hqContext.Db;
                var successCount = 0;
                var failCount = 0;
                var errors = new List<string>();
                var productCount = 0;
                var retailPriceCount = 0;

                // === 1. 写入 DIC_商品信息字典表 ===
                var syncProductCodes = validProducts
                    .Select(p => p.ProductCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .ToList();
                var existingProducts =
                    await hqDb.Queryable<BlazorApp.Shared.Models.HqEntities.DIC_商品信息字典表>()
                        .Where(x => x.H商品编码 != null && syncProductCodes.Contains(x.H商品编码))
                        .Select(x => x.H商品编码)
                        .ToListAsync();
                var existingCodes = existingProducts
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .ToHashSet();
                var notExistingProducts = validProducts
                    .Where(p => !existingCodes.Contains(p.ProductCode))
                    .ToList();
                var toUpdateProducts = validProducts
                    .Where(p => existingCodes.Contains(p.ProductCode))
                    .ToList();

                // 插入新商品
                if (notExistingProducts.Any())
                {
                    var insertList = notExistingProducts
                        .Select(p => new BlazorApp.Shared.Models.HqEntities.DIC_商品信息字典表
                        {
                            HGUID = Guid.NewGuid().ToString(),
                            H商品编码 = p.ProductCode,
                            H货号 = p.HBProductNo,
                            H主条形码 = p.Barcode,
                            H商品名称 = !string.IsNullOrWhiteSpace(p.EnglishProductName)
                                ? p.EnglishProductName
                                : p.ProductName,
                            H大写名称 = p.ProductName,
                            H商品类型 = 0,
                            H规格 = p.ProductSpecification,
                            H单位 = "个",
                            H进货价 = p.ImportPrice ?? 0,
                            H零售价 = p.OEMPrice ?? 0,
                            H是否自动定价 = false,
                            H商品图片 = p.ProductImage,
                            中包数量 = p.MiddlePackQuantity ?? 0,
                            H使用状态 = p.IsActive,
                            H是否特殊商品 = false,
                            H供货商编码 = "200",
                            CBP供应商编码 = p.SupplierCode,
                            FGC_Creator = "HBweb",
                            FGC_CreateDate = DateTime.Now,
                            FGC_LastModifier = "HBweb",
                            FGC_LastModifyDate = DateTime.Now,
                        })
                        .ToList();

                    await hqDb.Insertable(insertList)
                        .IgnoreColumns(x => x.ID)
                        .ExecuteCommandAsync();
                    productCount += insertList.Count;
                    _logger.LogInformation("[SendToHq] 新增商品字典 {Count} 条", insertList.Count);
                }

                // 更新已有商品
                if (toUpdateProducts.Any())
                {
                    foreach (var p in toUpdateProducts)
                    {
                        try
                        {
                            var updateSql = new List<string>();
                            if (!string.IsNullOrWhiteSpace(p.HBProductNo))
                                updateSql.Add($"[H货号] = N'{p.HBProductNo.Replace("'", "''")}'");
                            if (!string.IsNullOrWhiteSpace(p.Barcode))
                                updateSql.Add($"[H主条形码] = N'{p.Barcode.Replace("'", "''")}'");

                            var displayName = !string.IsNullOrWhiteSpace(p.EnglishProductName)
                                ? p.EnglishProductName
                                : p.ProductName;
                            if (!string.IsNullOrWhiteSpace(displayName))
                                updateSql.Add($"[H商品名称] = N'{displayName.Replace("'", "''")}'");
                            if (!string.IsNullOrWhiteSpace(p.ProductName))
                                updateSql.Add(
                                    $"[H大写名称] = N'{p.ProductName.Replace("'", "''")}'"
                                );

                            if (p.ImportPrice.HasValue)
                                updateSql.Add($"[H进货价] = {p.ImportPrice.Value}");
                            if (p.OEMPrice.HasValue)
                                updateSql.Add($"[H零售价] = {p.OEMPrice.Value}");
                            if (!string.IsNullOrWhiteSpace(p.ProductImage))
                                updateSql.Add(
                                    $"[H商品图片] = N'{p.ProductImage.Replace("'", "''")}'"
                                );
                            if (p.MiddlePackQuantity.HasValue)
                                updateSql.Add($"[中包数量] = {p.MiddlePackQuantity.Value}");

                            if (!string.IsNullOrWhiteSpace(p.SupplierCode))
                                updateSql.Add($"[CBP供应商编码] = N'{p.SupplierCode.Replace("'", "''")}'");
                            updateSql.Add("[H供货商编码] = '200'");
                            updateSql.Add($"[FGC_LastModifier] = 'HBweb'");
                            updateSql.Add(
                                $"[FGC_LastModifyDate] = '{DateTime.Now:yyyy-MM-dd HH:mm:ss}'"
                            );

                            if (updateSql.Any())
                            {
                                var sql =
                                    $"UPDATE [DIC_商品信息字典表] SET {string.Join(", ", updateSql)} WHERE [H商品编码] = N'{p.ProductCode.Replace("'", "''")}'";
                                await hqDb.Ado.ExecuteCommandAsync(sql);
                                productCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            failCount++;
                            errors.Add($"更新商品 {p.ProductCode} 失败: {ex.Message}");
                        }
                    }
                    _logger.LogInformation(
                        "[SendToHq] 更新商品字典 {Count} 条",
                        toUpdateProducts.Count
                    );
                }

                // === 2. 查询所有启用分店 ===
                var activeStores =
                    (
                        await hqDb.Queryable<BlazorApp.Shared.Models.HqEntities.HqBranch>()
                            .Select(b => b.BranchCode)
                            .ToListAsync()
                    )
                        .Where(storeCode => !string.IsNullOrWhiteSpace(storeCode))
                        .Select(storeCode => storeCode!)
                        .Distinct()
                        .ToList();

                var now = DateTime.Now;
                var nowStr = now.ToString("yyyy-MM-dd HH:mm:ss");

                if (!activeStores.Any())
                {
                    _logger.LogWarning("[SendToHq] 未找到启用的分店，跳过零售价写入");
                }
                else
                {
                    // === 3. 批量写入 DIC_商品零售价表（按分店） ===
                    var syncProductCodesForRetail = validProducts
                        .Select(p => p.ProductCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                        .ToList();
                    var existingRetails =
                        await hqDb.Queryable<BlazorApp.Shared.Models.HqEntities.DIC_商品零售价表>()
                            .Where(r =>
                                r.H商品编码 != null
                                && syncProductCodesForRetail.Contains(r.H商品编码)
                                && r.H分店代码 != null
                                && activeStores.Contains(r.H分店代码)
                            )
                            .Select(r => new { r.H分店代码, r.H商品编码 })
                            .ToListAsync();
                    var existingRetailSet = new HashSet<string>(
                        existingRetails.Select(e => $"{e.H分店代码}|{e.H商品编码}")
                    );

                    var toInsertList =
                        new List<BlazorApp.Shared.Models.HqEntities.DIC_商品零售价表>();
                    var updateSqlParts = new List<string>();

                    foreach (var p in validProducts)
                    {
                        foreach (var storeCode in activeStores)
                        {
                            var key = $"{storeCode}|{p.ProductCode}";
                            if (existingRetailSet.Contains(key))
                            {
                                var safeStore = storeCode.Replace("'", "''");
                                var safeCode = p.ProductCode.Replace("'", "''");
                                var safeStoreProduct = (storeCode + p.ProductCode).Replace(
                                    "'",
                                    "''"
                                );
                                var safeStoreSupplier = (storeCode + "200").Replace("'", "''");
                                updateSqlParts.Add(
                                    $"UPDATE [DIC_商品零售价表] SET [H进货价] = {p.ImportPrice ?? 0}, [H分店零售价] = {p.OEMPrice ?? 0}, [H分店商品编码] = N'{safeStoreProduct}', [H供应商编码] = '200', [H分店供应商编码] = '{safeStoreSupplier}', [FGC_LastModifier] = 'HBweb', [FGC_LastModifyDate] = '{nowStr}' WHERE [H分店代码] = N'{safeStore}' AND [H商品编码] = N'{safeCode}'"
                                );
                            }
                            else
                            {
                                toInsertList.Add(
                                    new BlazorApp.Shared.Models.HqEntities.DIC_商品零售价表
                                    {
                                        HGUID = Guid.NewGuid().ToString(),
                                        H分店代码 = storeCode,
                                        H商品编码 = p.ProductCode,
                                        H分店商品编码 = storeCode + p.ProductCode,
                                        H供应商编码 = "200",
                                        H分店供应商编码 = storeCode + "200",
                                        H进货价 = p.ImportPrice ?? 0,
                                        H分店零售价 = p.OEMPrice ?? 0,
                                        H库存 = 0,
                                        H库存金额 = 0,
                                        H使用状态 = true,
                                        H是否自动定价 = false,
                                        FGC_Creator = "HBweb",
                                        FGC_CreateDate = now,
                                        FGC_LastModifier = "HBweb",
                                        FGC_LastModifyDate = now,
                                    }
                                );
                            }
                        }
                        successCount++;
                    }

                    if (toInsertList.Any())
                    {
                        await hqDb.Insertable(toInsertList)
                            .IgnoreColumns(x => x.ID)
                            .ExecuteCommandAsync();
                        _logger.LogInformation(
                            "[SendToHq] 批量新增零售价 {Count} 条",
                            toInsertList.Count
                        );
                    }

                    if (updateSqlParts.Any())
                    {
                        var batchUpdateSql = string.Join(";", updateSqlParts);
                        await hqDb.Ado.ExecuteCommandAsync(batchUpdateSql);
                        _logger.LogInformation(
                            "[SendToHq] 批量更新零售价 {Count} 条",
                            updateSqlParts.Count
                        );
                    }

                    retailPriceCount = toInsertList.Count + updateSqlParts.Count;
                }

                // === 4. 批量写入 CBP_DIC_商品库存表 ===
                var inventoryProductCodes = validProducts
                    .Select(p => p.ProductCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!)
                    .ToList();
                var existingInventories =
                    await hqDb.Queryable<BlazorApp.Shared.Models.HqEntities.CBP_DIC_商品库存表>()
                        .Where(x => x.H商品编码 != null && inventoryProductCodes.Contains(x.H商品编码))
                        .Select(x => x.H商品编码)
                        .ToListAsync();
                var existingInventorySet = new HashSet<string>(
                    existingInventories
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                );

                var toInsertInventories =
                    new List<BlazorApp.Shared.Models.HqEntities.CBP_DIC_商品库存表>();
                var inventoryUpdateSqlParts = new List<string>();

                foreach (var p in validProducts)
                {
                    if (existingInventorySet.Contains(p.ProductCode))
                    {
                        var safeCode = p.ProductCode.Replace("'", "''");
                        var invUpdateSql =
                            $"UPDATE [CBP_DIC_商品库存表] SET [H国内价格] = {(p.DomesticPrice.HasValue ? p.DomesticPrice.Value.ToString() : "NULL")}, [H贴牌价格] = {(p.OEMPrice.HasValue ? p.OEMPrice.Value.ToString() : "NULL")}, [H进口价格] = {(p.ImportPrice.HasValue ? p.ImportPrice.Value.ToString() : "NULL")}, [FGC_LastModifier] = 'HBweb', [FGC_LastModifyDate] = '{nowStr}' WHERE [H商品编码] = N'{safeCode}'";
                        inventoryUpdateSqlParts.Add(invUpdateSql);
                    }
                    else
                    {
                        toInsertInventories.Add(
                            new BlazorApp.Shared.Models.HqEntities.CBP_DIC_商品库存表
                            {
                                HGUID = Guid.NewGuid().ToString(),
                                H商品编码 = p.ProductCode,
                                H国内价格 = p.DomesticPrice,
                                H贴牌价格 = p.OEMPrice,
                                H进口价格 = p.ImportPrice,
                                H库存 = 0,
                                H最小订货量 = 0,
                                H库存金额 = 0,
                                H库存预警数 = 0,
                                H使用状态 = 1,
                                FGC_Creator = "HBweb",
                                FGC_CreateDate = now,
                                FGC_LastModifier = "HBweb",
                                FGC_LastModifyDate = now,
                            }
                        );
                    }
                }

                var inventoryCount = 0;
                if (toInsertInventories.Any())
                {
                    inventoryCount += await hqDb.Insertable(toInsertInventories)
                        .IgnoreColumns(x => x.ID).ExecuteCommandAsync();
                    _logger.LogInformation(
                        "[SendToHq] 批量新增库存 {Count} 条",
                        toInsertInventories.Count
                    );
                }
                if (inventoryUpdateSqlParts.Any())
                {
                    var batchInvUpdateSql = string.Join(";", inventoryUpdateSqlParts);
                    inventoryCount += await hqDb.Ado.ExecuteCommandAsync(batchInvUpdateSql);
                    _logger.LogInformation(
                        "[SendToHq] 批量更新库存 {Count} 条",
                        inventoryUpdateSqlParts.Count
                    );
                }

                result.EndTime = DateTime.Now;
                var hbSalesMessage =
                    hbSalesSyncResult.Data == null
                        ? hbSalesSyncResult.Message
                        : $"新增 {hbSalesSyncResult.Data.AddedCount}，更新 {hbSalesSyncResult.Data.UpdatedCount}";
                result.Message =
                    $"发送完成：已先同步HBSales（{hbSalesMessage}）；HQ写入：商品字典 {productCount} 条，零售价 {retailPriceCount} 条（{activeStores.Count} 个分店 × {validProducts.Count} 个商品），库存 {inventoryCount} 条，失败 {failCount} 条";
                result.AddedCount = productCount;
                result.UpdatedCount = retailPriceCount;

                _logger.LogInformation("[SendToHq] {Message}", result.Message);

                return failCount > 0 && successCount > 0
                        ? ApiResponse<SyncResult>.OK(result, result.Message)
                    : failCount == 0 ? ApiResponse<SyncResult>.OK(result, result.Message)
                    : ApiResponse<SyncResult>.Error(string.Join("; ", errors), "PARTIAL_FAILURE");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SendToHq] 发送商品到HQ失败");
                return ApiResponse<SyncResult>.Error($"发送失败: {ex.Message}", "SEND_TO_HQ_ERROR");
            }
        }

        private static string SqlLiteral(string value)
        {
            // 原生批量 CASE SQL 使用字面量匹配商品编码，必须统一转义单引号。
            return value.Replace("'", "''");
        }
    }
}
