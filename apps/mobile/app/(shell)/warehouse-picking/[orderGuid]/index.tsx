import { useLocalSearchParams } from "expo-router";
import { PickingScreen } from "@/modules/warehouse-picking";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

export default function WarehousePickingOrderRoute() {
  const params = useLocalSearchParams();
  const focus = firstParam(params.focus);
  return <PickingScreen orderGuid={firstParam(params.orderGuid)} focusDetailGuid={focus || null} />;
}
