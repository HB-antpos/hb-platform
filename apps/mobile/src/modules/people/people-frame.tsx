import type { ReactNode } from "react";
import { Pressable, ScrollView, StyleSheet, TextInput, View, type StyleProp, type ViewStyle } from "react-native";
import { ActivityIndicator, Icon, IconButton, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import type { ListRowPosition } from "./list-position";

/**
 * 「人员管理」列表页（人员 / 角色 / 权限三个页签）共用的页面骨架与小部件。
 * 视觉来自设计稿 A1 / A6：白色标题栏（返回 + 标题 + 副标题 + 右上角图标）、页签条、圆角分段卡片、搜索框、下拉芯片。
 * 颜色只用 HB_COLORS / PEOPLE_TONES，不手写新的十六进制（少量设计稿里的浅底色已折进 people/ui.tsx）。
 */

/** Hub 传给各页签面板的外壳信息：页签条（只有一个页签时为 null）与返回动作。 */
export interface PeopleHubChrome {
  tabBar: ReactNode;
  onBack: () => void;
}

export function PeopleScreenFrame({
  title,
  subtitle,
  chrome,
  right,
  children,
}: {
  title: string;
  subtitle?: string | null;
  chrome: PeopleHubChrome;
  /** 标题栏右上角的图标按钮（批量打印等）。 */
  right?: ReactNode;
  children: ReactNode;
}) {
  const { t } = useAppTranslation("common");
  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
      <View style={styles.header}>
        <IconButton icon="chevron-left" accessibilityLabel={t("actions.back")} onPress={chrome.onBack} />
        <View style={styles.headerText}>
          <Text style={styles.title} numberOfLines={1} accessibilityRole="header">{title}</Text>
          {subtitle ? <Text style={styles.subtitle} numberOfLines={1}>{subtitle}</Text> : null}
        </View>
        {right ? <View style={styles.headerRight}>{right}</View> : null}
      </View>
      {chrome.tabBar ? <View style={styles.tabBar}>{chrome.tabBar}</View> : null}
      <View style={styles.body}>{children}</View>
    </SafeAreaView>
  );
}

/** 标题栏右上角的图标按钮（44pt 点击区）。 */
export function HeaderIconButton({
  icon,
  label,
  onPress,
  selected = false,
}: {
  icon: string;
  label: string;
  onPress: () => void;
  selected?: boolean;
}) {
  return (
    <IconButton
      icon={icon}
      accessibilityLabel={label}
      accessibilityState={{ selected }}
      onPress={onPress}
      iconColor={selected ? HB_COLORS.action : HB_COLORS.textPrimary}
      style={selected ? styles.headerIconSelected : undefined}
    />
  );
}

/** 搜索框：圆角、左侧放大镜、有内容时右侧出现清除键。 */
export function PeopleSearchBox({
  value,
  onChangeText,
  placeholder,
  style,
}: {
  value: string;
  onChangeText: (value: string) => void;
  placeholder: string;
  style?: StyleProp<ViewStyle>;
}) {
  const { t } = useAppTranslation("common");
  return (
    <View style={[styles.search, style]}>
      <Icon source="magnify" size={19} color={HB_COLORS.textSecondary} />
      <TextInput
        value={value}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor="#98A2B3"
        style={styles.searchInput}
        accessibilityLabel={placeholder}
        autoCapitalize="none"
        autoCorrect={false}
        returnKeyType="search"
      />
      {value ? (
        <Pressable accessibilityRole="button" accessibilityLabel={t("actions.clear")} onPress={() => onChangeText("")} hitSlop={10}>
          <Icon source="close-circle" size={18} color="#98A2B3" />
        </Pressable>
      ) : null}
    </View>
  );
}

/** 下拉芯片：点开弹层选值（分店 / 角色）；有值（非「全部」）时用蓝色描边。 */
export function DropdownChip({
  label,
  icon,
  active = false,
  size = "small",
  onPress,
  accessibilityLabel,
}: {
  label: string;
  icon?: string;
  active?: boolean;
  size?: "small" | "large";
  onPress: () => void;
  accessibilityLabel?: string;
}) {
  const color = active ? HB_COLORS.action : HB_COLORS.textPrimary;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? label}
      onPress={onPress}
      style={[styles.dropdown, size === "large" && styles.dropdownLarge, active && styles.dropdownActive]}
    >
      {icon ? <Icon source={icon} size={17} color={color} /> : null}
      <Text style={[styles.dropdownText, { color }, active && styles.dropdownTextActive]} numberOfLines={1}>{label}</Text>
      <Icon source="chevron-down" size={17} color={color} />
    </Pressable>
  );
}

/** 带计数的筛选芯片（可单独放进自己的横向滚动容器里与下拉芯片混排）。 */
export function CountChip({
  label,
  count,
  selected,
  onPress,
}: {
  label: string;
  count?: number | null;
  selected: boolean;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.countChip, selected && styles.countChipSelected]}
    >
      <Text style={[styles.countChipText, selected && styles.countChipTextSelected]}>{label}</Text>
      {typeof count === "number" ? <Text style={[styles.countChipCount, selected && styles.countChipTextSelected]}>{count}</Text> : null}
    </Pressable>
  );
}

/** 横向滚动的芯片行容器（放不下时滑动，不换行）。 */
export function ChipScroller({ children }: { children: ReactNode }) {
  return (
    <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chipScroller} keyboardShouldPersistTaps="handled">
      {children}
    </ScrollView>
  );
}

/** 弹层里允许换行的单选芯片组（分店 / 角色选项多，不能横向滚动）。 */
export function OptionChipWrap<T extends string>({
  items,
  value,
  onChange,
}: {
  items: readonly { key: T; label: string }[];
  value: T;
  onChange: (key: T) => void;
}) {
  return (
    <View style={styles.optionWrap}>
      {items.map((item) => {
        const selected = item.key === value;
        return (
          <Pressable
            key={item.key}
            accessibilityRole="radio"
            accessibilityState={{ selected, checked: selected }}
            onPress={() => onChange(item.key)}
            style={[styles.countChip, selected && styles.countChipSelected]}
          >
            <Text style={[styles.countChipText, selected && styles.countChipTextSelected]} numberOfLines={1}>{item.label}</Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/**
 * 同一组连续的行共用一张圆角卡片：首行上圆角、末行下圆角、两侧有边线。
 * FlatList 逐行渲染，没法给整组套一个容器，所以每一行自己画对应的那一段卡片边框。
 */
export function ListCardSegment({ position, children }: { position: ListRowPosition; children: ReactNode }) {
  const top = position === "first" || position === "only";
  const bottom = position === "last" || position === "only";
  return (
    <View
      style={[
        styles.segment,
        top && styles.segmentTop,
        bottom && styles.segmentBottom,
      ]}
    >
      {children}
    </View>
  );
}

/** 列表末尾：正在加载下一页的转圈，或「已显示全部」。 */
export function LoadMoreFooter({ loading, endLabel }: { loading: boolean; endLabel?: string | null }) {
  if (loading) {
    return (
      <View style={styles.footer}>
        <ActivityIndicator size="small" />
      </View>
    );
  }
  return endLabel ? (
    <View style={styles.footer}>
      <Text style={styles.footerText}>{endLabel}</Text>
    </View>
  ) : null;
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  header: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    flexDirection: "row",
    minHeight: 60,
    paddingRight: HB_SPACING.xs,
  },
  headerText: { flex: 1, minWidth: 0, paddingVertical: 6 },
  title: { color: HB_COLORS.textPrimary, fontSize: 19, fontWeight: "800", lineHeight: 25 },
  subtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, lineHeight: 17 },
  headerRight: { alignItems: "center", flexDirection: "row" },
  headerIconSelected: { backgroundColor: "#EFF6FF" },
  tabBar: { backgroundColor: HB_COLORS.white, paddingBottom: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md },
  body: { flex: 1 },
  search: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 10,
    borderWidth: 1,
    flexDirection: "row",
    gap: 8,
    height: 40,
    paddingHorizontal: 12,
  },
  searchInput: { color: HB_COLORS.textPrimary, flex: 1, fontSize: 14, minWidth: 0, paddingVertical: 0 },
  dropdown: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 999,
    borderWidth: 1,
    flexDirection: "row",
    gap: 3,
    maxWidth: 220,
    minHeight: 32,
    paddingHorizontal: 11,
  },
  dropdownLarge: { borderRadius: 10, height: 40, paddingHorizontal: 12 },
  dropdownActive: { backgroundColor: "#EFF6FF", borderColor: "#B2CCFF" },
  dropdownText: { flexShrink: 1, fontSize: 12.5 },
  dropdownTextActive: { fontWeight: "700" },
  countChip: {
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 999,
    borderWidth: 1,
    flexDirection: "row",
    gap: 4,
    minHeight: 32,
    paddingHorizontal: 11,
  },
  countChipSelected: { backgroundColor: "#EFF6FF", borderColor: "#B2CCFF" },
  countChipText: { color: HB_COLORS.textPrimary, fontSize: 12.5 },
  countChipTextSelected: { color: HB_COLORS.action, fontWeight: "700" },
  countChipCount: { color: "#667085", fontSize: 11, fontWeight: "600" },
  chipScroller: { alignItems: "center", gap: 6, paddingVertical: 2 },
  optionWrap: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  segment: {
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderLeftWidth: 1,
    borderRightWidth: 1,
    overflow: "hidden",
  },
  segmentTop: { borderTopLeftRadius: HB_RADIUS.surface, borderTopRightRadius: HB_RADIUS.surface, borderTopWidth: 1 },
  segmentBottom: { borderBottomLeftRadius: HB_RADIUS.surface, borderBottomRightRadius: HB_RADIUS.surface, borderBottomWidth: 1 },
  footer: { alignItems: "center", paddingVertical: HB_SPACING.md },
  footerText: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
});
