import { strict as assert } from "node:assert";
import peoplePosEn from "../../locales/en/screens/peoplePos.json";
import peoplePosZh from "../../locales/zh/screens/peoplePos.json";
import {
  buildPosPermissionDisplayGroups,
  computePosPermissionChanges,
} from "./pos-terminal-permission-presentation";
import {
  buildPosGroupSummary,
  buildPosHighRiskWarning,
  buildPosPermissionChangeRows,
} from "./pos-terminal-permissions-view";
import {
  createPosPermissionLabels,
  formatPosDiscountItemName,
  formatPosFooterSummary,
  formatPosGroupSummary,
  formatPosHighRiskWarning,
  type PosTranslate,
} from "./pos-terminal-permissions-format";
import type { PosTerminalPermissionOption } from "./types";

/**
 * 用真实的 zh / en 文案做 t：键写错、占位符漏传时直接抛错，
 * 同时把设计稿里的关键句子（A5 ②确认改动）锁成断言。
 */
function createTranslate(resource: unknown): PosTranslate {
  return (key, options) => {
    let node: unknown = resource;
    for (const part of key.split(".")) {
      assert.ok(node && typeof node === "object" && part in node, `文案缺少键 ${key}`);
      node = (node as Record<string, unknown>)[part];
    }
    assert.equal(typeof node, "string", `文案键 ${key} 不是字符串`);
    return (node as string).replace(/\{\{(\w+)\}\}/g, (_match, name: string) => {
      assert.ok(options && name in options, `键 ${key} 缺少占位符参数 ${name}`);
      return String(options[name]);
    });
  };
}
const zh = createTranslate(peoplePosZh);
const en = createTranslate(peoplePosEn);

const P = (suffix: string) => `Permissions.PosTerminal.${suffix}`;
const assignable: PosTerminalPermissionOption[] = [
  ["Sales.View", "查看销售页"],
  ["Sales.AddItem", "添加扫码商品"],
  ["Sales.AddOpenItem", "添加开放商品"],
  ["Sales.ChangePrice", "修改销售价格"],
  ["Returns.AddNoReceiptItem", "添加无小票退货商品"],
  ["CashDrawer.Open", "打开钱箱"],
  ["Sales.LineManualDiscount", "单行手工折扣"],
  ["Sales.OrderManualDiscount", "整单手工折扣"],
  ...(["10", "20", "30", "40", "50"] as const).flatMap((level): [string, string][] => [
    [`Sales.LineQuickDiscount${level}Percent`, `单行快捷${level}`],
    [`Sales.OrderQuickDiscount${level}Percent`, `整单快捷${level}`],
  ]),
].map(([suffix, name]) => ({ code: P(suffix), name, group: "POS", description: "" }));
const groups = buildPosPermissionDisplayGroups(assignable);
const discountGroup = groups.find((group) => group.key === "discount")!;
const salesGroup = groups.find((group) => group.key === "sales")!;

// ---------- 折扣项名称 ----------
assert.equal(formatPosDiscountItemName(zh, "order", "40"), "整单六折");
assert.equal(formatPosDiscountItemName(zh, "line", "10"), "单行九折");
assert.equal(formatPosDiscountItemName(zh, "line", "manual"), "单行手工折扣");
assert.equal(formatPosDiscountItemName(en, "order", "40"), "Order 40% off");
assert.equal(formatPosDiscountItemName(en, "line", "manual"), "Line manual discount");

// ---------- A5②：确认弹层警示条文案 ----------
const changes = computePosPermissionChanges(
  groups,
  [P("Sales.View"), P("Sales.ChangePrice")],
  [P("Sales.View"), P("Sales.OrderQuickDiscount40Percent")]
);
const warning = buildPosHighRiskWarning(changes, createPosPermissionLabels(zh))!;
assert.deepEqual(formatPosHighRiskWarning(warning, zh), {
  summary: "包含 1 项高风险权限：整单六折。",
  impact: "开启后该员工可以在收银机上直接给整单打六折。",
});
const warningEn = buildPosHighRiskWarning(changes, createPosPermissionLabels(en))!;
assert.deepEqual(formatPosHighRiskWarning(warningEn, en), {
  summary: "Includes 1 high-risk permission(s): Order 40% off.",
  impact: "Once on, this staff member can do the following on the POS: give the whole order 40% off.",
});

const manyWarning = buildPosHighRiskWarning(
  computePosPermissionChanges(
    groups,
    [],
    [
      P("Sales.ChangePrice"),
      P("Sales.LineManualDiscount"),
      P("Sales.OrderQuickDiscount50Percent"),
      P("Returns.AddNoReceiptItem"),
      P("CashDrawer.Open"),
    ]
  ),
  createPosPermissionLabels(zh)
)!;
assert.deepEqual(formatPosHighRiskWarning(manyWarning, zh), {
  summary: "包含 5 项高风险权限：修改销售价格、单行手工折扣、整单五折、添加无小票退货商品、打开钱箱。",
  impact:
    "开启后该员工可以在收银机上直接修改商品价格、对单行商品手工输入折扣、直接给整单打五折、不凭小票直接办理退货、不经销售直接打开钱箱。",
});
// 没有影响归类时只给第一句，不编造影响
assert.deepEqual(
  formatPosHighRiskWarning({ count: 1, names: ["某新权限"], impacts: [] }, zh),
  { summary: "包含 1 项高风险权限：某新权限。", impact: null }
);

// ---------- 改动行名称走同一套文案 ----------
const rows = buildPosPermissionChangeRows(changes, createPosPermissionLabels(zh));
assert.deepEqual(
  rows.map((row) => `${row.kind}|${row.name}|${row.groupLabel}`),
  ["added|整单六折|折扣", "removed|修改销售价格|销售"]
);
assert.equal(zh("confirm.rowAdded", { name: rows[0].name }), "开启 · 整单六折");
assert.equal(zh("confirm.rowRemoved", { name: rows[1].name }), "关闭 · 修改销售价格");
assert.equal(zh("confirm.title", { value: 2 }), "确认 2 项改动");
assert.equal(
  zh("confirm.subtitle", { user: "Emily Chen", store: "Bankstown 1008" }),
  "Emily Chen · 只对 Bankstown 1008 生效"
);
assert.equal(zh("subtitle", { user: "Emily Chen", store: "Bankstown 1008" }), "Emily Chen · Bankstown 1008");

// ---------- 分组摘要 ----------
assert.deepEqual(formatPosGroupSummary({ kind: "empty" }, zh), { text: "未开放", muted: true });
assert.deepEqual(
  formatPosGroupSummary(
    buildPosGroupSummary(salesGroup, new Set([P("Sales.View"), P("Sales.AddItem"), P("Sales.AddOpenItem"), P("Sales.ChangePrice")])),
    zh
  ),
  { text: "查看销售页、添加扫码商品、添加开放商品 等 4 项", muted: false }
);
assert.deepEqual(
  formatPosGroupSummary(buildPosGroupSummary(salesGroup, new Set([P("Sales.View"), P("Sales.AddItem")])), zh),
  { text: "查看销售页、添加扫码商品", muted: false },
  "未超出预览数量时不带「等 N 项」"
);
const discountSelected = new Set([
  P("Sales.LineQuickDiscount10Percent"),
  P("Sales.LineQuickDiscount20Percent"),
  P("Sales.LineQuickDiscount30Percent"),
  P("Sales.OrderQuickDiscount10Percent"),
  P("Sales.OrderQuickDiscount20Percent"),
  P("Sales.OrderQuickDiscount40Percent"),
]);
assert.deepEqual(formatPosGroupSummary(buildPosGroupSummary(discountGroup, discountSelected), zh), {
  text: "单行九/八/七折、整单九/八/六折",
  muted: false,
});
assert.equal(
  formatPosGroupSummary(buildPosGroupSummary(discountGroup, discountSelected), en).text,
  "Line 10/20/30% off, Order 10/20/40% off"
);
assert.equal(
  formatPosGroupSummary(
    buildPosGroupSummary(discountGroup, new Set([P("Sales.LineQuickDiscount50Percent"), P("Sales.LineManualDiscount"), P("Sales.OrderManualDiscount")])),
    zh
  ).text,
  "单行五折/手工、整单手工",
  "数字档位 + 手工合并成一段；只有手工的范围只写「整单手工」"
);

// ---------- 吸底栏 ----------
assert.deepEqual(formatPosFooterSummary(0, 0, zh), { main: "没有改动", note: "" });
assert.deepEqual(formatPosFooterSummary(2, 1, zh), { main: "2 项改动", note: "（含 1 项高风险）" });
assert.deepEqual(formatPosFooterSummary(3, 0, zh), { main: "3 项改动", note: "" });
assert.deepEqual(formatPosFooterSummary(2, 1, en), { main: "Changes: 2", note: " (incl. 1 high risk)" });

console.log("pos-terminal-permissions-format.test.ts: ok");
