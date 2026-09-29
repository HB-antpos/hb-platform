import { useEffect, useRef, useState, useSyncExternalStore } from "react";
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
import { posColors } from "@/ui/theme";

export const RETURN_MIN_TOUCH_TARGET = 44;

type ReturnScreenProps = Readonly<{
  presenter: ReturnPresenter;
  locale?: ReturnLocale;
  initialReceiptQuery?: string | null;
  onBack?(): void;
}>;

export function ReturnScreen({
  presenter,
  locale: localeOverride,
  initialReceiptQuery,
  onBack,
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

  if (state.phase === "submitting") {
    return (
      <StatusPage
        hint={t("status.waitingHint")}
        testID="return-waiting"
        title={t("status.waitingTitle")}
        tone="waiting"
      />
    );
  }
  if (state.phase === "unknown") {
    return (
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
    );
  }
  if (state.phase === "success" && state.result) {
    return (
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
    );
  }
  if (state.phase === "failed") {
    return (
      <StatusPage
        actionLabel={t("action.reset")}
        error={state.errorCode ? errorText(t, state.errorCode) : null}
        hint={t("status.failedHint")}
        onAction={() => presenter.reset()}
        testID="return-failed"
        title={t("status.failedTitle")}
        tone="danger"
      />
    );
  }

  // 无单退货可自由选择现金/银行卡/礼券；
  // 有单退货默认按原支付方式退回，同时开放现金、礼券作为代替选项。
  // 不能原路退回的额度（如手工刷卡）不提供原方式按钮，只能现金或礼券代替。
  const methods: readonly ReturnTenderMethod[] =
    state.mode === "no-receipt"
      ? ["cash", "card", "voucher"]
      : Array.from(
          new Set<ReturnTenderMethod>([
            ...state.capacities
              .filter((capacity) => capacity.originalRefundAvailable)
              .map((capacity) => capacity.method),
            "cash",
            "voucher",
          ]),
        );

  return (
    <SafeAreaView style={styles.safeArea} testID="return-screen">
      <View style={styles.header}>
        <View style={styles.headerIdentity}>
          <Text style={styles.title}>{t("title")}</Text>
          <Text style={styles.subtitle}>{t("subtitle")}</Text>
        </View>
        {onBack ? (
          <ActionButton
            label={t("action.back")}
            onPress={onBack}
            sound="navigate"
            testID="return-back"
            tone="quiet"
          />
        ) : null}
      </View>

      <View style={styles.modeTabs}>
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
      </View>

      <View style={styles.workspace}>
        <View style={styles.mainColumn}>
          <PosKeyboardAwareScrollView
            style={styles.editorScroll}
            testID="return-editor-keyboard-scroll"
          >
            {state.mode === "receipt" ? (
              <View style={styles.lookupCard}>
                <LabeledInput
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

          {state.orderSummary ? (
            <View style={styles.orderBanner} testID="return-order-summary">
              <Text style={styles.orderTitle}>
                {t("order.summary", { order: state.orderSummary })}
              </Text>
              <Text style={styles.orderSource}>
                {t(
                  state.loadedFrom === "remote"
                    ? "order.remote"
                    : "order.local",
                )}
              </Text>
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

          <View style={styles.sectionHeader}>
            <Text style={styles.sectionTitle}>{t("lines.title")}</Text>
            {state.phase === "loading" ? (
              <ActivityIndicator
                color={posColors.orange}
                testID="return-loading"
              />
            ) : null}
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
              <Text style={styles.emptyText}>{t("lines.empty")}</Text>
            )}
          </ScrollView>
        </View>

        <View style={styles.summaryColumn}>
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
        </View>
      </View>
    </SafeAreaView>
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
  return (
    <View style={styles.lineRow} testID={`return-row-${line.id}`}>
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
      <View style={styles.quantityControl}>
        <IconButton
          disabled={line.selectedQuantity === 0}
          label="−"
          onPress={onDecrease}
          testID={`return-decrease-${line.id}`}
          accessibilityLabel={t("action.decrease")}
        />
        <Text style={styles.quantityText} testID={`return-quantity-${line.id}`}>
          {line.sourceKind === "receipt"
            ? t("line.quantity", {
                selected: line.selectedQuantity,
                available: line.availableQuantity,
              })
            : t("line.quantityNoReceipt", {
                selected: line.selectedQuantity,
              })}
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
  editable,
  keyboardType,
  label,
  onChangeText,
  onSubmitEditing,
  placeholder,
  testID,
  value,
}: Readonly<{
  editable: boolean;
  keyboardType?: "default" | "decimal-pad";
  label: string;
  onChangeText(value: string): void;
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
        editable={editable}
        keyboardType={keyboardType}
        onChangeText={onChangeText}
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
  }).format(cents / 100);
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: posColors.canvas,
  },
  header: {
    minHeight: 78,
    paddingHorizontal: 24,
    paddingVertical: 14,
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
    fontSize: 28,
    fontWeight: "800",
  },
  subtitle: {
    color: posColors.mutedInk,
    fontSize: 14,
    marginTop: 3,
  },
  modeTabs: {
    flexDirection: "row",
    gap: 8,
    paddingHorizontal: 24,
    paddingTop: 12,
  },
  modeTab: {
    minHeight: RETURN_MIN_TOUCH_TARGET,
    minWidth: 150,
    paddingHorizontal: 20,
    alignItems: "center",
    justifyContent: "center",
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 8,
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
    flexDirection: "row",
    gap: 16,
    padding: 24,
    paddingTop: 14,
  },
  mainColumn: {
    flex: 1.7,
    minWidth: 0,
  },
  editorScroll: {
    flexGrow: 0,
  },
  summaryColumn: {
    flex: 1,
    minWidth: 290,
    maxWidth: 420,
    padding: 18,
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 10,
    backgroundColor: posColors.surface,
  },
  lookupCard: {
    flexDirection: "row",
    alignItems: "flex-end",
    gap: 10,
  },
  noReceiptPanel: {
    gap: 10,
  },
  openItemRow: {
    flexDirection: "row",
    alignItems: "flex-end",
    gap: 10,
  },
  amountInput: {
    maxWidth: 190,
    flex: 0.5,
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
    marginTop: 12,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderLeftColor: posColors.blue,
    borderLeftWidth: 4,
    backgroundColor: posColors.blueSoft,
  },
  orderTitle: {
    color: posColors.ink,
    fontSize: 16,
    fontWeight: "800",
  },
  orderSource: {
    color: posColors.blue,
    fontSize: 13,
    marginTop: 2,
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
  sectionHeader: {
    minHeight: RETURN_MIN_TOUCH_TARGET,
    marginTop: 10,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  sectionTitle: {
    color: posColors.ink,
    fontSize: 17,
    fontWeight: "800",
  },
  lineScroll: {
    flex: 1,
  },
  lineList: {
    gap: 8,
    paddingBottom: 12,
  },
  lineRow: {
    minHeight: 94,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: 12,
    padding: 14,
    borderColor: posColors.border,
    borderWidth: 1,
    borderRadius: 8,
    backgroundColor: posColors.surface,
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
  quantityControl: {
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
  },
  quantityText: {
    minWidth: 106,
    textAlign: "center",
    color: posColors.ink,
    fontSize: 14,
    fontWeight: "700",
  },
  iconButton: {
    width: RETURN_MIN_TOUCH_TARGET,
    height: RETURN_MIN_TOUCH_TARGET,
    alignItems: "center",
    justifyContent: "center",
    borderColor: posColors.blue,
    borderWidth: 1,
    borderRadius: 8,
    backgroundColor: posColors.blueSoft,
  },
  iconButtonText: {
    color: posColors.blue,
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
  actionButton: {
    minHeight: RETURN_MIN_TOUCH_TARGET,
    minWidth: 116,
    paddingHorizontal: 18,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 8,
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
