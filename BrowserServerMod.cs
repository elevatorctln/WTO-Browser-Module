using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using KSATelemetryOverlay;
using KSATelemetryOverlay.Config;
using KSATelemetryOverlay.Telemetry;
using StarMap.API;

namespace BrowserServerModule;

[StarMapMod]
public class BrowserServerMod
{
    private const string LogPrefix = "[BrowserServerModule] ";

    static BrowserServerMod()
    {
        AppDomain.CurrentDomain.AssemblyResolve += ResolveSiblingAssembly;
    }

    private static System.Reflection.Assembly? ResolveSiblingAssembly(object? sender, ResolveEventArgs args)
    {
        var requestedName = new System.Reflection.AssemblyName(args.Name).Name;
        if (string.Equals(requestedName, "KSATelemetryOverlay", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(asm.GetName().Name, requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    return asm;
                }
            }
        }
        return null;
    }

    private readonly TelemetryServer _server = new();
    private string? _modDirectory;

    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        _modDirectory = mod.DirectoryPath;
        Console.WriteLine(LogPrefix + $"attached to KSA mod '{mod.Id}' at '{_modDirectory}'");
    }

    [StarMapAllModsLoaded]
    public void OnAllModsLoaded()
    {
        TelemetryOverlayApi.OnTelemetrySampled += HandleTelemetrySampled;
        TelemetryOverlayApi.OnDrawSettings += DrawObsSettings;

        if (TelemetryOverlayApi.CurrentConfig is { } config)
        {
            _server.EnsureState(config.EnableObsServer, config.ObsServerPort, _modDirectory);
        }

        Console.WriteLine(LogPrefix + "attached to TelemetryOverlayApi");
    }

    [StarMapUnload]
    public void OnUnload()
    {
        try
        {
            TelemetryOverlayApi.OnTelemetrySampled -= HandleTelemetrySampled;
            TelemetryOverlayApi.OnDrawSettings -= DrawObsSettings;
        }
        catch { }

        try
        {
            _server.Dispose();
        }
        catch { }

        Console.WriteLine(LogPrefix + "unloaded");
    }

    private void HandleTelemetrySampled(TelemetrySnapshot snapshot, OverlayConfig config, double dt)
    {
        _server.EnsureState(config.EnableObsServer, config.ObsServerPort, _modDirectory);
        _server.Update(dt, snapshot, config);
    }

    private bool DrawObsSettings(OverlayConfig config)
    {
        bool changed = false;

        changed |= ImGui.Checkbox("Enable OBS browser source server"u8, ref config.EnableObsServer);

        ImGui.Spacing();
        ImGui.TextDisabled("Display Mode"u8);

        bool inGameOnly = config.DisplayMode == OverlayDisplayMode.InGameOnly;
        if (ImGui.RadioButton("In-game only"u8, inGameOnly))
        {
            config.DisplayMode = OverlayDisplayMode.InGameOnly;
            changed = true;
        }
        ImGui.SameLine();
        bool obsOnly = config.DisplayMode == OverlayDisplayMode.ObsOnly;
        if (ImGui.RadioButton("OBS only"u8, obsOnly))
        {
            config.DisplayMode = OverlayDisplayMode.ObsOnly;
            changed = true;
        }
        ImGui.SameLine();
        bool both = config.DisplayMode == OverlayDisplayMode.Both;
        if (ImGui.RadioButton("Both"u8, both))
        {
            config.DisplayMode = OverlayDisplayMode.Both;
            changed = true;
        }

        if (config.DisplayMode == OverlayDisplayMode.ObsOnly)
        {
            ImGui.TextColored(new float4(0.4f, 0.8f, 1f, 1f), "In-game overlay hidden; stock KSA HUD stays on screen."u8);
        }

        ImGui.Spacing();

        ImGui.SetNextItemWidth(120f);
        int port = config.ObsServerPort;
        if (ImGui.InputInt("Server port"u8, ref port, 1, 100))
        {
            config.ObsServerPort = Math.Clamp(port, 1024, 65535);
            changed = true;
        }

        ImGui.TextDisabled("Server status:"u8);
        ImGui.SameLine();
        if (_server.IsRunning)
        {
            ImGui.TextColored(new float4(0.2f, 1f, 0.4f, 1f), _server.StatusText);
        }
        else if (_server.StatusText.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
        {
            ImGui.TextColored(new float4(1f, 0.4f, 0.4f, 1f), _server.StatusText);
        }
        else
        {
            ImGui.TextColored(new float4(0.7f, 0.7f, 0.7f, 1f), _server.StatusText);
        }

        ImGui.SameLine();
        if (ImGui.Button("Restart Server"u8))
        {
            _server.Restart();
        }

        ImGui.TextDisabled("In OBS, add a Browser Source pointing to:"u8);
        ImGui.TextColored(new float4(1f, 0.9f, 0.3f, 1f), $"http://127.0.0.1:{config.ObsServerPort}/");
        ImGui.TextDisabled("Recommended size: 1920x1080 (transparent background)."u8);

        if (changed)
        {
            _server.EnsureState(config.EnableObsServer, config.ObsServerPort, _modDirectory);
        }

        return changed;
    }
}
