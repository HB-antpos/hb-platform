import { useCallback, useRef, useState } from "react";
import * as Location from "expo-location";
import { useFocusEffect } from "@react-navigation/native";
import { getStoredApiHost } from "@/shared/api/config";
import { probeWithTransportFallback } from "@/shared/api/transport";
import type {
  AttendancePunchVerificationState,
} from "@/modules/attendance/types";
import {
  collectRequiredLocation,
  isRequiredLocationError,
  RequiredLocationError,
  type CapturedLocationPayload,
} from "@/modules/attendance/required-location";
import { createAttendanceLocationCapture } from "./attendance-location-capture";
import { isIosReviewSessionActive } from "@/modules/ios-review/session";
import { reviewAwareFetch } from "@/modules/ios-review/network";

const NETWORK_CHECK_TIMEOUT_MS = 5000;

const DEFAULT_VERIFICATION_STATE: AttendancePunchVerificationState = {
  checkedAt: undefined,
  location: {
    status: "unknown",
    reason: "dependencyMissing",
    permissionStatus: "unavailable",
  },
  network: {
    status: "unknown",
    reason: "unknown",
    verificationStatus: "unknown",
  },
  payload: {
    locationPermissionStatus: "unavailable",
    networkVerificationStatus: "unknown",
  },
};

export async function verifyAttendanceNetworkReachability() {
  if (isIosReviewSessionActive()) {
    // 离线 Demo 的业务请求由本地 adapter 处理，无需探测生产 health endpoint。
    return {
      status: "available" as const,
      reason: "captured" as const,
      verificationStatus: "online" as const,
    };
  }
  const host = await getStoredApiHost();

  try {
    // 打卡时的网络校验也要走传输入口选择：个别运营商到某个入口的线路不通时，
    // 不能把门店员工误判成「离线」而影响打卡；当前入口不通会顺带试备用入口。
    // 每次尝试自带独立的超时控制器（第一次耗尽超时后，第二次不能共用已中止的控制器）。
    await probeWithTransportFallback(host, async (apiBaseUrl) => {
      const healthUrl = `${apiBaseUrl.replace(/\/api$/, "")}/health`;
      const controller = new AbortController();
      const timeoutId = setTimeout(
        () => controller.abort(),
        NETWORK_CHECK_TIMEOUT_MS,
      );
      try {
        return await reviewAwareFetch(healthUrl, {
          method: "GET",
          headers: { Accept: "application/json" },
          signal: controller.signal,
        });
      } finally {
        clearTimeout(timeoutId);
      }
    });
    return {
      status: "available" as const,
      reason: "captured" as const,
      verificationStatus: "online" as const,
    };
  } catch {
    return {
      status: "unavailable" as const,
      reason: "networkUnreachable" as const,
      verificationStatus: "offline" as const,
    };
  }
}

async function collectVerificationState(options: {
  collectLocation: () => Promise<CapturedLocationPayload>;
  networkVerified: boolean;
}): Promise<AttendancePunchVerificationState> {
  const [networkResult, locationResult] = await Promise.allSettled([
    options.networkVerified
      ? Promise.resolve({ status: "available" as const, reason: "captured" as const, verificationStatus: "online" as const })
      : verifyAttendanceNetworkReachability(),
    options.collectLocation(),
  ]);
  const checkedAt = new Date().toISOString();
  const network =
    networkResult.status === "fulfilled"
      ? networkResult.value
      : {
          status: "unavailable" as const,
          reason: "networkUnreachable" as const,
          verificationStatus: "offline" as const,
        };

  if (locationResult.status === "fulfilled") {
    const location = locationResult.value;
    return {
      checkedAt,
      location: {
        status: "available",
        reason: "captured",
        permissionStatus: location.locationPermissionStatus,
        latitude: location.locationLatitude,
        longitude: location.locationLongitude,
        accuracy: location.locationAccuracy,
      },
      network,
      payload: {
        ...location,
        networkVerificationStatus: network.verificationStatus,
      },
    };
  }

  const denied = isRequiredLocationError(locationResult.reason);
  const timedOut = locationResult.reason?.code === "LOCATION_TIMEOUT";

  return {
    checkedAt,
    location: {
      status: denied ? "permissionDenied" : "unavailable",
      reason: denied ? "permissionDenied" : timedOut ? "timeout" : "unknown",
      permissionStatus: denied ? "denied" : "unavailable",
    },
    network,
    payload: {
      locationPermissionStatus: denied ? "denied" : "unavailable",
      networkVerificationStatus: network.verificationStatus,
    },
  };
}

export function usePunchVerification() {
  const [verification, setVerification] = useState<AttendancePunchVerificationState>(
    DEFAULT_VERIFICATION_STATE,
  );
  const [isRefreshing, setIsRefreshing] = useState(false);
  const locationCaptureRef = useRef<ReturnType<typeof createAttendanceLocationCapture> | null>(null);
  const verificationRevisionRef = useRef(0);

  const stopLocationCapture = useCallback(() => {
    locationCaptureRef.current?.dispose();
    locationCaptureRef.current = null;
  }, []);

  const prewarmLocation = useCallback(() => {
    stopLocationCapture();
    if (isIosReviewSessionActive()) return;
    const capture = createAttendanceLocationCapture({
      watch: async (onPosition, onError, isActive) => {
        // 打开扫码只预热已授权定位，不能抢先弹出系统权限框。
        const permission = await Location.getForegroundPermissionsAsync();
        if (!isActive()) throw new Error("LOCATION_CANCELLED");
        if (permission.status !== "granted") throw new RequiredLocationError();
        return Location.watchPositionAsync({
          accuracy: Location.Accuracy.High,
          distanceInterval: 0,
          timeInterval: 1000,
        }, onPosition, (reason) => onError(new Error(reason)));
      },
    });
    locationCaptureRef.current = capture;
    capture.prewarm();
  }, [stopLocationCapture]);

  const captureCurrentLocation = useCallback(async (isActive: () => boolean): Promise<CapturedLocationPayload> => {
    if (isIosReviewSessionActive()) return collectRequiredLocation();
    let permission = await Location.getForegroundPermissionsAsync();
    if (!isActive()) throw new Error("LOCATION_CANCELLED");
    if (permission.status !== "granted") {
      permission = await Location.requestForegroundPermissionsAsync();
      if (!isActive()) throw new Error("LOCATION_CANCELLED");
      if (permission.status !== "granted") throw new RequiredLocationError();
      // 权限交互结束后重新开始本次采集，不能复用授权前的坐标。
      prewarmLocation();
    }
    if (!locationCaptureRef.current) prewarmLocation();
    const position = await locationCaptureRef.current!.capture();
    return {
      locationLatitude: position.coords.latitude,
      locationLongitude: position.coords.longitude,
      locationAccuracy: position.coords.accuracy ?? undefined,
      locationPermissionStatus: "granted",
      locationCapturedAtUtc: new Date(position.timestamp).toISOString(),
    };
  }, [prewarmLocation]);

  const refreshVerification = useCallback(async (options?: { networkVerified?: boolean }) => {
    const revision = ++verificationRevisionRef.current;
    const isActive = () => verificationRevisionRef.current === revision;
    setIsRefreshing(true);
    try {
      const nextState = await collectVerificationState({
        collectLocation: () => captureCurrentLocation(isActive),
        networkVerified: options?.networkVerified === true,
      });
      if (isActive()) setVerification(nextState);
      return nextState;
    } catch {
      const fallbackState: AttendancePunchVerificationState = {
        ...DEFAULT_VERIFICATION_STATE,
        checkedAt: new Date().toISOString(),
      };
      if (isActive()) setVerification(fallbackState);
      return fallbackState;
    } finally {
      if (isActive()) setIsRefreshing(false);
    }
  }, [captureCurrentLocation]);

  useFocusEffect(
    useCallback(() => {
      let active = true;
      const revision = verificationRevisionRef.current;
      setIsRefreshing(false);
      // 页面状态探测不占用扫码流程，也不在每次聚焦时额外发起一次 GPS 请求。
      void verifyAttendanceNetworkReachability().then((network) => {
        // 较早的 health 响应不能覆盖扫码请求已经确认的联网状态。
        if (active) setVerification((current) => (
          verificationRevisionRef.current === revision ? { ...current, network } : current
        ));
      }).catch(() => undefined);
      return () => {
        active = false;
        verificationRevisionRef.current += 1;
        stopLocationCapture();
      };
    }, [stopLocationCapture]),
  );

  return {
    verification,
    isRefreshing,
    refreshVerification,
    prewarmLocation,
    stopLocationCapture,
  };
}
