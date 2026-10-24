import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const currentDir = dirname(fileURLToPath(import.meta.url));
const read = (relative: string) => readFileSync(resolve(currentDir, relative), "utf8");

const tag = read("./SalesOrderStatusTag.tsx");
const view = read("./SalesOrdersView.tsx");
const detail = read("./SalesOrderDetailSheet.tsx");
const filterSheet = read("./SalesOrderFilterSheet.tsx");

// 颜色只在一处定义：部分退款橙、整单退款红；标签、卡片、详情、筛选档位共用。
assert.match(tag, /partial: \{ background: "#FDEBD8", color: HB_COLORS\.warning \}/, "部分退款用橙色");
assert.match(tag, /full: \{ background: "#FCEBEB", color: HB_COLORS\.danger \}/, "整单退款用红色");
assert.match(tag, /key === "refunded" && refundTone \? REFUND_TONES\[refundTone\]/, "「已退款」状态标签随整单金额变色");
assert.match(tag, /export function SalesOrderRefundTag/, "非已退款状态含退款商品时有附加标签");

// 卡片：状态标签与整单金额都按同一个着色结果显示；已退款状态不重复加附加标签
assert.match(view, /const refundTone = resolveSalesOrderRefundTone\(item\)/, "卡片按整单金额算着色");
assert.match(view, /refundTone && item\.status !== SALES_ORDER_REFUND_TYPE \? <SalesOrderRefundTag/, "已退款状态只显示一个标签");
assert.match(view, /REFUND_TONES\[refundTone\]\.color/, "卡片金额用同一套颜色");
assert.match(view, /status\.refunded"\)\} · \$\{t\(`refund\.\$\{filters\.refundTier\}`\)/, "筛选 chip 显示已退款档位");

// 详情：整单金额（实付）同色
assert.match(detail, /const refundTone = order \? resolveSalesOrderRefundTone\(order\) : null/, "详情按整单金额算着色");
assert.match(detail, /color=\{refundTone \? REFUND_TONES\[refundTone\]\.color : undefined\}/, "详情整单金额着色");

// 筛选面板：只有选了「已退款」才出现档位，档位 chip 与卡片同色；应用时非已退款类型一律回到 all
assert.match(filterSheet, /orderType === SALES_ORDER_REFUND_TYPE \? \(/, "只有已退款类型才显示档位");
assert.match(filterSheet, /REFUND_TONES\[tier\]/, "档位 chip 与卡片同色");
assert.match(
  filterSheet,
  /refundTier: orderType === SALES_ORDER_REFUND_TYPE \? refundTier : "all"/,
  "换成其它订单类型时不带残留档位",
);

console.log("sales-order-refund-tone-contract.test.ts: ok");
