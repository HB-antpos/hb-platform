import assert from "node:assert/strict";
import { formatOrderMoney, normalizeGrade, resolveGradeColors, resolveOrderStep, resolveTotalPages } from "./order-ui";
import { buildQuantityPresets } from "./quantity-presets";

assert.equal(formatOrderMoney(1234.5), "$1,234.50", "金额保留两位小数并加千分位");
assert.equal(formatOrderMoney(0), "$0.00", "零金额正常显示");
assert.equal(formatOrderMoney(null), "--", "缺失金额显示占位");
assert.equal(formatOrderMoney(Number.NaN), "--", "非法金额显示占位");

assert.equal(resolveOrderStep(6), 6, "起订量作为加减步长");
assert.equal(resolveOrderStep(0), 1, "起订量为 0 时按 1");
assert.equal(resolveOrderStep(undefined), 1, "缺失起订量时按 1");

assert.equal(resolveTotalPages(0, 18), 1, "没有数据时仍显示 1 页");
assert.equal(resolveTotalPages(18, 18), 1, "刚好一页");
assert.equal(resolveTotalPages(19, 18), 2, "多出一条进位到下一页");
assert.equal(resolveTotalPages(1284, 18), 72, "设计图示例总页数");

assert.equal(normalizeGrade(" b "), "B", "等级去空格转大写");
assert.deepEqual(resolveGradeColors("c"), { background: "#FFF4E5", text: "#B54708" }, "C 级使用浅底深字");
assert.deepEqual(resolveGradeColors("Z"), { background: "#F2F4F7", text: "#475467" }, "未知等级回落中性色");

assert.deepEqual(buildQuantityPresets(6), [0, 6, 12, 18, 24, 48], "快捷数量为清零加起订量倍数");
assert.deepEqual(buildQuantityPresets(1), [0, 1, 2, 3, 4, 8], "起订量为 1 时退化为 1/2/3/4/8");
assert.deepEqual(buildQuantityPresets(0), [0, 1, 2, 3, 4, 8], "非法起订量按 1 处理");

console.log("order-ui.test.ts: ok");
