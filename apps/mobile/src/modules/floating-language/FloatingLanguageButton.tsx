import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Animated,
  Easing,
  Keyboard,
  PanResponder,
  Pressable,
  StyleSheet,
  Text,
  View,
  type LayoutChangeEvent,
} from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useRouter } from "expo-router";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import Svg, { Circle } from "react-native-svg";
import { useAppNavigationAccess } from "@/modules/navigation/access-context";
import { shouldHidePrimaryTabBar } from "@/modules/navigation/primary-navigation";
import { buildPriceUpdatesRoute } from "@/modules/price-updates/navigation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { AppLanguage } from "@/shared/i18n/types";
import { HB_COLORS } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { useDeviceStore } from "@/store/device-store";
import {
  clampDragPoint,
  FLOATING_BUTTON_SIZE,
  FLOATING_CLOSE_ZONE_RADIUS,
  FLOATING_EDGE_TUCK,
  formatLongPressSeconds,
  hasMovedBeyondThreshold,
  isInCloseZone,
  isTapGesture,
  resolveCloseZoneCenter,
  resolveDockedPoint,
  resolveDropPosition,
  type FloatingBounds,
} from "./geometry";
import { changeLanguageFromFloatingButton, resolveNextLanguage } from "./language";
import { hydrateFloatingLanguagePreference, useFloatingLanguageStore } from "./preference";
import { buildQuickJumpModel, pushRecentRoute, resolveQuickJumpAction } from "./quick-jump";
import { loadRecentRoutesForScope, useQuickJumpRecentStore } from "./recent-routes";
import { QuickJumpPanel } from "./QuickJumpPanel";

const AnimatedCircle = Animated.createAnimatedComponent(Circle);
const RING_SIZE = FLOATING_BUTTON_SIZE + 14;
const RING_RADIUS = RING_SIZE / 2 - 3;
const RING_CIRCUMFERENCE = 2 * Math.PI * RING_RADIUS;
const HIDDEN_TOAST_MS = 4000;

interface FloatingLanguageButtonProps {
  pathname: string;
  currentRouteName?: string;
}

/**
 * 语言悬浮按钮 + 快速跳转。挂在壳布局装 Stack 的容器里（底部导航栏上方），登录 / 改密页天然没有。
 * 手势全部写在一个 PanResponder 里（不引入 gesture-handler / reanimated 这类原生依赖，可走 OTA）：
 * 点一下切中 / 英；拖动后松手吸到左 / 右缘；拖到底部红色区隐藏；按住 2 秒打开快速跳转。
 */
export function FloatingLanguageButton({ pathname, currentRouteName }: FloatingLanguageButtonProps) {
  const router = useRouter();
  const insets = useSafeAreaInsets();
  const { t, language } = useAppTranslation("common");
  const enabled = useFloatingLanguageStore((state) => state.enabled);
  const position = useFloatingLanguageStore((state) => state.position);
  const hydrated = useFloatingLanguageStore((state) => state.hydrated);
  const setEnabled = useFloatingLanguageStore((state) => state.setEnabled);
  const setPosition = useFloatingLanguageStore((state) => state.setPosition);
  const longPressMs = useFloatingLanguageStore((state) => state.longPressMs);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const iosReviewOfflineGuardActive = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const hardwareId = useDeviceStore((state) => state.session?.hardwareId);
  const {
    orderedVisibleRouteNames,
    navigationLoading,
    navigationErrorMessage,
    priceTasks,
    isDeviceMode,
  } = useAppNavigationAccess();

  const [bounds, setBounds] = useState<FloatingBounds | null>(null);
  const [keyboardVisible, setKeyboardVisible] = useState(false);
  const [panelOpen, setPanelOpen] = useState(false);
  const [holding, setHolding] = useState(false);
  const [dragging, setDragging] = useState(false);
  const [overCloseZone, setOverCloseZone] = useState(false);
  const [hiddenToastVisible, setHiddenToastVisible] = useState(false);

  const pan = useRef(new Animated.ValueXY({ x: 0, y: 0 })).current;
  const holdProgress = useRef(new Animated.Value(0)).current;
  const lastPointRef = useRef({ x: 0, y: 0 });

  useEffect(() => {
    void hydrateFloatingLanguagePreference();
  }, []);

  // 键盘弹出时收起按钮：不挡输入框，也不干扰 PDA 的扫码输入。
  useEffect(() => {
    const show = Keyboard.addListener("keyboardDidShow", () => setKeyboardVisible(true));
    const hide = Keyboard.addListener("keyboardDidHide", () => setKeyboardVisible(false));
    return () => {
      show.remove();
      hide.remove();
    };
  }, []);

  // ───────── 快速跳转数据：完全复用工作台那份权限过滤后的入口 ─────────
  const menuUsable = !navigationLoading && !navigationErrorMessage;
  const recentScope = isDeviceMode
    ? (hardwareId ? `device:${hardwareId}` : null)
    : (userGuid ? `account:${userGuid}` : null);
  const recentStoreScope = useQuickJumpRecentStore((state) => state.scope);
  const recentRoutes = useQuickJumpRecentStore((state) => state.routes);
  const replaceRecent = useQuickJumpRecentStore((state) => state.replace);
  const model = useMemo(
    () =>
      buildQuickJumpModel({
        orderedVisibleRouteNames,
        recentRouteNames: recentStoreScope === recentScope ? recentRoutes : [],
        currentRouteName,
        menuUsable,
      }),
    [currentRouteName, menuUsable, orderedVisibleRouteNames, recentRoutes, recentScope, recentStoreScope]
  );
  const trackableRouteNames = useMemo(
    () => new Set(model.sections.flatMap((section) => section.items.map((item) => item.routeName))),
    [model.sections]
  );

  useEffect(() => {
    if (enabled && recentScope) {
      void loadRecentRoutesForScope(recentScope);
    }
  }, [enabled, recentScope]);

  // 只在开启时记录最近访问：没开这个功能的人不产生任何额外写盘。
  useEffect(() => {
    if (!enabled || !recentScope || !currentRouteName || !trackableRouteNames.has(currentRouteName)) {
      return;
    }
    const state = useQuickJumpRecentStore.getState();
    if (state.scope !== recentScope || state.routes[0] === currentRouteName) {
      return;
    }
    replaceRecent(recentScope, pushRecentRoute(state.routes, currentRouteName, trackableRouteNames));
  }, [currentRouteName, enabled, recentScope, replaceRecent, trackableRouteNames]);

  // ───────── 何时显示 ─────────
  const reviewSession = sessionKind === "iosReview" || iosReviewOfflineGuardActive;
  const routeHidden = shouldHidePrimaryTabBar(pathname);
  const allowed = hydrated && enabled && !reviewSession && !routeHidden;
  const buttonVisible = allowed && Boolean(bounds) && !keyboardVisible && !panelOpen;

  // 不再允许显示时（隐藏 / 进审核态 / 进全屏作业页）收起面板与提示条。
  useEffect(() => {
    if (!allowed) {
      setPanelOpen(false);
      setHolding(false);
      setDragging(false);
    }
  }, [allowed]);

  // 面板开着时点了底部导航栏等别处跳走：换页即收起面板。
  useEffect(() => {
    setPanelOpen(false);
  }, [pathname]);

  // 容器尺寸或保存的位置变化（含字号放大、旋转）时，按比例重新吸边。
  useEffect(() => {
    if (!bounds || dragging) return;
    const docked = resolveDockedPoint(position, bounds);
    lastPointRef.current = docked;
    pan.setValue(docked);
  }, [bounds, dragging, pan, position]);

  const onLayout = useCallback(
    (event: LayoutChangeEvent) => {
      const { width, height } = event.nativeEvent.layout;
      setBounds((prev) =>
        prev && prev.width === width && prev.height === height && prev.topInset === insets.top
          ? prev
          : { width, height, topInset: insets.top }
      );
    },
    [insets.top]
  );

  // ───────── 动作 ─────────
  const toggleLanguage = useCallback(() => {
    void changeLanguageFromFloatingButton(resolveNextLanguage(language)).catch(() => undefined);
  }, [language]);

  const changeLanguage = useCallback((next: AppLanguage) => {
    void changeLanguageFromFloatingButton(next).catch(() => undefined);
  }, []);

  const hideButton = useCallback(() => {
    setPanelOpen(false);
    setEnabled(false);
    setHiddenToastVisible(true);
  }, [setEnabled]);

  useEffect(() => {
    if (!hiddenToastVisible) return;
    const timer = setTimeout(() => setHiddenToastVisible(false), HIDDEN_TOAST_MS);
    return () => clearTimeout(timer);
  }, [hiddenToastVisible]);

  const undoHide = useCallback(() => {
    setHiddenToastVisible(false);
    setEnabled(true);
  }, [setEnabled]);

  const closePanel = useCallback(() => setPanelOpen(false), []);

  const navigateTo = useCallback(
    (routeName: string) => {
      const action = resolveQuickJumpAction({
        targetRouteName: routeName,
        currentRouteName,
        reportsLocked: priceTasks.reportsLocked,
      });
      setPanelOpen(false);
      switch (action.kind) {
        case "dismiss-to":
          router.dismissTo(action.path as Parameters<typeof router.dismissTo>[0]);
          break;
        case "push":
          router.push(action.path as Parameters<typeof router.push>[0]);
          break;
        case "price-updates-overdue":
          router.push(
            buildPriceUpdatesRoute({
              storeCode: priceTasks.firstOverdueStoreCode,
              overdueOnly: true,
            }) as Parameters<typeof router.push>[0]
          );
          break;
        default:
          break;
      }
    },
    [currentRouteName, priceTasks.firstOverdueStoreCode, priceTasks.reportsLocked, router]
  );

  // ───────── 手势：一个 PanResponder 同时管点击、拖动、长按 ─────────
  // PanResponder 只创建一次，回调里通过 ref 读最新的尺寸 / 位置 / 动作，避免闭包拿到旧值。
  const latest = useRef({ bounds, position, toggleLanguage, hideButton, setPosition, longPressMs });
  latest.current = { bounds, position, toggleLanguage, hideButton, setPosition, longPressMs };
  const gesture = useRef({
    startPoint: { x: 0, y: 0 },
    startedAt: 0,
    moved: false,
    longPressFired: false,
    overClose: false,
    timer: null as ReturnType<typeof setTimeout> | null,
  });

  const panResponder = useMemo(() => {
    const clearHold = () => {
      const g = gesture.current;
      if (g.timer) {
        clearTimeout(g.timer);
        g.timer = null;
      }
      holdProgress.stopAnimation();
      holdProgress.setValue(0);
      setHolding(false);
    };

    const springTo = (point: { x: number; y: number }) => {
      lastPointRef.current = point;
      Animated.spring(pan, { toValue: point, useNativeDriver: false, friction: 7, tension: 70 }).start();
    };

    const finish = (dx: number, dy: number, cancelled: boolean) => {
      const g = gesture.current;
      const { bounds: b, position: p } = latest.current;
      clearHold();
      if (!b) return;
      if (g.moved) {
        const point = clampDragPoint({ x: g.startPoint.x + dx, y: g.startPoint.y + dy }, b);
        setDragging(false);
        setOverCloseZone(false);
        if (!cancelled && isInCloseZone(point, b)) {
          // 拖到底部红色区：隐藏按钮（位置不变，下次打开还在原处）。
          pan.setValue(resolveDockedPoint(p, b));
          latest.current.hideButton();
          return;
        }
        const next = resolveDropPosition(point, b);
        latest.current.setPosition(next);
        springTo(resolveDockedPoint(next, b));
        return;
      }
      if (!cancelled && isTapGesture({ moved: false, longPressFired: g.longPressFired, pressedMs: Date.now() - g.startedAt })) {
        latest.current.toggleLanguage();
      }
    };

    return PanResponder.create({
      onStartShouldSetPanResponder: () => true,
      onMoveShouldSetPanResponder: () => true,
      // 手指按在按钮上时，不让下面的列表 / 返回手势把触摸抢走。
      onPanResponderTerminationRequest: () => false,
      onPanResponderGrant: () => {
        const g = gesture.current;
        g.startPoint = lastPointRef.current;
        g.startedAt = Date.now();
        g.moved = false;
        g.longPressFired = false;
        g.overClose = false;
        setHolding(true);
        holdProgress.setValue(0);
        Animated.timing(holdProgress, {
          toValue: 1,
          duration: latest.current.longPressMs,
          easing: Easing.linear,
          // SVG 圆环的 strokeDashoffset 不能用原生驱动。
          useNativeDriver: false,
        }).start();
        g.timer = setTimeout(() => {
          g.timer = null;
          if (g.moved) return;
          g.longPressFired = true;
          holdProgress.setValue(0);
          setHolding(false);
          setPanelOpen(true);
        }, latest.current.longPressMs);
      },
      onPanResponderMove: (_event, state) => {
        const g = gesture.current;
        const b = latest.current.bounds;
        if (!b || g.longPressFired) return;
        if (!g.moved) {
          if (!hasMovedBeyondThreshold(state.dx, state.dy)) return;
          g.moved = true;
          clearHold();
          setDragging(true);
        }
        const point = clampDragPoint({ x: g.startPoint.x + state.dx, y: g.startPoint.y + state.dy }, b);
        pan.setValue(point);
        const over = isInCloseZone(point, b);
        if (over !== g.overClose) {
          g.overClose = over;
          setOverCloseZone(over);
        }
      },
      onPanResponderRelease: (_event, state) => finish(state.dx, state.dy, false),
      onPanResponderTerminate: (_event, state) => finish(state.dx, state.dy, true),
    });
  }, [holdProgress, pan]);

  // 组件卸载时清掉长按计时。
  useEffect(
    () => () => {
      if (gesture.current.timer) clearTimeout(gesture.current.timer);
    },
    []
  );

  if (!allowed && !hiddenToastVisible) {
    return null;
  }

  const currentLanguage: AppLanguage = language === "en" ? "en" : "zh";
  const nextLanguage = resolveNextLanguage(currentLanguage);
  const buttonText = t(nextLanguage === "en" ? "floatingLanguage.shortEn" : "floatingLanguage.shortZh");
  const dockedSide = position.side;
  const closeZone = bounds ? resolveCloseZoneCenter(bounds) : null;
  const ringOffset = holdProgress.interpolate({
    inputRange: [0, 1],
    outputRange: [RING_CIRCUMFERENCE, 0],
  });
  // 吸边时按钮有一截在屏幕外，文字往露出的那一侧挪，看起来居中。
  const tuckPadding = dragging || holding
    ? null
    : dockedSide === "right"
      ? { paddingRight: FLOATING_EDGE_TUCK }
      : { paddingLeft: FLOATING_EDGE_TUCK };

  return (
    <View style={StyleSheet.absoluteFill} pointerEvents="box-none" onLayout={onLayout}>
      {dragging && closeZone ? (
        <View pointerEvents="none" style={StyleSheet.absoluteFill}>
          <View style={styles.dragScrim} />
          <View
            style={[
              styles.closeZone,
              {
                left: closeZone.x - FLOATING_CLOSE_ZONE_RADIUS,
                top: closeZone.y - FLOATING_CLOSE_ZONE_RADIUS,
              },
              overCloseZone ? styles.closeZoneActive : null,
            ]}
          >
            <MaterialCommunityIcons name="close" size={30} color={HB_COLORS.white} />
          </View>
          <Text style={[styles.closeHint, { top: closeZone.y + FLOATING_CLOSE_ZONE_RADIUS + 6 }]}>
            {t("floatingLanguage.dragToHide")}
          </Text>
        </View>
      ) : null}

      {buttonVisible ? (
        <Animated.View
          {...panResponder.panHandlers}
          accessible
          accessibilityRole="button"
          accessibilityLabel={t("floatingLanguage.buttonLabel", { current: t(`language.${currentLanguage}`) })}
          accessibilityHint={t("floatingLanguage.buttonHint", {
            next: t(`language.${nextLanguage}`),
            seconds: formatLongPressSeconds(longPressMs),
          })}
          accessibilityActions={[{ name: "activate" }, { name: "longpress" }]}
          onAccessibilityAction={(event) => {
            // 读屏用户没法做拖动和长按手势：双击 = 切换语言，读屏长按 = 打开快速跳转。
            if (event.nativeEvent.actionName === "activate") toggleLanguage();
            if (event.nativeEvent.actionName === "longpress") setPanelOpen(true);
          }}
          style={[
            styles.buttonWrap,
            { transform: [{ translateX: pan.x }, { translateY: pan.y }] },
          ]}
        >
          {holding ? (
            <View pointerEvents="none" style={styles.ring}>
              <Svg width={RING_SIZE} height={RING_SIZE}>
                <Circle
                  cx={RING_SIZE / 2}
                  cy={RING_SIZE / 2}
                  r={RING_RADIUS}
                  stroke="rgba(22, 119, 255, 0.25)"
                  strokeWidth={4}
                  fill="none"
                />
                <AnimatedCircle
                  cx={RING_SIZE / 2}
                  cy={RING_SIZE / 2}
                  r={RING_RADIUS}
                  stroke={HB_COLORS.brand}
                  strokeWidth={4}
                  strokeLinecap="round"
                  fill="none"
                  strokeDasharray={`${RING_CIRCUMFERENCE} ${RING_CIRCUMFERENCE}`}
                  strokeDashoffset={ringOffset}
                  transform={`rotate(-90 ${RING_SIZE / 2} ${RING_SIZE / 2})`}
                />
              </Svg>
            </View>
          ) : null}
          <View
            style={[
              styles.button,
              tuckPadding,
              dragging || holding ? styles.buttonActive : null,
              dragging ? styles.buttonDragging : null,
            ]}
          >
            <Text allowFontScaling={false} style={styles.buttonText}>
              {buttonText}
            </Text>
          </View>
        </Animated.View>
      ) : null}

      {buttonVisible && holding && bounds ? (
        <View
          pointerEvents="none"
          style={[
            styles.holdTip,
            { top: lastPointRef.current.y + 8 },
            dockedSide === "right"
              ? { right: bounds.width - lastPointRef.current.x + 10 }
              : { left: lastPointRef.current.x + FLOATING_BUTTON_SIZE + 10 },
          ]}
        >
          <Text style={styles.holdTipText} numberOfLines={1} maxFontSizeMultiplier={1.3}>
            {t("floatingLanguage.holdHint")}
          </Text>
        </View>
      ) : null}

      {allowed && panelOpen && bounds ? (
        <QuickJumpPanel
          model={model}
          language={currentLanguage}
          menuUnavailable={!menuUsable}
          maxHeight={Math.max(240, bounds.height - insets.top - 24)}
          onClose={closePanel}
          onNavigate={navigateTo}
          onChangeLanguage={changeLanguage}
          onHideButton={hideButton}
        />
      ) : null}

      {hiddenToastVisible ? (
        <View style={styles.toast}>
          <Text style={styles.toastText} maxFontSizeMultiplier={1.4}>
            {t("floatingLanguage.hiddenToast")}
          </Text>
          <Pressable accessibilityRole="button" onPress={undoHide} hitSlop={10}>
            <Text style={styles.toastAction} maxFontSizeMultiplier={1.4}>
              {t("floatingLanguage.undo")}
            </Text>
          </Pressable>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  buttonWrap: {
    position: "absolute",
    left: 0,
    top: 0,
    width: FLOATING_BUTTON_SIZE,
    height: FLOATING_BUTTON_SIZE,
  },
  button: {
    width: FLOATING_BUTTON_SIZE,
    height: FLOATING_BUTTON_SIZE,
    borderRadius: FLOATING_BUTTON_SIZE / 2,
    backgroundColor: "rgba(16, 24, 40, 0.84)",
    alignItems: "center",
    justifyContent: "center",
    shadowColor: "#101828",
    shadowOpacity: 0.3,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 4 },
    elevation: 6,
  },
  buttonActive: {
    backgroundColor: HB_COLORS.brand,
  },
  buttonDragging: {
    transform: [{ scale: 1.12 }],
  },
  buttonText: {
    color: HB_COLORS.white,
    fontSize: 14,
    fontWeight: "800",
    letterSpacing: 0.3,
  },
  ring: {
    position: "absolute",
    left: (FLOATING_BUTTON_SIZE - RING_SIZE) / 2,
    top: (FLOATING_BUTTON_SIZE - RING_SIZE) / 2,
    width: RING_SIZE,
    height: RING_SIZE,
  },
  holdTip: {
    position: "absolute",
    backgroundColor: "#1D2939",
    borderRadius: 9,
    paddingHorizontal: 11,
    paddingVertical: 7,
    maxWidth: 260,
  },
  holdTipText: {
    color: HB_COLORS.white,
    fontSize: 12.5,
    fontWeight: "600",
  },
  dragScrim: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: "rgba(16, 24, 40, 0.18)",
  },
  closeZone: {
    position: "absolute",
    width: FLOATING_CLOSE_ZONE_RADIUS * 2,
    height: FLOATING_CLOSE_ZONE_RADIUS * 2,
    borderRadius: FLOATING_CLOSE_ZONE_RADIUS,
    backgroundColor: "rgba(240, 68, 56, 0.85)",
    alignItems: "center",
    justifyContent: "center",
  },
  closeZoneActive: {
    backgroundColor: "#D92D20",
    transform: [{ scale: 1.2 }],
  },
  closeHint: {
    position: "absolute",
    left: 0,
    right: 0,
    textAlign: "center",
    color: HB_COLORS.white,
    fontSize: 12,
    fontWeight: "700",
    textShadowColor: "rgba(0, 0, 0, 0.5)",
    textShadowRadius: 3,
  },
  toast: {
    position: "absolute",
    left: 12,
    right: 12,
    bottom: 12,
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    backgroundColor: "#1D2939",
    borderRadius: 10,
    paddingHorizontal: 14,
    paddingVertical: 11,
  },
  toastText: {
    flex: 1,
    color: HB_COLORS.white,
    fontSize: 13.5,
  },
  toastAction: {
    color: "#84CAFF",
    fontSize: 14,
    fontWeight: "800",
  },
});
