using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.DailyCloses;

/// <summary>
/// 日结记录后台只读查询（WPF、手持、iPad 收银端上传的日结与现金盘点）。
/// 分店范围：管理员看全部；店长只看自己关联的分店；其余账号无可见记录。
/// </summary>
public interface IDailyCloseQueryService
{
    /// <summary>
    /// 分页列表。参数非法（日期格式、区间过长、分页越界、枚举值）抛出 DailyCloseRequestException；
    /// 没有可见分店或请求分店与可见范围无交集时返回空结果而不是报错。
    /// </summary>
    Task<DailyCloseListResultDto> GetListAsync(
        DailyCloseQueryDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 单条详情；记录不存在与无分店权限都返回 null，调用方统一映射为 404，避免泄露记录是否存在。
    /// </summary>
    Task<DailyCloseDetailDto?> GetDetailAsync(
        Guid dailyCloseGuid,
        CancellationToken cancellationToken = default);
}
