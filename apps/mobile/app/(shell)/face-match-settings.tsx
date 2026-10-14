import { Redirect } from "expo-router";
import { buildFaceHubRoute, resolveLegacyFaceRedirectParams } from "@/modules/navigation/face-hub";

// 旧入口兼容：「人脸比对设置」已并入「人脸比对」合并页的「设置」页签；深链、旧通知与历史书签仍落到它。
export default function FaceMatchSettingsRoute() {
  return <Redirect href={buildFaceHubRoute(resolveLegacyFaceRedirectParams("face-match-settings"))} />;
}
