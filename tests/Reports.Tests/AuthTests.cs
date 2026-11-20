using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reports.Infrastructure;
using Reports.Publishing;
using Xunit;

namespace Reports.Tests
{
    public class AuthTests
    {
        [Fact]
        public void TokenFileIsSeparatePerPilotUserAndGitLabServer()
        {
            var a = GitLabTokenStore.PathFor("ivanov", "http://gitlab:8080");
            Assert.Equal(a, GitLabTokenStore.PathFor(" IVANOV ", "http://gitlab:8080/"));
            Assert.NotEqual(a, GitLabTokenStore.PathFor("petrov", "http://gitlab:8080"));
            Assert.NotEqual(a, GitLabTokenStore.PathFor("ivanov", "http://other:8080"));
            Assert.DoesNotContain("ivanov", a); // логин в имени файла не светится
        }

        [Fact]
        public void TokenIsEncryptedAndNotVisibleToAnotherPilotUser()
        {
            var host = "http://test-" + Guid.NewGuid().ToString("N") + ":8080";
            try
            {
                GitLabTokenStore.Save("user-a", host, new StoredToken { Kind = StoredToken.Pat, AccessToken = "glpat-secret-a", GitLabUser = "a" });

                Assert.Equal("glpat-secret-a", GitLabTokenStore.Load("user-a", host, useGitCredentials: false).AccessToken);
                Assert.Null(GitLabTokenStore.Load("user-b", host, useGitCredentials: false));

                var raw = File.ReadAllBytes(GitLabTokenStore.PathFor("user-a", host));
                Assert.DoesNotContain("glpat-secret-a", Encoding.UTF8.GetString(raw)); // на диске - только шифр DPAPI
            }
            finally
            {
                GitLabTokenStore.Clear("user-a", host);
            }
            Assert.Null(GitLabTokenStore.Load("user-a", host, useGitCredentials: false));
        }

        [Fact]
        public void ParsesCallbackQuery()
        {
            var q = GitLabOAuth.ParseQuery("?code=abc%2B1&state=xyz&empty=");
            Assert.Equal("abc+1", q["code"]);
            Assert.Equal("xyz", q["state"]);
            Assert.Equal("", q["empty"]);
            Assert.Equal("AQID", GitLabOAuth.Base64Url(new byte[] { 1, 2, 3 }));
        }

        /// <summary>
        /// Полный вход через браузер: тест играет роль браузера (переходит на redirect_uri с кодом)
        /// и GitLab (/oauth/token проверяет PKCE, /user). Токен сохраняется для пользователя Pilot.
        /// </summary>
        [Fact]
        public async Task BrowserLoginWithPkce()
        {
            var gitlabPort = FreePort();
            var callbackPort = FreePort();
            var redirect = $"http://127.0.0.1:{callbackPort}/callback";
            var gitlab = new FakeOAuthServer();
            var original = GitLabOAuth.OpenBrowser;
            var pilotLogin = "oauth-test-" + Guid.NewGuid().ToString("N");
            var baseUrl = $"http://localhost:{gitlabPort}";

            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add(baseUrl + "/");
                listener.Start();
                new Thread(() => gitlab.Serve(listener)) { IsBackground = true }.Start();

                // «Браузер»: пользователь разрешил доступ - GitLab перенаправляет на redirect_uri с кодом и state.
                GitLabOAuth.OpenBrowser = url =>
                {
                    var q = GitLabOAuth.ParseQuery(new Uri(url).Query);
                    gitlab.Challenge = q["code_challenge"];
                    Assert.Equal("S256", q["code_challenge_method"]);
                    Assert.Equal(redirect, q["redirect_uri"]);
                    Assert.Equal("app-id", q["client_id"]);
                    new Thread(() =>
                    {
                        using (var http = new HttpClient())
                            gitlab.CallbackPage = http.GetStringAsync($"{redirect}?code=the-code&state={Uri.EscapeDataString(q["state"])}").GetAwaiter().GetResult();
                    }) { IsBackground = true }.Start();
                };

                try
                {
                    var config = ModuleConfig.Parse($"{{\"gitLabOAuthClientId\":\"app-id\",\"gitLabOAuthRedirectUri\":\"{redirect}\"}}");
                    var auth = new GitLabAuth(pilotLogin, baseUrl, config);
                    Assert.True(auth.BrowserLoginAvailable);

                    var token = await auth.LoginInBrowserAsync(CancellationToken.None);
                    Assert.Equal(StoredToken.OAuth, token.Kind);
                    Assert.Equal("oauth-access", token.AccessToken);
                    Assert.Equal("oauth-refresh", token.RefreshToken);
                    Assert.Equal("gitlab-user", token.GitLabUser);
                    Assert.True(gitlab.VerifierMatched, "code_verifier не соответствует code_challenge");
                    Assert.Contains("Вход в GitLab выполнен", gitlab.CallbackPage);

                    // токен сохранён именно для этого пользователя Pilot
                    Assert.Equal("oauth-access", GitLabTokenStore.Load(pilotLogin, baseUrl, false).AccessToken);
                    Assert.Null(GitLabTokenStore.Load(pilotLogin + "-other", baseUrl, false));
                }
                finally
                {
                    GitLabOAuth.OpenBrowser = original;
                    GitLabTokenStore.Clear(pilotLogin, baseUrl);
                    listener.Stop();
                }
            }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private sealed class FakeOAuthServer
        {
            public volatile string Challenge;
            public volatile bool VerifierMatched;
            public volatile string CallbackPage;

            public void Serve(HttpListener listener)
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var ctx = listener.GetContext();
                        string body;
                        if (ctx.Request.Url.AbsolutePath == "/oauth/token")
                        {
                            string form;
                            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                                form = reader.ReadToEnd();
                            var f = GitLabOAuth.ParseQuery(form);
                            using (var sha = SHA256.Create())
                                VerifierMatched = GitLabOAuth.Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(f["code_verifier"]))) == Challenge
                                                  && f["code"] == "the-code" && f["grant_type"] == "authorization_code";
                            body = "{\"access_token\":\"oauth-access\",\"refresh_token\":\"oauth-refresh\",\"expires_in\":7200,\"token_type\":\"Bearer\"}";
                        }
                        else if (ctx.Request.Url.AbsolutePath == "/api/v4/user")
                        {
                            body = ctx.Request.Headers["Authorization"] == "Bearer oauth-access"
                                ? "{\"username\":\"gitlab-user\",\"name\":\"User\"}"
                                : "{\"message\":\"401\"}";
                        }
                        else
                        {
                            ctx.Response.StatusCode = 404;
                            body = "{}";
                        }
                        var bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                        ctx.Response.Close();
                    }
                }
                catch (HttpListenerException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }
}
