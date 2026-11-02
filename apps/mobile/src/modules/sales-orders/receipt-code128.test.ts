import assert from "node:assert/strict";
import { encodeCode128 } from "./receipt-code128";

// 向量来自 packages/pos-receipt-core 的 receiptCode128（参考实现）：改动任何一边都要同步。
const vectors: [string, number, string][] = [
  ["6E0F13AB", 123, "2112142231121321131231221323111232212211321113231311231224112331112"],
  ["20261030004008", 112, "2112322212313212212213122121232122222311131322121312222331112"],
  ["A", 46, "2112141113231311232331112"],
];
for (const [value, moduleCount, runs] of vectors) {
  const encoding = encodeCode128(value);
  assert.equal(encoding.moduleCount, moduleCount, `${value} 的模块数`);
  assert.equal(encoding.runs.join(""), runs, `${value} 的条空序列`);
}

// 36 位 GUID 为 431 个模块：宽 2 点 / 模块要 862 点，超过 80mm 纸的 576 点，这正是小票改用 8 位短码的原因。
assert.equal(encodeCode128("7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab").moduleCount, 431);

// 条空交替：起始符到终止符共 6n+7 个宽度，最后一个是条。
assert.equal(encodeCode128("6E0F13AB").runs.length % 2, 1);
assert.throws(() => encodeCode128(""), TypeError);
assert.throws(() => encodeCode128("小票"), TypeError);

console.log("receipt-code128.test.ts: ok");
