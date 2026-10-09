using System.Security.Claims;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Security;
using Hbpos.Api.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;

namespace Hbpos.Api.Auth;

public static class CashierAuthorizationPolicies
{
    public const string OrderSync = "Cashier.OrderSync";
    public const string History = "Cashier.History";
    public const string Returns = "Cashier.Returns";
    public const string Voucher = "Cashier.Voucher";
    public const string VoucherRefund = "Cashier.VoucherRefund";
    public const string CardRefund = "Cashier.CardRefund";
    public const string InstallmentView = "Cashier.InstallmentView";
    public const string InstallmentCreate = "Cashier.InstallmentCreate";
    public const string InstallmentPayment = "Cashier.InstallmentPayment";
    public const string InstallmentPickup = "Cashier.InstallmentPickup";
    public const string InstallmentCancel = "Cashier.InstallmentCancel";
    public const string InstallmentAmendLines = "Cashier.InstallmentAmendLines";
    public const string TakeCard = "Cashier.TakeCard";
    public const string DailyCloseSave = "Cashier.DailyCloseSave";
    public const string DailyClosePrint = "Cashier.DailyClosePrint";
    public const string PaymentSettings = "Cashier.PaymentSettings";
    public const string PaymentTerminalSelection = "Cashier.PaymentTerminalSelection";
    public const string SpecialProductsView = "Cashier.SpecialProductsView";
    public const string SpecialProductsManage = "Cashier.SpecialProductsManage";
    public const string DeviceRegistration = "Cashier.DeviceRegistration";
    public const string RemoteMaintenance = "Cashier.RemoteMaintenance";
    public const string DeviceRegistrationReset = "Cashier.DeviceRegistrationReset";
    public const string OperationAuditView = "Cashier.OperationAuditView";
    public const string HoldOrder = "Cashier.HoldOrder";
    public const string RecallOrder = "Cashier.RecallOrder";
    public const string HistoryRecall = "Cashier.HistoryRecall";
    public const string ReceiptPrinter = "Cashier.ReceiptPrinter";

    public static void AddPolicies(AuthorizationOptions options)
    {
        Add(options, OrderSync, Permissions.PosTerminal.Payment.Confirm, Permissions.PosTerminal.System.Sync);
        Add(options, History, Permissions.PosTerminal.History.View);
        Add(options, Returns, Permissions.PosTerminal.Returns.Confirm);
        Add(options, Voucher, Permissions.PosTerminal.Payment.TakeVoucher);
        Add(options, VoucherRefund,
            Permissions.PosTerminal.Returns.Confirm,
            Permissions.PosTerminal.Installments.Cancel);
        // 银行卡原路退款（Linkly TxnType=R）与礼券退款同口径：退货确认或分期取消权限之一；
        // 该策略不能挂在 TakeCard 端点的 Authorize 特性上，由控制器在识别出 R 交易后动态校验。
        Add(options, CardRefund,
            Permissions.PosTerminal.Returns.Confirm,
            Permissions.PosTerminal.Installments.Cancel);
        Add(options, InstallmentView, Permissions.PosTerminal.Installments.View);
        Add(options, InstallmentCreate, Permissions.PosTerminal.Installments.Create);
        Add(options, InstallmentPayment, Permissions.PosTerminal.Installments.AddRepayment);
        Add(options, InstallmentPickup, Permissions.PosTerminal.Installments.ConfirmPickup);
        Add(options, InstallmentCancel, Permissions.PosTerminal.Installments.Cancel);
        Add(options, InstallmentAmendLines, Permissions.PosTerminal.Installments.AmendLines);
        AddAll(options, TakeCard,
            Permissions.PosTerminal.Payment.TakeCard,
            Permissions.PosTerminal.Payment.Confirm);
        Add(options, DailyCloseSave, Permissions.PosTerminal.DailyClose.Save);
        // 结算回单的首次打印与重打均可记录，保持与日结功能的最小授权一致。
        Add(options, DailyClosePrint,
            Permissions.PosTerminal.DailyClose.Save,
            Permissions.PosTerminal.DailyClose.Reprint);
        Add(options, PaymentSettings, Permissions.PosTerminal.Settings.PaymentTerminal);
        AddPaymentTerminalSelection(options);
        Add(options, SpecialProductsView, Permissions.PosTerminal.SpecialProducts.View);
        Add(options, SpecialProductsManage, Permissions.PosTerminal.SpecialProducts.Manage);
        Add(options, DeviceRegistration, Permissions.PosTerminal.Settings.DeviceRegistration);
        // 远程维护面向已登录 cashier，但不要求可委派的设备设置权限；ticket 仍必须绑定当前设备。
        AddCashierTicketOnly(options, RemoteMaintenance);
        AddFreshEmployee(
            options,
            DeviceRegistrationReset,
            TimeSpan.FromMinutes(2),
            Permissions.PosTerminal.Settings.DeviceRegistration);
        Add(options, OperationAuditView, Permissions.PosTerminal.Audit.View);
        Add(options, HoldOrder, Permissions.PosTerminal.Sales.HoldOrder);
        Add(options, RecallOrder, Permissions.PosTerminal.Sales.RecallOrder);
        Add(options, HistoryRecall, Permissions.PosTerminal.History.Recall);
        Add(options, ReceiptPrinter, Permissions.PosTerminal.Settings.ReceiptPrinter);
    }

    private static void Add(AuthorizationOptions options, string name, params string[] permissions)
    {
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new CashierPermissionRequirement(permissions));
        });
    }

    private static void AddAll(AuthorizationOptions options, string name, params string[] permissions)
    {
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            // 关键逻辑：每个权限作为独立 requirement，确保刷卡入口同时具备收卡与确认权限。
            foreach (var permission in permissions)
            {
                policy.AddRequirements(new CashierPermissionRequirement([permission]));
            }
        });
    }

    private static void AddPaymentTerminalSelection(AuthorizationOptions options)
    {
        options.AddPolicy(PaymentTerminalSelection, policy =>
        {
            policy.RequireAuthenticatedUser();
            // 两个 requirement 共同表达：设置权限，或同时具备收卡与确认权限。
            policy.AddRequirements(
                new CashierPermissionRequirement(
                [
                    Permissions.PosTerminal.Settings.PaymentTerminal,
                    Permissions.PosTerminal.Payment.TakeCard
                ]),
                new CashierPermissionRequirement(
                [
                    Permissions.PosTerminal.Settings.PaymentTerminal,
                    Permissions.PosTerminal.Payment.Confirm
                ]));
        });
    }

    private static void AddFreshEmployee(
        AuthorizationOptions options,
        string name,
        TimeSpan maximumTicketAge,
        params string[] permissions)
    {
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new CashierPermissionRequirement(
                permissions,
                RequireFreshOnlineTicket: true,
                RequireActiveEmployee: true,
                MaximumTicketAge: maximumTicketAge));
        });
    }

    private static void AddCashierTicketOnly(AuthorizationOptions options, string name)
    {
        options.AddPolicy(name, policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new CashierPermissionRequirement([], RequireCashierTicket: true, RequireHardwareBoundTicket: true));
        });
    }
}

public sealed record CashierPermissionRequirement(
    string[] PermissionCodes,
    bool RequireFreshOnlineTicket = false,
    bool RequireActiveEmployee = false,
    TimeSpan? MaximumTicketAge = null,
    bool RequireCashierTicket = false,
    bool RequireHardwareBoundTicket = false) : IAuthorizationRequirement;

public static class CashierAuthorizationContext
{
    public const string CashierIdItemKey = "Hbpos.AuthorizedCashierId";
}

public sealed class CashierPermissionAuthorizationHandler(
    IHttpContextAccessor httpContextAccessor,
    ICashierAuthorizationTicketService ticketService,
    IConfiguration? configuration = null,
    ILogger<CashierPermissionAuthorizationHandler>? logger = null) : AuthorizationHandler<CashierPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CashierPermissionRequirement requirement)
    {
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        var deviceStoreCode = context.User.FindFirstValue(DeviceAuthConstants.StoreCodeClaim);
        var deviceCode = context.User.FindFirstValue(DeviceAuthConstants.DeviceCodeClaim);
        var deviceHardwareId = context.User.FindFirstValue(DeviceAuthConstants.HardwareIdClaim);
        // 高危操作必须有可核验的设备硬件身份；缺失时直接拒绝，避免继续进入员工校验分支。
        if (requirement.RequireActiveEmployee && string.IsNullOrWhiteSpace(deviceHardwareId))
        {
            return;
        }

        IPosIpadAppReviewAuthorizationBoundary? appReviewBoundary = null;
        bool? isAppReviewDevice = null;

        async Task<bool> IsAppReviewDeviceAsync()
        {
            if (isAppReviewDevice is not null)
            {
                return isAppReviewDevice.Value;
            }

            if (string.IsNullOrWhiteSpace(deviceStoreCode)
                || string.IsNullOrWhiteSpace(deviceCode)
                || string.IsNullOrWhiteSpace(deviceHardwareId))
            {
                isAppReviewDevice = false;
                return false;
            }

            appReviewBoundary = httpContext.RequestServices
                .GetRequiredService<IPosIpadAppReviewAuthorizationBoundary>();
            isAppReviewDevice = await appReviewBoundary.IsReviewDeviceAsync(
                deviceStoreCode,
                deviceCode,
                deviceHardwareId,
                httpContext.RequestAborted);
            return isAppReviewDevice.Value;
        }

        var token = httpContext.Request.Headers[CashierAuthorizationConstants.HeaderName].ToString();
        var ticket = ticketService.Validate(token);
        if (ticket is not null &&
            string.Equals(ticket.StoreCode, deviceStoreCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ticket.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase) &&
            (!requirement.RequireFreshOnlineTicket ||
             (!string.IsNullOrWhiteSpace(ticket.HardwareId) &&
              string.Equals(ticket.HardwareId, deviceHardwareId, StringComparison.Ordinal))) &&
            (!requirement.RequireHardwareBoundTicket ||
             (!string.IsNullOrWhiteSpace(ticket.HardwareId) &&
              string.Equals(ticket.HardwareId, deviceHardwareId, StringComparison.Ordinal))))
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var maximumTicketAge = requirement.MaximumTicketAge ?? TimeSpan.FromMinutes(2);
            var ticketIsFresh = !requirement.RequireFreshOnlineTicket
                || (ticket.BarcodeAuthenticatedAtUtc is { } barcodeAuthenticatedAtUtc
                    && barcodeAuthenticatedAtUtc <= nowUtc.AddSeconds(30)
                    && barcodeAuthenticatedAtUtc >= nowUtc.Subtract(maximumTicketAge));
            if (!ticketIsFresh)
            {
                return;
            }

            // 只有真正校验收银员票据时才解析数据库服务，普通设备认证端点不会提前连接数据库。
            var cashierService = httpContext!.RequestServices.GetRequiredService<ICashierService>();
            var hasPermission = requirement.RequireCashierTicket || await cashierService.HasAnyPermissionAsync(
                    ticket.UserGuid,
                    ticket.StoreCode,
                    requirement.PermissionCodes,
                    httpContext.RequestAborted);
            var reviewDevice = hasPermission && await IsAppReviewDeviceAsync();
            var mustBeActiveEmployee = requirement.RequireActiveEmployee || reviewDevice;
            var hasActiveEmployeeIdentity = hasPermission && (!mustBeActiveEmployee
                || await (appReviewBoundary ??= httpContext.RequestServices
                    .GetRequiredService<IPosIpadAppReviewAuthorizationBoundary>())
                    .IsActiveEmployeeCashierAsync(
                    ticket.CashierId,
                    ticket.UserGuid,
                    httpContext.RequestAborted));
            if (hasPermission && hasActiveEmployeeIdentity)
            {
                // 关键逻辑：敏感业务字段必须使用已验票身份，不能继续信任客户端快照中的 CashierId。
                httpContext.Items[CashierAuthorizationContext.CashierIdItemKey] = ticket.CashierId;
                context.Succeed(requirement);
                return;
            }
        }

        if (!requirement.RequireCashierTicket
            && !requirement.RequireFreshOnlineTicket
            && EmergencyLoginTokenCodec.HasSupportedPrefix(token)
            && !await IsAppReviewDeviceAsync())
        {
            // 仅紧急二维码才解析摘要数据库服务，缺失票据不会让普通设备请求提前连接数据库。
            var emergencyGrantService = httpContext.RequestServices
                .GetRequiredService<IEmergencyGrantAuthorizationService>();
            var emergency = await emergencyGrantService.ValidateAsync(
                token,
                deviceStoreCode ?? string.Empty,
                httpContext.RequestAborted);
            if (emergency is not null)
            {
                httpContext.Items[CashierAuthorizationContext.CashierIdItemKey] =
                    $"EMERGENCY:{emergency.GrantId:N}";
                context.Succeed(requirement);
                return;
            }
        }

        var isAuditMode = string.Equals(
            configuration?["CashierAuthorization:Mode"],
            "Audit",
            StringComparison.OrdinalIgnoreCase);
        if (!requirement.RequireCashierTicket
            && !requirement.RequireFreshOnlineTicket
            && isAuditMode
            && !await IsAppReviewDeviceAsync())
        {
            // 关键逻辑：仅 grant 消费表中的审核设备强制真实员工票据；同店既有设备保持 Audit 兼容。
            logger?.LogWarning(
                "Cashier authorization audit bypass store={StoreCode} device={DeviceCode} permissions={Permissions}",
                deviceStoreCode,
                deviceCode,
                string.Join(",", requirement.PermissionCodes));
            context.Succeed(requirement);
        }
    }
}
