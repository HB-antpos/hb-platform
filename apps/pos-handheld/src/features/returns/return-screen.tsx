import {
  type ReactNode,
  useCallback,
  useEffect,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

import {
  resolveReturnLocale,
  returnText,
  type ReturnCopyKey,
  type ReturnLocale,
} from "./return-copy";
import type { ReturnTenderMethod } from "@hb/pos-domain/features/returns/return-domain";
import {
  ReturnPresenter,
  type ReturnPresenterLine,
} from "@hb/pos-domain/features/returns/return-presenter";

import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPressable } from "@/ui/controls/pos-pressable";
import {
  HandheldActionButton,
  HandheldSection,
  HandheldStateSurface,
} from "@/ui/handheld";
import { posColors } from "@/ui/theme";

export const RETURN_MIN_TOUCH_TARGET = 48;

/** 退货页交给路由挂载的无焦点扫码目标：路由负责捕获，页面负责按模式分发。 */
export type ReturnHidScanTarget = Readonly<{
  enabled: boolean;
  onScan(value: string): Promise<void>;
}>;

type ReturnScreenProps = Readonly<{
  presenter: ReturnPresenter;
  locale?: ReturnLocale;
  initialReceiptQuery?: string | null;
  onBack?(): void;
  renderHidScannerCapture?(target: ReturnHidScanTarget): ReactNode;
}>;

export function ReturnScreen({
  presenter,
  locale: localeOverride,
  initialReceiptQuery,
  onBack,
  renderHidScannerCapture,
}: ReturnScreenProps) {
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { i18n } = useTranslation();
  const locale =
    localeOverride ??
    resolveReturnLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = (
    key: ReturnCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => returnText(locale, key, values);
  const [orderQuery, setOrderQuery] = useState(initialReceiptQuery ?? "");
  const [productQuery, setProductQuery] = useState("");
  const [openItemName, setOpenItemName] = useState("");
  const [openItemAmount, setOpenItemAmount] = useState("");
  const [confirmationOpen, setConfirmationOpen] = useState(false);
  // 可见输入框聚焦时由它直接接收扫码，隐藏捕获框让出焦点，避免两边抢焦点。
  const [manualInputFocused, setManualInputFocused] = useState(false);
  const initialLookupRef = useRef<Readonly<{
    presenter: ReturnPresenter;
    query: string;
  }> | null>(null);

  useEffect(() => {
    const query = initialReceiptQuery?.trim();
    if (!query || state.phase !== "search") return;
    if (
      initialLookupRef.current?.presenter === presenter &&
      initialLookupRef.current.query === query
    ) {
      return;
    }
    initialLookupRef.current = { presenter, query };
    setOrderQuery(query);
    void presenter.loadReceipt(query).then((ok) => {
      if (ok) setOrderQuery("");
    });
  }, [initialReceiptQuery, presenter, state.phase]);

  useEffect(() => {
    if (state.lines.length === 0) setConfirmationOpen(false);
  }, [state.lines.length]);

  const receiptLoaded =
    state.mode === "receipt" && state.orderSummary !== null;
  const lookupVisible = !confirmationOpen && !receiptLoaded;

  useEffect(() => {
    // 查询区卸载时聚焦中的输入框不一定触发 onBlur，回到查询区时从未聚焦开始。
    if (!lookupVisible) setManualInputFocused(false);
  }, [lookupVisible]);

  /**
   * 无焦点扫码：Zebra DataWedge / 蓝牙 HID 以键盘方式输出，只有聚焦的输入框能收到。
   * 查询区可见且没有手动聚焦输入框时，由隐藏捕获框接收整段条码：
   * 有单模式按订单号查单，无单模式按商品码加入退货行。并发由 presenter.runExclusive 兜底。
   */
  const handleHidScan = useCallback(
    async (value: string): Promise<void> => {
      if (state.mode === "receipt") {
        setOrderQuery(value);
        if (await presenter.loadReceipt(value)) setOrderQuery("");
        return;
      }
      setProductQuery(value);
      if (await presenter.addNoReceiptProduct(value)) setProductQuery("");
    },
    [presenter, state.mode],
  );

  if (state.phase === "submitting") {
    return (
      <HandheldStateSurface
        slug="return-confirmation"
        style={styles.stateSurface}
      >
        <StatusPage
          hint={t("status.waitingHint")}
          testID="return-waiting"
          title={t("status.waitingTitle")}
          tone="waiting"
        />
      </HandheldStateSurface>
    );
  }
  if (state.phase === "unknown") {
    return (
      <HandheldStateSurface
        slug="return-confirmation"
        style={styles.stateSurface}
      >
        <StatusPage
          actionLabel={t("action.recover")}
          busy={state.busy}
          error={state.errorCode ? errorText(t, state.errorCode) : null}
          hint={t("status.unknownHint")}
          onAction={() => void presenter.recoverUnknown()}
          testID="return-unknown"
          title={t("status.unknownTitle")}
          tone="warning"
        />
      </HandheldStateSurface>
    );
  }
  if (state.phase === "success" && state.result) {
    return (
      <HandheldStateSurface
        slug="return-confirmation"
        style={styles.stateSurface}
      >
        <StatusPage
          actionLabel={t("action.reset")}
          hint={t("status.successOrder", {
            order: state.result.returnOrderSummary,
          })}
          onAction={() => presenter.reset()}
          secondary={t("status.successAmount", {
            amount: formatAud(state.result.refundAmountCents, locale),
          })}
          testID="return-success"
          title={t("status.successTitle")}
          tone="success"
        />
      </HandheldStateSurface>
    );
  }
  if (state.phase === "failed") {
    return (
      <HandheldStateSurface
        slug="return-confirmation"
        style={styles.stateSurface}
      >
        <StatusPage
          actionLabel={t("action.reset")}
          error={state.errorCode ? errorText(t, state.errorCode) : null}
          hint={t("status.failedHint")}
          onAction={() => presenter.reset()}
          testID="return-failed"
          title={t("status.failedTitle")}
          tone="danger"
        />
      </HandheldStateSurface>
    );
  }

  // 无单退货可自由选择现金/银行卡/礼券；
  // 有单退货默认按原支付方式退回，同时开放现金作为代替选项。
  // 不能原路退回的额度（如手工刷卡）不提供原方式按钮，只能现金代替；
  // 礼券只退回原礼券额度，代替刷卡/现金的执行链路尚未打通，暂不提供。
  const methods: readonly ReturnTenderMethod[] =
    state.mode === "no-receipt"
      ? ["cash", "card", "voucher"]
      : Array.from(
          new Set<ReturnTenderMethod>([
            ...state.capacities
              .filter((capacity) => capacity.originalRefundAvailable)
              .map((capacity) => capacity.method),
            "cash",
          ]),
        );
  const selectedQuantity = state.lines.reduce(
    (total, line) => total + line.selectedQuantity,
    0,
  );

  return (
    <HandheldStateSurface
      slug={confirmationOpen ? "return-confirmation" : "returns-lookup"}
      style={styles.stateSurface}
    >
      <SafeAreaView style={styles.safeArea} testID="return-screen">
      {renderHidScannerCapture?.({
        enabled: lookupVisible && !manualInputFocused,
        onScan: handleHidScan,
      })}
      <View style={styles.header}>
        <View style={styles.headerIdentity}>
          <Text style={styles.title}>{t("title")}</Text>
          <Text style={styles.subtitle}>{t("subtitle")}</Text>
        </View>
        {confirmationOpen ? (
          <HandheldActionButton
            label={t("action.backToSelection")}
            onPress={() => setConfirmationOpen(false)}
            sound="navigate"
            testID="return-confirmation-back"
            variant="secondary"
          />
        ) : onBack ? (
          <HandheldActionButton
            label={t("action.back")}
            onPress={onBack}
            sound="navigate"
            testID="return-back"
            variant="secondary"
          />
        ) : null}
      </View>

      {!confirmationOpen ? <View style={styles.modeTabs}>
        <ModeTab
          active={state.mode === "receipt"}
          disabled={state.busy}
          label={t("mode.receipt")}
          onPress={() => presenter.reset()}
          testID="return-mode-receipt"
        />
        <ModeTab
          active={state.mode === "no-receipt"}
          disabled={state.busy}
          label={t("mode.noReceipt")}
          onPress={() => presenter.beginNoReceipt()}
          testID="return-mode-no-receipt"
        />
      </View> : null}

      <View style={styles.workspace}>
        <View style={styles.mainColumn}>
          {!confirmationOpen && !receiptLoaded ? (
            <PosKeyboardAwareScrollView
              style={styles.editorScroll}
              testID="return-editor-keyboard-scroll"
            >
            {state.mode === "receipt" ? (
              <View style={styles.lookupCard}>
                <LabeledInput
                  autoSubmitOnScanIdle
                  onFocusChange={setManualInputFocused}
                  editable={!state.busy}
                  label={t("search.orderLabel")}
                  onChangeText={setOrderQuery}
                  onSubmitEditing={() => {
                    void presenter.loadReceipt(orderQuery).then((ok) => {
                      if (ok) setOrderQuery("");
                    });
                  }}
                  placeholder={t("search.orderPlaceholder")}
                  testID="return-order-query"
                  value={orderQuery}
                />
                <ActionButton
                  disabled={state.busy}
                  label={t("action.search")}
                  onPress={() => {
                    void presenter.loadReceipt(orderQuery).then((ok) => {
                      if (ok) setOrderQuery("");
                    });
                  }}
                  testID="return-order-search"
                />
              </View>
            ) : (
              <View style={styles.noReceiptPanel}>
                <View style={styles.lookupCard}>
                  <LabeledInput
                    autoSubmitOnScanIdle
                    onFocusChange={setManualInputFocused}
                    editable={!state.busy}
                    label={t("search.productLabel")}
                    onChangeText={setProductQuery}
                    onSubmitEditing={() => {
                      void presenter
                        .addNoReceiptProduct(productQuery)
                        .then((ok) => {
                          if (ok) setProductQuery("");
                        });
                    }}
                    placeholder={t("search.productPlaceholder")}
                    testID="return-product-query"
                    value={productQuery}
                  />
                  <ActionButton
                    disabled={state.busy}
                    label={t("action.addProduct")}
                    onPress={() => {
                      void presenter
                        .addNoReceiptProduct(productQuery)
                        .then((ok) => {
                          if (ok) setProductQuery("");
                        });
                    }}
                    testID="return-product-add"
                    tone="secondary"
                  />
                </View>
                <View style={styles.openItemRow}>
                  <LabeledInput
                    editable={!state.busy}
                    label={t("openItem.nameLabel")}
                    onFocusChange={setManualInputFocused}
                    onChangeText={setOpenItemName}
                    placeholder={t("openItem.namePlaceholder")}
                    testID="return-open-item-name"
                    value={openItemName}
                  />
                  <View style={styles.amountInput}>
                    <LabeledInput
                      editable={!state.busy}
                      keyboardType="decimal-pad"
                      label={t("openItem.amountLabel")}
                      onFocusChange={setManualInputFocused}
                      onChangeText={setOpenItemAmount}
                      placeholder={t("openItem.amountPlaceholder")}
                      testID="return-open-item-amount"
                      value={openItemAmount}
                    />
                  </View>
                  <ActionButton
                    disabled={state.busy}
                    label={t("action.addOpenItem")}
                    onPress={() => {
                      const cents = parsePositiveAudCents(openItemAmount);
                      void presenter
                        .addNoReceiptOpenItem(
                          openItemName,
                          cents ?? Number.NaN,
                        )
                        .then((ok) => {
                          if (ok) {
                            setOpenItemName("");
                            setOpenItemAmount("");
                          }
                        });
                    }}
                    testID="return-open-item-add"
                    tone="secondary"
                  />
                </View>
              </View>
            )}
            </PosKeyboardAwareScrollView>
          ) : null}

          {state.orderSummary ? (
            <View style={styles.orderBanner} testID="return-order-summary">
              <View style={styles.orderIdentity}>
                <Text style={styles.orderTitle}>
                  {t("order.summary", { order: state.orderSummary })}
                </Text>
                <View style={styles.orderStatusBadge}>
                  <View style={styles.orderStatusDot} />
                  <Text style={styles.orderSource}>
                    {t(
                      state.loadedFrom === "remote"
                        ? "order.remote"
                        : "order.local",
                    )}
                  </Text>
                </View>
              </View>
              {!confirmationOpen && receiptLoaded ? (
                <ActionButton
                  disabled={state.busy}
                  label={t("action.requery")}
                  onPress={() => presenter.reset()}
                  testID="return-order-requery"
                  tone="quiet"
                />
              ) : null}
            </View>
          ) : null}
          {state.returnRecordsMayBeStale ? (
            <Text style={styles.warning} testID="return-stale-warning">
              {t("order.stale")}
            </Text>
          ) : null}
          {state.errorCode ? (
            <Text style={styles.errorNotice} testID="return-error">
              {errorText(t, state.errorCode)}
            </Text>
          ) : null}

          {!confirmationOpen ? (
            receiptLoaded ? (
              <>
                <View style={styles.sectionHeader}>
                  <Text style={styles.sectionTitle}>{t("lines.title")}</Text>
                  <Text style={styles.sectionCount}>
                    {returnableCountText(locale, state.lines.length)}
                  </Text>
                </View>
                <ScrollView
                  contentContainerStyle={styles.lineList}
                  style={styles.lineScroll}
                  testID="return-line-list"
                >
                  {state.lines.length ? (
                    state.lines.map((line) => (
                      <ReturnLineRow
                        key={line.id}
                        line={line}
                        locale={locale}
                        onDecrease={() => presenter.decrementLine(line.id)}
                        onIncrease={() => presenter.incrementLine(line.id)}
                        t={t}
                      />
                    ))
                  ) : (
                    <Text style={styles.emptyText}>
                      {t("lines.noneReturnable")}
                    </Text>
                  )}
                </ScrollView>
              </>
            ) : (
              <HandheldSection
                testID="return-lookup-result"
                title={t("lines.title")}
              >
                <Text style={styles.lookupResultCount}>
                  {state.lines.length
                    ? returnableCountText(locale, state.lines.length)
                    : t("lines.empty")}
                </Text>
                {state.phase === "loading" ? (
                  <ActivityIndicator
                    color={posColors.orange}
                    testID="return-loading"
                  />
                ) : null}
                {state.lines.length ? (
                  <HandheldActionButton
                    label={locale === "zh" ? "查看退货明细" : "Review return"}
                    onPress={() => setConfirmationOpen(true)}
                    testID="return-open-confirmation"
                  />
                ) : null}
              </HandheldSection>
            )
          ) : (
            <>
              <View style={styles.sectionHeader}>
                <Text style={styles.sectionTitle}>{t("lines.title")}</Text>
              </View>
              <ScrollView
                contentContainerStyle={styles.lineList}
                style={styles.lineScroll}
                testID="return-line-list"
              >
                {state.lines.map((line) => (
                  <ReturnLineRow
                    key={line.id}
                    line={line}
                    locale={locale}
                    onDecrease={() => presenter.decrementLine(line.id)}
                    onIncrease={() => presenter.incrementLine(line.id)}
                    t={t}
                  />
                ))}
              </ScrollView>
            </>
          )}
        </View>

        {confirmationOpen ? <View style={styles.summaryColumn}>
          <Text style={styles.sectionTitle}>{t("capacity.title")}</Text>
          {state.mode === "no-receipt" ? (
            <Text style={styles.capacityHint}>{t("capacity.noReceipt")}</Text>
          ) : (
            state.capacities.map((capacity) => (
              <Text
                key={capacity.method}
                style={styles.capacityRow}
                testID={`return-capacity-${capacity.method}`}
              >
                {t(
                  capacity.originalRefundAvailable
                    ? "capacity.remaining"
                    : "capacity.substituteOnly",
                  {
                    method: t(`method.${capacity.method}`),
                    amount: formatAud(capacity.remainingCents, locale),
                  },
                )}
              </Text>
            ))
          )}

          <View style={styles.divider} />
          <Text style={styles.sectionTitle}>{t("summary.title")}</Text>
          <Text style={styles.summaryLabel}>{t("summary.selected")}</Text>
          <Text style={styles.total} testID="return-selected-total">
            {formatAud(state.selectedTotalCents, locale)}
          </Text>
          <Text style={styles.summaryLabel}>{t("summary.method")}</Text>
          <View style={styles.methodGrid}>
            {methods.map((method) => (
              <MethodButton
                active={state.preferredMethod === method}
                key={method}
                label={t(`method.${method}`)}
                onPress={() => presenter.selectMethod(method)}
                testID={`return-method-${method}`}
              />
            ))}
          </View>
          <Text style={styles.rule}>
            {state.mode === "receipt" && state.preferredMethod === null
              ? t("summary.ruleReceiptDefault")
              : t("summary.rule")}
          </Text>
          <View style={styles.confirmArea}>
            <ActionButton
              disabled={!state.canConfirm || state.busy}
              label={t("action.confirm")}
              onPress={() => void presenter.confirm()}
              sound="danger"
              testID="return-confirm"
            />
          </View>
        </View> : null}
      </View>
      {!confirmationOpen && receiptLoaded && state.lines.length ? (
        <View style={styles.selectionFooter} testID="return-selection-footer">
          <View style={styles.selectionSummary}>
            <Text style={styles.selectionCount} testID="return-selected-count">
              {selectedCountText(locale, selectedQuantity)}
            </Text>
            <View style={styles.selectionTotalRow}>
              <Text style={styles.selectionTotalLabel}>{t("summary.total")}</Text>
              <Text
                style={styles.selectionTotal}
                testID="return-selected-total"
              >
                {formatAud(state.selectedTotalCents, locale)}
              </Text>
            </View>
          </View>
          <View style={styles.selectionNext}>
            <ActionButton
              disabled={!state.canConfirm || state.busy}
              label={t("action.next")}
              onPress={() => setConfirmationOpen(true)}
              sound="navigate"
              testID="return-next"
            />
          </View>
        </View>
      ) : null}
      </SafeAreaView>
    </HandheldStateSurface>
  );
}

function ReturnLineRow({
  line,
  locale,
  onDecrease,
  onIncrease,
  t,
}: Readonly<{
  line: ReturnPresenterLine;
  locale: ReturnLocale;
  onDecrease(): void;
  onIncrease(): void;
  t(
    key: ReturnCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ): string;
}>) {
  const selected = line.selectedQuantity > 0;

  return (
    <View
      style={[styles.lineRow, selected && styles.lineRowSelected]}
      testID={`return-row-${line.id}`}
    >
      <View style={styles.lineIdentity}>
        <Text numberOfLines={2} style={styles.lineName}>
          {line.displayName}
        </Text>
        {line.itemNumber ? (
          <Text style={styles.lineMeta}>
            {t("line.itemNumber", { itemNumber: line.itemNumber })}
          </Text>
        ) : null}
        <Text style={styles.lineAmount}>
          {t("line.refund", {
            amount: formatAud(line.amountCents, locale),
          })}
        </Text>
      </View>
      <View style={styles.lineSelection}>
        <Text style={styles.quantityText}>
          {line.sourceKind === "receipt"
            ? t("line.quantity", {
                selected: line.selectedQuantity,
                available: line.availableQuantity,
              })
            : t("line.quantityNoReceipt", {
                selected: line.selectedQuantity,
              })}
        </Text>
        <View style={styles.quantityControl}>
          <IconButton
            disabled={line.selectedQuantity === 0}
            label="−"
            onPress={onDecrease}
            testID={`return-decrease-${line.id}`}
            accessibilityLabel={t("action.decrease")}
          />
          <Text
            style={styles.quantityValue}
            testID={`return-quantity-${line.id}`}
          >
            {line.selectedQuantity}
          </Text>
          <IconButton
            disabled={
              line.sourceKind === "receipt" &&
              line.selectedQuantity >= line.availableQuantity
            }
            label="+"
            onPress={onIncrease}
            testID={`return-increase-${line.id}`}
            accessibilityLabel={t("action.increase")}
          />
        </View>
      </View>
    </View>
  );
}

function StatusPage({
  actionLabel,
  busy = false,
  error,
  hint,
  onAction,
  secondary,
  testID,
  title,
  tone,
}: Readonly<{
  actionLabel?: string;
  busy?: boolean;
  error?: string | null;
  hint: string;
  onAction?(): void;
  secondary?: string;
  testID: string;
  title: string;
  tone: "waiting" | "warning" | "success" | "danger";
}>) {
  return (
    <SafeAreaView style={styles.statusSafeArea} testID={testID}>
      <View
        style={[
          styles.statusMark,
          tone === "success"
            ? styles.statusSuccess
            : tone === "danger"
              ? styles.statusDanger
              : tone === "warning"
                ? styles.statusWarning
                : styles.statusWaiting,
        ]}
      >
        {tone === "waiting" ? (
          <ActivityIndicator color={posColors.blue} size="large" />
        ) : (
          <Text style={styles.statusMarkText}>
            {tone === "success" ? "✓" : tone === "danger" ? "!" : "?"}
          </Text>
        )}
      </View>
      <Text style={styles.statusTitle}>{title}</Text>
      <Text style={styles.statusHint}>{hint}</Text>
      {secondary ? <Text style={styles.statusSecondary}>{secondary}</Text> : null}
      {error ? <Text style={styles.statusError}>{error}</Text> : null}
      {actionLabel && onAction ? (
        <ActionButton
          disabled={busy}
          label={actionLabel}
          onPress={onAction}
          testID={`${testID}-action`}
        />
      ) : null}
    </SafeAreaView>
  );
}

function LabeledInput({
  autoSubmitOnScanIdle = false,
  editable,
  keyboardType,
  label,
  onChangeText,
  onFocusChange,
  onSubmitEditing,
  placeholder,
  testID,
  value,
}: Readonly<{
  autoSubmitOnScanIdle?: boolean;
  editable: boolean;
  keyboardType?: "default" | "decimal-pad";
  label: string;
  onChangeText(value: string): void;
  onFocusChange?(focused: boolean): void;
  onSubmitEditing?(): void;
  placeholder: string;
  testID: string;
  value: string;
}>) {
  return (
    <View style={styles.inputGroup}>
      <Text style={styles.inputLabel}>{label}</Text>
      <PosKeyboardAwareTextInput
        autoCapitalize="none"
        autoCorrect={false}
        autoSubmitOnScanIdle={autoSubmitOnScanIdle}
        editable={editable}
        keyboardType={keyboardType}
        onBlur={() => onFocusChange?.(false)}
        onChangeText={onChangeText}
        onFocus={() => onFocusChange?.(true)}
        onSubmitEditing={onSubmitEditing}
        placeholder={placeholder}
        placeholderTextColor={posColors.mutedInk}
        returnKeyType="done"
        style={styles.input}
        testID={testID}
        value={value}
      />
    </View>
  );
}

function ModeTab({
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
      accessibilityRole="tab"
      accessibilityState={{ disabled, selected: active }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.modeTab, active && styles.modeTabActive]}
      testID={testID}
    >
      <Text style={[styles.modeTabText, active && styles.modeTabTextActive]}>
        {label}
      </Text>
    </PosPressable>
  );
}

function MethodButton({
  active,
  label,
  onPress,
  testID,
}: Readonly<{
  active: boolean;
  label: string;
  onPress(): void;
  testID: string;
}>) {
  return (
    <PosPressable
      accessibilityRole="radio"
      accessibilityState={{ checked: active }}
      onPress={onPress}
      style={[styles.methodButton, active && styles.methodButtonActive]}
      testID={testID}
    >
      <Text
        style={[styles.methodButtonText, active && styles.methodButtonTextActive]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function IconButton({
  accessibilityLabel,
  disabled,
  label,
  onPress,
  testID,
}: Readonly<{
  accessibilityLabel: string;
  disabled: boolean;
  label: string;
  onPress(): void;
  testID: string;
}>) {
  return (
    <PosPressable
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.iconButton, disabled && styles.buttonDisabled]}
      testID={testID}
    >
      <Text style={styles.iconButtonText}>{label}</Text>
    </PosPressable>
  );
}

function ActionButton({
  disabled = false,
  label,
  onPress,
  sound = "tap",
  testID,
  tone = "primary",
}: Readonly<{
  disabled?: boolean;
  label: string;
  onPress(): void;
  sound?: "tap" | "navigate" | "danger";
  testID: string;
  tone?: "primary" | "secondary" | "quiet";
}>) {
  return (
    <PosPressable
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      sound={sound}
      style={({ pressed }) => [
        styles.actionButton,
        tone === "primary"
          ? styles.actionPrimary
          : tone === "secondary"
            ? styles.actionSecondary
            : styles.actionQuiet,
        disabled && styles.buttonDisabled,
        pressed && !disabled && styles.pressed,
      ]}
      testID={testID}
    >
      <Text
        style={[
          styles.actionButtonText,
          tone !== "primary" && styles.actionButtonTextDark,
        ]}
      >
        {label}
      </Text>
    </PosPressable>
  );
}

function errorText(
  t: (key: ReturnCopyKey) => string,
  code: `RETURN_${string}`,
): string {
  return t(`error.${code}` as ReturnCopyKey);
}

function returnableCountText(locale: ReturnLocale, count: number): string {
  if (locale === "zh") return `${count} 个可退商品`;
  return `${count} returnable item${count === 1 ? "" : "s"}`;
}

function selectedCountText(locale: ReturnLocale, count: number): string {
  if (locale === "zh") return `已选 ${count} 件`;
  return `${count} item${count === 1 ? "" : "s"} selected`;
}

export function parsePositiveAudCents(value: string): number | null {
  const match = /^\s*(\d+)(?:\.(\d{1,2}))?\s*$/.exec(value);
  if (!match) return null;
  const whole = Number(match[1] ?? "0");
  const fraction = Number((match[2] ?? "").padEnd(2, "0"));
  const cents = whole * 100 + fraction;
  return Number.isSafeInteger(cents) && cents > 0 ? cents : null;
}

function formatAud(cents: number, locale: ReturnLocale): string {
  return new Intl.NumberFormat(locale === "zh" ? "zh-AU" : "en-AU", {
    style: "currency",
    currency: "AUD",
    currencyDisplay: "narrowSymbol",
  }).format(cents / 100);
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
    minHeight: 64,
    paddingHorizontal: 16,
    paddingVertical: 8,
    backgroundColor: posColors.surface,
    borderBottomColor: posColors.border,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 16,
  },
  headerIdentity: {
    flex: 1,
  },
  title: {
    color: posColors.ink,
    fontSize: 24,
    fontWeight: "800",
  },
  subtitle: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 17,
    marginTop: 3,
  },
  modeTabs: {
    flexDirection: "row",
    gap: 8,
    paddingHorizontal: 16,
    paddingTop: 8,
  },
  modeTab: {
    flex: 1,
    minHeight: RETURN_MIN_TOUCH_TARGET,
    minWidth: 0,
    paddingHorizontal: 20,
    alignItems: "center",
    justifyContent: "center",
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 6,
    backgroundColor: posColors.surface,
  },
  modeTabActive: {
    borderColor: posColors.orange,
    backgroundColor: posColors.orangeSoft,
  },
  modeTabText: {
    color: posColors.mutedInk,
    fontSize: 15,
    fontWeight: "700",
  },
  modeTabTextActive: {
    color: posColors.orange,
  },
  workspace: {
    flex: 1,
    flexDirection: "column",
    gap: 8,
    minHeight: 0,
    padding: 16,
    paddingTop: 8,
  },
  mainColumn: {
    flex: 1,
    minHeight: 0,
    minWidth: 0,
  },
  editorScroll: {
    // 查询区按内容高度布局；小屏溢出时允许收缩并独立滚动，避免挤走结果列表。
    flexGrow: 0,
    flexShrink: 1,
  },
  summaryColumn: {
    flex: 0,
    minWidth: 0,
    padding: 16,
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 6,
    backgroundColor: posColors.surface,
  },
  lookupCard: {
    flexDirection: "column",
    alignItems: "stretch",
    gap: 8,
  },
  noReceiptPanel: {
    gap: 10,
  },
  openItemRow: {
    flexDirection: "column",
    alignItems: "stretch",
    gap: 8,
  },
  amountInput: {
    maxWidth: "100%",
    flex: 0,
  },
  inputGroup: {
    flex: 1,
    gap: 5,
  },
  inputLabel: {
    color: posColors.ink,
    fontSize: 13,
    fontWeight: "700",
  },
  input: {
    minHeight: RETURN_MIN_TOUCH_TARGET,
    paddingHorizontal: 13,
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 8,
    backgroundColor: posColors.surface,
    color: posColors.ink,
    fontSize: 16,
  },
  orderBanner: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    justifyContent: "space-between",
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderColor: posColors.border,
    borderLeftColor: posColors.green,
    borderLeftWidth: 4,
    borderRadius: 6,
    borderRightWidth: 1,
    borderTopWidth: 1,
    borderBottomWidth: 1,
    backgroundColor: posColors.surface,
  },
  orderIdentity: {
    flex: 1,
    minWidth: 0,
  },
  orderTitle: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "800",
  },
  orderSource: {
    color: posColors.green,
    fontSize: 12,
    fontWeight: "800",
  },
  orderStatusBadge: {
    alignItems: "center",
    alignSelf: "flex-start",
    backgroundColor: posColors.greenSoft,
    borderRadius: 12,
    flexDirection: "row",
    gap: 6,
    marginTop: 5,
    minHeight: 24,
    paddingHorizontal: 8,
  },
  orderStatusDot: {
    backgroundColor: posColors.green,
    borderRadius: 3,
    height: 6,
    width: 6,
  },
  warning: {
    marginTop: 8,
    padding: 10,
    color: "#6F4A00",
    backgroundColor: "#FFF4D9",
    borderRadius: 6,
    fontSize: 13,
    fontWeight: "600",
  },
  errorNotice: {
    marginTop: 8,
    padding: 10,
    color: posColors.red,
    backgroundColor: posColors.redSoft,
    borderRadius: 6,
    fontSize: 13,
    fontWeight: "700",
  },
  lookupResultCount: {
    color: posColors.mutedInk,
    fontSize: 14,
    lineHeight: 20,
  },
  sectionHeader: {
    minHeight: 36,
    marginTop: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  sectionTitle: {
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
  },
  sectionCount: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
  },
  lineScroll: {
    flex: 1,
    minHeight: 0,
  },
  lineList: {
    gap: 8,
    paddingBottom: 12,
  },
  lineRow: {
    minHeight: 104,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    padding: 10,
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 6,
    backgroundColor: posColors.surface,
  },
  lineRowSelected: {
    backgroundColor: posColors.orangeSoft,
    borderColor: posColors.orange,
  },
  lineIdentity: {
    flex: 1,
    minWidth: 0,
  },
  lineName: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "800",
  },
  lineMeta: {
    color: posColors.mutedInk,
    fontSize: 12,
    marginTop: 2,
  },
  lineAmount: {
    color: posColors.green,
    fontSize: 14,
    fontWeight: "800",
    marginTop: 5,
  },
  lineSelection: {
    alignItems: "flex-end",
    gap: 6,
  },
  quantityControl: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
  },
  quantityText: {
    textAlign: "right",
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
  },
  quantityValue: {
    color: posColors.ink,
    fontSize: 20,
    fontWeight: "900",
    minWidth: 28,
    textAlign: "center",
  },
  iconButton: {
    width: RETURN_MIN_TOUCH_TARGET,
    height: RETURN_MIN_TOUCH_TARGET,
    alignItems: "center",
    justifyContent: "center",
    borderColor: posColors.orange,
    borderWidth: 1,
    borderRadius: 6,
    backgroundColor: posColors.surface,
  },
  iconButtonText: {
    color: posColors.orange,
    fontSize: 24,
    fontWeight: "700",
  },
  emptyText: {
    padding: 28,
    textAlign: "center",
    color: posColors.mutedInk,
    fontSize: 14,
    borderColor: posColors.border,
    borderWidth: 1,
    borderStyle: "dashed",
    borderRadius: 8,
  },
  capacityHint: {
    color: posColors.mutedInk,
    fontSize: 13,
    lineHeight: 19,
    marginTop: 10,
  },
  capacityRow: {
    minHeight: RETURN_MIN_TOUCH_TARGET,
    paddingVertical: 11,
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "700",
    borderBottomColor: posColors.border,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  divider: {
    height: 1,
    marginVertical: 16,
    backgroundColor: posColors.border,
  },
  summaryLabel: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
    marginTop: 14,
    textTransform: "uppercase",
  },
  total: {
    color: posColors.ink,
    fontSize: 34,
    fontWeight: "900",
    marginTop: 2,
  },
  methodGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    marginTop: 8,
  },
  methodButton: {
    minHeight: RETURN_MIN_TOUCH_TARGET,
    minWidth: 92,
    paddingHorizontal: 12,
    alignItems: "center",
    justifyContent: "center",
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 8,
    backgroundColor: posColors.surface,
  },
  methodButtonActive: {
    borderColor: posColors.blue,
    backgroundColor: posColors.blueSoft,
  },
  methodButtonText: {
    color: posColors.mutedInk,
    fontSize: 13,
    fontWeight: "700",
  },
  methodButtonTextActive: {
    color: posColors.blue,
  },
  rule: {
    color: posColors.mutedInk,
    fontSize: 12,
    lineHeight: 18,
    marginTop: 14,
  },
  confirmArea: {
    flex: 1,
    justifyContent: "flex-end",
    paddingTop: 18,
  },
  selectionFooter: {
    alignItems: "center",
    backgroundColor: posColors.surface,
    borderTopColor: posColors.border,
    borderTopWidth: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 80,
    padding: 16,
  },
  selectionSummary: {
    flex: 1,
    minWidth: 0,
  },
  selectionCount: {
    color: posColors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
  },
  selectionTotalRow: {
    alignItems: "baseline",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    marginTop: 3,
  },
  selectionTotalLabel: {
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "800",
  },
  selectionTotal: {
    color: posColors.green,
    fontSize: 22,
    fontWeight: "900",
  },
  selectionNext: {
    width: 144,
  },
  actionButton: {
    minHeight: RETURN_MIN_TOUCH_TARGET,
    minWidth: 116,
    paddingHorizontal: 18,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 6,
  },
  actionPrimary: {
    backgroundColor: posColors.orange,
  },
  actionSecondary: {
    borderColor: posColors.blue,
    borderWidth: 1,
    backgroundColor: posColors.blueSoft,
  },
  actionQuiet: {
    borderColor: posColors.border,
    borderWidth: 1,
    backgroundColor: posColors.surface,
  },
  actionButtonText: {
    color: "#FFFFFF",
    fontSize: 14,
    fontWeight: "800",
  },
  actionButtonTextDark: {
    color: posColors.ink,
  },
  buttonDisabled: {
    opacity: 0.38,
  },
  pressed: {
    opacity: 0.78,
  },
  statusSafeArea: {
    flex: 1,
    padding: 32,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: posColors.canvas,
  },
  statusMark: {
    width: 82,
    height: 82,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 41,
    marginBottom: 20,
  },
  statusSuccess: {
    backgroundColor: posColors.greenSoft,
  },
  statusDanger: {
    backgroundColor: posColors.redSoft,
  },
  statusWarning: {
    backgroundColor: "#FFF4D9",
  },
  statusWaiting: {
    backgroundColor: posColors.blueSoft,
  },
  statusMarkText: {
    color: posColors.ink,
    fontSize: 38,
    fontWeight: "900",
  },
  statusTitle: {
    color: posColors.ink,
    fontSize: 30,
    fontWeight: "900",
    textAlign: "center",
  },
  statusHint: {
    maxWidth: 620,
    marginTop: 10,
    color: posColors.mutedInk,
    fontSize: 16,
    lineHeight: 23,
    textAlign: "center",
  },
  statusSecondary: {
    marginTop: 12,
    color: posColors.green,
    fontSize: 22,
    fontWeight: "800",
  },
  statusError: {
    maxWidth: 620,
    marginVertical: 16,
    padding: 12,
    color: posColors.red,
    backgroundColor: posColors.redSoft,
    borderRadius: 8,
    fontSize: 14,
    fontWeight: "700",
    textAlign: "center",
  },
});
