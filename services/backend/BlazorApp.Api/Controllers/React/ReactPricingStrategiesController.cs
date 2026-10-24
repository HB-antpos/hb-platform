using System.Threading.Tasks;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Services.Pricing;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlazorApp.Api.Controllers.React
{
    [ApiController]
    [Route("api/react/v1/pricing-strategies")]
    [Authorize]
    public class ReactPricingStrategiesController : ControllerBase
    {
        private readonly IPricingStrategyReactService _service;
        private readonly IAutoPricingService _autoPricing;

        public ReactPricingStrategiesController(
            IPricingStrategyReactService service,
            IAutoPricingService autoPricing
        )
        {
            _service = service;
            _autoPricing = autoPricing;
        }

        // 定价策略直接影响分店售价，读写分别按 PricingStrategy.View / Edit 授权，不再仅依赖登录态。
        [HttpPost("grid")]
        [Authorize(Policy = Permissions.PricingStrategy.View)]
        public async Task<ActionResult<GridResponseDto<PricingStrategyListDto>>> Grid(
            [FromBody] GridRequestDto request
        )
        {
            var res = await _service.GetGridAsync(request);
            return Ok(res);
        }

        [HttpGet("{id}")]
        [Authorize(Policy = Permissions.PricingStrategy.View)]
        public async Task<ActionResult<ApiResponse<PricingStrategyDetailDto>>> Get(string id)
        {
            var res = await _service.GetByIdAsync(id);
            return Ok(res);
        }

        [HttpPost]
        [Authorize(Policy = Permissions.PricingStrategy.Edit)]
        public async Task<ActionResult<ApiResponse<PricingStrategyDetailDto>>> Create(
            [FromBody] CreatePricingStrategyDto dto
        )
        {
            var res = await _service.CreateAsync(dto);
            return Ok(res);
        }

        [HttpPut("{id}")]
        [Authorize(Policy = Permissions.PricingStrategy.Edit)]
        public async Task<ActionResult<ApiResponse<PricingStrategyDetailDto>>> Update(
            string id,
            [FromBody] UpdatePricingStrategyDto dto
        )
        {
            var res = await _service.UpdateAsync(id, dto);
            return Ok(res);
        }

        [HttpDelete("{id}")]
        [Authorize(Policy = Permissions.PricingStrategy.Edit)]
        public async Task<ActionResult<ApiResponse<bool>>> Delete(string id)
        {
            var res = await _service.DeleteAsync(id);
            return Ok(res);
        }

        [HttpPost("evaluate")]
        [Authorize(Policy = Permissions.PricingStrategy.View)]
        public async Task<ActionResult<ApiResponse<PricingEvaluateResponse>>> Evaluate(
            [FromBody] PricingEvaluateRequest req
        )
        {
            if (req.PurchasePrice <= 0)
            {
                return Ok(ApiResponse<PricingEvaluateResponse>.Error("进货价必须大于0"));
            }

            var strategy = await _autoPricing.FindStrategyForPriceAsync(
                req.PurchasePrice,
                req.SupplierCode,
                req.StoreCode
            );
            decimal retail;
            decimal rate;
            try
            {
                retail = _autoPricing.CalculateRetailPrice(req.PurchasePrice, strategy);
                rate = _autoPricing.CalculateRate(req.PurchasePrice, strategy);
            }
            catch (ArgumentException ex)
            {
                return Ok(ApiResponse<PricingEvaluateResponse>.Error(ex.Message));
            }

            PricingEvaluateRuleInfo? ruleInfo = null;
            if (strategy?.Details != null)
            {
                var rule = strategy.Details.FirstOrDefault(d =>
                    req.PurchasePrice >= d.MinPrice && req.PurchasePrice <= d.MaxPrice
                );
                if (rule != null)
                {
                    ruleInfo = new PricingEvaluateRuleInfo
                    {
                        MinPrice = rule.MinPrice,
                        MaxPrice = rule.MaxPrice,
                        Algorithm = rule.Algorithm,
                        StartRate = rule.StartRate,
                        EndRate = rule.EndRate,
                        StartRetailPrice = rule.StartRetailPrice,
                        EndRetailPrice = rule.EndRetailPrice,
                        CurveBend = rule.CurveBend,
                    };
                }
            }

            var resp = new PricingEvaluateResponse
            {
                RetailPrice = retail,
                Rate = rate,
                EffectiveRate = retail / req.PurchasePrice,
                StrategyId = strategy?.Id,
                StrategyName = strategy?.Name,
                Rule = ruleInfo,
            };
            return Ok(ApiResponse<PricingEvaluateResponse>.OK(resp));
        }
    }
}
