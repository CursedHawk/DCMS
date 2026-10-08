using Microsoft.AspNetCore.Http;

namespace Dcms.PluginSdk.Abstractions;

/// <summary>
/// A plugin's say over every request its host serves: after the request's tenant and
/// authentication are known, before any endpoint runs — for a decision over endpoints the plugin
/// does not own (User Authentication's API access, over every instance's <c>/api/{slug}</c>).
/// Registered as a singleton in <see cref="IPlugin.ConfigureServices"/>; the host runs every gate
/// in registration order (<c>UseDcmsPluginGates</c>). The routed endpoint is already known.
/// </summary>
public interface IPluginRequestGate
{
    /// <summary>Calls <paramref name="next"/> to let the request on, or answers it itself.</summary>
    Task InvokeAsync(HttpContext http, RequestDelegate next);
}
