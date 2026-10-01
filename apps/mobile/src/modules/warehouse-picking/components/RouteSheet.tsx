import { Pressable, StyleSheet, View } from "react-native";
import { Button, Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import Svg, { Circle, Path, Rect } from "react-native-svg";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import type { PickRoute } from "../types";
import { PICK_COLORS } from "./pick-theme";

/** 俯视示意：四排货架，M 型每排同一端进出（折返深度到最远要拣的货位），S 型蛇形穿过。 */
const ROUTE_PATHS: Record<PickRoute, { path: string; dots: [number, number][] }> = {
  m: {
    path: "M4 96H26V56Q29 50 32 56V96H56V26Q59 20 62 26V96H86V46Q89 40 92 46V96H116V20Q119 14 122 20V96H128",
    dots: [[29, 56], [59, 26], [89, 46], [119, 20]],
  },
  s: {
    path: "M29 98V6H59V92H89V6H119V98",
    dots: [[29, 60], [59, 30], [59, 72], [89, 50], [119, 24]],
  },
};

function RouteDiagram({ route, active }: { route: PickRoute; active: boolean }) {
  const color = active ? PICK_COLORS.action : PICK_COLORS.inkMuted;
  const { path, dots } = ROUTE_PATHS[route];
  return (
    <View style={styles.map}>
      <Svg width={112} height={88} viewBox="0 0 132 104">
        {[12, 42, 72, 102].map((x) => (
          <Rect key={x} x={x} y={10} width={10} height={76} rx={2} fill={PICK_COLORS.outline} />
        ))}
        <Path d={path} stroke={color} strokeWidth={2.5} fill="none" strokeLinecap="round" strokeLinejoin="round" />
        {dots.map(([cx, cy]) => (
          <Circle key={`${cx}-${cy}`} cx={cx} cy={cy} r={3} fill={color} />
        ))}
      </Svg>
    </View>
  );
}

/** 切换走位方式（本机偏好）：M 型默认，S 型可选；只改“接下来”和全部明细的顺序。 */
export function RouteSheet({
  visible,
  value,
  onChange,
  onDismiss,
}: {
  visible: boolean;
  value: PickRoute;
  onChange: (route: PickRoute) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const options: { route: PickRoute; title: string; description: string; isDefault: boolean }[] = [
    { route: "m", title: t("picking.routeM"), description: t("route.mDesc"), isDefault: true },
    { route: "s", title: t("picking.routeS"), description: t("route.sDesc"), isDefault: false },
  ];
  return (
    <BusinessSheet
      visible={visible}
      title={t("route.title")}
      subtitle={t("route.subtitle")}
      onDismiss={onDismiss}
      footer={
        <Button mode="contained" onPress={onDismiss} contentStyle={styles.buttonContent} style={styles.button}>
          {t("route.done")}
        </Button>
      }
    >
      <View accessibilityRole="radiogroup" style={styles.body}>
        {options.map((option) => {
          const selected = option.route === value;
          return (
            <Pressable
              key={option.route}
              accessibilityRole="radio"
              accessibilityState={{ checked: selected }}
              onPress={() => onChange(option.route)}
              style={[styles.option, selected ? styles.optionOn : null]}
            >
              <RouteDiagram route={option.route} active={selected} />
              <View style={styles.optionText}>
                <View style={styles.titleRow}>
                  <Text style={styles.optionTitle}>{option.title}</Text>
                  {option.isDefault ? <Text style={styles.tag}>{t("route.defaultTag")}</Text> : null}
                </View>
                <Text style={styles.optionDescription}>{option.description}</Text>
              </View>
              <MaterialCommunityIcons
                name={selected ? "radiobox-marked" : "radiobox-blank"}
                size={22}
                color={selected ? PICK_COLORS.action : PICK_COLORS.inkMuted}
              />
            </Pressable>
          );
        })}
        <Text style={styles.note}>{t("route.note")}</Text>
      </View>
    </BusinessSheet>
  );
}

const styles = StyleSheet.create({
  body: { gap: 10 },
  option: {
    flexDirection: "row",
    alignItems: "center",
    gap: 12,
    padding: 10,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: PICK_COLORS.outline,
    backgroundColor: PICK_COLORS.white,
  },
  optionOn: { borderWidth: 2, padding: 9, borderColor: PICK_COLORS.action, backgroundColor: "#F5F8FF" },
  map: {
    width: 120,
    height: 96,
    borderRadius: 8,
    borderWidth: 1,
    borderColor: PICK_COLORS.outlineMuted,
    backgroundColor: PICK_COLORS.cellBg,
    alignItems: "center",
    justifyContent: "center",
  },
  optionText: { flex: 1, minWidth: 0, gap: 3 },
  titleRow: { flexDirection: "row", alignItems: "center", gap: 6 },
  optionTitle: { fontSize: 15, lineHeight: 21, fontWeight: "700", color: PICK_COLORS.ink },
  tag: {
    fontSize: 11,
    lineHeight: 18,
    fontWeight: "600",
    paddingHorizontal: 6,
    borderRadius: 4,
    overflow: "hidden",
    backgroundColor: "#D1E0FF",
    color: PICK_COLORS.infoText,
  },
  optionDescription: { fontSize: 12, lineHeight: 17, color: PICK_COLORS.textSecondary },
  note: {
    fontSize: 12,
    lineHeight: 17,
    color: PICK_COLORS.textSecondary,
    backgroundColor: PICK_COLORS.cellBg,
    borderWidth: 1,
    borderColor: PICK_COLORS.outlineMuted,
    borderRadius: 8,
    padding: 10,
  },
  button: { borderRadius: 8 },
  buttonContent: { height: 48 },
});
