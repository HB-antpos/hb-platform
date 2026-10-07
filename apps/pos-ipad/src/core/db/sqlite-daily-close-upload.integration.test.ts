import assert from "node:assert/strict";
import { DatabaseSync, type SQLInputValue } from "node:sqlite";
import test from "node:test";

import type {
  DailyCloseArchive,
  DailyCloseArchiveCommit,
} from "@hb/pos-domain/core/contracts/daily-close";
import { SqliteDailyCloseRepository } from "@hb/pos-db/core/db/sqlite-daily-close-repository";
import { SqliteDailyCloseUploadRepository } from "@hb/pos-db/core/db/sqlite-daily-close-upload-repository";
import type {
  SqliteConnectionPort,
  SqlRunResult,
  SqlValue,
} from "@hb/pos-db/core/db/types";
import { DailyCloseUploadService } from "@hb/pos-sync/core/sync/daily-close-upload-service";
import type {
  DailyCloseSyncPort,
  DailyCloseSyncResult,
} from "@hb/pos-sync/core/sync/hbpos-daily-close-sync-adapter";
import type { DailyCloseSyncRequest } from "@hb/pos-sync/core/sync/daily-close-sync-request";
import { PosSyncCoordinator } from "@hb/pos-sync/core/sync/sync-coordinator";
import {
  DAILY_CLOSE_FIXTURE_SCOPE,
  archiveFixture,
} from "@hb/pos-sync/testing/daily-close-fixtures";
import { validateAgainstServerRules } from "@hb/pos-sync/testing/daily-close-server-rules";

import { buildDailyCloseArchiveCommit } from "../../features/daily-close/daily-close-domain";
import { applyMigrations, POS_DATABASE_MIGRATIONS } from "./migrations";

/** 两端同一份用例：只有 clientKind 与“上传迁移的版本号”不同，其余行为必须完全一致。 */
const CLIENT_KIND = "Ipad" as const;
const UPLOAD_MIGRATION = POS_DATABASE_MIGRATIONS.find((migration) =>
  migration.name.endsWith("_daily_close_upload_outbox"),
)!;
const BEFORE_UPLOAD = POS_DATABASE_MIGRATIONS.filter(
  (migration) => migration.version < UPLOAD_MIGRATION.version,
);

const NOW = "2026-10-07T01:00:00.000Z";
const SCOPE = DAILY_CLOSE_FIXTURE_SCOPE;
const guid = (index: number) =>
  `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`;

type UploadRow = Readonly<{
  close_id: string;
  upload_state: string;
  upload_attempt_count: number;
  upload_next_attempt_at_iso: string | null;
  upload_last_attempt_at_iso: string | null;
  upload_error_code: string | null;
  upload_error_message: string | null;
  uploaded_at_iso: string | null;
}>;

function commitFor(
  closeId: string,
  overrides: Partial<DailyCloseArchive> = {},
): DailyCloseArchiveCommit {
  const archive = archiveFixture({ closeId, ...overrides });
  return {
    archive,
    audit: {
      eventId: `audit-${closeId}`,
      eventType: "DAILY_CLOSE_SAVE",
      occurredAtIso: archive.savedAtIso,
      orderGuid: null,
      correlationId: closeId,
      payload: { action: "daily-close-save", closeId },
    },
  };
}

async function rowOf(
  connection: SqliteConnectionPort,
  closeId: string,
): Promise<UploadRow> {
  const row = await connection.getFirst<UploadRow>(
    `SELECT close_id, upload_state, upload_attempt_count, upload_next_attempt_at_iso,
            upload_last_attempt_at_iso, upload_error_code, upload_error_message, uploaded_at_iso
     FROM local_daily_closes WHERE close_id = ?`,
    [closeId],
  );
  assert.ok(row, `缺少日结 ${closeId}`);
  return row;
}

async function upgradeToLatest(connection: SqliteConnectionPort): Promise<void> {
  await applyMigrations(connection, () => NOW);
}

test("旧库升级：历史日结自动 pending 且立即到期，legacy 非 GUID 行 skipped，状态列立即可 UPDATE", async () => {
  await withDatabase(async (connection) => {
    // 1. M6 旧库：一条非 GUID 的遗留日结，一条 closeId 恰好是 GUID 形态的遗留日结。
    await applyMigrations(
      connection,
      () => NOW,
      POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 16),
    );
    for (const [closeId, businessDate] of [
      ["legacy-close", "2026-07-27"],
      [guid(900), "2026-07-26"],
    ] as const) {
      await connection.run(
        `INSERT INTO local_daily_closes (
          close_id, business_date, store_code, device_code, state,
          expected_cash_cents, counted_cash_cents, variance_cents,
          created_at_iso, closed_at_iso
        ) VALUES (?, ?, 'S001', 'DEV-1', 'closed', 1200, 1250, 50, ?, ?)`,
        [closeId, businessDate, NOW, NOW],
      );
    }
    // 2. 升到“上传迁移之前”的最新版，并保存 3 条原生日结（升级前的历史日结）。
    await applyMigrations(connection, () => NOW, BEFORE_UPLOAD);
    const repository = new SqliteDailyCloseRepository(connection);
    for (const index of [1, 2, 3]) {
      await repository.saveArchive(
        commitFor(guid(index), { savedAtIso: `2026-10-0${index}T13:30:00.000Z` }),
      );
    }
    assert.equal(
      (await connection.getFirst<{ n: number }>("SELECT COUNT(*) AS n FROM local_daily_closes"))?.n,
      5,
    );

    // 3. 升级到带上传 outbox 的版本。
    await upgradeToLatest(connection);

    for (const closeId of [guid(1), guid(2), guid(3), guid(900)]) {
      const row = await rowOf(connection, closeId);
      assert.equal(row.upload_state, "pending", closeId);
      assert.equal(row.upload_attempt_count, 0);
      assert.equal(row.upload_next_attempt_at_iso, null, "NULL = 立即到期");
      assert.equal(row.uploaded_at_iso, null);
    }
    const legacy = await rowOf(connection, "legacy-close");
    assert.equal(legacy.upload_state, "skipped");
    assert.equal(legacy.upload_error_code, "DAILY_CLOSE_GUID_INVALID");

    // 升级后“立即到期”：无需任何回填，当前范围的历史日结直接出现在待传队列里。
    const uploads = new SqliteDailyCloseUploadRepository(connection);
    assert.deepEqual(
      await uploads.listDue(SCOPE, 20, NOW),
      [guid(1), guid(2), guid(3), guid(900)],
      "按保存时间先后补传；GUID 形态的遗留日结也要补传（服务端校验不过会被永久拒绝），skipped 的不在队列里",
    );

    // 不可变触发器只保护存档事实：业务列不可改，上传状态列立即可 UPDATE，删除依旧禁止。
    await assert.rejects(
      connection.run(
        "UPDATE local_daily_closes SET counted_cash_cents = counted_cash_cents + 1 WHERE close_id = ?",
        [guid(1)],
      ),
      /DAILY_CLOSE_ARCHIVE_IMMUTABLE/,
    );
    await connection.run(
      "UPDATE local_daily_closes SET upload_state = 'synced', uploaded_at_iso = ? WHERE close_id = ?",
      [NOW, guid(1)],
    );
    assert.equal((await rowOf(connection, guid(1))).upload_state, "synced");
    await assert.rejects(
      connection.run("DELETE FROM local_daily_closes WHERE close_id = ?", [guid(1)]),
      /DAILY_CLOSE_DELETE_FORBIDDEN/,
    );
    await assert.rejects(
      connection.run("UPDATE local_daily_closes SET upload_state = 'bogus' WHERE close_id = ?", [guid(2)]),
      /CHECK constraint failed/i,
    );
  });
});

test("迁移幂等：再次 applyMigrations 不会把已 synced / rejected 的行重置成 pending", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => NOW, BEFORE_UPLOAD);
    const repository = new SqliteDailyCloseRepository(connection);
    await repository.saveArchive(commitFor(guid(1)));
    await repository.saveArchive(commitFor(guid(2)));
    await upgradeToLatest(connection);
    await connection.run("UPDATE local_daily_closes SET upload_state = 'synced' WHERE close_id = ?", [guid(1)]);
    await connection.run("UPDATE local_daily_closes SET upload_state = 'rejected' WHERE close_id = ?", [guid(2)]);

    await upgradeToLatest(connection);

    assert.equal((await rowOf(connection, guid(1))).upload_state, "synced");
    assert.equal((await rowOf(connection, guid(2))).upload_state, "rejected");
  });
});

test("新库：保存日结后默认 pending 且立即到期（日结保存路径无需改动）", async () => {
  await withDatabase(async (connection) => {
    await upgradeToLatest(connection);
    await new SqliteDailyCloseRepository(connection).saveArchive(commitFor(guid(1)));

    const row = await rowOf(connection, guid(1));
    assert.equal(row.upload_state, "pending");
    assert.equal(row.upload_next_attempt_at_iso, null);
    assert.deepEqual(await new SqliteDailyCloseUploadRepository(connection).listDue(SCOPE, 20, NOW), [guid(1)]);
  });
});

test("listDue 只取当前授权范围内已到期的 pending，按保存时间先后，且遵守批次上限", async () => {
  await withDatabase(async (connection) => {
    await upgradeToLatest(connection);
    const saves = new SqliteDailyCloseRepository(connection);
    await saves.saveArchive(commitFor(guid(1), { savedAtIso: "2026-10-03T00:00:00.000Z" }));
    await saves.saveArchive(commitFor(guid(2), { savedAtIso: "2026-10-01T00:00:00.000Z" }));
    await saves.saveArchive(commitFor(guid(3), { savedAtIso: "2026-10-02T00:00:00.000Z", deviceCode: "OLD-DEVICE" }));
    await saves.saveArchive(commitFor(guid(4), { savedAtIso: "2026-10-04T00:00:00.000Z", storeCode: "OTHER" }));
    await saves.saveArchive(commitFor(guid(5), { savedAtIso: "2026-10-05T00:00:00.000Z" }));
    await saves.saveArchive(commitFor(guid(6), { savedAtIso: "2026-10-06T00:00:00.000Z" }));
    const uploads = new SqliteDailyCloseUploadRepository(connection);
    // 5 还没到期，6 已不是 pending。
    await connection.run("UPDATE local_daily_closes SET upload_next_attempt_at_iso = '2026-10-07T02:00:00.000Z' WHERE close_id = ?", [guid(5)]);
    await connection.run("UPDATE local_daily_closes SET upload_state = 'synced' WHERE close_id = ?", [guid(6)]);

    assert.deepEqual(await uploads.listDue(SCOPE, 20, NOW), [guid(2), guid(1)]);
    assert.deepEqual(await uploads.listDue(SCOPE, 1, NOW), [guid(2)]);
    // 到期后的 5 排在从未失败过（NULL）的行之后。
    assert.deepEqual(
      await uploads.listDue(SCOPE, 20, "2026-10-07T02:00:00.000Z"),
      [guid(2), guid(1), guid(5)],
    );
    // 其它范围的行保持 pending 且不会被取到。
    assert.deepEqual(
      await uploads.listDue({ storeCode: "S001", deviceCode: "OLD-DEVICE" }, 20, NOW),
      [guid(3)],
    );
    assert.equal((await rowOf(connection, guid(3))).upload_state, "pending");
    assert.deepEqual(await uploads.listDue({ storeCode: "OTHER", deviceCode: "DEV-1" }, 20, NOW), [guid(4)]);
  });
});

test("认领 CAS：并发认领同一条日结只有一个赢家，且只认当前范围、已到期、pending 的行", async () => {
  await withDatabase(async (connection) => {
    await upgradeToLatest(connection);
    const saves = new SqliteDailyCloseRepository(connection);
    await saves.saveArchive(commitFor(guid(1)));
    await saves.saveArchive(commitFor(guid(2), { deviceCode: "OLD-DEVICE" }));
    await saves.saveArchive(commitFor(guid(3)));
    await connection.run("UPDATE local_daily_closes SET upload_next_attempt_at_iso = '2026-10-07T02:00:00.000Z' WHERE close_id = ?", [guid(3)]);
    const uploads = new SqliteDailyCloseUploadRepository(connection);

    const results = await Promise.all(
      Array.from({ length: 12 }, () => uploads.tryClaim(guid(1), SCOPE, NOW)),
    );

    assert.equal(results.filter((lease) => lease !== null).length, 1);
    assert.deepEqual(results.find((lease) => lease !== null), { closeId: guid(1), attemptCount: 1 });
    const claimed = await rowOf(connection, guid(1));
    assert.equal(claimed.upload_state, "uploading");
    assert.equal(claimed.upload_attempt_count, 1);
    assert.equal(claimed.upload_last_attempt_at_iso, NOW);
    assert.equal(claimed.upload_error_code, null);

    // 已 uploading 的不能再认领；其它范围的行、未到期的行都认领不到。
    assert.equal(await uploads.tryClaim(guid(1), SCOPE, NOW), null);
    assert.equal(await uploads.tryClaim(guid(2), SCOPE, NOW), null);
    assert.equal(await uploads.tryClaim(guid(3), SCOPE, NOW), null);
    assert.equal((await rowOf(connection, guid(3))).upload_attempt_count, 0);
    // 到期后可以认领。
    assert.deepEqual(
      await uploads.tryClaim(guid(3), SCOPE, "2026-10-07T02:00:00.000Z"),
      { closeId: guid(3), attemptCount: 1 },
    );
  });
});

test("状态流转：成功 / 失败退避 / 授权问题撤销尝试计数 / 永久拒绝 / skipped；只有持有租约的一方能改状态", async () => {
  await withDatabase(async (connection) => {
    await upgradeToLatest(connection);
    const saves = new SqliteDailyCloseRepository(connection);
    for (const index of [1, 2, 3, 4, 5]) await saves.saveArchive(commitFor(guid(index)));
    const uploads = new SqliteDailyCloseUploadRepository(connection);

    // 未认领（pending）时 markPending / release / markRejected 都不生效。
    await uploads.markPending(guid(1), "2026-10-07T01:05:00.000Z", "X", "x");
    await uploads.releaseWithoutAttempt(guid(1), "2026-10-07T01:05:00.000Z", "X", "x");
    await uploads.markRejected(guid(1), "X", "x");
    assert.equal((await rowOf(connection, guid(1))).upload_state, "pending");
    assert.equal((await rowOf(connection, guid(1))).upload_next_attempt_at_iso, null);

    // 失败退避：保留已累加的尝试次数并设置下次到期时间。
    await uploads.tryClaim(guid(1), SCOPE, NOW);
    await uploads.markPending(guid(1), "2026-10-07T01:00:05.000Z", "HTTP_503", "unavailable");
    const retried = await rowOf(connection, guid(1));
    assert.equal(retried.upload_state, "pending");
    assert.equal(retried.upload_attempt_count, 1);
    assert.equal(retried.upload_next_attempt_at_iso, "2026-10-07T01:00:05.000Z");
    assert.equal(retried.upload_error_code, "HTTP_503");
    assert.equal(await uploads.tryClaim(guid(1), SCOPE, NOW), null, "退避期内不可认领");
    assert.deepEqual(await uploads.tryClaim(guid(1), SCOPE, "2026-10-07T01:00:05.000Z"), {
      closeId: guid(1),
      attemptCount: 2,
    });

    // 设备授权问题：撤销本次尝试计数（2 → 1），设置短冷却。
    await uploads.releaseWithoutAttempt(guid(1), "2026-10-07T01:01:05.000Z", "HTTP_403", "forbidden");
    const released = await rowOf(connection, guid(1));
    assert.equal(released.upload_state, "pending");
    assert.equal(released.upload_attempt_count, 1);
    assert.equal(released.upload_next_attempt_at_iso, "2026-10-07T01:01:05.000Z");
    // 尝试计数不会被撤销成负数。
    await uploads.tryClaim(guid(2), SCOPE, NOW);
    await connection.run("UPDATE local_daily_closes SET upload_attempt_count = 0 WHERE close_id = ?", [guid(2)]);
    await uploads.releaseWithoutAttempt(guid(2), NOW, "HTTP_401", "x");
    assert.equal((await rowOf(connection, guid(2))).upload_attempt_count, 0);

    // 成功。
    await uploads.tryClaim(guid(3), SCOPE, NOW);
    await uploads.markSucceeded(guid(3), "2026-10-07T01:00:09.000Z");
    const synced = await rowOf(connection, guid(3));
    assert.equal(synced.upload_state, "synced");
    assert.equal(synced.uploaded_at_iso, "2026-10-07T01:00:09.000Z");
    assert.equal(synced.upload_error_code, null);
    assert.equal(synced.upload_next_attempt_at_iso, null);

    // 永久拒绝：不再出现在待传队列，也不可再认领。
    await uploads.tryClaim(guid(4), SCOPE, NOW);
    await uploads.markRejected(guid(4), "INVALID_NOTE_SUBTOTAL", "mismatch");
    const rejected = await rowOf(connection, guid(4));
    assert.equal(rejected.upload_state, "rejected");
    assert.equal(rejected.upload_error_code, "INVALID_NOTE_SUBTOTAL");
    assert.equal(await uploads.tryClaim(guid(4), SCOPE, "2030-01-01T00:00:00.000Z"), null);

    // skipped。
    await uploads.markSkipped(guid(5), "DAILY_CLOSE_GUID_INVALID", "bad id");
    assert.equal((await rowOf(connection, guid(5))).upload_state, "skipped");
    // 迟到的“成功”不能覆盖已被拒绝/跳过的行。
    await uploads.markSucceeded(guid(4), NOW);
    await uploads.markSucceeded(guid(5), NOW);
    assert.equal((await rowOf(connection, guid(4))).upload_state, "rejected");
    assert.equal((await rowOf(connection, guid(5))).upload_state, "skipped");
    // 错误信息按长度截断。
    await uploads.tryClaim(guid(2), SCOPE, NOW);
    await uploads.markPending(guid(2), NOW, "C".repeat(300), "M".repeat(900));
    const truncated = await rowOf(connection, guid(2));
    assert.equal(truncated.upload_error_code?.length, 128);
    assert.equal(truncated.upload_error_message?.length, 512);
  });
});

test("回收过期租约：进程中途退出遗留的 uploading 超过 2 分钟回 pending，未过期的不动", async () => {
  await withDatabase(async (connection) => {
    await upgradeToLatest(connection);
    const saves = new SqliteDailyCloseRepository(connection);
    await saves.saveArchive(commitFor(guid(1)));
    await saves.saveArchive(commitFor(guid(2)));
    const uploads = new SqliteDailyCloseUploadRepository(connection);
    await uploads.tryClaim(guid(1), SCOPE, "2026-10-07T00:55:00.000Z");
    await uploads.tryClaim(guid(2), SCOPE, "2026-10-07T00:59:30.000Z");

    const recovered = await uploads.recoverExpiredUploading({
      staleBeforeIso: "2026-10-07T00:58:00.000Z",
      nextAttemptAtIso: NOW,
    });

    assert.equal(recovered, 1);
    const stale = await rowOf(connection, guid(1));
    assert.equal(stale.upload_state, "pending");
    assert.equal(stale.upload_next_attempt_at_iso, NOW);
    assert.equal(stale.upload_error_code, "UPLOAD_LEASE_EXPIRED");
    assert.equal(stale.upload_attempt_count, 1, "回收不改尝试次数，退避继续累计");
    assert.equal((await rowOf(connection, guid(2))).upload_state, "uploading");
  });
});

test("nextReadyAtIso：只看带退避时间的 pending（当前范围）与 uploading 租约到期，立即到期的 pending 不触发定时自旋", async () => {
  await withDatabase(async (connection) => {
    await upgradeToLatest(connection);
    const saves = new SqliteDailyCloseRepository(connection);
    for (const index of [1, 2, 3, 4]) await saves.saveArchive(commitFor(guid(index)));
    await saves.saveArchive(commitFor(guid(5), { deviceCode: "OLD-DEVICE" }));
    const uploads = new SqliteDailyCloseUploadRepository(connection);

    assert.equal(await uploads.nextReadyAtIso(SCOPE), null, "全是立即到期的 pending：无定时唤醒");

    await connection.run("UPDATE local_daily_closes SET upload_next_attempt_at_iso = '2026-10-07T03:00:00.000Z' WHERE close_id = ?", [guid(1)]);
    await connection.run("UPDATE local_daily_closes SET upload_next_attempt_at_iso = '2026-10-07T02:00:00.000Z' WHERE close_id = ?", [guid(2)]);
    await connection.run("UPDATE local_daily_closes SET upload_next_attempt_at_iso = '2026-10-07T01:30:00.000Z' WHERE close_id = ?", [guid(5)]);
    assert.equal(await uploads.nextReadyAtIso(SCOPE), "2026-10-07T02:00:00.000Z", "其它范围的 01:30 不参与");

    await uploads.tryClaim(guid(3), SCOPE, "2026-10-07T01:00:00.000Z");
    assert.equal(await uploads.nextReadyAtIso(SCOPE), "2026-10-07T01:02:00.000Z", "uploading 的租约到期（+2 分钟）更早");
  });
});

test("真实保存路径：订单汇总 → 冻结归档 → 映射的上传请求通过服务端全部校验并上传成功", async () => {
  await withDatabase(async (connection) => {
    await upgradeToLatest(connection);
    await seedOrder(connection, "sale-1", 1, "2026-10-06T15:00:00.000Z", "sale", "1", [["cash", 1005], ["card", 435]]);
    await seedOrder(connection, "return-1", 2, "2026-10-06T16:00:00.000Z", "return", "1.75", [["cash", -205], ["voucher", -115]]);
    const dailyCloses = new SqliteDailyCloseRepository(connection);
    const summary = await dailyCloses.summarize({
      businessDate: "2026-10-07",
      periodFromIso: "2026-10-06T14:00:00.000Z",
      periodToIso: "2026-10-07T14:00:00.000Z",
      storeCode: SCOPE.storeCode,
      deviceCode: SCOPE.deviceCode,
    });
    const commit = buildDailyCloseArchiveCommit({
      auditEventId: "audit-real-path",
      closeId: guid(77),
      counts: [
        { denominationCents: 500, quantity: 1 },
        { denominationCents: 200, quantity: 1 },
        { denominationCents: 100, quantity: 1 },
        { denominationCents: 5, quantity: 1 },
      ],
      savedAtIso: "2026-10-07T13:30:00.456Z",
      savedCashierId: "cashier-9",
      savedCashierName: "Cashier Nine",
      savedUserGuid: null,
      summary,
    });
    await dailyCloses.saveArchive(commit);

    const requests: DailyCloseSyncRequest[] = [];
    const service = uploadService(connection, {
      async sync(request) {
        requests.push(request);
        return { kind: "synced", outcome: "accepted" };
      },
    });
    const result = await service.drain();

    assert.equal(result.uploaded, 1);
    assert.equal(requests.length, 1);
    assert.deepEqual(validateAgainstServerRules(requests[0], SCOPE), { ok: true });
    assert.equal(requests[0]?.clientKind, CLIENT_KIND);
    assert.equal(requests[0]?.cashCounts?.length, 11);
    assert.equal(requests[0]?.refundAmount, 3.2, "退款 = |−2.05| + |−1.15|");
    assert.deepEqual(requests[0]?.tenders, [
      { method: "Cash", salesAmount: 10.05, refundAmount: 2.05, netAmount: 8 },
      { method: "Card", salesAmount: 4.35, refundAmount: 0, netAmount: 4.35 },
      { method: "Voucher", salesAmount: 0, refundAmount: 1.15, netAmount: -1.15 },
    ]);
    assert.equal(requests[0]?.returnQuantity, 1.75);
    assert.equal((await rowOf(connection, guid(77))).upload_state, "synced");
  });
});

test("历史补传：升级后全部历史日结（含 legacy GUID 形态）补传，非 GUID 的 skipped 不发请求；401 中断且不烧尝试次数", async () => {
  await withDatabase(async (connection) => {
    await applyMigrations(connection, () => NOW, POS_DATABASE_MIGRATIONS.filter((migration) => migration.version <= 16));
    await connection.run(
      `INSERT INTO local_daily_closes (
        close_id, business_date, store_code, device_code, state,
        expected_cash_cents, counted_cash_cents, variance_cents, created_at_iso, closed_at_iso
      ) VALUES ('legacy-close', '2026-07-27', 'S001', 'DEV-1', 'closed', 0, 0, 0, ?, ?)`,
      [NOW, NOW],
    );
    await applyMigrations(connection, () => NOW, BEFORE_UPLOAD);
    const saves = new SqliteDailyCloseRepository(connection);
    for (let index = 1; index <= 25; index += 1) {
      await saves.saveArchive(
        commitFor(guid(index), {
          savedAtIso: new Date(Date.UTC(2026, 8, 1, 0, index)).toISOString(),
          businessDate: "2026-09-01",
          periodFromIso: "2026-08-31T14:00:00.000Z",
          periodToIso: "2026-09-01T14:00:00.000Z",
        }),
      );
    }
    await upgradeToLatest(connection);

    // 第一轮：第 3 条返回 403 → 立即中断，其后的历史日结原样不动，尝试次数没被烧掉。
    const interrupting: DailyCloseSyncRequest[] = [];
    let call = 0;
    const first = await uploadService(connection, {
      async sync(request) {
        interrupting.push(request);
        call += 1;
        return call === 3
          ? { kind: "unauthorized", code: "HTTP_403", message: "forbidden" }
          : { kind: "synced", outcome: "replaced-placeholder" };
      },
    }).drain();
    assert.equal(first.interrupted, true);
    assert.equal(interrupting.length, 3);
    assert.equal((await rowOf(connection, guid(1))).upload_state, "synced");
    assert.equal((await rowOf(connection, guid(2))).upload_state, "synced");
    const blocked = await rowOf(connection, guid(3));
    assert.equal(blocked.upload_state, "pending");
    assert.equal(blocked.upload_attempt_count, 0, "撤销本次尝试计数");
    assert.equal(blocked.upload_error_code, "HTTP_403");
    const untouched = await rowOf(connection, guid(4));
    assert.equal(untouched.upload_state, "pending");
    assert.equal(untouched.upload_attempt_count, 0);
    assert.equal(untouched.upload_last_attempt_at_iso, null);

    // 冷却之后再跑：补完所有剩余历史日结（批次循环取空），legacy 非 GUID 行始终没有请求。
    const later: DailyCloseSyncRequest[] = [];
    const second = await uploadService(
      connection,
      {
        async sync(request) {
          later.push(request);
          return { kind: "synced", outcome: "replaced-placeholder" };
        },
      },
      () => new Date("2026-10-07T01:10:00.000Z"),
    ).drain();
    assert.equal(second.uploaded, 23);
    assert.equal(later.length, 23);
    assert.equal(
      [...interrupting.slice(0, 3), ...later].some((request) => request.dailyCloseGuid === "legacy-close"),
      false,
    );
    for (const request of [...interrupting, ...later]) {
      assert.deepEqual(validateAgainstServerRules(request, SCOPE), { ok: true });
    }
    const states = await connection.getAll<{ upload_state: string; n: number }>(
      "SELECT upload_state, COUNT(*) AS n FROM local_daily_closes GROUP BY upload_state ORDER BY upload_state",
    );
    assert.deepEqual(
      states.map((row) => [row.upload_state, row.n]),
      [["skipped", 1], ["synced", 25]],
    );
  });
});

test("协调器端到端：drain 末尾补传日结；失败带退避保持 pending，下一轮到期后成功", async () => {
  await withDatabase(async (connection) => {
    await upgradeToLatest(connection);
    await new SqliteDailyCloseRepository(connection).saveArchive(commitFor(guid(1)));
    let clock = new Date(NOW);
    let attempt = 0;
    const scheduled: number[] = [];
    const service = uploadService(
      connection,
      {
        async sync() {
          attempt += 1;
          return attempt === 1
            ? { kind: "retry", code: "HTTP_404", message: "not deployed yet" }
            : { kind: "synced", outcome: "accepted" };
        },
      },
      () => clock,
    );
    const coordinator = new PosSyncCoordinator({
      outbox: {
        async enqueue() {},
        async leaseReady() { return []; },
        async markSucceeded() {},
        async releaseRetry() {},
        async markBlocked403() {},
        async markRejected() {},
      },
      auditRepository: { async append() {}, async listPending() { return []; }, async markUploaded() {} },
      orderSync: { async sync() { return { kind: "synced", alreadySynced: false }; } },
      auditUploader: { async upload() { return { kind: "uploaded" }; } },
      dailyCloseUpload: service,
      security: { async lockDevice() {} },
      now: () => clock,
      timer: {
        set(delayMs) {
          scheduled.push(delayMs);
          return 1;
        },
        clear() {},
      },
    });

    await coordinator.requestDrain();
    const retried = await rowOf(connection, guid(1));
    assert.equal(retried.upload_state, "pending");
    assert.equal(retried.upload_error_code, "HTTP_404");
    assert.equal(retried.upload_next_attempt_at_iso, "2026-10-07T01:00:05.000Z");
    assert.deepEqual(scheduled, [5_000], "按日结重试时间安排下一次定时唤醒");

    clock = new Date("2026-10-07T01:00:05.000Z");
    await coordinator.requestDrain();
    assert.equal((await rowOf(connection, guid(1))).upload_state, "synced");
  });
});

function uploadService(
  connection: SqliteConnectionPort,
  sync: DailyCloseSyncPort | { sync(request: DailyCloseSyncRequest): Promise<DailyCloseSyncResult> },
  now: () => Date = () => new Date(NOW),
): DailyCloseUploadService {
  return new DailyCloseUploadService({
    repository: new SqliteDailyCloseUploadRepository(connection),
    sync,
    scope: () => SCOPE,
    clientKind: CLIENT_KIND,
    appVersion: "0.1.2",
    now,
  });
}

async function seedOrder(
  connection: SqliteConnectionPort,
  orderGuid: string,
  sequence: number,
  soldAtIso: string,
  lineKind: "sale" | "return",
  quantity: string,
  tenders: readonly (readonly ["cash" | "card" | "voucher", number])[],
): Promise<void> {
  await connection.run(
    `INSERT INTO local_orders (
      order_guid, local_sequence, store_code, device_code,
      cashier_id, cashier_name, sold_at_iso, state,
      total_cents, discount_cents, actual_amount_cents,
      original_order_guid, created_at_iso, updated_at_iso
    ) VALUES (?, ?, 'S001', 'DEV-1', 'cashier-1', 'Cashier One', ?, 'Synced',
      100, 0, 100, NULL, ?, ?)`,
    [orderGuid, sequence, soldAtIso, soldAtIso, soldAtIso],
  );
  await connection.run(
    `INSERT INTO local_order_lines (
      line_id, order_guid, line_sequence, product_code, item_number,
      lookup_code, display_name, quantity, unit_price_cents,
      discount_cents, actual_amount_cents, price_source, line_kind,
      return_source_key, original_order_guid, original_order_detail_guid,
      reference_code, sync_price_source
    ) VALUES (?, ?, 1, 'PRODUCT-1', 'ITEM-1', 'LOOKUP-1', 'Product',
      ?, 100, 0, 100, 'catalog', ?, NULL, NULL, NULL, 'REF-1', 0)`,
    [`line-${orderGuid}`, orderGuid, quantity, lineKind],
  );
  for (const [index, [method, amountCents]] of tenders.entries()) {
    await connection.run(
      `INSERT INTO order_tenders (
        tender_guid, order_guid, method, amount_cents, payment_attempt_id, created_at_iso
      ) VALUES (?, ?, ?, ?, NULL, ?)`,
      [`tender-${orderGuid}-${index}`, orderGuid, method, amountCents, soldAtIso],
    );
  }
}

class SystemSqliteConnection implements SqliteConnectionPort {
  public constructor(private readonly database: DatabaseSync) {
    this.database.exec("PRAGMA foreign_keys = ON;");
  }

  public async exec(sql: string): Promise<void> {
    this.database.exec(sql);
  }

  public async run(sql: string, parameters: readonly SqlValue[] = []): Promise<SqlRunResult> {
    const result = this.database.prepare(sql).run(...parameters.map(toSqlInputValue));
    return { changes: Number(result.changes), lastInsertRowId: Number(result.lastInsertRowid) };
  }

  public async getFirst<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<T | null> {
    return (this.database.prepare(sql).get(...parameters.map(toSqlInputValue)) as T | undefined) ?? null;
  }

  public async getAll<T extends object>(sql: string, parameters: readonly SqlValue[] = []): Promise<readonly T[]> {
    return this.database.prepare(sql).all(...parameters.map(toSqlInputValue)) as unknown as readonly T[];
  }

  public async withExclusiveTransaction<T>(
    operation: (transaction: SqliteConnectionPort) => Promise<T>,
  ): Promise<T> {
    this.database.exec("BEGIN IMMEDIATE;");
    const transaction = new TransactionConnection(this.database);
    try {
      const result = await operation(transaction);
      this.database.exec("COMMIT;");
      return result;
    } catch (error) {
      this.database.exec("ROLLBACK;");
      throw error;
    }
  }

  public async close(): Promise<void> {
    this.database.close();
  }
}

class TransactionConnection extends SystemSqliteConnection {
  public override withExclusiveTransaction<T>(): Promise<T> {
    return Promise.reject(new Error("Nested test transaction."));
  }

  public override close(): Promise<void> {
    return Promise.reject(new Error("Transaction cannot close database."));
  }
}

async function withDatabase(
  operation: (connection: SystemSqliteConnection) => Promise<void>,
): Promise<void> {
  const connection = new SystemSqliteConnection(new DatabaseSync(":memory:"));
  try {
    await operation(connection);
  } finally {
    await connection.close();
  }
}

function toSqlInputValue(value: SqlValue): SQLInputValue {
  return value as SQLInputValue;
}
