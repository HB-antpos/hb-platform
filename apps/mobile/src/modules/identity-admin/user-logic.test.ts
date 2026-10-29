import assert from "node:assert/strict";
import { EMPTY_IDENTITY_USER_FORM, isIdentitySessionAllowed, isUncertainIdentityWrite, sortRolesByLevel, validateIdentityUserForm } from "./user-logic";

const valid = { ...EMPTY_IDENTITY_USER_FORM, username: "staff01", email: "staff@example.com", password: "abcdef", confirmPassword: "abcdef" };
assert.equal(validateIdentityUserForm(valid, true), null);
assert.equal(validateIdentityUserForm({ ...valid, username: " x " }, true), "usernameInvalid");
assert.equal(validateIdentityUserForm({ ...valid, email: "name@" }, true), "emailInvalid");
assert.equal(validateIdentityUserForm({ ...valid, confirmPassword: "different" }, true), "passwordMismatch");
assert.equal(validateIdentityUserForm({ ...valid, password: "", confirmPassword: "" }, false), null);
for (const sessionKind of ["device", "iosReview", "anonymous"]) assert.equal(isIdentitySessionAllowed({ isAuthenticated: true, sessionKind, iosReviewOfflineGuardActive: false }, true), false);
assert.equal(isIdentitySessionAllowed({ isAuthenticated: true, sessionKind: "account", iosReviewOfflineGuardActive: true }, true), false);
assert.equal(isIdentitySessionAllowed({ isAuthenticated: true, sessionKind: "account", iosReviewOfflineGuardActive: false }, true), true);
assert.equal(isIdentitySessionAllowed({ isAuthenticated: true, sessionKind: "account", iosReviewOfflineGuardActive: false }, false), false);
// 角色筛选项：等级高的在前，同级按名字，没有等级的排最后；不裁剪（可选范围由服务端按等级决定）。
assert.deepEqual(
  sortRolesByLevel([
    { roleName: "User", level: 10 },
    { roleName: "StoreStaff", level: 30 },
    { roleName: "Custom" },
    { roleName: "Clerk", level: 30 },
    { roleName: "HrOps", level: 50 },
  ]).map(x => x.roleName),
  ["HrOps", "Clerk", "StoreStaff", "User", "Custom"],
);
assert.equal(isUncertainIdentityWrite(new Error("offline")), true);
assert.equal(isUncertainIdentityWrite({ response: { status: 503 } }), true);
assert.equal(isUncertainIdentityWrite({ response: { status: 400 } }), false);
assert.equal(isUncertainIdentityWrite({ response: { status: 403 } }), false);
assert.equal(isUncertainIdentityWrite({ apiBusinessError: true, code: "USERNAME_EXISTS" }), false);
console.log("identity user validation, session boundaries and uncertain writes: passed");
