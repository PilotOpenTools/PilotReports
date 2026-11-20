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
    /// Пункт «Загрузить отчёт» в главном меню Pilot (рядом с «Сервис»): окно с деревом отчётов,
    /// загрузкой .repx с диска и сравнением/обновлением из веток GitLab.
    /// </summary>
    [Export(typeof(IMenu<MainViewContext>))]
    public sealed class MainMenu : IMenu<MainViewContext>
    {
        private const string LoadReportItem = "Reports_LoadReport";

        private readonly IObjectsRepository _repository;
        private ReportManagerWindow _window;

        [ImportingConstructor]
        public MainMenu(IObjectsRepository repository)
        {
            _repository = repository;
        }

        public void Build(IMenuBuilder builder, MainViewContext context)
        {
            if (!builder.ItemNames.Contains(LoadReportItem))
                builder.AddItem(LoadReportItem, builder.Count).WithHeader("Загрузить отчёт");
        }

        public void OnMenuItemClick(string name, MainViewContext context)
        {
            if (name != LoadReportItem)
                return;
            try
            {
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
                ModuleLog.Error("Окно «Загрузить отчёт»", ex);
                MessageBox.Show(ex.Message, "Reports", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string PilotLogin()
        {
            try { return _repository.GetCurrentPerson()?.Login ?? Environment.UserName; }
            catch { return Environment.UserName; }
        }
    }
}
