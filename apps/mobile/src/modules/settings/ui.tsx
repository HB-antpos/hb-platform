// 设置页首屏的新版 UI 零件：名片、分区、单行条目、状态点。
// 约定与设置页一致：不限行数——英文更长，宁可换行也不能被省略号截断（见 settings-screen-contract 测试）。
import type { ReactNode, RefObject } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Icon, Surface, Text } from "react-native-paper";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

export type RowTone = "success" | "warning" | "danger" | "neutral";

const DOT_COLOR: Record<RowTone, string> = {
  success: "#17B26A",
  warning: "#F79009",
  danger: "#F04438",
  neutral: "#98A2B3",
};

/** 行尾状态：彩色小点 + 文字（取代原来的胶囊，省宽度，一眼能看出红 / 黄 / 绿）。 */
export function StatusDotLabel({ label, tone = "neutral" }: { label: string; tone?: RowTone }) {
  return (
    <View style={styles.statusWrap}>
      <View style={[styles.statusDot, { backgroundColor: DOT_COLOR[tone] }]} />
      <Text variant="labelMedium" style={styles.statusText}>
        {label}
      </Text>
    </View>
  );
}

/** 分区标题右侧的汇总胶囊：「全部正常」/「N 项需处理」。 */
export function SummaryPill({ label, tone }: { label: string; tone: "success" | "warning" }) {
  const ok = tone === "success";
  return (
    <View style={[styles.pill, ok ? styles.pillOk : styles.pillWarn]} accessibilityLiveRegion="polite">
      <Icon source={ok ? "check-circle-outline" : "alert-circle-outline"} size={13} color={ok ? HB_COLORS.success : HB_COLORS.warning} />
      <Text variant="labelSmall" style={[styles.pillText, { color: ok ? HB_COLORS.success : HB_COLORS.warning }]}>
        {label}
      </Text>
    </View>
  );
}

interface SectionBlockProps {
  /** 不给标题（也没有右侧内容）就不画标题行，如首页底部那张「更多设置」入口卡。 */
  title?: string;
  /** 标题右侧（如汇总胶囊）。 */
  right?: ReactNode;
  testID?: string;
  children: ReactNode;
}

/** 分区：小标题 + 一张卡。 */
export function SectionBlock({ title, right, testID, children }: SectionBlockProps) {
  return (
    <View style={styles.section} testID={testID}>
      {title || right ? (
        <View style={styles.sectionHeader}>
          <Text variant="labelLarge" style={styles.sectionTitle}>
            {title}
          </Text>
          {right}
        </View>
      ) : null}
      <Surface style={styles.card} elevation={0}>
        {children}
      </Surface>
    </View>
  );
}

interface SettingsRowProps {
  icon?: string;
  title: string;
  subtitle?: string;
  /** 行尾内容（状态点、开关、分段按钮等）。 */
  right?: ReactNode;
  onPress?: () => void;
  /** 不是第一行时画一条分隔线。 */
  divider?: boolean;
  testID?: string;
  actionRef?: RefObject<View | null>;
  accessibilityLabel?: string;
  disabled?: boolean;
  /** 可点的行默认在行尾画右箭头；折叠行等自带指示图标的行传 false。 */
  showChevron?: boolean;
}

/** 单行条目：图标 + 标题 / 副标题 + 行尾内容 + （可点时）右箭头。最小高度 48。 */
export function SettingsRow({
  icon,
  title,
  subtitle,
  right,
  onPress,
  divider = false,
  testID,
  actionRef,
  accessibilityLabel,
  disabled,
  showChevron = Boolean(onPress),
}: SettingsRowProps) {
  const content = (
    <View style={[styles.row, divider && styles.rowDivider]} testID={onPress ? undefined : testID}>
      {icon ? (
        <View style={styles.rowIcon}>
          <Icon source={icon} size={19} color={HB_COLORS.action} />
        </View>
      ) : null}
      <View style={styles.rowBody}>
        <Text variant="bodyMedium" style={styles.rowTitle}>
          {title}
        </Text>
        {subtitle ? (
          <Text variant="bodySmall" style={styles.rowSubtitle}>
            {subtitle}
          </Text>
        ) : null}
      </View>
      {right || showChevron ? (
        <View style={styles.rowEnd}>
          {right}
          {showChevron ? <Icon source="chevron-right" size={22} color={HB_COLORS.textSecondary} /> : null}
        </View>
      ) : null}
    </View>
  );
  if (!onPress) return content;
  return (
    <Pressable
      ref={actionRef}
      onPress={onPress}
      disabled={disabled}
      testID={testID}
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? [title, subtitle].filter(Boolean).join(". ")}
      accessibilityState={{ disabled: Boolean(disabled) }}
      style={({ pressed }) => pressed && styles.rowPressed}
    >
      {content}
    </Pressable>
  );
}

interface ValueRowProps {
  label: string;
  value: string;
  divider?: boolean;
}

/** 键值行：左标签、右值（可长按复制，截图报问题时用）。 */
export function ValueRow({ label, value, divider = false }: ValueRowProps) {
  return (
    <View style={[styles.row, divider && styles.rowDivider]} accessible accessibilityLabel={`${label}: ${value}`}>
      <Text variant="bodyMedium" style={styles.valueLabel}>
        {label}
      </Text>
      <Text variant="bodyMedium" selectable style={styles.valueText}>
        {value}
      </Text>
    </View>
  );
}

interface IdentityCardProps {
  /** 头像里的字（账号名片）；设备名片用 avatarIcon。 */
  initial?: string;
  avatarIcon?: string;
  name: string;
  roleLabel?: string | null;
  roleExtra?: number;
  /** 第二行：账号 · 分店。 */
  meta?: string;
  /** 码值一行（扫码枪扫不上时可手输）。 */
  codeLine?: string;
  /** 「个人资料 ›」；不给就不显示链接。 */
  profileLabel?: string;
  onPressProfile?: () => void;
  /** 右侧：个人码二维码方块。 */
  trailing?: ReactNode;
  testID?: string;
}

/** 首屏名片：左边头像 / 姓名 / 角色 / 账号分店 / 码值 / 个人资料链接，右边个人码二维码。 */
export function IdentityCard({
  initial,
  avatarIcon,
  name,
  roleLabel,
  roleExtra = 0,
  meta,
  codeLine,
  profileLabel,
  onPressProfile,
  trailing,
  testID,
}: IdentityCardProps) {
  return (
    <Surface style={styles.identity} elevation={0} testID={testID}>
      <View style={styles.identityInfo}>
        <View style={styles.identityWho}>
          <View style={styles.avatar}>
            {avatarIcon ? (
              <Icon source={avatarIcon} size={22} color={HB_COLORS.textSecondary} />
            ) : (
              <Text style={styles.avatarText}>{initial}</Text>
            )}
          </View>
          <View style={styles.identityNameBlock}>
            <Text variant="titleMedium" style={styles.identityName}>
              {name}
            </Text>
            {roleLabel ? (
              <View style={styles.roleRow}>
                <View style={styles.roleTag}>
                  <Text style={styles.roleTagText}>{roleLabel}</Text>
                </View>
                {roleExtra > 0 ? <Text style={styles.roleExtra}>+{roleExtra}</Text> : null}
              </View>
            ) : null}
          </View>
        </View>
        {meta ? (
          <Text variant="bodySmall" style={styles.identityMeta}>
            {meta}
          </Text>
        ) : null}
        {codeLine ? (
          <Text variant="labelSmall" selectable style={styles.identityCode}>
            {codeLine}
          </Text>
        ) : null}
        {profileLabel && onPressProfile ? (
          <Pressable
            onPress={onPressProfile}
            accessibilityRole="button"
            accessibilityLabel={profileLabel}
            style={styles.profileLink}
            hitSlop={8}
          >
            <Text style={styles.profileLinkText}>{profileLabel}</Text>
            <Icon source="chevron-right" size={18} color={HB_COLORS.action} />
          </Pressable>
        ) : null}
      </View>
      {trailing}
    </Surface>
  );
}

const styles = StyleSheet.create({
  statusWrap: { flexDirection: "row", alignItems: "center", gap: 6, flexShrink: 1 },
  statusDot: { width: 8, height: 8, borderRadius: 4 },
  statusText: { color: HB_COLORS.textSecondary, fontWeight: "600", flexShrink: 1 },
  pill: {
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingHorizontal: 8,
    paddingVertical: 2,
    borderRadius: 999,
  },
  pillOk: { backgroundColor: "#ECFDF3" },
  pillWarn: { backgroundColor: "#FFFAEB" },
  pillText: { fontWeight: "800" },
  section: { gap: HB_SPACING.xs },
  sectionHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.xxs,
  },
  sectionTitle: { color: HB_COLORS.textSecondary, fontWeight: "700", letterSpacing: 0.2, flexShrink: 1 },
  card: {
    overflow: "hidden",
    borderRadius: HB_RADIUS.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surface,
  },
  row: {
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: 6,
  },
  rowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  rowPressed: { backgroundColor: HB_COLORS.surfaceMuted },
  rowIcon: {
    width: 32,
    height: 32,
    borderRadius: 9,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#EFF6FF",
  },
  rowBody: { flex: 1, minWidth: 0 },
  rowTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  rowSubtitle: { color: HB_COLORS.textSecondary },
  valueLabel: { flex: 1, color: HB_COLORS.textSecondary },
  valueText: { flexShrink: 1, textAlign: "right", color: HB_COLORS.textPrimary, fontWeight: "700" },
  rowEnd: { flexDirection: "row", alignItems: "center", gap: 2, flexShrink: 1, maxWidth: "55%" },
  identity: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    paddingHorizontal: 12,
    paddingVertical: 10,
    borderRadius: 14,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surface,
  },
  identityInfo: { flex: 1, minWidth: 0, gap: 5 },
  identityWho: { flexDirection: "row", alignItems: "center", gap: 10 },
  avatar: {
    width: 46,
    height: 46,
    borderRadius: 23,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "#DBEAFE",
  },
  avatarText: { fontSize: 18, fontWeight: "800", color: HB_COLORS.action },
  identityNameBlock: { flex: 1, minWidth: 0, gap: 3 },
  identityName: { color: HB_COLORS.textPrimary, fontWeight: "800" },
  roleRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  roleTag: { alignSelf: "flex-start", paddingHorizontal: 7, paddingVertical: 2, borderRadius: 6, backgroundColor: "#EFF6FF" },
  roleTagText: { fontSize: 11.5, fontWeight: "700", color: HB_COLORS.action },
  roleExtra: { fontSize: 11.5, fontWeight: "700", color: HB_COLORS.textSecondary },
  identityMeta: { color: HB_COLORS.textSecondary },
  identityCode: { color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"], letterSpacing: 0.6 },
  profileLink: { flexDirection: "row", alignItems: "center", alignSelf: "flex-start", minHeight: 28 },
  profileLinkText: { fontSize: 13, fontWeight: "800", color: HB_COLORS.action },
});
