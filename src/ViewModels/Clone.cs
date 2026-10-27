using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    public record TargetGroup(string Id, string Name, string Description);

    public class Clone : Popup
    {
        [Required(ErrorMessage = "Remote URL is required")]
        [CustomValidation(typeof(Clone), nameof(ValidateRemote))]
        public string Remote
        {
            get => _remote;
            set
            {
                if (SetProperty(ref _remote, value, true))
                    UseSSH = Models.Remote.IsSSH(value);
            }
        }

        public bool UseSSH
        {
            get => _useSSH;
            set => SetProperty(ref _useSSH, value);
        }

        public string SSHKey
        {
            get => _sshKey;
            set => SetProperty(ref _sshKey, value);
        }

        [Required(ErrorMessage = "Parent folder is required")]
        [CustomValidation(typeof(Clone), nameof(ValidateParentFolder))]
        public string ParentFolder
        {
            get => _parentFolder;
            set => SetProperty(ref _parentFolder, value, true);
        }

        public string Local
        {
            get => _local;
            set => SetProperty(ref _local, value);
        }

        public List<TargetGroup> Groups
        {
            get;
        }

        public int SelectedGroupIndex
        {
            get => _selectedGroupIndex;
            set => SetProperty(ref _selectedGroupIndex, value);
        }

        public bool CanAutoSelectGroup
        {
            get => _canAutoSelectGroup;
            private set => SetProperty(ref _canAutoSelectGroup, value);
        }

        public int Bookmark
        {
            get => _bookmark;
            set => SetProperty(ref _bookmark, value);
        }

        public string ExtraArgs
        {
            get => _extraArgs;
            set => SetProperty(ref _extraArgs, value);
        }

        public bool InitAndUpdateSubmodules
        {
            get;
            set;
        } = true;

        public Clone(string pageId)
        {
            _pageId = pageId;
            CanTerminate = false;

            Groups = new List<TargetGroup>();
            Groups.Add(new TargetGroup(string.Empty, "Auto", "Based-on Default Clone Dir"));
            Groups.Add(new TargetGroup(string.Empty, "No Group", "Uncategorized"));
            CollectGroups(Groups, Preferences.Instance.RepositoryNodes);

            var activeWorkspace = Preferences.Instance.GetActiveWorkspace();
            _defaultCloneDir = activeWorkspace?.DefaultCloneDir;
            if (string.IsNullOrEmpty(_defaultCloneDir))
                _defaultCloneDir = Preferences.Instance.GitDefaultCloneDir;

            ParentFolder = _defaultCloneDir;
        }

        public static ValidationResult ValidateRemote(string remote, ValidationContext _)
        {
            if (!Models.Remote.IsValidURL(remote))
                return new ValidationResult("Invalid remote repository URL format");
            return ValidationResult.Success;
        }

        public static ValidationResult ValidateParentFolder(string folder, ValidationContext _)
        {
            if (!Directory.Exists(folder))
                return new ValidationResult("Given path can NOT be found");
            return ValidationResult.Success;
        }

        public override async Task<bool> Sure()
        {
            ProgressDescription = App.Text("Background.Queuing");
            RepositoryNode parent = null;
            if (_selectedGroupIndex == 0 && !string.IsNullOrEmpty(_defaultCloneDir))
            {
                var baseDir = _defaultCloneDir.Replace('\\', '/').TrimEnd('/') + "/";
                var targetDir = _parentFolder.Replace('\\', '/').TrimEnd('/') + "/";
                if (targetDir.Length > baseDir.Length && targetDir.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                    parent = Preferences.Instance.FindOrCreateGroupRecursive(targetDir[baseDir.Length..].TrimEnd('/'));
            }
            else if (_selectedGroupIndex > 0 && _selectedGroupIndex < Groups.Count)
                parent = Preferences.Instance.FindNode(Groups[_selectedGroupIndex].Id);

            var local = _local;
            if (string.IsNullOrWhiteSpace(local))
            {
                local = Path.GetFileName(_remote.TrimEnd('/'));
                if (local.EndsWith(".git", StringComparison.Ordinal)) local = local[..^4];
                else if (local.EndsWith(".bundle", StringComparison.Ordinal)) local = local[..^7];
            }
            try
            {
                await Models.BackgroundTasks.EnqueueCloneAsync(new Models.BackgroundCloneRequest
                {
                    GitExecutable = Native.OS.GitExecutable,
                    CredentialHelper = Native.OS.CredentialHelper,
                    ParentFolder = _parentFolder, LocalName = local, Remote = _remote,
                    SSHKey = _useSSH ? _sshKey : string.Empty, ExtraArgs = _extraArgs,
                    Submodules = InitAndUpdateSubmodules, GroupId = parent?.Id ?? string.Empty, Bookmark = _bookmark,
                });
                App.OpenRepositoryTools(2);
                return true;
            }
            catch (Exception ex)
            {
                Models.Notification.Send(_pageId, ex.Message, true);
                return false;
            }
        }

        private void CollectGroups(List<TargetGroup> outs, List<RepositoryNode> collections, string prefix = null)
        {
            foreach (var node in collections)
            {
                if (!node.IsRepository)
                {
                    var displayName = prefix != null ? $"{prefix}/{node.Name}" : node.Name;
                    outs.Add(new(node.Id, displayName, string.Empty));
                    CollectGroups(outs, node.SubNodes, displayName);
                }
            }
        }

        private string _pageId = string.Empty;
        private string _remote = string.Empty;
        private bool _useSSH = false;
        private string _sshKey = string.Empty;
        private string _parentFolder = string.Empty;
        private string _defaultCloneDir = string.Empty;
        private string _local = string.Empty;
        private string _extraArgs = string.Empty;
        private int _selectedGroupIndex = 0;
        private bool _canAutoSelectGroup = false;
        private int _bookmark = 0;

    }
}
