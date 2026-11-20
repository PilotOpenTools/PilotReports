using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Reports.Infrastructure;
using System.Threading.Tasks;

namespace Reports.Publishing
{
    /// <summary>
    /// Вход в GitLab через браузер: OAuth 2.0 Authorization Code + PKCE для «публичного» приложения
    /// (секрета нет - его нельзя хранить в модуле у всех пользователей).
    /// Браузер возвращает код на локальный адрес (redirect_uri, например http://127.0.0.1:7890/callback),
    /// который модуль слушает на время входа.
    /// </summary>
    internal sealed class GitLabOAuth
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly HttpClient Probe = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(3);

        /// <summary>Открытие адреса в браузере по умолчанию (в тестах подменяется).</summary>
        internal static Action<string> OpenBrowser = url => ExternalLink.Open(url);

        private readonly string _baseUrl;
        private readonly string _clientId;
        private readonly string _redirectUri;

        public GitLabOAuth(string baseUrl, string clientId, string redirectUri)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _clientId = clientId;
            _redirectUri = redirectUri;
        }

        /// <summary>Вход через браузер настроен: есть Application ID и локальный (loopback) redirect_uri.</summary>
        public static bool IsConfigured(string clientId, string redirectUri) =>
            !string.IsNullOrWhiteSpace(clientId) && Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) && uri.IsLoopback;

        /// <summary>Открывает браузер, ждёт возврата и меняет код на токен.</summary>
        public async Task<StoredToken> LoginAsync(CancellationToken ct)
        {
            var redirect = new Uri(_redirectUri);
            var verifier = Base64Url(RandomBytes(32));
            string challenge;
            using (var sha = SHA256.Create())
                challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomBytes(16));

            var listener = new TcpListener(IPAddress.Loopback, redirect.Port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"Порт {redirect.Port} для входа через браузер занят ({ex.Message}). Закройте другое окно входа и повторите.");
            }

            try
            {
                var publicBase = await ResolvePublicBaseUrlAsync().ConfigureAwait(false);
                var authorize = $"{publicBase}/oauth/authorize?client_id={E(_clientId)}&redirect_uri={E(_redirectUri)}&response_type=code" +
                                $"&state={E(state)}&scope=api&code_challenge={E(challenge)}&code_challenge_method=S256";
                OpenBrowser(authorize);

                var query = await WaitForCallbackAsync(listener, redirect.AbsolutePath, ct).ConfigureAwait(false);
                if (query.TryGetValue("error", out var error))
                    throw new InvalidOperationException($"GitLab отказал во входе: {error} {query.GetValueOrDefault("error_description")}");
                if (!query.TryGetValue("state", out var returnedState) || returnedState != state)
                    throw new InvalidOperationException("Ответ GitLab не прошёл проверку (state) - повторите вход.");
                if (!query.TryGetValue("code", out var code))
                    throw new InvalidOperationException("GitLab не вернул код авторизации.");

                return await TokenAsync(new Dictionary<string, string>
                {
                    ["client_id"] = _clientId,
                    ["code"] = code,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = _redirectUri,
                    ["code_verifier"] = verifier,
                }, verifier).ConfigureAwait(false);
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Адрес GitLab, который видит браузер. Если в настройках localhost, а external_url GitLab другой
        /// (например, 10.0.0.5), вход идёт на external_url, cookie сессии живут там, и после входа
        /// GitLab уже не помнит запрос OAuth - открывается главная. Поэтому authorize открываем сразу
        /// по адресу из редиректа /oauth/authorize / …/users/sign_in (как в веб-админке).
        /// </summary>
        private async Task<string> ResolvePublicBaseUrlAsync()
        {
            try
            {
                using (var response = await Probe.GetAsync(_baseUrl + "/oauth/authorize").ConfigureAwait(false))
                {
                    var location = response.Headers.Location;
                    if (location == null || !location.IsAbsoluteUri)
                        return _baseUrl;
                    var path = location.AbsolutePath;
                    var signIn = path.IndexOf("/users/sign_in", StringComparison.OrdinalIgnoreCase);
                    var root = signIn >= 0 ? path.Substring(0, signIn) : "";
                    return $"{location.Scheme}://{location.Authority}{root}".TrimEnd('/');
                }
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
            {
                return _baseUrl; // GitLab недоступен - ошибку покажет браузер
            }
        }

        /// <summary>Новый access-токен по refresh-токену (OAuth-токены GitLab живут 2 часа).</summary>
        public Task<StoredToken> RefreshAsync(StoredToken token) =>
            TokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["refresh_token"] = token.RefreshToken,
                ["grant_type"] = "refresh_token",
                ["redirect_uri"] = _redirectUri,
                ["code_verifier"] = token.CodeVerifier,
            }, token.CodeVerifier);

        private async Task<StoredToken> TokenAsync(Dictionary<string, string> form, string verifier)
        {
            using (var response = await Http.PostAsync(_baseUrl + "/oauth/token", new FormUrlEncodedContent(form)).ConfigureAwait(false))
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new GitLabApiException((int)response.StatusCode, $"GitLab не выдал токен ({(int)response.StatusCode}): {Trim(body)}");

                OAuthTokenResponse t;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                    t = (OAuthTokenResponse)new DataContractJsonSerializer(typeof(OAuthTokenResponse)).ReadObject(stream);
                return new StoredToken
                {
                    Kind = StoredToken.OAuth,
                    AccessToken = t.AccessToken,
                    RefreshToken = t.RefreshToken,
                    CodeVerifier = verifier,
                    ExpiresUtc = t.ExpiresIn > 0 ? DateTime.UtcNow.AddSeconds(t.ExpiresIn) : (DateTime?)null,
                };
            }
        }

        /// <summary>Ждёт один GET на redirect_uri, отвечает страницей «можно закрыть вкладку».</summary>
        private static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener, string path, CancellationToken ct)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(LoginTimeout);
                using (timeout.Token.Register(listener.Stop))
                {
                    while (true)
                    {
                        TcpClient client;
                        try
                        {
                            client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        }
                        catch (Exception) when (timeout.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(ct.IsCancellationRequested
                                ? "Вход отменён."
                                : $"Вход через браузер не завершён за {LoginTimeout.TotalMinutes:0} мин.");
                        }

                        using (client)
                        using (var stream = client.GetStream())
                        {
                            var requestLine = await ReadLineAsync(stream).ConfigureAwait(false);
                            // GET /callback?code=...&state=... HTTP/1.1
                            var parts = (requestLine ?? "").Split(' ');
                            if (parts.Length < 2 || !Uri.TryCreate("http://localhost" + parts[1], UriKind.Absolute, out var url) ||
                                !string.Equals(url.AbsolutePath, path, StringComparison.OrdinalIgnoreCase))
                            {
                                await RespondAsync(stream, 404, "Не найдено").ConfigureAwait(false);
                                continue; // например, запрос favicon.ico
                            }

                            var query = ParseQuery(url.Query);
                            var ok = query.ContainsKey("code");
                            await RespondAsync(stream, 200, ok
                                ? "Вход в GitLab выполнен. Вкладку можно закрыть и вернуться в Pilot."
                                : "Вход в GitLab не выполнен. Вернитесь в Pilot.").ConfigureAwait(false);
                            return query;
                        }
                    }
                }
            }
        }

        private static async Task<string> ReadLineAsync(Stream stream)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                var text = Encoding.ASCII.GetString(buffer, 0, total);
                var end = text.IndexOf("\r\n", StringComparison.Ordinal);
                if (end >= 0) return text.Substring(0, end);
            }
            return Encoding.ASCII.GetString(buffer, 0, total);
        }

        private static async Task RespondAsync(Stream stream, int status, string message)
        {
            var html = "<!doctype html><meta charset=\"utf-8\"><title>Reports</title>" +
                       $"<body style=\"font-family:sans-serif;padding:40px\"><h2>{WebUtility.HtmlEncode(message)}</h2></body>";
            var body = Encoding.UTF8.GetBytes(html);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                                               $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        }

        internal static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in (query ?? "").TrimStart('?').Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }

        private static byte[] RandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            return bytes;
        }

        internal static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string E(string value) => Uri.EscapeDataString(value ?? "");

        private static string Trim(string s) => s.Length > 300 ? s.Substring(0, 300) + "…" : s;

        [DataContract]
        private sealed class OAuthTokenResponse
        {
            [DataMember(Name = "access_token")] public string AccessToken { get; set; }
            [DataMember(Name = "refresh_token")] public string RefreshToken { get; set; }
            [DataMember(Name = "expires_in")] public int ExpiresIn { get; set; }
        }
    }

    internal static class DictionaryExtensions
    {
        public static string GetValueOrDefault(this Dictionary<string, string> d, string key) =>
            d.TryGetValue(key, out var v) ? v : null;
    }
}
