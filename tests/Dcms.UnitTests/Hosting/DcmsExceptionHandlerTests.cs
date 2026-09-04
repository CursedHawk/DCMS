using Dcms.Shared.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Dcms.UnitTests.Hosting;

/// <summary>
/// What status an unhandled exception becomes.
///
/// <para>This handler used to answer 500 for everything it caught, including the
/// <see cref="BadHttpRequestException"/> that model binding throws for a malformed request
/// body. That is a client error reported as a server fault, and the cost is not cosmetic:
/// every one lands in <c>http_requests_total{status="500"}</c>, which is what
/// <c>dcms:http_error_ratio</c> and the <c>HighErrorRate</c> alert are computed from. A client
/// looping on a bad payload would page somebody about a healthy server.</para>
/// </summary>
public class DcmsExceptionHandlerTests
{
    private static (DcmsExceptionHandler Handler, DefaultHttpContext Context) Build()
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);

        var problemDetails = Substitute.For<IProblemDetailsService>();
        problemDetails.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(new ValueTask<bool>(true));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        // Not a browser: this asserts the API shape rather than the HTML error page.
        context.Request.Headers.Accept = "application/json";

        return (new DcmsExceptionHandler(environment, problemDetails, NullLogger<DcmsExceptionHandler>.Instance),
                context);
    }

    [Fact]
    public async Task A_malformed_request_body_is_the_clients_error()
    {
        var (handler, context) = Build();

        await handler.TryHandleAsync(
            context,
            new BadHttpRequestException("Failed to read parameter from the request body as JSON.", 400),
            TestContext.Current.CancellationToken);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task A_request_too_large_keeps_its_own_status()
    {
        var (handler, context) = Build();

        await handler.TryHandleAsync(
            context,
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge),
            TestContext.Current.CancellationToken);

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
    }

    [Fact]
    public async Task Anything_else_is_still_a_server_fault()
    {
        var (handler, context) = Build();

        await handler.TryHandleAsync(
            context,
            new InvalidOperationException("the database went away"),
            TestContext.Current.CancellationToken);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }
}
