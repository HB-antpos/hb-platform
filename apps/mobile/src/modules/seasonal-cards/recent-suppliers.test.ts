import {
  buildSeasonalCardRecentSuppliersKey,
  normalizeSeasonalCardRecentSuppliers,
  pushSeasonalCardRecentSupplier,
  reconcileSeasonalCardRecentSuppliers,
} from "./recent-suppliers";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual);
  const expectedText = JSON.stringify(expected);
  if (actualText !== expectedText) {
    throw new Error(`${label}: expected ${expectedText}, got ${actualText}`);
  }
}

assertEqual(
  buildSeasonalCardRecentSuppliersKey(" 1013 "),
  "seasonalCards.recentSuppliers.v1:1013",
  "recent suppliers are stored per store"
);

assertDeepEqual(
  normalizeSeasonalCardRecentSuppliers("broken"),
  [],
  "corrupted storage reads as empty"
);

assertDeepEqual(
  normalizeSeasonalCardRecentSuppliers([
    { supplierCode: "A", supplierName: "Alpha" },
    { supplierCode: "a", supplierName: "dup" },
    { supplierCode: "", supplierName: "no code" },
    { supplierCode: "B" },
    { supplierCode: "C", supplierName: "Gamma" },
    { supplierCode: "D", supplierName: "Delta" },
  ]),
  [
    { supplierCode: "A", supplierName: "Alpha" },
    { supplierCode: "B", supplierName: "B" },
    { supplierCode: "C", supplierName: "Gamma" },
  ],
  "normalize de-duplicates, drops blank codes and keeps at most 3"
);

const pushed = pushSeasonalCardRecentSupplier(
  [
    { supplierCode: "A", supplierName: "Alpha" },
    { supplierCode: "B", supplierName: "Beta" },
    { supplierCode: "C", supplierName: "Gamma" },
  ],
  { supplierCode: "C", supplierName: "Gamma 2" }
);
assertDeepEqual(
  pushed.map((item) => item.supplierCode),
  ["C", "A", "B"],
  "the latest used supplier moves to the front"
);
assertEqual(pushed[0]?.supplierName, "Gamma 2", "the latest name wins");

assertDeepEqual(
  pushSeasonalCardRecentSupplier(pushed, { supplierCode: "D", supplierName: "Delta" }).map(
    (item) => item.supplierCode
  ),
  ["D", "C", "A"],
  "the oldest supplier drops off after 3"
);

assertDeepEqual(
  reconcileSeasonalCardRecentSuppliers(
    [
      { supplierCode: "A", supplierName: "Old name" },
      { supplierCode: "GONE", supplierName: "Disabled" },
    ],
    [{ supplierCode: "a", supplierName: "Alpha" }]
  ),
  [{ supplierCode: "a", supplierName: "Alpha" }],
  "recent suppliers use the active list's names and hide disabled suppliers"
);
assertEqual(
  reconcileSeasonalCardRecentSuppliers([{ supplierCode: "A", supplierName: "x" }], null).length,
  1,
  "without the active list recent suppliers are shown as stored"
);
