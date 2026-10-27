using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using SilicaAdminMod;
using UnityEngine;

[assembly: MelonInfo(typeof(SiModUI.ServerMod), "Si_ModUI_Server", "1.5.2", "DrMuck, Davey")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.MONO)]

namespace SiModUI
{
    // Server companion: subscriptions, frame push + diffs, command proxy replies, server-triggered events.
    // Clients never poll. The only client-initiated traffic is one hello per connection and one packet per command.
    public sealed class ServerMod : MelonMod
    {
        internal sealed class Subscriber
        {
            public string Key, Nonce = "", Root = "";
            public NetworkID Peer; public int Channel;
            public Dictionary<string, string> Known = new Dictionary<string, string>();
            public bool HelloPending; public float HelloAt = -100, LastCommandAt = -100;
            public long LastSeq = -1;
            public readonly Queue<KeyValuePair<long, string>> Pending = new Queue<KeyValuePair<long, string>>();
        }
        private static readonly Dictionary<string, Subscriber> subscribers = new Dictionary<string, Subscriber>();
        private static readonly Queue<KeyValuePair<string, string>> outbox = new Queue<KeyValuePair<string, string>>();
        private static readonly AccessTools.FieldRef<GameByteStreamReader, MemoryStream> Stream = AccessTools.FieldRefAccess<GameByteStreamReader, MemoryStream>("m_MemoryStream");
        private static ServerMod instance;
        private static bool enabled, voteWasInProgress;
        private static float tokens = TokenCap, lastFlush, nextTick, nextTrafficLog, nextError;
        private static long sentPackets, sentBytes, lastPackets, lastBytes, droppedPackets, commandsRun;
        // 160 packets/s * <=1.8 KB body = ~290 KB/s ceiling over all clients; bursts of 16 packets.
        private const float TokensPerSecond = 160, TokenCap = 16;

        public override void OnInitializeMelon()
        {
            instance = this;
            enabled = !Enum.GetValues(typeof(ENetworkPacketType)).Cast<object>().Any(v => Convert.ToInt32(v) == Protocol.PacketType);
            if (!enabled) { LoggerInstance.Error("Transport disabled: packet type " + Protocol.PacketType + " is now used by the game."); return; }
        }
        public override void OnLateInitializeMelon()
        {
            if (!enabled) return;
            MenuConfig.Load();
            try { Proxy.Install(HarmonyInstance); } catch (Exception error) { LoggerInstance.Error(error.Message); }
            try { Events.Install(HarmonyInstance); } catch (Exception error) { LoggerInstance.Warning("Vote events unavailable: " + error.Message); }
            GameEvents.OnPlayerLeft += PlayerLeft;
            try { HelperMethods.RegisterAdminCommand("moduitest", HudTest, Power.Generic, "!moduitest [chat command] - dump your HUD frame to UserData/cfg/ModUI, or run a command through the HUD proxy and echo its captured reply"); }
            catch (Exception error) { LoggerInstance.Warning("!moduitest not registered: " + error.Message); }
            // !slay <player>: kill that player's current unit (they respawn). No databomb mod has it; the Admin Mod reserves the
            // Power.Slay level for it. Registered here so the Match control page gets a Slay button with the player picker.
            try { HelperMethods.RegisterAdminCommand("slay", Slay, Power.Slay, "!slay <player> - kill that player's current unit (they respawn)"); }
            catch (Exception error) { LoggerInstance.Warning("!slay not registered: " + error.Message); }
            // /mods: chat directory of the server's mods for players without the client mod (see ModsCommand.cs); its
            // numbered navigation (/1, /back, /0) rides on the Admin Mod's chat event like the /buy menu does.
            try
            {
                PlayerMethods.RegisterPlayerCommand("mods", ModsCommand.Run, true);
                Event_Chat.OnRequestPlayerChat -= ModsCommand.OnChat;
                Event_Chat.OnRequestPlayerChat += ModsCommand.OnChat;
            }
            catch (Exception error) { LoggerInstance.Warning("/mods not registered: " + error.Message); }
            LoggerInstance.Msg("READY - marker " + Protocol.Marker + ", config " + MenuConfig.Path);
        }
        private static bool frameLogged;
        private static void LogPublicFrame()
        {
            frameLogged = true;
            var frame = Frame.BuildPublic();
            string json = Frame.Diff(frame, new Dictionary<string, string>(), false);
            int chunks = Protocol.Transfer(Protocol.NewNonce(), json).Length;
            instance?.LoggerInstance.Msg("Public frame: root " + frame.Root + ", " + frame.Mods.Count + " mods, " + frame.Mods.Values.Sum(m => m.Cmds.Count) + " commands, " + Encoding.UTF8.GetByteCount(json) + " bytes JSON -> " + chunks + " packet(s).");
            foreach (var mod in frame.Mods.Values.OrderBy(m => m.Priority).ThenBy(m => m.Name))
                instance?.LoggerInstance.Msg("  " + mod.Id + " [" + mod.Category + " " + mod.Priority + "] " + mod.Name + " " + mod.Version + ": " + string.Join(" ", mod.Cmds.Select(c => c.Text + (c.Aliases.Count > 0 ? "(" + string.Join(",", c.Aliases) + ")" : ""))));
        }
        // Test aid for admins before the client mod exists: exercises frame + proxy from an ordinary game client.
        private static void Slay(Player caller, string args)
        {
            try
            {
                string rest = (args ?? "").Trim(); int space = rest.IndexOf(' ');
                rest = space < 0 ? "" : rest.Substring(space + 1).Trim();
                if (rest.Length == 0) { HelperMethods.SendChatMessageToPlayer(caller, "Usage: !slay <player>"); return; }
                Player target = HelperMethods.FindTargetPlayer(rest);
                if (target == null) { HelperMethods.SendChatMessageToPlayer(caller, "No unique player matches '" + rest + "'."); return; }
                var unit = target.ControlledUnit;
                if (unit == null) { HelperMethods.SendChatMessageToPlayer(caller, target.PlayerName + " has no unit right now."); return; }
                unit.Suicide();   // server side: sets the (driven) unit's health to 0, a normal death with respawn
                instance?.LoggerInstance.Msg("slay: " + (caller ? caller.PlayerName : "console") + " -> " + target.PlayerName);
                HelperMethods.SendChatMessageToAll(HelperMethods.GetTeamColor(target) + target.PlayerName + "</color> was slain by an admin.");
            }
            catch (Exception error) { Report(error); HelperMethods.SendChatMessageToPlayer(caller, "slay failed: " + error.GetBaseException().Message); }
        }
        private static void HudTest(Player caller, string args)
        {
            if (!caller) return;
            try
            {
                string rest = args.Trim(); int space = rest.IndexOf(' ');
                rest = space < 0 ? "" : rest.Substring(space + 1).Trim();
                if (rest.Length == 0)
                {
                    var frame = Frame.Build(caller);
                    string json = Frame.Diff(frame, new Dictionary<string, string>(), false);
                    string file = Path.Combine(Path.GetDirectoryName(MenuConfig.Path), "last_frame_" + caller.PlayerID.SteamID.m_SteamID + ".json");
                    File.WriteAllText(file, json);
                    int chunks = Protocol.Transfer(Protocol.NewNonce(), json).Length;
                    string summary = "HUD frame: root " + frame.Root + ", " + frame.Mods.Count + " mods, " + frame.Mods.Values.Sum(m => m.Cmds.Count) + " commands, " + Encoding.UTF8.GetByteCount(json) + " bytes -> " + chunks + " packet(s). Saved " + file;
                    instance?.LoggerInstance.Msg(summary);
                    HelperMethods.SendChatMessageToPlayer(caller, summary);
                    return;
                }
                string error = Proxy.Validate(rest);
                if (error != null) { HelperMethods.SendChatMessageToPlayer(caller, "HUD proxy refused: " + error); return; }
                var lines = Proxy.Execute(caller, rest, out error);
                instance?.LoggerInstance.Msg("moduitest " + caller.PlayerName + ": " + rest + " -> " + lines.Count + " captured lines" + (error == null ? "" : ", error " + error));
                HelperMethods.SendChatMessageToPlayer(caller, "HUD proxy ran '" + rest + "': " + lines.Count + " line(s) captured" + (error == null ? "" : ", error: " + error));
                foreach (var line in lines.Take(30)) HelperMethods.SendChatMessageToPlayer(caller, "[HUD] " + line);
            }
            catch (Exception error) { Report(error); HelperMethods.SendChatMessageToPlayer(caller, "moduitest failed: " + error.GetBaseException().Message); }
        }
        public override void OnSceneWasLoaded(int index, string name) { subscribers.Clear(); outbox.Clear(); ModsCommand.Clear(); deferred.Clear(); }
        public override void OnDeinitializeMelon() { enabled = false; subscribers.Clear(); outbox.Clear(); GameEvents.OnPlayerLeft -= PlayerLeft; Event_Chat.OnRequestPlayerChat -= ModsCommand.OnChat; }
        // Chat commands a menu wants to run on a player's behalf (/mods "/3" -> "/elo top"): dispatched from the next
        // update through the Admin Mod's own entry point, never nested inside the chat event that asked for it.
        private static readonly Queue<KeyValuePair<Player, string>> deferred = new Queue<KeyValuePair<Player, string>>();
        internal static void RunLater(Player player, string text) { if (player && !string.IsNullOrEmpty(text)) deferred.Enqueue(new KeyValuePair<Player, string>(player, text)); }
        private static void RunDeferred()
        {
            int budget = 8;
            while (deferred.Count > 0 && budget-- > 0)
            {
                var item = deferred.Dequeue();
                if (!item.Key) continue;
                try { Event_Chat.FireOnRequestPlayerChatEvent(item.Key, item.Value, false); }
                catch (Exception error) { Report(error); }
            }
        }
        private static void PlayerLeft(Player player)
        {
            if (!player) return;
            try { ModsCommand.Forget(player.PlayerID.SteamID.m_SteamID); } catch { }
            foreach (var key in subscribers.Where(p => p.Value.Peer.SteamID.m_SteamID == player.PlayerID.SteamID.m_SteamID && p.Value.Channel == player.PlayerChannel).Select(p => p.Key).ToArray())
            { subscribers.Remove(key); instance?.LoggerInstance.Msg("Client left: " + key); }
        }

        [HarmonyPatch(typeof(NetworkLayer), "ProcessMessage")]
        private static class Receive
        {
            private static bool Prefix(GameByteStreamReader __0, uint __1, NetworkID __2, bool __3)
            {
                if (!enabled || !__3 || !Game.GetIsServer() || Game.GetIsClient() || __0 == null || __1 < 2) return true;
                var stream = Stream(__0);
                long position = stream.Position;
                bool ours = false;
                try
                {
                    if (__0.ReadByte() != Protocol.PacketType || __1 > Protocol.MaxPacket) return true;
                    if (__1 - stream.Position < Protocol.Marker.Length + 2 || !Protocol.ReadMarker(stream.ReadByte, (byte)EGameByteStreamType.String)) return true;
                    ours = true;
                    string body = __0.ReadString();
                    if (stream.Position > __1) return false;
                    float now = Time.realtimeSinceStartup;
                    if (Protocol.TryHello(body, out string nonce, out int channel, out string root, out var known))
                    {
                        var player = Player.FindPlayer(__2, channel);
                        if (!player || player.PlayerChannel != channel) return false;
                        string key = __2.SteamID.m_SteamID + ":" + channel;
                        if (subscribers.TryGetValue(key, out var sub) && now - sub.HelloAt < 1) return false;
                        if (sub == null || sub.Nonce != nonce)
                        {
                            sub = new Subscriber { Key = key, Nonce = nonce, Peer = __2, Channel = channel };
                            subscribers[key] = sub;
                            instance?.LoggerInstance.Msg("Client subscribed: " + player.PlayerName + " (" + key + "), knows " + known.Count + " mods, root " + (root.Length == 0 ? "-" : root));
                        }
                        sub.Known = known; sub.Root = root; sub.HelloAt = now; sub.HelloPending = true;
                        return false;
                    }
                    if (Protocol.TryCommand(body, out nonce, out long seq, out string text))
                    {
                        // channel is not in the command; find the subscription for this peer with a matching nonce
                        var sub = subscribers.Values.FirstOrDefault(s => s.Peer.SteamID.m_SteamID == __2.SteamID.m_SteamID && s.Nonce == nonce);
                        if (sub == null) return false;
                        if (seq <= sub.LastSeq) return false;
                        sub.LastSeq = seq;
                        string error = Proxy.Validate(text);
                        if (error != null) { Send(sub, Doc.Reply(seq, text, null, error)); return false; }
                        if (sub.Pending.Count >= 4) { Send(sub, Doc.Reply(seq, text, null, "Too many pending commands.")); return false; }
                        sub.Pending.Enqueue(new KeyValuePair<long, string>(seq, text));
                        return false;
                    }
                    return false;
                }
                catch (Exception error) { if (ours) Report(error); return !ours; }
                finally { stream.Position = position; }
            }
        }

        public override void OnUpdate()
        {
            if (!enabled || !Game.GetIsServer() || Game.GetIsClient()) return;
            float now = Time.realtimeSinceStartup;
            if (deferred.Count > 0) RunDeferred();
            if (!frameLogged && PlayerMethods.PlayerCommands != null && AdminMethods.AdminCommands != null) { try { LogPublicFrame(); } catch (Exception error) { frameLogged = true; Report(error); } }
            foreach (var sub in subscribers.Values.ToArray())
            {
                var player = Player.FindPlayer(sub.Peer, sub.Channel);
                if (!player) { subscribers.Remove(sub.Key); continue; }
                try
                {
                    if (sub.HelloPending) { sub.HelloPending = false; PushFrame(sub, player, true); }
                    if (sub.Pending.Count > 0 && now - sub.LastCommandAt >= 0.2f)
                    {
                        var command = sub.Pending.Dequeue(); sub.LastCommandAt = now; commandsRun++;
                        var lines = Proxy.Execute(player, command.Value, out string error);
                        if (MenuConfig.Current.Debug) LoggerInstance.Msg("Command " + player.PlayerName + ": " + command.Value + " -> " + lines.Count + " lines" + (error == null ? "" : ", error " + error));
                        Send(sub, Doc.Reply(command.Key, command.Value, lines, error));
                        Frame.Invalidate(); PushFrame(sub, player, false);   // switches reflect the new state right behind the reply
                    }
                }
                catch (Exception error) { Report(error); }
            }
            if (now >= nextTick)
            {
                nextTick = now + 5;
                try
                {
                    if (MenuConfig.ReloadIfChanged()) LoggerInstance.Msg("menu.json reloaded.");
                    foreach (var sub in subscribers.Values.ToArray())
                    {
                        var player = Player.FindPlayer(sub.Peer, sub.Channel);
                        if (player) PushFrame(sub, player, false);
                    }
                    bool voting = ChatVotes.IsVoteInProgress();
                    if (voteWasInProgress && !voting) Broadcast(Doc.Event("vote-end", null));
                    voteWasInProgress = voting;
                }
                catch (Exception error) { Report(error); }
                if (now >= nextTrafficLog)
                {
                    nextTrafficLog = now + 60;
                    if (sentPackets > lastPackets)
                        LoggerInstance.Msg("Traffic last interval: packets=" + (sentPackets - lastPackets) + ", bodyBytes=" + (sentBytes - lastBytes) + ", clients=" + subscribers.Count + ", commands=" + commandsRun + ", dropped=" + droppedPackets + " (native framing excluded).");
                    lastPackets = sentPackets; lastBytes = sentBytes;
                }
            }
            Flush(now);
        }

        // Sends the frame diff only when this player's root hash differs from what the client already holds.
        private static void PushFrame(Subscriber sub, Player player, bool hello)
        {
            var frame = Frame.Build(player);
            if (frame.Root == sub.Root && !hello) return;
            bool same = frame.Root == sub.Root;
            string doc = Frame.Diff(frame, sub.Known, same);
            sub.Root = frame.Root;
            sub.Known = frame.Mods.Values.ToDictionary(m => m.Id, m => m.Hash);
            Send(sub, doc);
            if (!same) instance?.LoggerInstance.Msg("Frame -> " + player.PlayerName + ": root " + frame.Root + ", " + frame.Mods.Count + " mods" + (hello ? " (hello)" : " (changed)"));
        }
        internal static void Broadcast(string json) { foreach (var sub in subscribers.Values) Send(sub, json); }
        private static void Send(Subscriber sub, string json)
        {
            try { foreach (var chunk in Protocol.Transfer(sub.Nonce, json)) outbox.Enqueue(new KeyValuePair<string, string>(sub.Key, chunk)); }
            catch (Exception error) { Report(error); }
        }
        private static void Flush(float now)
        {
            tokens = Math.Min(TokenCap, tokens + (now - lastFlush) * TokensPerSecond); lastFlush = now;
            while (tokens >= 1 && outbox.Count > 0)
            {
                var item = outbox.Dequeue();
                if (!subscribers.TryGetValue(item.Key, out var sub)) { droppedPackets++; continue; }
                var player = Player.FindPlayer(sub.Peer, sub.Channel);
                if (!player) { droppedPackets++; continue; }
                var writer = GameByteStreamWriter.GetGameByteStreamWriter(0, "Si_ModUI::Send", true);
                writer.WriteByte(Protocol.PacketType); writer.WriteString(Protocol.Marker); writer.WriteString(item.Value);
                if (NetworkLayer.SendPacketToPlayer(writer, ENetworkPacketSend.Reliable, player)) { sentPackets++; sentBytes += Encoding.UTF8.GetByteCount(item.Value); }
                else droppedPackets++;
                tokens--;
            }
        }
        private static void Report(Exception error)
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextError) return;
            nextError = now + 10;
            instance?.LoggerInstance.Warning(error.GetBaseException().Message);
        }
    }

    internal static class Doc
    {
        internal static string Reply(long seq, string cmd, List<string> lines, string error) =>
            JsonConvert.SerializeObject(new { k = "reply", seq, cmd, lines = lines ?? new List<string>(), err = error ?? "" }, Formatting.None);
        internal static string Event(string name, object data) =>
            JsonConvert.SerializeObject(new { k = "event", e = name, data }, Formatting.None);
    }
}
