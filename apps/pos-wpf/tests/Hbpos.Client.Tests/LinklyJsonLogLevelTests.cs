using System.Net;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class LinklyJsonLogLevelTests
{
    [Theory]
    [InlineData("failed", null, "Warning")]
    [InlineData("unknown", null, "Warning")]
    [InlineData("timeout", null, "Warning")]
    [InlineData("response", false, "Warning")]
    [InlineData("response", true, "Information")]
    [InlineData("request", null, "Information")]
    [InlineData("event", null, "Information")]
    public void Write_escalates_failures_and_unknown_results_to_warning(string phase, bool? success, string expectedLevel)
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            LinklyJsonLog.Write(
                "LinklyBackend",
                "backend",
                "transaction",
                phase,
                sessionId: "session-123",
                httpStatus: HttpStatusCode.BadGateway,
                success: success,
                elapsedMs: 42);

            var entry = Assert.Single(sink.Entries);
            Assert.Equal(expectedLevel, entry.Level);
            Assert.Equal("session-123", entry.TraceId);
            Assert.Equal(502, entry.StatusCode);
            Assert.Equal(42L, Convert.ToInt64(entry.Properties!["elapsedMs"]));
            Assert.Equal(phase, entry.Properties!["phase"]);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }
    }

    [Fact]
    public void Write_respects_explicit_level_and_passes_exception()
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            LinklyJsonLog.Write(
                "LinklySettlement",
                "backend",
                "settlement",
                "failed",
                success: false,
                exception: new HttpRequestException("network down"),
                level: LinklyLogLevel.Error);
            LinklyJsonLog.Write(
                "LinklyBackend",
                "backend",
                "transaction",
                "poll",
                success: false,
                level: LinklyLogLevel.Information);

            Assert.Collection(
                sink.Entries,
                error =>
                {
                    Assert.Equal("Error", error.Level);
                    Assert.Equal(nameof(HttpRequestException), error.ExceptionType);
                    Assert.Null(error.TraceId);
                },
                information => Assert.Equal("Information", information.Level));
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }
    }

    [Fact]
    public void WriteMessage_with_failed_wording_is_reported_as_warning()
    {
        var sink = new RecordingApplicationLogSink();
        ConsoleLog.ConfigureCenterSink(sink);
        try
        {
            LinklyJsonLog.WriteMessage("LinklyLocal", "local", "settlement failed reason=timeout");

            Assert.Equal("Warning", Assert.Single(sink.Entries).Level);
        }
        finally
        {
            ConsoleLog.ConfigureCenterSink(null);
        }
    }
}
