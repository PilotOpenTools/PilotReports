using System.Windows;

namespace Reports.Manager
{
    internal partial class ReportManagerWindow : Window
    {
        private readonly ReportManagerViewModel _viewModel;

        internal ReportManagerWindow(ReportManagerViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            viewModel.Owner = () => this;
            DataContext = viewModel;
            Loaded += async (s, e) =>
            {
                SearchBox.Focus();
                await viewModel.InitializeAsync();
            };
        }

        private async void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
            await _viewModel.SelectAsync(e.NewValue as ReportNode);
    }
}
