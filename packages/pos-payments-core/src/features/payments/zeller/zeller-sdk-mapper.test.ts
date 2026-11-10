import assert from "node:assert/strict";
import test from "node:test";

import { classifyZellerCall, classifyZellerVerification } from "./zeller-result-classifier";
import {
  ZELLER_NO_CALL_EVIDENCE,
  accumulateZellerCallEvidence,
  mapZellerSdkCallException,
  mapZellerSdkCallResponse,
  mapZellerSdkPairingResponse,
  mapZellerSdkQueryResponse,
  mapZellerSdkTransaction,
  normalizeZellerTimestamp,
  zellerErrorTypeFromSdkName,
} from "./zeller-sdk-mapper";
import { ZELLER_ERROR_TYPE_CODES } from "./zeller-terminal-models";

const REFERENCE = "HBZ-799287a33dc24df1bca3d33eea19b35a";
const UUID = "65c4b343-03e6-49ef-a54d-012e3f2c222c";

/** RN SDK 0.2.5 类型声明里的全部 24 种错误类型字符串。 */
const SDK_ERROR_NAMES = [
  "Setup Required",
  "Network Failure",
  "Operation Failure",
  "Operation Busy",
  "Operation Interrupted",
  "Operation Request Invalid",
  "Operation Response Invalid",
  "Connection Failure",
  "Paired Device Busy",
  "Paired Device Not Available",
  "Cancelled",
  "Inactivity Timeout",
  "Card Declined",
  "Card Blocked",
  "Card Not Supported",
  "Card Read Failure",
  "Card Insert Required",
  "Transaction Not Found",
  "Transaction Response Timeout",
  "Transaction Response Invalid",
  "Transaction Network Failure",
  "Transaction System Failure",
  "Transaction Declined",
  "Transaction Failure",
] as const;

function sdkError(type: unknown, message = ""): Error {
  return Object.assign(new Error(message), { type });
}

function sdkTransaction(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    $type: "Approved",
    type: "PURCHASE",
    amount: 100,
    caid: "CAID-1",
    currency: "AUD",
    responseCode: "00",
    responseText: "APPROVED",
    status: "APPROVED",
    timestampLocal: "2026-11-05T11:24:01+10:00",
    transactionUuid: UUID,
    externalReference: REFERENCE,
    catid: "CATID-1",
    approvalCode: "456321",
    panMasked: "1234",
    scheme: "VISA",
    receiptText: "APPROVED",
    ...over,
  };
}

test("RN SDK 的 24 种错误类型字符串都能对上我们的错误类型", () => {
  assert.equal(SDK_ERROR_NAMES.length, 24);
  for (const name of SDK_ERROR_NAMES) {
    const mapped = zellerErrorTypeFromSdkName(name);
    assert.notEqual(mapped, "Unrecognised", name);
    assert.ok(mapped in ZELLER_ERROR_TYPE_CODES, name);
    assert.equal(mapped, name.replaceAll(" ", ""));
  }
  assert.equal(zellerErrorTypeFromSdkName("Operation Busy"), "OperationBusy");
});

test("不认识的错误类型一律 Unrecognised，不会被当成未付款", () => {
  for (const unknownType of ["Something New", "", "cancelled", 12, null, undefined, {}]) {
    assert.equal(zellerErrorTypeFromSdkName(unknownType), "Unrecognised");
  }
  const classified = classifyZellerCall(mapZellerSdkCallResponse(sdkError("Something New")), {
    type: "Purchase",
    amountCents: 100,
    reference: REFERENCE,
  });
  assert.equal(classified.decision, "Unknown");
});

test("批准的扣款：转换后被分类器判为批准，字段逐项对应", () => {
  const result = mapZellerSdkCallResponse(sdkTransaction());
  assert.equal(result.outcome, "Transaction");
  assert.deepEqual(result.transaction, {
    status: "Approved",
    type: "Purchase",
    amountCents: 100,
    transactionUuid: UUID,
    externalReference: REFERENCE,
    responseCode: "00",
    responseText: "APPROVED",
    approvalCode: "456321",
    rrn: null,
    panMasked: "1234",
    scheme: "VISA",
    bin: null,
    merchantId: "CAID-1",
    terminalId: "CATID-1",
    receiptText: "APPROVED",
    receiptLink: null,
    timestampIso: "2026-11-05T11:24:01+10:00",
  });
  const classified = classifyZellerCall(result, {
    type: "Purchase",
    amountCents: 100,
    reference: REFERENCE,
  });
  assert.equal(classified.decision, "Approved");
});

test("被拒的交易也以交易形式返回，必须看 status（真机：余额不足 code 51）", () => {
  const result = mapZellerSdkCallResponse(
    sdkTransaction({ $type: "Declined", status: "DECLINED", responseCode: "51" }),
  );
  assert.equal(result.outcome, "Transaction");
  assert.equal(result.transaction?.status, "Declined");
});

test("不认识的状态和类型转成 Unknown，交给分类器按结果未知处理", () => {
  const transaction = mapZellerSdkTransaction(sdkTransaction({ status: "PENDING", type: "VOID" }));
  assert.equal(transaction?.status, "Unknown");
  assert.equal(transaction?.type, "Unknown");
  // 大小写不同也不认：只认 SDK 声明的取值。
  assert.equal(mapZellerSdkTransaction(sdkTransaction({ status: "Approved" }))?.status, "Unknown");
});

test("SDK 返回 Error 对象表示失败：带上错误类型和调用期间的事件证据", () => {
  const evidence = accumulateZellerCallEvidence(ZELLER_NO_CALL_EVIDENCE, {
    event: "AWAITING_PAYMENT_METHOD",
  });
  const result = mapZellerSdkCallResponse(sdkError("Connection Failure", "lost"), evidence);
  assert.deepEqual(result, {
    outcome: "Error",
    errorType: "ConnectionFailure",
    message: "lost",
    reachedTerminal: true,
    processingStarted: false,
  });
  // 请求已到刷卡机后断开：不能排除后来完成了扣款。
  const classified = classifyZellerCall(result, {
    type: "Purchase",
    amountCents: 100,
    reference: REFERENCE,
  });
  assert.equal(classified.unknownReason, "TerminalConnectionLost");
});

test("事件证据只增不减：PROCESSING 同时说明请求到过刷卡机，其余事件不改变证据", () => {
  let evidence = ZELLER_NO_CALL_EVIDENCE;
  evidence = accumulateZellerCallEvidence(evidence, { event: "DISPLAYING_PAYMENT_MESSAGE" });
  assert.deepEqual(evidence, { reachedTerminal: false, processingStarted: false });
  evidence = accumulateZellerCallEvidence(evidence, { event: "PROCESSING" });
  assert.deepEqual(evidence, { reachedTerminal: true, processingStarted: true });
  evidence = accumulateZellerCallEvidence(evidence, { event: "AWAITING_PAYMENT_METHOD" });
  assert.deepEqual(evidence, { reachedTerminal: true, processingStarted: true });
  for (const garbage of [null, undefined, "PROCESSING", 1, []]) {
    assert.deepEqual(
      accumulateZellerCallEvidence(ZELLER_NO_CALL_EVIDENCE, garbage),
      ZELLER_NO_CALL_EVIDENCE,
    );
  }
});

test("读不懂的返回值转成 Faulted（结果未知），绝不当成没扣款", () => {
  for (const unexpected of [null, undefined, true, "ok", 0, [], {}, sdkTransaction({ amount: "100" }), sdkTransaction({ amount: 1.5 })]) {
    const result = mapZellerSdkCallResponse(unexpected);
    assert.equal(result.outcome, "Faulted");
    const classified = classifyZellerCall(result, {
      type: "Purchase",
      amountCents: 100,
      reference: REFERENCE,
    });
    assert.equal(classified.decision, "Unknown");
    assert.equal(classified.rebuildTerminalFirst, true);
  }
  assert.equal(mapZellerSdkCallException(new Error("boom")).outcome, "Faulted");
  assert.equal(mapZellerSdkCallException(new Error("boom")).message, "boom");
  assert.equal(mapZellerSdkCallException("boom").outcome, "Faulted");
});

test("查询：数组转成成功，Error 和读不懂的返回转成失败", () => {
  const found = mapZellerSdkQueryResponse([sdkTransaction(), sdkTransaction({ status: "DECLINED" })]);
  assert.equal(found.succeeded, true);
  assert.equal(found.transactions.length, 2);

  assert.deepEqual(mapZellerSdkQueryResponse([]), { succeeded: true, transactions: [] });

  const failed = mapZellerSdkQueryResponse(sdkError("Network Failure", "offline"));
  assert.deepEqual(failed, {
    succeeded: false,
    transactions: [],
    errorType: "NetworkFailure",
    message: "offline",
  });

  for (const unexpected of [null, undefined, {}, "[]", [sdkTransaction(), null], [{ status: "APPROVED" }]]) {
    const result = mapZellerSdkQueryResponse(unexpected);
    assert.equal(result.succeeded, false);
    assert.equal(result.errorType, "Unrecognised");
  }
});

test("查询里夹着读不懂的一笔时，「0 笔」不能成为未付款的结论", () => {
  const verification = classifyZellerVerification(
    "VerifyThenNotPaid",
    mapZellerSdkQueryResponse([null]),
    { type: "Purchase", amountCents: 100, reference: REFERENCE },
  );
  assert.equal(verification.decision, "KeepUnknown");
  assert.equal(verification.unknownReason, "VerificationFailed");
});

test("配对结果：只有严格的 true 才算已配对", () => {
  assert.deepEqual(mapZellerSdkPairingResponse(true), {
    isPaired: true,
    errorType: null,
    message: null,
  });
  assert.equal(mapZellerSdkPairingResponse(sdkError("Setup Required")).errorType, "SetupRequired");
  for (const unexpected of [false, "true", 1, null, undefined, {}]) {
    const result = mapZellerSdkPairingResponse(unexpected);
    assert.equal(result.isPaired, false);
    assert.equal(result.errorType, "Unrecognised");
  }
});

test("时间戳：没有时区的按 UTC，解析不了的留空", () => {
  assert.equal(normalizeZellerTimestamp("2026-11-05T01:27:12.345Z"), "2026-11-05T01:27:12.345Z");
  assert.equal(normalizeZellerTimestamp("2026-11-05T11:24:01+10:00"), "2026-11-05T11:24:01+10:00");
  assert.equal(normalizeZellerTimestamp(" 2026-11-05T01:27:12 "), "2026-11-05T01:27:12Z");
  assert.equal(
    Date.parse(normalizeZellerTimestamp("2026-11-05T01:27:12")!),
    Date.UTC(2026, 10, 5, 1, 27, 12),
  );
  for (const invalid of ["", "   ", "not a time", null, undefined, 1730770032000]) {
    assert.equal(normalizeZellerTimestamp(invalid), null);
  }
});
