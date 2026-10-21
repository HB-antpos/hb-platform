using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Linkly;
using BlazorApp.Api.Services.React;
using BlazorApp.Shared.DTOs;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class LinklySettlementAmountParserTests
{
    private const string OfficialFixedWidthSettlement =
        "000000002138VISA                000000100001000000100001000000100001+000000300003" +
        "DEBIT               000000100001000000100001000000100001+000000300003" +
        "069TOTAL               000000300001000000300001000000300001+000000900009";

    private readonly LinklySettlementAmountParser _parser = new();

    [Fact]
    public void ParseFixedWidth_ReturnsTotalAndKeepsNonTotalCardOrder()
    {
        var value = FixedWidth(
            [
                Record("VISA", 100_00, 2, 20_00, 1, 10_00, 1, 110_00, 4),
                Record("DEBIT", 50_00, 1, 0, 0, 5_00, 1, 45_00, 2),
            ],
            Record("TOTAL", 150_00, 3, 20_00, 1, 15_00, 2, 155_00, 6));

        var result = _parser.Parse(value);

        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, result.Status);
        Assert.NotNull(result.Summary);
        Assert.Equal("AUD", result.Summary.CurrencyCode);
        Assert.Equal(15_500, result.Summary.TotalAmountMinor);
        Assert.Equal(6, result.Summary.TotalCount);
        Assert.Collection(
            result.CardTotals,
            visa => Assert.Equal("VISA", visa.CardName),
            debit => Assert.Equal("DEBIT", debit.CardName));
    }

    [Fact]
    public void ParseFixedWidth_AcceptsOfficialSamplePreservedByWpfSyncContract()
    {
        var result = _parser.Parse(OfficialFixedWidthSettlement);

        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, result.Status);
        Assert.Equal(900, result.Summary!.TotalAmountMinor);
        Assert.Equal(9, result.Summary.TotalCount);
        Assert.Equal(["VISA", "DEBIT"], result.CardTotals.Select(item => item.CardName));
    }

    [Fact]
    public void ParseFixedWidth_AcceptsOneExactlyDeclaredOptionalTail()
    {
        var value = FixedWidth([], Record("TOTAL", 0, 0, 0, 0, 0, 0, 0, 0)) + "005ABCDE";

        var result = _parser.Parse(value);

        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, result.Status);
    }

    [Fact]
    public void ParseFixedWidth_RejectsMismatchedLengthsAndDuplicateTotal()
    {
        var mismatchedTail = FixedWidth([], Record("TOTAL", 0, 0, 0, 0, 0, 0, 0, 0)) + "006ABCDE";
        var totalInCards = FixedWidth(
            [Record("TOTAL", 1, 1, 0, 0, 0, 0, 1, 1)],
            Record("TOTAL", 1, 1, 0, 0, 0, 0, 1, 1));

        Assert.Equal(LinklySettlementAmountParseStatus.Invalid, _parser.Parse(mismatchedTail).Status);
        Assert.Equal(LinklySettlementAmountParseStatus.Invalid, _parser.Parse(totalInCards).Status);
    }

    [Fact]
    public void ParseJson_SupportsRootAndResponseOfficialShapes()
    {
        const string totals = """
            [
              { "CardName": "VISA", "PurchaseAmount": 10000, "PurchaseCount": 2,
                "CashOutAmount": 0, "CashOutCount": 0, "RefundAmount": 1000,
                "RefundCount": 1, "TotalAmount": 9000, "TotalCount": 3 },
              { "CardName": "TOTAL", "PurchaseAmount": 10000, "PurchaseCount": 2,
                "CashOutAmount": 0, "CashOutCount": 0, "RefundAmount": 1000,
                "RefundCount": 1, "TotalAmount": 9000, "TotalCount": 3 }
            ]
            """;
        var root = $$"""{ "SettlementTotalsData": {{totals}} }""";
        var response = $$"""{ "Response": { "SettlementTotalsData": {{totals}} } }""";

        var rootResult = _parser.Parse(root);
        var responseResult = _parser.Parse(response);

        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, rootResult.Status);
        Assert.Equal(9_000, rootResult.Summary!.TotalAmountMinor);
        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, responseResult.Status);
        Assert.Equal("VISA", Assert.Single(responseResult.CardTotals).CardName);
    }

    [Fact]
    public void ParseJson_AllowsSettlementDataStringToContinueAsFixedWidth()
    {
        var fixedWidth = FixedWidth([], Record("TOTAL", 2_00, 1, 0, 0, 0, 0, 2_00, 1));
        var json = JsonSerializer.Serialize(new { Response = new { SettlementData = fixedWidth } });

        var result = _parser.Parse(json);

        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, result.Status);
        Assert.Equal(200, result.Summary!.TotalAmountMinor);
    }

    [Fact]
    public void Parse_DoesNotGuessAmountsFromReceiptLikeOrUnofficialData()
    {
        Assert.Equal(LinklySettlementAmountParseStatus.Missing, _parser.Parse("  ").Status);
        Assert.Equal(
            LinklySettlementAmountParseStatus.Unsupported,
            _parser.Parse("SETTLEMENT TOTAL $123.45").Status);
        Assert.Equal(
            LinklySettlementAmountParseStatus.Unsupported,
            _parser.Parse("{\"ReceiptText\":[\"TOTAL $123.45\"]}").Status);
    }

    [Fact]
    public void ParseJson_RequiresExactlyOneTotalAndIntegerFields()
    {
        const string missingTotal = """
            { "SettlementTotalsData": [
              { "CardName": "VISA", "PurchaseAmount": 100, "PurchaseCount": 1,
                "CashOutAmount": 0, "CashOutCount": 0, "RefundAmount": 0,
                "RefundCount": 0, "TotalAmount": 100, "TotalCount": 1 }
            ] }
            """;
        const string decimalAmount = """
            { "SettlementTotalsData": [
              { "CardName": "TOTAL", "PurchaseAmount": 1.25, "PurchaseCount": 1,
                "CashOutAmount": 0, "CashOutCount": 0, "RefundAmount": 0,
                "RefundCount": 0, "TotalAmount": 1.25, "TotalCount": 1 }
            ] }
            """;

        Assert.Equal(LinklySettlementAmountParseStatus.Invalid, _parser.Parse(missingTotal).Status);
        Assert.Equal(LinklySettlementAmountParseStatus.Invalid, _parser.Parse(decimalAmount).Status);
    }

    [Theory]
    [InlineData("PurchaseAmount", "-1")]
    [InlineData("PurchaseAmount", "1000000000")]
    [InlineData("PurchaseCount", "1000")]
    [InlineData("RefundCount", "-1")]
    [InlineData("TotalAmount", "-1000000000")]
    [InlineData("TotalCount", "1000")]
    public void ParseJson_RejectsValuesOutsideOfficialFixedWidthRanges(string field, string value)
    {
        var record = new Dictionary<string, object>
        {
            ["CardName"] = "TOTAL",
            ["PurchaseAmount"] = 100L,
            ["PurchaseCount"] = 1L,
            ["CashOutAmount"] = 0L,
            ["CashOutCount"] = 0L,
            ["RefundAmount"] = 0L,
            ["RefundCount"] = 0L,
            ["TotalAmount"] = 100L,
            ["TotalCount"] = 1L,
        };
        record[field] = long.Parse(value, CultureInfo.InvariantCulture);
        var json = JsonSerializer.Serialize(new { SettlementTotalsData = new[] { record } });

        Assert.Equal(LinklySettlementAmountParseStatus.Invalid, _parser.Parse(json).Status);
    }

    [Fact]
    public void ParseFixedWidth_AcceptsTotalsNameAndSpaceSignFromRealTerminal()
    {
        // 真实终端：汇总行名称为 TOTALS，非负合计的符号位是空格而不是 +。
        var value = FixedWidth(
            [Record("VISA", 100_00, 2, 20_00, 1, 10_00, 1, 110_00, 4).Remove(56, 1).Insert(56, " ")],
            Record("TOTALS", 100_00, 2, 20_00, 1, 10_00, 1, 110_00, 4).Remove(56, 1).Insert(56, " "));

        var result = _parser.Parse(value);

        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, result.Status);
        Assert.Equal(11_000, result.Summary!.TotalAmountMinor);
        Assert.Equal("VISA", Assert.Single(result.CardTotals).CardName);
    }

    [Fact]
    public void ParseMasked_RebuildsTotalsFromPreviouslySanitizedProductionData()
    {
        // 生产存量记录（旧版脱敏器把合计 12 位数字串误当卡号）；汇总行前一条的残留为「笔数末位 + 069」。
        const string masked =
            "****3207MASTERCARD          000040050038000000000000000000000000 ****0038" +
            "VISA                000041520038000000000000000000000000 ****0038" +
            "DEBIT               000001600001000000000000000000000000 ****1069" +
            "TOTALS              000083170077000000000000000000000000 ****0077";

        var result = _parser.Parse(masked);

        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, result.Status);
        Assert.Equal(83_170, result.Summary!.PurchaseAmountMinor);
        Assert.Equal(83_170, result.Summary.TotalAmountMinor);
        Assert.Equal(77, result.Summary.TotalCount);
        Assert.Equal(["MASTERCARD", "VISA", "DEBIT"], result.CardTotals.Select(item => item.CardName));
    }

    [Fact]
    public void ParseMasked_ReconcilesRefundsAndRejectsInconsistentResidue()
    {
        // 含退款：VISA 购买 517.02 / 34 笔，退款 17.48 / 2 笔 → 合计末位 4、笔数 036。
        const string withRefund =
            "****2138VISA                000051702034000000000000000001748002 ****4036" +
            "DEBIT               000000500001000000000000000000000000 ****1069" +
            "TOTALS              000052202035000000000000000001748002 ****4037";
        Assert.Equal(LinklySettlementAmountParseStatus.Parsed, _parser.Parse(withRefund).Status);

        // 残留末位与重算结果对不上时不得猜测金额。
        Assert.Equal(
            LinklySettlementAmountParseStatus.Invalid,
            _parser.Parse(withRefund.Replace("****4036", "****9036")).Status);
    }

    internal static string FixedWidth(IReadOnlyList<string> cards, string total)
    {
        var cardData = string.Concat(cards);
        return cards.Count.ToString("D9", CultureInfo.InvariantCulture)
            + cardData.Length.ToString("D3", CultureInfo.InvariantCulture)
            + cardData
            + total.Length.ToString("D3", CultureInfo.InvariantCulture)
            + total;
    }

    internal static string Record(
        string cardName,
        long purchaseAmount,
        long purchaseCount,
        long cashOutAmount,
        long cashOutCount,
        long refundAmount,
        long refundCount,
        long totalAmount,
        long totalCount)
    {
        var sign = totalAmount < 0 ? '-' : '+';
        return cardName.PadRight(20)
            + purchaseAmount.ToString("D9", CultureInfo.InvariantCulture)
            + purchaseCount.ToString("D3", CultureInfo.InvariantCulture)
            + cashOutAmount.ToString("D9", CultureInfo.InvariantCulture)
            + cashOutCount.ToString("D3", CultureInfo.InvariantCulture)
            + refundAmount.ToString("D9", CultureInfo.InvariantCulture)
            + refundCount.ToString("D3", CultureInfo.InvariantCulture)
            + sign
            + Math.Abs(totalAmount).ToString("D9", CultureInfo.InvariantCulture)
            + totalCount.ToString("D3", CultureInfo.InvariantCulture);
    }
}

public sealed class LinklySettlementQueryServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly POSMSqlSugarContext _context;

    public LinklySettlementQueryServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.Ado.ExecuteCommand("""
            CREATE TABLE POSM_LinklySettlement (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SettlementGuid TEXT NOT NULL,
                StoreCode TEXT NOT NULL,
                DeviceCode TEXT NOT NULL,
                BusinessDate TEXT NOT NULL,
                ConnectionMode TEXT NOT NULL,
                Environment TEXT NOT NULL,
                Status TEXT NOT NULL,
                ProviderSubmissionState TEXT NULL,
                ProviderSessionId TEXT NULL,
                CloudBackendSessionId INTEGER NULL,
                RequestedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                ResponseCode TEXT NULL,
                ResponseText TEXT NULL,
                SettlementData TEXT NULL,
                ReceiptTextsJson TEXT NULL,
                PrintCount INTEGER NOT NULL,
                FirstPrintedAtUtc TEXT NULL,
                LastPrintedAtUtc TEXT NULL,
                LastPrintError TEXT NULL,
                ClientRevision INTEGER NOT NULL,
                ReceivedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """);
        _context = CreateContext(_db);
    }

    [Fact]
    public async Task GetListAsync_FiltersInDatabasePaginatesThenParsesOnlyCurrentPage()
    {
        await InsertAsync(1, "S01", "POS-OLD", new DateTime(2026, 8, 1), new DateTime(2026, 8, 1, 8, 0, 0), "old");
        await InsertAsync(2, "S01", "POS-NEW", new DateTime(2026, 8, 2), new DateTime(2026, 8, 2, 8, 0, 0), "new");
        await InsertAsync(3, "OTHER", "POS-HIDDEN", new DateTime(2026, 8, 2), new DateTime(2026, 8, 3, 8, 0, 0), "hidden");
        var parser = new TrackingParser();
        var service = new LinklySettlementQueryService(_context, parser, new LinklySettlementExcelExporter());

        var result = await service.GetListAsync(new LinklySettlementQueryDto
        {
            BusinessDateFrom = "2026-08-01",
            BusinessDateTo = "2026-08-02",
            StoreCode = "S01",
            PageNumber = 1,
            PageSize = 1,
        });

        Assert.Equal(2, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal("2", item.Id);
        Assert.Equal(["new"], parser.Inputs);
    }

    [Fact]
    public async Task GetListAsync_KeywordDoesNotSearchSettlementOrReceiptPayloadsAndIsParameterized()
    {
        await InsertAsync(1, "SAFE", "POS-1", new DateTime(2026, 8, 1), DateTime.UtcNow, "SECRET-NEEDLE",
            receiptTextsJson: "[\"SECRET-NEEDLE\"]");
        var service = CreateService();

        var payloadResult = await service.GetListAsync(Query(keyword: "SECRET-NEEDLE"));
        var injectionResult = await service.GetListAsync(Query(keyword: "%' OR 1=1 --"));
        var scalarResult = await service.GetListAsync(Query(keyword: "SAFE"));

        Assert.Empty(payloadResult.Items);
        Assert.Empty(injectionResult.Items);
        Assert.Single(scalarResult.Items);
    }

    [Fact]
    public async Task GetDeviceOptionsAsync_ReturnsDistinctStoreDevicePairsInOrder()
    {
        await InsertAsync(1, "S02", "POS-2", new DateTime(2026, 8, 1), DateTime.UtcNow, "");
        await InsertAsync(2, "S01", "POS-9", new DateTime(2026, 8, 1), DateTime.UtcNow, "");
        await InsertAsync(3, "S01", "POS-1", new DateTime(2026, 8, 1), DateTime.UtcNow, "");
        await InsertAsync(4, "S01", "POS-1", new DateTime(2026, 8, 2), DateTime.UtcNow, "");

        var result = await CreateService().GetDeviceOptionsAsync();

        Assert.Equal(
            ["S01/POS-1", "S01/POS-9", "S02/POS-2"],
            result.Select(item => $"{item.StoreCode}/{item.DeviceCode}"));
    }

    [Fact]
    public async Task GetListAsync_PreservesNullableProviderSubmissionState()
    {
        await InsertAsync(
            1,
            "S01",
            "POS-1",
            new DateTime(2026, 8, 1),
            DateTime.UtcNow,
            "",
            providerSubmissionState: null);

        var result = await CreateService().GetListAsync(Query(keyword: "S01"));

        Assert.Null(Assert.Single(result.Items).ProviderSubmissionState);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsOrderedStringReceiptsWithDefensiveRedaction()
    {
        var id = await InsertAsync(
            10,
            "S01",
            "POS-1",
            new DateTime(2026, 8, 1),
            DateTime.UtcNow,
            LinklySettlementAmountParserTests.FixedWidth(
                [],
                LinklySettlementAmountParserTests.Record("TOTAL", 100, 1, 0, 0, 0, 0, 100, 1)),
            receiptTextsJson: JsonSerializer.Serialize(new[]
            {
                "FIRST PAN 4111 1111 1111 1111",
                "TRACK2: ;4111111111111111=29121010000000000000? CVV: 123 TOKEN=secret-token",
                "LAST",
            }));
        var service = CreateService();

        var result = await service.GetDetailAsync(id);

        Assert.NotNull(result);
        Assert.Equal(3, result.Receipts.Length);
        Assert.StartsWith("FIRST", result.Receipts[0], StringComparison.Ordinal);
        Assert.Equal("LAST", result.Receipts[2]);
        var combined = string.Join('\n', result.Receipts);
        Assert.DoesNotContain("4111111111111111", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("4111 1111 1111 1111", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("123", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SettlementData", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReceiptTextsJson", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DateTimeKind.Utc, result.RequestedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.ReceivedAtUtc.Kind);
        Assert.Contains("Z\"", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDetailAsync_RedactsAllWpfPanSeparatorsAndCredentialShapes()
    {
        var nbsp = '\u00A0';
        var id = await InsertAsync(
            12,
            "S01",
            "POS-1",
            new DateTime(2026, 8, 1),
            DateTime.UtcNow,
            "",
            JsonSerializer.Serialize(new[]
            {
                "DOT 1234.5678.9012",
                "TAB 1234\t5678\t9012",
                $"NBSP 1234{nbsp}5678{nbsp}9012",
                "Authorization: Bearer auth-secret",
                "Bearer raw-secret",
                "access_token=access-secret refreshToken=refresh-secret",
            }));

        var result = await CreateService().GetDetailAsync(id);

        var combined = string.Join('\n', result!.Receipts);
        Assert.DoesNotContain("1234.5678.9012", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("1234\t5678\t9012", combined, StringComparison.Ordinal);
        Assert.DoesNotContain($"1234{nbsp}5678{nbsp}9012", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("auth-secret", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("raw-secret", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("access-secret", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret", combined, StringComparison.Ordinal);
        Assert.Equal(3, combined.Split("[REDACTED PAN]", StringSplitOptions.None).Length - 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"text\":\"receipt\"}")]
    [InlineData("[\"valid\",123]")]
    public async Task GetDetailAsync_InvalidReceiptJsonReturnsEmptyStringArray(string? receiptJson)
    {
        var id = await InsertAsync(11, "S01", "POS-1", new DateTime(2026, 8, 1), DateTime.UtcNow, "", receiptJson);

        var result = await CreateService().GetDetailAsync(id);

        Assert.NotNull(result);
        Assert.Empty(result.Receipts);
    }

    [Fact]
    public async Task GetListAsync_RejectsInvalidDatePageAndSortContracts()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<LinklySettlementRequestException>(() => service.GetListAsync(new LinklySettlementQueryDto
        {
            BusinessDateFrom = "08/01/2026", BusinessDateTo = "2026-08-02",
        }));
        await Assert.ThrowsAsync<LinklySettlementRequestException>(() => service.GetListAsync(new LinklySettlementQueryDto
        {
            BusinessDateFrom = "2025-08-01", BusinessDateTo = "2026-08-02",
        }));
        await Assert.ThrowsAsync<LinklySettlementRequestException>(() => service.GetListAsync(new LinklySettlementQueryDto
        {
            BusinessDateFrom = "2026-08-01", BusinessDateTo = "2026-08-02", PageSize = 201,
        }));
        await Assert.ThrowsAsync<LinklySettlementRequestException>(() => service.GetListAsync(new LinklySettlementQueryDto
        {
            BusinessDateFrom = "2026-08-01", BusinessDateTo = "2026-08-02", SortBy = "SettlementData",
        }));
    }

    [Theory]
    [InlineData("connectionMode", "Cloud")]
    [InlineData("environment", "production")]
    [InlineData("status", "Completed")]
    [InlineData("providerSubmissionState", "Accepted")]
    public async Task GetListAsync_RejectsValuesOutsideDatabaseConstraintWhitelists(
        string field,
        string value)
    {
        var query = new LinklySettlementQueryDto
        {
            BusinessDateFrom = "2026-08-01",
            BusinessDateTo = "2026-08-02",
        };
        typeof(LinklySettlementQueryDto).GetProperty(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)!
            .SetValue(query, value);

        var exception = await Assert.ThrowsAsync<LinklySettlementRequestException>(
            () => CreateService().GetListAsync(query));

        Assert.Equal("INVALID_QUERY", exception.Code);
    }

    [Fact]
    public async Task ExportAsync_RejectsMoreThanThirtyOneInclusiveDays()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<LinklySettlementRequestException>(() => service.ExportAsync(new LinklySettlementQueryDto
        {
            BusinessDateFrom = "2026-07-01",
            BusinessDateTo = "2026-08-01",
        }));
    }

    [Fact]
    public async Task ExportAsync_AcceptsThirtyOneInclusiveDaysAndUsesFilterRangeInFileName()
    {
        var result = await CreateService().ExportAsync(new LinklySettlementQueryDto
        {
            BusinessDateFrom = "2026-07-02",
            BusinessDateTo = "2026-08-01",
        });

        Assert.Equal("linkly-settlements-20260702-20260801.xlsx", result.FileName);
        Assert.NotEmpty(result.Content);
    }

    [Fact]
    public async Task ExportAsync_RejectsMoreThanFiveThousandRowsBeforeParsingPayloads()
    {
        var requestedAtUtc = new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc);
        foreach (var batch in Enumerable.Range(0, 5_001).Chunk(250))
        {
            var rows = batch.Select(index => new PosmLinklySettlement
            {
                SettlementGuid = Guid.NewGuid(),
                StoreCode = "S01",
                DeviceCode = $"POS-{index}",
                BusinessDate = new DateTime(2026, 8, 1),
                ConnectionMode = "CloudDirectSync",
                Environment = "Production",
                Status = "Succeeded",
                ProviderSubmissionState = "Submitted",
                RequestedAtUtc = requestedAtUtc,
                SettlementData = $"payload-{index}",
                ReceiptTextsJson = "[]",
                ClientRevision = 1,
                ReceivedAtUtc = requestedAtUtc,
                UpdatedAtUtc = requestedAtUtc,
            }).ToArray();
            await _db.Insertable(rows).ExecuteCommandAsync();
        }
        var parser = new TrackingParser();
        var service = new LinklySettlementQueryService(_context, parser, new LinklySettlementExcelExporter());

        var exception = await Assert.ThrowsAsync<LinklySettlementRequestException>(() => service.ExportAsync(
            new LinklySettlementQueryDto
            {
                BusinessDateFrom = "2026-08-01",
                BusinessDateTo = "2026-08-01",
            }));

        Assert.Equal("EXPORT_ROW_LIMIT_EXCEEDED", exception.Code);
        Assert.Empty(parser.Inputs);
    }

    [Fact]
    public async Task ExportAsync_LoadsMultipleStablePagesAndKeepsEveryRowInOrder()
    {
        var requestedAtUtc = new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc);
        foreach (var batch in Enumerable.Range(0, 401).Chunk(134))
        {
            var rows = batch.Select(index => new PosmLinklySettlement
            {
                SettlementGuid = Guid.NewGuid(),
                StoreCode = "S01",
                DeviceCode = $"POS-{index:D3}",
                BusinessDate = new DateTime(2026, 8, 1),
                ConnectionMode = "CloudDirectSync",
                Environment = "Production",
                Status = "Succeeded",
                ProviderSubmissionState = "Submitted",
                RequestedAtUtc = requestedAtUtc,
                SettlementData = $"payload-{index}",
                ReceiptTextsJson = "[]",
                ClientRevision = 1,
                ReceivedAtUtc = requestedAtUtc,
                UpdatedAtUtc = requestedAtUtc,
            }).ToArray();
            await _db.Insertable(rows).ExecuteCommandAsync();
        }

        var idSnapshotSelectCount = 0;
        var detailBatchSelectCount = 0;
        _db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("POSM_LinklySettlement", StringComparison.OrdinalIgnoreCase))
            {
                if (!sql.Contains("SettlementData", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref idSnapshotSelectCount);
                }
                else if (sql.Contains("SettlementData", StringComparison.OrdinalIgnoreCase)
                    && sql.Contains(" IN ", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref detailBatchSelectCount);
                }
            }
        };

        var result = await CreateService().ExportAsync(new LinklySettlementQueryDto
        {
            BusinessDateFrom = "2026-08-01",
            BusinessDateTo = "2026-08-01",
        });

        using var workbook = new XLWorkbook(new MemoryStream(result.Content));
        var ids = workbook.Worksheet(1)
            .RangeUsed()!
            .RowsUsed()
            .Skip(1)
            .Select(row => long.Parse(row.Cell(1).GetString(), CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(401, ids.Length);
        Assert.Equal(ids.OrderByDescending(value => value), ids);
        Assert.Equal(401, ids.Distinct().Count());
        Assert.Equal(1, idSnapshotSelectCount);
        Assert.Equal(3, detailBatchSelectCount);
    }

    [Fact]
    public async Task ExportAsync_RejectsRecordChangedAfterVersionSnapshot()
    {
        var requestedAtUtc = new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc);
        var id = await InsertAsync(
            1,
            "S01",
            "POS-1",
            new DateTime(2026, 8, 1),
            requestedAtUtc,
            "payload");
        var snapshotUpdated = 0;
        _db.Aop.OnLogExecuted = (sql, _) =>
        {
            if (sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("ClientRevision", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("UpdatedAtUtc", StringComparison.OrdinalIgnoreCase)
                && !sql.Contains("SettlementData", StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref snapshotUpdated, 1, 0) == 0)
            {
                _db.Updateable<PosmLinklySettlement>()
                    .SetColumns(item => item.ClientRevision == 2)
                    .SetColumns(item => item.UpdatedAtUtc == requestedAtUtc.AddMinutes(2))
                    .Where(item => item.Id == id)
                    .ExecuteCommand();
            }
        };

        var exception = await Assert.ThrowsAsync<LinklySettlementExportChangedException>(() =>
            CreateService().ExportAsync(new LinklySettlementQueryDto
            {
                BusinessDateFrom = "2026-08-01",
                BusinessDateTo = "2026-08-01",
            }));

        Assert.Equal("EXPORT_SNAPSHOT_CHANGED", exception.Code);
        Assert.Equal(1, snapshotUpdated);
    }

    [Fact]
    public async Task GetListAsync_PropagatesCancellationTokenToDatabaseOperation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().GetListAsync(Query(keyword: "S01"), cancellation.Token));
    }

    private LinklySettlementQueryService CreateService() =>
        new(_context, new LinklySettlementAmountParser(), new LinklySettlementExcelExporter());

    private static LinklySettlementQueryDto Query(string keyword) => new()
    {
        BusinessDateFrom = "2026-08-01",
        BusinessDateTo = "2026-08-02",
        Keyword = keyword,
    };

    private async Task<long> InsertAsync(
        long id,
        string storeCode,
        string deviceCode,
        DateTime businessDate,
        DateTime requestedAtUtc,
        string settlementData,
        string? receiptTextsJson = null,
        string? providerSubmissionState = "Submitted")
    {
        return await _db.Insertable(new PosmLinklySettlement
        {
            Id = id,
            SettlementGuid = Guid.NewGuid(),
            StoreCode = storeCode,
            DeviceCode = deviceCode,
            BusinessDate = businessDate,
            ConnectionMode = "CloudDirectSync",
            Environment = "Production",
            Status = "Succeeded",
            ProviderSubmissionState = providerSubmissionState,
            RequestedAtUtc = requestedAtUtc,
            ResponseCode = "00",
            ResponseText = "APPROVED",
            SettlementData = settlementData,
            ReceiptTextsJson = receiptTextsJson,
            ClientRevision = 1,
            ReceivedAtUtc = requestedAtUtc.AddMinutes(1),
            UpdatedAtUtc = requestedAtUtc.AddMinutes(1),
        }).ExecuteReturnBigIdentityAsync();
    }

    private static POSMSqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (POSMSqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(POSMSqlSugarContext));
        typeof(POSMSqlSugarContext)
            .GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(context, db);
        return context;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class TrackingParser : ILinklySettlementAmountParser
    {
        public List<string?> Inputs { get; } = [];

        public LinklySettlementAmountParseResult Parse(string? value)
        {
            Inputs.Add(value);
            return LinklySettlementAmountParseResult.Missing;
        }
    }
}

public sealed class LinklySettlementExcelExporterTests
{
    [Fact]
    public void Export_BuildsSafeTypedSummaryWithoutRawPayloadColumns()
    {
        const string highPrecisionId = "9007199254740993";
        var row = new LinklySettlementExportRow
        {
            Item = new LinklySettlementListItemDto
            {
                Id = highPrecisionId,
                SettlementGuid = Guid.NewGuid(),
                StoreCode = "=1+1",
                DeviceCode = "POS-1",
                BusinessDate = new DateOnly(2026, 8, 3),
                ConnectionMode = "CloudDirectSync",
                Environment = "Production",
                Status = "Succeeded",
                ProviderSubmissionState = "Submitted",
                RequestedAtUtc = new DateTime(2026, 8, 3, 1, 2, 3, DateTimeKind.Utc),
                ResponseCode = "00",
                ResponseText = "APPROVED",
                ReceiptCount = 2,
                PrintCount = 1,
                AmountParseStatus = "Parsed",
                AmountSummary = new LinklySettlementAmountDto
                {
                    CurrencyCode = "AUD",
                    PurchaseAmountMinor = 12345,
                    PurchaseCount = 2,
                    TotalAmountMinor = 12345,
                    TotalCount = 2,
                },
            },
            ProviderSessionId = "provider-session",
            CloudBackendSessionId = highPrecisionId,
            ClientRevision = highPrecisionId,
        };

        var result = new LinklySettlementExcelExporter().Export(
            [row],
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3));

        Assert.Equal("linkly-settlements-20260801-20260803.xlsx", result.FileName);
        using var workbook = new XLWorkbook(new MemoryStream(result.Content));
        var sheet = workbook.Worksheet(1);
        var headers = sheet.Row(1).CellsUsed().Select(cell => cell.GetString()).ToArray();
        Assert.DoesNotContain(headers, header => header.Contains("SettlementData", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(headers, header => header.Contains("Receipt", StringComparison.OrdinalIgnoreCase) && header.Contains("Json", StringComparison.OrdinalIgnoreCase));
        var storeCell = sheet.Cell(2, Array.IndexOf(headers, "门店") + 1);
        Assert.Equal("=1+1", storeCell.GetString());
        Assert.True(storeCell.Style.IncludeQuotePrefix);
        Assert.DoesNotContain(sheet.Row(2).CellsUsed(), cell => cell.HasFormula);
        var purchaseCell = sheet.Cell(2, Array.IndexOf(headers, "购买金额") + 1);
        Assert.Equal(123.45m, purchaseCell.GetValue<decimal>());
        Assert.Contains("$", purchaseCell.Style.NumberFormat.Format, StringComparison.Ordinal);
        foreach (var headerName in new[] { "ID", "Cloud Backend Session ID", "客户端版本" })
        {
            var identifierCell = sheet.Cell(2, Array.IndexOf(headers, headerName) + 1);
            Assert.Equal(highPrecisionId, identifierCell.GetString());
            Assert.Equal("@", identifierCell.Style.NumberFormat.Format);
        }
    }

    [Fact]
    public void DtoIdentifiers_RoundTripAsExactDecimalStrings()
    {
        const string highPrecisionId = "9007199254740993";
        var source = new LinklySettlementDetailDto
        {
            Id = highPrecisionId,
            CloudBackendSessionId = highPrecisionId,
            ClientRevision = highPrecisionId,
        };

        var json = JsonSerializer.Serialize(source);
        var roundTrip = JsonSerializer.Deserialize<LinklySettlementDetailDto>(json)!;

        Assert.Equal(highPrecisionId, roundTrip.Id);
        Assert.Equal(highPrecisionId, roundTrip.CloudBackendSessionId);
        Assert.Equal(highPrecisionId, roundTrip.ClientRevision);
        Assert.Contains($"\"{highPrecisionId}\"", json, StringComparison.Ordinal);
    }
}

public sealed class LinklySettlementControllerContractTests
{
    [Fact]
    public void Controller_UsesV1RouteAndAdminRoleAliasesAtClassLevel()
    {
        var type = typeof(LinklySettlementsController);

        Assert.Equal(
            "api/react/v1/linkly-settlements",
            type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal(
            "Admin,管理员,SuperAdmin,超级管理员",
            type.GetCustomAttribute<AuthorizeAttribute>()!.Roles);
        Assert.NotNull(type.GetMethod(nameof(LinklySettlementsController.GetList)));
        Assert.NotNull(type.GetMethod(nameof(LinklySettlementsController.GetDetail)));
        Assert.NotNull(type.GetMethod(nameof(LinklySettlementsController.Export)));
        Assert.Null(type.GetMethod(nameof(LinklySettlementsController.GetList))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("{id:long}", type.GetMethod(nameof(LinklySettlementsController.GetDetail))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("export", type.GetMethod(nameof(LinklySettlementsController.Export))!
            .GetCustomAttribute<HttpPostAttribute>()!.Template);
    }

    [Fact]
    public async Task GetList_MapsValidationFailureToApiResponseBadRequest()
    {
        var service = new ThrowingService();
        var controller = new LinklySettlementsController(service);

        var action = await controller.GetList(new LinklySettlementQueryDto(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action.Result);
        var response = Assert.IsType<ApiResponse<PagedListReactDto<LinklySettlementListItemDto>>>(badRequest.Value);
        Assert.False(response.Success);
        Assert.Equal("INVALID_QUERY", response.ErrorCode);
    }

    [Fact]
    public async Task Export_MapsRowLimitFailureToApiResponseBadRequest()
    {
        var controller = new LinklySettlementsController(new ThrowingService());

        var action = await controller.Export(new LinklySettlementQueryDto(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(action);
        var response = Assert.IsType<ApiResponse<object>>(badRequest.Value);
        Assert.False(response.Success);
        Assert.Equal("EXPORT_ROW_LIMIT_EXCEEDED", response.ErrorCode);
    }

    [Fact]
    public async Task Export_MapsSnapshotVersionChangeToConflict()
    {
        var controller = new LinklySettlementsController(new ThrowingService(snapshotChanged: true));

        var action = await controller.Export(new LinklySettlementQueryDto(), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(action);
        var response = Assert.IsType<ApiResponse<object>>(conflict.Value);
        Assert.False(response.Success);
        Assert.Equal("EXPORT_SNAPSHOT_CHANGED", response.ErrorCode);
    }

    [Fact]
    public async Task Program_RegistersSettlementQueryParserAndExporter()
    {
        var programPath = Path.Combine(FindRepoRoot(), "services/backend/BlazorApp.Api/Program.cs");
        var program = await File.ReadAllTextAsync(programPath);

        Assert.Contains("AddScoped<ILinklySettlementQueryService, LinklySettlementQueryService>()", program);
        Assert.Contains("AddSingleton<ILinklySettlementAmountParser, LinklySettlementAmountParser>()", program);
        Assert.Contains("AddSingleton<LinklySettlementExcelExporter>()", program);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var path = Path.Combine(directory.FullName, "services/backend/BlazorApp.Api/Program.cs");
            if (File.Exists(path))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 hb-platform 仓库根目录");
    }

    private sealed class ThrowingService(bool snapshotChanged = false) : ILinklySettlementQueryService
    {
        public Task<PagedListReactDto<LinklySettlementListItemDto>> GetListAsync(
            LinklySettlementQueryDto request,
            CancellationToken cancellationToken = default) =>
            throw new LinklySettlementRequestException("INVALID_QUERY", "查询无效");

        public Task<IReadOnlyList<LinklySettlementDeviceOptionDto>> GetDeviceOptionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LinklySettlementDeviceOptionDto>>([]);

        public Task<LinklySettlementDetailDto?> GetDetailAsync(
            long id,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<LinklySettlementDetailDto?>(null);

        public Task<LinklySettlementExportResult> ExportAsync(
            LinklySettlementQueryDto request,
            CancellationToken cancellationToken = default)
        {
            if (snapshotChanged)
                throw new LinklySettlementExportChangedException();

            throw new LinklySettlementRequestException("EXPORT_ROW_LIMIT_EXCEEDED", "导出行数超限");
        }
    }
}
