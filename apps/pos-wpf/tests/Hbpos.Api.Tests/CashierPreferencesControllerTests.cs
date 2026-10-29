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
using Microsoft.Extensions.Logging.Abstractions;

namespace Hbpos.Api.Tests;

/// <summary>
/// 收银员配色偏好接口：员工只取自收银员票据并核对设备范围；保存时请求里的员工必须与票据一致，
/// 配色只接受固定的六个值；存储异常一律映射为 503 暂不可用，让收银端沿用本机主题。
/// </summary>
public sealed class CashierPreferencesControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Routes_and_authorization_attributes_are_stable()
    {
        var controllerType = typeof(CashierPreferencesController);
        Assert.Equal("api/v1/cashiers/me/preferences", controllerType.GetCustomAttribute<RouteAttribute>()?.Template);
        Assert.NotNull(controllerType.GetCustomAttribute<ApiControllerAttribute>());

        var get = controllerType.GetMethod(nameof(CashierPreferencesController.GetColorTheme))!;
        Assert.Equal("color-theme", get.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.NotNull(get.GetCustomAttribute<AuthorizeAttribute>());

        var save = controllerType.GetMethod(nameof(CashierPreferencesController.SaveColorTheme))!;
        Assert.Equal("color-theme", save.GetCustomAttribute<HttpPutAttribute>()?.Template);
        Assert.NotNull(save.GetCustomAttribute<AuthorizeAttribute>());
    }

    [Fact]
    public void Color_theme_list_matches_the_wpf_palettes_and_normalizes_case()
    {
        Assert.Equal(["Default", "Paper", "Graphite", "Celadon", "Blue", "Elder"], CashierColorThemes.All);
        Assert.Equal("Graphite", CashierColorThemes.Normalize(" graphite "));
        Assert.Null(CashierColorThemes.Normalize("Neon"));
        Assert.Null(CashierColorThemes.Normalize(null));
    }

    [Theory]
    [InlineData("Graphite", "Graphite")]
    [InlineData("elder", "Elder")]
    [InlineData(null, null)]
    [InlineData("Neon", null)]
    public async Task Get_returns_the_ticket_users_theme_and_treats_unknown_values_as_unset(string? stored, string? expected)
    {
        var store = new FakePreferenceStore { Stored = stored };
        var controller = CreateController(store, Ticket("S01", "POS-01"), AuthenticatedDevice("S01", "POS-01"));

        var action = await controller.GetColorTheme(CancellationToken.None);

        var data = AssertOk(action);
        Assert.Equal("user-guid-1", data.UserGuid);
        Assert.Equal(expected, data.ColorTheme);
        Assert.Equal(("user-guid-1", CashierPreferenceKeys.WpfColorTheme), Assert.Single(store.Reads));
    }

    [Fact]
    public async Task Get_without_a_valid_ticket_is_unauthorized_and_never_reads()
    {
        var store = new FakePreferenceStore();
        var controller = CreateController(store, ticket: null, AuthenticatedDevice("S01", "POS-01"));

        var action = await controller.GetColorTheme(CancellationToken.None);

        AssertError(action, StatusCodes.Status401Unauthorized, "CASHIER_SESSION_INVALID");
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task Ticket_for_another_store_is_forbidden_before_touching_the_store()
    {
        var store = new FakePreferenceStore();
        var controller = CreateController(store, Ticket("S02", "POS-09"), AuthenticatedDevice("S01", "POS-01"));

        var get = await controller.GetColorTheme(CancellationToken.None);
        var save = await controller.SaveColorTheme(
            new CashierColorThemePreferenceUpdateRequest("user-guid-1", "Paper"),
            CancellationToken.None);

        AssertError(get, StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN");
        AssertError(save, StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN");
        Assert.Empty(store.Reads);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task Store_failure_is_reported_as_unavailable()
    {
        var store = new FakePreferenceStore { Failure = new InvalidOperationException("Invalid object name 'dbo.UserPreference'.") };
        var controller = CreateController(store, Ticket("S01", "POS-01"), AuthenticatedDevice("S01", "POS-01"));

        var get = await controller.GetColorTheme(CancellationToken.None);
        var save = await controller.SaveColorTheme(
            new CashierColorThemePreferenceUpdateRequest("user-guid-1", "Paper"),
            CancellationToken.None);

        AssertError(get, StatusCodes.Status503ServiceUnavailable, "CASHIER_PREFERENCES_UNAVAILABLE");
        AssertError(save, StatusCodes.Status503ServiceUnavailable, "CASHIER_PREFERENCES_UNAVAILABLE");
    }

    [Fact]
    public async Task Save_writes_the_normalized_theme_for_the_ticket_user()
    {
        var store = new FakePreferenceStore();
        var controller = CreateController(store, Ticket("S01", "POS-01"), AuthenticatedDevice("S01", "POS-01"));

        var action = await controller.SaveColorTheme(
            new CashierColorThemePreferenceUpdateRequest("USER-GUID-1", "celadon"),
            CancellationToken.None);

        var data = AssertOk(action);
        Assert.Equal("Celadon", data.ColorTheme);
        Assert.Equal(
            ("user-guid-1", CashierPreferenceKeys.WpfColorTheme, "Celadon", Now.UtcDateTime),
            Assert.Single(store.Writes));
    }

    [Fact]
    public async Task Save_for_a_different_user_than_the_ticket_is_rejected()
    {
        var store = new FakePreferenceStore();
        var controller = CreateController(store, Ticket("S01", "POS-01"), AuthenticatedDevice("S01", "POS-01"));

        var action = await controller.SaveColorTheme(
            new CashierColorThemePreferenceUpdateRequest("user-guid-previous", "Paper"),
            CancellationToken.None);

        AssertError(action, StatusCodes.Status409Conflict, "CASHIER_PREFERENCE_USER_MISMATCH");
        Assert.Empty(store.Writes);
    }

    [Theory]
    [InlineData("Neon")]
    [InlineData("")]
    public async Task Save_rejects_unknown_themes(string theme)
    {
        var store = new FakePreferenceStore();
        var controller = CreateController(store, Ticket("S01", "POS-01"), AuthenticatedDevice("S01", "POS-01"));

        var action = await controller.SaveColorTheme(
            new CashierColorThemePreferenceUpdateRequest("user-guid-1", theme),
            CancellationToken.None);

        AssertError(action, StatusCodes.Status400BadRequest, "CASHIER_COLOR_THEME_INVALID");
        Assert.Empty(store.Writes);
    }

    private static CashierPreferencesController CreateController(
        FakePreferenceStore store,
        CashierAuthorizationTicket? ticket,
        ClaimsPrincipal user)
    {
        var controller = new CashierPreferencesController(
            new FakeTicketService { ValidateResult = ticket },
            store,
            new FixedTimeProvider(Now),
            NullLogger<CashierPreferencesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.User = user;
        controller.HttpContext.Request.Headers[CashierAuthorizationConstants.HeaderName] = "ticket-token";
        return controller;
    }

    private static ClaimsPrincipal AuthenticatedDevice(string storeCode, string deviceCode) =>
        new(new ClaimsIdentity(
            [
                new Claim(DeviceAuthConstants.StoreCodeClaim, storeCode),
                new Claim(DeviceAuthConstants.DeviceCodeClaim, deviceCode)
            ],
            "test"));

    private static CashierAuthorizationTicket Ticket(string storeCode, string deviceCode) =>
        new("C001", "user-guid-1", storeCode, deviceCode, Now.AddHours(24), "HW-1");

    private static CashierColorThemePreferenceDto AssertOk(ActionResult<ApiResult<CashierColorThemePreferenceDto>> action)
    {
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var envelope = Assert.IsType<ApiResult<CashierColorThemePreferenceDto>>(ok.Value);
        Assert.True(envelope.Success);
        return Assert.IsType<CashierColorThemePreferenceDto>(envelope.Data);
    }

    private static void AssertError(
        ActionResult<ApiResult<CashierColorThemePreferenceDto>> action,
        int expectedStatus,
        string expectedCode)
    {
        var result = Assert.IsAssignableFrom<ObjectResult>(action.Result);
        Assert.Equal(expectedStatus, result.StatusCode);
        var envelope = Assert.IsType<ApiResult<CashierColorThemePreferenceDto>>(result.Value);
        Assert.False(envelope.Success);
        Assert.Equal(expectedCode, envelope.ErrorCode);
    }

    private sealed class FakePreferenceStore : ICashierPreferenceStore
    {
        public string? Stored { get; init; }

        public Exception? Failure { get; init; }

        public List<(string UserGuid, string Key)> Reads { get; } = [];

        public List<(string UserGuid, string Key, string Value, DateTime UpdatedAtUtc)> Writes { get; } = [];

        public Task<string?> GetAsync(string userGuid, string preferenceKey, CancellationToken cancellationToken)
        {
            Reads.Add((userGuid, preferenceKey));
            return Failure is null ? Task.FromResult(Stored) : Task.FromException<string?>(Failure);
        }

        public Task SetAsync(
            string userGuid,
            string preferenceKey,
            string preferenceValue,
            DateTime updatedAtUtc,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            Writes.Add((userGuid, preferenceKey, preferenceValue, updatedAtUtc));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTicketService : ICashierAuthorizationTicketService
    {
        public CashierAuthorizationTicket? ValidateResult { get; init; }

        public (string Token, DateTimeOffset ExpiresAtUtc) Issue(
            string cashierId,
            string userGuid,
            string storeCode,
            string deviceCode) =>
            throw new NotSupportedException("偏好接口不应签发票据。");

        public CashierAuthorizationTicket? Validate(string? token) => ValidateResult;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
