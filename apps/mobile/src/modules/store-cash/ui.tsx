// 现金模块共用的小组件：页面框架、卡片、提示条、状态标签、金额/日期输入。
// 视觉沿用现有业务页（HB_COLORS / HB_SPACING / HB_RADIUS + react-native-paper），不引入新的设计风格。
import { forwardRef, useState, type ComponentRef, type ReactNode } from "react";
import { Pressable, StyleSheet, View, type StyleProp, type TextStyle, type ViewStyle } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, HelperText, Modal, Portal, Text, TextInput } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { MonthDatePicker } from "@/components/attendance/MonthDatePicker";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { sanitizeMoneyInput } from "./money";

export const CASH_SOFT = {
  accent: "#EAF2FF",
  success: "#ECFDF3",
  warning: "#FFFAEB",
  danger: "#FEF3F2",
} as const;

export type CashTone = "neutral" | "info" | "success" | "warning" | "danger";

const TONES: Record<CashTone, { background: string; foreground: string }> = {
  neutral: { background: HB_COLORS.surfaceMuted, foreground: HB_COLORS.textSecondary },
  info: { background: CASH_SOFT.accent, foreground: HB_COLORS.action },
  success: { background: CASH_SOFT.success, foreground: HB_COLORS.success },
  warning: { background: CASH_SOFT.warning, foreground: HB_COLORS.warning },
  danger: { background: CASH_SOFT.danger, foreground: HB_COLORS.danger },
};

type IconName = keyof typeof MaterialCommunityIcons.glyphMap;

/** 页面框架：顶部返回 + 标题，内容区自行滚动。 */
export function CashScreenFrame({
  title,
  onBack,
  children,
  footer,
}: {
  title: string;
  onBack: () => void;
  children: ReactNode;
  footer?: ReactNode;
}) {
  const { t } = useAppTranslation("common");
  return (
    <SafeAreaView style={styles.safe} edges={["top"]}>
      <View style={styles.header}>
        <Button compact onPress={onBack} icon="chevron-left" labelStyle={styles.backLabel}>
          {t("actions.back")}
        </Button>
        <Text variant="titleLarge" style={styles.headerTitle} numberOfLines={1}>
          {title}
        </Text>
        <View style={styles.headerSpacer} />
      </View>
      {children}
      {footer ? <View style={styles.footer}>{footer}</View> : null}
    </SafeAreaView>
  );
}

export function CashCard({
  title,
  right,
  children,
  style,
}: {
  title?: string;
  right?: ReactNode;
  children: ReactNode;
  style?: StyleProp<ViewStyle>;
}) {
  return (
    <View style={[styles.card, style]}>
      {title || right ? (
        <View style={styles.cardHeader}>
          {title ? (
            <Text variant="titleSmall" style={styles.cardTitle} numberOfLines={1}>
              {title}
            </Text>
          ) : (
            <View />
          )}
          {right}
        </View>
      ) : null}
      {children}
    </View>
  );
}

export function NoticeBanner({
  tone = "info",
  icon,
  text,
  children,
  accessibilityLabel,
}: {
  tone?: CashTone;
  icon?: IconName;
  text: string;
  children?: ReactNode;
  accessibilityLabel?: string;
}) {
  const palette = TONES[tone];
  return (
    <View
      accessible
      accessibilityRole="alert"
      accessibilityLabel={accessibilityLabel ?? text}
      style={[styles.notice, { backgroundColor: palette.background }]}
    >
      {icon ? <MaterialCommunityIcons name={icon} size={20} color={palette.foreground} style={styles.noticeIcon} /> : null}
      <View style={styles.noticeBody}>
        <Text style={[styles.noticeText, { color: palette.foreground }]}>{text}</Text>
        {children}
      </View>
    </View>
  );
}

export function StatusChip({ label, tone = "neutral" }: { label: string; tone?: CashTone }) {
  const palette = TONES[tone];
  return (
    <View style={[styles.chip, { backgroundColor: palette.background }]}>
      <Text style={[styles.chipText, { color: palette.foreground }]} numberOfLines={1}>
        {label}
      </Text>
    </View>
  );
}

/** 「标签 ……… 数值」一行，数值右对齐、等宽数字。 */
export function ValueRow({
  label,
  value,
  strong,
  muted,
  valueStyle,
}: {
  label: string;
  value: string;
  strong?: boolean;
  muted?: boolean;
  valueStyle?: StyleProp<TextStyle>;
}) {
  return (
    <View style={styles.valueRow}>
      <Text style={[styles.valueLabel, muted ? styles.mutedText : null]} numberOfLines={2}>
        {label}
      </Text>
      <Text style={[styles.valueText, strong ? styles.valueStrong : null, muted ? styles.mutedText : null, valueStyle]}>
        {value}
      </Text>
    </View>
  );
}

/** 横向可选 chip（筛选、类别选择共用）。 */
export function ChoiceChip({
  label,
  selected,
  onPress,
  disabled,
}: {
  label: string;
  selected: boolean;
  onPress: () => void;
  disabled?: boolean;
}) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected, disabled }}
      disabled={disabled}
      onPress={onPress}
      style={[styles.choice, selected ? styles.choiceSelected : null, disabled ? styles.disabled : null]}
    >
      <Text style={[styles.choiceText, selected ? styles.choiceTextSelected : null]} numberOfLines={1}>
        {label}
      </Text>
    </Pressable>
  );
}

/** 金额输入：decimal 键盘、前缀澳元符号、输入时即时过滤成最多两位小数。 */
export const MoneyInput = forwardRef<
  ComponentRef<typeof TextInput>,
  {
    label: string;
    value: string;
    onChange: (text: string) => void;
    error?: string;
    disabled?: boolean;
  }
>(function MoneyInput({ label, value, onChange, error, disabled }, ref) {
  return (
    <View>
      <TextInput
        ref={ref as never}
        mode="outlined"
        label={label}
        value={value}
        onChangeText={(text) => onChange(sanitizeMoneyInput(text))}
        keyboardType="decimal-pad"
        left={<TextInput.Affix text="$" />}
        error={Boolean(error)}
        disabled={disabled}
        style={styles.input}
      />
      {error ? <HelperText type="error" visible>{error}</HelperText> : null}
    </View>
  );
});

/**
 * 日期选择字段：点开后在 Paper 弹层里选日期（不用手机时区限制范围，范围由调用方按门店今天算好传入）。
 * allowEmpty 时弹层里有「清除」，用于允许清空的覆盖营业日。
 */
export function CashDateField({
  label,
  value,
  onChange,
  minDate,
  maxDate,
  allowEmpty = false,
  disabled,
  error,
  placeholder,
}: {
  label: string;
  value: string | null;
  onChange: (value: string | null) => void;
  minDate?: string | null;
  maxDate?: string;
  allowEmpty?: boolean;
  disabled?: boolean;
  error?: string;
  placeholder?: string;
}) {
  const { t } = useAppTranslation("common");
  const [open, setOpen] = useState(false);
  return (
    <View>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={`${label} ${value ?? placeholder ?? ""}`.trim()}
        accessibilityState={{ disabled, expanded: open }}
        disabled={disabled}
        onPress={() => setOpen(true)}
        style={[styles.dateField, error ? styles.dateFieldError : null, disabled ? styles.disabled : null]}
      >
        <View style={styles.dateFieldBody}>
          <Text style={styles.dateFieldLabel}>{label}</Text>
          <Text style={[styles.dateFieldValue, value ? null : styles.mutedText]}>{value || placeholder || "--"}</Text>
        </View>
        <MaterialCommunityIcons name="calendar-month-outline" size={22} color={HB_COLORS.action} />
      </Pressable>
      {error ? <HelperText type="error" visible>{error}</HelperText> : null}
      <Portal>
        <Modal visible={open} onDismiss={() => setOpen(false)} contentContainerStyle={styles.dateModal}>
          <Text variant="titleMedium" style={styles.dateModalTitle}>{label}</Text>
          <MonthDatePicker
            value={value ?? ""}
            allowEmpty={allowEmpty}
            minDate={minDate ?? undefined}
            maxDate={maxDate}
            onChange={(next) => {
              onChange(next);
              setOpen(false);
            }}
          />
          <View style={styles.dateModalActions}>
            {allowEmpty && value ? (
              <Button
                onPress={() => {
                  onChange(null);
                  setOpen(false);
                }}
              >
                {t("actions.clear")}
              </Button>
            ) : null}
            <Button onPress={() => setOpen(false)}>{t("actions.cancel")}</Button>
          </View>
        </Modal>
      </Portal>
    </View>
  );
}

export const cashUiStyles = StyleSheet.create({
  scroll: { flex: 1 },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xl },
  muted: { color: HB_COLORS.textSecondary },
  sectionTitle: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  row: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  rowWrap: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
});

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  header: {
    minHeight: 60,
    backgroundColor: HB_COLORS.white,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    borderBottomWidth: 1,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  backLabel: { color: HB_COLORS.action, fontSize: 16 },
  headerTitle: { color: HB_COLORS.textPrimary, fontWeight: "700", flexShrink: 1 },
  headerSpacer: { width: 80 },
  footer: {
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
    gap: HB_SPACING.xs,
  },
  card: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.md,
    gap: HB_SPACING.xs,
  },
  cardHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  cardTitle: { color: HB_COLORS.textPrimary, fontWeight: "700", flexShrink: 1 },
  notice: {
    flexDirection: "row",
    alignItems: "flex-start",
    gap: HB_SPACING.xs,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
  },
  noticeIcon: { marginTop: 1 },
  noticeBody: { flex: 1, minWidth: 0, gap: HB_SPACING.xs },
  noticeText: { fontSize: 13, lineHeight: 19 },
  chip: { alignSelf: "flex-start", paddingHorizontal: 8, paddingVertical: 3, borderRadius: 6 },
  chipText: { fontSize: 12, fontWeight: "600" },
  valueRow: { flexDirection: "row", alignItems: "baseline", justifyContent: "space-between", gap: HB_SPACING.sm, minHeight: 28 },
  valueLabel: { flex: 1, minWidth: 0, color: HB_COLORS.textSecondary, fontSize: 14 },
  valueText: { color: HB_COLORS.textPrimary, fontSize: 15, fontVariant: ["tabular-nums"], fontWeight: "600", flexShrink: 0 },
  valueStrong: { fontSize: 17, fontWeight: "700" },
  mutedText: { color: HB_COLORS.textSecondary },
  choice: {
    minHeight: 40,
    paddingHorizontal: HB_SPACING.sm,
    borderRadius: 20,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    alignItems: "center",
    justifyContent: "center",
  },
  choiceSelected: { borderColor: HB_COLORS.action, backgroundColor: CASH_SOFT.accent },
  choiceText: { color: HB_COLORS.textPrimary, fontSize: 14 },
  choiceTextSelected: { color: HB_COLORS.action, fontWeight: "600" },
  disabled: { opacity: 0.5 },
  input: { backgroundColor: HB_COLORS.white },
  dateField: {
    minHeight: 56,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
    paddingHorizontal: HB_SPACING.sm,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
  },
  dateFieldError: { borderColor: HB_COLORS.danger },
  dateFieldBody: { flex: 1, minWidth: 0 },
  dateFieldLabel: { color: HB_COLORS.textSecondary, fontSize: 12 },
  dateFieldValue: { color: HB_COLORS.textPrimary, fontSize: 16, fontVariant: ["tabular-nums"] },
  dateModal: {
    marginHorizontal: HB_SPACING.md,
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    padding: HB_SPACING.md,
    gap: HB_SPACING.xs,
  },
  dateModalTitle: { fontWeight: "700", color: HB_COLORS.textPrimary },
  dateModalActions: { flexDirection: "row", justifyContent: "flex-end", gap: HB_SPACING.xs },
});
