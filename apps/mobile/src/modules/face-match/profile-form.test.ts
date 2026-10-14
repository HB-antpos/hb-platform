import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { normalizeFaceStoreProfile } from "./normalize";
import {
  PROFILE_EDITABLE_FIELDS,
  PROFILE_REQUEST_KEYS,
  PROFILE_TIME_ZONE_OPTIONS,
  buildProfileUpdateRequest,
  countMissing,
  countWarnings,
  formatAutoConfirmScore,
  gateKeyLabelKey,
  isProfileDirty,
  parseAutoConfirmScore,
  profileToEditValues,
  summarizeGates,
  summarizeProfileChange,
  validateAutoConfirmScore,
  validateProfileEdits,
} from "./profile-form";
import type { FaceStoreProfile } from "./types";

const moduleDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(moduleDir, "../../../../..");

/** 一份「合规记录填得很全」的服务端档案：手机不编辑的字段都有值。 */
function fullProfile(overrides: Record<string, unknown> = {}): FaceStoreProfile {
  return normalizeFaceStoreProfile({
    storeCode: "1008",
    storeName: "Bankstown",
    exists: true,
    mode: 2,
    evaluateMatch: false,
    staffExclusionEnabled: true,
    trafficDedupEnabled: false,
    unwelcomeListEnabled: true,
    unwelcomeAutoConfirmScore: 85.5,
    allowedSubjects: null,
    piaReference: "PIA-2026-07",
    signageInstalledOn: "2026-09-01",
    staffNoticeDate: "2026-08-10",
    legalSignoffBy: "Lawyer A",
    legalSignoffAt: "2026-09-20T03:00:00Z",
    dedupLegalSignoffBy: "Lawyer B",
    dedupLegalSignoffAt: "2026-09-21T03:00:00Z",
    crossBorderRegion: "ap-singapore",
    crossBorderReviewRef: "XB-9",
    crossBorderReviewedBy: "Reviewer C",
    crossBorderReviewedAt: "2026-09-22T03:00:00Z",
    timeZoneId: "Australia/Sydney",
    unwelcomeAssessmentRef: "UA-3",
    unwelcomeLegalSignoffBy: "Lawyer D",
    unwelcomeLegalSignoffAt: "2026-09-23T03:00:00Z",
    staffTrainingDate: "2026-09-05",
    privacyContact: "privacy@example.com",
    updatedAtUtc: "2026-10-01T00:00:00Z",
    updatedBy: "admin",
    effectiveTimeZoneId: "Australia/Sydney",
    jurisdiction: "NSW",
    crossBorderReviewValid: true,
    gates: {
      facets: [
        { facet: "match", requested: true, missing: [], warnings: ["piaReference", "signageInstalledOn"] },
        { facet: "unwelcome", requested: true, missing: [], warnings: ["staffTrainingDate"] },
        { facet: "trafficDedup", requested: false, missing: [], warnings: [] },
      ],
      canSave: true,
    },
    ...overrides,
  });
}

// ───────── PUT 是整体替换：手机不编辑的合规字段必须原样带回 ─────────
{
  const profile = fullProfile();
  const edits = { ...profileToEditValues(profile), mode: 1, allowedSubjects: "  内部员工测试  ", staffExclusionEnabled: false, timeZoneId: "Australia/Brisbane" };
  const request = buildProfileUpdateRequest(profile, edits);

  // 手机可编辑的字段被覆盖（文本去空白）
  assert.equal(request.mode, 1);
  assert.equal(request.allowedSubjects, "内部员工测试");
  assert.equal(request.staffExclusionEnabled, false);
  assert.equal(request.timeZoneId, "Australia/Brisbane");
  // 命中自动确认阈值：没改就按服务端原值（数字）带回，不丢小数
  assert.equal(request.unwelcomeAutoConfirmScore, 85.5);

  // 16 个不编辑的字段原样带回——任何一个变成 null / 空串，PUT 都会把服务端的合规记录清空
  const untouched = PROFILE_REQUEST_KEYS.filter((key) => !(PROFILE_EDITABLE_FIELDS as readonly string[]).includes(key));
  assert.equal(untouched.length, 16);
  for (const key of untouched) {
    assert.equal(request[key], profile[key], `${key} 必须原样带回服务端值`);
    assert.notEqual(request[key], null, `${key} 在这份样例档案里有值，不能被清空`);
  }
  assert.equal(request.legalSignoffAt, "2026-09-20T03:00:00Z", "时间戳字符串原样带回，不重新格式化");
  assert.equal(request.signageInstalledOn, "2026-09-01");
  assert.equal(request.crossBorderRegion, "ap-singapore");
  assert.equal(request.privacyContact, "privacy@example.com");
}

// 服务端值为 null 的字段仍然带 null，且绝不预填（例如跨境审查地域不能偷偷填成当前配置地域）
{
  const blank = fullProfile({ crossBorderRegion: null, piaReference: null, legalSignoffAt: null, staffNoticeDate: null });
  const request = buildProfileUpdateRequest(blank, profileToEditValues(blank));
  assert.equal(request.crossBorderRegion, null);
  assert.equal(request.piaReference, null);
  assert.equal(request.legalSignoffAt, null);
  assert.equal(request.staffNoticeDate, null);
}

// 请求体恰好是 24 个字段：不多（不带 storeCode / gates 等只读字段），不少
{
  const profile = fullProfile();
  const request = buildProfileUpdateRequest(profile, profileToEditValues(profile));
  assert.deepEqual(Object.keys(request).sort(), [...PROFILE_REQUEST_KEYS].sort());
  assert.equal(PROFILE_REQUEST_KEYS.length, 24);
  assert.equal("storeCode" in request, false);
  assert.equal("gates" in request, false);
  assert.equal("updatedAtUtc" in request, false);
  // 没有修改任何字段时，请求体与服务端当前档案完全等价（保存不会产生任何副作用）
  for (const key of PROFILE_REQUEST_KEYS) {
    assert.equal(request[key], profile[key] === "" ? null : profile[key], key);
  }
}

// 请求体字段与 Web 的 FaceStoreProfileUpdateRequest 一致（从 Web 类型源码读出对照）
{
  const webTypes = readFileSync(resolve(repoRoot, "apps/web/src/types/faceMatch.ts"), "utf8");
  const start = webTypes.indexOf("export interface FaceStoreProfileUpdateRequest");
  assert.ok(start >= 0, "Web 的 FaceStoreProfileUpdateRequest 找不到了：契约对照需要更新");
  const end = webTypes.indexOf("\n}", start);
  const webKeys = Array.from(webTypes.slice(start, end).matchAll(/^\s+([A-Za-z]+):\s/gm), (match) => match[1]);
  assert.deepEqual([...PROFILE_REQUEST_KEYS].sort(), [...webKeys].sort(), "移动端 PUT 请求体字段必须与 Web 完全一致");
  const backend = readFileSync(
    resolve(repoRoot, "services/backend/BlazorApp.Api/Features/FaceMatch/FaceMatchContracts.cs"),
    "utf8",
  );
  const recordStart = backend.indexOf("public sealed record FaceStoreProfileUpdateRequest");
  const recordEnd = backend.indexOf("\n}", recordStart);
  const backendKeys = Array.from(
    backend.slice(recordStart, recordEnd).matchAll(/public [A-Za-z?<>]+ ([A-Za-z]+) \{ get; init; \}/g),
    (match) => match[1][0].toLowerCase() + match[1].slice(1),
  );
  assert.deepEqual([...PROFILE_REQUEST_KEYS].sort(), [...backendKeys].sort(), "移动端 PUT 请求体字段必须与后端契约完全一致");
}

// ───────── 编辑值与脏判断 ─────────
{
  const profile = fullProfile({ allowedSubjects: "  团队内部 " });
  const values = profileToEditValues(profile);
  assert.equal(values.allowedSubjects, "  团队内部 ", "回填服务端原值（保存时才去空白）");
  assert.equal(values.timeZoneId, "Australia/Sydney");
  assert.equal(isProfileDirty(profile, values), false);
  assert.equal(isProfileDirty(profile, { ...values, allowedSubjects: "团队内部" }), false, "只差首尾空白不算修改");
  assert.equal(isProfileDirty(profile, { ...values, mode: 1 }), true);
  assert.equal(isProfileDirty(profile, { ...values, unwelcomeListEnabled: false }), true);
  assert.equal(isProfileDirty(profile, { ...values, timeZoneId: "" }), true);
  // 未建档的门店：默认值（关闭）
  const fresh = normalizeFaceStoreProfile({ storeCode: "2001", exists: false });
  assert.deepEqual(profileToEditValues(fresh), {
    mode: 0,
    evaluateMatch: false,
    staffExclusionEnabled: false,
    trafficDedupEnabled: false,
    unwelcomeListEnabled: false,
    unwelcomeAutoConfirmScore: "90",
    allowedSubjects: "",
    timeZoneId: "",
  });
}

// ───────── 本地校验：Capture 必填允许拍摄对象 ─────────
{
  const base = profileToEditValues(fullProfile());
  // 2026-10-13 起 Capture 不填允许拍摄对象也不拦（后端只给警告），本地只拦最长 200。
  assert.deepEqual(validateProfileEdits({ ...base, mode: 1, allowedSubjects: "" }), {});
  assert.deepEqual(validateProfileEdits({ ...base, mode: 1, allowedSubjects: "   " }), {});
  assert.deepEqual(validateProfileEdits({ ...base, mode: 1, allowedSubjects: "内部" }), {});
  assert.deepEqual(validateProfileEdits({ ...base, mode: 2, allowedSubjects: "" }), {});
  assert.deepEqual(validateProfileEdits({ ...base, mode: 0, allowedSubjects: "" }), {});
  assert.equal(validateProfileEdits({ ...base, mode: 2, allowedSubjects: "x".repeat(201) }).allowedSubjects?.key, "validation.textTooLong");
  assert.deepEqual(validateProfileEdits({ ...base, mode: 1, allowedSubjects: "x".repeat(200) }), {});
}

// ───────── 时区选项：自动 + 后端允许的三个 ─────────
assert.deepEqual(
  PROFILE_TIME_ZONE_OPTIONS.map((option) => option.value),
  ["", "Australia/Sydney", "Australia/Brisbane", "Australia/Melbourne"],
);

// ───────── 闸门报告：警告条数、缺项条数 ─────────
{
  const profile = fullProfile();
  const rows = summarizeGates(profile.gates);
  assert.deepEqual(rows.map((row) => row.facet), ["match", "unwelcome"], "未请求启用的 facet 不展示");
  assert.equal(countWarnings(rows), 3, "警告条数是各 facet 之和（与服务端 gateWarningCount 同口径）");
  assert.equal(countMissing(rows), 0);
  assert.equal(rows[0].facetLabelKey, "settings.gate.facets.match");
  assert.deepEqual(summarizeGates(null), []);
  const blocked = summarizeGates(
    fullProfile({ gates: { facets: [{ facet: "capture", requested: true, missing: ["allowedSubjects"] }], canSave: false } }).gates,
  );
  assert.equal(countMissing(blocked), 1);
  assert.equal(gateKeyLabelKey("allowedSubjects"), "settings.gate.keys.allowedSubjects");
  assert.equal(gateKeyLabelKey("someFutureKey"), null, "未登记的键返回 null，由页面显示键名本身，不隐藏缺项");
}

// ───────── 保存前确认框的变化摘要 ─────────
{
  const profile = fullProfile({ mode: 0, staffExclusionEnabled: false, unwelcomeListEnabled: false });
  const values = profileToEditValues(profile);
  const toMatch = summarizeProfileChange(profile, { ...values, mode: 2, unwelcomeListEnabled: true });
  assert.equal(toMatch.modeChanged, true);
  assert.equal(toMatch.startsProcessing, true, "从关闭切到 Match 会开始处理真实抓拍，确认框要强调");
  assert.deepEqual(toMatch.switchesChanged, ["unwelcomeListEnabled"]);
  const toOff = summarizeProfileChange(fullProfile({ mode: 2 }), { ...profileToEditValues(fullProfile({ mode: 2 })), mode: 0 });
  assert.equal(toOff.startsProcessing, false, "关闭处理不需要额外强调");
  const unchanged = summarizeProfileChange(profile, values);
  assert.equal(unchanged.modeChanged, false);
  assert.deepEqual(unchanged.switchesChanged, []);
}

console.log("profile-form.test.ts: ok");

// ───────── 命中自动确认阈值：解析、校验、请求体、脏判断、确认摘要 ─────────
{
  // 解析：非负十进制、最多两位小数；逗号小数点按点处理；其余一律 null
  assert.equal(parseAutoConfirmScore("90"), 90);
  assert.equal(parseAutoConfirmScore(" 85.5 "), 85.5);
  assert.equal(parseAutoConfirmScore("89,99"), 89.99);
  assert.equal(parseAutoConfirmScore("0"), 0);
  assert.equal(parseAutoConfirmScore("100"), 100);
  for (const bad of ["", "  ", "abc", "-5", "1e2", "90.123", "90.", ".5", "1000", "9 0", "90%"]) {
    assert.equal(parseAutoConfirmScore(bad), null, `「${bad}」不是合法的阈值文本`);
  }
  assert.equal(formatAutoConfirmScore(90), "90");
  assert.equal(formatAutoConfirmScore(89.5), "89.5");
  assert.equal(formatAutoConfirmScore(0), "0");

  // 校验：0 = 关闭；50–100；空串 = 必填；其余 invalid（与后端 / Web 同一口径）
  assert.equal(validateAutoConfirmScore("0"), null);
  assert.equal(validateAutoConfirmScore("50"), null);
  assert.equal(validateAutoConfirmScore("90"), null);
  assert.equal(validateAutoConfirmScore("99.99"), null);
  assert.equal(validateAutoConfirmScore("100"), null);
  assert.equal(validateAutoConfirmScore("")?.key, "validation.autoConfirmScoreRequired");
  assert.equal(validateAutoConfirmScore("   ")?.key, "validation.autoConfirmScoreRequired");
  for (const bad of ["49.99", "1", "100.01", "101", "90.123", "-1", "abc"]) {
    const issue = validateAutoConfirmScore(bad);
    assert.equal(issue?.key, "validation.autoConfirmScoreInvalid", bad);
    assert.deepEqual(issue?.params, { min: 50, max: 100 });
  }

  // 编辑值回填、请求体、脏判断
  const profile = fullProfile();
  const values = profileToEditValues(profile);
  assert.equal(values.unwelcomeAutoConfirmScore, "85.5");
  assert.equal(isProfileDirty(profile, values), false);
  assert.equal(isProfileDirty(profile, { ...values, unwelcomeAutoConfirmScore: "85.50" }), false, "数值相同不算修改");
  assert.equal(isProfileDirty(profile, { ...values, unwelcomeAutoConfirmScore: "90" }), true);
  assert.equal(isProfileDirty(profile, { ...values, unwelcomeAutoConfirmScore: "0" }), true);
  assert.equal(isProfileDirty(profile, { ...values, unwelcomeAutoConfirmScore: "" }), true, "清空也算修改（校验会拦住保存）");
  assert.equal(buildProfileUpdateRequest(profile, { ...values, unwelcomeAutoConfirmScore: "92" }).unwelcomeAutoConfirmScore, 92);
  assert.equal(buildProfileUpdateRequest(profile, { ...values, unwelcomeAutoConfirmScore: "0" }).unwelcomeAutoConfirmScore, 0, "0 是合法值（关闭），不能被当成「没填」");
  assert.equal(
    buildProfileUpdateRequest(profile, { ...values, unwelcomeAutoConfirmScore: "abc" }).unwelcomeAutoConfirmScore,
    null,
    "不合法文本发 null（服务端保持原值），绝不改写成别的数字",
  );

  // validateProfileEdits：阈值不合法要报错，且与允许拍摄对象的错误互不影响
  assert.equal(validateProfileEdits({ ...values, unwelcomeAutoConfirmScore: "49" }).unwelcomeAutoConfirmScore?.key, "validation.autoConfirmScoreInvalid");
  assert.equal(validateProfileEdits({ ...values, unwelcomeAutoConfirmScore: "" }).unwelcomeAutoConfirmScore?.key, "validation.autoConfirmScoreRequired");
  assert.deepEqual(validateProfileEdits({ ...values, unwelcomeAutoConfirmScore: "0" }), {});
  assert.equal(validateProfileEdits({ ...values, allowedSubjects: "x".repeat(201) }).unwelcomeAutoConfirmScore, undefined);

  // 保存前确认摘要：阈值有改动才有 autoConfirmChange
  assert.deepEqual(summarizeProfileChange(profile, { ...values, unwelcomeAutoConfirmScore: "90" }).autoConfirmChange, { from: 85.5, to: 90 });
  assert.deepEqual(summarizeProfileChange(profile, { ...values, unwelcomeAutoConfirmScore: "0" }).autoConfirmChange, { from: 85.5, to: 0 });
  assert.equal(summarizeProfileChange(profile, values).autoConfirmChange, null);
  assert.equal(summarizeProfileChange(profile, { ...values, unwelcomeAutoConfirmScore: "85.50" }).autoConfirmChange, null);
  assert.equal(summarizeProfileChange(profile, { ...values, unwelcomeAutoConfirmScore: "abc" }).autoConfirmChange, null);
}
