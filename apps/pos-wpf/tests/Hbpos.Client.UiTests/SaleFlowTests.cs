using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.UiTests;

public sealed class OperationAuditBackendClientTests
{
    [Fact]
    public async Task FetchAllAsync_reads_all_pages_without_writes()
    {
        var rows = Enumerable.Range(1, 201).Select(Row).ToArray();
        var handler = new RecordingHandler(
            Page(rows[..200], 201),
            Page(rows[200..], 201));
        using var client = new OperationAuditBackendClient(
            new Uri("https://backend.example.test/"),
            "test-bearer-secret",
            handler);

        var result = await client.FetchAllAsync(
            new AuditQuery(
                DateTimeOffset.Parse("2026-07-11T10:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-07-11T10:10:00Z", CultureInfo.InvariantCulture),
                "1042",
                "DEV-1",
                "cashier-1"),
            CancellationToken.None);

        Assert.Equal(201, result.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Contains("pageNumber=1", handler.Requests[0].Uri.Query);
        Assert.Contains("pageNumber=2", handler.Requests[1].Uri.Query);
        Assert.All(handler.Requests, request =>
        {
            Assert.Contains("pageSize=200", request.Uri.Query);
            Assert.DoesNotContain("test-bearer-secret", request.Uri.ToString());
            Assert.Equal("Bearer", request.AuthorizationScheme);
            Assert.Equal("test-bearer-secret", request.AuthorizationParameter);
        });
    }

    [Fact]
    public async Task FetchAllAsync_rejects_empty_page_before_reported_total()
    {
        var firstPage = Enumerable.Range(1, 200).Select(Row).ToArray();
        var handler = new RecordingHandler(
            Page(firstPage, 201),
            Page([], 201));
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchAllAsync(Query(), CancellationToken.None));

        Assert.Contains("空页", error.Message);
    }

    [Fact]
    public async Task FetchAllAsync_rejects_short_page_before_reported_total()
    {
        var handler = new RecordingHandler(Page([Row(1)], 2));
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchAllAsync(Query(), CancellationToken.None));

        Assert.Contains("短页", error.Message);
    }

    [Fact]
    public async Task FetchAllAsync_rejects_repeated_page_event_ids()
    {
        var repeatedPage = Enumerable.Range(1, 200).Select(Row).ToArray();
        var handler = new RecordingHandler(
            Page(repeatedPage, 400),
            Page(repeatedPage, 400));
        using var client = Client(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchAllAsync(Query(), CancellationToken.None));
    }

    [Fact]
    public async Task FetchAllAsync_rejects_total_that_changes_between_pages()
    {
        var rows = Enumerable.Range(1, 201).Select(Row).ToArray();
        var handler = new RecordingHandler(
            Page(rows[..200], 201),
            Page(rows[200..], 200));
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchAllAsync(Query(), CancellationToken.None));

        Assert.Contains("总数变化", error.Message);
    }

    [Fact]
    public async Task FetchAllAsync_rejects_total_above_hard_page_limit()
    {
        var handler = new RecordingHandler(Page(Enumerable.Range(1, 200).Select(Row), 200_001));
        using var client = Client(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FetchAllAsync(Query(), CancellationToken.None));

        Assert.Contains("分页上限", error.Message);
    }

    [Fact]
    public async Task PollRequiredAsync_uses_login_cashier_id_for_the_five_events()
    {
        var required = RequiredRows();
        var handler = new RecordingHandler(
            Page(required.Where(row => row.OperationType == "CASHIER_LOGIN"), 1),
            Page(required, required.Count));
        using var client = new OperationAuditBackendClient(
            new Uri("https://backend.example.test/"),
            "test-bearer-secret",
            handler);

        var result = await client.PollRequiredAsync(
            new AuditQuery(
                DateTimeOffset.Parse("2026-07-11T10:00:00Z", CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-07-11T10:10:00Z", CultureInfo.InvariantCulture),
                "1042",
                "DEV-1"),
            new HashSet<Guid>(),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(5, result.Count);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("cashierKeyword", handler.Requests[0].Uri.Query);
        Assert.Contains("cashierKeyword=cashier-1", handler.Requests[1].Uri.Query);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task PollRequiredAsync_excludes_before_snapshot_login_and_sale()
    {
        var current = RequiredRows().ToList();
        var oldLogin = current[0] with
        {
            EventId = Guid.Parse("50000000-0000-0000-0000-000000000001"),
            OccurredAtUtc = current[0].OccurredAtUtc.AddMinutes(1),
            CashierId = "old-cashier",
        };
        var oldSale = current[4] with
        {
            EventId = Guid.Parse("50000000-0000-0000-0000-000000000002"),
            OccurredAtUtc = current[4].OccurredAtUtc.AddMinutes(1),
            OrderGuid = Guid.Parse("50000000-0000-0000-0000-000000000003").ToString(),
        };
        var excluded = new HashSet<Guid> { oldLogin.EventId, oldSale.EventId };
        var handler = new RecordingHandler(
            Page(new[] { current[0], oldLogin }, 2),
            Page(current.Append(oldSale), 6));
        using var client = Client(handler);

        var result = await client.PollRequiredAsync(
            Query() with { CashierKeyword = null },
            excluded,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(5, result.Count);
        Assert.DoesNotContain(result, row => excluded.Contains(row.EventId));
        Assert.Contains("cashierKeyword=cashier-1", handler.Requests[1].Uri.Query);
        Assert.Equal(current[4].OrderGuid, RequiredEventValidator.Validate(result, "1042", "DEV-1").OrderGuid);
    }

    [Fact]
    public async Task PollRequiredAsync_filters_events_from_other_instances()
    {
        var current = RequiredRows().ToList();
        var otherInstance = current[1] with
        {
            EventId = Guid.Parse("60000000-0000-0000-0000-000000000001"),
            InstanceId = "instance-2",
        };
        var handler = new RecordingHandler(
            Page([current[0]], 1),
            Page(current.Append(otherInstance), 6));
        using var client = Client(handler);

        var result = await client.PollRequiredAsync(
            Query() with { CashierKeyword = null },
            new HashSet<Guid>(),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(5, result.Count);
        Assert.DoesNotContain(result, row => row.InstanceId == "instance-2");
    }

    [Fact]
    public async Task PollRequiredAsync_hard_timeout_cancels_blocked_get()
    {
        using var client = Client(
            new SlowHandler(TimeSpan.FromMilliseconds(500)),
            TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.PollRequiredAsync(
                Query() with { CashierKeyword = null },
                new HashSet<Guid>(),
                TimeSpan.FromSeconds(5),
                CancellationToken.None));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("test-bearer-secret", error.Message);
        Assert.DoesNotContain("cashier-secret", error.Message);
        Assert.DoesNotContain("product-secret", error.Message);
    }

    [Fact]
    public async Task Detail_GET_requires_an_exact_product_identifier_match()
    {
        const string productBarcode = "test-product-secret";
        var eventId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var handler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            success = true,
            data = new
            {
                eventId,
                items = new[]
                {
                    new
                    {
                        productCode = "SKU-1",
                        itemNumber = "ITEM-1",
                        referenceCode = "REF-1",
                        lookupCode = productBarcode,
                    },
                },
            },
        }));
        using var client = new OperationAuditBackendClient(
            new Uri("https://backend.example.test/"),
            "test-bearer-secret",
            handler);

        using var detail = await client.GetDetailAsync(eventId, CancellationToken.None);

        AuditDetailValidator.AssertContainsProduct(detail, productBarcode);
        var error = Assert.Throws<InvalidOperationException>(() =>
            AuditDetailValidator.AssertContainsProduct(detail, "different-product-secret"));
        Assert.DoesNotContain("different-product-secret", error.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith(eventId.ToString(), request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task PollOrderAsync_accepts_only_the_target_store_order()
    {
        var orderGuid = Guid.Parse("30000000-0000-0000-0000-000000000001").ToString();
        var handler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            success = true,
            data = new { order = new { OrderGuid = orderGuid, BranchCode = "1042" } },
        }));
        using var client = new OperationAuditBackendClient(
            new Uri("https://backend.example.test/"),
            "test-bearer-secret",
            handler);

        await client.PollOrderAsync(orderGuid, "1042", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.EndsWith(orderGuid, request.Uri.AbsolutePath);

        var wrongStoreHandler = new RecordingHandler(JsonSerializer.Serialize(new
        {
            success = true,
            data = new { order = new { orderGuid, branchCode = "1005" } },
        }));
        using var wrongStoreClient = new OperationAuditBackendClient(
            new Uri("https://backend.example.test/"),
            "test-bearer-secret",
            wrongStoreHandler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            wrongStoreClient.PollOrderAsync(orderGuid, "1042", CancellationToken.None));
        Assert.DoesNotContain("test-bearer-secret", error.Message);
    }

    [Theory]
    [InlineData("{\"success\":true,\"data\":{\"order\":{}}}")]
    [InlineData("{\"success\":true,\"data\":{\"order\":{\"branchCode\":\"1042\"}}}")]
    [InlineData("{\"success\":true,\"data\":{\"order\":{\"orderGuid\":\"30000000-0000-0000-0000-000000000002\",\"branchCode\":\"1042\"}}}")]
    public async Task PollOrderAsync_rejects_order_without_the_requested_guid(string responseJson)
    {
        var requested = Guid.Parse("30000000-0000-0000-0000-000000000001").ToString();
        using var client = Client(new RecordingHandler(responseJson));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PollOrderAsync(requested, "1042", CancellationToken.None));

        Assert.DoesNotContain("test-bearer-secret", error.Message);
    }

    [Fact]
    public async Task PollOrderAsync_hard_timeout_cancels_blocked_get()
    {
        using var client = Client(
            new SlowHandler(TimeSpan.FromMilliseconds(500)),
            TimeSpan.FromMilliseconds(100));
        var elapsed = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            client.PollOrderAsync(
                Guid.Parse("30000000-0000-0000-0000-000000000001").ToString(),
                "1042",
                CancellationToken.None));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("test-bearer-secret", error.Message);
    }

    [Fact]
    public void Detail_selection_uses_only_new_ids_for_the_four_business_events()
    {
        var required = RequiredRows().ToList();
        var oldAdd = required[1] with
        {
            EventId = Guid.Parse("40000000-0000-0000-0000-000000000001"),
            OccurredAtUtc = required[1].OccurredAtUtc.AddMinutes(-1),
        };
        required.Add(oldAdd);
        var newIds = required
            .Where(row => row.OperationType != "CASHIER_LOGIN" && row.EventId != oldAdd.EventId)
            .Select(row => row.EventId)
            .ToHashSet();

        var selected = AuditDetailValidator.SelectNewRequiredEvents(required, newIds);

        Assert.Equal(4, selected.Count);
        Assert.All(selected, row => Assert.Contains(row.EventId, newIds));
        Assert.DoesNotContain(selected, row => row.EventId == oldAdd.EventId);
    }

    private static AuditRow Row(int index) => new(
        Guid.NewGuid(),
        DateTimeOffset.Parse("2026-07-11T10:00:00Z", CultureInfo.InvariantCulture).AddSeconds(index),
        DateTimeOffset.Parse("2026-07-11T10:00:01Z", CultureInfo.InvariantCulture).AddSeconds(index),
        "CART_ITEM_ADD",
        "Succeeded",
        "1042",
        "DEV-1",
        "cashier-1",
        "Cashier One",
        "instance-1",
        null,
        null,
        null);

    private static OperationAuditBackendClient Client(
        HttpMessageHandler handler,
        TimeSpan? maxPollDuration = null) => new(
        new Uri("https://backend.example.test/"),
        "test-bearer-secret",
        handler,
        maxPollDuration);

    private static AuditQuery Query() => new(
        DateTimeOffset.Parse("2026-07-11T10:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-07-11T10:10:00Z", CultureInfo.InvariantCulture),
        "1042",
        "DEV-1",
        "cashier-1");

    private static IReadOnlyList<AuditRow> RequiredRows()
    {
        var rows = Enumerable.Range(1, 5).Select(Row).ToArray();
        rows[0] = rows[0] with { OperationType = "CASHIER_LOGIN" };
        rows[1] = rows[1] with { OperationType = "CART_ITEM_ADD" };
        rows[2] = rows[2] with { OperationType = "CART_ITEM_QUANTITY_CHANGE" };
        rows[3] = rows[3] with { OperationType = "PAYMENT_TENDER_ADD", PaymentMethod = "Cash" };
        rows[4] = rows[4] with
        {
            OperationType = "SALE_COMPLETE",
            ReasonCode = "SALE",
            OrderGuid = Guid.Parse("10000000-0000-0000-0000-000000000001").ToString(),
        };
        return rows;
    }

    private static string Page(IEnumerable<AuditRow> rows, int total) => JsonSerializer.Serialize(
        new { success = true, data = new { items = rows, total } },
        new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class RecordingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));
            if (!_responses.TryDequeue(out var response))
                throw new InvalidOperationException("测试响应已耗尽。");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter);

    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            throw new HttpRequestException("测试 handler 已完成等待。");
        }
    }
}

public sealed class AuditSnapshotTests
{
    [Fact]
    public void Snapshot_rejects_new_event_in_other_store()
    {
        var before = AuditSnapshot.Create([
            Row(Guid.Parse("00000000-0000-0000-0000-000000001042"), "1042", "DEV-1", "CASHIER_LOGIN")
        ]);
        var after = AuditSnapshot.Create([
            Row(Guid.Parse("00000000-0000-0000-0000-000000001042"), "1042", "DEV-1", "CASHIER_LOGIN"),
            Row(Guid.Parse("00000000-0000-0000-0000-000000001009"), "1009", "DEV-X", "SALE_COMPLETE")
        ]);

        Assert.Throws<InvalidOperationException>(() =>
            AuditSnapshot.AssertOtherStoresUnchanged(
                before,
                after,
                new HashSet<string> { "1042", "1005" }));
    }

    [Fact]
    public void Required_events_must_be_succeeded_for_same_store_device_and_cashier()
    {
        var valid = RequiredRows();

        var sale = RequiredEventValidator.Validate(valid, "1042", "DEV-1");

        Assert.Equal("SALE_COMPLETE", sale.OperationType);
        var invalidSets = new IReadOnlyList<AuditRow>[]
        {
            Replace(valid, 1, valid[1] with { Outcome = "Failed" }),
            Replace(valid, 1, valid[1] with { StoreCode = "1005" }),
            Replace(valid, 1, valid[1] with { DeviceCode = "DEV-X" }),
            Replace(valid, 1, valid[1] with { CashierId = "cashier-2" }),
            Replace(valid, 3, valid[3] with { PaymentMethod = "Card" }),
            Replace(valid, 4, valid[4] with { ReasonCode = "REFUND" }),
            Replace(valid, 4, valid[4] with { OrderGuid = null }),
        };
        Assert.All(invalidSets, rows =>
            Assert.Throws<InvalidOperationException>(() =>
                RequiredEventValidator.Validate(rows, "1042", "DEV-1")));
    }

    [Fact]
    public void Required_events_must_share_a_non_empty_login_instance()
    {
        var valid = RequiredRows();

        Assert.Throws<InvalidOperationException>(() =>
            RequiredEventValidator.Validate(
                Replace(valid, 0, valid[0] with { InstanceId = null }),
                "1042",
                "DEV-1"));
        Assert.Throws<InvalidOperationException>(() =>
            RequiredEventValidator.Validate(
                Replace(valid, 2, valid[2] with { InstanceId = "instance-2" }),
                "1042",
                "DEV-1"));
    }

    [Fact]
    public void New_target_device_events_cannot_move_to_another_allowed_store()
    {
        var old = Row(Guid.NewGuid(), "1042", "DEV-1", "CASHIER_LOGIN");
        var before = AuditSnapshot.Create([old]);
        var afterRows = new[]
        {
            old,
            Row(Guid.NewGuid(), "1005", "DEV-1", "SALE_COMPLETE"),
        };

        Assert.Throws<InvalidOperationException>(() =>
            AuditSnapshot.AssertNewTargetDeviceBelongsToStore(
                before,
                afterRows,
                "DEV-1",
                "1042"));
    }

    private static IReadOnlyList<AuditRow> RequiredRows()
    {
        var orderGuid = Guid.Parse("10000000-0000-0000-0000-000000000001").ToString();
        return
        [
            Row(Guid.NewGuid(), "1042", "DEV-1", "CASHIER_LOGIN"),
            Row(Guid.NewGuid(), "1042", "DEV-1", "CART_ITEM_ADD"),
            Row(Guid.NewGuid(), "1042", "DEV-1", "CART_ITEM_QUANTITY_CHANGE"),
            Row(Guid.NewGuid(), "1042", "DEV-1", "PAYMENT_TENDER_ADD") with { PaymentMethod = "Cash" },
            Row(Guid.NewGuid(), "1042", "DEV-1", "SALE_COMPLETE") with
            {
                ReasonCode = "SALE",
                OrderGuid = orderGuid,
            },
        ];
    }

    private static AuditRow Row(Guid eventId, string store, string device, string operationType) => new(
        eventId,
        DateTimeOffset.Parse("2026-07-11T10:00:00Z", CultureInfo.InvariantCulture),
        DateTimeOffset.Parse("2026-07-11T10:00:01Z", CultureInfo.InvariantCulture),
        operationType,
        "Succeeded",
        store,
        device,
        "cashier-1",
        "Cashier One",
        "instance-1",
        null,
        null,
        null);

    private static IReadOnlyList<AuditRow> Replace(
        IReadOnlyList<AuditRow> rows,
        int index,
        AuditRow replacement) => rows.Select((row, itemIndex) =>
        itemIndex == index ? replacement : row).ToArray();
}

public sealed class WpfAppFixtureMessageTests
{
    [Fact]
    public void Missing_control_timeout_reports_step_and_automation_id()
    {
        using var fixture = new WpfAppFixture();

        var error = Assert.ThrowsAny<Exception>(() => fixture.WaitForAutomationId(
            "MissingControl",
            TimeSpan.FromMilliseconds(1),
            step: "收银员登录"));

        Assert.Contains("收银员登录", error.Message);
        Assert.Contains("MissingControl", error.Message);
    }

    [Fact]
    public void Capture_failure_redaction_error_does_not_replace_business_exception()
    {
        using var fixture = new WpfAppFixture();
        var original = new InvalidOperationException("原业务异常");
        var captured = false;

        var evidence = fixture.CaptureFailure(
            "失败步骤",
            () => throw new InvalidOperationException("脱敏失败"),
            _ => captured = true);
        var reported = evidence.Wrap(original);

        Assert.Same(original, reported.InnerException);
        Assert.False(captured);
        Assert.False(evidence.ScreenshotCaptured);
        Assert.DoesNotContain("脱敏失败", reported.Message);
    }

    [Fact]
    public void Failure_evidence_reports_redacted_screenshot_and_client_log_paths()
    {
        using var fixture = new WpfAppFixture();
        var evidence = fixture.CaptureFailure("失败步骤", () => { }, _ => { });

        var reported = evidence.Wrap(new InvalidOperationException("原业务异常"));

        Assert.False(evidence.ScreenshotCaptured);
        Assert.Contains(evidence.ScreenshotPath, reported.Message);
        Assert.Contains(evidence.ClientLogPath, reported.Message);
        Assert.Equal(
            Path.Combine(fixture.EvidenceDirectory, "hbpos-client.log"),
            evidence.ClientLogPath);
        Assert.DoesNotContain("cashier-secret", reported.Message);
        Assert.DoesNotContain("product-secret", reported.Message);
        Assert.DoesNotContain("token-secret", reported.Message);
    }

    [Fact]
    public void Failure_evidence_without_a_file_defaults_to_not_captured()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            "hbpos-ui-tests",
            Guid.NewGuid().ToString("N"),
            "missing.png");

        var evidence = new WpfFailureEvidence(missingPath, "client.log");

        Assert.False(File.Exists(missingPath));
        Assert.False(evidence.ScreenshotCaptured);
    }

    [Fact]
    public void Submitted_sensitive_value_skips_window_screenshot()
    {
        using var fixture = new WpfAppFixture();
        var redacted = false;
        var captured = false;

        var evidence = fixture.CaptureFailure(
            "失败步骤",
            allowScreenshot: false,
            () => redacted = true,
            _ => captured = true);

        Assert.False(redacted);
        Assert.False(captured);
        Assert.False(evidence.ScreenshotCaptured);
        Assert.Contains("未生成", evidence.Wrap(new InvalidOperationException("原业务异常")).Message);
    }

    [Fact]
    public void Live_launch_preflight_rejects_existing_target_executable()
    {
        var executablePath = Path.GetFullPath("Hbpos.Client.Wpf.exe");
        var inspected = 0;

        var error = Assert.Throws<InvalidOperationException>(() =>
            WpfAppFixture.EnsureSafeToLaunch(executablePath, "--culture=en-AU", _ =>
            {
                inspected++;
                return [executablePath];
            }));

        Assert.Equal(1, inspected);
        Assert.Contains("已有进程", error.Message);
    }

    [Fact]
    public void Live_launch_preflight_rejects_unreadable_same_name_process_path()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            WpfAppFixture.EnsureSafeToLaunch(
                Path.GetFullPath("Hbpos.Client.Wpf.exe"),
                "--culture=en-AU",
                _ => [null]));

        Assert.Contains("已有进程", error.Message);
    }

    [Fact]
    public void Preview_launch_preflight_does_not_inspect_existing_processes()
    {
        var inspected = false;

        WpfAppFixture.EnsureSafeToLaunch(
            Path.GetFullPath("Hbpos.Client.Wpf.exe"),
            "--preview --screen=pos",
            _ =>
            {
                inspected = true;
                return [Path.GetFullPath("Hbpos.Client.Wpf.exe")];
            });

        Assert.False(inspected);
    }
}

public sealed class SaleFlowWaitTests
{
    [Fact]
    public void Waits_until_delayed_ui_element_is_available()
    {
        var expected = new object();
        var attempts = 0;

        var result = SaleFlowTests.WaitForUiElement(
            () => ++attempts < 3 ? null : expected,
            "等待购物车数量",
            "CartLineQuantity",
            TimeSpan.FromSeconds(1));

        Assert.Same(expected, result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public void Delayed_ui_element_timeout_reports_step_and_automation_id()
    {
        var error = Assert.ThrowsAny<Exception>(() => SaleFlowTests.WaitForUiElement<object>(
            () => null,
            "等待购物车加号按钮",
            "CartLineIncreaseButton",
            TimeSpan.FromMilliseconds(1)));

        Assert.Contains("等待购物车加号按钮", error.Message);
        Assert.Contains("CartLineIncreaseButton", error.Message);
    }

    [Fact]
    public void Audit_window_must_not_expire_before_post_validation()
    {
        var windowTo = DateTimeOffset.Parse("2026-07-11T10:10:00Z", CultureInfo.InvariantCulture);

        SaleFlowTests.EnsureAuditWindowOpen(windowTo, windowTo);
        var error = Assert.Throws<InvalidOperationException>(() =>
            SaleFlowTests.EnsureAuditWindowOpen(windowTo.AddTicks(1), windowTo));

        Assert.Contains("审计时间窗", error.Message);
    }
}

public sealed class LiveE2eConfigurationTests
{
    [Theory]
    [InlineData("1002")]
    [InlineData("10420")]
    [InlineData("")]
    public void Rejects_store_outside_exact_allowlist(string storeCode)
    {
        var values = ValidValues();
        values["HBPOS_E2E_STORE_CODE"] = storeCode;

        var error = Assert.Throws<InvalidOperationException>(() =>
            LiveE2eConfiguration.FromEnvironment(name => values.GetValueOrDefault(name)));

        Assert.Contains("HBPOS_E2E_STORE_CODE", error.Message);
        AssertSecretsRedacted(error, values);
    }

    [Fact]
    public void Accepts_only_complete_1042_configuration()
    {
        var values = ValidValues();

        var result = LiveE2eConfiguration.FromEnvironment(name => values.GetValueOrDefault(name));

        Assert.Equal("1042", result.StoreCode);
        Assert.Equal(new Uri("http://localhost:5159/"), result.PosApiBaseUrl);
    }

    [Fact]
    public void Accepts_complete_1005_configuration()
    {
        var values = ValidValues();
        values["HBPOS_E2E_STORE_CODE"] = "1005";

        var result = LiveE2eConfiguration.FromEnvironment(name => values.GetValueOrDefault(name));

        Assert.Equal("1005", result.StoreCode);
    }

    [Theory]
    [InlineData("HBPOS_E2E_ENABLED", "false")]
    [InlineData("HBPOS_E2E_ENABLED", "")]
    [InlineData("HBPOS_E2E_ADMIN_AUDIT_SCOPE_CONFIRMED", "false")]
    [InlineData("HBPOS_E2E_ADMIN_AUDIT_SCOPE_CONFIRMED", "")]
    public void Rejects_each_confirmation_switch_unless_explicitly_true(string name, string value)
    {
        var values = ValidValues();
        values[name] = value;

        var error = Assert.Throws<InvalidOperationException>(() =>
            LiveE2eConfiguration.FromEnvironment(key => values.GetValueOrDefault(key)));

        Assert.Contains(name, error.Message);
        AssertSecretsRedacted(error, values);
    }

    [Theory]
    [InlineData("HBPOS_E2E_STORE_CODE")]
    [InlineData("HBPOS_E2E_CASHIER_BARCODE")]
    [InlineData("HBPOS_E2E_PRODUCT_BARCODE")]
    [InlineData("HBPOS_API_BASE_URL")]
    [InlineData("HBPOS_E2E_BACKEND_BASE_URL")]
    [InlineData("HBPOS_E2E_BACKEND_BEARER_TOKEN")]
    public void Rejects_each_missing_required_value_without_exposing_secrets(string name)
    {
        var values = ValidValues();
        values.Remove(name);

        var error = Assert.Throws<InvalidOperationException>(() =>
            LiveE2eConfiguration.FromEnvironment(key => values.GetValueOrDefault(key)));

        Assert.Contains(name, error.Message);
        AssertSecretsRedacted(error, values);
    }

    [Theory]
    [InlineData("HBPOS_API_BASE_URL", "not-a-uri")]
    [InlineData("HBPOS_API_BASE_URL", "/relative")]
    [InlineData("HBPOS_API_BASE_URL", "ftp://localhost/")]
    [InlineData("HBPOS_E2E_BACKEND_BASE_URL", "not-a-uri")]
    [InlineData("HBPOS_E2E_BACKEND_BASE_URL", "/relative")]
    [InlineData("HBPOS_E2E_BACKEND_BASE_URL", "ftp://backend.example.test/")]
    public void Rejects_invalid_or_relative_uri_without_exposing_secrets(string name, string value)
    {
        var values = ValidValues();
        values[name] = value;

        var error = Assert.Throws<InvalidOperationException>(() =>
            LiveE2eConfiguration.FromEnvironment(key => values.GetValueOrDefault(key)));

        Assert.Contains(name, error.Message);
        AssertSecretsRedacted(error, values);
    }

    [Fact]
    public void Reads_each_environment_value_once()
    {
        var values = ValidValues();
        var readCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        _ = LiveE2eConfiguration.FromEnvironment(name =>
        {
            readCounts[name] = readCounts.GetValueOrDefault(name) + 1;
            return values.GetValueOrDefault(name);
        });

        foreach (var name in values.Keys)
        {
            Assert.Equal(1, readCounts.GetValueOrDefault(name));
        }
    }

    [Theory]
    [InlineData("Store: Stafford (1042)", "1042")]
    [InlineData("Store 1005", "1005")]
    public void Extracts_only_an_exact_allowed_store_from_ui(string display, string expected)
    {
        Assert.Equal(expected, SaleFlowTests.ExtractAllowedStore(display));
    }

    [Theory]
    [InlineData("Store 1002")]
    [InlineData("Store 10420")]
    [InlineData("1042 / 1005")]
    public void Rejects_ambiguous_or_outside_store_from_ui(string display)
    {
        Assert.Throws<InvalidOperationException>(() => SaleFlowTests.ExtractAllowedStore(display));
    }

    private static void AssertSecretsRedacted(
        Exception error,
        IReadOnlyDictionary<string, string> values)
    {
        foreach (var name in new[]
                 {
                     "HBPOS_E2E_CASHIER_BARCODE",
                     "HBPOS_E2E_PRODUCT_BARCODE",
                     "HBPOS_E2E_BACKEND_BEARER_TOKEN",
                 })
        {
            if (values.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                Assert.DoesNotContain(value, error.Message);
        }
    }

    private static Dictionary<string, string> ValidValues() => new(StringComparer.Ordinal)
    {
        ["HBPOS_E2E_ENABLED"] = "true",
        ["HBPOS_E2E_ADMIN_AUDIT_SCOPE_CONFIRMED"] = "true",
        ["HBPOS_E2E_STORE_CODE"] = "1042",
        ["HBPOS_E2E_CASHIER_BARCODE"] = "test-cashier-secret",
        ["HBPOS_E2E_PRODUCT_BARCODE"] = "test-product-secret",
        ["HBPOS_API_BASE_URL"] = "http://localhost:5159/",
        ["HBPOS_E2E_BACKEND_BASE_URL"] = "https://backend.example.test/",
        ["HBPOS_E2E_BACKEND_BEARER_TOKEN"] = "test-bearer-secret",
        ["HBPOS_OPERATION_AUDIT_UPLOAD_ENABLED"] = "true",
    };
}

public sealed class LiveDeviceBindingTests
{
    [Fact]
    public async Task Reads_latest_binding_and_rejects_other_store_without_exposing_authorization_code()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-device-{Guid.NewGuid():N}.db");
        const string authorizationCode = "test-authorization-secret";

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            };
            await using (var connection = new SqliteConnection(builder.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE DeviceCache (
                        StoreCode TEXT NOT NULL,
                        DeviceCode TEXT NOT NULL,
                        AuthorizationCodeProtected TEXT NULL,
                        UpdatedAt TEXT NOT NULL
                    );
                    INSERT INTO DeviceCache (StoreCode, DeviceCode, AuthorizationCodeProtected, UpdatedAt)
                    VALUES ('1042', 'DEV-1042', $authorizationCode, '2026-07-11T00:00:00Z');
                    """;
                command.Parameters.AddWithValue("$authorizationCode", authorizationCode);
                await command.ExecuteNonQueryAsync();
            }

            var binding = await LiveDeviceBinding.ReadLatestAsync(databasePath);

            Assert.Equal("1042", binding.StoreCode);
            Assert.Equal("DEV-1042", binding.DeviceCode);
            binding.EnsureMatches("1042");
            var error = Assert.Throws<InvalidOperationException>(() => binding.EnsureMatches("1005"));
            Assert.DoesNotContain(authorizationCode, error.Message);
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }
}

[Collection(WpfUiCollection.Name)]
public sealed class SaleFlowTests
{
    private readonly WpfAppFixture _app;

    public SaleFlowTests(WpfAppFixture app) => _app = app;

    [Fact]
    [Trait("Category", "LiveE2e")]
    public async Task Store_cash_sale_reaches_payment_success()
    {
        var sensitiveValueSubmitted = false;
        try
        {
            // 关键门禁必须全部先于 WPF 启动，避免门店不匹配时产生任何业务操作。
            var config = LiveE2eConfiguration.FromEnvironment();
            var device = await LiveDeviceBinding.ReadLatestAsync();
            device.EnsureMatches(config.StoreCode);
            var runStartedUtc = DateTimeOffset.UtcNow;
            var windowFrom = runStartedUtc.AddMinutes(-2);
            var windowTo = runStartedUtc.AddMinutes(10);
            using var backend = new OperationAuditBackendClient(
                config.BackendBaseUrl,
                config.BackendBearerToken);
            // 全门店基线必须早于 WPF 启动，先证明管理端只读权限可用再做业务操作。
            var beforeRows = await backend.FetchAllAsync(
                new AuditQuery(windowFrom, windowTo),
                CancellationToken.None);
            var before = AuditSnapshot.Create(beforeRows);

            var window = _app.Launch("--culture=en-AU", new Dictionary<string, string?>
            {
                ["HBPOS_API_BASE_URL"] = config.PosApiBaseUrl.ToString(),
                ["HBPOS_OPERATION_AUDIT_UPLOAD_ENABLED"] = "true",
            });
            Assert.Equal("PosMainWindow", window.AutomationId);
            Assert.Equal(config.StoreCode, ExtractAllowedStore(_app.WaitForAutomationId(
                "CurrentStoreInfo",
                step: "校验当前门店").Name));

            var login = _app.WaitForAutomationId("CashierLoginInput", step: "等待收银员登录输入框");
            login.Focus();
            // 一旦开始输入任一受保护值，界面其他状态区域可能回显该值，后续失败必须跳过整窗截图。
            sensitiveValueSubmitted = true;
            Keyboard.Type(config.CashierBarcode);
            Keyboard.Type(VirtualKeyShort.RETURN);
            WaitUntilHidden("CashierLoginOverlay");

            var scan = _app.WaitForAutomationId("ProductBarcodeInput", step: "等待商品扫码输入框");
            scan.Focus();
            Keyboard.Type(config.ProductBarcode);
            Keyboard.Type(VirtualKeyShort.RETURN);
            var row = WaitForSingleCartRow("CartItemsGrid");
            var quantity = ReadQuantity(row, "CartLineQuantity");
            // 行模板中的 AutomationId 会重复，必须在目标行子树内定位。
            WaitForUiElement(
                    () => row.FindFirstDescendant("CartLineIncreaseButton"),
                    "等待购物车加号按钮",
                    "CartLineIncreaseButton")
                .AsButton()
                .Invoke();
            WaitForQuantity(row, quantity + 1m);

            _app.WaitForAutomationId("OpenPaymentButton", step: "等待支付入口").AsButton().Invoke();
            _app.WaitForAutomationId("PaymentScreen", TimeSpan.FromSeconds(30), "等待支付页面");
            _app.WaitForAutomationId("AddCashTenderButton", step: "等待现金付款按钮").AsButton().Invoke();
            WaitForTenderAndZeroRemaining();
            var confirm = _app.WaitForAutomationId("ConfirmPaymentButton", step: "等待确认支付按钮").AsButton();
            Assert.True(confirm.IsEnabled);
            confirm.Invoke();
            _app.WaitForAutomationId("PaymentSuccessCard", TimeSpan.FromSeconds(60), "等待收银主页的支付成功卡片");
            Assert.NotEqual("-", _app.WaitForAutomationId(
                "CompletedTransactionId",
                step: "等待交易编号").Name);
            var successEvidence = _app.CaptureFailure(
                $"{nameof(Store_cash_sale_reaches_payment_success)}-success");
            Assert.True(File.Exists(successEvidence.ScreenshotPath));
            // 成功页留证后优雅退出，让客户端执行最后一次 outbox flush。
            Assert.True(_app.CloseOwnedProcess(), "WPF 进程未能优雅关闭，无法确认最后一次 outbox flush。");
            EnsureAuditWindowOpen(DateTimeOffset.UtcNow, windowTo);

            var required = await backend.PollRequiredAsync(
                new AuditQuery(windowFrom, windowTo, config.StoreCode, device.DeviceCode),
                before.EventIds,
                TimeSpan.FromSeconds(120),
                CancellationToken.None);
            var sale = RequiredEventValidator.Validate(required, config.StoreCode, device.DeviceCode);
            await backend.PollOrderAsync(sale.OrderGuid!, config.StoreCode, CancellationToken.None);

            EnsureAuditWindowOpen(DateTimeOffset.UtcNow, windowTo);
            var afterRows = await backend.FetchAllAsync(
                new AuditQuery(windowFrom, windowTo),
                CancellationToken.None);
            EnsureAuditWindowOpen(DateTimeOffset.UtcNow, windowTo);
            var after = AuditSnapshot.Create(afterRows);
            AuditSnapshot.AssertOtherStoresUnchanged(
                before,
                after,
                new HashSet<string> { "1042", "1005" });
            AuditSnapshot.AssertNewTargetDeviceBelongsToStore(
                before,
                afterRows,
                device.DeviceCode,
                config.StoreCode);

            var newIds = new HashSet<Guid>(after.EventIds);
            newIds.ExceptWith(before.EventIds);
            // 详情只查本轮新增事件，避免把历史业务数据误判为本次销售证据。
            foreach (var auditEvent in AuditDetailValidator.SelectNewRequiredEvents(required, newIds))
            {
                using var detail = await backend.GetDetailAsync(auditEvent.EventId, CancellationToken.None);
                AuditDetailValidator.AssertContainsProduct(detail, config.ProductBarcode);
            }
        }
        catch (Exception error)
        {
            throw _app.CaptureFailure(
                    nameof(Store_cash_sale_reaches_payment_success),
                    allowScreenshot: !sensitiveValueSubmitted)
                .Wrap(error);
        }
        finally
        {
            _app.CloseOwnedProcess();
        }
    }

    internal static string ExtractAllowedStore(string display)
    {
        var matches = Regex.Matches(
            display,
            @"(?<!\d)(?:1042|1005)(?!\d)",
            RegexOptions.CultureInvariant);
        if (matches.Count != 1)
            throw new InvalidOperationException("当前界面门店必须且只能匹配 1042 或 1005。");
        return matches[0].Value;
    }

    internal static void EnsureAuditWindowOpen(DateTimeOffset now, DateTimeOffset windowTo)
    {
        if (now > windowTo)
            throw new InvalidOperationException("固定审计时间窗已过期，后置校验已中止。");
    }

    internal static T WaitForUiElement<T>(
        Func<T?> find,
        string step,
        string automationId,
        TimeSpan? timeout = null)
        where T : class =>
        Retry.WhileNull(
            find,
            timeout: timeout ?? TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(100),
            throwOnTimeout: true,
            ignoreException: true,
            timeoutMessage: $"{step}超时，AutomationId={automationId}。")
        .Result!;

    private void WaitUntilHidden(string automationId)
    {
        Retry.WhileFalse(
            () =>
            {
                var element = _app.MainWindow?.FindFirstDescendant(automationId);
                return element is null || element.IsOffscreen;
            },
            timeout: TimeSpan.FromSeconds(30),
            interval: TimeSpan.FromMilliseconds(100),
            throwOnTimeout: true,
            ignoreException: true,
            timeoutMessage: $"等待登录遮盖隐藏超时，AutomationId={automationId}。");
    }

    private AutomationElement WaitForSingleCartRow(string automationId)
    {
        var grid = _app.WaitForAutomationId(automationId, step: "等待购物车列表").AsGrid();
        return Retry.WhileNull(
            () =>
            {
                var rows = grid.Rows;
                return rows.Length == 1 ? rows[0] : null;
            },
            timeout: TimeSpan.FromSeconds(30),
            interval: TimeSpan.FromMilliseconds(100),
            throwOnTimeout: true,
            ignoreException: true,
            timeoutMessage: $"等待唯一购物车行超时，AutomationId={automationId}。")
        .Result!;
    }

    private static decimal ReadQuantity(AutomationElement row, string automationId)
    {
        var quantity = WaitForUiElement(
            () => row.FindFirstDescendant(automationId),
            "等待购物车数量控件",
            automationId);
        return ReadNumber(quantity.Name, automationId);
    }

    private static void WaitForQuantity(AutomationElement row, decimal expected)
    {
        const string automationId = "CartLineQuantity";
        Retry.WhileFalse(
            () => ReadQuantity(row, automationId) == expected,
            timeout: TimeSpan.FromSeconds(15),
            interval: TimeSpan.FromMilliseconds(100),
            throwOnTimeout: true,
            ignoreException: true,
            timeoutMessage: $"等待购物车数量更新超时，AutomationId={automationId}。");
    }

    private void WaitForTenderAndZeroRemaining()
    {
        const string tenderId = "AppliedTendersList";
        const string remainingId = "RemainingAmount";
        var tenders = _app.WaitForAutomationId(tenderId, step: "等待已应用付款项列表");
        var remaining = _app.WaitForAutomationId(remainingId, step: "等待剩余金额");
        Retry.WhileFalse(
            () => tenders.FindAllChildren().Length > 0 && ReadNumber(remaining.Name, remainingId) == 0m,
            timeout: TimeSpan.FromSeconds(30),
            interval: TimeSpan.FromMilliseconds(100),
            throwOnTimeout: true,
            ignoreException: true,
            timeoutMessage: $"等待现金付款项和余额归零超时，AutomationId={tenderId}/{remainingId}。");
    }

    private static decimal ReadNumber(string text, string automationId)
    {
        var normalized = Regex.Replace(text, @"[^0-9,.-]", string.Empty);
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.GetCultureInfo("en-AU"), out var value))
            return value;
        throw new InvalidOperationException($"AutomationId={automationId} 的数值格式无效。");
    }
}

internal sealed record LiveDeviceBinding(string StoreCode, string DeviceCode)
{
    public static async Task<LiveDeviceBinding> ReadLatestAsync(
        string? databasePath = null,
        CancellationToken cancellationToken = default)
    {
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Hbpos.Client", "hbpos_client.db");
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StoreCode, DeviceCode FROM DeviceCache ORDER BY UpdatedAt DESC LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("本机没有已注册的 WPF 设备绑定。");
        return new LiveDeviceBinding(reader.GetString(0).Trim(), reader.GetString(1).Trim());
    }

    public void EnsureMatches(string targetStoreCode)
    {
        if (!string.Equals(StoreCode, targetStoreCode, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(DeviceCode))
            throw new InvalidOperationException("本机设备门店与 HBPOS_E2E_STORE_CODE 不一致，已在业务操作前中止。");
    }
}

internal sealed record LiveE2eConfiguration(
    string StoreCode,
    string CashierBarcode,
    string ProductBarcode,
    Uri PosApiBaseUrl,
    Uri BackendBaseUrl,
    string BackendBearerToken)
{
    private static readonly HashSet<string> AllowedStores = ["1042", "1005"];

    public static LiveE2eConfiguration FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        if (!string.Equals(read("HBPOS_E2E_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("必须显式设置 HBPOS_E2E_ENABLED=true。");
        if (!string.Equals(read("HBPOS_E2E_ADMIN_AUDIT_SCOPE_CONFIRMED"), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("必须确认 HBPOS_E2E_ADMIN_AUDIT_SCOPE_CONFIRMED=true。");
        if (string.Equals(read("HBPOS_OPERATION_AUDIT_UPLOAD_ENABLED"), "false", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HBPOS_OPERATION_AUDIT_UPLOAD_ENABLED 不能为 false。");

        var store = Required("HBPOS_E2E_STORE_CODE", read);
        if (!AllowedStores.Contains(store))
            throw new InvalidOperationException("HBPOS_E2E_STORE_CODE 只允许 1042 或 1005。");

        return new LiveE2eConfiguration(
            store,
            Required("HBPOS_E2E_CASHIER_BARCODE", read),
            Required("HBPOS_E2E_PRODUCT_BARCODE", read),
            RequiredUri("HBPOS_API_BASE_URL", read),
            RequiredUri("HBPOS_E2E_BACKEND_BASE_URL", read),
            Required("HBPOS_E2E_BACKEND_BEARER_TOKEN", read));
    }

    private static string Required(string name, Func<string, string?> read)
    {
        var value = read(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"缺少环境变量 {name}。")
            : value.Trim();
    }

    private static Uri RequiredUri(string name, Func<string, string?> read)
    {
        var value = Required(name, read);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"环境变量 {name} 必须是绝对 HTTP/HTTPS URI。");
        return uri;
    }
}

internal sealed record AuditRow(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ReceivedAtUtc,
    string OperationType,
    string Outcome,
    string StoreCode,
    string DeviceCode,
    string? CashierId,
    string? CashierName,
    string? InstanceId,
    string? OrderGuid,
    string? PaymentMethod,
    string? ReasonCode);

internal sealed record AuditQuery(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string? StoreCode = null,
    string? DeviceCode = null,
    string? CashierKeyword = null);

internal sealed class OperationAuditBackendClient : IDisposable
{
    private const int PageSize = 200;
    private const int MaxPageCount = 1_000;
    private static readonly TimeSpan DefaultMaxPollDuration = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly Uri _baseUrl;
    private readonly HttpClient _client;
    private readonly TimeSpan _maxPollDuration;

    public OperationAuditBackendClient(
        Uri baseUrl,
        string bearerToken,
        HttpMessageHandler? handler = null,
        TimeSpan? maxPollDuration = null)
    {
        _baseUrl = baseUrl;
        _client = handler is null ? new HttpClient() : new HttpClient(handler);
        _maxPollDuration = maxPollDuration ?? DefaultMaxPollDuration;
        if (_maxPollDuration <= TimeSpan.Zero || _maxPollDuration > DefaultMaxPollDuration)
            throw new ArgumentOutOfRangeException(nameof(maxPollDuration), "轮询上限必须大于 0 且不超过 120 秒。");
        // Bearer token 只进入 Authorization header，绝不拼接到 URI 或错误消息。
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<IReadOnlyList<AuditRow>> FetchAllAsync(
        AuditQuery query,
        CancellationToken cancellationToken)
    {
        var rows = new List<AuditRow>();
        var eventIds = new HashSet<Guid>();
        int? expectedTotal = null;
        var expectedPageCount = 1;
        for (var pageNumber = 1; ; pageNumber++)
        {
            using var response = await _client.GetAsync(BuildListUri(query, pageNumber), cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (!root.GetProperty("success").GetBoolean())
                throw new InvalidOperationException("后台操作日志查询未成功。");
            var data = root.GetProperty("data");
            var page = data.GetProperty("items").Deserialize<AuditRow[]>(JsonOptions) ?? [];
            var total = data.GetProperty("total").GetInt32();
            if (total < 0) throw new InvalidOperationException("后台操作日志总数无效。");
            if (expectedTotal is null)
            {
                expectedTotal = total;
                var pageCount = Math.Max(1L, (total + (long)PageSize - 1) / PageSize);
                if (pageCount > MaxPageCount)
                    throw new InvalidOperationException("后台操作日志超过分页上限。");
                expectedPageCount = (int)pageCount;
            }
            else if (total != expectedTotal.Value)
            {
                throw new InvalidOperationException("后台操作日志分页总数变化。");
            }

            if (page.Length > PageSize)
                throw new InvalidOperationException("后台操作日志单页数量超过 pageSize。");
            if (page.Length == 0)
            {
                if (expectedTotal.Value == 0) return rows;
                throw new InvalidOperationException("后台操作日志空页与 total 不一致。");
            }

            foreach (var row in page)
            {
                if (!eventIds.Add(row.EventId))
                    throw new InvalidOperationException("后台操作日志分页包含重复 EventId。");
                rows.Add(row);
            }

            if (eventIds.Count > expectedTotal.Value)
                throw new InvalidOperationException("后台操作日志唯一 EventId 数量超过 total。");
            if (eventIds.Count == expectedTotal.Value) return rows;
            if (page.Length < PageSize)
                throw new InvalidOperationException("后台操作日志短页与 total 不一致。");
            if (pageNumber >= expectedPageCount)
                throw new InvalidOperationException("后台操作日志最终唯一 EventId 数量与 total 不一致。");
        }
    }

    public async Task<IReadOnlyList<AuditRow>> PollRequiredAsync(
        AuditQuery query,
        ISet<Guid> excludedEventIds,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var effectiveTimeout = timeout > _maxPollDuration ? _maxPollDuration : timeout;
        var elapsed = Stopwatch.StartNew();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var lastValidationReason = "尚未观察到当前运行的完整事件集合";
        string? cashierId = null;
        string? instanceId = null;
        if (effectiveTimeout <= TimeSpan.Zero)
            throw CreateRequiredTimeout(observed, lastValidationReason);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveTimeout);

        try
        {
            while (true)
            {
                if (cashierId is null)
                {
                    var loginRows = (await FetchAllAsync(
                            query with { CashierKeyword = null },
                            deadline.Token))
                        .Where(row => !excludedEventIds.Contains(row.EventId))
                        .ToArray();
                    observed.UnionWith(loginRows.Select(row => row.OperationType));
                    var login = loginRows
                        .Where(row => row.OperationType == "CASHIER_LOGIN" &&
                                      row.StoreCode == query.StoreCode &&
                                      row.DeviceCode == query.DeviceCode &&
                                      row.Outcome == "Succeeded" &&
                                      !string.IsNullOrWhiteSpace(row.CashierId) &&
                                      !string.IsNullOrWhiteSpace(row.InstanceId))
                        .OrderByDescending(row => row.OccurredAtUtc)
                        .FirstOrDefault();
                    cashierId = login?.CashierId;
                    instanceId = login?.InstanceId;
                    if (login is null) lastValidationReason = "尚未找到带 InstanceId 的当前登录事件";
                }

                if (cashierId is not null && instanceId is not null)
                {
                    var rows = (await FetchAllAsync(
                            query with { CashierKeyword = cashierId },
                            deadline.Token))
                        // 后续事件必须与本次新登录的收银员和实例完全一致，禁止跨实例拼接。
                        .Where(row =>
                            !excludedEventIds.Contains(row.EventId) &&
                            row.CashierId == cashierId &&
                            row.InstanceId == instanceId)
                        .ToArray();
                    observed.UnionWith(rows.Select(row => row.OperationType));
                    try
                    {
                        _ = RequiredEventValidator.Validate(rows, query.StoreCode!, query.DeviceCode!);
                        return rows;
                    }
                    catch (InvalidOperationException error)
                    {
                        // 验证器只返回固定字段原因，不包含 token 或条码。
                        lastValidationReason = error.Message;
                    }
                }

                if (elapsed.Elapsed >= effectiveTimeout)
                    throw CreateRequiredTimeout(observed, lastValidationReason);
                await DelayAsync(elapsed.Elapsed, effectiveTimeout, deadline.Token);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw CreateRequiredTimeout(observed, lastValidationReason, error);
        }
    }

    public async Task<JsonDocument> GetDetailAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUrl, $"api/react/pos-operation-audits/{eventId:D}");
        using var response = await _client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.GetProperty("success").GetBoolean()) return document;
        document.Dispose();
        throw new InvalidOperationException("后台操作日志详情查询未成功。");
    }

    public async Task PollOrderAsync(
        string orderGuid,
        string storeCode,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(orderGuid, out var requestedOrderGuid))
            throw new InvalidOperationException("订单标识必须是 GUID。");
        var uri = new Uri(
            _baseUrl,
            $"api/react/v1/posm-sales-orders/detail/{requestedOrderGuid:D}");
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_maxPollDuration);

        try
        {
            while (true)
            {
                using var response = await _client.GetAsync(uri, deadline.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
                    var root = document.RootElement;
                    if (root.GetProperty("success").GetBoolean() &&
                        root.TryGetProperty("data", out var data) &&
                        data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("order", out var order) &&
                        order.ValueKind == JsonValueKind.Object)
                    {
                        if (!TryGetPropertyIgnoreCase(order, "orderGuid", out var returnedOrderGuidValue) ||
                            returnedOrderGuidValue.ValueKind != JsonValueKind.String ||
                            !Guid.TryParse(returnedOrderGuidValue.GetString(), out var returnedOrderGuid) ||
                            returnedOrderGuid != requestedOrderGuid)
                            throw new InvalidOperationException("后台订单缺少匹配的 OrderGuid。");
                        if (TryGetPropertyIgnoreCase(order, "branchCode", out var branchCode) &&
                            branchCode.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(branchCode.GetString()) &&
                            branchCode.GetString() != storeCode)
                            throw new InvalidOperationException("后台订单门店与目标门店不一致。");
                        return;
                    }
                }

                if (elapsed.Elapsed >= _maxPollDuration)
                    throw new TimeoutException("等待后台订单处理超时。");
                await DelayAsync(elapsed.Elapsed, _maxPollDuration, deadline.Token);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待后台订单处理超时。", error);
        }
    }

    private static TimeoutException CreateRequiredTimeout(
        IEnumerable<string> observed,
        string lastValidationReason,
        Exception? inner = null)
    {
        var message =
            $"等待必需操作日志超时，已观察 OperationType={string.Join(",", observed.Order())}；" +
            $"最后验证原因={lastValidationReason}。";
        return inner is null ? new TimeoutException(message) : new TimeoutException(message, inner);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static Task DelayAsync(
        TimeSpan elapsed,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var remaining = timeout - elapsed;
        var delay = remaining <= TimeSpan.Zero
            ? TimeSpan.Zero
            : remaining < PollInterval ? remaining : PollInterval;
        return Task.Delay(delay, cancellationToken);
    }

    private Uri BuildListUri(AuditQuery query, int pageNumber)
    {
        var endpoint = new Uri(_baseUrl, "api/react/pos-operation-audits");
        var values = new List<KeyValuePair<string, string>>
        {
            new("fromUtc", query.FromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            new("toUtc", query.ToUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            new("pageNumber", pageNumber.ToString(CultureInfo.InvariantCulture)),
            new("pageSize", PageSize.ToString(CultureInfo.InvariantCulture)),
        };
        AddOptional(values, "storeCode", query.StoreCode);
        AddOptional(values, "deviceCode", query.DeviceCode);
        AddOptional(values, "cashierKeyword", query.CashierKeyword);
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Join("&", values.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    private static void AddOptional(
        ICollection<KeyValuePair<string, string>> values,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) values.Add(new KeyValuePair<string, string>(name, value));
    }

    public void Dispose() => _client.Dispose();
}

internal sealed record StoreAuditSnapshot(
    int Count,
    DateTimeOffset? MaxReceivedAtUtc,
    ISet<Guid> EventIds);

internal sealed record AuditSnapshot(
    IReadOnlyDictionary<string, StoreAuditSnapshot> Stores,
    ISet<Guid> EventIds)
{
    public static AuditSnapshot Create(IEnumerable<AuditRow> rows)
    {
        var materialized = rows.ToArray();
        var stores = materialized
            .GroupBy(row => row.StoreCode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new StoreAuditSnapshot(
                    group.Count(),
                    group.Max(row => (DateTimeOffset?)row.ReceivedAtUtc),
                    group.Select(row => row.EventId).ToHashSet()),
                StringComparer.Ordinal);
        return new AuditSnapshot(stores, materialized.Select(row => row.EventId).ToHashSet());
    }

    public static void AssertOtherStoresUnchanged(
        AuditSnapshot before,
        AuditSnapshot after,
        ISet<string> allowedStores)
    {
        foreach (var storeCode in before.Stores.Keys.Union(after.Stores.Keys, StringComparer.Ordinal)
                     .Where(store => !allowedStores.Contains(store)))
        {
            before.Stores.TryGetValue(storeCode, out var beforeStore);
            after.Stores.TryGetValue(storeCode, out var afterStore);
            var unchanged = beforeStore is not null &&
                            afterStore is not null &&
                            beforeStore.Count == afterStore.Count &&
                            beforeStore.MaxReceivedAtUtc == afterStore.MaxReceivedAtUtc &&
                            beforeStore.EventIds.SetEquals(afterStore.EventIds);
            if (unchanged) continue;

            var changedIds = new HashSet<Guid>(beforeStore?.EventIds ?? Enumerable.Empty<Guid>());
            changedIds.SymmetricExceptWith(afterStore?.EventIds ?? Enumerable.Empty<Guid>());
            if (changedIds.Count == 0)
                changedIds.UnionWith(
                    (beforeStore?.EventIds ?? Enumerable.Empty<Guid>())
                    .Concat(afterStore?.EventIds ?? Enumerable.Empty<Guid>()));
            throw new InvalidOperationException(
                $"门店 {storeCode} 的审计快照发生变化，EventId={string.Join(",", changedIds.Order())}。");
        }
    }

    public static void AssertNewTargetDeviceBelongsToStore(
        AuditSnapshot before,
        IEnumerable<AuditRow> afterRows,
        string deviceCode,
        string storeCode)
    {
        var invalid = afterRows
            .Where(row =>
                !before.EventIds.Contains(row.EventId) &&
                row.DeviceCode == deviceCode &&
                row.StoreCode != storeCode)
            .ToArray();
        if (invalid.Length == 0) return;
        throw new InvalidOperationException(
            $"门店 {invalid[0].StoreCode} 出现目标设备的新事件，EventId={string.Join(",", invalid.Select(row => row.EventId).Order())}。");
    }
}

internal static class RequiredEventValidator
{
    private static readonly HashSet<string> RequiredOperationTypes =
    [
        "CASHIER_LOGIN",
        "CART_ITEM_ADD",
        "CART_ITEM_QUANTITY_CHANGE",
        "PAYMENT_TENDER_ADD",
        "SALE_COMPLETE",
    ];

    public static AuditRow Validate(
        IReadOnlyList<AuditRow> rows,
        string storeCode,
        string deviceCode)
    {
        var requiredRows = rows
            .Where(row => RequiredOperationTypes.Contains(row.OperationType))
            .ToArray();
        var login = requiredRows
            .Where(row => row.OperationType == "CASHIER_LOGIN" &&
                          row.StoreCode == storeCode &&
                          row.DeviceCode == deviceCode &&
                          row.Outcome == "Succeeded" &&
                          !string.IsNullOrWhiteSpace(row.CashierId) &&
                          !string.IsNullOrWhiteSpace(row.InstanceId))
            .OrderByDescending(row => row.OccurredAtUtc)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("缺少有效的 CASHIER_LOGIN 事件。");

        foreach (var operationType in RequiredOperationTypes)
        {
            var matching = requiredRows.Where(row => row.OperationType == operationType).ToArray();
            if (matching.Length == 0)
                throw new InvalidOperationException($"缺少 {operationType} 事件。");
            if (matching.Any(row =>
                    row.StoreCode != storeCode ||
                    row.DeviceCode != deviceCode ||
                    row.CashierId != login.CashierId ||
                    row.InstanceId != login.InstanceId ||
                    row.Outcome != "Succeeded"))
                throw new InvalidOperationException($"{operationType} 事件的门店、设备、收银员、实例或结果不一致。");
        }

        if (requiredRows.Where(row => row.OperationType == "PAYMENT_TENDER_ADD")
            .Any(row => row.PaymentMethod != "Cash"))
            throw new InvalidOperationException("PAYMENT_TENDER_ADD 必须是 Cash。");

        var sales = requiredRows.Where(row => row.OperationType == "SALE_COMPLETE").ToArray();
        if (sales.Any(row => row.ReasonCode != "SALE" || !Guid.TryParse(row.OrderGuid, out _)))
            throw new InvalidOperationException("SALE_COMPLETE 必须包含 SALE 原因和有效 OrderGuid。");
        return sales.OrderByDescending(row => row.OccurredAtUtc).First();
    }
}

internal static class AuditDetailValidator
{
    private static readonly string[] ProductIdentifierNames =
        ["productCode", "itemNumber", "referenceCode", "lookupCode"];
    private static readonly string[] DetailOperationTypes =
    [
        "CART_ITEM_ADD",
        "CART_ITEM_QUANTITY_CHANGE",
        "PAYMENT_TENDER_ADD",
        "SALE_COMPLETE",
    ];

    public static IReadOnlyList<AuditRow> SelectNewRequiredEvents(
        IReadOnlyList<AuditRow> rows,
        ISet<Guid> newEventIds)
    {
        var selected = new List<AuditRow>(DetailOperationTypes.Length);
        foreach (var operationType in DetailOperationTypes)
        {
            var row = rows
                .Where(item => item.OperationType == operationType && newEventIds.Contains(item.EventId))
                .OrderByDescending(item => item.OccurredAtUtc)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"新增事件中缺少 {operationType}。");
            selected.Add(row);
        }
        return selected;
    }

    public static void AssertContainsProduct(JsonDocument detail, string productBarcode)
    {
        var root = detail.RootElement;
        if (!root.GetProperty("success").GetBoolean() ||
            !root.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("操作日志详情缺少商品明细。");

        foreach (var item in items.EnumerateArray())
        {
            if (ProductIdentifierNames.Any(name =>
                    item.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    string.Equals(value.GetString(), productBarcode, StringComparison.Ordinal)))
                return;
        }

        throw new InvalidOperationException("操作日志详情未包含测试商品。");
    }
}
