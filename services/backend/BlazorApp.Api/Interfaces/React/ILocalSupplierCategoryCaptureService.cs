using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React;

/// <summary>
/// 浏览器扩展回传的供应商分类采集写入。
/// </summary>
public interface ILocalSupplierCategoryCaptureService
{
    Task<BrowserExtensionCategoryCaptureResultDto> CaptureAsync(
        BrowserExtensionCategoryCaptureRequestDto request,
        string? actor,
        CancellationToken cancellationToken = default
    );

    Task<BrowserExtensionCategoryTreeSnapshotResultDto> ApplyTreeSnapshotAsync(
        BrowserExtensionCategoryTreeSnapshotRequestDto request,
        string? actor,
        CancellationToken cancellationToken = default
    );
}
