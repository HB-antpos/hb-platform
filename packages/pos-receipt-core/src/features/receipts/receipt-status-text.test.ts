import assert from "node:assert/strict";
import test from "node:test";

import { defaultSaleStatusText } from "./receipt-document";

test("小票状态行：净额为负的退款单打印 Refund，其余打印 Paid（中英文）", () => {
  assert.equal(defaultSaleStatusText(-1), "*** Refund ***");
  assert.equal(defaultSaleStatusText(-1, true), "*** 退款 ***");
  assert.equal(defaultSaleStatusText(0), "*** Paid ***");
  assert.equal(defaultSaleStatusText(548), "*** Paid ***");
  assert.equal(defaultSaleStatusText(548, true), "*** 已支付 ***");
});
