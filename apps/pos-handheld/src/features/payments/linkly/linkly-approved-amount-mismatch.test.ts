import assert from "node:assert/strict";
import test from "node:test";

import {
  LinklyCloudBackendApi,
  LinklyCloudBackendProvider,
  type LinklyCloudBackendSession,
  type LinklyTerminalSelectionPort,
} from "./linkly-cloud-backend";

import type { HbposTransport, HbposTransportRequest } from "@/core/api/hbpos-api";
import type { PaymentAttempt } from "@hb/pos-domain/core/contracts/payment";
import { parseApprovedAmountMismatchCents } from "@hb/pos-domain/features/payment-recovery/payment-recovery-center-contract";

const session = (overrides: Partial<LinklyCloudBackendSession> = {}): LinklyCloudBackendSession => ({
  environment: "Sandbox", storeCode: "S1", deviceCode: "DEV1", sessionId: "session-1", status: "Completed",
  terminalId: null, terminalDisplayName: null,
  txnRef: "TXN-1", responseCode: "00", responseText: "APPROVED", recoveryAction: null, displayText: null,
  cancelKeyFlag: false, okKeyFlag: false, acceptYesKeyFlag: false, declineNoKeyFlag: false, authoriseKeyFlag: false,
  inputType: null, graphicCode: null, displayLines: [], receiptText: null, recoveryCount: 0, receiptPrintedAt: null,
  clientAcknowledgedAt: null, lastHttpStatus: null, notifications: [], transactionSuccess: true,
  cardTransaction: null,
  ...overrides,
});

const evidence = (overrides: Record<string, unknown> = {}) => ({
  txnRef: "TXN-1", rfn: "RFN-1", authCode: "AUTH-1", cardType: "VISA", maskedCardNumber: "411111******1234",
  merchantId: "MID-1", responseCode: "00", responseText: "APPROVED", stan: "STAN-1",
  bankDateTime: "2026-07-28T10:30:00+10:00", amountCents: 1234,
  ...overrides,
});

const unknownAttempt = (): PaymentAttempt => ({
  attemptId: "attempt-1", idempotencyKey: "idem-1", orderGuid: "order-1", provider: "linkly-cloud", operation: "purchase",
  amount: { currency: "AUD", cents: 1234 }, state: "Unknown",
  references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null },
  providerEnvironment: "Sandbox",
  createdAtIso: "2026-07-28T00:00:00.000Z", updatedAtIso: "2026-07-28T00:00:00.000Z", lastErrorCode: null,
});

class FakeTransport implements HbposTransport {
  public readonly requests: HbposTransportRequest[] = [];
  public readonly responses: unknown[] = [];
  public async request<T>(request: HbposTransportRequest): Promise<{ status: number; data: T }> {
    this.requests.push(request);
    return this.responses.shift() as { status: number; data: T };
  }
}

const ok = (data: unknown) => ({ status: 200, data: { success: true, data } });
const selection: LinklyTerminalSelectionPort = {
  async readTerminals() { throw new Error("not used"); },
  async selectTerminal() { throw new Error("not used"); },
};

function queryProvider(backend: LinklyCloudBackendSession) {
  const transport = new FakeTransport();
  transport.responses.push(ok(backend));
  return new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), {
    environment: "Sandbox",
    terminalSelection: selection,
  });
}

test("M34：终端批准但实扣金额不符 → Unknown + 带实扣分值的响应码，不产出可入账证据", async () => {
  const result = await queryProvider(session({ cardTransaction: evidence({ amountCents: 1500 }) }))
    .queryExistingPayment(unknownAttempt());
  assert.equal(result.state, "Unknown");
  assert.equal(result.responseCode, "LINKLY_APPROVED_AMOUNT_MISMATCH:1500");
  assert.equal(result.protectedSyncEvidence, undefined);
  assert.equal(result.queryVerified, false, "金额不符不能作为人工“已收款”的有效对账");
  assert.equal(parseApprovedAmountMismatchCents(result.responseCode), 1500);
});

test("M34：金额一致仍为 Approved；身份不一致时金额不符不覆盖通用失败码", async () => {
  const approved = await queryProvider(session({ cardTransaction: evidence() })).queryExistingPayment(unknownAttempt());
  assert.equal(approved.state, "Approved");

  const wrongTxn = await queryProvider(session({
    txnRef: "TXN-OTHER",
    cardTransaction: evidence({ txnRef: "TXN-OTHER", amountCents: 1500 }),
  })).queryExistingPayment(unknownAttempt());
  assert.equal(wrongTxn.state, "Unknown");
  assert.notEqual(parseApprovedAmountMismatchCents(wrongTxn.responseCode), 1500);

  const wrongSession = await queryProvider(session({
    cardTransaction: evidence({ amountCents: 1500, txnRef: "TXN-OTHER" }),
  })).queryExistingPayment(unknownAttempt());
  assert.equal(wrongSession.responseCode, "LINKLY_CARD_EVIDENCE_MISMATCH");
});

test("M34：实扣分值解析只接受合法响应码", () => {
  for (const [value, expected] of [
    ["LINKLY_APPROVED_AMOUNT_MISMATCH:1500", 1500],
    ["linkly_approved_amount_mismatch:7", 7],
    ["LINKLY_APPROVED_AMOUNT_MISMATCH:0", null],
    ["LINKLY_APPROVED_AMOUNT_MISMATCH:", null],
    ["LINKLY_APPROVED_AMOUNT_MISMATCH:-5", null],
    ["LINKLY_APPROVED_AMOUNT_MISMATCH:15x", null],
    ["LINKLY_APPROVED_AMOUNT_MISMATCH:1234567890123", null],
    ["LINKLY_CARD_EVIDENCE_MISMATCH", null],
    [null, null],
  ] as const) {
    assert.equal(parseApprovedAmountMismatchCents(value), expected, String(value));
  }
});
