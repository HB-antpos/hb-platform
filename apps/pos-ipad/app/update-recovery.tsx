import { File, Paths } from "expo-file-system";
import {
  type Href,
  useLocalSearchParams,
  useRouter,
} from "expo-router";
import * as Sharing from "expo-sharing";
import { useEffect, useMemo, useRef, useState } from "react";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import {
  AppUpdateRecoveryScreen,
  UPDATE_RECOVERY_SNAPSHOT_UNAVAILABLE,
  combineAppUpdateRecoverySnapshot,
  serializeAppUpdateRecoverySnapshot,
  type AppUpdateRecoveryRuntimeSnapshot,
  type AppUpdateRecoverySection,
} from "@/features/app-updates";
import { usePosThemeVersion } from "@/ui/theme";

const UPDATE_RECOVERY_EXPORT_FILE_NAME =
  "hb-pos-update-support.json";

export function resolveUpdateRecoverySection(
  value: string | string[] | undefined,
): AppUpdateRecoverySection {
  const candidate = Array.isArray(value) ? value[0] : value;
  return candidate === "support" ? "support" : "settings";
}

export async function shareUpdateRecoverySnapshot(
  serializedJson: string,
): Promise<void> {
  const file = new File(
    Paths.cache,
    UPDATE_RECOVERY_EXPORT_FILE_NAME,
  );
  try {
    file.create({ intermediates: true, overwrite: true });
    file.write(serializedJson);
    if (!(await Sharing.isAvailableAsync())) {
      throw new Error("UPDATE_RECOVERY_SHARING_UNAVAILABLE");
    }
    await Sharing.shareAsync(file.uri, {
      UTI: "public.json",
      dialogTitle: "HB POS update diagnostics",
      mimeType: "application/json",
    });
  } finally {
    // 支持包只在缓存中短暂存在；分享成功、取消或失败都必须清理。
    if (file.exists) file.delete();
  }
}

export default function UpdateRecoveryRoute() {
  usePosThemeVersion();
  const router = useRouter();
  const params = useLocalSearchParams<{ section?: string | string[] }>();
  const runtime = usePosRuntime();
  const section = resolveUpdateRecoverySection(params.section);
  const [runtimeSnapshot, setRuntimeSnapshot] =
    useState<AppUpdateRecoveryRuntimeSnapshot | null>(null);
  const [failed, setFailed] = useState(false);
  const [generation, setGeneration] = useState(0);
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState(false);
  const exportInFlightRef = useRef(false);
  const mountedRef = useRef(true);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
    };
  }, []);

  useEffect(() => {
    const services = runtime.services;
    if (!services) {
      setRuntimeSnapshot(null);
      setFailed(true);
      return undefined;
    }
    let active = true;
    setFailed(false);
    setRuntimeSnapshot(null);
    void services.appUpdateRecovery
      .readSnapshot()
      .then((snapshot) => {
        if (active) setRuntimeSnapshot(snapshot);
      })
      .catch(() => {
        if (active) setFailed(true);
      });
    return () => {
      active = false;
    };
  }, [generation, runtime.services]);

  const snapshot = useMemo(
    () =>
      runtimeSnapshot
        ? combineAppUpdateRecoverySnapshot(runtimeSnapshot, {
            backendState: runtime.state.backend,
            deviceState: runtime.state.device,
          })
        : null,
    [
      runtime.state.backend,
      runtime.state.device,
      runtimeSnapshot,
    ],
  );

  return (
    <AppUpdateRecoveryScreen
      exportError={exportError}
      exporting={exporting}
      onExport={() => {
        if (!snapshot || exportInFlightRef.current) return;
        exportInFlightRef.current = true;
        setExportError(false);
        setExporting(true);
        void shareUpdateRecoverySnapshot(
          serializeAppUpdateRecoverySnapshot(snapshot),
        )
          .catch(() => {
            if (mountedRef.current) setExportError(true);
          })
          .finally(() => {
            exportInFlightRef.current = false;
            if (mountedRef.current) setExporting(false);
          });
      }}
      onOpenRegistration={() =>
        router.push("/registration" as Href)
      }
      onRetry={() => setGeneration((current) => current + 1)}
      onSelectSection={(next) =>
        router.replace(
          `/update-recovery?section=${next}` as Href,
        )
      }
      section={section}
      state={
        snapshot
          ? { kind: "ready", snapshot }
          : failed
            ? {
                kind: "error",
                errorCode:
                  UPDATE_RECOVERY_SNAPSHOT_UNAVAILABLE,
              }
            : { kind: "loading" }
      }
    />
  );
}
