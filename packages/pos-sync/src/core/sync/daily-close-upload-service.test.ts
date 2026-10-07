import assert from "node:assert/strict";
import test from "node:test";

import type { DailyCloseArchive } from "@hb/pos-domain/core/contracts/daily-close";
import type {
  DailyCloseUploadLease,
  DailyCloseUploadRepositoryPort,
  DailyCloseUploadScope,
  DailyCloseUploadState,
} from "@hb/pos-domain/core/contracts/daily-close-upload";

import { archiveFixture } from "../../testing/daily-close-fixtures";
import { validateAgainstServerRules } from "../../testing/daily-close-server-rules";
import type { DailyCloseSyncRequest } from "./daily-close-sync-request";
import {
  DAILY_CLOSE_UNAUTHORIZED_COOLDOWN_SECONDS,
  DailyCloseUploadService,
  dailyCloseUploadRetryDelaySeconds,
} from "./daily-close-upload-service";
import type {
  DailyCloseSyncPort,
  DailyCloseSyncResult,
} from "./hbpos-daily-close-sync-adapter";

const NOW = new Date("2026-10-07T01:00:00.000Z");
const SCOPE: DailyCloseUploadScope = { storeCode: "S001", deviceCode: "DEV-1" };

const guid = (index: number) =>
  `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`;

type Row = {
  archive: DailyCloseArchive;
  state: DailyCloseUploadState;
  attempts: number;
  nextAttemptAtIso: string | null;
  lastAttemptAtIso: string | null;
  errorCode: string | null;
  errorMessage: string | null;
  uploadedAtIso: string | null;
};

/** 与 SQLite 实现同语义的内存仓储：只验证上传服务的流程，SQL 语义由真实 SQLite 用例覆盖。 */
class MemoryUploadRepository implements DailyCloseUploadRepositoryPort {
  public readonly rows = new Map<string, Row>();
  public readonly listDueCalls: { scope: DailyCloseUploadScope; limit: number }[] = [];
  public readonly recoverCalls: { staleBeforeIso: string; nextAttemptAtIso: string }[] = [];
  public failClaimFor = new Set<string>();
  public failReadFor = new Set<string>();

  public add(archive: DailyCloseArchive, overrides: Partial<Row> = {}): void {
    this.rows.set(archive.closeId, {
      archive,
      state: "pending",
      attempts: 0,
      nextAttemptAtIso: null,
      lastAttemptAtIso: null,
      errorCode: null,
      errorMessage: null,
      uploadedAtIso: null,
      ...overrides,
    });
  }

  public row(closeId: string): Row {
    const row = this.rows.get(closeId);
    assert.ok(row, `缺少 ${closeId}`);
    return row;
  }

  public async recoverExpiredUploading(input: {
    staleBeforeIso: string;
    nextAttemptAtIso: string;
  }): Promise<number> {
    this.recoverCalls.push(input);
    let count = 0;
    for (const row of this.rows.values()) {
      if (
        row.state === "uploading" &&
        (row.lastAttemptAtIso === null || row.lastAttemptAtIso <= input.staleBeforeIso)
      ) {
        row.state = "pending";
        row.nextAttemptAtIso = input.nextAttemptAtIso;
        count += 1;
      }
    }
    return count;
  }

  public async listDue(scope: DailyCloseUploadScope, limit: number, nowIso: string) {
    this.listDueCalls.push({ scope, limit });
    return [...this.rows.entries()]
      .filter(
        ([, row]) =>
          row.state === "pending" &&
          row.archive.storeCode === scope.storeCode &&
          row.archive.deviceCode === scope.deviceCode &&
          (row.nextAttemptAtIso === null || row.nextAttemptAtIso <= nowIso),
      )
      .slice(0, limit)
      .map(([closeId]) => closeId);
  }

  public async tryClaim(
    closeId: string,
    scope: DailyCloseUploadScope,
    attemptedAtIso: string,
  ): Promise<DailyCloseUploadLease | null> {
    if (this.failClaimFor.has(closeId)) throw new Error("SQLITE_BUSY");
    const row = this.rows.get(closeId);
    if (
      !row ||
      row.state !== "pending" ||
      row.archive.storeCode !== scope.storeCode ||
      row.archive.deviceCode !== scope.deviceCode ||
      (row.nextAttemptAtIso !== null && row.nextAttemptAtIso > attemptedAtIso)
    ) {
      return null;
    }
    row.state = "uploading";
    row.attempts += 1;
    row.lastAttemptAtIso = attemptedAtIso;
    row.nextAttemptAtIso = null;
    return { closeId, attemptCount: row.attempts };
  }

  public async readArchive(closeId: string): Promise<DailyCloseArchive | null> {
    if (this.failReadFor.has(closeId)) throw new Error("archive corrupt");
    return this.rows.get(closeId)?.archive ?? null;
  }

  public async markSucceeded(closeId: string, uploadedAtIso: string): Promise<void> {
    const row = this.row(closeId);
    row.state = "synced";
    row.uploadedAtIso = uploadedAtIso;
    row.nextAttemptAtIso = null;
    row.errorCode = null;
  }

  public async markPending(closeId: string, nextAttemptAtIso: string, code: string, message: string) {
    const row = this.row(closeId);
    if (row.state !== "uploading") return;
    row.state = "pending";
    row.nextAttemptAtIso = nextAttemptAtIso;
    row.errorCode = code;
    row.errorMessage = message;
  }

  public async releaseWithoutAttempt(closeId: string, nextAttemptAtIso: string, code: string, message: string) {
    const row = this.row(closeId);
    if (row.state !== "uploading") return;
    row.state = "pending";
    row.attempts = Math.max(0, row.attempts - 1);
    row.nextAttemptAtIso = nextAttemptAtIso;
    row.errorCode = code;
    row.errorMessage = message;
  }

  public async markRejected(closeId: string, code: string, message: string) {
    const row = this.row(closeId);
    if (row.state !== "uploading") return;
    row.state = "rejected";
    row.nextAttemptAtIso = null;
    row.errorCode = code;
    row.errorMessage = message;
  }

  public async markSkipped(closeId: string, code: string, message: string) {
    const row = this.row(closeId);
    if (row.state !== "pending" && row.state !== "uploading") return;
    row.state = "skipped";
    row.nextAttemptAtIso = null;
    row.errorCode = code;
    row.errorMessage = message;
  }

  public async nextReadyAtIso(): Promise<string | null> {
    const times = [...this.rows.values()]
      .filter((row) => row.state === "pending" && row.nextAttemptAtIso !== null)
      .map((row) => row.nextAttemptAtIso!)
      .sort();
    return times[0] ?? null;
  }
}

class ScriptedSync implements DailyCloseSyncPort {
  public readonly requests: DailyCloseSyncRequest[] = [];
  public constructor(
    private readonly handler: (
      request: DailyCloseSyncRequest,
      index: number,
    ) => DailyCloseSyncResult | Promise<DailyCloseSyncResult>,
  ) {}
  public async sync(request: DailyCloseSyncRequest): Promise<DailyCloseSyncResult> {
    const index = this.requests.length;
    this.requests.push(request);
    return this.handler(request, index);
  }
}

const OK: DailyCloseSyncResult = { kind: "synced", outcome: "accepted" };

function makeService(
  repository: MemoryUploadRepository,
  sync: DailyCloseSyncPort,
  overrides: {
    scope?: () => DailyCloseUploadScope | null;
    now?: () => Date;
    batchSize?: number;
  } = {},
) {
  return new DailyCloseUploadService({
    repository,
    sync,
    scope: overrides.scope ?? (() => SCOPE),
    clientKind: "Handheld",
    appVersion: "0.1.2",
    now: overrides.now ?? (() => NOW),
    ...(overrides.batchSize === undefined ? {} : { batchSize: overrides.batchSize }),
  });
}

function archiveWithId(closeId: string, overrides: Partial<DailyCloseArchive> = {}) {
  return archiveFixture({ closeId, ...overrides });
}

test("pending 日结上传成功：请求带 Handheld / appVersion，状态变 synced 并记录上传时间", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)));
  const sync = new ScriptedSync(() => OK);

  const result = await makeService(repository, sync).drain();

  assert.deepEqual(result, {
    attempted: 1,
    uploaded: 1,
    rejected: 0,
    skipped: 0,
    deferred: 0,
    interrupted: false,
  });
  assert.equal(sync.requests.length, 1);
  assert.equal(sync.requests[0]?.clientKind, "Handheld");
  assert.equal(sync.requests[0]?.appVersion, "0.1.2");
  assert.equal(sync.requests[0]?.dailyCloseGuid, guid(1));
  assert.deepEqual(validateAgainstServerRules(sync.requests[0], SCOPE), { ok: true });
  const row = repository.row(guid(1));
  assert.equal(row.state, "synced");
  assert.equal(row.uploadedAtIso, NOW.toISOString());
  assert.equal(row.attempts, 1);
});

test("历史补传：批次 20 条循环取空，直到所有到期日结都已上传", async () => {
  const repository = new MemoryUploadRepository();
  for (let index = 1; index <= 45; index += 1) repository.add(archiveWithId(guid(index)));
  const sync = new ScriptedSync(() => OK);

  const result = await makeService(repository, sync).drain();

  assert.equal(result.uploaded, 45);
  assert.equal(sync.requests.length, 45);
  assert.ok(repository.listDueCalls.length >= 3);
  assert.ok(repository.listDueCalls.every((call) => call.limit === 20));
  assert.equal([...repository.rows.values()].every((row) => row.state === "synced"), true);
});

test("只上传当前设备授权范围内的日结，其它范围保持 pending 且未被认领", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)));
  repository.add(archiveWithId(guid(2), { deviceCode: "OLD-DEVICE" }));
  repository.add(archiveWithId(guid(3), { storeCode: "OTHER" }));
  const sync = new ScriptedSync(() => OK);

  await makeService(repository, sync).drain();

  assert.deepEqual(sync.requests.map((request) => request.dailyCloseGuid), [guid(1)]);
  assert.equal(repository.row(guid(2)).state, "pending");
  assert.equal(repository.row(guid(2)).attempts, 0);
  assert.equal(repository.row(guid(3)).state, "pending");
  assert.ok(repository.listDueCalls.every((call) => call.scope.deviceCode === "DEV-1"));
});

test("没有设备授权范围：不上传、不消耗尝试次数", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)));
  const sync = new ScriptedSync(() => OK);

  for (const scope of [null, { storeCode: "S001", deviceCode: " " }, { storeCode: "", deviceCode: "D" }]) {
    const result = await makeService(repository, sync, { scope: () => scope }).drain();
    assert.equal(result.attempted, 0);
  }
  assert.equal(sync.requests.length, 0);
  assert.equal(repository.row(guid(1)).attempts, 0);
  assert.equal(repository.listDueCalls.length, 0);
});

test("401/403 保持 pending、撤销本次尝试计数并中断本批次，后面的日结原样不动", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)));
  repository.add(archiveWithId(guid(2)));
  repository.add(archiveWithId(guid(3)));
  const sync = new ScriptedSync((_request, index) =>
    index === 1
      ? { kind: "unauthorized", code: "HTTP_403", message: "device forbidden" }
      : OK,
  );

  const result = await makeService(repository, sync).drain();

  assert.equal(result.interrupted, true);
  assert.equal(sync.requests.length, 2, "第 2 条 401 之后不再发请求");
  assert.equal(repository.row(guid(1)).state, "synced");
  const blocked = repository.row(guid(2));
  assert.equal(blocked.state, "pending");
  assert.equal(blocked.attempts, 0, "撤销本次尝试计数");
  assert.equal(blocked.errorCode, "HTTP_403");
  assert.equal(
    blocked.nextAttemptAtIso,
    new Date(NOW.getTime() + DAILY_CLOSE_UNAUTHORIZED_COOLDOWN_SECONDS * 1_000).toISOString(),
    "短冷却防止调度器 0ms 自旋",
  );
  const untouched = repository.row(guid(3));
  assert.equal(untouched.state, "pending");
  assert.equal(untouched.attempts, 0);
  assert.equal(untouched.lastAttemptAtIso, null);
});

test("永久拒绝：400 / 范围冲突 / 内容冲突 → rejected 并记录错误码，不再重试", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)));
  repository.add(archiveWithId(guid(2)));
  const sync = new ScriptedSync((_request, index) => ({
    kind: "rejected",
    code: index === 0 ? "INVALID_COUNTED_CASH_AMOUNT" : "DAILY_CLOSE_CONTENT_CONFLICT",
    message: "no",
  }));

  const result = await makeService(repository, sync).drain();
  assert.equal(result.rejected, 2);
  assert.equal(repository.row(guid(1)).state, "rejected");
  assert.equal(repository.row(guid(1)).errorCode, "INVALID_COUNTED_CASH_AMOUNT");
  assert.equal(repository.row(guid(2)).errorCode, "DAILY_CLOSE_CONTENT_CONFLICT");

  // 再跑一轮：rejected 不会被重新认领。
  await makeService(repository, sync).drain();
  assert.equal(sync.requests.length, 2);
});

test("可重试失败：回 pending 带 5s→10s→…→300s 退避，不丢数据", async () => {
  const delays = [5, 10, 20, 40, 80, 160, 300, 300, 300];
  assert.deepEqual(
    delays.map((_, index) => dailyCloseUploadRetryDelaySeconds(index + 1)),
    delays,
  );
  assert.equal(dailyCloseUploadRetryDelaySeconds(0), 5);

  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)));
  const sync = new ScriptedSync(() => ({ kind: "retry", code: "HTTP_404", message: "not deployed" }));
  let clock = NOW.getTime();
  const service = makeService(repository, sync, { now: () => new Date(clock) });

  for (const delaySeconds of delays) {
    const result = await service.drain();
    assert.equal(result.deferred, 1);
    const row = repository.row(guid(1));
    assert.equal(row.state, "pending");
    assert.equal(row.errorCode, "HTTP_404");
    assert.equal(row.nextAttemptAtIso, new Date(clock + delaySeconds * 1_000).toISOString());
    // 未到期时再 drain 不会再次尝试。
    const before = sync.requests.length;
    await service.drain();
    assert.equal(sync.requests.length, before);
    clock += delaySeconds * 1_000;
  }
  assert.equal(repository.row(guid(1)).attempts, delays.length);
});

test("非 GUID 的遗留 closeId：标记 skipped，不认领、不发请求", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId("legacy-close-1"));
  repository.add(archiveWithId(guid(2)));
  const sync = new ScriptedSync(() => OK);

  const result = await makeService(repository, sync).drain();

  assert.equal(result.skipped, 1);
  assert.equal(result.uploaded, 1);
  assert.equal(repository.row("legacy-close-1").state, "skipped");
  assert.equal(repository.row("legacy-close-1").attempts, 0);
  assert.equal(repository.row("legacy-close-1").errorCode, "DAILY_CLOSE_GUID_INVALID");
  assert.deepEqual(sync.requests.map((request) => request.dailyCloseGuid), [guid(2)]);
});

test("本地数据无法映射：永久 rejected，不发请求（不修正本地数据）", async () => {
  const repository = new MemoryUploadRepository();
  const broken = archiveWithId(guid(1));
  repository.add({ ...broken, periodToIso: "garbage" });
  const sync = new ScriptedSync(() => OK);

  const result = await makeService(repository, sync).drain();

  assert.equal(result.rejected, 1);
  assert.equal(sync.requests.length, 0);
  assert.equal(repository.row(guid(1)).state, "rejected");
  assert.equal(repository.row(guid(1)).errorCode, "DAILY_CLOSE_TIMESTAMP_INVALID");
});

test("每条日结独立处理：一条读取/认领异常不影响其余，且不会死循环", async () => {
  const repository = new MemoryUploadRepository();
  for (let index = 1; index <= 4; index += 1) repository.add(archiveWithId(guid(index)));
  repository.failReadFor.add(guid(2));
  repository.failClaimFor.add(guid(3));
  const sync = new ScriptedSync(() => OK);

  const result = await makeService(repository, sync).drain();

  assert.equal(result.uploaded, 2);
  assert.equal(result.deferred, 2);
  assert.equal(repository.row(guid(1)).state, "synced");
  assert.equal(repository.row(guid(4)).state, "synced");
  // 读取异常：回 pending 带退避；认领异常：行没动过（仍 pending、尝试 0）。
  assert.equal(repository.row(guid(2)).state, "pending");
  assert.equal(repository.row(guid(2)).errorCode, "UPLOAD_EXCEPTION");
  assert.equal(repository.row(guid(3)).state, "pending");
  assert.equal(repository.row(guid(3)).attempts, 0);
});

test("同步端口抛出异常也只延后这一条（保持 pending 带退避）", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)));
  repository.add(archiveWithId(guid(2)));
  const sync = new ScriptedSync((_request, index) => {
    if (index === 0) throw new Error("adapter bug");
    return OK;
  });

  const result = await makeService(repository, sync).drain();

  assert.equal(result.uploaded, 1);
  assert.equal(result.deferred, 1);
  assert.equal(repository.row(guid(1)).state, "pending");
  assert.equal(repository.row(guid(1)).errorCode, "UPLOAD_EXCEPTION");
  assert.equal(repository.row(guid(2)).state, "synced");
});

test("每轮开始先回收超过 2 分钟租约的 uploading，进程崩溃遗留的日结会被重新排队并上传", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)), {
    state: "uploading",
    attempts: 1,
    lastAttemptAtIso: new Date(NOW.getTime() - 3 * 60_000).toISOString(),
  });
  repository.add(archiveWithId(guid(2)), {
    state: "uploading",
    attempts: 1,
    lastAttemptAtIso: new Date(NOW.getTime() - 30_000).toISOString(),
  });
  const sync = new ScriptedSync(() => OK);

  await makeService(repository, sync).drain();

  assert.deepEqual(repository.recoverCalls, [
    {
      staleBeforeIso: new Date(NOW.getTime() - 120_000).toISOString(),
      nextAttemptAtIso: NOW.toISOString(),
    },
  ]);
  assert.equal(repository.row(guid(1)).state, "synced");
  assert.equal(repository.row(guid(2)).state, "uploading", "未过期的租约不能被抢走");
});

test("shouldStop 为真时立即停止取新记录", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)));
  repository.add(archiveWithId(guid(2)));
  let stopped = false;
  const service = makeService(
    repository,
    new ScriptedSync(() => {
      stopped = true;
      return OK;
    }),
  );

  const result = await service.drain({ shouldStop: () => stopped });

  assert.equal(result.uploaded, 1);
  assert.equal(repository.row(guid(2)).state, "pending");
});

test("nextReadyAtIso 取当前授权范围；无范围时为 null", async () => {
  const repository = new MemoryUploadRepository();
  repository.add(archiveWithId(guid(1)), { nextAttemptAtIso: "2026-10-07T01:05:00.000Z" });
  const sync = new ScriptedSync(() => OK);
  assert.equal(
    await makeService(repository, sync).nextReadyAtIso(),
    "2026-10-07T01:05:00.000Z",
  );
  assert.equal(
    await makeService(repository, sync, { scope: () => null }).nextReadyAtIso(),
    null,
  );
});
