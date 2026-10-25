import { PaidLeaveScreen } from "@/components/attendance/leave/PaidLeaveScreen";

/** 带薪假申请（隐藏子页，不进菜单）；参数 type=PaidSick|PaidAnnual|AnnualCashOut、storeCode。 */
export default function AttendanceLeavePaidRoute() {
  return <PaidLeaveScreen />;
}
