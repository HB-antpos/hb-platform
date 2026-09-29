using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Devices;
using Hbpos.Contracts.Linkly;
using Hbpos.Contracts.Orders;
using Hbpos.Contracts.Stores;

namespace Hbpos.Client.Tests;

public sealed class ReceiptPrinterProfileTests
{
    [Fact]
    public async Task Scoped_save_and_load_uses_profile_store_code()
    {
        var repository = new InMemorySettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, NewAuth("S001"));
        var settings = ReceiptPrinterSettings.Default with
        {
            BrandName = "HB",
            StoreName = "Sunnybank",
            StoreAddress = "Shop 1",
            StorePhone = "07",
            Abn = "ABN",
            ReturnPolicy = "Return within 7 days",
            CutDistance = 80
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(1, repository.BatchWriteCount);
        Assert.Equal("S001", await repository.GetValueAsync("ReceiptPrinter:ProfileStoreCode"));
        Assert.Equal("HB", loaded.BrandName);
        Assert.Equal("Sunnybank", loaded.StoreName);
        Assert.Equal("Shop 1", loaded.StoreAddress);
        Assert.Equal("07", loaded.StorePhone);
        Assert.Equal("ABN", loaded.Abn);
        Assert.Equal("Return within 7 days", loaded.ReturnPolicy);
        Assert.Equal(80, loaded.CutDistance);
    }

    [Fact]
    public async Task Bank_receipt_text_flag_defaults_to_print_and_round_trips_per_device()
    {
        var repository = new InMemorySettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, NewAuth("S001"));

        // 旧设备没有该键：默认继续打印银行原文。
        Assert.True((await store.LoadAsync()).PrintBankReceiptText);

        await store.SaveAsync(ReceiptPrinterSettings.Default with { PrintBankReceiptText = false });

        Assert.Equal("false", await repository.GetValueAsync("ReceiptPrinter:PrintBankReceiptText"));
        Assert.False((await store.LoadAsync()).PrintBankReceiptText);

        // 按设备保存：设备改店后仍沿用该偏好，不随门店资料回退。
        var movedStore = new ReceiptPrinterSettingsStore(repository, NewAuth("S002"));
        Assert.False((await movedStore.LoadAsync()).PrintBankReceiptText);

        // 值损坏时回退默认（打印），避免静默丢失银行收据。
        await repository.SetValueAsync("ReceiptPrinter:PrintBankReceiptText", "garbage");
        Assert.True((await store.LoadAsync()).PrintBankReceiptText);
    }

    [Fact]
    public async Task Legacy_unscoped_store_persists_bank_receipt_text_flag()
    {
        var repository = new InMemorySettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository);

        await store.SaveAsync(ReceiptPrinterSettings.Default with { PrintBankReceiptText = false });

        Assert.False((await store.LoadAsync()).PrintBankReceiptText);
    }

    [Fact]
    public async Task Explicit_empty_brand_name_persists_empty_not_default()
    {
        var repository = new InMemorySettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, NewAuth("S001"));
        var settings = ReceiptPrinterSettings.Default with { BrandName = string.Empty, StoreName = "Sunnybank" };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(string.Empty, loaded.BrandName);
        Assert.Equal("Sunnybank", loaded.StoreName);
    }

    [Fact]
    public async Task Fresh_no_profile_config_falls_back_to_current_store_and_keeps_hardware()
    {
        var repository = new InMemorySettingsRepository();
        var store = new ReceiptPrinterSettingsStore(repository, NewAuth("S001"));

        var loaded = await store.LoadAsync();

        // 全新无 profile 配置不触发迁移/绑定写入。
        Assert.Equal(0, repository.BatchWriteCount);
        Assert.Equal(ReceiptPrinterSettings.DefaultPrinterPort, loaded.PrinterPort);
        Assert.Equal(string.Empty, loaded.BrandName);
        Assert.Equal("S001", loaded.StoreName);
        Assert.Equal(string.Empty, loaded.ReturnPolicy);
    }

    [Fact]
    public async Task Fresh_fallback_prefers_current_device_store_name_then_code()
    {
        var repository = new InMemorySettingsRepository();
        var deviceRepository = new StaticDeviceRepository("S001", "Sunnybank");
        var store = new ReceiptPrinterSettingsStore(repository, NewAuth("S001"), deviceRepository);

        var loaded = await store.LoadAsync();

        Assert.Equal("Sunnybank", loaded.StoreName);
    }

    [Fact]
    public async Task Legacy_unscoped_profile_migrates_and_binds_current_store()
    {
        var repository = new InMemorySettingsRepository();
        await repository.SetValueAsync("ReceiptPrinter:BrandName", "Old Brand");
        await repository.SetValueAsync("ReceiptPrinter:StoreName", "Old Store");
        await repository.SetValueAsync("ReceiptPrinter:StoreAddress", "Old Address");
        await repository.SetValueAsync("ReceiptPrinter:ReturnPolicy", "Old policy");
        var store = new ReceiptPrinterSettingsStore(repository, NewAuth("S001"));

        var loaded = await store.LoadAsync();

        Assert.Equal(1, repository.BatchWriteCount);
        Assert.Equal("S001", await repository.GetValueAsync("ReceiptPrinter:ProfileStoreCode"));
        Assert.Equal("Old Brand", loaded.BrandName);
        Assert.Equal("Old Store", loaded.StoreName);
        Assert.Equal("Old Address", loaded.StoreAddress);
        Assert.Equal("Old policy", loaded.ReturnPolicy);
    }

    [Fact]
    public async Task Changed_store_does_not_use_old_store_profile()
    {
        var repository = new InMemorySettingsRepository();
        var auth = NewAuth("S001");
        var store = new ReceiptPrinterSettingsStore(repository, auth);
        await store.SaveAsync(ReceiptPrinterSettings.Default with
        {
            BrandName = "A Brand",
            StoreName = "A Store",
            StoreAddress = "A Address",
            PrinterPort = "USB,COM3"
        });

        auth.Set(new DeviceAuthorizationContext("DEV1", "S002", "HW", "AUTH"));
        var loaded = await store.LoadAsync();

        Assert.Equal("USB,COM3", loaded.PrinterPort);
        Assert.Equal(string.Empty, loaded.BrandName);
        Assert.Equal("S002", loaded.StoreName);
        Assert.Equal(string.Empty, loaded.StoreAddress);
    }

    [Fact]
    public void Formatter_title_falls_back_brand_then_store_then_code()
    {
        var formatter = new ReceiptTextFormatter();
        var receipt = CreateReceipt(Guid.NewGuid());

        var storeOnly = formatter.Build(receipt, ReceiptPrinterSettings.Default with { BrandName = string.Empty, StoreName = "Sunnybank" });
        Assert.StartsWith("Sunnybank", storeOnly.PlainText, StringComparison.Ordinal);

        var brandFirst = formatter.Build(receipt, ReceiptPrinterSettings.Default with { BrandName = "HB", StoreName = "Sunnybank" });
        var lines = brandFirst.PlainText.Split(Environment.NewLine);
        Assert.Equal("HB", lines[0]);
        Assert.Contains("Sunnybank", lines);

        var codeOnly = formatter.Build(receipt, ReceiptPrinterSettings.Default with { BrandName = string.Empty, StoreName = string.Empty });
        Assert.StartsWith("S001", codeOnly.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_skips_empty_return_policy()
    {
        var formatter = new ReceiptTextFormatter();
        var receipt = CreateReceipt(Guid.NewGuid());

        var document = formatter.Build(receipt, ReceiptPrinterSettings.Default with { ReturnPolicy = string.Empty });

        Assert.DoesNotContain("Refunds and returns", document.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Formatter_prints_return_policy_after_embedded_bank_receipt()
    {
        var formatter = new ReceiptTextFormatter();
        var receipt = CreateReceipt(Guid.NewGuid()) with
        {
            Payments =
            [
                new ReceiptPaymentLine(
                    PaymentMethodKind.Card,
                    9.00m,
                    "ANZ:123",
                    [
                        new CardTransactionDto(
                            "Linkly",
                            "TXN-1",
                            "AUTH1",
                            "VISA",
                            411111,
                            "****1111",
                            "M1",
                            "00",
                            "APPROVED",
                            "123456",
                            new DateTimeOffset(2026, 5, 27, 9, 1, 0, TimeSpan.Zero),
                            9.00m,
                            "APPROVED CARD RECEIPT")
                    ])
            ]
        };

        var document = formatter.Build(
            receipt,
            ReceiptPrinterSettings.Default with { ReturnPolicy = "Return within 7 days" });

        var bankReceiptIndex = document.PlainText.IndexOf("APPROVED CARD RECEIPT", StringComparison.Ordinal);
        var returnPolicyIndex = document.PlainText.IndexOf("Refunds and returns", StringComparison.Ordinal);
        Assert.True(bankReceiptIndex >= 0);
        Assert.True(returnPolicyIndex > bankReceiptIndex);
    }

    [Fact]
    public void Formatter_wraps_cjk_return_policy_by_display_width()
    {
        var formatter = new ReceiptTextFormatter();
        var policy = new string('退', 24);

        var document = formatter.Build(
            CreateReceipt(Guid.NewGuid()),
            ReceiptPrinterSettings.Default with { ReturnPolicy = policy });

        var elements = document.Elements.ToList();
        var headingIndex = elements.FindIndex(element =>
            element.Kind == ReceiptPrintElementKind.Text &&
            element.Text == "Refunds and returns");
        Assert.True(headingIndex >= 0);
        var policyLines = elements
            .Skip(headingIndex + 1)
            .TakeWhile(element => element.Kind == ReceiptPrintElementKind.Text)
            .Select(element => element.Text)
            .ToArray();
        Assert.Equal([new string('退', 21), new string('退', 3)], policyLines);
    }

    [Fact]
    public async Task Test_printer_async_builds_not_a_sale_document_and_uses_print_async()
    {
        var driver = new RecordingDriver();
        var settingsStore = new StaticSettingsStore(ReceiptPrinterSettings.Default with { BrandName = "HB", StoreName = "Sunnybank" });
        var queryService = new ThrowingReceiptQueryService();
        var service = new ReceiptPrintService(queryService, settingsStore, new ReceiptTextFormatter(), driver);

        var result = await service.TestPrinterAsync();

        Assert.True(result.Succeeded);
        Assert.NotNull(driver.LastDocument);
        var plainText = driver.LastDocument!.PlainText;
        Assert.Contains("TEST", plainText, StringComparison.Ordinal);
        Assert.Contains("NOT A SALE", plainText, StringComparison.Ordinal);
        // 完整正式格式：抬头、商品、Total、Payment 都必须出现，证明测试票与正式打印同源。
        Assert.Contains("Test Item", plainText, StringComparison.Ordinal);
        Assert.Contains("ITEM", plainText, StringComparison.Ordinal);
        Assert.Contains("Total(inc GST)", plainText, StringComparison.Ordinal);
        Assert.Contains("Payment:", plainText, StringComparison.Ordinal);
        Assert.Equal(0, driver.TestCallCount);
        Assert.Equal(1, driver.PrintCallCount);
        Assert.False(queryService.Called);
    }

    [Fact]
    public async Task Test_printer_uses_current_store_code_when_brand_and_store_name_are_empty()
    {
        var driver = new RecordingDriver();
        var settingsStore = new StaticSettingsStore(ReceiptPrinterSettings.Default with
        {
            BrandName = string.Empty,
            StoreName = string.Empty
        });
        var service = new ReceiptPrintService(
            new ThrowingReceiptQueryService(),
            settingsStore,
            new ReceiptTextFormatter(),
            driver,
            deviceAuthorizationState: NewAuth("S009"));

        var result = await service.TestPrinterAsync();

        Assert.True(result.Succeeded);
        Assert.NotNull(driver.LastDocument);
        Assert.StartsWith("S009", driver.LastDocument!.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("S001", driver.LastDocument.PlainText, StringComparison.Ordinal);
    }

    private static DeviceAuthorizationState NewAuth(string storeCode)
    {
        var state = new DeviceAuthorizationState();
        state.Set(new DeviceAuthorizationContext("DEV1", storeCode, "HW", "AUTH"));
        return state;
    }

    private static ReceiptDetails CreateReceipt(Guid orderGuid)
    {
        return new ReceiptDetails(
            orderGuid,
            "S001",
            "POS-01",
            "Alice",
            new DateTimeOffset(2026, 5, 27, 9, 0, 0, TimeSpan.Zero),
            9.20m,
            0.20m,
            9.00m,
            [
                new ReceiptPreviewLine("Organic Gala Apples", "690101", 2m, 2.50m, 0m, 5.00m),
                new ReceiptPreviewLine("Whole Grain Bread", "690102", 1m, 4.20m, 0.20m, 4.00m)
            ],
            [new ReceiptPaymentLine(PaymentMethodKind.Cash, 9.00m, "CASH", null)],
            null,
            null);
    }

    private sealed class StaticDeviceRepository(string storeCode, string storeName) : ILocalDeviceRepository
    {
        public Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LocalDeviceCache?>(new LocalDeviceCache(
                "DEV1",
                storeCode,
                storeName,
                "HW",
                1,
                true,
                null,
                DateTimeOffset.MinValue));
        }

        public Task SaveAsync(DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveAsync(DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveAsync(DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class InMemorySettingsRepository : ILocalAppSettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public int BatchWriteCount { get; private set; }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            BatchWriteCount++;
            foreach (var (key, value) in values)
            {
                _values[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticSettingsStore(ReceiptPrinterSettings settings) : IReceiptPrinterSettingsStore
    {
        public Task<ReceiptPrinterSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(settings);
        }

        public Task SaveAsync(ReceiptPrinterSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDriver : IReceiptPrinterDriver
    {
        public ReceiptPrintDocument? LastDocument { get; private set; }

        public ReceiptPrinterSettings? LastSettings { get; private set; }

        public int PrintCallCount { get; private set; }

        public int TestCallCount { get; private set; }

        public Task<ReceiptPrinterDriverResult> PrintAsync(
            ReceiptPrintDocument document,
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            PrintCallCount++;
            LastDocument = document;
            LastSettings = settings;
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "printed"));
        }

        public Task<ReceiptPrinterDriverResult> TestAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            TestCallCount++;
            LastSettings = settings;
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "tested"));
        }

        public Task<ReceiptPrinterDriverResult> OpenCashDrawerAsync(
            ReceiptPrinterSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReceiptPrinterDriverResult(true, "drawer opened"));
        }
    }

    private sealed class ThrowingReceiptQueryService : IReceiptQueryService
    {
        public bool Called { get; private set; }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(int take = 50, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("Test printer must not query orders.");
        }

        public Task<IReadOnlyList<LocalOrderSummary>> GetRecentOrdersAsync(LocalOrderHistoryQuery query, int take = 50, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("Test printer must not query orders.");
        }

        public Task<ReceiptDetails?> GetReceiptAsync(Guid orderGuid, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("Test printer must not query orders.");
        }

        public Task<ReceiptDetails?> GetLatestReceiptAsync(CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("Test printer must not query orders.");
        }
    }
}
