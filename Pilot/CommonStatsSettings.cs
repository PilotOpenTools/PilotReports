using System;
using System.Collections.Generic;
using Ascon.Pilot.SDK;
using Reports.Infrastructure;
using SdkCommonSettings = Pilot.ClientSdk.Pilot.CommonSettings;

namespace Reports.Pilot
{
    /// <summary>
    /// Статистика отчётов берёт адрес и ключ из раздела «Модули» общих настроек Pilot: его редактор - в Pilot.ClientSdk,
    /// здесь только подписка на значение. Администратор задаёт адрес один раз на контур, пересобирать модуль не нужно.
    /// </summary>
    internal static class CommonStatsSettings
    {
        public static IDisposable Subscribe(IPersonalSettings settings, ModuleConfig config)
        {
            if (settings == null)
                return null;
            try
            {
                return settings.SubscribeSetting(SdkCommonSettings.SettingKey).Subscribe(new Observer(config));
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось подписаться на общие настройки «Модули»: " + ex.Message);
                return null;
            }
        }

        private sealed class Observer : IObserver<KeyValuePair<string, string>>
        {
            private readonly ModuleConfig _config;

            public Observer(ModuleConfig config) => _config = config;

            public void OnNext(KeyValuePair<string, string> value)
            {
                if (value.Key != SdkCommonSettings.SettingKey)
                    return;
                _config.ApplyCommon(value.Value);
                ModuleLog.Info("Статистика: " + (_config.StatsEnabled && !string.IsNullOrWhiteSpace(_config.StatsUrl) ? _config.StatsUrl : "выключена"));
            }

            public void OnError(Exception error) => ModuleLog.Warn("Общие настройки «Модули»: ошибка подписки: " + error.Message);

            public void OnCompleted()
            {
            }
        }
    }
}
