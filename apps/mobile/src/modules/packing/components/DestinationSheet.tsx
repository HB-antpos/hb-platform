import { useEffect, useMemo, useState } from "react";
import { Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { createCarrier, fetchCarriers, fetchDestinations } from "../api";
import { packingErrorMessage } from "../packing-errors";
import { destinationFormError, filterDestinationStores, sameCode, splitDestinations } from "../packing-logic";
import { DESTINATION_TYPE, WAREHOUSE_OWNER_CODE, type DestinationFormValue, type PackCarrier } from "../types";
import { PACK_COLORS, SectionTitle, Segmented, packStyles } from "./pack-ui";

type DestinationTab = "local" | "store" | "warehouse" | "none";

function tabOf(type: number): DestinationTab {
  return type === DESTINATION_TYPE.local
    ? "local"
    : type === DESTINATION_TYPE.store
      ? "store"
      : type === DESTINATION_TYPE.warehouse
        ? "warehouse"
        : "none";
}

const TAB_TYPE: Record<DestinationTab, number> = {
  local: DESTINATION_TYPE.local,
  store: DESTINATION_TYPE.store,
  warehouse: DESTINATION_TYPE.warehouse,
  none: DESTINATION_TYPE.unassigned,
};

/**
 * 选择去向（箱和托盘共用）：本店存放 / 其他分店 / 总仓，可先不选。
 * 去其他分店或总仓时再选「怎么送」：自己送 / 对方来取，或交运输公司（可现场新增）。
 * 其他分店、总仓的候选都来自 `GET packing/destinations`（店员也能调）：返回里有总仓项才显示「总仓」一项，
 * 总仓作为所属地点时接口不返回总仓，「本店存放」叫「仓内存放」。
 */
export function DestinationSheet({
  visible,
  kind,
  ownerCode,
  title,
  confirmLabel,
  initial,
  saving,
  errorMessage,
  onDismiss,
  onConfirm,
}: {
  visible: boolean;
  kind: "carton" | "pallet";
  /** 当前箱 / 托盘所属的地点编码：用来取候选地点（接口已排除它自己），总仓所属时「本店存放」叫「仓内存放」。 */
  ownerCode: string;
  title: string;
  confirmLabel: string;
  initial: DestinationFormValue;
  saving: boolean;
  errorMessage?: string | null;
  onDismiss: () => void;
  onConfirm: (value: DestinationFormValue) => void;
}) {
  const { t, language } = useAppTranslation(["packing", "common"]);
  const queryClient = useQueryClient();
  const ownerIsWarehouse = sameCode(ownerCode, WAREHOUSE_OWNER_CODE);
  const [value, setValue] = useState<DestinationFormValue>(initial);
  const [viaCarrier, setViaCarrier] = useState(Boolean(initial.carrierGuid));
  const [storeKeyword, setStoreKeyword] = useState("");
  const [carrierName, setCarrierName] = useState("");
  const [addingCarrier, setAddingCarrier] = useState(false);
  const [carrierError, setCarrierError] = useState("");
  const [touched, setTouched] = useState(false);

  // 每次打开都从调用方的现值重新开始，中途关闭不保留草稿。
  useEffect(() => {
    if (!visible) return;
    setValue(initial);
    setViaCarrier(Boolean(initial.carrierGuid));
    setStoreKeyword("");
    setCarrierName("");
    setCarrierError("");
    setTouched(false);
    // 只在打开的那一刻同步；initial 是调用方每次渲染新建的对象，不能放进依赖。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible]);

  const tab = tabOf(value.destinationType);
  const shipping = tab === "store" || tab === "warehouse";

  const destinationsQuery = useQuery({
    queryKey: ["packing", "destinations", ownerCode],
    queryFn: ({ signal }) => fetchDestinations(ownerCode, signal),
    enabled: visible && Boolean(ownerCode),
    staleTime: 5 * 60_000,
  });
  const { warehouse: warehouseOption, stores: allStores } = useMemo(
    () => splitDestinations(destinationsQuery.data ?? []),
    [destinationsQuery.data],
  );
  const stores = useMemo(() => filterDestinationStores(allStores, storeKeyword), [allStores, storeKeyword]);
  // 候选还在加载时，已选的总仓不能凭空消失。
  const showWarehouse =
    Boolean(warehouseOption) || (destinationsQuery.isLoading && value.destinationType === DESTINATION_TYPE.warehouse);

  const carriersQuery = useQuery({
    queryKey: ["packing", "carriers"],
    queryFn: ({ signal }) => fetchCarriers(signal),
    enabled: visible && shipping && viaCarrier,
    staleTime: 60_000,
  });

  const formError = destinationFormError(value, viaCarrier);
  const patch = (next: Partial<DestinationFormValue>) => setValue((current) => ({ ...current, ...next }));

  const addCarrier = async () => {
    const name = carrierName.trim();
    if (!name || addingCarrier) return;
    setAddingCarrier(true);
    setCarrierError("");
    try {
      const carrier = await createCarrier(name);
      queryClient.setQueryData<PackCarrier[]>(["packing", "carriers"], (current) =>
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

  const submit = () => {
    setTouched(true);
    if (formError) return;
    onConfirm({
      ...value,
      // 本地存放 / 未指定不带收货地点；自己送 / 对方来取不带运输公司。
      code: value.destinationType === DESTINATION_TYPE.store ? value.code : null,
      carrierGuid: shipping && viaCarrier ? value.carrierGuid : null,
      carrierName: shipping && viaCarrier ? value.carrierName : null,
    });
  };

  const tabOptions = [
    { key: "local" as const, label: ownerIsWarehouse ? t("destination.typeLocalWarehouse") : t("destination.typeLocal") },
    { key: "store" as const, label: t("destination.typeStore") },
    ...(showWarehouse ? [{ key: "warehouse" as const, label: t("destination.typeWarehouse") }] : []),
  ];

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
          onPress={submit}
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
            // 切换去向类型时清掉不再适用的选择，避免提交时带着上一类的分店 / 公司。
            patch({
              destinationType: TAB_TYPE[next],
              ...(next === "store" ? {} : { code: null, name: next === "warehouse" ? warehouseOption?.name ?? null : null }),
            });
          }}
          options={tabOptions}
        />
        {tab === "none" ? <Text style={packStyles.muted}>{t("destination.noneHint")}</Text> : null}
        {tab === "local" ? (
          <Text style={packStyles.muted}>{ownerIsWarehouse ? t("destination.localWarehouseHint") : t("destination.localHint")}</Text>
        ) : null}
        {tab === "warehouse" ? <Text style={packStyles.muted}>{t("destination.warehouseHint")}</Text> : null}

        {tab === "store" ? (
          <>
            <SectionTitle title={t("destination.receivingStore")} />
            <TextInput
              accessibilityLabel={t("destination.searchStore")}
              placeholder={t("destination.searchStore")}
              placeholderTextColor={PACK_COLORS.inkMuted}
              value={storeKeyword}
              onChangeText={setStoreKeyword}
              autoCorrect={false}
              style={packStyles.textInput}
            />
            <View style={packStyles.cardList}>
              {destinationsQuery.isLoading ? <ActivityIndicator style={styles.inlineLoading} /> : null}
              {destinationsQuery.isError ? (
                <Pressable accessibilityRole="button" onPress={() => void destinationsQuery.refetch()} style={styles.clearRow}>
                  <Text style={styles.link}>{t("destination.loadFailedRetry")}</Text>
                </Pressable>
              ) : null}
              <ScrollView style={styles.storeList} nestedScrollEnabled keyboardShouldPersistTaps="handled">
                {stores.map((store, index) => {
                  const selected = sameCode(store.code, value.code);
                  return (
                    <Pressable
                      key={store.code}
                      accessibilityRole="radio"
                      accessibilityState={{ checked: selected }}
                      onPress={() => patch({ code: store.code, name: store.name })}
                      style={[styles.radioRow, index > 0 ? styles.divider : null]}
                    >
                      <MaterialCommunityIcons
                        name={selected ? "check-circle" : "circle-outline"}
                        size={22}
                        color={selected ? PACK_COLORS.action : PACK_COLORS.outline}
                      />
                      <Text numberOfLines={1} style={styles.radioText}>
                        {store.name}
                      </Text>
                    </Pressable>
                  );
                })}
              </ScrollView>
              {destinationsQuery.isSuccess && stores.length === 0 ? (
                <Text style={[packStyles.muted, styles.emptyHint]}>
                  {storeKeyword.trim() ? t("destination.noStoreMatch") : t("destination.noStores")}
                </Text>
              ) : null}
            </View>
          </>
        ) : null}

        {shipping ? (
          <>
            <SectionTitle title={t("destination.howToSend")} />
            <View style={packStyles.cardList}>
              <Pressable
                accessibilityRole="radio"
                accessibilityState={{ checked: !viaCarrier }}
                onPress={() => setViaCarrier(false)}
                style={styles.radioRow}
              >
                <MaterialCommunityIcons
                  name={!viaCarrier ? "check-circle" : "circle-outline"}
                  size={22}
                  color={!viaCarrier ? PACK_COLORS.action : PACK_COLORS.outline}
                />
                <Text style={styles.radioText}>{t("destination.selfDeliver")}</Text>
              </Pressable>
              <Pressable
                accessibilityRole="radio"
                accessibilityState={{ checked: viaCarrier }}
                onPress={() => setViaCarrier(true)}
                style={[styles.radioRow, styles.divider]}
              >
                <MaterialCommunityIcons
                  name={viaCarrier ? "check-circle" : "circle-outline"}
                  size={22}
                  color={viaCarrier ? PACK_COLORS.action : PACK_COLORS.outline}
                />
                <Text style={styles.radioText}>{t("destination.viaCarrier")}</Text>
                {viaCarrier && value.carrierName ? (
                  <Text numberOfLines={1} style={packStyles.muted}>
                    {value.carrierName}
                  </Text>
                ) : null}
              </Pressable>
            </View>

            {viaCarrier ? (
              <>
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
              </>
            ) : null}
            <View style={styles.note}>
              <MaterialCommunityIcons name="information-outline" size={16} color={PACK_COLORS.action} />
              <Text style={styles.noteText}>{t("destination.receiveNote")}</Text>
            </View>
          </>
        ) : null}

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

        {tab !== "none" ? (
          <Pressable
            accessibilityRole="button"
            onPress={() => {
              patch({ destinationType: DESTINATION_TYPE.unassigned, code: null, name: null });
              setViaCarrier(false);
            }}
            style={styles.clearRow}
          >
            <Text style={styles.link}>{t("destination.clearDestination")}</Text>
          </Pressable>
        ) : null}

        {touched && formError ? <Text style={packStyles.errorText}>{t(`destination.${formError}`)}</Text> : null}
        {errorMessage ? <Text style={packStyles.errorText}>{errorMessage}</Text> : null}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  radioRow: { minHeight: 48, flexDirection: "row", alignItems: "center", gap: 8 },
  radioText: { flex: 1, minWidth: 0, fontSize: 15, fontWeight: "600", color: PACK_COLORS.ink },
  divider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  storeList: { maxHeight: 240 },
  addRow: { minHeight: 48, flexDirection: "row", alignItems: "center", gap: 8 },
  addInput: { flex: 1, minWidth: 0, fontSize: 15, color: PACK_COLORS.ink, paddingVertical: 0 },
  clearRow: { minHeight: 44, justifyContent: "center" },
  link: { fontSize: 14, fontWeight: "600", color: PACK_COLORS.action },
  inlineLoading: { marginVertical: 12 },
  emptyHint: { paddingVertical: 12 },
  note: { flexDirection: "row", gap: 7, padding: 10, borderRadius: 8, backgroundColor: PACK_COLORS.neutralChipBg },
  noteText: { flex: 1, fontSize: 12.5, lineHeight: 19, color: PACK_COLORS.textSecondary },
});
