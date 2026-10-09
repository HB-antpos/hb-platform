import assert from "node:assert/strict";
import test from "node:test";

import {
  LinklyCloudBackendApi,
  LinklyCloudBackendProvider,
  LinklyPaymentTerminalSelectionCoordinator,
  type LinklyCloudBackendSession,
  type LinklyPaymentTerminalSelectionExpectation,
  type LinklyTerminalSelectionPort,
  type LinklyTerminalSelectionSnapshot,
} from "./linkly-cloud-backend";

import { HbposApiError, type HbposTransport, type HbposTransportRequest } from "@/core/api/hbpos-api";
import type { PaymentAttempt } from "@hb/pos-domain/core/contracts/payment";

const session = (overrides: Partial<LinklyCloudBackendSession> = {}): LinklyCloudBackendSession => ({
  environment: "Sandbox", storeCode: "S1", deviceCode: "IPAD1", sessionId: "session-1", status: "InProgress",
  terminalId: null, terminalDisplayName: null,
  txnRef: "TXN-1", responseCode: null, responseText: null, recoveryAction: null, displayText: null,
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

function ok(data: unknown): { status: number; data: { success: true; data: unknown } } { return { status: 200, data: { success: true, data } }; }
function none(): { status: number; data: { success: false; errorCode: string } } { return { status: 404, data: { success: false, errorCode: "LINKLY_CLOUD_BACKEND_SESSION_NOT_FOUND" } }; }

const terminalSelection = (
  overrides: Partial<LinklyTerminalSelectionSnapshot> = {},
): LinklyTerminalSelectionSnapshot => ({
  environment: "Sandbox",
  mode: "Active",
  selectedTerminalId: "terminal-1",
  selectionRevision: 3,
  terminals: [
    {
      terminalId: "terminal-1",
      laneNo: 1,
      displayName: "Front",
      pairingState: "Ready",
      isBusy: false,
      isReady: true,
      lastHealthStatus: "Ready",
      lastHealthAt: "2026-09-02T00:00:00.000Z",
    },
  ],
  ...overrides,
});

class FakeTerminalSelectionPort implements LinklyTerminalSelectionPort {
  public constructor(public snapshot = terminalSelection()) {}

  public readTerminals(): Promise<LinklyTerminalSelectionSnapshot> {
    return Promise.resolve(this.snapshot);
  }

  public selectTerminal(
    environment: string,
    terminalId: string,
    expectedRevision: number,
  ): Promise<LinklyTerminalSelectionSnapshot> {
    this.snapshot = terminalSelection({
      environment,
      selectedTerminalId: terminalId,
      selectionRevision: expectedRevision + 1,
    });
    return Promise.resolve(this.snapshot);
  }
}

function providerOptions(
  terminalSelectionPort: LinklyTerminalSelectionPort =
    new FakeTerminalSelectionPort(),
) {
  return {
    environment: "Sandbox",
    terminalSelection: terminalSelectionPort,
  } as const;
}

const recoveryUid = "3b241101-e2bb-4255-8caf-4136c566a962";

const confirmedTerminal = (
  overrides: Partial<LinklyPaymentTerminalSelectionExpectation> = {},
): LinklyPaymentTerminalSelectionExpectation => ({
  environment: "Sandbox",
  mode: "Active",
  terminalId: "terminal-1",
  selectionRevision: 3,
  ...overrides,
} as LinklyPaymentTerminalSelectionExpectation);

test("Linkly 新支付只使用 UI 已确认终端快照；权威选择漂移时零交易 POST", async () => {
  const matchingTransport = new FakeTransport();
  matchingTransport.responses.push(
    ok(terminalSelection()),
    none(),
    ok(session({ status: "Completed", transactionSuccess: false })),
  );
  const matchingApi = new LinklyCloudBackendApi(matchingTransport);
  const matchingSelection = new LinklyPaymentTerminalSelectionCoordinator(matchingApi);
  const matchingProvider = new LinklyCloudBackendProvider(matchingApi, {
    environment: "Sandbox",
    terminalSelection: matchingSelection,
  });

  const matchingResult = await matchingSelection.runWithSelection(
    "order-1",
    confirmedTerminal(),
    () => matchingProvider.submit(attempt()),
  );

  assert.equal(matchingResult.state, "Declined");
  assert.deepEqual(
    matchingTransport.requests.find(
      (request) =>
        request.method === "POST" &&
        request.url === "/api/v1/linkly/cloud-backend/transactions",
    )?.data,
    {
      environment: "Sandbox",
      terminalId: "terminal-1",
      selectionRevision: 3,
      txnType: "P",
      amtPurchase: 1234,
    },
  );

  const changedTransport = new FakeTransport();
  changedTransport.responses.push(ok(terminalSelection({
    selectedTerminalId: "terminal-2",
    selectionRevision: 4,
    terminals: [{
      ...terminalSelection().terminals[0]!,
      terminalId: "terminal-2",
      laneNo: 2,
      displayName: "Back",
    }],
  })));
  const changedApi = new LinklyCloudBackendApi(changedTransport);
  const changedSelection = new LinklyPaymentTerminalSelectionCoordinator(changedApi);
  const changedProvider = new LinklyCloudBackendProvider(changedApi, {
    environment: "Sandbox",
    terminalSelection: changedSelection,
  });

  const changedResult = await changedSelection.runWithSelection(
    "order-1",
    confirmedTerminal(),
    () => changedProvider.submit(attempt()),
  );

  assert.equal(changedResult.state, "Declined");
  assert.equal(
    changedResult.responseCode,
    "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  );
  assert.equal(
    changedTransport.requests.filter(
      (request) =>
        request.method === "POST" &&
        request.url === "/api/v1/linkly/cloud-backend/transactions",
    ).length,
    0,
  );
});

test("Linkly 退款进入已确认终端绑定后，权威选择漂移时零交易 POST", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    ok(terminalSelection({
      selectedTerminalId: "terminal-2",
      selectionRevision: 4,
      terminals: [{
        ...terminalSelection().terminals[0]!,
        terminalId: "terminal-2",
        laneNo: 2,
        displayName: "Back",
      }],
    })),
    none(),
    ok(session({ status: "Completed", transactionSuccess: false })),
  );
  const api = new LinklyCloudBackendApi(transport);
  const selection = new LinklyPaymentTerminalSelectionCoordinator(api);
  const provider = new LinklyCloudBackendProvider(api, {
    environment: "Sandbox",
    terminalSelection: selection,
  });

  const result = await selection.runWithSelection(
    "order-1",
    confirmedTerminal(),
    () => provider.refund(attempt({
      operation: "refund",
      amount: { currency: "AUD", cents: -1234 },
      references: {
        checkoutId: null,
        paymentId: null,
        sessionId: null,
        txnRef: "TXN-1",
        rfn: "RFN-1",
        voucherReservationToken: null,
      },
    })),
  );

  assert.equal(result.state, "Declined");
  assert.equal(
    result.responseCode,
    "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  );
  assert.equal(
    transport.requests.filter(
      (request) =>
        request.method === "POST" &&
        request.url === "/api/v1/linkly/cloud-backend/transactions",
    ).length,
    0,
  );
});

test("Linkly 新交易携带持久选择的 terminalId 与 selectionRevision，并解析会话终端显示名", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    none(),
    ok({
      ...session({ status: "Completed", transactionSuccess: false }),
      terminalId: "terminal-1",
      terminalDisplayName: "Front",
    }),
  );
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    {
      environment: "Sandbox",
      terminalSelection: new FakeTerminalSelectionPort(),
    },
  );

  const result = await provider.submit(attempt());

  assert.equal(result.state, "Declined");
  assert.deepEqual(transport.requests[1]?.data, {
    environment: "Sandbox",
    terminalId: "terminal-1",
    selectionRevision: 3,
    txnType: "P",
    amtPurchase: 1234,
  });
  const api = new LinklyCloudBackendApi(transport);
  transport.responses.push(ok({
    ...session(),
    terminalId: "terminal-1",
    terminalDisplayName: "Front",
  }));
  const bound = await api.status("Sandbox", "session-1");
  assert.equal(bound.terminalId, "terminal-1");
  assert.equal(bound.terminalDisplayName, "Front");
});

test("Linkly 未选择、忙碌或未配对终端时 fail closed 且不发送付款", async () => {
  for (const [snapshot, code] of [
    [terminalSelection({ selectedTerminalId: null }), "LINKLY_TERMINAL_SELECTION_REQUIRED"],
    [terminalSelection({ terminals: [{ ...terminalSelection().terminals[0]!, isBusy: true }] }), "LINKLY_TERMINAL_BUSY"],
    [terminalSelection({ terminals: [{ ...terminalSelection().terminals[0]!, pairingState: "Unpaired", isReady: false }] }), "LINKLY_TERMINAL_NOT_READY"],
  ] as const) {
    const transport = new FakeTransport();
    const provider = new LinklyCloudBackendProvider(
      new LinklyCloudBackendApi(transport),
      {
        environment: "Sandbox",
        terminalSelection: new FakeTerminalSelectionPort(snapshot),
      },
    );

    const result = await provider.submit(attempt());

    assert.equal(result.state, "Declined");
    assert.equal(result.responseCode, code);
    assert.equal(transport.requests.length, 0);
  }
});

test("Linkly Legacy 与 Draft 模式保持旧交易载荷且不因空终端列表锁卡", async () => {
  for (const mode of ["Legacy", "Draft"] as const) {
    const transport = new FakeTransport();
    transport.responses.push(
      none(),
      ok(session({ status: "Completed", transactionSuccess: false })),
    );
    const provider = new LinklyCloudBackendProvider(
      new LinklyCloudBackendApi(transport),
      providerOptions(
        new FakeTerminalSelectionPort(
          terminalSelection({
            mode,
            selectedTerminalId: null,
            terminals: [],
          }),
        ),
      ),
    );

    const result = await provider.submit(attempt());

    assert.equal(result.state, "Declined");
    assert.deepEqual(transport.requests[1]?.data, {
      environment: "Sandbox",
      txnType: "P",
      amtPurchase: 1234,
    });
  }
});

test("Linkly 兼容旧服务缺 mode 与 Draft 的 null revision，且均保持旧交易载荷", async () => {
  for (const rawMode of [undefined, "Draft"] as const) {
    const transport = new FakeTransport();
    transport.responses.push(
      ok({
        environment: "Sandbox",
        ...(rawMode === undefined ? {} : { mode: rawMode }),
        selectedTerminalId: null,
        selectionRevision: null,
        terminals: [],
      }),
      none(),
      ok(session({ status: "Completed", transactionSuccess: false })),
    );
    const api = new LinklyCloudBackendApi(transport);
    const provider = new LinklyCloudBackendProvider(api, {
      environment: "Sandbox",
      terminalSelection: api,
    });

    const result = await provider.submit(attempt());

    assert.equal(result.state, "Declined");
    assert.deepEqual(transport.requests[2]?.data, {
      environment: "Sandbox",
      txnType: "P",
      amtPurchase: 1234,
    });
  }
});

test("Linkly Active 新设备尚未选择终端时把 null revision 规范为零", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok({
    environment: "Sandbox",
    mode: "Active",
    selectedTerminalId: null,
    selectionRevision: null,
    terminals: terminalSelection().terminals,
  }));
  const api = new LinklyCloudBackendApi(transport);

  const result = await api.readTerminals("Sandbox");

  assert.equal(result.mode, "Active");
  assert.equal(result.selectedTerminalId, null);
  assert.equal(result.selectionRevision, 0);
});

function transactionNotification(input: Readonly<{
  uid: string;
  txnType?: "P" | "R";
  amountCents?: number;
  txnRef?: string;
}>): LinklyCloudBackendSession["notifications"][number] {
  return {
    type: "transaction",
    payloadJson: JSON.stringify({
      Response: {
        TxnType: input.txnType ?? "P",
        AmtPurchase: input.amountCents ?? 1234,
        TxnRef: input.txnRef ?? "TXN-RECOVERY",
        PurchaseAnalysisData: { UID: input.uid },
      },
    }),
    receivedAt: "2026-07-28T00:00:05.000Z",
  };
}

test("Linkly API 只经 Hbpos.Api 调用 create/status/active/resumable/recover/sendkey/receipt/ack", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok(session()), ok(session()), none(), ok(session()), ok(session()), ok(session()), ok(session()), ok(session()));
  const api = new LinklyCloudBackendApi(transport);

  await api.create({ environment: "Sandbox", terminalId: "terminal-1", selectionRevision: 3, txnType: "P", amtPurchase: 1234, purchaseAnalysisData: null });
  await api.status("Sandbox", "session-1");
  assert.equal(await api.active("Sandbox"), null);
  await api.resumable("Sandbox");
  await api.recover("Sandbox", "session-1");
  await api.sendKey("Sandbox", "session-1", "CANCEL", null);
  await api.markReceiptPrinted("Sandbox", "session-1");
  await api.acknowledge("Sandbox", "session-1");

  assert.deepEqual(transport.requests.map((request) => request.url), [
    "/api/v1/linkly/cloud-backend/transactions",
    "/api/v1/linkly/cloud-backend/transactions/session-1/status",
    "/api/v1/linkly/cloud-backend/transactions/active",
    "/api/v1/linkly/cloud-backend/transactions/resumable",
    "/api/v1/linkly/cloud-backend/transactions/session-1/recover",
    "/api/v1/linkly/cloud-backend/transactions/session-1/sendkey",
    "/api/v1/linkly/cloud-backend/transactions/session-1/receipt/printed",
    "/api/v1/linkly/cloud-backend/transactions/session-1/acknowledge",
  ]);
  assert.equal(transport.requests[2]?.params?.environment, "Sandbox");
});

test("Linkly 终端选择 API 仅解析安全字段，PUT 后重读列表为权威", async () => {
  const transport = new FakeTransport();
  const raw = {
    ...terminalSelection(),
    terminals: [
      {
        ...terminalSelection().terminals[0],
        username: "must-not-read",
        secret: "must-not-read",
        posId: "must-not-read",
      },
    ],
  };
  transport.responses.push(
    ok(raw),
    ok({
      environment: "Sandbox",
      selectedTerminalId: "terminal-1",
      selectionRevision: 4,
    }),
    ok({ ...raw, selectionRevision: 4 }),
  );
  const api = new LinklyCloudBackendApi(transport);

  const listed = await api.readTerminals("Sandbox");
  const selected = await api.selectTerminal("Sandbox", "terminal-1", 3);

  assert.deepEqual(listed, terminalSelection());
  assert.equal(selected.selectionRevision, 4);
  assert.deepEqual(
    transport.requests.map((request) => `${request.method} ${request.url}`),
    [
      "GET /api/v1/linkly/cloud-backend/terminals",
      "PUT /api/v1/linkly/cloud-backend/terminal-selection",
      "GET /api/v1/linkly/cloud-backend/terminals",
    ],
  );
  assert.deepEqual(transport.requests[1]?.data, {
    environment: "Sandbox",
    terminalId: "terminal-1",
    expectedRevision: 3,
  });
});

test("成功、拒绝和显式取消映射为支付结果，保留 SessionId/TxnRef/RFN", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    none(),
    ok({
      ...session({ status: "Completed", transactionSuccess: true, responseCode: "00" }),
      cardTransaction: cardTransaction({ rfn: "TXN-1" }),
    }),
    none(),
    ok(session({ status: "Completed", transactionSuccess: false, responseCode: "DECLINED" })),
    ok(session({ status: "Cancelled", transactionSuccess: false })),
  );
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());

  const approved = await provider.submit(attempt());
  const declined = await provider.submit(attempt({ attemptId: "attempt-2", idempotencyKey: "idem-2" }));
  const cancelled = await provider.cancel(attempt({ references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: "RFN-1", voucherReservationToken: null } }));

  assert.equal(approved.state, "Approved");
  assert.deepEqual(approved.references, { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: "TXN-1", voucherReservationToken: null });
  assert.equal(declined.state, "Declined");
  assert.equal(cancelled.state, "Cancelled");
  assert.equal(transport.requests.filter((request) => request.method === "POST" && request.url === "/api/v1/linkly/cloud-backend/transactions").length, 2);
});

test("新支付遇到其他 active session 或 create 409 时拒绝本次，不挪用旧 SessionId/TxnRef", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    ok(session({ sessionId: "other-session", txnRef: "OTHER-TXN", status: "Pending" })),
    none(),
    new HbposApiError("another transaction is active", { kind: "http", status: 409, code: "LINKLY_CLOUD_BACKEND_ACTIVE_TRANSACTION" }),
  );
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());

  const active = await provider.submit(attempt());
  const conflict = await provider.submit(attempt({ attemptId: "attempt-409", idempotencyKey: "idem-409" }));

  for (const result of [active, conflict]) {
    assert.equal(result.state, "Declined");
    assert.equal(result.responseCode, "LINKLY_ACTIVE_SESSION_CONFLICT");
    assert.equal(result.references.sessionId, null);
    assert.equal(result.references.txnRef, null);
  }
  assert.equal(transport.requests.filter((request) => request.method === "POST" && request.url === "/api/v1/linkly/cloud-backend/transactions").length, 1);
});

test("create 409 只精确分类 active transaction；selection conflict 独立且其他 409 是明确未提交的 Declined", async () => {
  const selectionTransport = new FakeTransport();
  selectionTransport.responses.push(
    none(),
    new HbposApiError("selection changed", {
      kind: "http",
      status: 409,
      code: "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
    }),
  );
  const selectionProvider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(selectionTransport),
    providerOptions(),
  );

  const selectionResult = await selectionProvider.submit(attempt());

  assert.equal(selectionResult.state, "Declined");
  assert.equal(
    selectionResult.responseCode,
    "LINKLY_CLOUD_TERMINAL_SELECTION_CONFLICT",
  );
  assert.equal(
    selectionTransport.requests.filter(
      (request) =>
        request.method === "POST" &&
        request.url === "/api/v1/linkly/cloud-backend/transactions",
    ).length,
    1,
  );

  const otherTransport = new FakeTransport();
  const otherConflict = new HbposApiError("unrelated conflict", {
    kind: "http",
    status: 409,
    code: "LINKLY_CLOUD_OTHER_CONFLICT",
  });
  otherTransport.responses.push(none(), otherConflict);
  const otherProvider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(otherTransport),
    providerOptions(),
  );

  // 其他 409（凭据轮换、重新录入等）发生在 Linkly POST 之前：证明没有创建会话，
  // 必须是带具体码的 Declined，而不是原样抛出后被写成无会话的 Unknown。
  const otherResult = await otherProvider.submit(attempt());
  assert.equal(otherResult.state, "Declined");
  assert.equal(otherResult.responseCode, "LINKLY_NOT_SUBMITTED_HTTP_409");
  assert.equal(otherResult.references.sessionId, null);
  assert.equal(
    otherTransport.requests.filter((request) => request.url.endsWith("/active") || request.url.endsWith("/resumable")).length,
    1,
    "明确未提交不得进入 active/resumable 歧义恢复",
  );
});

test("create 前终端变为不可用时把精确 409 映射为 Declined，且绝不恢复或重放", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    none(),
    new HbposApiError("terminal became unavailable", {
      kind: "http",
      status: 409,
      code: "LINKLY_CLOUD_TERMINAL_NOT_READY",
    }),
  );
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );

  const result = await provider.submit(attempt());

  assert.equal(result.state, "Declined");
  assert.equal(result.responseCode, "LINKLY_TERMINAL_NOT_READY");
  assert.equal(
    transport.requests.filter(
      (request) =>
        request.method === "POST" &&
        request.url === "/api/v1/linkly/cloud-backend/transactions",
    ).length,
    1,
  );
  assert.equal(transport.requests.length, 2);
});

test("旧 attempt 缺少有效 UID 时保持 Unknown，不查询或认领 claim 范围的旧交易", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    none(),
    new HbposApiError("connection lost", { kind: "transport" }),
    ok({
      ...session({
        sessionId: "stale-session",
        txnRef: "STALE-TXN",
        status: "Completed",
        transactionSuccess: true,
      }),
      cardTransaction: cardTransaction({
        txnRef: "STALE-TXN",
        rfn: "STALE-RFN",
      }),
    }),
  );
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());

  const result = await provider.submit(attempt());

  assert.equal(result.state, "Unknown");
  assert.equal(result.references.sessionId, null);
  assert.equal(result.references.txnRef, null);
  assert.equal(transport.requests.filter((request) => request.url === "/api/v1/linkly/cloud-backend/transactions" && request.method === "POST").length, 1);
  assert.equal(transport.requests.some((request) => request.url.endsWith("/resumable")), false);
  assert.equal(transport.requests.some((request) => request.url.endsWith("/sendkey")), false);
});

test("create 响应丢失后以持久化 UID 强匹配 active，再 status/recover 并绑定 SessionId", async () => {
  const transport = new FakeTransport();
  const active = session({
    sessionId: "session-recovery",
    txnRef: "TXN-RECOVERY",
    status: "Pending",
    recoveryAction: "Retry",
    notifications: [transactionNotification({ uid: recoveryUid })],
  });
  transport.responses.push(
    none(),
    new HbposApiError("connection lost after submit", { kind: "transport" }),
    ok(active),
    ok(active),
    ok({
      ...active,
      status: "Completed",
      transactionSuccess: true,
      responseCode: "00",
      cardTransaction: cardTransaction({
        txnRef: "TXN-RECOVERY",
        rfn: "RFN-RECOVERY",
      }),
    }),
  );
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );

  const result = await provider.submit(attempt({ idempotencyKey: recoveryUid }));

  assert.equal(result.state, "Approved");
  assert.equal(result.references.sessionId, "session-recovery");
  assert.equal(result.references.txnRef, "TXN-RECOVERY");
  assert.equal(result.references.rfn, "RFN-RECOVERY");
  assert.deepEqual(
    transport.requests.map((request) => `${request.method} ${request.url}`),
    [
      "GET /api/v1/linkly/cloud-backend/transactions/active",
      "POST /api/v1/linkly/cloud-backend/transactions",
      "GET /api/v1/linkly/cloud-backend/transactions/active",
      "GET /api/v1/linkly/cloud-backend/transactions/session-recovery/status",
      "POST /api/v1/linkly/cloud-backend/transactions/session-recovery/recover",
    ],
  );
  assert.deepEqual(
    (transport.requests[1]?.data as { purchaseAnalysisData?: unknown })
      .purchaseAnalysisData,
    { UID: recoveryUid },
  );
});

test("进程重启后的新 provider 用耐久 attempt UID 按 active→resumable 恢复响应丢失交易", async () => {
  const firstTransport = new FakeTransport();
  firstTransport.responses.push(
    none(),
    new HbposApiError("connection lost after submit", { kind: "transport" }),
    none(),
    none(),
  );
  const firstProvider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(firstTransport),
    providerOptions(),
  );
  const persistedAttempt = attempt({ idempotencyKey: recoveryUid });

  const ambiguous = await firstProvider.submit(persistedAttempt);
  assert.equal(ambiguous.state, "Unknown");
  assert.deepEqual(
    firstTransport.requests.slice(2).map((request) => request.url),
    [
      "/api/v1/linkly/cloud-backend/transactions/active",
      "/api/v1/linkly/cloud-backend/transactions/resumable",
    ],
  );

  const resumable = session({
    sessionId: "session-after-restart",
    txnRef: "TXN-RECOVERY",
    status: "Completed",
    transactionSuccess: true,
    responseCode: "00",
    notifications: [transactionNotification({ uid: recoveryUid })],
    cardTransaction: cardTransaction({
      txnRef: "TXN-RECOVERY",
      rfn: "RFN-AFTER-RESTART",
    }),
  });
  const restartedTransport = new FakeTransport();
  restartedTransport.responses.push(none(), ok(resumable), ok(resumable));
  const restartedProvider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(restartedTransport),
    providerOptions(),
  );

  const recovered = await restartedProvider.recover({
    ...persistedAttempt,
    state: "Unknown",
    updatedAtIso: "2026-07-28T00:01:00.000Z",
    lastErrorCode: "LINKLY_SESSION_UNRESOLVED",
  });

  assert.equal(recovered.state, "Approved");
  assert.equal(recovered.references.sessionId, "session-after-restart");
  assert.deepEqual(
    restartedTransport.requests.map((request) => request.url),
    [
      "/api/v1/linkly/cloud-backend/transactions/active",
      "/api/v1/linkly/cloud-backend/transactions/resumable",
      "/api/v1/linkly/cloud-backend/transactions/session-after-restart/status",
    ],
  );
});

test("退款响应丢失用独立 UID 和原 RFN 强匹配，不混淆金额方向", async () => {
  const recoveredRefund = session({
    sessionId: "refund-session",
    txnRef: "TXN-REFUND-RECOVERY",
    status: "Completed",
    transactionSuccess: true,
    responseCode: "00",
    notifications: [transactionNotification({
      uid: recoveryUid,
      txnType: "R",
      txnRef: "TXN-REFUND-RECOVERY",
    })],
    cardTransaction: cardTransaction({
      txnRef: "TXN-REFUND-RECOVERY",
      rfn: "RFN-ORIGINAL",
    }),
  });
  const transport = new FakeTransport();
  transport.responses.push(
    none(),
    new HbposApiError("connection lost after refund submit", {
      kind: "transport",
    }),
    none(),
    ok(recoveredRefund),
    ok(recoveredRefund),
  );
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );

  const result = await provider.refund(attempt({
    idempotencyKey: recoveryUid,
    operation: "refund",
    amount: { currency: "AUD", cents: -1234 },
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: null,
      txnRef: "TXN-1",
      rfn: "RFN-ORIGINAL",
      voucherReservationToken: null,
    },
  }));

  assert.equal(result.state, "Approved");
  assert.equal(result.references.sessionId, "refund-session");
  assert.equal(result.references.txnRef, "TXN-REFUND-RECOVERY");
  assert.equal(result.references.rfn, "RFN-ORIGINAL");
  assert.ok((transport.requests[2]?.timeoutMs ?? 0) <= 180_000);
  assert.ok((transport.requests[2]?.timeoutMs ?? 0) > 170_000);
  assert.deepEqual(
    (transport.requests[1]?.data as { purchaseAnalysisData?: unknown })
      .purchaseAnalysisData,
    {
      UID: recoveryUid,
      RFN: "RFN-ORIGINAL",
    },
  );
});

test("active/resumable 仅同额同类型但 UID 不匹配时拒绝误绑定", async () => {
  const transport = new FakeTransport();
  const weakCandidate = session({
    sessionId: "other-session",
    txnRef: "OTHER-TXN",
    status: "Pending",
    notifications: [transactionNotification({
      uid: "89bd9d8d-69a2-4803-8f8d-830ec69be3a0",
      txnRef: "OTHER-TXN",
    })],
  });
  transport.responses.push(ok(weakCandidate), ok(weakCandidate));
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );

  const result = await provider.recover(attempt({
    idempotencyKey: recoveryUid,
    state: "Unknown",
  }));

  assert.equal(result.state, "Unknown");
  assert.equal(result.references.sessionId, null);
  assert.deepEqual(
    transport.requests.map((request) => request.url),
    [
      "/api/v1/linkly/cloud-backend/transactions/active",
      "/api/v1/linkly/cloud-backend/transactions/resumable",
    ],
  );
  assert.equal(
    transport.requests.some((request) =>
      request.url.includes("/other-session/status") ||
      request.url.includes("/other-session/recover")),
    false,
  );
});

test("UID 相同但交易类型、金额、TxnRef 或通知身份不唯一时仍拒绝绑定", async () => {
  const candidates = [
    session({
      sessionId: "wrong-type",
      txnRef: "TXN-RECOVERY",
      notifications: [transactionNotification({
        uid: recoveryUid,
        txnType: "R",
      })],
    }),
    session({
      sessionId: "wrong-amount",
      txnRef: "TXN-RECOVERY",
      notifications: [transactionNotification({
        uid: recoveryUid,
        amountCents: 999,
      })],
    }),
    session({
      sessionId: "wrong-txn-ref",
      txnRef: "TXN-SESSION",
      notifications: [transactionNotification({
        uid: recoveryUid,
        txnRef: "TXN-NOTIFICATION",
      })],
    }),
    session({
      sessionId: "conflicting-notifications",
      txnRef: "TXN-RECOVERY",
      notifications: [
        transactionNotification({ uid: recoveryUid }),
        transactionNotification({
          uid: "89bd9d8d-69a2-4803-8f8d-830ec69be3a0",
        }),
      ],
    }),
  ];

  for (const candidate of candidates) {
    const transport = new FakeTransport();
    transport.responses.push(ok(candidate), ok(candidate));
    const provider = new LinklyCloudBackendProvider(
      new LinklyCloudBackendApi(transport),
      providerOptions(),
    );

    const result = await provider.recover(attempt({
      idempotencyKey: recoveryUid,
      state: "Unknown",
    }));

    assert.equal(result.state, "Unknown");
    assert.equal(result.references.sessionId, null);
    assert.deepEqual(
      transport.requests.map((request) => request.url),
      [
        "/api/v1/linkly/cloud-backend/transactions/active",
        "/api/v1/linkly/cloud-backend/transactions/resumable",
      ],
    );
  }
});

test("交易通知含大小写重复的 UID 字段时视为关联证据冲突", async () => {
  const ambiguousUid = session({
    sessionId: "ambiguous-uid",
    txnRef: "TXN-RECOVERY",
    status: "Pending",
    notifications: [{
      type: "transaction",
      payloadJson: JSON.stringify({
        Response: {
          TxnType: "P",
          AmtPurchase: 1234,
          TxnRef: "TXN-RECOVERY",
          PurchaseAnalysisData: {
            UID: recoveryUid,
            uid: "89bd9d8d-69a2-4803-8f8d-830ec69be3a0",
          },
        },
      }),
      receivedAt: "2026-07-28T00:00:05.000Z",
    }],
  });
  const transport = new FakeTransport();
  transport.responses.push(ok(ambiguousUid), ok(ambiguousUid), ok(ambiguousUid));
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );

  const result = await provider.recover(attempt({
    idempotencyKey: recoveryUid,
    state: "Unknown",
  }));

  assert.equal(result.state, "Unknown");
  assert.equal(result.references.sessionId, null);
  assert.deepEqual(
    transport.requests.map((request) => request.url),
    [
      "/api/v1/linkly/cloud-backend/transactions/active",
      "/api/v1/linkly/cloud-backend/transactions/resumable",
    ],
  );
});

test("候选绑定后 status 的门店或设备作用域变化时失败关闭且不调用 recover", async () => {
  const active = session({
    sessionId: "session-recovery",
    txnRef: "TXN-RECOVERY",
    status: "Pending",
    notifications: [transactionNotification({ uid: recoveryUid })],
  });

  for (const changedScope of [
    { storeCode: "S2" },
    { deviceCode: "IPAD2" },
  ]) {
    const transport = new FakeTransport();
    transport.responses.push(ok(active), ok(session({
      ...active,
      ...changedScope,
    })));
    const provider = new LinklyCloudBackendProvider(
      new LinklyCloudBackendApi(transport),
      providerOptions(),
    );

    const result = await provider.recover(attempt({
      idempotencyKey: recoveryUid,
      state: "Unknown",
    }));

    assert.equal(result.state, "Unknown");
    assert.equal(result.references.sessionId, null);
    assert.deepEqual(
      transport.requests.map((request) => request.url),
      [
        "/api/v1/linkly/cloud-backend/transactions/active",
        "/api/v1/linkly/cloud-backend/transactions/session-recovery/status",
      ],
    );
  }
});

test("Unknown 可通过既有 SessionId 恢复；旧 attempt 无 UID 时零网络，退款必须携带 RFN", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    ok({
      ...session({ status: "Completed", transactionSuccess: true, responseCode: "00", txnRef: "TXN-R" }),
      cardTransaction: cardTransaction({ txnRef: "TXN-R", rfn: "TXN-R" }),
    }),
    none(),
    ok({
      ...session({ status: "Completed", transactionSuccess: true, responseCode: "00", txnRef: "TXN-REFUND" }),
      cardTransaction: cardTransaction({ txnRef: "TXN-REFUND", rfn: "RFN-1" }),
    }),
  );
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());

  const unresolved = await provider.recover(attempt({ state: "Unknown" }));
  assert.equal(transport.requests.length, 0);
  const recovered = await provider.recover(attempt({ state: "Unknown", references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-R", rfn: "RFN-1", voucherReservationToken: null } }));
  const missingRfn = await provider.refund(attempt({
    operation: "refund",
    amount: { currency: "AUD", cents: -1234 },
  }));
  const refunded = await provider.refund(attempt({ attemptId: "attempt-refund", idempotencyKey: "idem-refund", operation: "refund", amount: { currency: "AUD", cents: -1234 }, references: { checkoutId: null, paymentId: null, sessionId: null, txnRef: "TXN-1", rfn: "RFN-1", voucherReservationToken: null } }));

  assert.equal(unresolved.state, "Unknown");
  assert.equal(recovered.state, "Approved");
  assert.equal(recovered.references.rfn, "TXN-R");
  assert.equal(missingRfn.state, "Declined");
  assert.equal(refunded.state, "Approved");
  const refundRequest = transport.requests.find((request) => request.method === "POST" && request.url === "/api/v1/linkly/cloud-backend/transactions" && (request.data as { txnType?: string }).txnType === "R");
  assert.deepEqual(refundRequest?.data, {
    environment: "Sandbox",
    terminalId: "terminal-1",
    selectionRevision: 3,
    txnType: "R",
    amtPurchase: 1234,
    purchaseAnalysisData: { RFN: "RFN-1" },
  });
});

test("refund 零、正数和 MIN_SAFE 金额均在 Linkly 请求前 fail closed", async () => {
  for (const cents of [0, 1234, Number.MIN_SAFE_INTEGER]) {
    const transport = new FakeTransport();
    const provider = new LinklyCloudBackendProvider(
      new LinklyCloudBackendApi(transport),
      providerOptions(),
    );

    await assert.rejects(
      () =>
        provider.refund(
          attempt({
            operation: "refund",
            amount: { currency: "AUD", cents },
            references: {
              checkoutId: null,
              paymentId: null,
              sessionId: null,
              txnRef: "TXN-1",
              rfn: "RFN-1",
              voucherReservationToken: null,
            },
          }),
        ),
      /LINKLY_AMOUNT_INVALID/,
    );
    assert.equal(transport.requests.length, 0);
  }
});

const cardTransaction = (overrides: Record<string, unknown> = {}) => ({
  txnRef: "TXN-1",
  rfn: "RFN-1",
  authCode: "AUTH-1",
  cardType: "VISA",
  maskedCardNumber: "411111******1234",
  merchantId: "MID-1",
  responseCode: "00",
  responseText: "APPROVED",
  stan: "STAN-1",
  bankDateTime: "2026-07-28T10:30:00+10:00",
  amountCents: 1234,
  ...overrides,
});

async function recoverFromSession(backendSession: unknown) {
  const transport = new FakeTransport();
  transport.responses.push(ok(backendSession), ok(backendSession));
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );
  const paymentAttempt = attempt({
    state: "Unknown",
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: "session-original",
      txnRef: "TXN-1",
      rfn: "RFN-ORIGINAL",
      voucherReservationToken: null,
    },
  });

  return {
    provider,
    result: await provider.recover(paymentAttempt),
    transport,
  };
}

test("未知 Linkly 状态失败关闭为 Unknown，并只保留原 SessionId 恢复", async () => {
  const recovered = await recoverFromSession(session({
    sessionId: "session-unexpected",
    status: "AwaitingSettlement",
    transactionSuccess: null,
  }));

  assert.equal(recovered.result.state, "Unknown");
  assert.equal(recovered.result.references.sessionId, "session-original");
  assert.equal(
    recovered.transport.requests[0]?.url,
    "/api/v1/linkly/cloud-backend/transactions/session-original/status",
  );

  const requestCountBeforeCancel = recovered.transport.requests.length;
  const cancelled = await recovered.provider.cancel(attempt({
    state: recovered.result.state,
    references: recovered.result.references,
  }));
  assert.equal(cancelled.state, "Unknown");
  assert.equal(recovered.transport.requests.length, requestCountBeforeCancel);
});

test("空白或缺失 Linkly 状态失败关闭为 Unknown", async () => {
  for (const backendSession of [
    session({ status: "   ", transactionSuccess: null }),
    { ...session({ status: "Pending", transactionSuccess: null }), status: undefined },
  ]) {
    const recovered = await recoverFromSession(backendSession);

    assert.equal(recovered.result.state, "Unknown");
    assert.equal(recovered.result.references.sessionId, "session-original");
  }
});

test("Linkly 状态与 transactionSuccess 语义冲突时失败关闭为 Unknown", async () => {
  for (const backendSession of [
    {
      ...session({
        status: "Cancelled",
        transactionSuccess: true,
      }),
      cardTransaction: cardTransaction({
        txnRef: "TXN-ORIGINAL",
        rfn: "RFN-ORIGINAL",
      }),
    },
    session({ status: "Pending", transactionSuccess: false }),
    session({ status: "Completed", transactionSuccess: null }),
    {
      ...session({
        status: "Failed",
        transactionSuccess: true,
      }),
      cardTransaction: cardTransaction({
        txnRef: "TXN-ORIGINAL",
        rfn: "RFN-ORIGINAL",
      }),
    },
  ]) {
    const recovered = await recoverFromSession(backendSession);

    assert.equal(recovered.result.state, "Unknown");
    assert.equal(recovered.result.references.sessionId, "session-original");
    assert.equal(recovered.result.protectedSyncEvidence, undefined);
  }
});

test("只有明确 Linkly 处理中状态且无最终结果时映射 Pending", async () => {
  for (const status of ["Pending", "TokenRefreshRequired"]) {
    const recovered = await recoverFromSession(session({
      sessionId: "session-original",
      status,
      transactionSuccess: null,
    }));

    assert.equal(recovered.result.state, "Pending");
  }
});

test("已批准购买只从结构化 cardTransaction 生成受保护同步证据", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    none(),
    ok({
      ...session({
        status: "Completed",
        transactionSuccess: true,
        responseCode: "00",
        receiptText: "RAW RECEIPT MUST NOT ENTER EVIDENCE",
        notifications: [{
          type: "transaction",
          payloadJson: JSON.stringify({
            Pan: "4111111111111234",
            AccessToken: "raw-token",
          }),
          receivedAt: "2026-07-28T00:30:00.000Z",
        }],
      }),
      cardTransaction: cardTransaction(),
    }),
  );
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );

  const result = await provider.submit(attempt());

  assert.equal(result.state, "Approved");
  assert.deepEqual(result.protectedSyncEvidence, {
    version: 1,
    provider: "linkly-cloud",
    operation: "purchase",
    processor: "ANZ",
    txnRef: "TXN-1",
    authCode: "AUTH-1",
    cardType: "VISA",
    cardBin: null,
    maskedCardNumber: "411111******1234",
    merchantId: "MID-1",
    responseCode: "00",
    responseText: "APPROVED",
    stan: "STAN-1",
    bankDateTimeIso: "2026-07-28T00:30:00.000Z",
    amountCents: 1234,
    refundReference: "RFN-1",
  });
  const protectedJson = JSON.stringify(result.protectedSyncEvidence);
  assert.doesNotMatch(protectedJson, /4111111111111234|raw-token|RAW RECEIPT|payloadJson|notifications/iu);
  assert.equal(result.references.rfn, "RFN-1");
});

test("已批准退款绑定原 RFN，金额证据保持正数且 operation 为 refund", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    none(),
    ok({
      ...session({
        status: "Completed",
        transactionSuccess: true,
        txnRef: "TXN-REFUND",
        responseCode: "00",
      }),
      cardTransaction: cardTransaction({
        txnRef: "TXN-REFUND",
        rfn: "RFN-ORIGINAL",
      }),
    }),
  );
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );

  const result = await provider.refund(attempt({
    operation: "refund",
    amount: { currency: "AUD", cents: -1234 },
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: null,
      txnRef: "TXN-ORIGINAL",
      rfn: "RFN-ORIGINAL",
      voucherReservationToken: null,
    },
  }));

  assert.equal(result.state, "Approved");
  assert.equal(result.protectedSyncEvidence?.operation, "refund");
  assert.equal(result.protectedSyncEvidence?.amountCents, 1234);
  assert.equal(result.protectedSyncEvidence?.txnRef, "TXN-REFUND");
  assert.equal(result.protectedSyncEvidence?.refundReference, "RFN-ORIGINAL");
  assert.equal(result.protectedSyncEvidence?.cardBin, null);
});

test("批准结果缺少或损坏 cardTransaction 时失败关闭为 Unknown", async () => {
  for (const unsafeCardTransaction of [
    null,
    cardTransaction({ amountCents: "1234" }),
    cardTransaction({ pan: "4111111111111234" }),
    cardTransaction({ payloadJson: "{\"Pan\":\"4111111111111234\"}" }),
  ]) {
    const transport = new FakeTransport();
    transport.responses.push(
      none(),
      ok({
        ...session({
          status: "Completed",
          transactionSuccess: true,
          responseCode: "00",
        }),
        cardTransaction: unsafeCardTransaction,
      }),
    );
    const provider = new LinklyCloudBackendProvider(
      new LinklyCloudBackendApi(transport),
      providerOptions(),
    );

    const result = await provider.submit(attempt());

    assert.equal(result.state, "Unknown");
    assert.match(result.responseCode ?? "", /^LINKLY_CARD_EVIDENCE_/u);
    assert.equal(result.protectedSyncEvidence, undefined);
  }
});

test("批准证据金额、SessionId、TxnRef 或退款 RFN 不一致时失败关闭", async () => {
  const cases: readonly Readonly<{
    paymentAttempt: PaymentAttempt;
    backendSession: LinklyCloudBackendSession;
    evidence: Record<string, unknown>;
  }>[] = [
    {
      paymentAttempt: attempt(),
      backendSession: session({ status: "Completed", transactionSuccess: true, responseCode: "00" }),
      evidence: cardTransaction({ amountCents: 999 }),
    },
    {
      paymentAttempt: attempt({
        state: "Unknown",
        references: {
          checkoutId: null,
          paymentId: null,
          sessionId: "session-expected",
          txnRef: "TXN-1",
          rfn: "RFN-1",
          voucherReservationToken: null,
        },
      }),
      backendSession: session({
        sessionId: "session-other",
        status: "Completed",
        transactionSuccess: true,
        responseCode: "00",
      }),
      evidence: cardTransaction(),
    },
    {
      paymentAttempt: attempt({
        state: "Unknown",
        references: {
          checkoutId: null,
          paymentId: null,
          sessionId: "session-1",
          txnRef: "TXN-EXPECTED",
          rfn: "RFN-1",
          voucherReservationToken: null,
        },
      }),
      backendSession: session({
        status: "Completed",
        transactionSuccess: true,
        responseCode: "00",
        txnRef: "TXN-OTHER",
      }),
      evidence: cardTransaction({ txnRef: "TXN-OTHER" }),
    },
    {
      paymentAttempt: attempt({
        operation: "refund",
        amount: { currency: "AUD", cents: -1234 },
        state: "Unknown",
        references: {
          checkoutId: null,
          paymentId: null,
          sessionId: "session-1",
          txnRef: "TXN-REFUND",
          rfn: "RFN-EXPECTED",
          voucherReservationToken: null,
        },
      }),
      backendSession: session({
        status: "Completed",
        transactionSuccess: true,
        responseCode: "00",
        txnRef: "TXN-REFUND",
      }),
      evidence: cardTransaction({
        txnRef: "TXN-REFUND",
        rfn: "RFN-OTHER",
      }),
    },
  ];

  for (const item of cases) {
    const transport = new FakeTransport();
    transport.responses.push(ok({
      ...item.backendSession,
      cardTransaction: item.evidence,
    }), ok({
      ...item.backendSession,
      cardTransaction: item.evidence,
    }));
    if (item.paymentAttempt.references.sessionId === null) {
      transport.responses.unshift(none());
    }
    const provider = new LinklyCloudBackendProvider(
      new LinklyCloudBackendApi(transport),
      providerOptions(),
    );

    const result = item.paymentAttempt.operation === "refund"
      ? await provider.refund(item.paymentAttempt)
      : await provider.submit(item.paymentAttempt);

    assert.equal(result.state, "Unknown");
    assert.ok(
      ["LINKLY_CARD_EVIDENCE_MISMATCH", "LINKLY_RECOVERY_CONTEXT_MISMATCH"].includes(
        result.responseCode ?? "",
      ) || (result.responseCode ?? "").startsWith("LINKLY_APPROVED_AMOUNT_MISMATCH:"),
    );
    assert.equal(result.protectedSyncEvidence, undefined);
  }
});

test("recover 只有在 Session/Txn/金额证据完全一致时恢复 Approved", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok({
    ...session({
      status: "Completed",
      transactionSuccess: true,
      responseCode: "00",
    }),
    cardTransaction: cardTransaction(),
  }));
  const provider = new LinklyCloudBackendProvider(
    new LinklyCloudBackendApi(transport),
    providerOptions(),
  );

  const result = await provider.recover(attempt({
    state: "Unknown",
    references: {
      checkoutId: null,
      paymentId: null,
      sessionId: "session-1",
      txnRef: "TXN-1",
      rfn: "RFN-1",
      voucherReservationToken: null,
    },
  }));

  assert.equal(result.state, "Approved");
  assert.equal(result.protectedSyncEvidence?.txnRef, "TXN-1");
  assert.equal(result.protectedSyncEvidence?.refundReference, "RFN-1");
  assert.equal(
    transport.requests[0]?.url,
    "/api/v1/linkly/cloud-backend/transactions/session-1/status",
  );
});

test("Pending、Declined 和 Cancelled 即使 DTO 存在也不携带 evidence", async () => {
  for (const backendSession of [
    session({ status: "Pending", transactionSuccess: null }),
    session({ status: "Completed", transactionSuccess: false, responseCode: "05" }),
    session({ status: "Cancelled", transactionSuccess: false }),
  ]) {
    const transport = new FakeTransport();
    transport.responses.push(ok({
      ...backendSession,
      cardTransaction: cardTransaction(),
    }));
    const provider = new LinklyCloudBackendProvider(
      new LinklyCloudBackendApi(transport),
      providerOptions(),
    );

    const result = await provider.recover(attempt({
      state: "Unknown",
      references: {
        checkoutId: null,
        paymentId: null,
        sessionId: "session-1",
        txnRef: "TXN-1",
        rfn: "RFN-1",
        voucherReservationToken: null,
      },
    }));

    assert.notEqual(result.state, "Approved");
    assert.equal(result.protectedSyncEvidence, undefined);
  }
});

test("Linkly 批准必须使用 WPF 批准码且顶层与 cardTransaction 证据一致", async () => {
  for (const approvalCode of ["00", "08", "11"]) {
    const transport = new FakeTransport();
    transport.responses.push(
      none(),
      ok({
        ...session({
          status: "Completed",
          transactionSuccess: true,
          responseCode: approvalCode,
          responseText: "APPROVED",
        }),
        cardTransaction: cardTransaction({ responseCode: approvalCode, responseText: "APPROVED" }),
      }),
    );
    const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
    const result = await provider.submit(attempt());
    assert.equal(result.state, "Approved");
  }

  for (const conflicting of [
    { responseCode: "05", cardCode: "00", responseText: "DECLINED", cardText: "APPROVED" },
    { responseCode: "00", cardCode: "05", responseText: "APPROVED", cardText: "DECLINED" },
    { responseCode: "99", cardCode: "99", responseText: "APPROVED", cardText: "APPROVED" },
  ]) {
    const transport = new FakeTransport();
    transport.responses.push(
      none(),
      ok({
        ...session({
          status: "Completed",
          transactionSuccess: true,
          responseCode: conflicting.responseCode,
          responseText: conflicting.responseText,
        }),
        cardTransaction: cardTransaction({
          responseCode: conflicting.cardCode,
          responseText: conflicting.cardText,
        }),
      }),
    );
    const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
    const result = await provider.submit(attempt());
    assert.equal(result.state, "Unknown");
    assert.equal(result.protectedSyncEvidence, undefined);
  }
});

test("cancel 先读取同 Session/环境的最新状态；只有当前可取消上下文才发送 CANCEL", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    ok(session({ status: "Pending", transactionSuccess: null, cancelKeyFlag: false, okKeyFlag: true })),
    ok(session({ status: "Pending", transactionSuccess: null, cancelKeyFlag: true })),
    ok(session({ status: "Cancelled", transactionSuccess: false })),
  );
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  const references = { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: "RFN-1", voucherReservationToken: null };

  const okOnly = await provider.cancel(attempt({ state: "Pending", references }));
  assert.equal(okOnly.state, "Unknown");
  assert.equal(transport.requests.length, 1);

  const cancelled = await provider.cancel(attempt({ state: "Pending", references }));
  assert.equal(cancelled.state, "Cancelled");
  assert.deepEqual(transport.requests.slice(1).map((request) => request.url), [
    "/api/v1/linkly/cloud-backend/transactions/session-1/status",
    "/api/v1/linkly/cloud-backend/transactions/session-1/sendkey",
  ]);
});

test("cancel display flag 对齐 WPF 的布尔/数字/字符串值；非法最新 display 失败关闭", async () => {
  for (const flag of [true, 1, "yes", "TRUE", "1"] as const) {
    const transport = new FakeTransport();
    transport.responses.push(
      ok(session({
        status: "Pending",
        notifications: [{
          type: "display",
          payloadJson: JSON.stringify({ CancelKeyFlag: flag }),
          receivedAt: "2026-09-09T00:00:00.000Z",
        }],
      })),
      ok(session({ status: "Cancelled", transactionSuccess: false })),
    );
    const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
    const result = await provider.cancel(attempt({ state: "Pending", references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null } }));
    assert.equal(result.state, "Cancelled");
    assert.equal(transport.requests.length, 2);
  }

  const invalidTransport = new FakeTransport();
  invalidTransport.responses.push(ok(session({
    status: "Pending",
    cancelKeyFlag: true,
    notifications: [{
      type: "display",
      payloadJson: JSON.stringify({ CancelKeyFlag: "maybe" }),
      receivedAt: "2026-09-09T00:00:00.000Z",
    }],
  })));
  const invalidProvider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(invalidTransport), providerOptions());
  const invalid = await invalidProvider.cancel(attempt({ state: "Pending", references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null } }));
  assert.equal(invalid.state, "Unknown");
  assert.equal(invalidTransport.requests.length, 1);
});

test("cancel 对已终态只返回状态，不发送 sendkey；状态操作使用 240 秒预算", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok(session({ status: "Completed", transactionSuccess: false, responseCode: "05" })));
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  const result = await provider.cancel(attempt({ state: "Pending", references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null } }));
  assert.equal(result.state, "Declined");
  assert.equal(transport.requests.length, 1);
  assert.equal(transport.requests[0]?.timeoutMs, 240_000);
});

test("recover 持久 SessionId 先 status；Pending 无 recoveryAction 不调用 recover", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok(session({ status: "Pending", transactionSuccess: null, recoveryAction: null })));
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  const result = await provider.recover(attempt({ state: "Unknown", references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null } }));
  assert.equal(result.state, "Pending");
  assert.deepEqual(transport.requests.map((request) => request.url), [
    "/api/v1/linkly/cloud-backend/transactions/session-1/status",
  ]);
});

test("recoverWithControl 为每个 status/recover 请求重算 180 秒剩余预算", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    ok(session({ status: "Pending", transactionSuccess: null, recoveryAction: "Retry" })),
    ok(session({ status: "Completed", transactionSuccess: false, responseCode: "05" })),
  );
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  const result = await provider.recoverWithControl(
    attempt({
      state: "Unknown",
      references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null },
    }),
    { signal: new AbortController().signal, deadlineAtMs: Date.now() + 180_000 },
  );
  assert.equal(result.state, "Declined");
  const timeouts = transport.requests.map((request) => request.timeoutMs ?? 0);
  assert.equal(timeouts.length, 2);
  assert.ok(timeouts[0]! <= 180_000 && timeouts[0]! > 170_000);
  assert.ok(timeouts[1]! <= timeouts[0]!);
});

test("recoverWithControl 截止或 abort 后保留 Unknown 且不查询终端", async () => {
  const transport = new FakeTransport();
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  const controller = new AbortController();
  controller.abort();
  const result = await provider.recoverWithControl(
    attempt({ state: "Pending", references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null } }),
    { signal: controller.signal, deadlineAtMs: Date.now() - 1 },
  );
  assert.equal(result.state, "Unknown");
  assert.equal(result.responseCode, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
  assert.equal(transport.requests.length, 0);
});

test("历史 attempt 缺少 providerEnvironment 时，recover/cancel 必须先 reconciliation，不猜当前环境或发变更请求", async () => {
  const transport = new FakeTransport();
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  const references = { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null };

  const recovered = await provider.recover(attempt({
    state: "Unknown",
    providerEnvironment: null,
    references,
  }));
  const cancelled = await provider.cancel(attempt({
    state: "Pending",
    providerEnvironment: null,
    references,
  }));

  assert.equal(recovered.state, "Unknown");
  assert.equal(recovered.responseCode, "LINKLY_RECOVERY_ENVIRONMENT_REQUIRED");
  assert.equal(cancelled.state, "Unknown");
  assert.equal(cancelled.responseCode, "LINKLY_CANCEL_ENVIRONMENT_REQUIRED");
  assert.equal(transport.requests.length, 0);
});

test("acknowledge 只调用原 SessionId，且校验冻结环境、SessionId、时间戳和终态", async () => {
  const transport = new FakeTransport();
  transport.responses.push(ok(session({
    environment: "Sandbox",
    status: "Completed",
    transactionSuccess: false,
    responseCode: "05",
    clientAcknowledgedAt: "2026-09-09T00:00:00.000Z",
  })));
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  await provider.acknowledge({
    ...attempt({ state: "Declined", references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null } }),
    providerEnvironment: "Sandbox",
  } as PaymentAttempt & { providerEnvironment: string });
  assert.equal(transport.requests.length, 1);
  assert.equal(transport.requests[0]?.url, "/api/v1/linkly/cloud-backend/transactions/session-1/acknowledge");
});

test("legacy 环境只在当前环境强匹配 SessionId、UID、类型和金额后返回可冻结环境", async () => {
  const transport = new FakeTransport();
  const candidate = session({
    sessionId: "session-1",
    txnRef: "TXN-1",
    status: "Completed",
    transactionSuccess: false,
    responseCode: "05",
    clientAcknowledgedAt: "2026-09-09T00:00:00.000Z",
    notifications: [transactionNotification({ uid: recoveryUid, txnRef: "TXN-1" })],
  });
  transport.responses.push(ok(candidate), none(), none());
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  const reconciled = await provider.reconcileLegacy(attempt({
    idempotencyKey: recoveryUid,
    providerEnvironment: null,
    references: { checkoutId: null, paymentId: null, sessionId: "session-1", txnRef: "TXN-1", rfn: null, voucherReservationToken: null },
  }));
  assert.deepEqual(reconciled, {
    environment: "Sandbox",
    clientAcknowledgedAt: "2026-09-09T00:00:00.000Z",
  });
  assert.equal(transport.requests.some((request) => request.method === "POST"), false);
});

test("冷启动 ACK probe 只发现当前环境 active/resumable 的未 ACK 且 UID 强匹配 Session", async () => {
  const transport = new FakeTransport();
  transport.responses.push(
    ok(session({ sessionId: "session-declined", status: "Completed", transactionSuccess: false, responseCode: "05", notifications: [transactionNotification({ uid: recoveryUid, txnRef: "TXN-1" })] })),
    ok(session({ environment: "Production", sessionId: "session-wrong-env", status: "Completed", transactionSuccess: false, responseCode: "05" })),
  );
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  assert.deepEqual(await provider.listUnacknowledgedSessions(), [
    { sessionId: "session-declined", environment: "Sandbox", idempotencyKey: recoveryUid },
  ]);
  assert.deepEqual(transport.requests.map((request) => ({ method: request.method, timeoutMs: request.timeoutMs })), [
    { method: "GET", timeoutMs: 240_000 },
    { method: "GET", timeoutMs: 240_000 },
  ]);
  assert.equal(transport.requests.some((request) => request.method === "POST"), false);

  transport.responses.push(
    ok(session({ sessionId: "session-duplicate", status: "Pending", transactionSuccess: null, notifications: [transactionNotification({ uid: recoveryUid, txnRef: "TXN-1" })] })),
    ok(session({ sessionId: "session-duplicate", status: "Pending", transactionSuccess: null, notifications: [transactionNotification({ uid: recoveryUid, txnRef: "TXN-1" })] })),
  );
  assert.deepEqual(await provider.listUnacknowledgedSessions(), [
    { sessionId: "session-duplicate", environment: "Sandbox", idempotencyKey: recoveryUid },
  ]);

  transport.responses.push(
    ok(session({ sessionId: "session-conflict", status: "Pending", transactionSuccess: null, notifications: [
      transactionNotification({ uid: recoveryUid, txnRef: "TXN-1" }),
      transactionNotification({ uid: "9b241101-e2bb-4255-8caf-4136c566a962", txnRef: "TXN-1" }),
    ] })),
    none(),
  );
  assert.deepEqual(await provider.listUnacknowledgedSessions(), []);
});

// ---------------------------------------------------------------------------
// 对齐 WPF #260 / 审查 H4、H5、H6、M18：TxnRef 派生认领、Failed/NotSubmitted 终态、
// 提交前失败的“未提交”分类，以及受控恢复中的传输异常。
// ---------------------------------------------------------------------------

/** 与 test-fixtures/linkly-attempt-txnref/vectors.json 中该 GUID 的黄金向量一致（C# 与 TS 共用），非自反推导。 */
const derivedAttemptId = "3b241101-e2bb-4255-8caf-4136c566a962";
const derivedPurchaseTxnRef = "PYYM521JPGH9ARTN";
const derivedRefundTxnRef = "RA80AJ7EBT50EKH8";

const noReferences = { checkoutId: null, paymentId: null, sessionId: null, txnRef: null, rfn: null, voucherReservationToken: null };

/** 服务在 Created→Submitted 的 CAS 里已预置派生 TxnRef 的 attempt。 */
const derivedAttempt = (overrides: Partial<PaymentAttempt> = {}): PaymentAttempt =>
  attempt({
    attemptId: derivedAttemptId,
    idempotencyKey: recoveryUid,
    state: "Submitted",
    references: { ...noReferences, txnRef: derivedPurchaseTxnRef },
    ...overrides,
  });

const posts = (transport: FakeTransport) =>
  transport.requests.filter((request) => request.method === "POST" && request.url === "/api/v1/linkly/cloud-backend/transactions");

test("H4：落库的 TxnRef 确为 attemptId 派生值时请求携带 attemptGuid；否则不带", async () => {
  const transport = new FakeTransport();
  transport.responses.push(none(), ok(session({ txnRef: derivedPurchaseTxnRef, status: "Pending" })));
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  await provider.submit(derivedAttempt({ state: "Created" }));
  assert.equal((posts(transport)[0]?.data as { attemptGuid?: unknown }).attemptGuid, derivedAttemptId);

  // 大小写不同的 attemptId 仍发送规范化（小写 D 格式）GUID。
  const upperTransport = new FakeTransport();
  upperTransport.responses.push(none(), ok(session({ txnRef: derivedPurchaseTxnRef, status: "Pending" })));
  await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(upperTransport), providerOptions()).submit(
    derivedAttempt({ state: "Created", attemptId: derivedAttemptId.toUpperCase() }),
  );
  assert.equal((posts(upperTransport)[0]?.data as { attemptGuid?: unknown }).attemptGuid, derivedAttemptId);

  // 本地没有派生引用（旧 attempt）或引用与 attemptId 不对应时绝不发送，避免后端派生出与本地不一致的值。
  for (const legacy of [
    attempt({ attemptId: derivedAttemptId }),
    attempt({ attemptId: "attempt-1", references: { ...noReferences, txnRef: derivedPurchaseTxnRef } }),
    derivedAttempt({ state: "Created", references: { ...noReferences, txnRef: "PAAAAAAAAAAAAAAA" } }),
  ]) {
    const legacyTransport = new FakeTransport();
    legacyTransport.responses.push(none(), ok(session({ status: "Pending" })));
    await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(legacyTransport), providerOptions()).submit(legacy);
    assert.equal("attemptGuid" in (posts(legacyTransport)[0]?.data as object), false);
  }

  // 退款用 R 类型派生值。
  const refundTransport = new FakeTransport();
  refundTransport.responses.push(none(), ok(session({ txnRef: derivedRefundTxnRef, status: "Pending" })));
  await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(refundTransport), providerOptions()).refund(
    derivedAttempt({
      state: "Created",
      operation: "refund",
      amount: { currency: "AUD", cents: -1234 },
      references: { ...noReferences, txnRef: derivedRefundTxnRef, rfn: "RFN-ORIGINAL" },
    }),
  );
  assert.equal((posts(refundTransport)[0]?.data as { attemptGuid?: unknown }).attemptGuid, derivedAttemptId);
});

test("H4：create 响应丢失后，服务端剥离了终态通知也能凭顶层 TxnRef 认领 Declined 会话并绑定 SessionId", async () => {
  const declined = session({
    sessionId: "orphan-declined",
    txnRef: derivedPurchaseTxnRef,
    status: "Completed",
    transactionSuccess: false,
    responseCode: "05",
    notifications: [], // 后端对 Declined/Cancelled 会话整体剥离 transaction 通知
  });
  const transport = new FakeTransport();
  transport.responses.push(
    none(),
    new HbposApiError("connection lost after submit", { kind: "transport" }),
    none(), // active 已无（会话已终态）
    ok(declined), // resumable
    ok(declined), // status
  );
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());

  const result = await provider.submit(derivedAttempt({ state: "Created" }));

  assert.equal(result.state, "Declined");
  assert.equal(result.references.sessionId, "orphan-declined");
  assert.equal(result.references.txnRef, derivedPurchaseTxnRef);
  assert.equal(posts(transport).length, 1, "歧义恢复绝不重发 create");
});

test("H4：Approved 会话的通知 TxnRef 被二次脱敏时仍凭顶层 TxnRef + 证据金额认领", async () => {
  const approved = {
    ...session({
      sessionId: "orphan-approved",
      txnRef: derivedPurchaseTxnRef,
      status: "Completed",
      transactionSuccess: true,
      responseCode: "00",
      notifications: [{
        type: "transaction",
        payloadJson: JSON.stringify({
          Response: {
            TxnType: "P",
            AmtPurchase: 1234,
            // 后端输出路径把 12 位数字串当 PAN 掩码：与顶层 txnRef 永远对不上。
            TxnRef: "****3456AB12",
            PurchaseAnalysisData: { UID: recoveryUid },
          },
        }),
        receivedAt: "2026-07-28T00:00:05.000Z",
      }],
    }),
    cardTransaction: cardTransaction({ txnRef: derivedPurchaseTxnRef, rfn: "RFN-ORPHAN" }),
  };
  const transport = new FakeTransport();
  transport.responses.push(ok(approved), ok(approved));
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());

  const result = await provider.recover(derivedAttempt({ state: "Unknown" }));

  assert.equal(result.state, "Approved");
  assert.equal(result.references.sessionId, "orphan-approved");
  assert.equal(result.protectedSyncEvidence?.amountCents, 1234);
});

test("H4：Pending 且尚无通知的 active 会话同样按顶层 TxnRef 认领；TxnRef、类型、金额、UID 任一矛盾则拒绝", async () => {
  const pending = session({ sessionId: "orphan-pending", txnRef: derivedPurchaseTxnRef, status: "Pending", notifications: [] });
  const transport = new FakeTransport();
  transport.responses.push(ok(pending), ok(pending));
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
  const claimed = await provider.recover(derivedAttempt({ state: "Unknown" }));
  assert.equal(claimed.state, "Pending");
  assert.equal(claimed.references.sessionId, "orphan-pending");

  const rejected = [
    session({ sessionId: "other", txnRef: "PZZZZZZZZZZZZZZZ", status: "Pending" }),
    session({ sessionId: "wrong-type", txnRef: derivedPurchaseTxnRef, status: "Pending", notifications: [transactionNotification({ uid: recoveryUid, txnType: "R" })] }),
    session({ sessionId: "wrong-amount", txnRef: derivedPurchaseTxnRef, status: "Pending", notifications: [transactionNotification({ uid: recoveryUid, amountCents: 999 })] }),
    session({ sessionId: "wrong-uid", txnRef: derivedPurchaseTxnRef, status: "Pending", notifications: [transactionNotification({ uid: "89bd9d8d-69a2-4803-8f8d-830ec69be3a0" })] }),
  ];
  for (const candidate of rejected) {
    const rejectTransport = new FakeTransport();
    rejectTransport.responses.push(ok(candidate), ok(candidate));
    const result = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(rejectTransport), providerOptions())
      .recover(derivedAttempt({ state: "Unknown" }));
    assert.equal(result.state, "Unknown", candidate.sessionId);
    assert.equal(result.references.sessionId, null, candidate.sessionId);
    assert.equal(rejectTransport.requests.some((request) => request.url.includes("/status") || request.url.includes("/recover")), false, candidate.sessionId);
  }
});

test("H4：没有派生 TxnRef 的旧 attempt 仍走 UID 通知认领，通知被剥离时失败关闭", async () => {
  const stripped = session({ sessionId: "legacy-orphan", txnRef: "TXN-LEGACY", status: "Completed", transactionSuccess: false, notifications: [] });
  const transport = new FakeTransport();
  transport.responses.push(ok(stripped), ok(stripped));
  const result = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions())
    .recover(attempt({ idempotencyKey: recoveryUid, state: "Unknown" }));
  assert.equal(result.state, "Unknown");
  assert.equal(result.references.sessionId, null);
});

test("H5：Failed/NotSubmitted 且 transactionSuccess 为 null 映射为 Declined，可进入 ACK；矛盾字段仍失败关闭", async () => {
  for (const status of ["Failed", "NotSubmitted", "failed", "notsubmitted"]) {
    for (const recoveryAction of [null, "RetryNewSession"]) {
      const recovered = await recoverFromSession(session({
        sessionId: "session-original",
        txnRef: "TXN-1",
        status,
        transactionSuccess: null,
        recoveryAction,
        lastHttpStatus: status.toLowerCase() === "notsubmitted" ? 404 : 400,
      }));
      assert.equal(recovered.result.state, "Declined", `${status}/${recoveryAction}`);
      assert.equal(recovered.result.references.sessionId, "session-original");
      assert.equal(recovered.result.protectedSyncEvidence, undefined);
    }
  }
  // 明确 success=false 保持 Declined；success=true 与 Failed 互相矛盾，不得放行也不得判拒绝。
  assert.equal((await recoverFromSession(session({ sessionId: "session-original", status: "Failed", transactionSuccess: false }))).result.state, "Declined");
  assert.equal((await recoverFromSession(session({ sessionId: "session-original", status: "NotSubmitted", transactionSuccess: true }))).result.state, "Unknown");
});

test("H6：create 之前读终端列表失败（超时、401、凭据轮换 409、响应非法）返回未提交 Declined，零 POST、无 SessionId", async () => {
  const failures: readonly unknown[] = [
    new HbposApiError("timeout", { kind: "transport", code: "NO_HTTP_RESPONSE" }),
    new HbposApiError("unauthorized", { kind: "http", status: 401 }),
    new HbposApiError("credential rotated", { kind: "http", status: 409, code: "LINKLY_CREDENTIAL_REENTRY_REQUIRED" }),
    new Error("Invalid Linkly terminal selection response."),
  ];
  for (const error of failures) {
    const transport = new FakeTransport();
    const selection = new FakeTerminalSelectionPort();
    selection.readTerminals = async () => { throw error; };
    const result = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions(selection))
      .submit(attempt({ state: "Submitted" }));
    assert.equal(result.state, "Declined");
    assert.equal(result.responseCode, "LINKLY_NOT_SUBMITTED_TERMINAL_LIST");
    assert.equal(result.references.sessionId, null);
    assert.equal(transport.requests.length, 0);
  }
});

test("H6：create 之前 active 预检失败返回未提交 Declined，不 create、不进入歧义恢复；退款同样", async () => {
  for (const operation of ["purchase", "refund"] as const) {
    const transport = new FakeTransport();
    transport.responses.push(new HbposApiError("gateway timeout", { kind: "transport", code: "NO_HTTP_RESPONSE" }));
    const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions());
    const result = operation === "purchase"
      ? await provider.submit(attempt({ state: "Submitted" }))
      : await provider.refund(attempt({
          state: "Submitted",
          operation: "refund",
          amount: { currency: "AUD", cents: -1234 },
          references: { ...noReferences, rfn: "RFN-1" },
        }));
    assert.equal(result.state, "Declined");
    assert.equal(result.responseCode, "LINKLY_NOT_SUBMITTED_ACTIVE_CHECK");
    assert.equal(transport.requests.length, 1);
    assert.equal(posts(transport).length, 0);
  }
});

test("H6：create 被 400/401/403/404/409/422/429 明确拒绝时返回带状态码的未提交 Declined，不查询 active/resumable", async () => {
  for (const status of [400, 401, 403, 404, 409, 422, 429]) {
    const transport = new FakeTransport();
    transport.responses.push(none(), new HbposApiError(`rejected ${status}`, { kind: "http", status, code: "SOME_SERVER_CODE" }));
    const result = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions())
      .submit(derivedAttempt());
    assert.equal(result.state, "Declined", String(status));
    assert.equal(result.responseCode, `LINKLY_NOT_SUBMITTED_HTTP_${status}`);
    assert.equal(result.references.sessionId, null);
    // 本地派生的 TxnRef 仍保留在引用里（未提交的 attempt 以 Declined 终结，不影响重试新 attempt）。
    assert.equal(result.references.txnRef, derivedPurchaseTxnRef);
    assert.equal(posts(transport).length, 1);
    assert.equal(transport.requests.length, 2);
  }
});

test("H6：只有 408、5xx、传输错误与无法解析的响应才进入结果不确定恢复，且绝不重发 POST", async () => {
  const ambiguous: readonly unknown[] = [
    new HbposApiError("request timeout", { kind: "http", status: 408 }),
    new HbposApiError("server error", { kind: "http", status: 500 }),
    new HbposApiError("bad gateway", { kind: "http", status: 503 }),
    new HbposApiError("connection lost", { kind: "transport", code: "NO_HTTP_RESPONSE" }),
    // 200 但响应体缺字段：POST 很可能已落地，必须按歧义处理而不是当作未提交。
    { status: 200, data: { success: true, data: {} } },
  ];
  for (const failure of ambiguous) {
    const transport = new FakeTransport();
    transport.responses.push(none(), failure, none(), none());
    const result = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions())
      .submit(derivedAttempt());
    assert.equal(result.state, "Unknown");
    assert.equal(result.responseCode, "LINKLY_SESSION_UNRESOLVED");
    assert.equal(posts(transport).length, 1);
    assert.deepEqual(
      transport.requests.slice(2).map((request) => request.url),
      ["/api/v1/linkly/cloud-backend/transactions/active", "/api/v1/linkly/cloud-backend/transactions/resumable"],
    );
  }
});

const pendingReferences = { ...noReferences, sessionId: "session-1", txnRef: "TXN-1" };
const freshControl = () => ({ signal: new AbortController().signal, deadlineAtMs: Date.now() + 180_000 });

test("M18：请求进行中被 abort 时返回中性码，而不是抛出后被写成 Unknown", async () => {
  const controller = new AbortController();
  const hangingTransport: HbposTransport = {
    request: (request) => new Promise((_resolve, reject) => {
      request.signal?.addEventListener("abort", () => {
        reject(new HbposApiError("Hbpos request was cancelled.", { kind: "transport", code: "REQUEST_ABORTED" }));
      });
    }),
  };
  const provider = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(hangingTransport), providerOptions());
  const running = provider.recoverWithControl(
    attempt({ state: "Pending", references: pendingReferences }),
    { signal: controller.signal, deadlineAtMs: Date.now() + 180_000 },
  );
  controller.abort();
  const aborted = await running;
  assert.equal(aborted.state, "Unknown");
  assert.equal(aborted.responseCode, "LINKLY_RECOVERY_ABORTED");
  assert.equal(aborted.references.sessionId, "session-1");

  // 无会话的歧义恢复同样把 abort 转成中性码。
  const ambiguousController = new AbortController();
  const ambiguousRunning = new LinklyCloudBackendProvider(new LinklyCloudBackendApi(hangingTransport), providerOptions())
    .recoverWithControl(
      derivedAttempt({ state: "Submitted" }),
      { signal: ambiguousController.signal, deadlineAtMs: Date.now() + 180_000 },
    );
  ambiguousController.abort();
  assert.equal((await ambiguousRunning).responseCode, "LINKLY_RECOVERY_ABORTED");
});

test("M18：单次请求在截止时间到期时超时，返回截止中性码", async () => {
  const slowFailure: HbposTransport = {
    request: () => new Promise((_resolve, reject) => {
      setTimeout(() => reject(new HbposApiError("timeout", { kind: "transport", code: "NO_HTTP_RESPONSE" })), 30);
    }),
  };
  const result = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(slowFailure), providerOptions())
    .recoverWithControl(
      attempt({ state: "Pending", references: pendingReferences }),
      { signal: new AbortController().signal, deadlineAtMs: Date.now() + 10 },
    );
  assert.equal(result.state, "Unknown");
  assert.equal(result.responseCode, "LINKLY_RECOVERY_DEADLINE_EXCEEDED");
});

test("M18：Pending 的瞬时传输错误/408/429/5xx 保持 Pending，Unknown 保持 Unknown；非瞬时错误仍抛出", async () => {
  const transient: readonly unknown[] = [
    new HbposApiError("offline", { kind: "transport", code: "NO_HTTP_RESPONSE" }),
    new HbposApiError("timeout", { kind: "http", status: 408 }),
    new HbposApiError("limited", { kind: "http", status: 429 }),
    new HbposApiError("bad gateway", { kind: "http", status: 502 }),
  ];
  for (const failure of transient) {
    const transport = new FakeTransport();
    transport.responses.push(failure);
    const pending = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(transport), providerOptions())
      .recoverWithControl(attempt({ state: "Pending", references: pendingReferences }), freshControl());
    assert.equal(pending.state, "Pending");
    assert.equal(pending.references.sessionId, "session-1");

    const unknownTransport = new FakeTransport();
    unknownTransport.responses.push(failure);
    const stillUnknown = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(unknownTransport), providerOptions())
      .recoverWithControl(attempt({ state: "Unknown", references: pendingReferences }), freshControl());
    assert.equal(stillUnknown.state, "Unknown");
  }

  // recoveryAction 触发的 POST recover 失败同样保持 Pending。
  const recoverFails = new FakeTransport();
  recoverFails.responses.push(
    ok(session({ status: "Pending", recoveryAction: "Retry" })),
    new HbposApiError("bad gateway", { kind: "http", status: 502 }),
  );
  const afterRecover = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(recoverFails), providerOptions())
    .recoverWithControl(attempt({ state: "Pending", references: pendingReferences }), freshControl());
  assert.equal(afterRecover.state, "Pending");

  const forbidden = new FakeTransport();
  forbidden.responses.push(new HbposApiError("forbidden", { kind: "http", status: 403 }));
  await assert.rejects(
    () => new LinklyCloudBackendProvider(new LinklyCloudBackendApi(forbidden), providerOptions())
      .recoverWithControl(attempt({ state: "Pending", references: pendingReferences }), freshControl()),
    HbposApiError,
  );
});

test("M18：取消键被 Linkly 拒绝（sendkey 400）或取消途中传输异常时保持 Pending，不禁用取消键", async () => {
  const cancellable = session({ status: "Pending", transactionSuccess: null, cancelKeyFlag: true, txnRef: "TXN-1" });
  const rejected = new FakeTransport();
  rejected.responses.push(
    ok(cancellable),
    new HbposApiError("Continue waiting for the transaction result", { kind: "http", status: 400 }),
  );
  const result = await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(rejected), providerOptions())
    .cancel(attempt({ state: "Pending", references: pendingReferences }));
  assert.equal(result.state, "Pending");
  assert.equal(result.responseCode, "LINKLY_CANCEL_NOT_ACCEPTED");
  assert.equal(result.references.sessionId, "session-1");

  const sendKeyDown = new FakeTransport();
  sendKeyDown.responses.push(ok(cancellable), new HbposApiError("offline", { kind: "transport", code: "NO_HTTP_RESPONSE" }));
  assert.equal((await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(sendKeyDown), providerOptions())
    .cancel(attempt({ state: "Pending", references: pendingReferences }))).state, "Pending");

  const statusDown = new FakeTransport();
  statusDown.responses.push(new HbposApiError("bad gateway", { kind: "http", status: 502 }));
  assert.equal((await new LinklyCloudBackendProvider(new LinklyCloudBackendApi(statusDown), providerOptions())
    .cancel(attempt({ state: "Pending", references: pendingReferences }))).state, "Pending");
});
