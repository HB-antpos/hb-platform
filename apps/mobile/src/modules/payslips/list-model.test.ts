import assert from "node:assert/strict";
import { FIXTURE_LIST_RESPONSE } from "./fixtures";
import { buildRowKey, buildRowTexts, buildSummary, failedCompanies, resolveRowPlacement } from "./list-model";
import { normalizeList } from "./normalize";

const list = normalizeList(FIXTURE_LIST_RESPONSE);
const [alex, , sam, casey] = list.rows;

// 卡片文案：实发 / 税前 / 支付日 d/m/yyyy / 周期 d/m — d/m · 频率
assert.deepEqual(buildRowTexts(alex), {
  netText: "$762.00",
  grossText: "$900.00",
  paymentDateText: "29/10/2026",
  periodText: "19/10 — 25/10 · Weekly",
});
assert.equal(buildRowTexts(sam).periodText, "19/10 — 25/10 · Fortnightly");

// 归属：未绑定 / 分店未知 / 单店 / 多店 / 推断
assert.deepEqual(resolveRowPlacement(alex), { kind: "stores", label: "1014 Kawana", inferred: false });
assert.deepEqual(resolveRowPlacement(casey), { kind: "unbound" });
assert.deepEqual(resolveRowPlacement({ placement: "Unknown", stores: [] }), { kind: "unknown" });
assert.deepEqual(
  resolveRowPlacement({ placement: "Inferred", stores: [{ storeCode: "1", storeLabel: "1 A" }, { storeCode: "2", storeLabel: "2 B" }, { storeCode: "3", storeLabel: "3 C" }] }),
  { kind: "stores", label: "1 A +2", inferred: true },
);

// 行 key 唯一（同一人同一支付日、不同周期也不冲突）
const keys = list.rows.map(buildRowKey);
assert.equal(new Set(keys).size, keys.length);
assert.notEqual(buildRowKey({ ...alex, periodStart: "2026-10-12" }), buildRowKey(alex));

// 汇总格 + 部分公司失败
assert.deepEqual(buildSummary(list), { count: 5, grossText: "$3,036.46", netText: "$2,605.76" });
assert.equal(failedCompanies(list).length, 0);
assert.deepEqual(
  failedCompanies({ companies: [{ connectionGuid: "a", connectionName: "A", ok: true, count: 1, errorCode: null, errorMessage: null }, { connectionGuid: "b", connectionName: "B", ok: false, count: 0, errorCode: "X", errorMessage: null }] }).map((company) => company.connectionName),
  ["B"],
);
