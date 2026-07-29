import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const frontendLibUrl = new URL("../src/SteamLoader.App/Assets/st-frontend-lib.js", import.meta.url);

test("Quick Access header actions participate in focus restoration", async () => {
  const [popupSource, frontendLibSource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibUrl, "utf8"),
  ]);

  for (const source of [popupSource, frontendLibSource]) {
    const createHeaderActionButton = source.match(
      /function createHeaderActionButton\([\s\S]*?\r?\n  \}/,
    )?.[0];

    assert.ok(createHeaderActionButton, "header action renderer should be present");
    assert.match(createHeaderActionButton, /data-slot-button/);
    assert.match(createHeaderActionButton, /data-slot-key/);
    assert.match(createHeaderActionButton, /data-header-action/);
    assert.match(createHeaderActionButton, /onGamepadFocus/);
    assert.match(createHeaderActionButton, /onMoveLeft/);
    assert.match(createHeaderActionButton, /onMoveRight/);
    assert.match(createHeaderActionButton, /onMoveUp/);
    assert.match(createHeaderActionButton, /onMoveDown/);
  }

  assert.match(
    popupSource,
    /\[data-slot-button\]:not\(\[data-header-action="true"\]\)/,
    "a fresh fallback should still choose the first content button, not a header action",
  );
  assert.match(
    popupSource,
    /pendingSlotKey\.startsWith\("header-action:"\)/,
    "a pending header focus must bypass row auto-focus",
  );
  assert.match(popupSource, /function moveHomeHeaderActionFocus\(/);
  assert.match(popupSource, /function moveHomeHeaderToContent\(/);
  assert.match(popupSource, /function moveHomeContentToHeader\(/);
  assert.match(
    popupSource,
    /pluginIndex === 0[\s\S]*?onMoveUp:[\s\S]*?moveHomeContentToHeader/,
    "only the first plugin row should move up into the horizontal header action group",
  );
  assert.match(popupSource, /fallbackSlotKey: "header-action:settings"/);
});
