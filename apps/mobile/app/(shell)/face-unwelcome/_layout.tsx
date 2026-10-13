import { Stack } from "expo-router";

export const unstable_settings = {
  initialRouteName: "index",
};

/** 列表与提交表单各自渲染自定义标题栏，因此整组隐藏原生 header。 */
export default function FaceUnwelcomeStackLayout() {
  return (
    <Stack screenOptions={{ headerShown: false, gestureEnabled: true }}>
      <Stack.Screen name="index" />
      <Stack.Screen name="entry-new" />
    </Stack>
  );
}
