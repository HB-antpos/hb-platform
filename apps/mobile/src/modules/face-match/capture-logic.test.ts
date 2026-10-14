import assert from "node:assert/strict";
import {
  CAPTURE_FILTERS,
  CAPTURE_RANGES,
  buildCaptureListParams,
  buildHitTimeline,
  captureFilterParams,
  captureScoreParts,
  clampPercent,
  describeDecision,
  describeHitNotify,
  formatCaptureFullTime,
  formatCaptureListTime,
  resolveCaptureRange,
  resolveReviewJump,
} from "./capture-logic";
import type { FaceCaptureHit } from "./types";

const hit = (patch: Partial<FaceCaptureHit> = {}): FaceCaptureHit => ({
  taskId: "t1",
  scorePercent: 87,
  band: "high",
  reviewStatus: 0,
  reviewStatusName: "Pending",
  imageAvailable: true,
  notifyState: "sent",
  notifiedAtUtc: "2026-10-14T01:21:02Z",
  notifyRecipientCount: 3,
  notifyReadCount: 1,
  ...patch,
});

// ───────── 筛选 chip → 查询参数 ─────────
assert.deepEqual(CAPTURE_FILTERS.map((filter) => filter.key), ["all", "hitOnly", "staff", "newVisitor", "repeatVisitor"]);
assert.deepEqual(captureFilterParams("all"), {});
assert.deepEqual(captureFilterParams("hitOnly"), { hitOnly: true });
assert.deepEqual(captureFilterParams("staff"), { decision: "staff" });
assert.deepEqual(captureFilterParams("newVisitor"), { decision: "newVisitor" });
assert.deepEqual(captureFilterParams("repeatVisitor"), { decision: "repeatVisitor" });
assert.deepEqual(
  buildCaptureListParams({ storeCode: "", decision: "", hitOnly: false, page: 3, pageSize: 20 }),
  { storeCode: undefined, decision: undefined, hitOnly: undefined, fromUtc: undefined, toUtc: undefined, page: 3, pageSize: 20 },
  "空值与 hitOnly=false 都不带",
);

// ───────── 时间范围：手机本地自然日 → UTC [from, to) ─────────
{
  const now = new Date(2026, 9, 14, 15, 30, 45); // 本地 2026-10-14 15:30:45
  const midnight = (dayOffset: number) => new Date(2026, 9, 14 + dayOffset, 0, 0, 0, 0).toISOString();
  assert.deepEqual(resolveCaptureRange("today", now), { fromUtc: midnight(0), toUtc: midnight(1) });
  assert.deepEqual(resolveCaptureRange("yesterday", now), { fromUtc: midnight(-1), toUtc: midnight(0) });
  assert.deepEqual(resolveCaptureRange("last7Days", now), { fromUtc: midnight(-6), toUtc: midnight(1) });
  // 午夜前后都落在各自的自然日
  const justAfter = resolveCaptureRange("today", new Date(2026, 9, 14, 0, 0, 1));
  assert.equal(justAfter.fromUtc, midnight(0));
  const lastSecond = resolveCaptureRange("today", new Date(2026, 9, 14, 23, 59, 59));
  assert.equal(lastSecond.toUtc, midnight(1));
  // 跨月：10 月 1 日的「昨天」是 9 月 30 日
  const first = resolveCaptureRange("yesterday", new Date(2026, 9, 1, 8, 0, 0));
  assert.equal(first.fromUtc, new Date(2026, 8, 30).toISOString());
  assert.deepEqual(CAPTURE_RANGES.map((range) => range.key), ["today", "yesterday", "last7Days"]);
}

// ───────── 判定与分数 ─────────
assert.deepEqual(describeDecision({ evaluated: true, decision: "staff" }), { labelKey: "capture.decision.staff", tone: "success" });
assert.deepEqual(describeDecision({ evaluated: true, decision: "newVisitor" }), { labelKey: "capture.decision.newVisitor", tone: "info" });
assert.deepEqual(describeDecision({ evaluated: true, decision: "repeatVisitor" }), { labelKey: "capture.decision.repeatVisitor", tone: "info" });
assert.deepEqual(describeDecision({ evaluated: true, decision: "lowQuality" }), { labelKey: "capture.decision.lowQuality", tone: "warning" });
assert.deepEqual(describeDecision({ evaluated: true, decision: "noMatch" }), { labelKey: "capture.decision.noMatch", tone: "neutral" });
assert.equal(describeDecision({ evaluated: false, decision: null }).labelKey, "capture.decision.notEvaluated", "没有评估行显示未比对");
assert.equal(describeDecision({ evaluated: false, decision: "staff" }).labelKey, "capture.decision.notEvaluated", "未评估时不信任残留的判定");
assert.equal(describeDecision({ evaluated: true, decision: "brandNew" }).labelKey, "capture.decision.other", "服务端新增取值按其他判定兜底");
assert.equal(clampPercent(87.6), 88);
assert.equal(clampPercent(-3), 0);
assert.equal(clampPercent(140), 100);
assert.equal(clampPercent(Number.NaN), 0);
assert.deepEqual(captureScoreParts({ staffScorePercent: 9, visitScorePercent: 31 }), [
  { labelKey: "capture.score.staff", percent: 9 },
  { labelKey: "capture.score.visit", percent: 31 },
]);
assert.deepEqual(captureScoreParts({ staffScorePercent: 91, visitScorePercent: null }), [{ labelKey: "capture.score.staff", percent: 91 }]);
assert.deepEqual(captureScoreParts({ staffScorePercent: null, visitScorePercent: null }), []);

// ───────── 通知发送情况：sent / pending / skipped，未知状态不展示 ─────────
{
  const sent = describeHitNotify(hit());
  assert.equal(sent?.labelKey, "capture.notify.sent");
  assert.deepEqual(sent?.params, { count: 3, read: 1 });
  assert.equal(describeHitNotify(hit({ notifyState: "pending" }))?.labelKey, "capture.notify.pending");
  assert.equal(describeHitNotify(hit({ notifyState: "skipped" }))?.labelKey, "capture.notify.skipped");
  assert.equal(describeHitNotify(hit({ notifyState: "someday" })), null);
  assert.equal(describeHitNotify(hit({ notifyState: "" })), null);
}

// ───────── 通知时间线：只有人数；没有的时间不编造 ─────────
{
  const sent = buildHitTimeline(hit());
  assert.deepEqual(
    sent.map((step) => [step.labelKey, step.state]),
    [
      ["capture.timeline.created", "done"],
      ["capture.timeline.notified", "done"],
      ["capture.timeline.read", "done"],
      ["capture.timeline.reviewPending", "waiting"],
    ],
  );
  assert.equal(sent[0].timeUtc, null, "服务端没有任务创建时间，不编造");
  assert.equal(sent[1].timeUtc, "2026-10-14T01:21:02Z", "通知发布时间用服务端给的");
  assert.deepEqual(sent[1].params, { count: 3 });
  assert.deepEqual(sent[2].params, { read: 1, count: 3 });
  assert.equal(sent[2].timeUtc, null, "服务端没有最近已读时间，不编造");
  assert.equal(buildHitTimeline(hit({ notifyReadCount: 0 }))[2].state, "waiting", "没人读过是空心点");
  assert.deepEqual(
    buildHitTimeline(hit({ notifyState: "pending", notifiedAtUtc: null })).map((step) => step.labelKey),
    ["capture.timeline.created", "capture.timeline.notifyPending", "capture.timeline.reviewPending"],
  );
  assert.deepEqual(
    buildHitTimeline(hit({ notifyState: "skipped", notifiedAtUtc: null, reviewStatus: 1 })).map((step) => step.labelKey),
    ["capture.timeline.created", "capture.timeline.notifySkipped", "capture.timeline.reviewDismissed"],
  );
  assert.equal(buildHitTimeline(hit({ reviewStatus: 2 })).at(-1)?.labelKey, "capture.timeline.reviewConfirmed");
  assert.deepEqual(
    buildHitTimeline(hit({ notifyState: "future-state", reviewStatus: 9 })).map((step) => step.labelKey),
    ["capture.timeline.created"],
    "不认识的通知状态与复核状态都省略，不编造含义",
  );
}

// ───────── 去复核：有命中 + 抓拍图还在 + 能打开名单页 ─────────
assert.deepEqual(resolveReviewJump(hit(), true), { enabled: true, hintKey: null });
assert.deepEqual(resolveReviewJump(null, true), { enabled: false, hintKey: "capture.detail.reviewHint.noHit" });
assert.deepEqual(resolveReviewJump(hit({ imageAvailable: false }), true), { enabled: false, hintKey: "capture.detail.reviewHint.imageGone" });
assert.deepEqual(resolveReviewJump(hit(), false), { enabled: false, hintKey: "capture.detail.reviewHint.noPermission" });
assert.equal(resolveReviewJump(hit({ imageAvailable: false }), false).hintKey, "capture.detail.reviewHint.imageGone", "图片已删除优先于权限提示");

// ───────── 时间展示 ─────────
{
  const now = new Date(2026, 9, 14, 12, 0, 0);
  const sameDay = new Date(2026, 9, 14, 9, 21, 14).toISOString();
  const otherDay = new Date(2026, 9, 13, 23, 5, 7).toISOString();
  assert.equal(formatCaptureListTime(sameDay, now), "09:21:14");
  assert.equal(formatCaptureListTime(otherDay, now), "10-13 23:05:07");
  assert.equal(formatCaptureListTime("", now), "-");
  assert.equal(formatCaptureListTime("not-a-date", now), "-");
  assert.equal(formatCaptureFullTime(sameDay), "2026-10-14 09:21:14");
  assert.equal(formatCaptureFullTime(null), "-");
}

console.log("capture-logic.test.ts: ok");
