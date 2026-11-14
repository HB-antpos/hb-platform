import assert from "node:assert/strict";
import test from "node:test";

import {
  HbposApiError,
  type HbposTransport,
  type HbposTransportRequest,
} from "@hb/pos-api-client/transport";

import { cashMovementFixture } from "../../testing/cash-movement-fixtures";

import {
  buildCashMovementSyncRequest,
  mapCashMovementToSyncItem,
} from "./cash-movement-sync-request";
import {
  CASH_MOVEMENT_RETRYABLE_REJECTION_CODES,
  HbposCashMovementSyncAdapter,
  type CashMovementSyncResult,
} from "./hbpos-cash-movement-sync-adapter";

const GUID = "6f9619ff-8b86-4011-b42d-00c04fc964aa";
const request = buildCashMovementSyncRequest(
  [mapCashMovementToSyncItem(cashMovementFixture())],
  { storeCode: "S001", deviceCode: "DEV-1", clientKind: "Handheld" },
);

type Script =
  | Readonly<{ status: number; data?: unknown }>
  | Readonly<{ throws: unknown }>;

function adapterFor(script: Script) {
  const requests: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(input: HbposTransportRequest) {
      requests.push(input);
      if ("throws" in script) throw script.throws;
      return { status: script.status, data: script.data as T };
    },
  };
  return { adapter: new HbposCashMovementSyncAdapter(transport), requests };
}

async function classify(script: Script): Promise<CashMovementSyncResult> {
  return adapterFor(script).adapter.sync(request);
}

async function itemOutcome(status: string, code?: string) {
  const result = await classify({
    status: 200,
    data: { results: [{ movementGuid: GUID, status, code, message: "m" }] },
  });
  assert.equal(result.kind, "results");
  return result.kind === "results" ? result.items[0] : undefined;
}

test("请求发往 POST /api/v1/cash-movements/sync，裸 JSON 响应，并放行 400 / 413 / 422 的错误体", async () => {
  const { adapter, requests } = adapterFor({
    status: 200,
    data: { results: [{ movementGuid: GUID, status: "Accepted" }] },
  });
  await adapter.sync(request);
  assert.equal(requests.length, 1);
  assert.equal(requests[0]?.method, "POST");
  assert.equal(requests[0]?.url, "/api/v1/cash-movements/sync");
  assert.deepEqual(requests[0]?.data, request);
  assert.deepEqual([...(requests[0]?.acceptedStatuses ?? [])].sort(), [400, 413, 422]);
});

test("200：Accepted / AlreadySynced 都算成功（大小写不敏感，guid 小写规范化）", async () => {
  assert.equal((await itemOutcome("Accepted"))?.outcome, "synced");
  assert.equal((await itemOutcome("AlreadySynced"))?.outcome, "synced");
  assert.equal((await itemOutcome("alreadysynced"))?.outcome, "synced");
  const upper = await classify({
    status: 200,
    data: { results: [{ movementGuid: GUID.toUpperCase(), status: "Accepted" }] },
  });
  assert.equal(upper.kind === "results" && upper.items[0]?.movementGuid, GUID);
});

test("200 单条 Rejected：永久类带错误码，重试类（VOIDED_MOVEMENT_NOT_FOUND / CASH_MOVEMENT_CONCURRENT_UPDATE）回到重试", async () => {
  for (const code of [
    "MOVEMENT_GUID_REQUIRED", "INVALID_REASON_CODE", "INVALID_DIRECTION", "INVALID_VOID",
    "DIRECTION_REASON_MISMATCH", "INVALID_AMOUNT", "NOTE_TOO_LONG", "NOTE_REQUIRED", "TIME_REQUIRED",
    "INVALID_BUSINESS_DATE", "INVALID_OCCURRED_AT", "CASHIER_ID_TOO_LONG", "CASHIER_NAME_TOO_LONG",
    "DISPLAY_NUMBER_TOO_LONG", "VALUE_TOO_LONG", "ALREADY_VOIDED", "CASH_MOVEMENT_SCOPE_CONFLICT",
    "CASH_MOVEMENT_CONTENT_CONFLICT",
  ]) {
    const item = await itemOutcome("Rejected", code);
    assert.equal(item?.outcome, "rejected", code);
    assert.equal(item?.code, code);
  }
  assert.deepEqual([...CASH_MOVEMENT_RETRYABLE_REJECTION_CODES].sort(), [
    "CASH_MOVEMENT_CONCURRENT_UPDATE",
    "VOIDED_MOVEMENT_NOT_FOUND",
  ]);
  for (const code of CASH_MOVEMENT_RETRYABLE_REJECTION_CODES) {
    const item = await itemOutcome("Rejected", code);
    assert.equal(item?.outcome, "retry", code);
    assert.equal(item?.code, code);
  }
  // Rejected 没带错误码：永久拒绝，兜底 REJECTED。
  const noCode = await itemOutcome("Rejected");
  assert.equal(noCode?.outcome, "rejected");
  assert.equal(noCode?.code, "REJECTED");
});

test("200 未知状态：保守重试而不是永久拒绝；缺 results / 空体 / 非对象：整批重试", async () => {
  const unknown = await itemOutcome("Whatever");
  assert.equal(unknown?.outcome, "retry");
  assert.equal(unknown?.code, "SYNC_STATUS_UNKNOWN");
  for (const data of [null, undefined, "", [], "ok", {}, { results: null }]) {
    const result = await classify({ status: 200, data });
    assert.equal(result.kind, "retry");
    assert.equal(result.kind === "retry" && result.code, "EMPTY_SYNC_RESPONSE");
  }
});

test("整批 400 / 413 / 422：整批永久拒绝，记录服务端 { code, message }", async () => {
  for (const status of [400, 413, 422]) {
    assert.deepEqual(
      await classify({ status, data: { code: "INVALID_BATCH_SIZE", message: "movements must contain 1 to 100 entries." } }),
      { kind: "rejected", code: "INVALID_BATCH_SIZE", message: "movements must contain 1 to 100 entries." },
    );
  }
  assert.deepEqual(await classify({ status: 400 }), {
    kind: "rejected",
    code: "HTTP_400",
    message: "Cash movement sync failed with HTTP 400.",
  });
  assert.equal(
    (await classify({ throws: new HbposApiError("bad", { kind: "http", status: 400, code: "X" }) })).kind,
    "rejected",
  );
});

test("401 / 403：设备授权问题，单独分类以便释放租约并中断批次", async () => {
  for (const status of [401, 403]) {
    assert.deepEqual(
      await classify({ throws: new HbposApiError("denied", { kind: "http", status }) }),
      { kind: "unauthorized", code: `HTTP_${status}`, message: "denied" },
    );
  }
  assert.equal(
    (await classify({ status: 403, data: { code: "DEVICE_SCOPE_FORBIDDEN", message: "scope" } })).kind,
    "unauthorized",
  );
});

test("404 / 408 / 429 / 5xx / 网络异常 / 未知异常：整批退避重试，永不抛出", async () => {
  for (const status of [404, 408, 429, 500, 502, 503, 504]) {
    const result = await classify({ throws: new HbposApiError("server", { kind: "http", status }) });
    assert.equal(result.kind, "retry", `HTTP ${status}`);
    assert.equal(result.kind === "retry" && result.code, `HTTP_${status}`);
  }
  const network = await classify({
    throws: new HbposApiError("offline", { kind: "transport", code: "NO_HTTP_RESPONSE" }),
  });
  assert.equal(network.kind === "retry" && network.code, "NETWORK");
  assert.equal((await classify({ throws: new Error("boom") })).kind, "retry");
  assert.equal((await classify({ throws: "string failure" })).kind, "retry");
  // 传输层「正常返回」的 5xx 同样是重试。
  assert.equal((await classify({ status: 503, data: { code: "X" } })).kind, "retry");
});
