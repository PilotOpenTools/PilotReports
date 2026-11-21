using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Reports.Infrastructure
{
    /// <summary>
    /// Значение раздела «PilotOpenTools отчёты (GitLab)» общих настроек Pilot: куда отправлять отчёты.
    /// Сервер и группа хранятся отдельно, модуль собирает из них адрес группы (<see cref="GroupUrl"/>).
    /// </summary>
    [DataContract]
    internal sealed class GitLabSettings
    {
        /// <summary>Адрес сервера GitLab: http(s)://сервер[:порт], без пути.</summary>
        [DataMember(Name = "gitLabUrl")]
        public string GitLabUrl { get; set; }

        /// <summary>Путь группы с отчётами, можно с подгруппой: reports или pilot/reports.</summary>
        [DataMember(Name = "group")]
        public string Group { get; set; }

        [DataMember(Name = "publishOnSave")]
        public bool PublishOnSave { get; set; } = true;

        [DataMember(Name = "gitLabOAuthClientId")]
        public string GitLabOAuthClientId { get; set; }

        [DataMember(Name = "gitLabOAuthRedirectUri")]
        public string GitLabOAuthRedirectUri { get; set; } = ModuleConfig.DefaultRedirectUri;

        [DataMember(Name = "useGitCredentials")]
        public bool UseGitCredentials { get; set; } = true;

        /// <summary>Адрес группы для API: сервер + группа; null - сервер или группа не заданы.</summary>
        public string GroupUrl => Combine(GitLabUrl, Group);

        internal static string Combine(string server, string group)
        {
            var s = server?.Trim().TrimEnd('/');
            var g = group?.Trim().Trim('/');
            return string.IsNullOrEmpty(s) || string.IsNullOrEmpty(g) ? null : s + "/" + g;
        }

        /// <summary>Настройки из адреса группы в файле: http://host:8080/reports / (http://host:8080, reports).</summary>
        internal static GitLabSettings FromConfig(ModuleConfig config)
        {
            var settings = new GitLabSettings
            {
                PublishOnSave = config.PublishOnSave,
                GitLabOAuthClientId = config.GitLabOAuthClientId,
                GitLabOAuthRedirectUri = config.GitLabOAuthRedirectUri,
                UseGitCredentials = config.UseGitCredentials,
            };
            if (Publishing.ReportPublishService.TryParseGroupUrl(config.ReportsGitLabGroupUrl, out var baseUrl, out var group))
            {
                settings.GitLabUrl = baseUrl;
                settings.Group = group;
            }
            return settings;
        }

        /// <summary>Проверка: null - настройки подходят, иначе текст ошибки.</summary>
        internal string Validate()
        {
            var hasServer = !string.IsNullOrWhiteSpace(GitLabUrl);
            var hasGroup = !string.IsNullOrWhiteSpace(Group);
            if (hasServer)
            {
                if (!Uri.TryCreate(GitLabUrl.Trim(), UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return "Адрес сервера GitLab должен начинаться с http:// или https://.";
                if (uri.AbsolutePath.Trim('/').Length > 0 || !string.IsNullOrEmpty(uri.Query))
                    return "Адрес сервера GitLab - без пути: http://сервер:порт. Группу укажите отдельно.";
            }
            if (hasServer != hasGroup)
                return hasServer ? "Укажите группу GitLab с отчётами." : "Укажите адрес сервера GitLab.";
            if (hasGroup && (Group.Contains("://") || Group.Contains("?") || Group.Contains(" ")))
                return "Группа - путь вида reports или pilot/reports, без адреса сервера и пробелов.";
            if (!string.IsNullOrWhiteSpace(GitLabOAuthRedirectUri) &&
                (!Uri.TryCreate(GitLabOAuthRedirectUri.Trim(), UriKind.Absolute, out var redirect) || !redirect.IsLoopback))
                return "Адрес возврата OAuth - локальный, например " + ModuleConfig.DefaultRedirectUri + ".";
            return null;
        }

        /// <summary>Проверка значения раздела (JSON): null - подходит (пустое - тоже: действует файл), иначе текст ошибки.</summary>
        internal static string ValidateValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            try
            {
                return Parse(value).Validate();
            }
            catch (Exception ex)
            {
                return "Значение не является настройками GitLab для отчётов: " + ex.GetBaseException().Message;
            }
        }

        internal static GitLabSettings Parse(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(GitLabSettings));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (GitLabSettings)serializer.ReadObject(stream);
        }

        internal string ToJson()
        {
            var serializer = new DataContractJsonSerializer(typeof(GitLabSettings));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, this);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        // DataContractJsonSerializer не вызывает инициализаторы свойств - выставляем значения по умолчанию сами.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            PublishOnSave = true;
            UseGitCredentials = true;
            GitLabOAuthRedirectUri = ModuleConfig.DefaultRedirectUri;
        }
    }
}
