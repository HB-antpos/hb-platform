import assert from "node:assert/strict";
import {
  PRICE_UPDATE_BATCH_CHUNK_SIZE,
  chunkItems,
  mergeBatchResults,
  runBatchInChunks,
} from "./batch-chunks";
import type { PriceUpdateBatchResult } from "./types";

function result(ids: number[], patch: Partial<PriceUpdateBatchResult> = {}): PriceUpdateBatchResult {
  return {
    items: ids.map((taskId) => ({ taskId, success: true, code: "ok", message: null, task: null })),
    successCount: ids.length,
    failedCount: 0,
    hqSyncEnabled: false,
    hqSyncSubmittedCount: 0,
    ...patch,
  };
}

async function main() {
  assert.equal(PRICE_UPDATE_BATCH_CHUNK_SIZE, 200, "每批 200 条");
  assert.deepEqual(chunkItems([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
  assert.deepEqual(chunkItems([], 2), []);
  assert.deepEqual(chunkItems([1, 2, 3], 0), [[1], [2], [3]], "非法批大小按 1 处理，不能死循环");
  assert.equal(chunkItems(Array.from({ length: 450 }, (_, index) => index)).length, 3, "450 条 = 200 + 200 + 50");

  const merged = mergeBatchResults([
    result([1, 2], { hqSyncEnabled: true, hqSyncSubmittedCount: 2 }),
    result([3], { hqSyncSubmittedCount: 1 }),
  ]);
  assert.deepEqual(merged.items.map((item) => item.taskId), [1, 2, 3]);
  assert.equal(merged.successCount, 3);
  assert.equal(merged.hqSyncEnabled, true, "任一批启用总部同步就算启用");
  assert.equal(merged.hqSyncSubmittedCount, 3);

  // 全部成功：按批串行调用，结果合并
  const calls: number[][] = [];
  const all = await runBatchInChunks({
    items: [1, 2, 3, 4, 5],
    size: 2,
    taskIdOf: (id) => id,
    call: async (chunk) => {
      calls.push(chunk);
      return result(chunk);
    },
  });
  assert.deepEqual(calls, [[1, 2], [3, 4], [5]]);
  assert.equal(all.successCount, 5);
  assert.equal(all.failedCount, 0);

  // 第一批就失败：原样抛出，与不分批时一致（调用方显示整体错误）
  await assert.rejects(
    runBatchInChunks({
      items: [1, 2, 3],
      size: 2,
      taskIdOf: (id) => id,
      call: async () => {
        throw new Error("network down");
      },
    }),
    /network down/
  );

  // 后面某批失败：已提交的保留，失败批及之后未提交的全部标记失败，不再继续发请求
  let callCount = 0;
  const partial = await runBatchInChunks({
    items: [1, 2, 3, 4, 5, 6],
    size: 2,
    taskIdOf: (id) => id,
    call: async (chunk) => {
      callCount += 1;
      if (callCount === 2) throw new Error("timeout");
      return result(chunk);
    },
  });
  assert.equal(callCount, 2, "失败后停止，不再发后面的批次");
  assert.equal(partial.successCount, 2, "第一批已成功提交，不回滚");
  assert.equal(partial.failedCount, 4);
  assert.deepEqual(
    partial.items.filter((item) => !item.success).map((item) => item.taskId),
    [3, 4, 5, 6],
    "失败批和之后没提交的任务都要标记失败，它们仍留在未完成里可重试"
  );
  assert.ok(partial.items.filter((item) => !item.success).every((item) => item.message === "timeout" && item.code === "failed"));

  // 空列表不发请求
  let emptyCalls = 0;
  const empty = await runBatchInChunks({
    items: [],
    taskIdOf: (id: number) => id,
    call: async () => {
      emptyCalls += 1;
      return result([]);
    },
  });
  assert.equal(emptyCalls, 0);
  assert.equal(empty.items.length, 0);

  console.log("price-updates/batch-chunks.test.ts: ok");
}

void main();
