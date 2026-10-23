# Building a UI from a Si_ModUI frame

Rules for anyone who writes a client (ours in `Client/`, Davey's, a web dashboard) against the
server contract in `README.md`. The server is the product; this document says how to turn
what it sends into a usable UI without knowing anything about the individual mods.

## 1. Mental model

The frame is a **declarative description of what this player may do on this server**, not a
UI layout. The server owns the truth: which mods exist, which commands the player may run,
what the current value of every switch is. The client owns only presentation state (open
tab, selected mod, half-filled picker, scroll position) and a cache of the last frame.

Three consequences:

- **UI = f(frame, reply, local state).** Rebuild the view from the frame every time; never
  keep derived copies that can drift. A frame diff replaces whole mod entries by `id`.
- **The client invents nothing.** Every outgoing command is the `t` of an entry the frame
  contains, optionally followed by tokens the frame told you how to collect. The only free
  text is a `text` picker stage or the Arguments field, and both still hang off a frame entry.
- **Unknown is ignored, never fatal.** New fields on a command, new keys in `data`, new
  event names: skip them and render what you understand. Old clients must keep working when
  the server grows.

## 2. The node tree

```
Frame
 └ Mod (id, name, ver, cat, prio, nav, hash, data?)
    ├ data.info[]        status lines
    ├ data.catalog       faction > category > [name, cost, label?]  (+ admin, all, credits, offText flags)
    ├ data.browse        section > entry > group > [label, value, vanilla?, note?]
    └ cmds[] (ordered by o)
       └ Entry (t, l, d, a, p, x, arg, ch, v, o)
```

`cat` groups mods into tabs (`player`, `admin`, `info`, `match`); `_match` is a pseudo mod
of match-control admin commands and goes first on the admin tab. `a` marks an admin entry;
the server never sends admin entries to non-admins, so the client does not filter by power,
it only separates them visually.

## 3. Entry kind: decided by fields, in this order

| Fields present | Kind | Render | Sends |
|---|---|---|---|
| `v` and `ch` | **Switch** | label + one button per choice, the one equal to `v` highlighted | `t + " " + value`, inline |
| `v`, `ch` and `m: true` | **Multi-select** | like Switch, but `v` lists every selected value (comma separated) and each listed one is highlighted; selected buttons stay clickable | `t + " " + value`, inline; the mod toggles that value |
| `v`, no `ch` | **Value** | label with current value, text field prefilled with `v`, SET | `t + " " + field`, inline |
| `ch`, no `v` | **Choice** | one-stage picker with the choices | `t + " " + value`, page |
| `arg` non-empty | **Chain** | one picker per stage (section 5) | `t + " " + tokens`, page |
| none of the above | **Action** | button | `t`, page |

Rules that follow from the table:

- The decision is made per entry and does not depend on the mod. A mod may mix all kinds.
- `v` is a string; compare choices by exact string match, fall back to case-insensitive.
- A `Switch` or `Value` is **inline**: the reply is shown under the list and the entry stays
  on screen, because the point is to flip and see the state. The server pushes a frame
  right behind the reply, so highlight optimistically and let the next frame overwrite.
- `Action`, `Choice` and `Chain` open a **reply page** (section 6), because their output is
  a document (status, list, menu).
- `x` are aliases: show them on the info tab, never render them as separate buttons.
- `p` is the power name for display only.

## 4. Mod page layout

Top to bottom, every element optional:

1. `data.info` lines (status). Plain text, keep order.
2. `data.tables`: grids (`n` title, `h` headers, `r` rows of cells), columns sized by content and
   scaled to the page width. Shared tables come from the mod's export, personal ones (a player's
   own stats) from the per-player hook; the client does not distinguish them.
3. Default view, if the mod has one: `catalog` renders as a **grid** for the player's own
   faction (one click = `cmd + " " + name`; with `"all": true` one tab per faction, with
   `"admin": true` on the admin tab, item `label` shown when present, cost hidden when 0);
   `browse` renders as a **tree** (sections > entries > stat table). When a default view
   exists, the command list sits behind a COMMANDS button and the view behind a
   BROWSE / CATALOG button.
3. Command list, ordered by `o` ascending (exports keep their order, registry commands are
   1000 and come last), player entries first, then a divider, then admin entries.
4. Inline reply footer (last inline reply, wrapped).

Mod list: ordered by `prio` within the tab. Do not reorder by name. Do not hide mods without
commands if they carry `data`. A client may let the user reorder the list (the reference client
drags rows); that order is local state, stored per server and tab, never sent to the server,
and mods the stored order does not know follow in server order. A mod whose `data.enabled` is `false` is switched off: the
player tab hides it, the admin tab shows it with an "(off)" mark so it can be switched back on.

Per-player rows: a switch or value row may arrive with `v` already personalised by the server
(the mod's `ModUIPlayerData(steamId)` hook, for example Elo's `/flex`). The client needs no
special handling, the row simply carries this player's state; a row with an empty `v` is a
switch whose state the server does not know for this player.

Brief replies: a reply of one or two lines that contains no option tokens and no error is
shown as the inline footer under the command list, not as a page. The user sees what
happened ("Flex ON: ...") without leaving the list. Longer replies and every reply with
options open the reply page.

## 5. Picker chains

`arg` is a comma list of stages. Each stage yields exactly one token, in order:

| Stage | Source | Token |
|---|---|---|
| `player` | players on the server (client side) | longest unique word of the name |
| `team` | teams of the current match (client side) | TeamShortName |
| `map` | server-sent `ch` on the entry (file name / display name / modes) | file name |
| `mode` | third element of the chosen map's `ch` row (`"MP_Strategy,MP_Siege"`) | mode object name |
| `ban` | server-sent `ch` | Steam64 ID (admin frames only) |
| `choice` | `ch` on the entry | value |
| `text` | typed, OK | the text |

A trailing `?` on any stage (`text?`, `mode?`) makes it optional: the client offers DEFAULT
(or SKIP for text) and sends the command without that token. An optional stage must be last.
`!map` and `/nominate` are `map,mode?`: pick the map, then one of its modes or DEFAULT.

Rules: BACK steps one stage back, never resets. The command is sent once after the last
stage. If the Arguments field is non-empty, skip all pickers and send `t + " " + field`.
`player,team` and `team,player` may be rendered as a matrix (rows = players, columns =
teams) sending both tokens in chain order; this is an optimisation, the stage-by-stage
form is always correct.

Why "longest unique word": admin commands take one token and match by substring, so a
player called `[TAG] Fresh Fish` is addressed as `Fresh` if that is unique.

## 6. Reply pages

A reply is `lines[]` of captured chat plus `err`. Rendering rules:

1. Strip rich-text tags (`<b>`, `<color=...>`) before parsing; render plain.
2. **Option tokens**: `/1`, `.1`, `!1` anywhere, or `1.` / `1)` followed by whitespace,
   several per line allowed. Each becomes a button sending `nav + number`. Text inside
   parentheses is masked first so hints like `(.1 .. or !b 1)` are not options. A single
   token that is not at the start of a line is text.
3. **Param rows**: an option whose label contains ` = ` renders as label + value field +
   SET and sends `nav + number + " " + value`.
4. **Usage lines**: `a | b | c` alternatives of bare words after the command root become
   buttons sending `root + " " + word`; alternatives with `<placeholders>` stay text.
5. Always offer BACK (`nav + "back"`), CLOSE (`nav + "0"`) and RERUN.
6. **Auto-close**: before switching mod, leaving the page or closing the panel, send
   `nav + "0"` silently if a server-side menu may be open. Server menus are exclusive per
   player; a forgotten one blocks the next.
7. An `err` or a line matching `Too few arguments` / `Usage:` means the entry was sent
   without the tokens it needs: show it, and treat it as a bug in the manifest or export
   (section 8), not as something the user must type around.

## 7. Session state machine

```
Disconnected --scene loaded/server changed--> HelloSent(nonce, cached hashes)
HelloSent --frame same--> Ready(cached frame)
HelloSent --frame diff--> Ready(merged frame, cache written)
Ready --command--> Ready(pending seq) --reply(seq)--> Ready
Ready --frame diff--> Ready(merged)                (server-initiated, any time)
Ready --event vote--> Ready(vote open) --vote-end / dur--> Ready
Ready --rehello or unknown nonce--> HelloSent      (server restarted)
```

- One pending command queue, at most 4, 200 ms apart (mirrors the server limit).
- `seq` matches replies to commands; a reply for an unknown `seq` is dropped.
- Cache the frame per server name, not per server Steam ID (the ID is fresh per restart).
- Persist: frame cache, window layout. Do not persist: picker progress, pending commands,
  optimistic values.

## 8. What mod authors must guarantee (so the rules above hold)

These are the export rules the server cannot enforce; breaking them shows up as UI bugs.

- **Deterministic exports.** `ModUIData()` must return the same document for the same
  state. No timers, uptime, tick counters or "credits now" in `info`: every change rehashes
  the mod and pushes a diff to every client. (Seen 2026-10-21: the root hash flipped between
  two values on every command because one export is not stable.)
- **Own text for every switch and admin entry.** `/elo matchmaking on|off`, not a bare
  `/elo` with choices: a bare registered command is already a player entry and the admin
  decoration is dropped. Convention: `<cmd> enabled on|off` for the master switch.
- **Every argument-taking command declares its tokens** (`arg`, `ch`, or a manifest entry).
  An action that answers "Too few arguments" is a missing declaration (today: `!banid` needs
  `text,text?`).
- **Reply synchronously, to the caller.** Only what the handler prints to the calling
  player inside the call is captured. Coroutine output lands in native chat.
- **Values carry their unit in the label** (`Radius (m)`), not in the value.
- **Descriptions are plain text**, no `<...>` (tags are stripped) and under ~120 chars.
- **Identities only in admin entries.** Steam IDs in player-visible labels, choices or data
  are scrubbed to `[id]` by the server and logged; design the export so it never happens.
- **The document is public.** Everything outside admin-flagged `commands` entries reaches
  every connected player: `info`, `catalog`, `browse`, values. No secrets (webhook URLs,
  tokens, file paths), no private player data. The `a` flag hides a button; it does not
  authorise anything. Authorisation stays in the mod's handler, which runs for the real
  sender exactly as it does for typed chat.
- **`info` is status, not help.** Up to about five short lines; help goes in `d`.
- **Idempotent and cheap.** The frame is rebuilt after every command; an export that takes
  longer than a few ms should cache on its own state (Unit Balance caches on config mtime).

## 9. Conformance levels for a client

**Minimal** (one evening of work): hello/diff/cache, tabs by `cat`, mod list by `prio`,
Action buttons, reply page with option tokens and BACK/CLOSE, vote panel, rehello. Every
mod is usable through its chat menus.

**Standard**: plus Switch, Value, Choice, Chain pickers, `info` lines, inline footer,
auto-close, usage buttons, optimistic switch highlight.

**Full** (the reference client): plus catalog grid, browse tree with CHANGES ONLY, matrix
picker, text stages, overlay over native menus, per-server layout persistence.

## 10. Open items (2026-10-21)

- In-game verification of 1.2.x: value rows, switch rows, matrix picker, text stages
  (`/buy give`, `/buy bounty add`), F6 over the faction and vote screens, admin pages of
  Logistics / KoH / HeavyTeleporter / CrabCannon, databomb pickers with a second player.
- Done 2026-10-21 13:50: manifest picker chains for `!banid` (`text,text?`), `!resources`,
  `!spawn`, `!addconfig`, `!loadconfig`, `!saveconfig`, `!time`; export audit of all nine of
  our mods (CrabCannon `/ccaim angle|speed` handler forms added, live super charges and KoH
  capture progress removed from `info` as the hash-flip sources, Elo `set`/`reset`,
  Leaderboard `reset`/faction choices, RTS AI `/rtsai enabled` master switch, Unit Balance
  `!rebalance default` + tech cost / health switch / decay randomisation / range mults /
  projectile overrides in the browser). Rebuilt DLLs wait in `ModsToUpdate`.
- Still unexported handler forms that need their own sub-command word first: HeavyTeleporter
  `/st <player>` and `/st <x> <z>`, Leaderboard `/lboard <name>`, RTS AI per-team `override`.
- Contract freeze: version the frame (`"v": 2` on the frame document) before Davey or
  anyone else builds against it; publish `README.md` + this file in the GitHub repo
  (remote still missing).
- Parked by user decision: Unit Balance admin editing stays on the proxied `!b` menu;
  drag/drop mod ordering.
