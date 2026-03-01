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
    public override string ModuleVersion     => "1.0.0";
    public override string ModuleAuthor      => "Codex";
    public override string ModuleDescription => "Parcours Practice multi-joueurs avec bots.";

    // ─── Chat prefix ───────────────────────────────────────────────────────────
    private const string P = " \x07[PRACWAY]\x01";

    // ─── Constantes ────────────────────────────────────────────────────────────
    private const int MinCtSpawns  = 5;   // spawns CT minimum pour qu'un parcours soit valide
    private const int MinBotSpawns = 12;  // spawns T/bots minimum
    private const int RoundDuration = 120; // 2 minutes max par manche

    // ─── Armes disponibles ─────────────────────────────────────────────────────
    private static readonly (string WeaponName, string Label)[] AllowedWeapons =
    {
        ("weapon_ak47",        "AK-47"),
        ("weapon_m4a1_s",      "M4A1-S"),
        ("weapon_m4a1",        "M4A1"),
        ("weapon_awp",         "AWP"),
        ("weapon_deagle",      "Desert Eagle"),
        ("weapon_usp_silencer","USP-S"),
    };

    private static readonly string[] UtilityPool =
        { "weapon_hegrenade", "weapon_flashbang", "weapon_smokegrenade", "weapon_molotov" };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly HashSet<string> PistolWeapons = new()
    {
        "weapon_deagle", "weapon_usp_silencer", "weapon_glock", "weapon_p250",
        "weapon_fiveseven", "weapon_tec9", "weapon_cz75a", "weapon_elite"
    };

    // ─── State ─────────────────────────────────────────────────────────────────
    private readonly Dictionary<string, WayZone>   _zones    = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, ZoneWizard> _wizards  = new();
    private readonly Dictionary<ulong, MenuContext> _menuCtx  = new();
    private readonly Dictionary<ulong, PendingConfig> _pending = new();
    private readonly List<CBeam> _spawnMarkers = new();

    // Kills par joueur (SteamID → count) pour la session en cours
    private readonly Dictionary<ulong, int> _killCount = new();

    private ActiveSession? _session;
    private bool _roundStartHandled = false;

    // ───────────────────────────────────────────────────────────────────────────
    public override void Load(bool hotReload)
    {
        AddCommand("css_way",      "Menu principal PracWay",  (c, _) => OpenMainMenu(c));
        AddCommand("css_way_stop", "Arrêter le parcours",     (c, _) => StopWay(c));
        AddCommand("css_way_help", "Aide PracWay",            (c, _) => PrintHelp(c));

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Pre);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnect);

        AddCommandListener("say",      OnPlayerSay);
        AddCommandListener("say_team", OnPlayerSay);

        LoadZones();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CHAT LISTENER
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
            case "way":
                // !way sans argument → menu principal ; !way "nom" → créer parcours
                if (string.IsNullOrWhiteSpace(arg)) OpenMainMenu(player);
                else CmdWayFromChat(player, arg);
                break;
            case "way_stop": StopWay(player);                break;
            case "way_help": PrintHelp(player);              break;
            case "set":      CmdSetFromChat(player, arg);    break;
            case "fin":      CmdFin(player);                 break;
            case "1": case "2": case "3": case "4":
            case "5": case "6": case "7": case "8":
                CmdShortcut(player, cmd);                    break;
            default: return HookResult.Continue;
        }

        return HookResult.Handled;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMMANDES CRÉATION DE PARCOURS
    // ═══════════════════════════════════════════════════════════════════════════

    private void CmdWayFromChat(CCSPlayerController? caller, string arg)
    {
        if (caller == null || string.IsNullOrWhiteSpace(arg))
        {
            caller?.PrintToChat($"{P} Usage: !way \"nom_parcours\"");
            return;
        }
        var name = arg.Trim('"', ' ');
        if (_zones.ContainsKey(name))
        {
            caller.PrintToChat($"{P} Le parcours \x07{name}\x01 existe déjà. Tapez \x0A!way \"autre_nom\"\x01.");
            return;
        }
        _zones[name] = new WayZone(name);
        SaveZones();
        _wizards[caller.SteamID] = new ZoneWizard { CurrentZoneName = name };
        caller.PrintToChat($"{P} Parcours \x0A{name}\x01 créé !");
        caller.PrintToChat($"{P} Utilisez \x0A!set <a|b> <numéro>\x01 pour définir les spawns.");
        caller.PrintToChat($"{P} Minimum : \x0C{MinCtSpawns} spawns CT (a)\x01 et \x07{MinBotSpawns} spawns Bots (b)\x01.");
        ShowSpawnMarkers(name, caller);
    }

    private void CmdSetFromChat(CCSPlayerController? caller, string arg)
    {
        if (caller == null) return;
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { caller.PrintToChat($"{P} Usage: !set <a|b> <numéro>"); return; }

        var teamStr = parts[0].ToLower();
        if (!int.TryParse(parts[1], out var slot) || slot < 1)
        {
            caller.PrintToChat($"{P} Numéro de spawn invalide (doit être ≥ 1).");
            return;
        }

        string? zoneName = null;
        if (_wizards.TryGetValue(caller.SteamID, out var wiz)) zoneName = wiz.CurrentZoneName;
        if (zoneName == null || !_zones.ContainsKey(zoneName))
        {
            caller.PrintToChat($"{P} Aucun parcours actif. Créez ou sélectionnez un parcours d'abord.");
            return;
        }

        var zone = _zones[zoneName];
        var team = teamStr == "a" ? WayTeam.CT : WayTeam.Bot;

        if (!TryGetPlayerCurrentSpawn(caller, out var spawn))
        {
            caller.PrintToChat($"{P} Impossible de lire votre position.");
            return;
        }

        zone.SetSpawn(team, slot - 1, spawn);
        SaveZones();

        int aOk    = zone.CtSpawns.Count;
        int bOk    = zone.BotSpawns.Count;
        int missA  = Math.Max(0, MinCtSpawns  - aOk);
        int missB  = Math.Max(0, MinBotSpawns - bOk);

        if (zone.IsReady)
        {
            caller.PrintToChat($"{P} Spawn {(team == WayTeam.CT ? "CT" : "Bot")} #{slot} enregistré. ✓ Parcours valide (CT:{aOk} Bot:{bOk})");
            caller.PrintToChat($"{P} Tapez \x0A!fin\x01 pour terminer ou continuez d'ajouter des spawns.");
        }
        else
        {
            string missing = "";
            if (missA > 0) missing += $"\x0C{missA} CT manquant(s)\x01 ";
            if (missB > 0) missing += $"\x07{missB} Bot manquant(s)\x01";
            caller.PrintToChat($"{P} Spawn enregistré. (CT:{aOk} Bot:{bOk}) — {missing}");
        }

        ShowSpawnMarkers(zoneName, caller);
    }

    private void CmdFin(CCSPlayerController? caller)
    {
        if (caller == null) return;
        if (_wizards.TryGetValue(caller.SteamID, out var wiz) && wiz.CurrentZoneName != null)
        {
            var zoneName = wiz.CurrentZoneName;
            var zone = _zones.TryGetValue(zoneName, out var z) ? z : null;
            _wizards.Remove(caller.SteamID);
            caller.PrintToChat($"{P} Fin d'édition pour \x0A{zoneName}\x01." +
                (zone?.IsReady == true ? " ✓ Parcours prêt." : " \x07⚠ Parcours incomplet (CT≥5, Bot≥12)."));
        }
        ClearSpawnMarkers();
        OpenZoneManagementMenu(caller);
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
            if (_session != null)
                StartNextRound();
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo _)
    {
        if (_session == null) return HookResult.Continue;

        var victim   = @event.Userid;
        var attacker = @event.Attacker;
        if (victim == null) return HookResult.Continue;

        // Comptage des kills joueurs (pas les bots)
        if (attacker != null && attacker.IsValid && !attacker.IsBot && attacker.SteamID != victim.SteamID)
        {
            _killCount.TryGetValue(attacker.SteamID, out var k);
            _killCount[attacker.SteamID] = k + 1;
        }

        var s = _session;

        // Vérifier si c'est un CT ou un T (bot ou joueur surplus) qui meurt
        bool isCT  = victim.TeamNum == (int)CsTeam.CounterTerrorist;
        bool isT   = victim.TeamNum == (int)CsTeam.Terrorist;

        if (isCT)  s.CTDeaths++;
        else if (isT) s.TDeaths++;
        else return HookResult.Continue;

        // Fin de manche si tous les CT morts OU tous les T morts
        int totalT = s.BotCount + s.TPlayers.Count;
        if (s.CTDeaths >= s.CTPlayers.Count || s.TDeaths >= totalT)
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
                p.PrintToChat($"{P} Un parcours est en cours. Vous participerez à la prochaine manche.");
            }
        });
        return HookResult.Continue;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // MENU SYSTEM
    // ═══════════════════════════════════════════════════════════════════════════

    private enum MenuContext
    {
        None,
        Main,
        SimpleWeapon,
        SimpleUtility,
        SpecificWay,
        SpecificWeapon,
        SpecificUtility,
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
            case MenuContext.Main:            HandleMainMenu(caller, choice);          break;
            case MenuContext.SimpleWeapon:    HandleSimpleWeapon(caller, choice);      break;
            case MenuContext.SimpleUtility:   HandleSimpleUtility(caller, choice);     break;
            case MenuContext.SpecificWay:     HandleSpecificWay(caller, choice);       break;
            case MenuContext.SpecificWeapon:  HandleSpecificWeapon(caller, choice);    break;
            case MenuContext.SpecificUtility: HandleSpecificUtility(caller, choice);   break;
            case MenuContext.ZoneManagement:  HandleZoneManagement(caller, choice);    break;
            case MenuContext.ZoneList:        HandleZoneListEdit(caller, choice);      break;
            case MenuContext.ZoneDeleteList:  HandleZoneDeleteList(caller, choice);    break;
        }
    }

    // ─── Menu principal ────────────────────────────────────────────────────────
    private void OpenMainMenu(CCSPlayerController? caller)
    {
        if (caller == null) return;
        SetCtx(caller, MenuContext.Main);
        caller.PrintToChat($"{P} ══ MENU PRINCIPAL ══");
        caller.PrintToChat($"{P} \x0A!1\x01 Lancer un parcours simple (rotation automatique)");
        caller.PrintToChat($"{P} \x0A!2\x01 Lancer un parcours spécifique");
        caller.PrintToChat($"{P} \x0A!3\x01 Gestion des parcours");
        caller.PrintToChat($"{P} \x0A!4\x01 Quitter");
    }

    private void HandleMainMenu(CCSPlayerController p, string c)
    {
        switch (c)
        {
            case "1": OpenSimpleWeaponMenu(p);     break;
            case "2": OpenSpecificWayMenu(p);      break;
            case "3": OpenZoneManagementMenu(p);   break;
            case "4": SetCtx(p, MenuContext.None); break;
        }
    }

    // ─── Parcours simple (rotation) ────────────────────────────────────────────
    private void OpenSimpleWeaponMenu(CCSPlayerController p)
    {
        _pending[p.SteamID] = new PendingConfig { SpecificWayName = null };
        SetCtx(p, MenuContext.SimpleWeapon);
        PrintWeaponMenu(p);
    }

    private void HandleSimpleWeapon(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        if (!ApplyWeaponChoice(c, cfg)) return;
        SetCtx(p, MenuContext.SimpleUtility);
        PrintUtilityMenu(p);
    }

    private void HandleSimpleUtility(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        ApplyUtilityChoice(c, cfg);
        SetCtx(p, MenuContext.None);
        LaunchWay(cfg);
    }

    // ─── Parcours spécifique ───────────────────────────────────────────────────
    private void OpenSpecificWayMenu(CCSPlayerController p)
    {
        _pending[p.SteamID] = new PendingConfig();
        SetCtx(p, MenuContext.SpecificWay);
        var ready = _zones.Values.Where(z => z.IsReady).ToList();
        if (ready.Count == 0)
        {
            p.PrintToChat($"{P} Aucun parcours valide disponible. Créez-en un via \x0A!3\x01.");
            SetCtx(p, MenuContext.None);
            return;
        }
        p.PrintToChat($"{P} ══ CHOIX DU PARCOURS ══");
        for (int i = 0; i < ready.Count; i++)
            p.PrintToChat($"{P} \x0A!{i + 1}\x01 {ready[i].Name}");
        if (_pending.TryGetValue(p.SteamID, out var cfg))
            cfg.WayListForMenu = ready.Select(z => z.Name).ToList();
    }

    private void HandleSpecificWay(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        if (!int.TryParse(c, out var idx) || idx < 1 || idx > cfg.WayListForMenu.Count) return;
        cfg.SpecificWayName = cfg.WayListForMenu[idx - 1];
        SetCtx(p, MenuContext.SpecificWeapon);
        PrintWeaponMenu(p);
    }

    private void HandleSpecificWeapon(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        if (!ApplyWeaponChoice(c, cfg)) return;
        SetCtx(p, MenuContext.SpecificUtility);
        PrintUtilityMenu(p);
    }

    private void HandleSpecificUtility(CCSPlayerController p, string c)
    {
        if (!_pending.TryGetValue(p.SteamID, out var cfg)) return;
        ApplyUtilityChoice(c, cfg);
        SetCtx(p, MenuContext.None);
        LaunchWay(cfg);
    }

    // ─── Gestion des parcours ──────────────────────────────────────────────────
    private void OpenZoneManagementMenu(CCSPlayerController? p)
    {
        if (p == null) return;
        SetCtx(p, MenuContext.ZoneManagement);
        p.PrintToChat($"{P} ══ GESTION DES PARCOURS ══");
        p.PrintToChat($"{P} \x0A!1\x01 Créer un nouveau parcours");
        p.PrintToChat($"{P} \x0A!2\x01 Modifier / ajouter des spawns");
        p.PrintToChat($"{P} \x0A!3\x01 Supprimer un parcours");
        p.PrintToChat($"{P} \x0A!4\x01 Retour au menu principal");
    }

    private void HandleZoneManagement(CCSPlayerController p, string c)
    {
        switch (c)
        {
            case "1":
                SetCtx(p, MenuContext.None);
                p.PrintToChat($"{P} Tapez \x0A!way \"nom_parcours\"\x01 pour créer un parcours.");
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
        if (zones.Count == 0)
        {
            p.PrintToChat($"{P} Aucun parcours existant.");
            OpenZoneManagementMenu(p);
            return;
        }
        p.PrintToChat($"{P} ══ {(edit ? "MODIFIER" : "SUPPRIMER")} PARCOURS ══");
        for (int i = 0; i < zones.Count; i++)
        {
            var z = zones[i];
            p.PrintToChat($"{P} \x0A!{i + 1}\x01 {z.Name} (CT:{z.CtSpawns.Count} Bot:{z.BotSpawns.Count}) {(z.IsReady ? "\x0C[OK]" : "\x07[!!]")}\x01");
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
        SetCtx(p, MenuContext.None);
        var z = _zones[zoneName];
        p.PrintToChat($"{P} Parcours \x0A{zoneName}\x01 — CT:{z.CtSpawns.Count} Bot:{z.BotSpawns.Count} spawns");
        p.PrintToChat($"{P} \x0A!set <a|b> <numéro>\x01 pour modifier/ajouter un spawn.");
        p.PrintToChat($"{P} \x0A!fin\x01 pour terminer.");
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
        Server.PrintToChatAll($"{P} Parcours \x07{zoneName}\x01 supprimé.");
        OpenZoneManagementMenu(p);
    }

    // ─── Menus armes / utility ─────────────────────────────────────────────────
    private static void PrintWeaponMenu(CCSPlayerController p)
    {
        p.PrintToChat($" \x07[PRACWAY]\x01 ══ CHOIX D'ARME ══");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!1\x01 AK-47");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!2\x01 M4A1-S");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!3\x01 M4A1");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!4\x01 AWP");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!5\x01 Desert Eagle");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!6\x01 USP-S");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!7\x01 Aléatoire identique pour tous");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!8\x01 Aléatoire différent par joueur");
    }

    private static void PrintUtilityMenu(CCSPlayerController p)
    {
        p.PrintToChat($" \x07[PRACWAY]\x01 ══ UTILITY ══");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!1\x01 1 Flash (identique)");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!2\x01 2 Flashs (identique)");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!3\x01 1 Utility aléatoire identique");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!4\x01 2 Utility aléatoires identiques");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!5\x01 1 Utility aléatoire par joueur");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!6\x01 2 Utility aléatoires par joueur");
        p.PrintToChat($" \x07[PRACWAY]\x01 \x0A!7\x01 Aucun utility");
    }

    private static bool ApplyWeaponChoice(string c, PendingConfig cfg)
    {
        switch (c)
        {
            case "1": cfg.WeaponMode = WeaponMode.Fixed; cfg.FixedWeapon = "weapon_ak47";         break;
            case "2": cfg.WeaponMode = WeaponMode.Fixed; cfg.FixedWeapon = "weapon_m4a1_s";       break;
            case "3": cfg.WeaponMode = WeaponMode.Fixed; cfg.FixedWeapon = "weapon_m4a1";         break;
            case "4": cfg.WeaponMode = WeaponMode.Fixed; cfg.FixedWeapon = "weapon_awp";          break;
            case "5": cfg.WeaponMode = WeaponMode.Fixed; cfg.FixedWeapon = "weapon_deagle";       break;
            case "6": cfg.WeaponMode = WeaponMode.Fixed; cfg.FixedWeapon = "weapon_usp_silencer"; break;
            case "7": cfg.WeaponMode = WeaponMode.RandomSame;       break;
            case "8": cfg.WeaponMode = WeaponMode.RandomPerPlayer;  break;
            default:  return false;
        }
        return true;
    }

    private static void ApplyUtilityChoice(string c, PendingConfig cfg)
    {
        cfg.UtilityMode = c switch
        {
            "1" => UtilityMode.Flash1Same,
            "2" => UtilityMode.Flash2Same,
            "3" => UtilityMode.Random1Same,
            "4" => UtilityMode.Random2Same,
            "5" => UtilityMode.Random1PerPlayer,
            "6" => UtilityMode.Random2PerPlayer,
            _   => UtilityMode.None,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LOGIQUE DE PARCOURS
    // ═══════════════════════════════════════════════════════════════════════════

    private void LaunchWay(PendingConfig cfg)
    {
        ClearSpawnMarkers();
        _killCount.Clear();
        _session = new ActiveSession(cfg);

        // Unload MatchZy pour éviter les conflits avec la gestion des bots
        Server.ExecuteCommand("css_plugins unload MatchZy");

        // Petit délai pour laisser MatchZy se décharger avant le restart
        AddTimer(0.5f, () =>
        {
            Server.ExecuteCommand("exec PracWay/pracway.cfg");
        });
    }

    private void StartNextRound()
    {
        if (_session == null) return;

        // Tous les joueurs humains actuellement connectés
        var humans = Utilities.GetPlayers()
            .Where(p => p.IsValid && !p.IsBot)
            .ToList();

        int count = humans.Count;

        // Assignation CT / T selon le nombre de joueurs
        // 1-3 joueurs → tous CT
        // 4 joueurs   → 3 CT + 1 T (tirage aléatoire)
        // 5 joueurs   → 3 CT + 2 T (tirage aléatoire)
        List<CCSPlayerController> ctPlayers;
        List<CCSPlayerController> tPlayers;

        if (count <= 3)
        {
            ctPlayers = humans.ToList();
            tPlayers  = new List<CCSPlayerController>();
        }
        else
        {
            var shuffled = FisherYatesShuffle(humans.ToList());
            ctPlayers = shuffled.Take(3).ToList();
            tPlayers  = shuffled.Skip(3).ToList(); // 1 ou 2 joueurs T
        }

        // Nombre de bots T : 12 si 1-3 joueurs, 5 si 4-5 joueurs
        int botCount = count <= 3 ? 12 : 5;

        // Choisir le parcours
        string? wayName = _session.Config.SpecificWayName;
        bool    isSpecific = wayName != null;

        if (!isSpecific || !_zones.ContainsKey(wayName!))
        {
            // Rotation : éviter le même parcours que le précédent
            var ready = _zones.Values.Where(z => z.IsReady).ToList();
            if (ready.Count == 0)
            {
                Server.PrintToChatAll($"{P} \x07Aucun parcours valide disponible.\x01");
                EndWaySession();
                return;
            }
            var candidates = ready.Where(z => z.Name != _session.CurrentWayName).ToList();
            if (candidates.Count == 0) candidates = ready;
            wayName = candidates[Random.Shared.Next(candidates.Count)].Name;
        }

        var zone = _zones[wayName!];
        _session.CurrentWayName = wayName;
        _session.CTPlayers  = ctPlayers;
        _session.TPlayers   = tPlayers;
        _session.CTDeaths   = 0;
        _session.TDeaths    = 0;
        _session.BotCount   = botCount;
        _session.RoundNumber++;

        // Assign teams côté moteur
        foreach (var p in ctPlayers) p.ChangeTeam(CsTeam.CounterTerrorist);
        foreach (var p in tPlayers)  p.ChangeTeam(CsTeam.Terrorist);

        // Spawn bots T
        Server.ExecuteCommand("bot_kick");
        AddTimer(0.3f, () =>
        {
            for (int i = 0; i < botCount; i++)
                Server.ExecuteCommand("bot_add_t");
        });

        // Shuffle spawns
        var ctSpawns  = FisherYatesShuffle(zone.CtSpawns.ToList());
        var botSpawns = FisherYatesShuffle(zone.BotSpawns.ToList());

        // Arme partagée si RandomSame
        var rng = new Random(Environment.TickCount ^ _session.RoundNumber * 7919);
        string? sharedWeapon = _session.Config.WeaponMode == WeaponMode.RandomSame
            ? AllowedWeapons[rng.Next(AllowedWeapons.Length)].WeaponName
            : null;

        // Équiper et téléporter CT
        for (int i = 0; i < ctPlayers.Count; i++)
        {
            var player = ctPlayers[i];
            if (i < ctSpawns.Count) TeleportPlayer(player, ctSpawns[i]);
            EquipPlayer(player, sharedWeapon, _session.Config, isCT: true);
        }

        // Équiper et téléporter joueurs T supplémentaires
        for (int i = 0; i < tPlayers.Count; i++)
        {
            var player = tPlayers[i];
            // Les joueurs T prennent les premiers spawns bot
            if (i < botSpawns.Count) TeleportPlayer(player, botSpawns[i]);
            EquipPlayer(player, sharedWeapon, _session.Config, isCT: false);
        }

        // Téléporter les bots (après un délai pour qu'ils soient spawned)
        AddTimer(0.8f, () =>
        {
            if (_session == null) return;
            var bots = Utilities.GetPlayers()
                .Where(p => p.IsValid && p.IsBot && p.TeamNum == (int)CsTeam.Terrorist)
                .ToList();
            var remainingBotSpawns = botSpawns.Skip(tPlayers.Count).ToList();
            var shuffledBotSpawns  = FisherYatesShuffle(remainingBotSpawns);
            for (int i = 0; i < bots.Count; i++)
            {
                if (i < shuffledBotSpawns.Count)
                    TeleportPlayer(bots[i], shuffledBotSpawns[i]);
            }
        });

        // Timer de 2 minutes
        _session.RoundTimer?.Kill();
        _session.RoundTimer = AddTimer(RoundDuration, () =>
        {
            if (_session != null)
            {
                Server.PrintToChatAll($"{P} ⏱ Temps écoulé !");
                FinalizeRound();
            }
        });

        // Annonce
        string format = count <= 3 ? $"{count}v12 bots"
                      : count == 4 ? "3CT + 1J vs 5 bots"
                      :              "3CT + 2J vs 5 bots";
        Server.PrintToChatAll($"{P} Manche \x04{_session.RoundNumber}\x01 — \x0A{wayName}\x01 — Format: {format}");
    }

    private void FinalizeRound()
    {
        if (_session == null) return;

        _session.RoundTimer?.Kill();
        _session.RoundTimer = null;

        int totalT = _session.BotCount + _session.TPlayers.Count;
        bool ctWin = _session.TDeaths >= totalT;             // tous les T/bots morts = succès CT
        bool tWin  = _session.CTDeaths >= _session.CTPlayers.Count; // tous les CT morts = échec

        if (ctWin)
        {
            Server.PrintToChatAll($"{P} \x0C✓ Parcours réussi !\x01 Prochain parcours dans 3 secondes...");
            _session.LastRoundSuccess = true;
            // Si parcours spécifique réussi : on relance indéfiniment le même
            // Si rotation : on passe à un autre parcours (géré dans StartNextRound via CurrentWayName)
        }
        else
        {
            Server.PrintToChatAll($"{P} \x07✗ Parcours échoué.\x01 Nouvelle tentative dans 3 secondes...");
            _session.LastRoundSuccess = false;
            // En cas d'échec : on force le même parcours au prochain round
            // (StartNextRound détectera SpecificWayName ou forcera le même via CurrentWayName)
            if (_session.Config.SpecificWayName == null)
                _session.Config.SpecificWayName = _session.CurrentWayName; // force le même
        }

        PrintKillCount();

        AddTimer(3.0f, () =>
        {
            if (_session == null) return;

            // Si c'était un parcours de rotation et qu'il a été réussi, on retire le forçage
            if (_session.LastRoundSuccess && _session.Config.SpecificWayName == _session.CurrentWayName
                && _session.Config.OriginalSpecificWayName == null)
            {
                _session.Config.SpecificWayName = null; // retour en rotation
            }

            Server.ExecuteCommand("mp_restartgame 1");
        });
    }

    private void PrintKillCount()
    {
        if (_killCount.Count == 0) return;
        Server.PrintToChatAll($"{P} ══ KILLS DE LA MANCHE ══");
        foreach (var kv in _killCount.OrderByDescending(x => x.Value))
        {
            var player = Utilities.GetPlayers().FirstOrDefault(p => p.IsValid && !p.IsBot && p.SteamID == kv.Key);
            var name = player?.PlayerName ?? $"#{kv.Key}";
            Server.PrintToChatAll($"{P} \x0A{name}\x01 : \x04{kv.Value}\x01 kill(s)");
        }
    }

    private void EndWaySession(CCSPlayerController? caller = null)
    {
        if (_session == null) return;

        _session.RoundTimer?.Kill();
        _session = null;

        Server.ExecuteCommand("bot_kick");
        PrintKillCount();
        _killCount.Clear();

        Server.PrintToChatAll($"{P} ══ FIN DU PARCOURS ══");
        Server.PrintToChatAll($"{P} Tapez \x0A!way_stop\x01 pour revenir au mode Practice.");

        // Reload MatchZy
        AddTimer(1.0f, () =>
        {
            Server.ExecuteCommand("css_plugins load MatchZy");
            AddTimer(1.0f, () =>
            {
                Server.ExecuteCommand("mp_warmuptime 9999");
                Server.ExecuteCommand("mp_warmup_pausetimer 1");
                if (caller != null && caller.IsValid)
                    caller.ExecuteClientCommandFromServer("css_prac");
                else
                {
                    Server.ExecuteCommand("mp_warmup_start");
                    Server.ExecuteCommand("mp_restartgame 1");
                }
            });
        });
    }

    private void StopWay(CCSPlayerController? caller)
    {
        if (_session == null)
        {
            caller?.PrintToChat($"{P} Aucun parcours en cours.");
            return;
        }
        Server.PrintToChatAll($"{P} Parcours arrêté par \x04{caller?.PlayerName ?? "le serveur"}\x01.");
        EndWaySession(caller);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ÉQUIPEMENT JOUEURS
    // ═══════════════════════════════════════════════════════════════════════════

    private void EquipPlayer(CCSPlayerController player, string? sharedWeapon, PendingConfig cfg, bool isCT)
    {
        player.RemoveWeapons();
        player.GiveNamedItem("weapon_knife");

        var weapon = cfg.WeaponMode switch
        {
            WeaponMode.Fixed           => cfg.FixedWeapon ?? "weapon_ak47",
            WeaponMode.RandomSame      => sharedWeapon ?? PickRandomWeapon(),
            WeaponMode.RandomPerPlayer => PickRandomWeapon(),
            _                          => "weapon_ak47"
        };
        player.GiveNamedItem(weapon);

        if (!PistolWeapons.Contains(weapon))
            player.GiveNamedItem(isCT ? "weapon_usp_silencer" : "weapon_glock");

        player.GiveNamedItem("item_kevlar");
        player.GiveNamedItem("item_assaultsuit");

        var utilities = GetUtilityItems(cfg.UtilityMode, 1).ToList();
        foreach (var u in utilities) player.GiveNamedItem(u);

        Server.ExecuteCommand("mp_buytime 0");
        Server.ExecuteCommand("mp_buy_anywhere 0");

        // Retirer la bombe si le moteur en donne une
        AddTimer(0.2f, () =>
        {
            if (!player.IsValid || player.PlayerPawn?.Value == null) return;
            var weapons = player.PlayerPawn.Value.WeaponServices?.MyWeapons;
            if (weapons == null) return;
            foreach (var wh in weapons)
            {
                var w = wh.Value;
                if (w != null && w.IsValid && w.DesignerName == "weapon_c4") { w.Remove(); break; }
            }
        });
    }

    private static string PickRandomWeapon()
    {
        var rng = new Random(Environment.TickCount ^ Thread.CurrentThread.ManagedThreadId);
        return AllowedWeapons[rng.Next(AllowedWeapons.Length)].WeaponName;
    }

    private static IEnumerable<string> GetUtilityItems(UtilityMode mode, int count)
    {
        switch (mode)
        {
            case UtilityMode.Flash1Same:
                for (int i = 0; i < count; i++) yield return "weapon_flashbang";
                break;
            case UtilityMode.Flash2Same:
                for (int i = 0; i < count; i++) { yield return "weapon_flashbang"; yield return "weapon_flashbang"; }
                break;
            case UtilityMode.Random1Same:
            {
                var pick = UtilityPool[Random.Shared.Next(UtilityPool.Length)];
                for (int i = 0; i < count; i++) yield return pick;
                break;
            }
            case UtilityMode.Random2Same:
            {
                var p1 = UtilityPool[Random.Shared.Next(UtilityPool.Length)];
                var p2 = UtilityPool[Random.Shared.Next(UtilityPool.Length)];
                for (int i = 0; i < count; i++) { yield return p1; yield return p2; }
                break;
            }
            case UtilityMode.Random1PerPlayer:
                for (int i = 0; i < count; i++) yield return UtilityPool[Random.Shared.Next(UtilityPool.Length)];
                break;
            case UtilityMode.Random2PerPlayer:
                for (int i = 0; i < count; i++)
                {
                    yield return UtilityPool[Random.Shared.Next(UtilityPool.Length)];
                    yield return UtilityPool[Random.Shared.Next(UtilityPool.Length)];
                }
                break;
        }
    }

    private static List<T> FisherYatesShuffle<T>(List<T> list)
    {
        var rng = new Random(Environment.TickCount ^ list.Count * 1000003);
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SPAWN MARKERS (visualisation)
    // ═══════════════════════════════════════════════════════════════════════════

    private void ShowSpawnMarkers(string zoneName, CCSPlayerController? caller)
    {
        ClearSpawnMarkers();
        if (!_zones.TryGetValue(zoneName, out var zone)) return;

        caller?.PrintToChat($"{P} \x0CCT(a): {zone.CtSpawns.Count}/{MinCtSpawns}\x01  \x07Bot(b): {zone.BotSpawns.Count}/{MinBotSpawns}\x01");
        caller?.PrintToChat($"{P} \x0CLasers bleus\x01 = CT (a) — \x07Lasers rouges\x01 = Bots (b)");

        for (int i = 0; i < zone.CtSpawns.Count; i++)
        {
            var s = zone.CtSpawns[i];
            var beam = CreateSpawnBeam(s, 0, 100, 255);
            if (beam != null) _spawnMarkers.Add(beam);
            caller?.PrintToChat($"{P} \x0C[CT a{i + 1}]\x01 X:{s.X:F0} Y:{s.Y:F0} Z:{s.Z:F0}");
        }
        for (int i = 0; i < zone.BotSpawns.Count; i++)
        {
            var s = zone.BotSpawns[i];
            var beam = CreateSpawnBeam(s, 255, 50, 50);
            if (beam != null) _spawnMarkers.Add(beam);
            caller?.PrintToChat($"{P} \x07[Bot b{i + 1}]\x01 X:{s.X:F0} Y:{s.Y:F0} Z:{s.Z:F0}");
        }

        if (_spawnMarkers.Count == 0)
            caller?.PrintToChat($"{P} Aucun spawn défini pour l'instant.");
    }

    private static CBeam? CreateSpawnBeam(WaySpawn s, byte r, byte g, byte b)
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

        beam.EndPos.X = s.X;
        beam.EndPos.Y = s.Y;
        beam.EndPos.Z = s.Z + 300f;

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
        if (p.PlayerPawn.Value != null)
            p.PlayerPawn.Value.Teleport(
                new Vector(s.X, s.Y, s.Z),
                new QAngle(s.Pitch, s.Yaw, s.Roll),
                new Vector(0, 0, 0));
    }

    private static bool TryGetPlayerCurrentSpawn(CCSPlayerController p, out WaySpawn s)
    {
        s = default;
        var pos = p.PlayerPawn.Value?.CBodyComponent?.SceneNode?.AbsOrigin;
        var ang = p.PlayerPawn.Value?.V_angle;
        if (pos == null || ang == null) return false;
        s = new WaySpawn(pos.X, pos.Y, pos.Z, ang.X, ang.Y, ang.Z);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PERSISTENCE JSON
    // ═══════════════════════════════════════════════════════════════════════════

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
                for (int i = 0; i < d.CtSpawns.Count;  i++) z.SetSpawn(WayTeam.CT,  i, d.CtSpawns[i].ToSpawn());
                for (int i = 0; i < d.BotSpawns.Count; i++) z.SetSpawn(WayTeam.Bot, i, d.BotSpawns[i].ToSpawn());
                _zones[z.Name] = z;
            }
        }
        catch { /* ignore parse errors */ }
    }

    private void SaveZones()
    {
        var path = GetZoneFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = _zones.Values.Select(z => new ZoneDto
        {
            Name      = z.Name,
            CtSpawns  = z.CtSpawns.Select(s  => SpawnDto.From(s)).ToList(),
            BotSpawns = z.BotSpawns.Select(s => SpawnDto.From(s)).ToList(),
        }).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
    }

    private string GetZoneFilePath()
    {
        var safeName = Server.MapName.Replace('/', '_').Replace('\\', '_');
        return Path.Combine(ModuleDirectory, "..", "..", "..", "configs", "PracWay", "zones", $"{safeName}.json");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // AIDE
    // ═══════════════════════════════════════════════════════════════════════════

    private void PrintHelp(CCSPlayerController? caller)
    {
        if (caller == null) return;
        caller.PrintToChat($"{P} ══ AIDE PRACWAY ══");
        caller.PrintToChat($"{P} \x0A!way\x01 — Menu principal");
        caller.PrintToChat($"{P} \x0A!way_stop\x01 — Arrêter le parcours");
        caller.PrintToChat($"{P} \x0A!way \"nom\"\x01 — Créer un parcours");
        caller.PrintToChat($"{P} \x0A!set <a|b> <numéro>\x01 — Définir un spawn (votre position)");
        caller.PrintToChat($"{P} \x0A!fin\x01 — Terminer l'édition");
        caller.PrintToChat($"{P} \x0A!way_help\x01 — Afficher cette aide");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TYPES
    // ═══════════════════════════════════════════════════════════════════════════

    private readonly record struct WaySpawn(float X, float Y, float Z, float Pitch, float Yaw, float Roll);
    private enum WayTeam   { CT, Bot }
    private enum WeaponMode  { Fixed, RandomSame, RandomPerPlayer }
    private enum UtilityMode { None, Flash1Same, Flash2Same, Random1Same, Random2Same, Random1PerPlayer, Random2PerPlayer }

    private sealed class PendingConfig
    {
        public string?      SpecificWayName         { get; set; }
        public string?      OriginalSpecificWayName { get; set; } // null si mode rotation
        public WeaponMode   WeaponMode              { get; set; }
        public string?      FixedWeapon             { get; set; }
        public UtilityMode  UtilityMode             { get; set; }
        public List<string> WayListForMenu          { get; set; } = new();
    }

    private sealed class ActiveSession
    {
        public PendingConfig Config        { get; }
        public List<CCSPlayerController> CTPlayers { get; set; } = new();
        public List<CCSPlayerController> TPlayers  { get; set; } = new();
        public int CTDeaths    { get; set; }
        public int TDeaths     { get; set; }
        public int BotCount    { get; set; }
        public int RoundNumber { get; set; }
        public bool LastRoundSuccess { get; set; }
        public string? CurrentWayName { get; set; }
        public CounterStrikeSharp.API.Modules.Timers.Timer? RoundTimer { get; set; }
        public ActiveSession(PendingConfig cfg) => Config = cfg;
    }

    private sealed class ZoneWizard
    {
        public string? CurrentZoneName { get; set; }
        public List<string> ZoneNames  { get; set; } = new();
    }

    private sealed class WayZone
    {
        public string Name { get; }
        public List<WaySpawn> CtSpawns  { get; } = new();
        public List<WaySpawn> BotSpawns { get; } = new();
        public bool IsReady => CtSpawns.Count >= MinCtSpawns && BotSpawns.Count >= MinBotSpawns;
        public WayZone(string n) => Name = n;
        public void SetSpawn(WayTeam t, int i, WaySpawn s)
        {
            var list = t == WayTeam.CT ? CtSpawns : BotSpawns;
            if (i < list.Count) list[i] = s;
            else                list.Add(s);
        }
    }

    // JSON DTOs
    private sealed class ZoneDto
    {
        public string         Name      { get; set; } = "";
        public List<SpawnDto> CtSpawns  { get; set; } = new();
        public List<SpawnDto> BotSpawns { get; set; } = new();
    }
    private sealed class SpawnDto
    {
        public float X { get; set; } public float Y { get; set; } public float Z { get; set; }
        public float Pitch { get; set; } public float Yaw { get; set; } public float Roll { get; set; }
        public WaySpawn ToSpawn() => new(X, Y, Z, Pitch, Yaw, Roll);
        public static SpawnDto From(WaySpawn s) => new() { X=s.X, Y=s.Y, Z=s.Z, Pitch=s.Pitch, Yaw=s.Yaw, Roll=s.Roll };
    }
}
