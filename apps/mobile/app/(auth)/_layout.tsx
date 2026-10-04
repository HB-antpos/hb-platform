import { Stack } from "expo-router";

export default function AuthLayout() {
  return (
    <Stack screenOptions={{ headerShown: false }}>
      <Stack.Screen name="login" />
      {/* 强制改密时不允许手势返回到未改密的界面。 */}
      <Stack.Screen name="change-password" options={{ gestureEnabled: false }} />
      <Stack.Screen name="reset-password" />
    </Stack>
  );
}
