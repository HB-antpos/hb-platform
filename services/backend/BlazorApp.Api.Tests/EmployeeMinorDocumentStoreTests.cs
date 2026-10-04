using System.Net;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class EmployeeMinorDocumentStoreTests
{
    [Fact]
    public async Task SaveSignsAndSendsPrivateAcl_AndReturnsOnlyObjectKey()
    {
        var handler = new CaptureHandler();
        var upload = new TencentCloudUploadService(Options.Create(new TencentCloudSettings { SecretId = "test-id", SecretKey = "test-key", BucketName = "test-bucket", Region = "test-region" }), NullLogger<TencentCloudUploadService>.Instance, new HttpClient(handler));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(x => x.CreateClient("minor-employment-documents")).Returns(() => new HttpClient(handler, false));
        var store = new CosEmployeeMinorDocumentStore(upload, factory.Object);
        var result = await store.SaveAsync("minor-employment/test/v1/test.pdf", [1, 2, 3]);
        Assert.True(result.Success);
        Assert.Equal("minor-employment/test/v1/test.pdf", result.Data);
        Assert.Equal("private", handler.Acl);
        Assert.Equal("no-store", handler.CacheControl);
        Assert.Equal("application/pdf", handler.ContentType);
        Assert.Contains("x-cos-acl", Uri.UnescapeDataString(handler.Query!));
        Assert.Contains("cache-control", Uri.UnescapeDataString(handler.Query!));
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Acl, CacheControl, ContentType, Query;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Acl = request.Headers.GetValues("x-cos-acl").Single();
            CacheControl = request.Headers.GetValues("Cache-Control").Single();
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Query = request.RequestUri?.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
