import { normalizeStoreUser } from "./profile-normalization";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

// 新后端：camelCase 与 PascalCase 都要能读到姓 / 名 / 中间名 / 昵称。
const camel = normalizeStoreUser({
  userGuid: "u-1",
  username: "emily.chen",
  fullName: "Emily Rose Chen",
  givenName: "Emily",
  middleName: "Rose",
  familyName: "Chen",
  nickname: "Em",
});
assertEqual(camel.givenName, "Emily", "camel givenName");
assertEqual(camel.middleName, "Rose", "camel middleName");
assertEqual(camel.familyName, "Chen", "camel familyName");
assertEqual(camel.nickname, "Em", "camel nickname");

const pascal = normalizeStoreUser({
  UserGUID: "u-2",
  Username: "fang.wang",
  FullName: "王芳",
  GivenName: "芳",
  FamilyName: "王",
  Nickname: "芳芳",
});
assertEqual(pascal.givenName, "芳", "pascal givenName");
assertEqual(pascal.familyName, "王", "pascal familyName");
assertEqual(pascal.nickname, "芳芳", "pascal nickname");

// 旧后端不返回新字段：保持 undefined，界面回落到 fullName。
const legacy = normalizeStoreUser({ userGuid: "u-3", username: "old.user", fullName: "Old User" });
assertEqual(legacy.givenName, undefined, "legacy givenName");
assertEqual(legacy.nickname, undefined, "legacy nickname");
assertEqual(legacy.fullName, "Old User", "legacy fullName 保持");
