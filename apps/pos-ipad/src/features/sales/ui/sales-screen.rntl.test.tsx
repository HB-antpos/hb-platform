import { afterEach, describe, expect, it, jest } from "@jest/globals";
import {
  act,
  fireEvent,
  render,
  waitFor,
} from "@testing-library/react-native";
import { FlatList, ScrollView, StyleSheet, TextInput } from "react-native";

import {
  EMPTY_SALE_CART,
  formatAud,
  MIN_TOUCH_TARGET,
  SalesPresenter,
  type SalesCapabilities,
  type SalesCartPort,
  type SalesCashCompletion,
  type SalesLookupSelection,
  type SalesProductSearchItem,
  type SalesWorkflowPort,
} from "./sales-presenter";
import { SalesScreen } from "./sales-screen";

import { createAud, type CartLine, type CartSnapshot } from "@/core/contracts";
import { usePosShellStore } from "@/ui/shell/pos-shell-store";
import { posColors } from "@/ui/theme";

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

let mockStatusStripProps: any;
let mockCameraModalProps: any;
let mockCameraInlineProps: any;

jest.mock("@/features/scanner-camera", () => {
  const { View } = jest.requireActual(
    "react-native",
  ) as typeof import("react-native");
  return {
    CameraScannerModal: (props: Readonly<{ visible: boolean }>) => {
      mockCameraModalProps = props;
      return props.visible ? <View testID="mock-camera-scanner-modal" /> : null;
    },
    CameraScannerInline: (props: unknown) => {
      mockCameraInlineProps = props;
      return <View testID="mock-camera-scanner-inline" />;
    },
  };
});

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
  mockCameraInlineProps = null;
  mockCameraModalProps = null;
  mockStatusStripProps = null;
  usePosShellStore.getState().reset();
  jest.restoreAllMocks();
});

describe("SalesScreen", () => {
  it("相机控制器未活动时把入口和单一模式开关排在同一行", async () => {
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const onManualInputFocusChange = jest.fn();
    const cameraScanner = {
      active: false,
      mode: "single" as const,
      scanner: {
        acceptCameraText: jest.fn(() => true),
        startCamera: jest.fn(async () => undefined),
        stopCamera: jest.fn(async () => undefined),
      },
      onModeChange: jest.fn(),
      onOpen: jest.fn(),
      onClose: jest.fn(),
      onScan: jest.fn(() => true),
    };
    const screen = await render(
      <SalesScreen
        cameraScanner={cameraScanner}
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        onOpenSpecialProducts={() => undefined}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const cameraRow = screen.getByTestId("sales-camera-scanner-row");
    const cameraAction = screen.getByTestId("sales-open-camera-scanner");
    const searchAction = screen.getByTestId("sales-search-button");
    const searchInputRow = screen.getByTestId("sales-search-input-row");
    const keyboardAction = screen.getByTestId("sales-show-keyboard");
    const openItemAction = screen.getByTestId("sales-open-item-button");
    const specialProductsAction = screen.getByTestId(
      "sales-open-special-products",
    );
    const modeControl = screen.getByTestId("sales-camera-mode-control");
    const modeToggle = screen.getByTestId("sales-camera-mode-toggle");
    expect(modeToggle.props.disabled).toBe(false);
    expect(modeToggle.props.value).toBe(false);
    expect(modeToggle.props.accessibilityValue).toEqual({ text: "单次" });
    expect(flattenedStyle(cameraRow).flexDirection).toBe("row");
    expect(flattenedStyle(modeControl).minHeight).toBeGreaterThanOrEqual(
      MIN_TOUCH_TARGET,
    );
    expect(flattenedStyle(modeControl).alignItems).toBe("center");
    expect(flattenedStyle(modeControl).justifyContent).toBe("center");
    expect(flattenedStyle(modeControl).flexShrink).toBe(1);
    expect(flattenedStyle(cameraAction).flexBasis).toBe("47%");
    expect(flattenedStyle(modeControl).flexBasis).toBe(
      flattenedStyle(cameraAction).flexBasis,
    );
    expect(flattenedStyle(modeControl).flexGrow).toBe(
      flattenedStyle(cameraAction).flexGrow,
    );
    expect(flattenedStyle(searchAction)).toMatchObject({
      backgroundColor: posColors.surface,
      borderColor: posColors.ink,
      height: 48,
      width: 48,
    });
    expect(flattenedStyle(keyboardAction)).toMatchObject({
      backgroundColor: posColors.surface,
      borderColor: posColors.ink,
      height: 48,
      width: 48,
    });
    expect(screen.getByTestId("sales-keyboard-icon").props.children).toBe(
      "keyboard-outline",
    );
    expect(descendantTestIds(searchInputRow)).toEqual([
      "sales-search-input-row",
      "sales-search-input",
      "sales-search-button",
      "sales-search-icon",
      "sales-show-keyboard",
      "sales-keyboard-icon",
    ]);
    expect(screen.queryByTestId("sales-add-code-button")).toBeNull();
    expect(flattenedStyle(openItemAction)).toMatchObject({
      backgroundColor: posColors.yellowSoft,
      borderColor: posColors.yellow,
    });
    expect(flattenedStyle(specialProductsAction)).toMatchObject({
      backgroundColor: posColors.blueSoft,
      borderColor: posColors.blue,
    });
    expect(flattenedStyle(modeToggle).alignSelf).toBe("center");
    expect(
      descendantTestIds(cameraRow).filter((testID) =>
        [
          "sales-open-camera-scanner",
          "sales-camera-mode-toggle",
        ].includes(testID),
      ),
    ).toEqual([
      "sales-open-camera-scanner",
      "sales-camera-mode-toggle",
    ]);
    expect(
      descendantTestIds(modeControl).filter((testID) =>
        [
          "sales-camera-mode-toggle",
          "sales-camera-mode-continuous-label",
        ].includes(testID),
      ),
    ).toEqual([
      "sales-camera-mode-toggle",
      "sales-camera-mode-continuous-label",
    ]);
    expect(screen.queryByText("单次")).toBeNull();
    const continuousLabel = screen.getByTestId(
      "sales-camera-mode-continuous-label",
    );
    expect(continuousLabel.props).toMatchObject({
      adjustsFontSizeToFit: true,
      minimumFontScale: 0.75,
      numberOfLines: 1,
    });
    expect(screen.getByText("连续")).toBeTruthy();
    expect(screen.getByText("相机扫码")).toBeTruthy();
    expect(screen.queryByTestId("sales-camera-mode-single")).toBeNull();
    expect(screen.queryByTestId("sales-camera-mode-continuous")).toBeNull();

    await fireEvent(modeToggle, "valueChange", true);
    expect(cameraScanner.onModeChange).toHaveBeenCalledWith("continuous");

    await screen.rerender(
      <SalesScreen
        cameraScanner={{ ...cameraScanner, mode: "continuous" }}
        locale="zh"
        onManualInputFocusChange={onManualInputFocusChange}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );
    const continuousToggle = screen.getByTestId("sales-camera-mode-toggle");
    expect(continuousToggle.props.value).toBe(true);
    expect(continuousToggle.props.accessibilityValue).toEqual({ text: "连续" });
    await fireEvent(continuousToggle, "valueChange", false);
    expect(cameraScanner.onModeChange).toHaveBeenLastCalledWith("single");

    await fireEvent(screen.getByTestId("sales-search-input"), "focus");
    jest.useFakeTimers();
    try {
      await fireEvent.press(screen.getByTestId("sales-open-camera-scanner"));
      await act(() => {
        jest.runOnlyPendingTimers();
      });
    } finally {
      jest.useRealTimers();
    }
    expect(cameraScanner.onOpen).toHaveBeenCalledTimes(1);
    expect(onManualInputFocusChange.mock.calls).toEqual([[true], [false]]);
    expect(screen.queryByTestId("mock-camera-scanner-modal")).toBeNull();
    expect(screen.queryByTestId("mock-camera-scanner-inline")).toBeNull();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("目录操作被门禁时同步禁用相机模式和入口", async () => {
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      capabilities: { ...ALL_CAPABILITIES, catalog: false },
    });
    const cameraScanner = {
      active: false,
      mode: "continuous" as const,
      scanner: {
        acceptCameraText: jest.fn(() => true),
        startCamera: jest.fn(async () => undefined),
        stopCamera: jest.fn(async () => undefined),
      },
      onModeChange: jest.fn(),
      onOpen: jest.fn(),
      onClose: jest.fn(),
      onScan: jest.fn(() => true),
    };
    const screen = await render(
      <SalesScreen
        cameraScanner={cameraScanner}
        locale="en"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.queryByText("Single")).toBeNull();
    expect(screen.getByText("Continuous")).toBeTruthy();
    expect(screen.getByText("Camera scanner")).toBeTruthy();
    expect(
      screen.queryByText(
        'Tapping the search field keeps HID-only input and submits scans with Enter. For touch or Chinese input, tap the "Keyboard" button above.',
      ),
    ).toBeNull();
    expect(
      screen.getByTestId("sales-open-camera-scanner").props.accessibilityState,
    ).toMatchObject({ disabled: true });
    expect(screen.getByTestId("sales-camera-mode-toggle").props.disabled).toBe(
      true,
    );

    await fireEvent(
      screen.getByTestId("sales-camera-mode-toggle"),
      "valueChange",
      false,
    );
    await fireEvent.press(screen.getByTestId("sales-open-camera-scanner"));
    expect(cameraScanner.onModeChange).not.toHaveBeenCalled();
    expect(cameraScanner.onOpen).not.toHaveBeenCalled();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("单次活动会话保留商品录入控件并显示相机覆盖层", async () => {
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const cameraScanner = {
      active: true,
      mode: "single" as const,
      scanner: {
        acceptCameraText: jest.fn(() => true),
        startCamera: jest.fn(async () => undefined),
        stopCamera: jest.fn(async () => undefined),
      },
      onModeChange: jest.fn(),
      onOpen: jest.fn(),
      onClose: jest.fn(),
      onScan: jest.fn(() => true),
    };
    const screen = await render(
      <SalesScreen
        cameraScanner={cameraScanner}
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.getByTestId("sales-search-input")).toBeTruthy();
    expect(screen.getByTestId("sales-transaction-functions")).toBeTruthy();
    expect(screen.queryByTestId("sales-camera-mode-toggle")).toBeNull();
    expect(screen.queryByTestId("sales-open-camera-scanner")).toBeNull();
    expect(screen.getByTestId("mock-camera-scanner-modal")).toBeTruthy();
    expect(screen.queryByTestId("mock-camera-scanner-inline")).toBeNull();
    expect(mockCameraModalProps).toMatchObject({
      context: "product",
      onClose: cameraScanner.onClose,
      onScan: cameraScanner.onScan,
      scanner: cameraScanner.scanner,
      visible: true,
    });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("连续活动会话仅替换商品录入控件并保留购物车和交易功能", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const onOpenPayment = jest.fn();
    const onReprintReceipt = jest.fn(async () => ({
      kind: "completed" as const,
    }));
    const onOpenCashDrawer = jest.fn(async () => ({
      kind: "completed" as const,
    }));
    const cameraScanner = {
      active: true,
      mode: "continuous" as const,
      scanner: {
        acceptCameraText: jest.fn(() => true),
        startCamera: jest.fn(async () => undefined),
        stopCamera: jest.fn(async () => undefined),
      },
      onModeChange: jest.fn(),
      onOpen: jest.fn(),
      onClose: jest.fn(),
      onScan: jest.fn(() => true),
    };
    const screen = await render(
      <SalesScreen
        cameraScanner={cameraScanner}
        locale="zh"
        onOpenCashDrawer={onOpenCashDrawer}
        onOpenPayment={onOpenPayment}
        onReprintReceipt={onReprintReceipt}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.queryByTestId("sales-search-input")).toBeNull();
    expect(screen.queryByTestId("sales-search-button")).toBeNull();
    expect(screen.queryByTestId("sales-camera-mode-toggle")).toBeNull();
    expect(screen.queryByTestId("sales-open-camera-scanner")).toBeNull();
    expect(screen.queryByTestId("mock-camera-scanner-modal")).toBeNull();
    expect(screen.getByTestId("mock-camera-scanner-inline")).toBeTruthy();
    expect(screen.getByTestId("sales-transaction-pane")).toBeTruthy();
    expect(screen.getByTestId("sales-cart-list")).toBeTruthy();
    expect(screen.getByTestId("sales-transaction-functions")).toBeTruthy();
    for (const testID of [
      "sales-hold",
      "sales-reprint-receipt",
      "sales-open-cash-drawer",
      "sales-open-payment",
    ]) {
      expect(screen.getByTestId(testID).props.accessibilityState).toMatchObject({
        disabled: true,
      });
    }
    expect(mockCameraInlineProps).toMatchObject({
      context: "product",
      onClose: cameraScanner.onClose,
      onScan: cameraScanner.onScan,
      scanner: cameraScanner.scanner,
      visible: true,
    });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("相机会话活动时离开 selling 会立即请求关闭", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const cameraScanner = {
      active: true,
      mode: "continuous" as const,
      scanner: {
        acceptCameraText: jest.fn(() => true),
        startCamera: jest.fn(async () => undefined),
        stopCamera: jest.fn(async () => undefined),
      },
      onModeChange: jest.fn(),
      onOpen: jest.fn(),
      onClose: jest.fn(),
      onScan: jest.fn(() => true),
    };
    const screen = await render(
      <SalesScreen
        cameraScanner={cameraScanner}
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await openLegacyCash(salesPresenter);

    await waitFor(() => expect(cameraScanner.onClose).toHaveBeenCalledTimes(1));
    salesPresenter.destroy();
    await screen.unmount();
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

  it("所有销售弹窗只声明应用支持的横屏方向", async () => {
    const salesPresenter = presenter(new ScreenCartPort(cartSnapshot()));
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const expectLandscapeModal = () => {
      const modalProps = findRenderedNodesByType(screen.toJSON(), "Modal");
      expect(modalProps).toHaveLength(1);
      expect(modalProps[0]?.supportedOrientations).toEqual([
        "landscape-left",
        "landscape-right",
      ]);
      return modalProps[0]!;
    };
    const dismissVisibleModal = async () => {
      const onRequestClose = expectLandscapeModal().onRequestClose;
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
    expectLandscapeModal();
    await fireEvent.press(screen.getByTestId("sales-line-discount-amount"));
    await dismissVisibleModal();

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    expectLandscapeModal();
    await fireEvent.press(screen.getByTestId("sales-order-discount-amount"));
    await dismissVisibleModal();

    await fireEvent.press(screen.getByTestId("sales-clear-cart"));
    await dismissVisibleModal();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("语言切换入口移入状态条并从销售工具栏移除", async () => {
    const onSwitchLanguage = jest.fn();
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART));
    const screen = await render(
      <SalesScreen
        locale="zh"
        onSwitchLanguage={onSwitchLanguage}
        presenter={salesPresenter}
      />,
    );

    expect(screen.queryByTestId("sales-switch-language")).toBeNull();
    expect(mockStatusStripProps).toMatchObject({
      language: "zh",
      onSwitchLanguage,
      showTerminalIdentity: true,
    });
    mockStatusStripProps.onSwitchLanguage();
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
    expect(englishScreen.queryByTestId("sales-switch-language")).toBeNull();
    expect(mockStatusStripProps).toMatchObject({
      language: "en",
      onSwitchLanguage,
      showTerminalIdentity: true,
    });
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

  it("分期管理迁入工具栏，本机历史占用原功能区位置，并保持 44pt 触控目标", async () => {
    const onOpenHeldOrders = jest.fn();
    const onOpenDailyClose = jest.fn();
    const onOpenLocalHistory = jest.fn();
    const onOpenRemoteHistory = jest.fn();
    const onOpenReturns = jest.fn();
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
        onOpenDailyClose={onOpenDailyClose}
        onOpenHeldOrders={onOpenHeldOrders}
        onOpenInstallments={onOpenInstallments}
        onOpenLocalHistory={onOpenLocalHistory}
        onOpenRemoteHistory={onOpenRemoteHistory}
        onOpenReturns={onOpenReturns}
        onOpenSettings={onOpenSettings}
        onOpenSpecialProducts={onOpenSpecialProducts}
        onOpenSyncHistory={onOpenSyncHistory}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    const heldOrders = screen.getByTestId("sales-open-held-orders");
    expect(screen.getByText("挂单管理")).toBeTruthy();
    expect(screen.queryByTestId("sales-open-held-orders-layout")).toBeNull();
    expect(
      StyleSheet.flatten(heldOrders.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(heldOrders);
    expect(onOpenHeldOrders).toHaveBeenCalledTimes(1);

    const dailyClose = screen.getByTestId("sales-open-daily-close");
    expect(screen.getByText("日结")).toBeTruthy();
    expect(
      StyleSheet.flatten(dailyClose.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(dailyClose);
    expect(onOpenDailyClose).toHaveBeenCalledTimes(1);

    const returns = screen.getByTestId("sales-open-returns");
    expect(screen.getByText("退货")).toBeTruthy();
    expect(screen.queryByTestId("sales-open-returns-layout")).toBeNull();
    expect(
      StyleSheet.flatten(returns.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(returns);
    expect(onOpenReturns).toHaveBeenCalledTimes(1);

    const remoteHistory = screen.getByTestId("sales-open-remote-history");
    expect(screen.getByText("远程历史")).toBeTruthy();
    expect(
      StyleSheet.flatten(remoteHistory.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(remoteHistory);
    expect(onOpenRemoteHistory).toHaveBeenCalledTimes(1);

    const specialProducts = screen.getByTestId("sales-open-special-products");
    expect(screen.getByText("特殊商品")).toBeTruthy();
    expect(
      StyleSheet.flatten(specialProducts.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(specialProducts);
    expect(onOpenSpecialProducts).toHaveBeenCalledTimes(1);

    const installments = screen.getByTestId("sales-open-installments");
    expect(screen.getByText("分期管理")).toBeTruthy();
    expect(screen.getByTestId("sales-open-installments-layout")).toBeTruthy();
    expect(
      StyleSheet.flatten(installments.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(installments);
    expect(onOpenInstallments).toHaveBeenCalledTimes(1);

    const localHistory = screen.getByTestId("sales-open-local-history");
    expect(screen.getByText("本机历史")).toBeTruthy();
    expect(screen.queryByTestId("sales-open-local-history-layout")).toBeNull();
    expect(
      StyleSheet.flatten(localHistory.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(localHistory);
    expect(onOpenLocalHistory).toHaveBeenCalledTimes(1);

    expect(
      descendantTestIds(screen.getByTestId("sales-toolbar")).filter(
        (testID) =>
          testID === "sales-open-remote-history" ||
          testID === "sales-open-installments",
      ),
    ).toEqual([
      "sales-open-remote-history",
      "sales-open-installments",
    ]);
    expect(
      descendantTestIds(
        screen.getByTestId("sales-transaction-functions"),
      ).filter(
        (testID) =>
          testID === "sales-open-returns" ||
          testID === "sales-open-local-history" ||
          testID === "sales-open-held-orders",
      ),
    ).toEqual([
      "sales-open-returns",
      "sales-open-local-history",
      "sales-open-held-orders",
    ]);

    const settings = screen.getByTestId("sales-open-settings");
    expect(screen.getByText("设置")).toBeTruthy();
    expect(
      StyleSheet.flatten(settings.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(settings);
    expect(onOpenSettings).toHaveBeenCalledTimes(1);

    const syncHistory = screen.getByTestId("sales-open-sync-history");
    expect(screen.getByText("同步历史")).toBeTruthy();
    expect(
      StyleSheet.flatten(syncHistory.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(syncHistory);
    expect(onOpenSyncHistory).toHaveBeenCalledTimes(1);

    const catalogMaintenance = screen.getByTestId(
      "sales-open-catalog-maintenance",
    );
    expect(screen.getByText("目录更新")).toBeTruthy();
    expect(
      StyleSheet.flatten(catalogMaintenance.props.style).minHeight,
    ).toBeGreaterThanOrEqual(MIN_TOUCH_TARGET);
    await fireEvent.press(catalogMaintenance);
    expect(onOpenCatalogMaintenance).toHaveBeenCalledTimes(1);

    expect(screen.queryByTestId("sales-open-camera-scanner")).toBeNull();
    expect(screen.getByTestId("sales-hold")).toBeTruthy();
    expect(screen.queryByTestId("sales-hold-layout")).toBeNull();
    expect(screen.getByTestId("sales-lock")).toBeTruthy();

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

    expect(
      withoutNavigation.queryByTestId("sales-open-held-orders"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-daily-close"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-remote-history"),
    ).toBeNull();
    expect(withoutNavigation.queryByTestId("sales-open-returns")).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-special-products"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-installments"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-local-history"),
    ).toBeNull();
    expect(withoutNavigation.queryByTestId("sales-open-settings")).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-sync-history"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-catalog-maintenance"),
    ).toBeNull();
    expect(
      withoutNavigation.queryByTestId("sales-open-camera-scanner"),
    ).toBeNull();

    noNavigationPresenter.destroy();
    await withoutNavigation.unmount();
  });

  it("横屏主工作区为双栏，购物车图片与交易汇总都属于当前交易区域", async () => {
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

    const transactionPane = screen.getByTestId("sales-transaction-pane");
    expect(screen.getByTestId("sales-function-pane")).toBeTruthy();
    expect(
      transactionPane.children.some(
        (child) =>
          typeof child !== "string" &&
          child.props.testID === "sales-summary-pane",
      ),
    ).toBe(true);

    const lineNumber = screen.getByTestId(
      "sales-line-line-1-line-number",
    );
    expect(lineNumber.props.accessibilityLabel).toBe("第 1 行");
    expect(lineNumber.props.children).toBe(1);
    expect(StyleSheet.flatten(lineNumber.props.style).width).toBe(24);

    const lineDiscount = screen.getByTestId(
      "sales-line-line-1-discount-amount",
    );
    expect(StyleSheet.flatten(lineDiscount.props.style).color).toBe(
      posColors.red,
    );
    const summaryDiscount = screen.getByTestId(
      "sales-summary-discount-amount",
    );
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

  it("购物车同时显示货号和目录条码，并在货号更新时刷新行内容", async () => {
    const initialCart = cartSnapshot();
    const cart = new ScreenCartPort({
      ...initialCart,
      lines: initialCart.lines.map((line) => ({
        ...line,
        lookupCode: "LOOKUP-001",
      })),
    });
    let completeProductDetails!: (details: {
      barcode: string | null;
      imageUri: string | null;
    }) => void;
    const pendingProductDetails = new Promise<{
      barcode: string | null;
      imageUri: string | null;
    }>((resolve) => {
      completeProductDetails = resolve;
    });
    const resolveCartProductDetails = jest.fn(
      (_input: { productCode: string; lookupCode: string }) =>
        pendingProductDetails,
    );
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        resolveCartProductDetails={resolveCartProductDetails}
        showStatusStrip={false}
      />,
    );

    await waitFor(() => {
      expect(resolveCartProductDetails).toHaveBeenCalledTimes(1);
    });
    expect(
      screen.getByTestId("sales-line-line-1-identifiers").props.children,
    ).toBe("货号：I-001 · 条码：—");
    await act(async () => {
      completeProductDetails({
        barcode: "930000000001",
        imageUri: null,
      });
      await pendingProductDetails;
    });
    await waitFor(() => {
      expect(
        screen.getByTestId("sales-line-line-1-identifiers").props.children,
      ).toBe("货号：I-001 · 条码：930000000001");
    });
    const identifiers = screen.getByTestId("sales-line-line-1-identifiers");
    expect(identifiers.props.numberOfLines).toBe(2);
    expect(resolveCartProductDetails).toHaveBeenCalledWith({
      productCode: "P-001",
      lookupCode: "LOOKUP-001",
    });

    await act(async () => {
      cart.publish({
        ...cart.snapshot,
        revision: cart.snapshot.revision + 1,
        lines: cart.snapshot.lines.map((line) => ({
          ...line,
          itemNumber: "I-002",
        })),
      });
    });
    await waitFor(() => {
      expect(
        screen.getByTestId("sales-line-line-1-identifiers").props.children,
      ).toBe("货号：I-002 · 条码：930000000001");
    });

    await screen.rerender(
      <SalesScreen
        locale="en"
        presenter={salesPresenter}
        resolveCartProductDetails={resolveCartProductDetails}
        showStatusStrip={false}
      />,
    );
    expect(
      screen.getByTestId("sales-line-line-1-identifiers").props.children,
    ).toBe("Item: I-002 · Barcode: 930000000001");
    expect(resolveCartProductDetails).toHaveBeenCalledTimes(1);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("目录确认没有独立条码后才回退显示售卖查询码", async () => {
    const initialCart = cartSnapshot();
    const cart = new ScreenCartPort({
      ...initialCart,
      lines: initialCart.lines.map((line) => ({
        ...line,
        lookupCode: "LOOKUP-001",
      })),
    });
    const salesPresenter = presenter(cart);
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        resolveCartProductDetails={async () => ({
          barcode: null,
          imageUri: null,
        })}
        showStatusStrip={false}
      />,
    );

    await waitFor(() => {
      expect(
        screen.getByTestId("sales-line-line-1-identifiers").props.children,
      ).toBe("货号：I-001 · 条码：LOOKUP-001");
    });

    salesPresenter.destroy();
    await screen.unmount();
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

    expect(flattenedStyle(screen.getByTestId("sales-line-positive"))).toMatchObject({
      backgroundColor: posColors.surface,
      borderColor: posColors.border,
    });
    expect(flattenedStyle(screen.getByTestId("sales-line-zero"))).toMatchObject({
      backgroundColor: posColors.yellowSoft,
      borderColor: posColors.yellow,
    });
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
    expect(flattenedStyle(screen.getByTestId("sales-line-zero"))).toMatchObject({
      backgroundColor: posColors.greenSoft,
      borderColor: posColors.green,
    });
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
      lines: [
        base.lines[0]!,
        { ...base.lines[0]!, lineId: "line-2" },
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

    expect(screen.getByTestId("sales-merge-cart").props.accessibilityState).toMatchObject({
      disabled: true,
    });

    cart.mergeAvailable = true;
    cart.mergeResult = {
      groups: [{ keptLineId: "line-1", removedLineIds: ["line-2"] }],
      removedLineCount: 1,
    };
    await act(async () => {
      cart.publish({ ...cart.snapshot, revision: cart.snapshot.revision + 1 });
    });
    expect(screen.getByTestId("sales-merge-cart").props.accessibilityState).toMatchObject({
      disabled: false,
    });

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
    const resolveCartProductImage = jest.fn(async () => null);
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
      lines: [
        base.lines[0]!,
        { ...base.lines[0]!, lineId: "line-2" },
      ],
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
          lines: [
            ...initial.lines,
            { ...base.lines[0]!, lineId: "line-3" },
          ],
        });
      });
      expect(scrollToIndex).toHaveBeenLastCalledWith({
        animated: true,
        index: 2,
        viewPosition: 1,
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
          barcode: "930000000001",
          lookupCode: "930000000001",
          displayName: "Fresh milk",
          unitPriceCents: 995,
          discountRate: null,
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
          barcode: "930000000001",
          lookupCode: "930000000001",
          displayName: "Fresh milk",
          unitPriceCents: 995,
          discountRate: null,
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
        barcode: "930000000099",
        lookupCode: "930000000099",
        displayName: "Search result",
        unitPriceCents: 250,
        discountRate: null,
      },
      {
        productCode: "P-OTHER",
        itemNumber: "I-OTHER",
        barcode: "930000000098",
        lookupCode: "930000000098",
        displayName: "Other result",
        unitPriceCents: 300,
        discountRate: null,
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
    expect(
      await screen.findByTestId("sales-search-results-drawer"),
    ).toBeTruthy();
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

  it("单一商品搜索结果直接加入购物车且不打开结果抽屉", async () => {
    let shouldFail = false;
    const addProduct = jest.fn(
      async (_product: SalesProductSearchItem) => {
        if (shouldFail) throw new Error("add failed");
      },
    );
    const searchProducts = jest.fn(async () => [
      {
        productCode: "P-SINGLE",
        itemNumber: "I-SINGLE",
        barcode: "930000000088",
        lookupCode: "930000000088",
        displayName: "Single result",
        unitPriceCents: 350,
        discountRate: 0.2,
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
    expect(addProduct).toHaveBeenCalledWith(
      expect.objectContaining({ lookupCode: "930000000088" }),
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

  it("多个商品搜索结果显示图片、货号、条码、零售价和折扣率", async () => {
    const searchProducts = jest.fn(async () => [
      {
        productCode: "P-DETAIL-A",
        itemNumber: "I-DETAIL-A",
        barcode: "930000000071",
        lookupCode: "LOOKUP-A",
        displayName: "Detailed product A",
        unitPriceCents: 250,
        discountRate: 0.2,
      },
      {
        productCode: "P-DETAIL-B",
        itemNumber: null,
        barcode: null,
        lookupCode: "LOOKUP-B",
        displayName: "Detailed product B",
        unitPriceCents: 500,
        discountRate: null,
      },
    ]);
    const resolveCartProductImage = jest.fn(async () =>
      "https://pos.example.test/images/detail-a.png",
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
        resolveCartProductImage={resolveCartProductImage}
        showStatusStrip={false}
      />,
    );

    await fireEvent.changeText(screen.getByTestId("sales-search-input"), "detail");
    await fireEvent.press(screen.getByTestId("sales-search-button"));

    expect(await screen.findByText("货号：I-DETAIL-A")).toBeTruthy();
    expect(screen.getByText("条码：930000000071")).toBeTruthy();
    expect(screen.getByText("零售价：$2.50")).toBeTruthy();
    expect(screen.getByText("折扣：20%")).toBeTruthy();
    expect(screen.getByText("货号：—")).toBeTruthy();
    expect(screen.getByText("条码：LOOKUP-B")).toBeTruthy();
    expect(screen.getByText("折扣：无")).toBeTruthy();
    await waitFor(() => {
      expect(
        screen.getByTestId("sales-product-P-DETAIL-A-image-content").props
          .source,
      ).toEqual({
        uri: "https://pos.example.test/images/detail-a.png",
      });
    });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("商品搜索没有结果时打开空结果抽屉", async () => {
    const salesPresenter = presenter(new ScreenCartPort(EMPTY_SALE_CART), {
      workflow: {
        ...workflow(),
        searchProducts: async () => [],
      },
    });
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    await fireEvent.changeText(screen.getByTestId("sales-search-input"), "不存在");
    await fireEvent.press(screen.getByTestId("sales-search-button"));

    expect(
      await screen.findByTestId("sales-search-results-drawer"),
    ).toBeTruthy();
    expect(screen.getByTestId("sales-search-results-empty")).toBeTruthy();
    expect(screen.getByText("没有匹配商品")).toBeTruthy();

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("搜索进行中修改输入会丢弃迟到结果且不会自动加入", async () => {
    let resolveSearch:
      | ((results: readonly SalesProductSearchItem[]) => void)
      | undefined;
    const pendingSearch = new Promise<readonly SalesProductSearchItem[]>(
      (resolve) => {
        resolveSearch = resolve;
      },
    );
    const addProduct = jest.fn(
      async (_product: SalesProductSearchItem) => undefined,
    );
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
          barcode: "930000000077",
          lookupCode: "930000000077",
          displayName: "Stale result",
          unitPriceCents: 700,
          discountRate: null,
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

  it("新搜索等待、失败或空白时不显示上一轮结果，并在抽屉提示必填", async () => {
    const resultA = {
      productCode: "P-A",
      itemNumber: "I-A",
      barcode: "BARCODE-A",
      lookupCode: "LOOKUP-A",
      displayName: "Product A",
      unitPriceCents: 100,
      discountRate: null,
    };
    const resultA2 = {
      ...resultA,
      productCode: "P-A2",
      itemNumber: "I-A2",
      barcode: "BARCODE-A2",
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
    expect(screen.queryByTestId("sales-search-results-drawer")).toBeNull();
    expect(screen.queryByTestId("sales-product-P-A-add")).toBeNull();

    await act(async () => {
      rejectSearchB?.(new Error("search B failed"));
      await Promise.resolve();
    });
    expect(screen.queryByTestId("sales-product-P-A-add")).toBeNull();
    await waitFor(() => {
      expect(
        screen.getAllByText("商品搜索失败，请重试。").length,
      ).toBeGreaterThan(0);
    });

    await fireEvent.press(screen.getByTestId("sales-search-results-close"));
    await fireEvent.changeText(searchInput, "");
    await fireEvent.press(screen.getByTestId("sales-search-button"));
    expect(screen.queryByTestId("sales-product-P-A-add")).toBeNull();
    await waitFor(() => {
      expect(
        screen.getAllByText("请输入条码、货号或商品名称。").length,
      ).toBeGreaterThan(0);
    });
    expect(searchProducts.mock.calls.map(([query]) => query)).toEqual(["A", "B"]);

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
    expect(
      withoutActions.getByTestId("sales-reprint-receipt").props
        .accessibilityState,
    ).toEqual({ disabled: true });
    expect(
      withoutActions.getByTestId("sales-open-cash-drawer").props
        .accessibilityState,
    ).toEqual({ disabled: true });
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

    const reprint = screen.getByTestId("sales-reprint-receipt");
    await fireEvent.press(reprint);
    await fireEvent.press(reprint);
    expect(onReprintReceipt).toHaveBeenCalledTimes(1);
    expect(
      screen.getByTestId("sales-open-cash-drawer").props.accessibilityState,
    ).toEqual({ disabled: true });
    await act(async () => {
      finishReprint?.({ kind: "completed" });
      await Promise.resolve();
    });
    expect(screen.getByText("上一张小票已发送到打印机。")).toBeTruthy();

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
        onOpenRequiredUpdate={onOpenRequiredUpdate}
        presenter={salesPresenter}
        showStatusStrip={false}
      />,
    );

    expect(screen.getByTestId("sales-new-transaction-gate")).toBeTruthy();
    expect(screen.getByTestId("sales-search-input").props.editable).toBe(false);
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

    expect(
      screen.getByText("完成 HB POS 更新后才能开始下一单"),
    ).toBeTruthy();
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
      expect(screen.getByTestId("sales-function-scroll").props).toMatchObject({
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
    expect(englishScreen.queryByText("Keyboard")).toBeNull();
    expect(
      englishScreen.getByTestId("sales-show-keyboard").props.accessibilityLabel,
    ).toBe("Keyboard");
    expect(
      englishScreen.getByTestId("sales-keyboard-icon").props.children,
    ).toBe("keyboard-outline");

    englishPresenter.destroy();
    await englishScreen.unmount();
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
      expect(
        textInputPrototype.blur.mock.invocationCallOrder[0],
      ).toBeLessThan(
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
      "sales-hold",
    ]) {
      expect(
        screen.getByTestId(testID, { includeHiddenElements: true }).props
          .accessibilityState,
      ).toMatchObject({ disabled: true });
    }
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
    expect(
      screen.getByTestId("sales-line-line-1-remove-action", {
        includeHiddenElements: true,
      }).props.accessibilityElementsHidden,
    ).toBe(true);

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
    expect(
      screen.getByTestId("sales-line-line-1-remove-action").props
        .accessibilityElementsHidden,
    ).toBe(false);

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
        rejectionScreen.getByTestId("sales-line-line-1-swipe-surface").props
          .onMoveShouldSetResponderCapture(rejectedEvent),
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
      disabledScreen.getByTestId("sales-line-line-1-swipe-surface").props
        .onMoveShouldSetResponderCapture(activationEvent),
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
      new Error("Terminal recall recovery must be resolved before editing the cart."),
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
        barcode: "930000000099",
        lookupCode: "930000000099",
        displayName: "Search result",
        unitPriceCents: 250,
        discountRate: null,
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

    await fireEvent.press(screen.getByTestId("sales-hold"));
    expect(holdCart).toHaveBeenCalledTimes(1);
    mockStatusStripProps = null;
    await fireEvent.press(screen.getByTestId("sales-lock"));
    expect(lockTerminal).toHaveBeenCalledTimes(1);
    expect(await screen.findByTestId("sales-locked")).toBeTruthy();
    expect(mockStatusStripProps).toMatchObject({
      language: "en",
      onSwitchLanguage,
      showTerminalIdentity: true,
    });

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
    expect(mockStatusStripProps).toMatchObject({
      language: "en",
      onSwitchLanguage,
      showTerminalIdentity: true,
    });
    expect(screen.getByText("$0.05")).toBeTruthy();
    expect(cart.clearSignals).toEqual(["order-ui-safe"]);

    salesPresenter.destroy();
    await screen.unmount();
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

  it("除现金提交中外，取消语义弹窗点击面板外遮罩即关闭且不触发确认", async () => {
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart);
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

    await fireEvent.press(screen.getByTestId("sales-open-item-button"));
    expect(screen.getByTestId("sales-open-item-modal")).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId("sales-open-item-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-open-item-modal")).toBeNull();

    await fireEvent.press(screen.getByTestId("sales-line-line-1-discount"));
    expect(screen.getByTestId("sales-discount-modal")).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId("sales-discount-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-discount-modal")).toBeNull();

    await fireEvent.press(screen.getByTestId("sales-line-line-1-edit"));
    expect(screen.getByTestId("sales-line-edit-modal")).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId("sales-line-edit-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-line-edit-modal")).toBeNull();

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    expect(screen.getByTestId("sales-order-discount-modal")).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId("sales-order-discount-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-order-discount-modal")).toBeNull();

    await fireEvent.press(screen.getByTestId("sales-order-discount"));
    await fireEvent.press(screen.getByTestId("sales-order-discount-amount"));
    expect(screen.getByTestId("sales-order-edit-modal")).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId("sales-order-edit-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-order-edit-modal")).toBeNull();

    await fireEvent.press(screen.getByTestId("sales-clear-cart"));
    expect(screen.getByTestId("sales-clear-cart-modal")).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId("sales-clear-cart-backdrop", {
        includeHiddenElements: true,
      }),
    );
    expect(screen.queryByTestId("sales-clear-cart-modal")).toBeNull();
    expect(cart.edits).toEqual([]);
    expect(cart.discounts).toEqual([]);

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("现金提交中点击遮罩不关闭，保留原有提交门禁", async () => {
    let resolveCompletion:
      | ((result: Awaited<ReturnType<SalesWorkflowPort["completeCash"]>>) => void)
      | undefined;
    const pending = new Promise<Awaited<
      ReturnType<SalesWorkflowPort["completeCash"]>
    >>((resolve) => {
      resolveCompletion = resolve;
    });
    const completeCash = jest.fn(() => pending);
    const cart = new ScreenCartPort(cartSnapshot());
    const salesPresenter = presenter(cart, {
      workflow: workflow(completeCash),
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
    expect(screen.getByTestId("sales-cash-modal")).toBeTruthy();
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
        orderGuid: "order-ui-submitting",
        cashDueCents: 995,
        changeCents: 5,
        postCommit: { drawerDisposition: "queued" },
      });
      await pending;
    });

    salesPresenter.destroy();
    await screen.unmount();
  });

  it("一码多商品候选弹窗列出名称、货号、编码、价格与价格来源，点选后加购所选商品", async () => {
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
    const resolveCartProductImage = jest.fn(
      async (input: Readonly<{ productCode: string }>) =>
        input.productCode === "P-FLOWER" ? "https://pos.example.test/flower.png" : null,
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
          itemNumber: "FLW-1",
          barcode: "6405090401470",
          lookupCode: "6405090401470",
          displayName: "flower",
          unitPriceCents: 299,
          discountRate: null,
          priceSource: 0,
        },
      ],
    };
    const screen = await render(
      <SalesScreen
        locale="zh"
        presenter={salesPresenter}
        resolveCartProductImage={resolveCartProductImage}
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
    expect(screen.getByText("EXTENSION Fly Swatter")).toBeTruthy();
    expect(screen.getByText("货号：FLW-1")).toBeTruthy();
    expect(screen.getAllByText("编码：6405090401470")).toHaveLength(2);
    expect(
      screen.getByTestId("sales-lookup-candidate-P-FLY-source"),
    ).toHaveTextContent("套装");
    expect(
      screen.getByTestId("sales-lookup-candidate-P-FLOWER-source"),
    ).toHaveTextContent("标准价");
    expect(
      screen.getByTestId("sales-lookup-candidate-P-FLY-price"),
    ).toHaveTextContent(formatAud(899, "zh"));
    await waitFor(() => {
      expect(
        screen.getByTestId("sales-lookup-candidate-P-FLOWER-image-content"),
      ).toBeTruthy();
    });

    await fireEvent.press(screen.getByTestId("sales-lookup-candidate-P-FLOWER"));

    expect(addLookupCandidate).toHaveBeenCalledWith({
      lookupCode: "6405090401470",
      productCode: "P-FLOWER",
      source: "hid",
    });
    // 中文注释：加购完成前弹窗保持打开且候选不可再点。
    expect(screen.getByTestId("sales-lookup-selection")).toBeTruthy();
    expect(
      screen.getByTestId("sales-lookup-candidate-P-FLOWER").props.accessibilityState,
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

    salesPresenter.destroy();
    await screen.unmount();
  });
});
