using System.Net;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

/// <summary>设备激活失败的具体原因文本：状态码、原因码、内层异常都要能带出来。</summary>
public sealed class DeviceActivationFailureDescriberTests
{
    [Theory]
    [InlineData("Device API request failed with HTTP 400.", HttpStatusCode.BadRequest, null, "HTTP 400")]
    [InlineData("Device error.", HttpStatusCode.InternalServerError, "SOME_NEW_CODE", "HTTP 500 · SOME_NEW_CODE")]
    [InlineData("Device activation returned incomplete credentials.", null, null, "Device activation returned incomplete credentials")]
    [InlineData("Device activation returned an unexpected success response.", null, "WEIRD", "WEIRD")]
    public void Rejection_detail_combines_status_and_reason(
        string message,
        HttpStatusCode? status,
        string? errorCode,
        string expected)
    {
        var detail = DeviceActivationFailureDescriber.DescribeRejection(new CatalogApiException(message, status, errorCode));

        Assert.Equal(expected, detail);
    }

    [Fact]
    public void Rejection_detail_is_null_when_there_is_nothing_specific_to_say()
    {
        var detail = DeviceActivationFailureDescriber.DescribeRejection(
            new CatalogApiException("Device API request failed with HTTP", null, null));

        Assert.Null(detail);
    }

    [Fact]
    public void Transport_failure_text_includes_the_innermost_cause()
    {
        var failure = new HttpRequestException(
            "An error occurred while sending the request.",
            new IOException("Unable to read data", new InvalidOperationException("The remote certificate is invalid")));

        Assert.Equal(
            "An error occurred while sending the request. (The remote certificate is invalid)",
            DeviceActivationFailureDescriber.DescribeTransportFailure(failure));
    }

    [Fact]
    public void Transport_failure_text_does_not_repeat_what_the_outer_message_already_says()
    {
        var failure = new HttpRequestException("No such host is known.", new InvalidOperationException("No such host is known."));

        Assert.Equal("No such host is known.", DeviceActivationFailureDescriber.DescribeTransportFailure(failure));
    }

    [Fact]
    public void Client_raised_unavailable_message_is_returned_unchanged()
    {
        var unavailable = new DeviceApiUnavailableException(HttpStatusCode.BadGateway, new IOException("socket closed"));

        Assert.Equal(unavailable.Message, DeviceActivationFailureDescriber.DescribeTransportFailure(unavailable));
    }

    [Fact]
    public void Exception_without_message_falls_back_to_its_type_name()
    {
        Assert.Equal(
            nameof(InvalidOperationException),
            DeviceActivationFailureDescriber.DescribeTransportFailure(new InvalidOperationException("")));
    }

    [Theory]
    [InlineData("ID (ending {0}) is registered.", "ABCDEF0123456789AABBCC", "ID (ending AABBCC) is registered.")]
    [InlineData("ID (ending {0}) is registered.", "abc", "ID (ending abc) is registered.")]
    [InlineData("ID (ending {0}) is registered.", null, "ID (ending ?) is registered.")]
    [InlineData("No placeholder here.", "ABCDEF", "No placeholder here.")]
    public void Hardware_id_suffix_is_filled_into_the_already_registered_message(string template, string? hardwareId, string expected)
    {
        Assert.Equal(expected, DeviceActivationFailureDescriber.ApplyHardwareIdSuffix(template, hardwareId));
    }
}
