using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    /// <summary>
    /// 移动端「价格更新」通知与「同步其它分店」接口。
    /// 放在商品维护控制器的分部类里，是为了复用其"登录账号 + 绑定设备"双轨鉴权与分店范围解析，
    /// 避免再复制一份鉴权逻辑。
    /// </summary>
    public partial class ReactStoreProductMaintenanceController
    {
        [HttpGet("price-update-tasks")]
        public async Task<IActionResult> GetPriceUpdateTasks([FromQuery] StorePriceUpdateTaskQueryDto query)
        {
            var guard = await GuardPriceTaskAccessAsync(query.StoreCode);
            if (guard.Failure != null) return guard.Failure;

            var page = await _priceTaskService!.GetPageAsync(query, guard.Access!.StoreCodes);
            return Ok(ApiResponse<StorePriceUpdateTaskPageDto>.OK(page, "查询成功"));
        }

        [HttpGet("price-update-tasks/count")]
        public async Task<IActionResult> GetPriceUpdateTaskCount([FromQuery] string? storeCode)
        {
            var guard = await GuardPriceTaskAccessAsync(storeCode);
            if (guard.Failure != null) return guard.Failure;

            var count = await _priceTaskService!.GetPendingCountAsync(storeCode!);
            return Ok(
                ApiResponse<StorePriceUpdateTaskCountDto>.OK(
                    new StorePriceUpdateTaskCountDto { PendingCount = count },
                    "查询成功"
                )
            );
        }

        /// <summary>
        /// 按分店汇总：当前账号 / 设备可访问的每家启用分店的未完成数与超期数（超期从首次发起起算）。
        /// 不需要传分店：设备只会得到绑定的那一家；管理员得到全部启用分店。
        /// 每小时提醒、分店切换条、工作台角标和报表锁定都读这个接口，客户端据此自行判定「管理分店」范围。
        /// </summary>
        [HttpGet("price-update-tasks/summary")]
        public async Task<IActionResult> GetPriceUpdateTaskStoreSummaries()
        {
            var guard = await GuardPriceTaskAccessAsync(null, requireStoreCode: false);
            if (guard.Failure != null) return guard.Failure;

            var summaries = await _priceTaskService!.GetStoreSummariesAsync(guard.Access!.StoreCodes);
            return Ok(ApiResponse<StorePriceUpdateTaskStoreSummaryListDto>.OK(summaries, "查询成功"));
        }

        [HttpPost("price-update-tasks/apply")]
        public async Task<IActionResult> ApplyPriceUpdateTasks([FromBody] ApplyStorePriceUpdateTasksRequestDto request)
        {
            var guard = await GuardPriceTaskAccessAsync(request.StoreCode);
            if (guard.Failure != null) return guard.Failure;

            var result = await _priceTaskService!.ApplyAsync(
                request,
                guard.Access!.ActorLabel,
                guard.Access.ActorLabel,
                guard.Access.StoreCodes
            );
            return Ok(ApiResponse<StorePriceUpdateTaskBatchResultDto>.OK(result, "处理完成"));
        }

        [HttpPost("price-update-tasks/keep")]
        public async Task<IActionResult> KeepStorePrice([FromBody] StorePriceUpdateTaskIdsRequestDto request)
        {
            var guard = await GuardPriceTaskAccessAsync(request.StoreCode);
            if (guard.Failure != null) return guard.Failure;

            var result = await _priceTaskService!.KeepStorePriceAsync(request, guard.Access!.ActorLabel);
            return Ok(ApiResponse<StorePriceUpdateTaskBatchResultDto>.OK(result, "处理完成"));
        }

        [HttpPost("price-update-tasks/labels")]
        public async Task<IActionResult> MarkPriceUpdateTaskLabels(
            [FromBody] MarkStorePriceUpdateTaskLabelsRequestDto request
        )
        {
            var guard = await GuardPriceTaskAccessAsync(request.StoreCode);
            if (guard.Failure != null) return guard.Failure;

            var result = await _priceTaskService!.MarkLabelsAsync(request, guard.Access!.ActorLabel);
            return Ok(ApiResponse<StorePriceUpdateTaskBatchResultDto>.OK(result, "处理完成"));
        }

        /// <summary>同步面板的数据：可同步到的目标分店及其当前价格。</summary>
        [HttpGet("products/{productCode}/sync-targets")]
        public async Task<IActionResult> GetSyncTargets(string productCode, [FromQuery] string? sourceStoreCode)
        {
            var guard = await GuardSyncToOtherStoresAsync(sourceStoreCode);
            if (guard.Failure != null) return guard.Failure;

            var code = productCode.Trim();
            var source = sourceStoreCode!.Trim();
            var storesQuery = _db.Queryable<Store>().Where(store => store.IsActive && !store.IsDeleted);
            var stores = await storesQuery.Select(store => new { store.StoreCode, store.StoreName }).ToListAsync();
            var allowed = guard.Access!.StoreCodes;
            var targetStores = stores
                .Where(store => !string.Equals(store.StoreCode, source, StringComparison.OrdinalIgnoreCase))
                .Where(store => allowed == null || allowed.Contains(store.StoreCode, StringComparer.OrdinalIgnoreCase))
                .OrderBy(store => store.StoreCode, StringComparer.Ordinal)
                .ToList();

            var storeCodes = targetStores.Select(store => store.StoreCode).Append(source).ToList();
            var rows = await _db.Queryable<StoreRetailPrice>()
                .Where(row =>
                    row.ProductCode == code
                    && row.StoreCode != null
                    && storeCodes.Contains(row.StoreCode)
                    && !row.IsDeleted
                )
                .ToListAsync();
            var sourceRow = rows.FirstOrDefault(row => string.Equals(row.StoreCode, source, StringComparison.OrdinalIgnoreCase));
            if (sourceRow == null)
            {
                return Ok(ApiResponse<StoreProductSyncTargetsDto>.Error("本店没有该商品的价格记录"));
            }

            var dto = new StoreProductSyncTargetsDto
            {
                SourceStoreCode = source,
                ProductCode = code,
                SourceRetailPrice = sourceRow.StoreRetailPriceValue,
                SourceDiscountRate = sourceRow.DiscountRate,
                SourcePurchasePrice = sourceRow.PurchasePrice,
                Targets = targetStores
                    .Select(store =>
                    {
                        var row = rows.FirstOrDefault(item =>
                            string.Equals(item.StoreCode, store.StoreCode, StringComparison.OrdinalIgnoreCase)
                        );
                        return new StoreProductSyncTargetDto
                        {
                            StoreCode = store.StoreCode,
                            StoreName = store.StoreName,
                            HasRecord = row != null,
                            RetailPrice = row?.StoreRetailPriceValue,
                            DiscountRate = row?.DiscountRate,
                            IsSpecialProduct = row?.IsSpecialProduct ?? false,
                        };
                    })
                    .ToList(),
            };
            return Ok(ApiResponse<StoreProductSyncTargetsDto>.OK(dto, "查询成功"));
        }

        [HttpPost("products/sync-to-other-stores")]
        public async Task<IActionResult> SyncToOtherStores([FromBody] MobileSyncStoreProductToOtherStoresRequestDto request)
        {
            var guard = await GuardSyncToOtherStoresAsync(request.SourceStoreCode);
            if (guard.Failure != null) return guard.Failure;
            if (_storePriceService == null)
            {
                return StatusCode(503, ApiResponse<object>.Error("同步服务未启用"));
            }

            var targets = (request.TargetStoreCodes ?? new List<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Where(code => !string.Equals(code, request.SourceStoreCode.Trim(), StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (targets.Count == 0 || string.IsNullOrWhiteSpace(request.ProductCode))
            {
                return BadRequest(ApiResponse<object>.Error("请选择商品和至少一个目标分店"));
            }

            // 目标分店必须全部落在当前账号可管理的范围内，禁止借同步越权改别人的店。
            var allowed = guard.Access!.StoreCodes;
            if (allowed != null && targets.Any(code => !allowed.Contains(code, StringComparer.OrdinalIgnoreCase)))
            {
                return Forbid();
            }

            var result = await _storePriceService.SyncToOtherStoresAsync(
                new SyncToOtherStoresDto
                {
                    ProductCodes = new List<string> { request.ProductCode.Trim() },
                    SourceStoreCode = request.SourceStoreCode.Trim(),
                    TargetStoreCodes = targets,
                    SyncRetailPrice = request.SyncRetailPrice,
                    SyncDiscountRate = request.SyncDiscountRate,
                    SyncPurchasePrice = request.SyncPurchasePrice,
                    Mode = SyncModeConstants.Overwrite,
                },
                guard.Access.ActorLabel
            );
            if (!result.Success)
            {
                return Ok(ApiResponse<MobileSyncStoreProductToOtherStoresResultDto>.Error(result.Message));
            }

            return Ok(
                ApiResponse<MobileSyncStoreProductToOtherStoresResultDto>.OK(
                    new MobileSyncStoreProductToOtherStoresResultDto { UpdatedStoreCount = targets.Count },
                    result.Message
                )
            );
        }

        private sealed record PriceTaskGuard(StoreAccessContext? Access, IActionResult? Failure);

        private async Task<PriceTaskGuard> GuardPriceTaskAccessAsync(string? storeCode, bool requireStoreCode = true)
        {
            if (_priceTaskService == null)
            {
                return new PriceTaskGuard(null, StatusCode(503, ApiResponse<object>.Error("价格更新通知未启用")));
            }

            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return new PriceTaskGuard(null, Unauthorized(ApiResponse<object>.Error(access.Message)));
            }
            if (requireStoreCode)
            {
                if (string.IsNullOrWhiteSpace(storeCode))
                {
                    return new PriceTaskGuard(null, BadRequest(ApiResponse<object>.Error("缺少分店代码")));
                }
                if (
                    access.StoreCodes != null
                    && !access.StoreCodes.Contains(storeCode.Trim(), StringComparer.OrdinalIgnoreCase)
                )
                {
                    return new PriceTaskGuard(null, Forbid());
                }
            }

            if (User?.Identity?.IsAuthenticated == true)
            {
                // 匿名绑定设备沿用既有的设备授权 + 分店范围控制；登录账号再叠加专用权限码。
                // 查看与操作共用 PriceUpdates，不依赖 StoreProducts.Edit。
                var authorized = await _authorizationService.AuthorizeAsync(
                    User,
                    resource: null,
                    Permissions.StoreProducts.PriceUpdates
                );
                if (!authorized.Succeeded)
                {
                    return new PriceTaskGuard(null, Forbid());
                }
            }

            return new PriceTaskGuard(access, null);
        }

        private async Task<PriceTaskGuard> GuardSyncToOtherStoresAsync(string? sourceStoreCode)
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return new PriceTaskGuard(null, Unauthorized(ApiResponse<object>.Error(access.Message)));
            }
            // 设备会话绑定单一分店，没有跨店身份：同步其它分店只对登录账号开放。
            if (User?.Identity?.IsAuthenticated != true)
            {
                return new PriceTaskGuard(null, Forbid());
            }
            if (string.IsNullOrWhiteSpace(sourceStoreCode))
            {
                return new PriceTaskGuard(null, BadRequest(ApiResponse<object>.Error("缺少来源分店代码")));
            }
            if (
                access.StoreCodes != null
                && !access.StoreCodes.Contains(sourceStoreCode.Trim(), StringComparer.OrdinalIgnoreCase)
            )
            {
                return new PriceTaskGuard(null, Forbid());
            }

            var authorized = await _authorizationService.AuthorizeAsync(
                User,
                resource: null,
                Permissions.StoreProducts.SyncToOtherStores
            );
            return authorized.Succeeded
                ? new PriceTaskGuard(access, null)
                : new PriceTaskGuard(null, Forbid());
        }
    }
}
