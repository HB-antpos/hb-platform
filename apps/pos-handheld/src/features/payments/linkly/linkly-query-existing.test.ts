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

const session = (overrides: Partial<LinklyCloudBackendSession> = {}): LinklyCloudBackendSession => ({
  environment: "Sandbox", storeCode: "S1", deviceCode: "HH1", sessionId: "session-1", status: "Unknown",
  terminalId: null, terminalDisplayName: null,
  txnRef: "TXN-1", responseCode: null, responseText: null, recoveryAction: "Recover", displayText: null,
  cancelKeyFlag: false, okKeyFlag: false, acceptYesKeyFlag: false, declineNoKeyFlag: false, authoriseKeyFlag: false,
  inputType: null, graphicCode: null, displayLines: [], receiptText: null, recoveryCount: 0, receiptPrintedAt: null,
  clientAcknowledgedAt: null, lastHttpStatus: null, notifications: [], transactionSuccess: null,
  cardTransaction: null,
  ...overrides,
});

const attempt = (overrides: Partial<PaymentAttempt> = {}): PaymentAttempt => ({
  attemptId: "attempt-1", idempotencyKey: "idem-1", orderGuid: "order-1", provider: "linkly-cloud", operation: "purchase",
  amount: { currency: "AUD", cents: 1234 }, state: "Created",
  references: { checkoutId: null, paymentId: null, sessionId: null, txnRef: null, rfn: null, voucherReservationToken: null },
  providerEnvironment: "Sandbox",
  createdAtIso: "2026-07-28T00:00:00.000Z", updatedAtIso: "2026-07-28T00:00:00.000Z", lastErrorCode: null,
  ...overrides,
});

class FakeTransport implements HbposTransport {
  public readonly requests: HbposTransportRequest[] = [];
  public readonly responses: unknown[] = [];
  public async request<T>(request: HbposTransportRequest): Promise<{ status: number; data: T }> {
    this.requests.push(request);
    const next = this.responses.shift();
    if (next instanceof Error) throw next;
    return next as { status: number; data: T };
  }
}

const ok = (data: unknown) => ({ status: 200, data: { success: true, data } });
const none = () => ({ status: 404, data: { success: false, errorCode: "LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND" } });
const selection: LinklyTerminalSelectionPort = {
  async readTerminals() { throw new Error("not used"); },
  async selectTerminal() { throw new Error("not used"); },
};
const provider = (transport: FakeTransport) =>
  new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), { environment: "Sandbox", terminalSelection: selection });

const unknownAttempt = () => attempt({
  state: "Unknown",
  references: { ...attempt().references, sessionId: "session-1", txnRef: "TXN-1" },
});

test("人工结论后只 GET 原 Linkly 状态，恢复提示也不触发 POST", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok(session()));
  const result = await provider(transport).queryExistingPayment(unknownAttempt());
  assert.equal(result.queryVerified, true);
  assert.equal(transport.requests.length, 1);
  assert.equal(transport.requests[0]?.method, "GET");
  assert.equal(transport.requests[0]?.url, "/api/v1/linkly/cloud-backend/transactions/session-1/status");
  assert.equal(transport.requests[0]?.timeoutMs, 10_000);
  const before = transport.requests.length;
  await provider(transport).queryExistingPayment(attempt());
  await provider(transport).queryExistingPayment(attempt({ state: "Unknown" }));
  assert.equal(transport.requests.length, before, "Created 或缺少会话身份时不发任何请求");
});

test("Linkly 只读查询找不到交易或身份不匹配不能生成有效对账证据", async () => {
  for (const response of [none(), ok(session({ sessionId: "other-session" }))]) {
    const transport = new FakeTransport();
    transport.responses.push(response);
    const result = await provider(transport).queryExistingPayment(unknownAttempt());
    assert.equal(result.state, "Unknown");
    assert.equal(result.queryVerified, false);
    assert.equal(transport.requests.length, 1);
  }
});
