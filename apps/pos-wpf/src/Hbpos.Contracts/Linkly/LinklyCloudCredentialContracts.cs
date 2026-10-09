namespace Hbpos.Contracts.Linkly;

public sealed record LinklyCloudCredentialUpsertRequest(
    string Environment,
    string Username,
    string Password);

public sealed record LinklyCloudCredentialUpsertResponse(
    string StoreCode,
    string Environment,
    string Username,
    bool HasPassword,
    DateTimeOffset UpdatedAt);
