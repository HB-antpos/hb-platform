import assert from "node:assert/strict";
import {
  changesBirthday,
  formatReviewDate,
  getReviewAvatarInitial,
  getReviewHandledAt,
  getReviewPersonLabel,
  getReviewResultTone,
  getReviewWait,
  groupChangedFields,
  summarizeStores,
} from "./review-list-presentation";
import { EMPLOYEE_PROFILE_SENSITIVE_FIELDS } from "./types";

// ---- 字段归类：9 个字段 → 4 类，顺序固定 ----
assert.deepEqual(
  groupChangedFields(["bankBsb", "bankAccountNumber", "superannuationCompanyName", "superannuationCompanyCode", "superannuationAccountNumber", "identityId"]),
  [
    { group: "bank", count: 2 },
    { group: "superannuation", count: 3 },
    { group: "identity", count: 1 },
  ],
  "线上截图里 admin 的 6 个字段应合并成 3 类"
);
assert.deepEqual(
  groupChangedFields(["identityId", "birthday", "bankBsb"]),
  [
    { group: "birthday", count: 1 },
    { group: "bank", count: 1 },
    { group: "identity", count: 1 },
  ],
  "输入顺序不影响显示顺序，生日排最前"
);
assert.deepEqual(groupChangedFields([]), []);
assert.deepEqual(
  groupChangedFields(["bankBsb", "bankBsb"]),
  [{ group: "bank", count: 1 }],
  "重复字段只算一次"
);
// 新增敏感字段时必须同步归类，否则这里会把它漏掉。
const allGrouped = groupChangedFields([...EMPLOYEE_PROFILE_SENSITIVE_FIELDS]);
assert.equal(allGrouped.reduce((sum, item) => sum + item.count, 0), EMPLOYEE_PROFILE_SENSITIVE_FIELDS.length);

assert.equal(changesBirthday(["birthday", "bankBsb"]), true);
assert.equal(changesBirthday(["bankBsb"]), false);

// ---- 等待时长：按本地日历日，分级 0-2 灰 / 3-7 橙 / 8+ 红 ----
const now = new Date(2026, 10, 11, 9, 30); // 2026-11-11 09:30 本地
const at = (y: number, m: number, d: number, h = 12, min = 0) => new Date(y, m - 1, d, h, min).toISOString();

assert.deepEqual(getReviewWait(at(2026, 11, 11, 8, 15), now), { days: 0, kind: "today", tone: "neutral" });
// 昨天 23:50 提交、现在才 9 个半小时，也算「昨天」（按日历日而不是 24 小时）。
assert.deepEqual(getReviewWait(at(2026, 11, 10, 23, 50), now), { days: 1, kind: "yesterday", tone: "neutral" });
assert.deepEqual(getReviewWait(at(2026, 11, 9), now), { days: 2, kind: "days", tone: "neutral" });
assert.deepEqual(getReviewWait(at(2026, 11, 8), now), { days: 3, kind: "days", tone: "warning" });
assert.deepEqual(getReviewWait(at(2026, 11, 4), now), { days: 7, kind: "days", tone: "warning" });
assert.deepEqual(getReviewWait(at(2026, 11, 3), now), { days: 8, kind: "days", tone: "danger" });
assert.equal(getReviewWait(at(2026, 7, 16), now).days, 118, "线上截图里 7 月 16 日提交的 admin 申请已等 118 天");
// 夏令时切换（悉尼 10 月第一个周日）跨越的日期不能差一天：10-04 → 11-11 共 38 天。
assert.equal(getReviewWait(at(2026, 10, 4, 1, 0), now).days, 38);
// 异常输入：无法解析 / 晚于现在 → 今天，不出现负数。
assert.deepEqual(getReviewWait("not-a-date", now), { days: 0, kind: "today", tone: "neutral" });
assert.deepEqual(getReviewWait(at(2026, 11, 12), now), { days: 0, kind: "today", tone: "neutral" });

// ---- 分店摘要：1～2 家全写，3 家起只写第一家 + 入口 ----
assert.deepEqual(summarizeStores([]), { label: "", total: 0, hasMore: false });
assert.deepEqual(summarizeStores(["Warners Bay"]), { label: "Warners Bay", total: 1, hasMore: false });
assert.deepEqual(summarizeStores(["Toukley", "Forster"]), { label: "Toukley · Forster", total: 2, hasMore: false });
assert.deepEqual(
  summarizeStores(["Tuggerah OutSide Closed", "Tuggerah", "Warners Bay"]),
  { label: "Tuggerah OutSide Closed", total: 3, hasMore: true }
);
assert.deepEqual(summarizeStores([" A ", "", "B", "  "]), { label: "A · B", total: 2, hasMore: false }, "空白分店名不计入");

// ---- 名字行 ----
assert.deepEqual(
  getReviewPersonLabel({ displayName: "Alice Wong", username: "alice.w", userGuid: "g1" }),
  { primary: "Alice Wong", secondary: "alice.w" }
);
assert.deepEqual(
  getReviewPersonLabel({ displayName: undefined, username: "admin", userGuid: "g1" }),
  { primary: "admin", secondary: undefined },
  "旧后端没有姓名 → 回退用户名"
);
assert.deepEqual(
  getReviewPersonLabel({ displayName: "ADMIN", username: "admin", userGuid: "g1" }),
  { primary: "admin", secondary: undefined },
  "姓名与用户名只差大小写 → 不重复显示"
);
assert.deepEqual(
  getReviewPersonLabel({ displayName: undefined, username: "", userGuid: "g-9" }),
  { primary: "g-9", secondary: undefined }
);

assert.equal(getReviewAvatarInitial("alice.w"), "A");
assert.equal(getReviewAvatarInitial("王小明"), "王");
assert.equal(getReviewAvatarInitial("  "), "?");

// ---- 日期格式 ----
const sample = new Date(2026, 6, 16, 12, 4).toISOString(); // 7 月 16 日 12:04
assert.equal(formatReviewDate(sample, "zh"), "7月16日 12:04");
assert.equal(formatReviewDate(sample, "zh", false), "7月16日");
assert.equal(formatReviewDate(sample, "en"), "16 Jul, 12:04");
assert.equal(formatReviewDate(new Date(2026, 0, 5, 9, 7).toISOString(), "en"), "5 Jan, 09:07", "单位数日 / 时 / 分补零规则");
assert.equal(formatReviewDate(new Date(2026, 0, 5, 9, 7).toISOString(), "zh"), "1月5日 09:07");
assert.equal(formatReviewDate(sample, "en", false), "16 Jul");
assert.equal(formatReviewDate("bad", "zh"), "bad");

// ---- 已处理：处理日期与色调 ----
assert.equal(getReviewHandledAt({ reviewedAt: "2026-11-10T01:00:00Z", submittedAt: "2026-11-09T01:00:00Z" }), "2026-11-10T01:00:00Z");
assert.equal(getReviewHandledAt({ reviewedAt: undefined, submittedAt: "2026-11-09T01:00:00Z" }), "2026-11-09T01:00:00Z");
assert.equal(getReviewResultTone("Approved"), "success");
assert.equal(getReviewResultTone("Rejected"), "danger");
assert.equal(getReviewResultTone("Withdrawn"), "muted");
assert.equal(getReviewResultTone("Superseded"), "muted");
assert.equal(getReviewResultTone("Pending"), "muted");

console.log("review-list-presentation tests passed");
