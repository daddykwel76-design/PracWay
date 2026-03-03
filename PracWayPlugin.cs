using System.Drawing;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace PracWay;

public sealed class PracWayPlugin : BasePlugin
{
    public override string ModuleName        => "PracWay";
    public override string ModuleVersion     => "2.0.0";
    public override string ModuleAuthor      => "Codex";
    public override string ModuleDescription => "Parcours Practice — joueurs Terro vs bots CT.";

    // ─── Chat prefix ───────────────────────────────────────────────────────────
    private const string P = " \x07[PRACWAY]\x01";

    // ─── Constantes ────────────────────────────────────────────────────────────
    private const int RoundDuration = 90; // 1 min 30

    // Minimums de spawns par slot pour qu'un parcours soit valide
    private const int MinA   = 3;
    private const int MinCT1 = 1;
    private const int MinCT2 = 1;
    private const int MinCT3 = 1;
    private const int MinCT4 = 1;
    private const int MinCT5 = 1;
    private const int MinJ1  = 1;
    private const int MinJ2  = 1;

    // ─── Slots spawn ───────────────────────────────────────────────────────────
    private enum SpawnSlot { A, J1, J2, CT1, CT2, CT3, CT4, CT5 }

    // ─── Actions bots CT ───────────────────────────────────────────────────────
    // Les actions *A définissent le point de départ, *B le point d'arrivée
    private enum BotAction { Stand, Crouch, CrouchPulse, PeekAB, PeekCrouch, FlashPeek }

    // ─── Argents disponibles ───────────────────────────────────────────────────
    private static readonly int[] MoneyOptions = { 800, 2000, 2500, 3000, 3500, 4000, 4500, 6000 };

    // ─── State ─────────────────────────────────────────────────────────────────
    private readonly Dictionary<string, WayZone>      _zones    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, ZoneWizard>    _wizards  = new();
    private readonly Dictionary<ulong, MenuContext>   _menuCtx  = new();
    private readonly Dictionary<ulong, PendingConfig> _pending  = new();
    private readonly List<CBeam>                      _spawnMarkers = new();
    private readonly Dictionary<ulong, int>           _killCount    = new();
    private readonly Dictionary<int, CounterStrikeSharp.API.Modules.Timers.Timer?> _botTimers = new();

    private ActiveSession? _session;
    private bool _roundStartHandled = false;

    // ───────────────────────────────────────────────────────────────────────────
    public override void Load(bool hotReload)
    {
        AddCommand("css_way",      "Menu principal PracWay", (c, _) => OpenMainMenu(c));
        AddCommand("css_way_stop", "Arreter le parcours",    (c, _) => StopWay(c));
        AddCommand("css_way_help", "Aide PracWay",           (c, _) => PrintHelp(c));

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Pre);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect);
		RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn, HookMode.Post);
        AddCommandListener("say",      OnPlayerSay);
        AddCommandListener("say_team", OnPlayerSay);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CHAT LISTENER  (meme structure que DuelPlugin)
    // ═══════════════════════════════════════════════════════════════════════════

    private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        var msg = info.GetArg(1).Trim();
        if (msg.Length < 2 || (msg[0] != '!' && msg[0] != '/')) return HookResult.Continue;

        var parts = msg[1..].Split(' ', 2);
        var cmd   = parts[0].ToLower();
        var arg   = parts.Length > 1 ? parts[1] : "";

        switch (cmd)
        {
            case "way":      OpenMainMenu(player);          break;
            case "way_stop": StopWay(player);               break;
            case "way_help": PrintHelp(player);             break;
            case "way_new":  CmdWayFromChat(player, arg);   break;
            case "wset":     CmdSetFromChat(player, arg);     break;
            case "wfin":     CmdFin(player);                  break;
            case "w1": case "w2": case "w3": case "w4":
            case "w5": case "w6": case "w7": case "w8": case "w9":
                CmdShortcut(player, cmd[1..]);  // retire le "w" → "1".."9"
                break;
            default: return HookResult.Continue;
        }

        return HookResult.Handled;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MAP / ROUND EVENTS
    // ═══════════════════════════════════════════════════════════════════════════

    private void OnMapStart(string _)
    {
        _session = null;
        _zones.Clear();
        _wizards.Clear();
        _killCount.Clear();
        ClearSpawnMarkers();
        StopAllBotTimers();
        LoadZones();
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo _)
    {
        if (_session == null) return HookResult.Continue;
        if (_roundStartHandled) return HookResult.Continue;
        _roundStartHandled = true;

        AddTimer(0.5f, () =>
        {
            _roundStartHandled = false;
            if (_session != null) StartNextRound();
        });

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo _)
    {
        if (_session == null) return HookResult.Continue;
        if (_session.RoundFinalized) return HookResult.Continue;
        if (_session.RoundNumber <= 0) return HookResult.Continue;

        // Si le moteur termine le round (ex: Terrorist win), on garde la boucle PracWay.
        AddTimer(0.1f, () =>
        {
            if (_session == null) return;
            FinalizeRound();
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

        var s = _session;
        bool isTerro = victim.TeamNum == (int)CsTeam.Terrorist;
        bool isCT    = victim.TeamNum == (int)CsTeam.CounterTerrorist;

        if (isTerro)   s.TerroDeaths++;
        else if (isCT) s.CtDeaths++;
        else return HookResult.Continue;

        int totalCT = s.BotCtCount + s.CtPlayers.Count;
        if (s.TerroDeaths >= s.TerroPlayers.Count || s.CtDeaths >= totalCT)
            FinalizeRound();

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
                p.PrintToChat(P + " Un parcours est en cours. Vous participerez a la prochaine manche.");
            }
        });
        return HookResult.Continue;
    }
private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo _)
{
    if (_session == null) return HookResult.Continue;
    var p = @event.Userid;
    if (p == null || !p.IsValid || !p.IsBot) return HookResult.Continue;
    // Ne pas kick ici: certains bots peuvent spawn légèrement plus tard,
    // on les corrige via EnsureExactCtBotsAndPlace.
    return HookResult.Continue;
}

    // ═══════════════════════════════════════════════════════════════════════════
    // MENU SYSTEM  (meme pattern que DuelPlugin)
    // ═══════════════════════════════════════════════════════════════════════════

    private enum MenuContext
    {
        None,
        Main,
        SimpleWay,
        SpecialCtSelect,
        SpecialWay,
        SpecialRounds,
        SpecialMoney,
        ZoneManagement,
        ZoneList,
        ZoneDeleteList,
    }

    private void SetCtx(CCSPlayerController p, MenuContext ctx) => _menuCtx[p.SteamID] = ctx;
    private MenuContext GetCtx(CCSPlayerController p) => _menuCtx.TryGetValue(p.SteamID, out var c) ? c : MenuContext.None;

    private void CmdShortcut(CCSPlayerController? caller, string choice)
    {
        if (caller == null) return;
        switch (GetCtx(caller))
        {
            case MenuContext.Main:            HandleMainMenu(caller, choice);         break;
            case MenuContext.SimpleWay:       HandleSimpleWay(caller, choice);        break;
            case MenuContext.SpecialCtSelect: HandleSpecialCtSelect(caller, choice);  break;
            case MenuContext.SpecialWay:      HandleSpecialWay(caller, choice);       break;
            case MenuContext.SpecialRounds:   HandleSpecialRounds(caller, choice);    break;
            case MenuContext.SpecialMoney:    HandleSpecialMoney(caller, choice);     break;
            case MenuContext.ZoneManagement:  HandleZoneManagement(caller, choice);   break;
            case MenuContext.ZoneList:        HandleZoneListEdit(caller, choice);     break;
            case MenuContext.ZoneDeleteList:  HandleZoneDeleteList(caller, choice);   break;
        }
    }

    // ─── Menu principal ────────────────────────────────────────────────────────
    private void OpenMainMenu(CCSPlayerController? caller)
    {
        if (caller == null) return;
        SetCtx(caller, MenuContext.Main);
        caller.PrintToChat(P + " == MENU PRINCIPAL ==");
        caller.PrintToChat(P + " \x0A!w1\x01 Parcours simple (tout aleatoire)");
        caller.PrintToChat(P + " \x0A!w2\x01 Parcours special (configuration manuelle)");
        caller.PrintToChat(P + " \x0A!w3\x01 Gestion des parcours");
        caller.PrintToChat(P + " \x0A!w4\x01 Quitter");
    }

    private void HandleMainMenu(CCSPlayerController p, string c)
    {
        switch (c)
        {
            case "1": OpenSimpleWayMenu(p);       break;
            case "2": OpenSpecialCtSelectMenu(p); break;
            case "3": OpenZoneManagementMenu(p);  break;
            case "4": SetCtx(p, MenuContext.None); break;
        }
    }

    // ─── Parcours simple ───────────────────────────────────────────────────────
    private void OpenSimpleWayMenu(CCSPlayerController p)
    {
        var ready = _zones.Values.Where(z => z.IsReady).ToList();
        if (ready.Count == 0)
        {
            p.PrintToChat(P + " Aucun parcours valide. Creez-en un via \x0A!w3\x01.");
            SetCtx(p, MenuContext.None);
            return;
        }
        SetCtx(p, MenuContext.SimpleWay);
        p.PrintToChat(P + " == PARCOURS SIMPLE ==");
        p.PrintToChat(P + " \x0A!w1\x01 Lancer (tout aleatoire)");
        p.PrintToChat(P + " \x0A!w2\x01 Retour");
    }

    private void HandleSimpleWay(CCSPlayerController p, string c)
    {
        if (c == "2") { OpenMainMenu(p); return; }
        if (c != "1") return;

        var humans   = Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot).ToList();
        int count    = humans.Count;
        int neededCt = count >= 5 ? 2 : count == 4 ? 1 : 0;

        var shuffled = FisherYatesShuffle(humans.ToList());
        var ctIds    = shuffled.Take(neededCt).Select(x => x.SteamID).ToList();

        if (neededCt > 0)
        {
            var names = ctIds.Select(sid => humans.FirstOrDefault(h => h.SteamID == sid)?.PlayerName ?? "?");
            Server.PrintToChatAll(P + " Joueur(s) CT (aleatoire) : \x04" + string.Join(", ", names) + "\x01");
        }

        var cfg = new PendingConfig
        {
            SpecificWayName  = null,
            RoundLimit       = 0,
            StartMoney       = -1,
            CtPlayerSteamIds = ctIds,
        };
        SetCtx(p, MenuContext.None);
        LaunchWay(cfg);
    }

    // ─── Parcours special ──────────────────────────────────────────────────────

    private void OpenSpecialCtSelectMenu(CCSPlayerController p)
    {
        _pending[p.SteamID] = new PendingConfig { CtPlayerSteamIds = new() };
        var humans   = Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot).ToList();
        int neededCt = humans.Count >= 5 ? 2 : humans.Count == 4 ? 1 : 0;

        if (neededCt == 0)
        {
            OpenSpecialWayMenu(p);
            return;
        }

        SetCtx(p, MenuContext.SpecialCtSelect);
        p.PrintToChat(P + " == JOUEURS CT (" + neededCt + " a choisir) ==");
        for (int i = 0; i < Math.Min(humans.Count, 8); i++)
            p.PrintToChat(P + " \x0A!w" + (i + 1) + "\x01 " + humans[i].PlayerName);

        if (_pending.TryGetValue(p.SteamID, out var cfg))
            cfg.HumanListForMenu = humans.Select(x => x.SteamID).ToList();
    }

    private void HandleSpecialCtSelect(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        if (!int.TryParse(c, out var idx) || idx < 1 || idx > (cfg.HumanListForMenu?.Count ?? 0)) return;

        var steamId = cfg.HumanListForMenu![idx - 1];
        if (cfg.CtPlayerSteamIds.Contains(steamId)) { p.PrintToChat(P + " Joueur deja selectionne."); return; }

        var humans   = Utilities.GetPlayers().Where(x => x.IsValid && !x.IsBot).ToList();
        int neededCt = humans.Count >= 5 ? 2 : 1;
        cfg.CtPlayerSteamIds.Add(steamId);

        var name = Utilities.GetPlayers().FirstOrDefault(x => x.SteamID == steamId)?.PlayerName ?? "?";
        p.PrintToChat(P + " \x0C" + name + "\x01 ajoute CT (" + cfg.CtPlayerSteamIds.Count + "/" + neededCt + ").");

        if (cfg.CtPlayerSteamIds.Count >= neededCt)
            OpenSpecialWayMenu(p);
    }

    private void OpenSpecialWayMenu(CCSPlayerController p)
    {
        SetCtx(p, MenuContext.SpecialWay);
        var ready = _zones.Values.Where(z => z.IsReady).ToList();
        if (ready.Count == 0)
        {
            p.PrintToChat(P + " Aucun parcours valide. Creez-en un via \x0A!w3\x01.");
            SetCtx(p, MenuContext.None);
            return;
        }
        p.PrintToChat(P + " == CHOIX DU PARCOURS ==");
        for (int i = 0; i < ready.Count; i++)
            p.PrintToChat(P + " \x0A!w" + (i + 1) + "\x01 " + ready[i].Name);
        p.PrintToChat(P + " \x0A!w" + (ready.Count + 1) + "\x01 Rotation (different a chaque manche)");

        if (_pending.TryGetValue(p.SteamID, out var cfg))
            cfg.WayListForMenu = ready.Select(z => z.Name).ToList();
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
        SetCtx(p, MenuContext.SpecialRounds);
        p.PrintToChat(P + " == NOMBRE DE ROUNDS ==");
        p.PrintToChat(P + " \x0A!w1\x01  5 rounds");
        p.PrintToChat(P + " \x0A!w2\x01 10 rounds");
        p.PrintToChat(P + " \x0A!w3\x01 15 rounds");
        p.PrintToChat(P + " \x0A!w4\x01 20 rounds");
        p.PrintToChat(P + " \x0A!w5\x01 Illimite");
    }

    private void HandleSpecialRounds(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        cfg.RoundLimit = c switch { "1" => 5, "2" => 10, "3" => 15, "4" => 20, _ => 0 };
        OpenSpecialMoneyMenu(p);
    }

    private void OpenSpecialMoneyMenu(CCSPlayerController p)
    {
        SetCtx(p, MenuContext.SpecialMoney);
        p.PrintToChat(P + " == ARGENT DE DEPART ==");
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
        SetCtx(p, MenuContext.None);
        LaunchWay(cfg);
    }

    // ─── Gestion des parcours ──────────────────────────────────────────────────
    private void OpenZoneManagementMenu(CCSPlayerController p)
    {
        SetCtx(p, MenuContext.ZoneManagement);
        p.PrintToChat(P + " == GESTION DES PARCOURS ==");
        p.PrintToChat(P + " \x0A!w1\x01 Creer un nouveau parcours");
        p.PrintToChat(P + " \x0A!w2\x01 Modifier / ajouter des spawns");
        p.PrintToChat(P + " \x0A!w3\x01 Supprimer un parcours");
        p.PrintToChat(P + " \x0A!w4\x01 Retour au menu principal");
    }

    private void HandleZoneManagement(CCSPlayerController p, string c)
    {
        switch (c)
        {
            case "1":
                SetCtx(p, MenuContext.None);
                p.PrintToChat(P + " Tapez \x0A!way_new \"nom_parcours\"\x01 pour creer un parcours.");
                break;
            case "2": OpenZoneListMenu(p, edit: true);  break;
            case "3": OpenZoneListMenu(p, edit: false); break;
            case "4": OpenMainMenu(p);                  break;
        }
    }

    private void OpenZoneListMenu(CCSPlayerController p, bool edit)
    {
        SetCtx(p, edit ? MenuContext.ZoneList : MenuContext.ZoneDeleteList);
        var zones = _zones.Values.ToList();
        if (zones.Count == 0) { p.PrintToChat(P + " Aucun parcours existant."); OpenZoneManagementMenu(p); return; }
        p.PrintToChat(P + " == " + (edit ? "MODIFIER" : "SUPPRIMER") + " PARCOURS ==");
        for (int i = 0; i < zones.Count; i++)
        {
            var z   = zones[i];
            var ok  = z.IsReady ? "\x0C[OK]" : "\x07[!!]";
            p.PrintToChat(P + " \x0A!w" + (i + 1) + "\x01 " + z.Name + " (a:" + z.GetSpawns(SpawnSlot.A).Count + " CT1:" + z.GetSpawns(SpawnSlot.CT1).Count + ") " + ok + "\x01");
        }
        if (!_wizards.ContainsKey(p.SteamID))
            _wizards[p.SteamID] = new ZoneWizard { ZoneNames = zones.Select(z => z.Name).ToList() };
        else
            _wizards[p.SteamID].ZoneNames = zones.Select(z => z.Name).ToList();
    }

    private void HandleZoneListEdit(CCSPlayerController p, string c)
    {
        if (!_wizards.TryGetValue(p.SteamID, out var wiz)) return;
        if (!int.TryParse(c, out var idx) || idx < 1 || idx > wiz.ZoneNames.Count) return;
        var zoneName = wiz.ZoneNames[idx - 1];
        wiz.CurrentZoneName = zoneName;
        RefreshSpawnEditCheats();
        SetCtx(p, MenuContext.None);
        p.PrintToChat(P + " Parcours \x0A" + zoneName + "\x01 -- edition.");
        p.PrintToChat(P + " \x0A!wset <a|j1|j2|CT1..CT5> <n> [action]\x01");
        p.PrintToChat(P + " Actions: stand  crouch  crouchpulse  peeka/peekb  crpeeka/crpeekb  flpeeka/flpeekb");
        p.PrintToChat(P + " Ex: \x0A!wset CT1 1 peeka\x01 puis se deplacer puis \x0A!wset CT1 1 peekb\x01");
        p.PrintToChat(P + " \x0A!wfin\x01 pour terminer.");
        ShowSpawnMarkers(zoneName, p);
    }

    private void HandleZoneDeleteList(CCSPlayerController p, string c)
    {
        if (!_wizards.TryGetValue(p.SteamID, out var wiz)) return;
        if (!int.TryParse(c, out var idx) || idx < 1 || idx > wiz.ZoneNames.Count) return;
        var zoneName = wiz.ZoneNames[idx - 1];
        _zones.Remove(zoneName);
        SaveZones();
        SetCtx(p, MenuContext.None);
        Server.PrintToChatAll(P + " Parcours \x07" + zoneName + "\x01 supprime.");
        OpenZoneManagementMenu(p);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMMANDES CREATION DE PARCOURS
    // ═══════════════════════════════════════════════════════════════════════════

    private void CmdWayFromChat(CCSPlayerController? caller, string arg)
    {
        if (caller == null) return;
        var name = arg.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(name)) { caller.PrintToChat(P + " Usage: !way_new \"nom_parcours\""); return; }

        if (_zones.ContainsKey(name))
        {
            caller.PrintToChat(P + " Le parcours \x07" + name + "\x01 existe deja. Tapez \x0A!way_new \"autre_nom\"\x01.");
            return;
        }
        _zones[name] = new WayZone(name);
        SaveZones();
        _wizards[caller.SteamID] = new ZoneWizard { CurrentZoneName = name };
        RefreshSpawnEditCheats();
        caller.PrintToChat(P + " Parcours \x0A" + name + "\x01 cree !");
        caller.PrintToChat(P + " \x0A!wset <a|j1|j2|CT1..CT5> <n> [action]\x01 pour definir les spawns.");
        caller.PrintToChat(P + " Actions: stand  crouch  crouchpulse  peeka/peekb  crpeeka/crpeekb  flpeeka/flpeekb");
        caller.PrintToChat(P + " Ex: \x0A!wset CT1 1 peeka\x01 (point A) puis \x0A!wset CT1 1 peekb\x01 (point B)");
        PrintSpawnStatus(caller, name);
        ShowSpawnMarkers(name, caller);
    }

    private void CmdSetFromChat(CCSPlayerController? caller, string arg)
    {
        if (caller == null) return;
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { caller.PrintToChat(P + " Usage: !wset <slot> <n> [action]"); return; }

        if (!TryParseSlot(parts[0], out var slot))
        {
            caller.PrintToChat(P + " Slot invalide. Valeurs: a j1 j2 CT1 CT2 CT3 CT4 CT5");
            return;
        }
        if (!int.TryParse(parts[1], out var num) || num < 1)
        {
            caller.PrintToChat(P + " Numero invalide (doit etre >= 1).");
            return;
        }

        string? zoneName = null;
        if (_wizards.TryGetValue(caller.SteamID, out var wiz)) zoneName = wiz.CurrentZoneName;
        if (zoneName == null || !_zones.ContainsKey(zoneName))
        {
            caller.PrintToChat(P + " Aucun parcours actif. Creez ou selectionnez un parcours d'abord.");
            return;
        }
        RefreshSpawnEditCheats();
        if (!TryGetPlayerCurrentSpawn(caller, out var spawn))
        {
            caller.PrintToChat(P + " Impossible de lire votre position.");
            return;
        }

        var actionStr = parts.Length >= 3 ? parts[2].ToLower() : "stand";
        var zone      = _zones[zoneName];
        var spawns    = zone.GetSpawns(slot);
        int idx       = num - 1;

        // ── Point B (peekb / crpeekb / flpeekb) ──────────────────────────────
        if (actionStr == "peekb" || actionStr == "crpeekb" || actionStr == "flpeekb")
        {
            if (idx >= spawns.Count)
            {
                caller.PrintToChat(P + " Spawn " + SlotLabel(slot) + "-" + num + " inexistant. Definissez d'abord le point A.");
                return;
            }
            var entry = spawns[idx];
            spawns[idx] = new SpawnEntry { Spawn = entry.Spawn, Action = entry.Action, PeekTarget = spawn };
            SaveZones();
            caller.PrintToChat(P + " Point B enregistre pour \x0A" + SlotLabel(slot) + "-" + num + "\x01 [" + ActionLabel(entry.Action) + "].");
            ShowSpawnMarkers(zoneName, caller);
            return;
        }

        // ── Point A (peeka / crpeeka / flpeeka / stand / crouch / crouchpulse) ─────────────
        BotAction action = actionStr switch
        {
            "peeka"   => BotAction.PeekAB,
            "crpeeka" => BotAction.PeekCrouch,
            "flpeeka" => BotAction.FlashPeek,
            "crouch"  => BotAction.Crouch,
            "crouchpulse" => BotAction.CrouchPulse,
            _         => BotAction.Stand,
        };

        // Préserver le PeekTarget existant si on réécrit juste le point A
        WaySpawn? existingPeek = (idx < spawns.Count) ? spawns[idx].PeekTarget : null;
        zone.SetSpawnFull(slot, idx, spawn, action, existingPeek);
        SaveZones();

        caller.PrintToChat(P + " Spawn \x0A" + SlotLabel(slot) + "-" + num + "\x01 [" + ActionLabel(action) + "] enregistre.");
        if (action != BotAction.Stand && action != BotAction.Crouch)
            caller.PrintToChat(P + " Definissez maintenant le point B : \x0A!wset " + SlotLabel(slot) + " " + num + " " + actionStr[..^1] + "b\x01");
        PrintSpawnStatus(caller, zoneName);
        ShowSpawnMarkers(zoneName, caller);
    }

    private void CmdFin(CCSPlayerController? caller)
    {
        if (caller == null) return;
        if (_wizards.TryGetValue(caller.SteamID, out var wiz) && wiz.CurrentZoneName != null)
        {
            var zoneName = wiz.CurrentZoneName;
            var zone     = _zones.TryGetValue(zoneName, out var z) ? z : null;
            _wizards.Remove(caller.SteamID);
            RefreshSpawnEditCheats();
            caller.PrintToChat(P + " Fin d'edition pour \x0A" + zoneName + "\x01." +
                (zone?.IsReady == true ? " \x0CParcours pret." : " \x07Parcours incomplet."));
        }
        ClearSpawnMarkers();
        OpenZoneManagementMenu(caller);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LOGIQUE DE PARCOURS
    // ═══════════════════════════════════════════════════════════════════════════

    private void LaunchWay(PendingConfig cfg)
    {
        ClearSpawnMarkers();
        _killCount.Clear();
        _session = new ActiveSession(cfg);

        // Met le serveur dans un état practice stable avant le premier round PracWay.
        Server.ExecuteCommand("exec PracWay/pracway.cfg");
        // Déclenche EventRoundStart => StartNextRound()
        Server.ExecuteCommand("mp_restartgame 1");
    }

    private void StartNextRound()
    {
        if (_session == null) return;

        StopAllBotTimers();
        _killCount.Clear();

        if (_session.Config.RoundLimit > 0 && _session.RoundNumber >= _session.Config.RoundLimit)
        {
            Server.PrintToChatAll(P + " \x0CLimite de rounds atteinte !");
            EndWaySession();
            return;
        }

        var humans   = Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot).ToList();
        int count    = humans.Count;
        int neededCt = count >= 5 ? 2 : count == 4 ? 1 : 0;

        List<CCSPlayerController> ctPlayers;
        List<CCSPlayerController> terroPlayers;

        if (neededCt == 0)
        {
            ctPlayers    = new List<CCSPlayerController>();
            terroPlayers = humans.Take(3).ToList();
        }
        else
        {
            var designated = _session.Config.CtPlayerSteamIds
                .Select(sid => humans.FirstOrDefault(h => h.SteamID == sid))
                .Where(h => h != null).Cast<CCSPlayerController>().ToList();

            if (designated.Count < neededCt)
            {
                var rest = FisherYatesShuffle(humans.Except(designated).ToList());
                designated.AddRange(rest.Take(neededCt - designated.Count));
            }
            ctPlayers    = designated.Take(neededCt).ToList();
            terroPlayers = humans.Except(ctPlayers).Take(3).ToList();
        }

        int botCtCount = 5 - ctPlayers.Count;

        string? wayName = _session.Config.SpecificWayName;
        if (wayName == null || !_zones.ContainsKey(wayName))
        {
            var ready      = _zones.Values.Where(z => z.IsReady).ToList();
            if (ready.Count == 0) { Server.PrintToChatAll(P + " \x07Aucun parcours valide."); EndWaySession(); return; }
            var candidates = ready.Where(z => z.Name != _session.CurrentWayName).ToList();
            if (candidates.Count == 0) candidates = ready;
            wayName = candidates[Random.Shared.Next(candidates.Count)].Name;
        }

        var zone = _zones[wayName];
        _session.CurrentWayName = wayName;
        _session.TerroPlayers   = terroPlayers;
        _session.CtPlayers      = ctPlayers;
        _session.TerroDeaths    = 0;
        _session.CtDeaths       = 0;
        _session.BotCtCount     = botCtCount;
        _session.RoundNumber++;

        int money = _session.Config.StartMoney < 0 ? PickRandomMoney() : _session.Config.StartMoney;

        foreach (var pl in terroPlayers) pl.ChangeTeam(CsTeam.Terrorist);
        foreach (var pl in ctPlayers)    pl.ChangeTeam(CsTeam.CounterTerrorist);

        var aSpawns = FisherYatesShuffle(zone.GetSpawns(SpawnSlot.A).ToList());
        for (int i = 0; i < terroPlayers.Count; i++)
        {
            if (i < aSpawns.Count) TeleportPlayer(terroPlayers[i], aSpawns[i].Spawn);
            SetPlayerMoney(terroPlayers[i], money);
        }

        var jSlots = new[] { SpawnSlot.J1, SpawnSlot.J2 };
        for (int i = 0; i < ctPlayers.Count; i++)
        {
            var jSpawns = zone.GetSpawns(jSlots[i]);
            if (jSpawns.Count > 0)
                TeleportPlayer(ctPlayers[i], jSpawns[Random.Shared.Next(jSpawns.Count)].Spawn);
            SetPlayerMoney(ctPlayers[i], money);
        }

var botSlots = new[] { SpawnSlot.CT1, SpawnSlot.CT2, SpawnSlot.CT3, SpawnSlot.CT4, SpawnSlot.CT5 };

// Étape 1 : kick + add des bots
Server.ExecuteCommand("bot_kick");
Server.ExecuteCommand("bot_quota 0");
Server.ExecuteCommand("bot_quota_mode normal");
Server.ExecuteCommand("bot_join_team ct");
Server.ExecuteCommand("bot_freeze 0");
Server.ExecuteCommand("bot_stop 0");
AddTimer(0.8f, () =>
{
    if (_session == null) return;
    for (int i = 0; i < botCtCount; i++)
        Server.ExecuteCommand("bot_add_ct");
});

// Étape 2 : téléportation + équipement + freeze (après que les bots soient spawned)
AddTimer(1.8f, () =>
{
    if (_session == null) return;
    EnsureExactCtBotsAndPlace(zone, botCtCount, money, botSlots, attempt: 0);
});

// Passe de correction tardive: certains bots peuvent spawn après la 1re passe.
AddTimer(2.8f, () =>
{
    if (_session == null) return;
    EnsureExactCtBotsAndPlace(zone, botCtCount, money, botSlots, attempt: 0);
});

        _session.RoundTimer?.Kill();
        _session.RoundTimer = AddTimer(RoundDuration, () =>
        {
            if (_session != null) { Server.PrintToChatAll(P + " Temps ecoule !"); FinalizeRound(); }
        });

        string fmt = count <= 3 ? count + "v5 bots CT"
                   : count == 4 ? "3T + 1J vs 4 bots CT"
                   :              "3T + 2J vs 3 bots CT";
        Server.PrintToChatAll(P + " Manche \x04" + _session.RoundNumber + "\x01 -- \x0A" + wayName + "\x01 -- " + fmt + " -- " + money + "$");
    }

private void FinalizeRound()
{
    if (_session == null) return;
    if (_session.RoundFinalized) return;
    _session.RoundFinalized = true;

    StopAllBotTimers();
    _session.RoundTimer?.Kill();
    _session.RoundTimer = null;

    int totalCT   = _session.BotCtCount + _session.CtPlayers.Count;
    bool terroWin = _session.CtDeaths >= totalCT;

    if (terroWin) { Server.PrintToChatAll(P + " \x0CParcours reussi ! Prochain dans 3 secondes..."); _session.LastRoundSuccess = true; }
    else
    {
        Server.PrintToChatAll(P + " \x07Parcours echoue. Nouvelle tentative dans 3 secondes...");
        _session.LastRoundSuccess = false;
        if (_session.Config.SpecificWayName == null) _session.Config.SpecificWayName = _session.CurrentWayName;
    }

    PrintKillCount();

    AddTimer(3.0f, () =>
    {
        if (_session == null) return;
        if (_session.LastRoundSuccess && _session.Config.SpecificWayName == _session.CurrentWayName && _session.Config.OriginalSpecificWayName == null)
            _session.Config.SpecificWayName = null;
        _session.RoundFinalized = false;
        // Force un nouveau round pour déclencher OnRoundStart => StartNextRound.
        Server.ExecuteCommand("mp_restartgame 1");
    });
}
    private void StopWay(CCSPlayerController? caller)
    {
        if (_session == null) { caller?.PrintToChat(P + " Aucun parcours en cours."); return; }
        Server.PrintToChatAll(P + " Parcours arrete par \x04" + (caller?.PlayerName ?? "le serveur") + "\x01.");
        try
        {
            EndWaySession(caller);
        }
        catch (Exception ex)
        {
            Server.PrintToConsole("[PracWay] StopWay exception: " + ex.Message);
            _session = null;
            StopAllBotTimers();
            Server.ExecuteCommand("bot_kick");
        }
    }

 private void EndWaySession(CCSPlayerController? _caller = null)
{
    if (_session == null) return;
    _session.RoundTimer?.Kill();
    _session = null;
    StopAllBotTimers();

    Server.ExecuteCommand("bot_kick");
    PrintKillCount();
    _killCount.Clear();
    Server.PrintToChatAll(P + " == FIN DU PARCOURS ==");
}
    

    private void PrintKillCount()
    {
        if (_killCount.Count == 0) return;
        Server.PrintToChatAll(P + " == KILLS ==");
        foreach (var kv in _killCount.ToArray().OrderByDescending(x => x.Value))
        {
            var pl   = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && !p.IsBot && p.SteamID == kv.Key);
            var name = pl?.PlayerName ?? "#" + kv.Key;
            Server.PrintToChatAll(P + " \x0A" + name + "\x01 : \x04" + kv.Value + "\x01 kill(s)");
        }
    }

    private void EnsureExactCtBotsAndPlace(WayZone zone, int expectedCtBots, int money, SpawnSlot[] botSlots, int attempt)
    {
        if (_session == null) return;

        // Réimpose les cvars bots à chaque passe (certains serveurs les écrasent).
        Server.ExecuteCommand("bot_quota 0");
        Server.ExecuteCommand("bot_quota_mode normal");
        Server.ExecuteCommand("bot_join_team ct");

        var allBots = Utilities.GetPlayers()
            .Where(p => p.IsValid && p.IsBot)
            .OrderBy(p => p.UserId ?? int.MaxValue)
            .ToList();

        // Supprime immédiatement les bots non-CT.
        foreach (var b in allBots.Where(b => b.TeamNum != (int)CsTeam.CounterTerrorist))
            Server.ExecuteCommand("bot_kick " + b.PlayerName);

        var ctBots = Utilities.GetPlayers()
            .Where(p => p.IsValid && p.IsBot && p.TeamNum == (int)CsTeam.CounterTerrorist)
            .OrderBy(p => p.UserId ?? int.MaxValue)
            .ToList();

        // Supprime les CT en trop (garde les plus anciens).
        if (ctBots.Count > expectedCtBots)
        {
            foreach (var extra in ctBots.Skip(expectedCtBots))
                Server.ExecuteCommand("bot_kick " + extra.PlayerName);
        }
        // Ajoute les CT manquants.
        else if (ctBots.Count < expectedCtBots)
        {
            int missing = expectedCtBots - ctBots.Count;
            for (int i = 0; i < missing; i++)
                Server.ExecuteCommand("bot_add_ct");
        }

        // Re-check après ajustement.
        var stableCtBots = Utilities.GetPlayers()
            .Where(p => p.IsValid && p.IsBot && p.TeamNum == (int)CsTeam.CounterTerrorist)
            .OrderBy(p => p.UserId ?? int.MaxValue)
            .ToList();
        var stableAllBots = Utilities.GetPlayers()
            .Where(p => p.IsValid && p.IsBot)
            .ToList();

        if (stableCtBots.Count != expectedCtBots || stableAllBots.Count != expectedCtBots)
        {
            if (attempt >= 3)
            {
                Server.PrintToConsole("[PracWay] Unable to stabilize bot count. Expected CT/Total: " + expectedCtBots + ", got CT: " + stableCtBots.Count + ", total: " + stableAllBots.Count);
                return;
            }

            AddTimer(0.6f, () =>
            {
                EnsureExactCtBotsAndPlace(zone, expectedCtBots, money, botSlots, attempt + 1);
            });
            return;
        }

        for (int i = 0; i < stableCtBots.Count && i < botSlots.Length; i++)
        {
            var bSpawns = zone.GetSpawns(botSlots[i]);
            if (bSpawns.Count == 0) continue;
            var entry = bSpawns[Random.Shared.Next(bSpawns.Count)];

            TeleportPlayer(stableCtBots[i], entry.Spawn);
            EquipBotCt(stableCtBots[i], money);
            ApplyBotAction(stableCtBots[i], entry);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EQUIPEMENT BOTS CT
    // ═══════════════════════════════════════════════════════════════════════════

    private static void EquipBotCt(CCSPlayerController bot, int money)
    {
        bot.RemoveWeapons();
        bot.GiveNamedItem("weapon_knife");
        bot.GiveNamedItem("item_kevlar");

        if (money <= 800)
        {
            bot.GiveNamedItem("weapon_usp_silencer");
        }
        else if (money <= 2000)
        {
            bot.GiveNamedItem("weapon_mp9");
        }
        else if (money <= 2500)
        {
            bot.GiveNamedItem("weapon_mp5sd");
        }
        else if (money <= 3000)
        {
            bot.GiveNamedItem("item_assaultsuit");
            bot.GiveNamedItem("weapon_famas");
        }
        else if (money <= 3500)
        {
            bot.GiveNamedItem("item_assaultsuit");
            bot.GiveNamedItem(Random.Shared.Next(2) == 0 ? "weapon_xm1014" : "weapon_p90");
        }
        else
        {
            bot.GiveNamedItem("item_assaultsuit");
            bot.GiveNamedItem(Random.Shared.Next(2) == 0 ? "weapon_m4a1_s" : "weapon_m4a4");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ACTIONS DES BOTS
    // ═══════════════════════════════════════════════════════════════════════════

    private void ApplyBotAction(CCSPlayerController bot, SpawnEntry entry)
    {
        if (!bot.IsValid || bot.PlayerPawn?.Value == null) return;

        switch (entry.Action)
        {
            case BotAction.Stand:
                StartHoldPositionTimer(bot, entry.Spawn, crouch: false);
                break;

            case BotAction.Crouch:
                StartCrouchHoldTimer(bot, entry.Spawn);
                break;

            case BotAction.CrouchPulse:
                StartCrouchPulseTimer(bot, entry.Spawn);
                break;

            case BotAction.PeekAB:
                if (entry.PeekTarget.HasValue)
                    StartPeekTimer(bot, entry.Spawn, entry.PeekTarget.Value, crouch: false);
                break;

            case BotAction.PeekCrouch:
                if (entry.PeekTarget.HasValue)
                    StartPeekTimer(bot, entry.Spawn, entry.PeekTarget.Value, crouch: true);
                break;

            case BotAction.FlashPeek:
                AddTimer(1.0f, () =>
                {
                    if (!bot.IsValid) return;
                    bot.GiveNamedItem("weapon_flashbang");
                    AddTimer(1.5f, () =>
                    {
                        if (!bot.IsValid || bot.PlayerPawn?.Value == null) return;
                        if (entry.PeekTarget.HasValue)
                        {
                            TeleportPlayer(bot, entry.PeekTarget.Value);
                            StartHoldPositionTimer(bot, entry.PeekTarget.Value, crouch: false);
                        }
                    });
                });
                break;
        }
    }

    private void StartPeekTimer(CCSPlayerController bot, WaySpawn posA, WaySpawn posB, bool crouch)
    {
        var key = bot.UserId ?? -1;
        if (key < 0) return;
        bool forward = true;
        float t = 0.0f;
        const float tick = 0.10f;
        const float moveDuration = 0.80f;
        const float holdDuration = 0.70f;
        bool holding = false;
        float holdElapsed = 0.0f;

        var timer = AddTimer(tick, () =>
        {
            if (!bot.IsValid || bot.PlayerPawn?.Value == null) return;

            SetBotCrouch(bot, crouch);

            if (holding)
            {
                holdElapsed += tick;
                if (holdElapsed < holdDuration) return;
                holding = false;
                holdElapsed = 0.0f;
                t = 0.0f;
                forward = !forward;
            }

            t += tick / moveDuration;
            if (t > 1.0f) t = 1.0f;

            var from = forward ? posA : posB;
            var to   = forward ? posB : posA;
            TeleportPlayer(bot, LerpSpawn(from, to, t));

            if (t >= 1.0f)
                holding = true;
        }, TimerFlags.REPEAT);
        _botTimers[key] = timer;
    }

    private void StartHoldPositionTimer(CCSPlayerController bot, WaySpawn anchor, bool crouch)
    {
        var key = bot.UserId ?? -1;
        if (key < 0) return;

        var timer = AddTimer(0.15f, () =>
        {
            if (!bot.IsValid || bot.PlayerPawn?.Value == null) return;
            TeleportPlayer(bot, anchor);
            SetBotCrouch(bot, crouch);
        }, TimerFlags.REPEAT);
        _botTimers[key] = timer;
    }

    private void StartCrouchHoldTimer(CCSPlayerController bot, WaySpawn anchor)
    {
        var key = bot.UserId ?? -1;
        if (key < 0) return;

        var timer = AddTimer(0.15f, () =>
        {
            if (!bot.IsValid || bot.PlayerPawn?.Value == null) return;
            TeleportPlayer(bot, anchor);
            SetBotCrouch(bot, true);
        }, TimerFlags.REPEAT);
        _botTimers[key] = timer;
    }

    private void StartCrouchPulseTimer(CCSPlayerController bot, WaySpawn anchor)
    {
        var key = bot.UserId ?? -1;
        if (key < 0) return;

        const float tick = 0.10f;
        const float crouchDuration = 2.0f;
        const float standDuration = 1.0f;
        bool crouching = true;
        float elapsed = 0.0f;

        var timer = AddTimer(tick, () =>
        {
            if (!bot.IsValid || bot.PlayerPawn?.Value == null) return;

            TeleportPlayer(bot, anchor);
            SetBotCrouch(bot, crouching);
            elapsed += tick;
            if (elapsed >= (crouching ? crouchDuration : standDuration))
            {
                elapsed = 0.0f;
                crouching = !crouching;
            }
        }, TimerFlags.REPEAT);
        _botTimers[key] = timer;
    }

    private void StopAllBotTimers()
    {
        foreach (var t in _botTimers.Values) t?.Kill();
        _botTimers.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SPAWN MARKERS
    // ═══════════════════════════════════════════════════════════════════════════

    private void ShowSpawnMarkers(string zoneName, CCSPlayerController? caller)
    {
        ClearSpawnMarkers();
        if (!_zones.TryGetValue(zoneName, out var zone)) return;

        var colors = new Dictionary<SpawnSlot, (byte r, byte g, byte b, string label)>
        {
            [SpawnSlot.A]   = (0,   100, 255, "Terro (a)"),
            [SpawnSlot.J1]  = (255, 0,   0,   "Joueur CT1 (j1)"),
            [SpawnSlot.J2]  = (255, 128, 0,   "Joueur CT2 (j2)"),
            [SpawnSlot.CT1] = (0,   220, 0,   "Bot CT1"),
            [SpawnSlot.CT2] = (220, 220, 0,   "Bot CT2"),
            [SpawnSlot.CT3] = (0,   220, 220, "Bot CT3"),
            [SpawnSlot.CT4] = (180, 0,   255, "Bot CT4"),
            [SpawnSlot.CT5] = (255, 255, 255, "Bot CT5"),
        };

        foreach (var (slot, (r, g, b, label)) in colors)
        {
            var spawns = zone.GetSpawns(slot);
            if (spawns.Count == 0) continue;
            caller?.PrintToChat(P + " \x0A" + label + "\x01: " + spawns.Count + " spawn(s)");
            for (int i = 0; i < spawns.Count; i++)
            {
                var entry = spawns[i];
                float h   = (entry.Action == BotAction.Crouch || entry.Action == BotAction.CrouchPulse) ? 150f : entry.Action == BotAction.FlashPeek ? 400f : 300f;
                var beam  = CreateSpawnBeam(entry.Spawn, r, g, b, h);
                if (beam != null) _spawnMarkers.Add(beam);
                caller?.PrintToChat(P + "  [" + SlotLabel(slot) + "-" + (i + 1) + "] " + ActionLabel(entry.Action));
            }
        }
    }

    private static CBeam? CreateSpawnBeam(WaySpawn s, byte r, byte g, byte b, float height = 300f)
    {
        var beam = Utilities.CreateEntityByName<CBeam>("beam");
        if (beam == null || !beam.IsValid) return null;
        beam.RenderMode = RenderMode_t.kRenderTransColor;
        beam.Render     = Color.FromArgb(255, r, g, b);
        beam.Width      = 10.0f;
        beam.EndWidth   = 10.0f;
        beam.LifeState  = 1;
        beam.Amplitude  = 0f;
        beam.Speed      = 0f;
        beam.FadeLength = 0f;
        beam.Flags      = 0;
        beam.BeamType   = BeamType_t.BEAM_POINTS;
        beam.EndPos.X   = s.X;
        beam.EndPos.Y   = s.Y;
        beam.EndPos.Z   = s.Z + height;
        beam.Teleport(
            new Vector(s.X, s.Y, s.Z),
            new QAngle(IntPtr.Zero),
            new Vector(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        beam.DispatchSpawn();
        return beam;
    }

    private void ClearSpawnMarkers()
    {
        foreach (var beam in _spawnMarkers)
            if (beam.IsValid) beam.Remove();
        _spawnMarkers.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UTILITY HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

private static void TeleportPlayer(CCSPlayerController p, WaySpawn s)
{
    var pawn = p.PlayerPawn?.Value;
    if (pawn == null)
        return;

    pawn.Teleport(
        new Vector(s.X, s.Y, s.Z),
        new QAngle(s.Pitch, s.Yaw, s.Roll),
        new Vector(0, 0, 0));
}

private static void SetBotCrouch(CCSPlayerController bot, bool crouch)
{
    var pawn = bot.PlayerPawn?.Value;
    if (pawn == null)
        return;

    if (crouch) pawn.Flags |= (uint)PlayerFlags.FL_DUCKING;
    else pawn.Flags &= ~(uint)PlayerFlags.FL_DUCKING;
}

private static WaySpawn LerpSpawn(WaySpawn a, WaySpawn b, float t)
{
    if (t < 0f) t = 0f;
    if (t > 1f) t = 1f;
    return new WaySpawn(
        a.X + (b.X - a.X) * t,
        a.Y + (b.Y - a.Y) * t,
        a.Z + (b.Z - a.Z) * t,
        a.Pitch + (b.Pitch - a.Pitch) * t,
        a.Yaw + (b.Yaw - a.Yaw) * t,
        a.Roll + (b.Roll - a.Roll) * t
    );
}

private void RefreshSpawnEditCheats()
{
    bool editingInProgress = _wizards.Values.Any(w => !string.IsNullOrWhiteSpace(w.CurrentZoneName));
    Server.ExecuteCommand("sv_cheats " + (editingInProgress ? "1" : "0"));
}

private static bool TryGetPlayerCurrentSpawn(CCSPlayerController p, out WaySpawn s)
{
    s = default;

    var pawn = p.PlayerPawn?.Value;
    if (pawn == null)
        return false;

    var pos = pawn.CBodyComponent?.SceneNode?.AbsOrigin;
    var ang = pawn.V_angle;

    if (pos == null || ang == null)
        return false;

    s = new WaySpawn(pos.X, pos.Y, pos.Z, ang.X, ang.Y, ang.Z);
    return true;
}

    private static void SetPlayerMoney(CCSPlayerController p, int amount)
    {
        if (p.InGameMoneyServices != null)
            p.InGameMoneyServices.Account = amount;
    }

    private static int PickRandomMoney() => MoneyOptions[Random.Shared.Next(MoneyOptions.Length)];

    private static bool TryParseSlot(string s, out SpawnSlot slot)
    {
        slot = SpawnSlot.A;
        switch (s.ToLower())
        {
            case "a":   slot = SpawnSlot.A;   return true;
            case "j1":  slot = SpawnSlot.J1;  return true;
            case "j2":  slot = SpawnSlot.J2;  return true;
            case "ct1": slot = SpawnSlot.CT1; return true;
            case "ct2": slot = SpawnSlot.CT2; return true;
            case "ct3": slot = SpawnSlot.CT3; return true;
            case "ct4": slot = SpawnSlot.CT4; return true;
            case "ct5": slot = SpawnSlot.CT5; return true;
        }
        return false;
    }

    private static bool TryParseAction(string s, out BotAction action)
    {
        action = BotAction.Stand;
        switch (s.ToLower())
        {
            case "stand":       action = BotAction.Stand;      return true;
            case "crouch":      action = BotAction.Crouch;     return true;
            case "crouchpulse": action = BotAction.CrouchPulse; return true;
            case "peek_ab":     action = BotAction.PeekAB;     return true;
            case "peek_crouch": action = BotAction.PeekCrouch; return true;
            case "flash_peek":  action = BotAction.FlashPeek;  return true;
        }
        return false;
    }

    private static string SlotLabel(SpawnSlot slot) => slot switch
    {
        SpawnSlot.A   => "a",   SpawnSlot.J1  => "j1",  SpawnSlot.J2  => "j2",
        SpawnSlot.CT1 => "CT1", SpawnSlot.CT2 => "CT2", SpawnSlot.CT3 => "CT3",
        SpawnSlot.CT4 => "CT4", SpawnSlot.CT5 => "CT5", _             => "?"
    };

    private static string ActionLabel(BotAction a) => a switch
    {
        BotAction.Stand      => "debout",
        BotAction.Crouch     => "accroupi",
        BotAction.CrouchPulse => "accroupi 2s/debout 1s",
        BotAction.PeekAB     => "decale A-B",
        BotAction.PeekCrouch => "decale accroupi",
        BotAction.FlashPeek  => "flash+decale",
        _                    => "?"
    };

    private static List<T> FisherYatesShuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    private void PrintSpawnStatus(CCSPlayerController p, string zoneName)
    {
        if (!_zones.TryGetValue(zoneName, out var zone)) return;
        var slots = new[] { SpawnSlot.A, SpawnSlot.CT1, SpawnSlot.CT2, SpawnSlot.CT3, SpawnSlot.CT4, SpawnSlot.CT5, SpawnSlot.J1, SpawnSlot.J2 };
        var mins  = new[] { MinA, MinCT1, MinCT2, MinCT3, MinCT4, MinCT5, MinJ1, MinJ2 };
        p.PrintToChat(P + " == SPAWNS ==");
        for (int i = 0; i < slots.Length; i++)
        {
            int cnt  = zone.GetSpawns(slots[i]).Count;
            int need = mins[i];
            string st = cnt >= need ? "\x0C" + cnt + "/" + need : "\x07" + cnt + "/" + need;
            p.PrintToChat(P + " " + SlotLabel(slots[i]) + ": " + st + "\x01");
        }
        if (zone.IsReady)
            p.PrintToChat(P + " \x0CParcours valide ! Tapez \x0A!wfin\x01 pour terminer.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // AIDE
    // ═══════════════════════════════════════════════════════════════════════════

    private static void PrintHelp(CCSPlayerController? caller)
    {
        if (caller == null) return;
        caller.PrintToChat(P + " == AIDE PRACWAY ==");
        caller.PrintToChat(P + " \x0A!way\x01 -- Menu principal");
        caller.PrintToChat(P + " \x0A!way_new \"nom\"\x01 -- Creer un parcours");
        caller.PrintToChat(P + " \x0A!wset <slot> <n> [action]\x01 -- Spawn (action defaut: stand)");
        caller.PrintToChat(P + " Actions: stand  crouch  crouchpulse  peeka/peekb  crpeeka/crpeekb  flpeeka/flpeekb");
        caller.PrintToChat(P + " \x0A!wfin\x01 -- Terminer l'edition");
        caller.PrintToChat(P + " \x0A!way_stop\x01 -- Arreter le parcours");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PERSISTENCE JSON
    // ═══════════════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void LoadZones()
    {
        var path = GetZoneFilePath();
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
        catch { /* ignore */ }
    }

    private void SaveZones()
    {
        var path = GetZoneFilePath();
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
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private string GetZoneFilePath() =>
        Path.Combine(ModuleDirectory, "..", "..", "..", "configs", "PracWay", "zones", Server.MapName + ".json");

    // ═══════════════════════════════════════════════════════════════════════════
    // TYPES
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly record struct WaySpawn(float X, float Y, float Z, float Pitch, float Yaw, float Roll);

    private sealed class SpawnEntry
    {
        public WaySpawn  Spawn      { get; set; }
        public BotAction Action     { get; set; } = BotAction.Stand;
        public WaySpawn? PeekTarget { get; set; }
    }

    private sealed class WayZone
    {
        public string Name { get; }
        private readonly Dictionary<SpawnSlot, List<SpawnEntry>> _slots = new();
        public WayZone(string n) => Name = n;

        public List<SpawnEntry> GetSpawns(SpawnSlot slot)
        {
            if (!_slots.TryGetValue(slot, out var list)) { list = new(); _slots[slot] = list; }
            return list;
        }
        public void SetSpawn(SpawnSlot slot, int idx, WaySpawn s, BotAction action = BotAction.Stand)
        {
            var list  = GetSpawns(slot);
            var entry = new SpawnEntry { Spawn = s, Action = action };
            if (idx < list.Count) list[idx] = entry;
            else                  list.Add(entry);
        }
        public void SetSpawnFull(SpawnSlot slot, int idx, WaySpawn s, BotAction action, WaySpawn? peekTarget)
        {
            var list  = GetSpawns(slot);
            var entry = new SpawnEntry { Spawn = s, Action = action, PeekTarget = peekTarget };
            if (idx < list.Count) list[idx] = entry;
            else                  list.Add(entry);
        }
        public void AddSpawn(SpawnSlot slot, SpawnEntry entry) => GetSpawns(slot).Add(entry);

        public bool IsReady =>
            GetSpawns(SpawnSlot.A).Count   >= MinA   &&
            GetSpawns(SpawnSlot.CT1).Count >= MinCT1 &&
            GetSpawns(SpawnSlot.CT2).Count >= MinCT2 &&
            GetSpawns(SpawnSlot.CT3).Count >= MinCT3 &&
            GetSpawns(SpawnSlot.CT4).Count >= MinCT4 &&
            GetSpawns(SpawnSlot.CT5).Count >= MinCT5;
    }

    private sealed class ZoneWizard
    {
        public string?      CurrentZoneName { get; set; }
        public List<string> ZoneNames       { get; set; } = new();
    }

    private sealed class PendingConfig
    {
        public string?         SpecificWayName         { get; set; }
        public string?         OriginalSpecificWayName { get; set; }
        public int             RoundLimit              { get; set; }
        public int             StartMoney              { get; set; } = -1;
        public List<ulong>     CtPlayerSteamIds        { get; set; } = new();
        public List<string>    WayListForMenu          { get; set; } = new();
        public List<ulong>?    HumanListForMenu        { get; set; }
    }

    private sealed class ActiveSession
    {
        public PendingConfig                 Config         { get; }
        public List<CCSPlayerController>     TerroPlayers   { get; set; } = new();
        public List<CCSPlayerController>     CtPlayers      { get; set; } = new();
        public int     TerroDeaths      { get; set; }
        public int     CtDeaths         { get; set; }
        public int     BotCtCount       { get; set; }
        public int     RoundNumber      { get; set; }
        public bool    LastRoundSuccess { get; set; }
		public bool 	RoundFinalized 	{ get; set; }
        public string? CurrentWayName   { get; set; }
        public CounterStrikeSharp.API.Modules.Timers.Timer? RoundTimer { get; set; }
        public ActiveSession(PendingConfig cfg) => Config = cfg;
    }

    private sealed class ZoneDto
    {
        public string Name  { get; set; } = "";
        public Dictionary<string, List<SpawnEntryDto>> Slots { get; set; } = new();
    }

    private sealed class SpawnEntryDto
    {
        public float  X     { get; set; } public float Y    { get; set; } public float Z    { get; set; }
        public float  Pitch { get; set; } public float Yaw  { get; set; } public float Roll  { get; set; }
        public string Action { get; set; } = "stand";
        public float? PX    { get; set; } public float? PY  { get; set; } public float? PZ   { get; set; }
        public float? PPitch { get; set; } public float? PYaw { get; set; } public float? PRoll { get; set; }

        public SpawnEntry ToEntry()
        {
            TryParseActionStatic(Action, out var a);
            WaySpawn? peek = PX.HasValue
                ? new WaySpawn(PX.Value, PY!.Value, PZ!.Value, PPitch!.Value, PYaw!.Value, PRoll!.Value)
                : null;
            return new SpawnEntry { Spawn = new WaySpawn(X, Y, Z, Pitch, Yaw, Roll), Action = a, PeekTarget = peek };
        }

        public static SpawnEntryDto From(SpawnEntry e) => new()
        {
            X = e.Spawn.X, Y = e.Spawn.Y, Z = e.Spawn.Z,
            Pitch = e.Spawn.Pitch, Yaw = e.Spawn.Yaw, Roll = e.Spawn.Roll,
            Action = ActionLabelStatic(e.Action),
            PX = e.PeekTarget?.X, PY = e.PeekTarget?.Y, PZ = e.PeekTarget?.Z,
            PPitch = e.PeekTarget?.Pitch, PYaw = e.PeekTarget?.Yaw, PRoll = e.PeekTarget?.Roll,
        };

        private static bool TryParseActionStatic(string s, out BotAction a)
        {
            a = BotAction.Stand;
            switch (s.ToLower())
            {
                case "stand":       a = BotAction.Stand;      return true;
                case "crouch":      a = BotAction.Crouch;     return true;
                case "crouchpulse": a = BotAction.CrouchPulse; return true;
                case "peek_ab":     a = BotAction.PeekAB;     return true;
                case "peek_crouch": a = BotAction.PeekCrouch; return true;
                case "flash_peek":  a = BotAction.FlashPeek;  return true;
            }
            return false;
        }

        private static string ActionLabelStatic(BotAction a) => a switch
        {
            BotAction.Stand      => "stand",
            BotAction.Crouch     => "crouch",
            BotAction.CrouchPulse => "crouchpulse",
            BotAction.PeekAB     => "peek_ab",
            BotAction.PeekCrouch => "peek_crouch",
            BotAction.FlashPeek  => "flash_peek",
            _                    => "stand"
        };
    }
}
