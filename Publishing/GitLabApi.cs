using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Reports.Infrastructure;

namespace Reports.Publishing
{
    /// <summary>
    /// Минимальный клиент GitLab REST API v4 для отправки отчётов: поиск проекта, дерево ветки,
    /// коммит файла, история. Без Newtonsoft/System.Text.Json - в общем AppDomain клиента Pilot их версии конфликтуют.
    /// </summary>
    internal sealed class GitLabApi
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        private readonly string _apiBase;
        private readonly string _token;

        public GitLabApi(string baseUrl, string token)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            _apiBase = BaseUrl + "/api/v4/";
            _token = token;
            if (BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !_warnedHttp)
            {
                _warnedHttp = true;
                ModuleLog.Warn($"GitLab {BaseUrl} работает по http: токен передаётся по сети без шифрования. Включите https и поменяйте адрес сервера в общих настройках «PilotOpenTools отчёты (GitLab)».");
            }
        }

        private static bool _warnedHttp;

        public string BaseUrl { get; }

        public Task<GitLabUser> GetCurrentUserAsync() => GetAsync<GitLabUser>("user");

        /// <summary>Проекты группы и всех её подгрупп (search - фильтр по имени/пути).</summary>
        public async Task<List<GitLabProject>> ListGroupProjectsAsync(string groupPath, string search)
        {
            var url = $"groups/{E(groupPath)}/projects?include_subgroups=true&archived=false&per_page=100" +
                      (string.IsNullOrEmpty(search) ? "" : "&search=" + E(search));
            return await GetPagedAsync<GitLabProject>(url) ?? throw new GitLabApiException(404, $"Группа «{groupPath}» не найдена в GitLab (или нет доступа).");
        }

        /// <summary>Группа по пути; null - группы нет или нет доступа.</summary>
        public Task<GitLabGroup> GetGroupAsync(string groupPath) => GetOrNullAsync<GitLabGroup>($"groups/{E(groupPath)}");

        /// <summary>Все подгруппы на любой глубине; null - группы нет.</summary>
        public Task<List<GitLabGroup>> ListDescendantGroupsAsync(string groupPath) =>
            GetPagedAsync<GitLabGroup>($"groups/{E(groupPath)}/descendant_groups?order_by=path&sort=asc&per_page=100");

        /// <summary>Уровень доступа пользователя к группе с учётом родительских групп; 0 - не участник.</summary>
        public async Task<int> GetGroupAccessLevelAsync(long groupId, long userId) =>
            (await GetOrNullAsync<GitLabMember>($"groups/{groupId}/members/all/{userId}").ConfigureAwait(false))?.AccessLevel ?? 0;

        /// <summary>Пустой проект в группе (namespaceId) с веткой по умолчанию main.</summary>
        public async Task<GitLabProject> CreateProjectAsync(string name, string path, long namespaceId, string description)
        {
            var sb = new StringBuilder("{");
            Prop(sb, "name", name).Append(',');
            Prop(sb, "path", path).Append(',');
            sb.Append("\"namespace_id\":").Append(namespaceId).Append(',');
            Prop(sb, "description", description ?? "").Append(',');
            Prop(sb, "default_branch", "main").Append(',');
            sb.Append("\"initialize_with_readme\":false}");
            using (var response = await SendAsync(HttpMethod.Post, "projects", sb.ToString()).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                return Read<GitLabProject>(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
            }
        }

        public async Task<GitLabBranch> CreateBranchAsync(long projectId, string branch, string reference)
        {
            using (var response = await SendAsync(HttpMethod.Post,
                       $"projects/{projectId}/repository/branches?branch={E(branch)}&ref={E(reference)}", "{}").ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                return Read<GitLabBranch>(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
            }
        }

        /// <summary>Дерево ветки; null - ветки нет.</summary>
        public Task<List<GitLabTreeItem>> ListTreeAsync(long projectId, string branch) =>
            GetPagedAsync<GitLabTreeItem>($"projects/{projectId}/repository/tree?ref={E(branch)}&recursive=true&per_page=100");

        /// <summary>Файл ветки байт в байт; null - файла нет.</summary>
        public async Task<byte[]> GetRawFileAsync(long projectId, string path, string branch)
        {
            using (var response = await SendAsync(HttpMethod.Get, $"projects/{projectId}/repository/files/{E(path)}/raw?ref={E(branch)}", null).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Коммит одного файла (байт в байт, base64) прямо в ветку.</summary>
        public Task<GitLabCommit> CommitFileAsync(long projectId, string branch, string path, bool create, byte[] content, string message) =>
            CommitFilesAsync(projectId, branch, message, new[] { new CommitFile(path, create, content) });

        /// <summary>
        /// Один коммит с несколькими файлами (байт в байт, base64) прямо в ветку.
        /// <paramref name="startBranch"/> - ветки <paramref name="branch"/> нет: GitLab создаст её от этой ветки.
        /// </summary>
        public async Task<GitLabCommit> CommitFilesAsync(long projectId, string branch, string message, IEnumerable<CommitFile> files,
            string startBranch = null)
        {
            var sb = new StringBuilder("{");
            Prop(sb, "branch", branch).Append(',');
            if (!string.IsNullOrEmpty(startBranch))
                Prop(sb, "start_branch", startBranch).Append(',');
            Prop(sb, "commit_message", message).Append(',');
            sb.Append("\"actions\":[");
            var first = true;
            foreach (var f in files)
            {
                if (!first)
                    sb.Append(',');
                first = false;
                sb.Append('{');
                Prop(sb, "action", f.Create ? "create" : "update").Append(',');
                Prop(sb, "file_path", f.Path).Append(',');
                Prop(sb, "encoding", "base64").Append(',');
                Prop(sb, "content", Convert.ToBase64String(f.Content));
                sb.Append('}');
            }
            sb.Append("]}");

            using (var response = await SendAsync(HttpMethod.Post, $"projects/{projectId}/repository/commits", sb.ToString()).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                return Read<GitLabCommit>(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
            }
        }

        /// <summary>Коммиты ветки, затрагивающие файл (новые первыми).</summary>
        public Task<List<GitLabCommit>> ListCommitsAsync(long projectId, string branch, string path, int max = 50) =>
            GetAsync<List<GitLabCommit>>($"projects/{projectId}/repository/commits?ref_name={E(branch)}&path={E(path)}&per_page={max}");

        // ------------------------------------------------------------------

        private static string E(string value) => Uri.EscapeDataString(value ?? "");

        private async Task<T> GetAsync<T>(string relative)
        {
            using (var response = await SendAsync(HttpMethod.Get, relative, null).ConfigureAwait(false))
            {
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                return Read<T>(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
            }
        }

        private async Task<T> GetOrNullAsync<T>(string relative) where T : class
        {
            using (var response = await SendAsync(HttpMethod.Get, relative, null).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                return Read<T>(await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
            }
        }

        private async Task<List<T>> GetPagedAsync<T>(string relative)
        {
            var result = new List<T>();
            for (var page = 1; page <= 100; page++)
            {
                using (var response = await SendAsync(HttpMethod.Get, $"{relative}&page={page}", null).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound && page == 1)
                        return null;
                    await EnsureSuccessAsync(response).ConfigureAwait(false);
                    var items = Read<List<T>>(await response.Content.ReadAsStreamAsync().ConfigureAwait(false)) ?? new List<T>();
                    result.AddRange(items);
                    var next = response.Headers.TryGetValues("X-Next-Page", out var values) ? values.FirstOrDefault() : null;
                    if (string.IsNullOrEmpty(next) || items.Count == 0)
                        break;
                }
            }
            return result;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, string jsonBody)
        {
            var request = new HttpRequestMessage(method, _apiBase + relative);
            // PAT и OAuth-токен GitLab принимает в заголовке Authorization: Bearer.
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
            if (jsonBody != null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            try
            {
                return await Http.SendAsync(request).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new GitLabApiException(0, $"GitLab {BaseUrl} недоступен: {ex.GetBaseException().Message}");
            }
            catch (TaskCanceledException)
            {
                throw new GitLabApiException(0, $"GitLab {BaseUrl} не ответил за {Http.Timeout.TotalSeconds:0} с.");
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
                return;

            var status = (int)response.StatusCode;
            if (status == 401)
                throw new GitLabApiException(status, "Токен GitLab недействителен или истёк - укажите новый.");
            if (status == 403)
                throw new GitLabApiException(status, "Недостаточно прав в GitLab: нужна роль Developer+ и право push в ветку (ветка может быть защищена).");

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var message = body;
            try
            {
                var error = Read<GitLabError>(new MemoryStream(Encoding.UTF8.GetBytes(body)));
                message = error?.Message?.ToString() ?? error?.Error ?? body;
            }
            catch
            {
                // не JSON
            }
            if (message.Length > 400)
                message = message.Substring(0, 400) + "…";
            throw new GitLabApiException(status, $"GitLab {status}: {message}");
        }

        private static T Read<T>(Stream stream)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
            return (T)serializer.ReadObject(stream);
        }

        private static StringBuilder Prop(StringBuilder sb, string name, string value)
        {
            Stats.Json.Escape(sb, name);
            sb.Append(':');
            Stats.Json.Escape(sb, value ?? "");
            return sb;
        }
    }
}
