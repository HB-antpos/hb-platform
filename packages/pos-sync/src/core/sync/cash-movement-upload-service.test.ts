import assert from "node:assert/strict";
import test from "node:test";

import type { LocalCashMovement } from "@hb/pos-domain/core/contracts/cash-movement";
import type {
  CashMovementClaimResult,
  CashMovementUploadRepositoryPort,
  CashMovementUploadScope,
} from "@hb/pos-domain/core/contracts/cash-movement-upload";

import { cashMovementFixture } from "../../testing/cash-movement-fixtures";

import {
  buildCashMovementSyncRequest,
  cashMovementSyncRequestBytes,
  mapCashMovementToSyncItem,
  type CashMovementSyncRequest,
} from "./cash-movement-sync-request";
import {
  CashMovementUploadService,
  CASH_MOVEMENT_UNAUTHORIZED_COOLDOWN_SECONDS,
} from "./cash-movement-upload-service";
import type {
  CashMovementItemResult,
  CashMovementSyncPort,
  CashMovementSyncResult,
} from "./hbpos-cash-movement-sync-adapter";

const SCOPE = { storeCode: "S001", deviceCode: "DEV-1" } as const;
const T0 = new Date("2026-11-14T06:00:00.000Z");

const guid = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;

type Row = {
  movement: LocalCashMovement;
  state: "pending" | "uploading" | "synced" | "rejected";
  attempts: number;
  nextAttemptAtIso: string | null;
  errorCode: string | null;
};

/** 按端口语义实现的内存仓储（真实 SQLite 实现的语义由 pos-db 的测试覆盖）。 */
class FakeRepository implements CashMovementUploadRepositoryPort {
  public readonly rows = new Map<string, Row>();
  public readonly calls: string[] = [];
  public claimCalls = 0;
  public readonly releasedNext: string[] = [];
  public failMarkSynced = false;

  public add(n: number, overrides: Partial<LocalCashMovement> = {}): void {
    const movement = cashMovementFixture({
      movementGuid: guid(n),
      displayNumber: `CM-${String(n).padStart(4, "0")}`,
      occurredAtIso: new Date(Date.UTC(2026, 10, 14, 1, 0, n)).toISOString(),
      ...overrides,
    });
    this.rows.set(movement.movementGuid, {
      movement,
      state: "pending",
      attempts: 0,
      nextAttemptAtIso: null,
      errorCode: null,
    });
  }

  public row(n: number): Row {
    const row = this.rows.get(guid(n));
    assert.ok(row);
    return row;
  }

  public async recoverExpiredUploading(): Promise<number> {
    this.calls.push("recover");
    return 0;
  }

  public async claimDueBatch(
    scope: CashMovementUploadScope,
    limit: number,
    nowIso: string,
  ): Promise<CashMovementClaimResult> {
    this.claimCalls += 1;
    const due = [...this.rows.values()]
      .filter(
        (row) =>
          row.state === "pending" &&
          row.movement.storeCode === scope.storeCode &&
          (row.nextAttemptAtIso === null || row.nextAttemptAtIso <= nowIso),
      )
      .sort((a, b) => a.movement.occurredAtIso.localeCompare(b.movement.occurredAtIso))
      .slice(0, limit);
    for (const row of due) {
      row.state = "uploading";
      row.attempts += 1;
      row.nextAttemptAtIso = null;
    }
    return { claimed: due.map((row) => ({ movement: row.movement, attemptCount: row.attempts })), rejectedLocally: 0 };
  }

  public async markSynced(guids: readonly string[]): Promise<void> {
    if (this.failMarkSynced) throw new Error("db locked");
    for (const g of guids) {
      const row = this.rows.get(g);
      if (row) row.state = "synced";
    }
  }

  public async markRetry(g: string, next: string, code: string): Promise<void> {
    const row = this.rows.get(g);
    if (row?.state !== "uploading") return;
    row.state = "pending";
    row.nextAttemptAtIso = next;
    row.errorCode = code;
  }

  public async releaseWithoutAttempt(guids: readonly string[], next: string, code: string): Promise<void> {
    for (const g of guids) {
      const row = this.rows.get(g);
      if (row?.state !== "uploading") continue;
      row.state = "pending";
      row.attempts = Math.max(0, row.attempts - 1);
      row.nextAttemptAtIso = next;
      row.errorCode = code;
      this.releasedNext.push(next);
    }
  }

  public async markRejected(g: string, code: string): Promise<void> {
    const row = this.rows.get(g);
    if (row?.state !== "uploading") return;
    row.state = "rejected";
    row.errorCode = code;
  }

  public async nextReadyAtIso(): Promise<string | null> {
    this.calls.push("nextReady");
    return "2026-11-14T06:30:00.000Z";
  }
}

class ScriptedSync implements CashMovementSyncPort {
  public readonly requests: CashMovementSyncRequest[] = [];
  public constructor(
    private readonly respond: (request: CashMovementSyncRequest, index: number) => CashMovementSyncResult,
  ) {}
  public async sync(request: CashMovementSyncRequest): Promise<CashMovementSyncResult> {
    const index = this.requests.length;
    this.requests.push(request);
    return this.respond(request, index);
  }
}

const allSynced = (request: CashMovementSyncRequest): CashMovementSyncResult => ({
  kind: "results",
  items: (request.movements ?? []).map((item) => ({
    movementGuid: item.movementGuid ?? "",
    outcome: "synced",
    code: null,
    message: null,
  })),
});

function service(
  repository: FakeRepository,
  sync: CashMovementSyncPort,
  extra: Partial<ConstructorParameters<typeof CashMovementUploadService>[0]> = {},
): CashMovementUploadService {
  return new CashMovementUploadService({
    repository,
    sync,
    scope: () => SCOPE,
    clientKind: "Ipad",
    appVersion: "1.0.13",
    now: () => T0,
    ...extra,
  });
}

test("按 occurred_at 升序上传（与插入顺序无关），Accepted / AlreadySynced 都记为已同步", async () => {
  const repository = new FakeRepository();
  repository.add(3);
  repository.add(1);
  repository.add(2);
  const sync = new ScriptedSync(allSynced);
  const result = await service(repository, sync).drain();
  assert.deepEqual(
    sync.requests[0]?.movements?.map((item) => item.movementGuid),
    [guid(1), guid(2), guid(3)],
  );
  assert.equal(sync.requests[0]?.storeCode, "S001");
  assert.equal(sync.requests[0]?.clientKind, "Ipad");
  assert.equal(sync.requests[0]?.appVersion, "1.0.13");
  assert.deepEqual(result, { attempted: 3, uploaded: 3, rejected: 0, skipped: 0, deferred: 0, interrupted: false });
  for (const n of [1, 2, 3]) assert.equal(repository.row(n).state, "synced");
  assert.equal(sync.requests.length, 1);
});

test("单条结果：永久拒绝标 rejected；VOIDED_MOVEMENT_NOT_FOUND / CONCURRENT_UPDATE / 缺失回执按尝试次数退避重试", async () => {
  const repository = new FakeRepository();
  for (const n of [1, 2, 3, 4, 5]) repository.add(n);
  repository.row(3).attempts = 3; // 认领后第 4 次尝试 → 退避 40s
  const items: Record<number, CashMovementItemResult | null> = {
    1: { movementGuid: guid(1), outcome: "synced", code: null, message: null },
    2: { movementGuid: guid(2), outcome: "rejected", code: "INVALID_AMOUNT", message: "bad" },
    3: { movementGuid: guid(3), outcome: "retry", code: "VOIDED_MOVEMENT_NOT_FOUND", message: "wait" },
    4: { movementGuid: guid(4), outcome: "retry", code: "CASH_MOVEMENT_CONCURRENT_UPDATE", message: "retry" },
    5: null, // 响应里缺失
  };
  const sync = new ScriptedSync(() => ({
    kind: "results",
    items: Object.values(items).filter((item): item is CashMovementItemResult => item !== null),
  }));
  const result = await service(repository, sync).drain();
  assert.equal(repository.row(1).state, "synced");
  assert.equal(repository.row(2).state, "rejected");
  assert.equal(repository.row(2).errorCode, "INVALID_AMOUNT");
  assert.equal(repository.row(3).state, "pending");
  assert.equal(repository.row(3).nextAttemptAtIso, "2026-11-14T06:00:40.000Z");
  assert.equal(repository.row(4).nextAttemptAtIso, "2026-11-14T06:00:05.000Z");
  assert.equal(repository.row(5).errorCode, "SYNC_RESULT_MISSING");
  assert.equal(repository.row(5).nextAttemptAtIso, "2026-11-14T06:00:05.000Z");
  assert.equal(result.uploaded, 1);
  assert.equal(result.rejected, 1);
  assert.equal(result.deferred, 3);
  assert.equal(result.interrupted, false);
});

test("整批可重试失败（网络 / 5xx / 429）：全部带退避回 pending，本轮结束不再继续发后面的批次", async () => {
  const repository = new FakeRepository();
  for (const n of [1, 2, 3]) repository.add(n);
  const sync = new ScriptedSync(() => ({ kind: "retry", code: "HTTP_503", message: "unavailable" }));
  const result = await service(repository, sync, { batchSize: 2 }).drain();
  assert.equal(sync.requests.length, 1, "失败后不再发下一批");
  assert.equal(repository.row(1).state, "pending");
  assert.equal(repository.row(1).errorCode, "HTTP_503");
  assert.equal(repository.row(1).nextAttemptAtIso, "2026-11-14T06:00:05.000Z");
  assert.equal(repository.row(3).attempts, 0, "没发出的记录不消耗尝试次数");
  assert.equal(result.deferred, 2);
  assert.equal(result.interrupted, false);
});

test("401 / 403：释放租约、撤销尝试次数、60 秒冷却并中断本批", async () => {
  const repository = new FakeRepository();
  repository.add(1);
  repository.add(2);
  const sync = new ScriptedSync(() => ({ kind: "unauthorized", code: "HTTP_401", message: "expired" }));
  const result = await service(repository, sync).drain();
  assert.equal(result.interrupted, true);
  assert.equal(result.deferred, 2);
  for (const n of [1, 2]) {
    assert.equal(repository.row(n).state, "pending", "租约已释放");
    assert.equal(repository.row(n).attempts, 0, "不消耗尝试次数");
    assert.equal(repository.row(n).nextAttemptAtIso, new Date(T0.getTime() + CASH_MOVEMENT_UNAUTHORIZED_COOLDOWN_SECONDS * 1_000).toISOString());
  }
  assert.equal(CASH_MOVEMENT_UNAUTHORIZED_COOLDOWN_SECONDS, 60);
});

test("整批 400：本批全部 rejected，之后继续处理后面的批次", async () => {
  const repository = new FakeRepository();
  for (const n of [1, 2, 3]) repository.add(n);
  const sync = new ScriptedSync((request, index) =>
    index === 0 ? { kind: "rejected", code: "INVALID_BATCH_SIZE", message: "bad" } : allSynced(request),
  );
  const result = await service(repository, sync, { batchSize: 2 }).drain();
  assert.equal(repository.row(1).state, "rejected");
  assert.equal(repository.row(2).state, "rejected");
  assert.equal(repository.row(1).errorCode, "INVALID_BATCH_SIZE");
  assert.equal(repository.row(3).state, "synced");
  assert.deepEqual({ rejected: result.rejected, uploaded: result.uploaded }, { rejected: 2, uploaded: 1 });
});

test("拆批：每个请求 <= 100 条（150 条分成 100 + 50）", async () => {
  const repository = new FakeRepository();
  for (let n = 1; n <= 150; n += 1) repository.add(n);
  const sync = new ScriptedSync(allSynced);
  const result = await service(repository, sync, { batchSize: 500 }).drain();
  assert.deepEqual(sync.requests.map((request) => request.movements?.length), [100, 50]);
  assert.equal(result.uploaded, 150);
});

test("拆批：超过字节上限时多出的记录退还租约（不算尝试），下一轮继续按顺序发完", async () => {
  const repository = new FakeRepository();
  for (let n = 1; n <= 5; n += 1) repository.add(n);
  // 用真实请求大小定出「刚好容纳 2 条」的上限：先发 2 条前缀，其余退还后再认领。
  const itemsOf = (ns: readonly number[]) =>
    ns.map((n) => mapCashMovementToSyncItem(repository.row(n).movement));
  const limit = cashMovementSyncRequestBytes(
    buildCashMovementSyncRequest(itemsOf([1, 2]), {
      storeCode: SCOPE.storeCode,
      deviceCode: SCOPE.deviceCode,
      clientKind: "Ipad",
      appVersion: "1.0.13",
    }),
  );
  const sync = new ScriptedSync(allSynced);
  const result = await service(repository, sync, { maximumRequestBytes: limit }).drain();
  assert.deepEqual(
    sync.requests.map((request) => request.movements?.map((item) => item.movementGuid)),
    [[guid(1), guid(2)], [guid(3), guid(4)], [guid(5)]],
  );
  assert.equal(result.uploaded, 5);
  for (const n of [1, 2, 3, 4, 5]) assert.equal(repository.row(n).attempts, 1, "退还的记录只算一次真实尝试");
});

test("本地无法映射的记录：永久拒绝且不发请求；同批其它记录照常上传", async () => {
  const repository = new FakeRepository();
  repository.add(1, { occurredAtIso: "garbage" });
  repository.add(2);
  const sync = new ScriptedSync(allSynced);
  const result = await service(repository, sync).drain();
  assert.equal(repository.row(1).state, "rejected");
  assert.equal(repository.row(1).errorCode, "CASH_MOVEMENT_OCCURRED_AT_INVALID");
  assert.deepEqual(sync.requests[0]?.movements?.map((item) => item.movementGuid), [guid(2)]);
  assert.equal(result.rejected, 1);
  assert.equal(result.uploaded, 1);
});

test("没有设备授权范围：不认领、不发请求、不消耗尝试次数", async () => {
  const repository = new FakeRepository();
  repository.add(1);
  const sync = new ScriptedSync(allSynced);
  for (const scope of [null, { storeCode: " ", deviceCode: "DEV-1" }, { storeCode: "S001", deviceCode: "" }]) {
    const result = await service(repository, sync, { scope: () => scope }).drain();
    assert.equal(result.attempted, 0);
  }
  assert.equal(repository.claimCalls, 0);
  assert.equal(sync.requests.length, 0);
  assert.equal(await service(repository, sync, { scope: () => null }).nextReadyAtIso(), null);
  assert.equal(await service(repository, sync).nextReadyAtIso(), "2026-11-14T06:30:00.000Z");
});

test("收到停止信号后不再认领新记录", async () => {
  const repository = new FakeRepository();
  repository.add(1);
  const sync = new ScriptedSync(allSynced);
  await service(repository, sync).drain({ shouldStop: () => true });
  assert.equal(repository.claimCalls, 0);
  assert.equal(repository.row(1).state, "pending");
});

test("上传过程抛异常：带退避回 pending，不抛出，不影响调用方", async () => {
  const repository = new FakeRepository();
  repository.add(1);
  const sync: CashMovementSyncPort = {
    async sync() {
      throw new Error("adapter bug");
    },
  };
  const result = await service(repository, sync).drain();
  assert.equal(repository.row(1).state, "pending");
  assert.equal(repository.row(1).errorCode, "UPLOAD_EXCEPTION");
  assert.equal(result.deferred, 1);

  // 写终态时仓储抛错（如 SQLite 瞬时锁）：同样不抛出。
  const lockedRepository = new FakeRepository();
  lockedRepository.add(1);
  lockedRepository.failMarkSynced = true;
  const lockedResult = await service(lockedRepository, new ScriptedSync(allSynced)).drain();
  assert.equal(lockedResult.deferred, 1);
});

test("每次 drain 先回收过期租约；退避 5s→10s→…→300s 封顶", async () => {
  const repository = new FakeRepository();
  await service(repository, new ScriptedSync(allSynced)).drain();
  assert.equal(repository.calls[0], "recover");

  const delays: number[] = [];
  for (const attempts of [1, 2, 3, 4, 5, 6, 7, 8, 20]) {
    const r = new FakeRepository();
    r.add(1);
    r.row(1).attempts = attempts - 1;
    await service(r, new ScriptedSync(() => ({ kind: "retry", code: "X", message: "x" }))).drain();
    delays.push((Date.parse(r.row(1).nextAttemptAtIso ?? "") - T0.getTime()) / 1_000);
  }
  assert.deepEqual(delays, [5, 10, 20, 40, 80, 160, 300, 300, 300]);
});
