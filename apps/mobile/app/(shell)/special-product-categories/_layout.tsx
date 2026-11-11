import { Stack } from "expo-router";

export const unstable_settings = {
  initialRouteName: "index",
};

/** 分类列表、分类里的商品、同步流程各自渲染标题栏，因此整组隐藏原生 header。 */
export default function SpecialProductCategoriesStackLayout() {
  return (
    <Stack screenOptions={{ headerShown: false, gestureEnabled: true }}>
      <Stack.Screen name="index" />
      <Stack.Screen name="category" />
      <Stack.Screen name="sync" />
    </Stack>
  );
}
