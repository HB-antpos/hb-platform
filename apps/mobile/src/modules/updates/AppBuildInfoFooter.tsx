import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import {
  buildAppUpdateInfoRows,
  formatAppPackageVersion,
  resolveAppUpdateChannelKind,
  resolveAppUpdateSourceKey,
  type AppUpdateChannelKind,
  type AppUpdateInfo,
} from "@/modules/updates/app-update-info";
import { getCurrentAppUpdateInfo } from "@/modules/updates/app-update-runtime";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

const EMPTY_UPDATE_INFO: AppUpdateInfo = {
  appVersion: null,
  appBuildVersion: null,
  runtimeVersion: null,
  channel: null,
  updateId: null,
  isEmbeddedLaunch: false,
};

// 与设置页状态标签同一套浅底 + 深字配色；开发渠道借用品牌蓝，和正式/预览区分开。
const CHANNEL_TONES: Record<AppUpdateChannelKind, { background: string; text: string }> = {
  production: { background: "#ECFDF3", text: HB_COLORS.success },
  preview: { background: "#FFFAEB", text: HB_COLORS.warning },
  development: { background: "#EFF8FF", text: HB_COLORS.action },
  custom: { background: HB_COLORS.surfaceMuted, text: HB_COLORS.textSecondary },
  none: { background: HB_COLORS.surfaceMuted, text: HB_COLORS.textSecondary },
};

function readUpdateInfo(): AppUpdateInfo {
  try {
    return getCurrentAppUpdateInfo();
  } catch {
    // 读不到原生版本信息（如模块不可用）时显示「未知」，不能让登录页因此报错。
    return EMPTY_UPDATE_INFO;
  }
}

/**
 * 登录页底部的版本条：一行显示「版本 · 渠道 · 来源」，点开查看完整信息。
 * 店员向管理员报问题、排查装的是正式包还是预览包时，不用先登录进设置页。
 */
export function AppBuildInfoFooter() {
  const { t } = useAppTranslation(["login", "settings", "common"]);
  // 版本与渠道在一次运行内不变（OTA 要重启才生效），挂载时读一次即可。
  const [info] = useState(readUpdateInfo);
  const [detailsVisible, setDetailsVisible] = useState(false);

  const version = formatAppPackageVersion(info, t("settings:updates.unknown"));
  const channelKind = resolveAppUpdateChannelKind(info.channel);
  const channelLabel = t(`buildInfo.channel.${channelKind}`);
  const sourceLabel = t(`settings:${resolveAppUpdateSourceKey(info)}`);
  const tone = CHANNEL_TONES[channelKind];
  const rows = useMemo(() => buildAppUpdateInfoRows(info), [info]);

  return (
    <>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("buildInfo.accessibilityLabel", {
          version,
          channel: channelLabel,
          source: sourceLabel,
        })}
        hitSlop={8}
        onPress={() => setDetailsVisible(true)}
        style={({ pressed }) => [styles.strip, pressed ? styles.stripPressed : null]}
      >
        <Text style={styles.version}>{t("buildInfo.version", { version })}</Text>
        <View style={[styles.channelPill, { backgroundColor: tone.background }]}>
          <View style={[styles.channelDot, { backgroundColor: tone.text }]} />
          <Text style={[styles.channelText, { color: tone.text }]}>{channelLabel}</Text>
        </View>
        <Text style={styles.source}>{sourceLabel}</Text>
        <Icon source="information-outline" size={14} color={HB_COLORS.textSecondary} />
      </Pressable>

      <BusinessSheet
        visible={detailsVisible}
        title={t("buildInfo.detailsTitle")}
        subtitle={t("buildInfo.detailsSubtitle")}
        onDismiss={() => setDetailsVisible(false)}
        footer={
          <View style={styles.sheetActions}>
            <Button mode="text" textColor={HB_COLORS.textSecondary} onPress={() => setDetailsVisible(false)}>
              {t("common:actions.close")}
            </Button>
          </View>
        }
      >
        <View style={styles.detailList}>
          {rows.map((row, index) => (
            <View key={row.key} style={[styles.detailRow, index > 0 ? styles.detailRowDivider : null]}>
              <Text style={styles.detailLabel}>{t(`settings:${row.labelKey}`)}</Text>
              {/* 渠道原文与更新 ID 很长：允许换行与长按复制，方便发给管理员。 */}
              <Text selectable style={styles.detailValue}>
                {row.value ?? (row.valueKey ? t(`settings:${row.valueKey}`) : "")}
              </Text>
            </View>
          ))}
        </View>
      </BusinessSheet>
    </>
  );
}

const styles = StyleSheet.create({
  strip: {
    alignItems: "center",
    alignSelf: "center",
    borderRadius: 999,
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 6,
    justifyContent: "center",
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
  },
  stripPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  version: {
    color: HB_COLORS.textSecondary,
    fontSize: 12,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
  },
  channelPill: {
    alignItems: "center",
    borderRadius: 999,
    flexDirection: "row",
    gap: 4,
    paddingHorizontal: 7,
    paddingVertical: 2,
  },
  channelDot: { borderRadius: 3, height: 6, width: 6 },
  channelText: { fontSize: 11, fontWeight: "700" },
  source: { color: HB_COLORS.textSecondary, fontSize: 12 },
  sheetActions: { flexDirection: "row", justifyContent: "flex-end" },
  detailList: { paddingBottom: HB_SPACING.xs },
  detailRow: { gap: 2, paddingVertical: 10 },
  detailRowDivider: {
    borderTopColor: HB_COLORS.outlineMuted,
    borderTopWidth: StyleSheet.hairlineWidth,
  },
  detailLabel: { color: HB_COLORS.textSecondary, fontSize: 12 },
  detailValue: {
    color: HB_COLORS.textPrimary,
    fontSize: 15,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
  },
});
