using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;

namespace BlazorApp.Api.Features.ProductDescriptions;

/// <summary>
/// 通过官方 Anthropic SDK 让 Claude 看商品图写英文名。
/// 一张图一个请求：不会把 A 图的名字配到 B 商品上，单条失败也只影响自己。
/// </summary>
public sealed class ClaudeProductVisionClient : IProductVisionClient
{
    // 拒答兜底：模型因安全分类器拒答时由服务端按类别换模型重跑（Sonnet 5.5 只支持 "default" 写法）。
    private const string ServerSideFallbackBeta = "server-side-fallback-2026-07-01";

    private const string SystemPrompt =
        """
        You write short English product names for a discount variety store in Australia.
        The name is printed on shelf labels and receipts, so it must be short and plain.

        Look at the product photo and name what the item is, plus its most useful attributes
        (material, colour, size, pack count) when they are visible in the photo or stated in the
        Chinese reference name.

        - Use 3 to 8 words and stay within {MAX_LENGTH} characters.
        - Use Title Case with no trailing punctuation and no quotation marks.
        - Leave out brand names unless the brand is clearly printed on the product.
        - Leave out marketing words such as premium, amazing, best or high quality.
        - When the photo and the reference name disagree, trust the photo for what the item is,
          and the reference name for pack count and size.
        - Use Australian English spelling (colour, grey).
        """;

    private static readonly IReadOnlyDictionary<string, JsonElement> OutputSchema =
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {
              "type": "object",
              "properties": { "name": { "type": "string" } },
              "required": ["name"],
              "additionalProperties": false
            }
            """
        )!;

    private readonly ProductImageDescriptionOptions _options;
    private readonly ILogger<ClaudeProductVisionClient> _logger;
    private readonly Lazy<AnthropicClient?> _client;

    public ClaudeProductVisionClient(
        IOptions<ProductImageDescriptionOptions> options,
        ILogger<ClaudeProductVisionClient> logger
    )
        : this(options, logger, httpClient: null) { }

    /// <summary>测试用：注入 HttpClient 截获真实请求体，核对 SDK 序列化出的字段。</summary>
    internal ClaudeProductVisionClient(
        IOptions<ProductImageDescriptionOptions> options,
        ILogger<ClaudeProductVisionClient> logger,
        HttpClient? httpClient
    )
    {
        _options = options.Value;
        _logger = logger;
        _client = new Lazy<AnthropicClient?>(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.Anthropic.ApiKey))
            {
                return null;
            }

            var apiKey = _options.Anthropic.ApiKey!.Trim();
            // 单条只输出一个短名字，60 秒足够；默认 10 分钟会让一条卡住拖住整批请求。
            return httpClient == null
                ? new AnthropicClient { ApiKey = apiKey, Timeout = TimeSpan.FromSeconds(60) }
                : new AnthropicClient { ApiKey = apiKey, HttpClient = httpClient, MaxRetries = 0 };
        });
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Anthropic.ApiKey);

    public async Task<ProductVisionResult> DescribeAsync(
        ProductImagePayload image,
        string? chineseName,
        int maxLength,
        CancellationToken cancellationToken
    )
    {
        var client = _client.Value;
        if (client == null)
        {
            return new ProductVisionResult(null, "看图服务未配置");
        }

        var referenceText = string.IsNullOrWhiteSpace(chineseName)
            ? "No Chinese reference name is available."
            : $"Chinese reference name: {chineseName.Trim()}";

        var parameters = new MessageCreateParams
        {
            Model = _options.Anthropic.Model,
            // 只输出一个短名字的 JSON，256 足够；撞到上限按失败处理，不写入半截结果。
            MaxTokens = 256,
            Betas = [ServerSideFallbackBeta],
            Fallbacks = new Default(),
            Thinking = BuildThinking(_options.Anthropic.Model),
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                Format = new BetaJsonOutputFormat { Schema = OutputSchema },
            },
            System = SystemPrompt.Replace("{MAX_LENGTH}", maxLength.ToString()),
            Messages =
            [
                new BetaMessageParam
                {
                    Role = Role.User,
                    Content = new List<BetaContentBlockParam>
                    {
                        new BetaImageBlockParam
                        {
                            Source = new BetaBase64ImageSource
                            {
                                Data = Convert.ToBase64String(image.Data),
                                MediaType = MediaType.ImageJpeg,
                            },
                        },
                        new BetaTextBlockParam { Text = referenceText },
                    },
                },
            ],
        };

        try
        {
            var response = await client.Beta.Messages.Create(parameters, cancellationToken);
            return ReadResult(response);
        }
        catch (AnthropicRateLimitException ex)
        {
            _logger.LogWarning(ex, "Claude 看图生成英文名被限流");
            return new ProductVisionResult(null, "看图服务繁忙，请稍后重试");
        }
        catch (AnthropicBadRequestException ex)
        {
            _logger.LogError(ex, "Claude 看图生成英文名请求被拒（400）");
            return new ProductVisionResult(null, "看图请求无效");
        }
        catch (AnthropicApiException ex)
        {
            _logger.LogError(ex, "Claude 看图生成英文名调用失败");
            return new ProductVisionResult(null, "看图服务调用失败");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // 网络中断、超时等非 API 错误：只让这一条退回文字翻译，不拖垮整批请求。
            _logger.LogError(ex, "Claude 看图生成英文名网络异常");
            return new ProductVisionResult(null, "看图服务连接失败");
        }
    }

    /// <summary>
    /// 这个任务不需要思考，按模型选「关思考」的写法（模型名是配置项，写错会 400）：
    /// Sonnet 5.5 不接受 disabled，用 between_tools（没有工具的单次请求里等于不思考）；
    /// Haiku 5.5 在 effort 不高于 high 时可 disabled，但不认 between_tools；
    /// 其他模型（如思考关不掉的 Opus 5.5）不传，由 effort=low 控制深度。
    /// </summary>
    internal static BetaThinkingConfigParam? BuildThinking(string model)
    {
        if (model.Contains("sonnet-5-5", StringComparison.OrdinalIgnoreCase))
        {
            return new BetaThinkingConfigBetweenTools();
        }

        if (model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
        {
            return new BetaThinkingConfigDisabled();
        }

        return null;
    }

    private ProductVisionResult ReadResult(BetaMessage response)
    {
        // 先看 stop_reason：拒答或被截断时 content 不可信，不能当成功写入。
        var stopReason = response.StopReason?.Raw();
        if (stopReason == "refusal")
        {
            _logger.LogWarning("Claude 拒绝为该商品图生成英文名，类别：{Category}", response.StopDetails?.Category);
            return new ProductVisionResult(null, "模型拒绝处理该图片");
        }

        if (stopReason == "max_tokens")
        {
            return new ProductVisionResult(null, "模型输出被截断");
        }

        var text = string.Concat(
            response.Content.Select(block => block.TryPickText(out var textBlock) ? textBlock.Text : string.Empty)
        );
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ProductVisionResult(null, "模型未返回内容");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                return new ProductVisionResult(name.GetString(), null);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Claude 看图结果不是有效 JSON：{Text}", text);
        }

        return new ProductVisionResult(null, "模型返回格式无效");
    }
}
