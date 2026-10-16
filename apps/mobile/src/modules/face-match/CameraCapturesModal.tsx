// 某台摄像头的「最近抓拍」弹框：列表只有推送报文的元数据（时间 / 事件类型 / 人脸数 / 解析状态），不含图片。
//
// 约定（由 source-contract 测试守住）：
// 1. 列表不带任何图片；持有 FaceCapture.Images.View 的人点「查看图片」后才按需读取，一次一张，读取前服务端写审计；
// 2. 图片只以 base64 data URI 存在于查看器组件 state，不写磁盘缓存、不进 react-query、不进日志；
//    查看器关闭 / 卸载 / 应用离开前台时立即丢弃并中止在途请求；
// 3. iOS 系统网络层会把响应写进缓存目录（即使 no-store），所以读取后用与复核弹框同一个清理器删除图片缓存文件（缓解，非根治，局限见 http-cache-scrub.ts）；
// 4. 谁能看图由服务端决定：页面只按服务端 canViewImages 显示入口，并如实展示服务端的拒绝。
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { ActivityIndicator, AppState, FlatList, Image, Modal, Pressable, StyleSheet, View } from "react-native";
import { Button, IconButton, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { faceMatchApi } from "./api";
import { cameraDisplayName, cameraImageLabel, cameraParseStatusLabelKey, cameraParseStatusTone, hasViewableImages, isCameraImageViewable } from "./cameras-logic";
import { formatLocalDateTime } from "./dates";
import { classifyReviewImageError } from "./errors";
import { createReviewImageScrubber } from "./http-cache-scrub-runtime";
import { useFaceErrorMessage } from "./shared";
import type { FaceCamera, FaceCameraCapture } from "./types";
import { useFaceCameraCaptures } from "./use-face-match";
import { FaceCard, MONO, Notice, Tag, faceStyles } from "./ui";

interface Props {
  camera: FaceCamera;
  storeLabel: string;
  onClose: () => void;
}

export function CameraCapturesModal({ camera, storeLabel, onClose }: Props) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const insets = useSafeAreaInsets();
  const query = useFaceCameraCaptures(camera.cameraId, true);
  const [viewing, setViewing] = useState<FaceCameraCapture | null>(null);

  const pages = query.data?.pages;
  const firstPage = pages?.[0] ?? null;
  const items = useMemo(() => {
    const seen = new Set<string>();
    const merged: FaceCameraCapture[] = [];
    for (const page of pages ?? []) {
      for (const capture of page.items) {
        if (seen.has(capture.captureId)) continue;
        seen.add(capture.captureId);
        merged.push(capture);
      }
    }
    return merged;
  }, [pages]);
  const canViewImages = firstPage?.canViewImages ?? false;

  const header = (
    <View style={styles.listHeader}>
      <Text style={faceStyles.muted}>{storeLabel}</Text>
      {firstPage ? (
        <>
          {firstPage.storeMode === "off" ? <Notice tone="warning" text={t("settings.cameras.captures.storeOff", { store: storeLabel })} /> : null}
          <Notice
            tone="info"
            icon={canViewImages ? "shield-lock-outline" : "information-outline"}
            text={canViewImages ? t("settings.cameras.captures.imagesOnDemand") : t("settings.cameras.captures.metadataOnly")}
          />
          <Text style={[faceStyles.muted, MONO]}>
            {firstPage.lastSeenAtUtc
              ? t("settings.cameras.captures.lastSeen", { time: formatLocalDateTime(firstPage.lastSeenAtUtc) })
              : t("settings.cameras.captures.neverSeen")}
          </Text>
        </>
      ) : null}
    </View>
  );

  let empty = null;
  if (query.isPending) {
    empty = (
      <View style={faceStyles.state}>
        <ActivityIndicator color={HB_COLORS.brand} />
        <Text style={faceStyles.muted}>{t("states.loading")}</Text>
      </View>
    );
  } else if (query.isError && items.length === 0) {
    empty = (
      <View style={faceStyles.state}>
        <EmptyState
          title={t("settings.cameras.captures.loadFailed")}
          description={describeError(query.error)}
          actionLabel={t("common:actions.retry")}
          onAction={() => void query.refetch()}
        />
      </View>
    );
  } else {
    empty = (
      <View style={faceStyles.state}>
        <EmptyState title={t("settings.cameras.captures.empty")} />
      </View>
    );
  }

  let footer = null;
  if (query.hasNextPage) {
    footer = (
      <Button mode="outlined" loading={query.isFetchingNextPage} disabled={query.isFetchingNextPage} onPress={() => void query.fetchNextPage()} style={styles.more}>
        {query.isFetchingNextPage ? t("settings.cameras.captures.loadingMore") : t("settings.cameras.captures.loadMore")}
      </Button>
    );
  } else if (query.isFetchNextPageError) {
    footer = (
      <Button mode="outlined" onPress={() => void query.fetchNextPage()} style={styles.more}>
        {t("states.loadMoreFailed")}
      </Button>
    );
  } else if (items.length > 0) {
    footer = <Text style={[faceStyles.muted, styles.end]}>{t("settings.cameras.captures.end")}</Text>;
  }

  return (
    <Modal
      visible
      animationType="slide"
      presentationStyle="fullScreen"
      statusBarTranslucent
      onRequestClose={() => (viewing ? setViewing(null) : onClose())}
    >
      <View style={[styles.root, { paddingTop: insets.top }]}>
        <View style={styles.headerBar}>
          <IconButton icon="close" accessibilityLabel={t("common:actions.close")} onPress={onClose} />
          <Text style={styles.headerTitle} numberOfLines={1}>
            {t("settings.cameras.captures.title", { name: cameraDisplayName(camera) })}
          </Text>
          <IconButton icon="refresh" accessibilityLabel={t("common:actions.retry")} disabled={query.isFetching} onPress={() => void query.refetch()} />
        </View>

        <FlatList
          data={items}
          keyExtractor={(item) => item.captureId}
          renderItem={({ item }) => <CaptureRow capture={item} canViewImages={canViewImages} onViewImages={() => setViewing(item)} />}
          ListHeaderComponent={header}
          ListEmptyComponent={empty}
          ListFooterComponent={footer}
          contentContainerStyle={[styles.content, { paddingBottom: insets.bottom + HB_SPACING.lg }]}
        />

        {/* 查看器是同一个 Modal 里的覆盖层（不嵌套原生 Modal）；以抓拍编号作 key，换一条就重新挂载，旧图片随之丢弃。 */}
        {viewing ? <CaptureImageViewer key={viewing.captureId} cameraId={camera.cameraId} capture={viewing} onClose={() => setViewing(null)} /> : null}
      </View>
    </Modal>
  );
}

function CaptureRow({ capture, canViewImages, onViewImages }: { capture: FaceCameraCapture; canViewImages: boolean; onViewImages: () => void }) {
  const { t } = useAppTranslation("faceMatch");
  return (
    <FaceCard style={styles.row}>
      <View style={faceStyles.rowBetween}>
        <Text style={[faceStyles.strong, MONO]}>{formatLocalDateTime(capture.receivedAtUtc)}</Text>
        <Text style={faceStyles.muted}>{t("settings.cameras.captures.faceCount", { count: capture.faceCount })}</Text>
      </View>
      <View style={faceStyles.rowWrap}>
        <Tag label={t(cameraParseStatusLabelKey(capture.parseStatus))} tone={cameraParseStatusTone(capture.parseStatus)} />
        {capture.isDuplicate ? <Tag label={t("settings.cameras.captures.duplicate")} /> : null}
      </View>
      {capture.eventType ? <Text style={faceStyles.muted}>{t("settings.cameras.captures.eventType", { type: capture.eventType })}</Text> : null}
      {capture.cameraEventTime ? (
        <Text style={[faceStyles.muted, MONO]}>{t("settings.cameras.captures.eventTime", { time: formatLocalDateTime(capture.cameraEventTime) })}</Text>
      ) : null}
      {capture.parseNote ? <Text style={faceStyles.muted}>{t("settings.cameras.captures.parseNote", { note: capture.parseNote })}</Text> : null}
      {canViewImages && hasViewableImages(capture) ? (
        <Button mode="outlined" icon="image-outline" onPress={onViewImages} style={styles.viewButton}>
          {t("settings.cameras.captures.viewImages")}
        </Button>
      ) : null}
    </FaceCard>
  );
}

type ImageState = { status: "loading" } | { status: "loaded"; uri: string } | { status: "error"; gone: boolean; message: string };

/** 图片查看器：打开后读第一张，其余在点选时才读；关闭 / 卸载 / 离开前台立即中止请求并丢弃全部图片。 */
function CaptureImageViewer({ cameraId, capture, onClose }: { cameraId: string; capture: FaceCameraCapture; onClose: () => void }) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const describeError = useFaceErrorMessage();
  const images = useMemo(() => capture.images.filter(isCameraImageViewable), [capture.images]);
  const [selectedId, setSelectedId] = useState<string | null>(images[0]?.imageId ?? null);
  const [states, setStates] = useState<Record<string, ImageState>>({});
  const requestedRef = useRef(new Set<string>());
  const controllersRef = useRef(new Map<string, AbortController>());
  const scrubberRef = useRef<ReturnType<typeof createReviewImageScrubber> | null>(null);
  const aliveRef = useRef(true);
  const closeRef = useRef(onClose);
  closeRef.current = onClose;

  useEffect(() => {
    // Map / Set 在组件生命周期内不会被换掉，先取出来，清理时用这两个局部变量。
    const controllers = controllersRef.current;
    const requested = requestedRef.current;
    aliveRef.current = true;
    scrubberRef.current = createReviewImageScrubber();
    const subscription = AppState.addEventListener("change", (next) => {
      if (next !== "active") closeRef.current();
    });
    return () => {
      aliveRef.current = false;
      subscription.remove();
      for (const controller of controllers.values()) controller.abort();
      controllers.clear();
      requested.clear();
      void scrubberRef.current?.release();
      scrubberRef.current = null;
    };
  }, []);

  const load = useCallback(
    (imageId: string) => {
      if (requestedRef.current.has(imageId)) return;
      requestedRef.current.add(imageId);
      setStates((current) => ({ ...current, [imageId]: { status: "loading" } }));
      const controller = new AbortController();
      controllersRef.current.set(imageId, controller);
      const scrubber = scrubberRef.current;
      const fetcher = () => faceMatchApi.fetchCameraCaptureImage(cameraId, capture.captureId, imageId, controller.signal);
      void (scrubber ? scrubber.wrap(fetcher) : fetcher())
        .then((uri) => {
          if (aliveRef.current && !controller.signal.aborted) setStates((current) => ({ ...current, [imageId]: { status: "loaded", uri } }));
        })
        .catch((error: unknown) => {
          if (!aliveRef.current || controller.signal.aborted) return;
          const gone = classifyReviewImageError(error) === "gone";
          setStates((current) => ({ ...current, [imageId]: { status: "error", gone, message: gone ? "" : describeError(error) } }));
        })
        .finally(() => controllersRef.current.delete(imageId));
    },
    [cameraId, capture.captureId, describeError],
  );

  useEffect(() => {
    if (selectedId) load(selectedId);
  }, [load, selectedId]);

  const selected = selectedId ? states[selectedId] : undefined;

  return (
    <View style={styles.viewer} accessibilityViewIsModal>
      <View style={styles.viewerBar}>
        <IconButton icon="close" iconColor={HB_COLORS.white} accessibilityLabel={t("settings.cameras.captures.close")} onPress={onClose} />
        <Text style={styles.viewerTitle} numberOfLines={1}>
          {t("settings.cameras.captures.viewerTitle")}
        </Text>
        <View style={styles.viewerSpacer} />
      </View>
      <View style={styles.viewerTabs}>
        {images.map((image, ordinal) => {
          const label = cameraImageLabel(image, ordinal);
          const active = image.imageId === selectedId;
          return (
            <Pressable
              key={image.imageId}
              accessibilityRole="button"
              accessibilityState={{ selected: active }}
              onPress={() => setSelectedId(image.imageId)}
              style={[styles.viewerTab, active ? styles.viewerTabOn : null]}
            >
              <Text style={[styles.viewerTabText, active ? styles.viewerTabTextOn : null]}>{t(label.key, label.values)}</Text>
            </Pressable>
          );
        })}
      </View>
      <View style={styles.viewerBody}>
        {!selected || selected.status === "loading" ? (
          <>
            <ActivityIndicator color={HB_COLORS.white} />
            <Text style={styles.viewerHint}>{t("settings.cameras.captures.imageLoading")}</Text>
          </>
        ) : selected.status === "loaded" ? (
          <Image source={{ uri: selected.uri }} resizeMode="contain" style={styles.viewerImage} accessibilityLabel={t("settings.cameras.captures.viewerTitle")} />
        ) : (
          <Text style={styles.viewerHint}>{selected.gone ? t("settings.cameras.captures.imageGone") : selected.message || t("settings.cameras.captures.imageFailed")}</Text>
        )}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: HB_COLORS.background },
  headerBar: { flexDirection: "row", alignItems: "center", paddingHorizontal: HB_SPACING.xxs },
  headerTitle: { flex: 1, fontSize: 17, fontWeight: "700", color: HB_COLORS.textPrimary },
  content: { gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md },
  listHeader: { gap: HB_SPACING.xs, paddingBottom: HB_SPACING.xs },
  row: { gap: 6, marginHorizontal: 0 },
  viewButton: { alignSelf: "flex-start" },
  more: { marginTop: HB_SPACING.xs },
  end: { textAlign: "center", paddingVertical: HB_SPACING.sm },
  viewer: { ...StyleSheet.absoluteFillObject, backgroundColor: "rgba(0,0,0,0.92)" },
  viewerBar: { flexDirection: "row", alignItems: "center", paddingTop: HB_SPACING.xl, paddingHorizontal: HB_SPACING.xxs },
  viewerTitle: { flex: 1, fontSize: 16, fontWeight: "700", color: HB_COLORS.white },
  viewerSpacer: { width: 48 },
  viewerTabs: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs, paddingHorizontal: HB_SPACING.md, paddingBottom: HB_SPACING.xs },
  viewerTab: { paddingHorizontal: 12, paddingVertical: 6, borderRadius: HB_RADIUS.control, borderWidth: 1, borderColor: "rgba(255,255,255,0.5)" },
  viewerTabOn: { backgroundColor: HB_COLORS.white, borderColor: HB_COLORS.white },
  viewerTabText: { fontSize: 13, color: HB_COLORS.white },
  viewerTabTextOn: { color: HB_COLORS.textPrimary, fontWeight: "700" },
  viewerBody: { flex: 1, alignItems: "center", justifyContent: "center", gap: HB_SPACING.sm, padding: HB_SPACING.md },
  viewerImage: { width: "100%", height: "100%" },
  viewerHint: { fontSize: 14, color: HB_COLORS.white, textAlign: "center" },
});
