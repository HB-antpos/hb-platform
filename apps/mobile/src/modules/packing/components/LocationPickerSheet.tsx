import { useEffect, useMemo, useState } from "react";
import { Pressable, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { createLocation, fetchLocations } from "../api";
import { packingErrorMessage, printErrorDetail } from "../packing-errors";
import { printLocationLabel } from "../pack-print";
import { sameCode } from "../packing-logic";
import { WAREHOUSE_OWNER_CODE, type PackLocation } from "../types";
import { PACK_COLORS, packStyles } from "./pack-ui";

/**
 * 选择存放位置（箱 / 托盘放位置、签收后放位置共用）：列表选择，或扫 / 输位置名称或位置条码后回车。
 * 分店位置由店长维护（可在这里新增并打印位置标签）；总仓列表就是现有货位，不在这里新增。
 */
export function LocationPickerSheet({
  visible,
  ownerCode,
  isManager,
  title,
  subtitle,
  selected,
  allowClear,
  saving,
  errorMessage,
  onDismiss,
  onSelect,
}: {
  visible: boolean;
  /** 位置属于哪个地点：当前箱 / 托盘的所属地点，签收时是收货地点。 */
  ownerCode: string;
  isManager: boolean;
  title: string;
  subtitle?: string;
  /** 当前已选位置名称。 */
  selected: string | null;
  /** 允许「不放位置 / 清除位置」。 */
  allowClear: boolean;
  saving: boolean;
  errorMessage?: string | null;
  onDismiss: () => void;
  /** 传位置名称（或用户扫 / 输的条码）；null 表示清除。 */
  onSelect: (code: string | null) => void;
}) {
  const { t, language } = useAppTranslation("packing");
  const queryClient = useQueryClient();
  const isWarehouse = sameCode(ownerCode, WAREHOUSE_OWNER_CODE);
  const [text, setText] = useState("");
  const [newName, setNewName] = useState("");
  const [adding, setAdding] = useState(false);
  const [printingGuid, setPrintingGuid] = useState<string | null>(null);
  const [localError, setLocalError] = useState("");

  useEffect(() => {
    if (!visible) return;
    setText("");
    setNewName("");
    setLocalError("");
  }, [visible]);

  const key = ["packing", "locations", ownerCode] as const;
  const query = useQuery({
    queryKey: key,
    queryFn: ({ signal }) => fetchLocations(ownerCode, signal),
    enabled: visible && Boolean(ownerCode),
    staleTime: 60_000,
  });

  const filtered = useMemo(() => {
    const keyword = text.trim().toLowerCase();
    const all = query.data ?? [];
    if (!keyword) return all;
    return all.filter((location) => location.name.toLowerCase().includes(keyword) || location.barcode.toLowerCase().includes(keyword));
  }, [query.data, text]);

  const submitText = () => {
    const code = text.trim();
    if (!code || saving) return;
    setLocalError("");
    // 完全匹配名称或条码就用列表里的名称；列表里没有（如总仓只取了前 500 个货位）也交给服务端校验。
    const match = (query.data ?? []).find((location) => sameCode(location.name, code) || sameCode(location.barcode, code));
    onSelect(match?.name ?? code);
  };

  const addLocation = async () => {
    const name = newName.trim();
    if (!name || adding) return;
    setAdding(true);
    setLocalError("");
    try {
      const created = await createLocation(ownerCode, name);
      queryClient.setQueryData<PackLocation[]>(key, (current) =>
        current?.some((item) => item.locationGuid === created.locationGuid) ? current : [...(current ?? []), created],
      );
      setNewName("");
    } catch (error) {
      setLocalError(packingErrorMessage(error, t, language));
    } finally {
      setAdding(false);
    }
  };

  const printLabel = async (location: PackLocation) => {
    if (printingGuid) return;
    setPrintingGuid(location.locationGuid);
    setLocalError("");
    try {
      await printLocationLabel(location);
    } catch (error) {
      setLocalError(t("location.printFailed", { detail: printErrorDetail(error) }));
    } finally {
      setPrintingGuid(null);
    }
  };

  const canManage = isManager && !isWarehouse;

  return (
    <BusinessSheet
      visible={visible}
      title={title}
      subtitle={subtitle}
      onDismiss={onDismiss}
      dismissable={!saving}
      footer={
        allowClear ? (
          <Button
            mode="outlined"
            disabled={saving}
            onPress={() => onSelect(null)}
            style={packStyles.button}
            contentStyle={packStyles.buttonContent}
          >
            {t("location.clear")}
          </Button>
        ) : undefined
      }
    >
      <View style={packStyles.sheetBody}>
        <View style={styles.searchRow}>
          <MaterialCommunityIcons name="barcode-scan" size={18} color={PACK_COLORS.textSecondary} />
          <TextInput
            accessibilityLabel={t("location.scanPlaceholder")}
            placeholder={t("location.scanPlaceholder")}
            placeholderTextColor={PACK_COLORS.inkMuted}
            value={text}
            onChangeText={setText}
            onSubmitEditing={submitText}
            autoCapitalize="characters"
            autoCorrect={false}
            autoFocus
            returnKeyType="done"
            style={styles.searchInput}
          />
        </View>

        <View style={packStyles.cardList}>
          {query.isLoading ? <ActivityIndicator style={styles.inlineLoading} /> : null}
          {query.isError ? (
            <Pressable accessibilityRole="button" onPress={() => void query.refetch()} style={styles.row}>
              <Text style={styles.link}>{t("actions.retry")}</Text>
            </Pressable>
          ) : null}
          {filtered.slice(0, 100).map((location, index) => {
            const isSelected = sameCode(location.name, selected);
            return (
              <View key={location.locationGuid} style={[styles.row, index > 0 ? styles.divider : null]}>
                <Pressable
                  accessibilityRole="radio"
                  accessibilityState={{ checked: isSelected }}
                  disabled={saving}
                  onPress={() => onSelect(location.name)}
                  style={styles.rowMain}
                >
                  <MaterialCommunityIcons
                    name={isSelected ? "check-circle" : "circle-outline"}
                    size={22}
                    color={isSelected ? PACK_COLORS.action : PACK_COLORS.outline}
                  />
                  <Text numberOfLines={1} style={styles.rowText}>
                    {location.name}
                  </Text>
                  {location.barcode ? (
                    <Text numberOfLines={1} style={styles.rowCode}>
                      {location.barcode}
                    </Text>
                  ) : null}
                </Pressable>
                {canManage ? (
                  <Pressable
                    accessibilityRole="button"
                    accessibilityLabel={t("location.printLabel", { name: location.name })}
                    disabled={Boolean(printingGuid)}
                    onPress={() => void printLabel(location)}
                    hitSlop={6}
                    style={styles.printButton}
                  >
                    {printingGuid === location.locationGuid ? (
                      <ActivityIndicator size={16} />
                    ) : (
                      <MaterialCommunityIcons name="printer-outline" size={20} color={PACK_COLORS.action} />
                    )}
                  </Pressable>
                ) : null}
              </View>
            );
          })}
          {query.isSuccess && filtered.length === 0 ? (
            <Text style={[packStyles.muted, styles.emptyHint]}>
              {text.trim() ? t("location.noMatch") : isWarehouse ? t("location.emptyWarehouse") : t("location.empty")}
            </Text>
          ) : null}
        </View>

        {canManage ? (
          <View style={styles.addBlock}>
            <Text style={styles.addTitle}>{t("location.addTitle")}</Text>
            <View style={styles.addRow}>
              <TextInput
                accessibilityLabel={t("location.newName")}
                placeholder={t("location.newName")}
                placeholderTextColor={PACK_COLORS.inkMuted}
                value={newName}
                onChangeText={setNewName}
                onSubmitEditing={() => void addLocation()}
                maxLength={50}
                style={[packStyles.textInput, styles.addInput]}
              />
              <Button mode="contained-tonal" loading={adding} disabled={!newName.trim() || adding} onPress={() => void addLocation()}>
                {t("location.add")}
              </Button>
            </View>
            <Text style={packStyles.muted}>{t("location.addHint")}</Text>
          </View>
        ) : null}

        {localError ? <Text style={packStyles.errorText}>{localError}</Text> : null}
        {errorMessage ? <Text style={packStyles.errorText}>{errorMessage}</Text> : null}
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  searchRow: {
    height: 48,
    flexDirection: "row",
    alignItems: "center",
    gap: 8,
    paddingHorizontal: 12,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: PACK_COLORS.outline,
    backgroundColor: PACK_COLORS.white,
  },
  searchInput: { flex: 1, minWidth: 0, fontSize: 16, color: PACK_COLORS.ink, paddingVertical: 0 },
  row: { minHeight: 48, flexDirection: "row", alignItems: "center", gap: 4 },
  rowMain: { flex: 1, minWidth: 0, minHeight: 48, flexDirection: "row", alignItems: "center", gap: 8 },
  divider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  rowText: { flexShrink: 1, fontSize: 15, fontWeight: "600", color: PACK_COLORS.ink },
  rowCode: { fontFamily: "monospace", fontSize: 12, color: PACK_COLORS.textSecondary },
  printButton: { width: 40, height: 40, alignItems: "center", justifyContent: "center" },
  link: { fontSize: 14, fontWeight: "600", color: PACK_COLORS.action },
  inlineLoading: { marginVertical: 12 },
  emptyHint: { paddingVertical: 12 },
  addBlock: { gap: 8 },
  addTitle: { fontSize: 13, fontWeight: "700", color: PACK_COLORS.textSecondary },
  addRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  addInput: { flex: 1 },
});
