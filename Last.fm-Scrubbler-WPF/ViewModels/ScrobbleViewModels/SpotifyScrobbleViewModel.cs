﻿using IF.Lastfm.Core.Objects;
using Last.fm_Scrubbler_WPF.Properties;
using Last.fm_Scrubbler_WPF.Views.ScrobbleViews;
using SpotifyAPI.Local;
using SpotifyAPI.Local.Models;
using System;
using System.Threading.Tasks;
using System.Timers;

namespace Last.fm_Scrubbler_WPF.ViewModels.ScrobbleViewModels
{
  /// <summary>
  /// ViewModel for the Spotify <see cref="MediaPlayerScrobbleControl"/>
  /// </summary>
  class SpotifyScrobbleViewModel : MediaPlayerScrobbleViewModelBase
  {
    #region Properties

    /// <summary>
    /// When true, tries to connect to Spotify on startup.
    /// </summary>
    public override bool AutoConnect
    {
      get { return Settings.Default.SpotifyAutoConnect; }
      set
      {
        Settings.Default.SpotifyAutoConnect = value;
        Settings.Default.Save();
        NotifyOfPropertyChange(() => AutoConnect);
      }
    }

    /// <summary>
    /// The name of the current playing track.
    /// </summary>
    public override string CurrentTrackName => _currentResponse?.Track?.TrackResource?.Name;

    /// <summary>
    /// The name of the current artist.
    /// </summary>
    public override string CurrentArtistName => _currentResponse?.Track?.ArtistResource?.Name;

    /// <summary>
    /// The name of the current album.
    /// </summary>
    public override string CurrentAlbumName => _currentResponse?.Track?.AlbumResource?.Name;

    /// <summary>
    /// The length of the current track.
    /// </summary>
    public override int CurrentTrackLength => ((_currentResponse == null ||_currentResponse.Track == null) ? 0 : _currentResponse.Track.Length);

    #endregion Properties

    #region Member

    /// <summary>
    /// Connection to the local Spotify client.
    /// </summary>
    private SpotifyLocalAPI _spotify;

    /// <summary>
    /// Info about the current Spotify status.
    /// </summary>
    private StatusResponse _currentResponse;

    /// <summary>
    /// Timer counting the played seconds.
    /// </summary>
    private Timer _counterTimer;

    /// <summary>
    /// Timer updating the <see cref="_currentResponse"/>.
    /// </summary>
    private Timer _refreshTimer;

    /// <summary>
    /// Uri of the last played track.
    /// </summary>
    private string _lastTrack;

    #endregion Member

    /// <summary>
    /// Constructor.
    /// </summary>
    public SpotifyScrobbleViewModel()
    {
      PercentageToScrobble = 0.5;
      _spotify = new SpotifyLocalAPI();
      _counterTimer = new Timer(1000);
      _counterTimer.Elapsed += _counterTimer_Elapsed;
      _refreshTimer = new Timer(1000);
      _refreshTimer.Elapsed += _refreshTimer_Elapsed;

      if (AutoConnect)
        Connect();
    }

    /// <summary>
    /// Connects to the Spotify client.
    /// </summary>
    public override void Connect()
    {
      try
      {
        EnableControls = false;

        if (IsConnected)
          Disconnect();

        SpotifyLocalAPI.RunSpotify();
        SpotifyLocalAPI.RunSpotifyWebHelper();

        if (!SpotifyLocalAPI.IsSpotifyRunning())
        {
          OnStatusUpdated("Error connecting to Spotify: Client not running");
          return;
        }
        if (!SpotifyLocalAPI.IsSpotifyWebHelperRunning())
        {
          OnStatusUpdated("Error connecting to Spotify: WebHelper not running");
          return;
        }

        if (!_spotify.Connect())
        {
          OnStatusUpdated("Error connecting to Spotify: Unknown error");
        }
        else
        {
          ConnectEvents();
          _currentResponse = _spotify.GetStatus();
          UpdateCurrentTrackInfo();

          _refreshTimer.Start();

          if (_currentResponse.Playing)
            _counterTimer.Start();
          else
            _counterTimer.Stop();

          IsConnected = true;
        }
      }
      catch (Exception ex)
      {
        OnStatusUpdated("Fatal error connecting to Spotify: " + ex.Message);
      }
      finally
      {
        EnableControls = true;
      }
    }

    /// <summary>
    /// Disconnects from the Spotify client.
    /// </summary>
    public override void Disconnect()
    {
      _counterTimer.Stop();
      _refreshTimer.Stop();
      DisconnectEvents();
      _spotify.Dispose();
      _currentResponse = null;
      IsConnected = false;
      UpdateCurrentTrackInfo();
    }

    /// <summary>
    /// Connects the necessary Spotify events.
    /// </summary>
    private void ConnectEvents()
    {
      _spotify.OnPlayStateChange += _spotify_OnPlayStateChange;
      _spotify.ListenForEvents = true;
    }

    /// <summary>
    /// Disconnects the Spotify events.
    /// </summary>
    private void DisconnectEvents()
    {
      _spotify.ListenForEvents = false;
      _spotify.OnPlayStateChange -= _spotify_OnPlayStateChange;
    }

    /// <summary>
    /// Starts or stops the <see cref="_counterTimer"/> depending
    /// on the Spotify play state.
    /// </summary>
    /// <param name="sender">Ignored.</param>
    /// <param name="e">EventArgs containing the current play state.</param>
    private void _spotify_OnPlayStateChange(object sender, PlayStateEventArgs e)
    {
      if (e.Playing)
        _counterTimer.Start();
      else
        _counterTimer.Stop();
    }

    /// <summary>
    /// Counts the listened seconds and scrobbles when
    /// the user listened long enough.
    /// </summary>
    /// <param name="sender">Ignored.</param>
    /// <param name="e">Ignored.</param>
    private void _counterTimer_Elapsed(object sender, ElapsedEventArgs e)
    {
      CountedSeconds++;

      if (CountedSeconds == CurrentTrackLengthToScrobble)
        Scrobble();
    }

    /// <summary>
    /// Updates the Spotify info.
    /// Disconnects if we can't a track.
    /// </summary>
    /// <param name="sender">Ignored.</param>
    /// <param name="e">Ignored.</param>
    private void _refreshTimer_Elapsed(object sender, ElapsedEventArgs e)
    {
      _lastTrack = _currentResponse?.Track?.TrackResource.Uri;
      _currentResponse = _spotify.GetStatus();

      if (_currentResponse.Track == null)
      {
        Disconnect();
        return;
      }

      if (_lastTrack != _currentResponse.Track.TrackResource.Uri)
      {
        CountedSeconds = 0;
        CurrentTrackScrobbled = false;
        UpdateCurrentTrackInfo();
      }
    }

    /// <summary>
    /// Notifies the ui of new track info.
    /// </summary>
    protected override void UpdateCurrentTrackInfo()
    {
      base.UpdateCurrentTrackInfo();
      FetchAlbumArtwork();
    }

    /// <summary>
    /// Scrobbles the currently playing track.
    /// </summary>
    /// <returns>Task.</returns>
    public override async Task Scrobble()
    {
      if (CanScrobble && !CurrentTrackScrobbled && !_currentResponse.Track.IsAd())
      {
        EnableControls = false;

        try
        {
          OnStatusUpdated("Trying to scrobble currently playing track...");

          Scrobble s = new Scrobble(CurrentArtistName, CurrentAlbumName, CurrentTrackName, DateTime.Now)
          {
            Duration = TimeSpan.FromSeconds(CurrentTrackLength),
          };

          var response = await MainViewModel.Scrobbler.ScrobbleAsync(s);
          if (response.Success)
          {
            OnStatusUpdated(string.Format("Successfully scrobbled {0}!", CurrentTrackName));
            CurrentTrackScrobbled = true;
          }
          else
            OnStatusUpdated("Error while scrobbling!");
        }
        catch (Exception ex)
        {
          OnStatusUpdated("Fatal error while trying to scrobble currently playing track. Error: " + ex.Message);
        }
        finally
        {
          EnableControls = true;
        }
      }
    }
  }
}