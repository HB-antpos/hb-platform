using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Features.WarehousePicking;
using BlazorApp.Api.Interfaces;
using BlazorApp.Shared.Models.POSM;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 拣货接口的终端与拣货人校验：设备会话必须扫员工码确认拣货人，凭证绑定终端，
/// 账号本人拣货须有仓库角色或拣货权限；只有经理 / 管理员能改已有中包数。
/// </summary>
public sealed class WarehousePickingControllerTests
{
    private const string OrderGuid = "order-1";

    private readonly Mock<IWarehousePickingService> _picking = new();
    private readonly Mock<IWarehousePickerService> _pickers = new();
    private readonly Mock<IWarehousePickingAssignmentService> _assignments = new();
    private readonly Mock<IDeviceRegistrationService> _devices = new();
    private readonly Mock<IAuthorizationService> _authorization = new();
    private readonly WarehousePickerTicketProtector _tickets = new(new EphemeralDataProtectionProvider());
    private WarehousePickerContext? _capturedPicker;

    public WarehousePickingControllerTests()
    {
        _picking
            .Setup(service => service.JoinAsync(It.IsAny<string>(), It.IsAny<WarehousePickerContext>()))
            .Callback<string, WarehousePickerContext>((_, picker) => _capturedPicker = picker)
            .ReturnsAsync(WarehousePickingResult<WarehousePickingSheetDto>.Ok(new WarehousePickingSheetDto()));
        _picking
            .Setup(service => service.AppendRecordAsync(It.IsAny<string>(), It.IsAny<WarehousePickingRecordRequestDto>(), It.IsAny<WarehousePickerContext>()))
            .Callback<string, WarehousePickingRecordRequestDto, WarehousePickerContext>((_, _, picker) => _capturedPicker = picker)
            .ReturnsAsync(WarehousePickingResult<WarehousePickingLineMutationDto>.Ok(new WarehousePickingLineMutationDto()));
        _devices.Setup(service => service.ValidateDeviceAuthCodeAsync("DEV-1", "AUTH")).ReturnsAsync(true);
        _devices
            .Setup(service => service.GetDeviceByHardwareIdAsync("DEV-1"))
            .ReturnsAsync(new POSM_设备注册信息表 { 设备硬件识别码 = "DEV-1", 系统设备编号 = "PDA-07", 设备状态 = 1 });
        _authorization
            .Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Failed());
    }

    [Fact]
    public async Task 派单只允许仓库经理_员工账号与设备会话都被拒绝()
    {
        var staff = await CreateController(User("u-staff", "WarehouseStaff"), headers: new())
            .SaveAssignments(OrderGuid, new WarehousePickingAssignmentSaveRequestDto());
        var device = await CreateController(user: null, headers: DeviceHeaders()).ListPickerCandidates();

        Assert.Equal(403, StatusOf(staff));
        Assert.Equal(WarehousePickingErrorCodes.AssignNotAllowed, ErrorCodeOf(staff));
        Assert.Equal(403, StatusOf(device));
        Assert.Equal(WarehousePickingErrorCodes.AssignNotAllowed, ErrorCodeOf(device));
        _assignments.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task 仓库经理或持有订货管理权限的账号可以派单_记录派单人()
    {
        WarehousePickingAssigner? captured = null;
        _assignments
            .Setup(service => service.SaveAsync(OrderGuid, It.IsAny<WarehousePickingAssignmentSaveRequestDto>(), It.IsAny<WarehousePickingAssigner>()))
            .Callback<string, WarehousePickingAssignmentSaveRequestDto, WarehousePickingAssigner>((_, _, assigner) => captured = assigner)
            .ReturnsAsync(WarehousePickingResult<WarehousePickingAssignmentSummaryDto>.Ok(new WarehousePickingAssignmentSummaryDto()));
        _authorization
            .Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), "Warehouse.Picking"))
            .ReturnsAsync(AuthorizationResult.Success());
        _authorization
            .Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), "Warehouse.ManageOrders"))
            .ReturnsAsync(AuthorizationResult.Success());

        var manager = await CreateController(User("u-mgr", "仓库经理"), headers: new())
            .SaveAssignments(OrderGuid, new WarehousePickingAssignmentSaveRequestDto());
        Assert.Equal(200, StatusOf(manager));
        Assert.Equal(new WarehousePickingAssigner("u-mgr", "Full u-mgr"), captured);

        var orders = await CreateController(User("u-orders", "User"), headers: new())
            .SaveAssignments(OrderGuid, new WarehousePickingAssignmentSaveRequestDto());
        Assert.Equal(200, StatusOf(orders));
        Assert.Equal("u-orders", captured!.UserGuid);
    }

    [Fact]
    public async Task 扫分单条码只需终端校验_纯设备会话没确认拣货人也能解析()
    {
        _assignments
            .Setup(service => service.ResolveSlipAsync("HBSP:SO1/1/A"))
            .ReturnsAsync(WarehousePickingResult<WarehousePickingSlipResolveDto>.Ok(new WarehousePickingSlipResolveDto { OrderGuid = OrderGuid }));

        var device = await CreateController(user: null, headers: DeviceHeaders()).ResolveSlip("HBSP:SO1/1/A");
        var anonymous = await CreateController(user: null, headers: new()).ResolveSlip("HBSP:SO1/1/A");

        Assert.Equal(200, StatusOf(device));
        Assert.Equal(401, StatusOf(anonymous));
    }

    [Fact]
    public async Task 扫分单领取需要先确认拣货人_按凭证记领取人()
    {
        WarehousePickerContext? claimer = null;
        _assignments
            .Setup(service => service.ClaimSlipAsync("HBSP:SO1/1/A", It.IsAny<WarehousePickerContext>()))
            .Callback<string?, WarehousePickerContext>((_, picker) => claimer = picker)
            .ReturnsAsync(WarehousePickingResult<WarehousePickingSlipResolveDto>.Ok(new WarehousePickingSlipResolveDto()));
        var (ticket, _) = _tickets.Issue(new WarehousePickerTicketIdentity("u-mia", "Mia Wong", false), "hw:DEV-1", DateTime.UtcNow);
        var headers = DeviceHeaders();
        headers[WarehousePickingController.PickerTicketHeader] = ticket;

        var withoutPicker = await CreateController(user: null, headers: DeviceHeaders())
            .ClaimSlip(new WarehousePickingSlipClaimRequestDto { Code = "HBSP:SO1/1/A" });
        var withPicker = await CreateController(user: null, headers)
            .ClaimSlip(new WarehousePickingSlipClaimRequestDto { Code = "HBSP:SO1/1/A" });

        Assert.Equal(WarehousePickingErrorCodes.PickerRequired, ErrorCodeOf(withoutPicker));
        Assert.Equal(200, StatusOf(withPicker));
        Assert.Equal("u-mia", claimer!.UserGuid);
    }

    [Fact]
    public async Task 订单列表认得出拣货人时传入拣货人_认不出时不传()
    {
        string? capturedPicker = "unset";
        _picking
            .Setup(service => service.ListOrdersAsync("mine", null, It.IsAny<string?>()))
            .Callback<string?, string?, string?>((_, _, picker) => capturedPicker = picker)
            .ReturnsAsync(WarehousePickingResult<WarehousePickingOrderListDto>.Ok(new WarehousePickingOrderListDto()));

        await CreateController(User("u-staff", "WarehouseStaff"), headers: new()).ListOrders("mine", null);
        Assert.Equal("u-staff", capturedPicker);

        var result = await CreateController(user: null, headers: DeviceHeaders()).ListOrders("mine", null);
        Assert.Equal(200, StatusOf(result));
        Assert.Null(capturedPicker);
    }

    [Fact]
    public async Task 未登录且没有设备头时返回401()
    {
        var controller = CreateController(user: null, headers: new());

        var result = await controller.Join(OrderGuid);

        Assert.Equal(401, StatusOf(result));
        _picking.Verify(service => service.JoinAsync(It.IsAny<string>(), It.IsAny<WarehousePickerContext>()), Times.Never);
    }

    [Fact]
    public async Task 设备会话没有员工码凭证时要求先确认拣货人()
    {
        var controller = CreateController(user: null, headers: DeviceHeaders());

        var result = await controller.Join(OrderGuid);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal(WarehousePickingErrorCodes.PickerRequired, ErrorCodeOf(result));
    }

    [Fact]
    public async Task 设备会话带本终端凭证时按凭证记人并带系统设备编号()
    {
        var (ticket, _) = _tickets.Issue(new WarehousePickerTicketIdentity("u-mia", "Mia Wong", false), "hw:DEV-1", DateTime.UtcNow);
        _pickers
            .Setup(service => service.GetEligibilityAsync("u-mia"))
            .ReturnsAsync(new WarehousePickerEligibility("u-mia", "Mia Wong", true, false, "WarehouseStaff"));
        var headers = DeviceHeaders();
        headers[WarehousePickingController.PickerTicketHeader] = ticket;
        var controller = CreateController(user: null, headers);

        var result = await controller.Join(OrderGuid);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal("u-mia", _capturedPicker!.UserGuid);
        Assert.Equal("PDA-07", _capturedPicker.DeviceCode);
        Assert.Null(_capturedPicker.AuthUserGuid);
    }

    [Fact]
    public async Task 凭证换到别的终端使用视为无效()
    {
        var (ticket, _) = _tickets.Issue(new WarehousePickerTicketIdentity("u-mia", "Mia Wong", false), "hw:OTHER-PDA", DateTime.UtcNow);
        var headers = DeviceHeaders();
        headers[WarehousePickingController.PickerTicketHeader] = ticket;
        var controller = CreateController(user: null, headers);

        var result = await controller.AppendRecord(OrderGuid, new WarehousePickingRecordRequestDto());

        Assert.Equal(403, StatusOf(result));
        Assert.Equal(WarehousePickingErrorCodes.PickerTicketInvalid, ErrorCodeOf(result));
    }

    [Fact]
    public async Task 员工已被停用时加入拣货重新核对资格并拒绝()
    {
        var (ticket, _) = _tickets.Issue(new WarehousePickerTicketIdentity("u-gone", "Gone", false), "hw:DEV-1", DateTime.UtcNow);
        _pickers.Setup(service => service.GetEligibilityAsync("u-gone")).ReturnsAsync((WarehousePickerEligibility?)null);
        var headers = DeviceHeaders();
        headers[WarehousePickingController.PickerTicketHeader] = ticket;

        var result = await CreateController(user: null, headers).Join(OrderGuid);

        Assert.Equal(WarehousePickingErrorCodes.PickerNotAllowed, ErrorCodeOf(result));
    }

    [Fact]
    public async Task 没有仓库角色也没有拣货权限的账号被拒绝()
    {
        var controller = CreateController(User("u-store", "StoreStaff"), headers: new());

        var result = await controller.Join(OrderGuid);

        Assert.Equal(403, StatusOf(result));
        Assert.Equal(WarehousePickingErrorCodes.PickerNotAllowed, ErrorCodeOf(result));
    }

    [Fact]
    public async Task 持有拣货权限的普通账号可以本人拣货()
    {
        _authorization
            .Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), "Warehouse.Picking"))
            .ReturnsAsync(AuthorizationResult.Success());

        var result = await CreateController(User("u-perm", "User"), headers: new()).AppendRecord(OrderGuid, new WarehousePickingRecordRequestDto());

        Assert.Equal(200, StatusOf(result));
        Assert.Equal("u-perm", _capturedPicker!.UserGuid);
    }

    [Fact]
    public async Task 仓库员工本人拣货不能覆盖中包数_仓库经理可以()
    {
        await CreateController(User("u-staff", "WarehouseStaff"), headers: new()).AppendRecord(OrderGuid, new WarehousePickingRecordRequestDto());
        var staff = _capturedPicker!;
        await CreateController(User("u-mgr", "仓库经理"), headers: new()).AppendRecord(OrderGuid, new WarehousePickingRecordRequestDto());
        var manager = _capturedPicker!;

        Assert.Equal("u-staff", staff.UserGuid);
        Assert.Equal("u-staff", staff.AuthUserGuid);
        Assert.False(staff.CanOverwriteMinOrderQuantity);
        Assert.True(manager.CanOverwriteMinOrderQuantity);
        Assert.Equal("Full u-mgr", manager.Name);
    }

    private WarehousePickingController CreateController(ClaimsPrincipal? user, Dictionary<string, string> headers)
    {
        var httpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) };
        foreach (var (name, value) in headers)
        {
            httpContext.Request.Headers[name] = value;
        }

        return new WarehousePickingController(
            _picking.Object,
            _pickers.Object,
            _assignments.Object,
            _tickets,
            _devices.Object,
            _authorization.Object,
            NullLogger<WarehousePickingController>.Instance
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static Dictionary<string, string> DeviceHeaders() => new()
    {
        ["X-Device-Id"] = "DEV-1",
        ["X-Auth-Code"] = "AUTH",
    };

    private static ClaimsPrincipal User(string userGuid, string role) =>
        new(new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userGuid),
                new Claim(ClaimTypes.Name, userGuid),
                new Claim("fullName", $"Full {userGuid}"),
                new Claim(ClaimTypes.Role, role),
            },
            "TestAuth"
        ));

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 200,
        StatusCodeResult statusCodeResult => statusCodeResult.StatusCode,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private static string? ErrorCodeOf(IActionResult result)
    {
        var value = (result as ObjectResult)?.Value;
        return value?.GetType().GetProperty("errorCode")?.GetValue(value) as string;
    }
}
