// 门店覆盖页：每项三态「跟随全局 / 开 / 关」，右上角显示本店最终值与来源；下面列本店设备；
// 底部固定「保存并下发」（只有 PosSettings.Manage 才显示），点后先弹确认。
import { useCallback, useMemo, useState } from "react";
import { Pressable, RefreshControl, ScrollView, StyleSheet } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQueryClient } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter, type Href } from "expo-router";
import { Text } from "react-native-paper";
import { CashCard, CashScreenFrame, NoticeBanner, cashUiStyles } from "@/modules/store-cash/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { savePosSettingsStore } from "./api";
import { POS_SETTINGS_SYNC_MINUTES } from "./constants";
import { resolvePosSettingsErrorMessage } from "./errors";
import {
  applyEdit,
  buildBaseStates,
  computeImpact,
  diffStates,
  previewEffective,
  resolveDisplayedState,
  resolveGlobalParent,
  sortDefinitions,
  sortDevices,
  type TriState,
} from "./logic";
import { LoadingState, MessageState, usePosSettingsAccess } from "./panel-common";
import { posSettingsKeys } from "./query-keys";
import { POS_SETTINGS_ROUTES, buildPosSettingsDeviceHref, firstParam } from "./routes";
import { DeviceStatusChip, SaveBar, SaveConfirmSheet, SettingCard } from "./ui";
import { invalidatePosSettings, usePosSettingsCatalog, usePosSettingsGlobal, usePosSettingsStore } from "./use-pos-settings";
import { usePosSettingsLeaveGuard } from "./use-leave-guard";
import { useSaveFlow } from "./use-save-flow";
import type { PosSettingDefinition, PosSettingEffective } from "./types";

const NO_VALUE: PosSettingEffective = { value: null, source: "none" };

export function PosSettingsStoreScreen() {
  const { t, language } = useAppTranslation(["posSettings", "common"]);
  const router = useRouter();
  const queryClient = useQueryClient();
  const params = useLocalSearchParams<{ storeCode?: string | string[] }>();
  const storeCode = firstParam(params.storeCode);
  const { canView, canManage } = usePosSettingsAccess();
  const catalogQuery = usePosSettingsCatalog(canView);
  const globalQuery = usePosSettingsGlobal(canView);
  const storeQuery = usePosSettingsStore(storeCode, canView);
  const [edits, setEdits] = useState<Record<string, TriState>>({});
  const [refreshing, setRefreshing] = useState(false);
  const [savedNotice, setSavedNotice] = useState(false);

  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace(POS_SETTINGS_ROUTES.home as Href);
  }, [router]);

  const detail = storeQuery.data;
  const definitions = useMemo(() => sortDefinitions(catalogQuery.data ?? []), [catalogQuery.data]);
  const base = useMemo(() => buildBaseStates(definitions, detail?.overrides ?? {}), [definitions, detail?.overrides]);
  const changes = useMemo(() => diffStates(definitions, base, edits), [base, definitions, edits]);

  /** 本店某项「跟随全局」时的值：优先用全局层现值算（草稿预览要用）；全局取不到时退回服务端算好的本店生效值。 */
  const parentFor = useCallback(
    (definition: PosSettingDefinition): PosSettingEffective => {
      if (globalQuery.data) return resolveGlobalParent(definition, globalQuery.data.values);
      if (base[definition.key] === "inherit") return detail?.effective[definition.key] ?? NO_VALUE;
      return NO_VALUE;
    },
    [base, detail?.effective, globalQuery.data],
  );

  const flow = useSaveFlow({
    changes,
    save: (patch) => savePosSettingsStore(storeCode, patch),
    onSaved: async (saved) => {
      queryClient.setQueryData(posSettingsKeys.store(storeCode), saved);
      setEdits({});
      setSavedNotice(true);
      await invalidatePosSettings(queryClient);
    },
  });
  usePosSettingsLeaveGuard({ dirty: changes.length > 0, busy: flow.busy });

  const refresh = async () => {
    setRefreshing(true);
    try {
      await Promise.all([catalogQuery.refetch(), globalQuery.refetch(), storeQuery.refetch()]);
    } finally {
      setRefreshing(false);
    }
  };

  const title = detail
    ? t("storeDetail.title", { store: detail.storeName || detail.storeCode })
    : t("title");

  if (!canView) {
    return (
      <CashScreenFrame title={t("title")} onBack={goBack}>
        <MessageState text={t("states.notAllowed")} />
      </CashScreenFrame>
    );
  }
  if (catalogQuery.isPending || storeQuery.isPending) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <LoadingState />
      </CashScreenFrame>
    );
  }
  if (catalogQuery.isError || storeQuery.isError || !catalogQuery.data || !detail) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <MessageState
          text={resolvePosSettingsErrorMessage(catalogQuery.error ?? storeQuery.error, { t, language })}
          actionLabel={t("common:actions.retry")}
          onAction={() => void refresh()}
        />
      </CashScreenFrame>
    );
  }

  const impact = computeImpact(detail.devices, definitions, changes);
  const warning = impact.notSynced.length > 0
    ? t("confirm.notSyncedWarning", {
        devices: impact.notSynced
          .map((device) => (device.appVersion ? `${device.deviceCode} (${t(`clients.${device.clientKind}`)} ${device.appVersion})` : device.deviceCode))
          .join(", "),
      })
    : null;
  const devices = sortDevices(detail.devices);

  return (
    <CashScreenFrame title={title} onBack={goBack}>
      <ScrollView
        style={cashUiStyles.scroll}
        contentContainerStyle={cashUiStyles.content}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void refresh()} />}
      >
        <NoticeBanner tone="info" icon="information-outline" text={t("storeDetail.intro")} />
        {!canManage ? <NoticeBanner tone="neutral" icon="lock-outline" text={t("states.readOnly")} /> : null}
        {savedNotice ? (
          <NoticeBanner tone="success" icon="check-circle-outline" text={t("saved.store", { minutes: POS_SETTINGS_SYNC_MINUTES })} />
        ) : null}
        {definitions.map((definition) => {
          const state = resolveDisplayedState(definition.key, base, edits);
          return (
            <SettingCard
              key={definition.key}
              definition={definition}
              scope="store"
              state={state}
              readOnly={!canManage}
              changed={state !== base[definition.key]}
              effective={previewEffective("store", state, parentFor(definition))}
              onChange={(next) => {
                setSavedNotice(false);
                setEdits((current) => applyEdit(current, base, definition.key, next));
              }}
            />
          );
        })}
        <CashCard title={t("storeDetail.devicesTitle")}>
          {devices.length === 0 ? <Text style={styles.muted}>{t("storeDetail.devicesEmpty")}</Text> : null}
          {devices.map((device) => (
            <Pressable
              key={device.deviceCode}
              accessibilityRole="button"
              accessibilityLabel={device.deviceCode}
              onPress={() => router.push(buildPosSettingsDeviceHref(device.deviceCode) as Href)}
              style={styles.deviceRow}
            >
              <Text style={styles.deviceName} numberOfLines={1}>
                {device.deviceCode} · {t(`clients.${device.clientKind}`)}
              </Text>
              <DeviceStatusChip status={device.status} />
              <MaterialCommunityIcons name="chevron-right" size={20} color={HB_COLORS.textSecondary} />
            </Pressable>
          ))}
        </CashCard>
      </ScrollView>
      {canManage ? <SaveBar disabled={changes.length === 0 || flow.busy} onPress={flow.request} /> : null}
      <SaveConfirmSheet
        visible={flow.open}
        title={t("confirm.titleStore", { target: detail.storeName || detail.storeCode })}
        subtitle={t("confirm.subtitleStore", { minutes: POS_SETTINGS_SYNC_MINUTES })}
        scope="store"
        changes={changes}
        parentFor={(key) => {
          const definition = definitions.find((item) => item.key === key);
          return definition ? parentFor(definition) : null;
        }}
        impactText={impact.affectedCount > 0 ? t("confirm.impactCount", { count: impact.affectedCount }) : t("confirm.impactNone")}
        warning={warning}
        error={flow.error}
        busy={flow.busy}
        onCancel={flow.cancel}
        onConfirm={() => void flow.confirm()}
      />
    </CashScreenFrame>
  );
}

const styles = StyleSheet.create({
  muted: { color: HB_COLORS.textSecondary },
  deviceRow: { flexDirection: "row", alignItems: "center", gap: HB_SPACING.xs, minHeight: 48 },
  deviceName: { flex: 1, minWidth: 0, color: HB_COLORS.textPrimary, fontSize: 15 },
});
