import type { ReactNode } from "react";
import { Pressable, StyleSheet, View } from "react-native";
import { Text } from "react-native-paper";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { MONO_FONT, PICK_COLORS } from "./pick-theme";
import { pickerInitials } from "../pick-view-model";

export function PickHeader({
  title,
  subtitle,
  monoTitle = false,
  onBack,
  backLabel,
  right,
  children,
}: {
  title: string;
  subtitle?: string | null;
  monoTitle?: boolean;
  onBack: () => void;
  backLabel: string;
  right?: ReactNode;
  children?: ReactNode;
}) {
  return (
    <View style={styles.header}>
      <View style={styles.row}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={backLabel}
          onPress={onBack}
          style={styles.back}
          hitSlop={4}
        >
          <MaterialCommunityIcons name="arrow-left" size={24} color={PICK_COLORS.ink} />
        </Pressable>
        <View style={styles.titleBlock}>
          <Text numberOfLines={1} style={[styles.title, monoTitle ? styles.mono : null]}>
            {title}
          </Text>
          {subtitle ? (
            <Text numberOfLines={1} style={styles.subtitle}>
              {subtitle}
            </Text>
          ) : null}
        </View>
        {right}
      </View>
      {children}
    </View>
  );
}

/** 当前拣货人胶囊：头像缩写 + 名字，一起拣时叠加同事头像与人数。 */
export function PickerChip({
  name,
  teammates = [],
  label,
  accessibilityLabel,
  onPress,
}: {
  name: string;
  teammates?: string[];
  label: string;
  accessibilityLabel: string;
  onPress: () => void;
}) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={accessibilityLabel} onPress={onPress} style={styles.chip}>
      <View style={styles.avatars}>
        <Avatar name={name} color={PICK_COLORS.action} />
        {teammates.slice(0, 1).map((teammate) => (
          <Avatar key={teammate} name={teammate} color={PICK_COLORS.textSecondary} overlap />
        ))}
      </View>
      <Text numberOfLines={1} style={styles.chipText}>
        {label}
      </Text>
    </Pressable>
  );
}

export function Avatar({ name, color, size = 30, overlap = false }: { name: string; color: string; size?: number; overlap?: boolean }) {
  return (
    <View
      style={[
        styles.avatar,
        { width: size, height: size, borderRadius: size / 2, backgroundColor: color },
        overlap ? styles.avatarOverlap : null,
      ]}
    >
      <Text style={[styles.avatarText, { fontSize: size >= 40 ? 15 : 11 }]}>{pickerInitials(name)}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  header: {
    backgroundColor: PICK_COLORS.white,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: PICK_COLORS.outlineMuted,
  },
  row: { minHeight: 52, flexDirection: "row", alignItems: "center", gap: 4, paddingLeft: 4, paddingRight: 8 },
  back: { width: 44, height: 44, borderRadius: 8, alignItems: "center", justifyContent: "center" },
  titleBlock: { flex: 1, minWidth: 0 },
  title: { fontSize: 17, lineHeight: 22, fontWeight: "700", color: PICK_COLORS.ink },
  mono: { fontFamily: MONO_FONT },
  subtitle: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary },
  chip: {
    minHeight: 44,
    maxWidth: 150,
    flexDirection: "row",
    alignItems: "center",
    gap: 6,
    paddingLeft: 5,
    paddingRight: 12,
    borderRadius: 22,
    borderWidth: 1,
    borderColor: PICK_COLORS.outline,
    backgroundColor: PICK_COLORS.white,
  },
  avatars: { flexDirection: "row" },
  chipText: { flexShrink: 1, fontSize: 13, fontWeight: "600", color: PICK_COLORS.ink },
  avatar: { alignItems: "center", justifyContent: "center", borderWidth: 2, borderColor: PICK_COLORS.white },
  avatarOverlap: { marginLeft: -8 },
  avatarText: { color: PICK_COLORS.white, fontWeight: "700" },
});
