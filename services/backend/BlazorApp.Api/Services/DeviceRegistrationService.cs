using System.Security.Cryptography;
using System.Text;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.MobileDeviceActivation;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.POSM;
using SqlSugar;

namespace BlazorApp.Api.Services
{
    /// <summary>
    /// 设备注册服务实现
    /// </summary>
    public class DeviceRegistrationService : IDeviceRegistrationService
    {
        private readonly POSMSqlSugarContext _posmContext;
        private readonly ILogger<DeviceRegistrationService> _logger;
        private readonly Func<DateTime> _now;
        private readonly IMobileDeviceActivationService _mobileDeviceActivationService;

        public DeviceRegistrationService(
            POSMSqlSugarContext posmContext,
            ILogger<DeviceRegistrationService> logger,
            IMobileDeviceActivationService mobileDeviceActivationService,
            Func<DateTime>? nowProvider = null
        )
        {
            _posmContext = posmContext;
            _logger = logger;
            _now = nowProvider ?? (() => DateTime.Now);
            _mobileDeviceActivationService = mobileDeviceActivationService
                ?? throw new ArgumentNullException(nameof(mobileDeviceActivationService));
        }

        /// <summary>
        /// 获取所有设备列表
        /// </summary>
        public async Task<List<POSM_设备注册信息表>> GetAllDevicesAsync()
        {
            try
            {
                return await _posmContext.DeviceRegistrationDb.GetListAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取所有设备列表失败");
                throw;
            }
        }

        /// <summary>
        /// 根据ID获取设备信息
        /// </summary>
        public async Task<POSM_设备注册信息表?> GetDeviceByIdAsync(int id)
        {
            try
            {
                return await _posmContext.DeviceRegistrationDb.GetByIdAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据ID获取设备信息失败: {DeviceId}", id);
                throw;
            }
        }

        /// <summary>
        /// 根据硬件识别码获取设备信息
        /// </summary>
        public async Task<POSM_设备注册信息表?> GetDeviceByHardwareIdAsync(string hardwareId)
        {
            try
            {
                return await _posmContext.DeviceRegistrationDb.GetFirstAsync(d =>
                    d.设备硬件识别码 == hardwareId || d.系统设备编号 == hardwareId
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据硬件识别码获取设备信息失败: {HardwareId}", hardwareId);
                throw;
            }
        }

        private async Task<POSM_设备注册信息表?> ResolveDeviceForAuthenticationAsync(
            string identifier)
        {
            var exactHardwareMatches = await _posmContext.DeviceRegistrationDb.GetListAsync(
                device => device.设备硬件识别码 == identifier);
            if (exactHardwareMatches.Count > 0)
            {
                if (exactHardwareMatches.Count == 1)
                {
                    return exactHardwareMatches[0];
                }

                _logger.LogWarning(
                    "设备认证发现重复硬件标识，拒绝无序选择: {Identifier}, Count: {Count}",
                    identifier,
                    exactHardwareMatches.Count);
                return null;
            }

            var systemNumberMatches = await _posmContext.DeviceRegistrationDb.GetListAsync(
                device => device.系统设备编号 == identifier);
            if (systemNumberMatches.Count == 1)
            {
                return systemNumberMatches[0];
            }

            if (systemNumberMatches.Count > 1)
            {
                _logger.LogWarning(
                    "设备认证发现系统设备编号映射多个记录，拒绝别名歧义: {Identifier}, Count: {Count}",
                    identifier,
                    systemNumberMatches.Count);
            }
            return null;
        }

        /// <summary>
        /// 根据系统设备编号获取设备信息
        /// </summary>
        public async Task<POSM_设备注册信息表?> GetDeviceBySystemNumberAsync(
            string systemDeviceNumber
        )
        {
            try
            {
                return await _posmContext.DeviceRegistrationDb.GetFirstAsync(d =>
                    d.系统设备编号 == systemDeviceNumber
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "根据系统设备编号获取设备信息失败: {SystemDeviceNumber}",
                    systemDeviceNumber
                );
                throw;
            }
        }

        /// <summary>
        /// 根据分店代码获取设备列表
        /// </summary>
        public async Task<List<POSM_设备注册信息表>> GetDevicesByStoreCodeAsync(string storeCode)
        {
            try
            {
                return await _posmContext.DeviceRegistrationDb.GetListAsync(d =>
                    d.分店代码 == storeCode
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据分店代码获取设备列表失败: {StoreCode}", storeCode);
                throw;
            }
        }

        /// <summary>
        /// 根据设备状态获取设备列表
        /// </summary>
        public async Task<List<POSM_设备注册信息表>> GetDevicesByStatusAsync(int status)
        {
            try
            {
                return await _posmContext.DeviceRegistrationDb.GetListAsync(d =>
                    d.设备状态 == status
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "根据设备状态获取设备列表失败: {Status}", status);
                throw;
            }
        }

        /// <summary>
        /// 创建新设备
        /// </summary>
        public async Task<POSM_设备注册信息表> CreateDeviceAsync(
            POSM_设备注册信息表 device,
            string createdBy
        )
        {
            try
            {
                // 检查硬件识别码是否已存在
                var existingDevice = await GetDeviceByHardwareIdAsync(device.设备硬件识别码);
                if (existingDevice != null)
                {
                    throw new InvalidOperationException(
                        $"设备硬件识别码 {device.设备硬件识别码} 已存在"
                    );
                }

                // 生成系统设备编号
                if (string.IsNullOrEmpty(device.系统设备编号))
                {
                    device.系统设备编号 = await GenerateSystemDeviceNumberAsync();
                }

                // 生成设备授权码
                if (string.IsNullOrEmpty(device.设备授权码))
                {
                    device.设备授权码 = GenerateAuthCode();
                }

                // 设置审计字段
                device.创建时间 = DateTime.Now;
                device.创建人 = createdBy;

                var result = await _posmContext.DeviceRegistrationDb.InsertReturnEntityAsync(
                    device
                );
                _logger.LogInformation(
                    "创建设备成功: {DeviceId} - {HardwareId}",
                    result.ID,
                    result.设备硬件识别码
                );

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建设备失败");
                throw;
            }
        }

        /// <summary>
        /// 更新设备信息
        /// </summary>
        public async Task<bool> UpdateDeviceAsync(POSM_设备注册信息表 device, string updatedBy)
        {
            try
            {
                // 设置审计字段
                device.最后修改时间 = DateTime.Now;
                device.最后修改人 = updatedBy;

                var result = await _posmContext.DeviceRegistrationDb.UpdateAsync(device);
                _logger.LogInformation("更新设备成功: {DeviceId}", device.ID);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新设备失败: {DeviceId}", device.ID);
                throw;
            }
        }

        /// <summary>
        /// 删除设备
        /// </summary>
        public async Task<bool> DeleteDeviceAsync(int id)
        {
            try
            {
                var result = await _posmContext.DeviceRegistrationDb.DeleteByIdAsync(id);
                _logger.LogInformation("删除设备成功: {DeviceId}", id);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除设备失败: {DeviceId}", id);
                throw;
            }
        }

        /// <summary>
        /// 设备注册
        /// </summary>
        public async Task<POSM_设备注册信息表> RegisterDeviceAsync(
            string hardwareId,
            string deviceType,
            string deviceSystem,
            string? storeCode = null
        )
        {
            try
            {
                // 检查设备是否已注册
                var existingDevice = await GetDeviceByHardwareIdAsync(hardwareId);
                if (existingDevice != null)
                {
                    // 如果设备已存在但状态为未注册，则更新状态
                    if (existingDevice.设备状态 == (int)DeviceStatus.未注册)
                    {
                        var normalizedStoreCode = NormalizeBindingStoreCode(storeCode);
                        existingDevice.设备状态 = (int)DeviceStatus.待确认;
                        existingDevice.设备类型 = deviceType;
                        existingDevice.设备系统 = deviceSystem;
                        existingDevice.分店代码 = normalizedStoreCode;
                        existingDevice.设备授权码 = GenerateAuthCode();
                        await UpdateDeviceAsync(existingDevice, "System");
                        return existingDevice;
                    }

                    _logger.LogInformation(
                        "设备已注册，返回已有设备信息: {HardwareId}, Status: {Status}, StoreCode: {StoreCode}",
                        hardwareId,
                        existingDevice.设备状态,
                        existingDevice.分店代码
                    );
                    return existingDevice;
                }

                var newDeviceStoreCode = NormalizeBindingStoreCode(storeCode);

                // 创建新设备
                var newDevice = new POSM_设备注册信息表
                {
                    设备硬件识别码 = hardwareId,
                    设备类型 = deviceType,
                    设备系统 = deviceSystem,
                    分店代码 = newDeviceStoreCode,
                    设备状态 = (int)DeviceStatus.待确认,
                    系统设备编号 = await GeneratePdaSystemDeviceNumberAsync(newDeviceStoreCode),
                    设备授权码 = GenerateAuthCode(),
                };

                return await CreateDeviceAsync(newDevice, "System");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备注册失败: {HardwareId}", hardwareId);
                throw;
            }
        }

        /// <summary>
        /// 激活设备
        /// </summary>
        public async Task<bool> ActivateDeviceAsync(int id, string activatedBy)
        {
            try
            {
                var device = await GetDeviceByIdAsync(id);
                if (device == null)
                {
                    throw new InvalidOperationException($"设备 {id} 不存在");
                }

                device.设备状态 = (int)DeviceStatus.启用;
                return await UpdateDeviceAsync(device, activatedBy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "激活设备失败: {DeviceId}", id);
                throw;
            }
        }

        /// <summary>
        /// 禁用设备
        /// </summary>
        public async Task<bool> DisableDeviceAsync(int id, string disabledBy)
        {
            try
            {
                var device = await GetDeviceByIdAsync(id);
                if (device == null)
                {
                    throw new InvalidOperationException($"设备 {id} 不存在");
                }

                device.设备状态 = (int)DeviceStatus.禁用;
                return await UpdateDeviceAsync(device, disabledBy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "禁用设备失败: {DeviceId}", id);
                throw;
            }
        }

        /// <summary>
        /// 锁定设备
        /// </summary>
        public async Task<bool> LockDeviceAsync(int id, string lockedBy)
        {
            try
            {
                var device = await GetDeviceByIdAsync(id);
                if (device == null)
                {
                    throw new InvalidOperationException($"设备 {id} 不存在");
                }

                device.设备状态 = (int)DeviceStatus.锁定;
                return await UpdateDeviceAsync(device, lockedBy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "锁定设备失败: {DeviceId}", id);
                throw;
            }
        }

        /// <summary>
        /// 验证设备授权码
        /// </summary>
        public async Task<bool> ValidateDeviceAuthCodeAsync(string hardwareId, string authCode)
        {
            try
            {
                // 先按请求中的硬件标识查询绑定历史，避免同一 hardware 的旧 POS/其他记录
                // 被无序取中后绕过 Mobile 动态门禁。
                var bindingValidation = await _mobileDeviceActivationService
                    .ValidateBoundDeviceCredentialAsync(hardwareId, authCode);
                if (bindingValidation.RequiresBoundCredential)
                {
                    return bindingValidation.IsValid;
                }

                var device = await ResolveDeviceForAuthenticationAsync(hardwareId);
                if (device == null || device.设备状态 != (int)DeviceStatus.启用)
                {
                    return false;
                }

                if (!string.Equals(
                        device.设备硬件识别码,
                        hardwareId,
                        StringComparison.Ordinal))
                {
                    // 兼容历史调用方以系统设备编号查询；解析到规范 hardware 后仍须再过绑定门禁。
                    bindingValidation = await _mobileDeviceActivationService
                        .ValidateBoundDeviceCredentialAsync(
                            device.设备硬件识别码,
                            authCode);
                    if (bindingValidation.RequiresBoundCredential)
                    {
                        return bindingValidation.IsValid;
                    }
                }

                // POS/WPF 与尚未升级的新旧 Mobile 继续使用原有精确授权码合同。
                return device.设备授权码 == authCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证设备授权码失败: {HardwareId}", hardwareId);
                throw;
            }
        }

        /// <summary>
        /// 更新设备运行状态
        /// </summary>
        public async Task<bool> UpdateRuntimeStatusAsync(
            string hardwareId,
            bool isOnline,
            string? cashierId,
            string? cashierName)
        {
            try
            {
                var device = await GetDeviceByHardwareIdAsync(hardwareId);
                if (device == null)
                {
                    _logger.LogWarning("运行状态上报失败，设备不存在: {HardwareId}", hardwareId);
                    return false;
                }

                var now = _now();
                var nextCashierId = string.IsNullOrWhiteSpace(cashierId) ? null : cashierId.Trim();
                var nextCashierName = string.IsNullOrWhiteSpace(cashierName) ? null : cashierName.Trim();
                var hasCashier = nextCashierId is not null || nextCashierName is not null;

                // 中文注释：心跳只写运行态字段，不更新注册审计字段，避免后台“最后更新”被15秒心跳刷屏。
                device.是否在线 = isOnline;
                device.最后心跳时间 = now;
                if (!hasCashier)
                {
                    device.当前收银员ID = null;
                    device.当前收银员姓名 = null;
                    device.收银员登录时间 = null;
                }
                else
                {
                    var cashierChanged =
                        !string.Equals(device.当前收银员ID, nextCashierId, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(device.当前收银员姓名, nextCashierName, StringComparison.Ordinal);
                    device.当前收银员ID = nextCashierId;
                    device.当前收银员姓名 = nextCashierName;
                    if (cashierChanged || device.收银员登录时间 is null)
                    {
                        device.收银员登录时间 = now;
                    }
                }

                return await _posmContext.DeviceRegistrationDb.UpdateAsync(device);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新设备运行状态失败: {HardwareId}", hardwareId);
                throw;
            }
        }

        /// <summary>
        /// 验证设备授权码并在授权码不匹配时返回数据库中的最新授权码（仅限启用设备）
        /// </summary>
        /// <param name="hardwareId">硬件ID</param>
        /// <param name="authCode">当前授权码</param>
        /// <returns>验证结果，包含是否有效和数据库中的最新授权码（如果不匹配）</returns>
        public async Task<(bool IsValid, string? NewAuthCode)> ValidateAndUpdateDeviceAuthCodeAsync(
            string hardwareId,
            string authCode
        )
        {
            try
            {
                var bindingValidation = await _mobileDeviceActivationService
                    .ValidateBoundDeviceCredentialAsync(hardwareId, authCode);
                if (bindingValidation.RequiresBoundCredential)
                {
                    // 绑定历史永久接管该 hardware；动态凭据无论成功失败都不回传内部授权码。
                    return (bindingValidation.IsValid, null);
                }

                var device = await ResolveDeviceForAuthenticationAsync(hardwareId);

                // 设备不存在
                if (device == null)
                {
                    _logger.LogWarning("设备不存在: {HardwareId}", hardwareId);
                    return (false, null);
                }

                // 设备未启用
                if (device.设备状态 != (int)DeviceStatus.启用)
                {
                    _logger.LogWarning(
                        "设备未启用: {HardwareId}, 状态: {Status}",
                        hardwareId,
                        device.设备状态
                    );
                    return (false, null);
                }

                if (!string.Equals(
                        device.设备硬件识别码,
                        hardwareId,
                        StringComparison.Ordinal))
                {
                    bindingValidation = await _mobileDeviceActivationService
                        .ValidateBoundDeviceCredentialAsync(
                            device.设备硬件识别码,
                            authCode);
                    if (bindingValidation.RequiresBoundCredential)
                    {
                        // 新 Mobile 绑定的原始凭据只做动态校验；设备表内部码永不返回客户端。
                        return (bindingValidation.IsValid, null);
                    }
                }

                // 授权码匹配
                if (device.设备授权码 == authCode)
                {
                    _logger.LogInformation("设备授权码验证成功: {HardwareId}", hardwareId);
                    return (true, null);
                }

                // 设备存在且启用，但授权码不匹配 - 返回数据库中的最新授权码
                _logger.LogInformation(
                    "设备已启用但授权码不匹配，返回数据库中的最新授权码: {HardwareId}",
                    hardwareId
                );
                var latestAuthCode = device.设备授权码;

                if (string.IsNullOrEmpty(latestAuthCode))
                {
                    _logger.LogWarning(
                        "设备已启用但数据库中无有效授权码: {HardwareId}",
                        hardwareId
                    );
                    return (false, null);
                }

                _logger.LogInformation("返回数据库中的最新授权码给前端: {HardwareId}", hardwareId);
                return (true, latestAuthCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "验证和更新设备授权码失败: {HardwareId}", hardwareId);
                throw;
            }
        }

        /// <summary>
        /// 解绑设备
        /// </summary>
        public async Task<bool> UnbindDeviceAsync(
            string hardwareId,
            string authCode,
            string updatedBy
        )
        {
            try
            {
                var device = await GetDeviceByHardwareIdAsync(hardwareId);
                if (device == null)
                {
                    _logger.LogWarning("设备解绑失败，设备不存在: {HardwareId}", hardwareId);
                    return false;
                }

                if (device.设备授权码 != authCode)
                {
                    _logger.LogWarning("设备解绑失败，授权码不匹配: {HardwareId}", hardwareId);
                    return false;
                }

                device.设备状态 = (int)DeviceStatus.未注册;
                device.设备授权码 = string.Empty;
                return await UpdateDeviceAsync(device, updatedBy);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设备解绑失败: {HardwareId}", hardwareId);
                throw;
            }
        }

        /// <summary>
        /// 生成新的设备授权码
        /// </summary>
        public async Task<string> GenerateNewAuthCodeAsync(int id, string generatedBy)
        {
            try
            {
                var device = await GetDeviceByIdAsync(id);
                if (device == null)
                {
                    throw new InvalidOperationException($"设备 {id} 不存在");
                }

                var newAuthCode = GenerateAuthCode();
                device.设备授权码 = newAuthCode;
                await UpdateDeviceAsync(device, generatedBy);

                return newAuthCode;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "生成新的设备授权码失败: {DeviceId}", id);
                throw;
            }
        }

        /// <summary>
        /// 获取设备统计信息
        /// </summary>
        public async Task<object> GetDeviceStatisticsAsync()
        {
            try
            {
                var allDevices = await GetAllDevicesAsync();

                return new
                {
                    总设备数 = allDevices.Count,
                    启用设备数 = allDevices.Count(d => d.设备状态 == (int)DeviceStatus.启用),
                    禁用设备数 = allDevices.Count(d => d.设备状态 == (int)DeviceStatus.禁用),
                    待确认设备数 = allDevices.Count(d => d.设备状态 == (int)DeviceStatus.待确认),
                    锁定设备数 = allDevices.Count(d => d.设备状态 == (int)DeviceStatus.锁定),
                    未注册设备数 = allDevices.Count(d => d.设备状态 == (int)DeviceStatus.未注册),
                    设备类型统计 = allDevices
                        .GroupBy(d => d.设备类型)
                        .Select(g => new { 设备类型 = g.Key, 数量 = g.Count() })
                        .ToList(),
                    设备系统统计 = allDevices
                        .GroupBy(d => d.设备系统)
                        .Select(g => new { 设备系统 = g.Key, 数量 = g.Count() })
                        .ToList(),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取设备统计信息失败");
                throw;
            }
        }

        /// <summary>
        /// 分页获取设备列表
        /// </summary>
        public async Task<(List<POSM_设备注册信息表> devices, int total)> GetDevicesPagedAsync(
            int page = 1,
            int pageSize = 20,
            string? storeCode = null,
            string? deviceType = null,
            string? deviceSystem = null,
            int? status = null,
            string? keyword = null
        )
        {
            try
            {
                var query = _posmContext.Db.Queryable<POSM_设备注册信息表>();

                // 应用过滤条件
                if (!string.IsNullOrEmpty(storeCode))
                {
                    query = query.Where(d => d.分店代码 == storeCode);
                }

                if (!string.IsNullOrEmpty(deviceType))
                {
                    query = query.Where(d => d.设备类型 == deviceType);
                }

                var normalizedDeviceSystem = deviceSystem?.Trim();
                if (!string.IsNullOrEmpty(normalizedDeviceSystem))
                {
                    if (
                        string.Equals(
                            normalizedDeviceSystem,
                            DeviceRegistrationDeviceSystems.Windows,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        query = query.Where(d =>
                            d.设备系统 == null
                            || d.设备系统.Trim() == string.Empty
                            || d.设备系统.Trim()
                                == DeviceRegistrationDeviceSystems.Windows
                        );
                    }
                    else if (
                        string.Equals(
                            normalizedDeviceSystem,
                            DeviceRegistrationDeviceSystems.Other,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        query = query.Where(d =>
                            d.设备系统 != null
                            && d.设备系统.Trim() != string.Empty
                            && d.设备系统.Trim()
                                != DeviceRegistrationDeviceSystems.Windows
                            && d.设备系统.Trim()
                                != DeviceRegistrationDeviceSystems.IpadOs
                            // 安卓平板有独立筛选项，不再归入「其他」。
                            && d.设备系统.Trim()
                                != DeviceRegistrationDeviceSystems.AndroidTablet
                        );
                    }
                    else
                    {
                        query = query.Where(d =>
                            d.设备系统 != null
                            && d.设备系统.Trim() == normalizedDeviceSystem
                        );
                    }
                }

                if (status.HasValue)
                {
                    query = query.Where(d => d.设备状态 == status.Value);
                }

                if (!string.IsNullOrEmpty(keyword))
                {
                    query = query.Where(d =>
                        (d.设备硬件识别码 ?? string.Empty).Contains(keyword)
                        || (d.系统设备编号 ?? string.Empty).Contains(keyword)
                        || (d.分店代码 ?? string.Empty).Contains(keyword)
                        || (d.备注 ?? string.Empty).Contains(keyword)
                    );
                }

                // 获取总数
                var total = await query.CountAsync();

                // 分页查询
                var devices = await query
                    .OrderByDescending(d => d.创建时间)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync();

                return (devices, total);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "分页获取设备列表失败");
                throw;
            }
        }

        #region 私有方法

        /// <summary>
        /// 生成系统设备编号
        /// </summary>
        private async Task<string> GenerateSystemDeviceNumberAsync()
        {
            var prefix = "DEV";
            var timestamp = DateTime.Now.ToString("yyyyMMdd");

            // 查询当天已有的设备数量
            var todayStart = DateTime.Today;
            var todayEnd = todayStart.AddDays(1);

            var todayCount = await _posmContext
                .Db.Queryable<POSM_设备注册信息表>()
                .Where(d => d.创建时间 >= todayStart && d.创建时间 < todayEnd)
                .CountAsync();

            var sequence = (todayCount + 1).ToString("D4");

            return $"{prefix}{timestamp}{sequence}";
        }

        private static string NormalizeBindingStoreCode(string? storeCode)
        {
            var normalizedStoreCode = storeCode?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedStoreCode))
            {
                throw new InvalidOperationException("设备绑定必须提供分店代码");
            }

            return normalizedStoreCode;
        }

        /// <summary>
        /// 为设备绑定流程生成同分店 HHMM 不重复的 PDA 编号
        /// </summary>
        private async Task<string> GeneratePdaSystemDeviceNumberAsync(string storeCode)
        {
            const int systemDeviceNumberMaxLength = 50;
            const int minutesPerDay = 1440;

            var prefix = $"PDA_{storeCode}_";
            if (prefix.Length + 4 > systemDeviceNumberMaxLength)
            {
                throw new InvalidOperationException("分店代码过长，无法生成设备编号");
            }

            var existingNumbers = await _posmContext
                .Db.Queryable<POSM_设备注册信息表>()
                .Where(d => d.系统设备编号.StartsWith(prefix))
                .Select(d => d.系统设备编号)
                .ToListAsync();

            var usedMinutes = existingNumbers
                .Where(number => number.Length == prefix.Length + 4)
                .Select(number => number.Substring(prefix.Length, 4))
                .Where(value => value.All(char.IsDigit))
                .ToHashSet();

            var startTime = _now();
            for (var offset = 0; offset < minutesPerDay; offset++)
            {
                var hhmm = startTime.AddMinutes(offset).ToString("HHmm");
                if (!usedMinutes.Contains(hhmm))
                {
                    return $"{prefix}{hhmm}";
                }
            }

            throw new InvalidOperationException($"分店 {storeCode} 的设备编号 HHMM 已用尽");
        }

        /// <summary>
        /// 生成设备授权码
        /// </summary>
        private string GenerateAuthCode()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                var bytes = new byte[32];
                rng.GetBytes(bytes);
                return Convert
                    .ToBase64String(bytes)
                    .Replace("+", "-")
                    .Replace("/", "_")
                    .Replace("=", "");
            }
        }

        #endregion
    }
}
