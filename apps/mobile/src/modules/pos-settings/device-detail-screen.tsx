// 设备覆盖页：每项三态「跟随门店 / 开 / 关」，并给出「应生效」（服务端合并结果）与「设备回报」（收银机实际在用的值），
// 两者不同标「待同步」；顶部显示应用状态与离线临时修改次数。保存同样先弹确认。
import { useCallback, useMemo, useState } from "react";
import { RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { useQueryClient } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter, type Href } from "expo-router";
import { Text } from "react-native-paper";
import { CashCard, CashScreenFrame, NoticeBanner, StatusChip, cashUiStyles } from "@/modules/store-cash/ui";
import { formatUtcInZone } from "@/modules/store-cash/dates";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import { savePosSettingsDevice } from "./api";
import { POS_SETTINGS_SYNC_MINUTES } from "./constants";
import { resolvePosSettingsErrorMessage } from "./errors";
import {
  applyEdit,
  buildBaseStates,
  diffStates,
  filterDefinitionsForClient,
  isSettingPendingSync,
  previewEffective,
  resolveDisplayedState,
  sortDefinitions,
  type TriState,
} from "./logic";
import { LoadingState, MessageState, usePosSettingsAccess } from "./panel-common";
import { posSettingsKeys } from "./query-keys";
import { POS_SETTINGS_ROUTES, firstParam } from "./routes";
import {
  DeviceStatusChip,
  DeviceStatusHint,
  InfoRow,
  InfoValueText,
  SaveBar,
  SaveConfirmSheet,
  SettingCard,
  effectiveText,
} from "./ui";
import { invalidatePosSettings, usePosSettingsCatalog, usePosSettingsDevice, usePosSettingsStore } from "./use-pos-settings";
import { usePosSettingsLeaveGuard } from "./use-leave-guard";
import { useSaveFlow } from "./use-save-flow";
import type { PosSettingEffective } from "./types";

const NO_VALUE: PosSettingEffective = { value: null, source: "none" };

export function PosSettingsDeviceScreen() {
  const { t, language } = useAppTranslation(["posSettings", "common"]);
  const router = useRouter();
  const queryClient = useQueryClient();
  const params = useLocalSearchParams<{ deviceCode?: string | string[] }>();
  const deviceCode = firstParam(params.deviceCode);
  const { canView, canManage } = usePosSettingsAccess();
  const catalogQuery = usePosSettingsCatalog(canView);
  const deviceQuery = usePosSettingsDevice(deviceCode, canView);
  const detail = deviceQuery.data;
  // 设备「跟随门店」时的值要用本店层的生效值预览，所以再取一次本店详情（设备详情里的 effective 已含设备层，不能当上一层）。
  const storeQuery = usePosSettingsStore(detail?.status.storeCode ?? "", canView);
  const [edits, setEdits] = useState<Record<string, TriState>>({});
  const [refreshing, setRefreshing] = useState(false);
  const [savedNotice, setSavedNotice] = useState(false);

  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace(POS_SETTINGS_ROUTES.home as Href);
  }, [router]);

  const clientKind = detail?.status.clientKind ?? "other";
  const definitions = useMemo(
    () => filterDefinitionsForClient(sortDefinitions(catalogQuery.data ?? []), clientKind),
    [catalogQuery.data, clientKind],
  );
  const base = useMemo(() => buildBaseStates(definitions, detail?.overrides ?? {}), [definitions, detail?.overrides]);
  const changes = useMemo(() => diffStates(definitions, base, edits), [base, definitions, edits]);

  /** 设备某项「跟随门店」时的值：本店层生效值；本店详情取不到时，若已保存为跟随则沿用设备详情的生效值。 */
  const parentFor = useCallback(
    (key: string): PosSettingEffective => {
      const fromStore = storeQuery.data?.effective[key];
      if (fromStore) return fromStore;
      if (base[key] === "inherit") return detail?.effective[key] ?? NO_VALUE;
      return NO_VALUE;
    },
    [base, detail?.effective, storeQuery.data?.effective],
  );

  const flow = useSaveFlow({
    changes,
    save: (patch) => savePosSettingsDevice(deviceCode, patch),
    onSaved: async (saved) => {
      queryClient.setQueryData(posSettingsKeys.device(deviceCode), saved);
      setEdits({});
      setSavedNotice(true);
      await invalidatePosSettings(queryClient);
    },
  });
  usePosSettingsLeaveGuard({ dirty: changes.length > 0, busy: flow.busy });

  const refresh = async () => {
    setRefreshing(true);
    try {
      await Promise.all([catalogQuery.refetch(), deviceQuery.refetch(), storeQuery.refetch()]);
    } finally {
      setRefreshing(false);
    }
  };

  const title = t("deviceDetail.title", { device: deviceCode });

  if (!canView) {
    return (
      <CashScreenFrame title={t("title")} onBack={goBack}>
        <MessageState text={t("states.notAllowed")} />
      </CashScreenFrame>
    );
  }
  if (catalogQuery.isPending || deviceQuery.isPending) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <LoadingState />
      </CashScreenFrame>
    );
  }
  if (catalogQuery.isError || deviceQuery.isError || !catalogQuery.data || !detail) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <MessageState
          text={resolvePosSettingsErrorMessage(catalogQuery.error ?? deviceQuery.error, { t, language })}
          actionLabel={t("common:actions.retry")}
          onAction={() => void refresh()}
        />
      </CashScreenFrame>
    );
  }

  const status = detail.status;
  const version = status.appVersion ? t("devices.version", { version: status.appVersion }) : t("devices.unknownVersion");

  return (
    <CashScreenFrame title={title} onBack={goBack}>
      <ScrollView
        style={cashUiStyles.scroll}
        contentContainerStyle={cashUiStyles.content}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void refresh()} />}
      >
        <CashCard>
          <View style={styles.headRow}>
            <Text style={styles.headText} numberOfLines={2}>
              {[detail.storeName || status.storeCode, t(`clients.${status.clientKind}`), version].join(" · ")}
            </Text>
            <DeviceStatusChip status={status.status} />
          </View>
          {status.appliedAtUtc ? (
            <Text style={styles.muted}>{t("deviceDetail.appliedAt", { time: formatUtcInZone(status.appliedAtUtc) })}</Text>
          ) : null}
          <DeviceStatusHint status={status.status} />
        </CashCard>
        {status.offlineOverrideCount > 0 ? (
          <NoticeBanner
            tone="warning"
            icon="lan-disconnect"
            text={
              status.lastOfflineOverrideAtUtc
                ? t("deviceDetail.offlineNotice", {
                    count: status.offlineOverrideCount,
                    time: formatUtcInZone(status.lastOfflineOverrideAtUtc),
                  })
                : t("deviceDetail.offlineNoticeNoTime", { count: status.offlineOverrideCount })
            }
          />
        ) : null}
        <NoticeBanner tone="info" icon="information-outline" text={t("deviceDetail.intro")} />
        {!canManage ? <NoticeBanner tone="neutral" icon="lock-outline" text={t("states.readOnly")} /> : null}
        {savedNotice ? (
          <NoticeBanner tone="success" icon="check-circle-outline" text={t("saved.device", { minutes: POS_SETTINGS_SYNC_MINUTES })} />
        ) : null}
        {definitions.length === 0 ? <Text style={styles.muted}>{t("deviceDetail.unsupportedNone")}</Text> : null}
        {definitions.map((definition) => {
          const state = resolveDisplayedState(definition.key, base, edits);
          const preview = previewEffective("device", state, parentFor(definition.key));
          const reported = detail.reported[definition.key];
          const pending = isSettingPendingSync(definition.key, detail.effective[definition.key], detail.reported, status.status);
          return (
            <SettingCard
              key={definition.key}
              definition={definition}
              scope="device"
              state={state}
              readOnly={!canManage}
              changed={state !== base[definition.key]}
              onChange={(next) => {
                setSavedNotice(false);
                setEdits((current) => applyEdit(current, base, definition.key, next));
              }}
            >
              <InfoRow label={t("effective.label")}>
                <InfoValueText text={effectiveText(t, preview)} />
              </InfoRow>
              <InfoRow label={t("effective.reported")}>
                {pending ? <StatusChip tone="warning" label={t("effective.pendingSync")} /> : null}
                <InfoValueText text={typeof reported === "boolean" ? t(`effective.value.${reported ? "on" : "off"}`) : t("effective.unreported")} />
              </InfoRow>
            </SettingCard>
          );
        })}
      </ScrollView>
      {canManage ? <SaveBar disabled={changes.length === 0 || flow.busy} onPress={flow.request} /> : null}
      <SaveConfirmSheet
        visible={flow.open}
        title={t("confirm.titleDevice", { target: deviceCode })}
        subtitle={t("confirm.subtitleDevice", { minutes: POS_SETTINGS_SYNC_MINUTES })}
        scope="device"
        changes={changes}
        parentFor={parentFor}
        impactText={t("confirm.impactCount", { count: 1 })}
        warning={status.status === "never" ? t("confirm.notSyncedWarning", { devices: deviceCode }) : null}
        error={flow.error}
        busy={flow.busy}
        onCancel={flow.cancel}
        onConfirm={() => void flow.confirm()}
      />
    </CashScreenFrame>
  );
}

const styles = StyleSheet.create({
  muted: { color: HB_COLORS.textSecondary, fontSize: 13 },
  headRow: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", gap: 8 },
  headText: { flex: 1, minWidth: 0, color: HB_COLORS.textPrimary, fontSize: 14 },
});
