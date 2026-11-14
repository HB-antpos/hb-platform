// 「设备」页签：先选门店，再列该店设备（状态标签），点进设备页。
import { useMemo, useState } from "react";
import { Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useRouter, type Href } from "expo-router";
import { Text } from "react-native-paper";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { StatusChip, cashUiStyles } from "@/modules/store-cash/ui";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useCartStore } from "@/store/cart-store";
import { resolvePosSettingsErrorMessage } from "./errors";
import { sortDevices } from "./logic";
import { LoadingState, MessageState } from "./panel-common";
import { buildPosSettingsDeviceHref } from "./routes";
import { DeviceStatusChip, DeviceStatusHint } from "./ui";
import { usePosSettingsDevices, usePosSettingsStores } from "./use-pos-settings";
import type { PosSettingDeviceStatus } from "./types";

export function DevicesPanel() {
  const { t, language } = useAppTranslation(["posSettings", "common"]);
  const router = useRouter();
  const cartStoreCode = useCartStore((state) => state.selectedStore?.storeCode);
  const storesQuery = usePosSettingsStores(true);
  const [pickedStoreCode, setPickedStoreCode] = useState<string | null>(null);
  const [pickerVisible, setPickerVisible] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const stores = storesQuery.data;
  // 默认选 App 当前所在门店（在可见列表里才算）；用户选过以用户选的为准。
  const storeCode = useMemo(() => {
    if (!stores) return "";
    if (pickedStoreCode && stores.some((store) => store.storeCode === pickedStoreCode)) return pickedStoreCode;
    if (cartStoreCode && stores.some((store) => store.storeCode === cartStoreCode)) return cartStoreCode;
    return "";
  }, [cartStoreCode, pickedStoreCode, stores]);
  const activeStore = stores?.find((store) => store.storeCode === storeCode) ?? null;
  const devicesQuery = usePosSettingsDevices(storeCode, true);
  const pickerStores = useMemo(
    () => (stores ?? []).map((store) => ({ storeCode: store.storeCode, storeName: store.storeName || store.storeCode })),
    [stores],
  );
  const devices = useMemo(() => sortDevices(devicesQuery.data ?? []), [devicesQuery.data]);

  const refresh = async () => {
    setRefreshing(true);
    try {
      await Promise.all([storesQuery.refetch(), storeCode ? devicesQuery.refetch() : Promise.resolve()]);
    } finally {
      setRefreshing(false);
    }
  };

  if (storesQuery.isPending) return <LoadingState />;
  if (storesQuery.isError || !stores) {
    return (
      <MessageState
        text={resolvePosSettingsErrorMessage(storesQuery.error, { t, language })}
        actionLabel={t("common:actions.retry")}
        onAction={() => void refresh()}
      />
    );
  }
  if (stores.length === 0) return <MessageState text={t("states.noStores")} />;

  return (
    <>
      <ScrollView
        style={cashUiStyles.scroll}
        contentContainerStyle={cashUiStyles.content}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void refresh()} />}
      >
        <Pressable accessibilityRole="button" accessibilityLabel={t("devices.pickStore")} onPress={() => setPickerVisible(true)} style={styles.storeCard}>
          <MaterialCommunityIcons name="store-outline" size={26} color={HB_COLORS.action} />
          <View style={styles.storeInfo}>
            <Text style={styles.muted}>{t("devices.currentStore")}</Text>
            <Text variant="titleMedium" numberOfLines={1}>
              {activeStore ? activeStore.storeName || activeStore.storeCode : t("devices.pickStore")}
            </Text>
          </View>
          <View style={styles.switchStore}>
            <Text style={styles.switchStoreText}>{t("devices.switchStore")}</Text>
            <MaterialCommunityIcons name="chevron-right" size={18} color={HB_COLORS.action} />
          </View>
        </Pressable>

        {!storeCode ? <Text style={styles.empty}>{t("states.pickStoreFirst")}</Text> : null}
        {storeCode && devicesQuery.isPending ? <LoadingState /> : null}
        {storeCode && devicesQuery.isError ? (
          <MessageState
            text={resolvePosSettingsErrorMessage(devicesQuery.error, { t, language })}
            actionLabel={t("common:actions.retry")}
            onAction={() => void devicesQuery.refetch()}
          />
        ) : null}
        {storeCode && devicesQuery.isSuccess && devices.length === 0 ? <Text style={styles.empty}>{t("states.noDevices")}</Text> : null}
        {devices.map((device) => (
          <DeviceRow key={device.deviceCode} device={device} onPress={() => router.push(buildPosSettingsDeviceHref(device.deviceCode) as Href)} />
        ))}
      </ScrollView>
      <StorePickerModal
        visible={pickerVisible}
        presentation="sheet"
        stores={pickerStores}
        selectedStoreCode={storeCode || null}
        title={t("common:labels.selectStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setPickerVisible(false)}
        onSelectStore={(picked) => {
          setPickerVisible(false);
          if (picked) setPickedStoreCode(picked.storeCode);
        }}
      />
    </>
  );
}

function DeviceRow({ device, onPress }: { device: PosSettingDeviceStatus; onPress: () => void }) {
  const { t } = useAppTranslation("posSettings");
  const version = device.appVersion ? t("devices.version", { version: device.appVersion }) : t("devices.unknownVersion");
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={device.deviceCode} onPress={onPress} style={styles.row}>
      <View style={styles.rowTop}>
        <View style={styles.rowName}>
          <Text variant="titleSmall" style={styles.deviceCode} numberOfLines={1}>{device.deviceCode}</Text>
          <Text style={styles.muted}>{t(`clients.${device.clientKind}`)}</Text>
        </View>
        <DeviceStatusChip status={device.status} />
        <MaterialCommunityIcons name="chevron-right" size={20} color={HB_COLORS.textSecondary} />
      </View>
      <Text style={styles.muted}>{version}</Text>
      <DeviceStatusHint status={device.status} />
      {device.hasDeviceOverride || device.offlineOverrideCount > 0 ? (
        <View style={cashUiStyles.rowWrap}>
          {device.hasDeviceOverride ? <StatusChip tone="info" label={t("devices.hasOverride")} /> : null}
          {device.offlineOverrideCount > 0 ? (
            <StatusChip tone="warning" label={t("devices.offlineCount", { count: device.offlineOverrideCount })} />
          ) : null}
        </View>
      ) : null}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  muted: { color: HB_COLORS.textSecondary, fontSize: 13 },
  empty: { color: HB_COLORS.textSecondary, textAlign: "center", padding: HB_SPACING.lg },
  storeCard: {
    backgroundColor: HB_COLORS.white,
    borderRadius: HB_RADIUS.sheet,
    paddingHorizontal: HB_SPACING.md,
    paddingVertical: HB_SPACING.sm,
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.sm,
    minHeight: 64,
  },
  storeInfo: { flex: 1, minWidth: 0 },
  switchStore: { flexDirection: "row", alignItems: "center", minHeight: 44, paddingLeft: HB_SPACING.xxs },
  switchStoreText: { color: HB_COLORS.action, fontWeight: "600", fontSize: 14 },
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
  deviceCode: { color: HB_COLORS.textPrimary, fontWeight: "700", flexShrink: 1 },
});
