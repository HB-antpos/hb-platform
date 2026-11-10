import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import { ActivityIndicator, ScrollView, Text, View } from "react-native";

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

import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import {
  PosKeyboardAwareScrollView,
  PosKeyboardAwareTextInput,
} from "@/ui/controls/pos-keyboard-aware-scroll-view";
import { PosPill } from "@/ui/controls/pos-pill";
import { PosPressable } from "@/ui/controls/pos-pressable";
import { PosSegmented } from "@/ui/controls/pos-segmented";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  usePosThemeVersion,
  type PosPalette,
} from "@/ui/theme";

export const RETURN_MIN_TOUCH_TARGET = 44;

/** 退款方式大块的图标；颜色只存色名，渲染时再按当前主题取色。 */
const RETURN_METHOD_ICON: Record<
  ReturnTenderMethod,
  Readonly<{ icon: PosIconName; color: keyof PosPalette }>
> = {
  card: { icon: "credit-card-outline", color: "blue" },
  cash: { icon: "cash", color: "green" },
  voucher: { icon: "wallet-giftcard", color: "yellow" },
  installment: { icon: "calendar-clock-outline", color: "ink" },
};

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
  usePosThemeVersion();
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
        pageSubtitle={t("subtitle")}
        pageTitle={t("title")}
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
        error={state.errorCode ? errorText(t, state.errorCode, state.requiredVoucherRefundCents) : null}
        hint={t("status.unknownHint")}
        pageSubtitle={t("subtitle")}
        pageTitle={t("title")}
        onAction={() => void presenter.recoverUnknown()}
        testID="return-unknown"
        title={t("status.unknownTitle")}
        tone="warning"
        resolution={
          state.supervisorResolutionAvailable
            ? {
                busy: state.busy,
                onResolve: (finding, evidenceReference, note) =>
                  void presenter.resolveBySupervisor({ finding, evidenceReference, note }),
                t,
                waitingRecorded: state.supervisorWaitingRecorded,
              }
            : undefined
        }
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
        pageSubtitle={t("subtitle")}
        pageTitle={t("title")}
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
        error={state.errorCode ? errorText(t, state.errorCode, state.requiredVoucherRefundCents) : null}
        hint={t("status.failedHint")}
        onAction={() => presenter.reset()}
        pageSubtitle={t("subtitle")}
        pageTitle={t("title")}
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

  // 只有可原路退回的原支付方式才标「原支付方式」；无小票退货没有原支付。
  const originalMethods = new Set<ReturnTenderMethod>(
    state.mode === "receipt"
      ? state.capacities
          .filter((capacity) => capacity.originalRefundAvailable)
          .map((capacity) => capacity.method)
      : [],
  );
  const submitReceiptQuery = () => {
    void presenter.loadReceipt(orderQuery).then((ok) => {
      if (ok) setOrderQuery("");
    });
  };
  const submitProductQuery = () => {
    void presenter.addNoReceiptProduct(productQuery).then((ok) => {
      if (ok) setProductQuery("");
    });
  };

  return (
    <PosScreenFrame
      active="returns"
      header={
        <PosPageHeader
          actions={
            // 模式切换放在页头右侧：当前是哪种退货一眼可见。
            <PosSegmented
              disabled={state.busy}
              onChange={(mode) => {
                if (mode === "receipt") presenter.reset();
                else presenter.beginNoReceipt();
              }}
              // 与改版前的页签一致：再点一次当前模式 = 清空并重新开始这种退货。
              onReselect={(mode) => {
                if (mode === "receipt") presenter.reset();
                else presenter.beginNoReceipt();
              }}
              options={[
                {
                  icon: "receipt-text-outline",
                  label: t("mode.receipt"),
                  testID: "return-mode-receipt",
                  value: "receipt",
                },
                {
                  icon: "receipt-text-remove-outline",
                  label: t("mode.noReceipt"),
                  testID: "return-mode-no-receipt",
                  value: "no-receipt",
                },
              ]}
              style={styles.modeSwitch}
              value={state.mode}
            />
          }
          backLabel={t("action.back")}
          backTestID="return-back"
          onBack={onBack}
          subtitle={t("subtitle")}
          title={t("title")}
        />
      }
      testID="return-screen"
    >
      <View style={styles.workspace}>
        {/* 左栏：查单 → 选商品。 */}
        <View style={styles.mainColumn}>
          <PosKeyboardAwareScrollView
            style={styles.editorScroll}
            testID="return-editor-keyboard-scroll"
          >
            {state.mode === "receipt" ? (
              <View style={styles.lookupRow}>
                <LabeledInput
                  editable={!state.busy}
                  fill
                  hideLabel
                  icon="barcode-scan"
                  label={t("search.orderLabel")}
                  onChangeText={setOrderQuery}
                  onSubmitEditing={submitReceiptQuery}
                  placeholder={t("search.orderPlaceholder")}
                  testID="return-order-query"
                  value={orderQuery}
                />
                <PosButton
                  disabled={state.busy}
                  icon="magnify"
                  label={t("action.search")}
                  onPress={submitReceiptQuery}
                  style={styles.lookupButton}
                  testID="return-order-search"
                />
              </View>
            ) : (
              <View style={styles.noReceiptPanel}>
                <View style={styles.lookupRow}>
                  <LabeledInput
                    editable={!state.busy}
                    fill
                    icon="barcode-scan"
                    label={t("search.productLabel")}
                    onChangeText={setProductQuery}
                    onSubmitEditing={submitProductQuery}
                    placeholder={t("search.productPlaceholder")}
                    testID="return-product-query"
                    value={productQuery}
                  />
                  <PosButton
                    disabled={state.busy}
                    icon="plus"
                    label={t("action.addProduct")}
                    onPress={submitProductQuery}
                    style={styles.lookupButton}
                    testID="return-product-add"
                  />
                </View>
                <View style={styles.lookupRow}>
                  <LabeledInput
                    editable={!state.busy}
                    fill
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
                  <PosButton
                    disabled={state.busy}
                    icon="plus"
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
                    style={styles.lookupButton}
                    testID="return-open-item-add"
                  />
                </View>
              </View>
            )}
          </PosKeyboardAwareScrollView>

          {state.orderSummary ? (
            <View style={styles.orderBanner} testID="return-order-summary">
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.green}
                name="check-decagram-outline"
                size={24}
              />
              <Text numberOfLines={2} style={styles.orderTitle}>
                {`${t("order.summary", { order: state.orderSummary })}${formatOrderMeta(
                    state.orderSoldAtIso,
                    state.orderCashierName,
                  )}`}
              </Text>
              <PosPill
                label={t(
                  state.loadedFrom === "remote"
                    ? "order.remote"
                    : "order.local",
                )}
                style={styles.orderSourcePill}
                tone={state.loadedFrom === "remote" ? "good" : "info"}
              />
            </View>
          ) : null}
          {state.returnRecordsMayBeStale ? (
            <Notice
              icon="alert-outline"
              testID="return-stale-warning"
              text={t("order.stale")}
              tone="warning"
            />
          ) : null}
          {state.errorCode ? (
            <Notice
              icon="alert-circle-outline"
              testID="return-error"
              text={errorText(t, state.errorCode, state.requiredVoucherRefundCents)}
              tone="danger"
            />
          ) : null}

          <View style={styles.sectionHeader}>
            <Text style={styles.sectionLabel}>{t("lines.title")}</Text>
            {state.phase === "loading" ? (
              <ActivityIndicator
                color={posColors.accent}
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
              <View style={styles.emptyLines}>
                <MaterialCommunityIcons
                  accessible={false}
                  color={posColors.mutedInk}
                  name="receipt-text-outline"
                  size={44}
                />
                <Text style={styles.emptyText}>{t("lines.empty")}</Text>
              </View>
            )}
          </ScrollView>
        </View>

        {/* 右栏：怎么退。顺序与操作顺序一致：可退容量 → 退款计划 → 确认。 */}
        <View style={styles.sideColumn}>
          <View style={styles.card}>
            <Text style={styles.sectionLabel}>{t("capacity.title")}</Text>
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
          </View>

          <View style={[styles.card, styles.planCard]}>
            {/* 退款方式较多或带代金券提示时内容会超出，卡片内部滚动，确认键始终固定在下方。 */}
            <ScrollView
              contentContainerStyle={styles.planContent}
              testID="return-plan-scroll"
            >
              <Text style={styles.sectionLabel}>{t("summary.title")}</Text>
              <View style={styles.totalRow}>
                <Text style={styles.totalLabel}>{t("summary.selected")}</Text>
                <Text
                  adjustsFontSizeToFit
                  numberOfLines={1}
                  style={styles.total}
                  testID="return-selected-total"
                >
                  {formatAud(state.selectedTotalCents, locale)}
                </Text>
              </View>
              <View style={styles.divider} />
              <Text style={styles.fieldLabel}>{t("summary.method")}</Text>
              <View accessibilityRole="radiogroup" style={styles.methodList}>
                {methods.map((method) => (
                  <MethodTile
                    active={state.preferredMethod === method}
                    badge={
                      originalMethods.has(method)
                        ? t("method.originalBadge")
                        : null
                    }
                    key={method}
                    label={t(`method.${method}`)}
                    method={method}
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
              {/* 代金券买的部分只能退代金券；计划阶段同口径强制，这里只做提示。 */}
              {state.requiredVoucherRefundCents > 0 ? (
                <Notice
                  icon="wallet-giftcard"
                  testID="return-voucher-funded-notice"
                  text={t("voucherFunded.notice", {
                    amount: formatAud(state.requiredVoucherRefundCents, locale),
                  })}
                  tone="warning"
                />
              ) : null}
            </ScrollView>
          </View>

          {/* 本页唯一的主按钮，固定在右下角，带上本次退款金额。 */}
          <PosButton
            disabled={!state.canConfirm || state.busy}
            icon="cash-refund"
            label={
              state.selectedTotalCents > 0
                ? t("action.confirmAmount", {
                    amount: formatAud(state.selectedTotalCents, locale),
                  })
                : t("action.confirm")
            }
            onPress={() => void presenter.confirm()}
            size="primary"
            sound="danger"
            testID="return-confirm"
            tone="primary"
          />
        </View>
      </View>
    </PosScreenFrame>
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
  usePosThemeVersion();
  const selected = line.selectedQuantity > 0;
  const amountText = formatAud(line.amountCents, locale);
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
      </View>
      <View style={styles.stepperColumn}>
        <View style={styles.stepper}>
          <StepButton
            accessibilityLabel={t("action.decrease")}
            disabled={line.selectedQuantity === 0}
            icon="minus"
            onPress={onDecrease}
            testID={`return-decrease-${line.id}`}
          />
          <Text style={styles.stepperValue}>{line.selectedQuantity}</Text>
          <StepButton
            accessibilityLabel={t("action.increase")}
            disabled={
              line.sourceKind === "receipt" &&
              line.selectedQuantity >= line.availableQuantity
            }
            icon="plus"
            onPress={onIncrease}
            testID={`return-increase-${line.id}`}
          />
        </View>
        <Text style={styles.quantityCaption} testID={`return-quantity-${line.id}`}>
          {line.sourceKind === "receipt"
            ? t("line.quantity", {
                selected: line.selectedQuantity,
                available: line.availableQuantity,
              })
            : t("line.quantityNoReceipt", {
                selected: line.selectedQuantity,
              })}
        </Text>
      </View>
      <View style={styles.lineAmountColumn}>
        <Text
          accessibilityLabel={t("line.refund", { amount: amountText })}
          adjustsFontSizeToFit
          numberOfLines={1}
          style={[styles.lineAmount, !selected && styles.lineAmountIdle]}
        >
          {amountText}
        </Text>
        <Text style={styles.lineAmountLabel}>{t("line.refundLabel")}</Text>
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
  pageSubtitle,
  pageTitle,
  secondary,
  testID,
  title,
  tone,
  resolution,
}: Readonly<{
  actionLabel?: string;
  busy?: boolean;
  /** 退款结果未知时的主管结案面板；面板含输入框，必须由本组件的键盘感知滚动容器承载。 */
  resolution?: Readonly<{
    busy: boolean;
    onResolve(
      finding: "not-refunded" | "keep-waiting",
      evidenceReference: string,
      note: string,
    ): void;
    t: (key: ReturnCopyKey) => string;
    waitingRecorded: boolean;
  }> | undefined;
  error?: string | null;
  hint: string;
  onAction?(): void;
  pageSubtitle: string;
  pageTitle: string;
  secondary?: string;
  testID: string;
  title: string;
  tone: "waiting" | "warning" | "success" | "danger";
}>) {
  usePosThemeVersion();
  const body = (
    <>
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
          <MaterialCommunityIcons
            accessible={false}
            color={
              tone === "success"
                ? posColors.green
                : tone === "danger"
                  ? posColors.red
                  : posColors.yellowInk
            }
            name={
              tone === "success"
                ? "check-bold"
                : tone === "danger"
                  ? "exclamation-thick"
                  : "help"
            }
            size={52}
          />
        )}
      </View>
      <Text style={styles.statusTitle}>{title}</Text>
      <Text style={styles.statusHint}>{hint}</Text>
      {secondary ? <Text style={styles.statusSecondary}>{secondary}</Text> : null}
      {error ? <Text style={styles.statusError}>{error}</Text> : null}
      {actionLabel && onAction ? (
        <PosButton
          disabled={busy}
          label={actionLabel}
          onPress={onAction}
          size="large"
          style={styles.statusAction}
          testID={`${testID}-action`}
          tone="primary"
        />
      ) : null}
    </>
  );
  return (
    // 退款中、结果未知、完成、未完成都是整页替换：只能按页内按钮继续，侧栏跳转一律锁住。
    <PosScreenFrame
      active="returns"
      header={<PosPageHeader subtitle={pageSubtitle} title={pageTitle} />}
      navigationLocked
      testID={testID}
    >
      {resolution ? (
        <PosKeyboardAwareScrollView
          contentContainerStyle={styles.statusScrollContent}
          style={styles.statusScroll}
          testID={`${testID}-scroll`}
        >
          {body}
          <SupervisorResolutionPanel
            busy={resolution.busy}
            onResolve={resolution.onResolve}
            t={resolution.t}
            waitingRecorded={resolution.waitingRecorded}
          />
        </PosKeyboardAwareScrollView>
      ) : (
        <View style={styles.statusBody}>{body}</View>
      )}
    </PosScreenFrame>
  );
}

function SupervisorResolutionPanel({
  busy,
  onResolve,
  t,
  waitingRecorded,
}: Readonly<{
  busy: boolean;
  onResolve(
    finding: "not-refunded" | "keep-waiting",
    evidenceReference: string,
    note: string,
  ): void;
  t: (key: ReturnCopyKey) => string;
  waitingRecorded: boolean;
}>) {
  usePosThemeVersion();
  const [evidence, setEvidence] = useState("");
  const [note, setNote] = useState("");
  const ready = !busy && evidence.trim().length > 0 && note.trim().length > 0;
  return (
    <View style={styles.resolutionCard} testID="return-resolution">
      <Text style={styles.resolutionTitle}>{t("resolution.title")}</Text>
      <Text style={styles.resolutionHint}>{t("resolution.hint")}</Text>
      <LabeledInput
        editable={!busy}
        label={t("resolution.evidenceLabel")}
        onChangeText={setEvidence}
        placeholder={t("resolution.evidencePlaceholder")}
        testID="return-resolution-evidence"
        value={evidence}
      />
      <LabeledInput
        editable={!busy}
        label={t("resolution.noteLabel")}
        onChangeText={setNote}
        placeholder={t("resolution.notePlaceholder")}
        testID="return-resolution-note"
        value={note}
      />
      {waitingRecorded ? (
        <Text style={styles.resolutionNotice} testID="return-resolution-waiting-recorded">
          {t("resolution.waitingRecorded")}
        </Text>
      ) : null}
      <View style={styles.resolutionActions}>
        <PosButton
          disabled={!ready}
          fill
          label={t("resolution.notRefunded")}
          onPress={() => onResolve("not-refunded", evidence, note)}
          sound="danger"
          testID="return-resolution-not-refunded"
          tone="danger"
        />
        <PosButton
          disabled={!ready}
          fill
          label={t("resolution.keepWaiting")}
          onPress={() => onResolve("keep-waiting", evidence, note)}
          testID="return-resolution-keep-waiting"
        />
      </View>
    </View>
  );
}

function LabeledInput({
  editable,
  fill = false,
  hideLabel = false,
  icon,
  keyboardType,
  label,
  onChangeText,
  onSubmitEditing,
  placeholder,
  testID,
  value,
}: Readonly<{
  editable: boolean;
  /** 在横排里占满剩余宽度。 */
  fill?: boolean;
  /** 不显示上方标签（占位文字已说明用途），标签仍作为无障碍名称。 */
  hideLabel?: boolean;
  icon?: PosIconName;
  keyboardType?: "default" | "decimal-pad";
  label: string;
  onChangeText(value: string): void;
  onSubmitEditing?(): void;
  placeholder: string;
  testID: string;
  value: string;
}>) {
  usePosThemeVersion();
  return (
    <View style={[styles.inputGroup, fill && styles.inputGroupFill]}>
      {hideLabel ? null : <Text style={styles.inputLabel}>{label}</Text>}
      <View style={[styles.inputBox, !editable && styles.inputBoxDisabled]}>
        {icon ? (
          <MaterialCommunityIcons
            accessible={false}
            color={editable ? posColors.ink : posColors.disabledInk}
            name={icon}
            size={24}
          />
        ) : null}
        <PosKeyboardAwareTextInput
          accessibilityLabel={label}
          autoCapitalize="none"
          autoCorrect={false}
          editable={editable}
          keyboardType={keyboardType}
          onChangeText={onChangeText}
          onSubmitEditing={onSubmitEditing}
          placeholder={placeholder}
          placeholderTextColor={posColors.placeholder}
          returnKeyType="done"
          style={styles.input}
          testID={testID}
          value={value}
        />
      </View>
    </View>
  );
}

/** 黄 / 红提示条：图标 + 一段文字，testID 落在文字上。 */
function Notice({
  icon,
  testID,
  text,
  tone,
}: Readonly<{
  icon: PosIconName;
  testID: string;
  text: string;
  tone: "warning" | "danger";
}>) {
  usePosThemeVersion();
  const danger = tone === "danger";
  return (
    <View
      style={[styles.notice, danger ? styles.noticeDanger : styles.noticeWarning]}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={danger ? posColors.red : posColors.yellowInk}
        name={icon}
        size={22}
      />
      <Text
        style={[
          styles.noticeText,
          danger ? styles.noticeTextDanger : styles.noticeTextWarning,
        ]}
        testID={testID}
      >
        {text}
      </Text>
    </View>
  );
}

/** 优先退款方式：68 高的大块单选，整块都是点击区。 */
function MethodTile({
  active,
  badge,
  label,
  method,
  onPress,
  testID,
}: Readonly<{
  active: boolean;
  badge: string | null;
  label: string;
  method: ReturnTenderMethod;
  onPress(): void;
  testID: string;
}>) {
  usePosThemeVersion();
  const visual = RETURN_METHOD_ICON[method];
  return (
    <PosPressable
      accessibilityLabel={badge ? `${label} · ${badge}` : label}
      accessibilityRole="radio"
      accessibilityState={{ checked: active }}
      onPress={onPress}
      style={({ pressed }) => [
        styles.methodTile,
        active && styles.methodTileActive,
        pressed && styles.pressed,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={posColors[visual.color]}
        name={visual.icon}
        size={28}
      />
      <Text numberOfLines={1} style={styles.methodTileText}>
        {label}
      </Text>
      {badge ? (
        <PosPill label={badge} style={styles.methodTileBadge} tone="accent" />
      ) : null}
    </PosPressable>
  );
}

/** 商品行的 −/＋：52×52。 */
function StepButton({
  accessibilityLabel,
  disabled,
  icon,
  onPress,
  testID,
}: Readonly<{
  accessibilityLabel: string;
  disabled: boolean;
  icon: "minus" | "plus";
  onPress(): void;
  testID: string;
}>) {
  usePosThemeVersion();
  return (
    <PosPressable
      accessibilityLabel={accessibilityLabel}
      accessibilityRole="button"
      accessibilityState={{ disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [
        styles.stepButton,
        disabled && styles.stepButtonDisabled,
        pressed && !disabled && styles.pressed,
      ]}
      testID={testID}
    >
      <MaterialCommunityIcons
        accessible={false}
        color={disabled ? posColors.disabledInk : posColors.ink}
        name={icon}
        size={28}
      />
    </PosPressable>
  );
}

function errorText(
  t: (key: ReturnCopyKey) => string,
  code: `RETURN_${string}`,
  requiredVoucherRefundCents: number,
): string {
  // 本次须退代金券时，联网与额度不足两类错误都源于代金券占比约束，给出可操作的说明。
  if (requiredVoucherRefundCents > 0) {
    if (code === "RETURN_ONLINE_REQUIRED") return t("voucherFunded.onlineRequired");
    if (code === "RETURN_CAPACITY_EXCEEDED") return t("voucherFunded.capacityExceeded");
  }
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

const styles = createPosStyles((colors) => ({
  modeSwitch: {
    width: 340,
  },
  workspace: {
    flex: 1,
    flexDirection: "row",
    gap: 12,
    minHeight: 0,
    padding: 12,
  },
  mainColumn: {
    flex: 1,
    gap: 10,
    minWidth: 0,
  },
  sideColumn: {
    flexShrink: 0,
    gap: 10,
    width: 380,
  },
  editorScroll: {
    flexGrow: 0,
  },
  lookupRow: {
    flexDirection: "row",
    alignItems: "flex-end",
    gap: 10,
  },
  lookupButton: {
    height: 60,
    minHeight: 60,
    minWidth: 150,
  },
  noReceiptPanel: {
    gap: 10,
  },
  amountInput: {
    width: 190,
  },
  inputGroup: {
    gap: 5,
  },
  inputGroupFill: {
    flex: 1,
    minWidth: 0,
  },
  inputLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  inputBox: {
    height: 60,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 14,
    borderColor: colors.borderStrong,
    borderWidth: 1.5,
    borderRadius: 10,
    backgroundColor: colors.surface,
  },
  inputBoxDisabled: {
    borderColor: colors.disabledBorder,
    backgroundColor: colors.disabledSurface,
  },
  input: {
    flex: 1,
    alignSelf: "stretch",
    minWidth: 0,
    padding: 0,
    color: colors.ink,
    fontSize: 17,
    fontWeight: "700",
  },
  orderBanner: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderColor: colors.greenBorder,
    borderWidth: 1.5,
    borderRadius: 10,
    backgroundColor: colors.greenSoft,
  },
  orderTitle: {
    flex: 1,
    color: colors.green,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
  },
  orderSourcePill: {
    alignSelf: "center",
    backgroundColor: colors.surface,
  },
  notice: {
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderWidth: 1.5,
    borderRadius: 10,
  },
  noticeWarning: {
    borderColor: colors.yellowBorder,
    backgroundColor: colors.yellowSoft,
  },
  noticeDanger: {
    borderColor: colors.redBorder,
    backgroundColor: colors.redSoft,
  },
  noticeText: {
    flex: 1,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 20,
  },
  noticeTextWarning: {
    color: colors.yellowInk,
  },
  noticeTextDanger: {
    color: colors.red,
  },
  sectionHeader: {
    minHeight: 24,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  sectionLabel: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "900",
    letterSpacing: 1,
  },
  lineScroll: {
    flex: 1,
  },
  lineList: {
    gap: 6,
    paddingBottom: 4,
  },
  lineRow: {
    minHeight: 92,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderColor: colors.border,
    borderWidth: 1.5,
    borderRadius: 10,
    backgroundColor: colors.surface,
  },
  // 已选中要退的行：主操作色粗描边 + 浅底；内边距各减 1 抵消加粗的描边，避免行内容跳动。
  lineRowSelected: {
    paddingHorizontal: 13,
    paddingVertical: 7,
    borderColor: colors.accent,
    borderWidth: 2.5,
    backgroundColor: colors.selectedSurface,
  },
  lineIdentity: {
    flex: 1,
    minWidth: 0,
  },
  lineName: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  lineMeta: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
    marginTop: 3,
  },
  stepperColumn: {
    alignItems: "center",
    flexShrink: 0,
    gap: 4,
  },
  stepper: {
    flexDirection: "row",
    alignItems: "center",
  },
  stepperValue: {
    width: 52,
    textAlign: "center",
    color: colors.ink,
    fontSize: 22,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  stepButton: {
    width: 52,
    height: 52,
    alignItems: "center",
    justifyContent: "center",
    borderColor: colors.borderStrong,
    borderWidth: 1.5,
    borderRadius: 10,
    backgroundColor: colors.surface,
  },
  stepButtonDisabled: {
    borderColor: colors.disabledBorder,
    backgroundColor: colors.disabledSurface,
  },
  quantityCaption: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
    textAlign: "center",
  },
  lineAmountColumn: {
    width: 96,
    alignItems: "flex-end",
    flexShrink: 0,
  },
  lineAmount: {
    color: colors.ink,
    fontSize: 18,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    textAlign: "right",
  },
  lineAmountIdle: {
    color: colors.disabledInk,
  },
  lineAmountLabel: {
    color: colors.mutedInk,
    fontSize: 12,
    fontWeight: "700",
    marginTop: 2,
  },
  emptyLines: {
    alignItems: "center",
    gap: 10,
    padding: 28,
    borderColor: colors.border,
    borderWidth: 1.5,
    borderStyle: "dashed",
    borderRadius: 10,
  },
  emptyText: {
    textAlign: "center",
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 22,
  },
  card: {
    gap: 6,
    padding: 14,
    borderColor: colors.border,
    borderWidth: 1,
    borderRadius: 12,
    backgroundColor: colors.surface,
  },
  planCard: {
    flex: 1,
    minHeight: 0,
    padding: 0,
  },
  planContent: {
    gap: 8,
    padding: 14,
  },
  capacityHint: {
    color: colors.mutedInk,
    fontSize: 14,
    fontWeight: "600",
    lineHeight: 20,
  },
  capacityRow: {
    color: colors.ink,
    fontSize: 17,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
    lineHeight: 30,
  },
  totalRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
  },
  totalLabel: {
    color: colors.mutedInk,
    fontSize: 16,
    fontWeight: "800",
  },
  total: {
    flex: 1,
    color: colors.ink,
    fontSize: 40,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    letterSpacing: -1,
    lineHeight: 46,
    textAlign: "right",
  },
  divider: {
    height: 1,
    backgroundColor: colors.border,
  },
  fieldLabel: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "800",
  },
  methodList: {
    gap: 8,
  },
  methodTile: {
    height: 68,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 14,
    borderColor: colors.border,
    borderWidth: 1.5,
    borderRadius: 10,
    backgroundColor: colors.surface,
  },
  methodTileActive: {
    paddingHorizontal: 13,
    borderColor: colors.accent,
    borderWidth: 2.5,
    backgroundColor: colors.selectedSurface,
  },
  methodTileText: {
    flex: 1,
    color: colors.ink,
    fontSize: 17,
    fontWeight: "900",
  },
  methodTileBadge: {
    alignSelf: "center",
  },
  rule: {
    color: colors.mutedInk,
    fontSize: 12.5,
    fontWeight: "600",
    lineHeight: 18,
  },
  pressed: {
    opacity: 0.72,
  },
  statusBody: {
    flex: 1,
    padding: 32,
    alignItems: "center",
    justifyContent: "center",
  },
  statusMark: {
    width: 96,
    height: 96,
    alignItems: "center",
    justifyContent: "center",
    borderRadius: 48,
    marginBottom: 20,
  },
  statusSuccess: {
    backgroundColor: colors.greenSoft,
  },
  statusDanger: {
    backgroundColor: colors.redSoft,
  },
  statusWarning: {
    backgroundColor: colors.yellowSoft,
  },
  statusWaiting: {
    backgroundColor: colors.blueSoft,
  },
  statusTitle: {
    color: colors.ink,
    fontSize: 30,
    fontWeight: "900",
    textAlign: "center",
  },
  statusHint: {
    maxWidth: 640,
    marginTop: 10,
    color: colors.mutedInk,
    fontSize: 17,
    fontWeight: "600",
    lineHeight: 25,
    textAlign: "center",
  },
  statusSecondary: {
    marginTop: 12,
    color: colors.green,
    fontSize: 30,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
  },
  statusAction: {
    marginTop: 22,
    minWidth: 320,
  },
  statusScroll: {
    flex: 1,
    alignSelf: "stretch",
  },
  statusScrollContent: {
    alignItems: "center",
    padding: 24,
  },
  resolutionCard: {
    alignSelf: "center",
    width: "100%",
    maxWidth: 640,
    marginTop: 20,
    padding: 16,
    gap: 12,
    borderColor: colors.border,
    borderWidth: 1,
    borderRadius: 12,
    backgroundColor: colors.surface,
  },
  resolutionTitle: {
    color: colors.ink,
    fontSize: 19,
    fontWeight: "900",
  },
  resolutionHint: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 22,
  },
  resolutionNotice: {
    padding: 12,
    color: colors.green,
    backgroundColor: colors.greenSoft,
    borderRadius: 10,
    fontSize: 15,
    fontWeight: "700",
  },
  resolutionActions: {
    flexDirection: "row",
    gap: 10,
  },
  statusError: {
    maxWidth: 640,
    marginTop: 16,
    padding: 12,
    color: colors.red,
    backgroundColor: colors.redSoft,
    borderRadius: 10,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 22,
    textAlign: "center",
  },
}));

/** 摘要后缀“  时间  收银员”（对齐 WPF #701：订单 #单号  时间  收银员）；缺失的部分省略。 */
function formatOrderMeta(
  soldAtIso: string | null | undefined,
  cashierName: string | null | undefined,
): string {
  const parts: string[] = [];
  const sold = soldAtIso ? new Date(soldAtIso) : null;
  if (sold && Number.isFinite(sold.getTime())) {
    const pad = (value: number) => String(value).padStart(2, "0");
    parts.push(
      `${sold.getFullYear()}-${pad(sold.getMonth() + 1)}-${pad(sold.getDate())} ${pad(sold.getHours())}:${pad(sold.getMinutes())}`,
    );
  }
  const cashier = cashierName?.trim();
  if (cashier) parts.push(cashier);
  return parts.length > 0 ? `  ${parts.join("  ")}` : "";
}
