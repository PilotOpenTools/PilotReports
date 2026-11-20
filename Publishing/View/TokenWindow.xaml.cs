using System.Windows;

namespace Reports.Publishing
{
    internal partial class TokenWindow : Window
    {
        internal TokenWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => TokenBox.Focus();
        }

        public string Token => TokenBox.Password;

        private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
