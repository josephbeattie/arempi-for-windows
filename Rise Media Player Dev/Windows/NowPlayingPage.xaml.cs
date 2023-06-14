﻿using CommunityToolkit.Mvvm.Input;
using Microsoft.Toolkit.Uwp.UI;
using Microsoft.Toolkit.Uwp.UI.Helpers;
using Rise.App.Helpers;
using Rise.App.UserControls;
using Rise.App.ViewModels;
using Rise.Common.Extensions;
using Rise.Common.Threading;
using Rise.Data.ViewModels;
using Rise.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Rise.App.Views
{
    /// <summary>
    /// A page that shows the current state of playback.
    /// </summary>
    public sealed partial class NowPlayingPage : Page
    {
        private MediaPlaybackViewModel MPViewModel => App.MPViewModel;
        private SettingsViewModel SViewModel => App.SViewModel;
        private bool FullScreenRequested = false;

        private List<SyncedLyricItem> _lyrics;

        // Used to check when transport controls are hiding or showing
        private TranslateTransform PlayerControlsTransform;
        private DependencyPropertyWatcher<double> PlayerControlsTransformWatcher;

        public NowPlayingPage()
        {
            InitializeComponent();
            TitleBar.SetTitleBarForCurrentView();

            MPViewModel.PlayingItemChanged += MPViewModel_PlayingItemChanged;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            _ = ApplyVisualizer(SViewModel.VisualizerType);
        }

        private void OnMainPlayerLoaded(object sender, RoutedEventArgs e)
        {
            MainPlayer.SetMediaPlayer(MPViewModel.Player);

            var controlGrid = MainPlayer.FindDescendant<Grid>((elm) => elm.Name == "ControlPanelGrid");
            if (controlGrid != null)
            {
                PlayerControlsTransform = (TranslateTransform)controlGrid.RenderTransform;

                PlayerControlsTransformWatcher = new(PlayerControlsTransform, "Y");
                PlayerControlsTransformWatcher.PropertyChanged += OnPlayerControlsTransformChanged;
            }
        }

        private void OnPlayerControlsTransformChanged(object sender, EventArgs e)
        {
            TitleAreaTranslate.Y = -PlayerControlsTransform.Y;
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            MPViewModel.Player.SeekCompleted -= Player_SeekCompleted;
            MPViewModel.Player.PlaybackSession.PositionChanged -= PlaybackSession_PositionChanged;
            MPViewModel.PlayingItemChanged -= MPViewModel_PlayingItemChanged;

            PlayerControlsTransformWatcher.Dispose();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is bool fs && fs)
                FullScreenRequested = fs;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (FullScreenRequested)
                ApplicationView.GetForCurrentView().ExitFullScreenMode();
        }
    }

    // Event handlers
    public sealed partial class NowPlayingPage
    {
        [RelayCommand]
        private void GoToMiniView()
        {
            _ = Frame.Navigate(typeof(CompactNowPlayingPage));
        }

        [RelayCommand]
        private void ToggleFullScreen()
        {
            var view = ApplicationView.GetForCurrentView();

            if (view.IsFullScreenMode)
                view.ExitFullScreenMode();
            else
                _ = view.TryEnterFullScreenMode();
        }

        private async void OnLyricsListLoaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SViewModel.MusixmatchLyricsToken))
            {
                _ = VisualStateManager.GoToState(this, "LyricsUnavailableState", true);
                return;
            }

            await FetchLyricsForCurrentItemAsync();

            MPViewModel.Player.SeekCompleted += Player_SeekCompleted;
            MPViewModel.Player.PlaybackSession.PositionChanged += PlaybackSession_PositionChanged;
        }

        private void OnExitButtonClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
            => PlayerControls.Show();

        private async void MPViewModel_PlayingItemChanged(object sender, MediaPlaybackItem e)
        {
            await Dispatcher;

            if (!string.IsNullOrWhiteSpace(SViewModel.MusixmatchLyricsToken))
                await FetchLyricsForCurrentItemAsync();
        }

        private bool ApplyVisualizer(int index) => index switch
        {
            1 => VisualStateManager.GoToState(this, "BloomVisualizerState", false),
            _ => VisualStateManager.GoToState(this, "NoVisualizerState", false),
        };
    }

    // Lyrics
    public sealed partial class NowPlayingPage
    {
        private void LyricItem_DoubleTapped(object sender, Windows.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var syncedLyricItem = (SyncedLyricItem)((LyricItem)sender).DataContext;
            MPViewModel.Player.PlaybackSession.Position = syncedLyricItem.TimeSpan + TimeSpan.FromMilliseconds(150);
        }

        private async void Player_SeekCompleted(MediaPlayer sender, object args)
            => await UpdateCurrentLyricAsync(sender.PlaybackSession.Position);

        private async void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
            => await UpdateCurrentLyricAsync(sender.Position);

        private IAsyncAction UpdateCurrentLyricAsync(TimeSpan playerPosition)
        {
            var lyricsItem = _lyrics?.LastOrDefault(item => item.TimeSpan.TotalSeconds < playerPosition.TotalSeconds);

            // The dispatcher call starts here due to wrong thread exceptions when
            // trying to access the lyric list's SelectedItem normally
            return Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (lyricsItem != null && lyricsItem != LyricsList.SelectedItem)
                {
                    var currentlySelectedLyric = _lyrics.FirstOrDefault(item => item.IsSelected);

                    if (currentlySelectedLyric != null)
                    {
                        var currentlySelectedLyricIndex = _lyrics.IndexOf(currentlySelectedLyric);
                        _lyrics[currentlySelectedLyricIndex].IsSelected = false;
                    }

                    int selectedLyricIndex = _lyrics.IndexOf(lyricsItem);
                    _lyrics[selectedLyricIndex].IsSelected = true;

                    LyricsList.SelectedIndex = selectedLyricIndex;
                    LyricsList.ScrollIntoView(lyricsItem);
                }
            });
        }

        private async Task FetchLyricsForCurrentItemAsync()
        {
            LyricsList.ItemsSource = null;
            if (MPViewModel.PlayingItemType == MediaPlaybackType.Music)
            {
                try
                {
                    var lyricsObj = await MusixmatchHelper.GetSyncedLyricsAsync(MPViewModel.PlayingItemProperties.Title, MPViewModel.PlayingItemProperties.Artist);
                    var body = lyricsObj.Message.Body;

                    if (body != null)
                    {
                        _lyrics = await Task.Run(() => new List<SyncedLyricItem>(body.Subtitle.Subtitles.Where(i => !string.IsNullOrWhiteSpace(i.Text))));
                        LyricsList.ItemsSource = _lyrics;

                        _ = VisualStateManager.GoToState(this, "LyricsAvailableState", true);
                        return;
                    }
                }
                catch (Exception e)
                {
                    e.WriteToOutput();
                }
            }

            _ = VisualStateManager.GoToState(this, "LyricsUnavailableState", true);
        }
    }
}
