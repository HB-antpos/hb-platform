// 存银行表单：一次可有多张存单，每张存单 = 金额 + 1~3 张照片 + 可选存单号。
// 弱网策略：提交按钮防重复点击；提交失败保留表单内容与已上传的附件，重试沿用同一个 clientRequestId。
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  View,
  type LayoutChangeEvent,
} from "react-native";
import { useQuery } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter, type Href } from "expo-router";
import { ActivityIndicator, Button, HelperText, Text, TextInput } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { useCartStore } from "@/store/cart-store";
import { canViewStoreCash } from "./access";
import { createCashDeposit, fetchCashSummary } from "./api";
import { createRuntimeClientRequestIdHolder } from "./client-request-id-runtime";
import { resolveEntryDateRange } from "./dates";
import {
  addSlip,
  appendDepositPhotos,
  buildCreateDepositRequest,
  canAddSlip,
  computeDepositDifference,
  computeDepositTotal,
  createInitialDeposit,
  isOverrideReasonVisible,
  listDepositPhotos,
  patchDepositPhoto,
  removeDepositPhoto,
  removeSlip,
  resetDepositPhotos,
  resolveDefaultCoveredRange,
  resolveDepositBaseline,
  updateSlip,
  validateDepositDraft,
  type DepositDraft,
  type DepositIssue,
  type DepositLimits,
} from "./deposit-form";
import { isAttachmentInvalidError, isOverrideReasonRequiredError, resolveCashErrorMessage } from "./errors";
import { depositIssueKey } from "./issue-messages";
import { formatAud, formatSignedAud } from "./money";
import { createCashPhotoUploadDeps } from "./photo-upload-runtime";
import { allPhotosUploaded, type PhotoDraft } from "./photo-drafts";
import { uploadPendingPhotos } from "./photo-upload";
import { PhotoField } from "./PhotoField";
import { storeCashKeys } from "./query-keys";
import { buildRecordDetailHref, firstParam } from "./routes";
import { runCashSubmit } from "./submit-flow";
import { resolveActiveStore } from "./store-selection";
import type { CashContext, CashStoreOption } from "./types";
import { useCashContextQuery, useInvalidateCashData } from "./use-store-cash";
import { useLatestState } from "./use-latest-state";
import { useLeaveGuard } from "./use-leave-guard";
import { useSubmitLock } from "./use-submit-lock";
import { CashCard, CashDateField, CashScreenFrame, MoneyInput, NoticeBanner, ValueRow, cashUiStyles } from "./ui";

/** 按日明细接口最多 93 天，这里取默认覆盖范围时的查询窗口。 */

export function DepositFormScreen() {
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
  const title = t("deposit.title");
  const context = contextQuery.data;
  const store = context ? resolveActiveStore(context.stores, firstParam(params.storeCode), globalStoreCode) : null;

  if (!allowed) return <FormState title={title} onBack={goBack} text={t("states.notAllowed")} />;
  if (contextQuery.isPending) return <FormState title={title} onBack={goBack} loading text={t("states.loading")} />;
  if (contextQuery.isError || !context) {
    return (
      <FormState
        title={title}
        onBack={goBack}
        text={resolveCashErrorMessage(contextQuery.error, { t, language, fallbackKey: "storeCash:states.loadFailed" })}
        actionLabel={t("common:actions.retry")}
        onAction={() => void contextQuery.refetch()}
      />
    );
  }
  if (!store) return <FormState title={title} onBack={goBack} text={t("states.noStores")} />;
  if (!context.capabilities.canCreateDeposit) {
    return <FormState title={title} onBack={goBack} text={t("states.noDepositPermission")} />;
  }
  // 换分店时整份表单重建，绝不把上一个分店的附件带到另一个分店
  return <DepositForm key={store.storeCode} context={context} store={store} onBack={goBack} />;
}

function FormState({
  title,
  onBack,
  text,
  loading,
  actionLabel,
  onAction,
}: {
  title: string;
  onBack: () => void;
  text: string;
  loading?: boolean;
  actionLabel?: string;
  onAction?: () => void;
}) {
  return (
    <CashScreenFrame title={title} onBack={onBack}>
      <View style={styles.center}>
        {loading ? <ActivityIndicator /> : null}
        <Text style={styles.message}>{text}</Text>
        {actionLabel && onAction ? <Button mode="outlined" onPress={onAction}>{actionLabel}</Button> : null}
      </View>
    </CashScreenFrame>
  );
}

function sectionKeyOf(issue: DepositIssue): string {
  switch (issue.code) {
    case "dateInvalid":
    case "dateOutOfRange":
      return "date";
    case "coveredIncomplete":
    case "coveredInvalid":
    case "coveredReversed":
    case "coveredFuture":
      return "covered";
    case "noteTooLong":
      return "note";
    case "slipsEmpty":
    case "slipsTooMany":
      return "slips";
    case "overrideReasonRequired":
      return "override";
    default:
      return `slip:${issue.slipKey}`;
  }
}

function DepositForm({
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

  // 同一个表单会话只生成一个 clientRequestId，失败重试沿用，成功后才换新
  const requestIdHolder = useRef(createRuntimeClientRequestIdHolder()).current;
  const uploadDeps = useMemo(() => createCashPhotoUploadDeps(store.storeCode), [store.storeCode]);
  const slipKeyCounter = useRef(0);
  const nextSlipKey = () => `slip-${++slipKeyCounter.current}`;
  const allowLeaveRef = useRef(false);
  const initialSnapshotRef = useRef("");
  const scrollRef = useRef<ScrollView>(null);
  const sectionY = useRef<Record<string, number>>({});
  const overrideInputRef = useRef<{ focus: () => void } | null>(null);

  const summaryQuery = useQuery({
    queryKey: storeCashKeys.summary(store.storeCode),
    queryFn: () => fetchCashSummary(store.storeCode),
  });
  const summary = summaryQuery.data ?? null;
  const oldestUncovered = summary?.oldestUncoveredDate ?? null;
  // 摘要有结论后再生成初始草稿：预填建议额与覆盖范围，之后不再被后台刷新覆盖用户的输入
  const ready = !summaryQuery.isPending;

  const draftState = useLatestState<DepositDraft>(null);
  const draft = draftState.state;

  useEffect(() => {
    if (!ready || draftState.ref.current) return;
    const latestCloseDate = summary?.latestCloseDate ?? null;
    const initial = createInitialDeposit({
      storeToday: store.storeToday,
      suggestedAmount: summary?.suggestedDepositAmount ?? null,
      covered: resolveDefaultCoveredRange({
        oldestUncoveredDate: oldestUncovered,
        latestCloseDate,
        fallbackTo: summary?.asOfDate || null,
      }),
      firstSlipKey: nextSlipKey(),
    });
    initialSnapshotRef.current = JSON.stringify(initial);
    draftState.replace(initial);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ready]);

  const [forceOverrideReason, setForceOverrideReason] = useState(false);
  const [focusOverrideToken, setFocusOverrideToken] = useState(0);
  const [showErrors, setShowErrors] = useState(false);
  const [submitError, setSubmitError] = useState("");

  const limits: DepositLimits = useMemo(
    () => ({
      maxSlips: context.maxSlipsPerDeposit,
      maxImagesPerSlip: context.maxImagesPerSlip,
      differenceThreshold: context.depositDifferenceReasonThreshold,
      baselineAmount: resolveDepositBaseline({
        poolBalance: summary?.poolBalance ?? null,
        suggestedDepositAmount: summary?.suggestedDepositAmount ?? null,
      }),
      forceOverrideReason,
      dateRange: resolveEntryDateRange({
        storeToday: store.storeToday,
        maxBackfillDays: context.maxBackfillDays,
        canViewAllStores: context.capabilities.canViewAllStores,
      }),
      storeToday: store.storeToday,
    }),
    [context, forceOverrideReason, store.storeToday, summary?.poolBalance, summary?.suggestedDepositAmount],
  );

  const issues = useMemo(() => (draft ? validateDepositDraft(draft, limits) : []), [draft, limits]);
  const photos = useMemo(() => (draft ? listDepositPhotos(draft) : []), [draft]);
  const dirty = draft != null && JSON.stringify(draft) !== initialSnapshotRef.current;
  useLeaveGuard({ dirty, busy: submitLock.busy, allowLeaveRef });

  // 服务端要求差异原因后，显示输入框并聚焦
  useEffect(() => {
    if (focusOverrideToken === 0) return;
    const y = sectionY.current.override;
    if (typeof y === "number") scrollRef.current?.scrollTo({ y: Math.max(0, y - 8), animated: true });
    const handle = setTimeout(() => overrideInputRef.current?.focus(), 150);
    return () => clearTimeout(handle);
  }, [focusOverrideToken]);

  const track = (key: string) => (event: LayoutChangeEvent) => {
    sectionY.current[key] = event.nativeEvent.layout.y;
  };

  const issueMessage = (issue: DepositIssue | undefined) =>
    issue && showErrors ? t(depositIssueKey(issue), { days: context.maxBackfillDays, max: context.maxImagesPerSlip, slips: context.maxSlipsPerDeposit }) : undefined;
  const firstIssue = (predicate: (issue: DepositIssue) => boolean) => issues.find(predicate);

  const scrollToIssue = (issue: DepositIssue) => {
    const key = sectionKeyOf(issue);
    let y = sectionY.current[key];
    // 存单卡片的 onLayout.y 是相对「存单区」容器的，要加上容器自身的偏移才是滚动内容里的位置
    if (typeof y === "number" && key.startsWith("slip:")) y += sectionY.current.slips ?? 0;
    if (typeof y === "number") scrollRef.current?.scrollTo({ y: Math.max(0, y - 8), animated: true });
  };

  const applyPhotoPatch = useCallback(
    (key: string, patch: Partial<Omit<PhotoDraft, "key">>) => draftState.update((current) => patchDepositPhoto(current, key, patch)),
    [draftState],
  );

  const retryPhoto = (key: string) => {
    // 提交中由提交流程统一上传，避免同一张图被两条路径同时传
    if (submitLock.lockRef.current) return;
    const target = draftState.ref.current ? listDepositPhotos(draftState.ref.current).find((photo) => photo.key === key) : null;
    if (target) void uploadPendingPhotos([target], uploadDeps, applyPhotoPatch);
  };

  const handleSubmit = () =>
    submitLock.run(async () => {
      const current = draftState.ref.current;
      if (!current) return;
      setShowErrors(true);
      setSubmitError("");
      const currentIssues = validateDepositDraft(current, limits);
      if (currentIssues.length > 0) {
        setSubmitError(t("errors.fixFirst"));
        scrollToIssue(currentIssues[0]);
        return;
      }
      const outcome = await runCashSubmit({
        uploadPhotos: () => uploadPendingPhotos(listDepositPhotos(draftState.ref.current ?? current), uploadDeps, applyPhotoPatch),
        buildRequest: () =>
          draftState.ref.current
            ? buildCreateDepositRequest(draftState.ref.current, {
                clientRequestId: requestIdHolder.current(),
                storeCode: store.storeCode,
              })
            : null,
        send: createCashDeposit,
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
        if (isAttachmentInvalidError(outcome.error)) {
          // 附件失效：已压缩的本地图片还在，退回待上传，下次提交自动重新上传，用户不必重拍
          draftState.update(resetDepositPhotos);
        }
        if (isOverrideReasonRequiredError(outcome.error)) {
          // 服务端的基准与客户端不一致（例如余额在填表期间变了）：显示并聚焦差异原因，其余内容与附件保留
          setForceOverrideReason(true);
          setFocusOverrideToken((value) => value + 1);
        }
        setSubmitError(
          `${resolveCashErrorMessage(outcome.error, { t, language })} ${t("errors.retryKeepsData")}`.trim(),
        );
        return;
      }
      // 成功：换新请求号，放行离开守卫，刷新数据后进入详情确认所记内容
      requestIdHolder.rotate();
      allowLeaveRef.current = true;
      void invalidate();
      router.replace(buildRecordDetailHref("deposit", outcome.result.depositGuid, store.storeCode, { created: true }) as Href);
    });

  if (!draft) {
    return (
      <CashScreenFrame title={t("deposit.title")} onBack={onBack}>
        <View style={styles.center}>
          <ActivityIndicator />
          <Text style={styles.message}>{t("states.loading")}</Text>
        </View>
      </CashScreenFrame>
    );
  }

  const total = computeDepositTotal(draft.slips);
  const difference = computeDepositDifference(total, limits.baselineAmount);
  const overrideVisible = isOverrideReasonVisible(draft, limits);
  const uploadedCount = photos.filter((photo) => photo.status === "uploaded").length;
  const uploadingNow = photos.some((photo) => photo.status === "uploading");
  const allUploaded = photos.length > 0 && allPhotosUploaded(photos);

  const footer = (
    <>
      {submitError ? (
        <NoticeBanner tone="danger" icon="alert-circle-outline" text={submitError} />
      ) : null}
      <View style={styles.footerTotal}>
        <Text style={styles.footerLabel}>{t("deposit.total")}</Text>
        <Text style={styles.footerAmount}>{formatAud(total)}</Text>
      </View>
      {submitLock.busy ? (
        <Text style={styles.progress} accessibilityLiveRegion="polite">
          {uploadingNow || !allUploaded
            ? t("deposit.uploading", { done: uploadedCount, total: photos.length })
            : t("deposit.sending")}
        </Text>
      ) : null}
      <Button
        mode="contained"
        icon="bank-transfer-in"
        onPress={() => void handleSubmit()}
        loading={submitLock.busy}
        disabled={submitLock.busy}
        contentStyle={styles.submitContent}
      >
        {t("deposit.submit")}
      </Button>
    </>
  );

  return (
    <CashScreenFrame title={t("deposit.title")} onBack={onBack} footer={footer}>
      <KeyboardAvoidingView style={cashUiStyles.scroll} behavior={Platform.OS === "ios" ? "padding" : undefined}>
        <ScrollView
          ref={scrollRef}
          style={cashUiStyles.scroll}
          contentContainerStyle={cashUiStyles.content}
          keyboardShouldPersistTaps="handled"
        >
          <Text style={styles.storeLine}>{t("deposit.store", { store: store.storeName || store.storeCode })}</Text>

          {limits.baselineAmount != null ? (
            <NoticeBanner
              tone="info"
              icon="cash-multiple"
              text={
                summary?.suggestedDepositAmount != null && summary.suggestedDepositAmount > 0
                  ? t("deposit.baseline", { amount: formatAud(limits.baselineAmount) })
                  : t("deposit.baselineNoSuggestion", {
                      amount: formatAud(limits.baselineAmount),
                      threshold: formatAud(context.depositDifferenceReasonThreshold),
                    })
              }
            />
          ) : summaryQuery.isError ? (
            <NoticeBanner tone="warning" icon="information-outline" text={t("deposit.baselineUnavailable")} />
          ) : null}

          <View onLayout={track("date")}>
            <CashCard>
              <CashDateField
                label={t("deposit.fields.date")}
                value={draft.depositDate}
                onChange={(value) => value && draftState.update((current) => ({ ...current, depositDate: value }))}
                minDate={limits.dateRange.min}
                maxDate={limits.dateRange.max}
                error={issueMessage(firstIssue((issue) => issue.code === "dateInvalid" || issue.code === "dateOutOfRange"))}
              />
              <Text style={styles.hint}>
                {context.capabilities.canViewAllStores
                  ? t("deposit.fields.dateHintUnlimited")
                  : t("deposit.fields.dateHintLimited", { days: context.maxBackfillDays })}
              </Text>
            </CashCard>
          </View>

          <View onLayout={track("covered")}>
            <CashCard title={t("deposit.covered.title")}>
              <Text style={styles.hint}>{t("deposit.covered.hint")}</Text>
              <View style={styles.coveredRow}>
                <View style={styles.coveredField}>
                  <CashDateField
                    label={t("deposit.covered.from")}
                    value={draft.coveredFromDate}
                    placeholder={t("deposit.covered.empty")}
                    allowEmpty
                    maxDate={store.storeToday}
                    onChange={(value) => draftState.update((current) => ({ ...current, coveredFromDate: value }))}
                  />
                </View>
                <View style={styles.coveredField}>
                  <CashDateField
                    label={t("deposit.covered.to")}
                    value={draft.coveredToDate}
                    placeholder={t("deposit.covered.empty")}
                    allowEmpty
                    maxDate={store.storeToday}
                    onChange={(value) => draftState.update((current) => ({ ...current, coveredToDate: value }))}
                  />
                </View>
              </View>
              {issueMessage(firstIssue((issue) => sectionKeyOf(issue) === "covered")) ? (
                <HelperText type="error" visible>
                  {issueMessage(firstIssue((issue) => sectionKeyOf(issue) === "covered"))}
                </HelperText>
              ) : null}
              {draft.coveredFromDate || draft.coveredToDate ? (
                <Button
                  compact
                  mode="text"
                  onPress={() => draftState.update((current) => ({ ...current, coveredFromDate: null, coveredToDate: null }))}
                  style={styles.clearButton}
                >
                  {t("deposit.covered.clear")}
                </Button>
              ) : null}
            </CashCard>
          </View>

          <View onLayout={track("slips")} style={styles.slips}>
            {draft.slips.map((slip, index) => (
              <View key={slip.key} onLayout={track(`slip:${slip.key}`)}>
                <CashCard
                  title={t("deposit.slip.title", { index: index + 1 })}
                  right={
                    draft.slips.length > 1 ? (
                      <Button
                        compact
                        textColor={HB_COLORS.danger}
                        disabled={submitLock.busy}
                        onPress={() => draftState.update((current) => removeSlip(current, slip.key))}
                      >
                        {t("deposit.slip.remove")}
                      </Button>
                    ) : undefined
                  }
                >
                  <MoneyInput
                    label={t("deposit.slip.amount")}
                    value={slip.amountText}
                    disabled={submitLock.busy}
                    onChange={(text) => draftState.update((current) => updateSlip(current, slip.key, { amountText: text }))}
                    error={issueMessage(firstIssue((issue) => issue.code === "slipAmount" && issue.slipKey === slip.key))}
                  />
                  <TextInput
                    mode="outlined"
                    label={t("deposit.slip.slipNo")}
                    value={slip.slipNo}
                    disabled={submitLock.busy}
                    onChangeText={(text) => draftState.update((current) => updateSlip(current, slip.key, { slipNo: text }))}
                    maxLength={64}
                    style={styles.input}
                  />
                  <PhotoField
                    label={t("deposit.slip.photos")}
                    hint={t("deposit.slip.photosHint", { max: context.maxImagesPerSlip })}
                    required
                    photos={slip.photos}
                    maxCount={context.maxImagesPerSlip}
                    disabled={submitLock.busy}
                    error={issueMessage(
                      firstIssue(
                        (issue) =>
                          (issue.code === "slipPhotosMissing" || issue.code === "slipPhotosTooMany") && issue.slipKey === slip.key,
                      ),
                    )}
                    onAppend={(incoming) =>
                      draftState.update((current) => appendDepositPhotos(current, slip.key, incoming, context.maxImagesPerSlip).draft)
                    }
                    onRemove={(key) => draftState.update((current) => removeDepositPhoto(current, key))}
                    onRetry={retryPhoto}
                  />
                </CashCard>
              </View>
            ))}
            {issueMessage(firstIssue((issue) => issue.code === "slipsEmpty" || issue.code === "slipsTooMany")) ? (
              <HelperText type="error" visible>
                {issueMessage(firstIssue((issue) => issue.code === "slipsEmpty" || issue.code === "slipsTooMany"))}
              </HelperText>
            ) : null}
            <Button
              mode="outlined"
              icon="plus"
              disabled={submitLock.busy || !canAddSlip(draft, context.maxSlipsPerDeposit)}
              onPress={() => draftState.update((current) => addSlip(current, nextSlipKey(), context.maxSlipsPerDeposit))}
              contentStyle={styles.addContent}
            >
              {canAddSlip(draft, context.maxSlipsPerDeposit)
                ? t("deposit.slip.add")
                : t("deposit.slip.limit", { max: context.maxSlipsPerDeposit })}
            </Button>
          </View>

          <CashCard title={t("deposit.summaryTitle")}>
            <ValueRow label={t("deposit.total")} value={formatAud(total)} strong />
            {limits.baselineAmount != null ? (
              <>
                <ValueRow label={t("deposit.baselineRow")} value={formatAud(limits.baselineAmount)} muted />
                {difference != null ? (
                  <ValueRow
                    label={t("deposit.difference")}
                    value={formatSignedAud(difference)}
                    valueStyle={overrideVisible ? styles.differenceAlert : undefined}
                  />
                ) : null}
              </>
            ) : null}
          </CashCard>

          {overrideVisible ? (
            <View onLayout={track("override")}>
              <CashCard title={t("deposit.override.title")}>
                <Text style={styles.hint}>
                  {t("deposit.override.hint", { threshold: formatAud(context.depositDifferenceReasonThreshold) })}
                </Text>
                <TextInput
                  ref={overrideInputRef as never}
                  mode="outlined"
                  label={t("deposit.override.label")}
                  placeholder={t("deposit.override.placeholder")}
                  value={draft.overrideReason}
                  disabled={submitLock.busy}
                  onChangeText={(text) => draftState.update((current) => ({ ...current, overrideReason: text }))}
                  multiline
                  numberOfLines={2}
                  maxLength={500}
                  error={Boolean(issueMessage(firstIssue((issue) => issue.code === "overrideReasonRequired")))}
                  style={styles.input}
                />
                {issueMessage(firstIssue((issue) => issue.code === "overrideReasonRequired")) ? (
                  <HelperText type="error" visible>
                    {issueMessage(firstIssue((issue) => issue.code === "overrideReasonRequired"))}
                  </HelperText>
                ) : null}
              </CashCard>
            </View>
          ) : null}

          <View onLayout={track("note")}>
            <CashCard>
              <TextInput
                mode="outlined"
                label={t("deposit.note.label")}
                value={draft.note}
                disabled={submitLock.busy}
                onChangeText={(text) => draftState.update((current) => ({ ...current, note: text }))}
                multiline
                numberOfLines={2}
                maxLength={500}
                style={styles.input}
              />
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
  coveredRow: { flexDirection: "row", gap: HB_SPACING.xs },
  coveredField: { flex: 1, minWidth: 0 },
  clearButton: { alignSelf: "flex-start" },
  slips: { gap: HB_SPACING.sm },
  addContent: { minHeight: 44 },
  differenceAlert: { color: HB_COLORS.danger },
  footerTotal: { flexDirection: "row", alignItems: "baseline", justifyContent: "space-between" },
  footerLabel: { color: HB_COLORS.textSecondary, fontSize: 14 },
  footerAmount: { color: HB_COLORS.textPrimary, fontSize: 22, fontWeight: "800", fontVariant: ["tabular-nums"] },
  progress: { color: HB_COLORS.textSecondary, fontSize: 12, textAlign: "center" },
  submitContent: { minHeight: 48 },
});
