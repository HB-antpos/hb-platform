import assert from "node:assert/strict";
import {
  CAMERA_CAPTURE_MAX_PAGE,
  EMPTY_CAMERA_FORM,
  buildCameraEnabledRequest,
  buildCreateCameraRequest,
  buildEditCameraRequest,
  cameraDisplayName,
  cameraImageLabel,
  cameraParseStatusLabelKey,
  cameraParseStatusTone,
  countCameraStatuses,
  filterCamerasByStore,
  getCameraStatus,
  hasParseFailures,
  hasViewableImages,
  isCameraFormChanged,
  isCameraImageViewable,
  nextCapturePage,
  normalizeOptionalText,
  toCameraFormValues,
  validateCameraForm,
  type CameraFormValues,
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

// ───────── 表单校验（与后端、网页后台同一套规则） ─────────
const good: CameraFormValues = { cameraId: "store1008-door-01", storeCode: "1008", note: "前门", deviceModel: "DS-2CD6", macAddress: "AA:BB" };
assert.deepEqual(validateCameraForm(good, "create"), {});
assert.deepEqual(validateCameraForm(EMPTY_CAMERA_FORM, "create"), {
  cameraId: "settings.cameras.validation.cameraIdRequired",
  storeCode: "settings.cameras.validation.storeCodeRequired",
});
for (const bad of ["Store1008", "-door", "door_01", "a".repeat(65), "门店-1", "door 01"]) {
  assert.equal(validateCameraForm({ ...good, cameraId: bad }, "create").cameraId, "settings.cameras.validation.cameraIdInvalid", `编号 ${JSON.stringify(bad)} 不合法`);
}
for (const ok of ["a", "0cam", "store1008-door-01", "a".repeat(64)]) {
  assert.equal(validateCameraForm({ ...good, cameraId: ok }, "create").cameraId, undefined, `编号 ${ok} 合法`);
}
assert.equal(validateCameraForm({ ...good, cameraId: "" }, "edit").cameraId, undefined, "编辑模式不校验编号（不可改）");
assert.equal(validateCameraForm({ ...good, storeCode: "a".repeat(33) }, "create").storeCode, "settings.cameras.validation.storeCodeTooLong");
assert.equal(validateCameraForm({ ...good, storeCode: "10 08" }, "create").storeCode, "settings.cameras.validation.storeCodeInvalid");
assert.equal(validateCameraForm({ ...good, storeCode: "ST_08-a" }, "create").storeCode, undefined);
assert.equal(validateCameraForm({ ...good, note: "x".repeat(201) }, "create").note, "settings.cameras.validation.noteTooLong");
assert.equal(validateCameraForm({ ...good, note: "x".repeat(200) }, "create").note, undefined);
assert.equal(validateCameraForm({ ...good, deviceModel: "x".repeat(65) }, "create").deviceModel, "settings.cameras.validation.deviceModelTooLong");
assert.equal(validateCameraForm({ ...good, macAddress: "x".repeat(33) }, "create").macAddress, "settings.cameras.validation.macAddressTooLong");

// ───────── 请求体：去首尾空白，空串转 null ─────────
assert.equal(normalizeOptionalText("  "), null);
assert.equal(normalizeOptionalText(" a "), "a");
assert.deepEqual(buildCreateCameraRequest({ cameraId: "  door-1 ", storeCode: " 1008 ", note: "  ", deviceModel: " M1 ", macAddress: "" }), {
  cameraId: "door-1",
  storeCode: "1008",
  note: null,
  deviceModel: "M1",
  macAddress: null,
});
{
  // 编辑请求体：整体替换，enabled 沿用传入值，没有编号字段（编号不可改）
  const request = buildEditCameraRequest({ ...good, note: "" }, false);
  assert.deepEqual(request, { storeCode: "1008", note: null, deviceModel: "DS-2CD6", macAddress: "AA:BB", enabled: false });
  assert.equal("cameraId" in request, false);
  assert.equal(Object.keys(request).some((key) => /token|url/i.test(key)), false);
}
{
  const form = toCameraFormValues(camera({ note: null, deviceModel: null, macAddress: null }));
  assert.deepEqual(form, { cameraId: "store1008-door-01", storeCode: "1008", note: "", deviceModel: "", macAddress: "" }, "null 展示成空串");
  assert.equal(isCameraFormChanged(form, { ...form }), false);
  assert.equal(isCameraFormChanged(form, { ...form, note: "  " }), false, "只多了空白不算改动");
  assert.equal(isCameraFormChanged(form, { ...form, note: "前门" }), true);
  assert.equal(isCameraFormChanged(form, { ...form, storeCode: "1009" }), true);
  assert.equal(isCameraFormChanged(form, { ...form, macAddress: "AA" }), true);
}

// ───────── 最近抓拍：分页 ─────────
assert.equal(nextCapturePage({ page: 1, hasMore: true }), 2);
assert.equal(nextCapturePage({ page: 1, hasMore: false }), undefined);
assert.equal(nextCapturePage({ page: CAMERA_CAPTURE_MAX_PAGE, hasMore: true }), undefined, "到页码上限不再提供下一页");
assert.equal(nextCapturePage({ page: CAMERA_CAPTURE_MAX_PAGE - 1, hasMore: true }), CAMERA_CAPTURE_MAX_PAGE);

// ───────── 最近抓拍：解析状态与图片展示 ─────────
assert.equal(cameraParseStatusLabelKey(0), "settings.cameras.captures.parse.parsed");
assert.equal(cameraParseStatusLabelKey(3), "settings.cameras.captures.parse.notFace");
assert.equal(cameraParseStatusLabelKey(null), "settings.cameras.captures.parse.unknown", "不认识的状态显示「未知」");
assert.deepEqual([0, 1, 2, 3, null].map((status) => cameraParseStatusTone(status as 0 | 1 | 2 | 3 | null)), ["success", "warning", "danger", "neutral", "neutral"]);
assert.equal(isCameraImageViewable({ storage: "cos" }), true);
assert.equal(isCameraImageViewable({ storage: "local" }), false, "还在接收服务本地磁盘的图片后台读不到");
assert.equal(isCameraImageViewable({ storage: "LOCAL" }), false);
assert.equal(hasViewableImages({ images: [] }), false);
assert.equal(hasViewableImages({ images: [{ imageId: "i", kind: 0, storage: "local", width: null, height: null, byteSize: 1, faceIndex: 0 }] }), false);
assert.equal(hasViewableImages({ images: [{ imageId: "i", kind: 0, storage: "cos", width: null, height: null, byteSize: 1, faceIndex: 0 }] }), true);
assert.deepEqual(cameraImageLabel({ kind: 0, faceIndex: 0 }, 5), { key: "settings.cameras.captures.imageFace", values: { index: 1 } }, "人脸图序号从 1 起，取 faceIndex");
assert.deepEqual(cameraImageLabel({ kind: 0, faceIndex: null }, 1), { key: "settings.cameras.captures.imageFace", values: { index: 2 } });
assert.deepEqual(cameraImageLabel({ kind: 1, faceIndex: null }, 0), { key: "settings.cameras.captures.imageBackground", values: { index: 1 } });
assert.deepEqual(cameraImageLabel({ kind: 2, faceIndex: null }, 2), { key: "settings.cameras.captures.imageOther", values: { index: 3 } });

console.log("cameras-logic.test.ts: ok");
