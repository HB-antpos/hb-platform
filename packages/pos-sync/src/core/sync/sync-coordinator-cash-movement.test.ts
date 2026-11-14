import assert from "node:assert/strict";
import test from "node:test";

import type { OutboxLease, OutboxRepositoryPort, AuditRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";

import {
  PosSyncCoordinator,
  type CashMovementUploadDrainPort,
  type DailyCloseUploadDrainControl,
  type DailyCloseUploadDrainPort,
  type DailyCloseUploadDrainResult,
} from "./sync-coordinator";

/** 现金进出补传接入 PosSyncCoordinator 的行为测试：先于日结、互相隔离、关闭感知、定时唤醒。 */

const IDLE: DailyCloseUploadDrainResult = {
  attempted: 0, uploaded: 0, rejected: 0, skipped: 0, deferred: 0, interrupted: false,
};

class FakeUpload implements CashMovementUploadDrainPort, DailyCloseUploadDrainPort {
  public drains = 0;
  public readonly controls: (DailyCloseUploadDrainControl | undefined)[] = [];
  public constructor(
    private readonly onDrain: () => void | Promise<void> = () => undefined,
    private readonly nextReady: () => string | null = () => null,
  ) {}
  public async drain(control?: DailyCloseUploadDrainControl): Promise<DailyCloseUploadDrainResult> {
    this.drains += 1;
    this.controls.push(control);
    await this.onDrain();
    return IDLE;
  }
  public async nextReadyAtIso(): Promise<string | null> {
    return this.nextReady();
  }
}

class FakeOutbox implements OutboxRepositoryPort {
  public async enqueue(): Promise<void> {}
  public async leaseReady(): Promise<readonly OutboxLease[]> { return []; }
  public async nextReadyAtIso(): Promise<string | null> { return null; }
  public async markSucceeded(): Promise<void> {}
  public async releaseRetry(): Promise<void> {}
  public async markBlocked403(): Promise<void> {}
  public async markRejected(): Promise<void> {}
}

class FakeAudit implements AuditRepositoryPort {
  public async append(): Promise<void> {}
  public async listPending(): Promise<readonly never[]> { return []; }
  public async markUploaded(): Promise<void> {}
}

function coordinator(
  options: Partial<ConstructorParameters<typeof PosSyncCoordinator>[0]>,
): PosSyncCoordinator {
  return new PosSyncCoordinator({
    outbox: new FakeOutbox(),
    auditRepository: new FakeAudit(),
    orderSync: { async sync() { return { kind: "synced", alreadySynced: false }; } },
    auditUploader: { async upload() { return { kind: "uploaded" }; } },
    security: { async lockDevice() {} },
    now: () => new Date("2026-11-14T00:00:00.000Z"),
    random: () => 0.5,
    ...options,
  });
}

test("现金进出先于日结上传：顺序 现金进出 → 日结", async () => {
  const events: string[] = [];
  await coordinator({
    cashMovementUpload: new FakeUpload(() => { events.push("cash-movement"); }),
    dailyCloseUpload: new FakeUpload(() => { events.push("daily-close"); }),
  }).requestDrain();
  assert.deepEqual(events, ["cash-movement", "daily-close"]);
});

test("现金进出失败（抛异常）不阻塞日结，也不让 drain 失败", async () => {
  const events: string[] = [];
  const report = await coordinator({
    cashMovementUpload: new FakeUpload(() => { throw new Error("cash movement storage unavailable"); }),
    dailyCloseUpload: new FakeUpload(() => { events.push("daily-close"); }),
  }).requestDrain();
  assert.deepEqual(events, ["daily-close"]);
  assert.equal(report.orderSucceeded, 0);
});

test("日结失败不影响现金进出；两者都没注入时行为与以前一致", async () => {
  const cash = new FakeUpload();
  await coordinator({
    cashMovementUpload: cash,
    dailyCloseUpload: new FakeUpload(() => { throw new Error("boom"); }),
  }).requestDrain();
  assert.equal(cash.drains, 1);
  await coordinator({}).requestDrain();
});

test("现金进出与开关无关：协调器不读取任何开关，注入即上传", async () => {
  const cash = new FakeUpload();
  await coordinator({ cashMovementUpload: cash }).requestDrain();
  assert.equal(cash.drains, 1);
});

test("shutdown 后现金进出 drain 收到停止信号，且不再被调用", async () => {
  const cash = new FakeUpload();
  const target = coordinator({ cashMovementUpload: cash });
  await target.requestDrain();
  assert.equal(cash.drains, 1);
  assert.equal(cash.controls[0]?.shouldStop?.(), false);
  await target.shutdown();
  assert.equal(cash.controls[0]?.shouldStop?.(), true, "已在执行的 drain 能感知关闭");
  await target.requestDrain();
  assert.equal(cash.drains, 1, "关闭后不再访问现金进出队列");
});

test("现金进出重试时间参与定时唤醒（至少晚 1 秒），与日结取最早者；读取失败被吞掉", async () => {
  const scheduled: number[] = [];
  const timer = {
    set(delayMs: number) { scheduled.push(delayMs); return scheduled.length; },
    clear() {},
  };
  await coordinator({
    timer,
    cashMovementUpload: new FakeUpload(undefined, () => "2026-11-14T00:00:05.000Z"),
  }).requestDrain();
  await coordinator({
    timer,
    cashMovementUpload: new FakeUpload(undefined, () => "2026-11-13T00:00:00.000Z"),
  }).requestDrain();
  await coordinator({
    timer,
    cashMovementUpload: new FakeUpload(undefined, () => "2026-11-14T00:00:30.000Z"),
    dailyCloseUpload: new FakeUpload(undefined, () => "2026-11-14T00:01:00.000Z"),
  }).requestDrain();
  await coordinator({
    timer,
    cashMovementUpload: new FakeUpload(undefined, () => { throw new Error("sqlite closed"); }),
  }).requestDrain();
  assert.deepEqual(scheduled, [5_000, 1_000, 30_000]);
});
