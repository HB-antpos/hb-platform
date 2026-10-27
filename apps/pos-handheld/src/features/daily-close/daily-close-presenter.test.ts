import assert from "node:assert/strict";
import test from "node:test";

import {
  DAILY_CLOSE_REPRINT_PERMISSION,
  DAILY_CLOSE_SAVE_PERMISSION,
  DAILY_CLOSE_VIEW_PERMISSION,
} from "@hb/pos-domain/features/daily-close/daily-close-authorization";
import {
  DailyClosePresenter,
  type DailyClosePrintJob,
} from "./daily-close-presenter";

import type {
  AuditEventDraft,
  DailyCloseArchive,
  DailyCloseArchiveCommit,
  DailyCloseRepositoryPort,
  DailyCloseScope,
  DailyCloseSummary,
} from "@/core/contracts";

test("仅 View 权限可读取同店同设备本地日结，但不能点钞、保存或补打", async () => {
  const harness = createHarness({
    permissions: [DAILY_CLOSE_VIEW_PERMISSION],
  });

  await harness.presenter.load();

  assert.deepEqual(harness.repository.summaryScopes, [
    {
      businessDate: "2026-07-28",
      periodFromIso: "2026-07-27T14:00:00.000Z",
      periodToIso: "2026-07-28T14:00:00.000Z",
      storeCode: "S1",
      deviceCode: "IPAD-1",
    },
  ]);
  assert.equal(harness.presenter.setCount(10_000, 2), false);
  await harness.presenter.saveAndPrint();
  await harness.presenter.reprintSelected();
  assert.equal(harness.repository.commits.length, 0);
  assert.equal(harness.printer.jobs.length, 0);
  assert.equal(harness.presenter.getState().statusCode, "permission-required");
});

test("保存先原子写归档和审计，再打印；打印失败不回滚且清空点钞并展示历史", async () => {
  const harness = createHarness({ printFails: true });
  await harness.presenter.load();
  assert.equal(harness.presenter.setCount(10_000, 1), true);
  assert.equal(harness.presenter.setCount(500, 2), true);

  await harness.presenter.saveAndPrint();

  assert.deepEqual(harness.events, ["save:close-1", "print:close-1"]);
  assert.equal(harness.repository.commits.length, 1);
  assert.equal(
    harness.repository.commits[0]?.audit.eventType,
    "DAILY_CLOSE_SAVE",
  );
  const state = harness.presenter.getState();
  assert.equal(state.statusCode, "saved-print-failed");
  assert.equal(state.activePane, "history");
  assert.equal(state.selectedArchive?.closeId, "close-1");
  assert.deepEqual(
    state.counts.map((entry) => entry.quantity),
    Array(11).fill(0),
  );
  assert.equal(state.archives.length, 1);
  assert.equal(harness.repository.archives.length, 1);
});

test("同一业务日可连续保存多个不可变归档，历史按新到旧选择", async () => {
  const harness = createHarness();
  await harness.presenter.load();

  harness.presenter.setCount(2_000, 1);
  await harness.presenter.saveAndPrint();
  harness.presenter.showCount();
  harness.presenter.setCount(2_000, 2);
  await harness.presenter.saveAndPrint();

  assert.deepEqual(
    harness.presenter.getState().archives.map((archive) => archive.closeId),
    ["close-2", "close-1"],
  );
  assert.equal(harness.presenter.getState().selectedArchive?.closeId, "close-2");
  assert.deepEqual(
    harness.repository.commits.map((commit) => commit.archive.businessDate),
    ["2026-07-28", "2026-07-28"],
  );
});

test("刷新同一营业日保留未保存点钞，切换日期才重置", async () => {
  const harness = createHarness();
  await harness.presenter.load();
  harness.presenter.setCount(5_000, 3);

  assert.equal(harness.presenter.setBusinessDate("2026-07-28"), true);
  assert.equal(
    harness.presenter
      .getState()
      .counts.find((entry) => entry.denominationCents === 5_000)
      ?.quantity,
    3,
  );
  assert.equal(harness.presenter.setBusinessDate("2026-07-27"), true);
  assert.equal(
    harness.presenter
      .getState()
      .counts.find((entry) => entry.denominationCents === 5_000)
      ?.quantity,
    0,
  );
});

test("保存失败保留点钞；补打只打印已选归档并带 reprint 标记", async () => {
  const failed = createHarness({ saveFails: true });
  await failed.presenter.load();
  failed.presenter.setCount(5_000, 3);
  await failed.presenter.saveAndPrint();
  assert.equal(
    failed.presenter
      .getState()
      .counts.find((entry) => entry.denominationCents === 5_000)?.quantity,
    3,
  );
  assert.equal(failed.presenter.getState().statusCode, "save-failed");

  const reprint = createHarness();
  reprint.repository.archives.push(archive("old-close"));
  await reprint.presenter.load();
  reprint.presenter.selectArchive("old-close");
  await reprint.presenter.reprintSelected();
  assert.equal(reprint.printer.jobs.at(-1)?.reprint, true);
  assert.equal(reprint.printer.jobs.at(-1)?.archive.closeId, "old-close");
  assert.match(
    reprint.printer.jobs.at(-1)?.document.lines.join("\n") ?? "",
    /REPRINT|补打/,
  );
  assert.match(
    reprint.printer.jobs.at(-1)?.document.lines.join("\n") ?? "",
    /Refunds and returns/,
  );
});

test("日结补打无论成功或失败都记录冻结员工身份", async () => {
  const succeeded = createHarness();
  succeeded.repository.archives.push(archive("reprint-success"));
  await succeeded.presenter.load();
  await succeeded.presenter.reprintSelected();

  assert.deepEqual(succeeded.audit.events, [
    {
      eventId: "close-1",
      eventType: "DAILY_CLOSE_REPRINT",
      occurredAtIso: "2026-07-28T08:00:00.000Z",
      orderGuid: null,
      correlationId: "reprint-success",
      payload: {
        action: "daily-close-reprint",
        status: "Printed",
        reason: "daily-close-reprint",
        source: "pos-handheld",
        outcome: "Succeeded",
        requestingCashierId: "C1",
        requestingCashierName: "Alice",
        requestingUserGuid: "U1",
      },
    },
  ]);

  const failed = createHarness({ printFails: true });
  failed.repository.archives.push(archive("reprint-failed"));
  await failed.presenter.load();
  await failed.presenter.reprintSelected();
  assert.equal(failed.presenter.getState().statusCode, "reprint-failed");
  assert.equal(failed.audit.events[0]?.payload.status, "Failed");
  assert.equal(failed.audit.events[0]?.payload.outcome, "Failed");
});

test("日结单已打印、仅现金单失败时，保存与补打都如实报告现金单失败，归档与审计照常保留", async () => {
  const saved = createHarness({ cashSlipFails: true });
  await saved.presenter.load();
  assert.equal(saved.presenter.setCount(10_000, 1), true);
  await saved.presenter.saveAndPrint();
  assert.equal(saved.presenter.getState().statusCode, "saved-cash-slip-failed");
  assert.equal(saved.repository.commits.length, 1);

  const reprint = createHarness({ cashSlipFails: true });
  reprint.repository.archives.push(archive("cash-slip-reprint"));
  await reprint.presenter.load();
  await reprint.presenter.reprintSelected();
  assert.equal(reprint.presenter.getState().statusCode, "reprint-cash-slip-failed");
  assert.equal(reprint.audit.events[0]?.payload.status, "Failed");
});

test("日结补打的审计故障不覆盖已经确认的打印结果", async () => {
  const harness = createHarness({ auditFails: true });
  harness.repository.archives.push(archive("reprint-audit-failure"));
  await harness.presenter.load();
  await harness.presenter.reprintSelected();

  assert.equal(harness.printer.jobs.length, 1);
  assert.equal(harness.presenter.getState().statusCode, "reprint-printed");
  assert.deepEqual(harness.audit.events, []);
});

function createHarness(
  options: Partial<{
    permissions: readonly string[];
    auditFails: boolean;
    printFails: boolean;
    /** 日结单已确认打印，随后的现金单失败（对齐 WPF #478）。 */
    cashSlipFails: boolean;
    saveFails: boolean;
  }> = {},
) {
  const events: string[] = [];
  const repository = new MemoryRepository(events);
  repository.saveFails = options.saveFails ?? false;
  const audit = {
    events: [] as AuditEventDraft[],
    async append(entries: readonly AuditEventDraft[]) {
      if (options.auditFails) throw new Error("audit store unavailable");
      this.events.push(...entries);
    },
  };
  const printer = {
    jobs: [] as DailyClosePrintJob[],
    async print(job: DailyClosePrintJob) {
      this.jobs.push(job);
      events.push(`print:${job.archive.closeId}`);
      if (options.printFails) throw new Error("usb://secret-printer");
      if (options.cashSlipFails) {
        throw Object.assign(new Error("cash slip"), {
          code: "DAILY_CLOSE_CASH_SLIP_NOT_CONFIRMED",
        });
      }
    },
  };
  const ids = ["close-1", "audit-1", "close-2", "audit-2"];
  const presenter = new DailyClosePresenter({
    businessTimeZone: "Australia/Brisbane",
    createId: () => ids.shift() ?? "unexpected-id",
    audit,
    identity: {
      cashierId: "C1",
      cashierName: "Alice",
      userGuid: "U1",
      deviceCode: "IPAD-1",
      permissions:
        options.permissions ??
        [
          DAILY_CLOSE_VIEW_PERMISSION,
          DAILY_CLOSE_SAVE_PERMISSION,
          DAILY_CLOSE_REPRINT_PERMISSION,
        ],
      storeCode: "S1",
    },
    initialBusinessDate: "2026-07-28",
    now: () => new Date("2026-07-28T08:00:00.000Z"),
    printer,
    receiptLocale: "en",
    receiptPaper: "58mm",
    repository,
    storeName: "Sunnybank",
    returnPolicy: "Refunds and returns are accepted within fourteen days.",
  });
  return { audit, events, presenter, printer, repository };
}

class MemoryRepository implements DailyCloseRepositoryPort {
  public readonly archives: DailyCloseArchive[] = [];
  public readonly commits: DailyCloseArchiveCommit[] = [];
  public readonly summaryScopes: DailyCloseScope[] = [];
  public saveFails = false;

  public constructor(private readonly events: string[]) {}

  public async summarize(scope: DailyCloseScope): Promise<DailyCloseSummary> {
    this.summaryScopes.push(scope);
    return summary(scope);
  }

  public async saveArchive(input: DailyCloseArchiveCommit) {
    if (this.saveFails) throw new Error("sqlite://secret-path");
    this.events.push(`save:${input.archive.closeId}`);
    this.commits.push(input);
    this.archives.unshift(input.archive);
    return { replayed: false, archive: input.archive };
  }

  public async getArchive(closeId: string) {
    return (
      this.archives.find((candidate) => candidate.closeId === closeId) ??
      null
    );
  }

  public async listArchives(input: {
    storeCode: string;
    deviceCode: string;
    businessDate?: string | null;
    limit: number;
  }) {
    return this.archives
      .filter(
        (candidate) =>
          candidate.storeCode === input.storeCode &&
          candidate.deviceCode === input.deviceCode &&
          (!input.businessDate ||
            candidate.businessDate === input.businessDate),
      )
      .slice(0, input.limit);
  }
}

function summary(scope: DailyCloseScope): DailyCloseSummary {
  return {
    ...scope,
    orderCount: 4,
    returnQuantity: "2",
    tenders: [
      {
        method: "cash",
        salesCents: 1_200,
        refundCents: -200,
        netCents: 1_000,
      },
      {
        method: "card",
        salesCents: 2_000,
        refundCents: 0,
        netCents: 2_000,
      },
      {
        method: "voucher",
        salesCents: 500,
        refundCents: -100,
        netCents: 400,
      },
    ],
    cashRoundingCents: 0,
    expectedCashCents: 1_000,
  };
}

function archive(closeId: string): DailyCloseArchive {
  const scope: DailyCloseScope = {
    businessDate: "2026-07-28",
    periodFromIso: "2026-07-27T14:00:00.000Z",
    periodToIso: "2026-07-28T14:00:00.000Z",
    storeCode: "S1",
    deviceCode: "IPAD-1",
  };
  return {
    ...summary(scope),
    closeId,
    savedCashierId: "C1",
    savedCashierName: "Alice",
    savedAtIso: "2026-07-28T08:00:00.000Z",
    denominations: [
      10_000, 5_000, 2_000, 1_000, 500, 200, 100, 50, 20, 10, 5,
    ].map((denominationCents) => ({
      denominationCents:
        denominationCents as DailyCloseArchive["denominations"][number]["denominationCents"],
      quantity: 0,
      subtotalCents: 0,
    })),
    notesSubtotalCents: 0,
    coinsSubtotalCents: 0,
    countedCashCents: 0,
    varianceCents: -1_000,
  };
}
