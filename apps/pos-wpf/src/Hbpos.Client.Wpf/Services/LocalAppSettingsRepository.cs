namespace Hbpos.Client.Wpf.Services;

public interface ILocalAppSettingsRepository
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// 一次读取多个键。结果只含存在的键（缺失键不出现在字典里，相当于 <see cref="GetValueAsync"/> 返回 null），
    /// 值与逐键 <see cref="GetValueAsync"/> 读到的完全一致（含显式空串）；重复键只读一次。
    /// 默认实现逐键调用 <see cref="GetValueAsync"/>，只实现单键读取的实现（含各测试替身）无需改动；
    /// <see cref="LocalAppSettingsRepository"/> 覆盖为单连接、单条 IN 查询。
    /// </summary>
    async Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(key, nameof(keys));
            var value = await GetValueAsync(key, cancellationToken);
            if (value is not null)
            {
                values[key] = value;
            }
        }

        return values;
    }

    async Task<LocalAppSettingEntry?> GetEntryAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var value = await GetValueAsync(key, cancellationToken);
        return value is null ? null : new LocalAppSettingEntry(value, null);
    }

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    Task SetValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default);

    Task DeleteValueAsync(string key, CancellationToken cancellationToken = default);
}

public sealed record LocalAppSettingEntry(string Value, DateTimeOffset? UpdatedAt);

public sealed class LocalAppSettingsRepository(LocalSqliteStore store) : ILocalAppSettingsRepository
{
    // 批量读取单条语句最多带的键数（每键一个参数），留足余量低于 SQLite 旧版本 999 的参数上限。
    private const int MaxKeysPerBatchQuery = 500;

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var keySnapshot = key;
        return Task.Run(async () =>
        {
            await using var connection = await store.OpenConnectionAsync(cancellationToken);
            await EnsureAppSettingsTableAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Value
                FROM AppSettings
                WHERE Key = $Key;
                """;
            command.Parameters.AddWithValue("$Key", keySnapshot);

            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }, cancellationToken);
    }

    public Task<IReadOnlyDictionary<string, string>> GetValuesAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        // 在调用线程先拍快照并去重：调用方之后改动集合不影响本次查询。
        var keySnapshot = keys.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var key in keySnapshot)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(keys));
        }

        if (keySnapshot.Length == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        // 与 GetValueAsync 一样放到线程池：Microsoft.Data.Sqlite 的 *Async 实际同步执行，不能占用调用方（UI）线程。
        return Task.Run<IReadOnlyDictionary<string, string>>(async () =>
        {
            // 中文注释：一个连接、一次建表检查、按参数化 IN 查询读完全部键；逐键读取时每个键都要新开连接
            // （Pooling=false）并执行两条 PRAGMA 与建表检查。
            await using var connection = await store.OpenConnectionAsync(cancellationToken);
            await EnsureAppSettingsTableAsync(connection, cancellationToken);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            // 分批是为了不触到 SQLite 单条语句的参数个数上限（旧版本为 999）；小票设置约 20 个键只会走一批。
            foreach (var chunk in keySnapshot.Chunk(MaxKeysPerBatchQuery))
            {
                await using var command = connection.CreateCommand();
                var parameterNames = new string[chunk.Length];
                for (var index = 0; index < chunk.Length; index++)
                {
                    parameterNames[index] = $"$Key{index}";
                    command.Parameters.AddWithValue(parameterNames[index], chunk[index]);
                }

                command.CommandText = $"""
                    SELECT Key, Value
                    FROM AppSettings
                    WHERE Key IN ({string.Join(", ", parameterNames)});
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    // 与 GetValueAsync 的 `ExecuteScalarAsync(...) as string` 保持一致：非文本值视同缺失。
                    if (reader.GetValue(0) is string key && reader.GetValue(1) is string value)
                    {
                        values[key] = value;
                    }
                }
            }

            return values;
        }, cancellationToken);
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        return SetValuesAsync(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [key] = value,
            },
            cancellationToken);
    }

    public Task<LocalAppSettingEntry?> GetEntryAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var keySnapshot = key;
        return Task.Run(async () =>
        {
            await using var connection = await store.OpenConnectionAsync(cancellationToken);
            await EnsureAppSettingsTableAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Value, UpdatedAt
                FROM AppSettings
                WHERE Key = $Key;
                """;
            command.Parameters.AddWithValue("$Key", keySnapshot);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var value = reader.GetString(0);
            var updatedAtText = reader.GetString(1);
            var updatedAt = DateTimeOffset.TryParse(
                updatedAtText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsedUpdatedAt)
                ? parsedUpdatedAt
                : (DateTimeOffset?)null;
            return new LocalAppSettingEntry(value, updatedAt);
        }, cancellationToken);
    }

    public Task SetValuesAsync(
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var valuesSnapshot = values
            .Select(pair =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(pair.Key);
                return new KeyValuePair<string, string>(pair.Key, pair.Value);
            })
            .ToArray();
        if (valuesSnapshot.Length == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            await using var connection = await store.OpenConnectionAsync(cancellationToken);
            await EnsureAppSettingsTableAsync(connection, cancellationToken);
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO AppSettings (Key, Value, UpdatedAt)
                VALUES ($Key, $Value, $UpdatedAt)
                ON CONFLICT(Key) DO UPDATE SET
                    Value = excluded.Value,
                    UpdatedAt = excluded.UpdatedAt;
                """;
            var keyParameter = command.Parameters.Add("$Key", Microsoft.Data.Sqlite.SqliteType.Text);
            var valueParameter = command.Parameters.Add("$Value", Microsoft.Data.Sqlite.SqliteType.Text);
            var updatedAtParameter = command.Parameters.Add("$UpdatedAt", Microsoft.Data.Sqlite.SqliteType.Text);
            var updatedAt = DateTimeOffset.UtcNow.ToString("O");

            foreach (var (key, value) in valuesSnapshot)
            {
                keyParameter.Value = key;
                valueParameter.Value = value;
                updatedAtParameter.Value = updatedAt;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var keySnapshot = key;
        return Task.Run(async () =>
        {
            await using var connection = await store.OpenConnectionAsync(cancellationToken);
            await EnsureAppSettingsTableAsync(connection, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM AppSettings
                WHERE Key = $Key;
                """;
            command.Parameters.AddWithValue("$Key", keySnapshot);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    private static async Task EnsureAppSettingsTableAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;

        // 设置读取可能早于完整本地库初始化，先保证自己的轻量配置表存在。
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
