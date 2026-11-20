using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Reports.Infrastructure;

namespace Reports.Publishing
{
    /// <summary>
    /// Репозитория отчёта нет: выбор подгруппы (с правами пользователя GitLab в каждой) и создание.
    /// Папки, где создавать нельзя, видны, но недоступны.
    /// </summary>
    internal partial class CreateRepoWindow : Window
    {
        private readonly ReportPublishService _service;
        private readonly string _reportName;
        private readonly string _pilotFolder;
        private List<RepoFolder> _folders = new List<RepoFolder>();
        private bool _busy;

        internal CreateRepoWindow(ReportPublishService service, string reportName, string pilotFolder)
        {
            InitializeComponent();
            _service = service;
            _reportName = reportName;
            _pilotFolder = pilotFolder;
            Header.Text = $"Репозитория для отчёта «{reportName}» нет в группе {service.GroupPath}";
            NameBox.Text = ReportNames.Suggest(reportName);
            PilotFolder.Text = string.IsNullOrEmpty(pilotFolder) ? "" : $"Папка отчёта в Pilot: {pilotFolder}";
            Loaded += async (s, e) => await LoadFoldersAsync();
        }

        /// <summary>Созданный репозиторий (после DialogResult = true).</summary>
        public ReportRepository Repository { get; private set; }

        private async System.Threading.Tasks.Task LoadFoldersAsync()
        {
            SetBusy(true, "Проверка прав в GitLab…");
            try
            {
                _folders = await ReportRepoCreation.ListFoldersAsync(_service.Api, _service.GroupPath);
                FolderBox.ItemsSource = _folders;

                var allowed = _folders.Where(f => f.CanCreate).ToList();
                if (allowed.Count == 0)
                {
                    Error.Text = $"Нет прав на создание репозитория ни в одной папке группы {_service.GroupPath}: " +
                                 "нужна роль, которую группа разрешает для создания проектов (обычно Maintainer). " +
                                 "Попросите владельца группы выдать права или создать репозиторий в веб-админке.";
                    FolderBox.SelectedItem = _folders.FirstOrDefault();
                    return;
                }

                var guess = ReportRepoCreation.GuessFolder(_pilotFolder, allowed.Select(f => f.Folder));
                FolderBox.SelectedItem = allowed.FirstOrDefault(f => f.Folder == guess) ?? allowed.First();
                Error.Text = "";
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Список папок GitLab для создания репозитория", ex);
                Error.Text = ex.Message;
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e) => Validate();

        private void OnFolderChanged(object sender, SelectionChangedEventArgs e) => Validate();

        private string Validate()
        {
            var error = ReportNames.NameError(NameBox.Text);
            if (error == null && ReportNames.Key(NameBox.Text) != ReportNames.Key(_reportName))
                error = "Имя должно совпадать с именем отчёта (можно убрать только недопустимые символы) - иначе модуль не найдёт репозиторий.";
            if (error == null && !(FolderBox.SelectedItem is RepoFolder folder && folder.CanCreate))
                error = "Выберите папку, где у вас есть права на создание.";
            if (!_busy)
            {
                CreateButton.IsEnabled = error == null;
                if (_folders.Any(f => f.CanCreate))
                    Error.Text = error ?? "";
            }
            return error;
        }

        private async void OnCreate(object sender, RoutedEventArgs e)
        {
            if (Validate() != null)
                return;
            var folder = (RepoFolder)FolderBox.SelectedItem;
            var name = NameBox.Text.Trim();
            SetBusy(true, "Создание репозитория…");
            try
            {
                Repository = await ReportRepoCreation.CreateAsync(_service.Api, name, folder);
                ModuleLog.Info($"Создан репозиторий отчёта «{_reportName}»: {Repository.Project.WebUrl}");
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ModuleLog.Error($"Создание репозитория отчёта «{_reportName}»", ex);
                SetBusy(false, ex.Message);
            }
        }

        private void SetBusy(bool busy, string text)
        {
            _busy = busy;
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            NameBox.IsEnabled = FolderBox.IsEnabled = !busy;
            CreateButton.IsEnabled = !busy && Validate() == null;
            if (text != null)
                Error.Text = text;
        }
    }
}
