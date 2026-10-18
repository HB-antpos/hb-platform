import { useCallback, useEffect, useState } from "react";
import { ScrollView, StyleSheet, View } from "react-native";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { acknowledgeStoreSupplyRestocked, getStoreSupplyWatches, unwatchStoreSupply } from "./api";
import { SupplyStatusCard } from "./supply-status-card";
import type { StoreSupplyStatus } from "./types";

interface SupplyWatchesSheetProps {
  visible: boolean;
  storeCode: string | null;
  onDismiss: () => void;
  /** 去订货：由首页按货号搜索。 */
  onOrder: (status: StoreSupplyStatus) => void;
  /** 关注状态变化后通知首页刷新汇总（角标、横幅）。 */
  onChanged?: () => void;
  /** 错误提示走首页的通知条，避免 Snackbar 被弹层压住。 */
  onError?: (message: string) => void;
}

/**
 * 我关注的商品：不新增 Tab 路由，从订货首页以弹层打开。
 * 已恢复订货的排最前，“知道了”只关闭已恢复的关注。确认步骤都在弹层内完成。
 */
export function SupplyWatchesSheet({ visible, storeCode, onDismiss, onOrder, onChanged, onError }: SupplyWatchesSheetProps) {
  const { t } = useAppTranslation("supplyNotice");
  const [items, setItems] = useState<StoreSupplyStatus[]>([]);
  const [loading, setLoading] = useState(false);
  const [busyCode, setBusyCode] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!storeCode) {
      setItems([]);
      return;
    }
    setLoading(true);
    try {
      setItems(await getStoreSupplyWatches(storeCode));
    } catch {
      onError?.(t("actionFailed"));
    } finally {
      setLoading(false);
    }
  }, [onError, storeCode, t]);

  useEffect(() => {
    if (visible) {
      void load();
    }
  }, [load, visible]);

  const runAction = async (productCode: string | null, action: () => Promise<unknown>) => {
    setBusyCode(productCode ?? "*");
    try {
      await action();
      await load();
      onChanged?.();
    } catch {
      onError?.(t("actionFailed"));
    } finally {
      setBusyCode(null);
    }
  };

  const restocked = items.filter((item) => item.isOrderable);
  const waiting = items.filter((item) => !item.isOrderable);

  return (
    <BusinessSheet visible={visible} title={t("watchesTitle")} onDismiss={onDismiss}>
      {loading && !items.length ? (
        <ActivityIndicator style={styles.loading} />
      ) : !items.length ? (
        <Text style={styles.empty}>{t("watchesEmpty")}</Text>
      ) : (
        <ScrollView contentContainerStyle={styles.list} keyboardShouldPersistTaps="handled">
          {restocked.length ? (
            <View style={styles.section}>
              <View style={styles.sectionHeader}>
                <Text variant="titleSmall">{t("restockedSection")} ({restocked.length})</Text>
                <Button compact mode="text" disabled={busyCode !== null} onPress={() => storeCode && void runAction(null, () => acknowledgeStoreSupplyRestocked(storeCode))}>
                  {t("acknowledgeAll")}
                </Button>
              </View>
              {restocked.map((status) => (
                <SupplyStatusCard
                  key={status.productCode}
                  status={status}
                  busy={busyCode === status.productCode}
                  onOrder={(item) => { onDismiss(); onOrder(item); }}
                  onAcknowledge={(item) => storeCode && void runAction(item.productCode, () => acknowledgeStoreSupplyRestocked(storeCode, [item.productCode]))}
                />
              ))}
            </View>
          ) : null}
          {waiting.length ? (
            <View style={styles.section}>
              <Text variant="titleSmall">{t("waitingSection")} ({waiting.length})</Text>
              {waiting.map((status) => (
                <SupplyStatusCard
                  key={status.productCode}
                  status={status}
                  busy={busyCode === status.productCode}
                  onUnwatch={(item) => storeCode && void runAction(item.productCode, () => unwatchStoreSupply(storeCode, item.productCode))}
                />
              ))}
            </View>
          ) : null}
        </ScrollView>
      )}
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  loading: { paddingVertical: HB_SPACING.xl },
  empty: { color: HB_COLORS.textSecondary, paddingVertical: HB_SPACING.lg, lineHeight: 20 },
  list: { gap: HB_SPACING.lg, paddingBottom: HB_SPACING.md },
  section: { gap: HB_SPACING.sm },
  sectionHeader: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
});
