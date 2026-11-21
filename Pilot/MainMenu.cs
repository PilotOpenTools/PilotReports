using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using Ascon.Pilot.SDK;
using Ascon.Pilot.SDK.Menu;
using Reports.Infrastructure;
using Reports.Manager;

namespace Reports.Pilot
{
    /// <summary>
    /// Меню «PilotReport» в главном меню Pilot (рядом с «Сервис»), выпадающее, как у других модулей:
    /// пункт открывает окно с деревом отчётов, загрузкой .repx с диска, сравнением/обновлением из веток GitLab
    /// и отправкой в dev или main. Видят его администраторы Pilot и люди, выбранные в общих настройках
    /// «PilotOpenTools отчёты (GitLab)».
    /// </summary>
    [Export(typeof(IMenu<MainViewContext>))]
    public sealed class MainMenu : IMenu<MainViewContext>
    {
        private const string RootItem = "Reports_PilotReport";
        private const string OpenItem = "Reports_LoadReport";
        public const string Title = "PilotReport";

        private readonly IObjectsRepository _repository;
        private ReportManagerWindow _window;

        [ImportingConstructor]
        public MainMenu(IObjectsRepository repository)
        {
            _repository = repository;
        }

        public void Build(IMenuBuilder builder, MainViewContext context)
        {
            if (builder.ItemNames.Contains(RootItem) || !Allowed())
                return;
            var submenu = builder.AddItem(RootItem, builder.Count).WithHeader(Title).WithSubmenu();
            submenu.AddItem(OpenItem, 0).WithHeader("Отчёты: загрузка, сравнение, отправка в GitLab…");
        }

        public void OnMenuItemClick(string name, MainViewContext context)
        {
            if (name != OpenItem)
                return;
            try
            {
                if (!Allowed())
                {
                    MessageBox.Show("PilotReport доступен администраторам Pilot и людям, выбранным в общих настройках " +
                                    $"«{GitLabCommonSettings.SectionTitle}».", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Одно окно на клиент: повторный щелчок возвращает к уже открытому.
                if (_window != null)
                {
                    if (_window.WindowState == WindowState.Minimized)
                        _window.WindowState = WindowState.Normal;
                    _window.Activate();
                    return;
                }

                _window = new ReportManagerWindow(new ReportManagerViewModel(ModuleConfig.Current, PilotLogin()))
                {
                    Owner = Application.Current?.MainWindow,
                };
                _window.Closed += (s, e) => _window = null;
                _window.Show();
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Окно PilotReport", ex);
                MessageBox.Show(ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Администратор Pilot или выбран в общих настройках.</summary>
        private bool Allowed()
        {
            try
            {
                var person = _repository.GetCurrentPerson();
                return person != null && ModuleConfig.Current.MenuAllowed(person.Id, person.IsAdmin);
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("PilotReport: не удалось проверить, кому показывать меню: " + ex.Message);
                return false;
            }
        }

        private string PilotLogin()
        {
            try { return _repository.GetCurrentPerson()?.Login ?? Environment.UserName; }
            catch { return Environment.UserName; }
        }
    }
}
