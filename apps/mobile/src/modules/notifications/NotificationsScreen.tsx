// 通知收件箱：按时间倒序的站内消息，下拉刷新、无限滚动、回到前台刷新；点击即标已读，右上「全部已读」。
// 标题与正文是后端固定的英文文案，中英文界面都原样展示；只有界面文字（已读、优先级、门店、时间）走 i18n。
// 点击疑似命中通知进「人脸比对」合并页（有抓拍权限进抓拍的黑名单未读，否则进名单的命中复核），其他类型按未知类型兜底（只标已读，不跳转）。本期不做 SignalR 与系统推送。
import { useEffect, useState } from "react";
import { AppState, FlatList, Pressable, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { FACE_SOFT, FaceCard, FaceScreenFrame, FaceScreenMessage, Tag, faceStyles } from "@/modules/face-match/ui";
import { buildFaceHubRoute, resolveFaceHubTabs } from "@/modules/navigation/face-hub";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { describeNotificationTime, isHighPriority, isUnread, resolveNotificationTarget } from "./logic";
import type { NotificationItem } from "./types";
import { useMarkAllNotificationsRead, useMarkNotificationsRead, useNotificationInbox } from "./use-notifications";

export function NotificationsScreen() {
  const router = useRouter();
  const { t } = useAppTranslation(["notifications", "common"]);
  const userGuid = useAuthStore((state) => state.user?.userGUID);
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const review = useAuthStore((state) => state.iosReviewOfflineGuardActive) || sessionKind === "iosReview";
  const goBack = () => (router.canGoBack() ? router.back() : router.replace("/(shell)/workbench"));
  // 审核模式没有离线通知；设备会话没有用户账号，收件箱按账号计算，同样不可用。
  if (review || !isAuthenticated || sessionKind === "device") {
    return <FaceScreenMessage message={t(review ? "messages.reviewUnavailable" : "messages.notAllowed")} backLabel={t("common:actions.back")} onBack={goBack} />;
  }
  // 账号变化时重建页面状态，避免把上一个账号的通知留在屏幕上。
  return <NotificationsContent key={userGuid} onBack={goBack} />;
}

function NotificationsContent({ onBack }: { onBack: () => void }) {
  const { t, language } = useAppTranslation(["notifications", "common"]);
  const router = useRouter();
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const { query, items, unreadCount, refresh } = useNotificationInbox(true);
  const markRead = useMarkNotificationsRead();
  const markAllRead = useMarkAllNotificationsRead();
  const [refreshing, setRefreshing] = useState(false);
  const [snack, setSnack] = useState("");
  // 合并页的页签各按各自权限显示：抓拍页签、名单页签各自能不能打开决定点开命中通知后去哪一页。
  const hubTabs = isAuthenticated ? resolveFaceHubTabs(hasPermission) : [];
  const targetAccess = { canViewCapture: hubTabs.includes("capture"), canViewList: hubTabs.includes("list") };

  const describeError = (error: unknown) =>
    resolveLocalizedErrorMessage(error, { t, language, fallbackKey: "errors.generic", allowRawMessageInChinese: false });

  // 回到前台补齐：应用在后台期间可能收到了新通知（本期没有实时通道）。
  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      if (next === "active") void refresh();
    });
    return () => subscription.remove();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- refresh 每次渲染都是新函数，只需订阅一次，内部读取的是最新缓存键
  }, []);

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await refresh();
    } finally {
      setRefreshing(false);
    }
  };

  const onOpen = (item: NotificationItem) => {
    // 点击即标已读（失败只提示，不拦住跳转）；已读的不重复请求。
    if (isUnread(item)) {
      markRead.mutate([item.id], { onError: (error) => setSnack(describeError(error)) });
    }
    const target = resolveNotificationTarget(item, targetAccess);
    if (target.kind === "route") router.push(buildFaceHubRoute(target.params));
  };

  const onMarkAll = () => {
    markAllRead.mutate(undefined, { onError: (error) => setSnack(describeError(error)) });
  };

  const right = (
    <Button compact disabled={unreadCount === 0 || markAllRead.isPending} onPress={onMarkAll}>
      {t("markAllRead")}
    </Button>
  );

  let footer = null;
  if (query.isFetchingNextPage) footer = <ActivityIndicator size="small" color={HB_COLORS.brand} style={styles.footer} />;
  else if (query.isFetchNextPageError) {
    footer = (
      <Pressable accessibilityRole="button" onPress={() => void query.fetchNextPage()} style={styles.footer}>
        <Text style={styles.footerError}>{t("states.loadMoreFailed")}</Text>
      </Pressable>
    );
  } else if (items.length > 0 && !query.hasNextPage) {
    footer = <Text style={[faceStyles.muted, styles.footer]}>{t("states.end")}</Text>;
  }

  let empty;
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
        <EmptyState title={t("states.loadFailed")} description={describeError(query.error)} actionLabel={t("common:actions.retry")} onAction={() => void query.refetch()} />
      </View>
    );
  } else {
    empty = (
      <View style={faceStyles.state}>
        <EmptyState title={t("empty")} />
      </View>
    );
  }

  return (
    <FaceScreenFrame title={t("title")} onBack={onBack} right={right}>
      <FlatList
        data={items}
        keyExtractor={(item) => item.id}
        renderItem={({ item }) => <NotificationCard item={item} onOpen={() => onOpen(item)} />}
        ListEmptyComponent={empty}
        ListFooterComponent={footer}
        onEndReachedThreshold={0.4}
        onEndReached={() => {
          if (query.hasNextPage && !query.isFetchingNextPage && !query.isFetchNextPageError) void query.fetchNextPage();
        }}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void onRefresh()} tintColor={HB_COLORS.brand} />}
        contentContainerStyle={styles.content}
      />
      <Snackbar visible={snack !== ""} onDismiss={() => setSnack("")} duration={3500}>
        {snack}
      </Snackbar>
    </FaceScreenFrame>
  );
}

function NotificationCard({ item, onOpen }: { item: NotificationItem; onOpen: () => void }) {
  const { t } = useAppTranslation("notifications");
  const unread = isUnread(item);
  const high = isHighPriority(item);
  const time = describeNotificationTime(item.createdAtUtc, new Date());
  const timeText = time ? (time.kind === "yesterday" ? t("time.yesterday") : time.text) : "";
  // 未读的高优先级通知整卡淡红高亮；已读的整体变淡。
  return (
    <Pressable accessibilityRole="button" onPress={onOpen}>
      <FaceCard style={unread && high ? styles.highCard : undefined}>
        <View style={faceStyles.rowBetween}>
          <View style={styles.titleRow}>
            {unread ? <View style={[styles.dot, high ? styles.dotHigh : null]} accessibilityLabel={t("unread")} /> : null}
            <Text style={[styles.title, unread ? null : styles.readText]} numberOfLines={2}>
              {item.title}
            </Text>
          </View>
          {unread ? (high ? <Tag label={t("priority.high")} tone="danger" /> : null) : <Text style={faceStyles.muted}>{t("read")}</Text>}
        </View>
        <Text style={[styles.body, unread ? null : styles.readText]}>{item.body}</Text>
        <View style={faceStyles.rowBetween}>
          <Text style={faceStyles.muted}>{item.storeCode ? t("storeLine", { store: item.storeCode }) : ""}</Text>
          <Text style={faceStyles.muted}>{timeText}</Text>
        </View>
      </FaceCard>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  content: { paddingTop: HB_SPACING.xs, paddingBottom: HB_SPACING.xl },
  footer: { paddingVertical: HB_SPACING.md, textAlign: "center", alignSelf: "center" },
  footerError: { fontSize: 13, color: HB_COLORS.danger },
  highCard: { backgroundColor: FACE_SOFT.danger, borderColor: "#FDA29B" },
  titleRow: { flex: 1, flexDirection: "row", alignItems: "center", gap: 6 },
  dot: { width: 8, height: 8, borderRadius: 4, backgroundColor: HB_COLORS.brand },
  dotHigh: { backgroundColor: HB_COLORS.danger },
  title: { flexShrink: 1, fontSize: 15, fontWeight: "700", color: HB_COLORS.textPrimary },
  body: { fontSize: 13, lineHeight: 19, color: HB_COLORS.textPrimary },
  readText: { color: HB_COLORS.textSecondary },
});
