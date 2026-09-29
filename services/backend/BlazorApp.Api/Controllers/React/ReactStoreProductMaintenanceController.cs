using System.Diagnostics;
using System.Security.Claims;
using AutoMapper;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/store-product-maintenance")]
    [AllowAnonymous]
    public partial class ReactStoreProductMaintenanceController : ControllerBase
    {
        private readonly IStoreProductMaintenanceReactService _service;
        private readonly IDeviceRegistrationService _deviceRegistrationService;
        private readonly IMapper _mapper;
        private readonly ISqlSugarClient _db;
        private readonly ILogger<ReactStoreProductMaintenanceController> _logger;
        private readonly IAuthorizationService _authorizationService;
        private readonly IStorePriceUpdateTaskService? _priceTaskService;
        private readonly IStoreProductPriceReactService? _storePriceService;
        private readonly Features.PromoPosters.IPromoPosterService? _promoPosterService;

        public ReactStoreProductMaintenanceController(
            IStoreProductMaintenanceReactService service,
            IDeviceRegistrationService deviceRegistrationService,
            IMapper mapper,
            SqlSugarContext context,
            ILogger<ReactStoreProductMaintenanceController> logger,
            IAuthorizationService authorizationService,
            // 价格更新通知、促销海报相关依赖为可选：既有测试直接构造本控制器，不应因新增功能被迫改动。
            IStorePriceUpdateTaskService? priceTaskService = null,
            IStoreProductPriceReactService? storePriceService = null,
            Features.PromoPosters.IPromoPosterService? promoPosterService = null
        )
        {
            _service = service;
            _deviceRegistrationService = deviceRegistrationService;
            _mapper = mapper;
            _db = context.Db;
            _logger = logger;
            _authorizationService = authorizationService;
            _priceTaskService = priceTaskService;
            _storePriceService = storePriceService;
            _promoPosterService = promoPosterService;
        }

        [HttpPost("lookup")]
        public async Task<IActionResult> Lookup([FromBody] StoreProductLookupRequestDto request)
        {
            var totalSw = Stopwatch.StartNew();
            var access = await ResolveAccessContextAsync();
            _logger.LogInformation(
                "StoreProductMaintenance lookup access resolved keyword={Keyword} requestedStore={RequestedStore} allowed={IsAllowed} scope={Scope} actor={Actor} access_ms={AccessMs} user_lookup_ms={UserLookupMs} user_store_scope_ms={UserStoreScopeMs} device_auth_ms={DeviceAuthMs} device_load_ms={DeviceLoadMs}",
                request.Keyword,
                request.StoreCode,
                access.IsAllowed,
                FormatStoreScope(access.StoreCodes),
                access.ActorLabel,
                access.AccessResolveMs,
                access.UserLookupMs,
                access.UserStoreScopeMs,
                access.DeviceAuthMs,
                access.DeviceLoadMs
            );
            if (!access.IsAllowed)
            {
                _logger.LogWarning(
                    "StoreProductMaintenance lookup unauthorized keyword={Keyword} message={Message} total_ms={TotalMs}",
                    request.Keyword,
                    access.Message,
                    totalSw.ElapsedMilliseconds
                );
                return Unauthorized(ApiResponse<List<StoreProductLookupItemDto>>.Error(access.Message));
            }

            var result = await _service.LookupAsync(request, access.StoreCodes, HttpContext.RequestAborted);
            _logger.LogInformation(
                "StoreProductMaintenance lookup completed keyword={Keyword} requestedStore={RequestedStore} allowed={IsAllowed} total_ms={TotalMs}",
                request.Keyword,
                request.StoreCode,
                true,
                totalSw.ElapsedMilliseconds
            );
            return Ok(result);
        }

        [HttpPost("scan-label")]
        public async Task<IActionResult> ScanLabel([FromBody] StoreProductLookupRequestDto request)
        {
            var totalSw = Stopwatch.StartNew();
            var access = await ResolveAccessContextAsync(reuseValidatedDeviceScope: true);
            if (!access.IsAllowed)
            {
                _logger.LogWarning(
                    "StoreProductMaintenance scan-label unauthorized message={Message} total_ms={TotalMs}",
                    access.Message,
                    totalSw.ElapsedMilliseconds
                );
                return Unauthorized(ApiResponse<StoreProductScanLabelResultDto>.Error(access.Message));
            }

            var result = await _service.ScanLabelAsync(request, access.StoreCodes, HttpContext.RequestAborted);
            _logger.LogInformation(
                "StoreProductMaintenance scan-label request completed requestedStore={RequestedStore} total_ms={TotalMs}",
                request.StoreCode,
                totalSw.ElapsedMilliseconds
            );
            return Ok(result);
        }

        [HttpGet("{productCode}/fast-detail")]
        public async Task<IActionResult> GetFastDetail(
            string productCode,
            [FromQuery] string? storeCode = null
        )
        {
            var totalSw = Stopwatch.StartNew();
            var access = await ResolveAccessContextAsync();
            _logger.LogInformation(
                "StoreProductMaintenance fast-detail access resolved productCode={ProductCode} requestedStore={RequestedStore} allowed={IsAllowed} scope={Scope} actor={Actor} access_ms={AccessMs} user_lookup_ms={UserLookupMs} user_store_scope_ms={UserStoreScopeMs} device_auth_ms={DeviceAuthMs} device_load_ms={DeviceLoadMs}",
                productCode,
                storeCode,
                access.IsAllowed,
                FormatStoreScope(access.StoreCodes),
                access.ActorLabel,
                access.AccessResolveMs,
                access.UserLookupMs,
                access.UserStoreScopeMs,
                access.DeviceAuthMs,
                access.DeviceLoadMs
            );
            if (!access.IsAllowed)
            {
                _logger.LogWarning(
                    "StoreProductMaintenance fast-detail unauthorized productCode={ProductCode} message={Message} total_ms={TotalMs}",
                    productCode,
                    access.Message,
                    totalSw.ElapsedMilliseconds
                );
                return Unauthorized(ApiResponse<StoreProductDetailDto>.Error(access.Message));
            }

            var result = await _service.GetFastDetailAsync(productCode, storeCode, access.StoreCodes, HttpContext.RequestAborted);
            _logger.LogInformation(
                "StoreProductMaintenance fast-detail completed productCode={ProductCode} requestedStore={RequestedStore} total_ms={TotalMs}",
                productCode,
                storeCode,
                totalSw.ElapsedMilliseconds
            );
            return Ok(result);
        }

        [HttpGet("{productCode}")]
        public async Task<IActionResult> GetDetail(
            string productCode,
            [FromQuery] string? storeCode = null,
            [FromQuery] bool includeCodes = true
        )
        {
            var totalSw = Stopwatch.StartNew();
            var access = await ResolveAccessContextAsync();
            _logger.LogInformation(
                "StoreProductMaintenance detail access resolved productCode={ProductCode} requestedStore={RequestedStore} allowed={IsAllowed} scope={Scope} actor={Actor} access_ms={AccessMs} user_lookup_ms={UserLookupMs} user_store_scope_ms={UserStoreScopeMs} device_auth_ms={DeviceAuthMs} device_load_ms={DeviceLoadMs}",
                productCode,
                storeCode,
                access.IsAllowed,
                FormatStoreScope(access.StoreCodes),
                access.ActorLabel,
                access.AccessResolveMs,
                access.UserLookupMs,
                access.UserStoreScopeMs,
                access.DeviceAuthMs,
                access.DeviceLoadMs
            );
            if (!access.IsAllowed)
            {
                _logger.LogWarning(
                    "StoreProductMaintenance detail unauthorized productCode={ProductCode} message={Message} total_ms={TotalMs}",
                    productCode,
                    access.Message,
                    totalSw.ElapsedMilliseconds
                );
                return Unauthorized(ApiResponse<StoreProductDetailDto>.Error(access.Message));
            }

            var result = await _service.GetDetailAsync(productCode, storeCode, access.StoreCodes, includeCodes);
            _logger.LogInformation(
                "StoreProductMaintenance detail completed productCode={ProductCode} requestedStore={RequestedStore} includeCodes={IncludeCodes} total_ms={TotalMs}",
                productCode,
                storeCode,
                includeCodes,
                totalSw.ElapsedMilliseconds
            );
            return Ok(result);
        }

        [HttpGet("{productCode}/codes")]
        public async Task<IActionResult> GetCodes(
            string productCode,
            [FromQuery] string? storeCode = null,
            [FromQuery] int type = 2,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? keyword = null
        )
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<object>.Error(access.Message));
            }

            if (type == 1)
            {
                var setResult = await _service.GetSetCodesAsync(
                    productCode,
                    storeCode,
                    page,
                    pageSize,
                    keyword,
                    access.StoreCodes
                );
                return Ok(setResult);
            }

            if (type == 2)
            {
                var multiResult = await _service.GetMultiCodesAsync(
                    productCode,
                    storeCode,
                    page,
                    pageSize,
                    keyword,
                    access.StoreCodes
                );
                return Ok(multiResult);
            }

            return BadRequest(ApiResponse<object>.Error("条码类型无效"));
        }

        [HttpPost("evaluate-auto-pricing")]
        public async Task<IActionResult> EvaluateAutoPricing(
            [FromBody] EvaluateStoreProductAutoPricingDto request
        )
        {
            var access = await ResolveAccessContextAsync();
            Console.WriteLine(
                $"[StoreProductMaintenance][EvaluateAutoPricing] productCode='{request.ProductCode}', requestedStore='{request.StoreCode}', forceAuto={request.ForceAutoPricing}, allowed={access.IsAllowed}, scope={FormatStoreScope(access.StoreCodes)}, actor='{access.ActorLabel}'"
            );
            if (!access.IsAllowed)
            {
                Console.WriteLine(
                    $"[StoreProductMaintenance][EvaluateAutoPricing] unauthorized: {access.Message}"
                );
                return Unauthorized(ApiResponse<EvaluateStoreProductAutoPricingResultDto>.Error(access.Message));
            }

            var result = await _service.EvaluateAutoPricingAsync(request, access.StoreCodes);
            return Ok(result);
        }

        [HttpPost("{productCode}/ensure-store-price")]
        public async Task<IActionResult> EnsureStorePrice(
            string productCode,
            [FromQuery] string? storeCode = null
        )
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<StoreProductStorePriceDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.EnsureStorePriceAsync(
                productCode, storeCode, access.ActorLabel, access.StoreCodes
            );
            return BuildMutationResult(result);
        }

        [HttpPut("store-prices/{uuid}")]
        public async Task<IActionResult> UpdateStorePrice(
            string uuid,
            [FromBody] UpdateStoreProductPriceDto request
        )
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<StoreProductStorePriceDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.UpdateStorePriceAsync(
                uuid,
                request,
                access.ActorLabel,
                access.StoreCodes
            );
            return BuildMutationResult(result);
        }

        [HttpPost("store-prices/{uuid}/sync-warehouse")]
        public async Task<IActionResult> SyncWarehousePrice(
            string uuid,
            [FromBody] SyncStoreProductWarehousePriceRequestDto request
        )
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(
                    ApiResponse<SyncStoreProductWarehousePriceResultDto>.Error(access.Message)
                );
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.SyncWarehousePriceAsync(
                uuid,
                request,
                access.ActorLabel,
                access.StoreCodes
            );
            if (string.Equals(result.ErrorCode, "PRICE_VERSION_CONFLICT", StringComparison.Ordinal))
            {
                return Conflict(result);
            }

            return BuildMutationResult(result);
        }

        [HttpPut("products/{productCode}/type")]
        public async Task<IActionResult> UpdateProductType(
            string productCode,
            [FromBody] UpdateStoreProductTypeDto request
        )
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<StoreProductTypeUpdateResultDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.UpdateProductTypeAsync(
                productCode,
                request,
                access.ActorLabel,
                access.StoreCodes
            );
            return Ok(result);
        }

        [HttpPut("multi-codes/{uuid}")]
        public async Task<IActionResult> UpdateMultiCode(
            string uuid,
            [FromBody] UpdateStoreProductMultiCodeDto request
        )
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<StoreProductMultiCodeDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.UpdateMultiCodeAsync(
                uuid,
                request,
                access.ActorLabel,
                access.StoreCodes
            );
            return BuildMutationResult(result);
        }

        [HttpPost("set-codes")]
        public async Task<IActionResult> CreateSetCode([FromBody] CreateStoreProductSetCodeDto request)
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<StoreProductSetCodeDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.CreateSetCodeAsync(request, access.ActorLabel, access.StoreCodes);
            return BuildMutationResult(result);
        }

        [HttpPut("set-codes/{setCodeId}")]
        public async Task<IActionResult> UpdateSetCode(
            string setCodeId,
            [FromBody] UpdateStoreProductSetCodeDto request
        )
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<StoreProductSetCodeDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.UpdateSetCodeAsync(
                setCodeId,
                request,
                access.ActorLabel,
                access.StoreCodes
            );
            return BuildMutationResult(result);
        }

        [HttpDelete("set-codes/{setCodeId}")]
        public async Task<IActionResult> DeleteSetCode(string setCodeId)
        {
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<DeleteStoreProductSetCodeResultDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.DeleteSetCodeAsync(setCodeId, access.ActorLabel, access.StoreCodes);
            return BuildMutationResult(result);
        }

        [HttpPost("set-codes/save-snapshot")]
        public async Task<IActionResult> SaveSetCodeSnapshot(
            [FromBody] SaveStoreProductSetCodeSnapshotDto request
        )
        {
            // 快照保存会同步所有门店的全局条码，必须使用不受限的超级管理员身份。
            if (!HasSuperAdminRole())
            {
                return Forbid();
            }

            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<SaveStoreProductSetCodeSnapshotResultDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.SaveSetCodeSnapshotAsync(
                request,
                access.ActorLabel,
                access.StoreCodes
            );
            return result.ErrorCode == StoreProductMaintenanceReactService.SetCodeSnapshotConflictErrorCode
                ? Conflict(result)
                : BuildMutationResult(result);
        }

        [HttpPut("products/{productCode}/clearance-price")]
        public async Task<IActionResult> UpsertClearancePrice(
            string productCode,
            [FromBody] UpsertStoreProductClearancePriceDto request
        )
        {
            _logger.LogInformation(
                "StoreProductMaintenance clearance price request received productCode={ProductCode} requestedStore={RequestedStore}",
                productCode,
                request.StoreCode
            );
            var access = await ResolveAccessContextAsync();
            if (!access.IsAllowed)
            {
                return Unauthorized(ApiResponse<StoreProductClearancePriceDto>.Error(access.Message));
            }
            var permissionFailure = await RequireEditPermissionAsync();
            if (permissionFailure != null) return permissionFailure;

            var result = await _service.UpsertClearancePriceAsync(
                productCode,
                request,
                access.ActorLabel,
                access.StoreCodes
            );
            return Ok(result);
        }

        private async Task<StoreAccessContext> ResolveAccessContextAsync(bool reuseValidatedDeviceScope = false)
        {
            var sw = Stopwatch.StartNew();
            if (User?.Identity?.IsAuthenticated == true)
            {
                try
                {
                    if (HasElevatedStoreAccess())
                    {
                        return new StoreAccessContext
                        {
                            IsAllowed = true,
                            ActorLabel = User.Identity?.Name ?? "system",
                            StoreCodes = null,
                            AccessResolveMs = sw.ElapsedMilliseconds,
                        };
                    }

                    var userLookupSw = Stopwatch.StartNew();
                    var userGuid = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                    var actorLabel = User.Identity?.Name ?? "system";
                    if (string.IsNullOrWhiteSpace(userGuid) && !string.IsNullOrWhiteSpace(actorLabel))
                    {
                        userGuid = await _db.Queryable<User>()
                            .Where(u => u.Username == actorLabel && !u.IsDeleted)
                            .Select(u => u.UserGUID)
                            .FirstAsync();
                    }
                    userLookupSw.Stop();

                    if (string.IsNullOrWhiteSpace(userGuid))
                    {
                        return new StoreAccessContext
                        {
                            IsAllowed = false,
                            Message = "未找到当前用户信息",
                            AccessResolveMs = sw.ElapsedMilliseconds,
                            UserLookupMs = userLookupSw.ElapsedMilliseconds,
                        };
                    }

                    // 仅扫码只读入口复用本次认证实时读取的门店范围；按用户核对，不跨请求缓存。
                    if (reuseValidatedDeviceScope
                        && HttpContext.Items.TryGetValue(typeof(AuthMobileDeviceValidationResult), out var snapshot)
                        && snapshot is AuthMobileDeviceValidationResult { IsValid: true } validated
                        && string.Equals(validated.UserGuid, userGuid, StringComparison.Ordinal)
                        && validated.AccessibleStoreCodes != null)
                    {
                        return new StoreAccessContext
                        {
                            IsAllowed = true,
                            ActorLabel = actorLabel,
                            StoreCodes = validated.AccessibleStoreCodes.ToList(),
                            AccessResolveMs = sw.ElapsedMilliseconds,
                            UserLookupMs = userLookupSw.ElapsedMilliseconds,
                        };
                    }

                    var userStoreScopeSw = Stopwatch.StartNew();
                    var storeCodes = await _db.Queryable<UserStore>()
                        .InnerJoin<Store>((us, s) => us.StoreGUID == s.StoreGUID)
                        .Where((us, s) => us.UserGUID == userGuid && !us.IsDeleted && !s.IsDeleted)
                        .Select((us, s) => s.StoreCode)
                        .ToListAsync();
                    userStoreScopeSw.Stop();

                    return new StoreAccessContext
                    {
                        IsAllowed = true,
                        ActorLabel = actorLabel,
                        StoreCodes = storeCodes,
                        AccessResolveMs = sw.ElapsedMilliseconds,
                        UserLookupMs = userLookupSw.ElapsedMilliseconds,
                        UserStoreScopeMs = userStoreScopeSw.ElapsedMilliseconds,
                    };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "解析登录用户可访问分店失败");
                    return new StoreAccessContext
                    {
                        IsAllowed = false,
                        Message = "解析当前用户分店权限失败",
                        AccessResolveMs = sw.ElapsedMilliseconds,
                    };
                }
            }

            var hardwareId = Request.Headers["X-Device-Id"].FirstOrDefault();
            var authCode = Request.Headers["X-Auth-Code"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(hardwareId) || string.IsNullOrWhiteSpace(authCode))
            {
                return new StoreAccessContext
                {
                    IsAllowed = false,
                    Message = "未登录且缺少设备授权信息",
                    AccessResolveMs = sw.ElapsedMilliseconds,
                };
            }

            var deviceAuthSw = Stopwatch.StartNew();
            var isValid = await _deviceRegistrationService.ValidateDeviceAuthCodeAsync(
                hardwareId,
                authCode
            );
            deviceAuthSw.Stop();
            if (!isValid)
            {
                return new StoreAccessContext
                {
                    IsAllowed = false,
                    Message = "设备授权无效",
                    AccessResolveMs = sw.ElapsedMilliseconds,
                    DeviceAuthMs = deviceAuthSw.ElapsedMilliseconds,
                };
            }

            var deviceLoadSw = Stopwatch.StartNew();
            var deviceEntity = await _deviceRegistrationService.GetDeviceByHardwareIdAsync(hardwareId);
            deviceLoadSw.Stop();
            if (deviceEntity == null)
            {
                return new StoreAccessContext
                {
                    IsAllowed = false,
                    Message = "设备不存在",
                    AccessResolveMs = sw.ElapsedMilliseconds,
                    DeviceAuthMs = deviceAuthSw.ElapsedMilliseconds,
                    DeviceLoadMs = deviceLoadSw.ElapsedMilliseconds,
                };
            }

            var device = _mapper.Map<DeviceDataDto>(deviceEntity);
            if (device.Status != 1 || string.IsNullOrWhiteSpace(device.StoreCode))
            {
                return new StoreAccessContext
                {
                    IsAllowed = false,
                    Message = "设备未启用或未绑定分店",
                    AccessResolveMs = sw.ElapsedMilliseconds,
                    DeviceAuthMs = deviceAuthSw.ElapsedMilliseconds,
                    DeviceLoadMs = deviceLoadSw.ElapsedMilliseconds,
                };
            }

            return new StoreAccessContext
            {
                IsAllowed = true,
                ActorLabel = $"device:{device.HardwareId}",
                StoreCodes = new List<string> { device.StoreCode },
                AccessResolveMs = sw.ElapsedMilliseconds,
                DeviceAuthMs = deviceAuthSw.ElapsedMilliseconds,
                DeviceLoadMs = deviceLoadSw.ElapsedMilliseconds,
            };
        }

        private async Task<IActionResult?> RequireEditPermissionAsync()
        {
            if (User?.Identity?.IsAuthenticated != true)
            {
                // 匿名绑定设备继续由既有设备授权与分店范围校验控制。
                return null;
            }

            var authorized = await _authorizationService.AuthorizeAsync(
                User,
                resource: null,
                Permissions.StoreProducts.Edit
            );
            return authorized.Succeeded ? null : Forbid();
        }

        private IActionResult BuildMutationResult<T>(ApiResponse<T> result)
        {
            return result.ErrorCode == SetChildPurchasePriceMutationLock.BusyErrorCode
                ? Conflict(result)
                : Ok(result);
        }

        private bool HasElevatedStoreAccess()
        {
            return HasSuperAdminRole()
                || HasRole("Manager")
                || HasRole("WarehouseManager")
                || HasRole("WarehouseStaff");
        }

        private bool HasSuperAdminRole()
        {
            return User?.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && Permissions.IsSuperAdminRole(claim.Value)
            ) == true;
        }

        private bool HasRole(string role)
        {
            return User?.Claims.Any(claim =>
                claim.Type == ClaimTypes.Role
                && claim.Value.Equals(role, StringComparison.OrdinalIgnoreCase)
            ) == true;
        }

        private static string FormatStoreScope(List<string>? storeCodes)
        {
            if (storeCodes == null)
            {
                return "ALL";
            }

            return storeCodes.Count == 0 ? "NONE" : string.Join(",", storeCodes);
        }

        private sealed class StoreAccessContext
        {
            public bool IsAllowed { get; set; }
            public string Message { get; set; } = "未授权";
            public string ActorLabel { get; set; } = "system";
            public List<string>? StoreCodes { get; set; }
            public long AccessResolveMs { get; set; }
            public long UserLookupMs { get; set; }
            public long UserStoreScopeMs { get; set; }
            public long DeviceAuthMs { get; set; }
            public long DeviceLoadMs { get; set; }
        }
    }
}
