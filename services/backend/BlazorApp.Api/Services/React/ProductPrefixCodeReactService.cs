using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    /// <summary>
    /// React 商品前缀服务（独立实现，不委托原服务）
    /// </summary>
    public class ProductPrefixCodeReactService : IProductPrefixCodeReactService
    {
        private readonly SqlSugarContext _context;
        private readonly IMapper _mapper;
        private readonly ILogger<ProductPrefixCodeReactService> _logger;

        public ProductPrefixCodeReactService(
            SqlSugarContext context,
            IMapper mapper,
            ILogger<ProductPrefixCodeReactService> logger
        )
        {
            _context = context;
            _mapper = mapper;
            _logger = logger;
        }

        public async Task<
            ApiResponse<List<SimpleProductPrefixCodeDto>>
        > GetPrefixesBySupplierCodeAsync(string supplierCode)
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
                _logger.LogError(
                    ex,
                    "获取供应商前缀列表失败，SupplierCode: {SupplierCode}",
                    supplierCode
                );
                return ApiResponse<List<SimpleProductPrefixCodeDto>>.Error(
                    "获取供应商前缀列表失败",
                    "GET_SUPPLIER_PREFIXES_ERROR"
                );
            }
        }

        public async Task<ApiResponse<PagedResult<ProductPrefixCodeDto>>> GetAllPrefixesAsync(
            ProductPrefixCodeQueryDto query)
        {
            try
            {
                var db = _context.Db;

                var prefixQuery = db.Queryable<ProductPrefixCode>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => !p.IsDeleted);

                if (!string.IsNullOrWhiteSpace(query.Search))
                {
                    // 没有「查看国内供应商名称」权限时，名称、店铺号两支换成匹配不到的占位关键词。
                    var supplierNameKeyword = DomesticSupplierInfoMasking.GuardNameKeyword(query.Search);
                    prefixQuery = prefixQuery.Where((p, s) =>
                        p.PrefixName.Contains(query.Search) ||
                        (p.PrefixDescription != null && p.PrefixDescription.Contains(query.Search)) ||
                        (s.SupplierName != null && s.SupplierName.Contains(supplierNameKeyword)) ||
                        p.SupplierCode.Contains(query.Search) ||
                        (s.ShopNumber != null && s.ShopNumber.Contains(supplierNameKeyword)));
                }

                if (!string.IsNullOrWhiteSpace(query.SupplierCode))
                {
                    prefixQuery = prefixQuery.Where((p, s) => p.SupplierCode == query.SupplierCode);
                }

                if (query.IsActive.HasValue)
                {
                    prefixQuery = prefixQuery.Where((p, s) => p.IsActive == query.IsActive.Value);
                }

                if (!string.IsNullOrEmpty(query.SortField))
                {
                    var isDescending = !string.IsNullOrEmpty(query.SortDirection) &&
                                     query.SortDirection.ToLower() == "desc";

                    prefixQuery = query.SortField.ToLower() switch
                    {
                        "prefixname" => isDescending
                            ? prefixQuery.OrderByDescending((p, s) => p.PrefixName)
                            : prefixQuery.OrderBy((p, s) => p.PrefixName),
                        // 按被遮掉的国内供应商名称排序会泄露相对顺序，无权限时改按供应商代码排。
                        "suppliername" when !DomesticSupplierInfoMasking.IsNameSearchAllowed => isDescending
                            ? prefixQuery.OrderByDescending((p, s) => p.SupplierCode)
                            : prefixQuery.OrderBy((p, s) => p.SupplierCode),
                        "suppliername" => isDescending
                            ? prefixQuery.OrderByDescending((p, s) => s.SupplierName)
                            : prefixQuery.OrderBy((p, s) => s.SupplierName),
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

                var totalCount = await prefixQuery.CountAsync();

                var prefixes = await prefixQuery
                    .Select((p, s) => new
                    {
                        Prefix = p,
                        SupplierName = s.SupplierName
                    })
                    .Skip(query.Skip)
                    .Take(query.Take)
                    .ToListAsync();

                var prefixDtos = prefixes.Select(x =>
                {
                    var dto = _mapper.Map<ProductPrefixCodeDto>(x.Prefix);
                    dto.SupplierName = x.SupplierName;
                    return dto;
                }).ToList();

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
                _logger.LogError(ex, "获取所有前缀列表失败");
                return ApiResponse<PagedResult<ProductPrefixCodeDto>>.Error(
                    "获取前缀列表失败",
                    "GET_ALL_PREFIXES_ERROR"
                );
            }
        }

        public async Task<ApiResponse<PagedResult<DomesticProductDto>>> GetProductsByPrefixCodeAsync(
            string prefixCode, int page, int pageSize)
        {
            try
            {
                var db = _context.Db;

                var prefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.PrefixCode == prefixCode && !p.IsDeleted)
                    .FirstAsync();

                if (prefix == null)
                {
                    return ApiResponse<PagedResult<DomesticProductDto>>.Error(
                        "前缀不存在",
                        "PREFIX_NOT_FOUND"
                    );
                }

                var productQuery = db.Queryable<DomesticProduct>()
                    .LeftJoin<ChinaSupplier>((p, s) => p.SupplierCode == s.SupplierCode)
                    .Where((p, s) => !p.IsDeleted && p.SupplierCode == prefix.SupplierCode)
                    .Where(p => p.HBProductNo != null && p.HBProductNo.Contains($"-{prefix.PrefixName}-"))
                    .OrderByDescending(p => p.CreatedAt);

                var totalCount = await productQuery.CountAsync();

                var products = await productQuery
                    .Select((p, s) => new
                    {
                        Product = p,
                        SupplierName = s.SupplierName
                    })
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                var productDtos = products.Select(x =>
                {
                    var dto = _mapper.Map<DomesticProductDto>(x.Product);
                    dto.SupplierName = x.SupplierName;
                    return dto;
                }).ToList();

                var result = new PagedResult<DomesticProductDto>
                {
                    Items = productDtos,
                    Total = totalCount,
                    Page = page,
                    PageSize = pageSize
                };

                return ApiResponse<PagedResult<DomesticProductDto>>.OK(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取前缀关联商品失败，PrefixCode: {PrefixCode}", prefixCode);
                return ApiResponse<PagedResult<DomesticProductDto>>.Error(
                    "获取关联商品失败",
                    "GET_PREFIX_PRODUCTS_ERROR"
                );
            }
        }

        public async Task<ApiResponse<ProductPrefixCodeDto>> CreateProductPrefixCodeAsync(
            CreateProductPrefixCodeDto dto
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
                    return ApiResponse<ProductPrefixCodeDto>.Error(
                        "供应商不存在",
                        "SUPPLIER_NOT_FOUND"
                    );
                }

                var existingPrefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p =>
                        p.SupplierCode == dto.SupplierCode
                        && p.PrefixName == dto.PrefixName
                        && !p.IsDeleted
                    )
                    .FirstAsync();
                if (existingPrefix != null)
                {
                    return ApiResponse<ProductPrefixCodeDto>.Error(
                        "该供应商的前缀代码已存在",
                        "PREFIX_NAME_EXISTS"
                    );
                }

                var prefix = _mapper.Map<ProductPrefixCode>(dto);
                prefix.PrefixCode = UuidHelper.GenerateUuid7();
                prefix.CreatedAt = DateTime.Now;
                prefix.UpdatedAt = DateTime.Now;
                prefix.CreatedBy = "System";
                prefix.UpdatedBy = "System";

                await db.Insertable(prefix).ExecuteCommandAsync();

                var prefixDto = _mapper.Map<ProductPrefixCodeDto>(prefix);
                prefixDto.SupplierName = supplier.SupplierName;

                _logger.LogInformation(
                    "创建商品前缀成功，PrefixCode: {PrefixCode}",
                    prefix.PrefixCode
                );
                return ApiResponse<ProductPrefixCodeDto>.OK(prefixDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建商品前缀失败");
                return ApiResponse<ProductPrefixCodeDto>.Error(
                    "创建商品前缀失败",
                    "CREATE_PREFIX_ERROR"
                );
            }
        }

        public async Task<ApiResponse<ProductPrefixCodeDto>> UpdateProductPrefixCodeAsync(
            string prefixCode,
            UpdateProductPrefixCodeDto dto
        )
        {
            try
            {
                var db = _context.Db;

                var prefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p => p.PrefixCode == prefixCode && !p.IsDeleted)
                    .FirstAsync();
                if (prefix == null)
                {
                    return ApiResponse<ProductPrefixCodeDto>.Error(
                        "商品前缀不存在",
                        "PREFIX_NOT_FOUND"
                    );
                }

                var existingPrefix = await db.Queryable<ProductPrefixCode>()
                    .Where(p =>
                        p.SupplierCode == prefix.SupplierCode
                        && p.PrefixName == dto.PrefixName
                        && p.PrefixCode != prefixCode
                        && !p.IsDeleted
                    )
                    .FirstAsync();
                if (existingPrefix != null)
                {
                    return ApiResponse<ProductPrefixCodeDto>.Error(
                        "该供应商的前缀代码已存在",
                        "PREFIX_NAME_EXISTS"
                    );
                }

                _mapper.Map(dto, prefix);
                prefix.UpdatedAt = DateTime.Now;
                prefix.UpdatedBy = "System";

                await db.Updateable(prefix).ExecuteCommandAsync();

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
                return ApiResponse<ProductPrefixCodeDto>.Error(
                    "更新商品前缀失败",
                    "UPDATE_PREFIX_ERROR"
                );
            }
        }

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

                var productCount = await db.Queryable<DomesticProduct>()
                    .Where(p =>
                        p.SupplierCode == prefix.SupplierCode
                        && p.HBProductNo != null
                        && p.HBProductNo.Contains($"-{prefix.PrefixName}-")
                        && !p.IsDeleted
                    )
                    .CountAsync();
                if (productCount > 0)
                {
                    return ApiResponse<bool>.Error(
                        $"该前缀已被 {productCount} 个商品使用，无法删除",
                        "PREFIX_IN_USE"
                    );
                }

                prefix.IsDeleted = true;
                prefix.UpdatedAt = DateTime.Now;
                prefix.UpdatedBy = "System";

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
    }
}
