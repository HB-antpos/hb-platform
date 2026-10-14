import { Redirect } from "expo-router";
import { buildFaceHubRoute, resolveLegacyFaceRedirectParams } from "@/modules/navigation/face-hub";

// 旧入口兼容：「抓拍与比对」已并入「人脸比对」合并页的「抓拍」页签；深链、旧通知与历史书签仍落到它。
export default function FaceCaptureResultsRoute() {
  return <Redirect href={buildFaceHubRoute(resolveLegacyFaceRedirectParams("face-capture-results"))} />;
}
