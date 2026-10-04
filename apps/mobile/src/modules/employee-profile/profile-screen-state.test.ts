import assert from "node:assert/strict";
import test from "node:test";

import type { EmployeeProfile, SensitiveEmployeeProfilePayload } from "./types";
import { getSensitiveStatusView } from "./sensitive-profile";
import {
  buildSensitiveReviewPayload,
  getBackAction,
  getSensitiveSectionOrder,
  getSensitiveSubmitFailureAction,
  hasBasicProfileChanges,
  hasSensitiveProfileChanges,
  shouldUnlockSensitiveConflict,
  shouldApplyEmployeeProfileOperation,
} from "./profile-screen-state";

const profile: EmployeeProfile = {
  username: "employee",
  phone: " 0400 000 000 ",
  email: "employee@example.com",
  bankBsb: "062000",
  bankAccountNumber: "12345678",
  superannuationCompanyName: "Example Super",
  superannuationCompanyCode: "EXAMPLE",
  superannuationAccountNumber: "SUPER1234",
  birthday: "1990-01-02",
  gender: "female",
  employmentType: "fullTime",
  avatarUrl: "",
  identityType: "Driver licence",
  identityId: "DL123456",
  identityPhotoUrl: "",
  address: " Brisbane ",
  sensitiveRevision: 7,
};

test("基本资料仅在规范化后的草稿变化时阻止离页", () => {
  const sameDraft = {
    phone: "0400 000 000",
    email: "employee@example.com",
    gender: "female",
    employmentType: "fullTime",
    address: "Brisbane",
  };
  assert.equal(hasBasicProfileChanges(sameDraft, profile), false);
  assert.equal(getBackAction({ view: "basic", hasUnsavedChanges: false }), "show-overview");

  const changedDraft = { ...sameDraft, phone: "0400 111 222" };
  assert.equal(hasBasicProfileChanges(changedDraft, profile), true);
  assert.equal(getBackAction({ view: "basic", hasUnsavedChanges: true }), "confirm-discard");
  assert.equal(getBackAction({ view: "overview", hasUnsavedChanges: false }), "navigate");
  // 审核进度页没有草稿，返回直接回到概览。
  assert.equal(getBackAction({ view: "progress", hasUnsavedChanges: false }), "show-overview");
  assert.equal(hasBasicProfileChanges({ ...sameDraft, email: " employee@example.com " }, profile), false);
  assert.equal(hasBasicProfileChanges({ ...sameDraft, email: "other@example.com" }, profile), true);
  assert.equal(hasBasicProfileChanges({ ...sameDraft, employmentType: "casual" }, profile, { canEditPositionType: false }), false);
});

test("敏感资料保留完整 Pending 草稿并带进入编辑时的 revision 提交", () => {
  const pendingDraft: SensitiveEmployeeProfilePayload = {
    birthday: " 1990-03-04 ",
    bankBsb: " 064-000 ",
    bankAccountNumber: " 99887766 ",
    superannuationCompanyName: " Pending Super ",
    superannuationCompanyCode: " PENDING ",
    superannuationAccountNumber: " S9988 ",
    identityType: " Passport ",
    identityId: " P1234567 ",
  };
  const editedDraft = { ...pendingDraft, identityId: " P7654321 " };

  assert.equal(hasSensitiveProfileChanges(pendingDraft, pendingDraft), false);
  assert.equal(hasSensitiveProfileChanges(editedDraft, pendingDraft), true);
  assert.deepEqual(buildSensitiveReviewPayload(editedDraft, 7), {
    birthday: "1990-03-04",
    bankBsb: "064-000",
    bankAccountNumber: "99887766",
    superannuationCompanyName: "Pending Super",
    superannuationCompanyCode: "PENDING",
    superannuationAccountNumber: "S9988",
    identityType: "Passport",
    identityId: "P7654321",
    expectedSensitiveRevision: 7,
  });
});

test("敏感编辑视图把用户选择的分组放在首位且仍保留全部分组", () => {
  assert.deepEqual(getSensitiveSectionOrder("identity"), [
    "identity",
    "personal",
    "banking",
    "superannuation",
  ]);
});

test("概览状态沿用审核申请的 Pending 状态", () => {
  assert.equal(getSensitiveStatusView({
    ...buildSensitiveReviewPayload({
      birthday: "1990-03-04",
      bankBsb: "064000",
      bankAccountNumber: "99887766",
      superannuationCompanyName: "Pending Super",
      superannuationCompanyCode: "PENDING",
      superannuationAccountNumber: "S9988",
      identityType: "Passport",
      identityId: "P7654321",
    }, 7),
    requestId: 9,
    status: "Pending",
    hasIdentityPhoto: true,
    identityPhotoUrl: "https://example.test/pending.jpg",
    baseSensitiveRevision: 7,
    submittedAt: "2026-09-10T01:00:00Z",
    changedFields: ["identityId"],
  }).statusKey, "status.pending");
});

test("revision 冲突退出过期编辑上下文，普通失败继续保留草稿", () => {
  assert.equal(
    getSensitiveSubmitFailureAction({ response: { status: 409 } }),
    "discard-stale-edit"
  );
  assert.equal(
    getSensitiveSubmitFailureAction(new Error("network unavailable")),
    "keep-draft"
  );
  assert.equal(shouldUnlockSensitiveConflict({ isError: true }), false);
  assert.equal(shouldUnlockSensitiveConflict({ isError: false }), true);
});

test("A 的延迟保存结果不能在切换到 B 或退出登录后更新当前资料", async () => {
  assert.equal(shouldApplyEmployeeProfileOperation({
    submittedIdentity: "user-a",
    currentIdentity: "user-a",
    submittedScope: 1,
    currentScope: 1,
    isAuthenticated: true,
  }), true);
  assert.equal(shouldApplyEmployeeProfileOperation({
    submittedIdentity: "user-a",
    currentIdentity: "user-b",
    submittedScope: 1,
    currentScope: 2,
    isAuthenticated: true,
  }), false);
  assert.equal(shouldApplyEmployeeProfileOperation({
    submittedIdentity: "user-a",
    currentIdentity: "user-a",
    submittedScope: 1,
    currentScope: 2,
    isAuthenticated: false,
  }), false);
  assert.equal(shouldApplyEmployeeProfileOperation({
    submittedIdentity: "user-a",
    currentIdentity: "user-a",
    submittedScope: 1,
    currentScope: 3,
    isAuthenticated: true,
  }), false);

  let resolveSave!: (value: string) => void;
  const delayedSave = new Promise<string>((resolve) => {
    resolveSave = resolve;
  });
  let visibleDraft = "B draft";
  let currentIdentity = "user-a";
  let currentScope = 1;
  const applyDelayedResult = delayedSave.then((savedDraft) => {
    if (shouldApplyEmployeeProfileOperation({
      submittedIdentity: "user-a",
      currentIdentity,
      submittedScope: 1,
      currentScope,
      isAuthenticated: true,
    })) {
      visibleDraft = savedDraft;
    }
  });
  currentIdentity = "user-b";
  currentScope = 2;
  resolveSave("A response");
  await applyDelayedResult;
  assert.equal(visibleDraft, "B draft");
});
