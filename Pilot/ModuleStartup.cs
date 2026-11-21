using System;
using System.ComponentModel.Composition;
using Ascon.Pilot.SDK;
using Reports.Infrastructure;
using Reports.Stats;
using StartupRunner = Pilot.ClientSdk.StartupRunner;
using IModule = Pilot.ClientSdk.IModule;

namespace Reports.Pilot
{
    /// <summary>
    /// Точка входа модуля. IDataPlugin клиент Pilot создаёт при старте - здесь сервисы SDK
    /// сохраняются в <see cref="PilotApi"/>, чтобы скрипты отчётов могли ими пользоваться,
    /// и запускается отправка статистики.
    /// IModule - по регламенту: окно «Загрузка модулей» показывает, запустился ли модуль и за сколько.
    /// </summary>
    [Export(typeof(IDataPlugin))]
    public sealed class ModuleStartup : IDataPlugin, IModule
    {
        private readonly IObjectsRepository _repository;
        private readonly ISearchService _searchService;
        private readonly IObjectModifier _modifier;
        private readonly IFileProvider _fileProvider;
        private readonly IPilotDialogService _dialogs;
        private readonly ITabServiceProvider _tabs;
        private readonly IClientInfo _clientInfo;
        private readonly IDisposable _settingsSubscription;
        private readonly IDisposable _gitLabSubscription;

        [ImportingConstructor]
        public ModuleStartup(
            IObjectsRepository repository,
            ISearchService searchService,
            IObjectModifier modifier,
            IFileProvider fileProvider,
            IPilotDialogService dialogs,
            ITabServiceProvider tabs,
            IClientInfo clientInfo,
            IPersonalSettings personalSettings)
        {
            _repository = repository;
            _searchService = searchService;
            _modifier = modifier;
            _fileProvider = fileProvider;
            _dialogs = dialogs;
            _tabs = tabs;
            _clientInfo = clientInfo;
            // Адрес статистики задаёт администратор в общих настройках: подписка живёт весь сеанс клиента.
            _settingsSubscription = CommonStatsSettings.Subscribe(personalSettings, ModuleConfig.Current);
            // Сервер и группу GitLab - тоже: раздел «PilotOpenTools отчёты (GitLab)».
            _gitLabSubscription = GitLabCommonSettings.Subscribe(personalSettings, ModuleConfig.Current);
            // Исключение из Initialize не пробрасывается (клиент запустится), а попадает в окно загрузки и в Grafana.
            StartupRunner.Run(this);
        }

        public void Initialize()
        {
            try
            {
                PilotApi.Init(_repository, _searchService, _modifier, _fileProvider, _dialogs, _tabs, _clientInfo);

                var config = ModuleConfig.Current;
                ReportStats.Start(config, ClientEnvironment.Capture(_repository, _clientInfo));

                ModuleLog.Info($"Reports {ClientEnvironment.ModuleVersion} загружен. Статистика: " +
                               (config.StatsEnabled && !string.IsNullOrWhiteSpace(config.StatsUrl) ? config.StatsUrl : "выключена"));
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Ошибка инициализации Reports", ex);
                throw;
            }
        }
    }
}
