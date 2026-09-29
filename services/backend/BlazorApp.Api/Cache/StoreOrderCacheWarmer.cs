using BlazorApp.Api.Features.StoreOrders.ProductPicker;
using BlazorApp.Api.Features.StoreOrders.ProductPicker.Domain;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.DTOs;
using Microsoft.Extensions.Caching.Memory;

namespace BlazorApp.Api.Cache
{
    /// <summary>
    /// 订货商品列表缓存预热服务
    /// 负责预热和清除商品列表缓存
    /// </summary>
    public class StoreOrderCacheWarmer : IStoreOrderCacheWarmer
    {
        private readonly IStoreOrderProductPickerSlice _productPicker;
        private readonly ILogger<StoreOrderCacheWarmer> _logger;
        private readonly IMemoryCache _cache;

        // 预热写入的首页键会被真实 /products 请求命中，必须与分页缓存同一时长，否则预热后首页仍按旧时长陈旧。
        private static readonly TimeSpan CACHE_DURATION = ProductPickerRules.HomePageCacheDuration;
        private static readonly TimeSpan HOME_PAGE_WARM_UP_TIMEOUT = TimeSpan.FromSeconds(30);
        private int _isHomePageWarmUpRunning;

        public StoreOrderCacheWarmer(
            IStoreOrderProductPickerSlice productPicker,
            ILogger<StoreOrderCacheWarmer> logger,
            IMemoryCache cache
        )
        {
            _productPicker = productPicker;
            _logger = logger;
            _cache = cache;
        }

        /// <summary>
        /// 预热首页默认数据缓存
        /// </summary>
        public async Task WarmUpHomePageAsync()
        {
            // 同一时刻只允许一个首页预热任务运行，避免后台重复打满同一条查询链路。
            if (Interlocked.CompareExchange(ref _isHomePageWarmUpRunning, 1, 0) != 0)
            {
                _logger.LogWarning("已有首页商品列表缓存预热正在运行，本次请求跳过");
                return;
            }

            _logger.LogInformation("开始预热首页商品列表缓存");
            using var timeoutSource = new CancellationTokenSource(HOME_PAGE_WARM_UP_TIMEOUT);

            try
            {
                await WarmUpProductListAsync(50, timeoutSource.Token);
                timeoutSource.Token.ThrowIfCancellationRequested();
                await WarmUpProductListAsync(18, timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                if (timeoutSource.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "首页商品列表缓存预热已取消，已在 {TimeoutSeconds} 秒超时边界内结束",
                        (int)HOME_PAGE_WARM_UP_TIMEOUT.TotalSeconds
                    );
                }
                else
                {
                    _logger.LogWarning("首页商品列表缓存预热已取消");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "预热首页商品列表缓存失败");
                throw;
            }
            finally
            {
                Volatile.Write(ref _isHomePageWarmUpRunning, 0);
            }
        }

        private async Task WarmUpProductListAsync(int pageSize, CancellationToken cancellationToken)
        {
            var filter = new StoreOrderFilterDto
            {
                PageNumber = 1,
                PageSize = pageSize,
                ItemNumber = null,
                ProductName = null,
                CategoryGUID = null,
                SortBy = "Default"
            };

            var result = await _productPicker.GetHomePageWarmUpPageAsync(
                pageSize,
                cancellationToken
            );
            // 轻量预热结果没有真实 Total，必须写入专用键，避免污染正常分页缓存。
            var warmUpCacheKey = StoreOrderCacheKeys.GetHomePageWarmUpCacheKey(pageSize);
            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(CACHE_DURATION)
                .SetPriority(Microsoft.Extensions.Caching.Memory.CacheItemPriority.High);

            _cache.Set(warmUpCacheKey, result, cacheOptions);

            _logger.LogInformation(
                "首页商品列表轻量预热完成，PageSize={PageSize}，共 {Count} 条商品，缓存键: {CacheKey}",
                pageSize,
                result.Items?.Count ?? 0,
                warmUpCacheKey
            );

            // 商品列表所有分店一致，StoreCode 只用于权限校验；正常首页缓存必须保留准确 Total，供真实 /products 命中。
            var homePageResult = await _productPicker.GetHomePageCachePageAsync(
                pageSize,
                cancellationToken
            );
            // 后台预热没有用户 Claims，固定写入普通范围，避免污染带货位解析能力的缓存。
            var homePageCacheKey = StoreOrderCacheKeys.Products(
                filter,
                locationLookupEnabled: false
            );
            var homePageCacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(CACHE_DURATION)
                .SetPriority(Microsoft.Extensions.Caching.Memory.CacheItemPriority.High);

            _cache.Set(homePageCacheKey, homePageResult, homePageCacheOptions);

            _logger.LogInformation(
                "首页商品列表正常缓存预热完成，PageSize={PageSize}，共 {Count} 条商品，总数 {Total}，缓存键: {CacheKey}",
                pageSize,
                homePageResult.Items?.Count ?? 0,
                homePageResult.Total,
                homePageCacheKey
            );
        }

        /// <summary>
        /// 清除所有商品列表缓存（排除首页缓存）
        /// </summary>
        public Task ClearCacheAsync()
        {
            // 先获取首页缓存键，确保 Web 与 Expo 首页默认列表都保留。
            var homePageCacheKeys = new HashSet<string>(StringComparer.Ordinal)
            {
                StoreOrderCacheKeys.GetHomePageCacheKey(50),
                StoreOrderCacheKeys.GetHomePageCacheKey(18),
                StoreOrderCacheKeys.GetHomePageWarmUpCacheKey(50),
                StoreOrderCacheKeys.GetHomePageWarmUpCacheKey(18),
            };
            var keysToClear = StoreOrderCacheKeys.ActiveKeys.ToList();

            // 排除首页缓存，只清除其他缓存
            var keysToRemove = keysToClear.Where(k => !homePageCacheKeys.Contains(k)).ToList();

            foreach (var key in keysToRemove)
            {
                _cache.Remove(key);
            }

            // 清除活动键列表，但保留首页缓存键
            StoreOrderCacheKeys.ClearActiveKeys();
            // 重新添加首页缓存键到活动键列表。
            foreach (var pageSize in new[] { 50, 18 })
            {
                _ = StoreOrderCacheKeys.GetHomePageCacheKey(pageSize);
                _ = StoreOrderCacheKeys.GetHomePageWarmUpCacheKey(pageSize);
            }

            _logger.LogInformation(
                "已清除 {Count} 个商品列表缓存（首页缓存已保留）",
                keysToRemove.Count
            );

            return Task.CompletedTask;
        }
    }
}
