using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Features.Packing;
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

    // ---- 拣货与装箱联动（二期）的新端点 ----

    [Fact]
    public async Task 装箱进度只需终端校验_认得出拣货人才传入_认不出不报错()
    {
        WarehousePickerContext? captured = new("unset", "unset", false, null, null);
        _picking
            .Setup(service => service.GetPackProgressAsync(OrderGuid, It.IsAny<WarehousePickerContext?>()))
            .Callback<string, WarehousePickerContext?>((_, picker) => captured = picker)
            .ReturnsAsync(WarehousePickingResult<WarehousePickingPackProgressDto>.Ok(new WarehousePickingPackProgressDto()));
        var (ticket, _) = _tickets.Issue(new WarehousePickerTicketIdentity("u-mia", "Mia Wong", false), "hw:DEV-1", DateTime.UtcNow);
        var withTicket = DeviceHeaders();
        withTicket[WarehousePickingController.PickerTicketHeader] = ticket;

        var withoutPicker = await CreateController(user: null, headers: DeviceHeaders()).GetPackProgress(OrderGuid);
        Assert.Equal(200, StatusOf(withoutPicker));
        Assert.Null(captured);

        var withPicker = await CreateController(user: null, withTicket).GetPackProgress(OrderGuid);
        Assert.Equal(200, StatusOf(withPicker));
        Assert.Equal("u-mia", captured!.UserGuid);

        var anonymous = await CreateController(user: null, headers: new()).GetPackProgress(OrderGuid);
        var notWarehouse = await CreateController(User("u-store", "StoreStaff"), headers: new()).GetPackProgress(OrderGuid);
        Assert.Equal(401, StatusOf(anonymous));
        Assert.Equal(403, StatusOf(notWarehouse));
        Assert.Equal(WarehousePickingErrorCodes.PickerNotAllowed, ErrorCodeOf(notWarehouse));
    }

    [Fact]
    public async Task 新建箱_封箱_先拣后装都要先确认拣货人_设备会话没有凭证时被拒绝且不调用服务()
    {
        var device = DeviceHeaders();

        var create = await CreateController(user: null, device).CreateCarton(OrderGuid, new WarehousePickingCreateCartonRequestDto());
        var seal = await CreateController(user: null, device).SealCarton(OrderGuid, "c-1", new WarehousePickingSealCartonRequestDto());
        var item = await CreateController(user: null, device).SetCartonItem(OrderGuid, "c-1", "P-1", new WarehousePickingSetCartonItemRequestDto { Quantity = 3 });
        var anonymous = await CreateController(user: null, headers: new()).CreateCarton(OrderGuid, null);
        var notWarehouse = await CreateController(User("u-store", "StoreStaff"), headers: new()).SealCarton(OrderGuid, "c-1", null);

        foreach (var result in new[] { create, seal, item })
        {
            Assert.Equal(403, StatusOf(result));
            Assert.Equal(WarehousePickingErrorCodes.PickerRequired, ErrorCodeOf(result));
        }

        Assert.Equal(401, StatusOf(anonymous));
        Assert.Equal(WarehousePickingErrorCodes.PickerNotAllowed, ErrorCodeOf(notWarehouse));
        _picking.Verify(service => service.CreateCartonAsync(It.IsAny<string>(), It.IsAny<WarehousePickingCreateCartonRequestDto>(), It.IsAny<WarehousePickerContext>()), Times.Never);
        _picking.Verify(service => service.SealCartonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WarehousePickingSealCartonRequestDto>(), It.IsAny<WarehousePickerContext>()), Times.Never);
        _picking.Verify(service => service.SetCartonItemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WarehousePickingSetCartonItemRequestDto>(), It.IsAny<WarehousePickerContext>()), Times.Never);
    }

    [Fact]
    public async Task 新建箱_封箱_先拣后装按凭证里的拣货人调用服务_订单号箱号去首尾空格_没带请求体按默认值处理()
    {
        WarehousePickerContext? picker = null;
        string? orderGuid = null;
        string? cartonGuid = null;
        string? productCode = null;
        WarehousePickingSealCartonRequestDto? sealRequest = null;
        _picking
            .Setup(service => service.CreateCartonAsync(It.IsAny<string>(), It.IsAny<WarehousePickingCreateCartonRequestDto>(), It.IsAny<WarehousePickerContext>()))
            .Callback<string, WarehousePickingCreateCartonRequestDto, WarehousePickerContext>((order, _, who) => { orderGuid = order; picker = who; })
            .ReturnsAsync(WarehousePickingResult<PackingCartonDetailDto>.Ok(new PackingCartonDetailDto { CartonGuid = "c-1" }));
        _picking
            .Setup(service => service.SealCartonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WarehousePickingSealCartonRequestDto>(), It.IsAny<WarehousePickerContext>()))
            .Callback<string, string, WarehousePickingSealCartonRequestDto, WarehousePickerContext>((_, carton, request, who) => { cartonGuid = carton; sealRequest = request; picker = who; })
            .ReturnsAsync(WarehousePickingResult<PackingCartonDetailDto>.Ok(new PackingCartonDetailDto { CartonGuid = "c-1" }));
        _picking
            .Setup(service => service.SetCartonItemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WarehousePickingSetCartonItemRequestDto>(), It.IsAny<WarehousePickerContext>()))
            .Callback<string, string, string, WarehousePickingSetCartonItemRequestDto, WarehousePickerContext>((_, carton, product, _, who) => { cartonGuid = carton; productCode = product; picker = who; })
            .ReturnsAsync(WarehousePickingResult<WarehousePickingSetCartonItemResultDto>.Ok(new WarehousePickingSetCartonItemResultDto()));
        var (ticket, _) = _tickets.Issue(new WarehousePickerTicketIdentity("u-mia", "Mia Wong", false), "hw:DEV-1", DateTime.UtcNow);
        var headers = DeviceHeaders();
        headers[WarehousePickingController.PickerTicketHeader] = ticket;

        var create = await CreateController(user: null, headers).CreateCarton(" order-1 ", null);
        Assert.Equal(200, StatusOf(create));
        Assert.Equal("order-1", orderGuid);
        Assert.Equal("u-mia", picker!.UserGuid);
        Assert.Equal("PDA-07", picker.DeviceCode);

        var seal = await CreateController(user: null, headers).SealCarton(OrderGuid, " c-1 ", null);
        Assert.Equal(200, StatusOf(seal));
        Assert.Equal("c-1", cartonGuid);
        Assert.Null(sealRequest!.PalletGuid);
        Assert.Null(sealRequest.WeightKg);

        picker = null;
        var item = await CreateController(user: null, headers).SetCartonItem(OrderGuid, "c-1", "P-1", new WarehousePickingSetCartonItemRequestDto { Quantity = 3 });
        Assert.Equal(200, StatusOf(item));
        Assert.Equal("P-1", productCode);
        Assert.Equal("u-mia", picker!.UserGuid);
    }

    [Fact]
    public async Task 先拣后装没带请求体_400_不会当成数量0把商品从箱里移除()
    {
        var result = await CreateController(User("u-staff", "WarehouseStaff"), headers: new()).SetCartonItem(OrderGuid, "c-1", "P-1", null);

        Assert.Equal(400, StatusOf(result));
        Assert.Equal(WarehousePickingErrorCodes.InvalidRequest, ErrorCodeOf(result));
        _picking.Verify(service => service.SetCartonItemAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WarehousePickingSetCartonItemRequestDto>(), It.IsAny<WarehousePickerContext>()), Times.Never);
    }

    [Fact]
    public async Task 箱操作失败原样映射状态码错误码与数据_拣货记录与改总数请求把箱号传给服务()
    {
        _picking
            .Setup(service => service.SealCartonAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WarehousePickingSealCartonRequestDto>(), It.IsAny<WarehousePickerContext>()))
            .ReturnsAsync(WarehousePickingResult<PackingCartonDetailDto>.Fail(409, PackingErrorCodes.PalletNotBuilding, "该托盘已封托"));
        WarehousePickingRecordRequestDto? record = null;
        WarehousePickingSetTotalRequestDto? total = null;
        _picking
            .Setup(service => service.AppendRecordAsync(It.IsAny<string>(), It.IsAny<WarehousePickingRecordRequestDto>(), It.IsAny<WarehousePickerContext>()))
            .Callback<string, WarehousePickingRecordRequestDto, WarehousePickerContext>((_, request, _) => record = request)
            .ReturnsAsync(WarehousePickingResult<WarehousePickingLineMutationDto>.Fail(
                409,
                PackingErrorCodes.CartonQtyInsufficient,
                "这只箱里该商品只有 6 件，不够减 12 件",
                new WarehousePickingLineMutationDto { Carton = new WarehousePickingCartonSyncDto { ItemPieces = 6 } }
            ));
        _picking
            .Setup(service => service.SetLineTotalAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<WarehousePickingSetTotalRequestDto>(), It.IsAny<WarehousePickerContext>()))
            .Callback<string, string, WarehousePickingSetTotalRequestDto, WarehousePickerContext>((_, _, request, _) => total = request)
            .ReturnsAsync(WarehousePickingResult<WarehousePickingLineMutationDto>.Ok(new WarehousePickingLineMutationDto()));
        var staff = User("u-staff", "WarehouseStaff");

        var sealResult = await CreateController(staff, headers: new()).SealCarton(OrderGuid, "c-1", new WarehousePickingSealCartonRequestDto { PalletGuid = "p-1" });
        var append = await CreateController(staff, headers: new()).AppendRecord(OrderGuid, new WarehousePickingRecordRequestDto { CartonGuid = "c-1" });
        var setTotal = await CreateController(staff, headers: new()).SetLineTotal(OrderGuid, "d-1", new WarehousePickingSetTotalRequestDto { CartonGuid = "c-2" });

        Assert.Equal(409, StatusOf(sealResult));
        Assert.Equal(PackingErrorCodes.PalletNotBuilding, ErrorCodeOf(sealResult));
        Assert.Equal(409, StatusOf(append));
        Assert.Equal(PackingErrorCodes.CartonQtyInsufficient, ErrorCodeOf(append));
        var data = ((ObjectResult)append).Value!.GetType().GetProperty("data")!.GetValue(((ObjectResult)append).Value) as WarehousePickingLineMutationDto;
        Assert.Equal(6, data!.Carton!.ItemPieces);
        Assert.Equal("c-1", record!.CartonGuid);
        Assert.Equal(200, StatusOf(setTotal));
        Assert.Equal("c-2", total!.CartonGuid);
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
