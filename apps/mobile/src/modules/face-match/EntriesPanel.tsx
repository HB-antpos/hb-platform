// 「名单条目」分区：按状态 / 门店筛选的条目列表，下拉刷新、触底翻页；批准、撤回、申诉备注；提交新条目入口。
// 按钮可用性只认 GET /face-match/config 的权限标志与服务端条目上的 canApprove，App 不自己推断谁能批准。
import { useState } from "react";
import { FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { useMutation } from "@tanstack/react-query";
import { useRouter } from "expo-router";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { EmptyState } from "@/components/ui/EmptyState";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import type { Store } from "@/modules/shop/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { canSubmitEntries, isWriteBlocked } from "./access";
import { faceMatchApi } from "./api";
import { ENTRY_STATUS_FILTERS, resolveEntryActions, type EntryStatusFilterKey, entryStatusFromFilter } from "./entries-logic";
import { extractHttpStatus } from "./errors";
import { EntryActionDialogs, type EntryAction } from "./EntryActionDialogs";
import { EntryCard } from "./EntryCard";
import { useFaceErrorMessage } from "./shared";
import type { FaceMatchConfig, FaceUnwelcomeEntry } from "./types";
import { useInvalidateUnwelcome, useUnwelcomeEntries } from "./use-face-match";
import { FilterChip, Notice, faceStyles } from "./ui";

interface MutationInput {
  kind: EntryAction["kind"];
  entry: FaceUnwelcomeEntry;
  text?: string;
}

export function EntriesPanel({ config, stores }: { config: FaceMatchConfig; stores: readonly Store[] }) {
  const { t } = useAppTranslation(["faceMatch", "common"]);
  const router = useRouter();
  const describeError = useFaceErrorMessage();
  const invalidate = useInvalidateUnwelcome();
  const [statusKey, setStatusKey] = useState<EntryStatusFilterKey>("all");
  const [storeCode, setStoreCode] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [action, setAction] = useState<EntryAction | null>(null);
  const [actionError, setActionError] = useState<string | undefined>();
  const [snack, setSnack] = useState("");
  const [refreshing, setRefreshing] = useState(false);
  const { query, items, refresh } = useUnwelcomeEntries(entryStatusFromFilter(statusKey), storeCode, true);
  const writeBlocked = isWriteBlocked(config);
  const { permissions } = config;

  const mutation = useMutation({
    mutationFn: async ({ kind, entry, text }: MutationInput) => {
      if (kind === "approve") return faceMatchApi.approveEntry(entry.entryId);
      if (kind === "withdraw") return faceMatchApi.withdrawEntry(entry.entryId, text ?? "");
      return faceMatchApi.setDisputeNote(entry.entryId, text ?? "");
    },
    onSuccess: async (_result, input) => {
      setAction(null);
      setActionError(undefined);
      setSnack(t(`unwelcome.${input.kind}.done`));
      await invalidate();
    },
    onError: async (error) => {
      setActionError(describeError(error));
      // 404 / 409：条目刚被别人处理或状态已变，刷新列表让按钮与进度回到真实状态。
      const status = extractHttpStatus(error);
      if (status === 404 || status === 409) await invalidate();
    },
  });

  const run = (input: MutationInput) => mutation.mutate(input);
  const openAction = (kind: EntryAction["kind"], entry: FaceUnwelcomeEntry) => {
    setActionError(undefined);
    mutation.reset();
    setAction({ kind, entry });
  };

  const onRefresh = async () => {
    setRefreshing(true);
    try {
      await refresh();
    } finally {
      setRefreshing(false);
    }
  };

  const selectedStore = stores.find((store) => store.storeCode === storeCode) ?? null;
  const canCreate = permissions.unwelcomeManage;
  const canSubmit = canSubmitEntries(config);

  const header = (
    <View style={styles.header}>
      <Notice text={t("unwelcome.intro")} />
      {canCreate && !config.objectStoreConfigured ? <Notice tone="warning" text={t("unwelcome.objectStoreMissing")} /> : null}
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips}>
        {ENTRY_STATUS_FILTERS.map((filter) => (
          <FilterChip key={filter.key} label={t(filter.labelKey)} selected={filter.key === statusKey} onPress={() => setStatusKey(filter.key)} />
        ))}
      </ScrollView>
      <View style={faceStyles.rowBetween}>
        <FilterChip
          icon="storefront-outline"
          label={selectedStore ? selectedStore.storeName || selectedStore.storeCode : t("filters.allStores")}
          selected={storeCode !== null}
          onPress={() => setPickerOpen(true)}
        />
        {canCreate ? (
          <Button
            mode="contained"
            icon="plus"
            compact
            disabled={!canSubmit}
            onPress={() => router.push({ pathname: "/(shell)/face-unwelcome/entry-new" })}
          >
            {t("unwelcome.actions.create")}
          </Button>
        ) : null}
      </View>
    </View>
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
        <EmptyState title={t("unwelcome.empty")} />
      </View>
    );
  }

  return (
    <View style={styles.flex}>
      <FlatList
        data={items}
        keyExtractor={(item) => item.entryId}
        renderItem={({ item }) => (
          <EntryCard
            entry={item}
            actions={resolveEntryActions(item, permissions, writeBlocked)}
            busy={mutation.isPending && action?.entry.entryId === item.entryId}
            onApprove={() => openAction("approve", item)}
            onDispute={() => openAction("dispute", item)}
            onWithdraw={() => openAction("withdraw", item)}
          />
        )}
        ListHeaderComponent={header}
        ListEmptyComponent={empty}
        ListFooterComponent={footer}
        onEndReachedThreshold={0.4}
        onEndReached={() => {
          if (query.hasNextPage && !query.isFetchingNextPage && !query.isFetchNextPageError) void query.fetchNextPage();
        }}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void onRefresh()} tintColor={HB_COLORS.brand} />}
        contentContainerStyle={styles.content}
      />
      <StorePickerModal
        presentation="sheet"
        visible={pickerOpen}
        stores={[...stores]}
        selectedStoreCode={storeCode}
        includeAllOption
        allLabel={t("filters.allStores")}
        title={t("filters.pickStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setPickerOpen(false)}
        onSelectStore={(store) => {
          setStoreCode(store?.storeCode ?? null);
          setPickerOpen(false);
        }}
      />
      <EntryActionDialogs
        action={action}
        busy={mutation.isPending}
        errorMessage={actionError}
        onCancel={() => setAction(null)}
        onApprove={(entry) => run({ kind: "approve", entry })}
        onWithdraw={(entry, text) => run({ kind: "withdraw", entry, text })}
        onDispute={(entry, text) => run({ kind: "dispute", entry, text })}
      />
      <Snackbar visible={snack !== ""} onDismiss={() => setSnack("")} duration={3000}>
        {snack}
      </Snackbar>
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  content: { paddingBottom: HB_SPACING.xl },
  header: { paddingHorizontal: HB_SPACING.md, paddingTop: HB_SPACING.sm, paddingBottom: HB_SPACING.xs, gap: HB_SPACING.xs },
  chips: { gap: HB_SPACING.xs, paddingVertical: 2 },
  footer: { paddingVertical: HB_SPACING.sm, alignItems: "center", textAlign: "center" },
  footerError: { fontSize: 13, color: HB_COLORS.action },
});
