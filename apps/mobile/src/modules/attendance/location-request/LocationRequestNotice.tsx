import { useRef } from "react";
import { Snackbar } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { useLocationRequestNoticeStore } from "./location-request-notice-store";
import type { LocationRequestNotice as Notice } from "./location-request-logic";

/** 提示文案放在 attendance 命名空间的 locationRequest.*（中英文同步）。 */
function describeNotice(notice: Notice, t: (key: string, options?: Record<string, unknown>) => string): string {
  switch (notice.kind) {
    case "reported-named":
      return t("locationRequest.reported", { name: notice.name });
    case "reported-many":
      return t("locationRequest.reportedMany", { count: notice.count });
    case "reported-generic":
      return t("locationRequest.reportedGeneric");
    case "location-unavailable":
      return t("locationRequest.locationUnavailable");
  }
}

/**
 * 位置请求的轻量、非阻塞提示：已按请求上报位置，或（员工点通知时）没取到位置。
 * 挂在根布局里，任何页面上都能看到；不打断操作，几秒后自动消失。
 */
export function LocationRequestNotice() {
  const { t } = useAppTranslation("attendance");
  const seq = useLocationRequestNoticeStore((state) => state.seq);
  const notice = useLocationRequestNoticeStore((state) => state.notice);
  const dismiss = useLocationRequestNoticeStore((state) => state.dismiss);
  // 收起动画期间 notice 已是 null：保留最后一条文字，免得淡出时文字先消失。
  const lastNotice = useRef<Notice | null>(null);
  if (notice) lastNotice.current = notice;
  const shown = notice ?? lastNotice.current;

  return (
    <Snackbar key={seq} visible={notice !== null} onDismiss={dismiss} duration={5000}>
      {shown ? describeNotice(shown, t as (key: string, options?: Record<string, unknown>) => string) : ""}
    </Snackbar>
  );
}
