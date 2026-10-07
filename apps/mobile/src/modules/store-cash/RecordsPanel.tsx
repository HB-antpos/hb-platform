// 记录：存款 / 支出 / 期初与盘点三个列表。已作废的记录显示作废样式；T2 的可见范围完全由服务端过滤，
// 客户端不做任何额外过滤，也不提示「有隐藏的 T2」。
import { useMemo, useState, type ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useInfiniteQuery, useMutation, useQuery } from "@tanstack/react-query";
import { useRouter, type Href } from "expo-router";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import {
  fetchCashDeposits,
  fetchCashEntries,
  fetchCashExpenses,
  voidCashEntry,
} from "./api";
import { CASH_EXPENSE_CATEGORIES } from "./constants";
import { formatUtcInZone } from "./dates";
import { resolveCashErrorMessage } from "./errors";
import { categoryLabel } from "./labels";
import { formatAud, formatSignedAud } from "./money";
import { storeCashKeys } from "./query-keys";
import {
  buildRecordsListParams,
  DEFAULT_RECORDS_FILTER,
  getNextOffset,
  mergeUniqueByKey,
  type RecordsFilter,
  type RecordsKind,
  type RecordsRangePreset,
} from "./records";
import { buildRecordDetailHref } from "./routes";
import type {
  CashBalanceEntry,
  CashDepositListItem,
  CashExpenseListItem,
  CashPaged,
  CashStoreOption,
} from "./types";
import { useInvalidateCashData } from "./use-store-cash";
import { VoidReasonDialog } from "./VoidReasonDialog";
import { CashCard, ChoiceChip, StatusChip } from "./ui";

const KINDS: RecordsKind[] = ["deposits", "expenses", "entries"];
const RANGES: RecordsRangePreset[] = ["last30", "last90", "all"];

export function RecordsPanel({ store }: { store: CashStoreOption }) {
  const { t } = useAppTranslation(["storeCash", "common"]);
  const [kind, setKind] = useState<RecordsKind>("deposits");
  const [filter, setFilter] = useState<RecordsFilter>(DEFAULT_RECORDS_FILTER);

  return (
    <View style={styles.stack}>
      <View style={styles.chips}>
        {KINDS.map((item) => (
          <ChoiceChip key={item} label={t(`records.kinds.${item}`)} selected={kind === item} onPress={() => setKind(item)} />
        ))}
      </View>

      <CashCard>
        {kind !== "entries" ? (
          <View style={styles.chips}>
            {RANGES.map((preset) => (
              <ChoiceChip
                key={preset}
                label={t(`records.ranges.${preset}`)}
                selected={filter.range === preset}
                onPress={() => setFilter((current) => ({ ...current, range: preset }))}
              />
            ))}
          </View>
        ) : null}
        {kind === "expenses" ? (
          <View style={styles.chips}>
            <ChoiceChip
              label={t("records.allCategories")}
              selected={filter.category === "all"}
              onPress={() => setFilter((current) => ({ ...current, category: "all" }))}
            />
            {CASH_EXPENSE_CATEGORIES.map((category) => (
              <ChoiceChip
                key={category}
                label={categoryLabel(t, category)}
                selected={filter.category === category}
                onPress={() => setFilter((current) => ({ ...current, category }))}
              />
            ))}
          </View>
        ) : null}
        <View style={styles.chips}>
          <ChoiceChip
            label={t("records.includeVoided")}
            selected={filter.includeVoided}
            onPress={() => setFilter((current) => ({ ...current, includeVoided: !current.includeVoided }))}
          />
        </View>
      </CashCard>

      {kind === "deposits" ? <DepositList store={store} filter={filter} /> : null}
      {kind === "expenses" ? <ExpenseList store={store} filter={filter} /> : null}
      {kind === "entries" ? <EntryList store={store} includeVoided={filter.includeVoided} /> : null}
    </View>
  );
}

// ───────────────────────── 通用：分页列表外壳 ─────────────────────────

function PagedListShell<T>({
  query,
  items,
  emptyKey,
  renderItem,
}: {
  query: {
    isPending: boolean;
    isError: boolean;
    error: unknown;
    refetch: () => unknown;
    hasNextPage: boolean;
    isFetchingNextPage: boolean;
    fetchNextPage: () => unknown;
  };
  items: T[];
  emptyKey: string;
  renderItem: (item: T) => ReactNode;
}) {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  if (query.isPending) {
    return (
      <View style={styles.center}>
        <ActivityIndicator />
        <Text style={styles.muted}>{t("states.loading")}</Text>
      </View>
    );
  }
  if (query.isError) {
    return (
      <View style={styles.center}>
        <Text style={styles.errorText}>{resolveCashErrorMessage(query.error, { t, language })}</Text>
        <Button mode="outlined" onPress={() => void query.refetch()}>{t("common:actions.retry")}</Button>
      </View>
    );
  }
  if (items.length === 0) {
    return (
      <View style={styles.center}>
        <Text style={styles.muted}>{t(emptyKey)}</Text>
      </View>
    );
  }
  return (
    <View style={styles.stack}>
      {items.map(renderItem)}
      {query.hasNextPage ? (
        <Button
          mode="outlined"
          onPress={() => void query.fetchNextPage()}
          loading={query.isFetchingNextPage}
          disabled={query.isFetchingNextPage}
          contentStyle={styles.buttonContent}
        >
          {t("records.loadMore")}
        </Button>
      ) : (
        <Text style={styles.endText}>{t("records.end")}</Text>
      )}
    </View>
  );
}

function useFlattened<T>(
  pages: readonly CashPaged<T>[] | undefined,
  getKey: (item: T) => string,
): T[] {
  return useMemo(() => mergeUniqueByKey((pages ?? []).map((page) => page.items), getKey), [getKey, pages]);
}

const depositKey = (item: CashDepositListItem) => item.depositGuid;
const expenseKey = (item: CashExpenseListItem) => item.expenseGuid;

// ───────────────────────── 存款 ─────────────────────────

function DepositList({ store, filter }: { store: CashStoreOption; filter: RecordsFilter }) {
  const { t } = useAppTranslation("storeCash");
  const router = useRouter();
  const query = useInfiniteQuery({
    queryKey: storeCashKeys.deposits(store.storeCode, filter),
    initialPageParam: 0,
    queryFn: ({ pageParam }) =>
      fetchCashDeposits(buildRecordsListParams(store.storeCode, store.storeToday, filter, pageParam)),
    getNextPageParam: (last, all) =>
      getNextOffset(all.reduce((count, page) => count + page.items.length, 0), last.total),
  });
  const items = useFlattened(query.data?.pages, depositKey);
  return (
    <PagedListShell
      query={query}
      items={items}
      emptyKey="records.emptyDeposits"
      renderItem={(item) => {
        const voided = item.status === "Voided";
        return (
          <Pressable
            key={item.depositGuid}
            accessibilityRole="button"
            accessibilityLabel={`${item.depositDate} ${formatAud(item.totalAmount)}`}
            onPress={() => router.push(buildRecordDetailHref("deposit", item.depositGuid, store.storeCode) as Href)}
            style={({ pressed }) => [styles.record, voided ? styles.recordVoided : null, pressed ? styles.pressed : null]}
          >
            <View style={styles.recordTop}>
              <Text style={[styles.recordDate, voided ? styles.voidedText : null]}>{item.depositDate}</Text>
              <Text style={[styles.recordAmount, voided ? styles.voidedAmount : null]}>{formatAud(item.totalAmount)}</Text>
            </View>
            <View style={styles.chips}>
              {voided ? <StatusChip tone="danger" label={t("records.voided")} /> : null}
              <StatusChip tone="neutral" label={t("records.depositMeta", { slips: item.slipCount, images: item.imageCount })} />
            </View>
            {item.coveredFromDate && item.coveredToDate ? (
              <Text style={styles.caption}>{t("records.covered", { from: item.coveredFromDate, to: item.coveredToDate })}</Text>
            ) : null}
            <Text style={styles.caption}>
              {t("records.createdBy", {
                name: item.createdByName ?? "--",
                time: formatUtcInZone(item.createdAtUtc, store.timeZoneId),
              })}
            </Text>
            {item.note ? <Text style={styles.caption} numberOfLines={2}>{item.note}</Text> : null}
            <MaterialCommunityIcons name="chevron-right" size={22} color={HB_COLORS.textSecondary} style={styles.chevron} />
          </Pressable>
        );
      }}
    />
  );
}

// ───────────────────────── 支出 ─────────────────────────

function ExpenseList({ store, filter }: { store: CashStoreOption; filter: RecordsFilter }) {
  const { t } = useAppTranslation("storeCash");
  const router = useRouter();
  const query = useInfiniteQuery({
    queryKey: storeCashKeys.expenses(store.storeCode, filter),
    initialPageParam: 0,
    queryFn: ({ pageParam }) =>
      fetchCashExpenses(buildRecordsListParams(store.storeCode, store.storeToday, filter, pageParam)),
    getNextPageParam: (last, all) =>
      getNextOffset(all.reduce((count, page) => count + page.items.length, 0), last.total),
  });
  const items = useFlattened(query.data?.pages, expenseKey);
  return (
    <PagedListShell
      query={query}
      items={items}
      emptyKey="records.emptyExpenses"
      renderItem={(item) => {
        const voided = item.status === "Voided";
        return (
          <Pressable
            key={item.expenseGuid}
            accessibilityRole="button"
            accessibilityLabel={`${item.expenseDate} ${categoryLabel(t, item.category)} ${formatAud(item.amount)}`}
            onPress={() => router.push(buildRecordDetailHref("expense", item.expenseGuid, store.storeCode) as Href)}
            style={({ pressed }) => [styles.record, voided ? styles.recordVoided : null, pressed ? styles.pressed : null]}
          >
            <View style={styles.recordTop}>
              <Text style={[styles.recordDate, voided ? styles.voidedText : null]}>{item.expenseDate}</Text>
              <Text style={[styles.recordAmount, voided ? styles.voidedAmount : null]}>{formatAud(item.amount)}</Text>
            </View>
            <View style={styles.chips}>
              <StatusChip tone="info" label={categoryLabel(t, item.category)} />
              {voided ? <StatusChip tone="danger" label={t("records.voided")} /> : null}
              {item.imageCount > 0 ? <StatusChip tone="neutral" label={t("records.images", { count: item.imageCount })} /> : null}
            </View>
            {item.payeeName ? <Text style={styles.caption}>{t("records.payee", { name: item.payeeName })}</Text> : null}
            {item.note ? <Text style={styles.caption} numberOfLines={2}>{item.note}</Text> : null}
            <Text style={styles.caption}>
              {t("records.createdBy", {
                name: item.createdByName ?? "--",
                time: formatUtcInZone(item.createdAtUtc, store.timeZoneId),
              })}
            </Text>
            <MaterialCommunityIcons name="chevron-right" size={22} color={HB_COLORS.textSecondary} style={styles.chevron} />
          </Pressable>
        );
      }}
    />
  );
}

// ───────────────────────── 期初与盘点 ─────────────────────────

function EntryList({ store, includeVoided }: { store: CashStoreOption; includeVoided: boolean }) {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const invalidate = useInvalidateCashData();
  const [voidTarget, setVoidTarget] = useState<CashBalanceEntry | null>(null);
  const [voidError, setVoidError] = useState("");
  const query = useQuery({
    queryKey: storeCashKeys.entries(store.storeCode, includeVoided),
    queryFn: () => fetchCashEntries(store.storeCode, includeVoided),
  });
  const mutation = useMutation({
    mutationFn: (input: { entryGuid: string; reason: string }) =>
      voidCashEntry(input.entryGuid, { reason: input.reason }),
    onSuccess: async () => {
      setVoidTarget(null);
      setVoidError("");
      await invalidate();
    },
    onError: (cause) => setVoidError(resolveCashErrorMessage(cause, { t, language })),
  });

  const entries = useMemo(
    () => [...(query.data ?? [])].sort((a, b) => (a.entryDate < b.entryDate ? 1 : a.entryDate > b.entryDate ? -1 : 0)),
    [query.data],
  );

  return (
    <>
      <PagedListShell
        query={{
          isPending: query.isPending,
          isError: query.isError,
          error: query.error,
          refetch: query.refetch,
          hasNextPage: false,
          isFetchingNextPage: false,
          fetchNextPage: () => undefined,
        }}
        items={entries}
        emptyKey="records.emptyEntries"
        renderItem={(entry) => {
          const voided = entry.status === "Voided";
          return (
            <View key={entry.entryGuid} style={[styles.record, voided ? styles.recordVoided : null]}>
              <View style={styles.recordTop}>
                <Text style={[styles.recordDate, voided ? styles.voidedText : null]}>{entry.entryDate}</Text>
                <Text style={[styles.recordAmount, voided ? styles.voidedAmount : null]}>{formatAud(entry.amount)}</Text>
              </View>
              <View style={styles.chips}>
                <StatusChip tone="info" label={t(`records.entryTypes.${entry.entryType}`)} />
                {voided ? <StatusChip tone="danger" label={t("records.voided")} /> : null}
              </View>
              {entry.entryType === "Count" && entry.difference != null ? (
                <Text style={styles.caption}>
                  {t("records.countDifference", {
                    expected: formatAud(entry.expectedAmount),
                    difference: formatSignedAud(entry.difference),
                  })}
                </Text>
              ) : null}
              {entry.note ? <Text style={styles.caption} numberOfLines={2}>{entry.note}</Text> : null}
              <Text style={styles.caption}>
                {t("records.createdBy", {
                  name: entry.createdByName ?? "--",
                  time: formatUtcInZone(entry.createdAtUtc, store.timeZoneId),
                })}
              </Text>
              {entry.canVoid && !voided ? (
                <View style={styles.entryActions}>
                  <Button
                    compact
                    textColor={HB_COLORS.danger}
                    onPress={() => {
                      setVoidError("");
                      setVoidTarget(entry);
                    }}
                  >
                    {t("void.action")}
                  </Button>
                </View>
              ) : null}
            </View>
          );
        }}
      />
      <VoidReasonDialog
        visible={voidTarget != null}
        title={t("void.entryTitle")}
        description={
          voidTarget
            ? t("void.entryDescription", {
                type: t(`records.entryTypes.${voidTarget.entryType}`),
                date: voidTarget.entryDate,
                amount: formatAud(voidTarget.amount),
              })
            : ""
        }
        busy={mutation.isPending}
        errorMessage={voidError}
        onCancel={() => setVoidTarget(null)}
        onConfirm={(reason) => voidTarget && mutation.mutate({ entryGuid: voidTarget.entryGuid, reason })}
      />
    </>
  );
}

const styles = StyleSheet.create({
  stack: { gap: HB_SPACING.sm },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  center: { alignItems: "center", justifyContent: "center", padding: 40, gap: 12 },
  muted: { color: HB_COLORS.textSecondary, textAlign: "center" },
  errorText: { color: HB_COLORS.danger, textAlign: "center" },
  endText: { color: HB_COLORS.textSecondary, textAlign: "center", fontSize: 12, paddingVertical: HB_SPACING.xs },
  buttonContent: { minHeight: 44 },
  record: {
    backgroundColor: HB_COLORS.white,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.md,
    paddingRight: HB_SPACING.xl,
    gap: 6,
    minHeight: 72,
  },
  recordVoided: { backgroundColor: HB_COLORS.surfaceMuted },
  pressed: { opacity: 0.85 },
  recordTop: { flexDirection: "row", alignItems: "baseline", justifyContent: "space-between", gap: HB_SPACING.sm },
  recordDate: { color: HB_COLORS.textPrimary, fontSize: 16, fontWeight: "700", fontVariant: ["tabular-nums"] },
  recordAmount: { color: HB_COLORS.textPrimary, fontSize: 17, fontWeight: "700", fontVariant: ["tabular-nums"] },
  voidedText: { color: HB_COLORS.textSecondary },
  voidedAmount: { color: HB_COLORS.textSecondary, textDecorationLine: "line-through" },
  caption: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  chevron: { position: "absolute", right: HB_SPACING.xs, top: "50%", marginTop: -11 },
  entryActions: { flexDirection: "row", justifyContent: "flex-end" },
});
