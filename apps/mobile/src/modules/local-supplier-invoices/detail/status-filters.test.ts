import {
  applyDetailFilters,
  countActiveMoreFilterGroups,
  createEmptyDetailFilters,
  getBarcodeStatusFilter,
  getDetailFilterCounts,
  getProductStatusFilter,
  getProductTypeFilter,
  hasActiveDetailFilters,
  isBarcodeStatusAbnormal,
  matchesDetailKeyword,
  toggleFilterValue,
  type DetailFilters,
} from "./status-filters";
import { DetailAction, getActionTypeFilter } from "./detail-status";
import { assertDeepEqual, assertEqual, makeItem } from "./test-helpers";

// ---- 商品状态 / 条码状态分类（口径同 Web statusFilters.ts） ----
assertEqual(getProductStatusFilter(makeItem({ existingProductCount: null })), "notDetected", "null 为未检测");
assertEqual(getProductStatusFilter(makeItem({ existingProductCount: undefined })), "notDetected", "undefined 为未检测");
assertEqual(getProductStatusFilter(makeItem({ existingProductCount: 0 })), "notExists", "0 为不存在");
assertEqual(getProductStatusFilter(makeItem({ existingProductCount: 2 })), "exists", ">0 为已存在");
assertEqual(
  getProductStatusFilter(makeItem({ existingProductCount: 1, isCreatedByThisInvoice: true })),
  "createdHere",
  "本单新品优先（再次检测会把数量改成 1）"
);
assertEqual(
  getProductStatusFilter(makeItem({ existingProductCount: null, isCreatedByThisInvoice: true })),
  "createdHere",
  "本单新品优先于未检测"
);

assertEqual(getBarcodeStatusFilter(makeItem({ barcodeStatus: 0 })), "notDetected", "0 为未检测");
assertEqual(getBarcodeStatusFilter(makeItem({ barcodeStatus: null })), "notDetected", "null 为未检测");
assertEqual(getBarcodeStatusFilter(makeItem({ barcodeStatus: 1 })), "normal", "1 为正常");
assertEqual(getBarcodeStatusFilter(makeItem({ barcodeStatus: 2, barcodeMatchCount: 0 })), "noMatch", "异常且匹配数 0 为无匹配");
assertEqual(getBarcodeStatusFilter(makeItem({ barcodeStatus: 2, barcodeMatchCount: null })), "noMatch", "匹配数空按 0");
assertEqual(getBarcodeStatusFilter(makeItem({ barcodeStatus: 2, barcodeMatchCount: 2 })), "multiMatch", "异常且匹配数>0 为多匹配");
assertEqual(isBarcodeStatusAbnormal(makeItem({ barcodeStatus: 2, barcodeMatchCount: 0 })), true, "无匹配属于异常");
assertEqual(isBarcodeStatusAbnormal(makeItem({ barcodeStatus: 2, barcodeMatchCount: 3 })), true, "多匹配属于异常");
assertEqual(isBarcodeStatusAbnormal(makeItem({ barcodeStatus: 1 })), false, "正常不属于异常");
assertEqual(isBarcodeStatusAbnormal(makeItem({ barcodeStatus: 0 })), false, "未检测不属于异常");

assertEqual(getActionTypeFilter(makeItem({ activityType: 99 })), DetailAction.None, "99 不在操作筛选枚举内，落入「无」");
assertEqual(
  getActionTypeFilter(makeItem({ detailGuid: "x", activityType: 0 }), { x: DetailAction.AddMultiCode }),
  DetailAction.AddMultiCode,
  "rowActions 覆盖"
);
assertEqual(getProductTypeFilter(makeItem({ productType: 1 })), 1, "套装");
assertEqual(getProductTypeFilter(makeItem({ productType: null })), "unknown", "未匹配主档为 unknown");
assertEqual(getProductTypeFilter(makeItem({ productType: 9 })), "unknown", "未知类型值为 unknown");

// ---- 关键词搜索 ----
const searchable = makeItem({
  productCode: "PC-Alpha",
  itemNumber: "IT-9",
  barcode: "93111",
  productName: "Red Plastic Forks",
  storeProductCode: "1005PC-Alpha",
});
assertEqual(matchesDetailKeyword(searchable, ""), true, "空关键词不过滤");
assertEqual(matchesDetailKeyword(searchable, "   "), true, "全空白不过滤");
assertEqual(matchesDetailKeyword(searchable, " pc-alpha "), true, "忽略大小写与首尾空格（productCode）");
assertEqual(matchesDetailKeyword(searchable, "it-9"), true, "itemNumber");
assertEqual(matchesDetailKeyword(searchable, "9311"), true, "barcode");
assertEqual(matchesDetailKeyword(searchable, "plastic"), true, "productName");
assertEqual(matchesDetailKeyword(searchable, "1005pc"), true, "storeProductCode");
assertEqual(matchesDetailKeyword(searchable, "nothing"), false, "无命中");
assertEqual(matchesDetailKeyword(makeItem({ productCode: null, storeProductCode: undefined }), "x"), false, "空字段不报错");

// ---- 组合筛选 ----
const rows = [
  makeItem({ detailGuid: "up", lastPurchasePrice: 1, purchasePrice: 2, productType: 0, isSpecialProduct: true }),
  makeItem({ detailGuid: "down", lastPurchasePrice: 2, purchasePrice: 1, productType: 1, barcodeStatus: 2, barcodeMatchCount: 0 }),
  makeItem({ detailGuid: "newItem", existingProductCount: 0, lastPurchasePrice: null, productType: null, barcodeStatus: 2, barcodeMatchCount: 2, activityType: DetailAction.CreateProduct }),
  makeItem({ detailGuid: "unchecked", existingProductCount: null, barcodeStatus: 0, productType: 2, activityType: DetailAction.CreateProduct }),
  makeItem({ detailGuid: "created", isCreatedByThisInvoice: true, productType: 0, activityType: 99 }),
];
const guids = (filters: Partial<DetailFilters>) =>
  applyDetailFilters(rows, { ...createEmptyDetailFilters(), ...filters }).map((item) => item.detailGuid);
const more = (patch: Partial<DetailFilters["more"]>) => ({ ...createEmptyDetailFilters().more, ...patch });

assertEqual(guids({}).length, rows.length, "空筛选返回全部");
assertEqual(applyDetailFilters(rows, createEmptyDetailFilters()) === rows, false, "返回新数组，不改入参");

// 快捷筛选
assertDeepEqual(guids({ quickFilters: ["priceUp"] }), ["up"], "涨价");
assertDeepEqual(guids({ quickFilters: ["priceDown"] }), ["down"], "降价");
assertDeepEqual(guids({ quickFilters: ["priceUp", "priceDown"] }), ["up", "down"], "涨价+降价同属价格组，按 OR");
assertDeepEqual(guids({ quickFilters: ["productNotExists"] }), ["newItem"], "主档不存在");
assertDeepEqual(guids({ quickFilters: ["barcodeAbnormal"] }), ["down", "newItem"], "条码异常 = 无匹配 + 多匹配");
assertDeepEqual(guids({ quickFilters: ["priceDown", "barcodeAbnormal"] }), ["down"], "不同快捷项之间 AND");

// 更多筛选：组内 OR
assertDeepEqual(guids({ more: more({ productTypes: [0, 1] }) }), ["up", "down", "created"], "商品类型组内 OR");
assertDeepEqual(guids({ more: more({ productTypes: ["unknown"] }) }), ["newItem"], "未知商品类型");
assertDeepEqual(guids({ more: more({ productStatuses: ["notExists", "notDetected"] }) }), ["newItem", "unchecked"], "商品状态组内 OR");
assertDeepEqual(guids({ more: more({ barcodeStatuses: ["noMatch", "multiMatch"] }) }), ["down", "newItem"], "条码状态组内 OR");
assertDeepEqual(guids({ more: more({ barcodeStatuses: ["notDetected"] }) }), ["unchecked"], "条码未检测");
assertDeepEqual(guids({ more: more({ actionTypes: [DetailAction.CreateProduct] }) }), ["newItem", "unchecked"], "操作类型");
assertDeepEqual(
  guids({ more: more({ actionTypes: [DetailAction.None] }) }),
  ["up", "down", "created"],
  "操作类型「无」包含 99 已执行的行（与 Web 一致）"
);
assertDeepEqual(guids({ more: more({ special: ["yes"] }) }), ["up"], "特殊商品=是");
assertDeepEqual(guids({ more: more({ special: ["no"] }) }), ["down", "newItem", "unchecked", "created"], "特殊商品=否");
assertDeepEqual(guids({ more: more({ special: ["yes", "no"] }) }).length, rows.length, "是+否 = 不限");

// 组间 AND
assertDeepEqual(
  guids({ more: more({ productTypes: [0, 1], barcodeStatuses: ["noMatch"] }) }),
  ["down"],
  "商品类型 AND 条码状态"
);
assertDeepEqual(
  guids({ more: more({ productTypes: [0], special: ["no"] }) }),
  ["created"],
  "商品类型 AND 特殊商品"
);
assertDeepEqual(
  guids({ more: more({ productStatuses: ["notExists"], actionTypes: [DetailAction.UpdatePurchasePrice] }) }),
  [],
  "组间 AND 可得空集"
);

// 搜索 + 快捷 + 更多 + 进度分段 一起 AND
assertDeepEqual(
  guids({ searchText: "72750", quickFilters: ["barcodeAbnormal"], more: more({ productTypes: [1] }) }),
  ["down"],
  "搜索 AND 快捷 AND 更多"
);
assertDeepEqual(guids({ progressBucket: "unchecked" }), ["unchecked"], "按处理进度分段（未检测优先于已设操作）");
assertDeepEqual(guids({ progressBucket: "pending" }), ["newItem"], "待执行不含未检测行");
assertDeepEqual(guids({ progressBucket: "executed" }), ["created"], "已执行");
assertDeepEqual(guids({ progressBucket: "pending", quickFilters: ["priceUp"] }), [], "进度分段 AND 快捷");
assertDeepEqual(
  guids({ rowActions: { up: DetailAction.UpdatePurchasePrice }, progressBucket: "pending" }),
  ["up", "newItem"],
  "rowActions 影响进度分段"
);
assertDeepEqual(
  guids({ rowActions: { up: DetailAction.UpdatePurchasePrice }, more: more({ actionTypes: [DetailAction.UpdatePurchasePrice] }) }),
  ["up"],
  "rowActions 影响操作类型筛选"
);

// ---- 计数 ----
const counts = getDetailFilterCounts(rows);
assertEqual(counts.total, 5, "total");
assertDeepEqual(counts.product, { notDetected: 1, exists: 2, notExists: 1, createdHere: 1 }, "商品状态计数");
assertDeepEqual(counts.barcode, { notDetected: 1, normal: 2, noMatch: 1, multiMatch: 1 }, "条码状态计数");
assertEqual(counts.barcodeAbnormal, 2, "条码异常计数 = 无匹配 + 多匹配");
assertDeepEqual(counts.action, { 0: 3, 1: 2, 2: 0, 3: 0, 4: 0, 5: 0 }, "操作类型计数（99 计入 0）");
assertDeepEqual(counts.productType, { 0: 2, 1: 1, 2: 1, unknown: 1 }, "商品类型计数");
assertDeepEqual(counts.special, { yes: 1, no: 4 }, "特殊商品计数");
assertDeepEqual(counts.price, { up: 1, down: 1 }, "涨降价计数");
assertEqual(
  Object.values(counts.product).reduce((sum, count) => sum + count, 0),
  counts.total,
  "商品状态计数合计等于行数"
);
assertEqual(getDetailFilterCounts(rows, { up: DetailAction.AddMultiCode }).action[DetailAction.AddMultiCode], 1, "计数也认 rowActions");

// ---- 辅助 ----
assertDeepEqual(toggleFilterValue([1, 2], 2), [1], "toggle 移除");
assertDeepEqual(toggleFilterValue([1], 2), [1, 2], "toggle 追加");
assertEqual(countActiveMoreFilterGroups(createEmptyDetailFilters().more), 0, "空更多筛选组数为 0");
assertEqual(countActiveMoreFilterGroups(more({ productTypes: [0, 1], special: ["yes"] })), 2, "按组计数，不按选项计");
assertEqual(hasActiveDetailFilters(createEmptyDetailFilters()), false, "空筛选未生效");
assertEqual(hasActiveDetailFilters({ ...createEmptyDetailFilters(), searchText: " x " }), true, "搜索生效");
assertEqual(hasActiveDetailFilters({ ...createEmptyDetailFilters(), searchText: "  " }), false, "全空白搜索不算生效");
assertEqual(hasActiveDetailFilters({ ...createEmptyDetailFilters(), progressBucket: "waiting" }), true, "进度分段生效");
