import { Stack } from "expo-router";

export const unstable_settings = {
  initialRouteName: "index",
};

export default function PosSettingsStackLayout() {
  return <Stack screenOptions={{ headerShown: false, gestureEnabled: true }} />;
}
