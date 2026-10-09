import assert from "node:assert/strict";
import test from "node:test";

import {
  LinklyCloudBackendApi,
  LinklyCloudBackendProvider,
  type LinklyCloudBackendSession,
  type LinklyTerminalSelectionPort,
} from "./linkly-cloud-backend";

import { HbposApiError, type HbposTransport, type HbposTransportRequest } from "@/core/api/hbpos-api";
import type { PaymentAttempt } from "@hb/pos-domain/core/contracts/payment";
import { LinklySupervisorAckSessionNotFoundError } from "@hb/pos-payments-core/features/payments/supervisor-resolution-acknowledgement-service";

const session = (overrides: Partial<LinklyCloudBackendSession> = {}): LinklyCloudBackendSession => ({
  environment: "Sandbox", storeCode: "S1", deviceCode: "DEV1", sessionId: "session-1", status: "Pending",
  terminalId: null, terminalDisplayName: null,
  txnRef: "TXN-1", responseCode: null, responseText: null, recoveryAction: null, displayText: null,
  cancelKeyFlag: false, okKeyFlag: false, acceptYesKeyFlag: false, declineNoKeyFlag: false, authoriseKeyFlag: false,
  inputType: null, graphicCode: null, displayLines: [], receiptText: null, recoveryCount: 0, receiptPrintedAt: null,
  clientAcknowledgedAt: null, lastHttpStatus: null, notifications: [], transactionSuccess: null,
  cardTransaction: null,
  ...overrides,
});

const unknownAttempt = (overrides: Partial<PaymentAttempt> = {}): PaymentAttempt => ({
  attemptId: "attempt-1", idempotencyKey: "idem-1", orderGuid: "order-1", provider: "linkly-cloud", operation: "purchase",
  amount: { currency: "AUD", cents: 1234 }, state: "Unknown",
  references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null },
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
const selection: LinklyTerminalSelectionPort = {
  async readTerminals() { throw new Error("not used"); },
  async selectTerminal() { throw new Error("not used"); },
};
const provider = (transport: FakeTransport) =>
  new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), { environment: "Sandbox", terminalSelection: selection });

test("M17：主管结案 ACK 带 supervisorResolved 标记，接受 SupervisorResolved 终态", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok(session({ status: "SupervisorResolved", clientAcknowledgedAt: "2026-09-09T00:00:00.000Z" })));
  await provider(transport).acknowledgeSupervisorResolved(unknownAttempt());
  assert.equal(transport.requests.length, 1);
  const request = transport.requests[0]!;
  assert.equal(request.method, "POST");
  assert.equal(request.url, "/api/v1/linkly/cloud-backend/transactions/session-1/acknowledge");
  assert.deepEqual(request.data, { environment: "Sandbox", supervisorResolved: true });
});

test("M17：后端已有 Linkly 终态时 ACK 也算确认（终态保持原样）", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok(session({
    status: "Completed", transactionSuccess: false, responseCode: "05",
    clientAcknowledgedAt: "2026-09-09T00:00:00.000Z",
  })));
  await provider(transport).acknowledgeSupervisorResolved(unknownAttempt());
});

test("M17：旧后端忽略 supervisorResolved（状态仍非终态）、身份不符或缺确认时间都不算确认", async () => {
  const cases: Array<[Partial<LinklyCloudBackendSession>, RegExp]> = [
    [{ status: "Pending", clientAcknowledgedAt: "2026-09-09T00:00:00.000Z" }, /LINKLY_ACK_SUPERVISOR_STATE_REQUIRED/],
    [{ status: "SupervisorResolved", clientAcknowledgedAt: null }, /LINKLY_ACK_NOT_CONFIRMED/],
    [{ status: "SupervisorResolved", sessionId: "other", clientAcknowledgedAt: "2026-09-09T00:00:00.000Z" }, /LINKLY_ACK_CONTEXT_MISMATCH/],
    [{ status: "SupervisorResolved", environment: "Production", clientAcknowledgedAt: "2026-09-09T00:00:00.000Z" }, /LINKLY_ACK_CONTEXT_MISMATCH/],
  ];
  for (const [overrides, pattern] of cases) {
    const transport = new FakeTransport();
    transport.responses.push(ok(session(overrides)));
    await assert.rejects(provider(transport).acknowledgeSupervisorResolved(unknownAttempt()), pattern);
  }
});

test("M17：缺冻结环境或 SessionId 不发请求；后端 404 转为可识别的“无会话可关闭”", async () => {
  const transport = new FakeTransport();
  await assert.rejects(
    provider(transport).acknowledgeSupervisorResolved(unknownAttempt({ providerEnvironment: null })),
    /LINKLY_ACK_PROVIDER_ENVIRONMENT_REQUIRED/,
  );
  await assert.rejects(
    provider(transport).acknowledgeSupervisorResolved(unknownAttempt({
      references: { checkoutId: null, paymentId: null, sessionId: null, txnRef: null, rfn: null, voucherReservationToken: null },
    })),
    /LINKLY_ACK_PROVIDER_ENVIRONMENT_REQUIRED/,
  );
  assert.equal(transport.requests.length, 0);

  transport.responses.push({ status: 404, data: { success: false, errorCode: "LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND" } });
  await assert.rejects(
    provider(transport).acknowledgeSupervisorResolved(unknownAttempt()),
    LinklySupervisorAckSessionNotFoundError,
  );
  transport.responses.push(new HbposApiError("boom", { kind: "transport" }));
  await assert.rejects(provider(transport).acknowledgeSupervisorResolved(unknownAttempt()), HbposApiError);
});
