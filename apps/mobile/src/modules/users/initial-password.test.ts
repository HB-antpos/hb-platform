import assert from "node:assert/strict";
import { buildLoginCredentialText, generateInitialPassword } from "./initial-password";
import { normalizePhoneInput, validateNewStaffPhone } from "./validation";

function sequence(values: number[]) {
  return (count: number) => Uint8Array.from({ length: count }, (_, index) => values[index % values.length]);
}

// 格式固定：4 位-4 位，字符集不含易混字符。
const randomPassword = generateInitialPassword((count) => Uint8Array.from({ length: count }, () => Math.floor(Math.random() * 256)));
assert.match(randomPassword, /^[a-km-zA-HJ-NP-Z2-9]{4}-[a-km-zA-HJ-NP-Z2-9]{4}$/);

// 全部落在字母上时，也会强制放入一个数字；全部落在数字上时强制放入字母。
const allLetters = generateInitialPassword(sequence([0]));
assert.match(allLetters, /[0-9]/, "必须至少包含一个数字");
assert.match(allLetters, /[a-zA-Z]/, "必须至少包含一个字母");
const allDigits = generateInitialPassword(sequence([48]));
assert.match(allDigits, /[a-zA-Z]/, "必须至少包含一个字母");
assert.match(allDigits, /[0-9]/, "必须至少包含一个数字");

// 多次生成长度稳定且满足后端 6–100 位限制。
for (let i = 0; i < 200; i += 1) {
  const value = generateInitialPassword((count) => Uint8Array.from({ length: count }, () => Math.floor(Math.random() * 256)));
  assert.equal(value.length, 9);
  assert.match(value, /[0-9]/);
  assert.match(value, /[a-zA-Z]/);
}

assert.equal(
  buildLoginCredentialText({
    heading: "HB 登录信息",
    usernameLabel: "用户名",
    passwordLabel: "初始密码",
    username: "demo.c",
    password: "Hb7k-2qMx",
    footer: "首次登录后请修改密码",
  }),
  "HB 登录信息\n用户名: demo.c\n初始密码: Hb7k-2qMx\n首次登录后请修改密码"
);

// 澳洲手机号校验。
const t = (key: string) => key;
assert.equal(normalizePhoneInput("0412 345-678"), "0412345678");
assert.equal(validateNewStaffPhone("", t), null, "手机号选填");
assert.equal(validateNewStaffPhone("0412 345 678", t), null);
assert.equal(validateNewStaffPhone("+61 412 345 678", t), null);
assert.equal(validateNewStaffPhone("0312345678", t), "messages.phoneInvalid", "座机号不是手机号");
assert.equal(validateNewStaffPhone("041234567", t), "messages.phoneInvalid", "位数不足");
assert.equal(validateNewStaffPhone("abc", t), "messages.phoneInvalid");

console.log("initial-password tests passed");
