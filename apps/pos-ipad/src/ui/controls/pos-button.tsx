import { MaterialCommunityIcons } from "@expo/vector-icons";
import { forwardRef } from "react";
import {
  ActivityIndicator,
  Text,
  type StyleProp,
  type TextStyle,
  type View,
  type ViewStyle,
} from "react-native";

import { PosPressable, type PosPressableProps } from "./pos-pressable";

import { createPosStyles, posColors, posRadius, posTouch } from "@/ui/theme";

export type PosIconName = keyof typeof MaterialCommunityIcons.glyphMap;

/**
 * primary：每屏唯一的主操作（实底主操作色）。
 * secondary：常规操作（白底强描边）。quiet：次要操作（白底浅描边）。
 * danger / success / info / warning：带语义色的浅底按钮。inverse：深色实底。
 */
export type PosButtonTone =
  | "primary"
  | "secondary"
  | "quiet"
  | "danger"
  | "success"
  | "info"
  | "warning"
  | "inverse";

/** 对应 posTouch 的高度：primary 88 / large 72 / button 64 / medium 56 / small 48。 */
export type PosButtonSize = "primary" | "large" | "button" | "medium" | "small";

export type PosButtonProps = Omit<PosPressableProps, "children" | "style"> &
  Readonly<{
    label: string;
    icon?: PosIconName;
    /** 图标放在文字后面（例如展开箭头）。 */
    trailingIcon?: PosIconName;
    tone?: PosButtonTone;
    size?: PosButtonSize;
    /** 显示转圈并禁用点击；文字保持不变，由调用方决定是否换成「处理中…」。 */
    loading?: boolean;
    /** 占满父容器剩余宽度（flex: 1）。 */
    fill?: boolean;
    style?: StyleProp<ViewStyle>;
    labelStyle?: StyleProp<TextStyle>;
    labelTestID?: string;
    numberOfLines?: number;
  }>;

const HEIGHT: Record<PosButtonSize, number> = {
  primary: posTouch.primary,
  large: posTouch.large,
  button: posTouch.button,
  medium: posTouch.medium,
  small: posTouch.small,
};
const FONT: Record<PosButtonSize, number> = {
  primary: 26,
  large: 21,
  button: 18,
  medium: 16,
  small: 15,
};
const ICON: Record<PosButtonSize, number> = {
  primary: 32,
  large: 26,
  button: 24,
  medium: 22,
  small: 20,
};

/** 统一尺寸与色调的 POS 按钮；触控音沿用 PosPressable。 */
export const PosButton = forwardRef<View, PosButtonProps>(function PosButton(
  {
    accessibilityLabel,
    disabled = false,
    fill = false,
    icon,
    label,
    labelStyle,
    labelTestID,
    loading = false,
    numberOfLines = 1,
    size = "button",
    style,
    tone = "secondary",
    trailingIcon,
    ...pressableProps
  },
  ref,
) {
  const inactive = disabled || loading;
  const foreground = inactive ? posColors.disabledInk : foregroundColor(tone);

  return (
    <PosPressable
      accessibilityLabel={accessibilityLabel ?? label}
      accessibilityRole="button"
      accessibilityState={
        loading ? { busy: true, disabled: true } : { disabled: inactive }
      }
      {...pressableProps}
      disabled={inactive}
      ref={ref}
      style={({ pressed }) => [
        styles.base,
        { minHeight: HEIGHT[size] },
        size === "small" && styles.small,
        size === "primary" && styles.primarySize,
        toneStyle(tone),
        fill && styles.fill,
        inactive && styles.disabled,
        pressed && !inactive && styles.pressed,
        style,
      ]}
    >
      {loading ? (
        <ActivityIndicator color={foreground} />
      ) : icon ? (
        <MaterialCommunityIcons
          accessible={false}
          color={foreground}
          name={icon}
          size={ICON[size]}
        />
      ) : null}
      <Text
        adjustsFontSizeToFit
        minimumFontScale={0.8}
        numberOfLines={numberOfLines}
        style={[
          styles.label,
          { color: foreground, fontSize: FONT[size] },
          labelStyle,
        ]}
        testID={labelTestID}
      >
        {label}
      </Text>
      {trailingIcon ? (
        <MaterialCommunityIcons
          accessible={false}
          color={foreground}
          name={trailingIcon}
          size={ICON[size]}
        />
      ) : null}
    </PosPressable>
  );
});

function foregroundColor(tone: PosButtonTone): string {
  switch (tone) {
    case "primary":
      return posColors.onAccent;
    case "inverse":
      return posColors.onInverse;
    case "danger":
      return posColors.red;
    case "success":
      return posColors.green;
    case "info":
      return posColors.blue;
    case "warning":
      return posColors.yellowInk;
    case "secondary":
    case "quiet":
      return posColors.ink;
  }
}

function toneStyle(tone: PosButtonTone): ViewStyle {
  switch (tone) {
    case "primary":
      return styles.primary;
    case "inverse":
      return styles.inverse;
    case "danger":
      return styles.danger;
    case "success":
      return styles.success;
    case "info":
      return styles.info;
    case "warning":
      return styles.warning;
    case "quiet":
      return styles.quiet;
    case "secondary":
      return styles.secondary;
  }
}

const styles = createPosStyles((colors) => ({
  base: {
    alignItems: "center",
    borderRadius: posRadius.medium,
    borderWidth: 1.5,
    flexDirection: "row",
    gap: 8,
    justifyContent: "center",
    minWidth: posTouch.min,
    paddingHorizontal: 18,
  },
  danger: {
    backgroundColor: colors.redSoft,
    borderColor: colors.red,
  },
  disabled: {
    backgroundColor: colors.disabledSurface,
    borderBottomWidth: 1.5,
    borderColor: colors.disabledBorder,
  },
  fill: {
    flex: 1,
  },
  info: {
    backgroundColor: colors.blueSoft,
    borderColor: colors.blue,
  },
  inverse: {
    backgroundColor: colors.inverseSurface,
    borderColor: colors.inverseSurface,
  },
  label: {
    flexShrink: 1,
    fontWeight: "900",
    textAlign: "center",
  },
  pressed: {
    opacity: 0.72,
  },
  primary: {
    backgroundColor: colors.accent,
    // 底边加厚成按下去的「台阶」，在浅色底上把主按钮和其他按钮区分开。
    borderBottomColor: colors.accentPressed,
    borderBottomWidth: 4,
    borderColor: colors.accent,
  },
  primarySize: {
    borderRadius: posRadius.large,
    gap: 12,
  },
  quiet: {
    backgroundColor: colors.surface,
    borderColor: colors.border,
  },
  secondary: {
    backgroundColor: colors.surface,
    borderColor: colors.borderStrong,
  },
  small: {
    borderRadius: 9,
    paddingHorizontal: 14,
  },
  success: {
    backgroundColor: colors.greenSoft,
    borderColor: colors.greenBorder,
  },
  warning: {
    backgroundColor: colors.yellowSoft,
    borderColor: colors.yellow,
  },
}));
