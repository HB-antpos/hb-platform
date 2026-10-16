import { DEFAULT_LIST_DATE_RANGE_MONTHS, getDefaultListDateRange } from "./default-list-date-range";

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual);
  const expectedText = JSON.stringify(expected);
  if (actualText !== expectedText) {
    throw new Error(`${label}: expected ${expectedText}, got ${actualText}`);
  }
}

assertDeepEqual(DEFAULT_LIST_DATE_RANGE_MONTHS, 6, "默认 6 个月");

assertDeepEqual(
  getDefaultListDateRange(new Date(2026, 9, 17)),
  { from: "2026-04-17", to: "2026-10-17" },
  "普通日期：6 个月前的同一天到今天"
);

assertDeepEqual(
  getDefaultListDateRange(new Date(2026, 7, 31)),
  { from: "2026-02-28", to: "2026-08-31" },
  "8 月 31 日往前推：2 月没有 31 日，取 2 月最后一天而不是溢出到 3 月"
);

assertDeepEqual(
  getDefaultListDateRange(new Date(2028, 7, 31)),
  { from: "2028-02-29", to: "2028-08-31" },
  "闰年 2 月取 29 日"
);

assertDeepEqual(
  getDefaultListDateRange(new Date(2026, 2, 15)),
  { from: "2025-09-15", to: "2026-03-15" },
  "跨年：3 月往前推 6 个月落在上一年 9 月"
);

assertDeepEqual(
  getDefaultListDateRange(new Date(2026, 0, 1)),
  { from: "2025-07-01", to: "2026-01-01" },
  "1 月 1 日跨年边界"
);

assertDeepEqual(
  getDefaultListDateRange(new Date(2026, 9, 17), 3),
  { from: "2026-07-17", to: "2026-10-17" },
  "月数可参数化"
);
