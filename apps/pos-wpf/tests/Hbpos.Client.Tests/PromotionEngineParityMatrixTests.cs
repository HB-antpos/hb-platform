using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Hbpos.Contracts.Promotions;
using Xunit.Abstractions;

namespace Hbpos.Client.Tests;

// 新旧自动促销引擎「逐格对照」矩阵。
//
// 背景：同一个折扣字段（CartLine 的 Promotion 来源折扣）目前有两个写入方：
//   旧引擎 = PosCartService.RefreshAutomaticPromotionDiscounts（规则来自目录同步的 CatalogPromotionRuleDto，购物车内同步重算）；
//   新引擎 = PromotionEvaluationService + PosCartService.ApplyPromotionDiscounts（规则来自 /promotions/rules，由 VM 排队异步重算）。
// 本文件把同一份中立规则/购物车描述分别喂给两套引擎，逐行钉住两边的输出。
//
// 标注约定：
//   EQ-*   = 等价格：两套引擎输出完全相同。
//   DIFF-* = 已知差异格：两套引擎本来就不同，钉的是各自「当前」输出；
//            在用户拍板选哪一边之前，不得为了让测试通过而改生产语义。拍板并收敛后，应把对应格改成 EQ 并更新期望值。
//
// 语义收敛记录（本 PR，不删旧引擎、不切换规则来源）：
//   D1 分摊到分：旧引擎改用新算法（共享 PromotionBundleAllocator）-> EQ-D1*；
//   D2 互斥判定范围：旧引擎只评估与购物车相关的规则 -> EQ-D2*；
//   D4 亚分单价：组原价按「行」舍入一次（新引擎改为与购物车行原价同一口径），凑组实收恰为规则目标价 -> EQ-D4*；
//   D5 负固定价：新引擎也整条忽略 -> EQ-D5*。
//   仍未决：D3 叠加封顶（DIFF-D3*）；S1 总部促销是规则源差异，不在本矩阵里（见 Hbpos.Api.Tests 的 PromotionRuleSourceParityTests）。
public sealed class PromotionEngineParityMatrixTests(ITestOutputHelper output)
{
    // 现配置同构 = 单条规则 + 互斥 + 单店；扩展配置 = 多条/非互斥/叠加/特殊价格等。
    private const string Prod = "现配置同构";
    private const string Ext = "扩展配置";

    private sealed record Cell(
        string Id,
        string Config,
        string Title,
        ParityRule[] Rules,
        ParityLine[] Lines,
        decimal[] Legacy,
        decimal[] Unified,
        bool SameTotalDiscount)
    {
        public bool IsEquivalent => Legacy.SequenceEqual(Unified);
    }

    private static readonly Cell[] Cells = BuildCells();

    public static TheoryData<string> CellIds()
    {
        var data = new TheoryData<string>();
        foreach (var cell in Cells)
        {
            data.Add(cell.Id);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CellIds))]
    public async Task Matrix_cell_pins_current_discounts_of_both_engines(string id)
    {
        var cell = Cells.Single(candidate => candidate.Id == id);

        var legacy = PromotionEngineParityHarness.RunLegacy(cell.Rules, cell.Lines);
        var legacyRulesLast = PromotionEngineParityHarness.RunLegacy(cell.Rules, cell.Lines, rulesFirst: false);
        var unified = await PromotionEngineParityHarness.RunUnifiedAsync(cell.Rules, cell.Lines);

        Assert.Equal(cell.Legacy, legacy.Discounts);
        Assert.Equal(cell.Unified, unified.Discounts);

        // 标注必须与事实一致：EQ 格两边逐行折扣、来源、小计、总额全部相同；DIFF 格必须真的不同。
        Assert.Equal(cell.IsEquivalent, id.StartsWith("EQ-", StringComparison.Ordinal));
        Assert.Equal(cell.IsEquivalent, legacy.SameAs(unified));
        Assert.Equal(cell.SameTotalDiscount, legacy.DiscountAmount == unified.DiscountAmount);

        // 旧引擎「先挂规则再加购（逐次编辑重算）」与「先加购再挂规则（目录重载后重算）」必须落到同一结果。
        Assert.True(legacy.SameAs(legacyRulesLast), $"legacy rules-first {legacy} != rules-last {legacyRulesLast}");

        // 金额恒等式：行实收 = 行原价 - 行折扣；总额同理。两套引擎都不能破坏它。
        foreach (var outcome in new[] { legacy, unified })
        {
            Assert.All(outcome.Lines, line => Assert.Equal(line.Gross - line.Discount, line.Actual));
            Assert.Equal(outcome.TotalAmount - outcome.DiscountAmount, outcome.ActualAmount);
        }
    }

    private static Cell[] BuildCells()
    {
        var cells = new List<Cell>();

        void Add(
            string id,
            string config,
            string title,
            ParityRule[] rules,
            ParityLine[] lines,
            decimal[] legacy,
            decimal[]? unified = null,
            bool sameTotalDiscount = true)
        {
            cells.Add(new Cell(id, config, title, rules, lines, legacy, unified ?? legacy, sameTotalDiscount));
        }

        // ---------------- A. 互斥规则 ----------------
        var prod2For15 = new[] { ParityRule.Of("PROMO-PROD", 2, 15m, ["A"], isExclusive: true) };
        Add("EQ-A1-single-exclusive-same-line", Prod, "单条互斥 2 件 15 元，同一行 2 件", prod2For15,
            [new("A", 10m, 2)], [5.00m]);
        Add("EQ-A1b-single-exclusive-two-lines", Prod, "同一商品分两行各 1 件（不同 lookup），折扣均分", prod2For15,
            [new("A", 10m), new("A", 10m)], [2.50m, 2.50m]);

        Add("EQ-A2-two-exclusive-higher-priority-wins", Ext, "两条互斥规则，只取优先级高的",
            [ParityRule.Of("LOW", 2, 18m, ["A"], isExclusive: true, priority: 1),
             ParityRule.Of("HIGH", 2, 15m, ["A"], isExclusive: true, priority: 5)],
            [new("A", 10m, 2)], [5.00m]);
        Add("EQ-A3-exclusive-beats-non-exclusive-when-both-match-cart", Ext, "互斥与非互斥规则都命中购物车：只取互斥",
            [ParityRule.Of("EX", 2, 15m, ["A"], isExclusive: true, priority: 1),
             ParityRule.Of("NX", 2, 12m, ["A"], isExclusive: false, priority: 9)],
            [new("A", 10m, 2)], [5.00m]);
        Add("EQ-A4-unrelated-exclusive-alone-gives-no-discount", Ext, "只有一条与购物车无关的互斥规则：无折扣",
            [ParityRule.Of("EX", 2, 15m, ["Z"], isExclusive: true, priority: 1)],
            [new("A", 10m, 2)], [0m]);
        Add("EQ-A5-two-non-exclusive-different-products", Ext, "两条非互斥规则各管一个商品，独立生效",
            [ParityRule.Of("N1", 2, 15m, ["A"], priority: 1), ParityRule.Of("N2", 2, 15m, ["B"], priority: 2)],
            [new("A", 10m, 2), new("B", 10m, 2)], [5.00m, 5.00m]);
        Add("EQ-A6-two-non-exclusive-same-product-stack-within-gross", Ext, "两条非互斥规则作用同一行，叠加不超过行原价",
            [ParityRule.Of("N1", 2, 15m, ["A"], priority: 20), ParityRule.Of("N2", 2, 16m, ["A"], priority: 10)],
            [new("A", 10m, 2)], [9.00m]);
        Add("EQ-A7-two-non-exclusive-stack-to-free", Ext, "两条非互斥规则叠加到免费（每行各自封顶在原价）",
            [ParityRule.Of("N1", 2, 5m, ["A", "B"], priority: 20), ParityRule.Of("N2", 2, 5m, ["A", "B"], priority: 10)],
            [new("A", 10m), new("B", 10m)], [10.00m, 10.00m]);
        // 叠加封顶里「谁先谁后」：新引擎按优先级顺序逐条消耗行容量，所以换个优先级顺序可以与旧引擎重合；
        // 旧引擎先累加后截断，与规则顺序无关（对照 DIFF-D3：优先级互换后结果就不同）。
        Add("EQ-A7b-stacked-cap-when-bundle-rule-runs-first", Ext, "同 DIFF-D3 但 N2(A+B 凑 2 件 1.00)优先级更高先跑：新引擎先分 A/B 各 1.50，再被 N1 补满 A，结果与旧引擎相同",
            [ParityRule.Of("N1", 1, 0m, ["A"], priority: 1), ParityRule.Of("N2", 2, 1m, ["A", "B"], priority: 2)],
            [new("A", 2m), new("B", 2m)], [2.00m, 1.50m]);
        Add("EQ-A8-equal-priority-exclusive-tie-broken-by-id", Ext, "两条互斥规则优先级并列：按规则 Id 升序取第一条",
            [ParityRule.Of("X-2", 2, 12m, ["A"], isExclusive: true, priority: 3),
             ParityRule.Of("X-1", 2, 15m, ["A"], isExclusive: true, priority: 3)],
            [new("A", 10m, 2)], [5.00m]);
        Add("EQ-A9-expired-exclusive-does-not-suppress-active-non-exclusive", Ext, "已过期的互斥规则不压制仍生效的非互斥规则",
            [ParityRule.Of("EX", 2, 15m, ["A"], isExclusive: true, startDaysFromNow: -3, endDaysFromNow: -1),
             ParityRule.Of("NX", 2, 16m, ["A"])],
            [new("A", 10m, 2)], [4.00m]);

        // ---------------- B. 数量阶梯（3 件 25 元，每件 10 元；边界：临界数量前后、整倍数前后） ----------------
        var tier = new[] { ParityRule.Of("TIER", 3, 25m, ["A"], isExclusive: true) };
        var tierExpected = new Dictionary<int, decimal>
        {
            [1] = 0m, [2] = 0m, [3] = 5m, [4] = 5m, [5] = 5m, [6] = 10m, [7] = 10m
        };
        foreach (var (quantity, discount) in tierExpected)
        {
            Add($"EQ-B1-tier-3for25-quantity-{quantity}", Prod, $"3 件 25 元，数量 {quantity}", tier,
                [new("A", 10m, quantity)], [discount]);
        }

        Add("EQ-B2-max-applications-1-with-quantity-6", Prod, "每单最多 1 次：数量 6 只凑一组",
            [ParityRule.Of("MAX", 3, 25m, ["A"], isExclusive: true, maxApplicationsPerOrder: 1)],
            [new("A", 10m, 6)], [5.00m]);

        var weighted = new[] { new ParityRule("WEIGHT", 3, 25m, [("A", 2), ("B", 1)], true) };
        Add("EQ-B3a-weight-below-threshold-single", Ext, "权重 A=2、凑 3 件：A×1 只占 2 个单位，不成组", weighted,
            [new("A", 10m)], [0m]);
        Add("EQ-B3b-weight-below-threshold-two-lines", Ext, "权重 A=2、凑 3 件：A×1 + B×1 展开 3 个单位成 1 组，但实物原价合计 20 < 固定价 25，无折扣", weighted,
            [new("A", 10m), new("B", 10m)], [0m, 0m]);
        Add("EQ-B3c-weight-two-units-no-bundle", Ext, "权重 A=2、凑 3 件：A×2 展开 4 个单位成 1 组，实物原价 20 < 25，无折扣（权重只用来凑数，不放大原价）", weighted,
            [new("A", 10m, 2)], [0m]);
        Add("EQ-B3d-weight-bundle-spans-two-lines", Ext, "权重 A=2、凑 3 件：A×2 + B×2 的第二组实物原价 30，折 5.00 按原价比例分摊 1.67 / 3.33", weighted,
            [new("A", 10m, 2), new("B", 10m, 2)], [1.67m, 3.33m]);

        // ---------------- C. 手工改价 / 手工折扣 / 目录折扣 / 开放商品 / 退货 ----------------
        var pair = new[] { ParityRule.Of("PAIR", 2, 15m, ["A", "B"], isExclusive: true) };
        Add("EQ-C1-manual-price-line-participates-at-manual-price", Prod, "手工改价行参与促销，按改后单价 8 元计", pair,
            [new("A", 10m, 1, ParityLineKind.ManualPrice, 8m), new("B", 10m)], [1.33m, 1.67m]);
        Add("EQ-C2a-manual-discount-amount-line-excluded", Prod, "行手工折扣金额：该行不参与，另一行凑不成组", pair,
            [new("A", 10m, 1, ParityLineKind.ManualDiscountAmount, 2m), new("B", 10m)], [2.00m, 0m]);
        Add("EQ-C2b-manual-discount-percent-line-excluded", Prod, "行手工折扣百分比：同上", pair,
            [new("A", 10m, 1, ParityLineKind.ManualDiscountPercent, 10m), new("B", 10m)], [1.00m, 0m]);
        Add("EQ-C3-catalog-discount-line-excluded", Prod, "目录折扣行不与促销叠加，也不参与成组", pair,
            [new("A", 10m, 1, ParityLineKind.CatalogDiscount, 0.2m), new("B", 10m)], [2.00m, 0m]);
        Add("EQ-C4-open-item-and-return-lines-excluded", Prod, "开放商品行与退货行不参与；两个普通 B 行照常成组", pair,
            [new("A", 10m, 1, ParityLineKind.OpenItem), new("B", 10m), new("A", 10m, 1, ParityLineKind.Return), new("B", 10m)],
            [0m, 2.50m, 0m, 2.50m]);

        // ---------------- F. 生效时间 ----------------
        Add("EQ-F1-expired-rule", Prod, "已过期（昨天结束）的规则", [ParityRule.Of("OLD", 2, 15m, ["A"], isExclusive: true, startDaysFromNow: -3, endDaysFromNow: -1)],
            [new("A", 10m, 2)], [0m]);
        Add("EQ-F2-not-yet-effective-rule", Prod, "未生效（明天开始）的规则", [ParityRule.Of("FUTURE", 2, 15m, ["A"], isExclusive: true, startDaysFromNow: 1, endDaysFromNow: 3)],
            [new("A", 10m, 2)], [0m]);

        // ---------------- G. 折扣为 0 / 封顶 / 取整 ----------------
        Add("EQ-G1a-fixed-price-equals-bundle-total", Prod, "固定价等于组内原价：折扣 0", [ParityRule.Of("P", 2, 20m, ["A"], isExclusive: true)],
            [new("A", 10m, 2)], [0m]);
        Add("EQ-G1b-fixed-price-above-bundle-total", Prod, "固定价高于组内原价：不加价、折扣 0", [ParityRule.Of("P", 2, 25m, ["A"], isExclusive: true)],
            [new("A", 10m, 2)], [0m]);
        Add("EQ-G2-fixed-price-zero-makes-bundle-free", Prod, "固定价 0：整组免费，折扣封顶在行原价", [ParityRule.Of("P", 2, 0m, ["A", "B"], isExclusive: true)],
            [new("A", 10m), new("B", 3m)], [10.00m, 3.00m]);
        Add("EQ-G3-odd-cents-divide-evenly", Prod, "3 行 3.33 元凑 3 件 9.00：折扣 0.99 均分 0.33", [ParityRule.Of("P", 3, 9m, ["A", "B", "C"], isExclusive: true)],
            [new("A", 3.33m), new("B", 3.33m), new("C", 3.33m)], [0.33m, 0.33m, 0.33m]);
        Add("EQ-G5-apply-quantity-1-single-item-price", Prod, "1 件 8 元（单件特价）：每件折 2 元", [ParityRule.Of("P", 1, 8m, ["A"], isExclusive: true)],
            [new("A", 10m, 3)], [6.00m]);
        Add("EQ-G7a-apply-quantity-zero-rule-ignored", Ext, "ApplyQuantity=0 的规则被忽略", [ParityRule.Of("P", 0, 1m, ["A"], isExclusive: true)],
            [new("A", 10m, 2)], [0m]);
        Add("EQ-G7b-rule-without-products-ignored", Ext, "没有商品明细的规则被忽略", [ParityRule.Of("P", 2, 1m, [], isExclusive: true)],
            [new("A", 10m, 2)], [0m]);
        Add("EQ-H1-product-code-case-and-whitespace-insensitive", Prod, "规则商品码带空格/大小写不同仍命中", [ParityRule.Of("P", 2, 15m, [" a "], isExclusive: true)],
            [new("A", 10m, 2)], [5.00m]);

        // ======================= 原差异 D1/D2/D4/D5：已收敛（旧引擎改用新算法，新引擎补齐过滤/亚分口径）=======================

        // D1 分摊到分：同一组跨多行时，逐行分摊用「新算法」——按行合并后，用递减的剩余折扣/剩余原价逐行取整，最后一行吃余数。
        //    旧引擎原先按「每个实物单位」用整组原价做分母一次性分摊（A 0.06 / B 0.04），现已改走共享的 PromotionBundleAllocator。
        //    现配置同构下就会出现，总折扣一直相同，收敛的只是逐行差几分。
        Add("EQ-D1a-cents-allocation-two-lines-3for2.90", Prod, "3 件 2.90，A(1.00)×2 + B(1.00)×1：折扣 0.10 在两行间分摊（原：旧 0.06/0.04，新 0.07/0.03）",
            [ParityRule.Of("P", 3, 2.90m, ["A", "B"], isExclusive: true)],
            [new("A", 1m, 2), new("B", 1m)],
            [0.07m, 0.03m]);
        Add("EQ-D1b-cents-allocation-three-single-lines", Prod, "3 件 2.90，三行各 1 件 1.00：0.10 分摊 0.03/0.04/0.03（原：旧 0.03/0.03/0.04）",
            [ParityRule.Of("P", 3, 2.90m, ["A", "B", "C"], isExclusive: true)],
            [new("A", 1m), new("B", 1m), new("C", 1m)],
            [0.03m, 0.04m, 0.03m]);

        // D2 互斥判定范围：只在「与购物车相关的规则」之间判互斥（相关 = 规则里至少有一个商品对应可参与促销的购物车行）。
        //    旧引擎原先「只要有任何生效的互斥规则（哪怕与购物车商品无关）就只评估那一条」，会把真正命中的非互斥规则整条压住；
        //    现已与新引擎、iPad 的 pricing-cart、共享挂单重演一致。
        Add("EQ-D2-unrelated-exclusive-does-not-suppress-non-exclusive", Ext, "互斥规则(商品 Z)与购物车无关 + 非互斥规则(商品 A)命中：两边都 5.00（原：旧=0）",
            [ParityRule.Of("EX", 2, 15m, ["Z"], isExclusive: true, priority: 1),
             ParityRule.Of("NX", 2, 15m, ["A"], isExclusive: false, priority: 9)],
            [new("A", 10m, 2)],
            [5.00m]);
        Add("EQ-D2b-unrelated-higher-priority-exclusive-does-not-beat-related-exclusive", Ext, "两条互斥：高优先级的(商品 Z)与购物车无关，低优先级的(商品 A)命中：取 A 那条",
            [ParityRule.Of("EX-Z", 2, 15m, ["Z"], isExclusive: true, priority: 9),
             ParityRule.Of("EX-A", 2, 15m, ["A"], isExclusive: true, priority: 1)],
            [new("A", 10m, 2)],
            [5.00m]);
        Add("EQ-D2c-exclusive-whose-only-product-line-is-ineligible-is-not-cart-related", Ext, "互斥规则(商品 A)的唯一购物车行带手工折扣不可参与 -> 该规则不算与购物车相关，非互斥规则(商品 B)照常生效",
            [ParityRule.Of("EX", 2, 15m, ["A"], isExclusive: true, priority: 9),
             ParityRule.Of("NX", 2, 15m, ["B"], isExclusive: false, priority: 1)],
            [new("A", 10m, 1, ParityLineKind.ManualDiscountAmount, 2m), new("B", 10m, 2)],
            [2.00m, 5.00m]);

        // D4 亚分单价：组原价按「行」舍入到分一次（单价不先舍入），与购物车行原价 CartLine.GrossAmount 同一口径，
        //    因此单行成组时凑组实收恰为规则目标价。3.335×3 凑 3 件 9.00：行原价 10.01，折 1.01，实收 9.00。
        //    新引擎原先把每个单位先舍入到分再求和（3.34×3 = 10.02），多折 1 分、实收 8.99。
        Add("EQ-D4a-sub-cent-unit-price-single-line", Ext, "单价 3.335×3 凑 3 件 9.00：两边折 1.01、实收 9.00（原：新折 1.02、实收 8.99）",
            [ParityRule.Of("P", 3, 9m, ["A"], isExclusive: true)],
            [new("A", 3.335m, 3)],
            [1.01m]);
        Add("EQ-D4b-sub-cent-unit-price-two-lines", Ext, "单价 3.335 两行凑 2 件 5.00：两行原价各 3.34，折 1.68 均分 0.84/0.84、实收 5.00（原：旧总折 1.67、实收 5.01）",
            [ParityRule.Of("P", 2, 5m, ["A", "B"], isExclusive: true)],
            [new("A", 3.335m), new("B", 3.335m)],
            [0.84m, 0.84m]);
        Add("EQ-D4c-sub-cent-unit-price-line-spans-two-bundles", Ext, "单价 3.335×6 凑 3 件 9.00：两组各折 1.01，行原价整行一次舍入 20.01，实收 17.99（既有行为：多组各自舍入，与目标价差 1 分）",
            [ParityRule.Of("P", 3, 9m, ["A"], isExclusive: true)],
            [new("A", 3.335m, 6)],
            [2.02m]);

        // D5 负固定价：整条规则忽略（不参与互斥选择、不产生折扣）。
        //    旧引擎的规则过滤原本就要求 FixedPrice >= 0（目录源服务端也过滤，iPad 契约校验同样要求 >= 0）；
        //    新引擎原先不检查 -> 整组被折成 0 元。后台是否可能保存负固定价未核实（服务端 PromotionReactService 未见校验）。
        Add("EQ-D5-negative-fixed-price-rule-ignored", Ext, "固定价 -1：两边都忽略该规则（原：新整组免费）",
            [ParityRule.Of("NEG", 2, -1m, ["A"], isExclusive: true)],
            [new("A", 10m, 2)],
            [0m]);
        Add("EQ-D5b-negative-exclusive-rule-does-not-suppress-others", Ext, "负固定价的互斥高优先级规则不压制其它规则：忽略 NEG，NX 照常生效",
            [ParityRule.Of("NEG", 2, -1m, ["A"], isExclusive: true, priority: 9),
             ParityRule.Of("NX", 2, 15m, ["A"], isExclusive: false, priority: 1)],
            [new("A", 10m, 2)],
            [5.00m]);

        // ======================= 已知差异格（两套引擎本来就不同，未决，等用户拍板）=======================

        // D3 叠加封顶：多条非互斥规则叠加超过行原价时，旧引擎先累加再逐行截断（超出部分丢弃）；
        //    新引擎逐条规则按行剩余容量封顶，并把同组里被封顶行的份额重新分给还有容量的行。
        Add("DIFF-D3-stacked-rules-cap-redistribution", Ext, "N1: A 免费(1 件 0 元)；N2: A+B 2 件 1.00。A 被 N1 免到 0 后，N2 的份额旧=丢弃、新=转给 B",
            [ParityRule.Of("N1", 1, 0m, ["A"], priority: 2), ParityRule.Of("N2", 2, 1m, ["A", "B"], priority: 1)],
            [new("A", 2m), new("B", 2m)],
            [2.00m, 1.50m], [2.00m, 2.00m], sameTotalDiscount: false);

        // D3b 同样的封顶差异也会出现在「单条规则 + UnitWeight>1」：一件实物按权重展开后落进多个组，
        //     同一行累计折扣超过行原价。2 件 3.00，A 权重 3：A@10 的折扣旧=10.00 封顶后多出的份额丢弃，新=转给 B。
        //     （生产那条促销的 UnitWeight 具体值未核实。）
        Add("DIFF-D3b-single-rule-unit-weight-above-one-cap-redistribution", Ext, "2 件 3.00，A 权重 3、B 权重 1，A@10 + B@2：B 折扣旧 1.50 / 新 2.00",
            [new ParityRule("W", 2, 3m, [("A", 3), ("B", 1)], true)],
            [new("A", 10m), new("B", 2m)],
            [10.00m, 1.50m], [10.00m, 2.00m], sameTotalDiscount: false);

        return cells.ToArray();
    }

    // ===================================================================================
    // 触发时机与覆盖顺序（远程复核 / 目录重载 / 无旧规则的购物车）
    // ===================================================================================

    private static readonly ParityRule ThreeFor290 =
        ParityRule.Of("P", 3, 2.90m, ["A", "B"], isExclusive: true);

    [Fact]
    public async Task Remote_review_recompute_by_legacy_no_longer_changes_cent_allocation_of_unified_discounts()
    {
        // 生产里两套引擎同时在线：购物车已挂旧规则 -> 每次编辑先由旧引擎同步算一遍，
        // VM 随后排队新引擎重算并覆盖；后台远程复核（PosTerminalWorkflowService -> UpdateLineFromRemote）
        // 只会再触发旧引擎，且 VM 不会为它排队新引擎。
        // D1 收敛前：旧引擎分摊到分与新引擎不同（0.06/0.04 vs 0.07/0.03），远程复核会把新值覆盖回旧值；
        // D1 收敛后：两边同一份分摊算法，远程复核重算出来的逐行折扣与新引擎相同，不再翻来覆去。
        var now = DateTimeOffset.UtcNow;
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules([ThreeFor290.ToLegacy(now)]);
        var lines = PromotionEngineParityHarness.AddLines(cart, [new("A", 1m, 2, Lookup: "LK-A"), new("B", 1m, Lookup: "LK-B")]);

        Assert.Equal([0.07m, 0.03m], PromotionEngineParityHarness.Capture(cart).Discounts);

        await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, [ThreeFor290], now);
        Assert.Equal([0.07m, 0.03m], PromotionEngineParityHarness.Capture(cart).Discounts);

        Assert.True(cart.UpdateLineFromRemote(
            lines[0],
            PromotionEngineParityHarness.CreateItem("A", "LK-A", 1m)));
        Assert.Equal([0.07m, 0.03m], PromotionEngineParityHarness.Capture(cart).Discounts);
    }

    [Fact]
    public async Task Remote_review_keeps_unified_discounts_when_cart_has_no_legacy_rules()
    {
        var now = DateTimeOffset.UtcNow;
        var cart = new PosCartService();
        var lines = PromotionEngineParityHarness.AddLines(cart, [new("A", 1m, 2, Lookup: "LK-A"), new("B", 1m, Lookup: "LK-B")]);
        await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, [ThreeFor290], now);

        Assert.True(cart.UpdateLineFromRemote(
            lines[0],
            PromotionEngineParityHarness.CreateItem("A", "LK-A", 1m)));

        Assert.Equal([0.07m, 0.03m], PromotionEngineParityHarness.Capture(cart).Discounts);
    }

    [Fact]
    public async Task Remote_review_price_change_is_recomputed_by_legacy_but_leaves_unified_discount_stale()
    {
        // 收敛时的隐患：删掉旧引擎后，远程复核改了单价，促销折扣金额不会自动重算（行内只按新毛额截断），
        // 必须额外补一次新引擎重算。VM 目前没有为远程复核排队重算。
        var now = DateTimeOffset.UtcNow;
        var twoFor15 = ParityRule.Of("P", 2, 15m, ["A"], isExclusive: true);

        var withLegacy = new PosCartService();
        withLegacy.SetAutomaticPromotionRules([twoFor15.ToLegacy(now)]);
        var legacyLine = PromotionEngineParityHarness.AddLines(withLegacy, [new("A", 10m, 2, Lookup: "LK-A")])[0];
        Assert.Equal(5.00m, legacyLine.DiscountAmount);
        Assert.True(withLegacy.UpdateLineFromRemote(legacyLine, PromotionEngineParityHarness.CreateItem("A", "LK-A", 12m)));
        Assert.Equal(9.00m, legacyLine.DiscountAmount);

        var unifiedOnly = new PosCartService();
        var unifiedLine = PromotionEngineParityHarness.AddLines(unifiedOnly, [new("A", 10m, 2, Lookup: "LK-A")])[0];
        await PromotionEngineParityHarness.ApplyUnifiedAsync(unifiedOnly, [twoFor15], now);
        Assert.Equal(5.00m, unifiedLine.DiscountAmount);
        Assert.True(unifiedOnly.UpdateLineFromRemote(unifiedLine, PromotionEngineParityHarness.CreateItem("A", "LK-A", 12m)));
        Assert.Equal(5.00m, unifiedLine.DiscountAmount);

        await PromotionEngineParityHarness.ApplyUnifiedAsync(unifiedOnly, [twoFor15], now);
        Assert.Equal(9.00m, unifiedLine.DiscountAmount);
    }

    [Fact]
    public async Task Shared_held_order_mapper_accepts_the_cart_whichever_engine_wrote_the_cent_allocation()
    {
        // 第三个消费者：共享挂单 SharedHeldOrderMapper 用「冻结规则」重演，要求每行折扣精确相等，
        // 否则 PromotionRulesMismatch 阻断发布（保留本地）。冻结规则取自购物车里的旧规则集（CreateFrozenAutomaticPromotionRules）。
        // D1 收敛前：旧引擎分摊到分（0.03/0.03/0.04）与重演（0.03/0.04/0.03）对不上 -> 阻断，
        //           远程复核让旧引擎覆盖新引擎结果后，这单又发布不到共享挂单；
        // D1 收敛后：旧引擎、新引擎、重演用同一份分摊算法，购物车上无论是谁写的折扣都能精确对应。
        var now = DateTimeOffset.UtcNow;
        var threeSingles = ParityRule.Of("P", 3, 2.90m, ["A", "B", "C"], isExclusive: true);
        var cart = new PosCartService();
        cart.SetAutomaticPromotionRules([threeSingles.ToLegacy(now)]);
        var lines = PromotionEngineParityHarness.AddLines(
            cart,
            [new("A", 1m, Lookup: "LK-A"), new("B", 1m, Lookup: "LK-B"), new("C", 1m, Lookup: "LK-C")]);
        var mapper = new SharedHeldOrderMapper();

        // 旧引擎刚算完：0.03/0.04/0.03，精确对应。
        Assert.Equal([0.03m, 0.04m, 0.03m], PromotionEngineParityHarness.Capture(cart).Discounts);
        var legacyResult = mapper.Map(ToSuspendedOrder(cart), cart.CreateFrozenAutomaticPromotionRules(), revision: 1);
        Assert.False(legacyResult.IsBlocked);

        // 新引擎覆盖后：同样精确对应。
        await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, [threeSingles], now);
        var unifiedResult = mapper.Map(ToSuspendedOrder(cart), cart.CreateFrozenAutomaticPromotionRules(), revision: 1);
        Assert.False(unifiedResult.IsBlocked);

        // 远程复核又让旧引擎覆盖回去：不再阻断。
        Assert.True(cart.UpdateLineFromRemote(lines[0], PromotionEngineParityHarness.CreateItem("A", "LK-A", 1m)));
        var overwrittenResult = mapper.Map(ToSuspendedOrder(cart), cart.CreateFrozenAutomaticPromotionRules(), revision: 1);
        Assert.False(overwrittenResult.IsBlocked);
    }

    [Fact]
    public void Shared_held_order_mapper_still_blocks_a_cart_holding_the_old_per_unit_cent_allocation()
    {
        // 精确对应的要求没有放松：老版本留在挂单/购物车里的逐单位分摊（0.03/0.03/0.04）与重演的 0.03/0.04/0.03 对不上，照旧阻断。
        var now = DateTimeOffset.UtcNow;
        var threeSingles = ParityRule.Of("P", 3, 2.90m, ["A", "B", "C"], isExclusive: true);
        var cart = new PosCartService();
        var lines = PromotionEngineParityHarness.AddLines(cart, [new("A", 1m), new("B", 1m), new("C", 1m)]);
        lines[0].SetPromotionDiscountAmount(0.03m);
        lines[1].SetPromotionDiscountAmount(0.03m);
        lines[2].SetPromotionDiscountAmount(0.04m);

        var result = new SharedHeldOrderMapper().Map(ToSuspendedOrder(cart), [threeSingles.ToLegacy(now)], revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Equal(SharedHeldOrderMappingReasons.PromotionRulesMismatch, result.Block!.Reason);
    }

    [Theory]
    [MemberData(nameof(CellIds))]
    public async Task Shared_held_order_mapper_accepts_the_unified_engine_result_of_every_matrix_cell(string id)
    {
        // 共享挂单重演必须与新引擎逐行一致：矩阵里每个格子，新引擎算出的购物车都能被重演精确对应（含 D3 叠加封顶格）。
        // 重演只认整分单价、不看生效时间、拒绝开放商品/退货行，所以这三类格子不适用。
        var cell = Cells.Single(candidate => candidate.Id == id);
        var applicable =
            cell.Lines.All(line => line.Kind is not (ParityLineKind.OpenItem or ParityLineKind.Return)) &&
            cell.Lines.All(line => line.Price * 100m == decimal.Truncate(line.Price * 100m)) &&
            cell.Rules.All(rule => rule.StartDaysFromNow <= 0 && rule.EndDaysFromNow >= 0);
        if (!applicable)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cart = new PosCartService();
        PromotionEngineParityHarness.AddLines(cart, cell.Lines);
        await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, cell.Rules, now);

        var result = new SharedHeldOrderMapper().Map(
            ToSuspendedOrder(cart),
            cell.Rules.Select(rule => rule.ToLegacy(now)).ToArray(),
            revision: 1);

        Assert.False(result.IsBlocked, result.Block?.Detail);
        var mappedCents = result.Payload!.PricingState.Lines
            .Select(line => line.DiscountState.Mode == "promotion" ? line.DiscountState.Cents!.Value : 0L)
            .ToArray();
        // 手工折扣行的折扣不是促销，不计入；其余行的促销折扣分必须与购物车一致。
        var expectedCents = cart.Lines
            .Select(line => line.DiscountSource == CartLineDiscountSource.Promotion ? (long)(line.DiscountAmount * 100m) : 0L)
            .ToArray();
        Assert.Equal(expectedCents, mappedCents);
    }

    [Fact]
    public async Task Shared_held_order_mapper_blocks_sub_cent_priced_cart_that_hits_a_promotion()
    {
        // D4 的副作用（只在零售价/手工价带亚分位时出现）：购物车按原价 3.335 算，3 件凑 9.00 折 1.01；
        // 共享挂单 canonical 只有整数分单价（3.335 -> 3.34），重演按 3.34×3 = 10.02 得 1.02，对不上 -> 阻断，这单保留在本地。
        // 阻断（fail-closed）比发布一份「单价 3.34×3 却只折 1.01、实收 9.01」的数据更安全。
        var now = DateTimeOffset.UtcNow;
        var rule = ParityRule.Of("P", 3, 9m, ["A"], isExclusive: true);
        var cart = new PosCartService();
        var line = PromotionEngineParityHarness.AddLines(cart, [new("A", 3.335m, 3)])[0];
        await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, [rule], now);
        Assert.Equal(1.01m, line.DiscountAmount);

        var result = new SharedHeldOrderMapper().Map(ToSuspendedOrder(cart), [rule.ToLegacy(now)], revision: 1);

        Assert.True(result.IsBlocked);
        Assert.Equal(SharedHeldOrderMappingReasons.PromotionRulesMismatch, result.Block!.Reason);
    }

    private static SuspendedOrder ToSuspendedOrder(PosCartService cart)
    {
        var orderGuid = Guid.NewGuid();
        var snapshot = cart.CreateSnapshot();
        var lines = snapshot.Lines
            .Select(line => new SuspendedOrderLine(
                Guid.NewGuid(),
                orderGuid,
                line.StoreCode,
                line.ProductCode,
                line.ReferenceCode,
                line.DisplayName,
                line.LookupCode,
                line.ItemNumber,
                line.ProductImage,
                line.Quantity,
                line.UnitPrice,
                line.DiscountAmount,
                line.DiscountPercent,
                line.Quantity * line.UnitPrice - line.DiscountAmount,
                line.PriceSource,
                line.PriceSourceLabel,
                line.DiscountSource)
            {
                Kind = line.Kind,
                IsManualPrice = line.IsManualPrice,
                CatalogDiscountBasisPoints = line.CatalogDiscountBasisPoints
            })
            .ToArray();
        return new SuspendedOrder(
            orderGuid,
            PromotionEngineParityHarness.StoreCode,
            "POS-01",
            "cashier-1",
            "Cashier One",
            DateTimeOffset.UtcNow,
            cart.TotalAmount,
            cart.DiscountAmount,
            cart.ActualAmount,
            SuspendedOrderStatus.Pending,
            lines);
    }

    [Fact]
    public async Task Cart_without_legacy_rules_never_applies_promotions_by_itself()
    {
        // 删除旧引擎的后果：购物车自己的任何编辑（加购/改数量/删除/其它页面如特价商品页的加购）都不再产生促销，
        // 只有 PosTerminalViewModel 排队的新引擎重算才会。
        var now = DateTimeOffset.UtcNow;
        var twoFor15 = ParityRule.Of("P", 2, 15m, ["A"], isExclusive: true);
        var cart = new PosCartService();
        var line = PromotionEngineParityHarness.AddLines(cart, [new("A", 10m, 1, Lookup: "LK-A")])[0];

        Assert.True(cart.SetLineQuantity(line, 2m));
        Assert.Equal(0m, line.DiscountAmount);

        await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, [twoFor15], now);
        Assert.Equal(5.00m, line.DiscountAmount);

        Assert.True(cart.SetLineQuantity(line, 1m));
        // 数量降回 1：行内只把旧折扣金额截断到新毛额，不会重判「已不满足成组条件」。
        Assert.Equal(5.00m, line.DiscountAmount);
        await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, [twoFor15], now);
        Assert.Equal(0m, line.DiscountAmount);
    }

    [Fact]
    public void Catalog_reload_with_legacy_rules_recomputes_even_if_unified_did_not_run()
    {
        // 目录重载（ShellCatalogService.ApplyPromotionRules -> SetAutomaticPromotionRules）只触发旧引擎。
        var twoFor15 = ParityRule.Of("P", 2, 15m, ["A"], isExclusive: true);
        var cart = new PosCartService();
        var line = PromotionEngineParityHarness.AddLines(cart, [new("A", 10m, 2, Lookup: "LK-A")])[0];
        Assert.Equal(0m, line.DiscountAmount);

        cart.SetAutomaticPromotionRules([twoFor15.ToLegacy(DateTimeOffset.UtcNow)]);
        Assert.Equal(5.00m, line.DiscountAmount);

        // 规则被撤销（空集合）后旧引擎清掉自己写的促销折扣；新引擎没有这条路径。
        cart.SetAutomaticPromotionRules([]);
        Assert.Equal(0m, line.DiscountAmount);
    }

    // ===================================================================================
    // 冻结折扣（共享挂单取单）/ 小数数量 / 预算
    // ===================================================================================

    [Fact]
    public async Task Frozen_shared_snapshot_promotion_survives_catalog_reload_but_not_unified_apply()
    {
        // 共享挂单取单：促销折扣按快照冻结，旧引擎在目录重载时不重算，直到首次用户编辑才释放；
        // 新引擎的 ApplyPromotionDiscounts 不认冻结，会按当前规则覆盖。
        // VM 不会在取单后（编辑之前）排队新引擎，所以这个差异当前不可达，但收敛时要保留「首次编辑才释放」语义。
        var snapshot = new PosCartSnapshot(
        [
            new PosCartLineSnapshot(
                "S001", "A", null, "Item A", "LK-A", "A", null,
                2m, 10m, 7.00m, null,
                PriceSourceKind.StoreRetailPrice, PriceSourceKind.StoreRetailPrice.ToString(),
                DiscountSource: PosCartLineDiscountSource.Promotion)
        ],
        SharedHeldOrderClaimId: Guid.NewGuid());
        var twoFor15 = ParityRule.Of("P", 2, 15m, ["A"], isExclusive: true);

        var cart = new PosCartService();
        cart.RestoreSharedSaleSnapshot(snapshot);
        var line = Assert.Single(cart.Lines);
        Assert.Equal(7.00m, line.DiscountAmount);

        cart.SetAutomaticPromotionRules([twoFor15.ToLegacy(DateTimeOffset.UtcNow)]);
        Assert.Equal(7.00m, line.DiscountAmount);

        await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, [twoFor15], DateTimeOffset.UtcNow);
        Assert.Equal(5.00m, line.DiscountAmount);
    }

    [Fact]
    public async Task Non_integer_quantity_line_is_excluded_by_both_engines()
    {
        // 小数数量只可能来自共享挂单取单（RestoreSharedSaleSnapshot）。两套引擎都只处理正整数数量行。
        var twoFor15 = ParityRule.Of("P", 2, 15m, ["A"], isExclusive: true);
        PosCartSnapshot Snapshot() => new(
        [
            new PosCartLineSnapshot(
                "S001", "A", null, "Item A", "LK-A", "A", null,
                2.5m, 10m, 0m, null,
                PriceSourceKind.StoreRetailPrice, PriceSourceKind.StoreRetailPrice.ToString())
        ],
        SharedHeldOrderClaimId: Guid.NewGuid());

        var legacyCart = new PosCartService();
        legacyCart.SetAutomaticPromotionRules([twoFor15.ToLegacy(DateTimeOffset.UtcNow)]);
        legacyCart.RestoreSharedSaleSnapshot(Snapshot());
        // 追加一个无关商品，触发一次旧引擎重算（同时释放共享快照保护）。
        legacyCart.AddItem(PromotionEngineParityHarness.CreateItem("Z", "LK-Z", 1m));

        var unifiedCart = new PosCartService();
        unifiedCart.RestoreSharedSaleSnapshot(Snapshot());
        unifiedCart.AddItem(PromotionEngineParityHarness.CreateItem("Z", "LK-Z", 1m));
        await PromotionEngineParityHarness.ApplyUnifiedAsync(unifiedCart, [twoFor15], DateTimeOffset.UtcNow);

        Assert.Equal(0m, legacyCart.Lines[0].DiscountAmount);
        Assert.Equal(0m, unifiedCart.Lines[0].DiscountAmount);
        Assert.Equal(2.5m, legacyCart.Lines[0].Quantity);
    }

    [Fact]
    public async Task Budget_exceeded_ends_with_no_promotion_discount_in_both_engines()
    {
        // 旧引擎：购物车内捕获并清空促销折扣，购物车继续可售；
        // 新引擎：EvaluateAsync 抛 PromotionComputationBudgetExceededException，由 VM 调 ApplyPromotionDiscounts([]) 清空并提示。
        var rule = ParityRule.Of("P", 1, 5m, ["A"], isExclusive: true);
        var overBudget = PromotionComputationBudget.MaxWorkUnitsPerRule + 1;

        var legacyCart = new PosCartService();
        var legacyLine = PromotionEngineParityHarness.AddLines(legacyCart, [new("A", 10m, 1, Lookup: "LK-A")])[0];
        legacyCart.SetAutomaticPromotionRules([rule.ToLegacy(DateTimeOffset.UtcNow)]);
        Assert.True(legacyLine.IsAutomaticPromotionDiscount);
        Assert.True(legacyCart.SetLineQuantity(legacyLine, overBudget));
        Assert.False(legacyLine.IsAutomaticPromotionDiscount);
        Assert.Equal(0m, legacyLine.DiscountAmount);

        var unifiedCart = new PosCartService();
        var unifiedLine = PromotionEngineParityHarness.AddLines(unifiedCart, [new("A", 10m, 1, Lookup: "LK-A")])[0];
        await PromotionEngineParityHarness.ApplyUnifiedAsync(unifiedCart, [rule], DateTimeOffset.UtcNow);
        Assert.True(unifiedLine.IsAutomaticPromotionDiscount);
        Assert.True(unifiedCart.SetLineQuantity(unifiedLine, overBudget));
        await Assert.ThrowsAsync<PromotionComputationBudgetExceededException>(() =>
            PromotionEngineParityHarness.ApplyUnifiedAsync(unifiedCart, [rule], DateTimeOffset.UtcNow));
        unifiedCart.ApplyPromotionDiscounts([]);
        Assert.False(unifiedLine.IsAutomaticPromotionDiscount);
        Assert.Equal(0m, unifiedLine.DiscountAmount);
    }

    // ===================================================================================
    // 与生产同构的配置，走真实本地库（两套规则各自的 SQLite 表 + 各自的读取排序/缓存）
    // ===================================================================================

    [Fact]
    public async Task Production_shaped_config_through_real_local_repositories_matches_for_every_quantity_and_store()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-promo-parity-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var catalogRepository = new LocalCatalogRepository(store);
            var promotionRepository = new LocalPromotionRepository(store);
            var now = DateTimeOffset.UtcNow;

            // 与生产同构：1 条生效促销、互斥、只绑 S001 一家店；S002 没有任何促销。
            // 旧源（目录同步）与新源（/promotions/rules）各写各的表，按 LocalCatalogSyncService 的顺序。
            var rule = ParityRule.Of("PROMO-PROD", 3, 25m, ["SKU-A"], isExclusive: true, priority: 1);
            await catalogRepository.ReplacePromotionRulesAsync("S001", [rule.ToLegacy(now)]);
            await promotionRepository.ReplaceStoreRulesAsync(
                "S001",
                new PromotionRulesResponse("S001", now, [rule.ToUnified(now)]));

            var expectedBySingleLineQuantity = new Dictionary<int, decimal>
            {
                [1] = 0m, [2] = 0m, [3] = 5m, [4] = 5m, [5] = 5m, [6] = 10m, [7] = 10m
            };
            foreach (var storeCode in new[] { "S001", "S002" })
            {
                foreach (var (quantity, discountAtBoundStore) in expectedBySingleLineQuantity)
                {
                    var lines = new ParityLine[] { new("SKU-A", 10m, quantity) };
                    var expected = storeCode == "S001" ? discountAtBoundStore : 0m;

                    var legacyCart = new PosCartService();
                    legacyCart.SetAutomaticPromotionRules(await catalogRepository.LoadPromotionRulesAsync(storeCode));
                    PromotionEngineParityHarness.AddLines(legacyCart, lines);

                    var unifiedCart = new PosCartService();
                    PromotionEngineParityHarness.AddLines(unifiedCart, lines);
                    var discounts = await new PromotionEvaluationService(promotionRepository)
                        .EvaluateAsync(unifiedCart.Lines.ToArray(), storeCode, DateTimeOffset.UtcNow);
                    unifiedCart.ApplyPromotionDiscounts(discounts);

                    var legacy = PromotionEngineParityHarness.Capture(legacyCart);
                    var unified = PromotionEngineParityHarness.Capture(unifiedCart);
                    Assert.Equal([expected], legacy.Discounts);
                    Assert.True(legacy.SameAs(unified), $"store={storeCode} quantity={quantity}: legacy {legacy} != unified {unified}");
                }
            }
        }
        finally
        {
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task Rules_outside_their_effective_window_are_ignored_by_both_real_repositories()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"hbpos-promo-parity-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LocalSqliteStore(databasePath);
            await new LocalSchemaService(store).InitializeAsync();
            var catalogRepository = new LocalCatalogRepository(store);
            var promotionRepository = new LocalPromotionRepository(store);
            var now = DateTimeOffset.UtcNow;

            ParityRule[] rules =
            [
                ParityRule.Of("ACTIVE", 2, 15m, ["A"], priority: 1),
                ParityRule.Of("EXPIRED", 2, 1m, ["A"], priority: 9, startDaysFromNow: -5, endDaysFromNow: -1),
                ParityRule.Of("FUTURE", 2, 1m, ["A"], priority: 8, startDaysFromNow: 1, endDaysFromNow: 5)
            ];
            await catalogRepository.ReplacePromotionRulesAsync("S001", rules.Select(rule => rule.ToLegacy(now)).ToArray());
            await promotionRepository.ReplaceStoreRulesAsync(
                "S001",
                new PromotionRulesResponse("S001", now, rules.Select(rule => rule.ToUnified(now)).ToArray()));

            var legacyCart = new PosCartService();
            legacyCart.SetAutomaticPromotionRules(await catalogRepository.LoadPromotionRulesAsync("S001"));
            PromotionEngineParityHarness.AddLines(legacyCart, [new("A", 10m, 2)]);

            var unifiedCart = new PosCartService();
            PromotionEngineParityHarness.AddLines(unifiedCart, [new("A", 10m, 2)]);
            unifiedCart.ApplyPromotionDiscounts(await new PromotionEvaluationService(promotionRepository)
                .EvaluateAsync(unifiedCart.Lines.ToArray(), "S001", DateTimeOffset.UtcNow));

            // 只有 ACTIVE 生效：2 件 15 元 -> 折 5.00；过期/未生效的低价规则两边都不参与。
            Assert.Equal([5.00m], PromotionEngineParityHarness.Capture(legacyCart).Discounts);
            Assert.Equal([5.00m], PromotionEngineParityHarness.Capture(unifiedCart).Discounts);
        }
        finally
        {
            foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    // ===================================================================================
    // 固定种子的随机对照
    // ===================================================================================

    [Fact]
    public async Task Production_shaped_random_carts_are_identical_in_every_line_for_both_engines()
    {
        // 固定种子的随机对照：单条互斥规则（ApplyQuantity 1~4、固定价 0~40、商品 1~3 个、可选每单上限），
        // 购物车 1~5 个普通销售行（数量 1~5、单价取常见零售价），覆盖跨行成组与取整。
        // 历史（D1 收敛前，同一发生器、同一种子、10000 个购物车）：总折扣从无差异，但约 96% 两边逐行完全一致，
        // 其余只差分摊到分（逐行最大差 0.02）；D1 收敛后两边用同一份分摊算法，必须 100% 逐行一致。
        string[] products = ["A", "B", "C", "D"];
        decimal[] prices = [0.99m, 1.00m, 1.25m, 3.33m, 5m, 7.77m, 10m, 12.5m, 19.99m];
        var random = new Random(20261012);
        var identical = 0;
        var totalDiscountDiffers = 0;
        const int scenarioCount = 10000;
        for (var scenario = 0; scenario < scenarioCount; scenario++)
        {
            var ruleProducts = products.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).Select(code => (code, 1)).ToArray();
            ParityRule[] rules =
            [
                new ParityRule(
                    "PROMO-PROD",
                    random.Next(1, 5),
                    Math.Round(random.Next(0, 4000) / 100m, 2),
                    ruleProducts,
                    IsExclusive: true,
                    Priority: random.Next(0, 3),
                    MaxApplicationsPerOrder: random.Next(3) == 0 ? random.Next(1, 3) : null)
            ];
            var lines = Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => new ParityLine(products[random.Next(products.Length)], prices[random.Next(prices.Length)], random.Next(1, 6)))
                .ToArray();

            var legacy = PromotionEngineParityHarness.RunLegacy(rules, lines);
            var unified = await PromotionEngineParityHarness.RunUnifiedAsync(rules, lines);

            if (legacy.DiscountAmount != unified.DiscountAmount)
            {
                totalDiscountDiffers++;
            }

            if (legacy.SameAs(unified))
            {
                identical++;
            }
        }

        output.WriteLine($"production-shaped random carts: identical={identical}/{scenarioCount}, total-discount-differs={totalDiscountDiffers}");
        Assert.Equal(0, totalDiscountDiffers);
        Assert.Equal(scenarioCount, identical);
    }

    [Fact]
    public async Task Extended_random_carts_differ_only_where_stacked_rules_hit_a_fully_discounted_line()
    {
        // 扩展配置的随机对照：1~3 条规则（互斥/非互斥、优先级、权重 1~3、可选每单上限）、购物车里混入手工折扣行与目录折扣行，
        // 单价取整分价。收敛后两套引擎唯一还不同的是 D3 叠加封顶（旧=先累加再逐行截断，新=按行剩余容量封顶并转给还有容量的行），
        // 而 D3 只有在某行被折到 0 元（折扣 = 行原价）时才可能发生。所以：
        //   - 新引擎结果里没有任何促销行被折到 0 元 -> 两边必须逐行完全一致；
        //   - 否则允许不同（这就是 DIFF-D3 / DIFF-D3b 两格的差异），但方向固定：新引擎把被封顶的份额转给还有容量的行，
        //     所以新引擎的总折扣只会大于等于旧引擎、不会更少。
        var (differing, withoutFullyDiscountedLine, unifiedBelowLegacy) = await CountExtendedDifferencesAsync(20261013, 5000, subCentPrices: false);

        output.WriteLine($"extended random carts: differing={differing}/5000, differing-without-fully-discounted-line={withoutFullyDiscountedLine}, unified-total-below-legacy={unifiedBelowLegacy}");
        Assert.Equal(0, withoutFullyDiscountedLine);
        Assert.Equal(0, unifiedBelowLegacy);
    }

    [Fact]
    public async Task Sub_cent_random_carts_differ_only_where_stacked_rules_hit_a_fully_discounted_line()
    {
        // 同上，但单价带亚分位（0.995、3.335、12.345 ...）：D4 收敛后两边的组原价口径相同，不应再有分位差异。
        var (differing, withoutFullyDiscountedLine, unifiedBelowLegacy) = await CountExtendedDifferencesAsync(20261014, 5000, subCentPrices: true);

        output.WriteLine($"sub-cent random carts: differing={differing}/5000, differing-without-fully-discounted-line={withoutFullyDiscountedLine}, unified-total-below-legacy={unifiedBelowLegacy}");
        Assert.Equal(0, withoutFullyDiscountedLine);
        Assert.Equal(0, unifiedBelowLegacy);
    }

    private static async Task<(int Differing, int WithoutFullyDiscountedLine, int UnifiedBelowLegacy)> CountExtendedDifferencesAsync(
        int seed,
        int scenarioCount,
        bool subCentPrices)
    {
        var random = new Random(seed);
        var differing = 0;
        var withoutFullyDiscountedLine = 0;
        var unifiedBelowLegacy = 0;
        for (var scenario = 0; scenario < scenarioCount; scenario++)
        {
            var (rules, lines) = BuildExtendedScenario(random, subCentPrices);

            var legacy = PromotionEngineParityHarness.RunLegacy(rules, lines);
            var unified = await PromotionEngineParityHarness.RunUnifiedAsync(rules, lines);

            if (legacy.SameAs(unified))
            {
                continue;
            }

            differing++;
            if (unified.DiscountAmount < legacy.DiscountAmount)
            {
                unifiedBelowLegacy++;
            }

            if (!unified.Lines.Any(line => line.Source == (int)CartLineDiscountSource.Promotion && line.Gross > 0m && line.Discount == line.Gross))
            {
                withoutFullyDiscountedLine++;
            }
        }

        return (differing, withoutFullyDiscountedLine, unifiedBelowLegacy);
    }

    [Fact]
    public async Task Shared_held_order_mapper_accepts_the_unified_engine_result_for_random_carts()
    {
        // 共享挂单重演与新引擎的逐行一致性（含多规则叠加、权重、每单上限、D3 封顶）：新引擎算出的购物车，重演必须能精确对应。
        // 单价取整分价（canonical 只有整数分单价；亚分价的后果见 Shared_held_order_mapper_blocks_sub_cent_priced_cart_that_hits_a_promotion）。
        var random = new Random(20261015);
        var promoted = 0;
        const int scenarioCount = 3000;
        for (var scenario = 0; scenario < scenarioCount; scenario++)
        {
            var (rules, lines) = BuildExtendedScenario(random, subCentPrices: false);
            var now = DateTimeOffset.UtcNow;
            var cart = new PosCartService();
            PromotionEngineParityHarness.AddLines(cart, lines);
            await PromotionEngineParityHarness.ApplyUnifiedAsync(cart, rules, now);
            if (cart.Lines.Any(line => line.DiscountSource == CartLineDiscountSource.Promotion))
            {
                promoted++;
            }

            var result = new SharedHeldOrderMapper().Map(
                ToSuspendedOrder(cart),
                rules.Select(rule => rule.ToLegacy(now)).ToArray(),
                revision: 1);

            Assert.False(
                result.IsBlocked,
                $"scenario {scenario}: {result.Block?.Reason} {result.Block?.Detail} | cart: {PromotionEngineParityHarness.Capture(cart)}");
        }

        output.WriteLine($"mapper random carts: promoted={promoted}/{scenarioCount}");
        // 防止发生器退化成「都没有促销」导致本用例空转。
        Assert.True(promoted > scenarioCount / 4, $"promoted={promoted}/{scenarioCount}");
    }

    /// <summary>扩展配置发生器：多规则、权重、互斥/非互斥、手工折扣行与目录折扣行；subCentPrices=true 时单价带亚分位。</summary>
    private static (ParityRule[] Rules, ParityLine[] Lines) BuildExtendedScenario(Random random, bool subCentPrices)
    {
        string[] products = ["A", "B", "C", "D"];
        decimal[] prices = subCentPrices
            ? [0.995m, 1.005m, 3.335m, 7.775m, 12.345m, 0.99m, 5m, 10m]
            : [0.99m, 1.00m, 1.25m, 3.33m, 5m, 7.77m, 10m, 12.5m, 19.99m];
        var rules = Enumerable.Range(0, random.Next(1, 4))
            .Select(index => new ParityRule(
                $"R{index}",
                random.Next(1, 5),
                Math.Round(random.Next(0, 4000) / 100m, 2),
                products.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).Select(code => (code, random.Next(1, 4))).ToArray(),
                IsExclusive: random.Next(4) == 0,
                Priority: random.Next(0, 4),
                MaxApplicationsPerOrder: random.Next(4) == 0 ? random.Next(1, 3) : null))
            .ToArray();
        var lines = Enumerable.Range(0, random.Next(1, 6))
            .Select(_ =>
            {
                var product = products[random.Next(products.Length)];
                var price = prices[random.Next(prices.Length)];
                var quantity = random.Next(1, 6);
                return random.Next(10) switch
                {
                    0 => new ParityLine(product, price, quantity, ParityLineKind.ManualDiscountPercent, 10m),
                    1 => new ParityLine(product, price, quantity, ParityLineKind.CatalogDiscount, 0.1m),
                    _ => new ParityLine(product, price, quantity)
                };
            })
            .ToArray();
        return (rules, lines);
    }
}
