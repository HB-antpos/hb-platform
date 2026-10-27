using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlazorApp.Api.Data;
using BlazorApp.Api.Security;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models.HBweb;
using BlazorApp.Shared.Models.POSM;
using Microsoft.Extensions.Options;
using SqlSugar;

namespace BlazorApp.Api.Services;

public sealed record RemoteMaintenancePrepareInternalRequest(
    Guid OperationId,
    string HardwareId,
    string ComputerName);

public sealed record RemoteMaintenanceCommitInternalRequest(
    Guid OperationId,
    string HardwareId,
    string RustdeskId,
    string ClientVersion,
    string Password);

public sealed class RemoteMaintenanceService
{
    private const int EnabledDeviceStatus = 1;
    private const string PosDeviceType = "POS";
    private const string WindowsDeviceSystem = "Windows";
    private const int MaxComputerNameLength = 120;
    private const int MaxRustdeskIdLength = 120;
    private const int MaxVersionLength = 80;
    private readonly SqlSugarContext _dbContext;
    private readonly POSMSqlSugarContext _posmContext;
    private readonly RemoteMaintenanceSecretProtector _secretProtector;
    private readonly IOptions<RemoteMaintenanceOptions> _options;
    private readonly ILogger<RemoteMaintenanceService> _logger;
    private readonly RemoteMaintenanceSchemaReadiness _schemaReadiness;
    private readonly Func<DateTime> _utcNow;

    public RemoteMaintenanceService(
        SqlSugarContext dbContext,
        POSMSqlSugarContext posmContext,
        RemoteMaintenanceSecretProtector secretProtector,
        IOptions<RemoteMaintenanceOptions> options,
        ILogger<RemoteMaintenanceService> logger,
        RemoteMaintenanceSchemaReadiness schemaReadiness,
        Func<DateTime>? utcNowProvider = null)
    {
        _dbContext = dbContext;
        _posmContext = posmContext;
        _secretProtector = secretProtector;
        _options = options;
        _logger = logger;
        _schemaReadiness = schemaReadiness;
        _utcNow = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    public async Task<RemoteMaintenanceResult<RemoteMaintenanceDeviceListResponseDto>> ListAsync(
        RemoteMaintenanceDeviceQueryDto query,
        CancellationToken cancellationToken = default)
    {
        if (!IsFeatureEnabled())
            return RemoteMaintenanceResult<RemoteMaintenanceDeviceListResponseDto>.Fail(
                "REMOTE_MAINTENANCE_DISABLED", "远程维护功能未启用");
        if (!await _schemaReadiness.IsReadyAsync(cancellationToken))
            return RemoteMaintenanceResult<RemoteMaintenanceDeviceListResponseDto>.Fail("REMOTE_MAINTENANCE_NOT_READY", "远程维护数据库迁移尚未完成");

        var now = EnsureUtc(_utcNow());
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, Math.Max(1, _options.Value.MaxPageSize));
        var threshold = now.AddSeconds(-Math.Max(1, _options.Value.OnlineThresholdSeconds));
        var dbQuery = _dbContext.Db.Queryable<RemoteMaintenanceDevice>()
            .Where(x => !x.IsDeleted);

        var keyword = Normalize(query.Keyword, 120);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            dbQuery = dbQuery.Where(x => x.StoreCode.Contains(keyword)
                || x.DeviceCode.Contains(keyword)
                || x.ComputerName.Contains(keyword)
                || (x.RustdeskId != null && x.RustdeskId.Contains(keyword)));
        }
        var storeCode = Normalize(query.StoreCode, 50);
        if (!string.IsNullOrWhiteSpace(storeCode)) dbQuery = dbQuery.Where(x => x.StoreCode == storeCode);
        var serviceStatus = Normalize(query.ServiceStatus, 20);
        if (!string.IsNullOrWhiteSpace(serviceStatus)) dbQuery = dbQuery.Where(x => x.ServiceStatus == serviceStatus);
        var onlineStatus = Normalize(query.OnlineStatus, 20)?.ToLowerInvariant();
        if (onlineStatus == RemoteMaintenanceOnlineStatuses.Never) dbQuery = dbQuery.Where(x => x.LastSeenAtUtc == null);
        if (onlineStatus == RemoteMaintenanceOnlineStatuses.Online) dbQuery = dbQuery.Where(x => x.LastSeenAtUtc >= threshold);
        if (onlineStatus == RemoteMaintenanceOnlineStatuses.Offline) dbQuery = dbQuery.Where(x => x.LastSeenAtUtc != null && x.LastSeenAtUtc < threshold);

        cancellationToken.ThrowIfCancellationRequested();
        var total = await dbQuery.CountAsync();
        var rows = await dbQuery.OrderByDescending(x => x.LastSeenAtUtc)
            .OrderByDescending(x => x.RegisteredAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        var response = new RemoteMaintenanceDeviceListResponseDto
        {
            Items = rows.Select(x => MapListItem(x, threshold)).ToList(),
            Total = total,
            Page = page,
            PageSize = pageSize,
            ServerTimeUtc = now
        };
        return RemoteMaintenanceResult<RemoteMaintenanceDeviceListResponseDto>.Ok(response);
    }

    /// <summary>
    /// 设备管理列表用：按设备注册 ID 批量取 RustDesk 客户端版本（只读，只带版本号，不碰 ID/密码/令牌）。
    /// 先按 DeviceRegistrationId 精确对应；对不上的（设备重新登记换了注册 ID）再按硬件识别码取最近一次上报。
    /// 表未迁移或查询失败时返回空字典，不影响设备列表本身。
    /// </summary>
    public async Task<Dictionary<int, string>> GetClientVersionsAsync(
        IReadOnlyCollection<(int RegistrationId, string? HardwareId)> devices,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, string>();
        if (devices.Count == 0) return result;
        var registrationIds = devices.Select(x => x.RegistrationId).Distinct().ToList();
        var hardwareIds = devices
            .Select(x => Normalize(x.HardwareId, 100))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<RemoteMaintenanceDevice> rows;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows = await _dbContext.Db.Queryable<RemoteMaintenanceDevice>()
                .Where(x => !x.IsDeleted && x.ClientVersion != null
                    && (registrationIds.Contains(x.DeviceRegistrationId) || hardwareIds.Contains(x.HardwareId)))
                .Select(x => new RemoteMaintenanceDevice
                {
                    Id = x.Id, DeviceRegistrationId = x.DeviceRegistrationId, HardwareId = x.HardwareId,
                    ClientVersion = x.ClientVersion, LastSeenAtUtc = x.LastSeenAtUtc, RegisteredAtUtc = x.RegisteredAtUtc
                })
                .ToListAsync();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 RustDesk 客户端版本失败，设备列表不显示远程版本");
            return result;
        }

        var withVersion = rows.Where(x => !string.IsNullOrWhiteSpace(x.ClientVersion)).ToList();
        // 同一注册 ID / 硬件号若有多条，取最近上报（LastSeen 优先，其次登记时间）的那条。
        static IEnumerable<RemoteMaintenanceDevice> Latest(IEnumerable<RemoteMaintenanceDevice> source) =>
            source.OrderByDescending(x => x.LastSeenAtUtc ?? DateTime.MinValue).ThenByDescending(x => x.RegisteredAtUtc);
        var byRegistration = withVersion.GroupBy(x => x.DeviceRegistrationId)
            .ToDictionary(g => g.Key, g => Latest(g).First().ClientVersion!.Trim());
        var byHardware = withVersion.Where(x => !string.IsNullOrWhiteSpace(x.HardwareId))
            .GroupBy(x => x.HardwareId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Latest(g).First().ClientVersion!.Trim(), StringComparer.OrdinalIgnoreCase);
        foreach (var (registrationId, hardwareId) in devices)
        {
            if (byRegistration.TryGetValue(registrationId, out var version))
                result[registrationId] = version;
            else if (Normalize(hardwareId, 100) is { } hw && byHardware.TryGetValue(hw, out var hwVersion))
                result[registrationId] = hwVersion;
        }
        return result;
    }

    /// <summary>远程维护下发的 RustDesk 安装包版本（配置项 RustdeskArtifact.Version）；功能未启用或未配置时为 null。</summary>
    public string? GetConfiguredRustdeskVersion()
    {
        if (!IsFeatureEnabled()) return null;
        var version = _options.Value.RustdeskArtifact?.Version?.Trim();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    public async Task<RemoteMaintenanceResult<RemoteMaintenanceCredentialResponseDto>> GetCredentialAsync(
        Guid id, string actor, CancellationToken cancellationToken = default)
    {
        if (!IsFeatureEnabled()) return RemoteMaintenanceResult<RemoteMaintenanceCredentialResponseDto>.Fail("REMOTE_MAINTENANCE_DISABLED", "远程维护功能未启用");
        if (!await _schemaReadiness.IsReadyAsync(cancellationToken)) return RemoteMaintenanceResult<RemoteMaintenanceCredentialResponseDto>.Fail("REMOTE_MAINTENANCE_NOT_READY", "远程维护数据库迁移尚未完成");
        cancellationToken.ThrowIfCancellationRequested();
        var row = await _dbContext.Db.Queryable<RemoteMaintenanceDevice>().FirstAsync(x => x.Id == id && !x.IsDeleted);
        if (row is null) return RemoteMaintenanceResult<RemoteMaintenanceCredentialResponseDto>.Fail("REMOTE_MAINTENANCE_DEVICE_NOT_FOUND", "远程维护设备不存在");
        var registrations = await RemoteMaintenanceRegistrationResolver.ResolveAsync(_posmContext, [row], cancellationToken);
        if (!registrations.ContainsKey(row.Id))
            return RemoteMaintenanceResult<RemoteMaintenanceCredentialResponseDto>.Fail("REMOTE_MAINTENANCE_DEVICE_DISABLED", "设备已停用或重新登记");
        if (string.IsNullOrWhiteSpace(row.CredentialCiphertext)) return RemoteMaintenanceResult<RemoteMaintenanceCredentialResponseDto>.Fail("REMOTE_MAINTENANCE_CREDENTIAL_NOT_READY", "设备尚未提交远程维护密码");
        try
        {
            var password = _secretProtector.UnprotectPassword(row.CredentialCiphertext);
            // 审计只写设备标识与动作，永不写入密码、token 或密文。
            _logger.LogInformation("Remote maintenance credential viewed. DeviceId={DeviceId}, Actor={Actor}", id, Normalize(actor, 120));
            return RemoteMaintenanceResult<RemoteMaintenanceCredentialResponseDto>.Ok(new() { Password = password });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "远程维护凭据解密失败。DeviceId={DeviceId}", id);
            return RemoteMaintenanceResult<RemoteMaintenanceCredentialResponseDto>.Fail("REMOTE_MAINTENANCE_CREDENTIAL_UNAVAILABLE", "远程维护密码暂不可用");
        }
    }

    public RemoteMaintenanceResult<RemoteMaintenanceManifestDto> GetManifest()
    {
        var readiness = ValidateArtifactReadiness();
        if (!readiness.Success)
            return RemoteMaintenanceResult<RemoteMaintenanceManifestDto>.Fail(readiness.Code!, readiness.Message!);
        return RemoteMaintenanceResult<RemoteMaintenanceManifestDto>.Ok(BuildManifest());
    }

    public RemoteMaintenanceResult<RemoteMaintenanceArtifactFile> GetArtifactFile(string kind)
    {
        var readiness = ValidateArtifactReadiness();
        if (!readiness.Success)
            return RemoteMaintenanceResult<RemoteMaintenanceArtifactFile>.Fail(readiness.Code!, readiness.Message!);
        var normalized = kind.Trim().ToLowerInvariant();
        var artifact = normalized switch
        {
            "rustdesk" => _options.Value.RustdeskArtifact,
            "status-agent" => _options.Value.StatusAgentArtifact,
            _ => null
        };
        if (artifact is null) return RemoteMaintenanceResult<RemoteMaintenanceArtifactFile>.Fail("REMOTE_MAINTENANCE_ARTIFACT_KIND_INVALID", "不支持的远程维护 artifact 类型");
        return RemoteMaintenanceResult<RemoteMaintenanceArtifactFile>.Ok(new(
            normalized, artifact.FileName, ResolveArtifactPath(artifact), artifact.SizeBytes, artifact.Sha256));
    }

    public async Task<RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>> PrepareAsync(
        RemoteMaintenancePrepareInternalRequest request, CancellationToken cancellationToken = default)
    {
        var readiness = ValidateArtifactReadiness();
        if (!readiness.Success) return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail(readiness.Code!, readiness.Message!);
        if (!await _schemaReadiness.IsReadyAsync(cancellationToken)) return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("REMOTE_MAINTENANCE_NOT_READY", "远程维护数据库迁移尚未完成");
        if (request.OperationId == Guid.Empty) return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("OPERATION_ID_REQUIRED", "operationId 必须提供");
        if (request.HardwareId?.Length > 100 || request.ComputerName?.Length > MaxComputerNameLength)
            return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("REMOTE_MAINTENANCE_INVALID_REQUEST", "硬件号或计算机名过长");
        var hardwareId = Normalize(request.HardwareId, 100);
        var computerName = Normalize(request.ComputerName, MaxComputerNameLength);
        if (string.IsNullOrWhiteSpace(hardwareId) || string.IsNullOrWhiteSpace(computerName)) return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("REMOTE_MAINTENANCE_INVALID_REQUEST", "硬件号和计算机名必须提供");
        var registration = await ResolveEnabledWindowsPosAsync(hardwareId, cancellationToken);
        if (registration is null) return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("REMOTE_MAINTENANCE_DEVICE_NOT_ELIGIBLE", "当前设备不是有效的 Windows POS 设备");
        var now = EnsureUtc(_utcNow());
        var row = await _dbContext.Db.Queryable<RemoteMaintenanceDevice>().FirstAsync(x => x.DeviceRegistrationId == registration.ID && !x.IsDeleted);
        if (row is null)
        {
            row = new RemoteMaintenanceDevice
            {
                Id = Guid.NewGuid(), DeviceRegistrationId = registration.ID, HardwareId = hardwareId,
                RegisteredAtUtc = now, LastAcceptedSequence = 0, IsDeleted = false
            };
            try { await _dbContext.Db.Insertable(row).ExecuteCommandAsync(); }
            catch (Exception ex) when (ex.Message.Contains("UX_HBweb_RemoteMaintenanceDevice_DeviceRegistrationId", StringComparison.OrdinalIgnoreCase))
            {
                row = await _dbContext.Db.Queryable<RemoteMaintenanceDevice>().FirstAsync(x => x.DeviceRegistrationId == registration.ID && !x.IsDeleted);
                if (row is null) return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_CONFLICT", "设备登记正在被其他操作处理");
            }
        }
        if (row.LastOperationId.HasValue && row.LastOperationId != request.OperationId && !string.IsNullOrWhiteSpace(row.CommitResponseCiphertext))
            return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_CONFLICT", "设备已有已提交的远程维护操作");
        if (row.LastOperationId.HasValue && row.LastOperationId != request.OperationId)
            return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_CONFLICT", "设备已有其他远程维护操作正在处理");
        // 只更新 prepare 所有的列，避免把并发 heartbeat 的 sequence/lastSeen 用旧对象覆盖。
        var prepared = await _dbContext.Db.Updateable<RemoteMaintenanceDevice>()
            .SetColumns(x => new RemoteMaintenanceDevice { HardwareId = hardwareId, StoreCode = Normalize(registration.分店代码, 50) ?? string.Empty, DeviceCode = Normalize(registration.系统设备编号, 100) ?? string.Empty, ComputerName = computerName, LastOperationId = request.OperationId })
            // 显式判空避免 SqlSugar 翻译 !HasValue 时丢失 OR；保留同一操作重试的并发保护。
            .Where(x => x.Id == row.Id && !x.IsDeleted && (x.LastOperationId == null || x.LastOperationId == request.OperationId))
            .ExecuteCommandAsync();
        if (prepared != 1) return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_CONFLICT", "设备登记正在被其他操作处理");
        var response = new RemoteMaintenancePrepareResponseDto
        {
            OperationId = request.OperationId, DeviceId = row.Id,
            Config = new() { IdServer = _options.Value.IdServer, RelayServer = _options.Value.RelayServer, PublicKey = _options.Value.PublicKey },
            ArtifactManifest = BuildManifest()
        };
        _logger.LogInformation("Remote maintenance prepare completed. DeviceId={DeviceId}, RegistrationId={RegistrationId}", row.Id, registration.ID);
        return RemoteMaintenanceResult<RemoteMaintenancePrepareResponseDto>.Ok(response);
    }

    public async Task<RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>> CommitAsync(
        RemoteMaintenanceCommitInternalRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsFeatureEnabled()) return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_DISABLED", "远程维护功能未启用");
        if (!await _schemaReadiness.IsReadyAsync(cancellationToken)) return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_NOT_READY", "远程维护数据库迁移尚未完成");
        var registration = await ResolveEnabledWindowsPosAsync(request.HardwareId, cancellationToken);
        if (registration is null) return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_DEVICE_NOT_ELIGIBLE", "当前设备不是有效的 Windows POS 设备");
        var row = await _dbContext.Db.Queryable<RemoteMaintenanceDevice>().FirstAsync(x => x.DeviceRegistrationId == registration.ID && !x.IsDeleted);
        if (row is null || row.LastOperationId != request.OperationId) return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_INVALID", "远程维护操作不存在或已过期");
        if (request.RustdeskId?.Length > MaxRustdeskIdLength || request.ClientVersion?.Length > MaxVersionLength || request.Password?.Length > 256)
            return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_INVALID_REQUEST", "RustDesk ID、版本或密码过长");
        if (string.IsNullOrWhiteSpace(request.RustdeskId) || string.IsNullOrWhiteSpace(request.ClientVersion) || string.IsNullOrWhiteSpace(request.Password)) return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_INVALID_REQUEST", "RustDesk ID、版本和密码必须提供");
        string expectedPassword;
        try { expectedPassword = string.IsNullOrWhiteSpace(row.CredentialCiphertext) ? string.Empty : _secretProtector.UnprotectPassword(row.CredentialCiphertext); }
        catch { return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_CREDENTIAL_UNAVAILABLE", "远程维护密码暂不可用"); }
        if (!string.IsNullOrWhiteSpace(expectedPassword) && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expectedPassword), Encoding.UTF8.GetBytes(request.Password)))
            return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_PASSWORD_MISMATCH", "远程维护密码不匹配");
        if (string.IsNullOrWhiteSpace(row.CredentialCiphertext)) row.CredentialCiphertext = _secretProtector.ProtectPassword(request.Password);
        if (!string.IsNullOrWhiteSpace(row.CommitResponseCiphertext))
        {
            if (!string.Equals(row.RustdeskId, Normalize(request.RustdeskId, MaxRustdeskIdLength), StringComparison.Ordinal)
                || !string.Equals(row.ClientVersion, Normalize(request.ClientVersion, MaxVersionLength), StringComparison.Ordinal))
                return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_CONFLICT", "同一操作的 RustDesk 客户端信息不匹配");
            try { return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Ok(JsonSerializer.Deserialize<RemoteMaintenanceCommitResponseDto>(_secretProtector.UnprotectOperationResponse(row.CommitResponseCiphertext))!); }
            catch { return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_UNAVAILABLE", "操作响应无法恢复"); }
        }
        var token = GenerateToken();
        var response = new RemoteMaintenanceCommitResponseDto { DeviceId = row.Id, MonitorToken = token, HeartbeatUrl = BuildHeartbeatUrl(row.Id) };
        var commitCiphertext = _secretProtector.ProtectOperationResponse(JsonSerializer.Serialize(response));
        var affected = await _dbContext.Db.Updateable<RemoteMaintenanceDevice>()
            .SetColumns(x => new RemoteMaintenanceDevice { RustdeskId = Normalize(request.RustdeskId, MaxRustdeskIdLength), ClientVersion = Normalize(request.ClientVersion, MaxVersionLength), MonitorTokenHash = Hash(token), CredentialCiphertext = string.IsNullOrWhiteSpace(row.CredentialCiphertext) ? _secretProtector.ProtectPassword(request.Password) : row.CredentialCiphertext, CommitResponseCiphertext = commitCiphertext })
            .Where(x => x.Id == row.Id && !x.IsDeleted && x.LastOperationId == request.OperationId && x.CommitResponseCiphertext == null)
            .ExecuteCommandAsync();
        if (affected != 1)
        {
            var committed = await _dbContext.Db.Queryable<RemoteMaintenanceDevice>().FirstAsync(x => x.Id == row.Id && !x.IsDeleted);
            if (committed is null || string.IsNullOrWhiteSpace(committed.CommitResponseCiphertext)) return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_CONFLICT", "操作正在被其他请求提交");
            try
            {
                var committedPassword = _secretProtector.UnprotectPassword(committed.CredentialCiphertext ?? string.Empty);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(committedPassword), Encoding.UTF8.GetBytes(request.Password)))
                    return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_PASSWORD_MISMATCH", "远程维护密码不匹配");
                if (!string.Equals(committed.RustdeskId, Normalize(request.RustdeskId, MaxRustdeskIdLength), StringComparison.Ordinal)
                    || !string.Equals(committed.ClientVersion, Normalize(request.ClientVersion, MaxVersionLength), StringComparison.Ordinal))
                    return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_CONFLICT", "同一操作的 RustDesk 客户端信息不匹配");
            }
            catch { return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_CREDENTIAL_UNAVAILABLE", "远程维护密码暂不可用"); }
            try { return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Ok(JsonSerializer.Deserialize<RemoteMaintenanceCommitResponseDto>(_secretProtector.UnprotectOperationResponse(committed.CommitResponseCiphertext))!); }
            catch { return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Fail("REMOTE_MAINTENANCE_OPERATION_UNAVAILABLE", "操作响应无法恢复"); }
        }
        _logger.LogInformation("Remote maintenance commit completed. DeviceId={DeviceId}", row.Id);
        return RemoteMaintenanceResult<RemoteMaintenanceCommitResponseDto>.Ok(response);
    }

    public async Task<RemoteMaintenanceResult<RemoteMaintenanceArtifactFile>> GetArtifactFileForHardwareAsync(
        string kind, string hardwareId, CancellationToken cancellationToken = default)
    {
        var registration = await ResolveEnabledWindowsPosAsync(hardwareId, cancellationToken);
        if (registration is null)
            return RemoteMaintenanceResult<RemoteMaintenanceArtifactFile>.Fail("REMOTE_MAINTENANCE_DEVICE_DISABLED", "设备已失效");
        return GetArtifactFile(kind);
    }

    public async Task<RemoteMaintenanceResult<object>> HeartbeatAsync(
        Guid deviceId, string monitorToken, RemoteMaintenanceHeartbeatRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!IsFeatureEnabled()) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_DISABLED", "远程维护功能未启用");
        if (!await _schemaReadiness.IsReadyAsync(cancellationToken)) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_NOT_READY", "远程维护数据库迁移尚未完成");
        if (deviceId == Guid.Empty || string.IsNullOrWhiteSpace(monitorToken) || monitorToken.Length > 512 || request.Sequence <= 0
            || request.AgentVersion?.Length > MaxVersionLength || request.RustdeskId?.Length > MaxRustdeskIdLength
            || request.ClientVersion?.Length > MaxVersionLength || !RemoteMaintenanceServiceStatuses.IsValid(request.ServiceStatus)) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_INVALID_REQUEST", "心跳参数无效");
        var tokenHash = Hash(monitorToken);
        var row = await _dbContext.Db.Queryable<RemoteMaintenanceDevice>().FirstAsync(x => x.Id == deviceId && !x.IsDeleted);
        if (row is null || string.IsNullOrWhiteSpace(row.MonitorTokenHash) || !FixedTimeEquals(row.MonitorTokenHash, tokenHash)) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_TOKEN_INVALID", "monitor token 无效");
        var registrations = await RemoteMaintenanceRegistrationResolver.ResolveAsync(_posmContext, [row], cancellationToken);
        if (!registrations.ContainsKey(row.Id)) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_DEVICE_DISABLED", "设备已失效");
        var now = EnsureUtc(_utcNow());
        if (request.Sequence <= row.LastAcceptedSequence) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_SEQUENCE_REJECTED", "心跳 sequence 必须严格递增");
        if (row.LastAcceptedAtUtc.HasValue && now - EnsureUtc(row.LastAcceptedAtUtc.Value) < TimeSpan.FromSeconds(Math.Max(1, _options.Value.HeartbeatMinIntervalSeconds))) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_RATE_LIMITED", "心跳过于频繁");
        var rateThreshold = now.AddSeconds(-Math.Max(1, _options.Value.HeartbeatMinIntervalSeconds));
        var affected = await _dbContext.Db.Updateable<RemoteMaintenanceDevice>()
            // heartbeat 只维护状态快照；commit 登记的 RustDesk ID/版本不能被空值或旧 agent 覆盖。
            .SetColumns(x => new RemoteMaintenanceDevice { LastAcceptedSequence = request.Sequence, LastAcceptedAtUtc = now, LastSeenAtUtc = now, AgentVersion = Normalize(request.AgentVersion, MaxVersionLength), ServiceStatus = request.ServiceStatus })
            .Where(x => x.Id == deviceId && !x.IsDeleted && x.MonitorTokenHash == tokenHash && x.LastAcceptedSequence < request.Sequence && (x.LastAcceptedAtUtc == null || x.LastAcceptedAtUtc <= rateThreshold))
            .ExecuteCommandAsync();
        if (affected == 1) return RemoteMaintenanceResult<object>.Ok(new { });
        // CAS 失败时重新读取仅用于区分稳定错误码；不会再次写入，且并发请求仍以数据库条件为准。
        var current = await _dbContext.Db.Queryable<RemoteMaintenanceDevice>()
            .FirstAsync(x => x.Id == deviceId && !x.IsDeleted);
        if (current?.LastAcceptedAtUtc is { } acceptedAt
            && EnsureUtc(acceptedAt) > rateThreshold)
            return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_RATE_LIMITED", "心跳过于频繁");
        return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_SEQUENCE_REJECTED", "心跳 sequence 必须严格递增");
    }

    private async Task<POSM_设备注册信息表?> ResolveEnabledWindowsPosAsync(string hardwareId, CancellationToken cancellationToken)
    {
        var normalized = Normalize(hardwareId, 100);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var latest = await _posmContext.Db.Queryable<POSM_设备注册信息表>()
            .Where(x => x.设备硬件识别码 == normalized)
            .OrderByDescending(x => x.ID).FirstAsync();
        return latest is not null && latest.设备状态 == EnabledDeviceStatus && string.Equals(latest.设备类型, PosDeviceType, StringComparison.OrdinalIgnoreCase) && string.Equals(latest.设备系统, WindowsDeviceSystem, StringComparison.OrdinalIgnoreCase)
            ? latest : null;
    }

    private RemoteMaintenanceResult<object> ValidateArtifactReadiness()
    {
        if (!IsFeatureEnabled()) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_DISABLED", "远程维护功能未启用");
        if (string.IsNullOrWhiteSpace(_options.Value.IdServer) || string.IsNullOrWhiteSpace(_options.Value.RelayServer) || string.IsNullOrWhiteSpace(_options.Value.PublicKey)) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_NOT_READY", "远程维护服务器配置不完整");
        foreach (var artifact in new[] { _options.Value.RustdeskArtifact, _options.Value.StatusAgentArtifact })
        {
            if (string.IsNullOrWhiteSpace(artifact.Version) || string.IsNullOrWhiteSpace(artifact.FileName) || string.IsNullOrWhiteSpace(artifact.Sha256) || artifact.SizeBytes <= 0) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_NOT_READY", "artifact 清单不完整");
            var path = ResolveArtifactPath(artifact);
            if (!File.Exists(path)) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_NOT_READY", "artifact 文件不存在");
            var info = new FileInfo(path);
            if (info.Length != artifact.SizeBytes) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_NOT_READY", "artifact 大小校验失败");
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase)) return RemoteMaintenanceResult<object>.Fail("REMOTE_MAINTENANCE_NOT_READY", "artifact SHA-256 校验失败");
        }
        return RemoteMaintenanceResult<object>.Ok(new { });
    }

    private RemoteMaintenanceManifestDto BuildManifest() => new()
    {
        IdServer = _options.Value.IdServer,
        RelayServer = _options.Value.RelayServer,
        PublicKey = _options.Value.PublicKey,
        Rustdesk = BuildArtifact(_options.Value.RustdeskArtifact, "rustdesk"),
        StatusAgent = BuildArtifact(_options.Value.StatusAgentArtifact, "status-agent")
    };

    private RemoteMaintenanceArtifactDto BuildArtifact(RemoteMaintenanceArtifactOptions artifact, string kind) => new()
    {
        Version = artifact.Version, FileName = artifact.FileName,
        DownloadUrl = $"/api/remote-maintenance/admin/artifacts/{kind}",
        Sha256 = artifact.Sha256.ToLowerInvariant(), SizeBytes = artifact.SizeBytes
    };

    private string BuildHeartbeatUrl(Guid id) => $"{(_options.Value.HeartbeatBaseUrl ?? "https://hotbargain.vip").TrimEnd('/')}/api/remote-maintenance/devices/{id:D}/heartbeat";
    private string ResolveArtifactPath(RemoteMaintenanceArtifactOptions artifact) => Path.IsPathRooted(artifact.Path) ? artifact.Path : Path.Combine(_options.Value.ArtifactRootPath ?? string.Empty, artifact.Path);
    private bool IsFeatureEnabled() => _options.Value.Enabled;
    internal static RemoteMaintenanceDeviceListItemDto MapListItem(RemoteMaintenanceDevice x, DateTime threshold) => new() { Id = x.Id, DeviceRegistrationId = x.DeviceRegistrationId, StoreCode = x.StoreCode, DeviceCode = x.DeviceCode, ComputerName = x.ComputerName, RustdeskId = x.RustdeskId, ClientVersion = x.ClientVersion, AgentVersion = x.AgentVersion, OnlineStatus = x.LastSeenAtUtc is null ? RemoteMaintenanceOnlineStatuses.Never : x.LastSeenAtUtc >= threshold ? RemoteMaintenanceOnlineStatuses.Online : RemoteMaintenanceOnlineStatuses.Offline, ServiceStatus = x.ServiceStatus, LastSeenAtUtc = x.LastSeenAtUtc.HasValue ? EnsureUtc(x.LastSeenAtUtc.Value) : null, RegisteredAtUtc = EnsureUtc(x.RegisteredAtUtc), IsStale = x.LastSeenAtUtc.HasValue && x.LastSeenAtUtc < threshold };
    private static string GeneratePassword() => GenerateToken(24);
    private static string GenerateToken(int bytes = 32) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool FixedTimeEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    private static string? Normalize(string? value, int max) { var normalized = value?.Trim(); return string.IsNullOrWhiteSpace(normalized) ? null : normalized.Length <= max ? normalized : normalized[..max]; }
    internal static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime()
    };
}
