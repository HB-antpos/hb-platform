import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import ts from "typescript";
import {
  buildWarehousePriceSyncRequest,
  calculateDiscountedRetailPrice,
  createWarehousePriceSyncState,
  extractWarehousePriceSyncConflict,
  formatWarehouseDiscountRate,
  isProductQueryInteractionBlocked,
  isWarehousePriceConflictSnapshotComplete,
  isWarehousePriceInteractionLocked,
  isWarehousePriceSyncSupplier,
  normalizeWarehouseDiscountRate,
  normalizeWarehouseMoney,
  normalizeWarehousePriceSyncResponse,
  reduceWarehousePriceSyncState,
  resolveWarehousePriceConfirmationFeedback,
  shouldAutoPrintWarehousePrice,
} from "./warehouse-price-sync";

// 执行页面真实详情后处理及扫码打印回调，只替换网络与打印机边界。
const pageSource = readFileSync(resolve(__dirname, "../../../app/(shell)/product-query.tsx"), "utf8");
const pageAst = ts.createSourceFile("product-query.tsx", pageSource, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const productQuery = pageAst.statements.find((node): node is ts.FunctionDeclaration =>
  ts.isFunctionDeclaration(node) && node.name?.text === "ProductQueryContent");
function callbackStatement(name: string): ts.VariableStatement {
  const statement = productQuery?.body?.statements.find((node) =>
    ts.isVariableStatement(node) && node.declarationList.declarations.some((declaration) =>
      ts.isIdentifier(declaration.name) && declaration.name.text === name));
  assert.ok(statement && ts.isVariableStatement(statement), `页面必须保留可执行的 ${name} 回调`);
  return statement;
}
const processLoadedDetailStatement = callbackStatement("processLoadedDetail");
const smartAutoPrintStatement = callbackStatement("smartAutoPrint");

function compileCallback<T>(statement: ts.VariableStatement, name: string, deps: Record<string, unknown>): T {
  const source = `const { ${Object.keys(deps).join(", ")} } = deps;\n${statement.getText(pageAst)}\nreturn ${name};`;
  return new Function("deps", ts.transpile(source, { target: ts.ScriptTarget.ES2022 }))(deps) as T;
}

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

const camelSnapshot = normalizeWarehousePriceSyncResponse({
  status: "confirmation_required",
  purchaseUpdated: true,
  retailUpdated: false,
  retailConfirmationRequired: true,
  storePrice: {
    uuid: "price-1",
    storeCode: "S01",
    purchasePrice: "3.25",
    retailPrice: 5,
    discountRate: 0.2,
    isAutoPricing: true,
    isSpecialProduct: false,
    isActive: true,
  },
  warehousePurchasePrice: "3.25",
  warehouseRetailPrice: 6,
  previousStorePurchasePrice: 3,
  previousStoreRetailPrice: 5,
  discountRate: 0.2,
  previousDiscountedRetailPrice: 4,
  newDiscountedRetailPrice: 4.8,
});

assertEqual(camelSnapshot.status, "confirmation_required", "camel response normalizes status");
assertEqual(camelSnapshot.storePrice?.purchasePrice, 3.25, "camel response normalizes latest purchase");
assertEqual(camelSnapshot.storePrice?.isAutoPricing, true, "camel response preserves auto pricing flag");
const pascalSnapshot = normalizeWarehousePriceSyncResponse({
  Success: true,
  Data: {
    Status: "synced",
    PurchaseUpdated: false,
    RetailUpdated: true,
    RetailConfirmationRequired: false,
    StorePrice: {
      Uuid: "price-2",
      StoreCode: "S02",
      PurchasePrice: 4,
      RetailPrice: 8,
      DiscountRate: 15,
      IsAutoPricing: false,
      IsSpecialProduct: true,
      IsActive: true,
    },
    WarehousePurchasePrice: 4,
    WarehouseRetailPrice: 8,
    PreviousStorePurchasePrice: 4,
    PreviousStoreRetailPrice: 7,
    DiscountRate: 15,
    PreviousDiscountedRetailPrice: 5.95,
    NewDiscountedRetailPrice: 6.8,
    HqSync: {
      OperationId: "operation-warehouse-1",
      Status: "pending",
      ProductCode: "P-WAREHOUSE",
      StoreCode: "S02",
      AttemptCount: 0,
      Retryable: true,
      Message: "pending",
    },
  },
});

assertEqual(pascalSnapshot.storePrice?.discountRate, 0.15, "Pascal response normalizes percent discount");
assertEqual(pascalSnapshot.newDiscountedRetailPrice, 6.8, "Pascal response normalizes discounted price");
assertEqual(
  pascalSnapshot.hqSync?.operationId,
  "operation-warehouse-1",
  "warehouse mutation exposes its HQ sync operation"
);

const nullSnapshot = normalizeWarehousePriceSyncResponse(null);
assertDeepEqual(
  {
    status: nullSnapshot.status,
    purchaseUpdated: nullSnapshot.purchaseUpdated,
    retailUpdated: nullSnapshot.retailUpdated,
    retailConfirmationRequired: nullSnapshot.retailConfirmationRequired,
    storePrice: nullSnapshot.storePrice,
    warehousePurchasePrice: nullSnapshot.warehousePurchasePrice,
  },
  {
    status: "missing_source",
    purchaseUpdated: false,
    retailUpdated: false,
    retailConfirmationRequired: false,
    storePrice: null,
    warehousePurchasePrice: null,
  },
  "null response normalizes to a safe non-writing snapshot"
);

assertEqual(isWarehousePriceSyncSupplier(" 200 "), true, "supplier 200 is trim-aware");
assertEqual(isWarehousePriceSyncSupplier("0200"), false, "other supplier is not applicable");
assertEqual(isWarehousePriceSyncSupplier(null), false, "missing supplier is not applicable");

assertEqual(normalizeWarehouseMoney(1.005), 1.01, "money rounds to two decimals");
assertEqual(normalizeWarehouseMoney("bad"), null, "invalid money becomes null");
assertEqual(normalizeWarehouseDiscountRate(20), 0.2, "percentage discount normalizes to ratio");
assertEqual(normalizeWarehouseDiscountRate(0.2), 0.2, "ratio discount stays unchanged");
assertEqual(calculateDiscountedRetailPrice(6, 0.2), 4.8, "discounted price preserves discount ratio");
assertEqual(calculateDiscountedRetailPrice(7.99, 15), 6.79, "discounted price rounds to cents");

const nullDiscountSnapshot = normalizeWarehousePriceSyncResponse({
  status: "confirmation_required",
  retailConfirmationRequired: true,
  storePrice: {
    uuid: "price-null-discount",
    purchasePrice: 4,
    retailPrice: 8,
    discountRate: null,
    isAutoPricing: false,
    isSpecialProduct: false,
    isActive: true,
  },
  warehousePurchasePrice: 4,
  warehouseRetailPrice: 10,
  previousStorePurchasePrice: 4,
  previousStoreRetailPrice: 8,
  discountRate: null,
  previousDiscountedRetailPrice: 8,
  newDiscountedRetailPrice: 10,
});
assertEqual(nullDiscountSnapshot.discountRate, null, "null discount remains null for concurrency snapshot");
assertEqual(calculateDiscountedRetailPrice(8, null), 8, "null discount calculates as zero discount");
assertEqual(formatWarehouseDiscountRate(null), "0%", "null discount displays as zero percent");
assertEqual(
  buildWarehousePriceSyncRequest(
    { ...nullDiscountSnapshot, discountRate: 0.2 },
    true
  ).expectedDiscountRate,
  null,
  "confirm request preserves latest store-price null discount for concurrency validation"
);
assertEqual(
  isWarehousePriceConflictSnapshotComplete(nullDiscountSnapshot),
  true,
  "conflict snapshot completeness allows a legitimate null discount"
);
assertEqual(
  isWarehousePriceConflictSnapshotComplete({
    ...nullDiscountSnapshot,
    previousStoreRetailPrice: null,
    storePrice: { ...nullDiscountSnapshot.storePrice!, retailPrice: null },
  }),
  true,
  "conflict snapshot completeness allows a legitimate null current retail price"
);

assertEqual(
  isProductQueryInteractionBlocked({
    loading: false,
    lookupVisible: false,
    lookupSelectionOpen: false,
    autoPricingVisible: false,
    autoPricingSaving: false,
    warehouseLocked: false,
    requestInFlight: false,
    storeSelectionInFlight: false,
  }),
  false,
  "idle product query accepts a new scan"
);
assertEqual(
  isProductQueryInteractionBlocked({
    loading: false,
    lookupVisible: false,
    lookupSelectionOpen: false,
    autoPricingVisible: false,
    autoPricingSaving: false,
    warehouseLocked: false,
    requestInFlight: true,
    storeSelectionInFlight: false,
  }),
  true,
  "in-flight lookup serializes camera, HID, and store selection"
);
assertEqual(
  isProductQueryInteractionBlocked({
    loading: false,
    lookupVisible: false,
    lookupSelectionOpen: true,
    autoPricingVisible: false,
    autoPricingSaving: false,
    warehouseLocked: false,
    requestInFlight: false,
    storeSelectionInFlight: false,
  }),
  true,
  "synchronous selection ref closes the state-update window before lookup sheet renders"
);

const confirmRequest = buildWarehousePriceSyncRequest(camelSnapshot, true);
assertDeepEqual(
  confirmRequest,
  {
    confirmRetailPrice: true,
    expectedWarehousePurchasePrice: 3.25,
    expectedWarehouseRetailPrice: 6,
    expectedStorePurchasePrice: 3.25,
    expectedStoreRetailPrice: 5,
    expectedDiscountRate: 0.2,
  },
  "confirm request uses the latest post-purchase-sync store snapshot"
);

let flowState = createWarehousePriceSyncState();
flowState = reduceWarehousePriceSyncState(flowState, { type: "preview_started" });
assertEqual(flowState.phase, "previewing", "preview transition enters busy state");
assertEqual(isWarehousePriceInteractionLocked(flowState), true, "preview disables scanner and editing");

flowState = reduceWarehousePriceSyncState(flowState, {
  type: "preview_succeeded",
  snapshot: camelSnapshot,
});
assertEqual(flowState.phase, "idle", "retail difference does not open a confirmation state");
assertEqual(isWarehousePriceInteractionLocked(flowState), false, "completed preview releases scanner state");

// 旧确认事件暂保留 API 契约；页面不再进入此流程。
flowState = { phase: "confirmation", snapshot: camelSnapshot, errorMessage: null };
flowState = reduceWarehousePriceSyncState(flowState, { type: "confirm_started" });
assertEqual(flowState.phase, "confirming", "confirm transition enters submit state");

flowState = reduceWarehousePriceSyncState(flowState, {
  type: "confirm_failed",
  message: "network failed",
});
assertEqual(flowState.phase, "confirmation", "confirm failure keeps modal open");
assertEqual(flowState.errorMessage, "network failed", "confirm failure remains retryable with error");

const conflictSnapshot = extractWarehousePriceSyncConflict({
  response: {
    status: 409,
    data: {
      success: false,
      errorCode: "PRICE_VERSION_CONFLICT",
      data: {
        status: "confirmation_required",
        retailConfirmationRequired: true,
        storePrice: {
          uuid: "price-1",
          purchasePrice: 3.5,
          retailPrice: 5.5,
          discountRate: 0.2,
          isAutoPricing: true,
          isSpecialProduct: false,
          isActive: true,
        },
        warehousePurchasePrice: 3.5,
        warehouseRetailPrice: 6.5,
        previousStorePurchasePrice: 3.5,
        previousStoreRetailPrice: 5.5,
        discountRate: 0.2,
        previousDiscountedRetailPrice: 4.4,
        newDiscountedRetailPrice: 5.2,
      },
    },
  },
});

assertEqual(conflictSnapshot?.storePrice?.purchasePrice, 3.5, "409 exposes latest store snapshot");
assertEqual(conflictSnapshot?.warehouseRetailPrice, 6.5, "409 exposes latest warehouse snapshot");
assertEqual(
  extractWarehousePriceSyncConflict({ response: { status: 500, data: {} } }),
  null,
  "non-conflict errors do not masquerade as a version conflict"
);

flowState = reduceWarehousePriceSyncState(flowState, {
  type: "conflict_received",
  snapshot: conflictSnapshot!,
  message: "price changed",
});
assertEqual(flowState.phase, "confirmation", "conflict refresh keeps confirmation open");
assertEqual(flowState.snapshot?.warehouseRetailPrice, 6.5, "conflict transition replaces stale snapshot");

flowState = reduceWarehousePriceSyncState(flowState, { type: "print_started" });
assertEqual(flowState.phase, "printing", "explicit keep-current-price print locks the flow");
assertEqual(isWarehousePriceInteractionLocked(flowState), true, "printing keeps scanner and editing locked");
flowState = reduceWarehousePriceSyncState(flowState, { type: "cancelled" });
assertEqual(flowState.phase, "idle", "print completion cleanup returns the flow to idle");
assertEqual(flowState.snapshot, null, "cancel cleanup clears the transient snapshot");

assertEqual(
  shouldAutoPrintWarehousePrice({
    lookupOrigin: "scan",
    stage: "preview_succeeded",
    snapshot: { ...camelSnapshot, status: "synced", retailConfirmationRequired: false },
    alreadyPrinted: false,
  }),
  true,
  "same-price warehouse sync still auto prints"
);
assertEqual(
  shouldAutoPrintWarehousePrice({
    lookupOrigin: "scan",
    stage: "preview_succeeded",
    snapshot: camelSnapshot,
    alreadyPrinted: false,
  }),
  true,
  "retail difference auto prints the verified current store price"
);
assertEqual(
  shouldAutoPrintWarehousePrice({
    lookupOrigin: "scan",
    stage: "confirmation_succeeded",
    snapshot: { ...pascalSnapshot, retailUpdated: true },
    alreadyPrinted: false,
  }),
  true,
  "scan prints after successful retail confirmation"
);
assertEqual(
  shouldAutoPrintWarehousePrice({
    lookupOrigin: "scan",
    stage: "confirmation_succeeded",
    snapshot: { ...pascalSnapshot, retailUpdated: false },
    alreadyPrinted: false,
  }),
  false,
  "confirmation response without a retail update does not print"
);
const keepCurrentPriceSnapshot = {
  ...camelSnapshot,
  status: "confirmation_required" as const,
  retailConfirmationRequired: true,
  retailUpdated: false,
  storePrice: {
    ...camelSnapshot.storePrice!,
    uuid: "price-current",
    retailPrice: 5,
  },
};
assertEqual(
  shouldAutoPrintWarehousePrice({
    lookupOrigin: "scan",
    stage: "retail_update_skipped",
    snapshot: keepCurrentPriceSnapshot,
    alreadyPrinted: false,
  }),
  true,
  "explicitly keeping the current store price allows the scanned label to print"
);
for (const [label, snapshot] of [
  ["skip print requires confirmation status", { ...keepCurrentPriceSnapshot, status: "synced" as const }],
  ["skip print requires a confirmation marker", { ...keepCurrentPriceSnapshot, retailConfirmationRequired: false }],
  ["skip print requires a store-price UUID", { ...keepCurrentPriceSnapshot, storePrice: { ...keepCurrentPriceSnapshot.storePrice!, uuid: "" } }],
  ["skip print rejects a missing current retail price", { ...keepCurrentPriceSnapshot, storePrice: { ...keepCurrentPriceSnapshot.storePrice!, retailPrice: null } }],
  ["skip print rejects a negative current retail price", { ...keepCurrentPriceSnapshot, storePrice: { ...keepCurrentPriceSnapshot.storePrice!, retailPrice: -1 } }],
  ["skip print rejects a non-finite current retail price", { ...keepCurrentPriceSnapshot, storePrice: { ...keepCurrentPriceSnapshot.storePrice!, retailPrice: Number.NaN } }],
] as const) {
  assertEqual(
    shouldAutoPrintWarehousePrice({
      lookupOrigin: "scan",
      stage: "retail_update_skipped",
      snapshot,
      alreadyPrinted: false,
    }),
    false,
    label
  );
}
for (const status of ["missing_source", "not_applicable"] as const) {
  assertEqual(
    shouldAutoPrintWarehousePrice({
      lookupOrigin: "scan",
      stage: "preview_succeeded",
      snapshot: { ...pascalSnapshot, status, retailUpdated: false },
      alreadyPrinted: false,
    }),
    false,
    `${status} does not print an unverified warehouse label`
  );
}
for (const [label, storePrice] of [
  ["missing UUID", { ...camelSnapshot.storePrice!, uuid: "" }],
  ["missing price", { ...camelSnapshot.storePrice!, retailPrice: null }],
  ["negative price", { ...camelSnapshot.storePrice!, retailPrice: -1 }],
  ["non-finite price", { ...camelSnapshot.storePrice!, retailPrice: Number.NaN }],
] as const) {
  assertEqual(
    shouldAutoPrintWarehousePrice({
      lookupOrigin: "scan",
      stage: "preview_succeeded",
      snapshot: { ...camelSnapshot, storePrice },
      alreadyPrinted: false,
    }),
    false,
    `retail difference preview rejects ${label}`
  );
}
for (const [label, input] of [
  [
    "manual lookup never auto prints",
    { lookupOrigin: "manual", stage: "preview_succeeded", snapshot: pascalSnapshot, alreadyPrinted: false },
  ],
  [
    "cancel never prints stale label",
    { lookupOrigin: "scan", stage: "cancelled", snapshot: camelSnapshot, alreadyPrinted: false },
  ],
  [
    "failure never prints stale label",
    { lookupOrigin: "scan", stage: "failed", snapshot: camelSnapshot, alreadyPrinted: false },
  ],
  [
    "already printed scan cannot print twice",
    { lookupOrigin: "scan", stage: "confirmation_succeeded", snapshot: pascalSnapshot, alreadyPrinted: true },
  ],
  [
    "dismissal/cancel does not print the current label",
    { lookupOrigin: "scan", stage: "cancelled", snapshot: keepCurrentPriceSnapshot, alreadyPrinted: false },
  ],
  [
    "manual keep-current-price action never auto prints",
    { lookupOrigin: "manual", stage: "retail_update_skipped", snapshot: keepCurrentPriceSnapshot, alreadyPrinted: false },
  ],
  [
    "repeat keep-current-price action cannot print twice",
    { lookupOrigin: "scan", stage: "retail_update_skipped", snapshot: keepCurrentPriceSnapshot, alreadyPrinted: true },
  ],
] as const) {
  assertEqual(shouldAutoPrintWarehousePrice(input), false, label);
}

assertEqual(
  resolveWarehousePriceConfirmationFeedback({
    retailUpdated: true,
    printAttempted: true,
    labelPrinted: false,
  }),
  "retail_updated_print_failed",
  "price success plus print failure uses combined feedback"
);
assertEqual(
  resolveWarehousePriceConfirmationFeedback({
    retailUpdated: true,
    printAttempted: false,
    labelPrinted: false,
  }),
  "retail_updated",
  "price success without auto print reports only the price update"
);
assertEqual(
  resolveWarehousePriceConfirmationFeedback({
    retailUpdated: false,
    printAttempted: false,
    labelPrinted: false,
  }),
  "no_update",
  "unchanged confirmation never claims that retail was updated"
);

// 执行查询页真实详情回调，确认供应商 200 直接使用本店在线价并保持条码匹配。
const currentDetail = {
  productCode: "PRODUCT-1",
  barcode: "MAIN-1",
  itemNumber: "ITEM-1",
  localSupplierCode: "200",
  storePrice: { uuid: "price-1", retailPrice: 5, storeProductCode: "STORE-1", discountRate: 0.2 },
  setCodes: [{ setCodeId: "set-id", setBarcode: "SET-1", setRetailPrice: 10 }],
  multiCodes: [{ multiCodeId: "multi-id", barcode: "MULTI-1", retailPrice: 4 }],
};
type Detail = typeof currentDetail;
type FlowResult = { labelPrinted: boolean; autoPricingStatus: string };
type PrintOptions = { barcode?: string; retailPrice?: number; action?: string; printType?: string | null };

function createPageFlow(options: {
  scanKeyword?: string;
  detail?: Detail;
  autoPrintEnabled?: boolean;
  offline?: boolean;
  scopeCurrent?: boolean;
  smallLabel?: boolean;
  printTarget?: Record<string, unknown> | null;
  print?: () => Promise<boolean>;
} = {}) {
  const detail = options.detail ?? structuredClone(currentDetail);
  const prints: { detail: Detail; options?: PrintOptions }[] = [];
  const messages: string[] = [];
  const feedback: string[] = [];
  let autoPricingCalls = 0;
  const smartAutoPrint = compileCallback<(keyword: string, value: Detail, target?: Record<string, unknown> | null) => Promise<boolean>>(
    smartAutoPrintStatement, "smartAutoPrint", {
      useCallback: (callback: unknown) => callback,
      sendProductLabel: async (value: Detail, printOptions?: PrintOptions) => {
        prints.push({ detail: value, options: printOptions });
        return options.print ? options.print() : true;
      },
      getMultiCodeItemId: (code: Detail["multiCodes"][number]) => code.multiCodeId,
      smallLabel: options.smallLabel === true,
      printQuantity: 1,
      quantitySingleUse: false,
      setSnackbarMessage: (message: string) => messages.push(message),
      t: (key: string) => key,
      getErrorMessage: (_error: unknown, fallback: string) => fallback,
    },
  );
  const processLoadedDetail = compileCallback<(value: Detail, input: Record<string, unknown>) => Promise<FlowResult>>(
    processLoadedDetailStatement, "processLoadedDetail", {
      useCallback: (callback: unknown) => callback,
      ensureCurrentDetailStoreScope: () => options.scopeCurrent !== false,
      offlineModeRef: { current: options.offline === true },
      playQueryFeedback: (value: string) => feedback.push(value),
      smartAutoPrint,
      maybeHandleAutoPricing: async () => {
        autoPricingCalls += 1;
        return { keepCameraOpen: false, labelPrinted: false, autoPricingStatus: "no_action" };
      },
      DEFAULT_LOOKUP_FLOW_RESULT: { keepCameraOpen: false, labelPrinted: false, autoPricingStatus: "no_action" },
      setSnackbarMessage: (message: string) => messages.push(message),
      t: (key: string) => key,
      getErrorMessage: (_error: unknown, fallback: string) => fallback,
    },
  );
  return {
    run: () => processLoadedDetail(detail, {
      scanSource: "camera",
      scanKeyword: options.scanKeyword ?? "MAIN-1",
      autoPrintEnabled: options.autoPrintEnabled !== false,
      printTarget: options.printTarget ?? null,
    }),
    prints, messages, feedback,
    get autoPricingCalls() { return autoPricingCalls; },
  };
}

async function runPageRegression() {
  for (const [scanKeyword, expectedOptions] of [
    ["MAIN-1", { printType: null }],
    ["PRODUCT-1", { printType: null }],
    ["SET-1", { barcode: "SET-1", retailPrice: 10, action: "set:set-id", printType: null }],
    ["MULTI-1", { barcode: "MULTI-1", retailPrice: 4, action: "multi:multi-id", printType: null }],
  ] as const) {
    const flow = createPageFlow({ scanKeyword });
    assert.equal((await flow.run()).labelPrinted, true, `${scanKeyword} 扫码打印本店价`);
    assert.equal(flow.prints.length, 1);
    assert.deepEqual(flow.prints[0].options, expectedOptions);
    assert.equal(flow.prints[0].detail.storePrice.retailPrice, 5, "标签使用当前门店售价");
    assert.equal(flow.autoPricingCalls, 0, "供应商 200 不评估自动价");
    assert.deepEqual(flow.feedback, ["found"]);
  }

  // 「自动打印」与「小标签」同时开启：主条码（含在线快路径的 product 目标）、套码、多码都必须打小标签。
  const productTarget = { kind: "product", barcode: "MAIN-1", retailPrice: 5, discountRate: 0.2, codeId: null };
  const setTarget = { kind: "set", barcode: "SET-1", retailPrice: 10, discountRate: null, codeId: "set-id" };
  const multiTarget = { kind: "multi", barcode: "MULTI-1", retailPrice: 4, discountRate: null, codeId: "multi-id" };
  for (const [scanKeyword, printTarget] of [
    ["MAIN-1", null],
    ["MAIN-1", productTarget],
    ["SET-1", null],
    ["SET-1", setTarget],
    ["MULTI-1", null],
    ["MULTI-1", multiTarget],
  ] as const) {
    for (const smallLabel of [true, false]) {
      const flow = createPageFlow({ scanKeyword, printTarget, smallLabel });
      assert.equal((await flow.run()).labelPrinted, true, `${scanKeyword} 扫码自动打印`);
      assert.equal(flow.prints.length, 1);
      assert.equal(
        flow.prints[0].options?.printType,
        smallLabel ? "small" : null,
        `${scanKeyword}${printTarget ? `（${printTarget.kind} 目标）` : ""} 小标签开关=${smallLabel}`,
      );
    }
  }

  const missingCode = createPageFlow({ scanKeyword: "MULTI-ON-PAGE-2" });
  assert.equal((await missingCode.run()).labelPrinted, false);
  assert.equal(missingCode.prints.length, 0, "未加载的多码不能回退打印主价");
  assert.deepEqual(missingCode.messages, ["messages.codesLoadFailed"]);

  for (const storePrice of [
    { ...currentDetail.storePrice, uuid: "" },
    { ...currentDetail.storePrice, retailPrice: null },
    { ...currentDetail.storePrice, retailPrice: -1 },
    { ...currentDetail.storePrice, retailPrice: Number.NaN },
  ]) {
    const flow = createPageFlow({ detail: { ...currentDetail, storePrice } as Detail });
    assert.equal((await flow.run()).labelPrinted, false, "无效门店价不打印");
    assert.equal(flow.prints.length, 0);
    assert.deepEqual(flow.messages, ["warehousePriceSync.currentPriceUnavailable"]);
  }

  const manual = createPageFlow({ autoPrintEnabled: false });
  assert.equal((await manual.run()).labelPrinted, false, "手动查询不会自动打印");
  assert.equal(manual.prints.length, 0);
  const otherSupplier = createPageFlow({ detail: { ...currentDetail, localSupplierCode: "100" } });
  assert.equal((await otherSupplier.run()).labelPrinted, true, "其他供应商维持自动价流程");
  assert.equal(otherSupplier.autoPricingCalls, 1);
  const offline = createPageFlow({ offline: true });
  assert.equal((await offline.run()).labelPrinted, true, "离线路径保持现有打印行为");
  const staleStore = createPageFlow({ scopeCurrent: false });
  assert.equal((await staleStore.run()).labelPrinted, false, "切店后的旧详情不打印");
  for (const print of [async () => false, async () => { throw new Error("printer disconnected"); }]) {
    const failedPrint = createPageFlow({ print });
    assert.equal((await failedPrint.run()).labelPrinted, false, "打印失败不能报告成功");
    assert.equal(failedPrint.prints.length, 1);
  }
  console.log("商品查询当前门店价扫码打印回归通过");
}

void runPageRegression().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
