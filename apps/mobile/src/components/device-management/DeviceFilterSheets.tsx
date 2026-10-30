import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Button, Icon, Searchbar, Text } from "react-native-paper";
import { FilterChip, StatusTag, type Translate } from "@/components/device-management/primitives";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import type { DeviceVersionSummary } from "@/modules/device-management/types";
import { compareVersions, type AppVersionGroup } from "@/modules/device-management/version-state";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";

export interface Option {
  value: string;
  label: string;
}

function Section({ title, hint, children, highlight }: { title: ReactNode; hint?: string; children: ReactNode; highlight?: boolean }) {
  return (
    <View style={[styles.section, highlight && styles.sectionHighlight]}>
      <View style={styles.sectionHeader}>
        <Text style={styles.sectionTitle}>{title}</Text>
        {hint ? <Text style={styles.sectionHint}>{hint}</Text> : null}
      </View>
      {children}
    </View>
  );
}

function ChipRow({
  options,
  selected,
  onSelect,
  counts,
}: {
  options: Option[];
  selected: string;
  onSelect: (value: string) => void;
  counts?: Partial<Record<string, number>>;
}) {
  return (
    <View style={styles.chipRow}>
      {options.map((option) => (
        <FilterChip
          key={option.value}
          label={option.label}
          count={counts?.[option.value]}
          selected={selected === option.value}
          onPress={() => onSelect(option.value)}
        />
      ))}
    </View>
  );
}

function StoreRow({ label, disabled, onPress, title }: { label: string; disabled: boolean; onPress: () => void; title: string }) {
  return (
    <Section title={title}>
      <Pressable
        accessibilityRole="button"
        disabled={disabled}
        onPress={onPress}
        style={({ pressed }) => [styles.select, pressed && styles.pressed, disabled && styles.disabled]}
      >
        <View style={styles.selectLabel}>
          <Icon source="store-outline" size={18} color={HB_COLORS.textSecondary} />
          <Text style={styles.selectText} numberOfLines={1}>{label}</Text>
        </View>
        <Icon source="chevron-right" size={18} color={HB_COLORS.textSecondary} />
      </Pressable>
    </Section>
  );
}

function SheetFooterButtons({ t, resultLabel, onClear, onApply }: { t: Translate; resultLabel: string; onClear: () => void; onApply: () => void }) {
  return (
    <View style={styles.footer}>
      <Button mode="outlined" style={styles.footerClear} contentStyle={styles.footerContent} onPress={onClear}>
        {t("deviceManagement:filters.clear")}
      </Button>
      <Button mode="contained" style={styles.footerApply} contentStyle={styles.footerContent} onPress={onApply}>
        {resultLabel}
      </Button>
    </View>
  );
}

const MAX_VERSION_CHIPS = 5;

export interface RegisteredFilterSheetProps {
  visible: boolean;
  t: Translate;
  onDismiss: () => void;
  onClear: () => void;
  storeLabel: string;
  storesLoading: boolean;
  onPickStore: () => void;
  statusOptions: Option[];
  status: string;
  onStatus: (value: string) => void;
  systemOptions: Option[];
  system: string;
  onSystem: (value: string) => void;
  typeOptions: Option[];
  type: string;
  onType: (value: string) => void;
  summary?: DeviceVersionSummary | null;
  versionState: string;
  onVersionState: (value: string) => void;
  appVersions: readonly string[];
  onToggleVersion: (version: string) => void;
  onOpenVersionPicker: () => void;
  resultCount: number;
}

/** 设备注册筛选抽屉：分店 + 版本区排在最前（小屏上不用滚动就能看到版本条件），其余结构化条件在后；关键词搜索在页面搜索框，不放在这里。 */
export function RegisteredFilterSheet(props: RegisteredFilterSheetProps) {
  const { t, summary } = props;
  const hasPolicy = Boolean(summary?.targetVersion);
  const states = summary?.states;
  const stateOptions: Option[] = [
    { value: "all", label: t("deviceManagement:filters.all") },
    ...(hasPolicy
      ? [
          { value: "latest", label: t("deviceManagement:versionFilter.latest") },
          { value: "behind", label: t("deviceManagement:versionFilter.behind") },
          ...(states && states.ahead > 0 ? [{ value: "ahead", label: t("deviceManagement:versionFilter.ahead") }] : []),
          { value: "notTargeted", label: t("deviceManagement:versionFilter.notTargeted") },
        ]
      : []),
    { value: "unreported", label: t("deviceManagement:versionFilter.unreported") },
  ];
  const versionItems = (summary?.items ?? []).filter((item): item is { appVersion: string; total: number } => item.appVersion !== null);

  return (
    <BusinessSheet
      visible={props.visible}
      title={t("deviceManagement:filters.title")}
      onDismiss={props.onDismiss}
      footer={
        <SheetFooterButtons
          t={t}
          resultLabel={t("deviceManagement:filters.viewResults", { count: props.resultCount })}
          onClear={props.onClear}
          onApply={props.onDismiss}
        />
      }
    >
      <StoreRow title={t("deviceManagement:filters.store")} label={props.storeLabel} disabled={props.storesLoading} onPress={props.onPickStore} />
      <Section
        highlight
        title={
          <View style={styles.titleWithTag}>
            <Icon source="tag-outline" size={16} color={HB_COLORS.textPrimary} />
            <Text style={styles.sectionTitle}>{t("deviceManagement:versionFilter.clientTitle")}</Text>
            <StatusTag tone="info" label={t("deviceManagement:versionFilter.newTag")} />
          </View>
        }
      >
        <ChipRow
          options={stateOptions}
          selected={props.appVersions.length > 0 ? "" : props.versionState}
          onSelect={props.onVersionState}
          counts={
            states
              ? { latest: states.latest, behind: states.behind, ahead: states.ahead, notTargeted: states.notTargeted, unreported: states.unreported }
              : undefined
          }
        />
        {versionItems.length > 0 ? (
          <>
            <Text style={styles.subLabel}>{t("deviceManagement:versionFilter.specific")}</Text>
            <View style={styles.chipRow}>
              {versionItems.slice(0, MAX_VERSION_CHIPS).map((item) => (
                <FilterChip
                  key={item.appVersion}
                  label={item.appVersion}
                  count={item.total}
                  selected={props.appVersions.includes(item.appVersion)}
                  onPress={() => props.onToggleVersion(item.appVersion)}
                />
              ))}
              {versionItems.length > MAX_VERSION_CHIPS ? (
                <FilterChip label={t("deviceManagement:versionFilter.moreVersions")} onPress={props.onOpenVersionPicker} />
              ) : null}
            </View>
          </>
        ) : null}
      </Section>
      <Section title={t("deviceManagement:filters.status")}>
        <ChipRow options={props.statusOptions} selected={props.status} onSelect={props.onStatus} />
      </Section>
      <Section title={t("deviceManagement:filters.systemTitle")}>
        <ChipRow options={props.systemOptions} selected={props.system} onSelect={props.onSystem} />
      </Section>
      <Section title={t("deviceManagement:filters.typeTitle")}>
        <ChipRow options={props.typeOptions} selected={props.type} onSelect={props.onType} />
      </Section>
    </BusinessSheet>
  );
}

export interface AppFilterSheetProps {
  visible: boolean;
  t: Translate;
  onDismiss: () => void;
  onClear: () => void;
  storeLabel: string;
  storesLoading: boolean;
  onPickStore: () => void;
  onlineOptions: Option[];
  online: string;
  onOnline: (value: string) => void;
  systemOptions: Option[];
  system: string;
  onSystem: (value: string) => void;
  /** 状态预设的数量，来自版本分布（按当前分店 / 系统 / 关键词范围）。 */
  stateCounts: { latest: number; behind: number; unreported: number };
  versionState: string;
  onVersionState: (value: string) => void;
  groups: AppVersionGroup[];
  selectedKey: string | null;
  onSelectVersion: (key: string) => void;
  resultCount: number;
}

/** App 使用情况筛选抽屉：版本按系统分组并带构建号，单选。 */
export function AppFilterSheet(props: AppFilterSheetProps) {
  const { t } = props;
  const stateOptions: Option[] = [
    { value: "all", label: t("deviceManagement:filters.all") },
    { value: "latest", label: t("deviceManagement:versionFilter.appLatest") },
    { value: "behind", label: t("deviceManagement:versionFilter.appBehind") },
    { value: "unreported", label: t("deviceManagement:versionFilter.unreported") },
  ];

  return (
    <BusinessSheet
      visible={props.visible}
      title={t("deviceManagement:filters.title")}
      onDismiss={props.onDismiss}
      footer={
        <SheetFooterButtons
          t={t}
          resultLabel={t("deviceManagement:filters.viewResults", { count: props.resultCount })}
          onClear={props.onClear}
          onApply={props.onDismiss}
        />
      }
    >
      <StoreRow title={t("deviceManagement:filters.store")} label={props.storeLabel} disabled={props.storesLoading} onPress={props.onPickStore} />
      <Section title={t("deviceManagement:filters.onlineTitle")}>
        <ChipRow options={props.onlineOptions} selected={props.online} onSelect={props.onOnline} />
      </Section>
      <Section title={t("deviceManagement:filters.systemTitle")}>
        <ChipRow options={props.systemOptions} selected={props.system} onSelect={props.onSystem} />
      </Section>
      <Section
        highlight
        title={
          <View style={styles.titleWithTag}>
            <Icon source="tag-outline" size={16} color={HB_COLORS.textPrimary} />
            <Text style={styles.sectionTitle}>{t("deviceManagement:versionFilter.appTitle")}</Text>
            <StatusTag tone="info" label={t("deviceManagement:versionFilter.newTag")} />
          </View>
        }
      >
        <ChipRow
          options={stateOptions}
          selected={props.selectedKey ? "" : props.versionState}
          onSelect={props.onVersionState}
          counts={props.stateCounts}
        />
        {props.groups.map((group) => (
          <View key={group.deviceSystem || "unknown"} style={styles.groupBlock}>
            <Text style={styles.subLabel}>{group.deviceSystem || t("deviceManagement:distribution.unknownSystem")}</Text>
            <View style={styles.chipRow}>
              {group.options.map((option) => (
                <FilterChip
                  key={option.key}
                  label={option.appBuildVersion ? `${option.appVersion} (${option.appBuildVersion})` : option.appVersion ?? ""}
                  count={option.total}
                  selected={props.selectedKey === option.key}
                  onPress={() => props.onSelectVersion(option.key)}
                />
              ))}
            </View>
          </View>
        ))}
      </Section>
    </BusinessSheet>
  );
}

/** 版本多选列表：搜索 + 勾选，确认后一次性写回筛选条件。 */
export function VersionPickerSheet({
  visible,
  t,
  summary,
  selected,
  onCancel,
  onConfirm,
}: {
  visible: boolean;
  t: Translate;
  summary?: DeviceVersionSummary | null;
  selected: readonly string[];
  onCancel: () => void;
  onConfirm: (versions: string[]) => void;
}) {
  const [draft, setDraft] = useState<string[]>([...selected]);
  const [query, setQuery] = useState("");

  // 每次打开都从当前已生效的筛选重新开始，取消即丢弃草稿。
  useEffect(() => {
    if (visible) {
      setDraft([...selected]);
      setQuery("");
    }
  }, [selected, visible]);

  const items = useMemo(
    () =>
      (summary?.items ?? [])
        .filter((item): item is { appVersion: string; total: number } => item.appVersion !== null)
        .filter((item) => item.appVersion.includes(query.trim())),
    [query, summary?.items]
  );

  const toggle = (version: string) =>
    setDraft((current) => (current.includes(version) ? current.filter((item) => item !== version) : [...current, version]));

  return (
    <BusinessSheet
      visible={visible}
      title={t("deviceManagement:versionFilter.pickerTitle")}
      onDismiss={onCancel}
      footer={
        <View style={styles.footer}>
          <Button mode="outlined" style={styles.footerClear} contentStyle={styles.footerContent} onPress={onCancel}>
            {t("deviceManagement:actions.cancel")}
          </Button>
          <Button mode="contained" style={styles.footerApply} contentStyle={styles.footerContent} onPress={() => onConfirm(draft)}>
            {t("deviceManagement:versionFilter.pickerConfirm", { versions: draft.length })}
          </Button>
        </View>
      }
    >
      <Searchbar
        placeholder={t("deviceManagement:versionFilter.pickerSearch")}
        value={query}
        onChangeText={setQuery}
        style={styles.search}
      />
      {items.map((item) => {
        const checked = draft.includes(item.appVersion);
        const isTarget = Boolean(summary?.targetVersion) && compareVersions(item.appVersion, summary?.targetVersion) === 0;
        return (
          <Pressable
            key={item.appVersion}
            accessibilityRole="checkbox"
            accessibilityState={{ checked }}
            onPress={() => toggle(item.appVersion)}
            style={styles.versionRow}
          >
            <View style={[styles.checkbox, checked && styles.checkboxChecked]}>
              {checked ? <Icon source="check" size={16} color={HB_COLORS.white} /> : null}
            </View>
            <Text style={styles.versionName}>{item.appVersion}</Text>
            {isTarget ? <View><StatusTag tone="ok" label={t("deviceManagement:versionFilter.targetTag")} /></View> : null}
            <View style={styles.versionSpacer} />
            <Text style={styles.versionCount}>{t("deviceManagement:versionFilter.deviceCount", { count: item.total })}</Text>
          </Pressable>
        );
      })}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  pressed: { opacity: 0.7 },
  disabled: { opacity: 0.5 },
  section: { gap: HB_SPACING.xs, paddingVertical: HB_SPACING.sm, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  // 版本区用淡蓝底拉出来，让新增的条件一眼可见；左右负边距抵消抽屉内容的内边距。
  sectionHighlight: { backgroundColor: "#F5F9FF", marginHorizontal: -HB_SPACING.md, paddingHorizontal: HB_SPACING.md, borderBottomWidth: 0 },
  sectionHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  sectionTitle: { fontSize: 13, lineHeight: 20, fontWeight: "700", color: HB_COLORS.textPrimary },
  sectionHint: { fontSize: 12, color: HB_COLORS.textSecondary },
  titleWithTag: { flexDirection: "row", alignItems: "center", gap: 6 },
  chipRow: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  subLabel: { fontSize: 12, lineHeight: 18, fontWeight: "600", color: HB_COLORS.textSecondary, marginTop: 4 },
  groupBlock: { gap: 6 },
  select: { minHeight: 48, borderWidth: 1, borderColor: HB_COLORS.outline, borderRadius: 10, paddingHorizontal: HB_SPACING.sm, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  selectLabel: { flexDirection: "row", alignItems: "center", gap: 8, flex: 1 },
  selectText: { fontSize: 14, color: HB_COLORS.textPrimary, flexShrink: 1 },
  footer: { flexDirection: "row", gap: HB_SPACING.xs, paddingBottom: HB_SPACING.xs },
  footerClear: { flex: 1, borderRadius: 10 },
  footerApply: { flex: 2, borderRadius: 10 },
  footerContent: { minHeight: 44 },
  search: { backgroundColor: "#F9FAFB", borderWidth: 1, borderColor: HB_COLORS.outlineMuted, elevation: 0 },
  versionRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm, minHeight: 52, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  checkbox: { width: 22, height: 22, borderRadius: 6, borderWidth: 1.5, borderColor: HB_COLORS.outline, alignItems: "center", justifyContent: "center" },
  checkboxChecked: { backgroundColor: HB_COLORS.action, borderColor: HB_COLORS.action },
  versionName: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  versionSpacer: { flex: 1 },
  versionCount: { fontSize: 13, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
});
