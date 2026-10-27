using System;
using System.Collections.Generic;
using System.Linq;
using SilicaAdminMod;

namespace SiModUI
{
    // /mods: the chat directory of the server's mods for players who do not run the client mod. It reads the same frame a
    // client would receive (filtered by the caller's powers, Steam IDs scrubbed, numbered navigation excluded), skips mods
    // whose master switch is off, and splits the rest into mods the player can DO something with and mods that only report:
    //   /mods             mods with commands that change something (buy, vote, teleport, your own settings)
    //   /mods info        mods and commands that only show information
    //   /mods <name>      one mod: status lines, its commands with argument hints and descriptions (name, id or a command)
    //   /mods ... <n>     page n when a list is longer than one page
    // Entries are numbered and the menu follows the /buy convention: while it is open for a player, /<number> opens that
    // mod (in a list) or runs that command (in a mod page, commands without arguments only), /back returns to the list,
    // /0, /exit or /close close it. Any other command closes it quietly; it also expires after a few idle minutes.
    internal static class ModsCommand
    {
        private const int PageLines = 9;      // body lines per chat page; header and footer come on top
        private const int MaxLine = 110;      // visible characters per line before the description is cut
        private const float IdleSeconds = 180;
        private const string Name = "<color=#9ad7ff>", Dim = "<color=#a0a0a0>", End = "</color>";

        private enum View { Do, Info, Detail }
        private sealed class State
        {
            public View View = View.Do, From = View.Do;    // From: the list a mod page returns to with /back
            public string ModId = "";
            public readonly List<string> ModIds = new List<string>();     // numbered entries of a list view
            public readonly List<string> CmdTexts = new List<string>();   // numbered entries of a mod page
            public float LastAt;
        }
        private sealed class Page { public string Title = ""; public readonly List<string> Body = new List<string>(); public string Footer = ""; public string Pager = "/mods"; }
        private static readonly Dictionary<ulong, State> states = new Dictionary<ulong, State>();

        internal static void Clear() => states.Clear();
        internal static void Forget(ulong steamId) => states.Remove(steamId);
        private static ulong Steam(Player player) { try { return player.PlayerID.SteamID.m_SteamID; } catch { return 0; } }
        private static float Now => UnityEngine.Time.realtimeSinceStartup;

        // ---- /mods [info|<name>] [page] ----
        internal static void Run(Player caller, string args)
        {
            if (!caller) return;
            try
            {
                string rest = (args ?? "").Trim(); int space = rest.IndexOf(' ');
                rest = space < 0 ? "" : rest.Substring(space + 1).Trim();
                var words = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                int page = 1;
                if (words.Count > 0 && int.TryParse(words[words.Count - 1], out int n) && n > 0) { page = n; words.RemoveAt(words.Count - 1); }
                string query = string.Join(" ", words);

                ulong steam = Steam(caller);
                if (!states.TryGetValue(steam, out var st)) { st = new State(); states[steam] = st; }
                st.LastAt = Now;
                var mods = ModsOf(caller, out bool admin);
                if (query.Length == 0) ShowList(caller, st, mods, admin, View.Do, page);
                else if (query.Equals("info", StringComparison.OrdinalIgnoreCase)) ShowList(caller, st, mods, admin, View.Info, page);
                else
                {
                    var hits = Resolve(mods, query);
                    if (hits.Count == 1) { st.From = View.Do; ShowDetail(caller, st, hits[0], admin, page); }
                    else
                    {
                        var p = new Page();
                        if (hits.Count == 0) { p.Title = "No mod matches '" + Plain(query) + "'."; p.Footer = Dim + "/mods lists them, /mods info the reporting ones." + End; }
                        else { p.Title = "Several mods match '" + Plain(query) + "':"; p.Body.Add(Fit("", Join(hits.Select(m => m.Name).ToList(), MaxLine))); p.Footer = Dim + "Try /mods <one of them>" + End; }
                        Send(caller, p, 1);
                    }
                }
            }
            catch (Exception error) { HelperMethods.SendChatMessageToPlayer(caller, "/mods failed: " + error.GetBaseException().Message); }
        }

        // ---- chat event: numbered navigation while this player's menu is open (same hook and words as /buy) ----
        internal static void OnChat(object sender, OnRequestPlayerChatArgs args)
        {
            try
            {
                var player = args?.Player;
                if (player == null || states.Count == 0) return;
                ulong steam = Steam(player);
                if (!states.TryGetValue(steam, out var st)) return;
                string text = (args.Text ?? "").Trim();
                if (text.Length < 2 || text[0] != '/') return;
                string sub = text.Substring(1).Trim().ToLowerInvariant();
                if (Now - st.LastAt > IdleSeconds) { states.Remove(steam); return; }
                bool isNum = int.TryParse(sub, out int n);
                if (!isNum && sub != "back" && sub != "exit" && sub != "close")
                {
                    // another command: give the menu up quietly so a later /1 is not hijacked; /mods itself re-enters through Run
                    if (!sub.StartsWith("mods", StringComparison.Ordinal)) states.Remove(steam);
                    return;
                }
                args.Block = true;
                st.LastAt = Now;
                Navigate(player, st, sub, n);
            }
            catch (Exception error) { MelonLoader.MelonLogger.Warning("/mods navigation threw: " + error.GetBaseException().Message); }
        }
        private static void Navigate(Player player, State st, string sub, int n)
        {
            ulong steam = Steam(player);
            if (sub == "0" || sub == "exit" || sub == "close") { states.Remove(steam); HelperMethods.SendChatMessageToPlayer(player, Dim + "Mod menu closed." + End); return; }
            var mods = ModsOf(player, out bool admin);
            if (sub == "back")
            {
                if (st.View == View.Detail) ShowList(player, st, mods, admin, st.From, 1);
                else { states.Remove(steam); HelperMethods.SendChatMessageToPlayer(player, Dim + "Mod menu closed." + End); }
                return;
            }
            if (st.View != View.Detail)
            {
                if (n < 1 || n > st.ModIds.Count) { HelperMethods.SendChatMessageToPlayer(player, Dim + "No entry " + n + "; the list has " + st.ModIds.Count + "." + End); return; }
                var mod = mods.FirstOrDefault(m => m.Id.Equals(st.ModIds[n - 1], StringComparison.OrdinalIgnoreCase));
                if (mod == null) { HelperMethods.SendChatMessageToPlayer(player, Dim + "That mod is not available any more." + End); return; }
                st.From = st.View; ShowDetail(player, st, mod, admin, 1);
                return;
            }
            if (n < 1 || n > st.CmdTexts.Count) { HelperMethods.SendChatMessageToPlayer(player, Dim + "No entry " + n + "; the page has " + st.CmdTexts.Count + "." + End); return; }
            var owner = mods.FirstOrDefault(m => m.Id.Equals(st.ModId, StringComparison.OrdinalIgnoreCase));
            var cmd = owner?.Cmds.FirstOrDefault(c => !c.Admin && c.Text.Equals(st.CmdTexts[n - 1], StringComparison.OrdinalIgnoreCase));
            if (cmd == null) { HelperMethods.SendChatMessageToPlayer(player, Dim + "That command is not available any more." + End); return; }
            if (NeedsArgument(cmd)) { HelperMethods.SendChatMessageToPlayer(player, "Type it with its argument: " + cmd.Text + ArgHint(cmd)); return; }
            states.Remove(steam);                         // the command may open a menu of its own
            HelperMethods.SendChatMessageToPlayer(player, Dim + "> " + cmd.Text + End);
            ServerMod.RunLater(player, cmd.Text);          // dispatched from the next update, never nested in this chat event
        }
        private static bool NeedsArgument(Frame.Cmd c)
        {
            if (c.Value != null) return true;                                   // a switch or value row wants its value
            if (c.Choices.Count > 0 && string.IsNullOrEmpty(c.Arg)) return true;
            foreach (var raw in (c.Arg ?? "").Split(',')) { string s = raw.Trim(); if (s.Length > 0 && !s.EndsWith("?")) return true; }
            return false;
        }

        // ---- views ----
        private static List<Frame.Mod> ModsOf(Player player, out bool admin)
        {
            admin = player.IsAdmin();
            return Frame.Build(player).Mods.Values.Where(m => !m.Id.StartsWith("_")).OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        private static IEnumerable<Frame.Cmd> PlayerCmds(Frame.Mod mod) => mod.Cmds.Where(c => !c.Admin);
        // bare command first ("/buy" before "/buy status"), then the mod's own order
        private static List<Frame.Cmd> Ordered(IEnumerable<Frame.Cmd> cmds) => cmds.Select((c, i) => new { c, i }).OrderBy(x => x.c.Text.Contains(" ") ? 1 : 0).ThenBy(x => x.i).Select(x => x.c).ToList();

        private static void ShowList(Player player, State st, List<Frame.Mod> mods, bool admin, View view, int page)
        {
            bool info = view == View.Info;
            var active = mods.Where(Frame.IsEnabled).ToList();
            int off = mods.Count - active.Count;
            st.View = view; st.ModIds.Clear(); st.CmdTexts.Clear(); st.ModId = "";
            var p = new Page { Pager = info ? "/mods info" : "/mods" };
            int otherSide = 0;
            foreach (var mod in active)
            {
                var player_ = PlayerCmds(mod).ToList();
                if (player_.Count == 0) continue;
                bool hasAction = player_.Any(c => !c.Info), hasInfo = player_.Any(c => c.Info);
                if (info ? hasInfo : hasAction)
                {
                    st.ModIds.Add(mod.Id);
                    var cmds = Ordered(player_.Where(c => c.Info == info));
                    p.Body.Add(Fit(st.ModIds.Count + ". " + Name + mod.Name + End + (mod.Version.Length > 0 ? " " + Dim + mod.Version + End : "") + ": ", Join(cmds.Select(c => c.Text).ToList(), 70)));
                }
                if (info ? hasAction && !hasInfo : hasInfo && !hasAction) otherSide++;
            }
            p.Title = (info ? "Mods and commands that show information (" : "Mods you can use on this server (") + st.ModIds.Count + ")"
                + (admin && off > 0 ? " " + Dim + "(" + off + " switched off)" + End : "") + ":";
            if (p.Body.Count == 0) p.Body.Add(Dim + "nothing here right now" + End);
            p.Footer = Dim + "Open: /<number>  |  " + (info ? "back: /mods" : otherSide > 0 ? otherSide + " more only show information: /mods info" : "details: /mods <name>") + "  |  close: /0" + End;
            Send(player, p, page);
        }

        private static void ShowDetail(Player player, State st, Frame.Mod mod, bool admin, int page)
        {
            st.View = View.Detail; st.ModId = mod.Id; st.ModIds.Clear(); st.CmdTexts.Clear();
            bool enabled = Frame.IsEnabled(mod);
            var p = new Page { Pager = "/mods " + (mod.Id.StartsWith("Si_", StringComparison.OrdinalIgnoreCase) ? mod.Id.Substring(3) : mod.Id) };
            p.Title = Name + mod.Name + End + (mod.Version.Length > 0 ? " " + Dim + mod.Version + End : "") + (enabled ? "" : " " + Dim + "(switched off)" + End);
            foreach (var line in Frame.InfoLines(mod).Take(3)) p.Body.Add(Fit(Dim, Plain(line) + End));
            var actions = Ordered(PlayerCmds(mod).Where(c => !c.Info));
            var reports = Ordered(PlayerCmds(mod).Where(c => c.Info));
            if (actions.Count > 0)
            {
                p.Body.Add(Dim + "You can:" + End);
                foreach (var c in actions) { st.CmdTexts.Add(c.Text); p.Body.Add(CommandLine(st.CmdTexts.Count, c)); }
            }
            if (reports.Count > 0)
            {
                p.Body.Add(Dim + "Information:" + End);
                foreach (var c in reports) { st.CmdTexts.Add(c.Text); p.Body.Add(CommandLine(st.CmdTexts.Count, c)); }
            }
            if (admin)
            {
                var adminCmds = Ordered(mod.Cmds.Where(c => c.Admin)).Select(c => c.Text).ToList();
                if (adminCmds.Count > 0) p.Body.Add(Fit(Dim + "Admin: ", Join(adminCmds, 90) + End));
            }
            if (st.CmdTexts.Count == 0) p.Body.Add(Dim + "no commands for you right now" + End);
            p.Footer = Dim + "Run: /<number> (without arguments)  |  <...> argument, [...] optional  |  /back list  |  /0 close" + End;
            Send(player, p, page);
        }

        // exact command, exact name or id, then substrings
        private static List<Frame.Mod> Resolve(List<Frame.Mod> mods, string query)
        {
            string q = query.Trim().TrimStart('/', '!', '.');
            string Bare(string text) => text.TrimStart('/', '!', '.');
            bool CmdMatch(Frame.Mod m, Func<string, bool> test) => m.Cmds.Any(c => test(Bare(c.Text)) || c.Aliases.Any(a => test(Bare(a))));
            string Short(Frame.Mod m) => m.Id.StartsWith("Si_", StringComparison.OrdinalIgnoreCase) ? m.Id.Substring(3) : m.Id;
            var tiers = new List<Func<Frame.Mod, bool>>
            {
                m => CmdMatch(m, t => t.Equals(q, StringComparison.OrdinalIgnoreCase)),
                m => m.Name.Equals(q, StringComparison.OrdinalIgnoreCase) || Short(m).Equals(q, StringComparison.OrdinalIgnoreCase),
                m => m.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || Short(m).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0,
                m => CmdMatch(m, t => t.StartsWith(q, StringComparison.OrdinalIgnoreCase)),
            };
            foreach (var tier in tiers) { var hits = mods.Where(tier).ToList(); if (hits.Count > 0) return hits; }
            return new List<Frame.Mod>();
        }

        // "3. /flex <sol|cent|alien> (now: sol) - Flex: The factions whose commander seat you accept ..."
        private static string CommandLine(int number, Frame.Cmd c)
        {
            string head = number + ". " + c.Text + ArgHint(c);
            string label = (c.Label ?? "").Trim(), desc = (c.Desc ?? "").Trim();
            if (label.Equals(c.Text, StringComparison.OrdinalIgnoreCase)) label = "";
            string tail = label.Length > 0 && desc.Length > 0 ? label + ": " + desc : label + desc;
            return tail.Length == 0 ? head : Fit(head + " " + Dim + "- ", tail + End);
        }
        private static string ArgHint(Frame.Cmd c)
        {
            var parts = new List<string>();
            string choices = c.Choices.Count > 0 ? string.Join("|", c.Choices.Take(6).Select(x => x[0])) + (c.Choices.Count > 6 ? "|..." : "") : null;
            string arg = (c.Arg ?? "").Trim();
            if (arg.Length == 0) { if (c.Value != null || choices != null) parts.Add("<" + (choices ?? "value") + ">"); }
            else
                foreach (var raw in arg.Split(','))
                {
                    string stage = raw.Trim(); if (stage.Length == 0) continue;
                    bool optional = stage.EndsWith("?"); stage = stage.TrimEnd('?').ToLowerInvariant();
                    if (stage == "players") { parts.Add(optional ? "[player ...]" : "<player> [player ...]"); continue; }
                    string hint;
                    switch (stage)
                    {
                        case "choice": hint = choices ?? "choice"; break;
                        case "text": hint = "value"; break;
                        case "ban": hint = "steam id"; break;
                        case "mode": hint = "game mode"; break;
                        default: hint = stage; break;
                    }
                    parts.Add(optional ? "[" + hint + "]" : "<" + hint + ">");
                }
            if (!string.IsNullOrEmpty(c.Value)) parts.Add("(now: " + c.Value + ")");
            return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
        }

        // ---- text helpers ----
        // joins items with ", " up to `budget` visible characters, then "+N more"
        private static string Join(List<string> items, int budget)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < items.Count; i++)
            {
                string next = (sb.Length > 0 ? ", " : "") + items[i];
                if (sb.Length > 0 && sb.Length + next.Length > budget) { sb.Append(" +" + (items.Count - i) + " more"); break; }
                sb.Append(next);
            }
            return sb.ToString();
        }
        // head (may contain colour tags) + tail, the tail cut so the visible text stays within MaxLine; a closing tag is kept
        private static string Fit(string head, string tail)
        {
            int visible = Plain(head).Length + Plain(tail).Length;
            if (visible <= MaxLine) return head + tail;
            bool closes = tail.EndsWith(End);
            string body = closes ? tail.Substring(0, tail.Length - End.Length) : tail;
            int keep = Math.Max(0, MaxLine - Plain(head).Length - 3);
            if (body.Length > keep) body = body.Substring(0, keep).TrimEnd() + "...";
            return head + body + (closes ? End : "");
        }
        private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "");

        private static void Send(Player caller, Page page, int pageNo)
        {
            int pages = Math.Max(1, (page.Body.Count + PageLines - 1) / PageLines);
            pageNo = Math.Min(Math.Max(1, pageNo), pages);
            HelperMethods.SendChatMessageToPlayer(caller, page.Title + (pages > 1 ? " " + Dim + "page " + pageNo + "/" + pages + End : ""));
            foreach (var line in page.Body.Skip((pageNo - 1) * PageLines).Take(PageLines)) HelperMethods.SendChatMessageToPlayer(caller, line);
            string footer = page.Footer;
            if (pageNo < pages) footer = Dim + "More: " + page.Pager + " " + (pageNo + 1) + End + (footer.Length > 0 ? "  " + footer : "");
            if (footer.Length > 0) HelperMethods.SendChatMessageToPlayer(caller, footer);
        }
    }
}
