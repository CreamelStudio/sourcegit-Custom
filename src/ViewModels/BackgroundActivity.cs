using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.ViewModels
{
    internal static class BackgroundActivity
    {
        public static void Start()
        {
            if (_timer != null) return;
            Preferences.Instance.PropertyChanged += OnPreferencesChanged;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += async (_, _) => await RefreshAsync();
            _timer.Start();
            _ = ConfigureAsync(App.GetLauncher()?.Pages.Select(p => (p.Data as Repository)?.FullPath).Where(p => p != null));
            _ = RefreshAsync();
        }

        public static async Task ConfigureAsync(IEnumerable<string> add = null, string remove = null)
        {
            var pref = Preferences.Instance;
            var settings = new Models.BackgroundFetchSettings
            {
                Enabled = pref.EnableAutoFetch && pref.EnableBackgroundFetch,
                Interval = pref.AutoFetchInterval,
                GitExecutable = Native.OS.GitExecutable,
                CredentialHelper = Native.OS.CredentialHelper,
            };
            try { await Models.BackgroundTasks.ConfigureFetchAsync(settings, add?.ToArray(), remove); }
            catch (Exception ex) { Models.Notification.Send(string.Empty, ex.Message, true); }
        }

        public static async Task RefreshAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                var busy = App.GetLauncher()?.Pages.Select(p => p.Data).OfType<Repository>()
                    .Select(r => (r.CommonDirectory, Busy: !r.CanCreatePopup())).ToArray();
                if (busy != null) await Task.Run(() => { foreach (var repository in busy) Models.BackgroundTasks.SetRepositoryBusy(repository.CommonDirectory, repository.Busy); });
                var states = await Task.Run(Models.BackgroundTasks.GetStates);
                var launcher = App.GetLauncher();
                if (launcher != null)
                    launcher.BackgroundTaskCount = states.Count(s => s.IsActive);
                foreach (var state in states.Where(s => !s.IsActive && s.Kind == "Clone"))
                {
                    var seen = Path.Combine(Models.BackgroundTasks.Root, "seen", state.Id + "." + state.UpdatedAt.UtcTicks);
                    if (File.Exists(seen)) continue;
                    if (state.Kind == "Clone" && state.Status == Models.BackgroundTaskStatus.Succeeded && Directory.Exists(state.Repository))
                    {
                        var parent = Preferences.Instance.FindNode(state.GroupId);
                        var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(state.Repository, parent, true, false);
                        node.Bookmark = state.Bookmark;
                        Preferences.Instance.Save();
                        Welcome.Instance.Refresh();
                        Models.Notification.Send(string.Empty, App.Text("Background.CloneDone", state.Repository), false);
                    }
                    else if (state.Kind == "Clone")
                        Models.Notification.Send(string.Empty, state.StatusText + ": " + state.Repository + "\n" + state.Detail, true);
                    Directory.CreateDirectory(Path.GetDirectoryName(seen)!);
                    File.WriteAllText(seen, string.Empty);
                }
            }
            catch (Exception ex) { Native.OS.LogException(ex); }
            finally { _refreshing = false; }
        }

        private static void OnPreferencesChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(Preferences.EnableAutoFetch) or nameof(Preferences.EnableBackgroundFetch) or nameof(Preferences.AutoFetchInterval) or nameof(Preferences.GitInstallPath))
                _ = ConfigureAsync(App.GetLauncher()?.Pages.Select(p => (p.Data as Repository)?.FullPath).Where(p => p != null));
        }

        private static DispatcherTimer _timer;
        private static bool _refreshing;
    }
}
