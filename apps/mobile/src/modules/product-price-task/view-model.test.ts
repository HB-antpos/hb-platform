import assert from "node:assert/strict";
import type { PriceUpdateRunState } from "../price-updates/batch-runner";
import { createTask } from "../price-updates/test-helpers";
import {
  buildProductTaskCardModel,
  pickProductTask,
  resolveRunOutcome,
  resolveRunSteps,
} from "./view-model";

function runState(patch: Partial<PriceUpdateRunState> = {}): PriceUpdateRunState {
  return {
    mode: "applyAndPrint",
    phase: "applying",
    apply: { total: 1, success: 0, failed: 0, targetChanged: 0 },
    hqSyncEnabled: false,
    hqSyncSubmittedCount: 0,
    prints: [],
    printerUnavailable: false,
    stopped: false,
    ...patch,
  };
}

const printItem = (status: PriceUpdateRunState["prints"][number]["status"], awaiting = false) => ({
  taskId: 1,
  task: createTask(),
  status,
  printedAwaitingConfirm: awaiting,
});

// ---- pickProductTask：精确匹配编码 + 分店，忽略已完成与模糊命中的其它商品 ----
{
  const tasks = [
    createTask({ id: 1, productCode: "P001", storeCode: "1001" }),
    createTask({ id: 2, productCode: "P0011", storeCode: "1001" }),
    createTask({ id: 3, productCode: "P001", storeCode: "1002" }),
    createTask({ id: 4, productCode: "P001", storeCode: "1001", status: "Completed" }),
  ];
  assert.equal(pickProductTask(tasks, { productCode: "p001 " }, "1001")?.id, 1, "编码忽略大小写与空白，且不命中 P0011");
  assert.equal(pickProductTask(tasks, { productCode: "P001" }, "1002")?.id, 3, "按分店过滤");
  assert.equal(pickProductTask(tasks, { productCode: "P002" }, "1001"), null, "没有任务返回 null");
  assert.equal(pickProductTask(tasks, { productCode: "" }, "1001"), null, "空编码不匹配任何任务");
  assert.equal(pickProductTask(tasks, { productCode: "P001" }, null), null, "没有分店不匹配");
}

// 同店同品多条（理论上不会出现）：优先需改价，其次最早发起
{
  const tasks = [
    createTask({ id: 10, kind: "LabelOnly", firstInitiatedAtUtc: "2026-09-01T00:00:00Z" }),
    createTask({ id: 11, kind: "PriceUpdate", firstInitiatedAtUtc: "2026-09-10T00:00:00Z" }),
    createTask({ id: 12, kind: "PriceUpdate", firstInitiatedAtUtc: "2026-09-05T00:00:00Z" }),
  ];
  assert.equal(pickProductTask(tasks, { productCode: "P001" }, "1001")?.id, 12);
}

// ---- buildProductTaskCardModel ----
{
  const priceUpdate = buildProductTaskCardModel(
    createTask({ storeRetailPrice: 10, targetRetailPrice: 12, isOverdue: true, overdueByDays: 3, waitingDays: 10 })
  );
  assert.equal(priceUpdate.variant, "priceUpdate");
  assert.equal(priceUpdate.overdue, true);
  assert.equal(priceUpdate.overdueByDays, 3);
  assert.equal(priceUpdate.comparison.fromPrice, 10);
  assert.equal(priceUpdate.comparison.toPrice, 12);
  assert.equal(priceUpdate.comparison.delta, 2);
  assert.equal(priceUpdate.comparison.direction, "up");

  // 待换标签：对比的是货架旧标签 → 本店现价
  const labelOnly = buildProductTaskCardModel(
    createTask({ kind: "LabelOnly", shelfRetailPrice: 9, storeRetailPrice: 8, targetRetailPrice: null })
  );
  assert.equal(labelOnly.variant, "labelOnly");
  assert.equal(labelOnly.comparison.fromPrice, 9);
  assert.equal(labelOnly.comparison.toPrice, 8);
  assert.equal(labelOnly.comparison.direction, "down");
}

// ---- resolveRunSteps ----
{
  assert.deepEqual(resolveRunSteps(null, true), { apply: "waiting", print: "waiting", complete: "waiting" });
  assert.deepEqual(resolveRunSteps(null, false), { apply: "skipped", print: "waiting", complete: "waiting" });

  assert.deepEqual(resolveRunSteps(runState(), true), { apply: "active", print: "waiting", complete: "waiting" });

  const printing = runState({
    phase: "printing",
    apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
    prints: [printItem("printing")],
  });
  assert.deepEqual(resolveRunSteps(printing, true), { apply: "done", print: "active", complete: "waiting" });

  const done = runState({
    phase: "done",
    apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
    prints: [printItem("printed")],
  });
  assert.deepEqual(resolveRunSteps(done, true), { apply: "done", print: "done", complete: "done" });

  // 仅更新：只有第一步
  const applyOnly = runState({
    mode: "applyOnly",
    phase: "done",
    apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
  });
  assert.deepEqual(resolveRunSteps(applyOnly, true), { apply: "done", print: "skipped", complete: "skipped" });

  // 标签机不可用：改价成功，后两步跳过
  const noPrinter = runState({
    phase: "done",
    printerUnavailable: true,
    apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
  });
  assert.deepEqual(resolveRunSteps(noPrinter, true), { apply: "done", print: "skipped", complete: "skipped" });

  // 改价失败：不会打印
  const applyFailed = runState({
    phase: "done",
    apply: { total: 1, success: 0, failed: 1, targetChanged: 0 },
  });
  assert.deepEqual(resolveRunSteps(applyFailed, true), { apply: "failed", print: "skipped", complete: "skipped" });

  // 打印失败：任务留在待换标签，不回写
  const printFailed = runState({
    phase: "done",
    apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
    prints: [printItem("failed")],
  });
  assert.deepEqual(resolveRunSteps(printFailed, true), { apply: "done", print: "failed", complete: "skipped" });

  // 已出纸但回写失败：打印算完成、回写失败（重试只补回写）
  const confirmFailed = runState({
    phase: "done",
    apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
    prints: [printItem("failed", true)],
  });
  assert.deepEqual(resolveRunSteps(confirmFailed, true), { apply: "done", print: "done", complete: "failed" });

  // 待换标签任务没有改价这一步
  const labelOnly = runState({
    phase: "done",
    apply: { total: 0, success: 0, failed: 0, targetChanged: 0 },
    prints: [printItem("printed")],
  });
  assert.deepEqual(resolveRunSteps(labelOnly, false), { apply: "skipped", print: "done", complete: "done" });
}

// ---- resolveRunOutcome ----
{
  assert.equal(resolveRunOutcome(null), "running");
  assert.equal(resolveRunOutcome(runState()), "running");

  assert.equal(
    resolveRunOutcome(
      runState({ phase: "done", apply: { total: 1, success: 0, failed: 1, targetChanged: 1 } })
    ),
    "targetChanged"
  );
  assert.equal(
    resolveRunOutcome(runState({ phase: "done", apply: { total: 1, success: 0, failed: 1, targetChanged: 0 } })),
    "applyFailed"
  );
  assert.equal(
    resolveRunOutcome(
      runState({
        phase: "done",
        apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
        prints: [printItem("printed")],
      })
    ),
    "completed"
  );
  assert.equal(
    resolveRunOutcome(
      runState({
        phase: "done",
        apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
        prints: [printItem("failed")],
      })
    ),
    "printFailed"
  );
  assert.equal(
    resolveRunOutcome(
      runState({
        mode: "applyOnly",
        phase: "done",
        apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
      })
    ),
    "priceUpdatedLabelPending"
  );
  assert.equal(
    resolveRunOutcome(
      runState({
        phase: "done",
        printerUnavailable: true,
        apply: { total: 1, success: 1, failed: 0, targetChanged: 0 },
      })
    ),
    "priceUpdatedLabelPending"
  );
}

console.log("product-price-task view-model tests passed");
