using Hbpos.Api.Services;

namespace Hbpos.Api.Tests;

public sealed class SquareTokenServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetActiveTokenAsync_ReturnsNullWhenRepositoryTokenHasBlankAccessToken(string? accessToken)
    {
        var repository = new RecordingSquareTokenRepository(new SquareTokenRecord
        {
            Environment = "Production",
            AccessToken = accessToken,
            IsEnabled = true,
            UpdatedAt = new DateTime(2026, 5, 26, 4, 0, 0, DateTimeKind.Utc)
        });
        var service = new SquareTokenService(repository, new FixedStoreScope(null));

        var response = await service.GetActiveTokenAsync("production", CancellationToken.None);

        Assert.Null(response);
        Assert.Equal("Production", repository.LastEnvironment);
    }

    [Fact]
    public async Task GetActiveTokenAsync_PassesDeviceStoreToRepository()
    {
        var repository = new RecordingSquareTokenRepository(new SquareTokenRecord
        {
            Environment = "Production",
            StoreCode = "1042",
            AccessToken = "store-token",
            IsEnabled = true,
            UpdatedAt = new DateTime(2026, 10, 14, 4, 0, 0, DateTimeKind.Utc)
        });
        var service = new SquareTokenService(repository, new FixedStoreScope("1042"));

        var response = await service.GetActiveTokenAsync("production", CancellationToken.None);

        Assert.Equal("store-token", response!.AccessToken);
        Assert.Equal("1042", repository.LastStoreCode);
    }

    [Fact]
    public async Task GetActiveTokenAsync_WithoutStoreContextOnlyAsksForSharedToken()
    {
        var repository = new RecordingSquareTokenRepository(new SquareTokenRecord
        {
            Environment = "Sandbox",
            AccessToken = "shared-token",
            IsEnabled = true,
            UpdatedAt = new DateTime(2026, 10, 14, 4, 0, 0, DateTimeKind.Utc)
        });
        var service = new SquareTokenService(repository, new FixedStoreScope(null));

        var response = await service.GetActiveTokenAsync("sandbox", CancellationToken.None);

        Assert.Equal("shared-token", response!.AccessToken);
        Assert.Null(repository.LastStoreCode);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(" 1042 ", "1042")]
    public void NormalizeStoreCode_TrimsAndTreatsBlankAsShared(string? input, string? expected)
    {
        Assert.Equal(expected, SquareTokenService.NormalizeStoreCode(input));
    }

    private sealed class FixedStoreScope(string? storeCode) : ISquareStoreScope
    {
        public string? StoreCode => storeCode;
    }

    private sealed class RecordingSquareTokenRepository(SquareTokenRecord? response) : ISquareTokenRepository
    {
        public string? LastEnvironment { get; private set; }

        public string? LastStoreCode { get; private set; }

        public Task<SquareTokenRecord?> GetActiveTokenAsync(
            string environment,
            string? storeCode,
            CancellationToken cancellationToken)
        {
            LastEnvironment = environment;
            LastStoreCode = storeCode;
            return Task.FromResult(response);
        }
    }
}
