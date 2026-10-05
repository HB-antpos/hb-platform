import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  Modal,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

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
  canSwitchLinklyTerminal,
  canSelectPaymentMethod,
  canSubmitPaymentMethod,
  type PaymentPresenterState,
  type PaymentPresenterTender,
  type PaymentScreenPresenter,
  type PaymentUiMethod,
  type PaymentUiPhase,
} from "./payment-presenter";

import type {
  LinklySafeOperatorKey,
} from "@/features/payments/runtime/linkly-operator-runtime";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosTextInput } from "@/ui/controls/pos-text-input";
import {
  HandheldActionButton,
  HandheldSection,
  HandheldStateSurface,
} from "@/ui/handheld";
import { PosStatusStrip } from "@/ui/shell/status-strip";
import { posColors } from "@/ui/theme";

export const PAYMENT_MIN_TOUCH_TARGET = 48;

const SQUARE_AUTO_RECOVERY_INTERVAL_MS = 2_000;
const SQUARE_AUTO_RECOVERY_MAX_ATTEMPTS = 45;
const SQUARE_AUTO_RECOVERY_WINDOW_MS = 90_000;
const LINKLY_AUTO_RECOVERY_INTERVAL_MS = 2_000;
const LINKLY_AUTO_RECOVERY_MAX_ATTEMPTS = 89;
const LINKLY_AUTO_RECOVERY_WINDOW_MS = 180_000;

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
  "manual-card",
  "voucher",
] as const satisfies readonly PaymentUiMethod[]);

type PaymentScreenProps = Readonly<{
  installmentModeControl?: PaymentInstallmentModeControl;
  presenter: PaymentScreenPresenter;
  locale?: PaymentLocale;
  onBack?(): void;
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
  onComplete,
  onPrintReceipt,
  showStatusStrip = true,
}: PaymentScreenProps) {
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
  // 独立手持 POS 始终使用单列竖屏信息架构，不再按平板宽度切回多栏。
  const compact = true;
  const shortLandscape = false;
  const [
    fullInstallmentConfirmationOpen,
    setFullInstallmentConfirmationOpen,
  ] = useState(false);
  const [
    preparedCashCancellationOpen,
    setPreparedCashCancellationOpen,
  ] = useState(false);
  const [paymentEntryOpen, setPaymentEntryOpen] = useState(false);
  const latestState = useRef(state);
  latestState.current = state;

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
  const designState = paymentDesignState(state);

  return (
    <HandheldStateSurface slug={designState} style={styles.stateSurface}>
      <SafeAreaView style={styles.safeArea} testID="payment-screen">
      <View style={styles.header}>
        <View style={styles.headerIdentity}>
          <Text style={styles.title}>{t("title")}</Text>
          <Text style={styles.subtitle}>{t("subtitle")}</Text>
        </View>
        <View style={styles.headerActions}>
          {state.orderGuid ? (
            <Text numberOfLines={1} style={styles.orderReference}>
              {t("summary.order", {
                order: shortIdentifier(state.orderGuid),
              })}
            </Text>
          ) : null}
        </View>
      </View>

      {showStatusStrip ? <PosStatusStrip /> : null}

      <PosKeyboardAwareScrollView
        contentContainerStyle={styles.scrollContent}
        scrollEnabled={compact || state.phase === "success"}
        style={styles.contentScroll}
        testID="payment-content-scroll"
      >
        {state.phase !== "success" ? (
          <HandheldSection testID="payment-status-section">
            <PaymentStatusPanel state={state} t={t} />
          </HandheldSection>
        ) : null}

        {state.runtimeErrorCode ? (
          <View
            accessibilityRole="alert"
            style={styles.errorBanner}
            testID="payment-runtime-error"
          >
            <Text style={styles.errorText}>
              {t(paymentRuntimeErrorCopyKey(state.runtimeErrorCode))}
            </Text>
            {!state.busy ? (
              <PosPressable
                accessibilityRole="button"
                onPress={() => presenter.dismissError()}
                sound="navigate"
                style={({ pressed }) => [
                  styles.errorDismiss,
                  pressed && styles.pressed,
                ]}
                testID="payment-error-dismiss"
              >
                <Text style={styles.errorDismissText}>
                  {t("action.dismiss")}
                </Text>
              </PosPressable>
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
            <PaymentSummary
              compact={compact}
              locale={locale}
              onCancelPreparedCash={() => {
                setPreparedCashCancellationOpen(true);
              }}
              onConfirm={() => {
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
              }}
              onSelectMethod={(method) => {
                if (presenter.selectMethod(method)) {
                  setPaymentEntryOpen(true);
                }
              }}
              presenter={presenter}
              state={state}
              t={t}
            />
            <PaymentContextPane
              canLeave={canLeave}
              compact={compact}
              installmentModeControl={installmentModeControl}
              locale={locale}
              onBack={onBack}
              presenter={presenter}
              state={state}
              t={t}
            />
            <Modal
              animationType="fade"
              onRequestClose={closePaymentEntry}
              testID="payment-entry-native-modal"
              transparent
              visible={paymentEntryOpen && showEntry}
            >
              <View
                accessibilityViewIsModal
                style={styles.entryModalBackdrop}
                testID="payment-entry-modal"
              >
                <PosPressable
                  accessible={false}
                  onPress={closePaymentEntry}
                  sound="navigate"
                  style={styles.modalDismissArea}
                  testID="payment-entry-backdrop"
                />
                <View
                  style={[
                    styles.entryPane,
                    shortLandscape && styles.entryPaneShort,
                    compact && styles.entryPaneCompact,
                    styles.entryModalCard,
                  ]}
                  testID="payment-entry-pane"
                >
                  <View style={styles.entryModalHeader}>
                    <Text style={styles.entryModalTitle}>
                      {state.selectedMethod
                        ? t(paymentMethodCopyKey(state.selectedMethod))
                        : t("method.title")}
                    </Text>
                    <Text style={styles.entryModalRemaining}>
                      {`${t("summary.remaining")} · ${formatAud(
                        state.remaining.cents,
                        locale,
                      )}`}
                    </Text>
                  </View>
              <PosKeyboardAwareScrollView
                contentContainerStyle={[
                  styles.entryScrollContent,
                  compact && styles.entryScrollContentCompact,
                ]}
                nestedScrollEnabled
                scrollEnabled
                style={[
                  styles.entryScroll,
                  compact && styles.entryScrollCompact,
                  styles.entryModalScroll,
                ]}
                testID="payment-entry-scroll"
              >
                {showEntry ? (
                  <View
                    style={[
                      styles.form,
                      shortLandscape && styles.formShort,
                    ]}
                    testID="payment-entry-form"
                  >
                    <Text style={styles.inputLabel}>{t("amount.label")}</Text>
                    <PosTextInput
                      accessibilityLabel={t("amount.label")}
                      editable={!state.busy && state.selectedMethod !== "manual-card"}
                      keyboardType="decimal-pad"
                      onChangeText={(value) => presenter.setAmountText(value)}
                      placeholder="0.00"
                      placeholderTextColor="#7B8793"
                      selectionColor={posColors.blue}
                      showSoftInputOnFocus={false}
                      style={[
                        styles.amountInput,
                        shortLandscape && styles.amountInputShort,
                      ]}
                      testID="payment-amount"
                      value={state.selectedMethod === "manual-card"
                        ? (state.remaining.cents / 100).toFixed(2)
                        : state.amountText}
                    />
                    <Text
                      style={[
                        styles.inputHint,
                        shortLandscape && styles.inputHintShort,
                      ]}
                    >
                      {t(state.selectedMethod === "manual-card" ? "manualCard.amountHint" : "amount.hint")}
                    </Text>
                    {state.selectedMethod === "manual-card" ? (
                      <View style={styles.manualCardConfirmation} testID="payment-manual-card-confirmation">
                        <Text style={styles.manualCardInstructions}>{t("manualCard.instructions")}</Text>
                        <PosPressable
                          accessibilityRole="checkbox"
                          accessibilityLabel={t("manualCard.checked")}
                          accessibilityState={{ checked: state.manualCardConfirmed === true, disabled: state.busy }}
                          disabled={state.busy}
                          onPress={() => presenter.setManualCardConfirmed?.(state.manualCardConfirmed !== true)}
                          sound="key"
                          style={({ pressed }) => [styles.manualCardCheckRow, pressed && styles.pressed]}
                          testID="payment-manual-card-check"
                        >
                          <View style={[styles.manualCardCheckbox, state.manualCardConfirmed === true && styles.manualCardCheckboxChecked]}>
                            {state.manualCardConfirmed === true ? <Text style={styles.manualCardCheckmark}>✓</Text> : null}
                          </View>
                          <Text style={styles.manualCardCheckLabel}>{t("manualCard.checked")}</Text>
                        </PosPressable>
                        <Text style={styles.inputHint}>{t("manualCard.uncertain")}</Text>
                      </View>
                    ) : (
                    <PaymentKeypad
                      amountText={state.amountText}
                      dense={shortLandscape}
                      disabled={state.busy}
                      onChange={(value) => presenter.setAmountText(value)}
                    />
                    )}

                    {state.selectedMethod === "cash" ? (
                      <View
                        style={[
                          styles.quickCashRow,
                          shortLandscape && styles.quickCashRowShort,
                        ]}
                        testID="payment-cash-quick"
                      >
                        {(
                          [
                            [5, styles.quickCashNote5],
                            [10, styles.quickCashNote10],
                            [20, styles.quickCashNote20],
                            [50, styles.quickCashNote50],
                            [100, styles.quickCashNote100],
                          ] as const
                        ).map(([amount, noteStyle]) => (
                          <ActionButton
                            key={amount}
                            label={`$${amount}`}
                            onPress={() =>
                              presenter.setAmountText(amount.toFixed(2))
                            }
                            sound="key"
                            style={[styles.quickCashButton, noteStyle]}
                            testID={`payment-cash-quick-${amount}`}
                            tone="quiet"
                          />
                        ))}
                      </View>
                    ) : null}

                    {state.selectedMethod === "voucher" ? (
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
                          placeholderTextColor="#7B8793"
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
                  </View>
                ) : null}
                {state.fieldIssue ? (
                  <Text
                    accessibilityRole="alert"
                    style={styles.fieldError}
                    testID="payment-field-error"
                  >
                    {t(paymentFieldIssueCopyKey(state.fieldIssue))}
                  </Text>
                ) : null}
              </PosKeyboardAwareScrollView>
              {showEntry ? (
                <View
                  style={[
                    styles.formActions,
                    shortLandscape && styles.formActionsShort,
                  ]}
                  testID="payment-entry-actions"
                >
                  <ActionButton
                    disabled={state.busy}
                    label={t("action.cancel")}
                    onPress={closePaymentEntry}
                    style={styles.formAction}
                    sound="danger"
                    testID="payment-entry-cancel"
                    tone="quiet"
                  />
                  <View style={styles.formAction}>
                    <HandheldActionButton
                      disabled={
                        !state.selectedMethod ||
                        !canSubmitPaymentMethod(state, state.selectedMethod)
                      }
                      label={
                        state.selectedMethod === "manual-card"
                          ? t("manualCard.confirm")
                          : state.checkout.flow === "installment-repayment" &&
                        state.selectedMethod === "cash"
                          ? t("action.prepareCashRepayment")
                          : state.orderGuid
                            ? t("action.addTender")
                            : t("action.pay")
                      }
                      onPress={() => {
                        void presenter.submitSelected().then((submitted) => {
                          if (submitted) setPaymentEntryOpen(false);
                        });
                      }}
                      testID="payment-submit"
                    />
                  </View>
                </View>
              ) : null}
                </View>
              </View>
            </Modal>

            {!paymentEntryOpen && state.fieldIssue ? (
              <Text
                accessibilityRole="alert"
                style={styles.fieldError}
                testID="payment-field-error"
              >
                {t(paymentFieldIssueCopyKey(state.fieldIssue))}
              </Text>
            ) : null}

          </View>
        )}
      </PosKeyboardAwareScrollView>

      <Modal
        animationType="fade"
        onRequestClose={() =>
          setFullInstallmentConfirmationOpen(false)
        }
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
            style={styles.modalDismissArea}
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
                tone="quiet"
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
              />
            </View>
          </View>
        </View>
      </Modal>

      <Modal
        animationType="fade"
        onRequestClose={closePreparedCashCancellation}
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
            style={styles.modalDismissArea}
            testID="payment-cancel-prepared-cash-backdrop"
          />
          <View style={styles.confirmationCard}>
            <Text style={styles.confirmationTitle}>
              {t("installment.cancelPreparedCash.title")}
            </Text>
            <Text style={styles.confirmationBody}>
              {t("installment.cancelPreparedCash.body")}
            </Text>
            <View style={styles.confirmationActions}>
              <ActionButton
                disabled={state.busy}
                label={t("installment.cancelPreparedCash.dismiss")}
                onPress={closePreparedCashCancellation}
                style={styles.confirmationAction}
                sound="navigate"
                testID="payment-cancel-prepared-cash-dismiss"
                tone="quiet"
              />
              <ActionButton
                disabled={state.busy}
                label={t("installment.cancelPreparedCash.confirm")}
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
      </SafeAreaView>
    </HandheldStateSurface>
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
  const { height: windowHeight, width: windowWidth } = useWindowDimensions();
  const paidCents = Math.max(
    0,
    state.total.cents - state.remaining.cents,
  );
  const cash = state.checkout.cash;
  // 320dp 竖屏的结算卡内容区约 238dp，找零区改为纵向可避免现金图标与长金额横向争抢空间。
  const compactSettlementLayout =
    compact && (windowWidth <= 360 || windowHeight <= 640);
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
      <View
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
          <View style={styles.successIcon}>
            <Text style={styles.successIconText}>✓</Text>
          </View>
          <View style={styles.successHeroCopy}>
            <Text style={styles.successTitle}>
              {t("status.success.title")}
            </Text>
            <Text style={styles.successHint}>
              {t("status.success.hint")}
            </Text>
          </View>
        </View>

        <View style={styles.successSummary}>
          <View style={styles.successTotal}>
            <Text style={styles.successLabel}>
              {t("success.paidTotal")}
            </Text>
            <Text style={styles.successTotalAmount}>
              {formatAud(paidCents, locale)}
            </Text>
          </View>
          <View style={styles.successSummaryRule} />
          <View style={styles.successTransaction}>
            <Text style={styles.successLabel}>
              {t("success.orderReference")}
            </Text>
            <Text
              selectable
              style={styles.successOrderReference}
            >
              {orderGuid}
            </Text>
          </View>
        </View>

        {cash.tenderedCents > 0 ? (
          <View
            style={[
              styles.successSettlement,
              compactSettlementLayout && styles.successSettlementCompact,
            ]}
            testID="payment-success-settlement"
          >
            <View
              style={[
                styles.successCashIdentity,
                compactSettlementLayout && styles.successCashIdentityCompact,
              ]}
            >
              <View style={styles.successCashIcon}>
                <Text style={styles.successCashIconText}>$</Text>
              </View>
              <View style={styles.successCashCopy}>
                <Text style={styles.successCashTitle}>
                  {t("success.cashSettlement")}
                </Text>
                <View
                  style={[
                    styles.successCashTenderedRow,
                    compactSettlementLayout &&
                      styles.successCashTenderedRowCompact,
                  ]}
                >
                  <Text style={styles.successCashTenderedLabel}>
                    {t("success.cashTendered")}
                  </Text>
                  <Text style={styles.successCashTenderedAmount}>
                    {formatAud(cash.tenderedCents, locale)}
                  </Text>
                </View>
              </View>
            </View>
            <View
              style={[
                styles.successChange,
                compactSettlementLayout && styles.successChangeCompact,
              ]}
            >
              <Text style={styles.successChangeLabel}>
                {t("success.changeDue")}
              </Text>
              <Text
                adjustsFontSizeToFit={compactSettlementLayout}
                minimumFontScale={0.65}
                numberOfLines={compactSettlementLayout ? 1 : undefined}
                style={[
                  styles.successChangeAmount,
                  compactSettlementLayout && styles.successChangeAmountCompact,
                ]}
                testID="payment-success-change"
              >
                {formatAud(cash.changeCents, locale)}
              </Text>
            </View>
          </View>
        ) : null}

        <View style={styles.successSync}>
          <View style={styles.successSyncDot} />
          <Text style={styles.successSyncText}>
            {t("success.syncQueued")}
          </Text>
        </View>

        <LinklyControls presenter={presenter} state={state} t={t} />
      </View>

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
          <Text style={styles.successReceiptTitle}>
            {t("success.receiptPreview")}
          </Text>
          <Text selectable style={styles.successReceiptOrder}>
            {orderGuid}
          </Text>
          <View style={styles.successReceiptRule} />

          <View style={styles.successReceiptLines}>
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
          </View>

          <View style={styles.successReceiptRule} />
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
          <View style={styles.successReceiptTotal}>
            <Text style={styles.successReceiptTotalLabel}>
              {t("summary.total")}
            </Text>
            <Text style={styles.successReceiptTotalAmount}>
              {formatAud(state.total.cents, locale)}
            </Text>
          </View>
        </View>

        <ActionButton
          disabled={!onPrintReceipt || printState === "printing"}
          label={t("action.printReceipt")}
          onPress={() => {
            void handlePrintReceipt();
          }}
          style={styles.successPrintAction}
          testID="payment-success-print"
          tone="quiet"
        />
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
        {onComplete ? (
          <ActionButton
            label={t("action.newSale")}
            onPress={() => onComplete(orderGuid)}
            sound="navigate"
            style={styles.successCompleteAction}
            testID="payment-complete"
          />
        ) : null}
      </View>
    </View>
  );
}

function PaymentContextPane({
  canLeave,
  compact,
  installmentModeControl,
  locale,
  onBack,
  presenter,
  state,
  t,
}: Readonly<{
  canLeave: boolean;
  compact: boolean;
  installmentModeControl: PaymentInstallmentModeControl | undefined;
  locale: PaymentLocale;
  onBack: (() => void) | undefined;
  presenter: PaymentScreenPresenter;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  const customer = state.checkout.installmentCustomer;
  const flowLabel =
    state.checkout.flow === "installment-create"
      ? locale === "zh"
        ? "新建分期"
        : "New installment"
      : state.checkout.flow === "installment-repayment"
        ? locale === "zh"
          ? "分期还款"
          : "Installment repayment"
        : state.checkout.flow === "installment-recovery"
          ? locale === "zh"
            ? "恢复分期支付"
            : "Recover installment"
          : locale === "zh"
            ? "当前交易"
            : "Current transaction";
  return (
    <View
      style={[
        styles.contextPane,
        compact && styles.contextPaneCompact,
      ]}
      testID="payment-context-pane"
    >
      <View style={styles.contextControls}>
        {onBack ? (
          <ActionButton
            disabled={!canLeave}
            label={locale === "zh" ? "返回收银" : "Back to sale"}
            onPress={onBack}
            sound="navigate"
            style={styles.contextBack}
            testID="payment-back"
            tone="quiet"
          />
        ) : null}
        {installmentModeControl ? (
          <PaymentInstallmentToggle
            control={installmentModeControl}
            t={t}
          />
        ) : null}
      </View>
      <Text style={styles.contextEyebrow}>{flowLabel}</Text>

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
                {locale === "zh" ? "分期顾客" : "Installment customer"}
              </Text>
              {customer.editable &&
              presenter.openInstallmentCustomerEditor ? (
                <PosPressable
                  accessibilityRole="button"
                  disabled={state.busy}
                  onPress={() =>
                    presenter.openInstallmentCustomerEditor?.()
                  }
                  sound="navigate"
                  style={({ pressed }) => [
                    styles.customerEdit,
                    pressed && styles.pressed,
                  ]}
                  testID="payment-customer-edit"
                >
                  <Text style={styles.customerEditText}>
                    {locale === "zh" ? "编辑" : "Edit"}
                  </Text>
                </PosPressable>
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
                    style={styles.customerEditorButton}
                    sound="navigate"
                    testID="payment-customer-cancel"
                    tone="quiet"
                  />
                  <ActionButton
                    label={locale === "zh" ? "保存" : "Save"}
                    onPress={() => presenter.saveInstallmentCustomer?.()}
                    style={styles.customerEditorButton}
                    testID="payment-customer-save"
                  />
                </View>
              </View>
            ) : null}
          </View>
        </PosKeyboardAwareScrollView>
      ) : null}

      <Text style={styles.contextListTitle}>
        {locale === "zh" ? "商品明细" : "Items"}
      </Text>
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
    </View>
  );
}

function PaymentStatusPanel({
  state,
  t,
}: Readonly<{
  state: PaymentPresenterState;
  t: Translate;
}>) {
  const tone = statusTone(state.phase);
  return (
    <View
      accessibilityRole="summary"
      style={[
        styles.statusPanel,
        tone === "warning" && styles.statusWarning,
        tone === "danger" && styles.statusDanger,
        tone === "success" && styles.statusSuccess,
      ]}
      testID={`payment-status-${state.phase}`}
    >
      <View style={styles.statusCopy}>
        <Text style={styles.statusTitle}>
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

function ProviderBlockers({
  state,
  t,
}: Readonly<{
  state: PaymentPresenterState;
  t: Translate;
}>) {
  const manualCardAvailable = state.providers.some(
    (provider) => provider.provider === "manual-card" && provider.available,
  );
  const blockers = state.providers.filter(
    (provider) => !provider.available && provider.blocker &&
      provider.blocker !== "MANUAL_CARD_CONFIGURATION_DISABLED" &&
      !(manualCardAvailable && (provider.provider === "square" || provider.provider === "linkly-cloud")),
  );
  if (!blockers.length) return null;
  return (
    <View style={styles.providerBlockers} testID="payment-provider-blockers">
      {blockers.map((provider) => (
        <Text key={provider.provider} style={styles.providerBlockerText}>
          {t(paymentMethodCopyKey(provider.provider))}:{" "}
          {t(paymentRuntimeErrorCopyKey(provider.blocker!))}
        </Text>
      ))}
    </View>
  );
}

function LinklyTerminalSelector({
  presenter,
  state,
  t,
}: Readonly<{
  presenter: PaymentScreenPresenter;
  state: PaymentPresenterState;
  t: Translate;
}>) {
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
  if (snapshot.terminals.length === 0) {
    return (
      <View style={styles.linklyTerminalPanel} testID="payment-linkly-terminal-empty">
        <Text style={styles.linklyTerminalError}>{t("linkly.noTerminals")}</Text>
      </View>
    );
  }
  if (snapshot.terminals.length === 1) {
    const terminal = snapshot.terminals[0]!;
    return (
      <View style={styles.linklyTerminalPanel} testID="payment-linkly-terminal-current">
        <Text style={styles.linklyTerminalTitle}>{t("linkly.currentTerminal")}</Text>
        <Text style={styles.linklyTerminalName}>
          {`${terminal.displayName} · Lane ${terminal.laneNo}`}
        </Text>
      </View>
    );
  }

  const switchAllowed = canSwitchLinklyTerminal(state);
  return (
    <View style={styles.linklyTerminalPanel} testID="payment-linkly-terminal-selector">
      <Text style={styles.linklyTerminalTitle}>{t("linkly.currentTerminal")}</Text>
      <View style={styles.linklyTerminalGrid}>
        {snapshot.terminals.map((terminal) => {
          const selected = terminal.terminalId === snapshot.selectedTerminalId;
          const disabled =
            !switchAllowed ||
            !terminal.isReady ||
            terminal.pairingState !== "Ready";
          const status = terminal.isBusy
            ? t("linkly.terminalBusy")
            : terminal.pairingState === "NeedsRepair"
              ? t("linkly.terminalNeedsRepair")
              : terminal.pairingState === "Unpaired"
                ? t("linkly.terminalUnpaired")
                : terminal.isReady && terminal.pairingState === "Ready"
                  ? selected
                    ? t("linkly.terminalSelected")
                    : t("linkly.terminalReady")
                  : t("linkly.terminalUnknown");
          return (
            <PosPressable
              accessibilityLabel={`${terminal.displayName}. Lane ${terminal.laneNo}. ${status}`}
              accessibilityRole="button"
              accessibilityState={{ disabled, selected }}
              disabled={disabled}
              key={terminal.terminalId}
              onPress={() => {
                void presenter.selectLinklyTerminal?.(terminal.terminalId);
              }}
              sound="tap"
              style={({ pressed }) => [
                styles.linklyTerminalButton,
                selected && styles.linklyTerminalButtonSelected,
                disabled && styles.disabled,
                pressed && !disabled && styles.pressed,
              ]}
              testID={`payment-linkly-terminal-${terminal.terminalId}`}
            >
              <Text style={styles.linklyTerminalName}>
                {`${terminal.displayName} · Lane ${terminal.laneNo}`}
              </Text>
              <Text style={styles.linklyTerminalHint}>{status}</Text>
            </PosPressable>
          );
        })}
      </View>
      {resource.kind === "switching" ? (
        <Text style={styles.linklyTerminalHint}>{t("linkly.terminalSwitching")}</Text>
      ) : null}
    </View>
  );
}

function RecoveryActions({
  onCancelPreparedCash,
  presenter,
  state,
  t,
}: Readonly<{
  onCancelPreparedCash(): void;
  presenter: PaymentScreenPresenter;
  state: PaymentPresenterState;
  t: Translate;
}>) {
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
  if (
    !state.allowedActions.recover &&
    !showGenericCancellation &&
    !showPreparedCashCancellation &&
    !cashConfirmationRecovery &&
    !showVoidBlockedTenderReversal
  ) {
    return null;
  }
  const actionDisabled = state.busy || state.recoveryInFlight === true;
  return (
    <View style={styles.recoveryActions} testID="payment-recovery-actions">
      {cashConfirmationRecovery ? (
        <ActionButton
          disabled={actionDisabled}
          label={t("action.confirmCashReceived")}
          onPress={() => {
            void presenter.confirm?.();
          }}
          testID="payment-confirm-cash-recovery"
          tone="primary"
        />
      ) : state.allowedActions.recover ? (
        <ActionButton
          disabled={actionDisabled}
          label={
            state.recoveryInFlight
              ? t("action.recovering")
              : t("action.recover")
          }
          onPress={() => {
            void presenter.recover();
          }}
          testID="payment-recover"
          tone="primary"
        />
      ) : null}
      {showVoidBlockedTenderReversal ? (
        <ActionButton
          disabled={actionDisabled}
          label={t("action.voidBlockedTenderReversal")}
          onPress={() => {
            void presenter.voidBlockedTenderReversal?.();
          }}
          testID="payment-void-blocked-tender-reversal"
          tone="danger"
        />
      ) : null}
      {showGenericCancellation ? (
        <ActionButton
          disabled={actionDisabled}
          label={t("action.cancel")}
          onPress={() => {
            void presenter.cancel();
          }}
          testID="payment-cancel"
          tone="danger"
        />
      ) : null}
      {showPreparedCashCancellation ? (
        <ActionButton
          disabled={actionDisabled}
          label={t("action.cancelPreparedCashRepayment")}
          onPress={onCancelPreparedCash}
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
        <Text style={styles.linklyTerminalHint} testID="payment-linkly-display-text">
          {interaction.displayText}
        </Text>
      ) : null}
      {interaction?.displayLines.map((line, index) => (
        <Text
          key={`${index}-${line}`}
          style={styles.linklyTerminalHint}
          testID={`payment-linkly-display-line-${index}`}
        >
          {line}
        </Text>
      ))}
      {interaction?.inputType ? (
        <Text style={styles.linklyTerminalHint} testID="payment-linkly-input-type">
          {t("terminal.inputRequired")}
        </Text>
      ) : null}
      {interaction?.graphicCode ? (
        <Text style={styles.linklyTerminalHint} testID="payment-linkly-graphic-code">
          {t(linklyGraphicCopyKey(interaction.graphicCode))}
        </Text>
      ) : null}
      {interaction?.recoveryAction ? (
        <Text style={styles.linklyTerminalHint} testID="payment-linkly-recovery-action">
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
              sound="key"
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
            testID="payment-linkly-receipt-printed"
            tone="secondary"
          />
          <ActionButton
            disabled={state.busy}
            label={t("action.linklyAcknowledge")}
            onPress={() => {
              void presenter.acknowledgeLinkly();
            }}
            testID="payment-linkly-acknowledge"
            tone="secondary"
          />
        </View>
      ) : null}
    </View>
  );
}

function PaymentSummary({
  compact,
  locale,
  onCancelPreparedCash,
  onConfirm,
  onSelectMethod,
  presenter,
  state,
  t,
}: Readonly<{
  compact: boolean;
  locale: PaymentLocale;
  onCancelPreparedCash(): void;
  onConfirm(): void;
  onSelectMethod(method: PaymentUiMethod): void;
  presenter: PaymentScreenPresenter;
  state: PaymentPresenterState;
  t: Translate;
}>) {
  const paidCents = Math.max(
    0,
    state.total.cents - state.remaining.cents,
  );
  const manualCardAvailable = state.providers.some(
    (entry) => entry.provider === "manual-card" && entry.available,
  );
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
  return (
    <View
      style={[
        styles.summaryPane,
        compact && styles.summaryPaneCompact,
      ]}
      testID="payment-summary"
    >
      <ScrollView
        contentContainerStyle={[
          styles.summaryScrollContent,
          compact && styles.summaryScrollContentCompact,
        ]}
        nestedScrollEnabled
        scrollEnabled={!compact}
        style={[
          styles.summaryScroll,
          compact && styles.summaryScrollCompact,
        ]}
        testID="payment-summary-scroll"
      >
        <Text style={styles.sectionTitle}>{t("method.title")}</Text>
        <View style={styles.methodGrid}>
          {PAYMENT_METHODS.filter((method) =>
            method === "manual-card"
              ? manualCardAvailable && state.checkout.flow === "regular"
              : !(manualCardAvailable && (method === "square" || method === "linkly-cloud")),
          ).map((method) => (
            <PaymentMethodButton
              active={state.selectedMethod === method}
              disabled={!canSelectPaymentMethod(state, method)}
              key={method}
              label={t(paymentMethodCopyKey(method))}
              onPress={() => onSelectMethod(method)}
              testID={`payment-method-${method}`}
            />
          ))}
        </View>
        <ProviderBlockers state={state} t={t} />
        <LinklyTerminalSelector presenter={presenter} state={state} t={t} />
        <View style={styles.summarySectionRule} />
        <Text style={styles.sectionTitle}>{t("summary.title")}</Text>
        <SummaryAmount
          label={t("summary.total")}
          locale={locale}
          value={state.total.cents}
        />
        <SummaryAmount
          label={t("summary.paid")}
          locale={locale}
          value={paidCents}
        />
        <View style={styles.remainingRule} />
        <SummaryAmount
          emphasis
          label={t("summary.remaining")}
          locale={locale}
          testID="payment-remaining"
          value={state.remaining.cents}
        />
        {state.checkout.cash.tenderedCents > 0 ? (
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

        <Text style={styles.tenderTitle}>{t("summary.tenders")}</Text>
        {state.tenders.length ? (
          state.tenders.map((tender) => (
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
          ))
        ) : (
          <Text style={styles.emptyTenders}>{t("summary.noTenders")}</Text>
        )}
        <RecoveryActions
          onCancelPreparedCash={onCancelPreparedCash}
          presenter={presenter}
          state={state}
          t={t}
        />
        <LinklyControls presenter={presenter} state={state} t={t} />
      </ScrollView>
      {showConfirmation || showPreparedCashCancellationInFooter ? (
        <View style={styles.summaryFooter} testID="payment-summary-footer">
          {showConfirmation ? (
            <ActionButton
              disabled={state.busy}
              label={
                state.checkout.cashRepaymentStatus === "ready" ||
                state.checkout.cashRepaymentStatus === "confirming"
                  ? t("action.confirmCashReceived")
                  : locale === "zh"
                    ? "确认分期付款"
                    : "Confirm installment payment"
              }
              onPress={onConfirm}
              sound="danger"
              style={styles.confirmAction}
              testID="payment-confirm"
            />
          ) : null}
          {showPreparedCashCancellationInFooter ? (
            <ActionButton
              disabled={state.busy}
              label={t("action.cancelPreparedCashRepayment")}
              onPress={onCancelPreparedCash}
              sound="danger"
              style={styles.preparedCashCancelAction}
              testID="payment-cancel-prepared-cash"
              tone="quiet"
            />
          ) : null}
        </View>
      ) : null}
    </View>
  );
}

function PaymentInstallmentToggle({
  control,
  t,
}: Readonly<{
  control: PaymentInstallmentModeControl;
  t: Translate;
}>) {
  return (
    <View style={styles.installmentToggleGroup}>
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
          control.locked && styles.disabled,
          pressed && !control.locked && styles.pressed,
        ]}
        testID="payment-installment-toggle"
      >
        <Text style={styles.installmentToggleLabel}>
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
      {control.issue ? (
        <Text
          accessibilityRole="alert"
          style={styles.installmentToggleIssue}
        >
          {t("installment.unavailable")}
        </Text>
      ) : null}
    </View>
  );
}

function PaymentKeypad({
  amountText,
  dense,
  disabled,
  onChange,
}: Readonly<{
  amountText: string;
  dense: boolean;
  disabled: boolean;
  onChange(value: string): void;
}>) {
  const keys = [
    "1",
    "2",
    "3",
    "4",
    "5",
    "6",
    "7",
    "8",
    "9",
    ".",
    "0",
    "backspace",
  ] as const;
  return (
    <View
      style={[styles.keypad, dense && styles.keypadShort]}
      testID="payment-keypad"
    >
      {keys.map((key) => (
        <PosPressable
          accessibilityRole="button"
          disabled={disabled}
          key={key}
          onPress={() =>
            onChange(nextKeypadAmount(amountText, key))
          }
          sound={key === "backspace" ? "danger" : "key"}
          style={({ pressed }) => [
            styles.keypadKey,
            dense && styles.keypadKeyShort,
            disabled && styles.disabled,
            pressed && !disabled && styles.pressed,
          ]}
          testID={`payment-key-${
            key === "." ? "decimal" : key
          }`}
        >
          <Text style={styles.keypadKeyText}>
            {key === "backspace" ? "⌫" : key}
          </Text>
        </PosPressable>
      ))}
    </View>
  );
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
  testID,
  value,
}: Readonly<{
  emphasis?: boolean;
  label: string;
  locale: PaymentLocale;
  testID?: string;
  value: number;
}>) {
  return (
    <View style={styles.summaryAmountRow}>
      <Text style={styles.summaryLabel}>{label}</Text>
      <Text
        style={[
          styles.summaryAmount,
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
  label,
  onPress,
  testID,
}: Readonly<{
  active: boolean;
  disabled: boolean;
  label: string;
  onPress(): void;
  testID: string;
}>) {
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled, selected: active }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.methodButton,
        active && styles.methodButtonActive,
        disabled && styles.disabled,
        pressed && !disabled && styles.pressed,
      ]}
      testID={testID}
    >
      <Text
        style={[
          styles.methodButtonText,
          active && styles.methodButtonTextActive,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function ActionButton({
  disabled = false,
  label,
  onPress,
  sound,
  style,
  testID,
  tone = "primary",
}: Readonly<{
  disabled?: boolean;
  label: string;
  onPress(): void;
  sound?: "tap" | "key" | "navigate" | "danger";
  style?: StyleProp<ViewStyle>;
  testID?: string;
  tone?: "primary" | "secondary" | "danger" | "quiet";
}>) {
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound={sound ?? (tone === "danger" ? "danger" : "tap")}
      style={({ pressed }) => [
        styles.actionButton,
        tone === "secondary" && styles.actionSecondary,
        tone === "danger" && styles.actionDanger,
        tone === "quiet" && styles.actionQuiet,
        disabled && styles.disabled,
        pressed && !disabled && styles.pressed,
        style,
      ]}
      testID={testID}
    >
      <Text
        style={[
          styles.actionButtonText,
          tone === "secondary" && styles.actionSecondaryText,
          tone === "quiet" && styles.actionQuietText,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
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

function paymentDesignState(
  state: PaymentPresenterState,
):
  | "payment-method"
  | "cash-payment"
  | "card-processing"
  | "payment-success"
  | "payment-failure" {
  if (state.phase === "success") return "payment-success";
  if (state.phase === "declined" || state.phase === "cancelled") {
    return "payment-failure";
  }

  const cardProvider =
    state.provider === "square" ||
    state.provider === "linkly-cloud" ||
    state.selectedMethod === "square" ||
    state.selectedMethod === "linkly-cloud";
  if (
    cardProvider &&
    ([
      "submitting",
      "awaiting-terminal",
      "pending",
      "unknown",
      "recovery-required",
    ] as readonly PaymentUiPhase[]).includes(state.phase)
  ) {
    return "card-processing";
  }

  if (
    state.selectedMethod === "cash" ||
    (
      [
        "offline-cash",
        "cash-collection-ready",
        "cash-confirming",
      ] as readonly PaymentUiPhase[]
    ).includes(state.phase)
  ) {
    return "cash-payment";
  }
  return "payment-method";
}

function canCancelPreparedCash(state: PaymentPresenterState): boolean {
  return (
    state.checkout.flow === "installment-repayment" &&
    state.allowedActions.cancel &&
    state.tenders.some(
      (tender) => tender.method === "cash" && !tender.reversible,
    )
  );
}

function canSafelyLeave(state: PaymentPresenterState): boolean {
  const installmentCashFence =
    state.checkout.flow === "installment-repayment" &&
    (state.checkout.cashRepaymentStatus === "ready" ||
      state.checkout.cashRepaymentStatus === "confirming" ||
      state.tenders.some(
        (tender) => tender.method === "cash" && !tender.reversible,
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

const styles = StyleSheet.create({
  stateSurface: {
    flex: 1,
  },
  safeArea: {
    flex: 1,
    backgroundColor: posColors.canvas,
  },
  header: {
    minHeight: 74,
    paddingHorizontal: 24,
    paddingVertical: 12,
    backgroundColor: posColors.ink,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 18,
  },
  headerIdentity: {
    flexShrink: 1,
  },
  title: {
    color: "#FFFFFF",
    fontSize: 25,
    fontWeight: "800",
    letterSpacing: -0.4,
  },
  subtitle: {
    marginTop: 2,
    color: "#C8D4DF",
    fontSize: 13,
    fontWeight: "600",
  },
  headerActions: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  orderReference: {
    maxWidth: 230,
    color: "#D8E2EA",
    fontSize: 13,
    fontVariant: ["tabular-nums"],
  },
  contentScroll: {
    flex: 1,
  },
  scrollContent: {
    flexGrow: 1,
    padding: 18,
    gap: 14,
  },
  statusPanel: {
    minHeight: 70,
    paddingHorizontal: 18,
    paddingVertical: 13,
    borderLeftWidth: 5,
    borderLeftColor: posColors.blue,
    backgroundColor: posColors.blueSoft,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 16,
  },
  statusWarning: {
    borderLeftColor: "#B7791F",
    backgroundColor: "#FFF4D6",
  },
  statusDanger: {
    borderLeftColor: posColors.red,
    backgroundColor: posColors.redSoft,
  },
  statusSuccess: {
    borderLeftColor: posColors.green,
    backgroundColor: posColors.greenSoft,
  },
  statusCopy: {
    flex: 1,
  },
  statusTitle: {
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "800",
  },
  statusHint: {
    marginTop: 4,
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 19,
  },
  errorBanner: {
    paddingHorizontal: 16,
    paddingVertical: 10,
    minHeight: PAYMENT_MIN_TOUCH_TARGET,
    borderWidth: 1,
    borderColor: "#DCA09C",
    backgroundColor: posColors.redSoft,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  errorText: {
    flex: 1,
    color: "#7A211D",
    fontSize: 14,
    fontWeight: "700",
  },
  errorDismiss: {
    minHeight: PAYMENT_MIN_TOUCH_TARGET,
    minWidth: PAYMENT_MIN_TOUCH_TARGET,
    paddingHorizontal: 12,
    alignItems: "center",
    justifyContent: "center",
  },
  errorDismissText: {
    color: posColors.red,
    fontSize: 14,
    fontWeight: "800",
  },
  successLayout: {
    flex: 1,
    minHeight: 520,
    flexDirection: "row",
    gap: 14,
  },
  successLayoutCompact: {
    minHeight: 0,
    flexDirection: "column",
  },
  successMain: {
    flex: 68,
    minWidth: 0,
    gap: 14,
  },
  successMainCompact: {
    flex: 0,
  },
  successHero: {
    minHeight: 118,
    paddingHorizontal: 24,
    paddingVertical: 18,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    flexDirection: "row",
    alignItems: "center",
    gap: 20,
  },
  successIcon: {
    width: 76,
    height: 76,
    borderWidth: 4,
    borderColor: posColors.green,
    borderRadius: 38,
    alignItems: "center",
    justifyContent: "center",
  },
  successIconText: {
    color: posColors.green,
    fontSize: 44,
    fontWeight: "800",
    lineHeight: 50,
  },
  successHeroCopy: {
    flex: 1,
  },
  successTitle: {
    color: posColors.green,
    fontSize: 32,
    fontWeight: "900",
    letterSpacing: -0.5,
  },
  successHint: {
    marginTop: 5,
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  successSummary: {
    minHeight: 154,
    padding: 24,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    flexDirection: "row",
    alignItems: "stretch",
    gap: 24,
  },
  successTotal: {
    flex: 1,
    justifyContent: "center",
  },
  successLabel: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  successTotalAmount: {
    marginTop: 7,
    color: posColors.green,
    fontSize: 40,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  successSummaryRule: {
    width: 1,
    backgroundColor: posColors.border,
  },
  successTransaction: {
    flex: 1,
    justifyContent: "center",
  },
  successOrderReference: {
    marginTop: 9,
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
    lineHeight: 23,
  },
  successSettlement: {
    minHeight: 116,
    paddingHorizontal: 22,
    paddingVertical: 18,
    borderWidth: 1,
    borderColor: "#D9A441",
    backgroundColor: posColors.yellowSoft,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 18,
  },
  successSettlementCompact: {
    alignItems: "stretch",
    flexDirection: "column",
    gap: 14,
  },
  successCashIdentity: {
    minWidth: 0,
    flex: 1,
    flexDirection: "row",
    alignItems: "center",
    gap: 16,
  },
  successCashIdentityCompact: {
    flex: 0,
    width: "100%",
  },
  successCashIcon: {
    width: 58,
    height: 58,
    borderRadius: 29,
    backgroundColor: posColors.yellow,
    alignItems: "center",
    justifyContent: "center",
  },
  successCashIconText: {
    color: "#FFFFFF",
    fontSize: 29,
    fontWeight: "900",
  },
  successCashCopy: {
    flex: 1,
    minWidth: 0,
  },
  successCashTitle: {
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "900",
  },
  successCashTenderedRow: {
    marginTop: 6,
    flexDirection: "row",
    alignItems: "center",
    gap: 7,
  },
  successCashTenderedRowCompact: {
    flexWrap: "wrap",
  },
  successCashTenderedLabel: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "700",
  },
  successCashTenderedAmount: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
    flexShrink: 1,
  },
  successChange: {
    minWidth: 180,
    paddingLeft: 22,
    borderLeftWidth: 1,
    borderLeftColor: "#D9A441",
    alignItems: "flex-end",
  },
  successChangeCompact: {
    minWidth: 0,
    width: "100%",
    paddingLeft: 0,
    paddingTop: 14,
    borderLeftWidth: 0,
    borderTopWidth: 1,
    borderTopColor: "#D9A441",
  },
  successChangeLabel: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  successChangeAmount: {
    marginTop: 5,
    color: posColors.orange,
    fontSize: 34,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  successChangeAmountCompact: {
    alignSelf: "stretch",
    fontSize: 30,
    lineHeight: 36,
    textAlign: "right",
  },
  successSync: {
    minHeight: 72,
    paddingHorizontal: 20,
    paddingVertical: 14,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  successSyncDot: {
    width: 12,
    height: 12,
    borderRadius: 6,
    backgroundColor: posColors.green,
  },
  successSyncText: {
    flex: 1,
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
  },
  successReceiptColumn: {
    flex: 32,
    minWidth: 300,
  },
  successReceiptColumnCompact: {
    flex: 0,
    minWidth: 0,
  },
  successReceipt: {
    flex: 1,
    minHeight: 390,
    padding: 22,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
  },
  successReceiptTitle: {
    color: posColors.ink,
    fontSize: 20,
    fontWeight: "900",
  },
  successReceiptOrder: {
    marginTop: 6,
    color: posColors.mutedInk,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
  },
  successReceiptRule: {
    height: 1,
    marginVertical: 15,
    backgroundColor: posColors.border,
  },
  successReceiptLines: {
    flex: 1,
    minHeight: 92,
  },
  successReceiptLine: {
    minHeight: 52,
    paddingVertical: 8,
    borderBottomWidth: 1,
    borderBottomColor: posColors.border,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  successReceiptLineCopy: {
    flex: 1,
  },
  successReceiptLineName: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "700",
  },
  successReceiptLineQuantity: {
    marginTop: 3,
    color: posColors.mutedInk,
    fontSize: 12,
  },
  successReceiptLineAmount: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  successReceiptEmpty: {
    paddingVertical: 16,
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
  },
  successReceiptTender: {
    minHeight: 34,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  successReceiptTenderLabel: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
  },
  successReceiptTenderAmount: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  successReceiptTotal: {
    minHeight: 48,
    marginTop: 8,
    paddingTop: 12,
    borderTopWidth: 1,
    borderTopColor: posColors.ink,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
  },
  successReceiptTotalLabel: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  successReceiptTotalAmount: {
    color: posColors.ink,
    fontSize: 22,
    fontWeight: "900",
    fontVariant: ["tabular-nums"],
  },
  successPrintAction: {
    marginTop: 12,
  },
  successPrintStatus: {
    marginTop: 7,
    fontSize: 13,
    fontWeight: "700",
    lineHeight: 18,
  },
  successPrintStatusPrinting: {
    color: posColors.blue,
  },
  successPrintStatusCompleted: {
    color: posColors.green,
  },
  successPrintStatusUnknown: {
    color: posColors.yellow,
  },
  successPrintStatusFailed: {
    color: posColors.red,
  },
  successCompleteAction: {
    marginTop: 10,
  },
  workspace: {
    flex: 1,
    minHeight: 0,
    flexDirection: "row",
    overflow: "hidden",
    backgroundColor: posColors.surface,
    borderWidth: 1,
    borderColor: posColors.border,
  },
  contextPane: {
    flex: 30,
    minHeight: 0,
    minWidth: 250,
    padding: 16,
    borderRightWidth: 1,
    borderRightColor: posColors.border,
    backgroundColor: "#FBFAF7",
  },
  contextPaneCompact: {
    flex: 0,
    minWidth: 0,
    maxHeight: 320,
    borderRightWidth: 0,
    borderBottomWidth: 1,
    borderBottomColor: posColors.border,
  },
  contextControls: {
    marginBottom: 14,
    gap: 8,
  },
  contextBack: {
    alignSelf: "flex-start",
    marginTop: 0,
    marginBottom: 0,
  },
  installmentToggleGroup: {
    gap: 5,
  },
  installmentToggle: {
    minHeight: PAYMENT_MIN_TOUCH_TARGET,
    paddingHorizontal: 11,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 10,
  },
  installmentToggleLabel: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  installmentToggleTrack: {
    width: 42,
    height: 24,
    padding: 2,
    borderRadius: 12,
    backgroundColor: "#AAB4BE",
    justifyContent: "center",
  },
  installmentToggleTrackEnabled: {
    backgroundColor: posColors.blue,
  },
  installmentToggleThumb: {
    width: 20,
    height: 20,
    borderRadius: 10,
    backgroundColor: "#FFFFFF",
  },
  installmentToggleThumbEnabled: {
    alignSelf: "flex-end",
  },
  installmentToggleIssue: {
    color: posColors.red,
    fontSize: 12,
    fontWeight: "700",
    lineHeight: 17,
  },
  contextEyebrow: {
    color: posColors.orange,
    fontSize: 13,
    fontWeight: "900",
    letterSpacing: 0.7,
    textTransform: "uppercase",
  },
  customerCard: {
    padding: 12,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
  },
  customerScroll: {
    flexShrink: 1,
    minHeight: 0,
    marginTop: 14,
  },
  customerScrollContent: {
    flexGrow: 0,
    paddingBottom: 4,
  },
  customerHeading: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 8,
  },
  customerTitle: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "900",
  },
  customerEdit: {
    minHeight: PAYMENT_MIN_TOUCH_TARGET,
    minWidth: PAYMENT_MIN_TOUCH_TARGET,
    paddingHorizontal: 8,
    alignItems: "center",
    justifyContent: "center",
  },
  customerEditText: {
    color: posColors.blue,
    fontSize: 14,
    fontWeight: "800",
  },
  customerNumber: {
    marginTop: 7,
    color: posColors.orange,
    fontSize: 12,
    fontWeight: "800",
  },
  customerValue: {
    marginTop: 5,
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "700",
  },
  customerEditor: {
    marginTop: 12,
    gap: 8,
  },
  customerInput: {
    minHeight: 48,
    paddingHorizontal: 10,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    color: posColors.ink,
    fontSize: 15,
  },
  customerEditorActions: {
    flexDirection: "row",
    gap: 8,
  },
  customerEditorButton: {
    flex: 1,
    marginTop: 0,
  },
  contextListTitle: {
    marginTop: 18,
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "900",
  },
  contextLines: {
    flex: 1,
    minHeight: 0,
    marginTop: 8,
  },
  contextLine: {
    minHeight: 58,
    paddingVertical: 9,
    borderBottomWidth: 1,
    borderBottomColor: posColors.border,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  contextLineCopy: {
    flex: 1,
  },
  contextLineName: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "700",
  },
  contextLineQuantity: {
    marginTop: 3,
    color: posColors.mutedInk,
    fontSize: 12,
  },
  contextLineAmount: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  contextEmpty: {
    paddingVertical: 18,
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 18,
  },
  workspaceCompact: {
    flex: 0,
    minHeight: 0,
    flexDirection: "column",
    overflow: "visible",
  },
  entryModalBackdrop: {
    flex: 1,
    padding: 16,
    backgroundColor: "rgba(15, 32, 48, 0.58)",
    alignItems: "center",
    justifyContent: "center",
  },
  entryModalCard: {
    flex: 0,
    width: "100%",
    maxWidth: 560,
    height: "90%",
    padding: 16,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
  },
  entryModalHeader: {
    paddingBottom: 12,
    borderBottomWidth: 1,
    borderBottomColor: posColors.border,
    gap: 4,
  },
  entryModalTitle: {
    color: posColors.ink,
    fontSize: 20,
    fontWeight: "900",
  },
  entryModalRemaining: {
    color: posColors.mutedInk,
    fontSize: 14,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  entryPane: {
    flex: 42,
    minHeight: 0,
    minWidth: 0,
    padding: 20,
    borderRightWidth: 1,
    borderRightColor: posColors.border,
  },
  entryPaneShort: {
    padding: 14,
  },
  entryPaneCompact: {
    flex: 0,
    padding: 16,
    borderRightWidth: 0,
    borderBottomWidth: 1,
    borderBottomColor: posColors.border,
  },
  entryScroll: {
    flex: 1,
    minHeight: 0,
  },
  entryModalScroll: {
    flex: 1,
  },
  entryScrollCompact: {
    flex: 0,
  },
  entryScrollContent: {
    flexGrow: 1,
    paddingBottom: 4,
  },
  entryScrollContentCompact: {
    flexGrow: 0,
  },
  summaryPane: {
    flex: 28,
    minHeight: 0,
    minWidth: 270,
    padding: 20,
    backgroundColor: "#FBFAF7",
  },
  summaryPaneCompact: {
    flex: 0,
    minWidth: 0,
  },
  summaryScroll: {
    flex: 1,
    minHeight: 0,
  },
  summaryScrollCompact: {
    flex: 0,
  },
  summaryScrollContent: {
    paddingBottom: 4,
  },
  summaryScrollContentCompact: {
    flexGrow: 0,
  },
  summaryFooter: {
    paddingTop: 12,
    borderTopWidth: 1,
    borderTopColor: posColors.border,
  },
  sectionTitle: {
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
  },
  methodGrid: {
    marginTop: 12,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  methodButton: {
    minHeight: 52,
    minWidth: 100,
    flexGrow: 1,
    flexBasis: "46%",
    paddingHorizontal: 18,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    alignItems: "center",
    justifyContent: "center",
  },
  methodButtonActive: {
    borderColor: posColors.blue,
    backgroundColor: posColors.blue,
  },
  methodButtonText: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  methodButtonTextActive: {
    color: "#FFFFFF",
  },
  providerBlockers: {
    marginTop: 10,
    gap: 3,
  },
  providerBlockerText: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 17,
  },
  linklyTerminalPanel: {
    backgroundColor: posColors.blueSoft,
    borderColor: posColors.border,
    borderRadius: 8,
    borderWidth: 1,
    marginTop: 10,
    padding: 10,
  },
  linklyTerminalTitle: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
    lineHeight: 18,
  },
  linklyTerminalGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    marginTop: 8,
  },
  linklyTerminalButton: {
    backgroundColor: posColors.surface,
    borderColor: posColors.border,
    borderRadius: 7,
    borderWidth: 1,
    flexBasis: "46%",
    flexGrow: 1,
    justifyContent: "center",
    minHeight: PAYMENT_MIN_TOUCH_TARGET,
    paddingHorizontal: 10,
    paddingVertical: 7,
  },
  linklyTerminalButtonSelected: {
    borderColor: posColors.blue,
  },
  linklyTerminalName: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "800",
    lineHeight: 18,
  },
  linklyTerminalHint: {
    color: posColors.mutedInk,
    fontSize: 11,
    fontWeight: "600",
    lineHeight: 16,
    marginTop: 2,
  },
  linklyTerminalError: {
    color: posColors.red,
    fontSize: 12,
    fontWeight: "700",
    lineHeight: 17,
  },
  form: {
    marginTop: 20,
    paddingTop: 18,
    borderTopWidth: 1,
    borderTopColor: posColors.border,
  },
  formShort: {
    marginTop: 12,
    paddingTop: 12,
  },
  inputLabel: {
    marginBottom: 6,
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  amountInput: {
    minHeight: 62,
    paddingHorizontal: 16,
    borderWidth: 2,
    borderColor: posColors.blue,
    backgroundColor: "#FFFFFF",
    color: posColors.ink,
    fontSize: 30,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  amountInputShort: {
    minHeight: 52,
    fontSize: 28,
  },
  keypad: {
    marginTop: 14,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  keypadShort: {
    marginTop: 8,
    gap: 6,
  },
  keypadKey: {
    minHeight: 54,
    flexBasis: "30%",
    flexGrow: 1,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    alignItems: "center",
    justifyContent: "center",
  },
  keypadKeyShort: {
    minHeight: PAYMENT_MIN_TOUCH_TARGET,
  },
  keypadKeyText: {
    color: posColors.ink,
    fontSize: 22,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  quickCashRow: {
    marginTop: 10,
    flexDirection: "row",
    flexWrap: "nowrap",
    gap: 7,
  },
  quickCashRowShort: {
    marginTop: 8,
    flexWrap: "nowrap",
    gap: 6,
  },
  quickCashButton: {
    flexGrow: 1,
    flexBasis: 0,
    minWidth: PAYMENT_MIN_TOUCH_TARGET,
    marginTop: 0,
    paddingHorizontal: 4,
  },
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
  voucherInputGroup: {
    marginTop: 16,
  },
  manualCardConfirmation: {
    marginTop: 14,
    gap: 12,
  },
  manualCardInstructions: {
    color: posColors.ink,
    fontSize: 16,
    lineHeight: 24,
  },
  manualCardCheckRow: {
    minHeight: 60,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    padding: 12,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#F4F8FF",
  },
  manualCardCheckbox: {
    width: 26,
    height: 26,
    borderWidth: 2,
    borderColor: posColors.blue,
    backgroundColor: "#FFFFFF",
    alignItems: "center",
    justifyContent: "center",
  },
  manualCardCheckboxChecked: {
    backgroundColor: posColors.blue,
  },
  manualCardCheckmark: {
    color: "#FFFFFF",
    fontSize: 18,
    fontWeight: "800",
  },
  manualCardCheckLabel: {
    flex: 1,
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "700",
    lineHeight: 23,
  },
  voucherInput: {
    minHeight: 52,
    paddingHorizontal: 14,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "700",
  },
  inputHint: {
    marginTop: 6,
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 18,
  },
  inputHintShort: {
    marginTop: 4,
    lineHeight: 17,
  },
  secureCapture: {
    marginTop: 6,
    color: posColors.green,
    fontSize: 13,
    fontWeight: "700",
  },
  fieldError: {
    marginTop: 10,
    color: posColors.red,
    fontSize: 14,
    fontWeight: "700",
  },
  formActions: {
    marginTop: 12,
    flexDirection: "row",
    gap: 10,
  },
  formActionsShort: {
    marginTop: 8,
  },
  formAction: {
    flex: 1,
    marginTop: 0,
  },
  recoveryActions: {
    marginTop: 16,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  linklyPanel: {
    marginTop: 18,
    paddingTop: 18,
    borderTopWidth: 1,
    borderTopColor: posColors.border,
  },
  linklyKeyGrid: {
    marginTop: 12,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  linklyConfirmation: {
    marginTop: 12,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  summaryAmountRow: {
    minHeight: 38,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 16,
  },
  summaryLabel: {
    color: posColors.mutedInk,
    fontSize: 14,
    fontWeight: "600",
  },
  summaryAmount: {
    color: posColors.ink,
    fontSize: 18,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  summaryAmountEmphasis: {
    color: posColors.orange,
    fontSize: 30,
  },
  remainingRule: {
    height: 1,
    marginVertical: 8,
    backgroundColor: posColors.border,
  },
  cashSettlement: {
    marginTop: 10,
    paddingTop: 8,
    borderTopWidth: 1,
    borderTopColor: posColors.border,
  },
  summarySectionRule: {
    height: 1,
    marginVertical: 16,
    backgroundColor: posColors.border,
  },
  tenderTitle: {
    marginTop: 22,
    marginBottom: 8,
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  emptyTenders: {
    paddingVertical: 18,
    color: posColors.mutedInk,
    fontSize: 14,
  },
  tenderRow: {
    minHeight: 66,
    paddingVertical: 8,
    borderTopWidth: 1,
    borderTopColor: posColors.border,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
  },
  tenderIdentity: {
    flex: 1,
  },
  tenderMethod: {
    color: posColors.ink,
    fontSize: 15,
    fontWeight: "800",
  },
  tenderDisposition: {
    marginTop: 2,
    color: posColors.mutedInk,
    fontSize: 12,
  },
  tenderAmount: {
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
  },
  actionButton: {
    minHeight: 48,
    marginTop: 12,
    paddingHorizontal: 18,
    borderWidth: 1,
    borderColor: posColors.blue,
    backgroundColor: posColors.blue,
    alignItems: "center",
    justifyContent: "center",
  },
  actionButtonText: {
    color: "#FFFFFF",
    fontSize: 15,
    fontWeight: "800",
  },
  confirmationBackdrop: {
    flex: 1,
    padding: 24,
    backgroundColor: "rgba(13, 36, 53, 0.58)",
    alignItems: "center",
    justifyContent: "center",
  },
  modalDismissArea: {
    bottom: 0,
    left: 0,
    position: "absolute",
    right: 0,
    top: 0,
  },
  confirmationCard: {
    width: "100%",
    maxWidth: 520,
    padding: 24,
    borderWidth: 1,
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
  },
  confirmationTitle: {
    color: posColors.ink,
    fontSize: 22,
    fontWeight: "900",
  },
  confirmationBody: {
    marginTop: 10,
    color: posColors.mutedInk,
    fontSize: 15,
    lineHeight: 22,
  },
  confirmationActions: {
    marginTop: 18,
    flexDirection: "row",
    gap: 10,
  },
  confirmationAction: {
    flex: 1,
    marginTop: 0,
  },
  actionSecondary: {
    borderColor: posColors.blue,
    backgroundColor: posColors.blueSoft,
  },
  actionSecondaryText: {
    color: posColors.blue,
  },
  actionDanger: {
    borderColor: posColors.red,
    backgroundColor: posColors.red,
  },
  actionQuiet: {
    borderColor: posColors.border,
    backgroundColor: "#FFFFFF",
  },
  actionQuietText: {
    color: posColors.ink,
  },
  completeAction: {
    alignSelf: "flex-end",
    minWidth: 240,
  },
  confirmAction: {
    marginTop: 0,
  },
  preparedCashCancelAction: {
    marginTop: 10,
  },
  disabled: {
    opacity: 0.42,
  },
  pressed: {
    opacity: 0.76,
  },
});
