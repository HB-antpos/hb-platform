import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import Module from "node:module";
import { resolve } from "node:path";
import React, { act } from "react";

// 商品查询页底部固定打印栏（LabelPrintCard）的真实渲染测试：
// 【状态图标】【普通】【小标签】【折扣】【大标签】——四个打印按钮全部留在页面上（Zebra 小屏一行），
// 小标签紧跟在普通后面，大标签不收进「更多」。i18n 桩直接读真实语言包，缺键即抛错。
function loadLocale(language: "zh" | "en") {
  return JSON.parse(
    readFileSync(resolve(__dirname, `../../locales/${language}/screens/productQuery.json`), "utf8"),
  ) as Record<string, unknown>;
}

const locales = { zh: loadLocale("zh"), en: loadLocale("en") };

function lookupIn(language: "zh" | "en", key: string): string {
  const value = key.split(".").reduce<unknown>(
    (node, part) => (node && typeof node === "object" ? (node as Record<string, unknown>)[part] : undefined),
    locales[language],
  );
  if (typeof value !== "string") throw new Error(`${language} 语言包缺少键：${key}`);
  return value;
}

const BUTTON_KEYS = ["print.productShort", "print.smallShort", "print.discountShort", "print.bigDiscountShort"] as const;

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
  });
  mockModule("react-native-paper", {
    Button: host("Button"),
    Card: host("Card"),
    Text: host("Text"),
  });
  mockModule(require.resolve("../../shared/i18n/use-app-translation"), {
    useAppTranslation: () => ({ t: (key: string) => lookupIn("zh", key) }),
  });

  const { createRoot } = await import("test-renderer");
  const { LabelPrintCard } = await import("./LabelPrintCard");

  const root = createRoot();
  const nodes = (type: string) => root.container.queryAll((node) => node.type === type);
  const buttons = () => nodes("Button");
  const labels = () => buttons().map((node) => String(node.props.children));
  const render = (props: Record<string, unknown>) =>
    act(() => root.render(React.createElement(LabelPrintCard, props as never)));
  const presses: string[] = [];
  const allHandlers = {
    onPrintProduct: () => presses.push("product"),
    onPrintSmall: () => presses.push("small"),
    onPrintDiscount: () => presses.push("discount"),
    onPrintBigDiscount: () => presses.push("bigDiscount"),
    canPrintDiscount: true,
    canPrintBigDiscount: true,
  };

  try {
    // 1. 四个按钮的顺序：普通 → 小标签（紧跟普通）→ 折扣 → 大标签；没有「更多」按钮 / 菜单。
    await render({ ...allHandlers, leading: React.createElement("PrinterChip") });
    assert.deepEqual(labels(), BUTTON_KEYS.map((key) => lookupIn("zh", key)), "按钮顺序应为 普通 / 小标签 / 折扣 / 大标签");
    assert.equal(nodes("Menu").length, 0, "大标签直接放在页面上，不收进「更多」菜单");
    assert.ok(!labels().includes(lookupIn("zh", "search.more")), "打印栏里没有「更多」按钮");

    // 2. 状态图标（leading）在所有按钮前面。
    const order = root.container.queryAll((node) => node.type === "PrinterChip" || node.type === "Button").map((node) => node.type);
    assert.deepEqual(order, ["PrinterChip", "Button", "Button", "Button", "Button"]);

    // 3. 每个按钮触发各自的回调，小标签不会误触发普通标签。
    for (const button of buttons()) {
      await act(() => button.props.onPress());
    }
    assert.deepEqual(presses, ["product", "small", "discount", "bigDiscount"]);

    // 4. 折扣 / 大标签只在商品有折扣率时可用；普通与小标签始终可用。
    await render({ ...allHandlers, canPrintDiscount: false, canPrintBigDiscount: false });
    assert.deepEqual(buttons().map((node) => node.props.disabled), [false, false, true, true]);

    // 5. 另一个打印进行中（回调被页面置空）：全部禁用；小标签自己在打印时显示加载并禁用。
    await render({});
    assert.ok(buttons().every((node) => node.props.disabled === true), "没有回调时四个按钮都禁用");
    await render({ ...allHandlers, isPrintingSmall: true });
    assert.equal(buttons()[1]!.props.loading, true);
    assert.equal(buttons()[1]!.props.disabled, true);
    assert.equal(buttons()[0]!.props.loading, false);

    // 6. 小屏字数上限：中文 ≤ 4 字、英文 ≤ 5 字符（简写）。真机 TC26 字体缩放 1.15 倍，
    //    实测 13sp 的 “Discount” 约 60dp，而四个按钮每个只有约 64dp（含标签边距几乎为零余量），
    //    所以英文用 Reg / Small / Disc / Large（最长约 38dp）。
    for (const key of BUTTON_KEYS) {
      assert.ok([...lookupIn("zh", key)].length <= 4, `中文按钮「${lookupIn("zh", key)}」超过 4 个字`);
      assert.ok(lookupIn("en", key).length <= 5, `英文按钮「${lookupIn("en", key)}」超过 5 个字符，真机会被截断`);
    }
    assert.deepEqual(
      BUTTON_KEYS.map((key) => lookupIn("en", key)),
      ["Reg", "Small", "Disc", "Large"],
      "英文按钮文案是 Reg / Small / Disc / Large",
    );

    // 6b. 简写只是视觉上的：读屏朗读完整名称（普通标签 / 小标签 / 折扣标签 / 大标签）。
    await render({ ...allHandlers });
    assert.deepEqual(
      buttons().map((node) => node.props.accessibilityLabel),
      ["print.product", "print.smallLabel", "print.discount", "print.bigDiscount"].map((key) => lookupIn("zh", key)),
    );
    assert.deepEqual(
      ["print.product", "print.smallLabel", "print.discount", "print.bigDiscount"].map((key) => lookupIn("en", key)),
      ["Regular label", "Small label (60mm)", "Discount label", "Large label"],
      "英文完整名称",
    );

    // 7. 页面接线契约：小标签动作存在、固定打小标签、并传给打印栏（页面太大没法整体渲染，按源码钉住）。
    const page = readFileSync(resolve(__dirname, "../../../app/(shell)/product-query.tsx"), "utf8");
    assert.match(page, /type PrintAction =[\s\S]{0,80}"small"/u, "打印动作类型包含 small");
    assert.match(
      page,
      /kind === "small"[\s\S]{0,400}printType:\s*"small"/u,
      "小标签按钮固定按 small 打印，不看打印设置里的小标签开关",
    );
    assert.match(page, /onPrintSmall=\{/u, "页面把小标签回调传给打印栏");
    assert.match(page, /isPrintingSmall=\{printingAction === "small"\}/u);
  } finally {
    await act(() => root.unmount());
  }
}

run().then(() => {
  console.log("label-print-card tests passed");
}).catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
