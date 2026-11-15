import { useCallback, useRef, useState } from "react";
import { Alert, Linking, StyleSheet, View } from "react-native";
import { CameraView, useCameraPermissions } from "expo-camera";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { restoreCameraBarcodeData } from "@/modules/scanner/camera-barcode-data";
import {
  LOCAL_IMAGE_FAILURE_MESSAGE_KEYS,
  scanBarcodeFromImage,
} from "@/modules/scanner/local-image-barcode";
import {
  canScanLocalImage,
  localImageScanner,
  pickLocalImage,
} from "@/modules/scanner/local-image-barcode-runtime";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/**
 * 扫码内容面板（相机取景 / 权限提示 + 从相册选择），不自带 Modal，可嵌进任何已打开的弹层。
 * 新增多码弹层把它直接放进自己的 Modal 里，避免「收起一个原生 Modal 的同时弹另一个」——
 * iOS 会把后者丢弃，弹层看不见但页面状态仍是「弹层已打开」，整页被锁住并一直转圈。
 */
export function BarcodeScanPanel({ onScan }: { onScan: (barcode: string) => void }) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const [permission, requestPermission] = useCameraPermissions();
  const [failed, setFailed] = useState(false);
  const [pickingImage, setPickingImage] = useState(false);
  const consumed = useRef(false);
  // 选图 / 识别期间拦住实时相机帧，避免相册结果与相机结果同时回填。
  const pickingImageRef = useRef(false);

  /** 从相册选一张图识别条码；与相机共用 consumed 锁，一次会话只回填一次，不进入查询或保存流程。 */
  const pickFromLibrary = useCallback(async () => {
    if (pickingImageRef.current || consumed.current) return;
    pickingImageRef.current = true;
    setPickingImage(true);
    try {
      const picked = await pickLocalImage();
      if (picked.status !== "picked") return;
      const outcome = await scanBarcodeFromImage(picked.uri, localImageScanner);
      if (!outcome.ok) {
        // 没识别到 / 识别到多个：弹提示让用户换图，面板与相机保持打开。
        Alert.alert(
          t("common:scanner.imageScanTitle"),
          t(`common:${LOCAL_IMAGE_FAILURE_MESSAGE_KEYS[outcome.reason]}`),
        );
        return;
      }
      const barcode = restoreCameraBarcodeData(outcome.hit.type, outcome.hit.data.trim());
      if (!barcode || consumed.current) return;
      consumed.current = true;
      onScan(barcode);
    } catch {
      Alert.alert(t("common:scanner.imageScanTitle"), t("common:scanner.imageReadFailed"));
    } finally {
      pickingImageRef.current = false;
      setPickingImage(false);
    }
  }, [onScan, t]);

  return (
    <>
      {permission?.granted && !failed ? (
        <View style={styles.preview}>
          <CameraView
            style={StyleSheet.absoluteFill}
            facing="back"
            barcodeScannerSettings={{
              barcodeTypes: [
                "ean13",
                "ean8",
                "upc_a",
                "upc_e",
                "code128",
                "code39",
                "code93",
                "itf14",
                "codabar",
              ],
            }}
            onMountError={() => setFailed(true)}
            onBarcodeScanned={({ data, type }) => {
              if (pickingImageRef.current) return;
              const barcode = restoreCameraBarcodeData(type, data.trim());
              // 原生相机会连续回调；同步锁保证一次会话只回填一次，不进入查询或保存流程。
              if (!barcode || consumed.current) return;
              consumed.current = true;
              onScan(barcode);
            }}
          />
          <View pointerEvents="none" style={styles.guide} />
        </View>
      ) : (
        <View style={styles.permission}>
          <Text>
            {t(
              failed
                ? "createProduct.cameraUnavailable"
                : "createProduct.cameraPermissionHint",
            )}
          </Text>
          {!failed ? (
            <Button
              mode="contained"
              onPress={() =>
                permission?.canAskAgain === false
                  ? void Linking.openSettings()
                  : void requestPermission()
              }
            >
              {t(
                permission?.canAskAgain === false
                  ? "createProduct.openSettings"
                  : "camera.grantPermission",
              )}
            </Button>
          ) : null}
        </View>
      )}
      {/* 相册入口独立于相机权限：没开相机权限 / 相机不可用时，仍可选图识别。 */}
      {canScanLocalImage() ? (
        <Button
          mode="outlined"
          icon="image-outline"
          onPress={() => void pickFromLibrary()}
          loading={pickingImage}
          disabled={pickingImage}
          contentStyle={styles.libraryButtonContent}
          style={styles.libraryButton}
        >
          {t("common:scanner.pickFromLibrary")}
        </Button>
      ) : null}
    </>
  );
}

/** 创建商品流程使用的独立扫码弹层：内容同 BarcodeScanPanel，自带 Modal 与取消按钮。 */
export function CreateBarcodeScanner({
  onScan,
  onDismiss,
}: {
  onScan: (barcode: string) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  return (
    <BusinessSheet
      visible
      title={t("createProduct.scanTitle")}
      subtitle={t("createProduct.scanHint")}
      onDismiss={onDismiss}
      footer={<Button onPress={onDismiss}>{t("common:actions.cancel")}</Button>}
    >
      <BarcodeScanPanel onScan={onScan} />
    </BusinessSheet>
  );
}
const styles = StyleSheet.create({
  preview: {
    height: 300,
    overflow: "hidden",
    borderRadius: 12,
    backgroundColor: "#101828",
    justifyContent: "center",
    alignItems: "center",
  },
  guide: {
    width: "82%",
    height: 120,
    borderWidth: 2,
    borderColor: "#FFFFFF",
    borderRadius: 10,
  },
  permission: { minHeight: 180, justifyContent: "center", gap: 16 },
  libraryButton: { marginTop: 12 },
  libraryButtonContent: { minHeight: 48 },
});
