using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MelonLoader;
using Newtonsoft.Json;
using SilicaAdminMod;

namespace SiModUI
{
    // The static "mod frame": every chat command the Admin Mod registry knows, grouped by the mod that registered it,
    // filtered by what THIS player may execute. Admin commands never leave the server for players without the power.
    internal static class Frame
    {
        internal sealed class Cmd
        {
            [JsonProperty("t")] public string Text;          // what the client sends, e.g. "/buy", "rtv", "!kick"
            [JsonProperty("l")] public string Label;
            [JsonProperty("d")] public string Desc;
            [JsonProperty("a")] public bool Admin;
            [JsonProperty("p")] public string Power;         // required admin power name, "" for player commands
            [JsonProperty("x")] public List<string> Aliases = new List<string>();
            // Picker chain for the arguments, comma separated: "player", "team" (built on the client), "map", "ban" or
            // "choice" (from Choices). "" = free text. "player,team" asks for a player, then a team, then sends both.
            [JsonProperty("arg")] public string Arg = "";
            [JsonProperty("ch")] public List<string[]> Choices = new List<string[]>();   // [value, label] pairs for map / ban / choice stages
            // Current state of a switch: with Choices it renders as one button per choice, the matching one highlighted,
            // a click sends Text + " " + value. Only mods set it (ModUIData "v").
            [JsonProperty("v", NullValueHandling = NullValueHandling.Ignore)] public string Value;
            // Display order inside the mod: registry commands sort by label (1000); a mod's exported list keeps its own order.
            [JsonProperty("o", DefaultValueHandling = DefaultValueHandling.Ignore)] [System.ComponentModel.DefaultValue(1000)] public int Order = 1000;
            [JsonIgnore] public MethodInfo Callback;
        }
        internal sealed class Mod
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("ver")] public string Version;
            [JsonProperty("cat")] public string Category;
            [JsonProperty("prio")] public int Priority;
            [JsonProperty("nav")] public string Nav;         // prefix for numbered menu options, e.g. "/" -> "/1"
            [JsonProperty("hash")] public string Hash;
            [JsonProperty("cmds")] public List<Cmd> Cmds = new List<Cmd>();
            // Optional structured data the mod exposes through `public static string ModUIData()` (e.g. the /buy catalog).
            [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)] public Newtonsoft.Json.Linq.JRaw Data;
        }
        internal sealed class Result { public string Root; public Dictionary<string, Mod> Mods; }

        // Mods may publish a JSON document for the client by exposing `public static string ModUIData()` on any type.
        // The lookup is by assembly name (= mod id); a failing or absent provider leaves the entry without data.
        private static readonly Dictionary<string, MethodInfo> providers = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> providerWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The document may carry "commands": [{"t":"/rtsai status","l":"Status","d":"...","a":false,"arg":"","ch":[[value,label]]}]
        // for sub-commands the Admin Mod registry cannot see. They are merged into the mod's command list here, admin ones
        // only for admins, and removed from the data that leaves the server.
        private static Newtonsoft.Json.Linq.JRaw ModData(string id, bool admin, Mod mod)
        {
            var raw = ModData(id);
            if (raw == null) return null;
            try
            {
                var doc = Newtonsoft.Json.Linq.JObject.Parse(raw.ToString());
                if (doc["commands"] is Newtonsoft.Json.Linq.JArray commands)
                {
                    int order = 0;
                    foreach (var entry in commands.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        order++;
                        List<string[]> choices = null;
                        if (entry["ch"] is Newtonsoft.Json.Linq.JArray ch)
                        {
                            choices = new List<string[]>();
                            foreach (var choice in ch.OfType<Newtonsoft.Json.Linq.JArray>())
                                if (choice.Count >= 2) choices.Add(new[] { (string)choice[0] ?? "", (string)choice[1] ?? "" });
                        }
                        MergeEntry(mod, id, (string)entry["t"] ?? "", entry["a"] != null && (bool)entry["a"], admin,
                            (string)entry["l"], (string)entry["d"], (string)entry["arg"], (string)entry["v"], choices, order);
                    }
                    doc.Remove("commands");
                }
                return doc.Count == 0 ? null : new Newtonsoft.Json.Linq.JRaw(JsonConvert.SerializeObject(doc, Formatting.None));
            }
            catch (Exception error)
            {
                if (providerWarned.Add(id + "#commands")) MelonLogger.Warning("ModUIData commands of " + id + " unreadable: " + error.GetBaseException().Message);
                return raw;
            }
        }
        // One entry from a mod's export or from the menu.json manifest: a new sub-command of the mod, or decoration (label,
        // description, picker, state) of a command the registry already knows. It cannot turn a player command into an admin
        // one or vice versa. Null fields leave the existing value alone. Admin entries exist only in admin frames.
        private static void MergeEntry(Mod mod, string source, string text, bool isAdmin, bool viewerAdmin, string label, string desc, string arg, string value, List<string[]> choices, int order)
        {
            if (string.IsNullOrEmpty(text) || (isAdmin && !viewerAdmin) || !Protocol.ValidCommandText(text)) return;
            var cmd = mod.Cmds.FirstOrDefault(c => c.Text.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (cmd != null && cmd.Admin != isAdmin)
            {
                if (providerWarned.Add(source + "#" + text)) MelonLogger.Warning(source + ": entry '" + text + "' dropped, the registry has it as a " + (cmd.Admin ? "admin" : "player") + " command; use a distinct sub-command text.");
                return;
            }
            bool fresh = cmd == null;
            if (fresh) cmd = new Cmd { Text = text, Admin = isAdmin, Power = isAdmin ? Power.Generic.ToString() : "", Label = text };
            cmd.Order = order;
            if (label != null) cmd.Label = label;
            if (desc != null) cmd.Desc = Plain(desc);
            if (arg != null) cmd.Arg = arg;
            if (value != null) cmd.Value = value;
            if (choices != null) { cmd.Choices = choices; if (cmd.Arg.Length == 0 && choices.Count > 0) cmd.Arg = "choice"; }
            if (fresh) mod.Cmds.Add(cmd);
        }
        // menu.json: Commands[text].Subs describe sub-commands of mods without an export (databomb's), hand-written from
        // their documentation. Applied after the registry so every sub attaches to its parent's mod.
        private static void ApplyManifest(Dictionary<string, Mod> mods, MenuConfig cfg, bool admin)
        {
            foreach (var mod in mods.Values.ToArray())
                foreach (var parent in mod.Cmds.ToArray())
                {
                    if (!cfg.Commands.TryGetValue(parent.Text, out var cc) || cc?.Subs == null) continue;
                    int order = 0;
                    foreach (var sub in cc.Subs)
                    {
                        if (sub == null || string.IsNullOrEmpty(sub.Text)) continue;
                        MergeEntry(mod, "menu.json", sub.Text, sub.Admin, admin, sub.Label, sub.Description, sub.Arg, null, sub.Choices, ++order);
                    }
                }
        }
        private static Newtonsoft.Json.Linq.JRaw ModData(string id)
        {
            if (!providers.TryGetValue(id, out var method))
            {
                method = null;
                try
                {
                    foreach (var melon in MelonMod.RegisteredMelons)
                    {
                        var assembly = melon.MelonAssembly?.Assembly;
                        if (assembly == null || assembly.GetName().Name != id) continue;
                        foreach (var type in assembly.GetTypes())
                        {
                            var m = type.GetMethod("ModUIData", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                                ?? type.GetMethod("SquadHudData", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);   // pre-rename hook name
                            if (m != null && m.ReturnType == typeof(string)) { method = m; break; }
                        }
                        break;
                    }
                }
                catch (Exception error) { if (providerWarned.Add(id)) MelonLogger.Warning("ModUIData lookup failed for " + id + ": " + error.GetBaseException().Message); }
                providers[id] = method;
            }
            if (method == null) return null;
            try
            {
                string json = method.Invoke(null, null) as string;
                if (string.IsNullOrEmpty(json)) return null;
                // the server's Newtonsoft lacks JToken.ToString(Formatting); JsonConvert is the safe compact serializer
                return new Newtonsoft.Json.Linq.JRaw(JsonConvert.SerializeObject(Newtonsoft.Json.Linq.JToken.Parse(json), Formatting.None));
            }
            catch (Exception error)
            {
                if (providerWarned.Add(id)) MelonLogger.Warning("ModUIData of " + id + " failed: " + error.GetBaseException().Message);
                return null;
            }
        }

        private static readonly Regex NavCommand = new Regex(@"^(?:\d{1,4}|back|exit|close|0)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Dictionary<Power, Result> cache = new Dictionary<Power, Result>();
        private static float cacheAt = -100;

        // After a command ran its mod's state may have changed: the next Build starts fresh so the switches update at once.
        internal static void Invalidate() => cacheAt = -100;
        internal static Result Build(Player player)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - cacheAt > 2) { cache.Clear(); cacheAt = now; }
            bool admin = player.IsAdmin();
            Power powers = admin ? player.GetAdminPowers() : Power.None;
            if (cache.TryGetValue(powers, out var cached)) return cached;
            var result = BuildFor(admin, powers);
            cache[powers] = result; return result;
        }

        internal static Result BuildPublic() => BuildFor(false, Power.None);
        private static Result BuildFor(bool admin, Power powers)
        {
            var cfg = MenuConfig.Current;
            var mods = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
            var byCallback = new Dictionary<MethodInfo, Cmd>();
            try
            {
                foreach (var c in PlayerMethods.PlayerCommands.ToArray()) Add(mods, byCallback, cfg, c.PlayerCommandCallback, "/" + c.CommandName, false, Power.None, "", admin);
                foreach (var c in PlayerMethods.PlayerPhrases.ToArray()) Add(mods, byCallback, cfg, c.PlayerCommandCallback, c.CommandName, false, Power.None, "", admin);
                if (admin)
                    foreach (var a in AdminMethods.AdminCommands.ToArray())
                        if (AdminMethods.PowerInPowers(a.AdminPower, powers))
                            Add(mods, byCallback, cfg, a.AdminCallback, "!" + a.AdminCommandText, true, a.AdminPower, a.AdminCommandDescription, admin);
            }
            catch (Exception error) { MelonLogger.Warning("Frame build incomplete: " + error.GetBaseException().Message); }

            // Match control: a fixed admin section that gathers selected admin commands from any mod.
            if (admin)
            {
                var match = new Mod { Id = "_match", Name = cfg.Categories.TryGetValue("match", out var mn) ? mn : "Match control", Version = "", Category = "match", Priority = 0, Nav = "!" };
                foreach (string name in cfg.MatchControl)
                {
                    var found = mods.Values.SelectMany(m => m.Cmds).FirstOrDefault(c => c.Admin && (c.Text.Equals("!" + name, StringComparison.OrdinalIgnoreCase) || c.Aliases.Any(x => x.Equals("!" + name, StringComparison.OrdinalIgnoreCase))));
                    if (found != null && !match.Cmds.Contains(found)) match.Cmds.Add(found);
                }
                if (match.Cmds.Count > 0) mods[match.Id] = match;
            }
            ApplyManifest(mods, cfg, admin);
            foreach (var mod in mods.Values.ToArray())
            {
                mod.Cmds = mod.Cmds.OrderBy(c => c.Admin).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (mod.Id != "_match") mod.Data = ModData(mod.Id, admin, mod);
                mod.Cmds = mod.Cmds.OrderBy(c => c.Admin).ThenBy(c => c.Order).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (mod.Cmds.Count == 0) { mods.Remove(mod.Id); continue; }
                mod.Hash = ""; mod.Hash = Protocol.ShortHash(JsonConvert.SerializeObject(mod, Formatting.None));
            }
            cfg.SaveIfDirty();
            string root = Protocol.ShortHash(string.Join("\n", mods.Values.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id + ":" + m.Hash)));
            return new Result { Root = root, Mods = mods };
        }

        private static void Add(Dictionary<string, Mod> mods, Dictionary<MethodInfo, Cmd> byCallback, MenuConfig cfg, HelperMethods.CommandCallback callback, string text, bool admin, Power power, string description, bool viewerAdmin)
        {
            if (callback == null || string.IsNullOrEmpty(text)) return;
            string bare = text.TrimStart('/', '!', '.');
            if (bare.Length == 0 || NavCommand.IsMatch(bare)) return;                       // numbered menu navigation / vote options are not catalog entries
            if (cfg.Hidden.Any(h => h.Equals(text, StringComparison.OrdinalIgnoreCase) || h.Equals(bare, StringComparison.OrdinalIgnoreCase))) return;
            if (!admin && cfg.Commands.TryGetValue(text, out var adminOnly) && adminOnly != null && adminOnly.AdminOnly)
            {
                if (!viewerAdmin) return;                                                     // menu.json AdminOnly: a player command whose handler is admin-gated
                admin = true; power = Power.Generic;
            }
            var method = callback.Method;
            if (byCallback.TryGetValue(method, out var existing))
            {
                // same handler registered under another name (rtv / rockthevote, command + phrase): keep one entry, list aliases
                if (!existing.Text.Equals(text, StringComparison.OrdinalIgnoreCase) && !existing.Aliases.Contains(text)) existing.Aliases.Add(text);
                return;
            }
            var assembly = OwningAssembly(callback);
            string id = assembly?.GetName().Name ?? "unknown";
            if (id == "SilicaAdminMod") id = "Si_AdminMod";
            cfg.Commands.TryGetValue(text, out var cc);
            if (!string.IsNullOrEmpty(cc?.Mod)) id = cc.Mod;                                   // sidecar override
            else if (assembly == null || IsFramework(assembly)) id = cfg.Attribute(text, id);   // DynamicMethod shims cannot be attributed by reflection on Mono
            if (!mods.TryGetValue(id, out var mod))
            {
                var info = assembly != null && assembly.GetName().Name == id ? ModInfo(assembly) : ModInfo(id);
                var mc = cfg.Mod(id, info.Key ?? id);
                mod = new Mod { Id = id, Name = mc.Name ?? id, Version = info.Value ?? "", Category = string.IsNullOrEmpty(mc.Category) ? "player" : mc.Category, Priority = mc.Priority, Nav = string.IsNullOrEmpty(mc.Nav) ? "/" : mc.Nav };
                mods[id] = mod;
            }
            var cmd = new Cmd
            {
                Text = text, Admin = admin, Power = admin ? power.ToString() : "", Callback = method,
                Label = cc?.Label ?? text,
                Desc = cc?.Description ?? Plain(description ?? "")
            };
            if (admin && cmd.Desc.Length == 0) cmd.Desc = "Requires admin power " + power + ".";
            cmd.Arg = cfg.ArgType(text) ?? "";
            if (cc?.Choices != null && cc.Choices.Count > 0) { cmd.Choices = cc.Choices; if (cmd.Arg.Length == 0) cmd.Arg = "choice"; }
            if (cmd.Arg.Length == 0 && cmd.Choices.Count == 0) InferArgs(cmd, description ?? "");
            if (cmd.Arg == "map") cmd.Choices = MapChoices();
            else if (cmd.Arg == "ban") cmd.Choices = BanChoices();
            mod.Cmds.Add(cmd); byCallback[method] = cmd;
        }
        // Mods that are not ours still describe their arguments in the registered description, databomb style:
        // "Usage: !team [optional:<player>] <teamname>", "!spawn <unit> on|off". Placeholders map to picker stages
        // (<player>, <team...>, <map...>), a literal a|b|c group to a choice; the chain stops at the first unknown token.
        private static readonly Regex UsageToken = new Regex(@"<([a-z][a-z0-9 _-]*)>|\b([a-z0-9_-]+(?:\|[a-z0-9_-]+)+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static void InferArgs(Cmd cmd, string description)
        {
            int at = description.IndexOf("usage", StringComparison.OrdinalIgnoreCase);
            if (at < 0) return;
            var stages = new List<string>();
            foreach (Match m in UsageToken.Matches(description.Substring(at)))
            {
                if (m.Groups[1].Success)
                {
                    string name = m.Groups[1].Value.ToLowerInvariant();
                    if (name.Contains("player") || name == "target" || name == "name") stages.Add("player");
                    else if (name.StartsWith("team")) stages.Add("team");
                    else if (name.StartsWith("map")) stages.Add("map");
                    else break;
                }
                else if (stages.Count == 0 && cmd.Choices.Count == 0)
                {
                    foreach (string option in m.Groups[2].Value.Split('|')) cmd.Choices.Add(new[] { option, option.ToUpperInvariant() });
                    stages.Add("choice"); break;   // a choice group ends the chain: the client offers it as the (single) picker
                }
                else break;
            }
            if (stages.Count > 0) cmd.Arg = string.Join(",", stages);
        }
        // The game's own ban list (serversettings.xml); the value is the Steam64 id that !unban accepts.
        private static List<string[]> BanChoices()
        {
            var result = new List<string[]>();
            try
            {
                var bans = NetworkServerSettings.Instance?.BannedPlayers;
                if (bans == null) return result;
                foreach (var ban in bans)
                {
                    if (ban == null) continue;
                    string id = ban.m_ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    result.Add(new[] { id, Plain(ban.m_Name ?? "?") + "  (" + id + ")" });
                }
            }
            catch (Exception error) { MelonLogger.Warning("Ban list unavailable: " + error.GetBaseException().Message); }
            return result.OrderBy(c => c[1], StringComparer.OrdinalIgnoreCase).ToList();
        }
        // Multiplayer maps the game knows; the value is the file name the map commands expect.
        private static List<string[]> MapChoices()
        {
            var result = new List<string[]>();
            try
            {
                var levels = GameDatabase.Database?.AllLevels;
                if (levels == null) return result;
                foreach (var level in levels)
                {
                    if (level == null || !level.Enabled || !level.IsMultiplayer || level.IsDeveloperOnly || string.IsNullOrEmpty(level.FileName)) continue;
                    string label = Plain(level.DisplayName);
                    result.Add(new[] { level.FileName, label.Length > 0 && label != level.FileName ? label + "  (" + level.FileName + ")" : level.FileName });
                }
            }
            catch (Exception error) { MelonLogger.Warning("Map list unavailable: " + error.GetBaseException().Message); }
            return result.OrderBy(c => c[1], StringComparer.OrdinalIgnoreCase).ToList();
        }

        // A callback created by reflection (Delegate.CreateDelegate on another delegate's Invoke) reports mscorlib
        // as the declaring assembly; the mod is the delegate's target, unwrapped as deep as needed.
        private static Assembly OwningAssembly(Delegate callback)
        {
            for (int depth = 0; callback != null && depth < 8; depth++)
            {
                var assembly = callback.Method?.DeclaringType?.Assembly;
                if (assembly != null && !IsFramework(assembly)) return assembly;
                // DynamicMethod shims (Unit Balance) carry no declaring type, but their owner module is the mod's.
                Assembly module = null;
                try { module = callback.Method?.Module?.Assembly; } catch { }
                if (module != null && !IsFramework(module)) return module;
                var target = callback.Target;
                if (target is Delegate inner) { callback = inner; continue; }
                if (target != null && !IsFramework(target.GetType().Assembly)) return target.GetType().Assembly;
                return assembly;
            }
            return null;
        }
        private static bool IsFramework(Assembly assembly)
        {
            string name = assembly.GetName().Name;
            return name == "mscorlib" || name == "netstandard" || name.StartsWith("System", StringComparison.Ordinal);
        }
        private static KeyValuePair<string, string> ModInfo(string assemblyName)
        {
            try
            {
                foreach (var melon in MelonMod.RegisteredMelons)
                    if (melon.MelonAssembly?.Assembly?.GetName().Name == assemblyName) return new KeyValuePair<string, string>(melon.Info.Name, melon.Info.Version);
            }
            catch { }
            return new KeyValuePair<string, string>(null, "");
        }
        private static KeyValuePair<string, string> ModInfo(Assembly assembly)
        {
            try
            {
                foreach (var melon in MelonMod.RegisteredMelons)
                    if (melon.MelonAssembly?.Assembly == assembly) return new KeyValuePair<string, string>(melon.Info.Name, melon.Info.Version);
            }
            catch { }
            return new KeyValuePair<string, string>(null, assembly?.GetName().Version?.ToString());
        }
        private static string Plain(string text) => Regex.Replace(text ?? "", "<[^>]*>", "").Replace('\n', ' ').Trim();

        // Everything the client does not have yet (by per-mod hash), plus ids it should drop.
        internal static string Diff(Result frame, Dictionary<string, string> known, bool same)
        {
            var cfg = MenuConfig.Current;
            if (same) return JsonConvert.SerializeObject(new { k = "frame", root = frame.Root, same = true }, Formatting.None);
            var changed = frame.Mods.Values.Where(m => !known.TryGetValue(m.Id, out var h) || h != m.Hash).OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var removed = known.Keys.Where(id => !frame.Mods.ContainsKey(id)).ToList();
            return JsonConvert.SerializeObject(new { k = "frame", root = frame.Root, full = known.Count == 0, cats = cfg.Categories, mods = changed, removed }, Formatting.None);
        }
    }
}
