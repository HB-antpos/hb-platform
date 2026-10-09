using System.Net;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Client.Tests;

public sealed class LinklyCloudCredentialApiClientTests
{
    [Fact]
    public async Task UpsertCredentialAsync_sends_environment_username_and_password_to_backend()
    {
        HttpRequestMessage? capturedRequest = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "storeCode": "S01",
                    "environment": "Sandbox",
                    "username": "store-user",
                    "hasPassword": true,
                    "updatedAt": "2026-06-02T04:00:00Z"
                  }
                }
                """);
        }))
        {
            BaseAddress = new Uri("https://pos.example/")
        };
        var client = new LinklyCloudCredentialApiClient(httpClient);

        var response = await client.UpsertCredentialAsync(
            CardTerminalEnvironment.Sandbox,
            "store-user",
            "store-password");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Put, capturedRequest!.Method);
        Assert.Equal("https://pos.example/api/v1/linkly/cloud-credential", capturedRequest.RequestUri!.AbsoluteUri);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(body, "environment"));
        Assert.Equal("store-user", ReadJsonString(body, "username"));
        Assert.Equal("store-password", ReadJsonString(body, "password"));
        Assert.Equal("S01", response.StoreCode);
        Assert.True(response.HasPassword);
    }

    [Fact]
    public async Task UpsertBackendTerminalCredentialAsync_sends_environment_secret_and_pos_id_to_backend()
    {
        HttpRequestMessage? capturedRequest = null;
        var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return JsonResponse(
                """
                {
                  "success": true,
                  "data": {
                    "environment": "Sandbox",
                    "storeCode": "S01",
                    "deviceCode": "TERM-1",
                    "hasSecret": true,
                    "posId": "8e0e2c3a-fb53-4ea6-9c4c-4342523f944b",
                    "updatedAt": "2026-06-02T04:00:00Z"
                  }
                }
                """);
        }))
        {
            BaseAddress = new Uri("https://pos.example/")
        };
        var client = new LinklyCloudCredentialApiClient(httpClient);

        var response = await client.UpsertBackendTerminalCredentialAsync(
            CardTerminalEnvironment.Sandbox,
            "cloud-secret",
            "8e0e2c3a-fb53-4ea6-9c4c-4342523f944b");

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Put, capturedRequest!.Method);
        Assert.Equal("https://pos.example/api/v1/linkly/cloud-backend/terminal", capturedRequest.RequestUri!.AbsoluteUri);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Equal("Sandbox", ReadJsonString(body, "environment"));
        Assert.Equal("cloud-secret", ReadJsonString(body, "secret"));
        Assert.Equal("8e0e2c3a-fb53-4ea6-9c4c-4342523f944b", ReadJsonString(body, "posId"));
        Assert.Equal("TERM-1", response.DeviceCode);
        Assert.True(response.HasSecret);
    }

    private static string? ReadJsonString(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString();
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
