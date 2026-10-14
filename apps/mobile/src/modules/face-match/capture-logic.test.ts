import assert from "node:assert/strict";
import {
  CAPTURE_FILTERS,
  CAPTURE_RANGES,
  FACE_HUB_LIST_HREF,
  FACE_HUB_REVIEW_HREF,
  HIT_STATUS_FILTERS,
  HIT_STATUS_KEYS,
  blacklistChipBadge,
  buildCaptureListParams,
  buildHitListParams,
  buildHitSummaryParams,
  buildHitTimeline,
  captureFilterParams,
  captureResultToRow,
  captureScoreParts,
  clampPercent,
  collectThumbnailTargets,
  describeDecision,
  describeHitListBar,
  describeHitNotify,
  describeHitScoreTag,
  describeHitStatusChip,
  describeHitStatusTag,
  describeRowThumbnail,
  formatCaptureFullTime,
  formatCaptureListTime,
  formatHitCount,
  hitResultToRow,
  hitStatusCount,
  isBlacklistFilter,
  mapPagesItems,
  markCaptureResultHitRead,
  markHitResultRead,
  normalizeHitStatusKey,
  planRowThumbnail,
  resolveCaptureRange,
  resolveCaptureSource,
  resolveDetailReviewAction,
  resolveInitialSelection,
  resolveReviewJump,
  reviewHref,
  type CaptureRow,
} from "./capture-logic";
import { normalizeFaceCaptureResult, normalizeFaceHitResult } from "./normalize";
import type { FaceCaptureHit, FaceHitResult, FaceHitSummary } from "./types";

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
  autoConfirmed: false,
  unread: false,
  notificationId: null,
  ...patch,
});

const hitResult = (patch: Partial<FaceHitResult> = {}): FaceHitResult =>
  normalizeFaceHitResult({
    taskId: "t1",
    captureId: null,
    storeCode: "1042",
    storeName: "TestStore",
    cameraId: "cam-1",
    capturedAtUtc: "2026-10-14T08:40:22Z",
    scorePercent: 100,
    band: "high",
    reviewStatus: 0,
    reviewStatusName: "Pending",
    autoConfirmed: false,
    imageAvailable: true,
    unread: true,
    notificationId: "n1",
    notifyState: "sent",
    notifiedAtUtc: "2026-10-14T08:40:30Z",
    notifyRecipientCount: 4,
    notifyReadCount: 0,
    canReview: true,
    ...patch,
  });

const summary = (patch: Partial<FaceHitSummary> = {}): FaceHitSummary => ({ total: 5, unread: 3, pending: 2, autoConfirmed: 2, confirmed: 0, dismissed: 1, ...patch });

// ───────── 筛选 chip → 查询参数 ─────────
// 第 2 个 chip 是「黑名单」（取代原来的「仅命中」），与 员工 / 新访客 / 回头客 互斥
assert.deepEqual(CAPTURE_FILTERS.map((filter) => filter.key), ["all", "blacklist", "staff", "newVisitor", "repeatVisitor"]);
assert.equal(CAPTURE_FILTERS[1].labelKey, "capture.filters.blacklist");
assert.deepEqual(captureFilterParams("all"), {});
assert.deepEqual(captureFilterParams("blacklist"), {}, "黑名单走命中流，不产生抓拍流参数");
assert.equal(isBlacklistFilter("blacklist"), true);
assert.equal(isBlacklistFilter("all"), false);
assert.equal(isBlacklistFilter("staff"), false);
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

// ───────── 数据源：黑名单 → 命中流；其余 → 抓拍流 ─────────
assert.deepEqual(resolveCaptureSource("blacklist", "unread"), { kind: "hits", hitStatus: "unread" });
assert.deepEqual(resolveCaptureSource("blacklist", "all"), { kind: "hits", hitStatus: "all" });
assert.deepEqual(resolveCaptureSource("staff", "unread"), { kind: "captures", params: { decision: "staff" } }, "不是黑名单时命中状态筛选不起作用");
assert.deepEqual(resolveCaptureSource("all", "all"), { kind: "captures", params: {} });

// ───────── 初始选中：只有 blacklist 时 initialHitStatus 才有意义 ─────────
assert.deepEqual(resolveInitialSelection(undefined, undefined), { filterKey: "all", hitStatus: "all" });
assert.deepEqual(resolveInitialSelection("all", "unread"), { filterKey: "all", hitStatus: "all" }, "initialFilter 不是 blacklist 时忽略 initialHitStatus");
assert.deepEqual(resolveInitialSelection("blacklist", "unread"), { filterKey: "blacklist", hitStatus: "unread" });
assert.deepEqual(resolveInitialSelection("blacklist", undefined), { filterKey: "blacklist", hitStatus: "all" });
assert.deepEqual(resolveInitialSelection("blacklist", "weird"), { filterKey: "blacklist", hitStatus: "all" }, "不认识的状态回落到全部");
assert.equal(normalizeHitStatusKey("autoConfirmed"), "autoConfirmed");
assert.equal(normalizeHitStatusKey(42), "all");
assert.deepEqual(HIT_STATUS_FILTERS.map((filter) => filter.key), [...HIT_STATUS_KEYS], "二级筛选与取值集合一致，顺序固定");
assert.deepEqual([...HIT_STATUS_KEYS], ["all", "unread", "pending", "autoConfirmed", "confirmed", "dismissed"]);

// ───────── 命中流请求参数：全部状态不发，空值不带 ─────────
assert.deepEqual(buildHitListParams({ storeCode: "", hitStatus: "all", page: 1, pageSize: 20 }), {
  storeCode: undefined,
  hitStatus: undefined,
  fromUtc: undefined,
  toUtc: undefined,
  page: 1,
  pageSize: 20,
});
assert.deepEqual(buildHitListParams({ storeCode: "1042", hitStatus: "autoConfirmed", fromUtc: "a", toUtc: "b", page: 2, pageSize: 20 }), {
  storeCode: "1042",
  hitStatus: "autoConfirmed",
  fromUtc: "a",
  toUtc: "b",
  page: 2,
  pageSize: 20,
});
assert.deepEqual(buildHitSummaryParams({ storeCode: "", fromUtc: "a", toUtc: "" }), { storeCode: undefined, fromUtc: "a", toUtc: undefined }, "汇总只带门店与时间");

// ───────── 二级筛选的数字：0 或没有时不显示 ─────────
{
  assert.equal(hitStatusCount(summary(), "all"), 5, "全部 = total");
  assert.equal(hitStatusCount(summary(), "unread"), 3);
  assert.equal(hitStatusCount(summary(), "pending"), 2);
  assert.equal(hitStatusCount(summary(), "autoConfirmed"), 2);
  assert.equal(hitStatusCount(summary(), "confirmed"), 0);
  assert.equal(hitStatusCount(summary(), "dismissed"), 1);
  assert.equal(hitStatusCount(null, "all"), null);
  assert.deepEqual(describeHitStatusChip(summary(), "all"), { labelKey: "hits.filters.all", countText: "5" });
  assert.deepEqual(describeHitStatusChip(summary(), "unread"), { labelKey: "hits.filters.unread", countText: "3" });
  assert.deepEqual(describeHitStatusChip(summary(), "confirmed"), { labelKey: "hits.filters.confirmed", countText: null }, "0 不显示数字");
  assert.deepEqual(describeHitStatusChip(null, "pending"), { labelKey: "hits.filters.pending", countText: null }, "没有汇总不显示数字");
  assert.equal(describeHitStatusChip(summary({ total: 1500 }), "all").countText, "999+");
  assert.equal(formatHitCount(0), null);
  assert.equal(formatHitCount(-2), null);
  assert.equal(formatHitCount(undefined), null);
  assert.equal(formatHitCount(Number.NaN), null);
  assert.equal(formatHitCount(7), "7");
  assert.equal(formatHitCount(99), "99");
  assert.equal(formatHitCount(100), "99+");
  assert.equal(formatHitCount(3.9), "3");
  // 「黑名单」chip 上的红色数字 = summary.unread
  assert.equal(blacklistChipBadge(summary()), "3");
  assert.equal(blacklistChipBadge(summary({ unread: 0 })), null);
  assert.equal(blacklistChipBadge(null), null);
  assert.equal(blacklistChipBadge(summary({ unread: 250 })), "99+");
}

// ───────── 列表条：共 N 条 · 未读 M + 全部标为已读 ─────────
{
  assert.deepEqual(describeHitListBar(summary(), "all", { count: 5, hasMore: false }), { total: 5, unread: 3, canMarkAll: true });
  assert.deepEqual(describeHitListBar(summary(), "pending", { count: 2, hasMore: false }), { total: 2, unread: 3, canMarkAll: true }, "总数跟随所选状态，未读始终是我的未读");
  assert.deepEqual(describeHitListBar(summary({ unread: 0 }), "all", { count: 5, hasMore: false }), { total: 5, unread: 0, canMarkAll: false }, "没有未读不出现「全部标为已读」");
  assert.deepEqual(describeHitListBar(null, "all", { count: 4, hasMore: false }), { total: 4, unread: null, canMarkAll: false }, "没取到汇总：加载完就用已加载条数");
  assert.deepEqual(describeHitListBar(null, "all", { count: 20, hasMore: true }), { total: null, unread: null, canMarkAll: false }, "没取到汇总且还有下一页：不显示总数");
}

// ───────── 命中卡片：标签与状态映射 ─────────
{
  assert.deepEqual(describeHitScoreTag({ scorePercent: 100, reviewStatus: 0 }), { percent: 100, solid: true });
  assert.deepEqual(describeHitScoreTag({ scorePercent: 72, reviewStatus: 2 }), { percent: 72, solid: true });
  assert.deepEqual(describeHitScoreTag({ scorePercent: 72.6, reviewStatus: 1 }), { percent: 73, solid: false }, "已驳回降成浅色标签");
  assert.equal(describeHitScoreTag({ scorePercent: 150, reviewStatus: 0 }).percent, 100);
  assert.deepEqual(describeHitStatusTag({ reviewStatus: 0, autoConfirmed: false }), { labelKey: "review.status.pending", tone: "warning", icon: null });
  assert.deepEqual(describeHitStatusTag({ reviewStatus: 2, autoConfirmed: true }), { labelKey: "review.autoConfirmedTag", tone: "info", icon: "robot-outline" });
  assert.deepEqual(describeHitStatusTag({ reviewStatus: 2, autoConfirmed: false }), { labelKey: "review.status.confirmed", tone: "success", icon: null });
  assert.deepEqual(describeHitStatusTag({ reviewStatus: 1, autoConfirmed: false }), { labelKey: "review.status.dismissed", tone: "neutral", icon: null });
  assert.equal(describeHitStatusTag({ reviewStatus: 1, autoConfirmed: true })?.labelKey, "review.status.dismissed", "只以复核状态为准，autoConfirmed 不改变已驳回");
  assert.equal(describeHitStatusTag({ reviewStatus: 9, autoConfirmed: false }), null, "不认识的状态不展示，不编造含义");
}

// ───────── 行模型：抓拍流与命中流统一 ─────────
{
  const capture = captureResultToRow(
    normalizeFaceCaptureResult({
      captureId: "c1",
      storeCode: "1042",
      cameraId: "cam-1",
      capturedAtUtc: "2026-10-14T08:40:22Z",
      faceCount: 1,
      evaluated: true,
      decision: "repeatVisitor",
      visitScorePercent: 100,
      faceImageId: "f1",
      sceneImageId: "s1",
      hit: { taskId: "t1", scorePercent: 100, band: "high", reviewStatus: 0, imageAvailable: true, notifyState: "sent", autoConfirmed: true, unread: true, notificationId: "n1" },
    }),
  );
  assert.equal(capture.source, "capture");
  assert.equal(capture.key, "c1");
  assert.equal(capture.hit?.autoConfirmed, true);
  assert.equal(capture.hit?.unread, true);
  assert.equal(capture.hit?.canReview, null, "抓拍流不给 canReview");
  const noHit = captureResultToRow(normalizeFaceCaptureResult({ captureId: "c2" }));
  assert.equal(noHit.hit, null);
  const fromHit = hitResultToRow(hitResult());
  assert.equal(fromHit.source, "hit");
  assert.equal(fromHit.key, "t1", "命中流行以任务编号为 key");
  assert.equal(fromHit.captureId, null, "Match 模式没有留存抓拍");
  assert.equal(fromHit.evaluated, false);
  assert.equal(fromHit.decision, null);
  assert.equal(fromHit.faceImageId, null);
  assert.equal(fromHit.isDuplicate, false);
  assert.equal(fromHit.hit?.canReview, true);
  assert.equal(fromHit.hit?.notificationId, "n1");
  const evaluated = hitResultToRow(hitResult({ decision: "newVisitor", visitScorePercent: 88, faceCount: 2 }));
  assert.equal(evaluated.evaluated, true, "有 decision 才说明有评估");
  assert.deepEqual(captureScoreParts(evaluated), [{ labelKey: "capture.score.visit", percent: 88 }]);
}

// ───────── 缩略图：每行要不要请求、显示什么 ─────────
{
  const captureRow = (patch: Partial<CaptureRow> = {}): CaptureRow => ({ ...captureResultToRow(normalizeFaceCaptureResult({ captureId: "c1", faceImageId: "f1" })), ...patch });
  // 抓拍流：有人脸小图编号才请求抓拍缩略图；没有就占位，不请求
  assert.deepEqual(planRowThumbnail(captureRow()), { type: "request", target: { kind: "capture", id: "c1" } });
  assert.deepEqual(planRowThumbnail(captureRow({ faceImageId: null })), { type: "placeholder" });
  // 没有人脸小图但带命中：取命中图（Match 模式命中图也有）；命中图已过保留期直接「已删除」，不请求
  const hitInCapture = normalizeFaceCaptureResult({ captureId: "c3", hit: { taskId: "t9", imageAvailable: true } });
  assert.deepEqual(planRowThumbnail(captureResultToRow(hitInCapture)), { type: "request", target: { kind: "hit", id: "t9" } });
  const goneHitInCapture = normalizeFaceCaptureResult({ captureId: "c3", hit: { taskId: "t9", imageAvailable: false } });
  assert.deepEqual(planRowThumbnail(captureResultToRow(goneHitInCapture)), { type: "gone" });
  // 抓拍有人脸小图时优先用抓拍缩略图
  const both = captureResultToRow(normalizeFaceCaptureResult({ captureId: "c4", faceImageId: "f4", hit: { taskId: "t4", imageAvailable: true } }));
  assert.deepEqual(planRowThumbnail(both), { type: "request", target: { kind: "capture", id: "c4" } });
  // 命中流：hit + 任务编号（即使有 captureId 也是命中图，命中流接口不给抓拍的图片编号）
  assert.deepEqual(planRowThumbnail(hitResultToRow(hitResult({ captureId: "c9" }))), { type: "request", target: { kind: "hit", id: "t1" } });
  assert.deepEqual(planRowThumbnail(hitResultToRow(hitResult({ imageAvailable: false }))), { type: "gone" });

  const request = { type: "request", target: { kind: "hit", id: "t1" } } as const;
  assert.deepEqual(describeRowThumbnail(request, undefined, true), { kind: "skeleton" }, "还没人要过 = 骨架（马上会被请求）");
  assert.deepEqual(describeRowThumbnail(request, { status: "loading" }, true), { kind: "skeleton" });
  assert.deepEqual(describeRowThumbnail(request, { status: "ok", uri: "data:image/jpeg;base64,/9j/AA==" }, true), { kind: "image", uri: "data:image/jpeg;base64,/9j/AA==" });
  assert.deepEqual(describeRowThumbnail(request, { status: "none" }, true), { kind: "placeholder", deleted: false });
  assert.deepEqual(describeRowThumbnail(request, { status: "denied" }, true), { kind: "placeholder", deleted: false }, "没权限与现在一样的占位图标");
  assert.deepEqual(describeRowThumbnail(request, { status: "failed" }, true), { kind: "placeholder", deleted: false });
  assert.deepEqual(describeRowThumbnail(request, { status: "gone" }, true), { kind: "placeholder", deleted: true });
  assert.deepEqual(describeRowThumbnail({ type: "gone" }, undefined, true), { kind: "placeholder", deleted: true });
  assert.deepEqual(describeRowThumbnail({ type: "placeholder" }, { status: "ok", uri: "x" }, true), { kind: "placeholder", deleted: false });
  // 「眼睛」隐藏：一律占位，哪怕内存里还有图
  assert.deepEqual(describeRowThumbnail(request, { status: "ok", uri: "data:x" }, false), { kind: "placeholder", deleted: false });
  assert.deepEqual(describeRowThumbnail({ type: "gone" }, undefined, false), { kind: "placeholder", deleted: false });
}

// ───────── 可见行 → 要取的缩略图（可见在前，向下预取，再向上预取；占位 / 已删除 / 重复不进入） ─────────
{
  const rows = Array.from({ length: 12 }, (_, index) => captureResultToRow(normalizeFaceCaptureResult({ captureId: `c${index}`, faceImageId: `f${index}` })));
  const ids = (targets: ReturnType<typeof collectThumbnailTargets>) => targets.map((target) => target.id);
  assert.deepEqual(ids(collectThumbnailTargets(rows, [5, 6])), ["c5", "c6", "c7", "c8", "c9", "c10", "c4", "c3"]);
  assert.deepEqual(ids(collectThumbnailTargets(rows, [0])), ["c0", "c1", "c2", "c3", "c4"], "上端没有更前的行");
  assert.deepEqual(ids(collectThumbnailTargets(rows, [11])), ["c11", "c10", "c9"], "下端没有更后的行");
  assert.deepEqual(collectThumbnailTargets(rows, []), [], "没有可见行就什么都不取");
  assert.deepEqual(ids(collectThumbnailTargets(rows, [3, 3, 4])), ["c3", "c4", "c5", "c6", "c7", "c8", "c2", "c1"], "重复的下标只算一次");
  assert.deepEqual(ids(collectThumbnailTargets(rows, [2], { before: 0, after: 0 })), ["c2"]);
  assert.deepEqual(collectThumbnailTargets(rows, [-1, 1.5, 99]), [], "非法下标 / 越界忽略");
  const mixed = [
    captureResultToRow(normalizeFaceCaptureResult({ captureId: "a1", faceImageId: null })),
    hitResultToRow(hitResult({ taskId: "h1", imageAvailable: false })),
    hitResultToRow(hitResult({ taskId: "h2" })),
    captureResultToRow(normalizeFaceCaptureResult({ captureId: "a2", faceImageId: "x" })),
  ];
  assert.deepEqual(collectThumbnailTargets(mixed, [0, 1, 2, 3]), [{ kind: "hit", id: "h2" }, { kind: "capture", id: "a2" }], "占位与已删除的行不请求");
}

// ───────── 标记已读后就地更新缓存里的行 ─────────
{
  const unread = hitResult();
  assert.equal(markHitResultRead(unread, "t1").unread, false);
  assert.equal(markHitResultRead(unread, "other"), unread, "编号对不上原样返回同一个引用");
  const read = hitResult({ unread: false });
  assert.equal(markHitResultRead(read, "t1"), read, "已读的原样返回同一个引用");
  const capture = normalizeFaceCaptureResult({ captureId: "c1", hit: { taskId: "t1", unread: true } });
  assert.equal(markCaptureResultHitRead(capture, "t1").hit?.unread, false);
  assert.equal(markCaptureResultHitRead(capture, "t2"), capture);
  const noHit = normalizeFaceCaptureResult({ captureId: "c2" });
  assert.equal(markCaptureResultHitRead(noHit, "t1"), noHit);

  const pages = {
    pages: [
      { items: [hitResult({ taskId: "a" }), hitResult({ taskId: "b" })], page: 1, pageSize: 20, hasMore: true },
      { items: [hitResult({ taskId: "c" })], page: 2, pageSize: 20, hasMore: false },
    ],
    pageParams: [1, 2],
  };
  const patched = mapPagesItems(pages, (item: FaceHitResult) => markHitResultRead(item, "b"));
  assert.ok(patched && patched !== pages);
  assert.equal(patched.pages[0].items[1].unread, false);
  assert.equal(patched.pages[0].items[0].unread, true);
  assert.equal(patched.pages[1], pages.pages[1], "没变的页保持同一个引用");
  assert.equal(patched.pageParams, pages.pageParams);
  assert.equal(mapPagesItems(pages, (item: FaceHitResult) => markHitResultRead(item, "zzz")), pages, "没有任何行变化返回原对象");
  assert.equal(mapPagesItems<FaceHitResult, typeof pages>(undefined, (item) => item), undefined);
}

// ───────── 详情里的「去复核」 ─────────
{
  const access = { canOpenUnwelcome: true, hasReviewPermission: true };
  // 命中流的行：只认服务端 canReview，为 true 才显示
  const reviewable = hitResultToRow(hitResult({ canReview: true }));
  assert.deepEqual(resolveDetailReviewAction(reviewable, access), { visible: true, enabled: true, hintKey: null, href: FACE_HUB_REVIEW_HREF });
  const notReviewable = hitResultToRow(hitResult({ canReview: false }));
  assert.equal(resolveDetailReviewAction(notReviewable, access).visible, false, "canReview 不是 true 不显示按钮");
  assert.equal(resolveDetailReviewAction(notReviewable, { canOpenUnwelcome: true, hasReviewPermission: false }).visible, false);
  // 抓拍流的行：沿用原来的判定（按钮始终在，不可用时给原因）
  const captureWithHit = captureResultToRow(normalizeFaceCaptureResult({ captureId: "c1", hit: { taskId: "t1", imageAvailable: true } }));
  assert.deepEqual(resolveDetailReviewAction(captureWithHit, access), { visible: true, enabled: true, hintKey: null, href: FACE_HUB_REVIEW_HREF });
  assert.deepEqual(resolveDetailReviewAction(captureWithHit, { canOpenUnwelcome: true, hasReviewPermission: false }), { visible: true, enabled: true, hintKey: null, href: FACE_HUB_LIST_HREF }, "只有管理 / 批准权限的去名单页");
  const captureNoHit = captureResultToRow(normalizeFaceCaptureResult({ captureId: "c2" }));
  assert.deepEqual(resolveDetailReviewAction(captureNoHit, access), { visible: true, enabled: false, hintKey: "capture.detail.reviewHint.noHit", href: FACE_HUB_REVIEW_HREF });
  assert.equal(FACE_HUB_REVIEW_HREF, "/(shell)/face-hub?tab=list&sub=review");
  assert.equal(reviewHref(true), FACE_HUB_REVIEW_HREF);
  assert.equal(reviewHref(false), FACE_HUB_LIST_HREF);
}

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
