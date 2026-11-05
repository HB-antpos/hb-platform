using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 商品前缀管理服务
    /// </summary>
    public class ProductPrefixCodeService : IProductPrefixCodeService
    {
        private readonly SqlSugarContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<ProductPrefixCodeService> _logger;

        public ProductPrefixCodeService(
            SqlSugarContext context,
            IMapper mapper,
            ILogger<ProductPrefixCodeService> logger)
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        /// <summary>
        /// 获取商品前缀分页列表
        /// </summary>
        public async Task<ApiResponse<PagedResult<ProductPrefixCodeDto>>> GetProductPrefixCodesAsync(ProductPrefixCodeQueryDto query)
        {
            try
            {
                var db = _context.Db;

                // 构建查询
                var prefixQuery = db.Queryable<ProductPrefixCode>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => !p.IsDeleted);

                // 应用搜索条件
                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    // 没有「查看国内供应商名称」权限时，名称这一支换成匹配不到的占位关键词。
                    var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(query.Search);
                    prefixQuery = prefixQuery.Where((p, s) =>
                        p.PrefixName.Contains(query.Search) ||
                        (p.PrefixDescription != null && p.PrefixDescription.Contains(query.Search)) ||
                        (s.SupplierName != null && s.SupplierName.Contains(supplierNameKeyword)));
                }

                if (!string.IsNullOrWhiteSpace(query.SupplierCode))
                {
                    prefixQuery = prefixQuery.Where((p, s) => p.SupplierCode == query.SupplierCode);
                }

                if (query.IsActive.HasValue)
                {
                    prefixQuery = prefixQuery.Where((p, s) => p.IsActive == query.IsActive.Value);
                }

                // 应用排序
                if (!string.IsNullOrEmpty(query.SortField))
                {
                    var isDescending = !string.IsNullOrEmpty(query.SortDirection) &&
                                     query.SortDirection.ToLower() == "desc";

                    prefixQuery = query.SortField.ToLower() switch
                    {
                        "prefixname" => isDescending
                            ? prefixQuery.OrderByDescending((p, s) => p.PrefixName)
                            : prefixQuery.OrderBy((p, s) => p.PrefixName),
                        "suppliercode" => isDescending
                            ? prefixQuery.OrderByDescending((p, s) => p.SupplierCode)
                            : prefixQuery.OrderBy((p, s) => p.SupplierCode),
                        "isactive" => isDescending
                            ? prefixQuery.OrderByDescending((p, s) => p.IsActive)
                            : prefixQuery.OrderBy((p, s) => p.IsActive),
                        "sortorder" => isDescending
                            ? prefixQuery.OrderByDescending((p, s) => p.SortOrder)
                            : prefixQuery.OrderBy((p, s) => p.SortOrder),
                        "createdat" => isDescending
                            ? prefixQuery.OrderByDescending((p, s) => p.CreatedAt)
                            : prefixQuery.OrderBy((p, s) => p.CreatedAt),
                        _ => prefixQuery.OrderBy((p, s) => p.SortOrder).OrderBy((p, s) => p.CreatedAt)
                    };
                }
                else
                {
                    prefixQuery = prefixQuery.OrderBy((p, s) => p.SortOrder).OrderBy((p, s) => p.CreatedAt);
                }

                // 获取总数
                var totalCount = await prefixQuery.CountAsync();

                // 分页查询
                var prefixes = await prefixQuery
                    .Select((p, s) => new ProductPrefixCode
                    {
                        PrefixCode = p.PrefixCode,
                        SupplierCode = p.SupplierCode,
                        PrefixName = p.PrefixName,
                        PrefixDescription = p.PrefixDescription,
                        IsActive = p.IsActive,
                        SortOrder = p.SortOrder,
                        CreatedAt = p.CreatedAt,
                        UpdatedAt = p.UpdatedAt,
                        CreatedBy = p.CreatedBy,
                        UpdatedBy = p.UpdatedBy,
                        Supplier = new ChinaSupplier { SupplierName = s.SupplierName }
                    })
                    .Skip(query.Skip)
                    .Take(query.Take)
                    .ToListAsync();

                // 映射到DTO
                var prefixDtos = _mapper.Map<List<ProductPrefixCodeDto>>(prefixes);

                var result = new PagedResult<ProductPrefixCodeDto>
                {
                    Items = prefixDtos,
                    Total = totalCount,
                    Page = query.Page,
                    PageSize = query.PageSize
                };

                return ApiResponse<PagedResult<ProductPrefixCodeDto>>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品前缀列表失败");
                return ApiResponse<PagedResult<ProductPrefixCodeDto>>.Error("获取商品前缀列表失败", "GET_PREFIX_LIST_ERROR");
            }
        }

        /// <summary>
        /// 根据编码获取商品前缀详情
        /// </summary>
        public async Task<ApiResponse<ProductPrefixCodeDetailDto>> GetProductPrefixCodeByCodeAsync(string prefixCode)
        {
            try
            {
                var db = _context.Db;

                var prefix = await db.Queryable<ProductPrefixCode>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => p.PrefixCode == prefixCode && !p.IsDeleted)
                    .Select((p, s) => new ProductPrefixCode
                    {
                        PrefixCode = p.PrefixCode,
                        SupplierCode = p.SupplierCode,
                        PrefixName = p.PrefixName,
                        PrefixDescription = p.PrefixDescription,
                        IsActive = p.IsActive,
                        SortOrder = p.SortOrder,
                        CreatedAt = p.CreatedAt,
                        UpdatedAt = p.UpdatedAt,
                        CreatedBy = p.CreatedBy,
                        UpdatedBy = p.UpdatedBy,
                        Supplier = new ChinaSupplier
                        {
                            SupplierCode = s.SupplierCode,
                            SupplierName = s.SupplierName
                        }
                    })
                    .FirstAsync();

                if (prefix == null)
                {
                    return ApiResponse<ProductPrefixCodeDetailDto>.Error("商品前缀不存在", "PREFIX_NOT_FOUND");
                }

                // 获取使用该前缀的商品数量
                var productCount = await db.Queryable<DomesticProduct>()
                    .Where(p => p.SupplierCode == prefix.SupplierCode && p.HBProductNo != null && p.HBProductNo.Contains($"-{prefix.PrefixName}-") && !p.IsDeleted)
                    .CountAsync();

                var prefixDetailDto = _mapper.Map<ProductPrefixCodeDetailDto>(prefix);
                prefixDetailDto.ProductCount = productCount;

                return ApiResponse<ProductPrefixCodeDetailDto>.OK(prefixDetailDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取商品前缀详情失败，PrefixCode: {PrefixCode}", prefixCode);
                return ApiResponse<ProductPrefixCodeDetailDto>.Error("获取商品前缀详情失败", "GET_PREFIX_DETAIL_ERROR");
            }
        }

        /// <summary>
        /// 根据供应商编码获取前缀列表
        /// </summary>
        public async Task<ApiResponse<List<SimpleProductPrefixCodeDto>>> GetPrefixesBySupplierCodeAsync(string supplierCode)
        {
            try
            {
                var db = _context.Db;

                var prefixes = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.SupplierCode == supplierCode && !p.IsDeleted)
                    .OrderBy(p => p.SortOrder)
                    .OrderBy(p => p.PrefixName)
                    .ToListAsync();

                var prefixDtos = _mapper.Map<List<SimpleProductPrefixCodeDto>>(prefixes);
                return ApiResponse<List<SimpleProductPrefixCodeDto>>.OK(prefixDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取供应商前缀列表失败，SupplierCode: {SupplierCode}", supplierCode);
                return ApiResponse<List<SimpleProductPrefixCodeDto>>.Error("获取供应商前缀列表失败", "GET_SUPPLIER_PREFIXES_ERROR");
            }
        }

        /// <summary>
        /// 获取启用的前缀列表
        /// </summary>
        public async Task<ApiResponse<List<SimpleProductPrefixCodeDto>>> GetActivePrefixesAsync(string? supplierCode = null)
        {
            try
            {
                var db = _context.Db;

                var query = db.Queryable<ProductPrefixCode>()
                    .Where(p => p.IsActive && !p.IsDeleted);

                if (!string.IsNullOrWhiteSpace(supplierCode))
                {
                    query = query.Where(p => p.SupplierCode == supplierCode);
                }

                var prefixes = await query
                    .OrderBy(p => p.SortOrder)
                    .OrderBy(p => p.PrefixName)
                    .ToListAsync();

                var prefixDtos = _mapper.Map<List<SimpleProductPrefixCodeDto>>(prefixes);
                return ApiResponse<List<SimpleProductPrefixCodeDto>>.OK(prefixDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取启用前缀列表失败，SupplierCode: {SupplierCode}", supplierCode);
                return ApiResponse<List<SimpleProductPrefixCodeDto>>.Error("获取启用前缀列表失败", "GET_ACTIVE_PREFIXES_ERROR");
            }
        }

        /// <summary>
        /// 创建商品前缀
        /// </summary>
        public async Task<ApiResponse<ProductPrefixCodeDto>> CreateProductPrefixCodeAsync(CreateProductPrefixCodeDto dto)
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
                    return ApiResponse<ProductPrefixCodeDto>.Error("供应商不存在", "SUPPLIER_NOT_FOUND");
                }

                // 检查前缀代码是否已存在
                var existingPrefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.SupplierCode == dto.SupplierCode && p.PrefixName == dto.PrefixName && !p.IsDeleted)
                    .FirstAsync();

                if (existingPrefix != null)
                {
                    return ApiResponse<ProductPrefixCodeDto>.Error("该供应商的前缀代码已存在", "PREFIX_NAME_EXISTS");
                }

                // 创建新前缀
                var prefix = _mapper.Map<ProductPrefixCode>(dto);
                prefix.PrefixCode = UuidHelper.GenerateUuid7();
                prefix.CreatedAt = DateTime.Now;
                prefix.UpdatedAt = DateTime.Now;
                prefix.CreatedBy = "System"; // TODO: 从当前用户获取
                prefix.UpdatedBy = "System";

                await db.Insertable(prefix).ExecuteCommandAsync();

                var prefixDto = _mapper.Map<ProductPrefixCodeDto>(prefix);
                prefixDto.SupplierName = supplier.SupplierName;

                _logger.LogInformation("创建商品前缀成功，PrefixCode: {PrefixCode}", prefix.PrefixCode);
                return ApiResponse<ProductPrefixCodeDto>.OK(prefixDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建商品前缀失败");
                return ApiResponse<ProductPrefixCodeDto>.Error("创建商品前缀失败", "CREATE_PREFIX_ERROR");
            }
        }

        /// <summary>
        /// 更新商品前缀
        /// </summary>
        public async Task<ApiResponse<ProductPrefixCodeDto>> UpdateProductPrefixCodeAsync(string prefixCode, UpdateProductPrefixCodeDto dto)
        {
            try
            {
                var db = _context.Db;

                var prefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.PrefixCode == prefixCode && !p.IsDeleted)
                    .FirstAsync();

                if (prefix == null)
                {
                    return ApiResponse<ProductPrefixCodeDto>.Error("商品前缀不存在", "PREFIX_NOT_FOUND");
                }

                // 检查前缀代码是否已存在（排除当前记录）
                var existingPrefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.SupplierCode == prefix.SupplierCode &&
                               p.PrefixName == dto.PrefixName &&
                               p.PrefixCode != prefixCode &&
                               !p.IsDeleted)
                    .FirstAsync();

                if (existingPrefix != null)
                {
                    return ApiResponse<ProductPrefixCodeDto>.Error("该供应商的前缀代码已存在", "PREFIX_NAME_EXISTS");
                }

                // 更新前缀信息
                _mapper.Map(dto, prefix);
                prefix.UpdatedAt = DateTime.Now;
                prefix.UpdatedBy = "System"; // TODO: 从当前用户获取

                await db.Updateable(prefix).ExecuteCommandAsync();

                // 获取供应商信息
                var supplier = await db.Queryable<ChinaSupplier>()
                    .Where(s => s.SupplierCode == prefix.SupplierCode)
                    .FirstAsync();

                var prefixDto = _mapper.Map<ProductPrefixCodeDto>(prefix);
                prefixDto.SupplierName = supplier?.SupplierName;

                _logger.LogInformation("更新商品前缀成功，PrefixCode: {PrefixCode}", prefixCode);
                return ApiResponse<ProductPrefixCodeDto>.OK(prefixDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新商品前缀失败，PrefixCode: {PrefixCode}", prefixCode);
                return ApiResponse<ProductPrefixCodeDto>.Error("更新商品前缀失败", "UPDATE_PREFIX_ERROR");
            }
        }

        /// <summary>
        /// 删除商品前缀
        /// </summary>
        public async Task<ApiResponse<bool>> DeleteProductPrefixCodeAsync(string prefixCode)
        {
            try
            {
                var db = _context.Db;

                var prefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.PrefixCode == prefixCode && !p.IsDeleted)
                    .FirstAsync();

                if (prefix == null)
                {
                    return ApiResponse<bool>.Error("商品前缀不存在", "PREFIX_NOT_FOUND");
                }

                // 检查是否有商品使用该前缀
                var productCount = await db.Queryable<DomesticProduct>()
                    .Where(p => p.SupplierCode == prefix.SupplierCode && p.HBProductNo != null && p.HBProductNo.Contains($"-{prefix.PrefixName}-") && !p.IsDeleted)
                    .CountAsync();

                if (productCount > 0)
                {
                    return ApiResponse<bool>.Error($"该前缀已被 {productCount} 个商品使用，无法删除", "PREFIX_IN_USE");
                }

                // 软删除
                prefix.IsDeleted = true;
                prefix.UpdatedAt = DateTime.Now;
                prefix.UpdatedBy = "System"; // TODO: 从当前用户获取

                await db.Updateable(prefix).ExecuteCommandAsync();

                _logger.LogInformation("删除商品前缀成功，PrefixCode: {PrefixCode}", prefixCode);
                return ApiResponse<bool>.OK(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除商品前缀失败，PrefixCode: {PrefixCode}", prefixCode);
                return ApiResponse<bool>.Error("删除商品前缀失败", "DELETE_PREFIX_ERROR");
            }
        }

        /// <summary>
        /// 切换商品前缀状态
        /// </summary>
        public async Task<ApiResponse<ProductPrefixCodeDto>> TogglePrefixStatusAsync(string prefixCode, bool isActive)
        {
            try
            {
                var db = _context.Db;

                var prefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.PrefixCode == prefixCode && !p.IsDeleted)
                    .FirstAsync();

                if (prefix == null)
                {
                    return ApiResponse<ProductPrefixCodeDto>.Error("商品前缀不存在", "PREFIX_NOT_FOUND");
                }

                prefix.IsActive = isActive;
                prefix.UpdatedAt = DateTime.Now;
                prefix.UpdatedBy = "System"; // TODO: 从当前用户获取

                await db.Updateable(prefix).ExecuteCommandAsync();

                // 获取供应商信息
                var supplier = await db.Queryable<ChinaSupplier>()
                    .Where(s => s.SupplierCode == prefix.SupplierCode)
                    .FirstAsync();

                var prefixDto = _mapper.Map<ProductPrefixCodeDto>(prefix);
                prefixDto.SupplierName = supplier?.SupplierName;

                _logger.LogInformation("切换商品前缀状态成功，PrefixCode: {PrefixCode}, IsActive: {IsActive}", prefixCode, isActive);
                return ApiResponse<ProductPrefixCodeDto>.OK(prefixDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "切换商品前缀状态失败，PrefixCode: {PrefixCode}", prefixCode);
                return ApiResponse<ProductPrefixCodeDto>.Error("切换商品前缀状态失败", "TOGGLE_PREFIX_STATUS_ERROR");
            }
        }

        /// <summary>
        /// 检查前缀代码是否存在
        /// </summary>
        public async Task<ApiResponse<bool>> CheckPrefixNameExistsAsync(string supplierCode, string prefixName, string? excludePrefixCode = null)
        {
            try
            {
                var db = _context.Db;

                var query = db.Queryable<ProductPrefixCode>()
                    .Where(p => p.SupplierCode == supplierCode && p.PrefixName == prefixName && !p.IsDeleted);

                if (!string.IsNullOrWhiteSpace(excludePrefixCode))
                {
                    query = query.Where(p => p.PrefixCode != excludePrefixCode);
                }

                var exists = await query.AnyAsync();
                return ApiResponse<bool>.OK(exists);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查前缀代码是否存在失败，SupplierCode: {SupplierCode}, PrefixName: {PrefixName}", supplierCode, prefixName);
                return ApiResponse<bool>.Error("检查前缀代码是否存在失败", "CHECK_PREFIX_EXISTS_ERROR");
            }
        }

        /// <summary>
        /// 批量创建商品前缀
        /// </summary>
        public async Task<ApiResponse<List<ProductPrefixCodeDto>>> BatchCreateProductPrefixCodesAsync(BatchCreateProductPrefixCodeDto dto)
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
                    return ApiResponse<List<ProductPrefixCodeDto>>.Error("供应商不存在", "SUPPLIER_NOT_FOUND");
                }

                // 检查前缀代码是否已存在
                var existingPrefixNames = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.SupplierCode == dto.SupplierCode && !p.IsDeleted)
                    .Select(p => p.PrefixName)
                    .ToListAsync();

                var duplicatePrefixes = dto.Prefixes
                    .Where(p => existingPrefixNames.Contains(p.PrefixName))
                    .Select(p => p.PrefixName)
                    .ToList();

                if (duplicatePrefixes.Any())
                {
                    return ApiResponse<List<ProductPrefixCodeDto>>.Error(
                        $"以下前缀代码已存在: {string.Join(", ", duplicatePrefixes)}",
                        "PREFIX_NAMES_EXISTS");
                }

                // 批量创建前缀
                var prefixes = new List<ProductPrefixCode>();
                var now = DateTime.Now;

                foreach (var prefixItem in dto.Prefixes)
                {
                    var prefix = new ProductPrefixCode
                    {
                        PrefixCode = UuidHelper.GenerateUuid7(),
                        SupplierCode = dto.SupplierCode,
                        PrefixName = prefixItem.PrefixName,
                        PrefixDescription = prefixItem.PrefixDescription,
                        IsActive = true,
                        SortOrder = prefixItem.SortOrder,
                        CreatedAt = now,
                        UpdatedAt = now,
                        CreatedBy = "System", // TODO: 从当前用户获取
                        UpdatedBy = "System"
                    };
                    prefixes.Add(prefix);
                }

                await db.Insertable(prefixes).ExecuteCommandAsync();

                var prefixDtos = _mapper.Map<List<ProductPrefixCodeDto>>(prefixes);
                foreach (var prefixDto in prefixDtos)
                {
                    prefixDto.SupplierName = supplier.SupplierName;
                }

                _logger.LogInformation("批量创建商品前缀成功，SupplierCode: {SupplierCode}, Count: {Count}", dto.SupplierCode, prefixes.Count);
                return ApiResponse<List<ProductPrefixCodeDto>>.OK(prefixDtos);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量创建商品前缀失败");
                return ApiResponse<List<ProductPrefixCodeDto>>.Error("批量创建商品前缀失败", "BATCH_CREATE_PREFIXES_ERROR");
            }
        }

        /// <summary>
        /// 批量删除商品前缀
        /// </summary>
        public async Task<ApiResponse<bool>> BatchDeleteProductPrefixCodesAsync(List<string> prefixCodes)
        {
            try
            {
                var db = _context.Db;

                // 检查前缀是否存在
                var prefixes = await db.Queryable<ProductPrefixCode>()
                    .Where(p => prefixCodes.Contains(p.PrefixCode) && !p.IsDeleted)
                    .ToListAsync();

                if (prefixes.Count != prefixCodes.Count)
                {
                    return ApiResponse<bool>.Error("部分前缀不存在", "SOME_PREFIXES_NOT_FOUND");
                }

                // 检查是否有商品使用这些前缀
                var usedPrefixes = new List<string>();

                foreach (var prefixItem in prefixes)
                {
                    var productCount = await db.Queryable<DomesticProduct>()
                        .Where(p => p.SupplierCode == prefixItem.SupplierCode && p.HBProductNo != null && p.HBProductNo.Contains($"-{prefixItem.PrefixName}-") && !p.IsDeleted)
                        .CountAsync();

                    if (productCount > 0)
                    {
                        usedPrefixes.Add(prefixItem.PrefixName);
                    }
                }

                if (usedPrefixes.Any())
                {
                    return ApiResponse<bool>.Error(
                        $"以下前缀已被商品使用，无法删除: {string.Join(", ", usedPrefixes)}",
                        "PREFIXES_IN_USE");
                }

                // 批量软删除
                var now = DateTime.Now;
                foreach (var prefix in prefixes)
                {
                    prefix.IsDeleted = true;
                    prefix.UpdatedAt = now;
                    prefix.UpdatedBy = "System"; // TODO: 从当前用户获取
                }

                await db.Updateable(prefixes).ExecuteCommandAsync();

                _logger.LogInformation("批量删除商品前缀成功，Count: {Count}", prefixes.Count);
                return ApiResponse<bool>.OK(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量删除商品前缀失败");
                return ApiResponse<bool>.Error("批量删除商品前缀失败", "BATCH_DELETE_PREFIXES_ERROR");
            }
        }

        /// <summary>
        /// 更新前缀排序
        /// </summary>
        public async Task<ApiResponse<bool>> UpdatePrefixSortOrderAsync(Dictionary<string, int> prefixCodes)
        {
            try
            {
                var db = _context.Db;

                var prefixes = await db.Queryable<ProductPrefixCode>()
                    .Where(p => prefixCodes.Keys.Contains(p.PrefixCode) && !p.IsDeleted)
                    .ToListAsync();

                if (prefixes.Count != prefixCodes.Count)
                {
                    return ApiResponse<bool>.Error("部分前缀不存在", "SOME_PREFIXES_NOT_FOUND");
                }

                var now = DateTime.Now;
                foreach (var prefix in prefixes)
                {
                    if (prefixCodes.TryGetValue(prefix.PrefixCode, out int sortOrder))
                    {
                        prefix.SortOrder = sortOrder;
                        prefix.UpdatedAt = now;
                        prefix.UpdatedBy = "System"; // TODO: 从当前用户获取
                    }
                }

                await db.Updateable(prefixes).ExecuteCommandAsync();

                _logger.LogInformation("更新前缀排序成功，Count: {Count}", prefixes.Count);
                return ApiResponse<bool>.OK(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新前缀排序失败");
                return ApiResponse<bool>.Error("更新前缀排序失败", "UPDATE_PREFIX_SORT_ORDER_ERROR");
            }
        }
    }
}
