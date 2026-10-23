using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarmonyLib;
using Il2Cpp;
using Il2CppDebugTools;
using Il2CppSilica;
using Il2CppSilica.UI;
using Il2CppSilica.UI.Menus;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace SiModUI;

// IMGUI rendering: one draggable menu window (Player / Admin / Info), reply pages, vote panel.
public sealed partial class ClientMod
{
    private sealed class WindowPos { public float X { get; set; } = .5f; public float Y { get; set; } = .5f; public float Width { get; set; } public float Height { get; set; } }
    // ModOrder: mod ids in the order the user dragged them, per "<server>|<tab>"; mods not listed follow in server order.
    private sealed class Layout { public Dictionary<string, WindowPos> Windows { get; set; } = new(); public Dictionary<string, List<string>> ModOrder { get; set; } = new(); }
    // Option parsing. Inline forms "/1 Commanders /2 FPS players /0 close" (several per line) and line forms
    // "1. Sol", "[BAL] 2) Centauri", "[3] Alien". Leading [TAG] prefixes are ignored.
    private sealed class Opt { public string Send, Label, Value; public bool Param; }   // Param: "name = value" row that takes a value
    private sealed class Row { public string Line, Lead = ""; public List<Opt> Opts = new(); public float Height; }
    private static readonly Regex Tag = new(@"^\s*(?:\[[^\]]{1,16}\]\s*)+", RegexOptions.Compiled);
    // A token is "/1", ".1", "!1" (explicit prefix) or "1." / "1)" followed by whitespace (plain numbering).
    private static readonly Regex Token = new(@"(?<![\w/.!])(?:([/.!])(\d{1,3})\b|(\d{1,3})[.)](?=\s))", RegexOptions.Compiled);
    private static readonly Regex Parens = new(@"\([^)]*\)", RegexOptions.Compiled);
    private Row ParseRow(string line, string nav)
    {
        var row = new Row { Line = line };
        string plain = Tag.Replace(Plain(line), "").Trim();
        // Hints inside parentheses such as "(.1 .. or !b 1)" are never options: mask them for tokenizing only.
        var maskBuilder = new System.Text.StringBuilder(plain);
        foreach (Match paren in Parens.Matches(plain)) for (int k = 0; k < paren.Length; k++) maskBuilder[paren.Index + k] = ' ';
        string masked = maskBuilder.ToString();
        var tokens = Token.Matches(masked);
        if (tokens.Count == 0 || (tokens.Count == 1 && tokens[0].Index > 0)) { ParseUsage(row, plain); return row; }
        row.Lead = plain.Substring(0, tokens[0].Index).Trim();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            int start = t.Index + t.Length, end = i + 1 < tokens.Count ? tokens[i + 1].Index : plain.Length;
            string label = plain.Substring(start, end - start).Trim().TrimEnd(',', ';', '|').Trim();
            string send = t.Groups[1].Success ? t.Groups[1].Value + t.Groups[2].Value : nav + t.Groups[3].Value;
            string shown = t.Groups[1].Success ? t.Groups[1].Value + t.Groups[2].Value : t.Groups[3].Value + ".";
            var opt = new Opt { Send = send, Label = shown + "  " + label };
            int eq = label.IndexOf(" = ", StringComparison.Ordinal);
            if (eq > 0)
            {
                opt.Param = true;
                string value = label.Substring(eq + 3).Trim();
                int cut = value.IndexOf(" (", StringComparison.Ordinal); if (cut >= 0) value = value.Substring(0, cut).Trim();
                opt.Value = value == "-" ? "" : value;
            }
            row.Opts.Add(opt);
        }
        return row;
    }
    // A usage / help line ("!elo | !elo top | !elo <name>", "usage: /rtsai [on|off|auto | status | mil | config [name]]",
    // "/logistics on|off, bonus on|off, share <0..1>") becomes buttons for its plain alternatives: the first command token
    // is the root, every alternative that is one bare word (or the root alone) sends "root word". Pieces with
    // placeholders or several words stay text; they need the Arguments field.
    private static readonly Regex RootToken = new(@"(?<![\w/.!])([/!.][A-Za-z][\w-]*)", RegexOptions.Compiled);
    private static readonly Regex BareWord = new(@"^[A-Za-z][\w-]*$", RegexOptions.Compiled);
    private void ParseUsage(Row row, string plain)
    {
        if (plain.IndexOf('|') < 0 && plain.IndexOf(',') < 0) return;
        var root = RootToken.Match(plain);
        if (!root.Success) return;
        string command = root.Groups[1].Value;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in plain.Replace("[", " ").Replace("]", " ").Split('|', ','))
        {
            string piece = raw.Trim().TrimEnd('.', ';').Trim();
            if (piece.StartsWith(command, StringComparison.OrdinalIgnoreCase)) piece = piece.Substring(command.Length).Trim();
            if (piece.Length > 0 && piece.IndexOf(' ') < 0 && piece.IndexOf('<') < 0 && piece[0] != '/' && piece[0] != '!' && BareWord.IsMatch(piece) && seen.Add(piece))
                row.Opts.Add(new Opt { Send = command + " " + piece, Label = piece });
        }
        if (row.Opts.Count < 2) { row.Opts.Clear(); return; }
        row.Lead = plain.Length > 70 ? plain.Substring(0, 70).Trim() + "..." : plain;
    }
    private float OptionWidth(Opt o, float max) => Math.Min(max, Math.Max(90, bodyStyle.CalcSize(new GUIContent(o.Label)).x + 28));
    private float MeasureRow(Row row, float width)
    {
        if (row.Opts.Count == 0) return Math.Max(24f, lineStyle.CalcHeight(new GUIContent(row.Line), width - 12) + 4);
        if (row.Opts.Count == 1 && row.Opts[0].Param) return 40;
        float h = row.Lead.Length > 0 ? 26 : 0, x = 0; int lines = 1;
        foreach (var o in row.Opts) { float w = OptionWidth(o, width - 8); if (x > 0 && x + w > width - 8) { lines++; x = 0; } x += w + 8; }
        return h + lines * 38 + 4;
    }
    private Rect voteRect;

    private MelonPreferences_Entry<int> fontPreference;
    private GUIStyle bodyStyle, lineStyle, titleStyle, smallStyle, buttonStyle, tabStyle, inputStyle, surfaceStyle;
    private Texture2D surfaceTexture, inputTexture;
    private Color accent = new(.5f, .85f, 1f, 1f), surface = new(.075f, .095f, .12f, .94f);
    private int appearanceTeam = int.MinValue, appearanceFont;
    private bool panelOpen, drawingEnabled = true;
    private double nextDrawRetry, releaseUntil;
    private CursorLockMode priorLock; private bool priorCursor;
    private string category = "player", arguments = "";
    private Mod selectedMod;
    private Vector2 modScroll, commandScroll, replyScroll, infoScroll;
    private Layout layout = new(); private string layoutPath = "", layoutProfile = "", movingWindow = ""; private bool layoutDirty, resizingWindow; private int dragControl;

    private static bool NativeTextInput() => UIManager.TextInputActive || DebugConsole.IsOpen;
    // Only the pause menu (InGameMenu with its settings screens) and the main menu block the panel. Every other native
    // menu of a running match (the battle-layout vote, the faction / role screen, the large map, the end screen) stays
    // open for long stretches in which commander pins and votes matter, so the panel opens on top of it and the game UI
    // underneath gets no clicks while it is up (BlockNativeClicks).
    private static bool NativeMenu() => Game.IsPauseMenuActive || Game.InMainMenu;
    private static string OverlayName()
    {
        try { var current = MenuManager.Instance == null ? null : MenuManager.Instance.GetCurrent(); return current == null ? "" : current.GetIl2CppType().Name; }
        catch { return "?"; }
    }
    // While the panel sits on a native menu the game's own UI must not receive the clicks: the EventSystem is disabled.
    // A disabled EventSystem is no longer EventSystem.current, so the instance is kept to switch it back on.
    private UnityEngine.EventSystems.EventSystem pausedEventSystem;
    private void BlockNativeClicks(bool block)
    {
        try
        {
            if (block)
            {
                if (pausedEventSystem != null || OverlayName().Length == 0) return;
                var system = UnityEngine.EventSystems.EventSystem.current;
                if (system == null) return;
                system.enabled = false; pausedEventSystem = system;
            }
            else if (pausedEventSystem != null)
            {
                try { pausedEventSystem.enabled = true; } catch { }
                pausedEventSystem = null;
            }
        }
        catch (Exception error) { pausedEventSystem = null; Error(error); }
    }
    private bool BlocksGameplay => panelOpen || clock.Elapsed.TotalSeconds < releaseUntil;

    private void TogglePanel()
    {
        if (panelOpen) { ClosePanel(); return; }
        if (!drawingEnabled) { ResetAppearance(); drawingEnabled = true; nextDrawRetry = 0; }
        var p = Player.CurrentPlayer;
        string blocked = NativeMenu() ? "native menu " + OverlayName() + (Game.IsPauseMenuActive ? " (pause)" : "") : NativeTextInput() ? "text input" : p == null ? "no local player"
            : !NetworkGameClient.GetInServer() ? "not in a server" : Game.GetIsServer() ? "this is the server" : null;
        if (blocked != null) { Write("F6 ignored: " + blocked + "."); return; }
        priorCursor = Cursor.visible; priorLock = Cursor.lockState;
        panelOpen = true; status = "";
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        GameInput.UpdateInput();
        BlockNativeClicks(true);
        if (!frameReady && networkEnabled && helloTries < 6) helloAt = -100;   // hello right away on open
        Write("Menu opened" + (OverlayName().Length > 0 ? " over " + OverlayName() : "") + ".");
    }
    private void ClosePanel()
    {
        EndWindowDrag();
        BlockNativeClicks(false);
        if (!panelOpen) return;
        AutoClose(null);
        panelOpen = false; releaseUntil = clock.Elapsed.TotalSeconds + .2;
        try
        {
            if (!Application.isFocused) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            else if (!NativeMenu() && !NativeTextInput())
            {
                bool show = GameMode.CurrentGameMode == null ? priorCursor : GameMode.CurrentGameMode.GetShowCursor();
                Cursor.visible = show; Cursor.lockState = show ? CursorLockMode.None : priorLock;
            }
            GameInput.UpdateInput();
        }
        catch { }
    }
    public override void OnLateUpdate()
    {
        try
        {
            if (!panelOpen) return;
            if (!Application.isFocused || NativeMenu() || NativeTextInput()) { ClosePanel(); return; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            // the faction screen may open or close underneath the panel (deploy, round start): keep the click block in step
            if (OverlayName().Length > 0) BlockNativeClicks(true); else BlockNativeClicks(false);
        }
        catch (Exception error) { ClosePanel(); Error(error); }
    }

    [HarmonyPatch(typeof(MenuManager), nameof(MenuManager.IsAnyMenuOpened))]
    private static class MenuFocus { private static void Postfix(ref bool __result) { if (instance?.BlocksGameplay == true) __result = true; } }
    [HarmonyPatch(typeof(GameInput), nameof(GameInput.GetPreferredInput))]
    private static class InputFocus { private static bool Prefix(ref GameInput.InputMap __result) { if (instance?.BlocksGameplay != true) return true; __result = GameInput.InputMap.UI; return false; } }
    [HarmonyPatch(typeof(Game), nameof(Game.GetControlsEnabled))]
    private static class ControlFocus { private static bool Prefix(ref bool __result) { if (instance?.BlocksGameplay != true) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(Game), nameof(Game.GetCameraEnabled))]
    private static class CameraFocus { private static bool Prefix(ref bool __result) { if (instance?.BlocksGameplay != true) return true; __result = false; return false; } }
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnEscape))]
    private static class EscapeFocus { private static bool Prefix() { if (instance?.panelOpen != true) return true; instance.ClosePanel(); return false; } }

    // ---- appearance ----
    private void ResetAppearance()
    {
        if (surfaceTexture != null) UnityEngine.Object.Destroy(surfaceTexture);
        if (inputTexture != null) UnityEngine.Object.Destroy(inputTexture);
        surfaceTexture = inputTexture = null; bodyStyle = lineStyle = titleStyle = smallStyle = buttonStyle = tabStyle = inputStyle = surfaceStyle = null; appearanceTeam = int.MinValue;
    }
    private void Appearance()
    {
        var p = Player.CurrentPlayer; var team = p == null ? null : p.Team;
        int id = team == null ? -1 : team.Index;
        int font = Math.Clamp(fontPreference?.Value ?? 17, 14, 26);
        if (bodyStyle != null && id == appearanceTeam && font == appearanceFont) return;
        ResetAppearance(); appearanceTeam = id; appearanceFont = font;
        string name = team == null ? "" : team.name;
        accent = team == null ? new Color(.5f, .85f, 1f, 1) : Color.Lerp(team.TeamColor, Color.white, .15f); accent.a = 1;
        surface = name == "Team_Alien" ? new Color(.075f, .08f, .025f, .95f) : name == "Team_Human_Centauri" ? new Color(.13f, .035f, .04f, .95f) : new Color(.075f, .095f, .12f, .95f);
        surfaceTexture = Solid(Color.white); inputTexture = Solid(new Color(surface.r * .5f, surface.g * .5f, surface.b * .5f, 1));
        surfaceStyle = new GUIStyle(); surfaceStyle.normal.background = surfaceTexture;
        bodyStyle = Text(font); lineStyle = Text(font); lineStyle.richText = true; lineStyle.wordWrap = true;
        titleStyle = Text(font + 3); titleStyle.fontStyle = FontStyle.Bold; titleStyle.normal.textColor = new Color(1f, .97f, .87f, 1);
        smallStyle = Text(Math.Max(13, font - 3)); smallStyle.normal.textColor = new Color(.8f, .82f, .8f, 1);
        buttonStyle = Text(font); buttonStyle.alignment = TextAnchor.MiddleLeft; buttonStyle.padding = new RectOffset(10, 8, 4, 4); buttonStyle.wordWrap = false;
        buttonStyle.normal.background = buttonStyle.hover.background = buttonStyle.active.background = buttonStyle.focused.background = surfaceTexture;
        buttonStyle.hover.textColor = buttonStyle.active.textColor = buttonStyle.focused.textColor = Color.white;
        tabStyle = Text(font); tabStyle.alignment = TextAnchor.MiddleCenter; tabStyle.fontStyle = FontStyle.Bold; tabStyle.padding = new RectOffset(6, 6, 4, 4); tabStyle.wordWrap = false; tabStyle.normal.background = tabStyle.hover.background = tabStyle.active.background = tabStyle.focused.background = surfaceTexture; tabStyle.hover.textColor = tabStyle.active.textColor = tabStyle.focused.textColor = Color.white;
        inputStyle = Text(font); inputStyle.alignment = TextAnchor.MiddleLeft; inputStyle.padding = new RectOffset(10, 8, 4, 4); inputStyle.wordWrap = false;
        inputStyle.normal.background = inputStyle.hover.background = inputStyle.active.background = inputStyle.focused.background = inputTexture;
        inputStyle.normal.textColor = inputStyle.hover.textColor = inputStyle.active.textColor = inputStyle.focused.textColor = Color.white;
    }
    private static Texture2D Solid(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = UnityEngine.HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color); texture.Apply(); return texture;
    }
    private GUIStyle Text(int size)
    {
        var style = new GUIStyle { font = GUI.skin.font, fontSize = size, wordWrap = true, richText = false };
        style.normal.textColor = new Color(.98f, .98f, .95f, 1); return style;
    }
    private void Surface(Rect rect, Color color) { var old = GUI.color; try { GUI.color = color; GUI.Box(rect, "", surfaceStyle); } finally { GUI.color = old; } }
    private bool Button(Rect rect, string text, bool selected = false, bool allowed = true, GUIStyle style = null)
    {
        var oldBackground = GUI.color; bool oldEnabled = GUI.enabled;
        try
        {
            bool hover = rect.Contains(Event.current.mousePosition);
            GUI.enabled = oldEnabled && allowed;
            GUI.color = Color.Lerp(surface, accent, selected ? .35f : hover ? .22f : .1f); GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, 1);
            return GUI.Button(rect, text, style ?? buttonStyle) && allowed;
        }
        finally { GUI.color = oldBackground; GUI.enabled = oldEnabled; }
    }

    // ---- window placement (per Steam user, local only) ----
    private void EnsureLayout()
    {
        var p = Player.CurrentPlayer; if (p == null) return;
        string id = p.PlayerID.SteamID.m_SteamID.ToString(CultureInfo.InvariantCulture);
        if (id == layoutProfile) return;
        EndWindowDrag(); layoutProfile = id; layout = new Layout();
        layoutPath = Path.Combine(MelonEnvironment.UserDataDirectory, "Si_ModUI", "layout-" + id + ".json");
        try { if (File.Exists(layoutPath)) layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(layoutPath)) ?? new Layout(); } catch (Exception error) { Error(error); layout = new Layout(); }
        layout.Windows ??= new(); layout.ModOrder ??= new();
    }
    private Rect LayoutRect(string key, float width, float height, float x = .5f, float y = .5f, float minWidth = 300, float minHeight = 160)
    {
        EnsureLayout();
        if (!layout.Windows.TryGetValue(key, out var w) || w == null) { w = new WindowPos { X = x, Y = y }; layout.Windows[key] = w; layoutDirty = true; }
        if (!float.IsFinite(w.X) || !float.IsFinite(w.Y) || !float.IsFinite(w.Width) || !float.IsFinite(w.Height)) w = layout.Windows[key] = new WindowPos { X = x, Y = y };
        width = Math.Clamp(w.Width > 0 ? w.Width : width, Math.Min(minWidth, Screen.width - 24), Screen.width - 24);
        height = Math.Clamp(w.Height > 0 ? w.Height : height, Math.Min(minHeight, Screen.height - 24), Screen.height - 24);
        return new Rect(12 + Math.Max(0, Screen.width - width - 24) * Math.Clamp(w.X, 0, 1), 12 + Math.Max(0, Screen.height - height - 24) * Math.Clamp(w.Y, 0, 1), width, height);
    }
    private void SaveLayout()
    {
        if (!layoutDirty || layoutPath.Length == 0) return;
        try { Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)); File.WriteAllText(layoutPath, JsonSerializer.Serialize(layout)); layoutDirty = false; } catch (Exception error) { Error(error); }
    }
    private void EndWindowDrag() { if (movingWindow.Length > 0 && GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0; movingWindow = ""; SaveLayout(); }
    private void PlaceWindow(string key, Rect rect, float dragWidth, float minWidth, float minHeight)
    {
        int id = GUIUtility.GetControlID(key.GetHashCode(), FocusType.Passive); var e = Event.current;
        bool interactive = GUI.enabled && Application.isFocused && Cursor.visible && !NativeMenu() && !NativeTextInput();
        var grip = new Rect(rect.xMax - 18, rect.yMax - 18, 18, 18);
        GUI.Label(grip, "//", smallStyle);
        if (interactive && e.type == EventType.MouseDown && e.button == 0 && (grip.Contains(e.mousePosition) || new Rect(rect.x + 5, rect.y + 3, Math.Max(0, dragWidth - 5), 30).Contains(e.mousePosition)))
        { GUIUtility.hotControl = id; dragControl = id; movingWindow = key; resizingWindow = grip.Contains(e.mousePosition); e.Use(); }
        if (movingWindow != key) return;
        if (!interactive) { EndWindowDrag(); return; }
        if (e.type == EventType.MouseDrag && GUIUtility.hotControl == dragControl)
        {
            var w = layout.Windows[key]; float width = rect.width, height = rect.height, x = rect.x, y = rect.y;
            if (resizingWindow)
            {
                width = Math.Clamp(width + e.delta.x, Math.Min(minWidth, Screen.width - 24), Screen.width - 24);
                height = Math.Clamp(height + e.delta.y, Math.Min(minHeight, Screen.height - 24), Screen.height - 24);
                w.Width = width; w.Height = height;
            }
            else { x += e.delta.x; y += e.delta.y; }
            w.X = Screen.width - width - 24 > 0 ? Math.Clamp((x - 12) / (Screen.width - width - 24), 0, 1) : 0;
            w.Y = Screen.height - height - 24 > 0 ? Math.Clamp((y - 12) / (Screen.height - height - 24), 0, 1) : 0;
            layoutDirty = true; e.Use();
        }
        if (e.type == EventType.MouseUp && e.button == 0) { EndWindowDrag(); e.Use(); }
    }

    // ---- rendering ----
    public override void OnGUI()
    {
        if (Game.InMainMenu || !Game.GetIsClient() || Game.GetIsServer() || !NetworkGameClient.GetInServer() || Player.CurrentPlayer == null) return;
        if (!drawingEnabled) { if (clock.Elapsed.TotalSeconds < nextDrawRetry) return; ResetAppearance(); drawingEnabled = true; }
        if (Screen.width < 200 || Screen.height < 100) return;
        try
        {
            Appearance();
            if (!panelOpen) { RenderHint(); return; }
            bool enabledBefore = GUI.enabled;
            GUI.enabled = enabledBefore && Application.isFocused && !NativeMenu() && !NativeTextInput() && Cursor.visible;
            try { RenderMenu(); if (voteOptions.Count > 0) RenderVote(); } finally { GUI.enabled = enabledBefore; }
        }
        catch (Exception error) { ClosePanel(); drawingEnabled = false; nextDrawRetry = clock.Elapsed.TotalSeconds + 2; Error(error); }
    }
    private void RenderHint()
    {
        if (voteOptions.Count == 0) return;
        string text = "Vote open: " + voteQuestion + "   (F6 to vote)";
        float width = Math.Min(Screen.width - 24, bodyStyle.CalcSize(new GUIContent(text)).x + 28);
        var rect = new Rect((Screen.width - width) / 2, 60, width, 34);
        Surface(rect, surface); Surface(new Rect(rect.x, rect.y, rect.width, 2), accent);
        GUI.Label(new Rect(rect.x + 14, rect.y + 6, rect.width - 28, 26), text, bodyStyle);
    }
    private IEnumerable<Mod> ModsFor(string cat) => cat == "admin"
        ? frame.Mods.Where(m => m.Cmds.Any(c => c.Admin)).OrderBy(m => m.Id == "_match" ? 0 : 1).ThenBy(m => OrderIndex(cat, m)).ThenBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
        : frame.Mods.Where(m => m.Id != "_match" && m.Category == cat && m.Cmds.Any(c => !c.Admin)).OrderBy(m => OrderIndex(cat, m)).ThenBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
    // user-dragged order of the mod list, kept per server and tab in the layout file; unlisted mods keep the server order after it
    private string OrderKey(string cat) => serverId + "|" + cat;
    private int OrderIndex(string cat, Mod m)
    {
        int i = layout?.ModOrder != null && layout.ModOrder.TryGetValue(OrderKey(cat), out var list) && list != null ? list.IndexOf(m.Id) : -1;
        return i < 0 ? int.MaxValue : i;
    }
    private Mod dragMod; private Vector2 dragStart; private bool dragging; private int dropIndex = -1;
    private void Reorder(List<Mod> mods, Mod mod, int index)
    {
        var ids = mods.Select(m => m.Id).ToList();
        int from = ids.IndexOf(mod.Id); if (from < 0) return;
        ids.RemoveAt(from); if (index > from) index--; index = Math.Clamp(index, 0, ids.Count); ids.Insert(index, mod.Id);
        EnsureLayout(); layout.ModOrder[OrderKey(category)] = ids; layoutDirty = true; SaveLayout();
        Write("Mod order (" + category + "): " + string.Join(", ", ids));
    }
    private string CatName(string key, string fallback) => frame.Cats != null && frame.Cats.TryGetValue(key, out var name) && !string.IsNullOrEmpty(name) ? name : fallback;

    private void RenderMenu()
    {
        var rect = LayoutRect("menu", Math.Min(1040, Screen.width - 24), Math.Min(760, Screen.height - 24), .5f, .5f, 720, 420);
        // A click anywhere outside the menu (and the vote panel) closes it.
        var ev = Event.current;
        if (ev.type == EventType.MouseDown && GUI.enabled && !rect.Contains(ev.mousePosition) && !(voteOptions.Count > 0 && voteRect.Contains(ev.mousePosition)))
        { ev.Use(); ClosePanel(); return; }
        Surface(rect, surface); Surface(new Rect(rect.x + 12, rect.y, rect.width - 24, 2), accent);
        PlaceWindow("menu", rect, rect.width - 110, 720, 420);
        float x = rect.x + 16, y = rect.y + 10, width = rect.width - 32;
        GUI.Label(new Rect(x, y, 300, 32), "MOD MENU", titleStyle);
        string state = !networkEnabled ? "transport unavailable" : serverSilent ? "no HUD server mod" : !frameReady ? "contacting server..." : frame.Mods.Count + " mods";
        GUI.Label(new Rect(x + 150, y + 6, width - 260, 26), state, smallStyle);
        if (Button(new Rect(rect.xMax - 96, y, 80, 32), "CLOSE", style: tabStyle)) { ClosePanel(); return; }
        y += 44;
        bool hasAdmin = frame.Mods.Any(m => m.Cmds.Any(c => c.Admin));
        var cats = new List<KeyValuePair<string, string>> { new("player", CatName("player", "Player")) };
        if (hasAdmin) cats.Add(new("admin", CatName("admin", "Admin")));
        cats.Add(new("info", CatName("info", "Info")));
        float tabWidth = Math.Min(170, (width - 10 * (cats.Count - 1)) / cats.Count);
        for (int i = 0; i < cats.Count; i++)
            if (Button(new Rect(x + i * (tabWidth + 10), y, tabWidth, 34), cats[i].Value.ToUpperInvariant(), category == cats[i].Key, style: tabStyle))
            { category = cats[i].Key; selectedMod = null; reply = null; }
        if (!cats.Any(c => c.Key == category)) category = "player";
        y += 46;
        float bottom = rect.yMax - 44;
        var statusRect = new Rect(x, rect.yMax - 36, width - 24, 26);
        GUI.Label(statusRect, status, smallStyle);
        if (category == "info") { RenderInfo(new Rect(x, y, width, bottom - y)); return; }
        float listWidth = Math.Min(280, width * .32f);
        var listRect = new Rect(x, y, listWidth, bottom - y);
        var pageRect = new Rect(x + listWidth + 14, y, width - listWidth - 14, bottom - y);
        RenderModList(listRect);
        if (pickerCmd != null) RenderPicker(pageRect);
        else if (reply != null) RenderReply(pageRect);
        else if (selectedMod != null && !browseCommands && CatalogFor(selectedMod) is Catalog shownCatalog && shownCatalog.Admin == (category == "admin")) RenderCatalog(pageRect);
        else if (selectedMod != null && category != "admin" && !browseCommands && BrowseFor(selectedMod) != null) RenderBrowse(pageRect);
        else if (selectedMod != null) RenderCommands(pageRect);
        else GUI.Label(new Rect(pageRect.x + 8, pageRect.y + 8, pageRect.width - 16, 60), frameReady ? "Choose a mod on the left." : "Waiting for the server's mod list...", bodyStyle);
    }
    private void RenderModList(Rect rect)
    {
        var mods = ModsFor(category).Where(m => category != "player" || IsEnabled(m)).ToList();   // switched-off mods: player tab hides them
        float rowHeight = 40, step = rowHeight + 6, w = rect.width - 24;
        bool custom = layout?.ModOrder != null && layout.ModOrder.ContainsKey(OrderKey(category));
        float total = mods.Count * step + (custom ? 34 : 0);
        modScroll = GUI.BeginScrollView(rect, modScroll, new Rect(0, 0, rect.width - 20, Math.Max(total, 1)), false, total > rect.height);
        try
        {
            // Drag to reorder: the raw event is read before the buttons consume it (mouse positions are scroll-content coordinates).
            var e = Event.current;
            bool mouseDown = e.type == EventType.MouseDown && e.button == 0, mouseUp = e.rawType == EventType.MouseUp, mouseDrag = e.type == EventType.MouseDrag;
            Vector2 mouse = e.mousePosition;
            float y = 0;
            for (int i = 0; i < mods.Count; i++)
            {
                var mod = mods[i];
                var row = new Rect(0, y, w, rowHeight);
                if (mouseDown && row.Contains(mouse)) { dragMod = mod; dragStart = mouse; dragging = false; dropIndex = -1; }
                if (dragging && dropIndex == i) GUI.Box(new Rect(0, y - 5, w, 4), GUIContent.none);
                string label = mod.Name + (IsEnabled(mod) ? "" : "  (off)") + (mod.Id == "_match" ? "" : "  (" + mod.Cmds.Count(c => category == "admin" ? c.Admin : !c.Admin) + ")");
                if (Button(row, label, selectedMod == mod) && !dragging)
                {
                    selectedMod = mod; reply = null; inlineReply = null; pickerCmd = null; status = ""; commandScroll = Vector2.zero;
                    browseCommands = false; browseSection = browseUnit = -1; browseScroll = Vector2.zero;
                    var catalog = CatalogFor(mod);
                    if (catalog != null && catalog.Credits && catalog.Admin == (category == "admin")) { AutoClose(null); SendCommand(catalog.Command + " status", mod, silent: true); }   // one cheap call: the credits line is read from the reply
                }
                y += step;
            }
            if (dragging && dropIndex == mods.Count) GUI.Box(new Rect(0, y - 5, w, 4), GUIContent.none);
            if (custom && Button(new Rect(0, y + 2, w, 26), "reset order", false, true, tabStyle)) { layout.ModOrder.Remove(OrderKey(category)); layoutDirty = true; SaveLayout(); }
            if (mods.Count == 0) GUI.Label(new Rect(4, 4, rect.width - 28, 60), frameReady ? "Nothing here for your role." : "", smallStyle);
            if (dragMod != null)
            {
                if (mouseDrag && !dragging && Math.Abs(mouse.y - dragStart.y) > 8) dragging = true;
                if (dragging)
                {
                    dropIndex = Math.Clamp((int)Math.Round(mouse.y / step), 0, mods.Count);
                    GUI.Label(new Rect(12, mouse.y - 14, w - 16, 28), "> " + dragMod.Name, bodyStyle);
                }
                if (mouseUp)
                {
                    if (dragging && dropIndex >= 0) Reorder(mods, dragMod, dropIndex);
                    dragMod = null; dragging = false; dropIndex = -1;
                }
            }
        }
        finally { GUI.EndScrollView(); }
    }
    private void RenderCommands(Rect rect)
    {
        var mod = selectedMod;
        bool admin = category == "admin";
        GUI.Label(new Rect(rect.x, rect.y, rect.width - 110, 30), mod.Name + (mod.Version.Length > 0 ? "  " + mod.Version : ""), titleStyle);
        if (browseCommands)
        {
            bool hasBrowse = !admin && BrowseFor(mod) != null, hasCatalog = CatalogFor(mod) is Catalog backCatalog && backCatalog.Admin == admin;
            if ((hasBrowse || hasCatalog) && Button(new Rect(rect.xMax - 100, rect.y, 96, 32), hasBrowse ? "BROWSE" : "CATALOG", style: tabStyle)) { browseCommands = false; return; }
        }
        float y = rect.y + 36;
        foreach (string line in InfoLines(mod))
        {
            float h = Math.Max(22, lineStyle.CalcHeight(new GUIContent(line), rect.width - 8));
            GUI.Label(new Rect(rect.x + 2, y, rect.width - 8, h), line, lineStyle); y += h + 2;
        }
        foreach (var table in TablesFor(mod)) y = RenderTable(rect, y, table);
        GUI.Label(new Rect(rect.x, y + 4, 110, 28), "Arguments", smallStyle);
        bool old = GUI.enabled; GUI.enabled = old && frameReady;
        try { arguments = GUI.TextField(new Rect(rect.x + 110, y, rect.width - 110, 32), arguments, 120, inputStyle); } finally { GUI.enabled = old; }
        y += 42;
        var cmds = mod.Cmds.Where(c => c.Admin == admin).OrderBy(c => c.Order).ToList();
        // the last switch / inline reply stays under the list instead of replacing it
        var footerLines = new List<string>();
        float footer = 0;
        if (inlineReply != null)
        {
            foreach (var raw in inlineReply.Lines) foreach (var part in raw.Replace("\r", "").Split('\n')) if (Plain(part).Trim().Length > 0) footerLines.Add(part.Trim());
            if (inlineReply.Error.Length > 0) footerLines.Add("Error: " + inlineReply.Error);
            if (footerLines.Count > 4) footerLines.RemoveRange(0, footerLines.Count - 4);
            footer = FooterHeight(rect, footerLines) + 10;
        }
        float width = rect.width - 24;
        float total = cmds.Sum(c => RowHeight(c, width));
        var view = new Rect(rect.x, y, rect.width, rect.yMax - footer - y);
        bool ready = frameReady && pendingSeq < 0 && outgoing.Count == 0;
        commandScroll = GUI.BeginScrollView(view, commandScroll, new Rect(0, 0, width, Math.Max(total, 1)), false, total > view.height);
        try
        {
            float cy = 0;
            foreach (var cmd in cmds)
            {
                float h = RowHeight(cmd, width);
                if (IsSwitch(cmd)) RenderSwitch(cmd, mod, cy, width, ready);
                else if (IsValueRow(cmd)) RenderValueRow(cmd, mod, cy, width, ready);
                else
                {
                    string label = cmd.Label + (cmd.Label != cmd.Text ? "   " + cmd.Text : "") + (cmd.Power.Length > 0 && cmd.Power != "None" ? "   [" + cmd.Power + "]" : "");
                    if (Button(new Rect(0, cy, width - 4, 36), label, false, ready))
                    {
                        string args = arguments.Trim();
                        if (args.Length == 0 && (cmd.Arg.Length > 0 || cmd.Choices.Count > 0)) { pickerCmd = cmd; pickerMod = mod; pickerScroll = Vector2.zero; pickerArgs.Clear(); }
                        else { AutoClose(mod); SendCommand(args.Length > 0 ? cmd.Text + " " + args : cmd.Text, mod); }
                    }
                }
                if (cmd.Desc.Length > 0) GUI.Label(new Rect(10, cy + h - DescHeight(cmd, width) - 2, width - 24, DescHeight(cmd, width)), cmd.Desc, smallStyle);
                cy += h;
            }
        }
        finally { GUI.EndScrollView(); }
        RenderFooter(rect, footerLines);
    }
    // ---- switches: a command with a current value and choices is one row "Label  [A] [B] [C]", current choice highlighted.
    // The click is shown at once (optimistic) until the next frame confirms the state; the mod's reply lands in the footer.
    private readonly Dictionary<string, KeyValuePair<string, double>> optimistic = new();
    private static bool IsSwitch(Cmd c) => c.Value != null && c.Choices.Count > 0;
    private static bool IsValueRow(Cmd c) => c.Value != null && c.Choices.Count == 0;   // a setting with a free value: field + SET
    private readonly Dictionary<string, string> valueInputs = new();
    private float ChoiceWidth(string label, float max) => Math.Min(max, Math.Max(64, bodyStyle.CalcSize(new GUIContent(label)).x + 26));
    private float DescHeight(Cmd cmd, float width) => cmd.Desc.Length > 0 ? smallStyle.CalcHeight(new GUIContent(cmd.Desc), width - 34) + 4 : 0;
    // one line when label and buttons fit, otherwise the label on its own line and the buttons flowing below it
    private void SwitchLayout(Cmd cmd, float width, out bool oneLine, out int buttonLines)
    {
        float labelW = bodyStyle.CalcSize(new GUIContent(cmd.Label)).x + 16, x = labelW; int lines = 1;
        foreach (var c in cmd.Choices) { float w = ChoiceWidth(c[1], width - 8); if (x > labelW && x + w > width - 8) { lines++; x = labelW; } x += w + 8; }
        oneLine = lines == 1;
        if (oneLine) { buttonLines = 1; return; }
        x = 0; lines = 1;
        foreach (var c in cmd.Choices) { float w = ChoiceWidth(c[1], width - 8); if (x > 0 && x + w > width - 8) { lines++; x = 0; } x += w + 8; }
        buttonLines = lines;
    }
    private float RowHeight(Cmd cmd, float width)
    {
        if (!IsSwitch(cmd)) return 42 + DescHeight(cmd, width);
        SwitchLayout(cmd, width, out bool oneLine, out int lines);
        return (oneLine ? 42 : 30 + lines * 40) + DescHeight(cmd, width);
    }
    private void RenderValueRow(Cmd cmd, Mod mod, float cy, float width, bool ready)
    {
        string current = cmd.Value ?? "";
        if (optimistic.TryGetValue(cmd.Text, out var chosen) && clock.Elapsed.TotalSeconds - chosen.Value < 10) current = chosen.Key;
        if (!valueInputs.TryGetValue(cmd.Text, out string input)) valueInputs[cmd.Text] = input = current;
        GUI.Label(new Rect(6, cy + 6, width - 240, 28), cmd.Label + "   (now " + current + ")", bodyStyle);
        bool oldEnabled = GUI.enabled; GUI.enabled = oldEnabled && ready;
        try { valueInputs[cmd.Text] = input = GUI.TextField(new Rect(width - 228, cy + 2, 130, 32), input, 32, inputStyle); } finally { GUI.enabled = oldEnabled; }
        string value = input.Trim();
        if (Button(new Rect(width - 90, cy + 2, 86, 32), "SET", false, ready && value.Length > 0 && value != current && Protocol.ValidCommandText(cmd.Text + " " + value), tabStyle))
        {
            optimistic[cmd.Text] = new KeyValuePair<string, double>(value, clock.Elapsed.TotalSeconds);
            AutoClose(mod); SendCommand(cmd.Text + " " + value, mod, inline: true);
        }
    }
    private static bool ContainsValue(string[] set, string value)
    {
        for (int i = 0; i < set.Length; i++) if (set[i].Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    private static string ToggledSet(string[] set, string value, bool add)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < set.Length; i++)
        {
            if (set[i].Equals(value, StringComparison.OrdinalIgnoreCase)) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(set[i]);
        }
        if (add) { if (sb.Length > 0) sb.Append(','); sb.Append(value); }
        return sb.ToString();
    }
    private void RenderSwitch(Cmd cmd, Mod mod, float cy, float width, bool ready)
    {
        SwitchLayout(cmd, width, out bool oneLine, out _);
        string current = cmd.Value ?? "";
        if (optimistic.TryGetValue(cmd.Text, out var chosen) && clock.Elapsed.TotalSeconds - chosen.Value < 10) current = chosen.Key;
        // Multi-select row ("m"): v lists every selected value, comma separated; a click toggles that value (the mod
        // adds or removes it and the next frame carries the real set). Selected buttons stay clickable.
        string[] selectedSet = cmd.Multi ? current.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries) : null;
        float labelW = bodyStyle.CalcSize(new GUIContent(cmd.Label)).x + 16;
        GUI.Label(new Rect(6, cy + 6, oneLine ? labelW : width - 12, 28), cmd.Label, bodyStyle);
        float x = oneLine ? labelW : 0, by = oneLine ? cy + 2 : cy + 32;
        foreach (var c in cmd.Choices)
        {
            if (c == null || c.Length < 2) continue;
            float w = ChoiceWidth(c[1], width - 8);
            if (x > (oneLine ? labelW : 0) && x + w > width - 8) { by += 40; x = oneLine ? labelW : 0; }
            bool selected = selectedSet == null ? c[0].Equals(current, StringComparison.OrdinalIgnoreCase) : ContainsValue(selectedSet, c[0]);
            if (Button(new Rect(x, by, w, 34), selected ? "[ " + c[1] + " ]" : c[1], selected, ready && (!selected || cmd.Multi)))
            {
                string next = selectedSet == null ? c[0] : ToggledSet(selectedSet, c[0], !selected);
                optimistic[cmd.Text] = new KeyValuePair<string, double>(next, clock.Elapsed.TotalSeconds);
                AutoClose(mod); SendCommand(cmd.Text + " " + c[0], mod, inline: true);
            }
            x += w + 8;
        }
    }
    // {"tables":[{"n":title,"h":[headers],"r":[[cells],...]}]} in a mod's data (shared, or per player through
    // ModUIPlayerData): grids shown above the command list, for example the Leaderboard's own stats.
    private sealed class Table { public string Name = ""; public List<string> Headers = new(); public List<List<string>> Rows = new(); }
    private static List<Table> TablesFor(Mod mod)
    {
        var tables = new List<Table>();
        try
        {
            if (mod?.Data == null || mod.Data.Value.ValueKind != JsonValueKind.Object || !mod.Data.Value.TryGetProperty("tables", out var arr) || arr.ValueKind != JsonValueKind.Array) return tables;
            foreach (var t in arr.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var table = new Table();
                if (t.TryGetProperty("n", out var n) && n.ValueKind == JsonValueKind.String) table.Name = Plain(n.GetString() ?? "");
                if (t.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.Array) foreach (var c in h.EnumerateArray()) table.Headers.Add(Plain(c.ToString()));
                if (t.TryGetProperty("r", out var r) && r.ValueKind == JsonValueKind.Array)
                    foreach (var row in r.EnumerateArray())
                    {
                        if (row.ValueKind != JsonValueKind.Array) continue;
                        var cells = new List<string>(); foreach (var c in row.EnumerateArray()) cells.Add(Plain(c.ToString()));
                        table.Rows.Add(cells);
                    }
                if (table.Rows.Count > 0 || table.Headers.Count > 0) tables.Add(table);
            }
        }
        catch { }
        return tables;
    }
    private float RenderTable(Rect rect, float y, Table t)
    {
        int cols = Math.Max(t.Headers.Count, t.Rows.Count > 0 ? t.Rows.Max(r => r.Count) : 0);
        if (cols == 0) return y;
        var widths = new float[cols];
        for (int c = 0; c < cols; c++)
        {
            float w = c < t.Headers.Count ? smallStyle.CalcSize(new GUIContent(t.Headers[c])).x : 0;
            foreach (var r in t.Rows) if (c < r.Count) w = Math.Max(w, bodyStyle.CalcSize(new GUIContent(r[c])).x);
            widths[c] = w + 18;
        }
        float sum = widths.Sum(), avail = rect.width - 8;
        if (sum > avail) for (int c = 0; c < cols; c++) widths[c] *= avail / sum;
        if (t.Name.Length > 0) { GUI.Label(new Rect(rect.x + 2, y, rect.width - 8, 24), t.Name, lineStyle); y += 24; }
        float x = rect.x + 2;
        if (t.Headers.Count > 0)
        {
            for (int c = 0; c < cols; c++) { if (c < t.Headers.Count) GUI.Label(new Rect(x, y, widths[c], 22), t.Headers[c], smallStyle); x += widths[c]; }
            y += 22;
        }
        foreach (var r in t.Rows)
        {
            x = rect.x + 2;
            for (int c = 0; c < cols; c++) { if (c < r.Count) GUI.Label(new Rect(x, y, widths[c], 24), r[c], bodyStyle); x += widths[c]; }
            y += 24;
        }
        return y + 8;
    }
    // {"enabled":false} in a mod's data: the mod is switched off. The player tab hides it, the admin tab marks it "(off)".
    private static bool IsEnabled(Mod mod)
    {
        try
        {
            if (mod?.Data != null && mod.Data.Value.ValueKind == JsonValueKind.Object && mod.Data.Value.TryGetProperty("enabled", out var e))
                return e.ValueKind != JsonValueKind.False;
        }
        catch { }
        return true;
    }
    // {"info":["line",...]} in a mod's data: status lines shown above its command list (for example the Elo switches).
    private static List<string> InfoLines(Mod mod)
    {
        var lines = new List<string>();
        try
        {
            if (mod?.Data != null && mod.Data.Value.ValueKind == JsonValueKind.Object && mod.Data.Value.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                foreach (var line in info.EnumerateArray()) if (line.ValueKind == JsonValueKind.String) lines.Add(Plain(line.GetString() ?? ""));
        }
        catch { }
        return lines;
    }

    // ---- catalog view: a mod that publishes {"cmd","catalog":{faction:[{"c":category,"u":[[name,cost],...]}]}} gets a
    // two-level button grid instead of the command list; a click sends one direct command ("/buy Heavy Tank").
    // Items are [name, cost] or [name, cost, label]; extras: "admin" (admin tab), "all" (faction tabs), "credits": false, "offText".
    private sealed class CatalogItem { public string Name = "", Label = ""; public int Cost; }
    private sealed class CatalogCategory { public string Name = ""; public readonly List<CatalogItem> Units = new(); }
    private sealed class Catalog
    {
        public string Command = "/buy", OffText = "The economy is switched off on this server.";
        public bool Enabled = true, Admin, All, Credits = true;
        public readonly List<string> FactionOrder = new();
        public readonly Dictionary<string, List<CatalogCategory>> Factions = new(StringComparer.OrdinalIgnoreCase);
    }
    private readonly Dictionary<string, Catalog> catalogs = new();   // keyed by mod id + hash
    private Vector2 catalogScroll;
    private string catalogFaction = "";
    private static string ItemLabel(CatalogItem item) => item.Cost > 0 ? item.Label + "  " + item.Cost.ToString(CultureInfo.InvariantCulture) + "cr" : item.Label;
    private string CatalogFaction(Catalog catalog)
    {
        if (catalogFaction.Length > 0 && catalog.Factions.ContainsKey(catalogFaction)) return catalogFaction;
        string local = LocalFaction();
        if (local.Length > 0 && catalog.Factions.ContainsKey(local)) return local;
        return catalog.FactionOrder.Count > 0 ? catalog.FactionOrder[0] : "";
    }
    private Catalog CatalogFor(Mod mod)
    {
        if (mod == null || mod.Data == null) return null;
        string key = mod.Id + ":" + mod.Hash;
        if (catalogs.TryGetValue(key, out var cached)) return cached;
        Catalog result = null;
        try
        {
            var data = mod.Data.Value;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("catalog", out var factions) && factions.ValueKind == JsonValueKind.Object)
            {
                result = new Catalog();
                if (data.TryGetProperty("cmd", out var cmd) && cmd.ValueKind == JsonValueKind.String) result.Command = cmd.GetString() ?? "/buy";
                if (data.TryGetProperty("enabled", out var enabled) && (enabled.ValueKind == JsonValueKind.True || enabled.ValueKind == JsonValueKind.False)) result.Enabled = enabled.GetBoolean();
                if (data.TryGetProperty("admin", out var adminFlag) && adminFlag.ValueKind == JsonValueKind.True) result.Admin = true;
                if (data.TryGetProperty("all", out var allFlag) && allFlag.ValueKind == JsonValueKind.True) result.All = true;
                if (data.TryGetProperty("credits", out var creditsFlag) && creditsFlag.ValueKind == JsonValueKind.False) result.Credits = false;
                if (data.TryGetProperty("offText", out var offText) && offText.ValueKind == JsonValueKind.String && (offText.GetString() ?? "").Length > 0) result.OffText = Plain(offText.GetString());
                foreach (var faction in factions.EnumerateObject())
                {
                    if (faction.Value.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<CatalogCategory>();
                    foreach (var cat in faction.Value.EnumerateArray())
                    {
                        if (cat.ValueKind != JsonValueKind.Object || !cat.TryGetProperty("c", out var name) || !cat.TryGetProperty("u", out var units) || units.ValueKind != JsonValueKind.Array) continue;
                        var entry = new CatalogCategory { Name = name.GetString() ?? "" };
                        foreach (var unit in units.EnumerateArray())
                        {
                            if (unit.ValueKind != JsonValueKind.Array || unit.GetArrayLength() < 2) continue;
                            string unitName = unit[0].GetString() ?? "";
                            string unitLabel = unit.GetArrayLength() > 2 && unit[2].ValueKind == JsonValueKind.String ? Plain(unit[2].GetString() ?? "") : "";
                            if (unitName.Length > 0 && unit[1].TryGetInt32(out int cost)) entry.Units.Add(new CatalogItem { Name = unitName, Cost = cost, Label = unitLabel.Length > 0 ? unitLabel : unitName });
                        }
                        if (entry.Units.Count > 0) list.Add(entry);
                    }
                    if (list.Count > 0) { result.Factions[faction.Name] = list; result.FactionOrder.Add(faction.Name); }
                }
                if (result.Factions.Count == 0) result = null;
            }
        }
        catch (Exception error) { Write("CATALOG of " + mod.Id + " unreadable: " + error.Message); result = null; }
        if (catalogs.Count > 16) catalogs.Clear();
        catalogs[key] = result;
        return result;
    }
    private static string LocalFaction()
    {
        try
        {
            var p = Player.CurrentPlayer; var team = p == null ? null : p.Team;
            string name = team == null ? "" : team.name ?? "";
            if (name.Contains("Alien")) return "Alien";
            if (name.Contains("Centauri")) return "Centauri";
            if (name.Contains("Sol")) return "Sol";
        }
        catch { }
        return "";
    }
    private void RenderCatalog(Rect rect)
    {
        var mod = selectedMod; var catalog = CatalogFor(mod);
        string faction = catalog.All ? CatalogFaction(catalog) : LocalFaction();
        GUI.Label(new Rect(rect.x, rect.y, rect.width - 4, 30), mod.Name + (!catalog.All && faction.Length > 0 ? "   " + faction : ""), titleStyle);
        float y = rect.y + 36, bw = Math.Min(150, (rect.width - 30) / 4);
        bool ready = frameReady && pendingSeq < 0 && outgoing.Count == 0;
        if (catalog.Credits) GUI.Label(new Rect(rect.x, y + 4, 2 * bw, 28), "Credits: " + (credits >= 0 ? credits.ToString(CultureInfo.InvariantCulture) : "?"), bodyStyle);
        if (Button(new Rect(rect.x + 2 * (bw + 10), y, bw, 34), "COMMANDS", false, true, tabStyle)) { browseCommands = true; return; }
        if (Button(new Rect(rect.x + 3 * (bw + 10), y, bw, 34), "CHAT MENU", false, ready, tabStyle)) { inlineReply = null; SendCommand(catalog.Command, mod); }
        y += 44;
        if (catalog.All)
        {
            // one tab per faction the catalog carries (an admin spawning for any team), instead of the viewer's own faction
            float fx = rect.x;
            foreach (string f in catalog.FactionOrder)
            {
                float fw = Math.Min(bw, Math.Max(90, bodyStyle.CalcSize(new GUIContent(f)).x + 24));
                if (fx > rect.x && fx + fw > rect.xMax) { fx = rect.x; y += 40; }
                if (Button(new Rect(fx, y, fw, 32), f, faction.Equals(f, StringComparison.OrdinalIgnoreCase), true, tabStyle)) { catalogFaction = f; catalogScroll = Vector2.zero; }
                fx += fw + 8;
            }
            y += 40;
        }
        foreach (string line in InfoLines(mod))
        {
            float h = Math.Max(22, lineStyle.CalcHeight(new GUIContent(line), rect.width - 8));
            GUI.Label(new Rect(rect.x + 2, y, rect.width - 8, h), line, lineStyle); y += h + 2;
        }
        // Footer: the last reply (purchase result / balance), kept under the grid so the grid never disappears.
        float footer = 0;
        var footerLines = new List<string>();
        if (inlineReply != null)
        {
            foreach (var raw in inlineReply.Lines) foreach (var part in raw.Replace("\r", "").Split('\n')) if (Plain(part).Trim().Length > 0) footerLines.Add(part.Trim());
            if (inlineReply.Error.Length > 0) footerLines.Add("Error: " + inlineReply.Error);
            if (footerLines.Count > 4) footerLines.RemoveRange(0, footerLines.Count - 4);
            footer = FooterHeight(rect, footerLines) + 10;
        }
        if (!catalog.Enabled) { GUI.Label(new Rect(rect.x, y, rect.width, 30), catalog.OffText, smallStyle); y += 30; }
        if (!catalog.Factions.TryGetValue(faction, out var categories))
        {
            GUI.Label(new Rect(rect.x, y, rect.width - 8, 60), faction.Length == 0 ? (catalog.All ? "The catalog is empty." : "Join a team to see what you can buy.") : "No catalog for " + faction + " on this server.", bodyStyle);
            RenderFooter(rect, footerLines);
            return;
        }
        float width = rect.width - 24;
        var view = new Rect(rect.x, y, rect.width, rect.yMax - footer - y);
        float total = 0;
        foreach (var cat in categories) total += 28 + FlowHeight(cat, width - 8);
        catalogScroll = GUI.BeginScrollView(view, catalogScroll, new Rect(0, 0, width, Math.Max(total, 1)), false, total > view.height);
        try
        {
            float cy = 0;
            foreach (var cat in categories)
            {
                GUI.Label(new Rect(6, cy, width - 12, 26), cat.Name, bodyStyle); cy += 28;
                float ox = 0;
                foreach (var unit in cat.Units)
                {
                    string label = ItemLabel(unit);
                    float w = Math.Min(width - 8, Math.Max(120, bodyStyle.CalcSize(new GUIContent(label)).x + 28));
                    if (ox > 0 && ox + w > width - 8) { cy += 40; ox = 0; }
                    if (Button(new Rect(ox, cy, w, 34), label, false, ready && catalog.Enabled)) { AutoClose(null); SendCommand(catalog.Command + " " + unit.Name, mod, inline: true); }
                    ox += w + 8;
                }
                cy += 46;
            }
        }
        finally { GUI.EndScrollView(); }
        RenderFooter(rect, footerLines);
    }
    // ---- browse view: a mod that publishes {"browse":{"title":..,"sections":[{"n":..,"u":[{"n":..,"c":changes,"g":[{"n":..,"r":[[label,value,vanilla?,note?],..]}]}]}]}}
    // gets a three-level read-only browser (sections > entries > stat groups). Rows with a third element differ from vanilla.
    private sealed class BrowseRow { public string Label = "", Value = "", Vanilla = "", Note = ""; public bool Changed; }
    private sealed class BrowseGroup { public string Name = ""; public readonly List<BrowseRow> Rows = new(); }
    private sealed class BrowseEntry { public string Name = ""; public int Changes; public readonly List<BrowseGroup> Groups = new(); }
    private sealed class BrowseSection { public string Name = ""; public readonly List<BrowseEntry> Entries = new(); public int Changes => Entries.Sum(e => e.Changes); }
    private sealed class Browse { public string Title = ""; public readonly List<BrowseSection> Sections = new(); }
    private readonly Dictionary<string, Browse> browses = new();   // keyed by mod id + hash
    private bool browseCommands, browseChangesOnly;
    private int browseSection = -1, browseUnit = -1;
    private Vector2 browseScroll;
    private Browse BrowseFor(Mod mod)
    {
        if (mod == null || mod.Data == null) return null;
        string key = mod.Id + ":" + mod.Hash;
        if (browses.TryGetValue(key, out var cached)) return cached;
        Browse result = null;
        try
        {
            var data = mod.Data.Value;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("browse", out var doc) && doc.ValueKind == JsonValueKind.Object
                && doc.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
            {
                result = new Browse();
                if (doc.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String) result.Title = title.GetString() ?? "";
                foreach (var sec in sections.EnumerateArray())
                {
                    if (sec.ValueKind != JsonValueKind.Object || !sec.TryGetProperty("n", out var sn) || !sec.TryGetProperty("u", out var units) || units.ValueKind != JsonValueKind.Array) continue;
                    var section = new BrowseSection { Name = sn.GetString() ?? "" };
                    foreach (var unit in units.EnumerateArray())
                    {
                        if (unit.ValueKind != JsonValueKind.Object || !unit.TryGetProperty("n", out var un)) continue;
                        var entry = new BrowseEntry { Name = un.GetString() ?? "" };
                        if (unit.TryGetProperty("g", out var groups) && groups.ValueKind == JsonValueKind.Array)
                            foreach (var grp in groups.EnumerateArray())
                            {
                                if (grp.ValueKind != JsonValueKind.Object || !grp.TryGetProperty("r", out var rows) || rows.ValueKind != JsonValueKind.Array) continue;
                                var group = new BrowseGroup { Name = grp.TryGetProperty("n", out var gn) && gn.ValueKind == JsonValueKind.String ? gn.GetString() ?? "" : "" };
                                foreach (var row in rows.EnumerateArray())
                                {
                                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 2) continue;
                                    var r = new BrowseRow { Label = row[0].ToString(), Value = row[1].ToString() };
                                    if (row.GetArrayLength() > 2) { r.Vanilla = row[2].ToString(); r.Changed = r.Vanilla.Length > 0; }
                                    if (row.GetArrayLength() > 3) r.Note = row[3].ToString();
                                    group.Rows.Add(r);
                                }
                                if (group.Rows.Count > 0) entry.Groups.Add(group);
                            }
                        entry.Changes = entry.Groups.Sum(g => g.Rows.Count(r => r.Changed));
                        if (entry.Groups.Count > 0) section.Entries.Add(entry);
                    }
                    if (section.Entries.Count > 0) result.Sections.Add(section);
                }
                if (result.Sections.Count == 0) result = null;
            }
        }
        catch (Exception error) { Write("BROWSE of " + mod.Id + " unreadable: " + error.Message); result = null; }
        if (browses.Count > 16) browses.Clear();
        browses[key] = result;
        return result;
    }
    private void RenderBrowse(Rect rect)
    {
        var mod = selectedMod; var browse = BrowseFor(mod);
        bool filter = browseChangesOnly;
        var sections = filter ? browse.Sections.Where(sct => sct.Changes > 0).ToList() : browse.Sections;
        if (browseSection >= sections.Count) { browseSection = -1; browseUnit = -1; }
        var section = browseSection >= 0 ? sections[browseSection] : null;
        var entries = section == null ? null : filter ? section.Entries.Where(e => e.Changes > 0).ToList() : section.Entries;
        if (entries != null && browseUnit >= entries.Count) browseUnit = -1;
        var entry = entries != null && browseUnit >= 0 ? entries[browseUnit] : null;
        string title = (browse.Title.Length > 0 ? browse.Title : mod.Name) + (section != null ? "  >  " + section.Name : "") + (entry != null ? "  >  " + entry.Name : "");
        GUI.Label(new Rect(rect.x, rect.y, rect.width - 4, 30), title, titleStyle);
        float y = rect.y + 36, bw = Math.Min(150, (rect.width - 30) / 4);
        if (Button(new Rect(rect.x, y, bw, 34), "BACK", false, section != null, tabStyle)) { if (entry != null) browseUnit = -1; else browseSection = -1; browseScroll = Vector2.zero; return; }
        if (Button(new Rect(rect.x + bw + 10, y, bw, 34), "CHANGES ONLY", browseChangesOnly, true, tabStyle)) { browseChangesOnly = !browseChangesOnly; browseScroll = Vector2.zero; return; }
        if (Button(new Rect(rect.x + 2 * (bw + 10), y, bw, 34), "COMMANDS", false, true, tabStyle)) { browseCommands = true; return; }
        y += 44;
        float width = rect.width - 24;
        var view = new Rect(rect.x, y, rect.width, rect.yMax - y);
        if (entry == null)
        {
            // section or entry list
            var labels = new List<string>();
            if (section == null) foreach (var sct in sections) labels.Add(sct.Name + "   " + sct.Entries.Count + (sct.Entries.Count == 1 ? " entry" : " entries") + (sct.Changes > 0 ? ", " + sct.Changes + " changes" : ""));
            else foreach (var e in entries) labels.Add(e.Name + (e.Changes > 0 ? "   " + e.Changes + (e.Changes == 1 ? " change" : " changes") : ""));
            float total = labels.Count * 40;
            browseScroll = GUI.BeginScrollView(view, browseScroll, new Rect(0, 0, width, Math.Max(total, 1)), false, total > view.height);
            try
            {
                float cy = 0;
                for (int i = 0; i < labels.Count; i++)
                {
                    if (Button(new Rect(0, cy, width - 4, 34), labels[i])) { if (section == null) browseSection = i; else browseUnit = i; browseScroll = Vector2.zero; return; }
                    cy += 40;
                }
                if (labels.Count == 0) GUI.Label(new Rect(4, 4, width - 8, 40), filter ? "Nothing differs from vanilla here." : "Nothing to show.", bodyStyle);
            }
            finally { GUI.EndScrollView(); }
            return;
        }
        // stat table of one entry
        var lines = new List<(string text, GUIStyle style, bool changed, float h)>();
        foreach (var group in entry.Groups)
        {
            var rows = filter ? group.Rows.Where(r => r.Changed).ToList() : group.Rows;
            if (rows.Count == 0) continue;
            if (group.Name.Length > 0) lines.Add((group.Name, bodyStyle, false, 30));
            foreach (var r in rows)
            {
                string text = r.Label + ":  " + r.Value + (r.Changed ? "   (vanilla " + r.Vanilla + (r.Note.Length > 0 ? ", " + r.Note : "") + ")" : "");
                lines.Add((text, lineStyle, r.Changed, Math.Max(24, lineStyle.CalcHeight(new GUIContent(text), width - 16) + 2)));
            }
        }
        float height = lines.Sum(l => l.h) + 8;
        browseScroll = GUI.BeginScrollView(view, browseScroll, new Rect(0, 0, width, Math.Max(height, 1)), false, height > view.height);
        try
        {
            float cy = 0;
            foreach (var line in lines)
            {
                if (line.changed) Surface(new Rect(0, cy, width - 4, line.h), Color.Lerp(surface, accent, .25f));
                GUI.Label(new Rect(line.style == bodyStyle ? 4 : 12, cy + (line.style == bodyStyle ? 6 : 0), width - 20, line.h), line.text, line.style);
                cy += line.h;
            }
            if (lines.Count == 0) GUI.Label(new Rect(4, 4, width - 8, 40), "Nothing differs from vanilla for " + entry.Name + ".", bodyStyle);
        }
        finally { GUI.EndScrollView(); }
    }
    private float FlowHeight(CatalogCategory cat, float width)
    {
        float ox = 0, rows = 1;
        foreach (var unit in cat.Units)
        {
            string label = ItemLabel(unit);
            float w = Math.Min(width, Math.Max(120, bodyStyle.CalcSize(new GUIContent(label)).x + 28));
            if (ox > 0 && ox + w > width) { rows++; ox = 0; }
            ox += w + 8;
        }
        return rows * 40 + 6;
    }
    private float FooterHeight(Rect rect, List<string> lines)
    {
        float total = 0;
        foreach (var line in lines) total += Math.Max(24, lineStyle.CalcHeight(new GUIContent(line), rect.width - 16)) + 2;
        return total;
    }
    private void RenderFooter(Rect rect, List<string> lines)
    {
        if (lines.Count == 0) return;
        float y = rect.yMax - FooterHeight(rect, lines) - 6;
        Surface(new Rect(rect.x, y - 4, rect.width - 4, 2), accent);
        foreach (var line in lines)
        {
            float h = Math.Max(24, lineStyle.CalcHeight(new GUIContent(line), rect.width - 16));
            GUI.Label(new Rect(rect.x + 6, y, rect.width - 16, h), line, lineStyle); y += h + 2;
        }
    }
    private void RenderReply(Rect rect)
    {
        var r = reply; var mod = replyMod ?? selectedMod; string nav = mod?.Nav ?? "/";
        GUI.Label(new Rect(rect.x, rect.y, rect.width - 4, 30), r.Cmd, titleStyle);
        float y = rect.y + 36, bw = Math.Min(150, (rect.width - 30) / 4);
        bool ready = frameReady && pendingSeq < 0 && outgoing.Count == 0;
        if (Button(new Rect(rect.x, y, bw, 34), "< COMMANDS", style: tabStyle)) { AutoClose(null); reply = null; status = ""; return; }
        if (Button(new Rect(rect.x + bw + 10, y, bw, 34), "BACK", false, ready, tabStyle)) SendCommand(nav + "back", mod);
        if (Button(new Rect(rect.x + 2 * (bw + 10), y, bw, 34), "CLOSE MENU", false, ready, tabStyle)) SendCommand(nav + "0", mod);
        if (Button(new Rect(rect.x + 3 * (bw + 10), y, bw, 34), "RERUN", false, ready, tabStyle)) SendCommand(r.Cmd, mod);
        y += 44;
        var view = new Rect(rect.x, y, rect.width, rect.yMax - y);
        float width = rect.width - 24;
        var rows = new List<Row>();
        foreach (var raw in r.Lines) foreach (var part in raw.Replace("\r", "").Split('\n')) if (Plain(part).Trim().Length > 0) rows.Add(ParseRow(part, nav));
        float total = 0;
        foreach (var row in rows) { row.Height = MeasureRow(row, width); total += row.Height; }
        replyScroll = GUI.BeginScrollView(view, replyScroll, new Rect(0, 0, width, Math.Max(total, 1)), false, total > view.height);
        try
        {
            float cy = 0;
            foreach (var row in rows)
            {
                if (row.Opts.Count == 0) GUI.Label(new Rect(6, cy, width - 12, row.Height), row.Line, lineStyle);
                else if (row.Opts.Count == 1 && row.Opts[0].Param)
                {
                    var o = row.Opts[0];
                    if (paramInputsSeq != r.Seq) { paramInputs.Clear(); paramInputsSeq = r.Seq; }
                    if (!paramInputs.ContainsKey(o.Send)) paramInputs[o.Send] = o.Value ?? "";
                    GUI.Label(new Rect(6, cy + 6, width - 236, 30), o.Label, bodyStyle);
                    bool oldEnabled = GUI.enabled; GUI.enabled = oldEnabled && ready;
                    try { paramInputs[o.Send] = GUI.TextField(new Rect(width - 224, cy + 2, 130, 32), paramInputs[o.Send], 32, inputStyle); } finally { GUI.enabled = oldEnabled; }
                    if (Button(new Rect(width - 86, cy + 2, 82, 32), "SET", false, ready && paramInputs[o.Send].Trim().Length > 0, tabStyle))
                        SendCommand(o.Send + " " + paramInputs[o.Send].Trim(), mod);
                }
                else
                {
                    float oy = cy, ox = 0;
                    if (row.Lead.Length > 0) { GUI.Label(new Rect(6, oy, width - 12, 24), row.Lead, bodyStyle); oy += 26; }
                    foreach (var o in row.Opts)
                    {
                        float w = OptionWidth(o, width - 8);
                        if (ox > 0 && ox + w > width - 8) { oy += 38; ox = 0; }
                        if (Button(new Rect(ox, oy, w, 34), o.Label, false, ready)) SendCommand(o.Send, mod);
                        ox += w + 8;
                    }
                }
                cy += row.Height;
            }
        }
        finally { GUI.EndScrollView(); }
    }
    // Argument picker. cmd.Arg is a chain of stages ("player", "team", "map", "ban", "choice"; comma separated); each stage
    // adds one token, the last one sends "<text> <tokens...>". player and team are built locally, the rest from cmd.Choices.
    private Cmd pickerCmd; private Mod pickerMod; private Vector2 pickerScroll; private readonly List<string> pickerArgs = new();
    private readonly Dictionary<string, string> paramInputs = new(); private long paramInputsSeq = -1;
    private static string[] Stages(Cmd cmd)
    {
        var stages = (cmd.Arg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return stages.Length > 0 ? stages : new[] { "choice" };
    }
    private void RenderPicker(Rect rect)
    {
        var cmd = pickerCmd; var mod = pickerMod;
        var stages = Stages(cmd);
        if (pickerArgs.Count >= stages.Length) { pickerCmd = null; return; }
        if (stages.Length == 2 && pickerArgs.Count == 0 && stages.Contains("player", StringComparer.OrdinalIgnoreCase) && stages.Contains("team", StringComparer.OrdinalIgnoreCase)) { RenderMatrixPicker(rect, cmd, mod, stages); return; }
        string rawStage = stages[pickerArgs.Count].ToLowerInvariant();
        bool optional = rawStage.EndsWith("?");                       // "<stage>?" may be skipped (must be the last stage)
        string stage = rawStage.TrimEnd('?');
        if (stage == "text") { RenderTextStage(rect, cmd, mod, stages, optional); return; }
        string what = stage == "map" ? "a map" : stage == "mode" ? "a game mode" : stage == "ban" ? "a banned player" : stage == "player" ? "a player" : stage == "team" ? "a team" : "an option";
        string collected = pickerArgs.Count > 0 ? " " + string.Join(" ", pickerArgs) : "";
        GUI.Label(new Rect(rect.x, rect.y, rect.width - 220, 30), cmd.Text + collected + "   choose " + what, titleStyle);
        if (Button(new Rect(rect.xMax - 100, rect.y, 96, 32), pickerArgs.Count > 0 ? "BACK" : "CANCEL", style: tabStyle))
        {
            if (pickerArgs.Count > 0) pickerArgs.RemoveAt(pickerArgs.Count - 1); else pickerCmd = null;
            return;
        }
        bool readySkip = frameReady && pendingSeq < 0 && outgoing.Count == 0;
        if (optional && Button(new Rect(rect.xMax - 206, rect.y, 100, 32), "DEFAULT", false, readySkip, tabStyle)) { FinishStage(cmd, mod, stages, skipped: true); return; }
        float y = rect.y + 40;
        var rows = new List<KeyValuePair<string, string>>();   // value to send, label
        string empty;
        if (stage == "player")
        {
            var names = new List<string>(); var labels = new List<string>();
            var players = Player.Players; var me = Player.CurrentPlayer;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i]; if (p == null) continue;
                string team = p.Team == null ? "spectator" : p.Team.name.Replace("Team_Human_", "").Replace("Team_", "");
                names.Add(p.PlayerName ?? ""); labels.Add((p.PlayerName ?? "?") + "   " + team + (p == me ? "   (you)" : ""));
            }
            for (int i = 0; i < names.Count; i++) rows.Add(new(TargetToken(names[i], names), labels[i]));
            empty = "No players found.";
        }
        else if (stage == "team") { rows = TeamRows(); empty = "No teams in this match."; }
        else if (stage == "mode") { rows = ModeRows(cmd, stages); empty = "This map lists no game modes; DEFAULT uses the map's first mode."; }
        else
        {
            foreach (var c in cmd.Choices) if (c != null && c.Length >= 2) rows.Add(new(c[0], c[1]));
            empty = stage == "map" ? "The server sent no map list." : stage == "ban" ? "Nobody is banned." : "The server sent no options.";
        }
        bool ready = frameReady && pendingSeq < 0 && outgoing.Count == 0;
        var view = new Rect(rect.x, y, rect.width, rect.yMax - y);
        float total = rows.Count * 40;
        pickerScroll = GUI.BeginScrollView(view, pickerScroll, new Rect(0, 0, rect.width - 20, Math.Max(total, 1)), false, total > view.height);
        try
        {
            float cy = 0;
            foreach (var row in rows)
            {
                if (Button(new Rect(0, cy, rect.width - 24, 34), row.Value, false, ready))
                {
                    pickerArgs.Add(row.Key); FinishStage(cmd, mod, stages); return;
                }
                cy += 40;
            }
            if (rows.Count == 0) GUI.Label(new Rect(4, 4, rect.width - 28, 40), empty, bodyStyle);
        }
        finally { GUI.EndScrollView(); }
    }
    // "mode" stage: the game modes of the map chosen in the earlier "map" stage. The server lists them as the third
    // element of that map's choice row ("MP_Strategy,MP_Siege"); the label drops the MP_ prefix.
    private List<KeyValuePair<string, string>> ModeRows(Cmd cmd, string[] stages)
    {
        var rows = new List<KeyValuePair<string, string>>();
        int mapStage = Array.FindIndex(stages, s => s.TrimEnd('?').Equals("map", StringComparison.OrdinalIgnoreCase));
        if (mapStage < 0 || mapStage >= pickerArgs.Count) return rows;
        string map = pickerArgs[mapStage];
        foreach (var c in cmd.Choices)
        {
            if (c == null || c.Length < 3 || !c[0].Equals(map, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var mode in c[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string m = mode.Trim(); if (m.Length == 0) continue;
                string label = (m.StartsWith("MP_", StringComparison.OrdinalIgnoreCase) ? m.Substring(3) : m).Replace("_", " ");
                rows.Add(new(m, label + "   (" + m + ")"));
            }
            break;
        }
        return rows;
    }
    // A typed stage ("text", or "text?" when it may be skipped): one field and OK; the value becomes the next token.
    private string pickerInput = "";
    private void RenderTextStage(Rect rect, Cmd cmd, Mod mod, string[] stages, bool optional)
    {
        string collected = pickerArgs.Count > 0 ? " " + string.Join(" ", pickerArgs) : "";
        GUI.Label(new Rect(rect.x, rect.y, rect.width - 110, 30), cmd.Text + collected + "   type a value", titleStyle);
        if (Button(new Rect(rect.xMax - 100, rect.y, 96, 32), pickerArgs.Count > 0 ? "BACK" : "CANCEL", style: tabStyle))
        {
            if (pickerArgs.Count > 0) pickerArgs.RemoveAt(pickerArgs.Count - 1); else pickerCmd = null;
            return;
        }
        float y = rect.y + 40;
        if (cmd.Desc.Length > 0) { float h = smallStyle.CalcHeight(new GUIContent(cmd.Desc), rect.width - 8); GUI.Label(new Rect(rect.x + 2, y, rect.width - 8, h), cmd.Desc, smallStyle); y += h + 6; }
        bool ready = frameReady && pendingSeq < 0 && outgoing.Count == 0;
        bool oldEnabled = GUI.enabled; GUI.enabled = oldEnabled && ready;
        try { pickerInput = GUI.TextField(new Rect(rect.x, y, Math.Min(260, rect.width - 200), 32), pickerInput, 40, inputStyle); } finally { GUI.enabled = oldEnabled; }
        string value = pickerInput.Trim().Replace(" ", "");
        float bx = rect.x + Math.Min(260, rect.width - 200) + 10;
        if (Button(new Rect(bx, y, 80, 32), "OK", false, ready && value.Length > 0, tabStyle)) { pickerArgs.Add(value); pickerInput = ""; FinishStage(cmd, mod, stages); return; }
        if (optional && Button(new Rect(bx + 90, y, 80, 32), "SKIP", false, ready, tabStyle)) { pickerInput = ""; FinishStage(cmd, mod, stages, skipped: true); return; }
    }
    // After a stage: continue with the next one, or send the command once the chain is complete.
    private void FinishStage(Cmd cmd, Mod mod, string[] stages, bool skipped = false)
    {
        int next = pickerArgs.Count + (skipped ? 1 : 0);
        if (next < stages.Length && !skipped) { pickerScroll = Vector2.zero; return; }
        string text = pickerArgs.Count > 0 ? cmd.Text + " " + string.Join(" ", pickerArgs) : cmd.Text;
        pickerCmd = null; pickerArgs.Clear(); AutoClose(mod); SendCommand(text, mod);
    }
    // Player + team in one page: one row per player with a button per team of the match; a click sends both tokens in
    // the chain's order ("!team <player> <team>", "/elo seat <team> <player>").
    private void RenderMatrixPicker(Rect rect, Cmd cmd, Mod mod, string[] stages)
    {
        bool teamFirst = stages[0].Equals("team", StringComparison.OrdinalIgnoreCase);
        GUI.Label(new Rect(rect.x, rect.y, rect.width - 110, 30), cmd.Text + "   pick the team next to the player", titleStyle);
        if (Button(new Rect(rect.xMax - 100, rect.y, 96, 32), "CANCEL", style: tabStyle)) { pickerCmd = null; return; }
        var teams = TeamRows();
        var names = new List<string>(); var teamOf = new List<string>();
        var players = Player.Players; var me = Player.CurrentPlayer;
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i]; if (p == null) continue;
            names.Add(p.PlayerName ?? ""); teamOf.Add(p.Team == null ? "" : p.Team.TeamShortName ?? "");
        }
        bool ready = frameReady && pendingSeq < 0 && outgoing.Count == 0;
        float y = rect.y + 40, width = rect.width - 24;
        float bw = Math.Min(120, Math.Max(70, (width * .5f - 8 * teams.Count) / Math.Max(1, teams.Count)));
        var view = new Rect(rect.x, y, rect.width, rect.yMax - y);
        float total = names.Count * 40;
        pickerScroll = GUI.BeginScrollView(view, pickerScroll, new Rect(0, 0, width, Math.Max(total, 1)), false, total > view.height);
        try
        {
            float cy = 0;
            for (int i = 0; i < names.Count; i++)
            {
                string label = names[i] + (teamOf[i].Length > 0 ? "   " + teamOf[i] : "   spectator") + (players[i] == me ? "   (you)" : "");
                GUI.Label(new Rect(4, cy + 6, width - 8 - teams.Count * (bw + 8), 28), label, bodyStyle);
                float bx = width - teams.Count * (bw + 8);
                foreach (var team in teams)
                {
                    bool current = team.Key.Equals(teamOf[i], StringComparison.OrdinalIgnoreCase);
                    if (Button(new Rect(bx, cy + 2, bw, 34), team.Key, current, ready))
                    {
                        string player = TargetToken(names[i], names);
                        string text = cmd.Text + " " + (teamFirst ? team.Key + " " + player : player + " " + team.Key);
                        pickerCmd = null; pickerArgs.Clear(); AutoClose(mod); SendCommand(text, mod); return;
                    }
                    bx += bw + 8;
                }
                cy += 40;
            }
            if (names.Count == 0) GUI.Label(new Rect(4, 4, width - 8, 40), "No players found.", bodyStyle);
            else if (teams.Count == 0) GUI.Label(new Rect(4, 4, width - 8, 40), "No teams in this match.", bodyStyle);
        }
        finally { GUI.EndScrollView(); }
    }
    // The teams of the current match (active strategy setups); the value is the TeamShortName the admin commands accept.
    private List<KeyValuePair<string, string>> TeamRows()
    {
        var rows = new List<KeyValuePair<string, string>>();
        try
        {
            var strategy = GameMode.CurrentGameMode == null ? null : GameMode.CurrentGameMode.TryCast<MP_Strategy>();
            if (strategy != null && strategy.TeamSetups != null)
                foreach (var setup in strategy.TeamSetups)
                {
                    if (setup == null || setup.Team == null || !strategy.GetTeamSetupActive(setup)) continue;
                    var team = setup.Team;
                    rows.Add(new(team.TeamShortName, team.TeamShortName + "   " + team.GetNumPlayers().ToString(CultureInfo.InvariantCulture) + " players"));
                }
            if (rows.Count == 0)
                foreach (var team in Team.Teams)
                    if (team != null && (team.TeamShortName == "Sol" || team.TeamShortName == "Centauri" || team.TeamShortName == "Alien"))
                        rows.Add(new(team.TeamShortName, team.TeamShortName));
        }
        catch (Exception error) { Write("Team list unavailable: " + error.Message); }
        return rows;
    }
    // Admin commands take one whitespace-free token and match it by substring, uniquely: pick the longest word of the name
    // that no other player's name contains; fall back to the name without spaces.
    private static string TargetToken(string name, List<string> all)
    {
        var words = name.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).OrderByDescending(w => w.Length).ToList();
        foreach (var word in words) if (all.Count(n => n.Contains(word)) == 1) return word;
        return name.Replace(" ", "");
    }
    private void RenderInfo(Rect rect)
    {
        var mods = frame.Mods.Where(m => m.Id != "_match").OrderBy(m => m.Priority).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        float width = rect.width - 24, total = 0;
        var entries = new List<(string text, GUIStyle style, float h)>();
        foreach (var mod in mods)
        {
            entries.Add((mod.Name + (mod.Version.Length > 0 ? "  " + mod.Version : ""), titleStyle, 34));
            foreach (var cmd in mod.Cmds)
            {
                string text = cmd.Text + (cmd.Aliases.Count > 0 ? "  (" + string.Join(", ", cmd.Aliases) + ")" : "") + (cmd.Admin ? "   admin: " + cmd.Power : "") + (cmd.Label != cmd.Text ? "   " + cmd.Label : "");
                entries.Add((text, bodyStyle, bodyStyle.CalcHeight(new GUIContent(text), width - 20) + 4));
                if (cmd.Desc.Length > 0) entries.Add((cmd.Desc, smallStyle, smallStyle.CalcHeight(new GUIContent(cmd.Desc), width - 40) + 6));
            }
            entries.Add(("", smallStyle, 10));
        }
        total = entries.Sum(e => e.h);
        infoScroll = GUI.BeginScrollView(rect, infoScroll, new Rect(0, 0, width, Math.Max(total, 1)), false, total > rect.height);
        try
        {
            float y = 0;
            foreach (var e in entries) { GUI.Label(new Rect(e.style == smallStyle ? 24 : e.style == bodyStyle ? 12 : 0, y, width - 30, e.h), e.text, e.style); y += e.h; }
            if (mods.Count == 0) GUI.Label(new Rect(4, 4, width, 40), frameReady ? "The server published no commands." : "Waiting for the server's mod list...", bodyStyle);
        }
        finally { GUI.EndScrollView(); }
    }
    private void RenderVote()
    {
        float height = 96 + voteOptions.Count * 40;
        var rect = LayoutRect("vote", 420, height, 1f, 0f, 300, 120);
        voteRect = rect;
        Surface(rect, surface); Surface(new Rect(rect.x + 12, rect.y, rect.width - 24, 2), accent);
        PlaceWindow("vote", rect, rect.width - 24, 300, 120);
        GUI.Label(new Rect(rect.x + 14, rect.y + 8, rect.width - 28, 30), "VOTE", titleStyle);
        GUI.Label(new Rect(rect.x + 14, rect.y + 40, rect.width - 28, 44), voteQuestion, bodyStyle);
        float y = rect.y + 88; bool ready = frameReady && pendingSeq < 0;
        foreach (var option in voteOptions)
        {
            if (Button(new Rect(rect.x + 14, y, rect.width - 28, 34), option.Key + "   " + option.Value, false, ready)) SendCommand(option.Key, null, true);
            y += 40;
        }
    }
}
