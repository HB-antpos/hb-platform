import { useLocalSearchParams } from "expo-router";
import { PackLaterScreen } from "@/modules/warehouse-picking/screens/PackLaterScreen";

function firstParam(value: string | string[] | undefined) {
  return Array.isArray(value) ? value[0] : value ?? "";
}

// 先拣后装：独立路由、直接按文件路径引入页面，不放进拣货模块的入口导出，没进这个页面就不加载它。
export default function WarehousePickingPackRoute() {
  const params = useLocalSearchParams();
  const focus = firstParam(params.focus);
  const orderGuid = firstParam(params.orderGuid);
  // 同一张单换定位商品时重建页面，不带上一次的高亮与扫码状态。
  return <PackLaterScreen key={`${orderGuid}:${focus}`} orderGuid={orderGuid} focusProductCode={focus || null} />;
}
