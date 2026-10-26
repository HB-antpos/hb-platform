import { useCallback, useRef, useState } from "react";
import { Alert } from "react-native";
import type { BarcodeScanningResult, CameraType } from "expo-camera";
import { useCameraPermissions } from "expo-camera";
import {
  createCameraScanGateController,
  type CameraScanGateController,
} from "@/modules/scanner/camera-scan-gate";
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

interface UseCameraScanOptions {
  cooldownMs?: number;
  disabled?: boolean;
  ignoreWhileProcessing?: boolean;
  resetKey?: string | number | boolean | null;
  singleScanUntilReset?: boolean;
  suppressRepeatsUntilChange?: boolean;
  onBarcode: (barcode: string) => void | Promise<void>;
}

export type CameraScanMode = "single" | "continuous";

function normalizeBarcode(rawValue: string) {
  return rawValue.trim();
}

export function useCameraScan({
  cooldownMs = 1200,
  disabled = false,
  ignoreWhileProcessing = false,
  resetKey = null,
  singleScanUntilReset = false,
  suppressRepeatsUntilChange = false,
  onBarcode,
}: UseCameraScanOptions) {
  const { t } = useAppTranslation("common");
  const [permission, requestPermission] = useCameraPermissions();
  const [pickingImage, setPickingImage] = useState(false);
  // 相册选图 / 识别期间相机仍在后台出帧，这里同步拦住实时扫码，避免和图片结果抢同一条业务链路。
  const pickingImageRef = useRef(false);
  const scanGateControllerRef = useRef<CameraScanGateController | null>(null);
  if (scanGateControllerRef.current === null) {
    scanGateControllerRef.current = createCameraScanGateController(resetKey);
  }
  const scanGateController = scanGateControllerRef.current;
  // 每次 render 同步登记当前会话，使已经排队的旧 callback 立即失效。
  scanGateController.setCurrentResetKey(resetKey);

  const handleBarcodeScanned = useCallback(
    async ({ data, type }: Pick<BarcodeScanningResult, "data" | "type">) => {
      if (disabled) {
        return;
      }

      const barcode = restoreCameraBarcodeData(type, normalizeBarcode(data));
      if (!barcode) {
        return;
      }

      const now = Date.now();
      const lease = scanGateController.tryStart(resetKey, barcode, now, {
        cooldownMs,
        ignoreWhileProcessing,
        singleScanUntilReset,
        suppressRepeatsUntilChange,
      });
      if (!lease) {
        return;
      }

      try {
        await onBarcode(barcode);
      } finally {
        scanGateController.finish(lease);
      }
    },
    [
      cooldownMs,
      disabled,
      ignoreWhileProcessing,
      onBarcode,
      resetKey,
      scanGateController,
      singleScanUntilReset,
      suppressRepeatsUntilChange,
    ]
  );

  const handleCameraFrameBarcode = useCallback(
    (result: BarcodeScanningResult) => {
      if (pickingImageRef.current) {
        return;
      }
      return handleBarcodeScanned(result);
    },
    [handleBarcodeScanned]
  );

  /** 从相册选一张图识别条码；识别结果走与相机扫码完全相同的 gate 与业务回调。 */
  const pickFromLibrary = useCallback(async () => {
    if (disabled || pickingImageRef.current) {
      return;
    }
    pickingImageRef.current = true;
    setPickingImage(true);
    try {
      const picked = await pickLocalImage();
      if (picked.status !== "picked") {
        return;
      }
      const outcome = await scanBarcodeFromImage(picked.uri, localImageScanner);
      if (!outcome.ok) {
        Alert.alert(t("scanner.imageScanTitle"), t(LOCAL_IMAGE_FAILURE_MESSAGE_KEYS[outcome.reason]));
        return;
      }
      // 先放开实时扫码拦截，再交给统一入口：图片结果本身仍受 gate 的冷却 / 重复抑制约束。
      pickingImageRef.current = false;
      await handleBarcodeScanned(outcome.hit);
    } catch {
      Alert.alert(t("scanner.imageScanTitle"), t("scanner.imageReadFailed"));
    } finally {
      pickingImageRef.current = false;
      setPickingImage(false);
    }
  }, [disabled, handleBarcodeScanned, t]);

  return {
    permission,
    requestPermission,
    cameraProps: {
      facing: "back" as CameraType,
      onBarcodeScanned: handleCameraFrameBarcode,
    },
    /** 面板上的「从相册选择」入口；当前平台 / 原生包不支持时为 null，面板据此隐藏按钮。 */
    libraryPicker: canScanLocalImage() ? { onPress: pickFromLibrary, busy: pickingImage } : null,
  };
}
