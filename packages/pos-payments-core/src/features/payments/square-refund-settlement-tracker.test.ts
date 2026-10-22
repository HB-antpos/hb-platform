import assert from "node:assert/strict";
import test from "node:test";

import {
  SquareRefundSettlementTracker,
  squareRefundSettlementObservation,
  type SquareRefundSettlementCandidate,
  type SquareRefundSettlementTrackerObservation,
  type SquareRefundSettlementTrackerStore,
  type SquareRefundStatusPort,
} from "./square-refund-settlement-tracker";

const scope = { storeCode: "S1", deviceCode: "D1" } as const;

class FakeStore implements SquareRefundSettlementTrackerStore {
  public readonly observations: { attemptId: string; observation: SquareRefundSettlementTrackerObservation }[] = [];
  public listCalls = 0;

  public constructor(
    private readonly candidates: readonly SquareRefundSettlementCandidate[],
    private readonly refundIds: Readonly<Record<string, string | null | Error>>,
  ) {}

  public async listSettling(): Promise<readonly SquareRefundSettlementCandidate[]> {
    this.listCalls += 1;
    return this.candidates;
  }

  public async readRefundId(attemptId: string): Promise<string | null> {
    const value = this.refundIds[attemptId];
    if (value instanceof Error) throw value;
    return value ?? null;
  }

  public async recordObservation(attemptId: string, observation: SquareRefundSettlementTrackerObservation): Promise<boolean> {
    this.observations.push({ attemptId, observation });
    return true;
  }
}

class FakeSquare implements SquareRefundStatusPort {
  public readonly calls: { environment: string; refundId: string }[] = [];

  public constructor(private readonly statuses: Readonly<Record<string, string | Error>>) {}

  public async getRefundStatus(environment: string, refundId: string): Promise<string> {
    this.calls.push({ environment, refundId });
    const status = this.statuses[refundId];
    if (status instanceof Error) throw status;
    return status ?? "";
  }
}

test("Square 退款状态映射：只有 COMPLETED 算到账，REJECTED/FAILED/CANCELED 需主管处理，PENDING 继续等", () => {
  assert.deepEqual(squareRefundSettlementObservation("COMPLETED"), { kind: "settled", squareStatus: "COMPLETED" });
  for (const status of ["REJECTED", "FAILED", "CANCELED"]) {
    assert.deepEqual(squareRefundSettlementObservation(status), { kind: "rejected", squareStatus: status });
  }
  assert.deepEqual(squareRefundSettlementObservation("PENDING"), { kind: "still-settling", squareStatus: "PENDING" });
  assert.deepEqual(squareRefundSettlementObservation("WEIRD"), { kind: "check-failed", errorCode: "SQUARE_REFUND_WEIRD" });
  assert.deepEqual(squareRefundSettlementObservation(""), { kind: "check-failed", errorCode: "SQUARE_REFUND_STATUS_MISSING" });
});

test("逐条只读查询同一笔退款并按冻结环境记录结论；单条失败不影响其他记录", async () => {
  const store = new FakeStore(
    [
      { attemptId: "a-completed", providerEnvironment: "Production" },
      { attemptId: "a-rejected", providerEnvironment: "Production" },
      { attemptId: "a-pending", providerEnvironment: "Sandbox" },
      { attemptId: "a-http-error", providerEnvironment: "Production" },
      { attemptId: "a-no-refund-id", providerEnvironment: "Production" },
      { attemptId: "a-no-environment", providerEnvironment: null },
      { attemptId: "a-evidence-broken", providerEnvironment: "Production" },
    ],
    {
      "a-completed": "refund-completed",
      "a-rejected": "refund-rejected",
      "a-pending": "refund-pending",
      "a-http-error": "refund-http-error",
      "a-no-refund-id": null,
      "a-no-environment": "refund-x",
      "a-evidence-broken": new Error("decrypt failed"),
    },
  );
  const square = new FakeSquare({
    "refund-completed": "completed",
    "refund-rejected": "REJECTED",
    "refund-pending": "PENDING",
    "refund-http-error": Object.assign(new Error("bad gateway"), { code: "SQUARE_HTTP_502" }),
  });

  const result = await new SquareRefundSettlementTracker(store, square).checkSettling(scope);

  assert.deepEqual(result, { checked: 7, settled: 1, rejected: 1, stillSettling: 1, errors: 4 });
  assert.deepEqual(
    Object.fromEntries(store.observations.map((item) => [item.attemptId, item.observation])),
    {
      "a-completed": { kind: "settled", squareStatus: "COMPLETED" },
      "a-rejected": { kind: "rejected", squareStatus: "REJECTED" },
      "a-pending": { kind: "still-settling", squareStatus: "PENDING" },
      "a-http-error": { kind: "check-failed", errorCode: "SQUARE_HTTP_502" },
      "a-no-refund-id": { kind: "check-failed", errorCode: "SQUARE_REFUND_ID_MISSING" },
      "a-no-environment": { kind: "check-failed", errorCode: "SQUARE_REFUND_ENVIRONMENT_REQUIRED" },
      "a-evidence-broken": { kind: "check-failed", errorCode: "SQUARE_REFUND_EVIDENCE_UNREADABLE" },
    },
  );
  // 只查询能取得退款号与环境的记录，且使用 attempt 冻结的环境。
  assert.deepEqual(square.calls, [
    { environment: "Production", refundId: "refund-completed" },
    { environment: "Production", refundId: "refund-rejected" },
    { environment: "Sandbox", refundId: "refund-pending" },
    { environment: "Production", refundId: "refund-http-error" },
  ]);
});

test("后台与手动同时触发时复用同一次检查，不并发查询同一记录", async () => {
  let release: (() => void) | null = null;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const store = new FakeStore([{ attemptId: "a-1", providerEnvironment: "Production" }], { "a-1": "refund-1" });
  const square: SquareRefundStatusPort = {
    async getRefundStatus() {
      await gate;
      return "PENDING";
    },
  };
  const tracker = new SquareRefundSettlementTracker(store, square);

  const first = tracker.checkSettling(scope);
  const second = tracker.checkSettling(scope);
  release!();
  const [a, b] = await Promise.all([first, second]);

  assert.equal(a, b);
  assert.equal(store.listCalls, 1);
  // 上一次结束后可以再次检查。
  await tracker.checkSettling(scope);
  assert.equal(store.listCalls, 2);
});

test("没有结算中的退款时不发任何查询", async () => {
  const store = new FakeStore([], {});
  const square = new FakeSquare({});
  const result = await new SquareRefundSettlementTracker(store, square).checkSettling(scope);
  assert.deepEqual(result, { checked: 0, settled: 0, rejected: 0, stillSettling: 0, errors: 0 });
  assert.equal(square.calls.length, 0);
});
