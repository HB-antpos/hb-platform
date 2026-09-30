using BlazorApp.Shared.Models.HBweb;

namespace BlazorApp.Api.Interfaces
{
    internal interface IMobileAppBuildMirrorQueue
    {
        Task<MobileAppBuild?> ClaimNextCosMirrorJobAsync(
            DateTime now,
            int maxAttempts,
            TimeSpan staleRunningAfter
        );

        Task CompleteCosMirrorSuccessAsync(
            MobileAppBuild entity,
            MobileAppBuildArtifactMirrorResult mirror
        );

        Task CompleteCosMirrorFailureAsync(MobileAppBuild entity, Exception exception);

        Task<MobileAppBuild?> ClaimNextCosChecksumBackfillJobAsync(
            DateTime now,
            int maxAttempts,
            TimeSpan retryAfter
        );

        Task CompleteCosChecksumBackfillSuccessAsync(
            MobileAppBuild entity,
            MobileAppBuildArtifactChecksum checksum
        );

        Task CompleteCosChecksumBackfillFailureAsync(MobileAppBuild entity, Exception exception);
    }
}
