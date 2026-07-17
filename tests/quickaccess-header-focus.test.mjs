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
  assert.match(popupSource, /fallbackSlotKey: "header-action:settings"/);
});
