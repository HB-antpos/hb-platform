import {
  CANCEL_NOTE_MAX_LENGTH,
  canCancelPriceTasks,
  classifyCancelOutcome,
  distinctProductCodes,
  normalizeCancelNote,
  runCancelByProduct,
} from "./cancel";
import { normalizeCancelPriceUpdateTasksResult, normalizePendingStoreCount } from "./normalize";
import type { PriceUpdateBatchResult } from "./types";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) {
    throw new Error(`${label}: expected ${e}, got ${a}`);
  }
}

// —— 入口显隐：账号会话 + 专用撤销码（旧后端阶段认 ManageProducts）；设备会话不显示
const can = (isDeviceMode: boolean, hasCancelPermission: boolean, hasManageProducts: boolean) =>
  canCancelPriceTasks({ isDeviceMode, hasCancelPermission, hasManageProducts });
assertEqual(can(false, true, false), true, "只有专用撤销码也能撤销");
assertEqual(can(false, false, true), true, "只有 ManageProducts 也能撤销（旧后端 / 别名阶段）");
assertEqual(can(false, true, true), true, "两个都有可以撤销");
assertEqual(can(false, false, false), false, "都没有不显示撤销");
assertEqual(can(true, true, true), false, "设备会话不显示撤销（没有账号权限）");

// —— 备注
assertEqual(normalizeCancelNote("   "), undefined, "空白备注不发送");
assertEqual(normalizeCancelNote("  测试店，无需执行  "), "测试店，无需执行", "备注去首尾空白");
assertEqual(normalizeCancelNote("a".repeat(CANCEL_NOTE_MAX_LENGTH + 30))?.length, CANCEL_NOTE_MAX_LENGTH, "备注截到上限");

// —— 撤销按商品：选中的任务折成不重复商品（忽略大小写、去空白、保持首次出现顺序）
assertEqual(
  distinctProductCodes([{ productCode: "P1" }, { productCode: " p1 " }, { productCode: "P2" }, { productCode: "" }]),
  ["P1", "P2"],
  "商品编码去重忽略大小写与空白，空编码丢弃"
);

// —— 响应归一化：信封（data 里）与裸结果都能读，字段大小写兼容
const enveloped = normalizeCancelPriceUpdateTasksResult({
  success: true,
  data: {
    items: [
      { taskId: 1, success: true, code: "ok", message: null },
      { taskId: 2, success: false, code: "not_pending", message: "任务已被处理" },
    ],
    cancelledCount: 1,
    skippedCount: 1,
  },
});
assertEqual([enveloped.successCount, enveloped.failedCount, enveloped.items.length], [1, 1, 2], "信封结果读取撤销数与跳过数");
const bare = normalizeCancelPriceUpdateTasksResult({ Items: [], CancelledCount: 3, SkippedCount: 0 });
assertEqual([bare.successCount, bare.failedCount], [3, 0], "裸结果与 PascalCase 字段兼容");
assertEqual(normalizeCancelPriceUpdateTasksResult(null).successCount, 0, "空响应不抛错");

// —— 确认框的「共 N 家分店」：只数还有未完成任务的店，按商品编码精确挑行
const byProduct = {
  success: true,
  data: {
    items: [
      { productCode: "P10", stores: [{ state: "PriceUpdate" }] },
      {
        productCode: "P1",
        stores: [
          { storeCode: "A", state: "PriceUpdate" },
          { storeCode: "B", state: "LabelOnly" },
          { storeCode: "C", state: "Completed" },
          { storeCode: "D", state: "Skipped" },
        ],
      },
    ],
  },
};
assertEqual(normalizePendingStoreCount(byProduct, "P1"), 2, "只数 PriceUpdate / LabelOnly 的分店（已完成、已跳过不算）");
assertEqual(normalizePendingStoreCount(byProduct, "p1"), 2, "商品编码忽略大小写，且不会误取模糊匹配的 P10");
assertEqual(normalizePendingStoreCount(byProduct, "P9"), null, "结果里没有该商品返回 null，不是 0");
assertEqual(normalizePendingStoreCount({ data: { items: "x" } }, "P1"), null, "结构异常返回 null");

// —— 结果分类
assertEqual(classifyCancelOutcome({ cancelled: 3, skipped: 0, notSubmitted: 0 }).kind, "success", "全部撤销");
assertEqual(classifyCancelOutcome({ cancelled: 2, skipped: 1, notSubmitted: 0 }).kind, "partial", "部分被跳过");
assertEqual(classifyCancelOutcome({ cancelled: 0, skipped: 2, notSubmitted: 0 }).kind, "nothing", "全部被跳过");
assertEqual(classifyCancelOutcome({ cancelled: 0, skipped: 0, notSubmitted: 0 }).kind, "nothing", "空结果按没有可撤销处理");
assertEqual(classifyCancelOutcome({ cancelled: 5, skipped: 0, notSubmitted: 2 }).kind, "interrupted", "有未提交的商品单独成类");

// —— 按商品串行撤销（异步用例；cjs 输出不支持顶层 await，包成函数并保证失败时非零退出）
async function runAsyncCases() {
  const result = (successCount: number, failedCount: number): PriceUpdateBatchResult => ({
    items: [],
    successCount,
    failedCount,
    hqSyncEnabled: false,
    hqSyncSubmittedCount: 0,
  });

  const calls: string[] = [];
  const ok = await runCancelByProduct({
    productCodes: ["P1", "P2", "P3"],
    cancelOne: async (code) => {
      calls.push(code);
      // P1 在 3 家分店撤销、P2 在 1 家撤销且另有 1 条已被处理、P3 都已被处理
      return code === "P1" ? result(3, 0) : code === "P2" ? result(1, 1) : result(0, 2);
    },
  });
  assertEqual(calls, ["P1", "P2", "P3"], "按顺序串行提交，每个商品一次请求");
  assertEqual(ok, { kind: "partial", cancelled: 4, skipped: 3, notSubmitted: 0 }, "跨商品、跨分店汇总撤销数与跳过数");

  let firstFailure: unknown = null;
  try {
    await runCancelByProduct({
      productCodes: ["P1", "P2"],
      cancelOne: async () => {
        throw new Error("network");
      },
    });
  } catch (error) {
    firstFailure = error;
  }
  assertEqual((firstFailure as Error | null)?.message, "network", "第一个商品就失败：原样抛出，整体失败");

  const later = await runCancelByProduct({
    productCodes: ["P1", "P2", "P3", "P4"],
    cancelOne: async (code) => {
      if (code === "P3") throw new Error("timeout");
      return result(2, 0);
    },
  });
  assertEqual(later, { kind: "interrupted", cancelled: 4, skipped: 0, notSubmitted: 2 }, "后面的商品失败：已撤销的保留，失败及之后的 2 个商品计为未处理");
}

runAsyncCases().catch((error) => {
  console.error(error);
  process.exit(1);
});
