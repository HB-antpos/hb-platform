// 复核弹框（敏感生物识别信息）：并排显示现场抓拍与条目参考照片，由两位不同的人分别判断。
//
// 约定（由 source-contract / image-session 测试守住）：
// 1. 弹框挂载（用户点击「复核」）后才请求图片：先现场抓拍与第一张参考照片，其余参考照片切换时才取；
// 2. 图片只以 base64 data URI 的形式存在于本组件 state 里，不由应用自己写磁盘缓存、不进 react-query、不进日志；
//    弹框关闭 / 卸载 / 应用离开前台（切后台、下拉通知栏、应用切换器）时立即丢弃；
//    iOS 系统网络层会自行把响应写进缓存目录（即使 no-store），所以读取后另有清理器删除这些文件（缓解，非根治）；
// 3. 不显示姓名与事件细节；相似度只写「相似度 NN%（不是同一个人的概率）」，档位用服务端 band 字段；
// 4. 第二复核人必须不同于第一复核人——由服务端强制，页面按服务端 canReview 控制按钮并如实展示错误；
// 5. 判断提交前在弹框内再确认一步（原生 Modal 之上看不到 Paper 弹层，确认放在弹框本身里）。
import { useEffect, useRef, useState } from "react";
import { ActivityIndicator, AppState, Image, Modal, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Button, IconButton, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { faceMatchApi } from "./api";
import { formatLocalDateTime } from "./dates";
import { extractHttpStatus } from "./errors";
import { createReviewImageScrubber } from "./http-cache-scrub-runtime";
import {
  CAPTURE_SLOT,
  createReviewImageSession,
  referenceSlot,
  type ReviewImageSession,
  type ReviewImageState,
  type ReviewImageStates,
} from "./image-session";
import {
  BAND_LABEL_KEYS,
  REVIEW_STEP_HINT_KEYS,
  canStepReference,
  canSubmitVerdict,
  formatSimilarityPercent,
  getReviewStep,
  getVerdictBlockReasonKey,
  stepReferenceIndex,
} from "./review-logic";
import { useFaceErrorMessage } from "./shared";
import type { FaceReviewVerdict, FaceUnwelcomeTask } from "./types";
import { MONO, Notice, Tag } from "./ui";

interface Props {
  /** 要复核的任务快照；父组件只在点击后才挂载本组件。 */
  task: FaceUnwelcomeTask;
  storeLabel: string;
  /** 功能未开启（只读）时不允许提交判断。 */
  writeBlocked: boolean;
  onClose: () => void;
  /** 判断已提交成功。 */
  onReviewed: (verdict: FaceReviewVerdict) => void;
  /** 提交被拒（409：任务刚被别人处理等），列表需要刷新，但不是「已记录」。 */
  onStale: () => void;
}

export function ReviewTaskModal({ task, storeLabel, writeBlocked, onClose, onReviewed, onStale }: Props) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const insets = useSafeAreaInsets();
  const [states, setStates] = useState<ReviewImageStates>(() => new Map());
  // 字节已拿到但系统解码失败（内容损坏）的图片槽位。
  const [broken, setBroken] = useState<ReadonlySet<string>>(() => new Set());
  const [referenceIndex, setReferenceIndex] = useState(0);
  const [zoomSlot, setZoomSlot] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<FaceReviewVerdict | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [errorText, setErrorText] = useState<string | null>(null);
  const sessionRef = useRef<ReviewImageSession | null>(null);
  const callbacks = useRef({ onClose, onReviewed, onStale });
  callbacks.current = { onClose, onReviewed, onStale };

  const taskId = task.taskId;
  const referenceCount = task.referencePhotoCount;
  const imageAvailable = task.imageAvailable;

  useEffect(() => {
    // 抓拍图已按保留期删除时没有可看的图，不发任何请求。
    if (!imageAvailable) return undefined;
    // iOS 的系统 HTTP 缓存会把图片响应写进应用缓存目录（即使 no-store），读取后由清理器删掉（局限见 http-cache-scrub.ts）。
    const scrubber = createReviewImageScrubber();
    const session = createReviewImageSession({
      referenceCount,
      fetchCapture: (signal) => scrubber.wrap(() => faceMatchApi.fetchTaskImage(taskId, signal)),
      fetchReference: (index, signal) => scrubber.wrap(() => faceMatchApi.fetchReferenceImage(taskId, index, signal)),
      onChange: setStates,
    });
    sessionRef.current = session;
    void session.start();
    // 关闭 / 切换任务 / 卸载：中止在途请求、清空全部图片引用，并清理系统缓存里的图片文件。
    return () => {
      session.dispose();
      void scrubber.release();
      sessionRef.current = null;
      setStates(new Map());
    };
  }, [taskId, referenceCount, imageAvailable]);

  // 应用离开前台（含 iOS 的 inactive：下拉通知、应用切换器快照前）就关闭弹框，图片随之丢弃。
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      if (next !== "active") callbacks.current.onClose();
    });
    return () => subscription.remove();
  }, []);

  const percent = formatSimilarityPercent(task);
  const step = getReviewStep(task);
  const blockedKey = getVerdictBlockReasonKey(task, writeBlocked);
  const verdictEnabled = canSubmitVerdict(task, writeBlocked) && !submitting;
  const indexes = Array.from({ length: Math.max(0, referenceCount) }, (_, index) => index);

  const markBroken = (slot: string) => setBroken((current) => new Set(current).add(slot));
  const selectReference = (delta: -1 | 1) => {
    const next = stepReferenceIndex(referenceIndex, referenceCount, delta);
    setReferenceIndex(next);
    void sessionRef.current?.loadReference(next);
  };

  const submit = (verdict: FaceReviewVerdict) => {
    setSubmitting(true);
    setErrorText(null);
    faceMatchApi
      .reviewTask(taskId, verdict)
      .then(() => {
        callbacks.current.onReviewed(verdict);
        callbacks.current.onClose();
      })
      .catch((error: unknown) => {
        setErrorText(describeError(error));
        setConfirming(null);
        // 409：任务刚被别人处理或第二复核人不符，刷新列表让按钮回到真实状态。
        if (extractHttpStatus(error) === 409) callbacks.current.onStale();
      })
      .finally(() => setSubmitting(false));
  };

  const captureState = states.get(CAPTURE_SLOT);
  const referenceKey = referenceSlot(referenceIndex);

  return (
    <Modal
      visible
      animationType="slide"
      presentationStyle="fullScreen"
      statusBarTranslucent
      onRequestClose={() => {
        if (zoomSlot) setZoomSlot(null);
        else if (!submitting) onClose();
      }}
    >
      <View style={[styles.root, { paddingTop: insets.top }]}>
        <View style={styles.header}>
          <IconButton icon="close" accessibilityLabel={t("review.close")} disabled={submitting} onPress={onClose} />
          <Text style={styles.headerTitle} numberOfLines={1}>
            {t("review.modalTitle")}
          </Text>
          <View style={styles.headerSpacer} />
        </View>

        <ScrollView contentContainerStyle={styles.body} keyboardShouldPersistTaps="handled">
          <Notice tone="warning" text={t("review.sensitiveNotice")} icon="shield-lock-outline" />
          <View style={styles.similarityRow}>
            <Text style={[styles.similarity, MONO]}>{t("review.similarity", { percent })}</Text>
            <Tag label={t(BAND_LABEL_KEYS[task.band])} tone={task.band === "high" ? "danger" : "warning"} />
          </View>
          <Text style={styles.muted}>{t("review.notProbability")}</Text>

          {imageAvailable ? (
            <View style={styles.pair}>
              <View style={styles.column}>
                <Text style={styles.columnTitle}>{t("review.captureTitle")}</Text>
                <ImageSlot
                  state={captureState}
                  isBroken={broken.has(CAPTURE_SLOT)}
                  alt={t("review.captureTitle")}
                  onBroken={() => markBroken(CAPTURE_SLOT)}
                  onPress={() => setZoomSlot(CAPTURE_SLOT)}
                />
                <Text style={styles.muted}>{storeLabel}</Text>
                <Text style={[styles.muted, MONO]}>{formatLocalDateTime(task.capturedAtUtc)}</Text>
              </View>
              <View style={styles.column}>
                <Text style={styles.columnTitle}>{t("review.referenceTitle")}</Text>
                {indexes.length === 0 ? (
                  <Notice tone="warning" text={t("review.noReference")} />
                ) : (
                  <>
                    <ImageSlot
                      // 切换参考照片时换 key：旧图卸载，不残留上一张的解码失败状态。
                      key={referenceKey}
                      state={states.get(referenceKey)}
                      isBroken={broken.has(referenceKey)}
                      alt={t("review.referenceTitle")}
                      onBroken={() => markBroken(referenceKey)}
                      onPress={() => setZoomSlot(referenceKey)}
                    />
                    {indexes.length > 1 ? (
                      <View style={styles.referenceNav}>
                        <IconButton
                          mode="outlined"
                          icon="chevron-left"
                          size={20}
                          accessibilityLabel={t("review.referencePrev")}
                          disabled={!canStepReference(referenceIndex, referenceCount, -1)}
                          onPress={() => selectReference(-1)}
                        />
                        <Text style={[styles.muted, MONO]}>{`${referenceIndex + 1} / ${indexes.length}`}</Text>
                        <IconButton
                          mode="outlined"
                          icon="chevron-right"
                          size={20}
                          accessibilityLabel={t("review.referenceNext")}
                          disabled={!canStepReference(referenceIndex, referenceCount, 1)}
                          onPress={() => selectReference(1)}
                        />
                      </View>
                    ) : null}
                  </>
                )}
              </View>
            </View>
          ) : (
            <Notice tone="warning" text={t("review.imageGone")} />
          )}

          <Notice
            tone={step === "done" ? "success" : "info"}
            text={t(REVIEW_STEP_HINT_KEYS[step])}
            icon={step === "done" ? "check-circle-outline" : "account-multiple-check-outline"}
          />
          {step === "second" && task.firstReviewerName ? (
            <Text style={styles.muted}>{t("review.firstReviewer", { name: task.firstReviewerName })}</Text>
          ) : null}
          {blockedKey && step !== "done" ? <Notice tone="warning" text={t(blockedKey)} /> : null}
          {errorText ? <Notice tone="danger" text={errorText} /> : null}
        </ScrollView>

        <View style={[styles.footer, { paddingBottom: Math.max(insets.bottom, HB_SPACING.sm) }]}>
          {confirming ? (
            <>
              <Text style={styles.confirmTitle}>
                {t(confirming === "match" ? "review.confirmMatch.title" : "review.confirmNotMatch.title")}
              </Text>
              <Text style={styles.muted}>
                {confirming === "match" ? t("review.confirmMatch.content", { percent }) : t("review.confirmNotMatch.content")}
              </Text>
              <View style={styles.footerRow}>
                <Button style={styles.footerButton} mode="outlined" disabled={submitting} onPress={() => setConfirming(null)}>
                  {t("review.confirmBack")}
                </Button>
                <Button style={styles.footerButton} mode="contained" loading={submitting} disabled={submitting} onPress={() => submit(confirming)}>
                  {t("review.confirmOk")}
                </Button>
              </View>
            </>
          ) : (
            <View style={styles.footerRow}>
              <Button style={styles.footerButton} mode="outlined" disabled={!verdictEnabled} onPress={() => setConfirming("notMatch")}>
                {t("review.verdict.notMatch")}
              </Button>
              <Button style={styles.footerButton} mode="contained" disabled={!verdictEnabled} onPress={() => setConfirming("match")}>
                {t("review.verdict.match")}
              </Button>
            </View>
          )}
        </View>

        {/* 放大查看：同一个弹框内的覆盖层，关闭即不再持有额外引用（图片仍只在 states 里）。 */}
        {zoomSlot ? (
          <View style={[styles.zoom, { paddingTop: insets.top, paddingBottom: insets.bottom }]}>
            <IconButton icon="close" iconColor={HB_COLORS.white} accessibilityLabel={t("review.close")} onPress={() => setZoomSlot(null)} style={styles.zoomClose} />
            <ImageSlot
              large
              state={states.get(zoomSlot)}
              isBroken={broken.has(zoomSlot)}
              alt={zoomSlot === CAPTURE_SLOT ? t("review.captureTitle") : t("review.referenceTitle")}
              onBroken={() => markBroken(zoomSlot)}
              onPress={() => setZoomSlot(null)}
            />
          </View>
        ) : null}
      </View>
    </Modal>
  );
}

/** 单张复核图片：Image 的 uri 永远是内存里的 data URI，不是后端地址。 */
function ImageSlot({
  state,
  isBroken,
  alt,
  large,
  onBroken,
  onPress,
}: {
  state: ReviewImageState | undefined;
  isBroken: boolean;
  alt: string;
  large?: boolean;
  onBroken: () => void;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("faceMatch");
  const boxStyle = large ? styles.imageLarge : styles.imageBox;
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
      <View style={[boxStyle, styles.center, styles.imageError]}>
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
  body: { padding: HB_SPACING.md, gap: HB_SPACING.xs },
  similarityRow: { flexDirection: "row", alignItems: "center", flexWrap: "wrap", gap: HB_SPACING.xs },
  similarity: { fontSize: 16, fontWeight: "700", color: HB_COLORS.textPrimary },
  muted: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  pair: { flexDirection: "row", gap: HB_SPACING.xs, marginTop: HB_SPACING.xs },
  column: { flex: 1, gap: 6 },
  columnTitle: { fontSize: 13, fontWeight: "700", color: HB_COLORS.textPrimary },
  imageBox: {
    width: "100%",
    aspectRatio: 3 / 4,
    borderRadius: HB_RADIUS.control,
    overflow: "hidden",
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  imageLarge: { flex: 1, width: "100%" },
  image: { width: "100%", height: "100%" },
  center: { alignItems: "center", justifyContent: "center", gap: 6, padding: HB_SPACING.xs },
  imageError: { borderWidth: 1, borderColor: HB_COLORS.outlineMuted },
  imageErrorText: { fontSize: 12, textAlign: "center", color: HB_COLORS.textSecondary },
  referenceNav: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  footer: {
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.sm,
    gap: HB_SPACING.xs,
    backgroundColor: HB_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  footerRow: { flexDirection: "row", gap: HB_SPACING.xs },
  footerButton: { flex: 1 },
  confirmTitle: { fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  zoom: { ...StyleSheet.absoluteFillObject, backgroundColor: "rgba(0,0,0,0.94)" },
  zoomClose: { alignSelf: "flex-end" },
});
