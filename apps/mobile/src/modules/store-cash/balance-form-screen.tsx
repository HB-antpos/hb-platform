// 期初现金 / 盘点表单（同一屏，由路由参数 kind 区分）。
// 期初：只有 summary.openingMissing 时才可录入（金额 ≥ 0，日期 ≤ 门店今天）。
// 盘点：任意时刻可录，提交后展示与应有余额的差异（difference 可能为 null）。
import { useCallback, useMemo, useRef, useState } from "react";
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, View } from "react-native";
import { useQuery } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter, type Href } from "expo-router";
import { ActivityIndicator, Button, HelperText, Text, TextInput } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { useCartStore } from "@/store/cart-store";
import { canViewStoreCash } from "./access";
import { createCashCount, fetchCashSummary, putCashOpening } from "./api";
import {
  buildCreateCountRequest,
  buildSetOpeningRequest,
  canRecordOpening,
  createInitialBalance,
  describeCountOutcome,
  resolveBalanceDateRange,
  validateBalanceDraft,
  type BalanceDraft,
  type BalanceKind,
} from "./balance-form";
import { createRuntimeClientRequestIdHolder } from "./client-request-id-runtime";
import { resolveEntryDateRange } from "./dates";
import { resolveCashErrorMessage } from "./errors";
import { balanceIssueKey } from "./issue-messages";
import { formatAud, formatSignedAud } from "./money";
import { storeCashKeys } from "./query-keys";
import { firstParam, parseBalanceKind } from "./routes";
import { resolveActiveStore } from "./store-selection";
import type { CashBalanceEntry, CashContext, CashStoreOption } from "./types";
import { useLatestState } from "./use-latest-state";
import { useLeaveGuard } from "./use-leave-guard";
import { useSubmitLock } from "./use-submit-lock";
import { useCashContextQuery, useInvalidateCashData } from "./use-store-cash";
import { CashCard, CashDateField, CashScreenFrame, MoneyInput, NoticeBanner, ValueRow, cashUiStyles } from "./ui";

export function BalanceFormScreen() {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const router = useRouter();
  const params = useLocalSearchParams<{ storeCode?: string | string[]; kind?: string | string[] }>();
  const kind = parseBalanceKind(params.kind);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const isReview = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const globalStoreCode = useCartStore((state) => state.selectedStore?.storeCode);
  const allowed = canViewStoreCash(isAuthenticated, hasPermission, isReview);
  const contextQuery = useCashContextQuery(allowed);
  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/store-cash" as Href);
  }, [router]);
  const title = kind === "opening" ? t("balance.openingTitle") : t("balance.countTitle");
  const context = contextQuery.data;
  const store = context ? resolveActiveStore(context.stores, firstParam(params.storeCode), globalStoreCode) : null;

  const state = (text: string, extra?: { loading?: boolean; actionLabel?: string; onAction?: () => void }) => (
    <CashScreenFrame title={title} onBack={goBack}>
      <View style={styles.center}>
        {extra?.loading ? <ActivityIndicator /> : null}
        <Text style={styles.message}>{text}</Text>
        {extra?.actionLabel && extra.onAction ? (
          <Button mode="outlined" onPress={extra.onAction}>{extra.actionLabel}</Button>
        ) : null}
      </View>
    </CashScreenFrame>
  );

  if (!allowed) return state(t("states.notAllowed"));
  if (contextQuery.isPending) return state(t("states.loading"), { loading: true });
  if (contextQuery.isError || !context) {
    return state(resolveCashErrorMessage(contextQuery.error, { t, language, fallbackKey: "storeCash:states.loadFailed" }), {
      actionLabel: t("common:actions.retry"),
      onAction: () => void contextQuery.refetch(),
    });
  }
  if (!store) return state(t("states.noStores"));
  if (!context.capabilities.canCreateDeposit) return state(t("states.noDepositPermission"));
  return <BalanceForm key={`${store.storeCode}:${kind}`} kind={kind} context={context} store={store} onBack={goBack} />;
}

function BalanceForm({
  kind,
  context,
  store,
  onBack,
}: {
  kind: BalanceKind;
  context: CashContext;
  store: CashStoreOption;
  onBack: () => void;
}) {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const invalidate = useInvalidateCashData();
  const submitLock = useSubmitLock();
  const requestIdHolder = useRef(createRuntimeClientRequestIdHolder()).current;
  const allowLeaveRef = useRef(false);
  const initialSnapshot = useRef(JSON.stringify(createInitialBalance(kind, store.storeToday))).current;
  const draftState = useLatestState<BalanceDraft>(createInitialBalance(kind, store.storeToday));
  const draft = draftState.state as BalanceDraft;
  const [showErrors, setShowErrors] = useState(false);
  const [submitError, setSubmitError] = useState("");
  const [saved, setSaved] = useState<CashBalanceEntry | null>(null);

  // 是否已有有效期初要以服务端为准：缺期初才允许录入
  const summaryQuery = useQuery({
    queryKey: storeCashKeys.summary(store.storeCode),
    queryFn: () => fetchCashSummary(store.storeCode),
  });
  // 摘要读取失败时无法确认，放行让用户尝试——「每店只能有一条有效期初」由服务端以 409 兜底
  const openingAllowed =
    kind !== "opening" ||
    summaryQuery.isError ||
    (summaryQuery.data ? canRecordOpening(summaryQuery.data.openingMissing, context.capabilities.canCreateDeposit) : false);

  const range = useMemo(
    () =>
      resolveBalanceDateRange(kind, {
        storeToday: store.storeToday,
        entryRange: resolveEntryDateRange({
          storeToday: store.storeToday,
          maxBackfillDays: context.maxBackfillDays,
          canViewAllStores: context.capabilities.canViewAllStores,
        }),
      }),
    [context, kind, store.storeToday],
  );
  const issues = useMemo(() => validateBalanceDraft(draft, range), [draft, range]);
  const dirty = saved == null && JSON.stringify(draft) !== initialSnapshot;
  useLeaveGuard({ dirty, busy: submitLock.busy, allowLeaveRef });

  const issueMessage = (predicate: (issue: (typeof issues)[number]) => boolean) => {
    const issue = issues.find(predicate);
    return issue && showErrors ? t(balanceIssueKey(issue), { days: context.maxBackfillDays }) : undefined;
  };

  const handleSubmit = () =>
    submitLock.run(async () => {
      const current = draftState.ref.current;
      if (!current) return;
      setShowErrors(true);
      setSubmitError("");
      if (validateBalanceDraft(current, range).length > 0) {
        setSubmitError(t("errors.fixFirst"));
        return;
      }
      const options = { clientRequestId: requestIdHolder.current(), storeCode: store.storeCode };
      const request = kind === "opening" ? buildSetOpeningRequest(current, options) : buildCreateCountRequest(current, options);
      if (!request) {
        setSubmitError(t("errors.generic"));
        return;
      }
      try {
        const entry = kind === "opening" ? await putCashOpening(request) : await createCashCount(request);
        requestIdHolder.rotate();
        allowLeaveRef.current = true;
        setSaved(entry);
        void invalidate();
      } catch (error) {
        // 失败不换请求号：弱网下请求可能已落库，重试沿用同一个号由服务端去重
        setSubmitError(`${resolveCashErrorMessage(error, { t, language })} ${t("errors.retryKeepsData")}`.trim());
      }
    });

  if (kind === "opening" && summaryQuery.isPending) {
    return (
      <CashScreenFrame title={t("balance.openingTitle")} onBack={onBack}>
        <View style={styles.center}>
          <ActivityIndicator />
          <Text style={styles.message}>{t("states.loading")}</Text>
        </View>
      </CashScreenFrame>
    );
  }

  const title = kind === "opening" ? t("balance.openingTitle") : t("balance.countTitle");

  if (saved) {
    const outcome = describeCountOutcome(saved);
    return (
      <CashScreenFrame
        title={title}
        onBack={onBack}
        footer={<Button mode="contained" onPress={onBack} contentStyle={styles.submitContent}>{t("balance.done")}</Button>}
      >
        <ScrollView style={cashUiStyles.scroll} contentContainerStyle={cashUiStyles.content}>
          <NoticeBanner
            tone="success"
            icon="check-circle-outline"
            text={kind === "opening" ? t("balance.openingSaved") : t("balance.countSaved")}
          />
          <CashCard>
            <ValueRow label={t("balance.entryDate")} value={saved.entryDate} />
            <ValueRow label={kind === "opening" ? t("balance.openingAmount") : t("balance.countAmount")} value={formatAud(saved.amount)} strong />
            {kind === "count" ? (
              <>
                {saved.expectedAmount != null ? (
                  <ValueRow label={t("balance.expected")} value={formatAud(saved.expectedAmount)} />
                ) : null}
                {outcome.kind === "unknown" ? (
                  <Text style={styles.hint}>{t("balance.differenceUnknown")}</Text>
                ) : (
                  <>
                    <ValueRow
                      label={t("balance.difference")}
                      value={formatSignedAud(outcome.difference)}
                      valueStyle={outcome.kind === "balanced" ? styles.balanced : styles.unbalanced}
                    />
                    <Text style={styles.hint}>{t(`balance.outcome.${outcome.kind}`)}</Text>
                  </>
                )}
              </>
            ) : null}
          </CashCard>
        </ScrollView>
      </CashScreenFrame>
    );
  }

  if (!openingAllowed) {
    return (
      <CashScreenFrame title={title} onBack={onBack}>
        <View style={styles.center}>
          <Text style={styles.message}>{t("balance.openingExists")}</Text>
          <Button mode="outlined" onPress={onBack}>{t("common:actions.back")}</Button>
        </View>
      </CashScreenFrame>
    );
  }

  const footer = (
    <>
      {submitError ? <NoticeBanner tone="danger" icon="alert-circle-outline" text={submitError} /> : null}
      <Button
        mode="contained"
        onPress={() => void handleSubmit()}
        loading={submitLock.busy}
        disabled={submitLock.busy}
        contentStyle={styles.submitContent}
      >
        {kind === "opening" ? t("balance.submitOpening") : t("balance.submitCount")}
      </Button>
    </>
  );

  return (
    <CashScreenFrame title={title} onBack={onBack} footer={footer}>
      <KeyboardAvoidingView style={cashUiStyles.scroll} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView style={cashUiStyles.scroll} contentContainerStyle={cashUiStyles.content} keyboardShouldPersistTaps="handled">
          <Text style={styles.storeLine}>{t("deposit.store", { store: store.storeName || store.storeCode })}</Text>
          <NoticeBanner
            tone="info"
            icon="information-outline"
            text={kind === "opening" ? t("balance.openingHint") : t("balance.countHint")}
          />
          <CashCard>
            <CashDateField
              label={kind === "opening" ? t("balance.openingDate") : t("balance.countDate")}
              value={draft.entryDate}
              onChange={(value) => value && draftState.update((current) => ({ ...current, entryDate: value }))}
              minDate={range.min}
              maxDate={range.max}
              error={issueMessage((issue) => issue.code === "dateInvalid" || issue.code === "dateOutOfRange")}
            />
            <MoneyInput
              label={kind === "opening" ? t("balance.openingAmount") : t("balance.countAmount")}
              value={draft.amountText}
              disabled={submitLock.busy}
              onChange={(text) => draftState.update((current) => ({ ...current, amountText: text }))}
              error={issueMessage((issue) => issue.code === "amount")}
            />
            <TextInput
              mode="outlined"
              label={t("balance.note")}
              value={draft.note}
              disabled={submitLock.busy}
              onChangeText={(text) => draftState.update((current) => ({ ...current, note: text }))}
              multiline
              numberOfLines={2}
              maxLength={500}
              style={styles.input}
            />
            {issueMessage((issue) => issue.code === "noteTooLong") ? (
              <HelperText type="error" visible>{issueMessage((issue) => issue.code === "noteTooLong")}</HelperText>
            ) : null}
          </CashCard>
        </ScrollView>
      </KeyboardAvoidingView>
    </CashScreenFrame>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
  message: { color: HB_COLORS.textPrimary, textAlign: "center" },
  storeLine: { color: HB_COLORS.textSecondary, fontSize: 13 },
  hint: { color: HB_COLORS.textSecondary, fontSize: 13, lineHeight: 19 },
  input: { backgroundColor: HB_COLORS.white },
  submitContent: { minHeight: 48 },
  balanced: { color: HB_COLORS.success },
  unbalanced: { color: HB_COLORS.danger },
});
