import { Redirect } from "expo-router";
import { buildFaceHubRoute, resolveLegacyFaceRedirectParams } from "@/modules/navigation/face-hub";

// 旧入口兼容：「不受欢迎人员名单」已并入「人脸比对」合并页的「名单」页签；深链、旧通知与历史书签仍落到它。
// 同目录的 entry-new（提交条目表单）仍是独立页面，不受影响。
export default function FaceUnwelcomeRoute() {
  return <Redirect href={buildFaceHubRoute(resolveLegacyFaceRedirectParams("face-unwelcome"))} />;
}
