import { useEffect, type ComponentProps } from "react";
import { BackHandler, Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { AppLanguage } from "@/shared/i18n/types";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type { WorkbenchNavigationItem } from "@/modules/navigation/workbench";
import type { QuickJumpModel } from "./quick-jump";

type IconName = ComponentProps<typeof MaterialCommunityIcons>["name"];

interface QuickJumpPanelProps {
  model: QuickJumpModel;
  language: AppLanguage;
  menuUnavailable: boolean;
  maxHeight: number;
  onClose: () => void;
  onNavigate: (routeName: string) => void;
  onChangeLanguage: (language: AppLanguage) => void;
  onHideButton: () => void;
}

/**
 * 按住悬浮按钮 2 秒弹出的快速跳转面板。
 * 用自绘遮罩 + 面板而不是原生 Modal：原生 Modal 是独立窗口、iOS 上连续开关还会被系统忽略；
 * 放在 Paper Provider 内部，页面自己的 Paper 弹窗仍会盖在它上面。
 */
export function QuickJumpPanel({
  model,
  language,
  menuUnavailable,
  maxHeight,
  onClose,
  onNavigate,
  onChangeLanguage,
  onHideButton,
}: QuickJumpPanelProps) {
  const { t } = useAppTranslation(["workbench", "common"]);

  // 安卓返回键先关面板，不退页面。
  useEffect(() => {
    const subscription = BackHandler.addEventListener("hardwareBackPress", () => {
      onClose();
      return true;
    });
    return () => subscription.remove();
  }, [onClose]);

  const renderTile = (item: WorkbenchNavigationItem) => (
    <Pressable
      key={item.routeName}
      accessibilityRole="button"
      accessibilityLabel={t(item.labelKey)}
      onPress={() => onNavigate(item.routeName)}
      style={({ pressed }) => [styles.tile, pressed ? styles.pressed : null]}
    >
      <View style={styles.tileIcon}>
        <MaterialCommunityIcons name={item.icon as IconName} size={22} color={HB_COLORS.action} />
      </View>
      <Text style={styles.tileLabel} numberOfLines={2} maxFontSizeMultiplier={1.4}>
        {t(item.labelKey)}
      </Text>
    </Pressable>
  );

  const renderChip = (key: string, icon: IconName, label: string, onPress: () => void) => (
    <Pressable
      key={key}
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={onPress}
      style={({ pressed }) => [styles.chip, pressed ? styles.pressed : null]}
    >
      <MaterialCommunityIcons name={icon} size={16} color={HB_COLORS.textSecondary} />
      <Text style={styles.chipText} numberOfLines={1} maxFontSizeMultiplier={1.4}>
        {label}
      </Text>
    </Pressable>
  );

  return (
    <View style={StyleSheet.absoluteFill} pointerEvents="box-none">
      <Pressable
        style={styles.scrim}
        accessibilityRole="button"
        accessibilityLabel={t("common:floatingLanguage.close")}
        onPress={onClose}
      />
      <View style={[styles.panel, { maxHeight }]} accessibilityViewIsModal>
        <View style={styles.header}>
          <Text style={styles.title} maxFontSizeMultiplier={1.4}>
            {t("common:floatingLanguage.quickJumpTitle")}
          </Text>
          <View style={styles.languagePill} accessibilityRole="radiogroup">
            {(["zh", "en"] as const).map((value) => {
              const selected = language === value;
              return (
                <Pressable
                  key={value}
                  accessibilityRole="radio"
                  accessibilityState={{ selected }}
                  accessibilityLabel={t(`common:language.${value}`)}
                  onPress={() => onChangeLanguage(value)}
                  style={[styles.languageOption, selected ? styles.languageOptionSelected : null]}
                >
                  <Text
                    style={[styles.languageText, selected ? styles.languageTextSelected : null]}
                    maxFontSizeMultiplier={1.3}
                  >
                    {t(value === "zh" ? "common:floatingLanguage.shortZh" : "common:floatingLanguage.shortEn")}
                  </Text>
                </Pressable>
              );
            })}
          </View>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("common:floatingLanguage.hide")}
            onPress={onHideButton}
            hitSlop={8}
            style={({ pressed }) => [styles.iconButton, pressed ? styles.pressed : null]}
          >
            <MaterialCommunityIcons name="eye-off-outline" size={21} color={HB_COLORS.textSecondary} />
          </Pressable>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("common:floatingLanguage.close")}
            onPress={onClose}
            hitSlop={8}
            style={({ pressed }) => [styles.iconButton, pressed ? styles.pressed : null]}
          >
            <MaterialCommunityIcons name="close" size={22} color={HB_COLORS.textSecondary} />
          </Pressable>
        </View>

        <ScrollView
          style={styles.scroll}
          contentContainerStyle={styles.scrollContent}
          keyboardShouldPersistTaps="handled"
        >
          <View style={styles.chipRow}>
            {model.canOpenWorkbench
              ? renderChip("workbench", "view-dashboard-outline", t("common:floatingLanguage.workbench"), () => onNavigate("workbench"))
              : null}
            {model.canOpenSettings
              ? renderChip("settings", "cog-outline", t("common:floatingLanguage.settings"), () => onNavigate("settings"))
              : null}
          </View>

          {model.recent.length > 0 ? (
            <>
              <Text style={styles.sectionTitle} maxFontSizeMultiplier={1.4}>
                {t("common:floatingLanguage.recent")}
              </Text>
              <View style={styles.chipRow}>
                {model.recent.map((item) =>
                  renderChip(`recent-${item.routeName}`, item.icon as IconName, t(item.labelKey), () => onNavigate(item.routeName))
                )}
              </View>
            </>
          ) : null}

          {menuUnavailable ? (
            <Text style={styles.notice}>{t("common:floatingLanguage.menuUnavailable")}</Text>
          ) : null}

          {model.sections.map((section) => (
            <View key={section.key}>
              <Text style={styles.sectionTitle} maxFontSizeMultiplier={1.4}>
                {t(section.titleKey)}
              </Text>
              <View style={styles.tileGrid}>{section.items.map(renderTile)}</View>
            </View>
          ))}
        </ScrollView>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  scrim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: "rgba(16, 24, 40, 0.45)",
  },
  panel: {
    position: "absolute",
    left: 10,
    right: 10,
    bottom: 10,
    backgroundColor: HB_COLORS.white,
    borderRadius: 20,
    paddingTop: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    shadowColor: "#101828",
    shadowOpacity: 0.28,
    shadowRadius: 20,
    shadowOffset: { width: 0, height: 12 },
    elevation: 12,
  },
  header: {
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingBottom: HB_SPACING.xs,
  },
  title: {
    flex: 1,
    fontSize: 16,
    fontWeight: "800",
    color: HB_COLORS.textPrimary,
  },
  languagePill: {
    flexDirection: "row",
    backgroundColor: HB_COLORS.surfaceMuted,
    borderRadius: 999,
    padding: 3,
    gap: 2,
  },
  languageOption: {
    minWidth: 40,
    minHeight: 30,
    paddingHorizontal: 10,
    borderRadius: 999,
    alignItems: "center",
    justifyContent: "center",
  },
  languageOptionSelected: {
    backgroundColor: HB_COLORS.brand,
  },
  languageText: {
    fontSize: 13,
    fontWeight: "700",
    color: HB_COLORS.textSecondary,
  },
  languageTextSelected: {
    color: HB_COLORS.white,
  },
  iconButton: {
    width: 36,
    height: 36,
    borderRadius: 18,
    alignItems: "center",
    justifyContent: "center",
  },
  scroll: {
    flexGrow: 0,
  },
  scrollContent: {
    paddingBottom: HB_SPACING.sm,
    gap: HB_SPACING.xs,
  },
  sectionTitle: {
    marginTop: 4,
    fontSize: 12.5,
    fontWeight: "700",
    color: HB_COLORS.textSecondary,
  },
  chipRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 6,
  },
  chip: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    minHeight: 34,
    maxWidth: "100%",
    paddingHorizontal: 11,
    borderRadius: 999,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  chipText: {
    flexShrink: 1,
    fontSize: 13,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  notice: {
    fontSize: 13,
    color: HB_COLORS.warning,
  },
  tileGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
  },
  tile: {
    width: "25%",
    alignItems: "center",
    paddingVertical: 6,
    paddingHorizontal: 2,
    gap: 5,
    borderRadius: HB_RADIUS.control,
  },
  tileIcon: {
    width: 42,
    height: 42,
    borderRadius: 12,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#EFF6FF",
  },
  tileLabel: {
    fontSize: 12,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    textAlign: "center",
  },
  pressed: {
    opacity: 0.6,
  },
});
