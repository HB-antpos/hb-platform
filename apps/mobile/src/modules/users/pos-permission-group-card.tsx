import { memo } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import {
  buildPosDiscountMatrix,
  getNextPosPermissionGroupChecked,
  getPosPermissionGroupSummary,
  getPosPermissionSelectionState,
  hasPosPermissionEntryGap,
  type PosPermissionDisplayGroup,
  type PosPermissionDisplayItem,
  type PosPermissionSelectionState,
} from "@/modules/users/pos-terminal-permission-presentation";
import { POS_PERMISSION_COLORS } from "@/modules/users/pos-permission-sheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

const CHECKBOX_ICONS: Record<PosPermissionSelectionState, string> = {
  all: "checkbox-marked",
  partial: "minus-box",
  none: "checkbox-blank-outline",
};

/** 纯展示的勾选框图标；交互由外层 Pressable 承担，避免嵌套可点击控件。 */
function CheckboxGlyph({
  state,
  disabled,
}: {
  state: PosPermissionSelectionState;
  disabled?: boolean;
}) {
  const color = disabled
    ? POS_PERMISSION_COLORS.disabledText
    : state === "none"
      ? HB_COLORS.outline
      : HB_COLORS.brand;
  return <Icon source={CHECKBOX_ICONS[state]} size={24} color={color} />;
}

export function PosPermissionHighRiskPill({ label }: { label: string }) {
  return (
    <View style={styles.riskPill}>
      <Text style={styles.riskPillText}>{label}</Text>
    </View>
  );
}

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
  onToggleExpanded: (group: PosPermissionDisplayGroup) => void;
  onToggleCode: (code: string) => void;
  onSetGroupChecked: (codes: string[], checked: boolean) => void;
}

function PosPermissionGroupCardComponent({
  group,
  groupLabel,
  fullGroup,
  selectedCodeSet,
  changedCodeSet,
  expanded,
  disabled,
  onToggleExpanded,
  onToggleCode,
  onSetGroupChecked,
}: PosPermissionGroupCardProps) {
  const { t } = useAppTranslation(["userManagement", "common"]);
  const fullCodes = fullGroup.items.map((item) => item.code);
  const selectionState = getPosPermissionSelectionState(fullCodes, selectedCodeSet);
  const summary = getPosPermissionGroupSummary(fullGroup, selectedCodeSet);
  const entryGap = hasPosPermissionEntryGap(fullGroup, selectedCodeSet);
  const groupHasChanges = fullCodes.some((code) => changedCodeSet.has(code));
  const subtitle =
    summary.selectedCount === 0
      ? t("posPermissions.group.notEnabled")
      : summary.remainingCount > 0
        ? t("posPermissions.group.previewMore", {
            names: summary.previewNames.join("、"),
            value: summary.selectedCount,
          })
        : summary.previewNames.join("、");

  const renderItemRow = (item: PosPermissionDisplayItem) => {
    const checked = selectedCodeSet.has(item.code);
    const changed = changedCodeSet.has(item.code);
    // 入口没开时，组内其他项在 POS 上用不到：只做视觉提示，不改勾选。
    const dimmed = entryGap && !item.isEntry;
    const accessibilityLabel = [
      item.name,
      item.highRisk ? t("posPermissions.badges.highRisk") : null,
      changed ? t("posPermissions.badges.changed") : null,
    ]
      .filter(Boolean)
      .join("，");

    return (
      <View key={item.code}>
        <Pressable
          accessibilityRole="checkbox"
          accessibilityState={{ checked, disabled }}
          accessibilityLabel={accessibilityLabel}
          accessibilityHint={item.description || undefined}
          disabled={disabled}
          onPress={() => onToggleCode(item.code)}
          style={({ pressed }) => [styles.itemRow, pressed && styles.pressed]}
        >
          <View style={styles.dotSlot}>{changed ? <PosPermissionChangeDot /> : null}</View>
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
          {item.highRisk ? (
            <PosPermissionHighRiskPill label={t("posPermissions.badges.highRisk")} />
          ) : null}
          <CheckboxGlyph state={checked ? "all" : "none"} disabled={disabled || dimmed} />
        </Pressable>
        {item.isEntry && entryGap ? (
          <View style={styles.entryHint} accessibilityRole="alert">
            <Icon source="alert-outline" size={16} color={HB_COLORS.warning} />
            <Text style={styles.entryHintText}>
              {t("posPermissions.entryHint", { name: item.name })}
            </Text>
          </View>
        ) : null}
      </View>
    );
  };

  const renderDiscountMatrix = () =>
    buildPosDiscountMatrix(group.items).map((row) => (
      <View key={row.scope} style={styles.matrixRow}>
        <Text style={styles.matrixLabel}>{t(`posPermissions.discount.${row.scope}`)}</Text>
        <View style={styles.matrixCells}>
          {row.cells.map(({ level, item }) => {
            const checked = selectedCodeSet.has(item.code);
            const changed = changedCodeSet.has(item.code);
            const levelLabel = t(`posPermissions.discount.levels.${level}`);
            return (
              <Pressable
                key={item.code}
                accessibilityRole="checkbox"
                accessibilityState={{ checked, disabled }}
                accessibilityLabel={[
                  item.name,
                  item.highRisk ? t("posPermissions.badges.highRisk") : null,
                  changed ? t("posPermissions.badges.changed") : null,
                ]
                  .filter(Boolean)
                  .join("，")}
                accessibilityHint={item.description || undefined}
                disabled={disabled}
                onPress={() => onToggleCode(item.code)}
                style={({ pressed }) => [
                  styles.matrixCell,
                  item.highRisk && styles.matrixCellRisk,
                  checked && (item.highRisk ? styles.matrixCellRiskOn : styles.matrixCellOn),
                  disabled && styles.matrixCellDisabled,
                  pressed && styles.pressed,
                ]}
              >
                <Text
                  numberOfLines={1}
                  style={[
                    styles.matrixCellText,
                    item.highRisk && styles.matrixCellTextRisk,
                    checked && styles.matrixCellTextOn,
                  ]}
                >
                  {levelLabel}
                </Text>
                {changed ? <PosPermissionChangeDot style={styles.matrixDot} /> : null}
              </Pressable>
            );
          })}
        </View>
      </View>
    ));

  return (
    <View style={styles.card}>
      <View style={styles.header}>
        <Pressable
          accessibilityRole="checkbox"
          accessibilityState={{
            checked: selectionState === "all" ? true : selectionState === "partial" ? "mixed" : false,
            disabled,
          }}
          accessibilityLabel={t("posPermissions.group.toggleAll", { group: groupLabel })}
          disabled={disabled}
          hitSlop={4}
          onPress={() =>
            onSetGroupChecked(fullCodes, getNextPosPermissionGroupChecked(selectionState))
          }
          style={({ pressed }) => [styles.groupCheckbox, pressed && styles.pressed]}
        >
          <CheckboxGlyph state={selectionState} disabled={disabled} />
        </Pressable>
        <Pressable
          accessibilityRole="button"
          accessibilityState={{ expanded }}
          accessibilityLabel={t(expanded ? "posPermissions.group.collapse" : "posPermissions.group.expand", {
            group: groupLabel,
          })}
          onPress={() => onToggleExpanded(group)}
          style={({ pressed }) => [styles.headerMain, pressed && styles.pressed]}
        >
          <View style={styles.headerText}>
            <View style={styles.headerTitleRow}>
              <Text style={styles.groupTitle} numberOfLines={1}>
                {groupLabel}
              </Text>
              {groupHasChanges ? <PosPermissionChangeDot /> : null}
            </View>
            <Text
              style={[styles.groupSubtitle, summary.selectedCount === 0 && styles.groupSubtitleMuted]}
              numberOfLines={1}
            >
              {subtitle}
            </Text>
          </View>
          <Text style={styles.groupCount}>
            {summary.selectedCount} / {summary.total}
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
  card: {
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    overflow: "hidden",
  },
  header: { flexDirection: "row", alignItems: "center", minHeight: 64 },
  groupCheckbox: {
    width: 52,
    alignSelf: "stretch",
    alignItems: "center",
    justifyContent: "center",
  },
  headerMain: {
    flex: 1,
    minHeight: 64,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingRight: HB_SPACING.sm,
  },
  headerText: { flex: 1, minWidth: 0, gap: 2 },
  headerTitleRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  groupTitle: { fontSize: 16, lineHeight: 22, fontWeight: "700", color: HB_COLORS.textPrimary },
  groupSubtitle: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  groupSubtitleMuted: { color: POS_PERMISSION_COLORS.disabledText },
  groupCount: {
    fontSize: 14,
    fontWeight: "600",
    color: HB_COLORS.textSecondary,
    fontVariant: ["tabular-nums"],
  },
  body: {
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
    paddingVertical: HB_SPACING.xxs,
  },
  itemRow: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingLeft: HB_SPACING.xs,
    paddingRight: HB_SPACING.md,
    paddingVertical: HB_SPACING.xs,
  },
  dotSlot: { width: 12, alignItems: "center" },
  itemName: { flex: 1, fontSize: 15, lineHeight: 21, color: HB_COLORS.textPrimary },
  itemNameEntry: { fontWeight: "700" },
  itemNameDimmed: { color: POS_PERMISSION_COLORS.disabledText },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  riskPill: {
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 4,
    backgroundColor: POS_PERMISSION_COLORS.dangerSoft,
  },
  riskPillText: { fontSize: 11, lineHeight: 16, fontWeight: "600", color: HB_COLORS.danger },
  changeDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
    backgroundColor: POS_PERMISSION_COLORS.changeDot,
  },
  entryHint: {
    marginLeft: HB_SPACING.lg - 4,
    marginRight: HB_SPACING.md,
    marginBottom: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: HB_SPACING.xs,
    borderRadius: HB_RADIUS.control,
    backgroundColor: POS_PERMISSION_COLORS.warningSoft,
    borderWidth: 1,
    borderColor: POS_PERMISSION_COLORS.warningBorder,
    flexDirection: "row",
    alignItems: "flex-start",
    gap: 6,
  },
  entryHintText: { flex: 1, fontSize: 13, lineHeight: 18, color: HB_COLORS.warning },
  matrixRow: { paddingHorizontal: HB_SPACING.md, paddingVertical: HB_SPACING.xs, gap: 6 },
  matrixLabel: { fontSize: 13, lineHeight: 18, fontWeight: "600", color: HB_COLORS.textSecondary },
  matrixCells: { flexDirection: "row", gap: 6 },
  matrixCell: {
    flex: 1,
    minHeight: 44,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 2,
  },
  matrixCellOn: { backgroundColor: HB_COLORS.brand, borderColor: HB_COLORS.brand },
  matrixCellRisk: { borderColor: POS_PERMISSION_COLORS.dangerBorder },
  matrixCellRiskOn: { backgroundColor: HB_COLORS.danger, borderColor: HB_COLORS.danger },
  matrixCellDisabled: { opacity: 0.5 },
  matrixCellText: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  matrixCellTextRisk: { color: HB_COLORS.danger },
  matrixCellTextOn: { color: HB_COLORS.white },
  matrixDot: { position: "absolute", top: 3, right: 3, borderWidth: 1, borderColor: HB_COLORS.white },
});
