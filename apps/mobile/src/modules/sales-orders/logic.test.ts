import assert from "node:assert/strict";
import {
  SALES_ORDER_MAX_RANGE_DAYS,
  SALES_ORDER_PAGE_SIZE,
  buildDefaultSalesOrderFilters,
  buildSalesOrderPresetRange,
  buildSalesOrderQueryBody,
  canViewSalesOrders,
  countActiveSalesOrderFilters,
  formatLocalDate,
  formatSalesOrderGuidTail,
  hasMoreSalesOrderPages,
  matchSalesOrderRangePreset,
  resolveSalesOrderRefundTone,
  mergeSalesOrderPages,
  normalizeSelectedBranchCodes,
  resolveSalesOrderStatusKey,
  summarizeSalesOrderLines,
  toggleSortDirection,
  validateSalesOrderRange,
} from "./logic";
import type { SalesOrderListItem } from "./types";

assert.equal(SALES_ORDER_MAX_RANGE_DAYS, 30, "区间上限必须与后端 PosmSalesOrderMobileRules 一致");
assert.equal(SALES_ORDER_PAGE_SIZE, 20, "每页 20 条");

// 默认区间是今天，其余筛选为空
const defaults = buildDefaultSalesOrderFilters("2026-09-17");
assert.deepEqual(defaults, {
  range: { startDate: "2026-09-17", endDate: "2026-09-17" },
  branchCodes: [],
  orderType: -1,
  refundTier: "all",
  sortDirection: "desc",
});
assert.equal(countActiveSalesOrderFilters(defaults, "2026-09-17"), 0);
assert.equal(
  countActiveSalesOrderFilters(
    { ...defaults, branchCodes: ["S1"], orderType: 1, range: { startDate: "2026-09-10", endDate: "2026-09-17" } },
    "2026-09-17",
  ),
  3,
);

// 区间校验：含首尾 30 天放行，31 天拒绝并给出收敛起始日
assert.deepEqual(validateSalesOrderRange({ startDate: "2026-09-01", endDate: "2026-09-30" }), {
  ok: true,
  dayCount: 30,
});
assert.deepEqual(validateSalesOrderRange({ startDate: "2026-09-01", endDate: "2026-10-01" }), {
  ok: false,
  dayCount: 31,
  reason: "tooLong",
  clampedStartDate: "2026-09-02",
});
assert.equal(validateSalesOrderRange({ startDate: "2026-09-05", endDate: "2026-09-01" }).reason, "order");
assert.equal(validateSalesOrderRange({ startDate: "2026-02-30", endDate: "2026-03-01" }).reason, "format");
assert.equal(validateSalesOrderRange({ startDate: "2026/09/01", endDate: "2026-09-01" }).reason, "format");

// 预设：今天 = 1 天；近 7 天含首尾
assert.deepEqual(buildSalesOrderPresetRange("2026-09-17", 1), {
  startDate: "2026-09-17",
  endDate: "2026-09-17",
});
assert.deepEqual(buildSalesOrderPresetRange("2026-09-17", 7), {
  startDate: "2026-09-11",
  endDate: "2026-09-17",
});
assert.equal(matchSalesOrderRangePreset({ startDate: "2026-09-11", endDate: "2026-09-17" }), 7);
assert.equal(matchSalesOrderRangePreset({ startDate: "2026-09-10", endDate: "2026-09-17" }), null);

// 本地日期
assert.equal(formatLocalDate(new Date(2026, 8, 7)), "2026-09-07");

// 请求体：关键词去空白，空串按 null；页大小固定
const body = buildSalesOrderQueryBody(
  { ...defaults, branchCodes: ["S1"], orderType: 1, sortDirection: "asc" },
  "  HB1001 ",
  2,
);
assert.equal(body.keyword, "HB1001");
assert.equal(body.pageNumber, 2);
assert.equal(body.pageSize, 20);
assert.equal(body.sortDirection, "asc");
assert.equal(body.orderType, 1);
assert.deepEqual(body.branchCodes, ["S1"]);
assert.equal(buildSalesOrderQueryBody(defaults, "   ", 1).keyword, null);
assert.equal("clientUtcOffsetMinutes" in body, false, "订单时间按墙钟比较，请求体不得携带设备时区偏移");

// 「已退款」= 含退款商品的订单（任意状态），按档位筛选：订单类型发「全部」，范围交给 refundFilter
const refundBody = (orderType: -1 | 1 | 3, refundTier: "all" | "partial" | "full") =>
  buildSalesOrderQueryBody({ ...defaults, orderType, refundTier }, "", 1);
assert.deepEqual(
  [refundBody(3, "all").orderType, refundBody(3, "all").refundFilter],
  [-1, "any"],
  "已退款 + 全部档位 = 含退款商品的订单，订单类型不再限定状态=已退款",
);
assert.equal(refundBody(3, "partial").refundFilter, "partial");
assert.equal(refundBody(3, "full").refundFilter, "full");
assert.deepEqual(
  [refundBody(1, "full").orderType, refundBody(1, "full").refundFilter],
  [1, null],
  "非「已退款」类型忽略残留的档位，不发 refundFilter",
);
assert.equal(refundBody(-1, "all").refundFilter, null);
assert.equal(
  countActiveSalesOrderFilters({ ...defaults, orderType: 3, refundTier: "partial" }, "2026-09-17"),
  1,
  "选「已退款」及档位只算一项筛选",
);

// 含退款商品的着色：整单金额（实付，为 0 / 空回落总金额）小于 0 红色，其余橙色；不含退款商品不着色
const refundItem = (overrides: Partial<SalesOrderListItem>): SalesOrderListItem => ({
  orderGuid: "R",
  branchCode: null,
  branchName: null,
  deviceCode: null,
  orderTime: null,
  skuCount: null,
  itemCount: null,
  quantityTotal: null,
  totalAmount: null,
  discountAmount: null,
  actualAmount: null,
  status: 1,
  hasRefundItems: true,
  matchedProducts: [],
  ...overrides,
});
assert.equal(resolveSalesOrderRefundTone(refundItem({ actualAmount: 15.5, totalAmount: 15.5 })), "partial", "整单金额大于 0：部分退款（橙）");
assert.equal(resolveSalesOrderRefundTone(refundItem({ actualAmount: -12, totalAmount: -12 })), "full", "整单金额小于 0：整单退款（红）");
assert.equal(resolveSalesOrderRefundTone(refundItem({ actualAmount: 0, totalAmount: -4 })), "full", "实付为 0 时回落订单总金额");
assert.equal(resolveSalesOrderRefundTone(refundItem({ actualAmount: null, totalAmount: 7 })), "partial", "实付为空时回落订单总金额");
assert.equal(resolveSalesOrderRefundTone(refundItem({ actualAmount: 0, totalAmount: 0 })), "partial", "金额都为 0 不算整单退款，按部分退款（橙）");
assert.equal(resolveSalesOrderRefundTone(refundItem({ hasRefundItems: false, actualAmount: -5, totalAmount: -5 })), null, "不含退款商品不着色（负金额的普通单也不着色）");
assert.equal(
  resolveSalesOrderRefundTone(refundItem({ hasRefundItems: false, status: 3, actualAmount: -9, totalAmount: -9 })),
  "full",
  "退款单（状态=已退款）一定含退款商品，旧后端没返回 hasRefundItems 也要认出来",
);

// 分页判断与去重合并
assert.equal(hasMoreSalesOrderPages({ pageNumber: 1, pageSize: 20, total: 21 }), true);
assert.equal(hasMoreSalesOrderPages({ pageNumber: 2, pageSize: 20, total: 40 }), false);
const item = (orderGuid: string): SalesOrderListItem => ({
  orderGuid,
  branchCode: null,
  branchName: null,
  deviceCode: null,
  orderTime: null,
  skuCount: null,
  itemCount: null,
  quantityTotal: null,
  totalAmount: null,
  discountAmount: null,
  actualAmount: null,
  status: 1,
  hasRefundItems: false,
  matchedProducts: [],
});
assert.deepEqual(
  mergeSalesOrderPages([item("A"), item("B")], [item("B"), item("C")]).map((entry) => entry.orderGuid),
  ["A", "B", "C"],
  "翻页期间订单位移不得导致同一单重复出现",
);

// 分店归一：全选或空选都等价于不限
assert.deepEqual(normalizeSelectedBranchCodes(["S1", "S2"], ["S1", "S2"]), []);
assert.deepEqual(normalizeSelectedBranchCodes(["S2", "S9", "S2"], ["S1", "S2"]), ["S2"]);
assert.deepEqual(normalizeSelectedBranchCodes([], ["S1"]), []);

// 其他展示辅助
assert.equal(formatSalesOrderGuidTail("4f2a1c9e-1234-5678-9abc-0000a3f91c"), "…A3F91C");
assert.equal(formatSalesOrderGuidTail("abc"), "ABC");
assert.equal(resolveSalesOrderStatusKey(4), "installment");
assert.equal(resolveSalesOrderStatusKey(99), "unknown");
assert.equal(resolveSalesOrderStatusKey(null), "unknown");
assert.equal(toggleSortDirection("desc"), "asc");
assert.equal(toggleSortDirection("asc"), "desc");

// 权限：只认独立的 SalesOrders.View，Web 收银记录页的 Orders.View 不放行，审核模式一律拒绝
assert.equal(canViewSalesOrders(true, (permission) => permission === "SalesOrders.View", false), true);
assert.equal(canViewSalesOrders(true, (permission) => permission === "Orders.View", false), false);
assert.equal(canViewSalesOrders(true, () => true, true), false);
assert.equal(canViewSalesOrders(false, () => true, false), false);

// 详情明细统计：同一货号多行只算一个 SKU，件数求和
const line = (productCode: string | null, quantity: number | null) => ({
  productCode,
  itemNumber: null,
  productName: null,
  productImage: null,
  quantity,
  unitPrice: null,
  discountAmount: null,
  actualAmount: null,
});
assert.deepEqual(
  summarizeSalesOrderLines([line("HB1", 2), line("HB1", 1), line("HB2", 3), line(null, 1)]),
  { skuCount: 3, itemCount: 7 },
);
assert.deepEqual(summarizeSalesOrderLines([]), { skuCount: 0, itemCount: 0 });

console.log("sales-orders logic tests passed");
