import { useState } from "react";
import { ActivityIndicator, Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { PageJumpSheet } from "./PageJumpSheet";
import { clampPage, getPageRange } from "./pagination-logic";

export interface PaginationBarProps {
  /** 当前页（1 起）；越界时按 [1, pageCount] 夹取显示 */
  page: number;
  pageCount: number;
  /** 总条数（用于「共 N 条 · 显示 a–b」），与 pageSize 一起决定显示区间 */
  total: number;
  pageSize: number;
  pageSizeOptions: readonly number[];
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  /** 外部禁用：上一页 / 下一页 / 跳页 / 每页条数全部不可点 */
  disabled?: boolean;
  /** 加载中：同样禁用全部操作，并在页码选择器上显示转圈 */
  loading?: boolean;
  /** 只显示第一行（上一页 / 页码选择 / 下一页），用于列表底部的次级分页条 */
  compact?: boolean;
  /** 跳页面板里「每页条数」下方的说明文案 */
  pageSizeHint?: string;
  /** 测试标识前缀，默认 "pagination"：{id}-prev / -next / -jump / -size-{n} / -summary */
  testID?: string;
}

/**
 * 通用分页条，放在列表 / 表格上方：
 * 第一行 [‹] [当前页 / 总页数 ⇅] [›]，第二行「共 N 条 · 显示 a–b」+「每页」分段选择。
 * 点页码选择器打开 PageJumpSheet（组件内部自带，调用方不用管面板状态）。
 */
export function PaginationBar({ page, pageCount, total, pageSize, pageSizeOptions, onPageChange, onPageSizeChange, disabled = false, loading = false, compact = false, pageSizeHint, testID = "pagination" }: PaginationBarProps) {
  const { t } = useAppTranslation("common");
  const [jumpOpen, setJumpOpen] = useState(false);
  const current = clampPage(page, pageCount);
  const locked = disabled || loading;
  const range = getPageRange(current, pageSize, total);
  const totalText = total.toLocaleString("en-AU");

  return <View style={styles.bar}>
    <View style={styles.pageRow}>
      <PagerButton testID={`${testID}-prev`} icon="chevron-left" label={t("pagination.previous")} disabled={locked || current <= 1} onPress={() => onPageChange(current - 1)} />
      <Pressable
        testID={`${testID}-jump`}
        accessibilityRole="button"
        accessibilityLabel={t("pagination.pageIndicatorLabel", { page: current, pageCount })}
        accessibilityState={{ disabled: locked, busy: loading }}
        disabled={locked}
        hitSlop={4}
        onPress={() => setJumpOpen(true)}
        style={[styles.selector, locked && styles.selectorDisabled]}
      >
        <Text style={styles.selectorText}>{t("pagination.pageIndicator", { page: current, pageCount })}</Text>
        {loading
          ? <ActivityIndicator size="small" color={HB_COLORS.textSecondary} />
          : <MaterialCommunityIcons name="unfold-more-horizontal" size={16} color={HB_COLORS.textSecondary} />}
      </Pressable>
      <PagerButton testID={`${testID}-next`} icon="chevron-right" label={t("pagination.next")} disabled={locked || current >= pageCount} onPress={() => onPageChange(current + 1)} />
    </View>

    {compact ? null : <View style={styles.infoRow}>
      <Text testID={`${testID}-summary`} style={styles.summary} numberOfLines={1}>
        {total > 0 ? t("pagination.summary", { total: totalText, from: range.from.toLocaleString("en-AU"), to: range.to.toLocaleString("en-AU") }) : t("pagination.summaryEmpty")}
      </Text>
      <View style={styles.sizeGroup}>
        <Text style={styles.sizeLabel}>{t("pagination.perPage")}</Text>
        <View style={styles.segment} accessibilityRole="radiogroup">
          {pageSizeOptions.map((option, index) => {
            const selected = option === pageSize;
            return <Pressable
              key={option}
              testID={`${testID}-size-${option}`}
              accessibilityRole="radio"
              accessibilityLabel={t("pagination.perPageOption", { count: option })}
              accessibilityState={{ checked: selected, disabled: locked }}
              disabled={locked}
              hitSlop={{ top: 4, bottom: 4 }}
              // 点已选中的选项不触发回调，避免页面无谓地回到第 1 页
              onPress={() => { if (!selected) onPageSizeChange(option); }}
              style={[styles.segmentButton, index > 0 && styles.segmentDivider, selected && styles.segmentSelected]}
            >
              <Text style={[styles.segmentText, selected && styles.segmentTextSelected]}>{option}</Text>
            </Pressable>;
          })}
        </View>
      </View>
    </View>}

    <PageJumpSheet
      testID={`${testID}-sheet`}
      visible={jumpOpen}
      page={current}
      pageCount={pageCount}
      total={total}
      pageSize={pageSize}
      pageSizeOptions={pageSizeOptions}
      pageSizeHint={pageSizeHint}
      onJump={(target) => { setJumpOpen(false); onPageChange(clampPage(target, pageCount)); }}
      onPageSizeChange={(value) => { setJumpOpen(false); if (value !== pageSize) onPageSizeChange(value); }}
      onDismiss={() => setJumpOpen(false)}
    />
  </View>;
}

function PagerButton({ icon, label, disabled, onPress, testID }: { icon: "chevron-left" | "chevron-right"; label: string; disabled: boolean; onPress: () => void; testID: string }) {
  return <Pressable testID={testID} accessibilityRole="button" accessibilityLabel={label} accessibilityState={{ disabled }} disabled={disabled} hitSlop={4} onPress={onPress} style={[styles.pagerButton, disabled && styles.pagerButtonDisabled]}>
    <MaterialCommunityIcons name={icon} size={22} color={disabled ? HB_COLORS.outline : HB_COLORS.action} />
  </Pressable>;
}

const CONTROL_SIZE = 36;

const styles = StyleSheet.create({
  bar: { backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, padding: HB_SPACING.xs, gap: HB_SPACING.xs },
  pageRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  pagerButton: { width: CONTROL_SIZE, height: CONTROL_SIZE, borderRadius: HB_RADIUS.control, borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white, alignItems: "center", justifyContent: "center" },
  pagerButtonDisabled: { borderColor: HB_COLORS.outlineMuted, backgroundColor: HB_COLORS.surfaceMuted },
  selector: { flex: 1, minWidth: 0, height: CONTROL_SIZE, borderRadius: HB_RADIUS.control, backgroundColor: HB_COLORS.surfaceMuted, flexDirection: "row", alignItems: "center", justifyContent: "center", gap: 6 },
  selectorDisabled: { opacity: 0.6 },
  selectorText: { color: HB_COLORS.textPrimary, fontSize: 15, fontWeight: "700", fontVariant: ["tabular-nums"] },
  // 第二行窄屏放不下时折行：说明文字占满一行，分段选择落到下一行靠右
  infoRow: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", justifyContent: "space-between", columnGap: HB_SPACING.xs, rowGap: HB_SPACING.xs },
  summary: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18, flexShrink: 1, fontVariant: ["tabular-nums"] },
  sizeGroup: { flexDirection: "row", alignItems: "center", gap: 6, marginLeft: "auto" },
  sizeLabel: { color: HB_COLORS.textSecondary, fontSize: 12 },
  segment: { flexDirection: "row", borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: HB_RADIUS.control, overflow: "hidden" },
  segmentButton: { minWidth: 40, height: CONTROL_SIZE - 2, paddingHorizontal: 6, alignItems: "center", justifyContent: "center", backgroundColor: HB_COLORS.white },
  segmentDivider: { borderLeftWidth: 1, borderLeftColor: HB_COLORS.outline },
  segmentSelected: { backgroundColor: HB_COLORS.action },
  segmentText: { color: HB_COLORS.textPrimary, fontSize: 13, fontWeight: "600", fontVariant: ["tabular-nums"] },
  segmentTextSelected: { color: HB_COLORS.white },
});
