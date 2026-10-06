using System;
using System.Collections.Generic;

namespace HowLongToBeat
{
    /// <summary>
    /// Test double that mimics the parts of the real HowLongToBeat extension the bridge talks
    /// to: a static PluginDatabase holding a database object with SetCurrentPlayTime plus the
    /// optional pre-checks (login state, ignore tag, linked data).
    /// </summary>
    public class HowLongToBeat
    {
        public static FakeHowLongToBeatDatabase PluginDatabase { get; } = new FakeHowLongToBeatDatabase();
    }

    /// <summary>Stand-in for a Playnite Game (the bridge only ever passes it back).</summary>
    public class FakeGame
    {
        public Guid Id { get; set; }

        public string Name { get; set; }
    }

    /// <summary>Stand-in for HowLongToBeatDatabase.</summary>
    public class FakeHowLongToBeatDatabase
    {
        public bool LoggedIn { get; set; } = true;
        public bool ThrowOnPush { get; set; }
        public bool PushReturnsFalse { get; set; }
        public bool LinkedDataMissing { get; set; }
        public HashSet<string> Ignored { get; } = new HashSet<string>(StringComparer.Ordinal);

        public int PushCount { get; private set; }
        public bool LastNoPlaying { get; private set; }
        public bool LastIsCompleted { get; private set; }
        public FakeGame LastGame { get; private set; }

        public FakeHowLongToBeatApi HowLongToBeatApi { get; }

        public FakeHowLongToBeatDatabase()
        {
            HowLongToBeatApi = new FakeHowLongToBeatApi(this);
        }

        public bool SetCurrentPlayTime(
            FakeGame game,
            bool noPlaying = false,
            bool isCompleted = false)
        {
            PushCount++;
            LastGame = game;
            LastNoPlaying = noPlaying;
            LastIsCompleted = isCompleted;

            if (ThrowOnPush) throw new InvalidOperationException("simulated HLTB failure");
            return !PushReturnsFalse;
        }

        public bool IsGameIgnoredForPlaytimeSync(FakeGame game) => Ignored.Contains(game.Name);

        public FakeHltbData Get(Guid gameId, bool fallback = true)
            => LinkedDataMissing ? null : new FakeHltbData();
    }

    /// <summary>Stand-in for HowLongToBeatApi.</summary>
    public class FakeHowLongToBeatApi
    {
        private readonly FakeHowLongToBeatDatabase _db;

        public FakeHowLongToBeatApi(FakeHowLongToBeatDatabase db) => _db = db;

        public bool GetIsUserLoggedIn() => _db.LoggedIn;
    }

    /// <summary>Stand-in for GameHowLongToBeat.</summary>
    public class FakeHltbData
    {
        public int HltbId { get; set; } = 12345;
    }

    /// <summary>
    /// Variant whose SetCurrentPlayTime takes a *required* extra parameter and has no
    /// noPlaying flag - used to check the bridge still finds and binds a changed signature
    /// without guessing at the game's playtime.
    /// </summary>
    public class FakeHltbCustomPlugin
    {
        public static FakeCustomDatabase PluginDatabase { get; } = new FakeCustomDatabase();
    }

    public class FakeCustomDatabase
    {
        public int Calls { get; set; }
        public int LastOptions { get; private set; }

        public bool IsGameIgnoredForPlaytimeSync(FakeGame game) => false;

        public FakeHltbData Get(Guid gameId, bool fallback = true) => new FakeHltbData();

        public bool SetCurrentPlayTime(FakeGame game, int listSyncOptions)
        {
            Calls++;
            LastOptions = listSyncOptions;
            return true;
        }
    }
}