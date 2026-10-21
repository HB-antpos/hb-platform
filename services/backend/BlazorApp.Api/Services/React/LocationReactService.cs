using System.Linq.Expressions;
using System.Text.RegularExpressions;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class LocationReactService : ILocationReactService
    {
        private const int PickingLocationType = 1;
        private const int StorageLocationType = 2;
        private const string LocationCodePattern = @"^[A-Z]-\d{2}-\d{2}-\d{2}$";
        private const string FilterTokenNamespace = "__filter";

        private readonly SqlSugarContext _context;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<LocationReactService> _logger;

        public LocationReactService(
            SqlSugarContext context,
            ICurrentUserService currentUserService,
            ILogger<LocationReactService> logger
        )
        {
            _context = context;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        public async Task<PagedListReactDto<LocationReactDto>> GetPagedListAsync(
            LocationReactFilterDto filter
        )
        {
            try
            {
                var query = _context.Db.Queryable<Location>().Where(l => !l.IsDeleted);

                if (filter.LocationType.HasValue)
                {
                    query = query.Where(l => l.LocationType == filter.LocationType.Value);
                }

                if (filter.IsUsed.HasValue)
                {
                    // 使用状态与列表商品加载保持同一口径：只把未删除商品的有效绑定视为已使用。
                    if (filter.IsUsed.Value)
                    {
                        query = query.Where(l =>
                            SqlFunc.Subqueryable<ProductLocation>()
                                .InnerJoin<Product>((pl, p) =>
                                    pl.ProductCode == p.ProductCode && !p.IsDeleted
                                )
                                .Where((pl, p) =>
                                    !pl.IsDeleted && pl.LocationGuid == l.LocationGuid
                                )
                                .Any()
                        );
                    }
                    else
                    {
                        query = query.Where(l =>
                            !SqlFunc.Subqueryable<ProductLocation>()
                                .InnerJoin<Product>((pl, p) =>
                                    pl.ProductCode == p.ProductCode && !p.IsDeleted
                                )
                                .Where((pl, p) =>
                                    !pl.IsDeleted && pl.LocationGuid == l.LocationGuid
                                )
                                .Any()
                        );
                    }
                }

                query = ApplyTopLevelFilters(query, filter);
                query = ApplyColumnFilters(query, filter.Filters);

                var total = await query.Clone().CountAsync();

                var sortBy = filter.SortBy ?? "LocationCode";
                var sortDirection = filter.SortDirection ?? "asc";
                query = ApplySorting(query, sortBy, sortDirection);

                var locations = await query.ToPageListAsync(filter.PageNumber, filter.PageSize);
                var result = locations.Select(MapToDto).ToList();
                await LoadProductsForLocationsAsync(result);

                return new PagedListReactDto<LocationReactDto>
                {
                    Items = result,
                    Total = total,
                    PageNumber = filter.PageNumber,
                    PageSize = filter.PageSize,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货位列表失败");
                throw;
            }
        }

        public async Task<ApiResponse<LocationReactDto>> GetByIdAsync(string locationGuid)
        {
            try
            {
                var location = await _context
                    .Db.Queryable<Location>()
                    .Where(l => l.LocationGuid == locationGuid && !l.IsDeleted)
                    .FirstAsync();

                if (location == null)
                {
                    return ApiResponse<LocationReactDto>.Error("货位不存在", "NOT_FOUND");
                }

                var dto = MapToDto(location);
                await LoadProductsAsync(dto);

                return ApiResponse<LocationReactDto>.OK(dto, "获取货位详情成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取货位详情失败: {LocationGuid}", locationGuid);
                return ApiResponse<LocationReactDto>.Error(
                    "获取货位详情失败",
                    "DATABASE_ERROR",
                    ex.Message
                );
            }
        }

        public async Task<ApiResponse<LocationReactDto>> CreateAsync(CreateLocationReactDto dto)
        {
            try
            {
                var validationError = ValidateLocationInput(dto.LocationCode, dto.LocationType, dto.Status);
                if (validationError != null)
                {
                    return ApiResponse<LocationReactDto>.Error(validationError.Value.Message, validationError.Value.ErrorCode);
                }

                var locationCode = NormalizeLocationCode(dto.LocationCode);
                var existingList = await _context
                    .Db.Queryable<Location>()
                    .Where(l => l.LocationCode == locationCode && !l.IsDeleted)
                    .ToListAsync();

                if (existingList.Count > 0)
                {
                    return ApiResponse<LocationReactDto>.Error("货位代码已存在", "CONFLICT");
                }

                var locationBarcode = await GenerateUniqueLocationBarcodeAsync();
                var username = _currentUserService.GetCurrentUsername();
                var location = new Location
                {
                    LocationGuid = Guid.NewGuid().ToString(),
                    LocationCode = locationCode,
                    LocationBarcode = locationBarcode,
                    LocationType = dto.LocationType,
                    Status = dto.Status ?? 1,
                    CreatedBy = username,
                    UpdatedBy = username,
                    UpdatedAt = DateTime.UtcNow,
                };

                await _context.Db.Insertable(location).ExecuteCommandAsync();

                var result = MapToDto(location);
                return ApiResponse<LocationReactDto>.OK(result, "创建货位成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建货位失败");
                return ApiResponse<LocationReactDto>.Error(
                    "创建货位失败",
                    "DATABASE_ERROR",
                    ex.Message
                );
            }
        }

        public async Task<ApiResponse<LocationReactDto>> UpdateAsync(
            string locationGuid,
            UpdateLocationReactDto dto
        )
        {
            try
            {
                var locationList = await _context
                    .Db.Queryable<Location>()
                    .Where(l => l.LocationGuid == locationGuid && !l.IsDeleted)
                    .ToListAsync();

                if (locationList.Count == 0)
                {
                    return ApiResponse<LocationReactDto>.Error("货位不存在", "NOT_FOUND");
                }

                var location = locationList[0];

                var validationError = ValidateLocationInput(dto.LocationCode, dto.LocationType, dto.Status);
                if (validationError != null)
                {
                    return ApiResponse<LocationReactDto>.Error(validationError.Value.Message, validationError.Value.ErrorCode);
                }

                var locationCode = NormalizeLocationCode(dto.LocationCode);
                var duplicateList = await _context
                    .Db.Queryable<Location>()
                    .Where(l =>
                        l.LocationCode == locationCode
                        && l.LocationGuid != locationGuid
                        && !l.IsDeleted
                    )
                    .ToListAsync();

                if (duplicateList.Count > 0)
                {
                    return ApiResponse<LocationReactDto>.Error(
                        "货位代码已被其他货位使用",
                        "CONFLICT"
                    );
                }

                var username = _currentUserService.GetCurrentUsername();
                location.LocationCode = locationCode;
                if (string.IsNullOrWhiteSpace(location.LocationBarcode))
                {
                    location.LocationBarcode = await GenerateUniqueLocationBarcodeAsync();
                }
                location.LocationType = dto.LocationType;
                location.Status = dto.Status;
                location.UpdatedBy = username;
                location.UpdatedAt = DateTime.UtcNow;

                await _context.Db.Updateable(location).ExecuteCommandAsync();

                return await GetByIdAsync(locationGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新货位失败: {LocationGuid}", locationGuid);
                return ApiResponse<LocationReactDto>.Error(
                    "更新货位失败",
                    "DATABASE_ERROR",
                    ex.Message
                );
            }
        }

        public async Task<ApiResponse<bool>> DeleteAsync(string locationGuid)
        {
            try
            {
                var locationList = await _context
                    .Db.Queryable<Location>()
                    .Where(l => l.LocationGuid == locationGuid && !l.IsDeleted)
                    .ToListAsync();

                if (locationList.Count == 0)
                {
                    return ApiResponse<bool>.Error("货位不存在", "NOT_FOUND");
                }

                var location = locationList[0];

                var hasProducts = await _context
                    .Db.Queryable<ProductLocation>()
                    .AnyAsync(pl => pl.LocationGuid == locationGuid && !pl.IsDeleted);

                if (hasProducts)
                {
                    return ApiResponse<bool>.Error("该货位下存在关联商品，无法删除", "CONFLICT");
                }

                var username = _currentUserService.GetCurrentUsername();
                location.IsDeleted = true;
                location.UpdatedBy = username;
                location.UpdatedAt = DateTime.UtcNow;

                await _context.Db.Updateable(location).ExecuteCommandAsync();

                return ApiResponse<bool>.OK(true, "删除货位成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除货位失败: {LocationGuid}", locationGuid);
                return ApiResponse<bool>.Error("删除货位失败", "DATABASE_ERROR", ex.Message);
            }
        }

        public async Task<List<LocationLookupItemDto>> LookupAsync(string keyword)
        {
            var trimmed = keyword?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return new List<LocationLookupItemDto>();
            }

            var lowered = trimmed.ToLower();
            var locationMatches = await _context
                .Db.Queryable<Location>()
                .Where(l =>
                    !l.IsDeleted
                    && (
                        (l.LocationCode != null && l.LocationCode.ToLower().Contains(lowered))
                        || (l.LocationBarcode != null && l.LocationBarcode.ToLower().Contains(lowered))
                    )
                )
                .OrderBy(l => l.LocationCode)
                .Take(20)
                .ToListAsync();

            var productMatches = await _context
                .Db.Queryable<ProductLocation>()
                .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                .InnerJoin<Location>((pl, p, l) => pl.LocationGuid == l.LocationGuid && !l.IsDeleted)
                .Where((pl, p, l) =>
                    !pl.IsDeleted
                    && pl.LocationGuid != null
                    && (
                        (p.ProductCode != null && p.ProductCode.ToLower().Contains(lowered))
                        || (p.ItemNumber != null && p.ItemNumber.ToLower().Contains(lowered))
                        || (p.Barcode != null && p.Barcode.ToLower().Contains(lowered))
                    )
                )
                .Select((pl, p, l) => new Location
                {
                    LocationGuid = l.LocationGuid,
                    LocationCode = l.LocationCode,
                    LocationBarcode = l.LocationBarcode,
                    Status = l.Status,
                    LocationType = l.LocationType,
                })
                .Distinct()
                .MergeTable()
                .OrderBy(l => l.LocationCode)
                .Take(20)
                .ToListAsync();

            var locations = locationMatches
                .Concat(productMatches)
                .GroupBy(location => location.LocationGuid)
                .Select(group => group.First())
                .OrderBy(location => location.LocationCode)
                .Take(20)
                .ToList();

            if (locations.Count == 0)
            {
                return new List<LocationLookupItemDto>();
            }

            var locationGuids = locations.Select(item => item.LocationGuid).ToList();
            var counts = await _context
                .Db.Queryable<ProductLocation>()
                .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                .Where((pl, p) => !pl.IsDeleted && locationGuids.Contains(pl.LocationGuid!))
                .GroupBy((pl, p) => pl.LocationGuid)
                .Select((pl, p) => new { LocationGuid = pl.LocationGuid, Count = SqlFunc.AggregateCount(pl.Guid) })
                .ToListAsync();

            var countMap = counts.ToDictionary(item => item.LocationGuid ?? string.Empty, item => item.Count);

            return locations.Select(location => new LocationLookupItemDto
            {
                LocationGuid = location.LocationGuid,
                LocationCode = location.LocationCode,
                LocationBarcode = location.LocationBarcode,
                Status = location.Status,
                LocationType = location.LocationType,
                ProductCount = countMap.TryGetValue(location.LocationGuid, out var count) ? count : 0,
            }).ToList();
        }

        public async Task<ApiResponse<LocationReactDto>> BindProductAsync(
            string locationGuid,
            string productIdentifier
        )
        {
            var transactionStarted = false;
            try
            {
                Console.WriteLine(
                    $"[LocationReactService.BindProduct] 开始绑定 LocationGuid={locationGuid}, ProductIdentifier={productIdentifier}"
                );

                var location = await _context
                    .Db.Queryable<Location>()
                    .Where(l => l.LocationGuid == locationGuid && !l.IsDeleted)
                    .FirstAsync();
                if (location == null)
                {
                    Console.WriteLine(
                        $"[LocationReactService.BindProduct] 绑定失败：货位不存在 LocationGuid={locationGuid}, ProductIdentifier={productIdentifier}"
                    );
                    return ApiResponse<LocationReactDto>.Error("货位不存在", "NOT_FOUND");
                }

                Console.WriteLine(
                    $"[LocationReactService.BindProduct] 命中货位 LocationGuid={locationGuid}, LocationCode={location.LocationCode}, LocationBarcode={location.LocationBarcode}, LocationType={location.LocationType}"
                );

                var productResult = await ResolveProductAsync(productIdentifier);
                if (!productResult.Success || productResult.Data == null)
                {
                    Console.WriteLine(
                        $"[LocationReactService.BindProduct] 绑定失败：商品解析失败 LocationGuid={locationGuid}, ProductIdentifier={productIdentifier}, ErrorCode={productResult.ErrorCode}, Message={productResult.Message}"
                    );
                    return ApiResponse<LocationReactDto>.Error(
                        productResult.Message,
                        productResult.ErrorCode,
                        productResult.Details
                    );
                }

                var productCode = productResult.Data.ProductCode;
                Console.WriteLine(
                    $"[LocationReactService.BindProduct] 命中商品 LocationGuid={locationGuid}, ProductIdentifier={productIdentifier}, ProductCode={productCode}, MatchedBy={productResult.Data.MatchedBy}, MatchedValue={productResult.Data.MatchedValue}"
                );

                var warehouseProduct = await _context
                    .Db.Queryable<WarehouseProduct>()
                    .Where(w => w.ProductCode == productCode && !w.IsDeleted)
                    .FirstAsync();
                if (warehouseProduct == null)
                {
                    Console.WriteLine(
                        $"[LocationReactService.BindProduct] 绑定失败：仓库商品不存在 LocationGuid={locationGuid}, ProductCode={productCode}"
                    );
                    return ApiResponse<LocationReactDto>.Error("仓库商品不存在", "NOT_FOUND");
                }

                var existingSameBinding = await _context
                    .Db.Queryable<ProductLocation>()
                    .AnyAsync(pl =>
                        pl.LocationGuid == locationGuid
                        && pl.ProductCode == productCode
                        && !pl.IsDeleted
                    );
                if (existingSameBinding)
                {
                    Console.WriteLine(
                        $"[LocationReactService.BindProduct] 已存在相同绑定，直接返回 LocationGuid={locationGuid}, ProductCode={productCode}"
                    );
                    return await GetByIdAsync(locationGuid);
                }

                if (location.LocationType == PickingLocationType)
                {
                    var existingLocationProduct = await _context
                        .Db.Queryable<ProductLocation>()
                        .Where(pl => pl.LocationGuid == locationGuid && !pl.IsDeleted)
                        .FirstAsync();

                    if (existingLocationProduct != null)
                    {
                        Console.WriteLine(
                            $"[LocationReactService.BindProduct] 绑定失败：配货位已有商品 LocationGuid={locationGuid}, LocationCode={location.LocationCode}, ExistingProductCode={existingLocationProduct.ProductCode}, NewProductCode={productCode}"
                        );
                        return ApiResponse<LocationReactDto>.Error(
                            "该配货位已绑定商品，请解绑后继续绑定新的货位",
                            "PICKING_LOCATION_ALREADY_BOUND",
                            new
                            {
                                locationGuid,
                                existingProductCode = existingLocationProduct.ProductCode,
                            }
                        );
                    }

                    var existingPickingLocation = await _context
                        .Db.Queryable<ProductLocation, Location>((pl, l) => new JoinQueryInfos(
                            JoinType.Inner,
                            pl.LocationGuid == l.LocationGuid
                        ))
                        .Where((pl, l) =>
                            pl.ProductCode == productCode
                            && !pl.IsDeleted
                            && !l.IsDeleted
                            && l.LocationType == PickingLocationType
                        )
                        .Select((pl, l) => new { pl.LocationGuid, l.LocationCode })
                        .FirstAsync();

                    if (existingPickingLocation != null)
                    {
                        Console.WriteLine(
                            $"[LocationReactService.BindProduct] 绑定失败：商品已绑定配货位 ProductCode={productCode}, ExistingLocationGuid={existingPickingLocation.LocationGuid}, ExistingLocationCode={existingPickingLocation.LocationCode}, NewLocationGuid={locationGuid}, NewLocationCode={location.LocationCode}"
                        );
                        return ApiResponse<LocationReactDto>.Error(
                            "该商品已绑定配货位，请解绑后继续绑定新的货位",
                            "PRODUCT_PICKING_LOCATION_ALREADY_BOUND",
                            new
                            {
                                productCode,
                                existingPickingLocation.LocationGuid,
                                existingPickingLocation.LocationCode,
                            }
                        );
                    }
                }
                else if (location.LocationType != StorageLocationType)
                {
                    Console.WriteLine(
                        $"[LocationReactService.BindProduct] 绑定失败：货位类型无效 LocationGuid={locationGuid}, LocationCode={location.LocationCode}, LocationType={location.LocationType}, ProductCode={productCode}"
                    );
                    return ApiResponse<LocationReactDto>.Error(
                        "货位类型只能是1=配货位或2=存货位",
                        "INVALID_LOCATION_TYPE"
                    );
                }

                await _context.Db.Ado.BeginTranAsync();
                transactionStarted = true;

                var username = _currentUserService.GetCurrentUsername();
                await _context
                    .Db.Insertable(new ProductLocation
                    {
                        Guid = Guid.NewGuid().ToString(),
                        ProductCode = productCode,
                        LocationGuid = locationGuid,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = username,
                        UpdatedAt = DateTime.UtcNow,
                        UpdatedBy = username,
                    })
                    .ExecuteCommandAsync();

                await _context.Db.Ado.CommitTranAsync();
                Console.WriteLine(
                    $"[LocationReactService.BindProduct] 写入绑定成功 LocationGuid={locationGuid}, LocationCode={location.LocationCode}, LocationType={location.LocationType}, ProductCode={productCode}, CreatedBy={username}"
                );
                return await GetByIdAsync(locationGuid);
            }
            catch (Exception ex)
            {
                if (transactionStarted)
                {
                    await _context.Db.Ado.RollbackTranAsync();
                }
                Console.WriteLine(
                    $"[LocationReactService.BindProduct] 绑定异常 LocationGuid={locationGuid}, ProductIdentifier={productIdentifier}, TransactionStarted={transactionStarted}, Error={ex}"
                );
                _logger.LogError(ex, "绑定货位商品失败: {LocationGuid} {ProductIdentifier}", locationGuid, productIdentifier);
                return ApiResponse<LocationReactDto>.Error("绑定商品失败", "DATABASE_ERROR", ex.Message);
            }
        }

        public async Task<ApiResponse<LocationProductResolveDto>> ResolveProductAsync(
            string productIdentifier
        )
        {
            var keyword = productIdentifier?.Trim();
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return ApiResponse<LocationProductResolveDto>.Error(
                    "商品货号或条码不能为空",
                    "VALIDATION_ERROR"
                );
            }

            try
            {
                var matches = new Dictionary<string, (Product Product, string MatchedBy, string MatchedValue)>();

                var products = await _context
                    .Db.Queryable<Product>()
                    .Where(p =>
                        !p.IsDeleted
                        && (
                            p.ProductCode == keyword
                            || p.ItemNumber == keyword
                            || p.Barcode == keyword
                        )
                    )
                    .ToListAsync();

                foreach (var product in products)
                {
                    AddProductResolveMatch(matches, product, keyword);
                }

                var setProductCodes = await _context
                    .Db.Queryable<ProductSetCode>()
                    .Where(psc =>
                        !psc.IsDeleted
                        && (
                            psc.SetItemNumber == keyword
                            || psc.SetBarcode == keyword
                            || psc.SetProductCode == keyword
                        )
                    )
                    .Select(psc => psc.ProductCode)
                    .Distinct()
                    .ToListAsync();

                var multiProductCodes = await _context
                    .Db.Queryable<StoreMultiCodeProduct>()
                    .Where(smp =>
                        !smp.IsDeleted
                        && (
                            smp.MultiBarcode == keyword
                            || smp.MultiCodeProductCode == keyword
                            || smp.StoreMultiCodeProductCode == keyword
                        )
                    )
                    .Select(smp => smp.ProductCode)
                    .Distinct()
                    .ToListAsync();

                var relatedProductCodes = setProductCodes
                    .Concat(multiProductCodes)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct()
                    .ToList();

                if (relatedProductCodes.Count > 0)
                {
                    var relatedProducts = await _context
                        .Db.Queryable<Product>()
                        .Where(p => relatedProductCodes.Contains(p.ProductCode) && !p.IsDeleted)
                        .ToListAsync();

                    foreach (var product in relatedProducts)
                    {
                        if (!string.IsNullOrWhiteSpace(product.ProductCode)
                            && !matches.ContainsKey(product.ProductCode))
                        {
                            matches[product.ProductCode] = (product, "barcode", keyword);
                        }
                    }
                }

                if (matches.Count == 0)
                {
                    return ApiResponse<LocationProductResolveDto>.Error(
                        "商品不存在",
                        "NOT_FOUND",
                        new { productIdentifier = keyword }
                    );
                }

                if (matches.Count > 1)
                {
                    return ApiResponse<LocationProductResolveDto>.Error(
                        "商品货号或条码匹配到多个商品，请使用商品编码绑定",
                        "AMBIGUOUS_PRODUCT",
                        new
                        {
                            productIdentifier = keyword,
                            productCodes = matches.Keys.ToList(),
                        }
                    );
                }

                var match = matches.Values.First();
                var dto = new LocationProductResolveDto
                {
                    ProductCode = match.Product.ProductCode ?? string.Empty,
                    ItemNumber = match.Product.ItemNumber,
                    Barcode = match.Product.Barcode,
                    ProductName = match.Product.ProductName,
                    ProductImage = match.Product.ProductImage,
                    MatchedBy = match.MatchedBy,
                    MatchedValue = match.MatchedValue,
                };

                return ApiResponse<LocationProductResolveDto>.OK(dto, "商品解析成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解析货位绑定商品失败: {ProductIdentifier}", productIdentifier);
                return ApiResponse<LocationProductResolveDto>.Error(
                    "解析商品失败",
                    "DATABASE_ERROR",
                    ex.Message
                );
            }
        }

        public async Task<ApiResponse<LocationReactDto>> UnbindProductAsync(
            string locationGuid,
            string productCode
        )
        {
            try
            {
                var location = await _context
                    .Db.Queryable<Location>()
                    .Where(l => l.LocationGuid == locationGuid && !l.IsDeleted)
                    .FirstAsync();
                if (location == null)
                {
                    return ApiResponse<LocationReactDto>.Error("货位不存在", "NOT_FOUND");
                }

                var affected = await _context
                    .Db.Deleteable<ProductLocation>()
                    .Where(pl => pl.LocationGuid == locationGuid && pl.ProductCode == productCode)
                    .ExecuteCommandAsync();

                if (affected == 0)
                {
                    return ApiResponse<LocationReactDto>.Error("货位商品关联不存在", "NOT_FOUND");
                }

                return await GetByIdAsync(locationGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解绑货位商品失败: {LocationGuid} {ProductCode}", locationGuid, productCode);
                return ApiResponse<LocationReactDto>.Error("解绑商品失败", "DATABASE_ERROR", ex.Message);
            }
        }

        private async Task LoadProductsAsync(LocationReactDto dto)
        {
            var productLocations = await _context
                .Db.Queryable<ProductLocation>()
                .Where(pl => pl.LocationGuid == dto.LocationGuid && !pl.IsDeleted)
                .ToListAsync();

            if (productLocations.Count == 0)
                return;

            var productCodes = productLocations
                .Select(pl => pl.ProductCode)
                .Where(pc => !string.IsNullOrEmpty(pc))
                .ToList();

            if (productCodes.Count == 0)
                return;

            var products = await _context
                .Db.Queryable<Product>()
                .Where(p => productCodes.Contains(p.ProductCode) && !p.IsDeleted)
                .ToListAsync();

            dto.Products = products
                .Select(p => new LocationReactProductDto
                {
                    ProductCode = p.ProductCode,
                    ItemNumber = p.ItemNumber,
                    Barcode = p.Barcode,
                    ProductName = p.ProductName,
                    ProductImage = p.ProductImage,
                    MiddlePackageQuantity = p.MiddlePackageQuantity,
                })
                .ToList();
        }

        private async Task LoadProductsForLocationsAsync(List<LocationReactDto> locations)
        {
            if (locations.Count == 0)
                return;

            var locationGuids = locations.Select(location => location.LocationGuid).ToList();
            // 批量加载当前页商品，避免列表每行分别查询 ProductLocation 和 Product。
            var products = await _context
                .Db.Queryable<ProductLocation>()
                .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                .Where((pl, p) =>
                    !pl.IsDeleted
                    && pl.LocationGuid != null
                    && locationGuids.Contains(pl.LocationGuid)
                )
                .Select((pl, p) => new
                {
                    pl.LocationGuid,
                    p.ProductCode,
                    p.ItemNumber,
                    p.Barcode,
                    p.ProductName,
                    p.ProductImage,
                    p.MiddlePackageQuantity,
                })
                .ToListAsync();

            var productMap = products
                .GroupBy(product => product.LocationGuid ?? string.Empty)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(product => new LocationReactProductDto
                        {
                            ProductCode = product.ProductCode,
                            ItemNumber = product.ItemNumber,
                            Barcode = product.Barcode,
                            ProductName = product.ProductName,
                            ProductImage = product.ProductImage,
                            MiddlePackageQuantity = product.MiddlePackageQuantity,
                        })
                        .ToList()
                );

            foreach (var location in locations)
            {
                if (productMap.TryGetValue(location.LocationGuid, out var locationProducts))
                {
                    location.Products = locationProducts;
                }
            }
        }

        private async Task<string> GenerateUniqueLocationBarcodeAsync()
        {
            var timestamp = DateTime.Now;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var barcode12 = timestamp.AddSeconds(attempt).ToString("yyMMddHHmmss");
                var barcode = barcode12 + CalculateEan13CheckDigit(barcode12);
                var exists = await _context
                    .Db.Queryable<Location>()
                    .AnyAsync(l => l.LocationBarcode == barcode && !l.IsDeleted);

                if (!exists)
                {
                    return barcode;
                }
            }

            throw new InvalidOperationException("生成货位条码失败，请稍后重试");
        }

        private static int CalculateEan13CheckDigit(string barcode12)
        {
            var sum = 0;
            for (var i = 0; i < barcode12.Length; i++)
            {
                var digit = barcode12[i] - '0';
                sum += i % 2 == 0 ? digit : digit * 3;
            }

            return (10 - sum % 10) % 10;
        }

        private static (string Message, string ErrorCode)? ValidateLocationInput(
            string? locationCode,
            int? locationType,
            int? status
        )
        {
            var normalizedCode = NormalizeLocationCode(locationCode);
            if (string.IsNullOrWhiteSpace(normalizedCode))
            {
                return ("货位代码不能为空", "VALIDATION_ERROR");
            }

            if (!Regex.IsMatch(normalizedCode, LocationCodePattern))
            {
                return ("货位代码格式不正确，应为 A-00-00-01 格式：字母A-Z + 00-99 + 00-99 + 00-99", "INVALID_LOCATION_CODE");
            }

            if (locationType != PickingLocationType && locationType != StorageLocationType)
            {
                return ("货位类型只能是1=配货位或2=存货位", "INVALID_LOCATION_TYPE");
            }

            if (status.HasValue && status.Value != 0 && status.Value != 1)
            {
                return ("货位状态只能是0或1", "INVALID_STATUS");
            }

            return null;
        }

        private static string NormalizeLocationCode(string? locationCode)
        {
            return locationCode?.Trim().ToUpperInvariant() ?? string.Empty;
        }

        private static void AddProductResolveMatch(
            Dictionary<string, (Product Product, string MatchedBy, string MatchedValue)> matches,
            Product product,
            string keyword
        )
        {
            if (string.IsNullOrWhiteSpace(product.ProductCode))
            {
                return;
            }

            var matchedBy = product.ProductCode == keyword
                ? "productCode"
                : product.ItemNumber == keyword
                    ? "itemNumber"
                    : "barcode";

            matches[product.ProductCode] = (product, matchedBy, keyword);
        }

        private static LocationReactDto MapToDto(Location location)
        {
            return new LocationReactDto
            {
                LocationGuid = location.LocationGuid,
                LocationCode = location.LocationCode,
                LocationBarcode = location.LocationBarcode,
                Status = location.Status,
                LocationType = location.LocationType,
                UpdatedAt = location.UpdatedAt,
                UpdatedBy = location.UpdatedBy,
            };
        }

        private static ISugarQueryable<Location> ApplyTopLevelFilters(
            ISugarQueryable<Location> query,
            LocationReactFilterDto filter
        )
        {
            if (!string.IsNullOrWhiteSpace(filter.LocationCode))
            {
                query = ApplyLocationTextFilter(
                    query,
                    "locationcode",
                    new[] { filter.LocationCode }
                );
            }

            if (!string.IsNullOrWhiteSpace(filter.LocationBarcode))
            {
                query = ApplyLocationTextFilter(
                    query,
                    "locationbarcode",
                    new[] { filter.LocationBarcode }
                );
            }

            if (filter.Status.HasValue)
            {
                query = ApplyStatusEqualsFilter(query, filter.Status.Value);
            }

            if (filter.UpdatedAtStart.HasValue)
            {
                var updatedAtStart = filter.UpdatedAtStart.Value;
                query = query.Where(l => l.UpdatedAt >= updatedAtStart);
            }

            if (filter.UpdatedAtEnd.HasValue)
            {
                var updatedAtEnd = filter.UpdatedAtEnd.Value;
                query = query.Where(l => l.UpdatedAt <= updatedAtEnd);
            }

            if (!string.IsNullOrWhiteSpace(filter.UpdatedBy))
            {
                query = ApplyLocationTextFilter(query, "updatedby", new[] { filter.UpdatedBy });
            }

            return query;
        }

        private static ISugarQueryable<Location> ApplyColumnFilters(
            ISugarQueryable<Location> query,
            Dictionary<string, string[]>? filters
        )
        {
            if (filters == null || filters.Count == 0)
            {
                return query;
            }

            foreach (var kv in filters)
            {
                var key = kv.Key?.Trim().ToLowerInvariant();
                var values = NormalizeFilterValues(kv.Value);
                if (string.IsNullOrWhiteSpace(key) || values.Count == 0)
                {
                    continue;
                }

                query = key switch
                {
                    "locationcode" or "locationbarcode" or "updatedby" =>
                        ApplyLocationTextFilter(query, key, values),
                    "status" => ApplyNullableIntFilter(query, "status", values),
                    "locationtype" => ApplyNullableIntFilter(query, "locationtype", values),
                    "updatedat" => ApplyUpdatedAtFilter(query, values),
                    "productitemnumber" or "productbarcode" or "productname" =>
                        ApplyProductTextFilter(query, key, values),
                    _ => query,
                };
            }

            return query;
        }

        private static ISugarQueryable<Location> ApplyLocationTextFilter(
            ISugarQueryable<Location> query,
            string key,
            IEnumerable<string> values
        )
        {
            var criteria = ParseTextFilterCriteria(values);
            foreach (var criterion in criteria)
            {
                var value = criterion.Value;
                // 文本列保留 token 模式；默认 contains 兼容旧客户端的裸值筛选。
                query = key switch
                {
                    "locationcode" => ApplyLocationCodeFilter(query, criterion.Mode, value),
                    "locationbarcode" => ApplyLocationBarcodeFilter(query, criterion.Mode, value),
                    "updatedby" => ApplyUpdatedByFilter(query, criterion.Mode, value),
                    _ => query,
                };
            }

            return query;
        }

        private static ISugarQueryable<Location> ApplyLocationCodeFilter(
            ISugarQueryable<Location> query,
            LocationTextFilterMode mode,
            string value
        )
        {
            return mode switch
            {
                LocationTextFilterMode.Equals => query.Where(l =>
                    l.LocationCode != null && l.LocationCode == value
                ),
                LocationTextFilterMode.Starts => query.Where(l =>
                    l.LocationCode != null && l.LocationCode.StartsWith(value)
                ),
                LocationTextFilterMode.Ends => query.Where(l =>
                    l.LocationCode != null && l.LocationCode.EndsWith(value)
                ),
                _ => query.Where(l =>
                    l.LocationCode != null && l.LocationCode.Contains(value)
                ),
            };
        }

        private static ISugarQueryable<Location> ApplyLocationBarcodeFilter(
            ISugarQueryable<Location> query,
            LocationTextFilterMode mode,
            string value
        )
        {
            return mode switch
            {
                LocationTextFilterMode.Equals => query.Where(l =>
                    l.LocationBarcode != null && l.LocationBarcode == value
                ),
                LocationTextFilterMode.Starts => query.Where(l =>
                    l.LocationBarcode != null && l.LocationBarcode.StartsWith(value)
                ),
                LocationTextFilterMode.Ends => query.Where(l =>
                    l.LocationBarcode != null && l.LocationBarcode.EndsWith(value)
                ),
                _ => query.Where(l =>
                    l.LocationBarcode != null && l.LocationBarcode.Contains(value)
                ),
            };
        }

        private static ISugarQueryable<Location> ApplyUpdatedByFilter(
            ISugarQueryable<Location> query,
            LocationTextFilterMode mode,
            string value
        )
        {
            return mode switch
            {
                LocationTextFilterMode.Equals => query.Where(l =>
                    l.UpdatedBy != null && l.UpdatedBy == value
                ),
                LocationTextFilterMode.Starts => query.Where(l =>
                    l.UpdatedBy != null && l.UpdatedBy.StartsWith(value)
                ),
                LocationTextFilterMode.Ends => query.Where(l =>
                    l.UpdatedBy != null && l.UpdatedBy.EndsWith(value)
                ),
                _ => query.Where(l => l.UpdatedBy != null && l.UpdatedBy.Contains(value)),
            };
        }

        private static ISugarQueryable<Location> ApplyProductTextFilter(
            ISugarQueryable<Location> query,
            string key,
            IEnumerable<string> values
        )
        {
            var criteria = ParseTextFilterCriteria(values);
            foreach (var criterion in criteria)
            {
                var value = criterion.Value;
                // 商品列必须用 EXISTS 子查询，避免主查询 join 后一对多导致货位重复和分页漂移。
                query = key switch
                {
                    "productitemnumber" => ApplyProductItemNumberFilter(
                        query,
                        criterion.Mode,
                        value
                    ),
                    "productbarcode" => ApplyProductBarcodeFilter(query, criterion.Mode, value),
                    "productname" => ApplyProductNameFilter(query, criterion.Mode, value),
                    _ => query,
                };
            }

            return query;
        }

        private static ISugarQueryable<Location> ApplyProductItemNumberFilter(
            ISugarQueryable<Location> query,
            LocationTextFilterMode mode,
            string value
        )
        {
            return mode switch
            {
                LocationTextFilterMode.Equals => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.ItemNumber != null
                            && p.ItemNumber == value
                        )
                        .Any()
                ),
                LocationTextFilterMode.Starts => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.ItemNumber != null
                            && p.ItemNumber.StartsWith(value)
                        )
                        .Any()
                ),
                LocationTextFilterMode.Ends => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.ItemNumber != null
                            && p.ItemNumber.EndsWith(value)
                        )
                        .Any()
                ),
                _ => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.ItemNumber != null
                            && p.ItemNumber.Contains(value)
                        )
                        .Any()
                ),
            };
        }

        private static ISugarQueryable<Location> ApplyProductBarcodeFilter(
            ISugarQueryable<Location> query,
            LocationTextFilterMode mode,
            string value
        )
        {
            return mode switch
            {
                LocationTextFilterMode.Equals => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.Barcode != null
                            && p.Barcode == value
                        )
                        .Any()
                ),
                LocationTextFilterMode.Starts => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.Barcode != null
                            && p.Barcode.StartsWith(value)
                        )
                        .Any()
                ),
                LocationTextFilterMode.Ends => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.Barcode != null
                            && p.Barcode.EndsWith(value)
                        )
                        .Any()
                ),
                _ => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.Barcode != null
                            && p.Barcode.Contains(value)
                        )
                        .Any()
                ),
            };
        }

        private static ISugarQueryable<Location> ApplyProductNameFilter(
            ISugarQueryable<Location> query,
            LocationTextFilterMode mode,
            string value
        )
        {
            return mode switch
            {
                LocationTextFilterMode.Equals => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.ProductName != null
                            && p.ProductName == value
                        )
                        .Any()
                ),
                LocationTextFilterMode.Starts => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.ProductName != null
                            && p.ProductName.StartsWith(value)
                        )
                        .Any()
                ),
                LocationTextFilterMode.Ends => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.ProductName != null
                            && p.ProductName.EndsWith(value)
                        )
                        .Any()
                ),
                _ => query.Where(l =>
                    SqlFunc.Subqueryable<ProductLocation>()
                        .InnerJoin<Product>((pl, p) => pl.ProductCode == p.ProductCode && !p.IsDeleted)
                        .Where((pl, p) =>
                            !pl.IsDeleted
                            && pl.LocationGuid == l.LocationGuid
                            && p.ProductName != null
                            && p.ProductName.Contains(value)
                        )
                        .Any()
                ),
            };
        }

        /// <summary>
        /// 按单个状态码过滤货位（0 = 禁用，1 = 启用）。
        /// 注意不能写 <c>l.Status.HasValue &amp;&amp; ...</c>：SqlSugar 会生成 <c>[Status]&lt;&gt;''</c>，
        /// SQL Server 把 '' 隐式转成 int 0，状态码 0（禁用）会被整个排除。直接比较即可，NULL 不会等于任何值。
        /// </summary>
        internal static ISugarQueryable<Location> ApplyStatusEqualsFilter(
            ISugarQueryable<Location> query,
            int status
        )
        {
            return query.Where(l => l.Status == status);
        }

        internal static ISugarQueryable<Location> ApplyNullableIntFilter(
            ISugarQueryable<Location> query,
            string key,
            IEnumerable<string> values
        )
        {
            var intValues = values
                .Select(value => int.TryParse(value, out var intValue) ? intValue : (int?)null)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .Distinct()
                .ToList();

            if (intValues.Count == 0)
            {
                return query;
            }

            return key switch
            {
                // 不用 HasValue：见 ApplyStatusEqualsFilter 的说明，IN 本身不会命中 NULL。
                "status" => query.Where(l => intValues.Contains(l.Status!.Value)),
                "locationtype" => query.Where(l => intValues.Contains(l.LocationType!.Value)),
                _ => query,
            };
        }

        private static ISugarQueryable<Location> ApplyUpdatedAtFilter(
            ISugarQueryable<Location> query,
            List<string> values
        )
        {
            var bounds = ParseDateFilterBounds(values);
            if (bounds.Start.HasValue)
            {
                var start = bounds.Start.Value;
                query = query.Where(l => l.UpdatedAt >= start);
            }

            if (bounds.End.HasValue)
            {
                var end = bounds.End.Value;
                query = bounds.EndIsExclusive
                    ? query.Where(l => l.UpdatedAt < end)
                    : query.Where(l => l.UpdatedAt <= end);
            }

            return query;
        }

        private static List<string> NormalizeFilterValues(IEnumerable<string>? values)
        {
            return values?
                    .Select(value => value?.Trim())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .Distinct()
                    .ToList()
                ?? new List<string>();
        }

        private static List<LocationTextFilterCriterion> ParseTextFilterCriteria(
            IEnumerable<string> values
        )
        {
            return NormalizeFilterValues(values)
                .Select(ParseTextFilterCriterion)
                .Where(criterion => !string.IsNullOrWhiteSpace(criterion.Value))
                .Distinct()
                .ToList();
        }

        private static LocationTextFilterCriterion ParseTextFilterCriterion(string value)
        {
            var normalizedValue = value.Trim();
            var namespacePrefix = $"{FilterTokenNamespace}:";
            if (!normalizedValue.StartsWith(namespacePrefix, StringComparison.Ordinal))
            {
                return new LocationTextFilterCriterion(LocationTextFilterMode.Contains, normalizedValue);
            }

            var tokenBody = normalizedValue[namespacePrefix.Length..];
            var separatorIndex = tokenBody.IndexOf(':');
            if (separatorIndex <= 0)
            {
                return new LocationTextFilterCriterion(LocationTextFilterMode.Contains, normalizedValue);
            }

            var rawMode = tokenBody[..separatorIndex];
            var tokenValue = tokenBody[(separatorIndex + 1)..].Trim();
            var mode = rawMode switch
            {
                "eq" => LocationTextFilterMode.Equals,
                "starts" => LocationTextFilterMode.Starts,
                "ends" => LocationTextFilterMode.Ends,
                _ => LocationTextFilterMode.Contains,
            };

            return new LocationTextFilterCriterion(mode, tokenValue);
        }

        private static LocationDateFilterBounds ParseDateFilterBounds(List<string> values)
        {
            var bounds = new LocationDateFilterBounds();

            if (
                values.Count >= 2
                && !HasTokenPrefix(values[0])
                && !HasTokenPrefix(values[1])
            )
            {
                ApplyDateStart(values[0], bounds);
                ApplyDateEnd(values[1], bounds);
                return bounds;
            }

            foreach (var value in values)
            {
                if (value.StartsWith("gte:", StringComparison.Ordinal))
                {
                    ApplyDateStart(value["gte:".Length..], bounds);
                    continue;
                }

                if (value.StartsWith("lte:", StringComparison.Ordinal))
                {
                    ApplyDateEnd(value["lte:".Length..], bounds);
                    continue;
                }

                var parsedToken = ParseTextFilterCriterion(value);
                if (
                    parsedToken.Mode == LocationTextFilterMode.Equals
                    && DateTime.TryParse(parsedToken.Value, out var exactDate)
                )
                {
                    bounds.Start = exactDate.Date;
                    bounds.End = exactDate.Date.AddDays(1);
                    bounds.EndIsExclusive = true;
                    continue;
                }

                if (DateTime.TryParse(value, out var legacyExactDate))
                {
                    // 裸日期来自旧筛选形态，按当天精确匹配，避免单值日期变成无限上界查询。
                    bounds.Start = legacyExactDate.Date;
                    bounds.End = legacyExactDate.Date.AddDays(1);
                    bounds.EndIsExclusive = true;
                }
            }

            return bounds;
        }

        private static bool HasTokenPrefix(string value)
        {
            return value.StartsWith($"{FilterTokenNamespace}:", StringComparison.Ordinal)
                || value.StartsWith("gte:", StringComparison.Ordinal)
                || value.StartsWith("lte:", StringComparison.Ordinal);
        }

        private static void ApplyDateStart(string value, LocationDateFilterBounds bounds)
        {
            if (DateTime.TryParse(value, out var dateStart))
            {
                bounds.Start = IsDateOnlyValue(value) ? dateStart.Date : dateStart;
            }
        }

        private static void ApplyDateEnd(string value, LocationDateFilterBounds bounds)
        {
            if (!DateTime.TryParse(value, out var dateEnd))
            {
                return;
            }

            bounds.End = IsDateOnlyValue(value) ? dateEnd.Date.AddDays(1) : dateEnd;
            bounds.EndIsExclusive = IsDateOnlyValue(value);
        }

        private static bool IsDateOnlyValue(string value)
        {
            return Regex.IsMatch(value.Trim(), @"^\d{4}-\d{1,2}-\d{1,2}$");
        }

        private enum LocationTextFilterMode
        {
            Contains,
            Equals,
            Starts,
            Ends,
        }

        private readonly struct LocationTextFilterCriterion
        {
            public LocationTextFilterCriterion(LocationTextFilterMode mode, string value)
            {
                Mode = mode;
                Value = value;
            }

            public LocationTextFilterMode Mode { get; }

            public string Value { get; }
        }

        private sealed class LocationDateFilterBounds
        {
            public DateTime? Start { get; set; }

            public DateTime? End { get; set; }

            public bool EndIsExclusive { get; set; }
        }

        private ISugarQueryable<Location> ApplySorting(
            ISugarQueryable<Location> query,
            string sortBy,
            string sortDirection
        )
        {
            var isDescending = sortDirection.ToLower() == "desc";
            if (sortBy == "Usage")
            {
                return isDescending
                    ? query.OrderByDescending(l =>
                        SqlFunc.Subqueryable<ProductLocation>()
                            .InnerJoin<Product>((pl, p) =>
                                pl.ProductCode == p.ProductCode && !p.IsDeleted
                            )
                            .Where((pl, p) =>
                                !pl.IsDeleted && pl.LocationGuid == l.LocationGuid
                            )
                            .Any()
                    )
                    : query.OrderBy(l =>
                        SqlFunc.Subqueryable<ProductLocation>()
                            .InnerJoin<Product>((pl, p) =>
                                pl.ProductCode == p.ProductCode && !p.IsDeleted
                            )
                            .Where((pl, p) =>
                                !pl.IsDeleted && pl.LocationGuid == l.LocationGuid
                            )
                            .Any()
                    );
            }

            var orderByExpression = CreateOrderByExpression(sortBy);
            return isDescending
                ? query.OrderByDescending(orderByExpression)
                : query.OrderBy(orderByExpression);
        }

        private static Expression<Func<Location, object?>> CreateOrderByExpression(string sortBy)
        {
            return sortBy switch
            {
                "LocationCode" => l => l.LocationCode,
                "LocationBarcode" => l => l.LocationBarcode,
                "Status" => l => l.Status,
                "LocationType" => l => l.LocationType,
                "UpdatedAt" => l => l.UpdatedAt,
                "UpdatedBy" => l => l.UpdatedBy,
                _ => l => l.LocationCode,
            };
        }
    }
}
