import assert from "node:assert/strict";
import {
  buildCreateNameBody,
  buildPersonNameValues,
  diffNameParts,
  isPersonNameEmpty,
  resolveUpdateFullName,
} from "./person-form";
import {
  hasPersonNameErrors,
  PERSON_FULL_NAME_MAX_LENGTH,
  PERSON_NAME_PART_MAX_LENGTH,
  validatePersonNameFields,
} from "@/modules/users/validation";

const blank = { familyName: "", givenName: "", middleName: "", nickname: "" };

// ── 预填：详情有结构化姓名就直接用，不再猜 ──
{
  const { values, legacySplit } = buildPersonNameValues({
    fullName: "Emily Rose Chen",
    givenName: "Emily",
    middleName: "Rose",
    familyName: "Chen",
    nickname: "Em",
  });
  assert.deepEqual(values, { familyName: "Chen", givenName: "Emily", middleName: "Rose", nickname: "Em" });
  assert.equal(legacySplit, false);
}
// 只有名、没有姓也算结构化（后端迁移时单个词的全名可能只落在「名」上），不再二次拆分。
{
  const { values, legacySplit } = buildPersonNameValues({ fullName: "Birgit", givenName: "Birgit" });
  assert.deepEqual(values, { familyName: "", givenName: "Birgit", middleName: "", nickname: "" });
  assert.equal(legacySplit, false);
}

// ── 预填：存量账号只有 fullName，用 splitLegacyFullName 拆，并标记 legacySplit ──
{
  const western = buildPersonNameValues({ fullName: "Emily Chen" });
  assert.deepEqual(western.values, { familyName: "Chen", givenName: "Emily", middleName: "", nickname: "" });
  assert.equal(western.legacySplit, true);

  const three = buildPersonNameValues({ fullName: "Emily Rose Chen", nickname: "Em" });
  assert.deepEqual(three.values, { familyName: "Chen", givenName: "Emily", middleName: "Rose", nickname: "Em" });
  assert.equal(three.legacySplit, true, "昵称不影响是否按全名拆分");

  const cjk = buildPersonNameValues({ fullName: "王芳" });
  assert.deepEqual(cjk.values, { familyName: "王", givenName: "芳", middleName: "", nickname: "" });
  assert.equal(cjk.legacySplit, true);

  const single = buildPersonNameValues({ fullName: "Staff" });
  assert.deepEqual(single.values, { familyName: "", givenName: "Staff", middleName: "", nickname: "" });
  assert.equal(single.legacySplit, true);
}
// 什么都没有：不显示「自动拆分」提示。
{
  const empty = buildPersonNameValues({});
  assert.deepEqual(empty.values, blank);
  assert.equal(empty.legacySplit, false);
  assert.equal(isPersonNameEmpty(empty.values), true);
  assert.equal(isPersonNameEmpty({ ...blank, nickname: "Em" }), false);
}

// ── 差异：未改动 → 什么都不发；没有 fullName ──
{
  const base = { familyName: "Chen", givenName: "Emily", middleName: "Rose", nickname: "Em" };
  const same = diffNameParts(base, { ...base, givenName: "  Emily " });
  assert.deepEqual(same.parts, {}, "仅首尾空白不同不算改动");
  assert.equal(same.fullName, undefined);
  assert.equal(same.changedCount, 0);
  assert.equal(same.nameChanged, false);
}
// 只改昵称：只发昵称，不发 fullName（全名没变）。
{
  const base = { familyName: "Chen", givenName: "Emily", middleName: "", nickname: "" };
  const diff = diffNameParts(base, { ...base, nickname: "Em" });
  assert.deepEqual(diff.parts, { nickname: "Em" });
  assert.equal(diff.fullName, undefined);
  assert.equal(diff.nameChanged, false);
  assert.equal(diff.changedCount, 1);
}
// 改名：发变化的段 + 合成后的 fullName（旧后端靠它）。
{
  const base = { familyName: "Chen", givenName: "Emily", middleName: "", nickname: "Em" };
  const diff = diffNameParts(base, { ...base, givenName: "Emma" });
  assert.deepEqual(diff.parts, { givenName: "Emma" });
  assert.equal(diff.fullName, "Emma Chen");
  assert.equal(diff.nameChanged, true);
}
// 清空中间名：空串表示清空，fullName 随之变化。
{
  const base = { familyName: "Chen", givenName: "Emily", middleName: "Rose", nickname: "" };
  const diff = diffNameParts(base, { ...base, middleName: "" });
  assert.deepEqual(diff.parts, { middleName: "" });
  assert.equal(diff.fullName, "Emily Chen");
}
// 清空昵称：只发空串，不带 fullName。
{
  const base = { familyName: "Chen", givenName: "Emily", middleName: "", nickname: "Em" };
  const diff = diffNameParts(base, { ...base, nickname: "" });
  assert.deepEqual(diff.parts, { nickname: "" });
  assert.equal(diff.fullName, undefined);
}
// 中文姓名：姓 + 名无空格。
{
  const base = { familyName: "王", givenName: "芳", middleName: "", nickname: "" };
  const diff = diffNameParts(base, { ...base, givenName: "芳芳" });
  assert.equal(diff.fullName, "王芳芳");
  assert.deepEqual(diff.parts, { givenName: "芳芳" });
}
// 三段全清空：fullName 为空串（界面层会因必填校验拦下，纯函数不抛）。
{
  const base = { familyName: "Chen", givenName: "Emily", middleName: "", nickname: "" };
  const diff = diffNameParts(base, blank);
  assert.equal(diff.fullName, "");
  assert.deepEqual(diff.parts, { familyName: "", givenName: "" });
}

// ── 创建：四段有值才带，fullName 始终是合成值 ──
assert.deepEqual(
  buildCreateNameBody({ familyName: "Chen", givenName: "Emily", middleName: "", nickname: "" }),
  { fullName: "Emily Chen", familyName: "Chen", givenName: "Emily" },
  "空的中间名 / 昵称不发"
);
assert.deepEqual(
  buildCreateNameBody({ familyName: " Chen ", givenName: "Emily", middleName: "Rose", nickname: "Em" }),
  { fullName: "Emily Rose Chen", familyName: "Chen", givenName: "Emily", middleName: "Rose", nickname: "Em" }
);
assert.deepEqual(buildCreateNameBody(blank), {}, "全空不带任何键");
assert.deepEqual(
  buildCreateNameBody({ familyName: "王", givenName: "芳", middleName: "", nickname: "芳芳" }),
  { fullName: "王芳", familyName: "王", givenName: "芳", nickname: "芳芳" }
);

// ── 更新时 fullName 的取值：永远不能让后端把它当成「清空」 ──
assert.equal(
  resolveUpdateFullName({ diff: diffNameParts(blank, { ...blank, givenName: "A", familyName: "B" }), currentFullName: "Old", baselineNames: blank }),
  "A B",
  "姓名有变化用合成值"
);
assert.equal(
  resolveUpdateFullName({ diff: diffNameParts(blank, blank), currentFullName: "  Old Name ", baselineNames: blank }),
  "Old Name",
  "没变化沿用原全名"
);
assert.equal(
  resolveUpdateFullName({
    diff: diffNameParts({ ...blank, givenName: "Emily", familyName: "Chen" }, { ...blank, givenName: "Emily", familyName: "Chen" }),
    currentFullName: undefined,
    baselineNames: { ...blank, givenName: "Emily", familyName: "Chen" },
  }),
  "Emily Chen",
  "详情没有全名但有结构化姓名：补合成值，避免被清空"
);
assert.equal(resolveUpdateFullName({ diff: diffNameParts(blank, blank), currentFullName: undefined, baselineNames: blank }), undefined);

// ── 校验 ──
{
  const ok = validatePersonNameFields({ ...blank, familyName: "Chen", givenName: "Emily" }, { requireNames: true });
  assert.deepEqual(ok, {});
  assert.equal(hasPersonNameErrors(ok), false);

  const missing = validatePersonNameFields(blank, { requireNames: true });
  assert.deepEqual(missing, { familyName: "required", givenName: "required" }, "姓、名必填，中间名 / 昵称选填");
  assert.deepEqual(validatePersonNameFields(blank, { requireNames: false }), {}, "编辑时没动过姓名区不强制必填");

  const longPart = "x".repeat(PERSON_NAME_PART_MAX_LENGTH + 1);
  const tooLong = validatePersonNameFields({ familyName: longPart, givenName: "A", middleName: longPart, nickname: longPart }, { requireNames: true });
  assert.equal(tooLong.familyName, "tooLong");
  assert.equal(tooLong.middleName, "tooLong");
  assert.equal(tooLong.nickname, "tooLong");
  assert.equal(tooLong.givenName, undefined);
  assert.equal(tooLong.fullName, undefined, "单段已超长时不再叠加全名超长");

  // 每段 ≤ 50 但合成后 > 100：错误挂在 fullName 上。
  const half = "y".repeat(PERSON_NAME_PART_MAX_LENGTH);
  const combined = validatePersonNameFields({ familyName: half, givenName: half, middleName: "", nickname: "" }, { requireNames: true });
  assert.equal(combined.fullName, "fullNameTooLong");
  assert.equal(`${half} ${half}`.length > PERSON_FULL_NAME_MAX_LENGTH, true);

  // 恰好 50 / 100 通过。
  assert.deepEqual(
    validatePersonNameFields({ familyName: half, givenName: "y".repeat(49), middleName: "", nickname: half }, { requireNames: true }),
    {}
  );
  // 空白折叠后再算长度，纯空白按空处理。
  assert.equal(validatePersonNameFields({ ...blank, familyName: "   ", givenName: "A" }, { requireNames: true }).familyName, "required");
}

console.log("person-form.test.ts: ok");
