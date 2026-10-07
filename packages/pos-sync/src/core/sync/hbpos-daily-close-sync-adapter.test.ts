import assert from "node:assert/strict";
import test from "node:test";

import {
  HbposApiError,
  type HbposTransport,
  type HbposTransportRequest,
} from "@hb/pos-api-client/transport";

import { archiveFixture } from "../../testing/daily-close-fixtures";
import { mapDailyCloseArchiveToSyncRequest } from "./daily-close-sync-request";
import {
  HbposDailyCloseSyncAdapter,
  type DailyCloseSyncResult,
} from "./hbpos-daily-close-sync-adapter";

const request = mapDailyCloseArchiveToSyncRequest(archiveFixture(), {
  clientKind: "Handheld",
});

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
  return { adapter: new HbposDailyCloseSyncAdapter(transport), requests };
}

async function classify(script: Script): Promise<DailyCloseSyncResult> {
  return adapterFor(script).adapter.sync(request);
}

test("请求发往 POST /api/v1/daily-closes/sync，并让传输层放行可读错误体的状态码", async () => {
  const { adapter, requests } = adapterFor({
    status: 200,
    data: { accepted: true, alreadySynced: false, replacedPlaceholder: false },
  });
  await adapter.sync(request);
  assert.equal(requests.length, 1);
  assert.equal(requests[0]?.method, "POST");
  assert.equal(requests[0]?.url, "/api/v1/daily-closes/sync");
  assert.deepEqual(requests[0]?.data, request);
  assert.deepEqual([...(requests[0]?.acceptedStatuses ?? [])].sort(), [400, 409, 413, 422]);
});

test("200：Accepted / AlreadySynced / ReplacedPlaceholder 任一为 true 即视为已同步", async () => {
  assert.deepEqual(
    await classify({ status: 200, data: { accepted: true, alreadySynced: false, replacedPlaceholder: false } }),
    { kind: "synced", outcome: "accepted" },
  );
  assert.deepEqual(
    await classify({ status: 200, data: { accepted: false, alreadySynced: true, replacedPlaceholder: false } }),
    { kind: "synced", outcome: "already-synced" },
  );
  assert.deepEqual(
    await classify({ status: 200, data: { accepted: false, alreadySynced: false, replacedPlaceholder: true } }),
    { kind: "synced", outcome: "replaced-placeholder" },
  );
});

test("200 但三个标志都 false 或响应体为空：视为可重试，不永久拒绝", async () => {
  const none = await classify({
    status: 200,
    data: { accepted: false, alreadySynced: false, replacedPlaceholder: false },
  });
  assert.equal(none.kind, "retry");
  assert.equal(none.kind === "retry" && none.code, "SYNC_NOT_ACCEPTED");
  for (const data of [null, undefined, "", [], "ok"]) {
    const result = await classify({ status: 200, data });
    assert.equal(result.kind, "retry");
    assert.equal(result.kind === "retry" && result.code, "EMPTY_SYNC_RESPONSE");
  }
});

test("400 / 413 / 422：永久拒绝，记录服务端 { code, message }", async () => {
  for (const status of [400, 413, 422]) {
    const result = await classify({
      status,
      data: { code: "INVALID_NOTE_SUBTOTAL", message: "noteSubtotal does not match the cash counts." },
    });
    assert.deepEqual(result, {
      kind: "rejected",
      code: "INVALID_NOTE_SUBTOTAL",
      message: "noteSubtotal does not match the cash counts.",
    });
  }
  // 没有错误体时用 HTTP_<status> 兜底。
  assert.deepEqual(await classify({ status: 413 }), {
    kind: "rejected",
    code: "HTTP_413",
    message: "Daily close sync failed with HTTP 413.",
  });
});

test("409：范围冲突与内容冲突永久拒绝，并发更新冲突退避重试", async () => {
  for (const code of ["DAILY_CLOSE_SCOPE_CONFLICT", "DAILY_CLOSE_CONTENT_CONFLICT"]) {
    const result = await classify({ status: 409, data: { code, message: "conflict" } });
    assert.equal(result.kind, "rejected", code);
    assert.equal(result.kind === "rejected" && result.code, code);
  }
  const concurrent = await classify({
    status: 409,
    data: { code: "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE", message: "retry" },
  });
  assert.equal(concurrent.kind, "retry");
  assert.equal(concurrent.kind === "retry" && concurrent.code, "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE");
  // 409 没有错误码时无法证明是暂时冲突，按永久冲突处理。
  assert.equal((await classify({ status: 409, data: {} })).kind, "rejected");
});

test("401 / 403（传输层抛出的 HbposApiError）：设备授权问题，单独分类以便中断批次", async () => {
  for (const status of [401, 403]) {
    const result = await classify({
      throws: new HbposApiError("denied", { kind: "http", status }),
    });
    assert.deepEqual(result, { kind: "unauthorized", code: `HTTP_${status}`, message: "denied" });
  }
  const withCode = await classify({
    throws: new HbposApiError("scope", { kind: "http", status: 403, code: "DEVICE_SCOPE_FORBIDDEN" }),
  });
  assert.deepEqual(withCode, {
    kind: "unauthorized",
    code: "DEVICE_SCOPE_FORBIDDEN",
    message: "scope",
  });
});

test("404 / 408 / 429 / 5xx：退避重试（404 = 服务端尚未部署接口，不丢数据）", async () => {
  for (const status of [404, 408, 429, 500, 502, 503, 504]) {
    const result = await classify({
      throws: new HbposApiError("server", { kind: "http", status }),
    });
    assert.equal(result.kind, "retry", `HTTP ${status}`);
    assert.equal(result.kind === "retry" && result.code, `HTTP_${status}`);
  }
});

test("抛出的 400 / 409 / 413 / 422 与传输层放行的同状态码分类一致", async () => {
  assert.equal(
    (await classify({ throws: new HbposApiError("bad", { kind: "http", status: 400, code: "X" }) })).kind,
    "rejected",
  );
  assert.equal(
    (await classify({ throws: new HbposApiError("bad", { kind: "http", status: 409, code: "DAILY_CLOSE_SYNC_CONCURRENT_UPDATE" }) })).kind,
    "retry",
  );
  assert.equal(
    (await classify({ throws: new HbposApiError("bad", { kind: "http", status: 422 }) })).kind,
    "rejected",
  );
});

test("网络异常 / 超时 / 取消 / 未知异常：退避重试，永不抛出", async () => {
  const network = await classify({
    throws: new HbposApiError("offline", { kind: "transport", code: "NO_HTTP_RESPONSE" }),
  });
  assert.equal(network.kind, "retry");
  assert.equal(network.kind === "retry" && network.code, "NETWORK");
  const aborted = await classify({
    throws: new HbposApiError("cancelled", { kind: "transport", code: "REQUEST_ABORTED" }),
  });
  assert.equal(aborted.kind, "retry");
  const unknown = await classify({ throws: new Error("boom") });
  assert.equal(unknown.kind, "retry");
  assert.equal(unknown.kind === "retry" && unknown.code, "UPLOAD_EXCEPTION");
  const nonError = await classify({ throws: "string failure" });
  assert.equal(nonError.kind, "retry");
});
