// 离开页面的守卫：有未保存并下发的修改时先确认；保存进行中不允许离开。
import { useRef } from "react";
import { Alert } from "react-native";
import { useNavigation, usePreventRemove } from "@react-navigation/native";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";

export function usePosSettingsLeaveGuard(options: { dirty: boolean; busy: boolean }) {
  const { t } = useAppTranslation(["posSettings", "common"]);
  const navigation = useNavigation();
  const optionsRef = useRef(options);
  optionsRef.current = options;

  usePreventRemove(options.dirty || options.busy, ({ data }) => {
    if (optionsRef.current.busy) {
      Alert.alert(t("leave.busyTitle"), t("leave.busyMessage"), [{ text: t("common:actions.confirm") }]);
      return;
    }
    Alert.alert(t("leave.discardTitle"), t("leave.discardMessage"), [
      { text: t("leave.keepEditing"), style: "cancel" },
      { text: t("leave.discard"), style: "destructive", onPress: () => navigation.dispatch(data.action) },
    ]);
  });
}
