import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { CARD_STYLE, StatusTag, TONE_COLORS, type Translate } from "@/components/device-management/primitives";
import type { DeviceVersionSummary } from "@/modules/device-management/types";
import {
  buildRegisteredVersionSegments,
  type AppVersionGroup,
  type SegmentTone,
} from "@/modules/device-management/version-state";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

// 分布条的实心色：比标签浅底更饱和，保证一小段也看得清；与标签同一色相。
const BAR_COLORS: Record<SegmentTone, string> = {
  ok: "#12B76A",
  warn: "#F79009",
  info: HB_COLORS.brand,
  muted: HB_COLORS.outline,
};

const MAX_LEGEND_ITEMS = 5;

/** 设备注册的版本分布：一条按版本分段的占比条，图例可点击按版本筛选。 */
export function RegisteredVersionDistribution({
  summary,
  selectedVersions,
  unreportedSelected,
  onToggleVersion,
  onSelectUnreported,
  t,
}: {
  summary?: DeviceVersionSummary | null;
  selectedVersions: readonly string[];
  unreportedSelected: boolean;
  onToggleVersion: (version: string) => void;
  onSelectUnreported: () => void;
  t: Translate;
}) {
  const segments = buildRegisteredVersionSegments(summary);
  const counted = segments.reduce((sum, segment) => sum + segment.total, 0);
  // 没有任何带版本信息的设备（例如只筛了手机端）时整块不显示，免得出现空分布条。
  if (!summary || counted === 0) return null;

  const visible = segments.slice(0, MAX_LEGEND_ITEMS);
  const hiddenCount = segments.slice(MAX_LEGEND_ITEMS).reduce((sum, segment) => sum + segment.total, 0);

  return (
    <View style={styles.card}>
      <View style={styles.header}>
        <Text style={styles.title}>{t("deviceManagement:distribution.title", { count: counted })}</Text>
        {summary.targetVersion ? (
          <StatusTag tone="info" icon="tag-outline" label={t("deviceManagement:distribution.target", { version: summary.targetVersion })} />
        ) : (
          <StatusTag tone="muted" label={t("deviceManagement:distribution.noPolicy")} />
        )}
      </View>
      <View style={styles.bar} accessibilityRole="image" accessibilityLabel={t("deviceManagement:distribution.barLabel")}>
        {segments.map((segment) => (
          <View
            key={segment.appVersion ?? "unreported"}
            style={[styles.barSegment, { flexGrow: segment.total, backgroundColor: BAR_COLORS[segment.tone] }]}
          />
        ))}
      </View>
      <View style={styles.legend}>
        {visible.map((segment) => {
          const version = segment.appVersion;
          const selected = version === null ? unreportedSelected : selectedVersions.includes(version);
          return (
            <Pressable
              key={version ?? "unreported"}
              accessibilityRole="button"
              accessibilityState={{ selected }}
              onPress={() => (version === null ? onSelectUnreported() : onToggleVersion(version))}
              style={[styles.legendItem, selected && styles.legendItemSelected]}
              hitSlop={4}
            >
              <View style={[styles.swatch, { backgroundColor: BAR_COLORS[segment.tone] }]} />
              <Text style={[styles.legendText, selected && styles.legendTextSelected]}>
                {version === null ? t("deviceManagement:versionState.unreported") : version}
              </Text>
              <Text style={[styles.legendCount, selected && styles.legendTextSelected]}>{segment.total}</Text>
            </Pressable>
          );
        })}
        {hiddenCount > 0 ? (
          <Text style={styles.more}>{t("deviceManagement:distribution.others", { count: hiddenCount })}</Text>
        ) : null}
      </View>
    </View>
  );
}

/** App 的版本分布：按系统分行，每个版本一个可点 pill，绿色表示该系统在用的最新版本。 */
export function AppVersionDistributionCard({
  groups,
  selectedKey,
  onSelect,
  t,
}: {
  groups: AppVersionGroup[];
  selectedKey: string | null;
  onSelect: (key: string) => void;
  t: Translate;
}) {
  if (groups.length === 0) return null;
  return (
    <View style={styles.card}>
      <Text style={styles.title}>{t("deviceManagement:distribution.appTitle")}</Text>
      {groups.map((group) => (
        <View key={group.deviceSystem || "unknown"} style={styles.systemRow}>
          <Text style={styles.systemName}>{group.deviceSystem || t("deviceManagement:distribution.unknownSystem")}</Text>
          <View style={styles.pills}>
            {group.options.slice(0, 4).map((option) => {
              const selected = selectedKey === option.key;
              const colors = option.isLatest ? TONE_COLORS.ok : null;
              return (
                <Pressable
                  key={option.key}
                  accessibilityRole="button"
                  accessibilityState={{ selected }}
                  onPress={() => onSelect(option.key)}
                  hitSlop={4}
                  style={[
                    styles.pill,
                    colors && { backgroundColor: colors.bg, borderColor: "#ABEFC6" },
                    selected && styles.pillSelected,
                  ]}
                >
                  <Text style={[styles.pillText, colors && { color: colors.fg }, selected && styles.pillTextSelected]}>
                    {option.appVersion}
                  </Text>
                  <Text style={[styles.pillCount, selected && styles.pillTextSelected]}>{option.total}</Text>
                </Pressable>
              );
            })}
          </View>
        </View>
      ))}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { ...CARD_STYLE, paddingVertical: HB_SPACING.sm },
  header: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm },
  title: { fontSize: 13, lineHeight: 20, fontWeight: "700", color: HB_COLORS.textPrimary, flexShrink: 1 },
  bar: { flexDirection: "row", height: 10, borderRadius: 5, overflow: "hidden", gap: 2 },
  barSegment: { flexShrink: 1, flexBasis: 0, minWidth: 6 },
  legend: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: 6 },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 6, minHeight: 32, paddingHorizontal: 8, borderRadius: 8, borderWidth: 1, borderColor: "transparent" },
  legendItemSelected: { backgroundColor: "#EAF2FF", borderColor: "#B2CCFF" },
  swatch: { width: 9, height: 9, borderRadius: 3 },
  legendText: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  legendTextSelected: { color: HB_COLORS.action, fontWeight: "700" },
  legendCount: { fontSize: 12, lineHeight: 18, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  more: { fontSize: 12, color: HB_COLORS.textSecondary },
  systemRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  systemName: { width: 64, fontSize: 12, lineHeight: 18, fontWeight: "700", color: HB_COLORS.textPrimary },
  pills: { flex: 1, flexDirection: "row", flexWrap: "wrap", gap: 6 },
  pill: { flexDirection: "row", alignItems: "center", gap: 5, minHeight: 32, paddingHorizontal: 10, borderRadius: 16, borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white },
  pillSelected: { backgroundColor: "#EAF2FF", borderColor: HB_COLORS.action },
  pillText: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  pillTextSelected: { color: HB_COLORS.action, fontWeight: "700" },
  pillCount: { fontSize: 12, lineHeight: 18, fontWeight: "700", color: HB_COLORS.action, fontVariant: ["tabular-nums"] },
});
