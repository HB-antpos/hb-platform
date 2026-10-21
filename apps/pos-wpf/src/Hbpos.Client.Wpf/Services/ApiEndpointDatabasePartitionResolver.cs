using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hbpos.Client.Wpf.Services;

public sealed class ApiEndpointDatabasePartitionResolver
{
    private const string LegacyDatabaseFileName = "hbpos_client.db";
    private const string MappingFileName = "server-data-map.json";
    private readonly string _rootDirectory;
    private readonly string _mappingPath;
    private readonly string _legacyEndpointKey;

    public ApiEndpointDatabasePartitionResolver(string rootDirectory, string initialAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(_rootDirectory);
        _mappingPath = Path.Combine(_rootDirectory, MappingFileName);

        var initialKey = CreateEndpointKey(initialAddress);
        _legacyEndpointKey = LoadOrCreateLegacyBinding(initialKey);
    }

    public string GetDatabasePath(string address)
    {
        var endpointKey = CreateEndpointKey(address);
        if (string.Equals(endpointKey, _legacyEndpointKey, StringComparison.Ordinal))
        {
            return Path.Combine(_rootDirectory, LegacyDatabaseFileName);
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpointKey))).ToLowerInvariant();
        return Path.Combine(_rootDirectory, $"hbpos_client-{hash}.db");
    }

    internal static string CreateEndpointKey(string address)
    {
        // 关键逻辑：先把别名入口折算成规范入口再算分区键，pos 入口与 hotbargain.vip 必须落在同一份本地库上，
        // 否则升级后门店会丢掉已激活状态并重新下载全部数据。
        var uri = ApiEndpointAliases.Canonicalize(
            new Uri(ApiServerSettingsService.NormalizeAddress(address), UriKind.Absolute));
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.IdnHost.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty
        };
        var normalized = builder.Uri.AbsoluteUri;
        return normalized.EndsWith('/') ? normalized : normalized + "/";
    }

    private string LoadOrCreateLegacyBinding(string initialKey)
    {
        if (File.Exists(_mappingPath))
        {
            return ReadLegacyBinding();
        }

        // 每个创建者使用独立临时文件，写完后以不覆盖 Move 原子竞争发布。
        var temporaryPath = $"{_mappingPath}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(new DatabaseMapping(initialKey));
            File.WriteAllText(temporaryPath, json);
            try
            {
                // 旧版数据库只绑定到第一个成功发布的初始端点，禁止后到进程覆盖赢家。
                File.Move(temporaryPath, _mappingPath);
                return initialKey;
            }
            catch (IOException) when (File.Exists(_mappingPath))
            {
                // 另一创建者已原子发布完整 sidecar；所有失败者统一采用赢家映射。
                return ReadLegacyBinding();
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private string ReadLegacyBinding()
    {
        // Move 发布者在 Windows 上可能仍短暂持有 DELETE/WRITE 访问；读取方允许共享这些访问即可直接读取已发布的完整文件。
        using var stream = new FileStream(
            _mappingPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var mapping = JsonSerializer.Deserialize<DatabaseMapping>(stream);
        if (mapping is null || string.IsNullOrWhiteSpace(mapping.LegacyEndpointKey))
        {
            throw new InvalidDataException("服务器数据库映射文件无效。");
        }

        return mapping.LegacyEndpointKey;
    }

    private sealed record DatabaseMapping(string LegacyEndpointKey);
}
