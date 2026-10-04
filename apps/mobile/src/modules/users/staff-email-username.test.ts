import assert from "node:assert/strict";
import { hasDeliverableEmail, MAX_USERNAME_LENGTH, resolveUsernameFromEmail } from "./staff-email-username";
import { normalizePhoneInput, validateNewStaffEmail, validateNewStaffPhone } from "./validation";

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
