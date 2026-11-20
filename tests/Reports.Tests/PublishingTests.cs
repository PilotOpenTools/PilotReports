using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reports.Publishing;
using Xunit;

namespace Reports.Tests
{
    public class PublishingTests
    {
        [Theory]
        [InlineData("<XtraReportsLayoutSerializer />", "<XtraReportsLayoutSerializer />")]
        [InlineData("<a></XtraReportsLayoutSerializer>", "<a></XtraReportsLayoutSerializer>")]
        [InlineData("<a></XtraReportsLayoutSerializer>zation\r\n</XtraReportsLayoutSerializer>", "<a></XtraReportsLayoutSerializer>")]
        public void TrimTail_removes_rest_of_old_file(string content, string expected)
        {
            var trimmed = ReportFile.TrimTail(Encoding.UTF8.GetBytes(content));
            Assert.Equal(expected, Encoding.UTF8.GetString(trimmed));
        }

        private static List<GitLabTreeItem> Tree(params string[] paths) =>
            paths.Select((p, i) => new GitLabTreeItem { Type = "blob", Path = p, Id = i.ToString() }).ToList();

        [Fact]
        public void NameKeyMatchesAdminRules()
        {
            // Те же пары, что в тестах веб-админки (ProjectNames.Key)
            Assert.Equal(ReportNames.Key("Отчёт (вторая форма) #"), ReportNames.Key("отчет вторая  форма"));
            Assert.Equal("разрешения на внесение изменений вторая форма _v2", ReportNames.Key("Разрешения на внесение изменений (вторая форма)_V2 #"));
            Assert.NotEqual(ReportNames.Key("Отчет А"), ReportNames.Key("Отчет Б"));
        }

        [Fact]
        public void FindsRepxLikeAdmin()
        {
            Assert.Equal("Реестр НОПРИЗ.repx", ReportNames.FindPath(Tree("README.md", "Реестр НОПРИЗ.repx", "old/Другой.repx"), "Реестр НОПРИЗ"));
            Assert.Equal("reports/Отчет (вторая форма).repx", ReportNames.FindPath(Tree("reports/Отчет (вторая форма).repx", "x.repx"), "Отчет вторая форма"));
            Assert.Equal("report.repx", ReportNames.FindPath(Tree("report.repx"), "Любое имя"));
            Assert.Null(ReportNames.FindPath(Tree("a.repx", "b.repx"), "Третий"));
            Assert.Equal("Отчет_ ОВАД.repx", ReportNames.DefaultPath("Отчет: ОВАД"));
        }

        [Fact]
        public void ParsesGroupUrl()
        {
            Assert.True(ReportPublishService.TryParseGroupUrl("http://localhost:8080/repports/", out var baseUrl, out var group));
            Assert.Equal("http://localhost:8080", baseUrl);
            Assert.Equal("repports", group);
            Assert.False(ReportPublishService.TryParseGroupUrl("http://localhost:8080/", out _, out _));
            Assert.False(ReportPublishService.TryParseGroupUrl(null, out _, out _));
        }

        [Fact]
        public void ChangelogAddsUnreleasedAndReleaseLikeAdmin()
        {
            var day = new DateTime(2026, 9, 27);
            var dev = ReportChangelog.AddUnreleased(null, "Реестр", new[] { "Новая колонка" }, day);
            Assert.StartsWith("# Журнал изменений: Реестр", dev);
            Assert.Contains("## [Не выпущено]\n\n- Новая колонка _(27.09.2026)_\n", dev);

            var existing = "# Мой\n\n## [Не выпущено]\n\n- старое\n\n## [2026-09-01]\n\n- прод\n";
            dev = ReportChangelog.AddUnreleased(existing, "Реестр", new[] { "новое" }, day);
            Assert.Contains("## [Не выпущено]\n\n- новое _(27.09.2026)_\n- старое\n\n## [2026-09-01]\n\n- прод\n", dev);

            var main = ReportChangelog.AddRelease("# Мой\n\n## [2026-09-01]\n\n- прод\n", "Реестр", new[] { "срочно" }, day);
            Assert.Equal("# Мой\n\n## [2026-09-27]\n\n- срочно\n\n## [2026-09-01]\n\n- прод\n", main);

            Assert.Equal(new[] { "a", "b" }, ReportChangelog.SplitNotes("- a\r\n\r\n• b"));
        }

        [Fact]
        public void ProjectCreationRightsFollowGroupSetting()
        {
            Assert.True(ReportRepoCreation.CanCreate("maintainer", 40, false).Can);
            Assert.False(ReportRepoCreation.CanCreate("maintainer", 30, false).Can);
            Assert.False(ReportRepoCreation.CanCreate(null, 30, false).Can); // по умолчанию в GitLab - Maintainer
            Assert.True(ReportRepoCreation.CanCreate("developer", 30, false).Can);
            Assert.False(ReportRepoCreation.CanCreate("developer", 20, false).Can);
            Assert.False(ReportRepoCreation.CanCreate("owner", 40, false).Can);
            Assert.False(ReportRepoCreation.CanCreate("noone", 50, false).Can);
            Assert.True(ReportRepoCreation.CanCreate("noone", 0, true).Can);
            Assert.Contains("нужен Maintainer", ReportRepoCreation.CanCreate("maintainer", 30, false).Why);
        }

        [Fact]
        public void GuessesFolderLikeAdmin()
        {
            var folders = new[] { "", "01-folder", "02-registry", "archive" };
            Assert.Equal("01-folder", ReportRepoCreation.GuessFolder("01-Папка", folders));
            Assert.Equal("archive", ReportRepoCreation.GuessFolder("Archive", folders));
            Assert.Equal("", ReportRepoCreation.GuessFolder("XX-В РАЗРАБОТКЕ", folders));
            Assert.Equal("", ReportRepoCreation.GuessFolder(null, folders));
        }

        [Fact]
        public void RepoNameIsValidAndMatchesReport()
        {
            var name = ReportNames.Suggest("Отчёт (вторая форма) #");
            Assert.Equal("Отчёт вторая форма", name);
            Assert.Null(ReportNames.NameError(name));
            Assert.Equal(ReportNames.Key("Отчёт (вторая форма) #"), ReportNames.Key(name));
            Assert.NotNull(ReportNames.NameError("Отчёт (1)"));
            Assert.Equal("otchet-po-zamorozhennym-elementam", ReportNames.Slug("Отчет по замороженным элементам"));
        }

        /// <summary>Поиск проекта по имени отчёта и коммит в dev - на фейковом GitLab.</summary>
        [Fact]
        public async Task FindsProjectByNameAndCommitsExactBytes()
        {
            var port = FreePort();
            var gitlab = new FakeGitLab();
            using (var listener = new HttpListener())
            {
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                new Thread(() => gitlab.Serve(listener)) { IsBackground = true }.Start();

                var service = new ReportPublishService(new GitLabApi($"http://localhost:{port}", "token"), "repports");
                var repo = await service.FindAsync("Отчет по замороженным элементам (v2)");

                Assert.NotNull(repo);
                Assert.Equal(7, repo.Project.Id);
                Assert.Equal("01-folder", repo.Folder);
                Assert.Contains("dev", repo.Branches);
                Assert.Equal("Отчет по замороженным элементам v2.repx", repo.RepxPathByBranch["dev"]);

                var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("<xml>новый</xml>\r\n")).ToArray();
                var commit = await service.PublishAsync(repo, "dev", content, "Отчет: правка", new[] { "правка" });
                Assert.Equal("abc1234", commit.ShortId);
                Assert.Contains("\"file_path\":\"CHANGELOG.md\"", gitlab.LastCommitBody);
                Assert.Contains("\"branch\":\"dev\"", gitlab.LastCommitBody);
                Assert.Contains("\"action\":\"update\"", gitlab.LastCommitBody);
                Assert.Contains("\"encoding\":\"base64\"", gitlab.LastCommitBody);
                Assert.Contains(Convert.ToBase64String(content), gitlab.LastCommitBody);
                Assert.Equal("Bearer token", gitlab.LastAuthorization);

                // тот же файл уже в ветке - коммит не нужен
                gitlab.LastCommitBody = null;
                Assert.Null(await service.PublishAsync(repo, "dev", gitlab.DevFile, "повтор"));
                Assert.Null(gitlab.LastCommitBody);

                // ветки main нет - создаётся тем же коммитом от dev, файл отчёта в ней уже есть (update)
                Assert.DoesNotContain("main", repo.Branches);
                Assert.NotNull(await service.PublishAsync(repo, "main", content, "в прод"));
                Assert.Contains("\"branch\":\"main\"", gitlab.LastCommitBody);
                Assert.Contains("\"start_branch\":\"dev\"", gitlab.LastCommitBody);
                Assert.Contains("\"action\":\"update\"", gitlab.LastCommitBody);
                Assert.Contains("main", repo.Branches);

                listener.Stop();
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

        private sealed class FakeGitLab
        {
            public readonly byte[] DevFile = Encoding.UTF8.GetBytes("<xml>старый</xml>");
            public string LastCommitBody;
            public string LastAuthorization;

            public void Serve(HttpListener listener)
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var ctx = listener.GetContext();
                        LastAuthorization = ctx.Request.Headers["Authorization"];
                        var path = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath);
                        var query = ctx.Request.Url.Query;
                        byte[] body;
                        var status = 200;
                        if (path == "/api/v4/groups/repports/projects")
                            body = Json(query.Contains("search=")
                                ? "[]" // поиск GitLab по подстроке не нашёл (скобки в имени) - модуль берёт весь список
                                : "[{\"id\":3,\"name\":\"Другой отчёт\",\"path\":\"other\",\"path_with_namespace\":\"repports/other\",\"web_url\":\"http://x/3\"}," +
                                  "{\"id\":7,\"name\":\"Отчет по замороженным элементам v2\",\"path\":\"frozen-element\",\"path_with_namespace\":\"repports/01-folder/frozen-element\",\"web_url\":\"http://x/7\"}]");
                        else if (path == "/api/v4/projects/7/repository/tree" && query.Contains("ref=dev"))
                            body = Json("[{\"id\":\"b1\",\"name\":\"x\",\"type\":\"blob\",\"path\":\"Отчет по замороженным элементам v2.repx\"},{\"id\":\"b2\",\"name\":\"README.md\",\"type\":\"blob\",\"path\":\"README.md\"}]");
                        else if (path == "/api/v4/projects/7/repository/tree")
                        {
                            status = 404;
                            body = Json("{\"message\":\"404 Tree Not Found\"}");
                        }
                        else if (path.StartsWith("/api/v4/projects/7/repository/files/") && path.EndsWith("/raw"))
                            body = DevFile;
                        else if (path == "/api/v4/projects/7/repository/commits" && ctx.Request.HttpMethod == "POST")
                        {
                            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                                LastCommitBody = reader.ReadToEnd();
                            body = Json("{\"id\":\"abc1234567\",\"short_id\":\"abc1234\",\"title\":\"ok\"}");
                        }
                        else
                        {
                            status = 404;
                            body = Json("{\"message\":\"not found\"}");
                        }
                        ctx.Response.StatusCode = status;
                        ctx.Response.OutputStream.Write(body, 0, body.Length);
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

            private static byte[] Json(string s) => Encoding.UTF8.GetBytes(s);
        }
    }
}
