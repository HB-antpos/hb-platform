using System.Collections.Concurrent;
using System.Text.Json;

namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>
/// 内存版写库器：与 <see cref="SqlCaptureEventWriter"/> 同一口径——按 CaptureId 幂等写事件、按 ImageId 幂等写图片、
/// 同摄像机 24 小时内同哈希的最早事件作为 DuplicateOfCaptureId。可设置「接下来失败 N 次」或「一直失败」。
/// </summary>
internal sealed class FakeCaptureEventWriter : ICaptureEventWriter
{
    private readonly object _gate = new();

    public Dictionary<string, (CaptureEventRecord Event, string? DuplicateOf)> Events { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, CaptureImageRecord> Images { get; } = new(StringComparer.Ordinal);

    public List<(string CameraId, DateTime SeenAtUtc)> Touches { get; } = [];

    public int WriteCalls { get; private set; }

    /// <summary>接下来要失败的次数；<see cref="int.MaxValue"/> 表示一直失败。</summary>
    public int FailuresRemaining { get; set; }

    public Task<CaptureWriteOutcome> WriteAsync(CaptureEventRecord captureEvent, IReadOnlyList<CaptureImageRecord> images, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            WriteCalls++;
            if (FailuresRemaining > 0)
            {
                if (FailuresRemaining != int.MaxValue)
                {
                    FailuresRemaining--;
                }

                throw new InvalidOperationException("Simulated database outage (fake writer).");
            }

            var inserted = false;
            string? duplicateOf;
            if (Events.TryGetValue(captureEvent.CaptureId, out var existing))
            {
                duplicateOf = existing.DuplicateOf;
            }
            else
            {
                duplicateOf = Events.Values
                    .Where(e => e.Event.CameraId == captureEvent.CameraId
                        && e.Event.BodySha256 == captureEvent.BodySha256
                        && e.Event.CaptureId != captureEvent.CaptureId
                        && e.Event.ReceivedAtUtc >= captureEvent.ReceivedAtUtc - SqlCaptureEventWriter.DuplicateWindow
                        && e.Event.ReceivedAtUtc <= captureEvent.ReceivedAtUtc)
                    .OrderBy(e => e.Event.ReceivedAtUtc)
                    .ThenBy(e => e.Event.CaptureId, StringComparer.Ordinal)
                    .Select(e => e.Event.CaptureId)
                    .FirstOrDefault();
                Events[captureEvent.CaptureId] = (captureEvent, duplicateOf);
                inserted = true;
            }

            var imagesInserted = 0;
            foreach (var image in images)
            {
                if (Images.TryAdd(image.ImageId, image))
                {
                    imagesInserted++;
                }
            }

            return Task.FromResult(new CaptureWriteOutcome(inserted, imagesInserted, duplicateOf));
        }
    }

    public Task TouchCameraLastSeenAsync(string cameraId, DateTime seenAtUtc, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Touches.Add((cameraId, seenAtUtc));
        }

        return Task.CompletedTask;
    }

    /// <summary>把写入的全部行序列化成文本，用于「不含 token」断言。</summary>
    public string DumpAll()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(Events.Values.Select(e => e.Event)) + JsonSerializer.Serialize(Images.Values);
        }
    }
}

/// <summary>可编排的假摄像机仓储：返回给定行，或抛出模拟的连接失败。</summary>
internal sealed class FakeFaceCameraRepository : IFaceCameraRepository
{
    private readonly object _gate = new();
    private IReadOnlyList<FaceCameraRow> _rows = [];
    private bool _fail = true;

    public int LoadCalls { get; private set; }

    public void Fail()
    {
        lock (_gate)
        {
            _fail = true;
        }
    }

    public void Return(params FaceCameraRow[] rows)
    {
        lock (_gate)
        {
            _rows = rows;
            _fail = false;
        }
    }

    public Task<IReadOnlyList<FaceCameraRow>> LoadAllAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            LoadCalls++;
            if (_fail)
            {
                throw new TimeoutException("Simulated database connection timeout (fake repository).");
            }

            return Task.FromResult(_rows);
        }
    }

    public static FaceCameraRow Row(TestCamera camera) => new(camera.CameraId, camera.StoreCode, camera.TokenSha256, camera.Enabled);
}

/// <summary>可让存图失败的图片存储包装：默认委托给真实存储。</summary>
internal sealed class FlakyImageStore(IImageStore inner) : IImageStore
{
    private int _failuresRemaining;

    public ConcurrentQueue<ImageWriteRequest> Saved { get; } = new();

    public string StorageName => inner.StorageName;

    public int FailuresRemaining
    {
        get => Volatile.Read(ref _failuresRemaining);
        set => Volatile.Write(ref _failuresRemaining, value);
    }

    public async Task<StoredObject> SaveImageAsync(ImageWriteRequest request, CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
        {
            throw new CosUploadException("COS PUT failed status=503 etag=missing kind=images");
        }

        Interlocked.Exchange(ref _failuresRemaining, 0);
        Saved.Enqueue(request);
        return await inner.SaveImageAsync(request, cancellationToken);
    }

    public Task<StoredObject?> SaveRawBodyAsync(RawBodyWriteRequest request, CancellationToken cancellationToken)
        => inner.SaveRawBodyAsync(request, cancellationToken);
}
