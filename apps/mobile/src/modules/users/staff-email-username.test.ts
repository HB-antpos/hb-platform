import assert from "node:assert/strict";
import { getDisplayableEmail, hasDeliverableEmail, MAX_USERNAME_LENGTH, resolveUsernameFromEmail } from "./staff-email-username";
import { normalizePhoneInput, validateNewStaffEmail, validateNewStaffPhone, validateStaffRecoveryEmail } from "./validation";

const t = (key: string) => key;

// 用户名默认取邮箱（转小写、去空格）；超过 50 个字符不自动带入。
assert.equal(resolveUsernameFromEmail(" New.Staff@Example.com "), "new.staff@example.com");
assert.equal(resolveUsernameFromEmail(`${"a".repeat(MAX_USERNAME_LENGTH)}@example.com`), "");
assert.equal(resolveUsernameFromEmail(""), "");

// 内部占位邮箱收不到信。
assert.equal(hasDeliverableEmail("staff@example.com"), true);
assert.equal(hasDeliverableEmail("staff@s001.store.local"), false);
assert.equal(hasDeliverableEmail(""), false);
assert.equal(hasDeliverableEmail(undefined), false);

// 占位邮箱在界面上显示为「未设置」（空串），真实邮箱原样展示。
assert.equal(getDisplayableEmail("staff_1@s001.store.local"), "");
assert.equal(getDisplayableEmail(" staff@example.com "), "staff@example.com");
assert.equal(getDisplayableEmail(null), "");

// 店长给老账号补邮箱：必填、格式正确、不能填占位邮箱。
assert.equal(validateStaffRecoveryEmail("", t), "messages.emailRequired");
assert.equal(validateStaffRecoveryEmail("w@example", t), "messages.emailInvalid");
assert.equal(validateStaffRecoveryEmail("w@s001.store.local", t), "messages.emailNotDeliverable");
assert.equal(validateStaffRecoveryEmail(" w.worker@example.com ", t), null);

// 新建时邮箱必填且格式正确。
assert.equal(validateNewStaffEmail("", t), "messages.emailRequired");
assert.equal(validateNewStaffEmail("staff@example", t), "messages.emailInvalid");
assert.equal(validateNewStaffEmail(" staff@example.com ", t), null);

// 澳洲手机号校验。
assert.equal(normalizePhoneInput("0412 345-678"), "0412345678");
assert.equal(validateNewStaffPhone("", t), null, "手机号选填");
assert.equal(validateNewStaffPhone("0412 345 678", t), null);
assert.equal(validateNewStaffPhone("+61 412 345 678", t), null);
assert.equal(validateNewStaffPhone("0312345678", t), "messages.phoneInvalid", "座机号不是手机号");
assert.equal(validateNewStaffPhone("041234567", t), "messages.phoneInvalid", "位数不足");

console.log("staff-email-username tests passed");
