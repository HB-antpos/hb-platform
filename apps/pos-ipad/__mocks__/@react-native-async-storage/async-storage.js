// 官方提供的内存实现：Jest 里不触碰原生模块，生产环境使用真机 AsyncStorage。
// 真实包是 ESM 默认导出，这里补成同样的形状，业务代码 `require(...).default` 两边一致。
const mock = require("@react-native-async-storage/async-storage/jest/async-storage-mock");

module.exports = { __esModule: true, default: mock };
