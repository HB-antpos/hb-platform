using AutoMapper;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using BlazorApp.Shared.Options;

namespace BlazorApp.Api.Controllers
{
    /// <summary>
    /// POSM 设备注册管理控制器
    /// </summary>
    [ApiController]
    [Route("api")]
    public class DeviceRegistrationController : ControllerBase
    {
        private const string BearerPrefix = "Bearer ";
        private const string DeviceHardwareIdHeader = "X-HBPOS-Hardware-Id";
        private const string MobileDeviceIdHeader = "X-Device-Id";
        private const string MobileDeviceAuthCodeHeader = "X-Auth-Code";

        private readonly IDeviceRegistrationService _deviceService;
        private readonly ILogger<DeviceRegistrationController> _logger;
        private readonly IMapper _mapper;
        private readonly IStoreService _storeService;
        private readonly UserLoginDeviceAuditService? _loginDeviceAuditService;
        private readonly DeviceActivationOptions _deviceActivationOptions;
        private readonly MobileDeviceActivationOptions _mobileDeviceActivationOptions;
        private readonly RemoteMaintenanceService? _remoteMaintenanceService;

        public DeviceRegistrationController(
            IDeviceRegistrationService deviceService,
            ILogger<DeviceRegistrationController> logger,
            IMapper mapper,
            IStoreService storeService,
            UserLoginDeviceAuditService? loginDeviceAuditService = null,
            IOptions<DeviceActivationOptions>? deviceActivationOptions = null,
            IOptions<MobileDeviceActivationOptions>? mobileDeviceActivationOptions = null,
            RemoteMaintenanceService? remoteMaintenanceService = null
        )
        {
            _deviceService = deviceService;
            _logger = logger;
            _mapper = mapper;
            _storeService = storeService;
            _loginDeviceAuditService = loginDeviceAuditService;
            _deviceActivationOptions = deviceActivationOptions?.Value ?? new DeviceActivationOptions();
            _mobileDeviceActivationOptions =
                mobileDeviceActivationOptions?.Value ?? new MobileDeviceActivationOptions();
            _remoteMaintenanceService = remoteMaintenanceService;
        }

        /// <summary>
        /// 获取所有设备列表
        /// </summary>
        [HttpGet]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetAllDevices()
        {
            try
            {
                var devices = await _deviceService.GetAllDevicesAsync();
                var deviceDtos = _mapper.Map<List<DeviceListItemDto>>(devices);

                return Ok(new { success = true, data = deviceDtos });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取所有设备列表失败");
                return StatusCode(500, new { success = false, message = "获取设备列表失败" });
            }
        }

        /// <summary>
        /// 分页获取设备列表
        /// </summary>
        [HttpGet("paged")]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetDevicesPaged(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? storeCode = null,
            [FromQuery] string? deviceType = null,
            [FromQuery] string? deviceSystem = null,
            [FromQuery] int? status = null,
            [FromQuery] string? keyword = null
        )
        {
            try
            {
                var (devices, total) = await _deviceService.GetDevicesPagedAsync(
                    page,
                    pageSize,
                    storeCode,
                    deviceType,
                    deviceSystem,
                    status,
                    keyword
                );
                var deviceDtos = _mapper.Map<List<DeviceListItemDto>>(devices);

                // 远程版本：只为本页设备批量取 RustDesk 客户端版本号，不带任何远程维护凭据。
                string? rustDeskLatestVersion = null;
                if (_remoteMaintenanceService != null && deviceDtos.Count > 0)
                {
                    var versions = await _remoteMaintenanceService.GetClientVersionsAsync(
                        deviceDtos.Select(d => (d.Id, (string?)d.HardwareId)).ToList(),
                        HttpContext?.RequestAborted ?? CancellationToken.None
                    );
                    foreach (var dto in deviceDtos)
                    {
                        if (versions.TryGetValue(dto.Id, out var version))
                            dto.RustDeskClientVersion = version;
                    }
                    rustDeskLatestVersion = _remoteMaintenanceService.GetConfiguredRustdeskVersion();
                }

                return Ok(
                    new
                    {
                        success = true,
                        data = new
                        {
                            devices = deviceDtos,
                            rustDeskLatestVersion,
                            pagination = new
                            {
                                page = page,
                                pageSize = pageSize,
                                total = total,
                                totalPages = (int)Math.Ceiling((double)total / pageSize),
                            },
                        },
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分页获取设备列表失败");
                return StatusCode(500, new { success = false, message = "获取设备列表失败" });
            }
        }

        /// <summary>
        /// WPF设备运行状态上报
        /// </summary>
        [HttpPost("devices/runtime-status")]
        [AllowAnonymous]
        public async Task<IActionResult> ReportRuntimeStatus(
            [FromBody] DeviceRuntimeStatusUpdateDto request
        )
        {
            try
            {
                var hardwareId = ReadHeader(DeviceHardwareIdHeader);
                var authCode = ReadBearerToken();
                if (string.IsNullOrWhiteSpace(hardwareId) || string.IsNullOrWhiteSpace(authCode))
                {
                    return Unauthorized(new { success = false, message = "设备授权信息缺失" });
                }

                var isAuthorized = await _deviceService.ValidateDeviceAuthCodeAsync(
                    hardwareId,
                    authCode
                );
                if (!isAuthorized)
                {
                    return Unauthorized(new { success = false, message = "设备授权无效" });
                }

                var updated = await _deviceService.UpdateRuntimeStatusAsync(
                    hardwareId,
                    request.IsOnline,
                    request.CurrentCashierId,
                    request.CurrentCashierName
                );
                if (!updated)
                {
                    return NotFound(new { success = false, message = "设备不存在" });
                }

                return Ok(new { success = true, message = "设备运行状态已更新" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备运行状态上报失败");
                return StatusCode(500, new { success = false, message = "设备运行状态上报失败" });
            }
        }

        /// <summary>
        /// 移动端管理员分页获取设备列表
        /// </summary>
        [HttpGet("mobile/device-management/paged")]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public Task<IActionResult> GetMobileAdminDevicesPaged(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? storeCode = null,
            [FromQuery] string? deviceType = null,
            [FromQuery] string? deviceSystem = null,
            [FromQuery] int? status = null,
            [FromQuery] string? keyword = null
        )
        {
            return GetDevicesPaged(page, pageSize, storeCode, deviceType, deviceSystem, status, keyword);
        }

        /// <summary>
        /// 根据ID获取设备信息
        /// </summary>
        [HttpGet("{id:int}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> GetDeviceById(int id)
        {
            try
            {
                var device = await _deviceService.GetDeviceByIdAsync(id);
                if (device == null)
                {
                    return NotFound(new { success = false, message = "设备不存在" });
                }

                return Ok(new { success = true, data = device });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备信息失败: {DeviceId}", id);
                return StatusCode(500, new { success = false, message = "获取设备信息失败" });
            }
        }

        /// <summary>
        /// 根据硬件识别码获取设备信息
        /// </summary>
        [HttpGet("by-hardware-id/{hardwareId}")]
        public async Task<IActionResult> GetDeviceByHardwareId(string hardwareId)
        {
            try
            {
                var headerHardwareId = Request.Headers[MobileDeviceIdHeader].FirstOrDefault();
                var authCode = Request.Headers[MobileDeviceAuthCodeHeader].FirstOrDefault();
                if (
                    string.IsNullOrWhiteSpace(headerHardwareId)
                    || string.IsNullOrWhiteSpace(authCode)
                    || !string.Equals(
                        headerHardwareId,
                        hardwareId,
                        StringComparison.Ordinal
                    )
                )
                {
                    return Unauthorized(new { success = false, message = "设备授权信息缺失或不匹配" });
                }

                var isAuthorized = await _deviceService.ValidateDeviceAuthCodeAsync(
                    headerHardwareId,
                    authCode
                );
                if (!isAuthorized)
                {
                    return Unauthorized(new { success = false, message = "设备授权无效" });
                }

                var device = await _deviceService.GetDeviceByHardwareIdAsync(hardwareId);
                if (device == null)
                {
                    return NotFound(new { success = false, message = "设备不存在" });
                }

                // 使用AutoMapper转换为前端期望的格式
                var deviceData = _mapper.Map<DeviceDataDto>(device);
                // 凭据只能由注册/换绑响应下发；普通资料查询不得再次回传设备授权码。
                deviceData.AuthCode = string.Empty;
                await PopulateStoreNameAsync(deviceData);

                return Ok(new { success = true, data = deviceData });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据硬件识别码获取设备信息失败: {HardwareId}", hardwareId);
                return StatusCode(500, new { success = false, message = "获取设备信息失败" });
            }
        }

        private async Task PopulateStoreNameAsync(DeviceDataDto deviceData)
        {
            if (string.IsNullOrWhiteSpace(deviceData.StoreCode))
            {
                return;
            }

            try
            {
                var storeResult = await _storeService.GetStoreByCodeAsync(deviceData.StoreCode);
                if (storeResult.Success && storeResult.Data != null)
                {
                    deviceData.StoreName = storeResult.Data.StoreName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "获取设备绑定分店名称失败: {StoreCode}",
                    deviceData.StoreCode
                );
            }
        }

        /// <summary>
        /// 根据分店代码获取设备列表
        /// </summary>
        [HttpGet("by-store/{storeCode}")]
        [Authorize(Policy = Permissions.DeviceRegistration.View)]
        public async Task<IActionResult> GetDevicesByStoreCode(string storeCode)
        {
            try
            {
                var devices = await _deviceService.GetDevicesByStoreCodeAsync(storeCode);
                var deviceDtos = _mapper.Map<List<DeviceListItemDto>>(devices);
                return Ok(new { success = true, data = deviceDtos });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据分店代码获取设备列表失败: {StoreCode}", storeCode);
                return StatusCode(500, new { success = false, message = "获取设备列表失败" });
            }
        }

        /// <summary>
        /// 创建新设备
        /// </summary>
        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateDevice([FromBody] POSM_设备注册信息表 device)
        {
            try
            {
                var createdBy = User.Identity?.Name ?? "Unknown";
                var result = await _deviceService.CreateDeviceAsync(device, createdBy);

                return CreatedAtAction(
                    nameof(GetDeviceById),
                    new { id = result.ID },
                    new
                    {
                        success = true,
                        message = "设备创建成功",
                        data = result,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建设备失败");
                return StatusCode(500, new { success = false, message = "创建设备失败" });
            }
        }

        /// <summary>
        /// 更新设备信息
        /// </summary>
        [HttpPut("{id:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateDevice(int id, [FromBody] POSM_设备注册信息表 device)
        {
            try
            {
                if (id != device.ID)
                {
                    return BadRequest(new { success = false, message = "设备ID不匹配" });
                }

                var updatedBy = User.Identity?.Name ?? "Unknown";
                var result = await _deviceService.UpdateDeviceAsync(device, updatedBy);

                if (result)
                {
                    return Ok(new { success = true, message = "设备更新成功" });
                }
                else
                {
                    return NotFound(new { success = false, message = "设备不存在" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新设备失败: {DeviceId}", id);
                return StatusCode(500, new { success = false, message = "更新设备失败" });
            }
        }

        /// <summary>
        /// 删除设备
        /// </summary>
        [HttpDelete("{id:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteDevice(int id)
        {
            try
            {
                var result = await _deviceService.DeleteDeviceAsync(id);

                if (result)
                {
                    return Ok(new { success = true, message = "设备删除成功" });
                }
                else
                {
                    return NotFound(new { success = false, message = "设备不存在" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除设备失败: {DeviceId}", id);
                return StatusCode(500, new { success = false, message = "删除设备失败" });
            }
        }

        /// <summary>
        /// 设备注册
        /// </summary>
        [HttpPost("register")]
        [AllowAnonymous] // 允许匿名访问，用于新设备注册
        public async Task<IActionResult> RegisterDevice(
            [FromBody] DeviceRegistrationRequestDto request
        )
        {
            try
            {
                if (
                    string.IsNullOrEmpty(request.HardwareId)
                    || string.IsNullOrEmpty(request.DeviceType)
                    || string.IsNullOrEmpty(request.DeviceSystem)
                )
                {
                    return BadRequest(new { success = false, message = "必填字段不能为空" });
                }

                if (
                    _mobileDeviceActivationOptions.EnforceForNewRegistrations
                    && string.Equals(
                        request.DeviceType.Trim(),
                        "Mobile",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return Ok(new
                    {
                        success = false,
                        message = "需要一次性移动端设备开通码",
                        reasonCode = "ACTIVATION_CODE_REQUIRED",
                    });
                }

                if (string.Equals(request.DeviceType.Trim(), "POS", StringComparison.OrdinalIgnoreCase)
                    && DeviceActivationOptions.TryNormalizeDeviceSystem(
                        request.DeviceSystem,
                        out var deviceSystem)
                    && !_deviceActivationOptions.IsLegacyRegistrationEnabled(deviceSystem))
                {
                    // 中央旧入口仅拦截 POS 首次注册；PDA/Mobile、验证审批与专用 App Review 流程均不受影响。
                    return Ok(new
                    {
                        success = false,
                        message = "需要一次性设备开通码",
                        reasonCode = "ACTIVATION_CODE_REQUIRED",
                    });
                }

                var device = await _deviceService.RegisterDeviceAsync(
                    request.HardwareId,
                    request.DeviceType,
                    request.DeviceSystem,
                    request.StoreCode
                );

                // 使用AutoMapper转换设备注册响应数据
                var responseData = _mapper.Map<DeviceRegistrationResponseDto>(device);

                return Ok(
                    new
                    {
                        success = true,
                        message = "设备注册信息已同步",
                        data = responseData,
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备注册失败: {HardwareId}", request.HardwareId);
                return StatusCode(500, new { success = false, message = "设备注册失败" });
            }
        }

        /// <summary>
        /// 激活设备
        /// </summary>
        [HttpPost("{id}/activate")]
        [Authorize(Policy = Permissions.DeviceRegistration.Manage)]
        public async Task<IActionResult> ActivateDevice(int id)
        {
            try
            {
                var activatedBy = User.Identity?.Name ?? "Unknown";
                var result = await _deviceService.ActivateDeviceAsync(id, activatedBy);

                if (result)
                {
                    return Ok(new { success = true, message = "设备激活成功" });
                }
                else
                {
                    return NotFound(new { success = false, message = "设备不存在" });
                }
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "激活设备失败: {DeviceId}", id);
                return StatusCode(500, new { success = false, message = "激活设备失败" });
            }
        }

        /// <summary>
        /// 移动端管理员激活设备
        /// </summary>
        [HttpPost("mobile/device-management/{id}/activate")]
        [Authorize(Policy = Permissions.DeviceRegistration.Manage)]
        public Task<IActionResult> ActivateMobileAdminDevice(int id)
        {
            return ActivateDevice(id);
        }

        /// <summary>
        /// 禁用设备
        /// </summary>
        [HttpPost("{id}/disable")]
        [Authorize(Policy = Permissions.DeviceRegistration.Manage)]
        public async Task<IActionResult> DisableDevice(int id)
        {
            try
            {
                var disabledBy = User.Identity?.Name ?? "Unknown";
                var result = await _deviceService.DisableDeviceAsync(id, disabledBy);

                if (result)
                {
                    return Ok(new { success = true, message = "设备禁用成功" });
                }
                else
                {
                    return NotFound(new { success = false, message = "设备不存在" });
                }
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "禁用设备失败: {DeviceId}", id);
                return StatusCode(500, new { success = false, message = "禁用设备失败" });
            }
        }

        /// <summary>
        /// 移动端管理员禁用设备
        /// </summary>
        [HttpPost("mobile/device-management/{id}/disable")]
        [Authorize(Policy = Permissions.DeviceRegistration.Manage)]
        public Task<IActionResult> DisableMobileAdminDevice(int id)
        {
            return DisableDevice(id);
        }

        /// <summary>
        /// 锁定设备
        /// </summary>
        [HttpPost("{id}/lock")]
        [Authorize(Policy = Permissions.DeviceRegistration.Manage)]
        public async Task<IActionResult> LockDevice(int id)
        {
            try
            {
                var lockedBy = User.Identity?.Name ?? "Unknown";
                var result = await _deviceService.LockDeviceAsync(id, lockedBy);

                if (result)
                {
                    return Ok(new { success = true, message = "设备锁定成功" });
                }
                else
                {
                    return NotFound(new { success = false, message = "设备不存在" });
                }
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "锁定设备失败: {DeviceId}", id);
                return StatusCode(500, new { success = false, message = "锁定设备失败" });
            }
        }

        /// <summary>
        /// 移动端管理员锁定设备
        /// </summary>
        [HttpPost("mobile/device-management/{id}/lock")]
        [Authorize(Policy = Permissions.DeviceRegistration.Manage)]
        public Task<IActionResult> LockMobileAdminDevice(int id)
        {
            return LockDevice(id);
        }

        /// <summary>
        /// 解绑设备
        /// </summary>
        [HttpPost("unbind-device")]
        [AllowAnonymous]
        public async Task<IActionResult> UnbindDevice([FromBody] DeviceUnbindRequestDto request)
        {
            try
            {
                if (
                    string.IsNullOrEmpty(request.HardwareId)
                    || string.IsNullOrEmpty(request.AuthCode)
                )
                {
                    return BadRequest(
                        new { success = false, message = "硬件识别码和授权码不能为空" }
                    );
                }

                var result = await _deviceService.UnbindDeviceAsync(
                    request.HardwareId,
                    request.AuthCode,
                    "DeviceSelfService"
                );

                if (!result)
                {
                    return BadRequest(
                        new { success = false, message = "设备不存在或授权码不匹配" }
                    );
                }

                return Ok(new { success = true, message = "设备已解绑" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备解绑失败: {HardwareId}", request.HardwareId);
                return StatusCode(500, new { success = false, message = "设备解绑失败" });
            }
        }

        /// <summary>
        /// 验证设备授权码
        /// </summary>
        [HttpPost("validate-auth")]
        [AllowAnonymous] // 允许匿名访问，用于设备认证
        public async Task<IActionResult> ValidateDeviceAuth(
            [FromBody] DeviceAuthValidationRequestDto request
        )
        {
            try
            {
                if (
                    string.IsNullOrEmpty(request.HardwareId)
                    || string.IsNullOrEmpty(request.AuthCode)
                )
                {
                    return BadRequest(
                        new { success = false, message = "硬件识别码和授权码不能为空" }
                    );
                }

                var locationValidation = RequiredLocationValidator.Validate(
                    request.LocationLatitude,
                    request.LocationLongitude,
                    request.LocationPermissionStatus,
                    request.LocationCapturedAtUtc,
                    "设备登录需要位置信息",
                    TimeSpan.FromMinutes(5)
                );
                if (!locationValidation.Success)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = locationValidation.Message,
                        errorCode = locationValidation.ErrorCode,
                    });
                }

                // 使用新的验证和更新方法
                var (isValid, newAuthCode) =
                    await _deviceService.ValidateAndUpdateDeviceAuthCodeAsync(
                        request.HardwareId,
                        request.AuthCode
                    );

                var device = isValid ? await _deviceService.GetDeviceByHardwareIdAsync(request.HardwareId) : null;
                LoginDeviceAuditResult? audit = null;
                if (isValid && _loginDeviceAuditService != null)
                {
                    var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
                    var userAgent = Request.Headers["User-Agent"].ToString();
                    audit = await _loginDeviceAuditService.RecordDeviceLoginAsync(new DeviceLoginAuditInput(
                        HardwareId: request.HardwareId,
                        SystemDeviceNumber: request.SystemDeviceNumber ?? device?.系统设备编号,
                        DeviceSystem: request.DeviceSystem ?? device?.设备系统,
                        StoreCode: device?.分店代码,
                        LoginIp: ipAddress,
                        UserAgent: userAgent,
                        LocationLatitude: request.LocationLatitude,
                        LocationLongitude: request.LocationLongitude,
                        LocationAccuracy: request.LocationAccuracy,
                        LocationCapturedAtUtc: request.LocationCapturedAtUtc
                    ));
                }

                var responseData = new
                {
                    isValid = isValid,
                    newAuthCode = newAuthCode,
                    isDeviceSwitched = audit?.IsDeviceSwitched ?? false,
                    isCommonDevice = audit?.IsCommonDevice ?? false,
                };

                if (isValid)
                {
                    if (!string.IsNullOrEmpty(newAuthCode))
                    {
                        _logger.LogInformation(
                            "设备授权码验证成功，已返回最新授权码: {HardwareId}",
                            request.HardwareId
                        );
                        return Ok(
                            new
                            {
                                success = true,
                                data = responseData,
                                message = "设备验证成功，已获取最新授权码",
                            }
                        );
                    }
                    else
                    {
                        _logger.LogInformation(
                            "设备授权码验证成功: {HardwareId}",
                            request.HardwareId
                        );
                        return Ok(
                            new
                            {
                                success = true,
                                data = responseData,
                                message = "设备验证成功",
                            }
                        );
                    }
                }
                else
                {
                    _logger.LogWarning("设备授权码验证失败: {HardwareId}", request.HardwareId);
                    return Ok(
                        new
                        {
                            success = true,
                            data = responseData,
                            message = "设备验证失败",
                        }
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证设备授权码失败: {HardwareId}", request.HardwareId);
                return StatusCode(500, new { success = false, message = "验证授权码失败" });
            }
        }

        /// <summary>
        /// 生成新的设备授权码
        /// </summary>
        [HttpPost("{id}/generate-auth-code")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GenerateNewAuthCode(int id)
        {
            try
            {
                var generatedBy = User.Identity?.Name ?? "Unknown";
                var newAuthCode = await _deviceService.GenerateNewAuthCodeAsync(id, generatedBy);

                return Ok(
                    new
                    {
                        success = true,
                        message = "新授权码生成成功",
                        data = new { authCode = newAuthCode },
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成新授权码失败: {DeviceId}", id);
                return StatusCode(500, new { success = false, message = "生成新授权码失败" });
            }
        }

        /// <summary>
        /// 健康检查端点 - 用于测试API连通性
        /// </summary>
        [HttpGet("device-health")]
        [AllowAnonymous]
        public IActionResult HealthCheck()
        {
            return Ok(
                new
                {
                    success = true,
                    message = "API服务正常运行",
                    timestamp = DateTime.UtcNow,
                    version = "1.0.0",
                }
            );
        }

        /// <summary>
        /// 数据库健康检查端点 - 用于测试数据库连接
        /// </summary>
        [HttpGet("device-health/database")]
        [AllowAnonymous]
        public async Task<IActionResult> DatabaseHealthCheck()
        {
            try
            {
                // 尝试执行一个简单的数据库查询
                var deviceCount = await _deviceService.GetAllDevicesAsync();

                return Ok(
                    new
                    {
                        success = true,
                        message = "数据库连接正常",
                        timestamp = DateTime.UtcNow,
                        database = "POSM",
                        deviceCount = deviceCount.Count,
                        version = "1.0.0",
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库健康检查失败");
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "数据库连接失败",
                        error = ex.Message,
                        timestamp = DateTime.UtcNow,
                        database = "POSM",
                    }
                );
            }
        }

        /// <summary>
        /// 获取设备统计信息
        /// </summary>
        [HttpGet("statistics")]
        [Authorize(Roles = "Admin,Manager,StoreManager")]
        public async Task<IActionResult> GetStatistics()
        {
            try
            {
                var statistics = await _deviceService.GetDeviceStatisticsAsync();
                return Ok(new { success = true, data = statistics });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备统计信息失败");
                return StatusCode(500, new { success = false, message = "获取统计信息失败" });
            }
        }

        /// <summary>
        /// 获取状态描述
        /// </summary>
        private string GetStatusDescription(int status)
        {
            return status switch
            {
                0 => "待激活",
                1 => "已激活",
                2 => "已禁用",
                3 => "已锁定",
                _ => "未知状态",
            };
        }

        private string? ReadHeader(string headerName)
        {
            return Request.Headers.TryGetValue(headerName, out var values)
                ? values.ToString().Trim()
                : null;
        }

        private string? ReadBearerToken()
        {
            var authorization = ReadHeader("Authorization");
            if (string.IsNullOrWhiteSpace(authorization)
                || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return authorization[BearerPrefix.Length..].Trim();
        }

        /// <summary>
        /// 心跳检测端点 - 用于PDA设备测试服务器连接
        /// </summary>
        [HttpGet("Heartbeat")]
        [AllowAnonymous]
        public IActionResult Heartbeat()
        {
            try
            {
                _logger.LogInformation("收到心跳检测请求");
                return Ok(
                    new ApiResponse<bool>
                    {
                        Success = true,
                        Data = true,
                        Message = "服务器正常运行",
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "心跳检测处理失败");
                return Ok(
                    new ApiResponse<bool>
                    {
                        Success = false,
                        Data = false,
                        Message = "服务器异常",
                    }
                );
            }
        }
    }
}
