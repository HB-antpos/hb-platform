using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    public interface ISeasonalCardRemainingReactService
    {
        Task<ApiResponse<List<SeasonalCardCatalogDto>>> GetCatalogAsync();
        Task<ApiResponse<SeasonalCardRemainingSubmissionDto>> CreateSubmissionAsync(
            CreateSeasonalCardRemainingSubmissionDto request
        );
        Task<ApiResponse<SeasonalCardBatchDto>> CreateBatchAsync(
            CreateSeasonalCardRemainingBatchDto request
        );
        Task<ApiResponse<SeasonalCardOverviewDto>> GetOverviewAsync(
            SeasonalCardOverviewQueryDto query
        );
        Task<ApiResponse<PagedResult<SeasonalCardRemainingSubmissionDto>>> GetSubmissionsAsync(
            SeasonalCardRemainingSubmissionQueryDto query
        );
        Task<ApiResponse<SeasonalCardRemainingSubmissionDto>> GetSubmissionByGuidAsync(
            string submissionGuid
        );
    }
}
