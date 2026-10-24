import { useEffect, useMemo, useState } from "react";
import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Button, Icon, Text, TextInput } from "react-native-paper";
import { InsightSheet } from "@/components/warehouse-product-insights/InsightSheet";
import { SalesOrderBranchPicker } from "./SalesOrderBranchPicker";
import { REFUND_TONES } from "./SalesOrderStatusTag";
import {
  SALES_ORDER_MAX_RANGE_DAYS,
  SALES_ORDER_RANGE_PRESETS,
  SALES_ORDER_REFUND_TIERS,
  SALES_ORDER_REFUND_TYPE,
  SALES_ORDER_TYPE_FILTERS,
  buildDefaultSalesOrderFilters,
  buildSalesOrderPresetRange,
  matchSalesOrderRangePreset,
  normalizeSelectedBranchCodes,
  resolveSalesOrderStatusKey,
  validateSalesOrderRange,
} from "@/modules/sales-orders/logic";
import type {
  SalesOrderBranch,
  SalesOrderFilters,
  SalesOrderRefundTier,
  SalesOrderScope,
  SalesOrderTypeFilter,
} from "@/modules/sales-orders/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export type SalesOrderFilterSection = "range" | "branches" | "orderType";

export interface SalesOrderFilterSheetProps {
  visible: boolean;
  filters: SalesOrderFilters;
  today: string;
  branches: SalesOrderBranch[];
  branchesLoading: boolean;
  branchesError: string | null;
  scope: SalesOrderScope | null;
  onRetryBranches: () => void;
  onClose: () => void;
  onApply: (filters: SalesOrderFilters) => void;
}

/** 筛选面板：区间（30 天上限 + 一键收敛）、分店多选、订单类型；排序放在列表条上单独切换。 */
export function SalesOrderFilterSheet({
  visible,
  filters,
  today,
  branches,
  branchesLoading,
  branchesError,
  scope,
  onRetryBranches,
  onClose,
  onApply,
}: SalesOrderFilterSheetProps) {
  const { t } = useAppTranslation("salesOrders");
  const [startDate, setStartDate] = useState(filters.range.startDate);
  const [endDate, setEndDate] = useState(filters.range.endDate);
  const [selectedBranches, setSelectedBranches] = useState<string[]>(filters.branchCodes);
  const [orderType, setOrderType] = useState<SalesOrderTypeFilter>(filters.orderType);
  const [refundTier, setRefundTier] = useState<SalesOrderRefundTier>(filters.refundTier);

  useEffect(() => {
    // 每次打开都从已生效的筛选重新起草，关闭未应用的改动不得残留。
    setStartDate(filters.range.startDate);
    setEndDate(filters.range.endDate);
    setSelectedBranches(filters.branchCodes);
    setOrderType(filters.orderType);
    setRefundTier(filters.refundTier);
  }, [filters, visible]);

  const draftRange = useMemo(() => ({ startDate, endDate }), [startDate, endDate]);
  const validation = useMemo(() => validateSalesOrderRange(draftRange), [draftRange]);
  const activePreset = validation.ok ? matchSalesOrderRangePreset(draftRange) : null;
  const overflow = validation.reason === "tooLong";
  const usage = Math.min(validation.dayCount / SALES_ORDER_MAX_RANGE_DAYS, 1);
  const availableCodes = useMemo(() => branches.map((branch) => branch.storeCode), [branches]);

  return (
    <InsightSheet
      visible={visible}
      title={t("filterSheet.title")}
      subtitle={t("filterSheet.limit", { days: SALES_ORDER_MAX_RANGE_DAYS })}
      closeLabel={t("actions.close")}
      onClose={onClose}
      heightRatio={0.74}
    >
      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        <Text style={styles.sectionTitle}>{t("filterSheet.range")}</Text>
        <View style={styles.chips}>
          {SALES_ORDER_RANGE_PRESETS.map((preset) => {
            const selected = activePreset === preset;
            return (
              <Pressable
                key={preset}
                accessibilityRole="button"
                onPress={() => {
                  // 预设以今天为结束日，避免用户改过结束日后预设算出未来区间。
                  const next = buildSalesOrderPresetRange(today, preset);
                  setStartDate(next.startDate);
                  setEndDate(next.endDate);
                }}
                style={[styles.chip, selected ? styles.chipSelected : null]}
              >
                <Text style={[styles.chipText, selected ? styles.chipTextSelected : null]}>
                  {preset === 1 ? t("filterSheet.today") : t("filterSheet.preset", { days: preset })}
                </Text>
              </Pressable>
            );
          })}
        </View>
        <View style={styles.inputs}>
          <TextInput
            mode="outlined"
            dense
            label={t("filterSheet.startDate")}
            value={startDate}
            onChangeText={setStartDate}
            placeholder="YYYY-MM-DD"
            keyboardType="numbers-and-punctuation"
            error={overflow || validation.reason === "order"}
            style={styles.input}
          />
          <TextInput
            mode="outlined"
            dense
            label={t("filterSheet.endDate")}
            value={endDate}
            onChangeText={setEndDate}
            placeholder="YYYY-MM-DD"
            keyboardType="numbers-and-punctuation"
            style={styles.input}
          />
        </View>
        <View>
          <View style={styles.usageRow}>
            <Text style={[styles.usageText, overflow ? styles.usageDanger : null]}>
              {overflow
                ? t("filterSheet.overflow", {
                    days: validation.dayCount,
                    excess: validation.dayCount - SALES_ORDER_MAX_RANGE_DAYS,
                  })
                : t("filterSheet.selectedDays", { days: validation.dayCount })}
            </Text>
            <Text style={styles.usageText}>
              {t("filterSheet.maxDays", { days: SALES_ORDER_MAX_RANGE_DAYS })}
            </Text>
          </View>
          <View style={styles.track}>
            <View
              style={[
                styles.trackFill,
                {
                  width: `${Math.round((overflow ? 1 : usage) * 100)}%`,
                  backgroundColor: overflow ? HB_COLORS.danger : HB_COLORS.brand,
                },
              ]}
            />
          </View>
        </View>
        {validation.reason === "format" ? (
          <Notice tone="danger" text={t("filterSheet.invalidFormat")} />
        ) : null}
        {validation.reason === "order" ? (
          <Notice tone="danger" text={t("filterSheet.invalidOrder")} />
        ) : null}
        {overflow && validation.clampedStartDate ? (
          <View style={styles.overflowRow}>
            <Notice
              tone="danger"
              text={t("filterSheet.overflowHint", { days: SALES_ORDER_MAX_RANGE_DAYS })}
            />
            <Button
              mode="outlined"
              compact
              icon="arrow-collapse-right"
              onPress={() => setStartDate(validation.clampedStartDate!)}
            >
              {t("filterSheet.clamp", { days: SALES_ORDER_MAX_RANGE_DAYS })}
            </Button>
          </View>
        ) : null}

        <View style={styles.sectionHeader}>
          <Text style={styles.sectionTitle}>{t("filterSheet.branches")}</Text>
          <Text style={styles.sectionHint}>
            {scope === "all-stores"
              ? t("filterSheet.branchScopeAll")
              : t("filterSheet.branchScopeAuthorized")}
          </Text>
        </View>
        {branchesError ? (
          <View style={styles.overflowRow}>
            <Notice tone="danger" text={branchesError} />
            <Button mode="outlined" compact onPress={onRetryBranches}>
              {t("actions.retry")}
            </Button>
          </View>
        ) : null}
        <SalesOrderBranchPicker
          branches={branches}
          loading={branchesLoading}
          selected={selectedBranches}
          onChange={setSelectedBranches}
        />

        <Text style={styles.sectionTitle}>{t("filterSheet.orderType")}</Text>
        <View style={styles.chips}>
          {SALES_ORDER_TYPE_FILTERS.map((type) => {
            const selected = orderType === type;
            const key = type === -1 ? "all" : resolveSalesOrderStatusKey(type);
            return (
              <Pressable
                key={type}
                accessibilityRole="button"
                onPress={() => {
                  setOrderType(type);
                  // 换类型时档位回到「全部」，别把上一次的档位带进别的类型。
                  if (type !== SALES_ORDER_REFUND_TYPE) setRefundTier("all");
                }}
                style={[styles.chip, selected ? styles.chipSelected : null]}
              >
                <Text style={[styles.chipText, selected ? styles.chipTextSelected : null]}>
                  {t(`status.${key}`)}
                </Text>
              </Pressable>
            );
          })}
        </View>

        {orderType === SALES_ORDER_REFUND_TYPE ? (
          <>
            <View style={styles.sectionHeader}>
              <Text style={styles.sectionTitle}>{t("filterSheet.refundTier")}</Text>
              <Text style={styles.sectionHint}>{t("filterSheet.refundHint")}</Text>
            </View>
            <View style={styles.chips}>
              {SALES_ORDER_REFUND_TIERS.map((tier) => {
                const selected = refundTier === tier;
                const tone = tier === "all" ? null : REFUND_TONES[tier];
                return (
                  <Pressable
                    key={tier}
                    accessibilityRole="button"
                    onPress={() => setRefundTier(tier)}
                    style={[
                      styles.chip,
                      // 档位 chip 与卡片上的标签同色，选中时用同一套橙 / 红底，选「全部」沿用品牌色。
                      tone ? { backgroundColor: tone.background } : null,
                      selected ? (tone ? { backgroundColor: tone.color } : styles.chipSelected) : null,
                    ]}
                  >
                    <Text
                      style={[
                        styles.chipText,
                        tone ? { color: tone.color, fontWeight: "600" } : null,
                        selected ? styles.chipTextSelected : null,
                      ]}
                    >
                      {t(`refund.${tier}`)}
                    </Text>
                  </Pressable>
                );
              })}
            </View>
          </>
        ) : null}

        <View style={styles.actions}>
          <Button
            mode="outlined"
            compact
            onPress={() => {
              const defaults = buildDefaultSalesOrderFilters(today);
              setStartDate(defaults.range.startDate);
              setEndDate(defaults.range.endDate);
              setSelectedBranches(defaults.branchCodes);
              setOrderType(defaults.orderType);
              setRefundTier(defaults.refundTier);
            }}
            style={styles.resetButton}
          >
            {t("filterSheet.reset")}
          </Button>
          <Button
            mode="contained"
            disabled={!validation.ok}
            onPress={() =>
              onApply({
                range: draftRange,
                branchCodes: normalizeSelectedBranchCodes(selectedBranches, availableCodes),
                orderType,
                refundTier: orderType === SALES_ORDER_REFUND_TYPE ? refundTier : "all",
                // 排序不在面板里改，沿用当前值。
                sortDirection: filters.sortDirection,
              })
            }
            style={styles.applyButton}
          >
            {t("filterSheet.apply")}
          </Button>
        </View>
      </ScrollView>
    </InsightSheet>
  );
}

function Notice({ tone, text }: { tone: "danger" | "muted"; text: string }) {
  const danger = tone === "danger";
  return (
    <View style={[styles.notice, danger ? styles.noticeDanger : null]}>
      <Icon
        source={danger ? "alert-circle-outline" : "information-outline"}
        size={16}
        color={danger ? HB_COLORS.danger : HB_COLORS.textSecondary}
      />
      <Text style={[styles.noticeText, danger ? styles.noticeTextDanger : null]}>{text}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm },
  sectionHeader: { flexDirection: "row", alignItems: "baseline", gap: 8, marginTop: HB_SPACING.xs },
  sectionTitle: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  sectionHint: { fontSize: 12, color: HB_COLORS.textSecondary, flexShrink: 1 },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  chip: {
    paddingVertical: 6,
    paddingHorizontal: 12,
    borderRadius: 16,
    backgroundColor: HB_COLORS.surfaceMuted,
    maxWidth: "100%",
  },
  chipSelected: { backgroundColor: HB_COLORS.brand },
  chipText: { fontSize: 12, color: HB_COLORS.textSecondary },
  chipTextSelected: { color: HB_COLORS.white, fontWeight: "600" },
  inputs: { flexDirection: "row", gap: HB_SPACING.xs },
  input: { flex: 1, minWidth: 0, backgroundColor: HB_COLORS.white },
  usageRow: { flexDirection: "row", justifyContent: "space-between" },
  usageText: { fontSize: 12, color: HB_COLORS.textSecondary },
  usageDanger: { color: HB_COLORS.danger, fontWeight: "700" },
  track: {
    height: 6,
    marginTop: 6,
    borderRadius: 3,
    backgroundColor: HB_COLORS.outlineMuted,
    overflow: "hidden",
  },
  trackFill: { height: "100%" },
  overflowRow: { gap: HB_SPACING.xs },
  notice: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  noticeDanger: { backgroundColor: "#FCEBEB" },
  noticeText: { flex: 1, fontSize: 12, color: HB_COLORS.textSecondary, lineHeight: 18 },
  noticeTextDanger: { color: HB_COLORS.danger },
  actions: { flexDirection: "row", gap: HB_SPACING.xs, alignItems: "center", marginTop: HB_SPACING.xs },
  resetButton: { flex: 1 },
  applyButton: { flex: 2 },
});
