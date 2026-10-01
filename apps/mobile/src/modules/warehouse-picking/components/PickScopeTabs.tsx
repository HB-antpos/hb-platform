import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { PickScope } from "../types";
import { PICK_COLORS } from "./pick-theme";

const SCOPE_LABEL_KEYS: Record<PickScope, string> = {
  mine: "picking.scopeMine",
  all: "picking.scopeAll",
  located: "picking.scopeLocated",
  unlocated: "picking.scopeUnlocated",
};

/** 有拣货分配的订单页头只放“我的 / 全部”；有货位 / 无货位收进全部明细。 */
export const ASSIGNED_HEADER_SCOPES: PickScope[] = ["mine", "all"];
export const ASSIGNED_SHEET_SCOPES: PickScope[] = ["mine", "all", "located", "unlocated"];
export const DEFAULT_SCOPES: PickScope[] = ["all", "located", "unlocated"];

/** 拣货范围分段（各带行数）：没有分配时是全部 / 有货位 / 无货位，有分配时页头是我的 / 全部。 */
export function PickScopeTabs({
  value,
  counts,
  scopes = DEFAULT_SCOPES,
  onChange,
}: {
  value: PickScope;
  counts: Record<PickScope, number>;
  scopes?: PickScope[];
  onChange: (scope: PickScope) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  return (
    <View accessibilityRole="tablist" accessibilityLabel={t("picking.scopeLabel")} style={styles.track}>
      {scopes.map((scope) => {
        const selected = scope === value;
        return (
          <Pressable
            key={scope}
            accessibilityRole="tab"
            accessibilityState={{ selected }}
            onPress={() => onChange(scope)}
            style={[styles.tab, selected ? styles.tabOn : null]}
          >
            <Text numberOfLines={1} style={[styles.label, selected ? styles.labelOn : null]}>
              {t(SCOPE_LABEL_KEYS[scope])} <Text style={[styles.count, selected ? styles.countOn : null]}>{counts[scope]}</Text>
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
