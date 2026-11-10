import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text, View, type StyleProp, type ViewStyle } from "react-native";

import { PosPressable } from "@/ui/controls/pos-pressable";
import {
  createPosStyles,
  POS_THEME_IDS,
  POS_THEMES,
  posColors,
  posRadius,
  usePosTheme,
  type PosThemeId,
} from "@/ui/theme";
import { usePosThemeController } from "@/ui/theme-controller";

export type PosThemePickerProps = Readonly<{
  /** zh 开头显示中文名称，其余显示英文。 */
  language: string;
  style?: StyleProp<ViewStyle>;
  testID?: string;
}>;

/** 主题切换：每套主题一块色样，点一下立即生效。 */
export function PosThemePicker({
  language,
  style,
  testID = "pos-theme-picker",
}: PosThemePickerProps) {
  const activeTheme = usePosTheme();
  const { selectTheme } = usePosThemeController();
  const zh = language.toLowerCase().startsWith("zh");

  return (
    <View
      accessibilityRole="radiogroup"
      style={[styles.row, style]}
      testID={testID}
    >
      {POS_THEME_IDS.map((themeId) => {
        const theme = POS_THEMES[themeId];
        const selected = themeId === activeTheme.id;
        const label = zh ? theme.label.zh : theme.label.en;
        const description = zh ? theme.description.zh : theme.description.en;
        return (
          <PosPressable
            accessibilityHint={description}
            accessibilityLabel={label}
            accessibilityRole="radio"
            accessibilityState={{ checked: selected }}
            key={themeId}
            onPress={() => selectTheme(themeId)}
            style={({ pressed }) => [
              styles.option,
              selected && styles.optionSelected,
              pressed && styles.optionPressed,
            ]}
            testID={`${testID}-${themeId}`}
          >
            <ThemeSwatch themeId={themeId} />
            <View style={styles.copy}>
              <Text numberOfLines={1} style={styles.label}>
                {label}
              </Text>
              <Text numberOfLines={2} style={styles.description}>
                {description}
              </Text>
            </View>
            {selected ? (
              <MaterialCommunityIcons
                accessible={false}
                color={posColors.accent}
                name="check-circle"
                size={24}
              />
            ) : null}
          </PosPressable>
        );
      })}
    </View>
  );
}

/** 色样固定用各主题自己的颜色，不跟随当前主题，所以写内联样式而不是主题样式表。 */
function ThemeSwatch({ themeId }: Readonly<{ themeId: PosThemeId }>) {
  const palette = POS_THEMES[themeId].palette;
  return (
    <View
      accessible={false}
      style={[
        styles.swatch,
        { backgroundColor: palette.canvas, borderColor: palette.border },
      ]}
    >
      <View style={[styles.swatchChrome, { backgroundColor: palette.chrome }]} />
      <View style={styles.swatchBody}>
        <View
          style={[
            styles.swatchCard,
            { backgroundColor: palette.surface, borderColor: palette.border },
          ]}
        />
        <View
          style={[styles.swatchAccent, { backgroundColor: palette.accent }]}
        />
      </View>
    </View>
  );
}

const styles = createPosStyles((colors) => ({
  copy: {
    flex: 1,
    minWidth: 0,
  },
  description: {
    color: colors.mutedInk,
    fontSize: 12,
    fontWeight: "600",
    lineHeight: 16,
    marginTop: 2,
  },
  label: {
    color: colors.ink,
    fontSize: 16,
    fontWeight: "900",
  },
  option: {
    alignItems: "center",
    backgroundColor: colors.surface,
    borderColor: colors.border,
    borderRadius: posRadius.large,
    borderWidth: 1.5,
    flexBasis: "47%",
    flexDirection: "row",
    flexGrow: 1,
    gap: 12,
    minHeight: 76,
    paddingHorizontal: 12,
    paddingVertical: 8,
  },
  optionPressed: {
    opacity: 0.72,
  },
  optionSelected: {
    backgroundColor: colors.selectedSurface,
    borderColor: colors.accent,
    borderWidth: 2.5,
    paddingHorizontal: 11,
  },
  row: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  swatch: {
    borderRadius: 8,
    borderWidth: 1,
    height: 52,
    overflow: "hidden",
    width: 68,
  },
  swatchAccent: {
    borderRadius: 4,
    height: 14,
    width: 22,
  },
  swatchBody: {
    alignItems: "flex-end",
    flex: 1,
    flexDirection: "row",
    gap: 4,
    padding: 5,
  },
  swatchCard: {
    borderRadius: 4,
    borderWidth: 1,
    flex: 1,
    height: 26,
  },
  swatchChrome: {
    height: 10,
  },
}));
