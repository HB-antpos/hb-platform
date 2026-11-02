import {
  getPriceChangeStats,
  getPurchasePriceChange,
  getPurchasePriceChangeRatio,
  isPurchasePriceDown,
  isPurchasePriceUp,
} from "./price-change";
import { assertDeepEqual, assertEqual } from "./test-helpers";

const change = (last: number | null | undefined, current: number | null | undefined) =>
  getPurchasePriceChange({ lastPurchasePrice: last as number | null, purchasePrice: current as number | null });

// 无本次进货价 -> none；上次价空或 0 -> noHistory
assertEqual(change(1, null).kind, "none", "无进货价为 none");
assertEqual(change(1, undefined).kind, "none", "undefined 进货价为 none");
assertEqual(change(null, null).kind, "none", "两者都空时 none 优先");
assertEqual(change(null, 5).kind, "noHistory", "上次价空为 noHistory");
assertEqual(change(0, 5).kind, "noHistory", "上次价 0 为 noHistory");
assertEqual(change(undefined, 5).kind, "noHistory", "上次价 undefined 为 noHistory");
assertEqual(change(0, 5).ratio, null, "noHistory 无涨跌幅");
assertEqual(change(null, null).last, null, "none 且无上次价时 last 为 null");

// 持平：差值 < 0.00005
assertDeepEqual(change(10, 10), { kind: "flat", ratio: 0, level: null, last: 10 }, "完全相同为 flat");
assertEqual(change(10, 10.00004).kind, "flat", "差值 0.00004 仍为 flat");
assertEqual(change(10, 10.0001).kind, "up", "差值达到 0.0001 不再算持平");
assertEqual(change(10, 9.99996).kind, "flat", "降价微小差额也为 flat");

// 涨幅分档：>20% strong、>5% normal、其余 light（边界不含）
assertEqual(change(10, 12.01).level, "strong", "涨 20.1% 为 strong");
assertEqual(change(10, 12).level, "normal", "涨恰好 20% 不算 strong");
assertEqual(change(10, 10.51).level, "normal", "涨 5.1% 为 normal");
assertEqual(change(10, 10.5).level, "light", "涨恰好 5% 不算 normal（沿用 Web 的 (current-last)/last 公式）");
assertEqual(change(10, 10.1).level, "light", "涨 1% 为 light");
assertEqual(change(10, 10.1).kind, "up", "涨价 kind");

// 降价无分档，ratio 为负
const down = change(10, 8);
assertEqual(down.kind, "down", "降价 kind");
assertEqual(down.level, null, "降价没有分档");
assertEqual(Math.round((down.ratio as number) * 1000) / 1000, -0.2, "降价 ratio 为负");
assertEqual(down.last, 10, "last 带出上次价");
assertEqual(Math.round((change(1.94, 0.44).ratio as number) * 1000) / 1000, -0.773, "实际样例降价幅度");

// getPurchasePriceChangeRatio：搬自 Web batchExecuteConfirm.test.ts
assertEqual(getPurchasePriceChangeRatio(0, 5), null, "上次价为 0 无可比价");
assertEqual(getPurchasePriceChangeRatio(-1, 5), null, "上次价为负无可比价");
assertEqual(getPurchasePriceChangeRatio(undefined, 5), null, "上次价缺失无可比价");
assertEqual(getPurchasePriceChangeRatio(null, 5), null, "上次价 null 无可比价");
assertEqual(getPurchasePriceChangeRatio(10, undefined), null, "本次价缺失无可比价");
assertEqual(getPurchasePriceChangeRatio(10, Number.NaN), null, "本次价 NaN 无可比价");
assertEqual(getPurchasePriceChangeRatio(10, 14), 14 / 10 - 1, "按 本次/上次-1 计算");
assertEqual(getPurchasePriceChangeRatio(10, 6) as number < 0, true, "降价为负");

// 涨/降价判定（筛选口径）：上次价必须 > 0，严格大小比较
const price = (last: number | null, current: number | null) => ({ lastPurchasePrice: last, purchasePrice: current });
assertEqual(isPurchasePriceUp(price(1, 1.01)), true, "涨价");
assertEqual(isPurchasePriceUp(price(1, 1)), false, "持平不是涨价");
assertEqual(isPurchasePriceUp(price(0, 5)), false, "上次价 0 不参与涨跌");
assertEqual(isPurchasePriceUp(price(null, 5)), false, "上次价空不参与涨跌");
assertEqual(isPurchasePriceUp(price(1, null)), false, "本次价空不参与涨跌");
assertEqual(isPurchasePriceDown(price(1, 0.99)), true, "降价");
assertEqual(isPurchasePriceDown(price(1, 0)), true, "本次价 0 视为降价（与 Web 一致）");
assertEqual(isPurchasePriceDown(price(1, 1)), false, "持平不是降价");
assertEqual(isPurchasePriceDown(price(-1, -2)), false, "上次价为负不参与涨跌");

assertDeepEqual(
  getPriceChangeStats([price(1, 2), price(2, 1), price(1, 1), price(0, 3), price(null, 1), price(1, null), price(1, 3)]),
  { upCount: 2, downCount: 1 },
  "涨跌统计"
);
