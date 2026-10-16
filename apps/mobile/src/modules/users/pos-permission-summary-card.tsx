import { Pressable, ScrollView, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import { InfoCard, PEOPLE_TONES, PeopleTag } from "@/modules/people/ui";
import { usePosPermissionTranslation } from "@/modules/users/pos-permission-i18n";
import {
  POS_PERMISSION_PRESET_ORDER,
  type PosPermissionPresetKey,
} from "@/modules/users/pos-terminal-permission-presentation";
import type { PosPermissionOverview } from "@/modules/users/pos-terminal-permissions-view";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

interface PosPermissionSummaryCardProps {
  overview: PosPermissionOverview;
  /** 草稿与哪些快速套用完全一致（可分配范围受限时可能同时命中多个）。 */
  matchingPresets: ReadonlySet<PosPermissionPresetKey>;
  busy: boolean;
  onApplyPreset: (preset: PosPermissionPresetKey) => void;
  onOpenCopy: () => void;
  onRestore: () => void;
}

/**
 * 概要卡（设计稿 A5①）：模式标签 + 已开 / 总数 + 高风险数 + 「恢复角色默认」，
 * 下面一排快速套用芯片和「从同事复制」。恢复入口从原来藏在 ⋮ 里的菜单搬到这里；
 * 只有「本店自定义」才有东西可恢复，所以角色默认模式下直接不显示（不灰着占位）。
 */
export function PosPermissionSummaryCard({
  overview,
  matchingPresets,
  busy,
  onApplyPreset,
  onOpenCopy,
  onRestore,
}: PosPermissionSummaryCardProps) {
  const { t } = usePosPermissionTranslation();
  const override = overview.mode === "override";
  const hasRisk = overview.highRiskEnabledCount > 0;

  return (
    <InfoCard>
      <View style={styles.body}>
        <View style={styles.topRow}>
          <PeopleTag label={t(`mode.${overview.mode}`)} tone={override ? "blue" : "gray"} />
          <Text
            style={styles.stats}
            accessible
            accessibilityLabel={t("overview.a11y", {
              selected: overview.enabledCount,
              total: overview.total,
              risk: overview.highRiskEnabledCount,
            })}
          >
            {t("overview.enabledLabel")} <Text style={styles.statsNumber}>{overview.enabledCount}</Text>{" "}
            {t("overview.ofTotal", { total: overview.total })}
            {"  ·  "}
            {t("overview.highRiskLabel")}{" "}
            <Text style={[styles.statsNumber, hasRisk && styles.statsNumberRisk]}>
              {overview.highRiskEnabledCount}
            </Text>
          </Text>
          {override ? (
            <Pressable
              accessibilityRole="button"
              accessibilityState={{ disabled: busy }}
              disabled={busy}
              hitSlop={8}
              onPress={onRestore}
              style={styles.restore}
            >
              <Text style={[styles.restoreText, busy && styles.restoreTextDisabled]}>
                {t("actions.restore")}
              </Text>
            </Pressable>
          ) : null}
        </View>

        <ScrollView
          horizontal
          showsHorizontalScrollIndicator={false}
          contentContainerStyle={styles.chipRow}
          keyboardShouldPersistTaps="handled"
        >
          <Text style={styles.chipPrefix}>{t("presets.apply")}</Text>
          {POS_PERMISSION_PRESET_ORDER.map((preset) => {
            const active = matchingPresets.has(preset);
            return (
              <Pressable
                key={preset}
                accessibilityRole="button"
                accessibilityState={{ selected: active, disabled: busy }}
                disabled={busy}
                hitSlop={4}
                onPress={() => onApplyPreset(preset)}
                style={({ pressed }) => [styles.chip, active && styles.chipActive, pressed && styles.pressed]}
              >
                <Text style={[styles.chipText, active && styles.chipTextActive]}>
                  {t(`presets.${preset}`)}
                </Text>
              </Pressable>
            );
          })}
          <View style={styles.chipDivider} />
          <Pressable
            accessibilityRole="button"
            accessibilityState={{ disabled: busy }}
            disabled={busy}
            hitSlop={4}
            onPress={onOpenCopy}
            style={({ pressed }) => [styles.chip, pressed && styles.pressed]}
          >
            <Icon source="account-arrow-left-outline" size={15} color={HB_COLORS.textPrimary} />
            <Text style={styles.chipText}>{t("presets.copy")}</Text>
          </Pressable>
        </ScrollView>
      </View>
    </InfoCard>
  );
}

const styles = StyleSheet.create({
  body: { paddingHorizontal: 14, paddingVertical: 12, gap: 10 },
  topRow: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: 8 },
  stats: { fontSize: 13, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  statsNumber: { fontWeight: "700", color: HB_COLORS.textPrimary },
  statsNumberRisk: { color: HB_COLORS.danger },
  restore: { marginLeft: "auto", minHeight: 28, justifyContent: "center" },
  restoreText: { fontSize: 13, fontWeight: "700", color: HB_COLORS.action },
  restoreTextDisabled: { opacity: 0.45 },
  chipRow: { alignItems: "center", gap: 6, paddingRight: HB_SPACING.xs },
  chipPrefix: { fontSize: 12.5, color: HB_COLORS.textSecondary, marginRight: 2 },
  chip: {
    minHeight: 34,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingHorizontal: 12,
    borderRadius: 999,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  chipActive: { backgroundColor: PEOPLE_TONES.blue.bg, borderColor: HB_COLORS.brand },
  chipText: { fontSize: 13.5, fontWeight: "600", color: HB_COLORS.textPrimary },
  chipTextActive: { color: HB_COLORS.action },
  chipDivider: {
    width: StyleSheet.hairlineWidth,
    height: 18,
    marginHorizontal: 2,
    backgroundColor: HB_COLORS.outline,
  },
  pressed: { opacity: 0.7 },
});
