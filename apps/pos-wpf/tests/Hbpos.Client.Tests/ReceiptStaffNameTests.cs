using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Cashiers;
using Hbpos.Contracts.Orders;

namespace Hbpos.Client.Tests;

public sealed class ReceiptStaffNameTests
{
    // 参数顺序：Format(nickname, givenName, username, familyName, fullName)。

    [Fact]
    public void Format_prefers_nickname_with_uppercase_family_initial()
    {
        Assert.Equal("Amy L", ReceiptStaffName.Format("Amy", "Alice", "amy.lee", "Lee"));
        Assert.Equal("Amy L", ReceiptStaffName.Format("Amy", "Alice", "amy.lee", "lee"));
    }

    [Fact]
    public void Format_uses_given_name_when_nickname_is_missing()
    {
        Assert.Equal("Alice L", ReceiptStaffName.Format(null, "Alice", "amy.lee", "Lee"));
        Assert.Equal("Alice L", ReceiptStaffName.Format("   ", "Alice", "amy.lee", "Lee"));
    }

    [Fact]
    public void Format_falls_back_to_username_when_nickname_and_given_name_are_missing()
    {
        Assert.Equal("amy.lee L", ReceiptStaffName.Format(null, null, "amy.lee", "Lee"));
        Assert.Equal("amy.lee L", ReceiptStaffName.Format("", "  ", "amy.lee", "Lee"));
    }

    [Fact]
    public void Format_uses_part_before_at_when_username_is_an_email()
    {
        Assert.Equal("Emily.Chen C", ReceiptStaffName.Format(null, null, "Emily.Chen@hotbargain.com.au", "Chen"));
        // 保留原大小写。
        Assert.Equal("AMY", ReceiptStaffName.Format(null, null, "AMY@hb.com.au", null));
    }

    [Fact]
    public void Format_treats_empty_part_before_at_as_no_username()
    {
        Assert.Null(ReceiptStaffName.Format(null, null, "@example.com", "Lee"));
        Assert.Null(ReceiptStaffName.Format(null, null, "  @example.com", null));
    }

    [Fact]
    public void Format_shows_only_the_base_name_when_family_name_is_missing()
    {
        Assert.Equal("Amy", ReceiptStaffName.Format("Amy", "Alice", "amy.lee", null));
        Assert.Equal("Alice", ReceiptStaffName.Format(null, "Alice", "amy.lee", "  "));
        Assert.Equal("amy.lee", ReceiptStaffName.Format(null, null, "amy.lee", ""));
    }

    [Fact]
    public void Format_returns_null_when_every_base_part_is_blank()
    {
        Assert.Null(ReceiptStaffName.Format(null, null, null, null));
        Assert.Null(ReceiptStaffName.Format("", " ", "  ", "Lee"));
    }

    [Fact]
    public void Format_joins_family_and_base_without_space_when_cjk_is_involved()
    {
        Assert.Equal("王芳", ReceiptStaffName.Format(null, "芳", "wf", "王"));
        Assert.Equal("王小芳", ReceiptStaffName.Format("小芳", "芳", "wf", "王"));
        // 只要姓或 base 任一含中日韩字符就整段拼接，不缩写首字母。
        Assert.Equal("李明Amy", ReceiptStaffName.Format("Amy", null, "amy", "李明"));
        Assert.Equal("Lee小美", ReceiptStaffName.Format("小美", null, "xm", "Lee"));
        // 没有姓只输出 base。
        Assert.Equal("小芳", ReceiptStaffName.Format("小芳", null, "wf", null));
    }

    [Fact]
    public void Format_does_not_split_surrogate_pairs_or_combining_marks()
    {
        Assert.Equal("Amy 😀", ReceiptStaffName.Format("Amy", null, "amy", "😀smith"));
        // "é" 以「e + 组合重音」存储时，首字母必须带着重音一起取。
        Assert.Equal("Amy É", ReceiptStaffName.Format("Amy", null, "amy", "éclair"));
    }

    [Fact]
    public void Format_trims_and_collapses_whitespace()
    {
        Assert.Equal("Amy L", ReceiptStaffName.Format("  Amy  ", null, " amy.lee ", "  lee  "));
        Assert.Equal("Mary Ann L", ReceiptStaffName.Format(" Mary   Ann ", null, "mal", "Lee"));
    }

    [Fact]
    public void Format_splits_legacy_full_name_when_given_and_family_names_are_missing()
    {
        // 西文 2 个词：名 姓。
        Assert.Equal("Emily C", ReceiptStaffName.Format(null, null, "emily", null, "Emily Chen"));
        // 西文 ≥3 个词：首词名、末词姓（中间名不印）。
        Assert.Equal("Emily C", ReceiptStaffName.Format(null, null, "emily", null, " Emily  Rose Chen "));
        // 西文 1 个词：只当名。
        Assert.Equal("Emily", ReceiptStaffName.Format(null, null, "em01", null, "Emily"));
        // 有昵称时昵称优先，姓仍取自全名。
        Assert.Equal("Em C", ReceiptStaffName.Format("Em", null, "emily", null, "Emily Chen"));
        // CJK 无空格：首字当姓、其余当名；单字只当名。
        Assert.Equal("王芳", ReceiptStaffName.Format(null, null, "wf", null, "王芳"));
        Assert.Equal("王小芳", ReceiptStaffName.Format("小芳", null, "wf", null, "王芳"));
        Assert.Equal("芳", ReceiptStaffName.Format(null, null, "wf", null, "芳"));
    }

    [Fact]
    public void Format_ignores_legacy_full_name_when_structured_name_exists()
    {
        Assert.Equal("Alice L", ReceiptStaffName.Format(null, "Alice", "amy", "Lee", "Emily Chen"));
        // 只有姓也算结构化姓名：不拆全名，base 退到账号。
        Assert.Equal("bob C", ReceiptStaffName.Format(null, null, "bob", "Chen", "Robert Smith"));
    }

    [Fact]
    public void Format_does_not_split_full_name_that_is_just_the_username()
    {
        // 服务端没有全名时用账号填 CashierName：不拆，按账号规则（邮箱取 @ 前，保留原大小写）。
        Assert.Equal("AMY", ReceiptStaffName.Format(null, null, "AMY@hb.com.au", null, " amy@hb.com.au "));
        Assert.Equal("amy.lee", ReceiptStaffName.Format(null, null, "amy.lee", null, "Amy.Lee"));
    }

    [Fact]
    public void Format_session_uses_given_name_and_legacy_cashier_name()
    {
        Assert.Equal(
            "Alice L",
            ReceiptStaffName.Format(CreateSession("Alice Lee", "C1", familyName: "Lee", username: "alice", givenName: "Alice")));
        // 老账号：会话里只有全名（CashierName），按全名拆。
        Assert.Equal("Emily C", ReceiptStaffName.Format(CreateSession("Emily Rose Chen", "C2", username: "emily")));
        Assert.Equal("王芳", ReceiptStaffName.Format(CreateSession("王芳", "C3", username: "wf")));
        // 没有全名时服务端用账号填 CashierName：不拆，邮箱取 @ 前。
        Assert.Equal("amy", ReceiptStaffName.Format(CreateSession("amy@hb.com.au", "C4", username: "amy@hb.com.au")));
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
    public void Directory_resolves_legacy_full_name_only_accounts_from_a_new_server()
    {
        var directory = new ReceiptStaffDirectory();

        Assert.True(directory.Remember(CreateSession("Emily Chen", "C1", username: "emily")));

        Assert.Equal("Emily C", directory.ResolveDisplayName("Emily Chen"));
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
        bool emergency = false,
        string? givenName = null)
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
            FamilyName: familyName,
            GivenName: givenName);
    }
}
