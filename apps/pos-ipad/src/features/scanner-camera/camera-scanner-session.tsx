import {
  CameraView,
  useCameraPermissions,
  type BarcodeScanningResult,
  type BarcodeType,
} from "expo-camera";
import { useCallback, useEffect, useRef, useState } from "react";
import {
  ActivityIndicator,
  Platform,
  Text,
  View,
  type StyleProp,
  type ViewStyle,
} from "react-native";

import type { CameraScannerCopyKey } from "./camera-scanner-copy";

import { PosButton } from "@/ui/controls/pos-button";
import {
  createPosStyles,
  posColors,
  posRadius,
  usePosThemeVersion,
} from "@/ui/theme";

/** 相机扫码界面里按钮与状态条的最小高度（收银场景的常规按钮从 56 起）。 */
export const CAMERA_SCANNER_MIN_TOUCH_TARGET = 56;
/**
 * 取景框固定深色底、白色对准框，不跟随主题：相机画面没出来之前和画面四周都应该是暗的，
 * 对准框压在实时画面上，只有白色在各种商品包装上都看得清。
 */
export const CAMERA_SCANNER_PREVIEW_BACKGROUND = "#0A1723";
export const CAMERA_SCANNER_TARGET_BORDER = "#FFFFFF";
export const CAMERA_SCANNER_SUPPORTED_ORIENTATIONS = [
  "landscape-left",
  "landscape-right",
] satisfies ("landscape-left" | "landscape-right")[];

export const CAMERA_SCANNER_BARCODE_TYPES: BarcodeType[] = [
  "ean13",
  "code128",
  "qr",
];

/** 相机功能只依赖扫描公共边界，禁止把会话或授权资料传入 UI。 */
export type CameraScannerPort = Pick<
  import("@/core/peripherals/scanner").HidScannerRouter,
  "acceptCameraText" | "startCamera" | "stopCamera"
>;

export type NativeAvailability = "checking" | "ready" | "unavailable";

export type CameraScannerBarcodeResult = BarcodeScanningResult;

export type CameraScannerTranslate = (key: CameraScannerCopyKey) => string;

const cameraSessionOwnerByPort = new WeakMap<CameraScannerPort, symbol>();

function releaseOwnedCameraSession(
  scanner: CameraScannerPort,
  token: symbol,
): boolean {
  if (cameraSessionOwnerByPort.get(scanner) !== token) return false;
  cameraSessionOwnerByPort.delete(scanner);
  void scanner.stopCamera();
  return true;
}

export function useCameraScannerSession({
  scanner,
  visible,
}: Readonly<{
  scanner: CameraScannerPort;
  visible: boolean;
}>) {
  const [permission, requestPermission] = useCameraPermissions();
  const [availability, setAvailability] =
    useState<NativeAvailability>("checking");
  const [cameraStarted, setCameraStarted] = useState(false);
  const cameraStartedRef = useRef(false);
  const cameraSessionTokenRef = useRef<symbol | null>(null);
  const permissionRequestTokenRef = useRef<symbol | null>(null);
  const sessionGenerationRef = useRef(0);

  const stopCamera = useCallback(() => {
    const token = cameraSessionTokenRef.current;
    cameraSessionTokenRef.current = null;
    if (cameraStartedRef.current) {
      cameraStartedRef.current = false;
      setCameraStarted(false);
    }
    if (token) releaseOwnedCameraSession(scanner, token);
  }, [scanner]);

  const deactivateSession = useCallback(() => {
    sessionGenerationRef.current += 1;
    permissionRequestTokenRef.current = null;
    stopCamera();
  }, [stopCamera]);

  const markCameraUnavailable = useCallback(() => {
    deactivateSession();
    setAvailability("unavailable");
  }, [deactivateSession]);

  useEffect(() => {
    if (!visible) {
      setAvailability("checking");
      deactivateSession();
    }
  }, [deactivateSession, visible]);

  useEffect(() => {
    if (!visible || !permission?.granted) {
      setAvailability("checking");
      return;
    }

    // expo-camera 的 isAvailableAsync 仅在 Web 实现；iOS 由相机视图挂载结果判断真实能力。
    if (Platform.OS !== "web") {
      setAvailability("ready");
      return;
    }

    let cancelled = false;
    const checkNativeCamera = CameraView.isAvailableAsync;
    if (typeof checkNativeCamera !== "function") {
      setAvailability("unavailable");
      return;
    }
    void checkNativeCamera()
      .then((isAvailable) => {
        if (!cancelled) {
          setAvailability(isAvailable ? "ready" : "unavailable");
        }
      })
      .catch(() => {
        // Development Build 缺少原生模块或模拟器无相机时必须保持关闭状态。
        if (!cancelled) setAvailability("unavailable");
      });
    return () => {
      cancelled = true;
    };
  }, [permission?.granted, visible]);

  useEffect(() => {
    if (!visible || !permission?.granted || availability !== "ready") {
      stopCamera();
      return;
    }

    let cancelled = false;
    const generation = sessionGenerationRef.current;
    const token = Symbol("camera-scanner-session");
    cameraSessionOwnerByPort.set(scanner, token);
    cameraSessionTokenRef.current = token;

    const startCamera = async (): Promise<void> => {
      try {
        await scanner.startCamera();
        const ownsCamera = cameraSessionOwnerByPort.get(scanner) === token;
        if (
          cancelled ||
          generation !== sessionGenerationRef.current ||
          !ownsCamera
        ) {
          if (ownsCamera) {
            releaseOwnedCameraSession(scanner, token);
          } else if (!cameraSessionOwnerByPort.has(scanner)) {
            // 已关闭且没有新会话接管时，清理由旧启动迟到恢复的底层相机。
            void scanner.stopCamera();
          }
          return;
        }
        cameraStartedRef.current = true;
        setCameraStarted(true);
      } catch {
        const ownsCamera = cameraSessionOwnerByPort.get(scanner) === token;
        if (ownsCamera) releaseOwnedCameraSession(scanner, token);
        if (cameraSessionTokenRef.current === token) {
          cameraSessionTokenRef.current = null;
        }
        if (!cancelled && generation === sessionGenerationRef.current) {
          setAvailability("unavailable");
        }
      }
    };
    void startCamera();
    return () => {
      cancelled = true;
      if (cameraSessionTokenRef.current === token) {
        cameraSessionTokenRef.current = null;
        if (cameraStartedRef.current) {
          cameraStartedRef.current = false;
          setCameraStarted(false);
        }
      }
      releaseOwnedCameraSession(scanner, token);
    };
  }, [availability, permission?.granted, scanner, stopCamera, visible]);

  const requestCameraPermission = useCallback(() => {
    if (permissionRequestTokenRef.current) return;
    const generation = sessionGenerationRef.current;
    const token = Symbol("camera-permission-request");
    permissionRequestTokenRef.current = token;
    const finishRequest = () => {
      if (permissionRequestTokenRef.current !== token) return false;
      permissionRequestTokenRef.current = null;
      return true;
    };
    const handleFailure = () => {
      if (
        finishRequest() &&
        generation === sessionGenerationRef.current
      ) {
        markCameraUnavailable();
      }
    };
    try {
      void Promise.resolve(requestPermission()).then(finishRequest, handleFailure);
    } catch {
      handleFailure();
    }
  }, [markCameraUnavailable, requestPermission]);

  return {
    availability,
    cameraStarted,
    cameraStartedRef,
    deactivateSession,
    markCameraUnavailable,
    permissionDenied: permission?.status === "denied",
    permissionGranted: Boolean(permission?.granted),
    requestCameraPermission,
    showCamera: Boolean(
      visible && permission?.granted && availability === "ready" && cameraStarted,
    ),
  } as const;
}

export function CameraScannerCameraView({
  accessibilityLabel,
  onBarcodeScanned,
  onMountUnavailable,
  style,
  testID = "camera-scanner-preview",
}: Readonly<{
  accessibilityLabel: string;
  onBarcodeScanned(result: BarcodeScanningResult): void;
  onMountUnavailable(): void;
  style: StyleProp<ViewStyle>;
  testID?: string;
}>) {
  usePosThemeVersion();
  return (
    <CameraView
      accessibilityLabel={accessibilityLabel}
      active
      barcodeScannerSettings={{
        barcodeTypes: CAMERA_SCANNER_BARCODE_TYPES,
      }}
      facing="back"
      onBarcodeScanned={onBarcodeScanned}
      onMountError={onMountUnavailable}
      style={style}
      testID={testID}
    />
  );
}

export function CameraScannerState({
  availability,
  onRequestPermission,
  permissionDenied,
  permissionGranted,
  stateStyle,
  t,
}: Readonly<{
  availability: NativeAvailability;
  permissionDenied: boolean;
  permissionGranted: boolean;
  onRequestPermission(): void;
  stateStyle?: StyleProp<ViewStyle>;
  t: CameraScannerTranslate;
}>) {
  usePosThemeVersion();
  if (permissionDenied) {
    return (
      <View
        accessibilityRole="alert"
        style={[styles.state, stateStyle]}
        testID="camera-scanner-permission-denied"
      >
        <Text style={styles.stateTitle}>{t("permission.denied.title")}</Text>
        <Text style={styles.stateCopy}>{t("permission.denied.body")}</Text>
      </View>
    );
  }

  if (availability === "unavailable") {
    return (
      <View
        accessibilityRole="alert"
        style={[styles.state, stateStyle]}
        testID="camera-scanner-unavailable"
      >
        <Text style={styles.stateTitle}>{t("unavailable.title")}</Text>
        <Text style={styles.stateCopy}>{t("unavailable.body")}</Text>
      </View>
    );
  }

  if (!permissionGranted) {
    return (
      <View
        style={[styles.state, stateStyle]}
        testID="camera-scanner-permission-request-state"
      >
        <Text style={styles.stateTitle}>{t("permission.required.title")}</Text>
        <Text style={styles.stateCopy}>{t("permission.required.body")}</Text>
        <PosButton
          icon="camera-outline"
          label={t("action.allowCamera")}
          onPress={onRequestPermission}
          size="medium"
          style={styles.permissionButton}
          testID="camera-scanner-request-permission"
          tone="primary"
        />
      </View>
    );
  }

  if (availability === "checking") {
    return (
      <View
        accessibilityLiveRegion="polite"
        style={[styles.state, stateStyle]}
        testID="camera-scanner-starting"
      >
        <ActivityIndicator color={posColors.orange} />
        <Text style={styles.stateTitle}>{t("checking")}</Text>
      </View>
    );
  }

  return (
    <View
      accessibilityLiveRegion="polite"
      style={[styles.state, stateStyle]}
      testID="camera-scanner-starting"
    >
      <ActivityIndicator color={posColors.orange} />
      <Text style={styles.stateTitle}>{t("starting")}</Text>
    </View>
  );
}

const styles = createPosStyles((colors) => ({
  permissionButton: {
    alignSelf: "flex-start",
    marginTop: 16,
    minHeight: CAMERA_SCANNER_MIN_TOUCH_TARGET,
  },
  state: {
    alignItems: "flex-start",
    backgroundColor: colors.canvas,
    borderRadius: posRadius.medium,
    justifyContent: "center",
    minHeight: 200,
    padding: 20,
  },
  stateCopy: {
    color: colors.mutedInk,
    fontSize: 15,
    fontWeight: "600",
    lineHeight: 22,
    marginTop: 8,
  },
  stateTitle: { color: colors.ink, fontSize: 18, fontWeight: "900" },
}));
