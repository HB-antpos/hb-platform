import { useEffect, useState, type ReactNode } from "react";
import { Image, Pressable, StyleSheet, View } from "react-native";
import { Button, Switch, Text, TextInput } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { OptionChip } from "./container-detail-chip";
import { getDetailRemark, isSetChildDetail } from "./container-detail-table-columns";
import {
  CONTAINER_DETAIL_MATCH_TYPE_OPTIONS,
  CONTAINER_DETAIL_PRODUCT_TYPE_OPTIONS,
  CONTAINER_DETAIL_RANGE_FILTER_PAIRS,
  CONTAINER_DETAIL_SEARCH_FIELDS,
  CONTAINER_DETAIL_SORT_OPTIONS,
  CONTAINER_DETAIL_WAREHOUSE_STATUS_OPTIONS,
  createEmptyContainerDetailFilters,
  findInvalidContainerDetailRangePairs,
  getDetailBarcode,
  getDetailDomesticProductCode,
  getDetailEnglishName,
  getDetailImageUrl,
  getDetailItemNumber,
  getDetailLocalProductCode,
  getDetailMatchType,
  getDetailProductName,
  getDetailReadonlyOemPrice,
  getDetailRealtimeRetailPrice,
  hasDetailProductCodeConflict,
  toggleContainerDetailFilterOption,
} from "./query";
import type {
  ContainerDetail,
  ContainerDetailFilterState,
  ContainerDetailRangeFilterKey,
  ContainerDetailSearchField,
  ContainerDetailSort,
} from "./types";

function formatPrice(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "--" : value.toFixed(2);
}

function SheetRow({ label, selected, trailing, onPress }: { label: string; selected?: boolean; trailing?: ReactNode; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityState={{ selected: Boolean(selected) }} onPress={onPress} style={[styles.optionRow, selected && styles.optionRowSelected]}>
      <Text style={[styles.optionLabel, selected && styles.optionLabelSelected]}>{label}</Text>
      {trailing}
    </Pressable>
  );
}

// ---------------------------------------------------------------------------
// 搜索字段 / 排序
// ---------------------------------------------------------------------------

export function ContainerDetailSearchFieldSheet({
  visible,
  value,
  onSelect,
  onDismiss,
}: {
  visible: boolean;
  value: ContainerDetailSearchField;
  onSelect: (field: ContainerDetailSearchField) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("containerDetail");
  return (
    <BusinessSheet visible={visible} title={t("search.fieldTitle")} onDismiss={onDismiss}>
      {CONTAINER_DETAIL_SEARCH_FIELDS.map((item) => (
        <SheetRow
          key={item.field}
          label={t(`searchField.${item.field}`)}
          selected={item.field === value}
          trailing={item.field === value ? <MaterialCommunityIcons name="check" size={20} color={HB_COLORS.action} /> : null}
          onPress={() => onSelect(item.field)}
        />
      ))}
    </BusinessSheet>
  );
}

export function ContainerDetailSortSheet({
  visible,
  sort,
  onSelect,
  onDismiss,
}: {
  visible: boolean;
  sort: ContainerDetailSort;
  /** 点选某字段：当前字段翻转方向，其他字段使用默认方向（由调用方用 toggleContainerDetailSort 计算） */
  onSelect: (field: ContainerDetailSort["field"]) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("containerDetail");
  const direction = t(sort.order === "ascend" ? "sort.ascend" : "sort.descend");
  return (
    <BusinessSheet
      visible={visible}
      title={t("sort.title")}
      subtitle={t("sort.current", { label: t(`sort.options.${sort.field}`), direction })}
      onDismiss={onDismiss}
    >
      <Text style={styles.hint}>{t("sort.hint")}</Text>
      {CONTAINER_DETAIL_SORT_OPTIONS.map((option) => {
        const current = option.field === sort.field;
        return (
          <SheetRow
            key={option.field}
            label={t(`sort.options.${option.field}`)}
            selected={current}
            onPress={() => onSelect(option.field)}
            trailing={current ? (
              <View style={styles.sortTrailing}>
                <Text style={styles.sortDirection}>{direction}</Text>
                <MaterialCommunityIcons name={sort.order === "ascend" ? "arrow-up" : "arrow-down"} size={18} color={HB_COLORS.action} />
              </View>
            ) : null}
          />
        );
      })}
    </BusinessSheet>
  );
}

// ---------------------------------------------------------------------------
// 筛选面板
// ---------------------------------------------------------------------------

export function ContainerDetailFilterSheet({
  visible,
  applied,
  showReadonlyOemPrice,
  onShowReadonlyOemPriceChange,
  onApply,
  onDismiss,
}: {
  visible: boolean;
  applied: ContainerDetailFilterState;
  showReadonlyOemPrice: boolean;
  onShowReadonlyOemPriceChange: (value: boolean) => void;
  onApply: (filters: ContainerDetailFilterState) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("containerDetail");
  const [draft, setDraft] = useState<ContainerDetailFilterState>(applied);
  const [attempted, setAttempted] = useState(false);

  // 每次打开都从当前已生效的筛选重新起草，取消不会残留半成品
  useEffect(() => {
    if (visible) {
      setDraft(applied);
      setAttempted(false);
    }
    // 仅在打开时同步；打开期间 applied 不会变化
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible]);

  const invalidPairs = findInvalidContainerDetailRangePairs(draft);
  const setRange = (key: ContainerDetailRangeFilterKey, value: string) =>
    setDraft((current) => ({ ...current, ranges: { ...current.ranges, [key]: value } }));

  const apply = () => {
    setAttempted(true);
    // 非法区间（非数字或下限大于上限）不允许应用，避免静默丢弃条件
    if (invalidPairs.length) return;
    onApply(draft);
  };

  return (
    <BusinessSheet
      visible={visible}
      title={t("filter.title")}
      onDismiss={onDismiss}
      footer={(
        <View style={styles.footerRow}>
          <Button mode="outlined" style={[BUSINESS_UI.button, styles.footerButton]} contentStyle={BUSINESS_UI.buttonContent} onPress={() => { setDraft(createEmptyContainerDetailFilters()); setAttempted(false); }}>
            {t("actions.reset")}
          </Button>
          <Button mode="contained" style={[BUSINESS_UI.button, styles.footerButtonWide]} contentStyle={BUSINESS_UI.buttonContent} onPress={apply}>
            {t("actions.apply")}
          </Button>
        </View>
      )}
    >
      {CONTAINER_DETAIL_RANGE_FILTER_PAIRS.map((pair) => {
        const invalid = attempted && invalidPairs.includes(pair.key);
        return (
          <View key={pair.key} style={styles.rangeBlock}>
            <Text style={styles.sectionLabel}>{t(`filter.ranges.${pair.key}`)}</Text>
            <View style={styles.rangeRow}>
              <TextInput
                mode="outlined"
                dense
                label={t("filter.min")}
                keyboardType="decimal-pad"
                value={draft.ranges[pair.minKey]}
                error={invalid}
                onChangeText={(value) => setRange(pair.minKey, value)}
                style={styles.rangeInput}
              />
              <Text style={styles.rangeDash}>–</Text>
              <TextInput
                mode="outlined"
                dense
                label={t("filter.max")}
                keyboardType="decimal-pad"
                value={draft.ranges[pair.maxKey]}
                error={invalid}
                onChangeText={(value) => setRange(pair.maxKey, value)}
                style={styles.rangeInput}
              />
            </View>
            {invalid ? <Text style={styles.errorText}>{t("filter.invalidRange")}</Text> : null}
          </View>
        );
      })}

      <Text style={styles.sectionLabel}>{t("filter.productTypes")}</Text>
      <View style={styles.chipWrap}>
        {CONTAINER_DETAIL_PRODUCT_TYPE_OPTIONS.map((option) => (
          <OptionChip
            key={option.value}
            role="checkbox"
            label={t(`filter.productTypeOptions.${option.value}`)}
            selected={draft.productTypes.includes(option.value)}
            onPress={() => setDraft((current) => ({ ...current, productTypes: toggleContainerDetailFilterOption(current.productTypes, option.value) }))}
          />
        ))}
      </View>

      <Text style={styles.sectionLabel}>{t("filter.warehouseStatus")}</Text>
      <View style={styles.chipWrap}>
        {CONTAINER_DETAIL_WAREHOUSE_STATUS_OPTIONS.map((option) => (
          <OptionChip
            key={option.value}
            role="checkbox"
            label={t(`filter.warehouseStatusOptions.${option.value}`)}
            selected={draft.warehouseStatus.includes(option.value)}
            onPress={() => setDraft((current) => ({ ...current, warehouseStatus: toggleContainerDetailFilterOption(current.warehouseStatus, option.value) }))}
          />
        ))}
      </View>

      <Text style={styles.sectionLabel}>{t("filter.matchTypes")}</Text>
      <View style={styles.chipWrap}>
        {CONTAINER_DETAIL_MATCH_TYPE_OPTIONS.map((option) => (
          <OptionChip
            key={option.value}
            role="checkbox"
            label={t(`info.match.${option.value}`)}
            selected={draft.matchTypes.includes(option.value)}
            onPress={() => setDraft((current) => ({ ...current, matchTypes: toggleContainerDetailFilterOption(current.matchTypes, option.value) }))}
          />
        ))}
      </View>

      {/* 显示选项不属于筛选条件，立即生效，不计入角标 */}
      <View style={styles.switchRow}>
        <View style={styles.switchText}>
          <Text style={styles.optionLabel}>{t("filter.showReadonlyOemPrice")}</Text>
          <Text style={styles.hint}>{t("filter.showReadonlyOemPriceHint")}</Text>
        </View>
        <Switch value={showReadonlyOemPrice} onValueChange={onShowReadonlyOemPriceChange} />
      </View>
    </BusinessSheet>
  );
}

// ---------------------------------------------------------------------------
// 批量操作菜单
// ---------------------------------------------------------------------------

export type ContainerDetailBulkActionKey =
  | "float"
  | "prices"
  | "recalculate"
  | "backfill"
  | "pushHq"
  | "submitContainer"
  | "delete"
  | "exportExcel"
  | "exportPdf";

export function ContainerDetailBulkActionsSheet({
  visible,
  selectedCount,
  filteredTotal,
  canEditContainer,
  canDeleteContainer,
  canRunProductJobs,
  busy,
  onAction,
  onDismiss,
}: {
  visible: boolean;
  selectedCount: number;
  filteredTotal: number;
  canEditContainer: boolean;
  canDeleteContainer: boolean;
  canRunProductJobs: boolean;
  busy: boolean;
  onAction: (action: ContainerDetailBulkActionKey) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("containerDetail");
  const noSelection = selectedCount === 0;
  const item = (action: ContainerDetailBulkActionKey, icon: string, options: { destructive?: boolean; needsSelection?: boolean; hint?: string } = {}) => {
    const disabled = busy || (options.needsSelection === true && noSelection);
    return (
      <Pressable
        key={action}
        accessibilityRole="button"
        accessibilityState={{ disabled }}
        disabled={disabled}
        onPress={() => onAction(action)}
        style={[styles.actionRow, disabled && styles.actionRowDisabled]}
      >
        <MaterialCommunityIcons name={icon as never} size={22} color={options.destructive ? HB_COLORS.danger : HB_COLORS.action} />
        <View style={styles.actionText}>
          <Text style={[styles.optionLabel, options.destructive && styles.dangerText]}>{t(`bulk.actions.${action}`)}</Text>
          {options.hint ? <Text style={styles.hint}>{options.hint}</Text> : null}
        </View>
      </Pressable>
    );
  };

  return (
    <BusinessSheet
      visible={visible}
      title={t("bulk.title")}
      subtitle={noSelection ? t("bulk.scopeFiltered", { count: filteredTotal }) : t("bulk.scopeSelected", { count: selectedCount })}
      onDismiss={onDismiss}
    >
      {canEditContainer ? (
        <>
          <Text style={styles.sectionLabel}>{t("bulk.groups.edit")}</Text>
          {item("float", "percent-outline")}
          {item("prices", "currency-usd")}
          {item("recalculate", "calculator-variant-outline")}
          {item("backfill", "history")}
        </>
      ) : null}
      {canRunProductJobs ? (
        <>
          <Text style={styles.sectionLabel}>{t("bulk.groups.hq")}</Text>
          {item("pushHq", "cloud-upload-outline", { needsSelection: true, hint: noSelection ? t("bulk.needsSelection") : undefined })}
          <Text style={styles.sectionLabel}>{t("bulk.groups.container")}</Text>
          {item("submitContainer", "check-decagram-outline", { hint: t("bulk.submitContainerHint") })}
        </>
      ) : null}
      <Text style={styles.sectionLabel}>{t("bulk.groups.export")}</Text>
      {item("exportExcel", "file-excel-outline")}
      {item("exportPdf", "file-pdf-box")}
      {canDeleteContainer ? (
        <>
          <Text style={styles.sectionLabel}>{t("bulk.groups.danger")}</Text>
          {item("delete", "trash-can-outline", { destructive: true, needsSelection: true, hint: noSelection ? t("bulk.needsSelection") : undefined })}
        </>
      ) : null}
    </BusinessSheet>
  );
}

// ---------------------------------------------------------------------------
// 行信息块：编辑弹窗顶部 / 只读查看共用
// ---------------------------------------------------------------------------

export function ContainerDetailInfoBlock({
  detail,
  showReadonlyOemPrice,
  canAlignDomesticProductCode,
  aligning,
  alignDisabled,
  onAlign,
}: {
  detail: ContainerDetail;
  showReadonlyOemPrice: boolean;
  /** 已含权限判定：canEditContainer && (管理员 || Products.Edit) */
  canAlignDomesticProductCode: boolean;
  aligning: boolean;
  alignDisabled: boolean;
  onAlign: () => void;
}) {
  const { t } = useAppTranslation("containerDetail");
  const imageUrl = getDetailImageUrl(detail);
  const [imageFailed, setImageFailed] = useState(false);
  const showImage = Boolean(imageUrl && !imageFailed);
  const localProductCode = getDetailLocalProductCode(detail);
  const domesticProductCode = getDetailDomesticProductCode(detail);
  const hasConflict = hasDetailProductCodeConflict(detail);
  const matchType = getDetailMatchType(detail);
  const remark = getDetailRemark(detail);
  // 对齐编码的条件与旧版卡片一致：有权限 + 存在编码冲突 + 两侧编码齐全 + 非套装子商品
  const canAlign = canAlignDomesticProductCode && hasConflict && Boolean(localProductCode && domesticProductCode) && !isSetChildDetail(detail);

  return (
    <View style={styles.infoBlock}>
      <View style={styles.infoTop}>
        <View style={styles.imageFrame}>
          {showImage ? (
            <Image source={{ uri: imageUrl! }} style={styles.image} resizeMode="contain" onError={() => setImageFailed(true)} />
          ) : (
            <Text style={styles.imagePlaceholder}>{t("info.noImage")}</Text>
          )}
        </View>
        <View style={styles.infoText}>
          <Text style={styles.infoItemNumber} numberOfLines={1}>{getDetailItemNumber(detail) || "--"}</Text>
          <Text style={styles.infoName} numberOfLines={2}>{getDetailProductName(detail) || getDetailEnglishName(detail) || "--"}</Text>
          <Text style={styles.infoMeta} numberOfLines={1}>{t("info.barcode")} {getDetailBarcode(detail) || "--"}</Text>
        </View>
      </View>
      <View style={styles.infoGrid}>
        <View style={styles.infoCell}>
          <Text style={styles.infoLabel}>{t("info.realtimeRetailPrice")}</Text>
          <Text style={styles.infoValue}>{formatPrice(getDetailRealtimeRetailPrice(detail))}</Text>
        </View>
        {showReadonlyOemPrice ? (
          <View style={styles.infoCell}>
            <Text style={styles.infoLabel}>{t("info.readonlyOemPrice")}</Text>
            <Text style={styles.infoValue}>{formatPrice(getDetailReadonlyOemPrice(detail))}</Text>
          </View>
        ) : null}
        <View style={styles.infoCell}>
          <Text style={styles.infoLabel}>{t("info.matchType")}</Text>
          <Text style={[styles.infoValue, hasConflict && styles.warningText]}>{t(`info.match.${matchType}`)}</Text>
        </View>
      </View>
      {hasConflict ? (
        <Text style={styles.warningText}>
          {t("info.conflictCandidate", { local: localProductCode || "--", domestic: domesticProductCode || "--" })}
        </Text>
      ) : null}
      {remark ? <Text style={styles.infoMeta}>{t("info.remarkLine", { value: remark })}</Text> : null}
      {canAlign ? (
        <Button
          mode="outlined"
          icon="link-variant"
          loading={aligning}
          disabled={alignDisabled}
          onPress={onAlign}
          style={BUSINESS_UI.button}
          contentStyle={BUSINESS_UI.buttonContent}
        >
          {t("info.align")}
        </Button>
      ) : null}
    </View>
  );
}

/** 没有编辑权限时点击行：只读查看明细信息。 */
export function ContainerDetailViewSheet({
  detail,
  showReadonlyOemPrice,
  onDismiss,
}: {
  detail: ContainerDetail | null;
  showReadonlyOemPrice: boolean;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("containerDetail");
  return (
    <BusinessSheet
      visible={Boolean(detail)}
      title={t("info.viewTitle")}
      onDismiss={onDismiss}
      footer={(
        <Button mode="outlined" style={BUSINESS_UI.button} contentStyle={BUSINESS_UI.buttonContent} onPress={onDismiss}>
          {t("actions.close")}
        </Button>
      )}
    >
      {detail ? (
        // 只读查看没有对齐权限（对齐需要编辑权限），所以固定不展示对齐按钮
        <ContainerDetailInfoBlock
          detail={detail}
          showReadonlyOemPrice={showReadonlyOemPrice}
          canAlignDomesticProductCode={false}
          aligning={false}
          alignDisabled
          onAlign={() => undefined}
        />
      ) : null}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  hint: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  sectionLabel: { color: HB_COLORS.textSecondary, fontSize: 13, fontWeight: "700", marginTop: HB_SPACING.xxs },
  optionRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  optionRowSelected: { borderColor: HB_COLORS.action },
  optionLabel: { color: HB_COLORS.textPrimary, fontSize: 15 },
  optionLabelSelected: { color: HB_COLORS.action, fontWeight: "700" },
  sortTrailing: { flexDirection: "row", alignItems: "center", gap: 4 },
  sortDirection: { color: HB_COLORS.action, fontSize: 13, fontWeight: "700" },
  footerRow: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  footerButtonWide: { flex: 2 },
  rangeBlock: { gap: 4 },
  rangeRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  rangeInput: { flex: 1, backgroundColor: HB_COLORS.white },
  rangeDash: { color: HB_COLORS.textSecondary, fontSize: 16 },
  errorText: { color: HB_COLORS.danger, fontSize: 12 },
  chipWrap: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  switchRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm, marginTop: HB_SPACING.xs },
  switchText: { flex: 1, gap: 2 },
  actionRow: {
    minHeight: 52,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  actionRowDisabled: { opacity: 0.45 },
  actionText: { flex: 1, gap: 2 },
  dangerText: { color: HB_COLORS.danger },
  warningText: { color: HB_COLORS.warning, fontSize: 13 },
  infoBlock: { gap: HB_SPACING.xs, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.surface, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, backgroundColor: HB_COLORS.surfaceMuted },
  infoTop: { flexDirection: "row", gap: HB_SPACING.sm },
  imageFrame: { width: 64, height: 64, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, overflow: "hidden", alignItems: "center", justifyContent: "center" },
  image: { width: "100%", height: "100%" },
  imagePlaceholder: { color: HB_COLORS.textSecondary, fontSize: 11 },
  infoText: { flex: 1, minWidth: 0, gap: 2 },
  infoItemNumber: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "700" },
  infoName: { color: HB_COLORS.textPrimary, fontSize: 13, lineHeight: 18 },
  infoMeta: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  infoGrid: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.sm },
  infoCell: { minWidth: 96, gap: 2 },
  infoLabel: { color: HB_COLORS.textSecondary, fontSize: 11 },
  infoValue: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "600", fontVariant: ["tabular-nums"] },
});
