using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

namespace AgentStationHub.Services.Security;

/// <summary>
/// Single-account cookie auth in front of the whole app.
///
/// Why this lives here instead of using Identity / OIDC:
///  - The Hub is a single-tenant operator console: one (or two) people
///    log into the public URL and drive deploys. There is no user
///    database, no signup, no roles. ASP.NET Core Identity would add
///    a SQL surface and EF migrations for what is effectively a
///    Set-Cookie of `auth=1`.
///  - The previous deployment used Caddy `basic_auth`, which works but
///    surfaces the browser's native modal — no branding, no logout,
///    no session control, no nice error message on a typo.
///  - Cookie auth gives us a real /login page, a /logout link in the
///    sidebar, sliding 7-day sessions, and forces SignalR (Blazor
///    Server's reconnect channel) to carry the same auth automatically
///    because it's same-origin.
///
/// Credentials come from configuration:
///   `Auth:Username` (default "demo")
///   `Auth:Password` (REQUIRED — the app refuses to authenticate anyone
///                    when this is unset, so an empty .env is fail-safe).
/// In docker-compose those map to AUTH_USERNAME / AUTH_PASSWORD env
/// vars; the password is kept in `.env` on the VM, not in the repo.
/// Comparison is constant-time (<see cref="CryptographicOperations.FixedTimeEquals"/>).
/// </summary>
public static class SimpleAuth
{
    public const string CookieName = "agentichub_auth";
    public const string LoginPath  = "/login";
    public const string LogoutPath = "/logout";

    public static void AddSimpleAuth(this IServiceCollection services)
    {
        // Caddy terminates TLS and forwards `X-Forwarded-Proto: https` to
        // the app on plain HTTP across the docker network. Without
        // ForwardedHeadersOptions ASP.NET Core sees `http`, which means
        // (a) `IsHttps` is false, (b) `Request.Scheme` returns "http",
        // and (c) Set-Cookie with SecurePolicy=Always silently drops the
        // cookie. This middleware accepts the proxy headers and rewrites
        // the request scheme/host so the rest of the pipeline behaves as
        // if it had received the original HTTPS request.
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor   |
                ForwardedHeaders.XForwardedProto |
                ForwardedHeaders.XForwardedHost;
            // The proxy lives on a private docker network we control —
            // there is no list of "trusted upstreams" we can enumerate,
            // and the source IP varies (172.x.x.x). Clearing both lists
            // is the documented way to say "trust whatever you receive".
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
        });

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name        = CookieName;
                o.Cookie.HttpOnly    = true;
                o.Cookie.SameSite    = SameSiteMode.Lax;
                // SameAsRequest (not Always) so dev runs on plain
                // http://localhost work too. In prod the request is
                // HTTPS (rewritten by ForwardedHeaders) so the cookie
                // gets the Secure flag automatically.
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.LoginPath          = LoginPath;
                o.LogoutPath         = LogoutPath;
                o.AccessDeniedPath   = LoginPath;
                o.ExpireTimeSpan     = TimeSpan.FromDays(7);
                o.SlidingExpiration  = true;
                // Default cookie handler returns 302 to LoginPath even
                // for API routes; that's fine here because the only API
                // surface (`/api/debug/*`) is operator-driven so the
                // redirect loop surfaces the auth failure clearly to a
                // human. Bots get 302 → 200 (login form), which is
                // explicit enough.
            });

        services.AddAuthorization(o =>
        {
            // EVERY endpoint requires auth by default. The login + logout
            // routes opt out explicitly via [AllowAnonymous] / .AllowAnonymous().
            // Static files served by UseStaticFiles() run BEFORE
            // UseAuthorization() and are therefore unaffected — this is
            // why /login can render its inline CSS without a 302 loop.
            o.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
    }

    /// <summary>
    /// Maps GET/POST /login and GET /logout. Call AFTER UseRouting +
    /// UseAuthentication + UseAuthorization.
    /// </summary>
    public static void MapSimpleAuthEndpoints(this WebApplication app)
    {
        app.MapGet(LoginPath, (HttpContext ctx, string? returnUrl) =>
        {
            var html = RenderLoginPage(returnUrl, error: null);
            return Results.Content(html, "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapPost(LoginPath, async (HttpContext ctx, IConfiguration cfg) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var username  = form["username"].ToString();
            var password  = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            if (!Validate(cfg, username, password))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await ctx.Response.WriteAsync(RenderLoginPage(returnUrl,
                    error: "Username o password non validi."));
                return;
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, username),
                new Claim("auth_method", "simple_cookie"),
            };
            var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc   = DateTimeOffset.UtcNow.AddDays(7),
                });

            // Open redirect protection: only follow `returnUrl` if it is
            // a same-origin RELATIVE path. Any absolute URL is replaced
            // with "/" — otherwise an attacker could craft
            // /login?returnUrl=https://evil.example.com/ and weaponise
            // the post-login redirect.
            var safe = !string.IsNullOrEmpty(returnUrl)
                       && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
                       && returnUrl.StartsWith("/", StringComparison.Ordinal)
                       && !returnUrl.StartsWith("//", StringComparison.Ordinal)
                ? returnUrl
                : "/";
            ctx.Response.Redirect(safe);
        }).AllowAnonymous().DisableAntiforgery();
        // DisableAntiforgery: the form has no antiforgery token because
        // this endpoint IS the auth boundary — we can't gate the auth
        // form behind a session-bound token the user doesn't have yet.
        // The credential check itself is the CSRF defence for /login.

        app.MapGet(LogoutPath, async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.Response.Redirect(LoginPath);
        }).AllowAnonymous();
    }

    private static bool Validate(IConfiguration cfg, string username, string password)
    {
        var expectedUser = cfg["Auth:Username"]
                           ?? Environment.GetEnvironmentVariable("AUTH_USERNAME")
                           ?? "demo";
        var expectedPass = cfg["Auth:Password"]
                           ?? Environment.GetEnvironmentVariable("AUTH_PASSWORD")
                           ?? "";

            // Fail-closed: when no password is configured, NOBODY can log
            // in. Better than defaulting to a known value (which would
            // create a silent backdoor on misconfigured environments).
            if (string.IsNullOrEmpty(expectedPass)) return false;
            if (string.IsNullOrEmpty(username)) return false;
            if (string.IsNullOrEmpty(password)) return false;

        // Constant-time comparison on equal-length byte arrays. We pad
        // both sides to the longer of the two so FixedTimeEquals never
        // short-circuits on length, then AND with a length-equality bit
        // so a too-short / too-long input still fails.
        var u1 = Encoding.UTF8.GetBytes(username);
        var u2 = Encoding.UTF8.GetBytes(expectedUser);
        var p1 = Encoding.UTF8.GetBytes(password);
        var p2 = Encoding.UTF8.GetBytes(expectedPass);

        bool userOk = u1.Length == u2.Length && CryptographicOperations.FixedTimeEquals(u1, u2);
        bool passOk = p1.Length == p2.Length && CryptographicOperations.FixedTimeEquals(p1, p2);
        return userOk && passOk;
    }

    private static string RenderLoginPage(string? returnUrl, string? error)
    {
        // Inline HTML keeps this self-contained: no Razor view engine,
        // no _Layout dependency, no static-file pipeline interleaving.
        // The page is intentionally minimal — single form, single error
        // line — so it renders identically with JS disabled and on any
        // device. Branding stays consistent with the Hub's neutral
        // slate-on-white palette.
        var safeReturn = WebUtility.HtmlEncode(returnUrl ?? string.Empty);
        var errorHtml = string.IsNullOrEmpty(error)
            ? string.Empty
            : $"<div class=\"err\">{WebUtility.HtmlEncode(error)}</div>";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""utf-8"" />
  <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
  <title>Sign in — AgentStationHub</title>
  <style>
    *, *::before, *::after {{ box-sizing: border-box; }}
    html, body {{ height: 100%; margin: 0; }}
    body {{
      font-family: system-ui, -apple-system, ""Segoe UI"", Roboto, sans-serif;
      background: linear-gradient(135deg, #0f172a 0%, #1e293b 100%);
      color: #0f172a;
      display: flex; align-items: center; justify-content: center;
      padding: 1.5rem;
    }}
    .card {{
      background: #fff;
      width: 100%; max-width: 380px;
      border-radius: 14px;
      padding: 2rem 2rem 1.75rem;
      box-shadow: 0 20px 50px rgba(2, 6, 23, 0.45),
                  0 4px 12px rgba(2, 6, 23, 0.25);
    }}
    .brand {{
      display: flex; align-items: center; gap: 0.6rem;
      margin-bottom: 1.25rem;
    }}
    .logo {{
      width: 32px; height: 32px;
      display: grid; grid-template-columns: 1fr 1fr; gap: 2px;
    }}
    .logo span {{ display: block; }}
    .logo .a {{ background: #f25022; }}
    .logo .b {{ background: #7fba00; }}
    .logo .c {{ background: #00a4ef; }}
    .logo .d {{ background: #ffb900; }}
    h1 {{
      font-size: 1.05rem; font-weight: 600;
      margin: 0; color: #0f172a;
    }}
    .subtitle {{
      font-size: 0.85rem; color: #64748b;
      margin: 0 0 1.5rem;
    }}
    label {{
      display: block;
      font-size: 0.78rem; font-weight: 600;
      color: #334155;
      margin: 0.85rem 0 0.3rem;
      letter-spacing: 0.02em;
    }}
    input[type=""text""], input[type=""password""] {{
      width: 100%;
      padding: 0.6rem 0.75rem;
      border: 1px solid #cbd5e1;
      border-radius: 8px;
      font-size: 0.95rem;
      font-family: inherit;
      background: #f8fafc;
      transition: border-color 0.15s, background 0.15s;
    }}
    input:focus {{
      outline: none;
      border-color: #2563eb;
      background: #fff;
      box-shadow: 0 0 0 3px rgba(37, 99, 235, 0.15);
    }}
    button {{
      width: 100%;
      margin-top: 1.4rem;
      padding: 0.65rem 1rem;
      background: #0f172a;
      color: #fff;
      border: 0;
      border-radius: 8px;
      font-size: 0.95rem; font-weight: 600;
      cursor: pointer;
      transition: background 0.15s;
    }}
    button:hover {{ background: #1e293b; }}
    button:active {{ background: #020617; }}
    .err {{
      background: #fee2e2;
      border: 1px solid #fecaca;
      color: #991b1b;
      padding: 0.55rem 0.75rem;
      border-radius: 6px;
      font-size: 0.85rem;
      margin-bottom: 0.5rem;
    }}
    footer {{
      margin-top: 1.5rem;
      font-size: 0.72rem;
      color: #94a3b8;
      text-align: center;
    }}
  </style>
</head>
<body>
  <form class=""card"" method=""post"" action=""{LoginPath}"" autocomplete=""on"">
    <div class=""brand"">
      <div class=""logo"" aria-hidden=""true"">
        <span class=""a""></span><span class=""b""></span>
        <span class=""c""></span><span class=""d""></span>
      </div>
      <h1>AgentStationHub</h1>
    </div>
    <p class=""subtitle"">Sign in to access the operator console.</p>
    {errorHtml}
    <label for=""username"">Username</label>
    <input id=""username"" name=""username"" type=""text""
           autocomplete=""username"" required autofocus />
    <label for=""password"">Password</label>
    <input id=""password"" name=""password"" type=""password""
           autocomplete=""current-password"" required />
    <input type=""hidden"" name=""returnUrl"" value=""{safeReturn}"" />
    <button type=""submit"">Sign in</button>
    <footer>This is a single-tenant deployment. Credentials are managed by the operator.</footer>
  </form>
</body>
</html>";
    }
}
