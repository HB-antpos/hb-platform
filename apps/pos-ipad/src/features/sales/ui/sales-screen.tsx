import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  memo,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import {
  ActivityIndicator,
  Animated,
  FlatList,
  Image,
  Modal,
  PanResponder,
  StyleSheet,
  Text,
  View,
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
  type SalesProductSearchItem,
} from "./sales-presenter";
import {
  applySalesSearchKey,
  SalesSearchKeyboard,
  type SalesSearchKey,
} from "./sales-search-keyboard";
import { useDelayedVisibility } from "./use-delayed-visibility";

import type { CartLine, CartSnapshot } from "@/core/contracts";
import type { NewTransactionGate } from "@/core/contracts/app-updates";
import type { CameraScanMode } from "@/core/contracts/scanner";
import type { AttendanceQrDisplaySource } from "@/features/attendance-audit/attendance-audit-runtime";
import {
  AttendanceQrEmptyCartCard,
  AttendanceQrModal,
  AttendanceQrRailTile,
  useAttendanceQrState,
} from "@/features/attendance-audit/attendance-qr-sales-panels";
import { MAX_MANUAL_UNIT_PRICE_DISPLAY } from "@/features/sales/domain";
import {
  CameraScannerInline,
  CameraScannerModal,
  type CameraScannerPort,
} from "@/features/scanner-camera";
import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPanResponderView } from "@/ui/controls/pos-pan-responder-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosSwitch } from "@/ui/controls/pos-switch";
import {
  type PosNavEntry,
  type PosNavModel,
} from "@/ui/shell/pos-app-rail";
import {
  DEFAULT_PINNED_DESTINATIONS,
  type PosDestinationId,
} from "@/ui/shell/pos-navigation";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import { usePosShellStore } from "@/ui/shell/pos-shell-store";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

const CATALOG_VERIFYING_SHOW_DELAY_MS = 300;
const CATALOG_VERIFYING_MIN_VISIBLE_MS = 500;
const QUICK_DISCOUNTS = [0, 1_000, 2_000, 3_000, 4_000, 5_000] as const;
const QUICK_ORDER_DISCOUNTS = [1_000, 2_000, 3_000, 4_000, 5_000] as const;
const AVERAGE_CART_LINE_HEIGHT = 128;
const CART_LINE_REMOVE_ACTION_WIDTH = 96;
const CART_LINE_SWIPE_ACTIVATION_DISTANCE = 12;

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

type QuickDiscountScope = "line" | "order";

type QuickDiscountApplied = Readonly<{
  scope: QuickDiscountScope;
  lineId: string | null;
  basisPoints: number;
  revision: number;
}>;

/** 选中行在某个编辑项下的初始值：数量 / 单价 / 折扣金额取当前值，折扣百分比留空待输入。 */
function lineEditDraft(line: CartLine, mode: LineEditMode): LineEditState {
  return {
    lineId: line.lineId,
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
}

export type SalesUtilityActionResult = Readonly<{
  kind:
    "completed" | "not-found" | "denied" | "unavailable" | "failed" | "unknown";
}>;

type SalesUtilityAction = "reprint" | "drawer";

export type SalesCameraScannerControl = Readonly<{
  active: boolean;
  mode: CameraScanMode;
  scanner: CameraScannerPort;
  onModeChange(mode: CameraScanMode): void;
  onOpen(): void;
  onClose(): void;
  onScan(value: string): boolean | Promise<boolean>;
}>;

export type SalesCartProductDetails = Readonly<{
  barcode: string | null;
  imageUri: string | null;
}>;

export type SalesScreenProps = Readonly<{
  presenter: SalesPresenter;
  /**
   * 员工打卡二维码（路由在收银页聚焦时创建、失焦时销毁）。不传或为 null 时不显示：
   * 空购物车显示原来的「扫描商品」提示，左侧栏也没有「考勤」格子。
   */
  attendanceQr?: AttendanceQrDisplaySource | null;
  cameraScanner?: SalesCameraScannerControl;
  locale?: SalesLocale;
  newTransactionGate?: NewTransactionGate;
  resolveCartProductDetails?: (input: {
    productCode: string;
    lookupCode: string;
  }) => Promise<SalesCartProductDetails | null>;
  resolveCartProductImage?: (input: {
    productCode: string;
    lookupCode: string;
  }) => Promise<string | null>;
  onOpenAttendanceAudit?: () => void;
  onOpenCashDrawer?: () => Promise<SalesUtilityActionResult>;
  /**
   * 打开「钱箱」弹窗（现金存入 / 取出 / 今日记录）。路由仅在本机开关 cashMovement.enabled 开启时才传，
   * 不传则不显示「钱箱」按钮，原有「开箱」按钮不变。
   */
  onOpenCashMovement?: () => void;
  onOpenCatalogMaintenance?: () => void;
  onOpenDailyClose?: () => void;
  onOpenHeldOrders?: () => void;
  onOpenInstallments?: () => void;
  onOpenLocalHistory?: () => void;
  onOpenPaymentRecovery?: () => void;
  onOpenPayment?: (cart: CartSnapshot) => void;
  onOpenRequiredUpdate?: () => void;
  onReprintReceipt?: () => Promise<SalesUtilityActionResult>;
  onOpenRemoteHistory?: () => void;
  onOpenReturns?: () => void;
  onOpenSettings?: () => void;
  onOpenSpecialProducts?: () => void;
  onOpenSyncHistory?: () => void;
  onSwitchLanguage?: () => void;
  /** 固定到左侧栏的入口发生变化（在「全部功能」面板里点了图钉）。 */
  onPinnedDestinationsChange?: (pinned: PosDestinationId[]) => void;
  onManualInputFocusChange?: (focused: boolean) => void;
  /** 原生版本号与 build 号（路由层读取），显示在头部，便于门店报告问题时核对安装的包。 */
  appVersionLabel?: string;
  showStatusStrip?: boolean;
  /** 固定到左侧栏的入口；不传用默认的五个常用入口。 */
  pinnedDestinations?: readonly PosDestinationId[];
}>;

type ActionButtonProps = Readonly<{
  label: string;
  onPress(): void;
  disabled?: boolean;
  tone?: "primary" | "secondary" | "danger" | "quiet" | "warning" | "info";
  sound?: "tap" | "key" | "navigate" | "danger";
  testID?: string;
  style?: StyleProp<ViewStyle>;
  accessibilityLabel?: string;
  icon?: PosIconName;
  /** 对应 posTouch：primary 88 / large 72 / button 64 / medium 56（默认）/ small 48。 */
  size?: "primary" | "large" | "button" | "medium" | "small";
}>;

type NumericValueDisplayProps = Readonly<{
  accessibilityLabel: string;
  currencyPrefix?: string;
  placeholder: string;
  testID: string;
  value: string;
}>;

export function SalesScreen({
  appVersionLabel,
  attendanceQr = null,
  presenter,
  cameraScanner,
  locale: localeOverride,
  newTransactionGate,
  resolveCartProductDetails,
  resolveCartProductImage,
  onOpenAttendanceAudit,
  onOpenCashDrawer,
  onOpenCashMovement,
  onOpenCatalogMaintenance,
  onOpenDailyClose,
  onOpenHeldOrders,
  onOpenInstallments,
  onOpenLocalHistory,
  onOpenPaymentRecovery,
  onOpenPayment,
  onOpenRequiredUpdate,
  onReprintReceipt,
  onOpenRemoteHistory,
  onOpenReturns,
  onOpenSettings,
  onOpenSpecialProducts,
  onOpenSyncHistory,
  onSwitchLanguage,
  onPinnedDestinationsChange,
  onManualInputFocusChange,
  showStatusStrip = true,
  pinnedDestinations = DEFAULT_PINNED_DESTINATIONS,
}: SalesScreenProps) {
  usePosThemeVersion();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const connectivity = usePosShellStore((current) => current.connectivity);
  const locale = localeOverride ?? resolveSalesLocale();
  const t = useCallback(
    (
      key: SalesCopyKey,
      values?: Readonly<Record<string, string | number>>,
    ) => salesText(locale, key, values),
    [locale],
  );
  const keypadLabels = {
    backspace: t("keypad.backspace"),
    clear: t("keypad.clear"),
    decimal: t("keypad.decimal"),
    quick50: t("keypad.quick50"),
    quick99: t("keypad.quick99"),
  };
  const [openItemVisible, setOpenItemVisible] = useState(false);
  const [openItemPrice, setOpenItemPrice] = useState("");
  const [lineEdit, setLineEdit] = useState<LineEditState | null>(null);
  const [orderDiscountVisible, setOrderDiscountVisible] = useState(false);
  const [orderEdit, setOrderEdit] = useState<OrderEditState | null>(null);
  const [clearCartVisible, setClearCartVisible] = useState(false);
  // 快捷折扣默认作用于选中行；打开「整单折扣」开关后作用于整单。
  const [quickDiscountScope, setQuickDiscountScope] =
    useState<QuickDiscountScope>("line");
  // 购物车快照不记录「打的是哪一档」（整单折扣会摊到各行），
  // 只高亮刚刚成功打的那一档；购物车之后再有任何变动就不再高亮，避免显示过期状态。
  const [quickDiscountApplied, setQuickDiscountApplied] =
    useState<QuickDiscountApplied | null>(null);
  const attendanceQrState = useAttendanceQrState(attendanceQr);
  const [attendanceQrModalVisible, setAttendanceQrModalVisible] =
    useState(false);
  useEffect(() => {
    // 离开收银页（考勤码随之销毁）或锁屏后弹窗自动关闭，回来时不会自己弹出。
    if (!attendanceQr) setAttendanceQrModalVisible(false);
  }, [attendanceQr]);
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
  const [searchKeyboardVisible, setSearchKeyboardVisible] = useState(false);
  const searchInputRef = useRef<NativeTextInput>(null);
  const cartListRef = useRef<FlatList<CartLine>>(null);
  const cartLinesRef = useRef(state.cart.lines);
  cartLinesRef.current = state.cart.lines;
  const cartRevealRetryTimerRef = useRef<ReturnType<
    typeof setTimeout
  > | null>(null);
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
  const cameraCloseRequestedRef = useRef(false);
  manualInputFocusChangeRef.current = onManualInputFocusChange;
  const successDrawerWarning = state.success
    ? drawerWarningCopyKey(state.success.drawerDisposition)
    : null;
  const cartEmpty = state.cart.lines.length === 0;
  const searchResultsAreCurrent =
    state.searchStatus === "ready" &&
    searchResultsQuery !== null &&
    searchResultsQuery === searchDrawerQuery &&
    state.query.trim() === searchDrawerQuery;
  const visibleSearchResults = searchResultsAreCurrent
    ? state.searchResults
    : [];
  // 一码多商品待选只在收银态展示；结账准备、锁屏或成功页一律不弹出。
  const lookupSelection =
    state.phase === "selling" ? state.lookupSelection : null;
  // 收起后弹窗隐藏、待选仍在队列里，由常驻提示条重新展开。
  const lookupSelectionModalVisible =
    lookupSelection !== null && !state.lookupSelectionCollapsed;
  const newTransactionBlocked =
    cartEmpty && newTransactionGate?.canStartNewTransaction === false;
  const checkoutVerifying = state.phase === "verifying-checkout";
  // 在线时每扫一枪都会后台核验（本地命中也要校准单价），通常几百毫秒就结束：
  // 超过 300ms 才显示、显示后至少停 500ms，避免每枪都闪；点结账等待时立即显示。
  const catalogVerifyingVisible = useDelayedVisibility(
    state.pendingLookupCount > 0 || checkoutVerifying,
    {
      immediate: checkoutVerifying,
      minVisibleMs: CATALOG_VERIFYING_MIN_VISIBLE_MS,
      showDelayMs: CATALOG_VERIFYING_SHOW_DELAY_MS,
    },
  );
  const cameraScannerActive = cameraScanner?.active === true;
  const closeCameraScanner = cameraScanner?.onClose;
  const transactionActionsDisabled =
    state.phase !== "selling" || cameraScannerActive;
  const mergeCartDisabled =
    transactionActionsDisabled || !presenter.canMergeCompatibleLines();
  const catalogActionsDisabled =
    !state.capabilities.catalog ||
    newTransactionBlocked ||
    transactionActionsDisabled;
  // 连续相机扫码期间也要能选择候选，只受目录能力、新交易门禁与在途点选约束。
  const lookupCandidatesDisabled =
    !state.capabilities.catalog ||
    newTransactionBlocked ||
    state.lookupSelectionPending;
  const manualInputTreeUnavailable =
    state.phase === "success" ||
    state.phase === "locked" ||
    checkoutVerifying ||
    newTransactionBlocked;

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
          viewPosition: 1,
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
    const index = state.cart.lines.findIndex((line) => line.lineId === lineId);
    if (index < 0) {
      cartRevealIndexRef.current = null;
      return;
    }
    cartRevealIndexRef.current = index;
    cartListRef.current?.scrollToIndex({
      animated: true,
      index,
      viewPosition: 1,
    });
  }, [state.cart.lines, state.revealLineId, state.revealRevision]);

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

  const scheduleSearchKeyboardEnable = (
    requestGeneration: number,
  ): void => {
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
    clearSearchKeyboardTimer();
    const requestGeneration =
      searchKeyboardRequestGenerationRef.current + 1;
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

  // 连着蓝牙扫码枪时 iPadOS 处于外接键盘模式，聚焦输入框只给工具条、不会自动弹出系统软键盘；
  // 键盘图标因此改为开关 App 内搜索键盘，系统键盘（如需中文输入）仍可从面板的「系统键盘」键唤起。
  const toggleSearchKeyboard = (): void => {
    setSearchKeyboardVisible((visible) => !visible);
  };

  const handleSearchKeyboardKey = (key: SalesSearchKey): void => {
    switch (key.kind) {
      case "search":
        setSearchKeyboardVisible(false);
        runCombinedProductSearch();
        return;
      case "close":
        setSearchKeyboardVisible(false);
        return;
      case "system":
        setSearchKeyboardVisible(false);
        requestSearchKeyboard();
        return;
      default:
        // 读 presenter 最新状态再拼接，连续快速点按时不会基于过期的渲染值。
        presenter.setQuery(
          applySalesSearchKey(presenter.getState().query, key),
        );
    }
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

  const resetSearchInputToHidMode = (): void => {
    clearSearchKeyboardTimer();
    searchKeyboardRequestGenerationRef.current += 1;
    searchKeyboardRequestPhaseRef.current = "idle";
    searchInputRef.current?.setNativeProps({ showSoftInputOnFocus: false });
    setSearchSoftInputOnFocus(false);
  };

  const openCameraScanner = (): void => {
    setSearchKeyboardVisible(false);
    resetSearchInputToHidMode();
    searchInputRef.current?.blur();
    // TextInput 被连续扫码区域替换时不保证触发 onBlur，必须主动释放 HID 暂停态。
    notifyManualInputBlurred();
    cameraScanner?.onOpen();
  };

  const beginNumericInput = (): void => {
    setSearchKeyboardVisible(false);
    numericInputModalActiveRef.current = true;
    resetSearchInputToHidMode();
    searchInputRef.current?.blur();
    notifyManualInputFocused();
  };
  beginNumericInputRef.current = beginNumericInput;

  const handleSearchInputBlur = (): void => {
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

  // 单价上限提示只属于弹窗里这一次输入：关闭弹窗或切换编辑项时清掉，下次打开不再残留旧提示。
  const dismissPriceTooHighError = (): void => {
    if (presenter.getState().errorCode === "price-too-high") {
      presenter.dismissError();
    }
  };

  const closeOpenItemInput = (): void => {
    numericInputModalActiveRef.current = false;
    notifyManualInputBlurred();
    dismissPriceTooHighError();
    setOpenItemVisible(false);
  };

  // 常驻编辑区没有「关闭」：取消 = 放弃这次输入，回到选中行当前的值。
  const resetLineEditDraft = (): void => {
    dismissPriceTooHighError();
    setLineEdit((current) => {
      if (!current) return null;
      const line = presenter
        .getState()
        .cart.lines.find((candidate) => candidate.lineId === current.lineId);
      return line ? lineEditDraft(line, current.mode) : null;
    });
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
    setSearchKeyboardVisible(false);
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
    if (!cameraScannerActive || state.phase === "selling") {
      cameraCloseRequestedRef.current = false;
      return;
    }
    if (cameraCloseRequestedRef.current) return;
    cameraCloseRequestedRef.current = true;
    closeCameraScanner?.();
  }, [cameraScannerActive, closeCameraScanner, state.phase]);

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

  const selectLineEditMode = (mode: LineEditMode): void => {
    dismissPriceTooHighError();
    setLineEdit((current) => {
      if (!current) return null;
      const line = state.cart.lines.find(
        (candidate) => candidate.lineId === current.lineId,
      );
      return line ? lineEditDraft(line, mode) : null;
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
    const { lineId, mode } = lineEdit;
    void submission.then((applied) => {
      if (!applied) return;
      dismissPriceTooHighError();
      const line = presenter
        .getState()
        .cart.lines.find((candidate) => candidate.lineId === lineId);
      setLineEdit(line ? lineEditDraft(line, mode) : null);
    });
  };

  const applyQuickDiscount = (basisPoints: number): void => {
    const scope = quickDiscountScope;
    const lineId = scope === "line" ? state.selectedLineId : null;
    if (scope === "line" && !lineId) return;
    // 整单「无」= 把整单折扣设回 0%（走整单手动折扣权限）；单行「无」由 presenter 按 0 处理。
    const submission =
      scope === "order"
        ? basisPoints === 0
          ? presenter.applyOrderManualDiscountBasisPoints(0)
          : presenter.applyOrderQuickDiscount(basisPoints)
        : presenter.applyLineDiscount(lineId!, basisPoints);
    void submission.then((applied) => {
      if (!applied) return;
      setQuickDiscountApplied(
        basisPoints === 0
          ? null
          : {
              scope,
              lineId,
              basisPoints,
              revision: presenter.getState().cart.revision,
            },
      );
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
    (lineId: string) => presenter.selectLine(lineId),
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
      <CartLineRow
        disabled={
          !state.capabilities.cartEditing || transactionActionsDisabled
        }
        index={index}
        isSelected={state.selectedLineId === item.lineId}
        item={item}
        locale={locale}
        onDecrease={handleDecreaseCartLine}
        onIncrease={handleIncreaseCartLine}
        onRemove={handleRemoveCartLine}
        onSelect={handleSelectCartLine}
        {...(resolveCartProductDetails
          ? { resolveProductDetails: resolveCartProductDetails }
          : {})}
        {...(resolveCartProductImage
          ? { resolveProductImage: resolveCartProductImage }
          : {})}
        t={t}
      />
    ),
    [
      locale,
      handleDecreaseCartLine,
      handleIncreaseCartLine,
      handleRemoveCartLine,
      handleSelectCartLine,
      resolveCartProductDetails,
      resolveCartProductImage,
      state.capabilities.cartEditing,
      state.selectedLineId,
      t,
      transactionActionsDisabled,
    ],
  );

  const selectedLine =
    state.cart.lines.find((line) => line.lineId === state.selectedLineId) ??
    null;
  const selectedLineSignature = selectedLine
    ? `${selectedLine.lineId}|${selectedLine.quantity}|${selectedLine.unitPrice.cents}|${selectedLine.discount.cents}`
    : null;
  useEffect(() => {
    // 选中行换了，或它的数量 / 单价 / 折扣被别处改了（加减按钮、扫码叠加、快捷折扣）：
    // 收银员还没在键盘上输入时把编辑值刷新成当前值；已经在输入的不打断。
    const line =
      cartLinesRef.current.find(
        (candidate) => candidate.lineId === state.selectedLineId,
      ) ?? null;
    setLineEdit((current) => {
      if (!line) return null;
      if (!current || current.lineId !== line.lineId) {
        return lineEditDraft(line, current?.mode ?? "quantity");
      }
      return current.replaceOnNextDigit
        ? lineEditDraft(line, current.mode)
        : current;
    });
    // 只按签名触发：签名已包含选中行 id 与三项可编辑值。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedLineSignature]);
  useEffect(() => {
    // 购物车清空（结账、挂单、清空）后回到默认的单行模式。
    if (cartEmpty) {
      setQuickDiscountScope("line");
      setQuickDiscountApplied(null);
    }
  }, [cartEmpty]);

  if (state.phase === "success" && state.success) {
    return (
      <PosScreenFrame
        appVersionLabel={appVersionLabel ?? null}
        language={locale}
        {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
        rail="none"
        testID="sales-screen"
        topBar={showStatusStrip ? undefined : false}
      >
        <View style={styles.successScreen} testID="sales-success">
          <View style={styles.successBanner}>
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.green}
              name="check-circle"
              size={76}
            />
            <View style={styles.successCopy}>
              <Text style={styles.successEyebrow}>{t("success.eyebrow")}</Text>
              <Text style={styles.successTitle}>
                {t(
                  state.success.cashDueCents === 0
                    ? "success.titleZero"
                    : "success.title",
                )}
              </Text>
              <Text numberOfLines={1} style={styles.successOrder}>
                {t("success.order", { orderGuid: state.success.orderGuid })}
              </Text>
            </View>
          </View>
          {/* 0 元订单没有收款也没有找零，不显示「应找金额」。 */}
          {state.success.cashDueCents === 0 ? null : (
            <View style={styles.changeCard}>
              <Text style={styles.changeLabel}>{t("success.change")}</Text>
              <Text
                adjustsFontSizeToFit
                numberOfLines={1}
                style={styles.changeAmount}
              >
                {formatAud(state.success.changeCents, locale)}
              </Text>
            </View>
          )}
          <Text style={styles.successSync}>
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
          <ActionButton
            disabled={!state.success.clearCartSignalled}
            icon="arrow-right-circle"
            label={t("success.newSale")}
            onPress={() => presenter.startNewSale()}
            size="primary"
            style={styles.successAction}
            testID="sales-new-sale"
          />
        </View>
      </PosScreenFrame>
    );
  }

  if (state.phase === "locked") {
    return (
      <PosScreenFrame
        appVersionLabel={appVersionLabel ?? null}
        language={locale}
        {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
        rail="none"
        testID="sales-screen"
        topBar={showStatusStrip ? undefined : false}
      >
        <View style={styles.lockedScreen} testID="sales-locked">
          <View style={styles.lockIcon}>
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.onInverse}
              name="lock-outline"
              size={68}
            />
          </View>
          <Text style={styles.lockedTitle}>{t("locked.title")}</Text>
          <Text style={styles.lockedHint}>{t("locked.hint")}</Text>
          <View style={styles.lockedNotice}>
            <Text style={styles.lockedNoticeText}>
              {t("locked.unlockPending")}
            </Text>
          </View>
        </View>
      </PosScreenFrame>
    );
  }

  const runtimeUnavailable =
    !state.capabilities.catalog ||
    !state.capabilities.cartEditing ||
    !state.capabilities.cashCheckout;
  const cashDraft = deriveCashDraft(state.cart, state.cashTenderedText);
  // 合计恰好 0 元的整单：结账按钮与现金弹窗都改成零元结账的样子（无需收款、没有键盘）。
  const zeroTotalSale =
    state.cart.lines.length > 0 && state.cart.actualAmount.cents === 0;
  // 左侧栏与「全部功能」面板的入口：路由层没给回调（没权限）的入口不出现。
  const navEntries: PosNavEntry[] = [];
  const addNavEntry = (
    id: PosDestinationId,
    labelKey: SalesCopyKey,
    descriptionKey: SalesCopyKey,
    onPress: (() => void) | undefined,
    testID: string,
  ): void => {
    if (!onPress) return;
    navEntries.push({
      description: t(descriptionKey),
      id,
      label: t(labelKey),
      onPress,
      testID,
    });
  };
  addNavEntry(
    "held-orders",
    "header.heldOrders",
    "navDesc.heldOrders",
    onOpenHeldOrders
      ? () => {
          // 取单会替换当前购物车：有一码多商品待选时先拦下并展开待选。
          if (presenter.requireNoPendingLookupSelections()) onOpenHeldOrders();
        }
      : undefined,
    "sales-open-held-orders",
  );
  addNavEntry(
    "returns",
    "header.returns",
    "navDesc.returns",
    onOpenReturns,
    "sales-open-returns",
  );
  addNavEntry(
    "installments",
    "header.installments",
    "navDesc.installments",
    onOpenInstallments,
    "sales-open-installments",
  );
  addNavEntry(
    "payment-recovery",
    "functions.paymentRecovery",
    "navDesc.paymentRecovery",
    onOpenPaymentRecovery,
    "sales-open-payment-recovery",
  );
  addNavEntry(
    "local-history",
    "functions.localHistory",
    "navDesc.localHistory",
    onOpenLocalHistory,
    "sales-open-local-history",
  );
  addNavEntry(
    "remote-history",
    "header.remoteHistory",
    "navDesc.remoteHistory",
    onOpenRemoteHistory,
    "sales-open-remote-history",
  );
  addNavEntry(
    "sync-history",
    "header.syncHistory",
    "navDesc.syncHistory",
    onOpenSyncHistory,
    "sales-open-sync-history",
  );
  addNavEntry(
    "daily-close",
    "header.dailyClose",
    "navDesc.dailyClose",
    onOpenDailyClose,
    "sales-open-daily-close",
  );
  addNavEntry(
    "attendance-audit",
    "header.attendanceAudit",
    "navDesc.attendanceAudit",
    onOpenAttendanceAudit,
    "sales-open-attendance-audit",
  );
  addNavEntry(
    "catalog-maintenance",
    "header.catalogMaintenance",
    "navDesc.catalogMaintenance",
    onOpenCatalogMaintenance,
    "sales-open-catalog-maintenance",
  );
  addNavEntry(
    "settings",
    "header.settings",
    "navDesc.settings",
    onOpenSettings,
    "sales-open-settings",
  );
  const navModel: PosNavModel = {
    entries: navEntries,
    lockDisabled: !state.capabilities.lock,
    lockTestID: "sales-lock",
    onLock: () => {
      void presenter.lockTerminal();
    },
    pinned: pinnedDestinations,
    ...(onPinnedDestinationsChange
      ? { onPinnedChange: onPinnedDestinationsChange }
      : {}),
    ...(onSwitchLanguage ? { onSwitchLanguage } : {}),
    // 「考勤」格子固定在「更多 / 锁屏」上方；缩略码只是入口，点开放大再扫。
    ...(attendanceQrState
      ? {
          accessory: (
            <AttendanceQrRailTile
              active={attendanceQrModalVisible}
              language={locale}
              onPress={() => setAttendanceQrModalVisible(true)}
              state={attendanceQrState}
            />
          ),
        }
      : {}),
  };
  const cartEditingDisabled =
    cartEmpty || !state.capabilities.cartEditing || transactionActionsDisabled;
  const selectedLineNumber = selectedLine
    ? state.cart.lines.indexOf(selectedLine) + 1
    : 0;
  const lineEditorDisabled =
    !selectedLine ||
    lineEdit === null ||
    !state.capabilities.cartEditing ||
    transactionActionsDisabled;
  const quickDiscountDisabled =
    cartEditingDisabled || (quickDiscountScope === "line" && !selectedLine);
  const quickDiscountBaseCents =
    quickDiscountScope === "order"
      ? state.cart.subtotal.cents
      : selectedLine
        ? selectedLine.unitPrice.cents * (Number(selectedLine.quantity) || 0)
        : 0;
  const activeQuickDiscount =
    quickDiscountApplied &&
    quickDiscountApplied.revision === state.cart.revision &&
    quickDiscountApplied.scope === quickDiscountScope &&
    (quickDiscountScope === "order" ||
      quickDiscountApplied.lineId === state.selectedLineId)
      ? quickDiscountApplied.basisPoints
      : null;

  return (
    <PosScreenFrame
      active="sales"
      appVersionLabel={appVersionLabel ?? null}
      language={locale}
      {...(onSwitchLanguage ? { onSwitchLanguage } : {})}
      rail={navModel}
      testID="sales-screen"
      topBar={showStatusStrip ? undefined : false}
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

      {lookupSelection && !lookupSelectionModalVisible ? (
        <PosPressable
          accessibilityHint={t("lookupSelection.pendingHint")}
          accessibilityLabel={t("lookupSelection.pendingBar", {
            count: state.lookupSelectionQueueLength,
          })}
          accessibilityLiveRegion="polite"
          accessibilityRole="button"
          onPress={() => presenter.expandLookupSelection()}
          sound="navigate"
          style={styles.lookupPendingBar}
          testID="sales-lookup-selection-pending-bar"
        >
          <Text numberOfLines={1} style={styles.lookupPendingBarTitle}>
            {t("lookupSelection.pendingBar", {
              count: state.lookupSelectionQueueLength,
            })}
          </Text>
          <Text style={styles.lookupPendingBarAction}>
            {t("lookupSelection.pendingAction")}
          </Text>
        </PosPressable>
      ) : null}

      <View style={styles.workspaceRow}>
        <View style={styles.cartColumn}>
        <View style={styles.cartPane} testID="sales-transaction-pane">
          <View style={styles.cartHeader}>
            <Text style={styles.paneTitle}>{t("cart.title")}</Text>
            <Text style={styles.cartCount}>
              {t(state.cart.lines.length === 1 ? "cart.itemsOne" : "cart.items", { count: state.cart.lines.length })}
            </Text>
            <View style={styles.cartHeaderActions}>
              <ActionButton
                disabled={mergeCartDisabled}
                icon="call-merge"
                label={t("functions.mergeCart")}
                onPress={() => {
                  void presenter.mergeCompatibleLines();
                }}
                size="small"
                testID="sales-merge-cart"
                tone="quiet"
              />
              <ActionButton
                disabled={cartEditingDisabled}
                icon="trash-can-outline"
                label={t("summary.clearCart")}
                onPress={() => setClearCartVisible(true)}
                size="small"
                testID="sales-clear-cart"
                tone="danger"
              />
            </View>
          </View>
          {cartEmpty && attendanceQrState ? (
            // 空购物车直接显示大码：上班、下班、交接时员工不用收银员点任何按钮就能扫。
            <View style={styles.emptyCartWithQr} testID="sales-cart-empty">
              <View style={styles.emptyCartCompactHint}>
                <MaterialCommunityIcons
                  accessible={false}
                  color={posColors.mutedInk}
                  name="barcode-scan"
                  size={22}
                />
                <Text style={styles.emptyCartHint}>{t("cart.emptyHint")}</Text>
              </View>
              <AttendanceQrEmptyCartCard
                language={locale}
                state={attendanceQrState}
              />
            </View>
          ) : cartEmpty ? (
            <View style={styles.emptyCart} testID="sales-cart-empty">
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.mutedInk}
                name="barcode-scan"
                size={56}
              />
              <Text style={styles.emptyCartTitle}>{t("cart.emptyTitle")}</Text>
              <Text style={styles.emptyCartHint}>{t("cart.emptyHint")}</Text>
            </View>
          ) : (
            <FlatList
              contentContainerStyle={styles.cartList}
              data={state.cart.lines}
              initialNumToRender={10}
              keyExtractor={cartLineKeyExtractor}
              maxToRenderPerBatch={8}
              onScrollToIndexFailed={retryCartReveal}
              ref={cartListRef}
              removeClippedSubviews
              renderItem={renderCartLine}
              style={styles.cartListViewport}
              testID="sales-cart-list"
              updateCellsBatchingPeriod={32}
              windowSize={7}
            />
          )}
        </View>
          {connectivity === "offline" ? (
            <View
              accessibilityRole="alert"
              style={styles.offlineCard}
              testID="sales-offline-cash-only"
            >
              <Text style={styles.offlineTitle}>{t("summary.cashOnly")}</Text>
              <Text style={styles.offlineHint}>
                {t("summary.cashOnlyHint")}
              </Text>
            </View>
          ) : null}
          {/* 结算条在购物车下方：眼睛看完商品和金额，手直接落在右边的收款按钮上。 */}
          <View style={styles.checkoutBar} testID="sales-checkout-bar">
            <View style={styles.checkoutSummary} testID="sales-summary-pane">
              <View style={styles.checkoutCell}>
                <Text style={styles.checkoutCellLabel}>
                  {t("summary.subtotal")}
                </Text>
                <Text style={styles.checkoutCellAmount}>
                  {formatAud(state.cart.subtotal.cents, locale)}
                </Text>
              </View>
              <View style={styles.checkoutCell}>
                <Text style={styles.checkoutCellLabel}>
                  {t("summary.discount")}
                </Text>
                <Text
                  style={[styles.checkoutCellAmount, styles.discountAmountText]}
                  testID="sales-summary-discount-amount"
                >
                  {`−${formatAud(state.cart.discount.cents, locale)}`}
                </Text>
              </View>
              <View style={styles.checkoutTotal}>
                <Text style={styles.checkoutCellLabel}>{t("summary.total")}</Text>
                <Text
                  adjustsFontSizeToFit
                  minimumFontScale={0.6}
                  numberOfLines={1}
                  style={styles.checkoutTotalAmount}
                >
                  {formatAud(state.cart.actualAmount.cents, locale)}
                </Text>
              </View>
            </View>
            <ActionButton
              disabled={
                cartEmpty ||
                !onOpenPayment ||
                !state.capabilities.cartEditing ||
                transactionActionsDisabled
              }
              icon="arrow-right-circle"
              label={
                cartEmpty
                  ? t("summary.checkoutDisabled")
                  : `${t(
                      zeroTotalSale
                        ? "summary.completeZeroTotal"
                        : "summary.goToPayment",
                    )} · ${formatAud(state.cart.actualAmount.cents, locale)}`
              }
              onPress={() => {
                if (!onOpenPayment) return;
                // 合计恰好 0 元时 presenter 自己打开零元结账确认弹窗，不进付款页（付款页不接收 0 元订单）。
                void presenter.startCheckout().then((route) => {
                  if (route?.kind === "payment") onOpenPayment(route.cart);
                });
              }}
              size="primary"
              sound="navigate"
              style={styles.checkoutButton}
              testID="sales-open-payment"
            />
          </View>
        </View>

        <View style={styles.functionPane} testID="sales-function-pane">
          <PosKeyboardAwareScrollView
            contentContainerStyle={styles.functionScrollContent}
            showsVerticalScrollIndicator={false}
            style={styles.functionScroll}
            testID="sales-function-scroll"
          >
            {cameraScanner?.active && cameraScanner.mode === "continuous" ? (
              <CameraScannerInline
                context="product"
                onClose={cameraScanner.onClose}
                onScan={cameraScanner.onScan}
                scanner={cameraScanner.scanner}
                visible
              />
            ) : (
              <>
            <View style={styles.searchInputRow} testID="sales-search-input-row">
              <PosKeyboardAwareTextInput
                ref={searchInputRef}
                accessibilityLabel={t("catalog.searchPlaceholder")}
                autoCapitalize="none"
                autoCorrect={false}
                editable={!catalogActionsDisabled}
                onBlur={handleSearchInputBlur}
                onChangeText={(value) => presenter.setQuery(value)}
                onFocus={handleSearchInputFocus}
                onSubmitEditing={() => {
                  resetSearchInputToHidMode();
                  void presenter.addLookupCode();
                }}
                placeholder={t("catalog.searchPlaceholder")}
                placeholderTextColor={posColors.placeholder}
                returnKeyType="done"
                selectionColor={posColors.accent}
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
                onPress={toggleSearchKeyboard}
                testID="sales-show-keyboard"
              />
            </View>
            <View
              style={styles.functionSectionHeader}
              testID="sales-product-entry-header"
            >
              <Text style={styles.functionSectionTitle}>
                {t("functions.productEntry")}
              </Text>
              {cameraScanner && !cameraScanner.active ? (
                // 连续扫码开关挪到标题行右侧，让三个录入按钮并成一行。
                <View
                  style={[
                    styles.cameraModeControl,
                    catalogActionsDisabled && styles.actionButtonDisabled,
                  ]}
                  testID="sales-camera-mode-control"
                >
                  <PosSwitch
                    accessibilityLabel={t("catalog.cameraModeSwitch")}
                    accessibilityValue={{
                      text: t(
                        cameraScanner.mode === "continuous"
                          ? "catalog.cameraModeContinuous"
                          : "catalog.cameraModeSingle",
                      ),
                    }}
                    disabled={catalogActionsDisabled}
                    ios_backgroundColor={posColors.border}
                    onValueChange={(continuous) =>
                      cameraScanner.onModeChange(
                        continuous ? "continuous" : "single",
                      )
                    }
                    style={styles.cameraModeSwitch}
                    testID="sales-camera-mode-toggle"
                    thumbColor={
                      cameraScanner.mode === "continuous"
                        ? posColors.accent
                        : posColors.surface
                    }
                    trackColor={{
                      false: posColors.border,
                      true: posColors.accentSoft,
                    }}
                    value={cameraScanner.mode === "continuous"}
                  />
                  <Text
                    adjustsFontSizeToFit
                    minimumFontScale={0.75}
                    numberOfLines={1}
                    style={[
                      styles.cameraModeLabel,
                      cameraScanner.mode === "continuous" &&
                        styles.cameraModeLabelSelected,
                      catalogActionsDisabled && styles.actionButtonTextDisabled,
                    ]}
                    testID="sales-camera-mode-continuous-label"
                  >
                    {t("catalog.cameraModeContinuous")}
                  </Text>
                </View>
              ) : null}
            </View>
            <View style={styles.searchActions} testID="sales-camera-scanner-row">
              <ActionButton
                disabled={catalogActionsDisabled}
                icon="tag-plus-outline"
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
                  icon="star-box-outline"
                  label={t("catalog.specialProducts")}
                  onPress={onOpenSpecialProducts}
                  sound="navigate"
                  style={styles.searchAction}
                  testID="sales-open-special-products"
                  tone="info"
                />
              ) : null}
              {cameraScanner && !cameraScanner.active ? (
                <ActionButton
                  disabled={catalogActionsDisabled}
                  icon="camera-outline"
                  label={t("catalog.cameraScanner")}
                  onPress={openCameraScanner}
                  sound="navigate"
                  style={styles.searchAction}
                  testID="sales-open-camera-scanner"
                  tone="secondary"
                />
              ) : null}
            </View>
              </>
            )}

            {/* 核验提示放在标题行右侧：这一行高度固定，提示出现 / 消失不会推动下面的交易功能区。 */}
            <View
              style={styles.functionSectionHeader}
              testID="sales-function-section-header"
            >
              <Text style={styles.functionSectionTitle}>
                {t("functions.saleActions")}
              </Text>
              {catalogVerifyingVisible ? (
                <View
                  style={styles.verifyingIndicator}
                  testID="sales-catalog-verifying-indicator"
                >
                  <ActivityIndicator color={posColors.mutedInk} size="small" />
                  <Text
                    accessibilityLiveRegion="polite"
                    numberOfLines={1}
                    style={styles.verifyingMessage}
                    testID="sales-catalog-verifying"
                  >
                    {t("catalog.verifying")}
                  </Text>
                </View>
              ) : null}
            </View>
            <View
              style={styles.functionTileRow}
              testID="sales-transaction-functions"
            >
              <FunctionTile
                disabled={
                  !state.capabilities.hold ||
                  cartEmpty ||
                  transactionActionsDisabled
                }
                icon="pause-circle-outline"
                label={t("functions.hold")}
                onPress={() => {
                  void presenter.holdCart();
                }}
                testID="sales-hold"
              />
              <FunctionTile
                disabled={cartEditingDisabled}
                icon="percent-outline"
                label={t("summary.orderDiscount")}
                onPress={() => setOrderDiscountVisible(true)}
                sound="navigate"
                testID="sales-order-discount"
              />
              <FunctionTile
                disabled={
                  transactionActionsDisabled ||
                  !onReprintReceipt ||
                  utilityActionPending !== null
                }
                icon="printer-outline"
                label={
                  utilityActionPending === "reprint"
                    ? t("functions.reprinting")
                    : t("functions.reprintReceipt")
                }
                onPress={() => runUtilityAction("reprint", onReprintReceipt)}
                quiet
                testID="sales-reprint-receipt"
              />
              <FunctionTile
                disabled={
                  transactionActionsDisabled ||
                  !onOpenCashDrawer ||
                  utilityActionPending !== null
                }
                icon="archive-arrow-up-outline"
                label={
                  utilityActionPending === "drawer"
                    ? t("functions.openingDrawer")
                    : t("functions.openDrawer")
                }
                onPress={() => runUtilityAction("drawer", onOpenCashDrawer)}
                quiet
                testID="sales-open-cash-drawer"
              />
              {onOpenCashMovement ? (
                <FunctionTile
                  disabled={
                    transactionActionsDisabled || utilityActionPending !== null
                  }
                  icon="cash-plus"
                  label={t("functions.cashMovement")}
                  onPress={onOpenCashMovement}
                  quiet
                  sound="navigate"
                  testID="sales-cash-movement"
                />
              ) : null}
            </View>
            {utilityActionResult ? (
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
            ) : null}
            {/* 常驻编辑区：点购物车某一行 = 选中，在这里直接改数量 / 单价 / 折扣，不再弹窗。 */}
            <View style={styles.lineEditor} testID="sales-line-editor">
              <View style={styles.functionSectionHeader}>
                <Text numberOfLines={1} style={styles.functionSectionTitle}>
                  {selectedLine
                    ? `${t("editor.title")} · ${t("cart.lineNumber", {
                        number: selectedLineNumber,
                      })}`
                    : t("editor.title")}
                </Text>
                {selectedLine ? (
                  <ActionButton
                    disabled={lineEditorDisabled}
                    icon="trash-can-outline"
                    label={t("editLine.remove")}
                    onPress={() => {
                      void presenter.removeLine(selectedLine.lineId);
                    }}
                    size="small"
                    testID="sales-line-edit-remove"
                    tone="danger"
                  />
                ) : null}
              </View>
              <Text
                numberOfLines={1}
                style={
                  selectedLine ? styles.lineEditorName : styles.lineEditorEmpty
                }
                testID="sales-line-editor-target"
              >
                {selectedLine ? selectedLine.displayName : t("editor.empty")}
              </Text>
              <View style={styles.editModeRow}>
                <LineEditModeTile
                  compact
                  disabled={lineEditorDisabled}
                  label={t("editLine.quantity")}
                  onPress={() => selectLineEditMode("quantity")}
                  selected={lineEdit?.mode === "quantity"}
                  testID="sales-line-edit-quantity"
                  value={selectedLine?.quantity ?? "—"}
                />
                <LineEditModeTile
                  compact
                  disabled={lineEditorDisabled}
                  label={t("editLine.price")}
                  onPress={() => selectLineEditMode("price")}
                  selected={lineEdit?.mode === "price"}
                  testID="sales-line-edit-price"
                  value={
                    selectedLine
                      ? formatAud(selectedLine.unitPrice.cents, locale)
                      : "—"
                  }
                />
                <LineEditModeTile
                  compact
                  disabled={lineEditorDisabled}
                  label={t("editLine.discountAmount")}
                  onPress={() => selectLineEditMode("discount-amount")}
                  selected={lineEdit?.mode === "discount-amount"}
                  testID="sales-line-edit-discount-amount"
                  tone={
                    selectedLine && selectedLine.discount.cents > 0
                      ? "danger"
                      : "default"
                  }
                  value={
                    selectedLine && selectedLine.discount.cents > 0
                      ? `−${formatAud(selectedLine.discount.cents, locale)}`
                      : "—"
                  }
                />
                <LineEditModeTile
                  compact
                  disabled={lineEditorDisabled}
                  label={t("editLine.discountPercent")}
                  onPress={() => selectLineEditMode("discount-percent")}
                  selected={lineEdit?.mode === "discount-percent"}
                  testID="sales-line-edit-discount-percent"
                  value="%"
                />
              </View>
              <NumericValueDisplay
                accessibilityLabel={t("editLine.value")}
                placeholder={
                  (lineEdit?.mode ?? "quantity") === "quantity" ? "0" : "0.00"
                }
                testID="sales-line-edit-value"
                value={lineEdit?.value ?? ""}
              />
              {lineEdit?.mode === "price" &&
              state.errorCode === "price-too-high" ? (
                <Text
                  accessibilityRole="alert"
                  style={styles.cashError}
                  testID="sales-line-edit-price-error"
                >
                  {t("error.priceTooHigh", ERROR_COPY_VALUES)}
                </Text>
              ) : null}
              <View style={styles.lineEditorKeypadRow}>
                <View style={styles.lineEditorKeypad}>
                  <SalesNumberKeypad
                    compact
                    disabled={lineEditorDisabled}
                    labels={keypadLabels}
                    // 没有选中行时按默认的「数量」显示整数键盘。
                    mode={
                      (lineEdit?.mode ?? "quantity") === "quantity"
                        ? "integer"
                        : "decimal"
                    }
                    onKeyPress={handleLineEditKey}
                    testIDPrefix="sales-line-edit"
                  />
                </View>
                <View style={styles.lineEditorSideKeys}>
                  <ActionButton
                    disabled={lineEditorDisabled}
                    label={t("discount.cancel")}
                    onPress={resetLineEditDraft}
                    sound="navigate"
                    style={styles.lineEditorCancel}
                    testID="sales-line-edit-cancel"
                    tone="secondary"
                  />
                  <ActionButton
                    disabled={lineEditorDisabled}
                    icon="check"
                    label={t("editLine.confirm")}
                    onPress={submitLineEdit}
                    style={styles.lineEditorConfirm}
                    testID="sales-line-edit-confirm"
                  />
                </View>
              </View>
            </View>
          </PosKeyboardAwareScrollView>
          {/* 快捷折扣固定在右栏最下方、不随上面滚动：11 寸屏上面一截要滚时它也一直可见，与左侧结算条底部对齐。 */}
          <View style={styles.quickDiscountSection} testID="sales-quick-discount">
            <View style={styles.functionSectionHeader}>
              <Text style={styles.functionSectionTitle}>
                {t("quickDiscount.title")}
              </Text>
              <View style={styles.quickDiscountToggle}>
                <PosSwitch
                  accessibilityLabel={t("quickDiscount.orderToggle")}
                  disabled={cartEditingDisabled}
                  ios_backgroundColor={posColors.border}
                  onValueChange={(order) => {
                    setQuickDiscountScope(order ? "order" : "line");
                  }}
                  style={styles.cameraModeSwitch}
                  testID="sales-quick-discount-order-toggle"
                  thumbColor={
                    quickDiscountScope === "order"
                      ? posColors.accent
                      : posColors.surface
                  }
                  trackColor={{
                    false: posColors.border,
                    true: posColors.accentSoft,
                  }}
                  value={quickDiscountScope === "order"}
                />
                <Text
                  style={[
                    styles.cameraModeLabel,
                    quickDiscountScope === "order" &&
                      styles.cameraModeLabelSelected,
                  ]}
                >
                  {t("quickDiscount.orderToggle")}
                </Text>
              </View>
            </View>
            <Text
              numberOfLines={1}
              style={styles.quickDiscountTarget}
              testID="sales-quick-discount-target"
            >
              {quickDiscountScope === "order"
                ? t("quickDiscount.targetOrder", {
                    count: state.cart.lines.length,
                  })
                : selectedLine
                  ? t("quickDiscount.targetLine", {
                      name: selectedLine.displayName,
                      number: selectedLineNumber,
                    })
                  : t("quickDiscount.targetNone")}
            </Text>
            <View style={styles.quickDiscountRow}>
              {QUICK_DISCOUNTS.map((basisPoints) => (
                <QuickDiscountKey
                  disabled={quickDiscountDisabled}
                  key={basisPoints}
                  label={
                    basisPoints === 0
                      ? t("quickDiscount.none")
                      : `${basisPoints / 100}%`
                  }
                  onPress={() => applyQuickDiscount(basisPoints)}
                  preview={
                    basisPoints === 0 || quickDiscountBaseCents <= 0
                      ? null
                      : `−${formatAud(
                          Math.round(
                            (quickDiscountBaseCents * basisPoints) / 10_000,
                          ),
                          locale,
                        )}`
                  }
                  selected={activeQuickDiscount === basisPoints}
                  testID={`sales-discount-${basisPoints}`}
                />
              ))}
            </View>
          </View>

        </View>
      </View>

      {state.errorCode ? (
        <ErrorBanner
          dismissLabel={t("common.dismiss")}
          message={t(errorCopyKey(state.errorCode), ERROR_COPY_VALUES)}
          onDismiss={() => presenter.dismissError()}
        />
      ) : null}

      {/* 不用 Modal：面板不抢第一响应者，隐藏 HID 输入保持聚焦，打开期间扫码枪照常可用。 */}
      {searchKeyboardVisible && !catalogActionsDisabled ? (
        <View style={styles.searchKeyboardDock} testID="sales-search-keyboard-dock">
          <SalesSearchKeyboard
            labels={{
              title: t("searchKeyboard.title"),
              backspace: t("searchKeyboard.backspace"),
              space: t("searchKeyboard.space"),
              clear: t("searchKeyboard.clear"),
              search: t("searchKeyboard.search"),
              close: t("searchKeyboard.close"),
              system: t("searchKeyboard.system"),
            }}
            onKeyPress={handleSearchKeyboardKey}
          />
        </View>
      ) : null}

      {cameraScanner?.active && cameraScanner.mode === "single" ? (
        <CameraScannerModal
          context="product"
          onClose={cameraScanner.onClose}
          onScan={cameraScanner.onScan}
          scanner={cameraScanner.scanner}
          visible
        />
      ) : null}

      <Modal
        animationType="fade"
        onRequestClose={() => setSearchDrawerVisible(false)}
        presentationStyle="overFullScreen"
        supportedOrientations={["landscape-left", "landscape-right"]}
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
          <View
            accessibilityViewIsModal
            style={styles.searchDrawer}
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
              <Text accessibilityRole="alert" style={styles.searchDrawerError}>
                {t(errorCopyKey(state.errorCode))}
              </Text>
            ) : null}
            <FlatList
              contentContainerStyle={styles.searchResults}
              data={visibleSearchResults}
              keyExtractor={(item) => `${item.productCode}:${item.lookupCode}`}
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
                  {...(resolveCartProductImage
                    ? { resolveProductImage: resolveCartProductImage }
                    : {})}
                  t={t}
                />
              )}
              style={styles.searchDrawerList}
              testID="sales-search-results-list"
            />
          </View>
        </View>
      </Modal>

      {lookupSelection && lookupSelectionModalVisible ? (
        <Modal
          animationType="fade"
          onRequestClose={() => presenter.collapseLookupSelection()}
          presentationStyle="overFullScreen"
          supportedOrientations={["landscape-left", "landscape-right"]}
          transparent
          visible
        >
          <View style={styles.searchDrawerBackdrop}>
            {/* 点遮罩只收起，不放弃：待选不能因误触而丢失（WPF 放弃须二次确认）。 */}
            <PosPressable
              accessibilityLabel={t("lookupSelection.later")}
              accessibilityRole="button"
              onPress={() => presenter.collapseLookupSelection()}
              sound="navigate"
              style={styles.searchDrawerDismissArea}
              testID="sales-lookup-selection-backdrop"
            />
            <View
              accessibilityViewIsModal
              style={styles.searchDrawer}
              testID="sales-lookup-selection"
            >
              <View style={styles.searchDrawerHeader}>
                <View style={styles.searchDrawerHeading}>
                  <Text style={styles.modalTitle}>
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
                  {state.lookupSelectionQueueLength > 1 ? (
                    <Text
                      style={styles.lookupSelectionQueue}
                      testID="sales-lookup-selection-queue"
                    >
                      {t("lookupSelection.queue", {
                        count: state.lookupSelectionQueueLength,
                      })}
                    </Text>
                  ) : null}
                </View>
                <LookupSelectionActions
                  disabled={state.lookupSelectionPending}
                  // 换到下一个待选时重置二次确认，避免确认状态落到别的条目上。
                  key={lookupSelection.selectionId}
                  onAbandon={() => presenter.cancelLookupSelection()}
                  onLater={() => presenter.collapseLookupSelection()}
                  t={t}
                />
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
            </View>
          </View>
        </Modal>
      ) : null}

      <Modal
        animationType="fade"
        onRequestClose={closeCashInput}
        presentationStyle="overFullScreen"
        supportedOrientations={["landscape-left", "landscape-right"]}
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
            <Text style={styles.modalTitle}>
              {t(zeroTotalSale ? "cash.zeroTitle" : "cash.title")}
            </Text>
            <View style={styles.numericEditorBody}>
              <View style={styles.numericEditorSummary}>
                <Text style={styles.cashDueLabel}>{t("cash.amountDue")}</Text>
                <Text style={styles.cashDueAmount}>
                  {formatAud(getCashDueCents(state.cart), locale)}
                </Text>
                {zeroTotalSale ? (
                  // 0 元订单没有款项可收：不显示实收 / 正好 / 找零与键盘，只说明确认后会发生什么。
                  <Text style={styles.fieldLabel} testID="sales-cash-zero-hint">
                    {t("cash.zeroHint")}
                  </Text>
                ) : (
                  <>
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
                  </>
                )}
                {state.errorCode === "cash-invalid" ||
                state.errorCode === "cash-insufficient" ||
                state.errorCode === "cash-failed" ? (
                  <Text accessibilityRole="alert" style={styles.cashError}>
                    {t(errorCopyKey(state.errorCode))}
                  </Text>
                ) : null}
              </View>
              {zeroTotalSale ? null : (
                <View style={styles.numericKeypadColumn}>
                  <SalesNumberKeypad
                    disabled={state.phase !== "cash"}
                    labels={keypadLabels}
                    mode="decimal"
                    onKeyPress={handleCashKey}
                    testIDPrefix="sales-cash"
                  />
                </View>
              )}
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
                    : t(zeroTotalSale ? "cash.confirmZero" : "cash.confirm")
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
        supportedOrientations={["landscape-left", "landscape-right"]}
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
          <View
            accessibilityViewIsModal
            style={styles.numericModal}
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
                {state.errorCode === "price-too-high" ? (
                  <Text
                    accessibilityRole="alert"
                    style={styles.cashError}
                    testID="sales-open-item-price-error"
                  >
                    {t("error.priceTooHigh", ERROR_COPY_VALUES)}
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
            <View style={styles.modalActions}>
              <ActionButton
                label={t("discount.cancel")}
                onPress={closeOpenItemInput}
                sound="navigate"
                style={styles.modalAction}
                testID="sales-open-item-cancel"
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
                    style={styles.modalAction}
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
                  style={styles.modalAction}
                  testID="sales-open-item-confirm"
                />
              )}
            </View>
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={() => setOrderDiscountVisible(false)}
        presentationStyle="overFullScreen"
        supportedOrientations={["landscape-left", "landscape-right"]}
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
            {/*
              快捷折扣排成一行：按钮样式是 flex: 1（按宽度均分），放进竖排容器时
              flexBasis 为 0，容器按 0 高度排版，按钮溢出压住下面的「金额 / 百分比 / 取消」。
            */}
            <View
              style={styles.editModeRow}
              testID="sales-order-discount-presets"
            >
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
        supportedOrientations={["landscape-left", "landscape-right"]}
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
        supportedOrientations={["landscape-left", "landscape-right"]}
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
      <AttendanceQrModal
        language={locale}
        onClose={() => setAttendanceQrModalVisible(false)}
        state={attendanceQrState}
        visible={attendanceQrModalVisible}
      />
    </PosScreenFrame>
  );
}

function formatCatalogField(
  label: string,
  value: string,
  locale: SalesLocale,
): string {
  return `${label}${locale === "zh" ? "：" : ": "}${value}`;
}

function formatCatalogDiscountRate(
  discountRate: number | null,
  locale: SalesLocale,
): string | null {
  if (
    discountRate === null ||
    !Number.isFinite(discountRate) ||
    discountRate <= 0
  ) {
    return null;
  }
  return new Intl.NumberFormat(locale === "zh" ? "zh-AU" : "en-AU", {
    style: "percent",
    maximumFractionDigits: 2,
  }).format(discountRate);
}

function ProductSearchRow({
  disabled = false,
  item,
  locale,
  onAdd,
  resolveProductImage,
  t,
}: Readonly<{
  disabled?: boolean;
  item: SalesProductSearchItem;
  locale: SalesLocale;
  onAdd(): void;
  resolveProductImage?: (input: {
    productCode: string;
    lookupCode: string;
  }) => Promise<string | null>;
  t(
    key: SalesCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ): string;
}>) {
  usePosThemeVersion();
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

  const itemNumber = item.itemNumber?.trim() || "—";
  const barcode = item.barcode?.trim() || item.lookupCode;
  const discountRate = formatCatalogDiscountRate(item.discountRate, locale);
  return (
    <View style={styles.productRow}>
      <View style={styles.productRowMain}>
        <CartProductThumbnail
          accessibilityLabel={t("cart.productImage", {
            product: item.displayName,
          })}
          imageUri={imageUri}
          placeholderLabel={t("cart.imagePlaceholder")}
          testID={`sales-product-${item.productCode}-image`}
        />
        <View style={styles.productIdentity}>
          <Text numberOfLines={2} style={styles.productName}>
            {item.displayName}
          </Text>
          <Text numberOfLines={1} style={styles.productCode}>
            {formatCatalogField(t("catalog.itemNumber"), itemNumber, locale)}
          </Text>
          <Text numberOfLines={1} style={styles.productCode}>
            {formatCatalogField(t("catalog.barcode"), barcode, locale)}
          </Text>
        </View>
      </View>
      <View style={styles.productRowFooter}>
        <View style={styles.productPricing}>
          <Text style={styles.productPrice}>
            {formatCatalogField(
              t("catalog.retailPrice"),
              formatAud(item.unitPriceCents, locale),
              locale,
            )}
          </Text>
          <Text style={styles.productDiscount}>
            {formatCatalogField(
              t("catalog.discount"),
              discountRate ?? t("catalog.noDiscount"),
              locale,
            )}
          </Text>
        </View>
        <ActionButton
          disabled={disabled}
          label={t("catalog.add")}
          onPress={onAdd}
          testID={`sales-product-${item.productCode}-add`}
          tone="secondary"
        />
      </View>
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

/** 一码多商品候选行：整行即选择按钮，同码商品按名称、货号、价格与价格来源区分。 */
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
  usePosThemeVersion();
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

  const itemNumber = item.itemNumber?.trim() || "—";
  const price = formatAud(item.unitPriceCents, locale);
  const discountRate = formatCatalogDiscountRate(item.discountRate, locale);
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
      <View style={styles.productIdentity}>
        <Text numberOfLines={2} style={styles.productName}>
          {item.displayName}
        </Text>
        <Text numberOfLines={1} style={styles.productCode}>
          {formatCatalogField(t("catalog.itemNumber"), itemNumber, locale)}
        </Text>
        <Text numberOfLines={1} style={styles.productCode}>
          {formatCatalogField(
            t("lookupSelection.lookupCode"),
            item.lookupCode,
            locale,
          )}
        </Text>
        <View style={styles.lookupCandidateTags}>
          <Text
            numberOfLines={1}
            style={styles.lookupCandidateSource}
            testID={`sales-lookup-candidate-${item.productCode}-source`}
          >
            {t(priceSourceCopyKey(item.priceSource))}
          </Text>
          {discountRate ? (
            <Text numberOfLines={1} style={styles.productDiscount}>
              {formatCatalogField(t("catalog.discount"), discountRate, locale)}
            </Text>
          ) : null}
        </View>
      </View>
      <View style={styles.lookupCandidatePricing}>
        <Text
          style={styles.lookupCandidatePrice}
          testID={`sales-lookup-candidate-${item.productCode}-price`}
        >
          {price}
        </Text>
        <Text style={styles.lookupCandidateChoose}>
          {t("lookupSelection.choose")}
        </Text>
      </View>
    </PosPressable>
  );
}

type CartLineRowProps = Readonly<{
  disabled: boolean;
  index: number;
  isSelected: boolean;
  item: CartLine;
  locale: SalesLocale;
  onDecrease(lineId: string): void;
  onIncrease(lineId: string): void;
  onRemove(lineId: string): void;
  onSelect(lineId: string): void;
  resolveProductDetails?: (input: {
    productCode: string;
    lookupCode: string;
  }) => Promise<SalesCartProductDetails | null>;
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
  disabled,
  index,
  isSelected,
  item,
  locale,
  onDecrease,
  onIncrease,
  onRemove,
  onSelect,
  resolveProductDetails,
  resolveProductImage,
  t,
}: CartLineRowProps) {
  usePosThemeVersion();
  const [imageUri, setImageUri] = useState<string | null | undefined>();
  const [resolvedBarcode, setResolvedBarcode] = useState<
    string | null | undefined
  >();
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
          swipeStartOffsetRef.current +
          gestureState.dx +
          gestureState.vx * 24;
        settleSwipe(
          projectedOffset <= -CART_LINE_REMOVE_ACTION_WIDTH / 2,
        );
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
    setResolvedBarcode(undefined);
    if (!resolveProductDetails && !resolveProductImage) return;
    let active = true;
    const identity = {
      productCode: item.productCode,
      lookupCode: item.lookupCode,
    };
    const pendingDetails = resolveProductDetails
      ? resolveProductDetails(identity)
      : resolveProductImage!(identity).then((resolvedImageUri) => ({
          barcode: null,
          imageUri: resolvedImageUri,
        }));
    void pendingDetails
      .then((resolved) => {
        if (!active) return;
        setImageUri(resolved?.imageUri?.trim() || null);
        setResolvedBarcode(resolved?.barcode?.trim() || null);
      })
      .catch(() => {
        if (!active) return;
        setImageUri(null);
        setResolvedBarcode(null);
      });
    return () => {
      active = false;
    };
  }, [
    item.lookupCode,
    item.productCode,
    resolveProductDetails,
    resolveProductImage,
  ]);

  const itemNumber = item.itemNumber?.trim() || "—";
  const fallbackLookupCode = item.lookupCode.trim() || "—";
  const barcode =
    resolveProductDetails && resolvedBarcode === undefined
      ? "—"
      : resolvedBarcode || fallbackLookupCode;

  return (
    <View style={styles.cartLineSwipeContainer}>
      <View
        accessibilityElementsHidden={!removeActionVisible}
        importantForAccessibility={
          removeActionVisible ? "auto" : "no-hide-descendants"
        }
        style={styles.cartLineRemoveAction}
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
      {/*
        pointerEvents="box-none"：这层手势壳铺满整行，向左滑开后右侧 96 宽的区域是空的，
        露出的「删除」按钮在它下面。壳自己要是接触摸（默认 auto），点「删除」会被它截走，
        真机上就点不了；box-none 让壳只在子视图（滑动面）上触发手势，空白处的触摸交给下面的按钮。
      */}
      <PosPanResponderView
        panHandlers={swipeResponder.panHandlers}
        pointerEvents="box-none"
        testID={`sales-line-${item.lineId}-swipe-surface`}
      >
        <Animated.View
          style={[
            styles.cartLineSwipeSurface,
            { transform: [{ translateX: swipeOffset }] },
          ]}
        >
          <PosPressable
            accessibilityActions={[
              { label: t("cart.remove"), name: "delete" },
            ]}
            accessibilityHint={t("cart.editHint")}
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
              // 点商品行 = 选中；数量 / 单价 / 折扣在右栏常驻编辑区修改。
              onSelect(item.lineId);
            }}
            sound="navigate"
            style={({ pressed }) => [
              styles.cartLine,
              item.actualAmount.cents < 0 && styles.cartLineNegative,
              item.actualAmount.cents === 0 && styles.cartLineZero,
              isSelected && styles.cartLineSelected,
              pressed && styles.cartLinePressed,
            ]}
            testID={`sales-line-${item.lineId}`}
          >
            <Text
              accessibilityLabel={t("cart.lineNumber", {
                number: index + 1,
              })}
              style={styles.cartLineNumber}
              testID={`sales-line-${item.lineId}-line-number`}
            >
              {index + 1}
            </Text>
            <CartProductThumbnail
              accessibilityLabel={t("cart.productImage", {
                product: item.displayName,
              })}
              imageUri={imageUri}
              placeholderLabel={t("cart.imagePlaceholder")}
              testID={`sales-line-${item.lineId}-image`}
            />
            <View style={styles.cartLineIdentity}>
              <Text numberOfLines={2} style={styles.cartLineName}>
                {item.displayName}
              </Text>
              <Text
                numberOfLines={2}
                style={styles.cartLineCode}
                testID={`sales-line-${item.lineId}-identifiers`}
              >
                {`${formatCatalogField(t("catalog.itemNumber"), itemNumber, locale)} · ${formatCatalogField(t("catalog.barcode"), barcode, locale)}`}
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
            {/*
              加减区整列（含按钮之间、数量上、按钮上下的空白）都接住点击但什么也不做：
              整行点一下就开编辑弹窗，点偏一点（−/＋ 旁边）不能再落到行上。
            */}
            <PosPressable
              accessible={false}
              onPress={ignoreLineControlsPress}
              sound={false}
              style={styles.lineControls}
              testID={`sales-line-${item.lineId}-controls`}
            >
              <ActionButton
                accessibilityLabel={t("cart.decrease")}
                disabled={disabled}
                label="−"
                onPress={() => onDecrease(item.lineId)}
                size="small"
                style={styles.lineStepButton}
                testID={`sales-line-${item.lineId}-decrease`}
                tone="secondary"
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
                size="small"
                style={styles.lineStepButton}
                testID={`sales-line-${item.lineId}-increase`}
                tone="secondary"
              />
            </PosPressable>
            <Text
              adjustsFontSizeToFit
              minimumFontScale={0.7}
              numberOfLines={1}
              style={styles.cartLineTotal}
            >
              {formatAud(item.actualAmount.cents, locale)}
            </Text>
          </PosPressable>
        </Animated.View>
      </PosPanResponderView>
    </View>
  );
}, cartLineRowPropsEqual);

/** 加减区空白处的点击：有意什么都不做，只是不让它冒泡成「点整行 → 打开编辑弹窗」。 */
function ignoreLineControlsPress(): void {}

function cartLineRowPropsEqual(
  previous: CartLineRowProps,
  next: CartLineRowProps,
): boolean {
  const before = previous.item;
  const after = next.item;
  return (
    previous.disabled === next.disabled &&
    previous.index === next.index &&
    previous.isSelected === next.isSelected &&
    previous.locale === next.locale &&
    previous.onDecrease === next.onDecrease &&
    previous.onIncrease === next.onIncrease &&
    previous.onRemove === next.onRemove &&
    previous.onSelect === next.onSelect &&
    previous.resolveProductDetails === next.resolveProductDetails &&
    previous.resolveProductImage === next.resolveProductImage &&
    previous.t === next.t &&
    before.lineId === after.lineId &&
    before.productCode === after.productCode &&
    before.itemNumber === after.itemNumber &&
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
  usePosThemeVersion();
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
  usePosThemeVersion();
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


function ErrorBanner({
  dismissLabel,
  message,
  onDismiss,
}: Readonly<{
  dismissLabel: string;
  message: string;
  onDismiss(): void;
}>) {
  usePosThemeVersion();
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

/**
 * 待选弹窗操作：「稍后选择」收起继续扫码；「放弃此项」须再点一次确认
 * （对齐 WPF），确认态由父级按条目 key 重置。
 */
function LookupSelectionActions({
  disabled,
  onAbandon,
  onLater,
  t,
}: Readonly<{
  disabled: boolean;
  onAbandon: () => void;
  onLater: () => void;
  t(
    key: SalesCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ): string;
}>) {
  usePosThemeVersion();
  const [confirmingAbandon, setConfirmingAbandon] = useState(false);
  return (
    <View style={styles.lookupSelectionActions}>
      <ActionButton
        disabled={disabled}
        label={t("lookupSelection.later")}
        onPress={() => {
          setConfirmingAbandon(false);
          onLater();
        }}
        sound="navigate"
        testID="sales-lookup-selection-later"
        tone="quiet"
      />
      <ActionButton
        disabled={disabled}
        label={t(
          confirmingAbandon
            ? "lookupSelection.confirmAbandon"
            : "lookupSelection.cancel",
        )}
        onPress={() => {
          if (!confirmingAbandon) {
            setConfirmingAbandon(true);
            return;
          }
          setConfirmingAbandon(false);
          onAbandon();
        }}
        testID="sales-lookup-selection-cancel"
        tone={confirmingAbandon ? "danger" : "secondary"}
      />
    </View>
  );
}

/** 收银页内的按钮：尺寸与配色走通用 PosButton，保留「危险操作用警示音」的约定。 */
function ActionButton({
  accessibilityLabel,
  disabled = false,
  icon,
  label,
  onPress,
  size = "medium",
  sound,
  style,
  testID,
  tone = "primary",
}: ActionButtonProps) {
  return (
    <PosButton
      {...(accessibilityLabel ? { accessibilityLabel } : {})}
      disabled={disabled}
      {...(icon ? { icon } : {})}
      label={label}
      onPress={onPress}
      size={size}
      sound={sound ?? (tone === "danger" ? "danger" : "tap")}
      style={style}
      {...(testID ? { testID } : {})}
      tone={tone}
    />
  );
}

type LineEditModeTileProps = Readonly<{
  /** 右栏常驻编辑区用矮一点的格子。 */
  compact?: boolean;
  disabled?: boolean;
  label: string;
  onPress(): void;
  selected: boolean;
  testID: string;
  tone?: "default" | "danger";
  value: string;
}>;

/** 编辑项：直接显示这一项现在的值，点一下切换键盘要改的内容。 */
function LineEditModeTile({
  compact = false,
  disabled = false,
  label,
  onPress,
  selected,
  testID,
  tone = "default",
  value,
}: LineEditModeTileProps) {
  return (
    <PosPressable
      accessibilityLabel={`${label}: ${value}`}
      accessibilityRole="button"
      accessibilityState={{ disabled, selected }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.editModeButton,
        compact && styles.editModeButtonCompact,
        selected && styles.editModeButtonSelected,
        compact && selected && styles.editModeButtonCompactSelected,
        disabled && styles.actionButtonDisabled,
        pressed && !disabled && styles.actionButtonPressed,
      ]}
      testID={testID}
    >
      <Text
        numberOfLines={1}
        style={[
          styles.editModeLabel,
          selected && styles.editModeLabelSelected,
        ]}
      >
        {label}
      </Text>
      <Text
        adjustsFontSizeToFit
        minimumFontScale={0.7}
        numberOfLines={1}
        style={[
          styles.editModeValue,
          compact && styles.editModeValueCompact,
          tone === "danger" && styles.discountAmountText,
        ]}
      >
        {value}
      </Text>
    </PosPressable>
  );
}

type FunctionTileProps = Readonly<{
  disabled: boolean;
  icon: PosIconName;
  label: string;
  onPress(): void;
  /** 打印、开箱、钱箱这类辅助操作用浅底。 */
  quiet?: boolean;
  sound?: "tap" | "navigate";
  testID: string;
}>;

/** 交易功能小按钮：图标在上、文字在下，五个并成一行。 */
function FunctionTile({
  disabled,
  icon,
  label,
  onPress,
  quiet = false,
  sound = "tap",
  testID,
}: FunctionTileProps) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityLabel={label}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound={sound}
      style={({ pressed }) => [
        styles.functionTile,
        quiet && styles.functionTileQuiet,
        disabled && styles.actionButtonDisabled,
        pressed && !disabled && styles.actionButtonPressed,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={disabled ? posColors.disabledInk : posColors.ink}
        name={icon}
        size={22}
      />
      <Text
        adjustsFontSizeToFit
        minimumFontScale={0.75}
        numberOfLines={1}
        style={[
          styles.functionTileLabel,
          disabled && styles.actionButtonTextDisabled,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

type QuickDiscountKeyProps = Readonly<{
  disabled: boolean;
  label: string;
  onPress(): void;
  /** 这一档能减多少钱；「无」与没有基数时不显示。 */
  preview: string | null;
  selected: boolean;
  testID: string;
}>;

function QuickDiscountKey({
  disabled,
  label,
  onPress,
  preview,
  selected,
  testID,
}: QuickDiscountKeyProps) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityLabel={preview ? `${label} ${preview}` : label}
      accessibilityRole="button"
      accessibilityState={{ disabled, selected }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.quickDiscountKey,
        selected && styles.quickDiscountKeySelected,
        disabled && styles.actionButtonDisabled,
        pressed && !disabled && styles.actionButtonPressed,
      ]}
      testID={testID}
    >
      <Text
        numberOfLines={1}
        style={[
          styles.quickDiscountKeyLabel,
          selected && styles.quickDiscountKeyLabelSelected,
          disabled && styles.actionButtonTextDisabled,
        ]}
      >
        {label}
      </Text>
      {preview ? (
        <Text
          adjustsFontSizeToFit
          minimumFontScale={0.7}
          numberOfLines={1}
          style={[
            styles.quickDiscountKeyPreview,
            selected && styles.quickDiscountKeyPreviewSelected,
          ]}
        >
          {preview}
        </Text>
      ) : null}
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
  icon: "keyboard-outline" | "magnify";
  iconTestID: string;
  loading?: boolean;
  onPress(): void;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      accessibilityState={{ busy: loading, disabled }}
      disabled={disabled}
      onPress={onPress}
      sound="navigate"
      style={({ pressed }) => [
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
          size={30}
          testID={iconTestID}
        />
      )}
    </PosPressable>
  );
}

function cartLineKeyExtractor(line: CartLine): string {
  return line.lineId;
}

// 错误文案的占位值：单价上限来自共享常量，不在文案里写死。
const ERROR_COPY_VALUES = { max: MAX_MANUAL_UNIT_PRICE_DISPLAY } as const;

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
    case "price-too-high":
      return "error.priceTooHigh";
    case "invalid-discount":
      return "error.invalidDiscount";
    case "empty-cart":
      return "error.emptyCart";
    case "total-not-payable":
      return "error.totalNotPayable";
    case "zero-price-item":
      return "error.zeroPriceItem";
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
    case "lookup-selection-required":
      return "error.lookupSelectionRequired";
    case "lookup-selection-queue-full":
      return "error.lookupSelectionQueueFull";
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

const styles = createPosStyles((colors) => ({
  actionButtonDisabled: {
    backgroundColor: colors.disabledSurface,
    borderColor: colors.disabledBorder,
    opacity: 0.72,
  },
  actionButtonPressed: {
    opacity: 0.72,
    transform: [{ scale: 0.99 }],
  },
  actionButtonTextDisabled: {
    color: colors.disabledInk,
  },
  cartCount: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
  },
  cartHeader: {
    alignItems: "center",
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: 64,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  cartLine: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 10,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    minHeight: 86,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  cartLineRemoveAction: {
    bottom: 0,
    position: "absolute",
    right: 0,
    top: 0,
    width: CART_LINE_REMOVE_ACTION_WIDTH,
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
    backgroundColor: colors.surface,
  },
  cartLineNegative: {
    backgroundColor: colors.redSoft,
    borderColor: colors.red,
  },
  cartLinePressed: {
    opacity: 0.82,
  },
  cartLineSelected: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.green,
  },
  cartLineZero: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellow,
  },
  cartLineCode: {
    color: colors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    marginTop: 2,
  },
  cartLineIdentity: {
    flex: 1,
    minWidth: 0,
  },
  cartLineName: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 18,
  },
  cartLineNumber: {
    color: colors.mutedInk,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
    textAlign: "center",
    width: 24,
  },
  cartLineTotal: {
    color: colors.ink,
    flexShrink: 0,
    fontSize: 19,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    textAlign: "right",
    width: 92,
  },
  cartLineUnitPrice: {
    color: colors.ink,
    fontSize: 13.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
    marginTop: 1,
  },
  cartList: {
    gap: 6,
    padding: 8,
  },
  cartListViewport: {
    backgroundColor: colors.surfaceAlt,
    flex: 1,
  },
  cartPane: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1,
    flex: 1,
    minHeight: 0,
    minWidth: 0,
    overflow: "hidden",
  },
  cartProductImage: {
    height: "100%",
    width: "100%",
  },
  cartProductImageFrame: {
    alignItems: "center",
    backgroundColor: colors.surfaceSunken,
    borderRadius: 8,
    height: 54,
    justifyContent: "center",
    overflow: "hidden",
    width: 54,
  },
  cartProductImagePlaceholder: {
    color: colors.placeholder,
    fontSize: 10,
    fontWeight: "900",
    letterSpacing: 0.4,
  },
  // 连续扫码开关放在「商品录入」标题行右侧：不再占一整块按钮位。
  cameraModeControl: {
    alignItems: "center",
    borderRadius: 8,
    flexDirection: "row",
    flexShrink: 1,
    gap: 6,
    justifyContent: "center",
    // 开关整块仍是 44 的触控区。
    minHeight: 44,
  },
  cameraModeLabel: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "800",
  },
  cameraModeLabelSelected: {
    color: colors.orange,
    fontWeight: "900",
  },
  cameraModeSwitch: {
    alignSelf: "center",
    flexShrink: 0,
  },
  cameraScannerAction: {
    flexBasis: "47%",
    flexGrow: 1,
  },
  cameraScannerRow: {
    alignItems: "stretch",
    flexDirection: "row",
    gap: 8,
  },
  cashDueAmount: {
    color: colors.ink,
    fontSize: 42,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -1,
    marginBottom: 18,
  },
  cashDueLabel: {
    color: colors.mutedInk,
    fontSize: 12,
    fontWeight: "800",
    marginTop: 18,
    textTransform: "uppercase",
  },
  cashError: {
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    marginTop: 12,
  },
  cashExactAction: {
    marginTop: 12,
    width: "100%",
  },
  changeAmount: {
    color: colors.accent,
    fontSize: 72,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -2,
    lineHeight: 80,
  },
  changeCard: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 14,
    borderWidth: 1,
    maxWidth: 720,
    paddingHorizontal: 36,
    paddingVertical: 14,
    width: "100%",
  },
  changeLabel: {
    color: colors.accent,
    fontSize: 18,
    fontWeight: "900",
  },
  changePreview: {
    alignItems: "center",
    backgroundColor: colors.greenSoft,
    borderRadius: 4,
    flexDirection: "row",
    justifyContent: "space-between",
    marginTop: 18,
    minHeight: 64,
    paddingHorizontal: 18,
  },
  changePreviewAmount: {
    color: colors.green,
    fontSize: 26,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  changePreviewLabel: {
    color: colors.green,
    fontSize: 14,
    fontWeight: "800",
  },
  cartColumn: {
    flex: 1,
    gap: 10,
    minHeight: 0,
    minWidth: 0,
  },
  checkoutBar: {
    alignItems: "stretch",
    flexDirection: "row",
    gap: 10,
  },
  checkoutSummary: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1,
    flex: 1,
    flexDirection: "row",
    gap: 24,
    minWidth: 0,
    paddingHorizontal: 18,
    paddingVertical: 8,
  },
  checkoutCell: { gap: 2 },
  checkoutCellLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  checkoutCellAmount: {
    color: colors.ink,
    fontSize: 18,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  checkoutTotal: {
    alignItems: "flex-end",
    flex: 1,
    gap: 2,
    minWidth: 0,
  },
  checkoutTotalAmount: {
    color: colors.ink,
    fontSize: 34,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -0.5,
    textAlign: "right",
  },
  checkoutButton: {
    flexBasis: 300,
    flexGrow: 0,
    flexShrink: 1,
    minWidth: 220,
  },
  onlineCheckoutButton: {
    marginTop: 10,
    minHeight: 64,
    width: "100%",
  },
  discountAmountText: {
    color: colors.red,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  discountButton: {
    flex: 1,
  },
  discountGrid: {
    gap: 8,
  },
  discountHint: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 18,
  },
  editModeButton: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1.5,
    flex: 1,
    justifyContent: "center",
    minHeight: 88,
    paddingHorizontal: 16,
    paddingVertical: 8,
  },
  editModeGrid: {
    gap: 10,
  },
  editModeButtonCompact: {
    minHeight: 56,
    paddingHorizontal: 8,
    paddingVertical: 4,
  },
  editModeButtonCompactSelected: { paddingHorizontal: 7 },
  editModeValueCompact: { fontSize: 17 },
  editorModal: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 6,
    borderWidth: 1,
    maxWidth: 560,
    padding: 26,
    width: "65%",
  },
  emptyCart: {
    alignItems: "center",
    backgroundColor: colors.surfaceAlt,
    flex: 1,
    gap: 8,
    justifyContent: "center",
    padding: 24,
  },
  emptyCartCompactHint: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    justifyContent: "center",
    paddingTop: 18,
  },
  emptyCartWithQr: {
    backgroundColor: colors.surfaceAlt,
    flex: 1,
  },
  emptyCartHint: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    textAlign: "center",
  },
  emptyCartIcon: {
    color: colors.chromeMuted,
    fontSize: 38,
    fontWeight: "300",
  },
  emptyCartTitle: {
    color: colors.ink,
    fontSize: 22,
    fontWeight: "900",
  },
  errorBanner: {
    alignItems: "center",
    backgroundColor: colors.redSoft,
    borderColor: colors.red,
    borderTopWidth: 1,
    flexDirection: "row",
    minHeight: 48,
    paddingLeft: 18,
  },
  errorBannerText: {
    color: colors.red,
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
    color: colors.red,
    fontSize: 24,
    fontWeight: "700",
  },
  fieldLabel: {
    color: colors.ink,
    fontSize: 13,
    fontWeight: "800",
    marginBottom: 7,
  },
  functionAction: {
    flexBasis: "47%",
    flexGrow: 1,
    minHeight: 76,
  },
  functionGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  functionPane: {
    flexBasis: 404,
    flexGrow: 0,
    flexShrink: 0,
    gap: 10,
    maxWidth: 420,
    minWidth: 340,
  },
  functionScroll: {
    flex: 1,
    minHeight: 0,
  },
  functionScrollContent: {
    gap: 10,
    paddingBottom: 4,
  },
  functionTileRow: {
    flexDirection: "row",
    gap: 6,
  },
  functionTile: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1.5,
    flex: 1,
    gap: 2,
    justifyContent: "center",
    minHeight: 60,
    paddingHorizontal: 4,
  },
  functionTileQuiet: { backgroundColor: colors.surfaceAlt },
  functionTileLabel: {
    color: colors.ink,
    fontSize: 13.5,
    fontWeight: "900",
  },
  lineEditor: { gap: 8 },
  lineEditorName: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  lineEditorEmpty: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
  },
  lineEditorKeypadRow: {
    flexDirection: "row",
    gap: 6,
  },
  lineEditorKeypad: { flex: 1 },
  lineEditorSideKeys: {
    gap: 6,
    width: 92,
  },
  lineEditorCancel: { flex: 2 },
  lineEditorConfirm: { flex: 3 },
  quickDiscountSection: {
    borderTopColor: colors.border,
    borderTopWidth: 1,
    gap: 6,
    paddingTop: 8,
  },
  quickDiscountToggle: {
    alignItems: "center",
    flexDirection: "row",
    gap: 6,
    minHeight: 32,
  },
  quickDiscountTarget: {
    color: colors.accent,
    fontSize: 12.5,
    fontWeight: "800",
  },
  quickDiscountRow: {
    flexDirection: "row",
    gap: 6,
  },
  quickDiscountKey: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1.5,
    flex: 1,
    justifyContent: "center",
    minHeight: 54,
    paddingHorizontal: 2,
  },
  quickDiscountKeySelected: {
    backgroundColor: colors.accent,
    borderColor: colors.accent,
  },
  quickDiscountKeyLabel: {
    color: colors.ink,
    fontSize: 17,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  quickDiscountKeyLabelSelected: { color: colors.onAccent },
  quickDiscountKeyPreview: {
    color: colors.mutedInk,
    fontSize: 10.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  quickDiscountKeyPreviewSelected: { color: colors.onAccent },
  // 固定行高 = 小号转圈的高度：核验提示出现 / 消失时这一行不变高，下面的按钮区不跳动。
  functionSectionHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 8,
    justifyContent: "space-between",
    minHeight: 20,
  },
  functionSectionTitle: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
  },
  lineControls: {
    alignItems: "center",
    alignSelf: "stretch",
    flexDirection: "row",
    flexShrink: 0,
    paddingHorizontal: 8,
  },
  lockIcon: {
    alignItems: "center",
    backgroundColor: colors.inverseSurface,
    borderRadius: 64,
    height: 128,
    justifyContent: "center",
    width: 128,
  },
  lockedHint: {
    color: colors.mutedInk,
    fontSize: 18,
    fontWeight: "700",
  },
  lockedNotice: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 4,
    borderWidth: 1,
    marginTop: 24,
    paddingHorizontal: 20,
    paddingVertical: 14,
  },
  lockedNoticeText: {
    color: colors.mutedInk,
    fontSize: 14,
  },
  lockedScreen: {
    alignItems: "center",
    flex: 1,
    gap: 12,
    justifyContent: "center",
    padding: 32,
  },
  lockedTitle: {
    color: colors.ink,
    fontSize: 36,
    fontWeight: "900",
    marginTop: 8,
  },
  lookupCandidateChoose: {
    color: colors.blue,
    fontSize: 13,
    fontWeight: "900",
  },
  lookupCandidatePrice: {
    color: colors.ink,
    fontSize: 18,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  lookupCandidatePricing: {
    alignItems: "flex-end",
    gap: 6,
    justifyContent: "center",
    minWidth: 72,
  },
  lookupCandidateRow: {
    alignItems: "center",
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: 96,
    paddingVertical: 10,
  },
  lookupCandidateSource: {
    backgroundColor: colors.orangeSoft,
    borderRadius: 3,
    color: colors.orange,
    fontSize: 11,
    fontWeight: "900",
    overflow: "hidden",
    paddingHorizontal: 6,
    paddingVertical: 2,
  },
  lookupCandidateTags: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 6,
    marginTop: 5,
  },
  lookupSelectionHint: {
    color: colors.mutedInk,
    fontSize: 13,
    lineHeight: 18,
    marginTop: 4,
  },
  lookupPendingBar: {
    alignItems: "center",
    backgroundColor: colors.yellowSoft,
    borderBottomColor: colors.orange,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 16,
    justifyContent: "space-between",
    minHeight: MIN_TOUCH_TARGET,
    paddingHorizontal: 20,
    paddingVertical: 8,
  },
  lookupPendingBarAction: {
    color: colors.orange,
    fontSize: 14,
    fontWeight: "900",
  },
  lookupPendingBarTitle: {
    color: colors.ink,
    flex: 1,
    fontSize: 15,
    fontWeight: "900",
  },
  lookupSelectionActions: {
    flexDirection: "row",
    gap: 10,
  },
  lookupSelectionQueue: {
    color: colors.orange,
    fontSize: 13,
    fontWeight: "800",
    marginTop: 4,
  },
  modalAction: {
    flex: 1,
  },
  modalCancelAction: {
    marginTop: 12,
  },
  modalActions: {
    flexDirection: "row",
    gap: 12,
    marginTop: 22,
  },
  modalBackdrop: {
    alignItems: "center",
    backgroundColor: colors.overlay,
    flex: 1,
    justifyContent: "center",
    padding: 28,
  },
  modalDismissArea: {
    ...StyleSheet.absoluteFillObject,
  },
  modalTitle: {
    color: colors.ink,
    fontSize: 24,
    fontWeight: "900",
  },
  numericEditorBody: {
    alignItems: "stretch",
    flexDirection: "row",
    gap: 24,
    marginTop: 18,
  },
  numericEditorSummary: {
    flex: 1,
    justifyContent: "center",
    minWidth: 0,
  },
  numericKeypadColumn: {
    flexBasis: 352,
    flexGrow: 0,
    flexShrink: 1,
    maxWidth: 380,
    minWidth: 292,
  },
  numericModal: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 6,
    borderWidth: 1,
    maxWidth: 820,
    padding: 26,
    width: "78%",
  },
  numericValueDisplay: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 12,
    borderWidth: 2,
    flexDirection: "row",
    minHeight: 72,
    paddingHorizontal: 16,
  },
  numericValuePlaceholder: {
    color: colors.placeholder,
  },
  numericValuePrefix: {
    color: colors.mutedInk,
    fontSize: 24,
    fontWeight: "800",
    marginRight: 8,
  },
  numericValueText: {
    color: colors.ink,
    flex: 1,
    fontSize: 30,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    textAlign: "right",
  },
  mutedText: {
    color: colors.mutedInk,
  },
  offlineCard: {
    backgroundColor: colors.orangeSoft,
    borderColor: colors.orange,
    borderRadius: 4,
    borderWidth: 1,
    marginBottom: 10,
    marginTop: 8,
    padding: 14,
  },
  offlineHint: {
    color: colors.yellowInk,
    fontSize: 12,
    lineHeight: 17,
    marginTop: 4,
  },
  offlineTitle: {
    color: colors.yellowInk,
    fontSize: 13,
    fontWeight: "900",
  },
  paneTitle: {
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
  },
  productCode: {
    color: colors.mutedInk,
    fontSize: 11,
    fontVariant: ["tabular-nums"],
    marginTop: 3,
  },
  productIdentity: {
    flex: 1,
    minWidth: 0,
  },
  productName: {
    color: colors.ink,
    fontSize: 13,
    fontWeight: "800",
    lineHeight: 17,
  },
  productPrice: {
    color: colors.ink,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  productDiscount: {
    color: colors.red,
    fontSize: 12,
    fontWeight: "800",
  },
  productPricing: {
    flex: 1,
    gap: 3,
    minWidth: 0,
  },
  productRow: {
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    minHeight: 116,
    paddingVertical: 10,
  },
  productRowFooter: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "space-between",
    marginTop: 8,
  },
  productRowMain: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
  quantityText: {
    color: colors.ink,
    fontSize: 22,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  quantityValue: {
    alignItems: "center",
    justifyContent: "center",
    width: 48,
  },
  runtimeBanner: {
    backgroundColor: colors.blueSoft,
    borderBottomColor: colors.blue,
    borderBottomWidth: 1,
    minHeight: 38,
    paddingHorizontal: 20,
    paddingVertical: 9,
  },
  runtimeBannerText: {
    color: colors.blue,
    fontSize: 12,
    fontWeight: "800",
  },
  updateGateBanner: {
    alignItems: "center",
    backgroundColor: colors.yellowSoft,
    borderBottomColor: colors.orange,
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
    color: colors.mutedInk,
    fontSize: 12,
    lineHeight: 17,
  },
  updateGateTitle: {
    color: colors.ink,
    fontSize: 14,
    fontWeight: "900",
  },
  searchAction: {
    flex: 1,
    minHeight: 76,
  },
  searchActions: {
    flexDirection: "row",
    gap: 8,
  },
  searchDrawer: {
    backgroundColor: colors.surface,
    borderLeftColor: colors.border,
    borderLeftWidth: 1,
    flexBasis: "38%",
    height: "100%",
    maxWidth: 460,
    minWidth: 360,
    paddingBottom: 16,
    paddingHorizontal: 18,
    paddingTop: 24,
  },
  searchDrawerBackdrop: {
    backgroundColor: colors.overlay,
    flex: 1,
    flexDirection: "row",
    justifyContent: "flex-end",
  },
  searchDrawerDismissArea: {
    flex: 1,
  },
  searchDrawerError: {
    backgroundColor: colors.redSoft,
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    marginVertical: 8,
    padding: 10,
  },
  searchDrawerHeader: {
    alignItems: "center",
    borderBottomColor: colors.border,
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
    color: colors.mutedInk,
    fontSize: 12,
    marginTop: 4,
  },
  searchInput: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 10,
    borderWidth: 2,
    color: colors.ink,
    flex: 1,
    fontSize: 17,
    fontWeight: "600",
    height: 64,
    paddingHorizontal: 14,
  },
  searchInputRow: {
    alignItems: "stretch",
    flexDirection: "row",
    gap: 8,
  },
  // 贴底整宽面板，位置与系统键盘一致；底部多留空间避开 Home 指示条。
  searchKeyboardDock: {
    backgroundColor: colors.canvas,
    borderTopColor: colors.border,
    borderTopWidth: 1,
    bottom: 0,
    left: 0,
    paddingBottom: 20,
    paddingHorizontal: 16,
    paddingTop: 12,
    position: "absolute",
    right: 0,
  },
  searchIconAction: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
    borderRadius: 10,
    borderWidth: 1.5,
    height: 64,
    justifyContent: "center",
    minHeight: 64,
    minWidth: 64,
    width: 64,
  },
  searchMessage: {
    color: colors.mutedInk,
    fontSize: 13,
    paddingVertical: 16,
    textAlign: "center",
  },
  searchResults: {
    paddingBottom: 12,
  },
  successAction: {
    marginTop: 6,
    maxWidth: 720,
    width: "100%",
  },
  successEyebrow: {
    color: colors.green,
    fontSize: 12,
    fontWeight: "900",
    letterSpacing: 1.5,
    marginTop: 18,
  },
  successOrder: {
    color: colors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    marginTop: 8,
  },
  successScreen: {
    alignItems: "center",
    flex: 1,
    gap: 14,
    justifyContent: "center",
    padding: 24,
  },
  successSync: {
    color: colors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 16,
    maxWidth: 480,
    textAlign: "center",
  },
  successTitle: {
    color: colors.green,
    fontSize: 32,
    fontWeight: "900",
  },
  successWarning: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
    borderRadius: 6,
    borderWidth: 1,
    color: colors.ink,
    fontSize: 14,
    lineHeight: 20,
    marginTop: 14,
    maxWidth: 560,
    paddingHorizontal: 14,
    paddingVertical: 10,
    textAlign: "center",
  },
  summaryAmount: {
    color: colors.ink,
    fontSize: 14,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  summaryLabel: {
    color: colors.ink,
    fontSize: 14,
  },
  summaryPane: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: 12,
    borderWidth: 1,
    paddingHorizontal: 16,
    paddingVertical: 10,
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
  summaryTotal: {
    alignItems: "baseline",
    borderTopColor: colors.border,
    borderTopWidth: 1,
    flexDirection: "row",
    gap: 12,
    justifyContent: "space-between",
    marginTop: 6,
    paddingTop: 6,
  },
  totalAmount: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 46,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -1,
    lineHeight: 52,
    textAlign: "right",
  },
  totalLabel: {
    color: colors.ink,
    fontSize: 18,
    fontWeight: "900",
  },
  totalRow: {
    alignItems: "flex-end",
    flexDirection: "row",
    justifyContent: "space-between",
  },
  utilityResult: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.green,
    borderRadius: 4,
    borderWidth: 1,
    color: colors.green,
    fontSize: 12,
    fontWeight: "700",
    lineHeight: 17,
    padding: 10,
  },
  utilityResultWarning: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.orange,
    color: colors.yellowInk,
  },
  verifyingIndicator: {
    alignItems: "center",
    flexDirection: "row",
    flexShrink: 1,
    gap: 6,
    height: 20,
  },
  verifyingMessage: {
    color: colors.mutedInk,
    flexShrink: 1,
    fontSize: 12,
  },
  totalRule: {
    backgroundColor: colors.inverseSurface,
    height: 2,
    marginBottom: 14,
    marginTop: 10,
  },
  workspaceRow: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  cartHeaderActions: {
    flexDirection: "row",
    gap: 8,
    marginLeft: "auto",
  },
  lineStepButton: {
    minHeight: 64,
    paddingHorizontal: 0,
    width: 60,
  },
  lineEditModal: {
    backgroundColor: colors.canvas,
    borderRadius: 16,
    gap: 14,
    maxWidth: 840,
    padding: 20,
    width: "90%",
  },
  lineEditHeader: {
    alignItems: "center",
    flexDirection: "row",
    gap: 14,
  },
  lineEditHeading: {
    flex: 1,
    minWidth: 0,
  },
  lineEditSubtitle: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    marginTop: 3,
  },
  lineEditTotal: {
    alignItems: "flex-end",
  },
  lineEditTotalLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
  },
  lineEditTotalAmount: {
    color: colors.ink,
    fontSize: 24,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  lineEditBody: {
    alignItems: "flex-start",
    flexDirection: "row",
    gap: 18,
  },
  lineEditModes: {
    flex: 1,
    gap: 8,
    minWidth: 0,
  },
  lineEditKeypad: {
    gap: 10,
    width: 356,
  },
  lineEditValue: {
    gap: 6,
  },
  lineEditFooter: {
    alignItems: "center",
    flexDirection: "row",
    gap: 12,
  },
  lineEditFooterSpacer: {
    flex: 1,
  },
  lineEditCancel: {
    minWidth: 150,
  },
  lineEditConfirm: {
    minWidth: 220,
  },
  editModeButtonSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
    borderWidth: 2.5,
    paddingHorizontal: 15,
  },
  editModeLabel: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
  },
  editModeLabelSelected: {
    color: colors.accent,
  },
  editModeValue: {
    color: colors.ink,
    fontSize: 26,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  successBanner: {
    alignItems: "center",
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
    borderRadius: 14,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 18,
    maxWidth: 720,
    paddingHorizontal: 24,
    paddingVertical: 16,
    width: "100%",
  },
  successCopy: {
    flex: 1,
    minWidth: 0,
  },
  discountModal: {
    backgroundColor: colors.canvas,
    borderRadius: 16,
    gap: 12,
    maxWidth: 620,
    padding: 22,
    width: "70%",
  },
  editModeRow: {
    flexDirection: "row",
    gap: 10,
  },
}));
