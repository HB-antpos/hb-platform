import assert from "node:assert/strict";
import {
  buildCreateEntryInput,
  createEmptyEntryForm,
  hasEntryFormErrors,
  isEntryFormDirty,
  normalizeText,
  validateDisputeNote,
  validateEntryForm,
  validateExpiry,
  validatePhotoSet,
  validateWithdrawReason,
  type EntryFormValues,
} from "./entry-form";
import type { FacePhotoDraft } from "./types";

const NOW = new Date(2026, 9, 13, 12, 0, 0, 0); // 2026-10-13 本地中午

function photo(overrides: Partial<FacePhotoDraft> = {}): FacePhotoDraft {
  return { key: "p1", base64: "AAAA", byteSize: 1024, width: 800, height: 600, previewDataUri: "data:image/jpeg;base64,AAAA", ...overrides };
}

function valid(overrides: Partial<EntryFormValues> = {}): EntryFormValues {
  return {
    incidentCategory: 1,
    categoryNote: "",
    evidenceRef: "INC-2026-001",
    storeScope: ["1008"],
    expiresOn: "2026-12-31",
    ...overrides,
  };
}

// ───────── 通过的基线 ─────────
assert.deepEqual(validateEntryForm(valid(), [photo()], NOW), {});

// ───────── 事件类别：枚举，没有「一般偷窃」 ─────────
assert.equal(validateEntryForm(valid({ incidentCategory: null }), [photo()], NOW).incidentCategory?.key, "validation.categoryRequired");
assert.equal(validateEntryForm(valid({ incidentCategory: 4 }), [photo()], NOW).incidentCategory?.key, "validation.categoryRequired", "不存在的类别（如一般偷窃）被拒绝");
assert.equal(validateEntryForm(valid({ incidentCategory: 0 }), [photo()], NOW).incidentCategory?.key, "validation.categoryRequired");
for (const category of [1, 2, 3]) {
  const errors = validateEntryForm(valid({ incidentCategory: category, categoryNote: category === 3 ? "说明" : "" }), [photo()], NOW);
  assert.equal(errors.incidentCategory, undefined);
}

// ───────── Other 必须说明；说明 ≤200 ─────────
assert.equal(validateEntryForm(valid({ incidentCategory: 3, categoryNote: "   " }), [photo()], NOW).categoryNote?.key, "validation.categoryNoteRequired");
assert.equal(validateEntryForm(valid({ incidentCategory: 3, categoryNote: "x".repeat(200) }), [photo()], NOW).categoryNote, undefined);
assert.equal(validateEntryForm(valid({ incidentCategory: 3, categoryNote: "x".repeat(201) }), [photo()], NOW).categoryNote?.key, "validation.categoryNoteTooLong");
assert.equal(validateEntryForm(valid({ incidentCategory: 1, categoryNote: "x".repeat(201) }), [photo()], NOW).categoryNote?.key, "validation.categoryNoteTooLong");

// ───────── 证据编号必填，≤100 ─────────
assert.equal(validateEntryForm(valid({ evidenceRef: "  " }), [photo()], NOW).evidenceRef?.key, "validation.evidenceRefRequired");
assert.equal(validateEntryForm(valid({ evidenceRef: "E".repeat(100) }), [photo()], NOW).evidenceRef, undefined);
assert.deepEqual(validateEntryForm(valid({ evidenceRef: "E".repeat(101) }), [photo()], NOW).evidenceRef, { key: "validation.evidenceRefTooLong", params: { max: 100 } });

// ───────── 至少一家生效门店 ─────────
assert.equal(validateEntryForm(valid({ storeScope: [] }), [photo()], NOW).storeScope?.key, "validation.storeScopeRequired");

// ───────── 到期日：必填，明天 ~ 12 个月 ─────────
assert.equal(validateExpiry("", NOW)?.key, "validation.expiresRequired");
assert.equal(validateExpiry("2026-02-30", NOW)?.key, "validation.expiresRequired");
assert.equal(validateExpiry("2026-10-13", NOW)?.key, "validation.expiresPast", "今天不行，必须晚于今天");
assert.equal(validateExpiry("2026-10-12", NOW)?.key, "validation.expiresPast");
assert.equal(validateExpiry("2026-10-14", NOW), null, "明天是最早可选");
assert.equal(validateExpiry("2027-10-13", NOW), null, "今天起 12 个月是最晚可选");
assert.deepEqual(validateExpiry("2027-10-14", NOW), { key: "validation.expiresTooFar", params: { months: 12 } });

// ───────── 照片 1–5 张、每张 ≤5MB ─────────
assert.equal(validatePhotoSet([])?.key, "validation.photoCountRange");
assert.deepEqual(validatePhotoSet([])?.params, { min: 1, max: 5 });
assert.equal(validatePhotoSet([photo()]), null);
assert.equal(validatePhotoSet(Array.from({ length: 5 }, () => photo())), null);
assert.equal(validatePhotoSet(Array.from({ length: 6 }, () => photo()))?.key, "validation.photoCountRange");
assert.equal(validatePhotoSet([photo({ byteSize: 5 * 1024 * 1024 })]), null, "恰好 5MB 通过");
assert.equal(validatePhotoSet([photo({ byteSize: 5 * 1024 * 1024 + 1 })])?.key, "validation.photoTooLarge");
assert.equal(validatePhotoSet([photo({ byteSize: 0 })])?.key, "validation.photoEmpty");
assert.equal(validateEntryForm(valid(), [], NOW).photos?.key, "validation.photoCountRange");

// ───────── 空表单一次报全 ─────────
{
  const errors = validateEntryForm(createEmptyEntryForm(), [], NOW);
  assert.deepEqual(Object.keys(errors).sort(), ["evidenceRef", "expiresOn", "incidentCategory", "photos", "storeScope"]);
  assert.equal(hasEntryFormErrors(errors), true);
  assert.equal(hasEntryFormErrors({}), false);
}

// ───────── 脏表单判断 ─────────
assert.equal(isEntryFormDirty(createEmptyEntryForm(), 0), false);
assert.equal(isEntryFormDirty(createEmptyEntryForm(), 1), true, "只选了照片也算有未提交内容");
assert.equal(isEntryFormDirty({ ...createEmptyEntryForm(), evidenceRef: " " }, 0), false, "纯空白不算");
assert.equal(isEntryFormDirty({ ...createEmptyEntryForm(), storeScope: ["1008"] }, 0), true);

// ───────── 提交内容构造 ─────────
{
  const input = buildCreateEntryInput(valid({ incidentCategory: 1, categoryNote: "遗留的说明", evidenceRef: "  INC-1  ", storeScope: ["1009", "1008", "1009", " "] }), [photo()], NOW);
  assert.equal(input.categoryNote, null, "只有 Other 才提交说明，其余类别残留的说明不提交");
  assert.equal(input.evidenceRef, "INC-1");
  assert.deepEqual(input.storeScope, ["1008", "1009"], "去重并排序");
  assert.equal(input.expiresAtUtc, new Date(2026, 11, 31, 0, 0, 0, 0).toISOString());
  const other = buildCreateEntryInput(valid({ incidentCategory: 3, categoryNote: "  情形说明  " }), [photo()], NOW);
  assert.equal(other.categoryNote, "情形说明");
  assert.equal(other.incidentCategory, 3);
}

// ───────── 撤回原因 / 申诉备注 ─────────
assert.equal(validateWithdrawReason("")?.key, "validation.textRequired");
assert.equal(validateWithdrawReason("  ")?.key, "validation.textRequired");
assert.equal(validateWithdrawReason("录入有误"), null);
assert.deepEqual(validateWithdrawReason("x".repeat(201)), { key: "validation.textTooLong", params: { max: 200 } });
assert.equal(validateWithdrawReason("x".repeat(200)), null);
assert.equal(validateDisputeNote(""), null, "申诉备注可清空");
assert.equal(validateDisputeNote("x".repeat(500)), null);
assert.deepEqual(validateDisputeNote("x".repeat(501)), { key: "validation.textTooLong", params: { max: 500 } });
assert.equal(normalizeText("  a "), "a");
assert.equal(normalizeText("   "), null);

console.log("entry-form.test.ts: ok");
