import { useCallback, useEffect, useMemo, useRef, useState, type ComponentProps } from "react";
import { Platform, Pressable, ScrollView, Share, StyleSheet, View } from "react-native";
import { ActivityIndicator, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import * as Clipboard from "expo-clipboard";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useAuthStore } from "@/store/auth-store";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { buildNetworkReport, describeClientIp, describeSampleError, formatSydneyDateTime, type NetworkTestTranslate } from "./report";
import { createInitialLinks, DOWNLOAD_PROBE_KB, runNetworkTest } from "./runner";
import { computeLinkStats, computeVerdict, formatDuration, formatSpeed, rateLink, type NetworkVerdict } from "./stats";
import type { NetworkLinkResult, NetworkRating, NetworkTestLinkId, NetworkTestRunSummary } from "./types";
import { useNetworkTestAvailable } from "./availability";
import { useNetworkTestEnvironment } from "./use-network-test-context";

type IconName = ComponentProps<typeof MaterialCommunityIcons>["name"];

const LINK_ICONS: Record<NetworkTestLinkId, IconName> = {
  internet: "earth",
  server: "server-network",
  auth: "account-key-outline",
  database: "database-outline",
  image: "image-outline",
  download: "download-network-outline",
};

// 与设计图一致的浅底深字配色；成功/警告/错误沿用全局令牌的深色。
const RATING_TONES: Record<NetworkRating | "neutral", { background: string; text: string; icon: IconName }> = {
  good: { background: "#ECFDF3", text: HB_COLORS.success, icon: "check-circle" },
  fair: { background: "#FFFAEB", text: HB_COLORS.warning, icon: "alert-circle" },
  bad: { background: "#FEF3F2", text: HB_COLORS.danger, icon: "close-circle" },
  neutral: { background: HB_COLORS.surfaceMuted, text: HB_COLORS.textSecondary, icon: "minus-circle-outline" },
};

const VERDICT_TONES: Record<NetworkVerdict["tone"], { background: string; border: string; iconBackground: string; title: string; body: string; icon: IconName }> = {
  good: { background: "#ECFDF3", border: "#ABEFC6", iconBackground: "#DCFAE6", title: "#085D3A", body: "#05603A", icon: "check-circle-outline" },
  warn: { background: "#FFFAEB", border: "#FEDF89", iconBackground: "#FEF0C7", title: "#93370D", body: "#7A2E0E", icon: "alert-outline" },
  bad: { background: "#FEF3F2", border: "#FECDCA", iconBackground: "#FEE4E2", title: "#912018", body: "#7A271A", icon: "close-network-outline" },
  neutral: { background: HB_COLORS.surface, border: HB_COLORS.outlineMuted, iconBackground: HB_COLORS.surfaceMuted, title: HB_COLORS.textPrimary, body: HB_COLORS.textSecondary, icon: "stop-circle-outline" },
};

const SPARK_HEIGHT = 30;

function emptySummary(accessToken: string | null): NetworkTestRunSummary {
  return {
    links: createInitialLinks({ accessToken }),
    clientIp: null,
    requestId: null,
    startedAtIso: new Date().toISOString(),
    finishedAtIso: null,
    cancelled: false,
  };
}

/** 进度：已完成请求数 / 计划请求数（跳过与「服务器未提供」的不计）。 */
function computeProgress(links: readonly NetworkLinkResult[]) {
  const counted = links.filter((link) => link.state !== "skipped" && link.state !== "unavailable");
  const planned = counted.reduce((sum, link) => sum + link.plannedCount, 0);
  const done = counted.reduce(
    (sum, link) => sum + (link.state === "done" ? link.plannedCount : Math.min(link.samples.length, link.plannedCount)),
    0
  );
  const currentIndex = links.findIndex((link) => link.state === "running" || link.state === "pending");
  return {
    fraction: planned === 0 ? 1 : done / planned,
    current: currentIndex < 0 ? links.length : currentIndex + 1,
    // 每次请求连同间隔大约 0.6 秒，下载按 2 秒估；只用于「约 N 秒」提示。
    remainingSeconds: Math.max(1, Math.ceil((planned - done) * 0.6)),
  };
}

function RatingChip({ rating, label }: { rating: NetworkRating | "neutral"; label: string }) {
  const tone = RATING_TONES[rating];
  return (
    <View style={[styles.rateChip, { backgroundColor: tone.background }]}>
      <MaterialCommunityIcons name={tone.icon} size={12} color={tone.text} />
      <Text style={[styles.rateText, { color: tone.text }]} maxFontSizeMultiplier={1.3}>
        {label}
      </Text>
    </View>
  );
}

/** 每次请求一根柱：越高越慢；失败满高红色；还没测到的位置留空，宽度不跳。 */
function Sparkline({ link }: { link: NetworkLinkResult }) {
  const successTimes = link.samples.filter((sample) => sample.ok).map((sample) => sample.ms);
  const scaleMax = Math.max(...successTimes, 1);
  const median = successTimes.length ? [...successTimes].sort((a, b) => a - b)[Math.floor(successTimes.length / 2)] : 0;
  return (
    <View style={styles.spark} accessible={false}>
      {Array.from({ length: link.plannedCount }, (_, index) => {
        const sample = link.samples[index];
        if (!sample) return <View key={index} style={styles.sparkSlot} />;
        if (!sample.ok) return <View key={index} style={[styles.sparkBar, styles.sparkBarFailed, { height: SPARK_HEIGHT }]} />;
        // 明显慢于中位数（2 倍且超过 300ms）的标成黄色，一眼看出抖动。
        const slow = sample.ms > median * 2 && sample.ms > 300;
        return (
          <View
            key={index}
            style={[styles.sparkBar, slow ? styles.sparkBarSlow : null, { height: Math.max(3, (sample.ms / scaleMax) * SPARK_HEIGHT) }]}
          />
        );
      })}
    </View>
  );
}

function LinkRow({
  link,
  serverHost,
  expanded,
  onToggle,
}: {
  link: NetworkLinkResult;
  serverHost: string;
  expanded: boolean;
  onToggle: () => void;
}) {
  const { t: translate } = useAppTranslation("networkTest");
  const t: NetworkTestTranslate = (key, params) => translate(key, params);
  const stats = computeLinkStats(link.samples);
  const rating = rateLink(link);
  const multiSample = link.plannedCount > 1;
  const running = link.state === "running";
  const done = link.state === "done";

  let description = t(`links.${link.id}.desc`, { host: serverHost, kb: DOWNLOAD_PROBE_KB });
  if (link.state === "skipped") description = t("state.skippedNotLoggedIn");
  else if (link.state === "pending" && link.id === "auth") description = t("state.willSkip");
  else if (link.state === "unavailable") description = t("state.unavailable");
  else if (running && multiSample) {
    description = `${t(`links.${link.id}.desc`, { host: serverHost })} · ${t("sampleProgress", {
      current: Math.min(link.samples.length + 1, link.plannedCount),
      total: link.plannedCount,
    })}`;
  } else if (done && multiSample) {
    description = `${t(`links.${link.id}.desc`, { host: serverHost })} · ${t("successCount", { success: stats.success, total: stats.total })}`;
  } else if (done && link.id === "download" && link.samples[0]?.ok && link.downloadBytes) {
    description = t("downloadDetail", {
      kb: Math.round(link.downloadBytes / 1024),
      seconds: (link.samples[0].ms / 1000).toFixed(1),
    });
  }

  let value: string | null = null;
  if (done) {
    const first = link.samples[0];
    if (link.id === "database") {
      value = first?.ok ? (link.serverLatencyMs == null ? t("state.databaseUnavailable") : formatDuration(link.serverLatencyMs)) : first ? describeSampleError(first, t) : null;
    } else if (link.id === "download") {
      value = first?.ok ? formatSpeed(link.downloadKBps) : first ? describeSampleError(first, t) : null;
    } else {
      value = stats.avgMs == null ? describeSampleError(link.samples[link.samples.length - 1], t) : formatDuration(stats.avgMs);
    }
  }

  // 主服务器是最关键的链路，测完默认展开统计与柱状图；其余链路点开才看。
  const showStats = done && multiSample && stats.success > 0 && (link.id === "server" || expanded);
  const showSpark = multiSample && link.samples.length > 0 && (running || link.id === "server" || expanded);
  const canExpand = done && link.samples.length > 0;

  return (
    <Pressable
      accessibilityRole={canExpand ? "button" : undefined}
      accessibilityState={canExpand ? { expanded } : undefined}
      disabled={!canExpand}
      onPress={onToggle}
      style={({ pressed }) => [styles.item, link.state === "pending" ? styles.itemPending : null, pressed ? styles.pressed : null]}
    >
      <View style={styles.itemTop}>
        <View style={[styles.itemIcon, running ? styles.itemIconRunning : null]}>
          <MaterialCommunityIcons name={LINK_ICONS[link.id]} size={19} color={running ? HB_COLORS.white : HB_COLORS.action} />
        </View>
        <View style={styles.itemName}>
          <Text style={styles.itemTitle} numberOfLines={1} maxFontSizeMultiplier={1.4}>
            {t(`links.${link.id}.name`)}
          </Text>
          <Text style={styles.itemDesc} numberOfLines={1} maxFontSizeMultiplier={1.4}>
            {description}
          </Text>
        </View>
        <View style={styles.itemValue}>
          {running ? <ActivityIndicator size={18} color={HB_COLORS.action} /> : null}
          {link.state === "pending" ? <Text style={styles.itemWaiting}>{t("state.waiting")}</Text> : null}
          {link.state === "skipped" ? <RatingChip rating="neutral" label={t("rating.skip")} /> : null}
          {link.state === "unavailable" ? <RatingChip rating="neutral" label={t("rating.unavailable")} /> : null}
          {done && value ? (
            <Text style={styles.itemValueText} numberOfLines={1} maxFontSizeMultiplier={1.3}>
              {value}
            </Text>
          ) : null}
          {done && rating ? <RatingChip rating={rating} label={t(`rating.${rating}`)} /> : null}
        </View>
      </View>

      {showStats ? (
        <View style={styles.statsRow}>
          {[
            { key: "min", value: formatDuration(stats.minMs) },
            { key: "p95", value: formatDuration(stats.p95Ms) },
            { key: "jitter", value: formatDuration(stats.jitterMs) },
            { key: "loss", value: `${Math.round(stats.lossRate * 100)}%` },
          ].map((item) => (
            <View key={item.key} style={styles.statCell}>
              <Text style={styles.statLabel}>{t(`stats.${item.key}`)}</Text>
              <Text style={styles.statValue} maxFontSizeMultiplier={1.3}>
                {item.value}
              </Text>
            </View>
          ))}
        </View>
      ) : null}

      {showSpark ? (
        <>
          <Sparkline link={link} />
          {done ? (
            <View style={styles.sparkLegend}>
              <Text style={styles.sparkLegendText}>{t("spark.legend")}</Text>
              <Text style={styles.sparkLegendText}>
                <Text style={{ color: HB_COLORS.danger }}>■ </Text>
                {t("spark.failed")}
              </Text>
            </View>
          ) : null}
        </>
      ) : null}

      {expanded && canExpand ? (
        <View style={styles.sampleList}>
          {link.samples.map((sample, index) => (
            <View key={index} style={styles.sampleRow}>
              <Text style={styles.sampleLabel}>{t("sampleRow", { index: index + 1 })}</Text>
              <Text style={[styles.sampleValue, !sample.ok ? { color: HB_COLORS.danger } : null]}>
                {sample.ok ? formatDuration(sample.ms) : `${describeSampleError(sample, t)} · ${formatDuration(sample.ms)}`}
              </Text>
              <Text style={styles.sampleStatus}>{sample.status ?? "--"}</Text>
            </View>
          ))}
        </View>
      ) : null}
    </Pressable>
  );
}

function ContextRow({ label, value, tag }: { label: string; value: string; tag?: { text: string; tone: "blue" | "gray" } }) {
  return (
    <View style={styles.ctxRow}>
      <Text style={styles.ctxLabel} maxFontSizeMultiplier={1.4}>
        {label}
      </Text>
      <View style={styles.ctxValueWrap}>
        <Text style={styles.ctxValue} numberOfLines={1} selectable maxFontSizeMultiplier={1.4}>
          {value}
        </Text>
        {tag ? (
          <Text style={[styles.tag, tag.tone === "blue" ? styles.tagBlue : styles.tagGray]} maxFontSizeMultiplier={1.3}>
            {tag.text}
          </Text>
        ) : null}
      </View>
    </View>
  );
}

/**
 * 网络测试页：放在登录守卫之外（app/network-test.tsx），未登录也能打开。
 * 进入即自动开始测试；测完给一句话结论，可复制 / 分享纯文本报告。
 */
export function NetworkTestScreen() {
  const { t: translate } = useAppTranslation(["networkTest", "login"]);
  const t: NetworkTestTranslate = useCallback((key, params) => translate(key, params), [translate]);
  const router = useRouter();
  const environment = useNetworkTestEnvironment();
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const { context } = environment;
  const [summary, setSummary] = useState<NetworkTestRunSummary | null>(null);
  const [running, setRunning] = useState(false);
  const [expanded, setExpanded] = useState<NetworkTestLinkId | null>(null);
  const [report, setReport] = useState<string | null>(null);
  const [copyFailed, setCopyFailed] = useState(false);
  const [toastVisible, setToastVisible] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const autoStartedRef = useRef(false);
  const reviewSession = !useNetworkTestAvailable();

  const start = useCallback(() => {
    if (!context) return;
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;
    setExpanded(null);
    setSummary(emptySummary(context.accessToken));
    setRunning(true);
    void runNetworkTest({
      context,
      signal: controller.signal,
      onUpdate: (next) => {
        // 已被新一轮测试替换的旧结果不再写入界面。
        if (abortRef.current === controller) setSummary(next);
      },
    }).finally(() => {
      if (abortRef.current === controller) setRunning(false);
    });
  }, [context]);

  // 进入页面即自动开始（只一次）；离开页面中止正在进行的请求。
  useEffect(() => {
    if (!context || reviewSession || autoStartedRef.current) return;
    autoStartedRef.current = true;
    start();
  }, [context, reviewSession, start]);
  useEffect(() => () => abortRef.current?.abort(), []);

  const links = summary?.links ?? createInitialLinks({ accessToken: context?.accessToken ?? null });
  const finished = Boolean(summary?.finishedAtIso) && !running;
  const verdict = useMemo(() => (summary && finished ? computeVerdict(summary) : null), [finished, summary]);
  const progress = computeProgress(links);
  const serverHost = context?.serverHost ?? "--";
  const serverSourceLabel = context ? t(`context.serverSource.${context.serverSource}`) : "";
  const appLabel = [
    environment.appVersionLabel,
    t(`login:buildInfo.channel.${environment.appChannelKind}`),
    environment.otaId ? t("context.ota", { id: environment.otaId }) : t("context.embedded"),
  ].join(" · ");
  const accountLabel = environment.accountLabel ?? t("context.notLoggedIn");

  const goBack = () => {
    if (router.canGoBack()) {
      router.back();
      return;
    }
    // 深链直接打开时没有上一页：未登录回登录页（「/」会先跳工作台再被踢回登录，中间白屏），已登录回首页。
    router.replace(isAuthenticated ? "/" : "/(auth)/login");
  };

  const copyReport = async () => {
    if (!summary || !verdict) return;
    const text = buildNetworkReport(
      {
        summary,
        verdict,
        serverHost,
        serverSourceLabel,
        deviceLabel: environment.deviceLabel,
        appLabel,
        accountLabel,
      },
      t
    );
    setReport(text);
    try {
      await Clipboard.setStringAsync(text);
      setCopyFailed(false);
      setToastVisible(true);
    } catch {
      // 复制失败仍然打开报告弹层，文字可长按手动复制。
      setCopyFailed(true);
    }
  };

  const verdictTone = verdict ? VERDICT_TONES[verdict.tone] : null;
  const verdictParams = {
    host: serverHost,
    avg: formatDuration(computeLinkStats(links.find((link) => link.id === "server")?.samples ?? []).avgMs),
  };

  if (reviewSession) {
    return (
      <SafeAreaView edges={["top", "left", "right", "bottom"]} style={styles.container}>
        <View style={styles.appbar}>
          <Pressable accessibilityRole="button" accessibilityLabel={t("back")} hitSlop={8} onPress={goBack} style={styles.backButton}>
            <MaterialCommunityIcons name="chevron-left" size={28} color={HB_COLORS.action} />
          </Pressable>
          <Text style={styles.appbarTitle}>{t("title")}</Text>
        </View>
        <Text style={styles.reviewNotice}>{t("reviewUnavailable")}</Text>
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView edges={["top", "left", "right"]} style={styles.container}>
      <View style={styles.appbar}>
        <Pressable accessibilityRole="button" accessibilityLabel={t("back")} hitSlop={8} onPress={goBack} style={styles.backButton}>
          <MaterialCommunityIcons name="chevron-left" size={28} color={HB_COLORS.action} />
        </Pressable>
        <Text style={styles.appbarTitle} maxFontSizeMultiplier={1.3}>
          {t("title")}
        </Text>
      </View>

      <ScrollView contentContainerStyle={styles.content} showsVerticalScrollIndicator={false}>
        {verdict && verdictTone ? (
          <View style={[styles.verdict, { backgroundColor: verdictTone.background, borderColor: verdictTone.border }]} accessibilityLiveRegion="polite">
            <View style={[styles.verdictIcon, { backgroundColor: verdictTone.iconBackground }]}>
              <MaterialCommunityIcons name={verdictTone.icon} size={24} color={verdictTone.title} />
            </View>
            <View style={styles.verdictText}>
              <Text style={[styles.verdictTitle, { color: verdictTone.title }]} maxFontSizeMultiplier={1.4}>
                {t(`verdict.${verdict.key}.title`)}
              </Text>
              <Text style={[styles.verdictBody, { color: verdictTone.body }]} maxFontSizeMultiplier={1.4}>
                {t(`verdict.${verdict.key}.body`, verdictParams)}
              </Text>
            </View>
          </View>
        ) : null}

        <View style={styles.card}>
          <ContextRow label={t("context.server")} value={serverHost} tag={context ? { text: serverSourceLabel, tone: "blue" } : undefined} />
          {finished ? (
            <ContextRow
              label={t("context.publicIp")}
              value={summary ? describeClientIp(summary, t) : t("context.publicIpUnknown")}
              tag={summary?.clientIp ? { text: t("context.publicIpTag"), tone: "gray" } : undefined}
            />
          ) : null}
          <ContextRow label={t("context.account")} value={accountLabel} />
          <ContextRow label={t("context.device")} value={environment.deviceLabel} />
          <ContextRow label={t("context.app")} value={appLabel} />
          {finished && summary?.finishedAtIso ? (
            <ContextRow
              label={t("context.time")}
              value={`${formatSydneyDateTime(summary.finishedAtIso).slice(0, 16)}${t("context.timeSuffix")}`}
            />
          ) : null}
        </View>

        {!finished ? (
          <View style={styles.card}>
            <View style={styles.progressHead}>
              <Text style={styles.progressTitle}>{t("progress.running", { current: progress.current, total: links.length })}</Text>
              <Text style={styles.progressHint}>{t("progress.remaining", { seconds: progress.remainingSeconds })}</Text>
            </View>
            <View style={styles.track}>
              <View style={[styles.trackFill, { width: `${Math.round(progress.fraction * 100)}%` }]} />
            </View>
          </View>
        ) : null}

        <View style={styles.list}>
          {links.map((link, index) => (
            <View key={link.id} style={index > 0 ? styles.itemDivider : null}>
              <LinkRow
                link={link}
                serverHost={serverHost}
                expanded={expanded === link.id}
                onToggle={() => setExpanded((current) => (current === link.id ? null : link.id))}
              />
            </View>
          ))}
        </View>
        {finished ? <Text style={styles.note}>{t("expandHint")}</Text> : null}
      </ScrollView>

      <SafeAreaView edges={["bottom"]} style={styles.bottomBar}>
        {running ? (
          <Pressable
            accessibilityRole="button"
            onPress={() => abortRef.current?.abort()}
            style={({ pressed }) => [styles.bottomButton, styles.bottomDanger, pressed ? styles.pressed : null]}
          >
            <MaterialCommunityIcons name="stop-circle-outline" size={18} color={HB_COLORS.danger} />
            <Text style={[styles.bottomText, { color: HB_COLORS.danger }]}>{t("actions.stop")}</Text>
          </Pressable>
        ) : (
          <>
            <Pressable
              accessibilityRole="button"
              disabled={!context}
              onPress={start}
              style={({ pressed }) => [styles.bottomButton, styles.bottomOutline, pressed ? styles.pressed : null]}
            >
              <MaterialCommunityIcons name="refresh" size={18} color={HB_COLORS.textPrimary} />
              <Text style={styles.bottomText}>{t("actions.retest")}</Text>
            </Pressable>
            <Pressable
              accessibilityRole="button"
              disabled={!finished}
              onPress={() => void copyReport()}
              style={({ pressed }) => [styles.bottomButton, styles.bottomSolid, !finished ? styles.disabled : null, pressed ? styles.pressed : null]}
            >
              <MaterialCommunityIcons name="content-copy" size={18} color={HB_COLORS.white} />
              <Text style={[styles.bottomText, { color: HB_COLORS.white }]}>{t("actions.copy")}</Text>
            </Pressable>
          </>
        )}
      </SafeAreaView>

      <BusinessSheet
        visible={report != null}
        title={t("report.sheetTitle")}
        subtitle={copyFailed ? t("report.copyFailed") : t("report.copied")}
        onDismiss={() => setReport(null)}
        footer={
          <View style={styles.sheetActions}>
            <Pressable
              accessibilityRole="button"
              onPress={() => report && void Share.share({ message: report })}
              style={({ pressed }) => [styles.bottomButton, styles.bottomOutline, pressed ? styles.pressed : null]}
            >
              <MaterialCommunityIcons name="share-variant-outline" size={18} color={HB_COLORS.textPrimary} />
              <Text style={styles.bottomText}>{t("actions.share")}</Text>
            </Pressable>
            <Pressable
              accessibilityRole="button"
              onPress={() => void copyReport()}
              style={({ pressed }) => [styles.bottomButton, styles.bottomSolid, pressed ? styles.pressed : null]}
            >
              <MaterialCommunityIcons name="content-copy" size={18} color={HB_COLORS.white} />
              <Text style={[styles.bottomText, { color: HB_COLORS.white }]}>{t("actions.copyAgain")}</Text>
            </Pressable>
          </View>
        }
      >
        <ScrollView horizontal style={styles.reportBox} contentContainerStyle={styles.reportContent}>
          <Text selectable style={styles.reportText}>
            {report}
          </Text>
        </ScrollView>
      </BusinessSheet>

      <Snackbar visible={toastVisible} onDismiss={() => setToastVisible(false)} duration={2000}>
        {t("report.copiedToast")}
      </Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: HB_COLORS.background },
  appbar: {
    height: 52,
    flexDirection: "row",
    alignItems: "center",
    backgroundColor: HB_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
    paddingHorizontal: HB_SPACING.xs,
  },
  backButton: { width: 40, height: 40, alignItems: "center", justifyContent: "center" },
  appbarTitle: { flex: 1, textAlign: "center", marginRight: 40, fontSize: 17, fontWeight: "700", color: HB_COLORS.textPrimary },
  reviewNotice: { padding: HB_SPACING.lg, color: HB_COLORS.textSecondary, textAlign: "center" },
  content: { padding: HB_SPACING.md, gap: HB_SPACING.sm, paddingBottom: HB_SPACING.lg },
  verdict: { flexDirection: "row", gap: HB_SPACING.sm, borderWidth: 1, borderRadius: HB_RADIUS.surface, padding: 14 },
  verdictIcon: { width: 40, height: 40, borderRadius: 20, alignItems: "center", justifyContent: "center" },
  verdictText: { flex: 1, gap: 3 },
  verdictTitle: { fontSize: 16, fontWeight: "700" },
  verdictBody: { fontSize: 13, lineHeight: 19 },
  card: {
    backgroundColor: HB_COLORS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    paddingHorizontal: 14,
    paddingVertical: HB_SPACING.sm,
    gap: 6,
  },
  ctxRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.sm },
  ctxLabel: { width: 56, fontSize: 13, color: HB_COLORS.textSecondary },
  ctxValueWrap: { flex: 1, flexDirection: "row", alignItems: "center", gap: 4, minWidth: 0 },
  ctxValue: { flexShrink: 1, fontSize: 13, fontWeight: "500", color: HB_COLORS.textPrimary },
  tag: { fontSize: 11, fontWeight: "600", paddingHorizontal: 6, paddingVertical: 1, borderRadius: 4, overflow: "hidden" },
  tagBlue: { backgroundColor: "#EFF4FF", color: HB_COLORS.action },
  tagGray: { backgroundColor: HB_COLORS.surfaceMuted, color: HB_COLORS.textSecondary },
  progressHead: { flexDirection: "row", justifyContent: "space-between", alignItems: "center", marginBottom: 4 },
  progressTitle: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  progressHint: { fontSize: 13, color: HB_COLORS.textSecondary },
  track: { height: 6, borderRadius: 3, backgroundColor: HB_COLORS.surfaceMuted, overflow: "hidden" },
  trackFill: { height: 6, borderRadius: 3, backgroundColor: HB_COLORS.brand },
  list: {
    backgroundColor: HB_COLORS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: HB_RADIUS.surface,
    overflow: "hidden",
  },
  itemDivider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: HB_COLORS.outlineMuted },
  item: { paddingHorizontal: 14, paddingVertical: HB_SPACING.sm },
  itemPending: { opacity: 0.5 },
  pressed: { opacity: 0.7 },
  disabled: { opacity: 0.5 },
  itemTop: { flexDirection: "row", alignItems: "center", gap: 10 },
  itemIcon: { width: 34, height: 34, borderRadius: 9, backgroundColor: "#EFF4FF", alignItems: "center", justifyContent: "center" },
  itemIconRunning: { backgroundColor: HB_COLORS.action },
  itemName: { flex: 1, minWidth: 0 },
  itemTitle: { fontSize: 14, fontWeight: "600", color: HB_COLORS.textPrimary },
  itemDesc: { fontSize: 12, color: HB_COLORS.textSecondary, marginTop: 1 },
  itemValue: { alignItems: "flex-end", gap: 3, maxWidth: 140 },
  itemValueText: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  itemWaiting: { fontSize: 11, color: HB_COLORS.textSecondary },
  rateChip: { flexDirection: "row", alignItems: "center", gap: 3, borderRadius: 10, paddingHorizontal: 7, paddingVertical: 2 },
  rateText: { fontSize: 11, fontWeight: "700" },
  statsRow: { flexDirection: "row", marginTop: 10, marginLeft: 44 },
  statCell: { flex: 1 },
  statLabel: { fontSize: 11, color: HB_COLORS.textSecondary },
  statValue: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  spark: { flexDirection: "row", alignItems: "flex-end", gap: 3, height: SPARK_HEIGHT, marginTop: 8, marginLeft: 44 },
  sparkSlot: { flex: 1 },
  sparkBar: { flex: 1, borderTopLeftRadius: 2, borderTopRightRadius: 2, backgroundColor: "#84ADFF" },
  sparkBarSlow: { backgroundColor: "#FDB022" },
  sparkBarFailed: { backgroundColor: HB_COLORS.danger, opacity: 0.85 },
  sparkLegend: { flexDirection: "row", justifyContent: "space-between", marginTop: 3, marginLeft: 44 },
  sparkLegendText: { fontSize: 10, color: "#98A2B3" },
  sampleList: { marginTop: 8, marginLeft: 44, gap: 2 },
  sampleRow: { flexDirection: "row", gap: HB_SPACING.xs },
  sampleLabel: { width: 72, fontSize: 12, color: HB_COLORS.textSecondary },
  sampleValue: { flex: 1, fontSize: 12, color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  sampleStatus: { fontSize: 12, color: HB_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  note: { fontSize: 11, color: "#98A2B3", textAlign: "center" },
  bottomBar: {
    flexDirection: "row",
    gap: 10,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: 10,
    paddingBottom: 10,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  bottomButton: {
    flex: 1,
    height: 46,
    borderRadius: 23,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 6,
  },
  bottomOutline: { borderWidth: 1, borderColor: HB_COLORS.outline, backgroundColor: HB_COLORS.white },
  bottomSolid: { backgroundColor: HB_COLORS.brand },
  bottomDanger: { borderWidth: 1, borderColor: "#FECDCA", backgroundColor: HB_COLORS.white },
  bottomText: { fontSize: 15, fontWeight: "600", color: HB_COLORS.textPrimary },
  sheetActions: { flexDirection: "row", gap: 10 },
  reportBox: {
    backgroundColor: HB_COLORS.surfaceMuted,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 10,
  },
  reportContent: { padding: HB_SPACING.sm },
  reportText: { fontFamily: Platform.select({ ios: "Menlo", default: "monospace" }), fontSize: 11.5, lineHeight: 18, color: HB_COLORS.textPrimary },
});
