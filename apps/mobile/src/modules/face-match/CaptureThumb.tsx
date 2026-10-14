// 抓拍列表左侧的 56×56 头像位（设计图 06 的 6 种状态）：
// 缩略图 / 灰色骨架 / 占位图标 /（命中行）红框 + 右下角盾牌角标 /（已过保留期）占位图标 + 底部「已删除」小条。
// Image 的来源只能是内存里的 data URI（thumbnail-loader 的状态），不是后端地址；这个文件是列表里唯一允许渲染 Image 的地方。
// 缩略图与整图一样属于生物识别信息：所有展示都由上层的「眼睛」开关、离开页面 / 切后台的清空逻辑控制，这里不做任何缓存。
import { Image, StyleSheet, View } from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS } from "@/shared/theme/tokens";
import type { ThumbDisplay } from "./capture-logic";
import { THUMBNAIL_SIZE } from "./constants";
import { FACE_HIT } from "./ui";

export function CaptureThumb({ display, hit }: { display: ThumbDisplay; hit: boolean }) {
  const { t } = useAppTranslation("faceMatch");
  return (
    // 头像只是装饰：不进无障碍树（卡片本身的文字已经读出时间、门店、判定与命中情况）。
    <View style={styles.box} accessibilityElementsHidden importantForAccessibility="no-hide-descendants">
      <View style={styles.clip}>
        {display.kind === "image" ? (
          <Image source={{ uri: display.uri }} resizeMode="cover" fadeDuration={0} style={styles.image} />
        ) : display.kind === "skeleton" ? (
          <View style={styles.skeleton} />
        ) : (
          <View style={styles.placeholder}>
            <MaterialCommunityIcons name="account" size={28} color={HB_COLORS.textSecondary} />
          </View>
        )}
        {display.kind === "placeholder" && display.deleted ? (
          <View style={styles.deletedBar}>
            <Text style={styles.deletedText} numberOfLines={1}>
              {t("thumb.deleted")}
            </Text>
          </View>
        ) : null}
      </View>
      {hit ? <View pointerEvents="none" style={styles.ring} /> : null}
      {hit ? (
        <View style={styles.shield}>
          <MaterialCommunityIcons name="shield-alert" size={13} color={HB_COLORS.white} />
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  box: { width: THUMBNAIL_SIZE, height: THUMBNAIL_SIZE },
  clip: { flex: 1, borderRadius: HB_RADIUS.control, overflow: "hidden", backgroundColor: HB_COLORS.surfaceMuted },
  image: { width: "100%", height: "100%" },
  skeleton: { flex: 1, backgroundColor: "#E9EDF2" },
  placeholder: { flex: 1, alignItems: "center", justifyContent: "center", backgroundColor: HB_COLORS.surfaceMuted },
  deletedBar: { position: "absolute", left: 0, right: 0, bottom: 0, paddingVertical: 1, backgroundColor: "rgba(16,24,40,0.62)" },
  deletedText: { fontSize: 10, lineHeight: 14, textAlign: "center", color: HB_COLORS.white },
  // 红框画在头像外圈 2px（设计稿是 box-shadow），不挤占头像本身的 56px。
  ring: { position: "absolute", top: -2, left: -2, right: -2, bottom: -2, borderRadius: 10, borderWidth: 2, borderColor: FACE_HIT.solid },
  shield: {
    position: "absolute",
    right: -1,
    bottom: -1,
    width: 20,
    height: 20,
    alignItems: "center",
    justifyContent: "center",
    borderTopLeftRadius: 10,
    borderBottomRightRadius: 8,
    backgroundColor: FACE_HIT.solid,
  },
});
