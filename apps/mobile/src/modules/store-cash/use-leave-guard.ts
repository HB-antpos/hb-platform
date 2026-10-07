// 离开页面的守卫：表单有未提交内容（尤其是已拍好的照片）时先确认，提交进行中不允许离开。
// 进行中离开会丢掉本次 clientRequestId，重新进入再提交就可能重复记账，所以直接拦住。
import { useRef, type MutableRefObject } from "react";
import { Alert } from "react-native";
import { useNavigation, usePreventRemove } from "@react-navigation/native";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

export function useLeaveGuard(options: {
  dirty: boolean;
  busy: boolean;
  /** 提交成功后置 true，随后的 router.replace 不再被拦截。 */
  allowLeaveRef: MutableRefObject<boolean>;
}) {
  const { t } = useAppTranslation(["storeCash", "common"]);
  const navigation = useNavigation();
  const optionsRef = useRef(options);
  optionsRef.current = options;

  usePreventRemove(options.dirty || options.busy, ({ data }) => {
    const current = optionsRef.current;
    if (current.allowLeaveRef.current) {
      navigation.dispatch(data.action);
      return;
    }
    if (current.busy) {
      Alert.alert(t("leave.busyTitle"), t("leave.busyMessage"), [{ text: t("common:actions.confirm") }]);
      return;
    }
    Alert.alert(t("leave.discardTitle"), t("leave.discardMessage"), [
      { text: t("leave.keepEditing"), style: "cancel" },
      { text: t("leave.discard"), style: "destructive", onPress: () => navigation.dispatch(data.action) },
    ]);
  });
}
