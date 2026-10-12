namespace BlazorApp.FaceIngest;

/// <summary>
/// 人脸抓拍原始报文接收服务（P0）的配置，节名 <c>FaceIngest</c>。
/// 业务口径：只抓门店海康摄像机推来的原始报文用于现场调研，不解析、不入库；
/// 摄像机 token 只存 SHA-256，原文不进配置。运行期统一经 <see cref="FaceIngestSettingsMonitor"/>
/// 读取「已校验快照」，不直接读本类，避免热更新成非法配置后影响接收端点。
/// </summary>
internal sealed class FaceIngestOptions
{
    /// <summary>配置节名。</summary>
    public const string SectionName = "FaceIngest";

    /// <summary>cameras.json 所在目录的配置键（环境变量写作 FaceIngest__ConfigDirectory）。</summary>
    public const string ConfigDirectoryKey = SectionName + ":" + nameof(ConfigDirectory);

    /// <summary>容器内 cameras.json 的默认目录（compose 把宿主机目录只读挂到这里）。</summary>
    public const string DefaultConfigDirectory = "/app/config";

    /// <summary>摄像机清单文件名，形状为 <c>{ "FaceIngest": { "Cameras": [ ... ] } }</c>。</summary>
    public const string CamerasFileName = "cameras.json";

    /// <summary>单个请求体默认上限 16MB（与 Nginx client_max_body_size 16m 对齐）。</summary>
    public const long DefaultMaxRequestBodyBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 是否真正落盘。关闭时鉴权通过的请求只读掉请求体后回 200，不写任何文件。
    /// 默认关闭，试点门店确认后再打开。
    /// </summary>
    public bool CaptureEnabled { get; set; }

    /// <summary>原始报文根目录，报文写在其下 <c>raw/</c> 子目录。修改后需重启服务才生效。</summary>
    public string StorageRootPath { get; set; } = "/app/App_Data/FaceIngest";

    /// <summary>单个请求体上限（字节），Kestrel 全局 MaxRequestBodySize 也取这个值；超限回 413。</summary>
    public long MaxRequestBodyBytes { get; set; } = DefaultMaxRequestBodyBytes;

    /// <summary>已存原始报文总字节硬上限（.bin + .json）；达到后新报文读掉丢弃并回 200。</summary>
    public long MaxTotalBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>已存文件数上限（.bin 与 .json 各算一个文件）；达到后新报文读掉丢弃并回 200。</summary>
    public long MaxFileCount { get; set; } = 100_000;

    /// <summary>存储所在盘的最低剩余空间（字节），低于此值停止落盘；设为 0 表示不检查。</summary>
    public long MinFreeDiskBytes { get; set; } = 10L * 1024 * 1024 * 1024;

    /// <summary>原始报文保留天数，超期由后台清理任务删除。</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>后台清理周期（分钟）。</summary>
    public int CleanupIntervalMinutes { get; set; } = 10;

    /// <summary>cameras.json 所在目录；只在启动时读取一次（AddJsonFile 之前）。</summary>
    public string? ConfigDirectory { get; set; }

    /// <summary>摄像机清单；现场通常放在 cameras.json，可热更新。</summary>
    public List<FaceIngestCameraOptions> Cameras { get; set; } = [];
}

/// <summary>
/// 单台摄像机的登记信息。一台摄像机一个 token，配置里只放 token 的 SHA-256（64 位十六进制）。
/// </summary>
internal sealed class FaceIngestCameraOptions
{
    /// <summary>摄像机编号，会进文件路径，必须匹配 <c>^[a-z0-9][a-z0-9-]{0,63}$</c>，全局唯一。</summary>
    public string? CameraId { get; set; }

    /// <summary>所属门店编码，非空、不超过 32 个字符，只允许字母数字、下划线与连字符。</summary>
    public string? StoreCode { get; set; }

    /// <summary>token 原文 UTF-8 字节的 SHA-256，64 位十六进制，大小写皆可，全局唯一。</summary>
    public string? TokenSha256 { get; set; }

    /// <summary>
    /// 是否启用：<c>true</c> / <c>false</c>（大小写不敏感），缺省为启用；停用的摄像机推送一律 401。
    /// 关键逻辑：故意绑定成字符串、由 <see cref="FaceIngestSettings.TryCreate"/> 严格解析。
    /// 若声明成 bool，ConfigurationBinder 绑定 Cameras 列表时会吞掉单个元素的类型转换异常并把整台摄像机丢掉
    /// （例如手写成 <c>"Enabled": 1</c> / <c>"yes"</c> / <c>null</c>），校验看不到它：启动不失败、热更新也不记 Error，
    /// 那台摄像机只会静默变成 401。
    /// </summary>
    public string? Enabled { get; set; }

    /// <summary>备注（安装位置等），仅供人看。</summary>
    public string? Note { get; set; }
}
