import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 供应商紧凑表 + 点击后的弹窗（选择 → 商品明细 / 分店数据 → 继续下钻）的真实渲染测试：
// 在 test-renderer 里挂载真实组件（react-native / paper / react-query 换成同名宿主桩），点击并断言可见文案与回调，
// 钉住「表头列顺序」「中国页签名称下固定显示编号」「点任意位置打开弹窗」「表头排序」「滑动提示」
// 「商品明细里点商品看分店」「分店数据里点分店看该分店商品」「返回回到上一层」「保留只联动筛选入口」。
const zh = JSON.parse(readFileSync(resolve(__dirname, "../../locales/zh/common.json"), "utf8")) as Record<string, unknown>;

function resolveKey(key: string): unknown {
  return key
    .split(".")
    .reduce<unknown>(
      (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
      zh,
    );
}

function lookup(key: string, options?: Record<string, unknown>): string {
  const value = resolveKey(key);
  if (typeof value !== "string") throw new Error(`中文语言包缺少键：${key}`);
  return value.replace(/\{\{(\w+)\}\}/g, (_, name: string) => String(options?.[name] ?? ""));
}

type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };

function textOf(node: Node | string): string {
  if (typeof node === "string") return node;
  return node.children.map(textOf).join("");
}

const snapshot = <T,>(data: T) => ({
  data,
  statisticStatus: "Fresh",
  statisticMessage: null,
  statisticUpdatedAt: null,
  cacheVersion: "v1",
  isComplete: true,
  pollingExhausted: false,
  pollingAttemptCount: 1,
});

const productRow = (index: number) => ({
  id: `p${index}`,
  productCode: `CODE${index}`,
  itemNumber: `HB2030${index}`,
  productImage: null,
  productName: `Sample Product ${index}`,
  quantity: 100 * index,
  compareQuantity: 80 * index,
  salesAmount: 1000 * index,
  compareSalesAmount: 800 * index,
  grossProfit: 300 * index,
  compareGrossProfit: 240 * index,
  grossMarginRate: 0.3,
  compareGrossMarginRate: 0.3,
  averageUnitPrice: 10,
  compareAverageUnitPrice: 10,
  orderCount: 10,
  compareOrderCount: 8,
  costStatus: "Complete" as const,
  compareCostStatus: "Complete" as const,
});

const supplierBranchRow = (code: string, name: string, revenue: number) => ({
  id: `sb-${code}`,
  branchCode: code,
  branchName: name,
  supplierCode: "SUP-1013",
  supplierName: "Sample Toys Wholesale",
  revenue,
  compareRevenue: revenue / 2,
  grossProfit: revenue * 0.3,
  compareGrossProfit: revenue * 0.15,
  grossMarginRate: 0.3,
  compareGrossMarginRate: 0.3,
  totalQuantity: 50,
  compareTotalQuantity: 40,
  averagePrice: revenue / 50,
  compareAveragePrice: revenue / 50,
  orderCount: 5,
  compareOrderCount: 4,
  averageTransaction: 10,
  compareAverageTransaction: 10,
  costStatus: "Complete" as const,
  compareCostStatus: "Complete" as const,
});

const productBranchRow = (code: string, name: string, amount: number) => ({
  id: `pb-${code}`,
  branchCode: code,
  branchName: name,
  quantity: 20,
  compareQuantity: 15,
  discountedQuantity: 0,
  salesAmount: amount,
  compareSalesAmount: amount / 2,
  grossProfit: amount * 0.3,
  compareGrossProfit: amount * 0.15,
  grossMarginRate: 0.3,
  compareGrossMarginRate: 0.3,
  averageUnitPrice: 10,
  compareAverageUnitPrice: 10,
  costStatus: "Complete" as const,
  compareCostStatus: "Complete" as const,
});

async function run() {
  Object.assign(globalThis, { __DEV__: false, IS_REACT_ACT_ENVIRONMENT: true, React });
  const mockModule = (name: string, exports: object) => {
    const filename = require.resolve(name);
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };
  const rendererReactFilename = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReactFilename !== require.resolve("react")) mockModule(rendererReactFilename, React);

  const host = (type: string) => {
    const Stub = (props: Record<string, unknown>) => React.createElement(type, props);
    Stub.displayName = type;
    return Stub;
  };
  mockModule("react-native", {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Pressable: host("Pressable"),
    ScrollView: host("ScrollView"),
    Image: host("Image"),
  });
  // Modal 只在 visible 时渲染内容；Portal 直接透传。
  const ModalStub = (props: { visible: boolean; children?: React.ReactNode }) =>
    props.visible ? React.createElement("Modal", props) : null;
  mockModule("react-native-paper", {
    Text: host("Text"),
    Button: host("Button"),
    IconButton: host("IconButton"),
    ActivityIndicator: host("ActivityIndicator"),
    Modal: ModalStub,
    Portal: (props: { children?: React.ReactNode }) => React.createElement(React.Fragment, null, props.children),
  });
  mockModule("react-native-safe-area-context", { useSafeAreaInsets: () => ({ top: 0, bottom: 0, left: 0, right: 0 }) });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: lookup, language: "zh" }),
  });
  // react-query 换成按 queryKey 返回固定数据的桩；enabled=false 时和真实库一样返回 data: undefined。
  const requestedKinds: string[] = [];
  mockModule("@tanstack/react-query", {
    keepPreviousData: (previous: unknown) => previous,
    useQuery: (options: { queryKey: readonly unknown[]; enabled?: boolean }) => {
      const kind = String(options.queryKey[7]);
      let data: ReturnType<typeof snapshot> | undefined;
      if (options.enabled !== false) {
        requestedKinds.push(kind);
        if (kind === "products") {
          data = snapshot({ rows: [productRow(1), productRow(2)], total: 128, pageIndex: 1, pageSize: 100 });
        } else if (kind === "branch-products") {
          data = snapshot({ rows: [productRow(3)], total: 46, pageIndex: 1, pageSize: 100 });
        } else if (kind === "branches") {
          data = snapshot([
            supplierBranchRow("BNK", "Sample Bankstown", 8420),
            supplierBranchRow("PAR", "Sample Parramatta", 6910),
          ]);
        } else if (kind === "product-branches") {
          data = snapshot([productBranchRow("BNK", "Sample Bankstown", 1420), productBranchRow("LIV", "Sample Liverpool", 980)]);
        }
      }
      return { data, isLoading: false, isError: false, isFetching: false, isPlaceholderData: false, refetch: () => undefined };
    },
  });

  const { createRoot } = await import("test-renderer");
  const { SupplierReportTable } = await import("./supplier-report-table");
  const { SupplierDetailSheet } = await import("./supplier-detail-sheet");

  const root = createRoot();
  const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
  const all = (type: string) => container.queryAll((node) => node.type === type);
  const texts = () => all("Text").map(textOf);
  const render = (element: React.ReactElement) => act(() => root.render(element));
  const byLabel = (type: string, label: string) => all(type).find((node) => node.props.accessibilityLabel === label);

  try {
    // ───────────── 供应商紧凑表 ─────────────
    const cell = (value: string, compare: string | null = null, extra: Record<string, unknown> = {}) => ({ value, compare, ...extra });
    const tableRow = (id: string, title: string, code: string, selected: boolean, onPress: () => void, compare: boolean) => ({
      id,
      rank: "01",
      title,
      code,
      selected,
      accessibilityLabel: `open ${title}`,
      onPress,
      growth: React.createElement("Text", null, "+17.3%"),
      cells: {
        revenue: cell("$48,320", compare ? "$41,200" : null),
        quantity: cell("6,812", compare ? "5,890" : null),
        share: cell("13.0%", compare ? "11.0%" : null, { barPercent: 80 }),
        averagePrice: cell("$7.09", compare ? "$7.00" : null),
        grossProfit: cell("$17,250", compare ? "$14,000" : null),
        grossMargin: cell("成本待补全", compare ? "35.0%" : null, { pending: true }),
        totalShare: cell("6.7%", compare ? "5.9%" : null, { barPercent: 40 }),
      },
    });
    const tableEvents: string[] = [];
    const sorts: string[] = [];
    const renderTable = (isChina: boolean, selectedIndex = -1) =>
      render(
        React.createElement(SupplierReportTable, {
          rows: [
            tableRow("a", "Sample Gift & Homewares Pty Ltd", "SUP-1001", selectedIndex === 0, () => tableEvents.push("a"), isChina),
            tableRow("b", "Sample Kitchen Supplies", "SUP-1007", selectedIndex === 1, () => tableEvents.push("b"), isChina),
          ],
          isChina,
          hint: "hint-text",
          sort: { field: "amount", order: "desc" },
          onSort: (field: string) => sorts.push(field),
        }),
      );

    await renderTable(false);
    const headerTexts = texts();
    const order = ["金额 ▼", "数量", "增长率", "占比", "均价", "毛利额", "毛利率", "总占比"].map((label) => headerTexts.indexOf(label));
    assert.ok(order.every((index) => index >= 0), `表头 8 列必须齐全：${headerTexts.join("|")}`);
    assert.deepEqual([...order].sort((a, b) => a - b), order, "列顺序：首屏 金额 数量 增长率 占比，左滑后 均价 毛利额 毛利率 总占比");
    assert.ok(headerTexts.includes(lookup("productReport.sections.suppliers")), "固定列表头是「供应商」");
    assert.equal(all("ScrollView").length, 1, "右侧所有行共用一个横向 ScrollView");
    assert.equal(all("ScrollView")[0].props.horizontal, true);
    assert.ok(headerTexts.includes("营业额"), "澳洲占比列副标题是「营业额」");
    assert.ok(!headerTexts.some((text) => text === lookup("productReport.supplierTable.compareLegend")), "澳洲页签表头不标注同期");
    assert.ok(!headerTexts.includes("$41,200"), "澳洲页签不出现同期小字");

    // 编号：澳洲页签先不显示（名称按两行处理），名称单行时才显示。
    assert.ok(!headerTexts.includes("SUP-1001"), "澳洲页签名称占两行时不显示编号");
    const nameText = all("Text").find((node) => textOf(node) === "Sample Kitchen Supplies")!;
    await act(() => nameText.props.onTextLayout({ nativeEvent: { lines: [{}] } }));
    assert.ok(texts().includes("SUP-1007"), "澳洲页签名称单行时在名称下方显示编号");

    // 点任意位置（左列行 / 右侧行）都走同一个回调；表头点按排序。
    await act(() => byLabel("Pressable", "open Sample Gift & Homewares Pty Ltd")!.props.onPress());
    const rightRows = all("Pressable").filter((node) => node.props.accessible === false);
    assert.equal(rightRows.length, 2, "右侧每行是一个不重复朗读的可点区域");
    await act(() => rightRows[1].props.onPress());
    assert.deepEqual(tableEvents, ["a", "b"], "名称与右侧指标点哪里都触发该行回调");
    await act(() => byLabel("Pressable", lookup("productReport.sort.accessibilityLabel", { column: "均价", state: "未排序" }))!.props.onPress());
    await act(() => byLabel("Pressable", lookup("productReport.sort.accessibilityLabel", { column: "数量", state: "未排序" }))!.props.onPress());
    await act(() => byLabel("Pressable", lookup("productReport.sort.accessibilityLabel", { column: "金额", state: "降序" }))!.props.onPress());
    assert.deepEqual(sorts, ["unitPrice", "quantity", "amount"], "表头只有 金额 / 数量 / 均价 可排序，字段沿用原排序字段");
    assert.equal(
      all("Pressable").filter((node) => String(node.props.accessibilityLabel ?? "").startsWith("按")).length,
      3,
      "增长率、占比、毛利额、毛利率、总占比不可排序",
    );

    // 滑动提示：量到视口比内容窄才出现；滑到底后改成「右滑回首屏」。
    assert.ok(!texts().some((text) => text.includes(lookup("productReport.supplierTable.swipeMore"))), "还没量到视口宽度时不显示滑动提示");
    const viewport = all("View").find((node) => typeof node.props.onLayout === "function")!;
    await act(() => viewport.props.onLayout({ nativeEvent: { layout: { width: 249, height: 100, x: 0, y: 0 } } }));
    assert.ok(texts().includes(`← ${lookup("productReport.supplierTable.swipeMore")}`), "内容比视口宽时提示左滑");
    await act(() => all("ScrollView")[0].props.onScroll({ nativeEvent: { contentOffset: { x: 240, y: 0 } } }));
    assert.ok(texts().includes(`→ ${lookup("productReport.supplierTable.swipeBack")}`), "滑到最右后提示右滑回首屏");
    await act(() => viewport.props.onLayout({ nativeEvent: { layout: { width: 600, height: 100, x: 0, y: 0 } } }));
    assert.ok(!texts().some((text) => text.includes(lookup("productReport.supplierTable.swipeMore"))), "宽屏放得下全部列时不提示滑动");

    // 中国页签：编号固定显示、同期小字与图例、已选中行。
    await renderTable(true, 0);
    const chinaTexts = texts();
    assert.ok(chinaTexts.includes("SUP-1001") && chinaTexts.includes("SUP-1007"), "中国页签名称下固定显示供应商编号");
    assert.ok(chinaTexts.includes("$41,200") && chinaTexts.includes("5,890") && chinaTexts.includes("11.0%"), "中国页签每格下方有同期小字");
    assert.equal(
      chinaTexts.filter((text) => text === lookup("productReport.supplierTable.compareLegend")).length,
      2,
      "金额、均价两列表头标注「灰字＝同期」",
    );
    assert.ok(chinaTexts.includes(lookup("productReport.chinaGoods.compareShareOfChina")), "中国页签占比列副标题是「占中国货」");
    assert.ok(chinaTexts.includes(lookup("productReport.chinaGoods.compareShareOfTotal")), "中国页签总占比列副标题是「占总营业」");
    assert.equal(byLabel("Pressable", "open Sample Gift & Homewares Pty Ltd")!.props.accessibilityState.selected, true, "联动选中的行带选中态");
    assert.equal(chinaTexts.filter((text) => text === "成本待补全").length, 2, "成本待补全沿用文字提示");

    // ───────────── 供应商弹窗 ─────────────
    const supplier = {
      id: "s1",
      supplierCode: "SUP-1013",
      supplierName: "Sample Toys Wholesale Australia",
      revenue: 31470,
      compareRevenue: 22950,
      grossProfit: 9000,
      compareGrossProfit: 7000,
      grossMarginRate: 0.28,
      compareGrossMarginRate: 0.27,
      totalQuantity: 4120,
      compareTotalQuantity: 2988,
      averagePrice: 7.64,
      compareAveragePrice: 7.5,
      storeCount: 6,
      orderCount: 100,
      compareOrderCount: 80,
      averageTransaction: 10,
      compareAverageTransaction: 10,
      costStatus: "Complete" as const,
      compareCostStatus: "Complete" as const,
    };
    const sheetEvents: string[] = [];
    await render(
      React.createElement(SupplierDetailSheet, {
        supplier,
        kind: "australia",
        queryParams: {
          startDate: "2026-10-01",
          endDate: "2026-10-21",
          compareStartDate: "2025-10-01",
          compareEndDate: "2025-10-21",
          compareMode: "ByDate",
          branchCodes: ["BNK", "PAR"],
        },
        rangeLabel: "2026-10-01 ~ 2026-10-21",
        accountIdentity: "u1",
        scopeVersion: 1,
        summaryShareText: "营业额占比 8.5%",
        growthNewLabel: "新增",
        costPendingLabel: "成本待补全",
        costNoActivityLabel: "无交易活动",
        onClose: () => sheetEvents.push("close"),
        onLinkFilter: (code: string) => sheetEvents.push(`link:${code}`),
      }),
    );

    // ① 选择页：两个入口 + 数量；打开就预取商品明细与分店数据。
    let sheetTexts = texts();
    assert.ok(sheetTexts.includes("Sample Toys Wholesale Australia"), "标题是供应商名称");
    assert.ok(sheetTexts.some((text) => text.includes("SUP-1013") && text.includes("2026-10-01 ~ 2026-10-21") && text.includes("全部分店")), "副标题：编号 · 日期范围 · 全部分店");
    assert.ok(sheetTexts.includes("营业额占比 8.5%"), "选择页汇总显示占比");
    assert.ok(sheetTexts.includes("128 款") && sheetTexts.includes("2 家"), "两个入口带出商品款数与分店数");
    assert.ok(requestedKinds.includes("products") && requestedKinds.includes("branches"), "弹窗一打开就取商品明细与分店数据");
    assert.ok(!requestedKinds.includes("branch-products") && !requestedKinds.includes("product-branches"), "下钻层级没点开前不取数");

    // 保留「只联动筛选页面」
    await act(() => all("Pressable").find((node) => textOf(node).includes("联动筛选"))!.props.onPress());
    assert.deepEqual(sheetEvents, ["link:SUP-1013", "close"], "「只在页面里联动筛选」联动后关闭弹窗");

    // ② 商品明细 → 点商品 → 该商品各分店 → 返回
    await act(() => all("Pressable").find((node) => String(node.props.accessibilityLabel ?? "").startsWith(lookup("productReport.sheet.tabProducts")))!.props.onPress());
    sheetTexts = texts();
    assert.ok(sheetTexts.includes("商品明细 · 128") && sheetTexts.includes("分店数据 · 2"), "分段控件带计数");
    assert.ok(sheetTexts.includes("HB20301") && sheetTexts.includes("HB20302"), "商品卡列出货号");
    assert.ok(sheetTexts.includes("共 128 条 · 每页 100"), "底部显示总数与每页条数");
    assert.equal(sheetTexts.filter((text) => text === `${lookup("productReport.actions.branchDetail")} ›`).length, 2, "每张商品卡带「分店 ›」提示");
    await act(() => all("Pressable").find((node) => String(node.props.accessibilityLabel ?? "").startsWith("HB20301"))!.props.onPress());
    sheetTexts = texts();
    assert.ok(sheetTexts.includes("Sample Product 1"), "下钻后标题换成商品名称");
    assert.ok(requestedKinds.includes("product-branches"), "点商品后才取该商品的分店数据");
    assert.ok(sheetTexts.includes("Sample Bankstown") && sheetTexts.includes("Sample Liverpool"), "显示该商品在各分店的销售");
    assert.ok(!sheetTexts.some((text) => text.startsWith("商品明细 · ")), "下钻层级不再显示分段控件");
    await act(() => byLabel("IconButton", lookup("actions.back"))!.props.onPress());
    assert.ok(texts().includes("商品明细 · 128"), "返回回到商品明细");

    // ③ 分店数据 → 点分店 → 该分店商品明细 → 返回
    await act(() => all("Pressable").find((node) => node.props.accessibilityRole === "tab" && textOf(node).startsWith("分店数据"))!.props.onPress());
    sheetTexts = texts();
    assert.ok(sheetTexts.includes("Sample Bankstown") && sheetTexts.includes("Sample Parramatta"), "分店数据列出各分店");
    assert.equal(sheetTexts.filter((text) => text === `${lookup("productReport.sheet.rowDetailHint")} ›`).length, 2, "每个分店行带「明细 ›」提示");
    await act(() => byLabel("Pressable", "Sample Bankstown")!.props.onPress());
    sheetTexts = texts();
    assert.ok(requestedKinds.includes("branch-products"), "点分店后才取该分店的商品明细");
    assert.ok(sheetTexts.includes("HB20303"), "显示该供应商在该分店卖的商品");
    assert.ok(sheetTexts.includes("共 46 条 · 每页 100"), "分店商品明细带自己的总数");
    assert.ok(sheetTexts.includes(lookup("productReport.linked.branchLabel")) && sheetTexts.includes(lookup("productReport.linked.supplierLabel")), "范围标签：分店 + 供应商");
    // 分店 → 商品 → 该商品各分店：返回回到刚才的分店商品列表（不是跳回供应商商品明细）。
    await act(() => all("Pressable").find((node) => String(node.props.accessibilityLabel ?? "").startsWith("HB20303"))!.props.onPress());
    assert.ok(texts().includes("Sample Product 3") && texts().includes("Sample Liverpool"), "从分店商品点商品，继续看该商品的各分店销售");
    await act(() => byLabel("IconButton", lookup("actions.back"))!.props.onPress());
    assert.ok(texts().includes("共 46 条 · 每页 100") && texts().includes("HB20303"), "返回回到该分店的商品明细，而不是供应商的商品明细");
    await act(() => byLabel("IconButton", lookup("actions.back"))!.props.onPress());
    assert.ok(texts().includes("分店数据 · 2") && texts().includes("Sample Parramatta"), "再返回回到分店数据");

    // 关闭
    await act(() => byLabel("IconButton", lookup("actions.close"))!.props.onPress());
    assert.equal(sheetEvents.at(-1), "close");
  } finally {
    await act(() => root.render(React.createElement(React.Fragment)));
  }
}

run().then(
  () => console.log("supplier-report-render.test.ts: ok"),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
