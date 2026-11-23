import { Fragment, useEffect, useRef, useState } from "react";
import {
  Image,
  Modal,
  Pressable,
  StyleSheet,
  View,
  useWindowDimensions,
  type StyleProp,
  type ViewStyle,
} from "react-native";
import { Icon, Text } from "react-native-paper";
import { claimImageTooltip, markImageTooltipSeen } from "@/modules/image-preview/preview-hint";
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
  /**
   * 主商品图传 true：每台设备第一次看到时，在图下方弹一次「点图片可全屏查看」气泡，点任意处消失。
   * 列表里的小缩略图不要传，避免同屏出现一排气泡。
   */
  introHint?: boolean;
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
  introHint = false,
  style,
}: PreviewableImageProps) {
  const { t } = useAppTranslation("common");
  const { width: windowWidth } = useWindowDimensions();
  const [failed, setFailed] = useState(false);
  const [open, setOpen] = useState(false);
  const [bubble, setBubble] = useState<{ x: number; y: number; h: number } | null>(null);
  const anchor = useRef<View>(null);
  // 换了一张图就清掉上一张的失败标记。
  useEffect(() => setFailed(false), [uri]);

  const canPreview = Boolean(uri) && !failed;
  // 首次引导气泡：先到先得地领取本次启动唯一的名额，等布局稳定后量出图片在屏幕上的位置再弹出。
  useEffect(() => {
    if (!introHint || !canPreview) return;
    let alive = true;
    let timer: ReturnType<typeof setTimeout> | null = null;
    void claimImageTooltip().then((allowed) => {
      if (!alive || !allowed) return;
      timer = setTimeout(() => {
        anchor.current?.measureInWindow((x, y, _w, h) => {
          if (alive && h > 0) setBubble({ x, y, h });
        });
      }, 600);
    });
    return () => {
      alive = false;
      if (timer) clearTimeout(timer);
    };
  }, [canPreview, introHint]);
  const dismissBubble = () => {
    setBubble(null);
    void markImageTooltipSeen();
  };

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
        ref={anchor}
        collapsable={false}
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
      {bubble ? (
        <Modal visible transparent animationType="fade" statusBarTranslucent onRequestClose={dismissBubble}>
          <Pressable style={StyleSheet.absoluteFill} onPress={dismissBubble} accessibilityLabel={t("imagePreview.tooltipDismiss")}>
            <View
              style={[
                styles.bubble,
                // 贴着图片左边缘，右侧留 12pt 安全边距；箭头对准图片中心。
                { top: bubble.y + bubble.h + 10, left: Math.min(Math.max(bubble.x, 12), Math.max(12, windowWidth - 232)) },
              ]}
            >
              <View style={[styles.arrow, { left: Math.max(14, bubble.x + size / 2 - Math.min(Math.max(bubble.x, 12), Math.max(12, windowWidth - 232)) - 6) }]} />
              <Text style={styles.bubbleText}>{t("imagePreview.tooltip")}</Text>
              <Text style={styles.bubbleAction}>{t("imagePreview.tooltipDismiss")}</Text>
            </View>
          </Pressable>
        </Modal>
      ) : null}
      {open ? <ImagePreviewModal uri={uri} title={label} subtitle={caption} onClose={() => setOpen(false)} /> : null}
    </Fragment>
  );
}

const styles = StyleSheet.create({
  bubble: {
    position: "absolute",
    maxWidth: 220,
    flexDirection: "row",
    alignItems: "center",
    gap: 10,
    paddingHorizontal: 12,
    paddingVertical: 9,
    borderRadius: 8,
    backgroundColor: "#101828",
  },
  arrow: {
    position: "absolute",
    top: -6,
    width: 0,
    height: 0,
    borderLeftWidth: 6,
    borderRightWidth: 6,
    borderBottomWidth: 6,
    borderLeftColor: "transparent",
    borderRightColor: "transparent",
    borderBottomColor: "#101828",
  },
  bubbleText: { flexShrink: 1, color: HB_COLORS.white, fontSize: 12.5, fontWeight: "600" },
  bubbleAction: { color: "#9CC2FF", fontSize: 12.5, fontWeight: "600" },
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
