using System.Security.Cryptography;
using System.Text;

namespace BlazorApp.FaceIngest;

/// <summary>
/// 一份不可变的摄像机清单快照，负责按 token 查摄像机。json 来源与数据库来源共用这一实现，
/// 鉴权口径完全一致（P0 的常量时间比较）。
/// </summary>
internal sealed class CameraRegistry
{
    private readonly CameraRegistration[] _cameras;

    public CameraRegistry(IEnumerable<CameraRegistration> cameras)
    {
        _cameras = [.. cameras];
    }

    /// <summary>空清单（所有请求都会 401）。</summary>
    public static CameraRegistry Empty { get; } = new([]);

    /// <summary>清单里的摄像机（含停用的）。</summary>
    public IReadOnlyList<CameraRegistration> Cameras => _cameras;

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
}

/// <summary>
/// 摄像机清单来源。选择规则（启动时定一次，不合并）：配置了 <c>FaceIngest:Database:ConnectionString</c> → 数据库 FaceCamera 表；
/// 否则 → cameras.json / 内存配置（P0 行为不变）。
/// </summary>
internal interface ICameraRegistrySource
{
    /// <summary>来源名，写进 /health：<c>json</c> | <c>database</c>。</summary>
    string SourceName { get; }

    /// <summary>
    /// 当前可用的清单；<c>null</c> 表示来源从未成功加载过（只会出现在数据库来源启动后、首次加载成功之前），
    /// 此时接收端点对所有请求回 503，让摄像机断网续传缓存后重推，而不是 401 丢数据。
    /// </summary>
    CameraRegistry? Current { get; }
}

/// <summary>
/// cameras.json / 内存配置来源：直接取 <see cref="FaceIngestSettingsMonitor"/> 的当前已校验快照，
/// 热更新、非法配置保留旧快照等行为与 P0 完全一致；永远「已就绪」。
/// </summary>
internal sealed class JsonFileCameraSource : ICameraRegistrySource
{
    public const string Name = "json";

    private readonly FaceIngestSettingsMonitor _settings;

    public JsonFileCameraSource(FaceIngestSettingsMonitor settings)
    {
        _settings = settings;
    }

    public string SourceName => Name;

    public CameraRegistry? Current => _settings.Current.Registry;
}
