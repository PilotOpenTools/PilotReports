using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Reports.Infrastructure;

namespace Reports.Publishing
{
    internal partial class HistoryWindow : Window
    {
        internal HistoryWindow(IReadOnlyList<GitLabCommit> commits)
        {
            InitializeComponent();
            Grid.ItemsSource = commits;
        }

        private void OnOpen(object sender, MouseButtonEventArgs e)
        {
            if (Grid.SelectedItem is GitLabCommit commit && !string.IsNullOrEmpty(commit.WebUrl))
                ExternalLink.Open(commit.WebUrl);
        }
    }
}
