using BlazorApp.Api.Data;
using BlazorApp.Shared.Models;
using Microsoft.Data.SqlClient;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceCameras;

/// <summary>
/// 人脸抓拍摄像机后台管理：增、改（含启停）、轮换 token、查看最近抓拍元数据。没有删除，只能停用。
/// 人脸接收服务每 30 秒重读 FaceCamera 表，所以这里的改动最多 30 秒后在接收端生效。
///
/// token 安全约束（逐条都有测试）：
/// 1. 明文只存在于 Create / Rotate 的局部变量和一次性响应里，库里只写 SHA-256；
/// 2. 明文不进日志、不进异常消息、不进审计内容；唯一冲突时只记「哪个约束」，不记异常原文；
/// 3. 抓拍记录只返回元数据；图片元数据只在调用者有 FaceCapture.Images.View 时才查询和返回，
///    且查询里根本不取 ObjectKey 列，响应契约里也没有对象键 / 地址 / 哈希字段。
/// </summary>
public sealed class FaceCameraAdminService(
    SqlSugarContext context,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<FaceCameraAdminService> logger
) : IFaceCameraAdminService
{
    public const int OnlineWindowMinutes = 10;
    public const int StatsWindowHours = 24;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    /// <summary>offset 分页的页码上限：翻到 5000 条以前足够排查，更早的记录请查库，避免深翻页扫描。</summary>
    public const int MaxPage = 100;

    internal const string DefaultPublicBaseUrl = "https://hotbargain.vip";
    internal const string PublicBaseUrlConfigKey = "FaceCapture:PublicBaseUrl";

    // 极小概率的 token 哈希撞车：换一个新 token 重试，仍撞则报 409，不会死循环。
    private const int MaxTokenAttempts = 3;
    private const int StatsChunkSize = 500;
    private const byte ParseStatusUnparsed = 2;
    private const int MaxAuditTextLength = 120;

    private readonly ISqlSugarClient _db = context.Db;

    public async Task<FaceCameraListDto> ListAsync(CancellationToken cancellationToken = default)
    {
        var now = Now();
        var cameras = await _db.Queryable<FaceCamera>()
            .OrderBy(camera => camera.StoreCode)
            .OrderBy(camera => camera.CameraId)
            .ToListAsync(cancellationToken);
        return new FaceCameraListDto
        {
            Items = await BuildDtosAsync(cameras, now, cancellationToken),
            GeneratedAtUtc = now,
            OnlineWindowMinutes = OnlineWindowMinutes,
            StatsWindowHours = StatsWindowHours,
        };
    }

    public async Task<FaceCameraSecretDto> CreateAsync(
        FaceCameraCreateRequest request,
        FaceCameraActor actor,
        CancellationToken cancellationToken = default
    )
    {
        var cameraId = request.CameraId?.Trim();
        if (!FaceCameraRules.IsValidCameraId(cameraId))
        {
            throw new FaceCameraException(400, "FACE_CAMERA_ID_INVALID",
                "摄像机编号只能用小写字母、数字和横线，不能以横线开头，最长 64 位。");
        }

        var fields = ValidateFields(request.Note, request.DeviceModel, request.MacAddress);
        var storeCode = await ResolveStoreCodeAsync(request.StoreCode, cancellationToken);

        if (await _db.Queryable<FaceCamera>().AnyAsync(camera => camera.CameraId == cameraId, cancellationToken))
        {
            throw CameraIdExists();
        }

        var now = Now();
        var entity = new FaceCamera
        {
            CameraId = cameraId!,
            StoreCode = storeCode,
            Enabled = true,
            Note = fields.Note,
            DeviceModel = fields.DeviceModel,
            MacAddress = fields.MacAddress,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var token = await InsertWithFreshTokenAsync(entity, cancellationToken);
        Audit("create", actor, entity, $"model={entity.DeviceModel ?? "-"}");
        return await BuildSecretAsync(entity, token, now, cancellationToken);
    }

    public async Task<FaceCameraDto> UpdateAsync(
        string cameraId,
        FaceCameraUpdateRequest request,
        FaceCameraActor actor,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await GetEntityAsync(cameraId, cancellationToken);
        if (request.Enabled is not bool enabled)
        {
            throw FieldInvalid("缺少 enabled（是否启用）。");
        }

        var fields = ValidateFields(request.Note, request.DeviceModel, request.MacAddress);
        var storeCode = await ResolveStoreCodeAsync(request.StoreCode, cancellationToken);

        var changed = new List<string>();
        if (!string.Equals(entity.StoreCode, storeCode, StringComparison.Ordinal)) changed.Add($"storeCode:{entity.StoreCode}->{storeCode}");
        if (!string.Equals(entity.Note, fields.Note, StringComparison.Ordinal)) changed.Add("note");
        if (!string.Equals(entity.DeviceModel, fields.DeviceModel, StringComparison.Ordinal)) changed.Add("deviceModel");
        if (!string.Equals(entity.MacAddress, fields.MacAddress, StringComparison.Ordinal)) changed.Add("macAddress");
        var enabledChanged = entity.Enabled != enabled;

        var now = Now();
        entity.StoreCode = storeCode;
        entity.Note = fields.Note;
        entity.DeviceModel = fields.DeviceModel;
        entity.MacAddress = fields.MacAddress;
        entity.Enabled = enabled;
        entity.UpdatedAtUtc = now;

        // 只更新管理字段；TokenSha256 / LastSeenAtUtc 不在列表里，避免覆盖接收服务刚写入的最近推送时间。
        var rows = await _db.Updateable(entity)
            .UpdateColumns(camera => new
            {
                camera.StoreCode,
                camera.Note,
                camera.DeviceModel,
                camera.MacAddress,
                camera.Enabled,
                camera.UpdatedAtUtc,
            })
            .ExecuteCommandAsync(cancellationToken);
        if (rows == 0)
        {
            throw NotFound();
        }

        // 启停单独记一条，方便审计检索「谁停了哪台摄像机」；其余字段变化合并为 update。
        if (enabledChanged)
        {
            Audit(enabled ? "enable" : "disable", actor, entity, null);
        }

        if (changed.Count > 0 || !enabledChanged)
        {
            Audit("update", actor, entity, changed.Count == 0 ? "no-field-change" : string.Join(",", changed));
        }

        return (await BuildDtosAsync([entity], now, cancellationToken))[0];
    }

    public async Task<FaceCameraSecretDto> RotateTokenAsync(
        string cameraId,
        FaceCameraActor actor,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await GetEntityAsync(cameraId, cancellationToken);
        var now = Now();
        string? token = null;
        for (var attempt = 0; attempt < MaxTokenAttempts && token == null; attempt++)
        {
            var candidate = FaceCameraRules.GenerateToken();
            entity.TokenSha256 = FaceCameraRules.HashToken(candidate);
            entity.UpdatedAtUtc = now;
            try
            {
                var rows = await _db.Updateable(entity)
                    .UpdateColumns(camera => new { camera.TokenSha256, camera.UpdatedAtUtc })
                    .ExecuteCommandAsync(cancellationToken);
                if (rows == 0)
                {
                    throw NotFound();
                }

                token = candidate;
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // 只记「撞了哪个约束」，不记异常原文：原文里有被撞的哈希值。
                logger.LogWarning("人脸摄像机轮换 token 时哈希唯一约束冲突，重试。CameraId={CameraId}, Attempt={Attempt}",
                    entity.CameraId, attempt + 1);
            }
        }

        if (token == null)
        {
            throw TokenConflict();
        }

        Audit("rotate-token", actor, entity, null);
        return await BuildSecretAsync(entity, token, now, cancellationToken);
    }

    public async Task<FaceCapturePageDto> ListCapturesAsync(
        string cameraId,
        int page,
        int pageSize,
        bool includeImages = false,
        CancellationToken cancellationToken = default
    )
    {
        var entity = await GetEntityAsync(cameraId, cancellationToken);
        var size = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        var number = Math.Max(page, 1);
        if (number > MaxPage)
        {
            throw new FaceCameraException(400, "FACE_CAMERA_FIELD_INVALID", $"页码不能超过 {MaxPage}。");
        }

        // 排序与索引 IX_FaceCaptureEvent_Camera_Received（CameraId, ReceivedAtUtc DESC，隐含尾键 CaptureId 升序）一致，
        // 不需要额外排序；多取一行用来判断是否还有下一页，避免对大表做 COUNT。
        var rows = await _db.Queryable<FaceCaptureEvent>()
            .Where(item => item.CameraId == entity.CameraId)
            .OrderBy(item => item.ReceivedAtUtc, OrderByType.Desc)
            .OrderBy(item => item.CaptureId, OrderByType.Asc)
            .Skip((number - 1) * size)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var pageRows = rows.Take(size).ToList();
        // 图片元数据整页只查一次（IN 查询，不是每条记录各查一次）；没有权限时连查询都不发，
        // 调用方得到的是空数组 + CanViewImages=false，无法从元数据得知图片结构。
        var images = includeImages
            ? await QueryImageMetaAsync(pageRows.Select(row => row.CaptureId.TrimEnd()).ToList(), cancellationToken)
            : null;

        return new FaceCapturePageDto
        {
            Items = pageRows.Select(row =>
            {
                // char(32) 列在 SQL Server 上会补空格；真实 CaptureId 恰好 32 位，这里仍去掉尾部空白以防历史数据。
                var captureId = row.CaptureId.TrimEnd();
                return new FaceCaptureItemDto
                {
                    CaptureId = captureId,
                    ReceivedAtUtc = AsUtc(row.ReceivedAtUtc),
                    CameraEventTime = row.CameraEventTime,
                    EventType = row.EventType,
                    FaceCount = row.FaceCount,
                    ParseStatus = row.ParseStatus,
                    ParseNote = row.ParseNote,
                    IsDuplicate = !string.IsNullOrEmpty(row.DuplicateOfCaptureId),
                    Images = images != null && images.TryGetValue(captureId, out var list) ? list : [],
                };
            }).ToList(),
            Page = number,
            PageSize = size,
            HasMore = rows.Count > size,
            CanViewImages = includeImages,
        };
    }

    // ---------- 内部 ----------

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;

    private async Task<FaceCamera> GetEntityAsync(string cameraId, CancellationToken cancellationToken)
    {
        var id = cameraId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            throw NotFound();
        }

        return await _db.Queryable<FaceCamera>().FirstAsync(camera => camera.CameraId == id, cancellationToken)
            ?? throw NotFound();
    }

    private static (string? Note, string? DeviceModel, string? MacAddress) ValidateFields(
        string? note, string? deviceModel, string? macAddress)
    {
        if (!FaceCameraRules.TryNormalizeOptionalText(note, FaceCameraRules.MaxNoteLength, out var normalizedNote))
        {
            throw FieldInvalid($"备注最长 {FaceCameraRules.MaxNoteLength} 个字符，且不能含控制字符。");
        }

        if (!FaceCameraRules.TryNormalizeOptionalText(deviceModel, FaceCameraRules.MaxDeviceModelLength, out var normalizedModel))
        {
            throw FieldInvalid($"设备型号最长 {FaceCameraRules.MaxDeviceModelLength} 个字符，且不能含控制字符。");
        }

        if (!FaceCameraRules.TryNormalizeOptionalText(macAddress, FaceCameraRules.MaxMacAddressLength, out var normalizedMac))
        {
            throw FieldInvalid($"MAC 地址最长 {FaceCameraRules.MaxMacAddressLength} 个字符，且不能含控制字符。");
        }

        return (normalizedNote, normalizedModel, normalizedMac);
    }

    /// <summary>门店编码必须是 Store 表里真实存在且未删除的门店；入库用门店表里的规范写法。</summary>
    private async Task<string> ResolveStoreCodeAsync(string? storeCode, CancellationToken cancellationToken)
    {
        var code = storeCode?.Trim();
        if (!FaceCameraRules.IsValidStoreCode(code))
        {
            throw new FaceCameraException(400, "FACE_CAMERA_STORE_CODE_INVALID",
                "门店编码不能为空，最长 32 位，只能用字母、数字、下划线和横线。");
        }

        var store = await _db.Queryable<Store>()
            .Where(item => item.StoreCode == code && !item.IsDeleted)
            .FirstAsync(cancellationToken);
        if (store == null)
        {
            throw new FaceCameraException(400, "FACE_CAMERA_STORE_NOT_FOUND", "门店不存在或已删除。");
        }

        // 门店表允许更长、更宽的编码；人脸接收服务只认 32 位以内的字母数字下划线横线。
        if (!FaceCameraRules.IsValidStoreCode(store.StoreCode))
        {
            throw new FaceCameraException(400, "FACE_CAMERA_STORE_CODE_INVALID",
                "该门店的编码不符合人脸接收服务要求（最长 32 位，只能用字母、数字、下划线和横线）。");
        }

        return store.StoreCode;
    }

    private async Task<string> InsertWithFreshTokenAsync(FaceCamera entity, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxTokenAttempts; attempt++)
        {
            var token = FaceCameraRules.GenerateToken();
            entity.TokenSha256 = FaceCameraRules.HashToken(token);
            try
            {
                await _db.Insertable(entity).ExecuteCommandAsync(cancellationToken);
                return token;
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // 主键冲突 = 并发创建了同名摄像机（409）；哈希冲突 = 换 token 重试。
                if (!IsTokenIndexViolation(ex))
                {
                    throw CameraIdExists();
                }

                logger.LogWarning("人脸摄像机新增时 token 哈希唯一约束冲突，重试。CameraId={CameraId}, Attempt={Attempt}",
                    entity.CameraId, attempt + 1);
            }
        }

        throw TokenConflict();
    }

    private async Task<FaceCameraSecretDto> BuildSecretAsync(
        FaceCamera entity, string token, DateTime now, CancellationToken cancellationToken)
    {
        var camera = (await BuildDtosAsync([entity], now, cancellationToken))[0];
        return new FaceCameraSecretDto
        {
            Camera = camera,
            Token = token,
            CameraUrl = FaceCameraRules.BuildCameraUrl(ResolvePublicBaseUrl(), token),
        };
    }

    private string ResolvePublicBaseUrl()
    {
        var configured = configuration[PublicBaseUrlConfigKey]?.Trim();
        return Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.Query)
            && uri.AbsolutePath == "/"
            ? uri.GetLeftPart(UriPartial.Authority)
            : DefaultPublicBaseUrl;
    }

    /// <summary>
    /// 把摄像机行补上门店名、在线状态与近 24 小时统计。统计按摄像机集合一次聚合，不对每台摄像机各查一次。
    /// </summary>
    private async Task<IReadOnlyList<FaceCameraDto>> BuildDtosAsync(
        IReadOnlyList<FaceCamera> cameras, DateTime now, CancellationToken cancellationToken)
    {
        if (cameras.Count == 0)
        {
            return [];
        }

        var storeCodes = cameras.Select(camera => camera.StoreCode).Distinct(StringComparer.Ordinal).ToList();
        var stores = await _db.Queryable<Store>()
            .Where(store => storeCodes.Contains(store.StoreCode) && !store.IsDeleted)
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync(cancellationToken);
        var storeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in stores)
        {
            storeNames.TryAdd(store.StoreCode, store.StoreName);
        }

        var stats = await QueryStatsAsync(cameras.Select(camera => camera.CameraId).ToList(), now, cancellationToken);
        var onlineSince = now.AddMinutes(-OnlineWindowMinutes);
        return cameras.Select(camera =>
        {
            stats.TryGetValue(camera.CameraId, out var stat);
            var lastSeen = camera.LastSeenAtUtc;
            return new FaceCameraDto
            {
                CameraId = camera.CameraId,
                StoreCode = camera.StoreCode,
                StoreName = storeNames.GetValueOrDefault(camera.StoreCode),
                DeviceModel = camera.DeviceModel,
                MacAddress = camera.MacAddress,
                Note = camera.Note,
                Enabled = camera.Enabled,
                CreatedAtUtc = AsUtc(camera.CreatedAtUtc),
                UpdatedAtUtc = AsUtc(camera.UpdatedAtUtc),
                LastSeenAtUtc = AsUtc(lastSeen),
                // 在线判定：启用 且 最近推送时间 >= now-10 分钟（恰好 10 分钟算在线；时钟偏差导致的未来时间也算在线）。
                Online = camera.Enabled && lastSeen.HasValue && lastSeen.Value >= onlineSince,
                CaptureCount24h = stat?.CaptureCount ?? 0,
                FaceCount24h = stat?.FaceCount ?? 0,
                ParseFailureCount24h = stat?.ParseFailureCount ?? 0,
                LastEventAtUtc = AsUtc(stat?.LastEventAtUtc),
            };
        }).ToList();
    }

    /// <summary>
    /// 一页抓拍的图片元数据：一条 IN 查询（命中 IX_FaceCaptureImage_CaptureId），不取 ObjectKey / Sha256 / 属性 JSON。
    /// CaptureId 是 char(32)：参数显式声明为定长 ANSI，避免 SQL Server 按 nvarchar 做隐式转换而放弃索引查找。
    /// </summary>
    private async Task<Dictionary<string, List<FaceCaptureImageMetaDto>>> QueryImageMetaAsync(
        IReadOnlyList<string> captureIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<FaceCaptureImageMetaDto>>(StringComparer.OrdinalIgnoreCase);
        if (captureIds.Count == 0)
        {
            return result;
        }

        var names = captureIds.Select((_, index) => $"@c{index}").ToList();
        var sql = $"""
SELECT i.ImageId AS ImageId, i.CaptureId AS CaptureId, i.Kind AS Kind, i.PartIndex AS PartIndex,
       i.FaceIndex AS FaceIndex, i.Storage AS Storage, i.ByteSize AS ByteSize, i.Width AS Width, i.Height AS Height,
       i.RectX AS RectX, i.RectY AS RectY, i.RectW AS RectW, i.RectH AS RectH,
       i.RectNormalized AS RectNormalized, i.FaceScore AS FaceScore
FROM FaceCaptureImage i
WHERE i.CaptureId IN ({string.Join(",", names)})
ORDER BY i.CaptureId, i.PartIndex
""";
        var parameters = captureIds
            .Select((id, index) => new SugarParameter($"@c{index}", id, System.Data.DbType.AnsiStringFixedLength) { Size = 32 })
            .ToList();
        var rows = await _db.Ado.SqlQueryAsync<FaceImageMetaRow>(sql, parameters);
        foreach (var row in rows)
        {
            var captureId = row.CaptureId.TrimEnd();
            if (!result.TryGetValue(captureId, out var list))
            {
                result[captureId] = list = [];
            }

            list.Add(new FaceCaptureImageMetaDto
            {
                ImageId = row.ImageId.TrimEnd(),
                Kind = row.Kind,
                Storage = (row.Storage ?? string.Empty).Trim().ToLowerInvariant(),
                Width = row.Width,
                Height = row.Height,
                ByteSize = row.ByteSize,
                FaceIndex = row.FaceIndex,
                Rect = row.RectX.HasValue && row.RectY.HasValue && row.RectW.HasValue && row.RectH.HasValue
                    ? new FaceCaptureImageRectDto(row.RectX.Value, row.RectY.Value, row.RectW.Value, row.RectH.Value)
                    : null,
                RectNormalized = row.RectNormalized,
                FaceScore = row.FaceScore,
            });
        }

        return result;
    }

    /// <summary>
    /// 统计 SQL（SQL Server 与 SQLite 通用，全部参数化）：
    /// - 近 24 小时聚合：CameraId IN (...) AND ReceivedAtUtc >= @since，命中 IX_FaceCaptureEvent_Camera_Received 做区间查找；
    /// - 最近一次事件：相关子查询 MAX(ReceivedAtUtc) WHERE CameraId = c.CameraId，同一索引上退化为 TOP 1 查找，不扫全表；
    /// - 人脸数排除 DuplicateOfCaptureId 不为空的重复报文，解析失败数为 ParseStatus = 2。
    /// </summary>
    private async Task<Dictionary<string, FaceCameraStatsRow>> QueryStatsAsync(
        IReadOnlyList<string> cameraIds, DateTime now, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FaceCameraStatsRow>(StringComparer.OrdinalIgnoreCase);
        var since = now.AddHours(-StatsWindowHours);
        foreach (var chunk in cameraIds.Chunk(StatsChunkSize))
        {
            var names = chunk.Select((_, index) => $"@c{index}").ToList();
            var inList = string.Join(",", names);
            var sql = $"""
SELECT c.CameraId AS CameraId,
       a.CaptureCount AS CaptureCount,
       a.FaceCount AS FaceCount,
       a.ParseFailureCount AS ParseFailureCount,
       (SELECT MAX(l.ReceivedAtUtc) FROM FaceCaptureEvent l WHERE l.CameraId = c.CameraId) AS LastEventAtUtc
FROM FaceCamera c
LEFT JOIN (
    SELECT e.CameraId AS CameraId,
           COUNT(*) AS CaptureCount,
           SUM(CASE WHEN e.DuplicateOfCaptureId IS NULL THEN e.FaceCount ELSE 0 END) AS FaceCount,
           SUM(CASE WHEN e.ParseStatus = {ParseStatusUnparsed} THEN 1 ELSE 0 END) AS ParseFailureCount
    FROM FaceCaptureEvent e
    WHERE e.CameraId IN ({inList}) AND e.ReceivedAtUtc >= @since
    GROUP BY e.CameraId
) a ON a.CameraId = c.CameraId
WHERE c.CameraId IN ({inList})
""";
            var parameters = chunk
                .Select((id, index) => new SugarParameter($"@c{index}", id))
                .Append(new SugarParameter("@since", since))
                .ToList();
            var rows = await _db.Ado.SqlQueryAsync<FaceCameraStatsRow>(sql, parameters);
            foreach (var row in rows)
            {
                result[row.CameraId] = row;
            }
        }

        return result;
    }

    /// <summary>写操作审计：谁（用户 Id + 用户名）在什么时候对哪台摄像机做了什么；内容里绝不含 token 或其哈希。</summary>
    private void Audit(string action, FaceCameraActor actor, FaceCamera camera, string? detail)
    {
        logger.LogInformation(
            "人脸摄像机后台审计: Action={Action}, CameraId={CameraId}, StoreCode={StoreCode}, ActorId={ActorId}, ActorName={ActorName}, AtUtc={AtUtc:O}, Detail={Detail}",
            action,
            camera.CameraId,
            camera.StoreCode,
            SanitizeForLog(actor.Id),
            SanitizeForLog(actor.Name),
            Now(),
            detail ?? "-");
    }

    private static string SanitizeForLog(string? value)
    {
        var text = new string((value ?? string.Empty).Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (text.Length == 0) return "unknown";
        return text.Length > MaxAuditTextLength ? text[..MaxAuditTextLength] : text;
    }

    /// <summary>
    /// 取异常链里真正的数据库驱动异常。SqlSugar 会把驱动异常包一层，包装消息里带完整 SQL 文本
    /// （含 TokenSha256 列名），按包装消息判断约束名会误判，所以只看驱动异常本身。
    /// </summary>
    private static Exception? FindProviderException(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SqlException || current.GetType().Name == "SqliteException")
            {
                return current;
            }
        }

        return null;
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        var provider = FindProviderException(exception);
        // SQL Server：2627 违反唯一约束 / 主键，2601 违反唯一索引；SQLite（测试库）：UNIQUE constraint failed。
        return provider is SqlException { Number: 2627 or 2601 }
            || (provider != null && provider is not SqlException
                && provider.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>哈希唯一索引冲突（UX_FaceCamera_TokenSha256 / SQLite 的 FaceCamera.TokenSha256），区别于主键 CameraId 冲突。</summary>
    private static bool IsTokenIndexViolation(Exception exception) =>
        FindProviderException(exception)?.Message.Contains("TokenSha256", StringComparison.OrdinalIgnoreCase) == true;

    private static FaceCameraException NotFound() =>
        new(404, "FACE_CAMERA_NOT_FOUND", "摄像机不存在。");

    private static FaceCameraException CameraIdExists() =>
        new(409, "FACE_CAMERA_ID_EXISTS", "摄像机编号已存在。");

    private static FaceCameraException TokenConflict() =>
        new(409, "FACE_CAMERA_TOKEN_CONFLICT", "生成 token 时发生冲突，请稍后重试。");

    private static FaceCameraException FieldInvalid(string message) =>
        new(400, "FACE_CAMERA_FIELD_INVALID", message);

    private sealed class FaceImageMetaRow
    {
        public string ImageId { get; set; } = string.Empty;
        public string CaptureId { get; set; } = string.Empty;
        public int Kind { get; set; }
        public int PartIndex { get; set; }
        public int? FaceIndex { get; set; }
        public string? Storage { get; set; }
        public int ByteSize { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public decimal? RectX { get; set; }
        public decimal? RectY { get; set; }
        public decimal? RectW { get; set; }
        public decimal? RectH { get; set; }
        public bool? RectNormalized { get; set; }
        public decimal? FaceScore { get; set; }
    }

    private sealed class FaceCameraStatsRow
    {
        public string CameraId { get; set; } = string.Empty;
        public int? CaptureCount { get; set; }
        public int? FaceCount { get; set; }
        public int? ParseFailureCount { get; set; }
        public DateTime? LastEventAtUtc { get; set; }
    }
}
