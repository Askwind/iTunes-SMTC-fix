using iTunes.SMTC.iTunes.Extensions;
using iTunes.SMTC.iTunes.Model;
using iTunesLib;
using Microsoft.Toolkit.Uwp.Notifications;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Timer = System.Timers.Timer;

namespace iTunes.SMTC.iTunes
{
    public class iTunesController : BaseController
    {
        private bool _isPlaying = false;
        private bool _metadataEmpty = true;

        private iTunesApp _iTunesApp;
        private TrackMetadata _currentTrack;
        private readonly DispatcherQueueController iTunesDispatcherCtrl;
        private readonly DispatcherQueue iTunesDispatcher;

        private DispatcherQueueTimer _timelineTimer;
        private Timer _statusTimer;
        private Timer _delayStartTimer;

        private Uri _artworkUri;

        /// <summary>递增序号，用于丢弃过期的在线封面结果</summary>
        private volatile int _artworkSequence;

        private CancellationTokenSource cts = null;

        public override string Key => "iTunes";
        public override bool IsEnabled => Settings.EnableiTunesController;

        public iTunesController() : base()
        {
            iTunesDispatcherCtrl = DispatcherQueueController.CreateOnDedicatedThread();
            iTunesDispatcher = iTunesDispatcherCtrl.DispatcherQueue;
        }

        protected override string GetNotificationTag() => "iTunes.SMTC";

        public override void Initialize()
        {
            base.Initialize();
            InitializeiTunesController();
        }

        public override void Destroy()
        {
            base.Destroy();
            RemoveEvents();
            DisconnectiTunes(wasAlive: true, stop: true);
        }

        private void InitializeiTunesController()
        {
            _delayStartTimer = new Timer()
            {
                AutoReset = false,
                Interval = 35000
            };

            _timelineTimer = iTunesDispatcher.CreateTimer();
            _timelineTimer.Interval = TimeSpan.FromSeconds(1);
            _timelineTimer.Tick += (s, e) =>
            {
                UpdateSMTCTimeline(_currentTrack);

                if (!_isPlaying)
                {
                    s.Stop();
                }
            };
            _timelineTimer.IsRepeating = true;

            _statusTimer = new Timer()
            {
                AutoReset = true,
                Interval = 1000
            };
            _statusTimer.Elapsed += (s, e) =>
            {
                _statusTimer.Stop();

                try
                {
                    // Check if iTunes is currently running
                    if (IsiTunesRunning())
                    {
                        // If running, check connection status
                        if (_iTunesApp == null && !_delayStartTimer.Enabled)
                        {
                            InitializeiTunes();
                        }

                        // Update SMTC display
                        // NOTE: Needed as the iTunes OnPlayerPlayEvent does not always fire
                        iTunesDispatcher.TryEnqueue(() =>
                        {
                            try
                            {
                                var playerState = _iTunesApp?.PlayerState ?? ITPlayerState.ITPlayerStateStopped;
                                var currentTrack = _iTunesApp?.CurrentTrack?.GetMetadata();

                                var isPlaying = currentTrack != null && playerState == ITPlayerState.ITPlayerStatePlaying;

                                if (currentTrack == null)
                                {
                                    _isPlaying = false;
                                    // Only update if track is N/A and metadata is populated
                                    if (!_metadataEmpty)
                                    {
                                        UpdateSMTCDisplay(null);
                                    }
                                }
                                else if (_currentTrack == null || _currentTrack.DatabaseID != currentTrack?.DatabaseID)
                                {
                                    _isPlaying = isPlaying;
                                    var artworkSeq = SaveArtwork(currentTrack, out var comArtwork);
                                    UpdateSMTCDisplay(currentTrack);
                                    _ = RefreshArtworkAsync(artworkSeq, currentTrack, comArtwork);

                                    if (_isPlaying && Settings.ShowTrackToast)
                                    {
                                        ShowToastNotification(currentTrack);
                                    }
                                }
                                else if (_isPlaying != isPlaying)
                                {
                                    _isPlaying = isPlaying;
                                    UpdateSMTCPlaybackState(currentTrack);
                                }

                                _currentTrack = currentTrack;

                                if (_isPlaying && _timelineTimer?.IsRunning != true)
                                {
                                    _timelineTimer?.Start();
                                }
                            }
                            catch (Exception)
                            {
                                // no-op
                            }
                        });
                    }
                    else
                    {
                        // Stop timer once process has ended
                        _delayStartTimer?.Stop();

                        if (_iTunesApp != null)
                        {
                            DisconnectiTunes();
                        }
                    }

                    _statusTimer.Start();
                }
                catch (Exception)
                {
                    _statusTimer.Start();
                }
            };
            _statusTimer.Start();
        }

        private void InitializeiTunes()
        {
            try
            {
                Trace.WriteLine("Connecting to iTunes...");

                _iTunesApp = new iTunesAppClass();

                IntializeEvents();
                InitializeControls(_iTunesApp?.CurrentTrack?.GetMetadata());
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
            }
        }

        /// <summary>
        /// Writes artwork to the shared artwork file.
        /// <paramref name="usableArtwork"/> reports whether COM actually produced a real cover;
        /// catalog tracks can hand back an artwork object that writes nothing, or writes
        /// iTunes' own grey placeholder, in which case the caller must run the online fallback.
        /// </summary>
        private int SaveArtwork(TrackMetadata track, out bool usableArtwork)
        {
            var sequence = ++_artworkSequence;
            usableArtwork = false;

            if (_artworkUri == null)
            {
#if UNPACKAGEDDEBUG || UNPACKAGEDRELEASE
                var BasePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
                var AppPath = Path.Combine(BasePath, "iTunes.SMTC");
                Directory.CreateDirectory(AppPath);
                var FilePath = Path.Combine(AppPath, "artwork.img");
                _artworkUri = new Uri(FilePath);
#else
                var BaseFolder = ApplicationData.Current.LocalCacheFolder;
                var ArtworkFolderPath = Path.Combine(BaseFolder.Path, "Artwork");
                Directory.CreateDirectory(ArtworkFolderPath);
                var ArtworkFilePath = Path.Combine(ArtworkFolderPath, "artwork.img");
                _artworkUri = new Uri(ArtworkFilePath);
#endif
            }

            var artworkPath = _artworkUri.LocalPath;

            try
            {
                if (track?.Artwork != null)
                {
                    // Delete first so a failed save cannot leave the previous cover behind
                    try
                    {
                        if (File.Exists(artworkPath))
                        {
                            File.Delete(artworkPath);
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        track.Artwork.SaveArtworkToFile(artworkPath);
                        usableArtwork = IsUsableArtwork(artworkPath);
                    }
                    catch (Exception ex)
                    {
                        SentrySdk.CaptureException(ex);
                        usableArtwork = false;
                    }
                }

                if (!usableArtwork)
                {
                    // Replace with the empty placeholder so the flyout never keeps showing
                    // the previous track's cover while the online lookup is in flight
                    Properties.Resources.no_artwork.Save(artworkPath);
                }
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                usableArtwork = false;
            }

            ArtworkFallback.Log(string.Format(
                "track name='{0}' artist='{1}' album='{2}' comArtworkObject={3} comArtworkUsable={4} fileBytes={5}",
                track?.Name, track?.Artist, track?.Album, track?.Artwork != null, usableArtwork, SafeFileLength(artworkPath)));

            return sequence;
        }

        private static long SafeFileLength(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : -1;
            }
            catch
            {
                return -1;
            }
        }

        private static byte[] _placeholderBytes;

        private static byte[] GetPlaceholderBytes()
        {
            if (_placeholderBytes == null)
            {
                try
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "smtc_no_artwork_" + Guid.NewGuid().ToString("N") + ".png");
                    Properties.Resources.no_artwork.Save(tmp);
                    _placeholderBytes = File.ReadAllBytes(tmp);
                    File.Delete(tmp);
                }
                catch
                {
                    _placeholderBytes = new byte[0];
                }
            }

            return _placeholderBytes;
        }

        /// <summary>
        /// True when the file on disk is a real cover: present, not trivially small, and not
        /// byte-identical to the built-in placeholder that iTunes itself sometimes emits.
        /// </summary>
        private static bool IsUsableArtwork(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 1024)
                {
                    return false;
                }

                var placeholder = GetPlaceholderBytes();
                if (placeholder.Length == info.Length)
                {
                    return !File.ReadAllBytes(path).SequenceEqual(placeholder);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Fallback for Apple Music streaming tracks: iTunes only returns an empty
        /// artwork collection over COM, so match the cover online and refresh the display.
        /// </summary>
        private async Task RefreshArtworkAsync(int sequence, TrackMetadata track, bool comArtworkUsable)
        {
            try
            {
                if (comArtworkUsable)
                {
                    return;
                }

                var fallbackBytes = await ArtworkFallback.TryGetArtworkBytesAsync(track?.Artist, track?.Album, track?.Name);

                if (fallbackBytes == null || fallbackBytes.Length == 0)
                {
                    ArtworkFallback.Log("fallback: no match for '" + track?.Name + "'");
                    return;
                }

                ArtworkFallback.Log("fallback: got " + fallbackBytes.Length + " bytes for '" + track?.Name + "'");

                // Track changed while the request was in flight: drop the stale result
                if (_artworkSequence != sequence)
                {
                    return;
                }

                // Hop back onto the dispatcher: UpdateSMTCDisplay touches the SMTC
                // objects that the rest of the controller only ever touches there.
                iTunesDispatcher.TryEnqueue(() =>
                {
                    if (_artworkSequence != sequence)
                    {
                        return;
                    }

                    try
                    {
                        File.WriteAllBytes(_artworkUri.LocalPath, fallbackBytes);
                        UpdateSMTCDisplay(track);
                    }
                    catch (Exception ex)
                    {
                        SentrySdk.CaptureException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
            }
        }

        public override void OnSystemControlsButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                switch (args.Button)
                {
                    case SystemMediaTransportControlsButton.Play:
                        _iTunesApp?.PlayOrResume();
                        break;
                    case SystemMediaTransportControlsButton.Pause:
                        _iTunesApp?.PauseOrResume();
                        break;
                    case SystemMediaTransportControlsButton.Stop:
                        _iTunesApp?.Stop();
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        _iTunesApp?.BackTrack();
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        _iTunesApp?.NextTrack();
                        break;
                    case SystemMediaTransportControlsButton.Rewind:
                        _iTunesApp?.Rewind();
                        break;
                    case SystemMediaTransportControlsButton.FastForward:
                        _iTunesApp?.FastForward();
                        break;
                }
            });
        }

        public override void OnSystemControlsShuffleEnabledChangeRequested(SystemMediaTransportControls sender, ShuffleEnabledChangeRequestedEventArgs args)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                if (_iTunesApp != null)
                {
                    var currentPlaylist = _iTunesApp.CurrentPlaylist;

                    if (currentPlaylist != null && _iTunesApp.CanSetShuffle[currentPlaylist])
                    {
                        currentPlaylist.Shuffle = args.RequestedShuffleEnabled;
                        sender.ShuffleEnabled = args.RequestedShuffleEnabled;
                    }
                    else
                    {
                        sender.ShuffleEnabled = false;
                    }
                }
            });
        }

        public override void OnSystemControlsAutoRepeatModeChangeRequested(SystemMediaTransportControls sender, AutoRepeatModeChangeRequestedEventArgs args)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                if (_iTunesApp != null)
                {
                    var currentPlaylist = _iTunesApp.CurrentPlaylist;

                    if (currentPlaylist != null && _iTunesApp.CanSetSongRepeat[currentPlaylist])
                    {
                        switch (args.RequestedAutoRepeatMode)
                        {
                            case MediaPlaybackAutoRepeatMode.None:
                                currentPlaylist.SongRepeat = ITPlaylistRepeatMode.ITPlaylistRepeatModeOff;
                                break;
                            case MediaPlaybackAutoRepeatMode.Track:
                                currentPlaylist.SongRepeat = ITPlaylistRepeatMode.ITPlaylistRepeatModeOne;
                                break;
                            case MediaPlaybackAutoRepeatMode.List:
                                currentPlaylist.SongRepeat = ITPlaylistRepeatMode.ITPlaylistRepeatModeAll;
                                break;
                        }

                        sender.AutoRepeatMode = args.RequestedAutoRepeatMode;
                    }
                    else
                    {
                        sender.AutoRepeatMode = MediaPlaybackAutoRepeatMode.None;
                    }
                }
            });
        }

        public override void OnSystemControlsPlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                if (_iTunesApp != null)
                {
                    _iTunesApp.PlayerPosition = (int)args.RequestedPlaybackPosition.TotalSeconds;
                }
            });
        }

        private void IntializeEvents()
        {
            if (_iTunesApp != null)
            {
                _iTunesApp.OnPlayerPlayingTrackChangedEvent += _iTunesApp_OnPlayerPlayingTrackChangedEvent;
                _iTunesApp.OnPlayerPlayEvent += _iTunesApp_OnPlayerPlayEvent;
                _iTunesApp.OnPlayerStopEvent += _iTunesApp_OnPlayerStopEvent;
                _iTunesApp.OnQuittingEvent += _iTunesApp_OnQuittingEvent;
                _iTunesApp.OnAboutToPromptUserToQuitEvent += _iTunesApp_OnAboutToPromptUserToQuitEvent;
            }
        }

        private void RemoveEvents()
        {
            if (_iTunesApp != null)
            {
                try
                {
                    _iTunesApp.OnPlayerPlayingTrackChangedEvent -= _iTunesApp_OnPlayerPlayingTrackChangedEvent;
                    _iTunesApp.OnPlayerPlayEvent -= _iTunesApp_OnPlayerPlayEvent;
                    _iTunesApp.OnPlayerStopEvent -= _iTunesApp_OnPlayerStopEvent;
                    _iTunesApp.OnQuittingEvent -= _iTunesApp_OnQuittingEvent;
                    _iTunesApp.OnAboutToPromptUserToQuitEvent -= _iTunesApp_OnAboutToPromptUserToQuitEvent;
                }
                catch (Exception)
                {
                    // Ignore. iTunes may be in the process of ending
                }
            }
        }

        private void _iTunesApp_OnAboutToPromptUserToQuitEvent()
        {
            // Disconnect before user is prompted, to avoid 'scripting' popup
            _statusTimer?.Stop();
            _timelineTimer?.Stop();
            _delayStartTimer?.Stop();

            RemoveEvents();

            if (_iTunesApp != null)
            {
                Trace.WriteLine("Releasing iTunes COM object...");
                Marshal.FinalReleaseComObject(_iTunesApp);
                _iTunesApp = null;
            }

            GC.Collect();

            DisconnectiTunes(true);
        }

        private void _iTunesApp_OnQuittingEvent()
        {
            _statusTimer?.Stop();
            _timelineTimer?.Stop();
            _delayStartTimer?.Stop();

            RemoveEvents();
            DisconnectiTunes(true);
        }

        private void DisconnectiTunes(bool wasAlive = false, bool stop = false)
        {
            Trace.WriteLine("Disconnecting...");

            _statusTimer?.Stop();
            _timelineTimer?.Stop();
            _delayStartTimer?.Stop();

            if (_currentTrack != null)
            {
                Trace.WriteLine("Releasing Track COM object...");
                _currentTrack?.Dispose();
            }
            _currentTrack = null;

            if (_iTunesApp != null)
            {
                Trace.WriteLine("Releasing iTunes COM object...");
                wasAlive = true;

                RemoveEvents();
                Marshal.FinalReleaseComObject(_iTunesApp);
                _iTunesApp = null;
            }

            GC.Collect();

            _isPlaying = false;

            if (_systemMediaTransportControls != null)
            {
                _systemMediaTransportControls.IsEnabled = false;
            }

            if (!stop)
            {
                Task.Run(() =>
                {
                    // Give process time to end
                    Trace.WriteLine("Starting disconnect delay...");
                    Thread.Sleep(TimeSpan.FromSeconds(15));
                    Trace.WriteLine("Ended disconnect delay...");

                    if (wasAlive)
                    {
                        // NOTE: if the scripting popup appears, iTunes will close in 30s
                        // Therefore the process may still be alive after disconnecting
                        // To prevent restarting iTunes after disconnecting (since the process is still alive, delay the status timer
                        // from starting by waiting ~30s; We'll stop timer once time has expired or iTunes process ended
                        _delayStartTimer?.Start();
                    }

                    _statusTimer?.Start();
                });
            }
        }

        private void InitializeControls(TrackMetadata currentTrack)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                _statusTimer?.Stop();

                var playerState = _iTunesApp?.PlayerState ?? ITPlayerState.ITPlayerStateStopped;

                _isPlaying = currentTrack != null && playerState == ITPlayerState.ITPlayerStatePlaying;

                var artworkSeq = SaveArtwork(currentTrack, out var comArtwork);
                UpdateSMTCDisplay(currentTrack);
                _ = RefreshArtworkAsync(artworkSeq, currentTrack, comArtwork);
                UpdateSMTCTimeline(currentTrack);

                _currentTrack = currentTrack;

                _statusTimer?.Start();

                if (_isPlaying)
                {
                    _timelineTimer?.Start();
                }
            });
        }

        private void _iTunesApp_OnPlayerStopEvent(object iTrack)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                _statusTimer?.Stop();
                _timelineTimer?.Stop();

                var isPlaying = false;

                var track = GetCurrentTrack(iTrack)?.GetMetadata();

                if (_currentTrack == null || _currentTrack.DatabaseID != track?.DatabaseID)
                {
                    _isPlaying = isPlaying;
                    var artworkSeq = SaveArtwork(track, out var comArtwork);
                    UpdateSMTCDisplay(track);
                    _ = RefreshArtworkAsync(artworkSeq, track, comArtwork);
                }
                else if (_isPlaying != isPlaying)
                {
                    _isPlaying = isPlaying;
                    UpdateSMTCPlaybackState(track);
                }

                _currentTrack = track;

                _statusTimer?.Start();
            });
        }

        private void _iTunesApp_OnPlayerPlayEvent(object iTrack)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                _statusTimer?.Stop();
                _timelineTimer?.Stop();

                var isPlaying = true;

                var track = GetCurrentTrack(iTrack)?.GetMetadata();

                if (_currentTrack == null || _currentTrack.DatabaseID != track?.DatabaseID)
                {
                    _isPlaying = isPlaying;
                    var artworkSeq = SaveArtwork(track, out var comArtwork);
                    UpdateSMTCDisplay(track);
                    _ = RefreshArtworkAsync(artworkSeq, track, comArtwork);
                }
                else if (_isPlaying != isPlaying)
                {
                    _isPlaying = isPlaying;
                    UpdateSMTCPlaybackState(track);
                }

                if (Settings.ShowTrackToast)
                {
                    ShowToastNotification(track);
                }

                _currentTrack = track;

                _statusTimer?.Start();
                _timelineTimer?.Start();
            });
        }

        private void _iTunesApp_OnPlayerPlayingTrackChangedEvent(object iTrack)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                _statusTimer?.Stop();

                var currentTrack = GetCurrentTrack(iTrack)?.GetMetadata();
                var artworkSeq = SaveArtwork(currentTrack, out var comArtwork);
                UpdateSMTCDisplay(currentTrack);
                _ = RefreshArtworkAsync(artworkSeq, currentTrack, comArtwork);
                _currentTrack = currentTrack;

                _statusTimer?.Start();
            });
        }

        private IITTrack GetCurrentTrack(object iTrack)
        {
            IITTrack currentTrack;

            if (iTrack is IITTrack track)
            {
                currentTrack = track;
            }
            else
            {
                currentTrack = _iTunesApp?.CurrentTrack;
            }

            return currentTrack;
        }

        private void UpdateSMTCDisplay(TrackMetadata currentTrack)
        {
            iTunesDispatcher.TryEnqueue(async () =>
            {
                var playerState = _iTunesApp?.PlayerState ?? ITPlayerState.ITPlayerStateStopped;

                switch (playerState)
                {
                    case ITPlayerState.ITPlayerStateStopped:
                        _systemMediaTransportControls.PlaybackStatus = currentTrack != null ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Stopped;
                        break;
                    case ITPlayerState.ITPlayerStatePlaying:
                        _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                        break;
                    case ITPlayerState.ITPlayerStateFastForward:
                    case ITPlayerState.ITPlayerStateRewind:
                        _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                        break;
                }

                _systemMediaTransportControls.IsEnabled = currentTrack != null;

                _systemMediaTransportControls.ShuffleEnabled = _iTunesApp?.CurrentPlaylist?.Shuffle ?? false;
                _systemMediaTransportControls.AutoRepeatMode = GetRepeatModeFromITunes(_iTunesApp?.CurrentPlaylist?.SongRepeat) ?? MediaPlaybackAutoRepeatMode.None;

                SystemMediaTransportControlsDisplayUpdater updater = _systemMediaTransportControls.DisplayUpdater;
                updater.ClearAll();

                if (currentTrack != null)
                {
                    updater.Type = MediaPlaybackType.Music;
                    updater.AppMediaId = currentTrack?.DatabaseID.ToString();
                    updater.MusicProperties.Artist = currentTrack?.Artist;
                    updater.MusicProperties.AlbumTitle = currentTrack?.Album;
                    updater.MusicProperties.Title = currentTrack?.Name;
                    updater.MusicProperties.TrackNumber = currentTrack?.TrackNumber ?? 0;

                    try
                    {
                        updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(_artworkUri.LocalPath));
                    }
                    catch (Exception ex)
                    {
                        SentrySdk.CaptureException(ex);
                    }

                    _metadataEmpty = false;
                }
                else
                {
                    _metadataEmpty = true;
                }

                updater.Update();
            });
        }

        private void UpdateSMTCPlaybackState(TrackMetadata currentTrack)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                var playerState = _iTunesApp?.PlayerState ?? ITPlayerState.ITPlayerStateStopped;

                switch (playerState)
                {
                    case ITPlayerState.ITPlayerStateStopped:
                        _systemMediaTransportControls.PlaybackStatus = currentTrack != null ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Stopped;
                        break;
                    case ITPlayerState.ITPlayerStatePlaying:
                        _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                        break;
                    case ITPlayerState.ITPlayerStateFastForward:
                    case ITPlayerState.ITPlayerStateRewind:
                        _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                        break;
                }
            });
        }

        private void UpdateSMTCTimeline(TrackMetadata currentTrack)
        {
            iTunesDispatcher.TryEnqueue(() =>
            {
                // Update timeline
                var timelineProperties = new SystemMediaTransportControlsTimelineProperties();

                if (currentTrack != null)
                {
                    timelineProperties.StartTime = TimeSpan.FromSeconds(currentTrack.StartTime);
                    timelineProperties.EndTime = TimeSpan.FromSeconds(currentTrack.EndTime);
                    timelineProperties.Position = TimeSpan.FromSeconds(_iTunesApp?.PlayerPosition ?? 0);

                    timelineProperties.MinSeekTime = TimeSpan.FromSeconds(currentTrack.StartTime);
                    timelineProperties.MaxSeekTime = TimeSpan.FromSeconds(currentTrack.EndTime);
                }

                _systemMediaTransportControls.UpdateTimelineProperties(timelineProperties);
            });
        }

        private static MediaPlaybackAutoRepeatMode? GetRepeatModeFromITunes(ITPlaylistRepeatMode? repeatMode)
        {
            return repeatMode switch
            {
                ITPlaylistRepeatMode.ITPlaylistRepeatModeOff => MediaPlaybackAutoRepeatMode.None,
                ITPlaylistRepeatMode.ITPlaylistRepeatModeOne => MediaPlaybackAutoRepeatMode.Track,
                ITPlaylistRepeatMode.ITPlaylistRepeatModeAll => MediaPlaybackAutoRepeatMode.List,
                _ => null,
            };
        }

        private static bool IsiTunesRunning()
        {
            Process[] iTunesProcesses = Process.GetProcessesByName("iTunes");

            return iTunesProcesses.Length > 0;
        }

        private void ResetToken()
        {
            cts?.Cancel();
            cts = new CancellationTokenSource();
        }

        private void ShowToastNotification(TrackMetadata track)
        {
            if (track != null)
            {
                ResetToken();

                iTunesDispatcher.TryEnqueue(() =>
                {
                    var notifTag = GetNotificationTag();

                    try
                    {
                        ToastNotificationManagerCompat.History.Remove(notifTag);
                    }
                    catch { }

                    new ToastContentBuilder()
                        .AddText(track.Name, AdaptiveTextStyle.Base, hintMaxLines: 1)
                        .AddText(track.Artist, AdaptiveTextStyle.Body, hintMaxLines: 1)
                        .AddText(track.Album, AdaptiveTextStyle.Body, hintMaxLines: 1)
                        .AddAppLogoOverride(_artworkUri)
                        .AddAudio(null, silent: true) // Disable sound
                        .Show(async (t) =>
                        {
                            t.ExpirationTime = DateTimeOffset.Now.AddSeconds(5);
                            t.Tag = notifTag;

                            try
                            {
                                await Task.Delay(5250, cts.Token);

                                ToastNotificationManagerCompat.History.Remove(t.Tag);
                            }
                            catch { }
                        });
                });
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose managed state (managed objects)
                iTunesDispatcherCtrl.ShutdownQueueAsync();

                Destroy();
                _delayStartTimer?.Stop();
                _statusTimer?.Stop();

                _delayStartTimer?.Dispose();
                _statusTimer?.Dispose();

                _currentTrack?.Dispose();
            }

            // Free unmanaged resources (unmanaged objects) and override finalizer
            if (_iTunesApp != null)
            {
                Marshal.FinalReleaseComObject(_iTunesApp);
            }
            // Set large fields to null
            _iTunesApp = null;

            base.Dispose(disposing);
        }

        // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        ~iTunesController()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }
    }
}
