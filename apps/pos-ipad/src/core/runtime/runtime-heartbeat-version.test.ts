import assert from "node:assert/strict";
import test from "node:test";

import { formatIpadHeartbeatAppVersion } from "./runtime-heartbeat-version";

const embedded = { updateId: "01a215d1-fdd8-779c-bb52-253c500fb602", isEmbeddedLaunch: true };
const ota = { updateId: "01a215d1-fdd8-779c-bb52-253c500fb602", isEmbeddedLaunch: false };

test("心跳版本带上原生 build 号；跑 OTA 时再带 OTA 编号", () => {
  assert.equal(formatIpadHeartbeatAppVersion("0.2.0", "22", embedded), "0.2.0 (22)");
  assert.equal(formatIpadHeartbeatAppVersion(" 0.2.0 ", " 22 ", ota), "0.2.0 (22)+ota.01a215d1");
  assert.ok(formatIpadHeartbeatAppVersion("0.2.0", "2147483647", ota).length <= 50);
});

test("build 号缺失或异常时保持旧格式", () => {
  for (const build of [null, undefined, "", "abc", "22a", "12345678901"]) {
    assert.equal(formatIpadHeartbeatAppVersion("0.2.0", build, ota), "0.2.0+ota.01a215d1", String(build));
  }
});
