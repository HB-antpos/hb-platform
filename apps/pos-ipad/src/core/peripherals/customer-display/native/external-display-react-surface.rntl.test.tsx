import { afterEach, beforeEach, expect, test } from "@jest/globals";
import { act, fireEvent, render, within } from "@testing-library/react-native";
import { createInstance } from "i18next";
import {
  I18nextProvider,
  initReactI18next,
} from "react-i18next";
import { Dimensions, StyleSheet } from "react-native";

import {
  ExternalDisplaySurface,
  fitCustomerDisplayFontSize,
  registerExternalDisplayReactSurface,
  resolveCustomerDisplayLayout,
} from "./external-display-react-surface";

import type { CustomerDisplaySnapshot } from "@/core/contracts";
import type { ExternalDisplayNativeModule } from "@/core/peripherals/customer-display/native/external-display-bridge";
import en from "@/i18n/locales/en.json";
import zh from "@/i18n/locales/zh.json";

const advert: NonNullable<CustomerDisplaySnapshot["advert"]> = {
  kind: "image",
  localUri: "file:///customer-display/adverts/welcome.png",
};

afterEach(() => {
  registerExternalDisplayReactSurface(null);
});

const SCREEN_1080P = { width: 1_920, height: 1_080 } as const;

beforeEach(() => {
  setWindow(SCREEN_1080P.width, SCREEN_1080P.height);
});

test("布局公式：宽屏 60% 购物车、广告面板与 Swift 同公式", () => {
  const layout = resolveCustomerDisplayLayout(SCREEN_1080P);
  const s = 1_080 / 768;

  expect(layout.scale).toBeCloseTo(s, 10);
  // 1920×1080 的等效画布宽 1365.33，几乎等于 1366，因此占比约 0.6。
  expect(layout.cartShare).toBeCloseTo(0.6, 3);
  expect(layout.margin).toBeCloseTo(18 * s, 10);
  expect(layout.summaryHeight).toBeCloseTo(152 * s, 10);
  expect(layout.contentWidth).toBeCloseTo(1_920 - 36 * s, 10);
  expect(layout.contentHeight).toBeCloseTo(1_080 - 36 * s - 152 * s - 20 * s, 10);
  expect(layout.cartPanel.width).toBeCloseTo(layout.contentWidth * layout.cartShare, 10);
  expect(layout.advertPanel.width).toBeCloseTo(
    layout.contentWidth * (1 - layout.cartShare) - 18 * s,
    10,
  );
  // 购物车与广告之间正好留 18s；两者同高同顶。
  expect(layout.advertPanel.x).toBeCloseTo(
    layout.cartPanel.x + layout.cartPanel.width + 18 * s,
    10,
  );
  expect(layout.advertPanel.y).toBeCloseTo(layout.cartPanel.y, 10);
  expect(layout.advertPanel.height).toBeCloseTo(layout.cartPanel.height, 10);
  // 不做宽屏留白：广告面板右缘距屏幕右边恰为外边距。
  expect(layout.advertPanel.x + layout.advertPanel.width).toBeCloseTo(
    1_920 - 18 * s,
    10,
  );
  // 汇总区在内容区下方 20s，底边距屏幕底部为外边距。
  expect(layout.summaryPanel.y).toBeCloseTo(
    layout.cartPanel.y + layout.contentHeight + 20 * s,
    10,
  );
  expect(layout.summaryPanel.y + layout.summaryPanel.height).toBeCloseTo(
    1_080 - 18 * s,
    10,
  );
});

test("布局公式：4:3 窄屏购物车占比 68%，超宽屏仍按 60%", () => {
  const narrow = resolveCustomerDisplayLayout({ width: 1_024, height: 768 });
  expect(narrow.scale).toBe(1);
  expect(narrow.canvasWidth).toBe(1_024);
  expect(narrow.cartShare).toBeCloseTo(0.68, 10);
  expect(narrow.advertPanel.width).toBeCloseTo((1_024 - 36) * 0.32 - 18, 10);

  const ultraWide = resolveCustomerDisplayLayout({ width: 3_440, height: 1_440 });
  expect(ultraWide.canvasWidth).toBe(1_366);
  expect(ultraWide.cartShare).toBeCloseTo(0.6, 10);

  const mid = resolveCustomerDisplayLayout({ width: 1_229, height: 768 });
  expect(mid.cartShare).toBeGreaterThan(0.6);
  expect(mid.cartShare).toBeLessThan(0.68);

  // 异常尺寸退回 WPF 默认画布，不产生 NaN。
  const fallback = resolveCustomerDisplayLayout({ width: 0, height: 0 });
  expect(fallback.scale).toBe(1);
  expect(Number.isFinite(fallback.advertPanel.width)).toBe(true);
});

test("金额缩放：只缩小不放大，超宽或超高时等比缩小", () => {
  expect(
    fitCustomerDisplayFontSize({
      text: "$1.00",
      fontSize: 28,
      maxWidth: 400,
      maxHeight: 100,
    }),
  ).toBe(28);
  const shrunkByWidth = fitCustomerDisplayFontSize({
    text: "$123456.78",
    fontSize: 62,
    maxWidth: 200,
    maxHeight: 1_000,
  });
  expect(shrunkByWidth).toBeLessThan(62);
  const shrunkByHeight = fitCustomerDisplayFontSize({
    text: "$1",
    fontSize: 62,
    maxWidth: 1_000,
    maxHeight: 40,
  });
  expect(shrunkByHeight).toBeCloseTo(40 / 1.2, 5);
});

test("外接客显固定使用英文且整个 surface 永远不接收触摸", async () => {
  const chinese = await createTestI18n("zh");
  const screen = await render(
    <I18nextProvider i18n={chinese}>
      <ExternalDisplaySurface
        snapshot={snapshot("change")}
        surfaceId="external-1"
      />
    </I18nextProvider>,
  );

  expect(screen.getByTestId("external-display-surface").props.pointerEvents).toBe(
    "none",
  );
  expect(screen.getByTestId("external-display-surface").props.accessible).toBe(
    false,
  );
  // 标题栏与 "Your order" 标题行已删除。
  expect(screen.queryByText("Customer Display")).toBeNull();
  expect(screen.queryByText("Your order")).toBeNull();
  expect(screen.getByText("Your change")).toBeTruthy();
  expect(screen.getByText("Item Quantity")).toBeTruthy();
  expect(screen.getByText("SKU Count")).toBeTruthy();
  expect(screen.queryByText("客显")).toBeNull();
  expect(screen.queryByText("您的订单")).toBeNull();
  expect(screen.queryByText("找零")).toBeNull();
  expect(screen.getByText("Tea")).toBeTruthy();
  expect(screen.queryAllByRole("button")).toHaveLength(0);
});

test("表头为 WPF 四列英文文案，空购物车只显示列头不显示空提示", async () => {
  const screen = await renderSurface(
    snapshot("cart", { items: [] }),
  );

  expect(screen.getByText("Item Description")).toBeTruthy();
  expect(screen.getByText("Qty")).toBeTruthy();
  expect(screen.getByText("Price")).toBeTruthy();
  expect(screen.getByText("Total")).toBeTruthy();
  expect(screen.queryByText("Your basket is empty")).toBeNull();
  expect(screen.queryAllByTestId("external-display-item-row")).toHaveLength(0);
  expect(screen.queryByTestId("external-display-title-bar")).toBeNull();
  expect(screen.queryByTestId("external-display-order-heading")).toBeNull();
});

test("面板几何按屏幕尺度绝对定位，广告透明窗口等于规格矩形", async () => {
  const layout = resolveCustomerDisplayLayout(SCREEN_1080P);
  const screen = await renderSurface(designedSnapshot("cart"));
  const s = layout.scale;

  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-advert-window").props.style,
    ),
  ).toMatchObject({
    position: "absolute",
    left: layout.advertPanel.x,
    top: layout.advertPanel.y,
    width: layout.advertPanel.width,
    height: layout.advertPanel.height,
    backgroundColor: "transparent",
    borderRadius: 18 * s,
  });
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-transaction-panel").props.style,
    ),
  ).toMatchObject({
    position: "absolute",
    left: layout.cartPanel.x,
    top: layout.cartPanel.y,
    width: layout.cartPanel.width,
    height: layout.cartPanel.height,
    backgroundColor: "#09111F",
    borderRadius: 12 * s,
    padding: 20 * s,
  });
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-summary-panel").props.style,
    ),
  ).toMatchObject({
    position: "absolute",
    left: layout.summaryPanel.x,
    top: layout.summaryPanel.y,
    width: layout.summaryPanel.width,
    height: 152 * s,
    backgroundColor: "#101B2D",
    paddingHorizontal: 24 * s,
    paddingVertical: 16 * s,
  });
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-amount-due").props.style,
    ),
  ).toMatchObject({ width: 220 * s });
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-status-region").props.style,
    ),
  ).toMatchObject({ width: 220 * s });
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-status-card").props.style,
    ),
  ).toMatchObject({
    backgroundColor: "rgba(105,227,194,0.12)",
    borderColor: "#69E3C2",
    borderRadius: 12 * s,
    padding: 16 * s,
  });
});

test("窗口尺寸变化后样式随尺度重算", async () => {
  setWindow(1_024, 768);
  const layout = resolveCustomerDisplayLayout({ width: 1_024, height: 768 });
  const screen = await renderSurface(designedSnapshot("cart"));

  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-advert-window").props.style,
    ),
  ).toMatchObject({
    left: layout.advertPanel.x,
    width: layout.advertPanel.width,
  });
  const row = screen.getAllByTestId("external-display-item-row")[0]!;
  expect(StyleSheet.flatten(row.props.style)).toMatchObject({ height: 72 });
});

test.each([
  ["idle", "Ready for Payment", "Insert or tap card"],
  ["cart", "Ready for Payment", "Insert or tap card"],
  ["payment", "Ready for Payment", "Insert or tap card"],
  ["change", "Your change", "$5.00"],
  ["success", "Payment complete", "Change $5.00"],
] as const)(
  "%s 状态卡文案符合 WPF 或保留 iPad 找零/成功文案",
  async (mode, title, subtitle) => {
    const screen = await renderSurface(
      designedSnapshot(mode, { change: { currency: "AUD", cents: 500 } }),
    );

    expect(screen.getByTestId("external-display-status-card")).toBeTruthy();
    expect(screen.getByText(title)).toBeTruthy();
    expect(screen.getByText(subtitle)).toBeTruthy();
  },
);

test("无快照时汇总区显示零值与 Ready for Payment", async () => {
  const screen = await renderSurface(null);

  expect(screen.getByText("Ready for Payment")).toBeTruthy();
  expect(screen.getAllByText("$0.00").length).toBeGreaterThanOrEqual(3);
  expect(screen.queryByTestId("external-display-savings")).toBeNull();
});

test("汇总区：Subtotal / GST / Total To Pay，Savings 非零时带减号显示", async () => {
  const screen = await renderSurface(designedSnapshot("cart"));

  expect(screen.getByText("Subtotal")).toBeTruthy();
  expect(screen.getByText("$13.34")).toBeTruthy();
  expect(screen.getByText("GST")).toBeTruthy();
  expect(screen.getByText("$1.12")).toBeTruthy();
  expect(screen.getByText("Savings")).toBeTruthy();
  expect(screen.getByText("-$1.00")).toBeTruthy();
  expect(screen.getByText("Total To Pay")).toBeTruthy();
  expect(
    within(screen.getByTestId("external-display-amount-due")).getByText("$12.34"),
  ).toBeTruthy();
  expect(screen.getByText("Item Quantity")).toBeTruthy();
  expect(screen.getByText("SKU Count")).toBeTruthy();
});

test("Savings 为 0 时整栏隐藏，不显示 $0.00 优惠", async () => {
  const screen = await renderSurface(
    designedSnapshot("cart", { discount: { currency: "AUD", cents: 0 } }),
  );

  expect(screen.queryByText("Savings")).toBeNull();
  expect(screen.queryByTestId("external-display-savings")).toBeNull();
  expect(screen.queryByText("$0.00")).toBeNull();
});

test("商品行：货号、查询码、折扣率、原价删除线与强调色实收", async () => {
  const screen = await renderSurface(
    snapshot("cart", {
      items: [
        {
          ...item("Green Tea"),
          unitPrice: { currency: "AUD", cents: 1_000 },
          itemNumber: "HB-1001",
          lookupCode: "9300000000017",
          grossAmount: { currency: "AUD", cents: 2_000 },
          discountRate: "10",
          amount: { currency: "AUD", cents: 1_800 },
        },
      ],
    }),
  );

  expect(screen.getByText("Green Tea")).toBeTruthy();
  expect(screen.getByText("Item No. HB-1001")).toBeTruthy();
  expect(screen.getByText("9300000000017")).toBeTruthy();
  expect(screen.getByText("-10%")).toBeTruthy();
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-item-discount-rate").props.style,
    ),
  ).toMatchObject({ color: "#69E3C2", fontWeight: "700" });
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-item-gross-amount").props.style,
    ),
  ).toMatchObject({ textDecorationLine: "line-through" });
  expect(screen.getByText("$20.00")).toBeTruthy();
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-item-amount").props.style,
    ),
  ).toMatchObject({ color: "#69E3C2", fontWeight: "700" });
  // 数量胶囊内容是数量本身，不再带 "×"。
  expect(screen.queryByText("× 2")).toBeNull();
  expect(screen.getAllByText("2").length).toBeGreaterThan(0);
});

test("旧快照缺少新增字段时正常渲染：无货号行、无折扣、实收为白色", async () => {
  const screen = await renderSurface(snapshot("cart"));

  expect(screen.getByText("Tea")).toBeTruthy();
  expect(screen.queryByTestId("external-display-item-number")).toBeNull();
  expect(screen.queryByTestId("external-display-item-lookup-code")).toBeNull();
  expect(screen.queryByTestId("external-display-item-discount-rate")).toBeNull();
  expect(screen.queryByTestId("external-display-item-gross-amount")).toBeNull();
  expect(screen.queryByTestId("external-display-item-thumb-image")).toBeNull();
  expect(screen.getByTestId("external-display-item-thumb-icon")).toBeTruthy();
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-item-amount").props.style,
    ),
  ).toMatchObject({ color: "#FFFFFF" });
});

test("货号与查询码只有其一时不渲染竖分隔线", async () => {
  const onlyNumber = await renderSurface(
    snapshot("cart", { items: [{ ...item("A"), itemNumber: "N1" }] }),
  );
  expect(onlyNumber.getByText("Item No. N1")).toBeTruthy();
  expect(onlyNumber.queryByTestId("external-display-item-lookup-code")).toBeNull();
  onlyNumber.unmount();

  const onlyCode = await renderSurface(
    snapshot("cart", { items: [{ ...item("B"), lookupCode: "C2" }] }),
  );
  expect(onlyCode.getByText("C2")).toBeTruthy();
  expect(onlyCode.queryByTestId("external-display-item-number")).toBeNull();
});

test("缩略图加载本地 file URI，失败后回退为购物袋图标", async () => {
  const uri = "file:///product-images/p1.jpg";
  const screen = await renderSurface(
    snapshot("cart", { items: [{ ...item("Pic"), imageUri: uri }] }),
  );

  const image = screen.getByTestId("external-display-item-thumb-image");
  expect(image.props.source).toEqual({ uri });
  expect(image.props.resizeMode).toBe("contain");
  expect(screen.getByTestId("external-display-item-thumb-icon")).toBeTruthy();

  await fireEvent(image, "error");

  expect(screen.queryByTestId("external-display-item-thumb-image")).toBeNull();
  expect(screen.getByTestId("external-display-item-thumb-icon")).toBeTruthy();
});

test("奇数序行使用表面色交替背景，行高与分隔线按尺度", async () => {
  const items = Array.from({ length: 3 }, (_, index) => item(`Item ${index + 1}`));
  const screen = await renderSurface(snapshot("cart", { items }));
  const s = 1_080 / 768;

  const rows = screen
    .getAllByTestId("external-display-item-row")
    .map((row) => StyleSheet.flatten(row.props.style));
  expect(rows[0]).toMatchObject({ backgroundColor: "#09111F", height: 72 * s });
  expect(rows[1]).toMatchObject({ backgroundColor: "#101B2D" });
  expect(rows[2]).toMatchObject({ backgroundColor: "#09111F" });
  for (const style of rows) {
    expect(style).toMatchObject({
      flexGrow: 0,
      flexShrink: 0,
      borderBottomColor: "rgba(255,255,255,0.12)",
    });
    expect(style).not.toHaveProperty("flex");
  }
});

test("idle 空购物篮且有广告时 RN surface 全屏透明且不渲染交易面板", async () => {
  const screen = await renderSurface(
    snapshot("idle", { advert, items: [] }),
  );
  const surfaceStyle = StyleSheet.flatten(
    screen.getByTestId("external-display-surface").props.style,
  );
  const advertWindowStyle = StyleSheet.flatten(
    screen.getByTestId("external-display-advert-window").props.style,
  );

  expect(
    screen.queryByTestId("external-display-transaction-panel"),
  ).toBeNull();
  expect(
    screen.queryByTestId("external-display-summary-panel"),
  ).toBeNull();
  expect(surfaceStyle).toMatchObject({
    backgroundColor: "transparent",
    flex: 1,
  });
  expect(surfaceStyle).not.toHaveProperty("gap");
  expect(surfaceStyle).not.toHaveProperty("paddingHorizontal");
  expect(surfaceStyle).not.toHaveProperty("paddingVertical");
  expect(advertWindowStyle).toMatchObject({
    backgroundColor: "transparent",
    flex: 1,
  });
  expect(advertWindowStyle).not.toHaveProperty("borderRadius");
  expect(advertWindowStyle).not.toHaveProperty("borderWidth");
  expect(advertWindowStyle).not.toHaveProperty("margin");
  expect(advertWindowStyle).not.toHaveProperty("padding");
  expect(advertWindowStyle).not.toHaveProperty("position");
  expect(screen.queryByText("Welcome")).toBeNull();
});

test("idle 无广告时保留列头、广告位纯底色占位和结算卡，不显示任何广告文案", async () => {
  const screen = await renderSurface(
    snapshot("idle", { advert: null, items: [] }),
  );

  expect(screen.getByText("Item Description")).toBeTruthy();
  expect(screen.getByText("Ready for Payment")).toBeTruthy();
  expect(screen.getByTestId("external-display-advert-window")).toBeTruthy();
  expect(screen.queryByText("Your basket is empty")).toBeNull();
});

test.each(["idle", "cart", "payment", "change", "success"] as const)(
  "%s 模式有商品时即使有广告也保留左右交易布局",
  async (mode) => {
    const screen = await renderSurface(snapshot(mode, { advert }));
    const layout = resolveCustomerDisplayLayout(SCREEN_1080P);

    expect(screen.getByText("Tea")).toBeTruthy();
    expect(
      StyleSheet.flatten(
        screen.getByTestId("external-display-transaction-panel").props.style,
      ),
    ).toMatchObject({ width: layout.cartPanel.width });
    expect(
      StyleSheet.flatten(
        screen.getByTestId("external-display-advert-window").props.style,
      ),
    ).toMatchObject({ width: layout.advertPanel.width });
  },
);

test("旧快照默认显示末尾 6 行，并提示前面被隐藏的行数", async () => {
  const items = Array.from({ length: 8 }, (_, index) =>
    item(`Item ${index + 1}`),
  );
  const screen = await renderSurface(snapshot("cart", { items }));

  expect(screen.queryByText("Item 1")).toBeNull();
  expect(screen.queryByText("Item 2")).toBeNull();
  expect(screen.getByText("Item 3")).toBeTruthy();
  expect(screen.getByText("Item 8")).toBeTruthy();
  expect(screen.getByText("2 earlier")).toBeTruthy();
  expect(screen.getAllByText("—")).toHaveLength(6);
  expect(screen.getAllByTestId("external-display-item-row")).toHaveLength(6);
});

test("显式窗口保持原顺序，并在购物车面板内显示上下隐藏数量", async () => {
  const items = Array.from({ length: 12 }, (_, index) =>
    item(`Item ${index + 1}`),
  );
  const screen = await renderSurface(
    snapshot("cart", { items, visibleItemStart: 3 }),
  );

  expect(screen.queryByText("Item 3")).toBeNull();
  expect(screen.getByText("Item 4")).toBeTruthy();
  expect(screen.getByText("Item 9")).toBeTruthy();
  expect(screen.queryByText("Item 10")).toBeNull();
  expect(screen.getByText("3 earlier · 3 later")).toBeTruthy();
  expect(
    StyleSheet.flatten(
      screen.getByTestId("external-display-hidden-items").props.style,
    ),
  ).toMatchObject({ position: "absolute" });
});

test("没有隐藏行时不显示提示", async () => {
  const screen = await renderSurface(snapshot("cart"));

  expect(screen.queryByTestId("external-display-hidden-items")).toBeNull();
});

test("中文主界面下客显窗口提示仍固定为英文", async () => {
  const chinese = await createTestI18n("zh");
  const items = Array.from({ length: 9 }, (_, index) =>
    item(`商品 ${index + 1}`),
  );
  const screen = await render(
    <I18nextProvider i18n={chinese}>
      <ExternalDisplaySurface
        snapshot={snapshot("cart", { items, visibleItemStart: 2 })}
        surfaceId="external-zh-window"
      />
    </I18nextProvider>,
  );

  expect(screen.getByText("商品 3")).toBeTruthy();
  expect(screen.getByText("商品 8")).toBeTruthy();
  expect(screen.getByText("2 earlier · 1 later")).toBeTruthy();
  expect(screen.queryByText("上方 2 件 · 下方 1 件")).toBeNull();
});

test("成功且无需找零时状态卡显示感谢语", async () => {
  const screen = await renderSurface(
    designedSnapshot("success", {
      change: { currency: "AUD", cents: 0 },
    }),
  );

  expect(screen.getByText("Payment complete")).toBeTruthy();
  expect(screen.getByText("Thank you for shopping with us")).toBeTruthy();
});

test("快照事件只接受严格更高 revision 并忽略相同或迟到结果", async () => {
  const nativeModule = installNativeModule();
  const screen = await renderSurface(
    snapshot("cart", {
      items: [item("Initial")],
      revision: 5,
    }),
  );

  await nativeModule.emit(
    snapshot("success", {
      items: [item("Latest")],
      revision: 7,
    }),
  );
  expect(screen.getByText("Payment complete")).toBeTruthy();
  expect(screen.getByText("Latest")).toBeTruthy();

  await nativeModule.emit(
    snapshot("payment", {
      items: [item("Same revision")],
      revision: 7,
    }),
  );
  await nativeModule.emit(
    snapshot("payment", {
      items: [item("Late")],
      revision: 6,
    }),
  );

  expect(screen.getByText("Payment complete")).toBeTruthy();
  expect(screen.getByText("Latest")).toBeTruthy();
  expect(screen.queryByText("Same revision")).toBeNull();
  expect(screen.queryByText("Late")).toBeNull();
});

function setWindow(width: number, height: number) {
  Dimensions.set({
    window: { width, height, scale: 2, fontScale: 1 },
    screen: { width, height, scale: 2, fontScale: 1 },
  });
}

async function renderSurface(value: CustomerDisplaySnapshot | null) {
  const i18n = await createTestI18n("en");
  return render(
    <I18nextProvider i18n={i18n}>
      <ExternalDisplaySurface
        snapshot={value}
        surfaceId="external-test"
      />
    </I18nextProvider>,
  );
}

async function createTestI18n(language: "en" | "zh") {
  const instance = createInstance();
  await instance.use(initReactI18next).init({
    compatibilityJSON: "v4",
    fallbackLng: "en",
    lng: language,
    resources: {
      en: { translation: en },
      zh: { translation: zh },
    },
    interpolation: { escapeValue: false },
  });
  return instance;
}

function snapshot(
  mode: CustomerDisplaySnapshot["mode"],
  overrides: Partial<CustomerDisplaySnapshot> = {},
): CustomerDisplaySnapshot {
  return {
    revision: 2,
    mode,
    items: [item("Tea")],
    gst: { currency: "AUD", cents: 112 },
    discount: { currency: "AUD", cents: 100 },
    total: { currency: "AUD", cents: 1_234 },
    change: { currency: "AUD", cents: 500 },
    advert: null,
    ...overrides,
  };
}

function designedSnapshot(
  mode: CustomerDisplaySnapshot["mode"],
  overrides: Partial<CustomerDisplaySnapshot> = {},
): CustomerDisplaySnapshot {
  const current = snapshot(mode, overrides);
  return {
    ...current,
    items: current.items.map((value) => ({
      ...value,
      unitPrice: { currency: "AUD", cents: 667 },
    })),
    summary: {
      itemQuantity: "2",
      skuCount: 1,
      subtotal: { currency: "AUD", cents: 1_334 },
    },
  };
}

function item(name: string): CustomerDisplaySnapshot["items"][number] {
  return {
    name,
    quantity: "2",
    amount: { currency: "AUD", cents: 1_234 },
  };
}

function installNativeModule() {
  let snapshotListener:
    | ((value: CustomerDisplaySnapshot) => void)
    | null = null;
  const status = {
    state: "ready" as const,
    enabled: true,
    connected: true,
    revision: 0,
    widthPixels: 1920,
    heightPixels: 1080,
    scale: 1,
    reason: "ready",
  };
  const nativeModule: ExternalDisplayNativeModule = {
    async getStatus() {
      return status;
    },
    async setEnabled() {
      return status;
    },
    async publishSnapshot(value) {
      return {
        accepted: true,
        revision: value.revision,
        latestRevision: value.revision,
        reason: "accepted",
      };
    },
    async markReactSurfaceReady() {},
    async markReactSurfaceRendered() {},
    addListener(eventName, listener) {
      if (eventName === "onSnapshotChanged") {
        snapshotListener = listener as (
          value: CustomerDisplaySnapshot,
        ) => void;
      }
      return { remove() {} };
    },
  };
  registerExternalDisplayReactSurface(nativeModule);

  return {
    async emit(value: CustomerDisplaySnapshot) {
      await act(async () => {
        snapshotListener?.(value);
      });
    },
  };
}
