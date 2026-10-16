import { strict as assert } from "node:assert";
import {
  buildPosPermissionDisplayGroups,
  computePosPermissionChanges,
  filterPosPermissionGroups,
  type PosDiscountLevel,
  type PosDiscountScope,
  type PosPermissionDisplayGroupKey,
} from "./pos-terminal-permission-presentation";
import {
  buildPosDiscountGrid,
  buildPosGroupSummary,
  buildPosHighRiskWarning,
  buildPosPermissionChangeRows,
  buildPosPermissionFilterChips,
  buildPosPermissionOverview,
  buildPosStoreLabel,
  getPosHighRiskImpact,
  getPosPermissionEmptyKind,
  getPosPermissionFooterKind,
  getPosPermissionItemLabel,
  groupHasEnabledHighRisk,
  normalizePosPermissionMode,
  resolvePosPermissionStoreTarget,
  type PosPermissionLabelResolver,
} from "./pos-terminal-permissions-view";
import { togglePosPermissionCode } from "./pos-terminal-permissions";
import type { PosTerminalPermissionOption } from "./types";

const P = (suffix: string) => `Permissions.PosTerminal.${suffix}`;

// 与后端 45 项可分配权限一致的夹具（顺序故意打乱，分组与排序由 buildPosPermissionDisplayGroups 负责）。
const BACKEND_CODES: [string, string][] = [
  ["Receipt.PrintLast", "打印上一张小票"],
  ["Sales.View", "查看销售页"],
  ["Sales.AddItem", "添加扫码商品"],
  ["Sales.AddOpenItem", "添加开放商品"],
  ["Sales.RemoveLine", "移除销售行"],
  ["Sales.ChangeQuantity", "修改销售数量"],
  ["Sales.ChangePrice", "修改销售价格"],
  ["Sales.ClearCart", "清空购物车"],
  ["Sales.HoldOrder", "挂起订单"],
  ["Sales.RecallOrder", "召回挂单"],
  ["Payment.View", "查看收款页"],
  ["Payment.TakeCash", "现金收款"],
  ["Payment.TakeCard", "刷卡收款"],
  ["Payment.TakeVoucher", "代金券收款"],
  ["Payment.RemoveTender", "移除收款"],
  ["Payment.Confirm", "确认收款"],
  ["Returns.AddReceiptLine", "添加小票退货行"],
  ["Returns.View", "查看退货页"],
  ["Returns.AddNoReceiptItem", "添加无小票退货商品"],
  ["Returns.Confirm", "确认退货"],
  ["History.View", "查看历史订单页"],
  ["History.Recall", "召回历史订单"],
  ["History.Reprint", "重打历史订单小票"],
  ["DailyClose.View", "查看日结页"],
  ["DailyClose.Save", "保存日结"],
  ["DailyClose.Reprint", "重打日结小票"],
  ["Installments.View", "查看分期页"],
  ["Installments.Create", "创建分期"],
  ["Installments.AddRepayment", "添加分期还款"],
  ["Installments.Cancel", "取消分期"],
  ["Installments.ConfirmPickup", "确认分期取货"],
  ["Installments.AmendLines", "修改分期商品列表"],
  ["CashDrawer.Open", "打开钱箱"],
  ["Sales.OrderManualDiscount", "整单手工折扣"],
  ["Sales.LineManualDiscount", "单行手工折扣"],
  ...(["10", "20", "30", "40", "50"] as const).flatMap((level): [string, string][] => [
    [`Sales.LineQuickDiscount${level}Percent`, `单行快捷${level}`],
    [`Sales.OrderQuickDiscount${level}Percent`, `整单快捷${level}`],
  ]),
];
const assignable: PosTerminalPermissionOption[] = BACKEND_CODES.map(([suffix, name]) => ({
  code: P(suffix),
  name,
  group: "POS",
  description: "",
}));
assert.equal(assignable.length, 45);

const groups = buildPosPermissionDisplayGroups(assignable);
const discountGroup = groups.find((group) => group.key === "discount")!;
const salesGroup = groups.find((group) => group.key === "sales")!;
const returnsGroup = groups.find((group) => group.key === "returns")!;

const groupLabels: Record<PosPermissionDisplayGroupKey, string> = {
  sales: "销售",
  discount: "折扣",
  payment: "收款",
  returns: "退货",
  history: "历史订单",
  dailyClose: "日结",
  installments: "分期",
  drawerReceipt: "钱箱与小票",
  other: "其他",
};
const scopeLabels: Record<PosDiscountScope, string> = { line: "单行", order: "整单" };
const levelLabels: Record<PosDiscountLevel, string> = {
  "10": "九折",
  "20": "八折",
  "30": "七折",
  "40": "六折",
  "50": "五折",
  manual: "手工折扣",
};
const labels: PosPermissionLabelResolver = {
  group: (key) => groupLabels[key],
  discountItem: (scope, level) => `${scopeLabels[scope]}${levelLabels[level]}`,
};

// ---------- 模式 ----------
assert.equal(normalizePosPermissionMode("Override"), "override");
assert.equal(normalizePosPermissionMode(" override "), "override");
assert.equal(normalizePosPermissionMode("Inherited"), "inherited");
assert.equal(normalizePosPermissionMode(undefined), "inherited", "缺省按角色默认");
assert.equal(normalizePosPermissionMode(""), "inherited");

// ---------- 概要统计 + 筛选芯片 ----------
const baseline = [P("Sales.View"), P("Sales.ChangePrice"), P("Payment.View")];
const selected = [P("Sales.View"), P("Payment.View"), P("Sales.OrderQuickDiscount40Percent"), P("Sales.LineQuickDiscount10Percent")];
const changes = computePosPermissionChanges(groups, baseline, selected);
const selectedSet = new Set(selected);
const overview = buildPosPermissionOverview({ groups, selectedCodeSet: selectedSet, changes, mode: "Override" });
assert.deepEqual(overview, {
  mode: "override",
  enabledCount: 4,
  total: 45,
  highRiskEnabledCount: 1,
  highRiskTotal: 9,
  changedCount: 3,
  addedHighRiskCount: 1,
});
assert.deepEqual(
  buildPosPermissionFilterChips(overview),
  [
    { key: "all" },
    { key: "enabled", count: 4 },
    { key: "highRisk", count: 9 },
    { key: "changed", count: 3 },
  ],
  "筛选芯片固定四个，计数分别是已开 / 高风险总数 / 本次改动"
);
const emptyOverview = buildPosPermissionOverview({
  groups,
  selectedCodeSet: new Set(),
  changes: computePosPermissionChanges(groups, [], []),
});
assert.equal(emptyOverview.mode, "inherited");
assert.equal(emptyOverview.changedCount, 0);
assert.equal(
  buildPosPermissionFilterChips(emptyOverview).find((chip) => chip.key === "changed")?.count,
  0,
  "没有改动时芯片仍显示 0，而不是消失"
);

// 筛选芯片的计数与「选中该芯片后列表实际条数」一致：已开 / 高风险 / 本次改动
const countItems = (filter: "enabled" | "highRisk" | "changed") =>
  filterPosPermissionGroups(groups, {
    query: "",
    filter,
    selectedCodeSet: selectedSet,
    changedCodeSet: changes.changedCodes,
    getGroupLabel: labels.group,
  }).flatMap((group) => group.items).length;
assert.equal(countItems("enabled"), overview.enabledCount);
assert.equal(countItems("highRisk"), overview.highRiskTotal);
assert.equal(countItems("changed"), overview.changedCount);

// ---------- 空列表提示 ----------
assert.equal(getPosPermissionEmptyKind("", "all"), "noResults");
assert.equal(getPosPermissionEmptyKind("  ", "enabled"), "noEnabled");
assert.equal(getPosPermissionEmptyKind("", "changed"), "noChanges");
assert.equal(getPosPermissionEmptyKind("折", "changed"), "noResults", "有搜索词时统一提示没有符合条件");
assert.equal(getPosPermissionEmptyKind("", "highRisk"), "noResults");

// ---------- 吸底栏 ----------
assert.equal(getPosPermissionFooterKind(emptyOverview), "idle");
assert.equal(getPosPermissionFooterKind(overview), "withHighRisk");
assert.equal(
  getPosPermissionFooterKind({ changedCount: 2, addedHighRiskCount: 0 }),
  "plain",
  "只关了高风险项不算「含高风险」"
);

// ---------- 名称 ----------
const orderSixty = discountGroup.items.find((item) => item.code === P("Sales.OrderQuickDiscount40Percent"))!;
const lineManual = discountGroup.items.find((item) => item.code === P("Sales.LineManualDiscount"))!;
const changePrice = salesGroup.items.find((item) => item.code === P("Sales.ChangePrice"))!;
assert.equal(getPosPermissionItemLabel(orderSixty, labels), "整单六折", "折扣项用范围+档位生成名称，不用后端的「整单快捷40」");
assert.equal(getPosPermissionItemLabel(lineManual, labels), "单行手工折扣");
assert.equal(getPosPermissionItemLabel(changePrice, labels), "修改销售价格", "非折扣项沿用后端权限名");

// ---------- 改动行：开启在前、关闭在后，各自按分组顺序 ----------
const rows = buildPosPermissionChangeRows(changes, labels);
assert.deepEqual(
  rows.map((row) => [row.kind, row.name, row.groupLabel, row.highRisk]),
  [
    ["added", "单行九折", "折扣", false],
    ["added", "整单六折", "折扣", true],
    ["removed", "修改销售价格", "销售", true],
  ],
  "开启与关闭分开列；折扣项名称为「范围+档位」；关闭的高风险项仍带高风险标记"
);
const mixed = computePosPermissionChanges(groups, [P("Sales.View"), P("History.View")], [P("Payment.View"), P("DailyClose.View")]);
assert.deepEqual(
  buildPosPermissionChangeRows(mixed, labels).map((row) => `${row.kind}:${row.code.replace("Permissions.PosTerminal.", "")}`),
  ["added:Payment.View", "added:DailyClose.View", "removed:Sales.View", "removed:History.View"],
  "即使关闭项的分组更靠前，也排在所有开启项之后"
);
assert.deepEqual(buildPosPermissionChangeRows(computePosPermissionChanges(groups, [], []), labels), []);

// ---------- 高风险文案 ----------
assert.deepEqual(getPosHighRiskImpact(orderSixty), { kind: "quickDiscountOrder", level: "40" });
assert.deepEqual(getPosHighRiskImpact(lineManual), { kind: "manualDiscountLine" });
assert.deepEqual(getPosHighRiskImpact(changePrice), { kind: "changePrice" });
assert.deepEqual(
  getPosHighRiskImpact(returnsGroup.items.find((item) => item.code === P("Returns.AddNoReceiptItem"))!),
  { kind: "noReceiptReturn" }
);
assert.deepEqual(
  getPosHighRiskImpact(
    groups.find((group) => group.key === "drawerReceipt")!.items.find((item) => item.code === P("CashDrawer.Open"))!
  ),
  { kind: "openCashDrawer" }
);
assert.equal(
  getPosHighRiskImpact(salesGroup.items.find((item) => item.code === P("Sales.View"))!),
  null,
  "普通权限没有高风险影响归类"
);

// 每个高风险定义项都必须有影响归类，否则警示条会漏写「开启后能做什么」
const highRiskItems = groups.flatMap((group) => group.items).filter((item) => item.highRisk);
assert.equal(highRiskItems.length, 9);
highRiskItems.forEach((item) => assert.notEqual(getPosHighRiskImpact(item), null, `${item.code} 缺少影响归类`));

const warning = buildPosHighRiskWarning(changes, labels);
assert.deepEqual(warning, {
  count: 1,
  names: ["整单六折"],
  impacts: [{ kind: "quickDiscountOrder", level: "40" }],
}, "只统计本次新开启的高风险项；关闭改价不进警示条");
assert.equal(
  buildPosHighRiskWarning(computePosPermissionChanges(groups, [P("Sales.ChangePrice")], []), labels),
  null,
  "只关高风险权限不出警示条"
);
assert.equal(buildPosHighRiskWarning(computePosPermissionChanges(groups, [], [P("Sales.View")]), labels), null);

const multi = buildPosHighRiskWarning(
  computePosPermissionChanges(
    groups,
    [],
    [
      P("Sales.OrderQuickDiscount40Percent"),
      P("Sales.OrderQuickDiscount50Percent"),
      P("Sales.LineManualDiscount"),
      P("Sales.ChangePrice"),
      P("CashDrawer.Open"),
      P("Sales.View"),
    ]
  ),
  labels
)!;
assert.equal(multi.count, 5, "普通项 Sales.View 不计入高风险");
assert.deepEqual(multi.names, ["修改销售价格", "单行手工折扣", "整单六折", "整单五折", "打开钱箱"], "按页面分组顺序（销售→折扣→钱箱）列名");
assert.deepEqual(
  multi.impacts.map((impact) => `${impact.kind}${impact.level ? `:${impact.level}` : ""}`),
  ["changePrice", "manualDiscountLine", "quickDiscountOrder:40", "quickDiscountOrder:50", "openCashDrawer"]
);

// ---------- 分组「含高风险」标签 ----------
assert.equal(groupHasEnabledHighRisk(discountGroup, selectedSet), true, "折扣组开了整单六折");
assert.equal(groupHasEnabledHighRisk(salesGroup, selectedSet), false, "销售组的改价已关，不亮标签");
assert.equal(groupHasEnabledHighRisk(returnsGroup, new Set([P("Returns.AddReceiptLine")])), false, "只开了有小票退货，不算高风险");
assert.equal(groupHasEnabledHighRisk(returnsGroup, new Set([P("Returns.AddNoReceiptItem")])), true);

// ---------- 折扣矩阵取值与回写 ----------
const grid = buildPosDiscountGrid(discountGroup.items, selectedSet, changes.changedCodes);
assert.deepEqual(
  grid.columns,
  [
    { level: "10", highRisk: false },
    { level: "20", highRisk: false },
    { level: "30", highRisk: false },
    { level: "40", highRisk: true },
    { level: "50", highRisk: true },
    { level: "manual", highRisk: true },
  ],
  "六列固定顺序；六折 / 五折 / 手工三列标红"
);
assert.deepEqual(grid.rows.map((row) => row.scope), ["line", "order"]);
assert.deepEqual(
  grid.rows.map((row) => row.cells.map((cell) => (cell?.checked ? 1 : 0))),
  [
    [1, 0, 0, 0, 0, 0],
    [0, 0, 0, 1, 0, 0],
  ],
  "矩阵取值 = 草稿选中状态（单行九折、整单六折）"
);
assert.deepEqual(
  grid.rows.map((row) => row.cells.map((cell) => (cell?.changed ? 1 : 0))),
  [
    [1, 0, 0, 0, 0, 0],
    [0, 0, 0, 1, 0, 0],
  ],
  "格子带本次改动标记"
);
assert.equal(grid.rows[1].cells[3]?.item.code, P("Sales.OrderQuickDiscount40Percent"));

// 回写：点格子 = 对 cell.item.code 做 toggle，草稿更新后矩阵取值随之变化；其他分组的草稿不受影响
const afterTap = togglePosPermissionCode(selected, grid.rows[0].cells[2]!.item.code);
assert.ok(afterTap.includes(P("Sales.LineQuickDiscount30Percent")));
assert.equal(afterTap.length, selected.length + 1);
const gridAfterTap = buildPosDiscountGrid(discountGroup.items, new Set(afterTap), changes.changedCodes);
assert.equal(gridAfterTap.rows[0].cells[2]?.checked, true);
assert.equal(gridAfterTap.rows[1].cells[3]?.checked, true, "其他格子保持");
const afterUntap = togglePosPermissionCode(afterTap, grid.rows[1].cells[3]!.item.code);
assert.equal(afterUntap.includes(P("Sales.OrderQuickDiscount40Percent")), false);
assert.ok(afterUntap.includes(P("Payment.View")), "回写不影响其他分组");

// 筛选后的矩阵：缺格用 null 占位，列只保留可见档位，空行不出现
const lineHighRiskOnly = discountGroup.items.filter((item) => item.discount?.scope === "line" && item.highRisk);
const partialGrid = buildPosDiscountGrid(lineHighRiskOnly, new Set(), new Set());
assert.deepEqual(partialGrid.columns.map((column) => column.level), ["40", "50", "manual"]);
assert.deepEqual(partialGrid.rows.map((row) => row.scope), ["line"]);
const sparse = discountGroup.items.filter(
  (item) => !(item.discount?.scope === "line" && item.discount.level === "20")
);
const sparseGrid = buildPosDiscountGrid(sparse, new Set(), new Set());
assert.equal(sparseGrid.columns.length, 6, "只缺单行八折时八折这一列仍在（整单还有）");
assert.equal(sparseGrid.rows[0].cells[1], null, "单行八折被去掉，用 null 占位保持列对齐");
assert.notEqual(sparseGrid.rows[1].cells[1], null);
const manualOnlyOrder = discountGroup.items.filter(
  (item) =>
    (item.discount?.scope === "line" && item.discount.level !== "20") ||
    (item.discount?.scope === "order" && item.discount.level === "manual")
);
const manualOnlyGrid = buildPosDiscountGrid(manualOnlyOrder, new Set(), new Set());
assert.deepEqual(
  manualOnlyGrid.columns.map((column) => column.level),
  ["10", "30", "40", "50", "manual"],
  "某一档两行都没有时整列消失"
);
assert.equal(manualOnlyGrid.rows[1].cells[0], null);
assert.notEqual(manualOnlyGrid.rows[1].cells[4], null);
assert.deepEqual(buildPosDiscountGrid([], new Set(), new Set()), { columns: [], rows: [] });
assert.deepEqual(buildPosDiscountGrid(salesGroup.items, new Set(), new Set()), { columns: [], rows: [] }, "非折扣项不进矩阵");

// ---------- 分组摘要 ----------
assert.deepEqual(buildPosGroupSummary(salesGroup, new Set()), { kind: "empty" });
assert.deepEqual(
  buildPosGroupSummary(salesGroup, new Set([P("Sales.View"), P("Sales.AddItem"), P("Sales.AddOpenItem"), P("Sales.RemoveLine")])),
  {
    kind: "names",
    names: ["查看销售页", "添加扫码商品", "添加开放商品"],
    selectedCount: 4,
    remainingCount: 1,
  }
);
assert.deepEqual(
  buildPosGroupSummary(
    discountGroup,
    new Set([
      P("Sales.LineQuickDiscount10Percent"),
      P("Sales.LineQuickDiscount20Percent"),
      P("Sales.LineQuickDiscount30Percent"),
      P("Sales.OrderQuickDiscount10Percent"),
      P("Sales.OrderQuickDiscount20Percent"),
      P("Sales.OrderQuickDiscount40Percent"),
    ])
  ),
  {
    kind: "discount",
    parts: [
      { scope: "line", levels: ["10", "20", "30"], manual: false },
      { scope: "order", levels: ["10", "20", "40"], manual: false },
    ],
  },
  "折扣组摘要：单行九/八/七折、整单九/八/六折"
);
assert.deepEqual(
  buildPosGroupSummary(discountGroup, new Set([P("Sales.OrderManualDiscount"), P("Sales.LineQuickDiscount50Percent"), P("Sales.LineManualDiscount")])),
  {
    kind: "discount",
    parts: [
      { scope: "line", levels: ["50"], manual: true },
      { scope: "order", levels: [], manual: true },
    ],
  },
  "只开手工折扣的范围 levels 为空、manual 为 true"
);
assert.deepEqual(buildPosGroupSummary(discountGroup, new Set()), { kind: "empty" });

// ---------- 页头分店 ----------
assert.equal(buildPosStoreLabel("Bankstown", "1008"), "Bankstown 1008");
assert.equal(buildPosStoreLabel("Bankstown 1008", "1008"), "Bankstown 1008", "店名已含编号不重复");
assert.equal(buildPosStoreLabel("  Bankstown ", " "), "Bankstown");
assert.equal(buildPosStoreLabel("", "1008"), "1008");
assert.equal(buildPosStoreLabel("Bankstown", undefined), "Bankstown");

// ---------- 路由参数 → 分店 ----------
const sessionStores = [
  { storeGUID: "G-1008", storeCode: "1008", storeName: "Bankstown" },
  { storeGUID: "G-1012", storeCode: "1012", storeName: "Liverpool" },
  { storeCode: "1015", storeName: "Parramatta" },
];
assert.deepEqual(
  resolvePosPermissionStoreTarget({ storeGuid: "G-X", storeCode: "1008", storeName: "Custom" }, sessionStores),
  { storeGuid: "G-X", storeCode: "1008", storeName: "Custom" },
  "路由给全了就完全按路由，不被会话分店覆盖"
);
assert.deepEqual(
  resolvePosPermissionStoreTarget({ storeCode: " 1008 " }, sessionStores),
  { storeGuid: "G-1008", storeCode: "1008", storeName: "Bankstown" },
  "只带 storeCode：从会话分店补 storeGuid 与店名"
);
assert.deepEqual(
  resolvePosPermissionStoreTarget({ storeGuid: "g-1012" }, sessionStores),
  { storeGuid: "g-1012", storeCode: "1012", storeName: "Liverpool" },
  "只带 storeGuid：补 storeCode（复制同事要用），GUID 不区分大小写匹配"
);
assert.deepEqual(
  resolvePosPermissionStoreTarget({ storeCode: "1015" }, sessionStores),
  { storeGuid: "", storeCode: "1015", storeName: "Parramatta" },
  "会话分店没有 GUID 时保持空，页面按参数不完整处理"
);
assert.deepEqual(
  resolvePosPermissionStoreTarget({ storeCode: "9999", storeName: "Elsewhere" }, sessionStores),
  { storeGuid: "", storeCode: "9999", storeName: "Elsewhere" },
  "不在会话分店里的不猜"
);
assert.deepEqual(resolvePosPermissionStoreTarget({}, sessionStores), { storeGuid: "", storeCode: "", storeName: "" });

console.log("pos-terminal-permissions-view.test.ts: ok");
