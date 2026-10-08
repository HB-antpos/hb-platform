import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Text, TextInput } from "react-native-paper";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { isSeasonalCardCustomPriceOption } from "@/modules/seasonal-cards/form";
import {
  useSeasonalCardCatalog,
  useSeasonalCardOverview,
  useSeasonalCardSuppliers,
  useSubmitSeasonalCardBatch,
} from "@/modules/seasonal-cards/hooks";
import {
  formatSeasonalCardMoney,
  formatSeasonalCardShortDateTime,
} from "@/modules/seasonal-cards/format";
import { reconcileSeasonalCardRecentSuppliers } from "@/modules/seasonal-cards/recent-suppliers";
import {
  loadSeasonalCardRecentSuppliers,
  rememberSeasonalCardRecentSupplier,
} from "@/modules/seasonal-cards/recent-suppliers-storage";
import {
  buildSeasonalCardBatchPayload,
  buildSeasonalCardComboKey,
  buildSeasonalCardComparison,
  createSeasonalCardDraft,
  formatQuantityDiff,
  getSeasonalCardOptionsForType,
  getSeasonalCardPriceDisplayLabel,
  rebaseSeasonalCardDraft,
  resolveSeasonalCardSubmitState,
  sanitizePriceInput,
  sanitizeQuantityInput,
  stepQuantityInput,
  summarizeSeasonalCardDraft,
  type SeasonalCardDraft,
  type SeasonalCardDraftLine,
} from "@/modules/seasonal-cards/submit-draft";
import {
  getSeasonalCardErrorCode,
  getSeasonalCardSubmitErrorKey,
  SEASONAL_CARD_STALE,
} from "@/modules/seasonal-cards/submit-errors";
import type {
  SeasonalCardCatalogItem,
  SeasonalCardSupplierOption,
  SeasonalCardType,
} from "@/modules/seasonal-cards/types";
import { HolidayGrid, type HolidayFillStatus, type HolidayGridItem } from "./HolidayGrid";
import { OverwriteConfirmSheet, type OverwriteConfirmRow } from "./OverwriteConfirmSheet";
import { PriceQuantityRow } from "./PriceQuantityRow";
import { StatusBanner } from "./StatusBanner";
import { SubmitFooter } from "./SubmitFooter";
import { SupplierField } from "./SupplierField";
import { YearChips } from "./YearChips";

const CARD_TYPES: SeasonalCardType[] = [1, 2, 3, 4, 5];

interface SelectedSupplier extends SeasonalCardSupplierOption {
  /** 供应商是按分店记住的；分店切换后旧选择作废。 */
  storeCode: string;
}

/**
 * 填报视图：年份 + 节日 + 供应商定位一个组合，整组填写 4 个价格的剩余数量后一次提交。
 * 已填过的组合进入即预填上次数量；改过才能「覆盖提交」，并先弹对比确认。
 */
export function SeasonalCardSubmitView({
  storeCode,
  canSubmit,
  onNotify,
}: {
  storeCode: string;
  canSubmit: boolean;
  onNotify: (message: string) => void;
}) {
  const { t, language } = useAppTranslation(["seasonalCards", "common"]);
  const localeTag = useMemo(() => resolveLocaleTag(language), [language]);
  const currentYear = useMemo(() => new Date().getFullYear(), []);
  const years = useMemo(() => [currentYear - 1, currentYear, currentYear + 1], [currentYear]);
  const [seasonYear, setSeasonYear] = useState(currentYear);
  const [cardType, setCardType] = useState<SeasonalCardType>(1);
  const [supplier, setSupplier] = useState<SelectedSupplier | null>(null);
  const [recent, setRecent] = useState<{ storeCode: string; items: SeasonalCardSupplierOption[] }>({
    storeCode: "",
    items: [],
  });
  const [draft, setDraft] = useState<SeasonalCardDraft | null>(null);
  const [confirmVisible, setConfirmVisible] = useState(false);
  const [showValidation, setShowValidation] = useState(false);
  const [staleComboKey, setStaleComboKey] = useState<string | null>(null);
  // 提交进行中或刚被「抢先提交」拒绝时，基线变化只换对比基准，不覆盖用户已填的数量。
  const preserveEditsRef = useRef(false);

  const supplierCode = supplier && supplier.storeCode === storeCode ? supplier.supplierCode : "";
  const catalogQuery = useSeasonalCardCatalog(canSubmit);
  const suppliersQuery = useSeasonalCardSuppliers(canSubmit);
  const overviewQuery = useSeasonalCardOverview(
    { storeCode, seasonYear, localSupplierCode: supplierCode },
    canSubmit
  );
  const submitMutation = useSubmitSeasonalCardBatch();
  const isBusy = submitMutation.isPending;

  const activeSuppliers = suppliersQuery.data ?? null;
  const recentSuppliers = useMemo(
    () =>
      reconcileSeasonalCardRecentSuppliers(
        recent.storeCode === storeCode ? recent.items : [],
        activeSuppliers
      ),
    [activeSuppliers, recent, storeCode]
  );
  const selectedSupplierName = useMemo(() => {
    if (!supplierCode) {
      return "";
    }
    const lowered = supplierCode.toLowerCase();
    return (
      activeSuppliers?.find((item) => item.supplierCode.toLowerCase() === lowered)?.supplierName ||
      supplier?.supplierName ||
      overviewQuery.data?.supplierName ||
      supplierCode
    );
  }, [activeSuppliers, overviewQuery.data?.supplierName, supplier?.supplierName, supplierCode]);

  // 进入或切换分店：读取该分店「最近使用」，默认选中最近一次用的供应商。
  useEffect(() => {
    let cancelled = false;
    if (!storeCode) {
      return undefined;
    }
    void loadSeasonalCardRecentSuppliers(storeCode).then((items) => {
      if (cancelled) {
        return;
      }
      setRecent({ storeCode, items });
      setSupplier((current) =>
        current && current.storeCode === storeCode
          ? current
          : items[0]
            ? { ...items[0], storeCode }
            : null
      );
    });
    return () => {
      cancelled = true;
    };
  }, [storeCode]);

  // 已停用的供应商（不在启用列表里）不能提交，自动清掉，提示重新选择。
  useEffect(() => {
    if (!activeSuppliers || !supplierCode) {
      return;
    }
    const lowered = supplierCode.toLowerCase();
    if (!activeSuppliers.some((item) => item.supplierCode.toLowerCase() === lowered)) {
      setSupplier(null);
    }
  }, [activeSuppliers, supplierCode]);

  const options = useMemo<SeasonalCardCatalogItem[]>(
    () => getSeasonalCardOptionsForType(catalogQuery.data ?? [], cardType),
    [catalogQuery.data, cardType]
  );
  const comboKey = buildSeasonalCardComboKey({
    storeCode,
    seasonYear,
    cardType,
    localSupplierCode: supplierCode,
  });
  const overview = supplierCode ? overviewQuery.data ?? null : null;
  const currentBatch =
    overview?.holidays.find((holiday) => holiday.cardType === cardType)?.currentBatch ?? null;

  // 组合变化 → 用当前生效批次重新预填；同一组合的批次变化 → 换基线（必要时保留输入）。
  useEffect(() => {
    if (!overview || !options.length) {
      return;
    }
    setDraft((current) => {
      if (!current || current.comboKey !== comboKey) {
        preserveEditsRef.current = false;
        return createSeasonalCardDraft(comboKey, options, currentBatch);
      }
      return rebaseSeasonalCardDraft(current, currentBatch, options, preserveEditsRef.current);
    });
  }, [comboKey, currentBatch, options, overview]);

  useEffect(() => {
    setShowValidation(false);
    setConfirmVisible(false);
    setStaleComboKey(null);
  }, [comboKey]);

  const activeDraft = draft && overview && draft.comboKey === comboKey ? draft : null;
  const summary = useMemo(
    () => (activeDraft && options.length ? summarizeSeasonalCardDraft(activeDraft, options) : null),
    [activeDraft, options]
  );
  const baseline = activeDraft?.baseline ?? null;
  const submitState = resolveSeasonalCardSubmitState({
    ready: Boolean(storeCode && supplierCode && summary),
    hasBaseline: Boolean(baseline),
    changedCount: summary?.changedCount ?? 0,
  });
  const isStale = Boolean(baseline && staleComboKey === comboKey);

  const getCardTypeLabel = useCallback(
    (value: SeasonalCardType) => t(`cardTypes.${value}`),
    [t]
  );
  const otherLabel = t("form.customPriceOption");
  const getOptionLabel = useCallback(
    (option: SeasonalCardCatalogItem) =>
      isSeasonalCardCustomPriceOption(option)
        ? otherLabel
        : getSeasonalCardPriceDisplayLabel(
            option.priceOption,
            option.priceLabel,
            otherLabel,
            option.fixedUnitPrice
          ),
    [otherLabel]
  );
  const comboText = [String(seasonYear), getCardTypeLabel(cardType), selectedSupplierName]
    .filter(Boolean)
    .join(" · ");

  const holidayItems = useMemo<HolidayGridItem[]>(
    () =>
      CARD_TYPES.map((value) => {
        let status: HolidayFillStatus = "unknown";
        if (overview) {
          const holiday = overview.holidays.find((item) => item.cardType === value);
          status = holiday?.currentBatch ? "filled" : "pending";
        }
        return { cardType: value, label: getCardTypeLabel(value), status };
      }),
    [getCardTypeLabel, overview]
  );

  const updateDraft = (updater: (current: SeasonalCardDraft) => SeasonalCardDraft) => {
    setDraft((current) => (current && current.comboKey === comboKey ? updater(current) : current));
  };

  const refresh = async () => {
    await Promise.all([
      catalogQuery.refetch(),
      suppliersQuery.refetch(),
      supplierCode ? overviewQuery.refetch() : Promise.resolve(),
    ]);
  };

  const submit = async () => {
    if (!activeDraft || !summary) {
      return;
    }
    const snapshot = activeDraft;
    const isOverwrite = Boolean(snapshot.baseline);
    const submittedCombo = comboText;
    const supplierSnapshot = { supplierCode, supplierName: selectedSupplierName };
    const payload = buildSeasonalCardBatchPayload(
      { storeCode, seasonYear, cardType, localSupplierCode: supplierCode },
      snapshot,
      options
    );
    preserveEditsRef.current = true;
    try {
      const result = await submitMutation.mutateAsync(payload);
      setConfirmVisible(false);
      setStaleComboKey(null);
      setShowValidation(false);
      if (result) {
        // 直接以服务端返回的新批次为基线，按钮立即变成「未修改」，不必等总览重拉。
        setDraft((current) =>
          current && current.comboKey === snapshot.comboKey
            ? rebaseSeasonalCardDraft(current, result, options, true)
            : current
        );
      }
      preserveEditsRef.current = false;
      onNotify(
        isOverwrite
          ? t("messages.overwriteSuccess")
          : t("messages.submitSuccess", { combo: submittedCombo })
      );
      void rememberSeasonalCardRecentSupplier(storeCode, supplierSnapshot).then((items) =>
        setRecent({ storeCode, items })
      );
    } catch (error) {
      setConfirmVisible(false);
      if (getSeasonalCardErrorCode(error) === SEASONAL_CARD_STALE) {
        // 有人抢先提交：总览已在 onSettled 里重拉，保留输入，只把对比基准换成最新批次。
        setStaleComboKey(snapshot.comboKey);
        onNotify(t("messages.staleSnackbar"));
        return;
      }
      preserveEditsRef.current = false;
      const errorKey = getSeasonalCardSubmitErrorKey(error);
      onNotify(
        errorKey
          ? t(errorKey)
          : resolveLocalizedErrorMessage(error, {
              language,
              t,
              fallbackKey: "messages.submitFailed",
            })
      );
    }
  };

  const handlePrimaryPress = () => {
    if (!summary) {
      return;
    }
    if (summary.hasInvalidQuantity) {
      onNotify(t("errors.remainingQuantity"));
      return;
    }
    if (summary.customPriceRequired) {
      setShowValidation(true);
      onNotify(t("errors.customUnitPrice"));
      return;
    }
    if (submitState === "overwrite") {
      setConfirmVisible(true);
      return;
    }
    if (submitState === "submit") {
      void submit();
    }
  };

  const describeLine = (option: SeasonalCardCatalogItem, line: SeasonalCardDraftLine | null) => {
    if (!line) {
      return isSeasonalCardCustomPriceOption(option) ? t("rows.customPriceHint") : t("rows.fixedPrice");
    }
    if (baseline) {
      const previous = line.previousQuantity == null ? "—" : String(line.previousQuantity);
      if (line.priceChanged) {
        return t("rows.priceChanged", {
          previous: formatSeasonalCardMoney(line.previousUnitPrice),
          current: line.unitPrice == null ? "—" : formatSeasonalCardMoney(line.unitPrice),
        });
      }
      return line.changed
        ? t("rows.previousChanged", { previous, current: line.quantity ?? 0 })
        : t("rows.previousUnchanged", { previous });
    }
    const hasQuantity = (line.quantity ?? 0) > 0;
    if (line.isCustomPrice) {
      return hasQuantity && line.unitPrice != null
        ? t("rows.customPriceSubtotal", { amount: formatSeasonalCardMoney(line.subtotal) })
        : t("rows.customPriceHint");
    }
    return hasQuantity
      ? t("rows.fixedPriceSubtotal", { amount: formatSeasonalCardMoney(line.subtotal) })
      : t("rows.fixedPrice");
  };

  const renderBanner = () => {
    if (!storeCode) {
      return <StatusBanner tone="info" title={t("messages.selectStoreFirst")} />;
    }
    if (!supplierCode) {
      return (
        <StatusBanner
          tone="info"
          title={t("banner.noSupplierTitle")}
          body={t("banner.noSupplierBody")}
        />
      );
    }
    if (overviewQuery.isError && !overview) {
      return (
        <StatusBanner
          tone="danger"
          title={t("banner.loadFailedTitle")}
          body={resolveLocalizedErrorMessage(overviewQuery.error, {
            language,
            t,
            fallbackKey: "banner.loadFailedTitle",
          })}
          actionLabel={t("banner.retry")}
          onAction={() => void overviewQuery.refetch()}
        />
      );
    }
    if (!activeDraft) {
      return null;
    }
    if (baseline) {
      const params = {
        name: baseline.submittedByName || "--",
        time: formatSeasonalCardShortDateTime(baseline.submittedAt, localeTag),
      };
      return isStale ? (
        <StatusBanner tone="danger" title={t("banner.staleTitle", params)} body={t("banner.staleBody")} />
      ) : (
        <StatusBanner tone="warning" title={t("banner.filledTitle", params)} body={t("banner.filledBody")} />
      );
    }
    return (
      <StatusBanner
        tone="info"
        title={t("banner.pendingTitle", { year: seasonYear, holiday: getCardTypeLabel(cardType) })}
        body={t("banner.pendingBody")}
      />
    );
  };

  const renderPriceRows = () => {
    if (catalogQuery.isLoading || (supplierCode && overviewQuery.isLoading)) {
      return (
        <View style={styles.feedback}>
          <ActivityIndicator />
        </View>
      );
    }
    if (catalogQuery.isError) {
      return (
        <Text style={styles.feedbackText}>
          {resolveLocalizedErrorMessage(catalogQuery.error, {
            language,
            t,
            fallbackKey: "messages.catalogEmpty",
          })}
        </Text>
      );
    }
    if (!options.length) {
      return <Text style={styles.feedbackText}>{t("messages.catalogEmpty")}</Text>;
    }
    const rowsDisabled = !activeDraft || isBusy;
    return options.map((option, index) => {
      const line = summary?.lines[index] ?? null;
      const label = getOptionLabel(option);
      const isCustom = isSeasonalCardCustomPriceOption(option);
      return (
        <PriceQuantityRow
          key={option.catalogGuid}
          label={label}
          hint={describeLine(option, line)}
          hintTone={line?.changed ? "changed" : "muted"}
          changed={Boolean(line?.changed)}
          quantity={activeDraft?.quantities[option.catalogGuid] ?? ""}
          disabled={rowsDisabled}
          decreaseLabel={t("form.decrease")}
          increaseLabel={t("form.increase")}
          quantityA11yLabel={t("form.quantityA11y", { label })}
          onChangeQuantity={(text) =>
            updateDraft((current) => ({
              ...current,
              quantities: { ...current.quantities, [option.catalogGuid]: sanitizeQuantityInput(text) },
            }))
          }
          onStep={(delta) =>
            updateDraft((current) => ({
              ...current,
              quantities: {
                ...current.quantities,
                [option.catalogGuid]: stepQuantityInput(
                  current.quantities[option.catalogGuid] ?? "",
                  delta
                ),
              },
            }))
          }
          customPrice={
            isCustom
              ? {
                  label: t("form.customUnitPrice"),
                  value: activeDraft?.customUnitPrice ?? "",
                  error:
                    showValidation && summary?.customPriceRequired
                      ? t("errors.customUnitPrice")
                      : null,
                  onChange: (text) =>
                    updateDraft((current) => ({
                      ...current,
                      customUnitPrice: sanitizePriceInput(text),
                    })),
                }
              : undefined
          }
        />
      );
    });
  };

  const comparison = summary ? buildSeasonalCardComparison(summary) : null;
  const unchangedLabel = t("confirm.unchanged");
  const confirmRows: OverwriteConfirmRow[] = comparison
    ? comparison.rows.map((row, index) => {
        const option = options[index];
        return {
          key: row.catalogGuid,
          label: option ? getOptionLabel(option) : "",
          previous: String(row.previous),
          current: String(row.current),
          diff:
            row.diff === 0 && row.priceChanged
              ? t("confirm.priceChanged")
              : formatQuantityDiff(row.diff, unchangedLabel),
          changed: row.diff !== 0 || row.priceChanged,
        };
      })
    : [];

  // 底部固定栏只放供应商编码：供应商全名可能很长，换行后会把固定栏撑高、挤占填写区域；
  // 全名在上方供应商框、覆盖确认弹窗和提交提示里完整显示。
  const summaryLine = [
    String(seasonYear),
    getCardTypeLabel(cardType),
    // 编码里的连字符换成不换行连字符，避免「SUP-」与「A01」被拆到两行。
    supplierCode ? supplierCode.replace(/-/g, "\u2011") : t("form.selectSupplier"),
  ].join(" · ");
  const changedLabel =
    baseline && summary?.changedCount
      ? t("footer.changedCount", { count: summary.changedCount })
      : undefined;
  const buttonLabel =
    submitState === "overwrite"
      ? t("footer.overwrite")
      : submitState === "unchanged"
        ? t("footer.unchanged")
        : t("footer.submit");

  return (
    <View style={styles.container}>
      <ScrollView
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode="on-drag"
        automaticallyAdjustKeyboardInsets
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={Boolean(
              catalogQuery.isRefetching || (supplierCode && overviewQuery.isRefetching)
            )}
            onRefresh={() => {
              void refresh();
            }}
          />
        }
      >
        <View style={[BUSINESS_UI.section, styles.selectorCard]}>
          <YearChips
            years={years}
            currentYear={currentYear}
            selectedYear={seasonYear}
            disabled={isBusy}
            title={t("form.seasonYear")}
            hint={t("form.seasonYearHint")}
            thisYearLabel={t("form.thisYear")}
            onSelect={setSeasonYear}
          />
          <HolidayGrid
            title={t("form.cardType")}
            items={holidayItems}
            selected={cardType}
            disabled={isBusy}
            statusLabels={{
              filled: t("status.filled"),
              pending: t("status.pending"),
              unknown: t("status.unknown"),
            }}
            onSelect={setCardType}
          />
          <SupplierField
            title={t("form.supplier")}
            placeholder={t("form.selectSupplier")}
            pickerTitle={t("form.supplierPickerTitle")}
            searchPlaceholder={t("form.supplierSearchPlaceholder")}
            cancelLabel={t("common:actions.cancel")}
            recentLabel={t("form.recentSuppliers")}
            selectedCode={supplierCode}
            selectedName={selectedSupplierName}
            suppliers={activeSuppliers ?? []}
            recentSuppliers={recentSuppliers}
            loadErrorMessage={suppliersQuery.isError ? t("messages.supplierLoadFailed") : null}
            disabled={isBusy || !storeCode}
            onSelect={(option) => setSupplier({ ...option, storeCode })}
          />
        </View>

        {renderBanner()}

        <View style={BUSINESS_UI.section}>
          <View style={styles.priceHeader}>
            <Text style={styles.priceTitle}>{t("form.priceSectionTitle")}</Text>
            <Text style={styles.priceHint}>{t("form.priceSectionHint")}</Text>
          </View>
          {renderPriceRows()}
        </View>

        <View style={[BUSINESS_UI.section, styles.remarkCard]}>
          <TextInput
            mode="outlined"
            dense
            multiline
            label={t("form.remark")}
            placeholder={t("form.remarkPlaceholder")}
            value={activeDraft?.remark ?? ""}
            editable={Boolean(activeDraft) && !isBusy}
            maxLength={500}
            onChangeText={(text) => updateDraft((current) => ({ ...current, remark: text }))}
            style={styles.remarkInput}
          />
        </View>
      </ScrollView>

      <SubmitFooter
        summaryLine={summaryLine}
        changedLabel={changedLabel}
        totalLine={t("footer.total", {
          count: summary?.totalQuantity ?? 0,
          amount: formatSeasonalCardMoney(summary?.totalAmount ?? 0),
        })}
        state={submitState}
        buttonLabel={buttonLabel}
        busy={isBusy}
        onPress={handlePrimaryPress}
      />

      <OverwriteConfirmSheet
        visible={confirmVisible && Boolean(comparison && baseline)}
        busy={isBusy}
        title={t("confirm.title")}
        comboLine={comboText}
        lastLine={
          baseline
            ? t("confirm.last", {
                time: formatSeasonalCardShortDateTime(baseline.submittedAt, localeTag),
                name: baseline.submittedByName || "--",
              })
            : ""
        }
        headers={{
          priceType: t("confirm.priceType"),
          previous: t("confirm.previous"),
          current: t("confirm.current"),
          diff: t("confirm.diff"),
        }}
        rows={confirmRows}
        total={{
          label: t("confirm.total"),
          previous: String(comparison?.previousTotal ?? 0),
          current: String(comparison?.currentTotal ?? 0),
          diff: formatQuantityDiff(comparison?.diffTotal ?? 0, unchangedLabel),
        }}
        note={t("confirm.note")}
        cancelLabel={t("confirm.back")}
        confirmLabel={t("confirm.confirm")}
        onCancel={() => setConfirmVisible(false)}
        onConfirm={() => void submit()}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1 },
  content: { ...BUSINESS_UI.content, paddingBottom: HB_SPACING.lg },
  selectorCard: { padding: HB_SPACING.md, gap: HB_SPACING.md },
  priceHeader: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: 14,
    paddingBottom: 10,
    flexDirection: "row",
    flexWrap: "wrap",
    alignItems: "baseline",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
  },
  priceTitle: { fontSize: 15, lineHeight: 22, fontWeight: "600", color: HB_COLORS.textPrimary, flexShrink: 1 },
  priceHint: { fontSize: 12, lineHeight: 16, color: HB_COLORS.textSecondary },
  feedback: { minHeight: 120, alignItems: "center", justifyContent: "center" },
  feedbackText: {
    paddingHorizontal: HB_SPACING.md,
    paddingBottom: HB_SPACING.md,
    fontSize: 13,
    lineHeight: 20,
    color: HB_COLORS.textSecondary,
  },
  remarkCard: { padding: HB_SPACING.sm },
  remarkInput: { backgroundColor: HB_COLORS.white },
});
