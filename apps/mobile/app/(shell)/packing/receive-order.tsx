import { useLocalSearchParams } from "expo-router";
import { ReceiveOrderScreen } from "@/modules/packing/screens/ReceiveOrderScreen";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

// 按订单签收：从「签收到货」的扫码结果进入；按文件路径引入，没进这个页面就不加载它。
export default function PackingReceiveOrderRoute() {
  const params = useLocalSearchParams();
  const orderGuid = firstParam(params.orderGuid);
  return <ReceiveOrderScreen key={orderGuid} orderGuid={orderGuid} />;
}
