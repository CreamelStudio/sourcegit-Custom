using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public sealed class SearchRepository : ObservableObject
    {
        public string Path { get; init; }
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
        private bool _isSelected = true;
    }

    public sealed class RepositoryTools : ObservableObject, IDisposable
    {
        public AvaloniaList<SearchRepository> Repositories { get; } = new();
        public AvaloniaList<Models.WorkspaceSearchHit> Hits { get; } = new();
        public AvaloniaList<Models.RecoveryPoint> RecoveryPoints { get; } = new();
        public AvaloniaList<Models.BackgroundTaskState> Jobs { get; } = new();
        public AvaloniaList<string> ScheduledRepositories { get; } = new();
        public List<string> SearchKinds { get; } = [App.Text("WorkspaceSearch.Commits"), App.Text("WorkspaceSearch.Files"), App.Text("WorkspaceSearch.Text")];
        public int TabIndex { get => _tabIndex; set => SetProperty(ref _tabIndex, value); }
        public int SearchKind { get; set; }
        public string Query { get; set; } = string.Empty;
        public string Status { get => _status; private set => SetProperty(ref _status, value); }
        public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
        public string RecoveryStatus { get => _recoveryStatus; private set => SetProperty(ref _recoveryStatus, value); }
        public Models.RecoveryPoint SelectedRecoveryPoint { get; set; }
        public Models.WorkspaceSearchHit SelectedHit { get; set; }
        public SearchRepository RecoveryRepository
        {
            get => _recoveryRepository;
            set { if (SetProperty(ref _recoveryRepository, value)) _ = LoadRecoveryAsync(); }
        }

        public RepositoryTools(Launcher launcher, int tab = 0)
        {
            TabIndex = tab;
            var preset = launcher?.ActivePreset;
            _roots = preset?.IsSaved == true ? preset.Tabs.Select(t => t.Path).ToList() : launcher?.ActiveWorkspace.Repositories.ToList() ?? new();
            var current = (launcher?.ActivePage?.Data as Repository)?.FullPath;
            if (current != null) _roots.Insert(0, current);
        }

        public async Task InitializeAsync()
        {
            try
            {
                var paths = await Models.WorkspaceSearch.FindRepositoriesAsync(_roots, _lifetime.Token);
                foreach (var path in paths) Repositories.Add(new SearchRepository { Path = path });
                RecoveryRepository = Repositories.FirstOrDefault();
                Status = App.Text("WorkspaceSearch.Scope", Repositories.Count);
                await RefreshJobsAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Status = ex.Message; }
        }

        public async Task RefreshScopeAsync(Launcher launcher)
        {
            if (IsBusy) return;
            _roots.Clear();
            _roots.AddRange(launcher.ActivePreset?.IsSaved == true ? launcher.ActivePreset.Tabs.Select(t => t.Path) : launcher.ActiveWorkspace.Repositories);
            if (launcher.ActivePage?.Data is Repository current) _roots.Insert(0, current.FullPath);
            Repositories.Clear();
            await InitializeAsync();
        }

        public async Task LoadRecoveryAsync()
        {
            _recovery?.Cancel();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _recovery = cancellation;
            try
            {
                RecoveryPoints.Clear();
                if (RecoveryRepository == null) return;
                var points = await Models.RecoveryTimeline.ReadAsync(RecoveryRepository.Path, cancellation.Token);
                RecoveryPoints.AddRange(points);
                RecoveryStatus = App.Text("Recovery.Count", points.Count);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RecoveryStatus = ex.Message; }
            finally { if (_recovery == cancellation) _recovery = null; }
        }

        public async Task CreateRecoveryBranchAsync()
        {
            if (RecoveryRepository == null || SelectedRecoveryPoint == null) return;
            try
            {
                var name = await Models.RecoveryTimeline.CreateBranchAsync(RecoveryRepository.Path, SelectedRecoveryPoint, _lifetime.Token);
                RecoveryStatus = App.Text("Recovery.Created", name);
                var launcher = App.GetLauncher();
                foreach (var page in launcher?.Pages ?? new AvaloniaList<LauncherPage>())
                    if (page.Data is Repository repo && repo.FullPath == RecoveryRepository.Path.Replace('\\', '/')) repo.RefreshAll();
            }
            catch (Exception ex) { RecoveryStatus = ex.Message; }
        }

        public async Task SearchAsync()
        {
            if (IsBusy) return;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _search = cancellation;
            IsBusy = true;
            Hits.Clear();
            Status = App.Text("WorkspaceSearch.Searching");
            var query = Query;
            var kind = (Models.WorkspaceSearchKind)Math.Clamp(SearchKind, 0, 2);
            var targets = Repositories.Where(r => r.IsSelected).Select(r => r.Path).ToList();
            var errors = new List<string>();
            var limited = false;
            try
            {
                using var slots = new SemaphoreSlim(2);
                var results = await Task.WhenAll(targets.Select(async path =>
                {
                    await slots.WaitAsync(cancellation.Token);
                    try { return (path, result: await Models.WorkspaceSearch.SearchAsync(path, kind, query, cancellation.Token), error: (string)null); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { return (path, result: (new List<Models.WorkspaceSearchHit>(), false), error: ex.Message); }
                    finally { slots.Release(); }
                }));
                foreach (var item in results)
                {
                    if (item.error != null) errors.Add(Path.GetFileName(item.path) + ": " + item.error);
                    limited |= item.result.Item2;
                    Hits.AddRange(item.result.Item1.Take(Math.Max(0, 1000 - Hits.Count)));
                    if (Hits.Count >= 1000) limited = true;
                }
                Status = App.Text(limited ? "WorkspaceSearch.Limited" : "WorkspaceSearch.Count", Hits.Count) +
                    (errors.Count == 0 ? string.Empty : "\n" + string.Join('\n', errors));
            }
            catch (OperationCanceledException) { Status = App.Text("Cancel"); }
            finally { _search = null; IsBusy = false; }
        }

        public void CancelSearch() => _search?.Cancel();

        public void OpenHit()
        {
            var hit = SelectedHit;
            if (hit == null) return;
            if (!string.IsNullOrEmpty(hit.SHA))
            {
                var launcher = App.GetLauncher();
                if (launcher?.TryOpenRepositoryFromPath(hit.Repository) == true && launcher.ActivePage.Data is Repository repo)
                    repo.NavigateToCommit(hit.SHA);
            }
            else
            {
                var path = Path.GetFullPath(Path.Combine(hit.Repository, hit.Location));
                if (path.StartsWith(Path.TrimEndingDirectorySeparator(hit.Repository) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(path))
                    Native.OS.OpenWithDefaultEditor(path);
            }
        }

        public async Task RefreshJobsAsync()
        {
            var states = await Task.Run(Models.BackgroundTasks.GetStates);
            Jobs.Clear();
            Jobs.AddRange(states);
            var config = Models.BackgroundTasks.Read(Path.Combine(Models.BackgroundTasks.Root, "fetch.json"), JsonCodeGen.Default.BackgroundFetchSettings);
            ScheduledRepositories.Clear();
            if (config != null) ScheduledRepositories.AddRange(config.Repositories);
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _search?.Cancel();
            _recovery?.Cancel();
            _lifetime.Dispose();
        }

        private readonly List<string> _roots;
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource _search;
        private CancellationTokenSource _recovery;
        private SearchRepository _recoveryRepository;
        private int _tabIndex;
        private string _status = string.Empty;
        private string _recoveryStatus = string.Empty;
        private bool _isBusy;
    }
}
