import { useRef, useState } from "react";
import { Linking, StyleSheet, View } from "react-native";
import { CameraView, useCameraPermissions } from "expo-camera";
import { Button, Text } from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { restoreCameraBarcodeData } from "@/modules/scanner/camera-barcode-data";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

export function CreateBarcodeScanner({
  onScan,
  onDismiss,
}: {
  onScan: (barcode: string) => void;
  onDismiss: () => void;
}) {
  const { t } = useAppTranslation(["productQuery", "common"]);
  const [permission, requestPermission] = useCameraPermissions();
  const [failed, setFailed] = useState(false);
  const consumed = useRef(false);
  return (
    <BusinessSheet
      visible
      title={t("createProduct.scanTitle")}
      subtitle={t("createProduct.scanHint")}
      onDismiss={onDismiss}
      footer={<Button onPress={onDismiss}>{t("common:actions.cancel")}</Button>}
    >
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
});
