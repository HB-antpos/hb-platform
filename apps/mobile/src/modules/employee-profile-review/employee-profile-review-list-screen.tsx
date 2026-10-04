import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, RefreshControl, StyleSheet, View } from "react-native";
import { keepPreviousData, useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { useFocusEffect, useRouter } from "expo-router";
import { ActivityIndicator, Badge, Button, Card, Chip, Searchbar, SegmentedButtons, Text, useTheme } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { getEmployeeProfileReviewAccess } from "./access";
import { getEmployeeProfileReviewRequestsApi } from "./api";
import {
  EMPLOYEE_PROFILE_REVIEW_SEARCH_DEBOUNCE_MS,
  EMPLOYEE_PROFILE_REVIEW_SEGMENTS,
  getEmployeeProfileReviewListQueryKey,
  getEmployeeProfileReviewStatusFilter,
  normalizeEmployeeProfileReviewSearch,
  type EmployeeProfileReviewSegment,
} from "./list-filters";
import { clearEmployeeProfileReviewListCache } from "./review-cache";
import { getReviewFailureKind } from "./review-logic";
import { mergeUniqueEmployeeProfileReviewPages } from "./pagination";
import type { EmployeeProfileReviewStatus, EmployeeProfileReviewSummary } from "./types";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

const STATUS_ICONS: Record<EmployeeProfileReviewStatus, string> = {
  Pending: "clock-outline",
  Approved: "check-circle-outline",
  Rejected: "close-circle-outline",
  Superseded: "minus-circle-outline",
  Withdrawn: "undo-variant",
};

function formatDateTime(value: string, language: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return new Intl.DateTimeFormat(language, {
    dateStyle: "medium",
    timeStyle: "short",
  }).format(date);
}

export function EmployeeProfileReviewListScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const theme = useTheme();
  const { t, language } = useAppTranslation("employeeProfileReview");
  const currentUser = useAuthStore((state) => state.user);
  const sessionKind = useAuthStore((state) => state.sessionKind);
  const navigationItems = useAppNavigationStore((state) => state.items);
  const navigationReady = useAppNavigationStore((state) => state.isReady);
  const mountedRef = useRef(true);
  const [listPrivacyHidden, setListPrivacyHidden] = useState(false);
  const [segment, setSegment] = useState<EmployeeProfileReviewSegment>("pending");
  const [searchInput, setSearchInput] = useState("");
  const [search, setSearch] = useState("");
  const listQueryKey = useMemo(
    () => getEmployeeProfileReviewListQueryKey(segment, search),
    [search, segment]
  );
  const listQueryKeyRef = useRef(listQueryKey);
  listQueryKeyRef.current = listQueryKey;
  useEffect(() => {
    const timer = setTimeout(
      () => setSearch(normalizeEmployeeProfileReviewSearch(searchInput)),
      EMPLOYEE_PROFILE_REVIEW_SEARCH_DEBOUNCE_MS
    );
    return () => clearTimeout(timer);
  }, [searchInput]);
  const reviewAccess = useMemo(
    () => getEmployeeProfileReviewAccess({
      roleNames: currentUser?.roleNames,
      permissions: currentUser?.permissions,
      menuRouteNames: navigationItems.map((item) => item.routeName),
      sessionKind,
    }),
    [currentUser?.permissions, currentUser?.roleNames, navigationItems, sessionKind]
  );
  const reviewQuery = useInfiniteQuery({
    queryKey: listQueryKey,
    enabled: navigationReady && reviewAccess.allowed,
    // 切换分段/搜索时保留上一屏结果，避免整页转圈导致搜索框失焦。
    placeholderData: keepPreviousData,
    initialPageParam: 1,
    queryFn: ({ pageParam }) => getEmployeeProfileReviewRequestsApi({
      page: pageParam,
      pageSize: 50,
      status: getEmployeeProfileReviewStatusFilter(segment),
      search: search || undefined,
    }),
    getNextPageParam: (lastPage) =>
      lastPage.page * lastPage.pageSize < lastPage.total
        ? lastPage.page + 1
        : undefined,
  });

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  const clearListAndExit = useCallback(async () => {
    if (mountedRef.current) {
      setListPrivacyHidden(true);
    }
    await clearEmployeeProfileReviewListCache(queryClient);
    if (mountedRef.current) {
      router.replace("/(shell)/settings");
    }
  }, [queryClient, router]);

  const listScopeInvalidated = getReviewFailureKind(reviewQuery.error) === "forbidden";

  useEffect(() => {
    if (navigationReady && (!reviewAccess.allowed || listScopeInvalidated)) {
      // 直接深链也必须在发起详情请求前关闭，最终授权仍由后端执行。
      void clearListAndExit();
    }
  }, [clearListAndExit, listScopeInvalidated, navigationReady, reviewAccess.allowed]);

  useFocusEffect(useCallback(() => {
    if (!navigationReady || !reviewAccess.allowed || listScopeInvalidated) {
      return;
    }
    setListPrivacyHidden(false);
    // offset 分页重新聚焦时丢弃旧后续页，从新首页重新建立稳定分页窗口。
    // 只在聚焦时重置当前分段/搜索的列表；切换分段或搜索由新的查询键自然触发首屏请求，不重复请求。
    void queryClient.resetQueries({
      queryKey: listQueryKeyRef.current,
      exact: true,
    });
  }, [listScopeInvalidated, navigationReady, queryClient, reviewAccess.allowed]));

  const openDetail = (item: EmployeeProfileReviewSummary) => {
    router.push({
      pathname: "/employee-profile-review/[requestId]",
      params: { requestId: String(item.requestId) },
    });
  };

  if (listPrivacyHidden || listScopeInvalidated) {
    return <SafeAreaView style={styles.centered} edges={["top"]} />;
  }

  if (!navigationReady || (reviewQuery.isLoading && !reviewQuery.data)) {
    return (
      <SafeAreaView style={styles.centered} edges={["top"]}>
        <ActivityIndicator size="large" accessibilityLabel={t("list.loading")} />
      </SafeAreaView>
    );
  }

  if (!reviewAccess.allowed) {
    return (
      <SafeAreaView style={styles.centered} edges={["top"]}>
        <Text selectable>{t("messages.permissionChanged")}</Text>
      </SafeAreaView>
    );
  }

  if (reviewQuery.isError && !reviewQuery.data) {
    return (
      <SafeAreaView style={styles.centered} edges={["top"]}>
        <EmptyState
          title={t("messages.listLoadFailed")}
          description={t("messages.retryHint")}
          primaryAction={{
            label: t("actions.retry"),
            icon: "refresh",
            onPress: () => void reviewQuery.refetch(),
          }}
        />
      </SafeAreaView>
    );
  }

  const items = mergeUniqueEmployeeProfileReviewPages(reviewQuery.data?.pages ?? []);
  const total = reviewQuery.data?.pages[0]?.total ?? 0;
  const isProcessed = segment === "processed";
  const emptyTitle = search ? t("list.searchEmpty") : t(isProcessed ? "list.processedEmpty" : "list.empty");
  const emptyDescription = search
    ? t("list.searchEmptyDescription")
    : t(isProcessed ? "list.processedEmptyDescription" : "list.emptyDescription");
  return (
    <SafeAreaView style={[styles.container, { backgroundColor: theme.colors.background }]} edges={["top"]}>
      <View style={styles.header}>
        <View style={styles.titleRow}>
          <Text variant="headlineSmall" selectable>{t("list.title")}</Text>
          <Badge accessibilityLabel={t(isProcessed ? "list.processedCount" : "list.pendingCount", { count: total })}>
            {total}
          </Badge>
          {reviewQuery.isPlaceholderData ? <ActivityIndicator size="small" /> : null}
        </View>
        <Button
          icon="shield-account-outline"
          mode="outlined"
          compact
          onPress={() => router.push("/(shell)/employee-minor-employment-review" as never)}
        >
          未成年用工合规
        </Button>
        <Text variant="bodyMedium" style={{ color: theme.colors.onSurfaceVariant }} selectable>
          {t("list.subtitle")}
        </Text>
        <SegmentedButtons
          value={segment}
          onValueChange={(value) => setSegment(value as EmployeeProfileReviewSegment)}
          buttons={EMPLOYEE_PROFILE_REVIEW_SEGMENTS.map((value) => ({
            value,
            label: t(`segments.${value}`),
          }))}
        />
        <Searchbar
          value={searchInput}
          onChangeText={setSearchInput}
          placeholder={t("list.searchPlaceholder")}
          autoCapitalize="none"
          autoCorrect={false}
          style={styles.searchbar}
        />
      </View>
      <FlatList
        data={items}
        keyExtractor={(item) => String(item.requestId)}
        contentInsetAdjustmentBehavior="automatic"
        contentContainerStyle={items.length ? styles.list : styles.emptyList}
        refreshControl={(
          <RefreshControl
            refreshing={reviewQuery.isRefetching && !reviewQuery.isFetchingNextPage}
            onRefresh={() => void reviewQuery.refetch()}
          />
        )}
        onEndReachedThreshold={0.35}
        onEndReached={() => {
          if (reviewQuery.hasNextPage && !reviewQuery.isFetchingNextPage) {
            void reviewQuery.fetchNextPage();
          }
        }}
        ListFooterComponent={reviewQuery.isFetchingNextPage ? (
          <ActivityIndicator style={styles.pageLoader} accessibilityLabel={t("list.loadingMore")} />
        ) : reviewQuery.isFetchNextPageError ? (
          <View style={styles.pageLoader}>
            <Button
              icon="refresh"
              mode="text"
              contentStyle={styles.touchTarget}
              onPress={() => void reviewQuery.fetchNextPage()}
            >
              {t("list.loadMoreFailed")}
            </Button>
          </View>
        ) : null}
        ListEmptyComponent={(
          <EmptyState
            title={emptyTitle}
            description={emptyDescription}
            primaryAction={{
              label: t("actions.refresh"),
              icon: "refresh",
              onPress: () => void reviewQuery.refetch(),
            }}
          />
        )}
        renderItem={({ item }) => (
          <Card
            mode="outlined"
            style={styles.requestCard}
            onPress={() => openDetail(item)}
            accessibilityLabel={t("list.openRequest", { name: item.username || item.userGuid })}
          >
            <Card.Content style={styles.cardContent}>
              <View style={styles.cardTitleRow}>
                <View style={styles.employeeText}>
                  <Text variant="titleMedium" selectable numberOfLines={1}>
                    {item.username || item.userGuid}
                  </Text>
                  <Text variant="bodySmall" style={{ color: theme.colors.onSurfaceVariant }} selectable>
                    {item.storeNames.length ? item.storeNames.join(" · ") : t("list.storeUnavailable")}
                  </Text>
                </View>
                <Chip compact icon={STATUS_ICONS[item.status]}>{t(`statuses.${item.status}`)}</Chip>
              </View>
              <View style={styles.chips}>
                {item.changedFields.map((field) => (
                  <Chip compact key={field}>{t(`fields.${field}`)}</Chip>
                ))}
              </View>
              <View style={styles.footerRow}>
                <Text variant="bodySmall" style={{ color: theme.colors.onSurfaceVariant }} selectable>
                  {formatDateTime(item.submittedAt, language)}
                </Text>
                <Button
                  compact
                  mode="text"
                  contentStyle={styles.touchTarget}
                  onPress={() => openDetail(item)}
                >
                  {t(item.status === "Pending" ? "actions.review" : "actions.view")}
                </Button>
              </View>
            </Card.Content>
          </Card>
        )}
      />
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: BUSINESS_UI.screen,
  centered: { flex: 1, alignItems: "center", justifyContent: "center", padding: HB_SPACING.lg },
  header: { ...BUSINESS_UI.header, backgroundColor: HB_COLORS.white, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  titleRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  searchbar: { backgroundColor: HB_COLORS.surfaceMuted, elevation: 0 },
  list: { ...BUSINESS_UI.content, paddingBottom: HB_SPACING.xl },
  emptyList: { flexGrow: 1, justifyContent: "center", padding: HB_SPACING.md },
  requestCard: BUSINESS_UI.section,
  cardContent: { ...BUSINESS_UI.sectionContent, paddingVertical: HB_SPACING.sm },
  cardTitleRow: { flexDirection: "row", alignItems: "flex-start", gap: HB_SPACING.sm },
  employeeText: { flex: 1, gap: HB_SPACING.xxs },
  chips: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  footerRow: { minHeight: 48, flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs },
  touchTarget: { minHeight: 48 },
  pageLoader: { paddingVertical: HB_SPACING.md },
});
