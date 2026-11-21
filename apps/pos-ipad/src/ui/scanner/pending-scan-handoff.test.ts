import assert from "node:assert/strict";
import test from "node:test";

import {
  offerPendingSalesScan,
  PENDING_SALES_SCAN_MAX_AGE_MS,
  takePendingSalesScan,
} from "./pending-scan-handoff";

test("交接码只能取走一次，取走后清空", () => {
  takePendingSalesScan(0);
  assert.equal(takePendingSalesScan(0), null);

  offerPendingSalesScan("9313559558795", "ble", 1_000);
  assert.deepEqual(takePendingSalesScan(1_500), {
    value: "9313559558795",
    source: "ble",
    atMs: 1_000,
  });
  assert.equal(takePendingSalesScan(1_500), null);
});

test("只保留最近一个码：切页期间扫到第二个码以最新的为准", () => {
  offerPendingSalesScan("FIRST", "hid", 1_000);
  offerPendingSalesScan("SECOND", "ble", 1_200);
  assert.equal(takePendingSalesScan(1_300)?.value, "SECOND");
});

test("超过有效期的码被丢弃，不会在很久以后回到收银页时误加购", () => {
  offerPendingSalesScan("STALE", "ble", 1_000);
  assert.equal(
    takePendingSalesScan(1_000 + PENDING_SALES_SCAN_MAX_AGE_MS + 1),
    null,
  );
  // 过期的码同样被清掉，之后不会再被取到。
  assert.equal(takePendingSalesScan(1_000), null);

  // 恰好等于有效期仍然有效。
  offerPendingSalesScan("EDGE", "hid", 2_000);
  assert.equal(
    takePendingSalesScan(2_000 + PENDING_SALES_SCAN_MAX_AGE_MS)?.value,
    "EDGE",
  );
});
