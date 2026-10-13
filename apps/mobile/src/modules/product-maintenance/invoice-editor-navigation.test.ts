import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { resolve } from "node:path";
import ts from "typescript";
import { resolveInvoiceEditorExitAction } from "./invoice-editor-exit";
import {
  buildLocalSupplierInvoicesRestoreHref,
  type LocalSupplierInvoicesReturnState,
} from "../local-supplier-invoices/navigation";

const localRequire = createRequire(__filename);
// 使用 renderer 自己的 React，避免 workspace 中两份 React 导致 Hook 失效。
const React: typeof import("react") = createRequire(localRequire.resolve("test-renderer"))("react");
const { createRoot }: typeof import("test-renderer") = localRequire("test-renderer");
Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true });

const source = readFileSync(resolve(__dirname, "../../../app/(shell)/product-query.tsx"), "utf8");
const ast = ts.createSourceFile("product-query.tsx", source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const component = ast.statements.find((node): node is ts.FunctionDeclaration =>
  ts.isFunctionDeclaration(node) && node.name?.text === "ProductQueryContent");
assert.ok(component?.body);

// 执行页面真实的退出处理器及 React effects；只替换原生导航和保存边界。
const names = new Set([
  "discardReturnVisible", "allowInvoiceExit", "pendingInvoiceExitRef", "invoiceExitSavingRef",
  "isInvoiceExitBusy", "isInvoiceEditorSessionActive", "performReturnToInvoices", "requestInvoiceExit",
  "handleReturnToInvoices", "handleSaveAndReturnToInvoices",
  "handleCancelInvoiceExit", "handleDiscardInvoiceExit",
]);
function bindingNames(name: ts.BindingName): string[] {
  return ts.isIdentifier(name) ? [name.text] : name.elements.flatMap((element) =>
    ts.isBindingElement(element) ? bindingNames(element.name) : []);
}
const exitCode = component.body.statements.filter((statement) => {
  if (ts.isVariableStatement(statement)) {
    return statement.declarationList.declarations.some((declaration) =>
      bindingNames(declaration.name).some((name) => names.has(name)));
  }
  if (!ts.isExpressionStatement(statement) || !ts.isCallExpression(statement.expression)) return false;
  const call = statement.expression;
  return call.expression.getText(ast) === "usePreventRemove" ||
    (call.expression.getText(ast) === "useEffect" && call.getText(ast).includes("allowInvoiceExit"));
}).map((statement) => statement.getText(ast)).join("\n");

type Action = { type: string; payload?: object };
type Capture = {
  discardReturnVisible: boolean;
  handleReturnToInvoices(): void;
  handleSaveAndReturnToInvoices(): Promise<void>;
  handleCancelInvoiceExit(): void;
  handleDiscardInvoiceExit(): void;
};
const returnState: LocalSupplierInvoicesReturnState = {
  source: "local-supplier-invoices" as const,
  returnInvoiceGuid: "invoice-7", returnDetailGuid: "line-160", returnDetailsPage: 4,
  returnDetailsPageSize: 50 as const, returnDetailPriceChangeFilter: "all" as const,
  returnDetailSearch: "", returnListPage: 3, returnListPageSize: 20 as const,
  filters: { storeCode: "LIST-STORE" }, sort: { colId: "OrderDate", direction: "desc" as const },
};

async function mount(options: { dirty?: boolean; busy?: boolean; hasContext?: boolean; device?: boolean; save?: () => Promise<boolean> } = {}) {
  let current!: Capture;
  let authenticated = !options.device;
  let deviceSession = options.device ? { hardwareId: "device-1", authCode: "test-code", storeCode: "S001" } : null;
  const events: string[] = [];
  const destinations: unknown[] = [];
  const dispatched: Action[] = [];
  let removal: { prevent: boolean; callback: (event: { data: { action: Action } }) => void } | undefined;
  const navigation = { dispatch(action: Action) {
    assert.equal(removal?.prevent, false, "必须先经 React 更新关闭 guard，再重放导航");
    dispatched.push(action);
  } };
  const router = { replace(href: unknown) {
    assert.equal(removal?.prevent, false, "恢复发票前必须先关闭 guard");
    events.push("return"); destinations.push(href);
  } };
  const usePreventRemove = (prevent: boolean, callback: NonNullable<typeof removal>["callback"]) => {
    React.useEffect(() => { removal = { prevent, callback }; });
  };
  const code = `const { useState, useRef, useEffect, useCallback } = React;
    return function Harness() {
      const invoiceReturnState = config.hasContext === false ? null : returnState;
      const detail = { dirty: !!config.dirty }, initialDetail = {};
      const isAuthenticated = useAuthStore.getState().isAuthenticated;
      const isDeviceMode = !!config.device;
      const saving = !!config.busy, savingItemId = null, savingClearance = false,
        productTypeSaving = false, nameSaving = false, createProductBusy = false, hqSyncRetrying = false,
        autoPricingDialogSaving = false, printingAction = null;
      const warehousePriceSyncState = { phase: "idle" };
      ${exitCode}
      capture({ discardReturnVisible, handleReturnToInvoices, handleSaveAndReturnToInvoices,
        handleCancelInvoiceExit: typeof handleCancelInvoiceExit === "function" ? handleCancelInvoiceExit : () => {},
        handleDiscardInvoiceExit: typeof handleDiscardInvoiceExit === "function" ? handleDiscardInvoiceExit : () => {} });
      return null;
    }`;
  const Harness = new Function("React", "config", "returnState", "capture", "usePreventRemove",
    "navigation", "router", "useAuthStore", "resolveInvoiceEditorExitAction",
    "isStorePriceDirty", "buildLocalSupplierInvoicesRestoreHref", "handleSaveAll", "setSnackbarMessage", "t", "useDeviceStore",
    ts.transpile(code, { target: ts.ScriptTarget.ES2022 }))(
    React, options, returnState, (value: Capture) => { current = value; }, usePreventRemove,
    navigation, router, { getState: () => ({ isAuthenticated: authenticated, sessionKind: options.device ? "device" : "account" }) }, resolveInvoiceEditorExitAction,
    (detail: { dirty: boolean }) => detail.dirty, buildLocalSupplierInvoicesRestoreHref,
    async () => { events.push("save"); return options.save ? options.save() : true; },
    () => { events.push("busy"); }, (key: string) => key, { getState: () => ({ session: deviceSession }) },
  );
  const root = createRoot();
  await React.act(() => root.render(React.createElement(Harness)));
  return {
    get current() { return current; }, events, destinations, dispatched,
    expireSession() { authenticated = false; deviceSession = null; },
    async remove(action: Action) {
      await React.act(() => {
        if (removal?.prevent) removal.callback({ data: { action } });
        else dispatched.push(action);
      });
    },
    async unmount() { await React.act(() => root.unmount()); },
  };
}

async function run() {
  for (const action of [{ type: "GO_BACK" }, { type: "POP", payload: { count: 1 } }]) {
    const editor = await mount({ dirty: true });
    try {
      await editor.remove(action);
      assert.equal(editor.current.discardReturnVisible, true, `${action.type} 必须提示未保存修改`);
      assert.equal(editor.dispatched.length, 0);
      await React.act(() => editor.current.handleCancelInvoiceExit());
      assert.equal(editor.current.discardReturnVisible, false);
      assert.equal(editor.destinations.length, 0, "取消退出必须留页");
      await editor.remove(action);
      await React.act(() => editor.current.handleDiscardInvoiceExit());
      assert.deepEqual(editor.destinations, [buildLocalSupplierInvoicesRestoreHref(returnState)], "放弃后恢复原发票、页码、商品和列表分店");
    } finally { await editor.unmount(); }
  }
  const clean = await mount();
  try {
    await clean.remove({ type: "GO_BACK" });
    assert.deepEqual(clean.destinations, [buildLocalSupplierInvoicesRestoreHref(returnState)]);
  } finally { await clean.unmount(); }

  const device = await mount({ device: true, dirty: true });
  try {
    await device.remove({ type: "GO_BACK" });
    assert.equal(device.current.discardReturnVisible, true, "设备模式没有账号 token，也必须拦截未保存返回");
    await React.act(() => device.current.handleDiscardInvoiceExit());
    assert.deepEqual(device.destinations, [buildLocalSupplierInvoicesRestoreHref(returnState)]);
  } finally { await device.unmount(); }

  for (const succeeded of [false, true]) {
    let finish!: (value: boolean) => void;
    const saved = new Promise<boolean>((resolve) => { finish = resolve; });
    const editor = await mount({ dirty: true, save: () => saved });
    try {
      let pending!: Promise<void>;
      await React.act(() => { pending = editor.current.handleSaveAndReturnToInvoices(); });
      await editor.remove({ type: "GO_BACK" });
      await React.act(() => editor.current.handleSaveAndReturnToInvoices());
      assert.equal(editor.events.filter((event) => event === "save").length, 1, "保存中重复触发不能重复写入");
      assert.equal(editor.destinations.length, 0, "保存完成前不能离页");
      await React.act(async () => { finish(succeeded); await pending; });
      assert.equal(editor.destinations.length, succeeded ? 1 : 0, "仅保存成功才能返回");
    } finally { await editor.unmount(); }
  }
  const busy = await mount({ dirty: true, busy: true });
  try {
    await busy.remove({ type: "POP", payload: { count: 1 } });
    assert.equal(busy.current.discardReturnVisible, false);
    assert.deepEqual(busy.events, ["busy"]);
  } finally { await busy.unmount(); }

  const auth = await mount({ dirty: true });
  try {
    auth.expireSession();
    const action = { type: "REPLACE", payload: { name: "(auth)/login" } };
    await auth.remove(action);
    assert.deepEqual(auth.dispatched, [action], "登录失效应重放真实重定向，不跳回发票");
    assert.equal(auth.destinations.length, 0);
  } finally { await auth.unmount(); }

  const differentDestination = await mount({ dirty: true });
  try {
    const action = { type: "REPLACE", payload: { name: "orders" } };
    await differentDestination.remove(action);
    assert.equal(differentDestination.current.discardReturnVisible, true);
    await React.act(() => differentDestination.current.handleDiscardInvoiceExit());
    assert.deepEqual(differentDestination.dispatched, [action], "明确跳转其他页面时保留原目标");
  } finally { await differentDestination.unmount(); }
  const ordinary = await mount({ hasContext: false, dirty: true });
  try {
    await ordinary.remove({ type: "GO_BACK" });
    assert.equal(ordinary.dispatched.length, 1, "普通商品查询保持原导航行为");
  } finally { await ordinary.unmount(); }
  assert.match(source, /onDismiss=\{handleCancelInvoiceExit\}/);
  assert.match(source, /onPress=\{handleDiscardInvoiceExit\}/);
  console.log("发票商品编辑导航生命周期回归通过");
}

void run().catch((error) => { console.error(error); process.exitCode = 1; });
