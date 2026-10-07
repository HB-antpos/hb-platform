// 按日明细：逐日列出日结现金、是否已被存款覆盖、当天支出；展开后看每台设备的日结存档并可手选纳入。
import { useMemo, useState } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQuery } from "@tanstack/react-query";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { fetchCashDaily } from "./api";
import { buildRecentRange, weekdayOf } from "./dates";
import { DeviceCloseCard } from "./DeviceCloseCard";
import { resolveCashErrorMessage } from "./errors";
import { formatAud } from "./money";
import { storeCashKeys } from "./query-keys";
import type { CashContext, CashDailyRow, CashStoreOption } from "./types";
import { CashCard, ChoiceChip, NoticeBanner, StatusChip, ValueRow } from "./ui";

const RANGE_PRESETS = [7, 14, 30, 90] as const;

export function DailyPanel({ context, store }: { context: CashContext; store: CashStoreOption }) {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const [days, setDays] = useState<number>(14);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const range = useMemo(() => buildRecentRange(store.storeToday, days), [store.storeToday, days]);

  const query = useQuery({
    queryKey: storeCashKeys.daily(store.storeCode, range.from, range.to),
    queryFn: () => fetchCashDaily(store.storeCode, range.from, range.to),
    enabled: context.dailyCloseConnected,
  });

  const toggle = (date: string) =>
    setExpanded((current) => {
      const next = new Set(current);
      if (next.has(date)) next.delete(date);
      else next.add(date);
      return next;
    });

  if (!context.dailyCloseConnected) {
    return <NoticeBanner tone="warning" icon="information-outline" text={t("daily.disconnected")} />;
  }

  return (
    <View style={styles.stack}>
      <View style={styles.chips}>
        {RANGE_PRESETS.map((preset) => (
          <ChoiceChip
            key={preset}
            label={t("daily.rangeDays", { count: preset })}
            selected={days === preset}
            onPress={() => setDays(preset)}
          />
        ))}
      </View>
      <Text style={styles.caption}>{t("daily.rangeCaption", { from: range.from, to: range.to })}</Text>

      {query.isPending ? (
        <View style={styles.center}>
          <ActivityIndicator />
          <Text style={styles.muted}>{t("states.loading")}</Text>
        </View>
      ) : query.isError ? (
        <View style={styles.center}>
          <Text style={styles.errorText}>{resolveCashErrorMessage(query.error, { t, language })}</Text>
          <Button mode="outlined" onPress={() => void query.refetch()}>{t("common:actions.retry")}</Button>
        </View>
      ) : query.data.rows.length === 0 ? (
        <View style={styles.center}>
          <Text style={styles.muted}>{t("daily.empty")}</Text>
        </View>
      ) : (
        query.data.rows.map((row) => (
          <DailyRow
            key={row.businessDate}
            row={row}
            store={store}
            canManage={context.capabilities.canCreateDeposit}
            expanded={expanded.has(row.businessDate)}
            onToggle={() => toggle(row.businessDate)}
          />
        ))
      )}
    </View>
  );
}

function DailyRow({
  row,
  store,
  canManage,
  expanded,
  onToggle,
}: {
  row: CashDailyRow;
  store: CashStoreOption;
  canManage: boolean;
  expanded: boolean;
  onToggle: () => void;
}) {
  const { t } = useAppTranslation("storeCash");
  const weekday = weekdayOf(row.businessDate);
  const staleCount = row.devices.filter((device) => device.selectionStale).length;
  return (
    <CashCard>
      <Pressable
        accessibilityRole="button"
        accessibilityState={{ expanded }}
        accessibilityLabel={`${row.businessDate} ${formatAud(row.inflowCash)}`}
        onPress={onToggle}
        style={styles.rowHeader}
      >
        <View style={styles.rowTitle}>
          <Text style={styles.date}>
            {row.businessDate}
            {weekday != null ? <Text style={styles.weekday}>{`  ${t(`weekdays.${weekday}`)}`}</Text> : null}
          </Text>
          <View style={styles.chips}>
            {!row.hasClose ? (
              <StatusChip tone="neutral" label={t("daily.noClose")} />
            ) : row.covered ? (
              <StatusChip tone="success" label={t("daily.covered")} />
            ) : (
              <StatusChip tone="warning" label={t("daily.uncovered")} />
            )}
            {staleCount > 0 ? <StatusChip tone="danger" label={t("daily.needsConfirm")} /> : null}
          </View>
        </View>
        <View style={styles.rowAmount}>
          <Text style={styles.inflow}>{formatAud(row.inflowCash)}</Text>
          <MaterialCommunityIcons
            name={expanded ? "chevron-up" : "chevron-down"}
            size={22}
            color={HB_COLORS.textSecondary}
          />
        </View>
      </Pressable>
      <ValueRow label={t("daily.expense")} value={formatAud(row.expenseTotal)} muted />
      {expanded ? (
        row.devices.length === 0 ? (
          <Text style={styles.caption}>{t("daily.noDevices")}</Text>
        ) : (
          row.devices.map((device) => (
            <DeviceCloseCard
              key={device.deviceCode}
              storeCode={store.storeCode}
              businessDate={row.businessDate}
              timeZoneId={store.timeZoneId}
              device={device}
              canManage={canManage}
            />
          ))
        )
      ) : null}
    </CashCard>
  );
}

const styles = StyleSheet.create({
  stack: { gap: HB_SPACING.sm },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  caption: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  muted: { color: HB_COLORS.textSecondary },
  errorText: { color: HB_COLORS.danger, textAlign: "center" },
  center: { alignItems: "center", justifyContent: "center", padding: 40, gap: 12 },
  rowHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm, minHeight: 52 },
  rowTitle: { flex: 1, minWidth: 0, gap: 6 },
  date: { color: HB_COLORS.textPrimary, fontSize: 16, fontWeight: "700", fontVariant: ["tabular-nums"] },
  weekday: { color: HB_COLORS.textSecondary, fontSize: 13, fontWeight: "400" },
  rowAmount: { flexDirection: "row", alignItems: "center", gap: 2 },
  inflow: { color: HB_COLORS.textPrimary, fontSize: 18, fontWeight: "700", fontVariant: ["tabular-nums"] },
});
