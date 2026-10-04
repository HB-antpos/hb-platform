using System.Net.Http.Headers;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services;

/// <summary>签署原件只能经鉴权接口读取；与图片公开上传路径隔离。</summary>
public interface IEmployeeMinorDocumentStore
{
    Task<ApiResponse<string>> SaveAsync(string objectKey, byte[] bytes);
    Task<ApiResponse<byte[]>> ReadAsync(string objectKey);
    Task<ApiResponse<bool>> DeleteAsync(string objectKey);
}

public sealed class CosEmployeeMinorDocumentStore(TencentCloudUploadService storage, IHttpClientFactory clients) : IEmployeeMinorDocumentStore
{
    public async Task<ApiResponse<string>> SaveAsync(string objectKey, byte[] bytes)
    {
        if (!storage.HasRequiredConfiguration()) return ApiResponse<string>.Error("签署原件存储未配置", "DOCUMENT_STORAGE_UNAVAILABLE");
        // 显式 private ACL 必须参与 COS 请求签名，不能依赖桶的默认可见性。
        var headers = new Dictionary<string, string> { ["x-cos-acl"] = "private", ["Cache-Control"] = "no-store" };
        var signature = storage.GetDirectUploadSignature(objectKey, "application/pdf", 300, headers);
        using var request = new HttpRequestMessage(HttpMethod.Put, signature.Url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        try
        {
            using var client = clients.CreateClient("minor-employment-documents");
            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode ? ApiResponse<string>.OK(objectKey) : ApiResponse<string>.Error("签署原件保存失败", "DOCUMENT_STORAGE_FAILED");
        }
        catch (HttpRequestException) { return ApiResponse<string>.Error("签署原件保存失败", "DOCUMENT_STORAGE_FAILED"); }
        catch (TaskCanceledException) { return ApiResponse<string>.Error("签署原件保存超时，请刷新状态后重试", "DOCUMENT_STORAGE_TIMEOUT"); }
    }

    public Task<ApiResponse<byte[]>> ReadAsync(string objectKey) => storage.DownloadObjectBytesAsync(objectKey, 20 * 1024 * 1024);
    public Task<ApiResponse<bool>> DeleteAsync(string objectKey) => storage.DeleteObjectAsync(objectKey);
}
