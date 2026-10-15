using System.Globalization;
using System.Text.RegularExpressions;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 人脸比对（节 <c>FaceMatch</c>）的已校验配置快照。业务口径（设计第 13.2、13.5、15 节）：
/// - 腾讯云凭据只认 <c>FaceMatch:Tencent:SecretId/SecretKey</c>（compose 里由 FACE_MATCH_TENCENT_SECRET_ID/KEY 映射），
///   缺失时匹配整体关闭；<b>绝不</b>回退到 COS 密钥或任何别的密钥；
/// - 地域取 <c>FaceMatch:Tencent:Region</c>（出厂 appsettings 默认 ap-guangzhou），空或不在白名单时匹配整体关闭，
///   并记明确原因，<b>绝不</b>静默回退到别的地域；
/// - 任何一项配置非法都只关闭匹配（不让接收端点启动失败：接收落盘是 P0 的底线，不能被比对配置拖垮）；
/// - 只在启动时读取一次，修改后需重启容器（与 COS 配置一致）。
/// 本类不打印、不暴露任何密钥内容。
/// </summary>
internal sealed partial class FaceMatchSettings
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "FaceMatch";

    /// <summary>允许的腾讯云地域白名单（设计第 13.2 节）。</summary>
    public static readonly IReadOnlyList<string> AllowedRegions = ["ap-guangzhou", "ap-shanghai", "ap-singapore"];

    // 偏严的出厂阈值（设计第 5、14.3 节：试点用内部人员标定后再调；名单阈值须明显高于员工与去重）。
    // 腾讯云文档：一般 80 分左右适用大部分场景、建议不超过 90；这里员工 / 访客取 85，名单取 95。
    public const double DefaultStaffThreshold = 85;
    public const double DefaultVisitorThreshold = 85;
    public const double DefaultUnwelcomeThreshold = 95;

    private readonly Dictionary<string, StoreThresholds> _storeThresholds;

    private FaceMatchSettings()
    {
        _storeThresholds = new Dictionary<string, StoreThresholds>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>匹配整体是否可用：地域合法、凭据齐全、其余配置合法。为 false 时不会发出任何腾讯云请求。</summary>
    public bool MatcherEnabled { get; private set; }

    /// <summary>不可用的原因（只含键名与结论，不含任何值）；可用时为 null。</summary>
    public string? DisabledReason { get; private set; }

    /// <summary>
    /// 是否配置了任一腾讯云凭据。它是「档案闸门」的总开关：完全没配凭据 = 功能未启用，处理器保持 P1 现状；
    /// 一旦配了（即使不完整），没有档案的门店按 Off 处理（设计第 13.5 节）。
    /// </summary>
    public bool CredentialsConfigured { get; private set; }

    /// <summary>已配置且在白名单内的地域名；非法或缺失为空串（不回显非法值）。</summary>
    public string Region { get; private set; } = string.Empty;

    internal string SecretId { get; private set; } = string.Empty;

    internal string SecretKey { get; private set; } = string.Empty;

    public double StaffThreshold { get; private set; } = DefaultStaffThreshold;

    public double VisitorThreshold { get; private set; } = DefaultVisitorThreshold;

    public double UnwelcomeThreshold { get; private set; } = DefaultUnwelcomeThreshold;

    /// <summary>员工命中后同摄像机内相邻抓拍直接计员工、不再搜索的秒数（设计第 5 节起步 30）。</summary>
    public int StaffSuppressSeconds { get; private set; } = 30;

    /// <summary>同摄像机相同请求体哈希视为重复推送的窗口（秒），窗口内不重复搜索也不重复计数。</summary>
    public int DuplicateBodyWindowSeconds { get; private set; } = 300;

    /// <summary>质量闸门：人脸图最小边长（像素）。同时作为腾讯云 MinFaceSize（官方建议 80）。</summary>
    public int MinFaceEdgePixels { get; private set; } = 80;

    /// <summary>质量闸门：人脸图最小体积（字节），低于此值多半是空白 / 损坏图。</summary>
    public int MinFaceBytes { get; private set; } = 4096;

    /// <summary>档案刷新周期（秒），设计第 13.5 节为 30。</summary>
    public int ProfileRefreshSeconds { get; private set; } = 30;

    /// <summary>单次腾讯云调用的最大尝试次数（含首次）。</summary>
    public int ApiMaxAttempts { get; private set; } = 3;

    /// <summary>退避基数（毫秒）；第 n 次失败后等待 min(基数 × 2^(n-1), 上限)，限频再 ×4。</summary>
    public int ApiRetryBaseDelayMilliseconds { get; private set; } = 500;

    /// <summary>退避上限（毫秒）。</summary>
    public int ApiRetryMaxDelayMilliseconds { get; private set; } = 8000;

    /// <summary>账号级致命错误（认证失败、欠费、服务未开通）后暂停调用腾讯云的秒数，避免每条抓拍都去撞同一个错误。</summary>
    public int FatalCooldownSeconds { get; private set; } = 60;

    /// <summary>
    /// 旧 Match「匹配后丢弃」的单条最多尝试次数。2026-10-16 起 Match 走 Capture 入库（重试按 ProcessingMaxAttempts），本项不再使用，仅保留配置兼容。
    /// </summary>
    public int MatchMaxAttempts { get; private set; } = 3;

    /// <summary>旧 Match 报文本地最长滞留分钟数。2026-10-16 起不再使用（Match 与 Capture 同一留存口径），仅保留配置兼容。</summary>
    public int MatchMaxLocalAgeMinutes { get; private set; } = 15;

    /// <summary>名单命中任务的人脸图保留天数（设计第 14.3 节为 7 天）。</summary>
    public int UnwelcomeImageRetentionDays { get; private set; } = 7;

    /// <summary>取某门店的阈值：先看 <c>FaceMatch:Stores:{storeCode}:*</c> 覆盖，没有则用全局值。</summary>
    public StoreThresholds ThresholdsFor(string storeCode)
        => _storeThresholds.TryGetValue(storeCode, out var overrides)
            ? overrides
            : new StoreThresholds(StaffThreshold, VisitorThreshold, UnwelcomeThreshold);

    /// <summary>
    /// 从配置读取并校验。任何问题都只关闭匹配，原因汇总进 <see cref="DisabledReason"/>（只含键名）。
    /// 读取的是原始字符串再手工解析：ConfigurationBinder 遇到坏值会抛异常，那样会拖垮启动。
    /// </summary>
    public static FaceMatchSettings Create(IConfiguration configuration)
    {
        var settings = new FaceMatchSettings();
        var problems = new List<string>();

        var section = configuration.GetSection(SectionName);
        var secretId = section["Tencent:SecretId"]?.Trim() ?? string.Empty;
        var secretKey = section["Tencent:SecretKey"]?.Trim() ?? string.Empty;
        settings.CredentialsConfigured = secretId.Length > 0 || secretKey.Length > 0;

        // 地域：精确匹配白名单，不做大小写 / 别名归一化，更不回退默认值（未配置 = 关闭）。
        var region = section["Tencent:Region"]?.Trim() ?? string.Empty;
        if (region.Length == 0)
        {
            problems.Add("FaceMatch:Tencent:Region is missing");
        }
        else if (!AllowedRegions.Contains(region, StringComparer.Ordinal))
        {
            problems.Add("FaceMatch:Tencent:Region is not in the allowed list (" + string.Join(", ", AllowedRegions) + ")");
        }
        else
        {
            settings.Region = region;
        }

        if (secretId.Length == 0)
        {
            problems.Add("FaceMatch:Tencent:SecretId is missing");
        }
        else if (!SecretIdPattern().IsMatch(secretId))
        {
            problems.Add("FaceMatch:Tencent:SecretId has an invalid format");
        }

        if (secretKey.Length == 0)
        {
            problems.Add("FaceMatch:Tencent:SecretKey is missing");
        }
        else if (!SecretKeyPattern().IsMatch(secretKey))
        {
            problems.Add("FaceMatch:Tencent:SecretKey has an invalid format");
        }

        settings.StaffThreshold = ReadDouble(section, "StaffThreshold", DefaultStaffThreshold, problems);
        settings.VisitorThreshold = ReadDouble(section, "VisitorThreshold", DefaultVisitorThreshold, problems);
        settings.UnwelcomeThreshold = ReadDouble(section, "Unwelcome:Threshold", DefaultUnwelcomeThreshold, problems);
        CheckThresholdOrder("FaceMatch", settings.StaffThreshold, settings.VisitorThreshold, settings.UnwelcomeThreshold, problems);

        settings.StaffSuppressSeconds = ReadInt(section, "StaffSuppressSeconds", 30, 0, 3600, problems);
        settings.DuplicateBodyWindowSeconds = ReadInt(section, "DuplicateBodyWindowSeconds", 300, 0, 86400, problems);
        settings.MinFaceEdgePixels = ReadInt(section, "Quality:MinEdgePixels", 80, 64, 4000, problems);
        settings.MinFaceBytes = ReadInt(section, "Quality:MinBytes", 4096, 1, 5_000_000, problems);
        settings.ProfileRefreshSeconds = ReadInt(section, "ProfileRefreshSeconds", 30, 1, 3600, problems);
        settings.ApiMaxAttempts = ReadInt(section, "Retry:MaxAttempts", 3, 1, 10, problems);
        settings.ApiRetryBaseDelayMilliseconds = ReadInt(section, "Retry:BaseDelayMilliseconds", 500, 1, 60_000, problems);
        settings.ApiRetryMaxDelayMilliseconds = ReadInt(section, "Retry:MaxDelayMilliseconds", 8000, 1, 120_000, problems);
        settings.FatalCooldownSeconds = ReadInt(section, "FatalCooldownSeconds", 60, 0, 3600, problems);
        settings.MatchMaxAttempts = ReadInt(section, "MatchMaxAttempts", 3, 1, 10, problems);
        settings.MatchMaxLocalAgeMinutes = ReadInt(section, "MatchMaxLocalAgeMinutes", 15, 1, 1440, problems);
        settings.UnwelcomeImageRetentionDays = ReadInt(section, "Unwelcome:ImageRetentionDays", 7, 1, 30, problems);

        // 按门店覆盖阈值：FaceMatch:Stores:{storeCode}:StaffThreshold / VisitorThreshold / UnwelcomeThreshold。
        foreach (var storeSection in section.GetSection("Stores").GetChildren())
        {
            var storeKey = storeSection.Key;
            if (!FaceIngestSettings.IsValidStoreCode(storeKey))
            {
                problems.Add("FaceMatch:Stores contains an invalid store code key");
                continue;
            }

            var staff = ReadDouble(storeSection, "StaffThreshold", settings.StaffThreshold, problems, "FaceMatch:Stores:*:");
            var visitor = ReadDouble(storeSection, "VisitorThreshold", settings.VisitorThreshold, problems, "FaceMatch:Stores:*:");
            var unwelcome = ReadDouble(storeSection, "UnwelcomeThreshold", settings.UnwelcomeThreshold, problems, "FaceMatch:Stores:*:");
            CheckThresholdOrder("FaceMatch:Stores:*", staff, visitor, unwelcome, problems);
            settings._storeThresholds[storeKey] = new StoreThresholds(staff, visitor, unwelcome);
        }

        if (problems.Count == 0)
        {
            settings.MatcherEnabled = true;
            settings.SecretId = secretId;
            settings.SecretKey = secretKey;
        }
        else
        {
            // 关键逻辑：不可用时连密钥也不保留在快照里，下游即使误调用也拿不到可用凭据。
            settings.MatcherEnabled = false;
            settings.DisabledReason = string.Join("; ", problems);
        }

        return settings;
    }

    /// <summary>名单阈值必须不低于员工与访客阈值（设计第 14.3 节：名单阈值明显更高）。</summary>
    private static void CheckThresholdOrder(string label, double staff, double visitor, double unwelcome, List<string> problems)
    {
        if (unwelcome < Math.Max(staff, visitor))
        {
            problems.Add(label + " unwelcome threshold must not be lower than the staff or visitor threshold");
        }
    }

    private static double ReadDouble(IConfiguration section, string key, double defaultValue, List<string> problems, string labelPrefix = "FaceMatch:")
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        // 阈值是相似度分数，取值范围 (0, 100]；腾讯云 FaceMatchThreshold 的范围是 [0, 100)。
        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value) || value <= 0 || value > 100)
        {
            problems.Add(labelPrefix + key + " must be a number in (0, 100]");
            return defaultValue;
        }

        return value;
    }

    private static int ReadInt(IConfiguration section, string key, int defaultValue, int min, int max, List<string> problems)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"FaceMatch:{key} must be an integer in [{min}, {max}]"));
            return defaultValue;
        }

        return value;
    }

    // SecretId 进 Authorization 头，只允许字母数字与 _-，防止头注入；SecretKey 不得含空白 / 控制字符。
    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretIdPattern();

    [GeneratedRegex(@"^[\x21-\x7E]{8,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyPattern();
}

/// <summary>某门店生效的三个相似度阈值（0–100）。</summary>
internal readonly record struct StoreThresholds(double Staff, double Visitor, double Unwelcome);
