using System.Globalization;
using System.Text.Json;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.DailyClose;

namespace Hbpos.Client.Tests;

/// <summary>本地日结存档 → <see cref="DailyCloseSyncRequest"/> 的映射，以及"正常保存路径的数据能通过服务端校验"。</summary>
public sealed class DailyCloseSyncRequestMappingTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ToRequest_maps_every_field_of_a_saved_daily_close()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var saved = await DailyCloseTestData.SaveRealisticDailyCloseAsync(fixture.Store, new DateTime(2026, 5, 28));
        var archive = await fixture.Repository.GetArchiveForUploadAsync(saved.DailyCloseGuid);

        var request = DailyCloseSyncRequestMapper.ToRequest(archive!, "1.0.47");

        Assert.Equal(1, request.SchemaVersion);
        Assert.Equal(saved.DailyCloseGuid, request.DailyCloseGuid);
        Assert.Equal("S001", request.StoreCode);
        Assert.Equal("POS-01", request.DeviceCode);
        Assert.Equal("Wpf", request.ClientKind);
        Assert.Equal(new DateOnly(2026, 5, 28), request.BusinessDate);
        // 时间由本地 ISO "O" 串解析：保持同一时刻（含偏移）。
        Assert.Equal(saved.Report.PeriodFrom, request.PeriodFrom);
        Assert.Equal(saved.Report.PeriodTo, request.PeriodTo);
        Assert.Equal(saved.SavedAt, request.SavedAt);
        Assert.Equal(saved.Report.PeriodFrom.Offset, request.PeriodFrom.Offset);
        Assert.Equal("C001", request.CashierId);
        Assert.Equal("Alice", request.CashierName);
        Assert.Equal("1.0.47", request.AppVersion);
        Assert.Equal(3, request.OrderCount);
        Assert.Equal(3m, request.ReturnQuantity);
        Assert.Equal(7.35m, request.RefundAmount);

        // 支付方式恰好 Cash / Card / Voucher 三条，金额取本地列。
        Assert.Collection(
            request.Tenders,
            cash =>
            {
                Assert.Equal("Cash", cash.Method);
                Assert.Equal((29.55m, 0m, 29.55m), (cash.SalesAmount, cash.RefundAmount, cash.NetAmount));
            },
            card =>
            {
                Assert.Equal("Card", card.Method);
                Assert.Equal((10.30m, 5.15m, 5.15m), (card.SalesAmount, card.RefundAmount, card.NetAmount));
            },
            voucher =>
            {
                Assert.Equal("Voucher", voucher.Method);
                Assert.Equal((20.10m, 2.20m, 17.90m), (voucher.SalesAmount, voucher.RefundAmount, voucher.NetAmount));
            });

        // 11 档面额齐全、按面额降序，数量取盘点值，面额单位是分。
        Assert.Equal(DailyCloseContractConstants.DenominationCents, request.CashCounts.Select(count => count.DenominationCents));
        Assert.Equal(Enumerable.Range(1, 11), request.CashCounts.Select(count => count.Quantity));
        Assert.Equal(325m, request.NoteSubtotal);
        Assert.Equal(26.35m, request.CoinSubtotal);
        Assert.Equal(351.35m, request.CountedCashAmount);
        Assert.Equal(321.80m, request.CashDifference);
    }

    [Theory]
    [InlineData("100", 10000)]
    [InlineData("50", 5000)]
    [InlineData("20", 2000)]
    [InlineData("10", 1000)]
    [InlineData("5", 500)]
    [InlineData("2", 200)]
    [InlineData("1", 100)]
    [InlineData("0.50", 50)]
    [InlineData("0.5", 50)]
    [InlineData("0.20", 20)]
    [InlineData("0.10", 10)]
    [InlineData("0.05", 5)]
    [InlineData("0.050", 5)]
    // 这几个值用 double 乘 100 会差 1 分（如 4.35 * 100 = 434.99999999999994），必须用 decimal 运算。
    [InlineData("0.29", 29)]
    [InlineData("1.15", 115)]
    [InlineData("4.35", 435)]
    [InlineData("8.2", 820)]
    public void ToCents_converts_dollars_to_cents_with_decimal_arithmetic(string dollars, int expectedCents)
    {
        Assert.Equal(expectedCents, DailyCloseSyncRequestMapper.ToCents(decimal.Parse(dollars, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Every_local_denomination_maps_onto_the_contract_denomination_list_in_order()
    {
        var cents = DailyCloseService.AustralianDenominations
            .Select(denomination => DailyCloseSyncRequestMapper.ToCents(denomination.Value))
            .ToList();

        // 本地 11 档面额换算成分之后，必须与契约里服务端校验用的 11 档一一对应。
        Assert.Equal(DailyCloseContractConstants.DenominationCents, cents);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("1.0.47", "1.0.47")]
    [InlineData("  1.0.47  ", "1.0.47")]
    public void NormalizeAppVersion_trims_and_drops_blank_values(string? input, string? expected)
    {
        Assert.Equal(expected, DailyCloseSyncRequestMapper.NormalizeAppVersion(input));
    }

    [Fact]
    public void NormalizeAppVersion_truncates_overlong_informational_versions_to_the_server_limit()
    {
        var informationalVersion = "1.0.47+" + new string('a', 100);

        var normalized = DailyCloseSyncRequestMapper.NormalizeAppVersion(informationalVersion);

        Assert.Equal(64, normalized!.Length);
        Assert.Equal(informationalVersion[..64], normalized);
        // 64 个字符恰好是服务端 AppVersion 的上限：截断后不会再被 400 拒绝。
        Assert.Equal(64, DailyCloseSyncRequestMapper.MaximumAppVersionLength);
    }

    [Fact]
    public async Task Daily_close_produced_by_the_normal_save_path_passes_the_server_validation_rules()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var scenarios = new List<Guid>
        {
            // 有现金/刷卡/代金券销售与退款、11 档面额都有数量。
            (await DailyCloseTestData.SaveRealisticDailyCloseAsync(fixture.Store, new DateTime(2026, 5, 28))).DailyCloseGuid
        };
        var service = new DailyCloseService(fixture.Repository);
        // 空日：没有订单，盘点全 0。
        scenarios.Add((await service.SaveAsync(DailyCloseTestData.Session(), new DateTime(2026, 5, 29), [])).DailyCloseGuid);
        // 只点了部分面额，且实点远低于现金净额（短款，差额为负）。
        scenarios.Add((await DailyCloseTestData.SaveRealisticDailyCloseAsync(
            fixture.Store,
            new DateTime(2026, 5, 30),
            counts: [DailyCloseTestData.Count(5m, 1), DailyCloseTestData.Count(0.05m, 1)])).DailyCloseGuid);

        foreach (var guid in scenarios)
        {
            var archive = await fixture.Repository.GetArchiveForUploadAsync(guid);
            var request = DailyCloseSyncRequestMapper.ToRequest(archive!, "1.0.47");

            Assert.Null(DailyCloseServerRuleReplica.FirstViolation(request, "S001", "POS-01"));
        }
    }

    [Fact]
    public async Task Server_rule_replica_flags_tampered_requests_so_the_check_above_is_not_vacuous()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var saved = await DailyCloseTestData.SaveRealisticDailyCloseAsync(fixture.Store, new DateTime(2026, 5, 28));
        var archive = await fixture.Repository.GetArchiveForUploadAsync(saved.DailyCloseGuid);
        var good = DailyCloseSyncRequestMapper.ToRequest(archive!, "1.0.47");
        Assert.Null(DailyCloseServerRuleReplica.FirstViolation(good, "S001", "POS-01"));

        Assert.Equal("INVALID_NOTE_SUBTOTAL", DailyCloseServerRuleReplica.FirstViolation(good with { NoteSubtotal = 326m }, "S001", "POS-01"));
        Assert.Equal("INVALID_CASH_DIFFERENCE", DailyCloseServerRuleReplica.FirstViolation(good with { CashDifference = 0m }, "S001", "POS-01"));
        Assert.Equal("INVALID_CASH_COUNTS", DailyCloseServerRuleReplica.FirstViolation(good with { CashCounts = good.CashCounts.Skip(1).ToList() }, "S001", "POS-01"));
        Assert.Equal("INVALID_TENDERS", DailyCloseServerRuleReplica.FirstViolation(good with { Tenders = good.Tenders.Skip(1).ToList() }, "S001", "POS-01"));
        Assert.Equal("DEVICE_SCOPE_FORBIDDEN", DailyCloseServerRuleReplica.FirstViolation(good, "S001", "POS-99"));
        Assert.Equal(
            "TENDER_NET_MISMATCH",
            DailyCloseServerRuleReplica.FirstViolation(
                good with { Tenders = [good.Tenders[0] with { NetAmount = 1m }, good.Tenders[1], good.Tenders[2]] },
                "S001",
                "POS-01"));
        // 面额没有换算成分（元当成分）时，11 档面额与服务端清单对不上。
        Assert.Equal(
            "INVALID_CASH_COUNTS",
            DailyCloseServerRuleReplica.FirstViolation(
                good with { CashCounts = good.CashCounts.Select(count => count with { DenominationCents = count.DenominationCents / 100 }).ToList() },
                "S001",
                "POS-01"));
    }

    [Fact]
    public async Task Request_serializes_to_the_camel_case_wire_format_the_server_binds()
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        var guid = await fixture.InsertDailyCloseAsync();
        var archive = await fixture.Repository.GetArchiveForUploadAsync(guid);
        var request = DailyCloseSyncRequestMapper.ToRequest(archive!, "1.0.47");

        var json = JsonSerializer.Serialize(request, WebJson);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(guid, root.GetProperty("dailyCloseGuid").GetGuid());
        Assert.Equal("Wpf", root.GetProperty("clientKind").GetString());
        Assert.Equal("2026-05-28", root.GetProperty("businessDate").GetString());
        Assert.Equal(3, root.GetProperty("tenders").GetArrayLength());
        Assert.Equal("Cash", root.GetProperty("tenders")[0].GetProperty("method").GetString());
        Assert.Equal(11, root.GetProperty("cashCounts").GetArrayLength());
        Assert.Equal(10000, root.GetProperty("cashCounts")[0].GetProperty("denominationCents").GetInt32());
        Assert.Equal(100.15m, root.GetProperty("countedCashAmount").GetDecimal());
        // 日结存档不保存各支付方式笔数：请求里不发这类字段。
        Assert.False(root.GetProperty("tenders")[0].TryGetProperty("transactionCount", out _));
        var roundTripped = JsonSerializer.Deserialize<DailyCloseSyncRequest>(json, WebJson);
        Assert.Equal(request.DailyCloseGuid, roundTripped!.DailyCloseGuid);
        Assert.Equal(request.SavedAt, roundTripped.SavedAt);
        Assert.Equal(request.CashCounts, roundTripped.CashCounts);
    }
}

/// <summary>
/// 金额与时间解析必须与当前区域文化无关：收银机的 Windows 区域可能是 de-DE / fr-FR 这类
/// 以逗号为小数点的设置。修改 CultureInfo 会影响同进程其它测试，所以放进串行的 CultureSensitive 集合。
/// </summary>
[Collection(CultureSensitiveTestCollection.Name)]
public sealed class DailyCloseSyncRequestCultureTests
{
    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public async Task Reading_and_mapping_a_stored_daily_close_is_independent_of_the_current_culture(string cultureName)
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        // 在不变文化下写入（TEXT 里是 "0.15"、"100.15" 这类点号小数），再在目标文化下读取并映射。
        var guid = await fixture.InsertDailyCloseAsync();
        DailyCloseSyncRequest baseline;
        using (new CultureScope(CultureInfo.InvariantCulture))
        {
            baseline = DailyCloseSyncRequestMapper.ToRequest((await fixture.Repository.GetArchiveForUploadAsync(guid))!, "1.0.47");
        }

        DailyCloseSyncRequest underCulture;
        using (new CultureScope(CultureInfo.GetCultureInfo(cultureName)))
        {
            underCulture = DailyCloseSyncRequestMapper.ToRequest((await fixture.Repository.GetArchiveForUploadAsync(guid))!, "1.0.47");
        }

        Assert.Equal(100.15m, underCulture.CountedCashAmount);
        Assert.Equal(0.15m, underCulture.CoinSubtotal);
        Assert.Equal(5, underCulture.CashCounts.Single(count => count.Quantity == 3).DenominationCents);
        Assert.Equal(baseline.BusinessDate, underCulture.BusinessDate);
        Assert.Equal(baseline.SavedAt, underCulture.SavedAt);
        Assert.Equal(baseline.PeriodFrom, underCulture.PeriodFrom);
        Assert.Equal(JsonSerializer.Serialize(baseline), JsonSerializer.Serialize(underCulture));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public async Task Saving_under_a_comma_decimal_culture_still_produces_an_upload_that_passes_server_rules(string cultureName)
    {
        await using var fixture = await DailyCloseUploadFixture.CreateAsync();
        using var culture = new CultureScope(CultureInfo.GetCultureInfo(cultureName));

        var saved = await DailyCloseTestData.SaveRealisticDailyCloseAsync(fixture.Store, new DateTime(2026, 5, 28));
        var archive = await fixture.Repository.GetArchiveForUploadAsync(saved.DailyCloseGuid);
        var request = DailyCloseSyncRequestMapper.ToRequest(archive!, "1.0.47");

        Assert.Equal(351.35m, request.CountedCashAmount);
        Assert.Equal(7.35m, request.RefundAmount);
        Assert.Equal(5, request.CashCounts[^1].DenominationCents);
        Assert.Null(DailyCloseServerRuleReplica.FirstViolation(request, "S001", "POS-01"));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(CultureInfo culture)
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
