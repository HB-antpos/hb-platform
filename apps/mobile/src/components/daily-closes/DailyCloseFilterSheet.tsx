import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Icon, Text, TextInput } from "react-native-paper";
import { MonthDatePicker } from "@/components/attendance/MonthDatePicker";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import {
  buildDailyCloseListParams,
  canPickDailyCloseDevice,
  clearDailyCloseStores,
  createDefaultDailyCloseFilters,
  DAILY_CLOSE_CLIENT_KINDS,
  DAILY_CLOSE_KEYWORD_MAX_LENGTH,
  DAILY_CLOSE_MAX_RANGE_DAYS,
  DAILY_CLOSE_PAGE_SIZE,
  DAILY_CLOSE_RANGE_PRESETS,
  clientKindLabelKey,
  selectDailyClosePreset,
  todayInSydney,
  toggleDailyCloseStore,
  validateDailyCloseFilters,
} from "@/modules/daily-closes/logic";
import type { DailyCloseFilters } from "@/modules/daily-closes/types";
import { useDailyClosePreviewCount, useStoreDeviceOptions } from "@/modules/daily-closes/use-daily-closes";
import type { Store } from "@/modules/shop/types";
import { addDays, isValidDateString } from "@/modules/store-cash/dates";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { DAILY_UI } from "./ui";

export interface DailyCloseFilterSheetProps {
  visible: boolean;
  /** 已生效的筛选（含状态页签）；打开面板时草稿从它复制。 */
  filters: DailyCloseFilters;
  stores: Store[];
  /** 已生效筛选的命中总数：草稿与已生效一致时直接用它，不再多发一次请求。 */
  appliedTotal: number | undefined;
  onClose: () => void;
  onApply: (filters: DailyCloseFilters) => void;
}

/**
 * 筛选面板（底部抽屉）：本地草稿，点「查看 N 条」才回写。沿用员工操作日志筛选抽屉的交互：
 * 分店多选、营业日预设、来源、终端（仅选中单个分店后可选）、收银员；N 是草稿条件的实时命中数。
 * 自定义日期用同一个弹层里的「选日期」第二步（内嵌月历，选完自动返回）：BusinessSheet 是原生 Modal，
 * Paper 的 Portal 弹层会被压在它下面，所以不能用弹窗式日期选择；月历放在主表单下面又会落在折叠线以下看不见。
 */
export function DailyCloseFilterSheet({ visible, filters, stores, appliedTotal, onClose, onApply }: DailyCloseFilterSheetProps) {
  const { t } = useAppTranslation("dailyCloses");
  const [draft, setDraft] = useState(filters);
  const [storeQuery, setStoreQuery] = useState("");
  // 正在选哪一端日期：非空时弹层内容换成月历（第二步），选完或点「返回」回到主表单
  const [pickingDate, setPickingDate] = useState<"from" | "to" | null>(null);
  const today = todayInSydney();

  useEffect(() => {
    if (visible) {
      setDraft(filters);
      setStoreQuery("");
      setPickingDate(null);
    }
  }, [filters, visible]);

  const patch = (partial: Partial<DailyCloseFilters>) => setDraft((current) => ({ ...current, ...partial }));
  const visibleStores = useMemo(() => {
    const query = storeQuery.trim().toLowerCase();
    return query ? stores.filter((store) => store.storeCode.toLowerCase().includes(query) || store.storeName?.toLowerCase().includes(query)) : stores;
  }, [storeQuery, stores]);

  const rangeIssue = validateDailyCloseFilters(draft, today);
  const singleStore = canPickDailyCloseDevice(draft) ? draft.storeCodes[0].trim() : null;
  const { devices, loading: devicesLoading } = useStoreDeviceOptions(singleStore, draft.deviceCode);

  // 草稿与已生效筛选等价时用已加载的总数，否则取草稿条件的命中数
  const sameAsApplied =
    JSON.stringify(buildDailyCloseListParams(draft, 1, DAILY_CLOSE_PAGE_SIZE, today)) ===
    JSON.stringify(buildDailyCloseListParams(filters, 1, DAILY_CLOSE_PAGE_SIZE, today));
  const preview = useDailyClosePreviewCount(draft, visible && !sameAsApplied && !rangeIssue);
  const total = sameAsApplied ? appliedTotal : preview.total;

  const from = isValidDateString(draft.customFrom) ? draft.customFrom : today;
  const to = isValidDateString(draft.customTo) ? draft.customTo : today;
  const lastDay = DAILY_CLOSE_MAX_RANGE_DAYS - 1;

  return (
    <BusinessSheet
      visible={visible}
      title={pickingDate ? t(pickingDate === "from" ? "filters.rangeFrom" : "filters.rangeTo") : t("filters.title")}
      // 选日期这一步里点关闭 / 背景只退回主表单，不丢掉草稿
      onDismiss={() => (pickingDate ? setPickingDate(null) : onClose())}
      footer={
        pickingDate ? (
          <Button mode="outlined" onPress={() => setPickingDate(null)}>
            {t("filters.backToFilters")}
          </Button>
        ) : (
          <View>
            {rangeIssue ? <Text style={styles.issue}>{t(`filters.rangeIssue.${rangeIssue}`)}</Text> : null}
            <View style={styles.footer}>
              <Button mode="outlined" onPress={() => setDraft({ ...createDefaultDailyCloseFilters(), status: filters.status })} style={styles.footerButton}>
                {t("filters.reset")}
              </Button>
              <Button mode="contained" disabled={Boolean(rangeIssue)} onPress={() => onApply(draft)} style={[styles.footerButton, styles.footerPrimary]}>
                {total === undefined || rangeIssue ? t("filters.viewResults") : t("filters.viewCount", { count: total })}
              </Button>
            </View>
          </View>
        )
      }
    >
      {pickingDate === "from" ? (
        // 起始日不晚于结束日，且与结束日相距不超过 93 天：选不出不合法的区间
        <MonthDatePicker
          value={from}
          minDate={addDays(to, -lastDay)}
          maxDate={to}
          onChange={(next) => {
            patch({ customFrom: next });
            setPickingDate(null);
          }}
        />
      ) : pickingDate === "to" ? (
        <MonthDatePicker
          value={to}
          minDate={from}
          maxDate={minDate(today, addDays(from, lastDay))}
          onChange={(next) => {
            patch({ customTo: next });
            setPickingDate(null);
          }}
        />
      ) : (
        <>
          <View style={styles.headRow}>
            <Text style={DAILY_UI.sectionLabel}>{t("filters.stores")}</Text>
            <Text style={styles.hint}>{t("filters.storesHint")}</Text>
          </View>
          {stores.length > 6 ? (
            <TextInput
              mode="outlined"
              dense
              value={storeQuery}
              onChangeText={setStoreQuery}
              placeholder={t("filters.searchStore")}
              accessibilityLabel={t("filters.searchStore")}
              left={<TextInput.Icon icon="magnify" />}
            />
          ) : null}
          <View style={styles.wrap}>
            <Option label={t("filters.storesAll")} selected={draft.storeCodes.length === 0} onPress={() => setDraft(clearDailyCloseStores(draft))} />
            {visibleStores.map((store) => (
              <Option
                key={store.storeCode}
                label={store.storeName || store.storeCode}
                count={store.storeName ? store.storeCode : undefined}
                selected={draft.storeCodes.includes(store.storeCode)}
                onPress={() => setDraft(toggleDailyCloseStore(draft, store.storeCode))}
              />
            ))}
          </View>

          <View style={styles.headRow}>
            <Text style={DAILY_UI.sectionLabel}>{t("filters.range")}</Text>
            <Text style={styles.hint}>{t("filters.rangeHint")}</Text>
          </View>
          <View style={styles.wrap}>
            {DAILY_CLOSE_RANGE_PRESETS.map((preset) => (
              <Option
                key={preset}
                label={t(`presets.${preset}`)}
                selected={draft.preset === preset}
                onPress={() => setDraft(selectDailyClosePreset(draft, preset, today))}
              />
            ))}
          </View>
          {draft.preset === "custom" ? (
            <View style={styles.customBox}>
              <DateRow label={t("filters.rangeFrom")} value={draft.customFrom} onPress={() => setPickingDate("from")} />
              <DateRow label={t("filters.rangeTo")} value={draft.customTo} onPress={() => setPickingDate("to")} />
            </View>
          ) : null}

          <Text style={DAILY_UI.sectionLabel}>{t("filters.source")}</Text>
          <View style={styles.wrap}>
            <Option label={t("filters.sourceAll")} selected={!draft.clientKind} onPress={() => patch({ clientKind: null })} />
            {DAILY_CLOSE_CLIENT_KINDS.map((kind) => (
              <Option key={kind} label={t(`source.${clientKindLabelKey(kind)}`)} selected={draft.clientKind === kind} onPress={() => patch({ clientKind: kind })} />
            ))}
          </View>

          <View style={styles.headRow}>
            <Text style={DAILY_UI.sectionLabel}>{t("filters.device")}</Text>
            <Text style={styles.hint}>{t("filters.deviceHint")}</Text>
          </View>
          {singleStore ? (
            <View style={styles.wrap}>
              <Option label={t("filters.deviceAll")} selected={!draft.deviceCode} onPress={() => patch({ deviceCode: null })} />
              {devices.map((device) => (
                <Option key={device} label={device} selected={draft.deviceCode === device} onPress={() => patch({ deviceCode: device })} />
              ))}
              {devicesLoading ? (
                <View style={styles.inlineState}>
                  <ActivityIndicator size="small" color={HB_COLORS.brand} />
                  <Text style={styles.hint}>{t("filters.deviceLoading")}</Text>
                </View>
              ) : devices.length === 0 ? (
                <Text style={styles.hint}>{t("filters.deviceNone")}</Text>
              ) : null}
            </View>
          ) : (
            // 未选单个分店：用虚线占位说明为什么不能选，而不是整块隐藏
            <View style={[DAILY_UI.placeholder, styles.deviceOff]}>
              <Text style={DAILY_UI.placeholderText}>{t("filters.deviceNeedStore")}</Text>
            </View>
          )}

          <Text style={DAILY_UI.sectionLabel}>{t("filters.cashier")}</Text>
          <TextInput
            mode="outlined"
            dense
            value={draft.keyword}
            onChangeText={(keyword) => patch({ keyword })}
            placeholder={t("filters.cashierPlaceholder")}
            accessibilityLabel={t("filters.cashier")}
            maxLength={DAILY_CLOSE_KEYWORD_MAX_LENGTH}
          />
        </>
      )}
    </BusinessSheet>
  );
}

const minDate = (a: string, b: string) => (a < b ? a : b);

function Option({ label, count, selected, onPress }: { label: string; count?: string; selected: boolean; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityState={{ selected }} onPress={onPress} style={[styles.option, selected ? styles.optionOn : null]}>
      <Text style={[styles.optionText, selected ? styles.optionTextOn : null]}>{label}</Text>
      {count ? <Text style={[styles.optionCount, selected ? styles.optionTextOn : null]}>{count}</Text> : null}
    </Pressable>
  );
}

function DateRow({ label, value, onPress }: { label: string; value: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={`${label} ${value}`} onPress={onPress} style={styles.dateRow}>
      <Text style={styles.dateLabel}>{label}</Text>
      <Text style={[styles.dateValue, DAILY_UI.mono]}>{value || "--"}</Text>
      <Icon source="calendar-month-outline" size={20} color={HB_COLORS.action} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  headRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  hint: { flexShrink: 1, fontSize: 12, color: HB_COLORS.textSecondary, textAlign: "right" },
  wrap: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: HB_SPACING.xs },
  inlineState: { flexDirection: "row", alignItems: "center", gap: 6 },
  deviceOff: { alignItems: "flex-start" },
  customBox: { gap: HB_SPACING.xs },
  dateRow: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    minHeight: 44,
    paddingHorizontal: 12,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  dateLabel: { fontSize: 13, color: HB_COLORS.textSecondary },
  dateValue: { flex: 1, fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary, textAlign: "right" },
  option: {
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    minHeight: 36,
    paddingHorizontal: 12,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  optionOn: { borderColor: HB_COLORS.action, backgroundColor: "#EAF2FF" },
  optionText: { fontSize: 13, color: HB_COLORS.textPrimary },
  optionTextOn: { color: HB_COLORS.action, fontWeight: "600" },
  optionCount: { fontSize: 12, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  issue: { fontSize: 12, color: HB_COLORS.danger, marginBottom: HB_SPACING.xs },
  footer: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  footerPrimary: { flex: 2 },
});
