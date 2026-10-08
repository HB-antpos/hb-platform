using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/seasonal-card-remaining")]
    [Authorize]
    public class SeasonalCardRemainingController : ControllerBase
    {
        private readonly ISeasonalCardRemainingReactService _service;
        private readonly ISeasonalCardStatsReactService _statsService;

        public SeasonalCardRemainingController(
            ISeasonalCardRemainingReactService service,
            ISeasonalCardStatsReactService statsService
        )
        {
            _service = service;
            _statsService = statsService;
        }

        [HttpGet("catalog")]
        [Authorize(Policy = Permissions.SeasonalCards.Remaining.SubmitManagedStore)]
        public async Task<IActionResult> GetCatalog() => Ok(await _service.GetCatalogAsync());

        [HttpPost("submissions")]
        [Authorize(Policy = Permissions.SeasonalCards.Remaining.SubmitManagedStore)]
        public async Task<IActionResult> CreateSubmission(
            [FromBody] CreateSeasonalCardRemainingSubmissionDto request
        ) => Ok(await _service.CreateSubmissionAsync(request));

        // 填报页总览与整组提交属于填报流程，和目录同用提交权限。
        [HttpGet("overview")]
        [Authorize(Policy = Permissions.SeasonalCards.Remaining.SubmitManagedStore)]
        public async Task<IActionResult> GetOverview([FromQuery] SeasonalCardOverviewQueryDto query) =>
            Ok(await _service.GetOverviewAsync(query));

        [HttpPost("submissions/batch")]
        [Authorize(Policy = Permissions.SeasonalCards.Remaining.SubmitManagedStore)]
        public async Task<IActionResult> CreateBatch(
            [FromBody] CreateSeasonalCardRemainingBatchDto request
        ) => Ok(await _service.CreateBatchAsync(request));

        [HttpGet("submissions")]
        [Authorize(Policy = Permissions.SeasonalCards.Remaining.ViewManagedStore)]
        public async Task<IActionResult> GetSubmissions(
            [FromQuery] SeasonalCardRemainingSubmissionQueryDto query
        ) => Ok(await _service.GetSubmissionsAsync(query));

        [HttpGet("submissions/{submissionGuid}")]
        [Authorize(Policy = Permissions.SeasonalCards.Remaining.ViewManagedStore)]
        public async Task<IActionResult> GetSubmission(string submissionGuid) =>
            Ok(await _service.GetSubmissionByGuidAsync(submissionGuid));

        // 后台分店填报统计：全部分店口径，只认独立权限码（管理员隐含）。
        [HttpGet("admin/summary")]
        [Authorize(Policy = Permissions.SeasonalCards.Remaining.ViewAllStores)]
        public async Task<IActionResult> GetAdminSummary([FromQuery] SeasonalCardStatsQueryDto query) =>
            Ok(await _statsService.GetSummaryAsync(query));

        [HttpGet("admin/stores/{storeCode}")]
        [Authorize(Policy = Permissions.SeasonalCards.Remaining.ViewAllStores)]
        public async Task<IActionResult> GetAdminStoreDetail(
            string storeCode,
            [FromQuery] SeasonalCardStatsStoreDetailQueryDto query
        ) => Ok(await _statsService.GetStoreDetailAsync(storeCode, query));
    }
}
