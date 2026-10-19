using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Shared.Helper;
using BlazorApp.Shared.Models;
using BlazorApp.Shared.Models.HqEntities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/hq-products/translate-names")]
    [Authorize]
    public class ReactHqProductTranslationController : ControllerBase
    {
        private readonly IHqProductTranslationReactService _service;
        private readonly ILogger<ReactHqProductTranslationController> _logger;
        private readonly HqSqlSugarContext _hq;
        private readonly SqlSugarContext _local;

        public ReactHqProductTranslationController(
            IHqProductTranslationReactService service,
            ILogger<ReactHqProductTranslationController> logger,
            HqSqlSugarContext hq,
            SqlSugarContext local
        )
        {
            _service = service;
            _logger = logger;
            _hq = hq;
            _local = local;
        }

        public class TranslateRequest
        {
            public string Scope { get; set; } = "byContainers";
            public List<string>? ContainerGuids { get; set; }
            public bool OverwriteExisting { get; set; } = false;
        }

        [HttpPost]
        [Authorize(Roles = "Admin,WarehouseManager,WarehouseStaff")]
        public async Task<IActionResult> Translate([FromBody] TranslateRequest request)
        {
            try
            {
                if (string.Equals(request.Scope, "all", StringComparison.OrdinalIgnoreCase))
                {
                    var res = await _service.TranslateNamesAllAsync(request.OverwriteExisting);
                    return Ok(new { success = true, data = res });
                }

                var guids = request.ContainerGuids ?? new List<string>();
                var result = await _service.TranslateNamesByContainersAsync(
                    guids,
                    request.OverwriteExisting
                );
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量翻译英文名失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        public class TranslateByNumberRequest
        {
            public List<string> ContainerNumbers { get; set; } = new();

            /// <summary>
            /// 本地货柜 GUID（Container.ContainerCode）：后端据此读取真实货柜编号再匹配 HQ。
            /// 前端拿到的货柜编号可能是遮罩值（无「查看完整货柜编号」权限），所以优先传 GUID。
            /// </summary>
            public List<string>? ContainerGuids { get; set; }
            public bool OverwriteExisting { get; set; } = false;
        }

        [HttpPost("by-container-number")]
        [Authorize(Roles = "Admin,WarehouseManager,WarehouseStaff")]
        public async Task<IActionResult> TranslateByContainerNumber(
            [FromBody] TranslateByNumberRequest request
        )
        {
            try
            {
                var containerGuids = (request.ContainerGuids ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // 遮罩值（含 *）匹配不到 HQ，直接丢弃，避免把遮罩值当查询条件。
                var containerNumbers = (request.ContainerNumbers ?? new List<string>())
                    .Where(value => !string.IsNullOrWhiteSpace(value) && !ContainerNumberMasking.IsMasked(value))
                    .Select(value => value.Trim())
                    .ToList();
                if (containerGuids.Count > 0)
                {
                    var localNumbers = await _local
                        .Db.Queryable<Container>()
                        .Where(c => containerGuids.Contains(c.ContainerCode) && !c.IsDeleted)
                        .Select(c => c.ContainerNumber)
                        .ToListAsync();
                    containerNumbers.AddRange(
                        localNumbers
                            .Where(value => !string.IsNullOrWhiteSpace(value))
                            .Select(value => value!.Trim())
                    );
                }

                containerNumbers = containerNumbers.Distinct(StringComparer.Ordinal).ToList();
                if (containerNumbers.Count == 0)
                {
                    return BadRequest(new { success = false, message = "货柜编号列表不能为空" });
                }

                var hguids = await _hq
                    .Db.Queryable<CPT_RED_货柜单主表Store>()
                    .Where(x => x.货柜编号 != null && containerNumbers.Contains(x.货柜编号))
                    .Select(x => x.HGUID)
                    .ToListAsync();

                var validGuids = hguids
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => g!)
                    .Distinct()
                    .ToList();

                if (!validGuids.Any())
                {
                    return NotFound(new { success = false, message = "未找到对应的主表GUID" });
                }

                var result = await _service.TranslateNamesByContainersAsync(
                    validGuids,
                    request.OverwriteExisting
                );
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按货柜编号批量翻译失败");
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }

        [HttpPost("by-container-number/{containerNumber}")]
        [Authorize(Roles = "Admin,WarehouseManager,WarehouseStaff")]
        public async Task<IActionResult> TranslateByContainerNumberSingle(
            string containerNumber,
            [FromQuery] bool overwriteExisting = false
        )
        {
            try
            {
                if (string.IsNullOrWhiteSpace(containerNumber))
                {
                    return BadRequest(new { success = false, message = "货柜编号不能为空" });
                }

                var guid = await _hq
                    .Db.Queryable<CPT_RED_货柜单主表Store>()
                    .Where(x => x.货柜编号 == containerNumber)
                    .Select(x => x.HGUID)
                    .FirstAsync();

                if (string.IsNullOrWhiteSpace(guid))
                {
                    return NotFound(new { success = false, message = "未找到对应的主表GUID" });
                }

                var result = await _service.TranslateNamesByContainersAsync(
                    new List<string> { guid },
                    overwriteExisting
                );
                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按货柜编号翻译失败: {ContainerNumber}", containerNumber);
                return StatusCode(500, new { success = false, message = "服务器内部错误" });
            }
        }
    }
}
