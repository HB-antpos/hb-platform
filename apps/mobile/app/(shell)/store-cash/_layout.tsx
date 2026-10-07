import { Stack } from "expo-router";

export const unstable_settings = {
  initialRouteName: "index",
};

export default function StoreCashStackLayout() {
  return <Stack screenOptions={{ headerShown: false, gestureEnabled: true }} />;
}
