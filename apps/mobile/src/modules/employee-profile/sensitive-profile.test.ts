import assert from "node:assert/strict";
import * as sensitiveProfileModule from "./sensitive-profile";
import {
  buildNonSensitiveProfilePayload,
  getChangedSensitiveFields,
  hasEmailChanged,
  isEmailChangeValid,
  getSensitiveAccountSummary,
  getSensitiveStatusView,
  isSensitiveVersionConflict,
  normalizeSensitiveDraft,
  refreshEmployeeProfileAfterIdentityMutation,
  selectSensitiveDraft,
  shouldRefreshSensitiveProfile,
} from "./sensitive-profile";
import type {
  EmployeeProfile,
  EmployeeProfileSensitiveChangeRequest,
  SensitiveEmployeeProfilePayload,
} from "./types";

const formal: EmployeeProfile = {
  username: "employee-a",
  sensitiveRevision: 3,
  phone: "0400000000",
  email: "employee@example.com",
  bankBsb: "123-456",
  bankAccountNumber: "111122223333",
  superannuationCompanyName: "Future Super",
  superannuationCompanyCode: "FS01",
  superannuationAccountNumber: "SUPER-0001",
  birthday: "1990-01-02",
  gender: "female",
  employmentType: "fullTime",
  avatarUrl: "https://cdn/avatar.jpg",
  identityType: "passport",
  identityId: "P1234567",
  identityPhotoUrl: "https://cdn/formal-identity.jpg",
  address: "1 Queen Street",
};

const pending: EmployeeProfileSensitiveChangeRequest = {
  requestId: 42,
  status: "Pending",
  birthday: "1990-03-04",
  bankBsb: "654-321",
  bankAccountNumber: "999988883333",
  superannuationCompanyName: "Future Super",
  superannuationCompanyCode: "FS01",
  superannuationAccountNumber: "SUPER-0002",
  identityType: "driverLicence",
  identityId: "DL7654321",
  hasIdentityPhoto: true,
  identityPhotoUrl: "https://cdn/pending-identity.jpg",
  baseSensitiveRevision: 3,
  submittedAt: "2026-07-16T00:00:00Z",
  changedFields: ["bankBsb", "bankAccountNumber", "identityPhotoUrl"],
};

assert.deepEqual(
  selectSensitiveDraft(formal, pending),
  {
    birthday: pending.birthday,
    bankBsb: pending.bankBsb,
    bankAccountNumber: pending.bankAccountNumber,
    superannuationCompanyName: pending.superannuationCompanyName,
    superannuationCompanyCode: pending.superannuationCompanyCode,
    superannuationAccountNumber: pending.superannuationAccountNumber,
    identityType: pending.identityType,
    identityId: pending.identityId,
  },
  "Pending 申请存在时必须优先载入待审快照"
);

assert.deepEqual(
  selectSensitiveDraft(formal, { ...pending, status: "Rejected" }),
  {
    birthday: formal.birthday,
    bankBsb: formal.bankBsb,
    bankAccountNumber: formal.bankAccountNumber,
    superannuationCompanyName: formal.superannuationCompanyName,
    superannuationCompanyCode: formal.superannuationCompanyCode,
    superannuationAccountNumber: formal.superannuationAccountNumber,
    identityType: formal.identityType,
    identityId: formal.identityId,
  },
  "非 Pending 状态重新填报时必须回退正式资料"
);

// 正式生日来自后端的日期时间字符串，比较前只取日期部分，未改生日不能被误判为变更。
assert.deepEqual(
  getChangedSensitiveFields(
    { ...formal, birthday: "1990-01-02T00:00:00" },
    selectSensitiveDraft(formal, null)
  ),
  [],
  "正式生日带时间部分时，未修改的草稿不得算作变更"
);
assert.deepEqual(
  getChangedSensitiveFields(formal, { ...selectSensitiveDraft(formal, null), birthday: "1990-01-03" }),
  ["birthday"],
  "修改生日必须进入敏感变更字段"
);

const sameLastFourDraft: SensitiveEmployeeProfilePayload = {
  ...selectSensitiveDraft(formal, null),
  bankAccountNumber: "777766663333",
};
assert.equal(
  normalizeSensitiveDraft({ ...sameLastFourDraft, expectedSensitiveRevision: 3 }).expectedSensitiveRevision,
  3,
  "规范化敏感表单时必须保留打开表单时的 revision"
);
assert.equal(hasEmailChanged(" old@example.com ", "old@example.com"), false);
assert.equal(hasEmailChanged("old@example.com", "new@example.com"), true);
assert.equal(isEmailChangeValid("old@example.com", ""), false, "已有邮箱清空时必须阻止提交");
assert.equal(isEmailChangeValid("", ""), true, "初始为空邮箱且未修改时允许保存其他资料");
assert.equal(isEmailChangeValid("old@example.com", " new@example.com "), true);
assert.equal(isEmailChangeValid("legacy-invalid", "legacy-invalid"), true, "未修改历史邮箱时允许保存其他资料");
assert.equal(isEmailChangeValid("old@example.com", "invalid"), false);
assert.equal(sensitiveProfileModule.isValidEmail("a".repeat(249) + "@x.com"), false);
assert.ok(
  getChangedSensitiveFields(formal, sameLastFourDraft).includes("bankAccountNumber"),
  "末四位相同但完整账号不同仍必须识别为变更"
);
assert.equal(
  getSensitiveAccountSummary(" 1111 2222-3333 "),
  "•••• 3333",
  "账号只能显示规范化后的末四位摘要"
);

const nonSensitivePayload = buildNonSensitiveProfilePayload({
  phone: " 0400000000 ",
  email: " test@example.com ",
  gender: " female ",
  employmentType: " fullTime ",
  address: " 1 Queen Street ",
});
assert.deepEqual(nonSensitivePayload, {
  phone: "0400000000",
  email: "test@example.com",
  gender: "female",
  employmentType: "fullTime",
  address: "1 Queen Street",
});
assert.equal(
  "email" in buildNonSensitiveProfilePayload(nonSensitivePayload, { initialEmail: " test@example.com " }),
  false,
  "邮箱未变化时不得回传，以免覆盖并发更新"
);
assert.equal(
  buildNonSensitiveProfilePayload({ ...nonSensitivePayload, email: " new@example.com " }, { initialEmail: "test@example.com" }).email,
  "new@example.com"
);
assert.equal(
  "employmentType" in buildNonSensitiveProfilePayload({
    phone: "", email: "", gender: "", employmentType: "casual", address: "",
  }, { canEditPositionType: false }),
  false,
  "没有职位类型权限时保存 payload 不得携带 employmentType"
);
for (const sensitiveKey of [
  "birthday",
  "bankBsb",
  "bankAccountNumber",
  "superannuationCompanyName",
  "superannuationCompanyCode",
  "superannuationAccountNumber",
  "identityType",
  "identityId",
  "identityPhotoUrl",
]) {
  assert.equal(
    sensitiveKey in nonSensitivePayload,
    false,
    `旧 PUT /me payload 不得包含敏感字段 ${sensitiveKey}`
  );
}

assert.deepEqual(
  getSensitiveStatusView({
    ...pending,
    status: "Rejected",
    reviewReason: "证件号码不清晰",
  }),
  {
    statusKey: "status.rejected",
    canRefill: true,
    reviewReason: "证件号码不清晰",
    submittedAt: pending.submittedAt,
    changedFields: pending.changedFields,
  },
  "Rejected 状态必须保留拒绝原因、提交时间和变更字段，并允许重新填报"
);
assert.equal(
  getSensitiveStatusView({ ...pending, status: "Approved" }).statusKey,
  "status.approved",
  "Approved 状态文案键必须准确"
);
assert.equal(
  getSensitiveStatusView({ ...pending, status: "Superseded" }).statusKey,
  "status.superseded",
  "Superseded 状态文案键必须准确"
);

assert.equal(shouldRefreshSensitiveProfile("focus", true, "active"), true);
assert.equal(shouldRefreshSensitiveProfile("manual", true, "background"), true);
assert.equal(shouldRefreshSensitiveProfile("app-active", true, "active"), true);
assert.equal(shouldRefreshSensitiveProfile("app-active", true, "background"), false);
assert.equal(shouldRefreshSensitiveProfile("focus", false, "active"), false);
const shouldShowPendingRemoval = (
  sensitiveProfileModule as typeof sensitiveProfileModule & {
    shouldShowPendingIdentityPhotoRemoval?: (input: {
      changedFields: string[];
      pendingHasIdentityPhoto: boolean;
      formalHasIdentityPhoto: boolean;
    }) => boolean;
  }
).shouldShowPendingIdentityPhotoRemoval;
assert.equal(
  typeof shouldShowPendingRemoval,
  "function",
  "待审证件照删除提示必须由纯逻辑 helper 控制"
);
assert.equal(shouldShowPendingRemoval!({
  changedFields: ["identityPhotoUrl"],
  pendingHasIdentityPhoto: false,
  formalHasIdentityPhoto: true,
}), true);
assert.equal(shouldShowPendingRemoval!({
  changedFields: ["bankBsb"],
  pendingHasIdentityPhoto: false,
  formalHasIdentityPhoto: true,
}), false, "纯文字申请不得显示删除证件照片");
assert.equal(shouldShowPendingRemoval!({
  changedFields: ["identityPhotoUrl"],
  pendingHasIdentityPhoto: false,
  formalHasIdentityPhoto: false,
}), false, "正式资料原本无图时不得显示删除提示");
assert.equal(
  isSensitiveVersionConflict({ response: { status: 409 } }),
  true,
  "HTTP 409 必须提示重新填报"
);
assert.equal(
  isSensitiveVersionConflict(new Error("EMPLOYEE_PROFILE_SENSITIVE_VERSION_CONFLICT")),
  true,
  "业务 envelope 的版本冲突码也必须被兼容"
);

async function main() {
  const submitWithCache = (
    sensitiveProfileModule as typeof sensitiveProfileModule & {
      submitSensitiveProfileWithCache?: <TPayload, TRequest>(
        payload: TPayload,
        dependencies: {
          cancelRequestQuery: () => Promise<unknown>;
          submitRequest: (value: TPayload) => Promise<TRequest>;
          setRequestData: (value: TRequest) => void;
          refreshRequestQuery: () => Promise<unknown>;
        }
      ) => Promise<TRequest>;
    }
  ).submitSensitiveProfileWithCache;
  assert.equal(typeof submitWithCache, "function", "敏感 PUT 必须通过统一缓存协调 helper");

  const events: string[] = [];
  let releaseCancel: (() => void) | undefined;
  const cancelBarrier = new Promise<void>((resolve) => {
    releaseCancel = resolve;
  });
  const submitting = submitWithCache!(
    { bankBsb: "123-456" },
    {
      cancelRequestQuery: async () => {
        events.push("cancel");
        await cancelBarrier;
      },
      submitRequest: async (payload) => {
        events.push("put");
        return { requestId: 88, ...payload };
      },
      setRequestData: () => {
        events.push("set");
      },
      refreshRequestQuery: async () => {
        events.push("refresh");
      },
    }
  );
  await Promise.resolve();
  assert.deepEqual(events, ["cancel"], "PUT 必须等待在途旧 GET 取消完成");
  releaseCancel!();
  assert.equal((await submitting).requestId, 88);
  assert.deepEqual(
    events,
    ["cancel", "put", "set", "refresh"],
    "成功响应必须先写缓存，再触发 invalidate/refetch"
  );
  await assert.doesNotReject(() => submitWithCache!(
    { bankBsb: "654-321" },
    {
      cancelRequestQuery: async () => undefined,
      submitRequest: async (payload) => ({ requestId: 89, ...payload }),
      setRequestData: () => undefined,
      refreshRequestQuery: async () => {
        throw new Error("refetch failed");
      },
    }
  ), "PUT 已成功时，后续状态刷新失败不得误报提交失败或触发自动重提");

  const refreshOrder: string[] = [];
  const refreshResult = await refreshEmployeeProfileAfterIdentityMutation({
    refetchSensitive: async () => {
      refreshOrder.push("sensitive");
      return { isError: false };
    },
    refetchFormal: async () => {
      refreshOrder.push("formal");
      return { isError: false };
    },
  });
  assert.deepEqual(
    refreshOrder,
    ["sensitive", "formal"],
    "证件照完成或删除后必须先刷新待审快照，再刷新正式资料"
  );
  assert.deepEqual(refreshResult, { isError: false }, "刷新成功必须与服务端提交成功分开建模");

  const failedRefreshOrder: string[] = [];
  let failedRefreshResult: { isError: boolean } | undefined;
  await assert.doesNotReject(async () => {
    failedRefreshResult = await refreshEmployeeProfileAfterIdentityMutation({
      refetchSensitive: async () => {
        failedRefreshOrder.push("sensitive");
        throw new Error("refresh failed");
      },
      refetchFormal: async () => {
        failedRefreshOrder.push("formal");
        return { isError: false };
      },
    });
  }, "刷新失败不得冒充服务端上传或删除失败");
  assert.deepEqual(
    failedRefreshOrder,
    ["sensitive", "formal"],
    "一个刷新失败仍必须尝试另一个查询，且不得反向判定服务端提交失败"
  );
  assert.deepEqual(failedRefreshResult, { isError: true });

  console.log("sensitive-profile.test.ts: ok");
}

void main();
