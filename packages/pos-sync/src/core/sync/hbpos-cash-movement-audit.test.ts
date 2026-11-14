import assert from "node:assert/strict";
import test from "node:test";

import type { HbposTransport, HbposTransportRequest } from "@hb/pos-api-client/transport";
import { buildCashMovement } from "@hb/pos-domain/core/contracts/cash-movement";
import type { OrderRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";

import { HbposAuditBatchAdapter } from "./hbpos-sync-adapters";

/** 现金进出审计（CASH_IN / CASH_OUT / CASH_MOVEMENT_VOID）必须在上传白名单内，且映射出带符号的 amountDelta。 */

const GUID_A = "11111111-1111-4111-8111-111111111111";
const GUID_B = "22222222-2222-4222-8222-222222222222";
const AUDIT_A = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const AUDIT_B = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const AUDIT_C = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
const SCOPE = { storeCode: "S001", deviceCode: "IPAD-1" } as const;

type Dto = {
  eventId: string;
  operationType: string;
  outcome: string;
  cashierId: string | null;
  orderGuid: string | null;
  correlationId: string;
  amountDelta?: number | null;
  properties: Record<string, string> | null;
  items: unknown[];
};

async function upload(events: Parameters<HbposAuditBatchAdapter["upload"]>[0]) {
  const requests: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(input: HbposTransportRequest) {
      requests.push(input);
      const body = input.data as { events: { eventId: string }[] };
      return {
        status: 200,
        data: { results: body.events.map((event) => ({ eventId: event.eventId, status: "accepted" })) } as T,
      };
    },
  };
  const adapter = new HbposAuditBatchAdapter(
    transport,
    {} as unknown as OrderRepositoryPort,
    { ...SCOPE, appVersion: "1.0.0", instanceId: "install-1" },
  );
  const result = await adapter.upload(events);
  return { result, body: (requests[0]?.data as { events: Dto[] } | undefined)?.events ?? [] };
}

function commit(
  guid: string,
  auditId: string,
  entry: Parameters<typeof buildCashMovement>[0]["entry"],
  note: string | null = null,
) {
  return buildCashMovement({
    movementGuid: guid,
    auditEventId: auditId,
    entry,
    note,
    occurredAtIso: "2026-11-14T02:00:00.000Z",
    businessTimeZone: "Australia/Brisbane",
    ...SCOPE,
    cashier: { cashierId: "c1", cashierName: "Alice", userGuid: GUID_B },
  });
}

test("CASH_IN / CASH_OUT / CASH_MOVEMENT_VOID 可上传：类型保留、金额带符号、属性只含安全键、orderGuid 为空", async () => {
  const cashIn = commit(GUID_A, AUDIT_A, { kind: "movement", reasonCode: "ChangeTopUp", amountCents: 5_025 });
  const cashOut = commit(GUID_B, AUDIT_B, { kind: "movement", reasonCode: "PaidOut", amountCents: 1_250 }, "milk");
  const reversal = commit(
    "33333333-3333-4333-8333-333333333333",
    AUDIT_C,
    { kind: "void", original: { ...cashOut.movement } },
    "wrong amount",
  );
  const { result, body } = await upload(
    [cashIn, cashOut, reversal].map(({ audit }) => ({ ...audit, auditScope: SCOPE })),
  );
  assert.deepEqual(result, { kind: "uploaded" });
  assert.deepEqual(body.map((event) => event.operationType), ["CASH_IN", "CASH_OUT", "CASH_MOVEMENT_VOID"]);
  assert.deepEqual(body.map((event) => event.amountDelta), [50.25, -12.5, 12.5]);
  for (const event of body) {
    assert.equal(event.orderGuid, null);
    assert.equal(event.outcome, "Succeeded");
    assert.equal(event.cashierId, "c1");
    assert.deepEqual(event.items, []);
  }
  assert.equal(body[0]?.correlationId, GUID_A);
  assert.deepEqual(body[0]?.properties, {
    source: "cash-movement",
    action: "in",
    status: "completed",
    reason: "ChangeTopUp",
    paymentMethod: "Cash",
    requestingCashierId: "c1",
    requestingCashierName: "Alice",
    requestingUserGuid: GUID_B,
  });
  // 冲正原记录 guid 只存本地，不在上传白名单内。
  assert.equal(body[2]?.properties?.voidsMovementGuid, undefined);
});

test("correlationId 不是 UUID 的现金进出审计被永久拒绝（AUDIT_EVENT_INVALID）", async () => {
  const good = commit(GUID_A, AUDIT_A, { kind: "movement", reasonCode: "ChangeTopUp", amountCents: 100 });
  const { result } = await upload([
    { ...good.audit, correlationId: "CM-0001", auditScope: SCOPE },
  ]);
  assert.equal(result.kind, "acknowledged");
  assert.deepEqual(result.kind === "acknowledged" && result.rejected, [
    { eventId: AUDIT_A, code: "AUDIT_EVENT_INVALID" },
  ]);
});
