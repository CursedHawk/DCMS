using System.Net;
using Dcms.Identity.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Dcms.Identity.Endpoints;

/// <summary>
/// Minimal interactive login surface for the authorization-code flow. A plain
/// server-rendered form is enough for the authorization server; the rich admin
/// UI lives in the SPA, which is redirected here only to establish the cookie.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/account/login", (string? returnUrl, string? error) =>
            Results.Content(LoginPage(returnUrl, error), "text/html"));

        app.MapPost("/account/login", async (
            HttpContext context,
            SignInManager<DcmsUser> signInManager,
            [FromForm] string email,
            [FromForm] string password,
            [FromForm] string? returnUrl) =>
        {
            var result = await signInManager.PasswordSignInAsync(email, password, isPersistent: false, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                return Results.Redirect($"/account/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}");
            }

            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).DisableAntiforgery();

        return app;
    }

    // Prevent open redirects: only allow local paths.
    private static string SafeReturnUrl(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";

    private static string LoginPage(string? returnUrl, string? error)
    {
        var encodedReturn = WebUtility.HtmlEncode(returnUrl ?? "/");
        var errorBlock = error is null
            ? string.Empty
            : "<p style=\"color:#dc2626\">Invalid email or password.</p>";

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>DCMS — Sign in</title>
              <style>
                body { font-family: system-ui, sans-serif; background:#f1f5f9; display:flex; min-height:100vh; align-items:center; justify-content:center; margin:0; }
                form { background:#fff; padding:2rem; border-radius:.75rem; box-shadow:0 1px 3px rgba(0,0,0,.1); width:20rem; }
                h1 { font-size:1.25rem; margin:0 0 1rem; }
                label { display:block; font-size:.8rem; color:#475569; margin:.75rem 0 .25rem; }
                input { width:100%; padding:.5rem; border:1px solid #cbd5e1; border-radius:.375rem; box-sizing:border-box; }
                button { margin-top:1.25rem; width:100%; padding:.6rem; background:#0f172a; color:#fff; border:0; border-radius:.375rem; cursor:pointer; }
              </style>
            </head>
            <body>
              <form method="post" action="/account/login">
                <h1>Sign in to DCMS</h1>
                {{errorBlock}}
                <input type="hidden" name="returnUrl" value="{{encodedReturn}}" />
                <label for="email">Email</label>
                <input id="email" name="email" type="email" autocomplete="username" required autofocus />
                <label for="password">Password</label>
                <input id="password" name="password" type="password" autocomplete="current-password" required />
                <button type="submit">Sign in</button>
              </form>
            </body>
            </html>
            """;
    }
}
