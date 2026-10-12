using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 经过完整校验的配置快照（不可变）。一个请求从头到尾只用同一份快照，
/// 热更新只会整份替换，不会出现「摄像机清单换了一半」的中间状态。
/// </summary>
internal sealed partial class FaceIngestSettings
{
    private readonly CameraRegistration[] _cameras;

    private FaceIngestSettings(
        FaceIngestOptions options,
        string storageRootPath,
        CameraRegistration[] cameras,
        string? databaseConnectionString)
    {
        CaptureEnabled = options.CaptureEnabled;
        StorageRootPath = storageRootPath;
        MaxRequestBodyBytes = options.MaxRequestBodyBytes;
        MaxTotalBytes = options.MaxTotalBytes;
        MaxFileCount = options.MaxFileCount;
        MinFreeDiskBytes = options.MinFreeDiskBytes;
        RetentionDays = options.RetentionDays;
        CleanupIntervalMinutes = options.CleanupIntervalMinutes;
        _cameras = cameras;
        Registry = new CameraRegistry(cameras);
        ProcessingEnabled = options.ProcessingEnabled;
        ProcessingIntervalSeconds = options.ProcessingIntervalSeconds;
        ProcessingBatchSize = options.ProcessingBatchSize;
        ProcessingMaxAttempts = options.ProcessingMaxAttempts;
        DatabaseConnectionString = databaseConnectionString;
        CameraRefreshSeconds = options.Database.CameraRefreshSeconds;
        var cos = options.Cos;
        Cos = new CosSettings(
            cos.Enabled,
            cos.Bucket?.Trim() ?? string.Empty,
            cos.Region?.Trim() ?? string.Empty,
            cos.SecretId?.Trim() ?? string.Empty,
            cos.SecretKey?.Trim() ?? string.Empty,
            NormalizeKeyPrefix(cos.KeyPrefix),
            cos.UploadRawBody);
    }

    /// <summary>是否真正落盘。</summary>
    public bool CaptureEnabled { get; }

    /// <summary>原始报文根目录（已转成绝对路径）。</summary>
    public string StorageRootPath { get; }

    /// <summary>单个请求体上限（字节）。</summary>
    public long MaxRequestBodyBytes { get; }

    /// <summary>已存总字节上限。</summary>
    public long MaxTotalBytes { get; }

    /// <summary>已存文件数上限（.bin 与 .json 各算一个）。</summary>
    public long MaxFileCount { get; }

    /// <summary>最低剩余磁盘空间；0 表示不检查。</summary>
    public long MinFreeDiskBytes { get; }

    /// <summary>保留天数。</summary>
    public int RetentionDays { get; }

    /// <summary>清理周期（分钟）。</summary>
    public int CleanupIntervalMinutes { get; }

    /// <summary>已登记的摄像机（含停用的）。</summary>
    public IReadOnlyList<CameraRegistration> Cameras => _cameras;

    /// <summary>cameras.json / 内存配置里的摄像机清单（json 来源用；配置了数据库时不参与鉴权）。</summary>
    public CameraRegistry Registry { get; }

    /// <summary>是否运行后台处理器。</summary>
    public bool ProcessingEnabled { get; }

    /// <summary>处理器扫描周期（秒）。</summary>
    public int ProcessingIntervalSeconds { get; }

    /// <summary>每轮最多处理条数。</summary>
    public int ProcessingBatchSize { get; }

    /// <summary>单条最多尝试次数。</summary>
    public int ProcessingMaxAttempts { get; }

    /// <summary>数据库连接串（已校验格式）；null 表示不连库。只在启动时读取。</summary>
    internal string? DatabaseConnectionString { get; }

    /// <summary>是否配置了数据库。</summary>
    public bool DatabaseConfigured => DatabaseConnectionString is not null;

    /// <summary>摄像机表刷新周期（秒）。</summary>
    public int CameraRefreshSeconds { get; }

    /// <summary>COS 配置（已校验）。只在启动时读取。</summary>
    internal CosSettings Cos { get; }

    /// <summary>
    /// 校验并生成快照。任何一项不合法都返回 false 并给出全部错误；错误信息不含 token 哈希。
    /// </summary>
    public static bool TryCreate(
        FaceIngestOptions? options,
        [NotNullWhen(true)] out FaceIngestSettings? settings,
        out IReadOnlyList<string> errors)
    {
        settings = null;
        var problems = new List<string>();
        if (options is null)
        {
            errors = ["FaceIngest configuration section is missing."];
            return false;
        }

        string storageRootPath = string.Empty;
        if (string.IsNullOrWhiteSpace(options.StorageRootPath))
        {
            problems.Add("FaceIngest:StorageRootPath must not be empty.");
        }
        else
        {
            try
            {
                storageRootPath = Path.GetFullPath(options.StorageRootPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                problems.Add($"FaceIngest:StorageRootPath is not a valid path ({ex.GetType().Name}).");
            }
        }

        RequirePositive(problems, nameof(FaceIngestOptions.MaxRequestBodyBytes), options.MaxRequestBodyBytes);
        RequirePositive(problems, nameof(FaceIngestOptions.MaxTotalBytes), options.MaxTotalBytes);
        RequirePositive(problems, nameof(FaceIngestOptions.MaxFileCount), options.MaxFileCount);
        RequirePositive(problems, nameof(FaceIngestOptions.RetentionDays), options.RetentionDays);
        RequirePositive(problems, nameof(FaceIngestOptions.CleanupIntervalMinutes), options.CleanupIntervalMinutes);
        if (options.MinFreeDiskBytes < 0)
        {
            problems.Add("FaceIngest:MinFreeDiskBytes must be zero or positive.");
        }

        RequirePositive(problems, nameof(FaceIngestOptions.ProcessingIntervalSeconds), options.ProcessingIntervalSeconds);
        RequirePositive(problems, nameof(FaceIngestOptions.ProcessingBatchSize), options.ProcessingBatchSize);
        RequirePositive(problems, nameof(FaceIngestOptions.ProcessingMaxAttempts), options.ProcessingMaxAttempts);
        var databaseConnectionString = ValidateDatabase(problems, options.Database);
        ValidateCos(problems, options.Cos);

        var cameras = new List<CameraRegistration>();
        var seenCameraIds = new HashSet<string>(StringComparer.Ordinal);
        var seenTokenHashes = new HashSet<string>(StringComparer.Ordinal);
        var cameraOptions = options.Cameras ?? [];
        for (var index = 0; index < cameraOptions.Count; index++)
        {
            var camera = cameraOptions[index];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"FaceIngest:Cameras:{index}");
            if (camera is null)
            {
                problems.Add($"{prefix} is empty.");
                continue;
            }

            var valid = true;
            var cameraId = camera.CameraId ?? string.Empty;
            // 关键逻辑：CameraId 会拼进落盘路径，只允许小写字母数字与连字符，从根上杜绝 ../ 之类的路径穿越。
            if (!CameraIdPattern().IsMatch(cameraId))
            {
                problems.Add($"{prefix}:CameraId '{cameraId}' must match ^[a-z0-9][a-z0-9-]{{0,63}}$.");
                valid = false;
            }
            else if (!seenCameraIds.Add(cameraId))
            {
                problems.Add($"{prefix}:CameraId '{cameraId}' is duplicated.");
                valid = false;
            }

            var storeCode = camera.StoreCode ?? string.Empty;
            if (!StoreCodePattern().IsMatch(storeCode))
            {
                problems.Add($"{prefix}:StoreCode for camera '{cameraId}' must be 1-32 characters of [A-Za-z0-9_-].");
                valid = false;
            }

            // 中文注释：允许首尾空白（复制粘贴常带），其余必须是 64 位十六进制；错误信息里不回显哈希值。
            var tokenHash = camera.TokenSha256?.Trim() ?? string.Empty;
            byte[]? tokenHashBytes = null;
            if (!TokenHashPattern().IsMatch(tokenHash))
            {
                problems.Add($"{prefix}:TokenSha256 for camera '{cameraId}' must be 64 hexadecimal characters.");
                valid = false;
            }
            else if (!seenTokenHashes.Add(tokenHash.ToLowerInvariant()))
            {
                problems.Add($"{prefix}:TokenSha256 for camera '{cameraId}' duplicates another camera.");
                valid = false;
            }
            else
            {
                tokenHashBytes = Convert.FromHexString(tokenHash);
            }

            // 中文注释：缺省（null）视为启用；写了就必须是 true / false。JSON 里的 null 经配置系统变成空字符串，同样不合法。
            var enabled = true;
            if (camera.Enabled is not null && !bool.TryParse(camera.Enabled, out enabled))
            {
                problems.Add($"{prefix}:Enabled for camera '{cameraId}' must be true or false.");
                valid = false;
            }

            if (valid && tokenHashBytes is not null)
            {
                cameras.Add(new CameraRegistration(cameraId, storeCode, enabled, tokenHashBytes));
            }
        }

        if (problems.Count > 0)
        {
            errors = problems;
            return false;
        }

        settings = new FaceIngestSettings(options, storageRootPath, [.. cameras], databaseConnectionString);
        errors = [];
        return true;
    }

    /// <summary>按 token 找摄像机（含停用的，由调用方决定是否放行），常量时间比较，见 <see cref="CameraRegistry.FindCameraByToken"/>。</summary>
    public CameraRegistration? FindCameraByToken(string token) => Registry.FindCameraByToken(token);

    /// <summary>
    /// 校验数据库配置：连接串为空 = 不连库；非空必须是合法的 SQL Server 连接串且指定了服务器与库名（否则启动失败）。
    /// 关键逻辑：错误信息只给键名，绝不回显连接串或解析异常原文（里面可能带密码）。
    /// 连接串合法但连不上不算配置错误，由摄像机来源回 503 处理。
    /// </summary>
    private static string? ValidateDatabase(List<string> problems, FaceIngestDatabaseOptions? database)
    {
        database ??= new FaceIngestDatabaseOptions();
        RequirePositive(problems, "Database:CameraRefreshSeconds", database.CameraRefreshSeconds);
        var connectionString = database.ConnectionString?.Trim();
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog))
            {
                problems.Add("FaceIngest:Database:ConnectionString must specify both a server (Data Source) and a database (Initial Catalog).");
                return null;
            }

            // 中文注释：标上应用名，DBA 在会话列表里能一眼认出人脸服务的连接；运维显式写了就尊重原值。
            if (string.IsNullOrWhiteSpace(builder.ApplicationName) || builder.ApplicationName == "Core Microsoft SqlClient Data Provider")
            {
                builder.ApplicationName = "BlazorApp.FaceIngest";
            }

            return builder.ConnectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            problems.Add("FaceIngest:Database:ConnectionString is not a valid SQL Server connection string.");
            return null;
        }
    }

    /// <summary>
    /// 校验 COS 配置：Enabled=true 时 Bucket / Region / SecretId / SecretKey 缺一不可（启动失败，只报键名不报值）；
    /// 桶名、地域、前缀只允许安全字符，因为它们会拼进请求主机名与对象键。
    /// </summary>
    private static void ValidateCos(List<string> problems, FaceIngestCosOptions? cos)
    {
        if (cos is null || !cos.Enabled)
        {
            return;
        }

        foreach (var (name, value) in new[]
                 {
                     (nameof(FaceIngestCosOptions.Bucket), cos.Bucket),
                     (nameof(FaceIngestCosOptions.Region), cos.Region),
                     (nameof(FaceIngestCosOptions.SecretId), cos.SecretId),
                     (nameof(FaceIngestCosOptions.SecretKey), cos.SecretKey),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"FaceIngest:Cos:{name} is required when FaceIngest:Cos:Enabled is true.");
            }
        }

        if (!string.IsNullOrWhiteSpace(cos.Bucket) && !CosHostPartPattern().IsMatch(cos.Bucket.Trim()))
        {
            problems.Add("FaceIngest:Cos:Bucket must contain only lowercase letters, digits and hyphens.");
        }

        if (!string.IsNullOrWhiteSpace(cos.Region) && !CosHostPartPattern().IsMatch(cos.Region.Trim()))
        {
            problems.Add("FaceIngest:Cos:Region must contain only lowercase letters, digits and hyphens.");
        }

        if (!CosKeyPrefixPattern().IsMatch(NormalizeKeyPrefix(cos.KeyPrefix)))
        {
            problems.Add("FaceIngest:Cos:KeyPrefix must be 1-64 characters of [A-Za-z0-9_-] segments separated by '/'.");
        }
    }

    private static string NormalizeKeyPrefix(string? keyPrefix)
    {
        var trimmed = keyPrefix?.Trim().Trim('/') ?? string.Empty;
        return trimmed.Length == 0 ? FaceIngestCosOptions.DefaultKeyPrefix : trimmed;
    }

    private static void RequirePositive(List<string> problems, string name, long value)
    {
        if (value <= 0)
        {
            problems.Add($"FaceIngest:{name} must be positive.");
        }
    }

    // 中文注释：用 \z 而不是 $，因为 .NET 的 $ 允许末尾多一个 \n，会把带换行的 CameraId 放进路径。
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CameraIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex StoreCodePattern();

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenHashPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CosHostPartPattern();

    [GeneratedRegex(@"^(?=.{1,64}\z)[A-Za-z0-9_-]+(/[A-Za-z0-9_-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex CosKeyPrefixPattern();

    /// <summary>摄像机编号是否合法（DB 来源与处理器复用同一规则，保证能安全进文件路径）。</summary>
    internal static bool IsValidCameraId(string? cameraId) => cameraId is not null && CameraIdPattern().IsMatch(cameraId);

    /// <summary>门店编码是否合法。</summary>
    internal static bool IsValidStoreCode(string? storeCode) => storeCode is not null && StoreCodePattern().IsMatch(storeCode);

    /// <summary>token 哈希是否为 64 位十六进制（允许首尾空白由调用方先 Trim）。</summary>
    internal static bool IsValidTokenHash(string? tokenHash) => tokenHash is not null && TokenHashPattern().IsMatch(tokenHash);
}

/// <summary>已校验的 COS 配置。<see cref="Enabled"/> 为 true 时其余键保证非空。</summary>
internal sealed record CosSettings(
    bool Enabled,
    string Bucket,
    string Region,
    string SecretId,
    string SecretKey,
    string KeyPrefix,
    bool UploadRawBody)
{
    /// <summary>不在 ToString / 日志里输出密钥。</summary>
    public override string ToString()
        => $"CosSettings {{ Enabled = {Enabled}, Bucket = {Bucket}, Region = {Region}, KeyPrefix = {KeyPrefix}, UploadRawBody = {UploadRawBody} }}";
}

/// <summary>
/// 一台已登记摄像机的运行期视图。只持有 token 的哈希字节，不持有 token 原文。
/// </summary>
internal sealed class CameraRegistration
{
    public CameraRegistration(string cameraId, string storeCode, bool enabled, byte[] tokenSha256)
    {
        CameraId = cameraId;
        StoreCode = storeCode;
        Enabled = enabled;
        TokenSha256 = tokenSha256;
    }

    /// <summary>摄像机编号（已校验，可安全进路径）。</summary>
    public string CameraId { get; }

    /// <summary>门店编码。</summary>
    public string StoreCode { get; }

    /// <summary>是否启用。</summary>
    public bool Enabled { get; }

    /// <summary>token 的 SHA-256（32 字节）。</summary>
    internal byte[] TokenSha256 { get; }
}
