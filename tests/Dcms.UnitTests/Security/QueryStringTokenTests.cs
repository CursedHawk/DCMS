using Dcms.Shared.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dcms.UnitTests.Security;

/// <summary>
/// The platform console's hub sends its token in the query string, the only place a browser
/// WebSocket can put it. Before <c>AcceptQueryStringTokenFor</c> nothing read it from there and
/// every console handshake was refused; and no route but the hub may take a token that way.
/// </summary>
public class QueryStringTokenTests
{
    [Theory]
    [InlineData("/api/platform/hub/console", "abc")]
    [InlineData("/api/platform/tenants", null)]
    [InlineData("/api/platform/hubbub", null)]
    public async Task Only_the_hub_path_takes_its_token_from_the_query(string path, string? expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDcmsResourceAuthentication(new ConfigurationBuilder().Build());
        services.AcceptQueryStringTokenFor("/api/platform/hub");
        var options = services.BuildServiceProvider()
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.QueryString = new QueryString("?access_token=abc");
        var context = new MessageReceivedContext(
            http, new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)), options);

        await options.Events.OnMessageReceived(context);

        context.Token.Should().Be(expected);
    }
}
