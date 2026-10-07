// 现金池总览：余额、期初引导、存款提醒、支出分类、最近盘点与快捷操作。
// 日结未接入时余额是 null，必须显示「暂不可算」而不是 $0.00；其余功能不受影响。
import { StyleSheet, View } from "react-native";
import { useQuery } from "@tanstack/react-query";
import { useRouter, type Href } from "expo-router";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { resolveCashActionAccess } from "./access";
import { fetchCashSummary } from "./api";
import { formatAud, formatSignedAud } from "./money";
import { describeCountOutcome } from "./balance-form";
import { categoryLabel } from "./labels";
import { orderExpenseCategoryTotals, resolveOverviewState, shouldHighlightDeposit } from "./overview-state";
import { storeCashKeys } from "./query-keys";
import { buildBalanceNewHref, buildDepositNewHref, buildExpenseNewHref } from "./routes";
import type { CashContext, CashStoreOption } from "./types";
import { resolveCashErrorMessage } from "./errors";
import { CashCard, NoticeBanner, StatusChip, ValueRow } from "./ui";

export function OverviewPanel({ context, store }: { context: CashContext; store: CashStoreOption }) {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const router = useRouter();
  const summaryQuery = useQuery({
    queryKey: storeCashKeys.summary(store.storeCode),
    queryFn: () => fetchCashSummary(store.storeCode),
  });
  const access = resolveCashActionAccess(context.capabilities);
  const go = (href: string) => router.push(href as Href);

  if (summaryQuery.isPending) {
    return (
      <View style={styles.center}>
        <ActivityIndicator />
        <Text style={styles.muted}>{t("states.loading")}</Text>
      </View>
    );
  }
  if (summaryQuery.isError || !summaryQuery.data) {
    return (
      <View style={styles.center}>
        <Text style={styles.errorText}>
          {resolveCashErrorMessage(summaryQuery.error, { t, language, fallbackKey: "storeCash:states.loadFailed" })}
        </Text>
        <Button mode="outlined" onPress={() => void summaryQuery.refetch()}>
          {t("common:actions.retry")}
        </Button>
      </View>
    );
  }

  const summary = summaryQuery.data;
  const state = resolveOverviewState(context, summary, context.capabilities);
  const categories = orderExpenseCategoryTotals(summary.expenseByCategory);
  const countOutcome = summary.lastCount ? describeCountOutcome(summary.lastCount) : null;

  return (
    <View style={styles.stack}>
      {/* 现金池余额：日结未接入或缺期初时不显示成 $0.00 */}
      <CashCard>
        <Text style={styles.heroLabel}>{t("overview.balance")}</Text>
        <Text
          style={[styles.heroValue, state.balanceAvailable ? null : styles.heroUnavailable]}
          accessibilityLabel={
            state.balanceAvailable
              ? `${t("overview.balance")} ${formatAud(summary.poolBalance)}`
              : `${t("overview.balance")} ${t("overview.balanceUnavailable")}`
          }
        >
          {state.balanceAvailable ? formatAud(summary.poolBalance) : t("overview.balanceUnavailable")}
        </Text>
        <Text style={styles.caption}>{t("overview.formula")}</Text>
        {summary.asOfDate ? <Text style={styles.caption}>{t("overview.asOf", { date: summary.asOfDate })}</Text> : null}
      </CashCard>

      {!state.dailyCloseConnected ? (
        <NoticeBanner tone="warning" icon="information-outline" text={t("overview.closeDisconnected")} />
      ) : null}

      {state.openingMissing ? (
        <NoticeBanner tone="info" icon="flag-outline" text={t("overview.openingMissing")}>
          {state.canRecordOpening ? (
            <Button
              mode="contained"
              compact
              onPress={() => go(buildBalanceNewHref(store.storeCode, "opening"))}
              style={styles.bannerButton}
            >
              {t("overview.recordOpening")}
            </Button>
          ) : null}
        </NoticeBanner>
      ) : null}

      <CashCard title={t("overview.breakdownTitle")}>
        <ValueRow
          label={
            summary.opening
              ? t("overview.opening", { date: summary.opening.entryDate })
              : t("overview.openingNone")
          }
          value={summary.opening ? formatAud(summary.opening.amount) : "--"}
        />
        <ValueRow label={t("overview.inflow")} value={formatAud(summary.inflowTotal)} />
        <ValueRow label={t("overview.depositTotal")} value={`-${formatAud(summary.depositTotal)}`} />
        <ValueRow label={t("overview.expenseTotal")} value={`-${formatAud(summary.expenseTotal)}`} />
      </CashCard>

      {/* 存款提醒：建议额 = 现金池余额（备用金已在点钱前取出） */}
      <CashCard
        title={t("overview.depositTitle")}
        right={state.depositOverdue ? <StatusChip tone="danger" label={t("overview.overdueChip")} /> : undefined}
      >
        {state.suggestedDepositAvailable ? (
          <ValueRow label={t("overview.suggested")} value={formatAud(summary.suggestedDepositAmount)} strong />
        ) : (
          <Text style={styles.caption}>
            {state.dailyCloseConnected && !state.openingMissing ? t("overview.noSuggestion") : t("overview.suggestionUnavailable")}
          </Text>
        )}
        {state.depositOverdue ? (
          <NoticeBanner
            tone="danger"
            icon="alert-circle-outline"
            text={t("overview.overdue", { days: context.depositOverdueDays })}
          />
        ) : null}
        {state.uncoveredDayCount > 0 ? (
          <Text style={styles.caption}>
            {t("overview.uncovered", {
              count: state.uncoveredDayCount,
              date: summary.oldestUncoveredDate ?? "--",
              amount: formatAud(summary.uncoveredCash),
            })}
          </Text>
        ) : null}
        <Text style={styles.caption}>
          {summary.lastDepositDate ? t("overview.lastDeposit", { date: summary.lastDepositDate }) : t("overview.noDepositYet")}
        </Text>
        {access.canDeposit ? (
          <Button
            mode={shouldHighlightDeposit(state) ? "contained" : "outlined"}
            icon="bank-transfer-in"
            contentStyle={styles.buttonContent}
            onPress={() => go(buildDepositNewHref(store.storeCode))}
          >
            {t("overview.depositAction")}
          </Button>
        ) : null}
      </CashCard>

      {state.hasMissingCloseDates ? (
        <NoticeBanner
          tone="neutral"
          icon="calendar-question"
          text={t("overview.missingClose", {
            count: summary.missingCloseDates.length,
            dates: summary.missingCloseDates.join(", "),
          })}
        />
      ) : null}

      <CashCard
        title={t("overview.expenseTitle")}
        right={<Text style={styles.cardAmount}>{formatAud(summary.expenseTotal)}</Text>}
      >
        {categories.length === 0 ? (
          <Text style={styles.caption}>{t("overview.noExpense")}</Text>
        ) : (
          categories.map((item) => (
            <ValueRow key={item.category} label={categoryLabel(t, item.category)} value={formatAud(item.amount)} />
          ))
        )}
        {access.canExpense ? (
          <Button
            mode="outlined"
            icon="cash-minus"
            contentStyle={styles.buttonContent}
            onPress={() => go(buildExpenseNewHref(store.storeCode))}
          >
            {t("overview.expenseAction")}
          </Button>
        ) : null}
      </CashCard>

      <CashCard title={t("overview.countTitle")}>
        {summary.lastCount ? (
          <>
            <ValueRow label={t("overview.countDate")} value={summary.lastCount.entryDate} />
            <ValueRow label={t("overview.countAmount")} value={formatAud(summary.lastCount.amount)} />
            {summary.lastCount.expectedAmount != null ? (
              <ValueRow label={t("overview.countExpected")} value={formatAud(summary.lastCount.expectedAmount)} />
            ) : null}
            {countOutcome && countOutcome.kind !== "unknown" ? (
              <ValueRow
                label={t("overview.countDifference")}
                value={formatSignedAud(countOutcome.difference)}
                valueStyle={countOutcome.kind === "balanced" ? styles.balanced : styles.unbalanced}
              />
            ) : null}
          </>
        ) : (
          <Text style={styles.caption}>{t("overview.noCount")}</Text>
        )}
        {access.canManageBalance ? (
          <Button
            mode="outlined"
            icon="clipboard-check-outline"
            contentStyle={styles.buttonContent}
            onPress={() => go(buildBalanceNewHref(store.storeCode, "count"))}
          >
            {t("overview.countAction")}
          </Button>
        ) : null}
      </CashCard>
    </View>
  );
}

const styles = StyleSheet.create({
  stack: { gap: HB_SPACING.sm },
  center: { alignItems: "center", justifyContent: "center", padding: 48, gap: 12 },
  muted: { color: HB_COLORS.textSecondary },
  errorText: { color: HB_COLORS.danger, textAlign: "center" },
  heroLabel: { color: HB_COLORS.textSecondary, fontSize: 13 },
  heroValue: { color: HB_COLORS.textPrimary, fontSize: 34, lineHeight: 42, fontWeight: "800", fontVariant: ["tabular-nums"] },
  heroUnavailable: { fontSize: 20, lineHeight: 28, fontWeight: "700", color: HB_COLORS.warning },
  caption: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  cardAmount: { color: HB_COLORS.textPrimary, fontWeight: "700", fontSize: 16, fontVariant: ["tabular-nums"] },
  bannerButton: { alignSelf: "flex-start" },
  buttonContent: { minHeight: 44 },
  balanced: { color: HB_COLORS.success },
  unbalanced: { color: HB_COLORS.danger },
});
