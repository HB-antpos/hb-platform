import { afterEach, describe, expect, it, jest } from "@jest/globals";
import { act, fireEvent, render, waitFor } from "@testing-library/react-native";
import {
  Dimensions,
  FlatList,
  Platform,
  ScrollView,
  StyleSheet,
  TextInput,
} from "react-native";

import { salesEnglishCopy, salesText } from "./sales-copy";
import {
  EMPTY_SALE_CART,
  formatAud,
  MIN_TOUCH_TARGET,
  SalesPresenter,
  type SalesCapabilities,
  type SalesCartPort,
  type SalesCashCompletion,
  type SalesFeedbackEvent,
  type SalesLookupSelection,
  type SalesProductSearchItem,
  type SalesWorkflowPort,
} from "./sales-presenter";
import { SalesScreen } from "./sales-screen";

import { createAud, type CartLine, type CartSnapshot } from "@/core/contracts";
import { usePosShellStore } from "@/ui/shell/pos-shell-store";
import { posColors } from "@/ui/theme";

// 本文件渲染完整销售交互树；为低性能 CI 保留有界余量，避免 5 秒默认值造成假失败。
jest.setTimeout(15_000);

// UI 行为测试不读取 Expo 构建环境；计时器在独立 node:test 中覆盖。
jest.mock("@/features/sales/runtime/scan-timing", () => ({
  scanTiming: {
    beginHid: () => undefined,
    noteHidCharacter: () => undefined,
  },
}));

jest.mock("@expo/vector-icons", () => {
  const { Text } = jest.requireActual(
    "react-native",
  ) as typeof import("react-native");
  return {
    MaterialCommunityIcons: ({
      name,
      ...props
    }: Readonly<{ name: string; testID?: string }>) => (
      <Text {...props}>{name}</Text>
    ),
  };
});

jest.mock("@/ui/feedback", () => ({
  usePosSound: () => ({ play: jest.fn() }),
}));

jest.mock("expo-status-bar", () => ({ StatusBar: () => null }));

let mockStatusStripProps: any;

jest.mock("@/ui/shell/status-strip", () => ({
  PosStatusStrip: (props: unknown) => {
    mockStatusStripProps = props;
    return null;
  },
}));

const ALL_CAPABILITIES: SalesCapabilities = {
  catalog: true,
  cartEditing: true,
  cashCheckout: true,
  hold: true,
  lock: true,
};

function descendantTestIds(root: unknown): string[] {
  const result: string[] = [];
  const visit = (node: unknown): void => {
    if (typeof node !== "object" || node === null) return;
    const candidate = node as Readonly<{
      props?: Readonly<{ testID?: unknown }>;
      children?: readonly unknown[];
    }>;
    if (typeof candidate.props?.testID === "string") {
      result.push(candidate.props.testID);
    }
    for (const child of candidate.children ?? []) visit(child);
  };
  visit(root);
  return result;
}

function cartLinePanEvent(
  previousX: number,
  currentX: number,
  timeStamp: number,
  previousY = 100,
  currentY = 100,
) {
  const touch = {
    identifier: 0,
    locationX: currentX,
    locationY: currentY,
    pageX: currentX,
    pageY: currentY,
    target: 1,
    timestamp: timeStamp,
  };
  return {
    nativeEvent: {
      ...touch,
      changedTouches: [touch],
      touches: [touch],
    },
    touchHistory: {
      indexOfSingleActiveTouch: 0,
      mostRecentTimeStamp: timeStamp,
      numberActiveTouches: 1,
      touchBank: [
        {
          currentPageX: currentX,
          currentPageY: currentY,
          currentTimeStamp: timeStamp,
          previousPageX: previousX,
          previousPageY: previousY,
          previousTimeStamp: Math.max(0, timeStamp - 16),
          startPageX: 240,
          startPageY: 100,
          startTimeStamp: 1,
          touchActive: true,
        },
      ],
    },
  };
}

class ScreenCartPort implements SalesCartPort {
  public snapshot: CartSnapshot;
  public mergeAvailable = false;
  public mergeCompatibilityChecks = 0;
  public mergeResult = {
    groups: [] as {
      keptLineId: string;
      removedLineIds: readonly string[];
    }[],
    removedLineCount: 0,
  };
  public readonly clearSignals: string[] = [];
  public readonly discounts: { lineId: string; basisPoints: number }[] = [];
  public readonly edits: {
    operation: string;
    lineId?: string;
    value?: number;
  }[] = [];
  private readonly listeners = new Set<() => void>();

  public constructor(snapshot: CartSnapshot) {
    this.snapshot = snapshot;
  }

  public getSnapshot(): CartSnapshot {
    return this.snapshot;
  }

  public subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  public publish(snapshot: CartSnapshot): void {
    this.snapshot = snapshot;
    this.emit();
  }

  public hasMergeCompatibleLines(): boolean {
    this.mergeCompatibilityChecks += 1;
    return this.mergeAvailable;
  }

  public async mergeCompatibleLines() {
    this.edits.push({ operation: "merge-compatible-lines" });
    return this.mergeResult;
  }

  public async increaseLine(lineId: string): Promise<void> {
    this.updateQuantity(lineId, 1);
  }

  public async decreaseLine(lineId: string): Promise<void> {
    this.updateQuantity(lineId, -1);
  }

  public async removeLine(lineId: string): Promise<void> {
    this.snapshot = {
      ...this.snapshot,
      revision: this.snapshot.revision + 1,
      lines: this.snapshot.lines.filter((line) => line.lineId !== lineId),
    };
    this.emit();
  }

  public async applyLineDiscountBasisPoints(
    lineId: string,
    basisPoints: number,
  ): Promise<void> {
    this.discounts.push({ lineId, basisPoints });
  }

  public async setLineQuantity(
    lineId: string,
    quantity: number,
  ): Promise<void> {
    this.edits.push({ operation: "line-quantity", lineId, value: quantity });
  }

  public async setLineUnitPriceCents(
    lineId: string,
    unitPriceCents: number,
  ): Promise<void> {
    this.edits.push({ operation: "line-price", lineId, value: unitPriceCents });
  }

  public async applyLineDiscountAmountCents(
    lineId: string,
    discountCents: number,
  ): Promise<void> {
    this.edits.push({
      operation: "line-discount-amount",
      lineId,
      value: discountCents,
    });
  }

  public async applyLineManualDiscountBasisPoints(
    lineId: string,
    basisPoints: number,
  ): Promise<void> {
    this.edits.push({
      operation: "line-discount-percent",
      lineId,
      value: basisPoints,
    });
  }

  public async applyOrderDiscountAmountCents(
    discountCents: number,
  ): Promise<void> {
    this.edits.push({
      operation: "order-discount-amount",
      value: discountCents,
    });
  }

  public async applyOrderManualDiscountBasisPoints(
    basisPoints: number,
  ): Promise<void> {
    this.edits.push({
      operation: "order-discount-percent",
      value: basisPoints,
    });
  }

  public async applyOrderQuickDiscountBasisPoints(
    basisPoints: number,
  ): Promise<void> {
    this.edits.push({
      operation: "order-discount-quick",
      value: basisPoints,
    });
  }

  public async clearCart(): Promise<void> {
    this.edits.push({ operation: "clear-cart" });
    this.snapshot = {
      ...EMPTY_SALE_CART,
      revision: this.snapshot.revision + 1,
    };
    this.emit();
  }

  public async clearAfterCommittedOrder(orderGuid: string): Promise<void> {
    this.clearSignals.push(orderGuid);
    this.snapshot = {
      ...EMPTY_SALE_CART,
      revision: this.snapshot.revision + 1,
    };
    this.emit();
  }

  private updateQuantity(lineId: string, delta: number): void {
    this.snapshot = {
      ...this.snapshot,
      revision: this.snapshot.revision + 1,
      lines: this.snapshot.lines.map((line) =>
        line.lineId === lineId
          ? {
              ...line,
              quantity: String(Math.max(1, Number(line.quantity) + delta)),
            }
          : line,
      ),
    };
    this.emit();
  }

  private emit(): void {
    for (const listener of this.listeners) {
      listener();
    }
  }
}

function cartSnapshot(actualAmountCents = 995): CartSnapshot {
  const line: CartLine = {
    lineId: "line-1",
    productCode: "P-001",
    itemNumber: "I-001",
    lookupCode: "930000000001",
    displayName: "Fresh milk",
    quantity: "1",
    unitPrice: createAud(actualAmountCents),
    discount: createAud(0),
    actualAmount: createAud(actualAmountCents),
    priceSource: "catalog",
    kind: "sale",
    returnSourceKey: null,
    originalOrderGuid: null,
    originalOrderDetailGuid: null,
  };
  return {
    revision: 1,
    mode: "sale",
    lines: [line],
    subtotal: createAud(actualAmountCents),
    discount: createAud(0),
    actualAmount: createAud(actualAmountCents),
  };
}

function workflow(
  completeCash: SalesWorkflowPort["completeCash"] = async () => ({
    completed: true,
    canClearCart: true,
    orderGuid: "order-ui-1",
    cashDueCents: 995,
    changeCents: 5,
    postCommit: { drawerDisposition: "queued" },
  }),
): SalesWorkflowPort {
  return {
    async searchProducts() {
      return [];
    },
    async addProduct() {},
    async addByLookupCode() {
      return null;
    },
    subscribeScanTarget: () => () => undefined,
    async addOpenItem() {},
    getPendingCatalogWorkCount: () => 0,
    subscribePendingCatalogWork: () => () => undefined,
    async settlePendingCatalogWork() {
      return { timedOut: false };
    },
    disposePendingCatalogWork() {},
    releasePreparedCheckout() {},
    completeCash,
    async holdCart() {},
    async lockTerminal() {},
  };
}

function presenter(
  cart: ScreenCartPort,
  options: Readonly<{
    capabilities?: SalesCapabilities;
    workflow?: SalesWorkflowPort;
  }> = {},
): SalesPresenter {
  return new SalesPresenter({
    cart,
    capabilities: options.capabilities ?? ALL_CAPABILITIES,
    workflow: options.workflow ?? workflow(),
    createCheckoutIntentId: () => "checkout-ui-1",
    canStartNewTransaction: () => true,
  });
}

async function pressKeypadKeys(
  screen: Awaited<ReturnType<typeof render>>,
  testIDPrefix: string,
  keys: readonly string[],
): Promise<void> {
  for (const key of keys) {
    await fireEvent.press(screen.getByTestId(`${testIDPrefix}-key-${key}`));
  }
}

async function openLegacyCash(salesPresenter: SalesPresenter): Promise<void> {
  await act(async () => {
    expect(await salesPresenter.openCash()).toBe(true);
  });
}

/** 按 HID 逐字符节奏写入搜索框：每个值代表一次受控累计 onChangeText。 */
async function typeSalesSearchSequence(
  screen: Awaited<ReturnType<typeof render>>,
  values: readonly string[],
  gapMs: number,
): Promise<void> {
  for (const value of values) {
    await fireEvent.changeText(
      screen.getByTestId("sales-search-input"),
      value,
    );
    if (gapMs > 0) {
      await act(async () => {
        jest.advanceTimersByTime(gapMs);
      });
    }
  }
}

function findRenderedNodesByType(
  value: unknown,
  type: string,
): readonly Record<string, unknown>[] {
  if (Array.isArray(value)) {
    return value.flatMap((item) => findRenderedNodesByType(item, type));
  }
  if (!value || typeof value !== "object") {
    return [];
  }

  const node = value as {
    children?: unknown;
    props?: Record<string, unknown>;
    type?: unknown;
  };
  return [
    ...(node.type === type ? [node.props ?? {}] : []),
    ...findRenderedNodesByType(node.children, type),
  ];
}

function flattenedStyle(node: Readonly<{ props: { style?: unknown } }>) {
  const style =
    typeof node.props.style === "function"
      ? node.props.style({ pressed: false })
      : node.props.style;
  return StyleSheet.flatten(style);
}

afterEach(() => {
  mockStatusStripProps = null;
  usePosShellStore.getState().reset();
  jest.restoreAllMocks();
});

describe("SalesScreen", () => {
  it("销售更新门禁文案只使用手持终端命名", () => {
    const copy = `${salesEnglishCopy["updateGate.disabledTitle"]} ${salesText("zh", "updateGate.disabledTitle")}`;
    expect(copy).toMatch(/terminal|终端/iu);
    expect(copy).not.toMatch(/ipad/iu);
  });

  it("手持商品录入隐藏标题和待机提示，并把相机放在键盘后", async () => {
    const onOpenCameraScanner = jest.fn();
    const onOpenSpecialProducts = jest.fn();
    const emptyPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const empty = await render(
      <SalesScreen
        locale="zh"
        onOpenCameraScanner={onOpenCameraScanner}
        onOpenPayment={jest.fn()}
        onOpenSpecialProducts={onOpenSpecialProducts}
        presenter={emptyPresenter}
        showStatusStrip={false}
      />,
    );

    expect(empty.getByTestId("handheld-state-sales-empty")).toBeTruthy();
    expect(
      StyleSheet.flatten(
        empty.getByTestId("handheld-state-sales-empty").props.style,
      ).flexDirection,
    ).toBe("column");
    expect(
      flattenedStyle(empty.getByTestId("sales-open-payment")).minHeight,
    ).toBeGreaterThanOrEqual(48);
    expect(
      flattenedStyle(empty.getByTestId("sales-open-camera-scanner")).minHeight,
    ).toBeGreaterThanOrEqual(48);
    expect(
      flattenedStyle(empty.getByTestId("sales-open-camera-scanner")),
    ).toMatchObject({ height: 48, width: 48 });
    expect(
      empty.getByTestId("sales-open-camera-scanner").props.accessibilityLabel,
    ).toBe("相机扫码");
    expect(empty.getByTestId("sales-camera-icon").props.children).toBe(
      "camera-outline",
    );
    expect(
      descendantTestIds(empty.getByTestId("sales-search-input-row")),
    ).toEqual([
      "sales-search-input-row",
      "sales-search-input",
      "sales-search-button",
      "sales-search-icon",
      "sales-show-keyboard",
      "sales-keyboard-icon",
      "sales-open-camera-scanner",
      "sales-camera-icon",
    ]);
    expect(
      flattenedStyle(empty.getByTestId("sales-search-button")),
    ).toMatchObject({ height: 48, width: 48 });
    expect(
      empty.getByTestId("sales-search-button").props.accessibilityLabel,
    ).toBe("搜索并加入商品");
    expect(empty.getByTestId("sales-search-icon").props.children).toBe(
      "magnify",
    );
    expect(
      flattenedStyle(empty.getByTestId("sales-show-keyboard")),
    ).toMatchObject({ height: 48, width: 48 });
    expect(empty.getByTestId("sales-keyboard-icon").props.children).toBe(
      "keyboard-outline",
    );
    expect(
      descendantTestIds(empty.getByTestId("sales-product-entry-actions")),
    ).toEqual([
      "sales-product-entry-actions",
      "sales-open-item-button",
      "sales-open-special-products",
    ]);
    expect(
      flattenedStyle(empty.getByTestId("sales-open-item-button")),
    ).toMatchObject({
      backgroundColor: posColors.yellowSoft,
      borderColor: posColors.yellow,
    });
    expect(
      flattenedStyle(empty.getByTestId("sales-open-special-products")),
    ).toMatchObject({
      backgroundColor: posColors.blueSoft,
      borderColor: posColors.blue,
    });
    expect(empty.queryByTestId("sales-add-code-button")).toBeNull();
    expect(empty.queryByText("搜索")).toBeNull();
    expect(empty.queryByText("按编码加入")).toBeNull();
    expect(empty.queryByText("相机扫码")).toBeNull();
    expect(empty.queryByText("商品录入")).toBeNull();
    expect(empty.queryByText("扫码器就绪")).toBeNull();
    expect(empty.getByTestId("handheld-state-pda-sales-input")).toBeTruthy();
    expect(
      empty.queryByText("请使用 PDA 扳机扫码；相机扫码仅作为备用。"),
    ).toBeNull();
    expect(empty.queryByTestId("handheld-state-pda-scan-ready")).toBeNull();
    expect(
      empty.queryByText(
        "点击搜索框默认只接收 HID 扫码，并以回车提交；触摸或中文输入请点击上方“键盘”按钮。",
      ),
    ).toBeNull();
    await fireEvent.press(empty.getByTestId("sales-open-camera-scanner"));
    expect(onOpenCameraScanner).toHaveBeenCalledTimes(1);
    await fireEvent.press(empty.getByTestId("sales-open-special-products"));
    expect(onOpenSpecialProducts).toHaveBeenCalledTimes(1);
    await fireEvent.press(empty.getByTestId("sales-open-item-button"));
    expect(empty.getByTestId("handheld-state-open-item-keypad")).toBeTruthy();
    await empty.unmount();
    emptyPresenter.destroy();
  });

  it("竖屏保留单一购物车列表，并把汇总与付款共同固定在 footer", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        onOpenCameraScanner={jest.fn()}
        onOpenPayment={jest.fn()}
        presenter={salesPresenter}
      />,
    );

    const cartList = screen.getByTestId("sales-cart-list");
    const listTestIds = descendantTestIds(cartList);
    expect(screen.queryByTestId("sales-function-scroll")).toBeNull();
    expect(listTestIds).toEqual(
      expect.arrayContaining(["sales-product-entry", "sales-line-line-1"]),
    );
    expect(listTestIds).not.toContain("sales-summary-pane");
    expect(listTestIds).not.toContain("sales-function-pane");
    expect(listTestIds).not.toContain("sales-open-payment");
    expect(screen.queryByTestId("sales-function-pane")).toBeNull();

    const testIds = descendantTestIds(screen.toJSON());
    const positions = [
      "handheld-operational-strip",
      "sales-product-entry",
      "sales-line-line-1",
      "sales-summary-pane",
      "sales-order-discount",
      "sales-clear-cart",
      "sales-open-payment",
    ].map((testID) => testIds.indexOf(testID));
    expect(positions.every((position) => position >= 0)).toBe(true);
    expect(positions).toEqual(
      [...positions].sort((left, right) => left - right),
    );
    const footer = screen.getByTestId("sales-screen-footer");
    for (const testID of [
      "sales-summary-pane",
      "sales-order-discount",
      "sales-clear-cart",
      "sales-open-payment",
    ]) {
      expect(footer).toContainElement(screen.getByTestId(testID));
    }
    expect(
      flattenedStyle(screen.getByTestId("sales-summary-overview")),
    ).toMatchObject({ flexDirection: "row" });
    expect(
      flattenedStyle(screen.getByTestId("sales-summary-actions")),
    ).toMatchObject({ flexDirection: "row" });
    expect(
      flattenedStyle(screen.getByTestId("sales-summary-pane")),
    ).toMatchObject({ elevation: 3, flexDirection: "column" });
    expect(screen.queryByTestId("sales-compact-top-bar")).toBeNull();
    expect(flattenedStyle(cartList).minHeight).toBeUndefined();
    expect(
      flattenedStyle(screen.getByTestId("sales-screen-footer")),
    ).toMatchObject({ padding: 16 });
    expect(
      screen.getByTestId("handheld-operational-strip").props.horizontal,
    ).toBe(true);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("320×568 折扣与商品行编辑弹窗的底部按钮保持自然高度且不溢出", async () => {
    const compactMetrics = {
      width: 320,
      height: 568,
      scale: 2,
      fontScale: 1,
    };
    const defaultMetrics = {
      width: 390,
      height: 844,
      scale: 3,
      fontScale: 1,
    };
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    await act(async () => {
      Dimensions.set({ window: compactMetrics, screen: compactMetrics });
    });

    try {
      const screen = await render(
        <SalesScreen
          locale="zh"
          presenter={salesPresenter}
          showStatusStrip={false}
        />,
      );

      await fireEvent.press(screen.getByTestId("sales-line-line-1-discount"));

      for (const testID of [
        "sales-line-discount-amount",
        "sales-line-discount-percent",
      ]) {
        const actionStyle = flattenedStyle(screen.getByTestId(testID));
        expect(actionStyle).toMatchObject({
          alignSelf: "stretch",
          minHeight: MIN_TOUCH_TARGET,
        });
        expect(actionStyle.flex).toBeUndefined();
      }
      expect(
        flattenedStyle(screen.getByTestId("sales-discount-cancel")).minHeight,
      ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);

      await fireEvent.press(screen.getByTestId("sales-discount-cancel"));
      await fireEvent.press(screen.getByTestId("sales-line-line-1-edit"));

      for (const action of [
        screen.getByLabelText("取消"),
        screen.getByTestId("sales-line-edit-confirm"),
      ]) {
        const actionStyle = flattenedStyle(action);
        expect(actionStyle).toMatchObject({
          alignSelf: "stretch",
          minHeight: MIN_TOUCH_TARGET,
        });
        expect(actionStyle.flex).toBeUndefined();
      }

      const valueDisplay = screen.getByTestId("sales-line-edit-value");
      let valueSummary = valueDisplay.parent;
      while (
        valueSummary &&
        flattenedStyle(valueSummary)?.justifyContent !== "center"
      ) {
        valueSummary = valueSummary.parent;
      }
      expect(valueSummary).not.toBeNull();
      if (!valueSummary) throw new Error("未找到商品行编辑数值摘要区");
      expect(flattenedStyle(valueSummary).flex).toBeUndefined();

      await screen.unmount();
    } finally {
      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      salesPresenter.destroy();
    }
  });

  it("360×592 TC26 无码商品弹窗操作并排且内容可滚动", async () => {
    const tc26Metrics = {
      width: 360,
      height: 592,
      scale: 2,
      fontScale: 1,
    };
    const defaultMetrics = {
      width: 390,
      height: 844,
      scale: 3,
      fontScale: 1,
    };
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    await act(async () => {
      Dimensions.set({ window: tc26Metrics, screen: tc26Metrics });
    });

    try {
      const screen = await render(
        <SalesScreen
          locale="zh"
          presenter={salesPresenter}
          showStatusStrip={false}
        />,
      );

      await fireEvent.press(screen.getByTestId("sales-open-item-button"));

      const cancel = screen.getByLabelText("取消");
      const confirm = screen.getByTestId("sales-open-item-confirm");
      const modalScroll = screen.getByTestId("sales-open-item-modal");
      expect(modalScroll.props).toMatchObject({
        bounces: false,
        showsVerticalScrollIndicator: false,
      });
      let actionAncestor = confirm.parent;
      while (actionAncestor && actionAncestor !== modalScroll) {
        actionAncestor = actionAncestor.parent;
      }
      expect(actionAncestor).toBe(modalScroll);
      expect(
        flattenedStyle(screen.getByTestId("handheld-state-open-item-keypad")),
      ).toMatchObject({ overflow: "hidden" });
      expect(confirm.parent).not.toBeNull();
      expect(flattenedStyle(confirm.parent!)).toMatchObject({
        flexDirection: "row",
      });
      for (const action of [cancel, confirm]) {
        expect(flattenedStyle(action)).toMatchObject({
          flex: 1,
          minHeight: MIN_TOUCH_TARGET,
        });
      }

      await screen.unmount();
    } finally {
      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      salesPresenter.destroy();
    }
  });

  it("默认不显示扫码待机文案，真实扫描和打印结果仍明确反馈", async () => {
    let publishLookupOutcome: ((event: SalesFeedbackEvent) => void) | undefined;
    const scanWorkflow: SalesWorkflowPort = {
      ...workflow(),
      subscribeLookupOutcome(listener) {
        publishLookupOutcome = listener;
        return () => {
          publishLookupOutcome = undefined;
        };
      },
    };
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: scanWorkflow,
    });
    const onReprintReceipt = jest.fn(async () => ({
      kind: "completed" as const,
    }));
    const screen = await render(
      <SalesScreen
        onReprintReceipt={onReprintReceipt}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.queryByTestId("handheld-state-pda-scan-ready")).toBeNull();
    expect(screen.queryByTestId("handheld-state-pda-scan-result")).toBeNull();
    await act(async () => {
      publishLookupOutcome?.({
        kind: "added",
        lineId: "line-1",
        source: "hid",
      });
    });
    expect(screen.getByTestId("handheld-state-pda-scan-result")).toBeTruthy();
    expect(screen.queryByTestId("handheld-state-pda-scan-ready")).toBeNull();
    await fireEvent.press(screen.getByTestId("sales-scan-result-dismiss"));
    expect(screen.queryByTestId("handheld-state-pda-scan-ready")).toBeNull();
    expect(screen.queryByTestId("handheld-state-pda-scan-result")).toBeNull();

    expect(
      screen.queryByTestId("handheld-state-pda-print-drawer-result"),
    ).toBeNull();
    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    await fireEvent.press(screen.getByTestId("sales-reprint-receipt"));
    await waitFor(() => {
      expect(onReprintReceipt).toHaveBeenCalledTimes(1);
      expect(
        screen.getByTestId("handheld-state-pda-print-drawer-result"),
      ).toBeTruthy();
    });
    await screen.unmount();
    salesPresenter.destroy();
  });

  it("无码商品使用自定义小数键盘且不会暴露系统文本输入", async () => {
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-open-item-button"));

    const valueDisplay = screen.getByTestId("sales-open-item-price");
    expect(valueDisplay.props.onChangeText).toBeUndefined();
    expect(valueDisplay.props.autoFocus).toBeUndefined();
    expect(valueDisplay.props.showSoftInputOnFocus).toBeUndefined();
    expect(valueDisplay.props.accessibilityValue).toEqual({ text: "0.00" });
    expect(screen.getByText("0.5")).toBeTruthy();
    expect(screen.getByText("0.99")).toBeTruthy();
    expect(
      screen.getByTestId("sales-open-item-key-clear").props.accessibilityLabel,
    ).toBe("清除");
    expect(
      screen.getByTestId("sales-open-item-key-backspace").props
        .accessibilityLabel,
    ).toBe("退格");

    const firstKey = screen.getByTestId("sales-open-item-key-1");
    expect(
      StyleSheet.flatten(firstKey.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(firstKey);
    await fireEvent.press(screen.getByTestId("sales-open-item-key-2"));
    await fireEvent.press(screen.getByTestId("sales-open-item-key-quick-50"));
    expect(
      screen.getByTestId("sales-open-item-price").props.accessibilityValue,
    ).toEqual({ text: "12.50" });

    await fireEvent.press(screen.getByTestId("sales-open-item-key-quick-99"));
    await fireEvent.press(screen.getByTestId("sales-open-item-key-decimal"));
    await fireEvent.press(screen.getByTestId("sales-open-item-key-8"));
    expect(
      screen.getByTestId("sales-open-item-price").props.accessibilityValue,
    ).toEqual({ text: "12.99" });

    await fireEvent.press(screen.getByTestId("sales-open-item-key-backspace"));
    await fireEvent.press(screen.getByTestId("sales-open-item-key-5"));
    expect(
      screen.getByTestId("sales-open-item-price").props.accessibilityValue,
    ).toEqual({ text: "12.95" });

    await fireEvent.press(screen.getByTestId("sales-open-item-key-clear"));
    await fireEvent.press(screen.getByTestId("sales-open-item-key-decimal"));
    await fireEvent.press(screen.getByTestId("sales-open-item-key-5"));
    expect(
      screen.getByTestId("sales-open-item-price").props.accessibilityValue,
    ).toEqual({ text: "0.5" });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("所有销售弹窗只声明手持设备支持的竖屏方向", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const expectPortraitModal = () => {
      const modalProps = findRenderedNodesByType(screen.toJSON(), "Modal");
      expect(modalProps).toHaveLength(1);
      expect(modalProps[0]?.supportedOrientations).toEqual(["portrait"]);
      return modalProps[0]!;
    };
    const dismissVisibleModal = async () => {
      const onRequestClose = expectPortraitModal().onRequestClose;
      expect(typeof onRequestClose).toBe("function");
      await act(async () => {
        (onRequestClose as () => void)();
      });
    };

    await openLegacyCash(salesPresenter);
    await dismissVisibleModal();

    await fireEvent.press(screen.getByTestId("sales-open-item-button"));
    await dismissVisibleModal();

    await fireEvent.press(screen.getByTestId("sales-line-line-1-discount"));
    expectPortraitModal();
    await fireEvent.press(screen.getByTestId("sales-line-discount-amount"));
    await dismissVisibleModal();

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    expectPortraitModal();
    await fireEvent.press(screen.getByTestId("sales-order-discount-amount"));
    await dismissVisibleModal();

    await fireEvent.press(screen.getByTestId("sales-clear-cart"));
    await dismissVisibleModal();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("语言切换入口位于手持页头且状态区不再包含客显语义", async () => {
    const onSwitchLanguage = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onSwitchLanguage={onSwitchLanguage}
        presenter={salesPresenter}
      />,
    );

    expect(screen.getByTestId("sales-switch-language")).toBeTruthy();
    expect(screen.getByTestId("handheld-operational-strip")).toBeTruthy();
    expect(mockStatusStripProps).toBeNull();
    await fireEvent.press(screen.getByTestId("sales-switch-language"));
    expect(onSwitchLanguage).toHaveBeenCalledTimes(1);

    salesPresenter.destroy();
    await screen.unmount();

    const englishPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const englishScreen = await render(
      <SalesScreen
        locale="en"
        onSwitchLanguage={onSwitchLanguage}
        presenter={englishPresenter}
      />,
    );
    expect(englishScreen.getByTestId("sales-switch-language")).toBeTruthy();
    expect(
      englishScreen.getByTestId("handheld-operational-strip"),
    ).toBeTruthy();
    expect(mockStatusStripProps).toBeNull();
    englishPresenter.destroy();
    await englishScreen.unmount();
  });

  it("空购物车禁用结账，并为操作按钮保留至少 44pt 触控目标", async () => {
    usePosShellStore.getState().setConnectivity("offline");
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.getByTestId("sales-cart-empty")).toBeTruthy();
    expect(screen.getByTestId("sales-offline-cash-only")).toBeTruthy();
    const checkout = screen.getByTestId("sales-open-payment");
    expect(checkout.props.accessibilityState).toEqual({ disabled: true });
    const flattenedStyle = StyleSheet.flatten(checkout.props.style);
    expect(flattenedStyle.minHeight).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("更多弹窗承载全部交易功能，并保持默认顺序和 48px 触控目标", async () => {
    const onOpenHeldOrders = jest.fn();
    const onOpenCashDrawer = jest.fn(async () => ({
      kind: "completed" as const,
    }));
    const onOpenDailyClose = jest.fn();
    const onOpenLocalHistory = jest.fn();
    const onOpenRemoteHistory = jest.fn();
    const onOpenReturns = jest.fn();
    const onReprintReceipt = jest.fn(async () => ({
      kind: "completed" as const,
    }));
    const onOpenSpecialProducts = jest.fn();
    const onOpenSyncHistory = jest.fn();
    const onOpenCatalogMaintenance = jest.fn();
    const onOpenInstallments = jest.fn();
    const onOpenSettings = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onOpenCatalogMaintenance={onOpenCatalogMaintenance}
        onOpenCashDrawer={onOpenCashDrawer}
        onOpenDailyClose={onOpenDailyClose}
        onOpenHeldOrders={onOpenHeldOrders}
        onOpenInstallments={onOpenInstallments}
        onOpenLocalHistory={onOpenLocalHistory}
        onOpenRemoteHistory={onOpenRemoteHistory}
        onOpenReturns={onOpenReturns}
        onOpenSettings={onOpenSettings}
        onOpenSpecialProducts={onOpenSpecialProducts}
        onOpenSyncHistory={onOpenSyncHistory}
        onReprintReceipt={onReprintReceipt}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const pressVisibleAction = async (testID: string, callback: jest.Mock) => {
      const control = screen.getByTestId(testID);
      expect(
        StyleSheet.flatten(control.props.style).minHeight,
      ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
      await fireEvent.press(control);
      expect(callback).toHaveBeenCalledTimes(1);
    };
    const pressMoreAction = async (testID: string, callback: jest.Mock) => {
      await fireEvent.press(screen.getByTestId("sales-toolbar"));
      expect(
        screen.getByTestId("handheld-state-sales-more-actions"),
      ).toBeTruthy();
      await pressVisibleAction(testID, callback);
      expect(
        screen.queryByTestId("handheld-state-sales-more-actions"),
      ).toBeNull();
    };

    expect(screen.queryByTestId("sales-function-pane")).toBeNull();
    for (const testID of [
      "sales-hold",
      "sales-merge-cart",
      "sales-open-returns",
      "sales-open-local-history",
      "sales-open-held-orders",
      "sales-reprint-receipt",
      "sales-open-cash-drawer",
    ]) {
      expect(screen.queryByTestId(testID)).toBeNull();
    }

    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    const menuTestIds = descendantTestIds(
      screen.getByTestId("sales-toolbar-actions"),
    );
    const transactionActionIds = [
      "sales-hold",
      "sales-merge-cart",
      "sales-open-returns",
      "sales-open-local-history",
      "sales-open-held-orders",
      "sales-reprint-receipt",
      "sales-open-cash-drawer",
    ];
    const transactionPositions = transactionActionIds.map((testID) =>
      menuTestIds.indexOf(testID),
    );
    expect(transactionPositions.every((position) => position >= 0)).toBe(true);
    expect(transactionPositions).toEqual(
      [...transactionPositions].sort((left, right) => left - right),
    );
    expect(Math.max(...transactionPositions)).toBeLessThan(
      menuTestIds.indexOf("sales-open-daily-close"),
    );
    for (const testID of transactionActionIds) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
      ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    }
    await fireEvent.press(screen.getByTestId("sales-toolbar-close"));

    await pressMoreAction("sales-open-held-orders", onOpenHeldOrders);
    await pressMoreAction("sales-open-returns", onOpenReturns);
    await pressMoreAction("sales-open-local-history", onOpenLocalHistory);
    await pressVisibleAction(
      "sales-open-special-products",
      onOpenSpecialProducts,
    );

    await pressMoreAction("sales-open-daily-close", onOpenDailyClose);
    await pressMoreAction("sales-open-remote-history", onOpenRemoteHistory);
    await pressMoreAction("sales-open-installments", onOpenInstallments);
    await pressMoreAction("sales-open-settings", onOpenSettings);
    await pressMoreAction("sales-open-sync-history", onOpenSyncHistory);
    await pressMoreAction(
      "sales-open-catalog-maintenance",
      onOpenCatalogMaintenance,
    );

    expect(screen.queryByTestId("sales-open-camera-scanner")).toBeNull();
    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    expect(screen.getByTestId("sales-hold")).toBeTruthy();
    expect(screen.getByTestId("sales-lock")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("sales-toolbar-close"));

    salesPresenter.destroy();
    await screen.unmount();

    const noNavigationPresenter = presenter(
      new ScreenCartPort(EMPTY_SALE_CART),
    );
    const withoutNavigation = await render(
      <SalesScreen
        locale="en"
        presenter={noNavigationPresenter}
        showStatusStrip={false}
      />,
    );

    expect(withoutNavigation.queryByTestId("sales-hold")).toBeNull();
    expect(withoutNavigation.queryByTestId("sales-merge-cart")).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-special-products"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-camera-scanner"),
    ).toBeNull();
    expect(
      descendantTestIds(
        withoutNavigation.getByTestId("sales-product-entry-actions"),
      ),
    ).toEqual(["sales-product-entry-actions", "sales-open-item-button"]);
    expect(
      flattenedStyle(withoutNavigation.getByTestId("sales-open-item-button")),
    ).toMatchObject({ flexGrow: 1 });
    await fireEvent.press(withoutNavigation.getByTestId("sales-toolbar"));
    expect(withoutNavigation.getByTestId("sales-hold")).toBeTruthy();
    expect(withoutNavigation.getByTestId("sales-merge-cart")).toBeTruthy();
    expect(
      withoutNavigation.queryByTestId("sales-open-held-orders"),
    ).toBeNull();
    expect(withoutNavigation.queryByTestId("sales-open-returns")).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-local-history"),
    ).toBeNull();
    for (const testID of ["sales-reprint-receipt", "sales-open-cash-drawer"]) {
      const disabledAction = withoutNavigation.getByTestId(testID);
      expect(flattenedStyle(disabledAction)).toMatchObject({ opacity: 0.72 });
      await fireEvent.press(disabledAction);
      expect(
        withoutNavigation.getByTestId("handheld-state-sales-more-actions"),
      ).toBeTruthy();
    }
    expect(
      withoutNavigation.queryByTestId("sales-open-daily-close"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-remote-history"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-installments"),
    ).toBeNull();
    expect(withoutNavigation.queryByTestId("sales-open-settings")).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-sync-history"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-catalog-maintenance"),
    ).toBeNull();

    noNavigationPresenter.destroy();
    await withoutNavigation.unmount();
  });

  it("单列交易列表包含购物车图片，交易汇总固定在列表外", async () => {
    const resolveCartProductImage = jest.fn(
      async (_input: { productCode: string; lookupCode: string }) =>
        "https://pos.example.test/images/P-001.png",
    );
    const initialCart = cartSnapshot();
    const cart = new ScreenCartPort({
      ...initialCart,
      actualAmount: createAud(795),
      discount: createAud(200),
      lines: initialCart.lines.map((line) => ({
        ...line,
        actualAmount: createAud(795),
        discount: createAud(200),
      })),
    });
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        resolveCartProductImage={resolveCartProductImage}
        showStatusStrip={false}
      />,
    );

    const transactionPane = screen.getByTestId("sales-cart-list");
    expect(screen.queryByTestId("sales-function-pane")).toBeNull();
    expect(descendantTestIds(transactionPane)).toEqual(
      expect.arrayContaining(["sales-line-line-1"]),
    );
    expect(descendantTestIds(transactionPane)).not.toContain(
      "sales-summary-pane",
    );
    expect(screen.getByTestId("sales-screen-footer")).toContainElement(
      screen.getByTestId("sales-summary-pane"),
    );

    const lineNumber = screen.getByTestId("sales-line-line-1-line-number");
    expect(lineNumber.props.accessibilityLabel).toBe("第 1 行");
    expect(lineNumber.props.children).toBe(1);
    expect(StyleSheet.flatten(lineNumber.props.style).width).toBe(24);

    const lineDiscount = screen.getByTestId(
      "sales-line-line-1-discount-amount",
    );
    expect(StyleSheet.flatten(lineDiscount.props.style).color).toBe(
      posColors.red,
    );
    const summaryDiscount = screen.getByTestId("sales-summary-discount-amount");
    expect(StyleSheet.flatten(summaryDiscount.props.style).color).toBe(
      posColors.red,
    );

    const imageFrame = screen.getByTestId("sales-line-line-1-image");
    const imageStyle = StyleSheet.flatten(imageFrame.props.style);
    expect(imageStyle.width).toBeGreaterThanOrEqual(52);
    expect(imageStyle.width).toBeLessThanOrEqual(56);
    expect(imageStyle.height).toBe(imageStyle.width);
    const image = await screen.findByTestId("sales-line-line-1-image-content");
    expect(image.props.source).toEqual({
      uri: "https://pos.example.test/images/P-001.png",
    });
    expect(resolveCartProductImage).toHaveBeenCalledTimes(1);

    await fireEvent.press(screen.getByTestId("sales-line-line-1-increase"));
    expect(resolveCartProductImage).toHaveBeenCalledTimes(1);
    await fireEvent(image, "error");
    expect(screen.queryByTestId("sales-line-line-1-image-content")).toBeNull();
    expect(screen.getByTestId("sales-line-line-1-image")).toBeTruthy();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("购物车倒序展示时行号从总行数递减到一", async () => {
    const initialCart = cartSnapshot();
    const firstLine = initialCart.lines[0]!;
    const cart = new ScreenCartPort({
      ...initialCart,
      revision: 2,
      lines: [
        firstLine,
        {
          ...firstLine,
          lineId: "line-2",
          productCode: "P-002",
          itemNumber: "I-002",
          lookupCode: "930000000002",
          displayName: "Fresh bread",
        },
      ],
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={presenter(cart)}
        showStatusStrip={false}
      />,
    );

    const newestLineNumber = screen.getByTestId(
      "sales-line-line-2-line-number",
    );
    expect(newestLineNumber.props.accessibilityLabel).toBe("第 2 行");
    expect(newestLineNumber.props.children).toBe(2);

    const oldestLineNumber = screen.getByTestId(
      "sales-line-line-1-line-number",
    );
    expect(oldestLineNumber.props.accessibilityLabel).toBe("第 1 行");
    expect(oldestLineNumber.props.children).toBe(1);

    await screen.unmount();
  });

  it("购物车按加入顺序倒序展示，最新商品位于第一行", async () => {
    const baseLine = cartSnapshot().lines[0]!;
    const cart = new ScreenCartPort({
      ...cartSnapshot(),
      revision: 3,
      lines: [
        { ...baseLine, lineId: "line-1", displayName: "First item" },
        { ...baseLine, lineId: "line-2", displayName: "Second item" },
        { ...baseLine, lineId: "line-3", displayName: "Latest item" },
      ],
    });
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const cartList = screen.getByTestId("sales-cart-list");
    expect(
      (cartList.props.data as readonly CartLine[]).map((line) => line.lineId),
    ).toEqual(["line-3", "line-2", "line-1"]);
    expect(
      descendantTestIds(cartList).filter((testID) =>
        /^sales-line-line-\d+$/.test(testID),
      ),
    ).toEqual(["sales-line-line-3", "sales-line-line-2", "sales-line-line-1"]);
    expect(
      screen.getByTestId("sales-line-line-3-line-number").props
        .accessibilityLabel,
    ).toBe("第 3 行");
    expect(
      screen.getByTestId("sales-line-line-1-line-number").props
        .accessibilityLabel,
    ).toBe("第 1 行");

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("320×568 销售页允许商品信息和金额换行，不截断固定付款入口", async () => {
    const compactMetrics = {
      width: 320,
      height: 568,
      scale: 2,
      fontScale: 1,
    };
    const defaultMetrics = {
      width: 390,
      height: 844,
      scale: 3,
      fontScale: 1,
    };
    const baseLine = cartSnapshot().lines[0]!;
    const cart = new ScreenCartPort({
      ...cartSnapshot(),
      lines: [
        baseLine,
        {
          ...baseLine,
          displayName: "A long compact-screen product name",
          lineId: "line-latest",
        },
      ],
    });
    const salesPresenter = presenter(cart);

    await act(async () => {
      Dimensions.set({ window: compactMetrics, screen: compactMetrics });
    });
    try {
      const screen = await render(
        <SalesScreen
          locale="zh"
          onOpenPayment={jest.fn()}
          presenter={salesPresenter}
          showStatusStrip={false}
        />,
      );

      expect(
        flattenedStyle(screen.getByTestId("sales-cart-list")),
      ).toMatchObject({ flex: 1 });
      expect(
        flattenedStyle(screen.getByTestId("sales-line-line-latest"))
          .paddingHorizontal,
      ).toBeLessThanOrEqual(10);
      expect(
        flattenedStyle(screen.getByTestId("sales-line-line-latest-identity"))
          .minWidth,
      ).toBe(0);
      expect(
        flattenedStyle(screen.getByTestId("sales-line-line-latest-total")),
      ).toMatchObject({ flexBasis: "100%", minWidth: 0 });
      expect(screen.getByTestId("sales-screen-footer")).toContainElement(
        screen.getByTestId("sales-open-payment"),
      );

      await screen.unmount();
    } finally {
      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      salesPresenter.destroy();
    }
  });

  it("360×592 TC26 键盘关闭时保留半屏商品行与紧凑交易操作", async () => {
    const tc26Metrics = {
      width: 360,
      height: 592,
      scale: 2,
      fontScale: 1,
    };
    const defaultMetrics = {
      width: 390,
      height: 844,
      scale: 3,
      fontScale: 1,
    };
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));

    await act(async () => {
      Dimensions.set({ window: tc26Metrics, screen: tc26Metrics });
    });
    try {
      const screen = await render(
        <SalesScreen
          locale="zh"
          onOpenPayment={jest.fn()}
          onOpenSpecialProducts={jest.fn()}
          onSwitchLanguage={jest.fn()}
          presenter={salesPresenter}
        />,
      );

      expect(
        flattenedStyle(screen.getByTestId("sales-compact-top-bar")),
      ).toMatchObject({ height: 48, minHeight: 48 });
      expect(
        descendantTestIds(screen.getByTestId("sales-compact-top-bar")),
      ).toEqual(
        expect.arrayContaining([
          "sales-switch-language",
          "sales-toolbar",
          "handheld-operational-strip",
        ]),
      );
      expect(
        flattenedStyle(screen.getByTestId("sales-search-input-row")),
      ).toMatchObject({ height: 48, minHeight: 48 });
      expect(
        flattenedStyle(screen.getByTestId("sales-product-entry-actions")),
      ).toMatchObject({ height: 48, minHeight: 48 });
      expect(
        descendantTestIds(screen.getByTestId("sales-product-entry-actions")),
      ).toEqual(
        expect.arrayContaining([
          "sales-open-item-button",
          "sales-open-special-products",
        ]),
      );
      expect(
        flattenedStyle(screen.getByTestId("sales-transaction-pane")),
      ).toMatchObject({ height: 24, minHeight: 24 });
      expect(
        flattenedStyle(screen.getByTestId("sales-cart-list")).minHeight,
      ).toBe(416);
      expect(
        flattenedStyle(screen.getByTestId("sales-summary-pane")),
      ).toMatchObject({ flexDirection: "row", height: 48, minHeight: 48 });
      for (const actionTestID of ["sales-order-discount", "sales-clear-cart"]) {
        expect(flattenedStyle(screen.getByTestId(actionTestID))).toMatchObject({
          minHeight: 48,
          minWidth: 48,
        });
      }
      expect(
        flattenedStyle(screen.getByTestId("sales-open-payment")).minHeight,
      ).toBeGreaterThanOrEqual(48);
      expect(
        flattenedStyle(screen.getByTestId("sales-screen-footer")),
      ).toMatchObject({
        borderTopWidth: 0,
        padding: 8,
        paddingBottom: 0,
        paddingHorizontal: 8,
        paddingTop: 8,
      });

      await screen.unmount();
    } finally {
      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      salesPresenter.destroy();
    }
  });

  it("320×568 空购物车仍为商品行保留半屏高度", async () => {
    const compactMetrics = {
      width: 320,
      height: 568,
      scale: 2,
      fontScale: 1,
    };
    const defaultMetrics = {
      width: 390,
      height: 844,
      scale: 3,
      fontScale: 1,
    };
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));

    await act(async () => {
      Dimensions.set({ window: compactMetrics, screen: compactMetrics });
    });
    try {
      const screen = await render(
        <SalesScreen
          locale="zh"
          onOpenPayment={jest.fn()}
          onOpenSpecialProducts={jest.fn()}
          presenter={salesPresenter}
          showStatusStrip={false}
        />,
      );

      expect(
        flattenedStyle(screen.getByTestId("sales-cart-list")).minHeight,
      ).toBe(404);
      expect(
        flattenedStyle(screen.getByTestId("sales-cart-empty")).minHeight,
      ).toBe(284);
      expect(
        descendantTestIds(screen.getByTestId("sales-product-entry-actions")),
      ).toEqual(
        expect.arrayContaining([
          "sales-open-item-button",
          "sales-open-special-products",
        ]),
      );

      await screen.unmount();
    } finally {
      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      salesPresenter.destroy();
    }
  });

  it("TC26 将离线、核验、扫码、工具结果与错误收进可关闭状态栏", async () => {
    const tc26Metrics = {
      width: 360,
      height: 592,
      scale: 2,
      fontScale: 1,
    };
    const defaultMetrics = {
      width: 390,
      height: 844,
      scale: 3,
      fontScale: 1,
    };
    let publishLookupOutcome: ((event: SalesFeedbackEvent) => void) | undefined;
    const scanWorkflow: SalesWorkflowPort = {
      ...workflow(),
      getPendingCatalogWorkCount: () => 1,
      subscribeLookupOutcome(listener) {
        publishLookupOutcome = listener;
        return () => {
          publishLookupOutcome = undefined;
        };
      },
    };
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: scanWorkflow,
    });
    const onReprintReceipt = jest.fn(async () => ({
      kind: "completed" as const,
    }));

    await act(async () => {
      Dimensions.set({ window: tc26Metrics, screen: tc26Metrics });
      usePosShellStore.getState().setConnectivity("offline");
    });
    try {
      const screen = await render(
        <SalesScreen
          locale="zh"
          onReprintReceipt={onReprintReceipt}
          presenter={salesPresenter}
        />,
      );
      const expectCartHeightUnchanged = () => {
        expect(
          flattenedStyle(screen.getByTestId("sales-cart-list")).minHeight,
        ).toBe(416);
      };

      expect(screen.queryByTestId("sales-offline-cash-only")).toBeNull();
      expect(screen.queryByTestId("sales-catalog-verifying")).toBeNull();
      expect(
        screen.getByLabelText(
          "离线模式 · 仅现金: 银行卡和代金券支付必须连接后端。",
        ),
      ).toBeTruthy();
      expect(screen.getByLabelText("扫码器: 正在核验商品…")).toBeTruthy();
      expectCartHeightUnchanged();

      await act(async () => {
        publishLookupOutcome?.({
          kind: "added",
          lineId: "line-1",
          source: "hid",
        });
      });
      expect(screen.queryByTestId("handheld-state-pda-scan-result")).toBeNull();
      const scanStatus =
        screen.getByLabelText("已接收扫码: 商品已加入当前交易。");
      expect(scanStatus.props.accessibilityRole).toBe("button");
      expectCartHeightUnchanged();
      await fireEvent.press(scanStatus);
      expect(
        screen.queryByLabelText("已接收扫码: 商品已加入当前交易。"),
      ).toBeNull();

      await fireEvent.press(screen.getByTestId("sales-toolbar"));
      await fireEvent.press(screen.getByTestId("sales-reprint-receipt"));
      await waitFor(() => {
        expect(
          screen.getByLabelText("重新打印: 上一张小票已发送到打印机。"),
        ).toBeTruthy();
      });
      expect(
        screen.queryByTestId("handheld-state-pda-print-drawer-result"),
      ).toBeNull();
      expectCartHeightUnchanged();
      await fireEvent.press(
        screen.getByLabelText("重新打印: 上一张小票已发送到打印机。"),
      );
      expect(
        screen.queryByLabelText("重新打印: 上一张小票已发送到打印机。"),
      ).toBeNull();

      await act(async () => {
        await salesPresenter.addLookupCode();
      });
      const errorStatus = screen.getByLabelText(
        "需要处理: 请输入条码、货号或商品名称。",
      );
      expect(errorStatus.props.accessibilityRole).toBe("button");
      expectCartHeightUnchanged();
      await fireEvent.press(errorStatus);
      expect(salesPresenter.getState().errorCode).toBeNull();
      expect(
        screen.queryByLabelText("需要处理: 请输入条码、货号或商品名称。"),
      ).toBeNull();

      await screen.unmount();
    } finally {
      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      salesPresenter.destroy();
    }
  });

  it("购物车按选中、负金额、零金额和普通金额优先级显示单选底色", async () => {
    const baseLine = cartSnapshot().lines[0]!;
    const cart = new ScreenCartPort({
      ...cartSnapshot(),
      lines: [
        { ...baseLine, lineId: "positive", actualAmount: createAud(100) },
        { ...baseLine, lineId: "zero", actualAmount: createAud(0) },
        { ...baseLine, lineId: "negative", actualAmount: createAud(-100) },
      ],
    });
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(
      flattenedStyle(screen.getByTestId("sales-line-positive")),
    ).toMatchObject({
      backgroundColor: posColors.surface,
      borderColor: posColors.border,
    });
    expect(flattenedStyle(screen.getByTestId("sales-line-zero"))).toMatchObject(
      {
        backgroundColor: posColors.yellowSoft,
        borderColor: posColors.yellow,
      },
    );
    expect(
      flattenedStyle(screen.getByTestId("sales-line-negative")),
    ).toMatchObject({
      backgroundColor: posColors.greenSoft,
      borderColor: posColors.green,
    });
    expect(
      screen.getByTestId("sales-line-negative").props.accessibilityState,
    ).toMatchObject({ selected: true });

    await fireEvent.press(screen.getByTestId("sales-line-zero"));
    expect(flattenedStyle(screen.getByTestId("sales-line-zero"))).toMatchObject(
      {
        backgroundColor: posColors.greenSoft,
        borderColor: posColors.green,
      },
    );
    expect(
      flattenedStyle(screen.getByTestId("sales-line-negative")),
    ).toMatchObject({
      backgroundColor: posColors.redSoft,
      borderColor: posColors.red,
    });
    expect(
      screen.getByTestId("sales-line-negative").props.accessibilityState,
    ).toMatchObject({ selected: false });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("交易功能仅在领域确认存在兼容组时启用合并购物车", async () => {
    const base = cartSnapshot();
    const cart = new ScreenCartPort({
      ...base,
      lines: [base.lines[0]!, { ...base.lines[0]!, lineId: "line-2" }],
    });
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    expect(
      flattenedStyle(screen.getByTestId("sales-merge-cart")),
    ).toMatchObject({ opacity: 0.72 });
    await fireEvent.press(screen.getByTestId("sales-toolbar-close"));

    cart.mergeAvailable = true;
    cart.mergeResult = {
      groups: [{ keptLineId: "line-1", removedLineIds: ["line-2"] }],
      removedLineCount: 1,
    };
    await act(async () => {
      cart.publish({ ...cart.snapshot, revision: cart.snapshot.revision + 1 });
    });
    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    expect(
      flattenedStyle(screen.getByTestId("sales-merge-cart")).opacity,
    ).not.toBe(0.72);

    await fireEvent.press(screen.getByTestId("sales-merge-cart"));
    expect(cart.edits).toContainEqual({ operation: "merge-compatible-lines" });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("替换 Presenter 后虚拟化行操作只调用新的交易会话", async () => {
    const oldCart = new ScreenCartPort(cartSnapshot());
    const newCart = new ScreenCartPort(cartSnapshot());
    const oldPresenter = presenter(oldCart);
    const newPresenter = presenter(newCart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={oldPresenter}
        showStatusStrip={false}
      />,
    );

    await screen.rerender(
      <SalesScreen
        locale="zh"
        presenter={newPresenter}
        showStatusStrip={false}
      />,
    );
    await fireEvent.press(screen.getByTestId("sales-line-line-1-increase"));

    expect(oldCart.snapshot.lines[0]?.quantity).toBe("1");
    expect(newCart.snapshot.lines[0]?.quantity).toBe("2");

    oldPresenter.destroy();
    newPresenter.destroy();
    await screen.unmount();
  });

  it("数百行购物车配置有限首屏、批次和窗口虚拟化参数", async () => {
    const baseLine = cartSnapshot().lines[0]!;
    const resolveCartProductImage = jest.fn(
      async (_input: { productCode: string; lookupCode: string }) => null,
    );
    const cart = new ScreenCartPort({
      ...cartSnapshot(),
      revision: 300,
      lines: Array.from({ length: 300 }, (_, index) => ({
        ...baseLine,
        lineId: `line-${index + 1}`,
        productCode: `P-${index + 1}`,
        lookupCode: `BC-${index + 1}`,
      })),
    });
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        resolveCartProductImage={resolveCartProductImage}
        showStatusStrip={false}
      />,
    );

    expect(screen.getByTestId("sales-cart-list").props).toMatchObject({
      initialNumToRender: 10,
      maxToRenderPerBatch: 8,
      updateCellsBatchingPeriod: 32,
      windowSize: 7,
    });
    expect(resolveCartProductImage).toHaveBeenCalledTimes(10);
    expect(resolveCartProductImage).toHaveBeenNthCalledWith(1, {
      lookupCode: "BC-300",
      productCode: "P-300",
    });
    expect(resolveCartProductImage).toHaveBeenNthCalledWith(10, {
      lookupCode: "BC-291",
      productCode: "P-291",
    });
    const checksAfterInitialRender = cart.mergeCompatibilityChecks;
    await fireEvent.changeText(
      screen.getByTestId("sales-search-input"),
      "930000000001",
    );
    expect(cart.mergeCompatibilityChecks).toBe(checksAfterInitialRender);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("新 reveal 会取消旧行尚未测量时排队的滚动重试", async () => {
    const base = cartSnapshot();
    const initial = {
      ...base,
      lines: [base.lines[0]!, { ...base.lines[0]!, lineId: "line-2" }],
    };
    const cart = new ScreenCartPort(initial);
    const salesPresenter = presenter(cart);
    const scrollToIndex = jest
      .spyOn(FlatList.prototype, "scrollToIndex")
      .mockImplementation(() => undefined);
    const scrollToOffset = jest
      .spyOn(FlatList.prototype, "scrollToOffset")
      .mockImplementation(() => undefined);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    jest.useFakeTimers();
    try {
      await act(() => {
        screen.getByTestId("sales-cart-list").props.onScrollToIndexFailed({
          averageItemLength: 128,
          highestMeasuredFrameIndex: 0,
          index: 1,
        });
      });
      await act(() => {
        cart.publish({
          ...initial,
          revision: initial.revision + 1,
          lines: [...initial.lines, { ...base.lines[0]!, lineId: "line-3" }],
        });
      });
      expect(scrollToIndex).toHaveBeenLastCalledWith({
        animated: true,
        index: 0,
        viewPosition: 0,
      });
      scrollToIndex.mockClear();

      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(scrollToIndex).not.toHaveBeenCalled();
    } finally {
      jest.useRealTimers();
      scrollToIndex.mockRestore();
      scrollToOffset.mockRestore();
    }

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("清空购物车后释放图片缓存，同一商品进入新交易时重新解析", async () => {
    const resolveCartProductImage = jest.fn(
      async (_input: { productCode: string; lookupCode: string }) =>
        "https://pos.example.test/images/P-001.png",
    );
    const cart = new ScreenCartPort(cartSnapshot());
    const addProduct = jest.fn(async () => {
      const next = cartSnapshot();
      cart.snapshot = {
        ...next,
        revision: cart.snapshot.revision + 1,
        lines: next.lines.map((line) => ({
          ...line,
          lineId: "line-new-sale",
        })),
      };
    });
    const salesPresenter = presenter(cart, {
      workflow: {
        ...workflow(),
        addProduct,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        resolveCartProductImage={resolveCartProductImage}
        showStatusStrip={false}
      />,
    );

    expect(
      await screen.findByTestId("sales-line-line-1-image-content"),
    ).toBeTruthy();
    expect(resolveCartProductImage).toHaveBeenCalledTimes(1);

    await act(async () => {
      await cart.clearCart();
    });
    expect(screen.queryByTestId("sales-line-line-1-image")).toBeNull();

    await act(async () => {
      expect(
        await salesPresenter.addProduct({
          productCode: "P-001",
          itemNumber: "I-001",
          lookupCode: "930000000001",
          displayName: "Fresh milk",
          unitPriceCents: 995,
        }),
      ).toBe(true);
    });
    expect(
      await screen.findByTestId("sales-line-line-new-sale-image-content"),
    ).toBeTruthy();
    expect(resolveCartProductImage).toHaveBeenCalledTimes(2);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("清车释放未完成的图片请求，旧响应迟到也不能覆盖新交易", async () => {
    let resolveOldImage: ((uri: string | null) => void) | undefined;
    const oldImage = new Promise<string | null>((resolve) => {
      resolveOldImage = resolve;
    });
    let imageRequestCount = 0;
    const resolveCartProductImage = jest.fn(() => {
      imageRequestCount += 1;
      return imageRequestCount === 1
        ? oldImage
        : Promise.resolve("https://pos.example.test/images/current.png");
    });
    const cart = new ScreenCartPort(cartSnapshot());
    const addProduct = jest.fn(async () => {
      const next = cartSnapshot();
      cart.snapshot = {
        ...next,
        revision: cart.snapshot.revision + 1,
        lines: next.lines.map((line) => ({
          ...line,
          lineId: "line-current-sale",
        })),
      };
    });
    const salesPresenter = presenter(cart, {
      workflow: {
        ...workflow(),
        addProduct,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        resolveCartProductImage={resolveCartProductImage}
        showStatusStrip={false}
      />,
    );
    expect(resolveCartProductImage).toHaveBeenCalledTimes(1);

    await act(async () => {
      await cart.clearCart();
    });
    await act(async () => {
      expect(
        await salesPresenter.addProduct({
          productCode: "P-001",
          itemNumber: "I-001",
          lookupCode: "930000000001",
          displayName: "Fresh milk",
          unitPriceCents: 995,
        }),
      ).toBe(true);
    });

    const currentImage = await screen.findByTestId(
      "sales-line-line-current-sale-image-content",
    );
    expect(currentImage.props.source).toEqual({
      uri: "https://pos.example.test/images/current.png",
    });
    expect(resolveCartProductImage).toHaveBeenCalledTimes(2);

    await act(async () => {
      resolveOldImage?.("https://pos.example.test/images/stale.png");
      await oldImage;
    });
    expect(
      screen.getByTestId("sales-line-line-current-sale-image-content").props
        .source,
    ).toEqual({
      uri: "https://pos.example.test/images/current.png",
    });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("旧图片 URI 的迟到错误不会隐藏同一行的新图片", async () => {
    const resolveCartProductImage = jest.fn(
      async (input: { productCode: string; lookupCode: string }) =>
        `https://pos.example.test/images/${input.lookupCode}.png`,
    );
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        resolveCartProductImage={resolveCartProductImage}
        showStatusStrip={false}
      />,
    );
    const staleImage = await screen.findByTestId(
      "sales-line-line-1-image-content",
    );
    const staleOnError = staleImage.props.onError as () => void;

    await act(() => {
      cart.publish({
        ...cart.snapshot,
        revision: cart.snapshot.revision + 1,
        lines: cart.snapshot.lines.map((line) => ({
          ...line,
          lookupCode: "930000000002",
        })),
      });
    });
    await waitFor(() => {
      expect(
        screen.getByTestId("sales-line-line-1-image-content").props.source,
      ).toEqual({
        uri: "https://pos.example.test/images/930000000002.png",
      });
    });
    await act(() => {
      staleOnError();
    });

    expect(
      screen.getByTestId("sales-line-line-1-image-content").props.source,
    ).toEqual({
      uri: "https://pos.example.test/images/930000000002.png",
    });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("商品搜索结果显示在右侧抽屉，成功加购关闭而失败时保留", async () => {
    let shouldFail = false;
    const addProduct = jest.fn(async () => {
      if (shouldFail) throw new Error("add failed");
    });
    const searchProducts = jest.fn(async () => [
      {
        productCode: "P-SEARCH",
        itemNumber: "I-SEARCH",
        lookupCode: "930000000099",
        displayName: "Search result",
        unitPriceCents: 250,
      },
      {
        productCode: "P-SEARCH-2",
        itemNumber: "I-SEARCH-2",
        lookupCode: "930000000100",
        displayName: "Search result 2",
        unitPriceCents: 350,
      },
    ]);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        addProduct,
        searchProducts,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.changeText(screen.getByTestId("sales-search-input"), "奶");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(screen.getByTestId("sales-search-results-drawer")).toBeTruthy();
    const addButton = await screen.findByTestId("sales-product-P-SEARCH-add");
    expect(findRenderedNodesByType(screen.toJSON(), "Image")).toHaveLength(0);
    await fireEvent.press(addButton);
    expect(addProduct).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("sales-search-results-drawer")).toBeNull();

    shouldFail = true;
    await fireEvent.changeText(screen.getByTestId("sales-search-input"), "奶");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    await fireEvent.press(
      await screen.findByTestId("sales-product-P-SEARCH-add"),
    );
    expect(addProduct).toHaveBeenCalledTimes(2);
    expect(screen.getByTestId("sales-search-results-drawer")).toBeTruthy();
    expect(screen.getAllByText("商品无法加入购物车。").length).toBeGreaterThan(
      0,
    );

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("零个商品搜索结果打开抽屉且不尝试自动加入", async () => {
    const addProduct = jest.fn(async () => undefined);
    const searchProducts = jest.fn(async () => []);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        addProduct,
        searchProducts,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.changeText(
      screen.getByTestId("sales-search-input"),
      "不存在的商品",
    );
    await fireEvent.press(screen.getByTestId("sales-search-button"));

    expect(
      await screen.findByTestId("sales-search-results-drawer"),
    ).toBeTruthy();
    expect(screen.getByTestId("sales-search-results-empty")).toBeTruthy();
    expect(addProduct).not.toHaveBeenCalled();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("单一商品搜索结果自动加入，加入失败时回退结果抽屉", async () => {
    let shouldFail = false;
    const addProduct = jest.fn(async () => {
      if (shouldFail) throw new Error("add failed");
    });
    const searchProducts = jest.fn(async () => [
      {
        productCode: "P-SINGLE",
        itemNumber: "I-SINGLE",
        lookupCode: "930000000088",
        displayName: "Single result",
        unitPriceCents: 350,
      },
    ]);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        addProduct,
        searchProducts,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.changeText(screen.getByTestId("sales-search-input"), "奶");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    await waitFor(() => expect(addProduct).toHaveBeenCalledTimes(1));
    await waitFor(() =>
      expect(screen.getByTestId("sales-search-input").props.value).toBe(""),
    );
    expect(screen.queryByTestId("sales-search-results-drawer")).toBeNull();

    shouldFail = true;
    await fireEvent.changeText(screen.getByTestId("sales-search-input"), "奶");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    await waitFor(() => expect(addProduct).toHaveBeenCalledTimes(2));
    expect(
      await screen.findByTestId("sales-search-results-drawer"),
    ).toBeTruthy();
    expect(screen.getByTestId("sales-product-P-SINGLE-add")).toBeTruthy();
    expect(screen.getAllByText("商品无法加入购物车。").length).toBeGreaterThan(
      0,
    );

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("新搜索等待、失败或空白时不显示上一轮结果，并在抽屉提示必填", async () => {
    const resultA = {
      productCode: "P-A",
      itemNumber: "I-A",
      lookupCode: "LOOKUP-A",
      displayName: "Product A",
      unitPriceCents: 100,
    };
    const resultA2 = {
      ...resultA,
      productCode: "P-A2",
      itemNumber: "I-A2",
      lookupCode: "LOOKUP-A2",
      displayName: "Product A2",
    };
    let rejectSearchB: ((reason?: unknown) => void) | undefined;
    const searchB = new Promise<readonly (typeof resultA)[]>(
      (_resolve, reject) => {
        rejectSearchB = reject;
      },
    );
    const searchProducts = jest.fn((query: string) =>
      query === "A" ? Promise.resolve([resultA, resultA2]) : searchB,
    );
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        searchProducts,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    const searchInput = screen.getByTestId("sales-search-input");

    await fireEvent.changeText(searchInput, "A");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(await screen.findByTestId("sales-product-P-A-add")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("sales-search-results-close"));

    await fireEvent.changeText(searchInput, "B");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(screen.getByTestId("sales-search-progress")).toBeTruthy();
    expect(
      screen.getByTestId("sales-search-button").props.accessibilityState,
    ).toEqual({ busy: true, disabled: true });
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(searchProducts).toHaveBeenCalledTimes(2);
    expect(screen.queryByTestId("sales-product-P-A-add")).toBeNull();

    await act(async () => {
      rejectSearchB?.(new Error("search B failed"));
      await Promise.resolve();
    });
    expect(screen.queryByTestId("sales-product-P-A-add")).toBeNull();
    expect(
      screen.getAllByText("商品搜索失败，请重试。").length,
    ).toBeGreaterThan(0);

    await fireEvent.press(screen.getByTestId("sales-search-results-close"));
    await fireEvent.changeText(searchInput, "");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(screen.queryByTestId("sales-product-P-A-add")).toBeNull();
    expect(
      screen.getAllByText("请输入条码、货号或商品名称。").length,
    ).toBeGreaterThan(0);
    expect(searchProducts.mock.calls.map(([query]) => query)).toEqual([
      "A",
      "B",
    ]);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("搜索期间输入变化会丢弃迟到的单一结果且不自动加入", async () => {
    let resolveSearch:
      ((results: readonly SalesProductSearchItem[]) => void) | undefined;
    const pendingSearch = new Promise<readonly SalesProductSearchItem[]>(
      (resolve) => {
        resolveSearch = resolve;
      },
    );
    const addProduct = jest.fn(async () => undefined);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        addProduct,
        searchProducts: async () => pendingSearch,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    const searchInput = screen.getByTestId("sales-search-input");

    await fireEvent.changeText(searchInput, "旧查询");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(screen.getByTestId("sales-search-progress")).toBeTruthy();
    await fireEvent.changeText(searchInput, "新查询");
    await act(async () => {
      resolveSearch?.([
        {
          productCode: "P-STALE",
          itemNumber: "I-STALE",
          lookupCode: "930000000077",
          displayName: "Stale result",
          unitPriceCents: 700,
        },
      ]);
      await pendingSearch;
    });

    await waitFor(() => {
      expect(screen.queryByTestId("sales-search-progress")).toBeNull();
    });
    expect(addProduct).not.toHaveBeenCalled();
    expect(screen.queryByTestId("sales-search-results-drawer")).toBeNull();
    expect(screen.getByTestId("sales-search-input").props.value).toBe("新查询");

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("重打和开箱无回调时禁用，执行时防重复并显示可信结果", async () => {
    const noActionPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const withoutActions = await render(
      <SalesScreen
        locale="zh"
        presenter={noActionPresenter}
        showStatusStrip={false}
      />,
    );
    await fireEvent.press(withoutActions.getByTestId("sales-toolbar"));
    for (const testID of ["sales-reprint-receipt", "sales-open-cash-drawer"]) {
      const disabledAction = withoutActions.getByTestId(testID);
      expect(flattenedStyle(disabledAction)).toMatchObject({ opacity: 0.72 });
      await fireEvent.press(disabledAction);
      expect(
        withoutActions.getByTestId("handheld-state-sales-more-actions"),
      ).toBeTruthy();
    }
    await fireEvent.press(withoutActions.getByTestId("sales-toolbar-close"));
    noActionPresenter.destroy();
    await withoutActions.unmount();

    let finishReprint: ((result: { kind: "completed" }) => void) | undefined;
    const onReprintReceipt = jest.fn(
      () =>
        new Promise<{ kind: "completed" }>((resolve) => {
          finishReprint = resolve;
        }),
    );
    const onOpenCashDrawer = jest.fn(async () => ({
      kind: "denied" as const,
    }));
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onOpenCashDrawer={onOpenCashDrawer}
        onReprintReceipt={onReprintReceipt}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    await fireEvent.press(screen.getByTestId("sales-reprint-receipt"));
    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    const pendingReprint = screen.getByTestId("sales-reprint-receipt");
    expect(flattenedStyle(pendingReprint)).toMatchObject({ opacity: 0.72 });
    await fireEvent.press(pendingReprint);
    expect(onReprintReceipt).toHaveBeenCalledTimes(1);
    expect(
      flattenedStyle(screen.getByTestId("sales-open-cash-drawer")),
    ).toMatchObject({ opacity: 0.72 });
    await fireEvent.press(screen.getByTestId("sales-toolbar-close"));
    await act(async () => {
      finishReprint?.({ kind: "completed" });
      await Promise.resolve();
    });
    expect(screen.getByText("上一张小票已发送到打印机。")).toBeTruthy();

    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    await fireEvent.press(screen.getByTestId("sales-open-cash-drawer"));
    expect(onOpenCashDrawer).toHaveBeenCalledTimes(1);
    expect(screen.getByText("此操作需要主管授权。")).toBeTruthy();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("强制更新或门禁关闭时空车禁止开始交易，并保留明确更新入口", async () => {
    const onOpenRequiredUpdate = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        newTransactionGate={{
          state: "force-update",
          canStartNewTransaction: false,
          canContinueRecovery: true,
        }}
        onOpenCameraScanner={jest.fn()}
        onOpenRequiredUpdate={onOpenRequiredUpdate}
        onOpenSpecialProducts={jest.fn()}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.getByTestId("sales-new-transaction-gate")).toBeTruthy();
    expect(screen.getByTestId("sales-search-input").props.editable).toBe(false);
    for (const testID of [
      "sales-open-camera-scanner",
      "sales-search-button",
      "sales-show-keyboard",
      "sales-open-item-button",
      "sales-open-special-products",
    ]) {
      expect(screen.getByTestId(testID).props.accessibilityState).toMatchObject(
        {
        disabled: true,
        },
      );
    }
    await fireEvent.press(screen.getByTestId("sales-open-required-update"));
    expect(onOpenRequiredUpdate).toHaveBeenCalledTimes(1);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("OTA required 在空车时显示专用提示，但交易安全前不提供 reload 入口", async () => {
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        newTransactionGate={{
          state: "ota-update",
          canStartNewTransaction: false,
          canContinueRecovery: true,
        }}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.getByText("完成 HB POS 更新后才能开始下一单")).toBeTruthy();
    expect(screen.getByTestId("sales-search-input").props.editable).toBe(false);
    expect(screen.queryByTestId("sales-open-required-update")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("手动搜索键盘聚焦时滚动功能区揭示输入，HID 聚焦保持静默", async () => {
    const revealInput = jest
      .spyOn(
        ScrollView.prototype,
        "scrollResponderScrollNativeHandleToKeyboard",
      )
      .mockImplementation(() => undefined);
    const textInputPrototype = (
      TextInput as unknown as {
        prototype: {
          isFocused: jest.Mock;
        };
      }
    ).prototype;
    textInputPrototype.isFocused.mockReturnValue(false);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    jest.useFakeTimers();
    try {
      expect(screen.getByTestId("sales-cart-list").props).toMatchObject({
        automaticallyAdjustKeyboardInsets: true,
        keyboardDismissMode: "interactive",
        keyboardShouldPersistTaps: "handled",
      });
      await fireEvent(screen.getByTestId("sales-search-input"), "focus", {
        target: 401,
      });
      expect(revealInput).not.toHaveBeenCalled();

      await fireEvent.press(screen.getByTestId("sales-show-keyboard"));
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      await fireEvent(screen.getByTestId("sales-search-input"), "focus", {
        target: 402,
      });
      expect(revealInput).toHaveBeenCalledWith(402, 16, true);
    } finally {
      jest.useRealTimers();
      textInputPrototype.isFocused.mockReset();
      revealInput.mockRestore();
      salesPresenter.destroy();
      await screen.unmount();
    }
  });

  it("搜索系统键盘切换到自定义编辑器时隐藏键盘并保持 HID 暂停", async () => {
    const onManualInputFocusChange = jest.fn();
    const textInputPrototype = (
      TextInput as unknown as {
        prototype: {
          focus: jest.Mock;
          isFocused: jest.Mock;
        };
      }
    ).prototype;
    textInputPrototype.focus.mockClear();
    textInputPrototype.isFocused.mockReturnValue(true);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const searchInput = screen.getByTestId("sales-search-input");
    expect(searchInput.props.showSoftInputOnFocus).toBe(false);
    expect(searchInput.props.submitBehavior).toBe("blurAndSubmit");
    expect(
      screen.queryByText(
        "点击搜索框默认只接收 HID 扫码，并以回车提交；触摸或中文输入请点击上方“键盘”按钮。",
      ),
    ).toBeNull();

    jest.useFakeTimers();
    try {
      await fireEvent(searchInput, "focus");
      await fireEvent.press(screen.getByTestId("sales-show-keyboard"));
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);
      await act(() => {
        jest.runAllTimers();
      });
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
      await fireEvent(screen.getByTestId("sales-search-input"), "blur");
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(true);
      expect(textInputPrototype.focus).toHaveBeenCalledTimes(1);
      await fireEvent(screen.getByTestId("sales-search-input"), "focus", {
        target: 403,
      });
      await fireEvent.press(screen.getByTestId("sales-open-item-button"));
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(onManualInputFocusChange).toHaveBeenCalledTimes(1);
      expect(onManualInputFocusChange).toHaveBeenLastCalledWith(true);

      await fireEvent.press(screen.getByText("取消"));
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(onManualInputFocusChange).toHaveBeenNthCalledWith(2, false);
    } finally {
      textInputPrototype.isFocused.mockReset();
      jest.useRealTimers();
    }

    salesPresenter.destroy();
    await screen.unmount();

    const englishPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const englishScreen = await render(
      <SalesScreen
        locale="en"
        presenter={englishPresenter}
        showStatusStrip={false}
      />,
    );
    expect(
      englishScreen.queryByText(
        'Tapping the search field keeps HID-only input and submits scans with Enter. For touch or Chinese input, tap the "Keyboard" button above.',
      ),
    ).toBeNull();
    expect(englishScreen.getByLabelText("Keyboard")).toBeTruthy();

    englishPresenter.destroy();
    await englishScreen.unmount();
  });

  it("相机入口先收起软键盘并释放手动输入焦点，再转发扫码动作", async () => {
    const onManualInputFocusChange = jest.fn();
    const onOpenCameraScanner = jest.fn();
    const textInputPrototype = (
      TextInput as unknown as {
        prototype: {
          blur: jest.Mock;
          setNativeProps: jest.Mock;
        };
      }
    ).prototype;
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        onOpenCameraScanner={onOpenCameraScanner}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    jest.useFakeTimers();
    try {
      await fireEvent(screen.getByTestId("sales-search-input"), "focus");
      textInputPrototype.blur.mockClear();
      textInputPrototype.setNativeProps.mockClear();

      await fireEvent.press(screen.getByTestId("sales-open-camera-scanner"));

      expect(textInputPrototype.setNativeProps).toHaveBeenCalledWith({
        showSoftInputOnFocus: false,
      });
      expect(textInputPrototype.blur).toHaveBeenCalledTimes(1);
      expect(onOpenCameraScanner).toHaveBeenCalledTimes(1);
      expect(textInputPrototype.blur.mock.invocationCallOrder[0]).toBeLessThan(
        onOpenCameraScanner.mock.invocationCallOrder[0]!,
      );
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(onManualInputFocusChange.mock.calls).toEqual([[true], [false]]);
    } finally {
      jest.useRealTimers();
    }

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("键盘按钮通过真实失焦和重新聚焦首次及重复唤起系统键盘", async () => {
    const onManualInputFocusChange = jest.fn();
    const textInputPrototype = (
      TextInput as unknown as {
        prototype: {
          blur: jest.Mock;
          focus: jest.Mock;
          isFocused: jest.Mock;
          setNativeProps: jest.Mock;
        };
      }
    ).prototype;
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const keyboardButton = screen.getByTestId("sales-show-keyboard");
    expect(keyboardButton.props.accessibilityRole).toBe("button");
    expect(keyboardButton.props.accessibilityLabel).toBe("键盘");
    expect(
      StyleSheet.flatten(keyboardButton.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);

    const searchInput = screen.getByTestId("sales-search-input");
    expect(searchInput.props.showSoftInputOnFocus).toBe(false);
    expect(searchInput.props.submitBehavior).toBe("blurAndSubmit");

    textInputPrototype.blur.mockClear();
    textInputPrototype.focus.mockClear();
    textInputPrototype.isFocused.mockReturnValue(true);
    textInputPrototype.setNativeProps.mockClear();
    jest.useFakeTimers();
    try {
      await fireEvent(searchInput, "focus");
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);

      await fireEvent.press(keyboardButton);
      expect(textInputPrototype.blur).toHaveBeenCalledTimes(1);
      expect(textInputPrototype.setNativeProps).toHaveBeenCalledWith({
        showSoftInputOnFocus: false,
      });
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);

      await act(() => {
        jest.runAllTimers();
      });
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
      expect(textInputPrototype.isFocused).toHaveBeenCalledTimes(1);

      // 原生 blur 回调是进入软键盘阶段的唯一门槛。
      await fireEvent(screen.getByTestId("sales-search-input"), "blur");
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(true);
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(textInputPrototype.setNativeProps).toHaveBeenLastCalledWith({
        showSoftInputOnFocus: true,
      });
      expect(textInputPrototype.focus).toHaveBeenCalledTimes(1);
      expect(textInputPrototype.blur.mock.invocationCallOrder[0]).toBeLessThan(
        textInputPrototype.setNativeProps.mock.invocationCallOrder[1]!,
      );
      expect(
        textInputPrototype.setNativeProps.mock.invocationCallOrder[1],
      ).toBeLessThan(textInputPrototype.focus.mock.invocationCallOrder[0]!);
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(true);

      // focus 命令发出后请求仍未完成；真正 onFocus 到达后，普通 blur 才回 HID。
      await fireEvent(screen.getByTestId("sales-search-input"), "focus", {
        target: 404,
      });
      await fireEvent(screen.getByTestId("sales-search-input"), "blur");
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(onManualInputFocusChange.mock.calls).toEqual([[true], [false]]);

      textInputPrototype.blur.mockClear();
      textInputPrototype.focus.mockClear();
      textInputPrototype.setNativeProps.mockClear();

      // 系统键盘被手动收起后再次点击，仍重新建立第一响应者。
      await fireEvent(screen.getByTestId("sales-search-input"), "focus", {
        target: 405,
      });
      await fireEvent.press(keyboardButton);
      expect(textInputPrototype.blur).toHaveBeenCalledTimes(1);
      expect(textInputPrototype.setNativeProps).toHaveBeenCalledWith({
        showSoftInputOnFocus: false,
      });
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);
      await act(() => {
        jest.runAllTimers();
      });
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
      await fireEvent(screen.getByTestId("sales-search-input"), "blur");
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(textInputPrototype.setNativeProps).toHaveBeenLastCalledWith({
        showSoftInputOnFocus: true,
      });
      expect(textInputPrototype.focus).toHaveBeenCalledTimes(1);
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(true);
      await fireEvent(screen.getByTestId("sales-search-input"), "focus", {
        target: 406,
      });

      await fireEvent(
        screen.getByTestId("sales-search-input"),
        "submitEditing",
      );
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);
      expect(textInputPrototype.setNativeProps).toHaveBeenLastCalledWith({
        showSoftInputOnFocus: false,
      });

      await fireEvent(screen.getByTestId("sales-search-input"), "blur");
      await act(() => {
        jest.runOnlyPendingTimers();
      });
    } finally {
      textInputPrototype.isFocused.mockReset();
      jest.useRealTimers();
    }

    expect(onManualInputFocusChange.mock.calls).toEqual([
      [true],
      [false],
      [true],
      [false],
    ]);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("搜索输入未聚焦时直接启用软键盘并等待真实 focus 确认", async () => {
    const textInputPrototype = (
      TextInput as unknown as {
        prototype: {
          blur: jest.Mock;
          focus: jest.Mock;
          isFocused: jest.Mock;
          setNativeProps: jest.Mock;
        };
      }
    ).prototype;
    const onManualInputFocusChange = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    textInputPrototype.blur.mockClear();
    textInputPrototype.focus.mockClear();
    textInputPrototype.isFocused.mockReturnValue(false);
    textInputPrototype.setNativeProps.mockClear();
    jest.useFakeTimers();
    try {
      await fireEvent.press(screen.getByTestId("sales-show-keyboard"));
      expect(textInputPrototype.blur).not.toHaveBeenCalled();
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);

      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(true);
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(textInputPrototype.setNativeProps).toHaveBeenLastCalledWith({
        showSoftInputOnFocus: true,
      });
      expect(textInputPrototype.focus).toHaveBeenCalledTimes(1);

      // focus 尚未确认前的旧 blur 不结束请求；确认后普通 blur 才恢复 HID。
      await fireEvent(screen.getByTestId("sales-search-input"), "blur");
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(true);
      await fireEvent(screen.getByTestId("sales-search-input"), "focus", {
        target: 407,
      });
      await fireEvent(screen.getByTestId("sales-search-input"), "blur");
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);
      await act(() => {
        jest.runOnlyPendingTimers();
      });
      expect(onManualInputFocusChange.mock.calls).toEqual([[true], [false]]);
    } finally {
      textInputPrototype.isFocused.mockReset();
      jest.useRealTimers();
    }

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("输入不可用或组件卸载会取消尚未完成的键盘唤起", async () => {
    const textInputPrototype = (
      TextInput as unknown as {
        prototype: {
          blur: jest.Mock;
          focus: jest.Mock;
          isFocused: jest.Mock;
          setNativeProps: jest.Mock;
        };
      }
    ).prototype;
    const onManualInputFocusChange = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const defaultProps = {
      locale: "zh" as const,
      onManualInputFocusChange,
      presenter: salesPresenter,
      showStatusStrip: false,
    };
    const screen = await render(<SalesScreen {...defaultProps} />);

    jest.useFakeTimers();
    try {
      await fireEvent(screen.getByTestId("sales-search-input"), "focus");
      textInputPrototype.blur.mockClear();
      textInputPrototype.focus.mockClear();
      textInputPrototype.isFocused.mockReturnValue(true);
      textInputPrototype.setNativeProps.mockClear();

      await fireEvent.press(screen.getByTestId("sales-show-keyboard"));
      await screen.rerender(
        <SalesScreen
          {...defaultProps}
          newTransactionGate={{
            state: "force-update",
            canStartNewTransaction: false,
            canContinueRecovery: true,
          }}
        />,
      );
      await fireEvent(screen.getByTestId("sales-search-input"), "blur");
      await act(() => {
        jest.runAllTimers();
      });

      expect(screen.getByTestId("sales-search-input").props.editable).toBe(
        false,
      );
      expect(
        screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
      ).toBe(false);
      expect(textInputPrototype.setNativeProps).not.toHaveBeenCalledWith({
        showSoftInputOnFocus: true,
      });
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
      expect(onManualInputFocusChange.mock.calls).toEqual([[true], [false]]);

      await screen.rerender(<SalesScreen {...defaultProps} />);
      textInputPrototype.focus.mockClear();
      textInputPrototype.isFocused.mockReturnValue(false);
      textInputPrototype.setNativeProps.mockClear();
      await fireEvent.press(screen.getByTestId("sales-show-keyboard"));
      await screen.unmount();
      await act(() => {
        jest.runAllTimers();
      });
      expect(textInputPrototype.setNativeProps).not.toHaveBeenCalledWith({
        showSoftInputOnFocus: true,
      });
      expect(textInputPrototype.focus).not.toHaveBeenCalled();
    } finally {
      textInputPrototype.isFocused.mockReset();
      jest.useRealTimers();
    }

    salesPresenter.destroy();
  });

  it("现金收款输入占用和释放手动输入焦点", async () => {
    const onManualInputFocusChange = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await openLegacyCash(salesPresenter);
    const cashValue = screen.getByTestId("sales-cash-tendered");
    expect(cashValue.props.onChangeText).toBeUndefined();
    expect(cashValue.props.showSoftInputOnFocus).toBeUndefined();

    jest.useFakeTimers();
    try {
      expect(onManualInputFocusChange).toHaveBeenNthCalledWith(1, true);
      await fireEvent.press(screen.getByTestId("sales-cash-exact"));
      expect(
        screen.getByTestId("sales-cash-tendered").props.accessibilityValue,
      ).toEqual({ text: "9.95" });
      await fireEvent.press(screen.getByTestId("sales-cash-cancel"));
      await act(() => {
        jest.runOnlyPendingTimers();
      });
    } finally {
      jest.useRealTimers();
    }
    expect(onManualInputFocusChange).toHaveBeenNthCalledWith(1, true);
    expect(onManualInputFocusChange).toHaveBeenNthCalledWith(2, false);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("无码商品输入取消卸载时释放手动输入焦点", async () => {
    const onManualInputFocusChange = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    jest.useFakeTimers();
    try {
      await fireEvent.press(screen.getByTestId("sales-open-item-button"));
      await fireEvent.press(screen.getByText("取消"));
      await act(() => {
        jest.runOnlyPendingTimers();
      });
    } finally {
      jest.useRealTimers();
    }

    expect(onManualInputFocusChange).toHaveBeenNthCalledWith(1, true);
    expect(onManualInputFocusChange).toHaveBeenNthCalledWith(2, false);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("现金输入在关闭或成功卸载时释放手动输入焦点", async () => {
    const onManualInputFocusChange = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    jest.useFakeTimers();
    try {
      await openLegacyCash(salesPresenter);
      await fireEvent.press(screen.getByTestId("sales-cash-cancel"));
      await act(() => {
        jest.runOnlyPendingTimers();
      });

      await openLegacyCash(salesPresenter);
      await pressKeypadKeys(screen, "sales-cash", [
        "1",
        "0",
        "decimal",
        "0",
        "0",
      ]);
      await fireEvent.press(screen.getByTestId("sales-cash-confirm"));
      expect(await screen.findByTestId("sales-success")).toBeTruthy();
      await act(() => {
        jest.runOnlyPendingTimers();
      });
    } finally {
      jest.useRealTimers();
    }

    expect(onManualInputFocusChange.mock.calls).toEqual([
      [true],
      [false],
      [true],
      [false],
    ]);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("SalesScreen 卸载时释放仍占用的手动输入焦点", async () => {
    const onManualInputFocusChange = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent(screen.getByTestId("sales-search-input"), "focus");
    await screen.unmount();
    expect(onManualInputFocusChange.mock.calls).toEqual([[true], [false]]);

    salesPresenter.destroy();
  });

  it("购物车非空时只显示统一支付入口，离线仍可进入支付页", async () => {
    const amountCents = 997;
    const onOpenPayment = jest.fn();
    usePosShellStore.getState().setConnectivity("online");
    const salesPresenter = presenter(
      new ScreenCartPort(cartSnapshot(amountCents)),
    );
    const screen = await render(
      <SalesScreen
        locale="zh"
        onOpenPayment={onOpenPayment}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const paymentButton = screen.getByTestId("sales-open-payment");
    expect(paymentButton.props.accessibilityState).toMatchObject({
      disabled: false,
    });
    expect(paymentButton.props.accessibilityLabel).toBe(
      `跳转支付 · ${formatAud(amountCents, "zh")}`,
    );
    expect(
      StyleSheet.flatten(paymentButton.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    expect(screen.queryByTestId("sales-cash-checkout")).toBeNull();
    expect(screen.queryByTestId("sales-online-checkout")).toBeNull();
    await fireEvent.press(paymentButton);
    expect(onOpenPayment).toHaveBeenCalledTimes(1);
    expect(onOpenPayment).toHaveBeenLastCalledWith(
      expect.objectContaining({
        revision: 1,
        actualAmount: { currency: "AUD", cents: amountCents },
      }),
    );

    await act(async () => {
      salesPresenter.releasePreparedCheckout();
      usePosShellStore.getState().setConnectivity("offline");
      await Promise.resolve();
    });
    expect(
      screen.getByTestId("sales-open-payment").props.accessibilityState,
    ).toMatchObject({ disabled: false });
    await fireEvent.press(screen.getByTestId("sales-open-payment"));
    expect(onOpenPayment).toHaveBeenCalledTimes(2);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("目录核验使用轻量状态提示，结账等待时禁止扫码和重复结账", async () => {
    usePosShellStore.getState().setConnectivity("online");
    let resolveSettlement:
      ((result: Readonly<{ timedOut: boolean }>) => void) | undefined;
    const settlement = new Promise<Readonly<{ timedOut: boolean }>>(
      (resolve) => {
        resolveSettlement = resolve;
      },
    );
    let pendingCount = 1;
    const pendingListeners = new Set<() => void>();
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: {
        ...workflow(),
        getPendingCatalogWorkCount: () => pendingCount,
        subscribePendingCatalogWork(listener) {
          pendingListeners.add(listener);
          return () => pendingListeners.delete(listener);
        },
        settlePendingCatalogWork: () => settlement,
      },
    });
    const onOpenPayment = jest.fn();
    const screen = await render(
      <SalesScreen
        locale="zh"
        onOpenPayment={onOpenPayment}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.getByTestId("sales-catalog-verifying").props.children).toBe(
      "正在核验商品…",
    );
    await fireEvent.press(screen.getByTestId("sales-open-payment"));
    await act(async () => {
      await Promise.resolve();
    });

    expect(salesPresenter.getState().phase).toBe("verifying-checkout");
    expect(screen.getByTestId("sales-search-input").props.editable).toBe(false);
    expect(
      screen.getByTestId("sales-open-payment").props.accessibilityState,
    ).toMatchObject({ disabled: true });
    for (const testID of [
      "sales-line-line-1-decrease",
      "sales-line-line-1-increase",
      "sales-line-line-1-edit",
      "sales-line-line-1-discount",
      "sales-line-line-1-remove",
      "sales-order-discount",
      "sales-clear-cart",
    ]) {
      expect(
        screen.getByTestId(testID, { includeHiddenElements: true }).props
          .accessibilityState,
      ).toMatchObject({ disabled: true });
    }
    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    expect(flattenedStyle(screen.getByTestId("sales-hold"))).toMatchObject({
      opacity: 0.72,
    });
    await fireEvent.press(screen.getByTestId("sales-toolbar-close"));
    expect(screen.queryByTestId("sales-cash-modal")).toBeNull();

    await act(async () => {
      pendingCount = 0;
      pendingListeners.forEach((listener) => listener());
      resolveSettlement?.({ timedOut: false });
      await settlement;
    });
    expect(onOpenPayment).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId("sales-cash-modal")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("购物车行默认隐藏删除操作，左滑后才可删除", async () => {
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="en"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-line-line-1-increase"));
    expect(salesPresenter.getState().cart.lines[0]?.quantity).toBe("2");

    await fireEvent.press(screen.getByTestId("sales-line-line-1-discount"));
    expect(screen.getByTestId("sales-discount-modal")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("sales-discount-1000"));
    expect(cart.discounts).toEqual([{ lineId: "line-1", basisPoints: 1_000 }]);

    expect(
      descendantTestIds(screen.getByTestId("sales-line-line-1-controls")),
    ).not.toContain("sales-line-line-1-remove");
    const hiddenRemoveAction = screen.getByTestId(
      "sales-line-line-1-remove-action",
      { includeHiddenElements: true },
    );
    expect(hiddenRemoveAction.props.accessibilityElementsHidden).toBe(true);
    expect(hiddenRemoveAction.props.pointerEvents).toBe("none");

    const swipeSurface = screen.getByTestId("sales-line-line-1-swipe-surface");
    const startEvent = cartLinePanEvent(240, 240, 1);
    const activationEvent = cartLinePanEvent(240, 220, 20);
    const dragEvent = cartLinePanEvent(220, 100, 80);
    await act(async () => {
      swipeSurface.props.onStartShouldSetResponderCapture(startEvent);
      expect(
        swipeSurface.props.onMoveShouldSetResponderCapture(activationEvent),
      ).toBe(true);
      swipeSurface.props.onResponderGrant(activationEvent);
      swipeSurface.props.onResponderMove(dragEvent);
      swipeSurface.props.onResponderRelease(dragEvent);
    });
    const visibleRemoveAction = screen.getByTestId(
      "sales-line-line-1-remove-action",
    );
    expect(visibleRemoveAction.props.accessibilityElementsHidden).toBe(false);
    expect(visibleRemoveAction.props.pointerEvents).toBe("auto");
    expect(flattenedStyle(visibleRemoveAction)).toMatchObject({ zIndex: 1 });

    await fireEvent.press(screen.getByTestId("sales-line-line-1-remove"));
    expect(salesPresenter.getState().cart.lines).toHaveLength(0);

    salesPresenter.destroy();
    await screen.unmount();

    for (const rejectedEvent of [
      cartLinePanEvent(240, 265, 20),
      cartLinePanEvent(240, 236, 20, 100, 140),
    ]) {
      const rejectionPresenter = presenter(new ScreenCartPort(cartSnapshot()));
      const rejectionScreen = await render(
        <SalesScreen
          locale="en"
          presenter={rejectionPresenter}
          showStatusStrip={false}
        />,
      );
      expect(
        rejectionScreen
          .getByTestId("sales-line-line-1-swipe-surface")
          .props.onMoveShouldSetResponderCapture(rejectedEvent),
      ).toBe(false);
      rejectionPresenter.destroy();
      await rejectionScreen.unmount();
    }

    const disabledPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      capabilities: { ...ALL_CAPABILITIES, cartEditing: false },
    });
    const disabledScreen = await render(
      <SalesScreen
        locale="en"
        presenter={disabledPresenter}
        showStatusStrip={false}
      />,
    );
    expect(
      disabledScreen
        .getByTestId("sales-line-line-1-swipe-surface")
        .props.onMoveShouldSetResponderCapture(activationEvent),
    ).toBe(false);
    expect(
      disabledScreen.getByTestId("sales-line-line-1-remove-action", {
        includeHiddenElements: true,
      }).props.accessibilityElementsHidden,
    ).toBe(true);
    disabledPresenter.destroy();
    await disabledScreen.unmount();
  });

  it("商品行预填值退格后继续逐位输入而不会覆盖整个值", async () => {
    const initial = cartSnapshot(1_234);
    const cart = new ScreenCartPort({
      ...initial,
      lines: initial.lines.map((line) => ({ ...line, quantity: "12" })),
    });
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-line-line-1-edit"));
    await pressKeypadKeys(screen, "sales-line-edit", ["backspace", "3"]);
    expect(
      screen.getByTestId("sales-line-edit-value").props.accessibilityValue,
    ).toEqual({ text: "13" });

    await fireEvent.press(screen.getByTestId("sales-line-edit-price"));
    await pressKeypadKeys(screen, "sales-line-edit", ["backspace", "5"]);
    expect(
      screen.getByTestId("sales-line-edit-value").props.accessibilityValue,
    ).toEqual({ text: "12.35" });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("OPENITEM、数量、价格和手动行折扣通过触控编辑器调用对应 Port", async () => {
    const cart = new ScreenCartPort(cartSnapshot(2_000));
    const addOpenItem = jest.fn(async (_unitPriceCents: number) => undefined);
    const salesPresenter = presenter(cart, {
      workflow: {
        ...workflow(),
        addOpenItem,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-open-item-button"));
    expect(screen.getByTestId("sales-open-item-modal")).toBeTruthy();
    await pressKeypadKeys(screen, "sales-open-item", [
      "1",
      "2",
      "decimal",
      "3",
      "4",
    ]);
    await fireEvent.press(screen.getByTestId("sales-open-item-confirm"));
    expect(addOpenItem).toHaveBeenCalledWith(1_234);

    await fireEvent.press(screen.getByTestId("sales-line-line-1-edit"));
    expect(screen.getByTestId("sales-line-edit-modal")).toBeTruthy();
    expect(
      screen.getByTestId("sales-line-edit-value").props.onChangeText,
    ).toBeUndefined();
    expect(screen.queryByTestId("sales-line-edit-key-decimal")).toBeNull();
    expect(screen.queryByTestId("sales-line-edit-key-quick-50")).toBeNull();
    await pressKeypadKeys(screen, "sales-line-edit", ["3"]);
    await fireEvent.press(screen.getByTestId("sales-line-edit-confirm"));

    await fireEvent.press(screen.getByTestId("sales-line-line-1-edit"));
    await fireEvent.press(screen.getByTestId("sales-line-edit-price"));
    await pressKeypadKeys(screen, "sales-line-edit", ["quick-99"]);
    expect(
      screen.getByTestId("sales-line-edit-value").props.accessibilityValue,
    ).toEqual({ text: "20.99" });
    await pressKeypadKeys(screen, "sales-line-edit", ["clear"]);
    await pressKeypadKeys(screen, "sales-line-edit", ["8", "quick-50"]);
    await fireEvent.press(screen.getByTestId("sales-line-edit-confirm"));

    await fireEvent.press(screen.getByTestId("sales-line-line-1-discount"));
    await fireEvent.press(screen.getByTestId("sales-line-discount-amount"));
    await pressKeypadKeys(screen, "sales-line-edit", [
      "1",
      "decimal",
      "2",
      "5",
    ]);
    await fireEvent.press(screen.getByTestId("sales-line-edit-confirm"));

    await fireEvent.press(screen.getByTestId("sales-line-line-1-discount"));
    await fireEvent.press(screen.getByTestId("sales-line-discount-percent"));
    await pressKeypadKeys(screen, "sales-line-edit", [
      "1",
      "2",
      "decimal",
      "5",
    ]);
    await fireEvent.press(screen.getByTestId("sales-line-edit-confirm"));

    expect(cart.edits).toEqual([
      { operation: "line-quantity", lineId: "line-1", value: 3 },
      { operation: "line-price", lineId: "line-1", value: 850 },
      {
        operation: "line-discount-amount",
        lineId: "line-1",
        value: 125,
      },
      {
        operation: "line-discount-percent",
        lineId: "line-1",
        value: 1_250,
      },
    ]);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("无码商品遇到终端取单恢复锁时在弹窗内引导前往挂单管理", async () => {
    const recoveryError = Object.assign(
      new Error(
        "Terminal recall recovery must be resolved before editing the cart.",
      ),
      { code: "ACTIVE_PRICING_CART_TERMINAL_RECOVERY_REQUIRED" },
    );
    const cart = new ScreenCartPort(cartSnapshot(2_000));
    const salesPresenter = presenter(cart, {
      workflow: {
        ...workflow(),
        async addOpenItem() {
          throw recoveryError;
        },
      },
    });
    const onOpenHeldOrders = jest.fn();
    const screen = await render(
      <SalesScreen
        locale="zh"
        onOpenHeldOrders={onOpenHeldOrders}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-open-item-button"));
    await pressKeypadKeys(screen, "sales-open-item", ["1"]);
    await fireEvent.press(screen.getByTestId("sales-open-item-confirm"));

    await waitFor(() =>
      expect(
        screen.getByTestId("sales-open-item-recovery-error").props.children,
      ).toBe(
        "此终端仍有一笔取单恢复未完成。请前往挂单管理，选择“恢复取单”或“退回待取”后再继续收银。",
      ),
    );
    await fireEvent.press(
      screen.getByTestId("sales-open-item-recovery-action"),
    );
    expect(onOpenHeldOrders).toHaveBeenCalledTimes(1);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("整单快捷/手动折扣和清空购物车均要求明确确认", async () => {
    const cart = new ScreenCartPort(cartSnapshot(2_000));
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="en"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    expect(screen.getByTestId("sales-order-discount-modal")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("sales-order-discount-2000"));

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    await fireEvent.press(screen.getByTestId("sales-order-discount-amount"));
    expect(
      screen.getByTestId("sales-order-edit-value").props.onChangeText,
    ).toBeUndefined();
    await pressKeypadKeys(screen, "sales-order-edit", [
      "3",
      "decimal",
      "0",
      "0",
    ]);
    await fireEvent.press(screen.getByTestId("sales-order-edit-confirm"));

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    await fireEvent.press(screen.getByTestId("sales-order-discount-percent"));
    await pressKeypadKeys(screen, "sales-order-edit", [
      "1",
      "2",
      "decimal",
      "5",
    ]);
    await fireEvent.press(screen.getByTestId("sales-order-edit-confirm"));

    await fireEvent.press(screen.getByTestId("sales-clear-cart"));
    expect(screen.getByTestId("sales-clear-cart-modal")).toBeTruthy();
    await fireEvent.press(screen.getByTestId("sales-clear-cart-cancel"));
    expect(cart.edits).toHaveLength(3);

    await fireEvent.press(screen.getByTestId("sales-clear-cart"));
    await fireEvent.press(screen.getByTestId("sales-clear-cart-confirm"));
    expect(cart.edits).toEqual([
      { operation: "order-discount-quick", value: 2_000 },
      { operation: "order-discount-amount", value: 300 },
      { operation: "order-discount-percent", value: 1_250 },
      { operation: "clear-cart" },
    ]);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("搜索、扫码回车、挂单和锁屏都只调用注入的业务 Port", async () => {
    const searchProducts = jest.fn(async (_query: string) => [
      {
        productCode: "P-SEARCH",
        itemNumber: "I-SEARCH",
        lookupCode: "930000000099",
        displayName: "Search result",
        unitPriceCents: 250,
      },
    ]);
    const addProduct = jest.fn(async () => undefined);
    const addByLookupCode = jest.fn(async (_lookupCode: string) => null);
    const holdCart = jest.fn(async () => undefined);
    const lockTerminal = jest.fn(async () => undefined);
    const onSwitchLanguage = jest.fn();
    const injectedWorkflow: SalesWorkflowPort = {
      ...workflow(),
      searchProducts,
      addProduct,
      addByLookupCode,
      holdCart,
      lockTerminal,
    };
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: injectedWorkflow,
    });
    const screen = await render(
      <SalesScreen
        locale="en"
        onSwitchLanguage={onSwitchLanguage}
        presenter={salesPresenter}
      />,
    );

    const searchInput = screen.getByTestId("sales-search-input");
    await fireEvent.changeText(searchInput, "milk");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(searchProducts).toHaveBeenCalledWith("milk");
    await waitFor(() => expect(addProduct).toHaveBeenCalledTimes(1));
    expect(screen.queryByTestId("sales-search-results-drawer")).toBeNull();

    await fireEvent.changeText(searchInput, "930000000099");
    await fireEvent(searchInput, "submitEditing");
    expect(addByLookupCode).toHaveBeenCalledWith("930000000099");

    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    await fireEvent.press(screen.getByTestId("sales-hold"));
    expect(holdCart).toHaveBeenCalledTimes(1);
    mockStatusStripProps = null;
    await fireEvent.press(screen.getByTestId("sales-toolbar"));
    await fireEvent.press(screen.getByTestId("sales-lock"));
    expect(lockTerminal).toHaveBeenCalledTimes(1);
    expect(await screen.findByTestId("sales-locked")).toBeTruthy();
    expect(screen.getByTestId("handheld-operational-strip")).toBeTruthy();
    expect(mockStatusStripProps).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("现金确认防重复提交，提交成功后显示找零并清空信号", async () => {
    let resolveCompletion: ((result: SalesCashCompletion) => void) | undefined;
    const pending = new Promise<SalesCashCompletion>((resolve) => {
      resolveCompletion = resolve;
    });
    const completeCash = jest.fn(() => pending);
    const onSwitchLanguage = jest.fn();
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart, {
      workflow: workflow(completeCash),
    });
    const screen = await render(
      <SalesScreen
        locale="en"
        onSwitchLanguage={onSwitchLanguage}
        presenter={salesPresenter}
      />,
    );

    await openLegacyCash(salesPresenter);
    expect(screen.getByTestId("sales-cash-modal")).toBeTruthy();
    await pressKeypadKeys(screen, "sales-cash", [
      "1",
      "0",
      "decimal",
      "0",
      "0",
    ]);
    const confirm = screen.getByTestId("sales-cash-confirm");
    await fireEvent.press(confirm);
    expect(
      screen.getByTestId("sales-cash-key-1").props.accessibilityState,
    ).toEqual({ disabled: true });
    await fireEvent.press(confirm);
    expect(completeCash).toHaveBeenCalledTimes(1);

    mockStatusStripProps = null;
    await act(async () => {
      resolveCompletion?.({
        completed: true,
        canClearCart: true,
        orderGuid: "order-ui-safe",
        cashDueCents: 995,
        changeCents: 5,
        postCommit: { drawerDisposition: "queued" },
      });
      await pending;
    });

    expect(await screen.findByTestId("sales-success")).toBeTruthy();
    expect(screen.getByTestId("handheld-operational-strip")).toBeTruthy();
    expect(mockStatusStripProps).toBeNull();
    expect(screen.getByText("$0.05")).toBeTruthy();
    expect(cart.clearSignals).toEqual(["order-ui-safe"]);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("320×568 成功页的找零卡可收缩，长金额仍完整可读", async () => {
    const compactMetrics = {
      width: 320,
      height: 568,
      scale: 2,
      fontScale: 1,
    };
    const defaultMetrics = {
      width: 390,
      height: 844,
      scale: 3,
      fontScale: 1,
    };
    await act(async () => {
      Dimensions.set({ window: compactMetrics, screen: compactMetrics });
    });
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart, {
      workflow: workflow(async () => ({
        completed: true,
        canClearCart: true,
        orderGuid: "order-compact-success",
        cashDueCents: 995,
        changeCents: 123_456_789,
        postCommit: { drawerDisposition: "queued" },
      })),
    });

    try {
      const screen = await render(
        <SalesScreen
          locale="en"
          presenter={salesPresenter}
          showStatusStrip={false}
        />,
      );
      await openLegacyCash(salesPresenter);
      await pressKeypadKeys(screen, "sales-cash", [
        "1",
        "0",
        "decimal",
        "0",
        "0",
      ]);
      await fireEvent.press(screen.getByTestId("sales-cash-confirm"));

      const changeAmount = await screen.findByText("$1,234,567.89");
      const changeCard = changeAmount.parent;
      expect(changeCard).not.toBeNull();
      const changeCardStyle = flattenedStyle(changeCard!);
      expect(changeCardStyle).toEqual(
        expect.objectContaining({ maxWidth: 360, width: "100%" }),
      );
      expect(changeCardStyle.minWidth).toBeUndefined();
      expect(changeAmount.props.adjustsFontSizeToFit).toBe(true);
      expect(changeAmount.props.minimumFontScale).toBe(0.7);
      expect(changeAmount.props.numberOfLines).toBe(1);
      expect(flattenedStyle(changeAmount).fontSize).toBeLessThanOrEqual(36);

      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      expect(flattenedStyle(changeCard!).paddingHorizontal).toBe(36);
      expect(flattenedStyle(changeAmount).fontSize).toBe(52);

      await screen.unmount();
    } finally {
      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      salesPresenter.destroy();
    }
  });

  it("现金交易已提交但钱箱权限被拒绝时显示非阻塞主管提示", async () => {
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart, {
      workflow: workflow(async () => ({
        completed: true,
        canClearCart: true,
        orderGuid: "order-drawer-denied",
        cashDueCents: 995,
        changeCents: 5,
        postCommit: { drawerDisposition: "permission-denied" },
      })),
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await openLegacyCash(salesPresenter);
    await pressKeypadKeys(screen, "sales-cash", [
      "1",
      "0",
      "decimal",
      "0",
      "0",
    ]);
    await fireEvent.press(screen.getByTestId("sales-cash-confirm"));

    expect(await screen.findByTestId("sales-drawer-warning")).toBeTruthy();
    expect(
      screen.getByText("钱箱未打开：当前收银员没有开钱箱权限，请联系主管。"),
    ).toBeTruthy();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("购物车行操作弹窗点击面板外遮罩关闭", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-line-line-1"));
    expect(screen.getByTestId("handheld-state-cart-item-actions")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("sales-cart-actions-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("handheld-state-cart-item-actions")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("无码商品弹窗点击面板外遮罩关闭", async () => {
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-open-item-button"));
    expect(screen.getByTestId("sales-open-item-modal")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("sales-open-item-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-open-item-modal")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("行折扣弹窗点击面板外遮罩关闭", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-line-line-1-discount"));
    expect(screen.getByTestId("sales-discount-modal")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("sales-discount-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-discount-modal")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("行编辑弹窗点击面板外遮罩关闭", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-line-line-1-edit"));
    expect(screen.getByTestId("sales-line-edit-modal")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("sales-line-edit-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-line-edit-modal")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("整单折扣弹窗点击面板外遮罩关闭", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    expect(screen.getByTestId("sales-order-discount-modal")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("sales-order-discount-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-order-discount-modal")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("整单编辑弹窗点击面板外遮罩关闭", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    await fireEvent.press(screen.getByTestId("sales-order-discount-amount"));
    expect(screen.getByTestId("sales-order-edit-modal")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("sales-order-edit-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-order-edit-modal")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("清空确认弹窗点击面板外遮罩只关闭不确认", async () => {
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.press(screen.getByTestId("sales-clear-cart"));
    expect(screen.getByTestId("sales-clear-cart-modal")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("sales-clear-cart-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-clear-cart-modal")).toBeNull();
    expect(cart.clearSignals).toEqual([]);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("现金弹窗非提交态点击面板外遮罩关闭", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await openLegacyCash(salesPresenter);
    expect(screen.getByTestId("sales-cash-modal")).toBeTruthy();

    await fireEvent.press(
      screen.getByTestId("sales-cash-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-cash-modal")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("现金弹窗提交中点击面板外遮罩保持打开", async () => {
    let resolveCompletion: ((result: SalesCashCompletion) => void) | undefined;
    const pending = new Promise<SalesCashCompletion>((resolve) => {
      resolveCompletion = resolve;
    });
    const completeCash = jest.fn(() => pending);
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart, {
      workflow: workflow(completeCash),
    });
    const screen = await render(
      <SalesScreen
        locale="en"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await openLegacyCash(salesPresenter);
    await pressKeypadKeys(screen, "sales-cash", [
      "1",
      "0",
      "decimal",
      "0",
      "0",
    ]);
    await fireEvent.press(screen.getByTestId("sales-cash-confirm"));
    expect(completeCash).toHaveBeenCalledTimes(1);

    await fireEvent.press(
      screen.getByTestId("sales-cash-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.getByTestId("sales-cash-modal")).toBeTruthy();

    await act(async () => {
      resolveCompletion?.({
        completed: true,
        canClearCart: true,
        orderGuid: "order-ui-safe",
        cashDueCents: 995,
        changeCents: 5,
        postCommit: { drawerDisposition: "queued" },
      });
      await pending;
    });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("HID 无回车扫码只提交追加条码并保留手动查询草稿", async () => {
    const addByLookupCode = jest.fn(
      async (
        _lookupCode: string,
        _options?: Readonly<{
          source?: "manual" | "hid" | "camera";
          timingId?: string;
        }>,
      ) => null,
    );
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: {
        ...workflow(),
        addByLookupCode,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    jest.useFakeTimers();
    try {
      await typeSalesSearchSequence(screen, ["A", "AB", "ABC"], 100);
      expect(screen.getByTestId("sales-search-input").props.value).toBe("ABC");

      await typeSalesSearchSequence(
        screen,
        ["ABC1", "ABC12", "ABC123", "ABC1234", "ABC12345", "ABC123456"],
        50,
      );
      expect(addByLookupCode).toHaveBeenCalledTimes(0);

      await act(async () => {
        jest.advanceTimersByTime(85);
      });
      expect(addByLookupCode).toHaveBeenCalledTimes(1);
      expect(addByLookupCode).toHaveBeenCalledWith(
        "123456",
        expect.objectContaining({ source: "hid" }),
      );
      expect(screen.getByTestId("sales-search-input").props.value).toBe("ABC");
    } finally {
      jest.useRealTimers();
      salesPresenter.destroy();
      await screen.unmount();
    }
  });

  it("Android DataWedge 整串写入搜索框后无需回车自动查询并保留草稿", async () => {
    const originalPlatform = Platform.OS;
    Object.defineProperty(Platform, "OS", {
      configurable: true,
      value: "android",
    });
    const addByLookupCode = jest.fn(
      async (
        _lookupCode: string,
        _options?: Readonly<{
          source?: "manual" | "hid" | "camera";
          timingId?: string;
        }>,
      ) => null,
    );
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: {
        ...workflow(),
        addByLookupCode,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    jest.useFakeTimers();
    try {
      await typeSalesSearchSequence(screen, ["A", "AB", "ABC"], 100);

      // DataWedge 关闭“字符作为事件发送”时会把完整条码一次写入当前输入框。
      await fireEvent.changeText(
        screen.getByTestId("sales-search-input"),
        "ABC123456",
      );
      expect(addByLookupCode).toHaveBeenCalledTimes(0);

      await act(async () => {
        jest.advanceTimersByTime(85);
      });
      expect(addByLookupCode).toHaveBeenCalledTimes(1);
      expect(addByLookupCode).toHaveBeenCalledWith(
        "123456",
        expect.objectContaining({ source: "hid" }),
      );
      expect(screen.getByTestId("sales-search-input").props.value).toBe("ABC");
    } finally {
      jest.useRealTimers();
      salesPresenter.destroy();
      await screen.unmount();
      Object.defineProperty(Platform, "OS", {
        configurable: true,
        value: originalPlatform,
      });
    }
  });

  it("HID 连续无回车扫码各提交一次且每次恢复同一手动草稿", async () => {
    const addByLookupCode = jest.fn(
      async (
        _lookupCode: string,
        _options?: Readonly<{
          source?: "manual" | "hid" | "camera";
          timingId?: string;
        }>,
      ) => null,
    );
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: {
        ...workflow(),
        addByLookupCode,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    jest.useFakeTimers();
    try {
      await typeSalesSearchSequence(screen, ["A", "AB", "ABC"], 100);

      for (const digit of ["1", "2"] as const) {
        await typeSalesSearchSequence(
          screen,
          [
            `ABC${digit}`,
            `ABC${digit.repeat(2)}`,
            `ABC${digit.repeat(3)}`,
            `ABC${digit.repeat(4)}`,
            `ABC${digit.repeat(5)}`,
            `ABC${digit.repeat(6)}`,
          ],
          50,
        );
        await act(async () => {
          jest.advanceTimersByTime(85);
        });
        expect(screen.getByTestId("sales-search-input").props.value).toBe(
          "ABC",
        );
      }

      expect(addByLookupCode.mock.calls).toEqual([
        ["111111", expect.objectContaining({ source: "hid" })],
        ["222222", expect.objectContaining({ source: "hid" })],
      ]);
    } finally {
      jest.useRealTimers();
      salesPresenter.destroy();
      await screen.unmount();
    }
  });

  it("HID 带回车立即提交且不因 idle 定时器重复加购", async () => {
    const addByLookupCode = jest.fn(
      async (
        _lookupCode: string,
        _options?: Readonly<{
          source?: "manual" | "hid" | "camera";
          timingId?: string;
        }>,
      ) => null,
    );
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: {
        ...workflow(),
        addByLookupCode,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    jest.useFakeTimers();
    try {
      await typeSalesSearchSequence(screen, ["A", "AB", "ABC"], 100);
      await typeSalesSearchSequence(
        screen,
        ["ABC1", "ABC12", "ABC123", "ABC1234", "ABC12345", "ABC123456"],
        50,
      );

      await act(async () => {
        fireEvent(screen.getByTestId("sales-search-input"), "submitEditing");
      });
      expect(addByLookupCode).toHaveBeenCalledTimes(1);
      expect(addByLookupCode).toHaveBeenCalledWith(
        "123456",
        expect.objectContaining({ source: "hid" }),
      );

      await act(async () => {
        jest.advanceTimersByTime(200);
      });
      expect(addByLookupCode).toHaveBeenCalledTimes(1);
      expect(screen.getByTestId("sales-search-input").props.value).toBe("ABC");
    } finally {
      jest.useRealTimers();
      salesPresenter.destroy();
      await screen.unmount();
    }
  });

  it("快速人工输入不足阈值时不误提交为 HID", async () => {
    const addByLookupCode = jest.fn(
      async (
        _lookupCode: string,
        _options?: Readonly<{
          source?: "manual" | "hid" | "camera";
          timingId?: string;
        }>,
      ) => null,
    );
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        addByLookupCode,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    jest.useFakeTimers();
    try {
      await typeSalesSearchSequence(
        screen,
        ["A", "AB", "ABC", "ABCD", "ABCDE", "ABCDEF"],
        100,
      );
      await act(async () => {
        jest.advanceTimersByTime(500);
      });
      expect(addByLookupCode).toHaveBeenCalledTimes(0);
      expect(screen.getByTestId("sales-search-input").props.value).toBe(
        "ABCDEF",
      );
    } finally {
      jest.useRealTimers();
      salesPresenter.destroy();
      await screen.unmount();
    }
  });

  it("尚未达到 HID 阈值的快速手动输入按回车时完整提交", async () => {
    const addByLookupCode = jest.fn(
      async (
        _lookupCode: string,
        _options?: Readonly<{
          source?: "manual" | "hid" | "camera";
          timingId?: string;
        }>,
      ) => null,
    );
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        addByLookupCode,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    jest.useFakeTimers();
    try {
      await typeSalesSearchSequence(screen, ["A", "AB", "ABC"], 50);
      await act(async () => {
        fireEvent(screen.getByTestId("sales-search-input"), "submitEditing");
      });

      expect(addByLookupCode).toHaveBeenCalledTimes(1);
      expect(addByLookupCode).toHaveBeenCalledWith("ABC");
    } finally {
      jest.useRealTimers();
      salesPresenter.destroy();
      await screen.unmount();
    }
  });

  it("尚未达到 HID 阈值的快速手动输入点击搜索时完整查询", async () => {
    const searchProducts = jest.fn(async (_query: string) => []);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        searchProducts,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    jest.useFakeTimers();
    try {
      await typeSalesSearchSequence(screen, ["A", "AB", "ABC"], 50);
      await fireEvent.press(screen.getByTestId("sales-search-button"));

      expect(searchProducts).toHaveBeenCalledWith("ABC");
    } finally {
      jest.useRealTimers();
      salesPresenter.destroy();
      await screen.unmount();
    }
  });

  it("blur 丢弃未确认的半码并恢复扫描前草稿", async () => {
    const addByLookupCode = jest.fn(
      async (
        _lookupCode: string,
        _options?: Readonly<{
          source?: "manual" | "hid" | "camera";
          timingId?: string;
        }>,
      ) => null,
    );
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()), {
      workflow: {
        ...workflow(),
        addByLookupCode,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    jest.useFakeTimers();
    try {
      await typeSalesSearchSequence(screen, ["A", "AB", "ABC"], 100);
      await typeSalesSearchSequence(screen, ["ABC1", "ABC12"], 50);
      expect(screen.getByTestId("sales-search-input").props.value).toBe(
        "ABC12",
      );

      await act(async () => {
        fireEvent(screen.getByTestId("sales-search-input"), "blur");
      });
      expect(screen.getByTestId("sales-search-input").props.value).toBe("ABC");

      await act(async () => {
        jest.advanceTimersByTime(200);
      });
      expect(addByLookupCode).toHaveBeenCalledTimes(0);
    } finally {
      jest.useRealTimers();
      salesPresenter.destroy();
      await screen.unmount();
    }
  });

  it("手动搜索提交前恢复隐藏 HID 并结束手动输入", async () => {
    const onManualInputFocusChange = jest.fn();
    const searchProducts = jest.fn(async (_query: string) => []);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        searchProducts,
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent(screen.getByTestId("sales-search-input"), "focus");
    await fireEvent.changeText(
      screen.getByTestId("sales-search-input"),
      "奶",
    );
    await fireEvent.press(screen.getByTestId("sales-search-button"));

    expect(searchProducts).toHaveBeenCalledWith("奶");
    expect(
      screen.getByTestId("sales-search-input").props.showSoftInputOnFocus,
    ).toBe(false);
    await waitFor(() => {
      expect(onManualInputFocusChange).toHaveBeenLastCalledWith(false);
    });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("HID/相机成功反馈关闭搜索结果抽屉", async () => {
    let publishLookupOutcome: ((event: SalesFeedbackEvent) => void) | undefined;
    const searchProducts = jest.fn(async (_query: string) => [
      {
        productCode: "P-SCAN-1",
        itemNumber: "I-SCAN-1",
        lookupCode: "930000000101",
        displayName: "Scan result 1",
        unitPriceCents: 250,
      },
      {
        productCode: "P-SCAN-2",
        itemNumber: "I-SCAN-2",
        lookupCode: "930000000102",
        displayName: "Scan result 2",
        unitPriceCents: 350,
      },
    ]);
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        searchProducts,
        subscribeLookupOutcome(listener) {
          publishLookupOutcome = listener;
          return () => {
            publishLookupOutcome = undefined;
          };
        },
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.changeText(
      screen.getByTestId("sales-search-input"),
      "奶",
    );
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(screen.getByTestId("sales-search-results-drawer")).toBeTruthy();

    await act(async () => {
      publishLookupOutcome?.({
        kind: "added",
        lineId: "line-scan",
        source: "camera",
      });
    });
    expect(screen.queryByTestId("sales-search-results-drawer")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("TC26 窄屏一码多商品候选整行可点，列出名称、货号、编码、价格来源与价格，底部可取消", async () => {
    const tc26Metrics = { width: 360, height: 592, scale: 2, fontScale: 1 };
    const defaultMetrics = { width: 390, height: 844, scale: 3, fontScale: 1 };
    let publishSelection:
      | ((selection: SalesLookupSelection) => void)
      | null = null;
    let releaseAdd: (() => void) | null = null;
    const addLookupCandidate = jest.fn(
      async (
        _input: Readonly<{
          lookupCode: string;
          productCode: string;
          source?: "manual" | "hid" | "camera";
        }>,
      ) => {
        await new Promise<void>((resolve) => {
          releaseAdd = resolve;
        });
        return null;
      },
    );
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        subscribeLookupSelection(listener) {
          publishSelection = listener;
          return () => {
            publishSelection = null;
          };
        },
        addLookupCandidate,
      },
    });
    const selection: SalesLookupSelection = {
      selectionId: "lookup-1",
      lookupCode: "6405090401470",
      source: "hid",
      candidates: [
        {
          productCode: "P-FLY",
          itemNumber: "EXT-FLY",
          barcode: "9300000000017",
          lookupCode: "6405090401470",
          displayName: "EXTENSION Fly Swatter",
          unitPriceCents: 899,
          discountRate: null,
          priceSource: 2,
        },
        {
          productCode: "P-FLOWER",
          itemNumber: null,
          barcode: "6405090401470",
          lookupCode: "6405090401470",
          displayName: "flower",
          unitPriceCents: 299,
          discountRate: null,
          priceSource: 0,
        },
      ],
    };
    await act(async () => {
      Dimensions.set({ window: tc26Metrics, screen: tc26Metrics });
    });

    try {
      const screen = await render(
        <SalesScreen
          locale="zh"
          presenter={salesPresenter}
          showStatusStrip={false}
        />,
      );
      expect(screen.queryByTestId("sales-lookup-selection")).toBeNull();

      await act(async () => {
        publishSelection?.(selection);
      });

      expect(screen.getByTestId("sales-lookup-selection")).toBeTruthy();
      expect(screen.getByText("选择商品")).toBeTruthy();
      expect(
        screen.getByText(
          "编码 6405090401470 对应 2 个商品，请选择本次实际售出的商品。",
        ),
      ).toBeTruthy();
      expect(screen.getByText("货号 EXT-FLY")).toBeTruthy();
      expect(screen.getByText("货号 —")).toBeTruthy();
      expect(screen.getAllByText("编码 6405090401470")).toHaveLength(2);
      expect(
        screen.getByTestId("sales-lookup-candidate-P-FLY-source"),
      ).toHaveTextContent("套装");
      expect(
        screen.getByTestId("sales-lookup-candidate-P-FLOWER-price"),
      ).toHaveTextContent(formatAud(299, "zh"));
      const row = screen.getByTestId("sales-lookup-candidate-P-FLY");
      expect(flattenedStyle(row)).toMatchObject({ flexDirection: "row" });
      expect(
        Number(flattenedStyle(row).minHeight),
      ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
      expect(
        flattenedStyle(screen.getByTestId("sales-lookup-selection-cancel")),
      ).toMatchObject({ alignSelf: "stretch", minHeight: MIN_TOUCH_TARGET });

      await fireEvent.press(row);
      expect(addLookupCandidate).toHaveBeenCalledWith({
        lookupCode: "6405090401470",
        productCode: "P-FLY",
        source: "hid",
      });
      // 中文注释：加购完成前弹窗保持打开且候选不可再点。
      expect(screen.getByTestId("sales-lookup-selection")).toBeTruthy();
      expect(
        screen.getByTestId("sales-lookup-candidate-P-FLY").props.accessibilityState,
      ).toMatchObject({ disabled: true });
      await act(async () => {
        (releaseAdd as (() => void) | null)?.();
      });
      expect(screen.queryByTestId("sales-lookup-selection")).toBeNull();

      await act(async () => {
        publishSelection?.(selection);
      });
      await fireEvent.press(screen.getByTestId("sales-lookup-selection-cancel"));
      expect(screen.queryByTestId("sales-lookup-selection")).toBeNull();
      expect(addLookupCandidate).toHaveBeenCalledTimes(1);

      await screen.unmount();
    } finally {
      await act(async () => {
        Dimensions.set({ window: defaultMetrics, screen: defaultMetrics });
      });
      salesPresenter.destroy();
    }
  });
});
