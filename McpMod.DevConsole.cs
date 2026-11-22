using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.DevConsole;

namespace STS2_MCP;

// Runs the game's own developer console commands (power, damage, fight, ...)
// for testing. The console entry point is DevConsole.ProcessCommand(string),
// which returns a CmdResult { success, msg, task }.
//
// Release builds only register DebugOnly commands (most of the useful ones)
// when the DevConsole is constructed with shouldAllowDebugCommands = true,
// which the in-game console does only for debug builds or when the player
// enables "full_console" in settings.save. Instead of flipping that persistent
// setting, the mod owns a separate DevConsole instance constructed with
// debug commands allowed. It never touches the NDevConsole UI node.
public static partial class McpMod
{
    internal const int DevConsoleMaxCommandLength = 500;

    // How long the HTTP thread waits for an async command (CmdResult.task),
    // e.g. `fight`, before reporting it as still pending. Kept below the MCP
    // client's 10 s HTTP timeout.
    private static readonly TimeSpan DevConsoleTaskWait = TimeSpan.FromSeconds(5);

    // Only accessed on the main thread.
    private static DevConsole? _devConsole;

    // BBCode tags the console uses for coloring. Listed explicitly so usage
    // text such as "[run]" or "[id:string]" is left intact.
    private static readonly Regex DevConsoleBbcode = new(
        @"\[/?(?:gold|color|b|i|u|s|red|green|blue|aqua|orange|purple|pink|yellow|gray|grey|white|center|code|url|font_size|bgcolor|fgcolor|outline_color|outline_size|wave|shake|rainbow|fade|jitter|sine|tornado)(?:=[^\]]*)?\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void HandlePostDevConsole(HttpListenerRequest request, HttpListenerResponse response)
    {
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
            body = reader.ReadToEnd();

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);
        }
        catch
        {
            SendError(response, 400, "Invalid JSON");
            return;
        }

        if (parsed == null || !parsed.TryGetValue("command", out var cmdElem)
            || cmdElem.ValueKind != JsonValueKind.String)
        {
            SendError(response, 400, "Missing 'command' string field");
            return;
        }

        string? validationError = ValidateDevConsoleCommand(cmdElem.GetString(), out string command);
        if (validationError != null)
        {
            SendError(response, 400, validationError);
            return;
        }

        try
        {
            var result = RunOnMainThread(() => ExecuteDevConsoleCommand(command)).GetAwaiter().GetResult();
            if (result.Task != null)
                AwaitDevConsoleTask(result.Task, result.Response);
            SendJson(response, result.Response);
        }
        catch (Exception ex)
        {
            SendError(response, 500, $"Dev console command failed: {ex.Message}");
        }
    }

    /// <summary>Returns an error message, or null with the trimmed command.</summary>
    internal static string? ValidateDevConsoleCommand(string? raw, out string command)
    {
        command = (raw ?? "").Trim();
        if (command.Length == 0)
            return "Command is empty";
        if (command.Length > DevConsoleMaxCommandLength)
            return $"Command is too long ({command.Length} chars, max {DevConsoleMaxCommandLength})";
        if (command.IndexOfAny(new[] { '\n', '\r' }) >= 0)
            return "Command must be a single line";
        return null;
    }

    private sealed record DevConsoleRun(Dictionary<string, object?> Response, Task? Task);

    // Main thread only. Never throws: any failure becomes an error response.
    private static DevConsoleRun ExecuteDevConsoleCommand(string command)
    {
        try
        {
            _devConsole ??= new DevConsole(shouldAllowDebugCommands: true);
            CmdResult result = _devConsole.ProcessCommand(command);
            string output = StripDevConsoleMarkup(result.msg);
            GD.Print($"[STS2 MCP] dev_console `{command}` -> success={result.success}");

            var response = new Dictionary<string, object?>
            {
                ["status"] = result.success ? "ok" : "error",
                ["command"] = command,
                ["success"] = result.success,
                ["output"] = output,
            };
            if (!result.success)
                response["error"] = string.IsNullOrEmpty(output) ? "Command failed" : output;
            return new DevConsoleRun(response, result.task);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[STS2 MCP] dev_console `{command}` threw: {ex}");
            return new DevConsoleRun(new Dictionary<string, object?>
            {
                ["status"] = "error",
                ["command"] = command,
                ["success"] = false,
                ["output"] = "",
                ["error"] = $"{ex.GetType().Name}: {ex.Message}",
            }, null);
        }
    }

    // HTTP thread. Waits briefly for an async command so the response can say
    // whether it finished; a pending task keeps running in the game.
    private static void AwaitDevConsoleTask(Task task, Dictionary<string, object?> response)
    {
        try
        {
            task.Wait(DevConsoleTaskWait);
        }
        catch
        {
            // Faults are reported from the task state below.
        }

        if (task.IsFaulted)
        {
            var inner = task.Exception?.GetBaseException();
            response["task_status"] = "faulted";
            response["status"] = "error";
            response["success"] = false;
            response["error"] = inner != null ? $"{inner.GetType().Name}: {inner.Message}" : "Command task faulted";
        }
        else if (task.IsCanceled)
        {
            response["task_status"] = "canceled";
        }
        else
        {
            response["task_status"] = task.IsCompleted ? "completed" : "pending";
        }
    }

    private static string StripDevConsoleMarkup(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        return DevConsoleBbcode.Replace(text, "").Trim();
    }
}
