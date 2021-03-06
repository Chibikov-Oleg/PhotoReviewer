using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Shell;
using Common.Logging;
using JetBrains.Annotations;
using PhotoReviewer.Contracts.DAL;
using PhotoReviewer.Contracts.View;
using PhotoReviewer.Core;
using PropertyChanged;
using Scar.Common.Events;
using Scar.Common.IO;
using Scar.Common.WPF.Commands;
using Scar.Common.WPF.View;

namespace PhotoReviewer.ViewModel
{
    //TODO: More logs
    //TODO: Localization
    //TODO: Detect photo change (rotate in standard viewer)

    [AddINotifyPropertyChangedInterface]
    [UsedImplicitly]
    public sealed class MainViewModel : IDisposable
    {
        [NotNull]
        private readonly Func<MainViewModel, Photo, PhotoViewModel> _photoViewModelFactory;
        [NotNull]
        private readonly Func<IMainWindow, PhotoViewModel, IPhotoWindow> _photoWindowFactory;
        [NotNull]
        private readonly WindowFactory<IMainWindow> _mainWindowFactory;

        [NotNull]
        private readonly ILog _logger;

        [NotNull]
        private readonly ISettingsRepository _settingsRepository;

        [NotNull]
        private readonly SynchronizationContext _syncContext = SynchronizationContext.Current;

        [NotNull]
        private readonly WindowsArranger _windowsArranger;

        public MainViewModel(
            [NotNull] PhotoCollection photoCollection,
            [NotNull] ISettingsRepository settingsRepository,
            [NotNull] WindowsArranger windowsArranger,
            [NotNull] ILog logger,
            [NotNull] Func<MainViewModel, ShiftDateViewModel> shiftDateViewModelFactory,
            [NotNull] WindowFactory<IMainWindow> mainWindowFactory,
            [NotNull] Func<MainViewModel, Photo, PhotoViewModel> photoViewModelFactory,
            [NotNull] Func<IMainWindow, PhotoViewModel, IPhotoWindow> photoWindowFactory)
        {
            PhotoCollection = photoCollection ?? throw new ArgumentNullException(nameof(photoCollection));
            _settingsRepository = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
            _windowsArranger = windowsArranger;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            shiftDateViewModelFactory = shiftDateViewModelFactory ?? throw new ArgumentNullException(nameof(shiftDateViewModelFactory));
            _mainWindowFactory = mainWindowFactory ?? throw new ArgumentNullException(nameof(mainWindowFactory));
            _photoViewModelFactory = photoViewModelFactory ?? throw new ArgumentNullException(nameof(photoViewModelFactory));
            _photoWindowFactory = photoWindowFactory ?? throw new ArgumentNullException(nameof(photoWindowFactory));

            BrowseDirectoryCommand = new CorrelationCommand(BrowseDirectory);
            ChangeDirectoryPathCommand = new CorrelationCommand<string>(ChangeDirectoryPath);
            ShowOnlyMarkedChangedCommand = new CorrelationCommand<bool>(ShowOnlyMarkedChanged);
            CopyFavoritedCommand = new CorrelationCommand(CopyFavoritedAsync);
            DeleteMarkedCommand = new CorrelationCommand(DeleteMarkedAsync);
            FavoriteCommand = new CorrelationCommand(Favorite);
            MarkForDeletionCommand = new CorrelationCommand(MarkForDeletion);
            RenameToDateCommand = new CorrelationCommand(RenameToDateAsync);
            OpenPhotoInExplorerCommand = new CorrelationCommand(OpenPhotoInExplorer);
            OpenDirectoryInExplorerCommand = new CorrelationCommand(OpenDirectoryInExplorer);
            OpenPhotoCommand = new CorrelationCommand(OpenPhotoAsync);
            SelectionChangedCommand = new CorrelationCommand<IList>(SelectionChanged);
            WindowClosingCommand = new CorrelationCommand(WindowClosing);
            OpenSettingsFolderCommand = new CorrelationCommand(OpenSettingsFolder);
            ViewLogsCommand = new CorrelationCommand(ViewLogs);
            PhotoCollection.Progress += PhotosCollection_Progress;
            PhotoCollection.CollectionChanged += PhotoCollection_CollectionChanged;
            PhotoCollection.PhotoNotification += PhotoCollection_PhotoNotification;
            ShiftDateViewModel = shiftDateViewModelFactory(this);

            var directoryPath = settingsRepository.Settings.LastUsedDirectoryPath;
            if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
            {
                SetDirectoryPathAsync(directoryPath);
            }
        }

        [NotNull]
        [DoNotNotify]
        internal IEnumerable<Photo> SelectedPhotos { get; private set; } = new Photo[0];

        [NotNull]
        [DoNotNotify]
        public PhotoCollection PhotoCollection { get; }

        [NotNull]
        [DoNotNotify]
        public ShiftDateViewModel ShiftDateViewModel { get; }

        public void Dispose()
        {
            PhotoCollection.Progress -= PhotosCollection_Progress;
            PhotoCollection.PhotoNotification -= PhotoCollection_PhotoNotification;
            PhotoCollection.CollectionChanged -= PhotoCollection_CollectionChanged;
        }

        private void BeginProgress()
        {
            ProgressState = TaskbarItemProgressState.Normal;
            ProgressDescription = "Caclulating...";
            Progress = 0;
        }

        private void EndProgress()
        {
            ProgressState = TaskbarItemProgressState.None;
        }

        private void PhotoCollection_CollectionChanged([NotNull] object sender, [NotNull] NotifyCollectionChangedEventArgs e)
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (SelectedPhoto == null)
                    {
                        SelectedPhoto = e.NewItems.Cast<Photo>().First();
                        //SelectionChanged is not hit sometimes
                        SelectionChanged(e.NewItems);
                    }

                    break;
                case NotifyCollectionChangedAction.Remove:
                    foreach (var photo in e.OldItems.Cast<Photo>())
                    {
                        _windowsArranger.ClosePhoto(photo);
                    }

                    break;
            }
        }

        private void PhotoCollection_PhotoNotification(object sender, EventArgs e)
        {
            foreach (var photo in _windowsArranger.Photos)
            {
                photo.ReloadCollectionInfoIfNeeded();
            }

            SelectedPhoto?.ReloadCollectionInfoIfNeeded();
        }

        private void PhotosCollection_Progress([NotNull] object sender, [NotNull] ProgressEventArgs e)
        {
            _syncContext.Send(
                x =>
                {
                    Progress = e.Percentage;
                    ProgressDescription = $"{e.Current} of {e.Total} ({e.Percentage} %)";
                    if (e.Current == 0)
                    {
                        BeginProgress();
                    }
                    else if (e.Current == e.Total)
                    {
                        EndProgress();
                    }
                },
                null);
        }

        /// <summary>Selects and scrolls to the first photo of current selection</summary>
        private void ReselectPhoto()
        {
            var photo = SelectedPhoto;
            SelectedPhoto = null;
            SelectedPhoto = photo;
        }

        private async void SetDirectoryPathAsync([NotNull] string directoryPath)
        {
            directoryPath = directoryPath.RemoveTrailingBackslash();
            var settings = _settingsRepository.Settings;
            var task = PhotoCollection.SetDirectoryPathAsync(directoryPath);

            if (task.IsCompleted || task.IsFaulted)
            {
                //Restore previous path since current is corrupted
                CurrentDirectoryPath = null;
                CurrentDirectoryPath = settings.LastUsedDirectoryPath;
            }
            else
            {
                _windowsArranger.ClosePhotos();
                settings.LastUsedDirectoryPath = CurrentDirectoryPath = directoryPath;
                _settingsRepository.Settings = settings;
            }

            await task.ConfigureAwait(false);
        }

        #region Dependency Properties

        public double PhotoSize { get; set; } = 230;

        [CanBeNull]
        public Photo SelectedPhoto { get; set; }

        public int SelectedCount { get; private set; }

        public int Progress { get; private set; }

        public string ProgressDescription { get; private set; }

        public TaskbarItemProgressState ProgressState { get; private set; }

        [CanBeNull]
        public string CurrentDirectoryPath { get; private set; }

        #endregion

        #region Commands

        [NotNull]
        public ICommand BrowseDirectoryCommand { get; }

        [NotNull]
        public ICommand ChangeDirectoryPathCommand { get; }

        [NotNull]
        public ICommand ShowOnlyMarkedChangedCommand { get; }

        [NotNull]
        public ICommand CopyFavoritedCommand { get; }

        [NotNull]
        public ICommand DeleteMarkedCommand { get; }

        [NotNull]
        public ICommand FavoriteCommand { get; }

        [NotNull]
        public ICommand MarkForDeletionCommand { get; }

        [NotNull]
        public ICommand OpenPhotoInExplorerCommand { get; }

        [NotNull]
        public ICommand OpenDirectoryInExplorerCommand { get; }

        [NotNull]
        public ICommand RenameToDateCommand { get; }

        [NotNull]
        public ICommand OpenPhotoCommand { get; }

        [NotNull]
        public ICommand SelectionChangedCommand { get; }

        [NotNull]
        public ICommand WindowClosingCommand { get; }

        [NotNull]
        public ICommand ViewLogsCommand { get; }

        [NotNull]
        public ICommand OpenSettingsFolderCommand { get; }

        #endregion

        #region Command handlers

        private void BrowseDirectory()
        {
            _logger.Trace("Browsing directory...");
            //TODO: Another dialog third party? Use OpenFileService and DI
            var dialog = new FolderBrowserDialog();
            var lastUsedPath = _settingsRepository.Settings.LastUsedDirectoryPath;

            if (!string.IsNullOrEmpty(lastUsedPath))
            {
                dialog.SelectedPath = lastUsedPath;
            }

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            _logger.Debug($"Changing directory path to {dialog.SelectedPath}...");
            SetDirectoryPathAsync(dialog.SelectedPath);
        }

        private void ChangeDirectoryPath([NotNull] string directoryPath)
        {
            _logger.Debug($"Changing directory path to {directoryPath}...");
            if (directoryPath == null)
            {
                throw new ArgumentNullException(nameof(directoryPath));
            }

            SetDirectoryPathAsync(directoryPath);
        }

        private void ShowOnlyMarkedChanged(bool isChecked)
        {
            _logger.Debug($"Setting show only marked to {isChecked}...");
            PhotoCollection.ChangeFilter(isChecked);
        }

        private async void CopyFavoritedAsync()
        {
            _logger.Info("Copying favorited photos...");
            await PhotoCollection.CopyFavoritedAsync().ConfigureAwait(false);
        }

        private async void DeleteMarkedAsync()
        {
            _logger.Info("Deleting marked photos...");
            await PhotoCollection.DeleteMarkedAsync().ConfigureAwait(false);
        }

        internal void Favorite()
        {
            _logger.Info("(Un)Favoriting selected photos...");
            if (!SelectedPhotos.Any())
            {
                throw new InvalidOperationException("Photos are not selected");
            }

            PhotoCollection.Favorite(SelectedPhotos.ToArray());
        }

        internal void MarkForDeletion()
        {
            _logger.Info("(Un)Marking selected photos for deletion...");
            if (!SelectedPhotos.Any())
            {
                throw new InvalidOperationException("Photos are not selected");
            }

            PhotoCollection.MarkForDeletion(SelectedPhotos.ToArray());
        }

        internal async void RenameToDateAsync()
        {
            _logger.Info("Renaming selected photos to date...");
            if (!SelectedPhotos.Any())
            {
                throw new InvalidOperationException("Photos are not selected");
            }

            await PhotoCollection.RenameToDateAsync(SelectedPhotos.ToArray()).ConfigureAwait(false);
        }

        private void OpenPhotoInExplorer()
        {
            _logger.Trace($"Opening selected photo ({SelectedPhoto?.FileLocation}) in explorer...");
            if (SelectedPhoto == null)
            {
                throw new InvalidOperationException("Photos are not selected");
            }

            SelectedPhoto.FileLocation.ToString().OpenFileInExplorer();
        }

        private void OpenDirectoryInExplorer()
        {
            _logger.Trace($"Opening current directory ({CurrentDirectoryPath}) in explorer...");
            if (CurrentDirectoryPath == null)
            {
                throw new InvalidOperationException("No directory entered");
            }

            CurrentDirectoryPath.OpenDirectoryInExplorer();
        }

        private async void OpenPhotoAsync()
        {
            _logger.Trace($"Opening selected photo ({SelectedPhoto?.FileLocation}) in a separate window...");
            if (SelectedPhoto == null)
            {
                throw new InvalidOperationException("Photos are not selected");
            }

            ReselectPhoto();
            var mainWindow = await _mainWindowFactory.GetWindowAsync(CancellationToken.None);
            var photoViewModel = _photoViewModelFactory(this, SelectedPhoto);
            //Window is shown in its constructor
            var window = _photoWindowFactory(mainWindow, photoViewModel);
            _windowsArranger.Add(window);
        }

        private void SelectionChanged([CanBeNull] IList items)
        {
            SelectedPhotos = items?.Cast<Photo>() ?? throw new ArgumentNullException(nameof(items));
            SelectedCount = SelectedPhotos.Count();
            SelectedPhoto?.ReloadCollectionInfoIfNeeded();
        }

        private void WindowClosing()
        {
            _logger.Trace("Performing cleanup before dependencies disposal...");
            //Need to finish current task before disposal (especially, repository)
            PhotoCollection.CancelCurrentTasks();
        }

        private void OpenSettingsFolder()
        {
            _logger.Trace($"Opening setting folder ({CommonPaths.SettingsPath})...");
            $@"{CommonPaths.SettingsPath}".OpenDirectoryInExplorer();
        }

        private void ViewLogs()
        {
            var logsPath = $@"{Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}\{nameof(Scar)}\{nameof(PhotoReviewer)}\Logs\Full.log";
            _logger.Trace($"Viewing logs file ({logsPath})...");
            logsPath.OpenFile();
        }

        #endregion
    }
}