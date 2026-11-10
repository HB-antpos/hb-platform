import { MaterialCommunityIcons } from "@expo/vector-icons";
import {
  Fragment,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
  type RefObject,
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
  canSubmitPaymentMethod,
  isQuickCashSufficient,
  nextQuickCashAmountText,
  parseAudInput,
  type PaymentPresenterState,
  type PaymentPresenterTender,
  type PaymentScreenPresenter,
  type PaymentUiMethod,
  type PaymentUiPhase,
} from "./payment-presenter";

import { INSTALLMENT_MINIMUM_TOTAL_CENTS } from "@/features/installments/installment-presenter";

import { receiptQrMatrix } from "@hb/pos-receipt-core/features/local-history/receipt-qr-matrix";
import type {
  LinklySafeOperatorKey,
} from "@/features/payments/runtime/linkly-operator-runtime";
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
import { PosTextInput } from "@/ui/controls/pos-text-input";
import { PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  usePosThemeVersion,
  type PosPalette,
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

const PAYMENT_METHODS = Object.freeze([
  "cash",
  "square",
  "linkly-cloud",
  "zeller",
  "manual-card",
  "voucher",
] as const satisfies readonly PaymentUiMethod[]);

/** 付款方式大块的图标、图标颜色（存色名，渲染时再取当前主题色）和一句话说明。 */
const PAYMENT_METHOD_VISUALS: Readonly<
  Record<
    PaymentUiMethod,
    Readonly<{
      icon: PosIconName;
      color: keyof PosPalette;
      hintKey: PaymentCopyKey;
    }>
  >
> = Object.freeze({
  cash: { icon: "cash", color: "green", hintKey: "method.cash.hint" },
  square: {
    icon: "square-rounded-outline",
    color: "ink",
    hintKey: "method.square.hint",
  },
  "linkly-cloud": {
    icon: "credit-card-outline",
    color: "blue",
    hintKey: "method.linkly-cloud.hint",
  },
  zeller: {
    icon: "contactless-payment",
    color: "ink",
    hintKey: "method.zeller.hint",
  },
  "manual-card": {
    icon: "credit-card-check-outline",
    color: "ink",
    hintKey: "method.manual-card.hint",
  },
  voucher: {
    icon: "wallet-giftcard",
    color: "yellow",
    hintKey: "method.voucher.hint",
  },
});

/** 纸币快捷键面额（澳元纸币）。 */
const QUICK_CASH_NOTES = [5, 10, 20, 50, 100] as const;

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
  const { width } = useWindowDimensions();
  // 应用只支持横屏 iPad；宽度不足 900 时退回单列（分屏、测试夹具），三栏整体可滚动。
  const compact = width < 900;
  // 1024 宽的旧款 iPad：左右两栏收窄一点，给中间的付款方式留出两列。
  const narrowLandscape = !compact && width < 1100;
  const [
    fullInstallmentConfirmationOpen,
    setFullInstallmentConfirmationOpen,
  ] = useState(false);
  const [
    preparedCashCancellationOpen,
    setPreparedCashCancellationOpen,
  ] = useState(false);
  const [paymentEntryOpen, setPaymentEntryOpen] = useState(false);
  // Lay-by 总额不足起订金额的页内提示（对齐 WPF #891）：按提示时的订单金额记住“已关闭”，金额变化后再次提示。
  const [dismissedMinimumNoticeCents, setDismissedMinimumNoticeCents] =
    useState<number | null>(null);
  const installmentMinimumNoticeOpen =
    state.fieldIssue === "installment-total-below-minimum" &&
    state.checkout.flow === "installment-create" &&
    dismissedMinimumNoticeCents !== state.total.cents;
  const latestState = useRef(state);
  latestState.current = state;
  // 上一次快捷现金写入输入框的文本：输入框仍等于它时，再点快捷金额是累加而不是覆盖。
  const lastQuickCashText = useRef<string | null>(null);

  const closePaymentEntry = (): void => {
    if (state.busy) return;
    presenter.setManualCardConfirmed?.(false);
    setPaymentEntryOpen(false);
  };

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

  useEffect(() => {
    if (!showEntry || !state.selectedMethod) {
      setPaymentEntryOpen(false);
    }
  }, [showEntry, state.selectedMethod]);

  // 右栏金额录入是否“已点选付款方式、可以输入”。
  // 开合条件与原金额弹窗完全一致：点付款方式且 presenter 接受后打开；提交成功、取消录入、
  // 或当前状态不再允许录入时关闭。键盘常驻右栏只是换了显示位置，没有改变任何业务调用顺序。
  const entryArmed = paymentEntryOpen && showEntry;

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
        ) : (
          <View
            style={[
              styles.workspace,
              compact && styles.workspaceCompact,
            ]}
            testID="payment-workspace"
          >
            <PaymentContextPane
              canLeave={canLeave}
              compact={compact}
              locale={locale}
              narrow={narrowLandscape}
              onBack={onBack}
              onOpenRecoveryCenter={onOpenRecoveryCenter}
              presenter={presenter}
              state={state}
              t={t}
            />
            <PaymentSummary
              compact={compact}
              entryArmed={entryArmed}
              installmentModeControl={installmentModeControl}
              locale={locale}
              onSelectMethod={(method) => {
                // 只有 presenter 接受了这次选择，右栏才进入可输入状态。
                if (presenter.selectMethod(method)) {
                  setPaymentEntryOpen(true);
                }
              }}
              presenter={presenter}
              showStatusPanel={showStatusPanel}
              state={state}
              t={t}
            />
            <PaymentEntryPane
              compact={compact}
              entryArmed={entryArmed}
              lastQuickCashText={lastQuickCashText}
              locale={locale}
              narrow={narrowLandscape}
              onCancelEntry={closePaymentEntry}
              onCancelPreparedCash={() => {
                setPreparedCashCancellationOpen(true);
              }}
              onConfirm={() => {
                const customer =
                  state.checkout.installmentCustomer;
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
              }}
              onSubmit={() => {
                void presenter.submitSelected().then((submitted) => {
                  // 提交成功后退出录入状态，下一笔必须重新点选付款方式。
                  if (submitted) setPaymentEntryOpen(false);
                });
              }}
              presenter={presenter}
              showEntry={showEntry}
              state={state}
              t={t}
            />
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

/** 左栏：返回收银、订单明细与合计、取消支付。返回和取消上下分开放，避免误触。 */
function PaymentContextPane({
  canLeave,
  compact,
  locale,
  narrow,
  onBack,
  onOpenRecoveryCenter,
  presenter,
  state,
  t,
}: Readonly<{
  canLeave: boolean;
  compact: boolean;
  locale: PaymentLocale;
  narrow: boolean;
  onBack: (() => void) | undefined;
  onOpenRecoveryCenter: (() => void) | undefined;
  presenter: PaymentScreenPresenter;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const customer = state.checkout.installmentCustomer;
  const paidCents = Math.max(
    0,
    state.total.cents - state.remaining.cents,
  );
  const recovery = recoveryActionFlags(state, presenter);
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
        styles.contextPane,
        narrow && styles.contextPaneNarrow,
        compact && styles.contextPaneCompact,
      ]}
      testID="payment-context-pane"
    >
      {onBack ? (
        <ActionButton
          disabled={!canLeave}
          icon="arrow-left"
          label={locale === "zh" ? "返回收银" : "Back to sale"}
          onPress={onBack}
          size="button"
          sound="navigate"
          testID="payment-back"
          tone="secondary"
        />
      ) : null}
      {onOpenRecoveryCenter && ["recovery-required", "declined", "cancelled"].includes(state.phase) ? (
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
      ) : null}

      <View style={styles.contextCard}>
        <View style={styles.contextCardHeader}>
          <View style={styles.contextCardHeading}>
            <Text style={styles.contextEyebrow}>{flowLabel}</Text>
            <Text style={styles.contextListTitle}>
              {locale === "zh" ? "商品明细" : "Items"}
            </Text>
          </View>
          {state.orderGuid ? (
            <Text numberOfLines={1} style={styles.orderReference}>
              {t("summary.order", {
                order: shortIdentifier(state.orderGuid),
              })}
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
            <View
              style={styles.customerCard}
              testID="payment-installment-customer"
            >
              <View style={styles.customerHeading}>
                <Text style={styles.customerTitle}>
                  {locale === "zh" ? "分期顾客" : "Lay-by customer"}
                </Text>
                {customer.editable &&
                presenter.openInstallmentCustomerEditor ? (
                  <ActionButton
                    disabled={state.busy}
                    icon="pencil-outline"
                    label={locale === "zh" ? "编辑" : "Edit"}
                    onPress={() =>
                      presenter.openInstallmentCustomerEditor?.()
                    }
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
                {customer.name ||
                  (locale === "zh" ? "未填写姓名" : "Name required")}
              </Text>
              <Text style={styles.customerValue}>
                {customer.phone ||
                  (locale === "zh" ? "未填写电话" : "Phone required")}
              </Text>
              {customer.editorOpen ? (
                <View
                  style={styles.customerEditor}
                  testID="payment-customer-editor"
                >
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
                      onPress={() =>
                        presenter.cancelInstallmentCustomerEditor?.()
                      }
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
          scrollEnabled
          style={styles.contextLines}
          testID="payment-context-lines"
        >
          {state.checkout.lines.length ? (
            state.checkout.lines.map((line) => (
              <View key={line.lineKey} style={styles.contextLine}>
                <View style={styles.contextLineCopy}>
                  <Text numberOfLines={2} style={styles.contextLineName}>
                    {line.displayName}
                  </Text>
                  <Text style={styles.contextLineQuantity}>× {line.quantity}</Text>
                </View>
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

        <View style={styles.contextTotals}>
          <SummaryAmount
            label={t("summary.total")}
            locale={locale}
            strong
            value={state.total.cents}
          />
          <SummaryAmount
            label={t("summary.paid")}
            locale={locale}
            value={paidCents}
          />
        </View>
      </View>

      {recovery.showGenericCancellation ? (
        // 取消支付固定在左下角，离右下角的主按钮最远。
        <ActionButton
          disabled={state.busy || state.recoveryInFlight === true}
          icon="close-circle-outline"
          label={t("action.cancel")}
          onPress={() => {
            void presenter.cancel();
          }}
          size="button"
          testID="payment-cancel"
          tone="danger"
        />
      ) : null}
    </View>
  );
}

/** 支付状态说明：并入「剩余应付」卡片，等待、未知、被拒等状态用卡片底色区分。 */
function PaymentStatusPanel({
  state,
  t,
}: Readonly<{
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const tone = statusTone(state.phase);
  return (
    <View
      accessibilityRole="summary"
      style={styles.statusPanel}
      testID={`payment-status-${state.phase}`}
    >
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
  if (!configured || !resource || resource.kind === "unavailable") return null;
  if (resource.kind === "loading") {
    return (
      <View style={styles.linklyTerminalPanel} testID="payment-linkly-terminal-loading">
        <Text style={styles.linklyTerminalHint}>{t("linkly.terminalsLoading")}</Text>
      </View>
    );
  }
  const snapshot = resource.snapshot;
  if (!snapshot || resource.kind === "failed") {
    return (
      <View style={styles.linklyTerminalPanel} testID="payment-linkly-terminal-empty">
        <Text style={styles.linklyTerminalError}>{t("linkly.terminalsFailed")}</Text>
      </View>
    );
  }
  if (snapshot.mode !== "Active") return null;
  // 付款页只展示设置中已选中的线路，不在收款过程中提供重新绑定入口。
  const terminal = snapshot.terminals.find(
    (item) => item.terminalId === snapshot.selectedTerminalId,
  );
  if (!terminal) {
    return (
      <View style={styles.linklyTerminalPanel} testID="payment-linkly-terminal-empty">
        <Text style={styles.linklyTerminalError}>{t("linkly.noTerminals")}</Text>
      </View>
    );
  }
  const status = terminal.isBusy
    ? t("linkly.terminalBusy")
    : terminal.pairingState === "NeedsRepair"
      ? t("linkly.terminalNeedsRepair")
      : terminal.pairingState === "Unpaired"
        ? t("linkly.terminalUnpaired")
        : terminal.isReady && terminal.pairingState === "Ready"
          ? t("linkly.terminalSelected")
          : t("linkly.terminalUnknown");
  return (
    <View style={styles.linklyTerminalPanel} testID="payment-linkly-terminal-current">
      <Text style={styles.linklyTerminalTitle}>{t("linkly.currentTerminal")}</Text>
      <Text style={styles.linklyTerminalName}>
        {`${terminal.displayName} · Lane ${terminal.laneNo}`}
      </Text>
      <Text style={styles.linklyTerminalHint}>{status}</Text>
    </View>
  );
}

/**
 * 右栏动作区里的恢复类操作：恢复支付 / 确认已收 / 主管作废礼品卡 / 尚未收现取消续付。
 * 「取消支付」单独放在左栏底部（见 PaymentContextPane），显示条件由 recoveryActionFlags 统一给出。
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

/** 中栏：剩余应付大数字（含支付状态）+ 付款方式大块 + 当前付款列表。 */
function PaymentSummary({
  compact,
  entryArmed,
  installmentModeControl,
  locale,
  onSelectMethod,
  presenter,
  showStatusPanel,
  state,
  t,
}: Readonly<{
  compact: boolean;
  entryArmed: boolean;
  installmentModeControl: PaymentInstallmentModeControl | undefined;
  locale: PaymentLocale;
  onSelectMethod(method: PaymentUiMethod): void;
  presenter: PaymentScreenPresenter;
  showStatusPanel: boolean;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const tone = showStatusPanel ? statusTone(state.phase) : "neutral";
  return (
    <View
      style={[
        styles.summaryPane,
        compact && styles.summaryPaneCompact,
      ]}
      testID="payment-summary"
    >
      <View
        style={[
          styles.dueCard,
          tone === "warning" && styles.dueCardWarning,
          tone === "danger" && styles.dueCardDanger,
          tone === "success" && styles.dueCardSuccess,
        ]}
        testID="payment-due-card"
      >
        <View style={styles.dueRow}>
          <Text style={styles.dueLabel}>{t("summary.remaining")}</Text>
          <Text
            adjustsFontSizeToFit
            numberOfLines={1}
            style={styles.dueAmount}
            testID="payment-remaining"
          >
            {formatAud(state.remaining.cents, locale)}
          </Text>
        </View>
        {showStatusPanel ? (
          <PaymentStatusPanel state={state} t={t} />
        ) : null}
      </View>

      <ScrollView
        contentContainerStyle={styles.summaryScrollContent}
        nestedScrollEnabled
        scrollEnabled={!compact}
        style={[
          styles.summaryScroll,
          compact && styles.summaryScrollCompact,
        ]}
        testID="payment-summary-scroll"
      >
        <Text style={styles.sectionLabel}>{t("method.title")}</Text>
        <View style={styles.methodGrid}>
          {PAYMENT_METHODS.filter((method) =>
            // 运行时已按设置限制唯一银行卡通道；付款中暂时禁用不影响展示。
            method === "manual-card" &&
            state.checkout.flow !== "regular" &&
            state.checkout.flow !== "installment-repayment"
              ? false
              : method === "cash"
              ? state.cashAvailable === true
              : state.providers.some(
                  (entry) => entry.provider === method && entry.available,
                ),
          ).map((method) => {
            const active = state.selectedMethod === method;
            const disabled = !canSelectPaymentMethod(state, method);
            return (
              <PaymentMethodButton
                active={active}
                disabled={disabled}
                // 橙框只在右栏正在为这个方式录入金额时出现，和“可以输入”保持同一个含义。
                highlighted={active && entryArmed}
                hint={t(
                  disabled && !state.busy
                    ? "method.unavailable"
                    : PAYMENT_METHOD_VISUALS[method].hintKey,
                )}
                icon={PAYMENT_METHOD_VISUALS[method].icon}
                iconColor={PAYMENT_METHOD_VISUALS[method].color}
                key={method}
                label={t(paymentMethodCopyKey(method))}
                onPress={() => onSelectMethod(method)}
                testID={`payment-method-${method}`}
              />
            );
          })}
          {installmentModeControl ? (
            <PaymentInstallmentToggle
              control={installmentModeControl}
              t={t}
            />
          ) : null}
        </View>
        <LinklyTerminalSelector state={state} t={t} />

        <Text style={styles.sectionLabel}>{t("summary.tenders")}</Text>
        {state.tenders.length ? (
          <View style={styles.tenderList}>
            {state.tenders.map((tender) => (
              <TenderRow
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
          </View>
        ) : (
          <View style={styles.emptyTenders}>
            <Text style={styles.emptyTendersText}>
              {t("summary.noTenders")}
            </Text>
          </View>
        )}
      </ScrollView>
    </View>
  );
}

/**
 * 右栏：本次金额 + 纸币快捷键 + 数字键盘常驻显示，底部是唯一的橙色主按钮。
 * 没有点选付款方式时键盘置灰；只有 entryArmed 为真才接受输入和提交。
 */
function PaymentEntryPane({
  compact,
  entryArmed,
  lastQuickCashText,
  locale,
  narrow,
  onCancelEntry,
  onCancelPreparedCash,
  onConfirm,
  onSubmit,
  presenter,
  showEntry,
  state,
  t,
}: Readonly<{
  compact: boolean;
  entryArmed: boolean;
  lastQuickCashText: RefObject<string | null>;
  locale: PaymentLocale;
  narrow: boolean;
  onCancelEntry(): void;
  onCancelPreparedCash(): void;
  onConfirm(): void;
  onSubmit(): void;
  presenter: PaymentScreenPresenter;
  showEntry: boolean;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  usePosThemeVersion();
  const flags = recoveryActionFlags(state, presenter);
  const preparedCashCancellation = canCancelPreparedCash(state);
  const showPreparedCashCancellationInFooter =
    preparedCashCancellation && state.phase !== "recovery-required";
  const showConfirmation =
    Boolean(presenter.confirm) &&
    state.checkout.canConfirm &&
    !(
      state.phase === "recovery-required" &&
      state.checkout.cashRepaymentStatus === "ready"
    );
  const showFooter = showConfirmation || showPreparedCashCancellationInFooter;
  // 每屏只有一个橙色主按钮：有待确认的收款时是「确认」，否则是右下角的提交，
  // 两者都没有时（等待终端、需要恢复等）才轮到恢复类操作。
  const primaryOwner: "confirm" | "submit" | "recovery" = showConfirmation
    ? "confirm"
    : showEntry
      ? "submit"
      : "recovery";
  const salesManualCard = isSalesManualCard(state);
  const manualCardEntry = entryArmed && salesManualCard;
  // 纸币快捷键：录入现金时可用；尚未点选付款方式时只要有现金权限就置灰占位，保持右栏布局稳定。
  const showQuickCash = entryArmed
    ? state.selectedMethod === "cash"
    : state.cashAvailable === true;
  // 主按钮上带本次金额，按之前再确认一眼；手动刷卡固定为全部剩余应付。
  const submitAmountCents = !entryArmed
    ? null
    : salesManualCard
      ? state.remaining.cents
      : (parseAudInput(state.amountText)?.cents ?? null);
  // 未点选付款方式时按钮文案不跟随 presenter 的默认选择，避免暗示已经选好了方式。
  const submitBaseLabel =
    entryArmed && salesManualCard
      ? t("manualCard.confirm")
      : entryArmed &&
          state.checkout.flow === "installment-repayment" &&
          state.selectedMethod === "cash"
        ? t("action.prepareCashRepayment")
        : state.orderGuid
          ? t("action.addTender")
          : t("action.pay");
  const submitLabel =
    submitAmountCents === null
      ? submitBaseLabel
      : `${submitBaseLabel} · ${formatAud(submitAmountCents, locale)}`;

  return (
    <View
      style={[
        styles.entryPane,
        narrow && styles.entryPaneNarrow,
        compact && styles.entryPaneCompact,
      ]}
      testID="payment-entry-pane"
    >
      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.entryScrollContent}
        nestedScrollEnabled
        scrollEnabled
        style={[
          styles.entryScroll,
          compact && styles.entryScrollCompact,
        ]}
        testID="payment-entry-scroll"
      >
        {showEntry ? (
          <View style={styles.form} testID="payment-entry-form">
            <View
              style={[
                styles.amountBox,
                entryArmed && styles.amountBoxArmed,
              ]}
            >
              <View style={styles.amountBoxMeta}>
                <Text numberOfLines={1} style={styles.amountBoxLabel}>
                  {entryArmed && state.selectedMethod
                    ? `${t("amount.label")} · ${t(
                        paymentMethodCopyKey(state.selectedMethod),
                      )}`
                    : t("amount.label")}
                </Text>
                {entryArmed ? (
                  // 退出本次录入（等同原金额弹窗的关闭）：不发起付款，也不取消订单。
                  <ActionButton
                    disabled={state.busy}
                    icon="close"
                    label={t("entry.cancel")}
                    onPress={onCancelEntry}
                    size="small"
                    sound="danger"
                    testID="payment-entry-cancel"
                    tone="quiet"
                  />
                ) : null}
              </View>
              <View style={styles.amountValueRow}>
                <Text
                  style={[
                    styles.amountCurrency,
                    !entryArmed && styles.amountCurrencyIdle,
                  ]}
                >
                  $
                </Text>
                <PosTextInput
                  accessibilityLabel={t("amount.label")}
                  editable={entryArmed && !state.busy && !salesManualCard}
                  keyboardType="decimal-pad"
                  onChangeText={(value) => presenter.setAmountText(value)}
                  placeholder="0.00"
                  placeholderTextColor={posColors.placeholder}
                  selectionColor={posColors.blue}
                  showSoftInputOnFocus={false}
                  style={styles.amountInput}
                  testID="payment-amount"
                  value={
                    !entryArmed
                      ? ""
                      : salesManualCard
                        ? (state.remaining.cents / 100).toFixed(2)
                        : state.amountText
                  }
                />
              </View>
            </View>
            <Text style={styles.inputHint}>
              {t(
                !entryArmed
                  ? "entry.selectMethodFirst"
                  : salesManualCard
                    ? "manualCard.amountHint"
                    : "amount.hint",
              )}
            </Text>

            {entryArmed && state.selectedMethod === "voucher" ? (
              <View
                key={state.sensitiveInputRevision}
                style={styles.voucherInputGroup}
              >
                <Text style={styles.inputLabel}>
                  {t("voucher.label")}
                </Text>
                <PosKeyboardAwareTextInput
                  accessibilityLabel={t("voucher.label")}
                  autoCapitalize="characters"
                  autoCorrect={false}
                  editable={!state.busy}
                  onChangeText={(value) =>
                    presenter.setVoucherCode(value)
                  }
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
            ) : null}

            {manualCardEntry ? (
              <View style={styles.manualCardConfirmation} testID="payment-manual-card-confirmation">
                <Text style={styles.manualCardInstructions}>{t("manualCard.instructions")}</Text>
                <PosPressable
                  accessibilityRole="checkbox"
                  accessibilityLabel={t("manualCard.checked")}
                  accessibilityState={{ checked: state.manualCardConfirmed === true, disabled: state.busy }}
                  disabled={state.busy}
                  onPress={() => presenter.setManualCardConfirmed?.(state.manualCardConfirmed !== true)}
                  sound="key"
                  style={({ pressed }) => [
                    styles.manualCardCheckRow,
                    state.manualCardConfirmed === true && styles.manualCardCheckRowChecked,
                    pressed && styles.pressed,
                  ]}
                  testID="payment-manual-card-check"
                >
                  <MaterialCommunityIcons
                    accessible={false}
                    color={state.manualCardConfirmed === true ? posColors.green : posColors.mutedInk}
                    name={state.manualCardConfirmed === true ? "checkbox-marked" : "checkbox-blank-outline"}
                    size={34}
                  />
                  <Text style={styles.manualCardCheckLabel}>{t("manualCard.checked")}</Text>
                </PosPressable>
                <Text style={styles.inputHint}>{t("manualCard.uncertain")}</Text>
              </View>
            ) : (
              <Fragment>
                {showQuickCash ? (
                  <View
                    style={styles.quickCashRow}
                    testID="payment-cash-quick"
                  >
                    {QUICK_CASH_NOTES.map((amount) => (
                      <QuickCashButton
                        amount={amount}
                        disabled={
                          !entryArmed ||
                          state.busy ||
                          isQuickCashSufficient(
                            state.amountText,
                            lastQuickCashText.current,
                            state.remaining,
                          )
                        }
                        key={amount}
                        onPress={() => {
                          const next = nextQuickCashAmountText(
                            state.amountText,
                            lastQuickCashText.current,
                            amount,
                          );
                          lastQuickCashText.current = next;
                          presenter.setAmountText(next);
                        }}
                      />
                    ))}
                  </View>
                ) : null}
                <PaymentKeypad
                  amountText={state.amountText}
                  disabled={!entryArmed || state.busy}
                  onChange={(value) => presenter.setAmountText(value)}
                />
              </Fragment>
            )}
          </View>
        ) : null}

        {state.checkout.cash.tenderedCents > 0 ? (
          // 实收与找零只显示 presenter 给出的结算结果，界面不自己估算（普通销售按五分取整，分期不取整）。
          <View style={styles.cashSettlement} testID="payment-cash-settlement">
            <SummaryAmount
              label={locale === "zh" ? "实收现金" : "Cash tendered"}
              locale={locale}
              value={state.checkout.cash.tenderedCents}
            />
            <SummaryAmount
              label={locale === "zh" ? "入账金额" : "Applied"}
              locale={locale}
              testID="payment-cash-applied"
              value={state.checkout.cash.appliedCents}
            />
            <SummaryAmount
              emphasis
              label={locale === "zh" ? "找零" : "Change"}
              locale={locale}
              testID="payment-change"
              value={state.checkout.cash.changeCents}
            />
          </View>
        ) : null}

        <LinklyControls presenter={presenter} state={state} t={t} />
      </PosKeyboardAwareScrollView>

      {state.fieldIssue ? (
        <Text
          accessibilityRole="alert"
          style={styles.fieldError}
          testID="payment-field-error"
        >
          {t(paymentFieldIssueCopyKey(state.fieldIssue))}
        </Text>
      ) : null}

      {flags.hasDockActions || showFooter ? (
        <View
          style={styles.summaryActionDock}
          testID="payment-summary-action-dock"
        >
          <RecoveryActions
            onCancelPreparedCash={onCancelPreparedCash}
            presenter={presenter}
            primary={primaryOwner === "recovery"}
            state={state}
            t={t}
          />
          {showFooter ? (
            <View style={styles.summaryFooter} testID="payment-summary-footer">
              {state.checkout.preparedCollection === "manual-card" ? (
                <Text style={styles.preparedHint} testID="payment-manual-card-prepared-hint">
                  {t("installmentManualCard.prepared")}
                </Text>
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
                  size="primary"
                  sound="danger"
                  testID="payment-confirm"
                  tone="primary"
                />
              ) : null}
              {showPreparedCashCancellationInFooter ? (
                <ActionButton
                  disabled={state.busy}
                  label={t(cancelPreparedLabelKey(state))}
                  onPress={onCancelPreparedCash}
                  size="button"
                  sound="danger"
                  testID="payment-cancel-prepared-cash"
                  tone="danger"
                />
              ) : null}
            </View>
          ) : null}
        </View>
      ) : null}

      {showEntry ? (
        <View style={styles.formActions} testID="payment-entry-actions">
          <ActionButton
            disabled={
              !entryArmed ||
              state.busy ||
              !state.selectedMethod ||
              !canSubmitPaymentMethod(state, state.selectedMethod)
            }
            icon="check-circle"
            label={submitLabel}
            onPress={onSubmit}
            size={primaryOwner === "submit" ? "primary" : "large"}
            testID="payment-submit"
            tone={primaryOwner === "submit" ? "primary" : "secondary"}
          />
        </View>
      ) : null}
    </View>
  );
}

/** 分期开关：放在付款方式网格里，和其他付款方式同一尺寸。 */
function PaymentInstallmentToggle({
  control,
  t,
}: Readonly<{
  control: PaymentInstallmentModeControl;
  t: Translate;
}>) {
  usePosThemeVersion();
  return (
    <Fragment>
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
          styles.methodButton,
          control.locked && styles.methodButtonDisabled,
          pressed && !control.locked && styles.pressed,
        ]}
        testID="payment-installment-toggle"
      >
        <MaterialCommunityIcons
          accessible={false}
          color={control.locked ? posColors.disabledInk : posColors.ink}
          name="calendar-month-outline"
          size={34}
        />
        <View style={styles.methodCopy}>
          <Text
            numberOfLines={1}
            style={[
              styles.methodButtonText,
              control.locked && styles.methodTextDisabled,
            ]}
          >
            {t("installment.toggle")}
          </Text>
          <Text
            numberOfLines={1}
            style={[
              styles.methodHint,
              control.locked && styles.methodTextDisabled,
            ]}
          >
            {t("installment.toggleHint")}
          </Text>
        </View>
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
      {control.issue ? (
        <Text
          accessibilityRole="alert"
          style={styles.installmentToggleIssue}
        >
          {t("installment.unavailable")}
        </Text>
      ) : null}
    </Fragment>
  );
}

const KEYPAD_ROWS = [
  ["1", "2", "3"],
  ["4", "5", "6"],
  ["7", "8", "9"],
  [".", "0", "backspace"],
] as const;

function PaymentKeypad({
  amountText,
  disabled,
  onChange,
}: Readonly<{
  amountText: string;
  disabled: boolean;
  onChange(value: string): void;
}>) {
  usePosThemeVersion();
  return (
    <View style={styles.keypad} testID="payment-keypad">
      {KEYPAD_ROWS.map((row) => (
        <View key={row[0]} style={styles.keypadRow}>
          {row.map((key) => (
            <PosPressable
              // 退格键改成图标后没有文字，沿用原来的符号作为朗读内容。
              {...(key === "backspace" ? { accessibilityLabel: "⌫" } : {})}
              accessibilityRole="button"
              disabled={disabled}
              key={key}
              onPress={() =>
                onChange(nextKeypadAmount(amountText, key))
              }
              sound={key === "backspace" ? "danger" : "key"}
              style={({ pressed }) => [
                styles.keypadKey,
                disabled && styles.keypadKeyDisabled,
                pressed && !disabled && styles.pressed,
              ]}
              testID={`payment-key-${
                key === "." ? "decimal" : key
              }`}
            >
              {key === "backspace" ? (
                <MaterialCommunityIcons
                  accessible={false}
                  color={disabled ? posColors.disabledInk : posColors.ink}
                  name="backspace-outline"
                  size={30}
                />
              ) : (
                <Text
                  style={[
                    styles.keypadKeyText,
                    disabled && styles.keypadKeyTextDisabled,
                  ]}
                >
                  {key}
                </Text>
              )}
            </PosPressable>
          ))}
        </View>
      ))}
    </View>
  );
}

/** 纸币快捷键：底色对应真实纸币颜色，不随主题变化。 */
function QuickCashButton({
  amount,
  disabled,
  onPress,
}: Readonly<{
  amount: (typeof QUICK_CASH_NOTES)[number];
  disabled: boolean;
  onPress(): void;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound="key"
      style={({ pressed }) => [
        styles.quickCashButton,
        quickCashNoteStyle(amount),
        disabled && styles.disabled,
        pressed && !disabled && styles.pressed,
      ]}
      testID={`payment-cash-quick-${amount}`}
    >
      <Text style={styles.quickCashNoteText}>{`$${amount}`}</Text>
    </PosPressable>
  );
}

function quickCashNoteStyle(
  amount: (typeof QUICK_CASH_NOTES)[number],
): ViewStyle {
  switch (amount) {
    case 5:
      return styles.quickCashNote5;
    case 10:
      return styles.quickCashNote10;
    case 20:
      return styles.quickCashNote20;
    case 50:
      return styles.quickCashNote50;
    case 100:
      return styles.quickCashNote100;
  }
}

function TenderRow({
  locale,
  onRemove,
  state,
  tender,
  t,
}: Readonly<{
  locale: PaymentLocale;
  onRemove(): void;
  state: PaymentPresenterState;
  tender: PaymentPresenterTender;
  t: Translate;
}>) {
  usePosThemeVersion();
  return (
    <View
      style={styles.tenderRow}
      testID={`payment-tender-${tender.tenderGuid}`}
    >
      <View style={styles.tenderIdentity}>
        <Text style={styles.tenderMethod}>
          {t(
            tender.method === "card"
              ? "method.card"
              : paymentMethodCopyKey(tender.method),
          )}
        </Text>
        <Text style={styles.tenderDisposition}>
          {t(
            tender.reversible
              ? "summary.reversible"
              : "summary.locked",
          )}
        </Text>
      </View>
      <Text style={styles.tenderAmount}>
        {formatAud(tender.amount.cents, locale)}
      </Text>
      {tender.reversible ? (
        <ActionButton
          disabled={!state.allowedActions.removeTender || state.busy}
          label={t("action.remove")}
          onPress={onRemove}
          size="small"
          sound="danger"
          testID={`payment-remove-${tender.tenderGuid}`}
          tone="quiet"
        />
      ) : null}
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

function PaymentMethodButton({
  active,
  disabled,
  highlighted,
  hint,
  icon,
  iconColor,
  label,
  onPress,
  testID,
}: Readonly<{
  /** presenter 当前选中的付款方式（无障碍 selected 状态沿用它）。 */
  active: boolean;
  disabled: boolean;
  /** 右栏正在为这个方式录入金额。 */
  highlighted: boolean;
  hint: string;
  icon: PosIconName;
  iconColor: keyof PosPalette;
  label: string;
  onPress(): void;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled, selected: active }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.methodButton,
        highlighted && styles.methodButtonActive,
        disabled && styles.methodButtonDisabled,
        pressed && !disabled && styles.pressed,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={disabled ? posColors.disabledInk : posColors[iconColor]}
        name={icon}
        size={34}
      />
      <View style={styles.methodCopy}>
        <Text
          numberOfLines={1}
          style={[
            styles.methodButtonText,
            disabled && styles.methodTextDisabled,
          ]}
        >
          {label}
        </Text>
        <Text
          numberOfLines={1}
          style={[
            styles.methodHint,
            disabled && styles.methodTextDisabled,
          ]}
        >
          {hint}
        </Text>
      </View>
      {highlighted ? (
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.accent}
          name="check-circle"
          size={22}
          style={styles.methodCheck}
        />
      ) : null}
    </PosPressable>
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
 * 恢复类操作的显示条件，左栏的「取消支付」与右栏动作区共用同一份判断，
 * 条件本身与改版前完全一致。
 */
function recoveryActionFlags(
  state: PaymentPresenterState,
  presenter: PaymentScreenPresenter,
): Readonly<{
  cashConfirmationRecovery: boolean;
  showPreparedCashCancellation: boolean;
  showGenericCancellation: boolean;
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
  const showGenericCancellation =
    state.allowedActions.cancel && !preparedCashCancellation;
  // Blocked 礼券撤券只能由主管授权作废；没有授权能力时不显示任何可点按钮。
  const showVoidBlockedTenderReversal =
    state.tenderReversalRecovery?.status === "blocked" &&
    state.tenderReversalRecovery.voidAvailable === true &&
    typeof presenter.voidBlockedTenderReversal === "function";
  return {
    cashConfirmationRecovery,
    showPreparedCashCancellation,
    showGenericCancellation,
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

/** 普通销售里的手动刷卡（整笔、勾选确认）；分期续付里的手动刷卡走“准备→确认”，不显示这套输入。 */
function isSalesManualCard(state: PaymentPresenterState): boolean {
  return (
    state.selectedMethod === "manual-card" && state.checkout.flow === "regular"
  );
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

  /* ---------- 三栏骨架 ---------- */
  workspace: {
    flex: 1,
    minHeight: 0,
    flexDirection: "row",
    gap: 12,
  },
  workspaceCompact: {
    flex: 0,
    flexDirection: "column",
  },
  contextPane: {
    width: 318,
    minHeight: 0,
    gap: 10,
  },
  contextPaneNarrow: {
    width: 270,
  },
  contextPaneCompact: {
    width: "100%",
    maxHeight: 420,
  },
  summaryPane: {
    flex: 1,
    minHeight: 0,
    minWidth: 0,
    gap: 10,
  },
  summaryPaneCompact: {
    flex: 0,
  },
  entryPane: {
    width: 392,
    minHeight: 0,
    gap: 10,
  },
  entryPaneNarrow: {
    width: 350,
  },
  entryPaneCompact: {
    width: "100%",
  },

  /* ---------- 左栏：订单明细 ---------- */
  contextCard: {
    flex: 1,
    minHeight: 0,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    overflow: "hidden",
  },
  contextCardHeader: {
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
    flexDirection: "row",
    alignItems: "flex-end",
    justifyContent: "space-between",
    gap: 10,
  },
  contextCardHeading: {
    flexShrink: 1,
  },
  contextEyebrow: {
    color: colors.accent,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 0.7,
    textTransform: "uppercase",
  },
  contextListTitle: {
    marginTop: 2,
    color: colors.ink,
    fontSize: 18,
    fontWeight: "900",
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
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
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
  },
  contextLine: {
    minHeight: 56,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
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
    marginTop: 2,
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    fontVariant: ["tabular-nums"],
  },
  contextLineAmount: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  contextEmpty: {
    paddingHorizontal: 14,
    paddingVertical: 18,
    color: colors.mutedInk,
    fontSize: 15,
    lineHeight: 21,
  },
  contextTotals: {
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderTopWidth: 1,
    borderTopColor: colors.border,
    backgroundColor: colors.surfaceAlt,
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

  /* ---------- 中栏：剩余应付 + 支付状态 ---------- */
  dueCard: {
    paddingHorizontal: 18,
    paddingVertical: 12,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    gap: 8,
  },
  dueCardWarning: {
    borderColor: colors.yellowBorder,
    backgroundColor: colors.yellowSoft,
  },
  dueCardDanger: {
    borderColor: colors.redBorder,
    backgroundColor: colors.redSoft,
  },
  dueCardSuccess: {
    borderColor: colors.greenBorder,
    backgroundColor: colors.greenSoft,
  },
  dueRow: {
    flexDirection: "row",
    alignItems: "flex-end",
    justifyContent: "space-between",
    gap: 12,
  },
  dueLabel: {
    paddingBottom: 8,
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
  },
  dueAmount: {
    flexShrink: 1,
    color: colors.ink,
    fontSize: 58,
    fontWeight: "900",
    letterSpacing: -1.5,
    lineHeight: 62,
    textAlign: "right",
    fontVariant: ["tabular-nums"],
  },
  statusPanel: {
    paddingTop: 8,
    borderTopWidth: 1,
    borderTopColor: colors.border,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 14,
  },
  statusCopy: {
    flex: 1,
  },
  statusTitle: {
    color: colors.ink,
    fontSize: 17,
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
  statusHint: {
    marginTop: 2,
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 18,
  },

  /* ---------- 中栏：付款方式 ---------- */
  summaryScroll: {
    flex: 1,
    minHeight: 0,
  },
  summaryScrollCompact: {
    flex: 0,
  },
  summaryScrollContent: {
    flexGrow: 1,
    gap: 8,
    paddingBottom: 2,
  },
  sectionLabel: {
    paddingHorizontal: 2,
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
    letterSpacing: 0.5,
  },
  sectionTitle: {
    color: colors.ink,
    fontSize: 18,
    fontWeight: "900",
  },
  methodGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  methodButton: {
    minHeight: 92,
    minWidth: 150,
    // 固定两列：落单的最后一块也只占半行，不会被拉成整行宽。
    maxWidth: "49%",
    flexGrow: 1,
    flexBasis: "46%",
    paddingHorizontal: 16,
    paddingVertical: 10,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  methodButtonActive: {
    borderWidth: 2.5,
    borderColor: colors.accent,
    backgroundColor: colors.accentSoft,
  },
  methodButtonDisabled: {
    borderColor: colors.disabledBorder,
    backgroundColor: colors.disabledSurface,
  },
  methodCopy: {
    flex: 1,
    minWidth: 0,
  },
  methodButtonText: {
    color: colors.ink,
    fontSize: 19,
    fontWeight: "900",
  },
  methodHint: {
    marginTop: 3,
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
  },
  methodTextDisabled: {
    color: colors.disabledInk,
  },
  methodCheck: {
    position: "absolute",
    top: 8,
    right: 10,
  },
  installmentToggleTrack: {
    width: 54,
    height: 32,
    padding: 3,
    borderRadius: 16,
    backgroundColor: colors.border,
    justifyContent: "center",
  },
  installmentToggleTrackEnabled: {
    backgroundColor: colors.green,
  },
  installmentToggleThumb: {
    width: 26,
    height: 26,
    borderRadius: 13,
    backgroundColor: colors.surface,
  },
  installmentToggleThumbEnabled: {
    alignSelf: "flex-end",
  },
  installmentToggleIssue: {
    width: "100%",
    color: colors.red,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 18,
  },
  linklyTerminalPanel: {
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderRadius: 10,
    borderWidth: 1,
    borderColor: colors.blueBorder,
    backgroundColor: colors.blueSoft,
  },
  linklyTerminalTitle: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "800",
  },
  linklyTerminalName: {
    marginTop: 2,
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  linklyTerminalHint: {
    marginTop: 2,
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    lineHeight: 18,
  },
  linklyTerminalError: {
    color: colors.red,
    fontSize: 15,
    fontWeight: "800",
    lineHeight: 21,
  },

  /* ---------- 中栏：当前付款 ---------- */
  tenderList: {
    borderRadius: 12,
    borderWidth: 1,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    overflow: "hidden",
  },
  tenderRow: {
    minHeight: 78,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderBottomWidth: 1,
    borderBottomColor: colors.border,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  tenderIdentity: {
    flex: 1,
    minWidth: 0,
  },
  tenderMethod: {
    color: colors.ink,
    fontSize: 17,
    fontWeight: "900",
  },
  tenderDisposition: {
    marginTop: 2,
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
  },
  tenderAmount: {
    color: colors.ink,
    fontSize: 20,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  emptyTenders: {
    flexGrow: 1,
    minHeight: 78,
    padding: 14,
    borderRadius: 12,
    borderWidth: 1.5,
    borderStyle: "dashed",
    borderColor: colors.border,
    backgroundColor: colors.surface,
    alignItems: "center",
    justifyContent: "center",
  },
  emptyTendersText: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
    textAlign: "center",
  },

  /* ---------- 右栏：本次金额、纸币快捷键、数字键盘 ---------- */
  entryScroll: {
    flex: 1,
    minHeight: 0,
  },
  entryScrollCompact: {
    flex: 0,
  },
  entryScrollContent: {
    flexGrow: 1,
    gap: 10,
  },
  form: {
    flexGrow: 1,
    gap: 10,
  },
  amountBox: {
    minHeight: 92,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 12,
    borderWidth: 2,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  amountBoxArmed: {
    borderColor: colors.borderStrong,
  },
  amountBoxMeta: {
    flexShrink: 1,
    alignItems: "flex-start",
    gap: 4,
  },
  amountBoxLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  amountValueRow: {
    flex: 1,
    minWidth: 120,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "flex-end",
  },
  amountCurrency: {
    color: colors.ink,
    fontSize: 42,
    fontWeight: "900",
    lineHeight: 48,
  },
  amountCurrencyIdle: {
    color: colors.placeholder,
  },
  amountInput: {
    flexShrink: 1,
    minWidth: 96,
    minHeight: 56,
    paddingHorizontal: 0,
    paddingVertical: 0,
    color: colors.ink,
    fontSize: 42,
    fontWeight: "900",
    letterSpacing: -1,
    textAlign: "right",
    fontVariant: ["tabular-nums"],
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
  voucherInputGroup: {
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
  quickCashRow: {
    flexDirection: "row",
    flexWrap: "nowrap",
    gap: 8,
  },
  quickCashButton: {
    flexGrow: 1,
    flexBasis: 0,
    minWidth: PAYMENT_MIN_TOUCH_TARGET,
    minHeight: 60,
    paddingHorizontal: 4,
    borderRadius: 10,
    borderWidth: 1.5,
    alignItems: "center",
    justifyContent: "center",
  },
  // 纸币快捷键对应真实纸币颜色：底色、描边和文字都保持字面量，不随主题变化。
  quickCashNote5: {
    backgroundColor: "#E7C5DD",
    borderColor: "#956485",
  },
  quickCashNote10: {
    backgroundColor: "#B9DCEB",
    borderColor: "#4F8198",
  },
  quickCashNote20: {
    backgroundColor: "#EDB5AA",
    borderColor: "#A85848",
  },
  quickCashNote50: {
    backgroundColor: "#F4DB7F",
    borderColor: "#9C7A18",
  },
  quickCashNote100: {
    backgroundColor: "#B9D8B4",
    borderColor: "#5C8358",
  },
  quickCashNoteText: {
    color: "#10253A",
    fontSize: 19,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  keypad: {
    flexGrow: 1,
    gap: 10,
  },
  keypadRow: {
    flexGrow: 1,
    flexBasis: 56,
    minHeight: 56,
    maxHeight: 86,
    flexDirection: "row",
    gap: 10,
  },
  keypadKey: {
    flex: 1,
    minHeight: 56,
    borderRadius: 10,
    borderWidth: 1.5,
    borderColor: colors.border,
    backgroundColor: colors.surface,
    alignItems: "center",
    justifyContent: "center",
  },
  keypadKeyDisabled: {
    borderColor: colors.disabledBorder,
    backgroundColor: colors.disabledSurface,
  },
  keypadKeyText: {
    color: colors.ink,
    fontSize: 30,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  keypadKeyTextDisabled: {
    color: colors.disabledInk,
  },
  cashSettlement: {
    paddingHorizontal: 16,
    paddingVertical: 8,
    borderRadius: 12,
    borderWidth: 1.5,
    borderColor: colors.greenBorder,
    backgroundColor: colors.greenSoft,
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

  /* ---------- 右栏：恢复 / 确认 / 提交 ---------- */
  summaryActionDock: {
    flexShrink: 0,
    gap: 10,
  },
  recoveryActions: {
    gap: 10,
  },
  summaryFooter: {
    gap: 10,
  },
  preparedHint: {
    color: colors.ink,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 22,
  },
  formActions: {
    flexShrink: 0,
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
