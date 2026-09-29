import { useState } from "react";
import { Image, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { PICK_COLORS } from "./pick-theme";

/** 商品图：后端返回完整 URL；没有图或加载失败时显示占位，不让布局跳动。 */
export function ProductThumb({ uri, size = 56 }: { uri: string | null | undefined; size?: number }) {
  const [failed, setFailed] = useState(false);
  const box = { width: size, height: size, borderRadius: size >= 48 ? 8 : 6 };
  if (uri && !failed) {
    return (
      <Image
        source={{ uri }}
        style={[styles.image, box]}
        resizeMode="cover"
        onError={() => setFailed(true)}
        accessibilityIgnoresInvertColors
      />
    );
  }

  return (
    <View style={[styles.placeholder, box]}>
      <MaterialCommunityIcons name="image-outline" size={Math.round(size * 0.4)} color={PICK_COLORS.inkMuted} />
    </View>
  );
}

const styles = StyleSheet.create({
  image: { backgroundColor: PICK_COLORS.neutralChipBg },
  placeholder: { backgroundColor: PICK_COLORS.neutralChipBg, alignItems: "center", justifyContent: "center" },
});
