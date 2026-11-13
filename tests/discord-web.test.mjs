import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const moduleUrl = new URL("../src/SteamLoader.App/Assets/tfs-discord.mjs", import.meta.url);
const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const hostUrl = new URL("../src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs", import.meta.url);
const bridgeUrl = new URL("../src/SteamLoader.App/Infrastructure/Discord/DiscordWebBridge.cs", import.meta.url);

const discord = await import(moduleUrl);

test("Discord Web paths only open app pages", () => {
  const guild = "123456789012345678";
  const channel = "223456789012345678";
  assert.equal(discord.buildDiscordPath({ guildId: guild }), `/channels/${guild}`);
  assert.equal(discord.buildDiscordPath({ guildId: guild, channelId: channel }), `/channels/${guild}/${channel}`);
  assert.equal(discord.buildDiscordPath({ userId: channel }), `/users/${channel}`);
  assert.equal(discord.buildDiscordPath({ path: "/channels/@me" }), "/channels/@me");
  assert.equal(discord.buildDiscordPath({ guildId: "12" }), "");
  assert.equal(discord.buildDiscordPath({ path: "https://evil.example/" }), "");
  assert.equal(discord.buildDiscordPath(), "");
});

test("Controller buttons map to Discord shortcuts the backend allows", async () => {
  const { BUTTON, BUTTON_ACTIONS } = discord;
  // Steam's GamepadButton values (pR export) checked live on 2026-11-09.
  assert.deepEqual(
    [BUTTON.OK, BUTTON.CANCEL, BUTTON.SECONDARY, BUTTON.OPTIONS, BUTTON.BUMPER_LEFT, BUTTON.BUMPER_RIGHT, BUTTON.DIR_UP, BUTTON.SELECT, BUTTON.START],
    [1, 2, 3, 4, 5, 6, 9, 13, 14],
  );
  assert.equal(BUTTON_ACTIONS[BUTTON.BUMPER_LEFT], "previous-server");
  assert.equal(BUTTON_ACTIONS[BUTTON.BUMPER_RIGHT], "next-server");
  assert.equal(BUTTON_ACTIONS[BUTTON.DIR_UP], "previous-channel");
  assert.equal(BUTTON_ACTIONS[BUTTON.DIR_DOWN], "next-channel");
  assert.equal(BUTTON_ACTIONS[BUTTON.OK], undefined, "A is a click, not a shortcut");
  assert.equal(BUTTON_ACTIONS[BUTTON.CANCEL], undefined, "B stays with the back logic");

  const bridge = await readFile(bridgeUrl, "utf8");
  const moduleSource = await readFile(moduleUrl, "utf8");
  const used = new Set([
    ...Object.values(BUTTON_ACTIONS),
    ...[...moduleSource.matchAll(/run\("([a-z-]+)"\)|onAction\("([a-z-]+)"\)/g)].map((match) => match[1] || match[2]),
  ]);
  for (const action of used) {
    assert.match(bridge, new RegExp(`\\["${action}"\\] = new\\(`), `backend whitelist should contain ${action}`);
  }
});

test("Steam Controller 2 grips scroll and jump, Steam's own buttons stay with Steam", () => {
  const { BUTTON, BUTTON_ACTIONS, BUTTON_CLICKS } = discord;
  assert.deepEqual(
    [BUTTON.RSTICK_CLICK, BUTTON.REAR_LEFT_UPPER, BUTTON.REAR_LEFT_LOWER, BUTTON.REAR_RIGHT_UPPER, BUTTON.REAR_RIGHT_LOWER, BUTTON.STEAM_GUIDE, BUTTON.STEAM_QUICK_MENU],
    [16, 23, 24, 25, 26, 27, 28],
  );
  assert.equal(BUTTON_ACTIONS[BUTTON.REAR_LEFT_UPPER], "page-up");
  assert.equal(BUTTON_ACTIONS[BUTTON.REAR_RIGHT_UPPER], "page-down");
  assert.equal(BUTTON_CLICKS[BUTTON.RSTICK_CLICK], "right");
  for (const button of [BUTTON.STEAM_GUIDE, BUTTON.STEAM_QUICK_MENU, BUTTON.OK, BUTTON.CANCEL]) {
    assert.equal(BUTTON_ACTIONS[button], undefined);
    assert.equal(BUTTON_CLICKS[button], undefined);
  }
});

test("Text fields open Steam's keyboard through the BrowserView's keyboard reference", async () => {
  const calls = [];
  const keyboard = {
    ShowVirtualKeyboard() { calls.push(["show", this === keyboard]); },
    HideVirtualKeyboard() { calls.push(["hide", this === keyboard]); },
  };
  const browser = { m_refKeyboard: keyboard };
  assert.equal(discord.setSteamKeyboard(browser, "show", true), true);
  assert.equal(discord.setSteamKeyboard(browser, "hide", true), true);
  assert.equal(discord.setSteamKeyboard(browser, "show", false), false, "not while the page is hidden or the menu is open");
  assert.equal(discord.setSteamKeyboard(browser, "type", true), false);
  assert.equal(discord.setSteamKeyboard(null, "show", true), false);
  assert.equal(discord.setSteamKeyboard({ m_refKeyboard: {} }, "show", true), false);
  assert.deepEqual(calls, [["show", true], ["hide", true]]);

  const bridge = await readFile(bridgeUrl, "utf8");
  assert.match(bridge, /KeyboardBindingName = "tfsDiscordKeyboard"/);
  assert.match(bridge, /globalThis\.ToolsForSteamDiscord\?\.keyboard\?\./);
  const source = await readFile(moduleUrl, "utf8");
  assert.match(source, /runtime\.keyboard = \(kind\) => setSteamKeyboard\(currentBrowser\(\), kind\)/);
});

test("Voice summary reflects the page state", () => {
  assert.equal(discord.describeVoice(null), "");
  assert.equal(discord.describeVoice({ running: true, signedIn: false }), "Sign in to Discord");
  assert.equal(discord.describeVoice({ running: true, signedIn: true, voiceConnected: true }), "In voice");
  assert.equal(discord.describeVoice({ running: true, voiceConnected: true, muted: true }), "In voice · Muted");
  assert.equal(discord.describeVoice({ running: true, voiceConnected: true, deafened: true, muted: true }), "In voice · Deafened");
  assert.equal(discord.describeVoice({ running: true, voiceConnected: false }), "");
});

test("BrowserView liveness follows Steam's wrapper", () => {
  assert.equal(discord.isBrowserAlive(null), false);
  assert.equal(discord.isBrowserAlive({ GetBrowser: () => null }), false);
  assert.equal(discord.isBrowserAlive({ GetBrowser: () => ({}) }), true);
  assert.equal(discord.isBrowserAlive({ GetBrowser: () => { throw new Error("gone"); } }), false);
});

test("Discord Web uses Steam's own browser host and keeps the view alive", async () => {
  const source = await readFile(moduleUrl, "utf8");
  assert.match(source, /mainWindow\.CreateBrowserView\(BROWSER_NAME,/, "the view comes from Big Picture's window");
  assert.match(source, /findExportBySource\?\.\(\s*"displayURLBar",\s*"allowUnderlay",\s*"BExternalTriggeredLoad"/);
  assert.match(source, /Symbol\.for\("tfs\.discord\.browser"\)/, "the view survives tfs-discord updates");
  assert.match(source, /runtime\.deinit = \(\) => \{[\s\S]*?The BrowserView stays/);
  assert.match(source, /core\.routes\.add\(ROUTE, pages\.DiscordPage/);
  assert.match(source, /onCancelButton[\s\S]*?api\/discord\/web\/back[\s\S]*?core\.routes\.back\(\)/, "B closes popups before leaving");
  assert.doesNotMatch(source, /webpack\.require\(\d+\)|t\(\d{4,}\)/, "no hard-coded Steam module ids");
});

test("Quick Access opens Discord Web and the bundle is registered", async () => {
  const popup = await readFile(popupUrl, "utf8");
  assert.match(popup, /window\.opener\?\.ToolsForSteamDiscord/);
  assert.match(popup, /function buildDiscordWebSlots\(\)/);
  assert.match(popup, /runDiscordWebAction\("toggle-mute"\)/);
  assert.match(popup, /runDiscordWebAction\("toggle-deafen"\)/);
  assert.match(popup, /"Open in Discord Web"/);
  assert.match(popup, /"Open in Discord Desktop App"/);

  const host = await readFile(hostUrl, "utf8");
  assert.match(host, /AddModule\(\s*"tfs-discord",\s*"__tfsDiscordVersion",\s*EmbeddedAssetReader\.ReadText\("Assets\/tfs-discord\.mjs"\)\)/);
  assert.match(host, /discordBundle\]\);/);
  assert.match(host, /DiscordWeb = new DiscordWebBridge\(/);
});
