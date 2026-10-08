using System.Security.Claims;
using System.Text.Json;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.Security;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Devices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hbpos.Api.Tests;

public sealed class CashierPermissionAuthorizationTests
{
    [Fact]
    public async Task Handler_rechecks_live_permission_for_device_bound_ticket()
    {
        var cashierService = new FakeCashierService(true);
        var httpContext = CreateHttpContext("ticket", cashierService, new FakeEmergencyGrantService(null));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "C001", "U001", "S001", "POS-01", DateTimeOffset.UtcNow.AddHours(1))));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal(Permissions.PosTerminal.Payment.Confirm, Assert.Single(cashierService.CheckedPermissions));
        Assert.Equal("C001", httpContext.Items[CashierAuthorizationContext.CashierIdItemKey]);
    }

    [Fact]
    public async Task Handler_rejects_ticket_from_another_device_before_database_check()
    {
        var cashierService = new FakeCashierService(true);
        var httpContext = CreateHttpContext("ticket", cashierService, new FakeEmergencyGrantService(null));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "C001", "U001", "S001", "POS-02", DateTimeOffset.UtcNow.AddHours(1))));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Empty(cashierService.CheckedPermissions);
    }

    [Theory]
    [InlineData("HBPOSE1-test-token")]
    [InlineData("HBPOSE2-test-token")]
    public async Task Handler_accepts_both_emergency_token_versions_without_cashier_database_identity(string token)
    {
        var cashierService = new FakeCashierService(false);
        var emergencyGrantService = new FakeEmergencyGrantService(new EmergencyLoginVerifiedClaims(
            Guid.NewGuid(),
            "S001",
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddHours(1)));
        var httpContext = CreateHttpContext(token, cashierService, emergencyGrantService);
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Returns.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(null));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_audit_mode_records_boundary_without_blocking_device_authenticated_request()
    {
        var httpContext = CreateHttpContext(
            string.Empty,
            new FakeCashierService(false),
            new FakeEmergencyGrantService(null));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierAuthorization:Mode"] = "Audit"
            })
            .Build();
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(null),
            configuration);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_audit_mode_does_not_bypass_missing_cashier_ticket_for_app_review_store()
    {
        var httpContext = CreateHttpContext(
            string.Empty,
            new FakeCashierService(false),
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: true, activeEmployeeCashier: true));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierAuthorization:Mode"] = "Audit",
                ["PosIpadAppReview:Enabled"] = "true",
                ["PosIpadAppReview:StoreCode"] = "S001"
            })
            .Build();
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(null),
            configuration);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("S002", "true", null)]
    [InlineData("S001", "false", null)]
    [InlineData("S001", "true", "2026-08-17T00:00:00Z")]
    public async Task Handler_consumed_review_device_stays_strict_when_current_review_configuration_changes(
        string? configuredStoreCode,
        string? enabled,
        string? expiresAtUtc)
    {
        var httpContext = CreateHttpContext(
            string.Empty,
            new FakeCashierService(false),
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: true, activeEmployeeCashier: true));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierAuthorization:Mode"] = "Audit",
                ["PosIpadAppReview:StoreCode"] = configuredStoreCode,
                ["PosIpadAppReview:Enabled"] = enabled,
                ["PosIpadAppReview:ExpiresAtUtc"] = expiresAtUtc
            })
            .Build();
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(null),
            configuration);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_app_review_store_rejects_emergency_grant_and_requires_real_cashier_ticket()
    {
        var emergencyGrantService = new FakeEmergencyGrantService(new EmergencyLoginVerifiedClaims(
            Guid.NewGuid(),
            "S001",
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddHours(1)));
        var httpContext = CreateHttpContext(
            "HBPOSE2-test-token",
            new FakeCashierService(false),
            emergencyGrantService,
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: true, activeEmployeeCashier: true));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierAuthorization:Mode"] = "Audit",
                ["PosIpadAppReview:Enabled"] = "true",
                ["PosIpadAppReview:StoreCode"] = " S001 "
            })
            .Build();
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(null),
            configuration);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_existing_device_in_review_store_keeps_audit_compatibility()
    {
        var httpContext = CreateHttpContext(
            string.Empty,
            new FakeCashierService(false),
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: false, activeEmployeeCashier: false));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierAuthorization:Mode"] = "Audit",
                ["PosIpadAppReview:Enabled"] = "true",
                ["PosIpadAppReview:StoreCode"] = "S001"
            })
            .Build();
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(null),
            configuration);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Handler_review_device_requires_active_employee_cashier_identity(
        bool activeEmployeeCashier,
        bool expectedSuccess)
    {
        var cashierService = new FakeCashierService(true);
        var httpContext = CreateHttpContext(
            "ticket",
            cashierService,
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: true, activeEmployeeCashier));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierAuthorization:Mode"] = "Audit",
                ["PosIpadAppReview:StoreCode"] = "S001"
            })
            .Build();
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "EMPLOYEE-HGUID", "U001", "S001", "POS-01", DateTimeOffset.UtcNow.AddHours(1))),
            configuration);

        await handler.HandleAsync(context);

        Assert.Equal(expectedSuccess, context.HasSucceeded);
    }

    [Fact]
    public async Task Handler_audit_mode_keeps_bypass_for_non_review_store_when_review_gate_is_enabled()
    {
        var httpContext = CreateHttpContext(
            string.Empty,
            new FakeCashierService(false),
            new FakeEmergencyGrantService(null));
        var requirement = new CashierPermissionRequirement([Permissions.PosTerminal.Payment.Confirm]);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierAuthorization:Mode"] = "Audit",
                ["PosIpadAppReview:Enabled"] = "true",
                ["PosIpadAppReview:StoreCode"] = "S002"
            })
            .Build();
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(null),
            configuration);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public void Take_card_policy_requires_take_card_and_confirm_permissions_separately()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);

        var policy = options.GetPolicy(CashierAuthorizationPolicies.TakeCard);
        Assert.NotNull(policy);
        var requirements = policy.Requirements
            .OfType<CashierPermissionRequirement>()
            .ToArray();

        Assert.Equal(2, requirements.Length);
        Assert.Contains(requirements, requirement =>
            requirement.PermissionCodes.SequenceEqual([Permissions.PosTerminal.Payment.TakeCard]));
        Assert.Contains(requirements, requirement =>
            requirement.PermissionCodes.SequenceEqual([Permissions.PosTerminal.Payment.Confirm]));
    }

    [Fact]
    public async Task Device_reset_policy_requires_fresh_real_employee_ticket_even_in_audit_mode()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);
        var policy = options.GetPolicy(CashierAuthorizationPolicies.DeviceRegistrationReset);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<CashierPermissionRequirement>());
        Assert.True(requirement.RequireFreshOnlineTicket);
        Assert.True(requirement.RequireActiveEmployee);
        Assert.Equal(TimeSpan.FromMinutes(2), requirement.MaximumTicketAge);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CashierAuthorization:Mode"] = "Audit"
            })
            .Build();
        var httpContext = CreateHttpContext(
            "ticket",
            new FakeCashierService(true),
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: false, activeEmployeeCashier: true));
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "EMPLOYEE-HGUID",
                "U001",
                "S001",
                "POS-01",
                DateTimeOffset.UtcNow.AddHours(1))
            {
                IssuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3),
                BarcodeAuthenticatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
            }),
            configuration);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Device_reset_policy_accepts_recent_device_bound_active_employee_ticket()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);
        var policy = options.GetPolicy(CashierAuthorizationPolicies.DeviceRegistrationReset);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<CashierPermissionRequirement>());
        var httpContext = CreateHttpContext(
            "ticket",
            new FakeCashierService(true),
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: false, activeEmployeeCashier: true));
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "EMPLOYEE-HGUID",
                "U001",
                "S001",
            "POS-01",
            DateTimeOffset.UtcNow.AddHours(1))
        {
            IssuedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-15),
            BarcodeAuthenticatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-15),
            HardwareId = "HW-01"
        }));

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
        Assert.Equal("EMPLOYEE-HGUID", httpContext.Items[CashierAuthorizationContext.CashierIdItemKey]);
    }

    [Fact]
    public async Task Device_reset_policy_rejects_refreshed_ticket_when_original_barcode_scan_is_stale()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);
        var policy = options.GetPolicy(CashierAuthorizationPolicies.DeviceRegistrationReset);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<CashierPermissionRequirement>());
        var httpContext = CreateHttpContext(
            "ticket",
            new FakeCashierService(true),
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: false, activeEmployeeCashier: true));
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "EMPLOYEE-HGUID",
                "U001",
                "S001",
                "POS-01",
                DateTimeOffset.UtcNow.AddHours(1),
                "HW-01")
            {
                IssuedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                BarcodeAuthenticatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3)
            }),
            new ConfigurationBuilder().Build());

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Device_reset_policy_rejects_legacy_emergency_inactive_and_unpermitted_authorizations()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);
        var policy = options.GetPolicy(CashierAuthorizationPolicies.DeviceRegistrationReset);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<CashierPermissionRequirement>());
        var recentTicket = new CashierAuthorizationTicket(
            "EMPLOYEE-HGUID",
            "U001",
            "S001",
            "POS-01",
            DateTimeOffset.UtcNow.AddHours(1))
        {
            IssuedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
            BarcodeAuthenticatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10)
        };
        var emergencyGrant = new EmergencyLoginVerifiedClaims(
            Guid.NewGuid(),
            "S001",
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddHours(1));
        var scenarios = new[]
        {
            new
            {
                Token = "ticket",
                Ticket = (CashierAuthorizationTicket?)new CashierAuthorizationTicket(
                    "EMPLOYEE-HGUID", "U001", "S001", "POS-01", DateTimeOffset.UtcNow.AddHours(1)),
                Permission = true,
                Active = true,
                Emergency = (EmergencyLoginVerifiedClaims?)null
            },
            new
            {
                Token = "HBPOSE2-test-token",
                Ticket = (CashierAuthorizationTicket?)null,
                Permission = true,
                Active = true,
                Emergency = (EmergencyLoginVerifiedClaims?)emergencyGrant
            },
            new
            {
                Token = "ticket",
                Ticket = (CashierAuthorizationTicket?)recentTicket,
                Permission = true,
                Active = false,
                Emergency = (EmergencyLoginVerifiedClaims?)null
            },
            new
            {
                Token = "ticket",
                Ticket = (CashierAuthorizationTicket?)recentTicket,
                Permission = false,
                Active = true,
                Emergency = (EmergencyLoginVerifiedClaims?)null
            }
        };

        foreach (var scenario in scenarios)
        {
            var httpContext = CreateHttpContext(
                scenario.Token,
                new FakeCashierService(scenario.Permission),
                new FakeEmergencyGrantService(scenario.Emergency),
                new FakeAppReviewAuthorizationBoundary(
                    isReviewDevice: false,
                    activeEmployeeCashier: scenario.Active));
            var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
            var handler = new CashierPermissionAuthorizationHandler(
                new HttpContextAccessor { HttpContext = httpContext },
                new FakeTicketService(scenario.Ticket));

            await handler.HandleAsync(context);

            Assert.False(context.HasSucceeded);
        }
    }

    [Fact]
    public async Task Device_reset_policy_rejects_ticket_from_the_same_device_code_on_different_hardware()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);
        var policy = options.GetPolicy(CashierAuthorizationPolicies.DeviceRegistrationReset);
        Assert.NotNull(policy);
        var ticket = JsonSerializer.Deserialize<CashierAuthorizationTicket>(
            JsonSerializer.Serialize(new
            {
                cashierId = "EMPLOYEE-HGUID",
                userGuid = "U001",
                storeCode = "S001",
                deviceCode = "POS-01",
                hardwareId = "HW-OTHER",
                issuedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
                expiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var httpContext = CreateHttpContext(
            "ticket",
            new FakeCashierService(true),
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: false, activeEmployeeCashier: true));
        var context = new AuthorizationHandlerContext(policy.Requirements, httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(ticket));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Require_active_employee_without_hardware_claim_fails_closed_without_throwing()
    {
        var httpContext = CreateHttpContext(
            "ticket",
            new FakeCashierService(true),
            new FakeEmergencyGrantService(null),
            new FakeAppReviewAuthorizationBoundary(isReviewDevice: false, activeEmployeeCashier: true));
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, "S001"),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01")
        ], "test"));
        var requirement = new CashierPermissionRequirement(
            [Permissions.PosTerminal.Settings.DeviceRegistration],
            RequireActiveEmployee: true);
        var context = new AuthorizationHandlerContext([requirement], httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "EMPLOYEE-HGUID", "U001", "S001", "POS-01", DateTimeOffset.UtcNow.AddHours(1))));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task Take_card_policy_rejects_ticket_with_only_take_card_permission()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);
        var policy = options.GetPolicy(CashierAuthorizationPolicies.TakeCard);
        Assert.NotNull(policy);
        var cashierService = new SelectiveCashierService(Permissions.PosTerminal.Payment.TakeCard);
        var httpContext = CreateHttpContext("ticket", cashierService, new FakeEmergencyGrantService(null));
        var context = new AuthorizationHandlerContext(policy.Requirements, httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "C001", "U001", "S001", "POS-01", DateTimeOffset.UtcNow.AddHours(1))));

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
        Assert.Contains(Permissions.PosTerminal.Payment.TakeCard, cashierService.CheckedPermissions);
        Assert.Contains(Permissions.PosTerminal.Payment.Confirm, cashierService.CheckedPermissions);
    }

    public static IEnumerable<object[]> PaymentTerminalSelectionPermissionCases()
    {
        yield return
        [
            new[] { Permissions.PosTerminal.Settings.PaymentTerminal },
            true
        ];
        yield return
        [
            new[]
            {
                Permissions.PosTerminal.Payment.TakeCard,
                Permissions.PosTerminal.Payment.Confirm
            },
            true
        ];
        yield return
        [
            new[] { Permissions.PosTerminal.Payment.TakeCard },
            false
        ];
        yield return
        [
            new[] { Permissions.PosTerminal.Payment.Confirm },
            false
        ];
        yield return [Array.Empty<string>(), false];
    }

    [Theory]
    [MemberData(nameof(PaymentTerminalSelectionPermissionCases))]
    public async Task Payment_terminal_selection_policy_allows_settings_or_complete_card_permissions(
        string[] grantedPermissions,
        bool expectedSuccess)
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);
        var policy = options.GetPolicy(CashierAuthorizationPolicies.PaymentTerminalSelection);
        Assert.NotNull(policy);
        var cashierService = new SelectiveCashierService(grantedPermissions);
        var httpContext = CreateHttpContext("ticket", cashierService, new FakeEmergencyGrantService(null));
        var permissionRequirements = policy.Requirements
            .OfType<CashierPermissionRequirement>()
            .ToArray();
        var context = new AuthorizationHandlerContext(permissionRequirements, httpContext.User, null);
        var handler = new CashierPermissionAuthorizationHandler(
            new HttpContextAccessor { HttpContext = httpContext },
            new FakeTicketService(new CashierAuthorizationTicket(
                "C001", "U001", "S001", "POS-01", DateTimeOffset.UtcNow.AddHours(1))));

        await handler.HandleAsync(context);

        Assert.Equal(expectedSuccess, context.HasSucceeded);
    }

    [Fact]
    public void Payment_terminal_selection_policy_encodes_settings_or_take_card_and_confirm()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);
        var policy = options.GetPolicy(CashierAuthorizationPolicies.PaymentTerminalSelection);
        Assert.NotNull(policy);
        var requirements = policy.Requirements
            .OfType<CashierPermissionRequirement>()
            .ToArray();

        Assert.Equal(2, requirements.Length);
        Assert.Contains(requirements, requirement => requirement.PermissionCodes.SequenceEqual(
        [
            Permissions.PosTerminal.Settings.PaymentTerminal,
            Permissions.PosTerminal.Payment.TakeCard
        ]));
        Assert.Contains(requirements, requirement => requirement.PermissionCodes.SequenceEqual(
        [
            Permissions.PosTerminal.Settings.PaymentTerminal,
            Permissions.PosTerminal.Payment.Confirm
        ]));
    }

    [Fact]
    public void Sensitive_policies_map_to_their_specific_pos_permissions()
    {
        var options = new AuthorizationOptions();
        CashierAuthorizationPolicies.AddPolicies(options);

        AssertPolicyPermissions(
            options,
            CashierAuthorizationPolicies.VoucherRefund,
            Permissions.PosTerminal.Returns.Confirm,
            Permissions.PosTerminal.Installments.Cancel);
        AssertPolicyPermissions(
            options,
            CashierAuthorizationPolicies.InstallmentView,
            Permissions.PosTerminal.Installments.View);
        AssertPolicyPermissions(
            options,
            CashierAuthorizationPolicies.InstallmentAmendLines,
            Permissions.PosTerminal.Installments.AmendLines);
        AssertPolicyPermissions(
            options,
            CashierAuthorizationPolicies.DeviceRegistration,
            Permissions.PosTerminal.Settings.DeviceRegistration);
        AssertPolicyPermissions(
            options,
            CashierAuthorizationPolicies.DailyCloseSave,
            Permissions.PosTerminal.DailyClose.Save);
        AssertPolicyPermissions(
            options,
            CashierAuthorizationPolicies.DailyClosePrint,
            Permissions.PosTerminal.DailyClose.Save,
            Permissions.PosTerminal.DailyClose.Reprint);

        Assert.Equal(
            CashierAuthorizationPolicies.InstallmentView,
            typeof(InstallmentsController).GetMethod(nameof(InstallmentsController.History))?
                .GetCustomAttributes(typeof(AuthorizeAttribute), false)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
        Assert.Equal(
            CashierAuthorizationPolicies.InstallmentView,
            typeof(InstallmentsController).GetMethod(nameof(InstallmentsController.Details))?
                .GetCustomAttributes(typeof(AuthorizeAttribute), false)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy);
    }

    private static void AssertPolicyPermissions(
        AuthorizationOptions options,
        string policyName,
        params string[] permissions)
    {
        var policy = options.GetPolicy(policyName);
        Assert.NotNull(policy);
        var requirement = Assert.Single(policy.Requirements.OfType<CashierPermissionRequirement>());
        Assert.Equal(permissions, requirement.PermissionCodes);
    }

    private static DefaultHttpContext CreateHttpContext(
        string token,
        ICashierService cashierService,
        IEmergencyGrantAuthorizationService emergencyGrantService,
        IPosIpadAppReviewAuthorizationBoundary? appReviewAuthorizationBoundary = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton(cashierService)
            .AddSingleton(emergencyGrantService)
            .AddSingleton(appReviewAuthorizationBoundary
                ?? new FakeAppReviewAuthorizationBoundary(false, false))
            .BuildServiceProvider();
        httpContext.Request.Headers[CashierAuthorizationConstants.HeaderName] = token;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, "S001"),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-01"),
            new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-01")
        ], "test"));
        return httpContext;
    }

    private sealed class FakeTicketService(CashierAuthorizationTicket? ticket)
        : ICashierAuthorizationTicketService
    {
        public (string Token, DateTimeOffset ExpiresAtUtc) Issue(
            string cashierId,
            string userGuid,
            string storeCode,
            string deviceCode) => throw new NotSupportedException();

        public CashierAuthorizationTicket? Validate(string? token) => token == "ticket" ? ticket : null;
    }

    private sealed class FakeCashierService(bool allowed) : ICashierService
    {
        public List<string> CheckedPermissions { get; } = [];

        public Task<CashierSessionDto?> BarcodeLoginAsync(
            CashierBarcodeLoginRequest request,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);

        public Task<CashierSessionDto?> RefreshSessionAsync(
            CashierAuthorizationTicket ticket,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);

        public Task<bool> HasAnyPermissionAsync(
            string userGuid,
            string storeCode,
            IReadOnlyCollection<string> permissionCodes,
            CancellationToken cancellationToken)
        {
            CheckedPermissions.AddRange(permissionCodes);
            return Task.FromResult(allowed);
        }
    }

    private sealed class SelectiveCashierService(params string[] allowedPermissions) : ICashierService
    {
        public List<string> CheckedPermissions { get; } = [];

        public Task<CashierSessionDto?> BarcodeLoginAsync(
            CashierBarcodeLoginRequest request,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);

        public Task<CashierSessionDto?> RefreshSessionAsync(
            CashierAuthorizationTicket ticket,
            CancellationToken cancellationToken) => Task.FromResult<CashierSessionDto?>(null);

        public Task<bool> HasAnyPermissionAsync(
            string userGuid,
            string storeCode,
            IReadOnlyCollection<string> permissionCodes,
            CancellationToken cancellationToken)
        {
            CheckedPermissions.AddRange(permissionCodes);
            return Task.FromResult(permissionCodes.Any(permission =>
                allowedPermissions.Contains(permission, StringComparer.OrdinalIgnoreCase)));
        }
    }

    private sealed class FakeEmergencyGrantService(EmergencyLoginVerifiedClaims? claims)
        : IEmergencyGrantAuthorizationService
    {
        public Task<EmergencyLoginVerifiedClaims?> ValidateAsync(
            string? token,
            string deviceStoreCode,
            CancellationToken cancellationToken) => Task.FromResult(claims);
    }

    private sealed class FakeAppReviewAuthorizationBoundary(
        bool isReviewDevice,
        bool activeEmployeeCashier) : IPosIpadAppReviewAuthorizationBoundary
    {
        public Task<bool> IsReviewDeviceAsync(
            string storeCode,
            string deviceCode,
            string hardwareId,
            CancellationToken cancellationToken) => Task.FromResult(isReviewDevice);

        public Task<bool> IsActiveEmployeeCashierAsync(
            string cashierId,
            string userGuid,
            CancellationToken cancellationToken) => Task.FromResult(activeEmployeeCashier);
    }
}
