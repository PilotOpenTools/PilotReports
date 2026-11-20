using System.Windows;

namespace Reports.Publishing
{
    internal partial class PublishWindow : Window
    {
        internal PublishWindow(PublishViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += result =>
            {
                DialogResult = result;
                Close();
            };
            Loaded += async (s, e) => await viewModel.InitializeAsync();
        }
    }
}
