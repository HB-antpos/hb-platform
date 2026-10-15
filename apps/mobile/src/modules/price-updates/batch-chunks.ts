import type { PriceUpdateBatchResult, PriceUpdateBatchResultItem } from "./types";

/**
 * 单次请求最多提交的任务数。批量处理现在可以全选几百项，一次全发容易超时，
 * 也会让一次网络失败波及全部任务；按 200 一批串行提交，失败范围可控。
 */
export const PRICE_UPDATE_BATCH_CHUNK_SIZE = 200;

export function chunkItems<T>(items: readonly T[], size: number = PRICE_UPDATE_BATCH_CHUNK_SIZE): T[][] {
  const chunkSize = Math.max(1, Math.trunc(size));
  const chunks: T[][] = [];
  for (let index = 0; index < items.length; index += chunkSize) {
    chunks.push(items.slice(index, index + chunkSize));
  }
  return chunks;
}

export function mergeBatchResults(results: readonly PriceUpdateBatchResult[]): PriceUpdateBatchResult {
  return {
    items: results.flatMap((result) => result.items),
    successCount: results.reduce((sum, result) => sum + result.successCount, 0),
    failedCount: results.reduce((sum, result) => sum + result.failedCount, 0),
    hqSyncEnabled: results.some((result) => result.hqSyncEnabled),
    hqSyncSubmittedCount: results.reduce((sum, result) => sum + result.hqSyncSubmittedCount, 0),
  };
}

function buildFailedResult(taskIds: readonly number[], message: string): PriceUpdateBatchResult {
  const items: PriceUpdateBatchResultItem[] = taskIds.map((taskId) => ({
    taskId,
    success: false,
    code: "failed",
    message,
    task: null,
  }));
  return {
    items,
    successCount: 0,
    failedCount: items.length,
    hqSyncEnabled: false,
    hqSyncSubmittedCount: 0,
  };
}

function describeError(error: unknown): string {
  return error instanceof Error && error.message ? error.message : String(error);
}

/**
 * 分批调用批量接口并合并结果。
 * - 第一批就失败：原样抛出，和不分批时的行为一致（整体失败，调用方显示错误）。
 * - 后面某一批失败：已提交的批次不回滚；该批及之后没提交的任务都标记为失败并带上错误信息，
 *   返回部分结果。它们仍留在未完成里，用户可以稍后重试，不会出现「以为都处理了」。
 */
export async function runBatchInChunks<T>(options: {
  items: readonly T[];
  taskIdOf: (item: T) => number;
  call: (chunk: T[]) => Promise<PriceUpdateBatchResult>;
  size?: number;
}): Promise<PriceUpdateBatchResult> {
  const chunks = chunkItems(options.items, options.size);
  const results: PriceUpdateBatchResult[] = [];
  for (let index = 0; index < chunks.length; index += 1) {
    try {
      results.push(await options.call(chunks[index]));
    } catch (error) {
      if (index === 0) {
        throw error;
      }
      const notSubmitted = chunks.slice(index).flat();
      results.push(buildFailedResult(notSubmitted.map(options.taskIdOf), describeError(error)));
      break;
    }
  }
  return mergeBatchResults(results);
}
