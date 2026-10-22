# Si_ModUI 1.0 (redesign)

Client UI for Silica server mods, redesigned from Davey's 0.8.1 around three rules:

1. **One static frame, pushed once.** The server builds a per-player catalog of every chat command the Admin Mod registry knows, grouped by mod. It is hashed per mod and as a whole. A client that already holds the frame sends its hashes and gets back only the mods that changed. Nothing in the frame is polled.
2. **Commands only.** The client never reads mod internals. It sends one of the frame's chat commands (or a numbered menu option); the server runs it through the Admin Mod's own dispatcher for that player and returns whatever the mod printed to that player. No per-mod adapters, no DLL hash pins.
3. **The server pushes, the client never polls.** Besides the hello and commands, all traffic is server-initiated: frame diffs when mods change, vote ballots, vote end.

Admin commands are filtered on the server by the player's actual powers. A player without a power never receives that command, so nothing admin-related can be shown to non-admins.

## Layout

| Path | What |
|---|---|
| `Shared/Protocol.cs` | Wire format, chunking, hashing. Compiled into both server and client. |
| `Server/ServerMod.cs` | MelonMod: receive patch, subscriptions, frame push, rate-limited outbox, `!moduitest`. |
| `Server/Frame.cs` | Frame builder from `PlayerMethods.PlayerCommands`, `PlayerPhrases`, `AdminMethods.AdminCommands`. |
| `Server/MenuConfig.cs` | Sidecar `UserData/cfg/ModUI/menu.json` (names, categories, priorities, labels, hidden, match control). |
| `Server/Proxy.cs` | Validates a command, runs it via `Event_Chat.FireOnRequestPlayerChatEvent`, captures `NetworkSendChat`/`NetworkSendConsole` to the caller. |
| `Server/Events.cs` | Postfix on `ChatVotes.HoldVote` pushes the ballot. |
| `include/` | Reference DLLs (server Managed 1.0.1, MelonLoader net35, Si_AdminMod, Newtonsoft). |

Build: `dotnet build -c Release` in `Server/`. Deploy: copy `Si_ModUI_Server.dll` to the server `Mods/` folder (or `ModsToUpdate/` while the server runs).

## Wire format

Native packet type `126` (unused by game 1.0.1), string marker `SiModUI/2`, then one string body of at most 1800 chars. Reliable channel. Old 0.8.1 clients use marker `/1` and ignore `/2`.

Client to server, always one packet:

```
H|<nonce>|<channel>|<rootHash or empty>|<id:hash,id:hash,...  or empty>
C|<nonce>|<seq>|<base64 utf8 command text>
```

Server to client, one JSON document split into chunks:

```
T|<nonce>|<transferId>|<index>|<count>|<r|g>|<base64 bytes>      g = gzip
```

Documents (`k` field):

```json
{"k":"frame","root":"<12 hex>","same":true}
{"k":"frame","root":"<12 hex>","full":true|false,"cats":{"player":"Player",...},
 "mods":[{"id":"Si_Buy","name":"Buy","ver":"1.0.0","cat":"player","prio":10,"nav":"/","hash":"<12 hex>",
          "cmds":[{"t":"/buy","l":"Buy units","d":"...","a":false,"p":"","x":["/b"]}]}],
 "removed":["Si_Old"]}
{"k":"reply","seq":7,"cmd":"/buy","lines":["<b>...</b>","1. Sol Light Vehicle Factory", ...],"err":""}
{"k":"event","e":"vote","data":{"q":"...","opts":[{"c":"1","d":"map A"}],"dur":30}}
{"k":"event","e":"vote-end","data":null}
```

`_match` is a pseudo mod (`cat` = `match`) holding the admin commands listed in `menu.json` `MatchControl` that this player may run. Commands marked `a` are admin commands; a mod can carry both player and admin commands.

### Mod data (`data`)

A mod can publish a JSON document for the client by exposing `public static string ModUIData()` on any type of its assembly (found by assembly name = mod id, called on every frame build, `null` = no data). The parsed document is embedded as `data` in the mod's frame entry and takes part in the mod hash, so a change (for example new unit prices at round start) reaches every subscribed client as a normal diff. The server does not interpret it.

The client understands a catalog:

```json
{"cmd":"/buy","enabled":true,"shrimpLock":300,
 "catalog":{"Sol":[{"c":"Barracks","u":[["Soldier",100],["Sniper",150]]},{"c":"Light Factory","u":[...]}],
            "Centauri":[...],"Alien":[...]}}
```

The document may also list sub-commands the Admin Mod registry cannot see:

```json
{"commands":[{"t":"/rtsai status","l":"Status","d":"Switches and overrides.","a":false},
             {"t":"/rtsai enable","l":"Faction switch","d":"...","a":true,"ch":[["alien on","Alien ON"],["alien off","Alien OFF"]]}]}
```

They are merged into the mod's command list on the server (`a` = admin, sent only to admins; `ch` = choices offered as a picker and appended to the text; `arg` = picker chain, see below) and stripped from the `data` that leaves the server. An entry whose text the registry already knows (the bare `/elo`) only decorates that command with label, description and picker. Si_RTS_AI 0.94.3 and Si_EloMatchmaking 0.4.0 publish their sub-commands this way.

An entry with `v` (its current value) and no `ch` is a value setting: the client shows the label with the current value, a text field and SET, which sends `t + " " + value`. An entry with `v` (its current value) and `ch` is a switch: the client renders it as one row with a button per choice, the one matching `v` highlighted; a click sends `t + " " + value` and shows the mod's reply under the list. After every command the server rebuilds and pushes the frame, so the highlight follows the real state within a packet. With `m: true` the switch is a multi-select: `v` lists every selected value (comma separated), each listed choice is highlighted and stays clickable, and a click sends `t + " " + value` for the mod to add or remove that value (builder `Multi(...)`; Si_EloMatchmaking's `/flex` row with one button per faction). `info` (an array of strings) in the document is shown as status lines above the mod's command list. Si_EloMatchmaking 0.4.0 and Si_RTS_AI 0.94.4 use both.

`enabled` (a boolean at the top of the document) is the mod's master switch: `false` hides the mod from the player tab, admins still see it marked "(off)". Every mod of ours sets it from its own switch (`ModUIExport.New().Enabled(cfg.Enabled)`).

`tables` is a list of grids shown above the command list: `[{"n":"Commander","h":["","Rating","Rank"],"r":[["Overall","1663","#26"],["Sol","1629","#14"]]}]` (`ModUIExport.New().Table(name, headers, rows...)`). The client sizes the columns by content and scales them to the page width.

Per-player state goes through a second hook, `public static string ModUIPlayerData(ulong steamId)`, returning `{"v":{"/flex":"on"},"tables":[...]}` (`ModUIExport.ForPlayer().Value("/flex", on ? "on" : "off").Table(...).Build()`; `PlayerValues(...)` is the short form for values only). The shared frame stays cached; the server clones the named mod for that player, fills the `v` of the listed switch and value rows, appends the player's tables to the mod's `data.tables` and rehashes it, so the client sees its own state and the diff is one mod. Per-player documents are refreshed at most once a minute, or right after any command (a stats table would otherwise rehash the mod after every kill). The shared export keeps `current: ""` for such rows. Si_EloMatchmaking 0.4.3 uses it for the `/flex` switch, Si_Leaderboard 0.1.4 for the player's own stats tables.

`arg` on a command is a chain of picker stages, comma separated: `player` (players on the server, built by the client), `team` (the teams of the current match, value = TeamShortName), `map`, `mode` (the game modes of the map chosen in the `map` stage: every map row carries them as a third element `"MP_Strategy,MP_Siege"`), `ban`, `choice` (from `ch`) or `text` (a typed value with OK). A trailing `?` on any stage (`text?`, `mode?`) makes it optional: the client offers DEFAULT, which sends the command without that token. `!map` and `/nominate` are seeded as `map,mode?`. Each stage adds one token; after the last one the client sends `text + " " + tokens`. `!team` is seeded as `player,team` in `menu.json` (pick the player, then the team), `/elo seat` as `team,player`.

The client also understands a browser document for read-only reference data such as the unit balance:

```json
{"browse":{"title":"Unit balance","sections":[{"n":"Sol — Barracks","u":[
   {"n":"Rifleman","c":1,"g":[{"n":"Health & Production","r":[["Health","150"],["Build Time","15","10","x1.5"]]}]}]}]}}
```

Sections hold entries, entries hold stat groups, a row is `[label, value]` or `[label, value, vanilla, note]` when the value differs from vanilla (`c` = number of such rows). The client renders it as section list > entry list > stat table with a CHANGES ONLY filter and a COMMANDS button back to the mod's commands. Si_UnitBalance 7.10.0 publishes its whole config this way (about 80 KB raw, cached on the server until the config file changes), so players browse the balance without the `/stats` chat walk.

A mod with a catalog is rendered as a button grid for the player's own faction; a click sends one direct command, `cmd + " " + name` (for example `/buy Heavy Tank`), so no chat menu is opened and no intermediate menu pages travel over the wire. Si_Buy 1.2.0 implements both ends (`ModUIData()` plus `/buy <name>` and `/buy <category#> <item#>`).

## Client contract

- Generate a 32-hex nonce per connection. On join (and after every scene load) send `H` with the root hash and per-mod hashes stored on disk for this server (keyed by the server's Steam ID). Receive the diff, merge, store.
- Render `cats` in the order player, info, admin. Player tab: mods with `cat` = `player` ordered by `prio`, each listing its non-admin commands. Admin tab: `_match` section first, then every mod that has admin commands. Info: all commands with labels and descriptions.
- A command button sends `C` with the command text. Render `lines` of the reply as the page. A line that starts with a number followed by `.` or `)` becomes a button that sends `nav + number` (for example `/3`); add `nav + "back"` and `nav + "0"` buttons.
- A `vote` event opens a panel with one button per option sending `c` as plain text (vote options are registered phrases, no prefix). Close it on `vote-end` or after `dur` seconds.
- `{"k":"rehello"}` or an unknown nonce means the server restarted: send `H` again.
- Never send free text. Never poll.

## Server limits

- Outbox token bucket: 160 packets/s across all clients, bursts of 16. With 1.8 KB bodies that is about 290 KB/s, under the 0.5 MB/s target.
- One command per client per 200 ms, at most 4 queued; hello at most once per second.
- Accepted command text: a registered player/admin command with prefix `/ ! .`, a registered phrase, or a numbered menu option (`/1`, `.back`, `/0`).
- Captured output is only what a mod prints synchronously inside its handler to the calling player. Anything printed later (coroutines, timers) stays in native chat.

## Client

`Client/` builds `Si_ModUI.dll` (Il2Cpp, net6) for the game's `Mods/` folder. F6 opens the menu; Escape or CLOSE closes it. F6 is ignored only while the pause menu (with its settings screens), the main menu or a chat input is open. Every other native menu of a running match (battle-layout vote, faction / role screen, large map, end screen) stays open for long stretches in which pins and votes matter, so the panel opens on top of it; the game's own UI gets no clicks while the panel is up (the EventSystem is disabled and the same instance re-enabled on close). An ignored F6 is logged in the session log with its reason. The frame is cached in `UserData/Si_ModUI/frame-<server name>.json` (the server's Steam ID changes on every restart, so the configured server name is the key), window positions in `layout-<yourSteamId>.json`, and every session writes `session-*.log` with HELLO / FRAME / SEND / REPLY / VOTE lines.

- Player tab: mods in priority order; click a mod, then a command. The optional Arguments field is appended to the command (for example a player name).
- Catalog mods (Buy): the unit grid for your faction with prices; one click buys directly (`/buy <name>`), the result and your balance appear under the grid. Opening the entry fetches the balance once (`/buy status`, silent); CHAT MENU falls back to the mod's own menu.
- Browser mods (Unit Balance): sections > units > stat groups, vanilla value and multiplier next to every changed stat, CHANGES ONLY filter; COMMANDS switches to the mod's command list.
- Commands with a picker chain (`!team`, `/elo seat`, `!kick`, `!map` ...) open one picker per argument (players, teams of the match, maps, banned players or server-supplied choices) and send the complete command once the last one is chosen; BACK steps one argument back. Text in the Arguments field skips the pickers.
- Reply page: the mod's captured output; numbered lines are buttons that send `nav + number`; BACK / CLOSE MENU send `nav + back` / `nav + 0`; RERUN repeats the command.
- Admin tab: only present when the server sent admin commands; Match control first, then every mod with admin commands.
- Info tab: all commands with aliases, powers, labels and descriptions.
- Vote: a banner shows when a vote opens; with the menu open a vote panel offers one button per option.

## New mods: how their commands get a usable UI

The Admin Mod registry only knows a mod's top-level commands; what comes after (`on|off`, sub-commands, values) is parsed inside each handler and invisible from outside. The HUD builds the best UI it can from three sources, in this order:

1. **The mod's own export** (`ModUIData()`, see Mod data): sub-commands with labels, switches with their current state (`v` + `ch`), value settings (`v` without `ch`: field + SET), pickers (`arg`), status lines (`info`). Exact and stateful; about 30 lines per mod. Our mods (Buy, RTS AI, Elo, Unit Balance, Logistics) ship it.
2. **Usage text in the registered description.** For mods we do not touch (databomb's), a description like `Usage: !team [optional:<player>] <teamname>` yields pickers automatically: `<player>` and `<target>` become the player picker, `<team...>` the team picker, `<map...>` the map picker, a literal `a|b|c` group a choice. The chain stops at the first unknown token.
3. **Usage lines in replies.** When a mod answers with a help line (`!elo | !elo top | !elo <name>`, `usage: /rtsai [on|off|auto | status | mil]`), every alternative that is one bare word becomes a button sending `root word`; pieces with placeholders stay text and take the Arguments field. So the bare command of any mod is at worst a two-click walk into its sub-commands.

A registered player command whose handler checks the admin power itself (Formation's tuning knobs) gets `"AdminOnly": true` in `menu.json`: it then appears as an admin entry in admin frames only and never reaches players. `menu.json` is the manual layer for mods without an export. Per command it takes `Label`, `Description`, `Arg` (picker chain), `Choices` (`[[value,label],...]` for a first argument with fixed words) and `Subs`, a list of sub-commands the registry cannot see, each `{ "Text": "!spawn list", "Label": ..., "Description": ..., "Admin": true, "Arg": "", "Choices": [] }`. Subs attach to the parent command's mod, admin ones only in admin frames; the same merge rules as for exports apply. The server ships entries for databomb's mods written from their sources and READMEs.

For our own mods, `Export/ModUI_Export.cs` is the builder to copy into the project: `ModUIExport.New().Info(...).Action(...).OnOff(...).Value(...).Picker(...).Build()` plus `Catalog(...)` and `Browse(...)` builders for the Buy grid and the Unit Balance tree. It depends on Newtonsoft.Json only and pins the document shapes in one place; Si_EloMatchmaking, Si_RTS_AI and Si_Logistics use it.

## Security model

Both directions are treated as untrusted input.

Server to client. The client never executes, loads or opens anything the server sends: documents are JSON parsed with System.Text.Json into fixed classes, rendered through IMGUI with rich text off, and written only to the cache file under `UserData/Si_ModUI/` whose name is derived from the server name with every non-alphanumeric character replaced. Packets from any peer other than the connected server are dropped. The only actions the server can trigger are chat commands the client sends back to that same server, bounded by the same validation as typed commands. Resource limits: a document inflates to at most 4 MB (`MaxDocumentBytes`, the inflater stops at the limit), at most 512 chunks per document and 8 documents in flight, stale transfers dropped after 30 s. A compromised server therefore gains nothing over what the game protocol already grants it.

Client to server. A client can only make the server do what typing in chat does, as that player: the command text (160 chars, no control characters) must resolve to a registered command, phrase or numbered menu option, and it is dispatched through the Admin Mod's own chat event, where the Admin Mod checks `IsAdmin` and the required power on the real `Player` resolved from the packet's sender. The sender cannot name another player: the subscription is keyed by the sending Steam ID and channel and commands are matched to it by the nonce, which never leaves that connection. Per client: one command per 200 ms, four queued, one hello per second, known-hash list capped at 1400 chars; every packet is length checked before parsing and a malformed one is ignored with the stream restored.

Identities. Steam IDs (ban list, bounty list, pins) travel only in admin frames: the entries that carry them are admin entries, which the server never sends to players. As a safety net the server scrubs every player frame before it leaves: any Steam64 ID in a label, description, value, choice or data document is replaced by `[id]`, choice lists with IDs are dropped, and the mod is named once in the log.

Not covered: the HUD does not add transport encryption or authentication beyond the game's own connection, and a chat command that is itself unsafe in a mod stays unsafe through the HUD.

## Testing without the client mod

As a Generic admin in a normal game client:

```
!moduitest            writes your frame to UserData/cfg/ModUI/last_frame_<steamid>.json and reports size/packets
!moduitest /buy       runs /buy through the proxy and echoes the captured lines back as chat, prefixed [HUD]
!moduitest /1         next menu step
!moduitest rtv        a phrase
```

The server log shows the public frame (mods, commands, bytes, packets) once after start and a traffic summary every minute.
