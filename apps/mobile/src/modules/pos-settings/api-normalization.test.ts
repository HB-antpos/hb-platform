import assert from "node:assert/strict";
import {
  buildSaveBody,
  normalizeBooleanMap,
  normalizePosSettingDefinition,
  normalizePosSettingDeviceDetail,
  normalizePosSettingDeviceList,
  normalizePosSettingEffective,
  normalizePosSettingLayer,
  normalizePosSettingStoreDetail,
  normalizePosSettingStoreList,
  normalizePosSettingsCatalog,
} from "./api";

// ───────── 目录 ─────────
{
  const catalog = normalizePosSettingsCatalog({
    definitions: [
      { key: "cashMovement.enabled", clientKinds: ["WPF", "ipad", "handheld"], defaultWhenUnset: false, isNew: true },
      { key: "returns.rescan", clientKinds: ["wpf"], defaultWhenUnset: null, isNew: false },
      { key: "returns.rescan", clientKinds: ["wpf"] }, // 重复键丢弃
      { key: "", clientKinds: [] }, // 空键丢弃
    ],
  });
  assert.equal(catalog.length, 2);
  assert.deepEqual(catalog[0], { key: "cashMovement.enabled", clientKinds: ["wpf", "ipad", "handheld"], defaultWhenUnset: false, isNew: true });
  assert.equal(catalog[1].defaultWhenUnset, null, "null 缺省 = 沿用本机");
  assert.deepEqual(normalizePosSettingsCatalog(null), []);
  assert.deepEqual(normalizePosSettingsCatalog({}), []);
  // 缺字段给稳定默认值
  assert.deepEqual(normalizePosSettingDefinition({ key: "x" }), { key: "x", clientKinds: [], defaultWhenUnset: null, isNew: false });
}

// ───────── 已设置项字典：只保留布尔 ─────────
assert.deepEqual(
  normalizeBooleanMap({ a: true, b: false, c: "true", d: "FALSE", e: null, f: 1, g: "yes", "": true }),
  { a: true, b: false, c: true, d: false },
);
assert.deepEqual(normalizeBooleanMap(undefined), {});
assert.deepEqual(normalizeBooleanMap([true]), {}, "数组不是字典");

// ───────── 层（全局） ─────────
{
  const layer = normalizePosSettingLayer({
    scope: "global",
    scopeKey: "*",
    values: { "cashMovement.enabled": true },
    updatedAtUtc: "2026-11-13T22:30:00",
    updatedByName: "  Sean ",
  });
  assert.deepEqual(layer.values, { "cashMovement.enabled": true });
  assert.equal(layer.updatedAtUtc, "2026-11-13T22:30:00Z", "没有时区标记的时间补 Z");
  assert.equal(layer.updatedByName, "Sean");
  const empty = normalizePosSettingLayer(undefined);
  assert.deepEqual(empty, { scope: "", scopeKey: "", values: {}, updatedAtUtc: null, updatedByName: null });
  assert.equal(normalizePosSettingLayer({ updatedAtUtc: "2026-11-13T22:30:00+10:00" }).updatedAtUtc, "2026-11-13T22:30:00+10:00", "已带时区的不再补 Z");
}

// ───────── 生效值 ─────────
assert.deepEqual(normalizePosSettingEffective({ value: true, source: "store" }), { value: true, source: "store" });
assert.deepEqual(normalizePosSettingEffective({ value: false, source: "DEFAULT" }), { value: false, source: "default" });
assert.deepEqual(normalizePosSettingEffective({ value: null, source: "global" }), { value: null, source: "none" }, "没有值一定是 none");
assert.deepEqual(normalizePosSettingEffective({ value: true, source: "weird" }), { value: true, source: "none" }, "未知来源不冒充某一层");
assert.deepEqual(normalizePosSettingEffective(undefined), { value: null, source: "none" });

// ───────── 设备列表与状态 ─────────
{
  const devices = normalizePosSettingDeviceList([
    {
      deviceCode: "ORI-01",
      storeCode: "ORI",
      clientKind: "WPF",
      appVersion: "1.0.120",
      status: "applied",
      appliedAtUtc: "2026-11-14T02:02:00Z",
      hasDeviceOverride: true,
      offlineOverrideCount: "2",
      lastOfflineOverrideAtUtc: "2026-11-13T20:00:00",
    },
    { deviceCode: "ORI-02", storeCode: "ORI", clientKind: "tablet", status: "something-new", offlineOverrideCount: -3 },
    { deviceCode: "", storeCode: "ORI" }, // 无编号丢弃
  ]);
  assert.equal(devices.length, 2);
  assert.equal(devices[0].clientKind, "wpf");
  assert.equal(devices[0].status, "applied");
  assert.equal(devices[0].offlineOverrideCount, 2, "数字字符串转数字");
  assert.equal(devices[0].lastOfflineOverrideAtUtc, "2026-11-13T20:00:00Z");
  assert.equal(devices[0].hasDeviceOverride, true);
  assert.equal(devices[1].clientKind, "other", "未知端归 other");
  assert.equal(devices[1].status, "never", "未知状态按尚未同步，不冒充已应用");
  assert.equal(devices[1].offlineOverrideCount, 0, "负数归零");
  assert.equal(devices[1].appVersion, null);
  assert.deepEqual(normalizePosSettingDeviceList(null), []);
}

// ───────── 门店列表 / 详情 ─────────
{
  const stores = normalizePosSettingStoreList([
    { storeCode: "ORI", storeName: "Orion", overrides: { "cashMovement.enabled": true, x: null }, deviceOverrideCount: 1, deviceTotal: 3, deviceApplied: 2 },
    { storeName: "无编码丢弃" },
  ]);
  assert.equal(stores.length, 1);
  assert.deepEqual(stores[0].overrides, { "cashMovement.enabled": true });
  assert.equal(stores[0].deviceApplied, 2);

  const detail = normalizePosSettingStoreDetail({
    storeCode: "ORI",
    storeName: "Orion",
    overrides: { "cashMovement.enabled": true },
    effective: {
      "cashMovement.enabled": { value: true, source: "store" },
      "returns.rescan": { value: null, source: "none" },
    },
    devices: [{ deviceCode: "ORI-01", storeCode: "ORI", clientKind: "wpf", status: "pending" }],
    updatedAtUtc: null,
    updatedByName: null,
  });
  assert.equal(detail.effective["cashMovement.enabled"].source, "store");
  assert.equal(detail.effective["returns.rescan"].value, null);
  assert.equal(detail.devices[0].status, "pending");
  assert.deepEqual(normalizePosSettingStoreDetail(undefined).devices, []);
}

// ───────── 设备详情 ─────────
{
  const detail = normalizePosSettingDeviceDetail({
    status: { deviceCode: "ORI-01", storeCode: "ORI", clientKind: "wpf", status: "pending" },
    storeName: "Orion",
    overrides: { "payment.voucher": true },
    effective: { "payment.voucher": { value: true, source: "device" } },
    reported: { "payment.voucher": false, bad: null },
  });
  assert.equal(detail.status.deviceCode, "ORI-01");
  assert.deepEqual(detail.reported, { "payment.voucher": false });
  assert.equal(detail.effective["payment.voucher"].source, "device");
  const empty = normalizePosSettingDeviceDetail(undefined);
  assert.equal(empty.status.status, "never");
  assert.deepEqual(empty.reported, {});
}

// ───────── 保存请求体：null = 清除，必须原样保留，不能被丢掉 ─────────
{
  const body = buildSaveBody({ "cashMovement.enabled": true, "returns.rescan": null, "payment.voucher": false });
  assert.deepEqual(body, { values: { "cashMovement.enabled": true, "returns.rescan": null, "payment.voucher": false } });
  assert.equal(JSON.stringify(body), '{"values":{"cashMovement.enabled":true,"returns.rescan":null,"payment.voucher":false}}');
}

console.log("api-normalization.test.ts: ok");
