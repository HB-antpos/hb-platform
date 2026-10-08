import assert from "node:assert/strict";
import {
  CONTAINER_DETAIL_DEFAULT_PAGE_SIZE,
  CONTAINER_DETAIL_PAGE_SIZE,
  CONTAINER_DETAIL_PAGE_SIZE_OPTIONS,
  CONTAINER_DETAIL_SORT_OPTIONS,
  DEFAULT_CONTAINER_DETAIL_SORT,
  DEFAULT_PUSH_PRODUCTS_TO_HQ_UPDATE_FIELDS,
  buildAlignDomesticProductCodePayload,
  buildContainerDetailFilterQuery,
  buildContainerDetailOverview,
  buildCreatedProductsHqPushPlan,
  countActiveContainerDetailFilters,
  createEmptyContainerDetailFilters,
  findContainerDetailsMissingRetailPrice,
  findInvalidContainerDetailRangePairs,
  getContainerDetailPageCount,
  getContainerLoadRatePercent,
  normalizeContainerDetailPageSize,
  normalizeContainerDetailSort,
  resetContainerDetailFilters,
  resolveContainerDetailOverviewStats,
  toggleContainerDetailFilterOption,
  toggleContainerDetailSort,
  buildContainerDetailHqPushSelection,
  buildContainerDetailQuery,
  buildDetailDetectionItems,
  buildContainerListPayload,
  buildCreateProductsOperationId,
  buildPushProductsToHqOperationId,
  buildBatchScope,
  getCurrentPageDetailGuids,
  getDetailImageUrl,
  getDetailLocalSupplierCode,
  getDetailMatchType,
  getDetailReadonlyOemPrice,
  getDetailRealtimeImportPrice,
  getDetailRealtimeRetailPrice,
  getDetailVisibleOemPrice,
  hasDetailProductCodeConflict,
  mergeDetailDetectionResults,
  normalizeAlignDomesticProductCodePreview,
  normalizeAlignDomesticProductCodeResult,
  normalizeCreateContainerResponse,
  normalizeContainerDetailResponse,
  normalizeContainerDetailQueryResult,
  normalizeContainerListResponse,
  normalizeContainerJob,
  toPushProductsToHqItems,
  toggleCurrentPageSelection,
} from "./query";

const listPayload = buildContainerListPayload({
  page: 2,
  containerNumberFilter: " C-01 ",
  itemNumberFilter: " A100 ",
  loadingDateStart: " 2026-01-01 ",
  totalPiecesMin: 10,
  totalAmountMax: 999.5,
});

assert.equal(listPayload.DateType, "预计到岸日期");
assert.equal(listPayload.SortBy, "预计到岸日期");
assert.equal(listPayload.SortDirection, "desc");
assert.equal(listPayload.ContainerNumberFilter, "C-01");
assert.equal(listPayload.ItemNumberFilter, "A100");
assert.equal(listPayload.Page, 2);
assert.equal(listPayload.LoadingDateStart, "2026-01-01");
assert.equal(listPayload.TotalPiecesMin, 10);
assert.equal(listPayload.TotalAmountMax, 999.5);

const detailQuery = buildContainerDetailQuery("container-1", {
  keyword: " hb-1 ",
  selectedTags: ["all", "new"],
  warehouseImportPriceMin: 5,
  warehouseImportPriceMax: 9,
  oemPriceMin: 10,
  oemPriceMax: 12,
});

assert.equal(detailQuery.itemNumber, "hb-1");
assert.equal(detailQuery.sortBy, "itemNumber");
assert.equal(detailQuery.sortOrder, "ascend");
assert.deepEqual(detailQuery.selectedTags, ["new"]);
assert.equal(detailQuery.includeTotal, true);
assert.equal(detailQuery.includeStats, true);
assert.equal(detailQuery.warehouseImportPriceMin, 5);
assert.equal(detailQuery.warehouseImportPriceMax, 9);
assert.equal(detailQuery.oemPriceMin, 10);
assert.equal(detailQuery.oemPriceMax, 12);

assert.deepEqual(
  buildBatchScope(detailQuery, [" D2 ", "", "D1"]),
  { selectedHguids: ["D2", "D1"] },
);
assert.deepEqual(
  buildBatchScope(detailQuery, []),
  {
    query: {
      ...detailQuery,
      includeTotal: false,
      includeStats: false,
    },
  },
);

assert.equal(
  buildCreateProductsOperationId("container-1", [" b ", "a"]),
  "container-create-products:container-1:a,b",
);
assert.equal(
  buildPushProductsToHqOperationId("container-1", ["P2", "P1"], 2, ["retailPrice", "barcode"]),
  "container-push-hq:container-1:P1,P2:2:barcode,retailPrice",
);

assert.deepEqual(
  buildAlignDomesticProductCodePayload({
    detailHguid: "D-ALIGN",
    expectedDomesticProductCode: "DOM-OLD",
    targetProductCode: "LOCAL-NEW",
    supplierCode: "200",
  }),
  {
    DetailHguid: "D-ALIGN",
    ExpectedDomesticProductCode: "DOM-OLD",
    TargetProductCode: "LOCAL-NEW",
    SupplierCode: "200",
  },
);

assert.equal(
  buildAlignDomesticProductCodePayload({
    detailHguid: "D-ALIGN",
    expectedDomesticProductCode: "DOM-OLD",
    targetProductCode: "LOCAL-NEW",
    supplierCode: "200",
    mergeIntoExistingDomesticProduct: true,
  }).MergeIntoExistingDomesticProduct,
  true,
);

const normalizedAlignPreview = normalizeAlignDomesticProductCodePreview({
  success: true,
  data: {
    mode: "Merge",
    affectedContainerDetails: 3,
    affectedContainers: 2,
    fields: [{ field: "Barcode", label: "条形码", existingValue: null, oldValue: "952", mergedValue: "952", filledFromOld: true }],
  },
});
assert.equal(normalizedAlignPreview.mode, "Merge");
assert.equal(normalizedAlignPreview.affectedContainerDetails, 3);
assert.deepEqual(normalizedAlignPreview.fields[0], {
  field: "Barcode",
  label: "条形码",
  existingValue: null,
  oldValue: "952",
  mergedValue: "952",
  filledFromOld: true,
});
assert.deepEqual(
  normalizeAlignDomesticProductCodeResult({ data: { Mode: "Merge", FilledFields: ["条形码", "国内价格"] } }).filledFields,
  ["条形码", "国内价格"],
);

const normalizedAlignResult = normalizeAlignDomesticProductCodeResult({
  data: {
    OldProductCode: "DOM-OLD",
    NewProductCode: "LOCAL-NEW",
    UpdatedDomesticProducts: "1",
    UpdatedContainerDetails: 2,
  },
});

assert.deepEqual(
  {
    oldProductCode: normalizedAlignResult.oldProductCode,
    newProductCode: normalizedAlignResult.newProductCode,
    updatedDomesticProducts: normalizedAlignResult.updatedDomesticProducts,
    updatedContainerDetails: normalizedAlignResult.updatedContainerDetails,
  },
  {
    oldProductCode: "DOM-OLD",
    newProductCode: "LOCAL-NEW",
    updatedDomesticProducts: 1,
    updatedContainerDetails: 2,
  },
);

const normalizedList = normalizeContainerListResponse({
  Items: [{ HGUID: "C1" }],
  TotalCount: 31,
  Page: 2,
  PageSize: 20,
});

assert.equal(normalizedList.containers[0]?.HGUID, "C1");
assert.equal(normalizedList.totalPages, 2);

const normalizedContainerDetail = normalizeContainerDetailResponse({
  success: true,
  data: { HGUID: "C1", 货柜编号: "CN-01" },
});

assert.equal(normalizedContainerDetail.HGUID, "C1");
assert.equal(normalizedContainerDetail.货柜编号, "CN-01");

assert.equal(
  normalizeCreateContainerResponse({
    success: true,
    data: { containerGuid: "C2" },
  }),
  "C2",
);

const normalizedDetail = normalizeContainerDetailQueryResult({
  Items: [{ HGUID: "D1" }],
  ItemsTotal: 40,
  PageNumber: 1,
  PageSize: 50,
  HasMore: true,
  TagStats: { all: 40, new: 3 },
}, detailQuery);

assert.equal(normalizedDetail.pageSize, 50);
assert.equal(normalizedDetail.items[0]?.HGUID, "D1");
assert.equal(normalizedDetail.hasMore, true);
assert.equal(normalizedDetail.tagStats.all, 40);
assert.equal(normalizedDetail.tagStats.new, 3);
assert.equal(normalizedDetail.tagStats.inactive, 0);

assert.equal(
  getDetailImageUrl({
    商品图片: " https://cdn.example.com/detail.png ",
    商品信息: { 商品图片: "https://cdn.example.com/product.png" },
  }),
  "https://cdn.example.com/detail.png",
);
assert.equal(
  getDetailImageUrl({
    商品信息: { 商品图片: " https://cdn.example.com/product.png " },
  }),
  "https://cdn.example.com/product.png",
);
assert.equal(
  toPushProductsToHqItems([{
    商品图片: " https://cdn.example.com/detail.png ",
    商品信息: { 商品图片: "https://cdn.example.com/product.png" },
  }])[0]?.imageUrl,
  "https://cdn.example.com/detail.png",
);

assert.equal(
  getDetailVisibleOemPrice({ 是否新商品: true, 贴牌价格: 2.2, warehouseOEMPrice: 6.6 }),
  2.2,
  "new product visible oem price uses detail oem price",
);
assert.equal(
  getDetailVisibleOemPrice({ 是否新商品: false, 贴牌价格: 2.2, warehouseOEMPrice: 6.6 }),
  6.6,
  "existing product visible oem price uses realtime warehouse retail price",
);
assert.equal(
  getDetailRealtimeImportPrice({ warehouseImportPrice: 5.5, LastImportPrice: 8.8 }),
  5.5,
  "realtime import price uses camelCase warehouse field",
);
assert.equal(
  getDetailRealtimeImportPrice({ LastImportPrice: 8.8 }),
  undefined,
  "realtime import price does not fall back to historical snapshot",
);
assert.equal(
  getDetailRealtimeRetailPrice({ WarehouseOEMPrice: 7.7, LastOEMPrice: 8.8, 贴牌价格: 2.2 }),
  7.7,
  "realtime retail price uses PascalCase warehouse field",
);
assert.equal(
  getDetailRealtimeRetailPrice({ LastOEMPrice: 8.8, 贴牌价格: 2.2 }),
  undefined,
  "realtime retail price does not fall back to historical snapshot or detail price",
);
assert.equal(
  getDetailReadonlyOemPrice({ readonlyOemPrice: 9.9, 贴牌价格: 2.2 }),
  9.9,
  "readonly oem price uses backend split price",
);
assert.equal(
  getDetailReadonlyOemPrice({ 贴牌价格: 2.2 }),
  undefined,
  "readonly oem price does not fall back to detail price",
);
assert.equal(
  hasDetailProductCodeConflict({ localProductCode: "LOCAL-1", domesticProductCode: "DOM-1" }),
  true,
  "different local and domestic product codes are a conflict",
);
assert.equal(
  getDetailMatchType({ localProductCode: "LOCAL-1", domesticProductCode: "DOM-1", matchType: "productCode" }),
  "supplierItem",
  "product code conflict is treated as supplier item match",
);
assert.equal(
  getDetailLocalSupplierCode({ localSupplierCode: " ", 商品信息: { localSupplierCode: " 201 " } }),
  "201",
  "align supplier code trims and falls back to product info",
);
assert.equal(
  getDetailLocalSupplierCode({}),
  "200",
  "missing align supplier code falls back to legacy HB supplier",
);
assert.equal(
  getDetailMatchType({ MatchType: "item_number" }),
  "supplierItem",
  "backend item_number match type is supplier item",
);
assert.equal(
  getDetailMatchType({ MatchType: "货号匹配" }),
  "supplierItem",
  "legacy Chinese item match type is supplier item",
);
assert.deepEqual(
  buildDetailDetectionItems([{
    商品编码: " DOM-1 ",
    localSupplierCode: "200",
    商品信息: { 货号: " SKU-1 ", 条形码: " BAR-1 " },
  }]),
  [{
    ProductCode: "DOM-1",
    SupplierCode: "200",
    ItemNumber: "SKU-1",
    Barcode: "BAR-1",
  }],
  "detection items preserve product code and supplier item candidate",
);
const mergedConflictDetails = mergeDetailDetectionResults(
  [{
    HGUID: "D-CONFLICT",
    商品编码: "DOM-1",
    localSupplierCode: "200",
    商品信息: { 货号: "SKU-1" },
  }],
  [{
    ProductCode: "DOM-1",
    MatchType: "item_number",
    LocalProductCode: "LOCAL-1",
    DomesticProductCode: "DOM-1",
    HasProductCodeConflict: true,
    ConflictReason: "国内商品编码与本地主档商品编码不一致",
  }],
);
assert.equal(hasDetailProductCodeConflict(mergedConflictDetails[0]!), true);
assert.equal(getDetailMatchType(mergedConflictDetails[0]!), "supplierItem");
assert.equal(mergedConflictDetails[0]?.localProductCode, "LOCAL-1");
assert.equal(
  toPushProductsToHqItems([{
    商品编码: "P1",
    是否新商品: false,
    贴牌价格: 2.2,
    warehouseOEMPrice: 6.6,
  }])[0]?.oemPrice,
  6.6,
  "HQ push uses visible oem price for existing products",
);
assert.equal(
  toPushProductsToHqItems([{
    商品编码: "P2",
    是否新商品: true,
    贴牌价格: 3.3,
    warehouseOEMPrice: 6.6,
  }])[0]?.oemPrice,
  3.3,
  "HQ push uses detail oem price for new products",
);
assert.deepEqual(
  buildContainerDetailHqPushSelection([{
    商品编码: "DOM-1",
    localSupplierCode: "200",
    商品信息: { 货号: "SKU-1" },
    localProductCode: "LOCAL-1",
    domesticProductCode: "DOM-1",
    是否新商品: true,
    贴牌价格: 2.2,
  }]),
  {
    productCodes: [],
    items: [{
      productCode: undefined,
      localSupplierCode: "200",
      itemNumber: "SKU-1",
      productName: "",
      englishName: "",
      barcode: "",
      imageUrl: undefined,
      domesticPrice: undefined,
      importPrice: undefined,
      oemPrice: 2.2,
      isNewProduct: true,
      warehouseIsActive: undefined,
    }],
  },
  "conflicted HQ push item must use supplier item candidate instead of old domestic code",
);

const currentPageDetails = [
  { HGUID: " D1 " },
  { hguid: "D2" },
  { HGUID: " " },
  {},
];
assert.deepEqual(getCurrentPageDetailGuids(currentPageDetails), ["D1", "D2"]);
assert.deepEqual(toggleCurrentPageSelection(["OLD"], currentPageDetails), ["OLD", "D1", "D2"]);
assert.deepEqual(toggleCurrentPageSelection(["OLD", "D1"], currentPageDetails), ["OLD", "D1", "D2"]);
assert.deepEqual(toggleCurrentPageSelection(["OLD", "D1", "D2"], currentPageDetails), ["OLD"]);

const normalizedJob = normalizeContainerJob({
  JobId: "job-1",
  Status: "Completed",
  Result: {
    CreatedCount: 2,
    FailedCount: 1,
    Errors: [{ message: "bad" }],
  },
});

assert.equal(normalizedJob.jobId, "job-1");
assert.equal(normalizedJob.status, "Succeeded");
assert.equal(normalizedJob.result.createdCount, 2);
assert.equal(normalizedJob.result.failedCount, 1);
assert.equal(normalizedJob.result.errors.length, 1);


// ---------------------------------------------------------------------------
// 每页条数：默认 50，可选 50/100/200/500，非法值回到默认
// ---------------------------------------------------------------------------
assert.deepEqual([...CONTAINER_DETAIL_PAGE_SIZE_OPTIONS], [50, 100, 200, 500]);
assert.equal(CONTAINER_DETAIL_DEFAULT_PAGE_SIZE, 50);
assert.equal(CONTAINER_DETAIL_PAGE_SIZE, 50, "旧导出名保留并跟随默认值");
assert.equal(detailQuery.pageSize, 50, "默认查询每页 50 条");
assert.equal(buildContainerDetailQuery("c", { pageSize: 200 }).pageSize, 200);
assert.equal(normalizeContainerDetailPageSize("100"), 100);
assert.equal(normalizeContainerDetailPageSize(500), 500);
assert.equal(normalizeContainerDetailPageSize(30), 50, "旧版本的 30 不在可选项里，回到默认");
assert.equal(normalizeContainerDetailPageSize("abc"), 50);
assert.equal(normalizeContainerDetailPageSize(null), 50);
assert.equal(getContainerDetailPageCount(0, 50), 1);
assert.equal(getContainerDetailPageCount(101, 50), 3);
assert.equal(getContainerDetailPageCount(100, 50), 2);
assert.equal(getContainerDetailPageCount(10, 0), 1);

// ---------------------------------------------------------------------------
// 搜索字段：keyword 只写入选中的字段；默认货号；显式字段优先
// ---------------------------------------------------------------------------
const nameSearch = buildContainerDetailQuery("c", { keyword: " mug ", searchField: "englishName" });
assert.equal(nameSearch.englishName, "mug");
assert.equal(nameSearch.itemNumber, undefined);
assert.equal(nameSearch.productName, undefined);
assert.equal(nameSearch.barcode, undefined);
assert.equal(buildContainerDetailQuery("c", { keyword: "中文", searchField: "productName" }).productName, "中文");
assert.equal(buildContainerDetailQuery("c", { keyword: "952", searchField: "barcode" }).barcode, "952");
assert.equal(buildContainerDetailQuery("c", { keyword: "hb" }).itemNumber, "hb", "未指定字段默认货号");
assert.equal(
  buildContainerDetailQuery("c", { keyword: "x", searchField: "bogus" as never }).itemNumber,
  "x",
  "未知字段回退货号",
);
assert.equal(
  buildContainerDetailQuery("c", { keyword: "kw", itemNumber: "explicit" }).itemNumber,
  "explicit",
  "显式 itemNumber 优先于 keyword（兼容旧调用）",
);
assert.equal(buildContainerDetailQuery("c", { keyword: "  " }).itemNumber, undefined);

// ---------------------------------------------------------------------------
// 排序：默认货号升序；sort 优先于 sortBy；点选切换方向
// ---------------------------------------------------------------------------
assert.deepEqual(DEFAULT_CONTAINER_DETAIL_SORT, { field: "itemNumber", order: "ascend" });
assert.equal(detailQuery.sortBy, "itemNumber");
const sortedQuery = buildContainerDetailQuery("c", { sort: { field: "oemPrice", order: "descend" }, sortBy: "barcode" });
assert.equal(sortedQuery.sortBy, "oemPrice");
assert.equal(sortedQuery.sortOrder, "descend");
const legacySortQuery = buildContainerDetailQuery("c", { sortBy: "barcode", sortOrder: "descend" });
assert.equal(legacySortQuery.sortBy, "barcode");
assert.equal(legacySortQuery.sortOrder, "descend");
assert.deepEqual(normalizeContainerDetailSort({ field: "nope" as never, order: "descend" }), { field: "itemNumber", order: "descend" });
assert.deepEqual(normalizeContainerDetailSort(null), DEFAULT_CONTAINER_DETAIL_SORT);
assert.equal(buildContainerDetailQuery("c", { sort: { field: "nope" as never } }).sortBy, "itemNumber");
// 后端 ApplyContainerDetailSort 白名单（ContainerReactService.cs）
const backendSortWhitelist = new Set([
  "barcode", "productName", "englishName", "productType", "newProduct", "containerPieces", "middlePackQuantity",
  "containerQuantity", "packingQuantity", "unitVolume", "domesticPrice", "floatRate", "transportCost",
  "unitTransportCost", "warehouseImportPrice", "lastOEMPrice", "importPrice", "oemPrice", "warehouseStatus",
  "remark", "itemNumber", "matchType",
]);
CONTAINER_DETAIL_SORT_OPTIONS.forEach((option) => {
  assert.ok(backendSortWhitelist.has(option.field), `${option.field} 必须在后端排序白名单内`);
  assert.ok(option.label && option.labelKey);
});
assert.equal(new Set(CONTAINER_DETAIL_SORT_OPTIONS.map((item) => item.field)).size, CONTAINER_DETAIL_SORT_OPTIONS.length);
assert.deepEqual(toggleContainerDetailSort({ field: "itemNumber", order: "ascend" }, "itemNumber"), { field: "itemNumber", order: "descend" });
assert.deepEqual(toggleContainerDetailSort({ field: "itemNumber", order: "descend" }, "itemNumber"), { field: "itemNumber", order: "ascend" });
assert.deepEqual(toggleContainerDetailSort({ field: "itemNumber", order: "ascend" }, "importPrice"), { field: "importPrice", order: "descend" });
assert.deepEqual(toggleContainerDetailSort({ field: "importPrice", order: "descend" }, "productName"), { field: "productName", order: "ascend" });

// ---------------------------------------------------------------------------
// 筛选状态：区间 + 商品类型 + 上下架 + 匹配方式
// ---------------------------------------------------------------------------
const emptyFilters = createEmptyContainerDetailFilters();
assert.equal(countActiveContainerDetailFilters(emptyFilters), 0);
assert.notEqual(emptyFilters.ranges, createEmptyContainerDetailFilters().ranges, "每次返回独立对象");
const activeFilters = createEmptyContainerDetailFilters();
activeFilters.ranges.oemPriceMin = " 5 ";
activeFilters.ranges.oemPriceMax = "9";
activeFilters.ranges.containerQuantityMax = "100";
activeFilters.productTypes = ["set", "multi"];
activeFilters.warehouseStatus = ["inactive"];
activeFilters.matchTypes = ["unmatched"];
assert.equal(countActiveContainerDetailFilters(activeFilters), 5, "2 个区间对 + 3 组多选；同一区间对只算一次");
assert.deepEqual(findInvalidContainerDetailRangePairs(activeFilters), []);
const filterQuery = buildContainerDetailFilterQuery(activeFilters);
assert.equal(filterQuery.oemPriceMin, 5);
assert.equal(filterQuery.oemPriceMax, 9);
assert.equal(filterQuery.containerQuantityMax, 100);
assert.equal(filterQuery.containerQuantityMin, undefined);
assert.deepEqual(filterQuery.productTypes, ["set", "multi"]);
assert.deepEqual(filterQuery.warehouseStatus, ["inactive"]);
assert.deepEqual(filterQuery.matchTypes, ["unmatched"]);
const filteredDetailQuery = buildContainerDetailQuery("c", { filters: activeFilters, selectedTags: ["new"] });
assert.equal(filteredDetailQuery.oemPriceMin, 5);
assert.deepEqual(filteredDetailQuery.productTypes, ["set", "multi"]);
assert.deepEqual(filteredDetailQuery.selectedTags, ["new"], "标签 chips 逻辑不变");
assert.equal(filteredDetailQuery.includeTotal, true);
assert.equal(filteredDetailQuery.includeStats, true);
assert.equal(
  buildContainerDetailQuery("c", { filters: activeFilters, oemPriceMin: 1 }).oemPriceMin,
  1,
  "显式字段优先于 filters",
);
assert.equal(buildContainerDetailQuery("c", { filters: emptyFilters }).productTypes, undefined);
const badRanges = createEmptyContainerDetailFilters();
badRanges.ranges.containerQuantityMin = "abc";
badRanges.ranges.oemPriceMin = "10";
badRanges.ranges.oemPriceMax = "2";
assert.deepEqual(findInvalidContainerDetailRangePairs(badRanges), ["containerQuantity", "oemPrice"]);
assert.equal(buildContainerDetailFilterQuery(badRanges).containerQuantityMin, undefined, "非法数字被丢弃而不是发 NaN");
assert.equal(countActiveContainerDetailFilters(resetContainerDetailFilters()), 0);
assert.deepEqual(toggleContainerDetailFilterOption(["a", "b"], "a"), ["b"]);
assert.deepEqual(toggleContainerDetailFilterOption(["a"], "b"), ["a", "b"]);
assert.equal(
  buildContainerDetailQuery("c", { includeTotal: false, includeStats: false }).includeTotal,
  false,
  "批量范围等场景仍可显式关闭总数/统计",
);

// ---------------------------------------------------------------------------
// 创建新商品：零售价必须 > 0
// ---------------------------------------------------------------------------
const missingPriceRows = findContainerDetailsMissingRetailPrice([
  { hguid: "N-OK", 是否新商品: true, 贴牌价格: 3.5, 商品信息: { 货号: "OK" } },
  { hguid: "N-ZERO", 是否新商品: true, 贴牌价格: 0, 商品信息: { 货号: "ZERO" } },
  { hguid: "N-NEG", 是否新商品: true, 贴牌价格: -1, 商品信息: { 货号: " " }, 商品编码: "P-NEG" },
  { hguid: "N-NAN", 是否新商品: true, 贴牌价格: Number.NaN },
  { hguid: "N-NONE", 是否新商品: true, 商品信息: { 货号: "NONE" } },
  { hguid: "OLD", 是否新商品: false, 商品信息: { 货号: "OLD" } },
]);
assert.deepEqual(
  missingPriceRows.map((row) => [row.hguid, row.label]),
  [["N-ZERO", "ZERO"], ["N-NEG", "P-NEG"], ["N-NAN", "N-NAN"], ["N-NONE", "NONE"]],
  "已有商品不校验；货号空白时依次回退商品编码、明细 GUID",
);
assert.deepEqual(findContainerDetailsMissingRetailPrice([]), []);

// ---------------------------------------------------------------------------
// 创建后同步 HQ 的发送计划
// ---------------------------------------------------------------------------
const createdRow = (hguid: string, productCode: string | undefined, extra: Record<string, unknown> = {}) => ({
  hguid,
  商品编码: productCode,
  是否新商品: false,
  商品信息: { 货号: `IT-${hguid}`, localSupplierCode: "200" },
  ...extra,
});
const latestRows = [createdRow("D1", "P1"), createdRow("D2", "p2")];
const confirmedRows = [
  createdRow("D1", "P1", { 是否新商品: true }),
  createdRow("D3", "P3", { 是否新商品: true }),
  createdRow("D4", undefined, { 商品信息: { 货号: " ", localSupplierCode: "" } }),
];
const hqPlan = buildCreatedProductsHqPushPlan(
  [
    { detailHguid: " d1 ", productCode: "P1" },
    { detailHguid: "D1", productCode: "P1" },
    { productCode: "P2" },
    { detailHguid: "D3" },
    { detailHguid: "D4" },
    { detailHguid: "GONE", productCode: "GONE" },
  ],
  latestRows,
  confirmedRows,
);
assert.deepEqual(hqPlan.selection.productCodes, ["P1", "p2", "P3"], "同一明细只发一次；最新行优先、找不到再回退确认时的行");
assert.equal(hqPlan.selection.items[0]?.isNewProduct, false, "优先使用重载后的最新行（已建档）");
assert.equal(hqPlan.selection.items.length, 3);
assert.equal(hqPlan.unsentCreatedCount, 2, "找不到明细 GONE + 缺编码和供应商货号的 D4");
assert.deepEqual(hqPlan.warnings, [{ code: "UNSENT_CREATED", count: 2 }]);
assert.equal(hqPlan.shouldPush, true);
const busyPlan = buildCreatedProductsHqPushPlan([{ detailHguid: "D1" }], latestRows, [], { pushInFlight: true });
assert.equal(busyPlan.shouldPush, false);
assert.deepEqual(busyPlan.warnings, [{ code: "PUSH_BUSY" }]);
const emptyPlan = buildCreatedProductsHqPushPlan([{ detailHguid: "NOPE" }], latestRows, []);
assert.equal(emptyPlan.shouldPush, false);
assert.deepEqual(emptyPlan.warnings, [{ code: "UNSENT_CREATED", count: 1 }], "一个候选都没有时按本次创建总数提示");
assert.deepEqual(buildCreatedProductsHqPushPlan([], latestRows).warnings, []);
// 编码冲突的行不带商品编码、改用供应商+货号候选
const conflictPlan = buildCreatedProductsHqPushPlan(
  [{ detailHguid: "C1" }],
  [createdRow("C1", "P-C", { hasProductCodeConflict: true })],
);
assert.equal(conflictPlan.selection.items[0]?.productCode, undefined);
assert.equal(conflictPlan.selection.items[0]?.itemNumber, "IT-C1");
assert.equal(DEFAULT_PUSH_PRODUCTS_TO_HQ_UPDATE_FIELDS.length, 17, "与 Web 默认 17 个更新字段对齐");
assert.ok(DEFAULT_PUSH_PRODUCTS_TO_HQ_UPDATE_FIELDS.includes("productType"));
assert.equal(new Set(DEFAULT_PUSH_PRODUCTS_TO_HQ_UPDATE_FIELDS).size, 17);

// ---------------------------------------------------------------------------
// 头部概览
// ---------------------------------------------------------------------------
const overview = buildContainerDetailOverview(
  {
    合计金额: 12345.6,
    总体积: 34,
    合计件数: 800,
    合计数量: 9600,
    运费: 5000,
    汇率: 4.8,
    装柜日期: "2026-09-01T00:00:00",
    预计到岸日期: "2026-10-10T00:00:00",
    状态: 1,
  },
  { all: 40, new: 3, existing: 37, noOemPrice: 0, abnormalImport: 0, active: 0, inactive: 0 },
  { today: "2026-10-08" },
);
assert.equal(overview.totalAmount, 12345.6);
assert.equal(overview.totalVolume, 34);
assert.equal(overview.loadRatePercent, 50);
assert.equal(overview.newCount, 3);
assert.equal(overview.existingCount, 37);
assert.equal(overview.rowCount, 40);
assert.equal(overview.etaDate, "2026-10-10");
assert.equal(overview.actualArrivalDate, undefined);
assert.equal(overview.statsMissing, false);
assert.deepEqual(overview.arrivalInsight, { text: "2 天后", tone: "muted", target: "estimated" });
const sparseOverview = buildContainerDetailOverview({}, undefined);
assert.equal(sparseOverview.totalAmount, undefined);
assert.equal(sparseOverview.loadRatePercent, undefined);
assert.equal(sparseOverview.newCount, undefined);
assert.equal(sparseOverview.statsMissing, true);
assert.equal(sparseOverview.arrivalInsight, null);
assert.equal(buildContainerDetailOverview(null, null).rowCount, undefined);
assert.equal(getContainerLoadRatePercent(68), 100);
assert.equal(getContainerLoadRatePercent(102), 150, "超过 100% 照实返回");
assert.equal(getContainerLoadRatePercent(-1), undefined);
assert.equal(getContainerLoadRatePercent(Number.NaN), undefined);
const wholeStats = { all: 10, new: 1, existing: 9, noOemPrice: 0, abnormalImport: 0, active: 0, inactive: 0 };
const filteredStats = { ...wholeStats, all: 2 };
assert.deepEqual(
  resolveContainerDetailOverviewStats({ remoteStats: wholeStats, hasScopeFilters: false, cachedStats: null }),
  { stats: wholeStats, cacheable: true },
);
assert.deepEqual(
  resolveContainerDetailOverviewStats({ remoteStats: filteredStats, hasScopeFilters: true, cachedStats: wholeStats }),
  { stats: wholeStats, cacheable: false },
  "带搜索/筛选时沿用整柜缓存，概览卡不跟着变",
);
assert.deepEqual(
  resolveContainerDetailOverviewStats({ remoteStats: filteredStats, hasScopeFilters: true, cachedStats: null }),
  { stats: null, cacheable: false },
);
assert.equal(
  resolveContainerDetailOverviewStats({ remoteStats: wholeStats, statsComputed: false, hasScopeFilters: false, cachedStats: null }).cacheable,
  false,
);

console.log("containers query tests passed");
