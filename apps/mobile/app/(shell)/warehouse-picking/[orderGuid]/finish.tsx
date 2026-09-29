import { useLocalSearchParams } from "expo-router";
import { PickFinishScreen } from "@/modules/warehouse-picking";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

export default function WarehousePickingFinishRoute() {
  const params = useLocalSearchParams();
  return <PickFinishScreen orderGuid={firstParam(params.orderGuid)} />;
}
