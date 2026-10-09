import assert from "node:assert/strict";
import test from "node:test";

import {
  HbposAdvertisementApi,
  type CustomerDisplayAdvertisementItem,
} from "./advertisement-api";

import type {
  HbposTransport,
  HbposTransportRequest,
} from "@/core/api/hbpos-api";

test("广告 API 使用冻结 active endpoint，并严格规范化当前门店素材", async () => {
  const requests: HbposTransportRequest[] = [];
  const transport: HbposTransport = {
    async request<T>(request: HbposTransportRequest) {
      requests.push(request);
      return {
        status: 200,
        data: {
          success: true,
          data: {
            storeCode: "S001",
            generatedAt: "2026-07-28T00:00:00.000Z",
            items: [
              {
                id: "ad-1",
                title: "Weekend",
                description: null,
                mediaType: "image",
                mediaUrl: "https://cdn.example.com/ad.png",
                thumbnailUrl: null,
                objectKey: "ads/ad.png",
                originalFileName: "ad.png",
                contentType: "image/png",
                fileSize: 1_024,
                effectiveStart: "2026-07-27T00:00:00.000Z",
                effectiveEnd: "2026-07-29T00:00:00.000Z",
                sortOrder: 2,
              },
            ],
          },
        } as T,
      };
    },
  };

  const response = await new HbposAdvertisementApi(transport).getActive(
    " S001 ",
  );

  assert.deepEqual(requests, [
    {
      method: "GET",
      url: "/api/v1/advertisements/active",
      params: { storeCode: "S001", take: 20 },
    },
  ]);
  assert.equal(response.storeCode, "S001");
  assert.deepEqual(response.items[0], {
    id: "ad-1",
    kind: "image",
    remoteUrl: "https://cdn.example.com/ad.png",
    objectKey: "ads/ad.png",
    originalFileName: "ad.png",
    contentType: "image/png",
    fileSize: 1_024,
    effectiveStartIso: "2026-07-27T00:00:00.000Z",
    effectiveEndIso: "2026-07-29T00:00:00.000Z",
    sortOrder: 2,
    // 响应未带 orientation（旧服务端）按 any 处理。
    orientation: "any",
  } satisfies CustomerDisplayAdvertisementItem);
});

test("广告 API 解析 orientation：trim+小写，只认 landscape/portrait，其余一律 any", async () => {
  const cases: readonly [unknown, string][] = [
    ["landscape", "landscape"],
    [" Portrait ", "portrait"],
    ["LANDSCAPE", "landscape"],
    ["any", "any"],
    ["square", "any"],
    ["", "any"],
    ["   ", "any"],
    [null, "any"],
    [undefined, "any"],
    [3, "any"],
  ];
  for (const [raw, expected] of cases) {
    const transport: HbposTransport = {
      async request<T>() {
        return {
          status: 200,
          data: {
            success: true,
            data: {
              storeCode: "S001",
              generatedAt: "2026-07-28T00:00:00.000Z",
              items: [
                {
                  id: "ad-1",
                  mediaType: "image",
                  mediaUrl: "https://cdn.example.com/ad.png",
                  objectKey: "ads/ad.png",
                  originalFileName: "ad.png",
                  contentType: "image/png",
                  fileSize: 1_024,
                  effectiveStart: "2026-07-27T00:00:00.000Z",
                  effectiveEnd: "2026-07-29T00:00:00.000Z",
                  sortOrder: 0,
                  orientation: raw,
                },
              ],
            },
          } as T,
        };
      },
    };
    const response = await new HbposAdvertisementApi(transport).getActive(
      "S001",
    );
    assert.equal(response.items[0]?.orientation, expected, String(raw));
  }
});

test("广告 API 对跨门店、非法类型、非 HTTPS 远端与超限文件 fail-closed", async () => {
  for (const item of [
    {
      mediaType: "html",
      mediaUrl: "https://cdn.example.com/ad.html",
      fileSize: 100,
    },
    {
      mediaType: "image",
      mediaUrl: "http://cdn.example.com/ad.png",
      fileSize: 100,
    },
    {
      mediaType: "video",
      mediaUrl: "https://cdn.example.com/ad.mp4",
      fileSize: 200 * 1024 * 1024 + 1,
    },
  ]) {
    const transport = responseTransport({
      storeCode: "S001",
      generatedAt: "2026-07-28T00:00:00.000Z",
      items: [
        {
          id: "ad-bad",
          title: "Bad",
          description: null,
          thumbnailUrl: null,
          objectKey: "bad",
          originalFileName: "bad",
          contentType: "application/octet-stream",
          effectiveStart: "2026-07-27T00:00:00.000Z",
          effectiveEnd: "2026-07-29T00:00:00.000Z",
          sortOrder: 0,
          ...item,
        },
      ],
    });
    await assert.rejects(
      () => new HbposAdvertisementApi(transport).getActive("S001"),
      /advertisement response/i,
    );
  }

  await assert.rejects(
    () =>
      new HbposAdvertisementApi(
        responseTransport({
          storeCode: "S002",
          generatedAt: "2026-07-28T00:00:00.000Z",
          items: [],
        }),
      ).getActive("S001"),
    /advertisement response/i,
  );
});

function responseTransport(data: unknown): HbposTransport {
  return {
    async request<T>() {
      return {
        status: 200,
        data: { success: true, data } as T,
      };
    },
  };
}
