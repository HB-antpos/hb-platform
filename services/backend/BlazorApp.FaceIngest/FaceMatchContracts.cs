namespace BlazorApp.FaceIngest;

/// <summary>
/// 比对引擎抽象（设计第 13.2 节）：业务逻辑只依赖这两个接口，腾讯云类型不外泄；
/// 将来改自建 / 换云只需新增实现。失败一律抛 <see cref="FaceMatchApiException"/>，由流水线统一退避或丢弃。
/// </summary>
internal interface IFaceMatcher
{
    /// <summary>
    /// 对一张人脸图做一次「按库返回」搜索：同时带上若干人员库（员工库 / 当日访客库 / 名单库），每个库返回最相似的一人。
    /// 图片里没有可用人脸或质量不达标时返回 <see cref="FaceSearchStatus.NoUsableFace"/>，不抛异常。
    /// </summary>
    Task<FaceSearchResult> SearchAsync(FaceSearchRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 访客库登记（设计第 13.2、9 节第 10 点）：只允许操作「当日访客库」<c>v{店}_{yyyyMMdd}</c>，
/// 员工库与名单库的写入只经主 API 的管理子账号，这里即使传入也会被实现拒绝。
/// </summary>
internal interface IVisitorRegistry
{
    /// <summary>创建当日访客库；库已存在视为成功（幂等）。</summary>
    Task CreateGroupAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>
    /// 在当日访客库建一个访客。<paramref name="personId"/> 由调用方按抓拍编号确定性生成：
    /// 重试时已存在视为成功，不会因为重试多建人。
    /// </summary>
    Task CreatePersonAsync(string groupId, string personId, ReadOnlyMemory<byte> image, CancellationToken cancellationToken);
}

/// <summary>一次搜索请求。<see cref="MinScore"/> 为 null 表示返回所有分数（阈值标定用）。</summary>
internal sealed record FaceSearchRequest(IReadOnlyList<string> GroupIds, ReadOnlyMemory<byte> Image, double? MinScore);

/// <summary>搜索结果状态。</summary>
internal enum FaceSearchStatus
{
    /// <summary>检索成功，<see cref="FaceSearchResult.Hits"/> 为各库最相似的一人（可能为空）。</summary>
    Ok,

    /// <summary>没有检测到人脸 / 质量控制不通过 / 图片本身不可用：计低质量，不重试。</summary>
    NoUsableFace,
}

/// <summary>某个库里最相似的候选人；<see cref="PersonId"/> 仅用于内部关联，禁止写日志。</summary>
internal sealed record FaceSearchHit(string GroupId, string PersonId, double Score);

/// <summary>搜索结果。</summary>
internal sealed record FaceSearchResult(FaceSearchStatus Status, IReadOnlyList<FaceSearchHit> Hits)
{
    public static FaceSearchResult NoUsableFace { get; } = new(FaceSearchStatus.NoUsableFace, []);

    public static FaceSearchResult Empty { get; } = new(FaceSearchStatus.Ok, []);

    /// <summary>取某个库的最高分命中；没有则为 null。</summary>
    public FaceSearchHit? BestIn(string groupId)
    {
        FaceSearchHit? best = null;
        foreach (var hit in Hits)
        {
            if (string.Equals(hit.GroupId, groupId, StringComparison.Ordinal) && (best is null || hit.Score > best.Score))
            {
                best = hit;
            }
        }

        return best;
    }
}

/// <summary>腾讯云错误的处理类别（决定重试、丢弃还是熔断）。</summary>
internal enum FaceMatchErrorKind
{
    /// <summary>网络超时、5xx、算法服务暂时异常：退避重试。</summary>
    Transient,

    /// <summary>限频：指数退避（更长），超限丢弃。</summary>
    RateLimited,

    /// <summary>账号级致命错误（认证失败、欠费、服务未开通等）：告警、不重试，并暂停调用一段时间。</summary>
    AccountFatal,

    /// <summary>请求级错误（库不存在、参数不合法等）：告警、不重试，只丢弃这一条。</summary>
    RequestFatal,

    /// <summary>图片质量类错误（无人脸、分辨率过小、解码失败等）：计低质量，不重试。</summary>
    Quality,
}

/// <summary>
/// 腾讯云调用失败。消息只含错误码、动作与请求编号（RequestId），<b>不含</b>腾讯云返回的自由文本、图片、密钥或签名。
/// </summary>
internal sealed class FaceMatchApiException : Exception
{
    public FaceMatchApiException(FaceMatchErrorKind kind, string code, string action, string? requestId = null, Exception? inner = null)
        : base($"Face match API error kind={kind} code={code} action={action} requestId={requestId ?? "-"}", inner)
    {
        Kind = kind;
        Code = code;
        Action = action;
    }

    public FaceMatchErrorKind Kind { get; }

    /// <summary>腾讯云错误码（如 AuthFailure.SignatureFailure）；网络层失败时为 Network.* 之类的内部标记。</summary>
    public string Code { get; }

    public string Action { get; }
}
