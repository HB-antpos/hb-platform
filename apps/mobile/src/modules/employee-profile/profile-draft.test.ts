import assert from "node:assert/strict";
import { syncEmployeeProfileDraft } from "./profile-draft";

const profile = {
  username: "admin",
  phone: "",
  email: "",
  bankBsb: "server-bsb",
  bankAccountNumber: "server-account",
  superannuationCompanyName: "",
  superannuationCompanyCode: "",
  superannuationAccountNumber: "",
  birthday: "",
  gender: "",
  employmentType: "",
  avatarUrl: "https://cdn/new-avatar.jpg",
  identityType: "",
  identityId: "",
  identityPhotoUrl: "",
  address: "server-address",
  sensitiveRevision: 0,
};
const draft = {
  phone: "draft-phone",
  email: "",
  gender: "",
  employmentType: "",
  address: "draft-address",
};

assert.deepEqual(
  syncEmployeeProfileDraft(draft, profile, true),
  draft,
  "头像即时保存刷新资料缓存时必须保留未保存的非敏感草稿"
);
assert.equal(
  syncEmployeeProfileDraft(draft, profile, false).phone,
  "",
  "首次加载资料时应初始化非敏感表单"
);
assert.equal(
  "bankAccountNumber" in syncEmployeeProfileDraft(draft, profile, false),
  false,
  "普通资料草稿不得携带银行账号"
);
assert.equal(
  syncEmployeeProfileDraft(draft, profile, false).address,
  "server-address",
  "切换账号后必须使用新账号资料重新初始化草稿"
);

console.log("profile-draft.test.ts: ok");
