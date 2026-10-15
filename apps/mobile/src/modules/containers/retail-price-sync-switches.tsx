import { StyleSheet, View } from "react-native";
import { Switch, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";
import type { RetailPriceSyncChoice } from "./retail-price-sync";

/**
 * 货柜明细改零售价时的两个开关（编辑弹窗 / 批量设价弹窗共用）：同步分店零售价、生成价格任务。
 * 文案与仓库商品页的零售价确认弹窗一致，各带一行说明；是否展示由调用方按「本次是否会改仓库零售价」决定。
 */
export function ContainerRetailPriceSyncSwitches({
  value,
  onChange,
  disabled,
}: {
  value: RetailPriceSyncChoice;
  onChange: (next: RetailPriceSyncChoice) => void;
  disabled?: boolean;
}) {
  const { t } = useAppTranslation("containerDetail");
  return (
    <View style={styles.group}>
      <View style={styles.option}>
        <View style={styles.row}>
          <Text style={styles.label}>{t("retailSync.storesLabel")}</Text>
          <Switch
            accessibilityLabel={t("retailSync.storesLabel")}
            value={value.syncStoreRetailPrices}
            disabled={disabled}
            onValueChange={(next) => onChange({ ...value, syncStoreRetailPrices: next })}
          />
        </View>
        <Text style={styles.helper}>{t("retailSync.storesHelper")}</Text>
      </View>
      <View style={styles.option}>
        <View style={styles.row}>
          <Text style={styles.label}>{t("retailSync.tasksLabel")}</Text>
          <Switch
            accessibilityLabel={t("retailSync.tasksLabel")}
            value={value.generateTasks}
            disabled={disabled}
            onValueChange={(next) => onChange({ ...value, generateTasks: next })}
          />
        </View>
        <Text style={styles.helper}>{t("retailSync.tasksHelper")}</Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  group: { gap: 10 },
  option: { gap: 4 },
  row: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    minHeight: 44,
  },
  label: { flex: 1, paddingRight: 12, color: HB_COLORS.textPrimary, fontSize: 15 },
  helper: { color: HB_COLORS.textSecondary, fontSize: 12 },
});
