import { Stack } from "expo-router";

// 深链或扫码直接进入某只箱 / 某个托盘 / 签收页时，打包首页仍垫在下面，返回能回到列表。
export const unstable_settings = {
  initialRouteName: "index",
};

export default function PackingStackLayout() {
  return <Stack screenOptions={{ headerShown: false, gestureEnabled: true }} />;
}
