import { StyleSheet, View } from "react-native";
import { Switch, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { InfoCard, NoticeBox, PeopleTag } from "@/modules/people";
import type { MobileMenuSection } from "@/modules/identity-admin/user-authorization-logic";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import type { AccessMenuItem, AccessMenuModel } from "./access-page-logic";
import { AccessRowFrame, accessSectionStyles } from "./access-section-ui";

const MENU_SECTION_ORDER: MobileMenuSection[] = ["bottom", "store", "operations", "reports"];

interface AccessMenuSectionProps {
  model: AccessMenuModel;
  /** 整个分段只读（保存中 / 目标是管理员默认全权限 / 没有编辑资格）。 */
  readOnly: boolean;
  onToggle: (item: AccessMenuItem, visible: boolean) => void;
}

/**
 * 菜单分段（仅管理员，原管理员授权页的「移动端菜单」）：
 * 每个菜单项的开关改的就是直接权限草稿里对应的权限码，所以和「直接权限」共用一个保存步骤。
 */
export function AccessMenuSection({ model, readOnly, onToggle }: AccessMenuSectionProps) {
  const { t } = useAppTranslation(["peopleAccess", "common"]);

  if (!model.items.length) {
    return <EmptyState title={t("menu.emptyTitle")} description={t("menu.emptyDescription")} />;
  }

  const subtitleOf = (item: AccessMenuItem) =>
    item.requireAdmin
      ? t("menu.adminOnly")
      : item.fixed
        ? t("menu.fixed")
        : item.inherited
          ? t("menu.inherited")
          : item.direct
            ? t("menu.direct")
            : item.visible
              ? t("menu.visible")
              : t("menu.hidden");

  return (
    <View style={accessSectionStyles.stack}>
      <NoticeBox tone="blue">{t("menu.hint")}</NoticeBox>
      {MENU_SECTION_ORDER.map((section) => {
        const items = model.sections[section];
        if (!items.length) return null;
        return (
          <View key={section} style={styles.group}>
            <Text style={styles.groupTitle}>{t(`menu.sections.${section}`)}</Text>
            <InfoCard>
              {items.map((item, index) => (
                <AccessRowFrame
                  key={item.key}
                  first={index === 0}
                  testID={`access-menu-row-${item.key}`}
                >
                  <View style={styles.text}>
                    <Text style={styles.name}>{item.title}</Text>
                    <PeopleTag
                      label={subtitleOf(item)}
                      tone={item.direct && !item.inherited ? "blue" : "gray"}
                    />
                  </View>
                  <Switch
                    value={item.visible}
                    disabled={readOnly || item.locked}
                    accessibilityLabel={item.title}
                    accessibilityHint={subtitleOf(item)}
                    onValueChange={(checked) => onToggle(item, checked)}
                  />
                </AccessRowFrame>
              ))}
            </InfoCard>
          </View>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  group: { gap: 6 },
  groupTitle: {
    color: HB_COLORS.textSecondary,
    fontSize: 12.5,
    fontWeight: "700",
    paddingHorizontal: 4,
  },
  text: { flex: 1, gap: 4, minWidth: 0 },
  name: { color: HB_COLORS.textPrimary, fontSize: 14.5, fontWeight: "600" },
});
