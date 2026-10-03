import assert from "node:assert/strict";
import {
  normalizeBranchHourlyRevenueSnapshot,
  normalizeBranchRevenueRows,
  normalizeDailyRevenueSnapshot,
  normalizeDailyRevenueRows,
  normalizeExecutiveBranchPerformance,
  normalizeHourlyRevenueSnapshot,
  normalizeHourlyRevenueRows,
  pollExecutiveBranchPerformance,
  pollRevenueDetailSnapshot,
  resolveCurrentPeriodFallback,
} from "./api";

const branchRows = normalizeBranchRevenueRows([
  {
    BranchCode: "S1",
    BranchName: "分店一",
    Revenue: 120,
    RevenueLY: 100,
    OrderCount: 6,
    OrderCountLY: 5,
    Aov: 20,
    AovLY: 20,
  },
]);

assert.equal(branchRows[0]?.compareRevenue, 100);
assert.equal(branchRows[0]?.revenueDelta, 20);
assert.equal(branchRows[0]?.revenueDeltaRatio, 0.2);
assert.equal(branchRows[0]?.transactions, 6);
assert.equal(branchRows[0]?.compareTransactions, 5);
assert.equal(branchRows[0]?.averageTransaction, 20);
assert.equal(branchRows[0]?.compareAverageTransaction, 20);

const highGrowthRows = normalizeBranchRevenueRows([
  {
    BranchCode: "S2",
    Revenue: 250,
    RevenueLY: 100,
  },
]);

assert.equal(highGrowthRows[0]?.revenueDeltaRatio, 1.5);

const hourlyRows = normalizeHourlyRevenueRows([
  {
    Hour: "09:00",
    Revenue: 80,
    RevenueLY: 100,
    OrderCount: 4,
    OrderCountLY: 5,
  },
]);

assert.equal(hourlyRows[0]?.hour, 9);
assert.equal(hourlyRows[0]?.label, "09:00");
assert.equal(hourlyRows[0]?.compareRevenue, 100);
assert.equal(hourlyRows[0]?.revenueDelta, -20);
assert.equal(hourlyRows[0]?.transactions, 4);
assert.equal(hourlyRows[0]?.compareTransactions, 5);
assert.equal(hourlyRows[0]?.averageTransaction, 20);
assert.equal(hourlyRows[0]?.compareAverageTransaction, 20);

const dailyRows = normalizeDailyRevenueRows([
  {
    Date: "2026-07-04T00:00:00",
    BranchCode: "S1",
    BranchName: "分店一",
    Revenue: 150,
    RevenueLY: 0,
    OrderCount: 7,
    OrderCountLY: 0,
  },
]);

assert.equal(dailyRows[0]?.date, "2026-07-04");
assert.equal(dailyRows[0]?.compareRevenue, 0);
assert.equal(dailyRows[0]?.revenueDeltaRatio, null);
assert.equal(dailyRows[0]?.averageTransaction, 150 / 7);
assert.equal(dailyRows[0]?.compareAverageTransaction, 0);

const completeHourlyDetail = normalizeHourlyRevenueSnapshot({
  items: [{ Hour: 9, Revenue: 80, OrderCount: 4 }],
  statisticsPending: false,
  statisticsExpectedItemCount: 1,
  statisticsSnapshotItemCount: 1,
  statisticUpdatedAt: "2026-09-06T01:02:03Z",
  cacheVersion: "v-report-1",
});
assert.equal(completeHourlyDetail.isComplete, true, "分时完整包络才可以进入首行可见计时");
assert.equal(completeHourlyDetail.rows[0]?.hour, 9);
assert.equal(completeHourlyDetail.statisticUpdatedAt, "2026-09-06T01:02:03Z");
assert.equal(completeHourlyDetail.cacheVersion, "v-report-1");

const completeDailyDetail = normalizeDailyRevenueSnapshot({
  items: [{ Date: "2026-07-04", BranchCode: "S1", Revenue: 150, OrderCount: 7 }],
  statisticsPending: false,
  statisticsExpectedItemCount: 1,
  statisticsSnapshotItemCount: 1,
});
assert.equal(completeDailyDetail.isComplete, true, "逐日完整包络才可以进入首行可见计时");
assert.equal(completeDailyDetail.rows[0]?.date, "2026-07-04");

const backendDataEnvelopeHourlyDetail = normalizeHourlyRevenueSnapshot({
  success: true,
  data: [{ Hour: 10, Revenue: 100, OrderCount: 5 }],
  statisticsPending: false,
  statisticsExpectedItemCount: 1,
  statisticsSnapshotItemCount: 1,
});
assert.equal(
  backendDataEnvelopeHourlyDetail.isComplete,
  true,
  "后端根级 data 数组与完整性元数据必须归一化为严格完整快照",
);
assert.equal(backendDataEnvelopeHourlyDetail.rows[0]?.hour, 10);

// 日报累计对比一次取多家店：后端按「店×小时」返回，Hour 是 "HH:00" 字符串
const multiBranchHourly = normalizeBranchHourlyRevenueSnapshot({
  success: true,
  data: [
    { hour: "09:00", branchCode: "1013", branchName: "Orion", revenue: 120, revenueLY: 90, orderCount: 6, orderCountLY: 5 },
    { hour: "09:00", branchCode: "1017", branchName: "Waratah", revenue: 40, revenueLY: 0, orderCount: 2, orderCountLY: 0 },
  ],
  statisticsPending: false,
  statisticsExpectedItemCount: 2,
  statisticsSnapshotItemCount: 2,
});
assert.equal(multiBranchHourly.isComplete, true, "多店小时快照同样按条目数核对完整性");
assert.deepEqual(
  multiBranchHourly.rows.map((row) => row.id),
  ["1013:9", "1017:9"],
  "同一小时的多家店必须保留分店，id 不能重复",
);
assert.equal(multiBranchHourly.rows[0]?.branchName, "Orion");
assert.equal(multiBranchHourly.rows[0]?.hour, 9);
assert.equal(multiBranchHourly.rows[0]?.compareRevenue, 90);
assert.equal(multiBranchHourly.rows[1]?.transactions, 2);
assert.equal(
  normalizeBranchHourlyRevenueSnapshot({ data: [{ hour: "09:00", branchCode: "1013", revenue: 1 }] }).isComplete,
  false,
  "缺完整性元数据时多店小时快照同样 fail-closed",
);

for (const [label, snapshot] of [
  ["裸数组", normalizeHourlyRevenueSnapshot([{ Hour: 9, Revenue: 80 }])],
  ["缺少 Pending", normalizeHourlyRevenueSnapshot({
    items: [{ Hour: 9, Revenue: 80 }],
    statisticsExpectedItemCount: 1,
    statisticsSnapshotItemCount: 1,
  })],
  ["缺少预期数量", normalizeHourlyRevenueSnapshot({
    items: [{ Hour: 9, Revenue: 80 }],
    statisticsPending: false,
    statisticsSnapshotItemCount: 1,
  })],
  ["缺少快照数量", normalizeHourlyRevenueSnapshot({
    items: [{ Hour: 9, Revenue: 80 }],
    statisticsPending: false,
    statisticsExpectedItemCount: 1,
  })],
  ["数量不一致", normalizeHourlyRevenueSnapshot({
    items: [{ Hour: 9, Revenue: 80 }],
    statisticsPending: false,
    statisticsExpectedItemCount: 2,
    statisticsSnapshotItemCount: 1,
  })],
] as const) {
  assert.equal(snapshot.isComplete, false, `${label}不得被当作可展示的完整分时快照`);
  assert.equal(snapshot.statisticsPending, true, `${label}必须 fail-closed 并继续视为未完成`);
}

function createBranchSnapshot(count: number, pending: boolean, expected = 28) {
  return normalizeExecutiveBranchPerformance({
    success: true,
    data: Array.from({ length: count }, (_, index) => ({
      BranchCode: `S${String(index + 1).padStart(2, "0")}`,
      BranchName: `分店 ${index + 1}`,
      Revenue: 100 + index,
    })),
    statisticsPending: pending,
    statisticsExpectedBranchCount: expected,
    statisticsSnapshotBranchCount: count,
    statisticUpdatedAt: "2026-09-06T01:02:03Z",
    cacheVersion: "v-report-1",
  });
}

const partialSnapshot = createBranchSnapshot(5, true);
assert.equal(partialSnapshot.rows.length, 5);
assert.equal(partialSnapshot.statisticsPending, true);
assert.equal(partialSnapshot.statisticsExpectedBranchCount, 28);
assert.equal(partialSnapshot.statisticsSnapshotBranchCount, 5);
assert.equal(partialSnapshot.isComplete, false);
assert.equal(partialSnapshot.statisticUpdatedAt, "2026-09-06T01:02:03Z");
assert.equal(partialSnapshot.cacheVersion, "v-report-1");

const countMismatchSnapshot = createBranchSnapshot(20, false);
assert.equal(
  countMismatchSnapshot.isComplete,
  false,
  "即使 pending=false，快照数少于预期分店数也不能当作完整数据",
);

const missingMetadataSnapshot = normalizeExecutiveBranchPerformance({
  data: [{ BranchCode: "INCOMPLETE", Revenue: 10 }],
  statisticsExpectedBranchCount: 1,
  statisticsSnapshotBranchCount: 1,
});
assert.equal(missingMetadataSnapshot.isComplete, false, "缺少 pending 完整性标记时不得展示营业额排行");
assert.equal(missingMetadataSnapshot.statisticsPending, true);

const legacySnapshot = normalizeExecutiveBranchPerformance([{ BranchCode: "S1", Revenue: 10 }]);
assert.equal(legacySnapshot.isComplete, false, "旧版裸列表响应不得绕过营业额完整性元数据");
assert.equal(legacySnapshot.statisticsPending, true);
assert.equal(legacySnapshot.statisticsExpectedBranchCount, null);

async function runPollingAssertions() {
  const snapshots = [
    createBranchSnapshot(5, true),
    createBranchSnapshot(20, true),
    createBranchSnapshot(28, false),
  ];
  const waits: number[] = [];
  const completed = await pollExecutiveBranchPerformance(
    async () => snapshots.shift() ?? createBranchSnapshot(28, false),
    {
      delaysMs: [200, 350],
      wait: async (delayMs) => {
        waits.push(delayMs);
      },
    },
  );

  assert.equal(completed.isComplete, true);
  assert.equal(completed.rows.length, 28, "部分非空快照必须继续追数到 28 家完整分店");
  assert.equal(completed.pollingAttemptCount, 3);
  assert.equal(completed.pollingExhausted, false);
  assert.deepEqual(waits, [200, 350], "退避必须属于同一次有界轮询会话");

  let elapsedMs = 0;
  const recoveredAfterLegacyWindow = await pollExecutiveBranchPerformance(
    async () => createBranchSnapshot(
      elapsedMs > 800 ? 28 : 20,
      elapsedMs <= 800,
    ),
    {
      deadlineMs: 5_000,
      now: () => elapsedMs,
      wait: async (delayMs) => { elapsedMs += delayMs; },
    },
  );
  assert.equal(
    recoveredAfterLegacyWindow.isComplete,
    true,
    "营业额排行持续 Pending 超过旧 550ms 窗口后仍须在截止前恢复 Fresh",
  );
  assert.ok(elapsedMs > 800 && elapsedMs <= 5_000);

  elapsedMs = 0;
  const exhaustedAtDeadline = await pollRevenueDetailSnapshot(
    async () => normalizeHourlyRevenueSnapshot({
      items: [{ Hour: 9, Revenue: 80 }],
      statisticsPending: true,
      statisticsExpectedItemCount: 2,
      statisticsSnapshotItemCount: 1,
    }),
    {
      deadlineMs: 1_000,
      now: () => elapsedMs,
      wait: async (delayMs) => { elapsedMs += delayMs; },
    },
  );
  assert.equal(exhaustedAtDeadline.isComplete, false);
  assert.equal(exhaustedAtDeadline.pollingExhausted, true, "分时追数只能在真实截止时间后耗尽");
  assert.equal(elapsedMs, 1_000);

  const abortController = new AbortController();
  let resolveLateRequest: ((snapshot: ReturnType<typeof createBranchSnapshot>) => void) | undefined;
  let visibleBranchCode = "";
  const lateRequest = pollExecutiveBranchPerformance(
    () => new Promise((resolve) => {
      resolveLateRequest = resolve;
    }),
    { signal: abortController.signal, delaysMs: [] },
  ).then((snapshot) => {
    visibleBranchCode = snapshot.rows[0]?.branchCode ?? "";
  });

  await Promise.resolve();
  abortController.abort();
  const currentRequest = await pollExecutiveBranchPerformance(
    async () => normalizeExecutiveBranchPerformance({
      data: [{ BranchCode: "NEW", Revenue: 20 }],
      statisticsPending: false,
      statisticsExpectedBranchCount: 1,
      statisticsSnapshotBranchCount: 1,
    }),
    { delaysMs: [] },
  );
  visibleBranchCode = currentRequest.rows[0]?.branchCode ?? "";
  resolveLateRequest?.(normalizeExecutiveBranchPerformance({
    data: [{ BranchCode: "OLD", Revenue: 10 }],
    statisticsPending: false,
    statisticsExpectedBranchCount: 1,
    statisticsSnapshotBranchCount: 1,
  }));

  await assert.rejects(lateRequest, (error: unknown) => (
    error instanceof Error && error.name === "AbortError"
  ));
  assert.equal(visibleBranchCode, "NEW", "旧请求晚返回不得覆盖新会话结果");

  const detailSnapshots = [
    normalizeHourlyRevenueSnapshot({
      items: [{ Hour: 9, Revenue: 80 }],
      statisticsPending: true,
      statisticsExpectedItemCount: 2,
      statisticsSnapshotItemCount: 1,
    }),
    normalizeHourlyRevenueSnapshot({
      items: [{ Hour: 9, Revenue: 80 }, { Hour: 10, Revenue: 100 }],
      statisticsPending: false,
      statisticsExpectedItemCount: 2,
      statisticsSnapshotItemCount: 2,
    }),
  ];
  const detailWaits: number[] = [];
  const completedDetail = await pollRevenueDetailSnapshot(
    async () => detailSnapshots.shift() ?? completeHourlyDetail,
    {
      delaysMs: [200],
      wait: async (delayMs) => {
        detailWaits.push(delayMs);
      },
    },
  );
  assert.equal(completedDetail.isComplete, true);
  assert.equal(completedDetail.rows.length, 2, "分时轮询必须在同一冷会话中等待完整快照");
  assert.deepEqual(detailWaits, [200]);

  const detailAbortController = new AbortController();
  let resolveLateDetail: ((snapshot: typeof completeHourlyDetail) => void) | undefined;
  const lateDetailRequest = pollRevenueDetailSnapshot(
    () => new Promise<typeof completeHourlyDetail>((resolve) => {
      resolveLateDetail = resolve;
    }),
    { signal: detailAbortController.signal, delaysMs: [] },
  );
  await Promise.resolve();
  detailAbortController.abort();
  resolveLateDetail?.(completeHourlyDetail);
  await assert.rejects(lateDetailRequest, (error: unknown) => (
    error instanceof Error && error.name === "AbortError"
  ), "分时/逐日轮询在请求晚返回后仍必须服从取消信号");
}

async function runCurrentPeriodFallbackAssertions() {
  const exhausted = { ...createBranchSnapshot(26, true), pollingExhausted: true, pollingAttemptCount: 6 };
  const currentComplete = createBranchSnapshot(26, false, 26);
  let currentOnlyCalls = 0;

  // 带同期已完整：不应发出本期回退请求。
  const fullyComplete = createBranchSnapshot(28, false);
  const untouched = await resolveCurrentPeriodFallback(fullyComplete, async () => {
    currentOnlyCalls += 1;
    return currentComplete;
  });
  assert.equal(untouched, fullyComplete);
  assert.equal(currentOnlyCalls, 0, "带同期已完整时不得追加本期回退请求");

  // 还在轮询中（未耗尽）也不回退，保持原有追数语义。
  const stillPolling = createBranchSnapshot(5, true);
  assert.equal(await resolveCurrentPeriodFallback(stillPolling, async () => currentComplete), stillPolling);

  // 同期补算拖住整体：本期完整即先展示本期，每行标记同期未知。
  const fallback = await resolveCurrentPeriodFallback(exhausted, async () => {
    currentOnlyCalls += 1;
    return currentComplete;
  });
  assert.equal(currentOnlyCalls, 1);
  assert.equal(fallback.isComplete, true, "本期可证明完整时应先展示本期排行");
  assert.equal(fallback.pollingExhausted, false);
  assert.equal(fallback.pollingAttemptCount, 7);
  assert.equal(fallback.rows.length, 26);
  assert.ok(fallback.rows.every((row) => row.compareUnavailable === true), "回退结果每行都必须标记同期未知");
  assert.equal(currentComplete.rows[0]?.compareUnavailable, undefined, "不得改写本期请求的原始快照");

  // 本期同样未完成：仍返回原 Pending 结果，保持 fail-closed。
  const currentPending = createBranchSnapshot(10, true, 26);
  assert.equal(await resolveCurrentPeriodFallback(exhausted, async () => currentPending), exhausted);

  // 本期请求失败：不覆盖原状态。
  assert.equal(
    await resolveCurrentPeriodFallback(exhausted, async () => {
      throw new Error("network");
    }),
    exhausted,
  );

  // 已取消：必须继续抛出，让页面丢弃过期请求。
  const abortController = new AbortController();
  abortController.abort();
  await assert.rejects(
    resolveCurrentPeriodFallback(exhausted, async () => currentComplete, abortController.signal),
    (error: unknown) => error instanceof Error && error.name === "AbortError",
  );
}

void runPollingAssertions().then(runCurrentPeriodFallbackAssertions);
