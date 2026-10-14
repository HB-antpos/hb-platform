import assert from "node:assert/strict";
import {
  canSubmitEntries,
  canViewFaceCaptureResults,
  canViewFaceMatchSettings,
  canViewFaceUnwelcome,
  getVisibleUnwelcomeTabs,
  isWriteBlocked,
  resolveSettingsSections,
} from "./access";
import { FACE_MATCH_PERMISSIONS } from "./constants";
import { normalizeFaceMatchConfig } from "./normalize";

const has = (...codes: string[]) => (permission: string) => codes.includes(permission);

// 权限码与后端 Permissions.FaceMatch.* / FaceCapture.Cameras.Manage 一致（本期不新增权限码）
assert.deepEqual(FACE_MATCH_PERMISSIONS, {
  manage: "FaceMatch.Manage",
  unwelcomeManage: "FaceMatch.Unwelcome.Manage",
  unwelcomeApprove: "FaceMatch.Unwelcome.Approve",
  unwelcomeReview: "FaceMatch.Unwelcome.Review",
  camerasManage: "FaceCapture.Cameras.Manage",
  imagesView: "FaceCapture.Images.View",
});

// ───────── 名单页入口：名单 Manage / Approve / Review 任一 ─────────
for (const code of ["FaceMatch.Unwelcome.Manage", "FaceMatch.Unwelcome.Approve", "FaceMatch.Unwelcome.Review"]) {
  assert.equal(canViewFaceUnwelcome(true, has(code), false), true, code);
}
// 档案管理 / 摄像机管理 / 客流统计 / 抓拍图查看都不能打开名单页
for (const code of ["FaceMatch.Manage", "FaceCapture.Cameras.Manage", "FaceMatch.Traffic.View", "FaceCapture.Images.View"]) {
  assert.equal(canViewFaceUnwelcome(true, has(code), false), false, code);
}
assert.equal(canViewFaceUnwelcome(false, has("FaceMatch.Unwelcome.Review"), false), false, "未登录");
assert.equal(canViewFaceUnwelcome(true, has("FaceMatch.Unwelcome.Review"), true), false, "iOS 审核离线会话不进入");
assert.equal(canViewFaceUnwelcome(true, has(), false), false);

// ───────── 设置页入口：FaceMatch.Manage 或 Cameras.Manage 任一 ─────────
assert.equal(canViewFaceMatchSettings(true, has("FaceMatch.Manage"), false), true);
assert.equal(canViewFaceMatchSettings(true, has("FaceCapture.Cameras.Manage"), false), true);
for (const code of ["FaceMatch.Unwelcome.Manage", "FaceMatch.Unwelcome.Approve", "FaceMatch.Unwelcome.Review", "FaceMatch.Traffic.View"]) {
  assert.equal(canViewFaceMatchSettings(true, has(code), false), false, code);
}
assert.equal(canViewFaceMatchSettings(false, has("FaceMatch.Manage"), false), false);
assert.equal(canViewFaceMatchSettings(true, has("FaceMatch.Manage"), true), false);

// ───────── 设置页两个区域各自需要自己的权限 ─────────
assert.deepEqual(resolveSettingsSections(has("FaceMatch.Manage")), { profile: true, cameras: false });
assert.deepEqual(resolveSettingsSections(has("FaceCapture.Cameras.Manage")), { profile: false, cameras: true });
assert.deepEqual(resolveSettingsSections(has("FaceMatch.Manage", "FaceCapture.Cameras.Manage")), { profile: true, cameras: true });
assert.deepEqual(resolveSettingsSections(has()), { profile: false, cameras: false });

// ───────── 名单页分区按 /config 权限标志 ─────────
{
  const flags = (partial: Record<string, boolean>) => normalizeFaceMatchConfig({ enabled: true, permissions: partial }).permissions;
  assert.deepEqual(getVisibleUnwelcomeTabs(flags({ unwelcomeManage: true })), ["entries"]);
  assert.deepEqual(getVisibleUnwelcomeTabs(flags({ unwelcomeApprove: true })), ["entries"]);
  assert.deepEqual(getVisibleUnwelcomeTabs(flags({ unwelcomeReview: true })), ["review"]);
  assert.deepEqual(getVisibleUnwelcomeTabs(flags({ unwelcomeManage: true, unwelcomeReview: true })), ["entries", "review"]);
  assert.deepEqual(getVisibleUnwelcomeTabs(flags({ manage: true, trafficView: true })), [], "档案管理 / 客流统计不产生名单分区");
  assert.deepEqual(getVisibleUnwelcomeTabs(flags({})), []);
}

// ───────── 只读与提交条件 ─────────
assert.equal(isWriteBlocked(null), true);
assert.equal(isWriteBlocked({ enabled: false }), true);
assert.equal(isWriteBlocked({ enabled: true }), false);
{
  const base = { enabled: true, objectStoreConfigured: true, permissions: { manage: false, trafficView: false, unwelcomeManage: true, unwelcomeApprove: false, unwelcomeReview: false } };
  assert.equal(canSubmitEntries(base), true);
  assert.equal(canSubmitEntries({ ...base, enabled: false }), false);
  assert.equal(canSubmitEntries({ ...base, objectStoreConfigured: false }), false, "私有桶未配置不能提交带照片的条目");
  assert.equal(canSubmitEntries({ ...base, permissions: { ...base.permissions, unwelcomeManage: false } }), false);
  assert.equal(canSubmitEntries(null), false);
}

console.log("access.test.ts: ok");

// ───────── 「抓拍与比对」入口：只认 FaceCapture.Images.View（结果里的照片是生物识别信息） ─────────
assert.equal(canViewFaceCaptureResults(true, has("FaceCapture.Images.View"), false), true);
for (const code of ["FaceMatch.Manage", "FaceMatch.Unwelcome.Review", "FaceCapture.Cameras.Manage", "FaceMatch.Traffic.View"]) {
  assert.equal(canViewFaceCaptureResults(true, has(code), false), false, `${code} 不能打开抓拍与比对`);
}
assert.equal(canViewFaceCaptureResults(false, has("FaceCapture.Images.View"), false), false, "未登录不可见");
assert.equal(canViewFaceCaptureResults(true, has("FaceCapture.Images.View"), true), false, "iOS 审核模式不可见");
