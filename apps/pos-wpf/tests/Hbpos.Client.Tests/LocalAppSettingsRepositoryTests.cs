using Hbpos.Client.Wpf.Services;
using Microsoft.Data.Sqlite;

namespace Hbpos.Client.Tests;

public sealed class LocalAppSettingsRepositoryTests
{
    [Fact]
    public async Task GetValuesAsync_returns_exactly_what_single_key_reads_return()
    {
        var databasePath = NewDatabasePath();
        try
        {
            var store = new LocalSqliteStore(databasePath);
            var repository = new LocalAppSettingsRepository(store);
            await repository.SetValuesAsync(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ReceiptPrinter:Port"] = " COM3 ",
                ["ReceiptPrinter:BrandName"] = string.Empty,
                ["ReceiptPrinter:S001:StoreName"] = "Sunnybank",
                ["lower-key"] = "lower",
            });
            await using (var connection = await store.OpenConnectionAsync())
            {
                // 非文本值：单键读取用 `as string` 视同缺失，批量读取必须一致。
                await using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO AppSettings (Key, Value, UpdatedAt) VALUES ('Blob', X'0102', '2026-10-12T00:00:00Z');";
                await command.ExecuteNonQueryAsync();
            }

            string[] keys =
            [
                "ReceiptPrinter:Port",
                "ReceiptPrinter:BrandName",
                "ReceiptPrinter:S001:StoreName",
                "ReceiptPrinter:Missing",
                "LOWER-KEY",
                "lower-key",
                "Blob",
                "ReceiptPrinter:Port",
            ];

            var values = await repository.GetValuesAsync(keys);

            foreach (var key in keys)
            {
                var single = await repository.GetValueAsync(key);
                Assert.Equal(single, values.TryGetValue(key, out var batched) ? batched : null);
            }

            // 显式空串与首尾空白原样保留；键区分大小写；缺失与非文本值不出现在结果里。
            Assert.Equal(
                new[] { "ReceiptPrinter:BrandName=", "ReceiptPrinter:Port= COM3 ", "ReceiptPrinter:S001:StoreName=Sunnybank", "lower-key=lower" },
                Flatten(values));
            // 与单键读取一样按原样比对键：只请求大写键时，不能把库里的小写键带回来。
            Assert.Empty(await repository.GetValuesAsync(["LOWER-KEY"]));
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task GetValuesAsync_reads_key_sets_larger_than_one_query_batch()
    {
        var databasePath = NewDatabasePath();
        try
        {
            var repository = new LocalAppSettingsRepository(new LocalSqliteStore(databasePath));
            var stored = Enumerable.Range(0, 1201)
                .ToDictionary(index => $"Key:{index:D4}", index => $"Value {index}", StringComparer.Ordinal);
            await repository.SetValuesAsync(stored);

            // 超过单条语句的键数上限时分批查询，每一批的结果都不能丢。
            var keys = stored.Keys.Concat(["Key:missing-1", "Key:missing-2"]).ToArray();
            var values = await repository.GetValuesAsync(keys);

            Assert.Equal(stored.Count, values.Count);
            foreach (var (key, value) in stored)
            {
                Assert.Equal(value, values[key]);
            }
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task GetValuesAsync_on_new_database_creates_settings_table_and_returns_empty()
    {
        var databasePath = NewDatabasePath();
        try
        {
            var repository = new LocalAppSettingsRepository(new LocalSqliteStore(databasePath));

            // 设置读取可能早于完整本地库初始化：与单键读取一样先保证设置表存在，而不是报表不存在。
            Assert.Empty(await repository.GetValuesAsync(["ReceiptPrinter:Port"]));
            Assert.Empty(await repository.GetValuesAsync(Array.Empty<string>()));
            Assert.Null(await repository.GetValueAsync("ReceiptPrinter:Port"));
        }
        finally
        {
            await SqliteTestDatabaseCleanup.DeleteDatabaseFilesAsync(databasePath);
        }
    }

    [Fact]
    public async Task Default_GetValuesAsync_reads_each_distinct_key_once_through_single_key_reads()
    {
        ILocalAppSettingsRepository repository = new SingleKeyOnlySettingsRepository(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["A"] = "1",
                ["B"] = string.Empty,
            });

        var values = await repository.GetValuesAsync(["A", "B", "Missing", "A"]);

        Assert.Equal(new[] { "A=1", "B=" }, Flatten(values));
        Assert.Equal(["A", "B", "Missing"], ((SingleKeyOnlySettingsRepository)repository).ReadKeys);
    }

    private static string[] Flatten(IReadOnlyDictionary<string, string> values) =>
        values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}")
            .ToArray();

    private static string NewDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"hbpos-app-settings-{Guid.NewGuid():N}.db");

    /// <summary>只实现单键读取的设置库（与各测试替身一样），用来验证接口默认的批量读取实现。</summary>
    private sealed class SingleKeyOnlySettingsRepository(IReadOnlyDictionary<string, string> values) : ILocalAppSettingsRepository
    {
        public List<string> ReadKeys { get; } = [];

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            ReadKeys.Add(key);
            return Task.FromResult(values.TryGetValue(key, out var value) ? value : null);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetValuesAsync(
            IReadOnlyDictionary<string, string> newValues,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
