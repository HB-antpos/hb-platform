import assert from "node:assert/strict";
import { test } from "node:test";
import {
  normalizeBatchCreate,
  normalizeBatchDetail,
  normalizeBatchList,
  normalizeCartonDetail,
  normalizeCartonList,
  normalizeLabelsPrinted,
  normalizeVoidBlank,
  readPackingError,
} from "./api-normalization";
import {
  BATCH_QUICK_COUNTS,
  BLANK_CARTON_LIMIT,
  MAX_BATCH_COUNT,
  batchRequestKey,
  blankPrintStatus,
  blankRemaining,
  chunkGuids,
  clampBatchCount,
  computeBatchProgress,
  createRequestIdReuse,
  isBatchApiMissing,
  markCartonsPrinted,
  parseBatchCountInput,
  pendingLabelCartons,
  runLabelQueue,
  selectedCartons,
  splitSelection,
  summarizeVoidResult,
  toggleSelectAll,
  toggleSelected,
} from "./batch-logic";
import { packingErrorMessage } from "./packing-errors";
import { batchRoute, cartonRoute } from "./pack-routes";
import {
  buildBlankCartonLabelPayload,
  buildCartonLabelPayload,
  blankCartonLabelPreview,
  isUnusedBlank,
} from "./packing-logic";
import { CARTON_STATUS, type PackCartonSummary } from "./types";

const wire = {
  cartonGuid: "c-1",
  cartonNo: "BX2611150001",
  status: 1,
  ownerCode: "TEST",
  ownerName: "TestStore",
  originCode: "TEST",
  originName: "TestStore",
  createdByName: "Sean",
  createdAtUtc: "2026-11-15T01:00:00Z",
  updatedAtUtc: "2026-11-15T01:00:00Z",
};

function carton(overrides: Partial<PackCartonSummary> & Pick<PackCartonSummary, "cartonGuid">): PackCartonSummary {
  return {
    ...normalizeCartonDetail({ ...wire, cartonNo: overrides.cartonGuid.toUpperCase() }),
    isBlank: true,
    ...overrides,
  };
}

function batch(count: number, printed: number[] = []): PackCartonSummary[] {
  return Array.from({ length: count }, (_, index) =>
    carton({
      cartonGuid: `g-${index + 1}`,
      cartonNo: `BX26111500${String(index + 1).padStart(2, "0")}`,
      batchGuid: "b-1",
      batchSeq: index + 1,
      batchCount: count,
      labelPrintCount: printed.includes(index + 1) ? 1 : 0,
    }),
  );
}

// ---- 归一化 ----

test("旧后端缺批量箱标字段：isBlank=false、labelPrintCount=0，blankCount 为 null（据此隐藏入口）", () => {
  const detail = normalizeCartonDetail(wire);
  assert.equal(detail.isBlank, false);
  assert.equal(detail.labelPrintCount, 0);
  assert.equal(detail.batchGuid, null);
  assert.equal(detail.batchSeq, null);
  assert.equal(detail.activatedAtUtc, null);
  const list = normalizeCartonList({ items: [wire], packingCount: 1 });
  assert.equal(list.blankCount, null, "缺失与 0 要区分");
  assert.equal(normalizeCartonList({ items: [], blankCount: 0 }).blankCount, 0);
});

test("新字段原样读入；null 字段不输出按缺失处理", () => {
  const detail = normalizeCartonDetail({
    ...wire,
    isBlank: true,
    batchGuid: "b-1",
    batchSeq: 3,
    batchCount: 20,
    labelPrintCount: 2,
    labelPrintedAtUtc: "2026-11-15T01:30:00Z",
  });
  assert.equal(detail.isBlank, true);
  assert.equal(detail.batchSeq, 3);
  assert.equal(detail.batchCount, 20);
  assert.equal(detail.labelPrintCount, 2);
  assert.equal(detail.activatedAtUtc, null, "没启用就是 null");
  assert.equal(isUnusedBlank(detail), true);
  assert.equal(isUnusedBlank({ ...detail, activatedAtUtc: "2026-11-15T02:00:00Z" }), false, "启用后不再是未启用空箱标");
});

test("批量创建 / 批次列表 / 批次详情 / 已打印上报 / 作废结果的归一化", () => {
  const created = normalizeBatchCreate({
    success: true,
    data: { batchGuid: "b-1", duplicate: true, count: 2, firstCartonNo: "BX1", lastCartonNo: "BX2", createdAtUtc: "t", cartons: [wire] },
  });
  assert.equal(created.duplicate, true);
  assert.equal(created.cartons.length, 1);
  assert.equal(normalizeBatchCreate({ batchGuid: "b-2", count: 1 }).duplicate, false, "duplicate 缺失按 false");

  const list = normalizeBatchList({ batches: [{ batchGuid: "b-1", count: 20, firstCartonNo: "BX1", lastCartonNo: "BX20", createdAtUtc: "t", createdByName: "Sean", printedCount: 12, unactivatedCount: 20, voidedCount: 0 }] });
  assert.equal(list[0].printedCount, 12);
  assert.deepEqual(normalizeBatchList({}), [], "没有 batches 按空列表");

  const detail = normalizeBatchDetail({ batch: { batchGuid: "b-1", count: 1 }, cartons: [wire] });
  assert.equal(detail.batch.printedCount, 0);
  assert.equal(detail.cartons[0].cartonGuid, "c-1");

  assert.deepEqual(normalizeLabelsPrinted({ updated: 3, skipped: [{ cartonGuid: "x", reason: "NOT_FOUND" }] }), {
    updated: 3,
    skipped: [{ cartonGuid: "x", reason: "NOT_FOUND" }],
  });
  assert.deepEqual(normalizeVoidBlank({ voided: ["a"] }), { voided: ["a"], skipped: [] });
  assert.throws(() => normalizeBatchDetail({ cartons: [] }), /Invalid warehouse packing response/);
});

test("BLANK_LIMIT_EXCEEDED：带 existing / limit 时给出还能建几张，没带时用通用提示", () => {
  const error = {
    response: { status: 409, data: { success: false, errorCode: "BLANK_LIMIT_EXCEEDED", data: { existing: 290, limit: 300 } } },
  };
  assert.deepEqual(readPackingError(error).blankLimit, { existing: 290, limit: 300 });
  const t = (key: string, options?: Record<string, unknown>) => `${key}|${JSON.stringify(options ?? {})}`;
  assert.equal(packingErrorMessage(error, t, "zh"), 'errors.BLANK_LIMIT_EXCEEDED|{"existing":290,"limit":300,"remaining":10}');
  const plain = { response: { status: 409, data: { success: false, errorCode: "BLANK_LIMIT_EXCEEDED" } } };
  assert.equal(readPackingError(plain).blankLimit, null);
  assert.equal(packingErrorMessage(plain, t, "zh"), "errors.BLANK_LIMIT_EXCEEDED_PLAIN|{}");
  assert.equal(blankRemaining(300), 0);
  assert.equal(blankRemaining(null), null);
  assert.equal(BLANK_CARTON_LIMIT, 300);
});

// ---- 数量 ----

test("数量上限 100、快捷值、输入解析", () => {
  assert.equal(MAX_BATCH_COUNT, 100);
  assert.deepEqual([...BATCH_QUICK_COUNTS], [10, 20, 50, 100]);
  assert.equal(clampBatchCount(0), 1);
  assert.equal(clampBatchCount(101), 100);
  assert.equal(clampBatchCount(Number.NaN), 1);
  assert.equal(parseBatchCountInput(""), null);
  assert.equal(parseBatchCountInput("0"), null);
  assert.equal(parseBatchCountInput("20"), 20);
  assert.equal(parseBatchCountInput("999"), 100, "超过上限按上限");
  assert.equal(parseBatchCountInput("1a2"), 12, "只留数字");
});

test("clientRequestId：同样的请求失败重试沿用同一个号，请求变了换新号，成功后 reset", () => {
  let seq = 0;
  const reuse = createRequestIdReuse(() => `id-${++seq}`);
  const key20 = batchRequestKey({ ownerCode: "TEST", count: 20, destinationType: 0, destinationCode: null, carrierGuid: null });
  assert.equal(reuse.get(key20), "id-1");
  assert.equal(reuse.get(key20), "id-1", "失败重试复用");
  const key30 = batchRequestKey({ ownerCode: "TEST", count: 30, destinationType: 0, destinationCode: null, carrierGuid: null });
  assert.equal(reuse.get(key30), "id-2", "数量变了换新号");
  reuse.reset();
  assert.equal(reuse.get(key30), "id-3", "创建成功后换新号");
});

// ---- 批次进度 / 下一张 / 断点续打 ----

test("批次进度：已打印 / 下一张 / 等待，按批内序号排序", () => {
  const cartons = batch(6, [1, 2, 3]).reverse(); // 乱序进来
  const progress = computeBatchProgress(cartons);
  assert.equal(progress.total, 6);
  assert.equal(progress.printed, 3);
  assert.equal(progress.remaining, 3);
  assert.equal(progress.next?.batchSeq, 4, "下一张是批内顺序第一张没打印的");
  assert.deepEqual(
    progress.rows.map((row) => `${row.carton.batchSeq}:${row.state}`),
    ["1:printed", "2:printed", "3:printed", "4:next", "5:waiting", "6:waiting"],
  );
  assert.equal(progress.ratio, 0.5);
});

test("批次进度：全部打完没有下一张；空批次进度为 0；作废的箱不计入", () => {
  assert.equal(computeBatchProgress(batch(3, [1, 2, 3])).next, null);
  assert.equal(computeBatchProgress([]).ratio, 0);
  const withVoided = batch(4, [1]);
  withVoided[2] = { ...withVoided[2], status: CARTON_STATUS.voided };
  const progress = computeBatchProgress(withVoided);
  assert.equal(progress.total, 3, "作废的不算");
  assert.equal(progress.next?.batchSeq, 2);
});

test("断点续打只取还没打印的；中间漏打的也会补上", () => {
  const cartons = batch(5, [1, 2, 4]); // 3 和 5 没打
  assert.deepEqual(pendingLabelCartons(cartons).map((item) => item.batchSeq), [3, 5]);
  const after = markCartonsPrinted(cartons, ["g-3"], "2026-11-15T03:00:00Z");
  assert.equal(after.find((item) => item.cartonGuid === "g-3")?.labelPrintCount, 1);
  assert.deepEqual(pendingLabelCartons(after).map((item) => item.batchSeq), [5]);
  assert.deepEqual(chunkGuids([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
  assert.equal(chunkGuids(Array.from({ length: 250 }, (_, index) => index)).length, 3, "上报每次最多 100 个");
});

test("列表里的打印状态文案来源：没打印过 / 打印过几次", () => {
  assert.deepEqual(blankPrintStatus({ labelPrintCount: 0, labelPrintedAtUtc: null }), { kind: "notPrinted" });
  assert.deepEqual(blankPrintStatus({ labelPrintCount: 2, labelPrintedAtUtc: "t" }), { kind: "printed", count: 2, at: "t" });
});

// ---- 连续打印执行器 ----

test("执行器：一张一张打，每成功一张就记一张并上报", async () => {
  const cartons = batch(3);
  const order: string[] = [];
  const reported: string[][] = [];
  const result = await runLabelQueue(cartons, {
    print: async (item) => void order.push(`print:${item.batchSeq}`),
    report: async (guids) => void reported.push(guids),
    onPrinted: (item) => order.push(`record:${item.batchSeq}`),
  });
  assert.deepEqual(order, ["print:1", "record:1", "print:2", "record:2", "print:3", "record:3"], "一张打完才打下一张");
  assert.deepEqual(reported, [["g-1"], ["g-2"], ["g-3"]], "每成功一张就报一张");
  assert.equal(result.printed.length, 3);
  assert.equal(result.failed, null);
  assert.deepEqual(result.unreported, []);
});

test("执行器：断连 / 卡纸时停在这一张，这一张不记为已打印，已成功的都已上报", async () => {
  const cartons = batch(5);
  const reported: string[] = [];
  const result = await runLabelQueue(cartons, {
    print: async (item) => {
      if (item.batchSeq === 3) throw new Error("Printer connection lost");
    },
    report: async (guids) => void reported.push(...guids),
  });
  assert.deepEqual(result.printed.map((item) => item.batchSeq), [1, 2]);
  assert.equal(result.failed?.carton.batchSeq, 3);
  assert.deepEqual(reported, ["g-1", "g-2"], "失败的那一张不上报");
  assert.deepEqual(result.unreported, []);
});

test("执行器：用户暂停时当前这张打完就停，下一张没发", async () => {
  const cartons = batch(4);
  let stop = false;
  const printedSeq: number[] = [];
  const result = await runLabelQueue(cartons, {
    print: async (item) => {
      printedSeq.push(item.batchSeq ?? 0);
      if (item.batchSeq === 2) stop = true;
    },
    report: async () => undefined,
    shouldStop: () => stop,
  });
  assert.deepEqual(printedSeq, [1, 2]);
  assert.equal(result.stopped, true);
  assert.equal(result.failed, null);
});

test("执行器：上报失败不影响继续打印，缓冲到下一张成功后一起补报；结束时仍失败则列入 unreported", async () => {
  const cartons = batch(3);
  let calls = 0;
  const reportedBatches: string[][] = [];
  const flaky = await runLabelQueue(cartons, {
    print: async () => undefined,
    report: async (guids) => {
      calls += 1;
      if (calls === 1) throw new Error("network");
      reportedBatches.push(guids);
    },
  });
  assert.equal(flaky.printed.length, 3, "上报失败不停打印");
  assert.deepEqual(reportedBatches[0], ["g-1", "g-2"], "第一张没报上，和第二张一起补报");
  assert.deepEqual(flaky.unreported, []);

  const down = await runLabelQueue(batch(2), {
    print: async () => undefined,
    report: async () => {
      throw new Error("offline");
    },
  });
  assert.equal(down.printed.length, 2);
  assert.deepEqual(down.unreported, ["g-1", "g-2"], "一直报不上就在结果里告诉界面");
});

test("执行器：失败退出前（finally）也会把已成功的补报上去", async () => {
  let first = true;
  const reported: string[] = [];
  const result = await runLabelQueue(batch(3), {
    print: async (item) => {
      if (item.batchSeq === 2) throw new Error("jam");
    },
    report: async (guids) => {
      if (first) {
        first = false;
        throw new Error("network blip");
      }
      reported.push(...guids);
    },
  });
  assert.equal(result.failed?.carton.batchSeq, 2);
  assert.deepEqual(reported, ["g-1"], "退出时重试上报，第一张最终报上了");
  assert.deepEqual(result.unreported, []);
});

// ---- 多选与作废结果 ----

test("多选：点选 / 全选 / 取消全选；列表刷新后消失的选中项自动剔除", () => {
  const cartons = batch(3);
  let selected: ReadonlySet<string> = new Set();
  selected = toggleSelected(selected, "g-1");
  selected = toggleSelected(selected, "g-2");
  assert.equal(selected.size, 2);
  selected = toggleSelected(selected, "g-1");
  assert.deepEqual([...selected], ["g-2"]);
  assert.equal(toggleSelectAll(cartons, selected).size, 3, "没全选时变全选");
  assert.equal(toggleSelectAll(cartons, new Set(["g-1", "g-2", "g-3"])).size, 0, "已全选再点变清空");
  assert.deepEqual(selectedCartons(cartons.slice(0, 2), new Set(["g-2", "g-3"])).map((item) => item.cartonGuid), ["g-2"]);
});

test("作废：选中里只有未启用的空箱标会发给后端，其余在结果里提示「N 只不是空箱标，已跳过」", () => {
  const blanks = batch(2);
  const used = carton({ cartonGuid: "g-used", isBlank: true, activatedAtUtc: "2026-11-15T02:00:00Z" });
  const normal = carton({ cartonGuid: "g-normal", isBlank: false });
  const sealed = carton({ cartonGuid: "g-sealed", isBlank: true, status: CARTON_STATUS.sealed });
  const chosen = [...blanks, used, normal, sealed];
  const { blanks: sendable, others } = splitSelection(chosen);
  assert.deepEqual(sendable.map((item) => item.cartonGuid), ["g-1", "g-2"]);
  assert.equal(others.length, 3);
  const summary = summarizeVoidResult(chosen.length, sendable.length, { voided: ["g-1", "g-2"], skipped: [] });
  assert.deepEqual(summary, { voided: 2, notBlank: 3, otherSkipped: 0 });
  // 服务端又跳过一张（NOT_BLANK）和一张找不到：一并计入。
  const merged = summarizeVoidResult(5, 2, {
    voided: ["g-1"],
    skipped: [
      { cartonGuid: "g-2", reason: "NOT_BLANK" },
      { cartonGuid: "gx", reason: "NOT_FOUND" },
    ],
  });
  assert.deepEqual(merged, { voided: 1, notBlank: 4, otherSkipped: 1 });
  assert.deepEqual(summarizeVoidResult(3, 0, null), { voided: 0, notBlank: 3, otherSkipped: 0 }, "全不是空箱标时不发请求");
});

// ---- 批次接口缺失 ----

test("批次接口 404 且不是业务错误码才算后端没有这个接口", () => {
  assert.equal(isBatchApiMissing(404, "ERR_BAD_REQUEST"), true, "axios 自己的 code 不算业务码");
  assert.equal(isBatchApiMissing(404, null), true);
  assert.equal(isBatchApiMissing(404, "BATCH_NOT_FOUND"), false);
  assert.equal(isBatchApiMissing(500, null), false);
});

// ---- 标签内容 ----

test("空箱标标签内容：无去向留横线，无 SKU / PCS，LABEL i / n，操作人是打印的人", () => {
  const blank = carton({ cartonGuid: "g-1", cartonNo: "BX2611150001", batchSeq: 1, batchCount: 20 });
  const payload = buildBlankCartonLabelPayload(blank, new Date(2026, 10, 15, 12, 40), "Sean");
  assert.equal(payload.blankLabel?.index, 1);
  assert.equal(payload.blankLabel?.total, 20);
  assert.equal(payload.operatorName, "Sean");
  assert.equal(payload.printedAt, "15 NOV 12:40");
  const preview = blankCartonLabelPreview(payload);
  assert.equal(preview.toRule, true);
  assert.equal(preview.destinationName, "");
  assert.equal(preview.statLine, "");
  assert.equal(preview.referenceLine, "LABEL 01 / 20");
  assert.equal(preview.timeLine, "15 NOV 12:40 SEAN");
  // 启用后（有内容）仍走内容标：带 SKU / PCS，没有 blankLabel。
  const content = buildCartonLabelPayload({ ...blank, skuCount: 2, pieceCount: 30, sealedByName: null }, new Date(2026, 10, 15, 15, 24));
  assert.equal(content.blankLabel, undefined);
  assert.equal(content.skuCount, 2);
});

test("路由：创建并打印带 autoprint 与份数；扫码启用后带 activated=1；不带参数时与旧路径一致", () => {
  assert.equal(batchRoute("b 1"), "/packing/batch/b%201");
  assert.equal(batchRoute("b1", { autoPrint: true, copies: 2 }), "/packing/batch/b1?autoprint=1&copies=2");
  assert.equal(batchRoute("b1", { autoPrint: true, copies: 1, duplicate: true }), "/packing/batch/b1?autoprint=1&duplicate=1");
  assert.equal(cartonRoute("c1"), "/packing/carton/c1");
  assert.equal(cartonRoute("c1", { activated: true }), "/packing/carton/c1?activated=1");
});
