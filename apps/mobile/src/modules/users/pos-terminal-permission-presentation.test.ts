import { strict as assert } from "node:assert";
import {
  POS_HIGH_RISK_PERMISSION_CODES,
  buildPosDiscountMatrix,
  buildPosPermissionDisplayGroups,
  buildPosPermissionPresetCodes,
  computePosPermissionChanges,
  filterPosPermissionCopyCandidates,
  filterPosPermissionGroups,
  getDefaultExpandedPosPermissionGroups,
  getMatchingPosPermissionPresets,
  getNextPosPermissionGroupChecked,
  getPosPermissionCopyCandidateName,
  getPosPermissionDisplayGroupKey,
  getPosPermissionGroupSummary,
  getPosPermissionInitials,
  getPosPermissionSelectionState,
  getPosPermissionStats,
  hasPosPermissionEntryGap,
  isHighRiskPosPermission,
  isPosPermissionFilterActive,
  type PosPermissionDisplayGroupKey,
} from "./pos-terminal-permission-presentation";
import { hasPrivilegedPosPermissionRole } from "./pos-terminal-permissions";
import type { PosTerminalPermissionOption, StoreUserListItem } from "./types";

const P = (suffix: string) => `Permissions.PosTerminal.${suffix}`;

// 与后端 PermissionSeedData.PosTerminalBusinessPermissionCodes 对齐的 44 项可分配权限（顺序故意打乱）。
const BACKEND_CODES: [string, string, string][] = [
  ["Receipt.PrintLast", "打印上一张小票", "POS 小票"],
  ["Sales.View", "查看销售页", "POS 销售"],
  ["Sales.AddItem", "添加扫码商品", "POS 销售"],
  ["Sales.AddOpenItem", "添加开放商品", "POS 销售"],
  ["Sales.RemoveLine", "移除销售行", "POS 销售"],
  ["Sales.ChangeQuantity", "修改销售数量", "POS 销售"],
  ["Sales.ChangePrice", "修改销售价格", "POS 销售"],
  ["Sales.ClearCart", "清空购物车", "POS 销售"],
  ["Sales.HoldOrder", "挂起订单", "POS 销售"],
  ["Sales.RecallOrder", "召回挂单", "POS 销售"],
  ["Payment.View", "查看收款页", "POS 收款"],
  ["Payment.TakeCash", "现金收款", "POS 收款"],
  ["Payment.TakeCard", "刷卡收款", "POS 收款"],
  ["Payment.TakeVoucher", "代金券收款", "POS 收款"],
  ["Payment.RemoveTender", "移除收款", "POS 收款"],
  ["Payment.Confirm", "确认收款", "POS 收款"],
  ["Returns.AddReceiptLine", "添加小票退货行", "POS 退货"],
  ["Returns.View", "查看退货页", "POS 退货"],
  ["Returns.AddNoReceiptItem", "添加无小票退货商品", "POS 退货"],
  ["Returns.Confirm", "确认退货", "POS 退货"],
  ["History.View", "查看历史订单页", "POS 历史订单"],
  ["History.Recall", "召回历史订单", "POS 历史订单"],
  ["History.Reprint", "重打历史订单小票", "POS 历史订单"],
  ["DailyClose.View", "查看日结页", "POS 日结"],
  ["DailyClose.Save", "保存日结", "POS 日结"],
  ["DailyClose.Reprint", "重打日结小票", "POS 日结"],
  ["Installments.View", "查看分期页", "POS 分期"],
  ["Installments.Create", "创建分期", "POS 分期"],
  ["Installments.AddRepayment", "添加分期还款", "POS 分期"],
  ["Installments.Cancel", "取消分期", "POS 分期"],
  ["Installments.ConfirmPickup", "确认分期取货", "POS 分期"],
  ["CashDrawer.Open", "打开钱箱", "POS 钱箱"],
  ["Sales.OrderManualDiscount", "整单手工折扣", "POS 销售"],
  ["Sales.LineManualDiscount", "单行手工折扣", "POS 销售"],
  ...(["10", "20", "30", "40", "50"] as const).flatMap((level): [string, string, string][] => [
    [`Sales.LineQuickDiscount${level}Percent`, `单行快捷${level}`, "POS 销售"],
    [`Sales.OrderQuickDiscount${level}Percent`, `整单快捷${level}`, "POS 销售"],
  ]),
];

const assignable: PosTerminalPermissionOption[] = BACKEND_CODES.map(([suffix, name, group]) => ({
  code: P(suffix),
  name,
  group,
  description: `${name}说明`,
}));

assert.equal(assignable.length, 44, "测试夹具应覆盖后端 44 项可分配权限");

// ---------- 前端分组 ----------
const groups = buildPosPermissionDisplayGroups(assignable);
const groupSizes = Object.fromEntries(groups.map((group) => [group.key, group.items.length]));
assert.deepEqual(
  groups.map((group) => group.key),
  ["sales", "discount", "payment", "returns", "history", "dailyClose", "installments", "drawerReceipt"],
  "分组按固定业务顺序输出，空组不出现"
);
assert.deepEqual(
  groupSizes,
  { sales: 9, discount: 12, payment: 6, returns: 4, history: 3, dailyClose: 3, installments: 5, drawerReceipt: 2 },
  "按 code 前缀映射：折扣 12 项独立成组，钱箱与小票合并"
);
assert.equal(
  groups.reduce((sum, group) => sum + group.items.length, 0),
  44,
  "分组不丢项"
);

const salesGroup = groups.find((group) => group.key === "sales")!;
assert.equal(salesGroup.entryCode, P("Sales.View"));
assert.equal(salesGroup.items[0].code, P("Sales.View"), "入口权限排在组内第一行");
assert.equal(salesGroup.items[0].isEntry, true);
assert.deepEqual(
  salesGroup.items.map((item) => item.code.replace("Permissions.PosTerminal.", "")),
  [
    "Sales.View",
    "Sales.AddItem",
    "Sales.AddOpenItem",
    "Sales.RemoveLine",
    "Sales.ChangeQuantity",
    "Sales.ChangePrice",
    "Sales.ClearCart",
    "Sales.HoldOrder",
    "Sales.RecallOrder",
  ],
  "组内按后端业务顺序排列，而不是按名称"
);

const returnsGroup = groups.find((group) => group.key === "returns")!;
assert.equal(returnsGroup.items[0].code, P("Returns.View"), "后端顺序打乱时入口仍排第一");

const drawerGroup = groups.find((group) => group.key === "drawerReceipt")!;
assert.equal(drawerGroup.entryCode, null, "钱箱与小票没有入口权限");

const discountGroup = groups.find((group) => group.key === "discount")!;
assert.equal(discountGroup.entryCode, null, "折扣组没有入口权限");
assert.deepEqual(
  discountGroup.items.slice(0, 6).map((item) => item.discount),
  [
    { scope: "line", level: "10" },
    { scope: "line", level: "20" },
    { scope: "line", level: "30" },
    { scope: "line", level: "40" },
    { scope: "line", level: "50" },
    { scope: "line", level: "manual" },
  ],
  "折扣按单行→整单、九折→手工排列"
);

// 未知 code 落入「其他」组，不能丢；重复 code 只出现一次。
const withUnknown = buildPosPermissionDisplayGroups([
  ...assignable,
  { code: "Permissions.PosTerminal.Future.View", name: "未来功能", group: "POS 未来", description: "" },
  { code: "Custom.Something", name: "自定义", group: "其他", description: "" },
  assignable[0],
]);
const otherGroup = withUnknown.find((group) => group.key === "other")!;
assert.deepEqual(otherGroup.items.map((item) => item.name), ["未来功能", "自定义"]);
assert.equal(otherGroup.entryCode, null, "未知模块的 View 不当作入口");
assert.equal(withUnknown.at(-1)?.key, "other", "其他组排在最后");
assert.equal(
  withUnknown.reduce((sum, group) => sum + group.items.length, 0),
  46,
  "重复 code 去重"
);

assert.equal(getPosPermissionDisplayGroupKey("PosTerminal.Payment.TakeCash"), "payment", "兼容不带 Permissions. 前缀的写法");
assert.equal(getPosPermissionDisplayGroupKey("permissions.posterminal.sales.linequickdiscount10percent"), "discount", "code 比较不区分大小写");
assert.equal(getPosPermissionDisplayGroupKey(P("Sales.LineDiscount")), "sales", "旧版折扣权限不进折扣矩阵");

// ---------- 高风险 ----------
assert.equal(POS_HIGH_RISK_PERMISSION_CODES.length, 9);
assert.deepEqual(
  assignable.filter((permission) => isHighRiskPosPermission(permission.code)).map((permission) => permission.code).sort(),
  [
    P("CashDrawer.Open"),
    P("Returns.AddNoReceiptItem"),
    P("Sales.ChangePrice"),
    P("Sales.LineManualDiscount"),
    P("Sales.LineQuickDiscount40Percent"),
    P("Sales.LineQuickDiscount50Percent"),
    P("Sales.OrderManualDiscount"),
    P("Sales.OrderQuickDiscount40Percent"),
    P("Sales.OrderQuickDiscount50Percent"),
  ].sort(),
  "高风险名单固定 9 项"
);
assert.equal(isHighRiskPosPermission(P("Sales.LineQuickDiscount30Percent")), false, "七折不是高风险");

// ---------- 三态 ----------
const paymentCodes = groups.find((group) => group.key === "payment")!.items.map((item) => item.code);
assert.equal(getPosPermissionSelectionState(paymentCodes, new Set()), "none");
assert.equal(getPosPermissionSelectionState(paymentCodes, new Set([paymentCodes[0]])), "partial");
assert.equal(getPosPermissionSelectionState(paymentCodes, new Set(paymentCodes)), "all");
assert.equal(getPosPermissionSelectionState([], new Set(paymentCodes)), "none", "空组视为未选");
assert.equal(getNextPosPermissionGroupChecked("all"), false, "全选时点击=清空本组");
assert.equal(getNextPosPermissionGroupChecked("partial"), true, "部分选中时点击=全选本组");
assert.equal(getNextPosPermissionGroupChecked("none"), true);

// ---------- 入口未开提示 ----------
assert.equal(hasPosPermissionEntryGap(salesGroup, new Set([P("Sales.AddItem")])), true, "入口没开但组内其他项开着");
assert.equal(hasPosPermissionEntryGap(salesGroup, new Set([P("Sales.View"), P("Sales.AddItem")])), false);
assert.equal(hasPosPermissionEntryGap(salesGroup, new Set()), false, "整组都没开不提示");
assert.equal(hasPosPermissionEntryGap(drawerGroup, new Set([P("CashDrawer.Open")])), false, "无入口的组不提示");

// ---------- 改动对比 ----------
const changes = computePosPermissionChanges(
  groups,
  [P("Sales.View"), P("Payment.TakeCash"), "Unknown.Permission"],
  [P("Sales.View"), P("CashDrawer.Open"), P("Sales.AddItem"), "Unknown.Permission"]
);
assert.deepEqual(
  changes.ordered.map(({ item, kind }) => [kind, item.code]),
  [
    ["added", P("Sales.AddItem")],
    ["removed", P("Payment.TakeCash")],
    ["added", P("CashDrawer.Open")],
  ],
  "改动按页面分组顺序列出，未知 code 不计入"
);
assert.equal(changes.added.length, 2);
assert.equal(changes.removed.length, 1);
assert.equal(changes.addedHighRiskCount, 1, "新开钱箱算一项高风险");
assert.deepEqual([...changes.changedCodes].sort(), [P("CashDrawer.Open"), P("Payment.TakeCash"), P("Sales.AddItem")].sort());
assert.equal(computePosPermissionChanges(groups, [P("Sales.View")], [P("Sales.View")]).ordered.length, 0);
assert.equal(
  computePosPermissionChanges(groups, [P("Sales.ChangePrice")], []).addedHighRiskCount,
  0,
  "关闭高风险项不触发高风险警告"
);

// ---------- 模板 ----------
const basic = buildPosPermissionPresetCodes("basic", assignable);
assert.equal(basic.length, 17, "基础收银 = 销售 7 + 收款 6 + 历史 3 + 补打小票 1");
assert.ok(!basic.includes(P("Sales.ChangePrice")) && !basic.includes(P("Sales.ClearCart")), "基础收银不含改价、清空购物车");
assert.ok(basic.includes(P("Receipt.PrintLast")));
const senior = buildPosPermissionPresetCodes("senior", assignable);
assert.equal(senior.length, 27, "资深收银 = 基础 17 + 退货 3 + 快捷九/八折 4 + 日结 3");
assert.ok(!senior.includes(P("Returns.AddNoReceiptItem")), "资深收银不含无小票退货");
assert.ok(senior.includes(P("Sales.OrderQuickDiscount20Percent")));
assert.ok(!senior.includes(P("Sales.LineQuickDiscount30Percent")));
assert.equal(senior.filter(isHighRiskPosPermission).length, 0, "两个收银模板都不含高风险项");
assert.equal(buildPosPermissionPresetCodes("all", assignable).length, 44);
assert.deepEqual(buildPosPermissionPresetCodes("none", assignable), []);

// 店长可分配范围更小时，模板与 assignable 取交集，且保留后端原始 code 写法。
const restricted: PosTerminalPermissionOption[] = [
  { code: "permissions.posterminal.sales.view", name: "查看销售页", group: "POS 销售", description: "" },
  { code: P("Payment.TakeCash"), name: "现金收款", group: "POS 收款", description: "" },
  { code: P("CashDrawer.Open"), name: "打开钱箱", group: "POS 钱箱", description: "" },
];
assert.deepEqual(
  buildPosPermissionPresetCodes("basic", restricted),
  ["permissions.posterminal.sales.view", P("Payment.TakeCash")],
  "模板只取可分配项，并返回后端原样 code"
);
assert.deepEqual(
  getMatchingPosPermissionPresets(["permissions.posterminal.sales.view", P("Payment.TakeCash")], restricted),
  ["basic", "senior"],
  "可分配范围受限时多个模板可能同时命中"
);
assert.deepEqual(getMatchingPosPermissionPresets(basic, assignable), ["basic"]);
assert.deepEqual(getMatchingPosPermissionPresets([...senior].reverse(), assignable), ["senior"], "与顺序无关");
assert.deepEqual(getMatchingPosPermissionPresets([], assignable), ["none"]);
assert.deepEqual(getMatchingPosPermissionPresets([...basic, P("Sales.ClearCart")], assignable), [], "多一项就不算命中");

// ---------- 搜索与筛选 ----------
const labels: Record<PosPermissionDisplayGroupKey, string> = {
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
const getGroupLabel = (key: PosPermissionDisplayGroupKey) => labels[key];
const baseFilter = {
  query: "",
  filter: "all" as const,
  selectedCodeSet: new Set<string>(),
  changedCodeSet: new Set<string>(),
  getGroupLabel,
};
assert.equal(filterPosPermissionGroups(groups, baseFilter).length, groups.length, "无条件时全部保留");
const searchByName = filterPosPermissionGroups(groups, { ...baseFilter, query: " 收款 " });
assert.deepEqual(
  searchByName.map((group) => [group.key, group.items.length]),
  [["payment", 6]],
  "命中分组名时整组保留（「收款」组 6 项；其他组没有名称含「收款」的项）"
);
const searchItem = filterPosPermissionGroups(groups, { ...baseFilter, query: "小票" });
assert.deepEqual(
  searchItem.map((group) => [group.key, group.items.map((item) => item.name)]),
  [
    ["returns", ["添加小票退货行", "添加无小票退货商品"]],
    ["history", ["重打历史订单小票"]],
    ["dailyClose", ["重打日结小票"]],
    ["drawerReceipt", ["打开钱箱", "打印上一张小票"]],
  ],
  "按权限名过滤，空组去掉；「小票」同时命中组名「钱箱与小票」，该组整组保留"
);
assert.deepEqual(
  filterPosPermissionGroups(groups, { ...baseFilter, query: "钱箱" }).map((group) => group.items.length),
  [2],
  "搜索合并组名「钱箱与小票」命中整组"
);
const highRiskOnly = filterPosPermissionGroups(groups, { ...baseFilter, filter: "highRisk" });
assert.equal(highRiskOnly.flatMap((group) => group.items).length, 9);
const enabledOnly = filterPosPermissionGroups(groups, {
  ...baseFilter,
  filter: "enabled",
  selectedCodeSet: new Set([P("Sales.View"), P("History.View")]),
});
assert.deepEqual(enabledOnly.map((group) => group.key), ["sales", "history"]);
const changedOnly = filterPosPermissionGroups(groups, {
  ...baseFilter,
  filter: "changed",
  changedCodeSet: changes.changedCodes,
  query: "钱箱",
});
assert.deepEqual(changedOnly.flatMap((group) => group.items.map((item) => item.code)), [P("CashDrawer.Open")], "筛选与搜索叠加");
assert.equal(filterPosPermissionGroups(groups, { ...baseFilter, query: "不存在的权限" }).length, 0);
assert.equal(isPosPermissionFilterActive("", "all"), false);
assert.equal(isPosPermissionFilterActive("  ", "all"), false);
assert.equal(isPosPermissionFilterActive("a", "all"), true);
assert.equal(isPosPermissionFilterActive("", "changed"), true);

// ---------- 默认展开 ----------
const expanded = getDefaultExpandedPosPermissionGroups(
  groups,
  new Set([...paymentCodes, P("Sales.View"), P("CashDrawer.Open")]),
  new Set([P("History.View")])
);
assert.deepEqual(
  [...expanded].sort(),
  ["drawerReceipt", "history", "sales"].sort(),
  "部分勾选或有改动的组展开；整组全开（收款）且无改动的组折叠"
);

// ---------- 组头摘要与统计 ----------
const salesSummary = getPosPermissionGroupSummary(
  salesGroup,
  new Set([P("Sales.View"), P("Sales.AddItem"), P("Sales.AddOpenItem"), P("Sales.RemoveLine")])
);
assert.deepEqual(salesSummary, {
  selectedCount: 4,
  total: 9,
  previewNames: ["查看销售页", "添加扫码商品", "添加开放商品"],
  remainingCount: 1,
});
assert.deepEqual(getPosPermissionGroupSummary(salesGroup, new Set()).previewNames, []);

const stats = getPosPermissionStats(groups, new Set([...basic, P("CashDrawer.Open"), "Unknown.Permission"]));
assert.equal(stats.enabledCount, 18, "统计只计可分配项");
assert.equal(stats.total, 44);
assert.equal(stats.highRiskTotal, 9);
assert.equal(stats.highRiskEnabledCount, 1);
assert.deepEqual(
  stats.segments.map((segment) => [segment.key, segment.selected, segment.total]),
  [
    ["sales", 7, 9],
    ["discount", 0, 12],
    ["payment", 6, 6],
    ["returns", 0, 4],
    ["history", 3, 3],
    ["dailyClose", 0, 3],
    ["installments", 0, 5],
    ["drawerReceipt", 2, 2],
  ],
  "分段进度条按分组统计"
);

// ---------- 折扣矩阵 ----------
const matrix = buildPosDiscountMatrix(discountGroup.items);
assert.deepEqual(
  matrix.map((row) => [row.scope, row.cells.map((cell) => cell.level)]),
  [
    ["line", ["10", "20", "30", "40", "50", "manual"]],
    ["order", ["10", "20", "30", "40", "50", "manual"]],
  ]
);
assert.equal(matrix[1].cells[5].item.code, P("Sales.OrderManualDiscount"));
const partialMatrix = buildPosDiscountMatrix(
  discountGroup.items.filter((item) => item.discount?.scope === "line" && item.highRisk)
);
assert.deepEqual(
  partialMatrix.map((row) => [row.scope, row.cells.map((cell) => cell.level)]),
  [["line", ["40", "50", "manual"]]],
  "筛选后矩阵只保留可见格子，空行不出现"
);

// ---------- 复制同事候选 ----------
function user(overrides: Partial<StoreUserListItem>): StoreUserListItem {
  return {
    userGUID: "u",
    username: "user",
    status: 1,
    roleNames: ["StoreStaff"],
    ...overrides,
  };
}
const staff: StoreUserListItem[] = [
  user({ userGUID: "TARGET", username: "target" }),
  user({ userGUID: "actor", username: "manager-self" }),
  user({ userGUID: "a", username: "alice", fullName: "Alice Wang" }),
  user({ userGUID: "b", username: "bob", status: 0 }),
  user({ userGUID: "c", username: "carol", roleNames: ["StoreManager"] }),
  user({ userGUID: "d", username: "dave", roleNames: ["店长"] }),
  user({ userGUID: "e", username: "erin", fullName: "  " }),
  user({ userGUID: "A", username: "alice-dup" }),
];
assert.deepEqual(
  filterPosPermissionCopyCandidates(staff, { targetUserGuid: "target", actorUserGuid: " ACTOR " }).map(
    (candidate) => candidate.username
  ),
  ["alice", "erin"],
  "排除目标本人、操作者本人、停用账号、高权限角色，并按 GUID 去重"
);
assert.deepEqual(
  filterPosPermissionCopyCandidates(staff, { targetUserGuid: "target", query: "wang" }).map(
    (candidate) => candidate.username
  ),
  ["alice"],
  "按姓名或账号搜索"
);
assert.deepEqual(
  filterPosPermissionCopyCandidates(staff, { targetUserGuid: "target", actorUserGuid: null }).map(
    (candidate) => candidate.username
  ),
  ["manager-self", "alice", "erin"],
  "拿不到操作者 GUID 时不误排除"
);
assert.equal(getPosPermissionCopyCandidateName(staff[2]), "Alice Wang");
assert.equal(getPosPermissionCopyCandidateName(staff[6]), "erin", "姓名为空白时回落到账号");
assert.equal(hasPrivilegedPosPermissionRole(["  storemanager "]), true);
assert.equal(hasPrivilegedPosPermissionRole(["StoreStaff"]), false);

// ---------- 头像首字母 ----------
assert.equal(getPosPermissionInitials("Alice Wang"), "AW");
assert.equal(getPosPermissionInitials("王小明"), "王小");
assert.equal(getPosPermissionInitials("   "), "?");

console.log("pos-terminal-permission-presentation.test.ts: ok");
