import { useState } from "react";
import { StyleSheet, View } from "react-native";
import { Button } from "react-native-paper";
import { CameraView } from "expo-camera";
import { CameraScanSheet } from "@/components/ui/CameraScanSheet";
import { useCameraScan, type CameraScanMode } from "@/modules/scanner/use-camera-scan";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/** 没有扫码枪时用相机扫：单次模式扫到即关闭，连续模式保持打开连扫。 */
export function PickCameraSheet({
  visible,
  title,
  onDismiss,
  onBarcode,
}: {
  visible: boolean;
  title: string;
  onDismiss: () => void;
  onBarcode: (barcode: string) => void;
}) {
  const { t } = useAppTranslation("warehousePicking");
  const [mode, setMode] = useState<CameraScanMode>("single");
  const camera = useCameraScan({
    disabled: !visible,
    cooldownMs: 1200,
    suppressRepeatsUntilChange: mode === "continuous",
    onBarcode: (barcode) => {
      if (mode === "single") {
        onDismiss();
      }
      onBarcode(barcode);
    },
  });

  return (
    <CameraScanSheet visible={visible} title={title} mode={mode} onModeChange={setMode} onDismiss={onDismiss}>
      {camera.permission?.granted ? (
        <CameraView style={styles.camera} {...camera.cameraProps} />
      ) : (
        <View style={styles.permission}>
          <Button mode="contained" onPress={() => void camera.requestPermission()}>
            {t("actions.enableCamera")}
          </Button>
        </View>
      )}
    </CameraScanSheet>
  );
}

const styles = StyleSheet.create({
  camera: { height: 280 },
  permission: { height: 160, alignItems: "center", justifyContent: "center" },
});
