import { memo, useState } from "react";
import { Image, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";
import { CONTAINER_DETAIL_IMAGE_SIZE } from "./container-detail-table-columns";
import { hasContainerDetailImageFailed, rememberContainerDetailImageFailure } from "./container-detail-image-failures";

/** 占位图标尺寸：约为图片框的一半。 */
const PLACEHOLDER_ICON_SIZE = CONTAINER_DETAIL_IMAGE_SIZE / 2;

/**
 * 货柜明细表格固定列里的商品缩略图（40×40）。
 *
 * - 有地址且没失败过：显示图片（contain，不裁切）；加载过程中只露出浅灰底，不先画占位图标，避免「图标 → 图片」闪一下。
 * - 没有地址 / 加载失败：显示同尺寸浅灰占位 + 灰色图标，不留空洞、不显示破图。
 * - 失败有两层记忆：组件内 state 保证同一行重渲染不重试；模块级按地址记忆（见 container-detail-image-failures）
 *   保证行滚出虚拟化窗口再回来、重新挂载时也不再请求同一个 404 地址。
 * - 图片不接收任何手势：外层槽位设了 pointerEvents="none"，行点击和勾选照常由行/勾选框处理。
 */
export const ContainerDetailThumb = memo(function ContainerDetailThumb({ uri, label }: { uri: string | null; label: string }) {
  const [failedUri, setFailedUri] = useState<string | null>(null);
  // 真正要请求的地址；为 null 就是占位（没地址 / 本次挂载内失败过 / 近期别处失败过）
  const imageUri = uri !== null && uri !== failedUri && !hasContainerDetailImageFailed(uri) ? uri : null;

  return (
    <View accessible accessibilityRole="image" accessibilityLabel={label} style={styles.frame}>
      {imageUri !== null ? (
        <Image
          source={{ uri: imageUri }}
          style={styles.image}
          resizeMode="contain"
          accessibilityIgnoresInvertColors
          onError={() => {
            rememberContainerDetailImageFailure(imageUri);
            setFailedUri(imageUri);
          }}
        />
      ) : (
        <MaterialCommunityIcons name="image-outline" size={PLACEHOLDER_ICON_SIZE} color={HB_COLORS.textSecondary} style={styles.placeholderIcon} />
      )}
    </View>
  );
});

const styles = StyleSheet.create({
  frame: {
    width: CONTAINER_DETAIL_IMAGE_SIZE,
    height: CONTAINER_DETAIL_IMAGE_SIZE,
    borderRadius: HB_RADIUS.control,
    backgroundColor: HB_COLORS.surfaceMuted,
    alignItems: "center",
    justifyContent: "center",
    overflow: "hidden",
  },
  image: { width: "100%", height: "100%" },
  // textSecondary 半透明叠在 surfaceMuted 上约等于浅灰，不引入新的硬编码颜色
  placeholderIcon: { opacity: 0.5 },
});
