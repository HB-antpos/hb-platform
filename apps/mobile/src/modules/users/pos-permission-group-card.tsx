import { memo, useMemo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { PeopleTag } from "@/modules/people/ui";
import { PosPermissionCheckbox } from "@/modules/users/pos-permission-checkbox";
import { usePosPermissionTranslation } from "@/modules/users/pos-permission-i18n";
import { POS_PERMISSION_COLORS } from "@/modules/users/pos-permission-sheet";
import {
  getNextPosPermissionGroupChecked,
  getPosPermissionSelectionState,
  hasPosPermissionEntryGap,
  type PosPermissionDisplayGroup,
  type PosPermissionDisplayItem,
} from "@/modules/users/pos-terminal-permission-presentation";
import { formatPosGroupSummary } from "@/modules/users/pos-terminal-permissions-format";
import {
  buildPosDiscountGrid,
  buildPosGroupSummary,
  getPosPermissionItemLabel,
  groupHasEnabledHighRisk,
} from "@/modules/users/pos-terminal-permissions-view";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { clauseSeparator } from "@/shared/i18n/punctuation";

/** 「本次改动」蓝点：分组名后、矩阵格与逐项行里都用同一个。 */
export function PosPermissionChangeDot({ style }: { style?: object }) {
  return <View style={[styles.changeDot, style]} />;
}

interface PosPermissionGroupCardProps {
  group: PosPermissionDisplayGroup;
  groupLabel: string;
  /** 用于组头统计与三态：始终基于完整分组，不受筛选影响。 */
  fullGroup: PosPermissionDisplayGroup;
  selectedCodeSet: ReadonlySet<string>;
  changedCodeSet: ReadonlySet<string>;
  expanded: boolean;
  disabled: boolean;
  /** 卡片内第一组不画上分隔线。 */
  first?: boolean;
  onToggleExpanded: (group: PosPermissionDisplayGroup) => void;
  onToggleCode: (code: string) => void;
  onSetGroupChecked: (codes: string[], checked: boolean) => void;
}

/**
 * 收银权限的一个分组：一行（分组勾选框 + 名称 + 摘要 + 已开/总数 + 展开箭头），
 * 展开后折扣组是「单行 / 整单 × 档位」矩阵，其余组是逐项勾选。
 * 组内容器（白底圆角）由页面提供，这里只负责行与展开区。
 */
function PosPermissionGroupCardComponent({
  group,
  groupLabel,
  fullGroup,
  selectedCodeSet,
  changedCodeSet,
  expanded,
  disabled,
  first = false,
  onToggleExpanded,
  onToggleCode,
  onSetGroupChecked,
}: PosPermissionGroupCardProps) {
  const { t, translate, labels } = usePosPermissionTranslation();
  const fullCodes = useMemo(() => fullGroup.items.map((item) => item.code), [fullGroup]);
  const selectionState = getPosPermissionSelectionState(fullCodes, selectedCodeSet);
  const selectedCount = fullCodes.filter((code) => selectedCodeSet.has(code)).length;
  const entryGap = hasPosPermissionEntryGap(fullGroup, selectedCodeSet);
  const groupHasChanges = fullCodes.some((code) => changedCodeSet.has(code));
  const hasHighRisk = groupHasEnabledHighRisk(fullGroup, selectedCodeSet);
  const summary = useMemo(
    () => formatPosGroupSummary(buildPosGroupSummary(fullGroup, selectedCodeSet), translate),
    [fullGroup, selectedCodeSet, translate]
  );
  const grid = useMemo(
    () =>
      group.key === "discount" && expanded
        ? buildPosDiscountGrid(group.items, selectedCodeSet, changedCodeSet)
        : null,
    [changedCodeSet, expanded, group.items, group.key, selectedCodeSet]
  );

  const buildAccessibilityLabel = (item: PosPermissionDisplayItem, changed: boolean) =>
    [
      getPosPermissionItemLabel(item, labels),
      item.highRisk ? t("badges.highRisk") : null,
      changed ? t("badges.changed") : null,
    ]
      .filter(Boolean)
      .join(clauseSeparator());

  const renderItemRow = (item: PosPermissionDisplayItem) => {
    const checked = selectedCodeSet.has(item.code);
    const changed = changedCodeSet.has(item.code);
    // 入口没开时，组内其他项在 POS 上用不到：只做视觉提示，不改勾选。
    const dimmed = entryGap && !item.isEntry;

    return (
      <View key={item.code}>
        <Pressable
          accessibilityRole="checkbox"
          accessibilityState={{ checked, disabled }}
          accessibilityLabel={buildAccessibilityLabel(item, changed)}
          accessibilityHint={item.description || undefined}
          disabled={disabled}
          onPress={() => onToggleCode(item.code)}
          style={({ pressed }) => [styles.itemRow, pressed && styles.pressed]}
        >
          <PosPermissionCheckbox
            state={checked ? "all" : "none"}
            tone={item.highRisk ? "danger" : "default"}
            disabled={disabled || dimmed}
          />
          <Text
            style={[
              styles.itemName,
              item.isEntry && styles.itemNameEntry,
              dimmed && styles.itemNameDimmed,
            ]}
            numberOfLines={2}
          >
            {item.name}
          </Text>
          {item.highRisk ? <PeopleTag label={t("badges.highRisk")} tone="red" /> : null}
          <View style={styles.dotSlot}>{changed ? <PosPermissionChangeDot /> : null}</View>
        </Pressable>
        {item.isEntry && entryGap ? (
          <View style={styles.entryHint} accessibilityRole="alert">
            <Icon source="alert-outline" size={16} color={HB_COLORS.warning} />
            <Text style={styles.entryHintText}>{t("entryHint", { name: item.name })}</Text>
          </View>
        ) : null}
      </View>
    );
  };

  const renderDiscountMatrix = () => {
    if (!grid || grid.rows.length === 0) return null;
    return (
      <View style={styles.matrix}>
        <View style={styles.matrixRow}>
          <View style={styles.matrixLabelCol} />
          {grid.columns.map((column) => (
            <View key={column.level} style={styles.matrixCol}>
              <Text
                numberOfLines={1}
                style={[styles.matrixHeadText, column.highRisk && styles.matrixHeadTextRisk]}
              >
                {t(`discount.levels.${column.level}`)}
              </Text>
            </View>
          ))}
        </View>
        {grid.rows.map((row) => (
          <View key={row.scope} style={styles.matrixRow}>
            <View style={styles.matrixLabelCol}>
              <Text style={styles.matrixRowLabel} numberOfLines={1}>
                {t(`discount.scopes.${row.scope}`)}
              </Text>
            </View>
            {row.cells.map((cell, index) =>
              cell ? (
                <Pressable
                  key={cell.item.code}
                  accessibilityRole="checkbox"
                  accessibilityState={{ checked: cell.checked, disabled }}
                  accessibilityLabel={buildAccessibilityLabel(cell.item, cell.changed)}
                  accessibilityHint={cell.item.description || undefined}
                  disabled={disabled}
                  onPress={() => onToggleCode(cell.item.code)}
                  style={({ pressed }) => [
                    styles.matrixCol,
                    styles.matrixCell,
                    pressed && styles.pressed,
                  ]}
                >
                  <PosPermissionCheckbox
                    state={cell.checked ? "all" : "none"}
                    tone={cell.item.highRisk ? "danger" : "default"}
                    disabled={disabled}
                  />
                  {cell.changed ? <PosPermissionChangeDot style={styles.matrixDot} /> : null}
                </Pressable>
              ) : (
                // 这一格没有对应权限（无权分配或被筛掉）：占位保持列对齐。
                <View key={grid.columns[index].level} style={styles.matrixCol} />
              )
            )}
          </View>
        ))}
      </View>
    );
  };

  return (
    <View style={[styles.section, !first && styles.sectionDivider]}>
      <View style={styles.header}>
        <Pressable
          accessibilityRole="checkbox"
          accessibilityState={{
            checked: selectionState === "all" ? true : selectionState === "partial" ? "mixed" : false,
            disabled,
          }}
          accessibilityLabel={t("group.toggleAll", { group: groupLabel })}
          disabled={disabled}
          hitSlop={4}
          onPress={() =>
            onSetGroupChecked(fullCodes, getNextPosPermissionGroupChecked(selectionState))
          }
          style={({ pressed }) => [styles.groupCheckbox, pressed && styles.pressed]}
        >
          <PosPermissionCheckbox state={selectionState} disabled={disabled} />
        </Pressable>
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ expanded }}
          accessibilityLabel={t(expanded ? "group.collapse" : "group.expand", { group: groupLabel })}
          onPress={() => onToggleExpanded(group)}
          style={({ pressed }) => [styles.headerMain, pressed && styles.pressed]}
        >
          <View style={styles.headerText}>
            <View style={styles.headerTitleRow}>
              <Text style={styles.groupTitle} numberOfLines={1}>
                {groupLabel}
              </Text>
              {groupHasChanges ? <PosPermissionChangeDot /> : null}
              {hasHighRisk ? <PeopleTag label={t("badges.groupHighRisk")} tone="red" /> : null}
            </View>
            <Text style={[styles.groupSummary, summary.muted && styles.groupSummaryMuted]} numberOfLines={1}>
              {summary.text}
            </Text>
          </View>
          <Text style={styles.groupCount}>
            {selectedCount}/{fullCodes.length}
          </Text>
          <Icon
            source={expanded ? "chevron-up" : "chevron-down"}
            size={22}
            color={HB_COLORS.textSecondary}
          />
        </Pressable>
      </View>

      {expanded ? (
        <View style={styles.body}>
          {group.key === "discount" ? renderDiscountMatrix() : group.items.map(renderItemRow)}
        </View>
      ) : null}
    </View>
  );
}

export const PosPermissionGroupCard = memo(PosPermissionGroupCardComponent);

const styles = StyleSheet.create({
  section: { backgroundColor: HB_COLORS.white },
  sectionDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  header: { flexDirection: "row", alignItems: "center", minHeight: 64 },
  groupCheckbox: {
    width: 52,
    alignSelf: "stretch",
    alignItems: "flex-end",
    justifyContent: "center",
    paddingRight: 4,
  },
  headerMain: {
    flex: 1,
    minHeight: 64,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingLeft: HB_SPACING.xs,
    paddingRight: HB_SPACING.sm,
  },
  headerText: { flex: 1, minWidth: 0, gap: 3 },
  headerTitleRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  groupTitle: {
    flexShrink: 1,
    fontSize: 16,
    lineHeight: 22,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
  },
  groupSummary: { fontSize: 12.5, lineHeight: 18, color: HB_COLORS.textSecondary },
  groupSummaryMuted: { color: POS_PERMISSION_COLORS.disabledText },
  groupCount: {
    fontSize: 14,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  body: { backgroundColor: HB_COLORS.surface, paddingBottom: HB_SPACING.xs },
  itemRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingLeft: HB_SPACING.md + 12,
    paddingRight: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
  },
  dotSlot: { width: 10, alignItems: "center" },
  itemName: { flex: 1, fontSize: 15, lineHeight: 21, color: HB_COLORS.textPrimary },
  itemNameEntry: { fontWeight: "700" },
  itemNameDimmed: { color: POS_PERMISSION_COLORS.disabledText },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  changeDot: {
    width: 7,
    height: 7,
    borderRadius: 4,
    backgroundColor: HB_COLORS.brand,
  },
  entryHint: {
    marginLeft: HB_SPACING.md + 12,
    marginRight: HB_SPACING.md,
    marginBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    borderRadius: 10,
    backgroundColor: POS_PERMISSION_COLORS.warningSoft,
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 6,
  },
  entryHintText: { flex: 1, fontSize: 13, lineHeight: 18, color: HB_COLORS.warning },
  matrix: { paddingHorizontal: HB_SPACING.sm, paddingTop: HB_SPACING.xxs },
  matrixRow: { flexDirection: "row", alignItems: "center" },
  matrixLabelCol: { width: 44, justifyContent: "center" },
  matrixCol: { flex: 1, minWidth: 0, alignItems: "center", justifyContent: "center" },
  matrixCell: { minHeight: 44 },
  matrixHeadText: { fontSize: 12.5, lineHeight: 28, color: HB_COLORS.textSecondary },
  matrixHeadTextRisk: { color: HB_COLORS.danger, fontWeight: "600" },
  matrixRowLabel: { fontSize: 13.5, fontWeight: "700", color: HB_COLORS.textPrimary },
  matrixDot: { position: "absolute", top: 6, right: 6 },
});
