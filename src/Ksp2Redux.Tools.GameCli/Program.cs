using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const int DefaultPort = 51693;
const int DefaultEditorPort = 51692;
const int ConnectTimeoutSeconds = 5;
const int ResponseTimeoutSeconds = 30;
const string RuntimePortFileName = "KSP2Redux.CliIntegration.RuntimePort";
const string EditorPortFileName = "KSP2Redux.CliIntegration.EditorPort";
const string McpToolsCacheFileName = "KSP2Redux.CliIntegration.McpTools.json";

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

try
{
    if (args[0] == "mcp")
    {
        await RunMcp();
        return 0;
    }

    JsonNode result = await RunCommand(args);
    Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<JsonNode> RunCommand(string[] args)
{
    string command = args[0];
    Dictionary<string, string?> options = ParseOptions(args.Skip(1).ToArray());
    int port = GetPort(options);
    int editorPort = GetEditorPort(options);

    return command switch
    {
        "call" => await CallRaw(port, RequiredArg(args, 1, "method"), JsonObject.Parse(args.Length > 2 ? args[2] : "{}")?.AsObject() ?? [], GetDouble(options, "timeout", ResponseTimeoutSeconds)),
        "editor-call" => await CallRaw(
            editorPort,
            RequiredArg(args, 1, "method"),
            JsonObject.Parse(args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "{}")?.AsObject() ?? [],
            GetDouble(options, "timeout", ResponseTimeoutSeconds)),
        "editor-state" => await CallRaw(editorPort, "editor.get_state", []),
        "play" => await Play(port, editorPort, GetDouble(options, "timeout", 60), GetBool(options, "no-wait", false)),
        "play-save" => await PlaySave(port, editorPort, RequiredArg(args, 1, "path"), GetDouble(options, "timeout", 180), GetBool(options, "no-wait", false)),
        "restart-save" => await RestartSave(port, editorPort, RequiredArg(args, 1, "path"), GetDouble(options, "timeout", 420)),
        "ping" => await CallRaw(port, "session.ping", []),
        "state" => await CallRaw(port, "session.get_state", []),
        "wait" => await WaitForCliIntegration(port, GetDouble(options, "timeout", 60)),
        "wait-state" => await WaitForState(port, RequiredArg(args, 1, "state"), GetDouble(options, "timeout", 120)),
        "wait-message" => await WaitForMessage(port, RequiredArg(args, 1, "messageType"), options),
        "watchers" => await CallRaw(port, "watcher.list", []),
        "watcher" => await Watcher(port, args, options),
        "wait-watcher" => await WaitWatcher(port, RequiredArg(args, 1, "watcherType"), options),
        "logs" => await CallRaw(port, "session.get_logs", Obj(("limit", GetInt(options, "limit", 100)))),
        "activity" => await CallRaw(port, "session.get_activity", Obj(("limit", GetInt(options, "limit", 100)))),
        "load-save" => await CallRaw(port, "save.load_save", Obj(("path", RequiredArg(args, 1, "path")))),
        "load-save-main-menu" => await LoadSaveMainMenu(port, RequiredArg(args, 1, "path"), GetDouble(options, "timeout", 120)),
        "quick-save" => await CallRaw(port, "save.quick_save", []),
        "save-as" => await CallRaw(port, "save.save_as", Obj(("path", RequiredArg(args, 1, "path")))),
        "quick-launch" => await CallRaw(port, "vessel.launch_quick", Obj(("kind", args.Length > 1 ? args[1] : "launchpad"))),
        "launch-active-assembly" => await CallRaw(port, "vessel.launch_active_assembly", []),
        "active-vessel" => await CallRaw(port, "telemetry.active_vessel", []),
        "vessels" => await CallRaw(port, "vessel.list", []),
        "controls" => await CallRaw(port, "flight.get_controls", []),
        "pause" => await Pause(port, args, options, pausedDefault: true),
        "resume" or "unpause" => await Pause(port, args, options, pausedDefault: false),
        "clock" => await CallRaw(port, "clock.get", []),
        "step" => await CallRaw(port, "clock.step", Obj(("ticks", args.Length > 1 && !args[1].StartsWith("--") ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1))),
        "run-until" => await RunUntil(port, options),
        "sas" => await Sas(port, args, options),
        "speed-mode" => await CallRaw(port, "flight.set_speed_mode", Obj(("mode", RequiredArg(args, 1, "mode")))),
        "throttle" => await CallRaw(port, "flight.set_throttle", Obj(("value", double.Parse(RequiredArg(args, 1, "value"), CultureInfo.InvariantCulture)))),
        "flight-inputs" => await FlightInputs(port, options),
        "stage" => await CallRaw(port, "flight.stage", []),
        "action-group" => await ActionGroup(port, args, options),
        "heat-shields" => await CallRaw(port, "telemetry.heat_shields", Obj(("guid", GetOption(options, "guid")))),
        "telemetry" => await Telemetry(port, args),
        "parts" => await CallRaw(port, "part.list", Obj(("filter", GetOption(options, "filter")), ("guid", GetOption(options, "guid")))),
        "part-action" => await CallRaw(port, "part.invoke_action", Obj(("partGuid", RequiredArg(args, 1, "partGuid")), ("action", RequiredArg(args, 2, "action")))),
        "part" => await Part(port, args, options),
        "transfer-start" => await TransferStart(port, options),
        "transfer-stop" => await CallRaw(port, "part.transfer_stop", []),
        "teleport" => await Teleport(port, args, options),
        "screenshot" => await CallRaw(port, "camera.screenshot", Obj(("path", args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null))),
        "eval" => await Eval(port, args),
        "cheat" => await Cheat(port, args),
        "quit-to-menu" => await CallRaw(port, "session.quit_to_main_menu", []),
        "events-start" => await EventsStart(port, options),
        "events-stop" => await CallRaw(port, "events.stop", []),
        "events-drain" => await CallRaw(port, "events.drain", Obj(("clear", !GetBool(options, "keep", false)))),
        "list-scenarios" => await CallRaw(port, "playtest.list_scenarios", []),
        "load-scenario" => await LoadScenario(port, args, options),
        "perf-capture" => await PerfCapture(port, options),
        "benchmark-structural" => await BenchmarkStructural(port, editorPort, options),
        "perf-chunks" => await CallRaw(port, "perf.chunks", []),
        "perf-markers" => await CallRaw(port, "perf.markers", []),
        "begin-run" => await BeginRun(port, args, options),
        "assert" => await PlaytestAssert(port, args, options),
        "end-run" => await CallRaw(port, "playtest.end_run", Obj(("result", GetOption(options, "result")), ("reason", GetOption(options, "reason")))),
        "abort-run" => await CallRaw(port, "playtest.abort", Obj(("reason", GetOption(options, "reason")))),
        "run-report" => await CallRaw(port, "playtest.report", []),
        "watch" => await Watch(options),
        "stop" => await Stop(port, editorPort),
        _ => throw new ArgumentException($"Unknown command '{command}'.")
    };
}

static async Task<JsonNode> Play(int port, int editorPort, double timeoutSeconds, bool noWait)
{
    editorPort = await WaitForEditorReadyToPlay(editorPort, timeoutSeconds);
    JsonNode editor = await CallRaw(editorPort, "editor.play", []);
    if (noWait)
    {
        return Obj(("editor", editor));
    }

    JsonNode runtime = await WaitForCliIntegration(port, timeoutSeconds);
    return Obj(("editor", editor), ("runtime", runtime));
}

static async Task<JsonNode> PlaySave(int port, int editorPort, string path, double timeoutSeconds, bool noWait)
{
    editorPort = await WaitForEditorReadyToPlay(editorPort, timeoutSeconds);
    JsonNode editor = await CallRaw(editorPort, "editor.play_save", Obj(("path", path)));
    if (noWait)
    {
        return Obj(("editor", editor));
    }

    JsonNode runtime = await WaitForCliIntegration(port, timeoutSeconds);
    JsonNode loaded = await WaitForSaveLoaded(port, timeoutSeconds);
    return Obj(("editor", editor), ("runtime", runtime), ("loaded", loaded));
}

// Stops play mode first if the editor is already playing (editor.play_save returns
// accepted:false in that case), waits for the editor to settle, then runs PlaySave.
static async Task<JsonNode> RestartSave(int port, int editorPort, string path, double timeoutSeconds)
{
    JsonNode state = await CallRaw(editorPort, "editor.get_state", []);
    if (IsEditorPlaying(state))
    {
        await CallRaw(editorPort, "editor.stop", []);
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            await Task.Delay(1000);
            try
            {
                // Exiting play mode triggers a domain reload that restarts the editor
                // server (possibly on a fresh port), so failures here mean "still busy".
                editorPort = TryReadProjectPortFile(EditorPortFileName) ?? editorPort;
                state = await CallRaw(editorPort, "editor.get_state", []);
                if (!IsEditorPlaying(state) &&
                    state?["isCompiling"]?.GetValue<bool>() != true &&
                    state?["isUpdating"]?.GetValue<bool>() != true)
                {
                    break;
                }
            }
            catch
            {
                // editor unreachable while reloading; keep waiting
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the editor to stop play mode and settle.");
            }
        }
    }

    return await PlaySave(port, editorPort, path, timeoutSeconds, noWait: false);
}

static bool IsEditorPlaying(JsonNode state)
{
    return state?["isPlaying"]?.GetValue<bool>() == true ||
        state?["isPlayingOrWillChangePlaymode"]?.GetValue<bool>() == true;
}

static async Task<int> WaitForEditorReadyToPlay(int editorPort, double timeoutSeconds)
{
    const int EnteredEditMode = 0;
    const int UnknownPlayModeState = -1;
    const int RequiredStableSamples = 8;

    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    JsonNode? lastHeartbeat = null;
    Exception? lastError = null;
    long previousTick = -1;
    int stableSamples = 0;
    int currentPort = editorPort;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            currentPort = TryReadProjectPortFile(EditorPortFileName) ?? currentPort;
            JsonNode heartbeat = await CallRaw(
                currentPort,
                "editor.heartbeat",
                [],
                readTimeoutSeconds: 2,
                connectTimeoutSeconds: 2
            );
            lastHeartbeat = heartbeat;
            bool isPlaying = heartbeat?["isPlaying"]?.GetValue<bool>() == true ||
                heartbeat?["isPlayingOrWillChangePlaymode"]?.GetValue<bool>() == true;
            if (isPlaying)
            {
                throw new InvalidOperationException(
                    "Unity is already in or entering play mode. Use restart-save to replace the active session."
                );
            }

            int playModeState = heartbeat?["playModeState"]?.GetValue<int>() ?? UnknownPlayModeState;
            bool isReady = heartbeat?["isCompiling"]?.GetValue<bool>() != true &&
                heartbeat?["isUpdating"]?.GetValue<bool>() != true &&
                (playModeState == EnteredEditMode || playModeState == UnknownPlayModeState);
            long tick = heartbeat?["tick"]?.GetValue<long>() ?? -1;
            stableSamples = isReady && tick > previousTick ? stableSamples + 1 : 0;
            previousTick = tick;
            if (stableSamples >= RequiredStableSamples)
            {
                return currentPort;
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            lastError = ex;
            stableSamples = 0;
        }

        await Task.Delay(250);
    }

    string detail = lastHeartbeat?.ToJsonString() ?? lastError?.Message ?? "no heartbeat";
    throw new TimeoutException($"Timed out waiting for Unity to settle in edit mode. Last result: {detail}");
}

static async Task<JsonNode> Stop(int port, int editorPort)
{
    try
    {
        JsonNode editor = await CallRaw(editorPort, "editor.stop", []);
        return Obj(("editor", editor));
    }
    catch
    {
        JsonNode runtime = await CallRaw(port, "session.stop_play_mode", []);
        return Obj(("runtime", runtime));
    }
}

static async Task<JsonNode> LoadSaveMainMenu(int port, string path, double timeoutSeconds)
{
    JsonNode state = await WaitForState(port, "MainMenu", timeoutSeconds);
    JsonNode load = await CallRaw(port, "save.load_when_main_menu", Obj(("path", path)));
    return Obj(("state", state), ("load", load));
}

static async Task<JsonNode> Telemetry(int port, string[] args)
{
    string target = args.Length > 1 ? args[1] : "active";
    if (target is "active" or "active-vessel")
    {
        return await CallRaw(port, "telemetry.active_vessel", []);
    }

    return await CallRaw(port, "telemetry.vessel", Obj(("guid", target)));
}

static async Task<JsonNode> Teleport(int port, string[] args, Dictionary<string, string?> options)
{
    string mode = RequiredArg(args, 1, "orbit|surface");
    if (mode == "orbit")
    {
        return await CallRaw(
            port,
            "teleport.to_orbit",
            Obj(
                ("guid", GetOption(options, "guid")),
                ("body", RequiredOption(options, "body")),
                ("sma", GetDouble(options, "sma", required: true)),
                ("ecc", GetDouble(options, "ecc", required: true)),
                ("inc", GetDouble(options, "inc", required: true)),
                ("lan", GetDouble(options, "lan", required: true)),
                ("argPe", GetDouble(options, "argPe", required: true)),
                ("meanAnomaly", GetDouble(options, "meanAnomaly", required: true)),
                ("epoch", GetOptionalDouble(options, "epoch"))
            )
        );
    }

    if (mode == "surface")
    {
        return await CallRaw(
            port,
            "teleport.to_surface",
            Obj(
                ("guid", GetOption(options, "guid")),
                ("body", RequiredOption(options, "body")),
                ("lat", GetDouble(options, "lat", required: true)),
                ("lon", GetDouble(options, "lon", required: true)),
                ("altitude", GetDouble(options, "altitude", required: true)),
                ("verticalSpeed", GetOptionalDouble(options, "verticalSpeed"))
            )
        );
    }

    throw new ArgumentException("teleport mode must be 'orbit' or 'surface'.");
}

static async Task<JsonNode> Sas(int port, string[] args, Dictionary<string, string?> options)
{
    string? positional = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
    string? mode = GetOption(options, "mode");
    bool? enabled = GetOptionalBool(options, "enabled");
    if (positional != null)
    {
        if (positional.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
        }
        else if (positional.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
        }
        else
        {
            mode = positional;
        }
    }

    return await CallRaw(
        port,
        "flight.set_sas",
        Obj(
            ("enabled", enabled),
            ("mode", mode),
            ("speedMode", GetOption(options, "speed-mode") ?? GetOption(options, "speedMode"))
        )
    );
}

static async Task<JsonNode> FlightInputs(int port, Dictionary<string, string?> options)
{
    return await CallRaw(
        port,
        "flight.set_inputs",
        Obj(
            ("throttle", GetOptionalDouble(options, "throttle")),
            ("pitch", GetOptionalDouble(options, "pitch")),
            ("yaw", GetOptionalDouble(options, "yaw")),
            ("roll", GetOptionalDouble(options, "roll")),
            ("wheelSteer", GetOptionalDouble(options, "wheel-steer") ?? GetOptionalDouble(options, "wheelSteer")),
            ("wheelThrottle", GetOptionalDouble(options, "wheel-throttle") ?? GetOptionalDouble(options, "wheelThrottle")),
            ("translateX", GetOptionalDouble(options, "translate-x") ?? GetOptionalDouble(options, "translateX")),
            ("translateY", GetOptionalDouble(options, "translate-y") ?? GetOptionalDouble(options, "translateY")),
            ("translateZ", GetOptionalDouble(options, "translate-z") ?? GetOptionalDouble(options, "translateZ")),
            ("killRotation", GetOptionalBool(options, "kill-rotation") ?? GetOptionalBool(options, "killRotation")),
            ("brakes", GetOptionalBool(options, "brakes")),
            ("gearUp", GetOptionalBool(options, "gear-up") ?? GetOptionalBool(options, "gearUp")),
            ("gearDown", GetOptionalBool(options, "gear-down") ?? GetOptionalBool(options, "gearDown")),
            ("headlight", GetOptionalBool(options, "headlight"))
        )
    );
}

static async Task<JsonNode> Pause(int port, string[] args, Dictionary<string, string?> options, bool pausedDefault)
{
    bool paused = pausedDefault;
    string? positional = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
    if (positional != null)
    {
        paused = positional.ToLowerInvariant() switch
        {
            "on" or "true" or "paused" or "pause" => true,
            "off" or "false" or "unpaused" or "resume" or "unpause" => false,
            _ => throw new ArgumentException("pause argument must be on/off, true/false, paused/unpaused, or resume/unpause.")
        };
    }

    paused = GetOptionalBool(options, "paused") ??
        GetOptionalBool(options, "is-paused") ??
        GetOptionalBool(options, "isPaused") ??
        GetOptionalBool(options, "value") ??
        paused;
    return await CallRaw(port, "flight.set_pause", Obj(("paused", paused)));
}

static async Task<JsonNode> ActionGroup(int port, string[] args, Dictionary<string, string?> options)
{
    string group = RequiredArg(args, 1, "group");
    string? action = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : GetOption(options, "action");
    bool? state = action?.ToLowerInvariant() switch
    {
        "on" or "true" => true,
        "off" or "false" => false,
        _ => GetOptionalBool(options, "state") ?? GetOptionalBool(options, "enabled")
    };
    string mode = action is "on" or "off" or "true" or "false" or null ? "set" : action;
    return await CallRaw(port, "flight.action_group", Obj(("group", group), ("mode", mode), ("state", state)));
}

static async Task<JsonNode> Eval(int port, string[] args)
{
    string mode = RequiredArg(args, 1, "lua|csharp");
    string expression = string.Join(' ', args.Skip(2));
    if (string.IsNullOrWhiteSpace(expression))
    {
        throw new ArgumentException("eval requires an expression.");
    }

    return mode switch
    {
        "lua" => await CallRaw(port, "eval.lua", Obj(("script", expression))),
        "csharp" or "cs" => await CallRaw(port, "eval.csharp", Obj(("code", expression))),
        "reflect" or "csharp-reflect" => await CallRaw(port, "eval.csharp_reflect", Obj(("expression", expression))),
        _ => throw new ArgumentException("eval mode must be 'lua', 'csharp', or 'reflect'.")
    };
}

static async Task<JsonNode> Cheat(int port, string[] args)
{
    string? name = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
    if (name == null)
    {
        return await CallRaw(port, "cheat.get", []);
    }

    bool enabled = RequiredArg(args, 2, "on|off").ToLowerInvariant() switch
    {
        "on" or "true" => true,
        "off" or "false" => false,
        _ => throw new ArgumentException("cheat state must be 'on' or 'off'.")
    };
    return await CallRaw(port, "cheat.set", Obj(("cheat", name), ("enabled", enabled)));
}

static async Task<JsonNode> EventsStart(int port, Dictionary<string, string?> options)
{
    JsonArray types = [];
    foreach (string type in RequiredOption(options, "types").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        types.Add(type);
    }

    return await CallRaw(
        port,
        "events.start",
        Obj(
            ("types", types),
            ("depth", GetInt(options, "depth", 1)),
            ("limit", GetInt(options, "limit", 1000))
        )
    );
}

static async Task<JsonNode> BeginRun(int port, string[] args, Dictionary<string, string?> options)
{
    JsonArray? deny = null;
    string? denyRaw = GetOption(options, "deny");
    if (!string.IsNullOrWhiteSpace(denyRaw))
    {
        deny = [];
        foreach (string pattern in denyRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            deny.Add(pattern);
        }
    }

    return await CallRaw(
        port,
        "playtest.begin_run",
        Obj(
            ("name", RequiredArg(args, 1, "name")),
            ("scenario", GetOption(options, "scenario")),
            ("denyPatterns", deny),
            ("runDeadlineSeconds", GetOptionalDouble(options, "deadline")),
            ("stallSeconds", GetOptionalDouble(options, "stall"))
        )
    );
}

static async Task<JsonNode> PlaytestAssert(int port, string[] args, Dictionary<string, string?> options)
{
    return await CallRaw(
        port,
        "playtest.assert",
        Obj(
            ("name", RequiredArg(args, 1, "name")),
            ("path", RequiredOption(options, "path")),
            ("op", RequiredOption(options, "op")),
            ("value", RequiredOption(options, "value"))
        )
    );
}

static async Task<JsonNode> RunUntil(int port, Dictionary<string, string?> options)
{
    string kind = RequiredOption(options, "kind");
    string? op = GetOption(options, "op");
    if (op == null && kind is "ut" or "ticks")
    {
        op = "gte";
    }

    double timeoutSeconds = GetDouble(options, "timeout", 300);
    bool noPause = GetBool(options, "no-pause", false);
    return await CallRaw(
        port,
        "clock.run_until",
        Obj(
            ("kind", kind),
            ("path", GetOption(options, "path")),
            ("op", op),
            ("value", RequiredOption(options, "value")),
            ("timeoutSeconds", timeoutSeconds),
            ("autoPause", noPause ? false : (bool?)null)
        ),
        timeoutSeconds + 30
    );
}

static async Task<JsonNode> LoadScenario(int port, string[] args, Dictionary<string, string?> options)
{
    double timeoutSeconds = GetDouble(options, "timeout", 180);
    return await CallRaw(
        port,
        "playtest.load_scenario",
        Obj(
            ("name", RequiredArg(args, 1, "name")),
            ("timeoutSeconds", timeoutSeconds),
            ("settleTicks", GetInt(options, "settle-ticks", 50))
        ),
        timeoutSeconds + 30
    );
}

static async Task<JsonNode> PerfCapture(int port, Dictionary<string, string?> options)
{
    JsonArray? markers = null;
    string? markersRaw = GetOption(options, "markers");
    if (!string.IsNullOrWhiteSpace(markersRaw))
    {
        markers = SplitToArray(markersRaw);
    }

    return await CallRaw(
        port,
        "perf.capture",
        Obj(("markers", markers), ("ticks", GetInt(options, "ticks", 250))),
        600
    );
}

static async Task<JsonNode> BenchmarkStructural(
    int port,
    int editorPort,
    Dictionary<string, string?> options)
{
    string campaign = Path.GetFullPath(RequiredOption(options, "campaign"));
    if (!Directory.Exists(campaign))
    {
        throw new DirectoryNotFoundException($"Campaign directory does not exist: {campaign}");
    }

    string[] modes = RequiredOption(options, "modes")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(mode => mode.ToLowerInvariant())
        .ToArray();
    if (modes.Length == 0 || modes.Any(mode => mode is not ("analytical" or "legacy")))
    {
        throw new ArgumentException("--modes must contain analytical and/or legacy.");
    }

    int repetitions = Math.Clamp(GetInt(options, "repetitions", 5), 1, 50);
    int warmupTicks = Math.Clamp(GetInt(options, "warmup-ticks", 500), 10, 20000);
    int sampleTicks = Math.Clamp(GetInt(options, "sample-ticks", 1000), 10, 20000);
    bool exercisePresentation = GetBool(options, "exercise-presentation", false);
    bool forceOwnerSleep = GetBool(options, "force-owner-sleep", false);
    int switchCycles = Math.Clamp(GetInt(options, "switch-cycles", 0), 0, 100);
    string? switchTargetGuid = GetOption(options, "switch-target-guid");
    double switchTimeoutSeconds = Math.Clamp(
        GetDouble(options, "switch-timeout-seconds", 1200),
        60,
        3600);
    string? playerExe = GetOption(options, "player-exe");
    if (!string.IsNullOrWhiteSpace(playerExe))
    {
        playerExe = Path.GetFullPath(playerExe);
        if (!File.Exists(playerExe))
        {
            throw new FileNotFoundException(
                "The standalone benchmark player executable does not exist.",
                playerExe);
        }
    }
    int playerPort = Math.Clamp(GetInt(options, "player-port", 52693), 1024, 65535);
    string outputRoot = Path.GetFullPath(RequiredOption(options, "output"));
    string runDirectory = Path.Combine(
        outputRoot,
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
    Directory.CreateDirectory(runDirectory);

    string[] requestedSaves = (GetOption(options, "saves") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    string[] saves = Directory.GetFiles(campaign, "*.json", SearchOption.TopDirectoryOnly)
        .Where(path => char.IsDigit(Path.GetFileName(path)[0]))
        .Where(path => requestedSaves.Length == 0 ||
            requestedSaves.Any(request =>
                Path.GetFileNameWithoutExtension(path).StartsWith(request, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(path), request, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (saves.Length == 0)
    {
        throw new InvalidOperationException("No numbered benchmark save JSON files matched the campaign/options.");
    }

    string gitWorkingDirectory = FindProjectRoot() ?? Directory.GetCurrentDirectory();
    JsonObject provenance = Obj(
        ("campaign", campaign),
        ("runDirectory", runDirectory),
        ("repetitions", repetitions),
        ("warmupTicks", warmupTicks),
        ("sampleTicks", sampleTicks),
        ("exercisePresentation", exercisePresentation),
        ("forceOwnerSleep", forceOwnerSleep),
        ("switchCycles", switchCycles),
        ("switchTargetGuid", switchTargetGuid),
        ("switchTimeoutSeconds", switchTimeoutSeconds),
        ("executionSurface", playerExe == null ? "editor-play-mode" : "development-player"),
        ("playerExe", playerExe),
        ("playerPort", playerExe == null ? null : playerPort),
        ("modes", StringArray(modes)),
        ("startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
        ("machineName", Environment.MachineName),
        ("operatingSystem", Environment.OSVersion.ToString()),
        ("processArchitecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()),
        ("gitWorkingDirectory", gitWorkingDirectory),
        ("gitBranch", ReadProcessOutput("git", "branch --show-current", gitWorkingDirectory)),
        ("gitCommit", ReadProcessOutput("git", "rev-parse HEAD", gitWorkingDirectory)),
        ("gitStatus", ReadProcessOutput("git", "status --short", gitWorkingDirectory))
    );
    JsonArray runs = [];
    try
    {
        for (int repetition = 0; repetition < repetitions; repetition++)
        {
            foreach (string save in saves)
            {
                for (int modeOffset = 0; modeOffset < modes.Length; modeOffset++)
                {
                    string mode = modes[(modeOffset + repetition) % modes.Length];
                    JsonObject run = await RunStructuralBenchmarkLoad(
                        port,
                        save,
                        mode,
                        repetition + 1,
                        warmupTicks,
                        sampleTicks,
                        runDirectory,
                        editorPort,
                        exercisePresentation,
                        forceOwnerSleep,
                        switchCycles,
                        switchTargetGuid,
                        switchTimeoutSeconds,
                        playerExe,
                        playerPort);
                    runs.Add(run);
                    if (provenance["runtime"] == null && run["environment"] != null)
                    {
                        provenance["runtime"] = run["environment"]!.DeepClone();
                    }
                    string runName =
                        $"{Path.GetFileNameWithoutExtension(save)}__{mode}__r{repetition + 1}.json";
                    File.WriteAllText(
                        Path.Combine(runDirectory, SanitizeFileName(runName)),
                        run.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
    }
    finally
    {
        if (playerExe == null)
        {
            try
            {
                await StopBenchmarkPlaySession(editorPort, 240);
            }
            catch
            {
                // The report still contains the last stable data if the editor is already stopping.
            }
        }
    }

    provenance["finishedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    int failedRunCount = runs
        .OfType<JsonObject>()
        .Count(run => run["completed"]?.GetValue<bool>() != true);
    bool allRunsCompleted = failedRunCount == 0;
    JsonObject report = new()
    {
        ["completed"] = allRunsCompleted,
        ["failedRunCount"] = failedRunCount,
        ["provenance"] = provenance,
        ["runs"] = runs
    };
    File.WriteAllText(
        Path.Combine(runDirectory, "structural-benchmark.json"),
        report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(
        Path.Combine(runDirectory, "summary.csv"),
        BuildStructuralSummaryCsv(runs));
    return Obj(
        ("completed", allRunsCompleted),
        ("failedRunCount", failedRunCount),
        ("runDirectory", runDirectory),
        ("runCount", runs.Count),
        ("json", Path.Combine(runDirectory, "structural-benchmark.json")),
        ("csv", Path.Combine(runDirectory, "summary.csv"))
    );
}

static async Task<JsonObject> RunStructuralBenchmarkLoad(
    int port,
    string save,
    string mode,
    int repetition,
    int warmupTicks,
    int sampleTicks,
    string runDirectory,
    int editorPort,
    bool exercisePresentation,
    bool forceOwnerSleep,
    int switchCycles,
    string? switchTargetGuid,
    double switchTimeoutSeconds,
    string? playerExe,
    int playerPort)
{
    JsonObject run = Obj(
        ("save", save),
        ("saveName", Path.GetFileNameWithoutExtension(save)),
        ("mode", mode),
        ("repetition", repetition),
        ("startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
    );
    JsonArray samples = [];
    run["samples"] = samples;
    Process? standalonePlayer = null;
    try
    {
        JsonNode freshPlaySession;
        if (playerExe == null)
        {
            freshPlaySession = await StartFreshBenchmarkPlaySession(port, editorPort, 900);
        }
        else
        {
            (standalonePlayer, freshPlaySession) =
                await StartFreshBenchmarkPlayerSession(playerExe, playerPort, 900);
        }
        run["freshPlaySession"] = freshPlaySession.DeepClone();
        port = freshPlaySession["runtimePort"]?.GetValue<int>() ?? port;
        run["environment"] = (await CallRaw(port, "perf.environment", [])).DeepClone();
        run["modeSelection"] =
            (await CallRaw(port, "physics.set_structural_mode", Obj(("mode", mode)))).DeepClone();
        run["load"] =
            (await CallRaw(port, "save.load_when_main_menu", Obj(("path", save)))).DeepClone();
        // The 1,062-part station can legitimately spend roughly fifteen minutes
        // materializing on slower Editor runs. The orchestrator timeout must exceed
        // that known-valid window; readiness and terminal load errors remain separate.
        await WaitForBenchmarkSaveLoaded(port, 1200);
        run["stableState"] = (await WaitForStableBenchmarkState(port, 3, 240)).DeepClone();

        JsonNode? active = await TryCallRaw(port, "vessel.get_active", []);
        if (active == null &&
            Path.GetFileNameWithoutExtension(save).StartsWith("8 -", StringComparison.OrdinalIgnoreCase))
        {
            JsonNode vesselsNode = await CallRaw(port, "vessel.list", []);
            JsonObject? largest = vesselsNode.AsArray()
                .OfType<JsonObject>()
                .OrderByDescending(vessel => vessel["partCount"]?.GetValue<int>() ?? 0)
                .FirstOrDefault();
            string? largestGuid = largest?["guid"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(largestGuid))
            {
                throw new InvalidOperationException("Save 8 contains no vessel that can be activated.");
            }

            run["initialActivation"] =
                (await CallRaw(
                    port,
                    "vessel.set_active",
                    Obj(("guid", largestGuid), ("observe", false)),
                    readTimeoutSeconds: switchTimeoutSeconds + 30)).DeepClone();
            await WaitForStableBenchmarkState(port, 3, 300, largestGuid);
            active = await CallRaw(port, "vessel.get_active", []);
        }

        await CaptureStructuralBenchmarkSample(
            port, save, mode, repetition, "initial", active, warmupTicks, sampleTicks, runDirectory,
            exercisePresentation, forceOwnerSleep, samples);

        if (Path.GetFileNameWithoutExtension(save).StartsWith("7 -", StringComparison.OrdinalIgnoreCase))
        {
            JsonNode vesselsNode = await CallRaw(port, "vessel.list", []);
            JsonObject? largest = vesselsNode.AsArray()
                .OfType<JsonObject>()
                .OrderByDescending(vessel => vessel["partCount"]?.GetValue<int>() ?? 0)
                .FirstOrDefault();
            string? largestGuid = largest?["guid"]?.GetValue<string>();
            string? activeGuid = active?["guid"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(largestGuid) &&
                !string.Equals(largestGuid, activeGuid, StringComparison.OrdinalIgnoreCase))
            {
                run["backgroundActivation"] =
                    (await CallRaw(
                        port,
                        "vessel.set_active",
                        Obj(("guid", largestGuid)),
                        readTimeoutSeconds: switchTimeoutSeconds + 30)).DeepClone();
                await WaitForStableBenchmarkState(port, 3, 300, largestGuid);
                JsonNode activated = await CallRaw(port, "vessel.get_active", []);
                JsonObject largestSample = await CaptureStructuralBenchmarkSample(
                    port, save, mode, repetition, "largest-background-vessel", activated,
                    warmupTicks, sampleTicks, runDirectory, exercisePresentation, forceOwnerSleep,
                    samples);

                if (exercisePresentation &&
                    mode == "analytical" &&
                    !string.IsNullOrWhiteSpace(activeGuid))
                {
                    JsonObject inactiveValidation = Obj(
                        ("observedVesselGuid", largestGuid),
                        ("newActiveVesselGuid", activeGuid));
                    run["presentationInactiveValidation"] = inactiveValidation;
                    inactiveValidation["beforeSwitch"] =
                        largestSample["presentationDiagnostics"]?.DeepClone();
                    inactiveValidation["switch"] = (await CallRaw(
                        port,
                        "vessel.set_active",
                        Obj(("guid", activeGuid), ("observe", false)),
                        readTimeoutSeconds: switchTimeoutSeconds + 30)).DeepClone();
                    inactiveValidation["stableState"] =
                        (await WaitForStableBenchmarkState(port, 3, 300, activeGuid)).DeepClone();
                    JsonNode? beforeIdle = await TryCallRaw(
                        port,
                        "perf.structural_presentation",
                        Obj(("guid", largestGuid)));
                    JsonNode? vfxBeforeIdle = await TryCallRaw(
                        port,
                        "telemetry.structural_vfx",
                        Obj(("guid", largestGuid)));
                    inactiveValidation["beforeIdle"] = beforeIdle?.DeepClone();
                    inactiveValidation["vfxBeforeIdle"] = vfxBeforeIdle?.DeepClone();
                    await CallRaw(port, "perf.capture", Obj(("ticks", 50)), 900);
                    JsonNode? afterIdle = await TryCallRaw(
                        port,
                        "perf.structural_presentation",
                        Obj(("guid", largestGuid)));
                    JsonNode? vfxAfterIdle = await TryCallRaw(
                        port,
                        "telemetry.structural_vfx",
                        Obj(("guid", largestGuid)));
                    inactiveValidation["afterIdle"] = afterIdle?.DeepClone();
                    inactiveValidation["vfxAfterIdle"] = vfxAfterIdle?.DeepClone();

                    long? beforeTraversalCount =
                        beforeIdle?["normalTraversalCount"]?.GetValue<long>();
                    long? afterTraversalCount =
                        afterIdle?["normalTraversalCount"]?.GetValue<long>();
                    bool unloaded = beforeIdle == null && afterIdle == null;
                    long? traversalDelta =
                        beforeTraversalCount.HasValue && afterTraversalCount.HasValue
                            ? afterTraversalCount.Value - beforeTraversalCount.Value
                            : null;
                    inactiveValidation["observation"] =
                        unloaded ? "unloaded" : "loaded";
                    inactiveValidation["traversalDelta"] = traversalDelta;
                    inactiveValidation["noTraversal"] =
                        unloaded || traversalDelta == 0;
                }
            }
        }
        if (switchCycles > 0 && mode == "analytical")
        {
            JsonObject lifecycle = Obj(
                ("requestedSwitches", switchCycles),
                ("timeoutSeconds", switchTimeoutSeconds),
                ("startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            run["switchLifecycle"] = lifecycle;
            await RunStructuralSwitchLifecycle(
                port,
                switchCycles,
                switchTargetGuid,
                switchTimeoutSeconds,
                lifecycle);
        }
        run["completed"] = true;
    }
    catch (Exception ex)
    {
        run["completed"] = false;
        run["terminalReason"] = ex.Message;
        if (run["switchLifecycle"] is JsonObject lifecycle)
        {
            lifecycle["completed"] = false;
            lifecycle["completedSwitches"] =
                lifecycle["switches"] is JsonArray lifecycleSwitches
                    ? lifecycleSwitches.Count(point =>
                        point?["completed"]?.GetValue<bool>() == true)
                    : 0;
            lifecycle["terminalReason"] = ex.Message;
            lifecycle["finishedUtc"] =
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
        if (samples.LastOrDefault() is JsonObject failedSample &&
            failedSample["completed"]?.GetValue<bool>() != true)
        {
            failedSample["completed"] = false;
            failedSample["terminalReason"] = ex.Message;
            failedSample["finishedUtc"] =
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
        JsonObject failureEvidence = await CaptureStructuralFailureEvidence(port);
        int retainedRawSampleCount = samples
            .OfType<JsonObject>()
            .Count(sample => sample["capture"]?["raw"] != null);
        failureEvidence["rawSamplesAvailable"] = retainedRawSampleCount > 0;
        failureEvidence["retainedRawSampleCount"] = retainedRawSampleCount;
        run["failureEvidence"] = failureEvidence;
        run["lastState"] = failureEvidence["state"]?.DeepClone();
        run["logs"] = failureEvidence["logs"]?.DeepClone();
        if (playerExe != null)
        {
            run["standaloneFailureArtifacts"] = CaptureStandalonePlayerCrashArtifacts(
                runDirectory,
                Path.GetFileNameWithoutExtension(save),
                mode,
                repetition,
                run["startedUtc"]?.GetValue<string>());
        }
    }
    finally
    {
        try
        {
            await TryCallRaw(port, "session.set_time_scale", Obj(("timeScale", 1)));
            run["stopPlayMode"] = playerExe == null
                ? (await StopBenchmarkPlaySession(editorPort, 240)).DeepClone()
                : (await StopStandaloneBenchmarkPlayer(
                    standalonePlayer,
                    120)).DeepClone();
            if (playerExe != null &&
                run["stopPlayMode"]?["exitCode"]?.GetValue<int>() is int exitCode &&
                exitCode != 0 &&
                run["stopPlayMode"]?["plannedProcessTermination"]?.GetValue<bool>() != true)
            {
                run["completed"] = false;
                run["terminalReason"] =
                    $"Standalone benchmark player exited with code {exitCode}.";
                run["standaloneCrashArtifacts"] = CaptureStandalonePlayerCrashArtifacts(
                    runDirectory,
                    Path.GetFileNameWithoutExtension(save),
                    mode,
                    repetition,
                    run["startedUtc"]?.GetValue<string>());
            }
        }
        catch (Exception ex)
        {
            run["playModeStopError"] = ex.Message;
        }
        run["finishedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }
    return run;
}

static async Task RunStructuralSwitchLifecycle(
    int port,
    int switchCycles,
    string? requestedSecondGuid,
    double timeoutSeconds,
    JsonObject result)
{
    JsonNode current = await CallRaw(port, "vessel.get_active", []);
    string? firstGuid = current["guid"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(firstGuid))
    {
        throw new InvalidOperationException(
            "The structural switch lifecycle requires an active vessel.");
    }

    JsonNode vesselsNode = await CallRaw(port, "vessel.list", []);
    JsonObject[] alternates = vesselsNode.AsArray()
        .OfType<JsonObject>()
        .Where(vessel =>
            !string.Equals(
                vessel["guid"]?.GetValue<string>(),
                firstGuid,
                StringComparison.OrdinalIgnoreCase) &&
            (vessel["partCount"]?.GetValue<int>() ?? 0) > 0)
        .ToArray();
    JsonObject? alternate;
    if (!string.IsNullOrWhiteSpace(requestedSecondGuid))
    {
        alternate = alternates.FirstOrDefault(vessel =>
            string.Equals(
                vessel["guid"]?.GetValue<string>(),
                requestedSecondGuid,
                StringComparison.OrdinalIgnoreCase));
        if (alternate == null)
        {
            throw new InvalidOperationException(
                $"The requested structural lifecycle target '{requestedSecondGuid}' " +
                "is not a distinct vessel with at least one part.");
        }
    }
    else
    {
        // The lifecycle test is intended to tear down and rematerialize the benchmark vessel,
        // not to alternate between the two largest craft in the campaign. Prefer the smallest
        // valid alternate so save 7 uses its two-part lander rather than another 250-part deployer.
        alternate = alternates
            .OrderBy(vessel => vessel["partCount"]?.GetValue<int>() ?? int.MaxValue)
            .ThenBy(vessel => vessel["name"]?.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
    string? secondGuid = alternate?["guid"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(secondGuid))
    {
        throw new InvalidOperationException(
            "The structural switch lifecycle requires a second vessel.");
    }

    result["firstGuid"] = firstGuid;
    result["firstName"] = current["name"]?.GetValue<string>();
    result["firstPartCount"] = current["partCount"]?.GetValue<int>();
    result["secondGuid"] = secondGuid;
    result["secondName"] = alternate?["name"]?.GetValue<string>();
    result["secondPartCount"] = alternate?["partCount"]?.GetValue<int>();
    result["targetSelection"] = string.IsNullOrWhiteSpace(requestedSecondGuid)
        ? "smallest-distinct-vessel"
        : "explicit-guid";
    JsonArray warmup = [];
    JsonArray switches = [];
    result["warmup"] = warmup;
    result["switches"] = switches;

    // Materialize both targets before taking the cache/memory baseline. Cache growth caused by
    // discovering the second vessel's genuinely new part definitions is expected; growth after
    // this point is not.
    JsonObject firstWarmup = [];
    warmup.Add(firstWarmup);
    await ActivateStructuralLifecycleTarget(
        port, firstGuid, 0, "warmup-first", timeoutSeconds, firstWarmup);
    JsonObject secondWarmup = [];
    warmup.Add(secondWarmup);
    await ActivateStructuralLifecycleTarget(
        port, secondGuid, 0, "warmup-second", timeoutSeconds, secondWarmup);
    JsonObject firstReturn = [];
    warmup.Add(firstReturn);
    await ActivateStructuralLifecycleTarget(
        port, firstGuid, 0, "warmup-return", timeoutSeconds, firstReturn);

    // Each baseline must be captured while that vessel is active. Querying the second vessel
    // after returning to the first can legitimately find no ECS/view data because it was packed.
    JsonObject firstBaseline = LifecyclePointSnapshot(firstReturn);
    JsonObject secondBaseline = LifecyclePointSnapshot(secondWarmup);
    result["baseline"] = Obj(
        ("first", firstBaseline),
        ("second", secondBaseline));
    int baselineCacheEntries = Math.Max(
        ReadInt(firstBaseline["activation"], "inertiaCacheEntryCount"),
        ReadInt(secondBaseline["activation"], "inertiaCacheEntryCount"));
    int maximumCacheEntries = baselineCacheEntries;
    double maximumMemoryDriftPercent = 0.0;
    string activeGuid = firstGuid;

    for (int i = 0; i < switchCycles; i++)
    {
        string targetGuid =
            string.Equals(activeGuid, firstGuid, StringComparison.OrdinalIgnoreCase)
                ? secondGuid
                : firstGuid;
        JsonObject point = [];
        switches.Add(point);
        await ActivateStructuralLifecycleTarget(
            port,
            targetGuid,
            i + 1,
            "measured",
            timeoutSeconds,
            point);
        activeGuid = targetGuid;

        JsonNode? memory = point["memory"];
        JsonNode? activation = point["activation"];
        long baselineBytes = string.Equals(
                targetGuid, firstGuid, StringComparison.OrdinalIgnoreCase)
            ? ReadLong(firstBaseline["memory"], "totalBytes")
            : ReadLong(secondBaseline["memory"], "totalBytes");
        long currentBytes = ReadLong(memory, "totalBytes");
        if (baselineBytes > 0 && currentBytes >= 0)
        {
            double driftPercent =
                Math.Abs(currentBytes - baselineBytes) * 100.0 / baselineBytes;
            point["memoryDriftPercent"] = driftPercent;
            maximumMemoryDriftPercent = Math.Max(
                maximumMemoryDriftPercent,
                driftPercent);
        }
        int cacheEntries = ReadInt(activation, "inertiaCacheEntryCount");
        maximumCacheEntries = Math.Max(maximumCacheEntries, cacheEntries);
    }

    JsonObject firstFinal = LatestLifecycleSnapshot(
        switches,
        firstGuid,
        firstBaseline);
    JsonObject secondFinal = LatestLifecycleSnapshot(
        switches,
        secondGuid,
        secondBaseline);
    result["final"] = Obj(
        ("first", firstFinal),
        ("second", secondFinal));
    result["completedSwitches"] = switches.Count;
    result["baselineInertiaCacheEntries"] = baselineCacheEntries;
    result["maximumInertiaCacheEntries"] = maximumCacheEntries;
    result["inertiaCacheBoundedAfterWarmup"] =
        maximumCacheEntries <= baselineCacheEntries;
    result["maximumMemoryDriftPercent"] = maximumMemoryDriftPercent;
    result["memoryWithinFivePercent"] = maximumMemoryDriftPercent <= 5.0;
    result["completed"] = true;
    result["finishedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}

static async Task<JsonObject> ActivateStructuralLifecycleTarget(
    int port,
    string guid,
    int switchNumber,
    string phase,
    double timeoutSeconds,
    JsonObject? retainedPoint = null)
{
    JsonObject point = retainedPoint ?? [];
    point["phase"] = phase;
    point["switchNumber"] = switchNumber;
    point["guid"] = guid;
    point["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    JsonNode? current = await TryCallRaw(port, "vessel.get_active", []);
    string? currentGuid = current?["guid"]?.GetValue<string>();
    if (string.Equals(currentGuid, guid, StringComparison.OrdinalIgnoreCase))
    {
        point["activationRequest"] = Obj(
            ("accepted", true),
            ("alreadyActive", true),
            ("guid", guid));
    }
    else
    {
        point["activationRequest"] = (await CallRaw(
            port,
            "vessel.set_active",
            // Lifecycle validation requires the target's view and physics bubble to
            // materialize. Merely changing the active GUID while leaving the observer
            // at a remote vessel makes readiness correctly remain unavailable.
            Obj(("guid", guid), ("observe", true)),
            readTimeoutSeconds: timeoutSeconds + 30)).DeepClone();
    }
    point["stableState"] =
        (await WaitForStableBenchmarkState(port, 3, timeoutSeconds, guid)).DeepClone();
    JsonNode readiness = await CallRaw(
        port,
        "perf.wait_structural_ready",
        Obj(("guid", guid), ("timeoutSeconds", timeoutSeconds)),
        timeoutSeconds + 30);
    point["readiness"] = readiness.DeepClone();
    if (readiness["ready"]?.GetValue<bool>() != true)
    {
        string terminalReason =
            readiness["terminalReason"]?.GetValue<string>() ??
            "structural readiness returned ready=false";
        throw new InvalidOperationException(
            $"Structural lifecycle target '{guid}' did not become ready: " +
            $"{terminalReason}. Last status: {readiness.ToJsonString()}");
    }
    JsonObject snapshot = await CaptureStructuralLifecycleSnapshot(port, guid);
    if (snapshot["memory"] == null || snapshot["activation"] == null)
    {
        throw new InvalidOperationException(
            $"Structural lifecycle target '{guid}' became ready but its memory or activation " +
            "snapshot was unavailable.");
    }
    point["memory"] = snapshot["memory"]?.DeepClone();
    point["activation"] = snapshot["activation"]?.DeepClone();
    point["finishedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    point["completed"] = true;
    return point;
}

static JsonObject LifecyclePointSnapshot(JsonObject point)
{
    return Obj(
        ("guid", point["guid"]?.DeepClone()),
        ("memory", point["memory"]?.DeepClone()),
        ("activation", point["activation"]?.DeepClone()));
}

static JsonObject LatestLifecycleSnapshot(
    JsonArray switches,
    string guid,
    JsonObject fallback)
{
    for (int i = switches.Count - 1; i >= 0; i--)
    {
        if (switches[i] is JsonObject point &&
            string.Equals(
                point["guid"]?.GetValue<string>(),
                guid,
                StringComparison.OrdinalIgnoreCase))
        {
            return LifecyclePointSnapshot(point);
        }
    }
    return fallback.DeepClone().AsObject();
}

static async Task<JsonObject> CaptureStructuralLifecycleSnapshot(int port, string guid)
{
    return Obj(
        ("guid", guid),
        ("memory", (await TryCallRaw(
            port,
            "perf.structural_memory",
            Obj(("guid", guid))))?.DeepClone()),
        ("activation", (await TryCallRaw(
            port,
            "perf.structural_activation",
            Obj(("guid", guid))))?.DeepClone()));
}

static int ReadInt(JsonNode? node, string property)
{
    return node?[property]?.GetValue<int>() ?? -1;
}

static long ReadLong(JsonNode? node, string property)
{
    return node?[property]?.GetValue<long>() ?? -1;
}

static async Task<JsonObject> CaptureStructuralBenchmarkSample(
    int port,
    string save,
    string mode,
    int repetition,
    string label,
    JsonNode? active,
    int warmupTicks,
    int sampleTicks,
    string runDirectory,
    bool exercisePresentation,
    bool forceOwnerSleep,
    JsonArray samples)
{
    JsonObject sample = Obj(
        ("label", label),
        ("activeVessel", active),
        ("startedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
    );
    // Retain the sample before readiness/warm-up begins. If any later operation fails,
    // the run still serializes the exact last readiness state and any raw capture data
    // already returned by the bridge.
    samples.Add(sample);
    string? guid = active?["guid"]?.GetValue<string>();
    if (!string.IsNullOrWhiteSpace(guid))
    {
        JsonNode readiness = await CallRaw(
            port,
            "perf.wait_structural_ready",
            Obj(("guid", guid), ("timeoutSeconds", 1200)),
            1230);
        sample["readiness"] = readiness.DeepClone();
        if (readiness["ready"]?.GetValue<bool>() != true)
        {
            string terminalReason =
                readiness["terminalReason"]?.GetValue<string>() ??
                "structural readiness returned ready=false";
            throw new InvalidOperationException(
                $"{terminalReason}. Last status: {readiness.ToJsonString()}");
        }
    }
    else
    {
        sample["readiness"] = Obj(
            ("ready", true),
            ("skipped", true),
            ("reason", "save has no active vessel"));
    }

    bool presentationExerciseEnabled = false;
    try
    {
        if (exercisePresentation &&
            mode == "analytical" &&
            !string.IsNullOrWhiteSpace(guid))
        {
            sample["presentationExercise"] = (await CallRaw(
                port,
                "debug.set_structural_presentation_test",
                Obj(("guid", guid), ("enabled", true), ("utilization", 0.75)))).DeepClone();
            presentationExerciseEnabled = true;
        }
        else if (exercisePresentation)
        {
            sample["presentationExercise"] = Obj(
                ("enabled", false),
                ("skipped", true),
                ("reason", mode == "analytical"
                    ? "save has no active vessel"
                    : "presentation exercise applies only to analytical mode"));
        }

        await CallRaw(port, "perf.capture", Obj(("ticks", warmupTicks)), 900);
        if (forceOwnerSleep &&
            mode == "analytical" &&
            !string.IsNullOrWhiteSpace(guid))
        {
            sample["ownerSleepRequest"] = (await CallRaw(
                port,
                "debug.set_structural_owner_sleep",
                Obj(("guid", guid), ("sleep", true)))).DeepClone();
        }
        else if (forceOwnerSleep)
        {
            sample["ownerSleepRequest"] = Obj(
                ("requestedSleep", true),
                ("skipped", true),
                ("reason", mode == "analytical"
                    ? "save has no active vessel"
                    : "forced owner sleep applies only to analytical mode"));
        }
        JsonArray markers = StringArray(
            "Physics.Simulate",
            "FixedUpdate.ScriptRunBehaviourFixedUpdate",
            "FixedUpdate.DirectorFixedUpdate",
            "FixedUpdate.PhysicsFixedUpdate",
            "FixedUpdate.DirectorFixedUpdatePostPhysics",
            "StructuralPhysics.Solver",
            "StructuralPhysics.AnalyticalForceDrain",
            "StructuralPhysics.AnalyticalWrenchApply",
            "StructuralPhysics.AnalyticalPartReadback",
            "StructuralPhysics.PresentationSnapshot",
            "StructuralPhysics.SolverWait",
            "StructuralPhysics.Solver.Initialize",
            "StructuralPhysics.Solver.Reduce",
            "StructuralPhysics.Solver.Connections",
            "StructuralPhysics.Solver.Finalize",
            "RigidbodyBehavior.OnPhysicsWrite()",
            "RigidbodyBehavior.OnFixedUpdate()");
        JsonObject groups = new()
        {
            ["physicsStructural"] = StringArray(
                "Physics.Simulate",
                "StructuralPhysics.Solver",
                "StructuralPhysics.Solver.Initialize",
                "StructuralPhysics.Solver.Reduce",
                "StructuralPhysics.Solver.Connections",
                "StructuralPhysics.Solver.Finalize",
                "StructuralPhysics.AnalyticalForceDrain",
                "StructuralPhysics.AnalyticalWrenchApply",
                "StructuralPhysics.AnalyticalPartReadback",
                "StructuralPhysics.PresentationSnapshot",
                "RigidbodyBehavior.OnPhysicsWrite()",
                "RigidbodyBehavior.OnFixedUpdate()"),
            ["fixedStep"] = StringArray(
                "FixedUpdate.ScriptRunBehaviourFixedUpdate",
                "FixedUpdate.DirectorFixedUpdate",
                "FixedUpdate.PhysicsFixedUpdate",
                "FixedUpdate.DirectorFixedUpdatePostPhysics")
        };
        sample["capture"] = (await CallRaw(
            port,
            "perf.capture",
            Obj(("markers", markers), ("groups", groups), ("raw", true), ("ticks", sampleTicks)),
            1800)).DeepClone();

        if (!string.IsNullOrWhiteSpace(guid))
        {
            sample["structural"] =
                (await TryCallRaw(port, "telemetry.structural", Obj(("guid", guid), ("top", 8))))?.DeepClone();
            sample["activation"] =
                (await TryCallRaw(port, "perf.structural_activation", Obj(("guid", guid))))?.DeepClone();
            sample["memory"] =
                (await TryCallRaw(port, "perf.structural_memory", Obj(("guid", guid))))?.DeepClone();
            sample["presentationDiagnostics"] =
                (await TryCallRaw(port, "perf.structural_presentation", Obj(("guid", guid))))?.DeepClone();
        }
        string screenshotName = SanitizeFileName(
            $"{Path.GetFileNameWithoutExtension(save)}__{mode}__r{repetition}__{label}.png");
        string screenshotPath = Path.Combine(runDirectory, screenshotName);
        sample["screenshot"] = (await TryCallRaw(
            port,
            "camera.screenshot",
            Obj(("path", screenshotPath))))?.DeepClone();
        sample["screenshotFile"] =
            (await WaitForBenchmarkScreenshot(screenshotPath, 15)).DeepClone();
        sample["logs"] = (await TryCallRaw(port, "session.get_logs", Obj(("limit", 200))))?.DeepClone();
    }
    finally
    {
        if (presentationExerciseEnabled)
        {
            sample["presentationExerciseRestore"] = (await TryCallRaw(
                port,
                "debug.set_structural_presentation_test",
                Obj(("guid", guid), ("enabled", false))))?.DeepClone();
        }
    }
    sample["completed"] = true;
    sample["finishedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    return sample;
}

static async Task<JsonObject> WaitForBenchmarkScreenshot(
    string path,
    double timeoutSeconds)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
        FileInfo file = new(path);
        if (file.Exists && file.Length > 0)
        {
            return Obj(
                ("ready", true),
                ("path", path),
                ("bytes", file.Length),
                ("lastWriteUtc", file.LastWriteTimeUtc.ToString(
                    "O",
                    CultureInfo.InvariantCulture)));
        }
        await Task.Delay(100);
    }

    throw new TimeoutException(
        $"The benchmark screenshot was not written within {timeoutSeconds:F0} seconds: {path}");
}

static async Task<JsonObject> CaptureStructuralFailureEvidence(int port)
{
    JsonNode? state = await TryCallRaw(port, "session.get_state", []);
    JsonNode? active = await TryCallRaw(port, "vessel.get_active", []);
    string? guid = active?["guid"]?.GetValue<string>();
    JsonObject evidence = Obj(
        ("capturedUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
        ("state", state?.DeepClone()),
        ("activeVessel", active?.DeepClone()),
        ("rawSamplesAvailable", false),
        ("rawSamplesNote",
            "Raw aligned samples are retained on the partial sample when perf.capture completed; " +
            "none exist when failure occurred before capture."));
    if (!string.IsNullOrWhiteSpace(guid))
    {
        evidence["structural"] = (await TryCallRaw(
            port,
            "telemetry.structural",
            Obj(("guid", guid), ("top", 8))))?.DeepClone();
        evidence["activation"] = (await TryCallRaw(
            port,
            "perf.structural_activation",
            Obj(("guid", guid))))?.DeepClone();
        evidence["memory"] = (await TryCallRaw(
            port,
            "perf.structural_memory",
            Obj(("guid", guid))))?.DeepClone();
        evidence["presentation"] = (await TryCallRaw(
            port,
            "perf.structural_presentation",
            Obj(("guid", guid))))?.DeepClone();
        evidence["vfx"] = (await TryCallRaw(
            port,
            "telemetry.structural_vfx",
            Obj(("guid", guid))))?.DeepClone();
    }
    evidence["logs"] = (await TryCallRaw(
        port,
        "session.get_logs",
        Obj(("limit", 200))))?.DeepClone();
    return evidence;
}

static async Task<JsonNode> WaitForStableBenchmarkState(
    int port,
    int requiredSamples,
    double timeoutSeconds,
    string? expectedActiveGuid = null)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    string? signature = null;
    int stable = 0;
    JsonNode? last = null;
    while (DateTime.UtcNow < deadline)
    {
        // Switching a large vessel into physics can occupy Unity's main thread for much
        // longer than the generic 30-second RPC timeout. Keep both state probes bounded by
        // this operation's explicit deadline so a slow, valid activation is not misreported
        // as a bridge failure.
        double remainingSeconds = Math.Max(
            (deadline - DateTime.UtcNow).TotalSeconds,
            1.0);
        JsonNode state = await CallRaw(
            port,
            "session.get_state",
            [],
            readTimeoutSeconds: remainingSeconds);
        remainingSeconds = Math.Max(
            (deadline - DateTime.UtcNow).TotalSeconds,
            1.0);
        JsonNode? active = await TryCallRaw(
            port,
            "vessel.get_active",
            [],
            readTimeoutSeconds: remainingSeconds);
        string currentSignature = string.Join("|",
            state["globalGameState"]?.ToJsonString(),
            state["isLoaded"]?.ToJsonString(),
            state["isLoading"]?.ToJsonString(),
            active?["guid"]?.ToJsonString(),
            active?["partCount"]?.ToJsonString());
        string? activeGuid = active?["guid"]?.GetValue<string>();
        bool expectedVesselIsActive = string.IsNullOrWhiteSpace(expectedActiveGuid) ||
            string.Equals(activeGuid, expectedActiveGuid, StringComparison.OrdinalIgnoreCase);
        bool valid = state["isLoading"]?.GetValue<bool>() != true &&
            state["isLoaded"]?.GetValue<bool>() == true &&
            expectedVesselIsActive;
        stable = valid && currentSignature == signature ? stable + 1 : valid ? 1 : 0;
        signature = currentSignature;
        last = Obj(
            ("state", state),
            ("activeVessel", active),
            ("expectedActiveGuid", expectedActiveGuid),
            ("expectedVesselIsActive", expectedVesselIsActive),
            ("stableSamples", stable));
        if (stable >= requiredSamples)
        {
            return last;
        }
        await Task.Delay(1000);
    }
    throw new TimeoutException($"Game state did not stabilize. Last sample: {last}");
}

static async Task<JsonNode> WaitForBenchmarkSaveLoaded(int port, double timeoutSeconds)
{
    DateTime started = DateTime.UtcNow;
    DateTime deadline = started.AddSeconds(timeoutSeconds);
    JsonNode? last = null;
    bool sawLoading = false;
    int terminalMenuSamples = 0;
    while (DateTime.UtcNow < deadline)
    {
        // Save materialization runs on Unity's main thread, which can prevent the runtime
        // bridge from servicing even this cheap state request for several minutes. Do not
        // let CallRaw's generic 30-second response timeout defeat the benchmark's explicit
        // long-load deadline.
        double remainingSeconds = Math.Max(
            (deadline - DateTime.UtcNow).TotalSeconds,
            1.0);
        last = await CallRaw(
            port,
            "session.get_state",
            [],
            readTimeoutSeconds: remainingSeconds);
        bool loaded = last["isLoaded"]?.GetValue<bool>() == true;
        bool loading = last["isLoading"]?.GetValue<bool>() == true;
        sawLoading |= loading;
        if (loaded && !loading)
        {
            return last;
        }

        bool returnedToMenu = string.Equals(
                last["globalGameState"]?.GetValue<string>(), "MainMenu", StringComparison.OrdinalIgnoreCase) &&
            !loaded && !loading && (sawLoading || DateTime.UtcNow - started > TimeSpan.FromSeconds(10));
        terminalMenuSamples = returnedToMenu ? terminalMenuSamples + 1 : 0;
        if (terminalMenuSamples >= 3)
        {
            throw new InvalidOperationException(
                $"Save load terminated at unloaded MainMenu. Last state: {last}");
        }
        await Task.Delay(500);
    }
    throw new TimeoutException($"Timed out waiting for benchmark save load. Last state: {last}");
}

static async Task EnsureUnloadedMainMenu(int port, double timeoutSeconds)
{
    JsonNode? state = await TryCallRaw(port, "session.get_state", []);
    bool ready = string.Equals(
            state?["globalGameState"]?.GetValue<string>(), "MainMenu", StringComparison.OrdinalIgnoreCase) &&
        state?["isLoaded"]?.GetValue<bool>() != true &&
        state?["isLoading"]?.GetValue<bool>() != true;
    if (!ready)
    {
        await TryCallRaw(port, "session.quit_to_main_menu", []);
    }

    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
        state = await TryCallRaw(port, "session.get_state", []);
        if (string.Equals(
                state?["globalGameState"]?.GetValue<string>(), "MainMenu", StringComparison.OrdinalIgnoreCase) &&
            state?["isLoaded"]?.GetValue<bool>() != true &&
            state?["isLoading"]?.GetValue<bool>() != true)
        {
            return;
        }
        await Task.Delay(500);
    }
    throw new TimeoutException($"Timed out waiting for unloaded MainMenu. Last state: {state}");
}

static async Task<JsonNode> StartFreshBenchmarkPlaySession(
    int port,
    int editorPort,
    double timeoutSeconds)
{
    JsonNode stopped = await StopBenchmarkPlaySession(editorPort, timeoutSeconds);
    // Returning to edit mode is observable before Unity has necessarily finished releasing the
    // previous play session's native AssetBundle state. An immediate re-entry can block while
    // reopening ksp2.catalog, so give the fully stopped editor a short quiescence interval.
    await Task.Delay(TimeSpan.FromSeconds(10));
    DeleteProjectPortFile(RuntimePortFileName);
    editorPort = TryReadProjectPortFile(EditorPortFileName) ?? editorPort;
    JsonNode editorBeforePlay = await CallRaw(editorPort, "editor.get_state", []);
    AssertExpectedEditorProject(editorBeforePlay);
    JsonNode playModePreparation =
        await CallRaw(editorPort, "editor.prepare_fresh_play", []);
    JsonNode editor = await CallRaw(editorPort, "editor.play", []);
    int runtimePort = await WaitForProjectPortFile(RuntimePortFileName, timeoutSeconds);
    JsonNode runtime = await WaitForCliIntegration(runtimePort, timeoutSeconds);
    await EnsureUnloadedMainMenu(runtimePort, timeoutSeconds);
    JsonNode menu = await CallRaw(runtimePort, "session.get_state", []);
    return Obj(
        ("previousSession", stopped),
        ("playModePreparation", playModePreparation),
        ("editor", editor),
        ("runtimePort", runtimePort),
        ("runtime", runtime),
        ("menu", menu));
}

static async Task<(Process Process, JsonNode Session)> StartFreshBenchmarkPlayerSession(
    string playerExe,
    int port,
    double timeoutSeconds)
{
    if (!await WaitForLoopbackPortAvailable(port, 30))
    {
        throw new InvalidOperationException(
            $"Cannot start the standalone benchmark player because loopback port {port} " +
            "remained in use for 30 seconds.");
    }

    string workingDirectory = Path.GetDirectoryName(playerExe)
        ?? throw new InvalidOperationException(
            $"Cannot determine the benchmark player's working directory: {playerExe}");
    DeletePlayerPortFile(workingDirectory, RuntimePortFileName);

    ProcessStartInfo startInfo = new()
    {
        FileName = playerExe,
        Arguments = "-screen-fullscreen 0 -screen-width 1920 -screen-height 1080",
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        CreateNoWindow = false,
        WindowStyle = ProcessWindowStyle.Normal,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    startInfo.Environment["KSP2REDUX_CLI_INTEGRATION_PORT"] =
        port.ToString(CultureInfo.InvariantCulture);

    Process process = Process.Start(startInfo)
        ?? throw new InvalidOperationException(
            $"Failed to start standalone benchmark player: {playerExe}");
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    try
    {
        Task<JsonNode> runtimeTask = WaitForCliIntegration(port, timeoutSeconds);
        Task exitTask = process.WaitForExitAsync();
        Task completed = await Task.WhenAny(runtimeTask, exitTask);
        if (completed == exitTask)
        {
            throw new InvalidOperationException(
                $"Standalone benchmark player exited before its CLI listener was ready " +
                $"(pid {process.Id}, exit code {process.ExitCode}).");
        }

        JsonNode runtime = await runtimeTask;
        await EnsureUnloadedMainMenu(port, timeoutSeconds);
        JsonNode menu = await CallRaw(port, "session.get_state", []);
        return (
            process,
            Obj(
                ("standalone", true),
                ("playerExe", playerExe),
                ("workingDirectory", workingDirectory),
                ("processId", process.Id),
                ("runtimePort", port),
                ("runtime", runtime),
                ("menu", menu)));
    }
    catch
    {
        await StopStandaloneBenchmarkPlayer(process, 30);
        throw;
    }
}

static async Task<JsonNode> StopStandaloneBenchmarkPlayer(
    Process? process,
    double timeoutSeconds)
{
    JsonObject result = Obj(
        ("standalone", true),
        ("processId", process?.Id),
        ("quitRequested", false),
        ("forcedTermination", false),
        ("plannedProcessTermination", false));
    if (process == null)
    {
        result["stopped"] = true;
        result["reason"] = "process was not started";
        return result;
    }

    if (!process.HasExited)
    {
        // Unity 6.5/KSP2 currently access-violates during Application.Quit even from an
        // unloaded MainMenu. The benchmark has already flushed its JSON, screenshot, and
        // log evidence, so terminate the owned player process directly to guarantee the
        // fresh-process boundary without entering that unrelated broken teardown path.
        process.Kill(entireProcessTree: true);
        result["plannedProcessTermination"] = true;
        result["terminationMode"] = "process-tree-kill";
    }

    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    while (!process.HasExited && DateTime.UtcNow < deadline)
    {
        await Task.Delay(250);
    }

    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: true);
        result["forcedTermination"] = true;
        result["terminationMode"] = "timeout-process-tree-kill";
        await process.WaitForExitAsync();
    }

    result["stopped"] = true;
    result["exitCode"] = process.ExitCode;
    return result;
}

static JsonObject CaptureStandalonePlayerCrashArtifacts(
    string runDirectory,
    string saveName,
    string mode,
    int repetition,
    string? runStartedUtc)
{
    JsonObject result = Obj(
        ("captured", false),
        ("playerLog", null),
        ("crashDirectory", null));
    try
    {
        string artifactDirectory = Path.Combine(
            runDirectory,
            SanitizeFileName(
                $"{saveName}__{mode}__r{repetition}__unexpected-player-exit"));
        Directory.CreateDirectory(artifactDirectory);

        string userProfile =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string playerLog = Path.Combine(
            userProfile,
            "AppData",
            "LocalLow",
            "Intercept Games",
            "Kerbal Space Program 2",
            "Player.log");
        if (File.Exists(playerLog))
        {
            string copiedLog = Path.Combine(artifactDirectory, "Player.log");
            File.Copy(playerLog, copiedLog, overwrite: true);
            result["playerLog"] = copiedLog;
        }

        DateTime startedUtc = DateTime.TryParse(
            runStartedUtc,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTime parsedStartedUtc)
                ? parsedStartedUtc.ToUniversalTime()
                : DateTime.UtcNow.AddHours(-1);
        string crashRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "Intercept Games",
            "Kerbal Space Program 2",
            "Crashes");
        DirectoryInfo? crashDirectory = Directory.Exists(crashRoot)
            ? new DirectoryInfo(crashRoot)
                .EnumerateDirectories()
                .Where(directory => directory.LastWriteTimeUtc >= startedUtc)
                .OrderByDescending(directory => directory.LastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        if (crashDirectory != null)
        {
            string copiedCrashDirectory = Path.Combine(
                artifactDirectory,
                crashDirectory.Name);
            Directory.CreateDirectory(copiedCrashDirectory);
            foreach (FileInfo file in crashDirectory.EnumerateFiles())
            {
                file.CopyTo(
                    Path.Combine(copiedCrashDirectory, file.Name),
                    overwrite: true);
            }
            result["crashDirectory"] = copiedCrashDirectory;
        }

        result["captured"] =
            result["playerLog"] != null ||
            result["crashDirectory"] != null;
        result["artifactDirectory"] = artifactDirectory;
    }
    catch (Exception ex)
    {
        result["captureError"] = ex.Message;
    }
    return result;
}

static bool IsLoopbackPortAvailable(int port)
{
    TcpListener listener = new(System.Net.IPAddress.Loopback, port);
    try
    {
        listener.Start();
        return true;
    }
    catch (SocketException)
    {
        return false;
    }
    finally
    {
        listener.Stop();
    }
}

static async Task<bool> WaitForLoopbackPortAvailable(
    int port,
    double timeoutSeconds)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
        if (IsLoopbackPortAvailable(port))
        {
            return true;
        }
        await Task.Delay(250);
    }
    return IsLoopbackPortAvailable(port);
}

static void DeletePlayerPortFile(string workingDirectory, string fileName)
{
    string path = Path.Combine(workingDirectory, "Temp", fileName);
    try
    {
        File.Delete(path);
        File.Delete(path + ".Pid");
    }
    catch
    {
        // Discovery files are optional; the explicitly requested player port remains authoritative.
    }
}

static async Task<JsonNode> StopBenchmarkPlaySession(int editorPort, double timeoutSeconds)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    JsonNode? state = null;
    bool stopRequested = false;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            editorPort = TryReadProjectPortFile(EditorPortFileName) ?? editorPort;
            state = await CallRaw(editorPort, "editor.get_state", []);
            AssertExpectedEditorProject(state);
            if (!IsEditorPlaying(state) &&
                state?["isCompiling"]?.GetValue<bool>() != true &&
                state?["isUpdating"]?.GetValue<bool>() != true)
            {
                JsonNode restored =
                    await CallRaw(editorPort, "editor.restore_play_mode_options", []);
                return Obj(("stopped", true), ("state", state), ("playModeSettings", restored));
            }
            if (IsEditorPlaying(state) && !stopRequested)
            {
                await CallRaw(editorPort, "editor.stop", []);
                stopRequested = true;
            }
        }
        catch
        {
            // Exiting play mode restarts the editor bridge; keep polling its project-local port.
        }
        await Task.Delay(500);
    }
    throw new TimeoutException($"Timed out waiting for a fresh edit-mode state. Last state: {state}");
}

static void AssertExpectedEditorProject(JsonNode? editorState)
{
    string? expectedRoot = FindProjectRoot();
    string? actualRoot = editorState?["projectPath"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(expectedRoot) || string.IsNullOrWhiteSpace(actualRoot))
    {
        throw new InvalidOperationException(
            $"Cannot verify the Unity editor project identity. Expected '{expectedRoot}', state: {editorState}");
    }

    string expected = Path.GetFullPath(expectedRoot)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    string actual = Path.GetFullPath(actualRoot)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Refusing to control Unity editor for '{actual}'; benchmark project is '{expected}'.");
    }
}

static async Task<JsonNode?> TryCallRaw(
    int port,
    string method,
    JsonObject parameters,
    double readTimeoutSeconds = ResponseTimeoutSeconds)
{
    try
    {
        return await CallRaw(
            port,
            method,
            parameters,
            readTimeoutSeconds: readTimeoutSeconds);
    }
    catch
    {
        return null;
    }
}

static JsonArray StringArray(params string[] values)
{
    JsonArray result = [];
    foreach (string value in values)
    {
        result.Add(value);
    }
    return result;
}

static string BuildStructuralSummaryCsv(JsonArray runs)
{
    var rows = new List<(string Save, string Mode, int Repetition, string Label, bool Completed,
        double PhysicsP95, double FixedP95, double GcTotal, string TerminalReason)>();
    foreach (JsonObject run in runs.OfType<JsonObject>())
    {
        foreach (JsonObject sample in (run["samples"] as JsonArray ?? []).OfType<JsonObject>())
        {
            rows.Add((
                run["saveName"]?.GetValue<string>() ?? string.Empty,
                run["mode"]?.GetValue<string>() ?? string.Empty,
                run["repetition"]?.GetValue<int>() ?? 0,
                sample["label"]?.GetValue<string>() ?? string.Empty,
                run["completed"]?.GetValue<bool>() == true &&
                    sample["completed"]?.GetValue<bool>() == true &&
                    sample["capture"] != null,
                ReadJsonDouble(sample, "capture", "groups", "physicsStructural", "p95"),
                ReadJsonDouble(sample, "capture", "groups", "fixedStep", "p95"),
                ReadJsonDouble(sample, "capture", "gcBytesPerTick", "totalBytes"),
                sample["terminalReason"]?.GetValue<string>() ??
                    run["terminalReason"]?.GetValue<string>() ??
                    string.Empty
            ));
        }
    }

    var output = new StringBuilder();
    output.AppendLine(
        "save,mode,repetition,label,gateScope,completed,physicsStructuralP95Ns,fixedStepP95Ns,gcTotalBytes,meetsZeroGcGate,analyticalVsLegacyRatio,meetsLegacyImprovementGate,meets4xStretchTarget,fixedStepVsLegacyRatio,meetsFixedStep5PctGate,terminalReason");
    foreach (var row in rows)
    {
        bool activeComparisonGate =
            row.Save.StartsWith("3 -", StringComparison.OrdinalIgnoreCase) ||
            row.Save.StartsWith("4 -", StringComparison.OrdinalIgnoreCase) ||
            row.Save.StartsWith("5 -", StringComparison.OrdinalIgnoreCase) ||
            row.Save.StartsWith("6 -", StringComparison.OrdinalIgnoreCase) ||
            row.Save.StartsWith("8 -", StringComparison.OrdinalIgnoreCase);
        string gateScope = activeComparisonGate
            ? "active-comparison"
            : row.Save.StartsWith("7 -", StringComparison.OrdinalIgnoreCase) &&
              row.Label != "initial"
                ? "activation"
                : "no-regression-control";
        var legacy = rows.FirstOrDefault(candidate =>
            candidate.Save == row.Save &&
            candidate.Repetition == row.Repetition &&
            candidate.Label == row.Label &&
            candidate.Mode == "legacy");
        double ratio = row.Completed && legacy.Completed &&
            row.Mode == "analytical" && legacy.PhysicsP95 > 0
            ? row.PhysicsP95 / legacy.PhysicsP95
            : double.NaN;
        double fixedRatio = row.Completed && legacy.Completed &&
            row.Mode == "analytical" && legacy.FixedP95 > 0
            ? row.FixedP95 / legacy.FixedP95
            : double.NaN;
        string improvementGate = !activeComparisonGate || double.IsNaN(ratio)
            ? string.Empty
            : (ratio < 1.0).ToString();
        string stretchTarget = !activeComparisonGate || double.IsNaN(ratio)
            ? string.Empty
            : (ratio <= 0.25).ToString();
        string fixedGate = double.IsNaN(fixedRatio)
            ? string.Empty
            : (fixedRatio <= 1.05).ToString();
        string zeroGcGate = row.Mode == "analytical" && row.Completed
            ? (row.GcTotal == 0).ToString()
            : string.Empty;
        output.Append(Csv(row.Save)).Append(',')
            .Append(Csv(row.Mode)).Append(',')
            .Append(row.Repetition).Append(',')
            .Append(Csv(row.Label)).Append(',')
            .Append(Csv(gateScope)).Append(',')
            .Append(row.Completed).Append(',')
            .Append(row.PhysicsP95.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(row.FixedP95.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(row.GcTotal.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(zeroGcGate).Append(',')
            .Append(double.IsNaN(ratio) ? string.Empty : ratio.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(improvementGate).Append(',')
            .Append(stretchTarget).Append(',')
            .Append(double.IsNaN(fixedRatio) ? string.Empty : fixedRatio.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(fixedGate).Append(',')
            .Append(Csv(row.TerminalReason))
            .AppendLine();
    }
    return output.ToString();
}

static double ReadJsonDouble(JsonNode? root, params string[] path)
{
    JsonNode? current = root;
    foreach (string segment in path)
    {
        current = current?[segment];
    }
    if (current == null)
    {
        return 0;
    }
    return current is JsonValue jsonValue && jsonValue.TryGetValue(out double value) ? value : 0;
}

static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

static string SanitizeFileName(string value)
{
    foreach (char invalid in Path.GetInvalidFileNameChars())
    {
        value = value.Replace(invalid, '_');
    }
    return value;
}

static string ReadProcessOutput(
    string fileName,
    string arguments,
    string? workingDirectory = null)
{
    try
    {
        using Process process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory()
        })!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return process.ExitCode == 0 ? output.Trim() : string.Empty;
    }
    catch
    {
        return string.Empty;
    }
}

static async Task<JsonNode> Part(int port, string[] args, Dictionary<string, string?> options)
{
    string action = RequiredArg(args, 1, "deploy|converter|drill|crossfeed|decouple|undock");
    string partGuid = RequiredArg(args, 2, "partGuid");
    return action switch
    {
        "deploy" => await CallRaw(port, "part.deploy", Obj(("partGuid", partGuid), ("extend", ParseOnOff(RequiredArg(args, 3, "on|off"))))),
        "converter" => await CallRaw(port, "part.converter", Obj(("partGuid", partGuid), ("enabled", GetOptionalBool(options, "enabled")), ("mode", GetOption(options, "mode")))),
        "drill" => await CallRaw(port, "part.drill", Obj(("partGuid", partGuid), ("enabled", ParseOnOff(RequiredArg(args, 3, "on|off"))))),
        "crossfeed" => await CallRaw(port, "part.crossfeed", Obj(("partGuid", partGuid), ("enabled", ParseOnOff(RequiredArg(args, 3, "on|off"))))),
        "decouple" => await CallRaw(port, "part.decouple", Obj(("partGuid", partGuid))),
        "undock" => await CallRaw(port, "part.undock", Obj(("partGuid", partGuid))),
        _ => throw new ArgumentException("part action must be deploy, converter, drill, crossfeed, decouple, or undock.")
    };
}

static async Task<JsonNode> TransferStart(int port, Dictionary<string, string?> options)
{
    return await CallRaw(
        port,
        "part.transfer_start",
        Obj(
            ("resource", RequiredOption(options, "resource")),
            ("from", SplitToArray(RequiredOption(options, "from"))),
            ("to", SplitToArray(RequiredOption(options, "to"))),
            ("rate", GetOptionalDouble(options, "rate"))
        )
    );
}

static JsonArray SplitToArray(string raw)
{
    JsonArray array = [];
    foreach (string item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        array.Add(item);
    }

    return array;
}

static bool ParseOnOff(string raw)
{
    return raw.ToLowerInvariant() switch
    {
        "on" or "true" => true,
        "off" or "false" => false,
        _ => throw new ArgumentException("state must be 'on' or 'off'.")
    };
}

static async Task<JsonNode> Watch(Dictionary<string, string?> options)
{
    double intervalSeconds = GetDouble(options, "interval", 2);
    double stallSeconds = GetDouble(options, "stall", 30);
    double deadlineSeconds = GetDouble(options, "deadline", 0);
    bool kill = GetBool(options, "kill", false);

    DateTime start = DateTime.UtcNow;
    DateTime lastSuccess = DateTime.UtcNow;
    int currentPort = GetPort(options);
    string state = "";

    while (true)
    {
        if (deadlineSeconds > 0 && (DateTime.UtcNow - start).TotalSeconds > deadlineSeconds)
        {
            Console.Error.WriteLine($"watch: deadline of {deadlineSeconds}s exceeded, stopping the game via editor.stop.");
            try
            {
                await CallRaw(GetEditorPort(options), "editor.stop", []);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"watch: editor.stop failed: {ex.Message}");
            }

            Environment.Exit(3);
        }

        bool pingOk;
        try
        {
            currentPort = RefreshRuntimePort(currentPort);
            await CallRaw(currentPort, "session.ping", []);
            pingOk = true;
        }
        catch
        {
            pingOk = false;
        }

        if (pingOk)
        {
            lastSuccess = DateTime.UtcNow;
            ReportWatchState(ref state, $"healthy (port {currentPort})");
        }
        else if ((DateTime.UtcNow - lastSuccess).TotalSeconds > stallSeconds)
        {
            ReportWatchState(ref state, $"stalled (no ping for over {stallSeconds}s), attempting editor.stop");
            bool stopped = false;
            try
            {
                await CallRaw(GetEditorPort(options), "editor.stop", []);
                stopped = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"watch: editor.stop failed: {ex.Message}");
            }

            if (stopped)
            {
                lastSuccess = DateTime.UtcNow;
                ReportWatchState(ref state, "editor.stop accepted, waiting for recovery");
            }
            else if (kill)
            {
                int? pid = TryReadProjectPidFile(EditorPortFileName + ".Pid");
                if (pid == null)
                {
                    Console.Error.WriteLine("watch: no editor PID file found, cannot kill.");
                }

                if (pid != null)
                {
                    Console.Error.WriteLine($"watch: killing editor process {pid}.");
                    try
                    {
                        Process.GetProcessById(pid.Value).Kill(true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"watch: kill failed: {ex.Message}");
                    }

                    Environment.Exit(2);
                }
            }
        }
        else
        {
            ReportWatchState(ref state, $"ping failing, waiting (stall threshold {stallSeconds}s)");
        }

        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
    }
}

static void ReportWatchState(ref string state, string next)
{
    if (state != next)
    {
        state = next;
        Console.Error.WriteLine($"watch: {next}");
    }
}

static async Task<JsonNode> WaitForCliIntegration(int port, double timeoutSeconds)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    Exception? lastError = null;
    int currentPort = port;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            currentPort = RefreshRuntimePort(currentPort);
            JsonNode state = await CallRaw(currentPort, "session.get_state", []);
            if (state?["isGameAvailable"]?.GetValue<bool>() == true)
            {
                return state;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        await Task.Delay(500);
    }

    throw new TimeoutException($"Timed out waiting for KSP2Redux CLI integration on port {currentPort}. Last error: {lastError?.Message}");
}

static async Task<JsonNode> WaitForState(int port, string state, double timeoutSeconds)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    JsonNode? lastState = null;
    Exception? lastError = null;
    int currentPort = port;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            currentPort = RefreshRuntimePort(currentPort);
            JsonNode current = await CallRaw(currentPort, "session.get_state", []);
            lastState = current;
            string? currentState = current["globalGameState"]?.GetValue<string>();
            if (string.Equals(currentState, state, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        await Task.Delay(500);
    }

    string detail = lastState?.ToJsonString() ?? lastError?.Message ?? "no state";
    throw new TimeoutException($"Timed out waiting for game state '{state}'. Last result: {detail}");
}

static async Task<JsonNode> WaitForSaveLoaded(int port, double timeoutSeconds)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    JsonNode? lastState = null;
    Exception? lastError = null;
    int currentPort = port;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            currentPort = RefreshRuntimePort(currentPort);
            JsonNode current = await CallRaw(currentPort, "session.get_state", []);
            lastState = current;
            bool isLoaded = current["isLoaded"]?.GetValue<bool>() == true;
            bool isLoading = current["isLoading"]?.GetValue<bool>() == true;
            if (isLoaded && !isLoading)
            {
                return current;
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        await Task.Delay(500);
    }

    string detail = lastState?.ToJsonString() ?? lastError?.Message ?? "no state";
    throw new TimeoutException($"Timed out waiting for save load. Last result: {detail}");
}

static async Task<JsonNode> WaitForMessage(int port, string messageType, Dictionary<string, string?> options)
{
    return await CallRaw(
        port,
        "session.wait_for_message",
        Obj(
            ("type", messageType),
            ("timeout", GetDouble(options, "timeout", 120)),
            ("depth", GetInt(options, "depth", 2)),
            ("where", ParseWhere(GetOption(options, "where")))
        )
    );
}

static async Task<JsonNode> Watcher(int port, string[] args, Dictionary<string, string?> options)
{
    string type = RequiredArg(args, 1, "watcherType");
    return await CallRaw(port, "watcher.get", Obj(("type", type)));
}

static async Task<JsonNode> WaitWatcher(int port, string watcherType, Dictionary<string, string?> options)
{
    string op = GetOption(options, "op") ?? "eq";
    return await CallRaw(
        port,
        "watcher.wait",
        Obj(
            ("type", watcherType),
            ("op", op),
            ("value", GetOption(options, "value")),
            ("timeout", GetDouble(options, "timeout", 120))
        )
    );
}

static async Task<JsonNode> CallRaw(int port, string method, JsonObject parameters, double readTimeoutSeconds = ResponseTimeoutSeconds, double connectTimeoutSeconds = ConnectTimeoutSeconds)
{
    using TcpClient client = new();
    using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(connectTimeoutSeconds));
    await client.ConnectAsync("127.0.0.1", port, connectTimeout.Token);
    await using NetworkStream stream = client.GetStream();
    using StreamReader reader = new(stream, Encoding.UTF8, false, 8192, true);
    await using StreamWriter writer = new(stream, new UTF8Encoding(false), 8192, true) { AutoFlush = true };

    JsonObject request = new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = Guid.NewGuid().ToString("N"),
        ["method"] = method,
        ["params"] = parameters
    };
    await writer.WriteLineAsync(request.ToJsonString());
    string responseLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(readTimeoutSeconds))
        ?? throw new IOException("CLI integration closed the connection.");
    JsonObject response = JsonNode.Parse(responseLine)?.AsObject() ?? throw new InvalidDataException("Invalid JSON-RPC response.");
    if (response["error"] is JsonNode error)
    {
        throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? error.ToJsonString());
    }

    return response["result"] ?? JsonValue.Create<object?>(null)!;
}

static async Task RunMcp()
{
    string? line;
    while ((line = await Console.In.ReadLineAsync()) != null)
    {
        JsonObject request = JsonNode.Parse(line)?.AsObject() ?? [];
        JsonNode? id = request["id"]?.DeepClone();
        string method = request["method"]?.GetValue<string>() ?? "";
        JsonObject response;
        try
        {
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "ksp2redux-cli-integration", ["version"] = "0.1.0" }
                },
                "tools/list" => new JsonObject { ["tools"] = await BuildToolList() },
                "tools/call" => await McpToolCall(request["params"]?.AsObject() ?? []),
                "notifications/initialized" => JsonValue.Create<object?>(null)!,
                _ => throw new InvalidOperationException($"Unsupported MCP method '{method}'.")
            };
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (Exception ex)
        {
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = new JsonObject { ["code"] = -32000, ["message"] = ex.Message }
            };
        }

        if (id != null)
        {
            Console.WriteLine(response.ToJsonString());
            await Console.Out.FlushAsync();
        }
    }
}

static async Task<JsonNode> McpToolCall(JsonObject parameters)
{
    string name = parameters["name"]?.GetValue<string>() ?? throw new ArgumentException("Missing tool name.");
    JsonObject arguments = parameters["arguments"]?.AsObject() ?? [];
    int port = arguments["port"]?.GetValue<int>() ?? GetPort([]);
    int editorPort = arguments["editorPort"]?.GetValue<int>() ?? GetEditorPort([]);
    if (name == "wait")
    {
        return McpContent(await WaitForCliIntegration(port, arguments["timeout"]?.GetValue<double>() ?? 60));
    }

    if (name == "editor_play")
    {
        return McpContent(await Play(
            port,
            editorPort,
            arguments["timeout"]?.GetValue<double>() ?? 60,
            arguments["noWait"]?.GetValue<bool>() ?? false
        ));
    }

    if (name == "editor_play_save")
    {
        return McpContent(await PlaySave(
            port,
            editorPort,
            arguments["path"]?.GetValue<string>() ?? throw new ArgumentException("Missing path."),
            arguments["timeout"]?.GetValue<double>() ?? 180,
            arguments["noWait"]?.GetValue<bool>() ?? false
        ));
    }

    if (name == "editor_restart_save")
    {
        return McpContent(await RestartSave(
            port,
            editorPort,
            arguments["path"]?.GetValue<string>() ?? throw new ArgumentException("Missing path."),
            arguments["timeout"]?.GetValue<double>() ?? 420
        ));
    }

    if (name == "editor_stop")
    {
        return McpContent(await Stop(port, editorPort));
    }

    if (name == "wait_state")
    {
        return McpContent(await WaitForState(
            port,
            arguments["state"]?.GetValue<string>() ?? throw new ArgumentException("Missing state."),
            arguments["timeout"]?.GetValue<double>() ?? 120
        ));
    }

    if (name == "wait_message")
    {
        double timeout = arguments["timeout"]?.GetValue<double>() ?? 120;
        return McpContent(await CallRaw(
            port,
            "session.wait_for_message",
            Obj(
                ("type", arguments["type"]?.GetValue<string>() ?? throw new ArgumentException("Missing type.")),
                ("timeout", timeout),
                ("depth", arguments["depth"]?.GetValue<int>() ?? 2),
                ("where", arguments["where"]?.AsObject())
            ),
            timeout + 30
        ));
    }

    if (name == "watcher_wait")
    {
        double timeout = arguments["timeout"]?.GetValue<double>() ?? 120;
        return McpContent(await CallRaw(
            port,
            "watcher.wait",
            Obj(
                ("type", arguments["type"]?.GetValue<string>() ?? throw new ArgumentException("Missing type.")),
                ("op", arguments["op"]?.GetValue<string>() ?? "eq"),
                ("value", arguments["value"]?.DeepClone()),
                ("timeout", timeout)
            ),
            timeout + 30
        ));
    }

    if (name == "save_load_main_menu")
    {
        return McpContent(await LoadSaveMainMenu(
            port,
            arguments["path"]?.GetValue<string>() ?? throw new ArgumentException("Missing path."),
            arguments["timeout"]?.GetValue<double>() ?? 120
        ));
    }

    JsonObject? descriptor = (await LoadServerDescriptors()).GetValueOrDefault(name);
    string method = descriptor?["method"]?.GetValue<string>() ?? McpMethodName(name);
    double readTimeoutSeconds = McpReadTimeoutSeconds(name, descriptor, arguments);
    // The arguments node is parented by the incoming MCP request; CallRaw inserts
    // its params into a new request object, which requires an unparented node.
    JsonObject forwarded = (JsonObject)arguments.DeepClone();
    if (method.StartsWith("editor.", StringComparison.Ordinal))
    {
        JsonNode editorResult = await CallRaw(editorPort, method, forwarded, readTimeoutSeconds);
        return McpContent(editorResult);
    }

    JsonNode result = await CallRaw(port, method, forwarded, readTimeoutSeconds);
    return McpContent(result);
}

// Read timeout for the TCP response: the tool's own wall-clock timeout argument
// (named by the descriptor's timeoutParam, with generic fallbacks) plus slack,
// otherwise the descriptor's defaultReadTimeoutSeconds, otherwise a tiny static
// fallback map kept for when no descriptor is available.
static double McpReadTimeoutSeconds(string tool, JsonObject? descriptor, JsonObject arguments)
{
    string? timeoutParam = descriptor?["timeoutParam"]?.GetValue<string>();
    double? requested = timeoutParam != null ? arguments[timeoutParam]?.GetValue<double>() : null;
    requested ??= arguments["timeoutSeconds"]?.GetValue<double>() ?? arguments["timeout"]?.GetValue<double>();
    if (requested.HasValue)
    {
        return requested.Value + 30;
    }

    if (descriptor?["defaultReadTimeoutSeconds"]?.GetValue<double>() is double defaultRead)
    {
        return defaultRead;
    }

    return tool switch
    {
        "clock_run_until" => 330,
        "playtest_load_scenario" => 210,
        "perf_capture" => 600,
        "session_wait_for_message" or "watcher_wait" => 150,
        _ => ResponseTimeoutSeconds
    };
}

static JsonObject McpContent(JsonNode result)
{
    return new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            }
        }
    };
}

// Client-side composite tools handled explicitly in McpToolCall; everything else
// comes from the servers' describe_methods. On a name collision the composite wins.
static (string Tool, string Description, JsonObject Schema)[] McpCompositeTools()
{
    return
    [
        ("wait", "Polls session state until the game reports isGameAvailable (use after entering play mode).", Schema(Opt("timeout", "number", "seconds to keep polling, default 60"))),
        ("wait_state", "Polls session state until globalGameState matches the requested state.", Schema(Req("state", "string", "global game state name, e.g. MainMenu, KerbalSpaceCenter, FlightView"), Opt("timeout", "number", "seconds, default 120"))),
        ("wait_message", "Long-polls for the next MessageCenter message of the given type.", Schema(Req("type", "string", "message type name, short or namespace-qualified"), Opt("timeout", "number", "seconds, default 120"), Opt("depth", "integer", "serialization depth for the message payload, default 2"), Opt("where", "object", "property=value pairs the message must match"))),
        ("editor_play", "Enters Unity editor play mode and waits for the runtime CLI integration to come up.", Schema(Opt("timeout", "number", "seconds to wait for the runtime, default 60"), Opt("noWait", "boolean", "return immediately after the editor accepts play"))),
        ("editor_play_save", "Enters editor play mode with a startup save and waits for it to load. Returns accepted:false if already playing; use editor_restart_save then.", Schema(Req("path", "string", "absolute path to the save .json to load on startup"), Opt("timeout", "number", "seconds per wait phase, default 180"), Opt("noWait", "boolean", "return after the editor accepts play, skipping the load wait"))),
        ("editor_restart_save", "Stops play mode if running, waits for the editor to settle, then plays the given save and waits for it to load.", Schema(Req("path", "string", "absolute path to the save .json to load on startup"), Opt("timeout", "number", "seconds per phase (stop wait, runtime wait, load wait), default 420"))),
        ("editor_stop", "Exits editor play mode, falling back to session.stop_play_mode on the runtime if the editor is unreachable.", Schema()),
        ("save_load_main_menu", "Waits for the MainMenu state, then loads the save.", Schema(Req("path", "string", "absolute save file path"), Opt("timeout", "number", "seconds to wait for MainMenu, default 120"))),
        ("watcher_wait", "Long-polls a PropertyWatcher until its value satisfies the op/value condition.", Schema(Req("type", "string", "watcher type name, short or namespace-qualified"), Opt("op", "string", "eq|ne|gt|gte|lt|lte|contains|changed, default eq"), Opt("value", "", "expected value; required unless op=changed"), Opt("timeout", "number", "seconds, default 120")))
    ];
}

// Fetches editor.describe_methods and session.describe_methods (each independently
// optional), overlaying the Temp cache so the tool list survives either server being
// down. Returns a map of tool name (dots -> underscores) to descriptor.
static async Task<Dictionary<string, JsonObject>> LoadServerDescriptors()
{
    if (McpState.Descriptors != null)
    {
        return McpState.Descriptors;
    }

    Dictionary<string, JsonObject> descriptors = ReadDescriptorCache();
    bool anyLive = false;
    try
    {
        JsonNode editor = await CallRaw(GetEditorPort([]), "editor.describe_methods", [], readTimeoutSeconds: 10, connectTimeoutSeconds: 2);
        MergeDescriptors(descriptors, editor, "editor");
        anyLive = true;
    }
    catch
    {
        // editor unreachable; keep cached/absent editor tools
    }

    try
    {
        JsonNode runtime = await CallRaw(GetPort([]), "session.describe_methods", [], readTimeoutSeconds: 10, connectTimeoutSeconds: 2);
        MergeDescriptors(descriptors, runtime, "runtime");
        anyLive = true;
    }
    catch
    {
        // runtime unreachable; keep cached/absent runtime tools
    }

    if (anyLive)
    {
        WriteDescriptorCache(descriptors);
    }

    McpState.Descriptors = descriptors;
    return descriptors;
}

// Replaces every cached entry for the given server with the live describe_methods
// response, so methods removed server-side disappear from the merged map.
static void MergeDescriptors(Dictionary<string, JsonObject> descriptors, JsonNode response, string server)
{
    foreach (string tool in descriptors.Where(entry => entry.Value["server"]?.GetValue<string>() == server).Select(entry => entry.Key).ToArray())
    {
        descriptors.Remove(tool);
    }

    foreach (JsonNode? entry in response?["methods"]?.AsArray() ?? [])
    {
        if (entry is not JsonObject descriptor)
        {
            continue;
        }

        string? method = descriptor["method"]?.GetValue<string>();
        if (string.IsNullOrEmpty(method))
        {
            continue;
        }

        JsonObject clone = descriptor.DeepClone().AsObject();
        clone["server"] = server;
        descriptors[method.Replace('.', '_')] = clone;
    }
}

static string? McpToolsCachePath()
{
    string? projectRoot = FindProjectRoot();
    return projectRoot == null ? null : Path.Combine(projectRoot, "Temp", McpToolsCacheFileName);
}

static Dictionary<string, JsonObject> ReadDescriptorCache()
{
    Dictionary<string, JsonObject> descriptors = new(StringComparer.Ordinal);
    try
    {
        string? path = McpToolsCachePath();
        if (path == null || !File.Exists(path))
        {
            return descriptors;
        }

        JsonObject cache = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [];
        foreach (JsonNode? entry in cache["methods"]?.AsArray() ?? [])
        {
            if (entry is not JsonObject descriptor)
            {
                continue;
            }

            string? method = descriptor["method"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(method))
            {
                descriptors[method.Replace('.', '_')] = descriptor.DeepClone().AsObject();
            }
        }
    }
    catch
    {
        // an unreadable cache is the same as no cache
    }

    return descriptors;
}

static void WriteDescriptorCache(Dictionary<string, JsonObject> descriptors)
{
    try
    {
        string? path = McpToolsCachePath();
        if (path == null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonArray methods = [];
        foreach (JsonObject descriptor in descriptors.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Value))
        {
            methods.Add(descriptor.DeepClone());
        }

        File.WriteAllText(path, new JsonObject { ["methods"] = methods }.ToJsonString());
    }
    catch
    {
        // caching is best-effort
    }
}

// Builds an MCP inputSchema: {type: object, properties: {...}, required: [...]}.
// An empty Type or Description omits that field; additionalProperties stays true
// because the server tolerates extras (e.g. port/editorPort overrides).
static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] properties)
{
    JsonObject props = [];
    JsonArray required = [];
    foreach ((string name, string type, string description, bool isRequired) in properties)
    {
        JsonObject prop = [];
        if (type.Length > 0)
        {
            prop["type"] = type;
        }

        if (description.Length > 0)
        {
            prop["description"] = description;
        }

        props[name] = prop;
        if (isRequired)
        {
            required.Add(name);
        }
    }

    JsonObject schema = new() { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = true };
    if (required.Count > 0)
    {
        schema["required"] = required;
    }

    return schema;
}

static (string Name, string Type, string Description, bool Required) Req(string name, string type, string description)
{
    return (name, type, description, true);
}

static (string Name, string Type, string Description, bool Required) Opt(string name, string type, string description)
{
    return (name, type, description, false);
}

// Composites first, then the live/cached server descriptors (sorted by tool name).
// With neither server reachable and no cache, the composites alone are served.
static async Task<JsonArray> BuildToolList()
{
    Dictionary<string, JsonObject> descriptors = await LoadServerDescriptors();
    JsonArray tools = [];
    HashSet<string> names = new(StringComparer.Ordinal);
    foreach ((string tool, string description, JsonObject schema) in McpCompositeTools())
    {
        names.Add(tool);
        tools.Add(new JsonObject
        {
            ["name"] = tool,
            ["description"] = description,
            ["inputSchema"] = schema
        });
    }

    foreach ((string tool, JsonObject descriptor) in descriptors.OrderBy(entry => entry.Key, StringComparer.Ordinal))
    {
        if (!names.Add(tool))
        {
            continue;
        }

        tools.Add(new JsonObject
        {
            ["name"] = tool,
            ["description"] = descriptor["description"]?.GetValue<string>() ?? "",
            ["inputSchema"] = descriptor["schema"]?.DeepClone() ?? Schema()
        });
    }

    return tools;
}

// Generic tool -> method fallback when no descriptor is available: the first
// underscore separates the method's domain (session_get_state -> session.get_state).
// Unknown names are sent through so the server reports the proper error.
static string McpMethodName(string name)
{
    int underscore = name.IndexOf('_');
    if (underscore <= 0 || underscore == name.Length - 1)
    {
        return name;
    }

    return name[..underscore] + "." + name[(underscore + 1)..];
}

static JsonObject Obj(params (string Key, object? Value)[] values)
{
    JsonObject obj = [];
    foreach ((string key, object? value) in values)
    {
        if (value == null)
        {
            continue;
        }

        obj[key] = value switch
        {
            JsonNode node => node.DeepClone(),
            string s => s,
            int i => i,
            double d => d,
            bool b => b,
            _ => JsonValue.Create(value)
        };
    }

    return obj;
}

static JsonObject? ParseWhere(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }

    JsonObject result = [];
    foreach (string segment in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        int equals = segment.IndexOf('=');
        if (equals <= 0)
        {
            throw new ArgumentException("--where expects Name=Value pairs separated by semicolons.");
        }

        result[segment[..equals]] = segment[(equals + 1)..];
    }

    return result;
}

static Dictionary<string, string?> ParseOptions(string[] args)
{
    Dictionary<string, string?> options = new(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        if (!arg.StartsWith("--"))
        {
            continue;
        }

        string key = arg[2..];
        string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
        options[key] = value;
    }

    return options;
}

static int GetPort(Dictionary<string, string?> options)
{
    string? raw = GetOption(options, "port") ?? Environment.GetEnvironmentVariable("KSP2REDUX_CLI_INTEGRATION_PORT");
    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
    {
        return port;
    }

    return TryReadProjectPortFile(RuntimePortFileName) ?? DefaultPort;
}

static int GetEditorPort(Dictionary<string, string?> options)
{
    string? raw = GetOption(options, "editor-port") ?? Environment.GetEnvironmentVariable("KSP2REDUX_CLI_EDITOR_PORT");
    if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
    {
        return port;
    }

    return TryReadProjectPortFile(EditorPortFileName) ?? DefaultEditorPort;
}

static int RefreshRuntimePort(int currentPort)
{
    return TryReadProjectPortFile(RuntimePortFileName) ?? currentPort;
}

static int? TryReadProjectPortFile(string fileName)
{
    string? projectRoot = FindProjectRoot();
    if (projectRoot == null)
    {
        return null;
    }

    string path = Path.Combine(projectRoot, "Temp", fileName);
    if (!File.Exists(path))
    {
        return null;
    }

    string raw = File.ReadAllText(path).Trim();
    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) && port > 0 && port <= 65535
        ? port
        : null;
}

static async Task<int> WaitForProjectPortFile(string fileName, double timeoutSeconds)
{
    DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    while (DateTime.UtcNow < deadline)
    {
        int? port = TryReadProjectPortFile(fileName);
        if (port.HasValue)
        {
            return port.Value;
        }
        await Task.Delay(250);
    }
    throw new TimeoutException(
        $"Timed out waiting for the fresh play session to create project port file '{fileName}'.");
}

static void DeleteProjectPortFile(string fileName)
{
    string? projectRoot = FindProjectRoot();
    if (projectRoot == null)
    {
        return;
    }

    string path = Path.Combine(projectRoot, "Temp", fileName);
    if (File.Exists(path))
    {
        File.Delete(path);
    }
}

static int? TryReadProjectPidFile(string fileName)
{
    string? projectRoot = FindProjectRoot();
    if (projectRoot == null)
    {
        return null;
    }

    string path = Path.Combine(projectRoot, "Temp", fileName);
    if (!File.Exists(path))
    {
        return null;
    }

    string raw = File.ReadAllText(path).Trim();
    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && pid > 0
        ? pid
        : null;
}

static string? FindProjectRoot()
{
    return FindProjectRootFrom(Directory.GetCurrentDirectory()) ?? FindProjectRootFrom(AppContext.BaseDirectory);
}

static string? FindProjectRootFrom(string startDirectory)
{
    DirectoryInfo? directory = new(startDirectory);
    while (directory != null)
    {
        if (Directory.Exists(Path.Combine(directory.FullName, "Assets")) &&
            Directory.Exists(Path.Combine(directory.FullName, "ProjectSettings")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    return null;
}

static string? GetOption(Dictionary<string, string?> options, string key)
{
    return options.TryGetValue(key, out string? value) ? value : null;
}

static string RequiredOption(Dictionary<string, string?> options, string key)
{
    return GetOption(options, key) ?? throw new ArgumentException($"Missing --{key}.");
}

static int GetInt(Dictionary<string, string?> options, string key, int fallback)
{
    return int.TryParse(GetOption(options, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value
        : fallback;
}

static bool GetBool(Dictionary<string, string?> options, string key, bool fallback)
{
    string? raw = GetOption(options, key);
    return raw == null ? fallback : bool.Parse(raw);
}

static bool? GetOptionalBool(Dictionary<string, string?> options, string key)
{
    string? raw = GetOption(options, key);
    return raw == null ? null : bool.Parse(raw);
}

static double GetDouble(Dictionary<string, string?> options, string key, double fallback = 0, bool required = false)
{
    string? raw = GetOption(options, key);
    if (raw == null)
    {
        if (required)
        {
            throw new ArgumentException($"Missing --{key}.");
        }

        return fallback;
    }

    return double.Parse(raw, CultureInfo.InvariantCulture);
}

static double? GetOptionalDouble(Dictionary<string, string?> options, string key)
{
    string? raw = GetOption(options, key);
    return raw == null ? null : double.Parse(raw, CultureInfo.InvariantCulture);
}

static string RequiredArg(string[] args, int index, string name)
{
    return args.Length > index ? args[index] : throw new ArgumentException($"Missing {name}.");
}

static void PrintHelp()
{
    Console.WriteLine(
        """
        ksp2redux-cli - KSP2Redux CLI integration client

        Commands:
          play [--timeout 60] [--no-wait]
          play-save <path> [--timeout 180] [--no-wait]
          restart-save <path> [--timeout 420]
          stop
          editor-state
          wait [--timeout 60]
          wait-state <state> [--timeout 120]
          wait-message <messageType> [--timeout 120] [--where Name=Value;Other=Value] [--depth 2]
          watchers
          watcher <watcherType>
          wait-watcher <watcherType> --op eq|ne|gt|gte|lt|lte|contains|changed [--value <value>] [--timeout 120]
          ping | state | logs [--limit 100] | activity [--limit 100]
          load-save <path>
          load-save-main-menu <path> [--timeout 120]
          quick-save
          save-as <path>
          quick-launch [launchpad|orbit|runway]
          launch-active-assembly
          active-vessel
          vessels
          controls
          pause [on|off]
          resume | unpause
          clock
          step [n]
          run-until --kind reflect|ut|ticks [--path <reflectPath>] [--op eq|ne|gt|gte|lt|lte|contains] --value <v> [--timeout 300] [--no-pause]
          sas [on|off|stabilityassist|prograde|retrograde|normal|antinormal|radialin|radialout|target|antitarget|maneuver] [--speed-mode Orbit|Surface|Target]
          speed-mode <Orbit|Surface|Target>
          throttle <0..1>
          flight-inputs [--pitch -1..1] [--yaw -1..1] [--roll -1..1] [--wheel-steer -1..1] [--wheel-throttle -1..1] [--translate-x -1..1] [--translate-y -1..1] [--translate-z -1..1]
          stage
          action-group <group> [trigger|toggle|on|off]
          heat-shields [--guid <guid>]
          telemetry [active|<guid>]
          parts [--filter <f>] [--guid <vesselGuid>]
          part-action <partGuid> <action>
          part deploy <partGuid> on|off
          part converter <partGuid> [--enabled true|false] [--mode <int-or-formula-key>]
          part drill <partGuid> on|off
          part crossfeed <partGuid> on|off
          part decouple <partGuid>
          part undock <partGuid>
          transfer-start --resource <name> --from g1,g2 --to g3,g4 [--rate 1.0]
          transfer-stop
          teleport orbit --body Kerbin --sma 700000 --ecc 0 --inc 0 --lan 0 --argPe 0 --meanAnomaly 0 [--guid <guid>]
          teleport surface --body Kerbin --lat 0 --lon 0 --altitude 100 [--guid <guid>]
          screenshot [path]
          eval lua <script>
          eval csharp <code>
          eval reflect <GameManager.Instance... path>
          cheat [<name> on|off]
          quit-to-menu
          events-start --types TypeA,TypeB [--depth 1] [--limit 1000]
          events-stop
          events-drain [--keep]
          perf-capture [--markers a,b,c] [--ticks 250]
          benchmark-structural --campaign <path> --modes analytical,legacy --repetitions 5 --warmup-ticks 500 --sample-ticks 1000 --output <directory> [--player-exe <path>] [--player-port 52693] [--saves 3,4] [--exercise-presentation] [--force-owner-sleep] [--switch-cycles 25] [--switch-target-guid <guid>] [--switch-timeout-seconds 1200]
          perf-chunks
          perf-markers
          list-scenarios
          load-scenario <name> [--timeout 180] [--settle-ticks 50]
          begin-run <name> [--scenario <s>] [--deny p1;p2] [--deadline <secs>] [--stall <secs>]
          assert <name> --path <reflectPath> --op eq|ne|gt|gte|lt|lte|contains|changed --value <v>
          end-run [--result passed|failed] [--reason <r>]
          abort-run [--reason <r>]
          run-report
          watch [--interval 2] [--stall 30] [--deadline 0] [--kill]
          call <json-rpc-method> [json-params]
          editor-call <json-rpc-method> [json-params] [--timeout 30]
          mcp

        Options:
          --port <port>  Defaults to KSP2REDUX_CLI_INTEGRATION_PORT, then the clone-local Temp port file, then 51693.
          --editor-port <port>  Defaults to KSP2REDUX_CLI_EDITOR_PORT, then the clone-local Temp port file, then 51692.
        """
    );
}

/// <summary>
/// Per-process memo of the merged server method descriptors (tool name -> descriptor),
/// shared by tools/list and tools/call during an MCP session.
/// </summary>
internal static class McpState
{
    public static Dictionary<string, JsonObject>? Descriptors;
}
