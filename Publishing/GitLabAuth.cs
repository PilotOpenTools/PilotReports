using System;
using System.Threading;
using System.Threading.Tasks;
using Reports.Infrastructure;

namespace Reports.Publishing
{
    /// <summary>
    /// Токен GitLab текущего пользователя Pilot: загрузка, обновление OAuth-токена, вход через браузер или по PAT, выход.
    /// </summary>
    internal sealed class GitLabAuth
    {
        private readonly string _pilotLogin;
        private readonly ModuleConfig _config;

        public GitLabAuth(string pilotLogin, string baseUrl, ModuleConfig config)
        {
            _pilotLogin = pilotLogin;
            BaseUrl = baseUrl;
            _config = config;
        }

        public string BaseUrl { get; }

        public bool BrowserLoginAvailable => GitLabOAuth.IsConfigured(_config.GitLabOAuthClientId, _config.GitLabOAuthRedirectUri);

        private GitLabOAuth OAuth => new GitLabOAuth(BaseUrl, _config.GitLabOAuthClientId, _config.GitLabOAuthRedirectUri);

        /// <summary>Действующий токен или null (нужно войти). Истекающий OAuth-токен обновляется.</summary>
        public async Task<StoredToken> GetAsync()
        {
            var token = GitLabTokenStore.Load(_pilotLogin, BaseUrl, _config.UseGitCredentials);
            if (token == null || token.Kind != StoredToken.OAuth || !token.IsExpiring)
                return token;

            if (string.IsNullOrEmpty(token.RefreshToken) || !BrowserLoginAvailable)
            {
                Logout();
                return null;
            }
            try
            {
                var refreshed = await OAuth.RefreshAsync(token).ConfigureAwait(false);
                refreshed.GitLabUser = token.GitLabUser;
                GitLabTokenStore.Save(_pilotLogin, BaseUrl, refreshed);
                return refreshed;
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось обновить OAuth-токен GitLab - нужен повторный вход", ex);
                Logout();
                return null;
            }
        }

        /// <summary>Вход через браузер (OAuth + PKCE). Возвращает сохранённый токен.</summary>
        public async Task<StoredToken> LoginInBrowserAsync(CancellationToken ct)
        {
            var token = await OAuth.LoginAsync(ct).ConfigureAwait(false);
            token.GitLabUser = (await new GitLabApi(BaseUrl, token.AccessToken).GetCurrentUserAsync().ConfigureAwait(false))?.Username;
            GitLabTokenStore.Save(_pilotLogin, BaseUrl, token);
            return token;
        }

        /// <summary>Вход по Personal Access Token (проверяется запросом /user).</summary>
        public async Task<StoredToken> LoginWithTokenAsync(string pat)
        {
            var user = await new GitLabApi(BaseUrl, pat.Trim()).GetCurrentUserAsync().ConfigureAwait(false);
            var token = new StoredToken { Kind = StoredToken.Pat, AccessToken = pat.Trim(), GitLabUser = user?.Username };
            GitLabTokenStore.Save(_pilotLogin, BaseUrl, token);
            return token;
        }

        public void Logout() => GitLabTokenStore.Clear(_pilotLogin, BaseUrl);
    }
}
