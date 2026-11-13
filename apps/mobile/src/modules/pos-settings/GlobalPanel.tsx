// 「全局」页签：每项三态（未设置 / 开 / 关）。「未设置」= 不下发，收银机继续用本机已有的值。
import { useMemo, useState } from "react";
import { RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { useQueryClient } from "@tanstack/react-query";
import { Text } from "react-native-paper";
import { NoticeBanner, cashUiStyles } from "@/modules/store-cash/ui";
import { formatUtcInZone } from "@/modules/store-cash/dates";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { savePosSettingsGlobal } from "./api";
import { POS_SETTINGS_SYNC_MINUTES } from "./constants";
import { resolvePosSettingsErrorMessage } from "./errors";
import {
  applyEdit,
  buildBaseStates,
  diffStates,
  previewEffective,
  resolveDisplayedState,
  sortDefinitions,
  type TriState,
} from "./logic";
import { LoadingState, MessageState } from "./panel-common";
import { posSettingsKeys } from "./query-keys";
import { SaveBar, SaveConfirmSheet, SettingCard } from "./ui";
import { invalidatePosSettings, usePosSettingsCatalog, usePosSettingsGlobal, usePosSettingsStores } from "./use-pos-settings";
import { useSaveFlow } from "./use-save-flow";
import { usePosSettingsLeaveGuard } from "./use-leave-guard";
import type { PosSettingEffective } from "./types";

const NO_VALUE: PosSettingEffective = { value: null, source: "none" };

export function GlobalPanel({ canManage }: { canManage: boolean }) {
  const { t, language } = useAppTranslation(["posSettings", "common"]);
  const queryClient = useQueryClient();
  const catalogQuery = usePosSettingsCatalog(true);
  const globalQuery = usePosSettingsGlobal(true);
  // 门店列表只用来估算「影响设备」总数；取不到不影响保存。
  const storesQuery = usePosSettingsStores(true);
  const [edits, setEdits] = useState<Record<string, TriState>>({});
  const [refreshing, setRefreshing] = useState(false);
  const [savedNotice, setSavedNotice] = useState(false);

  const definitions = useMemo(() => sortDefinitions(catalogQuery.data ?? []), [catalogQuery.data]);
  const base = useMemo(
    () => buildBaseStates(definitions, globalQuery.data?.values ?? {}),
    [definitions, globalQuery.data?.values],
  );
  const changes = useMemo(() => diffStates(definitions, base, edits), [base, definitions, edits]);

  const flow = useSaveFlow({
    changes,
    save: savePosSettingsGlobal,
    onSaved: async (layer) => {
      queryClient.setQueryData(posSettingsKeys.global(), layer);
      setEdits({});
      setSavedNotice(true);
      await invalidatePosSettings(queryClient);
    },
  });
  usePosSettingsLeaveGuard({ dirty: changes.length > 0, busy: flow.busy });

  const refresh = async () => {
    setRefreshing(true);
    try {
      await Promise.all([catalogQuery.refetch(), globalQuery.refetch(), storesQuery.refetch()]);
    } finally {
      setRefreshing(false);
    }
  };

  if (catalogQuery.isPending || globalQuery.isPending) return <LoadingState />;
  if (catalogQuery.isError || globalQuery.isError || !catalogQuery.data || !globalQuery.data) {
    return (
      <MessageState
        text={resolvePosSettingsErrorMessage(catalogQuery.error ?? globalQuery.error, { t, language })}
        actionLabel={t("common:actions.retry")}
        onAction={() => void refresh()}
      />
    );
  }

  const layer = globalQuery.data;
  const totalDevices = storesQuery.data?.reduce((sum, store) => sum + store.deviceTotal, 0);
  const impactText = totalDevices === undefined
    ? t("confirm.impactAll")
    : t("confirm.impactCount", { count: totalDevices });

  return (
    <View style={styles.flex}>
      <ScrollView
        style={cashUiStyles.scroll}
        contentContainerStyle={cashUiStyles.content}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void refresh()} />}
      >
        <NoticeBanner tone="info" icon="information-outline" text={t("global.intro", { minutes: POS_SETTINGS_SYNC_MINUTES })} />
        {!canManage ? <NoticeBanner tone="neutral" icon="lock-outline" text={t("states.readOnly")} /> : null}
        {savedNotice ? (
          <NoticeBanner tone="success" icon="check-circle-outline" text={t("saved.global", { minutes: POS_SETTINGS_SYNC_MINUTES })} />
        ) : null}
        {layer.updatedAtUtc ? (
          <Text style={styles.updated}>
            {layer.updatedByName
              ? t("global.updatedBy", { time: formatUtcInZone(layer.updatedAtUtc), name: layer.updatedByName })
              : t("global.updatedAt", { time: formatUtcInZone(layer.updatedAtUtc) })}
          </Text>
        ) : null}
        {definitions.map((definition) => {
          const state = resolveDisplayedState(definition.key, base, edits);
          // 全局层右上角显示：设置了取自己；没设置时有服务端缺省值显示「关 · 默认」，否则「沿用本机」。
          const unsetParent: PosSettingEffective = definition.defaultWhenUnset === null
            ? NO_VALUE
            : { value: definition.defaultWhenUnset, source: "default" };
          return (
            <SettingCard
              key={definition.key}
              definition={definition}
              scope="global"
              state={state}
              readOnly={!canManage}
              changed={state !== base[definition.key]}
              effective={previewEffective("global", state, unsetParent)}
              onChange={(next) => {
                setSavedNotice(false);
                setEdits((current) => applyEdit(current, base, definition.key, next));
              }}
            />
          );
        })}
      </ScrollView>
      {canManage ? <SaveBar disabled={changes.length === 0 || flow.busy} onPress={flow.request} /> : null}
      <SaveConfirmSheet
        visible={flow.open}
        title={t("confirm.titleGlobal")}
        subtitle={t("confirm.subtitleGlobal", { minutes: POS_SETTINGS_SYNC_MINUTES })}
        scope="global"
        changes={changes}
        parentFor={() => null}
        impactText={impactText}
        error={flow.error}
        busy={flow.busy}
        onCancel={flow.cancel}
        onConfirm={() => void flow.confirm()}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  updated: { color: HB_COLORS.textSecondary, fontSize: 12 },
});
