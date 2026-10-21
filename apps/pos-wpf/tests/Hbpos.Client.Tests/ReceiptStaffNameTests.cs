using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ReceiptStaffNameTests
{
    [Fact]
    public void Format_uses_nickname_and_uppercase_family_initial()
    {
        Assert.Equal("Amy L", ReceiptStaffName.Format("Amy", "amy.lee", "Lee"));
    }

    [Fact]
    public void Format_falls_back_to_username_when_nickname_is_missing()
    {
        Assert.Equal("amy.lee L", ReceiptStaffName.Format(null, "amy.lee", "Lee"));
        Assert.Equal("amy.lee L", ReceiptStaffName.Format("   ", "amy.lee", "Lee"));
    }

    [Fact]
    public void Format_shows_only_the_name_when_family_name_is_missing()
    {
        Assert.Equal("Amy", ReceiptStaffName.Format("Amy", "amy.lee", null));
        Assert.Equal("Amy", ReceiptStaffName.Format("Amy", "amy.lee", "  "));
        Assert.Equal("amy.lee", ReceiptStaffName.Format(null, "amy.lee", ""));
    }

    [Fact]
    public void Format_returns_null_when_everything_is_blank()
    {
        Assert.Null(ReceiptStaffName.Format(null, null, null));
        Assert.Null(ReceiptStaffName.Format("", "  ", "Lee"));
    }

    [Fact]
    public void Format_uppercases_lowercase_family_initial()
    {
        Assert.Equal("Amy L", ReceiptStaffName.Format("Amy", "amy.lee", "lee"));
    }

    [Fact]
    public void Format_takes_chinese_family_initial_as_a_whole_character()
    {
        Assert.Equal("Amy 李", ReceiptStaffName.Format("Amy", "amy.lee", "李明"));
    }

    [Fact]
    public void Format_does_not_split_surrogate_pairs_or_combining_marks()
    {
        Assert.Equal("Amy 😀", ReceiptStaffName.Format("Amy", "amy", "😀smith"));
        // "é" 以「e + 组合重音」存储时，首字母必须带着重音一起取。
        Assert.Equal("Amy É", ReceiptStaffName.Format("Amy", "amy", "éclair"));
    }

    [Fact]
    public void Format_trims_surrounding_whitespace()
    {
        Assert.Equal("Amy L", ReceiptStaffName.Format("  Amy  ", " amy.lee ", "  lee  "));
    }

    [Fact]
    public void Format_session_ignores_emergency_override()
    {
        Assert.Null(ReceiptStaffName.Format(CreateSession("EMERGENCY", "x", "Amy", "Lee", emergency: true)));
        Assert.Null(ReceiptStaffName.Format((CashierSessionDto?)null));
    }

    [Fact]
    public void Directory_resolves_stored_full_name_to_receipt_name()
    {
        var directory = new ReceiptStaffDirectory();

        Assert.True(directory.Remember(CreateSession("Amy Lee", "C1", "Amy", "Lee")));

        Assert.Equal("Amy L", directory.ResolveDisplayName("Amy Lee"));
        Assert.Equal("Amy L", directory.ResolveDisplayName("  amy lee "));
    }

    [Fact]
    public void Directory_falls_back_to_stored_name_for_unknown_or_blank_names()
    {
        var directory = new ReceiptStaffDirectory();

        Assert.Equal("Old Timer", directory.ResolveDisplayName(" Old Timer "));
        Assert.Equal(string.Empty, directory.ResolveDisplayName(null));
        Assert.Equal(string.Empty, directory.ResolveDisplayName("   "));
    }

    [Fact]
    public void Directory_ignores_sessions_from_an_old_server_without_name_parts()
    {
        var directory = new ReceiptStaffDirectory();

        Assert.False(directory.Remember(CreateSession("Amy Lee", "C1", nickname: null, familyName: null, username: null)));

        Assert.Equal("Amy Lee", directory.ResolveDisplayName("Amy Lee"));
    }

    [Fact]
    public void Directory_prefers_latest_profile_of_the_same_cashier()
    {
        var directory = new ReceiptStaffDirectory();
        directory.Remember(CreateSession("Amy Lee", "C1", "Amy", "Lee"));

        Assert.True(directory.Remember(CreateSession("Amy Lee", "C1", "Ames", "Lee")));
        Assert.False(directory.Remember(CreateSession("Amy Lee", "C1", "Ames", "Lee")));

        Assert.Equal("Ames L", directory.ResolveDisplayName("Amy Lee"));
    }

    [Fact]
    public void Directory_degrades_when_two_cashiers_share_the_same_full_name()
    {
        var directory = new ReceiptStaffDirectory();
        directory.Remember(CreateSession("Amy Lee", "C1", "Amy", "Lee"));
        directory.Remember(CreateSession("Amy Lee", "C2", "Amelia", "Lee"));

        Assert.Equal("Amy Lee", directory.ResolveDisplayName("Amy Lee"));
    }

    [Fact]
    public void Directory_round_trips_through_settings_json_and_keeps_fresher_memory_entries()
    {
        var source = new ReceiptStaffDirectory();
        source.Remember(CreateSession("Amy Lee", "C1", "Amy", "Lee"));
        source.Remember(CreateSession("Bob Chen", "C2", null, "Chen", username: "bob"));

        var restored = new ReceiptStaffDirectory();
        restored.Remember(CreateSession("Amy Lee", "C1", "Ames", "Lee"));
        restored.Load(source.Serialize());
        restored.Load("not json");
        restored.Load(null);

        Assert.Equal("Ames L", restored.ResolveDisplayName("Amy Lee"));
        Assert.Equal("bob C", restored.ResolveDisplayName("Bob Chen"));
    }

    [Fact]
    public void Receipt_cashier_line_uses_receipt_name_for_known_staff_and_original_text_otherwise()
    {
        // 共用目录是进程级的，这里用不会和其他用例撞名的员工。
        ReceiptStaffDirectory.Shared.Remember(CreateSession("Zed Receiptstaff", "ZR1", "Zed", "Receiptstaff"));
        var formatter = new ReceiptTextFormatter();

        var known = formatter.Build(CreateReceipt("Zed Receiptstaff"), ReceiptPrinterSettings.Default, null);
        var unknown = formatter.Build(CreateReceipt("Legacy Full Name"), ReceiptPrinterSettings.Default, null);

        Assert.Contains("Cashier: Zed R", known.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("Receiptstaff", known.PlainText, StringComparison.Ordinal);
        Assert.Contains("Cashier: Legacy Full Name", unknown.PlainText, StringComparison.Ordinal);
    }

    private static ReceiptDetails CreateReceipt(string cashierName)
    {
        return new ReceiptDetails(
            Guid.NewGuid(),
            "S001",
            "POS-01",
            cashierName,
            DateTimeOffset.Now,
            9.00m,
            0m,
            9.00m,
            [new ReceiptPreviewLine("Item", "LOOKUP-1", 1m, 9.00m, 0m, 9.00m)],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 9.00m, null)]);
    }

    private static CashierSessionDto CreateSession(
        string cashierName,
        string cashierId,
        string? nickname = null,
        string? familyName = null,
        string? username = "user",
        bool emergency = false)
    {
        return new CashierSessionDto(
            cashierId,
            "USER-" + cashierId,
            cashierName,
            "S001",
            "POS-01",
            [],
            [],
            ["S001"],
            IsSuperAdmin: false,
            IsOfflineCached: false,
            IsEmergencyOverride: emergency,
            Username: username,
            Nickname: nickname,
            FamilyName: familyName);
    }
}
