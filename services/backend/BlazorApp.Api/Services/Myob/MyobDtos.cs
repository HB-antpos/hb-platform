namespace BlazorApp.Api.Services.Myob;

// 全局 JSON 为 camelCase。所有 /api/react/v1/myob/** 接口返回 ApiResponse<T>，权限 Myob.Connection.Manage。
// 方案见 docs/plans/2026-10-27-myob-payroll-integration.md 第 5 节（阶段 0：连接）。

public static class MyobConnectionStatuses
{
    /// <summary>已授权，refresh token 可用。</summary>
    public const string Active = "Active";

    /// <summary>refresh 失败（token 过期 / 被撤销），需 Administrator 重新授权。</summary>
    public const string NeedsReauth = "NeedsReauth";

    /// <summary>被手动断开，token 已清除；映射已移除。</summary>
    public const string Disconnected = "Disconnected";
}

public static class MyobErrorCodes
{
    public const string NotConfigured = "MYOB_NOT_CONFIGURED";
    public const string InvalidState = "MYOB_INVALID_STATE";
    public const string AuthorizationDenied = "MYOB_AUTHORIZATION_DENIED";
    public const string TokenExchangeFailed = "MYOB_TOKEN_EXCHANGE_FAILED";
    public const string BusinessIdMissing = "MYOB_BUSINESS_ID_MISSING";
    public const string ConnectionNotFound = "MYOB_CONNECTION_NOT_FOUND";
    public const string ConnectionNotActive = "MYOB_CONNECTION_NOT_ACTIVE";
    public const string NeedsReauthorization = "MYOB_NEEDS_REAUTHORIZATION";
    public const string InvalidAbn = "MYOB_INVALID_ABN";
    public const string StoreNotFound = "MYOB_STORE_NOT_FOUND";
    public const string ApiCallFailed = "MYOB_API_CALL_FAILED";
}

/// <summary>一个已授权的 MYOB 公司文件（一个 ABN 一条）。</summary>
public sealed class MyobConnectionDto
{
    public string ConnectionGuid { get; set; } = string.Empty;

    /// <summary>MYOB 公司文件 ID（即 API 路径里的 cf_uri）。</summary>
    public string BusinessId { get; set; } = string.Empty;

    public string BusinessName { get; set; } = string.Empty;

    /// <summary>公司 ABN，只含数字（11 位）；MYOB 读不到或未填时为 null，可手动填写。</summary>
    public string? Abn { get; set; }

    /// <summary>Active / NeedsReauth / Disconnected。</summary>
    public string Status { get; set; } = MyobConnectionStatuses.Active;

    public string? AuthorizedByUserGuid { get; set; }

    /// <summary>授权人姓名（取自我方用户表，查不到为 null）。</summary>
    public string? AuthorizedByName { get; set; }

    public DateTime AuthorizedAt { get; set; }

    /// <summary>最近一次 token 刷新或连接验证成功的时间。</summary>
    public DateTime? LastRefreshedAt { get; set; }

    /// <summary>最近一次失败原因（成功后清空）。</summary>
    public string? LastError { get; set; }

    /// <summary>映射到本连接的分店编号。</summary>
    public List<string> StoreCodes { get; set; } = [];
}

/// <summary>POST /connections/authorize-url 的响应：前端把浏览器整页跳转到 Url。</summary>
public sealed class MyobAuthorizeUrlDto
{
    public string Url { get; set; } = string.Empty;
}

/// <summary>PUT /connections/{connectionGuid}/abn 的请求；Abn 传空字符串或 null 表示清除。</summary>
public sealed class MyobUpdateAbnRequest
{
    public string? Abn { get; set; }
}

/// <summary>GET /store-mappings 的一行：分店及其当前映射 / 建议映射。</summary>
public sealed class MyobStoreMappingItemDto
{
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;

    /// <summary>分店资料里的 ABN（只含数字）；未填为 null。</summary>
    public string? StoreAbn { get; set; }

    public bool IsActive { get; set; }

    /// <summary>当前映射到的连接；未映射为 null。</summary>
    public string? ConnectionGuid { get; set; }

    /// <summary>未映射时，按 ABN 一致建议的 Active 连接；没有可建议的为 null。已映射时恒为 null。</summary>
    public string? SuggestedConnectionGuid { get; set; }

    public DateTime? MappedAt { get; set; }
}

public sealed class MyobStoreMappingsDto
{
    /// <summary>全部分店（含未启用的），按 StoreCode 升序。</summary>
    public List<MyobStoreMappingItemDto> Stores { get; set; } = [];

    /// <summary>可选作映射目标的连接（仅 Active）。</summary>
    public List<MyobConnectionDto> Connections { get; set; } = [];
}

/// <summary>PUT /store-mappings 的请求；ConnectionGuid 为 null 表示移除该分店的映射。</summary>
public sealed class MyobUpdateStoreMappingsRequest
{
    public List<MyobStoreMappingChange> Items { get; set; } = [];
}

public sealed class MyobStoreMappingChange
{
    public string StoreCode { get; set; } = string.Empty;
    public string? ConnectionGuid { get; set; }
}
