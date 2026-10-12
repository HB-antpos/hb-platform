using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>可注入的剩余空间探针：默认报告空间充足，测试可改成「快满」。</summary>
internal sealed class FakeDiskSpaceProbe : IDiskSpaceProbe
{
    private readonly object _gate = new();
    private long? _freeBytes = 1L << 50;
    private int _callCount;

    public long? FreeBytes
    {
        get
        {
            lock (_gate)
            {
                return _freeBytes;
            }
        }

        set
        {
            lock (_gate)
            {
                _freeBytes = value;
            }
        }
    }

    public int CallCount => Volatile.Read(ref _callCount);

    public long? GetAvailableFreeBytes(string path)
    {
        Interlocked.Increment(ref _callCount);
        return FreeBytes;
    }
}

/// <summary>一条被捕获的日志。</summary>
internal sealed record CapturedLog(
    string Category,
    LogLevel Level,
    string Message,
    IReadOnlyList<string> Values,
    string? Exception,
    IReadOnlyList<string> Scopes)
{
    /// <summary>用于「不含 token 原文」断言的全部文本：格式化消息 + 结构化参数 + 异常 + 外部 scope。</summary>
    public string AllText => string.Join('\n', new[] { Message, Exception ?? string.Empty }.Concat(Values).Concat(Scopes));
}

/// <summary>
/// 把日志收集到内存，供断言「日志不含 token」「热更新失败记了 Error」等。
/// 实现 <see cref="ISupportExternalScope"/>：连同 ASP.NET Core 的请求 scope 一起捕获，
/// 这样「不含 token」的断言也覆盖 scope 这条泄露路径（控制台开 IncludeScopes 时会打印它）。
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public IReadOnlyList<CapturedLog> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries, () => _scopeProvider);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string category,
        ConcurrentQueue<CapturedLog> entries,
        Func<IExternalScopeProvider> scopeProvider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => scopeProvider().Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = new List<string>();
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    values.Add($"{pair.Key}={pair.Value}");
                }
            }

            var scopes = new List<string>();
            scopeProvider().ForEachScope(static (scope, list) => list.Add(scope?.ToString() ?? string.Empty), scopes);
            entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), values, exception?.ToString(), scopes));
        }
    }
}

/// <summary>
/// 测试可控的「热更新」配置源：<see cref="Replace"/> 会像文件改动一样触发 reload token，
/// 让 IOptionsMonitor 走与 cameras.json 热更新完全相同的通知链，但不依赖文件监听的时序。
/// </summary>
internal sealed class ReloadableTestConfigurationSource : IConfigurationSource
{
    public ReloadableTestConfigurationSource(IDictionary<string, string?> initialData)
    {
        Provider = new ReloadableTestConfigurationProvider(initialData);
    }

    public ReloadableTestConfigurationProvider Provider { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;

    public void Replace(IDictionary<string, string?> data) => Provider.Replace(data);
}

/// <summary><see cref="ReloadableTestConfigurationSource"/> 的提供程序。</summary>
internal sealed class ReloadableTestConfigurationProvider : ConfigurationProvider
{
    public ReloadableTestConfigurationProvider(IDictionary<string, string?> initialData)
    {
        Data = new Dictionary<string, string?>(initialData, StringComparer.OrdinalIgnoreCase);
    }

    public void Replace(IDictionary<string, string?> data)
    {
        Data = new Dictionary<string, string?>(data, StringComparer.OrdinalIgnoreCase);
        OnReload();
    }
}

/// <summary>只读、不可 Seek 的流，用来模拟没有 Content-Length 的（chunked）请求体。</summary>
internal sealed class NonSeekableReadStream(byte[] data) : Stream
{
    private readonly MemoryStream _inner = new(data, writable: false);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>先吐出一部分数据、随后抛 IOException 的流，模拟摄像机发送到一半断网。</summary>
internal sealed class FailingAfterPrefixStream(byte[] prefix) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= prefix.Length)
        {
            throw new IOException("Simulated connection reset while sending request body.");
        }

        var count = Math.Min(buffer.Length, prefix.Length - _position);
        prefix.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromResult(Read(buffer.AsSpan(offset, count)));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(Read(buffer.Span));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// 先吐出一部分数据，然后挂起直到 <paramref name="disconnect"/> 被触发，再抛 IOException，
/// 模拟摄像机发送到一半网络中断（测试可以先确认服务端已写出 .tmp 残片再「断网」）。
/// </summary>
internal sealed class StallingAfterPrefixStream(byte[] prefix, CancellationToken disconnect) : Stream
{
    private int _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < prefix.Length)
        {
            var count = Math.Min(buffer.Length, prefix.Length - _position);
            prefix.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disconnect);
        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
        catch (OperationCanceledException)
        {
        }

        throw new IOException("Simulated connection reset while sending request body.");
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// 第一次被读时先等服务端建好 <c>.bin.tmp</c>，再在同名 <c>.json.tmp</c> 的位置建一个目录，然后正常吐出全部数据。
/// 效果：请求体完整写入并改名成 .bin，随后创建元数据临时文件失败（IOException），
/// 用来覆盖「.bin 已改名、.json 写失败」时必须连 .bin 一起删除的分支，不需要给生产代码加注入点。
/// </summary>
internal sealed class BlockMetadataTempOnFirstReadStream(byte[] data, string cameraDirectory) : Stream
{
    private const string BodyTempSuffix = ".bin.tmp";
    private readonly MemoryStream _inner = new(data, writable: false);
    private bool _blocked;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_blocked)
        {
            await BlockMetadataTempAsync(cancellationToken);
            _blocked = true;
        }

        return await _inner.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task BlockMetadataTempAsync(CancellationToken cancellationToken)
    {
        // 中文注释：TestServer 的客户端在后台并行拷贝请求体，第一次读可能早于服务端建 .bin.tmp，所以轮询等待。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var bodyTemp = Directory.Exists(cameraDirectory)
                ? Directory.EnumerateFiles(cameraDirectory, "*" + BodyTempSuffix).FirstOrDefault()
                : null;
            if (bodyTemp is not null)
            {
                var baseName = Path.GetFileName(bodyTemp)[..^BodyTempSuffix.Length];
                Directory.CreateDirectory(Path.Combine(cameraDirectory, baseName + ".json.tmp"));
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new InvalidOperationException("服务端在超时前没有创建 .bin.tmp。");
    }
}
