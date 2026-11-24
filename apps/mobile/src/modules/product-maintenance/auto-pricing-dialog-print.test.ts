import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import ts from "typescript";
import { resolveAutoPricingLabelPlan } from "./auto-pricing-label";
import {
  isSubCodeFocus,
  resolveFocusLabelTarget,
  resolveMultiCodeLabelPrice,
  resolveScannedCodeLabel,
  shouldAutoPrintDiscountLabel,
} from "./scan-label";

// 自动调价弹窗「仅保存 / 保存并打印」回归：执行页面真实的回调，只替换网络与打印机边界。
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

function compileCallback<T>(name: string, deps: Record<string, unknown>): T {
  const statement = callbackStatement(name);
  const source = `const { ${Object.keys(deps).join(", ")} } = deps;\n${statement.getText(pageAst)}\nreturn ${name};`;
  return new Function("deps", ts.transpile(source, { target: ts.ScriptTarget.ES2022 }))(deps) as T;
}

/** 页面顶层的纯函数声明（如 normalizeDiscountRateValue）：按源码原样编译，一起放进同一作用域。 */
function compileTopLevelFunctions<T>(names: string[], deps: Record<string, unknown> = {}): T {
  const texts = names.map((name) => {
    const node = pageAst.statements.find((statement): statement is ts.FunctionDeclaration =>
      ts.isFunctionDeclaration(statement) && statement.name?.text === name);
    assert.ok(node, `页面必须保留顶层函数 ${name}`);
    return node.getText(pageAst);
  });
  const source = `const { ${Object.keys(deps).join(", ")} } = deps;\n${texts.join("\n")}\nreturn { ${names.join(", ")} };`;
  return new Function("deps", ts.transpile(source, { target: ts.ScriptTarget.ES2022 }))(deps) as T;
}

const { normalizeDiscountRateValue, hasAutoPricingDialogDiscount } = compileTopLevelFunctions<{
  normalizeDiscountRateValue: (value?: number | null) => number | null;
  hasAutoPricingDialogDiscount: (dialog: Record<string, unknown>) => boolean;
}>(["normalizeDiscountRateValue", "hasAutoPricingDialogDiscount"], { shouldAutoPrintDiscountLabel });

type Detail = {
  productCode: string;
  barcode: string;
  storePrice: { uuid: string; retailPrice: number; discountRate: number | null };
  setCodes: { setCodeId: string; setBarcode: string; setRetailPrice: number }[];
  multiCodes: { uuid: string; setCodeId: string; barcode: string; retailPrice: number; discountRate: number }[];
};

// 保存后的详情：新零售价 3.99，折扣 0.2。
function savedDetail(discountRate: number | null = 0.2): Detail {
  return {
    productCode: "PRODUCT-1",
    barcode: "MAIN-1",
    storePrice: { uuid: "price-1", retailPrice: 3.99, discountRate },
    setCodes: [{ setCodeId: "set-id", setBarcode: "SET-1", setRetailPrice: 10 }],
    multiCodes: [{ uuid: "multi-uuid", setCodeId: "multi-id", barcode: "MULTI-1", retailPrice: 4, discountRate: 0.5 }],
  };
}

// ---------------------------------------------------------------------------
// 1. Disc / Large 能不能选：评估折扣率优先，缺省回退门店价折扣。
// ---------------------------------------------------------------------------
const dialogOf = (evaluationRate: number | null | undefined, storeRate: number | null | undefined) => ({
  evaluation: { discountRate: evaluationRate },
  detail: { storePrice: { discountRate: storeRate } },
});
assert.equal(hasAutoPricingDialogDiscount(dialogOf(0.2, 0)), true, "评估有折扣：以评估为准");
assert.equal(hasAutoPricingDialogDiscount(dialogOf(0, 0.2)), false, "评估显式 0：不能被门店价旧折扣盖过");
assert.equal(hasAutoPricingDialogDiscount(dialogOf(null, 0.2)), true, "评估缺省：回退门店价折扣");
assert.equal(hasAutoPricingDialogDiscount(dialogOf(undefined, undefined)), false, "都没有折扣");
assert.equal(hasAutoPricingDialogDiscount(dialogOf(20, null)), true, "百分数口径 20 按 0.2 处理");
assert.equal(hasAutoPricingDialogDiscount(dialogOf(150, null)), false, "超出范围的折扣率视为没有折扣");

// ---------------------------------------------------------------------------
// 2. 真实 sendProductLabel 的折扣类分支：只打所选那一种，份数取打印数量，没折扣就拒绝。
// ---------------------------------------------------------------------------
function createSendProductLabel(quantity = 1) {
  const calls: { kind: "product" | "discount" | "bigDiscount"; printType?: string | null; overrides?: Record<string, unknown> }[] = [];
  const messages: string[] = [];
  const feedback: string[] = [];
  const quantityResets: number[] = [];
  const sendProductLabel = compileCallback<(value: Detail, options?: Record<string, unknown>) => Promise<boolean>>(
    "sendProductLabel", {
      useCallback: (callback: unknown) => callback,
      getSavedPrinter: async () => ({ address: "AA:BB" }),
      setSnackbarMessage: (message: string) => messages.push(message),
      t: (key: string) => key,
      playQueryFeedback: (value: string) => feedback.push(value),
      printerAutoReconnectPaused: false,
      setPrintingAction: () => undefined,
      printProductLabel: async (_value: Detail, overrides: Record<string, unknown>, printType?: string | null) => {
        calls.push({ kind: "product", printType, overrides });
        return true;
      },
      printDiscountLabel: async (_value: Detail, printType?: string | null, overrides?: Record<string, unknown>) => {
        calls.push({ kind: "discount", printType, overrides });
        return true;
      },
      printBigDiscountLabel: async (_value: Detail, printType?: string | null, overrides?: Record<string, unknown>) => {
        calls.push({ kind: "bigDiscount", printType, overrides });
        return true;
      },
      shouldAutoPrintDiscountLabel,
      printQuantity: quantity,
      quantitySingleUse: true,
      setPrintQuantity: (value: number) => quantityResets.push(value),
      getErrorMessage: (_error: unknown, fallback: string) => fallback,
      handleReconnectPrinter: async () => true,
      setSnackbarAction: () => undefined,
      getPrintedLabelCount: () => 0,
      showPrintFailure: (_error: unknown, fallback: string) => { messages.push(fallback); },
      sendProductLabelRef: { current: null },
    },
  );
  return { sendProductLabel, calls, messages, feedback, quantityResets };
}

async function runSendProductLabelRegression() {
  // Disc：只打折扣标签两张（不先打普通标签），跟随小标签尺寸，打完把「单次有效」的数量恢复为 1。
  const disc = createSendProductLabel(2);
  assert.equal(await disc.sendProductLabel(savedDetail(), { action: "discount", printType: "small", labelKind: "discount" }), true);
  assert.deepEqual(disc.calls.map((call) => call.kind), ["discount", "discount"], "只打折扣标签，份数 = 打印数量");
  assert.equal(disc.calls[0].printType, "small");
  assert.deepEqual(disc.quantityResets, [1]);
  assert.deepEqual(disc.messages, ["messages.printSuccess"]);

  // Large：走大折扣标签，不是折扣标签；子码的条码 / 价格 overrides 要带上。
  const large = createSendProductLabel(1);
  assert.equal(await large.sendProductLabel(savedDetail(), {
    barcode: "SET-1", retailPrice: 10, discountRate: 0.2, action: "set:set-id", printType: null, labelKind: "bigDiscount",
  }), true);
  assert.deepEqual(large.calls.map((call) => call.kind), ["bigDiscount"]);
  assert.deepEqual(large.calls[0].overrides, { barcode: "SET-1", retailPrice: 10, discountRate: 0.2 });

  // 保存后折扣没了（门店价无折扣、或显式折扣 0）：价格已保存，明确提示，不静默改打别的标签。
  for (const [label, detail, options] of [
    ["门店价无折扣", savedDetail(0), { labelKind: "discount" }],
    ["门店价折扣缺省", savedDetail(null), { labelKind: "bigDiscount" }],
    ["显式折扣 0 盖过门店价折扣", savedDetail(0.2), { barcode: "SET-1", retailPrice: 10, discountRate: 0, labelKind: "discount" }],
  ] as const) {
    const flow = createSendProductLabel(1);
    assert.equal(await flow.sendProductLabel(detail, options), false, label);
    assert.equal(flow.calls.length, 0, `${label}：不能打任何标签`);
    assert.deepEqual(flow.messages, ["messages.discountPrintUnavailable"], label);
    assert.deepEqual(flow.feedback, ["error"], label);
  }

  // 没带 labelKind 的老调用方行为不变：普通标签照常打。
  const legacy = createSendProductLabel(1);
  assert.equal(await legacy.sendProductLabel(savedDetail(), { printType: null }), true);
  assert.deepEqual(legacy.calls.map((call) => call.kind), ["product"]);
  console.log("自动调价弹窗：sendProductLabel 折扣类标签回归通过");
}

// ---------------------------------------------------------------------------
// 3. 真实 sendAutoPricingLabel：标签类型 -> 打印参数，套装 / 多码子项只换条码与价格。
// ---------------------------------------------------------------------------
function createAutoPricingLabelFlow(smallLabel = false) {
  const sent: Record<string, unknown>[] = [];
  const messages: string[] = [];
  const sendProductLabel = async (_value: Detail, options?: Record<string, unknown>) => {
    sent.push(options ?? {});
    return true;
  };
  const sendCodeLabel = compileCallback<(
    value: Detail, target: Record<string, unknown>, printType: string | null,
    withDiscountLabel?: boolean | "only", actionOverride?: string, labelKind?: string,
  ) => Promise<boolean>>("sendCodeLabel", {
    useCallback: (callback: unknown) => callback,
    sendProductLabel,
    playQueryFeedback: () => undefined,
    setSnackbarMessage: (message: string) => messages.push(message),
    t: (key: string) => key,
  });
  const sendAutoPricingLabel = compileCallback<(
    saved: Detail, dialog: Record<string, unknown>, labelKind?: string,
  ) => Promise<boolean>>("sendAutoPricingLabel", {
    useCallback: (callback: unknown) => callback,
    smallLabel,
    sendCodeLabel,
    sendProductLabel,
    resolveAutoPricingLabelPlan,
    resolveMultiCodeLabelPrice,
    isSubCodeFocus,
    resolveFocusLabelTarget,
    resolveScannedCodeLabel,
    setSnackbarMessage: (message: string) => messages.push(message),
    t: (key: string) => key,
  });
  return { sendAutoPricingLabel, sent, messages };
}

const mainDialog = { detail: savedDetail(), scanKeyword: "MAIN-1", printTarget: null, scanFocus: null, autoPrintEnabled: false };

async function runSendAutoPricingLabelRegression() {
  // 主档：四种标签对应的参数（口径与底部打印条一致）。
  for (const [kind, smallLabel, expected] of [
    ["product", false, { withDiscountLabel: false, printType: null, action: "product" }],
    ["product", true, { withDiscountLabel: false, printType: "small", action: "product" }],
    ["small", false, { withDiscountLabel: false, printType: "small", action: "small" }],
    ["discount", true, { withDiscountLabel: false, printType: "small", action: "discount", labelKind: "discount" }],
    ["bigDiscount", true, { withDiscountLabel: false, printType: null, action: "bigDiscount", labelKind: "bigDiscount" }],
  ] as const) {
    const flow = createAutoPricingLabelFlow(smallLabel);
    assert.equal(await flow.sendAutoPricingLabel(savedDetail(), mainDialog, kind), true);
    assert.deepEqual(flow.sent, [expected], `主档 ${kind} 小标签开关=${smallLabel}`);
  }

  // 套装子项：套装条码 + 套装价 + 保存后的主档折扣，标签种类跟随所选。
  const setDialog = {
    ...mainDialog, scanKeyword: "SET-1",
    printTarget: { kind: "set", codeId: "set-id", barcode: "SET-1", retailPrice: 10, discountRate: null },
  };
  const setDisc = createAutoPricingLabelFlow(false);
  await setDisc.sendAutoPricingLabel(savedDetail(), setDialog, "discount");
  assert.deepEqual(setDisc.sent, [{
    barcode: "SET-1", retailPrice: 10, discountRate: 0.2, action: "set:set-id",
    printType: null, withDiscountLabel: false, labelKind: "discount",
  }]);
  const setSmall = createAutoPricingLabelFlow(false);
  await setSmall.sendAutoPricingLabel(savedDetail(), setDialog, "small");
  assert.deepEqual(setSmall.sent, [{
    barcode: "SET-1", retailPrice: 10, discountRate: 0.2, action: "small",
    printType: "small", withDiscountLabel: false,
  }]);

  // 多码子项：多码条码 + 保存后的主档价与折扣（不取多码行自身的 4 / 0.5）。
  const multiDialog = {
    ...mainDialog, scanKeyword: "MULTI-1",
    printTarget: { kind: "multi", codeId: "multi-id", barcode: "MULTI-1", retailPrice: 4, discountRate: null },
  };
  const multiLarge = createAutoPricingLabelFlow(false);
  await multiLarge.sendAutoPricingLabel(savedDetail(), multiDialog, "bigDiscount");
  assert.deepEqual(multiLarge.sent, [{
    barcode: "MULTI-1", retailPrice: 3.99, discountRate: 0.2, action: "multi:multi-id",
    printType: null, withDiscountLabel: false, labelKind: "bigDiscount",
  }]);

  // 没传标签类型（免确认的自动打印路径）：按普通标签处理，折扣仍走原来的 "only" 口径。
  const auto = createAutoPricingLabelFlow(false);
  await auto.sendAutoPricingLabel(savedDetail(), { ...mainDialog, autoPrintEnabled: true });
  assert.deepEqual(auto.sent, [{ withDiscountLabel: "only", printType: null, action: "product" }]);

  // 扫到的码在已加载码表之外：提示加载失败，不回退打印主档。
  const missing = createAutoPricingLabelFlow(false);
  assert.equal(await missing.sendAutoPricingLabel(savedDetail(), { ...mainDialog, scanKeyword: "UNKNOWN-9" }, "discount"), false);
  assert.deepEqual(missing.sent, []);
  assert.deepEqual(missing.messages, ["messages.codesLoadFailed"]);
  console.log("自动调价弹窗：sendAutoPricingLabel 标签映射回归通过");
}

// ---------------------------------------------------------------------------
// 4. 真实 confirmAutoPricingDialog：仅保存只写价格；保存并打印按所选标签打；失败 / 重复点击不误打。
// ---------------------------------------------------------------------------
function createConfirmFlow(options: {
  dialog?: Record<string, unknown> | null;
  saving?: boolean;
  persistResult?: Detail | null;
  labelKind?: string;
  printResult?: boolean;
} = {}) {
  const dialog = options.dialog === undefined
    ? {
      detail: { ...savedDetail(), storePrice: { uuid: "price-1", retailPrice: 4.5, discountRate: 0 } },
      evaluation: { recalculatedRetailPrice: 3.99, discountRate: 0.2 },
      scanSource: "camera",
    }
    : options.dialog;
  const events: string[] = [];
  const persistCalls: unknown[] = [];
  const printCalls: unknown[][] = [];
  const finished: Record<string, unknown>[] = [];
  const savingStates: boolean[] = [];
  const savingModes: (string | null)[] = [];
  const confirm = compileCallback<(withPrint: boolean) => Promise<void>>("confirmAutoPricingDialog", {
    useCallback: (callback: unknown) => callback,
    autoPricingDialog: dialog,
    autoPricingDialogSaving: options.saving === true,
    autoPricingLabelKind: options.labelKind ?? "product",
    setAutoPricingDialogSaving: (value: boolean) => savingStates.push(value),
    setAutoPricingSavingMode: (value: string | null) => savingModes.push(value),
    shouldRestoreCameraScan: () => true,
    normalizeDiscountRateValue,
    persistStorePrice: async (_detail: unknown, patch: unknown) => {
      events.push("persist");
      persistCalls.push(patch);
      return options.persistResult === undefined ? savedDetail() : options.persistResult;
    },
    sendAutoPricingLabel: async (...args: unknown[]) => {
      events.push("print");
      printCalls.push(args);
      return options.printResult !== false;
    },
    restoreScanAbility: () => { events.push("restore"); },
    finishAutoPricingDialog: (result: Record<string, unknown>) => { events.push("finish"); finished.push(result); },
  });
  return { confirm, events, persistCalls, printCalls, finished, savingStates, savingModes, dialog };
}

async function runConfirmRegression() {
  // 仅保存：写价格后直接结束，一次打印都不能有；返回 labelPrinted=false 但状态仍是 confirmed（不触发兜底打印）。
  const saveOnly = createConfirmFlow({ labelKind: "discount" });
  await saveOnly.confirm(false);
  assert.deepEqual(saveOnly.events, ["persist", "restore", "finish"]);
  assert.deepEqual(saveOnly.persistCalls, [{ retailPrice: 3.99, discountRate: 0.2, isAutoPricing: true }]);
  assert.equal(saveOnly.printCalls.length, 0, "仅保存不能打印");
  assert.deepEqual(saveOnly.savingModes, ["save"]);
  assert.equal(saveOnly.finished[0].status, "confirmed");
  assert.equal(saveOnly.finished[0].labelPrinted, false);

  // 保存并打印：先写价格，再按所选标签打，打印的是保存后的详情。
  const saveAndPrint = createConfirmFlow({ labelKind: "discount" });
  await saveAndPrint.confirm(true);
  assert.deepEqual(saveAndPrint.events, ["persist", "print", "restore", "finish"]);
  assert.equal((saveAndPrint.printCalls[0][0] as Detail).storePrice.retailPrice, 3.99, "打印用保存后的新价详情");
  assert.equal(saveAndPrint.printCalls[0][1], saveAndPrint.dialog);
  assert.equal(saveAndPrint.printCalls[0][2], "discount", "按弹窗所选标签打印");
  assert.deepEqual(saveAndPrint.savingModes, ["saveAndPrint"]);
  assert.equal(saveAndPrint.finished[0].status, "confirmed");
  assert.equal(saveAndPrint.finished[0].labelPrinted, true);

  // 打印失败：价格已保存，仍按 confirmed 结束，只是 labelPrinted=false（失败提示由打印层给出）。
  const printFails = createConfirmFlow({ printResult: false });
  await printFails.confirm(true);
  assert.equal(printFails.finished[0].status, "confirmed");
  assert.equal(printFails.finished[0].labelPrinted, false);

  // 保存失败：不打印，以 failed 结束并恢复扫码。
  for (const withPrint of [false, true]) {
    const failed = createConfirmFlow({ persistResult: null });
    await failed.confirm(withPrint);
    assert.deepEqual(failed.events, ["persist", "restore", "finish"], `保存失败 withPrint=${withPrint}`);
    assert.equal(failed.printCalls.length, 0);
    assert.equal(failed.finished[0].status, "failed");
    assert.equal(failed.finished[0].labelPrinted, false);
  }

  // 保存中再点（闸门已关）或弹窗已不在：什么都不做，不会二次写库。
  for (const [label, flow] of [
    ["保存中", createConfirmFlow({ saving: true })],
    ["弹窗已关闭", createConfirmFlow({ dialog: null })],
  ] as const) {
    await flow.confirm(true);
    assert.deepEqual(flow.events, [], label);
    assert.deepEqual(flow.savingStates, [], label);
  }
  console.log("自动调价弹窗：仅保存 / 保存并打印流程回归通过");
}

// ---------------------------------------------------------------------------
// 5. 界面契约：两个确认键各自接线，旧的一键确认文案已移除。
// ---------------------------------------------------------------------------
function runSourceContract() {
  assert.ok(pageSource.includes("confirmAutoPricingDialog(false)"), "「仅保存」必须接到 confirmAutoPricingDialog(false)");
  assert.ok(pageSource.includes("confirmAutoPricingDialog(true)"), "「保存并打印」必须接到 confirmAutoPricingDialog(true)");
  assert.ok(pageSource.includes('t("autoPricingConfirm.saveOnly")'));
  assert.ok(pageSource.includes('t("autoPricingConfirm.saveAndPrint")'));
  assert.ok(!pageSource.includes("autoPricingConfirm.confirm"), "旧的一键确认文案不应再被引用");
  assert.ok(pageSource.includes("AUTO_PRICING_LABEL_KINDS.map"), "弹窗必须渲染标签单选");
  console.log("自动调价弹窗：界面契约回归通过");
}

async function main() {
  await runSendProductLabelRegression();
  await runSendAutoPricingLabelRegression();
  await runConfirmRegression();
  runSourceContract();
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
