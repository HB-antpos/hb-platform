import assert from "node:assert/strict";
import test from "node:test";

import {
  isManualUnitPriceWithinMaximum,
  MAX_MANUAL_UNIT_PRICE_CENTS,
  MAX_MANUAL_UNIT_PRICE_DISPLAY,
} from "./index";

test("手工单价上限与 WPF PosPriceInputRules.MaxUnitPrice 一致：9999 澳元（含）", () => {
  assert.equal(MAX_MANUAL_UNIT_PRICE_CENTS, 999_900);
  assert.equal(MAX_MANUAL_UNIT_PRICE_DISPLAY, "9999");

  assert.equal(isManualUnitPriceWithinMaximum(1), true);
  assert.equal(isManualUnitPriceWithinMaximum(999_900), true);
  assert.equal(isManualUnitPriceWithinMaximum(999_901), false);
  assert.equal(isManualUnitPriceWithinMaximum(1_000_000), false);
  // 生产 1014 店两次误输入的真实值：1 亿、1,000,006,767。
  assert.equal(isManualUnitPriceWithinMaximum(10_000_000_000), false);
  assert.equal(isManualUnitPriceWithinMaximum(100_000_676_700), false);
});
