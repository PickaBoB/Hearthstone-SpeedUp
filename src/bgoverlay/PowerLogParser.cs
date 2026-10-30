using System.Text.RegularExpressions;

namespace BgOverlay;

public enum ChoiceKind { Hero, LesserTrinket, GreaterTrinket }

public sealed record ChoiceOption(int EntityId, string CardId, string Name);

public sealed record PendingChoice(int Id, ChoiceKind Kind, IReadOnlyList<ChoiceOption> Options);

public sealed record OutcomeMinion(string CardId, string Name, int Attack, int Health, string Marks);

public sealed record SeenMinion(string CardId, string Name, int Attack, int Health,
    bool Taunt, bool DivineShield, bool Poisonous, bool Reborn, bool Windfury);

/// <summary>
/// Another player's board as it last took the field against us, which is as
/// much as the client is ever told about it.
/// </summary>
public sealed record SeenBoard(
    int PlayerId,
    int Place,
    bool Friendly,
    string HeroCardId,
    string HeroName,
    int Turn,
    int Hp,
    int TechLevel,
    IReadOnlyList<SeenMinion> Board)
{
    public int Stats => Board.Sum(m => m.Attack + Math.Max(0, m.Health));
}

/// <summary>A finished Battlegrounds game, as the overlay lists it later.</summary>
public sealed record GameOutcome(
    int GameIndex,
    TimeSpan At,
    bool IsDuo,
    string HeroCardId,
    string HeroName,
    int Place,
    int Turn,
    IReadOnlyList<int> Tribes,
    IReadOnlyList<OutcomeMinion> Board)
{
    public int BoardStats => Board.Sum(m => m.Attack + Math.Max(0, m.Health));
}

/// <summary>
/// A player in the lobby, in the order the game's leaderboard puts them:
/// by place, best first. The board is there once we have fought them.
/// </summary>
public sealed record LobbyPlayer(int Place, int PlayerId, string Hero, string HeroCard,
    bool Friendly, bool Alive, int Tier, SeenBoard? Board);

public sealed record GameSnapshot(
    bool InBattlegrounds,
    bool IsDuo,
    IReadOnlyList<(int Tribe, int Seen)> TribeCounts,
    IReadOnlyList<int> Tribes,
    bool TribesConfident,
    PendingChoice? Choice,
    IReadOnlyList<SeenBoard>? Seen = null,
    IReadOnlyList<LobbyPlayer>? Lobby = null,
    string? MyHeroCardId = null,
    string? MyHeroName = null,
    int Turn = 0,
    bool InCombat = false,
    int BoardStats = 0,
    int BoardMinions = 0,
    CombatInfo? Combat = null,
    bool CombatWaiting = false)
{
    public static readonly GameSnapshot Empty = new(false, false, [], [], false, null, null, null);
}

/// <summary>
/// Rebuilds the bits of Battlegrounds state the overlay needs from the
/// GameState.* lines of Power.log. PowerTaskList.* repeats the same data later
/// for animation and is skipped. A session log holds every game of the client
/// run; CREATE_GAME starts over.
/// </summary>
public sealed class PowerLogParser
{
    private sealed class Entity
    {
        public string  CardId = "";
        public string? Name;
        public string? CardType;
        public int     Controller;
        public string? Zone;
        public int     ZonePos;
        public int     Atk;
        public int     Health;
        public int     PlayerId;
        public Dictionary<string, int>? Tags;

        public int Tag(string name) => Tags is not null && Tags.TryGetValue(name, out int v) ? v : 0;
    }

    // Numeric tags kept per entity: only what a combat board needs.
    private static readonly HashSet<string> KeptTags =
    [
        "DAMAGE", "ARMOR", "TAUNT", "DIVINE_SHIELD", "POISONOUS", "VENOMOUS", "REBORN", "WINDFURY", "STEALTH",
        "MEGA_WINDFURY", "CANT_ATTACK",
        "TAG_SCRIPT_DATA_NUM_1", "TAG_SCRIPT_DATA_NUM_2", "ATTACHED", "CREATOR", "PLAYER_TECH_LEVEL",
        "PLAYER_LEADERBOARD_PLACE",
        "EXHAUSTED", "BACON_DUO_TEAMMATE_PLAYER_ID", "COPIED_FROM_ENTITY_ID",
    ];

    private sealed class RawChoice
    {
        public int    Id;
        public int    Turn;
        public string ChoiceType = "";
        public string Source = "";
        public readonly List<int> Entities = new();
    }

    private static readonly Regex ReFull       = new(@"^FULL_ENTITY - Creating ID=(\d+) CardID=(\S*)", RegexOptions.Compiled);
    private static readonly Regex ReUpdate     = new(@"^(?:SHOW|CHANGE)_ENTITY - Updating Entity=(.+?) CardID=(\S+)", RegexOptions.Compiled);
    private static readonly Regex ReTag        = new(@"^tag=(\w+) value=(\S+)", RegexOptions.Compiled);
    private static readonly Regex ReTagChange  = new(@"^(?:TAG_CHANGE|HIDE_ENTITY -) Entity=(.+?) tag=(\w+) value=(\S+)", RegexOptions.Compiled);
    private static readonly Regex ReBracket    = new(@"\[entityName=(.*?) id=(\d+) zone=(\w+) zonePos=(\d+) cardId=(\S*) player=(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex ReRefId      = new(@"\bid=(\d+)", RegexOptions.Compiled);
    private static readonly Regex ReChoiceHead = new(@"^id=(\d+) Player=(.*?) TaskList=\S* ChoiceType=(\w+)", RegexOptions.Compiled);
    private static readonly Regex ReChosen     = new(@"^id=(\d+)\b", RegexOptions.Compiled);
    private static readonly Regex RePlayer     = new(@"^Player EntityID=(\d+) PlayerID=(\d+) GameAccountId=\[hi=(\d+)", RegexOptions.Compiled);

    private const string LesserTrinketSource  = "cardId=BG30_Trinket_1st ";
    private const string GreaterTrinketSource = "cardId=BG30_Trinket_2nd ";

    private readonly object _lock = new();
    private readonly Dictionary<int, Entity> _entities = new();
    private readonly HashSet<(int Entity, int Tribe)> _races = new();
    private string     _gameType = "";
    private bool       _gameOver;
    private string?    _myName;
    private RawChoice? _choice;
    private Entity?    _cur;
    private int        _curId;
    // The real player is the one with an account; the other Player entity
    // in Battlegrounds is a dummy standing in for Bob and each opponent.
    private int        _me;
    private int        _meEntity;
    /// <summary>Set between the client's own spectating banners.</summary>
    private bool       _spectating;
    private int        _rawTurn;
    private int        _boardStats, _boardMinions;

    // What the game leaves behind once it is over.
    private int        _gameIndex;
    private int        _myPlace;
    private TimeSpan   _lineTime;
    private readonly List<GameOutcome> _outcomes = new();

    // Combat capture. GameState receives a whole fight at once, well before
    // it is animated, so the boards are read once per blow: the overlay then
    // shows the blow the player is watching rather than the whole fight at
    // the moment it reaches the log.
    private const int  MaxBlows = 400;
    private int        _combatSeq;
    private int        _combatFirstId;
    private bool       _combatOpen;
    private int        _blow;        // blows struck in this fight so far
    private int        _depth;       // how deep in BLOCK_START we are
    private bool       _buried;      // a minion left the field since the last reading
    private readonly List<SimSide> _mine = new(), _theirs = new();
    private List<CombatInfo> _blows = new();
    private List<CombatInfo> _lastBlows = new();

    // Duos: the partner's board as it last took the field. The copies the
    // client is sent for the other three players carry base stats, so the
    // only honest figure is the one we watched fight.
    private readonly Dictionary<int, SeenBoard> _seen = new();
    private readonly Dictionary<int, int> _places = new();
    // The standings as the player sees them: the data stream reorders the
    // leaderboard seconds before the animation does, and a board shown
    // against the wrong portrait is worse than none.
    private readonly Dictionary<int, int> _screenPlaces = new();
    private readonly HashSet<int> _seenThisFight = new();
    private IReadOnlyList<SimSide> _mateBoard = Array.Empty<SimSide>();
    private IReadOnlyList<SimSide> _ourBoard = Array.Empty<SimSide>();
    private int _mateTurn;
    private bool _mateSeen;

    // What the player is looking at. PowerTaskList repeats GameState as the
    // client animates it, ten seconds or more behind in a long fight, so it
    // is the only clock that matches the screen.
    private bool       _sawScreen;
    private int        _screenTurn;
    private bool       _screenCombat;
    private int        _screenBlows;

    public void Reset()
    {
        lock (_lock)
        {
            ResetLocked();
            _gameIndex = 0;
            _spectating = false;
            _outcomes.Clear();
        }
    }

    /// <summary>Games that have finished since the last call.</summary>
    public IReadOnlyList<GameOutcome> TakeOutcomes()
    {
        lock (_lock)
        {
            if (_outcomes.Count == 0) return Array.Empty<GameOutcome>();
            var done = _outcomes.ToList();
            _outcomes.Clear();
            return done;
        }
    }

    private void ResetLocked()
    {
        _entities.Clear();
        _races.Clear();
        _gameType = "";
        _gameOver = false;
        _myName = null;
        _choice = null;
        _cur = null;
        _me = 0;
        _meEntity = 0;
        _rawTurn = 0;
        _myPlace = 0;
        _boardStats = _boardMinions = 0;
        _combatOpen = false;
        _blow = 0;
        _depth = 0;
        _buried = false;
        _mine.Clear();
        _theirs.Clear();
        _blows = new List<CombatInfo>();
        _lastBlows = new List<CombatInfo>();
        _seen.Clear();
        _places.Clear();
        _screenPlaces.Clear();
        _seenThisFight.Clear();
        _mateBoard = Array.Empty<SimSide>();
        _ourBoard = Array.Empty<SimSide>();
        _mateTurn = 0;
        _mateSeen = false;
        _screenTurn = 0;
        _screenCombat = false;
        _screenBlows = 0;
    }

    public void Feed(string line)
    {
        // The client banners a spectated game at both ends:
        // "================== Begin Spectating 1st player ==================".
        // Between them the log describes someone else's game as fully as our
        // own, down to the choices, so without this it would be kept as ours.
        if (line.Contains("Spectat", StringComparison.Ordinal))
        {
            bool begin = line.Contains("Begin Spectating", StringComparison.Ordinal)
                      || line.Contains("Start Spectator", StringComparison.Ordinal);
            if (begin || line.Contains("End Spectator", StringComparison.Ordinal))
            {
                lock (_lock) _spectating = begin;
                return;
            }
        }

        // "D 21:26:41.2563247 GameState.DebugPrintPower() - ..."
        int at = line.IndexOf(" GameState.", StringComparison.Ordinal);
        if (at < 0 || at > 40) { OnScreen(line); return; }
        int dash = line.IndexOf("() - ", at, StringComparison.Ordinal);
        if (dash < 0) return;
        string method = line.Substring(at + 11, dash - at - 11);
        string body = line.Substring(dash + 5);
        string trimmed = body.TrimStart();

        lock (_lock)
        {
            _lineTime = LineTime(line);
            switch (method)
            {
                case "DebugPrintPower":          OnPower(trimmed); break;
                case "DebugPrintGame":           OnGame(trimmed); break;
                case "DebugPrintEntityChoices":  OnChoices(trimmed); break;
                case "DebugPrintEntitiesChosen":
                case "SendChoices":              OnChosen(trimmed); break;
            }
        }
    }

    /// <summary>The time of day a log line carries: "D 21:26:41.2563247 …".</summary>
    private static TimeSpan LineTime(string line) =>
        line.Length > 10 && line[4] == ':' && line[7] == ':'
        && int.TryParse(line.AsSpan(2, 2), out int h)
        && int.TryParse(line.AsSpan(5, 2), out int m)
        && int.TryParse(line.AsSpan(8, 2), out int sec)
            ? new TimeSpan(h, m, sec)
            : TimeSpan.Zero;

    private const string ScreenMarker = " PowerTaskList.DebugPrintPower() - ";

    /// <summary>
    /// The animation's own clock. GameState hands the client a whole fight
    /// in a few hundred milliseconds; PowerTaskList prints the same lines as
    /// they play out, so its GameEntity tags say what is on screen now.
    /// </summary>
    private void OnScreen(string line)
    {
        int at = line.IndexOf(ScreenMarker, StringComparison.Ordinal);
        if (at < 0 || at > 40) return;
        string s = line.Substring(at + ScreenMarker.Length).TrimStart();
        if (s.Contains("tag=PLAYER_LEADERBOARD_PLACE value=", StringComparison.Ordinal))
        {
            Match place = ReTagChange.Match(s);
            if (place.Success && int.TryParse(place.Groups[3].Value, out int standing))
            {
                int id = RefId(place.Groups[1].Value);
                lock (_lock)
                {
                    int pid = id > 0 ? Get(id).PlayerId : 0;
                    if (pid != 0) _screenPlaces[pid] = standing;
                }
            }
            return;
        }
        if (s.StartsWith("BLOCK_START BlockType=ATTACK", StringComparison.Ordinal))
        {
            // A blow the player is watching right now: until the first one
            // the boards are still flying in, whatever the data already has.
            lock (_lock)
            {
                _sawScreen = true;
                _screenBlows++;
            }
            return;
        }
        if (!s.StartsWith("TAG_CHANGE Entity=GameEntity tag=", StringComparison.Ordinal)) return;
        Match m = ReTagChange.Match(s);
        if (!m.Success || !int.TryParse(m.Groups[3].Value, out int v)) return;

        lock (_lock)
        {
            _sawScreen = true;
            if (m.Groups[2].Value == "TURN") _screenTurn = v;
            else if (m.Groups[2].Value == "BOARD_VISUAL_STATE")
            {
                if (v == 2 && !_screenCombat) _screenBlows = 0;
                _screenCombat = v == 2;
            }
        }
    }

    private void OnPower(string s)
    {
        if (s == "CREATE_GAME")
        {
            ResetLocked();
            _gameIndex++;
            return;
        }

        // The boards at each blow: once as it is struck, and again once the
        // blow is over and the dead have been cleared off the field. The
        // overlay shows the later reading, so what has just died is gone from
        // the count instead of still being weighed in.
        if (s.StartsWith("BLOCK_START", StringComparison.Ordinal))
        {
            _depth++;
            if (_combatOpen && s.StartsWith("BLOCK_START BlockType=ATTACK", StringComparison.Ordinal))
            {
                _blow++;
                CaptureBoards();
            }
        }
        else if (s.StartsWith("BLOCK_END", StringComparison.Ordinal))
        {
            if (_depth > 0) _depth--;
            // What a dying minion leaves behind is printed after it dies, so
            // the board is whole again only once the block is closed.
            if (_depth == 0 && _combatOpen && _buried && _blow > 0) CaptureBoards();
        }

        Match m = RePlayer.Match(s);
        if (m.Success)
        {
            _curId = int.Parse(m.Groups[1].Value);
            _cur = Get(_curId);
            if (_me == 0 && m.Groups[3].Value != "0")
            {
                _me = int.Parse(m.Groups[2].Value);
                _meEntity = _curId;
            }
            return;
        }

        m = ReFull.Match(s);
        if (m.Success)
        {
            _curId = int.Parse(m.Groups[1].Value);
            _cur = Get(_curId);
            _cur.CardId = m.Groups[2].Value;
            return;
        }

        m = ReUpdate.Match(s);
        if (m.Success)
        {
            SeedFromBrackets(s);
            int id = RefId(m.Groups[1].Value);
            _cur = id > 0 ? Get(id) : null;
            _curId = id;
            if (_cur is not null) _cur.CardId = m.Groups[2].Value;
            return;
        }

        if (_cur is not null)
        {
            m = ReTag.Match(s);
            if (m.Success)
            {
                ApplyTag(_curId, _cur, m.Groups[1].Value, m.Groups[2].Value, fromBlock: true);
                return;
            }
        }
        _cur = null;

        SeedFromBrackets(s);
        m = ReTagChange.Match(s);
        if (!m.Success) return;

        string who = m.Groups[1].Value, tag = m.Groups[2].Value, value = m.Groups[3].Value;
        if (who == "GameEntity")
        {
            if (tag == "STATE" && value == "COMPLETE")
            {
                FinishGame();
                _gameOver = true;
                _choice = null;
            }
            else if (tag == "TURN" && int.TryParse(value, out int turn))
            {
                _rawTurn = turn;
                if (turn % 2 == 0) OpenCombat();
                else CloseCombat();
            }
            else if (tag == "BOARD_VISUAL_STATE" && value == "2" && !_combatOpen)
                OpenCombat();
            return;
        }
        int target = RefId(who);
        if (target > 0) ApplyTag(target, Get(target), tag, value, fromBlock: false);
    }

    private void OnGame(string s)
    {
        if (s.StartsWith("GameType=", StringComparison.Ordinal)) _gameType = s.Substring(9).Trim();
    }

    private void OnChoices(string s)
    {
        Match m = ReChoiceHead.Match(s);
        if (m.Success)
        {
            string player = m.Groups[2].Value;
            string type = m.Groups[3].Value;
            // Only the local player's choices reach this log, but in Duos a
            // teammate's may too; the hero pick pins down who "we" are.
            if (type == "MULLIGAN") _myName ??= player;
            else if (_myName is not null && player != _myName) { _choice = null; return; }

            _choice = new RawChoice { Id = int.Parse(m.Groups[1].Value), ChoiceType = type, Turn = _rawTurn };
            return;
        }
        if (_choice is null) return;

        if (s.StartsWith("Source=", StringComparison.Ordinal))
        {
            _choice.Source = s.Substring(7);
            return;
        }
        if (s.StartsWith("Entities[", StringComparison.Ordinal))
        {
            SeedFromBrackets(s);
            int id = RefId(s);
            if (id > 0) _choice.Entities.Add(id);
        }
    }

    private void OnChosen(string s)
    {
        Match m = ReChosen.Match(s);
        if (m.Success && _choice is not null && _choice.Id == int.Parse(m.Groups[1].Value))
            _choice = null;
    }

    private void ApplyTag(int id, Entity e, string tag, string value, bool fromBlock)
    {
        switch (tag)
        {
            case "CARDTYPE":      e.CardType = value; break;
            case "ZONE":
                // A minion leaving the field during a fight: the boards the
                // overlay counts have to be read again without it.
                if (_combatOpen && e.CardType == "MINION" && e.Zone == "PLAY" && value != "PLAY") _buried = true;
                e.Zone = value;
                break;
            case "CONTROLLER":    if (int.TryParse(value, out int c)) e.Controller = c; break;
            case "PLAYER_ID":     if (int.TryParse(value, out int pid)) e.PlayerId = pid; break;
            case "ZONE_POSITION": if (int.TryParse(value, out int p)) e.ZonePos = p; break;
            case "PLAYER_LEADERBOARD_PLACE":
                if (int.TryParse(value, out int place) && e.PlayerId != 0)
                {
                    // The same standing the game shows beside the portraits.
                    _places[e.PlayerId] = place;
                    if (e.PlayerId == _me && _me != 0) _myPlace = place;
                }
                break;
            case "ATK":           if (int.TryParse(value, out int a)) e.Atk = a; break;
            case "HEALTH":        if (int.TryParse(value, out int hp)) e.Health = hp; break;
            case "CARDRACE":
                // Only what a card is printed with; buffs that change a
                // race come as TAG_CHANGE and say nothing about the lobby.
                if (fromBlock)
                {
                    int tribe = Tribes.FromLog(value);
                    if (tribe != 0) _races.Add((id, tribe));
                }
                break;
            default:
                if (KeptTags.Contains(tag) && int.TryParse(value, out int n))
                    (e.Tags ??= new())[tag] = n;
                break;
        }
    }

    private void OpenCombat()
    {
        CloseCombat();
        _combatSeq++;
        _combatOpen = true;
        _blow = 0;
        _buried = false;
        _combatFirstId = _entities.Count == 0 ? 0 : _entities.Keys.Max() + 1;
        _mine.Clear();
        _theirs.Clear();
        // The previous fight is likely still playing out on screen.
        _lastBlows = _blows;
        _blows = new List<CombatInfo>();
        _mateSeen = false;
        _seenThisFight.Clear();
        ReadOurBoard();
    }

    private void CloseCombat()
    {
        if (!_combatOpen) return;
        _combatOpen = false;
        _mine.Clear();
        _theirs.Clear();
    }

    private bool IsDuo => _gameType.Contains("DUO", StringComparison.Ordinal);

    private static bool IsCombatHero(string id) =>
        id.Length > 0
        && !id.StartsWith("TB_BaconShop_HERO_PH", StringComparison.Ordinal)
        && id is not "TB_BaconShopBob" and not "TB_BaconShop_HERO_KelThuzad";

    /// <summary>
    /// Reads the pairing on the board. We fight with the entities we already
    /// had in the shop; the opponent's hero and minions are made for this
    /// fight and belong to the stand-in player, so the controller tells the
    /// two sides apart. The client is also sent a copy of every other board
    /// in the lobby, but those wait in SETASIDE and never join the fight.
    /// </summary>
    private void CaptureBoards()
    {
        _buried = false;
        if (_me == 0) return;
        var play = _entities.Where(kv => kv.Value.Zone == "PLAY").ToList();
        var enchants = _entities
            .Where(kv => kv.Value.CardType == "ENCHANTMENT" && kv.Value.Tag("ATTACHED") != 0)
            .ToLookup(kv => kv.Value.Tag("ATTACHED"));

        _mine.Clear();
        _theirs.Clear();
        ReadSide(_mine, ours: true, play, enchants);
        ReadSide(_theirs, ours: false, play, enchants);
        if (_mine.Count == 0 || _theirs.Count == 0 || _blows.Count >= MaxBlows) return;

        int turn = Math.Max(1, _rawTurn / 2);
        RememberBoards(turn);
        int mate = _meEntity != 0 ? Get(_meEntity).Tag("BACON_DUO_TEAMMATE_PLAYER_ID") : 0;
        var mateHere = mate == 0 ? null
            : _mine.Where(s => Get(s.HeroEntityId).PlayerId == mate).ToList();
        // Their board as it took the field, not as it was left afterwards.
        if (mateHere is { Count: > 0 } && !_mateSeen)
        {
            _mateBoard = mateHere;
            _mateTurn = turn;
            _mateSeen = true;
        }

        // The team's other board, while it is still off the field. When the
        // partner is the one fighting, ours waits among the entities we
        // brought from the shop and can be read as it stands.
        bool weFight = _mine.Any(s => Get(s.HeroEntityId).PlayerId == _me);
        bool mateFights = mateHere is { Count: > 0 };
        var bench = mate == 0 || (weFight && mateFights) ? Array.Empty<SimSide>()
                  : weFight ? _mateBoard
                  : _ourBoard;
        _blows.Add(new CombatInfo(_combatSeq, turn, IsDuo, _mine.ToList(), _theirs.ToList())
        {
            Blow = _blow,
            MyBench = bench,
            BenchTurn = weFight ? _mateTurn : turn,
        });
    }

    /// <summary>
    /// One team's boards, in the order their heroes took the field: in Duos a
    /// second hero steps in once the first one's board is wiped, and its
    /// cards are created after it.
    /// </summary>
    private void ReadSide(List<SimSide> into, bool ours,
        List<KeyValuePair<int, Entity>> play, ILookup<int, KeyValuePair<int, Entity>> enchants)
    {
        bool Ours(Entity e) => e.Controller == _me;
        var heroes = play
            .Where(kv => kv.Value.CardType == "HERO" && Ours(kv.Value) == ours && IsCombatHero(kv.Value.CardId))
            .OrderBy(kv => kv.Key)
            .ToList();

        for (int i = 0; i < heroes.Count; i++)
        {
            int from = heroes[i].Key, to = i + 1 < heroes.Count ? heroes[i + 1].Key : int.MaxValue;
            Entity h = heroes[i].Value;
            // This hero's own cards: made after it and, on the other side,
            // never older than the fight.
            bool Mine(KeyValuePair<int, Entity> kv) =>
                Ours(kv.Value) == ours && kv.Key > from && kv.Key < to && (ours || kv.Key >= _combatFirstId);

            var board = play
                .Where(kv => kv.Value.CardType == "MINION" && Mine(kv))
                .OrderBy(kv => kv.Value.ZonePos == 0 ? 99 : kv.Value.ZonePos)
                .Take(7)
                .Select(kv => ToMinion(kv.Key, kv.Value, enchants[kv.Key]))
                .ToList();
            var powers = _entities
                .Where(kv => kv.Value.CardType == "HERO_POWER" && Mine(kv))
                .Select(kv => new SimCard(kv.Key, kv.Value.CardId, kv.Value.Tag("EXHAUSTED") != 0,
                    kv.Value.Tag("TAG_SCRIPT_DATA_NUM_1"), kv.Value.Tag("TAG_SCRIPT_DATA_NUM_2")))
                .ToList();
            var trinkets = _entities
                .Where(kv => kv.Value.CardType == "BATTLEGROUND_TRINKET" && Mine(kv))
                .Select(kv => new SimCard(kv.Key, kv.Value.CardId, false,
                    kv.Value.Tag("TAG_SCRIPT_DATA_NUM_1"), kv.Value.Tag("TAG_SCRIPT_DATA_NUM_2")))
                .ToList();

            into.Add(new SimSide(from, h.CardId, h.Name, h.Health - h.Tag("DAMAGE") + h.Tag("ARMOR"),
                Math.Max(1, h.Tag("PLAYER_TECH_LEVEL")), powers, trinkets, board));
        }
    }

    /// <summary>
    /// Our own board as the fight opens. When the partner is the one who
    /// takes the field, ours is the board waiting behind them, and this is
    /// the last moment its cards are still plainly in play.
    /// </summary>
    private void ReadOurBoard()
    {
        _ourBoard = Array.Empty<SimSide>();
        var hero = _entities
            .Where(kv => kv.Value.Controller == _me && kv.Value.CardType == "HERO"
                      && kv.Value.Zone == "PLAY" && IsCombatHero(kv.Value.CardId))
            .OrderByDescending(kv => kv.Key)
            .FirstOrDefault();
        if (hero.Value is null) return;

        var enchants = _entities
            .Where(kv => kv.Value.CardType == "ENCHANTMENT" && kv.Value.Tag("ATTACHED") != 0)
            .ToLookup(kv => kv.Value.Tag("ATTACHED"));
        var board = _entities
            .Where(kv => kv.Key > hero.Key && kv.Value.Controller == _me
                      && kv.Value.CardType == "MINION" && kv.Value.Zone == "PLAY")
            .OrderBy(kv => kv.Value.ZonePos == 0 ? 99 : kv.Value.ZonePos)
            .ThenBy(kv => kv.Key)
            .Take(7)
            .Select(kv => ToMinion(kv.Key, kv.Value, enchants[kv.Key]))
            .ToList();
        if (board.Count == 0) return;

        Entity h = hero.Value;
        _ourBoard = new[]
        {
            new SimSide(hero.Key, h.CardId, h.Name, h.Health - h.Tag("DAMAGE") + h.Tag("ARMOR"),
                Math.Max(1, h.Tag("PLAYER_TECH_LEVEL")), Array.Empty<SimCard>(), Array.Empty<SimCard>(), board),
        };
    }

    /// <summary>
    /// Keeps every board as it steps onto the field: that is the one moment
    /// the client is shown what the other players are holding, and it is what
    /// the game's own leaderboard portraits go back to afterwards.
    /// </summary>
    private void RememberBoards(int turn)
    {
        int mate = _meEntity != 0 ? Get(_meEntity).Tag("BACON_DUO_TEAMMATE_PLAYER_ID") : 0;

        foreach (SimSide side in _mine.Concat(_theirs))
        {
            Entity hero = Get(side.HeroEntityId);
            int pid = hero.PlayerId;
            if (pid == 0 || pid == _me) continue;
            if (!_seenThisFight.Add(pid)) continue;       // only as it takes the field
            if (side.Board.Count == 0) continue;

            var board = side.Board.Select(m => new SeenMinion(
                m.CardId, Get(m.EntityId).Name ?? m.CardId, m.Attack, Math.Max(0, m.Health),
                m.Taunt, m.DivineShield, m.Poisonous || m.Venomous, m.Reborn, m.Windfury)).ToList();
            _seen[pid] = new SeenBoard(pid, _places.TryGetValue(pid, out int at) ? at : 0,
                mate != 0 && pid == mate,
                hero.CardId, HeroNameFor(pid) ?? side.HeroName ?? hero.CardId,
                turn, side.Hp, side.TavernTier, board);
        }
    }

    /// <summary>
    /// The hero's name as the leaderboard spells it: the copies the client
    /// keeps for its portraits carry it, the entities that fight often do not.
    /// </summary>
    private string? HeroNameFor(int pid) => _entities.Values
        .FirstOrDefault(e => e.CardType == "HERO" && e.PlayerId == pid && e.Name is { Length: > 0 }
                          && IsCombatHero(e.CardId))?.Name;

    private static SimMinion ToMinion(int id, Entity e, IEnumerable<KeyValuePair<int, Entity>> ench) =>
        new(id, e.CardId, e.Atk, e.Health - e.Tag("DAMAGE"), e.Health,
            e.Tag("TAUNT") != 0, e.Tag("DIVINE_SHIELD") != 0, e.Tag("POISONOUS") != 0, e.Tag("VENOMOUS") != 0,
            e.Tag("REBORN") != 0, e.Tag("WINDFURY") != 0, e.Tag("STEALTH") != 0,
            e.Tag("TAG_SCRIPT_DATA_NUM_1"), e.Tag("TAG_SCRIPT_DATA_NUM_2"),
            ench.OrderBy(x => x.Key).Select(x => new SimEnchant(x.Value.CardId, x.Value.Tag("CREATOR"),
                x.Value.Tag("TAG_SCRIPT_DATA_NUM_1"), x.Value.Tag("TAG_SCRIPT_DATA_NUM_2"), x.Key)).ToList(),
            // Mega-windfury comes either as its own tag or as a windfury of 3.
            e.Tag("MEGA_WINDFURY") != 0 || e.Tag("WINDFURY") > 2,
            e.Tag("CANT_ATTACK") != 0);

    private void SeedFromBrackets(string s)
    {
        if (!s.Contains("[entityName=", StringComparison.Ordinal)) return;
        foreach (Match b in ReBracket.Matches(s))
        {
            Entity e = Get(int.Parse(b.Groups[2].Value));
            string name = b.Groups[1].Value;
            if (name.Length > 0 && !name.StartsWith("UNKNOWN", StringComparison.Ordinal)) e.Name = name;
            if (b.Groups[5].Value.Length > 0) e.CardId = b.Groups[5].Value;
            // A bracket shows what the client displays, which trails the
            // GameState tags (a removed copy still reads zone=PLAY), so it
            // only fills in entities not seen yet.
            if (e.Zone is null)
            {
                e.Zone = b.Groups[3].Value;
                e.ZonePos = int.Parse(b.Groups[4].Value);
                e.Controller = int.Parse(b.Groups[6].Value);
            }
        }
    }

    private Entity Get(int id)
    {
        if (!_entities.TryGetValue(id, out var e)) _entities[id] = e = new Entity();
        return e;
    }

    private static int RefId(string s)
    {
        if (int.TryParse(s, out int n)) return n;
        Match m = ReRefId.Match(s);
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    /// <summary>
    /// A lobby has five tribes. Off-lobby minions do show up (tokens, effects
    /// that summon from outside the pool), but a handful at most, so the
    /// fifth tribe has to clearly outnumber the sixth to be believed.
    /// </summary>
    private (List<(int Tribe, int Seen)> Counts, List<int> Tribes, bool Confident) Races()
    {
        var counts = _races
            .GroupBy(r => r.Tribe)
            .Select(g => (Tribe: g.Key, Seen: g.Count()))
            .OrderByDescending(t => t.Seen)
            .ToList();
        bool confident = counts.Count >= 5 && counts[4].Seen >= 6
                      && (counts.Count == 5 || counts[4].Seen >= 2 * counts[5].Seen);
        return (counts, counts.Take(5).Select(t => t.Tribe).ToList(), confident);
    }

    private Entity? MyHero()
    {
        if (_me == 0) return null;
        bool Real(Entity e) => e.CardType == "HERO" && e.Controller == _me
                            && !e.CardId.StartsWith("TB_BaconShop_HERO_PH", StringComparison.Ordinal);
        return _entities.Values.FirstOrDefault(e => Real(e) && e.PlayerId == _me)
            ?? _entities.Values.FirstOrDefault(e => Real(e) && e.Zone == "PLAY");
    }

    /// <summary>
    /// The lobby, ordered the way the leaderboard orders it. One hero entity
    /// per player carries the name the portraits show; the fight copies do
    /// not, so the named one wins.
    /// </summary>
    private List<LobbyPlayer> Lobby()
    {
        int mate = _meEntity != 0 ? Get(_meEntity).Tag("BACON_DUO_TEAMMATE_PLAYER_ID") : 0;
        var byPlayer = new Dictionary<int, Entity>();
        foreach (Entity e in _entities.Values)
        {
            if (e.CardType != "HERO" || e.PlayerId == 0 || !IsCombatHero(e.CardId)) continue;
            if (!byPlayer.TryGetValue(e.PlayerId, out Entity? kept)
                || (kept.Name is null && e.Name is { Length: > 0 }))
                byPlayer[e.PlayerId] = e;
        }

        var lobby = new List<LobbyPlayer>();
        foreach ((int pid, Entity hero) in byPlayer)
        {
            int hp = hero.Health - hero.Tag("DAMAGE") + hero.Tag("ARMOR");
            _seen.TryGetValue(pid, out SeenBoard? board);
            // The leaderboard entity keeps the tavern tier up to date, so it
            // is today's, not the one from the fight we last saw.
            int tier = hero.Tag("PLAYER_TECH_LEVEL");
            lobby.Add(new LobbyPlayer(
                _screenPlaces.TryGetValue(pid, out int shown) ? shown
                    : _places.TryGetValue(pid, out int at) ? at : 0,
                pid,
                hero.Name ?? hero.CardId,
                hero.CardId,
                pid == _me || (mate != 0 && pid == mate),
                hero.Health == 0 || hp > 0,
                tier > 0 ? tier : board?.TechLevel ?? 0,
                board));
        }
        lobby.Sort((a, b) => (a.Place == 0 ? 99 : a.Place).CompareTo(b.Place == 0 ? 99 : b.Place));
        return lobby;
    }

    /// <summary>Keeps the hero, the place and the last board of a game that just ended.</summary>
    private void FinishGame()
    {
        if (Environment.GetEnvironmentVariable("BGEND") == "1")
            Console.Error.WriteLine($"[end] over={_gameOver} place={_myPlace} type={_gameType} me={_me} "
                + $"hero={(MyHero() is { } hh ? hh.CardId + "/pid" + hh.PlayerId : "нет")} доска={_ourBoard.Count}");
        if (_gameOver || _myPlace == 0) return;
        // Watching someone else play is not a game of ours.
        if (_spectating) return;
        if (!_gameType.Contains("BATTLEGROUNDS", StringComparison.Ordinal)) return;
        if (MyHero() is not { } hero) return;

        var board = _ourBoard
            .SelectMany(side => side.Board)
            .Select(m => new OutcomeMinion(m.CardId, Get(m.EntityId).Name ?? m.CardId,
                m.Attack, Math.Max(0, m.Health), Keywords(m)))
            .ToList();
        _outcomes.Add(new GameOutcome(_gameIndex, _lineTime, IsDuo, hero.CardId, hero.Name ?? hero.CardId,
            _myPlace, (_rawTurn + 1) / 2, Races().Tribes, board));
    }

    /// <summary>What a minion ended the game with, worded as the overlay words it.</summary>
    private static string Keywords(SimMinion m)
    {
        var marks = new List<string>(3);
        if (m.Taunt) marks.Add("провок.");
        if (m.DivineShield) marks.Add("щит");
        if (m.Poisonous || m.Venomous) marks.Add("яд");
        if (m.Reborn) marks.Add("возр.");
        if (m.Windfury) marks.Add("вихрь");
        return string.Join(" · ", marks);
    }

    public GameSnapshot Snapshot()
    {
        lock (_lock)
        {
            bool bg = _gameType.Contains("BATTLEGROUNDS", StringComparison.Ordinal) && !_gameOver;
            if (!bg) return GameSnapshot.Empty;

            var (counts, tribes, confident) = Races();

            // TURN counts shop and combat phases separately: odd is the shop.
            bool inCombat = _rawTurn > 0 && _rawTurn % 2 == 0;
            // Without the animation's lines in the log there is nothing but
            // the data clock to go by.
            bool onScreenCombat = _sawScreen ? _screenCombat : inCombat;
            int turn = _sawScreen ? _screenTurn : _rawTurn;
            Entity? hero = null;
            if (_me != 0)
            {
                hero = MyHero();
                // Combat plays out on copies; the shop board is the one worth
                // comparing, so hold its last value through combat.
                if (!inCombat)
                {
                    var board = _entities.Values
                        .Where(e => e.Controller == _me && e.Zone == "PLAY" && e.CardType == "MINION")
                        .ToList();
                    _boardMinions = board.Count;
                    _boardStats = board.Sum(e => e.Atk + e.Health);
                }
            }

            // The fight on screen, blow by blow. Which readings are its own
            // is told by the turn: the data runs ahead and may already be
            // reading the next fight.
            int shopTurn = (turn + 1) / 2;
            List<CombatInfo>? blows =
                _blows.Count > 0 && (!_sawScreen || _blows[0].Turn == shopTurn) ? _blows :
                _lastBlows.Count > 0 && _lastBlows[0].Turn == shopTurn ? _lastBlows : null;
            // Each reading carries the blow it belongs to, and the last of the
            // blow on screen is the board as that blow left it. Before the
            // first blow the minions are still taking the field, and what the
            // data holds is no use to the player yet. Without the animation's
            // lines there is no clock, so the fight stays at its opening.
            CombatInfo? combat = null;
            if (onScreenCombat && blows is not null)
            {
                if (!_sawScreen) combat = blows[0];
                else
                    for (int i = blows.Count - 1; i >= 0; i--)
                        if (blows[i].Blow <= _screenBlows) { combat = blows[i]; break; }
            }

            var seen = _seen.Values.OrderByDescending(b => b.Turn).ThenBy(b => b.PlayerId).ToList();
            return new GameSnapshot(true, IsDuo, counts, tribes, confident, BuildChoice(), seen, Lobby(),
                hero?.CardId, hero?.Name, shopTurn, onScreenCombat, _boardStats, _boardMinions,
                combat, onScreenCombat && combat is null);
        }
    }

    private PendingChoice? BuildChoice()
    {
        if (_choice is null || _choice.Entities.Count == 0) return null;
        // The offer arrives with the rest of the turn, which the client is
        // still animating: the cards come up on screen only once it is there.
        if (_sawScreen && (_screenCombat || _screenTurn < _choice.Turn)) return null;

        ChoiceKind kind;
        if (_choice.ChoiceType == "MULLIGAN") kind = ChoiceKind.Hero;
        else if (_choice.Source.Contains(LesserTrinketSource, StringComparison.Ordinal)) kind = ChoiceKind.LesserTrinket;
        else if (_choice.Source.Contains(GreaterTrinketSource, StringComparison.Ordinal)) kind = ChoiceKind.GreaterTrinket;
        else return null;

        IEnumerable<int> ids = _choice.Entities;
        if (kind == ChoiceKind.Hero)
        {
            // A rerolled hero replaces one in hand without a new choice block,
            // so the offer is whatever hero cards sit in our hand right now.
            int me = Get(_choice.Entities[0]).Controller;
            var inHand = _entities
                .Where(kv => kv.Value.Controller == me && kv.Value.Zone == "HAND"
                          && (kv.Value.CardType == "HERO" || _choice.Entities.Contains(kv.Key)))
                .OrderBy(kv => kv.Value.ZonePos)
                .Select(kv => kv.Key)
                .ToList();
            if (inHand.Count > 0) ids = inHand;
        }

        var options = ids
            .Select(id => (id, e: Get(id)))
            .Select(x => new ChoiceOption(x.id, x.e.CardId, x.e.Name ?? x.e.CardId))
            .ToList();
        return new PendingChoice(_choice.Id, kind, options);
    }
}
