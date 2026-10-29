using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models.HBweb;
using SqlSugar;

namespace BlazorApp.Api.Services.React
{
    public class PromotionReactService : IPromotionReactService
    {
        private readonly SqlSugarContext _context;
        private readonly ICurrentUserManageableStoreScopeService? _storeScopeService;

        public PromotionReactService(
            SqlSugarContext context,
            ICurrentUserManageableStoreScopeService? storeScopeService = null
        )
        {
            _context = context;
            _storeScopeService = storeScopeService;
        }

        internal static Expression<Func<Promotion, bool>> BuildValidWindowPredicate(DateTime now)
        {
            // 查询与边界测试共用同一表达式，确保开始和结束时间都采用包含式判断。
            return p =>
                !p.IsDeleted && p.IsEnabled && p.EffectiveStart <= now && p.EffectiveEnd >= now;
        }

        public async Task<GridResponseDto<PromotionListDto>> GetGridAsync(GridRequestDto request)
        {
            var query = _context.PromotionDb.AsQueryable();

            if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
            {
                var keyword = request.GlobalSearch.Trim();
                query = query.Where(s => s.Name.Contains(keyword));
            }

            if (request.SortModel != null && request.SortModel.Count > 0)
            {
                foreach (var sm in request.SortModel)
                {
                    if (sm.ColId.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        query =
                            sm.Sort == "asc"
                                ? query.OrderBy(s => s.Name)
                                : query.OrderBy(s => s.Name, OrderByType.Desc);
                    }
                    else if (sm.ColId.Equals("priority", StringComparison.OrdinalIgnoreCase))
                    {
                        query =
                            sm.Sort == "asc"
                                ? query.OrderBy(s => s.Priority)
                                : query.OrderBy(s => s.Priority, OrderByType.Desc);
                    }
                    else if (sm.ColId.Equals("effectiveStart", StringComparison.OrdinalIgnoreCase))
                    {
                        query =
                            sm.Sort == "asc"
                                ? query.OrderBy(s => s.EffectiveStart)
                                : query.OrderBy(s => s.EffectiveStart, OrderByType.Desc);
                    }
                }
            }

            var total = await query.CountAsync();
            var items = await query.Skip(request.StartRow).Take(request.PageSize).ToListAsync();
            var ids = items.Select(x => x.Id).ToList();
            var allProducts =
                ids.Count > 0
                    ? await _context
                        .PromotionProductDb.AsQueryable()
                        .Where(p => ids.Contains(p.PromotionId))
                        .ToListAsync()
                    : new List<PromotionProduct>();
            var allStores =
                ids.Count > 0
                    ? await _context
                        .PromotionStoreDb.AsQueryable()
                        .Where(s => ids.Contains(s.PromotionId))
                        .ToListAsync()
                    : new List<PromotionStore>();

            var list = items
                .Select(s => new PromotionListDto
                {
                    Id = s.Id,
                    Name = s.Name,
                    EffectiveStart = s.EffectiveStart,
                    EffectiveEnd = s.EffectiveEnd,
                    IsEnabled = s.IsEnabled,
                    IsExclusive = s.IsExclusive,
                    Priority = s.Priority,
                    ApplyQuantity = s.ApplyQuantity,
                    FixedPrice = s.FixedPrice,
                    ProductsCount = allProducts.Count(p => p.PromotionId == s.Id),
                    StoresCount = allStores.Count(t => t.PromotionId == s.Id),
                })
                .ToList();

            return GridResponseDto<PromotionListDto>.OK(list, total);
        }

        public async Task<ApiResponse<PromotionDetailDto>> GetByIdAsync(string id)
        {
            var s = await _context.PromotionDb.AsQueryable().InSingleAsync(id);
            if (s == null)
            {
                return new ApiResponse<PromotionDetailDto>
                {
                    Success = false,
                    Message = "not found",
                };
            }
            var products = await _context.PromotionProductDb.GetListAsync(d => d.PromotionId == id);
            var stores = await _context.PromotionStoreDb.GetListAsync(t => t.PromotionId == id);
            var dto = new PromotionDetailDto
            {
                Id = s.Id,
                Name = s.Name,
                Description = s.Description,
                EffectiveStart = s.EffectiveStart,
                EffectiveEnd = s.EffectiveEnd,
                IsEnabled = s.IsEnabled,
                IsExclusive = s.IsExclusive,
                Priority = s.Priority,
                ApplyQuantity = s.ApplyQuantity,
                FixedPrice = s.FixedPrice,
                MaxApplicationsPerOrder = s.MaxApplicationsPerOrder,
                Products = (products ?? new List<PromotionProduct>())
                    .Select(p => new PromotionProductItemDto
                    {
                        Id = p.Id,
                        ProductCode = p.ProductCode,
                        UnitWeight = p.UnitWeight,
                    })
                    .ToList(),
                Stores = (stores ?? new List<PromotionStore>())
                    .Select(t => new PromotionStoreItemDto { Id = t.Id, StoreCode = t.StoreCode })
                    .ToList(),
            };
            return ApiResponse<PromotionDetailDto>.OK(dto);
        }

        public async Task<ApiResponse<PromotionDetailDto>> CreateAsync(CreatePromotionDto dto)
        {
            // 分店必选：未绑定分店的促销收银端不计算，统一口径后不再允许创建。
            dto.Stores = NormalizeStoreItems(dto.Stores);
            if (dto.Stores.Count == 0)
            {
                return ApiResponse<PromotionDetailDto>.Error("请至少选择一家适用分店", PromotionStoreRequiredErrorCode);
            }

            var id = UuidHelper.GenerateUuid7();
            var entity = new Promotion
            {
                Id = id,
                Name = dto.Name,
                Description = dto.Description,
                EffectiveStart = dto.EffectiveStart,
                EffectiveEnd = dto.EffectiveEnd,
                IsEnabled = dto.IsEnabled,
                IsExclusive = dto.IsExclusive,
                Priority = dto.Priority,
                ApplyQuantity = dto.ApplyQuantity,
                FixedPrice = dto.FixedPrice,
                MaxApplicationsPerOrder = dto.MaxApplicationsPerOrder,
            };
            await _context.PromotionDb.InsertAsync(entity);

            if (dto.Products != null && dto.Products.Count > 0)
            {
                var rows = dto
                    .Products.Select(d => new PromotionProduct
                    {
                        Id = UuidHelper.GenerateUuid7(),
                        PromotionId = id,
                        ProductCode = d.ProductCode,
                        UnitWeight = d.UnitWeight,
                    })
                    .ToList();
                await _context.PromotionProductDb.InsertRangeAsync(rows);
            }
            if (dto.Stores != null && dto.Stores.Count > 0)
            {
                var rows = dto
                    .Stores.Select(t => new PromotionStore
                    {
                        Id = UuidHelper.GenerateUuid7(),
                        PromotionId = id,
                        StoreCode = t.StoreCode,
                    })
                    .ToList();
                await _context.PromotionStoreDb.InsertRangeAsync(rows);
            }

            return await GetByIdAsync(id);
        }

        public async Task<ApiResponse<PromotionDetailDto>> UpdateAsync(
            string id,
            UpdatePromotionDto dto
        )
        {
            // 分店必选：先校验再落库，避免把已有促销改成未绑定分店（收银不计算）。
            dto.Stores = NormalizeStoreItems(dto.Stores);
            if (dto.Stores.Count == 0)
            {
                return ApiResponse<PromotionDetailDto>.Error("请至少选择一家适用分店", PromotionStoreRequiredErrorCode);
            }

            var s = await _context.PromotionDb.AsQueryable().InSingleAsync(id);
            if (s == null)
            {
                return new ApiResponse<PromotionDetailDto>
                {
                    Success = false,
                    Message = "not found",
                };
            }
            s.Name = dto.Name;
            s.Description = dto.Description;
            s.EffectiveStart = dto.EffectiveStart;
            s.EffectiveEnd = dto.EffectiveEnd;
            s.IsEnabled = dto.IsEnabled;
            s.IsExclusive = dto.IsExclusive;
            s.Priority = dto.Priority;
            s.ApplyQuantity = dto.ApplyQuantity;
            s.FixedPrice = dto.FixedPrice;
            s.MaxApplicationsPerOrder = dto.MaxApplicationsPerOrder;
            await _context.PromotionDb.UpdateAsync(s);

            await _context.PromotionProductDb.DeleteAsync(d => d.PromotionId == id);
            await _context.PromotionStoreDb.DeleteAsync(t => t.PromotionId == id);

            if (dto.Products != null && dto.Products.Count > 0)
            {
                var rows = dto
                    .Products.Select(d => new PromotionProduct
                    {
                        Id = UuidHelper.GenerateUuid7(),
                        PromotionId = id,
                        ProductCode = d.ProductCode,
                        UnitWeight = d.UnitWeight,
                    })
                    .ToList();
                await _context.PromotionProductDb.InsertRangeAsync(rows);
            }

            if (dto.Stores != null && dto.Stores.Count > 0)
            {
                var rows = dto
                    .Stores.Select(t => new PromotionStore
                    {
                        Id = UuidHelper.GenerateUuid7(),
                        PromotionId = id,
                        StoreCode = t.StoreCode,
                    })
                    .ToList();
                await _context.PromotionStoreDb.InsertRangeAsync(rows);
            }

            return await GetByIdAsync(id);
        }

        public async Task<ApiResponse<bool>> DeleteAsync(string id)
        {
            await _context.PromotionProductDb.DeleteAsync(d => d.PromotionId == id);
            await _context.PromotionStoreDb.DeleteAsync(t => t.PromotionId == id);
            var ok = await _context.PromotionDb.DeleteAsync(s => s.Id == id);
            return new ApiResponse<bool>
            {
                Success = ok,
                Message = ok ? "ok" : "failed",
                Data = ok,
            };
        }

        public async Task<ApiResponse<bool>> EnableAsync(string id, bool enable)
        {
            var s = await _context.PromotionDb.AsQueryable().InSingleAsync(id);
            if (s == null)
            {
                return new ApiResponse<bool>
                {
                    Success = false,
                    Message = "not found",
                    Data = false,
                };
            }

            s.IsEnabled = enable;

            if (enable && s.IsExclusive)
            {
                var storeCodes = await _context
                    .PromotionStoreDb.AsQueryable()
                    .Where(x => x.PromotionId == id)
                    .Select(x => x.StoreCode)
                    .ToListAsync();

                if (storeCodes.Count > 0)
                {
                    var nowOverlap = await _context
                        .PromotionDb.AsQueryable()
                        .Where(p => p.IsEnabled && p.IsExclusive && p.Id != id)
                        .Where(p =>
                            p.EffectiveStart <= s.EffectiveEnd && p.EffectiveEnd >= s.EffectiveStart
                        )
                        .Where(p =>
                            SqlFunc
                                .Subqueryable<PromotionStore>()
                                .Where(ps =>
                                    ps.PromotionId == p.Id && storeCodes.Contains(ps.StoreCode)
                                )
                                .Any()
                        )
                        .ToListAsync();

                    if (nowOverlap.Any(x => x.Priority >= s.Priority))
                    {
                        return new ApiResponse<bool>
                        {
                            Success = false,
                            Message = "exclusive_conflict",
                            Data = false,
                        };
                    }
                }
            }

            var ok = await _context.PromotionDb.UpdateAsync(s);
            return new ApiResponse<bool>
            {
                Success = ok,
                Message = ok ? "ok" : "failed",
                Data = ok,
            };
        }

        public async Task<GridResponseDto<PromotionListDto>> GetStoreGridAsync(
            StorePromotionGridRequestDto request
        )
        {
            var storeCode = NormalizeStoreCode(request.StoreCode);
            if (!await CanAccessStoreAsync(storeCode))
            {
                return GridResponseDto<PromotionListDto>.Error("STORE_FORBIDDEN");
            }

            var promotionsQuery = _context.PromotionDb.AsQueryable();
            if (!string.IsNullOrWhiteSpace(request.GlobalSearch))
            {
                var keyword = request.GlobalSearch.Trim();
                promotionsQuery = promotionsQuery.Where(s => s.Name.Contains(keyword));
            }

            // 范围筛选下推到数据库：只看绑定了当前分店的促销。
            // 未绑定任何分店的促销收银端不计算（PromotionRuleService），店级页面也不再把它当成全店促销展示。
            promotionsQuery = promotionsQuery.Where(promotion =>
                SqlFunc
                    .Subqueryable<PromotionStore>()
                    .Where(store =>
                        store.PromotionId == promotion.Id && store.StoreCode == storeCode
                    )
                    .Any()
            );
            promotionsQuery = ApplyStoreStatusFilter(promotionsQuery, request.Status, DateTime.UtcNow);

            promotionsQuery = ApplyStoreGridQuerySorting(promotionsQuery, request.SortModel);
            var total = await promotionsQuery.CountAsync();
            var pageSize = request.PageSize > 0 ? request.PageSize : 50;
            var promotions = await promotionsQuery
                .Skip(Math.Max(0, request.StartRow))
                .Take(pageSize)
                .ToListAsync();
            var promotionIds = promotions.Select(item => item.Id).ToList();
            var promotionStores =
                promotionIds.Count > 0
                    ? await _context
                        .PromotionStoreDb.AsQueryable()
                        .Where(item => promotionIds.Contains(item.PromotionId))
                        .ToListAsync()
                    : new List<PromotionStore>();
            var promotionProducts =
                promotionIds.Count > 0
                    ? await _context
                        .PromotionProductDb.AsQueryable()
                        .Where(item => promotionIds.Contains(item.PromotionId))
                        .ToListAsync()
                    : new List<PromotionProduct>();

            var items = promotions
                .Select(promotion =>
                {
                    var stores = promotionStores
                        .Where(item => item.PromotionId == promotion.Id)
                        .ToList();
                    var scopeType = ResolveScopeType(stores, storeCode);
                    return new { Promotion = promotion, Stores = stores, ScopeType = scopeType };
                })
                .Where(item => item.ScopeType != null)
                .Select(item =>
                    ToListDto(
                        item.Promotion,
                        promotionProducts.Count(product => product.PromotionId == item.Promotion.Id),
                        item.Stores.Count,
                        item.ScopeType!
                    )
                )
                .ToList();
            return GridResponseDto<PromotionListDto>.OK(items, total);
        }

        public async Task<ApiResponse<PromotionStoreStatusCountsDto>> GetStoreStatusCountsAsync(
            string storeCode,
            string? keyword = null,
            DateTime? asOf = null
        )
        {
            storeCode = NormalizeStoreCode(storeCode);
            if (!await CanAccessStoreAsync(storeCode))
            {
                return ApiResponse<PromotionStoreStatusCountsDto>.Error("无权访问当前分店", "STORE_FORBIDDEN");
            }

            var now = asOf ?? DateTime.UtcNow;
            ISugarQueryable<Promotion> BaseQuery()
            {
                var query = _context
                    .PromotionDb.AsQueryable()
                    .Where(promotion =>
                        SqlFunc
                            .Subqueryable<PromotionStore>()
                            .Where(store =>
                                store.PromotionId == promotion.Id && store.StoreCode == storeCode
                            )
                            .Any()
                    );
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    var trimmed = keyword.Trim();
                    query = query.Where(promotion => promotion.Name.Contains(trimmed));
                }

                return query;
            }

            // 各分段与列表 Status 筛选共用同一套判定，保证数字和点进去看到的条数一致。
            var counts = new PromotionStoreStatusCountsDto
            {
                Active = await ApplyStoreStatusFilter(BaseQuery(), PromotionStoreStatusFilters.Active, now).CountAsync(),
                Upcoming = await ApplyStoreStatusFilter(BaseQuery(), PromotionStoreStatusFilters.Upcoming, now).CountAsync(),
                Disabled = await ApplyStoreStatusFilter(BaseQuery(), PromotionStoreStatusFilters.Disabled, now).CountAsync(),
                Ended = await ApplyStoreStatusFilter(BaseQuery(), PromotionStoreStatusFilters.Ended, now).CountAsync(),
                Total = await BaseQuery().CountAsync(),
            };
            return ApiResponse<PromotionStoreStatusCountsDto>.OK(counts);
        }

        public async Task<ApiResponse<List<PromotionListDto>>> GetStoreProductPromotionsAsync(
            string productCode,
            string storeCode,
            DateTime? asOf = null
        )
        {
            storeCode = NormalizeStoreCode(storeCode);
            if (!await CanAccessStoreAsync(storeCode))
            {
                return ApiResponse<List<PromotionListDto>>.Error("无权访问当前分店", "STORE_FORBIDDEN");
            }

            productCode = productCode?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(productCode))
            {
                return ApiResponse<List<PromotionListDto>>.OK(new List<PromotionListDto>());
            }

            var now = asOf ?? DateTime.UtcNow;
            // 商品查询页需要看到「还能管」的促销：未结束（含未开始、已停用），且绑定了当前分店、包含本商品。
            var promotions = await _context
                .PromotionDb.AsQueryable()
                .Where(p => !p.IsDeleted && p.EffectiveEnd >= now)
                .Where(p =>
                    SqlFunc
                        .Subqueryable<PromotionStore>()
                        .Where(ps => ps.PromotionId == p.Id && !ps.IsDeleted && ps.StoreCode == storeCode)
                        .Any()
                )
                .Where(p =>
                    SqlFunc
                        .Subqueryable<PromotionProduct>()
                        .Where(pp => pp.PromotionId == p.Id && !pp.IsDeleted && pp.ProductCode == productCode)
                        .Any()
                )
                .OrderBy(p => p.EffectiveStart, OrderByType.Asc)
                .OrderBy(p => p.Priority, OrderByType.Desc)
                .OrderBy(p => p.Id, OrderByType.Asc)
                .ToListAsync();

            var ids = promotions.Select(item => item.Id).ToList();
            var stores =
                ids.Count > 0
                    ? await _context
                        .PromotionStoreDb.AsQueryable()
                        .Where(item => ids.Contains(item.PromotionId) && !item.IsDeleted)
                        .ToListAsync()
                    : new List<PromotionStore>();
            var products =
                ids.Count > 0
                    ? await _context
                        .PromotionProductDb.AsQueryable()
                        .Where(item => ids.Contains(item.PromotionId) && !item.IsDeleted)
                        .ToListAsync()
                    : new List<PromotionProduct>();

            var list = promotions
                .Select(promotion =>
                {
                    var promotionStores = stores.Where(item => item.PromotionId == promotion.Id).ToList();
                    var scopeType = ResolveScopeType(promotionStores, storeCode);
                    return scopeType == null
                        ? null
                        : ToListDto(
                            promotion,
                            products.Count(item => item.PromotionId == promotion.Id),
                            promotionStores.Count,
                            scopeType
                        );
                })
                .Where(item => item != null)
                .Select(item => item!)
                .ToList();
            return ApiResponse<List<PromotionListDto>>.OK(list);
        }

        public async Task<ApiResponse<PromotionDetailDto>> GetStoreByIdAsync(
            string id,
            string storeCode
        )
        {
            storeCode = NormalizeStoreCode(storeCode);
            if (!await CanAccessStoreAsync(storeCode))
            {
                return ApiResponse<PromotionDetailDto>.Error("无权访问当前分店", "STORE_FORBIDDEN");
            }

            var detail = await BuildDetailAsync(id, storeCode);
            return detail == null
                ? ApiResponse<PromotionDetailDto>.Error("促销不存在或不属于当前分店", "PROMOTION_NOT_RELATED")
                : ApiResponse<PromotionDetailDto>.OK(detail);
        }

        public async Task<ApiResponse<PromotionDetailDto>> CreateStorePromotionAsync(
            string storeCode,
            CreatePromotionDto dto
        )
        {
            storeCode = NormalizeStoreCode(storeCode);
            if (!await CanAccessStoreAsync(storeCode))
            {
                return ApiResponse<PromotionDetailDto>.Error("无权访问当前分店", "STORE_FORBIDDEN");
            }

            // 店级创建只允许落到当前分店，优先级默认低于总部/多店模板，避免误覆盖。
            dto.Priority = 0;
            dto.Stores = BuildSingleStoreItems(storeCode);
            if (
                await HasExclusiveConflictAsync(
                    null,
                    dto.IsEnabled,
                    dto.IsExclusive,
                    dto.EffectiveStart,
                    dto.EffectiveEnd,
                    dto.Priority,
                    new[] { storeCode }
                )
            )
            {
                return ApiResponse<PromotionDetailDto>.Error("排他促销优先级冲突", "exclusive_conflict");
            }

            var result = await CreateAsync(dto);
            return result.Success && result.Data != null
                ? ApiResponse<PromotionDetailDto>.OK(
                    (await BuildDetailAsync(result.Data.Id, storeCode)) ?? result.Data
                )
                : result;
        }

        public async Task<ApiResponse<PromotionDetailDto>> UpdateStorePromotionAsync(
            string id,
            string storeCode,
            UpdatePromotionDto dto
        )
        {
            storeCode = NormalizeStoreCode(storeCode);
            if (!await CanAccessStoreAsync(storeCode))
            {
                return ApiResponse<PromotionDetailDto>.Error("无权访问当前分店", "STORE_FORBIDDEN");
            }

            var scopeType = await GetScopeTypeAsync(id, storeCode);
            if (scopeType != PromotionStoreScopeTypes.StoreOnly)
            {
                return ApiResponse<PromotionDetailDto>.Error(
                    "只有本店促销可以直接修改",
                    "PROMOTION_NOT_STORE_ONLY"
                );
            }

            // 店级更新继续强制单店归属，防止前端把本店促销扩成多店促销。
            dto.Stores = BuildSingleStoreItems(storeCode);
            if (
                await HasExclusiveConflictAsync(
                    id,
                    dto.IsEnabled,
                    dto.IsExclusive,
                    dto.EffectiveStart,
                    dto.EffectiveEnd,
                    dto.Priority,
                    new[] { storeCode }
                )
            )
            {
                return ApiResponse<PromotionDetailDto>.Error("排他促销优先级冲突", "exclusive_conflict");
            }

            return await UpdateAsync(id, dto);
        }

        public async Task<ApiResponse<PromotionDetailDto>> CopyToStoreAsync(
            CopyStorePromotionRequestDto dto
        )
        {
            var storeCode = NormalizeStoreCode(dto.StoreCode);
            if (!await CanAccessStoreAsync(storeCode))
            {
                return ApiResponse<PromotionDetailDto>.Error("无权访问当前分店", "STORE_FORBIDDEN");
            }

            var source = await _context.PromotionDb.AsQueryable().InSingleAsync(dto.SourcePromotionId);
            if (source == null)
            {
                return ApiResponse<PromotionDetailDto>.Error("源促销不存在", "PROMOTION_NOT_FOUND");
            }

            var sourceStores = await _context.PromotionStoreDb.GetListAsync(item =>
                item.PromotionId == dto.SourcePromotionId
            );
            var scopeType = ResolveScopeType(sourceStores ?? new List<PromotionStore>(), storeCode);
            if (scopeType != PromotionStoreScopeTypes.MultiStore)
            {
                return ApiResponse<PromotionDetailDto>.Error(
                    "只能复制覆盖本店的多店促销",
                    "PROMOTION_NOT_COPYABLE"
                );
            }

            var sourceProducts = await _context.PromotionProductDb.GetListAsync(item =>
                item.PromotionId == dto.SourcePromotionId
            );

            var createDto = new CreatePromotionDto
            {
                Name = string.IsNullOrWhiteSpace(dto.Name) ? $"{source.Name} - 本店副本" : dto.Name.Trim(),
                Description = source.Description,
                EffectiveStart = source.EffectiveStart,
                EffectiveEnd = source.EffectiveEnd,
                // 复制出的店级促销先停用，避免绕过启用时的排他冲突校验。
                IsEnabled = false,
                IsExclusive = source.IsExclusive,
                Priority = 0,
                ApplyQuantity = source.ApplyQuantity,
                FixedPrice = source.FixedPrice,
                MaxApplicationsPerOrder = source.MaxApplicationsPerOrder,
                Products = (sourceProducts ?? new List<PromotionProduct>())
                    .Select(item => new PromotionProductItemDto
                    {
                        ProductCode = item.ProductCode,
                        UnitWeight = item.UnitWeight,
                    })
                    .ToList(),
                Stores = BuildSingleStoreItems(storeCode),
            };

            // 复制不修改源促销，只生成当前分店可独立维护的新促销。
            var result = await CreateAsync(createDto);
            return result.Success && result.Data != null
                ? ApiResponse<PromotionDetailDto>.OK(
                    (await BuildDetailAsync(result.Data.Id, storeCode)) ?? result.Data
                )
                : result;
        }

        public async Task<ApiResponse<bool>> EnableStorePromotionAsync(
            string id,
            string storeCode,
            bool enable
        )
        {
            storeCode = NormalizeStoreCode(storeCode);
            if (!await CanAccessStoreAsync(storeCode))
            {
                return ApiResponse<bool>.Error("无权访问当前分店", "STORE_FORBIDDEN");
            }

            var scopeType = await GetScopeTypeAsync(id, storeCode);
            if (scopeType != PromotionStoreScopeTypes.StoreOnly)
            {
                return ApiResponse<bool>.Error("只有本店促销可以直接启停", "PROMOTION_NOT_STORE_ONLY");
            }

            return await EnableAsync(id, enable);
        }

        public async Task<ApiResponse<PromotionEvaluateResponse>> EvaluateAsync(
            PromotionEvaluateRequest req
        )
        {
            var now = DateTime.UtcNow;
            var enabledPromotions = await _context
                .PromotionDb.AsQueryable()
                .Where(p => p.IsEnabled && p.EffectiveStart <= now && p.EffectiveEnd >= now)
                .Where(p =>
                    SqlFunc
                        .Subqueryable<PromotionStore>()
                        .Where(ps => ps.PromotionId == p.Id && ps.StoreCode == req.StoreCode)
                        .Any()
                )
                .ToListAsync();

            if (enabledPromotions.Count == 0)
            {
                return ApiResponse<PromotionEvaluateResponse>.OK(new PromotionEvaluateResponse());
            }

            var productMap = await _context
                .PromotionProductDb.AsQueryable()
                .Where(pp => enabledPromotions.Select(x => x.Id).Contains(pp.PromotionId))
                .ToListAsync();

            var groupByPromotion = productMap
                .GroupBy(x => x.PromotionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            List<Promotion> candidates = enabledPromotions;
            var exclusive = candidates.Any(c => c.IsExclusive);
            if (exclusive)
            {
                candidates = candidates
                    .Where(c => c.IsExclusive)
                    .OrderByDescending(c => c.Priority)
                    .Take(1)
                    .ToList();
            }

            var resp = new PromotionEvaluateResponse();

            foreach (var promo in candidates)
            {
                if (promo.ApplyQuantity <= 0)
                    continue;

                if (!groupByPromotion.TryGetValue(promo.Id, out var pproducts))
                    continue;

                var setCodes = pproducts.Select(x => x.ProductCode).ToHashSet();
                var weights = pproducts.ToDictionary(x => x.ProductCode, x => x.UnitWeight);

                var itemsInSet = req.Items.Where(i => setCodes.Contains(i.ProductCode)).ToList();
                if (itemsInSet.Count == 0)
                    continue;

                var totalCount = 0;
                var expandedItems =
                    new List<(string productCode, decimal unitPrice, int weight, int qty)>();
                foreach (var item in itemsInSet)
                {
                    var w = weights.TryGetValue(item.ProductCode, out var ww) ? ww : 1;
                    totalCount += item.Qty * w;
                    expandedItems.Add((item.ProductCode, item.UnitPrice, w, item.Qty));
                }

                var bundles = totalCount / promo.ApplyQuantity;
                if (promo.MaxApplicationsPerOrder.HasValue)
                {
                    bundles = Math.Min(bundles, promo.MaxApplicationsPerOrder.Value);
                }

                if (bundles <= 0)
                    continue;

                var remaining = bundles * promo.ApplyQuantity;

                var cartUnits = new List<(string code, decimal price)>();
                foreach (var x in expandedItems)
                {
                    for (int i = 0; i < x.qty * x.weight; i++)
                    {
                        cartUnits.Add((x.productCode, x.unitPrice));
                    }
                }

                int index = 0;
                var appliedBundles = 0;
                var adjustedItems = new List<PriceAdjustmentDto>();
                while (remaining > 0 && index < cartUnits.Count)
                {
                    var take = Math.Min(remaining, promo.ApplyQuantity);
                    var group = cartUnits.Skip(index).Take(take).ToList();
                    var sum = group.Sum(g => g.price);
                    var target = promo.FixedPrice;
                    var discount = sum - target;
                    if (discount <= 0)
                    {
                        index += take;
                        remaining -= take;
                        continue;
                    }

                    resp.TotalDiscount += discount;
                    appliedBundles++;

                    foreach (var g in group)
                    {
                        var ratio = sum == 0 ? 0m : g.price / sum;
                        var newPrice = g.price - (discount * ratio);
                        adjustedItems.Add(
                            new PriceAdjustmentDto
                            {
                                ProductCode = g.code,
                                QtyAdjusted = 1,
                                AdjustedUnitPrice = Math.Round(
                                    newPrice,
                                    2,
                                    MidpointRounding.AwayFromZero
                                ),
                            }
                        );
                    }

                    index += take;
                    remaining -= take;
                }

                if (appliedBundles > 0)
                {
                    resp.AppliedPromotions.Add(
                        new AppliedPromotionInfo
                        {
                            PromotionId = promo.Id,
                            AppliedBundles = appliedBundles,
                        }
                    );
                    resp.AdjustedItems.AddRange(adjustedItems);
                }
            }

            return ApiResponse<PromotionEvaluateResponse>.OK(resp);
        }

        public async Task<ApiResponse<List<PromotionListDto>>> GetValidByStoreAsync(
            string storeCode,
            DateTime? asOf = null
        )
        {
            var now = asOf ?? DateTime.UtcNow;
            var query = _context
                .PromotionDb.AsQueryable()
                .Where(p => p.IsEnabled && p.EffectiveStart <= now && p.EffectiveEnd >= now)
                .Where(p =>
                    SqlFunc
                        .Subqueryable<PromotionStore>()
                        .Where(ps => ps.PromotionId == p.Id && ps.StoreCode == storeCode)
                        .Any()
                );

            var items = await query.ToListAsync();
            var ids = items.Select(x => x.Id).ToList();
            var allProducts =
                ids.Count > 0
                    ? await _context
                        .PromotionProductDb.AsQueryable()
                        .Where(p => ids.Contains(p.PromotionId))
                        .ToListAsync()
                    : new List<PromotionProduct>();
            var allStores =
                ids.Count > 0
                    ? await _context
                        .PromotionStoreDb.AsQueryable()
                        .Where(s => ids.Contains(s.PromotionId))
                        .ToListAsync()
                    : new List<PromotionStore>();

            var list = items
                .Select(s => new PromotionListDto
                {
                    Id = s.Id,
                    Name = s.Name,
                    EffectiveStart = s.EffectiveStart,
                    EffectiveEnd = s.EffectiveEnd,
                    IsEnabled = s.IsEnabled,
                    IsExclusive = s.IsExclusive,
                    Priority = s.Priority,
                    ApplyQuantity = s.ApplyQuantity,
                    FixedPrice = s.FixedPrice,
                    ProductsCount = allProducts.Count(p => p.PromotionId == s.Id),
                    StoresCount = allStores.Count(t => t.PromotionId == s.Id),
                })
                .ToList();

            return ApiResponse<List<PromotionListDto>>.OK(list);
        }

        public async Task<ApiResponse<List<PromotionListDto>>> GetValidByProductAndStoreAsync(
            string productCode,
            string storeCode,
            DateTime? asOf = null
        )
        {
            var now = asOf ?? DateTime.UtcNow;
            var query = _context
                .PromotionDb.AsQueryable()
                .Where(BuildValidWindowPredicate(now))
                .Where(p =>
                    SqlFunc
                        .Subqueryable<PromotionStore>()
                        .Where(ps =>
                            ps.PromotionId == p.Id && !ps.IsDeleted && ps.StoreCode == storeCode
                        )
                        .Any()
                // 未绑定任何分店的活动收银端不计算，这里也不再当成全店活动返回。
                )
                .Where(p =>
                    SqlFunc
                        .Subqueryable<PromotionProduct>()
                        .Where(pp =>
                            pp.PromotionId == p.Id
                            && !pp.IsDeleted
                            && pp.ProductCode == productCode
                        )
                        .Any()
                )
                .OrderBy(p => p.Priority, OrderByType.Desc)
                .OrderBy(p => p.EffectiveStart, OrderByType.Asc)
                .OrderBy(p => p.Id, OrderByType.Asc);

            var items = await query.ToListAsync();
            var ids = items.Select(x => x.Id).ToList();
            var allProducts =
                ids.Count > 0
                    ? await _context
                        .PromotionProductDb.AsQueryable()
                        .Where(p => ids.Contains(p.PromotionId) && !p.IsDeleted)
                        .ToListAsync()
                    : new List<PromotionProduct>();
            var allStores =
                ids.Count > 0
                    ? await _context
                        .PromotionStoreDb.AsQueryable()
                        .Where(s => ids.Contains(s.PromotionId) && !s.IsDeleted)
                        .ToListAsync()
                    : new List<PromotionStore>();

            var list = items
                .Select(s => new PromotionListDto
                {
                    Id = s.Id,
                    Name = s.Name,
                    EffectiveStart = s.EffectiveStart,
                    EffectiveEnd = s.EffectiveEnd,
                    IsEnabled = s.IsEnabled,
                    IsExclusive = s.IsExclusive,
                    Priority = s.Priority,
                    ApplyQuantity = s.ApplyQuantity,
                    FixedPrice = s.FixedPrice,
                    ProductsCount = allProducts.Count(p => p.PromotionId == s.Id),
                    StoresCount = allStores.Count(t => t.PromotionId == s.Id),
                })
                .ToList();

            return ApiResponse<List<PromotionListDto>>.OK(list);
        }

        private async Task<bool> CanAccessStoreAsync(string storeCode)
        {
            if (string.IsNullOrWhiteSpace(storeCode))
            {
                return false;
            }

            return _storeScopeService != null
                && await _storeScopeService.CanAccessStoreCodeAsync(storeCode);
        }

        private async Task<bool> HasExclusiveConflictAsync(
            string? promotionId,
            bool isEnabled,
            bool isExclusive,
            DateTime effectiveStart,
            DateTime effectiveEnd,
            int priority,
            IReadOnlyCollection<string> storeCodes
        )
        {
            if (!isEnabled || !isExclusive || storeCodes.Count == 0)
            {
                return false;
            }

            var query = _context
                .PromotionDb.AsQueryable()
                .Where(p => p.IsEnabled && p.IsExclusive)
                .Where(p => p.EffectiveStart <= effectiveEnd && p.EffectiveEnd >= effectiveStart);

            if (!string.IsNullOrWhiteSpace(promotionId))
            {
                query = query.Where(p => p.Id != promotionId);
            }

            var overlaps = await query
                .Where(p =>
                    SqlFunc
                        .Subqueryable<PromotionStore>()
                        .Where(ps =>
                            ps.PromotionId == p.Id && storeCodes.Contains(ps.StoreCode)
                        )
                        .Any()
                )
                .ToListAsync();

            return overlaps.Any(item => item.Priority >= priority);
        }

        private async Task<string?> GetScopeTypeAsync(string promotionId, string storeCode)
        {
            var stores = await _context.PromotionStoreDb.GetListAsync(item =>
                item.PromotionId == promotionId
            );
            return ResolveScopeType(stores ?? new List<PromotionStore>(), storeCode);
        }

        private async Task<PromotionDetailDto?> BuildDetailAsync(string promotionId, string storeCode)
        {
            var promotion = await _context.PromotionDb.AsQueryable().InSingleAsync(promotionId);
            if (promotion == null)
            {
                return null;
            }

            var products = await _context.PromotionProductDb.GetListAsync(item =>
                item.PromotionId == promotionId
            );
            var stores = await _context.PromotionStoreDb.GetListAsync(item =>
                item.PromotionId == promotionId
            );
            var scopeType = ResolveScopeType(stores ?? new List<PromotionStore>(), storeCode);
            if (scopeType == null)
            {
                return null;
            }

            return ToDetailDto(promotion, products ?? new List<PromotionProduct>(), stores ?? new List<PromotionStore>(), scopeType);
        }

        private static string NormalizeStoreCode(string? storeCode) => storeCode?.Trim() ?? string.Empty;

        public const string PromotionStoreRequiredErrorCode = "PROMOTION_STORE_REQUIRED";

        /// <summary>去掉空白与重复的分店码（忽略大小写），保留首次出现的顺序。</summary>
        private static List<PromotionStoreItemDto> NormalizeStoreItems(List<PromotionStoreItemDto>? stores)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<PromotionStoreItemDto>();
            foreach (var store in stores ?? new List<PromotionStoreItemDto>())
            {
                var code = NormalizeStoreCode(store?.StoreCode);
                if (code.Length > 0 && seen.Add(code))
                {
                    result.Add(new PromotionStoreItemDto { StoreCode = code });
                }
            }

            return result;
        }

        private static List<PromotionStoreItemDto> BuildSingleStoreItems(string storeCode) =>
            new() { new PromotionStoreItemDto { StoreCode = storeCode } };

        private static string? ResolveScopeType(
            IReadOnlyCollection<PromotionStore> stores,
            string storeCode
        )
        {
            // 范围判定是店级页面的权限边界：只有本店独享可编辑，多店只能复制。
            // 未绑定任何分店的促销收银端不计算，对任何分店都视为无关（不再当作总部/全店促销）。
            if (stores.Count == 0)
            {
                return null;
            }

            var containsCurrentStore = stores.Any(item =>
                item.StoreCode.Equals(storeCode, StringComparison.OrdinalIgnoreCase)
            );
            if (!containsCurrentStore)
            {
                return null;
            }

            return stores.Count == 1
                ? PromotionStoreScopeTypes.StoreOnly
                : PromotionStoreScopeTypes.MultiStore;
        }

        private static PromotionListDto ToListDto(
            Promotion promotion,
            int productsCount,
            int storesCount,
            string? scopeType = null
        )
        {
            return new PromotionListDto
            {
                Id = promotion.Id,
                Name = promotion.Name,
                EffectiveStart = promotion.EffectiveStart,
                EffectiveEnd = promotion.EffectiveEnd,
                IsEnabled = promotion.IsEnabled,
                IsExclusive = promotion.IsExclusive,
                Priority = promotion.Priority,
                ApplyQuantity = promotion.ApplyQuantity,
                FixedPrice = promotion.FixedPrice,
                ProductsCount = productsCount,
                StoresCount = storesCount,
                ScopeType = scopeType,
                CanEditInStoreScope = scopeType == PromotionStoreScopeTypes.StoreOnly,
                CanCopyToStore = scopeType == PromotionStoreScopeTypes.MultiStore,
            };
        }

        private static PromotionDetailDto ToDetailDto(
            Promotion promotion,
            IReadOnlyCollection<PromotionProduct> products,
            IReadOnlyCollection<PromotionStore> stores,
            string? scopeType = null
        )
        {
            return new PromotionDetailDto
            {
                Id = promotion.Id,
                Name = promotion.Name,
                Description = promotion.Description,
                EffectiveStart = promotion.EffectiveStart,
                EffectiveEnd = promotion.EffectiveEnd,
                IsEnabled = promotion.IsEnabled,
                IsExclusive = promotion.IsExclusive,
                Priority = promotion.Priority,
                ApplyQuantity = promotion.ApplyQuantity,
                FixedPrice = promotion.FixedPrice,
                MaxApplicationsPerOrder = promotion.MaxApplicationsPerOrder,
                Products = products
                    .Select(item => new PromotionProductItemDto
                    {
                        Id = item.Id,
                        ProductCode = item.ProductCode,
                        UnitWeight = item.UnitWeight,
                    })
                    .ToList(),
                Stores = stores
                    .Select(item => new PromotionStoreItemDto
                    {
                        Id = item.Id,
                        StoreCode = item.StoreCode,
                    })
                    .ToList(),
                ScopeType = scopeType,
                CanEditInStoreScope = scopeType == PromotionStoreScopeTypes.StoreOnly,
                CanCopyToStore = scopeType == PromotionStoreScopeTypes.MultiStore,
            };
        }

        /// <summary>
        /// 店级状态分段：生效中=启用且在有效期内；未开始=启用且未到开始时间；
        /// 已停用=停用且未结束；已结束=结束时间已过（不论启停）。四段互斥，合计等于全部；
        /// notended 是「全部」去掉已结束的组合筛选，不是独立分段（不参与计数）。
        /// </summary>
        private static ISugarQueryable<Promotion> ApplyStoreStatusFilter(
            ISugarQueryable<Promotion> query,
            string? status,
            DateTime now
        )
        {
            return (status ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                PromotionStoreStatusFilters.Active => query.Where(p =>
                    p.IsEnabled && p.EffectiveStart <= now && p.EffectiveEnd >= now
                ),
                PromotionStoreStatusFilters.Upcoming => query.Where(p =>
                    p.IsEnabled && p.EffectiveStart > now && p.EffectiveEnd >= now
                ),
                PromotionStoreStatusFilters.Disabled => query.Where(p =>
                    !p.IsEnabled && p.EffectiveEnd >= now
                ),
                PromotionStoreStatusFilters.Ended => query.Where(p => p.EffectiveEnd < now),
                // 未结束=全部去掉已结束（含已停用但未到期的），供移动端「全部」分段隐藏已结束。
                PromotionStoreStatusFilters.NotEnded => query.Where(p => p.EffectiveEnd >= now),
                _ => query,
            };
        }

        private static ISugarQueryable<Promotion> ApplyStoreGridQuerySorting(
            ISugarQueryable<Promotion> query,
            List<SortModelDto>? sortModel
        )
        {
            if (sortModel == null || sortModel.Count == 0)
            {
                return query.OrderBy(item => item.EffectiveStart, OrderByType.Desc).OrderBy(item => item.Name);
            }

            foreach (var sort in sortModel)
            {
                var descending = sort.Sort.Equals("desc", StringComparison.OrdinalIgnoreCase);
                query = sort.ColId.ToLowerInvariant() switch
                {
                    "name" => descending
                        ? query.OrderBy(item => item.Name, OrderByType.Desc)
                        : query.OrderBy(item => item.Name),
                    "priority" => descending
                        ? query.OrderBy(item => item.Priority, OrderByType.Desc)
                        : query.OrderBy(item => item.Priority),
                    "effectivestart" => descending
                        ? query.OrderBy(item => item.EffectiveStart, OrderByType.Desc)
                        : query.OrderBy(item => item.EffectiveStart),
                    "effectiveend" => descending
                        ? query.OrderBy(item => item.EffectiveEnd, OrderByType.Desc)
                        : query.OrderBy(item => item.EffectiveEnd),
                    _ => descending
                        ? query.OrderBy(item => item.EffectiveStart, OrderByType.Desc)
                        : query.OrderBy(item => item.EffectiveStart),
                };
            }

            return query;
        }

        private static List<PromotionListDto> ApplyStoreGridSorting(
            List<PromotionListDto> items,
            List<SortModelDto>? sortModel
        )
        {
            if (sortModel == null || sortModel.Count == 0)
            {
                return items
                    .OrderByDescending(item => item.EffectiveStart)
                    .ThenBy(item => item.Name)
                    .ToList();
            }

            IOrderedEnumerable<PromotionListDto>? ordered = null;
            foreach (var sort in sortModel)
            {
                Func<PromotionListDto, object> selector = sort.ColId.ToLowerInvariant() switch
                {
                    "name" => item => item.Name,
                    "priority" => item => item.Priority,
                    "effectivestart" => item => item.EffectiveStart,
                    "effectiveend" => item => item.EffectiveEnd,
                    "scopetype" => item => item.ScopeType ?? string.Empty,
                    _ => item => item.EffectiveStart,
                };

                var descending = sort.Sort.Equals("desc", StringComparison.OrdinalIgnoreCase);
                ordered =
                    ordered == null
                        ? descending
                            ? items.OrderByDescending(selector)
                            : items.OrderBy(selector)
                        : descending
                            ? ordered.ThenByDescending(selector)
                            : ordered.ThenBy(selector);
            }

            return (ordered ?? items.OrderBy(item => item.Name)).ToList();
        }
    }
}
