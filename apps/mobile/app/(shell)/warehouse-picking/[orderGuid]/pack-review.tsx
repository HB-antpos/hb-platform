import { useLocalSearchParams } from "expo-router";
import { PackReviewScreen } from "@/modules/warehouse-picking/screens/PackReviewScreen";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

// 提交拣货前核对：提交被装箱拦下时进入；同样按文件路径引入，不进拣货模块的入口导出。
export default function WarehousePickingPackReviewRoute() {
  const params = useLocalSearchParams();
  return <PackReviewScreen orderGuid={firstParam(params.orderGuid)} />;
}
