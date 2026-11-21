using Reports.Infrastructure;
using Reports.Publishing;
using Xunit;

namespace Reports.Tests
{
    /// <summary>Раздел «PilotOpenTools отчёты (GitLab)» общих настроек: сервер и группа важнее файла, пустое значение возвращает файл.</summary>
    public class GitLabSettingsTests
    {
        [Fact]
        public void Common_gitlab_settings_override_file_until_cleared()
        {
            var config = ModuleConfig.Parse("{\"reportsGitLabGroupUrl\":\"\",\"gitLabOAuthClientId\":\"file-app\"}");
            Assert.False(ReportSaveHandler.ShouldOfferPublish(config));

            config.ApplyGitLab("{\"gitLabUrl\":\"http://gitlab.example.local:8080/\",\"group\":\"/pilot/reports/\",\"gitLabOAuthClientId\":\"common-app\",\"useGitCredentials\":false}");
            Assert.Equal("http://gitlab.example.local:8080/pilot/reports", config.ReportsGitLabGroupUrl);
            Assert.Equal("common-app", config.GitLabOAuthClientId);
            Assert.False(config.UseGitCredentials);
            Assert.True(config.PublishOnSave);
            Assert.Equal(ModuleConfig.DefaultRedirectUri, config.GitLabOAuthRedirectUri);
            Assert.True(ReportSaveHandler.ShouldOfferPublish(config));
            Assert.True(ReportPublishService.TryParseGroupUrl(config.ReportsGitLabGroupUrl, out var baseUrl, out var group));
            Assert.Equal("http://gitlab.example.local:8080", baseUrl);
            Assert.Equal("pilot/reports", group);

            config.ApplyGitLab("");
            Assert.Null(config.ReportsGitLabGroupUrl);
            Assert.Equal("file-app", config.GitLabOAuthClientId);
            Assert.True(config.UseGitCredentials);
            Assert.False(ReportSaveHandler.ShouldOfferPublish(config));
        }

        [Fact]
        public void Invalid_or_broken_value_keeps_previous_settings()
        {
            var config = ModuleConfig.Parse("{\"reportsGitLabGroupUrl\":\"http://file:8080/reports\"}");
            config.ApplyGitLab("{\"gitLabUrl\":\"http://common:8080\",\"group\":\"reports\"}");

            config.ApplyGitLab("это не json");
            Assert.Equal("http://common:8080/reports", config.ReportsGitLabGroupUrl);
            config.ApplyGitLab("{\"gitLabUrl\":\"file:///C:/x\",\"group\":\"reports\"}");
            Assert.Equal("http://common:8080/reports", config.ReportsGitLabGroupUrl);
            config.ApplyGitLab("{\"gitLabUrl\":\"http://common:8080\"}");
            Assert.Equal("http://common:8080/reports", config.ReportsGitLabGroupUrl);
        }

        [Fact]
        public void Publish_on_save_can_be_switched_off_in_common_settings()
        {
            var config = ModuleConfig.Parse("{\"reportsGitLabGroupUrl\":\"http://file:8080/reports\"}");
            config.ApplyGitLab("{\"gitLabUrl\":\"http://common:8080\",\"group\":\"reports\",\"publishOnSave\":false}");
            Assert.False(ReportSaveHandler.ShouldOfferPublish(config));
            Assert.Contains("publishOnSave", ReportSaveHandler.SkipReason(config));
        }

        [Fact]
        public void Editor_shows_file_values_split_into_server_and_group()
        {
            var config = ModuleConfig.Parse("{\"reportsGitLabGroupUrl\":\"http://gitlab.example.local:8080/reports/sub\"}");
            var file = config.FileGitLab;
            Assert.Equal("http://gitlab.example.local:8080", file.GitLabUrl);
            Assert.Equal("reports/sub", file.Group);
            Assert.Contains("PilotOpenTools отчёты (GitLab)", ReportSaveHandler.SkipReason(new ModuleConfig { PublishOnSave = true }));
        }

        [Theory]
        [InlineData("", null)]
        [InlineData("{\"gitLabUrl\":\"http://gl:8080\",\"group\":\"reports\"}", null)]
        [InlineData("{\"gitLabUrl\":\"\",\"group\":\"\"}", null)]
        [InlineData("{\"gitLabUrl\":\"http://gl:8080/reports\",\"group\":\"reports\"}", "без пути")]
        [InlineData("{\"gitLabUrl\":\"ftp://gl\",\"group\":\"reports\"}", "http://")]
        [InlineData("{\"gitLabUrl\":\"http://gl:8080\"}", "группу")]
        [InlineData("{\"group\":\"reports\"}", "сервера")]
        [InlineData("{\"gitLabUrl\":\"http://gl:8080\",\"group\":\"http://gl/reports\"}", "Группа")]
        [InlineData("{\"gitLabUrl\":\"http://gl:8080\",\"group\":\"reports\",\"gitLabOAuthRedirectUri\":\"http://evil.example/cb\"}", "OAuth")]
        [InlineData("не json", "не является")]
        public void Editor_validation(string value, string expectedPart)
        {
            var error = GitLabSettings.ValidateValue(value);
            if (expectedPart == null)
                Assert.Null(error);
            else
                Assert.Contains(expectedPart, error);
        }

        [Fact]
        public void Settings_round_trip_through_json()
        {
            var settings = new GitLabSettings { GitLabUrl = "https://gl", Group = "reports", PublishOnSave = false, UseGitCredentials = false };
            var back = GitLabSettings.Parse(settings.ToJson());
            Assert.Equal("https://gl/reports", back.GroupUrl);
            Assert.False(back.PublishOnSave);
            Assert.False(back.UseGitCredentials);
        }
    }
}
