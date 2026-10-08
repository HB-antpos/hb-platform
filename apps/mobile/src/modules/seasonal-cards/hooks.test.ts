import {
  seasonalCardQueryKeys,
  shouldEnableSeasonalCardCatalog,
  shouldEnableSeasonalCardOverview,
} from "./hooks";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

assertEqual(
  shouldEnableSeasonalCardCatalog(true),
  true,
  "submit-capable sessions should enable catalog query"
);

assertEqual(
  shouldEnableSeasonalCardCatalog(false),
  false,
  "view-only sessions should not enable catalog query"
);

assertEqual(
  shouldEnableSeasonalCardOverview(true, {
    storeCode: "1013",
    seasonYear: 2026,
    localSupplierCode: "SUP-A01",
  }),
  true,
  "overview query runs once store, year and supplier are all selected"
);

assertEqual(
  shouldEnableSeasonalCardOverview(true, {
    storeCode: "1013",
    seasonYear: 2026,
    localSupplierCode: " ",
  }),
  false,
  "overview query waits for a supplier"
);

assertEqual(
  shouldEnableSeasonalCardOverview(false, {
    storeCode: "1013",
    seasonYear: 2026,
    localSupplierCode: "SUP-A01",
  }),
  false,
  "view-only sessions should not request the overview"
);

// 提交后按前缀失效：具体查询键必须以前缀开头，否则 invalidateQueries 不会命中。
const keys = seasonalCardQueryKeys();
const overviewKey = keys.overview({
  storeCode: "1013",
  seasonYear: 2026,
  localSupplierCode: "SUP-A01",
});
assertEqual(
  keys.overviewAll.every((part, index) => overviewKey[index] === part),
  true,
  "overview keys share the invalidation prefix"
);
const submissionsKey = keys.submissions({ storeCode: "1013" });
assertEqual(
  keys.submissionsAll.every((part, index) => submissionsKey[index] === part),
  true,
  "submission keys share the invalidation prefix"
);
