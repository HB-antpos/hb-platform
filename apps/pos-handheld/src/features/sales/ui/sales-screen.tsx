import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  memo,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type ReactNode,
} from "react";
import {
  ActivityIndicator,
  Animated,
  FlatList,
  Image,
  Modal,
  PanResponder,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
  type ListRenderItemInfo,
  type StyleProp,
  type ViewStyle,
  type TextInput as NativeTextInput,
} from "react-native";

import {
  drawerWarningCopyKey,
  resolveSalesLocale,
  salesText,
  type SalesCopyKey,
  type SalesLocale,
} from "./sales-copy";
import {
  createInitialSalesHidScanState,
  reduceSalesHidScanChange,
  SALES_HID_SUBMIT_IDLE_MS,
  type SalesHidScanIdleState,
} from "./sales-hid-scan-idle";
import {
  applySalesNumberKey,
  SalesNumberKeypad,
  type SalesNumberKey,
  type SalesNumberKeypadMode,
} from "./sales-number-keypad";
import {
  deriveCashDraft,
  formatAud,
  getCashDueCents,
  MIN_TOUCH_TARGET,
  parseCashInput,
  type SalesLookupCandidate,
  type SalesPresenter,
  type SalesFeedbackEvent,
  type SalesProductSearchItem,
} from "./sales-presenter";
import { SalesToolbar, type SalesToolbarAction } from "./sales-toolbar";
import {
  DEFAULT_SALES_TOOLBAR_ORDER,
  type SalesToolbarActionId,
} from "./sales-toolbar-order";

import type { CartLine, CartSnapshot } from "@/core/contracts";
import type { NewTransactionGate } from "@/core/contracts/app-updates";
import { scanTiming } from "@/features/sales/runtime/scan-timing";
import {
  PosKeyboardAwareFlatList,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPanResponderView } from "@/ui/controls/pos-pan-responder-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { HandheldActionButton } from "@/ui/handheld/handheld-actions";
import { HandheldStateSurface } from "@/ui/handheld/handheld-design-states";
import {
  HandheldPageHeader,
  HandheldScreenFrame,
  HandheldSection,
} from "@/ui/handheld/handheld-layout";
import {
  HandheldOperationalStrip,
  type HandheldOperationalItem,
} from "@/ui/handheld/handheld-operational-strip";
import { usePosShellStore } from "@/ui/shell/pos-shell-store";
import { posColors } from "@/ui/theme";

const QUICK_DISCOUNTS = [0, 1_000, 2_000, 3_000, 4_000, 5_000] as const;
const QUICK_ORDER_DISCOUNTS = [1_000, 2_000, 3_000, 4_000, 5_000] as const;
const AVERAGE_CART_LINE_HEIGHT = 128;
const CART_LINE_REMOVE_ACTION_WIDTH = 96;
const CART_LINE_SWIPE_ACTIVATION_DISTANCE = 12;
const COMPACT_CART_HEADER_HEIGHT = 24;
const COMPACT_PRODUCT_ENTRY_HEIGHT = 96;

type LineEditMode =
  "quantity" | "price" | "discount-amount" | "discount-percent";

type LineEditState = Readonly<{
  lineId: string;
  mode: LineEditMode;
  replaceOnNextDigit: boolean;
  value: string;
}>;

type OrderEditState = Readonly<{
  mode: "amount" | "percent";
  value: string;
}>;

export type SalesUtilityActionResult = Readonly<{
  kind:
    "completed" | "not-found" | "denied" | "unavailable" | "failed" | "unknown";
}>;

type SalesUtilityAction = "reprint" | "drawer";

export type SalesScreenProps = Readonly<{
  presenter: SalesPresenter;
  locale?: SalesLocale;
  newTransactionGate?: NewTransactionGate;
  resolveCartProductImage?: (input: {
    productCode: string;
    lookupCode: string;
  }) => Promise<string | null>;
  onOpenAttendanceAudit?: () => void;
  onOpenCashDrawer?: () => Promise<SalesUtilityActionResult>;
  onOpenCatalogMaintenance?: () => void;
  onOpenCameraScanner?: () => void;
  onOpenDailyClose?: () => void;
  onOpenHeldOrders?: () => void;
  onOpenInstallments?: () => void;
  onOpenLocalHistory?: () => void;
  onOpenPayment?: (cart: CartSnapshot) => void;
  onOpenRequiredUpdate?: () => void;
  onReprintReceipt?: () => Promise<SalesUtilityActionResult>;
  onOpenRemoteHistory?: () => void;
  onOpenReturns?: () => void;
  onOpenSettings?: () => void;
  onOpenSpecialProducts?: () => void;
  onOpenSyncHistory?: () => void;
  onSwitchLanguage?: () => void;
  onToolbarOrderChange?: (order: readonly SalesToolbarActionId[]) => void;
  onManualInputFocusChange?: (focused: boolean) => void;
  showStatusStrip?: boolean;
  toolbarOrder?: readonly SalesToolbarActionId[];
}>;

type ActionButtonProps = Readonly<{
  label: string;
  onPress(): void;
  disabled?: boolean;
  tone?: "primary" | "secondary" | "danger" | "info" | "quiet" | "warning";
  sound?: "tap" | "key" | "navigate" | "danger";
  testID?: string;
  style?: StyleProp<ViewStyle>;
  accessibilityLabel?: string;
}>;

type NumericValueDisplayProps = Readonly<{
  accessibilityLabel: string;
  currencyPrefix?: string;
  placeholder: string;
  testID: string;
  value: string;
}>;

export function SalesScreen({
  presenter,
  locale: localeOverride,
  newTransactionGate,
  resolveCartProductImage,
  onOpenAttendanceAudit,
  onOpenCashDrawer,
  onOpenCameraScanner,
  onOpenCatalogMaintenance,
  onOpenDailyClose,
  onOpenHeldOrders,
  onOpenInstallments,
  onOpenLocalHistory,
  onOpenPayment,
  onOpenRequiredUpdate,
  onReprintReceipt,
  onOpenRemoteHistory,
  onOpenReturns,
  onOpenSettings,
  onOpenSpecialProducts,
  onOpenSyncHistory,
  onSwitchLanguage,
  onToolbarOrderChange = () => undefined,
  onManualInputFocusChange,
  showStatusStrip = true,
  toolbarOrder = DEFAULT_SALES_TOOLBAR_ORDER,
}: SalesScreenProps) {
  const { height: windowHeight, width: windowWidth } = useWindowDimensions();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const connectivity = usePosShellStore((current) => current.connectivity);
  const pendingSync = usePosShellStore(
    (current) => current.pendingSync,
  );
  const printer = usePosShellStore((current) => current.printer);
  const scanner = usePosShellStore((current) => current.scanner);
  const terminalPresentation = usePosShellStore(
    (current) => current.terminalPresentation,
  );
  const locale = localeOverride ?? resolveSalesLocale();
  const t = useCallback(
    (key: SalesCopyKey, values?: Readonly<Record<string, string | number>>) =>
      salesText(locale, key, values),
    [locale],
  );
  const keypadLabels = {
    backspace: t("keypad.backspace"),
    clear: t("keypad.clear"),
    decimal: t("keypad.decimal"),
    quick50: t("keypad.quick50"),
    quick99: t("keypad.quick99"),
  };
  const [discountLineId, setDiscountLineId] = useState<string | null>(null);
  const [cartItemActionsLineId, setCartItemActionsLineId] = useState<
    string | null
  >(null);
  const [lastScanFeedback, setLastScanFeedback] =
    useState<SalesFeedbackEvent | null>(null);
  const [openItemVisible, setOpenItemVisible] = useState(false);
  const [openItemPrice, setOpenItemPrice] = useState("");
  const [lineEdit, setLineEdit] = useState<LineEditState | null>(null);
  const [orderDiscountVisible, setOrderDiscountVisible] = useState(false);
  const [orderEdit, setOrderEdit] = useState<OrderEditState | null>(null);
  const [clearCartVisible, setClearCartVisible] = useState(false);
  const [searchDrawerVisible, setSearchDrawerVisible] = useState(false);
  const [searchDrawerQuery, setSearchDrawerQuery] = useState("");
  const [searchResultsQuery, setSearchResultsQuery] = useState<string | null>(
    null,
  );
  const [combinedSearchPending, setCombinedSearchPending] = useState(false);
  const [utilityActionPending, setUtilityActionPending] =
    useState<SalesUtilityAction | null>(null);
  const [utilityActionResult, setUtilityActionResult] = useState<Readonly<{
    action: SalesUtilityAction;
    result: SalesUtilityActionResult;
  }> | null>(null);
  const [searchSoftInputOnFocus, setSearchSoftInputOnFocus] = useState(false);
  const searchInputRef = useRef<NativeTextInput>(null);
  const hidScanStateRef = useRef<SalesHidScanIdleState>(
    createInitialSalesHidScanState(state.query),
  );
  const hidIdleTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const cartListRef = useRef<FlatList<CartLine>>(null);
  const cartLinesRef = useRef(state.cart.lines);
  cartLinesRef.current = state.cart.lines;
  const cartRevealRetryTimerRef = useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  const cartRevealGenerationRef = useRef(0);
  const cartRevealIndexRef = useRef<number | null>(null);
  const searchKeyboardRequestPhaseRef = useRef<
    "idle" | "awaiting-blur" | "enabling" | "awaiting-focus"
  >("idle");
  const searchKeyboardRequestGenerationRef = useRef(0);
  const searchKeyboardTimerRef = useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  const manualInputFocusChangeRef = useRef(onManualInputFocusChange);
  const manualInputActiveRef = useRef(false);
  const numericInputModalActiveRef = useRef(false);
  const manualInputBlurTimerRef = useRef<ReturnType<typeof setTimeout> | null>(
    null,
  );
  const searchRequestGenerationRef = useRef(0);
  const combinedSearchPendingRef = useRef(false);
  const beginNumericInputRef = useRef<() => void>(() => undefined);
  manualInputFocusChangeRef.current = onManualInputFocusChange;
  const successDrawerWarning = state.success
    ? drawerWarningCopyKey(state.success.drawerDisposition)
    : null;
  const cartEmpty = state.cart.lines.length === 0;
  const displayedCartLines = useMemo(
    () => [...state.cart.lines].reverse(),
    [state.cart.lines],
  );
  const searchResultsAreCurrent =
    state.searchStatus === "ready" &&
    searchResultsQuery !== null &&
    searchResultsQuery === searchDrawerQuery &&
    state.query.trim() === searchDrawerQuery;
  const visibleSearchResults = searchResultsAreCurrent
    ? state.searchResults
    : [];
  const newTransactionBlocked =
    cartEmpty && newTransactionGate?.canStartNewTransaction === false;
  const checkoutVerifying = state.phase === "verifying-checkout";
  const transactionActionsDisabled = state.phase !== "selling";
  const mergeCartDisabled =
    transactionActionsDisabled || !presenter.canMergeCompatibleLines();
  const catalogActionsDisabled =
    !state.capabilities.catalog ||
    newTransactionBlocked ||
    transactionActionsDisabled;
  // 一码多商品候选只在收银态展示；结账准备、锁屏或成功页一律不弹出。
  const lookupSelection =
    state.phase === "selling" ? state.lookupSelection : null;
  const lookupCandidatesDisabled =
    !state.capabilities.catalog ||
    newTransactionBlocked ||
    state.lookupSelectionPending;
  const manualInputTreeUnavailable =
    state.phase === "success" ||
    state.phase === "locked" ||
    checkoutVerifying ||
    newTransactionBlocked;
  // TC26 的应用可用区为 360×592dp；低矮设备也采用同一紧凑销售布局。
  const compactSalesLayout = windowWidth <= 360 || windowHeight <= 640;
  const compactCartRowsMinHeight = Math.ceil(windowHeight * 0.5);
  const compactCartListMinHeight =
    compactCartRowsMinHeight +
    COMPACT_PRODUCT_ENTRY_HEIGHT +
    COMPACT_CART_HEADER_HEIGHT;
  // 窄屏成功页优先保证找零金额完整可读，390×844 及以上保持原有视觉尺度。
  const compactSuccessLayout = windowWidth <= 360 || windowHeight <= 640;
  const selectedCartActionLine = cartItemActionsLineId
    ? (state.cart.lines.find((line) => line.lineId === cartItemActionsLineId) ??
      null)
    : null;

  const operationalItems: readonly HandheldOperationalItem[] = [
    {
      key: "network",
      label: t("status.network"),
      tone:
        connectivity === "online"
          ? "success"
          : connectivity === "offline"
            ? "danger"
            : "neutral",
      value: t(`status.${connectivity}` as SalesCopyKey),
    },
    {
      key: "sync",
      label: t("status.sync"),
      tone:
        pendingSync.kind === "ready"
          ? pendingSync.count === 0
            ? "success"
            : "warning"
          : "neutral",
      value:
        pendingSync.kind === "ready"
          ? t("status.pending", { count: pendingSync.count })
          : t(
              pendingSync.kind === "checking"
                ? "status.checking"
                : "status.unavailable",
            ),
    },
    {
      key: "printer",
      label: t("status.printer"),
      tone:
        printer === "ready"
          ? "success"
          : printer === "failed"
            ? "danger"
            : printer === "connecting" || printer === "scanning"
              ? "warning"
              : "neutral",
      value: t(`status.${printer}` as SalesCopyKey),
    },
    {
      key: "scanner",
      label: t("status.scanner"),
      tone:
        scanner === "capturing"
          ? "success"
          : scanner === "camera"
            ? "info"
            : "neutral",
      value: t(`status.${scanner}` as SalesCopyKey),
    },
  ];
  const compactOperationalItems: readonly HandheldOperationalItem[] = [
    {
      key: "workspace",
      label: t("app.title"),
      tone: "info",
      value: terminalPresentation
        ? `${terminalPresentation.storeName?.trim() || "—"} · ${terminalPresentation.deviceCode}`
        : t("app.workspace"),
    },
    ...(showStatusStrip ? operationalItems : []),
    ...(connectivity === "offline"
      ? [
          {
            key: "offline-cash-only",
            label: t("summary.cashOnly"),
            tone: "warning" as const,
            value: t("summary.cashOnlyHint"),
          },
        ]
      : []),
    ...(state.pendingLookupCount > 0 || checkoutVerifying
      ? [
          {
            accessibilityLiveRegion: "polite" as const,
            key: "catalog-verifying",
            label: t("status.scanner"),
            tone: "warning" as const,
            value: t("catalog.verifying"),
          },
        ]
      : []),
    ...(lastScanFeedback
      ? [
          {
            accessibilityHint: t("common.dismiss"),
            accessibilityLiveRegion: "polite" as const,
            key: "scan-result",
            label: t("pda.scanResultTitle"),
            onPress: () => setLastScanFeedback(null),
            tone: "info" as const,
            value: t(`pda.scanResult.${lastScanFeedback.kind}` as SalesCopyKey),
          },
        ]
      : []),
    ...(utilityActionResult
      ? [
          {
            accessibilityHint: t("common.dismiss"),
            accessibilityLiveRegion: "polite" as const,
            key: "utility-result",
            label: t(
              utilityActionResult.action === "reprint"
                ? "functions.reprintReceipt"
                : "functions.openDrawer",
            ),
            onPress: () => setUtilityActionResult(null),
            tone:
              utilityActionResult.result.kind === "completed"
                ? ("success" as const)
                : ("warning" as const),
            value: t(
              utilityResultCopyKey(
                utilityActionResult.action,
                utilityActionResult.result.kind,
              ),
            ),
          },
        ]
      : []),
    ...(state.errorCode
      ? [
          {
            accessibilityHint: t("common.dismiss"),
            accessibilityLiveRegion: "assertive" as const,
            key: "sales-error",
            label: t("status.failed"),
            onPress: () => presenter.dismissError(),
            tone: "danger" as const,
            value: t(errorCopyKey(state.errorCode)),
    },
        ]
      : []),
  ];

  useEffect(
    () =>
      presenter.subscribeFeedback((event) => {
        if (event.source === "hid" || event.source === "camera") {
          setLastScanFeedback(event);
          if (event.kind === "added" || event.kind === "incremented") {
            setSearchDrawerVisible(false);
            setSearchResultsQuery(null);
          }
        }
      }),
    [presenter],
  );

  const retryCartReveal = useCallback(
    (info: Readonly<{ averageItemLength: number; index: number }>): void => {
      if (cartRevealIndexRef.current !== info.index) return;
      const revealGeneration = cartRevealGenerationRef.current;
      const averageItemLength =
        info.averageItemLength > 0
          ? info.averageItemLength
          : AVERAGE_CART_LINE_HEIGHT;
      cartListRef.current?.scrollToOffset({
        animated: false,
        offset: Math.max(0, averageItemLength * info.index),
      });
      if (cartRevealRetryTimerRef.current !== null) {
        clearTimeout(cartRevealRetryTimerRef.current);
      }
      cartRevealRetryTimerRef.current = setTimeout(() => {
        cartRevealRetryTimerRef.current = null;
        if (
          cartRevealGenerationRef.current !== revealGeneration ||
          cartRevealIndexRef.current !== info.index
        ) {
          return;
        }
        cartListRef.current?.scrollToIndex({
          animated: true,
          index: info.index,
          viewPosition: 0,
        });
      }, 32);
    },
    [],
  );

  useEffect(() => {
    cartRevealGenerationRef.current += 1;
    if (cartRevealRetryTimerRef.current !== null) {
      clearTimeout(cartRevealRetryTimerRef.current);
      cartRevealRetryTimerRef.current = null;
    }
    const lineId = state.revealLineId;
    if (!lineId) {
      cartRevealIndexRef.current = null;
      return;
    }
    const index = displayedCartLines.findIndex(
      (line) => line.lineId === lineId,
    );
    if (index < 0) {
      cartRevealIndexRef.current = null;
      return;
    }
    cartRevealIndexRef.current = index;
    cartListRef.current?.scrollToIndex({
      animated: true,
      index,
      viewPosition: 0,
    });
  }, [displayedCartLines, state.revealLineId, state.revealRevision]);

  useEffect(
    () => () => {
      if (cartRevealRetryTimerRef.current !== null) {
        clearTimeout(cartRevealRetryTimerRef.current);
      }
    },
    [],
  );

  const clearManualInputBlurTimer = (): void => {
    if (manualInputBlurTimerRef.current === null) return;
    clearTimeout(manualInputBlurTimerRef.current);
    manualInputBlurTimerRef.current = null;
  };

  const clearSearchKeyboardTimer = (): void => {
    if (searchKeyboardTimerRef.current === null) return;
    clearTimeout(searchKeyboardTimerRef.current);
    searchKeyboardTimerRef.current = null;
  };

  const clearHidIdleTimer = (): void => {
    if (hidIdleTimerRef.current === null) return;
    clearTimeout(hidIdleTimerRef.current);
    hidIdleTimerRef.current = null;
  };

  const submitPendingHidScan = (): void => {
    const current = hidScanStateRef.current;
    if (!current.confirmed || !current.pendingCode) return;
    const draft = current.baseline;
    const code = current.pendingCode;
    clearHidIdleTimer();
    hidScanStateRef.current = createInitialSalesHidScanState(draft);
    presenter.setQuery(draft);
    void presenter.addScannedLookupCode(code, "hid");
  };

  const scheduleHidScanSubmit = (): void => {
    clearHidIdleTimer();
    hidIdleTimerRef.current = setTimeout(() => {
      hidIdleTimerRef.current = null;
      submitPendingHidScan();
    }, SALES_HID_SUBMIT_IDLE_MS);
  };

  /** 丢弃未完成/未确认的扫码节奏，并恢复扫描前手动查询草稿。 */
  const discardHidScanDraft = (): void => {
    clearHidIdleTimer();
    const current = hidScanStateRef.current;
    const hadPartial =
      current.rapidStreak > 0 ||
      current.confirmed ||
      current.pendingCode !== null;
    if (hadPartial) {
      presenter.setQuery(current.baseline);
    }
    hidScanStateRef.current = createInitialSalesHidScanState(
      hadPartial ? current.baseline : presenter.getState().query,
    );
  };

  const handleSearchInputChange = (value: string): void => {
    // 可见查询框临时接管 HID 时，同样从最后一个字符开始计算端到端耗时。
    scanTiming.noteHidCharacter();
    hidScanStateRef.current = reduceSalesHidScanChange(
      hidScanStateRef.current,
      {
        value,
        nowMs: Date.now(),
        // Android DataWedge 默认可能整串写入；iOS 继续只按逐字符 HID 节奏识别。
        dataWedgeBatch: Platform.OS === "android",
      },
    );
    presenter.setQuery(value);
    if (hidScanStateRef.current.confirmed) {
      scheduleHidScanSubmit();
    } else {
      clearHidIdleTimer();
    }
  };

  const handleSearchInputSubmit = (): void => {
    const current = hidScanStateRef.current;
    if (current.confirmed && current.pendingCode) {
      clearHidIdleTimer();
      const draft = current.baseline;
      const code = current.pendingCode;
      hidScanStateRef.current = createInitialSalesHidScanState(draft);
      presenter.setQuery(draft);
      void presenter.addScannedLookupCode(code, "hid");
      return;
    }
    // 尚未确认成 HID 的快速输入仍是完整手动查询，不能按半码回滚。
    resetSearchInputToHidMode(false);
    searchInputRef.current?.blur();
    notifyManualInputBlurred();
    void presenter.addLookupCode();
  };

  const scheduleSearchKeyboardEnable = (requestGeneration: number): void => {
    clearSearchKeyboardTimer();
    searchKeyboardRequestPhaseRef.current = "enabling";
    searchKeyboardTimerRef.current = setTimeout(() => {
      searchKeyboardTimerRef.current = null;
      if (
        requestGeneration !== searchKeyboardRequestGenerationRef.current ||
        searchKeyboardRequestPhaseRef.current !== "enabling"
      ) {
        return;
      }
      setSearchSoftInputOnFocus(true);
    }, 0);
  };

  const requestSearchKeyboard = (): void => {
    const previousPhase = searchKeyboardRequestPhaseRef.current;
    discardHidScanDraft();
    clearSearchKeyboardTimer();
    const requestGeneration = searchKeyboardRequestGenerationRef.current + 1;
    searchKeyboardRequestGenerationRef.current = requestGeneration;
    const searchInput = searchInputRef.current;
    searchInput?.setNativeProps({ showSoftInputOnFocus: false });
    setSearchSoftInputOnFocus(false);
    if (searchInput?.isFocused()) {
      searchKeyboardRequestPhaseRef.current = "awaiting-blur";
      // 重复点击复用唯一在途 blur，避免多个迟到事件影响下一次焦点周期。
      if (previousPhase !== "awaiting-blur") {
        searchInput.blur();
      }
      return;
    }
    scheduleSearchKeyboardEnable(requestGeneration);
  };

  const notifyManualInputFocused = (): void => {
    clearManualInputBlurTimer();
    if (manualInputActiveRef.current) return;
    manualInputActiveRef.current = true;
    manualInputFocusChangeRef.current?.(true);
  };

  const notifyManualInputBlurred = (): void => {
    if (!manualInputActiveRef.current || manualInputBlurTimerRef.current)
      return;
    // TextInput 切换时先触发 blur 再触发 focus；延后一轮才释放 HID 捕获。
    manualInputBlurTimerRef.current = setTimeout(() => {
      manualInputBlurTimerRef.current = null;
      if (!manualInputActiveRef.current) return;
      manualInputActiveRef.current = false;
      manualInputFocusChangeRef.current?.(false);
    }, 0);
  };

  const resetSearchInputToHidMode = (
    discardPendingScan: boolean = true,
  ): void => {
    if (discardPendingScan) {
      discardHidScanDraft();
    } else {
      clearHidIdleTimer();
      hidScanStateRef.current = createInitialSalesHidScanState(
        presenter.getState().query,
      );
    }
    clearSearchKeyboardTimer();
    searchKeyboardRequestGenerationRef.current += 1;
    searchKeyboardRequestPhaseRef.current = "idle";
    searchInputRef.current?.setNativeProps({ showSoftInputOnFocus: false });
    setSearchSoftInputOnFocus(false);
  };

  const openCameraScanner = (): void => {
    resetSearchInputToHidMode();
    searchInputRef.current?.blur();
    // 相机路由接管扫码期间不保证触发 onBlur，主动释放手动输入焦点以恢复 HID 状态机。
    notifyManualInputBlurred();
    onOpenCameraScanner?.();
  };

  const beginNumericInput = (): void => {
    numericInputModalActiveRef.current = true;
    resetSearchInputToHidMode();
    searchInputRef.current?.blur();
    notifyManualInputFocused();
  };
  beginNumericInputRef.current = beginNumericInput;

  const handleSearchInputBlur = (): void => {
    discardHidScanDraft();
    if (searchKeyboardRequestPhaseRef.current === "awaiting-blur") {
      const requestGeneration = searchKeyboardRequestGenerationRef.current;
      searchInputRef.current?.setNativeProps({
        showSoftInputOnFocus: false,
      });
      setSearchSoftInputOnFocus(false);
      scheduleSearchKeyboardEnable(requestGeneration);
      return;
    }
    if (
      searchKeyboardRequestPhaseRef.current === "enabling" ||
      searchKeyboardRequestPhaseRef.current === "awaiting-focus"
    ) {
      return;
    }
    resetSearchInputToHidMode();
    if (!numericInputModalActiveRef.current) {
      notifyManualInputBlurred();
    }
  };

  const handleSearchInputFocus = (): void => {
    if (searchKeyboardRequestPhaseRef.current === "awaiting-focus") {
      searchKeyboardRequestPhaseRef.current = "idle";
    }
    notifyManualInputFocused();
  };

  const closeCashInput = (): void => {
    if (!presenter.closeCash()) return;
    numericInputModalActiveRef.current = false;
    notifyManualInputBlurred();
  };

  const closeOpenItemInput = (): void => {
    numericInputModalActiveRef.current = false;
    notifyManualInputBlurred();
    setOpenItemVisible(false);
  };

  const closeLineEditInput = (): void => {
    numericInputModalActiveRef.current = false;
    notifyManualInputBlurred();
    setLineEdit(null);
  };

  const closeOrderEditInput = (): void => {
    numericInputModalActiveRef.current = false;
    notifyManualInputBlurred();
    setOrderEdit(null);
  };

  const handleCashKey = (key: SalesNumberKey): void => {
    presenter.setCashTenderedText(
      applySalesNumberKey(state.cashTenderedText, key, { mode: "decimal" }),
    );
  };

  const handleOpenItemKey = (key: SalesNumberKey): void => {
    setOpenItemPrice((current) =>
      applySalesNumberKey(current, key, { mode: "decimal" }),
    );
  };

  const handleLineEditKey = (key: SalesNumberKey): void => {
    setLineEdit((current) => {
      if (!current) return null;
      const mode: SalesNumberKeypadMode =
        current.mode === "quantity" ? "integer" : "decimal";
      const nextValue = applySalesNumberKey(current.value, key, {
        mode,
        replaceOnNextDigit: current.replaceOnNextDigit,
      });
      // 首次数字用于替换预填值；一旦用户执行实际编辑，后续数字就按位追加。
      const keepReplaceSelection =
        key === "decimal" && nextValue === current.value;
      return {
        ...current,
        replaceOnNextDigit: current.replaceOnNextDigit && keepReplaceSelection,
        value: nextValue,
      };
    });
  };

  const handleOrderEditKey = (key: SalesNumberKey): void => {
    setOrderEdit((current) =>
      current
        ? {
            ...current,
            value: applySalesNumberKey(current.value, key, {
              mode: "decimal",
            }),
          }
        : null,
    );
  };

  useEffect(
    () => () => {
      clearManualInputBlurTimer();
      clearSearchKeyboardTimer();
      clearHidIdleTimer();
      searchKeyboardRequestGenerationRef.current += 1;
      searchKeyboardRequestPhaseRef.current = "idle";
      searchInputRef.current?.setNativeProps({
        showSoftInputOnFocus: false,
      });
      searchRequestGenerationRef.current += 1;
      numericInputModalActiveRef.current = false;
      if (!manualInputActiveRef.current) return;
      manualInputActiveRef.current = false;
      manualInputFocusChangeRef.current?.(false);
    },
    [],
  );

  useEffect(() => {
    if (
      !searchSoftInputOnFocus ||
      searchKeyboardRequestPhaseRef.current !== "enabling"
    ) {
      return;
    }
    const requestGeneration = searchKeyboardRequestGenerationRef.current;
    clearSearchKeyboardTimer();
    searchKeyboardTimerRef.current = setTimeout(() => {
      searchKeyboardTimerRef.current = null;
      if (
        requestGeneration !== searchKeyboardRequestGenerationRef.current ||
        searchKeyboardRequestPhaseRef.current !== "enabling"
      ) {
        return;
      }
      searchKeyboardRequestPhaseRef.current = "awaiting-focus";
      searchInputRef.current?.setNativeProps({ showSoftInputOnFocus: true });
      searchInputRef.current?.focus();
    }, 0);
  }, [searchSoftInputOnFocus]);

  useEffect(() => {
    if (!catalogActionsDisabled && !manualInputTreeUnavailable) return;
    if (searchKeyboardTimerRef.current !== null) {
      clearTimeout(searchKeyboardTimerRef.current);
      searchKeyboardTimerRef.current = null;
    }
    searchKeyboardRequestGenerationRef.current += 1;
    searchKeyboardRequestPhaseRef.current = "idle";
    searchInputRef.current?.setNativeProps({
      showSoftInputOnFocus: false,
    });
    setSearchSoftInputOnFocus(false);
    searchInputRef.current?.blur();
    numericInputModalActiveRef.current = false;
    notifyManualInputBlurred();
  }, [catalogActionsDisabled, manualInputTreeUnavailable]);

  useEffect(() => {
    if (
      (state.phase !== "cash" && state.phase !== "submitting-cash") ||
      numericInputModalActiveRef.current
    ) {
      return;
    }
    // 现金入口已迁入统一支付页；若恢复流程仍进入旧现金态，继续安全接管手动输入。
    beginNumericInputRef.current();
  }, [state.phase]);

  const runCombinedProductSearch = (): void => {
    if (combinedSearchPendingRef.current) return;
    // 搜索按钮提交的是完整手动查询；未达到阈值的快速输入也必须保留。
    resetSearchInputToHidMode(false);
    searchInputRef.current?.blur();
    notifyManualInputBlurred();
    const query = state.query.trim();
    const generation = ++searchRequestGenerationRef.current;
    combinedSearchPendingRef.current = true;
    setCombinedSearchPending(true);
    setSearchDrawerQuery(query);
    setSearchResultsQuery(null);
    setSearchDrawerVisible(false);
    void presenter
      .searchProducts()
      .then(async (searched) => {
        if (generation !== searchRequestGenerationRef.current) return;
        const latest = presenter.getState();
        if (latest.query.trim() !== query) return;
        if (!searched) {
          if (latest.errorCode) setSearchDrawerVisible(true);
          return;
        }

        setSearchResultsQuery(query);
        if (latest.searchResults.length !== 1) {
          setSearchDrawerVisible(true);
          return;
        }

        const added = await presenter.addProduct(latest.searchResults[0]!);
        if (generation !== searchRequestGenerationRef.current || added) return;
        if (presenter.getState().query.trim() === query) {
          setSearchResultsQuery(query);
          setSearchDrawerVisible(true);
        }
      })
      .finally(() => {
        if (generation !== searchRequestGenerationRef.current) return;
        combinedSearchPendingRef.current = false;
        setCombinedSearchPending(false);
      });
  };

  const runUtilityAction = (
    action: SalesUtilityAction,
    operation: (() => Promise<SalesUtilityActionResult>) | undefined,
  ): void => {
    if (!operation || utilityActionPending) return;
    setUtilityActionPending(action);
    setUtilityActionResult(null);
    void operation()
      .then((result) => {
        setUtilityActionResult({ action, result });
      })
      .catch(() => {
        setUtilityActionResult({ action, result: { kind: "failed" } });
      })
      .finally(() => {
        setUtilityActionPending(null);
      });
  };

  const openLineEditor = useCallback((lineId: string): void => {
    const line = cartLinesRef.current.find(
      (candidate) => candidate.lineId === lineId,
    );
    if (!line) return;
    setLineEdit({
      lineId,
      mode: "quantity",
      replaceOnNextDigit: true,
      value: line.quantity,
    });
    beginNumericInputRef.current();
  }, []);

  const selectLineEditMode = (mode: LineEditMode): void => {
    setLineEdit((current) => {
      if (!current) return null;
      const line = state.cart.lines.find(
        (candidate) => candidate.lineId === current.lineId,
      );
      if (!line) return null;
      return {
        lineId: current.lineId,
        mode,
        replaceOnNextDigit: true,
        value:
          mode === "quantity"
            ? line.quantity
            : mode === "price"
              ? formatEditorMoney(line.unitPrice.cents)
              : mode === "discount-amount"
                ? formatEditorMoney(line.discount.cents)
                : "",
      };
    });
  };

  const submitLineEdit = (): void => {
    if (!lineEdit) return;
    let submission: Promise<boolean>;
    if (lineEdit.mode === "quantity") {
      submission = presenter.setLineQuantity(
        lineEdit.lineId,
        parsePositiveInteger(lineEdit.value) ?? 0,
      );
    } else if (lineEdit.mode === "price") {
      submission = presenter.setLineUnitPriceCents(
        lineEdit.lineId,
        parseCashInput(lineEdit.value) ?? -1,
      );
    } else if (lineEdit.mode === "discount-amount") {
      submission = presenter.applyLineDiscountAmountCents(
        lineEdit.lineId,
        parseCashInput(lineEdit.value) ?? -1,
      );
    } else {
      submission = presenter.applyLineManualDiscountBasisPoints(
        lineEdit.lineId,
        parsePercentageBasisPoints(lineEdit.value) ?? -1,
      );
    }
    void submission.then((applied) => {
      if (applied) closeLineEditInput();
    });
  };

  const submitOrderEdit = (): void => {
    if (!orderEdit) return;
    const submission =
      orderEdit.mode === "amount"
        ? presenter.applyOrderDiscountAmountCents(
            parseCashInput(orderEdit.value) ?? -1,
          )
        : presenter.applyOrderManualDiscountBasisPoints(
            parsePercentageBasisPoints(orderEdit.value) ?? -1,
          );
    void submission.then((applied) => {
      if (applied) closeOrderEditInput();
    });
  };

  const handleSelectCartLine = useCallback(
    (lineId: string) => {
      presenter.selectLine(lineId);
      setCartItemActionsLineId(lineId);
    },
    [presenter],
  );
  const handleDecreaseCartLine = useCallback(
    (lineId: string) => {
      void presenter.decreaseLine(lineId);
    },
    [presenter],
  );
  const handleIncreaseCartLine = useCallback(
    (lineId: string) => {
      void presenter.increaseLine(lineId);
    },
    [presenter],
  );
  const handleRemoveCartLine = useCallback(
    (lineId: string) => {
      void presenter.removeLine(lineId);
    },
    [presenter],
  );

  const renderCartLine = useCallback(
    ({ item, index }: ListRenderItemInfo<CartLine>) => (
      <View style={styles.cartListItem}>
        <CartLineRow
          compact={compactSalesLayout}
          disabled={
            !state.capabilities.cartEditing || transactionActionsDisabled
          }
          isSelected={state.selectedLineId === item.lineId}
          item={item}
          lineNumber={displayedCartLines.length - index}
          locale={locale}
          onDecrease={handleDecreaseCartLine}
          onDiscount={setDiscountLineId}
          onEdit={openLineEditor}
          onIncrease={handleIncreaseCartLine}
          onRemove={handleRemoveCartLine}
          onSelect={handleSelectCartLine}
          {...(resolveCartProductImage
            ? { resolveProductImage: resolveCartProductImage }
            : {})}
          t={t}
        />
      </View>
    ),
    [
      compactSalesLayout,
      displayedCartLines.length,
      locale,
      handleDecreaseCartLine,
      handleIncreaseCartLine,
      handleRemoveCartLine,
      handleSelectCartLine,
      openLineEditor,
      resolveCartProductImage,
      state.capabilities.cartEditing,
      state.selectedLineId,
      t,
      transactionActionsDisabled,
    ],
  );

  if (state.phase === "success" && state.success) {
    return (
      <HandheldScreenFrame
        footer={
          <HandheldActionButton
            disabled={!state.success.clearCartSignalled}
            label={t("success.newSale")}
            onPress={() => presenter.startNewSale()}
            testID="sales-new-sale"
          />
        }
        header={
          showStatusStrip ? (
            <HandheldOperationalStrip items={operationalItems} />
          ) : undefined
        }
        testID="sales-screen"
      >
        <View
          style={[
            styles.successScreen,
            compactSuccessLayout && styles.successScreenCompact,
          ]}
          testID="sales-success"
        >
          <View
            style={[
              styles.successMark,
              compactSuccessLayout && styles.successMarkCompact,
            ]}
          >
            <Text
              style={[
                styles.successMarkText,
                compactSuccessLayout && styles.successMarkTextCompact,
              ]}
            >
              ✓
            </Text>
          </View>
          <Text
            style={[
              styles.successEyebrow,
              compactSuccessLayout && styles.successEyebrowCompact,
            ]}
          >
            {t("success.eyebrow")}
          </Text>
          <Text
            style={[
              styles.successTitle,
              compactSuccessLayout && styles.successTitleCompact,
            ]}
          >
            {t("success.title")}
          </Text>
          <Text style={styles.successOrder}>
            {t("success.order", { orderGuid: state.success.orderGuid })}
          </Text>
          <View
            style={[
              styles.changeCard,
              compactSuccessLayout && styles.changeCardCompact,
            ]}
          >
            <Text style={styles.changeLabel}>{t("success.change")}</Text>
            <Text
              adjustsFontSizeToFit
              minimumFontScale={0.7}
              numberOfLines={1}
              style={[
                styles.changeAmount,
                compactSuccessLayout && styles.changeAmountCompact,
              ]}
            >
              {formatAud(state.success.changeCents, locale)}
            </Text>
          </View>
          <Text
            style={[
              styles.successSync,
              compactSuccessLayout && styles.successSyncCompact,
            ]}
          >
            {connectivity === "offline"
              ? t("success.syncOffline")
              : t("success.syncOnline")}
          </Text>
          {successDrawerWarning ? (
            <Text style={styles.successWarning} testID="sales-drawer-warning">
              {t(successDrawerWarning)}
            </Text>
          ) : null}
          {state.errorCode === "cart-clear-failed" ? (
            <ErrorBanner
              dismissLabel={t("common.dismiss")}
              message={t("error.cartClearFailed")}
              onDismiss={() => presenter.dismissError()}
            />
          ) : null}
        </View>
      </HandheldScreenFrame>
    );
  }

  if (state.phase === "locked") {
    return (
      <HandheldScreenFrame
        header={
          showStatusStrip ? (
            <HandheldOperationalStrip items={operationalItems} />
          ) : undefined
        }
        testID="sales-screen"
      >
        <View style={styles.lockedScreen} testID="sales-locked">
          <View style={styles.lockIcon}>
            <Text style={styles.lockIconText}>●</Text>
          </View>
          <Text style={styles.lockedTitle}>{t("locked.title")}</Text>
          <Text style={styles.lockedHint}>{t("locked.hint")}</Text>
          <View style={styles.lockedNotice}>
            <Text style={styles.lockedNoticeText}>
              {t("locked.unlockPending")}
            </Text>
          </View>
        </View>
      </HandheldScreenFrame>
    );
  }

  const runtimeUnavailable =
    !state.capabilities.catalog ||
    !state.capabilities.cartEditing ||
    !state.capabilities.cashCheckout;
  const cashDraft = deriveCashDraft(state.cart, state.cashTenderedText);
  const toolbarActions: readonly SalesToolbarAction[] = [
    {
      disabled:
        !state.capabilities.hold || cartEmpty || transactionActionsDisabled,
      id: "hold",
      label: t("functions.hold"),
      onPress: () => {
        void presenter.holdCart();
      },
      testID: "sales-hold",
      tone: "secondary",
    },
    {
      disabled: mergeCartDisabled,
      id: "merge-cart",
      label: t("functions.mergeCart"),
      onPress: () => {
        void presenter.mergeCompatibleLines();
      },
      testID: "sales-merge-cart",
      tone: "secondary",
    },
    ...(onOpenReturns
      ? [
          {
            id: "returns" as const,
            label: t("functions.returns"),
            onPress: onOpenReturns,
            testID: "sales-open-returns",
            tone: "secondary" as const,
          },
        ]
      : []),
    ...(onOpenLocalHistory
      ? [
          {
            id: "local-history" as const,
            label: t("functions.localHistory"),
            onPress: onOpenLocalHistory,
            testID: "sales-open-local-history",
            tone: "secondary" as const,
          },
        ]
      : []),
    ...(onOpenHeldOrders
      ? [
          {
            id: "held-orders" as const,
            label: t("functions.heldSales"),
            onPress: onOpenHeldOrders,
            testID: "sales-open-held-orders",
            tone: "secondary" as const,
          },
        ]
      : []),
    {
      disabled: !onReprintReceipt || utilityActionPending !== null,
      id: "reprint-receipt",
      label:
        utilityActionPending === "reprint"
          ? t("functions.reprinting")
          : t("functions.reprintReceipt"),
      onPress: () => runUtilityAction("reprint", onReprintReceipt),
      testID: "sales-reprint-receipt",
      tone: "quiet",
    },
    {
      disabled: !onOpenCashDrawer || utilityActionPending !== null,
      id: "cash-drawer",
      label:
        utilityActionPending === "drawer"
          ? t("functions.openingDrawer")
          : t("functions.openDrawer"),
      onPress: () => runUtilityAction("drawer", onOpenCashDrawer),
      testID: "sales-open-cash-drawer",
      tone: "quiet",
    },
    ...(onOpenDailyClose
      ? [
          {
            id: "daily-close" as const,
            label: t("header.dailyClose"),
            onPress: onOpenDailyClose,
            testID: "sales-open-daily-close",
            tone: "quiet" as const,
          },
        ]
      : []),
    ...(onOpenRemoteHistory
      ? [
          {
            id: "remote-history" as const,
            label: t("header.remoteHistory"),
            onPress: onOpenRemoteHistory,
            testID: "sales-open-remote-history",
            tone: "quiet" as const,
          },
        ]
      : []),
    ...(onOpenInstallments
      ? [
          {
            id: "installments" as const,
            label: t("header.installments"),
            onPress: onOpenInstallments,
            testID: "sales-open-installments",
            tone: "quiet" as const,
          },
        ]
      : []),
    ...(onOpenSettings
      ? [
          {
            id: "settings" as const,
            label: t("header.settings"),
            onPress: onOpenSettings,
            testID: "sales-open-settings",
            tone: "quiet" as const,
          },
        ]
      : []),
    ...(onOpenAttendanceAudit
      ? [
          {
            id: "attendance-audit" as const,
            label: t("header.attendanceAudit"),
            onPress: onOpenAttendanceAudit,
            testID: "sales-open-attendance-audit",
            tone: "quiet" as const,
          },
        ]
      : []),
    ...(onOpenSyncHistory
      ? [
          {
            id: "sync-history" as const,
            label: t("header.syncHistory"),
            onPress: onOpenSyncHistory,
            testID: "sales-open-sync-history",
            tone: "quiet" as const,
          },
        ]
      : []),
    ...(onOpenCatalogMaintenance
      ? [
          {
            id: "catalog-maintenance" as const,
            label: t("header.catalogMaintenance"),
            onPress: onOpenCatalogMaintenance,
            testID: "sales-open-catalog-maintenance",
            tone: "quiet" as const,
          },
        ]
      : []),
    {
      disabled: !state.capabilities.lock,
      id: "lock",
      label: t("header.lock"),
      onPress: () => {
        void presenter.lockTerminal();
      },
      testID: "sales-lock",
      tone: "quiet",
    },
  ];
  const summaryActionsDisabled =
    cartEmpty || !state.capabilities.cartEditing || transactionActionsDisabled;
  const languageSwitcher = onSwitchLanguage ? (
    <HandheldActionButton
      label={locale === "zh" ? "EN" : "中"}
      onPress={onSwitchLanguage}
      sound="navigate"
      testID="sales-switch-language"
      variant="secondary"
    />
  ) : null;
  const salesToolbar = (
    <SalesToolbar
      accessibilityCopy={{
        moveEarlier: t("toolbar.moveEarlier"),
        moveLater: t("toolbar.moveLater"),
        reorderHint: t("toolbar.reorderHint"),
        positionChanged: (label, position, total) =>
          t("toolbar.positionChanged", { label, position, total }),
      }}
      actions={toolbarActions}
      canonicalOrder={toolbarOrder}
      closeLabel={t("toolbar.close")}
      onOrderChange={onToolbarOrderChange}
      triggerLabel={t("toolbar.more")}
    />
  );
  return (
    <HandheldScreenFrame
      contentStyle={styles.screenContent}
      footerStyle={compactSalesLayout ? styles.compactScreenFooter : undefined}
      footer={
        <View
          style={[
            styles.checkoutDock,
            compactSalesLayout && styles.compactCheckoutDock,
          ]}
        >
          {compactSalesLayout ? (
            <View
              style={[styles.summaryPane, styles.compactSummaryPane]}
              testID="sales-summary-pane"
            >
              <View
                style={styles.compactSummaryOverview}
                testID="sales-summary-overview"
              >
                <View style={styles.compactSummaryMetric}>
                  <Text numberOfLines={1} style={styles.compactSummaryLabel}>
                    {t("summary.subtotal")}
                  </Text>
                  <Text
                    adjustsFontSizeToFit
                    minimumFontScale={0.65}
                    numberOfLines={1}
                    style={styles.compactSummaryAmount}
                  >
                    {formatAud(state.cart.subtotal.cents, locale)}
                  </Text>
                </View>
                <View style={styles.compactSummaryMetric}>
                  <Text numberOfLines={1} style={styles.compactSummaryLabel}>
                    {t("summary.discount")}
                  </Text>
                  <Text
                    adjustsFontSizeToFit
                    minimumFontScale={0.65}
                    numberOfLines={1}
                    style={styles.compactSummaryDiscount}
                    testID="sales-summary-discount-amount"
                  >
                    −{formatAud(state.cart.discount.cents, locale)}
                  </Text>
                </View>
                <View style={styles.compactSummaryTotal}>
                  <Text numberOfLines={1} style={styles.compactSummaryLabel}>
                    {t("summary.total")}
                  </Text>
                  <Text
                    adjustsFontSizeToFit
                    minimumFontScale={0.65}
                    numberOfLines={1}
                    style={styles.compactSummaryTotalAmount}
                  >
                    {formatAud(state.cart.actualAmount.cents, locale)}
                  </Text>
                </View>
              </View>
              <View
                style={[
                  styles.summaryEditActions,
                  styles.compactSummaryActions,
                ]}
                testID="sales-summary-actions"
              >
                <CompactSummaryAction
                  accessibilityLabel={t("summary.orderDiscount")}
                  disabled={summaryActionsDisabled}
                  icon="percent-outline"
                  onPress={() => setOrderDiscountVisible(true)}
                  testID="sales-order-discount"
                  tone="secondary"
                />
                <CompactSummaryAction
                  accessibilityLabel={t("summary.clearCart")}
                  disabled={summaryActionsDisabled}
                  icon="delete-outline"
                  onPress={() => setClearCartVisible(true)}
                  testID="sales-clear-cart"
                  tone="danger"
                />
              </View>
            </View>
          ) : (
          <View style={styles.summaryPane} testID="sales-summary-pane">
            <View
              style={styles.summaryOverview}
              testID="sales-summary-overview"
            >
              <View style={styles.summaryMetrics}>
                <Text style={styles.summaryTitle}>{t("summary.title")}</Text>
                <SummaryRow
                  amount={formatAud(state.cart.subtotal.cents, locale)}
                  label={t("summary.subtotal")}
                />
                <SummaryRow
                  amount={`−${formatAud(state.cart.discount.cents, locale)}`}
                  amountTestID="sales-summary-discount-amount"
                  amountTone="danger"
                  label={t("summary.discount")}
                  muted
                />
              </View>
              <View style={styles.summaryTotal}>
                <Text style={styles.totalLabel}>{t("summary.total")}</Text>
                <Text
                  adjustsFontSizeToFit
                  minimumFontScale={0.72}
                  numberOfLines={1}
                  style={styles.totalAmount}
                >
                  {formatAud(state.cart.actualAmount.cents, locale)}
                </Text>
              </View>
            </View>
            <View
              style={styles.summaryEditActions}
              testID="sales-summary-actions"
            >
              <ActionButton
                  disabled={summaryActionsDisabled}
                label={t("summary.orderDiscount")}
                onPress={() => setOrderDiscountVisible(true)}
                sound="navigate"
                style={styles.summaryEditAction}
                testID="sales-order-discount"
                tone="secondary"
              />
              <ActionButton
                  disabled={summaryActionsDisabled}
                label={t("summary.clearCart")}
                onPress={() => setClearCartVisible(true)}
                style={styles.summaryEditAction}
                testID="sales-clear-cart"
                tone="danger"
              />
            </View>
          </View>
          )}
          <HandheldActionButton
            disabled={
              cartEmpty ||
              !onOpenPayment ||
              !state.capabilities.cartEditing ||
              state.phase !== "selling"
            }
            label={
              cartEmpty
                ? t("summary.checkoutDisabled")
                : `${t("summary.goToPayment")} · ${formatAud(
                    state.cart.actualAmount.cents,
                    locale,
                  )}`
            }
            onPress={() => {
              if (!onOpenPayment) return;
              void presenter.prepareOnlineCheckout().then((preparedCart) => {
                if (preparedCart) onOpenPayment(preparedCart);
              });
            }}
            sound="navigate"
            testID="sales-open-payment"
          />
        </View>
      }
      header={
        compactSalesLayout ? (
          <View style={styles.compactTopBar} testID="sales-compact-top-bar">
            <View style={styles.compactTopBarEdge}>{languageSwitcher}</View>
            <HandheldOperationalStrip
              compact
              items={compactOperationalItems}
              style={styles.compactTopBarStatus}
            />
            <View style={styles.compactTopBarEdge}>{salesToolbar}</View>
          </View>
        ) : (
        <>
          <HandheldPageHeader
            eyebrow={
              terminalPresentation
                ? `${terminalPresentation.storeName?.trim() || "—"} · ${terminalPresentation.deviceCode}`
                : t("app.workspace")
            }
              leading={languageSwitcher ?? undefined}
            title={t("app.title")}
              trailing={salesToolbar}
          />
          {showStatusStrip ? (
            <HandheldOperationalStrip items={operationalItems} />
          ) : null}
        </>
        )
      }
      testID="sales-screen"
    >
      {runtimeUnavailable ? (
        <View
          accessibilityRole="alert"
          style={styles.runtimeBanner}
          testID="sales-runtime-unavailable"
        >
          <Text style={styles.runtimeBannerText}>
            {t("app.runtimeUnavailable")}
          </Text>
        </View>
      ) : null}
      {newTransactionBlocked ? (
        <View
          accessibilityRole="alert"
          style={styles.updateGateBanner}
          testID="sales-new-transaction-gate"
        >
          <View style={styles.updateGateCopy}>
            <Text style={styles.updateGateTitle}>
              {t(
                newTransactionGate?.state === "force-update"
                  ? "updateGate.forceTitle"
                  : newTransactionGate?.state === "ota-update"
                    ? "updateGate.otaTitle"
                    : newTransactionGate?.state === "disabled"
                      ? "updateGate.disabledTitle"
                      : "updateGate.uncheckedTitle",
              )}
            </Text>
            <Text style={styles.updateGateHint}>{t("updateGate.hint")}</Text>
          </View>
          {newTransactionGate?.state === "force-update" &&
          onOpenRequiredUpdate ? (
            <ActionButton
              label={t("updateGate.openStore")}
              onPress={onOpenRequiredUpdate}
              sound="navigate"
              testID="sales-open-required-update"
              tone="secondary"
            />
          ) : null}
        </View>
      ) : null}

      <HandheldStateSurface
        slug={cartEmpty ? "sales-empty" : "sales-active"}
        style={styles.salesStateSurface}
      >
        <PosKeyboardAwareFlatList<CartLine>
          ListHeaderComponent={
            <View
              style={[
                styles.workspaceHeader,
                compactSalesLayout && styles.compactWorkspaceHeader,
              ]}
            >
              {/* 键盘感知合同要求输入保持在滚动容器的词法子树；HID/软键盘状态机沿用原回调。 */}
              <HandheldStateSurface slug="pda-sales-input">
                <ProductEntryFrame compact={compactSalesLayout}>
                  <View
                    style={[
                      styles.searchInputRow,
                      compactSalesLayout && styles.compactSearchInputRow,
                    ]}
                    testID="sales-search-input-row"
                  >
              <PosKeyboardAwareTextInput
                ref={searchInputRef}
                accessibilityLabel={t("catalog.searchPlaceholder")}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!catalogActionsDisabled}
                onBlur={handleSearchInputBlur}
                onChangeText={handleSearchInputChange}
                onFocus={handleSearchInputFocus}
                onSubmitEditing={handleSearchInputSubmit}
                placeholder={t("catalog.searchPlaceholder")}
                placeholderTextColor="#7B8793"
                returnKeyType="done"
                selectionColor={posColors.orange}
                showSoftInputOnFocus={searchSoftInputOnFocus}
                style={styles.searchInput}
                submitBehavior="blurAndSubmit"
                testID="sales-search-input"
                value={state.query}
              />
              <CatalogIconButton
                accessibilityLabel={t("catalog.searchAndAdd")}
                disabled={catalogActionsDisabled || combinedSearchPending}
                icon="magnify"
                iconTestID="sales-search-icon"
                loading={combinedSearchPending}
                onPress={runCombinedProductSearch}
                testID="sales-search-button"
              />
              <CatalogIconButton
                accessibilityLabel={t("catalog.keyboard")}
                disabled={catalogActionsDisabled}
                icon="keyboard-outline"
                iconTestID="sales-keyboard-icon"
                onPress={requestSearchKeyboard}
                testID="sales-show-keyboard"
              />
              {onOpenCameraScanner ? (
                <CatalogIconButton
                  accessibilityLabel={t("catalog.camera")}
                  disabled={catalogActionsDisabled}
                  icon="camera-outline"
                  iconTestID="sales-camera-icon"
                  onPress={openCameraScanner}
                  testID="sales-open-camera-scanner"
                />
              ) : null}
            </View>
            <View
                    style={[
                      styles.searchActions,
                      compactSalesLayout && styles.compactSearchActions,
                    ]}
              testID="sales-product-entry-actions"
            >
              <ActionButton
                disabled={catalogActionsDisabled}
                label={t("catalog.openItem")}
                onPress={() => {
                  setOpenItemPrice("");
                  setOpenItemVisible(true);
                  beginNumericInput();
                }}
                sound="navigate"
                style={styles.searchAction}
                testID="sales-open-item-button"
                tone="warning"
              />
              {onOpenSpecialProducts ? (
                <ActionButton
                  disabled={catalogActionsDisabled}
                  label={t("catalog.specialProducts")}
                  onPress={onOpenSpecialProducts}
                  sound="navigate"
                  style={styles.searchAction}
                  testID="sales-open-special-products"
                  tone="info"
                />
              ) : null}
            </View>
                  {!compactSalesLayout &&
                  (state.pendingLookupCount > 0 || checkoutVerifying) ? (
              <Text
                accessibilityLiveRegion="polite"
                style={styles.verifyingMessage}
                testID="sales-catalog-verifying"
              >
                {t("catalog.verifying")}
              </Text>
            ) : null}
                  {!compactSalesLayout && lastScanFeedback ? (
              <HandheldStateSurface
                slug="pda-scan-result"
                style={styles.pdaState}
              >
                <View style={styles.pdaStateCopy}>
                  <Text style={styles.pdaStateTitle}>
                    {t("pda.scanResultTitle")}
                  </Text>
                  <Text style={styles.pdaStateHint}>
                          {t(
                            `pda.scanResult.${lastScanFeedback.kind}` as SalesCopyKey,
                          )}
                  </Text>
                </View>
                <HandheldActionButton
                  label={t("common.dismiss")}
                  onPress={() => setLastScanFeedback(null)}
                  testID="sales-scan-result-dismiss"
                  variant="secondary"
                />
              </HandheldStateSurface>
            ) : null}
                </ProductEntryFrame>
              </HandheldStateSurface>
              {!compactSalesLayout && connectivity === "offline" ? (
                <View
                  accessibilityRole="alert"
                  style={styles.offlineCard}
                  testID="sales-offline-cash-only"
                >
                  <Text style={styles.offlineTitle}>
                    {t("summary.cashOnly")}
                  </Text>
                  <Text style={styles.offlineHint}>
                    {t("summary.cashOnlyHint")}
                  </Text>
                </View>
              ) : null}
              <View
                style={[
                  styles.cartPaneHeaderSegment,
                  compactSalesLayout && styles.compactCartPaneHeaderSegment,
                ]}
                testID="sales-transaction-pane"
              >
              <View style={styles.cartHeader}>
                <Text style={styles.paneTitle}>{t("cart.title")}</Text>
                <Text style={styles.cartCount}>
                  {t("cart.items", { count: state.cart.lines.length })}
                </Text>
              </View>
              </View>
            </View>
          }
          ListEmptyComponent={
            <View
              style={[
                styles.cartPaneEmptySegment,
                styles.emptyCart,
                compactSalesLayout && {
                  minHeight: compactCartRowsMinHeight,
                },
              ]}
              testID="sales-cart-empty"
            >
                  <Text style={styles.emptyCartIcon}>＋</Text>
              <Text style={styles.emptyCartTitle}>{t("cart.emptyTitle")}</Text>
              <Text style={styles.emptyCartHint}>{t("cart.emptyHint")}</Text>
            </View>
          }
          ListFooterComponent={
            !cartEmpty ? <View style={styles.cartPaneFooterSegment} /> : null
          }
          contentContainerStyle={[
            styles.workspaceList,
            compactSalesLayout && styles.compactWorkspaceList,
          ]}
          data={displayedCartLines}
          initialNumToRender={10}
          keyExtractor={cartLineKeyExtractor}
          maxToRenderPerBatch={8}
          onScrollToIndexFailed={retryCartReveal}
          ref={cartListRef}
          removeClippedSubviews
          renderItem={renderCartLine}
          showsVerticalScrollIndicator={false}
          style={[
            styles.workspaceListViewport,
            compactSalesLayout && { minHeight: compactCartListMinHeight },
          ]}
          testID="sales-cart-list"
          updateCellsBatchingPeriod={32}
          windowSize={7}
        />
      </HandheldStateSurface>

      {!compactSalesLayout && utilityActionResult ? (
        <HandheldStateSurface
          slug="pda-print-drawer-result"
          style={styles.utilityResultSurface}
        >
          <Text
            accessibilityLiveRegion="polite"
            style={[
              styles.utilityResult,
              utilityActionResult.result.kind !== "completed" &&
                styles.utilityResultWarning,
            ]}
            testID="sales-utility-action-result"
          >
            {t(
              utilityResultCopyKey(
                utilityActionResult.action,
                utilityActionResult.result.kind,
              ),
            )}
          </Text>
        </HandheldStateSurface>
      ) : null}

      {!compactSalesLayout && state.errorCode ? (
        <ErrorBanner
          dismissLabel={t("common.dismiss")}
          message={t(errorCopyKey(state.errorCode))}
          onDismiss={() => presenter.dismissError()}
        />
      ) : null}

      <Modal
        animationType="fade"
        onRequestClose={() => setCartItemActionsLineId(null)}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={selectedCartActionLine !== null}
      >
        <View style={styles.modalBackdrop}>
          <PosPressable
            accessible={false}
            onPress={() => setCartItemActionsLineId(null)}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="sales-cart-actions-backdrop"
          />
          <HandheldStateSurface
            slug="cart-item-actions"
            style={styles.cartActionsModal}
          >
            <Text style={styles.modalTitle}>{t("cart.actionsTitle")}</Text>
            <Text style={styles.discountHint}>
              {t("cart.actionsHint", {
                product: selectedCartActionLine?.displayName ?? "",
              })}
            </Text>
            <View style={styles.cartActionsGrid}>
              <ActionButton
                disabled={transactionActionsDisabled}
                label={t("cart.decrease")}
                onPress={() => {
                  if (selectedCartActionLine) {
                    handleDecreaseCartLine(selectedCartActionLine.lineId);
                  }
                }}
                testID="sales-cart-actions-decrease"
                tone="secondary"
              />
              <ActionButton
                disabled={transactionActionsDisabled}
                label={t("cart.increase")}
                onPress={() => {
                  if (selectedCartActionLine) {
                    handleIncreaseCartLine(selectedCartActionLine.lineId);
                  }
                }}
                testID="sales-cart-actions-increase"
                tone="secondary"
              />
              <ActionButton
                disabled={transactionActionsDisabled}
                label={t("cart.edit")}
                onPress={() => {
                  const lineId = selectedCartActionLine?.lineId;
                  setCartItemActionsLineId(null);
                  if (lineId) openLineEditor(lineId);
                }}
                testID="sales-cart-actions-edit"
                tone="secondary"
              />
              <ActionButton
                disabled={transactionActionsDisabled}
                label={t("cart.discount")}
                onPress={() => {
                  const lineId = selectedCartActionLine?.lineId;
                  setCartItemActionsLineId(null);
                  if (lineId) setDiscountLineId(lineId);
                }}
                testID="sales-cart-actions-discount"
                tone="secondary"
              />
              <ActionButton
                disabled={transactionActionsDisabled}
                label={t("cart.remove")}
                onPress={() => {
                  const lineId = selectedCartActionLine?.lineId;
                  setCartItemActionsLineId(null);
                  if (lineId) handleRemoveCartLine(lineId);
                }}
                testID="sales-cart-actions-remove"
                tone="danger"
              />
              <ActionButton
                label={t("common.dismiss")}
                onPress={() => setCartItemActionsLineId(null)}
                sound="navigate"
                testID="sales-cart-actions-close"
                tone="quiet"
              />
            </View>
          </HandheldStateSurface>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={() => setSearchDrawerVisible(false)}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={searchDrawerVisible}
      >
        <View style={styles.searchDrawerBackdrop}>
          <PosPressable
            accessibilityLabel={t("catalog.closeResults")}
            accessibilityRole="button"
            onPress={() => setSearchDrawerVisible(false)}
            sound="navigate"
            style={styles.searchDrawerDismissArea}
            testID="sales-search-results-backdrop"
          />
          <HandheldStateSurface
            slug="product-search"
            style={styles.searchDrawer}
          >
            <View
              accessibilityViewIsModal
              style={styles.searchDrawerContent}
              testID="sales-search-results-drawer"
            >
              <View style={styles.searchDrawerHeader}>
                <View style={styles.searchDrawerHeading}>
                  <Text style={styles.modalTitle}>
                    {t("catalog.resultsTitle")}
                  </Text>
                  <Text numberOfLines={1} style={styles.searchDrawerQuery}>
                    {searchDrawerQuery}
                  </Text>
                </View>
                <ActionButton
                  label={t("catalog.closeResults")}
                  onPress={() => setSearchDrawerVisible(false)}
                  sound="navigate"
                  testID="sales-search-results-close"
                  tone="quiet"
                />
              </View>
              {state.searchStatus === "searching" ? (
                <Text
                  accessibilityLiveRegion="polite"
                  style={styles.searchMessage}
                  testID="sales-search-results-loading"
                >
                  {t("catalog.searching")}
                </Text>
              ) : null}
              {searchResultsAreCurrent && visibleSearchResults.length === 0 ? (
                <Text
                  accessibilityLiveRegion="polite"
                  style={styles.searchMessage}
                  testID="sales-search-results-empty"
                >
                  {t("catalog.noResults")}
                </Text>
              ) : null}
              {state.errorCode === "search-required" ||
              state.errorCode === "search-failed" ||
              state.errorCode === "product-add-failed" ||
              state.errorCode === "terminal-recovery-required" ||
              state.errorCode === "authorization-denied" ? (
                <Text
                  accessibilityRole="alert"
                  style={styles.searchDrawerError}
                >
                  {t(errorCopyKey(state.errorCode))}
                </Text>
              ) : null}
              <FlatList
                contentContainerStyle={styles.searchResults}
                data={visibleSearchResults}
                keyExtractor={(item) =>
                  `${item.productCode}:${item.lookupCode}`
                }
                keyboardShouldPersistTaps="handled"
                renderItem={({ item }) => (
                  <ProductSearchRow
                    disabled={catalogActionsDisabled}
                    item={item}
                    locale={locale}
                    onAdd={() => {
                      void presenter.addProduct(item).then((added) => {
                        if (added) setSearchDrawerVisible(false);
                      });
                    }}
                    t={t}
                  />
                )}
                style={styles.searchDrawerList}
                testID="sales-search-results-list"
              />
            </View>
          </HandheldStateSurface>
        </View>
      </Modal>

      {lookupSelection ? (
        <Modal
          animationType="fade"
          onRequestClose={() => presenter.cancelLookupSelection()}
          presentationStyle="overFullScreen"
          supportedOrientations={["portrait"]}
          transparent
          visible
        >
          <View style={styles.searchDrawerBackdrop}>
            <PosPressable
              accessibilityLabel={t("lookupSelection.cancel")}
              accessibilityRole="button"
              onPress={() => presenter.cancelLookupSelection()}
              sound="navigate"
              style={styles.searchDrawerDismissArea}
              testID="sales-lookup-selection-backdrop"
            />
            <View
              accessibilityViewIsModal
              style={styles.searchDrawer}
              testID="sales-lookup-selection"
            >
              <View style={styles.searchDrawerContent}>
                <View style={styles.lookupSelectionHeader}>
                  <Text style={styles.lookupSelectionTitle}>
                    {t("lookupSelection.title")}
                  </Text>
                  <Text
                    style={styles.lookupSelectionHint}
                    testID="sales-lookup-selection-hint"
                  >
                    {t("lookupSelection.hint", {
                      code: lookupSelection.lookupCode,
                      count: lookupSelection.candidates.length,
                    })}
                  </Text>
                </View>
                <FlatList
                  contentContainerStyle={styles.searchResults}
                  data={lookupSelection.candidates}
                  keyExtractor={(item) => item.productCode}
                  renderItem={({ item }) => (
                    <LookupCandidateRow
                      disabled={lookupCandidatesDisabled}
                      item={item}
                      locale={locale}
                      onChoose={() => {
                        void presenter.chooseLookupCandidate(item.productCode);
                      }}
                      {...(resolveCartProductImage
                        ? { resolveProductImage: resolveCartProductImage }
                        : {})}
                      t={t}
                    />
                  )}
                  style={styles.searchDrawerList}
                  testID="sales-lookup-selection-list"
                />
                <ActionButton
                  label={t("lookupSelection.cancel")}
                  onPress={() => presenter.cancelLookupSelection()}
                  sound="navigate"
                  style={styles.lookupSelectionCancel}
                  testID="sales-lookup-selection-cancel"
                  tone="secondary"
                />
              </View>
            </View>
          </View>
        </Modal>
      ) : null}

      <Modal
        animationType="fade"
        onRequestClose={closeCashInput}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={state.phase === "cash" || state.phase === "submitting-cash"}
      >
        <View style={styles.modalBackdrop}>
          <PosPressable
            accessible={false}
            onPress={closeCashInput}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="sales-cash-backdrop"
          />
          <View
            accessibilityViewIsModal
            style={styles.numericModal}
            testID="sales-cash-modal"
          >
            <Text style={styles.modalTitle}>{t("cash.title")}</Text>
            <View style={styles.numericEditorBody}>
              <View style={styles.numericEditorSummary}>
                <Text style={styles.cashDueLabel}>{t("cash.amountDue")}</Text>
                <Text style={styles.cashDueAmount}>
                  {formatAud(getCashDueCents(state.cart), locale)}
                </Text>
                <Text style={styles.fieldLabel}>{t("cash.tendered")}</Text>
                <NumericValueDisplay
                  accessibilityLabel={t("cash.tendered")}
                  currencyPrefix="$"
                  placeholder={t("cash.tenderedPlaceholder")}
                  testID="sales-cash-tendered"
                  value={state.cashTenderedText}
                />
                <ActionButton
                  disabled={state.phase !== "cash"}
                  label={t("cash.exact")}
                  onPress={() => presenter.setExactCash()}
                  style={styles.cashExactAction}
                  testID="sales-cash-exact"
                  tone="secondary"
                />
                <View style={styles.changePreview}>
                  <Text style={styles.changePreviewLabel}>
                    {t("cash.change")}
                  </Text>
                  <Text style={styles.changePreviewAmount}>
                    {formatAud(cashDraft.changeCents, locale)}
                  </Text>
                </View>
                {state.errorCode === "cash-invalid" ||
                state.errorCode === "cash-insufficient" ||
                state.errorCode === "cash-failed" ? (
                  <Text accessibilityRole="alert" style={styles.cashError}>
                    {t(errorCopyKey(state.errorCode))}
                  </Text>
                ) : null}
              </View>
              <View style={styles.numericKeypadColumn}>
                <SalesNumberKeypad
                  disabled={state.phase !== "cash"}
                  labels={keypadLabels}
                  mode="decimal"
                  onKeyPress={handleCashKey}
                  testIDPrefix="sales-cash"
                />
              </View>
            </View>
            <View style={styles.modalActions}>
              <ActionButton
                disabled={state.phase === "submitting-cash"}
                label={t("cash.cancel")}
                onPress={closeCashInput}
                sound="navigate"
                style={styles.modalAction}
                testID="sales-cash-cancel"
                tone="secondary"
              />
              <ActionButton
                disabled={state.phase === "submitting-cash" || !cashDraft.valid}
                label={
                  state.phase === "submitting-cash"
                    ? t("cash.confirming")
                    : t("cash.confirm")
                }
                onPress={() => {
                  void presenter.submitCash();
                }}
                style={styles.modalAction}
                testID="sales-cash-confirm"
              />
            </View>
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={closeOpenItemInput}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={openItemVisible}
      >
        <View style={styles.modalBackdrop}>
          <PosPressable
            accessible={false}
            onPress={closeOpenItemInput}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="sales-open-item-backdrop"
          />
          <HandheldStateSurface
            slug="open-item-keypad"
            style={[styles.numericModal, styles.openItemModalSurface]}
          >
            <ScrollView
              accessibilityViewIsModal
              bounces={false}
              showsVerticalScrollIndicator={false}
              style={styles.openItemModalScroll}
              testID="sales-open-item-modal"
            >
              <Text style={styles.modalTitle}>{t("openItem.title")}</Text>
              <Text style={styles.discountHint}>{t("openItem.hint")}</Text>
              <View style={styles.numericEditorBody}>
                <View style={styles.numericEditorSummary}>
                  <Text style={styles.fieldLabel}>{t("openItem.price")}</Text>
                  <NumericValueDisplay
                    accessibilityLabel={t("openItem.price")}
                    placeholder="0.00"
                    testID="sales-open-item-price"
                    value={openItemPrice}
                  />
                  {state.errorCode === "terminal-recovery-required" ? (
                    <Text
                      accessibilityRole="alert"
                      style={styles.cashError}
                      testID="sales-open-item-recovery-error"
                    >
                      {t(errorCopyKey(state.errorCode))}
                    </Text>
                  ) : null}
                </View>
                <View style={styles.numericKeypadColumn}>
                  <SalesNumberKeypad
                    labels={keypadLabels}
                    mode="decimal"
                    onKeyPress={handleOpenItemKey}
                    testIDPrefix="sales-open-item"
                  />
                </View>
              </View>
              {/* TC26 纵向空间有限：操作并排，额外文案过高时仍可滚动到按钮。 */}
              <View style={[styles.modalActions, styles.openItemModalActions]}>
                <ActionButton
                  label={t("discount.cancel")}
                  onPress={closeOpenItemInput}
                  sound="navigate"
                  style={[styles.modalAction, styles.openItemModalAction]}
                  tone="secondary"
                />
                {state.errorCode === "terminal-recovery-required" ? (
                  onOpenHeldOrders ? (
                    <ActionButton
                      label={t("openItem.resolveRecovery")}
                      onPress={() => {
                        closeOpenItemInput();
                        onOpenHeldOrders();
                      }}
                      sound="navigate"
                      style={[styles.modalAction, styles.openItemModalAction]}
                      testID="sales-open-item-recovery-action"
                    />
                  ) : null
                ) : (
                  <ActionButton
                    label={t("openItem.confirm")}
                    onPress={() => {
                      void presenter
                        .addOpenItem(parseCashInput(openItemPrice) ?? 0)
                        .then((added) => {
                          if (added) closeOpenItemInput();
                        });
                    }}
                    style={[styles.modalAction, styles.openItemModalAction]}
                    testID="sales-open-item-confirm"
                  />
                )}
              </View>
            </ScrollView>
          </HandheldStateSurface>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={() => setDiscountLineId(null)}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={discountLineId !== null}
      >
        <View style={styles.modalBackdrop}>
          <PosPressable
            accessible={false}
            onPress={() => setDiscountLineId(null)}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="sales-discount-backdrop"
          />
          <View
            accessibilityViewIsModal
            style={styles.discountModal}
            testID="sales-discount-modal"
          >
            <Text style={styles.modalTitle}>{t("discount.title")}</Text>
            <Text style={styles.discountHint}>{t("discount.hint")}</Text>
            <View style={styles.discountGrid}>
              {QUICK_DISCOUNTS.map((basisPoints) => (
                <ActionButton
                  key={basisPoints}
                  label={
                    basisPoints === 0
                      ? t("discount.none")
                      : `${basisPoints / 100}%`
                  }
                  onPress={() => {
                    const lineId = discountLineId;
                    setDiscountLineId(null);
                    if (lineId) {
                      void presenter.applyLineDiscount(lineId, basisPoints);
                    }
                  }}
                  style={styles.discountButton}
                  testID={`sales-discount-${basisPoints}`}
                  tone={basisPoints === 0 ? "quiet" : "secondary"}
                />
              ))}
            </View>
            <View style={styles.modalActions}>
              <ActionButton
                label={t("discount.amount")}
                onPress={() => {
                  const lineId = discountLineId;
                  setDiscountLineId(null);
                  if (lineId) {
                    setLineEdit({
                      lineId,
                      mode: "discount-amount",
                      replaceOnNextDigit: false,
                      value: "",
                    });
                    beginNumericInput();
                  }
                }}
                sound="navigate"
                style={styles.modalAction}
                testID="sales-line-discount-amount"
                tone="secondary"
              />
              <ActionButton
                label={t("discount.percent")}
                onPress={() => {
                  const lineId = discountLineId;
                  setDiscountLineId(null);
                  if (lineId) {
                    setLineEdit({
                      lineId,
                      mode: "discount-percent",
                      replaceOnNextDigit: false,
                      value: "",
                    });
                    beginNumericInput();
                  }
                }}
                sound="navigate"
                style={styles.modalAction}
                testID="sales-line-discount-percent"
                tone="secondary"
              />
            </View>
            <ActionButton
              label={t("discount.cancel")}
              onPress={() => setDiscountLineId(null)}
              sound="navigate"
              style={styles.modalCancelAction}
              testID="sales-discount-cancel"
              tone="quiet"
            />
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={closeLineEditInput}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={lineEdit !== null}
      >
        <View style={styles.modalBackdrop}>
          <PosPressable
            accessible={false}
            onPress={closeLineEditInput}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="sales-line-edit-backdrop"
          />
          <View
            accessibilityViewIsModal
            style={styles.numericModal}
            testID="sales-line-edit-modal"
          >
            <Text style={styles.modalTitle}>{t("editLine.title")}</Text>
            <View style={styles.editModeGrid}>
              <ActionButton
                label={t("editLine.quantity")}
                onPress={() => selectLineEditMode("quantity")}
                style={styles.editModeButton}
                testID="sales-line-edit-quantity"
                tone={lineEdit?.mode === "quantity" ? "primary" : "secondary"}
              />
              <ActionButton
                label={t("editLine.price")}
                onPress={() => selectLineEditMode("price")}
                style={styles.editModeButton}
                testID="sales-line-edit-price"
                tone={lineEdit?.mode === "price" ? "primary" : "secondary"}
              />
              <ActionButton
                label={t("editLine.discountAmount")}
                onPress={() => selectLineEditMode("discount-amount")}
                style={styles.editModeButton}
                testID="sales-line-edit-discount-amount"
                tone={
                  lineEdit?.mode === "discount-amount" ? "primary" : "secondary"
                }
              />
              <ActionButton
                label={t("editLine.discountPercent")}
                onPress={() => selectLineEditMode("discount-percent")}
                style={styles.editModeButton}
                testID="sales-line-edit-discount-percent"
                tone={
                  lineEdit?.mode === "discount-percent"
                    ? "primary"
                    : "secondary"
                }
              />
            </View>
            <View style={styles.numericEditorBody}>
              <View style={styles.numericEditorSummary}>
                <Text style={styles.fieldLabel}>{t("editLine.value")}</Text>
                <NumericValueDisplay
                  accessibilityLabel={t("editLine.value")}
                  placeholder={lineEdit?.mode === "quantity" ? "0" : "0.00"}
                  testID="sales-line-edit-value"
                  value={lineEdit?.value ?? ""}
                />
              </View>
              <View style={styles.numericKeypadColumn}>
                <SalesNumberKeypad
                  labels={keypadLabels}
                  mode={lineEdit?.mode === "quantity" ? "integer" : "decimal"}
                  onKeyPress={handleLineEditKey}
                  testIDPrefix="sales-line-edit"
                />
              </View>
            </View>
            <View style={styles.modalActions}>
              <ActionButton
                label={t("discount.cancel")}
                onPress={closeLineEditInput}
                sound="navigate"
                style={styles.modalAction}
                tone="secondary"
              />
              <ActionButton
                label={t("editLine.confirm")}
                onPress={submitLineEdit}
                style={styles.modalAction}
                testID="sales-line-edit-confirm"
              />
            </View>
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={() => setOrderDiscountVisible(false)}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={orderDiscountVisible}
      >
        <View style={styles.modalBackdrop}>
          <PosPressable
            accessible={false}
            onPress={() => setOrderDiscountVisible(false)}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="sales-order-discount-backdrop"
          />
          <View
            accessibilityViewIsModal
            style={styles.discountModal}
            testID="sales-order-discount-modal"
          >
            <Text style={styles.modalTitle}>{t("orderDiscount.title")}</Text>
            <Text style={styles.discountHint}>{t("orderDiscount.hint")}</Text>
            <View style={styles.discountGrid}>
              {QUICK_ORDER_DISCOUNTS.map((basisPoints) => (
                <ActionButton
                  key={basisPoints}
                  label={`${basisPoints / 100}%`}
                  onPress={() => {
                    void presenter
                      .applyOrderQuickDiscount(basisPoints)
                      .then((applied) => {
                        if (applied) setOrderDiscountVisible(false);
                      });
                  }}
                  style={styles.discountButton}
                  testID={`sales-order-discount-${basisPoints}`}
                  tone="secondary"
                />
              ))}
            </View>
            <View style={styles.modalActions}>
              <ActionButton
                label={t("discount.amount")}
                onPress={() => {
                  setOrderDiscountVisible(false);
                  setOrderEdit({ mode: "amount", value: "" });
                  beginNumericInput();
                }}
                sound="navigate"
                style={styles.modalAction}
                testID="sales-order-discount-amount"
                tone="secondary"
              />
              <ActionButton
                label={t("discount.percent")}
                onPress={() => {
                  setOrderDiscountVisible(false);
                  setOrderEdit({ mode: "percent", value: "" });
                  beginNumericInput();
                }}
                sound="navigate"
                style={styles.modalAction}
                testID="sales-order-discount-percent"
                tone="secondary"
              />
            </View>
            <ActionButton
              label={t("discount.cancel")}
              onPress={() => setOrderDiscountVisible(false)}
              sound="navigate"
              style={styles.modalCancelAction}
              tone="quiet"
            />
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={closeOrderEditInput}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={orderEdit !== null}
      >
        <View style={styles.modalBackdrop}>
          <PosPressable
            accessible={false}
            onPress={closeOrderEditInput}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="sales-order-edit-backdrop"
          />
          <View
            accessibilityViewIsModal
            style={styles.numericModal}
            testID="sales-order-edit-modal"
          >
            <Text style={styles.modalTitle}>
              {t("orderDiscount.editTitle")}
            </Text>
            <View style={styles.numericEditorBody}>
              <View style={styles.numericEditorSummary}>
                <Text style={styles.fieldLabel}>
                  {orderEdit?.mode === "percent"
                    ? t("editLine.discountPercent")
                    : t("editLine.discountAmount")}
                </Text>
                <NumericValueDisplay
                  accessibilityLabel={t("editLine.value")}
                  placeholder="0.00"
                  testID="sales-order-edit-value"
                  value={orderEdit?.value ?? ""}
                />
              </View>
              <View style={styles.numericKeypadColumn}>
                <SalesNumberKeypad
                  labels={keypadLabels}
                  mode="decimal"
                  onKeyPress={handleOrderEditKey}
                  testIDPrefix="sales-order-edit"
                />
              </View>
            </View>
            <View style={styles.modalActions}>
              <ActionButton
                label={t("discount.cancel")}
                onPress={closeOrderEditInput}
                sound="navigate"
                style={styles.modalAction}
                tone="secondary"
              />
              <ActionButton
                label={t("editLine.confirm")}
                onPress={submitOrderEdit}
                style={styles.modalAction}
                testID="sales-order-edit-confirm"
              />
            </View>
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={() => setClearCartVisible(false)}
        presentationStyle="overFullScreen"
        supportedOrientations={["portrait"]}
        transparent
        visible={clearCartVisible}
      >
        <View style={styles.modalBackdrop}>
          <PosPressable
            accessible={false}
            onPress={() => setClearCartVisible(false)}
            sound="navigate"
            style={styles.modalDismissArea}
            testID="sales-clear-cart-backdrop"
          />
          <View
            accessibilityViewIsModal
            style={styles.editorModal}
            testID="sales-clear-cart-modal"
          >
            <Text style={styles.modalTitle}>{t("clearCart.title")}</Text>
            <Text style={styles.discountHint}>{t("clearCart.hint")}</Text>
            <View style={styles.modalActions}>
              <ActionButton
                label={t("clearCart.cancel")}
                onPress={() => setClearCartVisible(false)}
                sound="navigate"
                style={styles.modalAction}
                testID="sales-clear-cart-cancel"
                tone="secondary"
              />
              <ActionButton
                label={t("clearCart.confirm")}
                onPress={() => {
                  void presenter.clearCart().then((cleared) => {
                    if (cleared) setClearCartVisible(false);
                  });
                }}
                style={styles.modalAction}
                testID="sales-clear-cart-confirm"
                tone="danger"
              />
            </View>
          </View>
        </View>
      </Modal>
    </HandheldScreenFrame>
  );
}

function ProductSearchRow({
  disabled = false,
  item,
  locale,
  onAdd,
  t,
}: Readonly<{
  disabled?: boolean;
  item: SalesProductSearchItem;
  locale: SalesLocale;
  onAdd(): void;
  t(
    key: SalesCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ): string;
}>) {
  return (
    <View style={styles.productRow}>
      <View style={styles.productIdentity}>
        <Text numberOfLines={2} style={styles.productName}>
          {item.displayName}
        </Text>
        <Text numberOfLines={1} style={styles.productCode}>
          {item.lookupCode}
        </Text>
      </View>
      <Text style={styles.productPrice}>
        {formatAud(item.unitPriceCents, locale)}
      </Text>
      <ActionButton
        disabled={disabled}
        label={t("catalog.add")}
        onPress={onAdd}
        testID={`sales-product-${item.productCode}-add`}
        tone="secondary"
      />
    </View>
  );
}

function priceSourceCopyKey(
  priceSource: SalesLookupCandidate["priceSource"],
): SalesCopyKey {
  switch (priceSource) {
    case 1:
      return "priceSource.1";
    case 2:
      return "priceSource.2";
    case 3:
      return "priceSource.3";
    case 4:
      return "priceSource.4";
    case 0:
    default:
      return "priceSource.0";
  }
}

/**
 * 一码多商品候选行：窄屏下整行即选择按钮，名称可两行显示，
 * 货号与编码各占一行，价格来源与价格同排，避免横向挤压。
 */
function LookupCandidateRow({
  disabled = false,
  item,
  locale,
  onChoose,
  resolveProductImage,
  t,
}: Readonly<{
  disabled?: boolean;
  item: SalesLookupCandidate;
  locale: SalesLocale;
  onChoose(): void;
  resolveProductImage?: (input: {
    productCode: string;
    lookupCode: string;
  }) => Promise<string | null>;
  t(
    key: SalesCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ): string;
}>) {
  const [imageUri, setImageUri] = useState<string | null | undefined>();
  useEffect(() => {
    setImageUri(undefined);
    if (!resolveProductImage) return;
    let active = true;
    void resolveProductImage({
      productCode: item.productCode,
      lookupCode: item.lookupCode,
    })
      .then((resolved) => {
        if (active) setImageUri(resolved?.trim() || null);
      })
      .catch(() => {
        if (active) setImageUri(null);
      });
    return () => {
      active = false;
    };
  }, [item.lookupCode, item.productCode, resolveProductImage]);

  const price = formatAud(item.unitPriceCents, locale);
  return (
    <PosPressable
      accessibilityLabel={t("lookupSelection.chooseProduct", {
        product: item.displayName,
        price,
      })}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onChoose}
      style={({ pressed }) => [
        styles.lookupCandidateRow,
        disabled && styles.actionButtonDisabled,
        pressed && !disabled && styles.actionButtonPressed,
      ]}
      testID={`sales-lookup-candidate-${item.productCode}`}
    >
      <CartProductThumbnail
        accessibilityLabel={t("cart.productImage", {
          product: item.displayName,
        })}
        imageUri={imageUri}
        placeholderLabel={t("cart.imagePlaceholder")}
        testID={`sales-lookup-candidate-${item.productCode}-image`}
      />
      <View style={styles.lookupCandidateBody}>
        <Text numberOfLines={2} style={styles.productName}>
          {item.displayName}
        </Text>
        <Text numberOfLines={1} style={styles.productCode}>
          {t("lookupSelection.itemNumber", {
            value: item.itemNumber?.trim() || "—",
          })}
        </Text>
        <Text numberOfLines={1} style={styles.productCode}>
          {t("lookupSelection.lookupCode", { value: item.lookupCode })}
        </Text>
        <View style={styles.lookupCandidateFooter}>
          <Text
            numberOfLines={1}
            style={styles.lookupCandidateSource}
            testID={`sales-lookup-candidate-${item.productCode}-source`}
          >
            {t(priceSourceCopyKey(item.priceSource))}
          </Text>
          <Text
            style={styles.lookupCandidatePrice}
            testID={`sales-lookup-candidate-${item.productCode}-price`}
          >
            {price}
          </Text>
        </View>
      </View>
    </PosPressable>
  );
}

type CartLineRowProps = Readonly<{
  compact: boolean;
  disabled: boolean;
  isSelected: boolean;
  item: CartLine;
  lineNumber: number;
  locale: SalesLocale;
  onDecrease(lineId: string): void;
  onDiscount(lineId: string): void;
  onEdit(lineId: string): void;
  onIncrease(lineId: string): void;
  onRemove(lineId: string): void;
  onSelect(lineId: string): void;
  resolveProductImage?: (input: {
    productCode: string;
    lookupCode: string;
  }) => Promise<string | null>;
  t(
    key: SalesCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ): string;
}>;

const CartLineRow = memo(function CartLineRow({
  compact,
  disabled,
  isSelected,
  item,
  lineNumber,
  locale,
  onDecrease,
  onDiscount,
  onEdit,
  onIncrease,
  onRemove,
  onSelect,
  resolveProductImage,
  t,
}: CartLineRowProps) {
  const [imageUri, setImageUri] = useState<string | null | undefined>();
  const [removeActionVisible, setRemoveActionVisible] = useState(false);
  const swipeOffset = useRef(new Animated.Value(0)).current;
  const swipeStartOffsetRef = useRef(0);
  const removeActionVisibleRef = useRef(false);

  const settleSwipe = useCallback(
    (visible: boolean): void => {
      removeActionVisibleRef.current = visible;
      setRemoveActionVisible(visible);
      Animated.spring(swipeOffset, {
        bounciness: 0,
        speed: 24,
        toValue: visible ? -CART_LINE_REMOVE_ACTION_WIDTH : 0,
        useNativeDriver: true,
      }).start();
    },
    [swipeOffset],
  );

  const removeLine = useCallback((): void => {
    settleSwipe(false);
    onRemove(item.lineId);
  }, [item.lineId, onRemove, settleSwipe]);

  const swipeResponder = useMemo(() => {
    const shouldBeginSwipe = (dx: number, dy: number): boolean =>
      !disabled &&
      Math.abs(dx) >= CART_LINE_SWIPE_ACTIVATION_DISTANCE &&
      Math.abs(dx) > Math.abs(dy) &&
      (dx < 0 || removeActionVisibleRef.current);

    return PanResponder.create({
      onMoveShouldSetPanResponder: (_event, gestureState) =>
        shouldBeginSwipe(gestureState.dx, gestureState.dy),
      onMoveShouldSetPanResponderCapture: (_event, gestureState) =>
        shouldBeginSwipe(gestureState.dx, gestureState.dy),
      onPanResponderGrant: () => {
        swipeOffset.stopAnimation();
        swipeStartOffsetRef.current = removeActionVisibleRef.current
          ? -CART_LINE_REMOVE_ACTION_WIDTH
          : 0;
      },
      onPanResponderMove: (_event, gestureState) => {
        const nextOffset = Math.max(
          -CART_LINE_REMOVE_ACTION_WIDTH,
          Math.min(0, swipeStartOffsetRef.current + gestureState.dx),
        );
        swipeOffset.setValue(nextOffset);
      },
      onPanResponderRelease: (_event, gestureState) => {
        const projectedOffset =
          swipeStartOffsetRef.current + gestureState.dx + gestureState.vx * 24;
        settleSwipe(projectedOffset <= -CART_LINE_REMOVE_ACTION_WIDTH / 2);
      },
      onPanResponderTerminate: () => {
        settleSwipe(removeActionVisibleRef.current);
      },
      onPanResponderTerminationRequest: () => false,
      onShouldBlockNativeResponder: () => true,
    });
  }, [disabled, settleSwipe, swipeOffset]);

  useEffect(() => {
    if (disabled && removeActionVisibleRef.current) settleSwipe(false);
  }, [disabled, settleSwipe]);

  useEffect(() => {
    setImageUri(undefined);
    if (!resolveProductImage) return;
    let active = true;
    void resolveProductImage({
      productCode: item.productCode,
      lookupCode: item.lookupCode,
    })
      .then((resolved) => {
        if (active) setImageUri(resolved?.trim() || null);
      })
      .catch(() => {
        if (active) setImageUri(null);
      });
    return () => {
      active = false;
    };
  }, [item.lookupCode, item.productCode, resolveProductImage]);

  return (
    <View style={styles.cartLineSwipeContainer}>
      {/* 手势壳覆盖整行；仅展开后抬高删除层，保证 Android 命中真实按钮。 */}
      <View
        accessibilityElementsHidden={!removeActionVisible}
        importantForAccessibility={
          removeActionVisible ? "auto" : "no-hide-descendants"
        }
        pointerEvents={removeActionVisible ? "auto" : "none"}
        style={[
          styles.cartLineRemoveAction,
          removeActionVisible && styles.cartLineRemoveActionInteractive,
        ]}
        testID={`sales-line-${item.lineId}-remove-action`}
      >
        <ActionButton
          disabled={disabled}
          label={t("cart.remove")}
          onPress={removeLine}
          style={styles.cartLineRemoveButton}
          testID={`sales-line-${item.lineId}-remove`}
          tone="danger"
        />
      </View>
      <PosPanResponderView
        panHandlers={swipeResponder.panHandlers}
        testID={`sales-line-${item.lineId}-swipe-surface`}
      >
        <Animated.View
          style={[
            styles.cartLineSwipeSurface,
            { transform: [{ translateX: swipeOffset }] },
          ]}
        >
          <PosPressable
            accessibilityActions={[{ label: t("cart.remove"), name: "delete" }]}
            accessibilityRole="button"
            accessibilityState={{ selected: isSelected }}
            onAccessibilityAction={(event) => {
              if (event.nativeEvent.actionName === "delete" && !disabled) {
                removeLine();
              }
            }}
            onPress={() => {
              if (removeActionVisibleRef.current) {
                settleSwipe(false);
                return;
              }
              onSelect(item.lineId);
            }}
            style={({ pressed }) => [
              styles.cartLine,
              compact && styles.cartLineCompact,
              item.actualAmount.cents < 0 && styles.cartLineNegative,
              item.actualAmount.cents === 0 && styles.cartLineZero,
              isSelected && styles.cartLineSelected,
              pressed && styles.cartLinePressed,
            ]}
            testID={`sales-line-${item.lineId}`}
          >
            <View
              style={[styles.cartLineTop, compact && styles.cartLineTopCompact]}
            >
        <Text
          accessibilityLabel={t("cart.lineNumber", {
            number: lineNumber,
          })}
          style={styles.cartLineNumber}
          testID={`sales-line-${item.lineId}-line-number`}
        >
          {lineNumber}
        </Text>
        <CartProductThumbnail
          accessibilityLabel={t("cart.productImage", {
            product: item.displayName,
          })}
          imageUri={imageUri}
          placeholderLabel={t("cart.imagePlaceholder")}
          testID={`sales-line-${item.lineId}-image`}
        />
        <View
          style={[
            styles.cartLineIdentity,
            compact && styles.cartLineIdentityCompact,
          ]}
          testID={`sales-line-${item.lineId}-identity`}
        >
          <Text numberOfLines={2} style={styles.cartLineName}>
            {item.displayName}
          </Text>
          <Text numberOfLines={1} style={styles.cartLineCode}>
            {item.lookupCode || item.productCode}
          </Text>
          <Text style={styles.cartLineUnitPrice}>
            {formatAud(item.unitPrice.cents, locale)}
            {item.discount.cents > 0 ? (
              <Text
                style={styles.discountAmountText}
                testID={`sales-line-${item.lineId}-discount-amount`}
              >
                {`  −${formatAud(item.discount.cents, locale)}`}
              </Text>
            ) : null}
          </Text>
        </View>
        <Text
          style={[
            styles.cartLineTotal,
            compact && styles.cartLineTotalCompact,
          ]}
          testID={`sales-line-${item.lineId}-total`}
        >
          {formatAud(item.actualAmount.cents, locale)}
        </Text>
      </View>
          <View
            style={styles.lineControls}
            testID={`sales-line-${item.lineId}-controls`}
          >
        <ActionButton
          accessibilityLabel={t("cart.decrease")}
          disabled={disabled}
          label="−"
          onPress={() => onDecrease(item.lineId)}
          testID={`sales-line-${item.lineId}-decrease`}
          tone="quiet"
        />
        <View
          accessibilityLabel={`${t("cart.quantity")}: ${item.quantity}`}
          style={styles.quantityValue}
        >
          <Text style={styles.quantityText}>{item.quantity}</Text>
        </View>
        <ActionButton
          accessibilityLabel={t("cart.increase")}
          disabled={disabled}
          label="+"
          onPress={() => onIncrease(item.lineId)}
          testID={`sales-line-${item.lineId}-increase`}
          tone="quiet"
        />
        <ActionButton
          disabled={disabled}
          label={t("cart.edit")}
          onPress={() => onEdit(item.lineId)}
          testID={`sales-line-${item.lineId}-edit`}
          tone="quiet"
        />
        <ActionButton
          disabled={disabled}
          label={t("cart.discount")}
          onPress={() => onDiscount(item.lineId)}
          testID={`sales-line-${item.lineId}-discount`}
          tone="secondary"
        />
            </View>
          </PosPressable>
        </Animated.View>
      </PosPanResponderView>
    </View>
  );
}, cartLineRowPropsEqual);

function cartLineRowPropsEqual(
  previous: CartLineRowProps,
  next: CartLineRowProps,
): boolean {
  const before = previous.item;
  const after = next.item;
  return (
    previous.compact === next.compact &&
    previous.disabled === next.disabled &&
    previous.lineNumber === next.lineNumber &&
    previous.isSelected === next.isSelected &&
    previous.locale === next.locale &&
    previous.onDecrease === next.onDecrease &&
    previous.onDiscount === next.onDiscount &&
    previous.onEdit === next.onEdit &&
    previous.onIncrease === next.onIncrease &&
    previous.onRemove === next.onRemove &&
    previous.onSelect === next.onSelect &&
    previous.resolveProductImage === next.resolveProductImage &&
    previous.t === next.t &&
    before.lineId === after.lineId &&
    before.productCode === after.productCode &&
    before.lookupCode === after.lookupCode &&
    before.displayName === after.displayName &&
    before.quantity === after.quantity &&
    before.unitPrice.cents === after.unitPrice.cents &&
    before.discount.cents === after.discount.cents &&
    before.actualAmount.cents === after.actualAmount.cents &&
    before.priceSource === after.priceSource
  );
}

function CartProductThumbnail({
  accessibilityLabel,
  imageUri,
  placeholderLabel,
  testID,
}: Readonly<{
  accessibilityLabel: string;
  imageUri: string | null | undefined;
  placeholderLabel: string;
  testID: string;
}>) {
  const [failedUri, setFailedUri] = useState<string | null>(null);
  const showImage = Boolean(imageUri) && failedUri !== imageUri;
  return (
    <View
      accessible
      accessibilityLabel={accessibilityLabel}
      style={styles.cartProductImageFrame}
      testID={testID}
    >
      {showImage ? (
        <Image
          accessible={false}
          onError={() => setFailedUri(imageUri ?? null)}
          resizeMode="contain"
          source={{ uri: imageUri as string }}
          style={styles.cartProductImage}
          testID={`${testID}-content`}
        />
      ) : (
        <Text
          accessibilityElementsHidden
          importantForAccessibility="no-hide-descendants"
          style={styles.cartProductImagePlaceholder}
        >
          {placeholderLabel}
        </Text>
      )}
    </View>
  );
}

function NumericValueDisplay({
  accessibilityLabel,
  currencyPrefix,
  placeholder,
  testID,
  value,
}: NumericValueDisplayProps) {
  const displayValue = value || placeholder;
  return (
    <View
      accessible
      accessibilityLabel={accessibilityLabel}
      accessibilityLiveRegion="polite"
      accessibilityValue={{ text: displayValue }}
      style={styles.numericValueDisplay}
      testID={testID}
    >
      {currencyPrefix ? (
        <Text style={styles.numericValuePrefix}>{currencyPrefix}</Text>
      ) : null}
      <Text
        adjustsFontSizeToFit
        minimumFontScale={0.65}
        numberOfLines={1}
        style={[
          styles.numericValueText,
          value.length === 0 && styles.numericValuePlaceholder,
        ]}
      >
        {displayValue}
      </Text>
    </View>
  );
}

function ProductEntryFrame({
  children,
  compact,
}: Readonly<{ children: ReactNode; compact: boolean }>) {
  if (!compact) {
    return (
      <HandheldSection testID="sales-product-entry">{children}</HandheldSection>
    );
  }
  return (
    <View style={styles.compactProductEntry} testID="sales-product-entry">
      {children}
    </View>
  );
}

function CompactSummaryAction({
  accessibilityLabel,
  disabled,
  icon,
  onPress,
  testID,
  tone,
}: Readonly<{
  accessibilityLabel: string;
  disabled: boolean;
  icon: "delete-outline" | "percent-outline";
  onPress(): void;
  testID: string;
  tone: "danger" | "secondary";
}>) {
  const iconColor = disabled
    ? "#7C8287"
    : tone === "danger"
      ? posColors.red
      : posColors.ink;
  return (
    <PosPressable
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound={tone === "danger" ? "danger" : "navigate"}
      style={({ pressed }) => [
        styles.compactSummaryAction,
        tone === "danger"
          ? styles.compactSummaryActionDanger
          : styles.compactSummaryActionSecondary,
        disabled && styles.actionButtonDisabled,
        pressed && !disabled && styles.actionButtonPressed,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons color={iconColor} name={icon} size={22} />
    </PosPressable>
  );
}

function SummaryRow({
  amount,
  amountTestID,
  amountTone = "default",
  label,
  muted = false,
}: Readonly<{
  amount: string;
  amountTestID?: string;
  amountTone?: "default" | "danger";
  label: string;
  muted?: boolean;
}>) {
  return (
    <View style={styles.summaryRow}>
      <Text style={[styles.summaryLabel, muted && styles.mutedText]}>
        {label}
      </Text>
      <Text
        style={[
          styles.summaryAmount,
          muted && styles.mutedText,
          amountTone === "danger" && styles.discountAmountText,
        ]}
        testID={amountTestID}
      >
        {amount}
      </Text>
    </View>
  );
}

function ErrorBanner({
  dismissLabel,
  message,
  onDismiss,
}: Readonly<{
  dismissLabel: string;
  message: string;
  onDismiss(): void;
}>) {
  return (
    <View accessibilityRole="alert" style={styles.errorBanner}>
      <Text style={styles.errorBannerText}>{message}</Text>
      <PosPressable
        accessibilityLabel={dismissLabel}
        accessibilityRole="button"
        hitSlop={8}
        onPress={onDismiss}
        sound="navigate"
        style={styles.errorDismiss}
      >
        <Text style={styles.errorDismissText}>×</Text>
      </PosPressable>
    </View>
  );
}

function ActionButton({
  accessibilityLabel,
  disabled = false,
  label,
  onPress,
  sound,
  style,
  testID,
  tone = "primary",
}: ActionButtonProps) {
  return (
    <PosPressable
      accessibilityLabel={accessibilityLabel ?? label}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound={sound ?? (tone === "danger" ? "danger" : "tap")}
      style={({ pressed }) => [
        styles.actionButton,
        actionToneStyles[tone],
        disabled && styles.actionButtonDisabled,
        pressed && !disabled && styles.actionButtonPressed,
        style,
      ]}
      testID={testID}
    >
      <Text
        style={[
          styles.actionButtonText,
          (tone === "quiet" || tone === "secondary") && styles.quietButtonText,
          tone === "danger" && styles.dangerButtonText,
          tone === "info" && styles.infoButtonText,
          tone === "warning" && styles.warningButtonText,
          disabled && styles.actionButtonTextDisabled,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function CatalogIconButton({
  accessibilityLabel,
  disabled,
  icon,
  iconTestID,
  loading = false,
  onPress,
  testID,
}: Readonly<{
  accessibilityLabel: string;
  disabled: boolean;
  icon: "camera-outline" | "keyboard-outline" | "magnify";
  iconTestID: string;
  loading?: boolean;
  onPress(): void;
  testID: string;
}>) {
  return (
    <PosPressable
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      accessibilityState={{ busy: loading, disabled }}
      disabled={disabled}
      onPress={onPress}
      sound="navigate"
      style={({ pressed }) => [
        styles.actionButton,
        actionToneStyles.secondary,
        styles.searchIconAction,
        disabled && styles.actionButtonDisabled,
        pressed && !disabled && styles.actionButtonPressed,
      ]}
      testID={testID}
    >
      {loading ? (
        <ActivityIndicator
          color={posColors.ink}
          size="small"
          testID="sales-search-progress"
        />
      ) : (
        <MaterialCommunityIcons
          color={posColors.ink}
          name={icon}
          size={24}
          testID={iconTestID}
        />
      )}
    </PosPressable>
  );
}

function cartLineKeyExtractor(line: CartLine): string {
  return line.lineId;
}

function errorCopyKey(errorCode: string): SalesCopyKey {
  switch (errorCode) {
    case "search-required":
      return "error.searchRequired";
    case "search-failed":
      return "error.searchFailed";
    case "product-add-failed":
      return "error.productAddFailed";
    case "terminal-recovery-required":
      return "error.terminalRecoveryRequired";
    case "authorization-denied":
      return "error.authorizationDenied";
    case "cart-update-failed":
      return "error.cartUpdateFailed";
    case "invalid-quantity":
      return "error.invalidQuantity";
    case "invalid-price":
      return "error.invalidPrice";
    case "invalid-discount":
      return "error.invalidDiscount";
    case "empty-cart":
      return "error.emptyCart";
    case "cash-invalid":
      return "error.cashInvalid";
    case "cash-insufficient":
      return "error.cashInsufficient";
    case "cash-failed":
      return "error.cashFailed";
    case "cart-clear-failed":
      return "error.cartClearFailed";
    case "hold-failed":
      return "error.holdFailed";
    case "lock-failed":
      return "error.lockFailed";
    case "new-transactions-disabled":
      return "error.newTransactionsDisabled";
    case "network-unavailable":
      return "error.networkUnavailable";
    case "runtime-unavailable":
    default:
      return "error.runtimeUnavailable";
  }
}

function utilityResultCopyKey(
  action: SalesUtilityAction,
  result: SalesUtilityActionResult["kind"],
): SalesCopyKey {
  if (result === "completed") {
    return action === "reprint"
      ? "functions.reprintCompleted"
      : "functions.drawerCompleted";
  }
  if (result === "not-found") {
    return action === "reprint"
      ? "functions.reprintNotFound"
      : "functions.drawerUnavailable";
  }
  if (result === "denied") {
    return "functions.actionDenied";
  }
  if (result === "unavailable") {
    return action === "reprint"
      ? "functions.reprintUnavailable"
      : "functions.drawerUnavailable";
  }
  if (result === "unknown") {
    return "functions.actionUnknown";
  }
  return action === "reprint"
    ? "functions.reprintFailed"
    : "functions.drawerFailed";
}

function parsePositiveInteger(value: string): number | null {
  const normalized = value.trim();
  if (!/^\d+$/.test(normalized)) return null;
  const quantity = Number(normalized);
  return Number.isSafeInteger(quantity) && quantity > 0 ? quantity : null;
}

function parsePercentageBasisPoints(value: string): number | null {
  const normalized = value.trim().replace(/%$/, "").trim();
  const match = /^(\d{1,3})(?:\.(\d{1,2}))?$/.exec(normalized);
  if (!match) return null;
  const whole = Number(match[1]);
  const fraction = Number((match[2] ?? "").padEnd(2, "0"));
  if (whole > 100 || (whole === 100 && fraction > 0)) return null;
  return whole * 100 + fraction;
}

function formatEditorMoney(cents: number): string {
  return `${Math.floor(cents / 100)}.${String(cents % 100).padStart(2, "0")}`;
}

const actionToneStyles = StyleSheet.create({
  danger: {
    backgroundColor: posColors.redSoft,
    borderColor: posColors.red,
  },
  info: {
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.blue,
  },
  primary: {
    backgroundColor: posColors.orange,
    borderColor: posColors.orange,
  },
  quiet: {
    backgroundColor: "transparent",
    borderColor: posColors.border,
  },
  secondary: {
    backgroundColor: posColors.surface,
    borderColor: posColors.ink,
  },
  warning: {
    backgroundColor: posColors.yellowSoft,
    borderColor: posColors.yellow,
  },
});

const styles = StyleSheet.create({
  actionButton: {
    alignItems: "center",
    borderRadius: 4,
    borderWidth: 1,
    justifyContent: "center",
    minHeight: MIN_TOUCH_TARGET,
    minWidth: MIN_TOUCH_TARGET,
    paddingHorizontal: 14,
  },
  actionButtonDisabled: {
    backgroundColor: "#E4E1DA",
    borderColor: "#D0CCC2",
    opacity: 0.72,
  },
  actionButtonPressed: {
    opacity: 0.72,
    transform: [{ scale: 0.99 }],
  },
  actionButtonText: {
    color: "#FFFFFF",
    fontSize: 14,
    fontWeight: "800",
  },
  actionButtonTextDisabled: {
    color: "#7C8287",
  },
  brand: {
    color: posColors.ink,
    fontSize: 19,
    fontWeight: "900",
    letterSpacing: 0.4,
  },
  cartCount: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
  },
  cartHeader: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  cartLine: {
    alignItems: "stretch",
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 4,
    borderWidth: 1,
    gap: 8,
    minHeight: 120,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  cartLineCompact: {
    paddingHorizontal: 10,
  },
  cartLineRemoveAction: {
    bottom: 0,
    position: "absolute",
    right: 0,
    top: 0,
    width: CART_LINE_REMOVE_ACTION_WIDTH,
  },
  cartLineRemoveActionInteractive: {
    zIndex: 1,
  },
  cartLineRemoveButton: {
    borderRadius: 0,
    flex: 1,
    paddingHorizontal: 8,
  },
  cartLineSwipeContainer: {
    borderRadius: 4,
    overflow: "hidden",
    position: "relative",
  },
  cartLineSwipeSurface: {
    backgroundColor: posColors.surface,
  },
  cartLineNegative: {
    backgroundColor: posColors.redSoft,
    borderColor: posColors.red,
  },
  cartLinePressed: {
    opacity: 0.82,
  },
  cartLineSelected: {
    backgroundColor: posColors.greenSoft,
    borderColor: posColors.green,
  },
  cartLineZero: {
    backgroundColor: posColors.yellowSoft,
    borderColor: posColors.yellow,
  },
  cartLineCode: {
    color: posColors.mutedInk,
    fontSize: 11,
    fontVariant: ["tabular-nums"],
  },
  cartLineIdentity: {
    flex: 1,
    minWidth: 120,
  },
  cartLineIdentityCompact: {
    minWidth: 0,
  },
  cartLineName: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 19,
  },
  cartLineNumber: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
    textAlign: "center",
    width: 24,
  },
  cartLineTotal: {
    color: posColors.ink,
    fontSize: 17,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    minWidth: 80,
    textAlign: "right",
  },
  cartLineTotalCompact: {
    flexBasis: "100%",
    minWidth: 0,
  },
  cartLineTop: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
  },
  cartLineTopCompact: {
    alignItems: "flex-start",
    flexWrap: "wrap",
    gap: 8,
  },
  cartActionsGrid: {
    flexDirection: "column",
    gap: 8,
    marginTop: 8,
  },
  cartActionsModal: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    flexDirection: "column",
    maxWidth: 520,
    padding: 16,
    width: "100%",
  },
  cartLineUnitPrice: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    marginTop: 4,
  },
  cartListItem: {
    backgroundColor: "#F8F6F1",
    borderColor: posColors.border,
    borderLeftWidth: 1,
    borderRightWidth: 1,
    paddingBottom: 8,
    paddingHorizontal: 14,
  },
  cartPaneEmptySegment: {
    backgroundColor: "#F8F6F1",
    borderBottomLeftRadius: 5,
    borderBottomRightRadius: 5,
    borderColor: posColors.border,
    borderBottomWidth: 1,
    borderLeftWidth: 1,
    borderRightWidth: 1,
    minHeight: 240,
  },
  cartPaneFooterSegment: {
    backgroundColor: "#F8F6F1",
    borderBottomLeftRadius: 5,
    borderBottomRightRadius: 5,
    borderColor: posColors.border,
    borderBottomWidth: 1,
    borderLeftWidth: 1,
    borderRightWidth: 1,
    height: 6,
  },
  cartPaneHeaderSegment: {
    backgroundColor: "#F8F6F1",
    borderColor: posColors.border,
    borderLeftWidth: 1,
    borderRightWidth: 1,
    borderTopLeftRadius: 5,
    borderTopRightRadius: 5,
    borderTopWidth: 1,
    paddingBottom: 12,
    paddingHorizontal: 14,
    paddingTop: 14,
  },
  compactCartPaneHeaderSegment: {
    height: COMPACT_CART_HEADER_HEIGHT,
    justifyContent: "center",
    minHeight: COMPACT_CART_HEADER_HEIGHT,
    paddingBottom: 0,
    paddingHorizontal: 8,
    paddingTop: 0,
  },
  cartProductImage: {
    height: "100%",
    width: "100%",
  },
  cartProductImageFrame: {
    alignItems: "center",
    backgroundColor: "#EEECE6",
    borderColor: posColors.border,
    borderRadius: 4,
    borderWidth: 1,
    height: 54,
    justifyContent: "center",
    overflow: "hidden",
    width: 54,
  },
  cartProductImagePlaceholder: {
    color: "#8B9399",
    fontSize: 10,
    fontWeight: "900",
    letterSpacing: 0.4,
  },
  catalogPane: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 5,
    borderWidth: 1,
    gap: 10,
    padding: 14,
    width: 252,
  },
  cashDueAmount: {
    color: posColors.ink,
    fontSize: 42,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -1,
    marginBottom: 18,
  },
  cashDueLabel: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "800",
    marginTop: 18,
    textTransform: "uppercase",
  },
  cashError: {
    color: posColors.red,
    fontSize: 13,
    fontWeight: "700",
    marginTop: 12,
  },
  cashExactAction: {
    marginTop: 12,
    width: "100%",
  },
  changeAmount: {
    color: posColors.green,
    fontSize: 52,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -1.5,
  },
  changeAmountCompact: {
    fontSize: 36,
    letterSpacing: -1,
  },
  changeCard: {
    alignItems: "center",
    backgroundColor: posColors.greenSoft,
    borderColor: posColors.green,
    borderRadius: 6,
    borderWidth: 1,
    maxWidth: 360,
    marginTop: 28,
    paddingHorizontal: 36,
    paddingVertical: 20,
    width: "100%",
  },
  changeCardCompact: {
    marginTop: 20,
    paddingHorizontal: 20,
    paddingVertical: 16,
  },
  changeLabel: {
    color: posColors.green,
    fontSize: 13,
    fontWeight: "800",
    textTransform: "uppercase",
  },
  changePreview: {
    alignItems: "center",
    backgroundColor: posColors.greenSoft,
    borderRadius: 4,
    flexDirection: "row",
    justifyContent: "space-between",
    marginTop: 18,
    minHeight: 64,
    paddingHorizontal: 18,
  },
  changePreviewAmount: {
    color: posColors.green,
    fontSize: 26,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  changePreviewLabel: {
    color: posColors.green,
    fontSize: 14,
    fontWeight: "800",
  },
  checkoutButton: {
    minHeight: 64,
    width: "100%",
  },
  checkoutDock: {
    gap: 8,
  },
  compactCheckoutDock: {
    gap: 0,
  },
  compactScreenFooter: {
    borderTopWidth: 0,
    padding: 8,
    paddingBottom: 0,
    paddingHorizontal: 8,
    paddingTop: 8,
  },
  compactTopBar: {
    alignItems: "center",
    backgroundColor: posColors.surface,
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    height: 48,
    minHeight: 48,
    overflow: "hidden",
  },
  compactTopBarEdge: {
    alignItems: "center",
    height: 48,
    justifyContent: "center",
    width: 64,
  },
  compactTopBarStatus: {
    borderBottomWidth: 0,
    flex: 1,
    height: 48,
    minWidth: 0,
  },
  onlineCheckoutButton: {
    marginTop: 10,
    minHeight: 64,
    width: "100%",
  },
  dangerButtonText: {
    color: posColors.red,
  },
  discountAmountText: {
    color: posColors.red,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  discountButton: {
    flexBasis: "29%",
    flexGrow: 1,
  },
  discountGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    marginBottom: 18,
    marginTop: 18,
  },
  discountHint: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 8,
  },
  discountModal: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    maxWidth: 520,
    padding: 16,
    width: "100%",
  },
  editModeButton: {
    flexBasis: "47%",
    flexGrow: 1,
  },
  editModeGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    marginBottom: 18,
    marginTop: 18,
  },
  editorModal: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    maxWidth: 560,
    padding: 16,
    width: "100%",
  },
  emptyCart: {
    alignItems: "center",
    flex: 1,
    justifyContent: "center",
    padding: 24,
  },
  emptyCartHint: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 6,
    maxWidth: 280,
    textAlign: "center",
  },
  emptyCartIcon: {
    color: "#A7AFB5",
    fontSize: 38,
    fontWeight: "300",
  },
  emptyCartTitle: {
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "800",
    marginTop: 8,
  },
  infoButtonText: {
    color: posColors.blue,
  },
  errorBanner: {
    alignItems: "center",
    backgroundColor: posColors.redSoft,
    borderColor: posColors.red,
    borderTopWidth: 1,
    flexDirection: "row",
    minHeight: 48,
    paddingLeft: 18,
  },
  errorBannerText: {
    color: posColors.red,
    flex: 1,
    fontSize: 13,
    fontWeight: "700",
  },
  errorDismiss: {
    alignItems: "center",
    height: MIN_TOUCH_TARGET,
    justifyContent: "center",
    width: MIN_TOUCH_TARGET,
  },
  errorDismissText: {
    color: posColors.red,
    fontSize: 24,
    fontWeight: "700",
  },
  fieldLabel: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
    marginBottom: 7,
  },
  header: {
    alignItems: "center",
    backgroundColor: posColors.surface,
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    justifyContent: "space-between",
    minHeight: 60,
    paddingHorizontal: 20,
    paddingVertical: 8,
  },
  headerActions: {
    flex: 1,
    marginLeft: 20,
    minWidth: 0,
  },
  lineControls: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 5,
    justifyContent: "flex-end",
  },
  lockIcon: {
    alignItems: "center",
    backgroundColor: posColors.redSoft,
    borderRadius: 32,
    height: 64,
    justifyContent: "center",
    width: 64,
  },
  lockIconText: {
    color: posColors.red,
    fontSize: 24,
  },
  lockedHint: {
    color: posColors.mutedInk,
    fontSize: 16,
    lineHeight: 24,
    maxWidth: 520,
    textAlign: "center",
  },
  lockedNotice: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 4,
    borderWidth: 1,
    marginTop: 24,
    paddingHorizontal: 20,
    paddingVertical: 14,
  },
  lockedNoticeText: {
    color: posColors.mutedInk,
    fontSize: 14,
  },
  lockedScreen: {
    alignItems: "center",
    backgroundColor: posColors.canvas,
    flex: 1,
    justifyContent: "center",
    padding: 32,
  },
  lockedTitle: {
    color: posColors.ink,
    fontSize: 30,
    fontWeight: "900",
    marginBottom: 10,
    marginTop: 20,
  },
  lookupCandidateBody: {
    flex: 1,
    gap: 2,
    minWidth: 0,
  },
  lookupCandidateFooter: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    justifyContent: "space-between",
    marginTop: 4,
  },
  lookupCandidatePrice: {
    color: posColors.ink,
    fontSize: 17,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  lookupCandidateRow: {
    alignItems: "flex-start",
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: 88,
    paddingVertical: 10,
  },
  lookupCandidateSource: {
    backgroundColor: posColors.orangeSoft,
    borderRadius: 3,
    color: posColors.orange,
    flexShrink: 1,
    fontSize: 11,
    fontWeight: "900",
    overflow: "hidden",
    paddingHorizontal: 6,
    paddingVertical: 2,
  },
  lookupSelectionCancel: {
    alignSelf: "stretch",
    marginTop: 12,
  },
  lookupSelectionHeader: {
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    gap: 4,
    paddingBottom: 12,
  },
  lookupSelectionHint: {
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 18,
  },
  lookupSelectionTitle: {
    color: posColors.ink,
    fontSize: 20,
    fontWeight: "900",
  },
  modalAction: {
    alignSelf: "stretch",
  },
  modalCancelAction: {
    marginTop: 12,
  },
  modalActions: {
    flexDirection: "column",
    gap: 8,
    marginTop: 16,
  },
  modalBackdrop: {
    alignItems: "center",
    backgroundColor: "rgba(16, 37, 58, 0.55)",
    flex: 1,
    justifyContent: "center",
    padding: 16,
  },
  modalDismissArea: {
    bottom: 0,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  modalTitle: {
    color: posColors.ink,
    fontSize: 24,
    fontWeight: "900",
  },
  numericEditorBody: {
    alignItems: "stretch",
    flexDirection: "column",
    gap: 16,
    marginTop: 16,
  },
  numericEditorSummary: {
    flexShrink: 0,
    justifyContent: "center",
    minWidth: 0,
  },
  numericKeypadColumn: {
    flexBasis: "auto",
    flexGrow: 0,
    flexShrink: 1,
    maxWidth: "100%",
    minWidth: 0,
    width: "100%",
  },
  numericModal: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    maxHeight: "94%",
    maxWidth: 520,
    padding: 16,
    width: "100%",
  },
  numericValueDisplay: {
    alignItems: "center",
    backgroundColor: "#FAFAF8",
    borderColor: posColors.ink,
    borderRadius: 4,
    borderWidth: 1,
    flexDirection: "row",
    minHeight: 64,
    paddingHorizontal: 16,
  },
  numericValuePlaceholder: {
    color: "#7B8793",
  },
  numericValuePrefix: {
    color: posColors.mutedInk,
    fontSize: 24,
    fontWeight: "800",
    marginRight: 8,
  },
  numericValueText: {
    color: posColors.ink,
    flex: 1,
    fontSize: 30,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    textAlign: "right",
  },
  openItemModalAction: {
    flex: 1,
  },
  openItemModalActions: {
    flexDirection: "row",
  },
  openItemModalScroll: {
    width: "100%",
  },
  openItemModalSurface: {
    overflow: "hidden",
  },
  mutedText: {
    color: posColors.mutedInk,
  },
  offlineCard: {
    backgroundColor: posColors.orangeSoft,
    borderColor: posColors.orange,
    borderRadius: 4,
    borderWidth: 1,
    marginBottom: 10,
    marginTop: 8,
    padding: 14,
  },
  offlineHint: {
    color: "#744024",
    fontSize: 12,
    lineHeight: 17,
    marginTop: 4,
  },
  offlineTitle: {
    color: "#743A20",
    fontSize: 13,
    fontWeight: "900",
  },
  paneTitle: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  pdaState: {
    backgroundColor: posColors.canvas,
    borderColor: posColors.border,
    borderRadius: 4,
    borderWidth: 1,
    flexDirection: "column",
    gap: 8,
    padding: 12,
  },
  pdaStateCopy: {
    gap: 4,
  },
  pdaStateHint: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 17,
  },
  pdaStateTitle: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  productCode: {
    color: posColors.mutedInk,
    fontSize: 11,
    fontVariant: ["tabular-nums"],
    marginTop: 3,
  },
  productIdentity: {
    flex: 1,
  },
  productName: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
    lineHeight: 17,
  },
  productPrice: {
    color: posColors.ink,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  productRow: {
    alignItems: "center",
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 8,
    minHeight: 66,
    paddingVertical: 8,
  },
  quantityText: {
    color: posColors.ink,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  quantityValue: {
    alignItems: "center",
    height: MIN_TOUCH_TARGET,
    justifyContent: "center",
    minWidth: MIN_TOUCH_TARGET,
  },
  quietButtonText: {
    color: posColors.ink,
  },
  warningButtonText: {
    color: posColors.ink,
  },
  runtimeBanner: {
    backgroundColor: posColors.blueSoft,
    borderBottomColor: posColors.blue,
    borderBottomWidth: 1,
    minHeight: 38,
    paddingHorizontal: 20,
    paddingVertical: 9,
  },
  runtimeBannerText: {
    color: posColors.blue,
    fontSize: 12,
    fontWeight: "800",
  },
  updateGateBanner: {
    alignItems: "center",
    backgroundColor: "#FFF4D8",
    borderBottomColor: posColors.orange,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 16,
    justifyContent: "space-between",
    minHeight: 58,
    paddingHorizontal: 20,
    paddingVertical: 8,
  },
  updateGateCopy: {
    flex: 1,
    gap: 2,
  },
  updateGateHint: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 17,
  },
  updateGateTitle: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "900",
  },
  safeArea: {
    backgroundColor: posColors.canvas,
    flex: 1,
  },
  salesStateSurface: {
    flex: 1,
    flexDirection: "column",
    minHeight: 0,
  },
  searchAction: {
    flexBasis: "47%",
    flexGrow: 1,
    paddingHorizontal: 7,
  },
  compactProductEntry: {
    height: COMPACT_PRODUCT_ENTRY_HEIGHT,
    minHeight: COMPACT_PRODUCT_ENTRY_HEIGHT,
  },
  compactSearchActions: {
    flexWrap: "nowrap",
    gap: 4,
    height: 48,
    minHeight: 48,
  },
  compactSearchInputRow: {
    gap: 4,
    height: 48,
    minHeight: 48,
  },
  searchActions: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  searchDrawer: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 6,
    borderWidth: 1,
    height: "88%",
    maxWidth: 520,
    minWidth: 0,
    paddingBottom: 16,
    paddingHorizontal: 18,
    paddingTop: 24,
    width: "100%",
  },
  searchDrawerContent: {
    flex: 1,
    minHeight: 0,
  },
  searchDrawerBackdrop: {
    alignItems: "center",
    backgroundColor: "rgba(16, 37, 58, 0.42)",
    flex: 1,
    justifyContent: "center",
    padding: 16,
  },
  searchDrawerDismissArea: {
    bottom: 0,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  searchDrawerError: {
    backgroundColor: posColors.redSoft,
    color: posColors.red,
    fontSize: 13,
    fontWeight: "700",
    marginVertical: 8,
    padding: 10,
  },
  searchDrawerHeader: {
    alignItems: "center",
    borderBottomColor: posColors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    paddingBottom: 12,
  },
  searchDrawerHeading: {
    flex: 1,
    minWidth: 0,
  },
  searchDrawerList: {
    flex: 1,
    minHeight: 0,
  },
  searchDrawerQuery: {
    color: posColors.mutedInk,
    fontSize: 12,
    marginTop: 4,
  },
  searchInput: {
    backgroundColor: "#FAFAF8",
    borderColor: posColors.ink,
    borderRadius: 4,
    borderWidth: 1,
    color: posColors.ink,
    flex: 1,
    fontSize: 15,
    height: 48,
    minWidth: 0,
    paddingHorizontal: 12,
  },
  searchInputRow: {
    alignItems: "stretch",
    flexDirection: "row",
    gap: 8,
  },
  searchIconAction: {
    height: 48,
    paddingHorizontal: 0,
    width: 48,
  },
  searchMessage: {
    color: posColors.mutedInk,
    fontSize: 13,
    paddingVertical: 16,
    textAlign: "center",
  },
  searchResults: {
    paddingBottom: 12,
  },
  successAction: {
    marginTop: 28,
    minWidth: 240,
  },
  successEyebrow: {
    color: posColors.green,
    fontSize: 12,
    fontWeight: "900",
    letterSpacing: 1.5,
    marginTop: 18,
  },
  successEyebrowCompact: {
    marginTop: 12,
  },
  successMark: {
    alignItems: "center",
    backgroundColor: posColors.green,
    borderRadius: 38,
    height: 76,
    justifyContent: "center",
    width: 76,
  },
  successMarkCompact: {
    borderRadius: 32,
    height: 64,
    width: 64,
  },
  successMarkText: {
    color: "#FFFFFF",
    fontSize: 38,
    fontWeight: "900",
  },
  successMarkTextCompact: {
    fontSize: 32,
  },
  successOrder: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    marginTop: 8,
  },
  successScreen: {
    alignItems: "center",
    backgroundColor: posColors.canvas,
    flex: 1,
    justifyContent: "center",
    padding: 30,
  },
  successScreenCompact: {
    padding: 20,
  },
  successSync: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 16,
    maxWidth: 480,
    textAlign: "center",
  },
  successSyncCompact: {
    marginTop: 12,
  },
  successTitle: {
    color: posColors.ink,
    fontSize: 31,
    fontWeight: "900",
    marginTop: 8,
  },
  successTitleCompact: {
    fontSize: 28,
  },
  successWarning: {
    backgroundColor: "#FFF4D8",
    borderColor: "#DFA321",
    borderRadius: 6,
    borderWidth: 1,
    color: posColors.ink,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 14,
    maxWidth: 560,
    paddingHorizontal: 14,
    paddingVertical: 10,
    textAlign: "center",
  },
  summaryAmount: {
    color: posColors.ink,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  summaryLabel: {
    color: posColors.ink,
    fontSize: 14,
  },
  summaryPane: {
    alignItems: "stretch",
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 5,
    borderWidth: 1,
    flexDirection: "column",
    gap: 8,
    padding: 10,
    shadowColor: posColors.ink,
    shadowOffset: { height: -1, width: 0 },
    shadowOpacity: 0.1,
    shadowRadius: 4,
    elevation: 3,
  },
  compactSummaryAction: {
    alignItems: "center",
    borderRadius: 0,
    borderWidth: 1,
    height: 48,
    justifyContent: "center",
    minHeight: 48,
    minWidth: 48,
    width: 48,
  },
  compactSummaryActionDanger: {
    backgroundColor: posColors.redSoft,
    borderColor: posColors.red,
  },
  compactSummaryActionSecondary: {
    backgroundColor: posColors.surface,
    borderColor: posColors.ink,
  },
  compactSummaryActions: {
    flexShrink: 0,
    gap: 0,
    height: 48,
    width: 96,
  },
  compactSummaryAmount: {
    color: posColors.ink,
    fontSize: 11,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
    lineHeight: 14,
  },
  compactSummaryDiscount: {
    color: posColors.red,
    fontSize: 11,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
    lineHeight: 14,
  },
  compactSummaryLabel: {
    color: posColors.mutedInk,
    fontSize: 9,
    fontWeight: "800",
    lineHeight: 12,
  },
  compactSummaryMetric: {
    flex: 1,
    justifyContent: "center",
    minWidth: 0,
    paddingHorizontal: 4,
  },
  compactSummaryOverview: {
    alignItems: "stretch",
    flex: 1,
    flexDirection: "row",
    height: 48,
    minWidth: 0,
  },
  compactSummaryPane: {
    flexDirection: "row",
    gap: 0,
    height: 48,
    minHeight: 48,
    overflow: "hidden",
    padding: 0,
  },
  compactSummaryTotal: {
    borderLeftColor: posColors.border,
    borderLeftWidth: 1,
    flex: 1.2,
    justifyContent: "center",
    minWidth: 0,
    paddingHorizontal: 4,
  },
  compactSummaryTotalAmount: {
    color: posColors.ink,
    fontSize: 16,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -0.5,
    lineHeight: 19,
  },
  summaryEditAction: {
    flex: 1,
    paddingHorizontal: 8,
  },
  summaryEditActions: {
    flexDirection: "row",
    gap: 8,
    minWidth: 0,
  },
  summaryMetrics: {
    flex: 1,
    justifyContent: "center",
    minWidth: 0,
  },
  summaryOverview: {
    alignItems: "stretch",
    flexDirection: "row",
    gap: 10,
  },
  summaryRow: {
    alignItems: "center",
    flexDirection: "row",
    justifyContent: "space-between",
    minHeight: 30,
  },
  summaryRows: {
    marginTop: 15,
  },
  summarySpacer: {
    flex: 1,
  },
  summaryTitle: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "900",
    marginBottom: 4,
  },
  summaryTotal: {
    alignItems: "flex-end",
    borderLeftColor: posColors.border,
    borderLeftWidth: 1,
    flexBasis: 116,
    justifyContent: "center",
    minWidth: 104,
    paddingLeft: 10,
  },
  totalAmount: {
    color: posColors.ink,
    fontSize: 28,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -0.8,
    textAlign: "right",
  },
  totalLabel: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "900",
    textTransform: "uppercase",
  },
  totalRow: {
    alignItems: "flex-end",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  utilityResult: {
    backgroundColor: posColors.greenSoft,
    borderColor: posColors.green,
    borderRadius: 4,
    borderWidth: 1,
    color: posColors.green,
    fontSize: 12,
    fontWeight: "700",
    lineHeight: 17,
    padding: 10,
  },
  utilityResultWarning: {
    backgroundColor: "#FFF4D8",
    borderColor: posColors.orange,
    color: "#744024",
  },
  utilityResultSurface: {
    marginHorizontal: 10,
  },
  screenContent: {
    paddingHorizontal: 0,
    paddingVertical: 0,
  },
  verifyingMessage: {
    color: posColors.mutedInk,
    fontSize: 12,
    paddingVertical: 4,
    textAlign: "center",
  },
  totalRule: {
    backgroundColor: posColors.ink,
    height: 2,
    marginBottom: 14,
    marginTop: 10,
  },
  workspace: {
    color: posColors.mutedInk,
    fontSize: 11,
    fontWeight: "700",
    marginTop: 1,
  },
  workspaceHeader: {
    gap: 10,
  },
  compactWorkspaceHeader: {
    gap: 0,
  },
  workspaceList: {
    flexGrow: 1,
    padding: 10,
  },
  compactWorkspaceList: {
    padding: 0,
  },
  workspaceListViewport: {
    flex: 1,
  },
});
