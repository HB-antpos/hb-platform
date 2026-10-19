import assert from "node:assert/strict";
import {
  containerNumberFileNamePart,
  formatContainerNumber,
  maskContainerNumber,
} from "./container-number";

// 与后端 ContainerNumberMasking、Web 同一规则：只露后 4 位，其余为 *，保持原长度。
assert.equal(maskContainerNumber("FFAU8228793"), "*******8793");
assert.equal(maskContainerNumber("12345"), "*2345");
assert.equal(maskContainerNumber("8793"), "8793");
assert.equal(maskContainerNumber(""), "");
assert.equal(maskContainerNumber(null), "");
assert.equal(maskContainerNumber(undefined), "");
assert.equal(maskContainerNumber("*******8793"), "*******8793");

assert.equal(formatContainerNumber("FFAU8228793", false), "*******8793");
assert.equal(formatContainerNumber("FFAU8228793", true), "FFAU8228793");
assert.equal(formatContainerNumber(undefined, true), "");

assert.equal(containerNumberFileNamePart("*******8793"), "8793");
assert.equal(containerNumberFileNamePart("FFAU8228793"), "FFAU8228793");

console.log("container-number tests passed");
