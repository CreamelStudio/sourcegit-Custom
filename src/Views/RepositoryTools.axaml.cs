using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SourceGit.Views
{
    public partial class RepositoryTools : ChromelessWindow
    {
        public RepositoryTools()
        {
            InitializeComponent();
            CloseOnESC = true;
            Opened += async (_, _) => { if (DataContext is ViewModels.RepositoryTools vm) await vm.InitializeAsync(); };
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (_, _) =>
            {
                if (DataContext is ViewModels.RepositoryTools { TabIndex: 2 } vm)
                    await vm.RefreshJobsAsync();
            };
            _timer.Start();
            Closed += (_, _) => { _timer.Stop(); (DataContext as ViewModels.RepositoryTools)?.Dispose(); };
        }

        private async void RefreshRecovery(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.RepositoryTools vm) await vm.LoadRecoveryAsync(); }
        private async void CreateRecoveryBranch(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.RepositoryTools vm) await vm.CreateRecoveryBranchAsync(); }
        private async void Search(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.RepositoryTools vm) await vm.SearchAsync(); }
        private void CancelSearch(object sender, RoutedEventArgs e) => (DataContext as ViewModels.RepositoryTools)?.CancelSearch();
        private async void SearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && DataContext is ViewModels.RepositoryTools vm) { e.Handled = true; await vm.SearchAsync(); }
        }
        private void OpenHit(object sender, TappedEventArgs e) => (DataContext as ViewModels.RepositoryTools)?.OpenHit();
        private void OpenHitButton(object sender, RoutedEventArgs e) => (DataContext as ViewModels.RepositoryTools)?.OpenHit();
        private async void RefreshJobs(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.RepositoryTools vm) await vm.RefreshJobsAsync(); }
        private void CancelJob(object sender, RoutedEventArgs e) { if (sender is Button { Tag: Models.BackgroundTaskState state }) Models.BackgroundTasks.Cancel(state.Id); }
        private void OpenJob(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Models.BackgroundTaskState state } && !state.IsActive)
                App.GetLauncher()?.TryOpenRepositoryFromPath(state.Repository);
        }
        private async void RemoveScheduledRepository(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string repository })
            {
                await ViewModels.BackgroundActivity.ConfigureAsync(remove: repository);
                if (DataContext is ViewModels.RepositoryTools vm) await vm.RefreshJobsAsync();
            }
        }
        private readonly DispatcherTimer _timer;
    }
}
