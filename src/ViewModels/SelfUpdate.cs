using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SelfUpdate : ObservableObject
    {
        public object Data
        {
            get => _data;
            set => SetProperty(ref _data, value);
        }

        public bool IsUpdating
        {
            get => _isUpdating;
            set => SetProperty(ref _isUpdating, value);
        }

        public double Progress
        {
            get => _progress;
            set => SetProperty(ref _progress, value);
        }

        private object _data = null;
        private bool _isUpdating = false;
        private double _progress = 0;
    }
}
