import { useEffect, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { StorePickerModal } from "@/components/ui/StorePickerModal";
import { getAllStores } from "@/modules/shop/api";
import { useCartStore } from "@/store/cart-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { createCarrier, fetchCarriers, searchPackOrders } from "../api";
import { packingErrorMessage } from "../packing-errors";
import { destinationFormError } from "../packing-logic";
import { DESTINATION_TYPE, type DestinationFormValue, type PackCarrier } from "../types";
import { KeyValueRow, PACK_COLORS, SectionTitle, Segmented, packStyles } from "./pack-ui";

type DestinationTab = "store" | "carrier" | "none";

const TAB_TYPE: Record<DestinationTab, number> = {
  store: DESTINATION_TYPE.store,
  carrier: DESTINATION_TYPE.carrier,
  none: DESTINATION_TYPE.unassigned,
};

function tabOf(type: number): DestinationTab {
  return type === DESTINATION_TYPE.store ? "store" : type === DESTINATION_TYPE.carrier ? "carrier" : "none";
}

/**
 * 选择去向（箱和托盘共用）：分店 / 运输公司 / 暂不指定。
 * 运输公司可现场新增，并可再选最终收货分店；托盘可填运单号和备注；装箱时可关联订货单。
 */
export function DestinationSheet({
  visible,
  kind,
  title,
  confirmLabel,
  initial,
  showOrder = false,
  saving,
  errorMessage,
  onDismiss,
  onConfirm,
}: {
  visible: boolean;
  kind: "carton" | "pallet";
  title: string;
  confirmLabel: string;
  initial: DestinationFormValue;
  showOrder?: boolean;
  saving: boolean;
  errorMessage?: string | null;
  onDismiss: () => void;
  onConfirm: (value: DestinationFormValue) => void;
}) {
  const { t, language } = useAppTranslation(["warehousePacking", "common"]);
  const queryClient = useQueryClient();
  const userStores = useCartStore((state) => state.userStores);
  const [value, setValue] = useState<DestinationFormValue>(initial);
  const [storePickerVisible, setStorePickerVisible] = useState(false);
  const [carrierName, setCarrierName] = useState("");
  const [addingCarrier, setAddingCarrier] = useState(false);
  const [carrierError, setCarrierError] = useState("");
  const [orderKeyword, setOrderKeyword] = useState("");
  const [debouncedKeyword, setDebouncedKeyword] = useState("");
  const [touched, setTouched] = useState(false);

  // 每次打开都从调用方的现值重新开始，中途关闭不保留草稿。
  useEffect(() => {
    if (!visible) return;
    setValue(initial);
    setCarrierName("");
    setCarrierError("");
    setOrderKeyword("");
    setTouched(false);
    // 只在打开的那一刻同步；initial 是调用方每次渲染新建的对象，不能放进依赖。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible]);
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedKeyword(orderKeyword.trim()), 300);
    return () => clearTimeout(timer);
  }, [orderKeyword]);

  const tab = tabOf(value.destinationType);
  const storesQuery = useQuery({
    queryKey: ["warehousePacking", "stores"],
    queryFn: getAllStores,
    enabled: visible,
    staleTime: 5 * 60_000,
  });
  // 取不到全部分店（没有权限或网络失败）时退回本账号可见的分店。
  const stores = storesQuery.data?.length ? storesQuery.data : userStores;
  const carriersQuery = useQuery({
    queryKey: ["warehousePacking", "carriers"],
    queryFn: ({ signal }) => fetchCarriers(signal),
    enabled: visible && tab === "carrier",
    staleTime: 60_000,
  });
  const ordersQuery = useQuery({
    queryKey: ["warehousePacking", "orderOptions", debouncedKeyword],
    queryFn: ({ signal }) => searchPackOrders(debouncedKeyword, signal),
    enabled: visible && showOrder && !value.orderGuid,
    staleTime: 30_000,
  });

  const formError = destinationFormError(value);
  const patch = (next: Partial<DestinationFormValue>) => setValue((current) => ({ ...current, ...next }));

  const addCarrier = async () => {
    const name = carrierName.trim();
    if (!name || addingCarrier) return;
    setAddingCarrier(true);
    setCarrierError("");
    try {
      const carrier = await createCarrier(name);
      queryClient.setQueryData<PackCarrier[]>(["warehousePacking", "carriers"], (current) =>
        current?.some((item) => item.carrierGuid === carrier.carrierGuid) ? current : [...(current ?? []), carrier],
      );
      patch({ carrierGuid: carrier.carrierGuid, carrierName: carrier.name });
      setCarrierName("");
    } catch (error) {
      setCarrierError(packingErrorMessage(error, t, language));
    } finally {
      setAddingCarrier(false);
    }
  };

  const storeRow = (label: string, optional: boolean) => (
    <View style={packStyles.cardList}>
      <KeyValueRow
        first
        label={label}
        value={value.storeName || value.storeCode}
        placeholder={optional ? t("destination.optional") : t("destination.chooseStore")}
        onPress={() => setStorePickerVisible(true)}
      />
      {optional && value.storeCode ? (
        <Pressable
          accessibilityRole="button"
          onPress={() => patch({ storeCode: null, storeName: null })}
          style={styles.clearRow}
        >
          <Text style={styles.link}>{t("destination.clearStore")}</Text>
        </Pressable>
      ) : null}
    </View>
  );

  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={t("destination.subtitle")}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        <Button
          mode="contained"
          loading={saving}
          disabled={saving}
          onPress={() => {
            setTouched(true);
            if (!formError) onConfirm(value);
          }}
          style={packStyles.button}
          contentStyle={packStyles.buttonContent}
        >
          {confirmLabel}
        </Button>
      }
    >
      <View style={packStyles.sheetBody}>
        <Segmented
          label={t("destination.typeLabel")}
          value={tab}
          onChange={(next) => {
            // 切到分店 / 运输公司时保留已选分店；切到暂不指定只是不提交，不清草稿。
            patch({ destinationType: TAB_TYPE[next] });
          }}
          options={[
            { key: "store", label: t("destination.typeStore") },
            { key: "carrier", label: t("destination.typeCarrier") },
            { key: "none", label: t("destination.typeNone") },
          ]}
        />

        {tab === "store" ? storeRow(t("destination.store"), false) : null}

        {tab === "carrier" ? (
          <>
            <SectionTitle title={t("destination.carrier")} />
            <View style={packStyles.cardList}>
              {carriersQuery.isLoading ? <ActivityIndicator style={styles.inlineLoading} /> : null}
              {carriersQuery.isError ? (
                <Pressable accessibilityRole="button" onPress={() => void carriersQuery.refetch()} style={styles.clearRow}>
                  <Text style={styles.link}>{t("actions.retry")}</Text>
                </Pressable>
              ) : null}
              {(carriersQuery.data ?? []).map((carrier, index) => {
                const selected = carrier.carrierGuid === value.carrierGuid;
                return (
                  <Pressable
                    key={carrier.carrierGuid}
                    accessibilityRole="radio"
                    accessibilityState={{ checked: selected }}
                    onPress={() => patch({ carrierGuid: carrier.carrierGuid, carrierName: carrier.name })}
                    style={[styles.radioRow, index > 0 ? styles.divider : null]}
                  >
                    <MaterialCommunityIcons
                      name={selected ? "check-circle" : "circle-outline"}
                      size={22}
                      color={selected ? PACK_COLORS.action : PACK_COLORS.outline}
                    />
                    <Text numberOfLines={1} style={styles.radioText}>
                      {carrier.name}
                    </Text>
                    {carrier.remark ? (
                      <Text numberOfLines={1} style={packStyles.muted}>
                        {carrier.remark}
                      </Text>
                    ) : null}
                  </Pressable>
                );
              })}
              {carriersQuery.isSuccess && carriersQuery.data.length === 0 ? (
                <Text style={[packStyles.muted, styles.emptyHint]}>{t("destination.noCarriers")}</Text>
              ) : null}
              <View style={[styles.addRow, styles.divider]}>
                <TextInput
                  accessibilityLabel={t("destination.newCarrier")}
                  placeholder={t("destination.newCarrier")}
                  placeholderTextColor={PACK_COLORS.inkMuted}
                  value={carrierName}
                  onChangeText={setCarrierName}
                  onSubmitEditing={() => void addCarrier()}
                  maxLength={100}
                  style={styles.addInput}
                />
                <Button compact loading={addingCarrier} disabled={!carrierName.trim() || addingCarrier} onPress={() => void addCarrier()}>
                  {t("destination.addCarrier")}
                </Button>
              </View>
            </View>
            {carrierError ? <Text style={packStyles.errorText}>{carrierError}</Text> : null}
            <SectionTitle title={t("destination.finalReceiver")} />
            {storeRow(t("destination.finalStore"), true)}
          </>
        ) : null}

        {tab === "none" ? <Text style={packStyles.muted}>{t("destination.noneHint")}</Text> : null}

        {kind === "pallet" ? (
          <>
            <TextInput
              accessibilityLabel={t("destination.consignmentNo")}
              placeholder={t("destination.consignmentNo")}
              placeholderTextColor={PACK_COLORS.inkMuted}
              value={value.consignmentNo}
              onChangeText={(next) => patch({ consignmentNo: next })}
              autoCapitalize="characters"
              autoCorrect={false}
              maxLength={100}
              style={packStyles.textInput}
            />
            <TextInput
              accessibilityLabel={t("destination.remark")}
              placeholder={t("destination.remark")}
              placeholderTextColor={PACK_COLORS.inkMuted}
              value={value.remark}
              onChangeText={(next) => patch({ remark: next })}
              maxLength={200}
              style={packStyles.textInput}
            />
          </>
        ) : null}

        {showOrder ? (
          <>
            <SectionTitle title={t("destination.order")} />
            {value.orderGuid ? (
              <View style={packStyles.cardList}>
                <KeyValueRow first label={t("destination.orderSelected")} value={value.orderNo || "—"} />
                <Pressable
                  accessibilityRole="button"
                  onPress={() => patch({ orderGuid: null, orderNo: null })}
                  style={styles.clearRow}
                >
                  <Text style={styles.link}>{t("destination.clearOrder")}</Text>
                </Pressable>
              </View>
            ) : (
              <>
                <TextInput
                  accessibilityLabel={t("destination.orderSearch")}
                  placeholder={t("destination.orderSearch")}
                  placeholderTextColor={PACK_COLORS.inkMuted}
                  value={orderKeyword}
                  onChangeText={setOrderKeyword}
                  autoCapitalize="characters"
                  autoCorrect={false}
                  style={packStyles.textInput}
                />
                <View style={packStyles.cardList}>
                  {ordersQuery.isLoading ? <ActivityIndicator style={styles.inlineLoading} /> : null}
                  {(ordersQuery.data ?? []).slice(0, 8).map((order, index) => (
                    <Pressable
                      key={order.orderGuid}
                      accessibilityRole="button"
                      onPress={() =>
                        // 还没指定去向时，选订货单顺带把去向设成订货单的分店（与后端新建箱的默认一致）。
                        patch({
                          orderGuid: order.orderGuid,
                          orderNo: order.orderNo,
                          ...(value.destinationType === DESTINATION_TYPE.unassigned && order.storeCode
                            ? { destinationType: DESTINATION_TYPE.store, storeCode: order.storeCode, storeName: order.storeName }
                            : {}),
                        })
                      }
                      style={[styles.radioRow, index > 0 ? styles.divider : null]}
                    >
                      <Text numberOfLines={1} style={[styles.radioText, packStyles.mono]}>
                        {order.orderNo || "—"}
                      </Text>
                      <Text numberOfLines={1} style={packStyles.muted}>
                        {order.storeName || order.storeCode || ""}
                      </Text>
                    </Pressable>
                  ))}
                  {ordersQuery.isSuccess && ordersQuery.data.length === 0 ? (
                    <Text style={[packStyles.muted, styles.emptyHint]}>{t("destination.noOrders")}</Text>
                  ) : null}
                  {ordersQuery.isError ? (
                    <Text style={[packStyles.errorText, styles.emptyHint]}>{packingErrorMessage(ordersQuery.error, t, language)}</Text>
                  ) : null}
                </View>
              </>
            )}
          </>
        ) : null}

        {touched && formError ? <Text style={packStyles.errorText}>{t(`destination.${formError}`)}</Text> : null}
        {errorMessage ? <Text style={packStyles.errorText}>{errorMessage}</Text> : null}
      </View>

      {/* 本弹层是原生 Modal，分店选择必须也用原生 Modal 承载才能显示在最上层。 */}
      <StorePickerModal
        presentation="sheet"
        host="native-modal"
        visible={storePickerVisible}
        stores={stores}
        selectedStoreCode={value.storeCode}
        title={t("destination.chooseStore")}
        cancelLabel={t("common:actions.cancel")}
        onDismiss={() => setStorePickerVisible(false)}
        onSelectStore={(store) => {
          if (store) patch({ storeCode: store.storeCode, storeName: store.storeName });
          setStorePickerVisible(false);
        }}
      />
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  radioRow: { minHeight: 48, flexDirection: "row", alignItems: "center", gap: 8 },
  radioText: { flex: 1, minWidth: 0, fontSize: 15, fontWeight: "600", color: PACK_COLORS.ink },
  divider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  addRow: { minHeight: 48, flexDirection: "row", alignItems: "center", gap: 8 },
  addInput: { flex: 1, minWidth: 0, fontSize: 15, color: PACK_COLORS.ink, paddingVertical: 0 },
  clearRow: { minHeight: 44, justifyContent: "center" },
  link: { fontSize: 14, fontWeight: "600", color: PACK_COLORS.action },
  inlineLoading: { marginVertical: 12 },
  emptyHint: { paddingVertical: 12 },
});
