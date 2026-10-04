import assert from "node:assert/strict";
import { PROFILE_COMPLETENESS_ITEMS, PROFILE_COMPLETENESS_LABEL_KEYS, getProfileCompleteness } from "./profile-completeness";

// 空资料：全部缺失。
const empty = getProfileCompleteness(null);
assert.equal(empty.filled, 0);
assert.equal(empty.total, PROFILE_COMPLETENESS_ITEMS.length);
assert.deepEqual(empty.missing, [...PROFILE_COMPLETENESS_ITEMS]);
assert.equal(empty.percent, 0);
assert.equal(empty.isComplete, false);

// 部分填写：空白字符串不算填写；银行/养老金须机构与账号齐全；非法生日不算。
const partial = getProfileCompleteness({
  phone: "0400 000 000",
  email: "  ",
  gender: "female",
  address: "1 Queen St",
  birthday: "2023-02-30",
  bankBsb: "123-456",
  bankAccountNumber: "",
  superannuationCompanyName: "Future Super",
  superannuationAccountNumber: "S-1",
  identityId: "P123",
  identityPhotoUrl: "",
});
assert.deepEqual(partial.missing, ["email", "birthday", "banking", "identityPhoto"]);
assert.equal(partial.filled, 5);
assert.equal(partial.percent, 56);

// 全部填写。
const full = getProfileCompleteness({
  phone: "0400",
  email: "a@b.co",
  gender: "male",
  address: "addr",
  birthday: "1998-06-21T00:00:00",
  bankBsb: "123-456",
  bankAccountNumber: "12345678",
  superannuationCompanyName: "Super",
  superannuationAccountNumber: "S-1",
  identityId: "P123",
  identityPhotoUrl: "https://cdn/id.jpg",
});
assert.equal(full.isComplete, true);
assert.equal(full.percent, 100);

// 每一项都必须有文案键。
for (const item of PROFILE_COMPLETENESS_ITEMS) {
  assert.ok(PROFILE_COMPLETENESS_LABEL_KEYS[item], `缺少 ${item} 的文案键`);
}

console.log("profile-completeness tests passed");
