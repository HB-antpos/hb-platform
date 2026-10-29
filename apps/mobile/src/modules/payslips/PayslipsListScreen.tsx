// 「工资单」列表页（设计图 ①）：公司 / 分店 / 日期预设筛选 + 汇总条 + 每张工资单一张卡。
// 拆成两层：PayslipsListScreen 是路由外壳（登录与权限守卫、账号变化重建），PayslipsListContent 负责数据与界面。
import { useMemo, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { IconButton, Searchbar, Text } from "react-native-paper";
import { DateRangePickerModal } from "@/components/ui/DateRangePickerModal";
import { EmptyState } from "@/components/ui/EmptyState";
import { OptionPickerSheet } from "@/components/ui/OptionPickerSheet";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { useStores } from "@/modules/shop/use-stores";
import { labelColon, listSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { resolvePayslipStoreOptions } from "./access";
import { describePayslipError } from "./errors";
import { formatDate } from "./format";
import { buildRowKey, buildRowTexts, buildSummary, failedCompanies, resolveRowPlacement } from "./list-model";
import {
  DEFAULT_PAYSLIP_PRESET,
  localTodayIso,
  normalizeCustomRange,
  PAYSLIP_PRESETS,
  resolvePreset,
  type PayslipDateRange,
  type PayslipPreset,
} from "./period";
import { FilterChip, MONO, NoticeBar, PayslipErrorCard, PayslipFrame, PayslipLoading, PayslipMessage, Tag } from "./payslip-ui";
import type { PayslipListRow } from "./types";
import { usePayslipCompanies, usePayslipList } from "./use-payslips";
import { usePayslipGuard } from "./use-payslip-guard";

const ALL_COMPANIES = "__all__";
const WEEKDAYS_ZH = ["一", "二", "三", "四", "五", "六", "日"];
const WEEKDAYS_EN = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

/** 路由外壳：登录 / 权限守卫；账号变化时重建页面状态，避免上一个账号的工资单留在屏幕上。 */
export function PayslipsListScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["payslips", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const blocked = usePayslipGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  if (blocked) return <PayslipMessage message={blocked} backLabel={t("common:actions.back")} onBack={goBack} />;
  return <PayslipsListContent key={userGuid} onBack={goBack} />;
}

export function PayslipsListContent({ onBack }: { onBack: () => void }) {
  const router = useRouter();
  const { t, language } = useAppTranslation(["payslips", "common"]);
  const isAdmin = useAuthStore((state) => state.access.isAdmin);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const { stores } = useStores();
  const storeOptions = useMemo(() => resolvePayslipStoreOptions(stores, { isAdmin, hasPermission }), [hasPermission, isAdmin, stores]);

  const [preset, setPreset] = useState<PayslipPreset>(DEFAULT_PAYSLIP_PRESET);
  const [customRange, setCustomRange] = useState<PayslipDateRange | null>(null);
  const [companyGuid, setCompanyGuid] = useState<string | null>(null);
  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [onlyUnassigned, setOnlyUnassigned] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchText, setSearchText] = useState("");
  const [keyword, setKeyword] = useState("");
  const [companyPickerOpen, setCompanyPickerOpen] = useState(false);
  const [storePickerOpen, setStorePickerOpen] = useState(false);
  const [dateOpen, setDateOpen] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const range = resolvePreset(preset, localTodayIso(), customRange);
  const companiesQuery = usePayslipCompanies();
  const companies = companiesQuery.data ?? [];
  const query = usePayslipList({ connectionGuid: companyGuid, storeCode, from: range.from, to: range.to, keyword, onlyUnassigned });
  const result = query.data;
  const failed = result ? failedCompanies(result) : [];
  const summary = result ? buildSummary(result) : null;

  const companyLabel = companies.find((company) => company.connectionGuid === companyGuid)?.connectionName ?? t("list.company.all");
  const storeLabel = storeCode
    ? (() => {
        const store = storeOptions.find((item) => item.storeCode === storeCode);
        return store ? `${store.storeCode} ${store.storeName}`.trim() : storeCode;
      })()
    : t("list.store.all");
  const customLabel = customRange && preset === "custom" ? `${formatDate(customRange.from)} — ${formatDate(customRange.to)}` : t("list.preset.custom");

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await query.refreshFromSource();
    } finally {
      setRefreshing(false);
    }
  };

  const openDetail = (row: PayslipListRow) =>
    router.push({
      pathname: "/(shell)/payslips/detail",
      params: {
        connectionGuid: row.connectionGuid,
        employeeUid: row.employeeUid,
        paymentDate: row.paymentDate,
        ...(row.periodStart ? { periodStart: row.periodStart } : {}),
      },
    });

  const submitSearch = () => setKeyword(searchText.trim());
  const toggleSearch = () => {
    if (searchOpen) {
      setSearchOpen(false);
      setSearchText("");
      setKeyword("");
    } else {
      setSearchOpen(true);
    }
  };

  const showUnassignedChip = Boolean(result?.canViewAllStores) && ((result?.unassignedCount ?? 0) > 0 || onlyUnassigned);

  const header = (
    <View style={styles.headerBlock}>
      {searchOpen ? (
        <Searchbar
          autoFocus
          value={searchText}
          onChangeText={setSearchText}
          onSubmitEditing={submitSearch}
          onClearIconPress={() => {
            setSearchText("");
            setKeyword("");
          }}
          placeholder={t("list.searchPlaceholder")}
          returnKeyType="search"
          style={styles.searchbar}
        />
      ) : null}
      <View style={styles.chipRow}>
        <FilterChip
          label={companyLabel}
          dropdown
          selected={companyGuid !== null}
          onPress={() => setCompanyPickerOpen(true)}
          accessibilityLabel={t("a11y.filterChip", { label: companyLabel })}
        />
        <FilterChip
          label={storeLabel}
          dropdown
          selected={storeCode !== null}
          onPress={() => setStorePickerOpen(true)}
          accessibilityLabel={t("a11y.filterChip", { label: storeLabel })}
        />
        {showUnassignedChip ? (
          <FilterChip
            label={t("list.onlyUnassigned", { count: result?.unassignedCount ?? 0 })}
            selected={onlyUnassigned}
            onPress={() => setOnlyUnassigned((value) => !value)}
          />
        ) : null}
      </View>
      <View style={styles.chipRow}>
        {PAYSLIP_PRESETS.map((item) => {
          const label = item === "custom" ? customLabel : t(`list.preset.${item}`);
          return (
            <FilterChip
              key={item}
              label={label}
              selected={preset === item}
              onPress={() => (item === "custom" ? setDateOpen(true) : setPreset(item))}
              accessibilityLabel={item === "custom" ? t("a11y.presetChip", { label }) : label}
            />
          );
        })}
      </View>
      {summary ? (
        <View style={styles.summary}>
          <SummaryTile narrow label={t("list.summary.count")} value={t("list.summary.countValue", { count: summary.count })} />
          <SummaryTile label={t("list.summary.gross")} value={summary.grossText} />
          <SummaryTile label={t("list.summary.net")} value={summary.netText} />
        </View>
      ) : null}
      {result ? (
        <Text style={styles.basis}>{t("list.basis", { from: formatDate(result.from || range.from), to: formatDate(result.to || range.to) })}</Text>
      ) : null}
      {failed.length > 0 ? (
        <View style={styles.partial}>
          <NoticeBar
            tone="warning"
            icon="alert-outline"
            text={`${t("list.partial.title", { count: failed.length })}${labelColon(language)}${failed.map((company) => company.connectionName).join(listSeparator(language))}`}
            action={{ label: t("list.partial.retry"), onPress: () => void query.refreshFromSource() }}
          />
        </View>
      ) : null}
      {query.isError && result ? <NoticeBar tone="danger" icon="alert-circle-outline" text={t("list.error.stale")} /> : null}
    </View>
  );

  let body: "loading" | "error" | "empty" | "list" = "list";
  if (!result) body = query.isError ? "error" : "loading";
  else if (result.rows.length === 0) body = "empty";

  return (
    <PayslipFrame
      onBack={onBack}
      title={t("title")}
      right={
        <IconButton
          icon={searchOpen ? "close" : "magnify"}
          size={24}
          accessibilityLabel={searchOpen ? t("list.searchClose") : t("list.searchOpen")}
          onPress={toggleSearch}
          style={styles.headerButton}
        />
      }
    >
      <FlatList
        data={body === "list" || body === "empty" ? (result?.rows ?? []) : []}
        keyExtractor={buildRowKey}
        renderItem={({ item }) => <PayslipRowCard row={item} onPress={() => openDetail(item)} />}
        ListHeaderComponent={header}
        ListEmptyComponent={
          body === "loading" ? (
            <PayslipLoading label={t("list.loading")} />
          ) : body === "error" ? (
            <PayslipErrorCard
              title={t("list.error.title")}
              detail={describePayslipError(t, language, query.error)}
              retryLabel={t("list.error.retry")}
              onRetry={() => void query.refetch()}
            />
          ) : (
            <EmptyState title={t("list.empty.title")} description={t("list.empty.description")} />
          )
        }
        contentContainerStyle={[styles.listContent, query.isPlaceholderData && query.isFetching ? styles.dimmed : null]}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} />}
        keyboardShouldPersistTaps="handled"
      />

      <OptionPickerSheet
        visible={companyPickerOpen}
        title={t("list.company.pickerTitle")}
        cancelLabel={t("common:actions.cancel")}
        options={[{ value: ALL_COMPANIES, label: t("list.company.all") }, ...companies.map((company) => ({ value: company.connectionGuid, label: company.connectionName }))]}
        selectedValue={companyGuid ?? ALL_COMPANIES}
        onSelect={(value) => {
          setCompanyGuid(value === ALL_COMPANIES ? null : value);
          setCompanyPickerOpen(false);
        }}
        onDismiss={() => setCompanyPickerOpen(false)}
      />
      <StorePickerModal
        presentation="sheet"
        visible={storePickerOpen}
        stores={storeOptions}
        selectedStoreCode={storeCode}
        title={t("list.store.pickerTitle")}
        cancelLabel={t("common:actions.cancel")}
        includeAllOption
        allLabel={t("list.store.all")}
        onDismiss={() => setStorePickerOpen(false)}
        onSelectStore={(store) => {
          setStoreCode(store?.storeCode ?? null);
          setStorePickerOpen(false);
        }}
      />
      <DateRangePickerModal
        visible={dateOpen}
        title={t("list.customTitle")}
        value={{ from: range.from, to: range.to }}
        onApply={(next) => {
          setDateOpen(false);
          const normalized = normalizeCustomRange(next.from, next.to);
          if (normalized) {
            setCustomRange(normalized);
            setPreset("custom");
          } else {
            // 清空 = 回到默认预设。
            setCustomRange(null);
            setPreset(DEFAULT_PAYSLIP_PRESET);
          }
        }}
        onCancel={() => setDateOpen(false)}
        cancelLabel={t("common:actions.cancel")}
        clearLabel={t("common:actions.clear")}
        applyLabel={t("common:actions.apply")}
        weekdayLabels={language === "zh" ? WEEKDAYS_ZH : WEEKDAYS_EN}
        localeTag={language === "zh" ? "zh-CN" : "en-AU"}
      />
    </PayslipFrame>
  );
}

function SummaryTile({ label, value, narrow = false }: { label: string; value: string; narrow?: boolean }) {
  return (
    <View style={[styles.tile, narrow ? styles.tileNarrow : null]} accessible accessibilityLabel={`${label} ${value}`}>
      <Text style={styles.tileLabel} numberOfLines={1}>
        {label}
      </Text>
      <Text style={[styles.tileValue, MONO]} numberOfLines={1} adjustsFontSizeToFit minimumFontScale={0.7}>
        {value}
      </Text>
    </View>
  );
}

function PayslipRowCard({ row, onPress }: { row: PayslipListRow; onPress: () => void }) {
  const { t } = useAppTranslation("payslips");
  const texts = buildRowTexts(row);
  const placement = resolveRowPlacement(row);
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={t("a11y.openPayslip", { name: row.employeeName, net: texts.netText, date: texts.paymentDateText })}
      onPress={onPress}
      style={({ pressed }) => [styles.card, pressed ? styles.pressed : null]}
    >
      <View style={styles.cardTop}>
        <Text style={styles.name} numberOfLines={1}>
          {row.employeeName}
          {row.displayId ? <Text style={[styles.displayId, MONO]}>{`  ${row.displayId}`}</Text> : null}
        </Text>
        <Text style={[styles.net, MONO]}>{texts.netText}</Text>
      </View>
      <View style={styles.cardLine}>
        <View style={styles.placementLine}>
          {placement.kind === "unbound" ? <Tag label={t("list.row.unbound")} tone="warning" /> : null}
          {placement.kind === "unknown" ? <Tag label={t("list.row.unknown")} /> : null}
          {placement.kind === "stores" ? (
            <Text style={styles.meta} numberOfLines={1}>
              {placement.label}
            </Text>
          ) : null}
          {placement.kind === "stores" && placement.inferred ? <Tag label={t("list.row.inferred")} /> : null}
          <Text style={[styles.meta, MONO]}>{`·  ${texts.paymentDateText}`}</Text>
        </View>
        <Text style={[styles.meta, MONO]}>{t("list.row.gross", { amount: texts.grossText })}</Text>
      </View>
      {texts.periodText ? <Text style={[styles.meta, MONO]}>{texts.periodText}</Text> : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  headerButton: { margin: 0, width: 48, height: 48 },
  headerBlock: { gap: HB_SPACING.sm, paddingBottom: HB_SPACING.xs },
  listContent: { padding: HB_SPACING.md, paddingBottom: HB_SPACING.xl, gap: HB_SPACING.sm },
  dimmed: { opacity: 0.6 },
  searchbar: { backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, minHeight: 48 },
  chipRow: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  summary: { flexDirection: "row", gap: HB_SPACING.xs, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted },
  tile: { flex: 1, minWidth: 0, gap: 2 },
  tileNarrow: { flex: 0.7 },
  tileLabel: { fontSize: 13, color: HB_COLORS.textSecondary },
  tileValue: { fontSize: 20, lineHeight: 28, fontWeight: "700", color: HB_COLORS.textPrimary },
  basis: { fontSize: 13, color: HB_COLORS.textSecondary },
  partial: { gap: HB_SPACING.xs },
  card: { gap: 6, padding: HB_SPACING.md, borderRadius: HB_RADIUS.surface, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, minHeight: 44 },
  pressed: { opacity: 0.7 },
  cardTop: { flexDirection: "row", alignItems: "baseline", justifyContent: "space-between", gap: HB_SPACING.xs },
  name: { flex: 1, fontSize: 17, lineHeight: 24, fontWeight: "700", color: HB_COLORS.textPrimary },
  displayId: { fontSize: 13, fontWeight: "400", color: HB_COLORS.textSecondary },
  net: { fontSize: 18, lineHeight: 24, fontWeight: "700", color: HB_COLORS.success },
  cardLine: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  placementLine: { flex: 1, minWidth: 0, flexDirection: "row", alignItems: "center", gap: 6, flexWrap: "wrap" },
  meta: { fontSize: 14, lineHeight: 20, color: HB_COLORS.textSecondary },
});
