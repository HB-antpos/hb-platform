import { Platform } from "react-native";

/** 货号、条码用等宽字体，便于逐位核对。单独成文件，让 order-ui 的纯计算可以在 node 下测试。 */
export const ORDER_MONO_FONT = Platform.select({ ios: "Menlo", android: "monospace", default: "monospace" });
