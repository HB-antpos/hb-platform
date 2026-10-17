import type { ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Icon, IconButton, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { InfoCard, PEOPLE_TONES, PersonAvatar, type PeopleTone } from "@/modules/people";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

/** 详情页顶部栏：返回 + 标题（可带副标题）+ 右侧图标操作（✎ / ⋮）。 */
export function DetailAppBar({
  title,
  subtitle,
  backLabel,
  onBack,
  actions = [],
}: {
  title: string;
  subtitle?: string;
  backLabel: string;
  onBack: () => void;
  actions?: { key: string; icon: string; label: string; onPress: () => void; testID?: string }[];
}) {
  return (
    <View style={styles.appBar}>
      <IconButton icon="arrow-left" accessibilityLabel={backLabel} onPress={onBack} />
      <View style={styles.appBarTitleWrap}>
        <Text accessibilityRole="header" style={styles.appBarTitle} numberOfLines={1}>{title}</Text>
        {subtitle ? <Text style={styles.appBarSubtitle} numberOfLines={1}>{subtitle}</Text> : null}
      </View>
      {actions.map((action) => (
        <IconButton
          key={action.key}
          icon={action.icon}
          accessibilityLabel={action.label}
          onPress={action.onPress}
          iconColor={HB_COLORS.textPrimary}
          testID={action.testID}
        />
      ))}
    </View>
  );
}

/** 头部快捷操作：图标方块 + 一行文字；危险操作（停用）整体偏红。 */
export function QuickActionTile({
  icon,
  label,
  tone = "blue",
  disabled = false,
  loading = false,
  onPress,
  testID,
}: {
  icon: string;
  label: string;
  tone?: PeopleTone;
  disabled?: boolean;
  loading?: boolean;
  onPress: () => void;
  testID?: string;
}) {
  const color = PEOPLE_TONES[tone];
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ disabled }}
      disabled={disabled || loading}
      onPress={onPress}
      style={[styles.quickTile, disabled && styles.quickTileDisabled]}
      testID={testID}
    >
      <View style={[styles.quickIcon, { backgroundColor: color.bg }]}>
        {loading ? <ActivityIndicator size={18} color={color.fg} /> : <Icon source={icon} size={22} color={color.fg} />}
      </View>
      <Text style={[styles.quickLabel, tone === "red" && { color: color.fg }]} numberOfLines={1}>{label}</Text>
    </Pressable>
  );
}

/** 头部卡片：头像 + 名字（昵称小字）+ 一行元信息 + 标签；children 放快捷操作行。 */
export function PersonHeaderCard({
  initials,
  seed,
  active,
  name,
  nickname,
  meta,
  tags,
  children,
}: {
  initials: string;
  seed: string;
  active: boolean;
  name: string;
  nickname: string | null;
  meta: string;
  tags: ReactNode;
  children?: ReactNode;
}) {
  return (
    <InfoCard>
      <View style={styles.heroRow}>
        <PersonAvatar initials={initials} seed={seed} size={60} disabled={!active} presence={active ? "on" : "off"} />
        <View style={styles.heroCopy}>
          <View style={styles.heroNameRow}>
            <Text style={styles.heroName} numberOfLines={2}>{name}</Text>
            {nickname ? <Text style={styles.heroNickname} numberOfLines={1}>{`「${nickname}」`}</Text> : null}
          </View>
          {meta ? <Text style={styles.heroMeta} numberOfLines={2}>{meta}</Text> : null}
          <View style={styles.heroTags}>{tags}</View>
        </View>
      </View>
      {children}
    </InfoCard>
  );
}

/** 整页状态（加载 / 缺参数 / 失败）：保留顶部返回，避免用户困在空白页。 */
export function DetailStateScreen({
  appBar,
  children,
}: {
  appBar: ReactNode;
  children: ReactNode;
}) {
  return (
    <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
      {appBar}
      <View style={styles.stateBody}>{children}</View>
    </SafeAreaView>
  );
}

export const detailStyles = StyleSheet.create({
  content: { gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xl, paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.xs },
  rowValueText: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 14 },
  rowMuted: { color: HB_COLORS.textSecondary, fontSize: 12.5 },
  mono: { fontFamily: "Menlo", fontVariant: ["tabular-nums"] },
  dangerButtonText: { color: HB_COLORS.danger, fontSize: 15, fontWeight: "800" },
});

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  appBar: { alignItems: "center", flexDirection: "row", minHeight: 56, paddingHorizontal: HB_SPACING.xxs },
  appBarTitleWrap: { flex: 1, minWidth: 0 },
  appBarTitle: { color: HB_COLORS.textPrimary, fontSize: 19, fontWeight: "800" },
  appBarSubtitle: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 1 },
  quickTile: { alignItems: "center", flex: 1, gap: 5, minHeight: 64, paddingVertical: 2 },
  quickTileDisabled: { opacity: 0.45 },
  quickIcon: { alignItems: "center", borderRadius: 14, height: 44, justifyContent: "center", width: 44 },
  quickLabel: { color: HB_COLORS.textPrimary, fontSize: 12, fontWeight: "700" },
  heroRow: { alignItems: "center", flexDirection: "row", gap: 12, paddingBottom: 10, paddingHorizontal: 14, paddingTop: 14 },
  heroCopy: { flex: 1, minWidth: 0 },
  heroNameRow: { alignItems: "baseline", flexDirection: "row", flexWrap: "wrap", gap: 6 },
  heroName: { color: HB_COLORS.textPrimary, flexShrink: 1, fontSize: 19, fontWeight: "800" },
  heroNickname: { color: "#667085", fontSize: 13, fontWeight: "600" },
  heroMeta: { color: HB_COLORS.textSecondary, fontSize: 12.5, marginTop: 2 },
  heroTags: { flexDirection: "row", flexWrap: "wrap", gap: 4, marginTop: 6 },
  stateBody: { flex: 1, justifyContent: "center" },
});
