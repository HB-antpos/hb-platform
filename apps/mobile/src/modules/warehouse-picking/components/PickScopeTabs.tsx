import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { PickScope } from "../types";
import { PICK_COLORS } from "./pick-theme";

const SCOPES: { value: PickScope; labelKey: string }[] = [
  { value: "all", labelKey: "picking.scopeAll" },
  { value: "located", labelKey: "picking.scopeLocated" },
  { value: "unlocated", labelKey: "picking.scopeUnlocated" },
];

/** 拣货范围分段：全部 / 有货位 / 无货位，各带行数；两个人可以按范围分工拣同一单。 */
export function PickScopeTabs({
  value,
  counts,
  onChange,
}: {
  value: PickScope;
  counts: Record<PickScope, number>;
  onChange: (scope: PickScope) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  return (
    <View accessibilityRole="tablist" accessibilityLabel={t("picking.scopeLabel")} style={styles.track}>
      {SCOPES.map((scope) => {
        const selected = scope.value === value;
        return (
          <Pressable
            key={scope.value}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(scope.value)}
            style={[styles.tab, selected ? styles.tabOn : null]}
          >
            <Text numberOfLines={1} style={[styles.label, selected ? styles.labelOn : null]}>
              {t(scope.labelKey)} <Text style={[styles.count, selected ? styles.countOn : null]}>{counts[scope.value]}</Text>
            </Text>
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  track: { flexDirection: "row", gap: 4, padding: 3, borderRadius: 10, backgroundColor: PICK_COLORS.neutralChipBg },
  tab: { flex: 1, minHeight: 38, borderRadius: 8, alignItems: "center", justifyContent: "center", paddingHorizontal: 4 },
  tabOn: {
    backgroundColor: PICK_COLORS.white,
    shadowColor: PICK_COLORS.ink,
    shadowOpacity: 0.12,
    shadowRadius: 2,
    shadowOffset: { width: 0, height: 1 },
    elevation: 1,
  },
  label: { fontSize: 13, fontWeight: "600", color: PICK_COLORS.textSecondary },
  labelOn: { color: PICK_COLORS.ink },
  count: { fontSize: 12, fontWeight: "600", color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  countOn: { color: PICK_COLORS.action },
});
