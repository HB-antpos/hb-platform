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
import { resolveScannedCodeLabel, shouldAutoPrintDiscountLabel } from "./scan-label";
import { resolveScanHighlight } from "../scanner/scan-highlight";

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
const sendCodeLabelStatement = callbackStatement("sendCodeLabel");
const sendProductLabelStatement = callbackStatement("sendProductLabel");
const playFoundFeedbackStatement = callbackStatement("playFoundFeedback");
const handlePrintStatement = callbackStatement("handlePrint");
const handlePrintMultiCodeProductStatement = callbackStatement("handlePrintMultiCodeProduct");
const handlePrintSetCodeProductStatement = callbackStatement("handlePrintSetCodeProduct");

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
  // 多码行自身价 4 / 折扣 0.5 与主档不同步：标签必须取主档价 5 与主档折扣 0.2。
  multiCodes: [{ uuid: "multi-uuid", setCodeId: "multi-id", barcode: "MULTI-1", retailPrice: 4, discountRate: 0.5 }],
};
type Detail = typeof currentDetail;
type FlowResult = { labelPrinted: boolean; autoPricingStatus: string };
type PrintOptions = {
  barcode?: string;
  retailPrice?: number;
  discountRate?: number;
  action?: string;
  printType?: string | null;
  withDiscountLabel?: boolean;
};

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
  const sendProductLabel = async (value: Detail, printOptions?: PrintOptions) => {
    prints.push({ detail: value, options: printOptions });
    return options.print ? options.print() : true;
  };
  const sendCodeLabel = compileCallback<(value: Detail, target: Record<string, unknown>, printType: string | null, withDiscountLabel?: boolean) => Promise<boolean>>(
    sendCodeLabelStatement, "sendCodeLabel", {
      useCallback: (callback: unknown) => callback,
      sendProductLabel,
      playQueryFeedback: (value: string) => feedback.push(value),
      setSnackbarMessage: (message: string) => messages.push(message),
      t: (key: string) => key,
    },
  );
  const smartAutoPrint = compileCallback<(keyword: string, value: Detail, target?: Record<string, unknown> | null) => Promise<boolean>>(
    smartAutoPrintStatement, "smartAutoPrint", {
      useCallback: (callback: unknown) => callback,
      sendProductLabel,
      sendCodeLabel,
      resolveScannedCodeLabel,
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
      // 「找到」反馈已拆到 playFoundFeedback（折扣 / 促销 / 多码换铃音），这里只关心它被调用。
      playFoundFeedback: () => { feedback.push("found"); },
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

// 执行页面真实的 sendProductLabel：普通标签之后，带 withDiscountLabel 的链路（自动打印、手动「普通」标签）有折扣才补打折扣标签。
function createSendProductLabel(options: { quantity?: number; failProduct?: boolean; failDiscount?: boolean } = {}) {
  const calls: { kind: "product" | "discount"; printType?: string | null; overrides?: Record<string, unknown> }[] = [];
  const messages: string[] = [];
  const feedback: string[] = [];
  const quantityResets: number[] = [];
  const sendProductLabel = compileCallback<(value: Detail, printOptions?: PrintOptions) => Promise<boolean>>(
    sendProductLabelStatement, "sendProductLabel", {
      useCallback: (callback: unknown) => callback,
      getSavedPrinter: async () => ({ address: "AA:BB" }),
      setSnackbarMessage: (message: string) => messages.push(message),
      t: (key: string) => key,
      playQueryFeedback: (value: string) => feedback.push(value),
      printerAutoReconnectPaused: false,
      setPrintingAction: () => undefined,
      printProductLabel: async (_value: Detail, overrides: Record<string, unknown>, printType?: string | null) => {
        calls.push({ kind: "product", printType, overrides });
        if (options.failProduct) throw new Error("product failed");
        return true;
      },
      printDiscountLabel: async (_value: Detail, printType?: string | null, overrides?: Record<string, unknown>) => {
        calls.push({ kind: "discount", printType, overrides });
        if (options.failDiscount) throw new Error("discount failed");
        return true;
      },
      shouldAutoPrintDiscountLabel,
      printQuantity: options.quantity ?? 1,
      quantitySingleUse: true,
      setPrintQuantity: (value: number) => quantityResets.push(value),
      getErrorMessage: (_error: unknown, fallback: string) => fallback,
    },
  );
  return { sendProductLabel, calls, messages, feedback, quantityResets };
}

async function runAutoDiscountLabelRegression() {
  const withDiscount = structuredClone(currentDetail); // 主档折扣 0.2
  const noDiscount = { ...structuredClone(currentDetail), storePrice: { ...currentDetail.storePrice, discountRate: 0 } };

  // 自动打印 + 有折扣：先普通标签、后折扣标签；折扣标签带同样的小标签类型与覆盖项（多码 / 套码不会打成主条码）。
  const auto = createSendProductLabel();
  assert.equal(await auto.sendProductLabel(withDiscount, { printType: "small", withDiscountLabel: true }), true);
  assert.deepEqual(auto.calls.map((call) => call.kind), ["product", "discount"], "普通标签在前、折扣标签在后");
  assert.equal(auto.calls[1].printType, "small");
  assert.deepEqual(auto.messages, ["messages.printSuccess"]);

  const multi = createSendProductLabel();
  await multi.sendProductLabel(withDiscount, { barcode: "MULTI-1", retailPrice: 5, discountRate: 0.2, action: "multi:multi-id", withDiscountLabel: true });
  assert.deepEqual(multi.calls.map((call) => call.kind), ["product", "discount"]);
  assert.deepEqual(multi.calls[1].overrides, { barcode: "MULTI-1", retailPrice: 5, discountRate: 0.2 }, "折扣标签沿用扫到的多码条码与价格");

  // 份数：折扣标签与普通标签份数一致（与手动「折扣标签」按钮同口径）。
  const twice = createSendProductLabel({ quantity: 2 });
  await twice.sendProductLabel(withDiscount, { withDiscountLabel: true });
  assert.deepEqual(twice.calls.map((call) => call.kind), ["product", "product", "discount", "discount"]);
  assert.deepEqual(twice.quantityResets, [1], "单次份数用完后复位");

  // 不补打：没有折扣、显式折扣 0（套装子项）、调用方没带 withDiscountLabel（如「小标签」按钮）。
  for (const [label, detail, printOptions] of [
    ["门店价无折扣", noDiscount, { withDiscountLabel: true }],
    ["套装子项显式折扣 0", withDiscount, { barcode: "SET-1", retailPrice: 10, discountRate: 0, withDiscountLabel: true }],
    ["调用方不带 withDiscountLabel", withDiscount, {}],
  ] as const) {
    const flow = createSendProductLabel();
    assert.equal(await flow.sendProductLabel(detail, printOptions), true, label);
    assert.deepEqual(flow.calls.map((call) => call.kind), ["product"], label);
  }

  // 普通标签失败：不再尝试折扣标签。
  const productFails = createSendProductLabel({ failProduct: true });
  assert.equal(await productFails.sendProductLabel(withDiscount, { withDiscountLabel: true }), false);
  assert.deepEqual(productFails.calls.map((call) => call.kind), ["product"], "普通标签失败不补打折扣标签");

  // 折扣标签失败：普通标签已打出，仍返回 true（调用方不会重打），并单独提示折扣标签失败。
  const discountFails = createSendProductLabel({ failDiscount: true, quantity: 2 });
  assert.equal(await discountFails.sendProductLabel(withDiscount, { withDiscountLabel: true }), true, "普通标签已打出，不能报告整体失败");
  assert.deepEqual(discountFails.messages, ["messages.discountAutoPrintFailed"]);
  assert.deepEqual(discountFails.feedback, ["error"]);
  assert.deepEqual(discountFails.quantityResets, [1], "折扣标签失败也要复位单次份数");
  console.log("商品查询自动打印补打折扣标签回归通过");
}

// 执行页面真实的 playFoundFeedback：折扣 / 促销 / 多码换铃音，其余仍是普通「找到」；促销请求慢或属于别的商品时不能误报。
async function runFoundFeedbackRegression() {
  type Promotions = unknown[] | null;
  const plain = { ...structuredClone(currentDetail), productCode: "P-1", multiCodeCount: 0, storePrice: { ...currentDetail.storePrice, discountRate: 0 } };
  const run = async (
    targetDetail: Record<string, unknown>,
    options: { pending?: { productCode: string; result: Promise<Promotions> } | null; offline?: boolean } = {},
  ) => {
    const played: string[] = [];
    const playFoundFeedback = compileCallback<(value: unknown) => Promise<void>>(
      playFoundFeedbackStatement, "playFoundFeedback", {
        useCallback: (callback: unknown) => callback,
        activePromotionLoadRef: { current: options.pending ?? null },
        offlineModeRef: { current: options.offline === true },
        PROMOTION_WAIT_MS: 30,
        countActiveProductPromotions: (items: unknown[]) => items.length,
        resolveScanHighlight,
        playScanHighlight: (kind: string) => played.push(kind),
        playQueryFeedback: (value: string) => played.push(`query:${value}`),
      },
    );
    await playFoundFeedback(targetDetail);
    return played;
  };
  const resolved = (productCode: string, items: Promotions) => ({ productCode, result: Promise.resolve(items) });

  assert.deepEqual(await run(plain), ["query:found"], "无折扣 / 促销 / 多码：普通找到");
  assert.deepEqual(await run({ ...plain, storePrice: { ...plain.storePrice, discountRate: 0.2 } }), ["discount"]);
  assert.deepEqual(await run({ ...plain, multiCodeCount: 2 }), ["multi_code"]);
  assert.deepEqual(await run(plain, { pending: resolved("P-1", [{}]) }), ["promotion"], "有生效促销");
  assert.deepEqual(
    await run({ ...plain, multiCodeCount: 2, storePrice: { ...plain.storePrice, discountRate: 0.2 } }, { pending: resolved("P-1", [{}]) }),
    ["promotion"], "同时命中：促销优先",
  );
  assert.deepEqual(await run(plain, { pending: resolved("P-1", []) }), ["query:found"], "促销为空");

  // 促销请求被取代 / 失败（null）、属于别的商品、离线：都不能报促销。
  assert.deepEqual(await run(plain, { pending: resolved("P-1", null) }), ["query:found"]);
  assert.deepEqual(await run(plain, { pending: resolved("OTHER", [{}]) }), ["query:found"], "别的商品的促销请求不能算到这个商品头上");
  assert.deepEqual(await run(plain, { pending: resolved("P-1", [{}]), offline: true }), ["query:found"], "离线不报促销");

  // 促销请求太慢：不拖慢扫码，按折扣 / 多码继续判定。
  const never = { productCode: "P-1", result: new Promise<Promotions>(() => undefined) };
  const started = Date.now();
  assert.deepEqual(await run({ ...plain, multiCodeCount: 1 }, { pending: never }), ["multi_code"], "促销超时后按多码判定");
  assert.ok(Date.now() - started < 500, "等待促销不能超过上限太多");
  console.log("商品查询找到提示音（折扣 / 促销 / 多码）回归通过");
}

// 执行页面真实的手动打印回调：「普通」按钮与多码子项补打折扣标签，「小标签」「折扣」与套码子项保持原样。
async function runManualPrintRegression() {
  const withDiscount = structuredClone(currentDetail); // 主档折扣 0.2
  const sendOptions: { options?: PrintOptions }[] = [];
  const sendProductLabel = async (_detail: Detail, options?: PrintOptions) => {
    sendOptions.push({ options });
    return true;
  };
  const codeCalls: unknown[][] = [];
  const sendCodeLabel = async (...args: unknown[]) => {
    codeCalls.push(args);
    return true;
  };
  const discountCalls: unknown[][] = [];
  const messages: string[] = [];
  const common = {
    useCallback: (callback: unknown) => callback,
    detail: { ...withDiscount, multiCodes: [{ barcode: "MULTI-1", id: "multi-id" }] },
    setSnackbarMessage: (message: string) => messages.push(message),
    t: (key: string) => key,
    setPrintingAction: () => undefined,
    smallLabel: false,
    sendProductLabel,
    sendCodeLabel,
    printDiscountLabel: async (...args: unknown[]) => { discountCalls.push(args); return true; },
    printBigDiscountLabel: async () => true,
    printClearanceLabel: async () => true,
    printQuantity: 1,
    quantitySingleUse: true,
    setPrintQuantity: () => undefined,
    getErrorMessage: (_error: unknown, fallback: string) => fallback,
    getMultiCodeItemId: (item: { id: string }) => item.id,
    resolveMultiCodeLabelPrice: () => ({ retailPrice: 5, discountRate: 0.2 }),
  };
  const handlePrint = compileCallback<(kind: string) => Promise<void>>(handlePrintStatement, "handlePrint", common);

  // 手动「普通」标签：带 withDiscountLabel，是否真有折扣交给 sendProductLabel 判定。
  await handlePrint("product");
  assert.deepEqual(sendOptions[0].options, { action: "product", printType: null, withDiscountLabel: true });

  // 手动「小标签」：固定小标签尺寸，不补打折扣标签（保持原行为）。
  await handlePrint("small");
  assert.deepEqual(sendOptions[1].options, { action: "small", printType: "small" });

  // 手动「折扣」按钮本身只打一次折扣标签，不能被「普通」标签的补打逻辑重复触发。
  await handlePrint("discount");
  assert.equal(discountCalls.length, 1);
  assert.equal(sendOptions.length, 2);

  // 多码子项手动打印：与主条码同一商品，补打折扣标签。
  const handleMulti = compileCallback<(id: string) => Promise<void>>(
    handlePrintMultiCodeProductStatement, "handlePrintMultiCodeProduct", common);
  await handleMulti("multi-id");
  assert.equal(codeCalls.length, 1);
  assert.equal(codeCalls[0][3], true, "多码子项手动打印带 withDiscountLabel");

  // 套码子项手动打印：套装价不印折扣，不带 withDiscountLabel。
  const handleSet = compileCallback<(id: string) => Promise<void>>(
    handlePrintSetCodeProductStatement, "handlePrintSetCodeProduct", {
      ...common,
      detail: { ...withDiscount, setCodes: [{ setCodeId: "set-id", setBarcode: "SET-1", setRetailPrice: 10 }] },
    });
  await handleSet("set-id");
  assert.equal(sendOptions[2].options?.withDiscountLabel, undefined, "套码子项手动打印不补打折扣标签");
  assert.equal(sendOptions[2].options?.discountRate, 0);

  // 真实 sendProductLabel 串联：手动「普通」标签 + 有折扣 -> 普通、折扣各一张；无折扣只打普通。
  for (const [label, detail, expected] of [
    ["有折扣", withDiscount, ["product", "discount"]],
    ["无折扣", { ...structuredClone(currentDetail), storePrice: { ...currentDetail.storePrice, discountRate: 0 } }, ["product"]],
  ] as const) {
    const real = createSendProductLabel();
    const run = compileCallback<(kind: string) => Promise<void>>(handlePrintStatement, "handlePrint", {
      ...common,
      detail,
      sendProductLabel: real.sendProductLabel,
    });
    await run("product");
    assert.deepEqual(real.calls.map((call) => call.kind), expected, `手动普通标签：${label}`);
  }
  console.log("商品查询手动打印补打折扣标签回归通过");
}

async function runPageRegression() {
  for (const [scanKeyword, expectedOptions] of [
    // 自动打印链路一律带 withDiscountLabel：普通标签之后有折扣再补打折扣标签（是否真有折扣由 sendProductLabel 判定）。
    ["MAIN-1", { printType: null, withDiscountLabel: true }],
    ["PRODUCT-1", { printType: null, withDiscountLabel: true }],
    // 套装子项：套装条码 + 套装价，不印折扣（显式 0，避免打印层回退到主档折扣）。
    ["SET-1", { barcode: "SET-1", retailPrice: 10, discountRate: 0, action: "set:set-id", printType: null, withDiscountLabel: true }],
    // 多码：多码条码 + 主档门店价 + 主档折扣。
    ["MULTI-1", { barcode: "MULTI-1", retailPrice: 5, discountRate: 0.2, action: "multi:multi-id", printType: null, withDiscountLabel: true }],
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
  // 离线 / 非 200 供应商（无服务器 printTarget）扫到未加载的码：不能回退打印主档条码和价格。
  const offlineMissing = createPageFlow({ offline: true, scanKeyword: "SET-ON-PAGE-2" });
  assert.equal((await offlineMissing.run()).labelPrinted, false);
  assert.equal(offlineMissing.prints.length, 0, "离线未匹配的码不能回退打印主档");
  assert.deepEqual(offlineMissing.messages, ["messages.codesLoadFailed"]);
  const offlineSet = createPageFlow({ offline: true, scanKeyword: "SET-1" });
  assert.equal((await offlineSet.run()).labelPrinted, true);
  assert.deepEqual(offlineSet.prints[0].options, { barcode: "SET-1", retailPrice: 10, discountRate: 0, action: "set:set-id", printType: null, withDiscountLabel: true });
  const staleStore = createPageFlow({ scopeCurrent: false });
  assert.equal((await staleStore.run()).labelPrinted, false, "切店后的旧详情不打印");
  for (const print of [async () => false, async () => { throw new Error("printer disconnected"); }]) {
    const failedPrint = createPageFlow({ print });
    assert.equal((await failedPrint.run()).labelPrinted, false, "打印失败不能报告成功");
    assert.equal(failedPrint.prints.length, 1);
  }
  await runAutoDiscountLabelRegression();
  await runFoundFeedbackRegression();
  await runManualPrintRegression();
  console.log("商品查询当前门店价扫码打印回归通过");
}

void runPageRegression().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
