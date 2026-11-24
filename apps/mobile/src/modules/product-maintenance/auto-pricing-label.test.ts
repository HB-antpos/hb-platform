import assert from "node:assert/strict";
import {
  AUTO_PRICING_LABEL_KINDS,
  isAutoPricingLabelKindAvailable,
  resolveAutoPricingLabelPlan,
  resolveAutoPricingLabelSelection,
} from "./auto-pricing-label";

// 选项顺序固定：与底部打印条的 Reg / Small / Disc / Large 一致。
assert.deepEqual([...AUTO_PRICING_LABEL_KINDS], ["product", "small", "discount", "bigDiscount"]);

// 普通 / 小标签始终可选；折扣 / 大标签只有新价有折扣时才可选。
for (const hasDiscount of [true, false]) {
  assert.equal(isAutoPricingLabelKindAvailable("product", hasDiscount), true);
  assert.equal(isAutoPricingLabelKindAvailable("small", hasDiscount), true);
}
assert.equal(isAutoPricingLabelKindAvailable("discount", true), true);
assert.equal(isAutoPricingLabelKindAvailable("bigDiscount", true), true);
assert.equal(isAutoPricingLabelKindAvailable("discount", false), false);
assert.equal(isAutoPricingLabelKindAvailable("bigDiscount", false), false);

// 默认选普通标签；记住的上次选择可用就沿用，不可用（新价没有折扣）回退到普通标签。
assert.equal(resolveAutoPricingLabelSelection(null, true), "product");
assert.equal(resolveAutoPricingLabelSelection(undefined, false), "product");
assert.equal(resolveAutoPricingLabelSelection("small", false), "small");
assert.equal(resolveAutoPricingLabelSelection("discount", true), "discount");
assert.equal(resolveAutoPricingLabelSelection("bigDiscount", true), "bigDiscount");
assert.equal(resolveAutoPricingLabelSelection("discount", false), "product");
assert.equal(resolveAutoPricingLabelSelection("bigDiscount", false), "product");

// 打印参数映射（口径与底部打印条的 handlePrint 一致）：
// 普通标签跟随打印设置里的「小标签」开关；小标签固定 60mm，不看开关。
assert.deepEqual(resolveAutoPricingLabelPlan("product", false), {
  action: "product",
  printType: null,
  discountKind: undefined,
});
assert.deepEqual(resolveAutoPricingLabelPlan("product", true), {
  action: "product",
  printType: "small",
  discountKind: undefined,
});
assert.deepEqual(resolveAutoPricingLabelPlan("small", false), {
  action: "small",
  printType: "small",
  discountKind: undefined,
});
assert.deepEqual(resolveAutoPricingLabelPlan("small", true), {
  action: "small",
  printType: "small",
  discountKind: undefined,
});
// 折扣标签跟随小标签开关；大折扣标签打印层不看 printType，固定为 null。
assert.deepEqual(resolveAutoPricingLabelPlan("discount", false), {
  action: "discount",
  printType: null,
  discountKind: "discount",
});
assert.deepEqual(resolveAutoPricingLabelPlan("discount", true), {
  action: "discount",
  printType: "small",
  discountKind: "discount",
});
assert.deepEqual(resolveAutoPricingLabelPlan("bigDiscount", true), {
  action: "bigDiscount",
  printType: null,
  discountKind: "bigDiscount",
});

console.log("auto-pricing-label tests passed");
