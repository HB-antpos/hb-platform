using System.Text.Json.Serialization;

namespace Hbpos.RemoteMaintenance.Setup;

public sealed record RemoteMaintenancePrepareRequest(
    Guid OperationId,
    string ComputerName);

public sealed record RemoteMaintenanceConfig(
    string IdServer,
    string RelayServer,
    string PublicKey);

public sealed record RemoteMaintenanceArtifact(
    string Version,
    string FileName,
    string DownloadUrl,
    string Sha256,
    long SizeBytes);

public sealed record RemoteMaintenanceArtifactManifest(
    RemoteMaintenanceArtifact RustDesk,
    RemoteMaintenanceArtifact StatusAgent);

public sealed record RemoteMaintenancePrepareResponse(
    Guid OperationId,
    Guid DeviceId,
    RemoteMaintenanceConfig Config,
    RemoteMaintenanceArtifactManifest ArtifactManifest);

public sealed record RemoteMaintenanceCommitRequest(
    Guid OperationId,
    string RustdeskId,
    string ClientVersion,
    string Password);

public sealed record RemoteMaintenanceCommitResponse(
    Guid DeviceId,
    string MonitorToken,
    string HeartbeatUrl);

public sealed record RemoteMaintenanceStatus(
    bool IsConfigured,
    string RustdeskId,
    string ClientVersion,
    string ServiceStatus,
    string? Detail = null,
    // 状态上报服务的运行状态（Windows 服务状态字符串；不存在为 "notInstalled"）；旧调用方不关心时为 null。
    string? StatusAgentStatus = null);

public sealed class RemoteMaintenanceApiException(
    string message,
    int statusCode,
    string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string? Code { get; } = code;
}

public interface IRemoteMaintenanceApiClient
{
    Task<RemoteMaintenancePrepareResponse> PrepareAsync(
        RemoteMaintenancePrepareRequest request,
        CancellationToken cancellationToken = default);

    Task<Stream> DownloadArtifactAsync(
        string downloadUrl,
        CancellationToken cancellationToken = default);

    Task<RemoteMaintenanceCommitResponse> CommitAsync(
        RemoteMaintenanceCommitRequest request,
        CancellationToken cancellationToken = default);
}

public interface IRemoteMaintenanceArtifactDownloader
{
    Task<string> DownloadAndVerifyAsync(
        RemoteMaintenanceArtifact artifact,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}

public interface IRemoteMaintenanceInstaller
{
    Task<RemoteMaintenanceInstallationResult> InstallAsync(
        RemoteMaintenanceInstallationRequest request,
        CancellationToken cancellationToken = default);

    Task FailClosedAsync(
        RemoteMaintenanceInstallationResult installation,
        CancellationToken cancellationToken = default);

    Task ConfigureStatusAgentAsync(
        RemoteMaintenancePrepareResponse prepare,
        RemoteMaintenanceCommitResponse commit,
        string rustdeskId,
        string clientVersion,
        CancellationToken cancellationToken = default);

    Task<string?> GetRustdeskIdAsync(CancellationToken cancellationToken = default);

    Task<RemoteMaintenanceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed record RemoteMaintenanceInstallationRequest(
    RemoteMaintenancePrepareResponse Prepare,
    string RustDeskArtifactPath,
    string StatusAgentArtifactPath,
    string Password,
    string RustdeskId,
    string ClientVersion,
    string DataDirectory);

public sealed record RemoteMaintenanceInstallationResult(
    bool RustDeskInstalled,
    bool StatusAgentInstalled,
    string RustdeskId,
    string ClientVersion,
    string DataDirectory);
