using System.Reflection;
using Hbpos.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Hbpos.Api.Tests;

/// <summary>
/// VoucherTerms / InstallmentTerms 新列的降级读取：先部署 Hbpos.Api、后跑 HBweb 迁移时，
/// 读新列的 SQL 会抛 SqlException 207，必须回退到不含新列的旧 SQL，且回退日志要限频。
/// 真实 SQL Server 上的行为见 StoreReceiptProfileReleaseSqlServerIntegrationTests。
/// </summary>
public sealed class StoreReceiptProfileColumnFallbackTests
{
    // ---------------------------------------------------------------- 207 识别

    [Fact]
    public void IsInvalidColumnName_recognizes_sql_exception_207_directly_and_when_wrapped()
    {
        var sqlException = CreateSqlException(207);

        Assert.True(StoreReceiptProfileColumnFallback.IsInvalidColumnName(sqlException));
        // SqlSugar 或上层可能再包一层，必须沿内部异常链找。
        Assert.True(StoreReceiptProfileColumnFallback.IsInvalidColumnName(
            new InvalidOperationException("wrapped", new InvalidOperationException("twice", sqlException))));
    }

    [Theory]
    [InlineData(208)]
    [InlineData(2627)]
    [InlineData(-2)]
    public void IsInvalidColumnName_ignores_other_sql_errors(int number)
    {
        Assert.False(StoreReceiptProfileColumnFallback.IsInvalidColumnName(CreateSqlException(number)));
    }

    [Fact]
    public void IsInvalidColumnName_ignores_non_sql_exceptions_even_with_similar_text()
    {
        // 只认错误号，不靠报错文本猜测，避免把无关异常误当成「列缺失」。
        Assert.False(StoreReceiptProfileColumnFallback.IsInvalidColumnName(
            new InvalidOperationException("Invalid column name 'VoucherTerms'.")));
    }

    // ---------------------------------------------------------------- 回退流程

    [Fact]
    public async Task QueryAsync_returns_new_column_result_without_touching_the_legacy_query()
    {
        var legacyCalls = 0;

        var result = await StoreReceiptProfileColumnFallback.QueryAsync(
            () => Task.FromResult("new"),
            () =>
            {
                legacyCalls++;
                return Task.FromResult("legacy");
            },
            "Release");

        Assert.Equal("new", result);
        Assert.Equal(0, legacyCalls);
    }

    [Fact]
    public async Task QueryAsync_falls_back_to_legacy_query_when_new_columns_are_missing()
    {
        var logger = new CapturingLogger();

        var result = await StoreReceiptProfileColumnFallback.QueryAsync<string>(
            () => throw CreateSqlException(207),
            () => Task.FromResult("legacy"),
            "Release",
            logger,
            NewThrottle(out _));

        Assert.Equal("legacy", result);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("Release", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_fallback_works_when_the_missing_column_error_is_async_and_wrapped()
    {
        var result = await StoreReceiptProfileColumnFallback.QueryAsync<string>(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("wrapped", CreateSqlException(207));
            },
            () => Task.FromResult("legacy"),
            "Store");

        Assert.Equal("legacy", result);
    }

    [Fact]
    public async Task QueryAsync_does_not_swallow_other_errors_or_run_the_legacy_query()
    {
        var legacyCalls = 0;

        await Assert.ThrowsAsync<TimeoutException>(() => StoreReceiptProfileColumnFallback.QueryAsync<string>(
            () => throw new TimeoutException("db timeout"),
            () =>
            {
                legacyCalls++;
                return Task.FromResult("legacy");
            },
            "Store"));
        await Assert.ThrowsAsync<SqlException>(() => StoreReceiptProfileColumnFallback.QueryAsync<string>(
            () => throw CreateSqlException(1205),
            () =>
            {
                legacyCalls++;
                return Task.FromResult("legacy");
            },
            "Store"));

        Assert.Equal(0, legacyCalls);
    }

    [Fact]
    public async Task QueryAsync_propagates_the_legacy_query_failure()
    {
        // 旧 SQL 也失败说明是别的库结构问题（例如 ReturnPolicy 列缺失），不能被降级吞掉。
        var exception = await Assert.ThrowsAsync<SqlException>(() => StoreReceiptProfileColumnFallback.QueryAsync<string>(
            () => throw CreateSqlException(207),
            () => throw CreateSqlException(207),
            "Store"));

        Assert.Equal(207, exception.Number);
    }

    [Fact]
    public async Task QueryAsync_propagates_cancellation_from_the_new_query()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StoreReceiptProfileColumnFallback.QueryAsync<string>(
            () => throw new OperationCanceledException(cancellation.Token),
            () => Task.FromResult("legacy"),
            "Release"));
    }

    // ---------------------------------------------------------------- 日志限频

    [Fact]
    public async Task QueryAsync_logs_the_fallback_once_per_scope_per_interval()
    {
        var logger = new CapturingLogger();
        var throttle = NewThrottle(out var clock);

        // 设备每 60 秒轮询一次：连续多轮回退只记第一条。
        for (var round = 0; round < 5; round++)
        {
            await FallbackOnceAsync("Release", logger, throttle);
            clock.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.Single(logger.Entries);

        // 另一个读取点（门店表）是独立的一条。
        await FallbackOnceAsync("Store", logger, throttle);
        Assert.Equal(2, logger.Entries.Count);

        // 超过限频间隔后再记一条。
        clock.Advance(StoreReceiptProfileColumnFallback.LogInterval);
        await FallbackOnceAsync("Release", logger, throttle);
        Assert.Equal(3, logger.Entries.Count);
    }

    [Fact]
    public void Throttle_allows_first_call_then_blocks_until_the_interval_elapses()
    {
        var throttle = NewThrottle(out var clock);

        Assert.True(throttle.ShouldLog("Release"));
        Assert.False(throttle.ShouldLog("Release"));
        clock.Advance(StoreReceiptProfileColumnFallback.LogInterval - TimeSpan.FromSeconds(1));
        Assert.False(throttle.ShouldLog("Release"));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.ShouldLog("Release"));
    }

    // ---------------------------------------------------------------- SQL 文本契约

    [Fact]
    public void New_sql_selects_the_new_columns_and_the_legacy_sql_does_not()
    {
        Assert.Contains("[VoucherTerms]", StoreReceiptProfileReleaseQueries.LatestReleaseSql, StringComparison.Ordinal);
        Assert.Contains("[InstallmentTerms]", StoreReceiptProfileReleaseQueries.LatestReleaseSql, StringComparison.Ordinal);
        Assert.DoesNotContain("VoucherTerms", StoreReceiptProfileReleaseQueries.LatestReleaseLegacySql, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallmentTerms", StoreReceiptProfileReleaseQueries.LatestReleaseLegacySql, StringComparison.Ordinal);

        Assert.Contains("VoucherTerms", StoreReceiptProfileService.SelectStoreSql, StringComparison.Ordinal);
        Assert.Contains("InstallmentTerms", StoreReceiptProfileService.SelectStoreSql, StringComparison.Ordinal);
        Assert.DoesNotContain("VoucherTerms", StoreReceiptProfileService.SelectStoreLegacySql, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallmentTerms", StoreReceiptProfileService.SelectStoreLegacySql, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_sql_keeps_the_same_filters_and_existing_columns_as_the_new_sql()
    {
        // 旧 SQL 只是少两列：其余列与过滤条件必须与新 SQL 逐字一致，回退时行为不能漂移。
        Assert.Equal(
            StripNewColumns(StoreReceiptProfileReleaseQueries.LatestReleaseSql),
            StripNewColumns(StoreReceiptProfileReleaseQueries.LatestReleaseLegacySql));
        Assert.Equal(
            StripNewColumns(StoreReceiptProfileService.SelectStoreSql),
            StripNewColumns(StoreReceiptProfileService.SelectStoreLegacySql));
    }

    // ---------------------------------------------------------------- 辅助

    private static string StripNewColumns(string sql)
    {
        var lines = sql.Split('\n')
            .Where(line => !line.Contains("VoucherTerms", StringComparison.Ordinal)
                && !line.Contains("InstallmentTerms", StringComparison.Ordinal))
            .Select(line => line.TrimEnd('\r', ' '));
        // ReturnPolicy 后面的逗号取决于其后是否还有列，统一去掉行尾逗号再比较。
        return string.Join('\n', lines.Select(line => line.TrimEnd(',')));
    }

    private static Task FallbackOnceAsync(string scope, ILogger logger, ColumnFallbackLogThrottle throttle) =>
        StoreReceiptProfileColumnFallback.QueryAsync<string>(
            () => throw CreateSqlException(207),
            () => Task.FromResult("legacy"),
            scope,
            logger,
            throttle);

    private static ColumnFallbackLogThrottle NewThrottle(out ManualTimeProvider clock)
    {
        clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero));
        return new ColumnFallbackLogThrottle(clock, StoreReceiptProfileColumnFallback.LogInterval);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan delta) => current += delta;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // 与 SquareWebhookSchemaInitializerTests 同一做法：SqlException 没有公开构造函数，只能反射创建。
    private static SqlException CreateSqlException(int number)
    {
        var errorCollection = (SqlErrorCollection)Activator.CreateInstance(
            typeof(SqlErrorCollection),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: null,
            culture: null)!;
        var error = CreateSqlError(number);
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(errorCollection, [error]);

        var createException = typeof(SqlException)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .First(method =>
            {
                var parameters = method.GetParameters();
                return method.Name == "CreateException" &&
                    parameters.Length >= 2 &&
                    parameters[0].ParameterType == typeof(SqlErrorCollection);
            });

        var args = createException.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(SqlErrorCollection)
                ? errorCollection
                : parameter.ParameterType == typeof(string)
                    ? "15.0.0"
                    : parameter.HasDefaultValue
                        ? parameter.DefaultValue
                        : parameter.ParameterType.IsValueType
                            ? Activator.CreateInstance(parameter.ParameterType)
                            : null)
            .ToArray();
        return (SqlException)createException.Invoke(null, args)!;
    }

    private static SqlError CreateSqlError(int number)
    {
        var constructor = typeof(SqlError)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .First();

        var args = constructor.GetParameters()
            .Select(parameter => CreateSqlErrorArgument(parameter, number))
            .ToArray();
        return (SqlError)constructor.Invoke(args);
    }

    private static object? CreateSqlErrorArgument(ParameterInfo parameter, int number)
    {
        if (parameter.ParameterType == typeof(int))
        {
            return string.Equals(parameter.Name, "infoNumber", StringComparison.OrdinalIgnoreCase)
                ? number
                : 0;
        }

        if (parameter.ParameterType == typeof(byte))
        {
            return (byte)0;
        }

        if (parameter.ParameterType == typeof(uint))
        {
            return 0u;
        }

        if (parameter.ParameterType == typeof(string))
        {
            return parameter.Name switch
            {
                "server" => "test-sql",
                "errorMessage" => $"SQL Server error {number}.",
                _ => string.Empty
            };
        }

        return parameter.HasDefaultValue
            ? parameter.DefaultValue
            : parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null;
    }
}
