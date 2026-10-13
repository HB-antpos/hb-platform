import assert from "node:assert/strict";
import {
  PRODUCT_NAME_MAX_LENGTH,
  evaluateProductNameDraft,
  formatUtcInstantAsLocal,
} from "./product-name-edit";

// 有效改名：去首尾空白后提交。
assert.deepEqual(evaluateProductNameDraft("  新名称  ", "旧名称"), {
  normalized: "新名称",
  canSave: true,
  issue: null,
});

// 空白、过长、控制字符都拦住；恰好 200 字符可以。
assert.equal(evaluateProductNameDraft("   ", "旧名称").issue, "empty");
assert.equal(evaluateProductNameDraft("名".repeat(PRODUCT_NAME_MAX_LENGTH + 1), "旧名称").issue, "tooLong");
assert.equal(evaluateProductNameDraft("名".repeat(PRODUCT_NAME_MAX_LENGTH), "旧名称").canSave, true);
assert.equal(evaluateProductNameDraft("第一行\n第二行", "旧名称").issue, "control");
assert.equal(evaluateProductNameDraft("带\t制表符", "旧名称").issue, "control");
assert.equal(evaluateProductNameDraft("带\u0085控制符", "旧名称").issue, "control", "C1 控制字符与后端 char.IsControl 同口径");

// 与当前名称相同（含首尾空白差异）视为没有变化，不发请求。
assert.equal(evaluateProductNameDraft("旧名称", "旧名称").issue, "unchanged");
assert.equal(evaluateProductNameDraft("  旧名称 ", " 旧名称").issue, "unchanged");
assert.equal(evaluateProductNameDraft("新名称", null).canSave, true, "当前名称为空时任何非空名称都是修改");

// 修改记录时间：有无 Z 后缀的同一 UTC 时刻必须显示一致，否则会被当成本地时间整体平移。
const withZone = formatUtcInstantAsLocal("2026-10-13T03:00:00Z");
assert.equal(formatUtcInstantAsLocal("2026-10-13T03:00:00"), withZone);
assert.equal(formatUtcInstantAsLocal("2026-10-13 03:00:00"), withZone);
assert.equal(formatUtcInstantAsLocal("2026-10-13T03:00:00.123456"), withZone);
assert.match(withZone, /^\d{2}\/\d{2}\/\d{4} \d{2}:\d{2}$/);
// 带偏移量的写法按偏移量解析：+11:00 的 14:00 就是 03:00Z。
assert.equal(formatUtcInstantAsLocal("2026-10-13T14:00:00+11:00"), withZone);
assert.equal(formatUtcInstantAsLocal(""), "—");
assert.equal(formatUtcInstantAsLocal(null), "—");
assert.equal(formatUtcInstantAsLocal("not-a-date"), "not-a-date");

console.log("product-name-edit.test.ts: ok");
