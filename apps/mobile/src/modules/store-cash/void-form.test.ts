import assert from "node:assert/strict";
import { buildVoidRequest, validateVoidReason } from "./void-form";

assert.equal(validateVoidReason(""), "tooShort");
assert.equal(validateVoidReason("   "), "tooShort");
assert.equal(validateVoidReason("错"), "tooShort", "一个字不够");
assert.equal(validateVoidReason(" 错 "), "tooShort", "首尾空白不计");
assert.equal(validateVoidReason("录错"), null, "两个汉字即可");
assert.equal(validateVoidReason("ab"), null);
assert.equal(validateVoidReason("😀😀"), null, "按字符而不是 UTF-16 码元计数");
assert.equal(validateVoidReason("😀"), "tooShort");
assert.equal(validateVoidReason("x".repeat(501)), "tooLong");
assert.equal(validateVoidReason("x".repeat(500)), null);

assert.deepEqual(buildVoidRequest("  金额录错了  "), { reason: "金额录错了" });
assert.equal(buildVoidRequest("错"), null);

console.log("void-form.test.ts: ok");
