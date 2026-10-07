import {
  normalizeDailyCloseCounts,
  type DailyCloseArchive,
} from "@hb/pos-domain/core/contracts/daily-close";

/** 仅供测试使用的日结存档夹具：汇总值全部由面额与支付方式推出，内部自洽。 */
export const DAILY_CLOSE_FIXTURE_GUID = "6f9619ff-8b86-4011-b42d-00c04fc964ff";
export const DAILY_CLOSE_FIXTURE_SCOPE = Object.freeze({
  storeCode: "S001",
  deviceCode: "DEV-1",
});

export function archiveFixture(
  overrides: Partial<DailyCloseArchive> & {
    counts?: readonly { denominationCents: number; quantity: number }[];
    tenders?: DailyCloseArchive["tenders"];
  } = {},
): DailyCloseArchive {
  const { counts, ...rest } = overrides;
  const denominations = normalizeDailyCloseCounts(
    counts ?? [
      { denominationCents: 10_000, quantity: 1 },
      { denominationCents: 500, quantity: 2 },
      { denominationCents: 200, quantity: 1 },
      { denominationCents: 5, quantity: 3 },
    ],
  );
  const notesSubtotalCents = denominations
    .filter((entry) => entry.denominationCents >= 500)
    .reduce((sum, entry) => sum + entry.subtotalCents, 0);
  const coinsSubtotalCents = denominations
    .filter((entry) => entry.denominationCents < 500)
    .reduce((sum, entry) => sum + entry.subtotalCents, 0);
  const countedCashCents = notesSubtotalCents + coinsSubtotalCents;
  const tenders: DailyCloseArchive["tenders"] = rest.tenders ?? [
    { method: "cash", salesCents: 11_005, refundCents: -205, netCents: 10_800 },
    { method: "card", salesCents: 435, refundCents: 0, netCents: 435 },
    { method: "voucher", salesCents: 0, refundCents: -115, netCents: -115 },
  ];
  const expectedCashCents =
    tenders.find((entry) => entry.method === "cash")?.netCents ?? 0;
  return {
    businessDate: "2026-10-07",
    periodFromIso: "2026-10-06T14:00:00.000Z",
    periodToIso: "2026-10-07T14:00:00.000Z",
    storeCode: DAILY_CLOSE_FIXTURE_SCOPE.storeCode,
    deviceCode: DAILY_CLOSE_FIXTURE_SCOPE.deviceCode,
    orderCount: 7,
    returnQuantity: "1.75",
    expectedCashCents,
    closeId: DAILY_CLOSE_FIXTURE_GUID,
    savedCashierId: "cashier-1",
    savedCashierName: "Cashier One",
    savedAtIso: "2026-10-07T13:30:00.123Z",
    denominations,
    notesSubtotalCents,
    coinsSubtotalCents,
    countedCashCents,
    varianceCents: countedCashCents - expectedCashCents,
    ...rest,
    tenders,
  };
}
