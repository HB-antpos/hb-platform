using BlazorApp.Api.Filters;
using BlazorApp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/hb-sales-record")]
    [Authorize]
    public class HBSalesRecordStatisticsController : ControllerBase
    {
        private readonly HBSalesRecordStatisticsService _service;
        private readonly ILogger<HBSalesRecordStatisticsController> _logger;

        public HBSalesRecordStatisticsController(
            HBSalesRecordStatisticsService service,
            ILogger<HBSalesRecordStatisticsController> logger
        )
        {
            _service = service;
            _logger = logger;
        }

        [HttpPost("import-2025")]
        [LegacyDatabaseDisabled(LegacyDatabaseKind.HBSales)]
        public async Task<IActionResult> ImportAndStatistics2025()
        {
            try
            {
                _logger.LogInformation("收到导入2025年HBSalesRecord数据的请求");

                var result = await _service.ImportAndStatistics2025Concurrent();

                return Ok(
                    new
                    {
                        success = result.Success,
                        message = result.Message,
                        data = new
                        {
                            taskId = result.TaskId,
                            totalDays = result.TotalDays,
                            processedDays = result.ProcessedDays,
                            failedDates = result.FailedDates,
                        },
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入2025年HBSalesRecord数据失败");
                return StatusCode(
                    500,
                    new { success = false, message = "导入失败: " + ex.Message }
                );
            }
        }

        [HttpGet("status")]
        [LegacyDatabaseDisabled(LegacyDatabaseKind.HBSales)]
        public IActionResult GetImportStatus()
        {
            try
            {
                _logger.LogInformation("收到获取导入状态的请求");

                return Ok(
                    new
                    {
                        success = true,
                        message = "导入状态查询成功",
                        data = new
                        {
                            description = "使用 POST /api/react/v1/hb-sales-record/import-2025 手动触发2025年数据导入",
                            status = "ready",
                        },
                    }
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取导入状态失败");
                return StatusCode(
                    500,
                    new { success = false, message = "查询失败: " + ex.Message }
                );
            }
        }
    }
}
