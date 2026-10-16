import { useQuery } from "@tanstack/react-query";
import { getStaffAttendanceRecords, getStaffAttendanceWeek } from "@/modules/users/staff-attendance-api";

/** 详情页汇总（近 30 天次数）与完整记录视图共用同一个查询：一次取够，两处复用。 */
export const STAFF_RECORDS_LIMIT = 50;

/** 本周周一（本地日期 YYYY-MM-DD）。 */
export function getCurrentWeekStartDate(now = new Date()) {
  const date = new Date(now);
  const diff = (date.getDay() + 6) % 7;
  date.setDate(date.getDate() - diff);
  date.setHours(0, 0, 0, 0);
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

// query key 沿用原员工详情页的写法，其他页面对它的失效逻辑继续有效。
export function useStaffWeekQuery(userGuid: string | undefined, storeCode: string | undefined, weekStartDate: string, enabled: boolean) {
  return useQuery({
    queryKey: ["staffAttendance", "week", storeCode ?? "", userGuid ?? "", weekStartDate],
    enabled: Boolean(enabled && userGuid && storeCode),
    queryFn: () => getStaffAttendanceWeek({ userGuid: userGuid!, storeCode, weekStartDate }),
  });
}

export function useStaffRecordsQuery(userGuid: string | undefined, storeCode: string | undefined, enabled: boolean) {
  return useQuery({
    queryKey: ["staffAttendance", "records", storeCode ?? "", userGuid ?? "", STAFF_RECORDS_LIMIT],
    enabled: Boolean(enabled && userGuid && storeCode),
    queryFn: () => getStaffAttendanceRecords({ userGuid: userGuid!, storeCode, limit: STAFF_RECORDS_LIMIT }),
  });
}
