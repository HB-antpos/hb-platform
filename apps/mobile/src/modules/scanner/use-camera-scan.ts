import { useCallback, useRef } from "react";
import type { BarcodeScanningResult, CameraType } from "expo-camera";
import { useCameraPermissions } from "expo-camera";
import {
  createCameraScanGateController,
  type CameraScanGateController,
} from "@/modules/scanner/camera-scan-gate";
import { restoreCameraBarcodeData } from "@/modules/scanner/camera-barcode-data";

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
  const [permission, requestPermission] = useCameraPermissions();
  const scanGateControllerRef = useRef<CameraScanGateController | null>(null);
  if (scanGateControllerRef.current === null) {
    scanGateControllerRef.current = createCameraScanGateController(resetKey);
  }
  const scanGateController = scanGateControllerRef.current;
  // 每次 render 同步登记当前会话，使已经排队的旧 callback 立即失效。
  scanGateController.setCurrentResetKey(resetKey);

  const handleBarcodeScanned = useCallback(
    async ({ data, type }: BarcodeScanningResult) => {
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

  return {
    permission,
    requestPermission,
    cameraProps: {
      facing: "back" as CameraType,
      onBarcodeScanned: handleBarcodeScanned,
    },
  };
}
