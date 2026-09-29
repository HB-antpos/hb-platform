using System.Reflection;
using System.Security.Claims;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Hbpos.Api.Tests;

/// <summary>
/// 直接覆盖收银员控制器：扫码登录与会话刷新都必须先核对设备 claim 的门店/设备范围，
/// 票据硬件绑定只取自已认证设备 claim，服务层返回空时分别映射为登录失败与会话已撤销。
/// </summary>
public sealed class CashiersControllerTests
{
    private static readonly CashierSessionDto Session = new(
        "C001",
        "user-guid-1",
        "Alice",
        "S01",
        "POS-01",
        ["Cashier"],
        ["Permissions.PosTerminal.Sales.Checkout"],
        ["S01"],
        IsSuperAdmin: false,
        IsOfflineCached: false,
        IsEmergencyOverride: false,
        AuthorizationToken: "ticket-token",
        AuthorizationExpiresAtUtc: new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Routes_and_authorization_attributes_are_stable()
    {
        var controllerType = typeof(CashiersController);
        Assert.Equal("api/v1/cashiers", controllerType.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(controllerType.GetCustomAttribute<ApiControllerAttribute>());

        var barcodeLogin = controllerType.GetMethod(nameof(CashiersController.BarcodeLogin))!;
        Assert.Equal("barcode-login", barcodeLogin.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.NotNull(barcodeLogin.GetCustomAttribute<AuthorizeAttribute>());

        var getSession = controllerType.GetMethod(nameof(CashiersController.GetSession))!;
        Assert.Equal("session", getSession.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.NotNull(getSession.GetCustomAttribute<AuthorizeAttribute>());
    }

    // ── BarcodeLogin ──

    [Fact]
    public async Task Barcode_login_returns_session_and_binds_hardware_id_from_device_claim()
    {
        var cashiers = new FakeCashierService { LoginResult = Session };
        var controller = CreateController(cashiers, new FakeTicketService(), AuthenticatedDevice("S01", "POS-01", "HW-CLAIM-1"));
        using var cancellation = new CancellationTokenSource();
        var request = new CashierBarcodeLoginRequest("S01", "USER-BARCODE-1", "POS-01");

        var action = await controller.BarcodeLogin(request, cancellation.Token);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var envelope = Assert.IsType<ApiResult<CashierSessionDto>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Same(Session, envelope.Data);
        var call = Assert.Single(cashiers.LoginCalls);
        Assert.Same(request, call.Request);
        Assert.Equal("HW-CLAIM-1", call.HardwareId);
        Assert.Equal(cancellation.Token, call.CancellationToken);
    }

    [Fact]
    public async Task Barcode_login_scope_match_is_case_insensitive()
    {
        var cashiers = new FakeCashierService { LoginResult = Session };
        var controller = CreateController(cashiers, new FakeTicketService(), AuthenticatedDevice("S01", "POS-01", "HW-1"));

        var action = await controller.BarcodeLogin(new CashierBarcodeLoginRequest("s01", "USER-1", "pos-01"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(action.Result);
        Assert.Single(cashiers.LoginCalls);
    }

    [Theory]
    [InlineData("S02", "POS-01")]
    [InlineData("S01", "POS-02")]
    public async Task Barcode_login_outside_device_scope_is_forbidden_before_service_call(string storeCode, string deviceCode)
    {
        var cashiers = new FakeCashierService { LoginResult = Session };
        var controller = CreateController(cashiers, new FakeTicketService(), AuthenticatedDevice("S01", "POS-01", "HW-1"));

        var action = await controller.BarcodeLogin(new CashierBarcodeLoginRequest(storeCode, "USER-1", deviceCode), CancellationToken.None);

        AssertError(action, StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN");
        Assert.Empty(cashiers.LoginCalls);
    }

    [Fact]
    public async Task Barcode_login_with_blank_device_code_only_checks_store_scope()
    {
        // 扩展方法对空设备码只校验门店；空设备码由 CashierService 负责拒绝，这里只验证不被控制器放大为 403。
        var cashiers = new FakeCashierService { LoginResult = null };
        var controller = CreateController(cashiers, new FakeTicketService(), AuthenticatedDevice("S01", "POS-01", "HW-1"));

        var action = await controller.BarcodeLogin(new CashierBarcodeLoginRequest("S01", "USER-1", " "), CancellationToken.None);

        AssertError(action, StatusCodes.Status401Unauthorized, "CASHIER_LOGIN_FAILED");
        Assert.Single(cashiers.LoginCalls);
    }

    [Fact]
    public async Task Barcode_login_failure_returns_unauthorized_envelope()
    {
        var cashiers = new FakeCashierService { LoginResult = null };
        var controller = CreateController(cashiers, new FakeTicketService(), AuthenticatedDevice("S01", "POS-01", "HW-1"));

        var action = await controller.BarcodeLogin(new CashierBarcodeLoginRequest("S01", "BAD-BARCODE", "POS-01"), CancellationToken.None);

        var envelope = AssertError(action, StatusCodes.Status401Unauthorized, "CASHIER_LOGIN_FAILED");
        Assert.Null(envelope.Data);
        Assert.Equal("收银员条码无效或已停用", envelope.Message);
    }

    [Fact]
    public async Task Barcode_login_without_hardware_claim_passes_null_hardware_id()
    {
        var cashiers = new FakeCashierService { LoginResult = Session };
        var controller = CreateController(cashiers, new FakeTicketService(), AuthenticatedDevice("S01", "POS-01", hardwareId: null));

        await controller.BarcodeLogin(new CashierBarcodeLoginRequest("S01", "USER-1", "POS-01"), CancellationToken.None);

        Assert.Null(Assert.Single(cashiers.LoginCalls).HardwareId);
    }

    // ── GetSession ──

    [Fact]
    public async Task Get_session_refreshes_valid_ticket_from_cashier_header()
    {
        var ticket = Ticket("S01", "POS-01");
        var tickets = new FakeTicketService { ValidateResult = ticket };
        var cashiers = new FakeCashierService { RefreshResult = Session };
        var controller = CreateController(cashiers, tickets, AuthenticatedDevice("S01", "POS-01", "HW-1"));
        controller.HttpContext.Request.Headers[CashierAuthorizationConstants.HeaderName] = "ticket-token";
        using var cancellation = new CancellationTokenSource();

        var action = await controller.GetSession(cancellation.Token);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var envelope = Assert.IsType<ApiResult<CashierSessionDto>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Same(Session, envelope.Data);
        Assert.Equal(new string?[] { "ticket-token" }, tickets.ValidatedTokens);
        var refresh = Assert.Single(cashiers.RefreshCalls);
        Assert.Same(ticket, refresh.Ticket);
        Assert.Equal(cancellation.Token, refresh.CancellationToken);
    }

    [Fact]
    public async Task Get_session_without_header_validates_empty_token_and_returns_invalid()
    {
        var tickets = new FakeTicketService { ValidateResult = null };
        var cashiers = new FakeCashierService { RefreshResult = Session };
        var controller = CreateController(cashiers, tickets, AuthenticatedDevice("S01", "POS-01", "HW-1"));

        var action = await controller.GetSession(CancellationToken.None);

        var envelope = AssertError(action, StatusCodes.Status401Unauthorized, "CASHIER_SESSION_INVALID");
        Assert.Equal("收银员会话已失效，请重新登录", envelope.Message);
        Assert.Equal(new string?[] { string.Empty }, tickets.ValidatedTokens);
        Assert.Empty(cashiers.RefreshCalls);
    }

    [Theory]
    [InlineData("S02", "POS-01")]
    [InlineData("S01", "POS-09")]
    public async Task Get_session_ticket_for_other_store_or_device_is_forbidden(string ticketStore, string ticketDevice)
    {
        // 票据来自另一台设备/门店时，即使签名有效也不能在本设备上刷新。
        var tickets = new FakeTicketService { ValidateResult = Ticket(ticketStore, ticketDevice) };
        var cashiers = new FakeCashierService { RefreshResult = Session };
        var controller = CreateController(cashiers, tickets, AuthenticatedDevice("S01", "POS-01", "HW-1"));
        controller.HttpContext.Request.Headers[CashierAuthorizationConstants.HeaderName] = "stolen-ticket";

        var action = await controller.GetSession(CancellationToken.None);

        AssertError(action, StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN");
        Assert.Empty(cashiers.RefreshCalls);
    }

    [Fact]
    public async Task Get_session_for_disabled_or_moved_cashier_returns_revoked()
    {
        var tickets = new FakeTicketService { ValidateResult = Ticket("S01", "POS-01") };
        var cashiers = new FakeCashierService { RefreshResult = null };
        var controller = CreateController(cashiers, tickets, AuthenticatedDevice("S01", "POS-01", "HW-1"));
        controller.HttpContext.Request.Headers[CashierAuthorizationConstants.HeaderName] = "ticket-token";

        var action = await controller.GetSession(CancellationToken.None);

        var envelope = AssertError(action, StatusCodes.Status401Unauthorized, "CASHIER_SESSION_REVOKED");
        Assert.Equal("收银员已停用或不再属于当前分店", envelope.Message);
        Assert.Single(cashiers.RefreshCalls);
    }

    private static CashiersController CreateController(
        FakeCashierService cashiers,
        FakeTicketService tickets,
        ClaimsPrincipal user)
    {
        var controller = new CashiersController(cashiers, tickets)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.User = user;
        return controller;
    }

    private static ClaimsPrincipal AuthenticatedDevice(string storeCode, string deviceCode, string? hardwareId)
    {
        var claims = new List<Claim>
        {
            new(DeviceAuthConstants.StoreCodeClaim, storeCode),
            new(DeviceAuthConstants.DeviceCodeClaim, deviceCode)
        };
        if (hardwareId is not null)
        {
            claims.Add(new Claim(DeviceAuthConstants.HardwareIdClaim, hardwareId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static CashierAuthorizationTicket Ticket(string storeCode, string deviceCode) =>
        new("C001", "user-guid-1", storeCode, deviceCode, new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero), "HW-1");

    private static ApiResult<CashierSessionDto> AssertError(
        ActionResult<ApiResult<CashierSessionDto>> action,
        int expectedStatus,
        string expectedCode)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(action.Result);
        Assert.Equal(expectedStatus, result.StatusCode);
        var envelope = Assert.IsType<ApiResult<CashierSessionDto>>(result.Value);
        Assert.False(envelope.Success);
        Assert.Equal(expectedCode, envelope.ErrorCode);
        return envelope;
    }

    private sealed class FakeCashierService : ICashierService
    {
        public CashierSessionDto? LoginResult { get; init; }

        public CashierSessionDto? RefreshResult { get; init; }

        public List<(CashierBarcodeLoginRequest Request, string? HardwareId, CancellationToken CancellationToken)> LoginCalls { get; } = [];

        public List<(CashierAuthorizationTicket Ticket, CancellationToken CancellationToken)> RefreshCalls { get; } = [];

        public Task<CashierSessionDto?> BarcodeLoginAsync(CashierBarcodeLoginRequest request, CancellationToken cancellationToken) =>
            BarcodeLoginAsync(request, null, cancellationToken);

        public Task<CashierSessionDto?> BarcodeLoginAsync(
            CashierBarcodeLoginRequest request,
            string? hardwareId,
            CancellationToken cancellationToken)
        {
            LoginCalls.Add((request, hardwareId, cancellationToken));
            return Task.FromResult(LoginResult);
        }

        public Task<bool> HasAnyPermissionAsync(
            string userGuid,
            string storeCode,
            IReadOnlyCollection<string> permissionCodes,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("控制器不应直接检查权限。");

        public Task<CashierSessionDto?> RefreshSessionAsync(CashierAuthorizationTicket ticket, CancellationToken cancellationToken)
        {
            RefreshCalls.Add((ticket, cancellationToken));
            return Task.FromResult(RefreshResult);
        }
    }

    private sealed class FakeTicketService : ICashierAuthorizationTicketService
    {
        public CashierAuthorizationTicket? ValidateResult { get; init; }

        public List<string?> ValidatedTokens { get; } = [];

        public (string Token, DateTimeOffset ExpiresAtUtc) Issue(
            string cashierId,
            string userGuid,
            string storeCode,
            string deviceCode) =>
            throw new NotSupportedException("控制器不应签发票据。");

        public CashierAuthorizationTicket? Validate(string? token)
        {
            ValidatedTokens.Add(token);
            return ValidateResult;
        }
    }
}
