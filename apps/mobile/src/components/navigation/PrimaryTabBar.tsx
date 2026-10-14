import { Platform, Pressable, StyleSheet, Text, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useRouter } from "expo-router";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAppNavigationAccess } from "@/modules/navigation/access-context";
import { TAB_PATHS } from "@/modules/navigation/default-route";
import {
  buildPrimaryNavigation,
  resolveMeTabLabel,
  resolvePrimaryNavigationAction,
  resolvePrimaryTabBadge,
} from "@/modules/navigation/primary-navigation";
import { markReportHubNavigationStart } from "@/modules/reports/report-load-performance";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

interface PrimaryTabBarProps {
  activeRouteName?: string;
}

export function PrimaryTabBar({ activeRouteName }: PrimaryTabBarProps) {
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const { t } = useAppTranslation("common");
  const { orderedVisibleRouteNames, isDeviceMode, faceHitUnreadCount } = useAppNavigationAccess();
  const fullName = useAuthStore((state) => state.user?.fullName);
  const username = useAuthStore((state) => state.user?.username);
  const items = buildPrimaryNavigation({
    activeRouteName,
    visibleRouteNames: orderedVisibleRouteNames,
    isDeviceMode,
  });

  return (
    <View
      style={[
        styles.container,
        {
          paddingBottom: Math.max(insets.bottom, Platform.OS === "android" ? 4 : 0),
        },
      ]}
    >
      {items.map((item) => {
        const targetPath = TAB_PATHS[item.targetRouteName];
        const baseLabel = t(item.labelKey);
        // 「我的」换成当前登录人姓名；无障碍标签保留「我的」前缀，读屏仍能识别入口用途。
        const label =
          item.key === "me"
            ? resolveMeTabLabel({ fullName, username, isDeviceMode, fallbackLabel: baseLabel })
            : baseLabel;
        const baseAccessibilityLabel = label === baseLabel ? baseLabel : `${baseLabel} ${label}`;
        // 红色数字徽标（> 0 才有，99 封顶）：无障碍标签带上真实数字，读屏不依赖视觉上的红点。
        const badge = resolvePrimaryTabBadge(item.key, { faceHitUnreadCount });
        const accessibilityLabel = badge
          ? t("tabs.unreadHitsLabel", { label: baseAccessibilityLabel, count: badge.count })
          : baseAccessibilityLabel;
        const iconColor = item.active ? HB_COLORS.brand : HB_COLORS.textSecondary;
        const labelColor = item.active ? HB_COLORS.action : HB_COLORS.textSecondary;

        const onPress = () => {
          if (!targetPath) {
            return;
          }

          const action = resolvePrimaryNavigationAction(activeRouteName, item);
          if (action === "none") {
            return;
          }
          if (item.targetRouteName === "reports") {
            // 报告路由常驻且可能停留在商品页签，实际活动页签会认领并整组消费这次点击。
            markReportHubNavigationStart();
          }
          if (action === "dismiss-to") {
            // 工作台与同一一级上下文的子页都回固定根页，离页仍受 usePreventRemove 保护。
            router.dismissTo(targetPath as Parameters<typeof router.dismissTo>[0]);
          } else if (action === "navigate") {
            router.navigate(targetPath as Parameters<typeof router.navigate>[0]);
          }
        };

        return (
          <Pressable
            key={item.key}
            accessibilityRole="button"
            accessibilityLabel={accessibilityLabel}
            accessibilityState={{ selected: item.active }}
            onPress={onPress}
            style={({ pressed }) => [
              styles.item,
              pressed ? styles.itemPressed : null,
            ]}
          >
            <View style={styles.iconArea}>
              {item.active ? <View style={styles.activeIndicator} /> : null}
              <MaterialCommunityIcons name={item.icon} color={iconColor} size={23} />
              {badge ? (
                <View style={styles.badge} pointerEvents="none">
                  <Text allowFontScaling={false} style={styles.badgeText}>
                    {badge.text}
                  </Text>
                </View>
              ) : null}
            </View>
            <Text
              allowFontScaling
              maxFontSizeMultiplier={1.6}
              numberOfLines={2}
              ellipsizeMode="tail"
              style={[
                styles.label,
                item.active ? styles.labelActive : null,
                { color: labelColor },
              ]}
            >
              {label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    flexDirection: "row",
    backgroundColor: HB_COLORS.surface,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outline,
  },
  item: {
    flex: 1,
    minHeight: Platform.OS === "android" ? 56 : 50,
    alignItems: "center",
    justifyContent: "center",
    paddingHorizontal: 2,
    paddingVertical: 5,
  },
  itemPressed: {
    backgroundColor: "#EAF2FF",
  },
  iconArea: {
    minWidth: 32,
    minHeight: 26,
    alignItems: "center",
    justifyContent: "center",
  },
  activeIndicator: {
    position: "absolute",
    top: -5,
    width: 24,
    height: 3,
    borderRadius: 2,
    backgroundColor: HB_COLORS.brand,
  },
  // 红色数字徽标：挂在图标右上角，红底白字、白色描边，不挤占标签宽度（设计稿 01/02：#D92D20、11px 加粗、最小 18×18）。
  badge: {
    position: "absolute",
    top: -4,
    right: -3,
    minWidth: 18,
    height: 18,
    paddingHorizontal: 5,
    borderRadius: 9,
    borderWidth: 1.5,
    borderColor: HB_COLORS.surface,
    backgroundColor: "#D92D20",
    alignItems: "center",
    justifyContent: "center",
  },
  badgeText: {
    color: HB_COLORS.white,
    fontSize: 11,
    lineHeight: 14,
    fontWeight: "800",
    fontVariant: ["tabular-nums"],
    includeFontPadding: false,
  },
  label: {
    marginTop: 1,
    fontSize: 12,
    lineHeight: 16,
    fontWeight: "500",
    textAlign: "center",
  },
  labelActive: {
    fontWeight: "700",
  },
});
