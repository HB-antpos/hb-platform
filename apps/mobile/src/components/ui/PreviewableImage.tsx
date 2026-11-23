import { Fragment, useEffect, useState } from "react";
import { Image, Pressable, StyleSheet, View, type StyleProp, type ViewStyle } from "react-native";
import { Icon } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";
import { ImagePreviewModal } from "./ImagePreviewModal";

export interface PreviewableImageProps {
  uri: string | null | undefined;
  size: number;
  /** 商品名：无障碍文案，也是大图预览底部标题。 */
  label: string;
  /** 大图预览底部副标题，例如「货号 XM2405 · 条码」。 */
  caption?: string | null;
  radius?: number;
  /** 没有图片或加载失败时的占位图标；传 null 则只显示灰色底。 */
  placeholderIcon?: string | null;
  style?: StyleProp<ViewStyle>;
}

/** 缩略图右下角的放大镜角标大小，随缩略图缩放，避免挡住商品。 */
function badgeSize(size: number) {
  if (size >= 56) return 20;
  if (size >= 44) return 18;
  return 16;
}

/**
 * 可点击放大的商品图：有图时右下角带放大镜角标，点一下打开全屏预览；
 * 没有图片或加载失败时显示占位，不可点、不显示角标。
 */
export function PreviewableImage({
  uri,
  size,
  label,
  caption,
  radius = HB_RADIUS.control,
  placeholderIcon = "image-outline",
  style,
}: PreviewableImageProps) {
  const { t } = useAppTranslation("common");
  const [failed, setFailed] = useState(false);
  const [open, setOpen] = useState(false);
  // 换了一张图就清掉上一张的失败标记。
  useEffect(() => setFailed(false), [uri]);

  const boxStyle = [styles.box, { width: size, height: size, borderRadius: radius }, style];
  if (!uri || failed) {
    return (
      <View style={boxStyle} accessible accessibilityRole="image" accessibilityLabel={label}>
        {placeholderIcon ? (
          <Icon source={placeholderIcon} size={Math.round(size * 0.38)} color="#98A2B3" />
        ) : null}
      </View>
    );
  }

  const badge = badgeSize(size);
  // 小图把点击热区向外扩到至少 44pt。
  const slop = Math.max(0, Math.ceil((44 - size) / 2));
  return (
    <Fragment>
      <Pressable
        accessibilityRole="imagebutton"
        accessibilityLabel={t("imagePreview.open", { name: label })}
        hitSlop={slop}
        onPress={() => setOpen(true)}
        style={boxStyle}
      >
        <Image source={{ uri }} style={styles.image} resizeMode="cover" onError={() => setFailed(true)} />
        <View style={[styles.badge, { width: badge, height: badge, borderRadius: badge / 2 }]} pointerEvents="none">
          <Icon source="magnify-plus-outline" size={Math.round(badge * 0.62)} color={HB_COLORS.white} />
        </View>
      </Pressable>
      {open ? <ImagePreviewModal uri={uri} title={label} subtitle={caption} onClose={() => setOpen(false)} /> : null}
    </Fragment>
  );
}

const styles = StyleSheet.create({
  box: {
    backgroundColor: HB_COLORS.surfaceMuted,
    alignItems: "center",
    justifyContent: "center",
    overflow: "hidden",
  },
  image: { width: "100%", height: "100%" },
  badge: {
    position: "absolute",
    right: 3,
    bottom: 3,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "rgba(16,24,40,0.66)",
  },
});
