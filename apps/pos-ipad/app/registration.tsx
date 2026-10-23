import { router, type Href } from "expo-router";

import { DeviceRegistrationScreen } from "@/features/device-registration";

export default function RegistrationRoute() {
  return (
    <DeviceRegistrationScreen
      onOpenNetworkCheck={() => router.push("/network-check" as Href)}
    />
  );
}
