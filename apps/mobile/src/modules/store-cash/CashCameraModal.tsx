// 现金单据拍照：全屏相机，可连续拍多张（一张存单最多 3 张、支出最多 5 张），拍满或点「完成」关闭。
// 拍照锁防止连点；压缩与加入列表由调用方的 onCaptured 完成，失败时在这里就地提示。
import { useEffect, useRef, useState } from "react";
import { Linking, Modal, Pressable, StyleSheet, View } from "react-native";
import { CameraView, useCameraPermissions } from "expo-camera";
import { MaterialCommunityIcons } from "@expo/vector-icons";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { useSafeAreaInsets } from "react-native-safe-area-context";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS } from "@/shared/theme/tokens";

export interface CapturedPhotoSource {
  uri: string;
  width: number;
  height: number;
}

export function CashCameraModal({
  visible,
  maxCount,
  currentCount,
  onDismiss,
  onCaptured,
}: {
  visible: boolean;
  /** 本字段最多几张。 */
  maxCount: number;
  /** 打开时已有几张。 */
  currentCount: number;
  onDismiss: () => void;
  /** 处理并加入列表；抛错表示这张没加成，相机保持打开让用户重拍。 */
  onCaptured: (source: CapturedPhotoSource) => Promise<void>;
}) {
  const { t } = useAppTranslation(["storeCash", "common"]);
  const insets = useSafeAreaInsets();
  const cameraRef = useRef<CameraView | null>(null);
  const captureLock = useRef(false);
  const [permission, requestPermission] = useCameraPermissions();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState("");
  const full = currentCount >= maxCount;

  useEffect(() => {
    if (visible) setError("");
  }, [visible]);

  const handleCapture = async () => {
    if (captureLock.current || full) return;
    captureLock.current = true;
    setPending(true);
    setError("");
    try {
      const picture = await cameraRef.current?.takePictureAsync({ quality: 1 });
      if (!picture?.uri) throw new Error("camera returned no photo");
      await onCaptured({ uri: picture.uri, width: picture.width, height: picture.height });
    } catch {
      setError(t("camera.captureFailed"));
    } finally {
      captureLock.current = false;
      setPending(false);
    }
  };

  if (!visible) return null;

  return (
    <Modal visible animationType="slide" presentationStyle="fullScreen" statusBarTranslucent onRequestClose={onDismiss}>
      <View style={styles.root}>
        <View style={[styles.topBar, { paddingTop: insets.top + 8 }]}>
          <Text style={styles.counter}>{t("camera.taken", { count: currentCount, max: maxCount })}</Text>
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t("common:actions.close")}
            onPress={onDismiss}
            disabled={pending}
            hitSlop={8}
            style={styles.closeButton}
          >
            <MaterialCommunityIcons name="close" size={26} color="#FFFFFF" />
          </Pressable>
        </View>

        {permission?.granted ? (
          <CameraView ref={cameraRef} style={styles.camera} facing="back" />
        ) : (
          <View style={styles.permission}>
            <Text style={styles.permissionTitle}>{t("camera.permissionTitle")}</Text>
            <Text style={styles.permissionText}>{t("camera.permissionDescription")}</Text>
            {permission && !permission.canAskAgain ? (
              <Button mode="contained" icon="cog" onPress={() => void Linking.openSettings()}>
                {t("camera.openSettings")}
              </Button>
            ) : (
              <Button mode="contained" onPress={() => void requestPermission()}>
                {t("camera.grantPermission")}
              </Button>
            )}
          </View>
        )}

        <View style={[styles.bottomBar, { paddingBottom: insets.bottom + 16 }]}>
          {error ? <Text style={styles.error}>{error}</Text> : null}
          {full ? <Text style={styles.hint}>{t("camera.full", { max: maxCount })}</Text> : null}
          <View style={styles.controls}>
            <View style={styles.side} />
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("camera.capture")}
              accessibilityState={{ disabled: pending || full || !permission?.granted }}
              disabled={pending || full || !permission?.granted}
              onPress={() => void handleCapture()}
              style={[styles.shutter, pending || full || !permission?.granted ? styles.shutterDisabled : null]}
            >
              {pending ? <ActivityIndicator color={HB_COLORS.action} /> : <View style={styles.shutterInner} />}
            </Pressable>
            <View style={styles.side}>
              <Button mode="contained" onPress={onDismiss} disabled={pending} compact>
                {t("camera.done")}
              </Button>
            </View>
          </View>
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: "#000000" },
  topBar: {
    paddingHorizontal: 16,
    paddingBottom: 8,
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  counter: { color: "#FFFFFF", fontSize: 16, fontWeight: "600" },
  closeButton: { width: 44, height: 44, alignItems: "center", justifyContent: "center" },
  camera: { flex: 1 },
  permission: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, paddingHorizontal: 32 },
  permissionTitle: { color: "#FFFFFF", fontSize: 18, fontWeight: "700", textAlign: "center" },
  permissionText: { color: "#D0D5DD", fontSize: 14, textAlign: "center" },
  bottomBar: { paddingTop: 12, paddingHorizontal: 16, gap: 8, backgroundColor: "#000000" },
  error: { color: "#FDA29B", textAlign: "center", fontSize: 14 },
  hint: { color: "#D0D5DD", textAlign: "center", fontSize: 14 },
  controls: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  side: { width: 96, alignItems: "flex-end" },
  shutter: {
    width: 76,
    height: 76,
    borderRadius: 38,
    borderWidth: 4,
    borderColor: "#FFFFFF",
    alignItems: "center",
    justifyContent: "center",
  },
  shutterDisabled: { opacity: 0.4 },
  shutterInner: { width: 56, height: 56, borderRadius: 28, backgroundColor: "#FFFFFF" },
});
