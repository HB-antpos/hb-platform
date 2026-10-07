import assert from "node:assert/strict";
import test from "node:test";

import {
  AUD_CASH_DENOMINATIONS_CENTS,
  type DailyCloseArchive,
} from "@hb/pos-domain/core/contracts/daily-close";

import {
  DAILY_CLOSE_FIXTURE_GUID,
  DAILY_CLOSE_FIXTURE_SCOPE,
  archiveFixture,
} from "../../testing/daily-close-fixtures";
import { validateAgainstServerRules } from "../../testing/daily-close-server-rules";
import {
  DailyCloseSyncMappingError,
  centsToAmount,
  mapDailyCloseArchiveToSyncRequest,
  normalizeDailyCloseAppVersion,
  returnQuantityToNumber,
} from "./daily-close-sync-request";

const SCOPE = DAILY_CLOSE_FIXTURE_SCOPE;
const GUID = DAILY_CLOSE_FIXTURE_GUID;

function formatCents(cents: number): string {
  const absolute = Math.abs(cents);
  const text = `${Math.floor(absolute / 100)}.${String(absolute % 100).padStart(2, "0")}`;
  return `${cents < 0 ? "-" : ""}${text}`;
}

test("分换算成元：易错值精确，序列化后恰为两位小数，且不产生 -0", () => {
  const cases: readonly (readonly [number, string])[] = [
    [0, "0"],
    [1, "0.01"],
    [5, "0.05"],
    [7, "0.07"],
    [10, "0.1"],
    [15, "0.15"],
    [99, "0.99"],
    [100, "1"],
    [105, "1.05"],
    [110, "1.1"],
    [115, "1.15"],
    [435, "4.35"],
    [1_999, "19.99"],
    [-5, "-0.05"],
    [-115, "-1.15"],
    [10_000_000_000, "100000000"],
    [99_999_999_999, "999999999.99"],
  ];
  for (const [cents, expected] of cases) {
    const amount = centsToAmount(cents);
    assert.equal(JSON.stringify(amount), expected, `${cents} 分`);
  }
  assert.ok(Object.is(centsToAmount(0), 0), "0 分不得是 -0");
  assert.ok(Object.is(centsToAmount(-0), 0), "-0 分不得是 -0");
});

test("分换算成元：随机大样本与十进制字符串逐一精确一致（不依赖 cents / 100 的浮点结果）", () => {
  let seed = 0x2f6e2b1;
  const next = () => {
    seed = (Math.imul(seed, 1_664_525) + 1_013_904_223) >>> 0;
    return seed;
  };
  for (let index = 0; index < 20_000; index += 1) {
    // 覆盖 ±1e10 分（服务端 1e8 元上限）内的各种数量级。
    const magnitude = 10 ** (next() % 11);
    const cents = Math.trunc(((next() / 0x1_0000_0000) * 2 - 1) * magnitude);
    const text = formatCents(cents).replace(/(\.\d*?)0+$/u, "$1").replace(/\.$/u, "");
    assert.equal(JSON.stringify(centsToAmount(cents)), text, `${cents} 分`);
  }
});

test("分换算拒绝非整数、非安全整数与超出精确表示范围的金额", () => {
  for (const bad of [0.5, Number.NaN, Number.POSITIVE_INFINITY, Number.MAX_SAFE_INTEGER, 2 ** 53]) {
    assert.throws(
      () => centsToAmount(bad),
      (error: unknown) =>
        error instanceof DailyCloseSyncMappingError &&
        error.code === "DAILY_CLOSE_AMOUNT_UNSAFE",
    );
  }
});

test("退货数量：先按远离零四舍五入到 3 位小数再转 number，无法精确表示则拒绝", () => {
  const cases: readonly (readonly [string, number])[] = [
    ["0", 0],
    ["1", 1],
    ["1.75", 1.75],
    ["12.345", 12.345],
    ["0.0005", 0.001],
    ["0.0004", 0],
    ["2.9995", 3],
    ["1.2345", 1.235],
    ["99999999.9999", 100_000_000],
  ];
  for (const [text, expected] of cases) {
    assert.equal(returnQuantityToNumber(text), expected, text);
  }
  for (const bad of ["", "-1", "1e3", "01", "1.", ".5", "abc", "1234567890123456789.5"]) {
    assert.throws(
      () => returnQuantityToNumber(bad),
      (error: unknown) =>
        error instanceof DailyCloseSyncMappingError &&
        error.code === "DAILY_CLOSE_RETURN_QUANTITY_INVALID",
      bad,
    );
  }
});

test("appVersion：去首尾空白、空串为 null、超过 64 字符截断", () => {
  assert.equal(normalizeDailyCloseAppVersion("  1.2.3  "), "1.2.3");
  assert.equal(normalizeDailyCloseAppVersion("   "), null);
  assert.equal(normalizeDailyCloseAppVersion(undefined), null);
  assert.equal(normalizeDailyCloseAppVersion(null), null);
  assert.equal(normalizeDailyCloseAppVersion("v".repeat(80))?.length, 64);
});

test("请求映射：身份、范围、时间、收银员与退款方向（退款上传正数、Net = Sales − Refund）", () => {
  const request = mapDailyCloseArchiveToSyncRequest(archiveFixture(), {
    clientKind: "Handheld",
    appVersion: "0.1.2",
  });
  assert.equal(request.schemaVersion, 1);
  assert.equal(request.dailyCloseGuid, GUID);
  assert.equal(request.storeCode, "S001");
  assert.equal(request.deviceCode, "DEV-1");
  assert.equal(request.clientKind, "Handheld");
  assert.equal(request.businessDate, "2026-10-07");
  assert.equal(request.periodFrom, "2026-10-06T14:00:00.000Z");
  assert.equal(request.periodTo, "2026-10-07T14:00:00.000Z");
  assert.equal(request.savedAt, "2026-10-07T13:30:00.123Z");
  assert.equal(request.cashierId, "cashier-1");
  assert.equal(request.cashierName, "Cashier One");
  assert.equal(request.appVersion, "0.1.2");
  assert.equal(request.orderCount, 7);
  assert.equal(request.returnQuantity, 1.75);
  // 退款总额 = 各支付方式退款绝对值之和：2.05 + 0 + 1.15。
  assert.equal(request.refundAmount, 3.2);
  assert.deepEqual(request.tenders, [
    { method: "Cash", salesAmount: 110.05, refundAmount: 2.05, netAmount: 108 },
    { method: "Card", salesAmount: 4.35, refundAmount: 0, netAmount: 4.35 },
    { method: "Voucher", salesAmount: 0, refundAmount: 1.15, netAmount: -1.15 },
  ]);
  assert.equal(
    request.tenders?.every((tender) => !Object.is(tender.refundAmount, -0)),
    true,
  );
});

test("请求映射：恰好 11 档面额（面额为整数分、缺档补 0）与金额换算", () => {
  const request = mapDailyCloseArchiveToSyncRequest(
    archiveFixture({
      counts: [
        { denominationCents: 5, quantity: 1 },
        { denominationCents: 50, quantity: 4 },
      ],
    }),
    { clientKind: "Ipad" },
  );
  assert.equal(request.clientKind, "Ipad");
  assert.deepEqual(
    request.cashCounts?.map((entry) => entry.denominationCents),
    [...AUD_CASH_DENOMINATIONS_CENTS],
  );
  assert.equal(request.cashCounts?.length, 11);
  assert.equal(
    request.cashCounts?.every((entry) => Number.isInteger(entry.denominationCents)),
    true,
  );
  assert.deepEqual(
    request.cashCounts?.filter((entry) => entry.quantity !== 0),
    [
      { denominationCents: 50, quantity: 4 },
      { denominationCents: 5, quantity: 1 },
    ],
  );
  assert.equal(request.noteSubtotal, 0);
  assert.equal(request.coinSubtotal, 2.05);
  assert.equal(request.countedCashAmount, 2.05);
  assert.equal(request.cashDifference, 2.05 - 108);
  assert.equal(request.appVersion, null);
});

test("请求映射：存档缺面额档时补 0，保证 11 档齐全且不改动其它数据", () => {
  const archive = archiveFixture();
  const partial: DailyCloseArchive = {
    ...archive,
    denominations: archive.denominations.filter((entry) => entry.quantity > 0),
  };
  const request = mapDailyCloseArchiveToSyncRequest(partial, { clientKind: "Handheld" });
  assert.equal(request.cashCounts?.length, 11);
  assert.equal(
    request.cashCounts?.find((entry) => entry.denominationCents === 100)?.quantity,
    0,
  );
});

test("请求映射：缺少偏移的时间串按设备时区确定为 UTC 时刻，带偏移的原样使用", () => {
  const withOffset = mapDailyCloseArchiveToSyncRequest(
    archiveFixture({ periodFromIso: "2026-10-07T00:00:00.000+10:00" }),
    { clientKind: "Handheld" },
  );
  assert.equal(withOffset.periodFrom, "2026-10-07T00:00:00.000+10:00");
  const naive = mapDailyCloseArchiveToSyncRequest(
    archiveFixture({ periodFromIso: "2026-10-07T00:00:00" }),
    { clientKind: "Handheld" },
  );
  assert.match(naive.periodFrom ?? "", /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/u);
  assert.equal(naive.periodFrom, new Date("2026-10-07T00:00:00").toISOString());
});

test("请求映射：本地数据无法映射时抛出带稳定错误码的映射错误（不修正数据）", () => {
  const archive = archiveFixture();
  assert.throws(
    () =>
      mapDailyCloseArchiveToSyncRequest(
        { ...archive, tenders: archive.tenders.filter((entry) => entry.method !== "card") },
        { clientKind: "Handheld" },
      ),
    (error: unknown) =>
      error instanceof DailyCloseSyncMappingError &&
      error.code === "DAILY_CLOSE_TENDER_MISSING",
  );
  assert.throws(
    () =>
      mapDailyCloseArchiveToSyncRequest(
        { ...archive, periodToIso: "not-a-date" },
        { clientKind: "Handheld" },
      ),
    (error: unknown) =>
      error instanceof DailyCloseSyncMappingError &&
      error.code === "DAILY_CLOSE_TIMESTAMP_INVALID",
  );
  assert.throws(
    () =>
      mapDailyCloseArchiveToSyncRequest(
        { ...archive, businessDate: "2026/10/07" },
        { clientKind: "Handheld" },
      ),
    (error: unknown) =>
      error instanceof DailyCloseSyncMappingError &&
      error.code === "DAILY_CLOSE_BUSINESS_DATE_INVALID",
  );
});

test("服务端规则复刻：映射结果通过全部校验（含 0.05 / 1.15 / 4.35 等易错金额、全零、纯退款、负差额）", () => {
  const archives: readonly DailyCloseArchive[] = [
    archiveFixture(),
    // 全零日结：没有订单、没有现金。
    archiveFixture({
      counts: [],
      orderCount: 0,
      returnQuantity: "0",
      tenders: [
        { method: "cash", salesCents: 0, refundCents: 0, netCents: 0 },
        { method: "card", salesCents: 0, refundCents: 0, netCents: 0 },
        { method: "voucher", salesCents: 0, refundCents: 0, netCents: 0 },
      ],
    }),
    // 纯退款日：现金净额为负，实点为 0.05 → 差额为正。
    archiveFixture({
      counts: [{ denominationCents: 5, quantity: 1 }],
      tenders: [
        { method: "cash", salesCents: 0, refundCents: -435, netCents: -435 },
        { method: "card", salesCents: 115, refundCents: -5, netCents: 110 },
        { method: "voucher", salesCents: 7, refundCents: -7, netCents: 0 },
      ],
    }),
    // 短款：实点远小于现金净额。
    archiveFixture({
      counts: [{ denominationCents: 100, quantity: 3 }],
      tenders: [
        { method: "cash", salesCents: 123_456, refundCents: -1_001, netCents: 122_455 },
        { method: "card", salesCents: 0, refundCents: 0, netCents: 0 },
        { method: "voucher", salesCents: 0, refundCents: 0, netCents: 0 },
      ],
    }),
    // 大面额大数量。
    archiveFixture({
      counts: AUD_CASH_DENOMINATIONS_CENTS.map((denominationCents) => ({
        denominationCents,
        quantity: 9_999,
      })),
    }),
  ];
  for (const [index, archive] of archives.entries()) {
    const request = mapDailyCloseArchiveToSyncRequest(archive, {
      clientKind: "Handheld",
      appVersion: "1.0.0",
    });
    assert.deepEqual(validateAgainstServerRules(request, SCOPE), { ok: true }, `样例 ${index}`);
  }
});

test("服务端规则复刻自检：不满足规则的请求会被拒绝（防止复刻函数形同虚设）", () => {
  const good = mapDailyCloseArchiveToSyncRequest(archiveFixture(), { clientKind: "Handheld" });
  const failing = (mutate: (request: typeof good) => typeof good) =>
    validateAgainstServerRules(mutate(good), SCOPE);
  assert.deepEqual(
    failing((request) => ({ ...request, noteSubtotal: (request.noteSubtotal ?? 0) + 0.01 })),
    { ok: false, code: "INVALID_NOTE_SUBTOTAL" },
  );
  assert.deepEqual(
    failing((request) => ({
      ...request,
      // 把退款当负数发（本地原始符号）：Net ≠ Sales − Refund。
      tenders: (request.tenders ?? []).map((tender) => ({
        ...tender,
        refundAmount: -(tender.refundAmount ?? 0),
      })),
    })),
    { ok: false, code: "TENDER_NET_MISMATCH" },
  );
  assert.deepEqual(
    failing((request) => ({ ...request, cashCounts: (request.cashCounts ?? []).slice(1) })),
    { ok: false, code: "INVALID_CASH_COUNTS" },
  );
  assert.deepEqual(
    failing((request) => ({ ...request, savedAt: "2026-10-07T13:30:00" })),
    { ok: false, code: "SAVED_AT_REQUIRED" },
  );
  assert.deepEqual(
    validateAgainstServerRules(good, { storeCode: "S001", deviceCode: "OTHER" }),
    { ok: false, code: "DEVICE_SCOPE_FORBIDDEN" },
  );
  assert.deepEqual(
    failing((request) => ({ ...request, cashDifference: (request.cashDifference ?? 0) + 0.5 })),
    { ok: false, code: "INVALID_CASH_DIFFERENCE" },
  );
});
