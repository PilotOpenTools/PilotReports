using System;
using System.Collections.Generic;
using System.IO;
using Reports.Infrastructure;
using Reports.Publishing;
using Xunit;

namespace Reports.Tests
{
    /// <summary>Адреса из ответов GitLab открываются оболочкой, поэтому допустимы только http и https.</summary>
    public class ExternalLinkTests
    {
        [Theory]
        [InlineData("http://gitlab.example.local:8080/group/project", true)]
        [InlineData("https://gitlab.example.local/group/project", true)]
        [InlineData("  https://gitlab.example.local/x  ", true)]
        [InlineData("file:///C:/Windows/System32/calc.exe", false)]
        [InlineData("\\\\evil\\share\\x.exe", false)]
        [InlineData("javascript:alert(1)", false)]
        [InlineData("calc.exe", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Only_http_and_https_are_safe(string url, bool expected) =>
            Assert.Equal(expected, ExternalLink.IsSafe(url, out _));

        [Fact]
        public void Open_launches_safe_urls_and_refuses_the_rest()
        {
            var launched = new List<string>();
            var original = ExternalLink.Launcher;
            ExternalLink.Launcher = launched.Add;
            try
            {
                Assert.True(ExternalLink.Open("https://gitlab.example.local/g/p"));
                Assert.False(ExternalLink.Open("file:///C:/Windows/System32/calc.exe"));
                Assert.False(ExternalLink.Open(null));

                Assert.Equal(new[] { "https://gitlab.example.local/g/p" }, launched);
            }
            finally
            {
                ExternalLink.Launcher = original;
            }
        }
    }

    public class GitLookupTests
    {
        [Fact]
        public void FindGit_looks_only_in_rooted_path_entries()
        {
            var dir = Path.Combine(Path.GetTempPath(), "git-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var git = Path.Combine(dir, "git.exe");
                File.WriteAllText(git, "");
                var sep = Path.PathSeparator;

                Assert.Equal(git, GitLabTokenStore.FindGit(dir));
                Assert.Equal(git, GitLabTokenStore.FindGit($".{sep}relative{sep}\"{dir}\""));  // кавычки в PATH допустимы, относительные каталоги нет
                Assert.Null(GitLabTokenStore.FindGit($".{sep}relative"));
                Assert.Null(GitLabTokenStore.FindGit(Path.Combine(dir, "нет")));
                Assert.Null(GitLabTokenStore.FindGit($"{sep}|*?{sep}"));  // недопустимые символы пропускаются без исключения
                Assert.Null(GitLabTokenStore.FindGit(null));
                Assert.Null(GitLabTokenStore.FindGit(""));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }

    public class ShippedConfigTests
    {
        /// <summary>Файл попадает в zip модуля: адреса чьей-то лаборатории в нём быть не должно.</summary>
        [Fact]
        public void Shipped_config_has_no_private_addresses_and_stays_valid()
        {
            var path = Path.Combine(AppContext.BaseDirectory, ModuleConfig.FileName);
            Assert.True(File.Exists(path), "Reports.config.json не скопирован в выходную папку");

            var config = ModuleConfig.Parse(File.ReadAllText(path));

            foreach (var value in new[] { config.StatsUrl, config.ReportsGitLabGroupUrl, config.GitLabOAuthClientId })
            {
                Assert.DoesNotContain("192.168.", value ?? "");
                Assert.DoesNotContain("localhost", value ?? "");
            }
            Assert.Equal(ModuleConfig.DefaultRedirectUri, config.GitLabOAuthRedirectUri);
            Assert.True(config.PublishOnSave);
        }

        [Fact]
        public void Save_does_not_offer_publish_without_group_url()
        {
            Assert.False(ReportSaveHandler.ShouldOfferPublish(new ModuleConfig { PublishOnSave = true, ReportsGitLabGroupUrl = "" }));
            Assert.False(ReportSaveHandler.ShouldOfferPublish(new ModuleConfig { PublishOnSave = false, ReportsGitLabGroupUrl = "http://gitlab.example.local/reports" }));
            Assert.True(ReportSaveHandler.ShouldOfferPublish(new ModuleConfig { PublishOnSave = true, ReportsGitLabGroupUrl = "http://gitlab.example.local/reports" }));
        }

        [Fact]
        public void Empty_group_url_is_reported_as_not_configured()
        {
            Assert.False(ReportPublishService.TryParseGroupUrl("", out _, out _));
            Assert.False(ReportPublishService.TryParseGroupUrl("file:///C:/reports", out _, out _));
            Assert.True(ReportPublishService.TryParseGroupUrl("http://gitlab.example.local:8080/reports/sub", out var baseUrl, out var group));
            Assert.Equal("http://gitlab.example.local:8080", baseUrl);
            Assert.Equal("reports/sub", group);
        }
    }
}
