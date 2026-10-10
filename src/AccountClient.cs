using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class AccountSession
{
    internal string AccessToken { get; set; }
    internal string RefreshToken { get; set; }

    internal AccountSession Copy() { return new AccountSession { AccessToken = AccessToken, RefreshToken = RefreshToken }; }

    internal static void ValidateToken(string value)
    {
        if (String.IsNullOrEmpty(value) || value.Length > 8192)
            throw new InvalidDataException("登录会话无效，请重新登录。");
        foreach (char c in value)
            if (!(Char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_' && c != '.')
                throw new InvalidDataException("登录会话无效，请重新登录。");
    }

    internal void Validate()
    {
        if (String.IsNullOrEmpty(AccessToken) && String.IsNullOrEmpty(RefreshToken))
            throw new InvalidDataException("登录会话无效，请重新登录。");
        if (!String.IsNullOrEmpty(AccessToken)) ValidateToken(AccessToken);
        if (!String.IsNullOrEmpty(RefreshToken)) ValidateToken(RefreshToken);
    }
}

internal sealed class AccountSnapshot
{
    internal string UserId { get; set; }
    internal string AccountName { get; set; }
    internal decimal Balance { get; set; }
    internal decimal Reserved { get; set; }
    internal decimal AvailableBalance { get { return Balance - Reserved; } }
    internal DateTime UpdatedAt { get; set; }
}

internal sealed class AccountFetchResult
{
    internal AccountSession Session { get; set; }
    internal AccountSnapshot Snapshot { get; set; }
}

internal sealed class AccountAuthenticationException : Exception
{
    internal AccountAuthenticationException() : base("登录已过期，请重新登录。") { }
}

// Website-account cookies are separate from the model API Key and Codex login.
// Every credential-bearing request has a fixed HTTPS origin and rejects redirects.
internal sealed class AccountClient
{
    internal const string AccessCookie = "codex_access";
    internal const string RefreshCookie = "codex_refresh";
    internal const int MaximumResponseBytes = 1024 * 1024;
    private static readonly Uri Origin = new Uri("https://xai-tools.cn/");
    private readonly Func<CookieContainer, HttpMessageHandler> handlerFactory;
    private readonly TimeSpan timeout;
    private readonly SemaphoreSlim sessionGate = new SemaphoreSlim(1, 1);

    internal AccountClient() : this(null, TimeSpan.FromSeconds(15)) { }
    internal AccountClient(Func<CookieContainer, HttpMessageHandler> handlerFactory, TimeSpan timeout)
    { this.handlerFactory = handlerFactory; this.timeout = timeout; }

    internal static HttpClientHandler CreateHandler(CookieContainer cookies)
    {
        return new HttpClientHandler {
            AllowAutoRedirect = false, UseCookies = true, CookieContainer = cookies,
            UseDefaultCredentials = false, SslProtocols = System.Security.Authentication.SslProtocols.Tls12
        };
    }

    private static CookieContainer Cookies(AccountSession session)
    {
        session.Validate();
        var cookies = new CookieContainer();
        if (!String.IsNullOrEmpty(session.AccessToken))
            cookies.Add(Origin, new Cookie(AccessCookie, session.AccessToken, "/") { Secure = true, HttpOnly = true });
        if (!String.IsNullOrEmpty(session.RefreshToken))
            cookies.Add(Origin, new Cookie(RefreshCookie, session.RefreshToken, "/") { Secure = true, HttpOnly = true });
        return cookies;
    }

    private HttpClient Client(CookieContainer cookies)
    {
        return new HttpClient(handlerFactory == null ? CreateHandler(cookies) : handlerFactory(cookies)) {
            Timeout = timeout, MaxResponseContentBufferSize = MaximumResponseBytes
        };
    }

    internal async Task<AccountFetchResult> FetchAsync(AccountSession session, CancellationToken cancellationToken)
    { return await FetchAsync(session, cancellationToken, null).ConfigureAwait(false); }

    internal async Task<AccountFetchResult> FetchAsync(AccountSession session, CancellationToken cancellationToken, Action<AccountSession> sessionChanged)
    {
        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await FetchCoreAsync(session, cancellationToken, sessionChanged).ConfigureAwait(false); }
        finally { sessionGate.Release(); }
    }

    private async Task<AccountFetchResult> FetchCoreAsync(AccountSession session, CancellationToken cancellationToken, Action<AccountSession> sessionChanged)
    {
        var cookies = Cookies(session);
        using (var client = Client(cookies))
        {
            bool refreshed = false;
            if (String.IsNullOrEmpty(session.AccessToken))
            {
                await RefreshAsync(client, cookies, cancellationToken).ConfigureAwait(false);
                UpdateSession(session, cookies, sessionChanged);
                refreshed = true;
            }
            Dictionary<string, object> user = null;
            Dictionary<string, object> wallet = null;
            bool needsRefresh = false;
            try
            {
                user = await GetAsync(client, "api/auth/me", cancellationToken).ConfigureAwait(false);
                wallet = await GetAsync(client, "api/user/wallet", cancellationToken).ConfigureAwait(false);
            }
            catch (ExpiredSessionException error)
            {
                if (error.Revoked || refreshed || String.IsNullOrEmpty(session.RefreshToken))
                    throw new AccountAuthenticationException();
                needsRefresh = true;
            }
            if (needsRefresh)
            {
                await RefreshAsync(client, cookies, cancellationToken).ConfigureAwait(false);
                // Refresh tokens rotate. Persist them before the wallet request,
                // even if the subsequent network request fails.
                UpdateSession(session, cookies, sessionChanged);
                user = await GetAfterRefreshAsync(client, "api/auth/me", cancellationToken).ConfigureAwait(false);
                wallet = await GetAfterRefreshAsync(client, "api/user/wallet", cancellationToken).ConfigureAwait(false);
            }
            AccountSnapshot snapshot = ParseSnapshot(user, wallet);
            return new AccountFetchResult { Snapshot = snapshot, Session = ReadSession(cookies) };
        }
    }

    internal async Task LogoutAsync(AccountSession session, CancellationToken cancellationToken)
    {
        await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await LogoutCoreAsync(session, cancellationToken).ConfigureAwait(false); }
        finally { sessionGate.Release(); }
    }

    private async Task LogoutCoreAsync(AccountSession session, CancellationToken cancellationToken)
    {
        var cookies = Cookies(session);
        using (var client = Client(cookies))
        {
            bool refreshed = false;
            if (String.IsNullOrEmpty(session.AccessToken))
            {
                await RefreshAsync(client, cookies, cancellationToken).ConfigureAwait(false);
                UpdateSession(session, cookies, null);
                refreshed = true;
            }
            while (true)
            {
                bool needsRefresh = false;
                using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, "api/auth/logout")))
                {
                    request.Headers.Add("X-CSRF-Protection", "1");
                    request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode == 401)
                        {
                            if (IsRevoked(await response.Content.ReadAsStringAsync().ConfigureAwait(false))) return;
                            if (refreshed || String.IsNullOrEmpty(session.RefreshToken)) throw new AccountAuthenticationException();
                            needsRefresh = true;
                        }
                        else { response.EnsureSuccessStatusCode(); return; }
                    }
                }
                if (needsRefresh)
                {
                    await RefreshAsync(client, cookies, cancellationToken).ConfigureAwait(false);
                    UpdateSession(session, cookies, null);
                    refreshed = true;
                }
            }
        }
    }

    private static async Task<Dictionary<string, object>> GetAfterRefreshAsync(HttpClient client, string route, CancellationToken token)
    {
        try { return await GetAsync(client, route, token).ConfigureAwait(false); }
        catch (ExpiredSessionException) { throw new AccountAuthenticationException(); }
    }

    private static async Task<Dictionary<string, object>> GetAsync(HttpClient client, string route, CancellationToken token)
    {
        using (HttpResponseMessage response = await client.GetAsync(new Uri(Origin, route), token).ConfigureAwait(false))
        {
            if ((int)response.StatusCode == 401)
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new ExpiredSessionException(IsRevoked(body));
            }
            if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                throw new InvalidDataException("账户接口返回了意外跳转，请稍后重试。");
            response.EnsureSuccessStatusCode();
            return ParseObject(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
    }

    private static async Task RefreshAsync(HttpClient client, CookieContainer cookies, CancellationToken token)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Origin, "api/auth/refresh")))
        {
            request.Headers.Add("X-CSRF-Protection", "1");
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using (HttpResponseMessage response = await client.SendAsync(request, token).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                    throw new AccountAuthenticationException();
                response.EnsureSuccessStatusCode();
                var current = ReadSession(cookies);
                if (String.IsNullOrEmpty(current.AccessToken)) throw new AccountAuthenticationException();
            }
        }
    }

    private static AccountSession ReadSession(CookieContainer cookies)
    {
        var values = cookies.GetCookies(Origin);
        var session = new AccountSession {
            AccessToken = values[AccessCookie] == null ? null : values[AccessCookie].Value,
            RefreshToken = values[RefreshCookie] == null ? null : values[RefreshCookie].Value
        };
        session.Validate();
        return session;
    }

    private static void UpdateSession(AccountSession session, CookieContainer cookies, Action<AccountSession> sessionChanged)
    {
        AccountSession current = ReadSession(cookies);
        session.AccessToken = current.AccessToken;
        session.RefreshToken = current.RefreshToken;
        if (sessionChanged != null) sessionChanged(current.Copy());
    }

    private static bool IsRevoked(string body)
    {
        try
        {
            var parsed = ParseObject(body);
            if (Json.Text(parsed, "code") == "session_revoked") return true;
            object raw;
            var error = parsed.TryGetValue("error", out raw) ? raw as Dictionary<string, object> : null;
            return error != null && Json.Text(error, "code") == "session_revoked";
        }
        catch (InvalidDataException) { return false; }
    }

    internal static Dictionary<string, object> ParseObject(string body)
    {
        if (body == null || body.Length > MaximumResponseBytes) throw new InvalidDataException("账户数据格式无效。");
        try
        {
            var parsed = new JavaScriptSerializer { MaxJsonLength = MaximumResponseBytes, RecursionLimit = 16 }
                .DeserializeObject(body) as Dictionary<string, object>;
            if (parsed == null) throw new InvalidDataException("账户数据格式无效。");
            return parsed;
        }
        catch (ArgumentException) { throw new InvalidDataException("账户数据格式无效。"); }
        catch (InvalidOperationException) { throw new InvalidDataException("账户数据格式无效。"); }
    }

    internal static AccountSnapshot ParseSnapshot(Dictionary<string, object> user, Dictionary<string, object> wallet)
    {
        string id = Json.Text(user, "id");
        string accountName = Json.Text(user, "phone");
        if (String.IsNullOrWhiteSpace(accountName)) accountName = Json.Text(user, "email");
        if (String.IsNullOrWhiteSpace(id) || id.Length > 128 || String.IsNullOrWhiteSpace(accountName) || accountName.Length > 254)
            throw new InvalidDataException("账户接口未返回有效用户。");
        return new AccountSnapshot {
            UserId = id, AccountName = accountName, Balance = MicroCny(wallet, "balanceMicroCny"),
            Reserved = MicroCny(wallet, "reservedMicroCny"), UpdatedAt = DateTime.Now
        };
    }

    private static decimal MicroCny(Dictionary<string, object> wallet, string name)
    {
        object raw;
        decimal value;
        if (wallet == null || !wallet.TryGetValue(name, out raw) || raw == null || raw is bool ||
            !Decimal.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Number, CultureInfo.InvariantCulture, out value) ||
            value != Decimal.Truncate(value) || value < Int64.MinValue || value > Int64.MaxValue)
            throw new InvalidDataException("余额数据无效，请稍后刷新。");
        return value / 1000000M;
    }

    private sealed class ExpiredSessionException : Exception
    {
        internal readonly bool Revoked;
        internal ExpiredSessionException(bool revoked) { Revoked = revoked; }
    }
}
