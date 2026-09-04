using System.Net;
using System.Security.Claims;
using Dcms.Identity.Domain;
using Dcms.Identity.Forgejo;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Messaging.Email;
using Dcms.Shared.Telemetry;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Dcms.Identity.Endpoints;

/// <summary>
/// Minimal interactive sign-in surface for the authorization-code flow: a plain
/// server-rendered sign in / sign up form plus optional Google SSO. The rich
/// admin UI lives in the SPA, which is redirected here only to establish the
/// cookie. New accounts created through Google are asked to pick a username
/// before the local account is created.
/// </summary>
public static class AccountEndpoints
{
    private const string GoogleScheme = "Google";

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/account/login", async (
            SignInManager<DcmsUser> signInManager, string? returnUrl, string? error) =>
            Results.Content(LoginPage(returnUrl, error, await GoogleEnabledAsync(signInManager)), "text/html"));

        app.MapPost("/account/login", async (
            SignInManager<DcmsUser> signInManager,
            UserManager<DcmsUser> userManager,
            ForgejoUserSync forgejo,
            IAuditRecorder audit,
            DcmsMetrics metrics,
            [FromForm] string email,
            [FromForm] string password,
            [FromForm] string? returnUrl,
            CancellationToken ct) =>
        {
            var result = await signInManager.PasswordSignInAsync(email, password, isPersistent: false, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                // A lockout is a different story from a wrong password — one is a user who
                // mistyped, the other is a pattern worth looking at — so they are separate
                // actions rather than one event with a flag.
                var failed = await userManager.FindByEmailAsync(email);
                var entry = audit.Declare(result.IsLockedOut ? AuditActions.LoginLockedOut : AuditActions.LoginFailed)
                    .Platform()
                    .As(AuditCategory.Auth, result.IsLockedOut ? AuditSeverity.Warning : AuditSeverity.Notice)
                    // The address is recorded even when no account matches: repeated failures
                    // against unknown addresses are exactly what an investigator looks for.
                    .With("email", email)
                    .Failed(result.IsLockedOut ? "locked-out" : "invalid-credentials");
                if (failed is not null)
                {
                    entry.About(failed.Id);
                }

                // Counted as well as recorded. The audit entry answers "who tried"; this
                // answers "is the rate abnormal", which is a question about the shape of the
                // last hour and not about any one attempt. No email or address as a label —
                // that would be both unbounded and a disclosure.
                metrics.Login("password", result.IsLockedOut ? "lockedout" : "failed");

                return Results.Redirect($"/account/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}");
            }

            // Self-heal: login is the one place we have the plaintext for existing users, so
            // it is where a Forgejo mirror that drifted out of band gets repaired.
            //
            // DEFERRED AND CONDITIONAL. Inline, this put an unconditional Forgejo admin-API
            // PATCH on the critical path of every authentication: 421 ms of a 539 ms login,
            // against ~9 ms of Postgres for the sign-in itself. Deferring it to
            // ForgejoSyncWorker took that off the request but still rewrote an unchanged
            // password fifteen seconds later, and still wrote an outbox row here. Neither is
            // needed on a mirror that is already current -- every path that CHANGES a
            // credential syncs inline -- so the common sign-in now does neither.
            // See ForgejoUserSync.DeferLoginResyncAsync for when it does still fire.
            var user = await userManager.FindByEmailAsync(email);
            if (user is not null) await forgejo.DeferLoginResyncAsync(user, password, ct);

            var success = audit.Declare(AuditActions.LoginSucceeded)
                .Platform()
                .As(AuditCategory.Auth)
                .With("method", "password");
            if (user is not null)
            {
                success.About(user.Id);
            }

            metrics.Login("password", "succeeded");

            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).DisableAntiforgery().WithAudit(AuditActions.LoginSucceeded, category: AuditCategory.Auth);

        app.MapGet("/account/register", async (
            SignInManager<DcmsUser> signInManager, string? returnUrl, string? error) =>
            Results.Content(RegisterPage(returnUrl, error, await GoogleEnabledAsync(signInManager)), "text/html"));

        app.MapPost("/account/register", async (
            UserManager<DcmsUser> userManager,
            SignInManager<DcmsUser> signInManager,
            ForgejoUserSync forgejo,
            DcmsMetrics metrics,
            [FromForm] string email,
            [FromForm] string password,
            [FromForm] string confirmPassword,
            [FromForm] string? returnUrl,
            CancellationToken ct) =>
        {
            var googleEnabled = await GoogleEnabledAsync(signInManager);
            if (password != confirmPassword)
            {
                return RegisterError("Passwords do not match.", returnUrl, googleEnabled);
            }

            var user = new DcmsUser
            {
                UserName = email,
                Email = email,
                DisplayName = email,
            };
            var create = await userManager.CreateAsync(user, password);
            if (!create.Succeeded)
            {
                return RegisterError(FirstError(create), returnUrl, googleEnabled);
            }

            // Mirror the new account into Forgejo with the same login + password.
            await forgejo.EnsureAsync(user, password, ct);

            await signInManager.SignInAsync(user, isPersistent: false);
            metrics.Signup("password");
            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).DisableAntiforgery().WithAudit(AuditActions.AccountRegistered, category: AuditCategory.Auth);

        // Forgot password: enter an email, receive a reset link.
        app.MapGet("/account/forgot-password", (string? returnUrl) =>
            Results.Content(ForgotPasswordPage(returnUrl, sent: false, error: null), "text/html"));

        app.MapPost("/account/forgot-password", async (
            HttpContext context,
            UserManager<DcmsUser> userManager,
            IEmailQueue emailQueue,
            ILoggerFactory loggerFactory,
            [FromForm] string email,
            [FromForm] string? returnUrl,
            CancellationToken ct) =>
        {
            email = email?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(email))
            {
                return Results.Content(ForgotPasswordPage(returnUrl, sent: false, "Please enter your email."), "text/html");
            }

            // Only send when the account exists AND has a usable password (Google-only
            // accounts have none). Either way the response is identical so the form
            // never reveals whether an address is registered.
            var user = await userManager.FindByEmailAsync(email);
            if (user is not null && await userManager.HasPasswordAsync(user))
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(user);
                var link = BuildResetLink(context, email, token, returnUrl);
                try
                {
                    await emailQueue.EnqueueAsync(new EmailMessage(
                        Recipients: [email],
                        Subject: "Reset your DCMS password",
                        HtmlBody: ResetEmailBody(link),
                        Purpose: "password-reset"), ct);
                }
                catch (Exception ex)
                {
                    // Don't leak queueing failures to the form (still no enumeration);
                    // log so the operator can see it. Delivery problems past this point
                    // are email-worker's to retry and report.
                    loggerFactory.CreateLogger("Account").LogError(ex, "Failed to queue password-reset email.");
                }
            }

            return Results.Content(ForgotPasswordPage(returnUrl, sent: true, error: null), "text/html");
        }).DisableAntiforgery().WithAudit(AuditActions.PasswordResetRequested, category: AuditCategory.Auth);

        // Reset password: reached via the emailed link (email + token in the query).
        app.MapGet("/account/reset-password", (string? email, string? token, string? returnUrl) =>
        {
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token))
            {
                return Results.Content(ResetInvalidPage(), "text/html");
            }
            return Results.Content(ResetPasswordPage(email, token, returnUrl, error: null), "text/html");
        });

        app.MapPost("/account/reset-password", async (
            UserManager<DcmsUser> userManager,
            ForgejoUserSync forgejo,
            [FromForm] string email,
            [FromForm] string token,
            [FromForm] string password,
            [FromForm] string confirmPassword,
            [FromForm] string? returnUrl,
            CancellationToken ct) =>
        {
            if (password != confirmPassword)
            {
                return Results.Content(ResetPasswordPage(email, token, returnUrl, "Passwords do not match."), "text/html");
            }

            var user = await userManager.FindByEmailAsync(email);
            if (user is null)
            {
                // Same generic failure as a bad token — don't reveal whether the email exists.
                return Results.Content(ResetPasswordPage(email, token, returnUrl, "This reset link is invalid or has expired."), "text/html");
            }

            var result = await userManager.ResetPasswordAsync(user, token, password);
            if (!result.Succeeded)
            {
                return Results.Content(ResetPasswordPage(email, token, returnUrl, FirstError(result)), "text/html");
            }

            // Propagate the new password to Forgejo so git credentials stay in sync.
            await forgejo.EnsureAsync(user, password, ct);

            return Results.Content(ResetDonePage(returnUrl), "text/html");
        }).DisableAntiforgery().WithAudit(AuditActions.PasswordResetCompleted, category: AuditCategory.Auth);

        // Google SSO: kick off the challenge, then handle the callback.
        app.MapGet("/account/external/google", (
            SignInManager<DcmsUser> signInManager, string? returnUrl) =>
        {
            var callback = "/account/external/callback?returnUrl=" + Uri.EscapeDataString(returnUrl ?? "/");
            var properties = signInManager.ConfigureExternalAuthenticationProperties(GoogleScheme, callback);
            return Results.Challenge(properties, [GoogleScheme]);
        });

        app.MapGet("/account/external/callback", async (
            SignInManager<DcmsUser> signInManager, string? returnUrl) =>
        {
            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
            {
                return Results.Redirect($"/account/login?error=external&returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}");
            }

            // Already linked → sign straight in.
            var signIn = await signInManager.ExternalLoginSignInAsync(
                info.LoginProvider, info.ProviderKey, isPersistent: false, bypassTwoFactor: true);
            if (signIn.Succeeded)
            {
                return Results.Redirect(SafeReturnUrl(returnUrl));
            }

            // New user → ask for a username before creating the local account. The
            // external cookie set during the challenge still carries the provider
            // info, so GetExternalLoginInfoAsync works again on the POST below.
            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            var suggested = SuggestUsername(email, info.Principal.FindFirstValue(ClaimTypes.Name));
            return Results.Content(CompleteExternalPage(returnUrl, email, suggested, error: null), "text/html");
        });

        app.MapPost("/account/external/complete", async (
            UserManager<DcmsUser> userManager,
            SignInManager<DcmsUser> signInManager,
            ForgejoUserSync forgejo,
            [FromForm] string username,
            [FromForm] string? returnUrl,
            CancellationToken ct) =>
        {
            var info = await signInManager.GetExternalLoginInfoAsync();
            if (info is null)
            {
                return Results.Redirect($"/account/login?error=external&returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}");
            }

            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            username = username?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(username))
            {
                return Results.Content(CompleteExternalPage(returnUrl, email, username, "Please choose a username."), "text/html");
            }
            if (await userManager.FindByNameAsync(username) is not null)
            {
                return Results.Content(CompleteExternalPage(returnUrl, email, username, "That username is already taken."), "text/html");
            }

            var user = new DcmsUser
            {
                UserName = username,
                Email = email,
                EmailConfirmed = true, // Google has verified the address.
                DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name) ?? username,
            };
            var create = await userManager.CreateAsync(user);
            if (!create.Succeeded)
            {
                return Results.Content(CompleteExternalPage(returnUrl, email, username, FirstError(create)), "text/html");
            }

            var link = await userManager.AddLoginAsync(user, info);
            if (!link.Succeeded)
            {
                return Results.Content(CompleteExternalPage(returnUrl, email, username, FirstError(link)), "text/html");
            }

            // Mirror into Forgejo with no git password (Google-only account). The Web IDE
            // prompts them to set a password or add an SSH key before cloning.
            await forgejo.EnsureAsync(user, password: null, ct);

            await signInManager.SignInAsync(user, isPersistent: false);
            return Results.Redirect(SafeReturnUrl(returnUrl));
        }).DisableAntiforgery().WithAudit(AuditActions.SsoLinked, category: AuditCategory.Auth);

        return app;
    }

    private static async Task<bool> GoogleEnabledAsync(SignInManager<DcmsUser> signInManager)
    {
        var schemes = await signInManager.GetExternalAuthenticationSchemesAsync();
        return schemes.Any(s => s.Name == GoogleScheme);
    }

    private static IResult RegisterError(string message, string? returnUrl, bool googleEnabled) =>
        Results.Content(RegisterPage(returnUrl, message, googleEnabled), "text/html");

    private static string FirstError(IdentityResult result) =>
        result.Errors.FirstOrDefault()?.Description ?? "Something went wrong. Please try again.";

    private static string SuggestUsername(string? email, string? name)
    {
        var local = email?.Split('@').FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(local)) return local;
        return name?.Replace(" ", string.Empty) ?? string.Empty;
    }

    // Prevent open redirects: only allow local paths.
    private static string SafeReturnUrl(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
            ? returnUrl
            : "/";

    private static string LoginPage(string? returnUrl, string? error, bool googleEnabled)
    {
        var errorBlock = error is null ? string.Empty : ErrorBlock("Invalid email or password.");
        var body = $$"""
            <form method="post" action="/account/login">
              <h1>Sign in to DCMS</h1>
              {{errorBlock}}
              <input type="hidden" name="returnUrl" value="{{Enc(returnUrl)}}" />
              <label for="email">Email</label>
              <input id="email" name="email" type="email" autocomplete="username" required autofocus />
              <label for="password">Password</label>
              <input id="password" name="password" type="password" autocomplete="current-password" required />
              <p class="forgot"><a href="/account/forgot-password{{QueryReturn(returnUrl)}}">Forgot password?</a></p>
              <button type="submit">Sign in</button>
              {{GoogleBlock(googleEnabled, returnUrl)}}
              <p class="alt">Don't have an account? <a href="/account/register{{QueryReturn(returnUrl)}}">Sign up</a></p>
            </form>
            """;
        return Layout("DCMS — Sign in", body);
    }

    private static string RegisterPage(string? returnUrl, string? error, bool googleEnabled)
    {
        var errorBlock = error is null ? string.Empty : ErrorBlock(error);
        var body = $$"""
            <form method="post" action="/account/register">
              <h1>Create your DCMS account</h1>
              {{errorBlock}}
              <input type="hidden" name="returnUrl" value="{{Enc(returnUrl)}}" />
              <label for="email">Email</label>
              <input id="email" name="email" type="email" autocomplete="username" required autofocus />
              <label for="password">Password</label>
              <input id="password" name="password" type="password" autocomplete="new-password" minlength="10" required />
              <label for="confirmPassword">Confirm password</label>
              <input id="confirmPassword" name="confirmPassword" type="password" autocomplete="new-password" minlength="10" required />
              <button type="submit">Sign up</button>
              {{GoogleBlock(googleEnabled, returnUrl)}}
              <p class="alt">Already have an account? <a href="/account/login{{QueryReturn(returnUrl)}}">Sign in</a></p>
            </form>
            """;
        return Layout("DCMS — Sign up", body);
    }

    private static string CompleteExternalPage(string? returnUrl, string? email, string? username, string? error)
    {
        var errorBlock = error is null ? string.Empty : ErrorBlock(error);
        var emailBlock = string.IsNullOrEmpty(email)
            ? string.Empty
            : $"<label>Email</label><input type=\"email\" value=\"{Enc(email)}\" disabled />";
        var body = $$"""
            <form method="post" action="/account/external/complete">
              <h1>Choose a username</h1>
              <p class="lead">You're almost done. Pick a username to finish creating your account.</p>
              {{errorBlock}}
              <input type="hidden" name="returnUrl" value="{{Enc(returnUrl)}}" />
              {{emailBlock}}
              <label for="username">Username</label>
              <input id="username" name="username" type="text" autocomplete="username" value="{{Enc(username)}}" required autofocus />
              <button type="submit">Finish sign up</button>
            </form>
            """;
        return Layout("DCMS — Choose a username", body);
    }

    private static string ForgotPasswordPage(string? returnUrl, bool sent, string? error)
    {
        if (sent)
        {
            var back = $"<p class=\"alt\"><a href=\"/account/login{QueryReturn(returnUrl)}\">Back to sign in</a></p>";
            return Layout("DCMS — Check your email", $$"""
                <form>
                  <h1>Check your email</h1>
                  <p class="notice">If an account exists for that email, we've sent a link to reset your password. The link expires shortly.</p>
                  {{back}}
                </form>
                """);
        }

        var errorBlock = error is null ? string.Empty : ErrorBlock(error);
        var body = $$"""
            <form method="post" action="/account/forgot-password">
              <h1>Reset your password</h1>
              <p class="lead">Enter your email and we'll send you a link to reset your password.</p>
              {{errorBlock}}
              <input type="hidden" name="returnUrl" value="{{Enc(returnUrl)}}" />
              <label for="email">Email</label>
              <input id="email" name="email" type="email" autocomplete="username" required autofocus />
              <button type="submit">Send reset link</button>
              <p class="alt"><a href="/account/login{{QueryReturn(returnUrl)}}">Back to sign in</a></p>
            </form>
            """;
        return Layout("DCMS — Reset your password", body);
    }

    private static string ResetPasswordPage(string email, string token, string? returnUrl, string? error)
    {
        var errorBlock = error is null ? string.Empty : ErrorBlock(error);
        var body = $$"""
            <form method="post" action="/account/reset-password">
              <h1>Choose a new password</h1>
              {{errorBlock}}
              <input type="hidden" name="email" value="{{Enc(email)}}" />
              <input type="hidden" name="token" value="{{Enc(token)}}" />
              <input type="hidden" name="returnUrl" value="{{Enc(returnUrl)}}" />
              <label for="password">New password</label>
              <input id="password" name="password" type="password" autocomplete="new-password" minlength="10" required autofocus />
              <label for="confirmPassword">Confirm password</label>
              <input id="confirmPassword" name="confirmPassword" type="password" autocomplete="new-password" minlength="10" required />
              <button type="submit">Reset password</button>
            </form>
            """;
        return Layout("DCMS — Choose a new password", body);
    }

    private static string ResetDonePage(string? returnUrl)
    {
        var body = $$"""
            <form>
              <h1>Password updated</h1>
              <p class="notice">Your password has been reset. You can now sign in with your new password.</p>
              <p class="alt"><a href="/account/login{{QueryReturn(returnUrl)}}">Go to sign in</a></p>
            </form>
            """;
        return Layout("DCMS — Password updated", body);
    }

    private static string ResetInvalidPage()
    {
        var body = """
            <form>
              <h1>Invalid reset link</h1>
              <p class="lead">This password-reset link is invalid or has expired. Please request a new one.</p>
              <p class="alt"><a href="/account/forgot-password">Request a new link</a></p>
            </form>
            """;
        return Layout("DCMS — Invalid reset link", body);
    }

    // Builds the absolute reset link from the current request. Behind the edge the
    // ForwardedHeaders middleware makes Scheme/Host the public origin
    // (https://admin.highgeek.eu), so the link is externally clickable. The token
    // contains base64 characters, so both it and the email are URL-encoded.
    private static string BuildResetLink(HttpContext context, string email, string token, string? returnUrl)
    {
        var origin = $"{context.Request.Scheme}://{context.Request.Host}";
        var url = $"{origin}/account/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        if (!string.IsNullOrEmpty(returnUrl))
        {
            url += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
        }
        return url;
    }

    private static string ResetEmailBody(string link) => $$"""
        <div style="font-family:system-ui,sans-serif;color:#0f172a;line-height:1.5">
          <h2 style="font-size:1.1rem">Reset your DCMS password</h2>
          <p>We received a request to reset your password. Click the button below to choose a new one. If you didn't request this, you can safely ignore this email.</p>
          <p style="margin:1.5rem 0">
            <a href="{{Enc(link)}}" style="background:#0f172a;color:#fff;padding:.65rem 1.25rem;border-radius:.375rem;text-decoration:none;display:inline-block">Reset password</a>
          </p>
          <p style="font-size:.85rem;color:#475569">Or paste this link into your browser:<br /><a href="{{Enc(link)}}">{{Enc(link)}}</a></p>
        </div>
        """;

    private static string GoogleBlock(bool enabled, string? returnUrl)
    {
        if (!enabled) return string.Empty;
        return $$"""
            <div class="divider"><span>or</span></div>
            <a class="google" href="/account/external/google{{QueryReturn(returnUrl)}}">
              <svg width="18" height="18" viewBox="0 0 48 48" aria-hidden="true"><path fill="#EA4335" d="M24 9.5c3.54 0 6.71 1.22 9.21 3.6l6.85-6.85C35.9 2.38 30.47 0 24 0 14.62 0 6.51 5.38 2.56 13.22l7.98 6.19C12.43 13.72 17.74 9.5 24 9.5z"/><path fill="#4285F4" d="M46.98 24.55c0-1.57-.15-3.09-.38-4.55H24v9.02h12.94c-.58 2.96-2.26 5.48-4.78 7.18l7.73 6c4.51-4.18 7.09-10.36 7.09-17.65z"/><path fill="#FBBC05" d="M10.53 28.59c-.48-1.45-.76-2.99-.76-4.59s.27-3.14.76-4.59l-7.98-6.19C.92 16.46 0 20.12 0 24c0 3.88.92 7.54 2.56 10.78l7.97-6.19z"/><path fill="#34A853" d="M24 48c6.48 0 11.93-2.13 15.89-5.81l-7.73-6c-2.15 1.45-4.92 2.3-8.16 2.3-6.26 0-11.57-4.22-13.47-9.91l-7.98 6.19C6.51 42.62 14.62 48 24 48z"/></svg>
              <span>Continue with Google</span>
            </a>
            """;
    }

    private static string ErrorBlock(string message) => $"<p class=\"error\">{Enc(message)}</p>";

    private static string QueryReturn(string? returnUrl) =>
        string.IsNullOrEmpty(returnUrl) ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl);

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Layout(string title, string body) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>{{Enc(title)}}</title>
          <style>
            body { font-family: system-ui, sans-serif; background:#f1f5f9; display:flex; min-height:100vh; align-items:center; justify-content:center; margin:0; }
            form { background:#fff; padding:2rem; border-radius:.75rem; box-shadow:0 1px 3px rgba(0,0,0,.1); width:20rem; }
            h1 { font-size:1.25rem; margin:0 0 1rem; }
            p.lead { font-size:.85rem; color:#475569; margin:0 0 1rem; }
            label { display:block; font-size:.8rem; color:#475569; margin:.75rem 0 .25rem; }
            input { width:100%; padding:.5rem; border:1px solid #cbd5e1; border-radius:.375rem; box-sizing:border-box; }
            input:disabled { background:#f8fafc; color:#94a3b8; }
            button { margin-top:1.25rem; width:100%; padding:.6rem; background:#0f172a; color:#fff; border:0; border-radius:.375rem; cursor:pointer; font-size:.9rem; }
            button:hover { background:#1e293b; }
            .error { color:#dc2626; font-size:.85rem; margin:.5rem 0 0; }
            .notice { color:#166534; background:#f0fdf4; border:1px solid #bbf7d0; border-radius:.375rem; padding:.75rem; font-size:.85rem; margin:0 0 1rem; }
            p.forgot { text-align:right; font-size:.78rem; margin:.5rem 0 0; }
            p.forgot a { color:#2563eb; text-decoration:none; }
            p.forgot a:hover { text-decoration:underline; }
            .divider { display:flex; align-items:center; text-align:center; color:#94a3b8; font-size:.75rem; margin:1.25rem 0 .75rem; }
            .divider::before, .divider::after { content:""; flex:1; border-bottom:1px solid #e2e8f0; }
            .divider span { padding:0 .75rem; }
            a.google { display:flex; align-items:center; justify-content:center; gap:.5rem; width:100%; padding:.55rem; border:1px solid #cbd5e1; border-radius:.375rem; background:#fff; color:#0f172a; text-decoration:none; font-size:.9rem; box-sizing:border-box; }
            a.google:hover { background:#f8fafc; }
            p.alt { font-size:.8rem; color:#475569; text-align:center; margin:1.25rem 0 0; }
            p.alt a { color:#2563eb; text-decoration:none; }
            p.alt a:hover { text-decoration:underline; }
          </style>
        </head>
        <body>
          {{body}}
        </body>
        </html>
        """;
}
