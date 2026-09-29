import { apiClient } from "@/shared/api/client";
import { parseAppInstallLinks } from "./logic";

export async function getAppInstallLinks() {
  // apiClient 的基址已包含 /api。
  const response = await apiClient.get("/mobile-app-install-links");
  return parseAppInstallLinks(response.data);
}
