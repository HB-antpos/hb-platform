import { router, type Href } from "expo-router";

import { DeviceRegistrationScreen } from "@/features/device-registration";
import { usePosThemeVersion } from "@/ui/theme";

export default function RegistrationRoute() {
  usePosThemeVersion();
  return (
    <DeviceRegistrationScreen
      onOpenNetworkCheck={() => router.push("/network-check" as Href)}
    />
  );
}
