// 抓拍详情（敏感生物识别信息）：抓拍流的行并排显示人脸小图与背景图；命中流的行（留存抓拍可能不存在）只显示命中图；
// 下面是比对结果、名单命中与通知发送情况。
//
// 约定（由 source-contract / capture-image-session 测试守住）：
// 1. 详情挂载（用户点击某条）后才请求整图（列表里只有 128px 缩略图，见 thumbnail-loader）；每次读取服务端都写审计；
// 2. 图片只以 base64 data URI 的形式存在于本组件 state 里，不由应用自己写磁盘缓存、不进 react-query、不进日志；
//    详情关闭 / 卸载 / 应用离开前台（切后台、下拉通知栏、应用切换器）时立即丢弃；
//    iOS 系统网络层会自行把响应写进缓存目录（即使 no-store），所以读取后另有清理器删除这些文件（缓解，非根治）；
// 3. 不显示姓名与事件细节；通知发送情况只有人数；服务端没有给出的时间不显示，不编造；
// 4. 「去复核」只跳转到合并页的名单分区（命中复核），复核本身仍在那里完成；命中流的行只在服务端说 canReview 时才显示这个按钮。
import { useEffect, useRef, useState, type ReactNode } from "react";
import { ActivityIndicator, AppState, Image, Modal, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useRouter, type Href } from "expo-router";
import { Button, IconButton, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { canViewFaceUnwelcome } from "./access";
import { faceMatchApi } from "./api";
import {
  buildHitTimeline,
  captureScoreParts,
  describeDecision,
  describeHitStatusTag,
  formatCaptureFullTime,
  resolveDetailReviewAction,
  type CaptureRow,
  type HitTimelineStep,
} from "./capture-logic";
import {
  createCaptureImageSession,
  type CaptureImageSession,
  type CaptureImageSlot,
  type CaptureImageStates,
} from "./capture-image-session";
import { createReviewImageScrubber } from "./http-cache-scrub-runtime";
import type { ReviewImageState } from "./image-session";
import { FACE_MATCH_PERMISSIONS } from "./constants";
import { BAND_LABEL_KEYS } from "./review-logic";
import { FACE_SOFT, MONO, Notice, Tag } from "./ui";

interface Props {
  /** 要查看的行（抓拍流或命中流）；父组件只在点击后才挂载本组件。 */
  result: CaptureRow;
  storeLabel: string;
  onClose: () => void;
}

export function CaptureResultDetail({ result, storeLabel, onClose }: Props) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const [states, setStates] = useState<CaptureImageStates>(() => new Map());
  // 字节已拿到但系统解码失败（内容损坏）的图片槽位。
  const [broken, setBroken] = useState<ReadonlySet<CaptureImageSlot>>(() => new Set());
  const [zoomSlot, setZoomSlot] = useState<CaptureImageSlot | null>(null);
  const onCloseRef = useRef(onClose);
  onCloseRef.current = onClose;

  const { captureId, faceImageId, sceneImageId } = result;
  // 命中流的行只有命中图可读；命中图已过保留期（imageAvailable=false）时不请求，直接显示「已被清理」。
  const hitTaskId = result.source === "hit" && result.hit?.imageAvailable ? result.hit.taskId : null;
  const hitGone = result.source === "hit" && !hitTaskId;

  useEffect(() => {
    // iOS 的系统 HTTP 缓存会把图片响应写进应用缓存目录（即使 no-store），读取后由清理器删掉（局限见 http-cache-scrub.ts）。
    const scrubber = createReviewImageScrubber();
    const session: CaptureImageSession = createCaptureImageSession({
      fetchers: {
        face: captureId && faceImageId ? (signal) => scrubber.wrap(() => faceMatchApi.fetchCaptureImage(captureId, faceImageId, signal)) : null,
        scene: captureId && sceneImageId ? (signal) => scrubber.wrap(() => faceMatchApi.fetchCaptureImage(captureId, sceneImageId, signal)) : null,
        hit: hitTaskId ? (signal) => scrubber.wrap(() => faceMatchApi.fetchHitImage(hitTaskId, signal)) : null,
      },
      onChange: setStates,
    });
    void session.start();
    // 关闭 / 切换 / 卸载：中止在途请求、清空全部图片引用，并清理系统缓存里的图片文件。
    return () => {
      session.dispose();
      void scrubber.release();
      setStates(new Map());
    };
  }, [captureId, faceImageId, sceneImageId, hitTaskId]);

  // 应用离开前台（含 iOS 的 inactive：下拉通知、应用切换器快照前）就关闭详情，图片随之丢弃。
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      if (next !== "active") onCloseRef.current();
    });
    return () => subscription.remove();
  }, []);

  const markBroken = (slot: CaptureImageSlot) => setBroken((current) => new Set(current).add(slot));
  const isHitRow = result.source === "hit";
  const hasAnyImage = isHitRow || Boolean(faceImageId || sceneImageId);
  const decision = describeDecision(result);
  const scoreParts = captureScoreParts(result);
  const { hit } = result;
  const statusTag = hit ? describeHitStatusTag(hit) : null;
  const reviewAction = resolveDetailReviewAction(result, {
    canOpenUnwelcome: canViewFaceUnwelcome(isAuthenticated, hasPermission, false),
    hasReviewPermission: hasPermission(FACE_MATCH_PERMISSIONS.unwelcomeReview),
  });
  const timeline = hit ? buildHitTimeline(hit) : [];
  // 命中图已过保留期：不发请求，直接给出「图片已被清理」的状态。
  const hitState = hitGone ? ({ status: "error", kind: "gone" } as const) : states.get("hit");

  const goReview = () => {
    onClose();
    router.push(reviewAction.href as Href);
  };

  return (
    <Modal
      visible
      animationType="slide"
      presentationStyle="fullScreen"
      statusBarTranslucent
      onRequestClose={() => {
        if (zoomSlot) setZoomSlot(null);
        else onClose();
      }}
    >
      <View style={[styles.root, { paddingTop: insets.top }]}>
        <View style={styles.header}>
          <IconButton icon="chevron-left" accessibilityLabel={t("common:actions.back")} onPress={onClose} />
          <Text style={styles.headerTitle} numberOfLines={1}>
            {t("capture.detail.title")}
          </Text>
          <View style={styles.headerSpacer} />
        </View>

        <ScrollView contentContainerStyle={styles.body}>
          <Notice tone="warning" text={t("capture.detail.sensitiveNotice")} icon="shield-lock-outline" />

          {isHitRow ? (
            <View style={styles.hitImageRow}>
              <ImageSlot state={hitState} hasId alt={t("capture.detail.hitImage")} isBroken={broken.has("hit")} onBroken={() => markBroken("hit")} onPress={() => setZoomSlot("hit")} />
            </View>
          ) : hasAnyImage ? (
            <View style={styles.imageRow}>
              <View style={styles.faceBox}>
                <ImageSlot state={states.get("face")} hasId={Boolean(faceImageId)} isBroken={broken.has("face")} alt={t("capture.detail.faceImage")} onBroken={() => markBroken("face")} onPress={() => setZoomSlot("face")} />
              </View>
              <View style={styles.sceneBox}>
                <ImageSlot state={states.get("scene")} hasId={Boolean(sceneImageId)} isBroken={broken.has("scene")} alt={t("capture.detail.sceneImage")} onBroken={() => markBroken("scene")} onPress={() => setZoomSlot("scene")} />
              </View>
            </View>
          ) : (
            <Notice tone="info" text={t("capture.detail.noImage")} />
          )}

          <View style={styles.card}>
            <Text style={styles.cardTitle}>{t("capture.detail.resultTitle")}</Text>
            <Row label={t("capture.detail.fields.timeCamera")}>
              <Text style={[styles.value, MONO]}>{`${formatCaptureFullTime(result.capturedAtUtc)} · ${result.cameraId}`}</Text>
            </Row>
            <Row label={t("capture.detail.fields.store")}>
              <Text style={styles.value}>{storeLabel}</Text>
            </Row>
            {/* 命中流的行没有评估（Match 模式不留存抓拍）时不显示判定行，免得写成「未比对」造成误解。 */}
            {result.source === "capture" || result.decision ? (
              <Row label={t("capture.detail.fields.decision")}>
                <View style={styles.tagRow}>
                  {result.isDuplicate ? <Tag label={t("capture.duplicate")} /> : null}
                  <Tag label={t(decision.labelKey)} tone={decision.tone} />
                </View>
              </Row>
            ) : null}
            {scoreParts.length > 0 ? (
              <Row label={t("capture.detail.fields.scores")}>
                <Text style={[styles.value, MONO]}>{scoreParts.map((part) => t(part.labelKey, { percent: part.percent })).join(" · ")}</Text>
              </Row>
            ) : null}
            {result.faceCount !== null && result.faceCount > 0 ? (
              <Row label={t("capture.detail.fields.faceCount")}>
                <Text style={[styles.value, MONO]}>{result.faceCount}</Text>
              </Row>
            ) : null}
            {hit ? (
              <Row label={t("capture.detail.fields.hit")}>
                <View style={styles.tagRow}>
                  <Text style={[styles.value, MONO]}>{t("capture.detail.hitSimilarity", { percent: hit.scorePercent })}</Text>
                  <Tag label={t(BAND_LABEL_KEYS[hit.band])} tone={hit.band === "high" ? "danger" : "warning"} />
                </View>
              </Row>
            ) : null}
            {statusTag ? (
              <Row label={t("capture.detail.fields.reviewStatus")}>
                <Tag label={t(statusTag.labelKey)} tone={statusTag.tone} icon={statusTag.icon ?? undefined} />
              </Row>
            ) : null}
          </View>

          <View style={styles.card}>
            <Text style={styles.cardTitle}>{t("capture.detail.notifyTitle")}</Text>
            {hit ? (
              <View>
                {timeline.map((step, index) => (
                  <TimelineRow key={step.labelKey} step={step} last={index === timeline.length - 1} />
                ))}
              </View>
            ) : (
              <Text style={styles.muted}>{t("capture.detail.noHit")}</Text>
            )}
          </View>
        </ScrollView>

        {reviewAction.visible ? (
          <View style={[styles.footer, { paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) }]}>
            {reviewAction.hintKey ? <Text style={styles.muted}>{t(reviewAction.hintKey)}</Text> : null}
            <Button mode="contained" disabled={!reviewAction.enabled} onPress={goReview}>
              {t("capture.detail.goReview")}
            </Button>
          </View>
        ) : null}

        {/* 放大查看：同一个详情内的覆盖层，关闭即不再持有额外引用（图片仍只在 states 里）。 */}
        {zoomSlot ? (
          <View style={[styles.zoom, { paddingTop: insets.top, paddingBottom: insets.bottom }]}>
            <IconButton icon="close" iconColor={HB_COLORS.white} accessibilityLabel={t("review.close")} onPress={() => setZoomSlot(null)} style={styles.zoomClose} />
            <ImageSlot
              large
              state={zoomSlot === "hit" ? hitState : states.get(zoomSlot)}
              hasId
              isBroken={broken.has(zoomSlot)}
              alt={zoomSlot === "face" ? t("capture.detail.faceImage") : zoomSlot === "hit" ? t("capture.detail.hitImage") : t("capture.detail.sceneImage")}
              onBroken={() => markBroken(zoomSlot)}
              onPress={() => setZoomSlot(null)}
            />
          </View>
        ) : null}
      </View>
    </Modal>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <View style={styles.row}>
      <Text style={styles.rowLabel}>{label}</Text>
      <View style={styles.rowValue}>{children}</View>
    </View>
  );
}

/** 时间线一行：实心点 = 已发生，空心点 = 尚未发生；有真实时间才显示时间。 */
function TimelineRow({ step, last }: { step: HitTimelineStep; last: boolean }) {
  const { t } = useAppTranslation("faceMatch");
  return (
    <View style={styles.timelineRow}>
      <View style={styles.timelineRail}>
        <View style={[styles.dot, step.state === "done" ? styles.dotDone : styles.dotWaiting]} />
        {last ? null : <View style={styles.line} />}
      </View>
      <View style={styles.timelineBody}>
        <Text style={styles.timelineLabel}>{t(step.labelKey, step.params)}</Text>
        {step.timeUtc ? <Text style={[styles.muted, MONO]}>{formatCaptureFullTime(step.timeUtc)}</Text> : null}
      </View>
    </View>
  );
}

/** 单张图片：Image 的 uri 永远是内存里的 data URI，不是后端地址。 */
function ImageSlot({
  state,
  hasId,
  isBroken,
  alt,
  large,
  onBroken,
  onPress,
}: {
  state: ReviewImageState | undefined;
  hasId: boolean;
  isBroken: boolean;
  alt: string;
  large?: boolean;
  onBroken: () => void;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("faceMatch");
  const boxStyle = large ? styles.imageLarge : styles.imageFill;
  if (!hasId) {
    return (
      <View style={[boxStyle, styles.center]}>
        <MaterialCommunityIcons name="image-off-outline" size={26} color={HB_COLORS.textSecondary} />
        <Text style={styles.imageErrorText}>{t("capture.detail.noImage")}</Text>
      </View>
    );
  }
  if (!state || state.status === "loading") {
    return (
      <View style={[boxStyle, styles.center]} accessibilityLabel={t("review.loadingImage")}>
        <ActivityIndicator color={large ? HB_COLORS.white : HB_COLORS.brand} />
      </View>
    );
  }
  if (state.status === "error" || isBroken) {
    const kind = state.status === "error" ? state.kind : "failed";
    return (
      <View style={[boxStyle, styles.center]}>
        <MaterialCommunityIcons name="image-off-outline" size={26} color={HB_COLORS.textSecondary} />
        <Text style={styles.imageErrorText}>{t(`review.imageErrors.${kind}`)}</Text>
      </View>
    );
  }
  return (
    <Pressable accessibilityRole="imagebutton" accessibilityLabel={alt} onPress={onPress} style={boxStyle}>
      <Image source={{ uri: state.uri }} resizeMode="contain" fadeDuration={0} style={styles.image} onError={onBroken} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: HB_COLORS.background },
  header: { flexDirection: "row", alignItems: "center" },
  headerTitle: { flex: 1, fontSize: 17, fontWeight: "700", color: HB_COLORS.textPrimary },
  headerSpacer: { width: 48 },
  body: { padding: HB_SPACING.md, gap: HB_SPACING.sm },
  imageRow: { flexDirection: "row", gap: HB_SPACING.xs, height: 180 },
  hitImageRow: { height: 220, borderRadius: HB_RADIUS.control, overflow: "hidden", backgroundColor: HB_COLORS.surfaceMuted },
  faceBox: { width: 135, borderRadius: HB_RADIUS.control, overflow: "hidden", backgroundColor: HB_COLORS.surfaceMuted },
  sceneBox: { flex: 1, borderRadius: HB_RADIUS.control, overflow: "hidden", backgroundColor: HB_COLORS.surfaceMuted },
  imageFill: { flex: 1, width: "100%" },
  imageLarge: { flex: 1, width: "100%" },
  image: { width: "100%", height: "100%" },
  center: { alignItems: "center", justifyContent: "center", gap: 6, padding: HB_SPACING.xs },
  imageErrorText: { fontSize: 12, textAlign: "center", color: HB_COLORS.textSecondary },
  card: {
    padding: HB_SPACING.md,
    gap: HB_SPACING.xs,
    borderRadius: HB_RADIUS.surface,
    backgroundColor: HB_COLORS.white,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: HB_COLORS.outlineMuted,
  },
  cardTitle: { fontSize: 14, fontWeight: "700", color: HB_COLORS.textPrimary },
  row: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.sm, minHeight: 28 },
  rowLabel: { fontSize: 13, color: HB_COLORS.textSecondary, flexShrink: 1 },
  rowValue: { flex: 1, alignItems: "flex-end" },
  value: { fontSize: 13, fontWeight: "600", color: HB_COLORS.textPrimary, textAlign: "right" },
  tagRow: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", justifyContent: "flex-end", gap: 6 },
  muted: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  timelineRow: { flexDirection: "row", gap: HB_SPACING.xs },
  timelineRail: { width: 14, alignItems: "center" },
  dot: { width: 10, height: 10, borderRadius: 5, marginTop: 4, borderWidth: 2 },
  dotDone: { backgroundColor: HB_COLORS.brand, borderColor: HB_COLORS.brand },
  dotWaiting: { backgroundColor: FACE_SOFT.accent, borderColor: HB_COLORS.outline },
  line: { flex: 1, width: 2, marginVertical: 2, backgroundColor: HB_COLORS.outlineMuted },
  timelineBody: { flex: 1, paddingBottom: HB_SPACING.sm, gap: 2 },
  timelineLabel: { fontSize: 14, color: HB_COLORS.textPrimary },
  footer: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    gap: HB_SPACING.xs,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  zoom: { ...StyleSheet.absoluteFillObject, backgroundColor: "rgba(0,0,0,0.94)" },
  zoomClose: { alignSelf: "flex-end" },
});
