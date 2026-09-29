using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalizationAndSettingsTests
{
    [Fact]
    public void Localization_defaults_to_en_us_and_returns_english_text()
    {
        var localization = new LocalizationService();

        Assert.Equal("en-US", localization.CurrentCulture.Name);
        Assert.Contains(localization.AvailableCultures, culture => culture.Name == "en-US");
        Assert.Contains(localization.AvailableCultures, culture => culture.Name == "zh-CN");
        Assert.Equal("POS Terminal", localization.T("PosTerminal"));
    }

    [Fact]
    public void Localization_switches_to_zh_cn_and_notifies_consumers()
    {
        var localization = new LocalizationService();
        var notificationCount = 0;
        localization.CultureChanged += (_, _) => notificationCount++;

        localization.SetCulture("zh-CN");

        Assert.Equal("zh-CN", localization.CurrentCulture.Name);
        Assert.Equal("POS \u6536\u94F6\u53F0", localization.T("PosTerminal"));
        Assert.Equal(1, notificationCount);
    }

    [Fact]
    public void Cashier_session_expired_message_is_localized()
    {
        var localization = new LocalizationService();

        Assert.Equal(
            "Cashier session expired. Sign in again.",
            localization.T("shell.cashierLogin.status.sessionExpired"));

        localization.SetCulture("zh-CN");

        Assert.Equal(
            "收银员会话已失效，请重新登录。",
            localization.T("shell.cashierLogin.status.sessionExpired"));
    }

    [Fact]
    public void Localization_has_startup_and_shell_control_text()
    {
        var localization = new LocalizationService();

        Assert.Equal("Point of sale", localization.T("startup.subtitle"));
        Assert.Equal("Loading products", localization.T("startup.phase.catalog"));
        Assert.Equal("Step {0} of {1}", localization.T("startup.step"));
        Assert.Equal("Scanner", localization.T("shell.scannerBinding"));

        localization.SetCulture("zh-CN");

        Assert.Equal("收银系统", localization.T("startup.subtitle"));
        Assert.Equal("正在加载商品", localization.T("startup.phase.catalog"));
        Assert.Equal("第 {0}/{1} 步", localization.T("startup.step"));
        Assert.Equal("\u626B\u7801\u67AA", localization.T("shell.scannerBinding"));
        Assert.Equal("\u91CD\u65B0\u5B66\u4E60\u626B\u7801\u67AA", localization.T("shell.scannerBinding.resetTooltip"));
    }

    [Fact]
    public void Card_payment_supervisor_note_is_localized_as_optional()
    {
        var localization = new LocalizationService();

        Assert.Equal("Supervisor note (optional)", localization.T("cardRecovery.payment.field.note"));
        Assert.Contains(
            "A supervisor note is optional.",
            localization.T("cardRecovery.payment.section.instructions"),
            StringComparison.Ordinal);

        localization.SetCulture("zh-CN");

        Assert.Equal("主管备注（选填）", localization.T("cardRecovery.payment.field.note"));
        Assert.Contains(
            "主管备注可选",
            localization.T("cardRecovery.payment.section.instructions"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Localization_has_settings_text()
    {
        var localization = new LocalizationService();

        Assert.Equal("Settings", localization.T("settings.title"));
        Assert.Equal("Data Download", localization.T("settings.section.dataDownload.title"));

        localization.SetCulture("zh-CN");

        Assert.Equal("\u8BBE\u7F6E", localization.T("settings.title"));
        Assert.Equal("\u6570\u636E\u4E0B\u8F7D", localization.T("settings.section.dataDownload.title"));
        Assert.Equal("\u66F4\u6362\u5206\u5E97\u91CD\u65B0\u6CE8\u518C", localization.T("settings.deviceRegistration.action"));
    }

    [Fact]
    public void Localization_has_linkly_sop_text()
    {
        string[] keys =
        [
            "settings.linkly.sop.title",
            "settings.linkly.sop.note",
            "settings.linkly.sop.cloud.title",
            "settings.linkly.sop.cloud.step1",
            "settings.linkly.sop.cloud.step8",
            "settings.linkly.sop.repair.title",
            "settings.linkly.sop.repair.step1",
            "settings.linkly.sop.repair.warning",
            "settings.linkly.sop.local.title",
            "settings.linkly.sop.local.step6",
            "settings.linkly.sop.local.step10",
            "settings.linkly.sop.manual.title",
            "settings.linkly.sop.manual.steps",
            "settings.linkly.sop.manual.warning",
            "settings.linkly.sop.ip.title",
            "settings.linkly.sop.ip.steps",
            "settings.linkly.sop.ip.note",
            "settings.linkly.sop.mode.title",
            "settings.linkly.sop.mode.localIp",
            "settings.linkly.sop.mode.cloudDirectSync",
            "settings.linkly.sop.mode.cloudBackendAsync",
            "settings.linkly.mode.localIp",
            "settings.linkly.mode.cloudDirectSync",
            "settings.linkly.mode.cloudBackendAsync",
            "settings.linkly.localIp.title",
            "settings.linkly.localIp.sandboxSafety",
            "settings.linkly.localIp.testVirtualTerminal",
            "settings.linkly.localIp.hostRequired",
            "settings.linkly.localIp.portInvalid",
            "settings.linkly.localIp.timeoutInvalid",
            "settings.linkly.localIp.testing",
            "settings.linkly.localIp.logon",
            "settings.linkly.localIp.loggingOn",
            "settings.linkly.localIp.logonSuccess",
            "settings.linkly.localIp.logonFailed",
            "settings.linkly.localIp.logonUnknown",
            "settings.linkly.test.configurationChanged",
            "settings.linkly.cloudDirect.title",
            "settings.linkly.cloudBackend.title",
            "settings.linkly.cloudBackend.description",
            "settings.linkly.cloudBackend.test",
            "linkly.local.logon.connectionFailed",
            "linkly.local.logon.requestFailed",
            "linkly.local.logon.success",
            "linkly.local.logon.failed",
            "linkly.local.logon.timeout",
            "linkly.local.logon.exception",
            "linkly.local.logon.cancelled",
            "linkly.local.logon.cancelledUnknown",
            "linkly.local.logon.outcomeUnknown"
        ];
        var localization = new LocalizationService();

        foreach (var key in keys)
        {
            var english = localization.T(key);
            Assert.False(english.StartsWith("[[", StringComparison.Ordinal), key);
            Assert.DoesNotContain("1111 2227", english, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pp.cloud.pceftpos.com", english, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PCEFTPOS", english, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Terminal setup steps", localization.T("settings.linkly.sop.title"), StringComparison.Ordinal);
        Assert.Contains("Async DLE 9600", localization.T("settings.linkly.sop.local.step6"), StringComparison.Ordinal);
        Assert.Contains("standalone", localization.T("settings.linkly.sop.manual.steps"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TCPIP", localization.T("settings.linkly.sop.ip.steps"), StringComparison.Ordinal);
        Assert.Contains("2011", localization.T("settings.linkly.sop.ip.steps"), StringComparison.Ordinal);

        localization.SetCulture("zh-CN");

        foreach (var key in keys)
        {
            var chinese = localization.T(key);
            Assert.False(chinese.StartsWith("[[", StringComparison.Ordinal), key);
            Assert.DoesNotContain("1111 2227", chinese, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pp.cloud.pceftpos.com", chinese, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PCEFTPOS", chinese, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("\u5237\u5361\u673A\u64CD\u4F5C\u6B65\u9AA4", localization.T("settings.linkly.sop.title"), StringComparison.Ordinal);
        Assert.Contains("Async DLE 9600", localization.T("settings.linkly.sop.local.step6"), StringComparison.Ordinal);
        Assert.Contains("standalone", localization.T("settings.linkly.sop.manual.steps"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TCPIP", localization.T("settings.linkly.sop.ip.steps"), StringComparison.Ordinal);
        Assert.Contains("2011", localization.T("settings.linkly.sop.ip.steps"), StringComparison.Ordinal);
    }

    [Fact]
    public void Localization_has_popup_dialog_text()
    {
        string[] keys =
        [
            "returns.openItem.title",
            "returns.openItem.subtitle",
            "returns.openItem.name",
            "returns.openItem.price",
            "returns.openItem.defaultName",
            "Clear",
            "Backspace",
            "common.space",
            "appUpdate.optional.prompt.description",
            "appUpdate.optional.prompt.readyStatus",
            "appUpdate.optional.prompt.currentVersion",
            "appUpdate.optional.prompt.newVersion",
            "appUpdate.optional.prompt.packageDownloaded",
            "appUpdate.optional.prompt.whatsNew",
            "appUpdate.optional.prompt.noReleaseNotes",
            "appUpdate.optional.prompt.safety",
            "appUpdate.optional.prompt.installLater",
            "appUpdate.optional.prompt.restartAndInstall",
            "appUpdate.optional.prompt.settingsHint",
            "linkly.cloud.directPendingMessage",
            "linkly.cloud.directStatusHelp",
            "linkly.cloud.directCancelRequested",
            "linkly.cloud.directCancelFailed",
            "linkly.backend.timeout",
            "linkly.backend.communicationFailed",
            "linkly.backend.invalidResponse",
            "linkly.backend.notSubmitted",
            "linkly.backend.sendKeyFailed",
            "linkly.backend.dialog.title",
            "linkly.backend.dialog.status",
            "linkly.backend.dialog.display",
            "linkly.backend.dialog.receipt",
            "linkly.backend.dialog.close",
            "linkly.backend.dialog.button.cancelPayment",
            "linkly.backend.dialog.button.okCancel",
            "linkly.backend.dialog.button.ok",
            "linkly.backend.dialog.button.yesApproved",
            "linkly.backend.dialog.button.noDeclined",
            "linkly.backend.dialog.button.authoriseSignature",
            "linkly.backend.dialog.button.cancel"
        ];
        var localization = new LocalizationService();

        foreach (var key in keys)
        {
            Assert.False(localization.T(key).StartsWith("[[", StringComparison.Ordinal), key);
        }

        Assert.Equal("Cancel payment", localization.T("linkly.backend.dialog.button.cancelPayment"));
        Assert.Equal("No-barcode return item", localization.T("returns.openItem.title"));

        localization.SetCulture("zh-CN");

        foreach (var key in keys)
        {
            Assert.False(localization.T(key).StartsWith("[[", StringComparison.Ordinal), key);
        }

        Assert.Equal("取消刷卡", localization.T("linkly.backend.dialog.button.cancelPayment"));
        Assert.Equal("无码退货商品", localization.T("returns.openItem.title"));
    }

    [Fact]
    public void Localization_has_installment_text()
    {
        string[] keys =
        [
            "installment.common.input.reference",
            "installment.common.input.voucherToken",
            "installment.center.title",
            "installment.center.currentOrder.none",
            "installment.center.currentOrder.amount",
            "installment.center.action.backToPayment",
            "installment.center.action.search",
            "installment.center.action.create",
            "installment.center.action.repay",
            "installment.center.action.cancel",
            "installment.center.action.void",
            "installment.center.action.confirmPickup",
            "installment.center.search",
            "installment.center.offline",
            "installment.center.column.orderNumber",
            "installment.center.column.customer",
            "installment.center.column.phone",
            "installment.center.column.total",
            "installment.center.column.paid",
            "installment.center.column.outstanding",
            "installment.center.column.status",
            "history.installment.status.paymentDue",
            "history.installment.status.readyForPickup",
            "history.installment.status.pickedUp",
            "history.installment.status.voided",
            "history.installment.status.cancelled",
            "installment.center.column.terminal",
            "installment.center.column.updated",
            "installment.center.selected.none",
            "installment.center.selected.customer.empty",
            "installment.center.selected.outstanding.empty",
            "installment.center.selected.outstanding",
            "installment.center.input.repaymentAmount",
            "installment.center.input.voidReason",
            "installment.center.status.ready",
            "installment.center.status.loaded",
            "installment.center.status.searched",
            "installment.center.status.empty",
            "installment.center.status.refreshFailed",
            "installment.center.status.cardTerminalRequired",
            "installment.center.status.cardNotAuthorized",
            "installment.create.title",
            "installment.create.action.back",
            "installment.create.action.submit",
            "installment.create.offline",
            "installment.create.section.customer",
            "installment.create.section.cart",
            "installment.create.section.payment",
            "installment.create.input.customerName",
            "installment.create.input.customerPhone",
            "installment.create.input.downPaymentAmount",
            "installment.create.input.note",
            "installment.create.order.total",
            "installment.create.order.financed",
            "installment.create.status.ready",
            "installment.create.status.missingCart",
            "installment.create.payment.status.offline",
            "installment.create.payment.status.cash",
            "installment.create.payment.status.card.empty",
            "installment.create.payment.status.card.ready",
            "installment.create.payment.status.voucher.missingCode",
            "installment.create.payment.status.voucher.missingToken",
            "installment.create.payment.status.voucher.ready",
            "payment.installment.confirmFullFirstPayment.title",
            "payment.installment.confirmFullFirstPayment.message",
            "payment.installment.status.fullFirstPaymentCancelled",
            "payment.installment.confirmPickupAfterPaidOff.title",
            "payment.installment.confirmPickupAfterPaidOff.message",
            "payment.installment.status.pickupConfirmFailed"
        ];
        var localization = new LocalizationService();

        foreach (var key in keys)
        {
            Assert.False(localization.T(key).StartsWith("[[", StringComparison.Ordinal), key);
        }

        Assert.Equal("Installment Center", localization.T("installment.center.title"));
        Assert.Equal("Create Installment", localization.T("installment.create.title"));
        Assert.Equal("Payment due", localization.T("history.installment.status.paymentDue"));

        localization.SetCulture("zh-CN");

        foreach (var key in keys)
        {
            Assert.False(localization.T(key).StartsWith("[[", StringComparison.Ordinal), key);
        }

        Assert.Equal("分期中心", localization.T("installment.center.title"));
        Assert.Equal("创建分期", localization.T("installment.create.title"));
        Assert.Equal("待补款", localization.T("history.installment.status.paymentDue"));
    }

    [Fact]
    public void Localization_has_confirmation_overlay_text_in_english_and_chinese()
    {
        string[] keys =
        [
            "common.confirm",
            "confirmation.exit.message",
            "settings.testSalesData.confirm.title",
            "settings.testSalesData.confirm.message",
            "settings.testSalesData.confirm.action"
        ];
        var localization = new LocalizationService();

        foreach (var key in keys)
        {
            Assert.False(localization.T(key).StartsWith("[[", StringComparison.Ordinal), key);
        }

        Assert.Equal("Confirm", localization.T("common.confirm"));
        Assert.Equal("Are you sure you want to exit the POS application?", localization.T("confirmation.exit.message"));
        localization.SetCulture("zh-CN");

        foreach (var key in keys)
        {
            Assert.False(localization.T(key).StartsWith("[[", StringComparison.Ordinal), key);
        }

        Assert.Equal("确认", localization.T("common.confirm"));
        Assert.Equal("确定要退出收银软件吗？", localization.T("confirmation.exit.message"));
    }

    [Fact]
    public void Localization_has_navigation_and_history_text()
    {
        var localization = new LocalizationService();

        Assert.Equal("Back to POS", localization.T("shell.backToPos"));
        Assert.Equal("History", localization.T("shell.page.history"));
        Assert.Equal("Local", localization.T("history.source.local"));
        Assert.Equal("Pending recall", localization.T("history.status.pendingRecall"));
        Assert.Equal("Order details load timed out. Please try again.", localization.T("history.detailsLoadTimeout"));
        Assert.Equal("History", localization.T("pos.terminal.actions.history"));
        Assert.Equal("Settings", localization.T("pos.terminal.actions.settings"));
        Assert.Equal("Customer Display", localization.T("pos.terminal.actions.customerDisplay"));

        localization.SetCulture("zh-CN");

        Assert.Equal("\u8FD4\u56DE\u6536\u94F6", localization.T("shell.backToPos"));
        Assert.Equal("\u5386\u53F2", localization.T("shell.page.history"));
        Assert.Equal("\u672C\u5730", localization.T("history.source.local"));
        Assert.Equal("\u5F85\u53D6\u56DE", localization.T("history.status.pendingRecall"));
        Assert.Equal("订单详情加载超时，请重试。", localization.T("history.detailsLoadTimeout"));
        Assert.Equal("\u5386\u53F2", localization.T("pos.terminal.actions.history"));
        Assert.Equal("\u8BBE\u7F6E", localization.T("pos.terminal.actions.settings"));
        Assert.Equal("\u5BA2\u663E", localization.T("pos.terminal.actions.customerDisplay"));
    }

    [Fact]
    public void Localization_returns_placeholder_for_missing_key()
    {
        var localization = new LocalizationService();

        Assert.Equal("[[DefinitelyMissingKey]]", localization.T("DefinitelyMissingKey"));
    }

    [Fact]
    public async Task App_settings_store_and_restore_language()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var settings = new LocalAppSettingsRepository(store);
            await schema.InitializeAsync();

            await settings.SetValueAsync("Language", "zh-CN");

            Assert.Equal("zh-CN", await settings.GetValueAsync("Language"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task App_settings_entry_returns_value_and_persisted_update_time()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var settings = new LocalAppSettingsRepository(store);
            await schema.InitializeAsync();

            await settings.SetValueAsync("VersionedSetting", "current");
            var entry = await settings.GetEntryAsync("VersionedSetting");

            Assert.NotNull(entry);
            Assert.Equal("current", entry.Value);
            Assert.NotNull(entry.UpdatedAt);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task App_settings_batch_write_rolls_back_all_values_when_second_write_fails()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var settings = new LocalAppSettingsRepository(store);
            await schema.InitializeAsync();
            await settings.SetValueAsync("First", "before");
            await using (var connection = await store.OpenConnectionAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TRIGGER RejectFailSetting
                    BEFORE INSERT ON AppSettings
                    WHEN NEW.Key = 'Fail'
                    BEGIN
                        SELECT RAISE(ABORT, 'simulated batch write failure');
                    END;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["First"] = "after",
                ["Fail"] = "invalid",
            };

            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => settings.SetValuesAsync(values));

            Assert.Equal("before", await settings.GetValueAsync("First"));
            Assert.Null(await settings.GetValueAsync("Fail"));
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Scanner_binding_service_clears_saved_device_path()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            var settings = new LocalAppSettingsRepository(store);
            var binding = new ScannerBindingService(settings);
            await schema.InitializeAsync();

            await binding.SetBoundDevicePathAsync("scanner-device");
            Assert.Equal("scanner-device", await binding.GetBoundDevicePathAsync());

            await binding.ClearBoundDevicePathAsync();

            Assert.Null(await binding.GetBoundDevicePathAsync());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Scanner_binding_service_can_read_before_full_schema_initialization()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var settings = new LocalAppSettingsRepository(store);
            var binding = new ScannerBindingService(settings);

            Assert.Null(await binding.GetBoundDevicePathAsync());
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    [Fact]
    public async Task Local_schema_creates_app_settings_table()
    {
        var databasePath = CreateTempDatabasePath();

        try
        {
            var store = new LocalSqliteStore(databasePath);
            var schema = new LocalSchemaService(store);
            await schema.InitializeAsync();

            await using var connection = await store.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'AppSettings';
                """;

            var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1L, count);
        }
        finally
        {
            DeleteTempDatabase(databasePath);
        }
    }

    private static string CreateTempDatabasePath()
    {
        return Path.Combine(Path.GetTempPath(), $"hbpos-client-{Guid.NewGuid():N}.db");
    }

    private static void DeleteTempDatabase(string databasePath)
    {
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }
}
