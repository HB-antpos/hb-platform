// 现金支出表单：类别固定四个（现金工资 / 现金购物 / T2 / 其他），录入即生效、无审核。
// 购物必须带至少 1 张收据，其他类别图片可选；工资可填收款人姓名。
import { useCallback, useMemo, useRef, useState } from "react";
import {
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  View,
  type LayoutChangeEvent,
} from "react-native";
import { useLocalSearchParams, useRouter, type Href } from "expo-router";
import { ActivityIndicator, Button, HelperText, Text, TextInput } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { useCartStore } from "@/store/cart-store";
import { canViewStoreCash } from "./access";
import { createCashExpense } from "./api";
import { createRuntimeClientRequestIdHolder } from "./client-request-id-runtime";
import { CASH_EXPENSE_CATEGORIES } from "./constants";
import { resolveEntryDateRange } from "./dates";
import { isAttachmentInvalidError, resolveCashErrorMessage } from "./errors";
import {
  appendExpensePhotos,
  buildCreateExpenseRequest,
  createInitialExpense,
  isPayeeApplicable,
  isReceiptRequired,
  patchExpensePhoto,
  removeExpensePhoto,
  resetExpensePhotos,
  validateExpenseDraft,
  type ExpenseDraft,
  type ExpenseIssue,
  type ExpenseLimits,
} from "./expense-form";
import { expenseIssueKey } from "./issue-messages";
import { categoryLabel } from "./labels";
import { formatAud, parseMoneyInput } from "./money";
import { uploadPendingPhotos } from "./photo-upload";
import { createCashPhotoUploadDeps } from "./photo-upload-runtime";
import type { PhotoDraft } from "./photo-drafts";
import { PhotoField } from "./PhotoField";
import { buildRecordDetailHref, firstParam } from "./routes";
import { resolveActiveStore } from "./store-selection";
import { runCashSubmit } from "./submit-flow";
import type { CashContext, CashStoreOption } from "./types";
import { useLatestState } from "./use-latest-state";
import { useLeaveGuard } from "./use-leave-guard";
import { useSubmitLock } from "./use-submit-lock";
import { useCashContextQuery, useInvalidateCashData } from "./use-store-cash";
import { CashCard, CashDateField, CashScreenFrame, ChoiceChip, MoneyInput, NoticeBanner, cashUiStyles } from "./ui";

export function ExpenseFormScreen() {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const router = useRouter();
  const params = useLocalSearchParams<{ storeCode?: string | string[] }>();
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
  const title = t("expense.title");
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
  if (!context.capabilities.canCreateExpense) return state(t("states.noExpensePermission"));
  return <ExpenseForm key={store.storeCode} context={context} store={store} onBack={goBack} />;
}

function sectionKeyOf(issue: ExpenseIssue): string {
  switch (issue.code) {
    case "dateInvalid":
    case "dateOutOfRange":
      return "date";
    case "categoryMissing":
      return "category";
    case "amount":
      return "amount";
    case "receiptRequired":
    case "photosTooMany":
      return "photos";
    case "payeeTooLong":
      // 收款人输入框嵌在「金额」卡片里，它的 onLayout.y 相对卡片，滚动定位到卡片顶部即可
      return "amount";
    default:
      return "note";
  }
}

function ExpenseForm({
  context,
  store,
  onBack,
}: {
  context: CashContext;
  store: CashStoreOption;
  onBack: () => void;
}) {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const router = useRouter();
  const invalidate = useInvalidateCashData();
  const submitLock = useSubmitLock();
  const requestIdHolder = useRef(createRuntimeClientRequestIdHolder()).current;
  const uploadDeps = useMemo(() => createCashPhotoUploadDeps(store.storeCode), [store.storeCode]);
  const allowLeaveRef = useRef(false);
  const scrollRef = useRef<ScrollView>(null);
  const sectionY = useRef<Record<string, number>>({});
  const initialSnapshot = useRef(JSON.stringify(createInitialExpense(store.storeToday))).current;
  const draftState = useLatestState<ExpenseDraft>(createInitialExpense(store.storeToday));
  const draft = draftState.state as ExpenseDraft;
  const [showErrors, setShowErrors] = useState(false);
  const [submitError, setSubmitError] = useState("");

  const limits: ExpenseLimits = useMemo(
    () => ({
      maxImages: context.maxImagesPerExpense,
      dateRange: resolveEntryDateRange({
        storeToday: store.storeToday,
        maxBackfillDays: context.maxBackfillDays,
        canViewAllStores: context.capabilities.canViewAllStores,
      }),
    }),
    [context, store.storeToday],
  );
  const issues = useMemo(() => validateExpenseDraft(draft, limits), [draft, limits]);
  const dirty = JSON.stringify(draft) !== initialSnapshot;
  useLeaveGuard({ dirty, busy: submitLock.busy, allowLeaveRef });

  const track = (key: string) => (event: LayoutChangeEvent) => {
    sectionY.current[key] = event.nativeEvent.layout.y;
  };
  const issueMessage = (predicate: (issue: ExpenseIssue) => boolean) => {
    const issue = issues.find(predicate);
    return issue && showErrors
      ? t(expenseIssueKey(issue), { days: context.maxBackfillDays, max: context.maxImagesPerExpense })
      : undefined;
  };

  const applyPhotoPatch = useCallback(
    (key: string, patch: Partial<Omit<PhotoDraft, "key">>) => draftState.update((current) => patchExpensePhoto(current, key, patch)),
    [draftState],
  );
  const retryPhoto = (key: string) => {
    if (submitLock.lockRef.current) return;
    const target = draftState.ref.current?.photos.find((photo) => photo.key === key);
    if (target) void uploadPendingPhotos([target], uploadDeps, applyPhotoPatch);
  };

  const handleSubmit = () =>
    submitLock.run(async () => {
      const current = draftState.ref.current;
      if (!current) return;
      setShowErrors(true);
      setSubmitError("");
      const currentIssues = validateExpenseDraft(current, limits);
      if (currentIssues.length > 0) {
        setSubmitError(t("errors.fixFirst"));
        const y = sectionY.current[sectionKeyOf(currentIssues[0])];
        if (typeof y === "number") scrollRef.current?.scrollTo({ y: Math.max(0, y - 8), animated: true });
        return;
      }
      const outcome = await runCashSubmit({
        uploadPhotos: () => uploadPendingPhotos(draftState.ref.current?.photos ?? current.photos, uploadDeps, applyPhotoPatch),
        buildRequest: () =>
          draftState.ref.current
            ? buildCreateExpenseRequest(draftState.ref.current, {
                clientRequestId: requestIdHolder.current(),
                storeCode: store.storeCode,
              })
            : null,
        send: createCashExpense,
      });
      if (outcome.status === "uploadFailed") {
        setSubmitError(t("errors.uploadFailed", { count: outcome.failedCount }));
        return;
      }
      if (outcome.status === "invalid") {
        setSubmitError(t("errors.generic"));
        return;
      }
      if (outcome.status === "error") {
        if (isAttachmentInvalidError(outcome.error)) draftState.update(resetExpensePhotos);
        setSubmitError(`${resolveCashErrorMessage(outcome.error, { t, language })} ${t("errors.retryKeepsData")}`.trim());
        return;
      }
      requestIdHolder.rotate();
      allowLeaveRef.current = true;
      void invalidate();
      router.replace(buildRecordDetailHref("expense", outcome.result.expenseGuid, store.storeCode, { created: true }) as Href);
    });

  const parsedAmount = parseMoneyInput(draft.amountText);
  const receiptRequired = isReceiptRequired(draft.category);
  const uploadedCount = draft.photos.filter((photo) => photo.status === "uploaded").length;

  const footer = (
    <>
      {submitError ? <NoticeBanner tone="danger" icon="alert-circle-outline" text={submitError} /> : null}
      <View style={styles.footerTotal}>
        <Text style={styles.footerLabel}>{t("expense.amountLabel")}</Text>
        <Text style={styles.footerAmount}>{formatAud(parsedAmount.ok ? parsedAmount.value : 0)}</Text>
      </View>
      {submitLock.busy ? (
        <Text style={styles.progress} accessibilityLiveRegion="polite">
          {draft.photos.length > 0 && uploadedCount < draft.photos.length
            ? t("deposit.uploading", { done: uploadedCount, total: draft.photos.length })
            : t("deposit.sending")}
        </Text>
      ) : null}
      <Button
        mode="contained"
        icon="cash-minus"
        onPress={() => void handleSubmit()}
        loading={submitLock.busy}
        disabled={submitLock.busy}
        contentStyle={styles.submitContent}
      >
        {t("expense.submit")}
      </Button>
    </>
  );

  return (
    <CashScreenFrame title={t("expense.title")} onBack={onBack} footer={footer}>
      <KeyboardAvoidingView style={cashUiStyles.scroll} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView
          ref={scrollRef}
          style={cashUiStyles.scroll}
          contentContainerStyle={cashUiStyles.content}
          keyboardShouldPersistTaps="handled"
        >
          <Text style={styles.storeLine}>{t("deposit.store", { store: store.storeName || store.storeCode })}</Text>
          <NoticeBanner tone="info" icon="information-outline" text={t("expense.effectiveNotice")} />

          <View onLayout={track("date")}>
            <CashCard>
              <CashDateField
                label={t("expense.fields.date")}
                value={draft.expenseDate}
                onChange={(value) => value && draftState.update((current) => ({ ...current, expenseDate: value }))}
                minDate={limits.dateRange.min}
                maxDate={limits.dateRange.max}
                error={issueMessage((issue) => issue.code === "dateInvalid" || issue.code === "dateOutOfRange")}
              />
              <Text style={styles.hint}>
                {context.capabilities.canViewAllStores
                  ? t("deposit.fields.dateHintUnlimited")
                  : t("deposit.fields.dateHintLimited", { days: context.maxBackfillDays })}
              </Text>
            </CashCard>
          </View>

          <View onLayout={track("category")}>
            <CashCard title={t("expense.fields.category")}>
              <View style={styles.chips}>
                {CASH_EXPENSE_CATEGORIES.map((category) => (
                  <ChoiceChip
                    key={category}
                    label={categoryLabel(t, category)}
                    selected={draft.category === category}
                    disabled={submitLock.busy}
                    onPress={() => draftState.update((current) => ({ ...current, category }))}
                  />
                ))}
              </View>
              {issueMessage((issue) => issue.code === "categoryMissing") ? (
                <HelperText type="error" visible>{issueMessage((issue) => issue.code === "categoryMissing")}</HelperText>
              ) : null}
            </CashCard>
          </View>

          <View onLayout={track("amount")}>
            <CashCard>
              <MoneyInput
                label={t("expense.fields.amount")}
                value={draft.amountText}
                disabled={submitLock.busy}
                onChange={(text) => draftState.update((current) => ({ ...current, amountText: text }))}
                error={issueMessage((issue) => issue.code === "amount")}
              />
              {isPayeeApplicable(draft.category) ? (
                <View onLayout={track("payee")}>
                  <TextInput
                    mode="outlined"
                    label={t("expense.fields.payee")}
                    value={draft.payeeName}
                    disabled={submitLock.busy}
                    onChangeText={(text) => draftState.update((current) => ({ ...current, payeeName: text }))}
                    maxLength={100}
                    style={styles.input}
                  />
                  {issueMessage((issue) => issue.code === "payeeTooLong") ? (
                    <HelperText type="error" visible>{issueMessage((issue) => issue.code === "payeeTooLong")}</HelperText>
                  ) : null}
                </View>
              ) : null}
            </CashCard>
          </View>

          <View onLayout={track("photos")}>
            <CashCard>
              <PhotoField
                label={receiptRequired ? t("expense.photos.receipt") : t("expense.photos.optional")}
                hint={receiptRequired ? t("expense.photos.receiptHint", { max: context.maxImagesPerExpense }) : t("expense.photos.optionalHint", { max: context.maxImagesPerExpense })}
                required={receiptRequired}
                photos={draft.photos}
                maxCount={context.maxImagesPerExpense}
                disabled={submitLock.busy}
                error={issueMessage((issue) => issue.code === "receiptRequired" || issue.code === "photosTooMany")}
                onAppend={(incoming) =>
                  draftState.update((current) => appendExpensePhotos(current, incoming, context.maxImagesPerExpense).draft)
                }
                onRemove={(key) => draftState.update((current) => removeExpensePhoto(current, key))}
                onRetry={retryPhoto}
              />
            </CashCard>
          </View>

          <View onLayout={track("note")}>
            <CashCard>
              <TextInput
                mode="outlined"
                label={t("expense.fields.note")}
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
          </View>
        </ScrollView>
      </KeyboardAvoidingView>
    </CashScreenFrame>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
  message: { color: HB_COLORS.textPrimary, textAlign: "center" },
  storeLine: { color: HB_COLORS.textSecondary, fontSize: 13 },
  hint: { color: HB_COLORS.textSecondary, fontSize: 12, lineHeight: 18 },
  input: { backgroundColor: HB_COLORS.white },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  footerTotal: { flexDirection: "row", alignItems: "baseline", justifyContent: "space-between" },
  footerLabel: { color: HB_COLORS.textSecondary, fontSize: 14 },
  footerAmount: { color: HB_COLORS.textPrimary, fontSize: 22, fontWeight: "800", fontVariant: ["tabular-nums"] },
  progress: { color: HB_COLORS.textSecondary, fontSize: 12, textAlign: "center" },
  submitContent: { minHeight: 48 },
});
