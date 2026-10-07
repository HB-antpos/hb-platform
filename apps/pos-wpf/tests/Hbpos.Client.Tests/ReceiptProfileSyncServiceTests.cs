using System.Net;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Stores;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>
/// 总部下发小票资料的同步循环：版本比较、校验与串店丢弃、原子写入、回执重试与 400 终止、404 退避、
/// 同一时刻只有一个同步在飞、换店后重新拉取。服务会写 ConsoleLog，因此与其他全局日志用例串行。
/// </summary>
[Collection(GlobalLoggingTestCollection.Name)]
public sealed class ReceiptProfileSyncServiceTests
{
    [Fact]
    public async Task Scheduled_tick_runs_immediately_then_every_fourth_tick()
    {
        using var harness = new Harness();

        Assert.NotNull(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.NotNull(await harness.Service.RunScheduledTickAsync());

        // 每 4 个 15 秒拍同步一轮（约 60 秒）：第 1、5 拍各请求一次。
        Assert.Equal(2, harness.Api.SyncKnownVersions.Count);
    }

    [Fact]
    public async Task Scheduled_tick_waits_for_device_authorization_without_using_up_the_first_run()
    {
        using var harness = new Harness();
        harness.Auth.Clear();

        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Empty(harness.Api.SyncKnownVersions);

        harness.Auth.Set(new DeviceAuthorizationContext("DEV1", "S001", "HW", "AUTH"));

        // 认证就绪后的第一拍立即同步，而不是再等 4 拍。
        Assert.NotNull(await harness.Service.RunScheduledTickAsync());
        Assert.Single(harness.Api.SyncKnownVersions);
    }

    [Fact]
    public async Task Changed_profile_is_applied_atomically_acked_and_recorded()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(3)));
        var applied = new List<ReceiptProfileAppliedEventArgs>();
        harness.Service.ProfileApplied += (_, args) => applied.Add(args);

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Applied, 3), result);
        Assert.Equal([0], harness.Api.SyncKnownVersions);
        Assert.Equal([3], harness.Api.AckVersions);
        Assert.Equal(new ReceiptProfileLocalState(3, 3), await harness.Store.LoadProfileStateAsync("S001"));
        var loaded = await harness.Store.LoadAsync();
        Assert.Equal("HB Brand", loaded.BrandName);
        Assert.Equal("Sunnybank", loaded.StoreName);
        Assert.Equal(ProfileTestData.Address, loaded.StoreAddress);
        Assert.Equal(ProfileTestData.Phone, loaded.StorePhone);
        Assert.Equal(ProfileTestData.Abn, loaded.Abn);
        Assert.Equal("Return within 7 days", loaded.ReturnPolicy);
        // 旧快照（没有这两个字段）：本机为空串＝未定制，打印走默认文案。
        Assert.Equal(string.Empty, loaded.VoucherTerms);
        Assert.Equal(string.Empty, loaded.InstallmentTerms);
        var args = Assert.Single(applied);
        Assert.Equal("S001", args.StoreCode);
        Assert.Equal(3, args.Version);
    }

    [Fact]
    public async Task Changed_profile_with_terms_is_applied_to_local_settings_and_acked()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(
            3,
            voucherTerms: "Voucher line 1\r\n\r\nVoucher line 2",
            installmentTerms: "Installment line 1")));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Applied, 3), result);
        Assert.Equal([3], harness.Api.AckVersions);
        var loaded = await harness.Store.LoadAsync();
        Assert.Equal("Voucher line 1\r\n\r\nVoucher line 2", loaded.VoucherTerms);
        Assert.Equal("Installment line 1", loaded.InstallmentTerms);
        // 打印取正文时空行才会被丢弃；落盘保留总部下发的原样（仅首尾 trim）。
        Assert.Equal(["Voucher line 1", "Voucher line 2"], ReceiptTermsText.SplitLines(loaded.VoucherTerms));
    }

    [Fact]
    public async Task Terms_at_exactly_the_length_limit_with_line_breaks_and_tabs_are_accepted()
    {
        using var harness = new Harness();
        var voucherTerms = "a\t\r\n" + new string('v', ReceiptTermsText.MaxLength - 4);
        var installmentTerms = new string('i', ReceiptTermsText.MaxLength);
        Assert.Equal(ReceiptTermsText.MaxLength, voucherTerms.Length);
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(
            3, voucherTerms: voucherTerms, installmentTerms: installmentTerms)));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Applied, result.Outcome);
    }

    [Fact]
    public async Task Logs_never_contain_the_terms_text_even_when_the_profile_is_discarded()
    {
        using var harness = new Harness();
        using var capture = new ConsoleLogCapture();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(
            3, voucherTerms: "TOP-SECRET-VOUCHER-LINE", installmentTerms: "TOP-SECRET-INSTALLMENT-LINE")));
        await harness.Service.SyncNowAsync();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(
            4,
            voucherTerms: "TOP-SECRET-VOUCHER-LINE\u0001",
            installmentTerms: "TOP-SECRET-INSTALLMENT-LINE")));
        await harness.Service.SyncNowAsync();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(
            5, installmentTerms: "TOP-SECRET-INSTALLMENT-LINE" + new string('x', ReceiptTermsText.MaxLength))));
        await harness.Service.SyncNowAsync();

        var text = string.Join('\n', capture.Lines);
        Assert.Contains("applied version=3", text, StringComparison.Ordinal);
        Assert.Contains("Discarded", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Next_round_sends_local_version_and_unchanged_response_neither_writes_nor_acks_again()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (known, _) => Task.FromResult(
            known == 0
                ? ProfileTestData.Changed(ProfileTestData.Profile(3))
                : ProfileTestData.Unchanged(3));
        await harness.Service.SyncNowAsync();
        var writesAfterApply = harness.Repository.BatchWriteCount;

        var second = await harness.Service.SyncNowAsync();

        Assert.Equal(new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.UpToDate, 3), second);
        Assert.Equal([0, 3], harness.Api.SyncKnownVersions);
        Assert.Equal([3], harness.Api.AckVersions);
        Assert.Equal(writesAfterApply, harness.Repository.BatchWriteCount);
    }

    [Fact]
    public async Task Unchanged_response_with_unacknowledged_local_version_resends_the_ack()
    {
        using var harness = new Harness();
        await harness.Store.ApplyHeadquartersProfileAsync("S001", Fields(), 2);
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(2));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.UpToDate, result.Outcome);
        Assert.False(result.AckPending);
        Assert.Equal([2], harness.Api.AckVersions);
        Assert.Equal(new ReceiptProfileLocalState(2, 2), await harness.Store.LoadProfileStateAsync("S001"));
    }

    [Fact]
    public async Task Ack_network_failure_keeps_the_applied_profile_and_retries_next_round()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (known, _) => Task.FromResult(
            known == 0
                ? ProfileTestData.Changed(ProfileTestData.Profile(3))
                : ProfileTestData.Unchanged(3));
        harness.Api.OnAck = (_, _) => Task.FromException<StoreReceiptProfileAckResultDto>(
            new HttpRequestException("offline"));

        var first = await harness.Service.SyncNowAsync();

        // 资料已生效（打印立即用新资料），只是回执没成功。
        Assert.Equal(ReceiptProfileSyncOutcome.Applied, first.Outcome);
        Assert.True(first.AckPending);
        Assert.Equal(new ReceiptProfileLocalState(3, 0), await harness.Store.LoadProfileStateAsync("S001"));
        Assert.Equal("HB Brand", (await harness.Store.LoadAsync()).BrandName);

        harness.Api.OnAck = (version, _) => Task.FromResult(new StoreReceiptProfileAckResultDto(version));
        var second = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.UpToDate, second.Outcome);
        Assert.Equal([3, 3], harness.Api.AckVersions);
        Assert.Equal(new ReceiptProfileLocalState(3, 3), await harness.Store.LoadProfileStateAsync("S001"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Ack_401_403_and_5xx_are_retried_every_round(HttpStatusCode status)
    {
        using var harness = new Harness();
        harness.Api.OnSync = (known, _) => Task.FromResult(
            known == 0
                ? ProfileTestData.Changed(ProfileTestData.Profile(3))
                : ProfileTestData.Unchanged(3));
        harness.Api.OnAck = (_, _) => Task.FromException<StoreReceiptProfileAckResultDto>(
            ProfileTestData.Http(status));

        await harness.Service.SyncNowAsync();
        await harness.Service.SyncNowAsync();
        await harness.Service.SyncNowAsync();

        Assert.Equal([3, 3, 3], harness.Api.AckVersions);
        Assert.Equal(new ReceiptProfileLocalState(3, 0), await harness.Store.LoadProfileStateAsync("S001"));
    }

    [Fact]
    public async Task Ack_400_stops_retrying_the_same_version_and_does_not_roll_back_the_profile()
    {
        using var harness = new Harness();
        var latest = 3;
        harness.Api.OnSync = (known, _) => Task.FromResult(
            known == latest ? ProfileTestData.Unchanged(latest) : ProfileTestData.Changed(ProfileTestData.Profile(latest)));
        harness.Api.OnAck = (_, _) => Task.FromException<StoreReceiptProfileAckResultDto>(
            ProfileTestData.Http(HttpStatusCode.BadRequest, "RECEIPT_PROFILE_VERSION_INVALID"));

        var first = await harness.Service.SyncNowAsync();
        await harness.Service.SyncNowAsync();
        await harness.Service.SyncNowAsync();

        // 服务端不认这个版本：本进程内只试一次，避免每分钟死循环；已写入的本机资料不回滚。
        Assert.Equal(ReceiptProfileSyncOutcome.Applied, first.Outcome);
        Assert.Equal([3], harness.Api.AckVersions);
        Assert.Equal(new ReceiptProfileLocalState(3, 0), await harness.Store.LoadProfileStateAsync("S001"));
        Assert.Equal("HB Brand", (await harness.Store.LoadAsync()).BrandName);

        // 总部又下发了新版本：对新版本重新回执。
        latest = 4;
        harness.Api.OnAck = (version, _) => Task.FromResult(new StoreReceiptProfileAckResultDto(version));
        await harness.Service.SyncNowAsync();

        Assert.Equal([3, 4], harness.Api.AckVersions);
        Assert.Equal(new ReceiptProfileLocalState(4, 4), await harness.Store.LoadProfileStateAsync("S001"));
    }

    [Fact]
    public async Task Ack_rejection_is_forgotten_after_restart()
    {
        using var harness = new Harness();
        await harness.Store.ApplyHeadquartersProfileAsync("S001", Fields(), 3);
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(3));
        harness.Api.OnAck = (_, _) => Task.FromException<StoreReceiptProfileAckResultDto>(
            ProfileTestData.Http(HttpStatusCode.BadRequest, "RECEIPT_PROFILE_VERSION_INVALID"));
        await harness.Service.SyncNowAsync();
        await harness.Service.SyncNowAsync();
        Assert.Equal([3], harness.Api.AckVersions);

        // 重启 = 新的服务实例：允许再试一次。
        var restarted = harness.CreateService();
        await restarted.SyncNowAsync();

        Assert.Equal([3, 3], harness.Api.AckVersions);
    }

    [Fact]
    public async Task Old_server_without_version_fields_is_treated_as_never_released_and_keeps_local_settings()
    {
        using var harness = new Harness();
        await harness.Store.SaveAsync(ReceiptPrinterSettings.Default with { BrandName = "Hand Typed", StoreName = "Manual" });
        var writesBefore = harness.Repository.BatchWriteCount;
        // changed/version/profile 全部缺失时反序列化出的默认值。
        harness.Api.OnSync = (_, _) => Task.FromResult(new StoreReceiptProfileSyncDto(false, 0, null));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.NeverReleased, result.Outcome);
        Assert.Equal(writesBefore, harness.Repository.BatchWriteCount);
        Assert.Empty(harness.Api.AckVersions);
        var loaded = await harness.Store.LoadAsync();
        Assert.Equal("Hand Typed", loaded.BrandName);
        Assert.Equal(0, loaded.ProfileVersion);
    }

    [Fact]
    public async Task Server_reporting_no_release_after_local_apply_keeps_the_applied_profile_without_ack()
    {
        using var harness = new Harness();
        await harness.Store.ApplyHeadquartersProfileAsync("S001", Fields(), 5);
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(0));

        var result = await harness.Service.SyncNowAsync();

        // 快照表被重建：本机继续用最后一次应用的资料；服务端必然拒绝回执，所以不发。
        Assert.Equal(ReceiptProfileSyncOutcome.NeverReleased, result.Outcome);
        Assert.Empty(harness.Api.AckVersions);
        Assert.Equal(5, (await harness.Store.LoadAsync()).ProfileVersion);
    }

    [Fact]
    public async Task Changed_response_without_profile_is_discarded_without_writing()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(new StoreReceiptProfileSyncDto(true, 3, null));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Discarded, result.Outcome);
        Assert.Equal(0, harness.Repository.BatchWriteCount);
        Assert.Empty(harness.Api.AckVersions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task Changed_profile_with_missing_or_non_positive_version_is_discarded(int version)
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(
            new StoreReceiptProfileSyncDto(true, 3, ProfileTestData.Profile(version)));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Discarded, result.Outcome);
        Assert.Equal(0, harness.Repository.BatchWriteCount);
        Assert.Empty(harness.Api.AckVersions);
    }

    [Fact]
    public async Task Profile_for_another_store_is_discarded()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(
            ProfileTestData.Changed(ProfileTestData.Profile(3, storeCode: "S999")));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Discarded, result.Outcome);
        Assert.Equal(0, harness.Repository.BatchWriteCount);
        Assert.Empty(harness.Api.AckVersions);
        Assert.Equal(0, (await harness.Store.LoadAsync()).ProfileVersion);
    }

    [Theory]
    [InlineData(" s001 ")]
    [InlineData("S001")]
    [InlineData("s001")]
    public async Task Profile_store_code_matches_ignoring_case_and_whitespace(string profileStoreCode)
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(
            ProfileTestData.Changed(ProfileTestData.Profile(3, storeCode: profileStoreCode)));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Applied, result.Outcome);
        Assert.Equal(3, (await harness.Store.LoadAsync()).ProfileVersion);
    }

    [Theory]
    [InlineData("control-char-in-address")]
    [InlineData("del-in-brand")]
    [InlineData("newline-in-phone")]
    [InlineData("tab-in-abn")]
    [InlineData("newline-in-store-name")]
    [InlineData("control-char-in-store-code")]
    [InlineData("return-policy-too-long")]
    [InlineData("voucher-terms-too-long")]
    [InlineData("installment-terms-too-long")]
    [InlineData("control-char-in-voucher-terms")]
    [InlineData("control-char-in-installment-terms")]
    [InlineData("del-in-voucher-terms")]
    [InlineData("blank-store-name")]
    [InlineData("null-store-name")]
    public async Task Invalid_profile_is_discarded_with_no_partial_write_and_no_ack(string scenario)
    {
        var profile = scenario switch
        {
            "control-char-in-address" => ProfileTestData.Profile(3, address: "Bad\u0001Address"),
            "del-in-brand" => ProfileTestData.Profile(3, brandName: "Bad\u007FBrand"),
            "newline-in-phone" => ProfileTestData.Profile(3, phone: "07 3000\n0000"),
            "tab-in-abn" => ProfileTestData.Profile(3, abn: "12\t345"),
            "newline-in-store-name" => ProfileTestData.Profile(3, storeName: "Sunny\nbank"),
            "control-char-in-store-code" => ProfileTestData.Profile(3, storeCode: "S001\u0002"),
            "return-policy-too-long" => ProfileTestData.Profile(3, returnPolicy: new string('x', 501)),
            "voucher-terms-too-long" => ProfileTestData.Profile(3, voucherTerms: new string('x', 601)),
            "installment-terms-too-long" => ProfileTestData.Profile(3, installmentTerms: new string('x', 601)),
            "control-char-in-voucher-terms" => ProfileTestData.Profile(3, voucherTerms: "Line1\nBad\u0001Line"),
            "control-char-in-installment-terms" => ProfileTestData.Profile(3, installmentTerms: "Bad\u0008Line"),
            "del-in-voucher-terms" => ProfileTestData.Profile(3, voucherTerms: "Bad\u007FLine"),
            "blank-store-name" => ProfileTestData.Profile(3, storeName: "   "),
            "null-store-name" => ProfileTestData.Profile(3, storeName: null),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
        using var harness = new Harness();
        // 先应用一份有效的 v2，证明被丢弃的 v3 不会动已有资料。
        await harness.Store.ApplyHeadquartersProfileAsync("S001", Fields(), 2);
        await harness.Store.MarkProfileAckedAsync("S001", 2);
        var before = harness.Repository.Snapshot();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(profile));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Discarded, result.Outcome);
        Assert.Empty(harness.Api.AckVersions);
        Assert.Equal(before, harness.Repository.Snapshot());
    }

    [Fact]
    public async Task Multiline_address_and_return_policy_are_accepted()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(
            3,
            address: "Line1\r\nLine2\tTabbed",
            returnPolicy: "Policy A\nPolicy B\r\n" + new string('p', 400))));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Applied, result.Outcome);
        var loaded = await harness.Store.LoadAsync();
        Assert.Equal("Line1\r\nLine2\tTabbed", loaded.StoreAddress);
        Assert.StartsWith("Policy A\nPolicy B", loaded.ReturnPolicy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_404_reports_unsupported_and_backs_off_for_ten_minutes()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromException<StoreReceiptProfileSyncDto>(
            ProfileTestData.Http(HttpStatusCode.NotFound));

        var first = await harness.Service.RunScheduledTickAsync();
        Assert.Equal(ReceiptProfileSyncOutcome.ServerUnsupported, first!.Outcome);
        Assert.Single(harness.Api.SyncKnownVersions);

        // 10 分钟内即使到了 4 拍也不再请求。
        harness.Time.Advance(TimeSpan.FromMinutes(9));
        for (var tick = 0; tick < 12; tick++)
        {
            Assert.Null(await harness.Service.RunScheduledTickAsync());
        }

        Assert.Single(harness.Api.SyncKnownVersions);

        // 退避结束后的第一个到点拍重试；成功后恢复 60 秒节奏。
        harness.Time.Advance(TimeSpan.FromMinutes(2));
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(0));
        Assert.NotNull(await harness.Service.RunScheduledTickAsync());
        Assert.Equal(2, harness.Api.SyncKnownVersions.Count);

        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.Null(await harness.Service.RunScheduledTickAsync());
        Assert.NotNull(await harness.Service.RunScheduledTickAsync());
        Assert.Equal(3, harness.Api.SyncKnownVersions.Count);
    }

    [Fact]
    public async Task Manual_sync_ignores_the_not_found_backoff()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromException<StoreReceiptProfileSyncDto>(
            ProfileTestData.Http(HttpStatusCode.NotFound));
        await harness.Service.RunScheduledTickAsync();
        Assert.Single(harness.Api.SyncKnownVersions);

        var manual = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.ServerUnsupported, manual.Outcome);
        Assert.Equal(2, harness.Api.SyncKnownVersions.Count);
    }

    [Fact]
    public async Task Network_and_server_errors_return_failed_and_never_throw()
    {
        using var harness = new Harness();
        await harness.Store.ApplyHeadquartersProfileAsync("S001", Fields(), 2);
        var before = harness.Repository.Snapshot();

        harness.Api.OnSync = (_, _) => Task.FromException<StoreReceiptProfileSyncDto>(new HttpRequestException("offline"));
        var offline = await harness.Service.SyncNowAsync();
        harness.Api.OnSync = (_, _) => Task.FromException<StoreReceiptProfileSyncDto>(
            ProfileTestData.Http(HttpStatusCode.InternalServerError));
        var serverError = await harness.Service.SyncNowAsync();
        harness.Api.OnSync = (_, _) => Task.FromException<StoreReceiptProfileSyncDto>(
            ProfileTestData.Http(HttpStatusCode.BadRequest, "STORE_PROFILE_INVALID_CHARACTERS"));
        var invalidSnapshot = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Failed, offline.Outcome);
        Assert.Equal(ReceiptProfileSyncOutcome.Failed, serverError.Outcome);
        Assert.Equal(ReceiptProfileSyncOutcome.Failed, invalidSnapshot.Outcome);
        // 离线继续用本机最后一次应用的资料。
        Assert.Equal(before, harness.Repository.Snapshot());
    }

    [Fact]
    public async Task Http_client_timeout_is_a_failure_not_a_cancellation()
    {
        using var harness = new Harness();
        // HttpClient 超时抛 TaskCanceledException，但调用方并没有取消。
        harness.Api.OnSync = (_, _) => Task.FromException<StoreReceiptProfileSyncDto>(
            new TaskCanceledException("timeout"));

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task Local_write_failure_is_reported_as_failed_and_does_not_ack()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(3)));
        harness.Repository.FailNextBatch = true;

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Api.AckVersions);
        Assert.Empty(harness.Repository.Snapshot());

        // 下一轮写入恢复后正常应用。
        var retry = await harness.Service.SyncNowAsync();
        Assert.Equal(ReceiptProfileSyncOutcome.Applied, retry.Outcome);
    }

    [Fact]
    public async Task Concurrent_syncs_share_a_single_in_flight_request()
    {
        using var harness = new Harness();
        var releaseSync = new TaskCompletionSource<StoreReceiptProfileSyncDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Api.OnSync = (_, _) => releaseSync.Task;

        var manual = harness.Service.SyncNowAsync();
        await WaitUntilAsync(() => harness.Api.SyncKnownVersions.Count == 1);
        var secondManual = harness.Service.SyncNowAsync();
        var tick = harness.Service.RunScheduledTickAsync();

        releaseSync.SetResult(ProfileTestData.Changed(ProfileTestData.Profile(3)));
        var results = await Task.WhenAll(manual, secondManual, tick!);

        // 三个调用方只发了一次请求，拿到同一个结果；之后可以正常发起下一轮。
        Assert.Single(harness.Api.SyncKnownVersions);
        Assert.All(results, result => Assert.Equal(ReceiptProfileSyncOutcome.Applied, result!.Outcome));
        Assert.Equal([3], harness.Api.AckVersions);

        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(3));
        Assert.Equal(ReceiptProfileSyncOutcome.UpToDate, (await harness.Service.SyncNowAsync()).Outcome);
        Assert.Equal([0, 3], harness.Api.SyncKnownVersions);
    }

    [Fact]
    public async Task Caller_cancellation_throws_and_does_not_leave_a_stuck_in_flight_round()
    {
        using var harness = new Harness();
        harness.Api.OnSync = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ProfileTestData.Unchanged(0);
        };
        using var cancellation = new CancellationTokenSource();

        var pending = harness.Service.SyncNowAsync(cancellation.Token);
        await WaitUntilAsync(() => harness.Api.SyncKnownVersions.Count == 1);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(0));
        Assert.Equal(ReceiptProfileSyncOutcome.NeverReleased, (await harness.Service.SyncNowAsync()).Outcome);
    }

    [Fact]
    public async Task Scheduled_round_that_exceeds_its_budget_fails_and_the_next_round_runs()
    {
        using var harness = new Harness();
        harness.Api.OnSync = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ProfileTestData.Unchanged(0);
        };

        var pending = harness.Service.RunScheduledTickAsync();
        await WaitUntilAsync(() => harness.Api.SyncKnownVersions.Count == 1);
        harness.Time.Advance(ReceiptProfileSyncService.ScheduledRoundBudget + TimeSpan.FromSeconds(1));

        var result = await pending.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.Equal(ReceiptProfileSyncOutcome.Failed, result!.Outcome);
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Unchanged(0));
        Assert.Equal(ReceiptProfileSyncOutcome.NeverReleased, (await harness.Service.SyncNowAsync()).Outcome);
    }

    [Fact]
    public async Task Store_change_clears_the_old_version_and_pulls_the_new_store_on_the_next_tick()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (known, _) => Task.FromResult(
            known == 0
                ? ProfileTestData.Changed(ProfileTestData.Profile(3))
                : ProfileTestData.Unchanged(3));
        await harness.Service.RunScheduledTickAsync();
        Assert.Equal(3, (await harness.Store.LoadAsync()).ProfileVersion);

        harness.Auth.Set(new DeviceAuthorizationContext("DEV1", "S002", "HW", "AUTH"));
        harness.Api.OnSync = (known, _) => Task.FromResult(
            ProfileTestData.Changed(ProfileTestData.Profile(1, storeCode: "S002", storeName: "Moorooka", brandName: "S2 Brand")));

        // 换店后的第一拍不必等满 4 拍；旧店版本不属于新店，所以带 knownVersion=0。
        var result = await harness.Service.RunScheduledTickAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Applied, result!.Outcome);
        Assert.Equal([0, 0], harness.Api.SyncKnownVersions);
        var loaded = await harness.Store.LoadAsync();
        Assert.Equal(1, loaded.ProfileVersion);
        Assert.Equal("S2 Brand", loaded.BrandName);
        Assert.Equal("Moorooka", loaded.StoreName);
        Assert.Equal(new ReceiptProfileLocalState(1, 1), await harness.Store.LoadProfileStateAsync("S002"));
    }

    [Fact]
    public async Task Store_change_during_the_request_discards_the_old_stores_profile()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) =>
        {
            // 请求在飞时设备被重新注册到另一家店。
            harness.Auth.Set(new DeviceAuthorizationContext("DEV1", "S002", "HW", "AUTH"));
            return Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(3)));
        };

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Discarded, result.Outcome);
        Assert.Equal(0, harness.Repository.BatchWriteCount);
        Assert.Empty(harness.Api.AckVersions);
    }

    [Fact]
    public async Task Sync_without_device_authorization_reports_not_ready()
    {
        using var harness = new Harness();
        harness.Auth.Clear();

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.NotReady, result.Outcome);
        Assert.Empty(harness.Api.SyncKnownVersions);
    }

    [Fact]
    public async Task Throwing_applied_subscriber_does_not_break_the_sync()
    {
        using var harness = new Harness();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(3)));
        harness.Service.ProfileApplied += (_, _) => throw new InvalidOperationException("subscriber bug");

        var result = await harness.Service.SyncNowAsync();

        Assert.Equal(ReceiptProfileSyncOutcome.Applied, result.Outcome);
        Assert.Equal([3], harness.Api.AckVersions);
    }

    [Fact]
    public async Task Logs_record_versions_and_results_but_never_profile_content()
    {
        using var harness = new Harness();
        using var capture = new ConsoleLogCapture();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(ProfileTestData.Profile(3)));
        await harness.Service.SyncNowAsync();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(
            ProfileTestData.Profile(4, address: "Bad\u0001Secret Street 7", phone: "0400 000 000")));
        await harness.Service.SyncNowAsync();
        harness.Api.OnSync = (_, _) => Task.FromResult(ProfileTestData.Changed(
            ProfileTestData.Profile(5, storeCode: "S999")));
        await harness.Service.SyncNowAsync();

        var text = string.Join('\n', capture.Lines);
        Assert.Contains("applied version=3", text, StringComparison.Ordinal);
        Assert.Contains("Discarded", text, StringComparison.Ordinal);
        foreach (var secret in new[]
                 {
                     "Secret Street", "07 3000 9999", "12 345 678 901", "HB Brand", "Return within 7 days",
                     "0400 000 000", "Sunnybank"
                 })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Repeated_identical_failures_are_logged_once_per_interval()
    {
        using var harness = new Harness();
        using var capture = new ConsoleLogCapture();
        harness.Api.OnSync = (_, _) => Task.FromException<StoreReceiptProfileSyncDto>(new HttpRequestException("offline"));

        for (var i = 0; i < 5; i++)
        {
            await harness.Service.SyncNowAsync();
        }

        Assert.Single(capture.Lines, line => line.Contains("sync request failed", StringComparison.Ordinal));

        harness.Time.Advance(TimeSpan.FromMinutes(11));
        await harness.Service.SyncNowAsync();

        Assert.Equal(2, capture.Lines.Count(line => line.Contains("sync request failed", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(ReceiptProfileSyncOutcome.Applied, "settings.status.receiptProfileSyncApplied")]
    [InlineData(ReceiptProfileSyncOutcome.UpToDate, "settings.status.receiptProfileSyncUpToDate")]
    [InlineData(ReceiptProfileSyncOutcome.NeverReleased, "settings.status.receiptProfileSyncNeverReleased")]
    [InlineData(ReceiptProfileSyncOutcome.Discarded, "settings.status.receiptProfileSyncDiscarded")]
    [InlineData(ReceiptProfileSyncOutcome.Failed, "settings.status.receiptProfileSyncFailed")]
    [InlineData(ReceiptProfileSyncOutcome.ServerUnsupported, "settings.status.receiptProfileSyncUnsupported")]
    [InlineData(ReceiptProfileSyncOutcome.NotReady, "settings.status.receiptProfileSyncNotReady")]
    public void Status_text_maps_every_outcome_to_a_localized_key_in_both_languages(
        ReceiptProfileSyncOutcome outcome,
        string expectedKey)
    {
        var (key, args) = ReceiptProfileSyncStatusText.Describe(
            new ReceiptProfileSyncResult(outcome, 7, "boom"));
        Assert.Equal(expectedKey, key);

        var localization = new LocalizationService();
        try
        {
            foreach (var culture in new[] { "en-US", "zh-CN" })
            {
                localization.SetCulture(culture);
                var template = localization.T(key);
                Assert.DoesNotContain("[[", template, StringComparison.Ordinal);
                var text = string.Format(localization.CurrentCulture, template, args);
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public void Status_text_formats_the_version_into_the_chinese_and_english_messages()
    {
        var localization = new LocalizationService();
        try
        {
            var (appliedKey, appliedArgs) = ReceiptProfileSyncStatusText.Describe(
                new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.Applied, 7));
            var (upToDateKey, upToDateArgs) = ReceiptProfileSyncStatusText.Describe(
                new ReceiptProfileSyncResult(ReceiptProfileSyncOutcome.UpToDate, 7));

            localization.SetCulture("zh-CN");
            Assert.Equal("已更新到版本 7", string.Format(localization.CurrentCulture, localization.T(appliedKey), appliedArgs));
            Assert.Equal("已是最新（版本 7）", string.Format(localization.CurrentCulture, localization.T(upToDateKey), upToDateArgs));
            localization.SetCulture("en-US");
            Assert.Equal("Updated to version 7", string.Format(localization.CurrentCulture, localization.T(appliedKey), appliedArgs));
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    [Fact]
    public void Managed_hint_and_sync_button_copy_exists_in_both_languages()
    {
        var localization = new LocalizationService();
        try
        {
            localization.SetCulture("zh-CN");
            Assert.Equal("由总部下发，请在 Web 分店管理修改", localization.T("settings.receiptPrinter.profileManaged"));
            Assert.Equal("立即同步", localization.T("settings.receiptPrinter.syncNow"));
            localization.SetCulture("en-US");
            Assert.DoesNotContain("[[", localization.T("settings.receiptPrinter.profileManaged"), StringComparison.Ordinal);
            Assert.Equal("Sync now", localization.T("settings.receiptPrinter.syncNow"));
        }
        finally
        {
            localization.SetCulture("en-US");
        }
    }

    private static ReceiptProfileFields Fields() => new(
        "HB Brand", "Sunnybank", ProfileTestData.Address, ProfileTestData.Phone, ProfileTestData.Abn, "Return within 7 days");

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Repository = new ProfileTestSettingsRepository();
            Auth = ProfileTestData.Auth("S001");
            Store = new ReceiptPrinterSettingsStore(Repository, Auth);
            Api = new ScriptedProfileApiClient();
            Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
            Service = CreateService();
        }

        public ProfileTestSettingsRepository Repository { get; }

        public DeviceAuthorizationState Auth { get; }

        public ReceiptPrinterSettingsStore Store { get; }

        public ScriptedProfileApiClient Api { get; }

        public FakeTimeProvider Time { get; }

        public ReceiptProfileSyncService Service { get; }

        public ReceiptProfileSyncService CreateService() => new(Store, Api, Auth, Time);

        public void Dispose()
        {
        }
    }

    private sealed class ConsoleLogCapture : IDisposable
    {
        private readonly List<string> _lines = [];

        public ConsoleLogCapture()
        {
            ConsoleLog.LineWritten += OnLineWritten;
        }

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToArray();
                }
            }
        }

        public void Dispose()
        {
            ConsoleLog.LineWritten -= OnLineWritten;
        }

        private void OnLineWritten(string line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
