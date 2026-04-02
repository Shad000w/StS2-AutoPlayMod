// =============================================================================
// ModConfigBridge.cs — Drop-in Template for ModConfig Integration
// =============================================================================
// Copy this file into your mod's Scripts/ folder, then:
//   1. Replace "YourMod" namespace and mod IDs with your own
//   2. Edit BuildEntries() to define your config items
//   3. Call ModConfigBridge.DeferredRegister() in your mod's Initialize()
//
// Zero DLL reference needed — everything is done via reflection.
// If ModConfig is not installed, your mod works normally (all GetValue calls
// return the fallback you provide).
// =============================================================================

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace AutoPlay.Config;

internal static class ModConfigBridge
{
    // ─── State ──────────────────────────────────────────────────
    private static bool _available;
    private static bool _registered;
    private static Type? _apiType;
    private static Type? _entryType;
    private static Type? _configTypeEnum;

    internal static bool IsAvailable => _available;

    // ─── Step 1: Call this in your Initialize() ─────────────────
    // ModConfig may load AFTER your mod (alphabetical order).
    // Deferring to the next frame ensures ModConfig is ready.

    internal static void DeferredRegister()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame += OnNextFrame;
    }

    private static void OnNextFrame()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.ProcessFrame -= OnNextFrame;
        Detect();
        if (_available) Register();
    }

    // ─── Step 2: Detect ModConfig via reflection ────────────────

    private static void Detect()
    {
        try
        {
            var allTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Type.EmptyTypes; }
                })
                .ToArray();

            _apiType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ModConfigApi");
            _entryType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigEntry");
            _configTypeEnum = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigType");
            _available = _apiType != null && _entryType != null && _configTypeEnum != null;
        }
        catch
        {
            _available = false;
        }
    }

    // ─── Step 3: Register your config entries ───────────────────

    private static void Register()
    {
        if (_registered) return;
        _registered = true;

        try
        {
            var entries = BuildEntries();

            // Localized display name (shows in ModConfig's mod list)
            var displayNames = new Dictionary<string, string>
            {
                ["en"] = "AutoPlay",
            };

            // ModConfig has 2 overloads: 3-param (no i18n) and 4-param (with i18n).
            // We prefer 4-param when available.
            var registerMethod = _apiType!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Register")
                .OrderByDescending(m => m.GetParameters().Length)
                .First();

            if (registerMethod.GetParameters().Length == 4)
            {
                registerMethod.Invoke(null, new object[]
                {
                    "AutoPlay",          // Must match your mod's ID
                    displayNames["en"],     // Fallback display name
                    displayNames,           // Localized display names
                    entries
                });
            }
            else
            {
                registerMethod.Invoke(null, new object[]
                {
                    "AutoPlay",
                    displayNames["en"],
                    entries
                });
            }
			AutoPlay.ModSettings.AutomaticDiscard = ModConfigBridge.GetValue("AutomaticDiscard", true);
			AutoPlay.ModSettings.AutomaticExhaust = ModConfigBridge.GetValue("AutomaticExhaust", true);
			AutoPlay.ModSettings.AutomaticRetain = ModConfigBridge.GetValue("AutomaticRetain", true);
			AutoPlay.ModSettings.AutomaticSelect = ModConfigBridge.GetValue("AutomaticSelect", true);
			AutoPlay.ModSettings.HardSelect = ModConfigBridge.GetValue("HardSelect", true);
		}
        catch (Exception e)
        {
            // Log but don't crash — ModConfig is optional
            GD.PrintErr($"[AutoPlay] ModConfig registration failed: {e}");
        }
	}

    // ─── Read/Write Config Values ───────────────────────────────

    /// <summary>Read a saved config value, with fallback if ModConfig absent.</summary>
    internal static T GetValue<T>(string key, T fallback)
    {
        if (!_available) return fallback;
        try
        {
            var result = _apiType!.GetMethod("GetValue", BindingFlags.Public | BindingFlags.Static)
                ?.MakeGenericMethod(typeof(T))
                ?.Invoke(null, new object[] { "AutoPlay", key });
            return result != null ? (T)result : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>
    /// Sync a value back to ModConfig (for persistence).
    /// Call this when your mod changes a setting outside ModConfig's UI
    /// (e.g. via hotkey or your own settings menu).
    /// </summary>
    internal static void SetValue(string key, object value)
    {
        if (!_available) return;
        try
        {
            _apiType!.GetMethod("SetValue", BindingFlags.Public | BindingFlags.Static)
                ?.Invoke(null, new object[] { "AutoPlay", key, value });
        }
        catch { }
    }

    // ═════════════════════════════════════════════════════════════
    //  EDIT BELOW: Define your config entries
    // ═════════════════════════════════════════════════════════════

    private static Array BuildEntries()
    {
        var list = new List<object>();

        // ─── Section Header (visual only) ───────────────────────

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Label", "Options");
            Set(cfg, "Type", EnumVal("Header"));
        }));

        // ─── Toggle (bool) ─────────────────────────────────────

        list.Add(Entry(cfg =>
        {
            Set(cfg, "Key", "AutomaticDiscard");
            Set(cfg, "Label", "Automatic Discard");
            Set(cfg, "Type", EnumVal("Toggle"));
            Set(cfg, "DefaultValue", (object)true);
            Set(cfg, "Description", "Turn automatic discard on or off");
            Set(cfg, "OnChanged", new Action<object>(v =>
            {
               AutoPlay.ModSettings.AutomaticDiscard = Convert.ToBoolean(v);
            }));
        }));

		list.Add(Entry(cfg =>
		{
			Set(cfg, "Key", "AutomaticExhaust");
			Set(cfg, "Label", "Automatic Exhaust");
			Set(cfg, "Type", EnumVal("Toggle"));
			Set(cfg, "DefaultValue", (object)true);
			Set(cfg, "Description", "Turn automatic exhaust on or off");
			Set(cfg, "OnChanged", new Action<object>(v =>
			{
				AutoPlay.ModSettings.AutomaticExhaust = Convert.ToBoolean(v);
			}));
		}));

		list.Add(Entry(cfg =>
		{
			Set(cfg, "Key", "AutomaticRetain");
			Set(cfg, "Label", "Automatic Retain");
			Set(cfg, "Type", EnumVal("Toggle"));
			Set(cfg, "DefaultValue", (object)true);
			Set(cfg, "Description", "Turn automatic retain on or off");
			Set(cfg, "OnChanged", new Action<object>(v =>
			{
				AutoPlay.ModSettings.AutomaticRetain = Convert.ToBoolean(v);
			}));
		}));

		list.Add(Entry(cfg =>
		{
			Set(cfg, "Key", "AutomaticSelect");
			Set(cfg, "Label", "Automatic Select");
			Set(cfg, "Type", EnumVal("Toggle"));
			Set(cfg, "DefaultValue", (object)true);
			Set(cfg, "Description", "All other types of selections.");
			Set(cfg, "OnChanged", new Action<object>(v =>
			{
				AutoPlay.ModSettings.AutomaticSelect = Convert.ToBoolean(v);
			}));
		}));

		list.Add(Entry(cfg =>
		{
			Set(cfg, "Key", "HardSelect");
			Set(cfg, "Label", "Hard Target Selection Feature");
			Set(cfg, "Type", EnumVal("Toggle"));
			Set(cfg, "DefaultValue", (object)true);
			Set(cfg, "Description", "This feature allows to click on enemy and route all single target cards onto him.");
			Set(cfg, "OnChanged", new Action<object>(v =>
			{
				AutoPlay.ModSettings.HardSelect = Convert.ToBoolean(v);
			}));
		}));

		// ─── Pack into typed array ─────────────────────────────

		var result = Array.CreateInstance(_entryType!, list.Count);
        for (int i = 0; i < list.Count; i++)
            result.SetValue(list[i], i);
        return result;
    }

    // ═════════════════════════════════════════════════════════════
    //  Reflection helpers (don't need to modify these)
    // ═════════════════════════════════════════════════════════════

    private static object Entry(Action<object> configure)
    {
        var inst = Activator.CreateInstance(_entryType!)!;
        configure(inst);
        return inst;
    }

    private static void Set(object obj, string name, object value)
        => obj.GetType().GetProperty(name)?.SetValue(obj, value);

    private static Dictionary<string, string> L(string en, string zhs)
        => new() { ["en"] = en };

    private static object EnumVal(string name)
        => Enum.Parse(_configTypeEnum!, name);
}
