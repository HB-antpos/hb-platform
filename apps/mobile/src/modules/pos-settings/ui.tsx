// 收银端设置共用的小组件：三态分段选择、设置项卡片、保存栏、保存确认底部弹层、设备状态标签。
// 视觉沿用现金模块（CashCard / StatusChip / NoticeBanner 等）与 HB_COLORS / HB_SPACING / HB_RADIUS。
import type { ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { CASH_SOFT, NoticeBanner, StatusChip, type CashTone } from "@/modules/store-cash/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { settingCopyId } from "./constants";
import {
  TRI_STATES,
  describeEffective,
  deviceStatusMeta,
  type PosSettingChange,
  type TriState,
} from "./logic";
import type {
  PosSettingDefinition,
  PosSettingDeviceStatusKind,
  PosSettingEffective,
  PosSettingScope,
} from "./types";

type TranslateFn = (key: string, options?: Record<string, unknown>) => string;

// ───────────────────────── 文案辅助 ─────────────────────────

/** 设置项名称：目录里有、文案里没有的新项（App 还没发版）回退显示键名。 */
export function settingName(t: TranslateFn, key: string): string {
  return t(`posSettings:settings.${settingCopyId(key)}.name`, { defaultValue: key });
}

export function settingHint(t: TranslateFn, key: string): string {
  return t(`posSettings:settings.${settingCopyId(key)}.hint`, { defaultValue: "" });
}

/** 适用端：「WPF · iPad · 手持」。 */
export function clientKindsText(t: TranslateFn, clientKinds: readonly string[]): string {
  return clientKinds.map((kind) => t(`posSettings:clients.${kind}`, { defaultValue: kind })).join(" · ");
}

/** 生效值文案：「开 · 本店」「关 · 默认」「沿用本机」。 */
export function effectiveText(t: TranslateFn, effective: PosSettingEffective): string {
  const parts = describeEffective(effective);
  const value = t(`posSettings:effective.value.${parts.value}`);
  const source = parts.source ? t(`posSettings:effective.source.${parts.source}`) : "";
  return source ? t("posSettings:effective.combined", { value, source }) : value;
}

export function effectiveTone(effective: PosSettingEffective): CashTone {
  return effective.value === true ? "success" : "neutral";
}

/** 三态在确认弹层里的文案：inherit 带上「上一层的值」，如「跟随全局（关）」；全局层的「未设置」不带括号。 */
export function stateText(
  t: TranslateFn,
  scope: PosSettingScope,
  state: TriState,
  parent: PosSettingEffective | null,
): string {
  const label = t(`posSettings:choice.${scope}.${state}`);
  if (state !== "inherit" || scope === "global" || !parent) return label;
  const value = t(`posSettings:effective.value.${describeEffective(parent).value}`);
  return t("posSettings:confirm.inheritWith", { label, value });
}

// ───────────────────────── 三态分段选择 ─────────────────────────

export function TriStateSegment({
  scope,
  value,
  onChange,
  disabled,
  accessibilityLabel,
}: {
  scope: PosSettingScope;
  value: TriState;
  onChange: (next: TriState) => void;
  disabled?: boolean;
  accessibilityLabel: string;
}) {
  const { t } = useAppTranslation("posSettings");
  return (
    <View accessibilityRole="radiogroup" accessibilityLabel={accessibilityLabel} style={[styles.segment, disabled ? styles.segmentReadOnly : null]}>
      {TRI_STATES.map((state, index) => {
        const selected = state === value;
        return (
          <Pressable
            key={state}
            accessibilityRole="radio"
            accessibilityState={{ selected, disabled: Boolean(disabled) }}
            disabled={disabled}
            onPress={() => onChange(state)}
            style={[
              styles.segmentItem,
              index > 0 ? styles.segmentDivider : null,
              selected ? styles.segmentItemSelected : null,
            ]}
          >
            <Text style={[styles.segmentText, selected ? styles.segmentTextSelected : null]} numberOfLines={1}>
              {t(`choice.${scope}.${state}`)}
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

// ───────────────────────── 设置项卡片 ─────────────────────────

export function SettingCard({
  definition,
  scope,
  state,
  onChange,
  readOnly,
  changed,
  effective,
  children,
}: {
  definition: PosSettingDefinition;
  scope: PosSettingScope;
  state: TriState;
  onChange: (next: TriState) => void;
  readOnly: boolean;
  /** 草稿与已保存值不同：卡片描上警示色边框。 */
  changed: boolean;
  /** 右上角显示该层预览的最终值与来源。 */
  effective?: PosSettingEffective;
  children?: ReactNode;
}) {
  const { t } = useAppTranslation("posSettings");
  const name = settingName(t, definition.key);
  const hint = settingHint(t, definition.key);
  return (
    <View style={[styles.card, changed ? styles.cardChanged : null]}>
      <View style={styles.cardHeader}>
        <View style={styles.cardTitleBlock}>
          <View style={styles.titleLine}>
            <Text variant="titleSmall" style={styles.cardTitle}>{name}</Text>
            {definition.isNew ? <StatusChip tone="warning" label={t("badge.new")} /> : null}
          </View>
          <Text style={styles.clients}>{clientKindsText(t, definition.clientKinds)}</Text>
          {hint ? <Text style={styles.hint}>{hint}</Text> : null}
        </View>
        {effective ? <StatusChip tone={effectiveTone(effective)} label={effectiveText(t, effective)} /> : null}
      </View>
      <TriStateSegment scope={scope} value={state} onChange={onChange} disabled={readOnly} accessibilityLabel={name} />
      {children}
    </View>
  );
}

/** 设备页每项下面的「标签 ……… 值」一行。 */
export function InfoRow({ label, children }: { label: string; children: ReactNode }) {
  return (
    <View style={styles.infoRow}>
      <Text style={styles.infoLabel}>{label}</Text>
      <View style={styles.infoValue}>{children}</View>
    </View>
  );
}

export function InfoValueText({ text }: { text: string }) {
  return <Text style={styles.infoValueText}>{text}</Text>;
}

// ───────────────────────── 设备状态标签 ─────────────────────────

export function DeviceStatusChip({ status }: { status: PosSettingDeviceStatusKind }) {
  const { t } = useAppTranslation("posSettings");
  const meta = deviceStatusMeta(status);
  return <StatusChip tone={meta.tone} label={t(`deviceStatus.${meta.labelKey}`)} />;
}

/** never 状态的说明：旧版本不支持，或还没联过网。 */
export function DeviceStatusHint({ status }: { status: PosSettingDeviceStatusKind }) {
  const { t } = useAppTranslation("posSettings");
  if (!deviceStatusMeta(status).hint) return null;
  return <Text style={styles.statusHint}>{t("deviceStatus.neverHint")}</Text>;
}

// ───────────────────────── 保存栏与确认弹层 ─────────────────────────

/** 底部固定的「保存并下发」栏；没有修改时按钮置灰。 */
export function SaveBar({ disabled, onPress }: { disabled: boolean; onPress: () => void }) {
  const { t } = useAppTranslation("posSettings");
  return (
    <View style={styles.saveBar}>
      <Button mode="contained" icon="send-outline" disabled={disabled} onPress={onPress} contentStyle={styles.saveButtonContent}>
        {t("actions.save")}
      </Button>
    </View>
  );
}

export function SaveConfirmSheet({
  visible,
  title,
  subtitle,
  scope,
  changes,
  parentFor,
  impactText,
  warning,
  error,
  busy,
  onCancel,
  onConfirm,
}: {
  visible: boolean;
  title: string;
  subtitle: string;
  scope: PosSettingScope;
  changes: readonly PosSettingChange[];
  /** 某项「跟随上一层」时上一层的生效值，用来写成「跟随全局（关）」。 */
  parentFor: (key: string) => PosSettingEffective | null;
  impactText: string;
  warning?: string | null;
  error?: string | null;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const { t } = useAppTranslation("posSettings");
  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={subtitle}
      onDismiss={onCancel}
      dismissable={!busy}
      footer={
        <View style={styles.sheetFooter}>
          <Button mode="outlined" style={styles.sheetButton} disabled={busy} onPress={onCancel}>
            {t("confirm.cancel")}
          </Button>
          <Button mode="contained" style={styles.sheetButton} disabled={busy} loading={busy} onPress={onConfirm}>
            {t("confirm.confirm")}
          </Button>
        </View>
      }
    >
      <View style={styles.changeList}>
        {changes.map((change, index) => (
          <View key={change.key} style={[styles.changeRow, index > 0 ? styles.changeRowDivider : null]}>
            <Text style={styles.changeName}>{settingName(t, change.key)}</Text>
            <Text style={styles.changeArrow}>
              {t("confirm.change", {
                from: stateText(t, scope, change.from, parentFor(change.key)),
                to: stateText(t, scope, change.to, parentFor(change.key)),
              })}
            </Text>
          </View>
        ))}
      </View>
      <View style={styles.impactRow}>
        <Text style={styles.impactLabel}>{t("confirm.impact")}</Text>
        <Text style={styles.impactValue}>{impactText}</Text>
      </View>
      {warning ? <NoticeBanner tone="warning" icon="alert-outline" text={warning} /> : null}
      {error ? <NoticeBanner tone="danger" icon="alert-circle-outline" text={error} /> : null}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  segment: {
    flexDirection: "row",
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    borderRadius: HB_RADIUS.control,
    overflow: "hidden",
    backgroundColor: HB_COLORS.white,
  },
  // 只读：保持当前选中项可见，只整体淡一点，提示不可点。
  segmentReadOnly: { opacity: 0.7 },
  segmentItem: { flex: 1, minHeight: 44, alignItems: "center", justifyContent: "center", paddingHorizontal: HB_SPACING.xxs },
  segmentDivider: { borderLeftWidth: 1, borderLeftColor: HB_COLORS.outline },
  segmentItemSelected: { backgroundColor: CASH_SOFT.accent },
  segmentText: { color: HB_COLORS.textPrimary, fontSize: 14 },
  segmentTextSelected: { color: HB_COLORS.action, fontWeight: "700" },
  card: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.md,
    gap: HB_SPACING.sm,
  },
  cardChanged: { borderColor: HB_COLORS.warning, backgroundColor: CASH_SOFT.warning },
  cardHeader: { flexDirection: "row", alignItems: "flex-start", justifyContent: "space-between", gap: HB_SPACING.xs },
  cardTitleBlock: { flex: 1, minWidth: 0, gap: 2 },
  titleLine: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: HB_SPACING.xs },
  cardTitle: { color: HB_COLORS.textPrimary, fontWeight: "700", flexShrink: 1 },
  clients: { color: HB_COLORS.textSecondary, fontSize: 12 },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 17 },
  infoRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm, minHeight: 28 },
  infoLabel: { color: HB_COLORS.textSecondary, fontSize: 14 },
  infoValue: { flexShrink: 1, alignItems: "flex-end", flexDirection: "row", gap: HB_SPACING.xs },
  infoValueText: { color: HB_COLORS.textPrimary, fontSize: 14, fontWeight: "600" },
  statusHint: { color: HB_COLORS.textSecondary, fontSize: 12 },
  saveBar: {
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
  },
  saveButtonContent: { minHeight: 48 },
  sheetFooter: { flexDirection: "row", gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xs },
  sheetButton: { flex: 1 },
  changeList: {
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    paddingHorizontal: HB_SPACING.sm,
  },
  changeRow: { paddingVertical: HB_SPACING.sm, gap: 2 },
  changeRowDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  changeName: { color: HB_COLORS.textPrimary, fontWeight: "700", fontSize: 15 },
  changeArrow: { color: HB_COLORS.textSecondary, fontSize: 14 },
  impactRow: { flexDirection: "row", justifyContent: "space-between", gap: HB_SPACING.sm },
  impactLabel: { color: HB_COLORS.textSecondary, fontSize: 14 },
  impactValue: { color: HB_COLORS.textPrimary, fontWeight: "700", fontSize: 14, flexShrink: 1, textAlign: "right" },
});
