import assert from "node:assert/strict";
import {
  BAND_LABEL_KEYS,
  canStepReference,
  canSubmitVerdict,
  formatSimilarityPercent,
  getReviewStep,
  getVerdictBlockReasonKey,
  referenceIndexes,
  stepReferenceIndex,
} from "./review-logic";

// ───────── 相似度：整数百分比，夹在 0-100 ─────────
assert.equal(formatSimilarityPercent({ scorePercent: 86 }), 86);
assert.equal(formatSimilarityPercent({ scorePercent: 86.6 }), 87);
assert.equal(formatSimilarityPercent({ scorePercent: 140 }), 100);
assert.equal(formatSimilarityPercent({ scorePercent: -5 }), 0);
assert.equal(formatSimilarityPercent({ scorePercent: Number.NaN }), 0);

// 档位文案键只有「较高 / 一般」两档，由服务端 band 决定
assert.deepEqual(BAND_LABEL_KEYS, { high: "review.band.high", normal: "review.band.normal" });

// ───────── 两人复核流程 ─────────
assert.equal(getReviewStep({ nextStep: "first" }), "first");
assert.equal(getReviewStep({ nextStep: "second" }), "second");
assert.equal(getReviewStep({ nextStep: "none" }), "done");
assert.equal(getReviewStep({ nextStep: "unexpected" }), "done", "未知值视为已结束，不开放判断");

// ───────── 判断按钮可用性：只认服务端 canReview ─────────
{
  const ok = { canReview: true, imageAvailable: true, nextStep: "first" };
  assert.equal(canSubmitVerdict(ok, false), true);
  assert.equal(canSubmitVerdict({ ...ok, nextStep: "second" }, false), true);
  // 第二位必须是不同的人：服务端对「第一复核人本人」返回 canReview=false → 按钮不可用
  assert.equal(canSubmitVerdict({ ...ok, canReview: false, nextStep: "second" }, false), false);
  assert.equal(canSubmitVerdict({ ...ok, imageAvailable: false }, false), false, "抓拍图已按保留期删除不能再复核");
  assert.equal(canSubmitVerdict({ ...ok, nextStep: "none" }, false), false, "已结束的任务");
  assert.equal(canSubmitVerdict(ok, true), false, "功能未开启时只读");

  // 不可用原因的优先级：已结束 > 图片已删 > 只读 > 无权复核
  assert.equal(getVerdictBlockReasonKey(ok, false), null);
  assert.equal(getVerdictBlockReasonKey({ ...ok, nextStep: "none" }, false), "review.blocked.done");
  assert.equal(getVerdictBlockReasonKey({ ...ok, imageAvailable: false }, false), "review.blocked.imageGone");
  assert.equal(getVerdictBlockReasonKey({ ...ok, imageAvailable: false, nextStep: "none" }, false), "review.blocked.done");
  assert.equal(getVerdictBlockReasonKey(ok, true), "review.blocked.readOnly");
  assert.equal(getVerdictBlockReasonKey({ ...ok, canReview: false }, false), "review.blocked.cannotReview");
}

// ───────── 参考照片切换 ─────────
assert.deepEqual(referenceIndexes({ referencePhotoCount: 3 }), [0, 1, 2]);
assert.deepEqual(referenceIndexes({ referencePhotoCount: 0 }), []);
assert.deepEqual(referenceIndexes({ referencePhotoCount: -1 }), []);
assert.equal(stepReferenceIndex(0, 3, 1), 1);
assert.equal(stepReferenceIndex(2, 3, 1), 2, "到头不循环");
assert.equal(stepReferenceIndex(0, 3, -1), 0, "到头不循环");
assert.equal(stepReferenceIndex(5, 0, 1), 0);
assert.equal(canStepReference(0, 3, 1), true);
assert.equal(canStepReference(0, 3, -1), false);
assert.equal(canStepReference(2, 3, 1), false);
assert.equal(canStepReference(0, 1, 1), false, "只有一张时不显示切换");
assert.equal(canStepReference(0, 0, 1), false);

console.log("review-logic.test.ts: ok");
