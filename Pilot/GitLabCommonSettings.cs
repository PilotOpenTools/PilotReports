using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using Ascon.Pilot.SDK;
using Reports.Infrastructure;

namespace Reports.Pilot
{
    /// <summary>
    /// Раздел «PilotOpenTools отчёты (GitLab)» в общих настройках Pilot (Сервис / Настройки / Общие настройки):
    /// сервер и группа GitLab с отчётами, OAuth-приложение, окно отправки при сохранении.
    /// Задаёт администратор; клиенты получают значение при запуске и при каждом изменении (без перезапуска).
    /// Пока раздел не заполнен, действуют настройки из Reports.config.json.
    /// </summary>
    [Export(typeof(ISettingsFeature2))]
    public sealed class GitLabCommonSettings : ISettingsFeature2
    {
        /// <summary>Ключ настройки в базе Pilot. Не менять: по нему клиенты находят сохранённое значение.</summary>
        public const string SettingKey = "Reports-GitLab-3A8E5D21-7C4B-4F96-B0E2-9D1A6C58F473";

        public const string SectionTitle = "PilotOpenTools отчёты (GitLab)";

        private ISettingValueProvider _valueProvider;

        public string Key => SettingKey;

        public string Title => SectionTitle;

        public FrameworkElement Editor
        {
            get
            {
                try
                {
                    return new GitLabSettingsEditor(_valueProvider, ModuleConfig.Current.FileGitLab);
                }
                catch (Exception ex)
                {
                    ModuleLog.Error("Не удалось открыть редактор настроек GitLab", ex);
                    MessageBox.Show("Не удалось открыть настройки отчётов: " + ex.Message, "Reports",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return null;
                }
            }
        }

        public bool IsValid(string settingsItemValue) => GitLabSettingsEditor.Validate(settingsItemValue) == null;

        public void SetValueProvider(ISettingValueProvider settingValueProvider)
        {
            _valueProvider = settingValueProvider;
        }

        /// <summary>Подписка на значение раздела: каждое изменение сразу применяется (<see cref="ModuleConfig.ApplyGitLab"/>).</summary>
        internal static IDisposable Subscribe(IPersonalSettings settings, ModuleConfig config)
        {
            if (settings == null)
                return null;
            try
            {
                return settings.SubscribeSetting(SettingKey).Subscribe(new Observer(config));
            }
            catch (Exception ex)
            {
                ModuleLog.Warn($"Не удалось подписаться на общие настройки «{SectionTitle}»: " + ex.Message);
                return null;
            }
        }

        private sealed class Observer : IObserver<KeyValuePair<string, string>>
        {
            private readonly ModuleConfig _config;

            public Observer(ModuleConfig config) => _config = config;

            public void OnNext(KeyValuePair<string, string> value)
            {
                if (value.Key != SettingKey)
                    return;
                _config.ApplyGitLab(value.Value);
                ModuleLog.Info("GitLab: " + (string.IsNullOrWhiteSpace(_config.ReportsGitLabGroupUrl)
                    ? "группа не указана - отправка отчётов выключена"
                    : _config.ReportsGitLabGroupUrl + (_config.PublishOnSave ? "" : " (окно при сохранении выключено)")) +
                    (string.IsNullOrWhiteSpace(value.Value) ? ", из файла " + ModuleConfig.FileName : ", из общих настроек"));
            }

            public void OnError(Exception error) => ModuleLog.Warn($"Общие настройки «{SectionTitle}»: ошибка подписки: " + error.Message);

            public void OnCompleted()
            {
            }
        }
    }
}
