import { useEffect, useMemo, useState } from "react";
import { Alert, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useQueryClient } from "@tanstack/react-query";
import { ActivityIndicator, Button, Icon, Text } from "react-native-paper";
import { SafeAreaView, useSafeAreaInsets } from "react-native-safe-area-context";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { EmptyState } from "@/components/ui/EmptyState";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { useAuthStore } from "@/store/auth-store";
import { listSeparator, withCode } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { specialCategoriesApi } from "./api";
import {
  buildSyncRequest,
  hasDestructiveChanges,
  pruneSelection,
  selectableTargetCodes,
  skippedReasonKey,
  sumSyncCounts,
  summarizeSyncOutcome,
  syncResultErrorKey,
  willMarkNewSpecialProducts,
} from "./logic";
import { SC_COLORS, ScreenHeader, SPECIAL_CATEGORIES_PATHS, SpecialCategoriesMessage, useApiErrorText, useSpecialCategoriesGuard } from "./shared";
import { specialCategoryKeys, useManagedStores, useSyncTargets } from "./use-special-product-categories";
import type { SyncCounts, SyncMode, SyncPreviewTarget, SyncResultTarget, SyncScope, SyncSkippedProduct } from "./types";

type Step = "settings" | "preview" | "result";

/** 单个分店在跳过清单里最多渲染的条数，避免一次渲染几百行；超出部分只显示数量。 */
const SKIPPED_RENDER_LIMIT = 100;

function firstParam(value: string | string[] | undefined) {
  return (Array.isArray(value) ? value[0] : value) ?? "";
}

export function SyncScreen() {
  const router = useRouter();
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const params = useLocalSearchParams<{ storeCode?: string }>();
  const { blocked } = useSpecialCategoriesGuard();
  const goBack = () => (router.canGoBack() ? router.back() : router.replace(SPECIAL_CATEGORIES_PATHS.list));
  if (blocked) return <SpecialCategoriesMessage message={blocked} onBack={goBack} />;
  return <SyncContent key={`${userGuid}:${firstParam(params.storeCode)}`} sourceStoreCode={firstParam(params.storeCode)} onExit={goBack} />;
}

function formatClock(date: Date) {
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function SyncContent({ sourceStoreCode, onExit }: { sourceStoreCode: string; onExit: () => void }) {
  const { t, language } = useAppTranslation("specialProductCategories");
  const insets = useSafeAreaInsets();
  const queryClient = useQueryClient();
  const errorText = useApiErrorText();
  const { stores } = useManagedStores();
  const targetsQuery = useSyncTargets(sourceStoreCode || null);

  const [step, setStep] = useState<Step>("settings");
  const [scope, setScope] = useState<SyncScope>("categories-and-products");
  const [mode, setMode] = useState<SyncMode>("merge");
  const [selected, setSelected] = useState<string[]>([]);
  const [preview, setPreview] = useState<SyncPreviewTarget[]>([]);
  const [results, setResults] = useState<SyncResultTarget[]>([]);
  const [confirmReplace, setConfirmReplace] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [skippedView, setSkippedView] = useState<{ storeName: string; storeCode: string; items: SyncSkippedProduct[] }[] | null>(null);
  const [finishedAt, setFinishedAt] = useState<Date | null>(null);

  const data = targetsQuery.data;
  const targets = useMemo(() => data?.targets ?? [], [data]);
  const allCodes = useMemo(() => selectableTargetCodes(targets), [targets]);
  const sourceName = stores.find((store) => store.storeCode === sourceStoreCode)?.storeName ?? sourceStoreCode;
  const sourceLabel = withCode(sourceName, sourceStoreCode, language);
  const separator = listSeparator(language);

  // 目标列表刷新后丢弃已不存在的勾选。
  useEffect(() => {
    setSelected((current) => pruneSelection(current, allCodes));
  }, [allCodes]);

  const selectedCodes = allCodes.filter((code) => selected.includes(code));
  const allSelected = allCodes.length > 0 && selectedCodes.length === allCodes.length;
  const sourceEmpty = (data?.source.categoryCount ?? 0) === 0;
  const withProducts = scope === "categories-and-products";

  const toggleTarget = (code: string) =>
    setSelected((current) => (current.includes(code) ? current.filter((item) => item !== code) : [...current, code]));

  const request = () => buildSyncRequest({ sourceStoreCode, targetStoreCodes: selectedCodes, scope, mode });

  const runPreview = async () => {
    if (busy || selectedCodes.length === 0) return;
    setBusy(true);
    setError("");
    try {
      const rows = await specialCategoriesApi.previewSync(request());
      setPreview(rows);
      setConfirmReplace(false);
      setStep("preview");
    } catch (cause) {
      setError(errorText(cause, "sync.previewFailed"));
    } finally {
      setBusy(false);
    }
  };

  const execute = async () => {
    setBusy(true);
    setError("");
    try {
      // 覆盖模式走到这里时用户已勾选确认并通过了二次弹窗，后端要求 confirmReplace=true 才会执行。
      const rows = await specialCategoriesApi.executeSync({ ...request(), confirmReplace: mode === "replace" });
      setResults(rows);
      setFinishedAt(new Date());
      setStep("result");
      // 来源与目标店的分类 / 商品都可能变了：让所有缓存失效。
      void queryClient.invalidateQueries({ queryKey: specialCategoryKeys.all });
    } catch (cause) {
      // 请求本身失败（超时 / 断网）时服务端可能已经写了一部分，不能当作「没写」。
      setError(errorText(cause, "sync.executeFailed"));
    } finally {
      setBusy(false);
    }
  };

  const confirmExecute = () => {
    if (busy) return;
    if (mode === "replace") {
      const totals = sumSyncCounts(preview.filter((item) => !item.error));
      Alert.alert(
        t("sync.replaceAlertTitle", { count: preview.filter((item) => !item.error).length }),
        t("sync.replaceAlertBody", { removed: totals.removedCategories, unmarked: totals.unmarkedProducts }),
        [
          { text: t("actions.cancel"), style: "cancel" },
          { text: t("sync.replaceAlertConfirm"), style: "destructive", onPress: () => void execute() },
        ],
      );
      return;
    }
    void execute();
  };

  const header =
    step === "settings" ? (
      <ScreenHeader title={t("sync.title")} backIcon="close" onBack={onExit} />
    ) : step === "preview" ? (
      <ScreenHeader title={t("sync.previewTitle")} onBack={() => (busy ? undefined : setStep("settings"))} />
    ) : (
      <ScreenHeader
        title={t("sync.resultTitle")}
        backIcon="close"
        onBack={onExit}
        right={
          <Button compact mode="text" onPress={onExit}>
            {t("actions.done")}
          </Button>
        }
      />
    );

  // ---------------- 设置 ----------------
  const settings = (
    <>
      <Text style={styles.label}>{t("sync.sourceLabel")}</Text>
      <View style={styles.sourceCard}>
        <Icon source="storefront-outline" size={20} color={HB_COLORS.textSecondary} />
        <Text style={styles.sourceName} numberOfLines={1}>
          {sourceLabel}
        </Text>
        {data ? <Text style={styles.sourceMeta}>{t("sync.sourceMeta", { categories: data.source.categoryCount, products: data.source.productCount })}</Text> : null}
      </View>
      {data && sourceEmpty ? <Banner tone="warn" icon="alert-outline" text={t("sync.sourceEmpty")} /> : null}

      <Text style={styles.label}>{t("sync.scopeLabel")}</Text>
      <OptionCard
        selected={scope === "categories"}
        title={t("sync.scopeCategories")}
        description={t("sync.scopeCategoriesHint")}
        onPress={() => setScope("categories")}
      />
      <OptionCard
        selected={scope === "categories-and-products"}
        title={t("sync.scopeProducts")}
        description={t("sync.scopeProductsHint")}
        onPress={() => setScope("categories-and-products")}
      />

      <View style={styles.labelRow}>
        <Text style={styles.label}>{t("sync.targetsLabel")}</Text>
        {allCodes.length > 0 ? (
          <Button compact mode="text" onPress={() => setSelected(allSelected ? [] : allCodes)}>
            {t(allSelected ? "sync.clearAll" : "sync.selectAll")}
          </Button>
        ) : null}
      </View>
      {targetsQuery.isPending ? (
        <ActivityIndicator style={styles.loader} color={HB_COLORS.brand} />
      ) : targetsQuery.isError ? (
        <EmptyState
          title={t("sync.targetsFailed")}
          description={errorText(targetsQuery.error, "sync.targetsFailed")}
          actionLabel={t("actions.retry")}
          onAction={() => void targetsQuery.refetch()}
        />
      ) : targets.length === 0 ? (
        <Text style={styles.hint}>{t("sync.noTargets")}</Text>
      ) : (
        <View style={styles.targetList}>
          {targets.map((target, index) => {
            const checked = selected.includes(target.storeCode);
            return (
              <Pressable
                key={target.storeCode}
                accessibilityRole="checkbox"
                accessibilityState={{ checked }}
                onPress={() => toggleTarget(target.storeCode)}
                style={[styles.targetRow, index < targets.length - 1 ? styles.targetDivider : null]}
              >
                <Icon source={checked ? "checkbox-marked" : "checkbox-blank-outline"} size={24} color={checked ? HB_COLORS.brand : HB_COLORS.outline} />
                <Text style={styles.targetName} numberOfLines={1}>
                  {withCode(target.storeName, target.storeCode, language)}
                </Text>
                <Text style={styles.targetMeta} numberOfLines={2}>
                  {target.categoryCount > 0 ? t("sync.targetHasCategories", { count: target.categoryCount }) : t("sync.targetNoCategories")}
                  {withProducts && target.specialProductCount > 0 ? `${separator}${t("sync.targetHasProducts", { count: target.specialProductCount })}` : ""}
                </Text>
              </Pressable>
            );
          })}
        </View>
      )}

      <Text style={styles.label}>{t("sync.modeLabel")}</Text>
      <OptionCard
        selected={mode === "merge"}
        title={t("sync.modeMerge")}
        description={t("sync.modeMergeHint")}
        onPress={() => setMode("merge")}
      />
      <OptionCard
        selected={mode === "replace"}
        danger
        title={t("sync.modeReplace")}
        description={withProducts ? t("sync.modeReplaceHintProducts") : t("sync.modeReplaceHintCategories")}
        onPress={() => setMode("replace")}
      />
      {error ? <Text style={styles.error}>{error}</Text> : null}
    </>
  );

  // ---------------- 预览 ----------------
  // 预览里带 error 的店执行时不会被改动，只统计能执行的店；全部都不能执行就不给确认。
  const runnable = preview.filter((item) => !item.error);
  const runnableCount = runnable.length;
  const totals = sumSyncCounts(runnable);
  const destructive = mode === "replace" && hasDestructiveChanges(runnable);
  const markWarning = willMarkNewSpecialProducts(scope, runnable);
  const previewBody = (
    <>
      <Banner tone="info" icon="information-outline" text={t("sync.previewInfo")} />
      {markWarning ? <Banner tone="warn" icon="alert-outline" text={t("sync.previewMarkWarning")} /> : null}
      {mode === "replace" ? (
        <View style={styles.dangerBox}>
          <View style={styles.dangerTitleRow}>
            <Icon source="alert-octagon-outline" size={20} color={HB_COLORS.danger} />
            <Text style={styles.dangerTitle}>{t("sync.replaceTitle")}</Text>
          </View>
          <Text style={styles.dangerText}>
            {destructive
              ? t(withProducts ? "sync.replaceSummaryProducts" : "sync.replaceSummaryCategories", { removed: totals.removedCategories, unmarked: totals.unmarkedProducts })
              : t("sync.replaceNothing")}
          </Text>
          {destructive ? (
            <Pressable
              accessibilityRole="checkbox"
              accessibilityState={{ checked: confirmReplace }}
              onPress={() => setConfirmReplace((value) => !value)}
              style={styles.confirmRow}
            >
              <Icon source={confirmReplace ? "checkbox-marked" : "checkbox-blank-outline"} size={24} color={HB_COLORS.danger} />
              <Text style={styles.confirmText}>{t("sync.replaceConfirm")}</Text>
            </Pressable>
          ) : null}
        </View>
      ) : null}
      {preview.map((target) => (
        <View key={target.storeCode} style={styles.storeCard}>
          <Text style={styles.storeTitle}>{withCode(target.storeName, target.storeCode, language)}</Text>
          {target.error ? (
            <View style={styles.dangerLine}>
              <Text style={styles.dangerLineText}>{t("sync.previewStoreError", { reason: target.error })}</Text>
            </View>
          ) : null}
          <CountGrid counts={target} withProducts={withProducts} />
          {mode === "replace" && (target.removedCategories > 0 || target.unmarkedProducts > 0) ? (
            <View style={styles.dangerLine}>
              {target.removedCategories > 0 ? <Text style={styles.dangerLineText}>{t("sync.willRemoveCategories", { count: target.removedCategories })}</Text> : null}
              {withProducts && target.unmarkedProducts > 0 ? <Text style={styles.dangerLineText}>{t("sync.willUnmarkProducts", { count: target.unmarkedProducts })}</Text> : null}
            </View>
          ) : null}
          {target.skipped.length > 0 ? (
            <View style={styles.skipLine}>
              <Icon source="alert-outline" size={16} color={HB_COLORS.warning} />
              <Text style={styles.skipText}>{t("sync.skippedLine", { count: target.skipped.length })}</Text>
              <Button
                compact
                mode="text"
                onPress={() => setSkippedView([{ storeName: target.storeName, storeCode: target.storeCode, items: target.skipped }])}
              >
                {t("sync.viewSkipped")}
              </Button>
            </View>
          ) : null}
        </View>
      ))}
      {error ? <Text style={styles.error}>{error}</Text> : null}
    </>
  );

  // ---------------- 结果 ----------------
  const outcome = summarizeSyncOutcome(results);
  const resultBody = (
    <>
      <View style={styles.resultHero}>
        <View style={[styles.heroIcon, outcome.succeeded.length === 0 ? styles.heroIconFailed : null]}>
          <Icon source={outcome.succeeded.length === 0 ? "close" : "check"} size={32} color={HB_COLORS.white} />
        </View>
        <Text style={styles.heroTitle}>{t(outcome.succeeded.length === 0 ? "sync.resultNone" : "sync.resultDone", { count: outcome.succeeded.length })}</Text>
        <Text style={styles.heroSub}>
          {sourceName}
          {" → "}
          {results.map((item) => item.storeName).join(separator)}
          {finishedAt ? ` · ${formatClock(finishedAt)}` : ""}
        </Text>
        {outcome.failed.length > 0 ? <Text style={styles.heroFailed}>{t("sync.resultFailedCount", { count: outcome.failed.length })}</Text> : null}
      </View>
      {results.map((target) => (
        <View key={target.storeCode} style={[styles.storeCard, !target.ok ? styles.storeCardFailed : null]}>
          <View style={styles.storeTitleRow}>
            <Icon source={target.ok ? "check-circle-outline" : "alert-circle-outline"} size={20} color={target.ok ? HB_COLORS.success : HB_COLORS.danger} />
            <Text style={styles.storeTitle}>{withCode(target.storeName, target.storeCode, language)}</Text>
          </View>
          {target.ok ? (
            <>
              <View style={styles.resultLine}>
                <ResultStat label={t("sync.resultCategories")} value={`${target.createdCategories + target.mergedCategories}`} note={t("sync.resultCreated", { count: target.createdCategories })} />
                {withProducts ? <ResultStat label={t("sync.resultMarked")} value={`+${target.newlyMarkedProducts}`} /> : null}
              </View>
              {withProducts ? (
                <View style={styles.resultLine}>
                  <ResultStat label={t("sync.resultReassigned")} value={`${target.reassignedProducts}`} />
                  <ResultStat label={t("sync.resultSkipped")} value={`${target.skipped.length}`} warn={target.skipped.length > 0} />
                </View>
              ) : null}
              {mode === "replace" && (target.removedCategories > 0 || target.unmarkedProducts > 0) ? (
                <Text style={styles.dangerLineText}>
                  {t("sync.resultReplaced", { removed: target.removedCategories, unmarked: target.unmarkedProducts })}
                </Text>
              ) : null}
            </>
          ) : (
            <Text style={styles.failedText}>
              {syncResultErrorKey(target.error) === "storeFailed"
                ? t("sync.storeFailed", { reason: target.error ?? "" })
                : t(`sync.${syncResultErrorKey(target.error)}`)}
            </Text>
          )}
        </View>
      ))}
      <Banner tone="info" icon="history" text={t("sync.resultLogHint")} />
    </>
  );

  const bottom =
    step === "settings" ? (
      <Button
        mode="contained"
        loading={busy}
        disabled={busy || selectedCodes.length === 0 || sourceEmpty}
        onPress={() => void runPreview()}
        style={BUSINESS_UI.button}
        contentStyle={BUSINESS_UI.buttonContent}
      >
        {t("sync.previewAction", { count: selectedCodes.length })}
      </Button>
    ) : step === "preview" ? (
      <View style={styles.footerRow}>
        <Button mode="outlined" disabled={busy} onPress={() => setStep("settings")} style={[BUSINESS_UI.button, styles.footerButton]} contentStyle={BUSINESS_UI.buttonContent}>
          {t("sync.backToEdit")}
        </Button>
        <Button
          mode="contained"
          loading={busy}
          buttonColor={mode === "replace" ? HB_COLORS.danger : undefined}
          disabled={busy || runnableCount === 0 || (destructive && !confirmReplace)}
          onPress={confirmExecute}
          style={[BUSINESS_UI.button, styles.footerButton]}
          contentStyle={BUSINESS_UI.buttonContent}
        >
          {t(mode === "replace" ? "sync.executeReplace" : "sync.execute", { count: runnableCount })}
        </Button>
      </View>
    ) : outcome.skippedProducts > 0 ? (
      <Button
        mode="outlined"
        onPress={() =>
          setSkippedView(
            outcome.succeeded.filter((item) => item.skipped.length > 0).map((item) => ({ storeName: item.storeName, storeCode: item.storeCode, items: item.skipped })),
          )
        }
        style={BUSINESS_UI.button}
        contentStyle={BUSINESS_UI.buttonContent}
      >
        {t("sync.viewAllSkipped", { count: outcome.skippedProducts })}
      </Button>
    ) : null;

  return (
    <SafeAreaView style={styles.safe} edges={["top", "left", "right"]}>
      {header}
      <ScrollView style={styles.flex} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        {step === "settings" ? settings : step === "preview" ? previewBody : resultBody}
      </ScrollView>
      {bottom ? <View style={[BUSINESS_UI.footer, { paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) }]}>{bottom}</View> : null}
      <SkippedSheet groups={skippedView} onDismiss={() => setSkippedView(null)} />
    </SafeAreaView>
  );
}

function Banner({ tone, icon, text }: { tone: "info" | "warn"; icon: string; text: string }) {
  const warn = tone === "warn";
  return (
    <View style={[styles.banner, warn ? styles.bannerWarn : styles.bannerInfo]}>
      <Icon source={icon} size={18} color={warn ? HB_COLORS.warning : HB_COLORS.action} />
      <Text style={[styles.bannerText, { color: warn ? HB_COLORS.warning : HB_COLORS.action }]}>{text}</Text>
    </View>
  );
}

function OptionCard({
  selected,
  title,
  description,
  danger = false,
  onPress,
}: {
  selected: boolean;
  title: string;
  description: string;
  danger?: boolean;
  onPress: () => void;
}) {
  const accent = danger ? HB_COLORS.danger : HB_COLORS.brand;
  return (
    <Pressable
      accessibilityRole="radio"
      accessibilityState={{ selected }}
      onPress={onPress}
      style={[styles.option, selected ? { borderColor: accent, backgroundColor: danger ? SC_COLORS.dangerBg : SC_COLORS.blueBg } : null]}
    >
      <Icon source={selected ? "radiobox-marked" : "radiobox-blank"} size={22} color={selected ? accent : HB_COLORS.outline} />
      <View style={styles.optionText}>
        <Text style={[styles.optionTitle, selected && danger ? { color: HB_COLORS.danger } : null]}>{title}</Text>
        <Text style={styles.optionDescription}>{description}</Text>
      </View>
    </Pressable>
  );
}

function CountGrid({ counts, withProducts }: { counts: SyncCounts; withProducts: boolean }) {
  const { t } = useAppTranslation("specialProductCategories");
  const cells: { label: string; value: number; warn?: boolean }[] = [
    { label: t("sync.countCreated"), value: counts.createdCategories },
    { label: t("sync.countMerged"), value: counts.mergedCategories },
  ];
  if (withProducts) {
    cells.push(
      { label: t("sync.countMarked"), value: counts.newlyMarkedProducts },
      { label: t("sync.countReassigned"), value: counts.reassignedProducts },
      { label: t("sync.countUnchanged"), value: counts.unchangedProducts },
      { label: t("sync.countSkipped"), value: counts.skipped.length, warn: counts.skipped.length > 0 },
    );
  }
  return (
    <View style={styles.grid}>
      {cells.map((cell) => (
        <View key={cell.label} style={styles.gridCell}>
          <Text style={styles.gridLabel}>{cell.label}</Text>
          <Text style={[styles.gridValue, cell.warn ? styles.gridWarn : null]}>{cell.value}</Text>
        </View>
      ))}
    </View>
  );
}

function ResultStat({ label, value, note, warn = false }: { label: string; value: string; note?: string; warn?: boolean }) {
  return (
    <View style={styles.resultStat}>
      <Text style={styles.gridLabel}>{label}</Text>
      <Text style={[styles.resultValue, warn ? styles.gridWarn : null]}>
        {value}
        {note ? <Text style={styles.resultNote}>{`  ${note}`}</Text> : null}
      </Text>
    </View>
  );
}

function SkippedSheet({
  groups,
  onDismiss,
}: {
  groups: { storeName: string; storeCode: string; items: SyncSkippedProduct[] }[] | null;
  onDismiss: () => void;
}) {
  const { t, language } = useAppTranslation("specialProductCategories");
  return (
    <BusinessSheet visible={groups !== null} title={t("sync.skippedTitle")} subtitle={t("sync.skippedSubtitle")} onDismiss={onDismiss}>
      {(groups ?? []).map((group) => (
        <View key={group.storeCode} style={styles.skippedGroup}>
          <Text style={styles.skippedStore}>{withCode(group.storeName, group.storeCode, language)}</Text>
          {group.items.slice(0, SKIPPED_RENDER_LIMIT).map((item) => {
            const key = skippedReasonKey(item.reason);
            return (
              <View key={item.productCode} style={styles.skippedRow}>
                <Text style={styles.skippedName} numberOfLines={2}>
                  {item.displayName}
                </Text>
                <Text style={styles.skippedMeta}>
                  {item.productCode}
                  {" · "}
                  {key ? t(`skippedReason.${key}`) : t("skippedReason.unknown", { reason: item.reason || "-" })}
                </Text>
              </View>
            );
          })}
          {group.items.length > SKIPPED_RENDER_LIMIT ? (
            <Text style={styles.skippedMore}>{t("sync.skippedMore", { count: group.items.length - SKIPPED_RENDER_LIMIT })}</Text>
          ) : null}
        </View>
      ))}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: HB_COLORS.background },
  flex: { flex: 1 },
  content: { paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.lg, gap: HB_SPACING.xs },
  label: { fontSize: 12, fontWeight: "700", color: HB_COLORS.textSecondary, marginTop: HB_SPACING.xs },
  labelRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  hint: { fontSize: 13, color: HB_COLORS.textSecondary, textAlign: "center", paddingVertical: HB_SPACING.md },
  loader: { paddingVertical: HB_SPACING.md },
  error: { fontSize: 13, color: HB_COLORS.danger, marginTop: HB_SPACING.xs },
  sourceCard: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 52, paddingHorizontal: HB_SPACING.sm, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface },
  sourceName: { flex: 1, fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  sourceMeta: { fontSize: 12, color: HB_COLORS.textSecondary },
  option: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.xs, padding: HB_SPACING.sm, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface },
  optionText: { flex: 1, gap: 2 },
  optionTitle: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  optionDescription: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  targetList: { backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface, overflow: "hidden" },
  targetRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 52, paddingHorizontal: HB_SPACING.sm, paddingVertical: HB_SPACING.xs },
  targetDivider: { borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  targetName: { flex: 1, fontSize: 15, color: HB_COLORS.textPrimary },
  targetMeta: { maxWidth: "45%", fontSize: 12, color: HB_COLORS.textSecondary, textAlign: "right" },
  banner: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.xs, padding: HB_SPACING.sm, borderRadius: HB_RADIUS.surface, borderWidth: 1 },
  bannerInfo: { backgroundColor: SC_COLORS.blueBg, borderColor: SC_COLORS.blueBorder },
  bannerWarn: { backgroundColor: SC_COLORS.warnBg, borderColor: SC_COLORS.warnBorder },
  bannerText: { flex: 1, fontSize: 13, lineHeight: 19 },
  dangerBox: { gap: HB_SPACING.xs, padding: HB_SPACING.sm, backgroundColor: SC_COLORS.dangerBg, borderWidth: 1, borderColor: SC_COLORS.dangerBorder, borderRadius: HB_RADIUS.surface },
  dangerTitleRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  dangerTitle: { fontSize: 15, fontWeight: "700", color: HB_COLORS.danger },
  dangerText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.danger },
  confirmRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 44 },
  confirmText: { flex: 1, fontSize: 13, lineHeight: 19, fontWeight: "600", color: HB_COLORS.danger },
  storeCard: { gap: HB_SPACING.xs, padding: HB_SPACING.sm, backgroundColor: HB_COLORS.white, borderWidth: 1, borderColor: HB_COLORS.outlineMuted, borderRadius: HB_RADIUS.surface },
  storeCardFailed: { borderColor: SC_COLORS.dangerBorder, backgroundColor: SC_COLORS.dangerBg },
  storeTitleRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  storeTitle: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  grid: { flexDirection: "row", flexWrap: "wrap", rowGap: HB_SPACING.xs },
  gridCell: { width: "50%", flexDirection: "row", alignItems: "center", justifyContent: "space-between", paddingRight: HB_SPACING.sm, gap: HB_SPACING.xxs },
  gridLabel: { flexShrink: 1, fontSize: 12, color: HB_COLORS.textSecondary },
  gridValue: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  gridWarn: { color: HB_COLORS.warning },
  dangerLine: { gap: 2, padding: HB_SPACING.xs, backgroundColor: SC_COLORS.dangerBg, borderRadius: HB_RADIUS.control },
  dangerLineText: { fontSize: 12, lineHeight: 18, fontWeight: "600", color: HB_COLORS.danger },
  skipLine: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xxs, backgroundColor: SC_COLORS.warnBg, borderRadius: HB_RADIUS.control, paddingLeft: HB_SPACING.xs },
  skipText: { flex: 1, fontSize: 12, color: HB_COLORS.warning },
  footerRow: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  resultHero: { alignItems: "center", gap: HB_SPACING.xxs, paddingVertical: HB_SPACING.md },
  heroIcon: { width: 56, height: 56, borderRadius: 28, alignItems: "center", justifyContent: "center", backgroundColor: HB_COLORS.success },
  heroIconFailed: { backgroundColor: HB_COLORS.danger },
  heroTitle: { fontSize: 20, fontWeight: "700", color: HB_COLORS.textPrimary },
  heroSub: { fontSize: 12, color: HB_COLORS.textSecondary, textAlign: "center" },
  heroFailed: { fontSize: 13, fontWeight: "600", color: HB_COLORS.danger },
  resultLine: { flexDirection: "row", gap: HB_SPACING.sm },
  resultStat: { flex: 1, gap: 2 },
  resultValue: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  resultNote: { fontSize: 12, fontWeight: "400", color: HB_COLORS.textSecondary },
  failedText: { fontSize: 13, lineHeight: 19, color: HB_COLORS.danger },
  skippedGroup: { gap: HB_SPACING.xs },
  skippedStore: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  skippedRow: { gap: 2, paddingVertical: HB_SPACING.xxs, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  skippedName: { fontSize: 14, color: HB_COLORS.textPrimary },
  skippedMeta: { fontSize: 12, color: HB_COLORS.textSecondary },
  skippedMore: { fontSize: 12, color: HB_COLORS.textSecondary },
});
