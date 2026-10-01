using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class ProductGradeReactService : IProductGradeReactService
    {
        private readonly SqlSugarContext _context;
        private readonly HqSqlSugarContext _hqContext;
        private readonly IMapper _mapper;
        private readonly ILogger<ProductGradeReactService> _logger;
        private readonly IWarehouseProductChangeHistoryService _changeHistoryService;
        private readonly ICurrentUserService _currentUserService;

        public ProductGradeReactService(
            SqlSugarContext context,
            HqSqlSugarContext hqContext,
            IMapper mapper,
            ILogger<ProductGradeReactService> logger,
            IWarehouseProductChangeHistoryService changeHistoryService,
            ICurrentUserService currentUserService
        )
        {
            _context = context;
            _hqContext = hqContext;
            _mapper = mapper;
            _logger = logger;
            _changeHistoryService = changeHistoryService;
            _currentUserService = currentUserService;
        }

        /// <summary>业务定义的固定等级（A 核心、B 观察、C 淘汰、D 清库存），与 Web 端 PRODUCT_GRADE_CONFIG 保持一致。</summary>
        private static readonly string[] DefinedGrades = { "A", "B", "C", "D" };

        public async Task<ApiResponse<List<string>>> GetGradeOptionsAsync()
        {
            try
            {
                // 直接对等级列去重：不能借用分页列表（按等级排序取前 1000 行再去重），
                // 否则同一等级的商品超过 1000 个时，后面的等级会整个被截掉（生产上 A 级 1045 个，只剩 A）。
                var grades = await _context.Db.Queryable<ProductGrade>()
                    .Where(g => !g.IsDeleted && g.Grade != null && g.Grade != "")
                    .Select(g => g.Grade)
                    .Distinct()
                    .ToListAsync();

                // 固定四档始终列出（某档暂无商品也要能选，例如 D 级），库里出现的其他等级按字母序追加在后。
                var extraGrades = grades
                    .Select(grade => grade?.Trim().ToUpperInvariant())
                    .Where(grade => !string.IsNullOrEmpty(grade))
                    .Select(grade => grade!)
                    .Where(grade => !DefinedGrades.Contains(grade, StringComparer.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(grade => grade, StringComparer.Ordinal);
                var options = DefinedGrades.Concat(extraGrades).ToList();

                return ApiResponse<List<string>>.OK(options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品等级选项失败");
                return ApiResponse<List<string>>.Error("获取商品等级选项失败", "GET_GRADE_OPTIONS_ERROR");
            }
        }

        public async Task<ApiResponse<PagedResult<ProductGradeDto>>> GetProductGradesAsync(
            ProductGradeListQueryDto query
        )
        {
            try
            {
                // 服务层也做分页兜底，确保导出/测试或其他内部调用不会绕过 Controller 校验。
                query.Page = Math.Max(query.Page, 1);
                query.PageSize = Math.Clamp(query.PageSize, 1, 1000);

                var db = _context.Db;

                var gradeQuery = db.Queryable<ProductGrade>()
                    .LeftJoin<DomesticProduct>((g, d) => g.ProductCode == d.ProductCode)
                    .LeftJoin<ChinaSupplier>((g, d, s) => d.SupplierCode == s.SupplierCode)
                    .LeftJoin<WarehouseProduct>((g, d, s, w) => g.ProductCode == w.ProductCode)
                    // 仓库分类挂在本地 Product.ProductCode 对应的主档行上，不能用 Product.UUID 否则分类会全部为空。
                    .LeftJoin<Product>((g, d, s, w, p) => g.ProductCode == p.ProductCode)
                    .LeftJoin<WarehouseCategory>(
                        (g, d, s, w, p, c) => p.WarehouseCategoryGUID == c.CategoryGUID && !c.IsDeleted
                    )
                    .Where((g, d, s, w, p, c) => !g.IsDeleted);

                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) =>
                            (d.HBProductNo != null && d.HBProductNo.Contains(query.Search))
                            || (d.ProductName != null && d.ProductName.Contains(query.Search))
                            || (s.SupplierName != null && s.SupplierName.Contains(query.Search))
                            || (d.SupplierCode != null && d.SupplierCode.Contains(query.Search))
                    );
                }

                // 列头筛选必须在 Count/Skip/Take 之前进入数据库查询，避免只筛当前页导致总数和分页错误。
                if (!string.IsNullOrWhiteSpace(query.Grade))
                {
                    gradeQuery = gradeQuery.Where((g, d, s, w, p, c) => g.Grade == query.Grade);
                }

                if (!string.IsNullOrWhiteSpace(query.SupplierCode))
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) => d.SupplierCode == query.SupplierCode
                    );
                }

                if (!string.IsNullOrWhiteSpace(query.HbProductNo))
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) =>
                            d.HBProductNo != null && d.HBProductNo.Contains(query.HbProductNo)
                    );
                }

                if (query.DomesticPriceMin.HasValue)
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) => d.DomesticPrice >= query.DomesticPriceMin.Value
                    );
                }

                if (query.DomesticPriceMax.HasValue)
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) => d.DomesticPrice <= query.DomesticPriceMax.Value
                    );
                }

                if (query.ImportPriceMin.HasValue)
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) => w.ImportPrice >= query.ImportPriceMin.Value
                    );
                }

                if (query.ImportPriceMax.HasValue)
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) => w.ImportPrice <= query.ImportPriceMax.Value
                    );
                }

                if (query.OemPriceMin.HasValue)
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) => w.OEMPrice >= query.OemPriceMin.Value
                    );
                }

                if (query.OemPriceMax.HasValue)
                {
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) => w.OEMPrice <= query.OemPriceMax.Value
                    );
                }

                if (query.WarehouseIsActive.HasValue)
                {
                    // 仓库上下架过滤必须参与总数计算，并且先于分页执行，避免只过滤当前页。
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) => w.IsActive == query.WarehouseIsActive.Value
                    );
                }

                if (!string.IsNullOrWhiteSpace(query.CategoryGuid))
                {
                    var categoryGuids = await GetCategoryAndDescendantGuidsAsync(
                        db,
                        query.CategoryGuid.Trim()
                    );
                    // 分类筛选包含子分类，并且必须先于 Count/Skip/Take，避免分页后才裁剪。
                    gradeQuery = categoryGuids.Any()
                        ? gradeQuery.Where(
                            (g, d, s, w, p, c) =>
                                p.WarehouseCategoryGUID != null
                                && categoryGuids.Contains(p.WarehouseCategoryGUID)
                        )
                        : gradeQuery.Where((g, d, s, w, p, c) => g.ProductCode == "__NO_MATCH_CATEGORY__");
                }
                else if (query.UncategorizedOnly == true)
                {
                    // 未分类按 Product.WarehouseCategoryGUID 为空判断；已删除分类引用不额外归入未分类。
                    gradeQuery = gradeQuery.Where(
                        (g, d, s, w, p, c) =>
                            p.WarehouseCategoryGUID == null || p.WarehouseCategoryGUID == string.Empty
                    );
                }

                if (!string.IsNullOrEmpty(query.SortField))
                {
                    var isDesc = query.SortDirection?.ToLower() == "desc";
                    // 列头排序同样走服务端，确保排序后的结果再分页。
                    gradeQuery = query.SortField.ToLower() switch
                    {
                        "grade" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => g.Grade)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => g.Grade),
                        "suppliercode" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => d.SupplierCode)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => d.SupplierCode),
                        "hbproductno" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => d.HBProductNo)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => d.HBProductNo),
                        "suppliername" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => s.SupplierName)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => s.SupplierName),
                        "domesticprice" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => d.DomesticPrice)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => d.DomesticPrice),
                        "importprice" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => w.ImportPrice)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => w.ImportPrice),
                        "oemprice" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => w.OEMPrice)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => w.OEMPrice),
                        "warehouseisactive" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => w.IsActive)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => w.IsActive),
                        "createdat" => isDesc
                            ? gradeQuery.OrderByDescending((g, d, s, w, p, c) => g.CreatedAt)
                            : gradeQuery.OrderBy((g, d, s, w, p, c) => g.CreatedAt),
                        _ => gradeQuery.OrderByDescending((g, d, s, w, p, c) => g.CreatedAt),
                    };
                }
                else
                {
                    gradeQuery = gradeQuery.OrderByDescending((g, d, s, w, p, c) => g.CreatedAt);
                }

                var totalCount = await gradeQuery.CountAsync();

                var items = await gradeQuery
                    .Select(
                        (g, d, s, w, p, c) =>
                            new ProductGradeDto
                            {
                                Id = g.Id,
                                ProductCode = g.ProductCode,
                                Grade = g.Grade,
                                SupplierCode = d.SupplierCode,
                                SupplierName = s.SupplierName,
                                HbProductNo = d.HBProductNo,
                                ProductName = d.ProductName,
                                ProductImage = d.ProductImage,
                                DomesticPrice = d.DomesticPrice,
                                ImportPrice = w.ImportPrice,
                                OemPrice = w.OEMPrice,
                                RetailPrice = p.RetailPrice,
                                WarehouseIsActive = w.IsActive,
                                MinOrderQuantity = w.MinOrderQuantity,
                                CategoryGuid = p.WarehouseCategoryGUID,
                                CategoryName = c.CategoryName,
                                CategoryChineseName = c.ChineseName,
                                Barcode = d.Barcode,
                                CreatedAt = g.CreatedAt,
                                UpdatedAt = g.UpdatedAt,
                                CreatedBy = g.CreatedBy,
                                UpdatedBy = g.UpdatedBy,
                            }
                    )
                    .Skip(query.Skip)
                    .Take(query.Take)
                    .ToListAsync();

                var result = new PagedResult<ProductGradeDto>
                {
                    Items = items,
                    Total = totalCount,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<ProductGradeDto>>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品等级列表失败");
                return ApiResponse<PagedResult<ProductGradeDto>>.Error(
                    "获取商品等级列表失败",
                    "GET_GRADES_ERROR"
                );
            }
        }

        public async Task<ApiResponse<ProductGradeDto>> CreateOrUpdateProductGradeAsync(
            CreateProductGradeDto dto
        )
        {
            try
            {
                var db = _context.Db;

                var existing = await db.Queryable<ProductGrade>()
                    .Where(g => g.ProductCode == dto.ProductCode && !g.IsDeleted)
                    .FirstAsync();

                if (existing != null)
                {
                    existing.Grade = dto.Grade;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.UpdatedBy = "System";
                    await db.Updateable(existing).ExecuteCommandAsync();

                    var updateDto = await BuildGradeDto(db, existing);
                    return ApiResponse<ProductGradeDto>.OK(updateDto);
                }

                var entity = new ProductGrade
                {
                    Id = UuidHelper.GenerateUuid7(),
                    ProductCode = dto.ProductCode,
                    Grade = dto.Grade,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CreatedBy = "System",
                    UpdatedBy = "System",
                };

                await db.Insertable(entity).ExecuteCommandAsync();

                var newDto = await BuildGradeDto(db, entity);
                return ApiResponse<ProductGradeDto>.OK(newDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "创建/更新商品等级失败，ProductCode: {ProductCode}",
                    dto.ProductCode
                );
                return ApiResponse<ProductGradeDto>.Error(
                    "创建/更新商品等级失败",
                    "CREATE_GRADE_ERROR"
                );
            }
        }

        public async Task<ApiResponse<bool>> BatchUpdateGradesAsync(BatchUpdateGradeDto dto)
        {
            try
            {
                var db = _context.Db;
                var productCodes = dto.Items.Select(i => i.ProductCode).Distinct().ToList();
                var gradeLookup = dto.Items.ToDictionary(i => i.ProductCode, i => i.Grade);

                var existingGrades = await db.Queryable<ProductGrade>()
                    .Where(g => productCodes.Contains(g.ProductCode) && !g.IsDeleted)
                    .ToListAsync();

                var existingCodeSet = new HashSet<string>(
                    existingGrades.Select(g => g.ProductCode)
                );
                var now = DateTime.UtcNow;

                foreach (var eg in existingGrades)
                {
                    if (gradeLookup.TryGetValue(eg.ProductCode, out var grade))
                    {
                        eg.Grade = grade;
                        eg.UpdatedAt = now;
                        eg.UpdatedBy = "System";
                    }
                }

                if (existingGrades.Count > 0)
                {
                    await db.Updateable(existingGrades).ExecuteCommandAsync();
                }

                var newCodes = productCodes.Where(c => !existingCodeSet.Contains(c)).ToList();
                if (newCodes.Count > 0)
                {
                    var newEntities = newCodes
                        .Select(c => new ProductGrade
                        {
                            Id = UuidHelper.GenerateUuid7(),
                            ProductCode = c,
                            Grade = gradeLookup[c],
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = "System",
                            UpdatedBy = "System",
                        })
                        .ToList();

                    await db.Insertable(newEntities).ExecuteCommandAsync();
                }

                _logger.LogInformation("批量更新商品等级成功，共 {Total} 条", dto.Items.Count);
                return ApiResponse<bool>.OK(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量更新商品等级失败");
                return ApiResponse<bool>.Error("批量更新商品等级失败", "BATCH_UPDATE_GRADE_ERROR");
            }
        }

        public async Task<ApiResponse<PasteImportResultDto>> PasteImportGradesAsync(
            PasteImportGradeDto dto
        )
        {
            try
            {
                var db = _context.Db;

                var productNumbers = dto
                    .ProductNumbers.Split(
                        new[] { '\n', '\r', '\t', ',' },
                        StringSplitOptions.RemoveEmptyEntries
                    )
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .ToList();

                if (productNumbers.Count == 0)
                {
                    return ApiResponse<PasteImportResultDto>.Error(
                        "货号列表为空",
                        "EMPTY_PRODUCT_NUMBERS"
                    );
                }

                var matchedProducts = await db.Queryable<DomesticProduct>()
                    .Where(d => d.SupplierCode == dto.SupplierCode && !d.IsDeleted)
                    .Where(d => d.HBProductNo != null && productNumbers.Contains(d.HBProductNo))
                    .ToListAsync();

                var matchedByNo = matchedProducts.ToDictionary(d => d.HBProductNo!);

                var previewItems = productNumbers
                    .Select(no =>
                    {
                        var matched = matchedByNo.TryGetValue(no, out var dp);
                        return new PasteImportPreviewItem
                        {
                            ProductNumber = no,
                            Matched = matched,
                            ProductCode = matched ? dp!.ProductCode : null,
                            ProductName = matched ? dp!.ProductName : null,
                            ProductImage = matched ? dp!.ProductImage : null,
                        };
                    })
                    .ToList();

                var matchedCodes = matchedProducts.Select(d => d.ProductCode).ToList();

                var existingGrades = await db.Queryable<ProductGrade>()
                    .Where(g => matchedCodes.Contains(g.ProductCode) && !g.IsDeleted)
                    .ToListAsync();

                var existingGradeMap = existingGrades.ToDictionary(g => g.ProductCode);

                foreach (var pi in previewItems.Where(p => p.Matched && p.ProductCode != null))
                {
                    if (existingGradeMap.TryGetValue(pi.ProductCode!, out var eg))
                    {
                        pi.ExistingGrade = eg.Grade;
                    }
                }

                var now = DateTime.UtcNow;
                var createdCount = 0;
                var updatedCount = 0;

                foreach (var eg in existingGrades.Where(g => matchedCodes.Contains(g.ProductCode)))
                {
                    eg.Grade = dto.Grade;
                    eg.UpdatedAt = now;
                    eg.UpdatedBy = "System";
                    updatedCount++;
                }

                if (existingGrades.Count > 0)
                {
                    await db.Updateable(existingGrades).ExecuteCommandAsync();
                }

                var existingCodeSet = new HashSet<string>(
                    existingGrades.Select(g => g.ProductCode)
                );
                var newCodes = matchedCodes.Where(c => !existingCodeSet.Contains(c)).ToList();

                if (newCodes.Count > 0)
                {
                    var newEntities = newCodes
                        .Select(c => new ProductGrade
                        {
                            Id = UuidHelper.GenerateUuid7(),
                            ProductCode = c,
                            Grade = dto.Grade,
                            CreatedAt = now,
                            UpdatedAt = now,
                            CreatedBy = "System",
                            UpdatedBy = "System",
                        })
                        .ToList();

                    await db.Insertable(newEntities).ExecuteCommandAsync();
                    createdCount = newCodes.Count;
                }

                var result = new PasteImportResultDto
                {
                    TotalCount = productNumbers.Count,
                    MatchedCount = matchedProducts.Count,
                    CreatedCount = createdCount,
                    UpdatedCount = updatedCount,
                    PreviewItems = previewItems,
                };

                _logger.LogInformation(
                    "粘贴导入商品等级完成，总计 {Total}，匹配 {Matched}，新建 {Created}，更新 {Updated}",
                    result.TotalCount,
                    result.MatchedCount,
                    result.CreatedCount,
                    result.UpdatedCount
                );

                return ApiResponse<PasteImportResultDto>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "粘贴导入商品等级失败");
                return ApiResponse<PasteImportResultDto>.Error(
                    "粘贴导入商品等级失败",
                    "PASTE_IMPORT_ERROR"
                );
            }
        }

        public async Task<ApiResponse<bool>> DeleteProductGradeAsync(string id)
        {
            try
            {
                var db = _context.Db;

                var grade = await db.Queryable<ProductGrade>()
                    .Where(g => g.Id == id && !g.IsDeleted)
                    .FirstAsync();

                if (grade == null)
                {
                    return ApiResponse<bool>.Error("商品等级不存在", "GRADE_NOT_FOUND");
                }

                grade.IsDeleted = true;
                grade.UpdatedAt = DateTime.UtcNow;
                grade.UpdatedBy = "System";

                await db.Updateable(grade).ExecuteCommandAsync();

                _logger.LogInformation("删除商品等级成功，Id: {Id}", id);
                return ApiResponse<bool>.OK(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除商品等级失败，Id: {Id}", id);
                return ApiResponse<bool>.Error("删除商品等级失败", "DELETE_GRADE_ERROR");
            }
        }

        public async Task<ApiResponse<List<ProductGradeDto>>> GetProductGradesByProductCodesAsync(
            List<string> productCodes
        )
        {
            try
            {
                if (productCodes == null || productCodes.Count == 0)
                {
                    return ApiResponse<List<ProductGradeDto>>.OK(new List<ProductGradeDto>());
                }

                var db = _context.Db;

                var normalizedProductCodes = productCodes
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct()
                    .ToList();

                if (!normalizedProductCodes.Any())
                {
                    return ApiResponse<List<ProductGradeDto>>.OK(new List<ProductGradeDto>());
                }

                // 导出已选商品需要完整列表字段，保持 by-codes 端点一次性回齐图片、供应商和价格信息。
                var grades = await db.Queryable<ProductGrade>()
                    .LeftJoin<DomesticProduct>((g, d) => g.ProductCode == d.ProductCode)
                    .LeftJoin<ChinaSupplier>((g, d, s) => d.SupplierCode == s.SupplierCode)
                    .LeftJoin<WarehouseProduct>((g, d, s, w) => g.ProductCode == w.ProductCode)
                    // by-codes 回查同样按本地商品编码关联，保证导出/加入订单能带回分类字段。
                    .LeftJoin<Product>((g, d, s, w, p) => g.ProductCode == p.ProductCode)
                    .LeftJoin<WarehouseCategory>(
                        (g, d, s, w, p, c) => p.WarehouseCategoryGUID == c.CategoryGUID && !c.IsDeleted
                    )
                    .Where(
                        (g, d, s, w, p, c) =>
                            normalizedProductCodes.Contains(g.ProductCode) && !g.IsDeleted
                    )
                    .Select(
                        (g, d, s, w, p, c) =>
                            new ProductGradeDto
                            {
                                Id = g.Id,
                                ProductCode = g.ProductCode,
                                Grade = g.Grade,
                                SupplierCode = d.SupplierCode,
                                SupplierName = s.SupplierName,
                                HbProductNo = d.HBProductNo,
                                ProductName = d.ProductName,
                                ProductImage = d.ProductImage,
                                DomesticPrice = d.DomesticPrice,
                                ImportPrice = w.ImportPrice,
                                OemPrice = w.OEMPrice,
                                RetailPrice = p.RetailPrice,
                                WarehouseIsActive = w.IsActive,
                                MinOrderQuantity = w.MinOrderQuantity,
                                CategoryGuid = p.WarehouseCategoryGUID,
                                CategoryName = c.CategoryName,
                                CategoryChineseName = c.ChineseName,
                                Barcode = d.Barcode,
                                CreatedAt = g.CreatedAt,
                                UpdatedAt = g.UpdatedAt,
                                CreatedBy = g.CreatedBy,
                                UpdatedBy = g.UpdatedBy,
                            }
                    )
                    .ToListAsync();

                return ApiResponse<List<ProductGradeDto>>.OK(grades);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量查询商品等级失败");
                return ApiResponse<List<ProductGradeDto>>.Error(
                    "批量查询商品等级失败",
                    "GET_GRADES_BY_CODES_ERROR"
                );
            }
        }

        private async Task<ProductGradeDto> BuildGradeDto(ISqlSugarClient db, ProductGrade entity)
        {
            var row = await db.Queryable<ProductGrade>()
                .LeftJoin<DomesticProduct>((g, d) => g.ProductCode == d.ProductCode)
                .LeftJoin<ChinaSupplier>((g, d, s) => d.SupplierCode == s.SupplierCode)
                .LeftJoin<WarehouseProduct>((g, d, s, w) => g.ProductCode == w.ProductCode)
                // 创建/更新后回读要和列表保持同一关联键，避免返回 DTO 丢失分类。
                .LeftJoin<Product>((g, d, s, w, p) => g.ProductCode == p.ProductCode)
                .LeftJoin<WarehouseCategory>(
                    (g, d, s, w, p, c) => p.WarehouseCategoryGUID == c.CategoryGUID && !c.IsDeleted
                )
                .Where((g, d, s, w, p, c) => g.Id == entity.Id)
                .Select(
                    (g, d, s, w, p, c) =>
                        new ProductGradeDto
                        {
                            Id = g.Id,
                            ProductCode = g.ProductCode,
                            Grade = g.Grade,
                            SupplierCode = d.SupplierCode,
                            SupplierName = s.SupplierName,
                            HbProductNo = d.HBProductNo,
                            ProductName = d.ProductName,
                            ProductImage = d.ProductImage,
                            DomesticPrice = d.DomesticPrice,
                            ImportPrice = w.ImportPrice,
                            OemPrice = w.OEMPrice,
                            RetailPrice = p.RetailPrice,
                            WarehouseIsActive = w.IsActive,
                            MinOrderQuantity = w.MinOrderQuantity,
                            CategoryGuid = p.WarehouseCategoryGUID,
                            CategoryName = c.CategoryName,
                            CategoryChineseName = c.ChineseName,
                            Barcode = d.Barcode,
                            CreatedAt = g.CreatedAt,
                            UpdatedAt = g.UpdatedAt,
                            CreatedBy = g.CreatedBy,
                            UpdatedBy = g.UpdatedBy,
                        }
                )
                .FirstAsync();

            return row ?? _mapper.Map<ProductGradeDto>(entity);
        }

        private static async Task<List<string>> GetCategoryAndDescendantGuidsAsync(
            ISqlSugarClient db,
            string categoryGuid
        )
        {
            if (string.IsNullOrWhiteSpace(categoryGuid))
            {
                return new List<string>();
            }

            var categories = await db.Queryable<WarehouseCategory>()
                .Where(c => !c.IsDeleted)
                .Select(c => new WarehouseCategory
                {
                    CategoryGUID = c.CategoryGUID,
                    ParentGUID = c.ParentGUID,
                })
                .ToListAsync();

            if (!categories.Any(c => c.CategoryGUID == categoryGuid))
            {
                return new List<string>();
            }

            var result = new HashSet<string> { categoryGuid };
            var pending = new Queue<string>();
            pending.Enqueue(categoryGuid);

            // 分类列头过滤沿用仓库分类语义：选择父分类时同时包含所有子分类商品。
            while (pending.Count > 0)
            {
                var parentGuid = pending.Dequeue();
                foreach (var child in categories.Where(c => c.ParentGUID == parentGuid))
                {
                    if (result.Add(child.CategoryGUID))
                    {
                        pending.Enqueue(child.CategoryGUID);
                    }
                }
            }

            return result.ToList();
        }

        public async Task<ApiResponse<BatchUpdateGradePriceResult>> BatchUpdateGradePriceAsync(
            BatchUpdateGradePriceDto dto
        )
        {
            try
            {
                if (dto.ImportPrice == null && dto.OEMPrice == null)
                {
                    return ApiResponse<BatchUpdateGradePriceResult>.Error(
                        "至少需要填写一个价格",
                        "NO_PRICE_PROVIDED"
                    );
                }

                var db = _context.Db;
                var productCodes = dto.ProductCodes
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (productCodes.Count == 0)
                {
                    return ApiResponse<BatchUpdateGradePriceResult>.Error(
                        "商品编码不能为空",
                        "NO_PRODUCT_CODE"
                    );
                }
                var totalAffected = 0;

                if (dto.TargetDatabase.Equals("HQ", StringComparison.OrdinalIgnoreCase))
                {
                    var hqDb = _hqContext.Db;
                    var now = DateTime.Now;

                    var hqInfoUpdate =
                        hqDb.Updateable<BlazorApp.Shared.Models.HqEntities.DIC_商品信息字典表>()
                            .Where(p => p.H商品编码 != null && productCodes.Contains(p.H商品编码))
                            .SetColumns(p => p.FGC_LastModifier == "HBweb")
                            .SetColumns(p => p.FGC_LastModifyDate == now);
                    if (dto.ImportPrice.HasValue)
                        hqInfoUpdate = hqInfoUpdate.SetColumns(p =>
                            p.H进货价 == dto.ImportPrice.Value
                        );
                    if (dto.OEMPrice.HasValue)
                        hqInfoUpdate = hqInfoUpdate.SetColumns(p =>
                            p.H零售价 == dto.OEMPrice.Value
                        );
                    totalAffected += await hqInfoUpdate.ExecuteCommandAsync();

                    var hqStockUpdate =
                        hqDb.Updateable<BlazorApp.Shared.Models.HqEntities.CBP_DIC_商品库存表>()
                            .Where(p => p.H商品编码 != null && productCodes.Contains(p.H商品编码))
                            .SetColumns(p => p.FGC_LastModifier == "HBweb")
                            .SetColumns(p => p.FGC_LastModifyDate == now);
                    if (dto.ImportPrice.HasValue)
                        hqStockUpdate = hqStockUpdate.SetColumns(p =>
                            p.H进口价格 == dto.ImportPrice.Value
                        );
                    if (dto.OEMPrice.HasValue)
                        hqStockUpdate = hqStockUpdate.SetColumns(p =>
                            p.H贴牌价格 == dto.OEMPrice.Value
                        );
                    totalAffected += await hqStockUpdate.ExecuteCommandAsync();

                    var hqRetailUpdate =
                        hqDb.Updateable<BlazorApp.Shared.Models.HqEntities.DIC_商品零售价表>()
                            .Where(p => productCodes.Contains(p.H商品编码))
                            .SetColumns(p => p.FGC_LastModifier == "HBweb")
                            .SetColumns(p => p.FGC_LastModifyDate == now);
                    if (dto.ImportPrice.HasValue)
                        hqRetailUpdate = hqRetailUpdate.SetColumns(p =>
                            p.H进货价 == dto.ImportPrice.Value
                        );
                    if (dto.OEMPrice.HasValue)
                        hqRetailUpdate = hqRetailUpdate.SetColumns(p =>
                            p.H分店零售价 == dto.OEMPrice.Value
                        );
                    totalAffected += await hqRetailUpdate.ExecuteCommandAsync();
                }
                else
                {
                    var batchGuid = Guid.NewGuid();
                    var actorName = _currentUserService.GetCurrentUsername();
                    var actorGuid = _currentUserService.GetCurrentUserGuid();
                    var transactionResult = await db.Ado.UseTranAsync(async () =>
                    {
                        var lockScope = await SetChildPurchasePriceMutationLock
                            .AcquireProductsAsync(db, productCodes);
                        var beforeSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(productCodes);
                        if (dto.ImportPrice.HasValue || dto.OEMPrice.HasValue)
                        {
                            var dpUpdate = db.Updateable<DomesticProduct>()
                                .Where(d => productCodes.Contains(d.ProductCode) && !d.IsDeleted);
                            if (dto.ImportPrice.HasValue)
                                dpUpdate = dpUpdate.SetColumns(d =>
                                    d.ImportPrice == dto.ImportPrice.Value
                                );
                            if (dto.OEMPrice.HasValue)
                                dpUpdate = dpUpdate.SetColumns(d =>
                                    d.OEMPrice == dto.OEMPrice.Value
                                );
                            dpUpdate = dpUpdate.SetColumns(d => d.UpdatedAt == DateTime.UtcNow)
                                .SetColumns(d => d.UpdatedBy == actorName);
                            totalAffected += await dpUpdate.ExecuteCommandAsync();

                            if (dto.ImportPrice.HasValue)
                            {
                                var wpList = await db.Queryable<WarehouseProduct>()
                                    .Where(wp =>
                                        productCodes.Contains(wp.ProductCode)
                                        && !wp.IsDeleted
                                        && wp.StockQuantity != null
                                    )
                                    .ToListAsync();
                                foreach (var wp in wpList)
                                {
                                    wp.ImportPrice = dto.ImportPrice.Value;
                                    wp.StockValue = wp.StockQuantity!.Value * dto.ImportPrice.Value;
                                    if (dto.OEMPrice.HasValue)
                                        wp.OEMPrice = dto.OEMPrice.Value;
                                    wp.UpdatedAt = DateTime.UtcNow;
                                    wp.UpdatedBy = actorName;
                                }
                                if (wpList.Count > 0)
                                    totalAffected += await db.Updateable(wpList)
                                        .ExecuteCommandAsync();

                                var wpNoStock = db.Updateable<WarehouseProduct>()
                                    .Where(wp =>
                                        productCodes.Contains(wp.ProductCode)
                                        && !wp.IsDeleted
                                        && wp.StockQuantity == null
                                    )
                                    .SetColumns(wp => wp.ImportPrice == dto.ImportPrice.Value)
                                        .SetColumns(wp => wp.UpdatedAt == DateTime.UtcNow)
                                        .SetColumns(wp => wp.UpdatedBy == actorName);
                                if (dto.OEMPrice.HasValue)
                                    wpNoStock = wpNoStock.SetColumns(wp =>
                                        wp.OEMPrice == dto.OEMPrice.Value
                                    );
                                totalAffected += await wpNoStock.ExecuteCommandAsync();
                            }
                            else if (dto.OEMPrice.HasValue)
                            {
                                var wpUpdate = db.Updateable<WarehouseProduct>()
                                    .Where(wp =>
                                        productCodes.Contains(wp.ProductCode) && !wp.IsDeleted
                                    )
                                    .SetColumns(wp => wp.OEMPrice == dto.OEMPrice.Value)
                                    .SetColumns(wp => wp.UpdatedAt == DateTime.UtcNow)
                                    .SetColumns(wp => wp.UpdatedBy == actorName);
                                totalAffected += await wpUpdate.ExecuteCommandAsync();
                            }

                            var pUpdate = db.Updateable<Product>()
                                .Where(p =>
                                    p.ProductCode != null
                                    && productCodes.Contains(p.ProductCode)
                                    && !p.IsDeleted
                                );
                            if (dto.ImportPrice.HasValue)
                                pUpdate = pUpdate.SetColumns(p =>
                                    p.PurchasePrice == dto.ImportPrice.Value
                                );
                            if (dto.OEMPrice.HasValue)
                                pUpdate = pUpdate.SetColumns(p =>
                                    p.RetailPrice == dto.OEMPrice.Value
                                );
                            pUpdate = pUpdate.SetColumns(p => p.UpdatedAt == DateTime.UtcNow)
                                .SetColumns(p => p.UpdatedBy == actorName);
                            totalAffected += await pUpdate.ExecuteCommandAsync();

                            var srpUpdate = db.Updateable<StoreRetailPrice>()
                                .Where(srp =>
                                    srp.ProductCode != null
                                    && productCodes.Contains(srp.ProductCode)
                                    && !srp.IsDeleted
                                );
                            if (dto.ImportPrice.HasValue)
                                srpUpdate = srpUpdate.SetColumns(srp =>
                                    srp.PurchasePrice == dto.ImportPrice.Value
                                );
                            if (dto.OEMPrice.HasValue)
                                srpUpdate = srpUpdate.SetColumns(srp =>
                                    srp.StoreRetailPriceValue == dto.OEMPrice.Value
                                );
                            srpUpdate = srpUpdate.SetColumns(srp =>
                                srp.UpdatedAt == DateTime.UtcNow
                            );
                            totalAffected += await srpUpdate.ExecuteCommandAsync();

                            if (dto.ImportPrice.HasValue)
                            {
                                var recalculation = await new SetChildPurchasePriceService(db)
                                    .RecalculateLockedAsync(
                                        lockScope,
                                        productCodes,
                                        storeCodes: null,
                                        updatedBy: actorName
                                    );
                                if (
                                    recalculation.ProductSetCode.SkippedGroupCount > 0
                                    || recalculation.StoreMultiCodeProduct.SkippedGroupCount > 0
                                )
                                {
                                    throw new InvalidOperationException(
                                        recalculation.Errors.FirstOrDefault()?.Reason
                                            ?? "套装与多码商品成本无法完整校正"
                                    );
                                }
                            }
                        }

                        var afterSnapshots = await _changeHistoryService.CaptureSnapshotsAsync(productCodes);
                        await _changeHistoryService.RecordChangesAsync(
                            beforeSnapshots,
                            afterSnapshots,
                            new WarehouseProductChangeHistoryContextDto
                            {
                                Action = "BatchUpdate",
                                Source = "ProductGrade",
                                SourceReference = $"ProductGrade:{string.Join(",", productCodes)}",
                                BatchGuid = batchGuid,
                                ActorUserGuid = actorGuid,
                                ActorName = actorName,
                            }
                        );
                    });
                    if (!transactionResult.IsSuccess)
                    {
                        throw transactionResult.ErrorException
                            ?? new InvalidOperationException("商品等级批量改价事务失败");
                    }
                }

                _logger.LogInformation(
                    "批量修改商品等级价格成功，数据库: {Db}，影响行数: {Count}",
                    dto.TargetDatabase,
                    totalAffected
                );
                return ApiResponse<BatchUpdateGradePriceResult>.OK(
                    new BatchUpdateGradePriceResult
                    {
                        AffectedCount = totalAffected,
                        Success = true,
                        Message = $"成功更新 {totalAffected} 条记录",
                    }
                );
            }
            catch (Exception ex) when (
                SetChildPurchasePriceMutationLock.TryResolveConflict(ex, out _)
            )
            {
                _logger.LogWarning(ex, "批量修改商品等级价格遇到套装成本业务锁冲突");
                return ApiResponse<BatchUpdateGradePriceResult>.Error(
                    "套装商品正在被其他操作修改，请稍后重试",
                    SetChildPurchasePriceMutationLock.BusyErrorCode
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量修改商品等级价格失败");
                return ApiResponse<BatchUpdateGradePriceResult>.Error(
                    "批量修改价格失败",
                    "BATCH_PRICE_ERROR"
                );
            }
        }
    }
}
