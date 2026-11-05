using System.Text.Json;
using Hbpos.Api.Services;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.Extensions.Configuration;

namespace Hbpos.Api.Tests;

public sealed class InstallmentRepaymentClaimContractsTests
{
    [Fact]
    public void Release_and_development_enable_required_cross_device_claims()
    {
        var releaseConfiguration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        var releaseOptions = new InstallmentRepaymentClaimOptions();
        releaseConfiguration.GetSection("InstallmentRepaymentClaims").Bind(releaseOptions);

        var developmentConfiguration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();
        var developmentOptions = new InstallmentRepaymentClaimOptions();
        developmentConfiguration.GetSection("InstallmentRepaymentClaims").Bind(developmentOptions);

        Assert.True(releaseOptions.Required);
        Assert.True(releaseOptions.CrossDeviceEnabled);
        Assert.True(developmentOptions.Required);
        Assert.True(developmentOptions.CrossDeviceEnabled);
    }

    [Fact]
    public void Capabilities_expose_locked_repayment_claim_contract()
    {
        var capabilities = new InstallmentRepaymentCapabilitiesResponse(
            RepaymentClaimsSupported: true,
            RepaymentClaimsRequired: false,
            CrossDeviceRepaymentEnabled: false,
            PreparedClaimTtlSeconds: 120,
            RepaymentClaimPrepareProviderV1: true);

        Assert.True(capabilities.RepaymentClaimsSupported);
        Assert.False(capabilities.RepaymentClaimsRequired);
        Assert.False(capabilities.CrossDeviceRepaymentEnabled);
        Assert.False(capabilities.CardRepaymentSupported);
        Assert.Equal(120, capabilities.PreparedClaimTtlSeconds);
        Assert.True(capabilities.RepaymentClaimPrepareProviderV1);

        var json = JsonSerializer.Serialize(capabilities, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"repaymentClaimPrepareProviderV1\":true", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Square", "square")]
    [InlineData("SQUARE_TERMINAL", "square")]
    [InlineData("Linkly", "linkly")]
    [InlineData("linkly-cloud", "linkly")]
    [InlineData("ANZ", "linkly")]
    [InlineData("Zeller", "zeller")]
    [InlineData("Manual", null)]
    [InlineData("cash", null)]
    [InlineData("  ", null)]
    public void Card_repayment_provider_families_normalize_claim_and_evidence_names(string value, string? expected)
    {
        Assert.Equal(expected, InstallmentCardRepaymentProviders.NormalizeFamily(value));
    }

    [Fact]
    public void Card_repayment_provider_list_parses_comma_separated_config_in_fixed_order()
    {
        Assert.Empty(InstallmentCardRepaymentProviders.ParseList(null));
        Assert.Empty(InstallmentCardRepaymentProviders.ParseList(" "));
        Assert.Equal(
            ["square", "linkly", "zeller"],
            InstallmentCardRepaymentProviders.ParseList("zeller,LINKLY, square,square,tyro"));
    }

    [Fact]
    public void Prepare_provider_request_contains_only_the_locked_payment_and_provider_facts()
    {
        var paymentGuid = Guid.NewGuid();
        var request = new InstallmentRepaymentClaimPrepareProviderRequest(
            paymentGuid,
            12.34m,
            PaymentMethodKind.Cash,
            "installment-action-1",
            "cash",
            "attempt-1");

        Assert.Equal(paymentGuid, request.PaymentGuid);
        Assert.Equal(12.34m, request.Amount);
        Assert.Equal(PaymentMethodKind.Cash, request.Method);
        Assert.Equal("installment-action-1", request.IdempotencyKey);
        Assert.Equal("cash", request.Provider);
        Assert.Equal("attempt-1", request.ProviderAttemptId);
        Assert.DoesNotContain(
            typeof(InstallmentRepaymentClaimPrepareProviderRequest).GetProperties(),
            property => property.Name is "StoreCode" or "DeviceCode" or "CashierId" or "CashierName" or "OperationGuid");
    }

    [Fact]
    public void Resolve_request_keeps_legacy_defaults_and_serializes_cash_release_evidence_in_camel_case()
    {
        var legacy = new InstallmentRepaymentClaimResolveRequest(
            InstallmentRepaymentClaimResolveOutcome.Released);
        Assert.False(legacy.CashNotCollectedConfirmed);
        Assert.Null(legacy.ProviderAttemptId);

        var request = legacy with
        {
            CashNotCollectedConfirmed = true,
            ProviderAttemptId = "cash-attempt-1"
        };
        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"cashNotCollectedConfirmed\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"providerAttemptId\":\"cash-attempt-1\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Claim_contract_has_locked_states_and_identity_free_request_body()
    {
        var operationGuid = Guid.NewGuid();
        var paymentGuid = Guid.NewGuid();
        var request = new InstallmentRepaymentClaimCreateRequest(
            operationGuid,
            paymentGuid,
            12.34m,
            PaymentMethodKind.Card,
            "installment-action-1");

        Assert.Equal(operationGuid, request.OperationGuid);
        Assert.Equal(paymentGuid, request.PaymentGuid);
        Assert.Equal(
        [
            InstallmentRepaymentClaimStatus.Prepared,
            InstallmentRepaymentClaimStatus.ProviderPending,
            InstallmentRepaymentClaimStatus.Committed,
            InstallmentRepaymentClaimStatus.Released,
            InstallmentRepaymentClaimStatus.Declined,
            InstallmentRepaymentClaimStatus.Unknown
        ],
            Enum.GetValues<InstallmentRepaymentClaimStatus>());
        Assert.DoesNotContain(
            typeof(InstallmentRepaymentClaimCreateRequest).GetProperties(),
            property => property.Name is "StoreCode" or "DeviceCode" or "CashierId" or "CashierName");
    }
}
