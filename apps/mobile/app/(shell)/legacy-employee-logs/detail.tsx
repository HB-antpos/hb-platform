import { LegacyEmployeeLogDetailScreen } from "@/modules/legacy-employee-logs/LegacyEmployeeLogDetailScreen";

/** 详情编号放在查询串（?id=）里：旧收银的日志编号不保证是规范 GUID。 */
export default function LegacyEmployeeLogDetailRoute() {
  return <LegacyEmployeeLogDetailScreen />;
}
