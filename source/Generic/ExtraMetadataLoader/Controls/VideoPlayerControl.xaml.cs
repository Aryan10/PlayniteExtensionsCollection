using ExtraMetadataLoader.Models;
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
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

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

        private static Uri EnsureMediaElementCompatibleUri(Uri uri)
        {
            if (uri == null)
            {
                return null;
            }

            if (uri.IsAbsoluteUri && uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(uri) { Scheme = "http", Port = -1 };
                return builder.Uri;
            }

            return uri;
        }

        private Uri videoSource;
        public Uri VideoSource
        {
            get => videoSource;
            set
            {
                var compatibleUri = EnsureMediaElementCompatibleUri(value);
                if (videoSource != compatibleUri)
                {
                    videoSource = compatibleUri;
                    OnPropertyChanged();
                }
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
            var resolved = await Task.Run(() => ResolveGameVideoSources(targetGame));

            if (currentGame == null || currentGame.Id != targetGame.Id)
            {
                return;
            }

            ApplyVideoSources(resolved);
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

        void SwitchVideoSource()
        {
            var activeVideoTypeSender = activeVideoType;
            var sourceSwitched = false;
            ResetPlayerValues();

            // Paths need to be revaluated in case videos were deleted since video playing started
            UpdateGameVideoSources();
            if (activeVideoTypeSender == ActiveVideoType.Trailer && microVideoPath != null)
            {
                VideoSource = microVideoPath;
                activeVideoType = ActiveVideoType.Microtrailer;
                sourceSwitched = true;
            }
            else if (activeVideoTypeSender == ActiveVideoType.Microtrailer && trailerVideoPath != null)
            {
                VideoSource = trailerVideoPath;
                activeVideoType = ActiveVideoType.Trailer;
                sourceSwitched = true;
            }

            if (sourceSwitched)
            {
                useMicrovideosSource = !useMicrovideosSource;
                playingContextChanged();
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

        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            //The GameContextChanged method is rised even when the control
            //is not in the active view. To prevent unecessary processing we
            //can stop processing if the active view is not the same one was
            //the one during creation
            if (PlayniteApi.ApplicationInfo.Mode == ApplicationMode.Desktop &&
                ActiveViewAtCreation != PlayniteApi.MainView.ActiveDesktopView)
            {
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
            player.Pause();
            VideoSource = null;
            SettingsModel.Settings.IsVideoPlaying = false;
            SettingsModel.Settings.IsAnyVideoAvailable = false;
            SettingsModel.Settings.IsTrailerAvailable = false;
            SettingsModel.Settings.IsMicrotrailerAvailable = false;
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
            public bool IsTrailerAvailable { get; set; }
            public bool IsMicrotrailerAvailable { get; set; }
            public bool MultipleSourcesAvailable { get; set; }
            public Uri SelectedVideoSource { get; set; }
            public ActiveVideoType SelectedVideoType { get; set; } = ActiveVideoType.None;
        }

        private ResolvedVideoSources ResolveGameVideoSources(Game game)
        {
            var result = new ResolvedVideoSources();
            if (game == null)
            {
                return result;
            }

            var videoPath = Path.Combine(PlayniteApi.Paths.ConfigurationPath, "ExtraMetadata", "games", game.Id.ToString(), "VideoTrailer.mp4");
            if (FileSystem.FileExists(videoPath))
            {
                result.IsTrailerAvailable = true;
                result.TrailerVideoPath = new Uri(videoPath);
            }

            var videoMicroPath = Path.Combine(PlayniteApi.Paths.ConfigurationPath, "ExtraMetadata", "games", game.Id.ToString(), "VideoMicrotrailer.mp4");
            if (FileSystem.FileExists(videoMicroPath))
            {
                result.IsMicrotrailerAvailable = true;
                result.MicroVideoPath = new Uri(videoMicroPath);
            }

            if (SettingsModel.Settings.StreamSteamVideosOnDemand && result.TrailerVideoPath == null)
            {
                var gameDataPath = Path.Combine(pluginDataPath, $"{game.Id}_SteamAppDetails.json");
                var jsonDownloadValid = true;
                if (!FileSystem.FileExists(gameDataPath))
                {
                    string steamId = GetSteamId(game);
                    if (steamId != null)
                    {
                        var url = string.Format(@"https://store.steampowered.com/api/appdetails?appids={0}", steamId);
                        var downloadResult = HttpRequestFactory.GetHttpFileRequest().WithUrl(url).WithDownloadTo(gameDataPath).DownloadFile();
                        if (!downloadResult.IsSuccess)
                        {
                            jsonDownloadValid = false;
                        }
                    }
                    else
                    {
                        jsonDownloadValid = false;
                    }
                }

                if (FileSystem.FileExists(gameDataPath) && jsonDownloadValid)
                {
                    var jsonString = FileSystem.ReadStringFromFile(gameDataPath);
                    try
                    {
                        var parsedData = Serialization.FromJson<Dictionary<string, SteamAppDetails>>(jsonString);
                        if (parsedData?.Keys?.Any() == true)
                        {
                            var response = parsedData[parsedData.Keys.First()];
                            if (response?.success == true && response.data?.Movies?.Count > 0)
                            {
                                bool highQuality = SettingsModel.Settings.StreamSteamHighQuality;
                                foreach (var movie in response.data.Movies)
                                {
                                    if (movie == null)
                                    {
                                        continue;
                                    }

                                    // 1. Direct Mp4 in API response if available (older games)
                                    if (movie.Mp4 != null)
                                    {
                                        var directMp4 = highQuality ? (movie.Mp4.Max ?? movie.Mp4.Q480) : (movie.Mp4.Q480 ?? movie.Mp4.Max);
                                        if (directMp4 != null)
                                        {
                                            result.TrailerVideoPath = directMp4;
                                            break;
                                        }
                                    }

                                    // 2. Steam CDN direct MP4 using movie.Id (modern Steam games where API omits mp4)
                                    // Use http:// to prevent WPF MediaPlayerState.OpenMedia NullReferenceException on non-ClickOnce apps
                                    if (movie.Id > 0)
                                    {
                                        var cdnCandidate = new Uri(highQuality
                                            ? $"http://steamcdn-a.akamaihd.net/steam/apps/{movie.Id}/movie_max.mp4"
                                            : $"http://steamcdn-a.akamaihd.net/steam/apps/{movie.Id}/movie480.mp4");

                                        bool reachable = false;
                                        try
                                        {
                                            var req = (HttpWebRequest)WebRequest.Create(cdnCandidate);
                                            req.Method = "HEAD";
                                            req.Timeout = 1500;
                                            using (var resp = (HttpWebResponse)req.GetResponse())
                                            {
                                                if (resp.StatusCode == HttpStatusCode.OK)
                                                {
                                                    reachable = true;
                                                }
                                            }
                                        }
                                        catch
                                        {
                                            if (highQuality)
                                            {
                                                try
                                                {
                                                    var cdnCandidate480 = new Uri($"http://steamcdn-a.akamaihd.net/steam/apps/{movie.Id}/movie480.mp4");
                                                    var req480 = (HttpWebRequest)WebRequest.Create(cdnCandidate480);
                                                    req480.Method = "HEAD";
                                                    req480.Timeout = 1500;
                                                    using (var resp480 = (HttpWebResponse)req480.GetResponse())
                                                    {
                                                        if (resp480.StatusCode == HttpStatusCode.OK)
                                                        {
                                                            cdnCandidate = cdnCandidate480;
                                                            reachable = true;
                                                        }
                                                    }
                                                }
                                                catch { }
                                            }
                                        }

                                        if (reachable)
                                        {
                                            result.TrailerVideoPath = cdnCandidate;
                                            break;
                                        }
                                    }

                                    // 3. Fallback to HlsH264 if available
                                    if (result.TrailerVideoPath == null && movie.HlsH264 != null)
                                    {
                                        result.TrailerVideoPath = EnsureMediaElementCompatibleUri(movie.HlsH264);
                                        break;
                                    }
                                }

                                // 4. If HEAD timed out or failed, fallback to first movie CDN candidate
                                if (result.TrailerVideoPath == null && response.data.Movies[0].Id > 0)
                                {
                                    result.TrailerVideoPath = new Uri($"http://steamcdn-a.akamaihd.net/steam/apps/{response.data.Movies[0].Id}/movie_max.mp4");
                                }

                                if (result.TrailerVideoPath != null)
                                {
                                    result.IsTrailerAvailable = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // According to #169, it seems that for some reason the uri redirects to
                        // another page and a html source gets downloaded so it needs to be deleted
                        logger.Error(ex, $"Error deserializing steam appdetails json file in {gameDataPath}");
                        FileSystem.DeleteFile(gameDataPath);
                    }
                }
            }

            if (result.TrailerVideoPath != null && result.MicroVideoPath != null)
            {
                result.MultipleSourcesAvailable = true;
            }

            if (useMicrovideosSource)
            {
                if (result.MicroVideoPath != null)
                {
                    result.SelectedVideoSource = result.MicroVideoPath;
                    result.SelectedVideoType = ActiveVideoType.Microtrailer;
                }
                else if (result.TrailerVideoPath != null && SettingsModel.Settings.FallbackVideoSource)
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
                else if (result.MicroVideoPath != null && SettingsModel.Settings.FallbackVideoSource)
                {
                    result.SelectedVideoSource = result.MicroVideoPath;
                    result.SelectedVideoType = ActiveVideoType.Microtrailer;
                }
            }

            return result;
        }

        private void ApplyVideoSources(ResolvedVideoSources resolved)
        {
            if (resolved == null || resolved.SelectedVideoSource == null)
            {
                ResetPlayerValues();
                ControlVisibility = Visibility.Collapsed;
                SettingsModel.Settings.NewContextVideoAvailable = false;
                return;
            }

            trailerVideoPath = resolved.TrailerVideoPath;
            microVideoPath = resolved.MicroVideoPath;
            multipleSourcesAvailable = resolved.MultipleSourcesAvailable;
            activeVideoType = resolved.SelectedVideoType;

            SettingsModel.Settings.IsTrailerAvailable = resolved.IsTrailerAvailable;
            SettingsModel.Settings.IsMicrotrailerAvailable = resolved.IsMicrotrailerAvailable;
            SettingsModel.Settings.IsAnyVideoAvailable = resolved.IsTrailerAvailable || resolved.IsMicrotrailerAvailable;

            VideoSource = resolved.SelectedVideoSource;

            playingContextChanged();
        }

        public void UpdateGameVideoSources()
        {
            if (currentGame == null)
            {
                return;
            }

            var resolved = ResolveGameVideoSources(currentGame);
            ApplyVideoSources(resolved);
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