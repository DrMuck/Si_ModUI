# Builds docs/manual/Silica_ModMenu_Client_Install_Guide.pdf (player-facing install guide for the client mod menu).
# Run: python docs/manual/build_manual.py   (reportlab + pillow; images in docs/manual/img)
import os
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import mm
from reportlab.lib import colors
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.enums import TA_LEFT
from reportlab.platypus import (SimpleDocTemplate, Paragraph, Spacer, Image, Table, TableStyle,
                                PageBreak, ListFlowable, ListItem, KeepTogether)
from PIL import Image as PILImage

HERE = os.path.dirname(os.path.abspath(__file__))
IMG = os.path.join(HERE, "img")
OUT = os.path.join(HERE, "Silica_ModMenu_Client_Install_Guide.pdf")

DATE = "24 October 2026"
ML_VERSION = "0.7.3"
OUR_VERSION = "1.5.1"
DAVEY_VERSION = "0.18.0"
LINK_ML = "https://github.com/LavaGang/MelonLoader/releases/latest"
LINK_OURS = "https://github.com/DrMuck/Si_ModUI/releases/latest"
LINK_DAVEY = "https://github.com/daveyvde-1988/Si_ClientModUI/releases/tag/Silica"

styles = getSampleStyleSheet()
BODY = ParagraphStyle("body", parent=styles["Normal"], fontName="Helvetica", fontSize=10, leading=14, spaceAfter=4)
SMALL = ParagraphStyle("small", parent=BODY, fontSize=8.5, leading=11, textColor=colors.HexColor("#444444"))
H1 = ParagraphStyle("h1", parent=styles["Heading1"], fontName="Helvetica-Bold", fontSize=18, leading=22, spaceBefore=6, spaceAfter=8, textColor=colors.HexColor("#7a1f2b"))
H2 = ParagraphStyle("h2", parent=styles["Heading2"], fontName="Helvetica-Bold", fontSize=13, leading=16, spaceBefore=12, spaceAfter=6, textColor=colors.HexColor("#7a1f2b"))
H3 = ParagraphStyle("h3", parent=styles["Heading3"], fontName="Helvetica-Bold", fontSize=10.5, leading=13, spaceBefore=8, spaceAfter=3)
TITLE = ParagraphStyle("title", parent=styles["Title"], fontName="Helvetica-Bold", fontSize=24, leading=28, alignment=TA_LEFT, spaceAfter=4, textColor=colors.HexColor("#7a1f2b"))
CODE = ParagraphStyle("code", parent=BODY, fontName="Courier", fontSize=9, leading=12, backColor=colors.HexColor("#f2f2f2"), borderPadding=(3, 4, 3, 4), leftIndent=4, spaceBefore=2, spaceAfter=6)
CAPTION = ParagraphStyle("caption", parent=SMALL, spaceBefore=2, spaceAfter=10)
BOX = ParagraphStyle("box", parent=BODY, backColor=colors.HexColor("#fff4e5"), borderColor=colors.HexColor("#e0a040"), borderWidth=0.8, borderPadding=(6, 8, 6, 8), spaceBefore=6, spaceAfter=10)

PAGE_W = A4[0] - 2 * 18 * mm


def P(text, style=BODY):
    return Paragraph(text, style)


TMP = os.path.join(HERE, "_build")
os.makedirs(TMP, exist_ok=True)


def picture(name, width=PAGE_W, caption=None, crop=None):
    # crop = (left, top, right, bottom) in source pixels; the screenshot is re-encoded as JPEG to keep the PDF small
    src = PILImage.open(os.path.join(IMG, name)).convert("RGB")
    if crop:
        src = src.crop(crop)
    path = os.path.join(TMP, os.path.splitext(name)[0] + ".jpg")
    src.save(path, "JPEG", quality=85, optimize=True)
    w, h = src.size
    scale = width / w
    items = [Image(path, width=w * scale, height=h * scale)]
    if caption:
        items.append(P(caption, CAPTION))
    return KeepTogether(items)


def bullets(items, style=BODY):
    return ListFlowable([ListItem(P(t, style), leftIndent=12) for t in items], bulletType="bullet", start="\u2022", leftIndent=14, bulletFontSize=8)


def numbered(items, style=BODY):
    return ListFlowable([ListItem(P(t, style), leftIndent=14) for t in items], bulletType="1", leftIndent=16)


def footer(canvas, doc):
    canvas.saveState()
    canvas.setFont("Helvetica", 8)
    canvas.setFillColor(colors.HexColor("#777777"))
    canvas.drawString(18 * mm, 12 * mm, "Silica Mod Menu - Client Install Guide - " + DATE)
    canvas.drawRightString(A4[0] - 18 * mm, 12 * mm, "Page %d" % doc.page)
    canvas.restoreState()


story = []

# ---------------------------------------------------------------- title
story += [
    P("Silica Mod Menu", TITLE),
    P("Client install guide: MelonLoader + mod menu", H2),
    P("For players on servers that run the <b>Si_ModUI_Server</b> companion (Prime Battalion, DrMuck's test server). "
      "Version of this guide: " + DATE + ". MelonLoader " + ML_VERSION + ", Si_ModUI client " + OUR_VERSION + ", Si_ClientModUI " + DAVEY_VERSION + "."),
    Spacer(1, 6),
    P("<b>What you get.</b> An in-game menu on <b>F6</b> for everything the server's mods offer: buy units with one click, "
      "nominate maps and rock the vote, set your Elo seat preferences, read the leaderboard, open the Unit Balance browser, "
      "and for admins kick, ban, move players, pin commanders and more. The menu shows exactly what the server offers you, "
      "so it stays correct when mods change."),
    P("<b>No client mod?</b> Type <font face='Courier'>/mods</font> in chat. It lists the same mods and commands as text: "
      "<font face='Courier'>/mods</font> (what you can do), <font face='Courier'>/mods info</font> (what only reports), "
      "<font face='Courier'>/mods &lt;name&gt;</font> (one mod), then <font face='Courier'>/1</font>, <font face='Courier'>/2</font> ... "
      "to open an entry, <font face='Courier'>/back</font> and <font face='Courier'>/0</font> to leave. Nothing to install."),
    P("<b>Overview of the steps</b>", H3),
    numbered([
        "Install MelonLoader " + ML_VERSION + " into your Silica folder and start the game once.",
        "Pick <b>one</b> mod menu (DrMuck's Si_ModUI or Davey's Si_ClientModUI) and drop its DLL into <font face='Courier'>Silica\\Mods</font>.",
        "Join the server and press F6.",
    ]),
    P("Time needed: about 10 minutes, most of it the first start of the game after installing MelonLoader.", SMALL),
]

# ---------------------------------------------------------------- choose
story += [
    P("Which mod menu?", H1),
    P("Both clients speak the same server protocol (SiModUI/2), both open with F6, and you can switch between them at any time "
      "by swapping the DLL. Install only one: with both in the Mods folder they fight over the F6 key."),
]
choice = Table([
    [P("<b>Si_ModUI (DrMuck)</b> " + OUR_VERSION, BODY), P("<b>Si_ClientModUI (Davey)</b> " + DAVEY_VERSION, BODY)],
    [P("One compact panel: PLAYER / ADMIN / INFO tabs, mods on the left, commands or the Buy grid on the right. "
       "Pickers for players, teams and maps. Drag mods to reorder. Written by the author of the server companion, "
       "so new server features show up here first.", BODY),
     P("A fuller mod hub: HELP, COMMANDS, MODS and SETTINGS pages, status header, your own sidebar shortcuts, client diagnostics. "
       "Also works standalone and in singleplayer; the server companion adds the server's commands and controls. "
       "F6 once expands the header, F6 again opens the menu.", BODY)],
    [P("Download: <link href='" + LINK_OURS + "' color='blue'>" + LINK_OURS + "</link>", SMALL),
     P("Download: <link href='" + LINK_DAVEY + "' color='blue'>" + LINK_DAVEY + "</link>", SMALL)],
], colWidths=[PAGE_W / 2 - 3, PAGE_W / 2 - 3])
choice.setStyle(TableStyle([
    ("BOX", (0, 0), (-1, -1), 0.6, colors.HexColor("#bbbbbb")),
    ("INNERGRID", (0, 0), (-1, -1), 0.4, colors.HexColor("#dddddd")),
    ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#f5e6e8")),
    ("VALIGN", (0, 0), (-1, -1), "TOP"),
    ("LEFTPADDING", (0, 0), (-1, -1), 6), ("RIGHTPADDING", (0, 0), (-1, -1), 6),
    ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
]))
story += [choice, Spacer(1, 8),
          picture("modui_drmuck.png", PAGE_W * 0.72, "Si_ModUI (DrMuck): Buy grid for your faction, one click buys the unit.", crop=(150, 190, 1200, 965)),
          picture("modui_davey.png", PAGE_W * 0.72, "Si_ClientModUI (Davey): MODS page with the same Buy catalog, plus HELP / COMMANDS / SETTINGS pages.", crop=(75, 195, 1275, 1105))]

# ---------------------------------------------------------------- before you start
story += [
    P("Before you start", H1),
    bullets([
        "<b>Find your Silica folder.</b> Steam &gt; Library &gt; right-click Silica &gt; Manage &gt; Browse local files. "
        "Typical paths: <font face='Courier'>C:\\Program Files (x86)\\Steam\\steamapps\\common\\Silica</font> or "
        "<font face='Courier'>D:\\SteamLibrary\\steamapps\\common\\Silica</font>. Everything below happens in that folder.",
        "<b>Match the server's Steam branch.</b> Prime Battalion runs the normal (default) branch. DrMuck's test server runs the "
        "<i>test</i> beta branch (Steam &gt; Properties &gt; Betas). The mod DLLs are the same for both branches; only the game "
        "has to match the server, otherwise the server browser says \"different version\".",
        "<b>Close Silica</b> while you install.",
        "Windows only. Everything here is a normal file copy; nothing is changed in Steam itself.",
    ]),
]

# ---------------------------------------------------------------- step 1
story += [
    P("Step 1: Install MelonLoader " + ML_VERSION, H1),
    numbered([
        "Download <b>MelonLoader.Installer.exe</b> from <link href='" + LINK_ML + "' color='blue'>" + LINK_ML + "</link> and run it. "
        "Windows SmartScreen may ask once; choose \"More info\" &gt; \"Run anyway\".",
        "The installer lists installed games. Pick <b>Silica</b>, or use the folder icon and select <font face='Courier'>Silica.exe</font> in your Silica folder.",
        "Select version <b>" + ML_VERSION + "</b> (the version both servers run). Leave \"Keep mods and settings\" ticked. "
        "If " + ML_VERSION + " is not in the list, tick \"Enable Nightly builds\" and take the newest " + ML_VERSION + " build, as in the screenshot.",
        "Click <b>Install</b> and wait for \"Installed\".",
        "<b>Start Silica once</b> from Steam. A black MelonLoader console opens next to the game and the first start takes a few minutes: "
        "MelonLoader generates its assemblies for the game. Wait for the main menu, then quit the game.",
    ]),
    picture("melonloader_installer.png", PAGE_W * 0.4, "MelonLoader installer after a successful install: Silica selected, version 0.7.3 (nightly builds enabled, which is fine), Keep mods and settings on.", crop=(0, 0, 500, 725)),
    P("After this your Silica folder contains <font face='Courier'>version.dll</font>, a <font face='Courier'>MelonLoader</font> folder, "
      "a <font face='Courier'>Mods</font> folder and a <font face='Courier'>UserData</font> folder. If <font face='Courier'>version.dll</font> "
      "is missing, read Troubleshooting below: your antivirus removed it.", BODY),
]

# ---------------------------------------------------------------- step 2
story += [
    P("Step 2: Install the mod menu", H1),
    P("Option A: Si_ModUI (DrMuck)", H2),
    numbered([
        "Download the client package from <link href='" + LINK_OURS + "' color='blue'>" + LINK_OURS + "</link> "
        "(<font face='Courier'>Si_ModUI_client_" + OUR_VERSION + ".zip</font>).",
        "Open the zip and copy <font face='Courier'>Mods\\Si_ModUI.dll</font> into your <font face='Courier'>Silica\\Mods</font> folder.",
        "If an older <font face='Courier'>Si_SquadHUD.dll</font> or <font face='Courier'>Si_ClientModUI.dll</font> is in the Mods folder, remove it (or move it out of Mods).",
    ]),
    P("Option B: Si_ClientModUI (Davey)", H2),
    numbered([
        "Download <font face='Courier'>Si_ClientModUI.dll</font> from <link href='" + LINK_DAVEY + "' color='blue'>" + LINK_DAVEY + "</link>.",
        "Copy it into your <font face='Courier'>Silica\\Mods</font> folder.",
        "Remove an older <font face='Courier'>Si_SquadHUD.dll</font> or a <font face='Courier'>Si_ModUI.dll</font> from the Mods folder.",
    ]),
    P("Your Mods folder then holds exactly one of the two DLLs. Other client mods you may have (Si_4way.dll for the 4-way events, for example) can stay.", BODY),
    P("Step 3: In the game", H1),
    bullets([
        "Join the server and press <b>F6</b>. Si_ModUI opens its panel at once; Si_ClientModUI expands its header on the first F6 and opens the menu on the second.",
        "<b>Escape</b> or CLOSE closes the menu. While it is open, the game's own screens under it receive no clicks.",
        "F6 is ignored while the pause menu, the main menu or the chat input is open. It works during the faction and role screen and during votes.",
        "Si_ModUI: PLAYER tab = the server's mods in order; click a mod, then a command. Buy shows the unit grid of your faction; a click buys. "
        "Commands with arguments open pickers (players, teams, maps). INFO lists every command with its description. "
        "ADMIN appears only if the server granted you admin powers. Drag mods in the list to reorder them; \"reset order\" restores the default.",
        "Replies of a command appear inside the menu, not in chat. Numbered lines in a reply are buttons.",
        "Everything the menu does is a normal chat command of the server. Nothing you can do in the menu is impossible in chat, and the server's permissions apply exactly as for typed commands.",
    ]),
]

# ---------------------------------------------------------------- troubleshooting
story += [
    P("Troubleshooting", H1),
    P("The game does not start after installing MelonLoader", H3),
    numbered([
        "Delete <font face='Courier'>version.dll</font> in your Silica folder.",
        "Start the game once without it, then quit.",
        "Run the MelonLoader installer again and reinstall " + ML_VERSION + ".",
    ]),
    P("There is no version.dll in the Silica folder, or it disappears again", H3),
    P("Your antivirus quarantined it (Windows Defender does this for some MelonLoader builds). Add the Silica folder to the Defender exclusions, then reinstall MelonLoader:"),
    numbered([
        "Windows Security &gt; Virus &amp; threat protection &gt; Virus &amp; threat protection settings &gt; Manage settings.",
        "Scroll to Exclusions &gt; Add or remove exclusions &gt; Add an exclusion &gt; Folder &gt; pick your Silica folder.",
        "Run the MelonLoader installer again. Other antivirus products have an equivalent exclusion list.",
    ]),
    picture("defender_exclusions.png", PAGE_W * 0.8, "Windows Security with the Silica folder added as an exclusion.", crop=(0, 0, 1000, 320)),
    P("Windows Firewall asks about Silica.exe, or servers no longer show up", H3),
    P("The first start with MelonLoader can trigger a new firewall prompt for Silica.exe because the loader changes the process. Allow it for private networks. "
      "If you clicked Cancel, open Windows Defender Firewall &gt; Allow an app or feature through Windows Defender Firewall, find Silica and tick Private (and Public if you play on such networks)."),
    P("The first start takes minutes or shows only a black console window", H3),
    P("Normal. MelonLoader generates its assemblies on the first start after an install and after every game update. Wait for the main menu; later starts are quick."),
    P("F6 does nothing", H3),
    bullets([
        "Open <font face='Courier'>Silica\\MelonLoader\\Latest.log</font>. It must contain a line with <font face='Courier'>Si_ModUI v" + OUR_VERSION + "</font> "
        "(or <font face='Courier'>Si_ClientModUI</font>) and, for Si_ModUI, <font face='Courier'>READY - F6 opens the mod menu</font>. If the mod is missing, the DLL is not in the Mods folder or was blocked by the antivirus.",
        "Close the pause menu, the main menu or the chat input first; F6 is ignored while they are open.",
        "Both client DLLs installed? Remove one.",
        "Any other mod or overlay bound to F6 (recording tools, Discord overlays) may swallow the key.",
    ]),
    P("The menu opens but shows no mods (or \"0 mods\")", H3),
    P("The server does not run the Si_ModUI_Server companion, or you joined before it finished starting. Reconnect. On servers without the companion the menu stays empty; "
      "the chat command <font face='Courier'>/mods</font> works on every server that has it, client mod or not."),
    P("\"Different version\" when joining", H3),
    P("Your Steam branch does not match the server. Steam &gt; Silica &gt; Properties &gt; Betas: \"None\" for Prime Battalion, \"test\" for DrMuck's test server. The mods need no change."),
    P("The game updated and the menu or the game broke", H3),
    P("After larger game updates MelonLoader has to be reinstalled (run the installer again, same version), and client mods may need a new build. Check the server's Discord for the current DLLs."),
    P("Removing everything", H3),
    P("Run the MelonLoader installer and choose <b>Uninstall</b>, or delete <font face='Courier'>version.dll</font>, the <font face='Courier'>MelonLoader</font> folder and the "
      "<font face='Courier'>Mods</font> folder from your Silica folder. Steam &gt; Properties &gt; Installed Files &gt; Verify integrity restores a clean game."),
    Spacer(1, 10),
    P("<b>Links</b><br/>"
      "MelonLoader: <link href='" + LINK_ML + "' color='blue'>" + LINK_ML + "</link><br/>"
      "Si_ModUI client (DrMuck): <link href='" + LINK_OURS + "' color='blue'>" + LINK_OURS + "</link><br/>"
      "Si_ClientModUI (Davey): <link href='" + LINK_DAVEY + "' color='blue'>" + LINK_DAVEY + "</link><br/>"
      "Server companion and protocol: <link href='https://github.com/DrMuck/Si_ModUI' color='blue'>https://github.com/DrMuck/Si_ModUI</link>", BOX),
    P("Security note: the client mod never runs code, opens files or follows links sent by a server. It only renders what the server advertises and sends chat commands you click. "
      "The server applies the same permission checks as for typed chat. Install mods only from the links above.", SMALL),
]

doc = SimpleDocTemplate(OUT, pagesize=A4, leftMargin=18 * mm, rightMargin=18 * mm, topMargin=16 * mm, bottomMargin=20 * mm,
                        title="Silica Mod Menu - Client Install Guide", author="DrMuck", subject="MelonLoader + Si_ModUI / Si_ClientModUI installation for players")
doc.build(story, onFirstPage=footer, onLaterPages=footer)
print("wrote", OUT)
