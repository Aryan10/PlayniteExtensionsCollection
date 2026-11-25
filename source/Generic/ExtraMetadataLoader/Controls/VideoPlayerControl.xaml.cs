using ExtraMetadataLoader.Models;
using EmlFullscreen;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using PluginsCommon;
using FlowHttp;
using SteamCommon.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Net.Http;

namespace ExtraMetadataLoader
{
    /// <summary>
    /// Interaction logic for VideoPlayerControl.xaml
    /// </summary>
    public partial class VideoPlayerControl : PluginUserControl, INotifyPropertyChanged
    {
        private static readonly ILogger logger = LogManager.GetLogger(); 
        private static readonly Regex steamLinkRegex = new Regex(@"^https?:\/\/store\.steampowered\.com\/app\/(\d+)", RegexOptions.None);
        public enum ActiveVideoType { Microtrailer, Trailer, None }; 
        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        IPlayniteAPI PlayniteApi; public ExtraMetadataLoaderSettingsViewModel SettingsModel { get; set; }

        
        private bool useMicrovideosSource;
        private readonly string pluginDataPath;
        private readonly DispatcherTimer updatePlayerTimer;
        private ActiveVideoType activeVideoType;
        private bool isDragging;
        private Uri microVideoPath;
        private Uri trailerVideoPath;
        private bool multipleSourcesAvailable = false;
        private Game currentGame;
        private CancellationTokenSource updateCancellationTokenSource;

        private DesktopView activeViewAtCreation;
        public DesktopView ActiveViewAtCreation
        {
            get => activeViewAtCreation;
            set
            {
                activeViewAtCreation = value;
                OnPropertyChanged();
            }
        }

        private Uri videoSource;
        public Uri VideoSource
        {
            get => videoSource;
            set
            {
                videoSource = value;
                OnPropertyChanged();
            }
        }

        private bool isSoundEnabled = false;
        public bool IsSoundEnabled
        {
            get => isSoundEnabled;
            set
            {
                isSoundEnabled = value;
                OnPropertyChanged();
            }
        }

        private bool displayControls = true;
        public bool DisplayControls
        {
            get => displayControls;
            set
            {
                displayControls = value;
                OnPropertyChanged();
            }
        }

        private bool isPlayerMuted = false;
        public bool IsPlayerMuted
        {
            get => isPlayerMuted;
            set
            {
                isPlayerMuted = value;
                OnPropertyChanged();
            }
        }

        private bool _isInFullscreen = false;
        public RelayCommand EnterFullscreenCommand { get; private set; }

        public double VideoPlayerVolumeLinear
        {
            get => Math.Sqrt(videoPlayerVolume);
            set
            {
                videoPlayerVolume = value * value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoPlayerVolume));
            }
        }

        private double videoPlayerVolume;
        public double VideoPlayerVolume
        {
            get => videoPlayerVolume;
            set
            {
                videoPlayerVolume = value * value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VideoPlayerVolumeLinear));
            }
        }

        DispatcherTimer timer;
        private string playbackTimeProgress = "00:00";
        public string PlaybackTimeProgress
        {
            get => playbackTimeProgress;
            set
            {
                playbackTimeProgress = value;
                OnPropertyChanged();
            }
        }
        private string playbackTimeTotal = "00:00";
        public string PlaybackTimeTotal
        {
            get => playbackTimeTotal;
            set
            {
                playbackTimeTotal = value;
                OnPropertyChanged();
            }
        }

        private Visibility controlVisibility = Visibility.Collapsed;
        public Visibility ControlVisibility
        {
            get => controlVisibility;
            set
            {
                controlVisibility = value;
                OnPropertyChanged();
            }
        }

        public VideoPlayerControl(IPlayniteAPI PlayniteApi, ExtraMetadataLoaderSettingsViewModel settings, string pluginDataPath, bool isConfiguredControl = false, bool displayControls = true, bool useSound = true)
        {
            InitializeComponent();
            this.pluginDataPath = pluginDataPath;
            this.PlayniteApi = PlayniteApi;
            SettingsModel = settings;
            DataContext = this;

            useMicrovideosSource = settings.Settings.UseMicrotrailersDefault;
            IsSoundEnabled = useSound;
            DisplayControls = displayControls;
            if (settings.Settings.StartNoSound || !IsSoundEnabled)
            {
                IsPlayerMuted = true;
            }

            var initialVolumeValue = settings.Settings.DefaultVolume / 100;
            volumeSlider.Value = initialVolumeValue;
            VideoPlayerVolume = initialVolumeValue;

            if (!isConfiguredControl && PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop)
            {
                ActiveViewAtCreation = PlayniteApi.MainView.ActiveDesktopView;
            }

            timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(250);
            timer.Tick += new EventHandler(timer_Tick);

            updatePlayerTimer = new DispatcherTimer();
            updatePlayerTimer.Interval = TimeSpan.FromMilliseconds(220);
            updatePlayerTimer.Tick += new EventHandler(UpdaterPlayerTimer_Tick);

            SetControlTextBlockStyle();

            EnterFullscreenCommand = new RelayCommand(() => EnterFullscreen(), () => true);
        }

        private void SetControlTextBlockStyle()
        {
            // Desktop mode uses BaseTextBlockStyle and Fullscreen Mode uses TextBlockBaseStyle
            var baseStyleName = "BaseTextBlockStyle";
            if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Fullscreen)
            {
                baseStyleName = "TextBlockBaseStyle";
            }

            if (ResourceProvider.GetResource(baseStyleName) is Style baseStyle &&
                baseStyle.TargetType == typeof(TextBlock))
            {
                var implicitStyle = new Style(typeof(TextBlock), baseStyle);
                Resources.Add(typeof(TextBlock), implicitStyle);
            }
        }

        private async void UpdaterPlayerTimer_Tick(object sender, EventArgs e)
        {
            updatePlayerTimer.Stop();
            if (!SettingsModel.Settings.EnableVideoPlayer || currentGame == null)
            {
                return;
            }

            var targetGame = currentGame;
            updateCancellationTokenSource?.Cancel();
            updateCancellationTokenSource?.Dispose();
            var cts = new CancellationTokenSource();
            updateCancellationTokenSource = cts;

            try
            {
                var resolved = await ResolveGameVideoSourcesAsync(targetGame, cts.Token);
                if (cts.IsCancellationRequested || currentGame == null || currentGame.Id != targetGame.Id)
                {
                    return;
                }

                ApplyResolvedVideoSources(resolved);
                playingContextChanged();
            }
            catch (OperationCanceledException)
            {
                // Selection changed before video resolution completed
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error updating video sources for game {targetGame.Name}");
            }
        }

        private void timer_Tick(object sender, EventArgs e)
        {
            if (!isDragging)
            {
                timelineSlider.Value = player.Position.TotalSeconds;
            }

            PlaybackTimeProgress = player.Position.ToString(@"mm\:ss") ?? "00:00";
            playbackProgressBar.Value = player.Position.TotalSeconds;
        }

        private void timelineSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            isDragging = true;
        }

        private void timelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            isDragging = false;
            player.Position = TimeSpan.FromSeconds(timelineSlider.Value);
        }

        private void timelineSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!isDragging)
            {
                var delta = e.GetPosition(timelineSlider).X / timelineSlider.ActualWidth;
                if (player.NaturalDuration.HasTimeSpan)
                {
                    player.Position = TimeSpan.FromSeconds(timelineSlider.Maximum * delta - timelineSlider.Minimum);
                }
            }
        }

        public RelayCommand<object> VideoPlayCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                MediaPlay();
            }, (a) => !SettingsModel.Settings.IsVideoPlaying && VideoSource != null);
        }

        void MediaPlay()
        {
            if (_isInFullscreen) return;

            player.Play();
            timer.Start();
            SettingsModel.Settings.IsVideoPlaying = true;

            OnPropertyChanged(nameof(VideoPlayCommand));
            OnPropertyChanged(nameof(VideoPauseCommand));
        }

        public RelayCommand VideoPauseCommand
        {
            get => new RelayCommand(() =>
            {
                MediaPause();
            }, () => SettingsModel.Settings.IsVideoPlaying && VideoSource != null);
        }

        public RelayCommand SwitchAutoPlayStatusCommand
        {
            get => new RelayCommand(() =>
            {
                SettingsModel.Settings.AutoPlayVideos = !SettingsModel.Settings.AutoPlayVideos;
            });
        }

        public void MediaPause()
        {
            player.Pause();
            timer.Stop();
            SettingsModel.Settings.IsVideoPlaying = false;

            OnPropertyChanged(nameof(VideoPlayCommand));
            OnPropertyChanged(nameof(VideoPauseCommand));
        }

        public RelayCommand<object> VideoMuteCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                MediaMute();
            });
        }

        void MediaMute()
        {
            IsPlayerMuted = !IsPlayerMuted;
        }

        public RelayCommand<object> SwitchVideoSourceCommand
        {
            get => new RelayCommand<object>((a) =>
            {
                SwitchVideoSource();
            }, (a) => multipleSourcesAvailable == true);
        }

        async void SwitchVideoSource()
        {
            if (currentGame == null)
            {
                return;
            }

            var targetGame = currentGame;
            var activeVideoTypeSender = activeVideoType;
            var sourceSwitched = false;
            ResetPlayerValues();

            // Paths need to be revaluated in case videos were deleted since video playing started
            try
            {
                var resolved = await ResolveGameVideoSourcesAsync(targetGame, CancellationToken.None);
                if (currentGame == null || currentGame.Id != targetGame.Id)
                {
                    return;
                }

                ApplyResolvedVideoSources(resolved);
                if (activeVideoTypeSender == ActiveVideoType.Trailer && microVideoPath != null)
                {
                    VideoSource = NormalizeVideoUri(microVideoPath);
                    activeVideoType = ActiveVideoType.Microtrailer;
                    sourceSwitched = true;
                }
                else if (activeVideoTypeSender == ActiveVideoType.Microtrailer && trailerVideoPath != null)
                {
                    VideoSource = NormalizeVideoUri(trailerVideoPath);
                    activeVideoType = ActiveVideoType.Trailer;
                    sourceSwitched = true;
                }

                if (sourceSwitched)
                {
                    useMicrovideosSource = !useMicrovideosSource;
                    playingContextChanged();
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Error switching video source for game {targetGame.Name}");
            }
        }

        private void player_MediaOpened(object sender, EventArgs e)
        {
            if (player.NaturalDuration.HasTimeSpan)
            {
                TimeSpan ts = player.NaturalDuration.TimeSpan;
                timelineSlider.SmallChange = 0.25;
                timelineSlider.LargeChange = Math.Min(10, ts.Seconds / 10);
                timelineSlider.Maximum = ts.TotalSeconds;
                playbackProgressBar.Maximum = ts.TotalSeconds;
                PlaybackTimeTotal = ts.ToString(@"mm\:ss");
            }

            OnPropertyChanged(nameof(VideoPlayCommand));
            OnPropertyChanged(nameof(VideoPauseCommand));
        }

        private void player_MediaEnded(object sender, EventArgs e)
        {
            if (_isInFullscreen) return;

            if (activeVideoType == ActiveVideoType.Trailer && SettingsModel.Settings.RepeatTrailerVideos
                || activeVideoType == ActiveVideoType.Microtrailer || !displayControls)
            {
                player.Position = new TimeSpan(0, 0, 0);
                MediaPlay();
            }
            else
            {
                player.Stop();
                timer.Stop();
                SettingsModel.Settings.IsVideoPlaying = false;
            }

            OnPropertyChanged(nameof(VideoPlayCommand));
            OnPropertyChanged(nameof(VideoPauseCommand));
        }

        public void ResetPlayerValues()
        {
            VideoSource = null;
            player.Stop();
            SettingsModel.Settings.IsVideoPlaying = false;
            timelineSlider.Value = 0;
            playbackProgressBar.Value = 0;
            PlaybackTimeProgress = "00:00";
            PlaybackTimeTotal = "00:00";
            activeVideoType = ActiveVideoType.None;
            SettingsModel.Settings.IsAnyVideoAvailable = false;
            SettingsModel.Settings.IsTrailerAvailable = false;
            SettingsModel.Settings.IsMicrotrailerAvailable = false;
            microVideoPath = null;
            trailerVideoPath = null;
            multipleSourcesAvailable = false;

            OnPropertyChanged(nameof(VideoPlayCommand));
            OnPropertyChanged(nameof(VideoPauseCommand));
        }

        private void Player_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                EnterFullscreenCommand?.Execute(null);
            }
        }

        private void EnterFullscreen()
        {
            if (_isInFullscreen || VideoSource == null) return;
            
            _isInFullscreen = true;
            bool wasPlaying = SettingsModel.Settings.IsVideoPlaying;
            TimeSpan currentPosition = player.Position;
            double currentVolume = VideoPlayerVolumeLinear;
            bool isMuted = IsPlayerMuted;
            
            player.Pause();
            timer.Stop();
            SettingsModel.Settings.IsVideoPlaying = false;

            Window ownerWindow = Window.GetWindow(this);
            var fsWindow = new FullscreenVideoWindow(
                source: VideoSource,
                startPosition: currentPosition,
                volume: currentVolume,
                startPlaying: wasPlaying,
                shouldLoop: true,
                isMuted: isMuted
            );
            
            if (ownerWindow != null)
            {
                fsWindow.Owner = ownerWindow;
                fsWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                fsWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            fsWindow.ShowDialog();
            
            ExitFullscreen(fsWindow);
        }

        private void ExitFullscreen(FullscreenVideoWindow fsWindow)
        {
            _isInFullscreen = false;
            
            // Sync play state, position, volume, and mute exactly from the fullscreen window's exit state
            bool shouldPlay = fsWindow.WasPlaying;
            TimeSpan finalPosition = fsWindow.ExitPosition;
            VideoPlayerVolumeLinear = fsWindow.ExitVolume;
            IsPlayerMuted = fsWindow.ExitMuted;

            player.Position = finalPosition;
            
            if (shouldPlay && displayControls)
            {
                MediaPlay();
            }
        }

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            //The GameContextChanged method is rised even when the control
            //is not in the active view. To prevent unecessary processing we
            //can stop processing if the active view is not the same one was
            //the one during creation
            if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop &&
                ActiveViewAtCreation != PlayniteApi.MainView.ActiveDesktopView)
            {
                updateCancellationTokenSource?.Cancel();
                VideoSource = null;
                return;
            }

            currentGame = null;
            if (newContext != null)
            {
                currentGame = newContext;
            }

            RefreshPlayer();
        }

        public void RefreshPlayer()
        {
            updateCancellationTokenSource?.Cancel();
            ResetPlayerValues();
            ControlVisibility = Visibility.Collapsed;
            SettingsModel.Settings.NewContextVideoAvailable = false;

            // Used to prevent using processing while quickly changing games
            updatePlayerTimer.Stop();
            updatePlayerTimer.Start();
        }

        private void playingContextChanged()
        {
            if (videoSource == null)
            {
                SettingsModel.Settings.NewContextVideoAvailable = false;
                ControlVisibility = Visibility.Collapsed;
                return;
            }

            SettingsModel.Settings.NewContextVideoAvailable = true;
            if (SettingsModel.Settings.AutoPlayVideos || !displayControls)
            {
                MediaPlay();
            }
            else
            {
                SettingsModel.Settings.IsVideoPlaying = false;
            }
            ControlVisibility = Visibility.Visible;

            OnPropertyChanged(nameof(VideoPlayCommand));
            OnPropertyChanged(nameof(VideoPauseCommand));
        }

        private class ResolvedVideoSources
        {
            public Uri TrailerVideoPath { get; set; }
            public Uri MicroVideoPath { get; set; }
            public bool MultipleSourcesAvailable { get; set; }
            public bool IsTrailerAvailable { get; set; }
            public bool IsMicrotrailerAvailable { get; set; }
            public Uri SelectedVideoSource { get; set; }
            public ActiveVideoType SelectedVideoType { get; set; } = ActiveVideoType.None;
        }

        public void UpdateGameVideoSources()
        {
            if (currentGame == null)
            {
                return;
            }

            var resolved = ResolveGameVideoSourcesAsync(currentGame, CancellationToken.None).GetAwaiter().GetResult();
            ApplyResolvedVideoSources(resolved);
        }

        private void ApplyResolvedVideoSources(ResolvedVideoSources resolved)
        {
            if (resolved == null)
            {
                return;
            }

            trailerVideoPath = resolved.TrailerVideoPath;
            microVideoPath = resolved.MicroVideoPath;
            multipleSourcesAvailable = resolved.MultipleSourcesAvailable;
            SettingsModel.Settings.IsTrailerAvailable = resolved.IsTrailerAvailable;
            SettingsModel.Settings.IsMicrotrailerAvailable = resolved.IsMicrotrailerAvailable;
            SettingsModel.Settings.IsAnyVideoAvailable = resolved.IsTrailerAvailable || resolved.IsMicrotrailerAvailable;

            if (resolved.SelectedVideoSource != null)
            {
                VideoSource = NormalizeVideoUri(resolved.SelectedVideoSource);
                activeVideoType = resolved.SelectedVideoType;
            }
        }

        private Task<ResolvedVideoSources> ResolveGameVideoSourcesAsync(Game game, CancellationToken cancellationToken)
        {
            var streamSteamVideosOnDemand = SettingsModel.Settings.StreamSteamVideosOnDemand;
            var streamSteamHighQuality = SettingsModel.Settings.StreamSteamHighQuality;
            var fallbackVideoSource = SettingsModel.Settings.FallbackVideoSource;
            var configPath = PlayniteApi.Paths.ConfigurationPath;
            var useMicrovideos = useMicrovideosSource;

            return Task.Run(async () =>
            {
                var result = new ResolvedVideoSources();
                cancellationToken.ThrowIfCancellationRequested();

                var videoPath = Path.Combine(configPath, "ExtraMetadata", "games", game.Id.ToString(), "VideoTrailer.mp4");
                if (FileSystem.FileExists(videoPath))
                {
                    result.IsTrailerAvailable = true;
                    result.TrailerVideoPath = new Uri(videoPath);
                }

                var videoMicroPath = Path.Combine(configPath, "ExtraMetadata", "games", game.Id.ToString(), "VideoMicrotrailer.mp4");
                if (FileSystem.FileExists(videoMicroPath))
                {
                    result.IsMicrotrailerAvailable = true;
                    result.MicroVideoPath = new Uri(videoMicroPath);
                }

                if (streamSteamVideosOnDemand && result.TrailerVideoPath == null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var gameDataPath = Path.Combine(pluginDataPath, $"{game.Id}_SteamAppDetails.json");
                    var jsonDownloadValid = true;
                    if (!FileSystem.FileExists(gameDataPath))
                    {
                        string steamId = GetSteamId(game);
                        if (steamId != null)
                        {
                            var url = string.Format(@"https://store.steampowered.com/api/appdetails?appids={0}", steamId);
                            HttpRequestFactory.GetHttpFileRequest().WithUrl(url).WithDownloadTo(gameDataPath).DownloadFile();
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    if (FileSystem.FileExists(gameDataPath) && jsonDownloadValid)
                    {
                        var jsonString = FileSystem.ReadStringFromFile(gameDataPath);
                        try
                        {
                            var parsedData = Serialization.FromJson<Dictionary<string, SteamAppDetails>>(jsonString);
                            if (parsedData.Keys?.Any() == true)
                            {
                                var response = parsedData[parsedData.Keys.First()];
                                if (response.success == true && response.data != null)
                                {
                                    if (result.TrailerVideoPath is null)
                                    {
                                        var movie = response.data?.Movies?.FirstOrDefault();
                                        if (movie?.Id != null)
                                        {
                                            // choose guessed filename based on StreamSteamHighQuality setting
                                            var guessedFile = streamSteamHighQuality ? "movie_max.mp4" : "movie480.mp4";
                                            var guessedUrl = $"https://video.akamai.steamstatic.com/store_trailers/{movie.Id}/{guessedFile}";
                                            try
                                            {
                                                var uri = new Uri(guessedUrl);
                                                using (var client = new HttpClient())
                                                {
                                                    var request = new HttpRequestMessage(HttpMethod.Head, uri);
                                                    var httpResponse = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                                                    if (httpResponse.IsSuccessStatusCode)
                                                    {
                                                        result.TrailerVideoPath = uri;
                                                        result.IsTrailerAvailable = true;
                                                    }
                                                }
                                            }
                                            catch (UriFormatException)
                                            {
                                                logger.Error($"Error forming guessed Steam trailer video url: {guessedUrl}");
                                            }
                                            catch (HttpRequestException)
                                            {
                                                logger.Info($"Guessed Steam trailer not available: {guessedUrl}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            // According to #169, it seems that for some reason the uri redirects to
                            // another page and a html source gets downloaded so it needs to be deleted
                            logger.Error($"Error deserializing steam appdetails json file in {gameDataPath}");
                            FileSystem.DeleteFile(gameDataPath);
                        }
                    }
                }

                if (result.TrailerVideoPath != null && result.MicroVideoPath != null)
                {
                    result.MultipleSourcesAvailable = true;
                }

                if (useMicrovideos)
                {
                    if (result.MicroVideoPath != null)
                    {
                        result.SelectedVideoSource = result.MicroVideoPath;
                        result.SelectedVideoType = ActiveVideoType.Microtrailer;
                    }
                    else if (result.TrailerVideoPath != null && fallbackVideoSource)
                    {
                        result.SelectedVideoSource = result.TrailerVideoPath;
                        result.SelectedVideoType = ActiveVideoType.Trailer;
                    }
                }
                else
                {
                    if (result.TrailerVideoPath != null)
                    {
                        result.SelectedVideoSource = result.TrailerVideoPath;
                        result.SelectedVideoType = ActiveVideoType.Trailer;
                    }
                    else if (result.MicroVideoPath != null && fallbackVideoSource)
                    {
                        result.SelectedVideoSource = result.MicroVideoPath;
                        result.SelectedVideoType = ActiveVideoType.Microtrailer;
                    }
                }

                return result;
            }, cancellationToken);
        }

        private static Uri NormalizeVideoUri(Uri original)
        {
            if (original.Scheme == Uri.UriSchemeHttps)
            {
                // Workaround for MediaElement crash when playing Steam videos over HTTPS (#677).
                // The crash occurs inside WPF’s internal OpenMedia method at:
                //   if (!SecurityHelper.AreStringTypesEqual(uri2.Scheme, Uri.UriSchemeHttps))
                // where `uri2` is null.
                //
                // Switching to HTTP avoids this code path and resolves the issue.
                // This is safe in our case since Steam trailers are public/non-sensitive content.
                var builder = new UriBuilder(original)
                {
                    Scheme = Uri.UriSchemeHttp,
                    Port = -1 // reset to default
                };

                return builder.Uri;
            }

            return original;
        }

        private string GetSteamId(Game game)
        {
            if (game.PluginId == Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab"))
            {
                return game.GameId;
            }
            else
            {
                if (game.Links == null)
                {
                    return null;
                }

                foreach (Link gameLink in game.Links)
                {
                    if (gameLink.Url.IsNullOrEmpty())
                    {
                        continue;
                    }
                    
                    var linkMatch = steamLinkRegex.Match(gameLink.Url);
                    if (linkMatch.Success)
                    {
                        return linkMatch.Groups[1].Value;
                    }
                }

                return null;
            }
        }
    }
}