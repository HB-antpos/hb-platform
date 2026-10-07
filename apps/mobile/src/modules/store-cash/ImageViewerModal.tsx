// 全屏图片查看器：简单的 Modal + 横向分页，左右滑动翻页（没有现成 lightbox，也不新增原生依赖）。
import { useCallback, useEffect, useRef, useState } from "react";
import {
  FlatList,
  Image,
  Modal,
  Pressable,
  StyleSheet,
  useWindowDimensions,
  View,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from "react-native";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { resolveViewerPage, type ViewerImage } from "./attachments";

export function ImageViewerModal({
  visible,
  images,
  initialIndex,
  onClose,
  onImageError,
}: {
  visible: boolean;
  images: ViewerImage[];
  initialIndex: number;
  onClose: () => void;
  /** 某张图加载失败（签名地址过期等）时通知调用方重新取详情。 */
  onImageError?: (image: ViewerImage) => void;
}) {
  const { t } = useAppTranslation("storeCash");
  const insets = useSafeAreaInsets();
  const { width, height } = useWindowDimensions();
  const listRef = useRef<FlatList<ViewerImage>>(null);
  const [page, setPage] = useState(initialIndex);
  const [failedKeys, setFailedKeys] = useState<Set<string>>(new Set());

  useEffect(() => {
    if (visible) {
      setPage(Math.min(Math.max(initialIndex, 0), Math.max(images.length - 1, 0)));
      setFailedKeys(new Set());
    }
    // 只在打开时重置页码；打开期间 images 刷新（重新取了签名地址）不应把用户翻回第一页
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visible, initialIndex]);

  const goTo = useCallback(
    (next: number) => {
      const target = Math.min(Math.max(next, 0), images.length - 1);
      listRef.current?.scrollToIndex({ index: target, animated: true });
      setPage(target);
    },
    [images.length],
  );

  const onMomentumEnd = (event: NativeSyntheticEvent<NativeScrollEvent>) => {
    setPage(resolveViewerPage(event.nativeEvent.contentOffset.x, width, images.length));
  };

  if (!visible || images.length === 0) return null;

  return (
    <Modal visible animationType="fade" presentationStyle="fullScreen" statusBarTranslucent onRequestClose={onClose}>
      <View style={styles.root}>
        <FlatList
          ref={listRef}
          data={images}
          horizontal
          pagingEnabled
          showsHorizontalScrollIndicator={false}
          keyExtractor={(item) => item.key}
          initialScrollIndex={Math.min(Math.max(initialIndex, 0), images.length - 1)}
          getItemLayout={(_, index) => ({ length: width, offset: width * index, index })}
          onMomentumScrollEnd={onMomentumEnd}
          renderItem={({ item }) => (
            <View style={{ width, height }}>
              {failedKeys.has(item.key) ? (
                <View style={styles.failed}>
                  <MaterialCommunityIcons name="image-broken-variant" size={48} color="#98A2B3" />
                  <Text style={styles.failedText}>{t("viewer.loadFailed")}</Text>
                </View>
              ) : (
                <Image
                  source={{ uri: item.uri }}
                  style={styles.image}
                  resizeMode="contain"
                  onError={() => {
                    setFailedKeys((current) => new Set(current).add(item.key));
                    onImageError?.(item);
                  }}
                />
              )}
            </View>
          )}
        />
        <View style={[styles.topBar, { paddingTop: insets.top + 8 }]} pointerEvents="box-none">
          <Text style={styles.counter} accessibilityLiveRegion="polite">
            {t("viewer.page", { current: page + 1, total: images.length })}
          </Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("viewer.close")}
            onPress={onClose}
            hitSlop={8}
            style={styles.closeButton}
          >
            <MaterialCommunityIcons name="close" size={26} color="#FFFFFF" />
          </Pressable>
        </View>
        {images.length > 1 ? (
          <View style={[styles.pager, { paddingBottom: insets.bottom + 16 }]} pointerEvents="box-none">
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("viewer.previous")}
              disabled={page <= 0}
              onPress={() => goTo(page - 1)}
              style={[styles.pagerButton, page <= 0 ? styles.pagerDisabled : null]}
            >
              <MaterialCommunityIcons name="chevron-left" size={32} color="#FFFFFF" />
            </Pressable>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("viewer.next")}
              disabled={page >= images.length - 1}
              onPress={() => goTo(page + 1)}
              style={[styles.pagerButton, page >= images.length - 1 ? styles.pagerDisabled : null]}
            >
              <MaterialCommunityIcons name="chevron-right" size={32} color="#FFFFFF" />
            </Pressable>
          </View>
        ) : null}
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: "#000000" },
  image: { width: "100%", height: "100%" },
  failed: { flex: 1, alignItems: "center", justifyContent: "center", gap: 8 },
  failedText: { color: "#D0D5DD", fontSize: 14 },
  topBar: {
    position: "absolute",
    top: 0,
    left: 0,
    right: 0,
    paddingHorizontal: 16,
    paddingBottom: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    backgroundColor: "rgba(0,0,0,0.35)",
  },
  counter: { color: "#FFFFFF", fontSize: 16, fontWeight: "600", fontVariant: ["tabular-nums"] },
  closeButton: { width: 44, height: 44, alignItems: "center", justifyContent: "center" },
  pager: {
    position: "absolute",
    left: 0,
    right: 0,
    bottom: 0,
    paddingHorizontal: 24,
    flexDirection: "row",
    justifyContent: "space-between",
  },
  pagerButton: {
    width: 52,
    height: 52,
    borderRadius: 26,
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: "rgba(0,0,0,0.5)",
  },
  pagerDisabled: { opacity: 0.3 },
});
