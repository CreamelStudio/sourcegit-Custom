using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Input;

using SourceGit;
using SourceGit.ViewModels;

internal static class PresetChecks
{
    public static void Run()
    {
        var pref = JsonSerializer.Deserialize("{}", JsonCodeGen.Default.Preferences)!;
        pref.PreparePresets();
        Check(pref.Presets.Select(p => p.Slot).SequenceEqual(Enumerable.Range(1, 10)), "Existing preferences gain ten empty preset slots.");
        Check(pref.Presets.All(p => !p.IsSaved), "New slots do not replace tabs until explicitly saved.");
        Key[] keys = [Key.F1, Key.F2, Key.F3, Key.F4, Key.F5, Key.F6, Key.F7, Key.F8, Key.F9, Key.F10];
        for (var i = 0; i < keys.Length; i++)
            Check(WorkspacePreset.GetSlot(keys[i], KeyModifiers.Control) == i + 1, "Each Ctrl+function key maps to its own slot.");
        foreach (var modifiers in new[] { KeyModifiers.None, KeyModifiers.Shift, KeyModifiers.Control | KeyModifiers.Shift, KeyModifiers.Control | KeyModifiers.Alt, KeyModifiers.Meta })
            Check(WorkspacePreset.GetSlot(Key.F1, modifiers) == 0, "Presets must not consume help, AltGr or other shortcuts.");
        Check(WorkspacePreset.GetSlot(Key.F11, KeyModifiers.Control) == 0 && WorkspacePreset.GetSlot(Key.D1, KeyModifiers.Control) == 0, "Other keys keep their existing actions.");

        pref.Theme = "Dark";
        pref.DefaultFontFamily = "Test Sans";
        pref.MonospaceFontFamily = "Test Mono";
        pref.DefaultFontSize = 15;
        pref.EditorFontSize = 16;
        pref.Zoom = 1.25;
        pref.UseTwoColumnsLayoutInHistories = true;
        pref.UseSideBySideDiff = true;
        pref.EnableDiffViewWordWrap = true;
        pref.Layout.RepositorySidebarWidth = 345;
        pref.Layout.WorkingCopyLeftWidth = new GridLength(456);
        pref.Layout.IsSidebarCollapsedInLocalChanges = true;
        var tabs = new List<PresetTab> { new() { Path = "D:/zh-magic", SelectedViewIndex = 1 }, new() { Path = "D:/zh-magic/Client" }, new() };
        var preset = pref.Presets[0];
        preset.Name = "마법고";
        preset.Capture(pref, tabs, 1);
        tabs[0].Path = "D:/changed";
        tabs.Reverse();
        pref.Layout.RepositorySidebarWidth = 999;
        pref.Theme = "Light";
        Check(preset.Tabs[0].Path == "D:/zh-magic" && preset.Tabs[2].Path.Length == 0 && preset.ActiveIndex == 1, "Capture keeps independent tab order, welcome tabs and selection.");
        Check(preset.Appearance.Layout.RepositorySidebarWidth == 345 && preset.Appearance.Theme == "Dark", "Later edits must not mutate saved appearance.");

        var json = JsonSerializer.Serialize(pref, JsonCodeGen.Default.Preferences);
        var loaded = JsonSerializer.Deserialize(json, JsonCodeGen.Default.Preferences)!;
        loaded.PreparePresets();
        var restored = loaded.Presets[0];
        Check(restored.IsSaved && restored.Name == "마법고" && restored.Tabs[0].SelectedViewIndex == 1 && restored.ActiveIndex == 1, "Preset names, views and selection survive a preference-file round trip.");
        loaded.EnableAutoFetch = false;
        loaded.AutoFetchInterval = 27;
        loaded.Layout.LauncherPositionX = -1920;
        loaded.Layout.LauncherWidth = 1440;
        loaded.Layout.LauncherWindowState = WindowState.Maximized;
        restored.Appearance.Apply(loaded);
        Check(loaded.Theme == "Dark" && loaded.DefaultFontSize == 15 && loaded.EditorFontSize == 16 && loaded.Zoom == 1.25, "Restore theme and text settings.");
        Check(loaded.UseTwoColumnsLayoutInHistories && loaded.UseSideBySideDiff && loaded.EnableDiffViewWordWrap && loaded.Layout.IsSidebarCollapsedInLocalChanges, "Restore history, diff and panel layout.");
        Check(loaded.Layout.RepositorySidebarWidth == 345 && loaded.Layout.WorkingCopyLeftWidth.Value == 456, "Restore independently copied panel widths.");
        Check(loaded.Layout.LauncherPositionX == -1920 && loaded.Layout.LauncherWidth == 1440 && loaded.Layout.LauncherWindowState == WindowState.Maximized, "Keep the current window geometry and monitor.");
        Check(!loaded.EnableAutoFetch && loaded.AutoFetchInterval == 27, "Visual presets must not overwrite fetch or Git preferences.");
        loaded.Layout.RepositorySidebarWidth = 777;
        Check(restored.Appearance.Layout.RepositorySidebarWidth == 345, "Applying a preset must not alias its stored layout.");
        restored.Capture(loaded, tabs, 99);
        Check(restored.ActiveIndex == 2, "Clamp invalid selected indices.");
        restored.Clear();
        Check(!restored.IsSaved && restored.Appearance == null && restored.Name == "마법고", "Clearing releases the snapshot and keeps the user-provided name.");
        loaded.Presets = [new() { Slot = 11 }, new() { Slot = 2, Name = "kept" }, new() { Slot = 2, Name = "duplicate" }, null!];
        loaded.PreparePresets();
        Check(loaded.Presets.Count == 10 && loaded.Presets[1].Name == "kept" && loaded.Presets.Select(p => p.Slot).Distinct().Count() == 10, "Normalize malformed slots without losing the first saved preset.");
        CheckMatching();
        Console.WriteLine("Preset checks passed: shortcuts, old settings migration, snapshot isolation, persistence, appearance, monitor preservation and clearing.");
    }

    private static void CheckMatching()
    {
        var pref = JsonSerializer.Deserialize("{}", JsonCodeGen.Default.Preferences)!;
        var paths = new List<PresetTab> { new() { Path = "D:/zh-magic" }, new() { Path = "D:/zh-magic/Client" } };
        var preset = new WorkspacePreset { Slot = 2 };
        preset.Capture(pref, paths, 0);
        Check(preset.Matches(pref, paths), "A newly saved configuration must be clean.");
        paths[1].SelectedViewIndex = 2;
        Check(preset.Matches(pref, paths), "Normal repository-view navigation must not mark a preset modified.");
        Check(!preset.Matches(pref, paths.AsEnumerable().Reverse()), "Repository order is part of the preset.");
        Check(!preset.Matches(pref, [new() { Path = "D:/zh-elf" }, paths[1]]), "Different AU folders must be distinguished even when folder names match.");
        Check(!preset.Matches(pref, [paths[0], new() { Path = "D:/zh-elf/Client" }]), "Distinguish different repositories with the same Client folder name by their full paths.");
        Check(!preset.Matches(pref, paths.Take(1)), "A removed repository changes the configuration.");
        pref.EnableAutoFetch = !pref.EnableAutoFetch;
        pref.Layout.LauncherPositionX = -1920;
        Check(preset.Matches(pref, paths), "Fetch and monitor changes must not set the visual-preset marker.");
        pref.Layout.RepositorySidebarWidth += 0.2;
        Check(preset.Matches(pref, paths), "Ignore subpixel sidebar rounding.");
        pref.Layout.RepositorySidebarWidth += 20;
        Check(!preset.Matches(pref, paths), "Changing sidebar width sets the marker.");
        preset.Appearance.Apply(pref);
        Check(preset.Matches(pref, paths), "Restoring saved appearance clears the marker.");
        pref.EditorFontSize++;
        Check(!preset.Matches(pref, paths), "Editor font size is part of the preset.");
        pref.ActivePresetSlot = 2;
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(pref, JsonCodeGen.Default.Preferences), JsonCodeGen.Default.Preferences)!;
        Check(roundTrip.ActivePresetSlot == 2, "Remember the last preset slot across restarts.");
        pref.ActivePresetSlot = 99;
        Check(pref.ActivePresetSlot == 0, "An invalid stored slot must not select an unrelated preset.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    public static void CheckLauncher()
    {
        var root = Path.Combine(Path.GetTempPath(), "SourceGit.PresetChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var directories = SourceGit.Native.OS.BasicDirectories;
        var configDir = directories.ConfigDir;
        var cacheDir = directories.CacheDir;
        directories.ConfigDir = root;
        directories.CacheDir = root;
        File.WriteAllText(Path.Combine(root, "preference.json"), """{"ShellOrTerminalType":0,"EnableAutoFetch":false}""");
        try
        {
            var launcher = new Launcher(null);
            var pref = Preferences.Instance;
            launcher.AddNewTab();
            launcher.SavePreset(pref.Presets[0]);
            Check(launcher.ActivePreset == pref.Presets[0] && !launcher.IsPresetModified, "Saving selects the preset and clears its modification marker.");
            pref.Presets[0].Name = "마법고";
            Check(launcher.PresetLabel == "F1 · 마법고" && launcher.PresetTooltip.Contains("Ctrl+F1"), "Reflect renamed presets and the exact shortcut in the compact label and tooltip.");
            var first = launcher.Pages[0];
            launcher.CloseTab(null);
            Check(launcher.IsPresetModified, "Closing a saved tab updates the marker immediately.");
            Check(launcher.ApplyPreset(1) && launcher.Pages.Count == 2 && launcher.Pages[0] == first && launcher.ActivePage == launcher.Pages[1], "Restore welcome tabs, exact selection and reuse an existing page.");
            Check(!launcher.IsPresetModified, "Applying a saved preset clears its marker.");
            var pages = launcher.Pages.ToArray();
            var selected = launcher.ActivePage;
            Check(!launcher.ApplyPreset(10) && launcher.Pages.SequenceEqual(pages), "An empty slot leaves current tabs untouched.");
            pref.Presets[1].Capture(pref, [new() { Path = Path.Combine(root, "missing") }], 0);
            Check(!launcher.ApplyPreset(2) && launcher.Pages.SequenceEqual(pages) && launcher.ActivePage == selected, "Missing repositories must leave current tabs and selection untouched.");
            var invalid = Path.Combine(root, "not-a-repo");
            Directory.CreateDirectory(invalid);
            pref.Presets[1].Capture(pref, [new() { Path = invalid.Replace('\\', '/') }], 0);
            Check(!launcher.ApplyPreset(2) && launcher.Pages.SequenceEqual(pages) && launcher.ActivePage == selected, "Failed repository opens remove temporary pages and preserve the original screen.");
            first.Popup = new Popup { InProgress = true };
            Check(!launcher.ApplyPreset(1) && launcher.Pages.SequenceEqual(pages), "An operation on an inactive tab must block preset switching.");
            first.Popup = new Popup();
            Check(!launcher.ApplyPreset(1), "Keep unfinished forms until the user closes them.");
            first.Popup = null;
            Check(launcher.ApplyPreset(1), "Switching is available again after the dialog closes.");
            launcher.ActivePage = first;
            Check(!launcher.IsPresetModified, "Simply selecting another tab must not set the marker.");
            pref.DefaultFontSize++;
            Check(launcher.IsPresetModified, "Observe appearance changes while the preset is active.");
            pref.DefaultFontSize--;
            Check(!launcher.IsPresetModified, "Reverting a setting clears the marker without saving.");
            launcher.AddNewTab();
            Check(launcher.IsPresetModified, "Observe new tabs on the collection installed by ApplyPreset.");
            launcher.CloseTab(null);
            Check(!launcher.IsPresetModified, "Removing the added tab restores the saved configuration.");
            pref.Layout.RepositorySidebarWidth += 25;
            Check(launcher.IsPresetModified, "Sidebar changes must notify the active-preset marker.");
            pref.Presets[1].Name = "엘프고";
            Check(launcher.QuickSavePreset(2) && launcher.ActivePreset == pref.Presets[1] && launcher.PresetLabel == "F2 · 엘프고" && !launcher.IsPresetModified, "Quick save overwrites the requested slot, selects it and clears the marker.");
            var oldLayout = pref.Layout;
            pref.Presets[1].Appearance.Apply(pref);
            oldLayout.RepositorySidebarWidth++;
            Check(!launcher.IsPresetModified, "Stop observing replaced layout objects.");
            pref.Layout.WorkingCopyLeftWidth = new GridLength(600);
            Check(launcher.IsPresetModified, "Observe split-panel sizes on the replacement layout.");
            Check(launcher.QuickSavePreset(2) && !launcher.IsPresetModified, "Resaving the active preset acknowledges the current layout.");
            pref.Presets[0].Clear();
            Check(launcher.ActivePreset == pref.Presets[1], "Clearing a different slot must not clear the current selection.");
            pref.Presets[1].Clear();
            Check(launcher.ActivePreset == null && pref.ActivePresetSlot == 0 && !launcher.IsPresetModified, "Clearing the selected slot resets the status indicator.");
            Check(!launcher.QuickSavePreset(11), "Ignore invalid quick-save slots.");
            Check(launcher.QuickSavePreset(10) && launcher.ActivePreset.Slot == 10 && !launcher.IsPresetModified, "Quick save also creates the last previously empty slot.");
            launcher.CloseOtherTabs();
            Check(launcher.IsPresetModified, "Track bulk tab closures.");
            launcher.AddNewTab();
            Check(!launcher.IsPresetModified, "Continue observing a tab collection replaced by CloseOtherTabs.");
            launcher.CloseAll();
            Check(!File.Exists(Path.Combine(root, "preference_tmp.json")), "Tests must not enable writes to a running application's preferences.");
            Console.WriteLine("Launcher preset checks passed: quick save, active label, modification/revert tracking, observer cleanup, selection, page reuse, failed-open recovery and operation guards.");
        }
        finally
        {
            directories.ConfigDir = configDir;
            directories.CacheDir = cacheDir;
            Directory.Delete(root, true);
        }
    }
}
