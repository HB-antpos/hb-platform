import { useCallback, useEffect, useRef, useState } from "react";
import { FlatList, Pressable, RefreshControl, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter, type Href } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery } from "@tanstack/react-query";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { EmptyState } from "@/components/ui/EmptyState";
import { formatOrderDate } from "@/modules/orders/order-list-display";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { claimPickSlip, fetchPickOrders, resolvePickOrder } from "../api";
import { readPickingError } from "../api-normalization";
import { pickingErrorMessage } from "../picking-errors";
import { isSlipCode } from "../code-resolver";
import { claimRouteParams, shortPickerName } from "../pick-view-model";
import { PickHeader, PickerChip } from "../components/PickHeader";
import { PickCameraSheet } from "../components/PickCameraSheet";
import { MONO_FONT, PICK_COLORS, segmentColor } from "../components/pick-theme";
import type { PickerIdentity, PickOrderFilter, PickOrderListItem } from "../types";

const FLOW_PICKING = 3;

/** 拣货入口（确认拣货人 / 订单列表）。 */
export const PICKING_HOME = "/(shell)/warehouse-picking" as Href;

export function pickingRoute(orderGuid: string, suffix = "") {
  return `/warehouse-picking/${encodeURIComponent(orderGuid)}${suffix}` as Href;
}

/** 扫分单后进入拣货页：带上段号与领取提示。 */
export function pickingSlipRoute(orderGuid: string, params: Record<string, string>) {
  return {
    pathname: "/(shell)/warehouse-picking/[orderGuid]",
    params: { orderGuid, ...params },
  } as unknown as Href;
}

/** 选择订单：列表、搜索，或直接扫配货单上的订单条码进入拣货。 */
export function PickOrderListView({
  picker,
  onBack,
  onSwitchPicker,
}: {
  picker: PickerIdentity;
  onBack: () => void;
  onSwitchPicker: () => void;
}) {
  const { t, language } = useAppTranslation("warehousePicking");
  const router = useRouter();
  const focused = useIsFocused();
  const [filter, setFilter] = useState<PickOrderFilter>("all");
  // 第一次拿到“派给我”的数量且大于 0 时默认切过去；拣货员手动选过筛选后不再自动切换。
  const filterTouchedRef = useRef(false);
  const [keyword, setKeyword] = useState("");
  const [debouncedKeyword, setDebouncedKeyword] = useState("");
  const [searchFocused, setSearchFocused] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [resolving, setResolving] = useState(false);
  const [snackbar, setSnackbar] = useState("");
  const resolvingRef = useRef(false);
  const localeTag = resolveLocaleTag(language);

  useEffect(() => {
    preloadScanFeedbackSounds();
  }, []);
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedKeyword(keyword.trim()), 300);
    return () => clearTimeout(timer);
  }, [keyword]);

  const query = useQuery({
    queryKey: ["warehousePicking", "orders", filter, debouncedKeyword],
    queryFn: ({ signal }) => fetchPickOrders(filter, debouncedKeyword, signal),
    enabled: focused,
    staleTime: 10_000,
    // 列表在前台时定期刷新：别人开始拣或提交后状态会变。
    refetchInterval: focused ? 30_000 : false,
  });

  useEffect(() => {
    const mineCount = query.data?.counts.mine;
    if (filterTouchedRef.current || mineCount == null) return;
    filterTouchedRef.current = true;
    if (mineCount > 0 && filter === "all") setFilter("mine");
  }, [filter, query.data?.counts.mine]);

  const openOrder = useCallback(
    (orderGuid: string) => router.push(pickingRoute(orderGuid)),
    [router],
  );

  const handleScan = useCallback(
    async (raw: string) => {
      const code = raw.trim();
      if (!code || resolvingRef.current) return;
      resolvingRef.current = true;
      setResolving(true);
      try {
        if (isSlipCode(code)) {
          // 分单条码：领取这一段（已被领取则只提示），直接进入那张单的那一段。
          const claim = await claimPickSlip(code);
          playScanFeedbackSound("found");
          router.push(pickingSlipRoute(claim.orderGuid, claimRouteParams(claim)));
          return;
        }
        const orderGuid = await resolvePickOrder(code);
        playScanFeedbackSound("found");
        openOrder(orderGuid);
      } catch (error) {
        playScanFeedbackSound("not_found");
        const { code: errorCode } = readPickingError(error);
        setSnackbar(errorCode === "ORDER_NOT_FOUND" ? t("orders.unknownCode") : pickingErrorMessage(error, t, language));
      } finally {
        resolvingRef.current = false;
        setResolving(false);
      }
    },
    [language, openOrder, router, t],
  );

  const hid = useHidBarcodeScanner({
    enabled: focused && !searchFocused && !cameraVisible && !resolving,
    onScan: handleScan,
  });

  const counts = query.data?.counts;
  const filters: { key: PickOrderFilter; label: string }[] = [
    // 认不出拣货人（设备会话）时服务端不返回 mine，不显示“派给我”。
    ...(counts?.mine != null ? [{ key: "mine" as const, label: t("orders.filterMine", { count: counts.mine }) }] : []),
    { key: "all", label: t("orders.filterAll", { count: counts?.all ?? 0 }) },
    { key: "toPick", label: t("orders.filterToPick", { count: counts?.toPick ?? 0 }) },
    { key: "picking", label: t("orders.filterPicking", { count: counts?.picking ?? 0 }) },
  ];

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.screen}>
      <PickHeader
        title={t("orders.title")}
        onBack={onBack}
        backLabel={t("actions.back")}
        right={
          <PickerChip
            name={picker.name}
            label={shortPickerName(picker.name)}
            accessibilityLabel={t("picker.switchPicker", { name: picker.name })}
            onPress={onSwitchPicker}
          />
        }
      >
        <View style={styles.searchBlock}>
          <View style={styles.searchRow}>
            <View style={styles.searchBox}>
              <MaterialCommunityIcons name="magnify" size={18} color={PICK_COLORS.textSecondary} />
              <TextInput
                accessibilityLabel={t("orders.searchLabel")}
                placeholder={t("orders.searchPlaceholder")}
                placeholderTextColor={PICK_COLORS.textSecondary}
                value={keyword}
                onChangeText={setKeyword}
                onFocus={() => {
                  setSearchFocused(true);
                  hid.pauseHiddenInputFocus();
                }}
                onBlur={() => {
                  setSearchFocused(false);
                  hid.resumeHiddenInputFocus();
                }}
                autoCapitalize="characters"
                autoCorrect={false}
                returnKeyType="search"
                style={styles.searchInput}
              />
            </View>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("orders.cameraLabel")}
              onPress={() => setCameraVisible(true)}
              style={styles.cameraButton}
            >
              <MaterialCommunityIcons name="camera-outline" size={22} color={PICK_COLORS.action} />
            </Pressable>
          </View>
          <View style={styles.hintRow}>
            <MaterialCommunityIcons name="barcode" size={14} color={PICK_COLORS.textSecondary} />
            <Text style={styles.hint}>{t("orders.scanHint")}</Text>
            {resolving ? <ActivityIndicator size={12} style={styles.hintSpinner} /> : null}
          </View>
          <View accessibilityRole="tablist" accessibilityLabel={t("orders.filterLabel")} style={styles.segment}>
            {filters.map((item, index) => (
              <Pressable
                key={item.key}
                accessibilityRole="tab"
                accessibilityState={{ selected: filter === item.key }}
                onPress={() => {
                  filterTouchedRef.current = true;
                  setFilter(item.key);
                }}
                style={[styles.segmentItem, index > 0 ? styles.segmentDivider : null, filter === item.key ? styles.segmentActive : null]}
              >
                <Text numberOfLines={1} style={[styles.segmentText, filter === item.key ? styles.segmentTextActive : null]}>
                  {item.label}
                </Text>
              </Pressable>
            ))}
          </View>
        </View>
      </PickHeader>

      <FlatList
        data={query.data?.items ?? []}
        keyExtractor={(item) => item.orderGuid}
        contentContainerStyle={styles.list}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={query.isRefetching} onRefresh={() => void query.refetch()} />}
        ListEmptyComponent={
          query.isLoading ? (
            <ActivityIndicator style={styles.loading} />
          ) : query.isError ? (
            <EmptyState
              title={pickingErrorMessage(query.error, t, language)}
              actionLabel={t("actions.retry")}
              onAction={() => void query.refetch()}
            />
          ) : (
            <EmptyState title={t("orders.emptyTitle")} description={t("orders.emptyHint")} />
          )
        }
        renderItem={({ item }) => (
          <OrderCard item={item} localeTag={localeTag} myUserGuid={picker.userGuid} onPress={() => openOrder(item.orderGuid)} />
        )}
      />

      <PickCameraSheet
        visible={cameraVisible}
        title={t("orders.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={(barcode) => void handleScan(barcode)}
      />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3000}>
        {snackbar}
      </Snackbar>
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={styles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </SafeAreaView>
  );
}

function OrderCard({
  item,
  localeTag,
  myUserGuid,
  onPress,
}: {
  item: PickOrderListItem;
  localeTag: string;
  myUserGuid: string;
  onPress: () => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const picking = item.flowStatus === FLOW_PICKING;
  const names = item.pickers.map((entry) => shortPickerName(entry.pickerName)).join("、");
  const progress = item.lineCount > 0 ? Math.min(1, item.pickedLineCount / item.lineCount) : 0;
  return (
    <Pressable accessibilityRole="button" onPress={onPress} style={styles.card}>
      <View style={styles.cardTop}>
        <Text numberOfLines={1} style={styles.store}>
          {item.storeName || item.storeCode || "—"}
        </Text>
        <Text style={[styles.tag, picking ? styles.tagPicking : styles.tagToPick]}>
          {picking ? (names ? t("orders.statusPickingBy", { names }) : t("orders.statusPicking")) : t("orders.statusToPick")}
        </Text>
      </View>
      <Text style={styles.meta}>
        <Text style={styles.orderNo}>{item.orderNo || "—"}</Text>
        {" · "}
        {t("orders.submittedAt", { time: formatOrderDate(item.orderDate ?? undefined, localeTag) })}
      </Text>
      {item.assignees.length > 0 ? (
        // 经理派单的各段：自己那段加粗，待领取的段标出来，方便员工拿分单去领。
        <View style={styles.assignees}>
          {item.assignees.map((assignee) => {
            const isMe = Boolean(assignee.pickerUserGuid && assignee.pickerUserGuid.toLowerCase() === myUserGuid.toLowerCase());
            return (
              <View key={assignee.segmentNo} style={[styles.assignee, isMe ? styles.assigneeMe : null]}>
                <View style={[styles.assigneeDot, { backgroundColor: segmentColor(assignee.segmentNo) }]} />
                <Text style={[styles.assigneeText, isMe ? styles.assigneeTextMe : null, !assignee.pickerName ? styles.assigneeClaimable : null]}>
                  {assignee.pickerName ? shortPickerName(assignee.pickerName) : t("orders.claimable")} {assignee.lineCount}
                </Text>
              </View>
            );
          })}
        </View>
      ) : null}
      {picking ? (
        <View style={styles.progressTrack}>
          <View style={[styles.progressFill, { width: `${Math.round(progress * 100)}%` }]} />
        </View>
      ) : null}
      <View style={styles.cardBottom}>
        <Text style={styles.summary}>
          {picking
            ? t("orders.pickedLines", { picked: item.pickedLineCount, total: item.lineCount })
            : t("orders.summary", { lines: item.lineCount, pieces: item.totalQuantity })}
        </Text>
        <View style={styles.cta}>
          <Text style={styles.ctaText}>{picking ? t("orders.join") : t("orders.start")}</Text>
          <MaterialCommunityIcons name="chevron-right" size={18} color={PICK_COLORS.action} />
        </View>
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: PICK_COLORS.background },
  searchBlock: { paddingHorizontal: 12, paddingBottom: 12, gap: 8 },
  searchRow: { flexDirection: "row", gap: 8 },
  searchBox: {
    flex: 1,
    height: 44,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 12,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: PICK_COLORS.outline,
    backgroundColor: PICK_COLORS.white,
  },
  searchInput: { flex: 1, minWidth: 0, fontSize: 15, color: PICK_COLORS.ink, paddingVertical: 0 },
  cameraButton: { width: 44, height: 44, borderRadius: 8, backgroundColor: PICK_COLORS.infoBg, alignItems: "center", justifyContent: "center" },
  hintRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  hint: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  hintSpinner: { marginLeft: 4 },
  segment: { flexDirection: "row", height: 44, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.outline, overflow: "hidden" },
  segmentItem: { flex: 1, alignItems: "center", justifyContent: "center", backgroundColor: PICK_COLORS.white, paddingHorizontal: 4 },
  segmentDivider: { borderLeftWidth: 1, borderLeftColor: PICK_COLORS.outline },
  segmentActive: { backgroundColor: PICK_COLORS.infoBg },
  segmentText: { fontSize: 13, fontWeight: "500", color: PICK_COLORS.neutralChipText },
  segmentTextActive: { fontWeight: "700", color: PICK_COLORS.action },
  list: { padding: 12, gap: 10, flexGrow: 1 },
  loading: { marginTop: 48 },
  card: { backgroundColor: PICK_COLORS.white, borderWidth: 1, borderColor: PICK_COLORS.outlineMuted, borderRadius: 12, padding: 12, gap: 6 },
  cardTop: { flexDirection: "row", alignItems: "center", gap: 8 },
  store: { flex: 1, fontSize: 16, lineHeight: 22, fontWeight: "700", color: PICK_COLORS.ink },
  tag: { fontSize: 12, lineHeight: 22, fontWeight: "600", paddingHorizontal: 8, borderRadius: 11, overflow: "hidden" },
  tagToPick: { backgroundColor: PICK_COLORS.neutralChipBg, color: PICK_COLORS.neutralChipText },
  tagPicking: { backgroundColor: PICK_COLORS.warningBg, color: PICK_COLORS.warning },
  meta: { fontSize: 13, lineHeight: 18, color: PICK_COLORS.textSecondary },
  orderNo: { fontFamily: MONO_FONT, fontWeight: "700", color: PICK_COLORS.ink },
  progressTrack: { height: 6, borderRadius: 3, backgroundColor: PICK_COLORS.outlineMuted, overflow: "hidden" },
  assignees: { flexDirection: "row", flexWrap: "wrap", gap: 6 },
  assignee: {
    minHeight: 24,
    flexDirection: "row",
    alignItems: "center",
    gap: 5,
    paddingHorizontal: 8,
    borderRadius: 12,
    backgroundColor: PICK_COLORS.neutralChipBg,
  },
  assigneeMe: { backgroundColor: PICK_COLORS.infoBg, borderWidth: 1, borderColor: PICK_COLORS.infoBorder },
  assigneeDot: { width: 8, height: 8, borderRadius: 4 },
  assigneeText: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.neutralChipText, fontVariant: ["tabular-nums"] },
  assigneeTextMe: { fontWeight: "700", color: PICK_COLORS.infoText },
  assigneeClaimable: { color: PICK_COLORS.warning },
  progressFill: { height: 6, borderRadius: 3, backgroundColor: PICK_COLORS.warningStrong },
  cardBottom: { flexDirection: "row", alignItems: "center" },
  summary: { flex: 1, fontSize: 13, lineHeight: 20, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  cta: { minHeight: 32, flexDirection: "row", alignItems: "center", gap: 2 },
  ctaText: { fontSize: 14, fontWeight: "700", color: PICK_COLORS.action },
  hiddenInput: { position: "absolute", width: 1, height: 1, opacity: 0, left: -100 },
});
