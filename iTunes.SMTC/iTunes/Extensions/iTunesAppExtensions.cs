using iTunesLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using iTunes.SMTC.iTunes.Model;

namespace iTunes.SMTC.iTunes.Extensions
{
    public static class iTunesAppExtensions
    {
        public static TrackMetadata GetMetadata(this IITTrack track)
        {
            var metadata = new TrackMetadata();

            // Every member is read in isolation on purpose. Apple Music catalog tracks
            // (the "For You" / "Browse" tabs) can throw on individual properties, and the
            // original single try/catch meant one bad member wiped out everything that
            // followed it - including Artist and Artwork. The online fallback then had no
            // artist to search with and silently gave up.
            metadata.DatabaseID = Read(() => track.TrackDatabaseID, 0);
            metadata.Artist = Read(() => track.Artist, null);
            metadata.Album = Read(() => track.Album, null);
            metadata.Name = Read(() => track.Name, null);
            metadata.TrackNumber = Read(() => (uint)track.TrackNumber, 0u);

            metadata.StartTime = Read(() => track.Start, 0);
            metadata.EndTime = Read(() => track.Finish, 0);
            metadata.Duration = Read(() => track.Duration, 0);

            metadata.Artwork = Read(() => track.Artwork?.Cast<IITArtwork>()?.FirstOrDefault(), null);

            return metadata;
        }

        /// <summary>
        /// Reads a single COM member, swallowing failures so that one bad property does not
        /// discard the rest of the metadata.
        /// </summary>
        private static T Read<T>(Func<T> getter, T fallback)
        {
            try
            {
                return getter();
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                return fallback;
            }
        }

        public static void PlayOrResume(this iTunesApp _iTunesApp)
        {
            var playerState = _iTunesApp?.PlayerState ?? ITPlayerState.ITPlayerStateStopped;

            if (playerState == ITPlayerState.ITPlayerStateRewind || playerState == ITPlayerState.ITPlayerStateFastForward)
            {
                _iTunesApp?.Resume();
            }
            else
            {
                _iTunesApp?.Play();
            }
        }

        public static void PauseOrResume(this iTunesApp _iTunesApp)
        {
            var playerState = _iTunesApp?.PlayerState ?? ITPlayerState.ITPlayerStateStopped;

            if (playerState == ITPlayerState.ITPlayerStateRewind || playerState == ITPlayerState.ITPlayerStateFastForward)
            {
                _iTunesApp?.Resume();
            }
            else
            {
                _iTunesApp?.Pause();
            }
        }
    }
}
