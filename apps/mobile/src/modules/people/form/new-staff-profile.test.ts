import assert from "node:assert/strict";
import {
  buildNewStaffProfileBody,
  EMPTY_NEW_STAFF_GUARDIAN,
  hasNewStaffProfileErrors,
  isNewStaffMinor,
  isNewStaffProfileDirty,
  markNewStaffServerError,
  normalizeAustralianMobile,
  resolveNewStaffAge,
  resolveNewStaffServerErrorKey,
  sydneyToday,
  validateNewStaffBirthday,
  validateNewStaffProfile,
  validateRequiredStaffPhone,
  type NewStaffProfileInput,
} from "./new-staff-profile";
import { readApiErrorCode } from "./identity-conflict";

const today = new Date(2026, 9, 30); // 悉尼 2026-10-30

// ── 悉尼「今天」：UTC 10-29 14:00 已是悉尼 10-30（夏令时 UTC+11）──
{
  const value = sydneyToday(new Date("2026-10-29T14:00:00Z"));
  assert.deepEqual([value.getFullYear(), value.getMonth() + 1, value.getDate()], [2026, 10, 30]);
  const before = sydneyToday(new Date("2026-10-29T12:59:00Z"));
  assert.equal(before.getDate(), 29, "悉尼还没过零点仍是 10-29");
}

// ── 生日：必填、不晚于今天、不早于 1940-01-01、格式必须是 YYYY-MM-DD ──
assert.equal(validateNewStaffBirthday("", today), "required");
assert.equal(validateNewStaffBirthday("  ", today), "required");
assert.equal(validateNewStaffBirthday("1940-01-01", today), undefined, "1940-01-01 本身允许");
assert.equal(validateNewStaffBirthday("1939-12-31", today), "invalid");
assert.equal(validateNewStaffBirthday("2026-10-30", today), undefined, "今天允许");
assert.equal(validateNewStaffBirthday("2026-10-31", today), "invalid", "明天不允许");
assert.equal(validateNewStaffBirthday("2011-02-30", today), "invalid", "不存在的日期");
assert.equal(validateNewStaffBirthday("2011/03/02", today), "invalid");

// ── 年龄与未成年：满 18 岁当天起算成年（与后端 AgeOn 一致）──
assert.deepEqual(resolveNewStaffAge("1998-07-21", today), { age: 28, minor: false });
assert.deepEqual(resolveNewStaffAge("2011-03-02", today), { age: 15, minor: true });
assert.deepEqual(resolveNewStaffAge("2008-10-30", today), { age: 18, minor: false }, "18 岁生日当天已成年");
assert.deepEqual(resolveNewStaffAge("2008-10-31", today), { age: 17, minor: true }, "18 岁生日前一天仍未成年");
assert.equal(resolveNewStaffAge("", today), null);
assert.equal(resolveNewStaffAge("1939-12-31", today), null, "无效生日不显示年龄");
assert.equal(isNewStaffMinor("", today), false, "没填生日不出现监护人一节");

// ── 电话：必填 + 澳洲手机号 ──
assert.equal(validateRequiredStaffPhone(""), "required");
assert.equal(validateRequiredStaffPhone("0412 345 678"), undefined);
assert.equal(validateRequiredStaffPhone("+61 412-345-678"), undefined);
assert.equal(validateRequiredStaffPhone("0298765432"), "invalid", "座机不是手机号");
assert.equal(normalizeAustralianMobile("0412 345 678"), "+61412345678");
assert.equal(normalizeAustralianMobile("+61412345678"), "+61412345678");
assert.equal(normalizeAustralianMobile("0298765432"), null);

// ── 整体校验 ──
const adult: NewStaffProfileInput = { phone: "0412 345 678", birthday: "1998-07-21", guardian: EMPTY_NEW_STAFF_GUARDIAN };
assert.equal(hasNewStaffProfileErrors(validateNewStaffProfile(adult, "me@gmail.com", today)), false);

const minor: NewStaffProfileInput = {
  phone: "0412 345 678",
  birthday: "2011-03-02",
  guardian: { name: "Linh Nguyen", relationship: "Mother", phone: "0423 111 222", email: "linh@gmail.com" },
};
assert.deepEqual(validateNewStaffProfile(minor, "me@gmail.com", today).guardian, {});
{
  const errors = validateNewStaffProfile(
    { ...minor, guardian: { name: " ", relationship: "", phone: "0298765432", email: "not-an-email" } },
    "me@gmail.com",
    today
  );
  assert.deepEqual(errors.guardian, { name: "required", relationship: "required", phone: "invalid", email: "invalid" });
}
{
  const errors = validateNewStaffProfile(
    { ...minor, guardian: { ...minor.guardian, phone: "0412-345-678", email: " ME@gmail.com " } },
    "me@gmail.com",
    today
  );
  assert.equal(errors.guardian.phone, "sameAsStaff");
  assert.equal(errors.guardian.email, "sameAsStaff");
}

// ── 请求体：成年不带监护人；未成年带，去首尾空白 ──
assert.deepEqual(buildNewStaffProfileBody({ ...adult, phone: " 0412 345 678 " }, today), { phone: "0412 345 678", birthday: "1998-07-21" });
assert.deepEqual(buildNewStaffProfileBody({ ...minor, guardian: { ...minor.guardian, name: " Linh Nguyen " } }, today), {
  phone: "0412 345 678",
  birthday: "2011-03-02",
  guardian: { name: "Linh Nguyen", relationship: "Mother", phone: "0423 111 222", email: "linh@gmail.com" },
});

// ── 是否填过内容 ──
assert.equal(isNewStaffProfileDirty({ phone: "", birthday: "", guardian: EMPTY_NEW_STAFF_GUARDIAN }), false);
assert.equal(isNewStaffProfileDirty({ phone: "", birthday: "", guardian: { ...EMPTY_NEW_STAFF_GUARDIAN, email: "x" } }), true);

// ── 服务端错误码：两种形态都能读到，映射到文案键并标红对应字段 ──
const businessError = Object.assign(new Error("请填写生日"), { code: "NEW_STAFF_BIRTHDAY_REQUIRED" });
const axiosLike = { code: "ERR_BAD_REQUEST", response: { status: 400, data: { success: false, errorCode: "NEW_STAFF_GUARDIAN_PHONE_INVALID" } } };
assert.equal(readApiErrorCode(businessError), "NEW_STAFF_BIRTHDAY_REQUIRED");
assert.equal(readApiErrorCode(axiosLike), "NEW_STAFF_GUARDIAN_PHONE_INVALID", "优先取响应体里的业务码，不取传输层码");
assert.equal(readApiErrorCode(new Error("x")), null);
assert.equal(resolveNewStaffServerErrorKey("NEW_STAFF_BIRTHDAY_REQUIRED"), "birthdayRequired");
assert.equal(resolveNewStaffServerErrorKey("NEW_STAFF_GUARDIAN_DUPLICATE"), "guardianDuplicate");
assert.equal(resolveNewStaffServerErrorKey("USERNAME_EXISTS"), null);
assert.equal(resolveNewStaffServerErrorKey(null), null);
assert.deepEqual(markNewStaffServerError({ guardian: {} }, "birthdayInvalid"), { guardian: {}, birthday: "invalid" });
assert.deepEqual(markNewStaffServerError({ guardian: { name: "required" } }, "guardianPhoneInvalid"), { guardian: { name: "required", phone: "invalid" } });
assert.deepEqual(markNewStaffServerError({ guardian: {} }, "guardianDuplicate"), { guardian: {} }, "重复只给顶部提示");

console.log("new-staff-profile.test.ts: ok");
