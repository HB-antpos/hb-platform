import { useLocalSearchParams } from "expo-router";
import { CartonScreen } from "@/modules/warehouse-packing";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

export default function WarehousePackingCartonRoute() {
  const params = useLocalSearchParams();
  const cartonGuid = firstParam(params.cartonGuid);
  // 「继续装下一箱」是同一路由换参数：按箱号重建页面，不带上一只箱的扫码状态。
  return <CartonScreen key={cartonGuid} cartonGuid={cartonGuid} />;
}
