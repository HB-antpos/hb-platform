import assert from "node:assert/strict";
import {
  buildAlignDomesticProductCodePayload,
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
  PageSize: 30,
  HasMore: true,
  TagStats: { all: 40, new: 3 },
}, detailQuery);

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

console.log("containers query tests passed");
