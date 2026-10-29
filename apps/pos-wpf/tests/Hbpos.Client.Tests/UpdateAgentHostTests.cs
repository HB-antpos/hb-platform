using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Hbpos.Updater;

namespace Hbpos.Client.Tests;

// 中文注释：用真实文件系统、本机回环 HTTP 服务和真实进程验证代理宿主的 IO 行为；不需要管理员权限。
public sealed class UpdateAgentHostTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hbpos-agent-host-{Guid.NewGuid():N}");
    private readonly WindowsUpdateAgentHost _host = new();

    public UpdateAgentHostTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task DownloadAsync_writes_exact_body_from_trusted_response()
    {
        var body = Encoding.UTF8.GetBytes("installer-bytes");
        await using var server = LoopbackServer.Start(_ => LoopbackServer.Response.Ok(body));

        var destination = Path.Combine(_root, "pkg.download");
        await _host.DownloadAsync(server.Url("/wpf-releases/a/setup.exe"), destination, body.Length, CancellationToken.None);

        Assert.Equal(body, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task DownloadAsync_rejects_content_length_that_differs_from_request()
    {
        await using var server = LoopbackServer.Start(_ => LoopbackServer.Response.Ok(new byte[10]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _host.DownloadAsync(server.Url("/x.exe"), Path.Combine(_root, "a.download"), expectedSize: 11, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAsync_aborts_when_body_is_larger_than_declared_without_content_length()
    {
        await using var server = LoopbackServer.Start(_ => LoopbackServer.Response.OkWithoutContentLength(new byte[64]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _host.DownloadAsync(server.Url("/x.exe"), Path.Combine(_root, "b.download"), expectedSize: 10, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAsync_rejects_short_body_without_content_length()
    {
        await using var server = LoopbackServer.Start(_ => LoopbackServer.Response.OkWithoutContentLength(new byte[5]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            _host.DownloadAsync(server.Url("/x.exe"), Path.Combine(_root, "c.download"), expectedSize: 10, CancellationToken.None));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(500)]
    public async Task DownloadAsync_fails_on_http_errors(int statusCode)
    {
        await using var server = LoopbackServer.Start(_ => LoopbackServer.Response.Status(statusCode));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            _host.DownloadAsync(server.Url("/x.exe"), Path.Combine(_root, "d.download"), expectedSize: 10, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadAsync_does_not_follow_redirects_to_other_hosts()
    {
        var followed = 0;
        await using var target = LoopbackServer.Start(_ =>
        {
            Interlocked.Increment(ref followed);
            return LoopbackServer.Response.Ok(new byte[10]);
        });
        await using var redirector = LoopbackServer.Start(_ => LoopbackServer.Response.Redirect(target.Url("/evil.exe").AbsoluteUri));

        // 中文注释：只从已校验的 COS 源站下载；被重定向到别处一律失败，不跟随。
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            _host.DownloadAsync(redirector.Url("/x.exe"), Path.Combine(_root, "e.download"), expectedSize: 10, CancellationToken.None));
        Assert.Equal(0, followed);
    }

    [Fact]
    public async Task TryComputeSha256Async_hashes_existing_files_and_returns_null_for_missing_ones()
    {
        var path = Path.Combine(_root, "data.bin");
        var content = Encoding.UTF8.GetBytes("hello");
        await File.WriteAllBytesAsync(path, content);

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
            await _host.TryComputeSha256Async(path, CancellationToken.None));
        Assert.Null(await _host.TryComputeSha256Async(Path.Combine(_root, "missing.bin"), CancellationToken.None));
    }

    [Fact]
    public void TryAcquireWorkerLock_is_exclusive_and_releases_and_removes_lock_file_on_dispose()
    {
        var lockPath = Path.Combine(_root, "worker.lock");

        using (var first = _host.TryAcquireWorkerLock(lockPath, TimeSpan.Zero))
        {
            Assert.NotNull(first);
            Assert.Null(_host.TryAcquireWorkerLock(lockPath, TimeSpan.FromMilliseconds(300)));
        }

        Assert.False(File.Exists(lockPath));
        using var second = _host.TryAcquireWorkerLock(lockPath, TimeSpan.Zero);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task RunInstallerAsync_returns_process_exit_code()
    {
        var exitCode = await _host.RunInstallerAsync(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/c exit 7",
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(7, exitCode);
    }

    [Fact]
    public async Task RunInstallerAsync_throws_timeout_when_installer_runs_too_long()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => _host.RunInstallerAsync(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/c ping -n 4 127.0.0.1 >nul",
            TimeSpan.FromMilliseconds(300),
            CancellationToken.None));
    }

    [Fact]
    public void WriteAllTextAtomic_replaces_content_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_root, "status.json");

        _host.WriteAllTextAtomic(path, "one");
        _host.WriteAllTextAtomic(path, "two");

        Assert.Equal("two", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(_root, "status.json*"));
    }

    [Fact]
    public void CopyDirectory_copies_nested_files_for_the_worker()
    {
        var source = Path.Combine(_root, "updater");
        Directory.CreateDirectory(Path.Combine(source, "zh-CN"));
        File.WriteAllText(Path.Combine(source, "Hbpos.Updater.exe"), "exe");
        File.WriteAllText(Path.Combine(source, "zh-CN", "res.dll"), "res");

        var destination = Path.Combine(_root, "runtime", "abc");
        _host.CopyDirectory(source, destination);

        Assert.Equal("exe", File.ReadAllText(Path.Combine(destination, "Hbpos.Updater.exe")));
        Assert.Equal("res", File.ReadAllText(Path.Combine(destination, "zh-CN", "res.dll")));
    }

    [Fact]
    public void CleanupStaleFiles_keeps_current_and_one_previous_package_and_removes_other_runtimes_and_old_files()
    {
        var layout = new UpdateAgentLayout(Path.Combine(_root, "app"));
        foreach (var directory in new[] { layout.PackagesDirectory, layout.StatusDirectory, layout.LogsDirectory, layout.RuntimeRootDirectory })
        {
            Directory.CreateDirectory(directory);
        }

        var packages = new[] { "a", "b", "c", "d" }.Select(name => Path.Combine(layout.PackagesDirectory, name + ".exe")).ToArray();
        for (var index = 0; index < packages.Length; index++)
        {
            File.WriteAllText(packages[index], "x");
            File.SetLastWriteTimeUtc(packages[index], DateTime.UtcNow.AddHours(-index - 1));
        }

        var currentRuntime = Directory.CreateDirectory(Path.Combine(layout.RuntimeRootDirectory, "current")).FullName;
        var oldRuntime = Directory.CreateDirectory(Path.Combine(layout.RuntimeRootDirectory, "old")).FullName;
        var oldStatus = Path.Combine(layout.StatusDirectory, "old.json");
        var freshStatus = Path.Combine(layout.StatusDirectory, "fresh.json");
        File.WriteAllText(oldStatus, "{}");
        File.WriteAllText(freshStatus, "{}");
        File.SetLastWriteTimeUtc(oldStatus, DateTime.UtcNow.AddDays(-10));

        // 中文注释：keep 是刚装完的那个包（这里选最旧的 d），保留它和最新的另一个包 a，其余删除。
        _host.CleanupStaleFiles(layout, packages[3], currentRuntime);

        Assert.Equal(
            [packages[0], packages[3]],
            Directory.GetFiles(layout.PackagesDirectory).Order().ToArray());
        Assert.True(Directory.Exists(currentRuntime));
        Assert.False(Directory.Exists(oldRuntime));
        Assert.False(File.Exists(oldStatus));
        Assert.True(File.Exists(freshStatus));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupStaleFiles_never_deletes_the_running_workers_own_runtime_folder(bool withTrailingSeparator)
    {
        var layout = new UpdateAgentLayout(Path.Combine(_root, "app"));
        Directory.CreateDirectory(layout.PackagesDirectory);
        Directory.CreateDirectory(layout.StatusDirectory);
        Directory.CreateDirectory(layout.LogsDirectory);
        var current = Directory.CreateDirectory(Path.Combine(layout.RuntimeRootDirectory, "current")).FullName;
        File.WriteAllText(Path.Combine(current, "Hbpos.Updater.exe"), "running worker");
        var other = Directory.CreateDirectory(Path.Combine(layout.RuntimeRootDirectory, "other")).FullName;
        var package = Path.Combine(layout.PackagesDirectory, "keep.exe");
        File.WriteAllText(package, "x");

        // 中文注释：worker 传入的是 AppContext.BaseDirectory，带结尾反斜杠；带不带都不能把自己所在目录当成旧目录删掉。
        _host.CleanupStaleFiles(layout, package, withTrailingSeparator ? current + Path.DirectorySeparatorChar : current);

        Assert.True(File.Exists(Path.Combine(current, "Hbpos.Updater.exe")));
        Assert.False(Directory.Exists(other));
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;

        private LoopbackServer(Func<string, Response> handler)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(() => AcceptAsync(handler));
        }

        public static LoopbackServer Start(Func<string, Response> handler) => new(handler);

        public Uri Url(string path) => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            try
            {
                await _loop.WaitAsync(TestTimeout);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }

            _stopping.Dispose();
        }

        private async Task AcceptAsync(Func<string, Response> handler)
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client, handler));
            }
        }

        private static async Task ServeAsync(TcpClient client, Func<string, Response> handler)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var requestBytes = new List<byte>();
                    var buffer = new byte[1024];
                    while (!Encoding.ASCII.GetString(requestBytes.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer);
                        if (read == 0)
                        {
                            return;
                        }

                        requestBytes.AddRange(buffer.AsSpan(0, read).ToArray());
                    }

                    var path = Encoding.ASCII.GetString(requestBytes.ToArray()).Split(' ', 3)[1];
                    var response = handler(path);
                    await stream.WriteAsync(response.ToBytes());
                    await stream.FlushAsync();
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                }
            }
        }

        public sealed record Response(int StatusCode, string? Location, byte[] Body, bool IncludeContentLength)
        {
            public static Response Ok(byte[] body) => new(200, null, body, true);

            public static Response OkWithoutContentLength(byte[] body) => new(200, null, body, false);

            public static Response Status(int statusCode) => new(statusCode, null, [], true);

            public static Response Redirect(string location) => new(302, location, [], true);

            public byte[] ToBytes()
            {
                var head = new StringBuilder($"HTTP/1.1 {StatusCode} X\r\nConnection: close\r\n");
                if (Location is not null)
                {
                    head.Append($"Location: {Location}\r\n");
                }

                if (IncludeContentLength)
                {
                    head.Append($"Content-Length: {Body.Length}\r\n");
                }

                head.Append("\r\n");
                return [.. Encoding.ASCII.GetBytes(head.ToString()), .. Body];
            }
        }
    }
}
