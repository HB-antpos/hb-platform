import { PayslipDetailScreen } from "@/modules/payslips/PayslipDetailScreen";

/** 工资单定位参数（connectionGuid / employeeUid / paymentDate / periodStart）放在查询串里。 */
export default function PayslipDetailRoute() {
  return <PayslipDetailScreen />;
}
