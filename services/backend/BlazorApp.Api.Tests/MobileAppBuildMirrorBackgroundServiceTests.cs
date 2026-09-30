using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services;
using BlazorApp.Shared.Models.HBweb;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlazorApp.Api.Tests;

public sealed class MobileAppBuildMirrorBackgroundServiceTests
{
    [Fact]
    public async Task ProcessOneAsync_宿主取消时不写失败状态()
    {
        var queue = new FakeMirrorQueue
        {
            ClaimedJob = new MobileAppBuild
            {
                Id = Guid.NewGuid(),
                EasBuildId = "build-cancel",
                ArtifactUrl = "https://expo.dev/artifacts/eas/build-cancel.apk",
            },
        };
        var mirror = new FakeArtifactMirror { ThrowWhenTokenCancelled = true };
        using var host = CreateHost(queue, mirror);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            host.Service.ProcessOneAsync(cts.Token)
        );

        Assert.Equal(1, queue.ClaimCalls);
        Assert.Equal(1, mirror.Calls);
        Assert.Equal(0, queue.SuccessCalls);
        Assert.Equal(0, queue.FailureCalls);
    }

    [Fact]
    public async Task ProcessOneAsync_只依赖镜像队列接口无需解析ConcreteService()
    {
        var queue = new FakeMirrorQueue();
        var mirror = new FakeArtifactMirror();
        using var host = CreateHost(queue, mirror);

        var processed = await host.Service.ProcessOneAsync(CancellationToken.None);

        Assert.False(processed);
        Assert.Equal(1, queue.ClaimCalls);
        Assert.Equal(1, queue.BackfillClaimCalls);
        Assert.Equal(0, mirror.Calls);
        Assert.Equal(0, mirror.ChecksumCalls);
    }

    [Fact]
    public async Task ProcessOneAsync_有镜像任务时_不做校验值补算()
    {
        var queue = new FakeMirrorQueue
        {
            ClaimedJob = new MobileAppBuild { Id = Guid.NewGuid(), EasBuildId = "build-new" },
            BackfillJob = new MobileAppBuild { Id = Guid.NewGuid(), EasBuildId = "build-old" },
        };
        var mirror = new FakeArtifactMirror();
        using var host = CreateHost(queue, mirror);

        Assert.True(await host.Service.ProcessOneAsync(CancellationToken.None));

        Assert.Equal(1, queue.SuccessCalls);
        Assert.Equal(0, queue.BackfillClaimCalls);
        Assert.Equal(0, mirror.ChecksumCalls);
    }

    [Fact]
    public async Task ProcessOneAsync_镜像队列空闲时_补算成功但缺校验值的记录()
    {
        var job = new MobileAppBuild { Id = Guid.NewGuid(), EasBuildId = "build-missing-checksum" };
        var queue = new FakeMirrorQueue { BackfillJob = job };
        var mirror = new FakeArtifactMirror();
        using var host = CreateHost(queue, mirror);

        Assert.True(await host.Service.ProcessOneAsync(CancellationToken.None));

        Assert.Equal(1, mirror.ChecksumCalls);
        Assert.Same(job, queue.BackfillSucceeded);
        Assert.Equal(new string('a', 64), queue.BackfillChecksum!.Sha256);
        Assert.Equal(0, queue.BackfillFailureCalls);
    }

    [Fact]
    public async Task ProcessOneAsync_补算失败_回写失败而不是抛出()
    {
        var queue = new FakeMirrorQueue
        {
            BackfillJob = new MobileAppBuild { Id = Guid.NewGuid(), EasBuildId = "build-cos-missing" },
        };
        var mirror = new FakeArtifactMirror
        {
            ChecksumException = new MobileAppBuildArtifactMirrorException("COS 对象返回 HTTP 404 Not Found"),
        };
        using var host = CreateHost(queue, mirror);

        Assert.True(await host.Service.ProcessOneAsync(CancellationToken.None));

        Assert.Null(queue.BackfillSucceeded);
        Assert.Equal(1, queue.BackfillFailureCalls);
    }

    private static TestHost CreateHost(
        FakeMirrorQueue queue,
        FakeArtifactMirror mirror
    )
    {
        var services = new ServiceCollection();
        services.AddScoped<IMobileAppBuildMirrorQueue>(_ => queue);
        services.AddScoped<IMobileAppBuildArtifactMirror>(_ => mirror);
        var provider = services.BuildServiceProvider();

        var service = new MobileAppBuildMirrorBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MobileAppBuildMirrorBackgroundService>.Instance
        );

        return new TestHost(provider, service);
    }

    private sealed class TestHost : IDisposable
    {
        private readonly ServiceProvider _provider;

        public TestHost(
            ServiceProvider provider,
            MobileAppBuildMirrorBackgroundService service
        )
        {
            _provider = provider;
            Service = service;
        }

        public MobileAppBuildMirrorBackgroundService Service { get; }

        public void Dispose()
        {
            _provider.Dispose();
        }
    }

    private sealed class FakeMirrorQueue : IMobileAppBuildMirrorQueue
    {
        public MobileAppBuild? ClaimedJob { get; init; }

        public int ClaimCalls { get; private set; }

        public int SuccessCalls { get; private set; }

        public int FailureCalls { get; private set; }

        public Task<MobileAppBuild?> ClaimNextCosMirrorJobAsync(
            DateTime now,
            int maxAttempts,
            TimeSpan staleRunningAfter
        )
        {
            ClaimCalls++;
            return Task.FromResult(ClaimedJob);
        }

        public Task CompleteCosMirrorSuccessAsync(
            MobileAppBuild entity,
            MobileAppBuildArtifactMirrorResult mirror
        )
        {
            SuccessCalls++;
            return Task.CompletedTask;
        }

        public Task CompleteCosMirrorFailureAsync(MobileAppBuild entity, Exception exception)
        {
            FailureCalls++;
            return Task.CompletedTask;
        }

        public MobileAppBuild? BackfillJob { get; init; }

        public int BackfillClaimCalls { get; private set; }

        public MobileAppBuild? BackfillSucceeded { get; private set; }

        public MobileAppBuildArtifactChecksum? BackfillChecksum { get; private set; }

        public int BackfillFailureCalls { get; private set; }

        public Task<MobileAppBuild?> ClaimNextCosChecksumBackfillJobAsync(
            DateTime now,
            int maxAttempts,
            TimeSpan retryAfter
        )
        {
            BackfillClaimCalls++;
            return Task.FromResult(BackfillJob);
        }

        public Task CompleteCosChecksumBackfillSuccessAsync(
            MobileAppBuild entity,
            MobileAppBuildArtifactChecksum checksum
        )
        {
            BackfillSucceeded = entity;
            BackfillChecksum = checksum;
            return Task.CompletedTask;
        }

        public Task CompleteCosChecksumBackfillFailureAsync(MobileAppBuild entity, Exception exception)
        {
            BackfillFailureCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArtifactMirror : IMobileAppBuildArtifactMirror
    {
        public bool ThrowWhenTokenCancelled { get; init; }

        public int Calls { get; private set; }

        public Task<MobileAppBuildArtifactMirrorResult> MirrorAsync(
            MobileAppBuild build,
            CancellationToken cancellationToken = default
        )
        {
            Calls++;
            if (ThrowWhenTokenCancelled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(
                new MobileAppBuildArtifactMirrorResult
                {
                    ArtifactUrl = "https://cos.example.com/mobile-app-builds/production/build.apk",
                    ObjectKey = "mobile-app-builds/production/build.apk",
                    Sha256 = new string('a', 64),
                    FileSize = 4096,
                    MirroredAt = DateTime.UtcNow,
                }
            );
        }

        public Exception? ChecksumException { get; init; }

        public int ChecksumCalls { get; private set; }

        public Task<MobileAppBuildArtifactChecksum> ComputeMirroredChecksumAsync(
            MobileAppBuild build,
            CancellationToken cancellationToken = default
        )
        {
            ChecksumCalls++;
            if (ChecksumException != null)
            {
                throw ChecksumException;
            }

            return Task.FromResult(
                new MobileAppBuildArtifactChecksum { Sha256 = new string('a', 64), FileSize = 4096 }
            );
        }
    }
}
