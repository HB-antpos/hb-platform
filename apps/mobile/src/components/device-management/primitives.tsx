import type { ReactNode } from "react";
import { Platform, Pressable, StyleSheet, View } from "react-native";
import { Icon, Text } from "react-native-paper";
import type { VersionBadge, VersionTone } from "@/modules/device-management/version-state";
import { getRelativeAge, parseUtcTimestamp } from "@/modules/device-management/version-state";
import type { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export type Translate = ReturnType<typeof useAppTranslation>["t"];

/** 色调 → 背景 / 前景色，全部来自 HB 令牌（浅底 + 深字，保证对比度）。 */
export const TONE_COLORS: Record<VersionTone | "danger", { bg: string; fg: string }> = {
  ok: { bg: "#E7F6EC", fg: HB_COLORS.success },
  warn: { bg: "#FEF0C7", fg: HB_COLORS.warning },
  info: { bg: "#EAF2FF", fg: HB_COLORS.action },
  muted: { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary },
  danger: { bg: "#FEE4E2", fg: HB_COLORS.danger },
};

export function StatusTag({ tone, label, icon }: { tone: VersionTone | "danger"; label: string; icon?: string }) {
  const colors = TONE_COLORS[tone];
  return (
    <View style={[styles.tag, { backgroundColor: colors.bg }]}>
      {icon ? <Icon source={icon} size={14} color={colors.fg} /> : null}
      <Text style={[styles.tagText, { color: colors.fg }]} numberOfLines={1}>{label}</Text>
    </View>
  );
}

/** 单选 / 多选共用的筛选 chip，右侧可带数量；数量为 0 时置灰但仍可点（便于清除已选）。 */
export function FilterChip({
  label,
  count,
  selected,
  onPress,
  icon,
  accessibilityLabel,
}: {
  label: string;
  count?: number;
  selected?: boolean;
  onPress: () => void;
  icon?: string;
  accessibilityLabel?: string;
}) {
  const dim = count === 0 && !selected;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected: Boolean(selected) }}
      accessibilityLabel={accessibilityLabel ?? (count === undefined ? label : `${label} ${count}`)}
      onPress={onPress}
      style={({ pressed }) => [
        styles.chip,
        selected && styles.chipSelected,
        dim && styles.chipDim,
        pressed && styles.pressed,
      ]}
    >
      {icon ? <Icon source={icon} size={14} color={selected ? HB_COLORS.action : HB_COLORS.textSecondary} /> : null}
      <Text style={[styles.chipText, selected && styles.chipTextSelected]} numberOfLines={1}>{label}</Text>
      {count !== undefined ? (
        <Text style={[styles.chipCount, selected && styles.chipTextSelected]}>{count}</Text>
      ) : null}
    </Pressable>
  );
}

/** 已生效筛选条件：点 × 单独移除。 */
export function RemovableChip({ label, icon, onRemove, removeLabel }: { label: string; icon?: string; onRemove: () => void; removeLabel: string }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${removeLabel} ${label}`}
      onPress={onRemove}
      style={({ pressed }) => [styles.removable, pressed && styles.pressed]}
      hitSlop={6}
    >
      {icon ? <Icon source={icon} size={14} color={HB_COLORS.action} /> : null}
      <Text style={styles.removableText} numberOfLines={1}>{label}</Text>
      <Icon source="close" size={14} color={HB_COLORS.action} />
    </Pressable>
  );
}

/** 卡片里的「当前版本」块：左边版本号，右边状态标签和一行提示。 */
export function VersionBlock({
  label,
  version,
  build,
  badge,
  t,
}: {
  label: string;
  version?: string | null;
  build?: string | null;
  badge: VersionBadge | null;
  t: Translate;
}) {
  return (
    <View style={styles.versionBlock}>
      <View style={styles.versionMain}>
        <Text style={styles.versionLabel}>{label}</Text>
        <Text style={styles.versionValue} numberOfLines={1}>
          {version || "—"}
          {build ? <Text style={styles.versionBuild}>{` (${build})`}</Text> : null}
        </Text>
      </View>
      {badge ? (
        <View style={styles.versionSide}>
          <StatusTag
            tone={badge.tone}
            label={t(`deviceManagement:${badge.labelKey}`, badge.labelParams)}
            icon={badge.tone === "ok" ? "check" : badge.tone === "warn" ? "arrow-up" : undefined}
          />
          {badge.hintKey ? (
            <Text style={styles.versionHint} numberOfLines={1}>{t(`deviceManagement:${badge.hintKey}`, badge.hintParams)}</Text>
          ) : null}
        </View>
      ) : null}
    </View>
  );
}

export function KeyValueGrid({ items }: { items: { label: string; value: ReactNode; mono?: boolean }[] }) {
  return (
    <View style={styles.kvGrid}>
      {items.map((item) => (
        <View key={item.label} style={styles.kvCell}>
          <Text style={styles.kvLabel}>{item.label}</Text>
          <Text style={[styles.kvValue, item.mono && styles.mono]} numberOfLines={2}>{item.value}</Text>
        </View>
      ))}
    </View>
  );
}

/** 「3 分钟前」之类的相对时间；时间缺失或无法解析时返回 null，调用方自行省略。 */
export function formatAgeText(iso: string | null | undefined, t: Translate): string | null {
  const age = getRelativeAge(parseUtcTimestamp(iso));
  if (!age) return null;
  return age.unit === "justNow"
    ? t("deviceManagement:time.justNow")
    : t(`deviceManagement:time.${age.unit}`, { n: age.count });
}

const styles = StyleSheet.create({
  pressed: { opacity: 0.7 },
  tag: { flexDirection: "row", alignItems: "center", gap: 4, height: 24, paddingHorizontal: 10, borderRadius: 12, alignSelf: "flex-start" },
  tagText: { fontSize: 12, lineHeight: 16, fontWeight: "600", flexShrink: 1 },
  chip: { flexDirection: "row", alignItems: "center", gap: 6, minHeight: 36, paddingHorizontal: 12, borderRadius: 18, borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white },
  chipSelected: { backgroundColor: "#EAF2FF", borderColor: "#84ADFF" },
  chipDim: { opacity: 0.5 },
  chipText: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  chipTextSelected: { color: HB_COLORS.action, fontWeight: "700" },
  chipCount: { fontSize: 12, lineHeight: 16, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  removable: { flexDirection: "row", alignItems: "center", gap: 6, minHeight: 32, paddingLeft: 10, paddingRight: 8, borderRadius: 16, backgroundColor: "#EAF2FF", borderWidth: 1, borderColor: "#B2CCFF", maxWidth: "100%" },
  removableText: { fontSize: 12.5, lineHeight: 18, color: HB_COLORS.action, flexShrink: 1 },
  versionBlock: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm, padding: HB_SPACING.sm, borderRadius: 10, backgroundColor: HB_COLORS.surfaceMuted },
  versionMain: { flexShrink: 1, gap: 2 },
  versionLabel: { fontSize: 11, lineHeight: 16, color: HB_COLORS.textSecondary },
  versionValue: { fontSize: 20, lineHeight: 26, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  versionBuild: { fontSize: 12, fontWeight: "500", color: HB_COLORS.textSecondary },
  versionSide: { alignItems: "flex-end", gap: 4, flexShrink: 1, maxWidth: "60%" },
  versionHint: { fontSize: 11, lineHeight: 16, color: HB_COLORS.textSecondary },
  kvGrid: { flexDirection: "row", flexWrap: "wrap", rowGap: HB_SPACING.sm, columnGap: HB_SPACING.sm },
  kvCell: { flexBasis: "47%", flexGrow: 1, gap: 1 },
  kvLabel: { fontSize: 11, lineHeight: 16, color: HB_COLORS.textSecondary },
  kvValue: { fontSize: 13, lineHeight: 18, fontWeight: "600", color: HB_COLORS.textPrimary },
  mono: { fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }), fontSize: 12 },
});

export const CARD_STYLE = {
  backgroundColor: HB_COLORS.white,
  borderWidth: 1,
  borderColor: HB_COLORS.outlineMuted,
  borderRadius: HB_RADIUS.surface,
  padding: HB_SPACING.md,
  gap: HB_SPACING.sm,
} as const;
