using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using BlazorApp.Shared.Constants;
using Hbpos.Api.Auth;
using Hbpos.Api.Controllers;
using Hbpos.Api.Data;
using Hbpos.Api.Services;
using Hbpos.Contracts.Common;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Installments;
using Hbpos.Contracts.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;

namespace Hbpos.Api.Tests;

/// <summary>共享规则 <see cref="InstallmentAmendRules"/> 的纯逻辑测试（客户端编辑界面与服务端落库共用）。</summary>
public sealed class InstallmentAmendRulesTests
{
    [Theory]
    [InlineData(InstallmentStatus.Active, true)]
    [InlineData(InstallmentStatus.PaidOff, true)]
    [InlineData(InstallmentStatus.PickedUp, false)]
    [InlineData(InstallmentStatus.Cancelled, false)]
    public void CanAmend_only_allows_active_and_paid_off(InstallmentStatus status, bool expected)
    {
        Assert.Equal(expected, InstallmentAmendRules.CanAmend(status));
    }

    [Fact]
    public void Amounts_round_to_two_decimals_away_from_zero()
    {
        // 3 × 0.335 = 1.005，AwayFromZero 舍入为 1.01；再减折扣 0.01 得 1.00。
        Assert.Equal(1.01m, InstallmentAmendRules.CalculateActualAmount(3m, 0.335m, 0m));
        Assert.Equal(1.00m, InstallmentAmendRules.CalculateActualAmount(3m, 0.335m, 0.01m));
        Assert.Equal(
            30.01m,
            InstallmentAmendRules.CalculateTotal([Line(10.004m), Line(20.006m)]));
    }

    [Theory]
    [InlineData(100, 20, 80, InstallmentStatus.Active)]
    [InlineData(100, 100, 0, InstallmentStatus.PaidOff)]
    [InlineData(100, 120, 0, InstallmentStatus.PaidOff)]
    // 差额 0.004 舍入后为 0，仍视为已付清。
    [InlineData(100.004, 100, 0, InstallmentStatus.PaidOff)]
    public void Balance_and_status_follow_total_minus_paid(
        double total,
        double paid,
        double expectedBalance,
        InstallmentStatus expectedStatus)
    {
        Assert.Equal((decimal)expectedBalance, InstallmentAmendRules.CalculateBalance((decimal)total, (decimal)paid));
        Assert.Equal(expectedStatus, InstallmentAmendRules.ResolveStatus((decimal)total, (decimal)paid));
    }

    [Fact]
    public void ValidateLines_rejects_null_and_empty_lists()
    {
        Assert.Equal(InstallmentAmendLinesValidation.NoLines, InstallmentAmendRules.ValidateLines(null));
        Assert.Equal(InstallmentAmendLinesValidation.NoLines, InstallmentAmendRules.ValidateLines([]));
    }

    [Fact]
    public void ValidateLines_accepts_well_formed_lines()
    {
        Assert.Equal(
            InstallmentAmendLinesValidation.Valid,
            InstallmentAmendRules.ValidateLines([Line(60m), Line(40m, quantity: 2m, unitPrice: 25m, discount: 10m)]));
    }

    [Fact]
    public void ValidateLines_rejects_empty_or_duplicate_line_guid()
    {
        Assert.Equal(
            InstallmentAmendLinesValidation.DuplicateLine,
            InstallmentAmendRules.ValidateLines([Line(10m) with { InstallmentLineGuid = Guid.Empty }]));
        var duplicate = Line(10m);
        Assert.Equal(
            InstallmentAmendLinesValidation.DuplicateLine,
            InstallmentAmendRules.ValidateLines([duplicate, duplicate with { DisplayName = "Other" }]));
    }

    [Theory]
    [InlineData("", "Tea", "930")]
    [InlineData("SKU", " ", "930")]
    [InlineData("SKU", "Tea", null)]
    public void ValidateLines_rejects_missing_text(string productCode, string displayName, string? lookupCode)
    {
        var line = Line(10m) with { ProductCode = productCode, DisplayName = displayName, LookupCode = lookupCode! };

        Assert.Equal(InstallmentAmendLinesValidation.MissingText, InstallmentAmendRules.ValidateLines([line]));
    }

    [Fact]
    public void ValidateLines_rejects_non_positive_quantity_and_unit_price()
    {
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidQuantity,
            InstallmentAmendRules.ValidateLines([Line(10m) with { Quantity = 0m }]));
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidQuantity,
            InstallmentAmendRules.ValidateLines([Line(10m) with { Quantity = -1m }]));
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidUnitPrice,
            InstallmentAmendRules.ValidateLines([Line(10m) with { UnitPrice = 0m }]));
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidUnitPrice,
            InstallmentAmendRules.ValidateLines([Line(10m) with { UnitPrice = -5m }]));
    }

    [Fact]
    public void ValidateLines_rejects_negative_or_full_discount()
    {
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidDiscount,
            InstallmentAmendRules.ValidateLines([Line(10m) with { DiscountAmount = -1m, ActualAmount = 11m }]));
        // 折扣等于毛额意味着行实收为 0，同样不允许。
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidDiscount,
            InstallmentAmendRules.ValidateLines([Line(10m) with { DiscountAmount = 10m, ActualAmount = 0m }]));
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidDiscount,
            InstallmentAmendRules.ValidateLines([Line(10m) with { DiscountAmount = 12m, ActualAmount = -2m }]));
    }

    [Fact]
    public void ValidateLines_rejects_actual_amount_that_does_not_match_quantity_price_discount()
    {
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidActualAmount,
            InstallmentAmendRules.ValidateLines([Line(10m) with { ActualAmount = 9m }]));
        Assert.Equal(
            InstallmentAmendLinesValidation.InvalidActualAmount,
            InstallmentAmendRules.ValidateLines([Line(10m) with { ActualAmount = 0m }]));
    }

    [Theory]
    [InlineData(49.99, 20, InstallmentAmendLinesValidation.TotalBelowMinimum)]
    [InlineData(50, 20, InstallmentAmendLinesValidation.Valid)]
    [InlineData(80, 80, InstallmentAmendLinesValidation.Valid)]
    [InlineData(79.99, 80, InstallmentAmendLinesValidation.TotalBelowPaid)]
    // 已付款大于 50 时，低于已付优先于低于下限判定。
    [InlineData(40, 60, InstallmentAmendLinesValidation.TotalBelowPaid)]
    public void ValidateTotal_checks_paid_before_minimum(
        double newTotal,
        double paid,
        InstallmentAmendLinesValidation expected)
    {
        Assert.Equal(expected, InstallmentAmendRules.ValidateTotal((decimal)newTotal, (decimal)paid));
    }

    [Fact]
    public void Minimum_total_matches_installment_creation_minimum()
    {
        Assert.Equal(InstallmentService.MinimumInstallmentTotalAmount, InstallmentAmendRules.MinimumTotalAmount);
    }

    private static InstallmentLineDto Line(
        decimal actual,
        decimal quantity = 1m,
        decimal? unitPrice = null,
        decimal discount = 0m)
    {
        var price = unitPrice ?? actual;
        return new InstallmentLineDto(
            Guid.NewGuid(),
            "SKU-001",
            null,
            "Tea",
            "9300001",
            quantity,
            price,
            discount,
            InstallmentAmendRules.CalculateActualAmount(quantity, price, discount));
    }
}

/// <summary>SQLite 仓储上的修改商品列表集成测试：事务、锁、乐观并发与金额 / 状态重算。</summary>
public sealed class InstallmentAmendLinesServiceTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-05-21T10:00:00Z");

    [Fact]
    public async Task Amend_replaces_lines_keeps_retained_guid_and_recalculates_amounts()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var retained = created.Details.Lines.Single();
        var newLineGuid = Guid.NewGuid();

        var response = await fixture.Service.AmendLinesAsync(
            fixture.Request(
                created.InstallmentGuid,
                created.Details.UpdatedAt!.Value,
                // 保留原行（改数量与单价）+ 新增一行；原行 GUID 不变。
                Line(retained.InstallmentLineGuid, 2m, 40m),
                Line(newLineGuid, 1m, 30m)),
            CancellationToken.None);

        Assert.Equal(110m, response.TotalAmount);
        Assert.Equal(20m, response.PaidAmount);
        Assert.Equal(90m, response.BalanceAmount);
        Assert.Equal(InstallmentStatus.Active, response.Status);
        Assert.Equal(20m, response.Details.DownPaymentAmount);
        Assert.Equal(20m, response.Details.MinimumDownPayment);
        Assert.True(response.Details.UpdatedAt > created.Details.UpdatedAt);
        Assert.Equal(2, response.Details.Lines.Count);
        var retainedAfter = Assert.Single(response.Details.Lines, line => line.InstallmentLineGuid == retained.InstallmentLineGuid);
        Assert.Equal(2m, retainedAfter.Quantity);
        Assert.Equal(40m, retainedAfter.UnitPrice);
        Assert.Contains(response.Details.Lines, line => line.InstallmentLineGuid == newLineGuid);

        var stored = await fixture.Repository.GetDetailsAsync(created.InstallmentGuid, CancellationToken.None);
        Assert.Equal(110m, stored!.TotalAmount);
        Assert.Equal(2, stored.Lines.Count);
    }

    [Fact]
    public async Task Amend_can_remove_lines_and_replace_everything_with_new_guids()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var oldGuid = created.Details.Lines.Single().InstallmentLineGuid;
        var newGuid = Guid.NewGuid();

        var response = await fixture.Service.AmendLinesAsync(
            fixture.Request(created.InstallmentGuid, created.Details.UpdatedAt!.Value, Line(newGuid, 1m, 70m)),
            CancellationToken.None);

        var only = Assert.Single(response.Details.Lines);
        Assert.Equal(newGuid, only.InstallmentLineGuid);
        Assert.NotEqual(oldGuid, only.InstallmentLineGuid);
        Assert.Equal(70m, response.TotalAmount);
        Assert.Equal(50m, response.BalanceAmount);
    }

    [Fact]
    public async Task Amend_total_equal_to_paid_marks_order_paid_off_with_zero_balance()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        // 先补款 30，已付 50，余额 50。
        var afterPayment = await fixture.Service.AppendPaymentAsync(
            fixture.Payment(created.InstallmentGuid, 30m),
            CancellationToken.None);
        Assert.Equal(50m, afterPayment.PaidAmount);

        var response = await fixture.Service.AmendLinesAsync(
            fixture.Request(
                created.InstallmentGuid,
                afterPayment.Details.UpdatedAt!.Value,
                Line(created.Details.Lines.Single().InstallmentLineGuid, 1m, 50m)),
            CancellationToken.None);

        Assert.Equal(50m, response.TotalAmount);
        Assert.Equal(0m, response.BalanceAmount);
        Assert.Equal(InstallmentStatus.PaidOff, response.Status);
    }

    [Fact]
    public async Task Amend_raising_total_of_paid_off_order_returns_it_to_active()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var paidOff = await fixture.Service.AppendPaymentAsync(
            fixture.Payment(created.InstallmentGuid, 80m),
            CancellationToken.None);
        Assert.Equal(InstallmentStatus.PaidOff, paidOff.Status);

        var response = await fixture.Service.AmendLinesAsync(
            fixture.Request(
                created.InstallmentGuid,
                paidOff.Details.UpdatedAt!.Value,
                Line(created.Details.Lines.Single().InstallmentLineGuid, 1m, 120m)),
            CancellationToken.None);

        Assert.Equal(InstallmentStatus.Active, response.Status);
        Assert.Equal(120m, response.TotalAmount);
        Assert.Equal(100m, response.PaidAmount);
        Assert.Equal(20m, response.BalanceAmount);
    }

    [Fact]
    public async Task Amend_total_below_paid_is_rejected_and_leaves_order_untouched()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var paidOff = await fixture.Service.AppendPaymentAsync(
            fixture.Payment(created.InstallmentGuid, 80m),
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InstallmentAmendLinesException>(() =>
            fixture.Service.AmendLinesAsync(
                fixture.Request(
                    created.InstallmentGuid,
                    paidOff.Details.UpdatedAt!.Value,
                    Line(created.Details.Lines.Single().InstallmentLineGuid, 1m, 80m)),
                CancellationToken.None));

        Assert.Equal(InstallmentAmendLinesErrorCodes.TotalBelowPaid, exception.Code);
        await fixture.AssertUnchangedAsync(created.InstallmentGuid, paidOff.Details);
    }

    [Fact]
    public async Task Amend_total_below_minimum_is_rejected()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);

        var exception = await Assert.ThrowsAsync<InstallmentAmendLinesException>(() =>
            fixture.Service.AmendLinesAsync(
                fixture.Request(
                    created.InstallmentGuid,
                    created.Details.UpdatedAt!.Value,
                    Line(created.Details.Lines.Single().InstallmentLineGuid, 1m, 49.99m)),
                CancellationToken.None));

        Assert.Equal(InstallmentAmendLinesErrorCodes.TotalBelowMinimum, exception.Code);
        await fixture.AssertUnchangedAsync(created.InstallmentGuid, created.Details);
    }

    [Theory]
    [InlineData(InstallmentStatus.PickedUp)]
    [InlineData(InstallmentStatus.Cancelled)]
    public async Task Amend_rejects_picked_up_and_cancelled_orders(InstallmentStatus status)
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        await fixture.SetStatusAsync(created.InstallmentGuid, status);
        var current = await fixture.Repository.GetDetailsAsync(created.InstallmentGuid, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InstallmentAmendLinesException>(() =>
            fixture.Service.AmendLinesAsync(
                fixture.Request(
                    created.InstallmentGuid,
                    current!.UpdatedAt!.Value,
                    Line(created.Details.Lines.Single().InstallmentLineGuid, 1m, 90m)),
                CancellationToken.None));

        Assert.Equal(InstallmentAmendLinesErrorCodes.StatusNotAllowed, exception.Code);
        Assert.Equal(100m, current!.TotalAmount);
    }

    [Fact]
    public async Task Amend_with_stale_expected_updated_at_is_rejected_but_millisecond_jitter_is_tolerated()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var lineGuid = created.Details.Lines.Single().InstallmentLineGuid;
        var updatedAt = created.Details.UpdatedAt!.Value;

        var stale = await Assert.ThrowsAsync<InstallmentAmendLinesException>(() =>
            fixture.Service.AmendLinesAsync(
                fixture.Request(created.InstallmentGuid, updatedAt.AddSeconds(-5), Line(lineGuid, 1m, 90m)),
                CancellationToken.None));
        Assert.Equal(InstallmentAmendLinesErrorCodes.Stale, stale.Code);
        await fixture.AssertUnchangedAsync(created.InstallmentGuid, created.Details);

        // 客户端把时间戳截断到毫秒（或带偏移格式）不应误判为过期。
        var accepted = await fixture.Service.AmendLinesAsync(
            fixture.Request(
                created.InstallmentGuid,
                updatedAt.ToOffset(TimeSpan.FromHours(10)).AddTicks(5_000),
                Line(lineGuid, 1m, 90m)),
            CancellationToken.None);
        Assert.Equal(90m, accepted.TotalAmount);

        // 同一个旧令牌再次提交，说明被另一次修改抢先了，必须过期。
        var replay = await Assert.ThrowsAsync<InstallmentAmendLinesException>(() =>
            fixture.Service.AmendLinesAsync(
                fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 1m, 95m)),
                CancellationToken.None));
        Assert.Equal(InstallmentAmendLinesErrorCodes.Stale, replay.Code);
    }

    [Fact]
    public async Task Amend_rejects_line_guid_that_belongs_to_another_installment()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var first = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var second = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var foreignLineGuid = second.Details.Lines.Single().InstallmentLineGuid;

        var exception = await Assert.ThrowsAsync<InstallmentAmendLinesException>(() =>
            fixture.Service.AmendLinesAsync(
                fixture.Request(
                    first.InstallmentGuid,
                    first.Details.UpdatedAt!.Value,
                    Line(first.Details.Lines.Single().InstallmentLineGuid, 1m, 60m),
                    Line(foreignLineGuid, 1m, 60m)),
                CancellationToken.None));

        Assert.Equal(InstallmentAmendLinesErrorCodes.InvalidLines, exception.Code);
        await fixture.AssertUnchangedAsync(first.InstallmentGuid, first.Details);
        await fixture.AssertUnchangedAsync(second.InstallmentGuid, second.Details);
    }

    [Fact]
    public async Task Amend_is_blocked_while_an_in_flight_claim_exists()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        await fixture.InsertBlockingCancelClaimAsync(created.InstallmentGuid);

        var exception = await Assert.ThrowsAsync<InstallmentRepaymentClaimException>(() =>
            fixture.Service.AmendLinesAsync(
                fixture.Request(
                    created.InstallmentGuid,
                    created.Details.UpdatedAt!.Value,
                    Line(created.Details.Lines.Single().InstallmentLineGuid, 1m, 90m)),
                CancellationToken.None));

        Assert.Equal(InstallmentRepaymentClaimErrorCodes.Busy, exception.Code);
        await fixture.AssertUnchangedAsync(created.InstallmentGuid, created.Details);
    }

    [Fact]
    public async Task Amend_rejects_malformed_lines_before_touching_storage()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var updatedAt = created.Details.UpdatedAt!.Value;
        var lineGuid = created.Details.Lines.Single().InstallmentLineGuid;

        var invalidRequests = new[]
        {
            fixture.Request(created.InstallmentGuid, updatedAt),
            fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 0m, 90m)),
            fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 1m, 90m), Line(lineGuid, 1m, 10m)),
            fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 1m, 90m) with { DisplayName = new string('x', 256) }),
            fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 1m, 90m) with { ProductCode = new string('x', 51) }),
            fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 1m, 90m) with { LookupCode = new string('x', 51) }),
            fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 1m, 90m) with { ReferenceCode = new string('x', 51) }),
            fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 1m, 90m) with { ItemNumber = new string('x', 51) }),
            fixture.Request(created.InstallmentGuid, updatedAt, Line(lineGuid, 1m, 90m) with { ActualAmount = 80m }),
            fixture.Request(created.InstallmentGuid, updatedAt) with { Lines = [null!] }
        };

        foreach (var invalid in invalidRequests)
        {
            var exception = await Assert.ThrowsAsync<InstallmentAmendLinesException>(() =>
                fixture.Service.AmendLinesAsync(invalid, CancellationToken.None));
            Assert.Equal(InstallmentAmendLinesErrorCodes.InvalidLines, exception.Code);
        }

        await fixture.AssertUnchangedAsync(created.InstallmentGuid, created.Details);
    }

    [Fact]
    public async Task Amend_trims_text_and_enforces_store_scope_and_existence()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var lineGuid = created.Details.Lines.Single().InstallmentLineGuid;

        var response = await fixture.Service.AmendLinesAsync(
            fixture.Request(
                created.InstallmentGuid,
                created.Details.UpdatedAt!.Value,
                Line(lineGuid, 1m, 90m) with { DisplayName = "  Green Tea  ", ReferenceCode = "  ", ItemNumber = " IT-1 " }),
            CancellationToken.None);
        var line = Assert.Single(response.Details.Lines);
        Assert.Equal("Green Tea", line.DisplayName);
        Assert.Null(line.ReferenceCode);
        Assert.Equal("IT-1", line.ItemNumber);

        var otherStore = fixture.Request(created.InstallmentGuid, response.Details.UpdatedAt!.Value, Line(lineGuid, 1m, 90m)) with
        {
            StoreCode = "S99"
        };
        var scopeException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.AmendLinesAsync(otherStore, CancellationToken.None));
        Assert.IsNotType<InstallmentAmendLinesException>(scopeException);

        var missing = fixture.Request(Guid.NewGuid(), response.Details.UpdatedAt!.Value, Line(Guid.NewGuid(), 1m, 90m));
        var notFound = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.AmendLinesAsync(missing, CancellationToken.None));
        Assert.Contains("not found", notFound.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Amend_request_path_does_not_run_schema_ddl()
    {
        await using var fixture = await AmendFixture.CreateAsync();
        var created = await fixture.CreateInstallmentAsync(total: 100m, downPayment: 20m);
        var statements = fixture.CaptureSql();

        await fixture.Service.AmendLinesAsync(
            fixture.Request(
                created.InstallmentGuid,
                created.Details.UpdatedAt!.Value,
                Line(created.Details.Lines.Single().InstallmentLineGuid, 1m, 90m)),
            CancellationToken.None);

        Assert.DoesNotContain(
            statements,
            sql => sql.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase) ||
                   sql.Contains("ALTER TABLE", StringComparison.OrdinalIgnoreCase) ||
                   sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Interface_defaults_throw_not_supported_so_existing_fakes_keep_compiling()
    {
        IInstallmentRepository repository = new DefaultOnlyRepository();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            repository.AmendLinesAsync(Guid.NewGuid(), [], DateTimeOffset.UtcNow, CancellationToken.None));
        IInstallmentService service = new DefaultOnlyService();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.AmendLinesAsync(
                new InstallmentAmendLinesRequest(Guid.NewGuid(), "S01", "POS01", "C01", "Cashier", [], DateTimeOffset.UtcNow),
                CancellationToken.None));
    }

    private static InstallmentLineDto Line(Guid lineGuid, decimal quantity, decimal unitPrice) =>
        new(
            lineGuid,
            "SKU-" + lineGuid.ToString("N")[..6],
            null,
            "Tea",
            "9300001",
            quantity,
            unitPrice,
            0m,
            InstallmentAmendRules.CalculateActualAmount(quantity, unitPrice, 0m));

    private sealed class DefaultOnlyRepository : IInstallmentRepository
    {
        public Task CreateAsync(InstallmentDetailsDto details, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto> AppendPaymentAsync(Guid installmentGuid, InstallmentPaymentDto payment, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto> ConfirmPickupAsync(Guid installmentGuid, DateTimeOffset pickedUpAt, string pickedUpBy, string? note, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto> CancelWithRefundAsync(Guid installmentGuid, IReadOnlyList<InstallmentPaymentDto> refunds, InstallmentCancellationInfoDto cancellationInfo, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto> VoidAsync(Guid installmentGuid, InstallmentCancellationInfoDto cancellationInfo, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentPaymentLookup?> FindPaymentAsync(Guid paymentGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentPaymentLookup?> FindPaymentByIdempotencyKeyAsync(Guid installmentGuid, string idempotencyKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentHistoryQueryResponse> QueryAsync(InstallmentHistoryQueryRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentDetailsDto?> GetDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DefaultOnlyService : IInstallmentService
    {
        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class AmendFixture : IAsyncDisposable
    {
        private readonly string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"hbpos-installment-amend-tests-{Guid.NewGuid():N}.db");
        private readonly SqlSugarClient client;
        private readonly MutableTimeProvider timeProvider = new(CreatedAt);

        private AmendFixture()
        {
            client = new SqlSugarClient(new ConnectionConfig
            {
                ConnectionString = $"Data Source={databasePath}",
                DbType = DbType.Sqlite,
                InitKeyType = InitKeyType.Attribute,
                IsAutoCloseConnection = true
            });
            client.CodeFirst.InitTables<
                InstallmentOrderEntity,
                InstallmentOrderLineEntity,
                InstallmentPaymentEntity,
                InstallmentRepaymentClaimEntity,
                InstallmentCancelClaimEntity>();
            Repository = new SqlSugarInstallmentRepository(CreateDbContext(client));
            // 全部用现金支付，不会触达券预留服务。
            Service = new InstallmentService(Repository, null!, timeProvider);
        }

        public SqlSugarInstallmentRepository Repository { get; }

        public InstallmentService Service { get; }

        public static Task<AmendFixture> CreateAsync() => Task.FromResult(new AmendFixture());

        public List<string> CaptureSql()
        {
            var statements = new List<string>();
            client.Aop.OnLogExecuting = (sql, _) => statements.Add(sql);
            return statements;
        }

        public Task<InstallmentCreateResponse> CreateInstallmentAsync(decimal total, decimal downPayment) =>
            Service.CreateAsync(
                new InstallmentCreateRequest(
                    Guid.NewGuid(),
                    "S01",
                    "POS01",
                    "C01",
                    "Cashier",
                    CreatedAt,
                    total,
                    downPayment,
                    [
                        new InstallmentLineDto(Guid.NewGuid(), "SKU-001", null, "Tea", "9300001", 1m, total, 0m, total)
                    ],
                    new InstallmentPaymentCommandDto(Guid.NewGuid(), PaymentMethodKind.Cash, downPayment, null, null),
                    "Alice",
                    "0400000000"),
                CancellationToken.None);

        public InstallmentAppendPaymentRequest Payment(Guid installmentGuid, decimal amount)
        {
            return new InstallmentAppendPaymentRequest(
                installmentGuid,
                Guid.NewGuid(),
                "S01",
                "POS01",
                "C01",
                "Cashier",
                amount,
                PaymentMethodKind.Cash,
                null,
                null);
        }

        public InstallmentAmendLinesRequest Request(
            Guid installmentGuid,
            DateTimeOffset expectedUpdatedAt,
            params InstallmentLineDto[] lines) =>
            new(installmentGuid, "S01", "POS02", "C02", "Cashier Two", lines, expectedUpdatedAt, "customer asked");

        public Task<int> SetStatusAsync(Guid installmentGuid, InstallmentStatus status) =>
            client.Ado.ExecuteCommandAsync(
                "UPDATE InstallmentOrder SET Status = @status WHERE InstallmentGuid = @guid",
                new SugarParameter("@status", (int)status),
                new SugarParameter("@guid", installmentGuid.ToString("D")));

        public Task<int> InsertBlockingCancelClaimAsync(Guid installmentGuid) =>
            client.Insertable(new InstallmentCancelClaimEntity
            {
                InstallmentGuid = installmentGuid,
                OperationGuid = Guid.NewGuid(),
                StoreCode = "S01",
                ClaimantDeviceCode = "POS01",
                CashierId = "C01",
                CashierName = "Cashier",
                IdempotencyKey = $"cancel-{Guid.NewGuid():N}",
                RefundPlanFingerprint = $"sha256:{new string('a', 64)}",
                Status = InstallmentCancelClaimStatus.RefundPending.ToString(),
                IsBlocking = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = null,
                Revision = 1
            }).ExecuteCommandAsync();

        /// <summary>失败路径必须整体回滚：金额、状态、商品行和 UpdatedAt 都与之前一致。</summary>
        public async Task AssertUnchangedAsync(Guid installmentGuid, InstallmentDetailsDto before)
        {
            var after = await Repository.GetDetailsAsync(installmentGuid, CancellationToken.None);
            Assert.NotNull(after);
            Assert.Equal(before.TotalAmount, after!.TotalAmount);
            Assert.Equal(before.PaidAmount, after.PaidAmount);
            Assert.Equal(before.BalanceAmount, after.BalanceAmount);
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Equal(
                before.Lines.Select(line => line.InstallmentLineGuid).OrderBy(guid => guid),
                after.Lines.Select(line => line.InstallmentLineGuid).OrderBy(guid => guid));
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            if (File.Exists(databasePath))
            {
                try
                {
                    File.Delete(databasePath);
                }
                catch (IOException)
                {
                    // SQLite 可能短暂占用测试数据库文件，不影响断言结果。
                }
            }

            return ValueTask.CompletedTask;
        }

        private static HbposSqlSugarContext CreateDbContext(ISqlSugarClient posmDb)
        {
            var context = (HbposSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(HbposSqlSugarContext));
            SetAutoProperty(context, nameof(HbposSqlSugarContext.MainDb), posmDb);
            SetAutoProperty(context, nameof(HbposSqlSugarContext.PosmDb), posmDb);
            return context;
        }

        private static void SetAutoProperty(HbposSqlSugarContext context, string propertyName, ISqlSugarClient value)
        {
            var backingField = typeof(HbposSqlSugarContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(backingField);
            backingField!.SetValue(context, value);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

/// <summary>控制器层：路由 / 策略、身份与权限、错误码到 HTTP 状态的映射。</summary>
public sealed class InstallmentAmendLinesControllerTests
{
    private static readonly Guid InstallmentGuid = Guid.Parse("4b7e1c58-6d3a-4c0a-a5de-0a1d8f8a4f10");
    private static readonly DateTimeOffset UpdatedAt = DateTimeOffset.Parse("2026-10-08T01:02:03.456Z");

    private static readonly InstallmentRepaymentClaimIdentity AllowedIdentity = new(
        "S01",
        "POS-02",
        "TRUSTED-CASHIER",
        "Trusted Cashier",
        [Permissions.PosTerminal.Installments.AmendLines],
        "USER-01");

    [Fact]
    public void Route_and_policy_are_stable()
    {
        var method = typeof(InstallmentsController).GetMethod(nameof(InstallmentsController.AmendLines));
        Assert.NotNull(method);
        Assert.Equal("{installmentGuid:guid}/amend-lines", method!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authorize);
        Assert.Equal(CashierAuthorizationPolicies.InstallmentAmendLines, authorize!.Policy);
        Assert.Equal("Cashier.InstallmentAmendLines", CashierAuthorizationPolicies.InstallmentAmendLines);
    }

    [Fact]
    public async Task Success_returns_200_envelope_and_overwrites_forged_cashier_identity()
    {
        var service = new FakeService();
        var controller = CreateController(service, AllowedIdentity);

        var result = await controller.AmendLines(
            InstallmentGuid,
            CreateRequest() with { CashierId = "FORGED", CashierName = "Forged Cashier" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var envelope = Assert.IsType<ApiResult<InstallmentAmendLinesResponse>>(ok.Value);
        Assert.True(envelope.Success);
        Assert.Equal(InstallmentGuid, envelope.Data!.InstallmentGuid);
        Assert.NotNull(service.Request);
        Assert.Equal("S01", service.Request!.StoreCode);
        Assert.Equal("POS-02", service.Request.DeviceCode);
        Assert.Equal("TRUSTED-CASHIER", service.Request.CashierId);
        Assert.Equal("Trusted Cashier", service.Request.CashierName);
        Assert.Equal(UpdatedAt, service.Request.ExpectedUpdatedAt);
    }

    [Fact]
    public async Task Missing_verified_identity_returns_401_without_calling_service()
    {
        // Audit 模式下策略不会因缺票据拒绝，控制器必须自己兜底。
        var service = new FakeService();
        var controller = CreateController(service, identity: null);

        var result = await controller.AmendLines(InstallmentGuid, CreateRequest(), CancellationToken.None);

        AssertError(result, StatusCodes.Status401Unauthorized, "CASHIER_AUTH_REQUIRED");
        Assert.Null(service.Request);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_cashier_without_amend_permission_returns_403_without_calling_service(bool emptyPermissions)
    {
        var service = new FakeService();
        // 持有其他分期权限但没有 AmendLines，或根本没有权限，都必须拒绝。
        var identity = AllowedIdentity with
        {
            PermissionCodes = emptyPermissions
                ? []
                : [Permissions.PosTerminal.Installments.Cancel, Permissions.PosTerminal.Installments.ConfirmPickup]
        };
        var controller = CreateController(service, identity);

        var result = await controller.AmendLines(InstallmentGuid, CreateRequest(), CancellationToken.None);

        AssertError(result, StatusCodes.Status403Forbidden, "INSTALLMENT_LIFECYCLE_PERMISSION_DENIED");
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Route_guid_mismatch_returns_400()
    {
        var service = new FakeService();
        var controller = CreateController(service, AllowedIdentity);

        var result = await controller.AmendLines(Guid.NewGuid(), CreateRequest(), CancellationToken.None);

        AssertError(result, StatusCodes.Status400BadRequest, "INSTALLMENT_GUID_MISMATCH");
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Forged_store_or_device_in_body_is_rejected_by_device_scope()
    {
        var service = new FakeService();
        var controller = CreateController(service, AllowedIdentity);

        var otherStore = await controller.AmendLines(
            InstallmentGuid,
            CreateRequest() with { StoreCode = "S99" },
            CancellationToken.None);
        var otherDevice = await controller.AmendLines(
            InstallmentGuid,
            CreateRequest() with { DeviceCode = "POS-99" },
            CancellationToken.None);

        AssertError(otherStore, StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN");
        AssertError(otherDevice, StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN");
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Installment_from_another_store_returns_403()
    {
        var service = new FakeService();
        var controller = CreateController(service, AllowedIdentity, detailsStoreCode: "S02");

        var result = await controller.AmendLines(InstallmentGuid, CreateRequest(), CancellationToken.None);

        AssertError(result, StatusCodes.Status403Forbidden, "DEVICE_SCOPE_FORBIDDEN");
        Assert.Null(service.Request);
    }

    [Theory]
    [InlineData("INSTALLMENT_AMEND_INVALID_LINES", StatusCodes.Status400BadRequest)]
    [InlineData("INSTALLMENT_AMEND_TOTAL_BELOW_PAID", StatusCodes.Status400BadRequest)]
    [InlineData("INSTALLMENT_AMEND_TOTAL_BELOW_MINIMUM", StatusCodes.Status400BadRequest)]
    [InlineData("INSTALLMENT_AMEND_STATUS_NOT_ALLOWED", StatusCodes.Status400BadRequest)]
    [InlineData("INSTALLMENT_AMEND_STALE", StatusCodes.Status409Conflict)]
    public async Task Amend_business_errors_map_to_status_and_code(string code, int expectedStatus)
    {
        var service = new FakeService { Failure = new InstallmentAmendLinesException(code, "amend failed") };
        var controller = CreateController(service, AllowedIdentity);

        var result = await controller.AmendLines(InstallmentGuid, CreateRequest(), CancellationToken.None);

        AssertError(result, expectedStatus, code);
    }

    [Fact]
    public void Error_codes_match_the_frozen_contract()
    {
        Assert.Equal("INSTALLMENT_AMEND_INVALID_LINES", InstallmentAmendLinesErrorCodes.InvalidLines);
        Assert.Equal("INSTALLMENT_AMEND_TOTAL_BELOW_PAID", InstallmentAmendLinesErrorCodes.TotalBelowPaid);
        Assert.Equal("INSTALLMENT_AMEND_TOTAL_BELOW_MINIMUM", InstallmentAmendLinesErrorCodes.TotalBelowMinimum);
        Assert.Equal("INSTALLMENT_AMEND_STATUS_NOT_ALLOWED", InstallmentAmendLinesErrorCodes.StatusNotAllowed);
        Assert.Equal("INSTALLMENT_AMEND_STALE", InstallmentAmendLinesErrorCodes.Stale);
    }

    [Fact]
    public async Task Lock_busy_from_service_returns_409_not_generic_400()
    {
        // InstallmentRepaymentClaimException 派生自 InvalidOperationException，必须先于通用处理被捕获。
        var service = new FakeService
        {
            Failure = new InstallmentRepaymentClaimException(
                InstallmentRepaymentClaimErrorCodes.Busy,
                "Installment is busy.")
        };
        var controller = CreateController(service, AllowedIdentity);

        var result = await controller.AmendLines(InstallmentGuid, CreateRequest(), CancellationToken.None);

        AssertError(result, StatusCodes.Status409Conflict, InstallmentRepaymentClaimErrorCodes.Busy);
    }

    [Fact]
    public async Task In_flight_claim_precheck_returns_409()
    {
        var service = new FakeService();
        var controller = CreateController(
            service,
            AllowedIdentity,
            repaymentBusy: true);

        var result = await controller.AmendLines(InstallmentGuid, CreateRequest(), CancellationToken.None);

        AssertError(result, StatusCodes.Status409Conflict, InstallmentRepaymentClaimErrorCodes.Busy);
        Assert.Null(service.Request);
    }

    [Fact]
    public async Task Unclassified_invalid_operation_returns_400_with_generic_code()
    {
        var service = new FakeService { Failure = new InvalidOperationException("Installment was not found.") };
        var controller = CreateController(service, AllowedIdentity);

        var result = await controller.AmendLines(InstallmentGuid, CreateRequest(), CancellationToken.None);

        AssertError(result, StatusCodes.Status400BadRequest, "INSTALLMENT_AMEND_INVALID");
    }

    [Fact]
    public void Capabilities_advertise_amend_lines_support()
    {
        var service = new InstallmentRepaymentClaimService(
            null!,
            null!,
            null!,
            Microsoft.Extensions.Options.Options.Create(new InstallmentRepaymentClaimOptions()));

        Assert.True(service.GetCapabilities().AmendLinesSupported);
    }

    private static InstallmentAmendLinesRequest CreateRequest() =>
        new(
            InstallmentGuid,
            "S01",
            "POS-02",
            "BODY-CASHIER",
            "Body Cashier",
            [
                new InstallmentLineDto(Guid.NewGuid(), "SKU-001", null, "Tea", "9300001", 1m, 80m, 0m, 80m)
            ],
            UpdatedAt,
            "reason");

    private static InstallmentsController CreateController(
        FakeService service,
        InstallmentRepaymentClaimIdentity? identity,
        string detailsStoreCode = "S01",
        bool repaymentBusy = false)
    {
        var controller = new InstallmentsController(
            service,
            new FakeHistoryService(detailsStoreCode),
            new FakeRepaymentClaimService(repaymentBusy),
            new FixedIdentityResolver(identity),
            new PassThroughCancelClaimService());
        var claims = new ClaimsIdentity(
        [
            new Claim(DeviceAuthConstants.StoreCodeClaim, "S01"),
            new Claim(DeviceAuthConstants.DeviceCodeClaim, "POS-02"),
            new Claim(DeviceAuthConstants.HardwareIdClaim, "HW-001")
        ], DeviceAuthConstants.Scheme);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(claims) }
        };
        return controller;
    }

    private static void AssertError(
        ActionResult<ApiResult<InstallmentAmendLinesResponse>> action,
        int statusCode,
        string errorCode)
    {
        // BadRequest(...) 的 BadRequestObjectResult 也派生自 ObjectResult，状态码统一取 StatusCode。
        var result = Assert.IsAssignableFrom<ObjectResult>(action.Result);
        Assert.Equal(statusCode, result.StatusCode);
        var envelope = Assert.IsType<ApiResult<InstallmentAmendLinesResponse>>(result.Value);
        Assert.False(envelope.Success);
        Assert.Equal(errorCode, envelope.ErrorCode);
    }

    private sealed class FakeService : IInstallmentService
    {
        public InstallmentAmendLinesRequest? Request { get; private set; }

        public Exception? Failure { get; init; }

        public Task<InstallmentAmendLinesResponse> AmendLinesAsync(
            InstallmentAmendLinesRequest request,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Request = request;
            var details = new InstallmentDetailsDto(
                request.InstallmentGuid,
                "INS-001",
                request.StoreCode,
                "POS-01",
                "C01",
                "Cashier",
                "Alice",
                "0400000000",
                DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
                80m,
                20m,
                20m,
                20m,
                60m,
                InstallmentStatus.Active,
                request.Lines,
                [],
                null);
            return Task.FromResult(new InstallmentAmendLinesResponse(
                request.InstallmentGuid,
                InstallmentStatus.Active,
                80m,
                20m,
                60m,
                details));
        }

        public Task<InstallmentCreateResponse> CreateAsync(InstallmentCreateRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentAppendPaymentResponse> AppendPaymentAsync(InstallmentAppendPaymentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentConfirmPickupResponse> ConfirmPickupAsync(InstallmentConfirmPickupRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentCancelResponse> CancelAsync(InstallmentCancelRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentVoidResponse> VoidAsync(InstallmentVoidRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeHistoryService(string storeCode) : IInstallmentHistoryService
    {
        public Task<InstallmentHistoryQueryResponse> QueryAsync(InstallmentHistoryQueryRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InstallmentDetailsDto?> GetDetailsAsync(Guid installmentGuid, CancellationToken cancellationToken) =>
            Task.FromResult<InstallmentDetailsDto?>(new InstallmentDetailsDto(
                installmentGuid,
                "INS-001",
                storeCode,
                "POS-01",
                "C01",
                "Cashier",
                "Alice",
                "0400000000",
                DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
                100m,
                20m,
                20m,
                20m,
                80m,
                InstallmentStatus.Active,
                [],
                [],
                null));
    }

    private sealed class FixedIdentityResolver(InstallmentRepaymentClaimIdentity? identity)
        : IInstallmentRepaymentClaimIdentityResolver
    {
        public Task<InstallmentRepaymentClaimIdentity?> ResolveAsync(HttpContext httpContext, CancellationToken cancellationToken) =>
            Task.FromResult(identity);
    }

    private sealed class FakeRepaymentClaimService(bool busy) : IInstallmentRepaymentClaimService
    {
        public InstallmentRepaymentCapabilitiesResponse GetCapabilities() => new(true, true, true, 120);

        public Task EnsureNoBlockingClaimAsync(Guid installmentGuid, CancellationToken cancellationToken) =>
            busy
                ? throw new InstallmentRepaymentClaimException(
                    InstallmentRepaymentClaimErrorCodes.Busy,
                    "Installment already has an in-flight repayment claim.")
                : Task.CompletedTask;

        public Task EnsureLegacyAppendAllowedAsync(Guid installmentGuid, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<InstallmentRepaymentClaimDto> CreateAsync(Guid installmentGuid, InstallmentRepaymentClaimCreateRequest request, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentRepaymentClaimDto> BeginProviderAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimBeginProviderRequest request, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentRepaymentClaimDto> PrepareProviderAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimPrepareProviderRequest request, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentRepaymentClaimDto> GetAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentRepaymentClaimDto> ResolveAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimResolveRequest request, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentRepaymentClaimDto> CommitAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimCommitRequest request, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PassThroughCancelClaimService : IInstallmentCancelClaimService
    {
        public Task EnsureNoBlockingClaimAsync(Guid installmentGuid, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task EnsureLegacyCancelAllowedAsync(Guid installmentGuid, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<InstallmentCancelClaimDto> CreateAsync(Guid installmentGuid, InstallmentCancelClaimCreateRequest request, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentCancelClaimDto> BeginRefundAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentCancelClaimDto> GetAsync(Guid installmentGuid, Guid operationGuid, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentCancelClaimDto> ResolveAsync(Guid installmentGuid, Guid operationGuid, InstallmentCancelClaimResolveRequest request, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InstallmentCancelClaimDto> CommitAsync(Guid installmentGuid, Guid operationGuid, InstallmentCancelClaimCommitRequest request, InstallmentRepaymentClaimIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
