using System;
using System.Threading;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;

namespace SourceGit.Views
{
    public class UpdateInfoView : TextEditor
    {
        protected override Type StyleKeyOverride => typeof(TextEditor);

        public UpdateInfoView() : base(new TextArea(), new TextDocument())
        {
            IsReadOnly = true;
            ShowLineNumbers = false;
            WordWrap = true;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            TextArea.TextView.Margin = new Thickness(4, 0);
            TextArea.TextView.Options.EnableHyperlinks = false;
            TextArea.TextView.Options.EnableEmailHyperlinks = false;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            if (_textMate == null)
            {
                _textMate = Models.TextMateHelper.CreateForEditor(this);
                Models.TextMateHelper.SetGrammarByFileName(_textMate, "README.md");
            }
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);

            if (_textMate != null)
            {
                _textMate.Dispose();
                _textMate = null;
            }

            GC.Collect();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (DataContext is Models.Version ver)
                Text = ver.Body;
        }

        private TextMate.Installation _textMate = null;
    }

    public partial class SelfUpdate : ChromelessWindow
    {
        public SelfUpdate()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        private void CloseWindow(object _1, RoutedEventArgs _2)
        {
            Close();
        }

        private void GotoDownload(object _, RoutedEventArgs e)
        {
            Native.OS.OpenBrowser(Models.SelfUpdater.RepositoryUrl + "/releases/latest");
            e.Handled = true;
        }

        private async void InstallUpdate(object _, RoutedEventArgs e)
        {
            e.Handled = true;
            if (DataContext is not ViewModels.SelfUpdate { Data: Models.Version version, IsUpdating: false } vm)
                return;

            vm.IsUpdating = true;
            vm.Progress = 0;
            using var cancellation = new CancellationTokenSource();
            _downloadCancellation = cancellation;
            try
            {
                await Models.SelfUpdater.InstallAsync(version, new Progress<double>(value => vm.Progress = value), cancellation.Token);
                App.Quit(0);
            }
            catch (OperationCanceledException)
            {
                // Closing the dialog cancels the download before installation.
            }
            catch (Exception exception)
            {
                vm.Data = new Models.SelfUpdateFailed(exception);
            }
            finally
            {
                _downloadCancellation = null;
                vm.IsUpdating = false;
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            _downloadCancellation?.Cancel();
            base.OnClosing(e);
        }

        private void IgnoreThisVersion(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.Version ver })
                ViewModels.Preferences.Instance.IgnoreUpdateTag = ver.TagName;

            if (App.GetLauncher() is { } launcher)
                launcher.NewVersion = null;

            Close();
            e.Handled = true;
        }

        private CancellationTokenSource _downloadCancellation = null;
    }
}
