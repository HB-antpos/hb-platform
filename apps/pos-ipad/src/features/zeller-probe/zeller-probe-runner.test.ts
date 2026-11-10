import assert from "node:assert/strict";
import test from "node:test";

import {
  buildZellerProbeReport,
  probeInitialise,
  probePurchase,
  probeQuery,
  probeRefund,
  probeSetup,
  type ZellerProbeOperation,
  type ZellerProbePorts,
  type ZellerProbeTerminal,
} from "./zeller-probe-runner";

const ATTEMPT = "799287a3-3dc2-4df1-bca3-d33eea19b35a";
const REFERENCE = "HBZ-799287a33dc24df1bca3d33eea19b35a";
const UUID = "65c4b343-03e6-49ef-a54d-012e3f2c222c";

function sdkError(type: string, message = ""): Error {
  return Object.assign(new Error(message), { type });
}

function sdkTransaction(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    $type: "Approved",
    type: "PURCHASE",
    amount: 100,
    status: "APPROVED",
    responseCode: "00",
    timestampLocal: "2026-11-05T11:24:01+10:00",
    transactionUuid: UUID,
    externalReference: REFERENCE,
    receiptText: "a very long receipt",
    ...over,
  };
}

/** 模拟 SDK 的调用句柄：可 await，带 cancel / onEvent，按给定顺序先推事件再给结果。 */
function operation(
  result: unknown,
  events: readonly unknown[] = [],
  hooks: { onCancel?: () => void } = {},
): ZellerProbeOperation {
  let listener: ((event: unknown) => void) | null = null;
  const promise = Promise.resolve().then(() => {
    for (const event of events) listener?.(event);
    return result;
  });
  return Object.assign(promise, {
    cancel: () => hooks.onCancel?.(),
    onEvent: (callback: (event: unknown) => void) => {
      listener = callback;
    },
  });
}

function terminalWith(over: Partial<ZellerProbeTerminal>): ZellerProbeTerminal {
  const unexpected = (): ZellerProbeOperation => {
    throw new Error("unexpected SDK call");
  };
  return {
    initialise: unexpected,
    setup: unexpected,
    purchase: unexpected,
    refund: unexpected,
    getTransactions: unexpected,
    ...over,
  };
}

function ports(over: Partial<ZellerProbePorts> = {}): ZellerProbePorts {
  let clock = 1000;
  return {
    newGuid: () => ATTEMPT,
    nowMs: () => (clock += 5),
    nowIso: () => "2026-11-10T02:00:00.000Z",
    ...over,
  };
}

test("扣款批准：reference 由本次尝试派生，日志带出订单引用，且不含小票原文", async () => {
  const calls: unknown[] = [];
  const terminal = terminalWith({
    purchase: (options) => {
      calls.push(options);
      return operation(sdkTransaction(), [
        { event: "AWAITING_PAYMENT_METHOD" },
        { event: "PROCESSING" },
      ]);
    },
  });

  const { entry, purchase } = await probePurchase(terminal, 100, ports());

  assert.deepEqual(calls, [{ amount: 100, reference: REFERENCE }]);
  assert.equal(entry.headline, "purchase → Approved");
  assert.equal(entry.tone, "good");
  assert.deepEqual(purchase, {
    expectation: { type: "Purchase", amountCents: 100, reference: REFERENCE },
    decision: "Approved",
    transactionUuid: UUID,
  });
  const text = entry.lines.join("\n");
  assert.match(text, /event \+\d+ms \{"event":"AWAITING_PAYMENT_METHOD"\}/u);
  assert.match(text, new RegExp(`order reference: ZELLER:${REFERENCE}:${UUID}`, "u"));
  assert.doesNotMatch(text, /a very long receipt/u);
});

test("请求到过刷卡机后连接断开：结论是结果未知，不是未付款", async () => {
  const terminal = terminalWith({
    purchase: () =>
      operation(sdkError("Connection Failure", "lost"), [{ event: "AWAITING_PAYMENT_METHOD" }]),
  });

  const { entry, purchase } = await probePurchase(terminal, 100, ports());

  assert.equal(purchase.decision, "Unknown");
  assert.equal(purchase.transactionUuid, null);
  assert.equal(entry.tone, "warn");
  const text = entry.lines.join("\n");
  assert.match(text, /sdk raw: Error\{type="Connection Failure", message="lost"\}/u);
  assert.match(text, /reachedTerminal=true/u);
  assert.match(text, /unknownReason=TerminalConnectionLost/u);
});

test("SDK 抛异常或返回读不懂的东西：都按结果未知记录，流程不中断", async () => {
  const thrown = await probePurchase(
    terminalWith({
      purchase: () => {
        throw new Error("native module missing");
      },
    }),
    100,
    ports(),
  );
  assert.equal(thrown.purchase.decision, "Unknown");
  assert.match(thrown.entry.lines.join("\n"), /sdk threw: native module missing/u);

  const garbage = await probePurchase(
    terminalWith({ purchase: () => operation({ hello: "world" }) }),
    100,
    ports(),
  );
  assert.equal(garbage.purchase.decision, "Unknown");
  assert.match(garbage.entry.lines.join("\n"), /mapped outcome=Faulted/u);
});

test("进行中的调用会交给界面用于取消，结束后清空", async () => {
  const seen: (ZellerProbeOperation | null)[] = [];
  let cancelled = 0;
  const terminal = terminalWith({
    purchase: () => operation(sdkError("Cancelled"), [], { onCancel: () => (cancelled += 1) }),
  });

  const { purchase } = await probePurchase(
    terminal,
    100,
    ports({
      onOperation: (current) => {
        seen.push(current);
        current?.cancel?.();
      },
    }),
  );

  assert.equal(cancelled, 1);
  assert.equal(seen.length, 2);
  assert.equal(seen[1], null);
  // 取消要先按 reference 查一次，查到 0 笔才算未付款。
  assert.equal(purchase.decision, "VerifyThenNotPaid");
});

test("查询：取消后的扣款查到 0 笔才判未付款；查询失败不下结论", async () => {
  const cancelled = {
    expectation: { type: "Purchase", amountCents: 100, reference: REFERENCE },
    decision: "VerifyThenNotPaid",
    transactionUuid: null,
  } as const;
  const queried: unknown[] = [];

  const empty = await probeQuery(
    terminalWith({
      getTransactions: (options) => {
        queried.push(options);
        return operation([]);
      },
    }),
    cancelled,
    ports(),
  );
  assert.deepEqual(queried, [{ reference: REFERENCE }]);
  assert.equal(empty.headline, "query → 0 transaction(s) → NotPaid");

  const failed = await probeQuery(
    terminalWith({ getTransactions: () => operation(sdkError("Network Failure")) }),
    cancelled,
    ports(),
  );
  assert.equal(failed.headline, "query → failed (NetworkFailure)");
  assert.equal(failed.tone, "bad");
  assert.match(failed.lines.join("\n"), /decision=KeepUnknown unknownReason=VerificationFailed/u);

  // 取消后其实已经扣了款：查到批准，结论要翻成批准。
  const late = await probeQuery(
    terminalWith({ getTransactions: () => operation([sdkTransaction()]) }),
    cancelled,
    ports(),
  );
  assert.equal(late.headline, "query → 1 transaction(s) → Approved");
  assert.equal(late.tone, "good");
});

test("查询已有定论的扣款：只展示查到的交易，不重新分类", async () => {
  const entry = await probeQuery(
    terminalWith({ getTransactions: () => operation([sdkTransaction()]) }),
    {
      expectation: { type: "Purchase", amountCents: 100, reference: REFERENCE },
      decision: "Approved",
      transactionUuid: UUID,
    },
    ports(),
  );
  assert.equal(entry.headline, "query → 1 transaction(s)");
  assert.match(entry.lines.join("\n"), /verification skipped: purchase already Approved/u);
  assert.match(entry.lines.join("\n"), /mapped\[0\] status=Approved type=Purchase amountCents=100/u);
});

test("退款：用原交易的 transactionUuid 指向原交易，reference 是新的 HBZR-", async () => {
  const calls: unknown[] = [];
  const refundReference = "HBZR-799287a33dc24df1bca3d33eea19b35a";
  const entry = await probeRefund(
    terminalWith({
      refund: (options) => {
        calls.push(options);
        return operation(
          sdkTransaction({ type: "REFUND", externalReference: refundReference }),
        );
      },
    }),
    { transactionUuid: UUID, amountCents: 100 },
    ports(),
  );
  assert.deepEqual(calls, [{ amount: 100, purchase: UUID, reference: refundReference }]);
  assert.equal(entry.headline, "refund → Approved");
});

test("配对：只有 SDK 返回 true 才算已配对", async () => {
  const paired = await probeInitialise(
    terminalWith({ initialise: () => operation(true) }),
    ports(),
  );
  assert.equal(paired.headline, "initialise → paired");
  assert.equal(paired.tone, "good");

  const required = await probeInitialise(
    terminalWith({ initialise: () => operation(sdkError("Setup Required")) }),
    ports(),
  );
  assert.equal(required.headline, "initialise → not paired (SetupRequired)");

  const cancelled = await probeSetup(
    terminalWith({ setup: () => operation(sdkError("Cancelled")) }),
    ports(),
  );
  assert.equal(cancelled.headline, "setup → not paired (Cancelled)");
  assert.equal(cancelled.tone, "bad");
});

test("日志报告：环境信息在前，每一步带序号、时间和耗时", async () => {
  const entry = await probeInitialise(
    terminalWith({ initialise: () => operation(true) }),
    ports(),
  );
  const report = buildZellerProbeReport(["Zeller terminal check", "app: 0.2.0"], [entry]);
  assert.deepEqual(report.split("\n").slice(0, 4), [
    "Zeller terminal check",
    "app: 0.2.0",
    "",
    "#1 2026-11-10T02:00:00.000Z initialise → paired (5 ms)",
  ]);
  assert.match(report, /\n {2}sdk raw: true/u);
});
