using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Reports.Publishing
{
    /// <summary>
    /// Отправка отчёта в GitLab. Репозиторий ищется по имени отчёта в группе отчётов (настройка
    /// reportsGitLabGroupUrl), ссылка на GitLab в скрипте отчёта больше не нужна.
    /// Ветки: <c>dev</c> - тестовый сервер, <c>main</c> - продовый.
    /// </summary>
    internal sealed class ReportPublishService
    {
        public const string DevBranch = "dev";
        public const string MainBranch = "main";

        /// <summary>
        /// Hotfix прода: своя ветка от main (hotfix/2026-10-30-1530). На прод её выпускает веб-админка
        /// («Hotfix в прод» на странице отчёта: MR в main, pipeline, продовый сервер, тег), отчёт в main напрямую не отправляется.
        /// </summary>
        public static string NewHotfixBranch(DateTime now) => "hotfix/" + now.ToString("yyyy-MM-dd-HHmm");

        private readonly GitLabApi _api;
        private readonly string _groupPath;

        public ReportPublishService(GitLabApi api, string groupPath)
        {
            _api = api;
            _groupPath = groupPath;
        }

        public string GroupPath => _groupPath;
        public GitLabApi Api => _api;
        public string GroupUrl => $"{_api.BaseUrl}/{_groupPath}";

        /// <summary>Разбор адреса группы: http://host:8080/reports/sub / (http://host:8080, reports/sub).</summary>
        public static bool TryParseGroupUrl(string url, out string baseUrl, out string groupPath)
        {
            baseUrl = groupPath = null;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return false;
            groupPath = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            baseUrl = $"{uri.Scheme}://{uri.Authority}";
            return groupPath.Length > 0;
        }

        /// <summary>Репозиторий отчёта; null - в группе нет проекта с таким именем.</summary>
        public async Task<ReportRepository> FindAsync(string reportName)
        {
            var key = ReportNames.Key(reportName);
            // Поиск GitLab по подстроке: пробуем точное имя, затем без спецсимволов, затем весь список группы.
            var candidates = await _api.ListGroupProjectsAsync(_groupPath, reportName).ConfigureAwait(false);
            var project = candidates.FirstOrDefault(p => p.Name == reportName) ?? candidates.FirstOrDefault(p => ReportNames.Key(p.Name) == key);
            if (project == null)
            {
                var all = await _api.ListGroupProjectsAsync(_groupPath, null).ConfigureAwait(false);
                project = all.FirstOrDefault(p => ReportNames.Key(p.Name) == key);
            }
            if (project == null)
                return null;

            var repo = new ReportRepository { Project = project, Folder = FolderOf(project.PathWithNamespace) };
            foreach (var branch in new[] { DevBranch, MainBranch })
            {
                var tree = await _api.ListTreeAsync(project.Id, branch).ConfigureAwait(false);
                if (tree == null)
                    continue;
                repo.Branches.Add(branch);
                var path = ReportNames.FindPath(tree, project.Name);
                if (path != null)
                    repo.RepxPathByBranch[branch] = path;
                if (tree.Any(t => t.Type == "blob" && t.Path == ReportChangelog.FileName))
                    repo.ChangelogBranches.Add(branch);
            }
            return repo;
        }

        /// <summary>
        /// Отправляет .repx в ветку. Возвращает коммит или null, если в ветке уже этот файл.
        /// <paramref name="notes"/> - «что изменилось»: тем же коммитом дописываются в CHANGELOG.md
        /// (dev - в «Не выпущено», main - новым разделом с датой).
        /// Если ветки нет, она создаётся этим же коммитом от main (для main - от dev); в пустом репозитории
        /// GitLab создаёт ветку первым коммитом.
        /// </summary>
        public async Task<GitLabCommit> PublishAsync(ReportRepository repo, string branch, byte[] content, string message,
            IReadOnlyList<string> notes = null)
        {
            // Ветка, от которой считаем состояние файлов: сама ветка или та, от которой её создадим.
            string startBranch = null;
            var baseBranch = branch;
            if (!repo.Branches.Contains(branch))
            {
                startBranch = new[] { MainBranch, DevBranch }.FirstOrDefault(b => b != branch && repo.Branches.Contains(b));
                baseBranch = startBranch;
            }

            string path = null;
            var exists = baseBranch != null && repo.RepxPathByBranch.TryGetValue(baseBranch, out path);
            if (!exists)
                path = repo.RepxPathByBranch.Values.FirstOrDefault() ?? ReportNames.DefaultPath(repo.Project.Name);

            if (exists && startBranch == null)
            {
                var current = await _api.GetRawFileAsync(repo.Project.Id, path, branch).ConfigureAwait(false);
                if (current != null && current.SequenceEqual(content))
                    return null;
            }

            var files = new List<CommitFile> { new CommitFile(path, !exists, content) };
            if (notes != null && notes.Count > 0)
            {
                var hasLog = baseBranch != null && repo.ChangelogBranches.Contains(baseBranch);
                var current = hasLog ? await _api.GetRawFileAsync(repo.Project.Id, ReportChangelog.FileName, baseBranch).ConfigureAwait(false) : null;
                var text = current == null ? null : Encoding.UTF8.GetString(current);
                text = branch == MainBranch
                    ? ReportChangelog.AddRelease(text, repo.Project.Name, notes, DateTime.Now)
                    : ReportChangelog.AddUnreleased(text, repo.Project.Name, notes, DateTime.Now);
                files.Add(new CommitFile(ReportChangelog.FileName, current == null, new UTF8Encoding(false).GetBytes(text)));
            }

            var commit = await _api.CommitFilesAsync(repo.Project.Id, branch, message, files, startBranch).ConfigureAwait(false);
            repo.Branches.Add(branch);
            repo.RepxPathByBranch[branch] = path;
            if (startBranch != null && repo.ChangelogBranches.Contains(startBranch))
                repo.ChangelogBranches.Add(branch);
            if (files.Count > 1)
                repo.ChangelogBranches.Add(branch);
            return commit;
        }

        public async Task<IReadOnlyList<GitLabCommit>> HistoryAsync(ReportRepository repo, string branch)
        {
            if (!repo.RepxPathByBranch.TryGetValue(branch, out var path))
                return Array.Empty<GitLabCommit>();
            return await _api.ListCommitsAsync(repo.Project.Id, branch, path).ConfigureAwait(false);
        }

        /// <summary>.repx отчёта в ветке байт в байт; null - в ветке нет файла отчёта.</summary>
        public async Task<byte[]> GetRepxAsync(ReportRepository repo, string branch)
        {
            if (!repo.RepxPathByBranch.TryGetValue(branch, out var path))
                return null;
            return await _api.GetRawFileAsync(repo.Project.Id, path, branch).ConfigureAwait(false);
        }

        /// <summary>Последний коммит ветки, затронувший файл отчёта; null - коммитов нет.</summary>
        public async Task<GitLabCommit> LastCommitAsync(ReportRepository repo, string branch)
        {
            if (!repo.RepxPathByBranch.TryGetValue(branch, out var path))
                return null;
            return (await _api.ListCommitsAsync(repo.Project.Id, branch, path, 1).ConfigureAwait(false))?.FirstOrDefault();
        }

        private string FolderOf(string pathWithNamespace)
        {
            var ns = pathWithNamespace.Contains('/') ? pathWithNamespace.Substring(0, pathWithNamespace.LastIndexOf('/')) : "";
            return ns.Length > _groupPath.Length && ns.StartsWith(_groupPath + "/", StringComparison.OrdinalIgnoreCase)
                ? ns.Substring(_groupPath.Length + 1)
                : "";
        }
    }
}
