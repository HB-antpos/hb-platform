import assert from "node:assert/strict";

import { buildEan13Modules, ean13BarRuns, EAN13_MODULE_COUNT, isValidEan13 } from "./ean13-modules";

// 已知真实条码 4006381333931：首位 4 → 左半区奇偶 LGLLGG。
const left = "0001101" + "0100111" + "0101111" + "0111101" + "0001001" + "0110011";
const right = "1000010" + "1000010" + "1000010" + "1110100" + "1000010" + "1100110";
assert.equal(buildEan13Modules("4006381333931"), `101${left}01010${right}101`, "EAN13 模块串与标准编码一致");

const personalCode = buildEan13Modules("2912345678906");
assert.ok(personalCode, "个人码测试值是合法 EAN13");
assert.equal(personalCode.length, EAN13_MODULE_COUNT, "EAN13 固定 95 个模块");
assert.ok(personalCode.startsWith("101") && personalCode.endsWith("101"), "两端是起止护线");
assert.equal(personalCode.slice(45, 50), "01010", "中间是中线护线");

// 每位数字占 7 个模块：L 码黑模块数为奇数，G 码和 R 码为偶数。
const darkModules = (segment: string): number => segment.split("").filter((char) => char === "1").length;
// 首位 2 对应左半区排列 LLGGLG。
"LLGGLG".split("").forEach((kind, index) => {
  const dark = darkModules(personalCode.slice(3 + index * 7, 10 + index * 7));
  assert.equal(dark % 2, kind === "L" ? 1 : 0, `左半区第 ${index + 1} 位应为 ${kind} 码`);
});
for (let index = 0; index < 6; index += 1) {
  const dark = darkModules(personalCode.slice(50 + index * 7, 57 + index * 7));
  assert.equal(dark % 2, 0, "右半区全部是 R 码，黑模块数为偶数");
}

assert.equal(isValidEan13("2912345678906"), true);
assert.equal(isValidEan13("2912345678900"), false, "校验位错误");
assert.equal(isValidEan13("291234567890"), false, "位数不足");
assert.equal(isValidEan13("29123456789O6"), false, "含非数字");
assert.equal(buildEan13Modules("2912345678900"), null, "非法值不生成条码，由界面回退到二维码");
assert.equal(buildEan13Modules(""), null);

const runs = ean13BarRuns("0110111000");
assert.deepEqual(runs, [[1, 2], [4, 3]], "连续黑模块合并成段");
assert.equal(
  ean13BarRuns(personalCode).reduce((sum, [, length]) => sum + length, 0),
  personalCode.split("1").length - 1,
  "合并后的黑条总宽等于黑模块总数"
);

console.log("ean13-modules tests passed");
