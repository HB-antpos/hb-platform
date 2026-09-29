import { Stack } from "expo-router";

// 深链直接进入某张订单时，入口页（确认拣货人 / 订单列表）仍垫在下面，返回能回到列表。
export const unstable_settings = {
  initialRouteName: "index",
};

export default function WarehousePickingStackLayout() {
  return <Stack screenOptions={{ headerShown: false, gestureEnabled: true }} />;
}
