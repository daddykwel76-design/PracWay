using System.Linq;
using System.Drawing;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory;

namespace PracWay;

public sealed class PracWayPlugin : BasePlugin
{
    public override string ModuleName        => "PracWay";
    public override string ModuleVersion     => "1.0.0";
    public override string ModuleAuthor      => "Codex";
    public override string ModuleDescription => "Parcours Practice — joueurs Terro vs bots CT.";

    // ─── Préfixe chat ──────────────────────────────────────────────────────────
    private const string P = " \x07[PRACWAY]\x01";

    // ─── Constantes ────────────────────────────────────────────────────────────
    private const int RoundDuration = 90;   // secondes
    private const int MaxTerros     = 3;
    // Pas de constante BotCtSlots — le nombre de bots est dynamique selon les slots configurés dans la zone.
    private const int MinASpawns    = 3;
    private const int MinCtSpawns   = 1;    // par slot CT

    // ─── Argents disponibles ───────────────────────────────────────────────────
    private static readonly int[] MoneyOptions = { 800, 2000, 2500, 3000, 3500, 4000, 4500, 6000 };

    // ─── Slots spawn ───────────────────────────────────────────────────────────
    private enum SpawnSlot { A, CT1, CT2, CT3, CT4, CT5, CT6, CT7, CT8, CT9, CT10, CT11, CT12 }

    // ─── Actions bots CT ───────────────────────────────────────────────────────
    private enum BotAction { Stand, Crouch }

    // ═══════════════════════════════════════════════════════════════════════════
    // TYPES DE DONNÉES
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly record struct WaySpawn(float X, float Y, float Z, float Pitch, float Yaw, float Roll);

    private sealed class SpawnEntry
    {
        public WaySpawn  Spawn      { get; set; }
        public BotAction Action     { get; set; } = BotAction.Stand;

    }

    private sealed class WayZone
    {
        public string Name { get; }
        private readonly Dictionary<SpawnSlot, List<SpawnEntry>> _slots = new();

        public WayZone(string name) => Name = name;

        public static readonly SpawnSlot[] BotSlots =
        {
            SpawnSlot.CT1, SpawnSlot.CT2, SpawnSlot.CT3, SpawnSlot.CT4, SpawnSlot.CT5, SpawnSlot.CT6,
            SpawnSlot.CT7, SpawnSlot.CT8, SpawnSlot.CT9, SpawnSlot.CT10, SpawnSlot.CT11, SpawnSlot.CT12
        };

        public List<SpawnEntry> GetSpawns(SpawnSlot slot)
        {
            if (!_slots.TryGetValue(slot, out var list)) { list = new(); _slots[slot] = list; }
            return list;
        }

        public void SetSpawnFull(SpawnSlot slot, int idx, WaySpawn s, BotAction action)
        {
            var list  = GetSpawns(slot);
            var entry = new SpawnEntry { Spawn = s, Action = action };
            if (idx < list.Count) list[idx] = entry;
            else                  list.Add(entry);
        }

        public void AddSpawn(SpawnSlot slot, SpawnEntry entry) => GetSpawns(slot).Add(entry);

        // Un parcours est valide s'il a assez de spawns A et au moins 1 slot CT configuré.
        public bool IsReady =>
            GetSpawns(SpawnSlot.A).Count >= MinASpawns &&
            BotSlots.Any(s => GetSpawns(s).Count >= MinCtSpawns);

        // Tous les slots CT qui ont au moins un spawn configuré
        public List<SpawnSlot> ConfiguredBotSlots =>
            BotSlots.Where(s => GetSpawns(s).Count >= MinCtSpawns).ToList();
    }

    // ─── Contextes de menu ─────────────────────────────────────────────────────
    private enum MenuCtx
    {
        None,
        Main,
        SpecialWay,
        SpecialRounds,
        SpecialMoney,
        CreateWay,
        EditWayList,
        DeleteWayList,
    }

    // ─── Configuration en attente (parcours spécial) ───────────────────────────
    private sealed class PendingConfig
    {
        public string? SpecificWayName { get; set; }
        public int     RoundLimit      { get; set; }
        public int     StartMoney      { get; set; } = -1;
        public List<string> WayListForMenu { get; set; } = new();
    }

    // ─── Session active ────────────────────────────────────────────────────────
    private sealed class ActiveSession
    {
        public PendingConfig             Config          { get; }
        public List<CCSPlayerController> TerroPlayers    { get; set; } = new();
        public int    TerroDeaths      { get; set; }
        public int    BotDeaths        { get; set; }
        public int    RoundNumber      { get; set; }
        public bool   RoundFinalized   { get; set; }
        public bool   LastRoundWon     { get; set; }
        public string? CurrentWayName  { get; set; }
        public int    BotCount        { get; set; }   // nombre de bots CT pour ce round
        public CounterStrikeSharp.API.Modules.Timers.Timer? RoundTimer { get; set; }
        public bool  RoundTimerPaused    { get; set; }
        public float RoundTimeRemaining  { get; set; } = RoundDuration;
        public System.Diagnostics.Stopwatch? RoundStopwatch { get; set; }

        public ActiveSession(PendingConfig cfg) => Config = cfg;
    }

    // ─── Wizard d'édition ──────────────────────────────────────────────────────
    private sealed class ZoneWizard
    {
        public string?      EditingZone { get; set; }
        public List<string> ZoneNames   { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // STATE
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly Dictionary<string, WayZone>      _zones     = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, MenuCtx>       _menuCtx   = new();
    private readonly Dictionary<ulong, PendingConfig> _pending   = new();
    private readonly Dictionary<ulong, ZoneWizard>    _wizards   = new();
    private readonly Dictionary<ulong, int>           _killCount = new();
    private readonly List<CBeam>                      _markers   = new();
    // Pattern OpenPrefirePrac : requêtes en attente et ownership des bots
    // Key = slot du bot, Value = (SpawnEntry, money)
    private readonly Dictionary<int, (SpawnEntry entry, int money)> _ownerOfBots = new();
    // Key = slot du bot à placer, Value = (SpawnEntry, money) en attente
    private readonly Dictionary<int, (SpawnEntry entry, int money)> _pendingBotSlots = new();
    // Nombre de bots encore attendus pour le round en cours
    private int _botRequestCount = 0;
    // Slots des bots effectivement spawnés ce round — pour les kicker proprement entre les rounds
    private readonly HashSet<int> _activeBotSlots = new();
    // Slots déjà traités dans OnPlayerSpawn ce round — bloque les re-fires CS2
    private readonly HashSet<int> _processedSpawnSlots = new();

    private ActiveSession? _session;
    private bool _roundTransitionPending;
    private bool _startingRound;

    // ═══════════════════════════════════════════════════════════════════════════
    // CHARGEMENT
    // ═══════════════════════════════════════════════════════════════════════════

    public override void Load(bool hotReload)
    {
        AddCommand("css_way",      "Menu principal PracWay", (c, _) => OpenMainMenu(c));
        AddCommand("css_way_stop", "Arreter le parcours",    (c, _) => CmdStop(c));
        AddCommand("css_duel_way", "Aide PracWay",           (c, _) => PrintHelp(c));

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Pre);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect);
        AddCommandListener("say",      OnSay);
        AddCommandListener("say_team", OnSay);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EVENEMENTS MAP / ROUND
    // ═══════════════════════════════════════════════════════════════════════════

    private void OnMapStart(string _)
    {
        _session = null;
        _zones.Clear();
        _wizards.Clear();
        _killCount.Clear();
        _ownerOfBots.Clear();
        _pendingBotSlots.Clear();
        _botRequestCount = 0;
        _roundTransitionPending = false;
        _startingRound = false;
        ClearMarkers();
        StopBotTimers();
        LoadZones();
    }

    private void OnClientPutInServer(int slot)
    {
        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot == null || !bot.IsValid || !bot.IsBot) return;

        if (_session == null) return;  // Pas de session : on ignore, pas de kick

        // Bot attendu : on lui assigne une entrée de spawn
        if (_botRequestCount > 0 && _pendingBotSlots.Count > 0)
        {
            var pending = _pendingBotSlots.First();
            _pendingBotSlots.Remove(pending.Key);
            _ownerOfBots[slot] = pending.Value;
            _activeBotSlots.Add(slot);
            _botRequestCount--;
            Console.WriteLine("[PracWay][DEBUG] OnClientPutInServer — bot slot=" + slot + " assigné, restants=" + _botRequestCount);
        }
        else
        {
            Console.WriteLine("[PracWay][DEBUG] OnClientPutInServer — bot slot=" + slot + " surnuméraire, kick");
            KickBot(slot);
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo _)
    {
        // Le cycle des rounds est géré explicitement par LaunchWay + FinalizeRound.
        // Ne rien faire ici pour éviter les doubles StartNextRound causés par les events warmup.
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo _)
    {
        if (_session == null || _session.RoundFinalized) return HookResult.Continue;

        // On verrouille immédiatement pour éviter la race condition avec OnPlayerDeath.
        _session.RoundFinalized = true;
        AddTimer(0.1f, () =>
        {
            if (_session != null) FinalizeRound();
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo _)
    {
        if (_session == null) return HookResult.Continue;

        var victim   = @event.Userid;
        var attacker = @event.Attacker;
        if (victim == null) return HookResult.Continue;

        if (attacker != null && attacker.IsValid && !attacker.IsBot && attacker.SteamID != victim.SteamID)
        {
            _killCount.TryGetValue(attacker.SteamID, out var k);
            _killCount[attacker.SteamID] = k + 1;
        }

        if (victim.IsBot && victim.TeamNum == (int)CsTeam.CounterTerrorist)
            _session.BotDeaths++;
        else if (!victim.IsBot && victim.TeamNum == (int)CsTeam.Terrorist)
            _session.TerroDeaths++;
        else
            return HookResult.Continue;

        if (_session.BotDeaths >= _session.BotCount || _session.TerroDeaths >= _session.TerroPlayers.Count)
        {
            if (!_session.RoundFinalized)
            {
                _session.RoundFinalized = true;
                FinalizeRound();
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo _)
    {
        if (_session == null) return HookResult.Continue;
        var p = @event.Userid;
        if (p == null || !p.IsValid) return HookResult.Continue;

        if (!p.IsBot) return HookResult.Continue;

        // Guard re-fires : CS2 peut fire OnPlayerSpawn 3-4x par bot.
        // Si ce slot a déjà été traité ce round, il faut IGNORER sans passer
        // par la branche "non géré" (sinon on re-freeze un bot déjà placé/équipé).
        if (_processedSpawnSlots.Contains(p.Slot))
        {
            Console.WriteLine("[PracWay][DEBUG] OnPlayerSpawn re-fire ignoré — " + p.PlayerName + " slot=" + p.Slot);
            return HookResult.Continue;
        }

        if (!_ownerOfBots.ContainsKey(p.Slot))
        {
            // Bot non attendu (respawn d'un bot déjà tué, ou surnuméraire).
            // On le freeze immédiatement pour l'empêcher de rusher, et on l'ignore.
            Console.WriteLine("[PracWay][DEBUG] OnPlayerSpawn — bot non géré " + p.PlayerName + " slot=" + p.Slot + ", freeze+ignore");
            AddTimer(0.3f, () => {
                if (!p.IsValid || !p.IsBot) return;
                FreezeBotAI(p, true);
                ZeroVelocity(p);
            });
            return HookResult.Continue;
        }

        var (entry, money) = _ownerOfBots[p.Slot];
        _processedSpawnSlots.Add(p.Slot);
        _ownerOfBots.Remove(p.Slot);
        Console.WriteLine("[PracWay][DEBUG] OnPlayerSpawn bot géré (1er fire) — " + p.PlayerName + " slot=" + p.Slot);

        // Forcer l'équipe CT (bot_join_team ct parfois ignoré par CS2 en warmup)
        if (p.TeamNum != (int)CsTeam.CounterTerrorist)
            p.ChangeTeam(CsTeam.CounterTerrorist);

        // Ordre exact d'OpenPrefirePrac :
        // t+0.5 : FreezeBot (VPHYSICS) — stabilise le pawn
        // t+0.55 : Teleport + crouch — l'angle est appliqué sur un pawn déjà figé
        // VPHYSICS ne peut plus écraser l'angle car le bot est déjà immobile.
        AddTimer(0.5f, () => {
            if (!p.IsValid || !p.IsBot) return;
            FreezeBotAI(p, true);
        });
        AddTimer(0.55f, () => {
            if (!p.IsValid || !p.IsBot) return;
            TeleportTo(p, entry.Spawn);
            SetCrouch(p, entry.Action == BotAction.Crouch);
            var combatSlot = EquipBot(p, money);
            ForceBotCombatSlot(p, combatSlot);
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerConnect(EventPlayerConnectFull @event, GameEventInfo _)
    {
        if (_session == null) return HookResult.Continue;
        var p = @event.Userid;
        if (p == null || p.IsBot) return HookResult.Continue;

        AddTimer(0.5f, () =>
        {
            if (p.IsValid && _session != null)
            {
                p.ChangeTeam(CsTeam.Spectator);
                p.PrintToChat(P + " Un parcours est en cours. Vous participerez a la prochaine manche si une place est disponible.");
            }
        });
        return HookResult.Continue;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LISTENER CHAT
    // ═══════════════════════════════════════════════════════════════════════════

    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        var msg = info.GetArg(1).Trim();
        if (msg.Length < 2 || (msg[0] != '!' && msg[0] != '/')) return HookResult.Continue;

        var parts = msg[1..].Split(' ', 2);
        var cmd   = parts[0].ToLower();
        var arg   = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "way":        OpenMainMenu(player);        break;
            case "way_stop":   CmdStop(player);             break;
            case "duel_way":   PrintHelp(player);           break;
            case "way_create": CmdWayCreate(player, arg);   break;
            case "wset":       CmdWset(player, arg);        break;
            case "wfin":       CmdWfin(player);             break;

            case "w1": case "w2": case "w3": case "w4": case "w5":
            case "w6": case "w7": case "w8": case "w9":
                CmdShortcut(player, cmd[1..]);
                break;

            default: return HookResult.Continue;
        }
        return HookResult.Handled;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MENU PRINCIPAL
    // ═══════════════════════════════════════════════════════════════════════════

    private void OpenMainMenu(CCSPlayerController? p)
    {
        if (p == null) return;
        SetCtx(p, MenuCtx.Main);
        p.PrintToChat(P + " \x04== MENU PRINCIPAL ==");
        p.PrintToChat(P + " \x0A!w1\x01 Lancer un parcours simple");
        p.PrintToChat(P + " \x0A!w2\x01 Lancer un parcours special");
        p.PrintToChat(P + " \x0A!w3\x01 Creation / gestion des parcours");
        p.PrintToChat(P + " \x0A!w4\x01 Quitter");
    }

    private void HandleMainMenu(CCSPlayerController p, string c)
    {
        switch (c)
        {
            case "1": StartSimpleWay(p);       break;
            case "2": OpenSpecialWayMenu(p);   break;
            case "3": OpenCreateWayMenu(p);    break;
            case "4": SetCtx(p, MenuCtx.None); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PARCOURS SIMPLE
    // ═══════════════════════════════════════════════════════════════════════════

    private void StartSimpleWay(CCSPlayerController p)
    {
        var ready = ReadyZones();
        if (ready.Count == 0)
        {
            p.PrintToChat(P + " \x07Aucun parcours valide. Creez-en un via \x0A!3\x01.");
            return;
        }
        SetCtx(p, MenuCtx.None);
        LaunchWay(new PendingConfig { SpecificWayName = null, RoundLimit = 0, StartMoney = -1 });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PARCOURS SPECIAL
    // ═══════════════════════════════════════════════════════════════════════════

    private void OpenSpecialWayMenu(CCSPlayerController p)
    {
        var ready = ReadyZones();
        if (ready.Count == 0)
        {
            p.PrintToChat(P + " \x07Aucun parcours valide. Creez-en un via \x0A!3\x01.");
            return;
        }

        _pending[p.SteamID] = new PendingConfig { WayListForMenu = ready.Select(z => z.Name).ToList() };
        SetCtx(p, MenuCtx.SpecialWay);
        p.PrintToChat(P + " \x04== CHOIX DU PARCOURS ==");
        for (int i = 0; i < ready.Count; i++)
            p.PrintToChat(P + " \x0A!w" + (i + 1) + "\x01 " + ready[i].Name);
        p.PrintToChat(P + " \x0A!w" + (ready.Count + 1) + "\x01 Different a chaque manche");
    }

    private void HandleSpecialWay(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        if (!int.TryParse(c, out var idx) || idx < 1 || idx > cfg.WayListForMenu.Count + 1) return;
        cfg.SpecificWayName = idx <= cfg.WayListForMenu.Count ? cfg.WayListForMenu[idx - 1] : null;
        OpenSpecialRoundsMenu(p);
    }

    private void OpenSpecialRoundsMenu(CCSPlayerController p)
    {
        SetCtx(p, MenuCtx.SpecialRounds);
        p.PrintToChat(P + " \x04== NOMBRE DE ROUNDS ==");
        p.PrintToChat(P + " \x0A!w1\x01  5 rounds");
        p.PrintToChat(P + " \x0A!w2\x01 10 rounds");
        p.PrintToChat(P + " \x0A!w3\x01 15 rounds");
        p.PrintToChat(P + " \x0A!w4\x01 20 rounds");
        p.PrintToChat(P + " \x0A!w5\x01 Illimites");
    }

    private void HandleSpecialRounds(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        cfg.RoundLimit = c switch { "1" => 5, "2" => 10, "3" => 15, "4" => 20, _ => 0 };
        OpenSpecialMoneyMenu(p);
    }

    private void OpenSpecialMoneyMenu(CCSPlayerController p)
    {
        SetCtx(p, MenuCtx.SpecialMoney);
        p.PrintToChat(P + " \x04== ARGENT DE DEPART ==");
        p.PrintToChat(P + " \x0A!w1\x01  800$");
        p.PrintToChat(P + " \x0A!w2\x01 2000$");
        p.PrintToChat(P + " \x0A!w3\x01 2500$");
        p.PrintToChat(P + " \x0A!w4\x01 3000$");
        p.PrintToChat(P + " \x0A!w5\x01 3500$");
        p.PrintToChat(P + " \x0A!w6\x01 4000$");
        p.PrintToChat(P + " \x0A!w7\x01 4500$");
        p.PrintToChat(P + " \x0A!w8\x01 6000$");
        p.PrintToChat(P + " \x0A!w9\x01 Aleatoire");
    }

    private void HandleSpecialMoney(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        cfg.StartMoney = c switch
        {
            "1" =>  800, "2" => 2000, "3" => 2500, "4" => 3000,
            "5" => 3500, "6" => 4000, "7" => 4500, "8" => 6000,
            _   =>   -1
        };
        _pending.Remove(p.SteamID);
        SetCtx(p, MenuCtx.None);
        LaunchWay(cfg);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MENU CREATION / GESTION
    // ═══════════════════════════════════════════════════════════════════════════

    private void OpenCreateWayMenu(CCSPlayerController p)
    {
        SetCtx(p, MenuCtx.CreateWay);
        p.PrintToChat(P + " \x04== GESTION DES PARCOURS ==");
        p.PrintToChat(P + " \x0A!w1\x01 Creer un nouveau parcours");
        p.PrintToChat(P + " \x0A!w2\x01 Modifier / ajouter des spawns");
        p.PrintToChat(P + " \x0A!w3\x01 Supprimer un parcours");
        p.PrintToChat(P + " \x0A!w4\x01 Retour au menu principal");
    }

    private void HandleCreateWayMenu(CCSPlayerController p, string c)
    {
        switch (c)
        {
            case "1":
                SetCtx(p, MenuCtx.None);
                p.PrintToChat(P + " Tapez \x0A!way_create \"nom_parcours\"\x01 pour creer un nouveau parcours.");
                break;
            case "2": OpenEditWayList(p);   break;
            case "3": OpenDeleteWayList(p); break;
            case "4": OpenMainMenu(p);      break;
        }
    }

    private void OpenEditWayList(CCSPlayerController p)
    {
        var zones = _zones.Values.ToList();
        if (zones.Count == 0) { p.PrintToChat(P + " Aucun parcours existant."); OpenCreateWayMenu(p); return; }

        SetCtx(p, MenuCtx.EditWayList);
        EnsureWizard(p).ZoneNames = zones.Select(z => z.Name).ToList();

        p.PrintToChat(P + " \x04== MODIFIER PARCOURS ==");
        for (int i = 0; i < zones.Count; i++)
        {
            var z  = zones[i];
            var ok = z.IsReady ? "\x0C[OK]" : "\x07[!!]";
            p.PrintToChat(P + " \x0A!w" + (i + 1) + "\x01 " + z.Name +
                " (a:" + z.GetSpawns(SpawnSlot.A).Count +
                " CT1:" + z.GetSpawns(SpawnSlot.CT1).Count +
                " CT2:" + z.GetSpawns(SpawnSlot.CT2).Count + "...) " + ok + "\x01");
        }
    }

    private void HandleEditWayList(CCSPlayerController p, string c)
    {
        if (!_wizards.TryGetValue(p.SteamID, out var wiz)) return;
        if (!int.TryParse(c, out var idx) || idx < 1 || idx > wiz.ZoneNames.Count) return;

        var name = wiz.ZoneNames[idx - 1];
        wiz.EditingZone = name;
        SetEditCheats();
        SetCtx(p, MenuCtx.None);
        p.PrintToChat(P + " Edition de \x0A" + name + "\x01.");
        PrintSpawnStatus(p, name);
        ShowMarkers(name);
        p.PrintToChat(P + " \x0A!wset <a|CT1..CT12> <n> [action]\x01 pour definir les spawns.");
        p.PrintToChat(P + " Actions: \x0Astand  crouch");
        p.PrintToChat(P + " Tapez \x0A!wfin\x01 pour terminer.");
    }

    private void OpenDeleteWayList(CCSPlayerController p)
    {
        var zones = _zones.Values.ToList();
        if (zones.Count == 0) { p.PrintToChat(P + " Aucun parcours existant."); OpenCreateWayMenu(p); return; }

        SetCtx(p, MenuCtx.DeleteWayList);
        EnsureWizard(p).ZoneNames = zones.Select(z => z.Name).ToList();

        p.PrintToChat(P + " \x04== SUPPRIMER PARCOURS ==");
        for (int i = 0; i < zones.Count; i++)
            p.PrintToChat(P + " \x0A!w" + (i + 1) + "\x01 " + zones[i].Name);
    }

    private void HandleDeleteWayList(CCSPlayerController p, string c)
    {
        if (!_wizards.TryGetValue(p.SteamID, out var wiz)) return;
        if (!int.TryParse(c, out var idx) || idx < 1 || idx > wiz.ZoneNames.Count) return;

        var name = wiz.ZoneNames[idx - 1];
        _zones.Remove(name);
        SaveZones();
        p.PrintToChat(P + " Parcours \x07" + name + "\x01 supprime.");
        OpenCreateWayMenu(p);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ROUTAGE DES RACCOURCIS
    // ═══════════════════════════════════════════════════════════════════════════

    private void CmdShortcut(CCSPlayerController? p, string choice)
    {
        if (p == null) return;
        switch (GetCtx(p))
        {
            case MenuCtx.Main:          HandleMainMenu(p, choice);       break;
            case MenuCtx.SpecialWay:    HandleSpecialWay(p, choice);     break;
            case MenuCtx.SpecialRounds: HandleSpecialRounds(p, choice);  break;
            case MenuCtx.SpecialMoney:  HandleSpecialMoney(p, choice);   break;
            case MenuCtx.CreateWay:     HandleCreateWayMenu(p, choice);  break;
            case MenuCtx.EditWayList:   HandleEditWayList(p, choice);    break;
            case MenuCtx.DeleteWayList: HandleDeleteWayList(p, choice);  break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMMANDES DE CREATION DE SPAWNS
    // ═══════════════════════════════════════════════════════════════════════════

    private void CmdWayCreate(CCSPlayerController? p, string arg)
    {
        if (p == null) return;
        var name = arg.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(name))
        {
            p.PrintToChat(P + " Usage: \x0A!way_create \"nom_parcours\"");
            return;
        }
        if (_zones.ContainsKey(name))
        {
            p.PrintToChat(P + " Le parcours \x07" + name + "\x01 existe deja. Choisissez un autre nom.");
            return;
        }

        _zones[name] = new WayZone(name);
        SaveZones();
        var wiz = EnsureWizard(p);
        wiz.EditingZone = name;
        SetEditCheats();

        p.PrintToChat(P + " Parcours \x0A" + name + "\x01 cree !");
        p.PrintToChat(P + " Definissez les spawns avec \x0A!wset <slot> <n> [action]");
        p.PrintToChat(P + " Slots: \x0Aa  CT1  CT2  CT3  CT4  CT5  CT6  CT7  CT8  CT9  CT10  CT11  CT12");
        p.PrintToChat(P + " Actions: \x0Astand  crouch");
        PrintSpawnStatus(p, name);
    }

    private void CmdWset(CCSPlayerController? p, string arg)
    {
        if (p == null) return;

        if (!_wizards.TryGetValue(p.SteamID, out var wiz) || wiz.EditingZone == null)
        {
            p.PrintToChat(P + " Aucun parcours en cours d'edition. Creez ou selectionnez un parcours.");
            return;
        }

        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { p.PrintToChat(P + " Usage: \x0A!wset <slot> <n> [action]"); return; }

        if (!TryParseSlot(parts[0], out var slot))
        {
            p.PrintToChat(P + " Slot invalide. Valeurs: \x0Aa  CT1..CT12");
            return;
        }
        if (!int.TryParse(parts[1], out var num) || num < 1)
        {
            p.PrintToChat(P + " Numero invalide (>= 1).");
            return;
        }
        if (!TryGetCurrentSpawn(p, out var spawn))
        {
            p.PrintToChat(P + " Impossible de lire votre position.");
            return;
        }

        var zoneName  = wiz.EditingZone;
        if (!_zones.TryGetValue(zoneName, out var zone))
        {
            p.PrintToChat(P + " \x07Le parcours \x0A" + zoneName + "\x01 n'existe plus (supprime entre-temps ?).");
            wiz.EditingZone = null;
            SetEditCheats();
            return;
        }
        var spawns    = zone.GetSpawns(slot);
        int idx       = num - 1;
        var actionStr = parts.Length >= 3 ? parts[2].ToLower() : "stand";

        //
        BotAction action = actionStr == "crouch" ? BotAction.Crouch : BotAction.Stand;

        zone.SetSpawnFull(slot, idx, spawn, action);
        SaveZones();

        p.PrintToChat(P + " Spawn \x0A" + SlotLabel(slot) + "-" + num + "\x01 [" + ActionLabel(action) + "] enregistre.");

        PrintSpawnStatus(p, zoneName);
        ShowMarkers(zoneName);
    }

    private void CmdWfin(CCSPlayerController? p)
    {
        if (p == null) return;
        if (_wizards.TryGetValue(p.SteamID, out var wiz) && wiz.EditingZone != null)
        {
            var name  = wiz.EditingZone;
            var ready = _zones.TryGetValue(name, out var z) && z.IsReady;
            wiz.EditingZone = null;
            SetEditCheats();
            p.PrintToChat(P + " Fin d'edition pour \x0A" + name + "\x01." +
                (ready ? " \x0CParcours pret !" : " \x07Parcours incomplet."));
        }
        ClearMarkers();
        OpenCreateWayMenu(p);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LOGIQUE DE SESSION
    // ═══════════════════════════════════════════════════════════════════════════

    private void LaunchWay(PendingConfig cfg)
    {
        Console.WriteLine("[PracWay][DEBUG] LaunchWay — debut");
        ClearMarkers();
        _killCount.Clear();
        _session = new ActiveSession(cfg);
        Server.ExecuteCommand("exec PracWay/pracway.cfg");
        Server.ExecuteCommand("bot_quota 0");
        Server.ExecuteCommand("bot_quota_mode normal");
        Server.ExecuteCommand("mp_warmup_pausetimer 1");
        Server.ExecuteCommand("mp_warmup_start");
        // Pas de bot_kick ici : un kick en warmup déclenche des respawns en cascade.
        // _activeBotSlots est vide au round 1 → StartNextRound fera bot_add_ct x5.
        // Les éventuels bots résidus seront capturés comme surnuméraires et ignorés.
        Console.WriteLine("[PracWay][DEBUG] LaunchWay — StartNextRound dans 1f");
        AddTimer(1f, () =>
        {
            if (_session != null) StartNextRound();
        });
        Console.WriteLine("[PracWay][DEBUG] LaunchWay — fin");
    }

    private void StartNextRound()
    {
        if (_session == null) return;
        if (_startingRound) return;
        _startingRound = true;
        Console.WriteLine("[PracWay][DEBUG] StartNextRound — debut (round " + (_session.RoundNumber + 1) + ")");

        StopBotTimers();
        _killCount.Clear();

        if (_session.Config.RoundLimit > 0 && _session.RoundNumber >= _session.Config.RoundLimit)
        {
            Server.PrintToChatAll(P + " \x0CLimite de rounds atteinte !");
            EndSession();
            _startingRound = false;
            return;
        }

        var humans = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot).ToList();
        var terros = humans.Take(MaxTerros).ToList();
        Console.WriteLine("[PracWay][DEBUG] StartNextRound — " + humans.Count + " humains, " + terros.Count + " terros");

        if (terros.Count == 0)
        {
            Server.PrintToChatAll(P + " \x07Aucun joueur present. Parcours annule.");
            EndSession();
            _startingRound = false;
            return;
        }

        string? wayName = _session.Config.SpecificWayName;
        if (wayName == null || !_zones.ContainsKey(wayName))
        {
            var ready = ReadyZones();
            if (ready.Count == 0) { Server.PrintToChatAll(P + " \x07Aucun parcours valide."); EndSession(); _startingRound = false; return; }

            var candidates = _session.LastRoundWon || _session.RoundNumber == 0
                ? ready.Where(z => z.Name != _session.CurrentWayName).ToList()
                : ready.Where(z => z.Name == _session.CurrentWayName).ToList();

            if (candidates.Count == 0) candidates = ready;
            wayName = candidates[Random.Shared.Next(candidates.Count)].Name;
        }

        var zone = _zones[wayName];
        _session.CurrentWayName = wayName;
        _session.TerroPlayers   = terros;
        _session.TerroDeaths    = 0;
        _session.BotDeaths      = 0;
        _session.RoundNumber++;

        int money = _session.Config.StartMoney >= 0 ? _session.Config.StartMoney : RandomMoney();
        Console.WriteLine("[PracWay][DEBUG] StartNextRound — wayName=" + wayName + " money=" + money);

        // Placement joueurs Terro — on vérifie que le pawn est valide avant toute manipulation.
        Console.WriteLine("[PracWay][DEBUG] StartNextRound — ChangeTeam Terrorist debut");
        foreach (var pl in humans)
        {
            if (!pl.IsValid) continue;
            var plPawn = pl.PlayerPawn?.Value;
            if (plPawn == null || plPawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) continue;
            Console.WriteLine("[PracWay][DEBUG] ChangeTeam T -> " + pl.PlayerName + " LifeState=" + plPawn.LifeState);
            pl.ChangeTeam(CsTeam.Terrorist);
        }
        Console.WriteLine("[PracWay][DEBUG] StartNextRound — ChangeTeam Terrorist fin");

        var aSpawns = Shuffle(zone.GetSpawns(SpawnSlot.A).ToList());
        Console.WriteLine("[PracWay][DEBUG] StartNextRound — TeleportTo debut (" + aSpawns.Count + " spawns A)");
        for (int i = 0; i < terros.Count; i++)
        {
            if (!terros[i].IsValid) continue;
            var spawnEntry = i < aSpawns.Count ? aSpawns[i] : aSpawns[0];
            var capturedPl = terros[i];
            var capturedMoney = money;
            Console.WriteLine("[PracWay][DEBUG] TeleportTo -> " + capturedPl.PlayerName);
            // Retry : le pawn peut ne pas encore être alive (premier round)
            AddTimer(0.15f, () => {
                if (!capturedPl.IsValid) return;
                var tp = capturedPl.PlayerPawn?.Value;
                if (tp != null && tp.LifeState == (byte)LifeState_t.LIFE_ALIVE)
                {
                    TeleportTo(capturedPl, spawnEntry.Spawn);
                    SetMoney(capturedPl, capturedMoney);
                }
                else
                {
                    // 2e tentative à +0.5f
                    AddTimer(0.5f, () => {
                        if (!capturedPl.IsValid) return;
                        var tp2 = capturedPl.PlayerPawn?.Value;
                        if (tp2 == null) return;
                        TeleportTo(capturedPl, spawnEntry.Spawn);
                        SetMoney(capturedPl, capturedMoney);
                    });
                }
            });
        }
        Console.WriteLine("[PracWay][DEBUG] StartNextRound — TeleportTo fin");

        // Reset HP à 100 pour chaque joueur au début du round
        AddTimer(0.1f, () => {
            if (_session == null) return;
            foreach (var pl in _session.TerroPlayers)
            {
                if (!pl.IsValid || !pl.PawnIsAlive || pl.Pawn?.Value == null) continue;
                pl.Pawn.Value.Health = 100;
                Utilities.SetStateChanged(pl.Pawn.Value, "CBaseEntity", "m_iHealth");
            }
        });

        // Joueurs en excès -> spectateur
        foreach (var pl in humans.Skip(MaxTerros))
        {
            if (!pl.IsValid) continue;
            pl.ChangeTeam(CsTeam.Spectator);
            pl.PrintToChat(P + " Places Terro completes, vous etes en spectateur.");
        }

        // Nettoyer les armes au sol
        foreach (var weapon in Utilities.FindAllEntitiesByDesignerName<CCSWeaponBase>("weapon_"))
            if (weapon.IsValid && weapon.OwnerEntity.Value == null)
                weapon.Remove();

        // Construire la liste des entrées spawn pour ce round
        var newEntries = new List<(SpawnEntry entry, int money)>();
        foreach (var slot in zone.ConfiguredBotSlots)
        {
            var spawns = zone.GetSpawns(slot);
            newEntries.Add((spawns[Random.Shared.Next(spawns.Count)], money));
        }
        _session.BotCount = newEntries.Count;
        Console.WriteLine("[PracWay][DEBUG] StartNextRound — " + _session.BotCount + " bots demandés");

        // Réassigner les entrées aux bots déjà sur le serveur (ils vont respawner via CommitSuicide)
        // AVANT le respawn pour que _ownerOfBots soit prêt quand OnPlayerSpawn fire.
        var existingSlots = _activeBotSlots
            .Where(slot =>
            {
                var bot = Utilities.GetPlayerFromSlot(slot);
                return bot != null && bot.IsValid && bot.IsBot;
            })
            .ToList();
        _activeBotSlots.Clear();
        int assignIdx = 0;
        foreach (var s in existingSlots)
        {
            if (assignIdx >= newEntries.Count) break;
            var assignment = newEntries[assignIdx++];
            _ownerOfBots[s] = assignment;
            _activeBotSlots.Add(s);
            var bot = Utilities.GetPlayerFromSlot(s);
            if (bot == null || !bot.IsValid || !bot.IsBot || !bot.PawnIsAlive) continue;

            // Bot déjà vivant au changement de round: le préparer immédiatement
            // au lieu d'attendre un spawn event qui peut ne jamais arriver.
            _processedSpawnSlots.Add(s);
            _ownerOfBots.Remove(s);
            AddTimer(0.5f, () =>
            {
                if (!bot.IsValid || !bot.IsBot) return;
                FreezeBotAI(bot, true);
            });
            AddTimer(0.55f, () =>
            {
                if (!bot.IsValid || !bot.IsBot) return;
                TeleportTo(bot, assignment.entry.Spawn);
                SetCrouch(bot, assignment.entry.Action == BotAction.Crouch);
                var combatSlot = EquipBot(bot, assignment.money);
                ForceBotCombatSlot(bot, combatSlot);
            });
        }

        // Calculer combien de nouveaux bots il faut ajouter
        int neededNew = newEntries.Count - assignIdx;
        _botRequestCount = neededNew;

        // Stocker les entrées restantes dans _pendingBotSlots pour OnClientPutInServer
        for (int i = assignIdx; i < newEntries.Count; i++)
            _pendingBotSlots[i - assignIdx] = newEntries[i];

        // IMPORTANT: en round actif, bot_quota doit refléter le nombre de bots voulu.
        // bot_quota 0 déclenche des kicks asynchrones et casse la manche suivante.
        Server.ExecuteCommand("bot_quota_mode fill");
        Server.ExecuteCommand("bot_quota " + _session.BotCount);
        Server.ExecuteCommand("bot_join_team ct");

        if (neededNew > 0)
        {
            Console.WriteLine("[PracWay][DEBUG] attente auto-fill de " + neededNew + " bots (bots existants=" + existingSlots.Count + ")");
        }
        else
        {
            Console.WriteLine("[PracWay][DEBUG] tous les bots réutilisés (existants=" + existingSlots.Count + "), pas de bot_add_ct");
        }

        Console.WriteLine("[PracWay][DEBUG] StartNextRound — RoundTimer start");
        _session.RoundTimer?.Kill();
        _session.RoundTimerPaused   = false;
        _session.RoundTimeRemaining = RoundDuration;
        _session.RoundStopwatch     = System.Diagnostics.Stopwatch.StartNew();
        _session.RoundTimer = AddTimer(RoundDuration, () =>
        {
            if (_session != null) { Server.PrintToChatAll(P + " \x07Temps ecoule !"); FinalizeRound(); }
        });

        Console.WriteLine("[PracWay][DEBUG] StartNextRound — fin OK");
        Server.PrintToChatAll(P + " Manche \x04" + _session.RoundNumber +
            "\x01 -- \x0A" + wayName + "\x01 -- " + terros.Count + "v" + _session.BotCount + " bots CT -- " + money + "$");
        _startingRound = false;
    }

    private void FinalizeRound()
    {
        if (_session == null) return;
        if (_roundTransitionPending) return;
        _roundTransitionPending = true;
        // RoundFinalized est déjà positionné par l'appelant (OnRoundEnd ou OnPlayerDeath).
        // On ne le reteste pas ici pour éviter la double-guard.

        StopBotTimers();
        _session.RoundTimer?.Kill();
        _session.RoundTimer = null;

        bool won = _session.BotDeaths >= _session.BotCount;
        _session.LastRoundWon = won;

        if (won)
            Server.PrintToChatAll(P + " \x0CParcours reussi ! Prochain dans 3 secondes...");
        else
            Server.PrintToChatAll(P + " \x07Parcours echoue. Nouvelle tentative dans 3 secondes...");

        PrintKillCount();

        // Calculer le délai nécessaire : les kicks de StopBotTimers sont déjà partis,
        // mais CS2 les traite de façon asynchrone. On attend que tous soient effectifs
        // avant de lancer bot_add_ct du round suivant.
        // Délai = 3s (annonce) + 1s (buffer kicks CS2)
        AddTimer(4.0f, () =>
        {
            _roundTransitionPending = false;
            if (_session == null) return;
            _session.RoundFinalized = false;
            StartNextRound();
        });
    }

    private void CmdStop(CCSPlayerController? caller)
    {
        if (_session == null) { caller?.PrintToChat(P + " Aucun parcours en cours."); return; }
        Server.PrintToChatAll(P + " Parcours arrete par \x04" + (caller?.PlayerName ?? "le serveur") + "\x01.");
        EndSession();
    }

    private void EndSession()
    {
        if (_session == null) return;
        _session.RoundTimer?.Kill();
        _session = null;
        _roundTransitionPending = false;
        _startingRound = false;
        StopBotTimers();
        // Sortir du warmup d'abord, PUIS kicker — hors warmup bot_quota 0 n'a plus d'effet
        // donc le kick ne provoquera pas de respawn automatique.
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_warmup_end");
        AddTimer(0.5f, () => Server.ExecuteCommand("bot_kick"));
        PrintKillCount();
        _killCount.Clear();
        Server.PrintToChatAll(P + " \x04== FIN DU PARCOURS ==");
    }

    private void PrintKillCount()
    {
        if (_killCount.Count == 0) return;
        Server.PrintToChatAll(P + " \x04== KILLS ==");
        foreach (var kv in _killCount.OrderByDescending(x => x.Value))
        {
            var pl   = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && !p.IsBot && p.SteamID == kv.Key);
            var name = pl?.PlayerName ?? "#" + kv.Key;
            Server.PrintToChatAll(P + " \x0A" + name + "\x01 : \x04" + kv.Value + "\x01 kill(s)");
        }
    }

    private static void KickBot(int slot)
    {
        var bot = Utilities.GetPlayerFromSlot(slot);
        if (bot == null || !bot.IsValid || !bot.IsBot) return;
        Server.ExecuteCommand("bot_kick " + bot.PlayerName);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EQUIPEMENT BOTS
    // ═══════════════════════════════════════════════════════════════════════════

    private static string EquipBot(CCSPlayerController bot, int money)
    {
        bot.RemoveWeapons();
        bot.GiveNamedItem("weapon_knife");
        bot.GiveNamedItem("item_kevlar");

        if (money <= 800)
        {
            bot.GiveNamedItem("weapon_usp_silencer");
            return "slot2";
        }
        if (money <= 2000) bot.GiveNamedItem("weapon_mp9");
        else if (money <= 2500) bot.GiveNamedItem("weapon_mp5sd");
        else if (money <= 3000) { bot.GiveNamedItem("item_assaultsuit"); bot.GiveNamedItem("weapon_famas"); }
        else if (money <= 3500) { bot.GiveNamedItem("item_assaultsuit"); bot.GiveNamedItem(Coin() ? "weapon_xm1014" : "weapon_p90"); }
        else                    { bot.GiveNamedItem("item_assaultsuit"); bot.GiveNamedItem(Coin() ? "weapon_m4a1_s" : "weapon_m4a4"); }

        return "slot1";
    }

    private void ForceBotCombatSlot(CCSPlayerController bot, string slotCmd)
    {
        if (!bot.IsValid || !bot.IsBot) return;

        // Plusieurs tentatives: l'inventaire peut être reconstruit juste après le spawn.
        bot.ExecuteClientCommandFromServer(slotCmd);
        AddTimer(0.15f, () =>
        {
            if (!bot.IsValid || !bot.IsBot) return;
            bot.ExecuteClientCommandFromServer(slotCmd);
        });
        AddTimer(0.40f, () =>
        {
            if (!bot.IsValid || !bot.IsBot) return;
            bot.ExecuteClientCommandFromServer(slotCmd);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ACTIONS BOTS
    // ═══════════════════════════════════════════════════════════════════════════

    private void ApplyBotAction(CCSPlayerController bot, SpawnEntry entry)
    {
        // Gardé pour compatibilité — l'ordre principal est géré dans OnPlayerSpawn.
        if (!bot.IsValid || !bot.IsBot) return;
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
        Console.WriteLine("[PracWay][DEBUG] ApplyBotAction — " + bot.PlayerName + " action=" + entry.Action);
        FreezeBotAI(bot, true);
        TeleportTo(bot, entry.Spawn);
        SetCrouch(bot, entry.Action == BotAction.Crouch);
        ZeroVelocity(bot);
        bot.ExecuteClientCommandFromServer("slot1");
    }



    private void StopBotTimers()
    {
        _processedSpawnSlots.Clear();
        _ownerOfBots.Clear();
        _pendingBotSlots.Clear();
        _botRequestCount = 0;

        // Ne pas vider _activeBotSlots ici — on en a besoin dans StartNextRound
        // pour réassigner les entrées spawn aux slots existants.
    }


    // ═══════════════════════════════════════════════════════════════════════════
    // MARQUEURS VISUELS
    // ═══════════════════════════════════════════════════════════════════════════

    private void ShowMarkers(string zoneName)
    {
        ClearMarkers();
        if (!_zones.TryGetValue(zoneName, out var zone)) return;

        var colors = new Dictionary<SpawnSlot, (byte r, byte g, byte b)>
        {
            [SpawnSlot.A]   = (0,   120, 255),
            [SpawnSlot.CT1] = (0,   220, 0  ),
            [SpawnSlot.CT2] = (220, 220, 0  ),
            [SpawnSlot.CT3] = (0,   220, 220),
            [SpawnSlot.CT4] = (180, 0,   255),
            [SpawnSlot.CT5] = (255, 255, 255),
            [SpawnSlot.CT6] = (255, 128, 0  ),
            [SpawnSlot.CT7] = (255, 0,   128),
            [SpawnSlot.CT8] = (128, 255, 0  ),
            [SpawnSlot.CT9] = (0,   128, 255),
            [SpawnSlot.CT10] = (128, 128, 255),
            [SpawnSlot.CT11] = (255, 128, 128),
            [SpawnSlot.CT12] = (128, 255, 255),
        };

        foreach (var (slot, (r, g, b)) in colors)
        {
            foreach (var entry in zone.GetSpawns(slot))
            {
                float h = entry.Action == BotAction.Crouch ? 100f : 200f;
                var beam = MakeBeam(entry.Spawn, r, g, b, h);
                if (beam != null) _markers.Add(beam);

            }
        }
    }

    private static CBeam? MakeBeam(WaySpawn s, byte r, byte g, byte b, float height)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null || !beam.IsValid) return null;
        beam.RenderMode = RenderMode_t.kRenderTransColor;
        beam.Render     = Color.FromArgb(255, r, g, b);
        beam.Width      = 8f;
        beam.EndWidth   = 8f;
        beam.LifeState  = 1;
        beam.Amplitude  = 0f;
        beam.Speed      = 0f;
        beam.FadeLength = 0f;
        beam.Flags      = 0;
        beam.BeamType   = BeamType_t.BEAM_POINTS;
        beam.EndPos.X   = s.X;
        beam.EndPos.Y   = s.Y;
        beam.EndPos.Z   = s.Z + height;
        beam.Teleport(new Vector(s.X, s.Y, s.Z), new QAngle(IntPtr.Zero), new Vector(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        beam.DispatchSpawn();
        return beam;
    }

    private void ClearMarkers()
    {
        foreach (var b in _markers) if (b.IsValid) b.Remove();
        _markers.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // AIDE
    // ═══════════════════════════════════════════════════════════════════════════

    private static void PrintHelp(CCSPlayerController? p)
    {
        if (p == null) return;
        p.PrintToChat(P + " \x04== AIDE PRACWAY ==");
        p.PrintToChat(P + " \x0A!way\x01 -- Menu principal");
        p.PrintToChat(P + " \x0A!way_stop\x01 -- Arreter le parcours");
        p.PrintToChat(P + " \x0A!way_create \"nom\"\x01 -- Creer un parcours");
        p.PrintToChat(P + " \x0A!wset <slot> <n> [action]\x01 -- Definir un spawn");
        p.PrintToChat(P + "   Slots: \x0Aa  CT1  CT2  CT3  CT4  CT5  CT6  CT7  CT8  CT9  CT10  CT11  CT12");
        p.PrintToChat(P + "   Actions: \x0Astand  crouch");
        p.PrintToChat(P + " \x0A!wfin\x01 -- Terminer l'edition");
        p.PrintToChat(P + " \x0A!duel_way\x01 -- Afficher cette aide");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PERSISTENCE JSON
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private void LoadZones()
    {
        var path = ZoneFilePath();
        if (!File.Exists(path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<List<ZoneDto>>(File.ReadAllText(path));
            if (data == null) return;
            foreach (var d in data)
            {
                var z = new WayZone(d.Name);
                foreach (var kv in d.Slots)
                {
                    if (!TryParseSlot(kv.Key, out var slot)) continue;
                    foreach (var e in kv.Value) z.AddSpawn(slot, e.ToEntry());
                }
                _zones[z.Name] = z;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PracWay] ERREUR chargement zones ({path}): {ex.Message}");
        }
    }

    private void SaveZones()
    {
        var path = ZoneFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = _zones.Values.Select(z =>
        {
            var slots = new Dictionary<string, List<SpawnEntryDto>>();
            foreach (SpawnSlot slot in Enum.GetValues<SpawnSlot>())
            {
                var spawns = z.GetSpawns(slot);
                if (spawns.Count > 0)
                    slots[SlotLabel(slot)] = spawns.Select(SpawnEntryDto.From).ToList();
            }
            return new ZoneDto { Name = z.Name, Slots = slots };
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOpts));
    }

    private string ZoneFilePath() =>
        Path.Combine(ModuleDirectory, "..", "..", "..", "configs", "PracWay", "zones", Server.MapName + ".json");

    // ─── DTOs JSON ─────────────────────────────────────────────────────────────

    private sealed class ZoneDto
    {
        public string Name  { get; set; } = "";
        public Dictionary<string, List<SpawnEntryDto>> Slots { get; set; } = new();
    }

    private sealed class SpawnEntryDto
    {
        public float  X     { get; set; } public float Y   { get; set; } public float Z    { get; set; }
        public float  Pitch { get; set; } public float Yaw { get; set; } public float Roll  { get; set; }
        public string Action { get; set; } = "stand";


        public SpawnEntry ToEntry()
        {
            TryParseAction(Action, out var a);
            return new SpawnEntry { Spawn = new WaySpawn(X, Y, Z, Pitch, Yaw, Roll), Action = a };
        }

        public static SpawnEntryDto From(SpawnEntry e) => new()
        {
            X = e.Spawn.X, Y = e.Spawn.Y, Z = e.Spawn.Z,
            Pitch = e.Spawn.Pitch, Yaw = e.Spawn.Yaw, Roll = e.Spawn.Roll,
            Action = ActionLabel(e.Action),
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UTILITAIRES
    // ═══════════════════════════════════════════════════════════════════════════

    private List<WayZone> ReadyZones() => _zones.Values.Where(z => z.IsReady).ToList();

    private MenuCtx GetCtx(CCSPlayerController p) =>
        _menuCtx.TryGetValue(p.SteamID, out var c) ? c : MenuCtx.None;

    private void SetCtx(CCSPlayerController p, MenuCtx ctx)
    {
        _menuCtx[p.SteamID] = ctx;
        // Nettoyage de la config en attente quand le joueur quitte le flux special.
        if (ctx == MenuCtx.None || ctx == MenuCtx.Main)
            _pending.Remove(p.SteamID);
    }

    private ZoneWizard EnsureWizard(CCSPlayerController p)
    {
        if (!_wizards.TryGetValue(p.SteamID, out var w)) { w = new ZoneWizard(); _wizards[p.SteamID] = w; }
        return w;
    }

    private void SetEditCheats()
    {
        bool editing = _wizards.Values.Any(w => !string.IsNullOrWhiteSpace(w.EditingZone));
        Server.ExecuteCommand("sv_cheats " + (editing ? "1" : "0"));

        if (_session == null) return;

        if (editing && !_session.RoundTimerPaused)
        {
            // Pause : on calcule le temps restant et on tue le timer actif.
            _session.RoundTimerPaused = true;
            _session.RoundTimer?.Kill();
            _session.RoundTimer = null;

            float elapsed = _session.RoundStopwatch != null
                ? (float)_session.RoundStopwatch.Elapsed.TotalSeconds
                : 0f;
            _session.RoundTimeRemaining = Math.Max(0f, _session.RoundTimeRemaining - elapsed);
            _session.RoundStopwatch = null;

            Server.PrintToChatAll(P + " \x04[Timer en pause — edition de parcours en cours]");
        }
        else if (!editing && _session.RoundTimerPaused)
        {
            // Reprise : relance le timer avec le temps restant.
            _session.RoundTimerPaused = false;
            float remaining = _session.RoundTimeRemaining;

            _session.RoundStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _session.RoundTimer = AddTimer(remaining, () =>
            {
                if (_session != null) { Server.PrintToChatAll(P + " \x07Temps ecoule !"); FinalizeRound(); }
            });

            int mins = (int)remaining / 60;
            int secs = (int)remaining % 60;
            Server.PrintToChatAll(P + " \x04[Timer repris — " + mins + ":" + secs.ToString("D2") + " restantes]");
        }
    }

    private static void FreezeBotAI(CCSPlayerController bot, bool freeze)
    {
        if (!bot.IsValid || !bot.IsBot) return;
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;

        if (freeze)
        {
            // MOVETYPE_VPHYSICS (5) est la méthode stable sur CS2.
            // MOVETYPE_NONE crashe le moteur natif (cf. OpenPrefirePrac).
            pawn.MoveType = MoveType_t.MOVETYPE_VPHYSICS;
            Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", 5);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
            ZeroVelocity(bot);
        }
        else
        {
            pawn.MoveType = MoveType_t.MOVETYPE_WALK;
            Schema.SetSchemaValue(pawn.Handle, "CBaseEntity", "m_nActualMoveType", 2);
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_MoveType");
        }
    }

    private static void ZeroVelocity(CCSPlayerController bot)
    {
        if (!bot.IsValid || !bot.IsBot) return;
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE) return;
        pawn.AbsVelocity.X = 0f;
        pawn.AbsVelocity.Y = 0f;
        pawn.AbsVelocity.Z = 0f;
    }

    private static void SetCrouch(CCSPlayerController bot, bool crouch)
    {
        // Modifier FL_DUCKING directement crashe CS2.
        // Méthode stable : MovementServices.DuckAmount + Bot.IsCrouching (cf. OpenPrefirePrac).
        if (!bot.IsValid || !bot.IsBot) return;
        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE || pawn.MovementServices == null) return;
        var movementService = new CCSPlayer_MovementServices(pawn.MovementServices.Handle);
        movementService.DuckAmount = crouch ? 1 : 0;
        if (pawn.Bot != null) pawn.Bot.IsCrouching = crouch;
    }

    private static void TeleportTo(CCSPlayerController p, WaySpawn s)
    {
        var pawn = p.PlayerPawn?.Value;
        if (pawn == null) return;
        pawn.Teleport(new Vector(s.X, s.Y, s.Z), new QAngle(s.Pitch, s.Yaw, s.Roll), Vector.Zero);
    }

    private static void SetMoney(CCSPlayerController p, int amount)
    {
        if (p.InGameMoneyServices != null) p.InGameMoneyServices.Account = amount;
    }

    private static bool TryGetCurrentSpawn(CCSPlayerController p, out WaySpawn s)
    {
        s = default;
        var pawn = p.PlayerPawn?.Value;
        if (pawn == null) return false;
        var pos = pawn.CBodyComponent?.SceneNode?.AbsOrigin;
        var ang = pawn.V_angle;
        if (pos == null || ang == null) return false;
        s = new WaySpawn(pos.X, pos.Y, pos.Z, ang.X, ang.Y, ang.Z);
        return true;
    }

    private static bool TryParseSlot(string s, out SpawnSlot slot)
    {
        slot = SpawnSlot.A;
        switch (s.ToLower())
        {
            case "a":   slot = SpawnSlot.A;   return true;
            case "ct1": slot = SpawnSlot.CT1; return true;
            case "ct2": slot = SpawnSlot.CT2; return true;
            case "ct3": slot = SpawnSlot.CT3; return true;
            case "ct4": slot = SpawnSlot.CT4; return true;
            case "ct5": slot = SpawnSlot.CT5; return true;
            case "ct6": slot = SpawnSlot.CT6; return true;
            case "ct7": slot = SpawnSlot.CT7; return true;
            case "ct8": slot = SpawnSlot.CT8; return true;
            case "ct9": slot = SpawnSlot.CT9; return true;
            case "ct10": slot = SpawnSlot.CT10; return true;
            case "ct11": slot = SpawnSlot.CT11; return true;
            case "ct12": slot = SpawnSlot.CT12; return true;
            default:                          return false;
        }
    }

    private static bool TryParseAction(string s, out BotAction a)
    {
        a = BotAction.Stand;
        switch (s.ToLower())
        {
            case "stand":  a = BotAction.Stand;  return true;
            case "crouch": a = BotAction.Crouch; return true;
            default:                              return false;
        }
    }

    private static string SlotLabel(SpawnSlot s) => s switch
    {
        SpawnSlot.A   => "a",
        SpawnSlot.CT1 => "CT1", SpawnSlot.CT2 => "CT2", SpawnSlot.CT3 => "CT3",
        SpawnSlot.CT4 => "CT4", SpawnSlot.CT5 => "CT5", SpawnSlot.CT6 => "CT6",
        SpawnSlot.CT7 => "CT7", SpawnSlot.CT8 => "CT8", SpawnSlot.CT9 => "CT9",
        SpawnSlot.CT10 => "CT10", SpawnSlot.CT11 => "CT11", SpawnSlot.CT12 => "CT12", _ => "?"
    };

    private static string ActionLabel(BotAction a) => a switch
    {
        BotAction.Crouch => "crouch",
        _                => "stand"
    };

    private static List<T> Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private static int  RandomMoney() => MoneyOptions[Random.Shared.Next(MoneyOptions.Length)];
    private static bool Coin()        => Random.Shared.Next(2) == 0;

    private void PrintSpawnStatus(CCSPlayerController p, string zoneName)
    {
        if (!_zones.TryGetValue(zoneName, out var zone)) return;
        var slots = new[]
        {
            SpawnSlot.A, SpawnSlot.CT1, SpawnSlot.CT2, SpawnSlot.CT3, SpawnSlot.CT4, SpawnSlot.CT5, SpawnSlot.CT6,
            SpawnSlot.CT7, SpawnSlot.CT8, SpawnSlot.CT9, SpawnSlot.CT10, SpawnSlot.CT11, SpawnSlot.CT12
        };
        var mins = Enumerable.Repeat(MinCtSpawns, slots.Length).ToArray();
        mins[0] = MinASpawns;
        p.PrintToChat(P + " \x04== SPAWNS ==");
        for (int i = 0; i < slots.Length; i++)
        {
            int cnt   = zone.GetSpawns(slots[i]).Count;
            int need  = mins[i];
            string col = cnt >= need ? "\x0C" : "\x07";
            p.PrintToChat(P + " " + SlotLabel(slots[i]) + ": " + col + cnt + "/" + need + "\x01");
        }
        if (zone.IsReady)
            p.PrintToChat(P + " \x0CParcours valide ! Tapez \x0A!wfin\x01 pour terminer.");
        else
            p.PrintToChat(P + " \x07Spawns manquants. Continuez avec \x0A!wset\x01.");
    }
}
