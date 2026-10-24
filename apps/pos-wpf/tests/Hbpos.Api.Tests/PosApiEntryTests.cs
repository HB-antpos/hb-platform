using Hbpos.Api.Services;
using Hbpos.Contracts.Stores;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Hbpos.Api.Tests;

/// <summary>
/// 收银端 POS API 入口（edge / aws / direct）：白名单与请求头解析、分店首选入口的下发（含缓存失效策略）、
/// 设备实际入口回显的节流与容错。真实 SQL（列缺失降级、UPDATE 定位设备行）见 PosApiEntrySqlServerIntegrationTests。
/// </summary>
public sealed class PosApiEntryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 1, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- 白名单与请求头

    [Theory]
    [InlineData("edge", "edge")]
    [InlineData("AWS", "aws")]
    [InlineData("  Direct ", "direct")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("tencent", null)]
    [InlineData("edge,aws", null)]
    [InlineData("aws;direct", null)]
    public void Normalize_accepts_only_the_three_known_entries(string? raw, string? expected)
    {
        Assert.Equal(expected, PosApiEntryModes.Normalize(raw));
    }

    [Fact]
    public void ReadFromRequest_accepts_one_whitelisted_header_value_and_ignores_everything_else()
    {
        Assert.Null(PosApiEntryModes.ReadFromRequest(null));
        Assert.Null(PosApiEntryModes.ReadFromRequest(new DefaultHttpContext().Request));

        Assert.Equal("aws", Read("aws"));
        Assert.Equal("direct", Read("DIRECT"));
        Assert.Null(Read("bogus"));
        Assert.Null(Read(""));

        // 多个同名头 / 逗号拼接：不可信，一律忽略（nginx 会覆盖成唯一值，出现多值说明不是经过它）。
        var multi = new DefaultHttpContext();
        multi.Request.Headers.Append(PosApiEntryModes.HeaderName, "edge");
        multi.Request.Headers.Append(PosApiEntryModes.HeaderName, "aws");
        Assert.Null(PosApiEntryModes.ReadFromRequest(multi.Request));
        Assert.Null(Read("edge, aws"));

        static string? Read(string value)
        {
            var context = new DefaultHttpContext();
            context.Request.Headers[PosApiEntryModes.HeaderName] = value;
            return PosApiEntryModes.ReadFromRequest(context.Request);
        }
    }

    // ---------------------------------------------------------------- 下发：缓存失效策略

    [Fact]
    public void ModeCache_expires_within_30_seconds_and_caches_unset_values_too()
    {
        // 失效策略：纯 TTL，必须 ≤ 30 秒（改设置后「客户端下次轮询 ≤60 秒 + 缓存」内生效）。
        Assert.True(StoreApiEntryModeCache.Ttl <= TimeSpan.FromSeconds(30));

        var clock = new MutableTimeProvider(T0);
        var cache = new StoreApiEntryModeCache(clock);
        Assert.False(cache.TryGet("S001", out _));

        cache.Set("S001", "aws");
        cache.Set("S002", null); // 未设置也缓存，避免未设置的店每分钟打库
        Assert.True(cache.TryGet("s001", out var aws)); // 门店代码大小写不敏感
        Assert.Equal("aws", aws);
        Assert.True(cache.TryGet("S002", out var unset));
        Assert.Null(unset);

        clock.Advance(StoreApiEntryModeCache.Ttl - TimeSpan.FromSeconds(1));
        Assert.True(cache.TryGet("S001", out _));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(cache.TryGet("S001", out _));
        Assert.False(cache.TryGet("S002", out _));
    }

    // ---------------------------------------------------------------- 下发：服务层

    [Theory]
    [InlineData("edge", "edge")]
    [InlineData("aws", "aws")]
    [InlineData("direct", "direct")]
    [InlineData(null, null)]
    public async Task GetSync_always_carries_the_current_api_entry_mode_whether_or_not_changed(
        string? stored,
        string? expected)
    {
        var reader = new FakeModeReader { Mode = stored };

        // 从未下发小票：Changed=false、Version=0，也要带。
        var never = await new StoreReceiptProfileReleaseService(new FakeReleaseRepository { LatestVersion = 0 }, null, reader)
            .GetSyncAsync("S001", 0, CancellationToken.None);
        // 版本没变：Changed=false。
        var same = await new StoreReceiptProfileReleaseService(
                new FakeReleaseRepository { LatestVersion = 3, Latest = Snapshot(3) }, null, reader)
            .GetSyncAsync("S001", 3, CancellationToken.None);
        // 版本不同：Changed=true 带快照。
        var changed = await new StoreReceiptProfileReleaseService(
                new FakeReleaseRepository { LatestVersion = 3, Latest = Snapshot(3) }, null, reader)
            .GetSyncAsync("S001", 1, CancellationToken.None);

        Assert.False(never.Sync!.Changed);
        Assert.False(same.Sync!.Changed);
        Assert.True(changed.Sync!.Changed);
        Assert.Equal(3, changed.Sync.Version);
        Assert.NotNull(changed.Sync.Profile);
        Assert.All([never, same, changed], result => Assert.Equal(expected, result.Sync!.ApiEntryMode));
        Assert.Equal(3, reader.Calls);
    }

    [Fact]
    public async Task GetSync_does_not_read_the_mode_when_the_lookup_fails_and_works_without_a_reader()
    {
        var reader = new FakeModeReader { Mode = "aws" };
        var blank = await new StoreReceiptProfileReleaseService(new FakeReleaseRepository(), null, reader)
            .GetSyncAsync(" ", 0, CancellationToken.None);
        Assert.Null(blank.Sync);
        Assert.Equal(StoreReceiptProfileReleaseService.StoreCodeRequiredCode, blank.ErrorCode);
        Assert.Equal(0, reader.Calls);

        // 旧的两参构造（不带 reader）仍可用，ApiEntryMode 为 null。
        var legacy = await new StoreReceiptProfileReleaseService(new FakeReleaseRepository { LatestVersion = 2 })
            .GetSyncAsync("S001", 2, CancellationToken.None);
        Assert.Null(legacy.Sync!.ApiEntryMode);
    }

    [Fact]
    public void Sync_dto_serializes_api_entry_mode_in_camel_case()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var json = System.Text.Json.JsonSerializer.Serialize(
            new StoreReceiptProfileSyncDto(false, 0, null, "aws"), options);
        Assert.Contains("\"apiEntryMode\":\"aws\"", json, StringComparison.Ordinal);

        // 旧服务端 JSON 里没有该字段：读出来是 null，不报错。
        var old = System.Text.Json.JsonSerializer.Deserialize<StoreReceiptProfileSyncDto>(
            """{"changed":false,"version":0,"profile":null}""", options);
        Assert.Null(old!.ApiEntryMode);
    }

    // ---------------------------------------------------------------- 回显：节流与容错

    [Fact]
    public async Task Recorder_ignores_missing_or_invalid_header_and_missing_identity()
    {
        var (recorder, writer, _) = CreateRecorder();

        await recorder.RecordAsync("POS-1", "S001", "HW-1", null, CancellationToken.None);
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "bogus", CancellationToken.None);
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "edge,aws", CancellationToken.None);
        await recorder.RecordAsync(null, "S001", "HW-1", "edge", CancellationToken.None);
        await recorder.RecordAsync("POS-1", " ", "HW-1", "edge", CancellationToken.None);

        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task Recorder_writes_on_first_sight_then_only_on_change_or_after_10_minutes()
    {
        var (recorder, writer, clock) = CreateRecorder();

        await recorder.RecordAsync(" POS-1 ", "S001", "HW-1", "AWS", CancellationToken.None);
        var first = Assert.Single(writer.Writes);
        // 归一化后的小写入口、去空白的设备编号，写入时间用 UTC 现在。
        Assert.Equal(("POS-1", "S001", "HW-1", "aws", T0.UtcDateTime), first);

        // 同入口 9 分钟内（sync 每分钟一次）不再写库。
        for (var i = 0; i < 9; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await recorder.RecordAsync("POS-1", "S001", "HW-1", "aws", CancellationToken.None);
        }
        Assert.Single(writer.Writes);

        // 满 10 分钟再写一次，刷新 LastApiEntryAt。
        clock.Advance(TimeSpan.FromMinutes(1));
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "aws", CancellationToken.None);
        Assert.Equal(2, writer.Writes.Count);

        // 入口变化（EdgeOne 出问题回退直连）立刻写，不等 10 分钟。
        clock.Advance(TimeSpan.FromSeconds(30));
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "direct", CancellationToken.None);
        Assert.Equal(3, writer.Writes.Count);
        Assert.Equal("direct", writer.Writes[2].Entry);

        // 另一台设备互不影响。
        await recorder.RecordAsync("POS-2", "S001", "HW-2", "direct", CancellationToken.None);
        Assert.Equal(4, writer.Writes.Count);
    }

    [Fact]
    public async Task Recorder_swallows_write_failures_logs_them_and_backs_off()
    {
        var (recorder, writer, clock) = CreateRecorder(out var logger);
        writer.Failure = new InvalidOperationException("column LastApiEntry is missing");

        // 写库失败不抛出，不影响 sync / runtime-status 的响应。
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "edge", CancellationToken.None);
        Assert.Single(writer.Writes);
        Assert.Single(logger.Warnings);

        // 同入口 5 分钟内不重试（避免列还没补好时每分钟打一次失败的 UPDATE）。
        clock.Advance(TimeSpan.FromMinutes(4));
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "edge", CancellationToken.None);
        Assert.Single(writer.Writes);

        // 超过退避窗口后重试；这次成功则恢复 10 分钟节流。
        clock.Advance(TimeSpan.FromMinutes(2));
        writer.Failure = null;
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "edge", CancellationToken.None);
        Assert.Equal(2, writer.Writes.Count);
        clock.Advance(TimeSpan.FromMinutes(1));
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "edge", CancellationToken.None);
        Assert.Equal(2, writer.Writes.Count);

        // 失败退避期间入口变化仍会立刻尝试。
        writer.Failure = new InvalidOperationException("still broken");
        await recorder.RecordAsync("POS-1", "S001", "HW-1", "aws", CancellationToken.None);
        Assert.Equal(3, writer.Writes.Count);
    }

    [Fact]
    public async Task Recorder_rethrows_only_when_the_request_itself_was_cancelled()
    {
        var (recorder, writer, _) = CreateRecorder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        writer.Failure = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            recorder.RecordAsync("POS-1", "S001", "HW-1", "edge", cts.Token));

        // 非请求取消引起的 OperationCanceledException（如数据库命令超时）按普通失败吞掉。
        var (other, otherWriter, _) = CreateRecorder();
        otherWriter.Failure = new OperationCanceledException();
        await other.RecordAsync("POS-1", "S001", "HW-1", "edge", CancellationToken.None);
        Assert.Single(otherWriter.Writes);
    }

    // ---------------------------------------------------------------- helpers

    private static (DeviceApiEntryRecorder Recorder, FakeWriter Writer, MutableTimeProvider Clock) CreateRecorder() =>
        CreateRecorder(out _);

    private static (DeviceApiEntryRecorder Recorder, FakeWriter Writer, MutableTimeProvider Clock) CreateRecorder(
        out CapturingLogger<DeviceApiEntryRecorder> logger)
    {
        var clock = new MutableTimeProvider(T0);
        var writer = new FakeWriter();
        logger = new CapturingLogger<DeviceApiEntryRecorder>();
        return (new DeviceApiEntryRecorder(writer, new DeviceApiEntryThrottle(clock), clock, logger), writer, clock);
    }

    private static StoreReceiptProfileDto Snapshot(int version) => new(
        "S001", "旗舰店", "Hot Bargain", "1 Queen St", "07 3000 0000", "12 345 678 901", "30 天无理由退换",
        version, T0);

    internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    internal sealed class RecordingRecorder : IDeviceApiEntryRecorder
    {
        public List<(string? DeviceCode, string? StoreCode, string? HardwareId, string? Entry)> Calls { get; } = [];

        public Task RecordAsync(
            string? deviceCode, string? storeCode, string? hardwareId, string? headerValue,
            CancellationToken cancellationToken)
        {
            Calls.Add((deviceCode, storeCode, hardwareId, headerValue));
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeModeReader : IStoreApiEntryModeReader
    {
        public string? Mode { get; set; }
        public int Calls { get; private set; }

        public Task<string?> GetModeAsync(string storeCode, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Mode);
        }
    }

    internal sealed class FakeWriter : IDeviceApiEntryWriter
    {
        public List<(string DeviceCode, string StoreCode, string? HardwareId, string Entry, DateTime NowUtc)> Writes { get; } = [];
        public Exception? Failure { get; set; }

        public Task<int> WriteAsync(
            string deviceCode, string storeCode, string? hardwareId, string entry, DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            Writes.Add((deviceCode, storeCode, hardwareId, entry, nowUtc));
            return Failure is null ? Task.FromResult(1) : Task.FromException<int>(Failure);
        }
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class FakeReleaseRepository : IStoreReceiptProfileReleaseRepository
    {
        public int LatestVersion { get; set; }
        public StoreReceiptProfileDto? Latest { get; set; }

        public Task<int> GetLatestVersionAsync(string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult(LatestVersion);

        public Task<StoreReceiptProfileDto?> GetLatestAsync(string storeCode, CancellationToken cancellationToken) =>
            Task.FromResult(Latest);

        public Task<int> UpsertAckAsync(
            string deviceCode, string storeCode, int version, string clientKind, DateTime nowUtc,
            CancellationToken cancellationToken) => Task.FromResult(version);
    }
}
