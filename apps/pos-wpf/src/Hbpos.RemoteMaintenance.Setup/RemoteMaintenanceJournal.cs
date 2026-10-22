using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hbpos.RemoteMaintenance.Setup;

public enum RemoteMaintenanceOperationState
{
    Prepared,
    InstalledPendingCommit,
    Committed,
    Failed
}

public sealed record RemoteMaintenanceJournalState(
    Guid OperationId,
    Guid DeviceId,
    RemoteMaintenanceOperationState State,
    string RustdeskId,
    string ClientVersion,
    string ProtectedPassword,
    string? ProtectedMonitorToken,
    string? HeartbeatUrl,
    DateTimeOffset UpdatedAtUtc,
    RemoteMaintenanceConfig? Config = null,
    string? RustDeskArtifactPath = null,
    string? StatusAgentArtifactPath = null,
    string? DataDirectory = null,
    RemoteMaintenanceArtifactManifest? ArtifactManifest = null,
    // 仅记录本次事务实际触及的服务；预检查拒绝时两个标志都保持 false。
    bool RustDeskInstalled = false,
    bool StatusAgentInstalled = false,
    // 启动对账：用户态写入中心期望的新服务器配置，提权 helper 校验白名单后应用，
    // 成功时由 helper 把它并入 Config 并清空。旧版本 journal 没有该字段，读出为 null。
    RemoteMaintenanceConfig? PendingConfig = null,
    // 自动对账连续失败次数（UAC 被拒、服务异常等）；达到上限后不再自动弹 UAC，改由设置页手动触发。
    int ReconcileFailureCount = 0);

public interface IRemoteMaintenanceSecretProtector
{
    string Protect(string plaintext);

    string? Unprotect(string protectedValue);
}

public sealed class DpapiRemoteMaintenanceSecretProtector : IRemoteMaintenanceSecretProtector
{
    public string Protect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string? Unprotect(string protectedValue)
    {
        try
        {
            var protectedBytes = Convert.FromBase64String(protectedValue);
            try
            {
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.LocalMachine);
                try { return Encoding.UTF8.GetString(bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        catch (FormatException) { return null; }
        catch (CryptographicException) { return null; }
    }
}

public sealed class RemoteMaintenanceJournal(
    string filePath,
    IRemoteMaintenanceSecretProtector protector)
{
    public string FilePath { get; } = Path.GetFullPath(filePath);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<RemoteMaintenanceJournalState?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath)) return null;
            var state = JsonSerializer.Deserialize<RemoteMaintenanceJournalState>(
                await File.ReadAllTextAsync(FilePath, cancellationToken), JsonOptions);
            return state is null || protector.Unprotect(state.ProtectedPassword) is null ||
                (state.ProtectedMonitorToken is not null && protector.Unprotect(state.ProtectedMonitorToken) is null)
                ? null
                : state;
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(
        RemoteMaintenanceJournalState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state.ProtectedPassword);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
            var temporary = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var json = JsonSerializer.Serialize(state, JsonOptions);
                await File.WriteAllTextAsync(temporary, json, cancellationToken);
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
}

public static class RemoteMaintenancePasswordGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string Create(int length = 20)
    {
        if (length < 12) throw new ArgumentOutOfRangeException(nameof(length));
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[length];
        for (var index = 0; index < chars.Length; index++) chars[index] = Alphabet[bytes[index] % Alphabet.Length];
        CryptographicOperations.ZeroMemory(bytes);
        return new string(chars);
    }
}
