import { MaterialCommunityIcons } from "@expo/vector-icons";
import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { Text, View } from "react-native";

import { POS_TOP_BAR_HEIGHT } from "./status-strip";

import { usePosRuntime } from "@/core/runtime/pos-runtime-context";
import { createPosStyles, posColors, usePosThemeVersion } from "@/ui/theme";

/** 轻提示停留时长：够看清一句话，又不挡后续操作。 */
const NOTICE_VISIBLE_MS = 2_500;

/**
 * BLE 扫码枪扫到码、但当前页面不接收扫码（付款中、设置等）时，在顶栏下方浮一条轻提示。
 * 不拦截触摸、不缓存那次扫码；没有 BLE 服务（旧原生包）时什么也不渲染。
 */
export function BleScanNoticeBridge() {
  usePosThemeVersion();
  const runtime = usePosRuntime();
  const ble = runtime.services?.scanner.ble ?? null;
  const { t } = useTranslation();
  const [visibleAt, setVisibleAt] = useState<number | null>(null);

  useEffect(() => {
    if (!ble) return undefined;
    return ble.subscribeUndelivered((notice) => setVisibleAt(notice.at));
  }, [ble]);

  useEffect(() => {
    if (visibleAt === null) return undefined;
    const timer = setTimeout(() => setVisibleAt(null), NOTICE_VISIBLE_MS);
    return () => clearTimeout(timer);
  }, [visibleAt]);

  if (visibleAt === null) return null;
  return (
    <View pointerEvents="none" style={styles.layer} testID="ble-scan-notice">
      <View accessibilityLiveRegion="polite" style={styles.notice}>
        <MaterialCommunityIcons
          accessible={false}
          color={posColors.onInverse}
          name="barcode-off"
          size={18}
        />
        <Text style={styles.text}>{t("status.scanner.bleNotAccepted")}</Text>
      </View>
    </View>
  );
}

const styles = createPosStyles((colors) => ({
  layer: {
    alignItems: "center",
    left: 0,
    position: "absolute",
    right: 0,
    // 顶栏之下再留出系统状态栏的高度，不压住顶栏状态。
    top: POS_TOP_BAR_HEIGHT + 36,
  },
  notice: {
    alignItems: "center",
    backgroundColor: colors.inverseSurface,
    borderRadius: 999,
    flexDirection: "row",
    gap: 8,
    paddingHorizontal: 16,
    paddingVertical: 10,
    shadowColor: colors.shadow,
    shadowOffset: { height: 3, width: 0 },
    shadowOpacity: 0.2,
    shadowRadius: 8,
  },
  text: {
    color: colors.onInverse,
    fontSize: 14,
    fontWeight: "800",
  },
}));
