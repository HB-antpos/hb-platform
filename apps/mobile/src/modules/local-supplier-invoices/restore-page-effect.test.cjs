/* global __dirname */
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { createRequire } = require("node:module");
const ts = require("typescript");
const requireFromMobile = createRequire(path.resolve(__dirname, "../../../package.json"));
const rendererRequire = createRequire(requireFromMobile.resolve("test-renderer"));
const React = rendererRequire("react");
const { createRoot } = rendererRequire("test-renderer");

// 从真实页面源码提取「从商品编辑返回后恢复详情」的两个 effect，在 test-renderer 中运行，
// 避免只用静态正则测试实现细节。明细已改为一次加载整张单：页面不再保存明细页码 / 搜索词，
// 只把 {detailGuid, priceChange, search} 作为 initialView 交给明细组件。
const screenPath = path.resolve(__dirname, "../../../app/(shell)/local-supplier-invoices.tsx");
const source = fs.readFileSync(screenPath, "utf8");
const sourceFile = ts.createSourceFile(screenPath, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
const effectTexts = [];
function collectEffects(node) {
  if (ts.isCallExpression(node) && node.expression.getText(sourceFile) === "useEffect") {
    effectTexts.push(node.getText(sourceFile));
  }
  ts.forEachChild(node, collectEffects);
}
collectEffects(sourceFile);
const restoreEffect = effectTexts.find((text) =>
  text.includes("restoreState") && text.includes("handledRestoreKeyRef")
);
const matchEffect = effectTexts.find((text) =>
  text.includes("pendingRestoreRef.current") && text.includes("setDetailInitialView") && text.includes("completedListRequest")
);
assert.ok(restoreEffect, "真实页面 restore effect must be present");
assert.ok(matchEffect, "真实页面 restore-match effect must be present");
// 旧的分页明细状态不应再出现在恢复链路里。
assert.ok(!restoreEffect.includes("setDetailsPage"), "restore effect must not touch detail paging any more");
assert.ok(!restoreEffect.includes("setDetailSearchQuery"), "restore effect must not touch detail search debounce any more");

const restoreHarnessSource = `
const { useRef, useEffect } = React;
function RestoreHarness({ restoreState, calls }) {
  const handledRestoreKeyRef = useRef(null);
  const pendingRestoreRef = useRef(null);
  const listRequestIdRef = useRef(0);
  const initialStoreScopeAppliedRef = useRef(false);
  const rec = (name) => (...args) => calls.push([name, ...args]);
  const setDraftFilters = rec("setDraftFilters"), setFilters = rec("setFilters"), setSort = rec("setSort"),
    setPageSize = rec("setPageSize"), setPage = rec("setPage"), setSelectedInvoice = rec("setSelectedInvoice"),
    setDetailInitialView = rec("setDetailInitialView"), setInitialStoreScopeReady = rec("setInitialStoreScopeReady");
  const bindDeviceStore = (value) => value;
  const buildInvoiceListRequestKey = (value) => JSON.stringify(value);
  const buildLocalSupplierInvoicesReturnParams = (value) => value;
  ${restoreEffect}
  calls.pending = pendingRestoreRef;
  return null;
}
return RestoreHarness;`;

const matchHarnessSource = `
const { useRef, useEffect } = React;
function MatchHarness({ pending, completedListRequest, items, page, pageSize, filters, sort, calls }) {
  const pendingRestoreRef = useRef(pending);
  const rec = (name) => (...args) => calls.push([name, ...args]);
  const setSnackbar = rec("setSnackbar"), setSelectedInvoice = rec("setSelectedInvoice"), setDetailInitialView = rec("setDetailInitialView");
  const t = (key) => key;
  ${matchEffect}
  return null;
}
return MatchHarness;`;

const compile = (code) => new Function("React", ts.transpile(code, { target: ts.ScriptTarget.ES2022 }))(React);
const RestoreHarness = compile(restoreHarnessSource);
const MatchHarness = compile(matchHarnessSource);

const filters = { storeCode: "S01" };
const sort = { colId: "OrderDate", direction: "desc" };
function makeRestore(priceChange, search) {
  return {
    returnInvoiceGuid: "invoice-1", returnDetailGuid: "detail-4",
    returnDetailsPage: 4, returnDetailsPageSize: 50,
    returnDetailPriceChangeFilter: priceChange, returnDetailSearch: search,
    returnListPage: 2, returnListPageSize: 20, filters, sort,
  };
}

async function render(root, element) {
  await React.act(async () => root.render(element));
}

(async () => {
  globalThis.IS_REACT_ACT_ENVIRONMENT = true;

  // 1) restore effect：恢复列表筛选 / 页码，清空当前详情；同一个 restoreState 重渲染不会重复触发。
  {
    const calls = [];
    const root = createRoot();
    const restoreState = makeRestore("up", "ITEM-4");
    await render(root, React.createElement(RestoreHarness, { restoreState, calls }));
    await render(root, React.createElement(RestoreHarness, { restoreState: { ...restoreState }, calls }));
    const names = calls.map((entry) => entry[0]);
    assert.equal(names.filter((name) => name === "setPage").length, 1, "same restore key runs only once");
    assert.deepEqual(calls.find((entry) => entry[0] === "setPage").slice(1), [2], "restores list page");
    assert.deepEqual(calls.find((entry) => entry[0] === "setSelectedInvoice").slice(1), [null], "clears current detail before restoring");
    assert.deepEqual(calls.find((entry) => entry[0] === "setDetailInitialView").slice(1), [null], "clears stale initial view");
    assert.ok(calls.pending.current && calls.pending.current.returnDetailGuid === "detail-4", "keeps the pending restore for the list-loaded match");
    await React.act(async () => root.unmount());
  }

  // 2) match effect：列表加载完成并命中原单据 → 打开详情并带上恢复视图。
  const completed = { id: 3, key: JSON.stringify({ filters, page: 2, pageSize: 20, sort }) };
  const pending = {
    ...makeRestore("down", "ITEM-9"),
    listRequestKey: completed.key,
    minimumListRequestId: 2,
  };
  {
    const calls = [];
    const root = createRoot();
    const invoice = { invoiceGuid: "invoice-1" };
    await render(root, React.createElement(MatchHarness, {
      pending, completedListRequest: completed, items: [{ invoiceGuid: "other" }, invoice], page: 2, pageSize: 20, filters, sort, calls,
    }));
    assert.deepEqual(
      calls.find((entry) => entry[0] === "setDetailInitialView").slice(1),
      [{ detailGuid: "detail-4", priceChange: "down", search: "ITEM-9" }],
      "match passes filter, search and anchor row to the detail view"
    );
    assert.deepEqual(calls.find((entry) => entry[0] === "setSelectedInvoice").slice(1), [invoice], "reopens the original invoice");
    await React.act(async () => root.unmount());
  }

  // 3) 列表里找不到原单据：提示而不是打开别的单据。
  {
    const calls = [];
    const root = createRoot();
    await render(root, React.createElement(MatchHarness, {
      pending, completedListRequest: completed, items: [{ invoiceGuid: "other" }], page: 2, pageSize: 20, filters, sort, calls,
    }));
    assert.deepEqual(calls.map((entry) => entry[0]), ["setSnackbar"], "missing invoice only shows the restore-missing message");
    assert.equal(calls[0][1], "messages.restoreInvoiceMissing");
    await React.act(async () => root.unmount());
  }

  // 4) 列表请求还没完成（请求序号低于恢复时记录的下限）：不能提前匹配。
  {
    const calls = [];
    const root = createRoot();
    await render(root, React.createElement(MatchHarness, {
      pending, completedListRequest: { ...completed, id: 1 }, items: [{ invoiceGuid: "invoice-1" }], page: 2, pageSize: 20, filters, sort, calls,
    }));
    assert.equal(calls.length, 0, "stale list request must not match the restore");
    await React.act(async () => root.unmount());
  }
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
