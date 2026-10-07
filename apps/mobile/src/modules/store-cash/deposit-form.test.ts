import assert from "node:assert/strict";
import {
  addSlip,
  appendDepositPhotos,
  buildCreateDepositRequest,
  canAddSlip,
  computeDepositDifference,
  computeDepositTotal,
  createEmptySlip,
  createInitialDeposit,
  isOverrideReasonRequired,
  isOverrideReasonVisible,
  listDepositPhotos,
  patchDepositPhoto,
  removeDepositPhoto,
  resolveDepositBaseline,
  removeSlip,
  resetDepositPhotos,
  resolveDefaultCoveredRange,
  updateSlip,
  validateDepositDraft,
  type DepositDraft,
  type DepositLimits,
} from "./deposit-form";
import { resolveEntryDateRange } from "./dates";
import { createPhotoDraft, type PhotoDraft } from "./photo-drafts";

const uploaded = (key: string, guid: string): PhotoDraft => ({
  ...createPhotoDraft(key, { uri: `file://${key}.jpg`, width: 10, height: 10, fileSize: 10 }),
  status: "uploaded",
  attachmentGuid: guid,
});
const pending = (key: string): PhotoDraft =>
  createPhotoDraft(key, { uri: `file://${key}.jpg`, width: 10, height: 10, fileSize: 10 });

const limits = (patch: Partial<DepositLimits> = {}): DepositLimits => ({
  maxSlips: 3,
  maxImagesPerSlip: 3,
  differenceThreshold: 20,
  baselineAmount: 1000,
  dateRange: resolveEntryDateRange({ storeToday: "2026-10-07", maxBackfillDays: 7, canViewAllStores: false }),
  storeToday: "2026-10-07",
  ...patch,
});

const baseDraft = (patch: Partial<DepositDraft> = {}): DepositDraft => ({
  depositDate: "2026-10-07",
  coveredFromDate: null,
  coveredToDate: null,
  note: "",
  overrideReason: "",
  slips: [{ key: "s1", amountText: "1000", slipNo: "", photos: [uploaded("p1", "att-1")] }],
  ...patch,
});

const codes = (draft: DepositDraft, patch: Partial<DepositLimits> = {}) =>
  validateDepositDraft(draft, limits(patch)).map((issue) => issue.code);

// ───────── 初始草稿：日期默认门店今天，首张存单预填建议额 ─────────
{
  const draft = createInitialDeposit({
    storeToday: "2026-10-07",
    suggestedAmount: 1234.5,
    covered: { from: "2026-10-01", to: "2026-10-06" },
    firstSlipKey: "k1",
  });
  assert.equal(draft.depositDate, "2026-10-07");
  assert.equal(draft.slips.length, 1);
  assert.equal(draft.slips[0].amountText, "1234.50");
  assert.equal(draft.coveredFromDate, "2026-10-01");
  assert.equal(draft.coveredToDate, "2026-10-06");
  const noSuggest = createInitialDeposit({
    storeToday: "2026-10-07",
    suggestedAmount: null,
    covered: { from: null, to: null },
    firstSlipKey: "k1",
  });
  assert.equal(noSuggest.slips[0].amountText, "", "建议额不可算时不预填");
  const zeroSuggest = createInitialDeposit({
    storeToday: "2026-10-07",
    suggestedAmount: -5,
    covered: { from: null, to: null },
    firstSlipKey: "k1",
  });
  assert.equal(zeroSuggest.slips[0].amountText, "", "建议额不为正时不预填");
}

// ───────── 默认覆盖范围 ─────────
assert.deepEqual(
  resolveDefaultCoveredRange({ oldestUncoveredDate: "2026-10-01", latestCloseDate: "2026-10-06", fallbackTo: "2026-10-07" }),
  { from: "2026-10-01", to: "2026-10-06" },
  "到最近有日结的日期",
);
assert.deepEqual(
  resolveDefaultCoveredRange({ oldestUncoveredDate: "2026-10-01", latestCloseDate: null, fallbackTo: "2026-10-05" }),
  { from: "2026-10-01", to: "2026-10-05" },
  "没有日结明细时退回 asOfDate",
);
assert.deepEqual(
  resolveDefaultCoveredRange({ oldestUncoveredDate: null, latestCloseDate: "2026-10-06", fallbackTo: "2026-10-07" }),
  { from: null, to: null },
  "没有未覆盖营业日时留空",
);
assert.deepEqual(
  resolveDefaultCoveredRange({ oldestUncoveredDate: "2026-10-05", latestCloseDate: "2026-10-03", fallbackTo: null }),
  { from: null, to: null },
  "终点早于起点时不给出残缺范围",
);

// ───────── 合计与差异 ─────────
assert.equal(
  computeDepositTotal([
    { key: "a", amountText: "0.1", slipNo: "", photos: [] },
    { key: "b", amountText: "0.2", slipNo: "", photos: [] },
    { key: "c", amountText: "abc", slipNo: "", photos: [] },
  ]),
  0.3,
  "合计 = 各存单金额之和，无效金额按 0 计，浮点精确",
);
assert.equal(computeDepositDifference(1000, 980.5), 19.5);
assert.equal(computeDepositDifference(1000, null), null);

// ───────── 差异原因：相差超过阈值才必填，建议额 null 不要求 ─────────
assert.equal(isOverrideReasonRequired(1020, 1000, 20), false, "恰好等于阈值不要求");
assert.equal(isOverrideReasonRequired(1020.01, 1000, 20), true, "超过阈值 1 分也要求");
assert.equal(isOverrideReasonRequired(979.99, 1000, 20), true, "少存同样适用");
assert.equal(isOverrideReasonRequired(980, 1000, 20), false);
assert.equal(isOverrideReasonRequired(5000, null, 20), false, "建议额为 null 时不要求");
assert.equal(isOverrideReasonRequired(0.3, 0.1 + 0.2, 0), false, "浮点误差不会误触发");

// ───────── 差异原因基准：现金池余额（负数按 0），与后端一致 ─────────
assert.equal(resolveDepositBaseline({ poolBalance: 1200.5, suggestedDepositAmount: 1200.5 }), 1200.5);
assert.equal(resolveDepositBaseline({ poolBalance: 0, suggestedDepositAmount: null }), 0, "余额 0：基准 0，存款超过阈值就要原因");
assert.equal(resolveDepositBaseline({ poolBalance: -35.2, suggestedDepositAmount: null }), 0, "余额为负按 0");
assert.equal(resolveDepositBaseline({ poolBalance: null, suggestedDepositAmount: null }), null, "余额不可算：客户端不主动要求");
assert.equal(resolveDepositBaseline({ poolBalance: null, suggestedDepositAmount: 300 }), 300, "余额缺失时退回建议额");
assert.equal(isOverrideReasonRequired(21, resolveDepositBaseline({ poolBalance: 0, suggestedDepositAmount: null }), 20), true);
assert.equal(isOverrideReasonRequired(20, resolveDepositBaseline({ poolBalance: -5, suggestedDepositAmount: null }), 20), false, "合计恰好等于阈值不要求");
assert.equal(isOverrideReasonRequired(21, resolveDepositBaseline({ poolBalance: null, suggestedDepositAmount: null }), 20), false);

// ───────── 差异原因输入框显隐 ─────────
{
  const needReason = baseDraft({ slips: [{ key: "s1", amountText: "1100", slipNo: "", photos: [uploaded("a", "1")] }] });
  const base = { baselineAmount: 1000, differenceThreshold: 20 };
  assert.equal(isOverrideReasonVisible(baseDraft(), base), false, "没超阈值不显示");
  assert.equal(isOverrideReasonVisible(needReason, base), true, "超阈值显示");
  assert.equal(isOverrideReasonVisible(baseDraft(), { ...base, forceOverrideReason: true }), true, "服务端要求过就一定显示");
  assert.equal(
    isOverrideReasonVisible(baseDraft({ overrideReason: "已写" }), base),
    true,
    "用户已写内容时不能把输入框藏掉",
  );
  assert.equal(
    isOverrideReasonVisible(baseDraft({ slips: [{ key: "s1", amountText: "", slipNo: "", photos: [] }] }), base),
    false,
    "金额还没填完时先不显示",
  );
}

// ───────── 服务端要求过原因后（强制）：即使客户端算不出基准也必须填 ─────────
assert.deepEqual(codes(baseDraft(), { baselineAmount: null, forceOverrideReason: true }), ["overrideReasonRequired"]);
assert.deepEqual(
  codes(baseDraft({ overrideReason: "服务端要求补充" }), { baselineAmount: null, forceOverrideReason: true }),
  [],
);
// 余额 0 且无建议额：合计超过阈值也需要原因
assert.deepEqual(codes(baseDraft(), { baselineAmount: 0 }), ["overrideReasonRequired"]);

// ───────── 存单数量与增删 ─────────
{
  const draft = baseDraft();
  assert.equal(canAddSlip(draft, 3), true);
  const two = addSlip(draft, "s2", 3);
  const three = addSlip(two, "s3", 3);
  assert.equal(three.slips.length, 3);
  assert.equal(canAddSlip(three, 3), false, "达到 maxSlipsPerDeposit 后不能再加");
  assert.equal(addSlip(three, "s4", 3), three, "超限时返回原草稿");
  assert.deepEqual(removeSlip(three, "s2").slips.map((slip) => slip.key), ["s1", "s3"]);
  assert.equal(removeSlip(baseDraft(), "s1").slips.length, 1, "至少保留一张存单");
  assert.equal(updateSlip(draft, "s1", { amountText: "50" }).slips[0].amountText, "50");
  assert.equal(createEmptySlip("x").photos.length, 0);
}

// ───────── 校验 ─────────
assert.deepEqual(codes(baseDraft()), [], "合规草稿无问题");
assert.deepEqual(codes(baseDraft({ depositDate: "2026-10-08" })), ["dateOutOfRange"], "不能选未来");
assert.deepEqual(codes(baseDraft({ depositDate: "2026-09-29" })), ["dateOutOfRange"], "店长超出回溯天数");
assert.deepEqual(
  codes(baseDraft({ depositDate: "2026-01-01" }), {
    dateRange: resolveEntryDateRange({ storeToday: "2026-10-07", maxBackfillDays: 7, canViewAllStores: true }),
  }),
  [],
  "有全部分店权限不限回溯",
);
assert.deepEqual(codes(baseDraft({ depositDate: "2026-02-30" })), ["dateInvalid"]);

assert.deepEqual(codes(baseDraft({ coveredFromDate: "2026-10-01", coveredToDate: null })), ["coveredIncomplete"]);
assert.deepEqual(codes(baseDraft({ coveredFromDate: null, coveredToDate: "2026-10-01" })), ["coveredIncomplete"]);
assert.deepEqual(codes(baseDraft({ coveredFromDate: "2026-10-05", coveredToDate: "2026-10-01" })), ["coveredReversed"]);
assert.deepEqual(codes(baseDraft({ coveredFromDate: "2026-10-05", coveredToDate: "2026-10-08" })), ["coveredFuture"]);
assert.deepEqual(
  codes(baseDraft({ depositDate: "2026-10-05", coveredFromDate: "2026-10-01", coveredToDate: "2026-10-06" })),
  ["coveredFuture"],
  "覆盖营业日不能晚于存款日期",
);
assert.deepEqual(
  codes(baseDraft({ depositDate: "2026-10-05", coveredFromDate: "2026-10-01", coveredToDate: "2026-10-05" })),
  [],
);
assert.deepEqual(codes(baseDraft({ coveredFromDate: "2026-10-01", coveredToDate: "2026-10-06" })), []);
assert.deepEqual(codes(baseDraft({ coveredFromDate: null, coveredToDate: null })), [], "覆盖范围允许清空");

assert.deepEqual(codes(baseDraft({ slips: [] })), ["slipsEmpty"]);
assert.deepEqual(
  validateDepositDraft(
    baseDraft({ slips: [{ key: "s1", amountText: "", slipNo: "", photos: [uploaded("p", "a")] }] }),
    limits(),
  ),
  [{ code: "slipAmount", slipKey: "s1", issue: "empty" }],
  "金额为空只报金额问题，不叠加差异原因提示",
);
assert.deepEqual(
  codes(baseDraft({ slips: [{ key: "s1", amountText: "1000", slipNo: "", photos: [] }] })),
  ["slipPhotosMissing"],
  "每张存单至少 1 张照片",
);
assert.deepEqual(
  codes(
    baseDraft({
      slips: [
        {
          key: "s1",
          amountText: "1000",
          slipNo: "",
          photos: [uploaded("a", "1"), uploaded("b", "2"), uploaded("c", "3"), uploaded("d", "4")],
        },
      ],
    }),
  ),
  ["slipPhotosTooMany"],
  "每张存单最多 maxImagesPerSlip 张",
);
assert.deepEqual(
  codes(baseDraft({ slips: [{ key: "s1", amountText: "1000", slipNo: "x".repeat(65), photos: [uploaded("a", "1")] }] })),
  ["slipNoTooLong"],
);
assert.deepEqual(
  codes(baseDraft({ slips: Array.from({ length: 4 }, (_, i) => ({ key: `s${i}`, amountText: "250", slipNo: "", photos: [uploaded(`p${i}`, `g${i}`)] })) })),
  ["slipsTooMany"],
);

// 差异原因：合计 1100 与建议 1000 相差 100 > 20
assert.deepEqual(
  codes(baseDraft({ slips: [{ key: "s1", amountText: "1100", slipNo: "", photos: [uploaded("a", "1")] }] })),
  ["overrideReasonRequired"],
);
assert.deepEqual(
  codes(baseDraft({ overrideReason: "  ", slips: [{ key: "s1", amountText: "1100", slipNo: "", photos: [uploaded("a", "1")] }] })),
  ["overrideReasonRequired"],
  "全空白原因不算填写",
);
assert.deepEqual(
  codes(baseDraft({ overrideReason: "多", slips: [{ key: "s1", amountText: "1100", slipNo: "", photos: [uploaded("a", "1")] }] })),
  ["overrideReasonRequired"],
  "原因至少两个字",
);
assert.deepEqual(
  codes(baseDraft({ overrideReason: "多存", slips: [{ key: "s1", amountText: "1100", slipNo: "", photos: [uploaded("a", "1")] }] })),
  [],
);
assert.deepEqual(
  codes(baseDraft({ overrideReason: "多存了备用金", slips: [{ key: "s1", amountText: "1100", slipNo: "", photos: [uploaded("a", "1")] }] })),
  [],
);
assert.deepEqual(
  codes(baseDraft({ slips: [{ key: "s1", amountText: "1100", slipNo: "", photos: [uploaded("a", "1")] }] }), { baselineAmount: null }),
  [],
  "建议额为 null 时不要求原因",
);

// ───────── 请求体构造 ─────────
{
  const request = buildCreateDepositRequest(
    baseDraft({
      coveredFromDate: "2026-10-01",
      coveredToDate: "2026-10-06",
      note: "  周末营收  ",
      overrideReason: "",
      slips: [
        { key: "s1", amountText: "600.50", slipNo: " A001 ", photos: [uploaded("p1", "att-1"), uploaded("p2", "att-2")] },
        { key: "s2", amountText: "399.5", slipNo: "", photos: [uploaded("p3", "att-3")] },
      ],
    }),
    { clientRequestId: "req-1", storeCode: "S01" },
  );
  assert.deepEqual(request, {
    clientRequestId: "req-1",
    storeCode: "S01",
    depositDate: "2026-10-07",
    coveredFromDate: "2026-10-01",
    coveredToDate: "2026-10-06",
    note: "周末营收",
    overrideReason: undefined,
    slips: [
      { amount: 600.5, slipNo: "A001", attachmentGuids: ["att-1", "att-2"] },
      { amount: 399.5, slipNo: undefined, attachmentGuids: ["att-3"] },
    ],
  });
  // JSON 序列化后空值字段被省略，不会发出 null
  assert.equal(JSON.stringify(request).includes("null"), false);
  assert.equal(JSON.stringify(request).includes("overrideReason"), false);
}
{
  const withoutCovered = buildCreateDepositRequest(baseDraft(), { clientRequestId: "r", storeCode: "S01" });
  assert.equal(withoutCovered?.coveredFromDate, undefined, "清空覆盖范围后不发送起止");
  assert.equal(withoutCovered?.coveredToDate, undefined);
}
assert.equal(
  buildCreateDepositRequest(
    baseDraft({ slips: [{ key: "s1", amountText: "1000", slipNo: "", photos: [uploaded("a", "1"), pending("b")] }] }),
    { clientRequestId: "r", storeCode: "S01" },
  ),
  null,
  "任一张照片未上传完成时不得构造请求",
);
assert.equal(
  buildCreateDepositRequest(baseDraft({ slips: [{ key: "s1", amountText: "abc", slipNo: "", photos: [uploaded("a", "1")] }] }), {
    clientRequestId: "r",
    storeCode: "S01",
  }),
  null,
);

// 照片展平
assert.deepEqual(
  listDepositPhotos(
    baseDraft({
      slips: [
        { key: "s1", amountText: "1", slipNo: "", photos: [pending("a"), pending("b")] },
        { key: "s2", amountText: "1", slipNo: "", photos: [pending("c")] },
      ],
    }),
  ).map((photo) => photo.key),
  ["a", "b", "c"],
);

// ───────── 照片在存单内的增删改 ─────────
{
  const twoSlips = baseDraft({
    slips: [
      { key: "s1", amountText: "1", slipNo: "", photos: [pending("a")] },
      { key: "s2", amountText: "1", slipNo: "", photos: [pending("b")] },
    ],
  });
  const appended = appendDepositPhotos(twoSlips, "s2", [pending("c"), pending("d"), pending("e")], 3);
  assert.deepEqual(appended.draft.slips[1].photos.map((photo) => photo.key), ["b", "c", "d"], "只加到指定存单且不超过每张上限");
  assert.equal(appended.rejected, 1);
  assert.deepEqual(appended.draft.slips[0].photos.map((photo) => photo.key), ["a"], "其它存单不受影响");

  const patched = patchDepositPhoto(appended.draft, "c", { status: "uploaded", attachmentGuid: "att-c" });
  assert.equal(patched.slips[1].photos[1].attachmentGuid, "att-c", "按照片键更新，不用关心它在哪张存单");
  assert.equal(patched.slips[0].photos[0].status, "ready");

  const resetAll = resetDepositPhotos(patched);
  assert.ok(resetAll.slips.every((slip) => slip.photos.every((item) => item.status === "ready" && !item.attachmentGuid)));

  const removed = removeDepositPhoto(patched, "a");
  assert.equal(removed.slips[0].photos.length, 0);
  assert.equal(removed.slips[1].photos.length, 3);
}

console.log("deposit-form.test.ts: ok");
