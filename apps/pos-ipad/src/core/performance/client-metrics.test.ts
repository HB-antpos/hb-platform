import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import {
  ClientMetricRecorder,
  ClientMetricRuntime,
  ClientMetricSampler,
  ClientMetricSamplingPolicyState,
  ClientMetricUploader,
  POS_CLIENT_METRICS,
  SqliteClientMetricOutbox,
  SqliteClientMetricSamplingPolicyStore,
  buildMetricEventV1,
  initializeClientMetricOutbox,
  normalizeClientMetricEnvironment,
  resolveClientMetricUploadConfig,
  type MetricEventV1,
} from "./client-metrics";

import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";

const EVENT_ID = "11111111-1111-4111-8111-111111111111";
const OBSERVED_AT = "2026-08-25T00:00:00.000Z";

test("MetricBatchV1 与中心 DTO 同口径且仅写七个客户端白名单维度", async () => {
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(database, () => OBSERVED_AT);
  const recorder = new ClientMetricRecorder({
    outbox,
    sampler: new ClientMetricSampler({
      policyState: new ClientMetricSamplingPolicyState(),
      sessionId: "session-full",
    }),
    context: {
      app: "pos-ipad",
      version: "0.2.0",
      channel: "pos-ipad-production",
      store: "001",
      environment: "Production",
    },
    createId: () => EVENT_ID,
    nowIso: () => OBSERVED_AT,
  });

  assert.equal(
    await recorder.record({
      metric: POS_CLIENT_METRICS.scanToCart,
      valueMs: 135.5,
      dimensions: { outcome: "success" },
    }),
    "queued",
  );

  const [delivery] = await outbox.listReady(10);
  assert.deepEqual(delivery?.event, {
    eventId: EVENT_ID,
    metric: "pos.scan_to_cart.duration",
    observedAt: OBSERVED_AT,
    value: 135.5,
    unit: "ms",
    dimensions: {
      app: "pos-ipad",
      version: "0.2.0",
      channel: "pos-ipad-production",
      store: "001",
      environment: "Production",
      outcome: "success",
    },
  });
  assert.deepEqual(Object.keys(delivery?.event.dimensions ?? {}).sort(), [
    "app",
    "channel",
    "environment",
    "outcome",
    "store",
    "version",
  ]);
  await database.close();
});

test("首次无策略全量；冻结后稳定 session 采样，失败/拒绝/超时和严格超阈值全量", () => {
  const policyState = new ClientMetricSamplingPolicyState();
  const selected = new ClientMetricSampler({
    policyState,
    sessionId: "selected",
    stableSessionUnit: 0.199999,
  });
  const omitted = new ClientMetricSampler({
    policyState,
    sessionId: "omitted",
    stableSessionUnit: 0.2,
  });
  const defaultFull = new ClientMetricSampler({
    policyState,
    sessionId: "first-launch",
    stableSessionUnit: 0.99,
  });
  const normal = metric({ value: 80, outcome: "success" });

  assert.equal(defaultFull.shouldKeep(normal), true);
  policyState.replace({
    baselineState: "frozen",
    defaultSampleRate: 0.2,
    policies: [
      {
        metric: POS_CLIENT_METRICS.scanToCart,
        selector: "pos-ipad",
        sampleRate: 0.2,
        slowThreshold: 1_000,
      },
    ],
  });
  assert.equal(selected.shouldKeep(normal), true);
  assert.equal(omitted.shouldKeep(normal), false);
  assert.equal(
    omitted.shouldKeep(
      metric({ metric: POS_CLIENT_METRICS.paymentResponse }),
    ),
    true,
  );
  for (const outcome of ["failure", "rejected", "timeout"]) {
    assert.equal(omitted.shouldKeep(metric({ value: 80, outcome })), true);
  }
  assert.equal(omitted.shouldKeep(metric({ value: 1_000 })), false);
  assert.equal(omitted.shouldKeep(metric({ value: 1_000.001 })), true);
});

test("敏感或未知维度 fail closed，错误不回显敏感值", () => {
  const secret = "ORDER-SECRET-950616";
  for (const key of [
    "deviceId",
    "employeeId",
    "orderId",
    "cardNumber",
    "barcode",
  ]) {
    assert.throws(
      () =>
        buildMetricEventV1({
          eventId: EVENT_ID,
          metric: POS_CLIENT_METRICS.paymentResponse,
          observedAt: OBSERVED_AT,
          valueMs: 10,
          dimensions: {
            app: "pos-ipad",
            outcome: "failure",
            [key]: secret,
          },
        }),
      (error: unknown) =>
        error instanceof Error &&
        error.message.includes(key) &&
        !error.message.includes(secret),
    );
  }
});

test("指标事件必须写入规范化的 Log Center 环境，缺失或非法配置停用上报", () => {
  for (const [raw, expected] of [
    ["production", "Production"],
    [" Development ", "Development"],
    ["PREVIEW", "Preview"],
  ] as const) {
    assert.equal(normalizeClientMetricEnvironment(raw), expected);
    const event = buildMetricEventV1({
      eventId: EVENT_ID,
      metric: POS_CLIENT_METRICS.scanToCart,
      observedAt: OBSERVED_AT,
      valueMs: 10,
      dimensions: { app: "pos-ipad", environment: raw },
    });
    assert.equal(event.dimensions.environment, expected);
    assert.equal(
      resolveClientMetricUploadConfig({
        enabled: true,
        logIngestUrl: "https://logs.example.test/api/system/logs/ingest",
        writeKey: "write-key-from-log-project",
        projectCode: "hbpos_ipad",
        environment: raw,
      }).enabled,
      true,
    );
  }

  for (const raw of [undefined, "", "staging", "prod"] as const) {
    assert.equal(normalizeClientMetricEnvironment(raw), null);
    assert.throws(() =>
      buildMetricEventV1({
        eventId: EVENT_ID,
        metric: POS_CLIENT_METRICS.scanToCart,
        observedAt: OBSERVED_AT,
        valueMs: 10,
        dimensions: { app: "pos-ipad", environment: raw },
      }),
    );
    assert.equal(
      resolveClientMetricUploadConfig({
        enabled: true,
        logIngestUrl: "https://logs.example.test/api/system/logs/ingest",
        writeKey: "write-key-from-log-project",
        projectCode: "hbpos_ipad",
        environment: raw,
      }).enabled,
      false,
    );
  }
});

test("服务端采样策略与 outbox 同库持久化，重启后仍可读取", async () => {
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const store = new SqliteClientMetricSamplingPolicyStore(
    database,
    () => OBSERVED_AT,
  );
  const policy = {
    baselineState: "frozen" as const,
    defaultSampleRate: 0.2,
    policies: [
      {
        metric: POS_CLIENT_METRICS.scanToCart,
        selector: "pos-ipad",
        sampleRate: 0.2,
        slowThreshold: 750,
      },
    ],
  };

  await store.save(policy);
  assert.deepEqual(await store.read(), policy);
  await database.close();
});

test("SQLite outbox 对 eventId 幂等，离线重试沿用同一事件且复用日志项目 key", async () => {
  let now = new Date(OBSERVED_AT);
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(
    database,
    () => now.toISOString(),
  );
  const policyStore = new SqliteClientMetricSamplingPolicyStore(
    database,
    () => now.toISOString(),
  );
  const policyState = new ClientMetricSamplingPolicyState();
  const event = metric();
  await outbox.enqueue(event);
  await outbox.enqueue(event);
  assert.equal((await outbox.listReady(10)).length, 1);

  const requests: { url: string; init?: RequestInit }[] = [];
  let offline = true;
  const fetchImpl: typeof fetch = async (input, init) => {
    requests.push({ url: String(input), ...(init ? { init } : {}) });
    if (offline) throw new TypeError("network unavailable");
    return new Response(
      JSON.stringify({
        success: true,
        data: {
          acceptedCount: 1,
          duplicateCount: 0,
          rejectedCount: 0,
          baselineState: "frozen",
          defaultSampleRate: 0.2,
          policies: [
            {
              metric: POS_CLIENT_METRICS.scanToCart,
              selector: "pos-ipad",
              sampleRate: 0.2,
            },
          ],
        },
      }),
      { status: 200, headers: { "Content-Type": "application/json" } },
    );
  };
  const uploader = new ClientMetricUploader({
    outbox,
    config: resolveClientMetricUploadConfig({
      enabled: true,
      logIngestUrl: "https://logs.example.test/api/system/logs/ingest",
      writeKey: "write-key-from-log-project",
      projectCode: "hbpos_ipad",
      environment: "Production",
    }),
    fetchImpl,
    now: () => now,
    random: () => 0,
    samplingPolicy: { state: policyState, store: policyStore },
  });

  assert.deepEqual(await uploader.flush(), {
    uploaded: 0,
    rejected: 0,
    retried: 1,
  });
  now = new Date("2026-08-25T01:00:00.000Z");
  offline = false;
  assert.deepEqual(await uploader.flush(), {
    uploaded: 1,
    rejected: 0,
    retried: 0,
  });

  assert.equal(
    requests[1]?.url,
    "https://logs.example.test/api/system/performance/client-batches",
  );
  const headers = new Headers(requests[1]?.init?.headers);
  assert.equal(headers.get("X-Log-Project"), "hbpos_ipad");
  assert.equal(headers.get("X-Log-Key"), "write-key-from-log-project");
  assert.equal(headers.has("Authorization"), false);
  const firstBody = JSON.parse(String(requests[0]?.init?.body));
  const secondBody = JSON.parse(String(requests[1]?.init?.body));
  assert.equal(firstBody.schemaVersion, 1);
  assert.equal(firstBody.events[0].eventId, EVENT_ID);
  assert.equal(secondBody.events[0].eventId, EVENT_ID);
  assert.equal((await outbox.listReady(10)).length, 0);
  assert.deepEqual(await policyStore.read(), policyState.read());
  assert.equal(policyState.read().baselineState, "frozen");
  assert.equal(policyState.read().policies[0]?.slowThreshold, null);
  await database.close();
});

test("运行时短时合并多条指标且上传复用设备认证请求头", async () => {
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(database, () => OBSERVED_AT);
  const policyStore = new SqliteClientMetricSamplingPolicyStore(
    database,
    () => OBSERVED_AT,
  );
  const policyState = new ClientMetricSamplingPolicyState();
  const eventIds = [
    "12121212-1212-4212-8212-121212121212",
    "34343434-3434-4434-8434-343434343434",
  ];
  const recorder = new ClientMetricRecorder({
    outbox,
    sampler: new ClientMetricSampler({ policyState, sessionId: "batch-session" }),
    context: {
      app: "pos-ipad",
      version: "0.2.0",
      channel: "pos-ipad-production",
      store: "001",
      environment: "Production",
    },
    createId: () => eventIds.shift()!,
    nowIso: () => OBSERVED_AT,
  });
  const requests: RequestInit[] = [];
  const uploaderDependencies = {
    outbox,
    config: resolveClientMetricUploadConfig({
      enabled: true,
      logIngestUrl: "https://logs.example.test/api/system/logs/ingest",
      writeKey: "public-write-key",
      projectCode: "hbpos_ipad",
      environment: "Production",
    }),
    fetchImpl: async (_input: URL | RequestInfo, init?: RequestInit) => {
      requests.push(init ?? {});
      const count = JSON.parse(String(init?.body)).events.length;
      return successfulIngestResponse(count);
    },
    getRequestHeaders: async () => ({
      Authorization: "Bearer device-authorization",
      "X-HBPOS-Device-Code": "IPAD-01",
      "X-HBPOS-Store-Code": "001",
      "X-HBPOS-Hardware-Id": "hardware-01",
    }),
    samplingPolicy: { state: policyState, store: policyStore },
    // 事件时间固定为 OBSERVED_AT；不注入时钟时上传器按真实时间剔除超过 30 天离线窗口的事件，
    // 过了 OBSERVED_AT 30 天后一条都不会发送，下面等待请求的循环会永远挂住。
    now: () => new Date(OBSERVED_AT),
  } as ConstructorParameters<typeof ClientMetricUploader>[0];
  let scheduledFlush: (() => void) | undefined;
  const runtime = new ClientMetricRuntime(
    recorder,
    new ClientMetricUploader(uploaderDependencies),
    () => database.close(),
    {
      setTimeout: (callback, delayMs) => {
        assert.equal(delayMs, 250);
        scheduledFlush = callback;
        return 1 as unknown as ReturnType<typeof setTimeout>;
      },
      clearTimeout: () => undefined,
    },
  );

  runtime.record({
    metric: POS_CLIENT_METRICS.scanToCart,
    valueMs: 10,
    dimensions: { outcome: "success" },
  });
  while ((await outbox.listReady(10)).length < 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  await new Promise((resolve) => setImmediate(resolve));
  runtime.record({
    metric: POS_CLIENT_METRICS.scanToCart,
    valueMs: 20,
    dimensions: { outcome: "success" },
  });

  while ((await outbox.listReady(10)).length < 2) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  assert.equal(requests.length, 0, "去抖窗口内不应逐条发送请求");
  assert.ok(scheduledFlush);
  scheduledFlush();
  while (requests.length < 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
  assert.equal(requests.length, 1);
  assert.equal(JSON.parse(String(requests[0]?.body)).events.length, 2);
  const headers = new Headers(requests[0]?.headers);
  assert.equal(headers.get("Authorization"), "Bearer device-authorization");
  assert.equal(headers.get("X-HBPOS-Device-Code"), "IPAD-01");
  assert.equal(headers.get("X-HBPOS-Store-Code"), "001");
  assert.equal(headers.get("X-HBPOS-Hardware-Id"), "hardware-01");
  await runtime.shutdown();
});

test("上传前仅剔除超过离线窗口或未来时钟事件，其余事件继续 ACK", async () => {
  const now = new Date("2026-08-25T01:00:00.000Z");
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(database, () => now.toISOString());
  const expired = metric({
    eventId: "33333333-3333-4333-8333-333333333333",
    observedAt: "2026-07-25T00:59:59.999Z",
  });
  const future = metric({
    eventId: "44444444-4444-4444-8444-444444444444",
    observedAt: "2026-08-25T01:05:00.001Z",
  });
  const valid = metric({
    eventId: "55555555-5555-4555-8555-555555555555",
    observedAt: "2026-08-25T00:59:59.999Z",
  });
  await outbox.enqueue(expired);
  await outbox.enqueue(future);
  await outbox.enqueue(valid);
  await database.run(
    "UPDATE client_metric_outbox SET next_attempt_at_iso = ? WHERE event_id = ?",
    [now.toISOString(), future.eventId],
  );
  const requestEventIds: string[][] = [];
  const uploader = createUploader(outbox, now, async (_input, init) => {
    requestEventIds.push(eventsFromRequest(init));
    return successfulIngestResponse(1);
  });

  assert.deepEqual(await uploader.flush(), { uploaded: 1, rejected: 2, retried: 0 });
  assert.deepEqual(requestEventIds, [[valid.eventId]]);
  assert.equal((await outbox.listReady(10)).length, 0);
  await database.close();
});

test("400 批次二分隔离单个坏事件并 ACK 有效事件", async () => {
  const now = new Date("2026-08-25T01:00:00.000Z");
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(database, () => now.toISOString());
  const goodBefore = metric({ eventId: "66666666-6666-4666-8666-666666666666" });
  const bad = metric({ eventId: "77777777-7777-4777-8777-777777777777", value: 999 });
  const goodAfter = metric({ eventId: "88888888-8888-4888-8888-888888888888" });
  await outbox.enqueue(goodBefore);
  await outbox.enqueue(bad);
  await outbox.enqueue(goodAfter);
  const sent: string[][] = [];
  const uploader = createUploader(outbox, now, async (_input, init) => {
    const eventIds = eventsFromRequest(init);
    sent.push(eventIds);
    return eventIds.includes(bad.eventId)
      ? new Response(null, { status: 400 })
      : successfulIngestResponse(eventIds.length);
  });

  assert.deepEqual(await uploader.flush(), { uploaded: 2, rejected: 1, retried: 0 });
  assert.deepEqual(sent, [
    [goodBefore.eventId, bad.eventId, goodAfter.eventId],
    [goodBefore.eventId],
    [bad.eventId, goodAfter.eventId],
    [bad.eventId],
    [goodAfter.eventId],
  ]);
  assert.equal((await outbox.listReady(10)).length, 0);
  await database.close();
});

test("单事件 413 才永久拒绝，避免继续拆分或重试", async () => {
  const now = new Date("2026-08-25T01:00:00.000Z");
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(database, () => now.toISOString());
  const bad = metric({ eventId: "99999999-9999-4999-8999-999999999999" });
  await outbox.enqueue(bad);
  let requests = 0;
  const uploader = createUploader(outbox, now, async () => {
    requests += 1;
    return new Response(null, { status: 413 });
  });

  assert.deepEqual(await uploader.flush(), { uploaded: 0, rejected: 1, retried: 0 });
  assert.equal(requests, 1);
  assert.equal((await outbox.listReady(10)).length, 0);
  await database.close();
});

test("2xx rejectedCount 无逐事件 ID 时二分隔离，不删除有效事件", async () => {
  const now = new Date("2026-08-25T01:00:00.000Z");
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(database, () => now.toISOString());
  const bad = metric({ eventId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", value: 999 });
  const good = metric({ eventId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb" });
  await outbox.enqueue(bad);
  await outbox.enqueue(good);
  const uploader = createUploader(outbox, now, async (_input, init) => {
    const eventIds = eventsFromRequest(init);
    return eventIds.includes(bad.eventId)
      ? rejectedIngestResponse(eventIds.length)
      : successfulIngestResponse(eventIds.length);
  });

  assert.deepEqual(await uploader.flush(), { uploaded: 1, rejected: 1, retried: 0 });
  assert.equal((await outbox.listReady(10)).length, 0);
  await database.close();
});

test("2xx 无效 envelope 仅安排重试，不把事件当作拒绝删除", async () => {
  const now = new Date("2026-08-25T01:00:00.000Z");
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(database, () => now.toISOString());
  const event = metric({ eventId: "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee" });
  await outbox.enqueue(event);
  const uploader = createUploader(
    outbox,
    now,
    async () => new Response(JSON.stringify({ success: true, data: {} }), { status: 200 }),
  );

  assert.deepEqual(await uploader.flush(), { uploaded: 0, rejected: 0, retried: 1 });
  const row = await database.getFirst<{ event_id: string; last_error_code: string }>(
    "SELECT event_id, last_error_code FROM client_metric_outbox WHERE event_id = ?",
    [event.eventId],
  );
  assert.deepEqual(row && { ...row }, {
    event_id: event.eventId,
    last_error_code: "INVALID_RESPONSE",
  });
  await database.close();
});

test("5xx 仅安排重试且不误删事件", async () => {
  const now = new Date("2026-08-25T01:00:00.000Z");
  const database = new NodeSqliteConnection(new DatabaseSync(":memory:"));
  await initializeClientMetricOutbox(database);
  const outbox = new SqliteClientMetricOutbox(database, () => now.toISOString());
  const first = metric({ eventId: "cccccccc-cccc-4ccc-8ccc-cccccccccccc" });
  const second = metric({ eventId: "dddddddd-dddd-4ddd-8ddd-dddddddddddd" });
  await outbox.enqueue(first);
  await outbox.enqueue(second);
  const uploader = createUploader(outbox, now, async () => new Response(null, { status: 503 }));

  assert.deepEqual(await uploader.flush(), { uploaded: 0, rejected: 0, retried: 2 });
  const rows = await database.getAll<{ event_id: string; attempt_count: number; last_error_code: string }>(
    "SELECT event_id, attempt_count, last_error_code FROM client_metric_outbox ORDER BY event_id",
  );
  assert.deepEqual(rows.map((row) => ({ ...row })), [
    { event_id: first.eventId, attempt_count: 1, last_error_code: "HTTP_503" },
    { event_id: second.eventId, attempt_count: 1, last_error_code: "HTTP_503" },
  ]);
  await database.close();
});

function metric(
  override: Readonly<{
    eventId?: string;
    metric?: MetricEventV1["metric"];
    observedAt?: string;
    value?: number;
    outcome?: string;
  }> = {},
): MetricEventV1 {
  return {
    eventId: override.eventId ?? EVENT_ID,
    metric: override.metric ?? POS_CLIENT_METRICS.scanToCart,
    observedAt: override.observedAt ?? OBSERVED_AT,
    value: override.value ?? 100,
    unit: "ms",
    dimensions: {
      app: "pos-ipad",
      version: "0.2.0",
      channel: "pos-ipad-production",
      store: "001",
      environment: "Production",
      outcome: override.outcome ?? "success",
    },
  };
}

function createUploader(
  outbox: SqliteClientMetricOutbox,
  now: Date,
  fetchImpl: typeof fetch,
): ClientMetricUploader {
  const state = new ClientMetricSamplingPolicyState();
  return new ClientMetricUploader({
    outbox,
    config: resolveClientMetricUploadConfig({
      enabled: true,
      logIngestUrl: "https://logs.example.test/api/system/logs/ingest",
      writeKey: "write-key-from-log-project",
      projectCode: "hbpos_ipad",
      environment: "Production",
    }),
    fetchImpl,
    now: () => now,
    random: () => 0,
    samplingPolicy: {
      state,
      store: {
        read: async () => state.read(),
        save: async () => undefined,
      },
    },
  });
}

function eventsFromRequest(init: RequestInit | undefined): string[] {
  const body = JSON.parse(String(init?.body)) as { events: { eventId: string }[] };
  return body.events.map((event) => event.eventId);
}

function successfulIngestResponse(count: number): Response {
  return new Response(
    JSON.stringify({
      success: true,
      data: { acceptedCount: count, duplicateCount: 0, rejectedCount: 0 },
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
}

function rejectedIngestResponse(count: number): Response {
  return new Response(
    JSON.stringify({
      success: false,
      data: { acceptedCount: 0, duplicateCount: 0, rejectedCount: count },
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
}

class NodeSqliteConnection implements SqliteConnectionPort {
  private transactionActive = false;

  public constructor(private readonly database: DatabaseSync) {}

  public async exec(sql: string): Promise<void> {
    this.database.exec(sql);
  }

  public async run(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<SqlRunResult> {
    const result = this.database
      .prepare(sql)
      .run(...parameters as readonly SQLInputValue[]);
    return {
      changes: Number(result.changes),
      lastInsertRowId: Number(result.lastInsertRowid),
    };
  }

  public async getFirst<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<T | null> {
    const row = this.database
      .prepare(sql)
      .get(...parameters as readonly SQLInputValue[]);
    return row === undefined ? null : row as T;
  }

  public async getAll<T extends object>(
    sql: string,
    parameters: readonly SqlValue[] = [],
  ): Promise<readonly T[]> {
    return this.database
      .prepare(sql)
      .all(...parameters as readonly SQLInputValue[]) as unknown as readonly T[];
  }

  public async withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    if (this.transactionActive) throw new Error("Nested test transaction.");
    this.transactionActive = true;
    this.database.exec("BEGIN IMMEDIATE");
    try {
      const result = await operation(this);
      this.database.exec("COMMIT");
      return result;
    } catch (error: unknown) {
      this.database.exec("ROLLBACK");
      throw error;
    } finally {
      this.transactionActive = false;
    }
  }

  public async close(): Promise<void> {
    this.database.close();
  }
}
