using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Hbpos.Contracts.CashMovements;
using Hbpos.Contracts.DailyClose;

namespace Hbpos.Client.Wpf.Services;

public interface ICashMovementSyncApiClient
{
    Task<CashMovementSyncResponse> SyncAsync(CashMovementSyncRequest request, CancellationToken cancellationToken = default);
}

/// <summary>POST api/v1/cash-movements/sync；HttpClient 与日结上传同一套设备授权配置。</summary>
public sealed class CashMovementSyncApiClient(HttpClient httpClient) : ICashMovementSyncApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CashMovementSyncResponse> SyncAsync(
        CashMovementSyncRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("api/v1/cash-movements/sync", request, JsonOptions, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            string? message = null;
            try
            {
                using var document = JsonDocument.Parse(content);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    code = document.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    message = document.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                }
            }
            catch (JsonException)
            {
                // 错误体不是 JSON 时只保留状态码。
            }

            throw new DailyCloseUploadApiException(
                message ?? $"Cash movement sync failed with HTTP {(int)response.StatusCode}.",
                response.StatusCode,
                code);
        }

        return JsonSerializer.Deserialize<CashMovementSyncResponse>(content, JsonOptions)
            ?? throw new DailyCloseUploadApiException("Cash movement sync returned an empty response.", response.StatusCode, "EMPTY_SYNC_RESPONSE");
    }
}

/// <summary>
/// 把本机待上传的现金进出按发生时间批量上传。单条被服务端拒绝（Rejected）标为永久拒绝不再重试；
/// 网络 / 5xx 失败按次数退避；设备授权问题（401/403）立即停止本轮。
/// </summary>
public sealed class CashMovementUploadService(
    ILocalCashMovementRepository repository,
    ICashMovementSyncApiClient apiClient,
    DeviceAuthorizationState authorizationState,
    TimeProvider? timeProvider = null,
    string? appVersion = null)
{
    private const int BatchSize = 50;
    private const int MaximumRounds = 20;
    private const int MaximumAppVersionLength = 64;

    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>返回本轮成功上传（含服务端已有）的条数。</summary>
    public async Task<int> ExecutePendingAsync(CancellationToken cancellationToken = default)
    {
        var uploaded = 0;
        for (var round = 0; round < MaximumRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scope = authorizationState.Current;
            if (scope is null || string.IsNullOrWhiteSpace(scope.StoreCode) || string.IsNullOrWhiteSpace(scope.DeviceCode))
            {
                return uploaded;
            }

            var now = clock.GetUtcNow();
            var due = await repository.GetDueUploadsAsync(scope.StoreCode, scope.DeviceCode, now, BatchSize, cancellationToken);
            if (due.Count == 0)
            {
                return uploaded;
            }

            CashMovementSyncResponse response;
            try
            {
                response = await apiClient.SyncAsync(ToRequest(scope.StoreCode, scope.DeviceCode, due), cancellationToken);
            }
            catch (DailyCloseUploadApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // 设备授权问题：不消耗尝试次数，等授权恢复后由轮询继续。
                return uploaded;
            }
            catch (DailyCloseUploadApiException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // 整批被拒（版本、批量大小等请求级问题）：逐条标记永久拒绝，避免每 30 秒重复失败。
                foreach (var movement in due)
                {
                    await repository.MarkUploadResultAsync(movement.MovementGuid, LocalCashMovementUploadStatus.Rejected, ex.ErrorCode, ex.Message, now, null, cancellationToken);
                }

                ConsoleLog.WriteError("CashMovementUpload", $"batch rejected code={ex.ErrorCode} message={ex.Message}");
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // 网络或服务端异常：整批退避，下一轮再试。
                foreach (var movement in due)
                {
                    await repository.MarkUploadResultAsync(movement.MovementGuid, LocalCashMovementUploadStatus.Pending, "NETWORK", ex.Message, now, now + Backoff(), cancellationToken);
                }

                ConsoleLog.WriteWarning("CashMovementUpload", $"upload failed, will retry error={ex.GetType().Name} message={ex.Message}");
                return uploaded;
            }

            var results = response.Results.ToDictionary(item => item.MovementGuid);
            foreach (var movement in due)
            {
                if (!results.TryGetValue(movement.MovementGuid, out var result))
                {
                    await repository.MarkUploadResultAsync(movement.MovementGuid, LocalCashMovementUploadStatus.Pending, "MISSING_RESULT", null, now, now + Backoff(), cancellationToken);
                    continue;
                }

                if (result.Status is CashMovementSyncStatuses.Accepted or CashMovementSyncStatuses.AlreadySynced)
                {
                    await repository.MarkUploadResultAsync(movement.MovementGuid, LocalCashMovementUploadStatus.Synced, null, null, now, null, cancellationToken);
                    uploaded++;
                }
                else if (result.Code == "VOIDED_MOVEMENT_NOT_FOUND")
                {
                    // 原记录还没到服务端（例如原记录这一轮恰好失败）：稍后重试，不判永久拒绝。
                    await repository.MarkUploadResultAsync(movement.MovementGuid, LocalCashMovementUploadStatus.Pending, result.Code, result.Message, now, now + Backoff(), cancellationToken);
                }
                else
                {
                    await repository.MarkUploadResultAsync(movement.MovementGuid, LocalCashMovementUploadStatus.Rejected, result.Code, result.Message, now, null, cancellationToken);
                    ConsoleLog.WriteError(
                        "CashMovementUpload",
                        $"movement rejected movement={movement.MovementGuid:D} code={result.Code} message={result.Message}");
                }
            }

            if (due.Count < BatchSize)
            {
                return uploaded;
            }
        }

        return uploaded;
    }

    internal CashMovementSyncRequest ToRequest(string storeCode, string deviceCode, IReadOnlyList<LocalCashMovement> movements)
    {
        var version = appVersion ?? ClientLogIdentity.CreateCurrent().AppVersion;
        if (version is not null && version.Length > MaximumAppVersionLength)
        {
            version = version[..MaximumAppVersionLength];
        }

        return new CashMovementSyncRequest(
            CashMovementContractConstants.SchemaVersion,
            storeCode,
            deviceCode,
            DailyCloseContractConstants.ClientKindWpf,
            version,
            movements
                .Select(item => new CashMovementSync(
                    item.MovementGuid,
                    CashMovementReasons.ToContractDirection(item.Direction),
                    item.ReasonCode,
                    item.Amount,
                    item.Note,
                    item.VoidsMovementGuid,
                    DateOnly.FromDateTime(item.BusinessDate),
                    item.OccurredAt,
                    item.CashierId,
                    item.CashierName,
                    item.AuthorizedById,
                    item.AuthorizedByName,
                    item.DisplayNumber))
                .ToList());
    }

    private static TimeSpan Backoff() => TimeSpan.FromMinutes(1);
}

/// <summary>
/// 日结上传 Worker 的执行服务装饰：每轮先上传现金进出，再上传日结。
/// 这样服务端收到日结（带存入 / 取出合计）时，对应的明细大多已经在库，后台按日能直接对上。
/// 现金进出上传失败不影响日结上传。
/// </summary>
public sealed class CashMovementFirstUploadExecutionService(
    CashMovementUploadService cashMovementUpload,
    IDailyCloseUploadExecutionService dailyCloseUpload) : IDailyCloseUploadExecutionService
{
    public async Task<DailyCloseUploadExecutionResult> ExecutePendingAsync(
        int batchSize = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await cashMovementUpload.ExecutePendingAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            ConsoleLog.WriteError(
                "CashMovementUpload",
                $"upload iteration failed error={ex.GetType().Name} message={ex.Message}",
                exception: ex);
        }

        return await dailyCloseUpload.ExecutePendingAsync(batchSize, cancellationToken);
    }
}
