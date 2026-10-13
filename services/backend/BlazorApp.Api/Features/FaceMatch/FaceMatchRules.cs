using System.Globalization;
using BlazorApp.Api.Features.FaceCameras;
using BlazorApp.Api.Services.Attendance;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Features.FaceMatch;

/// <summary>一张已识别类型的上传图片（只认 jpeg / png，按字节头判定，不信客户端声明的类型）。</summary>
internal readonly record struct DetectedImage(string ContentType, string Extension);

/// <summary>字段规则、上传校验、门店时区与州判断：服务层与后台任务共用，全部是不依赖机器时区的纯函数。</summary>
internal static class FaceMatchRules
{
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public const int MaxConsentScanBytes = 10 * 1024 * 1024;
    public const int MinEnrollmentPhotos = 3;
    public const int MaxEnrollmentPhotos = 5;
    public const int MaxUnwelcomePhotos = 5;
    public const int MaxUnwelcomeLifetimeMonths = 12;
    public const int ExpiryReminderDays = 30;
    public const int NswNoticeMinDays = 14;
    public const int MaxStoreScopeStores = 20;

    /// <summary>腾讯云人员库编号（同一账号下的全局名字）。</summary>
    public const string StaffGroupId = "staff";

    public const string UnwelcomeGroupId = "unwelcome";

    public static string EnrollmentPersonId(string enrollmentId) => $"st_{enrollmentId}";

    public static string UnwelcomePersonId(string entryId) => $"uw_{entryId}";

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>按字节头判定 JPEG（FF D8 FF）/ PNG（89 50 4E 47 0D 0A 1A 0A）；其余返回 null。</summary>
    public static DetectedImage? DetectImage(ReadOnlySpan<byte> bytes)
    {
        if (FaceCaptureImageService.MatchesMagicBytes(bytes, "image/jpeg"))
        {
            return new DetectedImage("image/jpeg", "jpg");
        }

        if (FaceCaptureImageService.MatchesMagicBytes(bytes, "image/png"))
        {
            return new DetectedImage("image/png", "png");
        }

        return null;
    }

    /// <summary>同意书扫描件：jpeg / png / pdf（%PDF-）。</summary>
    public static DetectedImage? DetectDocument(ReadOnlySpan<byte> bytes)
    {
        var image = DetectImage(bytes);
        if (image != null)
        {
            return image;
        }

        ReadOnlySpan<byte> pdf = "%PDF-"u8;
        return bytes.Length >= pdf.Length && bytes[..pdf.Length].SequenceEqual(pdf)
            ? new DetectedImage("application/pdf", "pdf")
            : null;
    }

    /// <summary>可选文本：trim，空串视为 null；超长或含控制字符返回 false。</summary>
    public static bool TryNormalizeText(string? value, int maxLength, out string? normalized) =>
        FaceCameraRules.TryNormalizeOptionalText(value, maxLength, out normalized);

    /// <summary>yyyy-MM-dd → 仅日期部分的 DateTime（Kind 未指定）；空串返回 null；格式不对返回 false。</summary>
    public static bool TryParseDate(string? value, out DateTime? date)
    {
        date = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = parsed.Date;
        return true;
    }

    public static string? FormatDate(DateTime? date) =>
        date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? AsUtc(DateTime? value) => value.HasValue ? AsUtc(value.Value) : null;

    /// <summary>
    /// 门店实际时区：档案 → 门店表 → 州推导（NSW→Sydney、QLD→Brisbane）→ Australia/Sydney。
    /// 非法 / 不在白名单的时区视为未配置，不会抛异常。
    /// </summary>
    public static string ResolveTimeZoneId(string? profileTimeZoneId, string? storeTimeZoneId, string? address)
    {
        if (StoreTimeZonePolicy.TryNormalize(profileTimeZoneId, out var fromProfile) && fromProfile != null)
        {
            return fromProfile;
        }

        if (StoreTimeZonePolicy.TryNormalize(storeTimeZoneId, out var fromStore) && fromStore != null)
        {
            return fromStore;
        }

        return ResolveJurisdiction(null, null, address) switch
        {
            "QLD" => StoreTimeZonePolicy.Brisbane,
            _ => StoreTimeZonePolicy.Sydney,
        };
    }

    /// <summary>NSW / QLD / null；先认时区（档案优先于门店表），再按地址邮编。</summary>
    public static string? ResolveJurisdiction(string? profileTimeZoneId, string? storeTimeZoneId, string? address)
    {
        var timeZone = StoreTimeZonePolicy.TryNormalize(profileTimeZoneId, out var fromProfile) && fromProfile != null
            ? fromProfile
            : storeTimeZoneId;
        return PublicHolidaySyncHelper.ResolveStoreJurisdiction(timeZone, address);
    }

    public static TimeZoneInfo FindTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // 白名单里的时区在任何带 tzdata / ICU 的环境都存在；缺失时按悉尼固定偏移之外的最安全做法是明确失败。
            throw new FaceMatchException(500, "FACE_MATCH_TIMEZONE_UNAVAILABLE", "服务器缺少门店时区数据。");
        }
    }

    /// <summary>UTC 时刻在门店时区下的本地日期。</summary>
    public static DateTime LocalDate(DateTime utc, string timeZoneId) =>
        TimeZoneInfo.ConvertTimeFromUtc(AsUtc(utc), FindTimeZone(timeZoneId)).Date;

    /// <summary>门店本地某日 0 点对应的 UTC 时刻（夏令时切换日以该日首个有效时刻为准）。</summary>
    public static DateTime LocalMidnightToUtc(DateTime localDate, string timeZoneId)
    {
        var zone = FindTimeZone(timeZoneId);
        var local = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Unspecified);
        // 午夜落在夏令时空洞时往后推到第一个有效时刻。
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>门店编码列表（英文逗号分隔、无空格）↔ 列表。</summary>
    public static string JoinStoreScope(IEnumerable<string> storeCodes) => string.Join(",", storeCodes);

    public static IReadOnlyList<string> SplitStoreScope(string? scope) =>
        string.IsNullOrWhiteSpace(scope)
            ? []
            : scope.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string EntryStatusName(byte status) => status switch
    {
        FaceUnwelcomeEntryStatus.Pending => "pending",
        FaceUnwelcomeEntryStatus.Active => "active",
        FaceUnwelcomeEntryStatus.Expired => "expired",
        FaceUnwelcomeEntryStatus.Withdrawn => "withdrawn",
        _ => "unknown",
    };

    public static string CategoryName(byte category) => category switch
    {
        FaceIncidentCategory.ViolenceOrThreat => "violenceOrThreat",
        FaceIncidentCategory.OrganisedOrRepeatTheft => "organisedOrRepeatTheft",
        FaceIncidentCategory.Other => "other",
        _ => "unknown",
    };

    public static string ReviewStatusName(byte status) => status switch
    {
        FaceMatchReviewStatus.Pending => "pending",
        FaceMatchReviewStatus.Dismissed => "dismissed",
        FaceMatchReviewStatus.ConfirmedBySecondPerson => "confirmed",
        _ => "unknown",
    };

    /// <summary>JSON 数组形式的对象键列表（登记照 / 参考照片）；坏数据按空列表处理，不抛异常。</summary>
    public static IReadOnlyList<string> ParseKeys(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static string SerializeKeys(IEnumerable<string> keys) => System.Text.Json.JsonSerializer.Serialize(keys);
}

/// <summary>门店信息（门店表 + 档案）：时区与州的统一入口。</summary>
internal sealed record FaceMatchStoreInfo(string StoreCode, string StoreName, string? Address, string? TimeZoneId)
{
    public static async Task<FaceMatchStoreInfo?> FindAsync(ISqlSugarClient db, string storeCode, CancellationToken cancellationToken)
    {
        var store = await db.Queryable<Store>()
            .Where(s => s.StoreCode == storeCode && !s.IsDeleted)
            .FirstAsync(cancellationToken);
        return store == null ? null : new FaceMatchStoreInfo(store.StoreCode, store.StoreName, store.Address, store.TimeZoneId);
    }
}
