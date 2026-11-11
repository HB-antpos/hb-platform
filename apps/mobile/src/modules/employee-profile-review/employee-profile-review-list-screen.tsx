import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { FlatList, RefreshControl, StyleSheet, View } from "react-native";
import { keepPreviousData, useInfiniteQuery, useQueryClient } from "@tanstack/react-query";
import { useFocusEffect, useRouter } from "expo-router";
import { ActivityIndicator, Button, Searchbar, Text, useTheme } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import { getEmployeeProfileReviewAccess } from "./access";
import { getEmployeeProfileReviewRequestsApi } from "./api";
import {
  EMPLOYEE_PROFILE_REVIEW_SEARCH_DEBOUNCE_MS,
  getEmployeeProfileReviewListQueryKey,
  getEmployeeProfileReviewSort,
  getEmployeeProfileReviewStatusFilter,
  normalizeEmployeeProfileReviewSearch,
  type EmployeeProfileReviewSegment,
} from "./list-filters";
import { clearEmployeeProfileReviewListCache } from "./review-cache";
import { getReviewFailureKind } from "./review-logic";
import { getReviewPersonLabel } from "./review-list-presentation";
import { ReviewRequestCard, ReviewSegmentControl, ReviewStoreSheet } from "./review-list-parts";
import { mergeUniqueEmployeeProfileReviewPages } from "./pagination";
import type { EmployeeProfileReviewSummary } from "./types";
import { useAppNavigationStore } from "@/modules/navigation/store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";

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
  // 分店多（例如总部账号挂了几十家店）时卡片只写第一家，点「等 N 家」在弹层里看全部。
  const [storeSheet, setStoreSheet] = useState<{ name: string; storeNames: string[] } | null>(null);
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
      // 待审核「等得最久的在前」；已处理沿用后端默认顺序。旧后端忽略 sort，只是顺序仍是最新在前。
      sort: getEmployeeProfileReviewSort(segment),
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

  const openStores = (item: EmployeeProfileReviewSummary) => {
    setStoreSheet({ name: getReviewPersonLabel(item).primary, storeNames: item.storeNames });
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
  // 搜索时 total 只是命中数，不能当作「待审核总数」展示在页签上。
  const pendingCountForTab = !isProcessed && !search ? total : null;
  const emptyTitle = search ? t("list.searchEmpty") : t(isProcessed ? "list.processedEmpty" : "list.empty");
  const emptyDescription = search
    ? t("list.searchEmptyDescription")
    : t(isProcessed ? "list.processedEmptyDescription" : "list.emptyDescription");
  return (
    <SafeAreaView style={[styles.container, { backgroundColor: theme.colors.background }]} edges={["top"]}>
      <View style={styles.header}>
        <View style={styles.titleRow}>
          <Text style={styles.title} numberOfLines={2} accessibilityRole="header" selectable>
            {t("list.title")}
          </Text>
          {/* 未成年用工合规是另一条审核队列，降为页头右侧的小入口，不再占一整行。 */}
          <Button
            icon="shield-account-outline"
            mode="outlined"
            compact
            style={styles.minorButton}
            contentStyle={styles.minorButtonContent}
            onPress={() => router.push("/(shell)/employee-minor-employment-review" as never)}
          >
            {t("list.minorEmploymentShort")}
          </Button>
        </View>
        <ReviewSegmentControl value={segment} onChange={setSegment} t={t} pendingCount={pendingCountForTab} />
        <Searchbar
          value={searchInput}
          onChangeText={setSearchInput}
          placeholder={t("list.searchPlaceholder")}
          autoCapitalize="none"
          autoCorrect={false}
          style={styles.searchbar}
          inputStyle={styles.searchInput}
        />
      </View>
      <View style={styles.metaRow}>
        <Text style={styles.metaText} accessibilityLiveRegion="polite">
          {t(isProcessed ? "list.processedSummary" : "list.pendingSummary", { count: total })}
        </Text>
        {reviewQuery.isPlaceholderData ? <ActivityIndicator size="small" /> : null}
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
          <ReviewRequestCard
            item={item}
            language={language}
            t={t}
            onOpen={openDetail}
            onOpenStores={openStores}
          />
        )}
      />
      <ReviewStoreSheet target={storeSheet} onDismiss={() => setStoreSheet(null)} t={t} />
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  container: BUSINESS_UI.screen,
  centered: { flex: 1, alignItems: "center", justifyContent: "center", padding: HB_SPACING.lg },
  header: { ...BUSINESS_UI.header, backgroundColor: HB_COLORS.white, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: HB_COLORS.outlineMuted },
  titleRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: HB_SPACING.xs, minHeight: 44 },
  title: { ...BUSINESS_UI.title, flex: 1 },
  minorButton: { borderRadius: 18 },
  minorButtonContent: { minHeight: 36 },
  searchbar: { backgroundColor: HB_COLORS.surfaceMuted, elevation: 0, height: 44, borderRadius: 10 },
  searchInput: { minHeight: 0, alignSelf: "center" },
  metaRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm - 2, paddingBottom: HB_SPACING.xxs },
  metaText: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  list: { paddingHorizontal: HB_SPACING.sm + 2, paddingTop: HB_SPACING.xxs, paddingBottom: HB_SPACING.xl, gap: HB_SPACING.xs },
  emptyList: { flexGrow: 1, justifyContent: "center", padding: HB_SPACING.md },
  touchTarget: { minHeight: 48 },
  pageLoader: { paddingVertical: HB_SPACING.md },
});
