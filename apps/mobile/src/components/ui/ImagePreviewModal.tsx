// 全屏大图预览：双指缩放（1–4×）、双击放大 / 还原、点空白或下滑关闭。
// App 没有 gesture-handler / reanimated，这里用 RN 自带的 PanResponder + Animated（原生驱动），
// 纯 JS 实现，可以走 OTA；手势几何都在 modules/image-preview/zoom-logic.ts（有单测）。
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  AccessibilityInfo,
  Animated,
  Easing,
  Image,
  Modal,
  PanResponder,
  Pressable,
  StyleSheet,
  View,
  useWindowDimensions,
  type GestureResponderEvent,
} from "react-native";
import { Icon, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { consumePreviewHint } from "@/modules/image-preview/preview-hint";
import {
  DOUBLE_TAP_MS,
  SNAP_BACK_SCALE,
  TAP_MAX_MS,
  TAP_SLOP,
  clampTranslation,
  doubleTapTarget,
  dragProgress,
  fitContain,
  isDoubleTap,
  isPointInsideImage,
  pinchScale,
  pinchTransform,
  shouldDismiss,
  touchDistance,
  touchMidpoint,
  zoomPercentLabel,
  type Point,
  type Size,
  type TapRecord,
} from "@/modules/image-preview/zoom-logic";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

export interface ImagePreviewModalProps {
  uri: string;
  /** 底部标题（商品名）。 */
  title?: string;
  /** 底部副标题（货号 · 条码）。 */
  subtitle?: string | null;
  onClose: () => void;
}

interface PinchStart {
  distance: number;
  scale: number;
  translate: Point;
  mid: Point;
}

export function ImagePreviewModal({ uri, title, subtitle, onClose }: ImagePreviewModalProps) {
  const { t } = useAppTranslation("common");
  const insets = useSafeAreaInsets();
  const { width, height } = useWindowDimensions();
  const [natural, setNatural] = useState<Size | null>(null);
  const [failed, setFailed] = useState(false);
  const [zoomLabel, setZoomLabel] = useState<string | null>(null);
  const [hintVisible, setHintVisible] = useState(false);

  // 动画值：缩放 / 平移（手势与双击）、下滑关闭的位移与缩小、背景透明度、打开时的放大。
  const scaleV = useRef(new Animated.Value(1)).current;
  const txV = useRef(new Animated.Value(0)).current;
  const tyV = useRef(new Animated.Value(0)).current;
  const dragYV = useRef(new Animated.Value(0)).current;
  const dragScaleV = useRef(new Animated.Value(1)).current;
  const backdropV = useRef(new Animated.Value(0)).current;
  const enterV = useRef(new Animated.Value(0.92)).current;

  const container = useMemo<Size>(() => ({ width, height }), [width, height]);
  const displayed = useMemo(() => fitContain(natural, container), [natural, container]);

  // 手势回调只创建一次，通过 ref 读取最新的尺寸与回调，避免闭包拿到旧值。
  const latest = useRef({ container, displayed, onClose });
  latest.current = { container, displayed, onClose };
  // JS 侧镜像当前变换（Animated.Value 不能同步读取）。
  const transform = useRef({ scale: 1, x: 0, y: 0 });
  const gesture = useRef({
    startTime: 0,
    start: { x: 0, y: 0 } as Point,
    last: null as Point | null,
    moved: 0,
    multi: false,
    dragY: 0,
    pinch: null as PinchStart | null,
  });
  // 双击缩放动画是否还在进行；transform.current 始终记着动画的「目标状态」。
  const zoomAnimation = useRef({ id: 0, running: false });
  const lastTap = useRef<TapRecord | null>(null);
  const tapTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const closing = useRef(false);
  const reduceMotion = useRef(false);
  const zoomLabelRef = useRef<string | null>(null);

  const syncZoomLabel = useCallback(() => {
    const next = transform.current.scale > SNAP_BACK_SCALE ? zoomPercentLabel(transform.current.scale) : null;
    if (next !== zoomLabelRef.current) {
      zoomLabelRef.current = next;
      setZoomLabel(next);
    }
  }, []);

  const apply = useCallback(
    (scale: number, x: number, y: number) => {
      transform.current = { scale, x, y };
      scaleV.setValue(scale);
      txV.setValue(x);
      tyV.setValue(y);
      syncZoomLabel();
    },
    [scaleV, syncZoomLabel, txV, tyV],
  );

  const animateTo = useCallback(
    (scale: number, x: number, y: number) => {
      transform.current = { scale, x, y };
      syncZoomLabel();
      const config = { duration: reduceMotion.current ? 0 : 200, easing: Easing.out(Easing.cubic), useNativeDriver: true };
      const id = ++zoomAnimation.current.id;
      zoomAnimation.current.running = true;
      Animated.parallel([
        Animated.timing(scaleV, { toValue: scale, ...config }),
        Animated.timing(txV, { toValue: x, ...config }),
        Animated.timing(tyV, { toValue: y, ...config }),
      ]).start(() => {
        // 只有最新一次动画结束才清除标记，被打断的旧动画回调不能覆盖。
        if (zoomAnimation.current.id === id) zoomAnimation.current.running = false;
      });
    },
    [scaleV, syncZoomLabel, txV, tyV],
  );

  const close = useCallback(() => {
    if (closing.current) return;
    closing.current = true;
    if (tapTimer.current) clearTimeout(tapTimer.current);
    Animated.timing(backdropV, { toValue: 0, duration: reduceMotion.current ? 0 : 150, useNativeDriver: true }).start(() =>
      latest.current.onClose(),
    );
  }, [backdropV]);

  const resetDrag = useCallback(() => {
    gesture.current.dragY = 0;
    dragYV.setValue(0);
    dragScaleV.setValue(1);
    backdropV.setValue(1);
  }, [backdropV, dragScaleV, dragYV]);

  // 打开：重置状态、淡入并轻微放大；系统开启「减弱动态效果」时直接显示。
  useEffect(() => {
    let alive = true;
    closing.current = false;
    apply(1, 0, 0);
    resetDrag();
    void AccessibilityInfo.isReduceMotionEnabled()
      .then((enabled) => {
        reduceMotion.current = enabled;
        if (!alive) return;
        if (enabled) {
          backdropV.setValue(1);
          enterV.setValue(1);
        }
      })
      .catch(() => undefined);
    backdropV.setValue(0);
    enterV.setValue(0.92);
    Animated.parallel([
      Animated.timing(backdropV, { toValue: 1, duration: 220, useNativeDriver: true }),
      Animated.timing(enterV, { toValue: 1, duration: 220, easing: Easing.out(Easing.cubic), useNativeDriver: true }),
    ]).start();
    return () => {
      alive = false;
      if (tapTimer.current) clearTimeout(tapTimer.current);
    };
    // 每次挂载只做一次初始化；各动画值与 apply 都是稳定引用。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // 读取原图尺寸，用于计算放大后的平移边界；读取失败时沿用正方形兜底。
  useEffect(() => {
    let alive = true;
    Image.getSize(
      uri,
      (w, h) => {
        if (alive) setNatural({ width: w, height: h });
      },
      () => undefined,
    );
    return () => {
      alive = false;
    };
  }, [uri]);

  // 手势提示条只在前 3 次打开时出现，2.5 秒后消失。
  useEffect(() => {
    let alive = true;
    let timer: ReturnType<typeof setTimeout> | null = null;
    void consumePreviewHint().then((show) => {
      if (!alive || !show) return;
      setHintVisible(true);
      timer = setTimeout(() => setHintVisible(false), 2500);
    });
    return () => {
      alive = false;
      if (timer) clearTimeout(timer);
    };
  }, []);

  const toRelative = useCallback(
    (touch: { pageX: number; pageY: number }): Point => ({
      x: touch.pageX - latest.current.container.width / 2,
      y: touch.pageY - latest.current.container.height / 2,
    }),
    [],
  );

  const handleTap = useCallback(
    (point: Point) => {
      const now: TapRecord = { time: Date.now(), x: point.x, y: point.y };
      const { container: box, displayed: shown } = latest.current;
      if (isDoubleTap(lastTap.current, now)) {
        if (tapTimer.current) clearTimeout(tapTimer.current);
        lastTap.current = null;
        const target = doubleTapTarget(transform.current.scale, point, shown, box);
        animateTo(target.scale, target.translate.x, target.translate.y);
        return;
      }
      lastTap.current = now;
      if (tapTimer.current) clearTimeout(tapTimer.current);
      // 等一个双击间隔：期间没有第二下才算单击；单击图片外的空白关闭预览。
      tapTimer.current = setTimeout(() => {
        lastTap.current = null;
        const { scale, x, y } = transform.current;
        if (!isPointInsideImage(point, scale, { x, y }, latest.current.displayed)) close();
      }, DOUBLE_TAP_MS);
    },
    [animateTo, close],
  );

  const finishGesture = useCallback(
    (velocityY: number, terminated: boolean) => {
      const g = gesture.current;
      const wasMulti = g.multi;
      g.pinch = null;
      g.last = null;
      if (g.dragY > 0 && transform.current.scale <= SNAP_BACK_SCALE) {
        if (!terminated && shouldDismiss(g.dragY, velocityY, transform.current.scale)) {
          // 下滑关闭：图片继续滑出屏幕、背景淡出，然后通知外层关闭。
          closing.current = true;
          Animated.parallel([
            Animated.timing(dragYV, { toValue: latest.current.container.height, duration: reduceMotion.current ? 0 : 160, useNativeDriver: true }),
            Animated.timing(backdropV, { toValue: 0, duration: reduceMotion.current ? 0 : 160, useNativeDriver: true }),
          ]).start(() => latest.current.onClose());
        } else {
          // 没到关闭阈值：回弹。
          g.dragY = 0;
          Animated.parallel([
            Animated.spring(dragYV, { toValue: 0, useNativeDriver: true, bounciness: 4 }),
            Animated.spring(dragScaleV, { toValue: 1, useNativeDriver: true, bounciness: 4 }),
            Animated.timing(backdropV, { toValue: 1, duration: 120, useNativeDriver: true }),
          ]).start();
        }
        return;
      }
      if (wasMulti) {
        // 缩放结束：几乎没放大就吸附回 1×，放大了则保持并保证不露黑边。
        if (transform.current.scale <= SNAP_BACK_SCALE) animateTo(1, 0, 0);
        else {
          const { scale, x, y } = transform.current;
          const clamped = clampTranslation({ x, y }, scale, latest.current.displayed, latest.current.container);
          animateTo(scale, clamped.x, clamped.y);
        }
        return;
      }
      if (!terminated && g.moved < TAP_SLOP && Date.now() - g.startTime < TAP_MAX_MS) handleTap(g.start);
    },
    [animateTo, backdropV, dragScaleV, dragYV, handleTap],
  );

  const responder = useRef(
    PanResponder.create({
      onStartShouldSetPanResponder: () => true,
      onMoveShouldSetPanResponder: () => true,
      // 预览期间手势不让给外层（如 ScrollView），避免缩放被打断。
      onPanResponderTerminationRequest: () => false,
      onPanResponderGrant: (event: GestureResponderEvent) => {
        const g = gesture.current;
        const touches = event.nativeEvent.touches;
        g.startTime = Date.now();
        g.start = touches.length ? toRelative(touches[0]) : { x: 0, y: 0 };
        g.last = null;
        g.moved = 0;
        g.multi = touches.length > 1;
        g.pinch = null;
        // 手指落下时若缩放动画还在进行，直接跳到它的目标状态（setValue 会停掉动画）。
        // 不再用 stopAnimation 的回调读回当前值：原生驱动下回调是异步的，快速连点时
        // 它可能晚于紧接着的 animateTo，把镜像写回旧值，导致第二次双击判断错误。
        if (zoomAnimation.current.running) {
          zoomAnimation.current.running = false;
          zoomAnimation.current.id++;
          const { scale, x, y } = transform.current;
          apply(scale, x, y);
        }
      },
      onPanResponderMove: (event: GestureResponderEvent) => {
        const g = gesture.current;
        const touches = event.nativeEvent.touches;
        const { container: box, displayed: shown } = latest.current;
        if (touches.length >= 2) {
          g.multi = true;
          const a = toRelative(touches[0]);
          const b = toRelative(touches[1]);
          if (!g.pinch) {
            // 两指落下的瞬间如果正在下滑，先放弃下滑状态再开始缩放。
            if (g.dragY !== 0) resetDrag();
            g.pinch = {
              distance: touchDistance(a, b),
              scale: transform.current.scale,
              translate: { x: transform.current.x, y: transform.current.y },
              mid: touchMidpoint(a, b),
            };
          }
          const nextScale = pinchScale(g.pinch.scale, g.pinch.distance, touchDistance(a, b));
          const moved = clampTranslation(
            pinchTransform(g.pinch.scale, g.pinch.translate, g.pinch.mid, touchMidpoint(a, b), nextScale),
            nextScale,
            shown,
            box,
          );
          apply(nextScale, moved.x, moved.y);
          g.last = null;
          return;
        }
        g.pinch = null;
        if (!touches.length) return;
        const current = toRelative(touches[0]);
        g.moved = Math.max(g.moved, Math.hypot(current.x - g.start.x, current.y - g.start.y));
        // 手指数量刚变化（例如两指抬起一根）时不产生位移跳变，先记录位置。
        if (!g.last) {
          g.last = current;
          return;
        }
        const dx = current.x - g.last.x;
        const dy = current.y - g.last.y;
        g.last = current;
        const { scale, x, y } = transform.current;
        if (scale > SNAP_BACK_SCALE) {
          // 放大状态：单指拖动平移，下滑也只是平移，不会触发关闭。
          const next = clampTranslation({ x: x + dx, y: y + dy }, scale, shown, box);
          apply(scale, next.x, next.y);
        } else {
          // 1×：向下拖动跟手缩小、背景变透明；向上拖动不响应。
          g.dragY = Math.max(0, g.dragY + dy);
          const progress = dragProgress(g.dragY, box.height);
          dragYV.setValue(g.dragY);
          dragScaleV.setValue(1 - progress * 0.15);
          backdropV.setValue(1 - progress * 0.8);
        }
      },
      onPanResponderRelease: (_event, state) => finishGesture(state.vy, false),
      onPanResponderTerminate: (_event, state) => finishGesture(state.vy, true),
    }),
  ).current;

  const zoomed = zoomLabel !== null;
  const stageScale = useMemo(
    () => Animated.multiply(Animated.multiply(scaleV, enterV), dragScaleV),
    [dragScaleV, enterV, scaleV],
  );
  const stageTranslateY = useMemo(() => Animated.add(tyV, dragYV), [dragYV, tyV]);

  return (
    <Modal visible transparent animationType="none" statusBarTranslucent onRequestClose={close}>
      <View style={styles.root}>
        <Animated.View style={[StyleSheet.absoluteFill, styles.backdrop, { opacity: backdropV }]} />
        <View style={StyleSheet.absoluteFill} {...responder.panHandlers} accessibilityViewIsModal>
          <Animated.View
            style={[
              styles.stage,
              { transform: [{ translateX: txV }, { translateY: stageTranslateY }, { scale: stageScale }] },
            ]}
          >
            {failed ? (
              <View style={styles.failed}>
                <Icon source="image-broken-variant" size={48} color="#98A2B3" />
                <Text style={styles.failedText}>{t("imagePreview.loadFailed")}</Text>
              </View>
            ) : (
              <Image
                source={{ uri }}
                style={styles.image}
                resizeMode="contain"
                accessibilityLabel={t("imagePreview.imageLabel", { name: title ?? "" })}
                onError={() => setFailed(true)}
              />
            )}
          </Animated.View>
        </View>

        <View style={[styles.topBar, { paddingTop: insets.top + 8 }]} pointerEvents="box-none">
          {zoomLabel ? (
            <View style={styles.chip}>
              <Text style={styles.chipText}>{zoomLabel}</Text>
            </View>
          ) : (
            <View />
          )}
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("imagePreview.close")}
            onPress={close}
            hitSlop={8}
            style={styles.closeButton}
          >
            <Icon source="close" size={24} color="#FFFFFF" />
          </Pressable>
        </View>

        <View style={[styles.bottom, { paddingBottom: insets.bottom + 16 }]} pointerEvents="none">
          {zoomed || hintVisible ? (
            <View style={styles.hint}>
              <Text style={styles.hintText}>{t(zoomed ? "imagePreview.zoomedHint" : "imagePreview.hint")}</Text>
            </View>
          ) : null}
          {!zoomed && (title || subtitle) ? (
            <View style={styles.caption}>
              {title ? (
                <Text style={styles.captionTitle} numberOfLines={2}>
                  {title}
                </Text>
              ) : null}
              {subtitle ? (
                <Text style={styles.captionSubtitle} numberOfLines={1}>
                  {subtitle}
                </Text>
              ) : null}
            </View>
          ) : null}
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  backdrop: { backgroundColor: "#050608" },
  stage: { flex: 1, alignItems: "center", justifyContent: "center" },
  image: { width: "100%", height: "100%" },
  failed: { alignItems: "center", justifyContent: "center", gap: 8 },
  failedText: { color: "#D0D5DD", fontSize: 14 },
  topBar: {
    position: "absolute",
    top: 0,
    left: 0,
    right: 0,
    paddingHorizontal: 12,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  chip: {
    height: 28,
    paddingHorizontal: 10,
    borderRadius: 14,
    justifyContent: "center",
    backgroundColor: "rgba(16,24,40,0.6)",
  },
  chipText: { color: "#FFFFFF", fontSize: 13, fontWeight: "700", fontVariant: ["tabular-nums"] },
  closeButton: {
    width: 44,
    height: 44,
    borderRadius: 22,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "rgba(16,24,40,0.6)",
  },
  bottom: { position: "absolute", left: 0, right: 0, bottom: 0, alignItems: "center", gap: 12 },
  hint: {
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 18,
    backgroundColor: "rgba(16,24,40,0.6)",
  },
  hintText: { color: "#FFFFFF", fontSize: 12.5 },
  caption: { alignSelf: "stretch", paddingHorizontal: 20, gap: 4 },
  captionTitle: { color: "#FFFFFF", fontSize: 16, fontWeight: "700" },
  captionSubtitle: { color: "rgba(255,255,255,0.7)", fontSize: 13 },
});
