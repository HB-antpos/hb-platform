import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  Modal,
  ScrollView,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
  type StyleProp,
  type ViewStyle,
} from "react-native";

import {
  linklyKeyCopyKey,
  paymentFieldIssueCopyKey,
  paymentMethodCopyKey,
  paymentPhaseHintKey,
  paymentPhaseTitleKey,
  paymentRuntimeErrorCopyKey,
  paymentText,
  resolvePaymentLocale,
  type PaymentCopyKey,
  type PaymentLocale,
} from "./payment-copy";
import {
  LINKLY_SAFE_OPERATOR_KEYS,
  canSelectPaymentMethod,
  parseAudInput,
  type PaymentPresenterState,
  type PaymentPresenterTender,
  type PaymentScreenPresenter,
  type PaymentUiPhase,
} from "./payment-presenter";

import { INSTALLMENT_MINIMUM_TOTAL_CENTS } from "@/features/installments/installment-presenter";

import { receiptQrMatrix } from "@hb/pos-receipt-core/features/local-history/receipt-qr-matrix";
import type {
  LinklySafeOperatorKey,
} from "@/features/payments/runtime/linkly-operator-runtime";
import { calculateCashSettlement } from "@/features/sales/domain";
import {
  PosButton,
  type PosButtonSize,
  type PosButtonTone,
  type PosIconName,
} from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  usePosThemeVersion,
} from "@/ui/theme";

export const PAYMENT_MIN_TOUCH_TARGET = 44;

const SQUARE_AUTO_RECOVERY_INTERVAL_MS = 2_000;
const SQUARE_AUTO_RECOVERY_MAX_ATTEMPTS = 45;
const SQUARE_AUTO_RECOVERY_WINDOW_MS = 90_000;
const LINKLY_AUTO_RECOVERY_INTERVAL_MS = 2_000;
const LINKLY_AUTO_RECOVERY_MAX_ATTEMPTS = 89;
const LINKLY_AUTO_RECOVERY_WINDOW_MS = 180_000;
// Zeller 的扣款交给 SDK 后立刻返回等待中，结果靠轮询收。读到卡后被取消的要等 60 秒才能收尾，窗口留够 5 分钟。
const ZELLER_AUTO_RECOVERY_INTERVAL_MS = 2_000;
const ZELLER_AUTO_RECOVERY_MAX_ATTEMPTS = 149;
const ZELLER_AUTO_RECOVERY_WINDOW_MS = 300_000;

export type PaymentInstallmentModeIssue = "unavailable";

export type PaymentReceiptPrintOutcome = "completed" | "unknown" | "failed";

type PaymentReceiptPrintState = "idle" | "printing" | PaymentReceiptPrintOutcome;

export type PaymentInstallmentModeControl = Readonly<{
  enabled: boolean;
  locked: boolean;
  issue: PaymentInstallmentModeIssue | null;
  onToggle(enabled: boolean): void;
}>;

/** 合并成一个「信用卡」方块的刷卡通道，按优先级取第一个可用的。 */
type PaymentCardChannel = "linkly-cloud" | "zeller" | "square";

const PAYMENT_CARD_CHANNEL_PRIORITY = Object.freeze([
  "linkly-cloud",
  "zeller",
  "square",
] as const satisfies readonly PaymentCardChannel[]);

/** 快捷现金面额（澳元纸币），从大到小排。 */
const QUICK_CASH_NOTES = [100, 50, 20, 10, 5] as const;

type QuickCashNote = (typeof QUICK_CASH_NOTES)[number];

type QuickCashNoteColors = Readonly<{ light: string; deep: string; ink: string }>;

/** 纸币代表色：浅色底、深色描边 / 渐变、同色系深色文字；固定字面量，不随主题变化。 */
const QUICK_CASH_NOTE_COLORS: Readonly<Record<QuickCashNote, QuickCashNoteColors>> =
  Object.freeze({
    100: { light: "#CFEEDB", deep: "#2E9E6B", ink: "#15603E" },
    50: { light: "#FBEDB5", deep: "#E2B022", ink: "#6F4F05" },
    20: { light: "#F9D3C7", deep: "#D8573E", ink: "#8E2615" },
    10: { light: "#D3E4FA", deep: "#4E86D6", ink: "#1D4A86" },
    5: { light: "#F2D4EA", deep: "#C77DB5", ink: "#6E2860" },
  });

/** 快捷现金不可用时整排灰化。 */
const QUICK_CASH_DISABLED_COLORS: QuickCashNoteColors = Object.freeze({
  light: "#E5E8E6",
  deep: "#B5BDB9",
  ink: "#6F7A75",
});

/** 「大档」控件（数字键、付款方式）的最小高度。 */
const PAYMENT_KEY_ROW_MIN = 56;
/** 「小档」控件（返回、删除）的边长。 */
const PAYMENT_SMALL_TARGET = 56;

/** 宽屏网格尺寸：三栏共用的行高、行距与字号档位（对齐设计稿 13 寸 / 11 寸两档）。 */
export type PaymentGridMetrics = Readonly<{
  /** 13 寸档（窗口高度 ≥ 1000）。 */
  large: boolean;
  /** 金额显示区高度。 */
  amounts: number;
  /** 「中档」高度 M：当前支付金额、快捷现金、完成支付。 */
  medium: number;
  gap: number;
  /** 顶部段 = 金额显示区 + 当前支付金额行 + 快捷现金行（含两道行距）。 */
  top: number;
  /** 键盘行高：4 行等高，与左栏汇总、右栏完成区共用。 */
  keyRow: number;
  heroFont: number;
  subFont: number;
  dueFont: number;
}>;

/**
 * 由工作区高度倒推键盘行高：工作区 = 顶部段 + 4 个键盘行 + 4 道行距。
 * RN 没有 CSS grid，三栏各块都按这里算出的显式高度排，保证行线对齐。
 */
export function paymentGridMetrics(
  areaHeight: number,
  windowHeight: number,
  compact = false,
): PaymentGridMetrics {
  const large = !compact && windowHeight >= 1000;
  const amounts = large ? 168 : 124;
  const medium = large ? 88 : 72;
  const gap = large ? 10 : 8;
  const top = amounts + medium * 2 + gap * 2;
  const keyRow = compact
    ? 64
    : areaHeight > 0
      ? Math.max(
          PAYMENT_KEY_ROW_MIN,
          Math.floor((areaHeight - top - gap * 4) / 4),
        )
      : PAYMENT_KEY_ROW_MIN;
  return {
    large,
    amounts,
    medium,
    gap,
    top,
    keyRow,
    heroFont: large ? 58 : 46,
    subFont: large ? 42 : 34,
    dueFont: large ? 38 : 32,
  };
}

/** 取当前可用的刷卡通道：linkly-cloud → zeller → square。 */
function resolveCardChannel(
  state: PaymentPresenterState,
): PaymentCardChannel | null {
  return (
    PAYMENT_CARD_CHANNEL_PRIORITY.find((provider) =>
      state.providers.some(
        (entry) => entry.provider === provider && entry.available,
      ),
    ) ?? null
  );
}

/**
 * 现金应付：普通销售按五分取整（与 presenter 默认金额同一算法）；分期不取整。
 * 1 / 2 分尾款取整后为零时，沿用原始余额，保证仍是可提交的正数。
 */
function cashDueForState(state: PaymentPresenterState): number {
  const remaining = state.remaining.cents;
  if (remaining <= 0) return 0;
  if (state.checkout.flow !== "regular") return remaining;
  const due = calculateCashSettlement({
    actualAmount: state.remaining,
    cashTendered: { currency: "AUD", cents: 0 },
  }).cashDue.cents;
  return due > 0 ? due : remaining;
}

/** 暂存现金的找零（只用于界面实时显示；落账后以 presenter 的结算结果为准）。 */
function stagedCashChangeCents(
  state: PaymentPresenterState,
  stagedCents: number,
): number {
  if (state.remaining.cents <= 0) return 0;
  if (state.checkout.flow !== "regular") {
    return Math.max(0, stagedCents - state.remaining.cents);
  }
  return calculateCashSettlement({
    actualAmount: state.remaining,
    cashTendered: { currency: "AUD", cents: stagedCents },
  }).change.cents;
}

function centsToAmountText(cents: number): string {
  return (cents / 100).toFixed(2);
}

/** 成功页交易号二维码的目标边长（pt）。 */
const PAYMENT_QR_TARGET_SIZE = 140;

type PaymentScreenProps = Readonly<{
  installmentModeControl?: PaymentInstallmentModeControl;
  presenter: PaymentScreenPresenter;
  locale?: PaymentLocale;
  onBack?(): void;
  onOpenRecoveryCenter?(): void;
  onComplete?(orderGuid: string): void;
  onPrintReceipt?(orderGuid: string): Promise<PaymentReceiptPrintOutcome>;
  showStatusStrip?: boolean;
}>;

type Translate = (
  key: PaymentCopyKey,
  values?: Readonly<Record<string, string | number>>,
) => string;

export function PaymentScreen({
  installmentModeControl,
  presenter,
  locale: localeOverride,
  onBack,
  onOpenRecoveryCenter,
  onComplete,
  onPrintReceipt,
  showStatusStrip = true,
}: PaymentScreenProps) {
  usePosThemeVersion();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { i18n } = useTranslation();
  const locale =
    localeOverride ??
    resolvePaymentLocale(i18n.resolvedLanguage ?? i18n.language);
  const t: Translate = (key, values) => paymentText(locale, key, values);
  const { width, height: windowHeight } = useWindowDimensions();
  // 应用只支持横屏 iPad；宽度不足 900 时退回单列（分屏、测试夹具），整页可滚动。
  const compact = width < 900;
  const [
    fullInstallmentConfirmationOpen,
    setFullInstallmentConfirmationOpen,
  ] = useState(false);
  const [
    preparedCashCancellationOpen,
    setPreparedCashCancellationOpen,
  ] = useState(false);
  // 「当前支付金额」输入串：空 = 使用默认值（现金按五分取整的应付）。
  const [amountInput, setAmountInput] = useState("");
  // 暂存现金：只在界面层，点「完成支付」或发起刷卡前才落账。
  const [stagedCashCents, setStagedCashCents] = useState<number | null>(null);
  const [voucherEntryOpen, setVoucherEntryOpen] = useState(false);
  const [manualCardEntryOpen, setManualCardEntryOpen] = useState(false);
  // 宽屏工作区实测高度：用来倒推键盘行高，让三栏共用同一套行线。
  const [workspaceHeight, setWorkspaceHeight] = useState(0);
  const actionLockRef = useRef(false);
  // Lay-by 总额不足起订金额的页内提示（对齐 WPF #891）：按提示时的订单金额记住“已关闭”，金额变化后再次提示。
  const [dismissedMinimumNoticeCents, setDismissedMinimumNoticeCents] =
    useState<number | null>(null);
  const installmentMinimumNoticeOpen =
    state.fieldIssue === "installment-total-below-minimum" &&
    state.checkout.flow === "installment-create" &&
    dismissedMinimumNoticeCents !== state.total.cents;
  const latestState = useRef(state);
  latestState.current = state;

  const closePreparedCashCancellation = (): void => {
    if (state.busy) return;
    setPreparedCashCancellationOpen(false);
  };

  useEffect(() => {
    void presenter.initialize();
    return () => presenter.destroy();
  }, [presenter]);

  useEffect(() => {
    // 核对只适用于本次进入时的金额；切换方式、尾款或结账流程都必须重新确认。
    presenter.setManualCardConfirmed?.(false);
  }, [presenter, state.selectedMethod, state.remaining.cents, state.checkout.flow]);

  useEffect(() => {
    setFullInstallmentConfirmationOpen(false);
  }, [
    presenter,
    state.busy,
    state.checkout.fullInstallmentConfirmationRequired,
  ]);

  useEffect(() => {
    if (!canCancelPreparedCash(state)) {
      setPreparedCashCancellationOpen(false);
    }
  }, [presenter, state]);

  useEffect(() => {
    if (state.phase !== "success") return;
    try {
      presenter.recordSuccessRendered?.();
    } catch {
      // 性能指标是成功页旁路，采集失败不能改变已提交的支付结果。
    }
  }, [presenter, state.orderGuid, state.phase]);

  useEffect(() => {
    const attemptId = state.attemptId;
    const autoRecovery = autoRecoveryPolicy(state.provider);
    if (
      !attemptId ||
      !autoRecovery ||
      state.phase !== "pending" ||
      state.runtimeStatus !== "pending" ||
      !state.allowedActions.recover
    ) {
      return;
    }

    const now = Date.now();
    const createdAtMs = canonicalAttemptCreatedAtMs(
      state.attemptCreatedAtIso,
      now,
      autoRecovery.windowMs,
    );
    if (createdAtMs === null) return;
    const deadlineAtMs = createdAtMs + autoRecovery.windowMs;
    let disposed = false;
    let tickTimer: ReturnType<typeof setTimeout> | null = null;
    let activeController: AbortController | null = null;

    const stopAtDeadline = () => {
      if (tickTimer !== null) clearTimeout(tickTimer);
      tickTimer = null;
      activeController?.abort();
    };

    const scheduleNext = () => {
      if (disposed) return;
      const scheduleNow = Date.now();
      if (scheduleNow >= deadlineAtMs) {
        stopAtDeadline();
        return;
      }
      const nextTickNumber =
        Math.floor((scheduleNow - createdAtMs) / autoRecovery.intervalMs) +
        1;
      const nextTickAtMs =
        createdAtMs + nextTickNumber * autoRecovery.intervalMs;
      if (
        nextTickNumber > autoRecovery.maxAttempts ||
        nextTickAtMs >= deadlineAtMs
      ) {
        return;
      }
      tickTimer = setTimeout(() => {
        tickTimer = null;
        void recoverAtAnchoredTick(nextTickAtMs);
      }, Math.max(0, nextTickAtMs - scheduleNow));
    };

    const recoverAtAnchoredTick = async (scheduledAtMs: number) => {
      if (disposed) return;
      const tickNow = Date.now();
      // 事件循环若跨过了下一个锚点，本 tick 已错过；直接跳到未来锚点，不追赶。
      if (
        tickNow >= deadlineAtMs ||
        tickNow >= scheduledAtMs + autoRecovery.intervalMs
      ) {
        scheduleNext();
        return;
      }
      const current = latestState.current;
      if (
        current.attemptId !== attemptId ||
        current.attemptCreatedAtIso !== state.attemptCreatedAtIso ||
        current.provider !== state.provider ||
        current.phase !== "pending" ||
        current.runtimeStatus !== "pending" ||
        current.busy ||
        current.recoveryInFlight === true ||
        !current.allowedActions.recover
      ) {
        scheduleNext();
        return;
      }

      // 每个真正发起的后台恢复只拥有这个 controller；任何生命周期切换只 abort 它。
      const controller = new AbortController();
      activeController = controller;
      try {
        await presenter.recover({
          background: true,
          signal: controller.signal,
          deadlineAtMs,
        });
      } catch {
        // 后台恢复不能产生 unhandled rejection；公开状态由 presenter/runtime 稳定映射。
      } finally {
        if (activeController === controller) activeController = null;
        scheduleNext();
      }
    };

    const deadlineTimer = setTimeout(
      stopAtDeadline,
      Math.max(0, deadlineAtMs - now),
    );
    scheduleNext();
    return () => {
      disposed = true;
      clearTimeout(deadlineTimer);
      if (tickTimer !== null) clearTimeout(tickTimer);
      activeController?.abort();
    };
  }, [
    presenter,
    state.allowedActions.recover,
    state.attemptId,
    state.attemptCreatedAtIso,
    state.phase,
    state.provider,
    state.runtimeStatus,
  ]);

  const canLeave = canSafelyLeave(state);
  // 提交阶段已结束但 presenter 保留 phase 供恢复操作时，不再回显进行中提示。
  const actionPhaseIdle =
    !state.busy &&
    (state.phase === "submitting" || state.phase === "cash-confirming");
  const showStatusPanel =
    state.phase !== "success" && !actionPhaseIdle;
  const showEntry =
    state.phase !== "loading" &&
    state.phase !== "success" &&
    state.phase !== "submitting" &&
    state.phase !== "cash-confirming" &&
    (state.allowedActions.start || state.allowedActions.addCash);

  /* ---------- 本地收款状态（只在界面层，不落账） ---------- */
  const regularFlow = state.checkout.flow === "regular";
  const remainingCents = state.remaining.cents;
  const cashDueCents = cashDueForState(state);
  const typedCents = amountInput
    ? (parseAudInput(amountInput)?.cents ?? null)
    : null;
  const staged = stagedCashCents;
  // 暂存现金是否已够付：普通销售按五分取整的现金应付比较，分期按原始余额比较。
  const stagedCovers =
    staged !== null &&
    (regularFlow ? staged >= cashDueCents : staged >= remainingCents);
  const displayRemainingCents =
    staged === null
      ? remainingCents
      : stagedCovers
        ? 0
        : Math.max(0, remainingCents - staged);
  const changeCents =
    staged !== null
      ? stagedCashChangeCents(state, staged)
      : state.checkout.cash.changeCents;
  const defaultEntryCents =
    staged === null ? cashDueCents : Math.max(0, cashDueCents - staged);
  const paidCents = Math.max(0, state.total.cents - remainingCents);
  const collectedCents = paidCents + (staged ?? 0);

  // 刷卡等待：已有支付尝试且终端尚未给出结果（部分付款批准后 attemptId 仍会保留，不能算等待）。
  const awaitingTerminal =
    state.attemptId !== null &&
    (state.phase === "awaiting-terminal" || state.phase === "pending");
  // 刷卡等待 / busy / 提交中：付款方式、快捷现金、键盘、完成支付全部锁住。
  const entryLocked =
    state.busy ||
    state.recoveryInFlight === true ||
    awaitingTerminal ||
    !showEntry;
  const cardChannel = resolveCardChannel(state);
  const cashSelectable =
    !entryLocked && remainingCents > 0 && canSelectPaymentMethod(state, "cash");
  // 已暂存现金：普通销售要先落账这笔现金再发起卡，现金已够付时卡没有余额可收；
  // 分期每单只允许一笔付款，暂存现金后不能再换卡或手动刷卡。
  const stagedBlocksCard = staged !== null && (stagedCovers || !regularFlow);
  const cardSelectable =
    !entryLocked &&
    cardChannel !== null &&
    !stagedBlocksCard &&
    canSelectPaymentMethod(state, cardChannel);
  const manualCardSelectable =
    !entryLocked &&
    !stagedBlocksCard &&
    canSelectPaymentMethod(state, "manual-card");
  // 收款顺序对齐 WPF：购物券 → 现金 → 刷卡；已有现金（落账或暂存）时不能再加购物券。
  const hasCash =
    staged !== null || state.tenders.some((tender) => tender.method === "cash");
  const voucherSelectable =
    !entryLocked && !hasCash && canSelectPaymentMethod(state, "voucher");
  const quickCashEnabled = cashSelectable && !stagedCovers;
  const showConfirmation =
    Boolean(presenter.confirm) &&
    state.checkout.canConfirm &&
    !(
      state.phase === "recovery-required" &&
      state.checkout.cashRepaymentStatus === "ready"
    );
  const finishEnabled =
    cashSelectable &&
    staged !== null &&
    (regularFlow ? staged >= cashDueCents : staged > 0);
  const preparedCashCancellation = canCancelPreparedCash(state);
  // 「取消」只在刷卡等待时出现；未动过的支付草稿（没有任何付款）也允许取消，否则无法离开。
  const showCancel =
    state.allowedActions.cancel &&
    !preparedCashCancellation &&
    (state.attemptId !== null ||
      (state.phase === "draft-prepared" && state.tenders.length === 0));
  const showPreparedCashCancellationInFooter =
    preparedCashCancellation && state.phase !== "recovery-required";
  // 每屏只有一个主按钮：有待确认的收款时是「确认」，能录入时是「完成支付」，其余轮到恢复类操作。
  const primaryOwner: "confirm" | "finish" | "recovery" = showConfirmation
    ? "confirm"
    : showEntry
      ? "finish"
      : "recovery";

  useEffect(() => {
    // 换订单、换结账流程或订单金额变化：本地暂存现金和输入一律作废，不能带到另一笔收款。
    setStagedCashCents(null);
    setAmountInput("");
  }, [presenter, state.checkout.flow, state.total.cents]);

  useEffect(() => {
    if (stagedCashCents === null || state.busy) return;
    // 现金已不能再收（订单完成、已落账现金、权限或阶段变化）时清掉暂存，避免完成支付落一笔过期现金。
    if (
      state.phase === "success" ||
      !canSelectPaymentMethod(state, "cash")
    ) {
      setStagedCashCents(null);
    }
  }, [state, stagedCashCents]);

  useEffect(() => {
    if (
      voucherEntryOpen &&
      !state.busy &&
      (state.phase === "success" || state.selectedMethod !== "voucher")
    ) {
      setVoucherEntryOpen(false);
    }
    if (
      manualCardEntryOpen &&
      !state.busy &&
      (state.phase === "success" || state.selectedMethod !== "manual-card")
    ) {
      setManualCardEntryOpen(false);
    }
  }, [manualCardEntryOpen, state.busy, state.phase, state.selectedMethod, voucherEntryOpen]);

  /** 同一时刻只允许一条界面动作链（例如「先落账现金再发起刷卡」），防止连点。 */
  const runAction = (action: () => Promise<void>): void => {
    if (actionLockRef.current) return;
    actionLockRef.current = true;
    void action().finally(() => {
      actionLockRef.current = false;
    });
  };

  /** 把暂存现金落账：selectMethod → setAmountText → submitSelected，成功后才清暂存。 */
  const commitStagedCash = async (): Promise<boolean> => {
    if (stagedCashCents === null) return true;
    if (!presenter.selectMethod("cash")) return false;
    presenter.setAmountText(centsToAmountText(stagedCashCents));
    const submitted = await presenter.submitSelected();
    if (submitted) {
      setStagedCashCents(null);
      setAmountInput("");
    }
    return submitted;
  };

  const handleStageCash = (): void => {
    if (!cashSelectable) return;
    if (amountInput && typedCents === null) return;
    // 点「现金」= 暂存一笔现金：金额取输入值，空则取现金应付；替换已有暂存。
    setStagedCashCents(typedCents ?? cashDueCents);
    setAmountInput("");
  };

  const handleQuickCash = (note: QuickCashNote): void => {
    if (!quickCashEnabled) return;
    // 快捷纸币累加到同一笔暂存现金（对齐 WPF #822）。
    setStagedCashCents((current) => (current ?? 0) + note * 100);
    setAmountInput("");
  };

  const handleFinish = (): void => {
    if (!finishEnabled) return;
    runAction(async () => {
      await commitStagedCash();
    });
  };

  const handleCard = (): void => {
    if (!cardSelectable || !cardChannel) return;
    runAction(async () => {
      // 先把不足额的暂存现金落成部分付款，再用卡收剩余。
      if (!(await commitStagedCash())) return;
      const latest = presenter.getState();
      if (latest.phase === "success" || latest.remaining.cents <= 0) return;
      if (!presenter.selectMethod(cardChannel)) return;
      // 普通销售的卡必须是足额的最后一笔；分期首付 / 续付可以按输入金额部分收。
      const amountCents = regularFlow
        ? latest.remaining.cents
        : (typedCents ?? latest.remaining.cents);
      presenter.setAmountText(centsToAmountText(amountCents));
      if (await presenter.submitSelected()) setAmountInput("");
    });
  };

  const handleManualCard = (): void => {
    if (!manualCardSelectable) return;
    runAction(async () => {
      if (!(await commitStagedCash())) return;
      const latest = presenter.getState();
      if (latest.phase === "success" || latest.remaining.cents <= 0) return;
      if (presenter.selectMethod("manual-card")) setManualCardEntryOpen(true);
    });
  };

  const submitManualCard = (): void => {
    runAction(async () => {
      if (!regularFlow) {
        // 分期续付的手动刷卡可以部分收：金额取输入值，空则取剩余。
        presenter.setAmountText(
          centsToAmountText(typedCents ?? presenter.getState().remaining.cents),
        );
      }
      if (await presenter.submitSelected()) {
        setManualCardEntryOpen(false);
        setAmountInput("");
      }
    });
  };

  const closeManualCardEntry = (): void => {
    if (state.busy) return;
    presenter.setManualCardConfirmed?.(false);
    setManualCardEntryOpen(false);
  };

  const handleVoucher = (): void => {
    if (!voucherSelectable) return;
    if (presenter.selectMethod("voucher")) setVoucherEntryOpen(true);
  };

  const voucherAmountCents = typedCents ?? remainingCents;
  const submitVoucher = (): void => {
    runAction(async () => {
      presenter.setAmountText(centsToAmountText(voucherAmountCents));
      if (await presenter.submitSelected()) {
        setVoucherEntryOpen(false);
        setAmountInput("");
      }
    });
  };

  const closeVoucherEntry = (): void => {
    if (state.busy) return;
    // 关闭即丢弃已录入的券码，券码不留在 presenter 私有字段里。
    presenter.setVoucherCode("");
    setVoucherEntryOpen(false);
  };

  const handleConfirm = (): void => {
    const customer = state.checkout.installmentCustomer;
    const customerComplete =
      customer !== null &&
      customer.name.trim().length > 0 &&
      customer.phone.trim().length > 0;
    if (
      state.checkout.fullInstallmentConfirmationRequired &&
      customerComplete
    ) {
      setFullInstallmentConfirmationOpen(true);
      return;
    }
    void presenter.confirm?.();
  };

  const metrics = paymentGridMetrics(
    compact ? 0 : workspaceHeight,
    windowHeight,
    compact,
  );
  const wide = !compact;

  const orderCard = (
    <PaymentOrderCard
      canLeave={canLeave}
      compact={compact}
      height={wide ? metrics.top + 3 * (metrics.keyRow + metrics.gap) : null}
      locale={locale}
      onBack={onBack}
      onOpenRecoveryCenter={onOpenRecoveryCenter}
      presenter={presenter}
      showLeaveHint={state.tenders.length > 0 || staged !== null}
      state={state}
      t={t}
    />
  );
  const orderSummary = (
    <PaymentOrderSummary
      collectedCents={collectedCents}
      height={wide ? metrics.keyRow : null}
      locale={locale}
      metrics={metrics}
      remainingCents={displayRemainingCents}
      t={t}
      totalCents={state.total.cents}
    />
  );
  const amountDisplay = (
    <PaymentAmountDisplay
      changeCents={changeCents}
      locale={locale}
      metrics={metrics}
      remainingCents={displayRemainingCents}
      t={t}
    />
  );
  const amountInputRow = (
    <PaymentAmountInputRow
      defaultCents={defaultEntryCents}
      disabled={entryLocked}
      inputText={amountInput}
      locale={locale}
      metrics={metrics}
      onBackspace={() =>
        setAmountInput((current) => nextKeypadAmount(current, "backspace"))
      }
      t={t}
    />
  );
  const quickCashRow = (
    <PaymentQuickCashRow
      enabled={quickCashEnabled}
      metrics={metrics}
      onPress={handleQuickCash}
    />
  );
  const keypad = (
    <PaymentKeypad
      disabled={entryLocked}
      metrics={metrics}
      onClear={() => setAmountInput("")}
      onKey={(key) =>
        setAmountInput((current) => nextKeypadAmount(current, key))
      }
      t={t}
    />
  );
  const methodsCard = (
    <PaymentMethodsCard
      cardChannel={cardChannel}
      cardSelectable={cardSelectable}
      cashSelectable={cashSelectable}
      compact={compact}
      height={wide ? metrics.top : null}
      installmentModeControl={installmentModeControl}
      manualCardSelectable={manualCardSelectable}
      metrics={metrics}
      onCard={handleCard}
      onCash={handleStageCash}
      onManualCard={handleManualCard}
      onVoucher={handleVoucher}
      state={state}
      t={t}
      voucherSelectable={voucherSelectable}
    />
  );
  const tendersCard = (
    <PaymentTendersCard
      compact={compact}
      height={wide ? 3 * metrics.keyRow + 2 * metrics.gap : null}
      installmentModeControl={installmentModeControl}
      locale={locale}
      onCancelPreparedCash={() => setPreparedCashCancellationOpen(true)}
      onRemoveStagedCash={() => setStagedCashCents(null)}
      presenter={presenter}
      recoveryPrimary={primaryOwner === "recovery"}
      showStatusPanel={showStatusPanel}
      stagedCashCents={staged}
      state={state}
      t={t}
    />
  );
  const finishArea = (
    <PaymentFinishArea
      collectedCents={collectedCents}
      finishEnabled={finishEnabled}
      height={wide ? metrics.keyRow : null}
      locale={locale}
      metrics={metrics}
      onCancel={() => {
        void presenter.cancel();
      }}
      onCancelPreparedCash={() => setPreparedCashCancellationOpen(true)}
      onConfirm={handleConfirm}
      onFinish={handleFinish}
      showCancel={showCancel}
      showConfirmation={showConfirmation}
      showPreparedCashCancellation={showPreparedCashCancellationInFooter}
      state={state}
      t={t}
      waiting={awaitingTerminal}
    />
  );

  return (
    <PosScreenFrame
      rail="none"
      testID="payment-screen"
      topBar={showStatusStrip ? undefined : false}
    >
      <PosKeyboardAwareScrollView
        contentContainerStyle={[
          styles.scrollContent,
          !compact && styles.scrollContentBounded,
        ]}
        scrollEnabled={compact}
        style={styles.contentScroll}
        testID="payment-content-scroll"
      >
        {state.runtimeErrorCode ? (
          <View
            accessibilityRole="alert"
            style={styles.errorBanner}
            testID="payment-runtime-error"
          >
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.red}
              name="alert-circle-outline"
              size={26}
            />
            <Text style={styles.errorText}>
              {t(paymentRuntimeErrorCopyKey(state.runtimeErrorCode))}
            </Text>
            {!state.busy ? (
              <ActionButton
                label={t("action.dismiss")}
                onPress={() => presenter.dismissError()}
                size="small"
                sound="navigate"
                testID="payment-error-dismiss"
                tone="danger"
              />
            ) : null}
          </View>
        ) : null}

        {state.phase === "success" && state.orderGuid ? (
          <PaymentSuccessLayout
            compact={compact}
            locale={locale}
            onComplete={onComplete}
            onPrintReceipt={onPrintReceipt}
            orderGuid={state.orderGuid}
            presenter={presenter}
            state={state}
            t={t}
          />
        ) : compact ? (
          // 窄屏（分屏、测试夹具）：同一套组件按单列堆叠，整页滚动，不要求对齐行线。
          <View
            style={[styles.workspace, styles.workspaceCompact]}
            testID="payment-workspace"
          >
            {amountDisplay}
            {amountInputRow}
            {quickCashRow}
            {keypad}
            {methodsCard}
            {tendersCard}
            {finishArea}
            {orderCard}
            {orderSummary}
          </View>
        ) : (
          // 宽屏三栏 28 : 42 : 30，三栏共用同一套横向行线：
          // 顶部段（金额显示 + 当前支付金额 + 快捷现金）之下是 4 个等高的键盘行。
          <View
            onLayout={(event) => {
              const next = Math.round(event.nativeEvent.layout.height);
              setWorkspaceHeight((current) => (current === next ? current : next));
            }}
            style={[styles.workspace, { gap: metrics.gap }]}
            testID="payment-workspace"
          >
            <View
              style={[styles.workspaceColumn, styles.workspaceLeft, { gap: metrics.gap }]}
              testID="payment-column-left"
            >
              {orderCard}
              {orderSummary}
            </View>
            <View
              style={[styles.workspaceColumn, styles.workspaceMiddle, { gap: metrics.gap }]}
              testID="payment-column-middle"
            >
              {amountDisplay}
              {amountInputRow}
              {quickCashRow}
              {keypad}
            </View>
            <View
              style={[styles.workspaceColumn, styles.workspaceRight, { gap: metrics.gap }]}
              testID="payment-column-right"
            >
              {methodsCard}
              {tendersCard}
              {finishArea}
            </View>
          </View>
        )}
      </PosKeyboardAwareScrollView>

      <Modal
        animationType="fade"
        onRequestClose={() =>
          setFullInstallmentConfirmationOpen(false)
        }
        // iPad 应用仅支持横屏；默认竖屏弹窗会被 UIKit 直接终止。
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="payment-full-installment-native-modal"
        transparent
        visible={fullInstallmentConfirmationOpen}
      >
        <View
          accessibilityViewIsModal
          style={styles.confirmationBackdrop}
          testID="payment-full-installment-confirmation"
        >
          <PosPressable
            accessible={false}
            onPress={() => setFullInstallmentConfirmationOpen(false)}
            sound="navigate"
            style={styles.backdropDismissArea}
            testID="payment-full-installment-backdrop"
          />
          <View style={styles.confirmationCard}>
            <Text style={styles.confirmationTitle}>
              {t("installment.fullPayment.title")}
            </Text>
            <Text style={styles.confirmationBody}>
              {t("installment.fullPayment.body")}
            </Text>
            <View style={styles.confirmationActions}>
              <ActionButton
                label={t("installment.fullPayment.cancel")}
                onPress={() =>
                  setFullInstallmentConfirmationOpen(false)
                }
                style={styles.confirmationAction}
                sound="navigate"
                testID="payment-full-installment-cancel"
                tone="secondary"
              />
              <ActionButton
                label={t("installment.fullPayment.confirm")}
                onPress={() => {
                  setFullInstallmentConfirmationOpen(false);
                  void presenter.confirm?.({
                    acknowledgeFullInstallmentPayment: true,
                  });
                }}
                style={styles.confirmationAction}
                sound="danger"
                testID="payment-full-installment-confirm"
                tone="primary"
              />
            </View>
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={() => setDismissedMinimumNoticeCents(state.total.cents)}
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="payment-installment-minimum-native-modal"
        transparent
        visible={installmentMinimumNoticeOpen}
      >
        <View
          accessibilityViewIsModal
          style={styles.confirmationBackdrop}
          testID="payment-installment-minimum-notice"
        >
          <PosPressable
            accessible={false}
            onPress={() => setDismissedMinimumNoticeCents(state.total.cents)}
            sound="navigate"
            style={styles.backdropDismissArea}
            testID="payment-installment-minimum-backdrop"
          />
          <View style={styles.confirmationCard}>
            <Text style={styles.confirmationTitle}>
              {t("installment.minimumNotice.title")}
            </Text>
            <Text style={styles.confirmationBody}>
              {t("installment.minimumNotice.body", {
                minimum: formatAud(INSTALLMENT_MINIMUM_TOTAL_CENTS, locale),
                current: formatAud(state.total.cents, locale),
              })}
            </Text>
            <View style={styles.confirmationActions}>
              <ActionButton
                label={t("installment.minimumNotice.dismiss")}
                onPress={() => setDismissedMinimumNoticeCents(state.total.cents)}
                style={styles.confirmationAction}
                sound="navigate"
                testID="payment-installment-minimum-dismiss"
                tone="secondary"
              />
              {installmentModeControl && !installmentModeControl.locked ? (
                <ActionButton
                  label={t("installment.minimumNotice.regular")}
                  onPress={() => {
                    setDismissedMinimumNoticeCents(state.total.cents);
                    installmentModeControl.onToggle(false);
                  }}
                  style={styles.confirmationAction}
                  sound="navigate"
                  testID="payment-installment-minimum-regular"
                  tone="primary"
                />
              ) : null}
            </View>
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={closePreparedCashCancellation}
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="payment-cancel-prepared-cash-native-modal"
        transparent
        visible={preparedCashCancellationOpen}
      >
        <View
          accessibilityViewIsModal
          style={styles.confirmationBackdrop}
          testID="payment-cancel-prepared-cash-confirmation"
        >
          <PosPressable
            accessible={false}
            onPress={closePreparedCashCancellation}
            sound="navigate"
            style={styles.backdropDismissArea}
            testID="payment-cancel-prepared-cash-backdrop"
          />
          <View style={styles.confirmationCard}>
            <Text style={styles.confirmationTitle}>
              {t(cancelPreparedModalKey(state, "title"))}
            </Text>
            <Text style={styles.confirmationBody}>
              {t(cancelPreparedModalKey(state, "body"))}
            </Text>
            <View style={styles.confirmationActions}>
              <ActionButton
                disabled={state.busy}
                label={t(cancelPreparedModalKey(state, "dismiss"))}
                onPress={closePreparedCashCancellation}
                style={styles.confirmationAction}
                sound="navigate"
                testID="payment-cancel-prepared-cash-dismiss"
                tone="secondary"
              />
              <ActionButton
                disabled={state.busy}
                label={t(cancelPreparedModalKey(state, "confirm"))}
                onPress={() => {
                  setPreparedCashCancellationOpen(false);
                  void presenter.cancel();
                }}
                style={styles.confirmationAction}
                sound="danger"
                testID="payment-cancel-prepared-cash-confirm"
                tone="danger"
              />
            </View>
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={closeVoucherEntry}
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="payment-voucher-native-modal"
        transparent
        visible={voucherEntryOpen}
      >
        <View
          accessibilityViewIsModal
          style={styles.confirmationBackdrop}
          testID="payment-voucher-entry"
        >
          <PosPressable
            accessible={false}
            onPress={closeVoucherEntry}
            sound="navigate"
            style={styles.backdropDismissArea}
            testID="payment-voucher-backdrop"
          />
          <PosKeyboardAwareScrollView
            contentContainerStyle={styles.modalScrollContent}
            style={styles.modalScroll}
            testID="payment-voucher-scroll"
          >
            <View style={styles.confirmationCard}>
              <Text style={styles.confirmationTitle}>
                {t("voucher.title")}
              </Text>
              <View
                key={state.sensitiveInputRevision}
                style={styles.voucherInputGroup}
              >
                <Text style={styles.inputLabel}>{t("voucher.label")}</Text>
                <PosKeyboardAwareTextInput
                  accessibilityLabel={t("voucher.label")}
                  autoCapitalize="characters"
                  autoCorrect={false}
                  autoFocus
                  editable={!state.busy}
                  onChangeText={(value) => presenter.setVoucherCode(value)}
                  placeholder={t("voucher.placeholder")}
                  placeholderTextColor={posColors.placeholder}
                  secureTextEntry
                  selectionColor={posColors.blue}
                  style={styles.voucherInput}
                  testID="payment-voucher-code"
                />
                {state.voucherCaptured ? (
                  <Text
                    style={styles.secureCapture}
                    testID="payment-voucher-captured"
                  >
                    {t("voucher.captured")}
                  </Text>
                ) : null}
              </View>
              <SummaryAmount
                label={t("voucher.amount")}
                locale={locale}
                strong
                testID="payment-voucher-amount"
                value={voucherAmountCents}
              />
              <Text style={styles.inputHint}>{t("voucher.amountHint")}</Text>
              {state.fieldIssue ? (
                <Text
                  accessibilityRole="alert"
                  style={styles.fieldError}
                  testID="payment-voucher-field-error"
                >
                  {t(paymentFieldIssueCopyKey(state.fieldIssue))}
                </Text>
              ) : null}
              <View style={styles.confirmationActions}>
                <ActionButton
                  disabled={state.busy}
                  label={t("modal.cancel")}
                  onPress={closeVoucherEntry}
                  style={styles.confirmationAction}
                  sound="navigate"
                  testID="payment-voucher-cancel"
                  tone="secondary"
                />
                <ActionButton
                  disabled={state.busy || !state.voucherCaptured}
                  label={`${t("voucher.submit")} · ${formatAud(voucherAmountCents, locale)}`}
                  onPress={submitVoucher}
                  style={styles.confirmationAction}
                  testID="payment-voucher-submit"
                  tone="primary"
                />
              </View>
            </View>
          </PosKeyboardAwareScrollView>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={closeManualCardEntry}
        supportedOrientations={["landscape-left", "landscape-right"]}
        testID="payment-manual-card-native-modal"
        transparent
        visible={manualCardEntryOpen}
      >
        <View
          accessibilityViewIsModal
          style={styles.confirmationBackdrop}
          testID="payment-manual-card-entry"
        >
          <PosPressable
            accessible={false}
            onPress={closeManualCardEntry}
            sound="navigate"
            style={styles.backdropDismissArea}
            testID="payment-manual-card-backdrop"
          />
          <View
            style={[styles.confirmationCard, styles.manualCardConfirmation]}
            testID="payment-manual-card-dialog"
          >
            <Text style={styles.confirmationTitle}>
              {t("method.manual-card")}
            </Text>
            <SummaryAmount
              label={t("amount.label")}
              locale={locale}
              strong
              testID="payment-manual-card-amount"
              value={
                regularFlow
                  ? remainingCents
                  : (typedCents ?? remainingCents)
              }
            />
            <Text style={styles.manualCardInstructions}>
              {t(regularFlow ? "manualCard.instructions" : "installmentManualCard.prepare")}
            </Text>
            {regularFlow ? (
              // 普通销售整笔收：必须勾选「已确认刷卡机付款成功」才能记录（沿用 presenter 的人工核对语义）。
              <PosPressable
                accessibilityLabel={t("manualCard.checked")}
                accessibilityRole="checkbox"
                accessibilityState={{
                  checked: state.manualCardConfirmed === true,
                  disabled: state.busy,
                }}
                disabled={state.busy}
                onPress={() =>
                  presenter.setManualCardConfirmed?.(
                    state.manualCardConfirmed !== true,
                  )
                }
                sound="key"
                style={({ pressed }) => [
                  styles.manualCardCheckRow,
                  state.manualCardConfirmed === true &&
                    styles.manualCardCheckRowChecked,
                  pressed && styles.pressed,
                ]}
                testID="payment-manual-card-check"
              >
                <MaterialCommunityIcons
                  accessible={false}
                  color={
                    state.manualCardConfirmed === true
                      ? posColors.green
                      : posColors.mutedInk
                  }
                  name={
                    state.manualCardConfirmed === true
                      ? "checkbox-marked"
                      : "checkbox-blank-outline"
                  }
                  size={34}
                />
                <Text style={styles.manualCardCheckLabel}>
                  {t("manualCard.checked")}
                </Text>
              </PosPressable>
            ) : null}
            {regularFlow ? (
              <Text style={styles.inputHint}>{t("manualCard.uncertain")}</Text>
            ) : null}
            {state.fieldIssue ? (
              <Text
                accessibilityRole="alert"
                style={styles.fieldError}
                testID="payment-manual-card-field-error"
              >
                {t(paymentFieldIssueCopyKey(state.fieldIssue))}
              </Text>
            ) : null}
            <View style={styles.confirmationActions}>
              <ActionButton
                disabled={state.busy}
                label={t("modal.cancel")}
                onPress={closeManualCardEntry}
                style={styles.confirmationAction}
                sound="navigate"
                testID="payment-manual-card-cancel"
                tone="secondary"
              />
              <ActionButton
                disabled={
                  state.busy ||
                  (regularFlow && state.manualCardConfirmed !== true)
                }
                label={
                  regularFlow
                    ? t("manualCard.confirm")
                    : t("action.prepareManualCardRepayment")
                }
                onPress={submitManualCard}
                style={styles.confirmationAction}
                testID="payment-manual-card-submit"
                tone="primary"
              />
            </View>
          </View>
        </View>
      </Modal>
    </PosScreenFrame>
  );
}

function PaymentSuccessLayout({
  compact,
  locale,
  onComplete,
  onPrintReceipt,
  orderGuid,
  presenter,
  state,
  t,
}: Readonly<{
  compact: boolean;
  locale: PaymentLocale;
  onComplete: ((orderGuid: string) => void) | undefined;
  onPrintReceipt:
    | ((orderGuid: string) => Promise<PaymentReceiptPrintOutcome>)
    | undefined;
  orderGuid: string;
  presenter: PaymentScreenPresenter;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const paidCents = Math.max(
    0,
    state.total.cents - state.remaining.cents,
  );
  const cash = state.checkout.cash;
  const [printState, setPrintState] = useState<PaymentReceiptPrintState>("idle");
  const printInFlightRef = useRef(false);
  const printGenerationRef = useRef(0);

  useEffect(() => {
    printGenerationRef.current += 1;
    printInFlightRef.current = false;
    setPrintState("idle");
    return () => {
      // 订单切换或卸载时使在途结果失效，禁止迟到 Promise 写回旧成功页。
      printGenerationRef.current += 1;
      printInFlightRef.current = false;
    };
  }, [orderGuid]);

  const handlePrintReceipt = async () => {
    if (!onPrintReceipt || printInFlightRef.current) return;
    printInFlightRef.current = true;
    const generation = printGenerationRef.current;
    setPrintState("printing");
    try {
      const result = await onPrintReceipt(orderGuid);
      if (generation === printGenerationRef.current) {
        setPrintState(result);
      }
    } catch {
      if (generation === printGenerationRef.current) {
        setPrintState("failed");
      }
    } finally {
      if (generation === printGenerationRef.current) {
        printInFlightRef.current = false;
      }
    }
  };

  return (
    <View
      style={[
        styles.successLayout,
        compact && styles.successLayoutCompact,
      ]}
      testID="payment-success-layout"
    >
      {/* 左栏：结果条、找零、交易号与终端操作；内容多时自己滚动，右栏按钮不跟着走。 */}
      <ScrollView
        contentContainerStyle={styles.successMainContent}
        nestedScrollEnabled
        scrollEnabled={!compact}
        style={[
          styles.successMain,
          compact && styles.successMainCompact,
        ]}
        testID="payment-success-summary"
      >
        <View
          accessibilityRole="summary"
          style={styles.successHero}
          testID="payment-status-success"
        >
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.green}
            name="check-circle"
            size={76}
          />
          <View style={styles.successHeroCopy}>
            <Text style={styles.successTitle}>
              {t("status.success.title")}
            </Text>
            <Text style={styles.successHint}>
              {t("status.success.hint")}
            </Text>
          </View>
          <View style={styles.successTotal}>
            <Text style={styles.successLabel}>
              {t("success.paidTotal")}
            </Text>
            <Text
              adjustsFontSizeToFit
              numberOfLines={1}
              style={styles.successTotalAmount}
            >
              {formatAud(paidCents, locale)}
            </Text>
          </View>
        </View>

        {cash.tenderedCents > 0 ? (
          <View
            style={styles.successSettlement}
            testID="payment-success-settlement"
          >
            <View style={styles.successCashTendered}>
              <Text style={styles.successCashTenderedLabel}>
                {t("success.cashTendered")}
              </Text>
              <Text
                adjustsFontSizeToFit
                numberOfLines={1}
                style={styles.successCashTenderedAmount}
              >
                {formatAud(cash.tenderedCents, locale)}
              </Text>
            </View>
            <View style={styles.successSettlementRule} />
            <View style={styles.successChange}>
              <Text style={styles.successChangeLabel}>
                {t("success.changeDue")}
              </Text>
              {/* 找零是这一屏最大的数字，收银员低头找钱时不用找。 */}
              <Text
                adjustsFontSizeToFit
                numberOfLines={1}
                style={styles.successChangeAmount}
                testID="payment-success-change"
              >
                {formatAud(cash.changeCents, locale)}
              </Text>
            </View>
          </View>
        ) : null}

        <View style={styles.successTransaction}>
          <PaymentTransactionQrCode
            label={t("success.orderReference")}
            value={orderGuid}
          />
          <View style={styles.successTransactionCopy}>
            <Text style={styles.successLabel}>
              {t("success.orderReference")}
            </Text>
            <Text
              selectable
              style={styles.successOrderReference}
            >
              {orderGuid}
            </Text>
            <View style={styles.successSync}>
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.green}
                name="cloud-check-outline"
                size={22}
              />
              <Text style={styles.successSyncText}>
                {t("success.syncQueued")}
              </Text>
            </View>
          </View>
        </View>

        <LinklyControls presenter={presenter} state={state} t={t} />
      </ScrollView>

      <View
        style={[
          styles.successReceiptColumn,
          compact && styles.successReceiptColumnCompact,
        ]}
      >
        <View
          style={styles.successReceipt}
          testID="payment-success-receipt-preview"
        >
          <View style={styles.successReceiptHeader}>
            <Text style={styles.successReceiptTitle}>
              {t("success.receiptPreview")}
            </Text>
            <Text
              numberOfLines={1}
              selectable
              style={styles.successReceiptOrder}
            >
              {orderGuid}
            </Text>
          </View>

          <ScrollView
            nestedScrollEnabled
            scrollEnabled={!compact}
            style={styles.successReceiptLines}
          >
            {state.checkout.lines.length ? (
              state.checkout.lines.map((line) => (
                <View key={line.lineKey} style={styles.successReceiptLine}>
                  <View style={styles.successReceiptLineCopy}>
                    <Text
                      numberOfLines={2}
                      style={styles.successReceiptLineName}
                    >
                      {line.displayName}
                    </Text>
                    <Text style={styles.successReceiptLineQuantity}>
                      × {line.quantity}
                    </Text>
                  </View>
                  <Text style={styles.successReceiptLineAmount}>
                    {formatAud(line.actualAmountCents, locale)}
                  </Text>
                </View>
              ))
            ) : (
              <Text style={styles.successReceiptEmpty}>
                {t("success.noItems")}
              </Text>
            )}
          </ScrollView>

          <View style={styles.successReceiptTotals}>
            {state.tenders.map((tender) => (
              <View
                key={tender.tenderGuid}
                style={styles.successReceiptTender}
              >
                <Text style={styles.successReceiptTenderLabel}>
                  {t(
                    tender.method === "card"
                      ? "method.card"
                      : paymentMethodCopyKey(tender.method),
                  )}
                </Text>
                <Text style={styles.successReceiptTenderAmount}>
                  {formatAud(tender.amount.cents, locale)}
                </Text>
              </View>
            ))}
            <View style={styles.successReceiptTender}>
              <Text style={styles.successReceiptTotalLabel}>
                {t("summary.total")}
              </Text>
              <Text style={styles.successReceiptTotalAmount}>
                {formatAud(state.total.cents, locale)}
              </Text>
            </View>
          </View>
        </View>

        {printState !== "idle" ? (
          <Text
            accessibilityLiveRegion="polite"
            style={[
              styles.successPrintStatus,
              printState === "printing" && styles.successPrintStatusPrinting,
              printState === "completed" && styles.successPrintStatusCompleted,
              printState === "unknown" && styles.successPrintStatusUnknown,
              printState === "failed" && styles.successPrintStatusFailed,
            ]}
            testID="payment-success-print-status"
          >
            {t(`success.print.${printState}`)}
          </Text>
        ) : null}
        <ActionButton
          disabled={!onPrintReceipt || printState === "printing"}
          icon="printer-outline"
          label={t("action.printReceipt")}
          onPress={() => {
            void handlePrintReceipt();
          }}
          size="large"
          testID="payment-success-print"
          tone="secondary"
        />
        {onComplete ? (
          // 成功页唯一的主按钮，位置与收银页的收款按钮相同。
          <ActionButton
            icon="arrow-right-circle"
            label={t("action.newSale")}
            onPress={() => onComplete(orderGuid)}
            size="primary"
            sound="navigate"
            testID="payment-complete"
            tone="primary"
          />
        ) : null}
      </View>
    </View>
  );
}

function PaymentTransactionQrCode({
  label,
  value,
}: Readonly<{
  label: string;
  value: string;
}>) {
  usePosThemeVersion();
  const matrix = useMemo(() => receiptQrMatrix(value), [value]);
  // 模块边长取 0.5pt 的整数倍，在 Retina 屏幕对应整数像素，保证扫码清晰。
  const moduleSize = Math.max(
    2,
    Math.floor((PAYMENT_QR_TARGET_SIZE / (matrix.length + 8)) * 2) / 2,
  );

  return (
    <View
      accessibilityLabel={`${label} QR ${value}`}
      accessibilityRole="image"
      style={styles.successTransactionQr}
      testID="payment-success-transaction-qr"
    >
      <View
        accessibilityElementsHidden
        importantForAccessibility="no-hide-descendants"
        style={[
          styles.successTransactionQrQuietZone,
          { padding: moduleSize * 4 },
        ]}
        testID="payment-success-transaction-qr-matrix"
      >
        {matrix.map((row, rowIndex) => (
          <View
            key={`row-${rowIndex}`}
            style={styles.successTransactionQrRow}
            testID={`payment-success-transaction-qr-row-${rowIndex}`}
          >
            {row.map((dark, columnIndex) => (
              <View
                key={`${rowIndex}-${columnIndex}`}
                style={[
                  styles.successTransactionQrCell,
                  { height: moduleSize, width: moduleSize },
                  dark && styles.successTransactionQrCellDark,
                ]}
              />
            ))}
          </View>
        ))}
      </View>
    </View>
  );
}

/** 左栏上部：返回收银 + 件数 + 商品明细（分期时含顾客资料）；已添加支付时提示先删除支付。 */
function PaymentOrderCard({
  canLeave,
  compact,
  height,
  locale,
  onBack,
  onOpenRecoveryCenter,
  presenter,
  showLeaveHint,
  state,
  t,
}: Readonly<{
  canLeave: boolean;
  compact: boolean;
  height: number | null;
  locale: PaymentLocale;
  onBack: (() => void) | undefined;
  onOpenRecoveryCenter: (() => void) | undefined;
  presenter: PaymentScreenPresenter;
  showLeaveHint: boolean;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const customer = state.checkout.installmentCustomer;
  const itemCount = state.checkout.lines.reduce((sum, line) => {
    const quantity = Number(line.quantity);
    return Number.isFinite(quantity) ? sum + quantity : sum;
  }, 0);
  const flowLabel =
    state.checkout.flow === "installment-create"
      ? locale === "zh"
        ? "新建分期"
        : "New lay-by"
      : state.checkout.flow === "installment-repayment"
        ? locale === "zh"
          ? "分期还款"
          : "Lay-by repayment"
        : state.checkout.flow === "installment-recovery"
          ? locale === "zh"
            ? "恢复分期支付"
            : "Recover lay-by"
          : locale === "zh"
            ? "当前交易"
            : "Current transaction";
  return (
    <View
      style={[
        styles.card,
        styles.orderCard,
        height !== null && { height },
        compact && styles.orderCardCompact,
      ]}
      testID="payment-context-pane"
    >
      <View style={styles.orderHeader}>
        {onBack ? (
          <ActionButton
            disabled={!canLeave}
            icon="arrow-left"
            label={t("action.back")}
            onPress={onBack}
            size="medium"
            sound="navigate"
            testID="payment-back"
            tone="secondary"
          />
        ) : null}
        <Text numberOfLines={1} style={styles.orderCount} testID="payment-item-count">
          {t("order.itemCount", { count: itemCount })}
        </Text>
      </View>
      {onOpenRecoveryCenter &&
      ["recovery-required", "declined", "cancelled"].includes(state.phase) ? (
        <View style={styles.orderRecoveryEntry}>
          <ActionButton
            disabled={state.busy || state.recoveryInFlight === true}
            icon="backup-restore"
            label={locale === "zh" ? "支付恢复中心" : "Payment recovery"}
            onPress={onOpenRecoveryCenter}
            size="medium"
            sound="navigate"
            testID="payment-open-recovery-center"
            tone="warning"
          />
        </View>
      ) : null}
      <View style={styles.orderMeta}>
        <Text style={styles.contextEyebrow}>{flowLabel}</Text>
        {state.orderGuid ? (
          <Text numberOfLines={1} style={styles.orderReference}>
            {t("summary.order", { order: shortIdentifier(state.orderGuid) })}
          </Text>
        ) : null}
      </View>

      {customer ? (
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.customerScrollContent}
          nestedScrollEnabled
          scrollEnabled={customer.editorOpen}
          style={styles.customerScroll}
          testID="payment-customer-scroll"
        >
          <View style={styles.customerCard} testID="payment-installment-customer">
            <View style={styles.customerHeading}>
              <Text style={styles.customerTitle}>
                {locale === "zh" ? "分期顾客" : "Lay-by customer"}
              </Text>
              {customer.editable && presenter.openInstallmentCustomerEditor ? (
                <ActionButton
                  disabled={state.busy}
                  icon="pencil-outline"
                  label={locale === "zh" ? "编辑" : "Edit"}
                  onPress={() => presenter.openInstallmentCustomerEditor?.()}
                  size="small"
                  sound="navigate"
                  testID="payment-customer-edit"
                  tone="quiet"
                />
              ) : null}
            </View>
            {customer.installmentNumber ? (
              <Text style={styles.customerNumber}>
                {customer.installmentNumber}
              </Text>
            ) : null}
            <Text style={styles.customerValue}>
              {customer.name || (locale === "zh" ? "未填写姓名" : "Name required")}
            </Text>
            <Text style={styles.customerValue}>
              {customer.phone || (locale === "zh" ? "未填写电话" : "Phone required")}
            </Text>
            {customer.editorOpen ? (
              <View style={styles.customerEditor} testID="payment-customer-editor">
                <PosKeyboardAwareTextInput
                  autoCorrect={false}
                  editable={!state.busy}
                  onChangeText={(value) =>
                    presenter.setInstallmentCustomerDraftName?.(value)
                  }
                  placeholder={locale === "zh" ? "顾客姓名" : "Customer name"}
                  placeholderTextColor={posColors.placeholder}
                  style={styles.customerInput}
                  testID="payment-customer-name"
                  value={customer.draftName}
                />
                <PosKeyboardAwareTextInput
                  autoCorrect={false}
                  editable={!state.busy}
                  keyboardType="phone-pad"
                  onChangeText={(value) =>
                    presenter.setInstallmentCustomerDraftPhone?.(value)
                  }
                  placeholder={locale === "zh" ? "联系电话" : "Phone"}
                  placeholderTextColor={posColors.placeholder}
                  style={styles.customerInput}
                  testID="payment-customer-phone"
                  value={customer.draftPhone}
                />
                <View style={styles.customerEditorActions}>
                  <ActionButton
                    label={locale === "zh" ? "取消" : "Cancel"}
                    onPress={() => presenter.cancelInstallmentCustomerEditor?.()}
                    size="medium"
                    style={styles.customerEditorButton}
                    sound="navigate"
                    testID="payment-customer-cancel"
                    tone="quiet"
                  />
                  <ActionButton
                    label={locale === "zh" ? "保存" : "Save"}
                    onPress={() => presenter.saveInstallmentCustomer?.()}
                    size="medium"
                    style={styles.customerEditorButton}
                    testID="payment-customer-save"
                    tone="inverse"
                  />
                </View>
              </View>
            ) : null}
          </View>
        </PosKeyboardAwareScrollView>
      ) : null}

      <ScrollView
        nestedScrollEnabled
        scrollEnabled={!compact}
        style={[styles.contextLines, compact && styles.contextLinesCompact]}
        testID="payment-context-lines"
      >
        {state.checkout.lines.length ? (
          state.checkout.lines.map((line) => (
            <View key={line.lineKey} style={styles.contextLine}>
              <View style={styles.contextLineCopy}>
                <Text numberOfLines={2} style={styles.contextLineName}>
                  {line.displayName}
                </Text>
              </View>
              <Text style={styles.contextLineQuantity}>× {line.quantity}</Text>
              <Text style={styles.contextLineAmount}>
                {formatAud(line.actualAmountCents, locale)}
              </Text>
            </View>
          ))
        ) : (
          <Text style={styles.contextEmpty}>
            {locale === "zh" ? "交易商品由收银页确认" : "Items confirmed at checkout"}
          </Text>
        )}
      </ScrollView>

      {showLeaveHint ? (
        <View style={styles.leaveHint} testID="payment-leave-hint">
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.yellowInk}
            name="information-outline"
            size={18}
          />
          <Text style={styles.leaveHintText}>{t("order.leaveHint")}</Text>
        </View>
      ) : null}
    </View>
  );
}

/** 左栏底部（第 4 个键盘行）：已收、剩余与大字应收。 */
function PaymentOrderSummary({
  collectedCents,
  height,
  locale,
  metrics,
  remainingCents,
  t,
  totalCents,
}: Readonly<{
  collectedCents: number;
  height: number | null;
  locale: PaymentLocale;
  metrics: PaymentGridMetrics;
  remainingCents: number;
  t: Translate;
  totalCents: number;
}>) {
  usePosThemeVersion();
  return (
    <View
      style={[styles.card, styles.orderSummary, height !== null && { height }]}
      testID="payment-order-summary"
    >
      <View style={styles.orderSummaryRow}>
        <Text style={styles.orderSummaryLabel}>{t("summary.paid")}</Text>
        <Text style={styles.orderSummaryValue} testID="payment-summary-paid">
          {formatAud(collectedCents, locale)}
        </Text>
      </View>
      <View style={styles.orderSummaryRow}>
        <Text style={styles.orderSummaryLabel}>{t("summary.remainingShort")}</Text>
        <Text style={styles.orderSummaryValue} testID="payment-summary-remaining">
          {formatAud(remainingCents, locale)}
        </Text>
      </View>
      <View style={styles.orderSummaryDue}>
        <Text style={styles.orderSummaryDueLabel}>{t("summary.due")}</Text>
        <Text
          adjustsFontSizeToFit
          numberOfLines={1}
          style={[styles.orderSummaryDueValue, { fontSize: metrics.dueFont }]}
          testID="payment-summary-total"
        >
          {formatAud(totalCents, locale)}
        </Text>
      </View>
    </View>
  );
}

/** 中栏顶部：「剩余应付」(3) :「找零」(2)。剩余是主角大字，找零次一级；有找零时找零卡变橙。 */
function PaymentAmountDisplay({
  changeCents,
  locale,
  metrics,
  remainingCents,
  t,
}: Readonly<{
  changeCents: number;
  locale: PaymentLocale;
  metrics: PaymentGridMetrics;
  remainingCents: number;
  t: Translate;
}>) {
  usePosThemeVersion();
  const changeActive = changeCents > 0;
  return (
    <View
      style={[styles.amountsRow, { gap: metrics.gap, height: metrics.amounts }]}
      testID="payment-amounts"
    >
      <View style={[styles.amountCard, styles.remainingCard]} testID="payment-due-card">
        <Text style={styles.amountCardLabel}>{t("pad.remaining")}</Text>
        <Text
          adjustsFontSizeToFit
          numberOfLines={1}
          style={[
            styles.remainingValue,
            { fontSize: metrics.heroFont, lineHeight: Math.round(metrics.heroFont * 1.1) },
            remainingCents === 0 && styles.remainingValuePaid,
          ]}
          testID="payment-remaining"
        >
          {formatAud(remainingCents, locale)}
        </Text>
      </View>
      <View
        style={[
          styles.amountCard,
          styles.changeCard,
          changeActive && styles.changeCardActive,
        ]}
        testID="payment-change-card"
      >
        <Text
          style={[styles.amountCardLabel, changeActive && styles.changeTextActive]}
        >
          {t("pad.change")}
        </Text>
        <Text
          adjustsFontSizeToFit
          numberOfLines={1}
          style={[
            styles.changeValue,
            { fontSize: metrics.subFont, lineHeight: Math.round(metrics.subFont * 1.1) },
            changeActive && styles.changeTextActive,
          ]}
          testID="payment-change"
        >
          {formatAud(changeCents, locale)}
        </Text>
      </View>
    </View>
  );
}

/** 中栏「当前支付金额」：显示键盘输入的金额（空时灰字显示默认值），右侧退格键。 */
function PaymentAmountInputRow({
  defaultCents,
  disabled,
  inputText,
  locale,
  metrics,
  onBackspace,
  t,
}: Readonly<{
  defaultCents: number;
  disabled: boolean;
  inputText: string;
  locale: PaymentLocale;
  metrics: PaymentGridMetrics;
  onBackspace(): void;
  t: Translate;
}>) {
  usePosThemeVersion();
  const typed = inputText.length > 0;
  return (
    <View
      style={[styles.amountRow, { gap: metrics.gap, height: metrics.medium }]}
      testID="payment-amount-row"
    >
      <View
        style={[styles.amountField, disabled && styles.amountFieldDisabled]}
        testID="payment-amount-field"
      >
        <Text numberOfLines={1} style={styles.amountFieldLabel}>
          {t("pad.amount")}
        </Text>
        <Text
          accessibilityLabel={t("pad.amount")}
          adjustsFontSizeToFit
          numberOfLines={1}
          style={[
            styles.amountFieldValue,
            { fontSize: Math.round(metrics.medium * 0.4) },
            !typed && styles.amountFieldPlaceholder,
          ]}
          testID="payment-amount"
        >
          {typed
            ? `${audCurrencyPrefix(locale)}${inputText}`
            : formatAud(defaultCents, locale)}
        </Text>
        {!disabled ? <View style={styles.amountCaret} /> : null}
      </View>
      <PosPressable
        accessibilityLabel={t("pad.backspace")}
        accessibilityRole="button"
        accessibilityState={{ disabled: disabled || !typed }}
        disabled={disabled || !typed}
        onPress={onBackspace}
        sound="danger"
        style={({ pressed }) => [
          styles.backspaceKey,
          { width: Math.round(metrics.medium * 1.1) },
          (disabled || !typed) && styles.keyDisabled,
          pressed && styles.pressed,
        ]}
        testID="payment-key-backspace"
      >
        <MaterialCommunityIcons
          accessible={false}
          color={disabled || !typed ? posColors.disabledInk : posColors.ink}
          name="backspace-outline"
          size={28}
        />
      </PosPressable>
    </View>
  );
}

/**
 * 快捷现金：澳元纸币风格（按面额代表色 + 左侧仿聚合物钞票透明窗口 + AUD 小字），
 * 只取颜色与版式，不复制真钞人像 / 国徽等图案。颜色固定，不随主题变化。
 */
function PaymentQuickCashRow({
  enabled,
  metrics,
  onPress,
}: Readonly<{
  enabled: boolean;
  metrics: PaymentGridMetrics;
  onPress(note: QuickCashNote): void;
}>) {
  usePosThemeVersion();
  return (
    <View
      style={[styles.quickCashRow, { gap: metrics.gap, height: metrics.medium }]}
      testID="payment-cash-quick"
    >
      {QUICK_CASH_NOTES.map((note) => {
        const colors = enabled
          ? QUICK_CASH_NOTE_COLORS[note]
          : QUICK_CASH_DISABLED_COLORS;
        return (
          <PosPressable
            accessibilityLabel={`AUD $${note}`}
            accessibilityRole="button"
            accessibilityState={{ disabled: !enabled }}
            disabled={!enabled}
            key={note}
            onPress={() => onPress(note)}
            sound="key"
            style={({ pressed }) => [
              styles.note,
              { backgroundColor: colors.light, borderColor: colors.deep },
              !enabled && styles.noteDisabled,
              pressed && enabled && styles.pressed,
            ]}
            testID={`payment-cash-quick-${note}`}
          >
            <View style={[styles.noteShade, { backgroundColor: colors.deep }]} />
            <View style={styles.noteWindow} />
            <Text style={[styles.noteAud, { color: colors.ink }]}>AUD</Text>
            <Text
              adjustsFontSizeToFit
              numberOfLines={1}
              style={[
                styles.noteValue,
                { color: colors.ink, fontSize: Math.round(metrics.medium * 0.32) },
              ]}
            >
              {`$${note}`}
            </Text>
          </PosPressable>
        );
      })}
    </View>
  );
}

const KEYPAD_ROWS = [
  ["1", "2", "3"],
  ["4", "5", "6"],
  ["7", "8", "9"],
  [".", "0", "clear"],
] as const;

/** 中栏数字键盘 4×3：四行与左栏汇总、右栏已添加支付 / 完成区共用行线。 */
function PaymentKeypad({
  disabled,
  metrics,
  onClear,
  onKey,
  t,
}: Readonly<{
  disabled: boolean;
  metrics: PaymentGridMetrics;
  onClear(): void;
  onKey(key: "0" | "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "."): void;
  t: Translate;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.keypad, { gap: metrics.gap }]} testID="payment-keypad">
      {KEYPAD_ROWS.map((row, rowIndex) => (
        <View
          key={row[0]}
          style={[styles.keypadRow, { gap: metrics.gap, height: metrics.keyRow }]}
          testID={`payment-keypad-row-${rowIndex}`}
        >
          {row.map((key) =>
            key === "clear" ? (
              <PosPressable
                accessibilityRole="button"
                disabled={disabled}
                key={key}
                onPress={onClear}
                sound="danger"
                style={({ pressed }) => [
                  styles.keypadKey,
                  styles.keypadClear,
                  disabled && styles.keyDisabled,
                  pressed && !disabled && styles.pressed,
                ]}
                testID="payment-key-clear"
              >
                <Text
                  style={[
                    styles.keypadClearText,
                    disabled && styles.keypadKeyTextDisabled,
                  ]}
                >
                  {t("pad.clear")}
                </Text>
              </PosPressable>
            ) : (
              <PosPressable
                accessibilityRole="button"
                disabled={disabled}
                key={key}
                onPress={() => onKey(key)}
                sound="key"
                style={({ pressed }) => [
                  styles.keypadKey,
                  disabled && styles.keyDisabled,
                  pressed && !disabled && styles.pressed,
                ]}
                testID={`payment-key-${key === "." ? "decimal" : key}`}
              >
                <Text
                  style={[
                    styles.keypadKeyText,
                    disabled && styles.keypadKeyTextDisabled,
                  ]}
                >
                  {key}
                </Text>
              </PosPressable>
            ),
          )}
        </View>
      ))}
    </View>
  );
}

/** 右栏上部：付款方式 2×2（现金 / 信用卡 / 手动刷卡 / 购物券）+ 终端标签 + 分期开关。 */
function PaymentMethodsCard({
  cardChannel,
  cardSelectable,
  cashSelectable,
  compact,
  height,
  installmentModeControl,
  manualCardSelectable,
  metrics,
  onCard,
  onCash,
  onManualCard,
  onVoucher,
  state,
  t,
  voucherSelectable,
}: Readonly<{
  cardChannel: PaymentCardChannel | null;
  cardSelectable: boolean;
  cashSelectable: boolean;
  compact: boolean;
  height: number | null;
  installmentModeControl: PaymentInstallmentModeControl | undefined;
  manualCardSelectable: boolean;
  metrics: PaymentGridMetrics;
  onCard(): void;
  onCash(): void;
  onManualCard(): void;
  onVoucher(): void;
  state: PaymentPresenterState;
  t: Translate;
  voucherSelectable: boolean;
}>) {
  usePosThemeVersion();
  const fontSize = metrics.large ? 17 : 15;
  return (
    <View
      style={[
        styles.card,
        styles.methodsCard,
        { gap: metrics.gap },
        height !== null && { height },
      ]}
      testID="payment-methods"
    >
      <View style={styles.methodsHeader}>
        <Text style={styles.cardTitle}>{t("method.title")}</Text>
        {cardChannel === "linkly-cloud" ? (
          <LinklyTerminalSelector state={state} t={t} />
        ) : cardChannel ? (
          <View style={styles.terminalChip} testID="payment-terminal-chip">
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.blue}
              name="tablet"
              size={14}
            />
            <Text numberOfLines={1} style={styles.terminalChipText}>
              {t("method.terminal", { name: t(paymentMethodCopyKey(cardChannel)) })}
            </Text>
          </View>
        ) : null}
      </View>
      <View
        style={[styles.methodTiles, compact && styles.methodTilesCompact, { gap: metrics.gap }]}
      >
        <View style={[styles.methodTileRow, { gap: metrics.gap }]}>
          <PaymentMethodTile
            disabled={!cashSelectable}
            fontSize={fontSize}
            icon="cash"
            label={t("tile.cash")}
            onPress={onCash}
            testID="payment-method-cash"
            tone="cash"
          />
          <PaymentMethodTile
            badge={cardChannel ? t(paymentMethodCopyKey(cardChannel)) : null}
            disabled={!cardSelectable}
            fontSize={fontSize}
            icon="credit-card-outline"
            label={t("tile.card")}
            onPress={onCard}
            testID="payment-method-card"
            tone="card"
          />
        </View>
        <View style={[styles.methodTileRow, { gap: metrics.gap }]}>
          <PaymentMethodTile
            disabled={!manualCardSelectable}
            fontSize={fontSize}
            icon="keyboard-outline"
            label={t("tile.manualCard")}
            onPress={onManualCard}
            testID="payment-method-manual-card"
            tone="plain"
          />
          <PaymentMethodTile
            disabled={!voucherSelectable}
            fontSize={fontSize}
            icon="ticket-percent-outline"
            label={t("tile.voucher")}
            onPress={onVoucher}
            testID="payment-method-voucher"
            tone="voucher"
          />
        </View>
      </View>
      {installmentModeControl ? (
        <PaymentInstallmentToggle control={installmentModeControl} t={t} />
      ) : null}
    </View>
  );
}

function PaymentMethodTile({
  badge = null,
  disabled,
  fontSize,
  icon,
  label,
  onPress,
  testID,
  tone,
}: Readonly<{
  badge?: string | null;
  disabled: boolean;
  fontSize: number;
  icon: PosIconName;
  label: string;
  onPress(): void;
  testID: string;
  tone: "cash" | "card" | "plain" | "voucher";
}>) {
  usePosThemeVersion();
  const color = disabled
    ? posColors.disabledInk
    : tone === "cash"
      ? posColors.green
      : tone === "card"
        ? posColors.blue
        : tone === "voucher"
          ? posColors.yellowInk
          : posColors.ink;
  return (
    <PosPressable
      accessibilityLabel={badge ? `${label} · ${badge}` : label}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.methodTile,
        tone === "cash" && styles.methodTileCash,
        tone === "card" && styles.methodTileCard,
        tone === "voucher" && styles.methodTileVoucher,
        disabled && styles.methodTileDisabled,
        pressed && !disabled && styles.pressed,
      ]}
      testID={testID}
    >
      {badge ? (
        // 信用卡角标只显示当前通道名（Linkly / Zeller / Square）。
        <View style={styles.methodBadge} testID={`${testID}-channel`}>
          <Text numberOfLines={1} style={styles.methodBadgeText}>
            {badge}
          </Text>
        </View>
      ) : null}
      <MaterialCommunityIcons accessible={false} color={color} name={icon} size={28} />
      <Text
        adjustsFontSizeToFit
        numberOfLines={1}
        style={[styles.methodTileText, { color, fontSize }]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

/** 右栏中部：已添加支付（含暂存现金）、支付状态、刷卡等待、Linkly 终端交互与恢复操作。 */
function PaymentTendersCard({
  compact,
  height,
  installmentModeControl,
  locale,
  onCancelPreparedCash,
  onRemoveStagedCash,
  presenter,
  recoveryPrimary,
  showStatusPanel,
  stagedCashCents,
  state,
  t,
}: Readonly<{
  compact: boolean;
  height: number | null;
  installmentModeControl: PaymentInstallmentModeControl | undefined;
  locale: PaymentLocale;
  onCancelPreparedCash(): void;
  onRemoveStagedCash(): void;
  presenter: PaymentScreenPresenter;
  recoveryPrimary: boolean;
  showStatusPanel: boolean;
  stagedCashCents: number | null;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const empty = state.tenders.length === 0 && stagedCashCents === null;
  const cashSettlement = state.checkout.cash;
  return (
    <View
      style={[styles.card, styles.tendersCard, height !== null && { height }]}
      testID="payment-tenders"
    >
      <Text style={styles.cardTitle}>{t("tenders.title")}</Text>
      <ScrollView
        contentContainerStyle={styles.tendersContent}
        nestedScrollEnabled
        scrollEnabled={!compact}
        style={styles.tendersScroll}
        testID="payment-tenders-scroll"
      >
        {state.fieldIssue ? (
          <Text
            accessibilityRole="alert"
            style={styles.fieldError}
            testID="payment-field-error"
          >
            {t(paymentFieldIssueCopyKey(state.fieldIssue))}
          </Text>
        ) : null}
        {installmentModeControl?.issue ? (
          <Text
            accessibilityRole="alert"
            style={styles.fieldError}
            testID="payment-installment-issue"
          >
            {t("installment.unavailable")}
          </Text>
        ) : null}
        {state.tenders.map((tender) => (
          <TenderRow
            cashTenderedCents={
              tender.method === "cash" && cashSettlement.tenderedCents > 0
                ? cashSettlement.tenderedCents
                : null
            }
            key={tender.tenderGuid}
            locale={locale}
            onRemove={() => {
              void presenter.removeTender(tender.tenderGuid);
            }}
            state={state}
            tender={tender}
            t={t}
          />
        ))}
        {stagedCashCents !== null ? (
          <View style={styles.tenderRow} testID="payment-staged-cash">
            <MaterialCommunityIcons
              accessible={false}
              color={posColors.green}
              name="cash"
              size={22}
            />
            <View style={styles.tenderIdentity}>
              <Text style={styles.tenderMethod}>{t("tenders.stagedCash")}</Text>
              <Text style={styles.tenderDisposition}>
                {t("tenders.cashTendered", {
                  amount: formatAud(stagedCashCents, locale),
                })}
              </Text>
            </View>
            <Text style={styles.tenderAmount}>
              {formatAud(stagedCashCents, locale)}
            </Text>
            <PosPressable
              accessibilityLabel={t("action.remove")}
              accessibilityRole="button"
              disabled={state.busy}
              onPress={onRemoveStagedCash}
              sound="danger"
              style={({ pressed }) => [
                styles.tenderRemove,
                state.busy && styles.keyDisabled,
                pressed && !state.busy && styles.pressed,
              ]}
              testID="payment-staged-cash-remove"
            >
              <MaterialCommunityIcons
                accessible={false}
                color={state.busy ? posColors.disabledInk : posColors.red}
                name="trash-can-outline"
                size={22}
              />
            </PosPressable>
          </View>
        ) : null}
        {cashSettlement.tenderedCents > 0 ? (
          // 实收与入账只显示 presenter 给出的结算结果（普通销售按五分取整，分期不取整）。
          <View style={styles.cashSettlement} testID="payment-cash-settlement">
            <SummaryAmount
              label={locale === "zh" ? "实收现金" : "Cash tendered"}
              locale={locale}
              value={cashSettlement.tenderedCents}
            />
            <SummaryAmount
              label={locale === "zh" ? "入账金额" : "Applied"}
              locale={locale}
              testID="payment-cash-applied"
              value={cashSettlement.appliedCents}
            />
          </View>
        ) : null}
        {showStatusPanel ? (
          state.phase === "ready" ? (
            empty ? (
              <View style={styles.emptyTenders} testID="payment-status-ready">
                <Text style={styles.emptyTendersText}>{t("tenders.empty")}</Text>
              </View>
            ) : null
          ) : (
            <PaymentStatusPanel state={state} t={t} />
          )
        ) : null}
        {state.checkout.preparedCollection === "manual-card" &&
        state.checkout.canConfirm ? (
          <Text style={styles.preparedHint} testID="payment-manual-card-prepared-hint">
            {t("installmentManualCard.prepared")}
          </Text>
        ) : null}
        <LinklyControls presenter={presenter} state={state} t={t} />
        <RecoveryActions
          onCancelPreparedCash={onCancelPreparedCash}
          presenter={presenter}
          primary={recoveryPrimary}
          state={state}
          t={t}
        />
      </ScrollView>
    </View>
  );
}

/**
 * 右栏底部（第 4 个键盘行）：上一行「已收 / 状态」，下面按钮行。
 * 普通情况只有「完成支付」整宽；刷卡等待时变为「取消」+「完成支付」(1 : 1.8)。
 */
function PaymentFinishArea({
  collectedCents,
  finishEnabled,
  height,
  locale,
  metrics,
  onCancel,
  onCancelPreparedCash,
  onConfirm,
  onFinish,
  showCancel,
  showConfirmation,
  showPreparedCashCancellation,
  state,
  t,
  waiting,
}: Readonly<{
  collectedCents: number;
  finishEnabled: boolean;
  height: number | null;
  locale: PaymentLocale;
  metrics: PaymentGridMetrics;
  onCancel(): void;
  onCancelPreparedCash(): void;
  onConfirm(): void;
  onFinish(): void;
  showCancel: boolean;
  showConfirmation: boolean;
  showPreparedCashCancellation: boolean;
  state: PaymentPresenterState;
  t: Translate;
  waiting: boolean;
}>) {
  usePosThemeVersion();
  const statusKey: PaymentCopyKey = waiting
    ? "finish.autoComplete"
    : finishEnabled || showConfirmation
      ? "finish.ready"
      : "finish.needFull";
  const buttonSize: PosButtonSize = metrics.large ? "primary" : "large";
  const buttonHeight = height === null
    ? metrics.medium
    : Math.max(PAYMENT_SMALL_TARGET, Math.min(metrics.medium, height - 26));
  return (
    <View
      style={[styles.finishArea, height !== null && { height }]}
      testID="payment-finish-area"
    >
      <View style={styles.finishLine}>
        <Text numberOfLines={1} style={styles.finishLineText}>
          {t("finish.collected", { amount: formatAud(collectedCents, locale) })}
        </Text>
        <Text numberOfLines={1} style={styles.finishLineStatus} testID="payment-finish-status">
          {t(statusKey)}
        </Text>
      </View>
      <View
        style={[styles.finishButtons, { gap: metrics.gap, height: buttonHeight }]}
        testID="payment-finish-buttons"
      >
        {showCancel ? (
          <ActionButton
            disabled={state.busy || state.recoveryInFlight === true}
            label={t("finish.cancel")}
            onPress={onCancel}
            size={buttonSize}
            style={[styles.finishCancel, { minHeight: buttonHeight }]}
            testID="payment-cancel"
            tone="danger"
          />
        ) : showPreparedCashCancellation ? (
          <ActionButton
            disabled={state.busy}
            label={t(cancelPreparedLabelKey(state))}
            onPress={onCancelPreparedCash}
            size={buttonSize}
            sound="danger"
            style={[styles.finishCancel, { minHeight: buttonHeight }]}
            testID="payment-cancel-prepared-cash"
            tone="danger"
          />
        ) : null}
        {showConfirmation ? (
          <ActionButton
            disabled={state.busy}
            icon="check-circle"
            label={
              state.checkout.cashRepaymentStatus === "ready" ||
              state.checkout.cashRepaymentStatus === "confirming"
                ? t(confirmCollectionLabelKey(state))
                : locale === "zh"
                  ? "确认分期付款"
                  : "Confirm lay-by payment"
            }
            onPress={onConfirm}
            size={buttonSize}
            sound="danger"
            style={[styles.finishPrimary, { minHeight: buttonHeight }]}
            testID="payment-confirm"
            tone="primary"
          />
        ) : (
          <ActionButton
            disabled={!finishEnabled}
            icon="check-circle-outline"
            label={t("finish.action")}
            onPress={onFinish}
            size={buttonSize}
            style={[styles.finishPrimary, { minHeight: buttonHeight }]}
            testID="payment-finish"
            tone="primary"
          />
        )}
      </View>
    </View>
  );
}

/** 支付状态说明：放在「已添加支付」卡里，等待、未知、被拒等状态用底色区分；刷卡等待时提示在终端上刷卡。 */
function PaymentStatusPanel({
  state,
  t,
}: Readonly<{
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const tone = statusTone(state.phase);
  const awaitingCard =
    state.attemptId !== null &&
    state.provider !== null &&
    state.provider !== "voucher" &&
    state.provider !== "manual-card" &&
    (state.phase === "awaiting-terminal" || state.phase === "pending");
  return (
    <View
      accessibilityRole="summary"
      style={[
        styles.statusPanel,
        tone === "warning" && styles.statusPanelWarning,
        tone === "danger" && styles.statusPanelDanger,
        tone === "success" && styles.statusPanelSuccess,
      ]}
      testID={`payment-status-${state.phase}`}
    >
      {awaitingCard ? (
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.blue}
          name="tablet"
          size={30}
        />
      ) : null}
      <View style={styles.statusCopy}>
        <Text
          style={[
            styles.statusTitle,
            tone === "warning" && styles.statusTitleWarning,
            tone === "danger" && styles.statusTitleDanger,
            tone === "success" && styles.statusTitleSuccess,
          ]}
        >
          {t(paymentPhaseTitleKey(state.phase))}
        </Text>
        {awaitingCard && state.provider ? (
          <Text style={styles.statusWaiting} testID="payment-terminal-waiting">
            {`${t("terminal.waiting")} · ${t(paymentMethodCopyKey(state.provider))}`}
          </Text>
        ) : null}
        <Text style={styles.statusHint}>
          {t(paymentPhaseHintKey(state.phase))}
        </Text>
      </View>
      {state.busy || state.recoveryInFlight ? (
        <ActivityIndicator
          accessibilityLabel={
            state.recoveryInFlight ? t("action.recovering") : undefined
          }
          color={tone === "danger" ? posColors.red : posColors.blue}
          size="small"
          testID={
            state.recoveryInFlight
              ? "payment-recovery-in-flight"
              : "payment-busy"
          }
        />
      ) : null}
    </View>
  );
}

/** 付款方式卡右上角的 Linkly 终端小标签：只展示设置中已选中的线路，付款页不提供重新绑定入口。 */
function LinklyTerminalSelector({
  state,
  t,
}: Readonly<{
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const configured = state.providers.some(
    (provider) => provider.provider === "linkly-cloud" && provider.available,
  );
  const resource = state.linklyTerminals;
  const channelChip = (
    <View style={styles.terminalChip} testID="payment-terminal-chip">
      <Text numberOfLines={1} style={styles.terminalChipText}>
        {t("method.terminal", { name: t("method.linkly-cloud") })}
      </Text>
    </View>
  );
  if (!configured || !resource || resource.kind === "unavailable") {
    return channelChip;
  }
  if (resource.kind === "loading") {
    return (
      <View style={styles.terminalChip} testID="payment-linkly-terminal-loading">
        <Text numberOfLines={1} style={styles.terminalChipText}>
          {t("linkly.terminalsLoading")}
        </Text>
      </View>
    );
  }
  const snapshot = resource.snapshot;
  if (!snapshot || resource.kind === "failed") {
    return (
      <View
        style={[styles.terminalChip, styles.terminalChipError]}
        testID="payment-linkly-terminal-empty"
      >
        <Text numberOfLines={2} style={styles.terminalChipErrorText}>
          {t("linkly.terminalsFailed")}
        </Text>
      </View>
    );
  }
  if (snapshot.mode !== "Active") return channelChip;
  const terminal = snapshot.terminals.find(
    (item) => item.terminalId === snapshot.selectedTerminalId,
  );
  if (!terminal) {
    return (
      <View
        style={[styles.terminalChip, styles.terminalChipError]}
        testID="payment-linkly-terminal-empty"
      >
        <Text numberOfLines={2} style={styles.terminalChipErrorText}>
          {t("linkly.noTerminals")}
        </Text>
      </View>
    );
  }
  const ready = !terminal.isBusy && terminal.isReady && terminal.pairingState === "Ready";
  const status = terminal.isBusy
    ? t("linkly.terminalBusy")
    : terminal.pairingState === "NeedsRepair"
      ? t("linkly.terminalNeedsRepair")
      : terminal.pairingState === "Unpaired"
        ? t("linkly.terminalUnpaired")
        : t("linkly.terminalUnknown");
  return (
    <View
      accessibilityLabel={t("linkly.currentTerminal")}
      style={[styles.terminalChip, !ready && styles.terminalChipError]}
      testID="payment-linkly-terminal-current"
    >
      <Text numberOfLines={1} style={styles.terminalChipText}>
        {t("method.terminal", { name: t("method.linkly-cloud") })}
      </Text>
      <Text numberOfLines={1} style={styles.terminalChipText}>
        ·
      </Text>
      <Text numberOfLines={1} style={styles.terminalChipName}>
        {`${terminal.displayName} · Lane ${terminal.laneNo}`}
      </Text>
      {!ready ? (
        <Text numberOfLines={1} style={styles.terminalChipErrorText}>
          {status}
        </Text>
      ) : null}
    </View>
  );
}

/**
 * 「已添加支付」卡里的恢复类操作：恢复支付 / 确认已收 / 主管作废购物券 / 尚未收现取消续付。
 * 「取消」只在完成区（刷卡等待时）出现，见 PaymentFinishArea。
 */
function RecoveryActions({
  onCancelPreparedCash,
  presenter,
  primary,
  state,
  t,
}: Readonly<{
  onCancelPreparedCash(): void;
  presenter: PaymentScreenPresenter;
  /** 这一组是否承担本屏唯一的主按钮（没有录入区和确认按钮时）。 */
  primary: boolean;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const flags = recoveryActionFlags(state, presenter);
  if (!flags.hasDockActions) return null;
  const actionDisabled = state.busy || state.recoveryInFlight === true;
  return (
    <View style={styles.recoveryActions} testID="payment-recovery-actions">
      {flags.cashConfirmationRecovery ? (
        <ActionButton
          disabled={actionDisabled}
          icon="check-circle"
          label={t(confirmCollectionLabelKey(state))}
          onPress={() => {
            void presenter.confirm?.();
          }}
          size={primary ? "large" : "button"}
          testID="payment-confirm-cash-recovery"
          tone={primary ? "primary" : "info"}
        />
      ) : state.allowedActions.recover ? (
        <ActionButton
          disabled={actionDisabled}
          icon="backup-restore"
          label={
            state.recoveryInFlight
              ? t("action.recovering")
              : t("action.recover")
          }
          onPress={() => {
            void presenter.recover();
          }}
          size={primary ? "large" : "button"}
          testID="payment-recover"
          tone={primary ? "primary" : "info"}
        />
      ) : null}
      {flags.showVoidBlockedTenderReversal ? (
        <ActionButton
          disabled={actionDisabled}
          icon="shield-key-outline"
          label={t("action.voidBlockedTenderReversal")}
          onPress={() => {
            void presenter.voidBlockedTenderReversal?.();
          }}
          size="button"
          testID="payment-void-blocked-tender-reversal"
          tone="danger"
        />
      ) : null}
      {flags.showPreparedCashCancellation ? (
        <ActionButton
          disabled={actionDisabled}
          label={t(cancelPreparedLabelKey(state))}
          onPress={onCancelPreparedCash}
          size="button"
          sound="danger"
          testID="payment-cancel-prepared-cash"
          tone="danger"
        />
      ) : null}
    </View>
  );
}

function LinklyControls({
  presenter,
  state,
  t,
}: Readonly<{
  presenter: PaymentScreenPresenter;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  if (state.provider !== "linkly-cloud" || !state.attemptId) {
    return null;
  }
  const allowed = new Set(state.linkly.allowedKeys);
  const interaction = state.linkly.interaction;
  const showSafeKeys =
    state.linkly.status === "in-progress" &&
    state.phase !== "unknown" &&
    state.phase !== "recovery-required";
  return (
    <View style={styles.linklyPanel} testID="payment-linkly-controls">
      <Text style={styles.sectionTitle}>{t("terminal.title")}</Text>
      <Text style={styles.inputHint}>{t("terminal.safeOnly")}</Text>
      {interaction?.displayText ? (
        <Text style={styles.linklyDisplayText} testID="payment-linkly-display-text">
          {interaction.displayText}
        </Text>
      ) : null}
      {interaction?.displayLines.map((line, index) => (
        <Text
          key={`${index}-${line}`}
          style={styles.linklyDisplayText}
          testID={`payment-linkly-display-line-${index}`}
        >
          {line}
        </Text>
      ))}
      {interaction?.inputType ? (
        <Text style={styles.linklyPromptText} testID="payment-linkly-input-type">
          {t("terminal.inputRequired")}
        </Text>
      ) : null}
      {interaction?.graphicCode ? (
        <Text style={styles.linklyPromptText} testID="payment-linkly-graphic-code">
          {t(linklyGraphicCopyKey(interaction.graphicCode))}
        </Text>
      ) : null}
      {interaction?.recoveryAction ? (
        <Text style={styles.linklyPromptText} testID="payment-linkly-recovery-action">
          {t("terminal.recoveryRequired")}
        </Text>
      ) : null}
      {showSafeKeys ? (
        <View style={styles.linklyKeyGrid}>
          {LINKLY_SAFE_OPERATOR_KEYS.filter((key) =>
            allowed.has(key) && !(key === "cancel" && allowed.has("ok"))).map((key) => (
            <ActionButton
              disabled={state.busy}
              key={key}
              label={t(key === "ok" && allowed.has("cancel") ? "terminal.okCancel" : linklyKeyCopyKey(key))}
              onPress={() => {
                void presenter.sendLinklyKey(key);
              }}
              size="button"
              sound="key"
              style={styles.linklyKey}
              testID={`payment-linkly-${key}`}
              tone={key === "cancel" || key === "no" ? "danger" : "secondary"}
            />
          ))}
        </View>
      ) : null}
      {state.linkly.status === "completed" ||
      state.linkly.status === "cancelled" ||
      state.linkly.status === "declined" ? (
        <View style={styles.linklyConfirmation}>
          <ActionButton
            disabled={state.busy}
            label={t("action.linklyPrinted")}
            onPress={() => {
              void presenter.markLinklyReceiptPrinted();
            }}
            size="button"
            sound="tap"
            style={styles.linklyKey}
            testID="payment-linkly-receipt-printed"
            tone="secondary"
          />
          <ActionButton
            disabled={state.busy}
            label={t("action.linklyAcknowledge")}
            onPress={() => {
              void presenter.acknowledgeLinkly();
            }}
            size="button"
            sound="tap"
            style={styles.linklyKey}
            testID="payment-linkly-acknowledge"
            tone="secondary"
          />
        </View>
      ) : null}
    </View>
  );
}

/** 分期开关：付款方式卡底部的一行，左侧文字、右侧开关。 */
function PaymentInstallmentToggle({
  control,
  t,
}: Readonly<{
  control: PaymentInstallmentModeControl;
  t: Translate;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityLabel={t("installment.toggle")}
      accessibilityRole="switch"
      accessibilityState={{
        checked: control.enabled,
        disabled: control.locked,
      }}
      disabled={control.locked}
      onPress={() => control.onToggle(!control.enabled)}
      style={({ pressed }) => [
        styles.installmentToggle,
        control.locked && styles.installmentToggleLocked,
        pressed && !control.locked && styles.pressed,
      ]}
      testID="payment-installment-toggle"
    >
      <MaterialCommunityIcons
        accessible={false}
        color={control.locked ? posColors.disabledInk : posColors.mutedInk}
        name="calendar-month-outline"
        size={22}
      />
      <Text
        numberOfLines={1}
        style={[
          styles.installmentToggleText,
          control.locked && styles.methodTextDisabled,
        ]}
      >
        {t("installment.toggle")}
      </Text>
      <View
        style={[
          styles.installmentToggleTrack,
          control.enabled && styles.installmentToggleTrackEnabled,
        ]}
      >
        <View
          style={[
            styles.installmentToggleThumb,
            control.enabled && styles.installmentToggleThumbEnabled,
          ]}
        />
      </View>
    </PosPressable>
  );
}

/** 已添加支付的一行：行高 ≥ 64，可撤销的右侧是 56×56 删除键，不可撤销的显示锁形。 */
function TenderRow({
  cashTenderedCents,
  locale,
  onRemove,
  state,
  tender,
  t,
}: Readonly<{
  cashTenderedCents: number | null;
  locale: PaymentLocale;
  onRemove(): void;
  state: PaymentPresenterState;
  tender: PaymentPresenterTender;
  t: Translate;
}>) {
  usePosThemeVersion();
  const removeDisabled = !state.allowedActions.removeTender || state.busy;
  const label =
    tender.method === "cash"
      ? t("tile.cash")
      : tender.method === "voucher"
        ? t("tile.voucher")
        : tender.provider && tender.provider !== "manual-card"
          ? `${t("tile.card")} · ${t(paymentMethodCopyKey(tender.provider))}`
          : tender.provider === "manual-card"
            ? t("tile.manualCard")
            : t("tile.card");
  return (
    <View style={styles.tenderRow} testID={`payment-tender-${tender.tenderGuid}`}>
      <MaterialCommunityIcons
        accessible={false}
        color={
          tender.method === "cash"
            ? posColors.green
            : tender.method === "voucher"
              ? posColors.yellowInk
              : posColors.blue
        }
        name={
          tender.method === "cash"
            ? "cash"
            : tender.method === "voucher"
              ? "ticket-percent-outline"
              : "credit-card-outline"
        }
        size={22}
      />
      <View style={styles.tenderIdentity}>
        <Text numberOfLines={1} style={styles.tenderMethod}>
          {label}
        </Text>
        <Text numberOfLines={1} style={styles.tenderDisposition}>
          {cashTenderedCents !== null
            ? t("tenders.cashTendered", {
                amount: formatAud(cashTenderedCents, locale),
              })
            : t(tender.reversible ? "summary.reversible" : "summary.locked")}
        </Text>
      </View>
      <Text style={styles.tenderAmount}>
        {formatAud(tender.amount.cents, locale)}
      </Text>
      {tender.reversible ? (
        <PosPressable
          accessibilityLabel={t("action.remove")}
          accessibilityRole="button"
          accessibilityState={{ disabled: removeDisabled }}
          disabled={removeDisabled}
          onPress={onRemove}
          sound="danger"
          style={({ pressed }) => [
            styles.tenderRemove,
            removeDisabled && styles.keyDisabled,
            pressed && !removeDisabled && styles.pressed,
          ]}
          testID={`payment-remove-${tender.tenderGuid}`}
        >
          <MaterialCommunityIcons
            accessible={false}
            color={removeDisabled ? posColors.disabledInk : posColors.red}
            name="trash-can-outline"
            size={22}
          />
        </PosPressable>
      ) : (
        <View
          accessibilityLabel={t("summary.locked")}
          style={styles.tenderLocked}
          testID={`payment-tender-locked-${tender.tenderGuid}`}
        >
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.mutedInk}
            name="lock-outline"
            size={20}
          />
        </View>
      )}
    </View>
  );
}

function SummaryAmount({
  emphasis = false,
  label,
  locale,
  strong = false,
  testID,
  value,
}: Readonly<{
  emphasis?: boolean;
  label: string;
  locale: PaymentLocale;
  strong?: boolean;
  testID?: string;
  value: number;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.summaryAmountRow}>
      <Text style={styles.summaryLabel}>{label}</Text>
      <Text
        style={[
          styles.summaryAmount,
          strong && styles.summaryAmountStrong,
          emphasis && styles.summaryAmountEmphasis,
        ]}
        testID={testID}
      >
        {formatAud(value, locale)}
      </Text>
    </View>
  );
}

/** 付款页内的按钮：尺寸与配色走通用 PosButton，保留「危险操作用警示音」的约定。 */
function ActionButton({
  disabled = false,
  icon,
  label,
  onPress,
  size = "button",
  sound,
  style,
  testID,
  tone = "secondary",
}: Readonly<{
  disabled?: boolean;
  icon?: PosIconName;
  label: string;
  onPress(): void;
  size?: PosButtonSize;
  sound?: "tap" | "key" | "navigate" | "danger";
  style?: StyleProp<ViewStyle>;
  testID?: string;
  tone?: PosButtonTone;
}>) {
  return (
    <PosButton
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

/**
 * 恢复类操作的显示条件（「已添加支付」卡里的恢复区），条件本身与改版前一致。
 */
function recoveryActionFlags(
  state: PaymentPresenterState,
  presenter: PaymentScreenPresenter,
): Readonly<{
  cashConfirmationRecovery: boolean;
  showPreparedCashCancellation: boolean;
  showVoidBlockedTenderReversal: boolean;
  hasDockActions: boolean;
}> {
  const preparedCashCancellation = canCancelPreparedCash(state);
  const cashConfirmationRecovery =
    state.checkout.flow === "installment-repayment" &&
    state.phase === "recovery-required" &&
    state.checkout.cashRepaymentStatus === "ready";
  const showPreparedCashCancellation =
    preparedCashCancellation && state.phase === "recovery-required";
  // Blocked 礼券撤券只能由主管授权作废；没有授权能力时不显示任何可点按钮。
  const showVoidBlockedTenderReversal =
    state.tenderReversalRecovery?.status === "blocked" &&
    state.tenderReversalRecovery.voidAvailable === true &&
    typeof presenter.voidBlockedTenderReversal === "function";
  return {
    cashConfirmationRecovery,
    showPreparedCashCancellation,
    showVoidBlockedTenderReversal,
    hasDockActions:
      state.allowedActions.recover ||
      showPreparedCashCancellation ||
      cashConfirmationRecovery ||
      showVoidBlockedTenderReversal,
  };
}

function statusTone(
  phase: PaymentUiPhase,
): "neutral" | "warning" | "danger" | "success" {
  if (phase === "success" || phase === "partial") return "success";
  if (
    phase === "unknown" ||
    phase === "recovery-required" ||
    phase === "offline-cash" ||
    phase === "draft-prepared"
  ) {
    return "warning";
  }
  if (phase === "declined" || phase === "cancelled") return "danger";
  return "neutral";
}

/** 已登记、等待收银员确认的收款 tender：现金，或手动刷卡；且不可撤销。 */
function isPreparedCollectionTender(
  tender: PaymentPresenterState["tenders"][number],
): boolean {
  return (
    tender.reversible === false &&
    (tender.method === "cash" || tender.provider === "manual-card")
  );
}

function confirmCollectionLabelKey(
  state: PaymentPresenterState,
): "action.confirmManualCardReceived" | "action.confirmCashReceived" {
  return state.checkout.preparedCollection === "manual-card"
    ? "action.confirmManualCardReceived"
    : "action.confirmCashReceived";
}

/** 取消确认弹窗的四段文案：现金需要主管核对钱箱；手动刷卡由原收银员核对刷卡机。 */
function cancelPreparedModalKey(
  state: PaymentPresenterState,
  part: "title" | "body" | "dismiss" | "confirm",
):
  | "installment.cancelPreparedCash.title"
  | "installment.cancelPreparedCash.body"
  | "installment.cancelPreparedCash.dismiss"
  | "installment.cancelPreparedCash.confirm"
  | "installment.cancelPreparedManualCard.title"
  | "installment.cancelPreparedManualCard.body"
  | "installment.cancelPreparedManualCard.dismiss"
  | "installment.cancelPreparedManualCard.confirm" {
  return state.checkout.preparedCollection === "manual-card"
    ? (`installment.cancelPreparedManualCard.${part}` as const)
    : (`installment.cancelPreparedCash.${part}` as const);
}

function cancelPreparedLabelKey(
  state: PaymentPresenterState,
): "action.cancelPreparedManualCard" | "action.cancelPreparedCashRepayment" {
  return state.checkout.preparedCollection === "manual-card"
    ? "action.cancelPreparedManualCard"
    : "action.cancelPreparedCashRepayment";
}

function canCancelPreparedCash(state: PaymentPresenterState): boolean {
  return (
    state.checkout.flow === "installment-repayment" &&
    state.allowedActions.cancel &&
    state.tenders.some(
      (tender) => isPreparedCollectionTender(tender),
    )
  );
}

function canSafelyLeave(state: PaymentPresenterState): boolean {
  const installmentCashFence =
    state.checkout.flow === "installment-repayment" &&
    (state.checkout.cashRepaymentStatus === "ready" ||
      state.checkout.cashRepaymentStatus === "confirming" ||
      state.tenders.some(
        (tender) => isPreparedCollectionTender(tender),
      ));
  if (
    !state.initialized ||
    state.busy ||
    state.attemptId !== null ||
    state.allowedActions.recover ||
    installmentCashFence
  ) {
    return false;
  }
  if (!state.orderGuid) return true;
  return (
    state.phase === "cancelled" ||
    state.phase === "declined" ||
    state.phase === "success"
  );
}

function canonicalAttemptCreatedAtMs(
  createdAtIso: string | null,
  nowMs: number,
  windowMs: number,
): number | null {
  if (!createdAtIso) return null;
  const createdAtMs = Date.parse(createdAtIso);
  if (!Number.isFinite(createdAtMs)) return null;
  try {
    // 自动恢复窗口只能由持久化的 canonical UTC ISO 身份决定。
    if (new Date(createdAtMs).toISOString() !== createdAtIso) return null;
  } catch {
    return null;
  }
  if (
    createdAtMs > nowMs ||
    nowMs >= createdAtMs + windowMs
  ) {
    return null;
  }
  return createdAtMs;
}

function autoRecoveryPolicy(
  provider: PaymentPresenterState["provider"],
): Readonly<{ intervalMs: number; maxAttempts: number; windowMs: number }> | null {
  if (provider === "square") {
    return {
      intervalMs: SQUARE_AUTO_RECOVERY_INTERVAL_MS,
      maxAttempts: SQUARE_AUTO_RECOVERY_MAX_ATTEMPTS,
      windowMs: SQUARE_AUTO_RECOVERY_WINDOW_MS,
    };
  }
  if (provider === "linkly-cloud") {
    return {
      intervalMs: LINKLY_AUTO_RECOVERY_INTERVAL_MS,
      maxAttempts: LINKLY_AUTO_RECOVERY_MAX_ATTEMPTS,
      windowMs: LINKLY_AUTO_RECOVERY_WINDOW_MS,
    };
  }
  if (provider === "zeller") {
    return {
      intervalMs: ZELLER_AUTO_RECOVERY_INTERVAL_MS,
      maxAttempts: ZELLER_AUTO_RECOVERY_MAX_ATTEMPTS,
      windowMs: ZELLER_AUTO_RECOVERY_WINDOW_MS,
    };
  }
  return null;
}

function linklyGraphicCopyKey(graphicCode: string): PaymentCopyKey {
  const normalized = graphicCode.trim().toLowerCase();
  if (normalized.includes("card") || normalized.includes("tap")) {
    return "terminal.graphicCard";
  }
  return "terminal.graphicTerminal";
}

function shortIdentifier(value: string): string {
  return value.length > 12 ? `${value.slice(0, 12)}…` : value;
}

/** 货币前缀（$ 或 AU$）：键盘输入中的金额与格式化金额用同一个货币符号。 */
function audCurrencyPrefix(locale: PaymentLocale): string {
  return formatAud(0, locale).replace(/[\d.,\s ]+$/u, "");
}

export function formatAud(cents: number, locale: PaymentLocale): string {
  return new Intl.NumberFormat(locale === "zh" ? "zh-AU" : "en-AU", {
    style: "currency",
    currency: "AUD",
    minimumFractionDigits: 2,
  }).format(cents / 100);
}

export function isSafeLinklyUiKey(
  key: string,
): key is LinklySafeOperatorKey {
  return LINKLY_SAFE_OPERATOR_KEYS.includes(
    key as LinklySafeOperatorKey,
  );
}

export function nextKeypadAmount(
  current: string,
  key: "0" | "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "." | "backspace",
): string {
  if (key === "backspace") return current.slice(0, -1);
  if (key === ".") {
    if (current.includes(".")) return current;
    return current ? `${current}.` : "0.";
  }
  const base = /^\d+\.\d{2}$/u.test(current) ? "" : current;
  const candidate = base === "0" ? key : `${base}${key}`;
  if (!/^\d{0,9}(?:\.\d{0,2})?$/u.test(candidate)) return current;
  return candidate;
}

const styles = createPosStyles((colors) => ({
  contentScroll: {
    flex: 1,
  },
  scrollContent: {
    flexGrow: 1,
    padding: 12,
    gap: 12,
  },
  scrollContentBounded: {
    flex: 1,
  },

  /* ---------- 错误提示条 ---------- */
  errorBanner: {
    minHeight: 64,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.redBorder,
    backgroundColor: colors.redSoft,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  errorText: {
    flex: 1,
    color: colors.red,
    fontSize: 16,
    fontWeight: "800",
    lineHeight: 22,
  },

  /* ---------- 三栏网格（28 : 42 : 30，共用行线） ---------- */
  workspace: {
    flex: 1,
    minHeight: 0,
    flexDirection: "row",
  },
  workspaceCompact: {
    flex: 0,
    flexDirection: "column",
    gap: 12,
  },
  workspaceColumn: {
    minWidth: 0,
    flexDirection: "column",
  },
  workspaceLeft: {
    flex: 28,
  },
  workspaceMiddle: {
    flex: 42,
  },
  workspaceRight: {
    flex: 30,
  },
  card: {
    borderRadius: 14,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    overflow: "hidden",
  },
  cardTitle: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
  },

  /* ---------- 左栏：订单卡 + 汇总 ---------- */
  orderCard: {
    minHeight: 0,
  },
  orderCardCompact: {
    maxHeight: 520,
  },
  orderHeader: {
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  orderCount: {
    flex: 1,
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
    textAlign: "right",
  },
  orderRecoveryEntry: {
    paddingHorizontal: 12,
    paddingTop: 10,
  },
  orderMeta: {
    paddingHorizontal: 14,
    paddingTop: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 10,
  },
  contextEyebrow: {
    color: colors.accent,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 0.7,
    textTransform: "uppercase",
  },
  orderReference: {
    flexShrink: 1,
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  customerScroll: {
    flexGrow: 0,
    flexShrink: 1,
    minHeight: 0,
    marginTop: 8,
    borderTopWidth: 1,
    borderBottomWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surfaceAlt,
  },
  customerScrollContent: {
    flexGrow: 0,
  },
  customerCard: {
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  customerHeading: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  customerTitle: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  customerNumber: {
    marginTop: 2,
    color: colors.accent,
    fontSize: 13,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  customerValue: {
    marginTop: 4,
    color: colors.ink,
    fontSize: 16,
    fontWeight: "700",
  },
  customerEditor: {
    marginTop: 10,
    gap: 8,
  },
  customerInput: {
    minHeight: 56,
    paddingHorizontal: 14,
    borderRadius: 10,
    borderWidth: 1.5,
    borderColor: colors.borderStrong,
    backgroundColor: colors.surface,
    color: colors.ink,
    fontSize: 17,
    fontWeight: "700",
  },
  customerEditorActions: {
    flexDirection: "row",
    gap: 8,
  },
  customerEditorButton: {
    flex: 1,
  },
  contextLines: {
    flex: 1,
    minHeight: 0,
    paddingHorizontal: 14,
  },
  contextLinesCompact: {
    flex: 0,
  },
  contextLine: {
    minHeight: 56,
    paddingVertical: 8,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
    borderStyle: "dashed",
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  contextLineCopy: {
    flex: 1,
    minWidth: 0,
  },
  contextLineName: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "700",
  },
  contextLineQuantity: {
    minWidth: 34,
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
    textAlign: "center",
    fontVariant: ["tabular-nums"],
  },
  contextLineAmount: {
    minWidth: 68,
    color: colors.ink,
    fontSize: 15,
    fontWeight: "900",
    textAlign: "right",
    fontVariant: ["tabular-nums"],
  },
  contextEmpty: {
    paddingVertical: 18,
    color: colors.mutedInk,
    fontSize: 15,
    lineHeight: 21,
  },
  leaveHint: {
    marginHorizontal: 12,
    marginBottom: 10,
    paddingHorizontal: 10,
    paddingVertical: 8,
    borderRadius: 9,
    backgroundColor: colors.yellowSoft,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  leaveHintText: {
    flex: 1,
    color: colors.yellowInk,
    fontSize: 12.5,
    fontWeight: "800",
  },
  orderSummary: {
    paddingHorizontal: 16,
    paddingVertical: 8,
    justifyContent: "center",
    gap: 2,
  },
  orderSummaryRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 10,
  },
  orderSummaryLabel: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
  },
  orderSummaryValue: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  orderSummaryDue: {
    marginTop: 4,
    paddingTop: 4,
    borderTopWidth: 1,
    borderTopColor: colors.border,
    flexDirection: "row",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: 10,
  },
  orderSummaryDueLabel: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "900",
  },
  orderSummaryDueValue: {
    flexShrink: 1,
    color: colors.ink,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  summaryAmountRow: {
    minHeight: 34,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 16,
  },
  summaryLabel: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
  },
  summaryAmount: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  summaryAmountStrong: {
    fontSize: 19,
    fontWeight: "900",
  },
  summaryAmountEmphasis: {
    color: colors.accent,
    fontSize: 32,
    fontWeight: "900",
  },

  /* ---------- 中栏：剩余应付 / 找零 ---------- */
  amountsRow: {
    flexDirection: "row",
  },
  amountCard: {
    paddingHorizontal: 18,
    paddingVertical: 10,
    borderRadius: 14,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    justifyContent: "center",
  },
  remainingCard: {
    flex: 3,
  },
  changeCard: {
    flex: 2,
  },
  // 有找零时找零卡变橙：固定橙色字面量，避免主题把主操作色换掉后找零不再醒目。
  changeCardActive: {
    borderColor: "#FDBA74",
    backgroundColor: "#FFEDD5",
  },
  amountCardLabel: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "900",
  },
  remainingValue: {
    color: colors.ink,
    fontWeight: "900",
    letterSpacing: -1,
    fontVariant: ["tabular-nums"],
  },
  remainingValuePaid: {
    color: colors.green,
  },
  changeValue: {
    color: colors.mutedInk,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  changeTextActive: {
    color: "#C2410C",
  },

  /* ---------- 中栏：当前支付金额 + 快捷现金 + 键盘 ---------- */
  amountRow: {
    flexDirection: "row",
  },
  amountField: {
    flex: 1,
    paddingHorizontal: 16,
    borderRadius: 12,
    borderWidth: 2,
    borderColor: colors.accent,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  amountFieldDisabled: {
    borderColor: colors.disabledBorder,
    backgroundColor: colors.disabledSurface,
  },
  amountFieldLabel: {
    flexShrink: 0,
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
  },
  amountFieldValue: {
    flex: 1,
    color: colors.ink,
    fontWeight: "900",
    textAlign: "right",
    fontVariant: ["tabular-nums"],
  },
  amountFieldPlaceholder: {
    color: colors.disabledInk,
  },
  amountCaret: {
    width: 2,
    height: "50%",
    backgroundColor: colors.accent,
  },
  backspaceKey: {
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    alignItems: "center",
    justifyContent: "center",
  },
  keyDisabled: {
    borderColor: colors.disabledBorder,
    backgroundColor: colors.disabledSurface,
  },
  quickCashRow: {
    flexDirection: "row",
  },
  note: {
    flex: 1,
    minWidth: PAYMENT_MIN_TOUCH_TARGET,
    paddingRight: 10,
    borderRadius: 10,
    borderWidth: 1.5,
    overflow: "hidden",
    alignItems: "flex-end",
    justifyContent: "center",
  },
  noteDisabled: {
    opacity: 0.45,
  },
  // 斜向深色块叠在浅色底上，模拟纸币的渐变底色。
  noteShade: {
    position: "absolute",
    top: "-70%",
    right: "-40%",
    width: "85%",
    height: "240%",
    opacity: 0.55,
    transform: [{ rotate: "28deg" }],
  },
  // 仿聚合物纸币的透明窗口。
  noteWindow: {
    position: "absolute",
    left: 8,
    top: 8,
    bottom: 8,
    width: "18%",
    borderRadius: 6,
    borderWidth: 1,
    borderColor: "rgba(255,255,255,0.9)",
    backgroundColor: "rgba(255,255,255,0.6)",
  },
  noteAud: {
    position: "absolute",
    top: 6,
    left: "27%",
    fontSize: 10,
    fontWeight: "900",
    letterSpacing: 1,
    opacity: 0.75,
  },
  noteValue: {
    fontWeight: "900",
    letterSpacing: -0.5,
    fontVariant: ["tabular-nums"],
  },
  keypad: {
    flexDirection: "column",
  },
  keypadRow: {
    flexDirection: "row",
  },
  keypadKey: {
    flex: 1,
    minHeight: PAYMENT_KEY_ROW_MIN,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    alignItems: "center",
    justifyContent: "center",
  },
  keypadClear: {
    borderColor: colors.redBorder,
    backgroundColor: colors.redSoft,
  },
  keypadKeyText: {
    color: colors.ink,
    fontSize: 30,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  keypadClearText: {
    color: colors.red,
    fontSize: 20,
    fontWeight: "900",
  },
  keypadKeyTextDisabled: {
    color: colors.disabledInk,
  },

  /* ---------- 右栏：付款方式 ---------- */
  methodsCard: {
    padding: 12,
  },
  methodsHeader: {
    minHeight: 24,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  terminalChip: {
    flexShrink: 1,
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: 8,
    backgroundColor: colors.blueSoft,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
  },
  terminalChipError: {
    backgroundColor: colors.redSoft,
  },
  terminalChipText: {
    color: colors.blue,
    fontSize: 12,
    fontWeight: "800",
  },
  terminalChipName: {
    flexShrink: 1,
    color: colors.blue,
    fontSize: 12,
    fontWeight: "900",
  },
  terminalChipErrorText: {
    flexShrink: 1,
    color: colors.red,
    fontSize: 12,
    fontWeight: "800",
  },
  methodTiles: {
    flex: 1,
    minHeight: 0,
  },
  methodTilesCompact: {
    flex: 0,
  },
  methodTileRow: {
    flex: 1,
    minHeight: PAYMENT_KEY_ROW_MIN,
    flexDirection: "row",
  },
  methodTile: {
    flex: 1,
    minHeight: PAYMENT_KEY_ROW_MIN,
    paddingHorizontal: 8,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
  },
  methodTileCash: {
    borderColor: colors.greenBorder,
    backgroundColor: colors.greenSoft,
  },
  methodTileCard: {
    borderColor: colors.blueBorder,
    backgroundColor: colors.blueSoft,
  },
  methodTileVoucher: {
    borderColor: colors.yellowBorder,
    backgroundColor: colors.yellowSoft,
  },
  methodTileDisabled: {
    borderColor: colors.disabledBorder,
    backgroundColor: colors.disabledSurface,
  },
  methodTileText: {
    fontWeight: "900",
  },
  methodBadge: {
    position: "absolute",
    top: 6,
    right: 6,
    maxWidth: "70%",
    paddingHorizontal: 5,
    borderRadius: 5,
    backgroundColor: colors.blue,
  },
  methodBadgeText: {
    color: colors.onAccent,
    fontSize: 11,
    fontWeight: "900",
  },
  methodTextDisabled: {
    color: colors.disabledInk,
  },
  installmentToggle: {
    minHeight: PAYMENT_SMALL_TARGET,
    paddingHorizontal: 12,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surfaceAlt,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  installmentToggleLocked: {
    borderColor: colors.disabledBorder,
    backgroundColor: colors.disabledSurface,
  },
  installmentToggleText: {
    flex: 1,
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
  },
  installmentToggleTrack: {
    width: 44,
    height: 26,
    padding: 3,
    borderRadius: 13,
    backgroundColor: colors.border,
    justifyContent: "center",
  },
  installmentToggleTrackEnabled: {
    backgroundColor: colors.green,
  },
  installmentToggleThumb: {
    width: 20,
    height: 20,
    borderRadius: 10,
    backgroundColor: colors.surface,
  },
  installmentToggleThumbEnabled: {
    alignSelf: "flex-end",
  },

  /* ---------- 右栏：已添加支付 ---------- */
  tendersCard: {
    minHeight: 0,
    padding: 12,
    gap: 8,
  },
  tendersScroll: {
    flex: 1,
    minHeight: 0,
  },
  tendersContent: {
    flexGrow: 1,
    gap: 8,
  },
  tenderRow: {
    minHeight: 64,
    paddingLeft: 12,
    paddingRight: 6,
    paddingVertical: 4,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surfaceAlt,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  tenderIdentity: {
    flex: 1,
    minWidth: 0,
  },
  tenderMethod: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "900",
  },
  tenderDisposition: {
    marginTop: 2,
    color: colors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
  },
  tenderAmount: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  tenderRemove: {
    width: PAYMENT_SMALL_TARGET,
    height: PAYMENT_SMALL_TARGET,
    borderRadius: 10,
    borderWidth: 1.5,
    borderColor: colors.redBorder,
    backgroundColor: colors.surface,
    alignItems: "center",
    justifyContent: "center",
  },
  tenderLocked: {
    width: PAYMENT_SMALL_TARGET,
    height: PAYMENT_SMALL_TARGET,
    alignItems: "center",
    justifyContent: "center",
  },
  emptyTenders: {
    flexGrow: 1,
    minHeight: 78,
    padding: 14,
    alignItems: "center",
    justifyContent: "center",
  },
  emptyTendersText: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 22,
    textAlign: "center",
  },
  cashSettlement: {
    paddingHorizontal: 14,
    paddingVertical: 4,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.greenBorder,
    backgroundColor: colors.greenSoft,
  },
  statusPanel: {
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.blueBorder,
    backgroundColor: colors.blueSoft,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  statusPanelWarning: {
    borderColor: colors.yellowBorder,
    backgroundColor: colors.yellowSoft,
  },
  statusPanelDanger: {
    borderColor: colors.redBorder,
    backgroundColor: colors.redSoft,
  },
  statusPanelSuccess: {
    borderColor: colors.greenBorder,
    backgroundColor: colors.greenSoft,
  },
  statusCopy: {
    flex: 1,
  },
  statusTitle: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  statusTitleWarning: {
    color: colors.yellowInk,
  },
  statusTitleDanger: {
    color: colors.red,
  },
  statusTitleSuccess: {
    color: colors.green,
  },
  statusWaiting: {
    marginTop: 2,
    color: colors.blue,
    fontSize: 15,
    fontWeight: "900",
  },
  statusHint: {
    marginTop: 2,
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "700",
    lineHeight: 18,
  },
  fieldError: {
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: colors.redBorder,
    backgroundColor: colors.redSoft,
    color: colors.red,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 21,
  },
  preparedHint: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 22,
  },
  recoveryActions: {
    gap: 10,
  },
  sectionTitle: {
    color: colors.ink,
    fontSize: 18,
    fontWeight: "900",
  },
  inputLabel: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  inputHint: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 18,
  },
  linklyPanel: {
    paddingHorizontal: 14,
    paddingVertical: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    gap: 6,
  },
  linklyDisplayText: {
    color: colors.ink,
    fontSize: 18,
    fontWeight: "900",
    lineHeight: 25,
  },
  linklyPromptText: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  linklyKeyGrid: {
    marginTop: 6,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  linklyConfirmation: {
    marginTop: 6,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  linklyKey: {
    flexGrow: 1,
    flexBasis: "46%",
  },

  /* ---------- 右栏：完成区 ---------- */
  finishArea: {
    justifyContent: "flex-end",
    gap: 6,
  },
  finishLine: {
    paddingHorizontal: 4,
    flexDirection: "row",
    justifyContent: "space-between",
    gap: 8,
  },
  finishLineText: {
    flexShrink: 1,
    color: colors.ink,
    fontSize: 13,
    fontWeight: "800",
  },
  finishLineStatus: {
    flexShrink: 1,
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
    textAlign: "right",
  },
  finishButtons: {
    flexDirection: "row",
  },
  finishCancel: {
    flex: 1,
  },
  finishPrimary: {
    flex: 1.8,
  },

  /* ---------- 购物券 / 手动刷卡弹窗 ---------- */
  modalScroll: {
    flexGrow: 0,
    width: "100%",
    maxWidth: 560,
  },
  modalScrollContent: {
    flexGrow: 0,
  },
  voucherInputGroup: {
    marginTop: 14,
    gap: 6,
  },
  voucherInput: {
    minHeight: 64,
    paddingHorizontal: 16,
    borderRadius: 10,
    borderWidth: 1.5,
    borderColor: colors.borderStrong,
    backgroundColor: colors.surface,
    color: colors.ink,
    fontSize: 20,
    fontWeight: "800",
  },
  secureCapture: {
    color: colors.green,
    fontSize: 14,
    fontWeight: "800",
  },
  manualCardConfirmation: {
    gap: 10,
  },
  manualCardInstructions: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "700",
    lineHeight: 25,
  },
  manualCardCheckRow: {
    minHeight: 72,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderRadius: 10,
    borderWidth: 1.5,
    borderColor: colors.borderStrong,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  manualCardCheckRowChecked: {
    borderColor: colors.green,
    backgroundColor: colors.greenSoft,
  },
  manualCardCheckLabel: {
    flex: 1,
    color: colors.ink,
    fontSize: 17,
    fontWeight: "800",
    lineHeight: 24,
  },
  /* ---------- 支付完成 ---------- */
  successLayout: {
    flex: 1,
    minHeight: 0,
    flexDirection: "row",
    gap: 12,
  },
  successLayoutCompact: {
    flex: 0,
    flexDirection: "column",
  },
  successMain: {
    flex: 1,
    minWidth: 0,
    minHeight: 0,
  },
  successMainCompact: {
    flex: 0,
  },
  successMainContent: {
    flexGrow: 1,
    gap: 12,
  },
  successHero: {
    minHeight: 118,
    paddingHorizontal: 20,
    paddingVertical: 16,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.greenBorder,
    backgroundColor: colors.greenSoft,
    flexDirection: "row",
    alignItems: "center",
    gap: 18,
  },
  successHeroCopy: {
    flex: 1,
    minWidth: 0,
  },
  successTitle: {
    color: colors.green,
    fontSize: 32,
    fontWeight: "900",
    letterSpacing: -0.5,
  },
  successHint: {
    marginTop: 2,
    color: colors.ink,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
  },
  successTotal: {
    flexShrink: 1,
    alignItems: "flex-end",
  },
  successLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  successTotalAmount: {
    color: colors.ink,
    fontSize: 38,
    fontWeight: "900",
    letterSpacing: -1,
    lineHeight: 44,
    fontVariant: ["tabular-nums"],
  },
  successSettlement: {
    paddingHorizontal: 20,
    paddingVertical: 14,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    gap: 22,
  },
  successCashTendered: {
    flex: 1,
    minWidth: 0,
  },
  successCashTenderedLabel: {
    color: colors.mutedInk,
    fontSize: 16,
    fontWeight: "800",
  },
  successCashTenderedAmount: {
    marginTop: 2,
    color: colors.ink,
    fontSize: 34,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  successSettlementRule: {
    width: 1,
    height: 76,
    backgroundColor: colors.border,
  },
  successChange: {
    flex: 1.4,
    minWidth: 0,
    alignItems: "flex-end",
  },
  successChangeLabel: {
    color: colors.accent,
    fontSize: 18,
    fontWeight: "900",
  },
  successChangeAmount: {
    color: colors.accent,
    fontSize: 72,
    fontWeight: "900",
    letterSpacing: -2,
    lineHeight: 78,
    fontVariant: ["tabular-nums"],
  },
  successTransaction: {
    flexGrow: 1,
    minHeight: 190,
    paddingHorizontal: 20,
    paddingVertical: 16,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    gap: 18,
  },
  successTransactionCopy: {
    flex: 1,
    minWidth: 0,
  },
  successOrderReference: {
    marginTop: 4,
    color: colors.ink,
    fontSize: 19,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
    lineHeight: 25,
  },
  successTransactionQr: {
    flexShrink: 0,
    alignItems: "center",
    justifyContent: "center",
  },
  // 二维码必须白底黑码，不随主题变化，否则深色主题下扫不出来。
  successTransactionQrQuietZone: {
    backgroundColor: "#FFFFFF",
  },
  successTransactionQrRow: {
    flexDirection: "row",
  },
  successTransactionQrCell: {
    backgroundColor: "#FFFFFF",
  },
  successTransactionQrCellDark: {
    backgroundColor: "#111111",
  },
  successSync: {
    marginTop: 14,
    alignSelf: "flex-start",
    maxWidth: "100%",
    paddingHorizontal: 12,
    paddingVertical: 9,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: colors.greenBorder,
    backgroundColor: colors.greenSoft,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  successSyncText: {
    flexShrink: 1,
    color: colors.green,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 21,
  },
  successReceiptColumn: {
    width: 430,
    minHeight: 0,
    gap: 12,
  },
  successReceiptColumnCompact: {
    width: "100%",
  },
  successReceipt: {
    flex: 1,
    minHeight: 220,
    paddingHorizontal: 16,
    paddingVertical: 14,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    gap: 8,
  },
  successReceiptHeader: {
    flexDirection: "row",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: 12,
  },
  successReceiptTitle: {
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
  },
  successReceiptOrder: {
    flexShrink: 1,
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    fontVariant: ["tabular-nums"],
  },
  successReceiptLines: {
    flex: 1,
    minHeight: 60,
  },
  successReceiptLine: {
    minHeight: 46,
    paddingVertical: 8,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  successReceiptLineCopy: {
    flex: 1,
    minWidth: 0,
    flexDirection: "row",
    alignItems: "baseline",
    gap: 6,
  },
  successReceiptLineName: {
    flexShrink: 1,
    color: colors.ink,
    fontSize: 15,
    fontWeight: "700",
  },
  successReceiptLineQuantity: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "700",
    fontVariant: ["tabular-nums"],
  },
  successReceiptLineAmount: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  successReceiptEmpty: {
    paddingVertical: 16,
    color: colors.mutedInk,
    fontSize: 15,
    lineHeight: 21,
  },
  successReceiptTotals: {
    paddingHorizontal: 14,
    paddingVertical: 4,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surfaceAlt,
  },
  successReceiptTender: {
    minHeight: 38,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  successReceiptTenderLabel: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
  },
  successReceiptTenderAmount: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  successReceiptTotalLabel: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  successReceiptTotalAmount: {
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  successPrintStatus: {
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 21,
  },
  successPrintStatusPrinting: {
    color: colors.blue,
  },
  successPrintStatusCompleted: {
    color: colors.green,
  },
  successPrintStatusUnknown: {
    color: colors.yellowInk,
  },
  successPrintStatusFailed: {
    color: colors.red,
  },

  /* ---------- 确认弹窗 ---------- */
  confirmationBackdrop: {
    flex: 1,
    padding: 24,
    backgroundColor: colors.overlay,
    alignItems: "center",
    justifyContent: "center",
  },
  backdropDismissArea: {
    ...StyleSheet.absoluteFillObject,
  },
  confirmationCard: {
    width: "100%",
    maxWidth: 560,
    padding: 24,
    borderRadius: 16,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
  },
  confirmationTitle: {
    color: colors.ink,
    fontSize: 23,
    fontWeight: "900",
  },
  confirmationBody: {
    marginTop: 10,
    color: colors.ink,
    fontSize: 17,
    lineHeight: 25,
  },
  confirmationActions: {
    marginTop: 20,
    flexDirection: "row",
    gap: 12,
  },
  confirmationAction: {
    flex: 1,
  },

  disabled: {
    opacity: 0.42,
  },
  pressed: {
    opacity: 0.76,
  },
}));
