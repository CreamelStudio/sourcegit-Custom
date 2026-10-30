using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace SourceGit.Views
{
    public partial class RepositoryToolbar : UserControl
    {
        public RepositoryToolbar()
        {
            InitializeComponent();
        }

        private void OpenProject(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || DataContext is not ViewModels.Repository repo)
                return;

            e.Handled = true;
            var opener = repo.ProjectOpener;
            if (opener == null && repo.HasUnityProjects)
                opener = new Models.ProjectOpener { Name = "Unity", IsUnity = true };
            if (opener == null || opener.IsUnity && !repo.HasUnityProjects)
            {
                ShowProjectOpeners(button, repo);
                return;
            }

            try
            {
                using var process = Process.Start(opener.CreateStartInfo(repo.FullPath));
            }
            catch (Exception ex)
            {
                repo.SendNotification(App.Text("Repository.ProjectOpener.Failed", ex.Message), true);
                ShowProjectOpeners(button, repo);
            }
        }

        private void SelectProjectOpener(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && DataContext is ViewModels.Repository repo)
                ShowProjectOpeners(button, repo);
            e.Handled = true;
        }

        private void ShowProjectOpeners(Button button, ViewModels.Repository repo)
        {
            var menu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft };
            if (repo.HasUnityProjects)
            {
                var project = repo.UnityProjects[0];
                var unity = new MenuItem { Header = "Unity", Icon = CreateProgramMenuIcon(() => project.FindEditor(Models.UnityProject.EditorRoots())) };
                unity.Click += (_, _) => repo.SetProjectOpener(new Models.ProjectOpener { Name = "Unity", IsUnity = true });
                menu.Items.Add(unity);
            }

            foreach (var tool in Native.OS.ExternalTools)
            {
                if (!File.Exists(tool.ExecFile))
                    continue;
                try
                {
                    var item = new MenuItem { Header = tool.Name, Icon = CreateProgramMenuIcon(() => tool.ExecFile) };
                    var options = tool.MakeLaunchOptions(repo.FullPath);
                    if (options is { Count: > 0 })
                    {
                        foreach (var option in options)
                        {
                            if (option.Target == null)
                                continue;
                            var target = option.Target;
                            var child = new MenuItem { Header = option.Title };
                            child.Click += (_, _) => SaveProjectOpener(repo, tool, target);
                            item.Items.Add(child);
                        }
                        if (tool.SupportOpenFolder)
                        {
                            var folder = new MenuItem { Header = App.Text("Repository.ProjectOpener.CurrentFolder") };
                            folder.Click += (_, _) => SaveProjectOpener(repo, tool, repo.FullPath);
                            item.Items.Add(folder);
                        }
                        if (item.Items.Count == 0)
                            continue;
                    }
                    else if (tool.SupportOpenFolder)
                    {
                        item.Click += (_, _) => SaveProjectOpener(repo, tool, repo.FullPath);
                    }
                    else
                    {
                        // A solution-only IDE can still be connected to a file selected by the user.
                        item.Click += async (_, _) => await PickProjectTargetAsync(repo, tool);
                    }
                    menu.Items.Add(item);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ToolTip.SetTip(button, App.Text("Repository.ProjectOpener.Failed", ex.Message));
                }
            }

            menu.Items.Add(new MenuItem { Header = "-" });
            var custom = new MenuItem { Header = App.Text("Repository.ProjectOpener.ChooseProgram") };
            custom.Click += async (_, _) =>
            {
                try
                {
                    var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = App.Text("Repository.ProjectOpener.ChooseProgram"), AllowMultiple = false,
                        FileTypeFilter = [new FilePickerFileType("Program") { Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"] }],
                    });
                    if (files.Count == 1)
                    {
                        var executable = files[0].Path.LocalPath;
                        if (!File.Exists(executable))
                            throw new FileNotFoundException("Program not found.", executable);
                        var name = FileVersionInfo.GetVersionInfo(executable).ProductName;
                        var tool = new Models.ExternalTool(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(executable) : name, executable, null, true);
                        SaveProjectOpener(repo, tool, repo.FullPath);
                    }
                }
                catch (Exception ex) { repo.SendNotification(App.Text("Repository.ProjectOpener.Failed", ex.Message), true); }
            };
            menu.Items.Add(custom);

            var canChooseTarget = repo.ProjectOpener is { IsUnity: false };
            var chooseFolder = new MenuItem { Header = App.Text("Repository.ProjectOpener.ChooseFolder"), IsEnabled = canChooseTarget };
            chooseFolder.Click += async (_, _) => await PickProjectTargetAsync(repo, null, true);
            menu.Items.Add(chooseFolder);
            var chooseFile = new MenuItem { Header = App.Text("Repository.ProjectOpener.ChooseFile"), IsEnabled = canChooseTarget };
            chooseFile.Click += async (_, _) => await PickProjectTargetAsync(repo);
            menu.Items.Add(chooseFile);
            var clear = new MenuItem { Header = App.Text("Repository.ProjectOpener.Reset"), IsEnabled = repo.ProjectOpener != null };
            clear.Click += (_, _) => repo.SetProjectOpener(null);
            menu.Items.Add(clear);
            menu.Items.Add(new MenuItem { Header = "-" });
            var other = new MenuItem { Header = App.Text("Repository.ProjectOpener.Other") };
            other.Click += (_, e) =>
            {
                menu.Close();
                OpenWithExternalTools(button, e);
            };
            menu.Items.Add(other);
            menu.Open(button);
        }

        private static void SaveProjectOpener(ViewModels.Repository repo, Models.ExternalTool tool, string target)
        {
            try { repo.SetProjectOpener(Models.ProjectOpener.FromTool(repo.FullPath, tool, target)); }
            catch (Exception ex) { repo.SendNotification(App.Text("Repository.ProjectOpener.Failed", ex.Message), true); }
        }

        private static Image CreateProgramMenuIcon(Func<string> executable)
        {
            var image = new Image { Width = 16, Height = 16 };
            System.Threading.Tasks.Task.Run(() =>
            {
                var icon = Native.OS.GetProgramIcon(executable());
                Avalonia.Threading.Dispatcher.UIThread.Post(() => image.Source = icon);
            });
            return image;
        }

        private async System.Threading.Tasks.Task PickProjectTargetAsync(ViewModels.Repository repo, Models.ExternalTool tool = null, bool folder = false)
        {
            try
            {
                var storage = TopLevel.GetTopLevel(this)!.StorageProvider;
                var title = App.Text(folder ? "Repository.ProjectOpener.ChooseFolder" : "Repository.ProjectOpener.ChooseFile");
                var location = await storage.TryGetFolderFromPathAsync(repo.FullPath);
                string target;
                if (folder)
                {
                    var selected = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = location });
                    if (selected.Count != 1)
                        return;
                    target = selected[0].Path.LocalPath;
                }
                else
                {
                    var selected = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = location });
                    if (selected.Count != 1)
                        return;
                    target = selected[0].Path.LocalPath;
                }
                if (tool != null)
                    SaveProjectOpener(repo, tool, target);
                else if (repo.ProjectOpener is { IsUnity: false } current)
                    repo.SetProjectOpener(new Models.ProjectOpener { Name = current.Name, Executable = current.Executable, Target = Models.ProjectOpener.RelativeTarget(repo.FullPath, target) });
            }
            catch (Exception ex) { repo.SendNotification(App.Text("Repository.ProjectOpener.Failed", ex.Message), true); }
        }

        private void OpenWithExternalTools(object sender, RoutedEventArgs ev)
        {
            if (sender is Button button && DataContext is ViewModels.Repository repo)
            {
                var fullpath = repo.FullPath;
                if (!Directory.Exists(fullpath))
                    return;

                var isMacOS = OperatingSystem.IsMacOS();
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                RenderOptions.SetBitmapInterpolationMode(menu, BitmapInterpolationMode.HighQuality);
                RenderOptions.SetEdgeMode(menu, EdgeMode.Antialias);
                RenderOptions.SetTextRenderingMode(menu, TextRenderingMode.SubpixelAntialias);

                var explore = new MenuItem();
                explore.Header = App.Text("Repository.Explore");
                explore.Icon = this.CreateMenuIcon("Icons.Explore");
                explore.Tag = isMacOS ? "⌘+E" : "Ctrl+E";
                explore.Click += (_, e) =>
                {
                    Native.OS.OpenInFileManager(fullpath);
                    e.Handled = true;
                };

                var terminal = new MenuItem();
                terminal.Header = App.Text("Repository.Terminal");
                terminal.Icon = this.CreateMenuIcon("Icons.Terminal");
                terminal.Tag = isMacOS ? "Λ+`" : "Ctrl+`";
                terminal.Click += (_, e) =>
                {
                    Native.OS.OpenTerminal(fullpath);
                    e.Handled = true;
                };

                menu.Items.Add(explore);
                menu.Items.Add(terminal);

                var tools = Native.OS.ExternalTools;
                if (tools.Count > 0)
                {
                    menu.Items.Add(new MenuItem() { Header = "-" });

                    foreach (var tool in tools)
                    {
                        var dupTool = tool;

                        var item = new MenuItem();
                        item.Header = App.Text("Repository.OpenIn", dupTool.Name);
                        item.Icon = CreateProgramMenuIcon(() => dupTool.ExecFile);

                        var options = dupTool.MakeLaunchOptions(fullpath);
                        var count = (dupTool.SupportOpenFolder ? 1 : 0) + (options?.Count ?? 0);
                        if (count == 0)
                            continue;

                        if (count == 1)
                        {
                            var args = fullpath.Quoted();
                            if (options is { Count: 1 })
                                args = options[0].Args;

                            item.Click += (_, e) =>
                            {
                                dupTool.Launch(args);
                                e.Handled = true;
                            };
                        }
                        else
                        {
                            foreach (var opt in options)
                            {
                                var subItem = new MenuItem();
                                subItem.Header = opt.Title;
                                subItem.Click += (_, e) =>
                                {
                                    dupTool.Launch(opt.Args);
                                    e.Handled = true;
                                };

                                item.Items.Add(subItem);
                            }

                            if (dupTool.SupportOpenFolder)
                            {
                                var open = new MenuItem();
                                open.Header = App.Text("Repository.OpenAsFolder");
                                open.Click += (_, e) =>
                                {
                                    dupTool.Launch(fullpath.Quoted());
                                    e.Handled = true;
                                };
                                item.Items.Add(new MenuItem() { Header = "-" });
                                item.Items.Add(open);
                            }
                        }

                        menu.Items.Add(item);
                    }
                }

                var urls = new Dictionary<string, string>();
                foreach (var r in repo.Remotes)
                {
                    if (r.TryGetVisitURL(out var visit))
                        urls.Add(r.Name, visit);
                }

                if (urls.Count > 0)
                {
                    menu.Items.Add(new MenuItem() { Header = "-" });

                    foreach (var (name, addr) in urls)
                    {
                        var dupUrl = addr;

                        var item = new MenuItem();
                        item.Header = App.Text("Repository.Visit", name);
                        item.Icon = this.CreateMenuIcon("Icons.Remotes");
                        item.Click += (_, e) =>
                        {
                            Native.OS.OpenBrowser(dupUrl);
                            e.Handled = true;
                        };

                        menu.Items.Add(item);
                    }
                }

                menu.Open(button);
                ev.Handled = true;
            }
        }

        private async void OpenStatistics(object _, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await this.ShowDialogAsync(new ViewModels.Statistics(repo.FullPath));
                e.Handled = true;
            }
        }

        private async void Fetch(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.FetchAsync(e.KeyModifiers is KeyModifiers.Control);
                e.Handled = true;
            }
        }

        private async void FetchDirectlyByHotKey(object sender, RoutedEventArgs e)
        {
            if (App.GetLauncher() is { CommandPalette: { } } launcher)
                return;

            if (DataContext is ViewModels.Repository repo)
            {
                await repo.FetchAsync(true);
                e.Handled = true;
            }
        }

        private async void Pull(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.PullAsync(e.KeyModifiers is KeyModifiers.Control);
                e.Handled = true;
            }
        }

        private async void PullDirectlyByHotKey(object sender, RoutedEventArgs e)
        {
            if (App.GetLauncher() is { CommandPalette: { } } launcher)
                return;

            if (DataContext is ViewModels.Repository repo)
            {
                await repo.PullAsync(true);
                e.Handled = true;
            }
        }

        private async void Push(object sender, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.PushAsync(e.KeyModifiers is KeyModifiers.Control);
                e.Handled = true;
            }
        }

        private async void PushDirectlyByHotKey(object sender, RoutedEventArgs e)
        {
            if (App.GetLauncher() is { CommandPalette: { } } launcher)
                return;

            if (DataContext is ViewModels.Repository repo)
            {
                await repo.PushAsync(true);
                e.Handled = true;
            }
        }

        private async void StashAll(object _, TappedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.StashAllAsync(e.KeyModifiers is KeyModifiers.Control);
                e.Handled = true;
            }
        }

        private void OpenGitFlowMenu(object sender, RoutedEventArgs ev)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control control)
            {
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                if (repo.IsGitFlowEnabled())
                {
                    var startFeature = new MenuItem();
                    startFeature.Header = App.Text("GitFlow.StartFeature");
                    startFeature.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.GitFlowStart(repo, Models.GitFlowBranchType.Feature));
                        e.Handled = true;
                    };

                    var startRelease = new MenuItem();
                    startRelease.Header = App.Text("GitFlow.StartRelease");
                    startRelease.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.GitFlowStart(repo, Models.GitFlowBranchType.Release));
                        e.Handled = true;
                    };

                    var startHotfix = new MenuItem();
                    startHotfix.Header = App.Text("GitFlow.StartHotfix");
                    startHotfix.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.GitFlowStart(repo, Models.GitFlowBranchType.Hotfix));
                        e.Handled = true;
                    };

                    menu.Items.Add(startFeature);
                    menu.Items.Add(startRelease);
                    menu.Items.Add(startHotfix);

                    var type = repo.CurrentBranch != null ? repo.GetGitFlowType(repo.CurrentBranch) : Models.GitFlowBranchType.None;
                    if (type != Models.GitFlowBranchType.None)
                    {
                        var finish = new MenuItem();
                        finish.Header = App.Text("GitFlow.Finish", repo.CurrentBranch.Name);
                        finish.Icon = this.CreateMenuIcon("Icons.GitFlow.Finish");
                        finish.Click += (_, e) =>
                        {
                            if (repo.CanCreatePopup())
                                repo.ShowPopup(new ViewModels.GitFlowFinish(repo, repo.CurrentBranch, type));
                            e.Handled = true;
                        };
                        menu.Items.Add(new MenuItem() { Header = "-" });
                        menu.Items.Add(finish);
                    }
                }
                else
                {
                    var init = new MenuItem();
                    init.Header = App.Text("GitFlow.Init");
                    init.Icon = this.CreateMenuIcon("Icons.Init");
                    init.Click += (_, e) =>
                    {
                        if (repo.CurrentBranch == null)
                            repo.SendNotification("Git flow init failed: No branch found!!!", true);
                        else if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.InitGitFlow(repo));

                        e.Handled = true;
                    };
                    menu.Items.Add(init);
                }

                menu.Open(control);
            }

            ev.Handled = true;
        }

        private void OpenGitLFSMenu(object sender, RoutedEventArgs ev)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control control)
            {
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                if (repo.IsLFSEnabled())
                {
                    var addPattern = new MenuItem();
                    addPattern.Header = App.Text("GitLFS.AddTrackPattern");
                    addPattern.Icon = this.CreateMenuIcon("Icons.File.Add");
                    addPattern.Click += (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            repo.ShowPopup(new ViewModels.LFSTrackCustomPattern(repo));

                        e.Handled = true;
                    };
                    menu.Items.Add(addPattern);
                    menu.Items.Add(new MenuItem() { Header = "-" });

                    var fetch = new MenuItem();
                    fetch.Header = App.Text("GitLFS.Fetch");
                    fetch.Icon = this.CreateMenuIcon("Icons.Fetch");
                    fetch.IsEnabled = repo.Remotes.Count > 0;
                    fetch.Click += async (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                        {
                            if (repo.Remotes.Count == 1)
                                await repo.ShowAndStartPopupAsync(new ViewModels.LFSFetch(repo));
                            else
                                repo.ShowPopup(new ViewModels.LFSFetch(repo));
                        }

                        e.Handled = true;
                    };
                    menu.Items.Add(fetch);

                    var pull = new MenuItem();
                    pull.Header = App.Text("GitLFS.Pull");
                    pull.Icon = this.CreateMenuIcon("Icons.Pull");
                    pull.IsEnabled = repo.Remotes.Count > 0;
                    pull.Click += async (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                        {
                            if (repo.Remotes.Count == 1)
                                await repo.ShowAndStartPopupAsync(new ViewModels.LFSPull(repo));
                            else
                                repo.ShowPopup(new ViewModels.LFSPull(repo));
                        }

                        e.Handled = true;
                    };
                    menu.Items.Add(pull);

                    var push = new MenuItem();
                    push.Header = App.Text("GitLFS.Push");
                    push.Icon = this.CreateMenuIcon("Icons.Push");
                    push.IsEnabled = repo.Remotes.Count > 0;
                    push.Click += async (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                        {
                            if (repo.Remotes.Count == 1)
                                await repo.ShowAndStartPopupAsync(new ViewModels.LFSPush(repo));
                            else
                                repo.ShowPopup(new ViewModels.LFSPush(repo));
                        }

                        e.Handled = true;
                    };
                    menu.Items.Add(push);

                    var prune = new MenuItem();
                    prune.Header = App.Text("GitLFS.Prune");
                    prune.Icon = this.CreateMenuIcon("Icons.Clean");
                    prune.Click += async (_, e) =>
                    {
                        if (repo.CanCreatePopup())
                            await repo.ShowAndStartPopupAsync(new ViewModels.LFSPrune(repo));

                        e.Handled = true;
                    };
                    menu.Items.Add(new MenuItem() { Header = "-" });
                    menu.Items.Add(prune);

                    var locks = new MenuItem();
                    locks.Header = App.Text("GitLFS.Locks");
                    locks.Icon = this.CreateMenuIcon("Icons.Lock");
                    locks.IsEnabled = repo.Remotes.Count > 0;
                    if (repo.Remotes.Count == 1)
                    {
                        locks.Click += async (_, e) =>
                        {
                            await this.ShowDialogAsync(new ViewModels.LFSLocks(repo, repo.Remotes[0].Name));
                            e.Handled = true;
                        };
                    }
                    else
                    {
                        foreach (var remote in repo.Remotes)
                        {
                            var remoteName = remote.Name;
                            var lockRemote = new MenuItem();
                            lockRemote.Header = remoteName;
                            lockRemote.Click += async (_, e) =>
                            {
                                await this.ShowDialogAsync(new ViewModels.LFSLocks(repo, remoteName));
                                e.Handled = true;
                            };
                            locks.Items.Add(lockRemote);
                        }
                    }

                    menu.Items.Add(new MenuItem() { Header = "-" });
                    menu.Items.Add(locks);
                }
                else
                {
                    var install = new MenuItem();
                    install.Header = App.Text("GitLFS.Install");
                    install.Icon = this.CreateMenuIcon("Icons.Init");
                    install.Click += async (_, e) =>
                    {
                        await repo.InstallLFSAsync();
                        e.Handled = true;
                    };
                    menu.Items.Add(install);
                }

                menu.Open(control);
            }

            ev.Handled = true;
        }

        private async void StartBisect(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository { IsBisectCommandRunning: false, InProgressContext: null } repo &&
                repo.CanCreatePopup())
            {
                if (repo.LocalChangesCount > 0)
                    repo.SendNotification("You have un-committed local changes. Please discard or stash them first.", true);
                else if (repo.IsBisectCommandRunning || repo.BisectState != Models.BisectState.None)
                    repo.SendNotification("Bisect is running! Please abort it before starting a new one.", true);
                else
                    await repo.ExecBisectCommandAsync("start");
            }

            e.Handled = true;
        }

        private void OpenCustomActionMenu(object sender, RoutedEventArgs ev)
        {
            if (DataContext is ViewModels.Repository repo && sender is Control control)
            {
                var menu = new ContextMenu();
                menu.Placement = PlacementMode.BottomEdgeAlignedLeft;

                var actions = repo.GetCustomActions(Models.CustomActionScope.Repository);
                if (actions.Count > 0)
                {
                    foreach (var action in actions)
                    {
                        var (dup, label) = action;
                        var item = new MenuItem();
                        item.Icon = this.CreateMenuIcon("Icons.Action");
                        item.Header = label;
                        item.Click += async (_, e) =>
                        {
                            await repo.ExecCustomActionAsync(dup, null);
                            e.Handled = true;
                        };

                        menu.Items.Add(item);
                    }
                }
                else
                {
                    menu.Items.Add(new MenuItem() { Header = App.Text("Repository.CustomActions.Empty") });
                }

                menu.Open(control);
            }

            ev.Handled = true;
        }

        private async void OpenGitLogs(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await this.ShowDialogAsync(new ViewModels.ViewLogs(repo));
                e.Handled = true;
            }
        }

        private async void Cleanup(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository repo)
            {
                await repo.CleanupAsync();
                e.Handled = true;
            }
        }

        private void NavigateToHead(object sender, RoutedEventArgs e)
        {
            if (DataContext is ViewModels.Repository { CurrentBranch: { } head } repo)
            {
                var repoView = TopLevel.GetTopLevel(this)?.FindDescendantOfType<Repository>();
                repoView?.LocalBranchTree?.Select(head);

                repo.NavigateToCommit(head.Head);
                e.Handled = true;
            }
        }
    }
}
