import assert from "node:assert/strict";
import {
  createEmployeeProfileReviewApi,
  normalizeEmployeeProfileReviewDetail,
  normalizeEmployeeProfileReviewList,
} from "./api";

async function main() {
const list = normalizeEmployeeProfileReviewList({
  Items: [
    {
      RequestId: 42,
      UserGuid: "user-42",
      Username: "employee42",
      Status: "Pending",
      BaseSensitiveRevision: 3,
      SubmittedAt: "2026-07-16T10:00:00Z",
      ChangedFields: ["bankAccountNumber", "identityId", "unexpected"],
      StoreCodes: ["BNE"],
      StoreNames: ["Brisbane"],
      // 列表即使收到越界字段也必须丢弃，避免完整账号进入列表状态或日志。
      BankAccountNumber: "123456789",
      ReviewReason: "must-not-leak",
    },
  ],
  Total: 1,
  Page: 1,
  PageSize: 20,
});

assert.equal(list.total, 1);
assert.deepEqual(list.items[0], {
  requestId: 42,
  userGuid: "user-42",
  username: "employee42",
  status: "Pending",
  baseSensitiveRevision: 3,
  submittedAt: "2026-07-16T10:00:00Z",
  reviewedAt: undefined,
  changedFields: ["bankAccountNumber", "identityId"],
  storeCodes: ["BNE"],
  storeNames: ["Brisbane"],
});
assert.equal("bankAccountNumber" in list.items[0], false);
assert.equal("reviewReason" in list.items[0], false);

const detail = normalizeEmployeeProfileReviewDetail({
  requestId: 42,
  userGuid: "user-42",
  username: "employee42",
  status: "Pending",
  baseSensitiveRevision: 3,
  submittedAt: "2026-07-16T10:00:00Z",
  changedFields: ["birthday", "bankAccountNumber", "identityPhotoUrl"],
  storeCodes: ["BNE"],
  storeNames: ["Brisbane"],
  birthday: "1998-06-12T00:00:00",
  bankBsb: "064000",
  bankAccountNumber: "123456789",
  superannuationCompanyName: "Demo Super",
  superannuationCompanyCode: "DS001",
  superannuationAccountNumber: "SUP-123456",
  identityType: "Driver Licence",
  identityId: "DL-123456",
  hasIdentityPhoto: true,
  identityPhotoUrl: "https://signed.example/proposed",
  identityPhotoUrlExpiresAt: "2026-07-16T10:05:00Z",
  submittedBy: "employee42",
  currentSnapshot: {
    Birthday: "1998-06-21T00:00:00",
    bankBsb: "062000",
    bankAccountNumber: "987654321",
    superannuationCompanyName: "Old Super",
    superannuationCompanyCode: "OS001",
    superannuationAccountNumber: "SUP-654321",
    identityType: "Passport",
    identityId: "P-654321",
    hasIdentityPhoto: true,
    identityPhotoUrl: "https://signed.example/current",
  },
});

assert.equal(detail.bankAccountNumber, "123456789");
assert.equal(detail.currentSnapshot.bankAccountNumber, "987654321");
// 生日纳入敏感字段白名单，只保留日期部分。
assert.deepEqual(detail.changedFields, ["birthday", "bankAccountNumber", "identityPhotoUrl"]);
assert.equal(detail.birthday, "1998-06-12");
assert.equal(detail.currentSnapshot.birthday, "1998-06-21");
assert.equal(detail.identityPhotoUrl, "https://signed.example/proposed");

const calls: Array<{ method: string; path: string; payload?: unknown; params?: unknown }> = [];
const api = createEmployeeProfileReviewApi({
  async get(path, config) {
    calls.push({ method: "GET", path, params: config?.params });
    return path.endsWith("/42") ? { data: detail } : { data: list };
  },
  async post(path, payload) {
    calls.push({ method: "POST", path, payload });
    return { data: detail };
  },
});

await api.getRequests({ page: 2, pageSize: 10, status: "Pending" });
await api.getDetail(42);
const approveResult = await api.approve(42, " checked ");
const rejectResult = await api.reject(42, " incorrect account ");
await assert.rejects(() => api.reject(42, "   "), /reason is required/i);

assert.deepEqual(approveResult, { requestId: 42, status: "Pending" });
assert.deepEqual(rejectResult, { requestId: 42, status: "Pending" });
assert.equal("bankAccountNumber" in approveResult, false);
assert.equal("identityPhotoUrl" in rejectResult, false);

assert.deepEqual(calls, [
  {
    method: "GET",
    path: "/EmployeeProfiles/review/change-requests",
    params: { page: 2, pageSize: 10, status: "Pending", search: undefined },
  },
  { method: "GET", path: "/EmployeeProfiles/review/change-requests/42", params: undefined },
  {
    method: "POST",
    path: "/EmployeeProfiles/review/change-requests/42/approve",
    payload: { reason: "checked" },
  },
  {
    method: "POST",
    path: "/EmployeeProfiles/review/change-requests/42/reject",
    payload: { reason: "incorrect account" },
  },
]);

}

void main();
