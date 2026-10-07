using System.Text.Json;
using Hbpos.Contracts.DailyClose;

namespace Hbpos.Api.Tests;

public sealed class DailyCloseSyncContractsTests
{
    private const string SampleJson = """
        {
          "schemaVersion": 1,
          "dailyCloseGuid": "11111111-2222-3333-4444-555555555555",
          "storeCode": "S001",
          "deviceCode": "POS-01",
          "clientKind": "Handheld",
          "businessDate": "2026-10-07",
          "periodFrom": "2026-10-07T00:00:00+11:00",
          "periodTo": "2026-10-08T00:00:00+11:00",
          "savedAt": "2026-10-07T20:15:30.1234567+11:00",
          "cashierId": "C001",
          "cashierName": "Alice",
          "appVersion": null,
          "orderCount": 12,
          "returnQuantity": 1.5,
          "refundAmount": 105.00,
          "tenders": [
            { "method": "Cash", "salesAmount": 500.00, "refundAmount": 100.00, "netAmount": 400.00 },
            { "method": "Card", "salesAmount": 250.50, "refundAmount": 0, "netAmount": 250.50 },
            { "method": "Voucher", "salesAmount": 20, "refundAmount": 5, "netAmount": 15 }
          ],
          "cashCounts": [
            { "denominationCents": 10000, "quantity": 2 },
            { "denominationCents": 5, "quantity": 11 }
          ],
          "noteSubtotal": 375.00,
          "coinSubtotal": 26.35,
          "countedCashAmount": 401.35,
          "cashDifference": 1.35
        }
        """;

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Request_exposes_the_agreed_versioned_close_snapshot_in_order()
    {
        var properties = typeof(DailyCloseSyncRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            [
                "SchemaVersion", "DailyCloseGuid", "StoreCode", "DeviceCode", "ClientKind", "BusinessDate",
                "PeriodFrom", "PeriodTo", "SavedAt", "CashierId", "CashierName", "AppVersion",
                "OrderCount", "ReturnQuantity", "RefundAmount", "Tenders", "CashCounts",
                "NoteSubtotal", "CoinSubtotal", "CountedCashAmount", "CashDifference"
            ],
            properties);
    }

    [Fact]
    public void Request_deserializes_from_camel_case_json()
    {
        var request = JsonSerializer.Deserialize<DailyCloseSyncRequest>(SampleJson, WebOptions)!;

        Assert.Equal(1, request.SchemaVersion);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), request.DailyCloseGuid);
        Assert.Equal("S001", request.StoreCode);
        Assert.Equal("POS-01", request.DeviceCode);
        Assert.Equal("Handheld", request.ClientKind);
        Assert.Equal(new DateOnly(2026, 10, 7), request.BusinessDate);
        Assert.Equal(TimeSpan.FromHours(11), request.PeriodFrom.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 20, 15, 30, TimeSpan.FromHours(11)).AddTicks(1234567), request.SavedAt);
        Assert.Null(request.AppVersion);
        Assert.Equal(12, request.OrderCount);
        Assert.Equal(1.5m, request.ReturnQuantity);
        Assert.Equal(3, request.Tenders.Count);
        Assert.Equal(new DailyCloseTenderSync("Card", 250.50m, 0m, 250.50m), request.Tenders[1]);
        Assert.Equal(new DailyCloseCashCountSync(10000, 2), request.CashCounts[0]);
        Assert.Equal(401.35m, request.CountedCashAmount);
        Assert.Equal(1.35m, request.CashDifference);
    }

    [Fact]
    public void Request_round_trips_through_json_without_losing_instants_or_amounts()
    {
        var original = JsonSerializer.Deserialize<DailyCloseSyncRequest>(SampleJson, WebOptions)!;

        var json = JsonSerializer.Serialize(original, WebOptions);
        var restored = JsonSerializer.Deserialize<DailyCloseSyncRequest>(json, WebOptions)!;

        Assert.Equal(original.SavedAt, restored.SavedAt);
        Assert.Equal(original.BusinessDate, restored.BusinessDate);
        Assert.Equal(original.Tenders, restored.Tenders);
        Assert.Equal(original.CashCounts, restored.CashCounts);
        Assert.Contains("\"dailyCloseGuid\"", json, StringComparison.Ordinal);
        Assert.Contains("\"denominationCents\":10000", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_serializes_with_camel_case_names()
    {
        var json = JsonSerializer.Serialize(new DailyCloseSyncResponse(true, false, true), WebOptions);

        Assert.Equal("{\"accepted\":true,\"alreadySynced\":false,\"replacedPlaceholder\":true}", json);
    }

    [Fact]
    public void Constants_describe_the_eleven_aud_denominations_in_descending_order()
    {
        Assert.Equal(1, DailyCloseContractConstants.SchemaVersion);
        Assert.Equal(
            [10000, 5000, 2000, 1000, 500, 200, 100, 50, 20, 10, 5],
            DailyCloseContractConstants.DenominationCents);
        Assert.Equal(["Wpf", "Handheld", "Ipad"], DailyCloseContractConstants.ClientKinds);
        Assert.Equal(["Cash", "Card", "Voucher"], DailyCloseContractConstants.TenderMethods);
        // 纸币（>= $5）与硬币（<= $2）互不重叠且合起来覆盖全部 11 档。
        var notes = DailyCloseContractConstants.DenominationCents
            .Where(cents => cents >= DailyCloseContractConstants.NoteMinimumDenominationCents);
        var coins = DailyCloseContractConstants.DenominationCents
            .Where(cents => cents < DailyCloseContractConstants.NoteMinimumDenominationCents);
        Assert.Equal([10000, 5000, 2000, 1000, 500], notes);
        Assert.Equal([200, 100, 50, 20, 10, 5], coins);
    }
}
