using System;
using System.Collections.Generic;
using System.Linq;

using Avalonia.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class PresetTab
    {
        public string Path { get; set; } = string.Empty;
        public int SelectedViewIndex { get; set; }
    }

    public class WorkspacePreset : ObservableObject
    {
        public int Slot { get; set; }

        public string Shortcut => $"Ctrl+F{Slot}";

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public List<PresetTab> Tabs
        {
            get => _tabs;
            set
            {
                if (SetProperty(ref _tabs, value))
                    OnPropertyChanged(nameof(IsSaved));
            }
        }

        public int ActiveIndex { get; set; }
        public PresetAppearance Appearance { get; set; }
        public bool IsSaved => Tabs is { Count: > 0 };

        public void Capture(Preferences preferences, IEnumerable<PresetTab> tabs, int activeIndex)
        {
            var snapshot = tabs.Select(t => new PresetTab { Path = t.Path, SelectedViewIndex = t.SelectedViewIndex }).ToList();
            ActiveIndex = Math.Clamp(activeIndex, 0, Math.Max(0, snapshot.Count - 1));
            Appearance = PresetAppearance.Capture(preferences);
            Tabs = snapshot;
        }

        public bool Matches(Preferences preferences, IEnumerable<PresetTab> tabs)
        {
            // Normal navigation between saved tabs and repository views is not a configuration change.
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            return IsSaved && Tabs.All(t => t?.Path != null) &&
                Tabs.Select(t => t.Path).SequenceEqual(tabs.Select(t => t.Path), comparer) &&
                (Appearance == null || Appearance.Matches(preferences));
        }

        public void Clear()
        {
            Tabs = null;
            Appearance = null;
            ActiveIndex = 0;
        }

        public static int GetSlot(Key key, KeyModifiers modifiers)
        {
            var slot = (int)key - (int)Key.F1 + 1;
            return modifiers == KeyModifiers.Control && slot is >= 1 and <= 10 ? slot : 0;
        }

        private string _name = string.Empty;
        private List<PresetTab> _tabs;
    }

    public class PresetAppearance
    {
        public string Theme { get; set; } = "Default";
        public string ThemeOverrides { get; set; } = string.Empty;
        public string DefaultFontFamily { get; set; } = string.Empty;
        public string MonospaceFontFamily { get; set; } = string.Empty;
        public double DefaultFontSize { get; set; } = 13;
        public double EditorFontSize { get; set; } = 13;
        public double Zoom { get; set; } = 1;
        public bool UseTwoColumnsLayoutInHistories { get; set; }
        public bool UseSideBySideDiff { get; set; }
        public bool EnableDiffViewWordWrap { get; set; }
        public LayoutInfo Layout { get; set; }

        public static PresetAppearance Capture(Preferences pref)
        {
            return new PresetAppearance
            {
                Theme = pref.Theme,
                ThemeOverrides = pref.ThemeOverrides,
                DefaultFontFamily = pref.DefaultFontFamily,
                MonospaceFontFamily = pref.MonospaceFontFamily,
                DefaultFontSize = pref.DefaultFontSize,
                EditorFontSize = pref.EditorFontSize,
                Zoom = pref.Zoom,
                UseTwoColumnsLayoutInHistories = pref.UseTwoColumnsLayoutInHistories,
                UseSideBySideDiff = pref.UseSideBySideDiff,
                EnableDiffViewWordWrap = pref.EnableDiffViewWordWrap,
                Layout = CopyPanels(pref.Layout, new LayoutInfo()),
            };
        }

        public void Apply(Preferences pref)
        {
            pref.ThemeOverrides = ThemeOverrides;
            pref.Theme = Theme;
            pref.DefaultFontFamily = DefaultFontFamily;
            pref.MonospaceFontFamily = MonospaceFontFamily;
            pref.DefaultFontSize = DefaultFontSize;
            pref.EditorFontSize = EditorFontSize;
            pref.Zoom = Zoom;
            pref.UseTwoColumnsLayoutInHistories = UseTwoColumnsLayoutInHistories;
            pref.UseSideBySideDiff = UseSideBySideDiff;
            pref.EnableDiffViewWordWrap = EnableDiffViewWordWrap;
            if (Layout != null)
                pref.Layout = CopyPanels(Layout, pref.Layout);
        }

        public bool Matches(Preferences pref)
        {
            return Theme == pref.Theme && ThemeOverrides == pref.ThemeOverrides &&
                DefaultFontFamily == pref.DefaultFontFamily && MonospaceFontFamily == pref.MonospaceFontFamily &&
                DefaultFontSize == pref.DefaultFontSize && EditorFontSize == pref.EditorFontSize && Zoom == pref.Zoom &&
                UseTwoColumnsLayoutInHistories == pref.UseTwoColumnsLayoutInHistories &&
                UseSideBySideDiff == pref.UseSideBySideDiff && EnableDiffViewWordWrap == pref.EnableDiffViewWordWrap &&
                (Layout == null ||
                    Math.Abs(Layout.RepositorySidebarWidth - pref.Layout.RepositorySidebarWidth) < 0.5 &&
                    Math.Abs(Layout.RepositorySearchCommitWidth - pref.Layout.RepositorySearchCommitWidth) < 0.5 &&
                    Layout.IsSidebarCollapsedInLocalChanges == pref.Layout.IsSidebarCollapsedInLocalChanges &&
                    Layout.WorkingCopyLeftWidth == pref.Layout.WorkingCopyLeftWidth &&
                    Layout.StashesLeftWidth == pref.Layout.StashesLeftWidth &&
                    Layout.CommitDetailChangesLeftWidth == pref.Layout.CommitDetailChangesLeftWidth &&
                    Layout.CommitDetailFilesLeftWidth == pref.Layout.CommitDetailFilesLeftWidth);
        }

        private static LayoutInfo CopyPanels(LayoutInfo source, LayoutInfo window)
        {
            // Keep the current window on its monitor; presets restore panel sizes only.
            return new LayoutInfo
            {
                LauncherWidth = window.LauncherWidth,
                LauncherHeight = window.LauncherHeight,
                LauncherPositionX = window.LauncherPositionX,
                LauncherPositionY = window.LauncherPositionY,
                LauncherWindowState = window.LauncherWindowState,
                RepositorySidebarWidth = source.RepositorySidebarWidth,
                RepositorySearchCommitWidth = source.RepositorySearchCommitWidth,
                IsSidebarCollapsedInLocalChanges = source.IsSidebarCollapsedInLocalChanges,
                WorkingCopyLeftWidth = source.WorkingCopyLeftWidth,
                StashesLeftWidth = source.StashesLeftWidth,
                CommitDetailChangesLeftWidth = source.CommitDetailChangesLeftWidth,
                CommitDetailFilesLeftWidth = source.CommitDetailFilesLeftWidth,
            };
        }
    }
}
