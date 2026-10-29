import { useCallback } from "react";
import {
  describeSubmittedAt,
  weekdayIndex,
  type WorkDateTier,
} from "@/modules/attendance/approval-board";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

/**
 * 审核页通用的日期 / 时薪档 / 提交时间文案。中文「周一 10-19」，英文按澳洲习惯「Mon 19/10」。
 */
export function useApprovalDateLabels() {
  const { t } = useAppTranslation(["attendance", "common"]);

  const weekdayDate = useCallback((workDate?: string) => {
    if (!workDate || workDate.length < 10) return "";
    const index = weekdayIndex(workDate);
    return t("approvals.board.weekdayDate", {
      weekday: index >= 0 ? t(`approvals.board.weekdays.${index}`) : "",
      month: workDate.slice(5, 7),
      day: workDate.slice(8, 10),
    });
  }, [t]);

  /** 详情页「按周六时薪」。 */
  const tierLabel = useCallback((tier: WorkDateTier) => t(`approvals.board.tierBy.${tier}`), [t]);
  /** 分组头「周日时薪」。 */
  const tierRate = useCallback((tier: WorkDateTier) => t(`approvals.board.tierRate.${tier}`), [t]);

  const submittedLabel = useCallback((submittedAt: string | undefined, nowMs: number) => {
    const relative = describeSubmittedAt(submittedAt, nowMs);
    if (!relative) return "";
    switch (relative.kind) {
      case "justNow":
        return t("approvals.board.submitted.justNow");
      case "minutes":
        return t("approvals.board.submitted.minutes", { count: relative.count });
      case "hours":
        return t("approvals.board.submitted.hours", { count: relative.count });
      case "yesterday":
        return t("approvals.board.submitted.yesterday", { time: relative.time });
      default:
        return t("approvals.board.submitted.date", {
          month: relative.date.slice(0, 2),
          day: relative.date.slice(3, 5),
          time: relative.time,
        });
    }
  }, [t]);

  return { weekdayDate, tierLabel, tierRate, submittedLabel };
}
