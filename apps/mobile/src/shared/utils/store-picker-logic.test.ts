import assert from "node:assert/strict";
import {
  filterStoreOptions,
  groupStoreOptionsByLetter,
  pickMyStoreOptions,
  splitStoreLabel,
  type StoreOption,
} from "./store-picker-logic";

const OPTIONS: StoreOption[] = [
  { value: "g-star", label: "Star Value Taree · 1038" },
  { value: "g-test", label: "Test · 2001" },
  { value: "g-tommy", label: "Tommy Bondi · 2005" },
  { value: "g-wall", label: "Wallsend · 1020" },
  { value: "g-cn", label: "悉尼仓 · 9001" },
];

// 拆分名称与编号：按最后一个分隔符拆，名称里自带「 · 」也不会错位；没有编号时 code 为空。
assert.deepEqual(splitStoreLabel("Wallsend · 1020"), { name: "Wallsend", code: "1020" });
assert.deepEqual(splitStoreLabel("A · B · 7"), { name: "A · B", code: "7" });
assert.deepEqual(splitStoreLabel("无编号"), { name: "无编号", code: "" });

// 搜索：名称、编号都能命中，不区分大小写，空关键词返回全部。
assert.deepEqual(filterStoreOptions(OPTIONS, "  tom ").map((o) => o.value), ["g-tommy"]);
assert.deepEqual(filterStoreOptions(OPTIONS, "1020").map((o) => o.value), ["g-wall"]);
assert.deepEqual(filterStoreOptions(OPTIONS, "").length, OPTIONS.length);
assert.deepEqual(filterStoreOptions(OPTIONS, "zzz"), []);

// 分组：按首字母，非字母归「#」放最后，组内保持原顺序。
assert.deepEqual(
  groupStoreOptionsByLetter(OPTIONS).map((group) => [group.letter, group.items.map((o) => o.value)]),
  [
    ["S", ["g-star"]],
    ["T", ["g-test", "g-tommy"]],
    ["W", ["g-wall"]],
    ["#", ["g-cn"]],
  ],
);
assert.deepEqual(groupStoreOptionsByLetter([]), []);

// 我的主分店：忽略大小写；不在可选列表里的忽略；等于全部可选分店时不显示快捷区。
assert.deepEqual(pickMyStoreOptions(OPTIONS, ["G-WALL", "not-listed"]).map((o) => o.value), ["g-wall"]);
assert.deepEqual(pickMyStoreOptions(OPTIONS, []), []);
assert.deepEqual(pickMyStoreOptions(OPTIONS, OPTIONS.map((o) => o.value)), []);
