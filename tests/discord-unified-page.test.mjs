import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);

test("Discord opens the unified friends, servers, and settings page", async () => {
  const source = await readFile(popupUrl, "utf8");
  const socialPageStart = source.indexOf("if (socialSdkActive)");
  const socialPageEnd = source.indexOf("const participantSlots", socialPageStart);
  const socialPage = source.slice(socialPageStart, socialPageEnd);

  assert.ok(socialPageStart >= 0 && socialPageEnd > socialPageStart, "social Discord page should be present");
  assert.match(
    source,
    /plugin\.id === "discord"[\s\S]*?pageId: "server"/,
    "the Discord home tile should open its main page directly",
  );

  assert.match(socialPage, /"Favorite Servers"/);
  assert.match(socialPage, /"Manage Server Favorites"/);
  assert.match(
    socialPage,
    /"Manage",\s*"Favorites, connection, privacy, refresh, and fallback options\."/,
  );
  assert.match(
    socialPage,
    /slots: snapshot\?\.authorized\s*\? socialSlots/,
    "authorized Discord content should use the collapsible social section list",
  );
  assert.match(socialPage, /makeAccordionSlot\(/);
  assert.match(socialPage, /toggleExpandedSection\(sectionKey, defaultExpanded\)/);
  assert.match(socialPage, /\.\.\.\(expanded \? children : \[\]\)/);

  const socialSlots = socialPage.slice(socialPage.indexOf("const socialSlots = ["));
  const favoritesIndex = socialSlots.indexOf('"Favorite Servers"');
  const friendsIndex = socialSlots.indexOf('"Friends"');
  const serversIndex = socialSlots.indexOf('"Servers"');
  const manageIndex = socialSlots.indexOf('"Manage"');
  assert.ok(
    favoritesIndex >= 0 && favoritesIndex < friendsIndex && friendsIndex < serversIndex && serversIndex < manageIndex,
    "favorite servers should render above friends, regular servers, and management",
  );
  assert.match(socialPage, /\(\) => openDiscordGuild\(guild\.id\)/);
  assert.match(socialPage, /setDiscordGuildFavorite\(guild\?\.id, !isFavorite\)/);
  assert.match(socialPage, /badge: isFavorite \? "Favorite"/);
  assert.match(socialPage, /friend\?\.status !== "offline"/);
  assert.match(socialPage, /buildDiscordMemberIcon\(friend\?\.avatarUrl, friend\?\.status\)/);

  const friendSlots = socialPage.match(/const friendSlots =[\s\S]*?const createGuildSlot =/)?.[0];
  assert.ok(friendSlots, "online friend slots should be present");
  assert.doesNotMatch(friendSlots, /disabled:\s*true/, "online friends should not appear dimmed");
  assert.match(source, /steamloader-discord-presence-dot\.is-online/);
  assert.match(source, /background:\s*#23a55a/);
  assert.match(source, /"friend-online-notifications"/);
  assert.match(source, /case "notifications\.show"/);
});
