import assert from "node:assert/strict";
import { buildLineViews, buildPaperFields, resolveLineKind } from "./detail-rows";
import { FIXTURE_DETAIL_RESPONSE } from "./fixtures";
import { normalizeDetail } from "./normalize";

const detail = normalizeDetail(FIXTURE_DETAIL_RESPONSE);

// 纸面抬头：MYOB 原文标签与顺序，日期 d/m/yyyy，Hourly Rate 四位小数，ABN 分组
assert.deepEqual(buildPaperFields(detail), [
  { label: "ABN:", value: "12 345 678 901" },
  { label: "Pay Slip For:", value: "ALEX MORGAN" },
  { label: "Reference Number:", value: "1263" },
  { label: "Payment Date:", value: "29/10/2026" },
  { label: "Hourly Rate:", value: "$24.0000" },
  { label: "Pay Period From:", value: "19/10/2026 To: 25/10/2026" },
  { label: "Superannuation Fund:", value: "SAMPLE SUPER" },
]);

// 缺值字段整行不出现
const sparse = buildPaperFields({ ...detail, referenceNumber: null, hourlyRate: null, superFundName: "", companyAbn: "" });
assert.deepEqual(
  sparse.map((field) => field.label),
  ["Pay Slip For:", "Payment Date:", "Pay Period From:"],
);

// 明细：保持 MYOB 原始顺序，两行一条
const views = buildLineViews(detail.lines);
assert.deepEqual(
  views.map((view) => view.description),
  ["Base Hourly", "SATURDAY (x1.25)", "PAYG Withholding", "Superannuation Guarantee", "Annual Leave Accrual"],
);
assert.deepEqual(
  views.map((view) => ({ tag: view.typeLabel, amount: view.amountText, calc: view.calcText, ytd: view.ytdText })),
  [
    { tag: "Wages", amount: "$720.00", calc: "30.00 h × $24.00", ytd: "YTD $11,520.00" },
    { tag: "Wages", amount: "$180.00", calc: "6.00 h × $30.00", ytd: "YTD $2,160.00" },
    { tag: "Tax", amount: "-$138.00", calc: "", ytd: "YTD -$2,208.00" },
    { tag: "Super", amount: "$108.00", calc: "", ytd: "YTD $1,641.60" },
    // Entitlement：小时数在 hours，没有金额，YTD 是累计小时
    { tag: "Entitlement", amount: "—", calc: "1.15 h", ytd: "YTD 18.45 h" },
  ],
);
assert.equal(views[2].amountNegative, true);
assert.equal(views[0].amountNegative, false);
assert.equal(views[4].amountNegative, false);
assert.equal(new Set(views.map((view) => view.key)).size, views.length, "行 key 唯一");

// 类型大类；typeLabel 缺失时用 type
assert.equal(resolveLineKind("Entitlement"), "entitlement");
assert.equal(resolveLineKind("Wages"), "wages");
assert.equal(resolveLineKind("Tax"), "tax");
assert.equal(resolveLineKind("Superannuation"), "super");
assert.equal(resolveLineKind("Deduction"), "deduction");
assert.equal(resolveLineKind("Other thing"), "other");
const bare = buildLineViews([{ description: "X", type: "Wages", typeLabel: "", hours: null, rate: null, amount: null, yearToDate: null }]);
assert.equal(bare[0].typeLabel, "Wages");
assert.equal(bare[0].amountText, "—");
assert.equal(bare[0].calcText, "");
assert.equal(bare[0].ytdText, "");
