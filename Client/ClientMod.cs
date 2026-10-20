using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(SiModUI.ClientMod), "Si_ModUI", "1.2.0", "DrMuck, Davey")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]
[assembly: MelonPlatform(MelonPlatformAttribute.CompatiblePlatforms.WINDOWS_X64)]
[assembly: MelonPlatformDomain(MelonPlatformDomainAttribute.CompatibleDomains.IL2CPP)]

namespace SiModUI;

// Client: holds the frame (cached on disk per server), sends commands, renders replies. Never polls.
public sealed partial class ClientMod : MelonMod
{
    public sealed class Cmd
    {
        [JsonPropertyName("t")] public string Text { get; set; } = "";
        [JsonPropertyName("l")] public string Label { get; set; } = "";
        [JsonPropertyName("d")] public string Desc { get; set; } = "";
        [JsonPropertyName("a")] public bool Admin { get; set; }
        [JsonPropertyName("p")] public string Power { get; set; } = "";
        [JsonPropertyName("x")] public List<string> Aliases { get; set; } = new();
        [JsonPropertyName("arg")] public string Arg { get; set; } = "";
        [JsonPropertyName("ch")] public List<string[]> Choices { get; set; } = new();
        [JsonPropertyName("o")] public int Order { get; set; } = 1000;
        [JsonPropertyName("v")] public string Value { get; set; }   // switch state (with Choices): rendered as one button per choice
    }
    public sealed class Mod
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("ver")] public string Version { get; set; } = "";
        [JsonPropertyName("cat")] public string Category { get; set; } = "player";
        [JsonPropertyName("prio")] public int Priority { get; set; }
        [JsonPropertyName("nav")] public string Nav { get; set; } = "/";
        [JsonPropertyName("hash")] public string Hash { get; set; } = "";
        [JsonPropertyName("cmds")] public List<Cmd> Cmds { get; set; } = new();
        [JsonPropertyName("data")] public JsonElement? Data { get; set; }   // mod-published document, e.g. the /buy catalog
    }
    public sealed class FrameDoc
    {
        [JsonPropertyName("k")] public string Kind { get; set; } = "";
        [JsonPropertyName("root")] public string Root { get; set; } = "";
        [JsonPropertyName("same")] public bool Same { get; set; }
        [JsonPropertyName("full")] public bool Full { get; set; }
        [JsonPropertyName("cats")] public Dictionary<string, string> Cats { get; set; }
        [JsonPropertyName("mods")] public List<Mod> Mods { get; set; } = new();
        [JsonPropertyName("removed")] public List<string> Removed { get; set; } = new();
    }
    public sealed class ReplyDoc
    {
        [JsonPropertyName("k")] public string Kind { get; set; } = "";
        [JsonPropertyName("seq")] public long Seq { get; set; }
        [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
        [JsonPropertyName("lines")] public List<string> Lines { get; set; } = new();
        [JsonPropertyName("err")] public string Error { get; set; } = "";
    }
    public sealed class StoredFrame
    {
        public string Root { get; set; } = "";
        public Dictionary<string, string> Cats { get; set; } = new();
        public List<Mod> Mods { get; set; } = new();
    }
    private sealed class Transfer { public bool Gzip; public int Count; public double At; public readonly Dictionary<int, byte[]> Parts = new(); }

    private static ClientMod instance;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    private StreamWriter log;
    private bool networkEnabled, keyboardEnabled = true, wasConnected;
    private double nextPoll, nextError, helloAt = -100, pendingAt;
    private int helloTries;
    private string nonce = Protocol.NewNonce(), context = "", serverId = "", framePath = "";
    private StoredFrame frame = new();
    private bool frameReady, serverSilent;
    private readonly Dictionary<string, Transfer> transfers = new();
    private long seq, pendingSeq = -1;
    private string pendingText = "", status = "";
    private ReplyDoc reply;
    private Mod replyMod;
    private string voteQuestion = "";
    private List<KeyValuePair<string, string>> voteOptions = new();
    private double voteUntil;

    public override void OnInitializeMelon()
    {
        instance = this;
        networkEnabled = !Enum.GetValues(typeof(ENetworkPacketType)).Cast<object>().Any(v => Convert.ToInt32(v) == Protocol.PacketType);
        try
        {
            var folder = Path.Combine(MelonEnvironment.UserDataDirectory, "Si_ModUI");
            string legacy = Path.Combine(MelonEnvironment.UserDataDirectory, "Si_SquadHUD");
            if (!Directory.Exists(folder) && Directory.Exists(legacy)) Directory.Move(legacy, folder);   // pre-rename folder, once
            Directory.CreateDirectory(folder);
            log = new StreamWriter(new FileStream(Path.Combine(folder, $"session-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception error) { LoggerInstance.Warning("Session log unavailable: " + error.Message); }
        var prefs = MelonPreferences.CreateCategory("Si_ModUI");
        fontPreference = prefs.CreateEntry("FontSize", 17);
        Write("READY - F6 opens the mod menu. Marker " + Protocol.Marker + (networkEnabled ? "" : " (transport disabled: packet 126 is defined by this game build)"));
    }
    public override void OnDeinitializeMelon() { ClosePanel(); ResetAppearance(); networkEnabled = false; log?.Dispose(); log = null; instance = null; }
    public override void OnSceneWasLoaded(int buildIndex, string name) { ClosePanel(); ResetAppearance(); ResetConnection("scene " + name); }

    // ---- transport ----
    [HarmonyPatch(typeof(NetworkLayer), "ProcessMessage")]
    private static class Receive
    {
        private static bool Prefix(GameByteStreamReader __0, uint __1, NetworkID __2, bool __3)
        {
            if (instance == null || !instance.networkEnabled || __3 || !Game.GetIsClient() || __0 == null || __1 < 2) return true;
            var stream = __0.m_MemoryStream;
            long position = stream.Position;
            bool ours = false;
            try
            {
                if (__0.ReadByte() != Protocol.PacketType || __1 > Protocol.MaxPacket) return true;
                if (__1 - stream.Position < Protocol.Marker.Length + 2 || !Protocol.ReadMarker(stream.ReadByte, (byte)EGameByteStreamType.String)) return true;
                ours = true;
                if (!NetworkGameClient.GetInServer() || __2.SteamID.m_SteamID != NetworkGameClient.GetServerID().SteamID.m_SteamID) return false;
                string body = __0.ReadString();
                if (stream.Position > __1) return false;
                if (Protocol.TryTransfer(body, out var chunk)) instance.ReceiveChunk(chunk);
                return false;
            }
            catch (Exception error) { if (ours) instance.Error(error); return !ours; }
            finally { stream.Position = position; }
        }
    }
    private void ReceiveChunk(Chunk chunk)
    {
        if (chunk.Nonce != nonce) return;
        double now = clock.Elapsed.TotalSeconds;
        foreach (var stale in transfers.Where(t => now - t.Value.At > 30).Select(t => t.Key).ToArray()) transfers.Remove(stale);
        if (!transfers.TryGetValue(chunk.Id, out var transfer))
        {
            if (transfers.Count >= 8) return;   // at most 8 documents in flight (~4 MB of chunks); more is not a well-behaved server
            transfers[chunk.Id] = transfer = new Transfer { Gzip = chunk.Gzip, Count = chunk.Count, At = now };
        }
        if (transfer.Count != chunk.Count || transfer.Gzip != chunk.Gzip) return;
        transfer.Parts[chunk.Index] = chunk.Bytes;
        if (transfer.Parts.Count < transfer.Count) return;
        transfers.Remove(chunk.Id);
        string document = Protocol.Assemble(Enumerable.Range(0, transfer.Count).Select(i => transfer.Parts[i]).ToList(), transfer.Gzip);
        HandleDocument(document);
    }
    private void HandleDocument(string document)
    {
        string kind;
        using (var parsed = JsonDocument.Parse(document)) kind = parsed.RootElement.TryGetProperty("k", out var k) ? k.GetString() ?? "" : "";
        switch (kind)
        {
            case "frame": ApplyFrame(JsonSerializer.Deserialize<FrameDoc>(document, json)); break;
            case "reply":
                var r = JsonSerializer.Deserialize<ReplyDoc>(document, json);
                if (r == null || r.Seq != pendingSeq) return;
                pendingSeq = -1;
                Write("REPLY " + r.Cmd + ": " + r.Lines.Count + " lines" + (r.Error.Length > 0 ? ", error " + r.Error : "") + (pendingSilent ? " (auto-close)" : pendingInline ? " (inline)" : ""));
                ScanCredits(r.Lines);
                if (pendingSilent) { PumpOutgoing(); return; }
                status = r.Error.Length > 0 ? "Error: " + r.Error : r.Lines.Count == 0 ? "The server mod printed nothing for " + r.Cmd + "." : "";
                if (pendingInline) { inlineReply = r; reply = null; PumpOutgoing(); break; }
                reply = r; replyScroll = Vector2.zero;
                for (int i = 0; i < Math.Min(4, r.Lines.Count); i++) Write("  line " + i + ": " + Plain(r.Lines[i]).Trim());
                PumpOutgoing();
                break;
            case "event":
                using (var parsed = JsonDocument.Parse(document))
                {
                    string name = parsed.RootElement.TryGetProperty("e", out var e) ? e.GetString() ?? "" : "";
                    if (name == "vote" && parsed.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        voteQuestion = Plain(data.TryGetProperty("q", out var q) ? q.GetString() ?? "" : "Vote");
                        voteOptions = new List<KeyValuePair<string, string>>();
                        if (data.TryGetProperty("opts", out var opts) && opts.ValueKind == JsonValueKind.Array)
                            foreach (var o in opts.EnumerateArray()) voteOptions.Add(new KeyValuePair<string, string>(o.GetProperty("c").GetString() ?? "", Plain(o.GetProperty("d").GetString() ?? "")));
                        int dur = data.TryGetProperty("dur", out var d) && d.TryGetInt32(out int seconds) ? seconds : 30;
                        voteUntil = clock.Elapsed.TotalSeconds + dur + 2;
                        Write("VOTE opened: " + voteQuestion + " (" + voteOptions.Count + " options, " + dur + " s)");
                    }
                    else if (name == "vote-end") { voteOptions.Clear(); voteQuestion = ""; Write("VOTE ended"); }
                }
                break;
            case "rehello": helloAt = -100; helloTries = 0; frameReady = false; break;
        }
    }
    private void ApplyFrame(FrameDoc doc)
    {
        if (doc == null) return;
        if (doc.Same && frame.Root == doc.Root) { frameReady = true; Write("FRAME unchanged (root " + doc.Root + ", " + frame.Mods.Count + " mods)"); return; }
        if (doc.Full || frame.Mods.Count == 0) frame.Mods = new List<Mod>();
        var byId = new Dictionary<string, Mod>();
        foreach (var existing in frame.Mods) byId[existing.Id] = existing;
        foreach (var removed in doc.Removed) byId.Remove(removed);
        foreach (var mod in doc.Mods) byId[mod.Id] = mod;
        frame.Mods = byId.Values.OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        frame.Root = doc.Root;
        if (doc.Cats != null && doc.Cats.Count > 0) frame.Cats = doc.Cats;
        frameReady = true;
        if (selectedMod != null && !byId.ContainsKey(selectedMod.Id)) selectedMod = null;
        else if (selectedMod != null) selectedMod = byId[selectedMod.Id];
        optimistic.Clear();
        SaveFrame();
        Write("FRAME " + (doc.Full ? "full" : "diff") + ": root " + doc.Root + ", +" + doc.Mods.Count + " -" + doc.Removed.Count + " -> " + frame.Mods.Count + " mods, " + frame.Mods.Sum(m => m.Cmds.Count) + " commands");
    }
    private void SendBody(string body)
    {
        var writer = GameByteStreamWriter.GetGameByteStreamWriter(0, "Si_ModUI::Send", true);
        writer.WriteByte(Protocol.PacketType); writer.WriteString(Protocol.Marker); writer.WriteString(body);
        NetworkLayer.SendPacket(writer, ENetworkPacketSend.Reliable);
    }
    private void SendHello(Player player)
    {
        helloAt = clock.Elapsed.TotalSeconds; helloTries++;
        var known = frame.Mods.Select(m => new KeyValuePair<string, string>(m.Id, m.Hash));
        SendBody(Protocol.Hello(nonce, player.PlayerChannel, frame.Root, known));
        Write("HELLO " + helloTries + ": root " + (frame.Root.Length == 0 ? "-" : frame.Root) + ", " + frame.Mods.Count + " known mods");
    }
    // Commands go out one at a time; the next one is sent when the reply (or an 8 s timeout) arrives.
    private sealed class Outgoing { public string Text; public Mod Mod; public bool Silent, Inline; }
    private readonly Queue<Outgoing> outgoing = new();
    private bool pendingSilent, pendingInline;
    private Mod openMenuMod;   // the mod whose chat menu we last opened on the server (menus are exclusive per player)
    // Inline commands (catalog purchases, balance refresh) show their reply under the catalog instead of switching to the reply page.
    private ReplyDoc inlineReply;
    private int credits = -1;   // last balance seen in any reply; -1 = unknown
    private static readonly System.Text.RegularExpressions.Regex CreditsPattern = new(@"(?:Credits|Balance|Remaining):\s*(\d+)|you have (\d+)\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private void ScanCredits(List<string> lines)
    {
        foreach (var line in lines)
        {
            var m = CreditsPattern.Match(Plain(line));
            if (!m.Success) continue;
            string value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c)) credits = c;
        }
    }
    private void SendCommand(string text, Mod mod, bool silent = false, bool inline = false)
    {
        if (!networkEnabled || !frameReady || !Protocol.ValidCommandText(text) || outgoing.Count >= 4) return;
        outgoing.Enqueue(new Outgoing { Text = text, Mod = mod, Silent = silent, Inline = inline });
        if (!silent && !inline)   // inline commands (direct purchases, balance) never open a chat menu on the server
        {
            string bare = text.Trim().TrimStart('/', '!', '.').Split(' ')[0].ToLowerInvariant();
            openMenuMod = bare == "0" || bare == "exit" || bare == "close" ? null : mod ?? openMenuMod;
        }
        PumpOutgoing();
    }
    // Before using another mod (or leaving the page), close the previous mod's chat menu on the server.
    private void AutoClose(Mod next)
    {
        if (openMenuMod == null || openMenuMod == next) return;
        var previous = openMenuMod; openMenuMod = null;
        if (networkEnabled && frameReady && wasConnected) SendCommand(previous.Nav + "0", previous, true);
    }
    private void PumpOutgoing()
    {
        if (pendingSeq >= 0 || outgoing.Count == 0) return;
        var next = outgoing.Dequeue();
        pendingSeq = ++seq; pendingText = next.Text; pendingSilent = next.Silent; pendingInline = next.Inline; pendingAt = clock.Elapsed.TotalSeconds;
        if (!next.Silent) { replyMod = next.Mod ?? replyMod; status = "Waiting for " + next.Text + " ..."; }
        try { SendBody(Protocol.Command(nonce, pendingSeq, next.Text)); Write("SEND " + next.Text + (next.Silent ? " (auto-close)" : "")); }
        catch (Exception error) { pendingSeq = -1; status = "Could not send."; Error(error); }
    }

    // ---- connection lifecycle ----
    private void ResetConnection(string why)
    {
        nonce = Protocol.NewNonce(); context = ""; frameReady = false; helloTries = 0; helloAt = -100; transfers.Clear();
        pendingSeq = -1; reply = null; inlineReply = null; credits = -1; replyMod = null; openMenuMod = null; outgoing.Clear(); voteOptions.Clear(); voteQuestion = ""; serverSilent = false; status = "";
        Write("Connection reset: " + why);
    }
    private void LoadFrame(string server)
    {
        serverId = server; frame = new StoredFrame();
        framePath = Path.Combine(MelonEnvironment.UserDataDirectory, "Si_ModUI", "frame-" + server + ".json");
        try { if (File.Exists(framePath)) frame = JsonSerializer.Deserialize<StoredFrame>(File.ReadAllText(framePath), json) ?? new StoredFrame(); }
        catch (Exception error) { Error(error); frame = new StoredFrame(); }
        frame.Mods ??= new(); frame.Cats ??= new(); frame.Root ??= "";
        if (!Protocol.ValidHash(frame.Root)) { frame.Root = ""; frame.Mods.Clear(); }
    }
    private void SaveFrame()
    {
        if (framePath.Length == 0) return;
        try { File.WriteAllText(framePath, JsonSerializer.Serialize(frame)); } catch (Exception error) { Error(error); }
    }

    public override void OnUpdate()
    {
        if (keyboardEnabled)
        {
            try
            {
                var keyboard = Keyboard.current;
                if (keyboard != null && Application.isFocused && keyboard.f6Key.wasPressedThisFrame && !NativeTextInput()) TogglePanel();
            }
            catch (Exception error) { keyboardEnabled = false; Error(error); }
        }
        double now = clock.Elapsed.TotalSeconds;
        if (now < nextPoll) return;
        nextPoll = now + 0.25;
        try
        {
            var player = Player.CurrentPlayer;
            bool connected = Game.GetIsClient() && !Game.GetIsServer() && NetworkGameClient.GetInServer() && player != null;
            if (!connected)
            {
                if (wasConnected) { ClosePanel(); ResetConnection("disconnected"); }
                wasConnected = false; return;
            }
            wasConnected = true;
            // The server's Steam ID is a fresh anonymous game-server login after every restart; its configured name is stable.
            string server = ServerKey();
            string next = server + ":" + NetworkGameClient.GetServerID().SteamID.m_SteamID.ToString(CultureInfo.InvariantCulture) + ":" + player.GetInstanceID();
            if (next != context) { ResetConnection("server/player changed"); context = next; LoadFrame(server); }
            if (networkEnabled && !frameReady && now - helloAt >= (helloTries == 0 ? 2 : 8))
            {
                if (helloTries < 6) SendHello(player);
                else if (!serverSilent) { serverSilent = true; status = "No answer from the server: the HUD server mod is not installed there."; Write(status); }
            }
            if (pendingSeq >= 0 && now - pendingAt > 8) { pendingSeq = -1; if (!pendingSilent) status = "No reply from the server for " + pendingText + "."; PumpOutgoing(); }
            if (voteOptions.Count > 0 && now > voteUntil) { voteOptions.Clear(); voteQuestion = ""; }
        }
        catch (Exception error) { Error(error); }
    }

    private static string ServerKey()
    {
        string name = "";
        try { name = Plain(NetworkGameClient.GetServerName() ?? "").Trim(); } catch { }
        if (name.Length == 0) return "sid-" + NetworkGameClient.GetServerID().SteamID.m_SteamID.ToString(CultureInfo.InvariantCulture);
        var safe = new StringBuilder();
        foreach (char c in name) safe.Append(char.IsLetterOrDigit(c) ? c : '_');
        return safe.ToString().Length > 48 ? safe.ToString().Substring(0, 48) : safe.ToString();
    }
    internal static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "");
    private void Error(Exception error)
    {
        if (clock.Elapsed.TotalSeconds < nextError) return;
        nextError = clock.Elapsed.TotalSeconds + 10;
        Write("ERROR " + error);
    }
    private void Write(string text)
    {
        var line = DateTime.UtcNow.ToString("o") + " " + text.Replace('\r', ' ').Replace('\n', ' ');
        LoggerInstance.Msg(line);
        if (log == null) return;
        try { log.WriteLine(line); } catch { try { log.Dispose(); } catch { } log = null; }
    }
}
