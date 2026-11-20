using System.Windows;

namespace Reports.Manager
{
    /// <summary>Запрос причины перед созданием новой версии отчёта в Pilot.</summary>
    internal partial class ReasonWindow : Window
    {
        internal ReasonWindow(string header, string details, string reason = null, string okText = null)
        {
            InitializeComponent();
            Header.Text = header;
            Details.Text = details;
            Details.Visibility = string.IsNullOrEmpty(details) ? Visibility.Collapsed : Visibility.Visible;
            ReasonBox.Text = reason ?? "";
            if (!string.IsNullOrEmpty(okText))
                OkButton.Content = okText;
            Loaded += (s, e) =>
            {
                ReasonBox.Focus();
                ReasonBox.SelectAll();
            };
        }

        public string Reason => ReasonBox.Text.Trim();

        private void OnOk(object sender, RoutedEventArgs e)
        {
            if (Reason.Length == 0)
            {
                Error.Text = "Укажите причину обновления.";
                return;
            }
            DialogResult = true;
        }
    }
}
