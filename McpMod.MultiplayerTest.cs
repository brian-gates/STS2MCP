using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace STS2_MCP;

// The game's debug multiplayer test scene (NMultiplayerTest, opened with the dev
// console command `multiplayer test`). It hosts or joins a direct ENet lobby on
// a fixed port without Steam, which is how two game instances on one machine
// play co-op against each other.
//
// Scene shape (from member names and types): _ipField and _idField are TextEdits.
// Join builds an ENetClientConnectionInitializer(netId, ip, port) from them, so
// _idField is this client's net id (a ulong, default "1000") and the port is the
// static NMultiplayerTest._port. Host / Join / Ready are buttons wired to the
// scene's private HostButtonPressed / JoinButtonPressed / ReadyButtonPressed;
// the character paginator raises CharacterChanged into OnCharacterChanged.
// Everything here calls those handlers directly on the main thread instead of
// simulating clicks.
//
// Deliberately NOT exposed: the scene's "Clear Cloud Saves" button and the
// Load Replay / Save From Replay buttons (ChooseReplayToLoad, ChooseReplayToSave,
// LoadReplay, WriteReplayAsSave). They delete or write files on disk (cloud
// saves, replay and save files), which is outside what this API should be able
// to do. Leave them out of both state and actions.
public static partial class McpMod
{
    private const string MultiplayerTestCharacterPrefix = "character_";

    private const BindingFlags MultiplayerTestMethodFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static NMultiplayerTest? FindVisibleMultiplayerTest(Node root)
    {
        var scene = FindFirst<NMultiplayerTest>(root);
        return scene != null && IsNodeVisible(scene) ? scene : null;
    }

    private static void AddMultiplayerTestMenuState(
        Dictionary<string, object?> result,
        NMultiplayerTest scene)
    {
        result["state_type"] = "menu";
        result["menu_screen"] = "multiplayer_test";
        result["message"] = "Debug multiplayer test scene. One instance uses 'host'; the other "
                            + "sets a different id with 'set_id' and uses 'join'. Pick a character "
                            + "with character_<ID>, then 'ready' on every instance to start the run.";

        var lobby = GetInstanceFieldValue(scene, "_lobby") as StartRunLobby;
        var status = GetMultiplayerTestStatus(scene, lobby);

        var info = new Dictionary<string, object?>
        {
            ["ip"] = (GetInstanceFieldValue(scene, "_ipField") as TextEdit)?.Text,
            ["id"] = (GetInstanceFieldValue(scene, "_idField") as TextEdit)?.Text,
            ["port"] = GetMultiplayerTestPort(),
            ["status"] = status,
        };

        var paginator = GetInstanceFieldValue(scene, "_characterPaginator") as NMultiplayerTestCharacterPaginator;
        var characters = GetMultiplayerTestCharacters(paginator);
        info["characters"] = characters.Select(c => c.Id.Entry).ToList();
        info["selected_character"] = SafeGetCharacterId(() => paginator?.Character);
        if (_pendingTestCharacter != null && _pendingTestSceneId == scene.GetInstanceId())
            info["pending_character"] = SafeGetCharacterId(() => _pendingTestCharacter);

        if (lobby != null)
        {
            try
            {
                var net = lobby.NetService;
                info["role"] = net.Type switch
                {
                    NetGameType.Host => "host",
                    NetGameType.Client => "client",
                    var other => other.ToString().ToLowerInvariant()
                };
                info["net_id"] = net.NetId.ToString();
                info["connected"] = net.IsConnected;
            }
            catch { }

            try
            {
                info["seed"] = lobby.Seed;
                info["ascension"] = lobby.Ascension;
                info["game_mode"] = lobby.GameMode.ToString();
            }
            catch { }

            info["players"] = BuildMultiplayerTestPlayers(lobby);
        }

        result["multiplayer_test"] = info;

        var options = new List<Dictionary<string, object?>>();
        bool idle = status == "idle";
        bool inLobby = lobby != null && status != "starting_run";
        bool localReady = lobby != null && TryGetLocalLobbyPlayer(lobby) is { isReady: true };

        void Add(string name, bool enabled) =>
            options.Add(new Dictionary<string, object?> { ["name"] = name, ["enabled"] = enabled });

        Add("set_ip", idle);
        Add("set_id", idle);
        Add("host", idle && IsMultiplayerTestButtonEnabled(scene, "HostButton"));
        Add("join", idle && IsMultiplayerTestButtonEnabled(scene, "JoinButton"));
        // Before a lobby exists the choice is stored and applied once host/join
        // creates it (see SelectMultiplayerTestCharacter), so these stay enabled.
        foreach (var character in characters)
            Add(MultiplayerTestCharacterPrefix + character.Id.Entry, status != "starting_run" && !localReady);
        if (inLobby)
        {
            Add("ready", !localReady);
            Add("disconnect", true);
        }
        Add("back", status != "starting_run");
        result["options"] = options;
    }

    private static string GetMultiplayerTestStatus(NMultiplayerTest scene, StartRunLobby? lobby)
    {
        if (GetInstanceFieldValue(scene, "_beginningRun") is true)
            return "starting_run";
        if (GetInstanceFieldValue(scene, "_loadingPanel") is CanvasItem loading && IsNodeVisible(loading))
            return "connecting";
        if (lobby == null)
            return "idle";
        try
        {
            return lobby.NetService.Type == NetGameType.Host ? "hosting" : "joined";
        }
        catch
        {
            return "unknown";
        }
    }

    private static List<Dictionary<string, object?>> BuildMultiplayerTestPlayers(StartRunLobby lobby)
    {
        var players = new List<Dictionary<string, object?>>();
        ulong? localId = null;
        try { localId = lobby.NetService.NetId; } catch { }
        try
        {
            foreach (var player in lobby.Players)
            {
                players.Add(new Dictionary<string, object?>
                {
                    ["id"] = player.id.ToString(),
                    ["slot"] = player.slotId,
                    ["character"] = SafeGetCharacterId(() => player.character),
                    ["ready"] = player.isReady,
                    ["local"] = localId == player.id,
                });
            }
        }
        catch { }
        return players;
    }

    private static StartRunLobbyPlayer? TryGetLocalLobbyPlayer(StartRunLobby lobby)
    {
        try
        {
            var localId = lobby.NetService.NetId;
            foreach (var player in lobby.Players)
                if (player.id == localId)
                    return player;
        }
        catch { }
        return null;
    }

    private static string? SafeGetCharacterId(Func<CharacterModel?> getter)
    {
        try { return getter()?.Id.Entry; }
        catch { return null; }
    }

    private static List<CharacterModel> GetMultiplayerTestCharacters(NMultiplayerTestCharacterPaginator? paginator)
    {
        if (paginator == null)
            return new List<CharacterModel>();
        return (GetInstanceFieldValue(paginator, "_characters") as CharacterModel[])?
            .Where(c => c != null).ToList() ?? new List<CharacterModel>();
    }

    private static object? GetMultiplayerTestPort()
    {
        try
        {
            return typeof(NMultiplayerTest)
                .GetField("_port", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?
                .GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    // Host and Join are scene children with no backing fields (node names from the
    // scene's own GetNode paths). Read their Disabled flag when present.
    private static bool IsMultiplayerTestButtonEnabled(NMultiplayerTest scene, string nodeName)
    {
        try
        {
            if (scene.FindChild(nodeName, true, false) is BaseButton button)
                return !button.Disabled && IsNodeVisible(button);
        }
        catch { }
        return true;
    }

    private static Dictionary<string, object?> ExecuteMultiplayerTestMenuOption(
        NMultiplayerTest scene,
        string option,
        string? value)
    {
        var missing = MissingMultiplayerTestMembers();
        if (missing.Count > 0)
            return Error($"Multiplayer test scene is missing {string.Join(", ", missing)} (game version changed?)");

        var lobby = GetInstanceFieldValue(scene, "_lobby") as StartRunLobby;
        var status = GetMultiplayerTestStatus(scene, lobby);
        var normalized = option.ToLowerInvariant();

        if (status == "starting_run")
            return Error("The run is starting; multiplayer test options are unavailable");

        switch (normalized)
        {
            case "set_ip":
            {
                if (status != "idle")
                    return Error($"Cannot change the IP while {status}; disconnect first");
                var ip = value?.Trim();
                if (string.IsNullOrEmpty(ip) || ip.Any(char.IsWhiteSpace))
                    return Error("set_ip requires a 'value' with the host address, e.g. \"127.0.0.1\"");
                if (GetInstanceFieldValue(scene, "_ipField") is not TextEdit ipField)
                    return Error("IP field not found on the multiplayer test scene");
                ipField.Text = ip;
                return Ok($"IP set to {ip}", ("ip", ip));
            }
            case "set_id":
            {
                if (status != "idle")
                    return Error($"Cannot change the id while {status}; disconnect first");
                var raw = value?.Trim();
                if (string.IsNullOrEmpty(raw) || !ulong.TryParse(raw, out var id))
                    return Error("set_id requires a 'value' with an unsigned integer net id, e.g. \"1001\"");
                if (GetInstanceFieldValue(scene, "_idField") is not TextEdit idField)
                    return Error("Id field not found on the multiplayer test scene");
                idField.Text = id.ToString();
                return Ok($"Net id set to {id}", ("id", id.ToString()));
            }
            case "host":
                if (status != "idle")
                    return Error($"Cannot host while {status}");
                return StartPendingCharacterAfter(scene, InvokeMultiplayerTestHandler(scene, "HostButtonPressed", "Hosting"));
            case "join":
                if (status != "idle")
                    return Error($"Cannot join while {status}");
                return StartPendingCharacterAfter(scene, InvokeMultiplayerTestHandler(scene, "JoinButtonPressed", "Joining"));
            case "ready":
                if (lobby == null)
                    return Error("Not in a lobby; host or join first");
                if (TryGetLocalLobbyPlayer(lobby) is { isReady: true })
                    return Error("Already ready");
                return InvokeMultiplayerTestHandler(scene, "ReadyButtonPressed", "Ready");
            case "disconnect":
                if (lobby == null)
                    return Error("Not in a lobby");
                ClearPendingTestCharacter();
                return InvokeMultiplayerTestHandler(scene, "Disconnect", "Disconnected", NetError.Quit);
            case "back":
            {
                ClearPendingTestCharacter();
                if (lobby != null)
                {
                    var disconnected = InvokeMultiplayerTestHandler(scene, "Disconnect", "Disconnected", NetError.Quit);
                    if ((string?)disconnected["status"] != "ok")
                        return disconnected;
                }
                var game = NGame.Instance;
                if (game == null)
                    return Error("Game node not available");
                MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(game.ReturnToMainMenu());
                return Ok("Returning to main menu");
            }
        }

        if (normalized.StartsWith(MultiplayerTestCharacterPrefix))
            return SelectMultiplayerTestCharacter(scene, lobby, option.Substring(MultiplayerTestCharacterPrefix.Length));

        return Error($"Unknown multiplayer test option: {option}. Use: set_ip, set_id, host, join, "
                     + "ready, character_<ID>, disconnect, back");
    }

    private static Dictionary<string, object?> SelectMultiplayerTestCharacter(
        NMultiplayerTest scene,
        StartRunLobby? lobby,
        string characterId)
    {
        if (lobby != null && TryGetLocalLobbyPlayer(lobby) is { isReady: true })
            return Error("Already ready; the character can no longer change");

        var paginator = GetInstanceFieldValue(scene, "_characterPaginator") as NMultiplayerTestCharacterPaginator;
        var characters = GetMultiplayerTestCharacters(paginator);
        var character = characters.FirstOrDefault(c =>
            string.Equals(c.Id.Entry, characterId, StringComparison.OrdinalIgnoreCase));
        if (paginator == null || character == null)
            return Error($"Character '{characterId}' not found. Available: "
                         + string.Join(", ", characters.Select(c => MultiplayerTestCharacterPrefix + c.Id.Entry)));

        // The scene's character handler dereferences the lobby, so selecting before
        // host/join throws inside the game. Store the choice instead and apply it once
        // the lobby has a local player (host/join start the watcher).
        if (lobby == null || TryGetLocalLobbyPlayer(lobby) == null)
        {
            _pendingTestCharacter = character;
            _pendingTestSceneId = scene.GetInstanceId();
            if (GetMultiplayerTestStatus(scene, lobby) != "idle")
                WatchPendingTestCharacter(scene);
            return Ok($"Stored {character.Id.Entry}; it is applied when host/join creates the lobby",
                ("character", character.Id.Entry), ("pending", true));
        }

        ClearPendingTestCharacter();
        var error = ApplyMultiplayerTestCharacter(scene, paginator, lobby, character);
        return error ?? Ok($"Selected {character.Id.Entry}", ("character", character.Id.Entry));
    }

    // Moves the paginator and makes sure the scene's own change handler ran so the
    // lobby gets the new character. Returns an error result, or null on success.
    private static Dictionary<string, object?>? ApplyMultiplayerTestCharacter(
        NMultiplayerTest scene,
        NMultiplayerTestCharacterPaginator paginator,
        StartRunLobby lobby,
        CharacterModel character)
    {
        int index = GetMultiplayerTestCharacters(paginator).IndexOf(character);
        if (index < 0)
            return Error($"Character {character.Id.Entry} is no longer offered by the paginator");
        try
        {
            paginator.SetIndex(index);
        }
        catch (Exception ex)
        {
            return Error($"NPaginator.SetIndex failed: {ex.Message} (game version changed?)");
        }

        if (TryGetLocalLobbyPlayer(lobby) is { } local && local.character == character)
            return null;
        var handled = InvokeMultiplayerTestHandler(scene, "OnCharacterChanged", "", character);
        return (string?)handled["status"] == "ok" ? null : handled;
    }

    // Pending character chosen before a lobby existed. Applied on the main thread by a
    // SceneTree.ProcessFrame watcher started after host/join, which stops once applied,
    // when the scene goes away, or after PendingTestCharacterTimeout.
    private static CharacterModel? _pendingTestCharacter;
    private static ulong _pendingTestSceneId;
    private static bool _pendingTestWatching;
    private static DateTime _pendingTestDeadline;
    private static readonly TimeSpan PendingTestCharacterTimeout = TimeSpan.FromSeconds(60);

    private static Dictionary<string, object?> StartPendingCharacterAfter(
        NMultiplayerTest scene,
        Dictionary<string, object?> handlerResult)
    {
        if ((string?)handlerResult["status"] == "ok" && _pendingTestCharacter != null)
        {
            WatchPendingTestCharacter(scene);
            handlerResult["pending_character"] = SafeGetCharacterId(() => _pendingTestCharacter);
        }
        return handlerResult;
    }

    private static void WatchPendingTestCharacter(NMultiplayerTest scene)
    {
        _pendingTestDeadline = DateTime.UtcNow + PendingTestCharacterTimeout;
        if (_pendingTestWatching || scene.GetTree() is not { } tree)
            return;
        _pendingTestWatching = true;
        tree.ProcessFrame += ApplyPendingTestCharacterOnFrame;
    }

    private static void ClearPendingTestCharacter()
    {
        _pendingTestCharacter = null;
        if (_pendingTestWatching && Engine.GetMainLoop() is SceneTree tree)
            tree.ProcessFrame -= ApplyPendingTestCharacterOnFrame;
        _pendingTestWatching = false;
    }

    private static void ApplyPendingTestCharacterOnFrame()
    {
        var character = _pendingTestCharacter;
        var scene = GodotObject.InstanceFromId(_pendingTestSceneId) as NMultiplayerTest;
        if (character == null || scene == null || !GodotObject.IsInstanceValid(scene))
        {
            ClearPendingTestCharacter();
            return;
        }

        var lobby = GetInstanceFieldValue(scene, "_lobby") as StartRunLobby;
        if (lobby == null || TryGetLocalLobbyPlayer(lobby) == null)
        {
            if (DateTime.UtcNow > _pendingTestDeadline)
            {
                GD.PrintErr($"[STS2 MCP] Multiplayer test: no lobby within {PendingTestCharacterTimeout.TotalSeconds:0} s; dropped pending character {character.Id.Entry}");
                ClearPendingTestCharacter();
            }
            return;
        }

        ClearPendingTestCharacter();
        if (GetInstanceFieldValue(scene, "_characterPaginator") is not NMultiplayerTestCharacterPaginator paginator)
        {
            GD.PrintErr("[STS2 MCP] Multiplayer test: character paginator not found; pending character not applied");
            return;
        }
        var error = ApplyMultiplayerTestCharacter(scene, paginator, lobby, character);
        if (error != null)
            GD.PrintErr($"[STS2 MCP] Multiplayer test: applying pending character {character.Id.Entry} failed: {error["error"]}");
    }

    private static List<string> MissingMultiplayerTestMembers()
    {
        var missing = new List<string>();
        foreach (var name in new[] { "_lobby", "_ipField", "_idField", "_characterPaginator" })
            if (typeof(NMultiplayerTest).GetField(name, MultiplayerTestMethodFlags) == null)
                missing.Add(name);
        return missing;
    }

    private static Dictionary<string, object?> InvokeMultiplayerTestHandler(
        NMultiplayerTest scene,
        string methodName,
        string message,
        params object[] args)
    {
        var method = typeof(NMultiplayerTest).GetMethod(methodName, MultiplayerTestMethodFlags);
        if (method == null)
            return Error($"Multiplayer test handler NMultiplayerTest.{methodName} not found (game version changed?)");
        try
        {
            method.Invoke(scene, args);
        }
        catch (Exception ex)
        {
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return Error($"NMultiplayerTest.{methodName} failed: {cause.GetType().Name}: {cause.Message} (game version changed?)");
        }
        return Ok(message);
    }

    private static Dictionary<string, object?> Ok(string message, params (string Key, object? Value)[] extra)
    {
        var result = new Dictionary<string, object?> { ["status"] = "ok", ["message"] = message };
        foreach (var (key, val) in extra)
            result[key] = val;
        return result;
    }
}
