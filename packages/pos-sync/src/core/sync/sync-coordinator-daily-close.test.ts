import assert from "node:assert/strict";
import test from "node:test";

import type { AuditEventDraft } from "@hb/pos-domain/core/contracts/order";
import type {
  AuditRepositoryPort,
  OutboxLease,
  OutboxRepositoryPort,
} from "@hb/pos-domain/core/contracts/repositories";

import {
  PosSyncCoordinator,
  SyncLifecycleController,
  type DailyCloseUploadDrainControl,
  type DailyCloseUploadDrainPort,
  type DailyCloseUploadDrainResult,
} from "./sync-coordinator";

/** 日结补传接入 PosSyncCoordinator 的行为测试（订单/审计既有行为见 sync-coordinator.test.ts）。 */

function lease(messageId: string): OutboxLease {
  return {
    messageId,
    leaseId: `lease-${messageId}`,
    aggregateId: `order-${messageId}`,
    kind: "order-sync",
    payloadJson: "{}",
    attemptCount: 0,
  };
}

class FakeOutbox implements OutboxRepositoryPort {
  public readonly succeeded: string[] = [];
  public leaseCalls = 0;
  private readonly nextReadyAtValues: (string | null)[];

  public constructor(
    private readonly batches: readonly (readonly OutboxLease[])[] = [[]],
    nextReadyAtValues: readonly (string | null)[] = [],
  ) {
    this.nextReadyAtValues = [...nextReadyAtValues];
  }

  public async enqueue(): Promise<void> {}

  public async leaseReady(): Promise<readonly OutboxLease[]> {
    return this.batches[this.leaseCalls++] ?? [];
  }

  public async nextReadyAtIso(): Promise<string | null> {
    return this.nextReadyAtValues.shift() ?? null;
  }

  public async markSucceeded(item: OutboxLease): Promise<void> {
    this.succeeded.push(item.messageId);
  }

  public async releaseRetry(): Promise<void> {}
  public async markBlocked403(): Promise<void> {}
  public async markRejected(): Promise<void> {}
}

class FakeAuditRepository implements AuditRepositoryPort {
  public uploaded: string[] = [];

  public constructor(private readonly pending: AuditEventDraft[] = []) {}

  public async append(): Promise<void> {}

  public async listPending(limit: number): Promise<readonly AuditEventDraft[]> {
    return this.pending.slice(0, limit);
  }

  public async markUploaded(eventIds: readonly string[]): Promise<void> {
    this.uploaded.push(...eventIds);
    this.pending.splice(0, eventIds.length);
  }
}

function audit(eventId: string): AuditEventDraft {
  return { eventId, eventType: "cash-sale", occurredAtIso: "2026-07-28T00:00:00.000Z", orderGuid: null, correlationId: eventId, payload: {} };
}

function deferred<T>(): Readonly<{
  promise: Promise<T>;
  resolve(value: T | PromiseLike<T>): void;
}> {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((accept) => {
    resolve = accept;
  });
  return { promise, resolve };
}

const IDLE_DAILY_CLOSE_RESULT: DailyCloseUploadDrainResult = {
  attempted: 0,
  uploaded: 0,
  rejected: 0,
  skipped: 0,
  deferred: 0,
  interrupted: false,
};

class FakeDailyCloseUpload implements DailyCloseUploadDrainPort {
  public drains = 0;
  public readonly controls: (DailyCloseUploadDrainControl | undefined)[] = [];
  public nextReadyCalls = 0;
  public constructor(
    private readonly onDrain: () => void | Promise<void> = () => undefined,
    private readonly nextReady: () => string | null | Promise<string | null> = () => null,
  ) {}
  public async drain(control?: DailyCloseUploadDrainControl): Promise<DailyCloseUploadDrainResult> {
    this.drains += 1;
    this.controls.push(control);
    await this.onDrain();
    return IDLE_DAILY_CLOSE_RESULT;
  }
  public async nextReadyAtIso(): Promise<string | null> {
    this.nextReadyCalls += 1;
    return this.nextReady();
  }
}

function coordinatorWithDailyClose(
  dailyCloseUpload: DailyCloseUploadDrainPort,
  overrides: Partial<ConstructorParameters<typeof PosSyncCoordinator>[0]> = {},
): PosSyncCoordinator {
  return new PosSyncCoordinator({
    outbox: new FakeOutbox([[]]),
    auditRepository: new FakeAuditRepository(),
    orderSync: { async sync() { return { kind: "synced", alreadySynced: false }; } },
    auditUploader: { async upload() { return { kind: "uploaded" }; } },
    dailyCloseUpload,
    security: { async lockDevice() {} },
    now: () => new Date("2026-07-28T00:00:00.000Z"),
    random: () => 0.5,
    ...overrides,
  });
}

test("日结补传在每次 drain 末尾执行：订单、员工审计之后，并随共享单飞 drain 触发", async () => {
  const events: string[] = [];
  const outbox = new FakeOutbox([[lease("one")]]);
  const coordinator = new PosSyncCoordinator({
    outbox,
    auditRepository: new FakeAuditRepository([audit("audit-1")]),
    orderSync: {
      async sync() {
        events.push("order");
        return { kind: "synced", alreadySynced: false };
      },
    },
    auditUploader: {
      async upload() {
        events.push("audit");
        return { kind: "uploaded" };
      },
    },
    dailyCloseUpload: new FakeDailyCloseUpload(() => {
      events.push("daily-close");
    }),
    security: { async lockDevice() {} },
    now: () => new Date("2026-07-28T00:00:00.000Z"),
    random: () => 0.5,
  });

  await new SyncLifecycleController(coordinator).onApplicationStarted();

  assert.deepEqual(events, ["order", "audit", "daily-close"]);
});

test("日结补传每次 drain 都会被调用（启动、前台、联网共用同一入口），多触发源合并为一次", async () => {
  const gate = deferred<void>();
  const upload = new FakeDailyCloseUpload(() => gate.promise);
  const coordinator = coordinatorWithDailyClose(upload);
  const lifecycle = new SyncLifecycleController(coordinator);

  const started = lifecycle.onApplicationStarted();
  const foreground = lifecycle.onForeground();
  const network = lifecycle.onNetworkChanged(true);
  await Promise.resolve();
  await Promise.resolve();
  gate.resolve(undefined);
  await Promise.all([started, foreground, network]);

  // 单飞：并发触发不会同时跑两个日结 drain（锁存的第二轮最多再补一次）。
  assert.ok(upload.drains >= 1 && upload.drains <= 2);
  assert.deepEqual(await lifecycle.onNetworkChanged(false).then((report) => report.leased), 0);
});

test("日结补传抛异常不影响订单与员工审计，drain 仍正常完成", async () => {
  const outbox = new FakeOutbox([[lease("one")]]);
  const auditRepository = new FakeAuditRepository([audit("audit-1")]);
  const coordinator = createCoordinatorWithDailyClose(
    outbox,
    auditRepository,
    new FakeDailyCloseUpload(() => {
      throw new Error("daily close storage unavailable");
    }),
  );

  const report = await coordinator.requestDrain();

  assert.deepEqual(outbox.succeeded, ["one"]);
  assert.deepEqual(auditRepository.uploaded, ["audit-1"]);
  assert.equal(report.orderSucceeded, 1);
});

function createCoordinatorWithDailyClose(
  outbox: FakeOutbox,
  auditRepository: FakeAuditRepository,
  dailyCloseUpload: DailyCloseUploadDrainPort,
): PosSyncCoordinator {
  return coordinatorWithDailyClose(dailyCloseUpload, { outbox, auditRepository });
}

test("没有注入日结上传端口时行为与以前完全一致", async () => {
  const outbox = new FakeOutbox([[lease("one")]]);
  const coordinator = new PosSyncCoordinator({
    outbox,
    auditRepository: new FakeAuditRepository(),
    orderSync: { async sync() { return { kind: "synced", alreadySynced: false }; } },
    auditUploader: { async upload() { return { kind: "uploaded" }; } },
    security: { async lockDevice() {} },
    now: () => new Date("2026-07-28T00:00:00.000Z"),
    random: () => 0.5,
  });
  const report = await coordinator.requestDrain();
  assert.equal(report.orderSucceeded, 1);
});

test("shutdown 后日结 drain 收到停止信号，且不再被调用", async () => {
  const upload = new FakeDailyCloseUpload();
  const coordinator = coordinatorWithDailyClose(upload);

  await coordinator.requestDrain();
  assert.equal(upload.drains, 1);
  assert.equal(upload.controls[0]?.shouldStop?.(), false);

  await coordinator.shutdown();
  assert.equal(upload.controls[0]?.shouldStop?.(), true, "已在执行的 drain 能感知关闭");
  await coordinator.requestDrain();
  assert.equal(upload.drains, 1, "关闭后不再访问日结队列");
});

test("日结重试时间参与定时唤醒；已过期的时间被钳到至少 1 秒后，避免 0ms 自旋", async () => {
  const scheduled: { delayMs: number }[] = [];
  const timer = {
    set(delayMs: number) {
      scheduled.push({ delayMs });
      return scheduled.length;
    },
    clear() {},
  };

  // 5 秒后的退避重试：按 5 秒唤醒。
  await coordinatorWithDailyClose(
    new FakeDailyCloseUpload(undefined, () => "2026-07-28T00:00:05.000Z"),
    { timer },
  ).requestDrain();
  // 已过期（本轮没处理掉）的时间：至少 1 秒，不能是 0。
  await coordinatorWithDailyClose(
    new FakeDailyCloseUpload(undefined, () => "2026-07-27T00:00:00.000Z"),
    { timer },
  ).requestDrain();
  // 没有待重试的日结：不安排唤醒。
  await coordinatorWithDailyClose(new FakeDailyCloseUpload(), { timer }).requestDrain();
  // 唤醒时间读取失败：吞掉，不影响 drain，也不安排唤醒。
  await coordinatorWithDailyClose(
    new FakeDailyCloseUpload(undefined, () => {
      throw new Error("sqlite closed");
    }),
    { timer },
  ).requestDrain();

  assert.deepEqual(scheduled.map((entry) => entry.delayMs), [5_000, 1_000]);
});

test("日结唤醒时间与订单/审计重试取最早者", async () => {
  const scheduled: { delayMs: number }[] = [];
  await coordinatorWithDailyClose(
    new FakeDailyCloseUpload(undefined, () => "2026-07-28T00:00:30.000Z"),
    {
      outbox: new FakeOutbox([[]], ["2026-07-28T00:01:00.000Z"]),
      timer: {
        set(delayMs) {
          scheduled.push({ delayMs });
          return 1;
        },
        clear() {},
      },
    },
  ).requestDrain();
  assert.deepEqual(scheduled.map((entry) => entry.delayMs), [30_000]);
});
