using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Reports.Infrastructure
{
    /// <summary>
    /// Настройки модуля. Читаются из Reports.config.json рядом с dll модуля.
    /// Статистику (включена ли, адрес, ключ) переопределяют общие настройки Pilot, раздел «Модули» (<see cref="ApplyCommon"/>),
    /// GitLab (сервер, группа, OAuth, окно при сохранении) - раздел «PilotOpenTools отчёты (GitLab)» (<see cref="ApplyGitLab"/>).
    /// Важнее всего адрес из переменной окружения REPORTS_STATS_URL (удобно на машине разработчика).
    /// </summary>
    [DataContract]
    internal sealed class ModuleConfig
    {
        public const string FileName = "Reports.config.json";

        [DataMember(Name = "statsEnabled")]
        public bool StatsEnabled { get; set; } = true;

        [DataMember(Name = "statsUrl")]
        public string StatsUrl { get; set; }

        [DataMember(Name = "apiKey")]
        public string ApiKey { get; set; }

        /// <summary>Группа GitLab с отчётами: при сохранении отчёта модуль ищет в ней проект с тем же именем.</summary>
        [DataMember(Name = "reportsGitLabGroupUrl")]
        public string ReportsGitLabGroupUrl { get; set; }

        /// <summary>Показывать окно отправки в GitLab при сохранении отчёта.</summary>
        [DataMember(Name = "publishOnSave")]
        public bool PublishOnSave { get; set; } = true;

        /// <summary>
        /// Application ID приложения GitLab для входа через браузер (OAuth). Приложение - НЕ confidential
        /// (публичное, с PKCE), scope api, Redirect URI = <see cref="GitLabOAuthRedirectUri"/>. Пусто - только токен.
        /// </summary>
        [DataMember(Name = "gitLabOAuthClientId")]
        public string GitLabOAuthClientId { get; set; }

        /// <summary>Локальный адрес, на который браузер вернёт код входа. Должен совпадать с Redirect URI приложения.</summary>
        [DataMember(Name = "gitLabOAuthRedirectUri")]
        public string GitLabOAuthRedirectUri { get; set; } = DefaultRedirectUri;

        /// <summary>Брать токен из менеджера учётных данных git, если своего нет.</summary>
        [DataMember(Name = "useGitCredentials")]
        public bool UseGitCredentials { get; set; } = true;

        public const string DefaultRedirectUri = "http://127.0.0.1:7890/callback";

        private const string EnvStatsUrl = "REPORTS_STATS_URL";
        private const string ModuleEventsPath = "/api/module-events";
        private const string ReportEventsPath = "/api/report-events";

        // Статистика из файла: к ней настройки возвращаются, когда раздел общих настроек очищен.
        private bool _fileStatsSaved;
        private bool _fileStatsEnabled;
        private string _fileStatsUrl;
        private string _fileApiKey;

        // GitLab из файла: к нему настройки возвращаются, когда раздел «PilotOpenTools отчёты (GitLab)» очищен.
        private GitLabSettings _fileGitLab;

        private static ModuleConfig _current;

        /// <summary>Настройки из папки модуля (читаются один раз).</summary>
        public static ModuleConfig Current =>
            _current ?? (_current = Load(Path.GetDirectoryName(typeof(ModuleConfig).Assembly.Location)));

        public static ModuleConfig Load(string directory)
        {
            var config = TryRead(Path.Combine(directory ?? string.Empty, FileName)) ?? new ModuleConfig();

            config.ApplyEnvironment();
            return config;
        }

        /// <summary>
        /// Применяет значение раздела «Модули» общих настроек Pilot: тот же JSON, что у Pilot.ClientSdk.
        /// Пустое значение возвращает статистику из файла, неразборчивое оставляет прежние настройки.
        /// </summary>
        public void ApplyCommon(string json)
        {
            if (!_fileStatsSaved)
            {
                _fileStatsSaved = true;
                _fileStatsEnabled = StatsEnabled;
                _fileStatsUrl = StatsUrl;
                _fileApiKey = ApiKey;
            }

            var enabled = _fileStatsEnabled;
            var url = _fileStatsUrl;
            var apiKey = _fileApiKey;
            if (!string.IsNullOrWhiteSpace(json))
            {
                ModuleConfig common;
                try
                {
                    common = Parse(json);
                }
                catch (Exception ex)
                {
                    ModuleLog.Error("Общие настройки «Модули» не разобраны - действуют прежние", ex);
                    return;
                }

                enabled = common.StatsEnabled;
                var reportUrl = ReportEventsUrl(common.StatsUrl);
                if (reportUrl != null)
                {
                    url = reportUrl;
                    apiKey = common.ApiKey;
                }
                else if (!string.IsNullOrWhiteSpace(common.StatsUrl))
                {
                    ModuleLog.Warn($"Общие настройки «Модули»: неверный адрес статистики '{common.StatsUrl}', действует адрес из файла");
                }
            }

            StatsEnabled = enabled;
            StatsUrl = url;
            ApiKey = apiKey;
            ApplyEnvironment();
        }

        /// <summary>Настройки GitLab из файла Reports.config.json - редактор показывает их, пока раздел не заполнен.</summary>
        internal GitLabSettings FileGitLab => _fileGitLab ?? GitLabSettings.FromConfig(this);

        /// <summary>
        /// Применяет раздел «PilotOpenTools отчёты (GitLab)» общих настроек Pilot: сервер и группа GitLab, OAuth, окно при сохранении.
        /// Пустое значение возвращает настройки из файла, неразборчивое или неверное оставляет прежние.
        /// </summary>
        public void ApplyGitLab(string json)
        {
            if (_fileGitLab == null)
                _fileGitLab = GitLabSettings.FromConfig(this);

            var settings = _fileGitLab;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    settings = GitLabSettings.Parse(json);
                }
                catch (Exception ex)
                {
                    ModuleLog.Error("Общие настройки «PilotOpenTools отчёты (GitLab)» не разобраны - действуют прежние", ex);
                    return;
                }
                var error = settings.Validate();
                if (error != null)
                {
                    ModuleLog.Warn("Общие настройки «PilotOpenTools отчёты (GitLab)»: " + error + " Действуют прежние.");
                    return;
                }
            }

            ReportsGitLabGroupUrl = settings.GroupUrl;
            PublishOnSave = settings.PublishOnSave;
            GitLabOAuthClientId = string.IsNullOrWhiteSpace(settings.GitLabOAuthClientId) ? null : settings.GitLabOAuthClientId.Trim();
            GitLabOAuthRedirectUri = string.IsNullOrWhiteSpace(settings.GitLabOAuthRedirectUri) ? DefaultRedirectUri : settings.GitLabOAuthRedirectUri.Trim();
            UseGitCredentials = settings.UseGitCredentials;
        }

        /// <summary>В общих настройках адрес статистики модулей; отчёты шлют на тот же сервис, другой метод API.</summary>
        internal static string ReportEventsUrl(string statsUrl)
        {
            if (!Uri.TryCreate(statsUrl?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;

            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith(ModuleEventsPath, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(0, path.Length - ModuleEventsPath.Length);
            return uri.GetLeftPart(UriPartial.Authority) + path + ReportEventsPath;
        }

        private void ApplyEnvironment()
        {
            var envUrl = Environment.GetEnvironmentVariable(EnvStatsUrl);
            if (!string.IsNullOrWhiteSpace(envUrl))
                StatsUrl = envUrl.Trim();
        }

        internal static ModuleConfig Parse(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(ModuleConfig));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (ModuleConfig)serializer.ReadObject(stream);
        }

        private static ModuleConfig TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Не удалось прочитать " + path, ex);
                return null;
            }
        }

        // DataContractJsonSerializer не вызывает инициализаторы свойств - выставляем значения по умолчанию сами.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            StatsEnabled = true;
            PublishOnSave = true;
            UseGitCredentials = true;
            GitLabOAuthRedirectUri = DefaultRedirectUri;
        }
    }
}
