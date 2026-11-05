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
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 国内商品管理服务
    /// </summary>
    public class DomesticProductService : IDomesticProductService
    {
        private readonly SqlSugarContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<DomesticProductService> _logger;
        private readonly ItemBarcodeService _itemBarcodeService;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;

        public DomesticProductService(
            SqlSugarContext context,
            IMapper mapper,
            ILogger<DomesticProductService> logger,
            ItemBarcodeService itemBarcodeService,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService
        )
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
            _itemBarcodeService = itemBarcodeService;
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

        /// <summary>
        /// 获取国内商品分页列表
        /// </summary>
        public async Task<ApiResponse<PagedResult<DomesticProductDto>>> GetDomesticProductsAsync(
            DomesticProductQueryDto query
        )
        {
            try
            {
                var db = _context.Db;

                // 构建查询
                var productQuery = db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => !p.IsDeleted);

                // 应用搜索条件
                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    productQuery = productQuery.Where(
                        (p, s) =>
                            (p.ProductName != null && p.ProductName.Contains(query.Search))
                            || (p.HBProductNo != null && p.HBProductNo.Contains(query.Search))
                            || (p.Barcode != null && p.Barcode.Contains(query.Search))
                            || (
                                p.EnglishProductName != null
                                && p.EnglishProductName.Contains(query.Search)
                            )
                    );
                }

                // 供应商编码筛选
                if (!string.IsNullOrWhiteSpace(query.SupplierCode))
                {
                    productQuery = productQuery.Where(
                        (p, s) =>
                            p.SupplierCode != null && p.SupplierCode.Contains(query.SupplierCode)
                    );
                }

                // 供应商名称筛选
                if (!string.IsNullOrWhiteSpace(query.SupplierName))
                {
                    // 没有「查看国内供应商名称」权限的账号按名称筛选时筛不出结果，不能借筛选试探名称。
                    var supplierNameFilter = DomesticSupplierInfoMasking.GuardNameKeyword(query.SupplierName);
                    productQuery = productQuery.Where(
                        (p, s) =>
                            s.SupplierName != null && s.SupplierName.Contains(supplierNameFilter)
                    );
                }

                // 商品名称筛选
                if (!string.IsNullOrWhiteSpace(query.ProductName))
                {
                    productQuery = productQuery.Where(
                        (p, s) => p.ProductName != null && p.ProductName.Contains(query.ProductName)
                    );
                }

                // 商品货号筛选
                if (!string.IsNullOrWhiteSpace(query.ProductNo))
                {
                    productQuery = productQuery.Where(
                        (p, s) => p.HBProductNo != null && p.HBProductNo.Contains(query.ProductNo)
                    );
                }

                if (query.ProductType.HasValue)
                {
                    productQuery = productQuery.Where(
                        (p, s) => p.ProductType == query.ProductType.Value
                    );
                }

                if (query.IsActive.HasValue)
                {
                    productQuery = productQuery.Where((p, s) => p.IsActive == query.IsActive.Value);
                }

                if (query.MinPrice.HasValue)
                {
                    productQuery = productQuery.Where(
                        (p, s) => p.DomesticPrice >= query.MinPrice.Value
                    );
                }

                if (query.MaxPrice.HasValue)
                {
                    productQuery = productQuery.Where(
                        (p, s) => p.DomesticPrice <= query.MaxPrice.Value
                    );
                }

                // 应用排序
                if (!string.IsNullOrEmpty(query.SortBy))
                {
                    var isDescending =
                        !string.IsNullOrEmpty(query.SortDirection)
                        && query.SortDirection.ToLower() == "desc";

                    productQuery = query.SortBy.ToLower() switch
                    {
                        "productname" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.ProductName)
                            : productQuery.OrderBy((p, s) => p.ProductName),
                        "hbproductno" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.HBProductNo)
                            : productQuery.OrderBy((p, s) => p.HBProductNo),
                        "suppliercode" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.SupplierCode)
                            : productQuery.OrderBy((p, s) => p.SupplierCode),
                        // 按被遮掉的国内供应商名称排序会泄露相对顺序，无权限时改按供应商代码排。
                        "suppliername" when !DomesticSupplierInfoMasking.IsNameSearchAllowed => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.SupplierCode)
                            : productQuery.OrderBy((p, s) => p.SupplierCode),
                        "suppliername" => isDescending
                            ? productQuery.OrderByDescending((p, s) => s.SupplierName)
                            : productQuery.OrderBy((p, s) => s.SupplierName),
                        "producttype" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.ProductType)
                            : productQuery.OrderBy((p, s) => p.ProductType),
                        "packingquantity" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.PackingQuantity)
                            : productQuery.OrderBy((p, s) => p.PackingQuantity),
                        "unitvolume" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.UnitVolume)
                            : productQuery.OrderBy((p, s) => p.UnitVolume),
                        "domesticprice" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.DomesticPrice)
                            : productQuery.OrderBy((p, s) => p.DomesticPrice),
                        "importprice" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.ImportPrice)
                            : productQuery.OrderBy((p, s) => p.ImportPrice),
                        "oemprice" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.OEMPrice)
                            : productQuery.OrderBy((p, s) => p.OEMPrice),
                        "isactive" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.IsActive)
                            : productQuery.OrderBy((p, s) => p.IsActive),
                        "setquantity" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.ProductType == 1 ? 1 : 0) // 套装商品优先，具体数量需要后续计算
                            : productQuery.OrderBy((p, s) => p.ProductType == 1 ? 1 : 0),
                        "updatedat" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.UpdatedAt)
                            : productQuery.OrderBy((p, s) => p.UpdatedAt),
                        "createdat" => isDescending
                            ? productQuery.OrderByDescending((p, s) => p.CreatedAt)
                            : productQuery.OrderBy((p, s) => p.CreatedAt),
                        _ => productQuery.OrderByDescending((p, s) => p.UpdatedAt),
                    };
                }
                else
                {
                    productQuery = productQuery.OrderByDescending((p, s) => p.CreatedAt);
                }

                // 获取总数
                var totalCount = await productQuery.CountAsync();

                // 分页查询基础商品信息
                var products = await productQuery
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
                                CreatedBy = p.CreatedBy,
                                UpdatedBy = p.UpdatedBy,
                                Supplier = new ChinaSupplier { SupplierName = s.SupplierName },
                            }
                    )
                    .Skip(query.Skip)
                    .Take(query.Take)
                    .ToListAsync();

                // 使用导航查询加载套装信息（仅对套装商品类型进行查询）
                if (products.Any(p => p.ProductType == 1 || p.ProductType == 2)) // 套装商品类型为1
                {
                    var setProductCodes = products
                        .Where(p => p.ProductType == 1 || p.ProductType == 2)
                        .Select(p => p.ProductCode)
                        .ToList();

                    // 批量查询套装信息
                    var setProducts = await db.Queryable<DomesticSetProduct>()
                        .Where(sp => setProductCodes.Contains(sp.ProductCode))
                        .ToListAsync();

                    // 将套装信息分配给相应的商品（使用Model的属性名，让映射器处理DTO转换）
                    foreach (var product in products.Where(p => p.ProductType == 1))
                    {
                        product.DomesticSetProducts = setProducts
                            .Where(sp => sp.ProductCode == product.ProductCode)
                            .ToList();
                    }
                }

                // 映射到DTO
                var productDtos = _mapper.Map<List<DomesticProductDto>>(products);

                var result = new PagedResult<DomesticProductDto>
                {
                    Items = productDtos,
                    Total = totalCount,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<DomesticProductDto>>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取国内商品列表失败");
                return ApiResponse<PagedResult<DomesticProductDto>>.Error(
                    "获取国内商品列表失败",
                    "GET_PRODUCTS_ERROR"
                );
            }
        }

        /// <summary>
        /// 获取国内商品分页列表（高级过滤）
        /// </summary>
        public async Task<
            ApiResponse<PagedResult<DomesticProductDto>>
        > GetDomesticProductsAdvancedAsync(DomesticProductAdvancedQueryDto query)
        {
            try
            {
                var db = _context.Db;

                // 构建基础查询
                var productQuery = db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => !p.IsDeleted);

                // 应用全局搜索
                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    productQuery = productQuery.Where(
                        (p, s) =>
                            (p.ProductName != null && p.ProductName.Contains(query.Search))
                            || (p.HBProductNo != null && p.HBProductNo.Contains(query.Search))
                            || (p.Barcode != null && p.Barcode.Contains(query.Search))
                            || (
                                p.EnglishProductName != null
                                && p.EnglishProductName.Contains(query.Search)
                            )
                    );
                }

                // 应用快速过滤条件
                if (!string.IsNullOrWhiteSpace(query.SupplierCode))
                {
                    productQuery = productQuery.Where(
                        (p, s) =>
                            p.SupplierCode != null && p.SupplierCode.Contains(query.SupplierCode)
                    );
                }

                if (query.ProductType.HasValue)
                {
                    productQuery = productQuery.Where(
                        (p, s) => p.ProductType == query.ProductType.Value
                    );
                }

                if (query.IsActive.HasValue)
                {
                    productQuery = productQuery.Where((p, s) => p.IsActive == query.IsActive.Value);
                }

                // 应用高级过滤条件
                if (query.FilterGroup != null)
                {
                    productQuery = ApplyFilterGroup(productQuery, query.FilterGroup);
                }

                // 应用排序
                if (!string.IsNullOrEmpty(query.SortBy))
                {
                    var isDescending =
                        !string.IsNullOrEmpty(query.SortDirection)
                        && query.SortDirection.ToLower() == "desc";
                    productQuery = ApplySqlSorting(productQuery, query.SortBy, isDescending);
                }
                else
                {
                    productQuery = productQuery.OrderByDescending((p, s) => p.CreatedAt);
                }

                // 获取总数
                var totalCount = await productQuery.CountAsync();

                // 分页查询基础商品信息
                var products = await productQuery
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
                                CreatedBy = p.CreatedBy,
                                UpdatedBy = p.UpdatedBy,
                                Supplier = new ChinaSupplier { SupplierName = s.SupplierName },
                            }
                    )
                    .Skip(query.Skip)
                    .Take(query.Take)
                    .ToListAsync();

                // 使用导航查询加载套装信息（仅对套装商品类型进行查询）
                if (products.Any(p => p.ProductType == 1 || p.ProductType == 2))
                {
                    var setProductCodes = products
                        .Where(p => p.ProductType == 1 || p.ProductType == 2)
                        .Select(p => p.ProductCode)
                        .ToList();

                    // 批量查询套装信息
                    var setProducts = await db.Queryable<DomesticSetProduct>()
                        .Where(sp => setProductCodes.Contains(sp.ProductCode))
                        .ToListAsync();

                    // 将套装信息分配给相应的商品（使用Model的属性名，让映射器处理DTO转换）
                    foreach (var product in products.Where(p => p.ProductType == 1))
                    {
                        product.DomesticSetProducts = setProducts
                            .Where(sp => sp.ProductCode == product.ProductCode)
                            .ToList();
                    }
                }

                // 映射到DTO
                var productDtos = _mapper.Map<List<DomesticProductDto>>(products);

                var result = new PagedResult<DomesticProductDto>
                {
                    Items = productDtos,
                    Total = totalCount,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<DomesticProductDto>>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取国内商品列表失败（高级过滤）");
                return ApiResponse<PagedResult<DomesticProductDto>>.Error(
                    "获取国内商品列表失败",
                    "GET_PRODUCTS_ADVANCED_ERROR"
                );
            }
        }

        /// <summary>
        /// 获取字段信息（用于构建过滤界面）
        /// </summary>
        public async Task<ApiResponse<List<BlazorApp.Shared.DTOs.FieldInfo>>> GetFieldInfoAsync()
        {
            try
            {
                List<BlazorApp.Shared.DTOs.FieldInfo> fieldInfos =
                    QueryHelper.GetDomesticProductFields();
                return await Task.FromResult(
                    ApiResponse<List<BlazorApp.Shared.DTOs.FieldInfo>>.OK(fieldInfos)
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取字段信息失败");
                return ApiResponse<List<BlazorApp.Shared.DTOs.FieldInfo>>.Error(
                    "获取字段信息失败",
                    "GET_FIELD_INFO_ERROR"
                );
            }
        }

        /// <summary>
        /// 应用过滤组条件到SQL查询（重新设计）
        /// 新逻辑：
        /// 1. 同一字段内的多个条件：按照字段组的LogicalOperator拼接
        /// 2. 不同字段间的条件组：用AND连接，每个字段组加括号
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyFilterGroup(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            FilterGroup filterGroup
        )
        {
            if (filterGroup == null)
                return queryable;

            // 处理直接条件（同一字段内的条件）
            if (filterGroup.Conditions?.Any() == true)
            {
                Console.WriteLine(
                    $"[DEBUG] 处理字段 {filterGroup.FieldName} 的直接条件，逻辑: {filterGroup.LogicalOperator}，条件数: {filterGroup.Conditions.Count}"
                );
                queryable = ApplyConditionsWithLogic(
                    queryable,
                    filterGroup.Conditions,
                    filterGroup.LogicalOperator
                );
            }

            // 处理子分组（不同字段的条件组，固定用AND连接）
            if (filterGroup.SubGroups?.Any() == true)
            {
                Console.WriteLine($"[DEBUG] 处理子分组，数量: {filterGroup.SubGroups.Count}");

                // 不同字段间固定使用AND逻辑，每个子分组相当于加了括号
                foreach (var subGroup in filterGroup.SubGroups)
                {
                    Console.WriteLine($"[DEBUG] 处理子分组字段: {subGroup.FieldName}");
                    queryable = ApplyFilterGroup(queryable, subGroup);
                }
            }

            return queryable;
        }

        /// <summary>
        /// 应用子分组条件（重新设计，简化逻辑）
        /// 新设计：不同字段间固定使用AND连接，每个字段组内部按照自己的LogicalOperator处理
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplySubGroupsWithLogic(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<FilterGroup> subGroups,
            LogicalOperator groupLogicalOperator
        )
        {
            if (subGroups == null || !subGroups.Any())
                return queryable;

            // 新设计：不同字段间固定使用AND连接
            // 每个子分组（字段组）递归处理，相当于加了括号
            foreach (var subGroup in subGroups)
            {
                Console.WriteLine(
                    $"[DEBUG] 应用字段组: {subGroup.FieldName}, 逻辑: {subGroup.LogicalOperator}"
                );
                queryable = ApplyFilterGroup(queryable, subGroup);
            }

            return queryable;
        }

        /// <summary>
        /// 应用多个子分组的OR逻辑，确保每个子分组的条件用括号包起来
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplySubGroupsWithOrLogic(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<FilterGroup> subGroups
        )
        {
            // 收集每个子分组的所有条件
            var allGroupConditions = new List<List<FilterCondition>>();

            foreach (var subGroup in subGroups)
            {
                var groupConditions = CollectAllConditions(subGroup);
                if (groupConditions.Any())
                {
                    allGroupConditions.Add(groupConditions);
                }
            }

            if (!allGroupConditions.Any())
                return queryable;

            // 构建OR逻辑：(Group1的条件) OR (Group2的条件) OR (Group3的条件)
            return ApplyGroupedOrConditions(queryable, allGroupConditions);
        }

        /// <summary>
        /// 递归收集过滤组中的所有条件
        /// </summary>
        private List<FilterCondition> CollectAllConditions(FilterGroup filterGroup)
        {
            var conditions = new List<FilterCondition>();

            // 收集直接条件
            if (filterGroup.Conditions?.Any() == true)
            {
                conditions.AddRange(filterGroup.Conditions);
            }

            // 递归收集子分组的条件
            if (filterGroup.SubGroups?.Any() == true)
            {
                foreach (var subGroup in filterGroup.SubGroups)
                {
                    conditions.AddRange(CollectAllConditions(subGroup));
                }
            }

            return conditions;
        }

        /// <summary>
        /// 应用分组的OR条件，每个组的条件用括号包起来
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyGroupedOrConditions(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<List<FilterCondition>> groupedConditions
        )
        {
            if (!groupedConditions.Any())
                return queryable;

            if (groupedConditions.Count == 1)
            {
                // 单个组，直接应用
                return ApplyConditionsWithLogic(
                    queryable,
                    groupedConditions[0],
                    LogicalOperator.And
                );
            }

            // 多个组需要用OR连接，每个组内用AND连接
            // 构建形如：WHERE (Group1条件AND连接) OR (Group2条件AND连接) OR (Group3条件AND连接) 的查询

            // 使用SqlSugar的WhereIF和表达式构建
            return ApplyComplexGroupedOrLogic(queryable, groupedConditions);
        }

        /// <summary>
        /// 应用复杂的分组OR逻辑
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyComplexGroupedOrLogic(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<List<FilterCondition>> groupedConditions
        )
        {
            // 为了确保正确的SQL括号优先级，我们需要构建Expression表达式
            // 形如：WHERE (条件1 AND 条件2) OR (条件3 AND 条件4) OR (条件5 AND 条件6)

            try
            {
                // 构建每个组的AND条件表达式
                var groupExpressions =
                    new List<Expression<Func<DomesticProduct, ChinaSupplier, bool>>>();

                foreach (var group in groupedConditions)
                {
                    var groupExpression = BuildGroupAndExpression(group);
                    if (groupExpression != null)
                    {
                        groupExpressions.Add(groupExpression);
                    }
                }

                if (!groupExpressions.Any())
                    return queryable;

                if (groupExpressions.Count == 1)
                {
                    // 单个表达式直接应用
                    return queryable.Where(groupExpressions[0]);
                }

                // 多个表达式用OR连接
                var combinedExpression = CombineExpressionsWithOr(groupExpressions);
                if (combinedExpression != null)
                {
                    return queryable.Where(combinedExpression);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "构建复杂OR表达式失败，使用简化处理");
            }

            // 如果Expression构建失败，使用简化处理
            return ApplySimplifiedGroupedOrConditions(queryable, groupedConditions);
        }

        /// <summary>
        /// 构建单个组的AND表达式
        /// </summary>
        private Expression<Func<DomesticProduct, ChinaSupplier, bool>>? BuildGroupAndExpression(
            List<FilterCondition> conditions
        )
        {
            if (!conditions.Any())
                return null;

            try
            {
                // 参数表达式
                var productParam = Expression.Parameter(typeof(DomesticProduct), "p");
                var supplierParam = Expression.Parameter(typeof(ChinaSupplier), "s");

                Expression? combinedExpression = null;

                foreach (var condition in conditions)
                {
                    var conditionExpression = BuildSingleConditionExpression(
                        productParam,
                        supplierParam,
                        condition
                    );
                    if (conditionExpression != null)
                    {
                        combinedExpression =
                            combinedExpression == null
                                ? conditionExpression
                                : Expression.AndAlso(combinedExpression, conditionExpression);
                    }
                }

                if (combinedExpression != null)
                {
                    return Expression.Lambda<Func<DomesticProduct, ChinaSupplier, bool>>(
                        combinedExpression,
                        productParam,
                        supplierParam
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "构建组AND表达式失败");
            }

            return null;
        }

        /// <summary>
        /// 构建单个条件的表达式
        /// </summary>
        private Expression? BuildSingleConditionExpression(
            ParameterExpression productParam,
            ParameterExpression supplierParam,
            FilterCondition condition
        )
        {
            try
            {
                var value = condition.Value?.ToString();
                if (string.IsNullOrEmpty(value))
                    return null;

                return condition.FieldName switch
                {
                    "ProductName" => BuildStringConditionExpression(
                        productParam,
                        "ProductName",
                        condition.Operator,
                        value
                    ),
                    "HBProductNo" => BuildStringConditionExpression(
                        productParam,
                        "HBProductNo",
                        condition.Operator,
                        value
                    ),
                    "SupplierCode" => BuildStringConditionExpression(
                        productParam,
                        "SupplierCode",
                        condition.Operator,
                        value
                    ),
                    "Supplier.SupplierName" => BuildStringConditionExpression(
                        supplierParam,
                        "SupplierName",
                        condition.Operator,
                        value
                    ),
                    "ProductType" when int.TryParse(value, out int intValue) =>
                        BuildIntConditionExpression(
                            productParam,
                            "ProductType",
                            condition.Operator,
                            intValue
                        ),
                    "IsActive" when bool.TryParse(value, out bool boolValue) =>
                        BuildBoolConditionExpression(
                            productParam,
                            "IsActive",
                            condition.Operator,
                            boolValue
                        ),
                    _ => null,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "构建单个条件表达式失败: {FieldName}", condition.FieldName);
                return null;
            }
        }

        /// <summary>
        /// 构建字符串条件表达式
        /// </summary>
        private Expression? BuildStringConditionExpression(
            ParameterExpression param,
            string propertyName,
            FilterOperator op,
            string value
        )
        {
            var property = Expression.Property(param, propertyName);
            var constantValue = Expression.Constant(value);

            return op switch
            {
                FilterOperator.Contains => Expression.Call(
                    property,
                    "Contains",
                    null,
                    constantValue
                ),
                FilterOperator.Equals => Expression.Equal(property, constantValue),
                FilterOperator.StartsWith => Expression.Call(
                    property,
                    "StartsWith",
                    null,
                    constantValue
                ),
                FilterOperator.EndsWith => Expression.Call(
                    property,
                    "EndsWith",
                    null,
                    constantValue
                ),
                _ => null,
            };
        }

        /// <summary>
        /// 构建整数条件表达式
        /// </summary>
        private Expression? BuildIntConditionExpression(
            ParameterExpression param,
            string propertyName,
            FilterOperator op,
            int value
        )
        {
            var property = Expression.Property(param, propertyName);
            var constantValue = Expression.Constant(value);

            return op switch
            {
                FilterOperator.Equals => Expression.Equal(property, constantValue),
                FilterOperator.GreaterThan => Expression.GreaterThan(property, constantValue),
                FilterOperator.LessThan => Expression.LessThan(property, constantValue),
                _ => null,
            };
        }

        /// <summary>
        /// 构建布尔条件表达式
        /// </summary>
        private Expression? BuildBoolConditionExpression(
            ParameterExpression param,
            string propertyName,
            FilterOperator op,
            bool value
        )
        {
            var property = Expression.Property(param, propertyName);
            var constantValue = Expression.Constant(value);

            return op == FilterOperator.Equals ? Expression.Equal(property, constantValue) : null;
        }

        /// <summary>
        /// 用OR连接多个表达式
        /// </summary>
        private Expression<Func<DomesticProduct, ChinaSupplier, bool>>? CombineExpressionsWithOr(
            List<Expression<Func<DomesticProduct, ChinaSupplier, bool>>> expressions
        )
        {
            if (!expressions.Any())
                return null;

            if (expressions.Count == 1)
                return expressions[0];

            try
            {
                var productParam = Expression.Parameter(typeof(DomesticProduct), "p");
                var supplierParam = Expression.Parameter(typeof(ChinaSupplier), "s");

                Expression? combinedExpression = null;

                foreach (var expr in expressions)
                {
                    // 替换参数以确保一致性
                    var rewrittenExpr = new ParameterRewriter(
                        expr.Parameters[0],
                        productParam,
                        expr.Parameters[1],
                        supplierParam
                    ).Visit(expr.Body);

                    combinedExpression =
                        combinedExpression == null
                            ? rewrittenExpr
                            : Expression.OrElse(combinedExpression, rewrittenExpr);
                }

                if (combinedExpression != null)
                {
                    return Expression.Lambda<Func<DomesticProduct, ChinaSupplier, bool>>(
                        combinedExpression,
                        productParam,
                        supplierParam
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "合并OR表达式失败");
            }

            return null;
        }

        /// <summary>
        /// 简化的分组OR条件处理（备用方案）
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplySimplifiedGroupedOrConditions(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<List<FilterCondition>> groupedConditions
        )
        {
            // 使用第一个组作为基础查询
            if (groupedConditions.Any())
            {
                var firstGroup = groupedConditions[0];
                queryable = ApplyConditionsWithLogic(queryable, firstGroup, LogicalOperator.And);

                _logger.LogWarning("使用简化的分组OR处理，可能不完全符合复杂逻辑需求");
            }

            return queryable;
        }

        /// <summary>
        /// 参数重写器，用于统一表达式参数
        /// </summary>
        private class ParameterRewriter : ExpressionVisitor
        {
            private readonly ParameterExpression _oldProductParam;
            private readonly ParameterExpression _newProductParam;
            private readonly ParameterExpression _oldSupplierParam;
            private readonly ParameterExpression _newSupplierParam;

            public ParameterRewriter(
                ParameterExpression oldProductParam,
                ParameterExpression newProductParam,
                ParameterExpression oldSupplierParam,
                ParameterExpression newSupplierParam
            )
            {
                _oldProductParam = oldProductParam;
                _newProductParam = newProductParam;
                _oldSupplierParam = oldSupplierParam;
                _newSupplierParam = newSupplierParam;
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                if (node == _oldProductParam)
                    return _newProductParam;
                if (node == _oldSupplierParam)
                    return _newSupplierParam;
                return base.VisitParameter(node);
            }
        }

        /// <summary>
        /// 根据每个条件的原始逻辑操作符应用条件（重新设计）
        /// 新逻辑：每个条件使用自己的LogicalOperator，不使用统一的logicalOperator参数
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyConditionsWithLogic(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<FilterCondition> conditions,
            LogicalOperator logicalOperator
        )
        {
            if (conditions == null || !conditions.Any())
                return queryable;

            Console.WriteLine($"[DEBUG] 应用条件组，条件数: {conditions.Count}");

            // 新逻辑：根据每个条件的原始逻辑操作符构建复杂的SQL
            return ApplyConditionsWithIndividualLogic(queryable, conditions);
        }

        /// <summary>
        /// 根据每个条件的个体逻辑操作符应用条件
        /// 支持复杂的混合逻辑：条件1 AND 条件2 OR 条件3 AND 条件4
        /// 使用Expression树构建复杂的WHERE子句
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyConditionsWithIndividualLogic(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<FilterCondition> conditions
        )
        {
            if (!conditions.Any())
                return queryable;

            try
            {
                // 构建复杂的Expression表达式
                var complexExpression = BuildComplexLogicExpression(conditions);
                if (complexExpression != null)
                {
                    Console.WriteLine($"[DEBUG] 应用复杂逻辑表达式，条件数: {conditions.Count}");
                    return queryable.Where(complexExpression);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "构建复杂逻辑表达式失败，使用简化处理");
            }

            // 如果Expression构建失败，使用简化处理（所有条件用AND连接）
            Console.WriteLine($"[DEBUG] 使用简化处理：所有条件用AND连接");
            foreach (var condition in conditions)
            {
                queryable = ApplyFilterCondition(queryable, condition);
            }

            return queryable;
        }

        /// <summary>
        /// 构建复杂逻辑的Expression表达式
        /// 支持：条件1 AND 条件2 OR 条件3 AND 条件4 这样的混合逻辑
        /// </summary>
        private Expression<Func<DomesticProduct, ChinaSupplier, bool>>? BuildComplexLogicExpression(
            List<FilterCondition> conditions
        )
        {
            if (!conditions.Any())
                return null;

            try
            {
                // 参数表达式
                var productParam = Expression.Parameter(typeof(DomesticProduct), "p");
                var supplierParam = Expression.Parameter(typeof(ChinaSupplier), "s");

                // 构建第一个条件表达式
                var currentExpression = BuildSingleConditionExpression(
                    productParam,
                    supplierParam,
                    conditions[0]
                );
                if (currentExpression == null)
                    return null;

                Console.WriteLine(
                    $"[DEBUG] 构建复杂表达式 - 第一个条件: {conditions[0].FieldName} {conditions[0].Operator} {conditions[0].Value}"
                );

                // 从第二个条件开始，根据逻辑操作符组合
                for (int i = 1; i < conditions.Count; i++)
                {
                    var condition = conditions[i];
                    var conditionExpr = BuildSingleConditionExpression(
                        productParam,
                        supplierParam,
                        condition
                    );

                    if (conditionExpr != null)
                    {
                        if (condition.LogicalOperator == LogicalOperator.Or)
                        {
                            // OR逻辑
                            currentExpression = Expression.OrElse(currentExpression, conditionExpr);
                            Console.WriteLine(
                                $"[DEBUG] 构建复杂表达式 - OR条件 {i}: {condition.FieldName} {condition.Operator} {condition.Value}"
                            );
                        }
                        else
                        {
                            // AND逻辑
                            currentExpression = Expression.AndAlso(
                                currentExpression,
                                conditionExpr
                            );
                            Console.WriteLine(
                                $"[DEBUG] 构建复杂表达式 - AND条件 {i}: {condition.FieldName} {condition.Operator} {condition.Value}"
                            );
                        }
                    }
                }

                // 返回Lambda表达式
                return Expression.Lambda<Func<DomesticProduct, ChinaSupplier, bool>>(
                    currentExpression,
                    productParam,
                    supplierParam
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "构建复杂逻辑表达式时发生异常");
                return null;
            }
        }

        /// <summary>
        /// 应用OR条件（支持单个或多个条件）
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyOrConditions(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<FilterCondition> conditions
        )
        {
            if (!conditions.Any())
                return queryable;

            // 单个条件也要应用OR逻辑（虽然单个条件OR没有实际意义，但保持逻辑一致性）
            if (conditions.Count == 1)
            {
                return ApplyFilterCondition(queryable, conditions[0]);
            }

            // 按字段名分组，同一字段的条件可以优化
            var groupedConditions = conditions.GroupBy(c => c.FieldName).ToList();

            if (groupedConditions.Count == 1)
            {
                // 同一字段的多个值，可以使用IN或多个OR
                var fieldName = groupedConditions[0].Key;
                var fieldConditions = groupedConditions[0].ToList();

                return ApplyFieldOrConditions(queryable, fieldName, fieldConditions);
            }
            else
            {
                // 不同字段的条件，需要复杂的OR逻辑
                // 这里暂时使用简化处理，每个条件单独处理然后OR连接
                // TODO: 可以进一步优化为更复杂的Expression构建
                return ApplyComplexOrConditions(queryable, conditions);
            }
        }

        /// <summary>
        /// 应用同一字段的OR条件
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyFieldOrConditions(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            string fieldName,
            List<FilterCondition> conditions
        )
        {
            if (!conditions.Any())
                return queryable;

            // 对于同一字段的多个值，构建OR条件
            var values = conditions
                .Select(c => c.Value?.ToString())
                .Where(v => !string.IsNullOrEmpty(v))
                .Cast<string>()
                .ToList();
            if (!values.Any())
                return queryable;

            // 根据字段类型和操作符构建查询
            var firstCondition = conditions.First();

            return fieldName switch
            {
                "ProductName" => firstCondition.Operator switch
                {
                    FilterOperator.Contains => queryable.Where(
                        (p, s) =>
                            values.Any(v => p.ProductName != null && p.ProductName.Contains(v))
                    ),
                    FilterOperator.Equals => queryable.Where(
                        (p, s) => p.ProductName != null && values.Contains(p.ProductName)
                    ),
                    _ => queryable,
                },
                "HBProductNo" => firstCondition.Operator switch
                {
                    FilterOperator.Contains => queryable.Where(
                        (p, s) =>
                            values.Any(v => p.HBProductNo != null && p.HBProductNo.Contains(v))
                    ),
                    FilterOperator.Equals => queryable.Where(
                        (p, s) => p.HBProductNo != null && values.Contains(p.HBProductNo)
                    ),
                    _ => queryable,
                },
                "SupplierCode" => firstCondition.Operator switch
                {
                    FilterOperator.Contains => queryable.Where(
                        (p, s) =>
                            values.Any(v => p.SupplierCode != null && p.SupplierCode.Contains(v))
                    ),
                    FilterOperator.Equals => queryable.Where(
                        (p, s) => p.SupplierCode != null && values.Contains(p.SupplierCode)
                    ),
                    _ => queryable,
                },
                // 没有「查看国内供应商名称」权限的账号按名称列筛选时筛不出结果（名称不可能等于占位值）。
                "Supplier.SupplierName" when !DomesticSupplierInfoMasking.IsNameSearchAllowed => queryable.Where(
                    (p, s) => s.SupplierName == DomesticSupplierInfoMasking.BlockedNameKeyword
                ),
                "Supplier.SupplierName" => firstCondition.Operator switch
                {
                    FilterOperator.Contains => queryable.Where(
                        (p, s) =>
                            values.Any(v => s.SupplierName != null && s.SupplierName.Contains(v))
                    ),
                    FilterOperator.Equals => queryable.Where(
                        (p, s) => s.SupplierName != null && values.Contains(s.SupplierName)
                    ),
                    _ => queryable,
                },
                "ProductType" => ApplyProductTypeFilter(queryable, firstCondition.Operator, values),
                "IsActive" => ApplyIsActiveFilter(queryable, firstCondition.Operator, values),
                _ => queryable,
            };
        }

        /// <summary>
        /// 应用复杂的OR条件（不同字段）
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyComplexOrConditions(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            List<FilterCondition> conditions
        )
        {
            // 对于跨字段的OR条件，这里使用简化处理
            // 在实际应用中，可能需要更复杂的Expression树构建
            // 暂时每个条件都单独处理，然后用OR连接（这需要SqlSugar的高级功能）

            // 由于SqlSugar的限制，这里暂时使用第一个条件作为示例
            // TODO: 实现真正的跨字段OR逻辑
            if (conditions.Any())
            {
                return ApplyFilterCondition(queryable, conditions.First());
            }

            return queryable;
        }

        /// <summary>
        /// 应用商品类型过滤
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyProductTypeFilter(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            FilterOperator filterOperator,
            List<string> values
        )
        {
            if (filterOperator == FilterOperator.Equals)
            {
                var intValues = values
                    .Where(v => int.TryParse(v, out _))
                    .Select(int.Parse)
                    .ToList();
                return intValues.Any()
                    ? queryable.Where((p, s) => intValues.Contains(p.ProductType))
                    : queryable;
            }
            return queryable;
        }

        /// <summary>
        /// 应用状态过滤
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyIsActiveFilter(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            FilterOperator filterOperator,
            List<string> values
        )
        {
            if (filterOperator == FilterOperator.Equals)
            {
                var boolValues = values
                    .Where(v => bool.TryParse(v, out _))
                    .Select(bool.Parse)
                    .ToList();
                return boolValues.Any()
                    ? queryable.Where((p, s) => boolValues.Contains(p.IsActive))
                    : queryable;
            }
            return queryable;
        }

        /// <summary>
        /// 应用单个过滤条件到SQL查询
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyFilterCondition(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            FilterCondition condition
        )
        {
            if (
                condition == null
                || string.IsNullOrEmpty(condition.FieldName)
                || condition.Value == null
            )
                return queryable;

            var value = condition.Value.ToString();
            if (string.IsNullOrEmpty(value))
                return queryable;

            // 根据字段名和操作符构建查询条件
            return condition.FieldName switch
            {
                "ProductName" => condition.Operator switch
                {
                    FilterOperator.Contains => queryable.Where(
                        (p, s) => p.ProductName != null && p.ProductName.Contains(value)
                    ),
                    FilterOperator.Equals => queryable.Where((p, s) => p.ProductName == value),
                    FilterOperator.StartsWith => queryable.Where(
                        (p, s) => p.ProductName != null && p.ProductName.StartsWith(value)
                    ),
                    FilterOperator.EndsWith => queryable.Where(
                        (p, s) => p.ProductName != null && p.ProductName.EndsWith(value)
                    ),
                    _ => queryable,
                },
                "HBProductNo" => condition.Operator switch
                {
                    FilterOperator.Contains => queryable.Where(
                        (p, s) => p.HBProductNo != null && p.HBProductNo.Contains(value)
                    ),
                    FilterOperator.Equals => queryable.Where((p, s) => p.HBProductNo == value),
                    FilterOperator.StartsWith => queryable.Where(
                        (p, s) => p.HBProductNo != null && p.HBProductNo.StartsWith(value)
                    ),
                    FilterOperator.EndsWith => queryable.Where(
                        (p, s) => p.HBProductNo != null && p.HBProductNo.EndsWith(value)
                    ),
                    _ => queryable,
                },
                "SupplierCode" => condition.Operator switch
                {
                    FilterOperator.Contains => queryable.Where(
                        (p, s) => p.SupplierCode != null && p.SupplierCode.Contains(value)
                    ),
                    FilterOperator.Equals => queryable.Where((p, s) => p.SupplierCode == value),
                    _ => queryable,
                },
                // 没有「查看国内供应商名称」权限的账号按名称列筛选时筛不出结果（名称不可能等于占位值）。
                "Supplier.SupplierName" when !DomesticSupplierInfoMasking.IsNameSearchAllowed => queryable.Where(
                    (p, s) => s.SupplierName == DomesticSupplierInfoMasking.BlockedNameKeyword
                ),
                "Supplier.SupplierName" => condition.Operator switch
                {
                    FilterOperator.Contains => queryable.Where(
                        (p, s) => s.SupplierName != null && s.SupplierName.Contains(value)
                    ),
                    FilterOperator.Equals => queryable.Where((p, s) => s.SupplierName == value),
                    _ => queryable,
                },
                "ProductType" => condition.Operator switch
                {
                    FilterOperator.Equals when int.TryParse(value, out int productType) =>
                        queryable.Where((p, s) => p.ProductType == productType),
                    _ => queryable,
                },
                "IsActive" => condition.Operator switch
                {
                    FilterOperator.Equals when bool.TryParse(value, out bool isActive) =>
                        queryable.Where((p, s) => p.IsActive == isActive),
                    _ => queryable,
                },
                "UpdatedAt" => condition.Operator switch
                {
                    FilterOperator.Equals when DateTime.TryParse(value, out DateTime updatedAt) =>
                        queryable.Where((p, s) => SqlFunc.DateIsSame(p.UpdatedAt, updatedAt)),
                    FilterOperator.GreaterThan when DateTime.TryParse(value, out DateTime gtDate) =>
                        queryable.Where((p, s) => p.UpdatedAt > gtDate),
                    FilterOperator.LessThan when DateTime.TryParse(value, out DateTime ltDate) =>
                        queryable.Where((p, s) => p.UpdatedAt < ltDate),
                    _ => queryable,
                },
                _ => queryable,
            };
        }

        /// <summary>
        /// 应用SQL排序
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplySqlSorting(
            ISugarQueryable<DomesticProduct, ChinaSupplier> queryable,
            string sortBy,
            bool isDescending
        )
        {
            return sortBy.ToLower() switch
            {
                "productname" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.ProductName)
                    : queryable.OrderBy((p, s) => p.ProductName),
                "hbproductno" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.HBProductNo)
                    : queryable.OrderBy((p, s) => p.HBProductNo),
                "suppliercode" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.SupplierCode)
                    : queryable.OrderBy((p, s) => p.SupplierCode),
                // 按被遮掉的国内供应商名称排序会泄露相对顺序，无权限时改按供应商代码排。
                "suppliername" when !DomesticSupplierInfoMasking.IsNameSearchAllowed => isDescending
                    ? queryable.OrderByDescending((p, s) => p.SupplierCode)
                    : queryable.OrderBy((p, s) => p.SupplierCode),
                "suppliername" => isDescending
                    ? queryable.OrderByDescending((p, s) => s.SupplierName)
                    : queryable.OrderBy((p, s) => s.SupplierName),
                "producttype" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.ProductType)
                    : queryable.OrderBy((p, s) => p.ProductType),
                "packingquantity" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.PackingQuantity)
                    : queryable.OrderBy((p, s) => p.PackingQuantity),
                "unitvolume" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.UnitVolume)
                    : queryable.OrderBy((p, s) => p.UnitVolume),
                "domesticprice" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.DomesticPrice)
                    : queryable.OrderBy((p, s) => p.DomesticPrice),
                "importprice" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.ImportPrice)
                    : queryable.OrderBy((p, s) => p.ImportPrice),
                "oemprice" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.OEMPrice)
                    : queryable.OrderBy((p, s) => p.OEMPrice),
                "createdat" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.CreatedAt)
                    : queryable.OrderBy((p, s) => p.CreatedAt),
                "updatedat" => isDescending
                    ? queryable.OrderByDescending((p, s) => p.UpdatedAt)
                    : queryable.OrderBy((p, s) => p.UpdatedAt),
                _ => queryable.OrderByDescending((p, s) => p.UpdatedAt), // 默认排序
            };
        }

        /// <summary>
        /// 应用排序（内存版本，保留用于非高级过滤场景）
        /// </summary>
        private List<DomesticProduct> ApplySorting(
            List<DomesticProduct> products,
            string sortBy,
            bool isDescending
        )
        {
            return sortBy.ToLower() switch
            {
                "productname" => isDescending
                    ? products.OrderByDescending(p => p.ProductName).ToList()
                    : products.OrderBy(p => p.ProductName).ToList(),
                "hbproductno" => isDescending
                    ? products.OrderByDescending(p => p.HBProductNo).ToList()
                    : products.OrderBy(p => p.HBProductNo).ToList(),
                "suppliercode" => isDescending
                    ? products.OrderByDescending(p => p.SupplierCode).ToList()
                    : products.OrderBy(p => p.SupplierCode).ToList(),
                // 按被遮掉的国内供应商名称排序会泄露相对顺序，无权限时改按供应商代码排。
                "suppliername" when !DomesticSupplierInfoMasking.IsNameSearchAllowed => isDescending
                    ? products.OrderByDescending(p => p.SupplierCode).ToList()
                    : products.OrderBy(p => p.SupplierCode).ToList(),
                "suppliername" => isDescending
                    ? products.OrderByDescending(p => p.Supplier?.SupplierName).ToList()
                    : products.OrderBy(p => p.Supplier?.SupplierName).ToList(),
                "producttype" => isDescending
                    ? products.OrderByDescending(p => p.ProductType).ToList()
                    : products.OrderBy(p => p.ProductType).ToList(),
                "packingquantity" => isDescending
                    ? products.OrderByDescending(p => p.PackingQuantity).ToList()
                    : products.OrderBy(p => p.PackingQuantity).ToList(),
                "unitvolume" => isDescending
                    ? products.OrderByDescending(p => p.UnitVolume).ToList()
                    : products.OrderBy(p => p.UnitVolume).ToList(),
                "domesticprice" => isDescending
                    ? products.OrderByDescending(p => p.DomesticPrice).ToList()
                    : products.OrderBy(p => p.DomesticPrice).ToList(),
                "importprice" => isDescending
                    ? products.OrderByDescending(p => p.ImportPrice).ToList()
                    : products.OrderBy(p => p.ImportPrice).ToList(),
                "oemprice" => isDescending
                    ? products.OrderByDescending(p => p.OEMPrice).ToList()
                    : products.OrderBy(p => p.OEMPrice).ToList(),
                "isactive" => isDescending
                    ? products.OrderByDescending(p => p.IsActive).ToList()
                    : products.OrderBy(p => p.IsActive).ToList(),
                "createdat" => isDescending
                    ? products.OrderByDescending(p => p.CreatedAt).ToList()
                    : products.OrderBy(p => p.CreatedAt).ToList(),
                "updatedat" => isDescending
                    ? products.OrderByDescending(p => p.UpdatedAt).ToList()
                    : products.OrderBy(p => p.UpdatedAt).ToList(),
                _ => products.OrderByDescending(p => p.UpdatedAt).ToList(),
            };
        }

        /// <summary>
        /// 为DTO加载套装商品信息
        /// </summary>
        private async Task LoadSetProductsForDtos(List<DomesticProductDto> productDtos)
        {
            var setProductCodes = productDtos
                .Where(p => p.ProductType == 1 || p.ProductType == 2)
                .Select(p => p.ProductCode)
                .ToList();

            if (setProductCodes.Any())
            {
                var db = _context.Db;
                var setProducts = await db.Queryable<DomesticSetProduct>()
                    .Where(sp => setProductCodes.Contains(sp.ProductCode))
                    .ToListAsync();

                foreach (var product in productDtos.Where(p => p.ProductType == 1))
                {
                    var setProductsForThisProduct = setProducts
                        .Where(sp => sp.ProductCode == product.ProductCode)
                        .ToList();

                    product.SetProducts = _mapper.Map<List<DomesticSetProductDto>>(
                        setProductsForThisProduct
                    );
                }
            }
        }

        /// <summary>
        /// 根据编码获取国内商品详情
        /// </summary>
        public async Task<ApiResponse<DomesticProductDetailDto>> GetDomesticProductByCodeAsync(
            string productCode
        )
        {
            try
            {
                var db = _context.Db;

                var product = await db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => p.ProductCode == productCode && !p.IsDeleted)
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
                                CreatedBy = p.CreatedBy,
                                UpdatedBy = p.UpdatedBy,
                                Supplier = new ChinaSupplier
                                {
                                    SupplierCode = s.SupplierCode,
                                    SupplierName = s.SupplierName,
                                },
                            }
                    )
                    .FirstAsync();

                if (product == null)
                {
                    return ApiResponse<DomesticProductDetailDto>.Error(
                        "商品不存在",
                        "PRODUCT_NOT_FOUND"
                    );
                }

                var productDetailDto = _mapper.Map<DomesticProductDetailDto>(product);

                // 如果是套装商品，获取套装商品列表
                if (product.ProductType == 2 && !string.IsNullOrWhiteSpace(product.HBProductNo))
                {
                    var setProducts = await db.Queryable<DomesticSetProduct>()
                        .Where(sp => sp.ProductCode == productCode && !sp.IsDeleted)
                        .ToListAsync();

                    productDetailDto.SetProducts = _mapper.Map<List<DomesticSetProductDto>>(
                        setProducts
                    );
                }

                return ApiResponse<DomesticProductDetailDto>.OK(productDetailDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取国内商品详情失败，ProductCode: {ProductCode}",
                    productCode
                );
                return ApiResponse<DomesticProductDetailDto>.Error(
                    "获取国内商品详情失败",
                    "GET_PRODUCT_DETAIL_ERROR"
                );
            }
        }

        /// <summary>
        /// 根据供应商编码获取商品列表
        /// </summary>
        public async Task<ApiResponse<List<DomesticProductDto>>> GetProductsBySupplierCodeAsync(
            string supplierCode
        )
        {
            try
            {
                var db = _context.Db;

                var products = await db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => p.SupplierCode == supplierCode && !p.IsDeleted)
                    .OrderBy((p, s) => p.ProductName)
                    .Select(
                        (p, s) =>
                            new DomesticProduct
                            {
                                ProductCode = p.ProductCode,
                                SupplierCode = p.SupplierCode,
                                ProductName = p.ProductName,
                                HBProductNo = p.HBProductNo,
                                ProductType = p.ProductType,
                                IsActive = p.IsActive,
                                Supplier = new ChinaSupplier { SupplierName = s.SupplierName },
                            }
                    )
                    .ToListAsync();

                var productDtos = _mapper.Map<List<DomesticProductDto>>(products);
                return ApiResponse<List<DomesticProductDto>>.OK(productDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取供应商商品列表失败，SupplierCode: {SupplierCode}",
                    supplierCode
                );
                return ApiResponse<List<DomesticProductDto>>.Error(
                    "获取供应商商品列表失败",
                    "GET_SUPPLIER_PRODUCTS_ERROR"
                );
            }
        }

        /// <summary>
        /// 获取启用的商品列表
        /// </summary>
        public async Task<ApiResponse<List<DomesticProductDto>>> GetActiveProductsAsync(
            string? supplierCode = null,
            int? productType = null
        )
        {
            try
            {
                var db = _context.Db;

                var query = db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => p.IsActive && !p.IsDeleted);

                if (!string.IsNullOrWhiteSpace(supplierCode))
                {
                    query = query.Where((p, s) => p.SupplierCode == supplierCode);
                }

                if (productType.HasValue)
                {
                    query = query.Where((p, s) => p.ProductType == productType.Value);
                }

                var products = await query
                    .OrderBy((p, s) => p.ProductName)
                    .Select(
                        (p, s) =>
                            new DomesticProduct
                            {
                                ProductCode = p.ProductCode,
                                SupplierCode = p.SupplierCode,
                                ProductName = p.ProductName,
                                HBProductNo = p.HBProductNo,
                                ProductType = p.ProductType,
                                DomesticPrice = p.DomesticPrice,
                                IsActive = p.IsActive,
                                Supplier = new ChinaSupplier { SupplierName = s.SupplierName },
                            }
                    )
                    .ToListAsync();

                var productDtos = _mapper.Map<List<DomesticProductDto>>(products);
                return ApiResponse<List<DomesticProductDto>>.OK(productDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取启用商品列表失败，SupplierCode: {SupplierCode}, ProductType: {ProductType}",
                    supplierCode,
                    productType
                );
                return ApiResponse<List<DomesticProductDto>>.Error(
                    "获取启用商品列表失败",
                    "GET_ACTIVE_PRODUCTS_ERROR"
                );
            }
        }

        /// <summary>
        /// 创建国内商品
        /// </summary>
        public async Task<ApiResponse<DomesticProductDto>> CreateDomesticProductAsync(
            CreateDomesticProductDto dto
        )
        {
            try
            {
                var db = _context.Db;

                // 检查供应商是否存在
                var supplier = await db.Queryable<ChinaSupplier>()
                    .Where(s => s.SupplierCode == dto.SupplierCode && !s.IsDeleted)
                    .FirstAsync();

                if (supplier == null)
                {
                    return ApiResponse<DomesticProductDto>.Error(
                        "供应商不存在",
                        "SUPPLIER_NOT_FOUND"
                    );
                }

                // 创建新商品
                var product = _mapper.Map<DomesticProduct>(dto);
                // 映射配置会忽略自动生成字段；显式输入必须规范化后回填，空白值继续走自动生成。
                product.HBProductNo = string.IsNullOrWhiteSpace(dto.HBProductNo)
                    ? null
                    : dto.HBProductNo.Trim();
                product.Barcode = string.IsNullOrWhiteSpace(dto.Barcode)
                    ? null
                    : dto.Barcode.Trim();
                product.ProductCode = UuidHelper.GenerateUuid7();

                // 生成HB货号（如果未提供）
                if (string.IsNullOrWhiteSpace(product.HBProductNo))
                {
                    var productNoResponse = await GenerateNextProductNoAsync(
                        dto.SupplierCode,
                        dto.PrefixCode
                    );
                    if (!productNoResponse.Success)
                    {
                        return ApiResponse<DomesticProductDto>.Error(
                            "生成商品货号失败",
                            "GENERATE_PRODUCT_NO_ERROR"
                        );
                    }
                    product.HBProductNo = productNoResponse.Data;
                }
                else
                {
                    // 检查HB货号是否已存在
                    var existingProduct = await db.Queryable<DomesticProduct>()
                        .Where(p => p.HBProductNo == product.HBProductNo && !p.IsDeleted)
                        .FirstAsync();

                    if (existingProduct != null)
                    {
                        return ApiResponse<DomesticProductDto>.Error(
                            "HB货号已存在",
                            "HB_PRODUCT_NO_EXISTS"
                        );
                    }
                }

                // 生成条形码（如果未提供）
                if (string.IsNullOrWhiteSpace(product.Barcode))
                {
                    var barcodeResponse = await GenerateProductBarcodeAsync(
                        dto.SupplierCode,
                        dto.ProductType
                    );
                    if (!barcodeResponse.Success)
                    {
                        return ApiResponse<DomesticProductDto>.Error(
                            "生成商品条码失败",
                            "GENERATE_BARCODE_ERROR"
                        );
                    }
                    product.Barcode = barcodeResponse.Data;
                }
                else
                {
                    // 检查条形码是否已存在
                    var existingBarcode = await db.Queryable<DomesticProduct>()
                        .Where(p => p.Barcode == product.Barcode && !p.IsDeleted)
                        .FirstAsync();

                    if (existingBarcode != null)
                    {
                        return ApiResponse<DomesticProductDto>.Error(
                            "条形码已存在",
                            "BARCODE_EXISTS"
                        );
                    }
                }

                // 生成默认图片地址（如果未提供）
                // 确保HBProductNo不是完整的URL，避免重复拼接
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

                product.CreatedAt = DateTime.Now;
                product.UpdatedAt = DateTime.Now;
                var currentUser = ResolveActorName();
                product.CreatedBy = currentUser;
                product.UpdatedBy = currentUser;

                var batchGuid = Guid.NewGuid();
                await db.Ado.BeginTranAsync();
                try
                {
                    // 快照、商品写入和历史写入必须共享同一个服务端事务。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        new[] { product.ProductCode }
                    );
                    await db.Insertable(product).ExecuteCommandAsync();
                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        new[] { product.ProductCode },
                        "Create",
                        "DomesticProduct",
                        batchGuid
                    );
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                var productDto = _mapper.Map<DomesticProductDto>(product);
                productDto.SupplierName = supplier.SupplierName;

                _logger.LogInformation(
                    "创建国内商品成功，ProductCode: {ProductCode}",
                    product.ProductCode
                );
                return ApiResponse<DomesticProductDto>.OK(productDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建国内商品失败");
                return ApiResponse<DomesticProductDto>.Error(
                    "创建国内商品失败",
                    "CREATE_PRODUCT_ERROR"
                );
            }
        }

        /// <summary>
        /// 更新国内商品
        /// </summary>
        public async Task<ApiResponse<DomesticProductDto>> UpdateDomesticProductAsync(
            string productCode,
            UpdateDomesticProductDto dto
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
                    return ApiResponse<DomesticProductDto>.Error("商品不存在", "PRODUCT_NOT_FOUND");
                }

                // 更新商品信息
                var batchGuid = Guid.NewGuid();
                await db.Ado.BeginTranAsync();
                ChinaSupplier? supplier = null;
                try
                {
                    // 必须在映射和更新前读取 before，历史失败时整个业务写入回滚。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        new[] { productCode }
                    );
                    _mapper.Map(dto, product);
                    product.UpdatedAt = DateTime.Now;
                    product.UpdatedBy = ResolveActorName();

                    await db.Updateable(product).ExecuteCommandAsync();

                    // 获取供应商信息
                    supplier = await db.Queryable<ChinaSupplier>()
                        .Where(s => s.SupplierCode == product.SupplierCode)
                        .FirstAsync();
                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        new[] { productCode },
                        "Update",
                        "DomesticProduct",
                        batchGuid
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
        /// 删除国内商品
        /// </summary>
        public async Task<ApiResponse<bool>> DeleteDomesticProductAsync(string productCode)
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

                // 检查是否有套装商品使用该商品
                var setProductCount = await db.Queryable<DomesticSetProduct>()
                    .Where(sp => sp.ProductCode == productCode && !sp.IsDeleted)
                    .CountAsync();

                if (setProductCount > 0)
                {
                    return ApiResponse<bool>.Error(
                        $"该商品已被 {setProductCount} 个套装商品使用，无法删除",
                        "PRODUCT_IN_USE"
                    );
                }

                // 软删除
                product.IsDeleted = true;
                product.UpdatedAt = DateTime.Now;
                product.UpdatedBy = ResolveActorName();

                await db.Updateable(product).ExecuteCommandAsync();

                _logger.LogInformation("删除国内商品成功，ProductCode: {ProductCode}", productCode);
                return ApiResponse<bool>.OK(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除国内商品失败，ProductCode: {ProductCode}", productCode);
                return ApiResponse<bool>.Error("删除国内商品失败", "DELETE_PRODUCT_ERROR");
            }
        }

        /// <summary>
        /// 切换国内商品状态
        /// </summary>
        public async Task<ApiResponse<DomesticProductDto>> ToggleProductStatusAsync(
            string productCode,
            bool isActive
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
                    return ApiResponse<DomesticProductDto>.Error("商品不存在", "PRODUCT_NOT_FOUND");
                }

                var batchGuid = Guid.NewGuid();
                await db.Ado.BeginTranAsync();
                ChinaSupplier? supplier = null;
                try
                {
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        new[] { productCode }
                    );
                    product.IsActive = isActive;
                    product.UpdatedAt = DateTime.Now;
                    product.UpdatedBy = ResolveActorName();

                    await db.Updateable(product).ExecuteCommandAsync();

                    // 获取供应商信息
                    supplier = await db.Queryable<ChinaSupplier>()
                        .Where(s => s.SupplierCode == product.SupplierCode)
                        .FirstAsync();
                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        new[] { productCode },
                        "ToggleActive",
                        "DomesticProduct",
                        batchGuid
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

                _logger.LogInformation(
                    "切换国内商品状态成功，ProductCode: {ProductCode}, IsActive: {IsActive}",
                    productCode,
                    isActive
                );
                return ApiResponse<DomesticProductDto>.OK(productDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "切换国内商品状态失败，ProductCode: {ProductCode}",
                    productCode
                );
                return ApiResponse<DomesticProductDto>.Error(
                    "切换国内商品状态失败",
                    "TOGGLE_PRODUCT_STATUS_ERROR"
                );
            }
        }

        /// <summary>
        /// 检查HB货号是否存在
        /// </summary>
        public async Task<ApiResponse<bool>> CheckHBProductNoExistsAsync(
            string hbProductNo,
            string? excludeProductCode = null
        )
        {
            try
            {
                var db = _context.Db;

                var query = db.Queryable<DomesticProduct>()
                    .Where(p => p.HBProductNo == hbProductNo && !p.IsDeleted);

                if (!string.IsNullOrWhiteSpace(excludeProductCode))
                {
                    query = query.Where(p => p.ProductCode != excludeProductCode);
                }

                var exists = await query.AnyAsync();
                return ApiResponse<bool>.OK(exists);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "检查HB货号是否存在失败，HBProductNo: {HBProductNo}",
                    hbProductNo
                );
                return ApiResponse<bool>.Error(
                    "检查HB货号是否存在失败",
                    "CHECK_HB_PRODUCT_NO_EXISTS_ERROR"
                );
            }
        }

        /// <summary>
        /// 检查条形码是否存在
        /// </summary>
        public async Task<ApiResponse<bool>> CheckBarcodeExistsAsync(
            string barcode,
            string? excludeProductCode = null
        )
        {
            try
            {
                var db = _context.Db;

                var query = db.Queryable<DomesticProduct>()
                    .Where(p => p.Barcode == barcode && !p.IsDeleted);

                if (!string.IsNullOrWhiteSpace(excludeProductCode))
                {
                    query = query.Where(p => p.ProductCode != excludeProductCode);
                }

                var exists = await query.AnyAsync();
                return ApiResponse<bool>.OK(exists);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查条形码是否存在失败，Barcode: {Barcode}", barcode);
                return ApiResponse<bool>.Error(
                    "检查条形码是否存在失败",
                    "CHECK_BARCODE_EXISTS_ERROR"
                );
            }
        }

        /// <summary>
        /// 生成下一个商品货号
        /// </summary>
        public async Task<ApiResponse<string>> GenerateNextProductNoAsync(
            string supplierCode,
            string? prefixCode = null
        )
        {
            try
            {
                string? prefixName = null;
                if (!string.IsNullOrWhiteSpace(prefixCode))
                {
                    var db = _context.Db;
                    var prefix = await db.Queryable<ProductPrefixCode>()
                        .Where(p =>
                            (
                                p.PrefixCode == prefixCode
                                && p.SupplierCode == supplierCode
                                && !p.IsDeleted
                            )
                            || (
                                p.PrefixName == prefixCode
                                && p.SupplierCode == supplierCode
                                && !p.IsDeleted
                            )
                        )
                        .FirstAsync();

                    if (prefix == null)
                    {
                        return ApiResponse<string>.Error(
                            "前缀不存在或不属于该供应商",
                            "PREFIX_NOT_FOUND"
                        );
                    }

                    prefixName = prefix.PrefixName;
                }

                var (itemNumber, _) = await _itemBarcodeService.GenerateItemNumberAndBarcodeAsync(
                    supplierCode,
                    ProductTypeEnum.Normal,
                    prefixName
                );
                return ApiResponse<string>.OK(itemNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "生成商品货号失败，SupplierCode: {SupplierCode}, PrefixCode: {PrefixCode}",
                    supplierCode,
                    prefixCode
                );
                return ApiResponse<string>.Error("生成商品货号失败", "GENERATE_PRODUCT_NO_ERROR");
            }
        }

        /// <summary>
        /// 生成商品条形码
        /// </summary>
        public async Task<ApiResponse<string>> GenerateProductBarcodeAsync(
            string supplierCode,
            int productType
        )
        {
            try
            {
                var (_, barcode) = await _itemBarcodeService.GenerateItemNumberAndBarcodeAsync(
                    supplierCode,
                    (ProductTypeEnum)productType
                );
                return ApiResponse<string>.OK(barcode);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "生成商品条码失败，SupplierCode: {SupplierCode}, ProductType: {ProductType}",
                    supplierCode,
                    productType
                );
                return ApiResponse<string>.Error("生成商品条码失败", "GENERATE_BARCODE_ERROR");
            }
        }

        /// <summary>
        /// 生成套装商品货号
        /// </summary>
        /// <param name="baseProductNo">基础商品货号</param>
        /// <param name="setType">套装类型（套10、套15等）</param>
        /// <param name="setIndex">套装序号（1-N）</param>
        /// <returns>生成的套装货号</returns>
        public Task<ApiResponse<string>> GenerateNextSetProductNoAsync(
            string baseProductNo,
            int setType,
            int setIndex
        )
        {
            try
            {
                // 验证输入参数
                if (string.IsNullOrWhiteSpace(baseProductNo))
                {
                    return Task.FromResult(
                        ApiResponse<string>.Error("基础商品货号不能为空", "BASE_PRODUCT_NO_EMPTY")
                    );
                }

                if (setType < 1 || setType > 50)
                {
                    return Task.FromResult(
                        ApiResponse<string>.Error("套装类型必须在1-50之间", "INVALID_SET_TYPE")
                    );
                }

                if (setIndex < 1 || setIndex > setType)
                {
                    return Task.FromResult(
                        ApiResponse<string>.Error(
                            $"套装序号必须在1-{setType}之间",
                            "INVALID_SET_INDEX"
                        )
                    );
                }

                // 生成套装货号
                // 格式：{基础商品货号}-S{套装类型}-{序号（两位数，前导0）}
                // 示例：HB001-S10-01, HB001-S10-02, ..., HB001-S10-10
                //       HB002-S15-01, HB002-S15-02, ..., HB002-S15-15
                var setProductNo =
                    $"{baseProductNo}-S{setType}-{setIndex.ToString().PadLeft(2, '0')}";

                _logger.LogDebug(
                    "生成套装货号: BaseProductNo={BaseProductNo}, SetType={SetType}, SetIndex={SetIndex}, Result={Result}",
                    baseProductNo,
                    setType,
                    setIndex,
                    setProductNo
                );

                return Task.FromResult(ApiResponse<string>.OK(setProductNo));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "生成套装货号失败: BaseProductNo={BaseProductNo}, SetType={SetType}, SetIndex={SetIndex}",
                    baseProductNo,
                    setType,
                    setIndex
                );
                return Task.FromResult(
                    ApiResponse<string>.Error("生成套装货号失败", "GENERATE_SET_PRODUCT_NO_ERROR")
                );
            }
        }

        /// <summary>
        /// 批量验证商品数据
        /// 在实际创建之前，验证数据的有效性
        /// </summary>
        public async Task<ApiResponse<object>> BatchValidateProductsAsync(
            BatchCreateDomesticProductDto dto
        )
        {
            try
            {
                var db = _context.Db;
                var validProducts = new List<object>();
                var invalidProducts = new List<object>();

                // 1. 检查供应商是否存在
                var supplier = await db.Queryable<ChinaSupplier>()
                    .Where(s => s.SupplierCode == dto.SupplierCode && !s.IsDeleted)
                    .FirstAsync();

                if (supplier == null)
                {
                    return ApiResponse<object>.Error("供应商不存在", "SUPPLIER_NOT_FOUND");
                }

                // 2. 如果提供了前缀，检查前缀是否存在且属于该供应商
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

                // 3. 获取现有的商品名称（用于检查重复）
                var existingProductNames = await db.Queryable<DomesticProduct>()
                    .Where(p => p.SupplierCode == dto.SupplierCode && !p.IsDeleted)
                    .Select(p => p.ProductName)
                    .ToListAsync();

                var existingNameSet = new HashSet<string>(
                    existingProductNames.Where(n => !string.IsNullOrWhiteSpace(n))!,
                    StringComparer.OrdinalIgnoreCase
                );

                // 4. 验证每个商品
                for (int i = 0; i < dto.Products.Count; i++)
                {
                    var product = dto.Products[i];
                    var errors = new Dictionary<string, List<string>>();
                    var rowNumber = i + 1;

                    // 验证商品名称
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

                    // 验证国内价格
                    if (product.DomesticPrice.HasValue)
                    {
                        if (product.DomesticPrice.Value < 0)
                        {
                            errors.Add(
                                "domesticPrice",
                                new List<string> { "国内价格必须为非负数" }
                            );
                        }
                    }

                    // 验证零售价
                    if (product.OEMPrice.HasValue)
                    {
                        if (product.OEMPrice.Value < 0)
                        {
                            errors.Add("oemPrice", new List<string> { "零售价必须为非负数" });
                        }
                    }

                    // 验证装箱数
                    if (product.PackingQuantity.HasValue)
                    {
                        if (product.PackingQuantity.Value <= 0)
                        {
                            errors.Add(
                                "packingQuantity",
                                new List<string> { "装箱数必须为正整数" }
                            );
                        }
                    }

                    // 验证单件体积
                    if (product.UnitVolume.HasValue)
                    {
                        if (product.UnitVolume.Value < 0)
                        {
                            errors.Add("unitVolume", new List<string> { "单件体积必须为非负数" });
                        }
                    }

                    // 验证中包数
                    if (product.MiddlePackQuantity.HasValue)
                    {
                        if (product.MiddlePackQuantity.Value <= 0)
                        {
                            errors.Add(
                                "middlePackQuantity",
                                new List<string> { "中包数必须为正整数" }
                            );
                        }
                    }

                    // 根据验证结果分类
                    if (errors.Any())
                    {
                        invalidProducts.Add(
                            new
                            {
                                rowNumber = rowNumber,
                                productName = product.ProductName,
                                errors = errors,
                            }
                        );
                    }
                    else
                    {
                        validProducts.Add(
                            new { rowNumber = rowNumber, productName = product.ProductName }
                        );
                    }
                }

                _logger.LogInformation(
                    "批量验证完成: 有效{Valid}件, 无效{Invalid}件",
                    validProducts.Count,
                    invalidProducts.Count
                );

                return ApiResponse<object>.OK(
                    new { validProducts = validProducts, invalidProducts = invalidProducts }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量验证商品失败");
                return ApiResponse<object>.Error("批量验证商品失败", "BATCH_VALIDATE_ERROR");
            }
        }

        /// <summary>
        /// 批量创建国内商品
        /// </summary>
        public async Task<ApiResponse<List<DomesticProductDto>>> BatchCreateDomesticProductsAsync(
            BatchCreateDomesticProductDto dto
        )
        {
            try
            {
                var db = _context.Db;

                // 检查供应商是否存在
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

                // 批量创建商品
                var products = new List<DomesticProduct>();
                var now = DateTime.Now;
                var currentUser = ResolveActorName();

                for (int i = 0; i < dto.Products.Count && i < itemNumberBarcodeList.Count; i++)
                {
                    var productItem = dto.Products[i];
                    var product = _mapper.Map<DomesticProduct>(productItem);

                    product.ProductCode = UuidHelper.GenerateUuid7();
                    product.SupplierCode = dto.SupplierCode;
                    product.HBProductNo = itemNumberBarcodeList[i].itemNumber;
                    product.Barcode = itemNumberBarcodeList[i].barcode;

                    // 生成默认图片地址（如果未提供）
                    // 确保HBProductNo不是完整的URL，避免重复拼接
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

                // 使用事务确保商品和日志都创建成功
                var auditBatchGuid = Guid.NewGuid();
                await db.Ado.BeginTranAsync();
                try
                {
                    // 生成的商品编码在事务外已确定，但快照仍必须在业务写入事务内读取。
                    var productCodes = products.Select(product => product.ProductCode).ToList();
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        productCodes
                    );

                    // 1. 批量插入商品
                    await BatchOperationHelper.BatchInsertAsync(
                        db,
                        products,
                        BatchOperationHelper.GetRecommendedBatchSize(products.Count, 2)
                    );

                    // 2. 创建批次号（同一批次使用相同的批次号）
                    var batchNumber = UuidHelper.GenerateUuid7();

                    // 4. 批量创建记录日志
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
                            PrefixName = prefixName,
                            CreationType = "Batch", // 批量创建
                            BatchNumber = batchNumber,
                            CreatedBy = currentUser,
                            CreatedAt = now,
                        };
                        creationLogs.Add(log);
                    }

                    // 5. 批量插入创建记录日志
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

                    // 6. 提交事务
                    await db.Ado.CommitTranAsync();

                    var productDtos = _mapper.Map<List<DomesticProductDto>>(products);
                    foreach (var productDto in productDtos)
                    {
                        productDto.SupplierName = supplier.SupplierName;
                    }

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

        /// <summary>
        /// 批量删除国内商品
        /// </summary>
        public async Task<ApiResponse<bool>> BatchDeleteDomesticProductsAsync(
            List<string> productCodes
        )
        {
            try
            {
                var db = _context.Db;

                // 检查商品是否存在
                var products = await db.Queryable<DomesticProduct>()
                    .Where(p => productCodes.Contains(p.ProductCode) && !p.IsDeleted)
                    .ToListAsync();

                if (products.Count != productCodes.Count)
                {
                    return ApiResponse<bool>.Error("部分商品不存在", "SOME_PRODUCTS_NOT_FOUND");
                }

                // 检查是否有套装商品使用这些商品
                var usedProductCodes = await db.Queryable<DomesticSetProduct>()
                    .Where(sp => productCodes.Contains(sp.ProductCode) && !sp.IsDeleted)
                    .Select(sp => sp.ProductCode)
                    .ToListAsync();

                var usedProducts = products
                    .Where(p => usedProductCodes.Contains(p.ProductCode))
                    .Select(p => p.ProductName ?? p.ProductCode)
                    .ToList();

                if (usedProducts.Any())
                {
                    return ApiResponse<bool>.Error(
                        $"以下商品已被套装商品使用，无法删除: {string.Join(", ", usedProducts)}",
                        "PRODUCTS_IN_USE"
                    );
                }

                // 批量软删除
                var now = DateTime.Now;
                var currentUser = ResolveActorName();
                foreach (var product in products)
                {
                    product.IsDeleted = true;
                    product.UpdatedAt = now;
                    product.UpdatedBy = currentUser;
                }

                await db.Updateable(products).ExecuteCommandAsync();

                _logger.LogInformation("批量删除国内商品成功，Count: {Count}", products.Count);
                return ApiResponse<bool>.OK(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除国内商品失败");
                return ApiResponse<bool>.Error(
                    "批量删除国内商品失败",
                    "BATCH_DELETE_PRODUCTS_ERROR"
                );
            }
        }

        /// <summary>
        /// 批量更新商品状态 - 优化版本，使用事务和批量操作
        /// </summary>
        public async Task<ApiResponse<bool>> BatchUpdateProductStatusAsync(
            List<string> productCodes,
            bool isActive
        )
        {
            if (productCodes == null || !productCodes.Any())
            {
                return ApiResponse<bool>.Error("商品编码列表不能为空", "EMPTY_PRODUCT_CODES");
            }

            try
            {
                var db = _context.Db;
                var auditBatchGuid = Guid.NewGuid();

                // 使用事务确保操作的原子性
                var success = await BatchOperationHelper.ExecuteInTransactionAsync(
                    db,
                    async () =>
                    {
                        // 1. 批量验证商品存在性
                        var existingProducts = await db.Queryable<DomesticProduct>()
                            .Where(p => productCodes.Contains(p.ProductCode) && !p.IsDeleted)
                            .ToListAsync();

                        if (existingProducts.Count != productCodes.Count)
                        {
                            var foundCodes = existingProducts.Select(p => p.ProductCode).ToList();
                            var missingCodes = productCodes.Except(foundCodes).ToList();
                            throw new InvalidOperationException(
                                $"部分商品不存在: {string.Join(", ", missingCodes)}"
                            );
                        }

                        // 在批量状态写入前捕获统一快照，历史失败会触发外层事务回滚。
                        var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            productCodes
                        );

                        // 2. 批量更新状态
                        var now = DateTime.Now;
                        var currentUser = ResolveActorName();

                        foreach (var product in existingProducts)
                        {
                            product.IsActive = isActive;
                            product.UpdatedAt = now;
                            product.UpdatedBy = currentUser;
                        }

                        // 3. 使用批量更新
                        var updatedCount = await BatchOperationHelper.BatchUpdateAsync(
                            db,
                            existingProducts,
                            BatchOperationHelper.GetRecommendedBatchSize(existingProducts.Count, 1)
                        );

                        var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            productCodes
                        );
                        var actorGuid = _currentUserService.GetCurrentUserGuid();
                        await _changeHistoryService.RecordChangesAsync(
                            beforeSnapshots,
                            afterSnapshots,
                            new WarehouseProductChangeHistoryContextDto
                            {
                                Action = "ToggleActive",
                                Source = "DomesticProductBatch",
                                BatchGuid = auditBatchGuid,
                                ActorUserGuid = string.IsNullOrWhiteSpace(actorGuid)
                                    ? null
                                    : actorGuid,
                                ActorName = currentUser,
                                ActorType = string.Equals(
                                        currentUser,
                                        "System",
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    ? "System"
                                    : "User",
                                OccurredAtUtc = DateTime.UtcNow,
                            }
                        );

                        return updatedCount > 0;
                    }
                );

                if (success)
                {
                    _logger.LogInformation(
                        "批量更新商品状态成功，Count: {Count}, IsActive: {IsActive}",
                        productCodes.Count,
                        isActive
                    );
                    return ApiResponse<bool>.OK(true);
                }
                else
                {
                    return ApiResponse<bool>.Error("批量更新失败", "BATCH_UPDATE_FAILED");
                }
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "批量更新商品状态业务验证失败");
                return ApiResponse<bool>.Error(ex.Message, "BUSINESS_VALIDATION_ERROR");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新商品状态失败");
                return ApiResponse<bool>.Error(
                    "批量更新商品状态失败",
                    "BATCH_UPDATE_PRODUCT_STATUS_ERROR"
                );
            }
        }

        /// <summary>
        /// 根据商品类型获取统计信息
        /// </summary>
        public async Task<ApiResponse<Dictionary<int, int>>> GetProductTypeStatisticsAsync(
            string? supplierCode = null
        )
        {
            try
            {
                var db = _context.Db;

                var query = db.Queryable<DomesticProduct>().Where(p => !p.IsDeleted);

                if (!string.IsNullOrWhiteSpace(supplierCode))
                {
                    query = query.Where(p => p.SupplierCode == supplierCode);
                }

                var statistics = await query
                    .GroupBy(p => p.ProductType)
                    .Select(g => new
                    {
                        ProductType = g.ProductType,
                        Count = SqlFunc.AggregateCount(g.ProductCode),
                    })
                    .ToListAsync();

                var result = statistics.ToDictionary(s => s.ProductType, s => s.Count);
                return ApiResponse<Dictionary<int, int>>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取商品类型统计失败，SupplierCode: {SupplierCode}",
                    supplierCode
                );
                return ApiResponse<Dictionary<int, int>>.Error(
                    "获取商品类型统计失败",
                    "GET_PRODUCT_TYPE_STATISTICS_ERROR"
                );
            }
        }

        /// <summary>
        /// 获取商品价格统计
        /// </summary>
        public async Task<ApiResponse<Dictionary<string, decimal?>>> GetProductPriceStatisticsAsync(
            string? supplierCode = null,
            int? productType = null
        )
        {
            try
            {
                var db = _context.Db;

                var query = db.Queryable<DomesticProduct>()
                    .Where(p => !p.IsDeleted && p.DomesticPrice.HasValue);

                if (!string.IsNullOrWhiteSpace(supplierCode))
                {
                    query = query.Where(p => p.SupplierCode == supplierCode);
                }

                if (productType.HasValue)
                {
                    query = query.Where(p => p.ProductType == productType.Value);
                }

                var prices = await query.Select(p => p.DomesticPrice!.Value).ToListAsync();

                var result = new Dictionary<string, decimal?>();

                if (prices.Any())
                {
                    result["Min"] = prices.Min();
                    result["Max"] = prices.Max();
                    result["Average"] = prices.Average();
                    result["Count"] = prices.Count;
                }
                else
                {
                    result["Min"] = null;
                    result["Max"] = null;
                    result["Average"] = null;
                    result["Count"] = 0;
                }

                return ApiResponse<Dictionary<string, decimal?>>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "获取商品价格统计失败，SupplierCode: {SupplierCode}, ProductType: {ProductType}",
                    supplierCode,
                    productType
                );
                return ApiResponse<Dictionary<string, decimal?>>.Error(
                    "获取商品价格统计失败",
                    "GET_PRODUCT_PRICE_STATISTICS_ERROR"
                );
            }
        }

        /// <summary>
        /// 批量检测商品信息 - 通过货号和供应商编码匹配现有数据
        /// </summary>
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

                // 检查供应商是否存在
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

                // 获取所有可能匹配的HB货号
                var hbProductNos = dto
                    .Products.Where(p => !string.IsNullOrWhiteSpace(p.HBProductNo))
                    .Select(p => p.HBProductNo!)
                    .Distinct()
                    .ToList();

                // 批量查询现有商品
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

                // 创建检测结果
                foreach (var inputProduct in dto.Products)
                {
                    var result = new BatchProductDetectionResultDto
                    {
                        InputData = inputProduct,
                        SupplierCode = dto.SupplierCode,
                        SupplierName = supplier.SupplierName,
                    };

                    // 查找匹配的现有商品
                    DomesticProduct? existingProduct = null;
                    if (!string.IsNullOrWhiteSpace(inputProduct.HBProductNo))
                    {
                        existingProduct = existingProducts.FirstOrDefault(p =>
                            p.HBProductNo == inputProduct.HBProductNo
                        );
                    }

                    if (existingProduct != null)
                    {
                        // 商品已存在
                        result.IsNewProduct = false;
                        result.ExistingData = _mapper.Map<DomesticProductDto>(existingProduct);
                        result.ExistingData.SupplierName = supplier.SupplierName;

                        // 检查是否有需要更新的字段
                        result.HasChanges = CheckForChanges(inputProduct, existingProduct);
                        result.ChangeList = GetChangeList(inputProduct, existingProduct);
                    }
                    else
                    {
                        // 新商品
                        result.IsNewProduct = true;
                        result.ExistingData = null;
                        result.HasChanges = false;
                        result.ChangeList = new List<string>();
                    }

                    results.Add(result);
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
        /// 批量创建和更新商品
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

                // 检查供应商是否存在
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

                await db.Ado.BeginTranAsync();
                try
                {
                    // 更新项的 before 快照必须早于任何实体修改；新建项编码稍后加入审计集合。
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        auditProductCodes
                    );

                    // 处理新建商品
                    if (dto.NewProducts?.Any() == true)
                    {
                        var newProducts = new List<DomesticProduct>();

                        // 统计需要生成货号条码的商品
                        var needGenerateProducts = dto
                            .NewProducts.Where(p =>
                                string.IsNullOrWhiteSpace(p.HBProductNo)
                                || string.IsNullOrWhiteSpace(p.Barcode)
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
                                    || string.IsNullOrWhiteSpace(product.Barcode)
                                )
                                {
                                    if (itemNumberBarcodeList != null)
                                    {
                                        var (itemNumber, barcode) = itemNumberBarcodeList[
                                            generateIndex++
                                        ];
                                        if (string.IsNullOrWhiteSpace(product.HBProductNo))
                                            product.HBProductNo = itemNumber;
                                        if (string.IsNullOrWhiteSpace(product.Barcode))
                                            product.Barcode = barcode;
                                    }
                                }

                                // 生成默认图片地址（如果未提供）
                                // 确保HBProductNo不是完整的URL，避免重复拼接
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

                        if (newProducts.Any())
                        {
                            await db.Insertable(newProducts).ExecuteCommandAsync();
                        }
                    }

                    // 处理更新商品（仅更新有变化的字段，并进行必要的校验）
                    if (dto.UpdateProducts?.Any() == true)
                    {
                        var productCodes = dto.UpdateProducts.Select(u => u.ProductCode).ToList();
                        var existingProducts = await db.Queryable<DomesticProduct>()
                            .Where(p => productCodes.Contains(p.ProductCode) && !p.IsDeleted)
                            .ToListAsync();

                        foreach (var updateDto in dto.UpdateProducts)
                        {
                            try
                            {
                                var existingProduct = existingProducts.FirstOrDefault(p =>
                                    p.ProductCode == updateDto.ProductCode
                                );
                                if (existingProduct == null)
                                {
                                    result.Errors.Add(
                                        $"更新商品失败: 商品不存在 {updateDto.ProductCode}"
                                    );
                                    continue;
                                }

                                var hasChanges = false;
                                var changedFields = new List<string>();

                                // 商品名称
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

                                // 英文名称
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

                                // 条形码（校验重复，不更新为重复值）
                                if (
                                    !string.IsNullOrWhiteSpace(updateDto.Barcode)
                                    && !string.Equals(updateDto.Barcode, existingProduct.Barcode)
                                )
                                {
                                    var duplicateBarcode = await db.Queryable<DomesticProduct>()
                                        .Where(p =>
                                            p.SupplierCode == existingProduct.SupplierCode
                                            && p.Barcode == updateDto.Barcode
                                            && !p.IsDeleted
                                            && p.ProductCode != existingProduct.ProductCode
                                        )
                                        .AnyAsync();
                                    if (duplicateBarcode)
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

                                // 国内价格
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

                                // 零售价
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

                                // 单件装箱数
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

                                // 单件体积
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

                                // 中包数量
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

                                if (hasChanges)
                                {
                                    existingProduct.UpdatedAt = now;
                                    existingProduct.UpdatedBy = currentUser;
                                    await db.Updateable(existingProduct).ExecuteCommandAsync();

                                    var productDto = _mapper.Map<DomesticProductDto>(
                                        existingProduct
                                    );
                                    productDto.SupplierName = supplier.SupplierName;
                                    result.UpdatedProducts.Add(productDto);

                                    // 记录变更字段列表
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
                                    // 无有效变更，跳过更新
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
        /// 检查商品是否有变更
        /// </summary>
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

        /// <summary>
        /// 获取变更字段列表
        /// </summary>
        private List<string> GetChangeList(
            BatchProductInputDto inputProduct,
            DomesticProduct existingProduct
        )
        {
            var changes = new List<string>();

            // 只比较新商品中有输入值的字段
            // 返回PascalCase字段名，与前端DTO保持一致

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

        /// <summary>
        /// 修复重复的图片URL
        /// </summary>
        public async Task<ApiResponse<ImageUrlFixResult>> FixDuplicateImageUrlsAsync(
            bool dryRun = true
        )
        {
            try
            {
                var result = new ImageUrlFixResult
                {
                    IsDryRun = dryRun,
                    Details = new List<ImageUrlFixDetail>(),
                };

                var db = _context.Db;

                // 查询所有商品
                var products = await db.Queryable<DomesticProduct>()
                    .Where(p => !p.IsDeleted)
                    .Select(p => new
                    {
                        p.ProductCode,
                        p.HBProductNo,
                        p.ProductName,
                        p.ProductImage,
                    })
                    .ToListAsync();

                result.TotalScanned = products.Count;
                _logger.LogInformation("开始扫描 {Count} 个商品的图片URL", products.Count);

                var productsToUpdate =
                    new List<(string ProductCode, string OriginalUrl, string FixedUrl)>();

                foreach (var product in products)
                {
                    // 检查是否有图片URL
                    if (string.IsNullOrWhiteSpace(product.ProductImage))
                        continue;

                    // 检查是否包含重复的http/https
                    var imageUrl = product.ProductImage;
                    var httpCount = System
                        .Text.RegularExpressions.Regex.Matches(
                            imageUrl,
                            "https?://",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        )
                        .Count;

                    if (httpCount > 1)
                    {
                        // 发现重复URL
                        result.ProblemsFound++;

                        // 修复URL
                        var fixedUrl = ImageUrlHelper.FixDuplicateUrl(imageUrl);

                        if (!string.IsNullOrWhiteSpace(fixedUrl) && fixedUrl != imageUrl)
                        {
                            var detail = new ImageUrlFixDetail
                            {
                                ProductCode = product.ProductCode,
                                HBProductNo = product.HBProductNo,
                                ProductName = product.ProductName,
                                OriginalImageUrl = imageUrl,
                                FixedImageUrl = fixedUrl,
                                IsSuccess = true,
                            };

                            result.Details.Add(detail);
                            productsToUpdate.Add((product.ProductCode, imageUrl, fixedUrl));

                            _logger.LogInformation(
                                "发现重复URL: {ProductCode} {HBProductNo} - {OriginalUrl} => {FixedUrl}",
                                product.ProductCode,
                                product.HBProductNo,
                                imageUrl,
                                fixedUrl
                            );
                        }
                    }
                }

                // 如果不是模拟运行，执行更新
                if (!dryRun && productsToUpdate.Any())
                {
                    _logger.LogInformation(
                        "开始更新 {Count} 个商品的图片URL",
                        productsToUpdate.Count
                    );

                    var auditBatchGuid = Guid.NewGuid();
                    var currentUser = ResolveActorName();
                    await db.Ado.BeginTranAsync();
                    try
                    {
                        var productCodes = productsToUpdate
                            .Select(update => update.ProductCode)
                            .ToList();
                        var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                            productCodes
                        );

                        foreach (var (productCode, originalUrl, fixedUrl) in productsToUpdate)
                        {
                            await db.Updateable<DomesticProduct>()
                                .SetColumns(p => p.ProductImage == fixedUrl)
                                .SetColumns(p => p.UpdatedAt == DateTime.Now)
                                .SetColumns(p => p.UpdatedBy == currentUser)
                                .Where(p => p.ProductCode == productCode)
                                .ExecuteCommandAsync();

                            result.SuccessfullyFixed++;
                        }

                        // 图片修复属于国内商品主档写入，审计失败时回滚本次全部修复。
                        await RecordDomesticProductChangesAsync(
                            beforeSnapshots,
                            productCodes,
                            "Update",
                            "DomesticProduct",
                            auditBatchGuid,
                            "FixDuplicateImageUrls"
                        );
                        await db.Ado.CommitTranAsync();
                    }
                    catch
                    {
                        await db.Ado.RollbackTranAsync();
                        throw;
                    }
                }
                else
                {
                    result.SuccessfullyFixed = productsToUpdate.Count; // 模拟运行时，假设都会成功
                }

                var message = dryRun
                    ? $"模拟运行完成：扫描 {result.TotalScanned} 个商品，发现 {result.ProblemsFound} 个重复URL"
                    : $"修复完成：扫描 {result.TotalScanned} 个商品，发现 {result.ProblemsFound} 个重复URL，成功修复 {result.SuccessfullyFixed} 个，失败 {result.FailedToFix} 个";

                _logger.LogInformation(message);

                return ApiResponse<ImageUrlFixResult>.OK(result, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "修复重复图片URL失败");
                return ApiResponse<ImageUrlFixResult>.Error("修复失败", "FIX_DUPLICATE_URL_ERROR");
            }
        }

        // ==================== React React-Data-Grid 专用方法 ====================

        /// <summary>
        /// 获取 React-Data-Grid 表格数据（支持服务端过滤、排序、分页）
        /// </summary>
        public async Task<GridResponseDto<DomesticProductDto>> GetGridDataAsync(
            GridRequestDto request
        )
        {
            try
            {
                var db = _context.Db;

                // 构建基础查询
                var query = db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where(p => !p.IsDeleted);

                // ========== 应用全局搜索（OR逻辑） ==========
                if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
                {
                    var keyword = request.GlobalSearch.Trim();
                    var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(keyword);
                    query = query.Where(
                        (p, s) =>
                            (p.ProductName != null && p.ProductName.Contains(keyword))
                            || (p.HBProductNo != null && p.HBProductNo.Contains(keyword))
                            || (p.Barcode != null && p.Barcode.Contains(keyword))
                            || (
                                p.EnglishProductName != null
                                && p.EnglishProductName.Contains(keyword)
                            )
                            // 没有「查看国内供应商名称」权限时，名称这一支换成匹配不到的占位关键词。
                            || (
                                s.SupplierName != null
                                && s.SupplierName.Contains(supplierNameKeyword)
                            )
                            || (p.SupplierCode != null && p.SupplierCode.Contains(keyword))
                    );
                }

                // ========== 应用过滤器（AND逻辑） ==========
                if (request.FilterModel != null && request.FilterModel.Any())
                {
                    query = ApplyAgGridFilters(query, request.FilterModel);
                }

                // ========== 应用排序 ==========
                if (request.SortModel != null && request.SortModel.Any())
                {
                    query = ApplyAgGridSorts(query, request.SortModel);
                }
                else
                {
                    // 默认按更新时间倒序
                    query = query.OrderBy(p => p.UpdatedAt, OrderByType.Desc);
                }

                // ========== 获取总数（复制主查询但不包含导航查询） ==========
                var countQuery = db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where(p => !p.IsDeleted);

                // 应用与主查询相同的全局搜索筛选条件
                if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
                {
                    var keyword = request.GlobalSearch.Trim();
                    var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(keyword);
                    countQuery = countQuery.Where(
                        (p, s) =>
                            (p.ProductName != null && p.ProductName.Contains(keyword))
                            || (p.HBProductNo != null && p.HBProductNo.Contains(keyword))
                            || (p.Barcode != null && p.Barcode.Contains(keyword))
                            || (
                                p.EnglishProductName != null
                                && p.EnglishProductName.Contains(keyword)
                            )
                            || (p.SupplierCode != null && p.SupplierCode.Contains(keyword))
                            // 没有「查看国内供应商名称」权限时，名称这一支换成匹配不到的占位关键词。
                            || (
                                s.SupplierName != null
                                && s.SupplierName.Contains(supplierNameKeyword)
                            )
                    );
                }

                var total = await countQuery.CountAsync();

                // ========== 分页查询 ==========
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
                                // 注意：以下字段在实体中不存在，DTO中保留为null
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

                // ========== 批量加载套装数量信息 ==========
                if (items.Any())
                {
                    // 获取套装类型商品的ProductCode列表
                    var setProductCodes = items
                        .Where(x => x.ProductType > 0) // 套装商品 多码商品
                        .Select(x => x.ProductCode)
                        .ToList();

                    if (setProductCodes.Any())
                    {
                        // 批量查询套装数量信息
                        var setItemCounts = await db.Queryable<DomesticSetProduct>()
                            .Where(x => setProductCodes.Contains(x.ProductCode))
                            .GroupBy(x => x.ProductCode)
                            .Select(g => new
                            {
                                ProductCode = g.ProductCode,
                                Count = SqlFunc.AggregateCount(1),
                            })
                            .ToListAsync();

                        // 创建快速查找字典
                        var setCountDict = setItemCounts.ToDictionary(
                            x => x.ProductCode,
                            x => x.Count
                        );

                        // 为套装商品填充数量信息
                        foreach (var item in items.Where(x => x.ProductType > 0))
                        {
                            if (setCountDict.TryGetValue(item.ProductCode, out var count))
                            {
                                // 创建空的套装商品列表，前端通过数量显示"N 件"
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
        /// 应用表格过滤器
        /// </summary>
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

        /// <summary>
        /// 应用文本过滤器
        /// </summary>
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

            // 为每个字段直接编写完整的Where条件
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

                // 对于已经是字符串的数字字段，使用文本匹配
                case "packingQty":
                case "domesticPrice":
                case "labelPrice":
                case "importPrice":
                case "volume":
                case "createdAt":
                case "updatedAt":
                    // 这些字段通常应该使用数字/日期筛选器，但如果前端发送文本筛选，则支持
                    return query;

                default:
                    return query;
            }
        }

        /// <summary>
        /// 直接应用文本操作（使用完整的Lambda表达式）
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyTextOperationDirect(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string operation,
            string value,
            Expression<Func<DomesticProduct, string?>> fieldSelector
        )
        {
            // 提取字段访问表达式
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

        /// <summary>
        /// 应用文本条件（用于关联表）
        /// </summary>
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

        /// <summary>
        /// 应用数字过滤器
        /// </summary>
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

        /// <summary>
        /// 应用数字操作（decimal）- 直接使用Lambda表达式
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyNumberOperation(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string? operation,
            System.Linq.Expressions.Expression<Func<DomesticProduct, decimal?>> fieldSelector,
            decimal value,
            object? filterTo
        )
        {
            // 构建比较表达式
            var parameter = fieldSelector.Parameters[0];
            var member = fieldSelector.Body;

            // 将 decimal 转换为 decimal? 以匹配字段类型
            var constantValue = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Constant(value),
                typeof(decimal?)
            );

            System.Linq.Expressions.Expression? condition = operation?.ToLower() switch
            {
                "equals" => System.Linq.Expressions.Expression.Equal(member, constantValue),
                "notequal" => System.Linq.Expressions.Expression.NotEqual(member, constantValue),
                "lessthan" => System.Linq.Expressions.Expression.LessThan(member, constantValue),
                "lessthanorequal" => System.Linq.Expressions.Expression.LessThanOrEqual(
                    member,
                    constantValue
                ),
                "greaterthan" => System.Linq.Expressions.Expression.GreaterThan(
                    member,
                    constantValue
                ),
                "greaterthanorequal" => System.Linq.Expressions.Expression.GreaterThanOrEqual(
                    member,
                    constantValue
                ),
                "inrange" => filterTo != null
                && decimal.TryParse(filterTo.ToString(), out var toValue)
                    ? System.Linq.Expressions.Expression.AndAlso(
                        System.Linq.Expressions.Expression.GreaterThanOrEqual(
                            member,
                            constantValue
                        ),
                        System.Linq.Expressions.Expression.LessThanOrEqual(
                            member,
                            System.Linq.Expressions.Expression.Convert(
                                System.Linq.Expressions.Expression.Constant(toValue),
                                typeof(decimal?)
                            )
                        )
                    )
                    : null,
                _ => null,
            };

            if (condition == null)
                return query;

            var lambda = System.Linq.Expressions.Expression.Lambda<Func<DomesticProduct, bool>>(
                condition,
                parameter
            );
            return query.Where(lambda);
        }

        /// <summary>
        /// 应用数字操作（int）- 直接使用Lambda表达式
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyIntNumberOperation(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            string? operation,
            System.Linq.Expressions.Expression<Func<DomesticProduct, int?>> fieldSelector,
            int value,
            object? filterTo
        )
        {
            // 构建比较表达式
            var parameter = fieldSelector.Parameters[0];
            var member = fieldSelector.Body;

            // 将 int 转换为 int? 以匹配字段类型
            var constantValue = System.Linq.Expressions.Expression.Convert(
                System.Linq.Expressions.Expression.Constant(value),
                typeof(int?)
            );

            System.Linq.Expressions.Expression? condition = operation?.ToLower() switch
            {
                "equals" => System.Linq.Expressions.Expression.Equal(member, constantValue),
                "notequal" => System.Linq.Expressions.Expression.NotEqual(member, constantValue),
                "lessthan" => System.Linq.Expressions.Expression.LessThan(member, constantValue),
                "lessthanorequal" => System.Linq.Expressions.Expression.LessThanOrEqual(
                    member,
                    constantValue
                ),
                "greaterthan" => System.Linq.Expressions.Expression.GreaterThan(
                    member,
                    constantValue
                ),
                "greaterthanorequal" => System.Linq.Expressions.Expression.GreaterThanOrEqual(
                    member,
                    constantValue
                ),
                "inrange" => filterTo != null && int.TryParse(filterTo.ToString(), out var toValue)
                    ? System.Linq.Expressions.Expression.AndAlso(
                        System.Linq.Expressions.Expression.GreaterThanOrEqual(
                            member,
                            constantValue
                        ),
                        System.Linq.Expressions.Expression.LessThanOrEqual(
                            member,
                            System.Linq.Expressions.Expression.Convert(
                                System.Linq.Expressions.Expression.Constant(toValue),
                                typeof(int?)
                            )
                        )
                    )
                    : null,
                _ => null,
            };

            if (condition == null)
                return query;

            var lambda = System.Linq.Expressions.Expression.Lambda<Func<DomesticProduct, bool>>(
                condition,
                parameter
            );
            return query.Where(lambda);
        }

        /// <summary>
        /// 应用集合过滤器（用于下拉多选）
        /// </summary>
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
                // 将字符串转换为ProductType枚举值
                var typeNames = filter.Values;
                return query.Where(p => typeNames.Contains(p.ProductType.ToString()));
            }

            return query;
        }

        /// <summary>
        /// 应用表格排序
        /// </summary>
        private ISugarQueryable<DomesticProduct, ChinaSupplier> ApplyAgGridSorts(
            ISugarQueryable<DomesticProduct, ChinaSupplier> query,
            List<SortModelDto> sortModel
        )
        {
            if (!sortModel.Any())
                return query;

            // 只取第一个排序（可扩展支持多列排序）
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
                    p => p.HBProductNo,
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

        /// <summary>
        /// 批量删除国内商品（通过商品编码）
        /// </summary>
        public async Task<ApiResponse<bool>> BatchDeleteAsync(List<string> productCodes)
        {
            try
            {
                if (productCodes == null || !productCodes.Any())
                {
                    return ApiResponse<bool>.Error("请选择要删除的商品", "NO_ITEMS_SELECTED");
                }

                var db = _context.Db;

                // 软删除
                var result = await db.Updateable<DomesticProduct>()
                    .SetColumns(p => p.IsDeleted == true)
                    .SetColumns(p => p.UpdatedAt == DateTime.Now)
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

        /// <summary>
        /// 获取套装商品信息列表
        /// </summary>
        public async Task<ApiResponse<List<DomesticSetProductDto>>> GetSetItemsAsync(
            string productCode
        )
        {
            try
            {
                var db = _context.Db;

                // 获取套装商品
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

                if (setProduct.ProductType != 1) // 1 = 套装商品
                {
                    return ApiResponse<List<DomesticSetProductDto>>.Error(
                        "该商品不是套装商品",
                        "NOT_SET_PRODUCT"
                    );
                }

                // 查询套装信息（使用现有的DomesticSetProduct表）
                var items = await db.Queryable<DomesticSetProduct>()
                    .Where(sp => sp.ProductCode == productCode && !sp.IsDeleted)
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

        /// <summary>
        /// 获取商品数量信息（套装/多码商品的数量统计）
        /// </summary>
        public async Task<ApiResponse<int>> GetProductQuantityAsync(string productCode)
        {
            try
            {
                var db = _context.Db;

                // 获取商品信息
                var product = await db.Queryable<DomesticProduct>()
                    .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                    .FirstAsync();

                if (product == null)
                {
                    return ApiResponse<int>.Error("商品不存在", "PRODUCT_NOT_FOUND");
                }

                int quantity;
                switch (product.ProductType)
                {
                    case 0: // 普通商品
                        quantity = 1;
                        break;
                    case 1: // 套装商品 - 统计套装中的商品数量
                        quantity = await db.Queryable<DomesticSetProduct>()
                            .Where(sp => sp.ProductCode == productCode && !sp.IsDeleted)
                            .CountAsync();
                        break;
                    case 2: // 多码商品 - 这里需要根据实际业务逻辑调整
                        // 暂时返回1，未来可以扩展为统计该商品的条码数量或规格数量
                        quantity = 1;
                        break;
                    default:
                        quantity = 1;
                        break;
                }

                return ApiResponse<int>.OK(quantity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品数量失败: ProductCode={ProductCode}", productCode);
                return ApiResponse<int>.Error("获取数量失败", "GET_QUANTITY_ERROR");
            }
        }

        /// <summary>
        /// 更新套装商品信息
        /// </summary>
        public async Task<ApiResponse<bool>> UpdateSetItemsAsync(
            string productCode,
            List<SetItemUpdateDto> items
        )
        {
            try
            {
                var db = _context.Db;

                // 1. 验证商品存在且是套装商品
                var product = await db.Queryable<DomesticProduct>()
                    .Where(p => p.ProductCode == productCode && !p.IsDeleted)
                    .FirstAsync();

                if (product == null)
                {
                    return ApiResponse<bool>.Error("商品不存在", "PRODUCT_NOT_FOUND");
                }

                if (product.ProductType != 1) // 1 = 套装商品
                {
                    return ApiResponse<bool>.Error("该商品不是套装商品", "NOT_SET_PRODUCT");
                }

                // 2. 获取现有的套装子项
                var existingItems = await db.Queryable<DomesticSetProduct>()
                    .Where(sp => sp.ProductCode == productCode && !sp.IsDeleted)
                    .ToListAsync();

                var existingItemsDict = existingItems.ToDictionary(x => x.SetProductCode);

                // 3. 获取当前用户信息（用于审计）
                var currentUser = ResolveActorName();
                var now = DateTime.UtcNow;

                // 4. 处理更新和新增
                var requestedSetProductCodes = new HashSet<string>();
                var itemsToUpdate = new List<DomesticSetProduct>();
                var itemsToInsert = new List<DomesticSetProduct>();

                foreach (var item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.SetProductCode))
                    {
                        requestedSetProductCodes.Add(item.SetProductCode);

                        // 更新现有记录
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
                        // 创建新记录 - 自动生成套装货号和条码

                        string setProductNo = item.SetProductNo ?? string.Empty;
                        string? setBarcode = item.SetBarcode;
                        if (
                            string.IsNullOrWhiteSpace(setProductNo)
                            || string.IsNullOrWhiteSpace(setBarcode)
                        )
                        {
                            var (newItemNumber, newBarcode) =
                                await _itemBarcodeService.GenerateSetItemNumberAndBarcodeAsync(
                                    product.HBProductNo ?? string.Empty,
                                    ProductTypeEnum.Set
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

                // 5. 找出要删除的项（软删除）
                var itemsToDelete = existingItems
                    .Where(x => !requestedSetProductCodes.Contains(x.SetProductCode))
                    .ToList();

                foreach (var item in itemsToDelete)
                {
                    item.IsDeleted = true;
                    item.UpdatedAt = now;
                    item.UpdatedBy = currentUser;
                }

                // 6. 使用事务执行数据库操作
                await db.Ado.BeginTranAsync();
                try
                {
                    // 更新
                    if (itemsToUpdate.Any())
                    {
                        await db.Updateable(itemsToUpdate).ExecuteCommandAsync();
                    }

                    // 插入
                    if (itemsToInsert.Any())
                    {
                        await db.Insertable(itemsToInsert).ExecuteCommandAsync();
                    }

                    // 删除
                    if (itemsToDelete.Any())
                    {
                        await db.Updateable(itemsToDelete)
                            .UpdateColumns(x => new
                            {
                                x.IsDeleted,
                                x.UpdatedAt,
                                x.UpdatedBy,
                            })
                            .ExecuteCommandAsync();
                    }

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

        /// <summary>
        /// 批量创建套装商品（统一规格）
        /// </summary>
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

                // 1. 验证供应商是否存在
                var supplier = await db.Queryable<ChinaSupplier>()
                    .FirstAsync(s => s.SupplierCode == dto.SupplierCode);

                if (supplier == null)
                {
                    return ApiResponse<BatchCreateSetProductsResultDto>.Error(
                        $"供应商 {dto.SupplierCode} 不存在",
                        "SUPPLIER_NOT_FOUND"
                    );
                }

                // 2. 验证套装价格数量是否匹配
                if (dto.SetPrices.Count != dto.SetType)
                {
                    return ApiResponse<BatchCreateSetProductsResultDto>.Error(
                        $"套装价格数量({dto.SetPrices.Count})与套装规格({dto.SetType})不匹配",
                        "PRICE_COUNT_MISMATCH"
                    );
                }

                // 3. 查询前缀信息（如果提供了前缀）
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

                // 4. 批量生成所有主商品的货号和条码
                var mainProductsItemNumbers =
                    await _itemBarcodeService.GenerateBatchItemNumbersAndBarcodesAsync(
                        dto.SupplierCode,
                        ProductTypeEnum.Set,
                        dto.Products.Count,
                        prefixName
                    );

                if (mainProductsItemNumbers.Count != dto.Products.Count)
                {
                    return ApiResponse<BatchCreateSetProductsResultDto>.Error(
                        "生成商品货号条码数量不匹配",
                        "GENERATE_BARCODE_ERROR"
                    );
                }

                // 5. 使用事务批量创建
                try
                {
                    db.Ado.BeginTran();
                    var auditBatchGuid = Guid.NewGuid();
                    var currentUser = ResolveActorName();
                    var now = DateTime.UtcNow;

                    var domesticProducts = new List<DomesticProduct>();
                    var allSetProducts = new List<DomesticSetProduct>();
                    var allCreationLogs = new List<DomesticProductCreationLog>();

                    for (int i = 0; i < dto.Products.Count; i++)
                    {
                        var product = dto.Products[i];
                        var (itemNumber, barcode) = mainProductsItemNumbers[i];

                        try
                        {
                            // 生成套装货号和条码
                            var setItemNumberBarcodeList =
                                await _itemBarcodeService.GenerateBatchSetItemNumbersAndBarcodesAsync(
                                    itemNumber,
                                    ProductTypeEnum.Set,
                                    dto.SetType
                                );

                            // 创建主商品
                            var productCode = Guid.NewGuid().ToString();
                            var domesticProduct = new DomesticProduct
                            {
                                ProductCode = productCode,
                                SupplierCode = dto.SupplierCode,
                                ProductName = product.ProductName,
                                EnglishProductName = product.EnglishProductName,
                                ProductSpecification = product.ProductSpecification,
                                HBProductNo = itemNumber,
                                Barcode = barcode,
                                ProductType = 1,
                                IsActive = true,
                                IsDeleted = false,
                                CreatedAt = now,
                                CreatedBy = currentUser,
                                UpdatedAt = now,
                                UpdatedBy = currentUser,
                            };

                            domesticProducts.Add(domesticProduct);

                            // 创建套装明细
                            for (int j = 0; j < dto.SetType; j++)
                            {
                                var setPriceItem = dto.SetPrices[j];
                                var setProductNo = setItemNumberBarcodeList[j].itemNumber;
                                var setBarcode = setItemNumberBarcodeList[j].barcode;

                                var setProduct = new DomesticSetProduct
                                {
                                    SetProductCode = Guid.NewGuid().ToString(),
                                    ProductCode = productCode,
                                    ProductNo = itemNumber,
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

                            // 记录创建日志
                            var creationLog = new DomesticProductCreationLog
                            {
                                LogId = Guid.NewGuid().ToString(),
                                ProductCode = productCode,
                                SupplierCode = dto.SupplierCode,
                                ProductName = product.ProductName,
                                HBProductNo = itemNumber,
                                PrefixCode = dto.PrefixCode,
                                CreationType = "BatchSetProducts",
                                Remark = $"套装商品批量创建，套装规格：套{dto.SetType}",
                                CreatedAt = DateTime.Now,
                            };

                            allCreationLogs.Add(creationLog);

                            totalSetItems += dto.SetType;

                            _logger.LogInformation(
                                "成功创建套装商品: {ProductName}, 货号: {ProductNo}, 套装数: {SetCount}",
                                product.ProductName,
                                itemNumber,
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

                    var auditProductCodes = domesticProducts
                        .Select(product => product.ProductCode)
                        .ToList();
                    var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(
                        auditProductCodes
                    );

                    // 批量插入所有数据
                    if (domesticProducts.Any())
                    {
                        await BatchOperationHelper.BatchInsertAsync(
                            db,
                            domesticProducts,
                            BatchOperationHelper.GetRecommendedBatchSize(domesticProducts.Count, 2)
                        );
                    }

                    if (allSetProducts.Any())
                    {
                        await BatchOperationHelper.BatchInsertAsync(
                            db,
                            allSetProducts,
                            BatchOperationHelper.GetRecommendedBatchSize(allSetProducts.Count, 2)
                        );
                    }

                    if (allCreationLogs.Any())
                    {
                        await BatchOperationHelper.BatchInsertAsync(
                            db,
                            allCreationLogs,
                            BatchOperationHelper.GetRecommendedBatchSize(allCreationLogs.Count, 2)
                        );
                    }

                    // 套装子项只写关系表，不进入 DomesticProduct 统一历史。
                    await RecordDomesticProductChangesAsync(
                        beforeSnapshots,
                        auditProductCodes,
                        "Create",
                        "DomesticProductBatch",
                        auditBatchGuid
                    );

                    // 提交事务
                    db.Ado.CommitTran();

                    // 5. 构建返回结果
                    var productDtos = _mapper.Map<List<DomesticProductDto>>(domesticProducts);
                    foreach (var productDto in productDtos)
                    {
                        productDto.SupplierName = supplier.SupplierName;
                    }
                    result.CreatedProducts = productDtos;
                    result.SuccessCount = domesticProducts.Count;
                    result.FailureCount = dto.Products.Count - domesticProducts.Count;
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
    }
}
