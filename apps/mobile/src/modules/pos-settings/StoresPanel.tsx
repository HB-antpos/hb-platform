// 「门店」页签：搜索 + 筛选 + 门店列表（覆盖项标签、设备已应用情况），点进门店覆盖页。
import { useMemo, useState } from "react";
import { Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useRouter, type Href } from "expo-router";
import { Text, TextInput } from "react-native-paper";
import { ChoiceChip, StatusChip, cashUiStyles } from "@/modules/store-cash/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { resolvePosSettingsErrorMessage } from "./errors";
import {
  countStoreFilters,
  filterStoreSummaries,
  listOverrideEntries,
  sortDefinitions,
  summarizeStoreApply,
  type StoreListFilter,
} from "./logic";
import { LoadingState, MessageState } from "./panel-common";
import { buildPosSettingsStoreHref } from "./routes";
import { settingName } from "./ui";
import { usePosSettingsCatalog, usePosSettingsStores } from "./use-pos-settings";
import type { PosSettingStoreSummary } from "./types";

export function StoresPanel() {
  const { t, language } = useAppTranslation(["posSettings", "common"]);
  const router = useRouter();
  const storesQuery = usePosSettingsStores(true);
  const catalogQuery = usePosSettingsCatalog(true);
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<StoreListFilter>("all");
  const [refreshing, setRefreshing] = useState(false);

  const definitions = useMemo(() => sortDefinitions(catalogQuery.data ?? []), [catalogQuery.data]);
  const summaries = storesQuery.data;
  const counts = useMemo(() => countStoreFilters(summaries ?? []), [summaries]);
  const visible = useMemo(() => filterStoreSummaries(summaries ?? [], query, filter), [filter, query, summaries]);

  const refresh = async () => {
    setRefreshing(true);
    try {
      await Promise.all([storesQuery.refetch(), catalogQuery.refetch()]);
    } finally {
      setRefreshing(false);
    }
  };

  if (storesQuery.isPending) return <LoadingState />;
  if (storesQuery.isError || !summaries) {
    return (
      <MessageState
        text={resolvePosSettingsErrorMessage(storesQuery.error, { t, language })}
        actionLabel={t("common:actions.retry")}
        onAction={() => void refresh()}
      />
    );
  }

  return (
    <ScrollView
      style={cashUiStyles.scroll}
      contentContainerStyle={cashUiStyles.content}
      keyboardShouldPersistTaps="handled"
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void refresh()} />}
    >
      <TextInput
        mode="outlined"
        value={query}
        onChangeText={setQuery}
        placeholder={t("stores.searchPlaceholder")}
        left={<TextInput.Icon icon="magnify" />}
        autoCapitalize="none"
        autoCorrect={false}
        returnKeyType="search"
        style={styles.search}
      />
      <View style={cashUiStyles.rowWrap}>
        <ChoiceChip label={t("stores.filterAll", { count: counts.all })} selected={filter === "all"} onPress={() => setFilter("all")} />
        <ChoiceChip label={t("stores.filterOverrides", { count: counts.overrides })} selected={filter === "overrides"} onPress={() => setFilter("overrides")} />
        <ChoiceChip label={t("stores.filterUnsynced", { count: counts.unsynced })} selected={filter === "unsynced"} onPress={() => setFilter("unsynced")} />
      </View>
      {visible.length === 0 ? <Text style={styles.empty}>{summaries.length === 0 ? t("states.noStores") : t("states.noResults")}</Text> : null}
      {visible.map((summary) => (
        <StoreRow
          key={summary.storeCode}
          summary={summary}
          overrideTags={listOverrideEntries(definitions, summary.overrides).map((entry) =>
            t("stores.overrideTag", {
              name: settingName(t, entry.key),
              value: t(`effective.value.${entry.value ? "on" : "off"}`),
            }),
          )}
          onPress={() => router.push(buildPosSettingsStoreHref(summary.storeCode) as Href)}
        />
      ))}
    </ScrollView>
  );
}

function StoreRow({
  summary,
  overrideTags,
  onPress,
}: {
  summary: PosSettingStoreSummary;
  overrideTags: string[];
  onPress: () => void;
}) {
  const { t } = useAppTranslation("posSettings");
  const apply = summarizeStoreApply(summary);
  const name = summary.storeName || summary.storeCode;
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${name} ${summary.storeCode}`}
      onPress={onPress}
      style={styles.row}
    >
      <View style={styles.rowTop}>
        <View style={styles.rowName}>
          <Text variant="titleSmall" style={styles.storeName} numberOfLines={1}>{name}</Text>
          <Text style={styles.storeCode}>{summary.storeCode}</Text>
        </View>
        {apply.kind === "applied" ? (
          <StatusChip tone="success" label={t("stores.applied", { applied: apply.applied, total: apply.total })} />
        ) : apply.kind === "pending" ? (
          <StatusChip tone="warning" label={t("stores.pending", { applied: apply.applied, total: apply.total })} />
        ) : (
          <StatusChip tone="neutral" label={t("stores.noDevices")} />
        )}
        <MaterialCommunityIcons name="chevron-right" size={20} color={HB_COLORS.textSecondary} />
      </View>
      {overrideTags.length > 0 ? (
        <View style={cashUiStyles.rowWrap}>
          {overrideTags.map((tag) => <StatusChip key={tag} tone="info" label={tag} />)}
        </View>
      ) : (
        <Text style={styles.follow}>{t("stores.followGlobal")}</Text>
      )}
      {summary.deviceOverrideCount > 0 ? (
        <Text style={styles.follow}>{t("stores.deviceOverrides", { count: summary.deviceOverrideCount })}</Text>
      ) : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  search: { backgroundColor: HB_COLORS.white },
  empty: { color: HB_COLORS.textSecondary, textAlign: "center", padding: HB_SPACING.lg },
  row: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    padding: HB_SPACING.md,
    gap: HB_SPACING.xs,
    minHeight: 64,
  },
  rowTop: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs },
  rowName: { flex: 1, minWidth: 0, flexDirection: "row", alignItems: "baseline", gap: HB_SPACING.xs },
  storeName: { color: HB_COLORS.textPrimary, fontWeight: "700", flexShrink: 1 },
  storeCode: { color: HB_COLORS.textSecondary, fontSize: 12 },
  follow: { color: HB_COLORS.textSecondary, fontSize: 13 },
});
