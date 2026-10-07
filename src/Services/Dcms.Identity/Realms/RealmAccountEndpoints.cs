using Dcms.Identity.Endpoints;
using Dcms.Shared.Audit;
using Dcms.Shared.Audit.Http;
using Dcms.Shared.Messaging.Email;
using Microsoft.AspNetCore.Antiforgery;
using Dcms.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static Dcms.Identity.Endpoints.AccountEndpoints;

namespace Dcms.Identity.Realms;

/// <summary>
/// A realm's own sign-in pages, at <c>/realm/{slug}/…</c> (ADR 0022): sign in, forgotten
/// password, reset, accepting an invitation, sign out. Server-rendered in the same shell as the
/// platform's account pages, named for the tenant, and touching only that tenant's realm: no page
/// here ever reads or writes identity's platform cookie.
///
/// <para>There is no sign-up. Accounts come from an invitation, or (UA2) from a provider the
/// tenant trusts.</para>
/// </summary>
public static class RealmAccountEndpoints
{
    public static IEndpointRouteBuilder MapRealmAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // The public sign-in forms: anonymous by nature, guarded by antiforgery, lockout and the
        // links' own signatures.
        var realm = app.MapGroup("/realm/{slug}").AllowAnonymous();

        realm.MapGet("/login", async (string slug, string? returnUrl, string? error, HttpContext http, IAntiforgery antiforgery,
            RealmStore store, IdentityDbContext db, CancellationToken ct) =>
            await store.FindRealmBySlugAsync(slug, ct) is { } r
                ? Html(LoginPage(r, await ProvidersAsync(db, r, ct), returnUrl, error, CsrfField(http, antiforgery)))
                : NotFound());

        realm.MapPost("/login", async (string slug, HttpContext http, RealmStore store, IAuditRecorder audit,
            [FromForm] string email, [FromForm] string password, [FromForm] string? returnUrl, CancellationToken ct) =>
        {
            if (await store.FindRealmBySlugAsync(slug, ct) is not { } r)
            {
                return NotFound();
            }
            if (!r.PasswordEnabled)
            {
                // Passwords are switched off for this realm: the form is gone, and so is the endpoint.
                return Results.Redirect($"/realm/{Uri.EscapeDataString(slug)}/login");
            }
            var (result, user) = await store.PasswordSignInAsync(r.TenantId, email ?? string.Empty, password ?? string.Empty, ct);
            if (result != RealmSignIn.Succeeded)
            {
                var entry = audit.Declare(result == RealmSignIn.LockedOut ? AuditActions.RealmLoginLockedOut : AuditActions.RealmLoginFailed)
                    .InTenant(r.TenantId)
                    .As(AuditCategory.Auth, result == RealmSignIn.LockedOut ? AuditSeverity.Warning : AuditSeverity.Notice)
                    .With("email", email)
                    .Failed(result == RealmSignIn.LockedOut ? "locked-out" : "invalid-credentials");
                if (user is not null)
                {
                    entry.About(user.Id);
                }
                return Results.Redirect($"/realm/{Uri.EscapeDataString(slug)}/login?error=1{ReturnQuery(returnUrl, '&')}");
            }

            await RealmCookies.SignInAsync(http, r.TenantId, RealmStore.CookiePrincipal(user!));
            audit.Declare(AuditActions.RealmLoginSucceeded).InTenant(r.TenantId).As(AuditCategory.Auth)
                .About(user!.Id).With("method", "password");
            return Results.Redirect(Landing(slug, returnUrl));
        }).WithAudit(AuditActions.RealmLoginSucceeded, category: AuditCategory.Auth);

        realm.MapGet("/forgot", async (string slug, string? returnUrl, HttpContext http, IAntiforgery antiforgery, RealmStore store, CancellationToken ct) =>
            await store.FindRealmBySlugAsync(slug, ct) is { } r
                ? Html(ForgotPage(r, returnUrl, sent: false, CsrfField(http, antiforgery)))
                : NotFound());

        realm.MapPost("/forgot", async (string slug, HttpContext http, RealmStore store, IEmailQueue email, IConfiguration configuration,
            IAuditRecorder audit, ILoggerFactory logs, [FromForm] string address, [FromForm] string? returnUrl, CancellationToken ct) =>
        {
            if (await store.FindRealmBySlugAsync(slug, ct) is not { } r)
            {
                return NotFound();
            }
            audit.Declared?.InTenant(r.TenantId);
            // Only an active account with a password, in a realm that still allows passwords, gets
            // a link; the page reads the same either way, so the form says nothing about who has an account.
            if (r.PasswordEnabled && await store.FindByEmailAsync(r.TenantId, address ?? string.Empty, ct) is { Status: RealmUserStatus.Active, PasswordHash: not null } user)
            {
                audit.Declared?.About(user.Id);
                var link = $"{PublicOrigin(configuration)}/realm/{Uri.EscapeDataString(slug)}/reset?token={Uri.EscapeDataString(store.ResetToken(user))}{ReturnQuery(returnUrl, '&')}";
                await SendAsync(email, logs, user.Email, $"Reset your {r.Name} password",
                    LinkEmail($"Reset your {r.Name} password",
                        "We received a request to reset your password. If it was not you, ignore this email.", "Reset password", link),
                    "realm-password-reset", ct);
            }
            return Html(ForgotPage(r, returnUrl, sent: true, string.Empty));
        }).WithAudit(AuditActions.RealmPasswordResetRequested, category: AuditCategory.Auth);

        realm.MapGet("/reset", async (string slug, string? token, string? returnUrl, HttpContext http, IAntiforgery antiforgery,
            RealmStore store, CancellationToken ct) =>
        {
            if (await store.FindRealmBySlugAsync(slug, ct) is not { } r)
            {
                return NotFound();
            }
            return await store.FromResetAsync(r.TenantId, token, ct) is null
                ? Html(InvalidLinkPage(r, "This reset link is invalid or has expired.", $"/realm/{Uri.EscapeDataString(slug)}/forgot"))
                : Html(PasswordPage(r, "Choose a new password", "reset", token!, returnUrl, null, CsrfField(http, antiforgery)));
        });

        realm.MapPost("/reset", async (string slug, HttpContext http, IAntiforgery antiforgery, RealmStore store, IAuditRecorder audit,
            [FromForm] string token, [FromForm] string password, [FromForm] string confirmPassword, [FromForm] string? returnUrl, CancellationToken ct) =>
        {
            if (await store.FindRealmBySlugAsync(slug, ct) is not { } r)
            {
                return NotFound();
            }
            audit.Declared?.InTenant(r.TenantId);
            if (await store.FromResetAsync(r.TenantId, token, ct) is not { } user)
            {
                return Html(InvalidLinkPage(r, "This reset link is invalid or has expired.", $"/realm/{Uri.EscapeDataString(slug)}/forgot"));
            }
            if (Mismatch(password, confirmPassword) is { } problem)
            {
                return Html(PasswordPage(r, "Choose a new password", "reset", token, returnUrl, problem, CsrfField(http, antiforgery)));
            }
            await store.SetPasswordAsync(user, password, ct);
            audit.Declared?.About(user.Id);
            await RealmCookies.SignInAsync(http, r.TenantId, RealmStore.CookiePrincipal(user));
            return Results.Redirect(Landing(slug, returnUrl));
        }).WithAudit(AuditActions.RealmPasswordResetCompleted, category: AuditCategory.Auth);

        realm.MapGet("/invite", async (string slug, string? token, HttpContext http, IAntiforgery antiforgery, RealmStore store,
            IdentityDbContext db, CancellationToken ct) =>
        {
            if (await store.FindRealmBySlugAsync(slug, ct) is not { } r)
            {
                return NotFound();
            }
            if (await store.FromInviteAsync(r.TenantId, token, ct) is not { } user)
            {
                return Html(InvalidLinkPage(r, "This invitation is invalid, already used or has expired. Ask for a new one.", null));
            }
            // Accept with a password, or by signing in through one of the realm's providers.
            var providers = ProviderButtons(r, await ProvidersAsync(db, r, ct), null, token);
            return Html(r.PasswordEnabled
                ? PasswordPage(r, $"Welcome, {user.DisplayName ?? user.Email}", "invite", token!, null, null, CsrfField(http, antiforgery),
                    $"Choose a password to finish setting up your {r.Name} account.", providers)
                : Layout($"{r.Name} — Welcome", $$"""
                    <form>
                      <h1>Welcome, {{Enc(user.DisplayName ?? user.Email)}}</h1>
                      <p class="lead">Sign in with your organisation's account to finish setting up your {{Enc(r.Name)}} access.</p>
                      {{providers}}
                    </form>
                    """));
        });

        realm.MapPost("/invite", async (string slug, HttpContext http, IAntiforgery antiforgery, RealmStore store, IAuditRecorder audit,
            [FromForm] string token, [FromForm] string password, [FromForm] string confirmPassword, CancellationToken ct) =>
        {
            if (await store.FindRealmBySlugAsync(slug, ct) is not { } r)
            {
                return NotFound();
            }
            audit.Declared?.InTenant(r.TenantId);
            if (await store.FromInviteAsync(r.TenantId, token, ct) is not { } user)
            {
                return Html(InvalidLinkPage(r, "This invitation is invalid, already used or has expired. Ask for a new one.", null));
            }
            if (Mismatch(password, confirmPassword) is { } problem)
            {
                return Html(PasswordPage(r, $"Welcome, {user.DisplayName ?? user.Email}", "invite", token, null, problem, CsrfField(http, antiforgery)));
            }
            await store.SetPasswordAsync(user, password, ct);
            audit.Declared?.About(user.Id);
            await RealmCookies.SignInAsync(http, r.TenantId, RealmStore.CookiePrincipal(user));
            return Results.Redirect(Landing(slug, null));
        }).WithAudit(AuditActions.RealmInviteAccepted, category: AuditCategory.Auth);

        realm.MapGet("/", async (string slug, HttpContext http, RealmStore store, CancellationToken ct) =>
        {
            if (await store.FindRealmBySlugAsync(slug, ct) is not { } r)
            {
                return NotFound();
            }
            var session = await RealmCookies.AuthenticateAsync(http, r.TenantId);
            return session.Succeeded
                ? Html(SignedInPage(r, session.Principal!.Identity!.Name ?? string.Empty))
                : Results.Redirect($"/realm/{Uri.EscapeDataString(slug)}/login");
        });

        realm.MapMethods("/logout", ["GET", "POST"], async (string slug, HttpContext http, RealmStore store, IAuditRecorder audit, CancellationToken ct) =>
        {
            if (await store.FindRealmBySlugAsync(slug, ct) is not { } r)
            {
                return NotFound();
            }
            audit.Declared?.InTenant(r.TenantId);
            await RealmCookies.SignOutAsync(http, r.TenantId);
            // Nowhere but the realm's own sign-in: a sign-out link is the easiest one to plant.
            return Results.Redirect($"/realm/{Uri.EscapeDataString(slug)}/login");
        }).WithAudit(AuditActions.RealmLogout, category: AuditCategory.Auth);

        return app;
    }

    /// <summary>
    /// Where a sign-in lands: back to the authorization it interrupted, and nowhere else a link
    /// could name — otherwise the realm's own page.
    /// </summary>
    private static string Landing(string slug, string? returnUrl) =>
        SafeReturnUrl(returnUrl) is var target && target.StartsWith("/connect/authorize?", StringComparison.Ordinal)
            ? target
            : $"/realm/{Uri.EscapeDataString(slug)}/";

    /// <summary>
    /// The origin people reach identity at, for links in email: the configured issuer, never the
    /// request's Host. A link carries a credential, and a Host header is whatever the caller
    /// sent — deriving the origin from it would let anyone have a victim's reset link mailed
    /// pointing at their own server. (Every deployment sets Identity__Issuer.)
    /// </summary>
    /// <exception cref="InvalidOperationException">No issuer is configured.</exception>
    internal static string PublicOrigin(IConfiguration configuration) =>
        configuration["Identity:Issuer"] is { Length: > 0 } issuer
            ? issuer.TrimEnd('/')
            : throw new InvalidOperationException("Identity:Issuer must be configured to send realm links.");

    internal static async Task SendAsync(IEmailQueue email, ILoggerFactory logs, string to, string subject, string html, string purpose, CancellationToken ct)
    {
        try
        {
            await email.EnqueueAsync(new EmailMessage(Recipients: [to], Subject: subject, HtmlBody: html, Purpose: purpose), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never surfaced to the form (no enumeration); delivery past the queue is email-worker's.
            logs.CreateLogger("Realm").LogError(ex, "Failed to queue a realm email ({Purpose}).", purpose);
        }
    }

    internal static string LinkEmail(string title, string text, string action, string link) => $$"""
        <div style="font-family:system-ui,sans-serif;color:#0f172a;line-height:1.5">
          <h2 style="font-size:1.1rem">{{Enc(title)}}</h2>
          <p>{{Enc(text)}}</p>
          <p style="margin:1.5rem 0">
            <a href="{{Enc(link)}}" style="background:#0f172a;color:#fff;padding:.65rem 1.25rem;border-radius:.375rem;text-decoration:none;display:inline-block">{{Enc(action)}}</a>
          </p>
          <p style="font-size:.85rem;color:#475569">Or paste this link into your browser:<br /><a href="{{Enc(link)}}">{{Enc(link)}}</a></p>
        </div>
        """;

    private static string? Mismatch(string? password, string? confirm) =>
        password != confirm ? "Passwords do not match."
        : (password?.Length ?? 0) < RealmStore.MinPasswordLength ? $"Use at least {RealmStore.MinPasswordLength} characters."
        : null;

    private static string ReturnQuery(string? returnUrl, char join) =>
        string.IsNullOrEmpty(returnUrl) ? string.Empty : $"{join}returnUrl={Uri.EscapeDataString(returnUrl)}";

    private static IResult Html(string page) => Results.Content(page, "text/html");

    private static IResult NotFound() => Results.Content(Layout("Not found", """
        <form><h1>Not found</h1><p class="lead">There is no sign-in here. Check the address you were given.</p></form>
        """), "text/html", statusCode: StatusCodes.Status404NotFound);

    private static string LoginPage(Realm realm, IReadOnlyList<RealmProvider> providers, string? returnUrl, string? error, string csrf)
    {
        var slug = Uri.EscapeDataString(realm.Slug);
        var problem = error switch
        {
            null => string.Empty,
            "sso" => ErrorBlock("Signing in through that provider did not work. Try again, or use another way in."),
            _ => ErrorBlock("Invalid email or password."),
        };
        var buttons = ProviderButtons(realm, providers, returnUrl, null);
        if (!realm.PasswordEnabled)
        {
            return Layout($"{realm.Name} — Sign in", $$"""
                <form>
                  <h1>Sign in to {{Enc(realm.Name)}}</h1>
                  {{problem}}
                  {{(providers.Count == 0 ? "<p class=\"lead\">There is no way to sign in here yet. Ask an administrator.</p>" : buttons)}}
                </form>
                """);
        }
        return Layout($"{realm.Name} — Sign in", $$"""
            <form method="post" action="/realm/{{slug}}/login">
              {{csrf}}
              <h1>Sign in to {{Enc(realm.Name)}}</h1>
              {{problem}}
              <input type="hidden" name="returnUrl" value="{{Enc(returnUrl)}}" />
              <label for="email">Email</label>
              <input id="email" name="email" type="email" autocomplete="username" required autofocus />
              <label for="password">Password</label>
              <input id="password" name="password" type="password" autocomplete="current-password" required />
              <p class="forgot"><a href="/realm/{{slug}}/forgot{{ReturnQuery(returnUrl, '?')}}">Forgot password?</a></p>
              <button type="submit">Sign in</button>
              {{(providers.Count == 0 ? string.Empty : "<div class=\"divider\"><span>or</span></div>" + buttons)}}
            </form>
            """);
    }

    private static Task<List<RealmProvider>> ProvidersAsync(IdentityDbContext db, Realm realm, CancellationToken ct) =>
        db.RealmProviders.AsNoTracking().Where(p => p.TenantId == realm.TenantId && p.Enabled).OrderBy(p => p.DisplayName).ToListAsync(ct);

    /// <summary>One button per provider, out to <c>/realm/{slug}/sso/{key}</c>.</summary>
    private static string ProviderButtons(Realm realm, IReadOnlyList<RealmProvider> providers, string? returnUrl, string? invite) =>
        string.Concat(providers.Select(p =>
        {
            var href = $"/realm/{Uri.EscapeDataString(realm.Slug)}/sso/{Uri.EscapeDataString(p.Key)}"
                       + (invite is null ? ReturnQuery(returnUrl, '?') : $"?invite={Uri.EscapeDataString(invite)}");
            return $"<a class=\"google\" href=\"{Enc(href)}\"><span>Continue with {Enc(p.DisplayName)}</span></a>";
        }));

    private static string ForgotPage(Realm realm, string? returnUrl, bool sent, string csrf)
    {
        var slug = Uri.EscapeDataString(realm.Slug);
        var back = $"<p class=\"alt\"><a href=\"/realm/{slug}/login{ReturnQuery(returnUrl, '?')}\">Back to sign in</a></p>";
        return sent
            ? Layout($"{realm.Name} — Check your email", $$"""
                <form>
                  <h1>Check your email</h1>
                  <p class="notice">If an account exists for that email, we've sent a link to reset its password. The link expires in two hours.</p>
                  {{back}}
                </form>
                """)
            : Layout($"{realm.Name} — Reset your password", $$"""
                <form method="post" action="/realm/{{slug}}/forgot">
                  {{csrf}}
                  <h1>Reset your password</h1>
                  <p class="lead">Enter your email and we'll send you a link to choose a new {{Enc(realm.Name)}} password.</p>
                  <input type="hidden" name="returnUrl" value="{{Enc(returnUrl)}}" />
                  <label for="address">Email</label>
                  <input id="address" name="address" type="email" autocomplete="username" required autofocus />
                  <button type="submit">Send reset link</button>
                  {{back}}
                </form>
                """);
    }

    private static string PasswordPage(Realm realm, string title, string action, string token, string? returnUrl, string? error, string csrf,
        string? lead = null, string? providers = null) => Layout($"{realm.Name} — {title}", $$"""
        <form method="post" action="/realm/{{Uri.EscapeDataString(realm.Slug)}}/{{action}}">
          {{csrf}}
          <h1>{{Enc(title)}}</h1>
          {{(lead is null ? string.Empty : $"<p class=\"lead\">{Enc(lead)}</p>")}}
          {{(error is null ? string.Empty : ErrorBlock(error))}}
          <input type="hidden" name="token" value="{{Enc(token)}}" />
          <input type="hidden" name="returnUrl" value="{{Enc(returnUrl)}}" />
          <label for="password">New password</label>
          <input id="password" name="password" type="password" autocomplete="new-password" minlength="{{RealmStore.MinPasswordLength}}" required autofocus />
          <label for="confirmPassword">Confirm password</label>
          <input id="confirmPassword" name="confirmPassword" type="password" autocomplete="new-password" minlength="{{RealmStore.MinPasswordLength}}" required />
          <button type="submit">Save password</button>
          {{(string.IsNullOrEmpty(providers) ? string.Empty : "<div class=\"divider\"><span>or</span></div>" + providers)}}
        </form>
        """);

    private static string InvalidLinkPage(Realm realm, string message, string? retry) => Layout($"{realm.Name} — Link expired", $$"""
        <form>
          <h1>This link does not work</h1>
          <p class="lead">{{Enc(message)}}</p>
          {{(retry is null ? string.Empty : $"<p class=\"alt\"><a href=\"{Enc(retry)}\">Request a new link</a></p>")}}
        </form>
        """);

    private static string SignedInPage(Realm realm, string name) => Layout($"{realm.Name} — Signed in", $$"""
        <form>
          <h1>You're signed in</h1>
          <p class="notice">Signed in to {{Enc(realm.Name)}} as {{Enc(name)}}. You can now open its sites.</p>
          <p class="alt"><a href="/realm/{{Uri.EscapeDataString(realm.Slug)}}/logout">Sign out</a></p>
        </form>
        """);
}
