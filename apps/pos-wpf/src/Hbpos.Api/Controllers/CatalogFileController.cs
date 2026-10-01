using System.Diagnostics;
using Hbpos.Api.Auth;
using Hbpos.Api.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Hbpos.Api.Controllers;

/// <summary>
/// 整文件目录下载。开关关闭时全部返回 404（CATALOG_FILE_DOWNLOAD_DISABLED），
/// 新客户端据此回退原有的分页 sync-plan；文件本身由磁盘直接流出并支持 Range 断点续传。
/// </summary>
[ApiController]
[Route("api/v1/catalog/files")]
[Authorize]
public sealed class CatalogFileController(ICatalogFileSyncService fileSyncService) : ControllerBase
{
    private const string DisabledCode = "CATALOG_FILE_DOWNLOAD_DISABLED";

    [HttpGet("sync-plan")]
    public async Task<ActionResult<ApiResult<CatalogFileSyncPlanResponse>>> GetFileSyncPlan(
        [FromQuery] string storeCode,
        [FromQuery] string? baseCatalogVersion,
        CancellationToken cancellationToken)
    {
        if (!fileSyncService.IsEnabled)
        {
            return NotFound(ApiResult<CatalogFileSyncPlanResponse>.Fail(DisabledCode, "catalog file download is disabled"));
        }

        if (string.IsNullOrWhiteSpace(storeCode))
        {
            return BadRequest(ApiResult<CatalogFileSyncPlanResponse>.Fail("STORE_CODE_REQUIRED", "storeCode is required"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<CatalogFileSyncPlanResponse>("Device is not authorized for this store.");
        }

        var stopwatch = Stopwatch.StartNew();
        CatalogFileSyncPlanResponse? response;
        try
        {
            response = await fileSyncService.GetFileSyncPlanAsync(storeCode, baseCatalogVersion, cancellationToken);
        }
        catch (CatalogDownloadFileUnavailableException exception)
        {
            Log($"sync-plan response store={storeCode} status=503 reason=file-unavailable error={exception.InnerException?.GetType().Name ?? "busy"} elapsedMs={stopwatch.ElapsedMilliseconds}");
            return Unavailable<CatalogFileSyncPlanResponse>("CATALOG_FILE_UNAVAILABLE", "catalog download file is temporarily unavailable");
        }
        catch (CatalogCapacityBusyException)
        {
            return Unavailable<CatalogFileSyncPlanResponse>("CATALOG_CAPACITY_BUSY", "catalog download capacity is busy");
        }
        catch (CatalogSnapshotIsolationUnavailableException)
        {
            return Unavailable<CatalogFileSyncPlanResponse>("CATALOG_SNAPSHOT_ISOLATION_UNAVAILABLE", "catalog snapshot isolation is unavailable");
        }

        Log(response is null
            ? $"sync-plan response store={storeCode} status=404 elapsedMs={stopwatch.ElapsedMilliseconds}"
            : $"sync-plan response store={response.StoreCode} status=200 mode={response.Mode} target={response.TargetCatalogVersion} total={response.TargetTotal} file={response.File?.Kind ?? "none"} bytes={response.File?.Bytes ?? 0} deltaOperations={response.DeltaOperationCount?.ToString() ?? "-"} elapsedMs={stopwatch.ElapsedMilliseconds} heapMb={GC.GetGCMemoryInfo().HeapSizeBytes / (1024 * 1024)}");
        return response is null
            ? NotFound(ApiResult<CatalogFileSyncPlanResponse>.Fail("STORE_NOT_FOUND", "store was not found or inactive"))
            : Ok(ApiResult<CatalogFileSyncPlanResponse>.Ok(response));
    }

    [HttpGet("full")]
    public IActionResult DownloadFullFile(
        [FromQuery] string storeCode,
        [FromQuery] string catalogVersion)
    {
        if (!fileSyncService.IsEnabled)
        {
            return NotFound(ApiResult<object>.Fail(DisabledCode, "catalog file download is disabled"));
        }

        if (string.IsNullOrWhiteSpace(storeCode) || string.IsNullOrWhiteSpace(catalogVersion))
        {
            return BadRequest(ApiResult<object>.Fail("CATALOG_VERSION_REQUIRED", "storeCode and catalogVersion are required"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<object>("Device is not authorized for this store.").Result!;
        }

        return ServeFile(storeCode, fileSyncService.FindFullFile(storeCode, catalogVersion));
    }

    [HttpGet("delta")]
    public IActionResult DownloadDeltaFile(
        [FromQuery] string storeCode,
        [FromQuery] string baseCatalogVersion,
        [FromQuery] string targetCatalogVersion)
    {
        if (!fileSyncService.IsEnabled)
        {
            return NotFound(ApiResult<object>.Fail(DisabledCode, "catalog file download is disabled"));
        }

        if (string.IsNullOrWhiteSpace(storeCode) ||
            string.IsNullOrWhiteSpace(baseCatalogVersion) ||
            string.IsNullOrWhiteSpace(targetCatalogVersion))
        {
            return BadRequest(ApiResult<object>.Fail(
                "CATALOG_VERSION_REQUIRED",
                "storeCode, baseCatalogVersion and targetCatalogVersion are required"));
        }

        if (!this.IsDeviceScopeAllowed(storeCode))
        {
            return DeviceAuthorizationExtensions.DeviceScopeForbidden<object>("Device is not authorized for this store.").Result!;
        }

        return ServeFile(storeCode, fileSyncService.FindDeltaFile(storeCode, baseCatalogVersion, targetCatalogVersion));
    }

    private IActionResult ServeFile(string storeCode, CatalogDownloadFileInfo? file)
    {
        if (file is null)
        {
            // 版本已被清理：客户端应重新请求同步计划，而不是重试同一地址。
            return NotFound(ApiResult<object>.Fail("CATALOG_FILE_NOT_FOUND", "catalog file was not found; request a new sync plan"));
        }

        var range = Request.Headers.Range.ToString();
        Log($"file download store={storeCode} kind={file.Kind} bytes={file.Bytes} range={(string.IsNullOrEmpty(range) ? "-" : range)}");
        // ETag 用内容 SHA-256：客户端续传时带 If-Range，内容不符会自动收到完整的 200 响应。
        return PhysicalFile(
            file.FilePath,
            CatalogFileFormats.ContentType,
            file.CreatedAt,
            new EntityTagHeaderValue($"\"{file.Sha256}\""),
            enableRangeProcessing: true);
    }

    private ObjectResult Unavailable<T>(string code, string message) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, ApiResult<T>.Fail(code, message));

    private static void Log(string message)
    {
        Console.WriteLine($"[HBPOS][Api][CatalogFiles] {DateTimeOffset.Now:O} {message}");
    }
}
