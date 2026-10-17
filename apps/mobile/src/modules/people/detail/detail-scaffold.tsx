import type { ReactNode } from "react";
import { useCallback, useState } from "react";
import { ScrollView, StyleSheet, type NativeScrollEvent, type NativeSyntheticEvent } from "react-native";
import { Portal, Snackbar } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { HB_COLORS } from "@/shared/theme/tokens";
import { DetailAppBar, detailStyles } from "./detail-ui";

/** 滚过头部卡片后，顶栏改显示人名（A2② 的「下滑后」状态）。 */
const SCROLLED_TITLE_OFFSET = 96;

export interface DetailScaffoldProps {
  /** 顶部栏静态标题（店长版「员工详情」/ 管理员版「人员详情」）。 */
  title: string;
  /** 下滑后顶栏的人名与副标题（@账号 · 角色）。 */
  scrolledTitle: string;
  scrolledSubtitle: string;
  backLabel: string;
  onBack: () => void;
  appBarActions: { key: string; icon: string; label: string; onPress: () => void; testID?: string }[];
  children: ReactNode;
  /** 所有弹层 / 确认框（条件渲染，关闭即卸载）。 */
  overlays?: ReactNode;
  snackbarMessage: string;
  onSnackbarDismiss: () => void;
}

/**
 * 详情页外壳：顶栏 + 可滚动内容 + 吸底区 + 弹层挂载点 + Snackbar。
 *
 * Snackbar 放进 Portal 且仅在有消息时才挂载：Portal 按挂载顺序叠放，后挂载的在上层，
 * 这样「弹层关闭前发生的提示」也不会被已经打开的底部弹层盖住。
 */
export function DetailScaffold({
  title,
  scrolledTitle,
  scrolledSubtitle,
  backLabel,
  onBack,
  appBarActions,
  children,
  overlays,
  snackbarMessage,
  onSnackbarDismiss,
}: DetailScaffoldProps) {
  const [scrolled, setScrolled] = useState(false);
  const handleScroll = useCallback((event: NativeSyntheticEvent<NativeScrollEvent>) => {
    const next = event.nativeEvent.contentOffset.y > SCROLLED_TITLE_OFFSET;
    // 只在跨过阈值时更新状态，避免每一帧滚动都触发重渲染。
    setScrolled((current) => (current === next ? current : next));
  }, []);

  return (
    // 底部安全区由 shell 的 PrimaryTabBar 负责，这里只处理上 / 左 / 右。
    <SafeAreaView style={styles.screen} edges={["top", "left", "right"]}>
      <DetailAppBar
        title={scrolled && scrolledTitle ? scrolledTitle : title}
        subtitle={scrolled && scrolledTitle ? scrolledSubtitle : undefined}
        backLabel={backLabel}
        onBack={onBack}
        actions={appBarActions}
      />
      <ScrollView
        style={styles.scroll}
        contentContainerStyle={detailStyles.content}
        onScroll={handleScroll}
        scrollEventThrottle={32}
        keyboardShouldPersistTaps="handled"
      >
        {children}
      </ScrollView>
      {overlays}
      {snackbarMessage ? (
        <Portal>
          <Snackbar visible onDismiss={onSnackbarDismiss} duration={2800}>{snackbarMessage}</Snackbar>
        </Portal>
      ) : null}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  screen: { backgroundColor: HB_COLORS.background, flex: 1 },
  scroll: { flex: 1 },
});
