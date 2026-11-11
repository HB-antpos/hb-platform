import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import path from "node:path";
import React, { act } from "react";

// 审核列表新部件（分段控件、请求卡片、分店清单弹层）的真实渲染测试：
// 用真实的中英文案文件做翻译，既校验界面文字，也校验 list / wait / groups / handled 文案键没有漏。
type Node = { type: string; props: Record<string, any>; children: (Node | string)[] };

function textOf(node: Node | string): string {
  if (typeof node === "string") return node;
  return node.children.map(textOf).join("");
}

type Locale = Record<string, any>;

/** 极简 i18next：按点路径取值，count 时回退 _one / _other，{{x}} 插值；键不存在直接断言失败。 */
function makeT(locale: Locale) {
  return (key: string, options: Record<string, unknown> = {}) => {
    const lookup = (k: string) => k.split(".").reduce<any>((node, part) => node?.[part], locale);
    let value = lookup(key);
    if (value === undefined && typeof options.count === "number") {
      value = lookup(`${key}_${options.count === 1 ? "one" : "other"}`);
    }
    assert.equal(typeof value, "string", `缺少文案键 ${key}`);
    return (value as string).replace(/\{\{(\w+)\}\}/g, (_, name) => String(options[name]));
  };
}

async function run() {
  Object.assign(globalThis, { __DEV__: false, IS_REACT_ACT_ENVIRONMENT: true, React });
  const mockModule = (filename: string, exports: object) => {
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
  mockModule(require.resolve("react-native"), {
    StyleSheet: { create: (styles: object) => styles, hairlineWidth: 1 },
    View: host("View"),
    Pressable: host("Pressable"),
  });
  mockModule(require.resolve("react-native-paper"), {
    Text: host("Text"),
    Icon: host("Icon"),
    Button: host("Button"),
    Card: Object.assign(host("Card"), { Content: host("CardContent") }),
  });
  // BusinessSheet 依赖弹窗 / 键盘 / 安全区原生模块，这里只关心传给它的 props 与子内容。
  const sheetFile = path.resolve(__dirname, "../../components/ui/BusinessSheet.tsx");
  mockModule(sheetFile, { BusinessSheet: host("BusinessSheet") });

  const { createRoot } = await import("test-renderer");
  const { ReviewRequestCard, ReviewSegmentControl, ReviewStoreSheet } = await import("./review-list-parts");

  const zh = JSON.parse(readFileSync(path.resolve(__dirname, "../../locales/zh/screens/employeeProfileReview.json"), "utf8"));
  const en = JSON.parse(readFileSync(path.resolve(__dirname, "../../locales/en/screens/employeeProfileReview.json"), "utf8"));
  const tZh = makeT(zh);
  const tEn = makeT(en);

  const render = async (element: React.ReactElement) => {
    const root = createRoot();
    await act(async () => { root.render(element); });
    const container = root.container as unknown as { queryAll: (predicate: (node: Node) => boolean) => Node[] };
    const all = (type: string) => container.queryAll((node) => node.type === type);
    return { all, texts: () => all("Text").map(textOf), buttons: () => all("Button").map(textOf), root };
  };

  const daysAgo = (days: number, hour = 12) => {
    const date = new Date();
    date.setDate(date.getDate() - days);
    date.setHours(hour, 4, 0, 0);
    return date.toISOString();
  };
  const base = {
    requestId: 1,
    userGuid: "g-1",
    username: "admin",
    status: "Pending" as const,
    baseSensitiveRevision: 1,
    submittedAt: daysAgo(118),
    changedFields: ["bankBsb", "bankAccountNumber", "superannuationCompanyName", "superannuationCompanyCode", "superannuationAccountNumber", "identityId"],
    storeCodes: [],
    storeNames: Array.from({ length: 41 }, (_, index) => (index === 0 ? "Tuggerah OutSide Closed" : `Store ${index}`)),
  };

  // ---------- 分段控件 ----------
  {
    const changed: string[] = [];
    const view = await render(React.createElement(ReviewSegmentControl, { value: "pending", onChange: (v: string) => changed.push(v), t: tZh, pendingCount: 5 }));
    assert.deepEqual(view.texts(), ["待审核", "5", "已处理"], "待审核页签带数量，已处理不带");
    const tabs = view.all("Pressable");
    assert.equal(tabs.length, 2);
    assert.equal(tabs[0].props.accessibilityState.selected, true);
    assert.equal(tabs[1].props.accessibilityState.selected, false);
    await act(async () => { tabs[1].props.onPress(); });
    assert.deepEqual(changed, ["processed"]);

    // 搜索中（pendingCount 为 null）或位于已处理页签：不显示数量。
    const searching = await render(React.createElement(ReviewSegmentControl, { value: "pending", onChange: () => {}, t: tZh, pendingCount: null }));
    assert.deepEqual(searching.texts(), ["待审核", "已处理"]);
    const processed = await render(React.createElement(ReviewSegmentControl, { value: "processed", onChange: () => {}, t: tZh, pendingCount: 5 }));
    assert.deepEqual(processed.texts(), ["待审核", "已处理"]);
  }

  // ---------- 待审核卡片（admin：41 家店、等了 118 天、银行 / 养老金 / 证件） ----------
  {
    const opened: number[] = [];
    const stores: number[] = [];
    const view = await render(React.createElement(ReviewRequestCard, {
      item: base as never, language: "zh", t: tZh,
      onOpen: (item: { requestId: number }) => opened.push(item.requestId),
      onOpenStores: (item: { requestId: number }) => stores.push(item.requestId),
    }));
    const texts = view.texts();
    assert.ok(texts.includes("A"), "头像字母");
    assert.ok(texts.some((text) => text === "admin"), "旧后端没有姓名 → 只显示用户名");
    assert.ok(texts.includes("Tuggerah OutSide Closed"), "只写第一家店");
    assert.ok(!texts.includes("Store 1"), "其余分店不出现在卡片上");
    assert.ok(texts.includes("等 41 家 ›"), "分店入口");
    assert.ok(texts.includes("已等 118 天"), "等待时长");
    assert.ok(!texts.includes("待审核"), "待审核分段里不再给每张卡挂「待审核」状态胶囊");
    assert.ok(["银行账户", "养老金", "证件"].every((label) => texts.includes(label)), "字段按类合并成 3 个胶囊");
    assert.deepEqual(texts.filter((text) => /^[0-9]$/.test(text)), ["2", "3", "1"], "每类带改了几项");
    assert.deepEqual(view.buttons(), ["审核 ›"], "操作按钮");
    assert.ok(!texts.some((text) => text.includes("BSB") || text.includes("银行账号")), "9 个原始字段名不再逐个列出");

    const card = view.all("Card")[0];
    const cardStyle = ([] as Record<string, unknown>[]).concat(card.props.style).filter(Boolean);
    assert.ok(cardStyle.some((style) => style.borderLeftWidth === 4), "等了 8 天以上 → 左侧红条");
    assert.equal(card.props.accessibilityLabel, "审核 admin 的申请");

    const [moreButton] = view.all("Pressable");
    assert.equal(moreButton.props.accessibilityLabel, "查看全部 41 家分店");
    await act(async () => { moreButton.props.onPress(); });
    assert.deepEqual(stores, [1], "点「等 41 家」只打开分店清单");
    assert.deepEqual(opened, []);
    await act(async () => { card.props.onPress(); });
    assert.deepEqual(opened, [1], "点卡片进入详情");
  }

  // ---------- 待审核卡片：有姓名、生日变更、昨天提交、2 家店 ----------
  {
    const item = { ...base, requestId: 2, username: "alice.w", displayName: "Alice Wong", submittedAt: daysAgo(1), changedFields: ["birthday", "identityType", "identityId"], storeNames: ["Toukley", "Forster"] };
    const view = await render(React.createElement(ReviewRequestCard, { item: item as never, language: "zh", t: tZh, onOpen: () => {}, onOpenStores: () => {} }));
    const texts = view.texts();
    assert.ok(texts.includes("Alice Wong  alice.w"), "姓名为主、用户名为辅（同一行，读屏也按整行读）");
    assert.ok(texts.includes("  alice.w"), "用户名是单独的次要样式文本");
    assert.ok(texts.includes("Toukley · Forster"), "1～2 家全写");
    assert.ok(!texts.some((text) => text.startsWith("等 ")), "不显示「等 N 家」入口");
    assert.ok(texts.includes("昨天"));
    assert.ok(texts.includes("生日 · 影响年龄"), "待审核的生日变更标「影响年龄」");
    assert.ok(texts.includes("证件") && texts.includes("2"));
    const style = ([] as Record<string, unknown>[]).concat(view.all("Card")[0].props.style).filter(Boolean);
    assert.ok(!style.some((entry) => entry.borderLeftWidth === 4), "昨天提交不加红条");
  }

  // ---------- 已处理卡片：状态胶囊 + 处理日期，没有等待时长 ----------
  {
    const reviewed = new Date(2026, 10, 10, 9, 0).toISOString();
    const item = { ...base, requestId: 3, status: "Approved" as const, reviewedAt: reviewed, changedFields: ["birthday"], storeNames: ["Charlestown Square"] };
    const view = await render(React.createElement(ReviewRequestCard, { item: item as never, language: "zh", t: tZh, onOpen: () => {}, onOpenStores: () => {} }));
    const texts = view.texts();
    assert.ok(texts.includes("已批准"));
    assert.ok(texts.includes("11月10日 处理"));
    assert.ok(texts.includes("生日"), "已处理里生日不再标「影响年龄」");
    assert.ok(!texts.includes("生日 · 影响年龄"));
    assert.deepEqual(view.buttons(), ["查看 ›"]);
    assert.ok(!texts.some((text) => text.startsWith("已等")), "已处理不显示等待时长");
    const style = ([] as Record<string, unknown>[]).concat(view.all("Card")[0].props.style).filter(Boolean);
    assert.ok(!style.some((entry) => entry.borderLeftWidth === 4), "已处理不加红条");

    for (const [status, expected] of [["Rejected", "已拒绝"], ["Withdrawn", "已撤回"], ["Superseded", "已失效"]] as const) {
      const other = await render(React.createElement(ReviewRequestCard, { item: { ...item, status } as never, language: "zh", t: tZh, onOpen: () => {}, onOpenStores: () => {} }));
      assert.ok(other.texts().includes(expected), `${status} → ${expected}`);
    }
  }

  // ---------- 英文界面 ----------
  {
    const view = await render(React.createElement(ReviewRequestCard, { item: base as never, language: "en", t: tEn, onOpen: () => {}, onOpenStores: () => {} }));
    const texts = view.texts();
    assert.ok(texts.includes("+40 more ›"), "英文入口显示还剩几家");
    assert.ok(texts.includes("118 days"));
    assert.deepEqual(view.buttons(), ["Review ›"]);
    assert.ok(["Bank", "Super", "ID"].every((label) => texts.includes(label)));
    assert.ok(texts.some((text) => /^\d{1,2} [A-Z][a-z]{2}, \d{2}:\d{2}$/.test(text)), "提交时间 16 Jul, 12:04 格式");

    const yesterday = await render(React.createElement(ReviewRequestCard, { item: { ...base, submittedAt: daysAgo(1) } as never, language: "en", t: tEn, onOpen: () => {}, onOpenStores: () => {} }));
    assert.ok(yesterday.texts().includes("Yesterday"));
    const rejected = await render(React.createElement(ReviewRequestCard, { item: { ...base, status: "Rejected", reviewedAt: new Date(2026, 10, 4).toISOString() } as never, language: "en", t: tEn, onOpen: () => {}, onOpenStores: () => {} }));
    assert.ok(rejected.texts().includes("Processed 4 Nov"));
  }

  // ---------- 分店清单弹层 ----------
  {
    let dismissed = 0;
    const view = await render(React.createElement(ReviewStoreSheet, { target: { name: "admin", storeNames: ["Tuggerah", "Warners Bay", "Toukley"] }, onDismiss: () => { dismissed += 1; }, t: tZh }));
    const sheet = view.all("BusinessSheet")[0];
    assert.equal(sheet.props.visible, true);
    assert.equal(sheet.props.title, "admin 所在分店");
    assert.equal(sheet.props.subtitle, "共 3 家");
    assert.deepEqual(view.texts(), ["Tuggerah", "Warners Bay", "Toukley"]);
    sheet.props.onDismiss();
    assert.equal(dismissed, 1);

    const closed = await render(React.createElement(ReviewStoreSheet, { target: null, onDismiss: () => {}, t: tZh }));
    assert.equal(closed.all("BusinessSheet")[0].props.visible, false);
  }

  console.log("review-list-parts render tests passed");
}

void run().catch((error) => {
  console.error(error);
  process.exit(1);
});
