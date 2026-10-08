import {
  computeSeasonalCardHolidayWindow,
  computeSeasonalCardHolidayWindows,
  findNextSeasonalCardOpening,
  formatSeasonalCardShortDay,
  pickSeasonalCardDefaultHoliday,
  resolveSeasonalCardHolidayWindows,
  type SeasonalCardHolidayWindow,
} from "./holiday-window";
import type { SeasonalCardOverview } from "./types";

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

// ---- 节日日期与开放窗口（与后端 SeasonalCardHolidayCalendar 同口径） ----
const october = computeSeasonalCardHolidayWindows("2026-10-08");
assertDeepEqual(
  october.map((item) => [item.cardType, item.isOpen, item.opensOn]),
  [
    [1, false, "2026-12-25"],
    [2, false, "2027-02-14"],
    [3, false, "2027-05-09"],
    [4, false, "2027-03-28"],
    [5, false, "2027-09-05"],
  ],
  "on 2026-10-08 nothing is open; closed holidays point to their next date (Mother's Day = 2nd Sunday of May, Easter Sunday, Father's Day = 1st Sunday of September)"
);
assertEqual(october[0]?.seasonYear, 2026, "closed holidays carry the year of the next holiday");

const mothers2026 = computeSeasonalCardHolidayWindow(3, "2026-05-10");
assertDeepEqual(
  [mothers2026.isOpen, mothers2026.seasonYear, mothers2026.opensOn, mothers2026.closesOn],
  [true, 2026, "2026-05-10", "2026-06-07"],
  "Mother's Day 2026 opens on 5/10 and closes 28 days later (29 days in total)"
);
assertEqual(computeSeasonalCardHolidayWindow(4, "2026-04-05").holidayDate, "2026-04-05", "Easter 2026 is 4/5");
assertEqual(computeSeasonalCardHolidayWindow(5, "2026-09-06").isOpen, true, "Father's Day 2026 is 9/6");
assertEqual(computeSeasonalCardHolidayWindow(2, "2026-02-13").isOpen, false, "the day before the holiday is not open");

const xmasCrossYear = computeSeasonalCardHolidayWindow(1, "2027-01-10");
assertDeepEqual(
  [xmasCrossYear.isOpen, xmasCrossYear.seasonYear, xmasCrossYear.opensOn, xmasCrossYear.closesOn],
  [true, 2026, "2026-12-25", "2027-01-22"],
  "in January the Christmas window still belongs to the previous year"
);
assertEqual(computeSeasonalCardHolidayWindow(1, "2027-01-22").isOpen, true, "the closing day is included");
const afterXmas = computeSeasonalCardHolidayWindow(1, "2027-01-23");
assertDeepEqual(
  [afterXmas.isOpen, afterXmas.seasonYear, afterXmas.opensOn],
  [false, 2027, "2027-12-25"],
  "after the window closes the next Christmas is shown"
);

// ---- 服务端窗口优先；字段缺失（旧后端）按开放兜底 ----
const overview: SeasonalCardOverview = {
  storeCode: "1013",
  seasonYear: 2027,
  today: "2027-01-10",
  localSupplierCode: "SUP-A01",
  supplierName: "A",
  holidays: [
    { cardType: 1, cardTypeName: "", isOpen: true, seasonYear: 2026, holidayDate: "2026-12-25", opensOn: "2026-12-25", closesOn: "2027-01-22", currentBatch: null },
    { cardType: 2, cardTypeName: "", isOpen: false, seasonYear: 2027, holidayDate: "2027-02-14", opensOn: "2027-02-14", closesOn: "2027-03-14", currentBatch: null },
    { cardType: 3, cardTypeName: "", isOpen: false, seasonYear: 2027, holidayDate: "2027-05-09", opensOn: "2027-05-09", closesOn: "2027-06-06", currentBatch: null },
    { cardType: 4, cardTypeName: "", isOpen: false, seasonYear: 2027, holidayDate: "2027-03-28", opensOn: "2027-03-28", closesOn: "2027-04-25", currentBatch: null },
    { cardType: 5, cardTypeName: "", isOpen: false, seasonYear: 2027, holidayDate: "2027-09-05", opensOn: "2027-09-05", closesOn: "2027-10-03", currentBatch: null },
  ],
};
const fromServer = resolveSeasonalCardHolidayWindows(overview, "2026-10-08");
assertEqual(fromServer[0]?.isOpen, true, "server windows win over the local date");
assertEqual(fromServer[0]?.seasonYear, 2026, "server season year is used");

const legacyOverview: SeasonalCardOverview = {
  ...overview,
  seasonYear: 2026,
  today: "",
  holidays: overview.holidays.map((holiday) => ({
    ...holiday,
    isOpen: true,
    seasonYear: 2026,
    holidayDate: "",
    opensOn: "",
    closesOn: "",
  })),
};
const legacy = resolveSeasonalCardHolidayWindows(legacyOverview, "2026-10-08");
assertEqual(legacy.every((item) => item.isOpen), true, "legacy backend: every holiday counts as open");
assertEqual(
  pickSeasonalCardDefaultHoliday(legacy, null),
  1,
  "legacy backend: unknown close dates fall back to card type order"
);
assertEqual(
  resolveSeasonalCardHolidayWindows(null, "2027-01-10")[0]?.seasonYear,
  2026,
  "without an overview the local calendar is used"
);

// ---- 默认选中 ----
function window(
  cardType: 1 | 2 | 3 | 4 | 5,
  isOpen: boolean,
  closesOn: string,
  opensOn = ""
): SeasonalCardHolidayWindow {
  return { cardType, isOpen, seasonYear: 2026, holidayDate: opensOn, opensOn, closesOn };
}
const twoOpen = [
  window(1, false, "2026-01-22", "2026-12-25"),
  window(3, true, "2026-06-07", "2026-05-10"),
  window(4, true, "2026-05-03", "2026-04-05"),
  window(5, false, "2026-10-04", "2026-09-06"),
];
assertEqual(pickSeasonalCardDefaultHoliday(twoOpen, null), 4, "picks the open holiday that closes first");
assertEqual(pickSeasonalCardDefaultHoliday(twoOpen, 3), 3, "keeps the previous choice while it is still open");
assertEqual(pickSeasonalCardDefaultHoliday(twoOpen, 1), 4, "drops a previous choice that is no longer open");
assertEqual(pickSeasonalCardDefaultHoliday(october, 1), null, "no open holiday → nothing selected");

assertEqual(findNextSeasonalCardOpening(october)?.cardType, 1, "next opening is the earliest opensOn (Christmas 12/25)");
assertEqual(findNextSeasonalCardOpening(legacy), null, "no closed holidays → no next opening");

// ---- 短日期 ----
assertEqual(formatSeasonalCardShortDay("2026-12-25", "zh"), "12/25", "Chinese short day is M/D");
assertEqual(formatSeasonalCardShortDay("2027-01-22", "en"), "22 Jan", "English short day avoids M/D ambiguity");
assertEqual(formatSeasonalCardShortDay("", "zh"), "", "unknown dates format to empty");
