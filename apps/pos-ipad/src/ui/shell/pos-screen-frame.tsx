import { useContext, type ReactNode } from "react";
import { Text, View, type StyleProp, type ViewStyle } from "react-native";
import {
  SafeAreaInsetsContext,
  SafeAreaView,
} from "react-native-safe-area-context";

import { PosAppRail, type PosNavModel } from "./pos-app-rail";
import type { PosActiveDestination } from "./pos-navigation";
import { usePosNavigation } from "./pos-navigation-context";
import { PosStatusStrip } from "./status-strip";

import { PosButton, type PosIconName } from "@/ui/controls/pos-button";
import { createPosStyles, posTouch, usePosThemeVersion } from "@/ui/theme";

const EDGES_ALL = ["top", "left", "right", "bottom"] as const;
const EDGES_WITHOUT_TOP = ["left", "right", "bottom"] as const;

export type PosScreenFrameProps = Readonly<{
  children: ReactNode;
  /** 侧栏高亮哪一项；不属于任何入口的页面传 "none"。 */
  active?: PosActiveDestination;
  /**
   * 左侧栏来源：
   * - "auto"（默认）：用路由层提供的导航模型；没有 Provider 时不显示。
   * - "none"：这一页不显示左侧栏（登录、注册、付款等不允许中途跳走的页面）。
   * - 传入模型：由页面自己提供入口（收银页）。
   */
  rail?: "auto" | "none" | PosNavModel;
  /** 页面正在处理不能中断的操作时锁住侧栏跳转。 */
  navigationLocked?: boolean;
  /** 页头，通常是 <PosPageHeader />。 */
  header?: ReactNode;
  /** 顶栏；默认显示。传 false 隐藏，或传入自定义节点。 */
  topBar?: ReactNode | false;
  language?: string;
  onSwitchLanguage?: () => void;
  appVersionLabel?: string | null;
  style?: StyleProp<ViewStyle>;
  contentStyle?: StyleProp<ViewStyle>;
  testID?: string;
  accessibilityRole?: "alert";
}>;

/**
 * 页面外壳：顶栏 + 左侧栏 + 页头 + 内容区。
 * 所有页面套同一副骨架，状态、导航、返回键的位置保持一致。
 */
export function PosScreenFrame({
  accessibilityRole,
  active = "none",
  appVersionLabel,
  children,
  contentStyle,
  header,
  language,
  navigationLocked = false,
  onSwitchLanguage,
  rail = "auto",
  style,
  testID,
  topBar,
}: PosScreenFrameProps) {
  usePosThemeVersion();
  // 没有 SafeAreaProvider 时（单独渲染、测试）取到 null，按 0 处理。
  const topInset = useContext(SafeAreaInsetsContext)?.top ?? 0;
  const navigation = usePosNavigation();
  const model =
    rail === "none" ? null : rail === "auto" ? (navigation?.model ?? null) : rail;
  const switchLanguage = onSwitchLanguage ?? navigation?.switchLanguage;
  const resolvedLanguage = language ?? navigation?.language;
  const versionLabel =
    appVersionLabel === undefined
      ? (navigation?.appVersionLabel ?? null)
      : appVersionLabel;

  // 顶栏是深色底、状态栏文字是浅色：把顶部安全区也铺成顶栏颜色，
  // 否则系统时间和电量会落在浅色页面底上看不清。
  // 这块深色用普通流式布局放在 SafeAreaView 外面（SafeAreaView 只处理左右下），
  // 不用绝对定位：原生 SafeAreaView 的内边距会让绝对定位的填充层落不到屏幕最顶端。
  const showTopInsetFill = topBar !== false && topInset > 0;

  return (
    <View style={styles.root}>
      {showTopInsetFill ? (
        <View
          pointerEvents="none"
          style={[styles.topInsetFill, { height: topInset }]}
          testID="pos-top-inset-fill"
        />
      ) : null}
    <SafeAreaView
      accessibilityRole={accessibilityRole}
      edges={showTopInsetFill ? EDGES_WITHOUT_TOP : EDGES_ALL}
      style={[styles.frame, style]}
      testID={testID}
    >
      {topBar === false
        ? null
        : (topBar ?? (
            <PosStatusStrip
              appVersionLabel={versionLabel}
              showBrand
              showTerminalIdentity
              {...(resolvedLanguage ? { language: resolvedLanguage } : {})}
              {...(switchLanguage ? { onSwitchLanguage: switchLanguage } : {})}
            />
          ))}
      <View style={styles.body}>
        {model ? (
          <PosAppRail
            active={active}
            language={resolvedLanguage}
            locked={navigationLocked}
            model={model}
          />
        ) : null}
        <View style={[styles.content, contentStyle]}>
          {header}
          {children}
        </View>
      </View>
    </SafeAreaView>
    </View>
  );
}

export type PosPageHeaderProps = Readonly<{
  title: string;
  subtitle?: string | undefined;
  /** 标题上方的小字分类（例如「门店运营」）。 */
  eyebrow?: string | undefined;
  /** 返回按钮；不传 onBack 则不显示。 */
  onBack?: (() => void) | undefined;
  backLabel?: string | undefined;
  backDisabled?: boolean;
  backIcon?: PosIconName;
  backTestID?: string;
  /** 页头右侧的页面级操作（刷新、筛选分段等）。 */
  actions?: ReactNode;
  /** 紧跟标题的状态标签等。 */
  titleAccessory?: ReactNode;
  testID?: string;
  titleTestID?: string;
}>;

/** 页头：返回键 + 标题与副标题 + 右侧页面级操作，高度固定，所有页面一致。 */
export function PosPageHeader({
  actions,
  backDisabled = false,
  backIcon = "arrow-left",
  backLabel,
  backTestID,
  eyebrow,
  onBack,
  subtitle,
  testID,
  title,
  titleAccessory,
  titleTestID,
}: PosPageHeaderProps) {
  return (
    <View style={styles.header} testID={testID}>
      {onBack && backLabel ? (
        <PosButton
          disabled={backDisabled}
          icon={backIcon}
          label={backLabel}
          onPress={onBack}
          size="medium"
          sound="navigate"
          testID={backTestID}
        />
      ) : null}
      <View style={styles.headerCopy}>
        {eyebrow ? (
          <Text numberOfLines={1} style={styles.eyebrow}>
            {eyebrow}
          </Text>
        ) : null}
        <View style={styles.titleRow}>
          <Text
            accessibilityRole="header"
            numberOfLines={1}
            style={styles.title}
            testID={titleTestID}
          >
            {title}
          </Text>
          {titleAccessory}
        </View>
        {subtitle ? (
          <Text numberOfLines={1} style={styles.subtitle}>
            {subtitle}
          </Text>
        ) : null}
      </View>
      {actions ? <View style={styles.headerActions}>{actions}</View> : null}
    </View>
  );
}

const styles = createPosStyles((colors) => ({
  body: {
    flex: 1,
    flexDirection: "row",
    minHeight: 0,
  },
  content: {
    flex: 1,
    minHeight: 0,
    minWidth: 0,
  },
  eyebrow: {
    color: colors.accent,
    fontSize: 12,
    fontWeight: "900",
    letterSpacing: 1.5,
  },
  frame: {
    backgroundColor: colors.canvas,
    flex: 1,
  },
  root: {
    backgroundColor: colors.canvas,
    flex: 1,
  },
  topInsetFill: {
    backgroundColor: colors.chrome,
  },
  header: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderBottomColor: colors.border,
    borderBottomWidth: 1,
    flexDirection: "row",
    gap: 14,
    minHeight: posTouch.large,
    paddingHorizontal: 14,
    paddingVertical: 8,
  },
  headerActions: {
    alignItems: "center",
    flexDirection: "row",
    flexShrink: 0,
    gap: 10,
  },
  headerCopy: {
    flex: 1,
    minWidth: 0,
  },
  subtitle: {
    color: colors.mutedInk,
    fontSize: 13,
    fontWeight: "600",
    marginTop: 2,
  },
  title: {
    color: colors.ink,
    flexShrink: 1,
    fontSize: 23,
    fontWeight: "900",
  },
  titleRow: {
    alignItems: "center",
    flexDirection: "row",
    gap: 10,
  },
}));
