import assert from "node:assert/strict";
import Module from "node:module";

async function run() {
  Object.assign(globalThis, { __DEV__: false });

  const mockModule = (name: string, exports: object) => {
    const filename = require.resolve(name);
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };

  // Node 契约测试只验证货柜 API，不加载 Expo 原生运行时。
  mockModule("expo-router", { router: { replace: () => undefined } });
  mockModule("react-native", {
    AppState: { addEventListener: () => ({ remove: () => undefined }) },
    NativeModules: {},
    Platform: {
      OS: "ios",
      select: <T>(values: { ios?: T; default?: T }) =>
        values.ios ?? values.default,
    },
  });
  mockModule("expo-secure-store", {
    getItemAsync: async () => null,
    setItemAsync: async () => undefined,
    deleteItemAsync: async () => undefined,
  });
  mockModule("expo-location", {
    hasStartedLocationUpdatesAsync: async () => false,
    stopLocationUpdatesAsync: async () => undefined,
  });
  mockModule("@react-native-async-storage/async-storage", {
    default: {
      getItem: async () => null,
      setItem: async () => undefined,
      removeItem: async () => undefined,
    },
  });

  const { apiClient } = await import("../../shared/api/client");
  const {
    batchUpdateDetails,
    createNewProductsAndSyncHq,
    getContainerDetailPresence,
    heartbeatContainerDetailPresence,
    previewContainerDetailBatchAction,
    queryContainerProducts,
  } = await import("./api");
  const { buildContainerDetailQuery } = await import("./query");
  const originalPost = apiClient.post;
  const responses = [
    {
      data: {
        totalUpdated: 1,
        totalRequested: 1,
        validationErrors: [
          {
            hguid: "DETAIL-CHINESE",
            field: "英文名称",
            code: "CONTAINS_CHINESE",
            message: "英文名称不能包含中文",
          },
          {
            hguid: "DETAIL-INCOMPLETE",
            field: "英文名称",
            code: "CONTAINS_CHINESE",
          },
        ],
        conflicts: [{
          hguid: "DETAIL-CONFLICT",
          field: "进口价格",
          code: "CONCURRENT_FIELD_UPDATE",
          message: "服务器已更新",
          serverValue: 4.2,
          submittedValue: 4.56,
          currentServerFieldToken: "current-token",
        }],
      },
    },
    {
      data: {
        totalUpdated: 1,
        totalRequested: 1,
      },
    },
    { data: { viewers: [], editors: [] } },
    { data: { viewers: [], editors: [] } },
    { data: { previewToken: "preview-token", affectedCount: 1, fieldSummary: ["进口价格"] } },
    { data: { totalUpdated: 2, totalRequested: 2 } },
  ];
  const requests: { url: string; body: unknown }[] = [];
  const originalGet = apiClient.get;

  apiClient.post = (async (url: string, body: unknown) => {
    requests.push({ url, body });
    const response = responses.shift();
    assert.ok(response, "每次调用都必须有对应的模拟响应");
    return response;
  }) as typeof apiClient.post;
  apiClient.get = (async (url: string) => {
    requests.push({ url, body: undefined });
    const response = responses.shift();
    assert.ok(response, "每次调用都必须有对应的模拟响应");
    return response;
  }) as typeof apiClient.get;

  try {
    const payload = [{
      hguid: "DETAIL-CHINESE",
      英文名称: "Large 草莓",
      进口价格: 4.56,
      expectedServerFieldTokens: { "进口价格": "baseline-token" },
      overrideAcknowledgements: { "进口价格": "current-token" },
    }];
    const validationResult = await batchUpdateDetails("CONTAINER-1", payload);
    assert.deepEqual(
      requests[0],
      {
        url: "/react/v1/containers/CONTAINER-1/batch-update-details",
        body: [{
          HGUID: "DETAIL-CHINESE",
          调整浮率: undefined,
          国内价格: undefined,
          进口价格: 4.56,
          运输成本: undefined,
          商品名称: undefined,
          英文名称: "Large 草莓",
          ClearEnglishName: undefined,
          贴牌价格: undefined,
          单件装箱数: undefined,
          中包数: undefined,
          单件体积: undefined,
          装柜数量: undefined,
          合计装柜体积: undefined,
          合计装柜金额: undefined,
          IsActive: undefined,
          SkipRelatedProductSync: undefined,
          ExpectedServerFieldTokens: { "进口价格": "baseline-token" },
          OverrideAcknowledgements: { "进口价格": "current-token" },
        }],
      },
      "批量更新必须继续调用同一 React 端点并保留现有 payload 映射",
    );
    assert.deepEqual(
      validationResult,
      {
        totalUpdated: 1,
        totalRequested: 1,
        validationErrors: [{
          hguid: "DETAIL-CHINESE",
          field: "英文名称",
          code: "CONTAINS_CHINESE",
          message: "英文名称不能包含中文",
        }],
        conflicts: [{
          hguid: "DETAIL-CONFLICT",
          field: "进口价格",
          code: "CONCURRENT_FIELD_UPDATE",
          message: "服务器已更新",
          serverValue: 4.2,
          submittedValue: 4.56,
          currentServerFieldToken: "current-token",
        }],
      },
      "必须保留完整字段校验错误，并丢弃无法安全展示的不完整条目",
    );

    const legacyResult = await batchUpdateDetails("CONTAINER-1", payload);
    assert.deepEqual(
      legacyResult,
      {
        totalUpdated: 1,
        totalRequested: 1,
        validationErrors: [],
        conflicts: [],
      },
      "旧响应缺少 validationErrors 时必须兼容为空数组",
    );

    const presence = await getContainerDetailPresence("CONTAINER-1");
    assert.deepEqual(requests[2], {
      url: "/react/v1/containers/CONTAINER-1/editing-presence",
      body: undefined,
    }, "活动用户查询必须走容器隔离端点");
    assert.deepEqual(presence, { viewers: [], editors: [] });

    await heartbeatContainerDetailPresence("CONTAINER-1", {
      clientSessionId: "mobile-session",
      state: "editing",
    });
    assert.deepEqual(requests[3], {
      url: "/react/v1/containers/CONTAINER-1/editing-presence/heartbeat",
      body: { clientSessionId: "mobile-session", state: "editing" },
    }, "心跳必须发送独立客户端会话和查看/编辑状态");

    const preview = await previewContainerDetailBatchAction("CONTAINER-1", {
      operation: "apply-prices",
      scope: { selectedHguids: ["DETAIL-1"] },
      parameters: { importPrice: 4.5 },
    });
    assert.deepEqual(requests[4], {
      url: "/react/v1/containers/CONTAINER-1/actions/preview",
      body: {
        operation: "apply-prices",
        scope: { selectedHguids: ["DETAIL-1"] },
        parameters: { importPrice: 4.5 },
      },
    }, "批量执行前必须先获取服务器签名预览令牌");
    assert.equal(preview.previewToken, "preview-token");

    await batchUpdateDetails("CONTAINER-1", [
      { hguid: "DETAIL-A", 国内价格: 8.8, expectedServerFieldTokens: { "国内价格": "price-token" } },
      { hguid: "DETAIL-B", 进口价格: 4.4, expectedServerFieldTokens: { "进口价格": "import-token" } },
    ]);
    const differentFieldBody = requests[5]?.body as Record<string, unknown>[];
    assert.deepEqual(differentFieldBody.map((item) => item.ExpectedServerFieldTokens), [
      { "国内价格": "price-token" },
      { "进口价格": "import-token" },
    ], "不同字段并发编辑时，客户端必须只提交各自字段的基线令牌，允许服务器自动合并");

    apiClient.post = (async () => {
      throw {
        response: {
          status: 428,
          data: { code: "CONCURRENCY_TOKEN_REQUIRED", message: "请升级" },
        },
      };
    }) as typeof apiClient.post;
    await assert.rejects(
      () => batchUpdateDetails("CONTAINER-1", payload),
      (error: unknown) => error instanceof Error && (error as Error & { code?: string }).code === "CONCURRENCY_TOKEN_REQUIRED",
      "旧客户端缺少令牌时必须保留稳定升级错误码",
    );

    // -----------------------------------------------------------------------
    // 明细查询：只调 products/query 一次，匹配字段直接取自返回行，不再调用 detect
    // -----------------------------------------------------------------------
    const calls: { method: "post" | "get"; url: string; body: any }[] = [];
    const queue: unknown[] = [];
    const nextResponse = () => {
      const item = queue.shift();
      assert.ok(item !== undefined, "每次调用都必须有对应的模拟响应");
      if (item instanceof Error) throw item;
      return { data: item };
    };
    apiClient.post = (async (url: string, body: unknown) => {
      calls.push({ method: "post", url, body });
      return nextResponse();
    }) as typeof apiClient.post;
    apiClient.get = (async (url: string) => {
      calls.push({ method: "get", url, body: undefined });
      return nextResponse();
    }) as typeof apiClient.get;
    const resetCalls = () => {
      calls.length = 0;
      queue.length = 0;
    };

    resetCalls();
    queue.push({
      success: true,
      data: {
        items: [{
          hguid: "D1",
          matchType: "supplierItem",
          localProductCode: "LOCAL-1",
          domesticProductCode: "DOM-1",
          hasProductCodeConflict: true,
          conflictReason: "编码不一致",
        }],
        itemsTotal: 120,
        pageNumber: 2,
        pageSize: 50,
        hasMore: true,
        tagStats: { all: 120, new: 4, existing: 116 },
      },
    });
    const queried = await queryContainerProducts(
      "CONTAINER-1",
      buildContainerDetailQuery("CONTAINER-1", { pageNumber: 2 }),
    );
    assert.equal(calls.length, 1, "翻页只发一次请求，不再额外调用 product-warehouse/detect");
    assert.equal(calls[0]?.url, "/react/v1/containers/CONTAINER-1/products/query");
    assert.ok(calls.every((call) => !call.url.includes("detect")));
    assert.equal(calls[0]?.body.pageSize, 50);
    assert.equal(calls[0]?.body.pageNumber, 2);
    assert.equal(calls[0]?.body.includeTotal, true);
    assert.equal(calls[0]?.body.includeStats, true);
    assert.equal(calls[0]?.body.sortBy, "itemNumber");
    assert.equal(queried.itemsTotal, 120);
    assert.equal(queried.items[0]?.matchType, "supplierItem");
    assert.equal(queried.items[0]?.hasProductCodeConflict, true);
    assert.equal(queried.items[0]?.localProductCode, "LOCAL-1");
    assert.equal(queried.items[0]?.domesticProductCode, "DOM-1");
    assert.equal(queried.tagStats.new, 4);

    // -----------------------------------------------------------------------
    // 创建新商品 + 同步 HQ
    // -----------------------------------------------------------------------
    const newRow = (hguid: string, productCode: string, price?: number) => ({
      hguid,
      商品编码: productCode,
      是否新商品: true,
      贴牌价格: price,
      商品信息: { 货号: `IT-${hguid}`, localSupplierCode: "200" },
    });
    const createdJob = (extra: Record<string, unknown> = {}) => ({
      success: true,
      data: {
        jobId: "CREATE-JOB",
        status: "Succeeded",
        result: { createdCount: 1, created: [{ detailHguid: "D1", productCode: "P1" }] },
        ...extra,
      },
    });
    const pushJob = { success: true, data: { jobId: "PUSH-JOB", status: "Succeeded", result: { successCount: 1, totalCount: 1 } } };

    // 1) 新品零售价缺失：前置拦截，不调用任何接口
    resetCalls();
    const blocked = await createNewProductsAndSyncHq({
      containerGuid: "CONTAINER-1",
      details: [newRow("D1", "P1", 0), newRow("D2", "P2", 3)],
    });
    assert.equal(blocked.status, "blocked");
    assert.equal(blocked.blockedReason, "MISSING_RETAIL_PRICE");
    assert.deepEqual(blocked.missingRetailPrice.map((row) => row.hguid), ["D1"]);
    assert.equal(calls.length, 0);
    const noDetails = await createNewProductsAndSyncHq({ containerGuid: "CONTAINER-1", details: [] });
    assert.equal(noDetails.blockedReason, "NO_DETAILS");
    assert.equal(calls.length, 0);

    // 2) 默认同步 HQ：先建商品，再按重载后的最新行发送到 HQ（17 个更新字段）
    resetCalls();
    queue.push(createdJob(), pushJob);
    let reloadCount = 0;
    const synced = await createNewProductsAndSyncHq({
      containerGuid: "CONTAINER-1",
      details: [newRow("D1", "P1", 3.5), newRow("D2", "P2", 4)],
      reloadDetails: async () => {
        reloadCount += 1;
        return [{ hguid: "D1", 商品编码: "P1", 是否新商品: false, 商品信息: { 货号: "IT-D1", localSupplierCode: "200" } }];
      },
    });
    assert.deepEqual(calls.map((call) => call.url), [
      "/react/v1/container-products/create-new-products/jobs",
      "/react/v1/products/push-to-hq/jobs",
    ]);
    assert.deepEqual(calls[0]?.body.detailHguids, ["D1", "D2"]);
    assert.equal(calls[0]?.body.operationId, "container-create-products:CONTAINER-1:D1,D2");
    assert.equal(reloadCount, 1);
    assert.deepEqual(calls[1]?.body.productCodes, ["P1"], "只发送本次真正创建成功的商品（D2 未创建）");
    assert.equal(calls[1]?.body.items.length, 1);
    assert.equal(calls[1]?.body.items[0].isNewProduct, false, "取重载后的最新行");
    assert.equal(calls[1]?.body.updateFields.length, 17);
    assert.ok(calls[1]?.body.updateFields.includes("productType"));
    assert.match(calls[1]?.body.operationId, /^container-push-hq:CONTAINER-1:P1:1:/);
    assert.equal(synced.status, "completed");
    assert.equal(synced.push.status, "succeeded");
    assert.equal(synced.job?.result.createdCount, 1);

    // 3) syncToHq=false：只建商品，不重载也不推送
    resetCalls();
    queue.push(createdJob());
    let reloadedWhenDisabled = false;
    const noSync = await createNewProductsAndSyncHq({
      containerGuid: "CONTAINER-1",
      details: [newRow("D1", "P1", 3.5)],
      syncToHq: false,
      reloadDetails: () => {
        reloadedWhenDisabled = true;
        return [];
      },
    });
    assert.equal(calls.length, 1);
    assert.deepEqual(noSync.push, { status: "skipped", reason: "sync-disabled" });
    assert.equal(reloadedWhenDisabled, false);

    // 4) 没有创建成功的商品：不推送
    resetCalls();
    queue.push(createdJob({ result: { createdCount: 0, created: [] } }));
    const nothingCreated = await createNewProductsAndSyncHq({
      containerGuid: "CONTAINER-1",
      details: [newRow("D1", "P1", 3.5)],
    });
    assert.equal(calls.length, 1);
    assert.deepEqual(nothingCreated.push, { status: "skipped", reason: "nothing-created" });

    // 5) 已有推送在途：不并发推送，并给出计划里的 PUSH_BUSY 警告
    resetCalls();
    queue.push(createdJob());
    const busy = await createNewProductsAndSyncHq({
      containerGuid: "CONTAINER-1",
      details: [newRow("D1", "P1", 3.5)],
      isPushInFlight: () => true,
    });
    assert.equal(calls.length, 1);
    assert.deepEqual(busy.push, { status: "skipped", reason: "push-busy" });
    assert.deepEqual(busy.plan?.warnings, [{ code: "PUSH_BUSY" }]);

    // 6) 推送阶段失败不影响创建结果，收敛为 error
    resetCalls();
    queue.push(createdJob(), new Error("HQ 不可用"));
    const pushFailed = await createNewProductsAndSyncHq({
      containerGuid: "CONTAINER-1",
      details: [newRow("D1", "P1", 3.5)],
    });
    assert.equal(pushFailed.status, "completed");
    assert.equal(pushFailed.job?.result.createdCount, 1);
    assert.deepEqual(pushFailed.push, { status: "error", message: "HQ 不可用" });

    // 7) 创建任务排队中：轮询到结束再推送；创建失败（抛错）不会继续推送
    resetCalls();
    queue.push(
      { success: true, data: { jobId: "CREATE-JOB", status: "Queued" } },
      { success: true, data: { jobId: "CREATE-JOB", status: "Running" } },
      createdJob(),
      { success: true, data: { jobId: "PUSH-JOB", status: "Running" } },
      pushJob,
    );
    const polled = await createNewProductsAndSyncHq({
      containerGuid: "CONTAINER-1",
      details: [newRow("D1", "P1", 3.5)],
      pollIntervalMs: 1,
    });
    assert.deepEqual(calls.map((call) => `${call.method}:${call.url}`), [
      "post:/react/v1/container-products/create-new-products/jobs",
      "get:/react/v1/container-products/create-new-products/jobs/CREATE-JOB",
      "get:/react/v1/container-products/create-new-products/jobs/CREATE-JOB",
      "post:/react/v1/products/push-to-hq/jobs",
      "get:/react/v1/products/push-to-hq/jobs/PUSH-JOB",
    ]);
    assert.equal(polled.push.status, "succeeded");
    resetCalls();
    queue.push(new Error("创建任务提交失败"));
    await assert.rejects(
      () => createNewProductsAndSyncHq({ containerGuid: "CONTAINER-1", details: [newRow("D1", "P1", 3.5)] }),
      /创建任务提交失败/,
    );
    assert.equal(calls.length, 1);
  } finally {
    apiClient.post = originalPost;
    apiClient.get = originalGet;
  }

  console.log("containers/api.test.ts: ok");
}

void run();
