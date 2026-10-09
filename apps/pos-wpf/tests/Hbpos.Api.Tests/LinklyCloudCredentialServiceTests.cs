using Hbpos.Api.Services;
using Hbpos.Contracts.Linkly;

namespace Hbpos.Api.Tests;

public sealed class LinklyCloudCredentialServiceTests
{
    [Fact]
    public void Repository_legacy_credential_upsert_locks_mode_and_rejects_active()
    {
        var sql = SqlSugarLinklyCloudCredentialRepository.UpsertSql;

        Assert.Contains("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("POSM_LinklyCloudConfigurationMode", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (UPDLOCK, HOLDLOCK)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THROW 51005", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COMMIT TRANSACTION", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpsertAsync_trims_scope_and_returns_sanitized_response()
    {
        LinklyCloudCredentialRecord? saved = null;
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: (_, _) => Task.FromResult(saved),
            upsertAsync: (storeCode, environment, username, password, updatedAt, updatedBy) =>
            {
                saved = new LinklyCloudCredentialRecord
                {
                    StoreCode = storeCode,
                    Environment = environment,
                    Username = username,
                    Password = password,
                    UpdatedAt = updatedAt,
                    UpdatedBy = updatedBy
                };
                return Task.FromResult(saved);
            }));

        var response = await service.UpsertAsync(
            " S01 ",
            new LinklyCloudCredentialUpsertRequest(" sandbox ", " merchant-user ", " merchant-password "),
            " device:POS-01 ",
            CancellationToken.None);

        Assert.Equal("S01", response.StoreCode);
        Assert.Equal("Sandbox", response.Environment);
        Assert.Equal("merchant-user", response.Username);
        Assert.True(response.HasPassword);
        Assert.Equal("merchant-password", saved?.Password);
        Assert.Equal("device:POS-01", saved?.UpdatedBy);
    }

    [Theory]
    [InlineData("staging", "merchant-user", "merchant-password", "environment must be Production or Sandbox")]
    [InlineData("Sandbox", "", "merchant-password", "username is required.")]
    [InlineData("Sandbox", "merchant-user", "", "password is required.")]
    public async Task UpsertAsync_rejects_invalid_input_without_exposing_secret_values(
        string environment,
        string username,
        string password,
        string expectedMessage)
    {
        var service = new LinklyCloudCredentialService(new FakeLinklyCloudCredentialRepository(
            getByStoreCodeAsync: (_, _) => Task.FromResult<LinklyCloudCredentialRecord?>(null)));

        var exception = await Assert.ThrowsAsync<LinklyCloudCredentialValidationException>(() =>
            service.UpsertAsync(
                "S01",
                new LinklyCloudCredentialUpsertRequest(environment, username, password),
                "device:POS-01",
                CancellationToken.None));

        Assert.Equal(expectedMessage, exception.Message);
        Assert.DoesNotContain("merchant-password", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeLinklyCloudCredentialRepository(
        Func<string, string, Task<LinklyCloudCredentialRecord?>> getByStoreCodeAsync,
        Func<string, string, string, string, DateTime, string?, Task<LinklyCloudCredentialRecord>>? upsertAsync = null) : ILinklyCloudCredentialRepository
    {
        public Task<LinklyCloudCredentialRecord?> GetByStoreCodeAsync(
            string storeCode,
            string environment,
            CancellationToken cancellationToken)
        {
            return getByStoreCodeAsync(storeCode, environment);
        }

        public Task<LinklyCloudCredentialRecord> UpsertAsync(
            string storeCode,
            string environment,
            string username,
            string password,
            DateTime updatedAt,
            string? updatedBy,
            CancellationToken cancellationToken)
        {
            if (upsertAsync is not null)
            {
                return upsertAsync(storeCode, environment, username, password, updatedAt, updatedBy);
            }

            return Task.FromResult(new LinklyCloudCredentialRecord
            {
                StoreCode = storeCode,
                Environment = environment,
                Username = username,
                Password = password,
                UpdatedAt = updatedAt,
                UpdatedBy = updatedBy
            });
        }
    }
}
