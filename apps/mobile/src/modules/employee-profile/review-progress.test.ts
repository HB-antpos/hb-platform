import assert from "node:assert/strict";
import {
  buildSensitiveReviewTimeline,
  canResubmitSensitiveRequest,
  canWithdrawSensitiveRequest,
  getEarlierSensitiveRequests,
  getResubmitSection,
} from "./review-progress";

const base = { requestId: 7, submittedAt: "2026-10-01T01:00:00Z" };

// 待审：提交完成、审核进行中、结果未开始。
const pending = buildSensitiveReviewTimeline({ ...base, status: "Pending" });
assert.deepEqual(pending.map((step) => step.state), ["done", "current", "upcoming"]);
assert.equal(pending[0].time, base.submittedAt);
assert.equal(pending[2].time, undefined);

// 批准与驳回：结果步骤带审核时间，驳回为 failed。
const approved = buildSensitiveReviewTimeline({ ...base, status: "Approved", reviewedAt: "2026-10-02T01:00:00Z" });
assert.deepEqual(approved.map((step) => step.state), ["done", "done", "done"]);
assert.equal(approved[2].labelKey, "progress.timeline.approved");
assert.equal(approved[2].time, "2026-10-02T01:00:00Z");
const rejected = buildSensitiveReviewTimeline({ ...base, status: "Rejected", reviewedAt: "2026-10-02T01:00:00Z" });
assert.equal(rejected[2].state, "failed");
assert.equal(rejected[2].labelKey, "progress.timeline.rejected");

// 撤回与失效：审核步骤中止，不能显示为已审核。
const withdrawn = buildSensitiveReviewTimeline({ ...base, status: "Withdrawn", reviewedAt: "2026-10-01T02:00:00Z" });
assert.deepEqual(withdrawn.map((step) => step.state), ["done", "stopped", "stopped"]);
assert.equal(withdrawn[2].labelKey, "progress.timeline.withdrawn");
const superseded = buildSensitiveReviewTimeline({ ...base, status: "Superseded" });
assert.equal(superseded[2].labelKey, "progress.timeline.superseded");

// 只有待审且编号有效才能撤回；已批准不再提供重提入口。
assert.equal(canWithdrawSensitiveRequest({ ...base, status: "Pending" }), true);
assert.equal(canWithdrawSensitiveRequest({ ...base, requestId: 0, status: "Pending" }), false);
assert.equal(canWithdrawSensitiveRequest({ ...base, status: "Rejected" }), false);
assert.equal(canWithdrawSensitiveRequest(null), false);
assert.equal(canResubmitSensitiveRequest({ ...base, status: "Rejected" }), true);
assert.equal(canResubmitSensitiveRequest({ ...base, status: "Withdrawn" }), true);
assert.equal(canResubmitSensitiveRequest({ ...base, status: "Pending" }), true);
assert.equal(canResubmitSensitiveRequest({ ...base, status: "Approved" }), false);
assert.equal(canResubmitSensitiveRequest(undefined), false);

// 重提定位到第一个可识别的变更分组。
assert.equal(getResubmitSection(["unknown", "bankAccountNumber", "birthday"]), "banking");
assert.equal(getResubmitSection(["identityPhotoUrl"]), "identity");
assert.equal(getResubmitSection(["superannuationCompanyCode"]), "superannuation");
assert.equal(getResubmitSection([]), "personal");

// 历史列表去掉当前申请。
assert.deepEqual(
  getEarlierSensitiveRequests([{ requestId: 9 }, { requestId: 7 }, { requestId: 5 }], 7).map((item) => item.requestId),
  [9, 5]
);

console.log("review-progress tests passed");
