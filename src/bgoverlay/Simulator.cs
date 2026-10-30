using System.Diagnostics;

namespace BgOverlay;

/// <summary>
/// Plays a fight out by the game's own rules — who swings first, who the blow
/// lands on, what a shield eats and what poison takes with it — and counts how
/// it ends over many runs. Targets are picked at random, so the same boards
/// are fought a few thousand times and the three figures are how often each
/// way came up.
///
/// What a minion is, beyond its stats and its keywords, is not in it: the log
/// says a minion has a deathrattle but never what the deathrattle does, so a
/// board that leans on what its minions leave behind fights better than this
/// reads it. Everything that happens before the first blow — hero powers,
/// trinkets, start-of-combat triggers — is already on the boards by the time
/// they are read, so none of that is missing.
/// </summary>
public static class CombatSim
{
    /// <summary>A fight neither board can finish is a draw.</summary>
    private const int MaxSwings = 400;

    public readonly record struct Odds(double Win, double Tie, double Lose);

    /// <param name="seed">Fixed, so the same boards always read the same.</param>
    /// <param name="maxMs">
    /// The count runs on the thread that draws the overlay, so it is cut
    /// short rather than allowed to stutter the window: a few hundred fights
    /// already place the figure within a percent or two.
    /// </param>
    public static Odds Run(IReadOnlyList<SimSide> us, IReadOnlyList<SimSide> them,
        int runs, int seed, int maxMs = 25)
    {
        List<Unit>[] mine = Muster(us), theirs = Muster(them);
        var rnd = new Random(seed);
        long until = Stopwatch.GetTimestamp() + maxMs * Stopwatch.Frequency / 1000;
        int win = 0, tie = 0, done = 0;
        for (int i = 0; i < runs; i++)
        {
            int r = Play(new Army(mine), new Army(theirs), rnd);
            if (r > 0) win++;
            else if (r == 0) tie++;
            done++;
            // Checking the clock costs as much as a fight, so not every time.
            if ((i & 63) == 63 && Stopwatch.GetTimestamp() >= until) break;
        }
        double all = Math.Max(1, done);
        return new Odds(win / all, tie / all, (done - win - tie) / all);
    }

    /// <summary>One minion as the fight needs it.</summary>
    private sealed class Unit
    {
        public int  Attack;
        public int  Health;
        public int  Swings = 1;      // windfury swings twice, mega four times
        public bool Taunt, Shield, Poison, Venom, Reborn, Stealth, Idle;
        public bool Doomed;          // poison took it, whatever its health says
        public Unit? Seed;           // what it was before the fight, for reborn

        public bool Alive => !Doomed && Health > 0;
        public bool CanSwing => Attack > 0 && !Idle;
        public Unit Copy() => (Unit)MemberwiseClone();
    }

    /// <summary>
    /// One team. In Duos a team brings two boards and the second steps onto
    /// the field once the first is wiped, so they are kept in a queue in the
    /// order they enter.
    /// </summary>
    private sealed class Army
    {
        private readonly List<Unit>[] _boards;
        private int _at;
        private int _next;           // whose turn it is to swing

        public Army(List<Unit>[] boards)
        {
            _boards = new List<Unit>[boards.Length];
            for (int i = 0; i < boards.Length; i++)
            {
                _boards[i] = new List<Unit>(boards[i].Count);
                foreach (Unit u in boards[i]) _boards[i].Add(u.Copy());
            }
        }

        /// <summary>The board on the field, the wiped ones passed over.</summary>
        public List<Unit>? Field
        {
            get
            {
                while (_at < _boards.Length && _boards[_at].Count == 0) { _at++; _next = 0; }
                return _at < _boards.Length ? _boards[_at] : null;
            }
        }

        public bool Standing
        {
            get
            {
                foreach (List<Unit> b in _boards) if (b.Count > 0) return true;
                return false;
            }
        }

        public bool CanSwing
        {
            get
            {
                if (Field is not { } f) return false;
                foreach (Unit u in f) if (u.CanSwing) return true;
                return false;
            }
        }

        /// <summary>
        /// The next minion in line. They swing left to right, and the line is
        /// kept through the fight as the game keeps it.
        /// </summary>
        public Unit? Attacker()
        {
            if (Field is not { Count: > 0 } f) return null;
            for (int i = 0; i < f.Count; i++)
            {
                int at = (_next + i) % f.Count;
                if (!f[at].CanSwing) continue;
                _next = at + 1;
                return f[at];
            }
            return null;
        }
    }

    /// <summary>1 we won, 0 a draw, -1 we lost.</summary>
    private static int Play(Army a, Army b, Random rnd)
    {
        int ca = a.Field?.Count ?? 0, cb = b.Field?.Count ?? 0;
        // The wider board swings first; even boards toss for it.
        bool ours = ca > cb || (ca == cb && rnd.Next(2) == 0);

        for (int swing = 0; swing < MaxSwings; swing++)
        {
            if (a.Field is null || b.Field is null) break;
            bool canA = a.CanSwing, canB = b.CanSwing;
            if (!canA && !canB) break;              // nothing left that can attack
            if (ours && !canA) ours = false;
            else if (!ours && !canB) ours = true;
            Swing(ours ? a : b, ours ? b : a, rnd);
            ours = !ours;
        }

        bool left = a.Standing, right = b.Standing;
        return left == right ? 0 : left ? 1 : -1;
    }

    private static void Swing(Army att, Army def, Random rnd)
    {
        if (att.Attacker() is not { } one) return;
        one.Stealth = false;        // swinging gives away the hiding place
        for (int s = 0; s < one.Swings; s++)
        {
            if (!one.Alive) return;
            if (att.Field is not { } mine || def.Field is not { } theirs) return;
            if (Target(theirs, rnd) is not { } foe) return;   // everything across is hidden
            Trade(one, foe);
            Bury(mine);
            Bury(theirs);
        }
    }

    /// <summary>
    /// Who the blow lands on: a taunt has to be attacked, stealth cannot be,
    /// and among what is left it is a toss-up.
    /// </summary>
    private static Unit? Target(List<Unit> field, Random rnd)
    {
        int open = 0, taunts = 0;
        foreach (Unit u in field)
        {
            if (u.Stealth) continue;
            open++;
            if (u.Taunt) taunts++;
        }
        if (open == 0) return null;
        bool tauntsOnly = taunts > 0;
        int pick = rnd.Next(tauntsOnly ? taunts : open);
        foreach (Unit u in field)
        {
            if (u.Stealth || (tauntsOnly && !u.Taunt)) continue;
            if (pick-- == 0) return u;
        }
        return null;
    }

    /// <summary>Both minions deal their attack to the other at once.</summary>
    private static void Trade(Unit att, Unit def)
    {
        bool defShield = Hit(def, att.Attack, att.Poison || att.Venom);
        bool attShield = Hit(att, def.Attack, def.Poison || def.Venom);
        // Venom is spent on the one body it reaches; poison is not.
        if (att.Venom && att.Attack > 0 && !defShield) att.Venom = false;
        if (def.Venom && def.Attack > 0 && !attShield) def.Venom = false;
    }

    /// <summary>Damage onto one minion; true when a shield ate it.</summary>
    private static bool Hit(Unit u, int damage, bool venomous)
    {
        if (damage <= 0) return false;
        if (u.Shield) { u.Shield = false; return true; }
        u.Health -= damage;
        if (venomous) u.Doomed = true;
        return false;
    }

    /// <summary>
    /// Clears the dead off the board. Reborn brings the card back where it
    /// stood, as it was before the fight and with one health.
    /// </summary>
    private static void Bury(List<Unit> field)
    {
        for (int i = field.Count - 1; i >= 0; i--)
        {
            Unit u = field[i];
            if (u.Alive) continue;
            if (u.Reborn && u.Seed is { } was)
            {
                Unit again = was.Copy();
                again.Health = 1;
                again.Reborn = false;
                field[i] = again;
            }
            else field.RemoveAt(i);
        }
    }

    private static List<Unit>[] Muster(IReadOnlyList<SimSide> sides)
    {
        var armies = new List<Unit>[sides.Count];
        for (int i = 0; i < sides.Count; i++)
        {
            armies[i] = new List<Unit>(sides[i].Board.Count);
            foreach (SimMinion m in sides[i].Board) armies[i].Add(Make(m));
        }
        return armies;
    }

    private static Unit Make(SimMinion m)
    {
        var u = new Unit
        {
            Attack = Math.Max(0, m.Attack),
            Health = Math.Max(1, m.Health),
            Swings = m.MegaWindfury ? 4 : m.Windfury ? 2 : 1,
            Taunt = m.Taunt,
            Shield = m.DivineShield,
            Poison = m.Poisonous,
            Venom = m.Venomous,
            Reborn = m.Reborn,
            Stealth = m.Stealth,
            Idle = m.CantAttack,
        };
        u.Seed = u.Copy();
        return u;
    }
}
