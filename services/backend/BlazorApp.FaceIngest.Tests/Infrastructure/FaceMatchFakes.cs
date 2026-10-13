using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>
/// 内存版比对引擎，同时扮演 <see cref="IFaceMatcher"/> 与 <see cref="IVisitorRegistry"/>。
/// 「同一个人」用人脸图字节的 SHA-256 表示：员工库 / 名单库 / 访客库里登记的是图片哈希，
/// 搜索时哈希相同返回高分（<see cref="MatchScore"/>），否则返回低分（<see cref="MissScore"/>）。
/// 可编排失败、可阻塞（验证串行）、记录全部调用（验证不重复搜索 / 不乱建人）。
/// </summary>
internal sealed class FakeFaceEngine : IFaceMatcher, IVisitorRegistry
{
    public const double MatchScore = 99.0;
    public const double MissScore = 30.0;

    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _groups = new(StringComparer.Ordinal);
    private int _concurrent;
    private int _maxConcurrent;

    public List<SearchCall> Searches { get; } = [];

    public List<string> CreatedGroups { get; } = [];

    public List<(string GroupId, string PersonId)> CreatedPersons { get; } = [];

    /// <summary>接下来要抛出的异常（先进先出）；队列空了才正常返回。</summary>
    public ConcurrentQueue<Exception> SearchFailures { get; } = new();

    public ConcurrentQueue<Exception> CreatePersonFailures { get; } = new();

    public ConcurrentQueue<Exception> CreateGroupFailures { get; } = new();

    /// <summary>搜索前先 await 它（用来在测试里卡住第一个搜索，观察第二个是否被串行挡住）。</summary>
    public Func<Task>? SearchGate { get; set; }

    public bool AllowEmptyGroupSearch { get; set; } = true;

    public int MaxConcurrentSearches => Volatile.Read(ref _maxConcurrent);

    public static string HashOf(ReadOnlySpan<byte> image) => Convert.ToHexStringLower(SHA256.HashData(image));

    /// <summary>往库里登记一个「人」（员工库 / 名单库，由测试手工登记，模拟主 API 的管理动作）。</summary>
    public string Enroll(string groupId, byte[] image)
    {
        lock (_gate)
        {
            if (!_groups.TryGetValue(groupId, out var persons))
            {
                _groups[groupId] = persons = new HashSet<string>(StringComparer.Ordinal);
            }

            var hash = HashOf(image);
            persons.Add(hash);
            return PersonIdFor(hash);
        }
    }

    public static string PersonIdFor(string imageHash) => "person-" + imageHash[..16];

    public int PersonCount(string groupId)
    {
        lock (_gate)
        {
            return _groups.TryGetValue(groupId, out var persons) ? persons.Count : 0;
        }
    }

    public async Task<FaceSearchResult> SearchAsync(FaceSearchRequest request, CancellationToken cancellationToken)
    {
        var concurrent = Interlocked.Increment(ref _concurrent);
        InterlockedMax(ref _maxConcurrent, concurrent);
        try
        {
            lock (_gate)
            {
                Searches.Add(new SearchCall([.. request.GroupIds], request.Image.Length, request.MinScore));
            }

            if (SearchGate is { } gate)
            {
                await gate();
            }

            if (SearchFailures.TryDequeue(out var failure))
            {
                throw failure;
            }

            var image = request.Image.ToArray();
            var hash = HashOf(image);
            var hits = new List<FaceSearchHit>();
            lock (_gate)
            {
                foreach (var groupId in request.GroupIds)
                {
                    if (!_groups.TryGetValue(groupId, out var persons))
                    {
                        // 中文注释：真实腾讯云对不存在的库报 GroupIdNotExist；访客库由流水线先 CreateGroup，所以这里按「空库」处理。
                        if (!AllowEmptyGroupSearch)
                        {
                            throw new FaceMatchApiException(FaceMatchErrorKind.RequestFatal, "InvalidParameterValue.GroupIdNotExist", "SearchFacesReturnsByGroup");
                        }

                        continue;
                    }

                    if (persons.Count == 0)
                    {
                        continue;
                    }

                    var score = persons.Contains(hash) ? MatchScore : MissScore;
                    var personId = persons.Contains(hash) ? PersonIdFor(hash) : "person-other";
                    if (request.MinScore is double min && score < min)
                    {
                        continue;
                    }

                    hits.Add(new FaceSearchHit(groupId, personId, score));
                }
            }

            return new FaceSearchResult(FaceSearchStatus.Ok, hits);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public Task CreateGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        if (CreateGroupFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        lock (_gate)
        {
            CreatedGroups.Add(groupId);
            _groups.TryAdd(groupId, new HashSet<string>(StringComparer.Ordinal));
        }

        return Task.CompletedTask;
    }

    public Task CreatePersonAsync(string groupId, string personId, ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        if (CreatePersonFailures.TryDequeue(out var failure))
        {
            throw failure;
        }

        lock (_gate)
        {
            CreatedPersons.Add((groupId, personId));
            if (!_groups.TryGetValue(groupId, out var persons))
            {
                _groups[groupId] = persons = new HashSet<string>(StringComparer.Ordinal);
            }

            persons.Add(HashOf(image.Span));
        }

        return Task.CompletedTask;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    internal sealed record SearchCall(IReadOnlyList<string> GroupIds, int ImageLength, double? MinScore);
}

/// <summary>内存版写库：计数桶（线程安全累加）、访客库行、任务、评估；可注入失败。</summary>
internal sealed class FakeFaceMatchStore : IFaceMatchStore
{
    private readonly object _gate = new();

    public Dictionary<(string StoreCode, DateTime Bucket), TrafficDelta> Buckets { get; } = [];

    public List<VisitGroupRecord> VisitGroups { get; } = [];

    public Dictionary<string, int> VisitorCounts { get; } = new(StringComparer.Ordinal);

    public List<MatchTaskRecord> Tasks { get; } = [];

    public List<EvaluationRecord> Evaluations { get; } = [];

    public Dictionary<string, UnwelcomeEntryInfo> Entries { get; } = new(StringComparer.Ordinal);

    public int ApplyCalls { get; private set; }

    /// <summary>接下来 ApplyTrafficAsync 要失败的次数；<see cref="int.MaxValue"/> 表示一直失败。</summary>
    public int ApplyFailuresRemaining { get; set; }

    public int EnsureGroupFailuresRemaining { get; set; }

    public int EvaluationFailuresRemaining { get; set; }

    public TrafficDelta Total()
    {
        lock (_gate)
        {
            return Buckets.Values.Aggregate(new TrafficDelta(), (sum, d) => new TrafficDelta
            {
                Capture = sum.Capture + d.Capture,
                StaffMatch = sum.StaffMatch + d.StaffMatch,
                UniqueVisitor = sum.UniqueVisitor + d.UniqueVisitor,
                Repeat = sum.Repeat + d.Repeat,
                LowQuality = sum.LowQuality + d.LowQuality,
                ApiFailure = sum.ApiFailure + d.ApiFailure,
            });
        }
    }

    public Task EnsureVisitGroupAsync(VisitGroupRecord group, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (EnsureGroupFailuresRemaining > 0)
            {
                EnsureGroupFailuresRemaining--;
                throw new InvalidOperationException("Simulated database outage (ensure visit group).");
            }

            if (VisitGroups.All(existing => existing.GroupId != group.GroupId))
            {
                VisitGroups.Add(group);
                VisitorCounts[group.GroupId] = 0;
            }
        }

        return Task.CompletedTask;
    }

    public Task ApplyTrafficAsync(TrafficUpdate update, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ApplyCalls++;
            if (ApplyFailuresRemaining > 0)
            {
                if (ApplyFailuresRemaining != int.MaxValue)
                {
                    ApplyFailuresRemaining--;
                }

                throw new InvalidOperationException("Simulated database outage (apply traffic).");
            }

            var key = (update.StoreCode, update.BucketStartUtc);
            Buckets.TryGetValue(key, out var existing);
            existing ??= new TrafficDelta();
            Buckets[key] = new TrafficDelta
            {
                Capture = existing.Capture + update.Delta.Capture,
                StaffMatch = existing.StaffMatch + update.Delta.StaffMatch,
                UniqueVisitor = existing.UniqueVisitor + update.Delta.UniqueVisitor,
                Repeat = existing.Repeat + update.Delta.Repeat,
                LowQuality = existing.LowQuality + update.Delta.LowQuality,
                ApiFailure = existing.ApiFailure + update.Delta.ApiFailure,
            };
            if (update.VisitGroupId is not null && update.VisitorsAdded > 0)
            {
                VisitorCounts[update.VisitGroupId] = VisitorCounts.GetValueOrDefault(update.VisitGroupId) + update.VisitorsAdded;
            }
        }

        return Task.CompletedTask;
    }

    public Task<UnwelcomeEntryInfo?> FindUnwelcomeEntryAsync(string tencentPersonId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(Entries.TryGetValue(tencentPersonId, out var entry) ? entry : null);
        }
    }

    public Task InsertMatchTaskAsync(MatchTaskRecord task, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Tasks.All(existing => existing.TaskId != task.TaskId))
            {
                Tasks.Add(task);
            }
        }

        return Task.CompletedTask;
    }

    public Task InsertEvaluationAsync(EvaluationRecord record, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (EvaluationFailuresRemaining > 0)
            {
                EvaluationFailuresRemaining--;
                throw new InvalidOperationException("Simulated database outage (evaluation).");
            }

            if (Evaluations.All(existing => existing.CaptureId != record.CaptureId))
            {
                Evaluations.Add(record);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>可编排的假档案仓储：返回给定行，或抛出模拟的连接失败。</summary>
internal sealed class FakeFaceStoreProfileRepository : IFaceStoreProfileRepository
{
    private readonly object _gate = new();
    private IReadOnlyList<FaceStoreProfileRow> _rows = [];
    private bool _fail = true;

    public int LoadCalls { get; private set; }

    public void Fail()
    {
        lock (_gate)
        {
            _fail = true;
        }
    }

    public void Return(params FaceStoreProfileRow[] rows)
    {
        lock (_gate)
        {
            _rows = rows;
            _fail = false;
        }
    }

    public Task<IReadOnlyList<FaceStoreProfileRow>> LoadAllAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            LoadCalls++;
            if (_fail)
            {
                throw new TimeoutException("Simulated database connection timeout (fake profile repository).");
            }

            return Task.FromResult(_rows);
        }
    }
}

/// <summary>名单命中图存储的假实现：记录保存的图与对象键。</summary>
internal sealed class FakeUnwelcomeHitImageStore : IUnwelcomeHitImageStore
{
    public bool Available { get; set; } = true;

    public ConcurrentQueue<(UnwelcomeHitImage Image, string ObjectKey)> Saved { get; } = new();

    public Task<string> SaveAsync(UnwelcomeHitImage image, CancellationToken cancellationToken)
    {
        // 与真实 CosImageStore 同一前缀规则，便于断言「私有 COS 前缀 face-ingest/unwelcome-hits/」。
        var datePath = image.CapturedAtUtc.UtcDateTime.ToString("yyyy'/'MM'/'dd", System.Globalization.CultureInfo.InvariantCulture);
        var key = $"face-ingest/unwelcome-hits/{image.StoreCode}/{datePath}/{image.CameraId}/{image.TaskId}.jpg";
        Saved.Enqueue((image, key));
        return Task.FromResult(key);
    }
}

/// <summary>记录所有存图调用的图片存储（Match / Off 下应当一次都没有）。</summary>
internal sealed class RecordingImageStore : IImageStore
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public string StorageName => "recording";

    public Task<StoredObject> SaveImageAsync(ImageWriteRequest request, CancellationToken cancellationToken)
    {
        Calls.Enqueue("image:" + request.CaptureId);
        return Task.FromResult(new StoredObject("local", "images/test/" + request.CaptureId));
    }

    public Task<StoredObject?> SaveRawBodyAsync(RawBodyWriteRequest request, CancellationToken cancellationToken)
    {
        Calls.Enqueue("raw:" + request.CaptureId);
        return Task.FromResult<StoredObject?>(null);
    }
}

/// <summary>
/// 假的腾讯云 HTTP 处理器：记录每个请求（方法 / URL / 头 / 体），按脚本返回响应。没有脚本时返回「空库、无命中」。
/// 任何测试只要没注册处理器，真实 HttpClient 就会去连外网——所以「整体关闭」的测试都用它的 RequestCount 断言为 0。
/// </summary>
internal sealed class ScriptedTencentHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _script = new();
    private int _requestCount;

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public int RequestCount => Volatile.Read(ref _requestCount);

    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response) => _script.Enqueue(response);

    public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK) => Enqueue(_ => Json(json, status));

    public void EnqueueError(string code) => EnqueueJson($$$"""{"Response":{"Error":{"Code":"{{{code}}}","Message":"free text that must never be logged"},"RequestId":"req-test-1"}}""");

    public void EnqueueThrow(Exception exception) => Enqueue(_ => throw exception);

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        Requests.Enqueue(new RecordedRequest(request.Method.Method, request.RequestUri!.ToString(), headers, body));
        if (_script.TryDequeue(out var next))
        {
            return next(request);
        }

        return Json("""{"Response":{"ResultsReturnsByGroup":[{"RetCode":0,"GroupCandidates":[]}],"RequestId":"req-default"}}""");
    }

    internal sealed record RecordedRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string Body)
    {
        public JsonElement Json()
        {
            using var document = JsonDocument.Parse(Body);
            return document.RootElement.Clone();
        }
    }
}
