import { Stack } from "expo-router";

export const unstable_settings = {
  initialRouteName: "index",
};

/** 列表与编辑页各自渲染标题栏，因此整组隐藏原生 header。 */
export default function PricingStrategiesStackLayout() {
  return (
    <Stack screenOptions={{ headerShown: false, gestureEnabled: true }}>
      <Stack.Screen name="index" />
      <Stack.Screen name="edit" />
    </Stack>
  );
}
