import assert from "node:assert/strict";
import test from "node:test";

import type { PaymentAttempt } from "@hb/pos-domain/core/contracts/payment";
import { normalizeCardSyncEvidence } from "@hb/pos-domain/core/contracts/payment";
import { formatZellerPayment } from "@hb/pos-payments-core/features/payments/zeller/zeller-payment-reference";

import { ZELLER_NOT_PAID_SETTLE_MS, ZellerPaymentAdapter } from "./zeller-payment-adapter";
import { ZellerTerminalRegistry } from "./zeller-terminal-registry";

import type {
  ZellerProbeOperation,
  ZellerProbeTerminal,
} from "@/features/zeller-probe/zeller-probe-runner";

const ATTEMPT_ID = "799287a3-3dc2-4df1-bca3-d33eea19b35a";
const REFERENCE = "HBZ-799287a33dc24df1bca3d33eea19b35a";
const UUID = "65c4b343-03e6-49ef-a54d-012e3f2c222c";
const REFUND_REFERENCE = "HBZR-799287a33dc24df1bca3d33eea19b35a";
const REFUND_UUID = "0f8fad5b-d9cb-469f-a165-70867728950e";

function attempt(over: Partial<PaymentAttempt> = {}): PaymentAttempt {
  return {
    attemptId: ATTEMPT_ID,
    idempotencyKey: "idempotency-1",
    orderGuid: "order-1",
    provider: "zeller",
    operation: "purchase",
    amount: { currency: "AUD", cents: 1_250 },
    state: "Submitted",
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: null,
      txnRef: REFERENCE,
      rfn: null,
      voucherReservationToken: null,
    },
    createdAtIso: "2026-11-10T00:00:00.000Z",
    updatedAtIso: "2026-11-10T00:00:01.000Z",
    lastErrorCode: null,
    ...over,
  };
}

function sdkError(type: string): Error {
  return Object.assign(new Error(""), { type });
}

function sdkTransaction(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    type: "PURCHASE",
    amount: 1_250,
    status: "APPROVED",
    responseCode: "00",
    responseText: "APPROVED",
    transactionUuid: UUID,
    externalReference: REFERENCE,
    approvalCode: "456321",
    panMasked: "1234",
    scheme: "VISA",
    bin: "411111",
    caid: "CAID-1",
    catid: "CATID-1",
    receiptText: "RECEIPT",
    timestampLocal: "2026-11-05T11:24:01+10:00",
    ...over,
  };
}

/** 一笔原路退款 attempt：金额为负，paymentId 是原交易号（保险库给的种子），txnRef 是本次退款的 reference。 */
function refundAttempt(over: Partial<PaymentAttempt> = {}): PaymentAttempt {
  return attempt({
    operation: "refund",
    amount: { currency: "AUD", cents: -500 },
    references: { ...attempt().references, paymentId: UUID, txnRef: REFUND_REFERENCE },
    ...over,
  });
}

function sdkRefund(over: Record<string, unknown> = {}): Record<string, unknown> {
  return sdkTransaction({
    type: "REFUND",
    amount: 500,
    transactionUuid: REFUND_UUID,
    externalReference: REFUND_REFERENCE,
    ...over,
  });
}

/** 假的 SDK 调用句柄：由测试决定何时结束、推什么事件，并记录取消次数。 */
function deferredOperation() {
  let resolve: (value: unknown) => void = () => undefined;
  let reject: (error: unknown) => void = () => undefined;
  let listener: ((event: unknown) => void) | null = null;
  const promise = new Promise<unknown>((done, fail) => {
    resolve = done;
    reject = fail;
  });
  const state = { cancels: 0 };
  const operation: ZellerProbeOperation = Object.assign(promise, {
    cancel: () => {
      state.cancels += 1;
    },
    onEvent: (callback: (event: unknown) => void) => {
      listener = callback;
    },
  });
  return {
    operation,
    state,
    emit: (event: string) => listener?.({ event }),
    resolve: async (value: unknown) => {
      resolve(value);
      await flush();
    },
    reject: async (error: unknown) => {
      reject(error);
      await flush();
    },
  };
}

async function flush(): Promise<void> {
  await new Promise((done) => setImmediate(done));
}

function harness() {
  const purchases: { options: unknown; call: ReturnType<typeof deferredOperation> }[] = [];
  const refunds: { options: unknown; call: ReturnType<typeof deferredOperation> }[] = [];
  const queries: unknown[] = [];
  let queryResult: () => unknown = () => [];
  let pairingResult: () => unknown = () => true;
  let pairingChecks = 0;
  const unexpected = (): ZellerProbeOperation => {
    throw new Error("unexpected SDK call");
  };
  const terminal: ZellerProbeTerminal = {
    initialise: () => {
      pairingChecks += 1;
      return Promise.resolve(pairingResult()) as ZellerProbeOperation;
    },
    setup: unexpected,
    refund: (options) => {
      const call = deferredOperation();
      refunds.push({ options, call });
      return call.operation;
    },
    purchase: (options) => {
      const call = deferredOperation();
      purchases.push({ options, call });
      return call.operation;
    },
    getTransactions: (options) => {
      queries.push(options);
      const value = queryResult();
      return Object.assign(
        value instanceof Promise ? value : Promise.resolve(value),
        {},
      ) as ZellerProbeOperation;
    },
  };
  const registry = new ZellerTerminalRegistry();
  const unregister = registry.register(terminal);
  let now = 1_000_000;
  const adapter = new ZellerPaymentAdapter({ terminals: registry, nowMs: () => now });
  return {
    adapter,
    registry,
    purchases,
    refunds,
    queries,
    unregister,
    advance: (ms: number) => {
      now += ms;
    },
    setQuery: (next: () => unknown) => {
      queryResult = next;
    },
    setPairing: (next: () => unknown) => {
      pairingResult = next;
    },
    pairingChecks: () => pairingChecks,
  };
}

test("扣款：交给 SDK 后立刻返回等待中，不等顾客刷完卡；批准后带出引用和卡证据", async () => {
  const h = harness();

  const submitted = await h.adapter.submit(attempt());
  assert.equal(submitted.state, "Pending");
  assert.deepEqual(h.purchases.map((purchase) => purchase.options), [
    { amount: 1_250, reference: REFERENCE },
  ]);
  // 还在刷卡：recover 只报告等待中，不查询、更不重新发起。
  assert.equal((await h.adapter.recover(attempt({ state: "Pending" }))).state, "Pending");
  assert.equal(h.purchases.length, 1);
  assert.equal(h.queries.length, 0);

  await h.purchases[0]!.call.resolve(sdkTransaction());
  const approved = await h.adapter.recover(attempt({ state: "Pending" }));

  assert.equal(approved.state, "Approved");
  assert.equal(approved.references.txnRef, REFERENCE);
  assert.equal(approved.references.paymentId, UUID);
  assert.equal(approved.receiptText, "RECEIPT");
  // 订单同步用这两项拼出与 WPF 相同的引用。
  assert.equal(
    formatZellerPayment(approved.references.txnRef!, approved.references.paymentId!),
    `ZELLER:${REFERENCE}:${UUID}`,
  );
  // 卡证据必须能通过共享契约的校验，字段对照与 WPF 一致。
  assert.deepEqual(normalizeCardSyncEvidence(approved.protectedSyncEvidence), {
    version: 1,
    provider: "zeller",
    operation: "purchase",
    processor: "Zeller",
    txnRef: UUID,
    authCode: "456321",
    cardType: "VISA",
    cardBin: 411111,
    maskedCardNumber: "1234",
    merchantId: "CAID-1",
    responseCode: "00",
    responseText: "APPROVED",
    stan: null,
    bankDateTimeIso: "2026-11-05T01:24:01.000Z",
    amountCents: 1_250,
    refundReference: null,
  });
  assert.equal(h.queries.length, 0);
});

test("同一笔 attempt 再次 submit 绝不发第二次扣款", async () => {
  const h = harness();
  await h.adapter.submit(attempt());
  const again = await h.adapter.submit(attempt());
  assert.equal(again.state, "Pending");
  assert.equal(h.purchases.length, 1);
});

test("请求没有发出的情况都按未付款收尾，且不调用 SDK", async () => {
  const cases: readonly (readonly [string, Partial<PaymentAttempt>])[] = [
    ["没有落库的 reference", { references: { ...attempt().references, txnRef: null } }],
    ["reference 不是本 attempt 派生的", { references: { ...attempt().references, txnRef: "HBZ-00000000000000000000000000000001" } }],
    ["attemptId 不是 GUID", { attemptId: "attempt-1" }],
    ["金额不是正数", { amount: { currency: "AUD", cents: 0 } }],
    ["是退款", { operation: "refund", amount: { currency: "AUD", cents: -500 } }],
  ];
  for (const [name, over] of cases) {
    const h = harness();
    const result = await h.adapter.submit(attempt(over));
    assert.equal(result.state, "Declined", name);
    assert.equal(result.responseCode, "ZELLER_NOT_SUBMITTED_INVALID_ATTEMPT", name);
    assert.equal(h.purchases.length, 0, name);
  }

  const offline = harness();
  offline.unregister();
  const noTerminal = await offline.adapter.submit(attempt());
  assert.equal(noTerminal.state, "Declined");
  assert.equal(noTerminal.responseCode, "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE");
});

test("同一时间只有一个 SDK 调用：另一笔扣款进行中时，新的扣款不发出", async () => {
  const h = harness();
  await h.adapter.submit(attempt());
  const otherId = "3b241101-e2bb-4255-8caf-4136c566a962";
  const other = attempt({
    attemptId: otherId,
    orderGuid: "order-2",
    references: { ...attempt().references, txnRef: "HBZ-3b241101e2bb42558caf4136c566a962" },
  });

  const blocked = await h.adapter.submit(other);
  assert.equal(blocked.state, "Declined");
  assert.equal(blocked.responseCode, "ZELLER_NOT_SUBMITTED_TERMINAL_BUSY");
  assert.equal(h.purchases.length, 1);

  // 前一笔结束后才可以发下一笔。
  await h.purchases[0]!.call.resolve(sdkTransaction());
  assert.equal((await h.adapter.submit(other)).state, "Pending");
  assert.equal(h.purchases.length, 2);
});

test("被拒的交易以交易形式返回：按拒付收尾，不查询", async () => {
  const h = harness();
  await h.adapter.submit(attempt());
  await h.purchases[0]!.call.resolve(sdkTransaction({ status: "DECLINED", responseCode: "51" }));

  const declined = await h.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(declined.state, "Declined");
  assert.equal(declined.responseCode, "51");
  assert.equal(declined.protectedSyncEvidence, undefined);
  assert.equal(h.queries.length, 0);
});

test("取消：只是请求取消，结果仍以 SDK 返回为准；没读到卡时查到 0 笔即收尾为已取消", async () => {
  const h = harness();
  await h.adapter.submit(attempt());

  const requested = await h.adapter.cancel(attempt({ state: "Pending" }));
  assert.equal(requested.state, "Pending");
  assert.equal(requested.responseCode, "ZELLER_CANCEL_REQUESTED");
  assert.equal(h.purchases[0]!.call.state.cancels, 1);

  await h.purchases[0]!.call.resolve(sdkError("Cancelled"));
  const cancelled = await h.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(cancelled.state, "Cancelled");
  assert.equal(cancelled.responseCode, "ZELLER_NOT_PAID_CANCELLED");
  // 取消不能只信 SDK：必须按 reference 查过一次。
  assert.deepEqual(h.queries, [{ reference: REFERENCE }]);
});

test("取消后其实已经扣了款：按 reference 查到批准，结论是批准", async () => {
  const h = harness();
  h.setQuery(() => [sdkTransaction({ receiptText: undefined, approvalCode: undefined })]);
  await h.adapter.submit(attempt());
  await h.purchases[0]!.call.resolve(sdkError("Cancelled"));

  const approved = await h.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(approved.state, "Approved");
  assert.equal(approved.references.paymentId, UUID);
  assert.equal(approved.receiptText, null);
});

test("刷卡机已读到卡又被取消：60 秒内查到 0 笔也不收尾，之后再查到 0 笔才算已取消", async () => {
  const h = harness();
  await h.adapter.submit(attempt());
  h.purchases[0]!.call.emit("AWAITING_PAYMENT_METHOD");
  h.purchases[0]!.call.emit("PROCESSING");
  await h.purchases[0]!.call.resolve(sdkError("Cancelled"));

  const early = await h.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(early.state, "Pending");
  assert.equal(early.responseCode, "ZELLER_VERIFYING_NOT_PAID");

  h.advance(ZELLER_NOT_PAID_SETTLE_MS - 1);
  assert.equal((await h.adapter.recover(attempt({ state: "Pending" }))).state, "Pending");

  // 等待期间钱到账了：结论翻成批准。
  h.setQuery(() => [sdkTransaction()]);
  assert.equal((await h.adapter.recover(attempt({ state: "Pending" }))).state, "Approved");

  // 另一笔同样的情形，一直查不到：满 60 秒后才收尾。
  const second = harness();
  await second.adapter.submit(attempt());
  second.purchases[0]!.call.emit("PROCESSING");
  await second.purchases[0]!.call.resolve(sdkError("Inactivity Timeout"));
  second.advance(ZELLER_NOT_PAID_SETTLE_MS);
  const settled = await second.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(settled.state, "Cancelled");
  assert.equal(settled.responseCode, "ZELLER_NOT_PAID_TIMEOUT");
});

test("断网（Network Failure）永远不当成未付款：查到 0 笔仍是结果未知，查到批准才算批准", async () => {
  const h = harness();
  await h.adapter.submit(attempt());
  await h.purchases[0]!.call.resolve(sdkError("Network Failure"));

  const unknown = await h.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(unknown.state, "Unknown");
  assert.equal(unknown.responseCode, "ZELLER_UNKNOWN_NOT_FOUND");

  // 查询本身失败：同样不下结论。
  h.setQuery(() => sdkError("Network Failure"));
  const failed = await h.adapter.recover(attempt({ state: "Unknown" }));
  assert.equal(failed.state, "Unknown");
  assert.equal(failed.responseCode, "ZELLER_UNKNOWN_VERIFICATION_FAILED");

  h.setQuery(() => [sdkTransaction()]);
  assert.equal((await h.adapter.recover(attempt({ state: "Unknown" }))).state, "Approved");
  // 整个过程只发起过一次扣款。
  assert.equal(h.purchases.length, 1);
});

test("SDK 抛异常或返回读不懂的东西：结果未知，按 reference 核实", async () => {
  const thrown = harness();
  await thrown.adapter.submit(attempt());
  await thrown.purchases[0]!.call.reject(new Error("boom"));
  assert.equal((await thrown.adapter.recover(attempt({ state: "Pending" }))).state, "Unknown");
  assert.equal(thrown.queries.length, 1);

  const garbage = harness();
  await garbage.adapter.submit(attempt());
  await garbage.purchases[0]!.call.resolve({ hello: "world" });
  assert.equal((await garbage.adapter.recover(attempt({ state: "Pending" }))).state, "Unknown");
});

test("批准但金额对不上、或同一 reference 下有两笔批准：结果未知，交给主管", async () => {
  const mismatch = harness();
  mismatch.setQuery(() => [sdkTransaction({ amount: 9_999 })]);
  await mismatch.adapter.submit(attempt());
  await mismatch.purchases[0]!.call.resolve(sdkTransaction({ amount: 9_999 }));
  const wrongAmount = await mismatch.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(wrongAmount.state, "Unknown");
  assert.equal(wrongAmount.responseCode, "ZELLER_UNKNOWN_AMOUNT_MISMATCH");

  const doubled = harness();
  doubled.setQuery(() => [
    sdkTransaction(),
    sdkTransaction({ transactionUuid: "98b690b7-5eaa-41ec-8ff8-168c60364aac" }),
  ]);
  await doubled.adapter.submit(attempt());
  await doubled.purchases[0]!.call.resolve(sdkError("Network Failure"));
  const twoApprovals = await doubled.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(twoApprovals.state, "Unknown");
  assert.equal(twoApprovals.responseCode, "ZELLER_UNKNOWN_MULTIPLE_APPROVED");
});

test("进程重启后（内存里没有这笔调用）：只按 reference 查询，绝不重新扣款", async () => {
  const h = harness();

  // 查到 0 笔：不能证明没付，保持结果未知。
  const notFound = await h.adapter.recover(attempt({ state: "Submitted" }));
  assert.equal(notFound.state, "Unknown");
  assert.equal(notFound.responseCode, "ZELLER_UNKNOWN_NOT_FOUND");

  h.setQuery(() => [sdkTransaction({ status: "DECLINED", responseCode: "05" })]);
  assert.equal((await h.adapter.recover(attempt({ state: "Unknown" }))).state, "Declined");

  h.setQuery(() => [sdkTransaction()]);
  const approved = await h.adapter.recover(attempt({ state: "Unknown" }));
  assert.equal(approved.state, "Approved");
  assert.equal(h.purchases.length, 0);

  // 重启后没有可取消的调用：取消不能当成已取消。
  const cancel = await h.adapter.cancel(attempt({ state: "Pending" }));
  assert.equal(cancel.state, "Unknown");
  assert.equal(cancel.responseCode, "ZELLER_CANCEL_NOT_POSSIBLE");
});

test("终端不在或 SDK 正忙时不下结论，等下次再查", async () => {
  const h = harness();
  h.unregister();
  const noTerminal = await h.adapter.recover(attempt({ state: "Unknown" }));
  assert.equal(noTerminal.state, "Unknown");
  assert.equal(noTerminal.responseCode, "ZELLER_TERMINAL_UNAVAILABLE");

  // 另一笔扣款占着 SDK 时，这一笔不能去查询（并发会串结果）。
  const busy = harness();
  await busy.adapter.submit(attempt());
  const otherId = "3b241101-e2bb-4255-8caf-4136c566a962";
  const other = attempt({
    attemptId: otherId,
    state: "Unknown",
    references: { ...attempt().references, txnRef: "HBZ-3b241101e2bb42558caf4136c566a962" },
  });
  const held = await busy.adapter.recover(other);
  assert.equal(held.state, "Unknown");
  assert.equal(held.responseCode, "ZELLER_TERMINAL_BUSY");
  assert.equal(busy.queries.length, 0);
});

test("卡证据里的附加字段过长或带控制字符时截断或留空，不让一笔已扣款的交易建不了单", async () => {
  const h = harness();
  await h.adapter.submit(attempt());
  await h.purchases[0]!.call.resolve(
    sdkTransaction({
      responseText: `line1\nline2 ${"x".repeat(400)}`,
      scheme: "S".repeat(80),
      bin: "not-a-bin",
      panMasked: "4111111111111234",
      timestampLocal: "2026-11-05T01:24:01",
    }),
  );
  const approved = await h.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(approved.state, "Approved");
  const evidence = normalizeCardSyncEvidence(approved.protectedSyncEvidence);
  assert.equal(evidence.responseText?.length, 256);
  assert.equal(evidence.cardType?.length, 32);
  assert.equal(evidence.cardBin, null);
  // 没有掩码的长卡号不进证据。
  assert.equal(evidence.maskedCardNumber, null);
  // 没带时区的刷卡机时间按 UTC。
  assert.equal(evidence.bankDateTimeIso, "2026-11-05T01:24:01.000Z");
});

test("原路退款：按原交易号退回原卡，等刷卡机给出结果再返回；引用不变，卡证据记退款自己的交易号", async () => {
  const h = harness();

  let settled = false;
  const refunding = h.adapter.refund(refundAttempt()).then((result) => {
    settled = true;
    return result;
  });
  await flush();
  // 发给 Zeller 的是正的退款额、原交易号和本次退款的 reference。
  assert.deepEqual(h.refunds.map((refund) => refund.options), [
    { amount: 500, purchase: UUID, reference: REFUND_REFERENCE },
  ]);
  // 刷卡机还没给结果：不返回（退货流程不轮询等待中的退款）。
  assert.equal(settled, false);

  await h.refunds[0]!.call.resolve(sdkRefund());
  const approved = await refunding;

  assert.equal(approved.state, "Approved");
  // paymentId 仍是原交易号：改成退款的交易号会被上游判成引用冲突，钱退了单子却卡在结果未知。
  assert.deepEqual(approved.references, refundAttempt().references);
  assert.deepEqual(normalizeCardSyncEvidence(approved.protectedSyncEvidence), {
    version: 1,
    provider: "zeller",
    operation: "refund",
    processor: "Zeller",
    txnRef: REFUND_UUID,
    authCode: "456321",
    cardType: "VISA",
    cardBin: 411111,
    maskedCardNumber: "1234",
    merchantId: "CAID-1",
    responseCode: "00",
    responseText: "APPROVED",
    stan: null,
    bankDateTimeIso: "2026-11-05T01:24:01.000Z",
    amountCents: 500,
    refundReference: null,
  });
  assert.equal(h.purchases.length, 0);
  assert.equal(h.queries.length, 0);
});

test("原路退款：请求没有发出的情况都按未退款收尾，且不调用 SDK", async () => {
  const cases: readonly (readonly [string, Partial<PaymentAttempt>])[] = [
    ["没有原交易号", { references: { ...refundAttempt().references, paymentId: null } }],
    ["原交易号不是 GUID", { references: { ...refundAttempt().references, paymentId: "payment-1" } }],
    ["reference 是扣款用的", { references: { ...refundAttempt().references, txnRef: REFERENCE } }],
    ["没有落库的 reference", { references: { ...refundAttempt().references, txnRef: null } }],
    ["金额不是负数", { amount: { currency: "AUD", cents: 500 } }],
    ["是扣款", { operation: "purchase", references: { ...attempt().references, paymentId: UUID } }],
  ];
  for (const [name, over] of cases) {
    const h = harness();
    const result = await h.adapter.refund(refundAttempt(over));
    assert.equal(result.state, "Declined", name);
    assert.equal(result.responseCode, "ZELLER_NOT_SUBMITTED_INVALID_ATTEMPT", name);
    assert.equal(h.refunds.length, 0, name);
  }

  const unpaired = harness();
  unpaired.setPairing(() => sdkError("Setup Required"));
  const blocked = await unpaired.adapter.refund(refundAttempt());
  assert.equal(blocked.state, "Declined");
  assert.equal(blocked.responseCode, "ZELLER_NOT_SUBMITTED_SETUP_REQUIRED");
  assert.equal(unpaired.refunds.length, 0);

  const offline = harness();
  offline.unregister();
  const noTerminal = await offline.adapter.refund(refundAttempt());
  assert.equal(noTerminal.state, "Declined");
  assert.equal(noTerminal.responseCode, "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE");
});

test("原路退款：Zeller 找不到原交易时退款没有发生；被拒的退款按拒绝收尾", async () => {
  const missing = harness();
  const refunding = missing.adapter.refund(refundAttempt());
  await flush();
  await missing.refunds[0]!.call.resolve(sdkError("Transaction Not Found"));
  const notFound = await refunding;
  assert.equal(notFound.state, "Declined");
  assert.equal(notFound.responseCode, "ZELLER_NOT_SUBMITTED_ORIGINAL_TRANSACTION_NOT_FOUND");
  assert.equal(missing.queries.length, 0);

  const declined = harness();
  const declining = declined.adapter.refund(refundAttempt());
  await flush();
  await declined.refunds[0]!.call.resolve(sdkRefund({ status: "DECLINED", responseCode: "05" }));
  const result = await declining;
  assert.equal(result.state, "Declined");
  assert.equal(result.responseCode, "05");
});

test("原路退款：结果不明时只按本次退款的 reference 查询，绝不重发；查不到就保持结果未知", async () => {
  const h = harness();
  const refunding = h.adapter.refund(refundAttempt());
  await flush();
  await h.refunds[0]!.call.resolve(sdkError("Network Failure"));
  const unknown = await refunding;
  // 断网不代表没退：查到 0 笔也不能判成没退，交给主管核实。
  assert.equal(unknown.state, "Unknown");
  assert.deepEqual(h.queries, [{ reference: REFUND_REFERENCE }]);

  // 同一笔 attempt 再次调用 refund（重复提交）：只收结果，不发第二次退款。
  h.setQuery(() => [sdkRefund()]);
  const again = await h.adapter.refund(refundAttempt({ state: "Unknown" }));
  assert.equal(again.state, "Approved");
  assert.equal(h.refunds.length, 1);

  // 进程重启后（内存里没有这笔调用）：只查询。
  const restarted = harness();
  const notFound = await restarted.adapter.recover(refundAttempt({ state: "Unknown" }));
  assert.equal(notFound.state, "Unknown");
  assert.equal(notFound.responseCode, "ZELLER_UNKNOWN_NOT_FOUND");
  restarted.setQuery(() => [sdkRefund()]);
  const recovered = await restarted.adapter.recover(refundAttempt({ state: "Unknown" }));
  assert.equal(recovered.state, "Approved");
  assert.equal(normalizeCardSyncEvidence(recovered.protectedSyncEvidence).txnRef, REFUND_UUID);
  // 查到的是同一 reference 下的扣款而不是退款：对不上，不能当成已退。
  restarted.setQuery(() => [sdkTransaction({ externalReference: REFUND_REFERENCE, amount: 500 })]);
  assert.equal((await restarted.adapter.recover(refundAttempt({ state: "Unknown" }))).state, "Unknown");
  assert.equal(restarted.refunds.length, 0);
});

test("分期用的扣款入口：等刷卡机给出结果再返回；没发出去的照常立刻返回", async () => {
  const h = harness();
  let settled = false;
  const paying = h.adapter.submitAndSettle(attempt()).then((result) => {
    settled = true;
    return result;
  });
  await flush();
  assert.equal(h.purchases.length, 1);
  assert.equal(settled, false);
  await h.purchases[0]!.call.resolve(sdkTransaction());
  const approved = await paying;
  assert.equal(approved.state, "Approved");
  assert.equal(approved.references.paymentId, UUID);

  // 没发出去（这里是没配对）：不等待，直接按「没有发出」返回。
  const unpaired = harness();
  unpaired.setPairing(() => sdkError("Setup Required"));
  const blocked = await unpaired.adapter.submitAndSettle(attempt());
  assert.equal(blocked.state, "Declined");
  assert.equal(blocked.responseCode, "ZELLER_NOT_SUBMITTED_SETUP_REQUIRED");
});

test("低于 1.00 澳元的扣款不向刷卡机发请求", async () => {
  const h = harness();
  const result = await h.adapter.submit(attempt({ amount: { currency: "AUD", cents: 99 } }));
  assert.equal(result.state, "Declined");
  assert.equal(result.responseCode, "ZELLER_NOT_SUBMITTED_BELOW_MINIMUM");
  assert.equal(h.purchases.length, 0);
  assert.equal(h.pairingChecks(), 0);

  assert.equal((await h.adapter.submit(attempt({ amount: { currency: "AUD", cents: 100 } }))).state, "Pending");
});

test("两个适配器实例共用同一台刷卡机时，SDK 闸门是共享的：一边在扣款，另一边不发请求也不查询", async () => {
  // 销售和分期各有一套通道注册表，各建一个适配器实例，但终端来源是同一个。
  const registry = new ZellerTerminalRegistry();
  const purchases: ReturnType<typeof deferredOperation>[] = [];
  let queries = 0;
  const terminal: ZellerProbeTerminal = {
    initialise: () => Promise.resolve(true) as ZellerProbeOperation,
    setup: () => Promise.resolve(true) as ZellerProbeOperation,
    refund: () => Promise.resolve(null) as ZellerProbeOperation,
    purchase: () => {
      const call = deferredOperation();
      purchases.push(call);
      return call.operation;
    },
    getTransactions: () => {
      queries += 1;
      return Promise.resolve([]) as ZellerProbeOperation;
    },
  };
  registry.register(terminal);
  const sales = new ZellerPaymentAdapter({ terminals: registry });
  const installments = new ZellerPaymentAdapter({ terminals: registry });

  assert.equal((await sales.submit(attempt())).state, "Pending");
  const otherId = "3b241101-e2bb-4255-8caf-4136c566a962";
  const other = attempt({
    attemptId: otherId,
    references: { ...attempt().references, txnRef: "HBZ-3b241101e2bb42558caf4136c566a962" },
  });
  const blocked = await installments.submit(other);
  assert.equal(blocked.state, "Declined");
  assert.equal(blocked.responseCode, "ZELLER_NOT_SUBMITTED_TERMINAL_BUSY");
  const held = await installments.recover({ ...other, state: "Unknown" });
  assert.equal(held.responseCode, "ZELLER_TERMINAL_BUSY");
  assert.equal(purchases.length, 1);
  assert.equal(queries, 0);

  // 第一笔结束后闸门放开，另一边可以发起。
  await purchases[0]!.resolve(sdkTransaction());
  assert.equal((await installments.submit(other)).state, "Pending");
  assert.equal(purchases.length, 2);
});

test("恢复中心的只读查询：只有真的从 Zeller 拿到答复才算核实过，且不发起扣款或取消", async () => {
  const h = harness();

  // 查到 0 笔：查过了，但结论仍是结果未知。
  const notFound = await h.adapter.queryExistingPayment(attempt({ state: "Unknown" }));
  assert.equal(notFound.state, "Unknown");
  assert.equal(notFound.queryVerified, true);

  // 查询失败：没查成。
  h.setQuery(() => sdkError("Network Failure"));
  const failed = await h.adapter.queryExistingPayment(attempt({ state: "Unknown" }));
  assert.equal(failed.state, "Unknown");
  assert.equal(failed.queryVerified, false);

  // 查到批准：核实过，系统直接采用批准，不需要主管人工确认。
  h.setQuery(() => [sdkTransaction()]);
  const approved = await h.adapter.queryExistingPayment(attempt({ state: "Unknown" }));
  assert.equal(approved.state, "Approved");
  assert.equal(approved.queryVerified, true);
  assert.equal(h.purchases.length, 0);

  // 终端不在：没查成。
  const offline = harness();
  offline.unregister();
  const noTerminal = await offline.adapter.queryExistingPayment(attempt({ state: "Unknown" }));
  assert.equal(noTerminal.queryVerified, false);

  // 这笔扣款还在刷卡机上进行：不算核实过，也不会去取消它。
  const busy = harness();
  await busy.adapter.submit(attempt());
  const inFlight = await busy.adapter.queryExistingPayment(attempt({ state: "Pending" }));
  assert.equal(inFlight.state, "Pending");
  assert.equal(inFlight.queryVerified, false);
  assert.equal(busy.purchases[0]!.call.state.cancels, 0);
});

test("需要终端而 SDK 还没加载时，会通知界面层去加载；登记后下一次就能用", async () => {
  const registry = new ZellerTerminalRegistry();
  let notified = 0;
  registry.subscribe(() => {
    notified += 1;
  });
  const adapter = new ZellerPaymentAdapter({ terminals: registry });
  assert.equal(registry.isWanted(), false);

  // 重启后有一笔 Zeller 付款要核实：这一次查不了，但已经提出需要终端。
  const held = await adapter.recover(attempt({ state: "Unknown" }));
  assert.equal(held.responseCode, "ZELLER_TERMINAL_UNAVAILABLE");
  assert.equal(registry.isWanted(), true);
  assert.equal(notified, 1);
  // 重复提出不会重复通知。
  await adapter.recover(attempt({ state: "Unknown" }));
  assert.equal(notified, 1);

  // 界面层加载好并登记后，下一次轮询就能查询。
  const unregister = registry.register({
    initialise: () => Promise.resolve(true),
    setup: () => Promise.resolve(true),
    purchase: () => Promise.resolve(null),
    refund: () => Promise.resolve(null),
    getTransactions: () => Promise.resolve([sdkTransaction()]),
  });
  assert.equal((await adapter.recover(attempt({ state: "Unknown" }))).state, "Approved");

  // 只注销自己登记的那一个：旧界面卸载不能清掉新界面刚登记的终端。
  const second = registry.register({
    initialise: () => Promise.resolve(true),
    setup: () => Promise.resolve(true),
    purchase: () => Promise.resolve(null),
    refund: () => Promise.resolve(null),
    getTransactions: () => Promise.resolve([]),
  });
  unregister();
  assert.notEqual(registry.current(), null);
  second();
  assert.equal(registry.current(), null);
});

test("扣款前先确认配对：没配对或查不了配对时请求不发出，不会落成结果未知", async () => {
  const unpaired = harness();
  unpaired.setPairing(() => sdkError("Setup Required"));
  const setupRequired = await unpaired.adapter.submit(attempt());
  assert.equal(setupRequired.state, "Declined");
  assert.equal(setupRequired.responseCode, "ZELLER_NOT_SUBMITTED_SETUP_REQUIRED");
  assert.equal(unpaired.purchases.length, 0);
  assert.equal(unpaired.queries.length, 0);

  const offline = harness();
  offline.setPairing(() => sdkError("Network Failure"));
  const unavailable = await offline.adapter.submit(attempt());
  assert.equal(unavailable.state, "Declined");
  assert.equal(unavailable.responseCode, "ZELLER_NOT_SUBMITTED_TERMINAL_UNAVAILABLE");
  assert.equal(offline.purchases.length, 0);

  // 配对正常：检查一次后才发扣款；核实和重复 submit 不再检查配对。
  const paired = harness();
  assert.equal((await paired.adapter.submit(attempt())).state, "Pending");
  assert.equal(paired.pairingChecks(), 1);
  assert.equal(paired.purchases.length, 1);
  await paired.adapter.submit(attempt());
  await paired.adapter.recover(attempt({ state: "Pending" }));
  assert.equal(paired.pairingChecks(), 1);
});


test("设置里打开「收款时不弹出 Zeller 窗口」：扣款带 unattended，改动立刻对下一笔生效；默认与关闭时请求不带它", async () => {
  const h = harness();

  // 默认：请求与以前完全一致，不带 unattended。
  await h.adapter.submit(attempt());
  assert.deepEqual(h.purchases[0]!.options, { amount: 1_250, reference: REFERENCE });
  await h.purchases[0]!.call.resolve(sdkTransaction());
  await h.adapter.recover(attempt({ state: "Pending" }));

  // 打开开关（无需重建适配器）：下一笔扣款隐藏 SDK 窗口。
  const secondId = "0f8fad5b-d9cb-469f-a165-70867728950e";
  const secondReference = "HBZ-0f8fad5bd9cb469fa16570867728950e";
  const second = attempt({
    attemptId: secondId,
    references: { ...attempt().references, txnRef: secondReference },
  });
  h.registry.setHideSdkUi(true);
  await h.adapter.submit(second);
  assert.deepEqual(h.purchases[1]!.options, {
    amount: 1_250,
    reference: secondReference,
    unattended: true,
  });
  await h.purchases[1]!.call.resolve(sdkTransaction({ externalReference: secondReference }));
  await h.adapter.recover({ ...second, state: "Pending" });

  // 再关闭：又恢复弹窗。
  const thirdId = "1a2b3c4d-1111-4222-8333-444455556666";
  const thirdReference = "HBZ-1a2b3c4d111142228333444455556666";
  const third = attempt({
    attemptId: thirdId,
    references: { ...attempt().references, txnRef: thirdReference },
  });
  h.registry.setHideSdkUi(false);
  await h.adapter.submit(third);
  assert.deepEqual(h.purchases[2]!.options, { amount: 1_250, reference: thirdReference });
});

test("打开隐藏窗口后，退款仍然不带 unattended（SDK 的退款没有这个选项，窗口照常弹出）", async () => {
  const h = harness();
  h.registry.setHideSdkUi(true);

  const refunding = h.adapter.refund(refundAttempt());
  await flush();
  assert.deepEqual(h.refunds.map((refund) => refund.options), [
    { amount: 500, purchase: UUID, reference: REFUND_REFERENCE },
  ]);
  await h.refunds[0]!.call.resolve(sdkRefund());
  await refunding;
});

test("终端来源没有提供隐藏窗口开关（旧组合）：按弹窗处理，不带 unattended", async () => {
  const purchases: unknown[] = [];
  const terminal: ZellerProbeTerminal = {
    initialise: () => Promise.resolve(true) as ZellerProbeOperation,
    setup: () => {
      throw new Error("unexpected SDK call");
    },
    refund: () => {
      throw new Error("unexpected SDK call");
    },
    purchase: (options) => {
      purchases.push(options);
      return deferredOperation().operation;
    },
    getTransactions: () => Promise.resolve([]) as ZellerProbeOperation,
  };
  const adapter = new ZellerPaymentAdapter({ terminals: { current: () => terminal } });
  await adapter.submit(attempt());
  assert.deepEqual(purchases, [{ amount: 1_250, reference: REFERENCE }]);
});

test("终端登记处：隐藏窗口开关默认关闭，可以开关", () => {
  const registry = new ZellerTerminalRegistry();
  assert.equal(registry.hideSdkUi(), false);
  registry.setHideSdkUi(true);
  assert.equal(registry.hideSdkUi(), true);
  registry.setHideSdkUi(false);
  assert.equal(registry.hideSdkUi(), false);
});
