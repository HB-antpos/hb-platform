import assert from "node:assert/strict";
import {
  getPasswordResetRuleState,
  isResetCodeFormatValid,
  isValidResetEmail,
  normalizeResetCode,
} from "./password-reset";

assert.equal(isValidResetEmail(" staff@example.com "), true);
assert.equal(isValidResetEmail("staff@example"), false);
assert.equal(isValidResetEmail(""), false);

// 从邮件复制的验证码可能带空格或横线，只保留 6 位数字。
assert.equal(normalizeResetCode("12 34-56"), "123456");
assert.equal(normalizeResetCode("1234567"), "123456");
assert.equal(isResetCodeFormatValid("12345"), false);
assert.equal(isResetCodeFormatValid("123 456"), true);

assert.deepEqual(getPasswordResetRuleState("abc", ""), { minLength: false, confirmed: false });
assert.deepEqual(getPasswordResetRuleState(" MyOwn456 ", "MyOwn456"), { minLength: true, confirmed: true });
assert.deepEqual(getPasswordResetRuleState("MyOwn456", "MyOwn45"), { minLength: true, confirmed: false });

console.log("password-reset tests passed");
