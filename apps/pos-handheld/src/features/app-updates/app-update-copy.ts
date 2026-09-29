export const appUpdateEnglishCopy = {
  "eyebrow.optional": "NEW VERSION",
  "eyebrow.required": "HB POS UPDATE",
  "optional.nativeTitle": "A new HB POS version is available",
  "optional.otaTitle": "A new HB POS update is available",
  "required.nativeTitle": "Update HB POS to continue",
  "required.otaTitle": "Finish this HB POS update to continue",
  "optional.body":
    "You can update now or continue working and choose Later.",
  "required.body":
    "This terminal is safe to update. Sales pages stay locked until the update finishes.",
  "action.openStore": "Open App Store",
  "action.installOta": "Install update",
  "action.restart": "Restart to update",
  "action.retry": "Retry download",
  "download.running": "Downloading the update in the background. You can keep working.",
  "download.required": "Downloading the required update in the background.",
  "download.failed": "Update download or verification failed. Please retry.",
  "download.apkReady": "Update downloaded and ready to install",
  "download.otaReady": "Update downloaded and ready to restart",
  "download.readyBody": "The update is downloaded. Install now or choose Later. Your current transaction must be completed first.",
  "action.openInstallSettings": "Allow installs",
  "action.working": "Updating…",
  "action.later": "Later",
  "action.settings": "Settings",
  "action.support": "Update support",
  "action.registration": "Device registration",
  "error.notSafe":
    "Finish the current sale or payment recovery before updating.",
  "error.unavailable":
    "The update is temporarily unavailable. Please retry or open update support.",
  "permission.required":
    "Allow HB POS to install unknown apps in Android Settings, then select Install update again.",
} as const;

export type AppUpdateCopyKey = keyof typeof appUpdateEnglishCopy;

export const appUpdateChineseCopy: Record<
  AppUpdateCopyKey,
  string
> = {
  "eyebrow.optional": "发现新版",
  "eyebrow.required": "应用更新",
  "optional.nativeTitle": "发现 HB POS 新版本",
  "optional.otaTitle": "发现 HB POS 新版本",
  "required.nativeTitle": "更新 HB POS 后才能继续",
  "required.otaTitle": "完成 HB POS 更新后才能继续",
  "optional.body": "可以立即更新，也可选择稍后并继续当前工作。",
  "required.body": "当前交易已安全收口，升级完成前业务页面保持锁定。",
  "action.openStore": "打开 App Store",
  "action.installOta": "安装更新",
  "action.restart": "重启更新",
  "action.retry": "重新下载",
  "download.running": "正在后台下载更新，可继续使用",
  "download.required": "正在后台下载必需更新",
  "download.failed": "更新下载或校验失败，请重试",
  "download.apkReady": "更新已下载，可以安装",
  "download.otaReady": "更新已下载，可以重启",
  "download.readyBody": "更新已下载，可立即安装或选择稍后。安装前请完成当前交易。",
  "action.openInstallSettings": "去授权",
  "action.working": "正在更新…",
  "action.later": "稍后",
  "action.settings": "设置",
  "action.support": "更新支持",
  "action.registration": "设备注册",
  "error.notSafe": "请先完成当前交易或支付恢复，再执行更新。",
  "error.unavailable": "更新暂不可用，请重试或打开更新支持。",
  "permission.required": "请在系统设置中允许 HB POS 安装未知应用后，再次点击安装更新。",
};

export function resolveAppUpdateCopy(
  language?: string,
): Readonly<Record<AppUpdateCopyKey, string>> {
  return language?.toLowerCase().startsWith("zh")
    ? appUpdateChineseCopy
    : appUpdateEnglishCopy;
}
