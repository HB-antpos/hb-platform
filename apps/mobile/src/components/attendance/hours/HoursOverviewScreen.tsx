// 店长「工时统计」列表页（设计图 A）：门店胶囊 + 周期条（1 周 / 2 周）+ 三个汇总格 + 提醒条 + 每人一张卡。
// 拆成两层：HoursOverviewScreen 是路由外壳（读路由参数、登录与权限守卫、算可选门店），
// HoursOverviewContent 只收 props、内部用 react-query 拉数据，不读登录态，便于免登录预览截图。
import { useEffect, useMemo, useState } from "react";
import { Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter } from "expo-router";
import { Text } from "react-native-paper";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import {
  formatHours,
  formatShortDate,
  localToday,
  periodStartForWeeksSwitch,
  periodWeekNumbers,
  shiftPeriodStart,
} from "@/modules/attendance/payroll-hours";
import { getPayrollHours, payrollHoursQueryKey } from "@/modules/attendance/payroll-hours-api";
import type { PayrollHoursEmployee, PayrollHoursQuery, PayrollHoursReport } from "@/modules/attendance/payroll-hours-types";
import { getPosEnabledStores } from "@/modules/shop/store-scope";
import type { Store } from "@/modules/shop/types";
import { useStores } from "@/modules/shop/use-stores";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { HoursAlerts } from "./HoursAlerts";
import { HoursEmployeeCard } from "./HoursEmployeeCard";
import {
  Hatch,
  HOURS_COLORS,
  HoursAlertBar,
  HoursCard,
  HoursError,
  HoursFrame,
  HoursLoading,
  HoursMessage,
  HoursTitle,
  MONO,
  SummaryTiles,
  WeeksSegment,
} from "./hours-ui";
import { firstParam, parseWeeksParam } from "./hours-params";
import { useHoursGuard } from "./use-hours-guard";

type Translate = (key: string, options?: Record<string, unknown>) => string;

function isPrimaryStore(store: object) {
  return "isPrimary" in store && store.isPrimary === true;
}

/** 路由外壳：/(shell)/attendance-hours?storeCode&weeks */
export function HoursOverviewScreen() {
  const router = useRouter();
  const params = useLocalSearchParams<{ storeCode?: string; weeks?: string }>();
  const { t } = useAppTranslation(["attendanceHours", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = useHoursGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <HoursMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  // 账号变化时重建页面状态，避免上一个账号的门店和工时留在屏幕上。
  return (
    <HoursOverviewStoreScope
      key={userGuid}
      initialStoreCode={firstParam(params.storeCode)}
      initialWeeks={parseWeeksParam(params.weeks) ?? 1}
      onBack={goBack}
    />
  );
}

/** 算可选门店（管理员看全部 POS 启用门店，否则只看主分店，与考勤管理页 managerStores 一致）并记住当前选择。 */
function HoursOverviewStoreScope({ initialStoreCode, initialWeeks, onBack }: { initialStoreCode?: string; initialWeeks: 1 | 2; onBack: () => void }) {
  const router = useRouter();
  const isAdmin = useAuthStore((state) => state.access.isAdmin);
  const { stores, selectedStoreCode: rememberedStoreCode, isLoading } = useStores();
  const managerStores = useMemo(() => {
    const posEnabled = getPosEnabledStores(stores);
    return isAdmin ? posEnabled : posEnabled.filter(isPrimaryStore);
  }, [isAdmin, stores]);
  const [storeCode, setStoreCode] = useState<string | null>(initialStoreCode ?? null);

  // 默认门店：路由参数 → App 当前已选门店 → 第一家；当前选择不在可选列表里时重新落位。
  useEffect(() => {
    if (managerStores.length === 0) return;
    if (storeCode && managerStores.some((store) => store.storeCode === storeCode)) return;
    const preferred = managerStores.find((store) => store.storeCode === rememberedStoreCode) ?? managerStores[0];
    setStoreCode(preferred.storeCode);
  }, [managerStores, rememberedStoreCode, storeCode]);

  const usableStoreCode = storeCode && managerStores.some((store) => store.storeCode === storeCode) ? storeCode : null;
  return (
    <HoursOverviewContent
      storeCode={usableStoreCode}
      stores={managerStores}
      storesLoading={isLoading}
      onSelectStore={setStoreCode}
      initialWeeks={initialWeeks}
      onBack={onBack}
      onOpenApprovals={() => router.push("/(shell)/attendance-management")}
      onOpenEmployee={(target) =>
        router.push({
          pathname: "/(shell)/attendance-hours-detail",
          params: { storeCode: target.storeCode, userGuid: target.userGuid, periodStart: target.periodStart, weeks: String(target.weeks) },
        })
      }
    />
  );
}

export interface HoursOverviewContentProps {
  /** 当前门店；为空时显示「没有可管理的分店」或加载中。 */
  storeCode: string | null;
  /** 门店选择胶囊的可选项；不传或只有一家时胶囊不可点。 */
  stores?: Store[];
  storesLoading?: boolean;
  onSelectStore?: (storeCode: string) => void;
  initialWeeks?: 1 | 2;
  /** 不传 = 后端取当前期。 */
  initialPeriodStart?: string;
  onBack: () => void;
  onOpenEmployee?: (target: { storeCode: string; userGuid: string; periodStart: string; weeks: 1 | 2 }) => void;
  onOpenApprovals?: () => void;
  /** 「今天」，只决定下一期按钮可不可点；预览截图时可固定。 */
  today?: string;
}

export function HoursOverviewContent({
  storeCode,
  stores = [],
  storesLoading = false,
  onSelectStore,
  initialWeeks = 1,
  initialPeriodStart,
  onBack,
  onOpenEmployee,
  onOpenApprovals,
  today = localToday(),
}: HoursOverviewContentProps) {
  const { t, language } = useAppTranslation(["attendanceHours", "common"]);
  const [weeks, setWeeks] = useState<1 | 2>(initialWeeks);
  // periodStart 为空 = 让后端取当前期；翻页 / 切周数后以后端返回的 periodStart 为基准推算。
  const [periodStart, setPeriodStart] = useState<string | undefined>(initialPeriodStart);
  const [mergeStores, setMergeStores] = useState(false);
  const [pickerVisible, setPickerVisible] = useState(false);
  const [manualRefreshing, setManualRefreshing] = useState(false);

  const query: PayrollHoursQuery | null = storeCode ? { storeCode, periodStart, weeks, mergeStores } : null;
  const hours = useQuery({
    queryKey: query ? payrollHoursQueryKey(query) : ["attendance", "payroll", "hours", "none"],
    queryFn: () => getPayrollHours(query as PayrollHoursQuery),
    enabled: Boolean(query),
    placeholderData: keepPreviousData,
  });
  // 换门店时不沿用上一家的数据。
  const report = hours.data && hours.data.storeCode === storeCode ? hours.data : undefined;
  const settled = Boolean(report) && !hours.isPlaceholderData;

  const storeLabel = stores.find((store) => store.storeCode === storeCode)?.storeName || report?.storeName || storeCode || "";

  const step = (direction: -1 | 1) => {
    if (!report || !settled) return;
    setPeriodStart(shiftPeriodStart(report.periodStart, report.weeks, direction));
  };
  const changeWeeks = (next: 1 | 2) => {
    if (next === weeks) return;
    if (report) setPeriodStart(periodStartForWeeksSwitch({ periodStart: report.periodStart, fromWeeks: report.weeks, toWeeks: next, today }));
    setWeeks(next);
  };
  const onRefresh = async () => {
    setManualRefreshing(true);
    try {
      await hours.refetch();
    } finally {
      setManualRefreshing(false);
    }
  };
  const openEmployee = onOpenEmployee && report
    ? (employee: PayrollHoursEmployee) => onOpenEmployee({ storeCode: report.storeCode, userGuid: employee.userGuid, periodStart: report.periodStart, weeks: report.weeks })
    : undefined;

  const storeChip = (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("store.pickerTitle")}
      disabled={stores.length < 2 || !onSelectStore}
      onPress={() => setPickerVisible(true)}
      style={styles.storeChip}
    >
      <MaterialCommunityIcons name="storefront-outline" size={16} color={HB_COLORS.textSecondary} />
      <Text style={styles.storeChipText} numberOfLines={1}>
        {storeLabel || (storesLoading ? t("store.loading") : "—")}
      </Text>
      {stores.length > 1 && onSelectStore ? <MaterialCommunityIcons name="chevron-down" size={16} color={HB_COLORS.textSecondary} /> : null}
    </Pressable>
  );

  return (
    <HoursFrame onBack={onBack} middle={<HoursTitle title={t("title")} />} right={storeChip}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={manualRefreshing} onRefresh={() => void onRefresh()} />}
        showsVerticalScrollIndicator={false}
      >
        <View style={styles.periodRow}>
          <PeriodStepper
            report={report}
            weeks={weeks}
            language={language}
            t={t}
            canStep={settled}
            canNext={Boolean(report && report.periodEnd < today)}
            onStep={step}
          />
          <WeeksSegment
            value={weeks}
            onChange={changeWeeks}
            labels={{ one: t("period.oneWeek"), two: t("period.twoWeeks") }}
            accessibilityLabel={t("period.weeksSwitch")}
          />
        </View>

        {!storeCode ? (
          storesLoading ? <HoursLoading /> : <HoursCard><Text style={styles.emptyText}>{t("messages.noStore")}</Text></HoursCard>
        ) : !report ? (
          hours.isError ? (
            <HoursError title={t("messages.loadFailed")} retryLabel={t("messages.retry")} onRetry={() => void hours.refetch()} />
          ) : (
            <HoursLoading />
          )
        ) : (
          <ReportBody
            report={report}
            t={t}
            mergeStores={mergeStores}
            onToggleMerge={setMergeStores}
            onOpenApprovals={onOpenApprovals}
            onOpenEmployee={openEmployee}
            stale={hours.isError}
            onRetry={() => void hours.refetch()}
          />
        )}
      </ScrollView>

      {onSelectStore ? (
        <StorePickerModal
          presentation="sheet"
          visible={pickerVisible}
          stores={stores}
          selectedStoreCode={storeCode}
          title={t("store.pickerTitle")}
          cancelLabel={t("store.cancel")}
          onDismiss={() => setPickerVisible(false)}
          onSelectStore={(store) => {
            setPickerVisible(false);
            if (store && store.storeCode !== storeCode) onSelectStore(store.storeCode);
          }}
        />
      ) : null}
    </HoursFrame>
  );
}

/** 周期条：「‹ 第 42 周 · 10-12 ~ 10-18 ›」；2 周时「第 41–42 周 · 10-05 ~ 10-18」。 */
function PeriodStepper({
  report,
  weeks,
  language,
  t,
  canStep,
  canNext,
  onStep,
}: {
  report?: PayrollHoursReport;
  weeks: 1 | 2;
  language: string;
  t: Translate;
  canStep: boolean;
  canNext: boolean;
  onStep: (direction: -1 | 1) => void;
}) {
  let title = "";
  if (report) {
    const numbers = periodWeekNumbers(report.periodStart, report.weeks);
    const weekText = report.weeks === 1
      ? t("period.week", { week: report.weekNumber ?? numbers.first })
      : t("period.weeks", { first: numbers.first, last: numbers.last });
    const range = t("period.range", { from: formatShortDate(report.periodStart, language), to: formatShortDate(report.periodEnd, language) });
    title = `${weekText} · ${range}`;
  }
  return (
    <View style={styles.stepper}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("period.previous")}
        disabled={!canStep}
        onPress={() => onStep(-1)}
        style={styles.stepperButton}
        hitSlop={6}
      >
        <MaterialCommunityIcons name="chevron-left" size={22} color={canStep ? HB_COLORS.textPrimary : HB_COLORS.outline} />
      </Pressable>
      <Text style={[styles.stepperTitle, MONO]} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.75} accessibilityLiveRegion="polite">
        {title || (weeks === 1 ? t("period.oneWeek") : t("period.twoWeeks"))}
      </Text>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("period.next")}
        disabled={!canStep || !canNext}
        onPress={() => onStep(1)}
        style={styles.stepperButton}
        hitSlop={6}
      >
        <MaterialCommunityIcons name="chevron-right" size={22} color={canStep && canNext ? HB_COLORS.textPrimary : HB_COLORS.outline} />
      </Pressable>
    </View>
  );
}

function ReportBody({
  report,
  t,
  mergeStores,
  onToggleMerge,
  onOpenApprovals,
  onOpenEmployee,
  stale,
  onRetry,
}: {
  report: PayrollHoursReport;
  t: Translate;
  mergeStores: boolean;
  onToggleMerge: (next: boolean) => void;
  onOpenApprovals?: () => void;
  onOpenEmployee?: (employee: PayrollHoursEmployee) => void;
  stale: boolean;
  onRetry: () => void;
}) {
  const unit = (value: number) => t("summary.hours", { value: formatHours(value) });
  return (
    <View style={styles.body}>
      <SummaryTiles
        items={[
          { label: t("summary.paid"), value: unit(report.totals.paidHours) },
          { label: t("summary.weekendOf"), value: unit(report.totals.weekendHours), color: HOURS_COLORS.tierText },
          { label: t("summary.paidLeave"), value: unit(report.totals.leaveHours) },
        ]}
      />
      {stale ? <HoursAlertBar tone="warning" icon="wifi-off" text={t("messages.loadFailed")} action={{ label: t("messages.retry"), onPress: onRetry }} /> : null}
      {report.periodStatus === "Submitted" ? <HoursAlertBar tone="neutral" icon="lock-outline" text={t("messages.submitted")} /> : null}
      <HoursAlerts
        alerts={report.alerts}
        employees={report.employees}
        pendingCount={report.totals.pendingCount}
        mergeStores={mergeStores}
        onToggleMerge={onToggleMerge}
        onOpenApprovals={onOpenApprovals}
      />
      {report.employees.length === 0 ? (
        <HoursCard>
          <Text style={styles.emptyText}>{t("messages.empty")}</Text>
        </HoursCard>
      ) : (
        report.employees.map((employee) => (
          <HoursEmployeeCard key={employee.userGuid} employee={employee} periodStart={report.periodStart} weeks={report.weeks} onPress={onOpenEmployee} />
        ))
      )}
      {report.employees.length > 0 ? <Legend t={t} /> : null}
    </View>
  );
}

/** 图例：格子颜色与角标的含义。 */
function Legend({ t }: { t: Translate }) {
  const swatches: { key: string; label: string; style: object; hatch?: boolean }[] = [
    { key: "weekend", label: t("legend.weekend"), style: styles.swatchWeekend },
    { key: "pending", label: t("legend.pending"), style: styles.swatchPending },
    { key: "annual", label: t("legend.annual"), style: styles.swatchAnnual },
    { key: "sick", label: t("legend.sick"), style: styles.swatchSick },
    { key: "unavailable", label: t("legend.unavailable"), style: styles.swatchUnavailable, hatch: true },
    { key: "rest", label: t("legend.rest"), style: styles.swatchRest },
  ];
  return (
    <View style={styles.legend} accessibilityLabel={t("legend.title")}>
      {swatches.map((item) => (
        <View key={item.key} style={styles.legendItem}>
          <View style={[styles.swatch, item.style]}>{item.hatch ? <Hatch /> : null}</View>
          <Text style={styles.legendText}>{item.label}</Text>
        </View>
      ))}
      <View style={styles.legendItem}>
        <View style={styles.legendCashOut}>
          <Text style={styles.legendCashOutText}>{t("cell.cashOutBadge")}</Text>
        </View>
        <Text style={styles.legendText}>{t("legend.cashOut")}</Text>
      </View>
      <View style={styles.legendItem}>
        <Text style={styles.legendEmoji}>🎂</Text>
        <Text style={styles.legendText}>{t("legend.birthday")}</Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  content: { padding: HB_SPACING.sm, paddingBottom: HB_SPACING.xl },
  storeChip: {
    flexDirection: "row",
    alignItems: "center",
    maxWidth: 170,
    height: 34,
    paddingHorizontal: 10,
    borderRadius: 17,
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outline,
    borderWidth: 1,
    gap: 4,
  },
  storeChipText: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary, flexShrink: 1 },
  periodRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, marginBottom: 10 },
  stepper: {
    flex: 1,
    minWidth: 0,
    flexDirection: "row",
    alignItems: "center",
    height: 40,
    backgroundColor: HB_COLORS.white,
    borderColor: HB_COLORS.outlineMuted,
    borderWidth: 1,
    borderRadius: 10,
  },
  stepperButton: { width: 32, height: 40, alignItems: "center", justifyContent: "center" },
  stepperTitle: { flex: 1, textAlign: "center", fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  body: { gap: 10 },
  emptyText: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center", paddingVertical: HB_SPACING.md },
  legend: { flexDirection: "row", flexWrap: "wrap", columnGap: 12, rowGap: 6, paddingHorizontal: 4, paddingTop: 2 },
  legendItem: { flexDirection: "row", alignItems: "center", gap: 4 },
  legendText: { fontSize: 11.5, color: HB_COLORS.textSecondary },
  swatch: { width: 14, height: 14, borderRadius: 4, borderWidth: 1, overflow: "hidden" },
  swatchWeekend: { backgroundColor: HOURS_COLORS.weekendBg, borderColor: HOURS_COLORS.weekendBorder },
  swatchPending: { backgroundColor: HOURS_COLORS.pendingBg, borderColor: HOURS_COLORS.pendingBorder, borderStyle: "dashed" },
  swatchAnnual: { backgroundColor: HOURS_COLORS.annualBg, borderColor: HOURS_COLORS.annualBorder },
  swatchSick: { backgroundColor: HOURS_COLORS.sickBg, borderColor: HOURS_COLORS.sickBorder },
  swatchUnavailable: { backgroundColor: HOURS_COLORS.hatchBg, borderColor: HOURS_COLORS.cellBorder },
  swatchRest: { backgroundColor: "transparent", borderColor: HOURS_COLORS.restBorder, borderStyle: "dashed" },
  legendCashOut: {
    minWidth: 14,
    height: 14,
    paddingHorizontal: 2,
    borderRadius: 3,
    borderWidth: 1,
    borderColor: HOURS_COLORS.cashOutBorder,
    backgroundColor: HOURS_COLORS.cashOutBg,
    alignItems: "center",
    justifyContent: "center",
  },
  legendCashOutText: { fontSize: 9, fontWeight: "700", color: HOURS_COLORS.cashOutText, lineHeight: 11 },
  legendEmoji: { fontSize: 11 },
});
