import { useLocalSearchParams } from "expo-router";
import { PalletScreen } from "@/modules/warehouse-packing";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

export default function WarehousePackingPalletRoute() {
  const params = useLocalSearchParams();
  const palletGuid = firstParam(params.palletGuid);
  return <PalletScreen key={palletGuid} palletGuid={palletGuid} />;
}
