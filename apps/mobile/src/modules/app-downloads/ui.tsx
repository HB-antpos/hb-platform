import { useEffect, useRef, useState, type ReactNode } from "react";
import {
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  TextInput as NativeTextInput,
  View,
  type KeyboardTypeOptions,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import { Button, Icon, Switch, Text } from "react-native-paper";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";

// 本页改版专用的界面基础件：只用 HB token，不改全局主题。
const SUCCESS_SOFT = "#ECFDF3";
const BRAND_SOFT = "#EEF4FF";
const SELECTED_SOFT = "#F5F8FF";
const DOT_SUCCESS = "#12B76A";
const ICON_MUTED = "#667085";

export type Tone = "success" | "neutral" | "warning" | "danger";

export interface SegmentOption<T extends string> {
  value: T;
  label: string;
  /** 可选状态点：线路页签、终端切换用它标出已激活 / 待处理 / 未启用。 */
  tone?: Tone | null;
}

/** 灰色轨道 + 白色选中块的分段控件；strong 用于页面级切换，soft 用于卡片内。 */
export function SegmentedControl<T extends string>({
  options,
  value,
  onChange,
  disabled,
  tone = "soft",
  style,
}: {
  options: SegmentOption<T>[];
  value: T;
  onChange: (value: T) => void;
  disabled?: boolean;
  tone?: "strong" | "soft";
  style?: StyleProp<ViewStyle>;
}) {
  return (
    <View
      accessibilityRole="radiogroup"
      style={[
        styles.segmentTrack,
        tone === "strong" ? styles.segmentTrackStrong : styles.segmentTrackSoft,
        style,
      ]}
    >
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <Pressable
            key={option.value}
            accessibilityRole="radio"
            accessibilityState={{ checked: selected, disabled }}
            disabled={disabled}
            onPress={() => {
              if (!selected) onChange(option.value);
            }}
            style={[styles.segment, selected && styles.segmentSelected]}
          >
            <Text
              numberOfLines={1}
              style={[
                styles.segmentLabel,
                selected && styles.segmentLabelSelected,
              ]}
            >
              {option.label}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

/** 下划线标签页：用于「原生安装包 / OTA 热更新」这类同级内容切换。 */
export function UnderlineTabs<T extends string>({
  options,
  value,
  onChange,
  disabled,
}: {
  options: SegmentOption<T>[];
  value: T;
  onChange: (value: T) => void;
  disabled?: boolean;
}) {
  return (
    <View accessibilityRole="tablist" style={styles.tabs}>
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <Pressable
            key={option.value}
            accessibilityRole="tab"
            accessibilityState={{ selected, disabled }}
            disabled={disabled}
            onPress={() => onChange(option.value)}
            style={[styles.tab, selected && styles.tabSelected]}
          >
            <View style={styles.tabInner}>
              {option.tone ? <StatusDot tone={option.tone} /> : null}
              <Text
                style={[styles.tabLabel, selected && styles.tabLabelSelected]}
              >
                {option.label}
              </Text>
            </View>
          </Pressable>
        );
      })}
    </View>
  );
}

/**
 * 终端切换：对应 Web 版本发布中心的左侧导航，手机上改成一行可横滑的胶囊，
 * 状态点取该终端最需要关注的线路状态（总览项不带点）。
 */
export function TerminalNav<T extends string>({
  options,
  value,
  onChange,
  disabled,
  accessibilityLabel,
}: {
  options: SegmentOption<T>[];
  value: T;
  onChange: (value: T) => void;
  disabled?: boolean;
  accessibilityLabel?: string;
}) {
  const scrollRef = useRef<ScrollView>(null);
  const offsetsRef = useRef(new Map<string, number>());
  // 选中项变化（含深链直接落到靠右的终端）时把它滚进可视区，左侧留一点余量提示还能往回滑。
  useEffect(() => {
    const x = offsetsRef.current.get(value);
    if (x !== undefined)
      scrollRef.current?.scrollTo({ x: Math.max(0, x - HB_SPACING.lg), animated: true });
  }, [value]);
  return (
    <ScrollView
      ref={scrollRef}
      horizontal
      showsHorizontalScrollIndicator={false}
      accessibilityRole="tablist"
      accessibilityLabel={accessibilityLabel}
      contentContainerStyle={styles.terminalNav}
    >
      {options.map((option) => {
        const selected = option.value === value;
        return (
          <Pressable
            key={option.value}
            onLayout={(event) => {
              const { x } = event.nativeEvent.layout;
              offsetsRef.current.set(option.value, x);
              // 首次布局时选中项已在右侧（深链进入）：不等用户切换就滚过去。
              if (selected)
                scrollRef.current?.scrollTo({
                  x: Math.max(0, x - HB_SPACING.lg),
                  animated: false,
                });
            }}
            accessibilityRole="tab"
            accessibilityState={{ selected, disabled }}
            disabled={disabled}
            onPress={() => {
              if (!selected) onChange(option.value);
            }}
            style={({ pressed }) => [
              styles.terminalPill,
              selected && styles.terminalPillSelected,
              pressed && !selected && styles.pressed,
            ]}
          >
            {option.tone ? <StatusDot tone={option.tone} /> : null}
            <Text
              numberOfLines={1}
              style={[
                styles.terminalLabel,
                selected && styles.terminalLabelSelected,
              ]}
            >
              {option.label}
            </Text>
          </Pressable>
        );
      })}
    </ScrollView>
  );
}

export function SectionHeader({
  title,
  meta,
  action,
}: {
  title: string;
  meta?: string;
  action?: ReactNode;
}) {
  return (
    <View style={styles.sectionHeader}>
      <Text accessibilityRole="header" style={styles.sectionTitle}>
        {title}
      </Text>
      {action ?? (meta ? <Text style={styles.caption}>{meta}</Text> : null)}
    </View>
  );
}

export function Panel({
  children,
  dashed,
  style,
}: {
  children: ReactNode;
  dashed?: boolean;
  style?: StyleProp<ViewStyle>;
}) {
  return (
    <View style={[styles.panel, dashed && styles.panelDashed, style]}>
      {children}
    </View>
  );
}

export function Pill({
  label,
  tone = "neutral",
}: {
  label: string;
  tone?: Tone;
}) {
  return (
    <View style={[styles.pill, pillTone[tone].box]}>
      <Text style={[styles.pillLabel, pillTone[tone].text]}>{label}</Text>
    </View>
  );
}

export function StatusDot({ tone }: { tone: Tone }) {
  return <View style={[styles.dot, { backgroundColor: dotTone[tone] }]} />;
}

export function IconBadge({
  icon,
  tone,
}: {
  icon: string;
  tone: "brand" | "success" | "neutral";
}) {
  const palette =
    tone === "brand"
      ? { bg: BRAND_SOFT, fg: HB_COLORS.action }
      : tone === "success"
        ? { bg: SUCCESS_SOFT, fg: HB_COLORS.success }
        : { bg: HB_COLORS.surfaceMuted, fg: HB_COLORS.textSecondary };
  return (
    <View style={[styles.iconBadge, { backgroundColor: palette.bg }]}>
      <Icon source={icon} size={22} color={palette.fg} />
    </View>
  );
}

export interface TileAction {
  icon: string;
  label: string;
  onPress: () => void;
  emphasis?: boolean;
  disabled?: boolean;
}

/** 卡片底部等宽操作格：图标在上、文字在下，一行放下四个操作。 */
export function ActionTiles({ items }: { items: TileAction[] }) {
  return (
    <View style={styles.tiles}>
      {items.map((item, index) => (
        <Pressable
          key={item.label}
          accessibilityRole="button"
          accessibilityLabel={item.label}
          accessibilityState={{ disabled: item.disabled }}
          disabled={item.disabled}
          onPress={item.onPress}
          style={({ pressed }) => [
            styles.tile,
            index > 0 && styles.tileDivider,
            pressed && styles.pressed,
          ]}
        >
          <Icon
            source={item.icon}
            size={20}
            color={item.disabled ? HB_COLORS.outline : HB_COLORS.action}
          />
          <Text
            numberOfLines={1}
            style={[
              styles.tileLabel,
              item.emphasis && styles.tileLabelEmphasis,
              item.disabled && styles.disabledText,
            ]}
          >
            {item.label}
          </Text>
        </Pressable>
      ))}
    </View>
  );
}

export function FooterLinks({
  items,
}: {
  items: { icon: string; label: string; onPress: () => void }[];
}) {
  if (items.length === 0) return null;
  return (
    <View style={styles.footerLinks}>
      {items.map((item, index) => (
        <Pressable
          key={item.label}
          accessibilityRole="button"
          onPress={item.onPress}
          style={({ pressed }) => [
            styles.footerLink,
            index > 0 && styles.tileDivider,
            pressed && styles.pressed,
          ]}
        >
          <Icon source={item.icon} size={16} color={HB_COLORS.action} />
          <Text style={styles.footerLinkLabel}>{item.label}</Text>
        </Pressable>
      ))}
    </View>
  );
}

export function KeyValueList({
  rows,
}: {
  rows: { label: string; value: string; muted?: boolean; strong?: boolean }[];
}) {
  return (
    <View style={styles.kvList}>
      {rows.map((row) => (
        <View key={row.label} style={styles.kvRow}>
          <Text style={styles.kvLabel}>{row.label}</Text>
          <Text
            style={[
              styles.kvValue,
              row.muted && styles.kvValueMuted,
              row.strong && styles.kvValueStrong,
            ]}
          >
            {row.value}
          </Text>
        </View>
      ))}
    </View>
  );
}

/** 带圆形指示的单选卡片；选中时蓝色描边和浅蓝底。 */
export function RadioCard({
  selected,
  disabled,
  onPress,
  title,
  meta,
  detail,
  badge,
}: {
  selected: boolean;
  disabled?: boolean;
  onPress: () => void;
  title: string;
  meta?: string;
  detail?: string;
  badge?: ReactNode;
}) {
  return (
    <Pressable
      accessibilityRole="radio"
      accessibilityState={{ checked: selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[
        styles.radioCard,
        selected && styles.radioCardSelected,
        disabled && styles.radioCardDisabled,
      ]}
    >
      <View
        style={[
          styles.radioRing,
          selected ? styles.radioRingSelected : styles.radioRingIdle,
        ]}
      />
      <View style={styles.flexText}>
        <Text style={[styles.radioTitle, disabled && styles.disabledText]}>
          {title}
        </Text>
        {meta ? <Text style={styles.caption}>{meta}</Text> : null}
        {detail ? (
          <Text numberOfLines={1} style={styles.radioDetail}>
            {detail}
          </Text>
        ) : null}
      </View>
      {badge}
    </Pressable>
  );
}

export function SwitchField({
  label,
  hint,
  value,
  onChange,
  disabled,
  warning,
  divider,
}: {
  label: string;
  hint?: string;
  value: boolean;
  onChange: (value: boolean) => void;
  disabled?: boolean;
  warning?: boolean;
  divider?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="switch"
      accessibilityLabel={label}
      accessibilityHint={hint}
      accessibilityState={{ checked: value, disabled }}
      disabled={disabled}
      onPress={() => onChange(!value)}
      style={[styles.switchRow, divider && styles.switchDivider]}
    >
      <View style={styles.flexText}>
        <Text style={styles.switchLabel}>{label}</Text>
        {hint ? <Text style={styles.caption}>{hint}</Text> : null}
      </View>
      {/* 整行可点，开关本身不再单独接收无障碍焦点 */}
      <View
        importantForAccessibility="no-hide-descendants"
        accessibilityElementsHidden
      >
        <Switch
          value={value}
          onValueChange={onChange}
          disabled={disabled}
          color={warning ? HB_COLORS.warning : HB_COLORS.action}
        />
      </View>
    </Pressable>
  );
}

export function LabeledInput({
  label,
  value,
  onChangeText,
  placeholder,
  keyboardType,
  multiline,
  autoFocus,
  helper,
  style,
}: {
  label: string;
  value: string;
  onChangeText: (value: string) => void;
  placeholder?: string;
  keyboardType?: KeyboardTypeOptions;
  multiline?: boolean;
  autoFocus?: boolean;
  helper?: string;
  style?: StyleProp<ViewStyle>;
}) {
  const [focused, setFocused] = useState(false);
  return (
    <View style={[styles.field, style]}>
      <Text style={styles.fieldLabel}>{label}</Text>
      <NativeTextInput
        accessibilityLabel={label}
        value={value}
        onChangeText={onChangeText}
        placeholder={placeholder}
        placeholderTextColor={ICON_MUTED}
        keyboardType={keyboardType}
        multiline={multiline}
        autoFocus={autoFocus}
        autoCapitalize="none"
        autoCorrect={false}
        onFocus={() => setFocused(true)}
        onBlur={() => setFocused(false)}
        style={[
          styles.input,
          multiline && styles.inputMultiline,
          focused && styles.inputFocused,
        ]}
      />
      {helper ? <Text style={styles.caption}>{helper}</Text> : null}
    </View>
  );
}

export function InfoNote({ children }: { children: string }) {
  return (
    <View style={styles.infoNote}>
      <Icon source="information-outline" size={16} color={ICON_MUTED} />
      <Text style={[styles.caption, styles.flexText]}>{children}</Text>
    </View>
  );
}

export function NavRow({
  icon,
  title,
  subtitle,
  onPress,
}: {
  icon: string;
  title: string;
  subtitle?: string;
  onPress: () => void;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      onPress={onPress}
      style={({ pressed }) => [
        styles.panel,
        styles.navRow,
        pressed && styles.pressed,
      ]}
    >
      <Icon source={icon} size={20} color={HB_COLORS.textSecondary} />
      <View style={styles.flexText}>
        <Text style={styles.switchLabel}>{title}</Text>
        {subtitle ? <Text style={styles.caption}>{subtitle}</Text> : null}
      </View>
      <Icon source="chevron-right" size={20} color={ICON_MUTED} />
    </Pressable>
  );
}

/** 底部保存栏：左侧说明改了什么，右侧主按钮；页面底部和弹层 footer 共用。 */
export function SaveBar({
  title,
  detail,
  actionLabel,
  onPress,
  disabled,
  loading,
  error,
  style,
}: {
  title: string;
  detail?: string;
  actionLabel: string;
  onPress: () => void;
  disabled?: boolean;
  loading?: boolean;
  error?: string;
  style?: StyleProp<ViewStyle>;
}) {
  return (
    <View style={style}>
      {error ? <Text style={styles.errorText}>{error}</Text> : null}
      <View style={styles.saveBar}>
        <View style={styles.flexText}>
          <Text style={styles.switchLabel}>{title}</Text>
          {detail ? (
            <Text numberOfLines={1} style={styles.caption}>
              {detail}
            </Text>
          ) : null}
        </View>
        <PrimaryButton
          label={actionLabel}
          onPress={onPress}
          disabled={disabled}
          loading={loading}
        />
      </View>
    </View>
  );
}

export function PrimaryButton({
  label,
  onPress,
  disabled,
  loading,
  icon,
  style,
}: {
  label: string;
  onPress: () => void;
  disabled?: boolean;
  loading?: boolean;
  icon?: string;
  style?: StyleProp<ViewStyle>;
}) {
  return (
    <Button
      mode="contained"
      icon={icon}
      onPress={onPress}
      disabled={disabled || loading}
      loading={loading}
      buttonColor={HB_COLORS.action}
      textColor={HB_COLORS.white}
      style={[styles.button, style]}
      contentStyle={styles.buttonContent}
      labelStyle={styles.buttonLabel}
    >
      {label}
    </Button>
  );
}

export function SecondaryButton({
  label,
  onPress,
  disabled,
  icon,
  style,
}: {
  label: string;
  onPress: () => void;
  disabled?: boolean;
  icon?: string;
  style?: StyleProp<ViewStyle>;
}) {
  return (
    <Button
      mode="outlined"
      icon={icon}
      onPress={onPress}
      disabled={disabled}
      textColor={HB_COLORS.action}
      style={[styles.button, styles.outlined, style]}
      contentStyle={styles.buttonContent}
      labelStyle={styles.buttonLabel}
    >
      {label}
    </Button>
  );
}

export function TextLink({
  label,
  icon,
  onPress,
  disabled,
}: {
  label: string;
  icon?: string;
  onPress: () => void;
  disabled?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [styles.textLink, pressed && styles.pressed]}
    >
      {icon ? <Icon source={icon} size={18} color={HB_COLORS.action} /> : null}
      <Text style={styles.textLinkLabel}>{label}</Text>
    </Pressable>
  );
}

export function ConfirmLines({
  title,
  lines,
}: {
  title?: string;
  lines: string[];
}) {
  return (
    <View style={styles.confirm}>
      {title ? <Text style={styles.switchLabel}>{title}</Text> : null}
      {lines.map((line) => (
        <Text key={line} style={styles.confirmLine}>
          {line}
        </Text>
      ))}
    </View>
  );
}

/** 页面骨架：可滚动内容 + 可选的固定底栏（OTA 页的保存栏）。 */
export function ScreenFrame({
  header,
  children,
  footer,
  refreshing,
  onRefresh,
}: {
  header: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  refreshing: boolean;
  onRefresh: () => void;
}) {
  return (
    <View style={styles.frame}>
      <ScrollView
        contentContainerStyle={styles.frameContent}
        keyboardShouldPersistTaps="handled"
        automaticallyAdjustKeyboardInsets
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={onRefresh}
            tintColor={HB_COLORS.action}
          />
        }
      >
        {header}
        <View style={styles.frameBody}>{children}</View>
      </ScrollView>
      {footer ? <View style={styles.frameFooter}>{footer}</View> : null}
    </View>
  );
}

export const ui = StyleSheet.create({
  caption: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  body: { fontSize: 14, lineHeight: 20, color: HB_COLORS.textPrimary },
  muted: { fontSize: 13, lineHeight: 20, color: HB_COLORS.textSecondary },
  error: { fontSize: 13, lineHeight: 20, color: HB_COLORS.danger },
  cardBody: { padding: HB_SPACING.md, gap: HB_SPACING.sm },
  headRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  flexText: { flex: 1, minWidth: 0 },
  cardTitle: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
  },
  version: {
    fontSize: 30,
    lineHeight: 36,
    fontWeight: "700",
    letterSpacing: -0.5,
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  versionRow: {
    flexDirection: "row",
    alignItems: "baseline",
    gap: HB_SPACING.xs,
  },
  statusRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  selectedSoft: { backgroundColor: SELECTED_SOFT },
});

const pillTone = {
  success: StyleSheet.create({
    box: { backgroundColor: SUCCESS_SOFT },
    text: { color: HB_COLORS.success },
  }),
  neutral: StyleSheet.create({
    box: { backgroundColor: HB_COLORS.surfaceMuted },
    text: { color: HB_COLORS.textSecondary },
  }),
  warning: StyleSheet.create({
    box: { backgroundColor: "#FFFAEB" },
    text: { color: HB_COLORS.warning },
  }),
  danger: StyleSheet.create({
    box: { backgroundColor: "#FEF3F2" },
    text: { color: HB_COLORS.danger },
  }),
};

const dotTone: Record<Tone, string> = {
  success: DOT_SUCCESS,
  neutral: "#98A2B3",
  warning: "#F79009",
  danger: "#F04438",
};

const styles = StyleSheet.create({
  frame: { flex: 1, backgroundColor: HB_COLORS.background },
  frameContent: { paddingBottom: HB_SPACING.xl },
  frameBody: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: 20,
    gap: 28,
  },
  frameFooter: {
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outline,
  },
  segmentTrack: {
    flexDirection: "row",
    gap: 4,
    padding: 4,
    borderRadius: 10,
  },
  segmentTrackStrong: { backgroundColor: HB_COLORS.outlineMuted },
  segmentTrackSoft: { backgroundColor: HB_COLORS.surfaceMuted },
  segment: {
    flex: 1,
    minHeight: 44,
    paddingHorizontal: 6,
    borderRadius: HB_RADIUS.control,
    alignItems: "center",
    justifyContent: "center",
  },
  segmentSelected: {
    backgroundColor: HB_COLORS.white,
    shadowColor: "#101828",
    shadowOpacity: 0.1,
    shadowRadius: 2,
    shadowOffset: { width: 0, height: 1 },
    elevation: 1,
  },
  segmentLabel: {
    fontSize: 14,
    fontWeight: "500",
    color: HB_COLORS.textSecondary,
  },
  segmentLabelSelected: { fontWeight: "600", color: HB_COLORS.textPrimary },
  tabs: {
    flexDirection: "row",
    borderBottomWidth: 1,
    borderBottomColor: HB_COLORS.outline,
  },
  tab: {
    flex: 1,
    minHeight: 44,
    alignItems: "center",
    justifyContent: "center",
    marginBottom: -1,
    borderBottomWidth: 2,
    borderBottomColor: "transparent",
  },
  tabSelected: { borderBottomColor: HB_COLORS.action },
  tabInner: { flexDirection: "row", alignItems: "center", gap: 6 },
  terminalNav: { flexDirection: "row", gap: HB_SPACING.xs, paddingVertical: 2 },
  terminalPill: {
    minHeight: 40,
    paddingHorizontal: 14,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    borderRadius: 20,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  terminalPillSelected: {
    backgroundColor: BRAND_SOFT,
    borderColor: "#B2CCFF",
  },
  terminalLabel: {
    fontSize: 14,
    fontWeight: "500",
    color: "#344054",
  },
  terminalLabelSelected: { fontWeight: "600", color: HB_COLORS.action },
  tabLabel: { fontSize: 15, fontWeight: "500", color: HB_COLORS.textSecondary },
  tabLabelSelected: { fontWeight: "600", color: HB_COLORS.action },
  sectionHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
    minHeight: 32,
  },
  sectionTitle: {
    fontSize: 16,
    lineHeight: 24,
    fontWeight: "700",
    color: HB_COLORS.textPrimary,
  },
  caption: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  panel: {
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    overflow: "hidden",
  },
  panelDashed: { borderStyle: "dashed", borderColor: HB_COLORS.outline },
  pill: {
    paddingHorizontal: 8,
    paddingVertical: 2,
    borderRadius: 999,
    flexShrink: 0,
  },
  pillLabel: { fontSize: 12, lineHeight: 18, fontWeight: "600" },
  dot: { width: 8, height: 8, borderRadius: 4 },
  iconBadge: {
    width: 40,
    height: 40,
    borderRadius: 10,
    alignItems: "center",
    justifyContent: "center",
  },
  tiles: {
    flexDirection: "row",
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  tile: {
    flex: 1,
    minHeight: 64,
    alignItems: "center",
    justifyContent: "center",
    gap: 4,
    paddingHorizontal: 2,
  },
  tileDivider: { borderLeftWidth: 1, borderLeftColor: HB_COLORS.outlineMuted },
  tileLabel: { fontSize: 12, lineHeight: 16, color: HB_COLORS.textPrimary },
  tileLabelEmphasis: { fontWeight: "600", color: HB_COLORS.action },
  disabledText: { color: HB_COLORS.textSecondary },
  pressed: { backgroundColor: HB_COLORS.surfaceMuted },
  footerLinks: {
    flexDirection: "row",
    borderTopWidth: 1,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  footerLink: {
    flex: 1,
    minHeight: 48,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
  },
  footerLinkLabel: { fontSize: 13, fontWeight: "500", color: HB_COLORS.action },
  kvList: { gap: 10 },
  kvRow: { flexDirection: "row", gap: HB_SPACING.sm },
  kvLabel: {
    width: 96,
    fontSize: 14,
    lineHeight: 20,
    color: HB_COLORS.textSecondary,
  },
  kvValue: {
    flex: 1,
    minWidth: 0,
    fontSize: 14,
    lineHeight: 20,
    color: HB_COLORS.textPrimary,
  },
  kvValueMuted: { color: HB_COLORS.textSecondary },
  kvValueStrong: { fontWeight: "600" },
  radioCard: {
    minHeight: 64,
    paddingHorizontal: 14,
    paddingVertical: HB_SPACING.sm,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 10,
  },
  radioCardSelected: {
    backgroundColor: SELECTED_SOFT,
    borderColor: HB_COLORS.action,
  },
  radioCardDisabled: { backgroundColor: HB_COLORS.surfaceMuted },
  radioRing: {
    width: 20,
    height: 20,
    borderRadius: 10,
    backgroundColor: HB_COLORS.white,
  },
  radioRingIdle: { borderWidth: 2, borderColor: "#98A2B3" },
  radioRingSelected: { borderWidth: 6, borderColor: HB_COLORS.action },
  radioTitle: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
    fontVariant: ["tabular-nums"],
  },
  radioDetail: { fontSize: 13, lineHeight: 20, color: HB_COLORS.textSecondary },
  flexText: { flex: 1, minWidth: 0 },
  switchRow: {
    minHeight: 64,
    paddingVertical: HB_SPACING.xs,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
  },
  switchDivider: { borderTopWidth: 1, borderTopColor: HB_COLORS.outlineMuted },
  switchLabel: {
    fontSize: 15,
    lineHeight: 22,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  field: { gap: 6 },
  fieldLabel: {
    fontSize: 13,
    lineHeight: 18,
    fontWeight: "600",
    color: "#344054",
  },
  input: {
    minHeight: 48,
    paddingHorizontal: HB_SPACING.sm,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.white,
    fontSize: 16,
    color: HB_COLORS.textPrimary,
  },
  inputMultiline: {
    minHeight: 88,
    paddingTop: HB_SPACING.sm,
    paddingBottom: HB_SPACING.sm,
    textAlignVertical: "top",
  },
  inputFocused: { borderWidth: 2, borderColor: HB_COLORS.action },
  infoNote: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.xs,
  },
  navRow: {
    minHeight: 56,
    paddingHorizontal: HB_SPACING.md,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
  },
  saveBar: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  errorText: {
    fontSize: 13,
    lineHeight: 20,
    color: HB_COLORS.danger,
    marginBottom: HB_SPACING.xs,
  },
  button: { borderRadius: HB_RADIUS.control },
  outlined: { borderColor: HB_COLORS.outline },
  buttonContent: { minHeight: 48, paddingHorizontal: 6 },
  buttonLabel: { fontSize: 15, fontWeight: "600" },
  textLink: {
    minHeight: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: 4,
    paddingHorizontal: 4,
    borderRadius: HB_RADIUS.control,
  },
  textLinkLabel: { fontSize: 14, fontWeight: "600", color: HB_COLORS.action },
  confirm: {
    gap: 6,
    padding: HB_SPACING.md,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  confirmLine: { fontSize: 14, lineHeight: 22, color: HB_COLORS.textPrimary },
});
