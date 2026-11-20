using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Reports.Publishing
{
    /// <summary>Папка (подгруппа) группы отчётов, в которой можно создать репозиторий, и права пользователя в ней.</summary>
    internal sealed class RepoFolder
    {
        public long GroupId { get; set; }

        /// <summary>Путь относительно группы отчётов; "" - сама группа.</summary>
        public string Folder { get; set; }

        public string FullPath { get; set; }
        public bool CanCreate { get; set; }

        /// <summary>«Developer - можно создавать» или почему нельзя.</summary>
        public string Access { get; set; }

        public string Display => (Folder.Length == 0 ? "(корень группы)" : Folder) + "  -  " + Access;
    }

    /// <summary>
    /// Создание репозитория отчёта из клиента - то же, что «создать репозиторий» во вкладке «Отчёты» веб-админки:
    /// проект с именем отчёта в выбранной подгруппе, в main - README.md и CHANGELOG.md, ветка dev от main.
    /// Сам .repx уходит следующей обычной отправкой.
    /// </summary>
    internal static class ReportRepoCreation
    {
        private const int Developer = 30;
        private const int Maintainer = 40;
        private const int Owner = 50;

        /// <summary>Корень группы и все подгруппы с правами текущего пользователя на создание проектов.</summary>
        public static async Task<List<RepoFolder>> ListFoldersAsync(GitLabApi api, string groupPath)
        {
            var user = await api.GetCurrentUserAsync().ConfigureAwait(false);
            var root = await api.GetGroupAsync(groupPath).ConfigureAwait(false)
                       ?? throw new GitLabApiException(404, $"Группа «{groupPath}» не найдена в GitLab (или нет доступа).");
            var groups = new List<GitLabGroup> { root };
            groups.AddRange(await api.ListDescendantGroupsAsync(groupPath).ConfigureAwait(false) ?? new List<GitLabGroup>());

            var result = new List<RepoFolder>();
            foreach (var g in groups)
            {
                var level = user.IsAdmin ? Owner : await api.GetGroupAccessLevelAsync(g.Id, user.Id).ConfigureAwait(false);
                var (can, why) = CanCreate(g.ProjectCreationLevel, level, user.IsAdmin);
                result.Add(new RepoFolder
                {
                    GroupId = g.Id,
                    FullPath = g.FullPath,
                    Folder = FolderOf(g.FullPath, root.FullPath),
                    CanCreate = can,
                    Access = why,
                });
            }
            return result.OrderBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Правило GitLab «кто может создавать проекты в группе» (project_creation_level) против роли пользователя.</summary>
        internal static (bool Can, string Why) CanCreate(string creationLevel, int accessLevel, bool isAdmin)
        {
            if (isAdmin)
                return (true, "администратор GitLab - можно создавать");

            var role = RoleTitle(accessLevel);
            int required;
            switch ((creationLevel ?? "maintainer").ToLowerInvariant())
            {
                case "noone":
                    return (false, "создание проектов в группе запрещено");
                case "administrator":
                    return (false, "проекты создаёт только администратор GitLab");
                case "developer":
                    required = Developer;
                    break;
                case "owner":
                    required = Owner;
                    break;
                default:
                    required = Maintainer;
                    break;
            }
            return accessLevel >= required
                ? (true, $"{role} - можно создавать")
                : (false, $"{role} - нет прав (нужен {RoleTitle(required)} или выше)");
        }

        private static string RoleTitle(int level) =>
            level >= Owner ? "Owner"
            : level >= Maintainer ? "Maintainer"
            : level >= Developer ? "Developer"
            : level >= 20 ? "Reporter"
            : level >= 10 ? "Guest"
            : level > 0 ? "Minimal access"
            : "не участник группы";

        private static string FolderOf(string fullPath, string rootPath) =>
            fullPath.Length > rootPath.Length && fullPath.StartsWith(rootPath + "/", StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(rootPath.Length + 1)
                : "";

        /// <summary>
        /// Папка GitLab, подходящая к папке отчёта в Pilot: по имени или по номеру в начале
        /// («01-Папка» / «01-folder»), как GuessFolder в веб-админке. "" - корень группы.
        /// </summary>
        public static string GuessFolder(string pilotFolder, IEnumerable<string> gitFolders)
        {
            if (string.IsNullOrWhiteSpace(pilotFolder))
                return "";
            var folders = gitFolders.Where(f => f.Length > 0).ToList();
            var top = pilotFolder.Split('/')[0];

            var byName = folders.FirstOrDefault(f => ReportNames.Key(f.Split('/').Last()) == ReportNames.Key(top) ||
                                                     ReportNames.Key(f) == ReportNames.Key(pilotFolder));
            if (byName != null)
                return byName;

            var number = LeadingNumber.Match(top);
            if (number.Success)
            {
                var byNumber = folders.FirstOrDefault(f =>
                {
                    var m = LeadingNumber.Match(f.Split('/').Last());
                    return m.Success && m.Value == number.Value;
                });
                if (byNumber != null)
                    return byNumber;
            }
            return "";
        }

        private static readonly Regex LeadingNumber = new Regex(@"^\d+");

        /// <summary>Создаёт проект, первый коммит в main (README.md, CHANGELOG.md) и ветку dev.</summary>
        public static async Task<ReportRepository> CreateAsync(GitLabApi api, string name, RepoFolder folder)
        {
            var project = await CreateProjectAsync(api, name, folder.GroupId).ConfigureAwait(false);
            try
            {
                var utf8 = new UTF8Encoding(false);
                await api.CommitFilesAsync(project.Id, ReportPublishService.MainBranch, $"{name}: создание репозитория отчёта", new[]
                {
                    new CommitFile("README.md", true, utf8.GetBytes(Readme(name))),
                    new CommitFile(ReportChangelog.FileName, true, utf8.GetBytes(ReportChangelog.DefaultPreamble(name))),
                }).ConfigureAwait(false);
                await api.CreateBranchAsync(project.Id, ReportPublishService.DevBranch, ReportPublishService.MainBranch).ConfigureAwait(false);
            }
            catch (GitLabApiException ex)
            {
                throw new GitLabApiException(ex.Status, $"Проект {project.WebUrl} создан, но не удалось заполнить его: {ex.Message}");
            }

            var repo = new ReportRepository { Project = project, Folder = folder.Folder };
            foreach (var b in new[] { ReportPublishService.MainBranch, ReportPublishService.DevBranch })
            {
                repo.Branches.Add(b);
                repo.ChangelogBranches.Add(b);
            }
            return repo;
        }

        private static async Task<GitLabProject> CreateProjectAsync(GitLabApi api, string name, long namespaceId)
        {
            var slug = ReportNames.Slug(name);
            for (var attempt = 1; ; attempt++)
            {
                var path = attempt == 1 ? slug : $"{slug}-{attempt}";
                try
                {
                    return await api.CreateProjectAsync(name, path, namespaceId, $"Отчёт Pilot «{name}»").ConfigureAwait(false);
                }
                catch (GitLabApiException ex) when (attempt < 10 && ex.Status == 400 &&
                                                    ex.Message.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                    ex.Message.IndexOf("taken", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // путь занят другим проектом - пробуем следующий
                }
            }
        }

        internal static string Readme(string name) =>
            $"# {name}\n\n" +
            "## Ветки\n\n" +
            "- `dev` - отчёт на тестовом сервере Pilot\n" +
            "- `main` - отчёт на продовом сервере Pilot\n\n" +
            "## Как меняется\n\n" +
            "- Сохранение отчёта в Pilot - модуль Reports предлагает отправить `.repx` в `dev` или `main`.\n" +
            "- Перенос на прод - веб-админка, вкладка «Отчёты»: Merge Request `dev` / `main`, после pipeline отчёт записывается на продовый сервер.\n\n" +
            "## Файлы\n\n" +
            $"- `{ReportNames.DefaultPath(name)}` - отчёт DevExpress\n";
    }
}
