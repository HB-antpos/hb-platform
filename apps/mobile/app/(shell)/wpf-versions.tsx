import { Redirect } from "expo-router";
import { VersionManagementGuard } from "@/modules/navigation/version-management-guard";

// 旧入口兼容：WPF 版本已并入版本发布中心，深链与历史书签直接落到它的 WPF 终端（对齐 Web 的 /system/wpf-versions 重定向）。
export default function WpfVersionsRoute() {
  return (
    <VersionManagementGuard>
      <Redirect
        href={{ pathname: "/(shell)/app-downloads", params: { view: "wpf" } }}
      />
    </VersionManagementGuard>
  );
}
