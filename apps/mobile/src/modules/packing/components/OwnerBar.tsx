import { useState, type ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { sameCode } from "../packing-logic";
import type { PackOwner } from "../types";
import { PACK_COLORS, packStyles } from "./pack-ui";

/**
 * 当前打包地点（分店 / 总仓）一行：账号只有一个地点时只显示名称，没有切换箭头；
 * 多个地点（店长管多家店、管理员）时点开切换，选择记在本机。
 */
export function OwnerBar({
  owners,
  owner,
  onChange,
  suffix,
  right,
}: {
  owners: readonly PackOwner[];
  owner: PackOwner | null;
  onChange?: (code: string) => void;
  /** 名称后面的说明，如「收货」。 */
  suffix?: string;
  right?: ReactNode;
}) {
  const { t } = useAppTranslation("packing");
  const [open, setOpen] = useState(false);
  const switchable = owners.length > 1 && Boolean(onChange);
  const icon = owner?.isWarehouse ? "warehouse" : "storefront-outline";

  const label = (
    <View style={styles.label}>
      <MaterialCommunityIcons name={icon} size={16} color={PACK_COLORS.action} />
      <Text numberOfLines={1} style={styles.name}>
        {owner?.name ?? "—"}
        {suffix ? ` ${suffix}` : ""}
      </Text>
      {switchable ? <MaterialCommunityIcons name="chevron-down" size={16} color={PACK_COLORS.action} /> : null}
    </View>
  );

  return (
    <View style={styles.bar}>
      {switchable ? (
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("owner.switch", { name: owner?.name ?? "" })}
          onPress={() => setOpen(true)}
          style={styles.press}
        >
          {label}
        </Pressable>
      ) : (
        <View style={styles.press}>{label}</View>
      )}
      <View style={styles.right}>{right}</View>

      <BusinessSheet visible={open} title={t("owner.title")} subtitle={t("owner.subtitle")} onDismiss={() => setOpen(false)}>
        <View style={packStyles.cardList}>
          {owners.map((item, index) => {
            const selected = sameCode(item.code, owner?.code);
            return (
              <Pressable
                key={item.code}
                accessibilityRole="radio"
                accessibilityState={{ checked: selected }}
                onPress={() => {
                  onChange?.(item.code);
                  setOpen(false);
                }}
                style={[styles.option, index > 0 ? styles.divider : null]}
              >
                <MaterialCommunityIcons
                  name={selected ? "check-circle" : "circle-outline"}
                  size={22}
                  color={selected ? PACK_COLORS.action : PACK_COLORS.outline}
                />
                <Text numberOfLines={1} style={styles.optionText}>
                  {item.name}
                </Text>
                {item.isWarehouse ? <Text style={packStyles.muted}>{t("owner.warehouse")}</Text> : null}
              </Pressable>
            );
          })}
        </View>
      </BusinessSheet>
    </View>
  );
}

const styles = StyleSheet.create({
  bar: { minHeight: 44, flexDirection: "row", alignItems: "center", gap: 8, paddingHorizontal: 12, paddingBottom: 6 },
  press: { flexShrink: 1, minHeight: 44, justifyContent: "center" },
  label: { flexDirection: "row", alignItems: "center", gap: 5 },
  name: { flexShrink: 1, fontSize: 13, lineHeight: 18, fontWeight: "700", color: PACK_COLORS.action },
  right: { flex: 1, alignItems: "flex-end" },
  option: { minHeight: 52, flexDirection: "row", alignItems: "center", gap: 8 },
  divider: { borderTopWidth: StyleSheet.hairlineWidth, borderTopColor: PACK_COLORS.outlineMuted },
  optionText: { flex: 1, minWidth: 0, fontSize: 15, fontWeight: "600", color: PACK_COLORS.ink },
});
