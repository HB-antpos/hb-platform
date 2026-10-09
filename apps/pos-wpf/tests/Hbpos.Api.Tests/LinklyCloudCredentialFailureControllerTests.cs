using System.Security.Claims;
using System.Text.Json;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudCredentialFailureControllerTests
{
    [Theory]
    [InlineData("reentry", "LINKLY_CLOUD_TERMINAL_CREDENTIAL_REENTRY_REQUIRED", "Linkly Cloud terminal credentials must be re-entered in the management portal.")]
    [InlineData("unavailable", "LINKLY_CLOUD_TERMINAL_CREDENTIAL_UNAVAILABLE", "Linkly Cloud terminal credentials are unavailable. Re-enter them in the management portal.")]
    // 密钥环不一致时“重新录入”无效，必须是专用错误码，且提示明确要求不要重录。
    [InlineData("keyring", "LINKLY_CLOUD_TERMINAL_CREDENTIAL_KEY_RING_MISMATCH", "Linkly Cloud terminal credentials cannot be decrypted by the POS server. Do not re-enter the password; ask an administrator to check that Admin and POS use the same Linkly credential key directory.")]
    public async Task Every_payment_entry_point_returns_the_same_safe_credential_failure(
        string scenario,
        string expectedCode,
        string expectedMessage)
    {
        Exception exception = scenario switch
        {
            "reentry" => new LinklyCloudTerminalCredentialReentryRequiredException(),
            "keyring" => new LinklyCloudTerminalCredentialKeyRingMismatchException(
                Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"),
                new System.Security.Cryptography.CryptographicException("key not found")),
            _ => new LinklyCloudTerminalCredentialUnavailableException()
        };
        var controller = CreateController(new ThrowingBackendService(exception));
        var terminalId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

        AssertFailure(
            await controller.StartCloudBackendTransaction(
                new LinklyCloudBackendTransactionRequest(
                    "Sandbox", "P", 100, null, terminalId, 1),
                CancellationToken.None),
            expectedCode,
            expectedMessage);
        AssertFailure(
            await controller.StartCloudBackendSettlement(
                new LinklyCloudBackendSettlementRequest("Sandbox", terminalId, 1),
                CancellationToken.None),
            expectedCode,
            expectedMessage);
        AssertFailure(
            await controller.GetCloudBackendHealth(
                "Sandbox", CancellationToken.None, terminalId, 1),
            expectedCode,
            expectedMessage);
        AssertFailure(
            await controller.RunCloudBackendStatusTest(
                "Sandbox", CancellationToken.None, terminalId, 1),
            expectedCode,
            expectedMessage);
        AssertFailure(
            await controller.RunCloudBackendLogonTest(
                "Sandbox", CancellationToken.None, terminalId, 1),
            expectedCode,
            expectedMessage);
        AssertFailure(
            await controller.GetCloudBackendTransactionStatus(
                "session-1", "Sandbox", CancellationToken.None),
            expectedCode,
            expectedMessage);
        AssertFailure(
            await controller.GetCloudBackendSettlementStatus(
                "session-1", "Sandbox", CancellationToken.None),
            expectedCode,
            expectedMessage);
        AssertFailure(
            await controller.RecoverCloudBackendTransaction(
                "session-1",
                new LinklyCloudBackendRecoverRequest("Sandbox"),
                CancellationToken.None),
            expectedCode,
            expectedMessage);
        AssertFailure(
            await controller.SendCloudBackendKey(
                "session-1",
                new LinklyCloudBackendSendKeyRequest("Sandbox", "OK", null),
                CancellationToken.None),
            expectedCode,
            expectedMessage);
    }

    private static void AssertFailure<T>(
        ActionResult<ApiResult<T>> action,
        string expectedCode,
        string expectedMessage)
    {
        var conflict = Assert.IsType<ConflictObjectResult>(action.Result);
        var envelope = Assert.IsType<ApiResult<T>>(conflict.Value);
        Assert.False(envelope.Success);
        Assert.Equal(expectedCode, envelope.ErrorCode);
        Assert.Equal(expectedMessage, envelope.Message);
    }

    private static LinklyController CreateController(ILinklyCloudBackendAsyncService backendService)
    {
        var services = new ServiceCollection()
            .AddSingleton<ILinklyCloudTerminalService>(
                new FixedLinklyCloudTerminalModeService("Active"))
            .BuildServiceProvider();
        return new LinklyController(
            new NoOpLinklyCloudCredentialService(),
            backendService,
            new NoOpLinklyCloudPairingService())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = services,
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
                        new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01")
                    ], "Test"))
                }
            }
        };
    }

    // internal：回调可观测性测试复用；acceptNotifications 让回调通知成功，其余入口仍抛 exception。
    internal sealed class ThrowingBackendService(Exception exception, bool acceptNotifications = false)
        : ILinklyCloudBackendAsyncService
    {
        public Task<LinklyCloudBackendSessionResponse> StartTransactionAsync(
            string storeCode, string deviceCode, LinklyCloudBackendTransactionRequest request,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendSessionResponse> StartSettlementAsync(
            string storeCode, string deviceCode, LinklyCloudBackendSettlementRequest request,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendSessionResponse?> GetStatusAsync(
            string storeCode, string deviceCode, string environment, string sessionId,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendSessionResponse?> GetSettlementStatusAsync(
            string storeCode, string deviceCode, string environment, string sessionId,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendSessionResponse?> GetActiveSessionAsync(
            string storeCode, string deviceCode, string environment,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse?> GetResumableSessionAsync(
            string storeCode, string deviceCode, string environment,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LinklyCloudBackendHealthResponse> GetHealthAsync(
            string storeCode, string deviceCode, string environment,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendLogonTestResponse> RunLogonTestAsync(
            string storeCode, string deviceCode, string environment,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendStatusTestResponse> RunStatusTestAsync(
            string storeCode, string deviceCode, string environment,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendTerminalCredentialResponse> UpsertTerminalCredentialAsync(
            string storeCode, string deviceCode,
            LinklyCloudBackendTerminalCredentialUpsertRequest request,
            string? updatedBy, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> RecoverAsync(
            string storeCode, string deviceCode, string sessionId,
            LinklyCloudBackendRecoverRequest request,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendSessionResponse> SendKeyAsync(
            string storeCode, string deviceCode, string sessionId,
            LinklyCloudBackendSendKeyRequest request,
            CancellationToken cancellationToken) => throw exception;

        public Task<LinklyCloudBackendSessionResponse> MarkReceiptPrintedAsync(
            string storeCode, string deviceCode, string sessionId,
            LinklyCloudBackendMarkReceiptPrintedRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<LinklyCloudBackendSessionResponse> AcknowledgeSessionAsync(
            string storeCode, string deviceCode, string environment, string sessionId,
            bool supervisorResolved, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReceiveNotificationAsync(
            string environment, string sessionId, string type, string? authorizationHeader,
            JsonElement payload, CancellationToken cancellationToken) =>
            acceptNotifications ? Task.CompletedTask : throw exception;
    }
}
