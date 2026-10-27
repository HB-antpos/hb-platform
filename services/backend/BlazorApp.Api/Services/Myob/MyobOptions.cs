namespace BlazorApp.Api.Services.Myob;

/// <summary>
/// MYOB 开发者应用配置（配置节 Myob，生产用环境变量 Myob__ClientId / Myob__ClientSecret / Myob__RedirectUri）。
/// ClientSecret 只放服务器环境变量，绝不进仓库、日志或接口响应。
/// RedirectUri 必须与 MYOB 开发者后台注册的 Redirect Uri 逐字一致（含 https、域名、路径、末尾斜杠）。
/// </summary>
public sealed class MyobOptions
{
    public const string SectionName = "Myob";

    /// <summary>MYOB 开发者后台的 API Key（OAuth client_id，同时是 x-myobapi-key 请求头）。</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>如 https://hotbargain.vip/api/myob/oauth/callback。</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// 空格分隔。公司文件（/{cf_uri}/、/Info）、公司设置（只读，含 GET /Company，授权后校验公司名与 ABN 用它）、
    /// 员工资料、工资。注意：sme-company-file 不覆盖 /Company，缺 sme-company-settings 会 401（2026-10-27 上线实测）。
    /// </summary>
    public string Scopes { get; set; } = "sme-company-file sme-company-settings sme-contacts-employee sme-payroll";

    public string AuthorizeUrl { get; set; } = "https://secure.myob.com/oauth2/account/authorize";

    public string TokenUrl { get; set; } = "https://secure.myob.com/oauth2/v1/authorize";

    /// <summary>云端公司文件的 API 根地址；完整地址 = ApiBaseUrl/{businessId}/{资源}。</summary>
    public string ApiBaseUrl { get; set; } = "https://api.myob.com/accountright";

    /// <summary>授权完成后浏览器被重定向回的 Web 页面路径（同源相对路径，不接受外部输入）。</summary>
    public string WebReturnPath { get; set; } = "/pos-admin/myob";

    /// <summary>相邻两次 MYOB 请求的最小间隔（毫秒）；MYOB 限流为每秒 8 次，默认 130ms 留出余量。测试可设 0。</summary>
    public int MinRequestIntervalMs { get; set; } = 130;

    /// <summary>三项必填配置齐全且 RedirectUri 是绝对地址才算已配置。</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect)
        && (redirect.Scheme == Uri.UriSchemeHttps || redirect.Scheme == Uri.UriSchemeHttp);
}
