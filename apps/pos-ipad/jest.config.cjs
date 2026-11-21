/** @type {import("jest").Config} */
module.exports = {
  preset: "react-native",
  testEnvironment: "node",
  // Expo 模块以 ESM 发布；必须由 Jest 转换，避免测试顺序中的 mock 偶然掩盖解析失败。
  // react-native-ble-plx（BLE 扫码枪）同样以 ESM + Flow 源码发布，运行时组装会导入它。
  transformIgnorePatterns: [
    "node_modules/(?!((jest-)?react-native|react-native-ble-plx|@react-native|expo-localization|@hb/pos-.*)/)",
  ],
  moduleNameMapper: {
    "^@/(.*)$": "<rootDir>/src/$1",
    "^@expo/vector-icons$": "<rootDir>/__mocks__/expo-vector-icons.js",
    "^expo-audio$": "<rootDir>/__mocks__/expo-audio.js",
    "\\.wav$": "<rootDir>/__mocks__/sound-asset.js",
  },
};
