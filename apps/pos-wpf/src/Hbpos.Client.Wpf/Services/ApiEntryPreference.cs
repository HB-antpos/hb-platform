using System.IO;
using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 本机记住的「后台下发的首选入口」。放在独立的小文件里而不是小票资料表：
/// 它和小票版本状态完全解耦（Changed=false 时也要处理），也不能因为本地库繁忙而影响读写。
/// 读写失败一律不抛出——收银与小票同步比记住入口更重要。
/// </summary>
public interface IApiEntryPreferenceStore
{
    /// <summary>读取本机记住的首选入口代号；从未收到过、后台未设置或文件损坏都返回 null（＝按 edge）。</summary>
    string? Load();

    /// <summary>记住首选入口（null＝后台未设置）。失败时返回 false，不抛出。</summary>
    bool TrySave(string? mode);
}

/// <summary>JSON 文件实现：{"mode":"aws"}；写临时文件再替换，避免掉电后留下半截文件。</summary>
public sealed class FileApiEntryPreferenceStore(string path) : IApiEntryPreferenceStore
{
    private readonly object _gate = new();

    public string? Load()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("mode", out var element) &&
                    element.ValueKind == JsonValueKind.String &&
                    ApiEntryModes.TryParse(element.GetString(), out var mode))
                {
                    return mode;
                }

                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ConsoleLog.WriteWarning("ApiEntry", $"read persisted api entry mode failed error={ex.GetType().Name}");
            return null;
        }
    }

    public bool TrySave(string? mode)
    {
        var temporary = path + ".tmp";
        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(temporary, JsonSerializer.Serialize(new { mode }));
                File.Move(temporary, path, overwrite: true);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleLog.WriteWarning("ApiEntry", $"persist api entry mode failed error={ex.GetType().Name}");
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // 临时文件清理失败无所谓，下次写入会覆盖它。
            }

            return false;
        }
    }
}

/// <summary>接收 receipt-profile/sync 下发的 ApiEntryMode。</summary>
public interface IApiEntryPreferenceService
{
    /// <summary>
    /// 应用服务端下发的首选入口：合法值→写入选择器并记住；null＝后台未设置→按 edge（并记住「未设置」）；
    /// 不认识的字符串→保持当前设置不变。永不抛出。
    /// </summary>
    void Apply(string? serverMode);
}

public sealed class ApiEntryPreferenceService : IApiEntryPreferenceService
{
    private readonly ApiTransportSelector _selector;
    private readonly IApiEntryPreferenceStore _store;
    private readonly object _gate = new();
    private string? _lastSeenUnknown;

    // 本机已持久化的值（null＝未设置 / 从未收到），用来避免每 60 秒都写一次盘。
    private string? _persisted;

    public ApiEntryPreferenceService(ApiTransportSelector selector, IApiEntryPreferenceStore store)
    {
        _selector = selector;
        _store = store;
        _persisted = SafeLoad(store);
    }

    /// <summary>
    /// 创建选择器并套用本机记住的首选入口：冷启动在第一个请求发出之前就生效，之后再被服务端的值覆盖。
    /// 从未收到过＝edge，与改造前的行为一致。
    /// </summary>
    public static ApiTransportSelector CreateSelector(IApiEntryPreferenceStore store, TimeProvider? timeProvider = null)
    {
        var selector = new ApiTransportSelector(timeProvider);
        selector.SetPreferredMode(SafeLoad(store));
        return selector;
    }

    public void Apply(string? serverMode)
    {
        try
        {
            string? effective;
            if (serverMode is null)
            {
                effective = null;
            }
            else if (ApiEntryModes.TryParse(serverMode, out var parsed))
            {
                effective = parsed;
            }
            else
            {
                // 未知取值（例如将来新增了入口代号而本机版本较旧）：保持当前设置不变，只在值变化时记一条日志。
                lock (_gate)
                {
                    if (string.Equals(_lastSeenUnknown, serverMode, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _lastSeenUnknown = serverMode;
                }

                ConsoleLog.WriteWarning("ApiEntry", $"ignored unknown api entry mode from server value={Truncate(serverMode)}");
                return;
            }

            var before = _selector.PreferredMode;
            _selector.SetPreferredMode(effective);
            if (!string.Equals(before, _selector.PreferredMode, StringComparison.Ordinal))
            {
                ConsoleLog.Write("ApiEntry", $"preferred api entry changed from={before} to={_selector.PreferredMode}");
            }

            bool needPersist;
            lock (_gate)
            {
                _lastSeenUnknown = null;
                needPersist = !string.Equals(_persisted, effective, StringComparison.Ordinal);
            }

            if (needPersist && _store.TrySave(effective))
            {
                lock (_gate)
                {
                    _persisted = effective;
                }
            }
        }
        catch (Exception ex)
        {
            // 记住入口只是锦上添花，任何异常都不能影响收银与小票同步。
            ConsoleLog.WriteWarning("ApiEntry", $"apply api entry mode failed error={ex.GetType().Name}");
        }
    }

    private static string? SafeLoad(IApiEntryPreferenceStore store)
    {
        try
        {
            return store.Load();
        }
        catch (Exception ex)
        {
            ConsoleLog.WriteWarning("ApiEntry", $"load persisted api entry mode failed error={ex.GetType().Name}");
            return null;
        }
    }

    private static string Truncate(string value) => value.Length <= 32 ? value : value[..32];
}
