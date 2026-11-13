import { useLocalSearchParams } from "expo-router";
import { PalletScreen } from "@/modules/packing";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

export default function PackingPalletRoute() {
  const params = useLocalSearchParams();
  const palletGuid = firstParam(params.palletGuid);
  return <PalletScreen key={palletGuid} palletGuid={palletGuid} />;
}
