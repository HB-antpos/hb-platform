import type { ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { OptionChip } from "./container-detail-chip";
import {
  formatContainerDetailAmount,
  formatContainerDetailNumber,
  formatContainerDetailQuantity,
  getContainerStatusKey,
  type ContainerHeaderInfo,
} from "./container-detail-table-columns";
import type {
  ContainerDetailOverview,
  ContainerDetailQueryTag,
  ContainerDetailSearchField,
  ContainerDetailSort,
  ContainerDetailTagStats,
} from "./types";

/** 标签条的展示顺序（与 Web 工具栏一致）。 */
export const CONTAINER_DETAIL_TAG_ORDER: readonly ContainerDetailQueryTag[] = [
  "all",
  "new",
  "existing",
  "noOemPrice",
  "abnormalImport",
  "active",
  "inactive",
];

interface ContainerDetailHeaderProps {
  loading: boolean;
  info: ContainerHeaderInfo;
  /** 兜底显示：货柜编号取不到时显示的 GUID */
  fallbackTitle: string;
  overview: ContainerDetailOverview;
  /** 其他人在线协作提示（页面层渲染，保持心跳逻辑与展示在同一处） */
  presence?: ReactNode;
  onBack: () => void;
}

/** 顶部：返回 + 柜号 + 状态 + 副标题 + 概览卡横条 + 协作提示。 */
export function ContainerDetailHeader({ loading, info, fallbackTitle, overview, presence, onBack }: ContainerDetailHeaderProps) {
  const { t } = useAppTranslation("containerDetail");
  const statusKey = getContainerStatusKey(info.status);
  const rowCount = overview.rowCount;
  const dash = t("common.none");
  const loadRate = overview.loadRatePercent;
  return (
    <View style={styles.header}>
      <View style={styles.titleRow}>
        <Pressable accessibilityRole="button" accessibilityLabel={t("actions.back")} hitSlop={4} onPress={onBack} style={styles.backButton}>
          <MaterialCommunityIcons name="chevron-left" size={28} color={HB_COLORS.action} />
        </Pressable>
        <Text numberOfLines={1} style={styles.title}>{info.containerNumber || fallbackTitle}</Text>
        {loading ? <ActivityIndicator size="small" color={HB_COLORS.action} /> : null}
        {info.status !== undefined ? (
          <View style={styles.statusTag}>
            <Text style={styles.statusTagText} numberOfLines={1}>
              {statusKey ? t(`status.${statusKey}`) : t("status.unknown", { status: info.status })}
            </Text>
          </View>
        ) : null}
      </View>
      <Text style={styles.subLine} numberOfLines={2}>
        {t("header.estimatedArrival")} {info.estimatedArrival || dash}
        {" · "}
        {t("header.actualArrival")} {info.actualArrival || dash}
        {" · "}
        {rowCount === undefined ? dash : t("header.rows", { count: rowCount })}
      </Text>
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.overviewStrip}>
        <View style={styles.card}>
          <Text style={styles.cardLabel}>{t("overview.amount")}</Text>
          <Text style={styles.cardValue} numberOfLines={1}>¥{formatContainerDetailAmount(overview.totalAmount)}</Text>
        </View>
        <View style={styles.card}>
          <Text style={styles.cardLabel}>{t("overview.volume")}</Text>
          <Text style={styles.cardValue} numberOfLines={1}>
            {overview.totalVolume === undefined ? dash : `${formatContainerDetailNumber(overview.totalVolume, 1)} m³`}
            {" / "}
            {loadRate === undefined ? dash : `${loadRate}%`}
          </Text>
          <View style={styles.progressTrack}>
            {/* 装载率超过 100% 时进度条封顶，数字照实显示 */}
            <View style={[styles.progressFill, { width: `${Math.min(100, Math.max(0, loadRate ?? 0))}%` }]} />
          </View>
        </View>
        <View style={styles.card}>
          <Text style={styles.cardLabel}>{t("overview.products")}</Text>
          <Text style={styles.cardValue} numberOfLines={1}>
            {overview.newCount === undefined ? dash : formatContainerDetailQuantity(overview.newCount)}
            {" / "}
            {overview.existingCount === undefined ? dash : formatContainerDetailQuantity(overview.existingCount)}
          </Text>
        </View>
      </ScrollView>
      {presence}
    </View>
  );
}

interface ContainerDetailQueryBarProps {
  keyword: string;
  searchField: ContainerDetailSearchField;
  sort: ContainerDetailSort;
  activeFilterCount: number;
  selectedTags: readonly ContainerDetailQueryTag[];
  tagStats: ContainerDetailTagStats | undefined;
  total: number;
  onKeywordChange: (value: string) => void;
  onSubmitSearch: () => void;
  onClearSearch: () => void;
  onOpenSearchField: () => void;
  onOpenSort: () => void;
  onOpenFilter: () => void;
  onToggleTag: (tag: ContainerDetailQueryTag) => void;
}

/** 查询区：左侧字段选择的搜索框 + 排序按钮 + 筛选按钮(角标) + 标签条。 */
export function ContainerDetailQueryBar({
  keyword,
  searchField,
  sort,
  activeFilterCount,
  selectedTags,
  tagStats,
  total,
  onKeywordChange,
  onSubmitSearch,
  onClearSearch,
  onOpenSearchField,
  onOpenSort,
  onOpenFilter,
  onToggleTag,
}: ContainerDetailQueryBarProps) {
  const { t } = useAppTranslation("containerDetail");
  const fieldLabel = t(`searchField.${searchField}`);
  const sortLabel = t(`sort.options.${sort.field}`);
  const sortDirection = t(sort.order === "ascend" ? "sort.ascend" : "sort.descend");
  return (
    <View style={styles.queryBar}>
      <View style={styles.searchRow}>
        <View style={styles.searchBox}>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("search.fieldLabel", { field: fieldLabel })}
            onPress={onOpenSearchField}
            style={styles.fieldSelector}
          >
            <Text style={styles.fieldSelectorText} numberOfLines={1}>{fieldLabel}</Text>
            <MaterialCommunityIcons name="menu-down" size={18} color={HB_COLORS.textSecondary} />
          </Pressable>
          <TextInput
            value={keyword}
            onChangeText={onKeywordChange}
            onSubmitEditing={onSubmitSearch}
            placeholder={t("search.placeholder", { field: fieldLabel })}
            placeholderTextColor={HB_COLORS.textSecondary}
            returnKeyType="search"
            autoCapitalize="none"
            autoCorrect={false}
            style={styles.searchInput}
          />
          {keyword ? (
            <Pressable accessibilityRole="button" accessibilityLabel={t("search.clear")} hitSlop={8} onPress={onClearSearch} style={styles.searchAction}>
              <MaterialCommunityIcons name="close-circle" size={18} color={HB_COLORS.textSecondary} />
            </Pressable>
          ) : null}
          <Pressable accessibilityRole="button" accessibilityLabel={t("search.submit")} onPress={onSubmitSearch} style={styles.searchAction}>
            <MaterialCommunityIcons name="magnify" size={22} color={HB_COLORS.action} />
          </Pressable>
        </View>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("sort.button", { label: sortLabel, direction: sortDirection })}
          onPress={onOpenSort}
          style={styles.iconButton}
        >
          <MaterialCommunityIcons name={sort.order === "ascend" ? "sort-ascending" : "sort-descending"} size={22} color={HB_COLORS.action} />
        </Pressable>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("filter.button", { count: activeFilterCount })}
          onPress={onOpenFilter}
          style={[styles.iconButton, activeFilterCount > 0 && styles.iconButtonActive]}
        >
          <MaterialCommunityIcons name="filter-variant" size={22} color={HB_COLORS.action} />
          {activeFilterCount > 0 ? (
            <View style={styles.badge}>
              <Text style={styles.badgeText}>{activeFilterCount}</Text>
            </View>
          ) : null}
        </Pressable>
      </View>
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipRow}>
        {CONTAINER_DETAIL_TAG_ORDER.map((tag) => {
          const selected = tag === "all" ? selectedTags.length === 0 : selectedTags.includes(tag);
          // 各标签计数来自当前搜索/筛选范围内的统计（不含标签自身），「全部」缺统计时退回当前结果总数
          const count = tagStats?.[tag] ?? (tag === "all" ? total : 0);
          return (
            <OptionChip
              key={tag}
              label={t("tags.chip", { label: t(`tags.${tag}`), count })}
              selected={selected}
              onPress={() => onToggleTag(tag)}
            />
          );
        })}
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  header: { gap: HB_SPACING.xs },
  titleRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 44 },
  backButton: { width: 44, height: 44, marginLeft: -HB_SPACING.xs, alignItems: "center", justifyContent: "center" },
  title: { flexShrink: 1, color: HB_COLORS.textPrimary, fontSize: 22, lineHeight: 30, fontWeight: "700" },
  statusTag: {
    paddingHorizontal: HB_SPACING.xs,
    height: 24,
    justifyContent: "center",
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.action,
  },
  statusTagText: { color: HB_COLORS.action, fontSize: 12, fontWeight: "700" },
  subLine: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18, fontVariant: ["tabular-nums"] },
  overviewStrip: { gap: HB_SPACING.xs, paddingVertical: HB_SPACING.xxs },
  card: {
    minWidth: 128,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    gap: 2,
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
  },
  cardLabel: { color: HB_COLORS.textSecondary, fontSize: 11, lineHeight: 16 },
  cardValue: { color: HB_COLORS.textPrimary, fontSize: 15, lineHeight: 22, fontWeight: "700", fontVariant: ["tabular-nums"] },
  progressTrack: { height: 4, borderRadius: 2, backgroundColor: HB_COLORS.surfaceMuted, overflow: "hidden" },
  progressFill: { height: 4, backgroundColor: HB_COLORS.brand },
  queryBar: { gap: HB_SPACING.xs },
  searchRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  searchBox: {
    flex: 1,
    minWidth: 0,
    height: 44,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    overflow: "hidden",
  },
  fieldSelector: {
    height: "100%",
    flexDirection: "row",
    alignItems: "center",
    paddingLeft: HB_SPACING.sm,
    paddingRight: HB_SPACING.xxs,
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRightWidth: 1,
    borderRightColor: HB_COLORS.outlineMuted,
  },
  fieldSelectorText: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "700", maxWidth: 64 },
  searchInput: { flex: 1, minWidth: 0, height: "100%", paddingVertical: 0, paddingHorizontal: HB_SPACING.xs, color: HB_COLORS.textPrimary, fontSize: 14 },
  searchAction: { width: 36, height: "100%", alignItems: "center", justifyContent: "center" },
  iconButton: {
    width: 44,
    height: 44,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
  },
  iconButtonActive: { borderColor: HB_COLORS.action },
  badge: {
    position: "absolute",
    top: -6,
    right: -6,
    minWidth: 18,
    height: 18,
    paddingHorizontal: 4,
    borderRadius: 9,
    backgroundColor: HB_COLORS.danger,
    alignItems: "center",
    justifyContent: "center",
  },
  badgeText: { color: HB_COLORS.white, fontSize: 11, fontWeight: "700", lineHeight: 14 },
  chipRow: { gap: HB_SPACING.xs, paddingVertical: HB_SPACING.xxs },
});
