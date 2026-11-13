import type { ComponentProps, ReactNode } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { MONO_FONT, PICK_COLORS } from "@/modules/warehouse-picking/components/pick-theme";

type IconName = ComponentProps<typeof MaterialCommunityIcons>["name"];

/** 打包各页共用的配色：沿用拣货页的状态色（同一套 HB_COLORS 色系）。 */
export const PACK_COLORS = PICK_COLORS;
export { MONO_FONT };

export type Tone = "blue" | "green" | "gray" | "orange";

const TONES: Record<Tone, { bg: string; text: string }> = {
  blue: { bg: PACK_COLORS.infoBg, text: PACK_COLORS.action },
  green: { bg: PACK_COLORS.successBg, text: PACK_COLORS.success },
  gray: { bg: PACK_COLORS.neutralChipBg, text: PACK_COLORS.neutralChipText },
  orange: { bg: PACK_COLORS.warningBg, text: PACK_COLORS.warning },
};

export function Tag({ tone, label, icon }: { tone: Tone; label: string; icon?: IconName }) {
  return (
    <View style={[styles.tag, { backgroundColor: TONES[tone].bg }]}>
      {icon ? <MaterialCommunityIcons name={icon} size={13} color={TONES[tone].text} /> : null}
      <Text numberOfLines={1} style={[styles.tagText, { color: TONES[tone].text }]}>
        {label}
      </Text>
    </View>
  );
}

/** 扫码 / 手输框 + 相机按钮；扫码枪的键盘输入也会落到这里，回车即提交。 */
export function ScanBar({
  value,
  onChangeText,
  onSubmit,
  placeholder,
  inputLabel,
  cameraLabel,
  onCamera,
  onFocus,
  onBlur,
  busy = false,
  icon = "barcode-scan",
}: {
  value: string;
  onChangeText: (value: string) => void;
  onSubmit: (value: string) => void;
  placeholder: string;
  inputLabel: string;
  cameraLabel: string;
  onCamera: () => void;
  onFocus?: () => void;
  onBlur?: () => void;
  busy?: boolean;
  icon?: IconName;
}) {
  return (
    <View style={styles.scanRow}>
      <View style={styles.scanBox}>
        <MaterialCommunityIcons name={icon} size={18} color={PACK_COLORS.textSecondary} />
        <TextInput
          accessibilityLabel={inputLabel}
          placeholder={placeholder}
          placeholderTextColor={PACK_COLORS.textSecondary}
          value={value}
          onChangeText={onChangeText}
          onFocus={onFocus}
          onBlur={onBlur}
          onSubmitEditing={() => onSubmit(value)}
          autoCapitalize="characters"
          autoCorrect={false}
          returnKeyType="search"
          style={styles.scanInput}
        />
        {busy ? <ActivityIndicator size={14} /> : null}
      </View>
      <Pressable accessibilityRole="button" accessibilityLabel={cameraLabel} onPress={onCamera} style={styles.cameraButton}>
        <MaterialCommunityIcons name="camera-outline" size={22} color={PACK_COLORS.action} />
      </Pressable>
    </View>
  );
}

export interface Option<T extends string> {
  key: T;
  label: string;
}

/** 分段切换（箱 / 托盘、分店 / 运输公司 / 暂不指定）。 */
export function Segmented<T extends string>({
  options,
  value,
  onChange,
  label,
}: {
  options: readonly Option<T>[];
  value: T;
  onChange: (value: T) => void;
  label: string;
}) {
  return (
    <View accessibilityRole="tablist" accessibilityLabel={label} style={styles.segment}>
      {options.map((option, index) => (
        <Pressable
          key={option.key}
          accessibilityRole="tab"
          accessibilityState={{ selected: value === option.key }}
          onPress={() => onChange(option.key)}
          style={[styles.segmentItem, index > 0 ? styles.segmentDivider : null, value === option.key ? styles.segmentActive : null]}
        >
          <Text numberOfLines={1} style={[styles.segmentText, value === option.key ? styles.segmentTextActive : null]}>
            {option.label}
          </Text>
        </Pressable>
      ))}
    </View>
  );
}

/** 一排可换行的筛选胶囊；单选。 */
export function ChipRow<T extends string>({
  options,
  value,
  onChange,
  label,
}: {
  options: readonly Option<T>[];
  value: T;
  onChange: (value: T) => void;
  label: string;
}) {
  return (
    <View accessibilityRole="radiogroup" accessibilityLabel={label} style={styles.chips}>
      {options.map((option) => (
        <Chip key={option.key} label={option.label} selected={value === option.key} onPress={() => onChange(option.key)} />
      ))}
    </View>
  );
}

export function Chip({ label, selected, onPress }: { label: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="radio"
      accessibilityState={{ checked: selected }}
      onPress={onPress}
      style={[styles.chip, selected ? styles.chipActive : null]}
    >
      <Text numberOfLines={1} style={[styles.chipText, selected ? styles.chipTextActive : null]}>
        {label}
      </Text>
    </Pressable>
  );
}

export function SectionTitle({ title, right }: { title: string; right?: string | null }) {
  return (
    <View style={styles.sectionRow}>
      <Text style={styles.sectionTitle}>{title}</Text>
      {right ? <Text style={styles.sectionRight}>{right}</Text> : null}
    </View>
  );
}

/** 图标 + 标题 + 副标题的一行（箱 / 托盘列表、下一步动作）。 */
export function UnitRow({
  icon,
  tone = "gray",
  title,
  monoTitle = false,
  subtitle,
  right,
  onPress,
  divider = false,
  accessibilityLabel,
}: {
  icon: IconName;
  tone?: Tone;
  title: string;
  monoTitle?: boolean;
  subtitle?: string | null;
  right?: ReactNode;
  onPress?: () => void;
  divider?: boolean;
  accessibilityLabel?: string;
}) {
  const content = (
    <>
      <View style={[styles.unitIcon, { backgroundColor: TONES[tone].bg }]}>
        <MaterialCommunityIcons name={icon} size={20} color={TONES[tone].text} />
      </View>
      <View style={styles.unitMid}>
        <Text numberOfLines={1} style={[styles.unitTitle, monoTitle ? styles.mono : null]}>
          {title}
        </Text>
        {subtitle ? (
          <Text numberOfLines={2} style={styles.unitSubtitle}>
            {subtitle}
          </Text>
        ) : null}
      </View>
      {right}
    </>
  );
  const rowStyle = [styles.unitRow, divider ? styles.unitDivider : null];
  return onPress ? (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel} onPress={onPress} style={rowStyle}>
      {content}
    </Pressable>
  ) : (
    <View style={rowStyle}>{content}</View>
  );
}

export function Chevron() {
  return <MaterialCommunityIcons name="chevron-right" size={20} color={PACK_COLORS.inkMuted} />;
}

/** 弹层 / 卡片里的「标签 — 值」一行；可点时右侧带箭头。 */
export function KeyValueRow({
  label,
  value,
  placeholder,
  onPress,
  first = false,
  children,
}: {
  label: string;
  value?: string | null;
  placeholder?: string;
  onPress?: () => void;
  first?: boolean;
  children?: ReactNode;
}) {
  const body = (
    <>
      <Text style={styles.kvLabel}>{label}</Text>
      {children ?? (
        <View style={styles.kvValueWrap}>
          <Text numberOfLines={1} style={[styles.kvValue, value ? null : styles.kvPlaceholder]}>
            {value || placeholder || "—"}
          </Text>
          {onPress ? <Chevron /> : null}
        </View>
      )}
    </>
  );
  const rowStyle = [styles.kvRow, first ? null : styles.kvDivider];
  return onPress ? (
    <Pressable accessibilityRole="button" accessibilityLabel={`${label} ${value || placeholder || ""}`} onPress={onPress} style={rowStyle}>
      {body}
    </Pressable>
  ) : (
    <View style={rowStyle}>{body}</View>
  );
}

/** − 数字 + 步进器；点数字由调用方弹数字键盘。 */
export function Stepper({
  value,
  onDecrease,
  onIncrease,
  onPressValue,
  decreaseLabel,
  increaseLabel,
  valueLabel,
  disabled = false,
  compact = false,
}: {
  value: number;
  onDecrease: () => void;
  onIncrease: () => void;
  onPressValue?: () => void;
  decreaseLabel: string;
  increaseLabel: string;
  valueLabel: string;
  disabled?: boolean;
  compact?: boolean;
}) {
  const size = compact ? 36 : 52;
  return (
    <View style={[styles.stepper, { height: size }]}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={decreaseLabel}
        disabled={disabled}
        onPress={onDecrease}
        style={[styles.stepButton, { width: size }]}
      >
        <MaterialCommunityIcons name="minus" size={compact ? 18 : 22} color={PACK_COLORS.ink} />
      </Pressable>
      <Pressable
        accessibilityRole={onPressValue ? "button" : "text"}
        accessibilityLabel={valueLabel}
        disabled={disabled || !onPressValue}
        onPress={onPressValue}
        style={[styles.stepValue, compact ? styles.stepValueCompact : null]}
      >
        <Text style={[styles.stepValueText, compact ? styles.stepValueTextCompact : null]}>{value}</Text>
      </Pressable>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={increaseLabel}
        disabled={disabled}
        onPress={onIncrease}
        style={[styles.stepButton, { width: size }]}
      >
        <MaterialCommunityIcons name="plus" size={compact ? 18 : 22} color={PACK_COLORS.ink} />
      </Pressable>
    </View>
  );
}

export function WarningCard({ children }: { children: ReactNode }) {
  return <View style={styles.warningCard}>{children}</View>;
}

export const packStyles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: PACK_COLORS.background },
  body: { padding: 12, gap: 10, flexGrow: 1 },
  card: { backgroundColor: PACK_COLORS.white, borderWidth: 1, borderColor: PACK_COLORS.outlineMuted, borderRadius: 12, padding: 12 },
  cardList: { backgroundColor: PACK_COLORS.white, borderWidth: 1, borderColor: PACK_COLORS.outlineMuted, borderRadius: 12, paddingHorizontal: 12, paddingVertical: 2 },
  bar: { paddingHorizontal: 12, paddingTop: 10, paddingBottom: 10, gap: 8, backgroundColor: PACK_COLORS.white, borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  barRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 8 },
  buttons: { flexDirection: "row", gap: 8 },
  button: { borderRadius: 8 },
  buttonContent: { minHeight: 48 },
  flex: { flex: 1 },
  flexSmall: { flex: 0.6 },
  muted: { fontSize: 12, lineHeight: 16, color: PACK_COLORS.textSecondary },
  strong: { fontSize: 14, lineHeight: 20, fontWeight: "700", color: PACK_COLORS.ink, fontVariant: ["tabular-nums"] },
  mono: { fontFamily: MONO_FONT },
  loading: { marginTop: 48 },
  hiddenInput: { position: "absolute", width: 1, height: 1, opacity: 0, left: -100 },
  sheetBody: { gap: 12 },
  textInput: {
    minHeight: 48,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: PACK_COLORS.outline,
    paddingHorizontal: 12,
    fontSize: 16,
    color: PACK_COLORS.ink,
    backgroundColor: PACK_COLORS.white,
  },
  errorText: { fontSize: 13, lineHeight: 18, color: PACK_COLORS.danger },
});

const styles = StyleSheet.create({
  tag: { minHeight: 22, flexDirection: "row", alignItems: "center", gap: 3, paddingHorizontal: 8, borderRadius: 11 },
  tagText: { fontSize: 12, lineHeight: 16, fontWeight: "600" },
  scanRow: { flexDirection: "row", gap: 8 },
  scanBox: {
    flex: 1,
    height: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 12,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: PACK_COLORS.outline,
    backgroundColor: PACK_COLORS.white,
  },
  scanInput: { flex: 1, minWidth: 0, fontSize: 15, color: PACK_COLORS.ink, paddingVertical: 0 },
  cameraButton: { width: 44, height: 44, borderRadius: 8, backgroundColor: PACK_COLORS.infoBg, alignItems: "center", justifyContent: "center" },
  segment: { flexDirection: "row", height: 44, borderRadius: 8, borderWidth: 1, borderColor: PACK_COLORS.outline, overflow: "hidden" },
  segmentItem: { flex: 1, alignItems: "center", justifyContent: "center", backgroundColor: PACK_COLORS.white, paddingHorizontal: 4 },
  segmentDivider: { borderLeftWidth: 1, borderLeftColor: PACK_COLORS.outline },
  segmentActive: { backgroundColor: PACK_COLORS.infoBg },
  segmentText: { fontSize: 13, fontWeight: "500", color: PACK_COLORS.neutralChipText },
  segmentTextActive: { fontWeight: "700", color: PACK_COLORS.action },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  chip: {
    minHeight: 36,
    paddingHorizontal: 12,
    borderRadius: 18,
    borderWidth: 1,
    borderColor: PACK_COLORS.outline,
    backgroundColor: PACK_COLORS.white,
    alignItems: "center",
    justifyContent: "center",
  },
  chipActive: { borderColor: PACK_COLORS.infoBorder, backgroundColor: PACK_COLORS.infoBg },
  chipText: { fontSize: 13, fontWeight: "500", color: PACK_COLORS.neutralChipText, fontVariant: ["tabular-nums"] },
  chipTextActive: { fontWeight: "700", color: PACK_COLORS.action },
  sectionRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 8, marginTop: 4 },
  sectionTitle: { fontSize: 13, lineHeight: 18, fontWeight: "700", color: PACK_COLORS.textSecondary },
  sectionRight: { fontSize: 12, lineHeight: 18, color: PACK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  unitRow: { minHeight: 56, flexDirection: "row", alignItems: "center", gap: 10, paddingVertical: 6 },
  unitDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  unitIcon: { width: 36, height: 36, borderRadius: 8, alignItems: "center", justifyContent: "center" },
  unitMid: { flex: 1, minWidth: 0 },
  unitTitle: { fontSize: 15, lineHeight: 20, fontWeight: "700", color: PACK_COLORS.ink },
  unitSubtitle: { fontSize: 12, lineHeight: 17, color: PACK_COLORS.textSecondary },
  mono: { fontFamily: MONO_FONT },
  kvRow: { minHeight: 48, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 12 },
  kvDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  kvLabel: { fontSize: 14, lineHeight: 20, color: PACK_COLORS.textSecondary },
  kvValueWrap: { flex: 1, minWidth: 0, flexDirection: "row", alignItems: "center", justifyContent: "flex-end", gap: 2 },
  kvValue: { flexShrink: 1, fontSize: 14, lineHeight: 20, fontWeight: "600", color: PACK_COLORS.ink, textAlign: "right" },
  kvPlaceholder: { fontWeight: "400", color: PACK_COLORS.inkMuted },
  stepper: { flexDirection: "row", gap: 8 },
  stepButton: { borderRadius: 8, borderWidth: 1, borderColor: PACK_COLORS.outline, backgroundColor: PACK_COLORS.white, alignItems: "center", justifyContent: "center" },
  stepValue: { flex: 1, minWidth: 0, borderRadius: 8, borderWidth: 1, borderColor: PACK_COLORS.outline, backgroundColor: PACK_COLORS.white, alignItems: "center", justifyContent: "center" },
  stepValueCompact: { flex: 0, minWidth: 44, borderWidth: 0, backgroundColor: "transparent" },
  stepValueText: { fontFamily: MONO_FONT, fontSize: 24, fontWeight: "700", color: PACK_COLORS.ink },
  stepValueTextCompact: { fontSize: 16 },
  warningCard: { backgroundColor: PACK_COLORS.warningBg, borderWidth: 1, borderColor: PACK_COLORS.warningBorder, borderRadius: 12, padding: 12, gap: 8 },
});
