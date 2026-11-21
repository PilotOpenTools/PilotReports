using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ascon.Pilot.SDK;
using Reports.Infrastructure;

namespace Reports.Pilot
{
    /// <summary>
    /// Редактор раздела «PilotOpenTools отчёты (GitLab)». Каждое изменение сразу пишется в значение настройки
    /// (<see cref="ISettingValueProvider.SetValue"/>), Pilot сохраняет его кнопкой «ОК»/«Применить» окна настроек.
    /// Окно собирается в коде, без XAML - как редактор раздела статистики в Pilot.ClientSdk.
    /// </summary>
    internal sealed class GitLabSettingsEditor : UserControl
    {
        private static readonly Brush Gray = Frozen(0x80, 0x80, 0x80);
        private static readonly Brush Green = Frozen(0x2E, 0x7D, 0x32);
        private static readonly Brush Red = Frozen(0xC6, 0x28, 0x28);

        private readonly ISettingValueProvider _provider;
        private readonly GitLabSettings _file;
        private readonly TextBox _server = new TextBox();
        private readonly TextBox _group = new TextBox();
        private readonly TextBlock _check = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        private readonly CheckBox _publishOnSave = new CheckBox { Content = "При сохранении отчёта в дизайнере предлагать отправить его в GitLab" };
        private readonly TextBox _clientId = new TextBox();
        private readonly TextBox _redirectUri = new TextBox();
        private readonly CheckBox _gitCredentials = new CheckBox { Content = "Брать токен из менеджера учётных данных git, если пользователь не вошёл в GitLab из Pilot" };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        private readonly TextBox _peopleFilter = new TextBox();
        private readonly StackPanel _peopleList = new StackPanel();
        private readonly TextBlock _peopleCount = new TextBlock { Foreground = Gray, Margin = new Thickness(0, 4, 0, 0) };
        private readonly List<PersonItem> _people;
        private readonly HashSet<int> _menuPersons = new HashSet<int>();
        private bool _loading;

        /// <summary>Человек в списке «кому показывать PilotReport».</summary>
        internal sealed class PersonItem
        {
            public PersonItem(int id, string name, string login, bool isAdmin)
            {
                Id = id;
                Name = name;
                Login = login;
                IsAdmin = isAdmin;
            }

            public int Id { get; }
            public string Name { get; }
            public string Login { get; }
            public bool IsAdmin { get; }
        }

        public GitLabSettingsEditor(ISettingValueProvider provider, GitLabSettings file, List<PersonItem> people)
        {
            _provider = provider;
            _file = file ?? new GitLabSettings();
            _people = people ?? new List<PersonItem>();

            var root = new StackPanel { Margin = new Thickness(4) };
            root.Children.Add(Group("GitLab с отчётами",
                Label("Адрес сервера GitLab (http://сервер:порт, без пути):"),
                ServerRow(),
                Label("Группа с отчётами (путь в GitLab, можно с подгруппой: reports или pilot/reports):"),
                _group,
                _check,
                Spaced(_publishOnSave)));
            root.Children.Add(Group("Вход в GitLab",
                Label("Application ID приложения GitLab для входа через браузер (пусто - только токен):"),
                _clientId,
                Label("Redirect URI приложения (локальный адрес):"),
                _redirectUri,
                Spaced(_gitCredentials)));
            root.Children.Add(Group("Кнопка PilotReport в главном меню",
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "Кому показывать: отметьте людей. Администраторы Pilot видят кнопку всегда. " +
                           "Изменение у пользователя видно после перезапуска клиента.",
                },
                Label("Поиск по имени или логину:"),
                _peopleFilter,
                new ScrollViewer { Content = _peopleList, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0) },
                _peopleCount));
            var reset = new Button { Content = "Очистить раздел (действуют настройки из файла " + ModuleConfig.FileName + ")", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 6, 0, 0) };
            reset.Click += (s, e) => Reset();
            root.Children.Add(reset);
            root.Children.Add(_status);
            root.Children.Add(new TextBlock
            {
                Foreground = Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
                Text = "Настройки действуют на всех пользователей и важнее файла " + ModuleConfig.FileName + ". " +
                       "У пользователей применяются сразу, без перезапуска клиента. Токены GitLab здесь не хранятся: " +
                       "каждый пользователь входит в GitLab сам, в окне отправки отчёта.",
            });
            Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            Load();

            foreach (var box in new[] { _publishOnSave, _gitCredentials })
            {
                box.Checked += (s, e) => Save();
                box.Unchecked += (s, e) => Save();
            }
            foreach (var text in new[] { _server, _group, _clientId, _redirectUri })
                text.TextChanged += (s, e) => Save();
            _peopleFilter.TextChanged += (s, e) => FillPeople();
        }

        /// <summary>Список людей с галочками (по фильтру). Отмеченные - вверху, администраторы помечены.</summary>
        private void FillPeople()
        {
            _peopleList.Children.Clear();
            var q = _peopleFilter.Text?.Trim();
            var shown = _people
                .Where(p => string.IsNullOrEmpty(q) ||
                            (p.Name?.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) ?? -1) >= 0 ||
                            (p.Login?.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) ?? -1) >= 0)
                .OrderByDescending(p => _menuPersons.Contains(p.Id) || p.IsAdmin)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase);
            foreach (var person in shown)
            {
                var box = new CheckBox
                {
                    Content = person.Name + (string.IsNullOrEmpty(person.Login) || person.Login == person.Name ? "" : $" ({person.Login})") +
                              (person.IsAdmin ? " - администратор, видит всегда" : ""),
                    IsChecked = person.IsAdmin || _menuPersons.Contains(person.Id),
                    IsEnabled = !person.IsAdmin,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                var id = person.Id;
                box.Checked += (s, e) => { _menuPersons.Add(id); UpdatePeopleCount(); Save(); };
                box.Unchecked += (s, e) => { _menuPersons.Remove(id); UpdatePeopleCount(); Save(); };
                _peopleList.Children.Add(box);
            }
            UpdatePeopleCount();
        }

        private void UpdatePeopleCount()
        {
            var admins = _people.Count(p => p.IsAdmin);
            _peopleCount.Text = $"Отмечено: {_menuPersons.Count}, администраторов: {admins}" +
                                (_people.Count == 0 ? ". Список людей не получен из Pilot." : "");
        }

        /// <summary>Проверка значения настройки: null - значение подходит, иначе текст ошибки.</summary>
        internal static string Validate(string value) => GitLabSettings.ValidateValue(value);

        private void Load()
        {
            _loading = true;
            try
            {
                var value = _provider?.GetValue();
                GitLabSettings settings = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    try
                    {
                        settings = GitLabSettings.Parse(value);
                    }
                    catch (Exception ex)
                    {
                        ShowStatus("Сохранённое значение не разобрано, показаны настройки из файла: " + ex.GetBaseException().Message, Red);
                    }
                }
                if (settings == null)
                {
                    // Раздел пуст: показываем то, что действует сейчас, - администратор правит от текущих значений.
                    settings = _file;
                    if (_status.Text.Length == 0)
                        ShowStatus("Раздел ещё не заполнен - показаны настройки из файла " + ModuleConfig.FileName + ". Любое изменение сохранит их в общие настройки.", Gray);
                }

                _server.Text = settings.GitLabUrl ?? "";
                _group.Text = settings.Group ?? "";
                _publishOnSave.IsChecked = settings.PublishOnSave;
                _clientId.Text = settings.GitLabOAuthClientId ?? "";
                _redirectUri.Text = settings.GitLabOAuthRedirectUri ?? ModuleConfig.DefaultRedirectUri;
                _gitCredentials.IsChecked = settings.UseGitCredentials;
                _menuPersons.Clear();
                foreach (var id in settings.MenuPersonIds ?? Array.Empty<int>())
                    _menuPersons.Add(id);
                FillPeople();
            }
            finally
            {
                _loading = false;
            }
        }

        private void Save()
        {
            if (_loading || _provider == null)
                return;

            var settings = new GitLabSettings
            {
                GitLabUrl = Trimmed(_server.Text)?.TrimEnd('/'),
                Group = Trimmed(_group.Text)?.Trim('/'),
                PublishOnSave = _publishOnSave.IsChecked == true,
                GitLabOAuthClientId = Trimmed(_clientId.Text),
                GitLabOAuthRedirectUri = Trimmed(_redirectUri.Text) ?? ModuleConfig.DefaultRedirectUri,
                UseGitCredentials = _gitCredentials.IsChecked == true,
                MenuPersonIds = _menuPersons.Count == 0 ? null : _menuPersons.OrderBy(i => i).ToArray(),
            };

            var error = settings.Validate();
            if (error != null)
            {
                ShowStatus(error + " Не сохранено.", Red);
                return;
            }
            _provider.SetValue(settings.ToJson());
            ShowStatus(settings.GroupUrl == null
                ? "Сервер и группа не указаны - отправка отчётов в GitLab будет выключена. Сохраняется кнопкой «ОК» или «Применить»."
                : "Группа: " + settings.GroupUrl + ". Изменения будут сохранены кнопкой «ОК» или «Применить».", Gray);
        }

        private void Reset()
        {
            _provider?.SetValue(string.Empty);
            _status.Text = "";
            Load();
            ShowStatus("Раздел очищен: после «ОК» у пользователей будут действовать настройки из файла " + ModuleConfig.FileName + ".", Gray);
        }

        private FrameworkElement ServerRow()
        {
            var check = new Button { Content = "Проверить", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(6, 0, 0, 0) };
            check.Click += async (s, e) => await CheckAsync(check);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(check, 1);
            grid.Children.Add(_server);
            grid.Children.Add(check);
            return grid;
        }

        /// <summary>GET {сервер}/api/v4/groups/{группа} без токена: доступен ли GitLab с этой машины и видна ли группа.</summary>
        private async Task CheckAsync(Button button)
        {
            var settings = new GitLabSettings { GitLabUrl = Trimmed(_server.Text)?.TrimEnd('/'), Group = Trimmed(_group.Text)?.Trim('/') };
            if (string.IsNullOrEmpty(settings.GitLabUrl))
            {
                SetCheck("Укажите адрес сервера, например http://gitlab.example.local:8080.", Red);
                return;
            }
            var error = string.IsNullOrEmpty(settings.Group) ? null : settings.Validate();
            if (error != null)
            {
                SetCheck(error, Red);
                return;
            }

            var url = string.IsNullOrEmpty(settings.Group)
                ? settings.GitLabUrl + "/api/v4/version"
                : settings.GitLabUrl + "/api/v4/groups/" + Uri.EscapeDataString(settings.Group);
            button.IsEnabled = false;
            SetCheck("Проверка " + url + " …", Gray);
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                using (var response = await http.GetAsync(url))
                {
                    if (response.IsSuccessStatusCode)
                        SetCheck(string.IsNullOrEmpty(settings.Group) ? "GitLab доступен с этого компьютера." : "GitLab доступен, группа найдена.", Green);
                    else if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Unauthorized)
                        SetCheck(string.IsNullOrEmpty(settings.Group)
                            ? "GitLab доступен с этого компьютера."
                            : "GitLab доступен, но группа не видна без входа: она закрытая или путь неверный. Проверьте путь в адресной строке группы в GitLab.",
                            string.IsNullOrEmpty(settings.Group) ? Green : Gray);
                    else
                        SetCheck($"Сервер ответил {(int)response.StatusCode} {response.ReasonPhrase}.", Red);
                }
            }
            catch (Exception ex)
            {
                SetCheck("GitLab недоступен: " + ex.GetBaseException().Message, Red);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private static string Trimmed(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        private void SetCheck(string text, Brush brush)
        {
            _check.Text = text;
            _check.Foreground = brush;
        }

        private void ShowStatus(string text, Brush brush)
        {
            _status.Text = text;
            _status.Foreground = brush;
        }

        private static GroupBox Group(string header, params UIElement[] children)
        {
            var panel = new StackPanel { Margin = new Thickness(6) };
            foreach (var child in children)
            {
                if (child is FrameworkElement fe && fe.Margin == default(Thickness) && !(child is TextBlock))
                    fe.Margin = new Thickness(0, 2, 0, 2);
                panel.Children.Add(child);
            }
            return new GroupBox { Header = header, Content = panel, Margin = new Thickness(0, 0, 0, 8) };
        }

        private static TextBlock Label(string text) =>
            new TextBlock { Text = text, Margin = new Thickness(0, 8, 0, 2), TextWrapping = TextWrapping.Wrap };

        private static UIElement Spaced(FrameworkElement element)
        {
            element.Margin = new Thickness(0, 8, 0, 2);
            return element;
        }

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}
