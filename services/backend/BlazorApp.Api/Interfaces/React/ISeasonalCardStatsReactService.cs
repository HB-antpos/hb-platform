using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Interfaces.React
{
    /// <summary>后台「节日贺卡填报统计」：全部分店口径，由控制器的 SeasonalCards.Remaining.ViewAllStores 策略把关。</summary>
    public interface ISeasonalCardStatsReactService
    {
        Task<ApiResponse<SeasonalCardStatsSummaryDto>> GetSummaryAsync(SeasonalCardStatsQueryDto query);
        Task<ApiResponse<SeasonalCardStatsStoreDetailDto>> GetStoreDetailAsync(
            string storeCode,
            SeasonalCardStatsStoreDetailQueryDto query
        );
    }
}
