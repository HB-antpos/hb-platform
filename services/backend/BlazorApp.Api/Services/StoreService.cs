using System.Collections.Concurrent;
using System.Threading;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 分店服务实现
    /// 提供分店管理的增删改查、状态控制、用户关联管理等核心业务功能
    /// 支持多分店体系、用户分配、批量用户操作等功能
    /// </summary>
    public class StoreService : IStoreService
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> HqBranchSyncLocks =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> AllowedBatchUpdateFields = new(
            new[]
            {
                StoreBatchUpdateFieldNames.TimeZoneId,
                StoreBatchUpdateFieldNames.ABN,
                StoreBatchUpdateFieldNames.BrandName,
                StoreBatchUpdateFieldNames.IsActive,
                StoreBatchUpdateFieldNames.ReturnPolicy,
                StoreBatchUpdateFieldNames.VoucherTerms,
                StoreBatchUpdateFieldNames.InstallmentTerms,
            },
            StringComparer.Ordinal
        );

        // 数据库上下文，用于数据库操作
        private readonly SqlSugarContext _context;

        // HQ数据库上下文，用于把本地分店资料同步到总部系统。
        private readonly HqSqlSugarContext? _hqContext;

        // 日志记录器，用于记录操作日志和错误信息
        private readonly ILogger<StoreService> _logger;

        // 用户访问写入需要识别当前操作者；保留可选注入以兼容后台任务与既有测试。
        private readonly ICurrentUserManageableStoreScopeService? _manageableStoreScopeService;

        /// <summary>
        /// 构造函数：初始化分店服务
        /// </summary>
        /// <param name="context">数据库上下文</param>
        /// <param name="logger">日志记录器</param>
        /// <param name="hqContext">HQ数据库上下文</param>
        /// <param name="manageableStoreScopeService">当前操作者的可管理分店范围</param>
        public StoreService(
            SqlSugarContext context,
            ILogger<StoreService> logger,
            HqSqlSugarContext? hqContext = null,
            ICurrentUserManageableStoreScopeService? manageableStoreScopeService = null
        )
        {
            _context = context;
            _hqContext = hqContext;
            _logger = logger;
            _manageableStoreScopeService = manageableStoreScopeService;
        }

        /// <summary>
        /// 获取所有未删除的分店列表（按名称排序）
        /// </summary>
        /// <returns>分店列表</returns>
        public async Task<ApiResponse<List<StoreDto>>> GetAllStoresByNameAsync()
        {
            try
            {
                var db = _context.Db;

                var stores = await db.Queryable<Store>()
                    .Where(s => s.IsDeleted == false)
                    .OrderBy(s => s.StoreName)
                    .Select(s => new StoreDto
                    {
                        StoreGUID = s.StoreGUID,
                        StoreName = s.StoreName,
                        StoreCode = s.StoreCode,
                        ABN = s.ABN,
                        BrandName = s.BrandName,
                        Address = s.Address,
                        TimeZoneId = s.TimeZoneId,
                        ReturnPolicy = s.ReturnPolicy,
                        VoucherTerms = s.VoucherTerms,
                        InstallmentTerms = s.InstallmentTerms,
                        ContactPhone = s.Phone,
                        ContactEmail = s.ContactEmail,
                        IsActive = s.IsActive,
                        CreatedAt = s.CreatedAt,
                        UpdatedAt = s.UpdatedAt ?? s.CreatedAt,
                    })
                    .ToListAsync();

                return ApiResponse<List<StoreDto>>.OK(stores, "获取所有分店列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取所有分店列表失败");
                return ApiResponse<List<StoreDto>>.Error(
                    "获取所有分店列表失败",
                    "GET_ALL_STORES_ERROR"
                );
            }
        }

        /// <summary>
        /// 获取所有激活的分店列表（用于数据同步分店选择）
        /// </summary>
        /// <returns>激活的分店列表</returns>
        public async Task<ApiResponse<List<StoreDto>>> GetActiveStoresAsync()
        {
            try
            {
                var db = _context.Db;

                // 查询所有激活状态的分店
                var activeStores = await db.Queryable<Store>()
                    .Where(s => s.IsActive == true && s.IsDeleted == false)
                    .Select(s => new StoreDto
                    {
                        StoreGUID = s.StoreGUID,
                        StoreName = s.StoreName,
                        StoreCode = s.StoreCode,
                        ABN = s.ABN,
                        BrandName = s.BrandName,
                        Address = s.Address,
                        TimeZoneId = s.TimeZoneId,
                        ReturnPolicy = s.ReturnPolicy,
                        VoucherTerms = s.VoucherTerms,
                        InstallmentTerms = s.InstallmentTerms,
                        ContactPhone = s.Phone,
                        ContactEmail = s.ContactEmail,
                        IsActive = s.IsActive,
                    })
                    .OrderBy(s => s.StoreCode)
                    .ToListAsync();

                _logger.LogInformation($"成功获取 {activeStores.Count} 个激活分店");

                return ApiResponse<List<StoreDto>>.OK(activeStores, "获取激活分店列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取激活分店列表失败");
                return ApiResponse<List<StoreDto>>.Error(
                    "获取激活分店列表失败",
                    "STORE_QUERY_ERROR"
                );
            }
        }

        /// <summary>
        /// 获取分店列表
        /// 支持分店搜索、状态筛选、用户关联筛选，包含用户统计信息
        /// 采用分步查询优化性能，避免复杂的连表统计查询
        /// </summary>
        /// <param name="query">分店查询条件</param>
        /// <returns>分页的分店列表，包含用户统计信息</returns>
        public async Task<ApiResponse<PagedResult<StoreDto>>> GetStoresAsync(StoreQueryDto query)
        {
            try
            {
                var db = _context.Db;

                // 1. 构建基础分店查询
                var storeQuery = db.Queryable<Store>();

                // 2. 关键字搜索条件 - 支持分店名称和分店代码模糊匹配
                if (!string.IsNullOrEmpty(query.Search))
                {
                    storeQuery = storeQuery.Where(s =>
                        s.StoreName.Contains(query.Search) || s.StoreCode.Contains(query.Search)
                    );
                }

                // 3. 分店状态筛选条件
                if (query.IsActive.HasValue)
                {
                    storeQuery = storeQuery.Where(s => s.IsActive == query.IsActive.Value);
                }

                // 4. 品牌名称精确筛选，表格列筛选传入完整品牌名。
                var brandName = query.BrandName?.Trim();
                if (!string.IsNullOrEmpty(brandName))
                {
                    storeQuery = storeQuery.Where(s => s.BrandName == brandName);
                }

                // 4.1 分店时区精确筛选：哨兵值 "__unset__" 匹配 NULL/空字符串，其余按存储值精确匹配。
                var timeZoneId = query.TimeZoneId?.Trim();
                if (!string.IsNullOrEmpty(timeZoneId))
                {
                    if (timeZoneId == "__unset__")
                    {
                        storeQuery = storeQuery.Where(s =>
                            s.TimeZoneId == null || s.TimeZoneId == ""
                        );
                    }
                    else
                    {
                        storeQuery = storeQuery.Where(s => s.TimeZoneId == timeZoneId);
                    }
                }

                // 5. 用户关联筛选条件 - 使用子查询优化性能
                if (!string.IsNullOrEmpty(query.UserGUID))
                {
                    var userStoreGuids = await db.Queryable<UserStore>()
                        .Where(us => us.UserGUID == query.UserGUID)
                        .Select(us => us.StoreGUID)
                        .ToListAsync();
                    storeQuery = storeQuery.Where(s => userStoreGuids.Contains(s.StoreGUID));
                }

                // 6. 获取符合条件的总记录数
                var total = await storeQuery.CountAsync();

                List<string> storeGuids;
                if (IsTotalUsersSort(query.SortField))
                {
                    // 用户数是关联表聚合字段，先在服务端取匹配分店再按统计值排序后分页。
                    storeGuids = await GetPagedStoreGuidsSortedByTotalUsersAsync(
                        db,
                        storeQuery,
                        query.SortOrder,
                        query.Page,
                        query.PageSize
                    );
                }
                else
                {
                    // 7. 应用普通分店字段排序
                    storeQuery = ApplySorting(storeQuery, query.SortField, query.SortOrder);

                    // 8. 获取当前页分店GUID列表，用于后续的用户统计查询
                    storeGuids = await storeQuery
                        .Skip((query.Page - 1) * query.PageSize) // 分页跳过
                        .Take(query.PageSize) // 分页取数量
                        .Select(s => s.StoreGUID) // 只选择GUID
                        .ToListAsync();
                }

                // 9. 批量获取用户统计信息 - 分步查询优化性能
                // 9.1 获取每个分店的用户总数
                var totalUserStats = await db.Queryable<UserStore>()
                    .Where(us => storeGuids.Contains(us.StoreGUID))
                    .GroupBy(us => us.StoreGUID)
                    .Select(group => new
                    {
                        StoreGUID = group.StoreGUID,
                        TotalUsers = SqlFunc.AggregateCount(group.StoreGUID), // 用户总数
                    })
                    .ToListAsync();

                // 9.2 获取每个分店的活跃用户数
                var activeUserStats = await db.Queryable<UserStore>()
                    .InnerJoin<User>((us, u) => us.UserGUID == u.UserGUID)
                    .Where((us, u) => storeGuids.Contains(us.StoreGUID) && u.IsActive)
                    .GroupBy(us => us.StoreGUID)
                    .Select(us => new
                    {
                        StoreGUID = us.StoreGUID,
                        ActiveUsers = SqlFunc.AggregateCount(us.StoreGUID), // 活跃用户数
                    })
                    .ToListAsync();

                var userStatsDict = totalUserStats.ToDictionary(
                    x => x.StoreGUID,
                    x => new { x.TotalUsers, ActiveUsers = 0 }
                );
                foreach (var activeStat in activeUserStats)
                {
                    if (userStatsDict.ContainsKey(activeStat.StoreGUID))
                    {
                        userStatsDict[activeStat.StoreGUID] = new
                        {
                            userStatsDict[activeStat.StoreGUID].TotalUsers,
                            activeStat.ActiveUsers,
                        };
                    }
                }

                // 获取分店详情，保持与查询相同的排序顺序
                var storeDetailQuery = db.Queryable<Store>()
                    .Where(s => storeGuids.Contains(s.StoreGUID));

                var stores = await storeDetailQuery
                    .Select(s => new StoreDto
                    {
                        StoreGUID = s.StoreGUID,
                        StoreName = s.StoreName,
                        StoreCode = s.StoreCode,
                        ABN = s.ABN,
                        BrandName = s.BrandName,
                        Description = null,
                        Address = s.Address,
                        TimeZoneId = s.TimeZoneId,
                        ReturnPolicy = s.ReturnPolicy,
                        VoucherTerms = s.VoucherTerms,
                        InstallmentTerms = s.InstallmentTerms,
                        ContactPhone = s.Phone,
                        ContactEmail = s.ContactEmail,
                        IsActive = s.IsActive,
                        CreatedAt = s.CreatedAt,
                        UpdatedAt = s.UpdatedAt ?? s.CreatedAt,
                        TotalUsers = 0, // 将在内存中填充
                        ActiveUsers = 0, // 将在内存中填充
                    })
                    .ToListAsync();

                var storeGuidOrder = storeGuids
                    .Select((storeGuid, index) => new { storeGuid, index })
                    .ToDictionary(x => x.storeGuid, x => x.index);

                stores = stores
                    .OrderBy(store => storeGuidOrder.TryGetValue(store.StoreGUID, out var index) ? index : int.MaxValue)
                    .ToList();

                // 在内存中填充用户统计信息
                foreach (var store in stores)
                {
                    if (userStatsDict.ContainsKey(store.StoreGUID))
                    {
                        store.TotalUsers = userStatsDict[store.StoreGUID].TotalUsers;
                        store.ActiveUsers = userStatsDict[store.StoreGUID].ActiveUsers;
                    }
                }

                var result = new PagedResult<StoreDto>
                {
                    Items = stores,
                    Total = total,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<StoreDto>>.OK(result, "获取分店列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分店列表失败");
                return ApiResponse<PagedResult<StoreDto>>.Error(
                    "获取分店列表失败",
                    "GET_STORES_ERROR"
                );
            }
        }

        private static bool IsTotalUsersSort(string? sortField)
        {
            return string.Equals(sortField, "totalUsers", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDescendingSort(string? sortOrder)
        {
            return !string.IsNullOrEmpty(sortOrder) && sortOrder.ToLower() == "desc";
        }

        private async Task<List<string>> GetPagedStoreGuidsSortedByTotalUsersAsync(
            ISqlSugarClient db,
            ISugarQueryable<Store> storeQuery,
            string? sortOrder,
            int page,
            int pageSize
        )
        {
            var matchedStores = await storeQuery
                .Select(s => new
                {
                    s.StoreGUID,
                    s.StoreName,
                })
                .ToListAsync();

            var matchedStoreGuids = matchedStores.Select(s => s.StoreGUID).ToList();
            var totalUserStats = await db.Queryable<UserStore>()
                .Where(us => matchedStoreGuids.Contains(us.StoreGUID))
                .GroupBy(us => us.StoreGUID)
                .Select(group => new
                {
                    StoreGUID = group.StoreGUID,
                    TotalUsers = SqlFunc.AggregateCount(group.StoreGUID),
                })
                .ToListAsync();

            var totalUsersByStoreGuid = totalUserStats.ToDictionary(x => x.StoreGUID, x => x.TotalUsers);
            var orderedStores = IsDescendingSort(sortOrder)
                ? matchedStores
                    .OrderByDescending(store => totalUsersByStoreGuid.TryGetValue(store.StoreGUID, out var totalUsers) ? totalUsers : 0)
                    .ThenBy(store => store.StoreName)
                : matchedStores
                    .OrderBy(store => totalUsersByStoreGuid.TryGetValue(store.StoreGUID, out var totalUsers) ? totalUsers : 0)
                    .ThenBy(store => store.StoreName);

            return orderedStores
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(store => store.StoreGUID)
                .ToList();
        }

        /// <summary>
        /// 根据GUID获取分店详情
        /// </summary>
        public async Task<ApiResponse<StoreDetailDto>> GetStoreByGuidAsync(string guid)
        {
            try
            {
                var db = _context.Db;
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == guid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<StoreDetailDto>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                return await GetStoreDetailAsync(store);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分店详情失败，GUID: {StoreGUID}", guid);
                return ApiResponse<StoreDetailDto>.Error(
                    "获取分店详情失败",
                    "GET_STORE_DETAIL_ERROR"
                );
            }
        }

        /// <summary>
        /// 根据分店代码获取分店信息
        /// </summary>
        public async Task<ApiResponse<StoreDto>> GetStoreByCodeAsync(string storeCode)
        {
            try
            {
                var db = _context.Db;
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreCode == storeCode)
                    .FirstAsync();

                if (store == null)
                {
                    _logger.LogWarning("根据分店代码未找到分店: {StoreCode}", storeCode);
                    return ApiResponse<StoreDto>.Error("分店不存在或已停用", "STORE_NOT_FOUND");
                }

                var result = new StoreDto
                {
                    StoreGUID = store.StoreGUID,
                    StoreName = store.StoreName,
                    StoreCode = store.StoreCode,
                    ABN = store.ABN,
                    BrandName = store.BrandName,
                    Address = store.Address,
                    TimeZoneId = store.TimeZoneId,
                    ReturnPolicy = store.ReturnPolicy,
                    VoucherTerms = store.VoucherTerms,
                    InstallmentTerms = store.InstallmentTerms,
                    ContactPhone = store.Phone,
                    ContactEmail = store.ContactEmail,
                    IsActive = store.IsActive,
                    CreatedAt = store.CreatedAt,
                    UpdatedAt = store.UpdatedAt ?? DateTime.UtcNow,
                };

                _logger.LogInformation(
                    "成功根据分店代码获取分店信息: {StoreCode} -> {StoreName}",
                    storeCode,
                    store.StoreName
                );
                return ApiResponse<StoreDto>.OK(result, "获取分店信息成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据分店代码获取分店信息失败: {StoreCode}", storeCode);
                return ApiResponse<StoreDto>.Error("获取分店信息失败", "GET_STORE_BY_CODE_ERROR");
            }
        }

        /// <summary>
        /// 获取下一个建议分店编码
        /// </summary>
        public async Task<ApiResponse<string>> GetNextStoreCodeAsync()
        {
            try
            {
                var storeCodes = await _context.Db.Queryable<Store>()
                    .Where(store => !store.IsDeleted && store.StoreCode != null && store.StoreCode != "")
                    .Select(store => store.StoreCode)
                    .ToListAsync();

                var maxCode = 0;
                foreach (var storeCode in storeCodes)
                {
                    var normalizedCode = storeCode?.Trim();
                    // 自动编码只基于纯数字分店编码，避免 S001/GUID 等历史标识干扰。
                    if (
                        !string.IsNullOrWhiteSpace(normalizedCode)
                        && normalizedCode.All(char.IsDigit)
                        && int.TryParse(normalizedCode, out var numericCode)
                        && numericCode > maxCode
                    )
                    {
                        maxCode = numericCode;
                    }
                }

                var nextCode = Math.Max(maxCode + 1, 1001).ToString();
                return ApiResponse<string>.OK(nextCode, "获取下一个分店编码成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取下一个分店编码失败");
                return ApiResponse<string>.Error("获取下一个分店编码失败", "GET_NEXT_STORE_CODE_ERROR");
            }
        }

        /// <summary>
        /// 根据GUID更新分店
        /// </summary>
        public async Task<ApiResponse<StoreDto>> UpdateStoreByGuidAsync(
            string guid,
            UpdateStoreDto dto
        )
        {
            try
            {
                var db = _context.Db;
                if (!StoreTimeZonePolicy.TryNormalize(dto.TimeZoneId, out var normalizedTimeZoneId))
                {
                    return ApiResponse<StoreDto>.Error(
                        "门店时区仅支持 Australia/Brisbane、Australia/Sydney 或 Australia/Melbourne",
                        "INVALID_STORE_TIME_ZONE"
                    );
                }

                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == guid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<StoreDto>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                // 检查分店代码是否重复（排除当前分店）
                var existingStore = await db.Queryable<Store>()
                    .Where(s => s.StoreCode == dto.StoreCode && s.StoreGUID != guid)
                    .FirstAsync();

                if (existingStore != null)
                {
                    return ApiResponse<StoreDto>.Error(
                        "分店代码已存在",
                        "DUPLICATE_STORE_CODE",
                        new { storeCode = dto.StoreCode }
                    );
                }

                // 更新分店信息
                store.StoreName = dto.StoreName;
                store.StoreCode = dto.StoreCode;
                store.ABN = dto.ABN;
                store.BrandName = dto.BrandName;
                store.Address = dto.Address;
                store.ReturnPolicy = dto.ReturnPolicy;
                store.VoucherTerms = dto.VoucherTerms;
                store.InstallmentTerms = dto.InstallmentTerms;
                if (!string.IsNullOrWhiteSpace(dto.TimeZoneId))
                {
                    store.TimeZoneId = normalizedTimeZoneId;
                }
                store.Phone = dto.ContactPhone;
                store.ContactEmail = dto.ContactEmail;
                store.IsActive = dto.IsActive;
                store.UpdatedAt = DateTime.UtcNow;

                await db.Updateable(store).ExecuteCommandAsync();

                var result = new StoreDto
                {
                    StoreGUID = store.StoreGUID,
                    StoreName = store.StoreName,
                    StoreCode = store.StoreCode,
                    ABN = store.ABN,
                    BrandName = store.BrandName,
                    Description = null,
                    Address = store.Address,
                    TimeZoneId = store.TimeZoneId,
                    ReturnPolicy = store.ReturnPolicy,
                    VoucherTerms = store.VoucherTerms,
                    InstallmentTerms = store.InstallmentTerms,
                    ContactPhone = store.Phone,
                    ContactEmail = store.ContactEmail,
                    IsActive = store.IsActive,
                    CreatedAt = store.CreatedAt,
                    UpdatedAt = store.UpdatedAt ?? store.CreatedAt,
                };

                return ApiResponse<StoreDto>.OK(result, "更新分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店失败，GUID: {StoreGUID}", guid);
                return ApiResponse<StoreDto>.Error("更新分店失败", "UPDATE_STORE_ERROR");
            }
        }

        /// <summary>
        /// 原子批量修改分店的指定字段；未列入 Fields 的值即使传入也会被忽略。
        /// </summary>
        public async Task<ApiResponse<BatchUpdateStoresResultDto>> BatchUpdateStoresAsync(
            BatchUpdateStoresDto dto
        )
        {
            var requestedCount = dto?.StoreGuids?.Count ?? 0;

            try
            {
                if (dto == null)
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "批量修改请求不能为空",
                        "INVALID_STORE_BATCH_REQUEST"
                    );
                }

                var storeGuids = dto.StoreGuids ?? new List<string>();
                if (
                    storeGuids.Count is < 1 or > 100
                    || storeGuids.Any(string.IsNullOrWhiteSpace)
                    || storeGuids.Distinct(StringComparer.Ordinal).Count()
                        != storeGuids.Count
                )
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "每批必须包含1至100个不重复的分店标识",
                        "INVALID_STORE_BATCH_REQUEST"
                    );
                }

                var fields = dto.Fields ?? new List<string>();
                if (
                    fields.Count is < 1 or > 7
                    || fields.Any(string.IsNullOrWhiteSpace)
                    || fields.Distinct(StringComparer.Ordinal).Count() != fields.Count
                    || fields.Any(field => !AllowedBatchUpdateFields.Contains(field))
                )
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "批量修改字段无效",
                        "INVALID_STORE_BATCH_FIELDS"
                    );
                }

                var selectedFields = fields.ToHashSet(StringComparer.Ordinal);
                var updateTimeZone = selectedFields.Contains(
                    StoreBatchUpdateFieldNames.TimeZoneId
                );
                var updateAbn = selectedFields.Contains(StoreBatchUpdateFieldNames.ABN);
                var updateBrandName = selectedFields.Contains(
                    StoreBatchUpdateFieldNames.BrandName
                );
                var updateIsActive = selectedFields.Contains(
                    StoreBatchUpdateFieldNames.IsActive
                );
                var updateReturnPolicy = selectedFields.Contains(
                    StoreBatchUpdateFieldNames.ReturnPolicy
                );
                var updateVoucherTerms = selectedFields.Contains(
                    StoreBatchUpdateFieldNames.VoucherTerms
                );
                var updateInstallmentTerms = selectedFields.Contains(
                    StoreBatchUpdateFieldNames.InstallmentTerms
                );

                string? normalizedTimeZoneId = null;
                if (
                    updateTimeZone
                    && (
                        !StoreTimeZonePolicy.TryNormalize(
                            dto.TimeZoneId,
                            out normalizedTimeZoneId
                        )
                        || normalizedTimeZoneId == null
                    )
                )
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "门店时区必须为 Australia/Brisbane、Australia/Sydney 或 Australia/Melbourne",
                        "INVALID_STORE_TIME_ZONE"
                    );
                }

                var normalizedAbn = NormalizeNullableBatchText(dto.ABN);
                if (updateAbn && normalizedAbn?.Length > 20)
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "ABN长度不能超过20个字符",
                        "INVALID_STORE_BATCH_VALUE"
                    );
                }

                var normalizedBrandName = NormalizeNullableBatchText(dto.BrandName);
                if (updateBrandName && normalizedBrandName?.Length > 100)
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "品牌名称长度不能超过100个字符",
                        "INVALID_STORE_BATCH_VALUE"
                    );
                }

                var normalizedReturnPolicy = NormalizeNullableBatchText(dto.ReturnPolicy);
                if (updateReturnPolicy && normalizedReturnPolicy?.Length > 500)
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "退换货政策长度不能超过500个字符",
                        "INVALID_STORE_BATCH_VALUE"
                    );
                }

                // 代金券使用说明 / 分期条款：与退换货政策同口径（trim、空白变 null），上限各 600。
                var normalizedVoucherTerms = NormalizeNullableBatchText(dto.VoucherTerms);
                if (updateVoucherTerms && normalizedVoucherTerms?.Length > 600)
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "代金券使用说明长度不能超过600个字符",
                        "INVALID_STORE_BATCH_VALUE"
                    );
                }

                var normalizedInstallmentTerms = NormalizeNullableBatchText(dto.InstallmentTerms);
                if (updateInstallmentTerms && normalizedInstallmentTerms?.Length > 600)
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "分期条款长度不能超过600个字符",
                        "INVALID_STORE_BATCH_VALUE"
                    );
                }

                if (updateIsActive && !dto.IsActive.HasValue)
                {
                    return CreateBatchUpdateError(
                        requestedCount,
                        "是否启用收银必须提供明确的布尔值",
                        "INVALID_STORE_BATCH_VALUE"
                    );
                }

                var db = _context.Db;
                await db.Ado.BeginTranAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    var existingStoreGuids = await db.Queryable<Store>()
                        .Where(store =>
                            storeGuids.Contains(store.StoreGUID) && store.IsDeleted == false
                        )
                        .Select(store => store.StoreGUID)
                        .ToListAsync();
                    var existingStoreGuidSet = existingStoreGuids.ToHashSet(
                        StringComparer.Ordinal
                    );
                    var invalidStoreGuids = storeGuids
                        .Where(storeGuid => !existingStoreGuidSet.Contains(storeGuid))
                        .ToList();

                    if (invalidStoreGuids.Count > 0)
                    {
                        await db.Ado.RollbackTranAsync();
                        return CreateBatchUpdateError(
                            requestedCount,
                            "部分分店不存在或已删除",
                            "STORE_BATCH_TARGET_INVALID",
                            new { invalidStoreGuids }
                        );
                    }

                    var updatedAt = DateTime.UtcNow;
                    var updater = db.Updateable<Store>()
                        .SetColumnsIF(
                            updateTimeZone,
                            store => store.TimeZoneId == normalizedTimeZoneId
                        )
                        .SetColumnsIF(updateAbn, store => store.ABN == normalizedAbn)
                        .SetColumnsIF(
                            updateBrandName,
                            store => store.BrandName == normalizedBrandName
                        )
                        .SetColumnsIF(
                            updateIsActive,
                            store => store.IsActive == dto.IsActive!.Value
                        )
                        .SetColumnsIF(
                            updateReturnPolicy,
                            store => store.ReturnPolicy == normalizedReturnPolicy
                        )
                        .SetColumnsIF(
                            updateVoucherTerms,
                            store => store.VoucherTerms == normalizedVoucherTerms
                        )
                        .SetColumnsIF(
                            updateInstallmentTerms,
                            store => store.InstallmentTerms == normalizedInstallmentTerms
                        )
                        .SetColumns(store => store.UpdatedAt == updatedAt)
                        .Where(store =>
                            storeGuids.Contains(store.StoreGUID) && store.IsDeleted == false
                        );

                    var affectedRows = await updater.ExecuteCommandAsync();
                    if (affectedRows != requestedCount)
                    {
                        await db.Ado.RollbackTranAsync();
                        return CreateBatchUpdateError(
                            requestedCount,
                            "批量修改期间分店数据发生变化，请刷新后重试",
                            "STORE_BATCH_CONFLICT",
                            new { requestedCount, affectedRows }
                        );
                    }

                    await db.Ado.CommitTranAsync();
                    return ApiResponse<BatchUpdateStoresResultDto>.OK(
                        new BatchUpdateStoresResultDto
                        {
                            RequestedCount = requestedCount,
                            UpdatedCount = affectedRows,
                            UpdatedStoreGuids = new List<string>(storeGuids),
                        },
                        $"成功修改{affectedRows}家分店"
                    );
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量修改分店失败，目标数量: {RequestedCount}", requestedCount);
                return CreateBatchUpdateError(
                    requestedCount,
                    "批量修改分店失败",
                    "BATCH_UPDATE_STORES_ERROR"
                );
            }
        }

        private static string? NormalizeNullableBatchText(string? value)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static ApiResponse<BatchUpdateStoresResultDto> CreateBatchUpdateError(
            int requestedCount,
            string message,
            string errorCode,
            object? details = null
        )
        {
            var response = ApiResponse<BatchUpdateStoresResultDto>.Error(
                message,
                errorCode,
                details
            );
            response.Data = new BatchUpdateStoresResultDto
            {
                RequestedCount = requestedCount,
                UpdatedCount = 0,
            };
            return response;
        }

        /// <summary>
        /// 根据GUID删除分店
        /// </summary>
        public async Task<ApiResponse<bool>> DeleteStoreByGuidAsync(string guid)
        {
            try
            {
                var db = _context.Db;

                // 检查分店是否存在
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == guid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                // 检查是否有关联用户
                var userCount = await db.Queryable<UserStore>()
                    .Where(us => us.StoreGUID == guid)
                    .CountAsync();

                if (userCount > 0)
                {
                    return ApiResponse<bool>.Error(
                        "无法删除有关联用户的分店",
                        "STORE_HAS_USERS",
                        new { userCount }
                    );
                }

                await db.Deleteable<Store>().Where(s => s.StoreGUID == guid).ExecuteCommandAsync();

                return ApiResponse<bool>.OK(true, "删除分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除分店失败，GUID: {StoreGUID}", guid);
                return ApiResponse<bool>.Error("删除分店失败", "DELETE_STORE_ERROR");
            }
        }

        /// <summary>
        /// 根据GUID更新分店状态
        /// </summary>
        public async Task<ApiResponse<bool>> UpdateStoreStatusByGuidAsync(
            string guid,
            bool isActive
        )
        {
            try
            {
                var db = _context.Db;

                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == guid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                store.IsActive = isActive;
                store.UpdatedAt = DateTime.UtcNow;

                await db.Updateable(store).ExecuteCommandAsync();

                return ApiResponse<bool>.OK(true, $"{(isActive ? "激活" : "停用")}分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店状态失败，GUID: {StoreGUID}", guid);
                return ApiResponse<bool>.Error("更新分店状态失败", "UPDATE_STORE_STATUS_ERROR");
            }
        }

        /// <summary>
        /// 将当前分店资料同步到HQ分店信息表
        /// </summary>
        public async Task<ApiResponse<bool>> SyncStoreToHqAsync(string guid)
        {
            try
            {
                if (_hqContext == null)
                {
                    return ApiResponse<bool>.Error("HQ数据库未配置", "HQ_CONTEXT_NOT_CONFIGURED");
                }

                var store = await _context.Db.Queryable<Store>()
                    .Where(s => s.StoreGUID == guid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                await UpsertHqBranchAsync(store);

                return ApiResponse<bool>.OK(true, "同步HQ分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "同步分店到HQ失败，StoreGUID: {StoreGUID}", guid);
                return ApiResponse<bool>.Error("同步HQ分店失败", "SYNC_STORE_TO_HQ_ERROR");
            }
        }

        private async Task UpsertHqBranchAsync(Store store)
        {
            var branchCode = store.StoreCode?.Trim();
            if (string.IsNullOrWhiteSpace(branchCode))
            {
                throw new InvalidOperationException("分店代码不能为空");
            }

            var syncLock = HqBranchSyncLocks.GetOrAdd(branchCode, _ => new SemaphoreSlim(1, 1));
            await syncLock.WaitAsync();
            try
            {
                var hqDb = _hqContext!.Db;

                if (hqDb.CurrentConnectionConfig.DbType == DbType.SqlServer)
                {
                    await MergeSqlServerHqBranchAsync(hqDb, branchCode, store);
                    return;
                }

                var affectedRows = await UpdateHqBranchAsync(hqDb, branchCode, store);
                if (affectedRows > 0)
                {
                    return;
                }

                try
                {
                    // HQ分店表没有本地StoreGUID，按分店代码做唯一业务键创建。
                    await hqDb.Insertable(MapHqBranch(branchCode, store)).ExecuteCommandAsync();
                }
                catch (Exception ex) when (IsDuplicateBranchInsertException(ex))
                {
                    // 并发实例同时插入时，唯一键命中后回到更新路径保持幂等。
                    await UpdateHqBranchAsync(hqDb, branchCode, store);
                }
            }
            finally
            {
                syncLock.Release();
            }
        }

        private static HqBranch MapHqBranch(string branchCode, Store store)
        {
            return new HqBranch
            {
                BranchCode = branchCode,
                BranchName = store.StoreName,
                BusinessNumber = store.ABN,
                Phone = store.Phone,
                Address = store.Address,
            };
        }

        private static Task<int> UpdateHqBranchAsync(
            ISqlSugarClient hqDb,
            string branchCode,
            Store store
        )
        {
            // 只覆盖分店基础资料，避免误动HQ表中未纳入本地分店管理的字段。
            return hqDb.Updateable<HqBranch>()
                .SetColumns(branch => new HqBranch
                {
                    BranchName = store.StoreName,
                    BusinessNumber = store.ABN,
                    Phone = store.Phone,
                    Address = store.Address,
                })
                .Where(branch => branch.BranchCode == branchCode)
                .ExecuteCommandAsync();
        }

        private static Task<int> MergeSqlServerHqBranchAsync(
            ISqlSugarClient hqDb,
            string branchCode,
            Store store
        )
        {
            const string sql = @"
MERGE [DIC_分店信息表] WITH (HOLDLOCK) AS target
USING (
    SELECT
        @BranchCode AS [H分店代码],
        @BranchName AS [H分店名称],
        @BusinessNumber AS [H商业编号],
        @Phone AS [H电话],
        @Address AS [H分店地址]
) AS source
ON target.[H分店代码] = source.[H分店代码]
WHEN MATCHED THEN
    UPDATE SET
        [H分店名称] = source.[H分店名称],
        [H商业编号] = source.[H商业编号],
        [H电话] = source.[H电话],
        [H分店地址] = source.[H分店地址]
WHEN NOT MATCHED THEN
    INSERT ([H分店代码], [H分店名称], [H商业编号], [H电话], [H分店地址])
    VALUES (source.[H分店代码], source.[H分店名称], source.[H商业编号], source.[H电话], source.[H分店地址]);";

            // SQL Server用HOLDLOCK保证同一分店代码的插入/更新为原子upsert。
            return hqDb.Ado.ExecuteCommandAsync(
                sql,
                ToParameter("@BranchCode", branchCode),
                ToParameter("@BranchName", store.StoreName),
                ToParameter("@BusinessNumber", store.ABN),
                ToParameter("@Phone", store.Phone),
                ToParameter("@Address", store.Address)
            );
        }

        private static SugarParameter ToParameter(string name, string? value)
        {
            return new SugarParameter(name, value ?? (object)DBNull.Value);
        }

        private static bool IsDuplicateBranchInsertException(Exception ex)
        {
            return ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("重复", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 获取分店用户列表
        /// </summary>
        public async Task<ApiResponse<PagedResult<StoreUserDto>>> GetStoreUsersAsync(
            string storeGuid,
            UserQueryDto query
        )
        {
            try
            {
                var db = _context.Db;

                // 检查分店是否存在
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == storeGuid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<PagedResult<StoreUserDto>>.Error(
                        "分店不存在",
                        "STORE_NOT_FOUND"
                    );
                }

                var userQuery = db.Queryable<UserStore>()
                    .InnerJoin<User>((us, u) => us.UserGUID == u.UserGUID)
                    .Where((us, u) => us.StoreGUID == storeGuid);

                // 搜索条件
                if (!string.IsNullOrEmpty(query.Search))
                {
                    userQuery = userQuery.Where(
                        (us, u) =>
                            u.Username.Contains(query.Search) || u.Email.Contains(query.Search)
                    );
                }

                // 获取总数
                var total = await userQuery.CountAsync();

                // 分页查询
                var users = await userQuery
                    .OrderByDescending((us, u) => us.AssignedAt)
                    .Skip((query.Page - 1) * query.PageSize)
                    .Take(query.PageSize)
                    .Select(us => new StoreUserDto
                    {
                        UserGUID = us.User.UserGUID,
                        Username = us.User.Username,
                        Email = us.User.Email,
                        Phone = string.Empty, // User模型中没有Phone字段
                        IsPrimary = us.IsPrimary,
                        AssignedAt = us.AssignedAt,
                    })
                    .ToListAsync();

                var result = new PagedResult<StoreUserDto>
                {
                    Items = users,
                    Total = total,
                    Page = query.Page,
                    PageSize = query.PageSize,
                };

                return ApiResponse<PagedResult<StoreUserDto>>.OK(result, "获取分店用户列表成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分店用户列表失败，StoreGUID: {StoreGUID}", storeGuid);
                return ApiResponse<PagedResult<StoreUserDto>>.Error(
                    "获取分店用户列表失败",
                    "GET_STORE_USERS_ERROR"
                );
            }
        }

        /// <summary>
        /// 为分店添加用户
        /// 建立用户与分店的关联关系，支持用户分配到指定分店
        /// 包含重复关联检查，防止重复分配
        /// </summary>
        /// <param name="storeGuid">分店GUID</param>
        /// <param name="dto">添加用户DTO，包含用户GUID</param>
        /// <returns>操作结果</returns>
        public async Task<ApiResponse<bool>> AddUserToStoreAsync(
            string storeGuid,
            AddUserToStoreDto dto
        )
        {
            try
            {
                var db = _context.Db;

                // 1. 验证分店是否存在
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == storeGuid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                // 2. 验证用户是否存在
                var user = await db.Queryable<User>()
                    .Where(u => u.UserGUID == dto.UserGUID)
                    .FirstAsync();

                if (user == null)
                {
                    return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var mutationDecision =
                    await UserAccessMutationSecurity.ValidateStoreTargetMutationAsync(
                        db,
                        _manageableStoreScopeService,
                        dto.UserGUID,
                        storeGuid,
                        dto.IsPrimary
                    );
                if (!mutationDecision.IsAllowed)
                {
                    return ApiResponse<bool>.Error(
                        mutationDecision.Message,
                        mutationDecision.ErrorCode
                    );
                }

                // 3. 检查用户是否已经关联到该分店，防止重复分配
                var existingUserStore = await db.Queryable<UserStore>()
                    .Where(us => us.StoreGUID == storeGuid && us.UserGUID == dto.UserGUID)
                    .FirstAsync();

                if (existingUserStore != null)
                {
                    return ApiResponse<bool>.Error("用户已关联到该分店", "USER_ALREADY_ASSIGNED");
                }

                // 4. 创建用户分店关联记录
                var userStore = new UserStore
                {
                    UserGUID = dto.UserGUID, // 用户GUID
                    StoreGUID = storeGuid, // 分店GUID
                    UserStoreGUID = Guid.NewGuid().ToString(), // 关联记录GUID
                    IsPrimary = dto.IsPrimary,
                    AssignedAt = DateTime.UtcNow, // 分配时间
                };

                // 5. 管理关系与派生店长角色必须在同一事务落库。
                await db.Ado.BeginTranAsync();
                try
                {
                    await db.Insertable(userStore).ExecuteCommandAsync();
                    if (dto.IsPrimary)
                    {
                        await UserStoreManagerRoleSynchronizer.SynchronizeAsync(
                            db,
                            dto.UserGUID
                        );
                    }
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<bool>.OK(true, "添加用户到分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "添加用户到分店失败，StoreGUID: {StoreGUID}, UserGUID: {UserGUID}",
                    storeGuid,
                    dto.UserGUID
                );
                return ApiResponse<bool>.Error("添加用户到分店失败", "ADD_USER_TO_STORE_ERROR");
            }
        }

        /// <summary>
        /// 从分店移除用户
        /// </summary>
        public async Task<ApiResponse<bool>> RemoveUserFromStoreAsync(
            string storeGuid,
            string userGuid
        )
        {
            try
            {
                var db = _context.Db;

                // 检查分店是否存在
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == storeGuid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                var userExists = await db.Queryable<User>()
                    .AnyAsync(user => user.UserGUID == userGuid && !user.IsDeleted);
                if (!userExists)
                {
                    return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                }

                var mutationDecision =
                    await UserAccessMutationSecurity.ValidateStoreTargetMutationAsync(
                        db,
                        _manageableStoreScopeService,
                        userGuid,
                        storeGuid,
                        false
                    );
                if (!mutationDecision.IsAllowed)
                {
                    return ApiResponse<bool>.Error(
                        mutationDecision.Message,
                        mutationDecision.ErrorCode
                    );
                }

                // 检查用户关联是否存在
                var userStore = await db.Queryable<UserStore>()
                    .Where(us => us.StoreGUID == storeGuid && us.UserGUID == userGuid)
                    .FirstAsync();

                if (userStore == null)
                {
                    return ApiResponse<bool>.Error("用户未关联到该分店", "USER_NOT_ASSIGNED");
                }

                // 检查是否为最后一个用户
                var userCount = await db.Queryable<UserStore>()
                    .Where(us => us.StoreGUID == storeGuid)
                    .CountAsync();

                if (userCount <= 1)
                {
                    return ApiResponse<bool>.Error(
                        "无法移除分店的最后一个用户",
                        "CANNOT_REMOVE_LAST_USER"
                    );
                }

                await db.Ado.BeginTranAsync();
                try
                {
                    await db.Deleteable<UserStore>()
                        .Where(us => us.StoreGUID == storeGuid && us.UserGUID == userGuid)
                        .ExecuteCommandAsync();
                    await UserStoreManagerRoleSynchronizer.SynchronizeAsync(db, userGuid);
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<bool>.OK(true, "从分店移除用户成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "从分店移除用户失败，StoreGUID: {StoreGUID}, UserGUID: {UserGUID}",
                    storeGuid,
                    userGuid
                );
                return ApiResponse<bool>.Error(
                    "从分店移除用户失败",
                    "REMOVE_USER_FROM_STORE_ERROR"
                );
            }
        }

        /// <summary>
        /// 设置用户是否可管理该分店
        /// </summary>
        public async Task<ApiResponse<bool>> SetPrimaryUserAsync(
            string storeGuid,
            string userGuid,
            bool isPrimary
        )
        {
            try
            {
                var db = _context.Db;

                // 检查分店是否存在
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == storeGuid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                var userExists = await db.Queryable<User>()
                    .AnyAsync(user => user.UserGUID == userGuid && !user.IsDeleted);
                if (!userExists)
                {
                    return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                }

                // 检查用户关联是否存在
                var userStore = await db.Queryable<UserStore>()
                    .Where(us => us.StoreGUID == storeGuid && us.UserGUID == userGuid)
                    .FirstAsync();

                if (userStore == null)
                {
                    return ApiResponse<bool>.Error("用户未关联到该分店", "USER_NOT_ASSIGNED");
                }

                var mutationDecision =
                    await UserAccessMutationSecurity.ValidateStoreTargetMutationAsync(
                        db,
                        _manageableStoreScopeService,
                        userGuid,
                        storeGuid,
                        isPrimary
                    );
                if (!mutationDecision.IsAllowed)
                {
                    return ApiResponse<bool>.Error(
                        mutationDecision.Message,
                        mutationDecision.ErrorCode
                    );
                }

                userStore.IsPrimary = isPrimary;
                userStore.UpdatedAt = DateTime.UtcNow;

                // 设置或取消管理关系时，同步维护派生店长角色，避免两个入口产生不一致数据。
                await db.Ado.BeginTranAsync();
                try
                {
                    await db.Updateable(userStore)
                        .UpdateColumns(us => new { us.IsPrimary, us.UpdatedAt })
                        .ExecuteCommandAsync();
                    await UserStoreManagerRoleSynchronizer.SynchronizeAsync(db, userGuid);
                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<bool>.OK(true, $"{(isPrimary ? "设置" : "取消")}分店管理关系成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "设置分店管理关系失败，StoreGUID: {StoreGUID}, UserGUID: {UserGUID}",
                    storeGuid,
                    userGuid
                );
                return ApiResponse<bool>.Error("设置分店管理关系失败", "SET_PRIMARY_USER_ERROR");
            }
        }

        /// <summary>
        /// 批量管理用户
        /// 支持批量添加或移除用户与分店的关联关系
        /// 提高批量用户操作的效率
        /// </summary>
        /// <param name="storeGuid">分店GUID</param>
        /// <param name="dto">批量操作DTO，包含操作类型和用户GUID列表</param>
        /// <returns>操作结果</returns>
        public async Task<ApiResponse<bool>> BatchManageUsersAsync(
            string storeGuid,
            BatchUserOperationDto dto
        )
        {
            try
            {
                var db = _context.Db;

                // 检查分店是否存在
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == storeGuid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                var action = (dto.Action ?? string.Empty).Trim().ToLowerInvariant();
                if (action is not ("add" or "remove"))
                {
                    return ApiResponse<bool>.Error("不支持的批量操作", "INVALID_BATCH_ACTION");
                }

                var targetUserGuids = dto.UserGUIDs
                    .Where(userGuid => !string.IsNullOrWhiteSpace(userGuid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (targetUserGuids.Count == 0)
                {
                    return ApiResponse<bool>.Error("用户列表不能为空", "USER_LIST_REQUIRED");
                }

                var existingUserGuids = await db.Queryable<User>()
                    .Where(user =>
                        targetUserGuids.Contains(user.UserGUID) && !user.IsDeleted
                    )
                    .Select(user => user.UserGUID)
                    .ToListAsync();
                if (existingUserGuids.Count != targetUserGuids.Count)
                {
                    return ApiResponse<bool>.Error("用户不存在", "USER_NOT_FOUND");
                }

                foreach (var userGuid in targetUserGuids)
                {
                    var mutationDecision =
                        await UserAccessMutationSecurity.ValidateStoreTargetMutationAsync(
                            db,
                            _manageableStoreScopeService,
                            userGuid,
                            storeGuid,
                            false
                        );
                    if (!mutationDecision.IsAllowed)
                    {
                        return ApiResponse<bool>.Error(
                            mutationDecision.Message,
                            mutationDecision.ErrorCode
                        );
                    }
                }

                var existingRelations = await db.Queryable<UserStore>()
                    .Where(userStore =>
                        userStore.StoreGUID == storeGuid
                        && targetUserGuids.Contains(userStore.UserGUID)
                        && !userStore.IsDeleted
                    )
                    .ToListAsync();
                var existingRelationUserGuids = existingRelations
                    .Select(item => item.UserGUID)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // 批量变更使用单一事务；移除管理关系时在提交前同步派生角色。
                await db.Ado.BeginTranAsync();
                try
                {
                    if (action == "add")
                    {
                        var newRelations = targetUserGuids
                            .Where(userGuid => !existingRelationUserGuids.Contains(userGuid))
                            .Select(userGuid => new UserStore
                            {
                                UserGUID = userGuid,
                                StoreGUID = storeGuid,
                                UserStoreGUID = Guid.NewGuid().ToString(),
                                IsPrimary = false,
                                AssignedAt = DateTime.UtcNow,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow,
                            })
                            .ToList();
                        if (newRelations.Count > 0)
                        {
                            await db.Insertable(newRelations).ExecuteCommandAsync();
                        }
                    }
                    else if (existingRelationUserGuids.Count > 0)
                    {
                        var relationUserGuids = existingRelationUserGuids.ToArray();
                        await db.Deleteable<UserStore>()
                            .Where(userStore =>
                                userStore.StoreGUID == storeGuid
                                && relationUserGuids.Contains(userStore.UserGUID)
                            )
                            .ExecuteCommandAsync();

                        foreach (var userGuid in relationUserGuids)
                        {
                            await UserStoreManagerRoleSynchronizer.SynchronizeAsync(db, userGuid);
                        }
                    }

                    await db.Ado.CommitTranAsync();
                }
                catch
                {
                    await db.Ado.RollbackTranAsync();
                    throw;
                }

                return ApiResponse<bool>.OK(true, $"批量{dto.Action}用户成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "批量管理用户失败，StoreGUID: {StoreGUID}, Action: {Action}",
                    storeGuid,
                    dto.Action
                );
                return ApiResponse<bool>.Error("批量管理用户失败", "BATCH_MANAGE_USERS_ERROR");
            }
        }

        /// <summary>
        /// 获取分店详情的私有方法
        /// </summary>
        private async Task<ApiResponse<StoreDetailDto>> GetStoreDetailAsync(Store store)
        {
            try
            {
                var db = _context.Db;
                var storeDto = new StoreDto
                {
                    StoreGUID = store.StoreGUID,
                    StoreName = store.StoreName,
                    StoreCode = store.StoreCode,
                    ABN = store.ABN,
                    BrandName = store.BrandName,
                    Address = store.Address,
                    TimeZoneId = store.TimeZoneId,
                    ReturnPolicy = store.ReturnPolicy,
                    VoucherTerms = store.VoucherTerms,
                    InstallmentTerms = store.InstallmentTerms,
                    ContactEmail = store.ContactEmail,
                    IsActive = store.IsActive,
                    CreatedAt = store.CreatedAt,
                    UpdatedAt = store.UpdatedAt ?? DateTime.UtcNow,
                };

                // 获取关联用户
                var users = await db.Queryable<UserStore>()
                    .InnerJoin<User>((us, u) => us.UserGUID == u.UserGUID)
                    .Where((us, u) => us.StoreGUID == store.StoreGUID)
                    .Select(
                        (us, u) =>
                            new StoreUserDto
                            {
                                UserGUID = u.UserGUID,
                                Username = u.Username,
                                Email = u.Email,
                                Phone = string.Empty,
                                IsPrimary = us.IsPrimary,
                                AssignedAt = us.AssignedAt,
                            }
                    )
                    .ToListAsync();

                var result = new StoreDetailDto
                {
                    StoreGUID = store.StoreGUID,
                    StoreName = store.StoreName,
                    StoreCode = store.StoreCode,
                    ABN = store.ABN,
                    BrandName = store.BrandName,
                    Description = string.Empty, // Store实体没有Description属性，使用空字符串
                    Address = store.Address,
                    TimeZoneId = store.TimeZoneId,
                    ReturnPolicy = store.ReturnPolicy,
                    VoucherTerms = store.VoucherTerms,
                    InstallmentTerms = store.InstallmentTerms,
                    ContactPhone = store.Phone,
                    ContactEmail = store.ContactEmail,
                    // 分店详情必须保留数据库状态，避免 DTO 默认值覆盖停用分店。
                    IsActive = store.IsActive,
                    Users = users,
                    TotalUsers = users.Count,
                    ActiveUsers = users.Count(u => true), // 这里可以根据用户状态进一步筛选
                };

                return ApiResponse<StoreDetailDto>.OK(result, "获取分店详情成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取分店详情失败");
                return ApiResponse<StoreDetailDto>.Error(
                    "获取分店详情失败",
                    "GET_STORE_DETAIL_ERROR"
                );
            }
        }

        /// <summary>
        /// 创建分店
        /// </summary>
        public async Task<ApiResponse<StoreDto>> CreateStoreAsync(CreateStoreDto dto)
        {
            try
            {
                var db = _context.Db;
                var normalizedStoreCode = dto.StoreCode?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedStoreCode))
                {
                    return ApiResponse<StoreDto>.Error(
                        "分店代码不能为空",
                        "STORE_CODE_REQUIRED"
                    );
                }

                if (!StoreTimeZonePolicy.TryNormalize(dto.TimeZoneId, out var normalizedTimeZoneId))
                {
                    return ApiResponse<StoreDto>.Error(
                        "门店时区仅支持 Australia/Brisbane、Australia/Sydney 或 Australia/Melbourne",
                        "INVALID_STORE_TIME_ZONE"
                    );
                }

                // 检查分店代码是否重复
                var existingStore = await db.Queryable<Store>()
                    .Where(s => s.StoreCode == normalizedStoreCode)
                    .FirstAsync();

                if (existingStore != null)
                {
                    return ApiResponse<StoreDto>.Error(
                        "分店代码已存在",
                        "DUPLICATE_STORE_CODE",
                        new { storeCode = normalizedStoreCode }
                    );
                }

                var store = new Store
                {
                    StoreName = dto.StoreName,
                    StoreCode = normalizedStoreCode,
                    Address = dto.Address,
                    TimeZoneId = normalizedTimeZoneId,
                    ReturnPolicy = dto.ReturnPolicy,
                    VoucherTerms = dto.VoucherTerms,
                    InstallmentTerms = dto.InstallmentTerms,
                    ABN = dto.ABN,
                    BrandName = dto.BrandName,
                    StoreGUID = Guid.NewGuid().ToString(),
                    // 新建分店默认不启用收银系统；只有表单显式开启时才写入 true。
                    IsActive = dto.IsActive,
                    Phone = dto.ContactPhone,
                    ContactEmail = dto.ContactEmail,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                };

                var storeId = await db.Insertable(store).ExecuteReturnIdentityAsync();

                var result = new StoreDto
                {
                    StoreGUID = store.StoreGUID,
                    StoreName = store.StoreName,
                    StoreCode = store.StoreCode,
                    ABN = store.ABN,
                    BrandName = store.BrandName,
                    Description = string.Empty,
                    ContactPhone = string.Empty,
                    Address = store.Address,
                    TimeZoneId = store.TimeZoneId,
                    ReturnPolicy = store.ReturnPolicy,
                    VoucherTerms = store.VoucherTerms,
                    InstallmentTerms = store.InstallmentTerms,
                    ContactEmail = store.ContactEmail,
                    IsActive = store.IsActive,
                    CreatedAt = store.CreatedAt,
                    UpdatedAt = store.UpdatedAt ?? DateTime.UtcNow,
                };

                return ApiResponse<StoreDto>.OK(result, "创建分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建分店失败");
                return ApiResponse<StoreDto>.Error("创建分店失败", "CREATE_STORE_ERROR");
            }
        }

        /// <summary>
        /// 更新分店
        /// </summary>
        public async Task<ApiResponse<StoreDto>> UpdateStoreAsync(string guid, UpdateStoreDto dto)
        {
            try
            {
                var db = _context.Db;
                if (!StoreTimeZonePolicy.TryNormalize(dto.TimeZoneId, out var normalizedTimeZoneId))
                {
                    return ApiResponse<StoreDto>.Error(
                        "门店时区仅支持 Australia/Brisbane、Australia/Sydney 或 Australia/Melbourne",
                        "INVALID_STORE_TIME_ZONE"
                    );
                }

                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == guid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<StoreDto>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                // 检查分店代码是否重复（排除当前分店）
                var existingStore = await db.Queryable<Store>()
                    .Where(s => s.StoreCode == dto.StoreCode && s.StoreGUID != guid)
                    .FirstAsync();

                if (existingStore != null)
                {
                    return ApiResponse<StoreDto>.Error(
                        "分店代码已存在",
                        "DUPLICATE_STORE_CODE",
                        new { storeCode = dto.StoreCode }
                    );
                }

                // 更新分店信息
                store.StoreName = dto.StoreName;
                store.StoreCode = dto.StoreCode;
                store.ABN = dto.ABN;
                store.BrandName = dto.BrandName;
                store.Address = dto.Address;
                store.ReturnPolicy = dto.ReturnPolicy;
                store.VoucherTerms = dto.VoucherTerms;
                store.InstallmentTerms = dto.InstallmentTerms;
                if (!string.IsNullOrWhiteSpace(dto.TimeZoneId))
                {
                    store.TimeZoneId = normalizedTimeZoneId;
                }
                store.Phone = dto.ContactPhone;
                store.ContactEmail = dto.ContactEmail;
                store.IsActive = dto.IsActive;
                store.UpdatedAt = DateTime.UtcNow;

                await db.Updateable(store).ExecuteCommandAsync();

                var result = new StoreDto
                {
                    StoreGUID = store.StoreGUID,
                    StoreName = store.StoreName,
                    StoreCode = store.StoreCode,
                    ABN = store.ABN,
                    BrandName = store.BrandName,
                    Description = string.Empty,
                    Address = store.Address,
                    TimeZoneId = store.TimeZoneId,
                    ReturnPolicy = store.ReturnPolicy,
                    VoucherTerms = store.VoucherTerms,
                    InstallmentTerms = store.InstallmentTerms,
                    ContactPhone = store.Phone,
                    ContactEmail = store.ContactEmail,
                    IsActive = store.IsActive,
                    CreatedAt = store.CreatedAt,
                    UpdatedAt = store.UpdatedAt ?? DateTime.UtcNow,
                };

                return ApiResponse<StoreDto>.OK(result, "更新分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店失败，ID: {StoreGUID}", guid);
                return ApiResponse<StoreDto>.Error("更新分店失败", "UPDATE_STORE_ERROR");
            }
        }

        /// <summary>
        /// 删除分店
        /// </summary>
        public async Task<ApiResponse<bool>> DeleteStoreAsync(string guid)
        {
            try
            {
                var db = _context.Db;

                // 检查分店是否存在
                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == guid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                // 检查是否有关联用户
                var userCount = await db.Queryable<UserStore>()
                    .Where(us => us.StoreGUID == guid)
                    .CountAsync();

                if (userCount > 0)
                {
                    return ApiResponse<bool>.Error(
                        "无法删除有关联用户的分店",
                        "STORE_HAS_USERS",
                        new { userCount }
                    );
                }

                await db.Deleteable<Store>().Where(s => s.StoreGUID == guid).ExecuteCommandAsync();

                return ApiResponse<bool>.OK(true, "删除分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除分店失败，ID: {StoreGUID}", guid);
                return ApiResponse<bool>.Error("删除分店失败", "DELETE_STORE_ERROR");
            }
        }

        /// <summary>
        /// 更新分店状态
        /// </summary>
        public async Task<ApiResponse<bool>> UpdateStoreStatusAsync(string guid, bool isActive)
        {
            try
            {
                var db = _context.Db;

                var store = await db.Queryable<Store>()
                    .Where(s => s.StoreGUID == guid)
                    .FirstAsync();

                if (store == null)
                {
                    return ApiResponse<bool>.Error("分店不存在", "STORE_NOT_FOUND");
                }

                store.IsActive = isActive;
                store.UpdatedAt = DateTime.UtcNow;

                await db.Updateable(store).ExecuteCommandAsync();

                return ApiResponse<bool>.OK(true, $"{(isActive ? "激活" : "停用")}分店成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新分店状态失败，ID: {StoreGUID}", guid);
                return ApiResponse<bool>.Error("更新分店状态失败", "UPDATE_STORE_STATUS_ERROR");
            }
        }

        /// <summary>
        /// 应用排序规则到查询
        /// </summary>
        /// <param name="query">分店查询</param>
        /// <param name="sortField">排序字段</param>
        /// <param name="sortOrder">排序方向</param>
        /// <returns>应用排序后的查询</returns>
        private ISugarQueryable<Store> ApplySorting(
            ISugarQueryable<Store> query,
            string? sortField,
            string? sortOrder
        )
        {
            var isDescending = IsDescendingSort(sortOrder);

            return sortField?.ToLower() switch
            {
                "storename" => isDescending
                    ? query.OrderByDescending(s => s.StoreName)
                    : query.OrderBy(s => s.StoreName),
                "storecode" => isDescending
                    ? query.OrderByDescending(s => s.StoreCode)
                    : query.OrderBy(s => s.StoreCode),
                "brandname" => isDescending
                    ? query.OrderByDescending(s => s.BrandName)
                    : query.OrderBy(s => s.BrandName),
                "contactphone" => isDescending
                    ? query.OrderByDescending(s => s.Phone)
                    : query.OrderBy(s => s.Phone),
                "createdat" => isDescending
                    ? query.OrderByDescending(s => s.CreatedAt)
                    : query.OrderBy(s => s.CreatedAt),
                "isactive" => isDescending
                    ? query.OrderByDescending(s => s.IsActive)
                    : query.OrderBy(s => s.IsActive),
                "address" => isDescending
                    ? query.OrderByDescending(s => s.Address)
                    : query.OrderBy(s => s.Address),
                _ => query.OrderBy(s => s.StoreName), // 默认按名称升序
            };
        }
    }
}
