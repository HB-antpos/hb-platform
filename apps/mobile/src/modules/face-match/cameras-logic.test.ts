import assert from "node:assert/strict";
import {
  buildCameraEnabledRequest,
  cameraDisplayName,
  countCameraStatuses,
  filterCamerasByStore,
  getCameraStatus,
  hasParseFailures,
} from "./cameras-logic";
import { normalizeFaceCamera } from "./normalize";

function camera(overrides: Record<string, unknown> = {}) {
  return normalizeFaceCamera({
    cameraId: "store1008-door-01",
    storeCode: "1008",
    storeName: "Bankstown",
    deviceModel: "DS-2CD6",
    macAddress: "AA:BB:CC:DD:EE:FF",
    note: "Front door",
    enabled: true,
    online: true,
    captureCount24h: 31,
    parseFailureCount24h: 0,
    lastSeenAtUtc: "2026-10-13T01:00:00Z",
    ...overrides,
  });
}

// ───────── 推送状态：不是在线 / 离线 ─────────
assert.equal(getCameraStatus({ enabled: true, online: true }), "recentlyActive");
assert.equal(getCameraStatus({ enabled: true, online: false }), "noRecentPush");
assert.equal(getCameraStatus({ enabled: false, online: true }), "disabled", "停用优先于推送状态");
assert.equal(getCameraStatus({ enabled: false, online: false }), "disabled");

// 状态文案键里不出现在线 / 离线字样
{
  const keys = JSON.stringify(
    [getCameraStatus({ enabled: true, online: true }), getCameraStatus({ enabled: true, online: false }), getCameraStatus({ enabled: false, online: false })],
  );
  assert.equal(/online|offline|在线|离线/i.test(keys), false);
}

// ───────── 按门店过滤并稳定排序 ─────────
{
  const list = [camera({ cameraId: "b-02", storeCode: "1008" }), camera({ cameraId: "a-01", storeCode: "1008" }), camera({ cameraId: "c-03", storeCode: "2001" })];
  assert.deepEqual(filterCamerasByStore(list, "1008").map((c) => c.cameraId), ["a-01", "b-02"]);
  assert.deepEqual(filterCamerasByStore(list, "2001").map((c) => c.cameraId), ["c-03"]);
  assert.deepEqual(filterCamerasByStore(list, "9999"), []);
  assert.deepEqual(filterCamerasByStore(list, null), [], "没选门店不显示任何摄像头");
}

// ───────── 名称、解析失败警示 ─────────
assert.equal(cameraDisplayName(camera()), "Front door");
assert.equal(cameraDisplayName(camera({ note: null })), "store1008-door-01");
assert.equal(cameraDisplayName(camera({ note: "   " })), "store1008-door-01");
assert.equal(hasParseFailures(camera({ parseFailureCount24h: 2 })), true);
assert.equal(hasParseFailures(camera()), false);

// ───────── 启停请求体：PUT 整体替换，除 enabled 外原样带回，绝不清空 ─────────
{
  const original = camera();
  const disable = buildCameraEnabledRequest(original, false);
  assert.deepEqual(disable, {
    storeCode: "1008",
    note: "Front door",
    deviceModel: "DS-2CD6",
    macAddress: "AA:BB:CC:DD:EE:FF",
    enabled: false,
  });
  assert.deepEqual(Object.keys(disable).sort(), ["deviceModel", "enabled", "macAddress", "note", "storeCode"]);
  const enable = buildCameraEnabledRequest(camera({ enabled: false, note: null, macAddress: null }), true);
  assert.deepEqual(enable, { storeCode: "1008", note: null, deviceModel: "DS-2CD6", macAddress: null, enabled: true });
  // 请求体里没有令牌 / 地址之类的字段
  assert.equal(Object.keys(enable).some((key) => /token|url/i.test(key)), false);
}

// ───────── 状态计数 ─────────
assert.deepEqual(
  countCameraStatuses([
    { enabled: true, online: true },
    { enabled: true, online: false },
    { enabled: false, online: true },
    { enabled: false, online: false },
  ]),
  { total: 4, recentlyActive: 1, noRecentPush: 1, disabled: 2 },
);

console.log("cameras-logic.test.ts: ok");
