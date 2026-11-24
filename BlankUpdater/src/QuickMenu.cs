// Quick menu page (part of BlankUpdater)
//
// A page of its own in the game's quick menu, built with BTKUILib: the result of the last check,
// the main settings and every mod from the download server with its own channel (Default, Stable,
// Beta or Don't update). Changes are saved right away and used at the next game start, because the
// updater runs before the mods are loaded.
//
// BTKUILib is optional. All BTKUILib code lives in QuickMenuPage and is only touched after BTKUILib
// was found, through methods that are never inlined, so a missing BTKUILib.dll can't break the updater.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using BTKUILib;
using BTKUILib.UIObjects;
using BTKUILib.UIObjects.Components;
using BTKUILib.UIObjects.Objects;
using MelonLoader;

namespace BlankUpdater
{
    internal static class QuickMenu
    {
        public static void Init(MelonLogger.Instance log)
        {
            bool found = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "BTKUILib");
            if (!found)
            {
                log.Msg("BTKUILib not installed, no quick menu page. Settings are in MelonPreferences.cfg");
                return;
            }

            try
            {
                Build(log);
            }
            catch (Exception e)
            {
                log.Warning("Quick menu page could not be created: " + e.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Build(MelonLogger.Instance log) => QuickMenuPage.Build(log);
    }

    internal static class QuickMenuPage
    {
        private const string ModName = "BlankUpdater";

        private static readonly string[] ChoiceKeys = { "default", "stable", "beta", "ignore" };
        private static readonly List<Action> Refreshers = new List<Action>();
        private static TextBlock _summary;
        private static bool _syncing;

        public static void Build(MelonLogger.Instance log)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
                return;

            var asm = typeof(QuickMenuPage).Assembly;
            foreach (string res in asm.GetManifestResourceNames().Where(r => r.StartsWith("BlankUpdater.Icons.", StringComparison.OrdinalIgnoreCase)))
            {
                string name = res.Substring("BlankUpdater.Icons.".Length);
                name = name.Substring(0, name.Length - ".png".Length);
                var stream = asm.GetManifestResourceStream(res);
                if (stream != null)
                    QuickMenuAPI.PrepareIcon(ModName, name, stream);
            }

            var page = new Page(ModName, "Mod Updater", true, "Tab", null, false)
            {
                MenuTitle = "Mod Updater",
                MenuSubtitle = $"BlankUpdater {BuildInfo.Version}  |  Updates for mods from download.blankinternational.com",
            };
            page.OnPageOpen += Refresh;

            // ---------------- Last check ----------------
            var result = page.AddCategory("Last check", true, false, false);
            _summary = result.AddTextBlock("");
            result.AddTextBlock("Changes on this page are used at the next game start, because updates are installed before the mods load");

            // ---------------- Settings ----------------
            var settings = page.AddCategory("Settings", true, false, false);
            Toggle(settings, plugin.EnabledEntry, "Check at start", "Look for updates every time the game starts");

            var channel = settings.AddButton("Channel", plugin.DefaultChannel == "beta" ? "Beta" : "Stable",
                "Stable: only finished releases. Beta: test versions too, and Stable when it is newer", ButtonStyle.TextWithIcon);
            channel.OnPress += () =>
            {
                var options = new[] { "Stable", "Beta" };
                var selection = new MultiSelection("Channel for all mods", options, plugin.DefaultChannel == "beta" ? 1 : 0);
                selection.OnOptionUpdated += i =>
                {
                    if (i < 0 || i >= options.Length || string.Equals(plugin.ChannelEntry.Value, options[i], StringComparison.OrdinalIgnoreCase))
                        return;
                    plugin.ChannelEntry.Value = options[i];
                    Save();
                    QuickMenuAPI.ShowAlertToast($"Channel: {options[i]}. Used at the next start", 3);
                    Refresh();
                };
                QuickMenuAPI.OpenMultiSelect(selection);
            };
            Refreshers.Add(() => channel.ButtonTooltip = $"Channel for all mods. Now: {(plugin.DefaultChannel == "beta" ? "Beta" : "Stable")}");

            Toggle(settings, plugin.ShowWindowEntry, "Progress window", "Show the animated window while checking and downloading");
            Toggle(settings, plugin.UpdatePluginsEntry, "Update plugins", "Also update plugins, including this updater. They are active after the next start");

            var step = settings.AddSlider("Step time", "How long each step stays visible in the window, in seconds", plugin.MinStepEntry.Value, 0f, 5f, 1, 2f, true);
            step.OnValueUpdated += v =>
            {
                if (_syncing || Math.Abs(v - plugin.MinStepEntry.Value) < 0.05f)
                    return;
                plugin.MinStepEntry.Value = (float)Math.Round(v, 1);
                Save();
            };
            Refreshers.Add(() =>
            {
                if (Math.Abs(step.SliderValue - plugin.MinStepEntry.Value) > 0.05f)
                    step.SetSliderValue(plugin.MinStepEntry.Value);
            });

            var web = settings.AddButton("Download page", "Web", "Opens download.blankinternational.com in your browser", ButtonStyle.TextWithIcon);
            web.OnPress += () =>
            {
                try { Process.Start(plugin.DownloadPage); }
                catch (Exception e) { QuickMenuAPI.ShowAlertToast("Could not open the browser: " + e.Message, 3); }
            };

            // ---------------- Mods ----------------
            var mods = page.AddCategory("Your mods from the server", true, false, false);
            if (plugin.Mods.Count == 0)
                mods.AddTextBlock("No installed mod or plugin is on the download server, or the server was not reachable at the start");
            foreach (var status in plugin.Mods.OrderBy(m => m.IsPlugin).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                ModButton(mods, plugin, status);

            Refresh();
            log.Msg("Quick menu page created");
        }

        // One tile per mod: shows versions and result, opens a choice of channel
        private static void ModButton(Category category, Plugin plugin, ModStatus status)
        {
            string icon = status.IsPlugin ? "Plugin"
                        : status.Result.StartsWith("Updated", StringComparison.Ordinal) ? "Update"
                        : status.Result == "Up to date" ? "UpToDate"
                        : status.Result == "Ignored" ? "Ignore"
                        : "Mod";
            var button = category.AddButton(status.Name, icon, "", ButtonStyle.TextWithIcon);

            button.OnPress += () =>
            {
                string current = plugin.GetModChoice(status.Name);
                var labels = ChoiceKeys.Select(k => ChoiceLabel(k, plugin)).ToArray();
                var selection = new MultiSelection(status.Name, labels, Math.Max(0, Array.IndexOf(ChoiceKeys, current)));
                selection.OnOptionUpdated += i =>
                {
                    if (i < 0 || i >= ChoiceKeys.Length || plugin.GetModChoice(status.Name) == ChoiceKeys[i])
                        return;
                    plugin.SetModChoice(status.Name, ChoiceKeys[i]);
                    QuickMenuAPI.ShowAlertToast($"{status.Name}: {ChoiceLabel(ChoiceKeys[i], plugin)}. Used at the next start", 3);
                    Refresh();
                };
                QuickMenuAPI.OpenMultiSelect(selection);
            };

            Refreshers.Add(() =>
            {
                string versions = $"Installed {status.Installed}  |  Stable {status.Stable ?? "-"}  |  Beta {status.Beta ?? "-"}";
                button.ButtonTooltip = $"{status.Result}  |  {versions}  |  Channel: {ChoiceLabel(plugin.GetModChoice(status.Name), plugin)}";
            });
        }

        private static string ChoiceLabel(string key, Plugin plugin)
        {
            switch (key)
            {
                case "stable": return "Stable";
                case "beta": return "Beta";
                case "ignore": return "Don't update";
                default: return $"Default ({(plugin.DefaultChannel == "beta" ? "Beta" : "Stable")})";
            }
        }

        private static void Refresh()
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
                return;

            _syncing = true;
            try
            {
                foreach (var refresh in Refreshers)
                    refresh();
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[BlankUpdater] Quick menu refresh failed: " + e.Message);
            }
            finally
            {
                _syncing = false;
            }

            string when = plugin.LastCheck == default ? "not yet" : plugin.LastCheck.ToString("HH:mm");
            string text = $"{plugin.LastResult}  |  Checked at {when}  |  {plugin.Mods.Count} mod(s) from the server  |  Channel: {(plugin.DefaultChannel == "beta" ? "Beta" : "Stable")}";
            if (!plugin.EnabledEntry.Value)
                text += "  |  Checking is turned off";
            if (_summary != null && _summary.Text != text)
                _summary.Text = text;
        }

        private static ToggleButton Toggle(Category category, MelonPreferences_Entry<bool> entry, string name, string tooltip)
        {
            var toggle = category.AddToggle(name, tooltip, entry.Value);
            toggle.OnValueUpdated += v =>
            {
                if (_syncing || entry.Value == v)
                    return;
                entry.Value = v;
                Save();
                Refresh();
            };
            Refreshers.Add(() =>
            {
                if (toggle.ToggleValue != entry.Value)
                    toggle.ToggleValue = entry.Value;
            });
            return toggle;
        }

        private static void Save()
        {
            try { MelonPreferences.Save(); }
            catch { }
        }
    }
}
