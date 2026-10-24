using SqlSugar;

namespace BlazorApp.Shared.Models.HBweb
{
    [SugarTable("PricingStrategyDetail")]
    public class PricingStrategyDetail
    {
        [SugarColumn(IsPrimaryKey = true)]
        public string Id { get; set; } = string.Empty;

        public string StrategyId { get; set; } = string.Empty;

        [SugarColumn(DecimalDigits = 4)]
        public decimal MinPrice { get; set; }

        [SugarColumn(DecimalDigits = 4)]
        public decimal MaxPrice { get; set; }

        [SugarColumn(DecimalDigits = 4)]
        public decimal StartRate { get; set; }

        [SugarColumn(DecimalDigits = 4)]
        public decimal EndRate { get; set; }

        [SugarColumn(Length = 18, DecimalDigits = 2, IsNullable = true)]
        public decimal? StartRetailPrice { get; set; }

        [SugarColumn(Length = 18, DecimalDigits = 2, IsNullable = true)]
        public decimal? EndRetailPrice { get; set; }

        [SugarColumn(Length = 18, DecimalDigits = 6, IsNullable = true)]
        public decimal? CurveBend { get; set; }

        /// <summary>
        /// Linear, ArcUp, ArcDown, Exponential, Step, Fixed
        /// </summary>
        [SugarColumn(Length = 20)]
        public string Algorithm { get; set; } = "Linear";
    }
}
