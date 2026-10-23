using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;

namespace SiModUI
{
    // Runs one chat command on behalf of a player through the Admin Mod's own dispatch entry point and captures
    // what the handling mod prints to THAT player. No mod-specific code: whatever a chat user can do, the UI can do.
    internal static class Proxy
    {
        private static readonly Regex NavCommand = new Regex(@"^(?:\d{1,4}|back|exit|close)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static Player captureTarget;
        private static List<string> captured;
        internal static bool Installed { get; private set; }

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            var chat = AccessTools.Method(typeof(HelperMethods), "NetworkSendChat");
            var console = AccessTools.Method(typeof(HelperMethods), "NetworkSendConsole");
            if (chat == null || console == null) throw new InvalidOperationException("Admin Mod chat send methods not found; command replies cannot be captured.");
            harmony.Patch(chat, prefix: new HarmonyMethod(typeof(Proxy), nameof(ChatPrefix)));
            harmony.Patch(console, prefix: new HarmonyMethod(typeof(Proxy), nameof(ConsolePrefix)));
            Installed = true;
        }
        private static bool ChatPrefix(Player __0, string[] __2) => !Capture(__0, __2);

        // Console sends: the Admin Mod splits a long message list into packets by its own size estimate, but the estimate
        // misses the writer's debug name (GetGameByteStreamWriter(0, "Si_AdminMod::NetworkSendConsole", writeDebug: true)
        // puts ~33 bytes into every packet) and never splits a single oversized string. A chunk that lands near the 2400-byte
        // cap is truncated by the receiver and the client throws EndOfStreamException in NetworkLayer.ProcessMessage (seen
        // 2026-10-23: the welcome banner with 40+ registered commands). Long lists are re-chunked here with a safe budget and
        // sent through the Admin Mod's own packet writer; short lists are left to the Admin Mod.
        private const int SafeChunkBytes = 1800;
        private const int PacketHeaderBytes = 64;
        private static readonly System.Reflection.MethodInfo consoleNetcode = AccessTools.Method(typeof(HelperMethods), "NetworkSendConsole_Netcode");
        private static bool ConsolePrefix(Player __0, string[] __1)
        {
            if (Capture(__0, __1)) return false;
            if (consoleNetcode == null || __1 == null || __1.Length == 0 || EstimateBytes(__1) <= SafeChunkBytes) return true;
            foreach (var chunk in Chunks(__1))
            {
                try { consoleNetcode.Invoke(null, new object[] { __0, chunk }); }
                catch (Exception e) { MelonLogger.Warning("Console chunk send failed: " + e.GetBaseException().Message); break; }
            }
            return false;
        }
        private static int Size(string s) => System.Text.Encoding.UTF8.GetByteCount(s ?? "") + 6;   // type tag + length + text + level byte
        private static int EstimateBytes(string[] messages) { int n = PacketHeaderBytes; foreach (var s in messages) n += Size(s); return n; }
        private static List<List<string>> Chunks(string[] messages)
        {
            var chunks = new List<List<string>>(); var cur = new List<string>(); int size = PacketHeaderBytes;
            foreach (var raw in messages)
                foreach (var piece in SplitBytes(raw ?? "", SafeChunkBytes - PacketHeaderBytes - 6))
                {
                    int ps = Size(piece);
                    if (cur.Count > 0 && size + ps > SafeChunkBytes) { chunks.Add(cur); cur = new List<string>(); size = PacketHeaderBytes; }
                    cur.Add(piece); size += ps;
                }
            if (cur.Count > 0) chunks.Add(cur);
            return chunks;
        }
        private static IEnumerable<string> SplitBytes(string s, int maxBytes)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(s) <= maxBytes) { yield return s; yield break; }
            int start = 0;
            while (start < s.Length)
            {
                int bytes = 0, end = start;
                while (end < s.Length)
                {
                    int step = char.IsHighSurrogate(s[end]) && end + 1 < s.Length ? 2 : 1;
                    int b = System.Text.Encoding.UTF8.GetByteCount(s.Substring(end, step));
                    if (bytes + b > maxBytes) break;
                    bytes += b; end += step;
                }
                if (end == start) end = start + 1;
                yield return s.Substring(start, end - start);
                start = end;
            }
        }
        private static bool Capture(Player recipient, string[] messages)
        {
            if (captureTarget == null || !ReferenceEquals(recipient, captureTarget)) return false;
            captured.Add(string.Concat(messages ?? Array.Empty<string>()));
            return true;
        }

        // Only text the chat dispatcher would route to a registered handler is accepted; nothing else reaches a mod.
        internal static string Validate(string text)
        {
            if (!Protocol.ValidCommandText(text)) return "Invalid command text.";
            text = text.Trim();
            char c = text[0];
            if (HelperMethods.IsValidCommandPrefix(c))
            {
                string token = text.Substring(1).Split(' ')[0];
                if (token.Length == 0) return "Empty command.";
                if (NavCommand.IsMatch(token)) return null;
                if (HelperMethods.GetPlayerCommand(text) != null || HelperMethods.GetAdminCommand(text) != null) return null;
                return "Unknown command " + c + token + ".";
            }
            if (HelperMethods.GetPlayerPhrase(text) != null) return null;
            return "Not a registered command.";
        }

        internal static List<string> Execute(Player player, string text, out string error)
        {
            error = null;
            var lines = new List<string>();
            if (!Installed) { error = "Capture unavailable."; return lines; }
            captureTarget = player; captured = lines;
            try { Event_Chat.FireOnRequestPlayerChatEvent(player, text.Trim(), false); }
            catch (Exception e) { error = e.GetBaseException().Message; MelonLogger.Warning("Command '" + text + "' threw: " + e.GetBaseException().Message); }
            finally { captureTarget = null; captured = null; }
            return lines;
        }
    }
}
