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

  assert.match(socialPage, /createSectionHeader\(0, "Friends"/);
  assert.match(socialPage, /createSectionHeader\(friendDisplaySlots\.length, "Servers"/);
  assert.match(
    socialPage,
    /createSectionHeader\(friendDisplaySlots\.length \+ guildDisplaySlots\.length, "Settings"/,
  );
  assert.match(
    socialPage,
    /slots: snapshot\?\.authorized\s*\? \[\.\.\.friendDisplaySlots, \.\.\.guildDisplaySlots, settingsSlot\]/,
    "the rendered order should be Friends, Servers, then Settings",
  );
  assert.match(socialPage, /\(\) => openDiscordGuild\(guild\.id\)/);
  assert.match(socialPage, /friend\?\.status !== "offline"/);
  assert.match(socialPage, /buildDiscordMemberIcon\(friend\?\.avatarUrl, friend\?\.status\)/);

  const friendSlots = socialPage.match(/const friendSlots =[\s\S]*?const guildSlots =/)?.[0];
  assert.ok(friendSlots, "online friend slots should be present");
  assert.doesNotMatch(friendSlots, /disabled:\s*true/, "online friends should not appear dimmed");
  assert.match(source, /steamloader-discord-presence-dot\.is-online/);
  assert.match(source, /background:\s*#23a55a/);
});
