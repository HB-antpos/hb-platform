import assert from "node:assert/strict";
import { createEmployeeProfileApi, normalizeEmployeeProfile, normalizeSensitiveChangeRequest } from "./api-contract";

const calls: Array<{ method: string; path: string; payload?: unknown }> = [];
assert.equal(normalizeEmployeeProfile({ UserName: "legacy" }).email, "", "旧 API 缺少 Email 时必须兼容为空字符串");
// 后端 Ok(null) 会变成 204 No Content，axios 拿到的 data 是空串；必须视为「没有申请」，不能拼出 status. 这种原样翻译键。
assert.equal(normalizeSensitiveChangeRequest(""), null, "204 空响应必须视为没有敏感资料申请");
assert.equal(normalizeSensitiveChangeRequest({}), null, "缺少状态的响应必须视为没有敏感资料申请");

async function main() {
  const api = createEmployeeProfileApi({
  get: async (path: string) => {
    calls.push({ method: "GET", path });
    if (path.includes("sensitive-change-requests?")) {
      return {
        data: [
          {
            RequestId: 9,
            Status: "Rejected",
            ChangedFields: ["bankAccountNumber"],
            SubmittedAt: "2026-10-02T00:00:00Z",
            ReviewedAt: "2026-10-03T00:00:00Z",
            ReviewReason: "账号与姓名不符",
            // 历史即使意外带回敏感值也必须被白名单丢弃。
            BankAccountNumber: "must-not-leak",
          },
          { RequestId: 8, Status: "withdrawn", ChangedFields: [], SubmittedAt: "2026-10-01T00:00:00Z" },
          { RequestId: 7, Status: "SomethingNew", SubmittedAt: "2026-09-30T00:00:00Z" },
          { RequestId: 0, Status: "Pending", SubmittedAt: "2026-09-29T00:00:00Z" },
        ],
      };
    }
    if (path.endsWith("sensitive-change-request")) {
      return {
        data: {
          RequestId: 42,
          Status: "Pending",
          Birthday: "1990-03-04T00:00:00",
          BankBsb: "123-456",
          BankAccountNumber: "111122223333",
          SuperannuationCompanyName: "Future Super",
          SuperannuationCompanyCode: "FS01",
          SuperannuationAccountNumber: "SUPER-1",
          IdentityType: "passport",
          IdentityId: "P1234",
          HasIdentityPhoto: true,
          IdentityPhotoUrl: "https://cdn/pending.jpg",
          BaseSensitiveRevision: 3,
          SubmittedAt: "2026-07-16T00:00:00Z",
          ReviewReason: null,
          ChangedFields: ["bankAccountNumber", "identityPhotoUrl"],
        },
      };
    }
    return { data: { UserName: "employee-a", Phone: "0400000000", Email: " EMPLOYEE@EXAMPLE.COM ", IdentityType: "passport", SensitiveRevision: 3 } };
  },

  put: async (path: string, payload: unknown) => {
    calls.push({ method: "PUT", path, payload });
    if (path.endsWith("sensitive-change-request")) {
      return {
        data: {
          requestId: 43,
          status: "Pending",
          bankAccountNumber: "999988887777",
          changedFields: ["bankAccountNumber"],
        },
      };
    }
    return { data: { username: "employee-a", phone: "0499999999" } };
  },

  post: async (path: string, payload?: unknown) => {
    calls.push({ method: "POST", path, payload });
    return { data: { RequestId: 43, Status: "Withdrawn", ChangedFields: ["bankAccountNumber"], SubmittedAt: "2026-10-04T00:00:00Z" } };
  },
  });

  const formal = await api.getMyEmployeeProfile();
  assert.equal(formal.phone, "0400000000", "正式资料响应必须映射 phone");
  assert.equal(formal.email, "EMPLOYEE@EXAMPLE.COM", "Email/Email 大小写字段必须 trim 映射");
  assert.equal(formal.identityType, "passport", "正式资料响应必须映射 identityType");
  assert.equal(formal.sensitiveRevision, 3, "正式资料响应必须映射敏感 revision");

  const request = await api.getMySensitiveChangeRequest();
  assert.equal(request?.requestId, 42);
  assert.equal(request?.status, "Pending");
  assert.deepEqual(request?.changedFields, ["bankAccountNumber", "identityPhotoUrl"]);
  assert.equal(request?.hasIdentityPhoto, true);
  assert.equal(request?.birthday, "1990-03-04", "待审生日必须只保留日期部分");

  const updated = await api.upsertMySensitiveChangeRequest({
    birthday: " ",
    bankBsb: "",
    bankAccountNumber: "999988887777",
    superannuationCompanyName: "",
    superannuationCompanyCode: "",
    superannuationAccountNumber: "",
    identityType: "",
    identityId: "",
    expectedSensitiveRevision: 3,
  });
  assert.equal(updated.requestId, 43);
  assert.equal(
    (calls[2]?.payload as { expectedSensitiveRevision?: number }).expectedSensitiveRevision,
    3,
    "敏感申请必须提交打开表单时的 revision"
  );
  assert.equal(
    (calls[2]?.payload as { birthday?: unknown }).birthday,
    null,
    "生日不填时必须提交 null，空串无法被后端 DateTime? 反序列化"
  );

  await api.updateMyEmployeeProfile({
    phone: "0499999999",
    gender: "",
    employmentType: "",
    address: "",
  });

  const withdrawn = await api.withdrawMySensitiveChangeRequest(43);
  assert.equal(withdrawn.status, "Withdrawn", "撤回响应必须识别 Withdrawn 状态");
  assert.deepEqual(calls[4]?.payload, { requestId: 43 }, "撤回必须携带页面上的申请编号");

  const history = await api.getMySensitiveChangeHistory(20);
  assert.deepEqual(history.map((item) => item.requestId), [9, 8, 7], "无效编号的历史条目必须丢弃");
  assert.deepEqual(history.map((item) => item.status), ["Rejected", "Withdrawn", "Superseded"], "未知状态按失效处理");
  assert.equal(history[0]?.reviewReason, "账号与姓名不符");
  assert.ok(!JSON.stringify(history).includes("must-not-leak"), "历史不得保留敏感值");

  assert.deepEqual(
    calls.map(({ method, path }) => ({ method, path })),
    [
      { method: "GET", path: "/EmployeeProfiles/me" },
      { method: "GET", path: "/EmployeeProfiles/me/sensitive-change-request" },
      { method: "PUT", path: "/EmployeeProfiles/me/sensitive-change-request" },
      { method: "PUT", path: "/EmployeeProfiles/me" },
      { method: "POST", path: "/EmployeeProfiles/me/sensitive-change-request/withdraw" },
      { method: "GET", path: "/EmployeeProfiles/me/sensitive-change-requests?take=20" },
    ],
    "员工资料 API 必须使用约定路径和方法"
  );
console.log("api.test.ts: ok");
}

void main();
