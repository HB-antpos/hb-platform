using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 经过完整校验的配置快照（不可变）。一个请求从头到尾只用同一份快照，
/// 热更新只会整份替换，不会出现「摄像机清单换了一半」的中间状态。
/// </summary>
internal sealed partial class FaceIngestSettings
{
    private readonly CameraRegistration[] _cameras;

    private FaceIngestSettings(FaceIngestOptions options, string storageRootPath, CameraRegistration[] cameras)
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

        settings = new FaceIngestSettings(options, storageRootPath, [.. cameras]);
        errors = [];
        return true;
    }

    /// <summary>
    /// 按 token 找摄像机（含停用的，由调用方决定是否放行）。
    /// 关键逻辑：先算 token 的 SHA-256，再用 <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// 与每台摄像机比对且不提前退出，避免时序侧信道暴露「哪台匹配 / 匹配到第几台」；30 台规模开销可忽略。
    /// </summary>
    public CameraRegistration? FindCameraByToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        Span<byte> incomingHash = stackalloc byte[SHA256.HashSizeInBytes];
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        try
        {
            SHA256.HashData(tokenBytes, incomingHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }

        CameraRegistration? matched = null;
        foreach (var camera in _cameras)
        {
            var equal = CryptographicOperations.FixedTimeEquals(incomingHash, camera.TokenSha256);
            matched = equal ? camera : matched;
        }

        return matched;
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
