import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useSyncExternalStore } from "react";
import { useTranslation } from "react-i18next";
import {
  ActivityIndicator,
  ScrollView,
  Text,
  useWindowDimensions,
  View,
} from "react-native";

import type {
  CatalogRefreshStep,
  CatalogSummary,
} from "@hb/pos-domain/features/catalog/catalog-refresh-contract";

import {
  catalogMaintenanceText,
  resolveCatalogMaintenanceLocale,
  type CatalogMaintenanceCopyKey,
  type CatalogMaintenanceLocale,
} from "@hb/pos-domain/features/catalog/maintenance/catalog-maintenance-copy";
import type {
  CatalogMaintenancePresenter,
  CatalogMaintenanceState,
  CatalogRefreshProgress,
  CatalogRefreshWarningCode,
} from "./catalog-maintenance-presenter";

import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import { PosPill } from "@/ui/controls/pos-pill";
import { PosPageHeader, PosScreenFrame } from "@/ui/shell/pos-screen-frame";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
} from "@/ui/theme";

export const CATALOG_MAINTENANCE_MIN_TOUCH_TARGET = 44;
const CATALOG_MAINTENANCE_COMPACT_WIDTH = 900;

type CatalogMaintenanceScreenProps = Readonly<{
  locale?: CatalogMaintenanceLocale;
  presenter: CatalogMaintenancePresenter;
  onBack?(): void;
}>;

type StepStatus = "Pending" | "Current" | "Complete";

/**
 * 目录更新页：左侧大卡片看当前目录与刷新进度，右侧是安全说明和唯一的主按钮。
 * 横屏并排；窄屏以可滚动的顺序堆叠，避免进度步骤被截断。
 */
export function CatalogMaintenanceScreen({
  locale: localeOverride,
  presenter,
  onBack,
}: CatalogMaintenanceScreenProps) {
  usePosThemeVersion();
  const state = useSyncExternalStore(
    presenter.subscribe,
    presenter.getState,
    presenter.getState,
  );
  const { width } = useWindowDimensions();
  const { i18n } = useTranslation();
  const locale =
    localeOverride ??
    resolveCatalogMaintenanceLocale(i18n.resolvedLanguage ?? i18n.language);
  const t = (
    key: CatalogMaintenanceCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => catalogMaintenanceText(locale, key, values);
  const compact = width < CATALOG_MAINTENANCE_COMPACT_WIDTH;
  const refreshInProgress = state.refresh.kind === "running";
  const catalogLoading = state.catalog.kind === "loading";

  return (
    // 刷新在应用内后台继续，离开本页不会中断，所以不锁侧栏和返回键。
    <PosScreenFrame
      active="catalog-maintenance"
      header={
        <PosPageHeader
          backLabel={t("action.back")}
          backTestID="catalog-maintenance-back"
          onBack={onBack}
          subtitle={t("header.subtitle")}
          title={t("header.title")}
        />
      }
      testID="catalog-maintenance-screen"
    >
      <ScrollView
        contentContainerStyle={styles.scrollContent}
        style={styles.scrollView}
      >
        <View
          style={[styles.page, compact && styles.pageCompact]}
          testID="catalog-maintenance-workspace"
        >
          <View style={[styles.statusPanel, compact && styles.panelCompact]}>
            <Text style={styles.panelTitle}>{t("status.panelLabel")}</Text>
            <StatusContent locale={locale} state={state} />
          </View>
          <View
            style={[styles.sideColumn, compact && styles.sideColumnCompact]}
            testID="catalog-maintenance-side-panel"
          >
            <View style={[styles.actionPanel, compact && styles.panelCompact]}>
              <Text style={styles.panelTitle}>{t("action.title")}</Text>
              <Text style={styles.actionCopy}>{t("action.copy")}</Text>
              <View style={styles.backgroundHint}>
                <MaterialCommunityIcons
                  accessible={false}
                  color={posColors.blue}
                  name="information-outline"
                  size={24}
                />
                <Text style={styles.backgroundHintText}>
                  {t("action.background")}
                </Text>
              </View>
              <Text style={styles.safetyFootnote}>{t("action.footnote")}</Text>
            </View>
            {/* 本页唯一的主按钮；刷新中禁用并换成「正在刷新目录…」。 */}
            <PosButton
              disabled={catalogLoading || refreshInProgress}
              icon={refreshInProgress ? "loading" : "refresh"}
              label={t(
                refreshInProgress ? "action.refreshing" : "action.refresh",
              )}
              onPress={() => void presenter.refresh()}
              size="primary"
              testID="catalog-maintenance-refresh"
              tone="primary"
            />
          </View>
        </View>
      </ScrollView>
    </PosScreenFrame>
  );
}

function StatusContent({
  locale,
  state,
}: Readonly<{
  locale: CatalogMaintenanceLocale;
  state: CatalogMaintenanceState;
}>) {
  usePosThemeVersion();
  const refreshTestId =
    state.refresh.kind === "running"
      ? "catalog-maintenance-downloading"
      : `catalog-maintenance-${state.refresh.kind}`;
  const progress =
    state.refresh.kind === "idle" ? null : state.refresh.progress;

  return (
    <View style={styles.statusBody} testID={refreshTestId}>
      <LocalCatalogSummary catalog={state.catalog} locale={locale} />
      <View style={styles.divider} />
      <RefreshStatus
        locale={locale}
        progress={progress}
        refresh={state.refresh}
      />
      {state.refresh.kind !== "warning" && state.catalog.summary !== null ? (
        <CatalogContinuityNote locale={locale} />
      ) : null}
    </View>
  );
}

function LocalCatalogSummary({
  catalog,
  locale,
}: Readonly<{
  catalog: CatalogMaintenanceState["catalog"];
  locale: CatalogMaintenanceLocale;
}>) {
  usePosThemeVersion();
  const t = (
    key: CatalogMaintenanceCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => catalogMaintenanceText(locale, key, values);
  if (catalog.kind === "loading") {
    return (
      <View style={styles.catalogNotice} testID="catalog-maintenance-catalog-loading">
        <ActivityIndicator color={posColors.blue} size="small" />
        <Text style={styles.catalogLoadingText}>{t("status.catalogLoading")}</Text>
      </View>
    );
  }
  if (catalog.summary === null) {
    return (
      <View
        style={[styles.catalogNotice, styles.catalogUnavailable]}
        testID="catalog-maintenance-catalog-unavailable"
      >
        <Text
          style={[
            styles.catalogUnavailableTitle,
            catalog.kind === "failed" && styles.failedTitle,
          ]}
        >
          {t(
            catalog.kind === "failed"
              ? "status.catalogMetadataError"
              : "status.catalogUnavailable",
          )}
        </Text>
        {catalog.kind === "failed" ? (
          <Text style={styles.errorCode}>
            {t("status.safeError", { errorCode: catalog.errorCode })}
          </Text>
        ) : null}
      </View>
    );
  }

  return <CatalogSummaryGrid locale={locale} summary={catalog.summary} />;
}

function CatalogSummaryGrid({
  locale,
  summary,
}: Readonly<{
  locale: CatalogMaintenanceLocale;
  summary: CatalogSummary;
}>) {
  usePosThemeVersion();
  const t = (key: CatalogMaintenanceCopyKey) =>
    catalogMaintenanceText(locale, key);
  return (
    <View style={styles.summaryGrid} testID="catalog-maintenance-summary">
      <Metric label={t("metric.version")} value={summary.catalogVersion} />
      <Metric label={t("metric.items")} value={String(summary.itemCount)} />
      <Metric
        label={t("metric.activated")}
        value={formatCatalogTimestamp(summary.activatedAt)}
      />
      {/* 快照 ID 很长：放不下时从中间省略，首尾都留着方便核对。 */}
      <Metric
        ellipsizeMiddle
        label={t("metric.snapshot")}
        value={summary.snapshotId}
      />
    </View>
  );
}

function RefreshStatus({
  locale,
  progress,
  refresh,
}: Readonly<{
  locale: CatalogMaintenanceLocale;
  progress: CatalogRefreshProgress | null;
  refresh: CatalogMaintenanceState["refresh"];
}>) {
  usePosThemeVersion();
  const t = (
    key: CatalogMaintenanceCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => catalogMaintenanceText(locale, key, values);

  if (refresh.kind === "idle") {
    return (
      <View style={styles.refreshStatus}>
        <Text style={styles.statusTitle}>{t("status.idle")}</Text>
        <Text style={styles.statusHint}>{t("status.idleHint")}</Text>
      </View>
    );
  }

  const title =
    refresh.kind === "success"
      ? t("status.success")
      : refresh.kind === "warning"
        ? t("status.warning")
      : refresh.kind === "failed"
        ? t("status.failed")
        : t("status.downloading");
  // 准备阶段还没有真实百分比：只显示等待提示和已用时，不画进度条。
  const preparing =
    progress !== null &&
    progress.currentStep === "prepare" &&
    progress.steps[0]?.percent === 0;
  return (
    <View
      accessibilityRole={
        refresh.kind === "failed" || refresh.kind === "warning"
          ? "alert"
          : undefined
      }
      style={styles.refreshStatus}
    >
      <View style={styles.statusHeader}>
        <Text
          style={[
            styles.statusTitle,
            styles.statusTitleInRow,
            refresh.kind === "success" && styles.successTitle,
            refresh.kind === "warning" && styles.warningTitle,
            refresh.kind === "failed" && styles.failedTitle,
          ]}
        >
          {title}
        </Text>
        {progress && !preparing ? (
          <View accessibilityLiveRegion="polite" style={styles.currentStepPill}>
            <PosPill
              label={t("progress.currentStep", {
                step: t(stepCopyKey(progress.currentStep)),
              })}
              tone="accent"
            />
          </View>
        ) : null}
        {progress ? (
          <Text style={styles.progressElapsed} testID="catalog-maintenance-elapsed">
            {t("progress.elapsed", {
              elapsed: formatElapsedMilliseconds(progress.elapsedMilliseconds),
            })}
          </Text>
        ) : null}
      </View>
      {progress ? (
        <RefreshProgress
          locale={locale}
          preparing={preparing}
          progress={progress}
        />
      ) : null}
      {refresh.kind === "failed" ? (
        <View style={[styles.resultNote, styles.resultNoteFailed]}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.red}
            name="alert-circle-outline"
            size={24}
          />
          <Text style={styles.errorCode}>
            {t("status.safeError", { errorCode: refresh.errorCode })}
          </Text>
        </View>
      ) : null}
      {refresh.kind === "warning" ? (
        <View style={[styles.resultNote, styles.resultNoteWarning]}>
          <MaterialCommunityIcons
            accessible={false}
            color={posColors.yellowInk}
            name="alert-outline"
            size={24}
          />
          <Text style={styles.warningCopy}>
            {t(warningCopyKey(refresh.warningCode))}
          </Text>
        </View>
      ) : null}
    </View>
  );
}

function RefreshProgress({
  locale,
  preparing,
  progress,
}: Readonly<{
  locale: CatalogMaintenanceLocale;
  preparing: boolean;
  progress: CatalogRefreshProgress;
}>) {
  usePosThemeVersion();
  const t = (
    key: CatalogMaintenanceCopyKey,
    values?: Readonly<Record<string, string | number>>,
  ) => catalogMaintenanceText(locale, key, values);
  const overallPercent = formatPercent(progress.overallPercent);

  if (preparing) {
    return (
      <View style={styles.progressSection} testID="catalog-maintenance-progress">
        <View
          accessibilityLiveRegion="polite"
          style={styles.progressPreparing}
          testID="catalog-maintenance-preparing"
        >
          <ActivityIndicator
            accessibilityLabel={t("progress.preparing")}
            color={posColors.accent}
            size="small"
          />
          <Text style={styles.progressPreparingText}>{t("progress.preparing")}</Text>
        </View>
      </View>
    );
  }

  return (
    <View style={styles.progressSection} testID="catalog-maintenance-progress">
      <View style={styles.progressRow}>
        <View
          accessibilityLabel={t("progress.accessibility", {
            percent: overallPercent,
          })}
          accessibilityRole="progressbar"
          accessibilityValue={{ max: 100, min: 0, now: progress.overallPercent }}
          style={styles.progressTrack}
          testID="catalog-maintenance-overall-progress"
        >
          <View
            style={[
              styles.progressFill,
              { width: `${progress.overallPercent}%` },
            ]}
          />
        </View>
        {/* 大字只显示百分比；完整的「总进度：x%」留给读屏。 */}
        <Text
          accessibilityLabel={t("progress.total", { percent: overallPercent })}
          style={styles.progressTotal}
          testID="catalog-maintenance-overall-percent"
        >
          {overallPercent}%
        </Text>
      </View>
      <View>
        {progress.steps.map((step, index) => {
          const percent = formatPercent(step.percent);
          const title = t(stepCopyKey(step.step));
          const status = stepStatus(progress, step.step, step.percent);
          const detail = formatStepDetail(step, (completed, total) =>
            t("progress.pages", { completed, total }),
          );
          return (
            <View
              accessibilityLabel={t("progress.stepAccessibility", {
                percent,
                step: title,
              })}
              key={step.step}
              style={[styles.stepRow, index > 0 && styles.stepRowDivider]}
              testID={`catalog-maintenance-step-${step.step}`}
            >
              <MaterialCommunityIcons
                accessible={false}
                color={stepIconColor(status)}
                name={STEP_ICON[status]}
                size={24}
              />
              <Text
                numberOfLines={1}
                style={[styles.stepLabel, styles[`stepLabel${status}`]]}
              >
                {title}
              </Text>
              <Text numberOfLines={1} style={styles.stepDetail}>
                {detail ?? "—"}
              </Text>
              <Text style={[styles.stepPercent, styles[`stepPercent${status}`]]}>
                {percent}%
              </Text>
            </View>
          );
        })}
      </View>
    </View>
  );
}

function CatalogContinuityNote({
  locale,
}: Readonly<{ locale: CatalogMaintenanceLocale }>) {
  usePosThemeVersion();
  return (
    <View style={styles.continuityNote}>
      <MaterialCommunityIcons
        accessible={false}
        color={posColors.green}
        name="shield-check-outline"
        size={24}
      />
      <Text style={styles.continuityNoteText}>
        {catalogMaintenanceText(locale, "continuity")}
      </Text>
    </View>
  );
}

function Metric({
  ellipsizeMiddle = false,
  label,
  value,
}: Readonly<{ ellipsizeMiddle?: boolean; label: string; value: string }>) {
  usePosThemeVersion();
  return (
    <View style={styles.metric}>
      <Text numberOfLines={1} style={styles.metricLabel}>
        {label}
      </Text>
      {/* 四张卡并排后每张较窄：长值先缩字号，再省略，文本内容保持完整。 */}
      <Text
        adjustsFontSizeToFit
        ellipsizeMode={ellipsizeMiddle ? "middle" : "tail"}
        minimumFontScale={0.6}
        numberOfLines={1}
        style={styles.metricValue}
      >
        {value}
      </Text>
    </View>
  );
}

// 步骤图标：做完打绿勾，进行中是下载进度，未开始是空心圆。
const STEP_ICON: Readonly<Record<StepStatus, PosIconName>> = {
  Complete: "check-circle",
  Current: "progress-download",
  Pending: "circle-outline",
};

// 颜色在渲染时才从当前主题取，换主题后步骤图标跟着变。
function stepIconColor(status: StepStatus): string {
  switch (status) {
    case "Complete":
      return posColors.green;
    case "Current":
      return posColors.accent;
    case "Pending":
      return posColors.mutedInk;
  }
}

function stepCopyKey(step: CatalogRefreshStep): CatalogMaintenanceCopyKey {
  return `step.${step}` as CatalogMaintenanceCopyKey;
}

function warningCopyKey(
  warningCode: CatalogRefreshWarningCode,
): CatalogMaintenanceCopyKey {
  switch (warningCode) {
    case "catalog-runtime-reload-failed":
      return "warning.runtimeReload";
    case "catalog-activation-verification-failed":
      return "warning.activationVerification";
  }
}

function stepStatus(
  progress: CatalogRefreshProgress,
  step: CatalogRefreshStep,
  percent: number,
): "Pending" | "Current" | "Complete" {
  if (percent === 100) return "Complete";
  return progress.currentStep === step ? "Current" : "Pending";
}

function formatPercent(percent: number): string {
  const rounded = Math.round(percent * 100) / 100;
  return Number.isInteger(rounded) ? String(rounded) : String(rounded);
}

function formatElapsedMilliseconds(elapsedMilliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(elapsedMilliseconds / 1_000));
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}`;
}

function formatStepDetail(
  step: CatalogRefreshProgress["steps"][number],
  formatPages: (completed: number, total: number) => string,
): string | null {
  const itemDetail =
    step.completedItemCount !== undefined && step.totalItemCount !== undefined
      ? `${step.completedItemCount} / ${step.totalItemCount}`
      : null;
  const pageDetail =
    step.completedPageCount !== undefined && step.totalPageCount !== undefined
      ? formatPages(step.completedPageCount, step.totalPageCount)
      : null;
  return [itemDetail, pageDetail].filter((value): value is string => value !== null).join(" · ") || null;
}

/** 目录仓储保存 canonical ISO；页面统一显示到分钟，避免因本地时区重解释启用事实。 */
function formatCatalogTimestamp(value: string): string {
  return value.replace(/:\d{2}(?:\.\d+)?Z$/, "Z");
}

const styles = createPosStyles((colors) => ({
  scrollView: { flex: 1 },
  scrollContent: { flexGrow: 1 },
  page: { flexDirection: "row", flexGrow: 1, gap: 12, padding: 12 },
  pageCompact: { flexDirection: "column" },
  statusPanel: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    // 横屏占满右侧说明栏以外的宽度；窄屏堆叠时由 panelCompact 改回按内容高度。
    flexBasis: 0,
    flexGrow: 1,
    flexShrink: 1,
    gap: 10,
    minWidth: 0,
    paddingHorizontal: 16,
    paddingVertical: 14,
  },
  panelCompact: { flexBasis: "auto", flexGrow: 0 },
  panelTitle: { color: colors.ink, fontSize: 18, fontWeight: "900" },
  statusBody: { flexGrow: 1, gap: 10 },
  divider: { backgroundColor: colors.border, height: 1, marginVertical: 4 },
  catalogNotice: {
    alignItems: "center",
    backgroundColor: colors.surfaceAlt,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flexDirection: "row",
    gap: 10,
    minHeight: 72,
    paddingHorizontal: 16,
    paddingVertical: 12,
  },
  catalogLoadingText: { color: colors.mutedInk, fontSize: 16, fontWeight: "700" },
  catalogUnavailable: {
    alignItems: "flex-start",
    flexDirection: "column",
    gap: 4,
    justifyContent: "center",
  },
  catalogUnavailableTitle: { color: colors.ink, fontSize: 18, fontWeight: "900" },
  summaryGrid: { flexDirection: "row", flexWrap: "wrap", gap: 8 },
  metric: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1,
    // 四张等宽并排；宽度不够（窄屏）时自动换行。
    flexBasis: 0,
    flexGrow: 1,
    minWidth: 140,
    paddingHorizontal: 14,
    paddingVertical: 10,
  },
  metricLabel: { color: colors.mutedInk, fontSize: 13, fontWeight: "800" },
  metricValue: {
    color: colors.ink,
    fontSize: 19,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    marginTop: 2,
  },
  refreshStatus: { gap: 10 },
  statusHeader: {
    alignItems: "center",
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
  },
  statusTitle: { color: colors.ink, fontSize: 18, fontWeight: "900", lineHeight: 26 },
  statusTitleInRow: { flexShrink: 1 },
  successTitle: { color: colors.green },
  warningTitle: { color: colors.yellowInk },
  failedTitle: { color: colors.red },
  statusHint: { color: colors.mutedInk, fontSize: 15, fontWeight: "600", lineHeight: 22 },
  currentStepPill: { flexShrink: 1 },
  progressElapsed: {
    color: colors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "700",
    marginLeft: "auto",
  },
  progressSection: { gap: 6 },
  progressPreparing: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
    minHeight: CATALOG_MAINTENANCE_MIN_TOUCH_TARGET,
  },
  progressPreparingText: { color: colors.mutedInk, fontSize: 15, fontWeight: "700" },
  progressRow: { alignItems: "center", flexDirection: "row", gap: 14 },
  progressTrack: {
    backgroundColor: colors.surfaceSunken,
    borderRadius: 11,
    flex: 1,
    height: 22,
    overflow: "hidden",
  },
  progressFill: { backgroundColor: colors.accent, borderRadius: 11, height: "100%" },
  progressTotal: {
    color: colors.ink,
    fontSize: 26,
    fontVariant: ["tabular-nums"],
    fontWeight: "900",
    minWidth: 64,
    textAlign: "right",
  },
  stepRow: { alignItems: "center", flexDirection: "row", gap: 10, minHeight: 50 },
  stepRowDivider: { borderTopColor: colors.surfaceSunken, borderTopWidth: 1 },
  stepLabel: { flex: 1, fontSize: 15.5, fontWeight: "800" },
  stepLabelPending: { color: colors.mutedInk },
  stepLabelCurrent: { color: colors.ink },
  stepLabelComplete: { color: colors.ink },
  stepDetail: {
    color: colors.mutedInk,
    fontSize: 13,
    fontVariant: ["tabular-nums"],
    fontWeight: "600",
    minWidth: 96,
    textAlign: "right",
  },
  stepPercent: {
    fontSize: 15.5,
    fontVariant: ["tabular-nums"],
    fontWeight: "800",
    minWidth: 62,
    textAlign: "right",
  },
  stepPercentPending: { color: colors.mutedInk },
  stepPercentCurrent: { color: colors.accent },
  stepPercentComplete: { color: colors.ink },
  resultNote: {
    alignItems: "center",
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  resultNoteFailed: { backgroundColor: colors.redSoft, borderColor: colors.redBorder },
  resultNoteWarning: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellowBorder,
  },
  errorCode: {
    color: colors.red,
    flexShrink: 1,
    fontFamily: "Courier",
    fontSize: 15,
    fontWeight: "700",
  },
  warningCopy: {
    color: colors.yellowInk,
    flex: 1,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 22,
  },
  continuityNote: {
    alignItems: "center",
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    // 贴在左侧卡片底部，刷新过程中一直看得到。
    marginTop: "auto",
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  continuityNoteText: {
    color: colors.green,
    flex: 1,
    fontSize: 15,
    fontWeight: "700",
    lineHeight: 21,
  },
  sideColumn: { gap: 12, width: 380 },
  sideColumnCompact: { width: "100%" },
  actionPanel: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blueBorder,
    borderRadius: posRadius.large,
    borderWidth: 1,
    flexGrow: 1,
    gap: 12,
    paddingHorizontal: 16,
    paddingVertical: 14,
  },
  actionCopy: { color: colors.ink, fontSize: 15, fontWeight: "700", lineHeight: 23 },
  backgroundHint: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.blueBorder,
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  backgroundHintText: {
    color: colors.blue,
    flex: 1,
    fontSize: 14,
    fontWeight: "700",
    lineHeight: 21,
  },
  safetyFootnote: {
    color: colors.mutedInk,
    fontSize: 12.5,
    lineHeight: 18,
    marginTop: "auto",
  },
}));
