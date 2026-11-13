import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);

function extractFunction(source, name) {
  const start = source.indexOf(`function ${name}(`);
  assert.ok(start >= 0, `${name} exists`);
  let depth = 0;
  for (let index = source.indexOf("{", start); index < source.length; index += 1) {
    if (source[index] === "{") depth += 1;
    if (source[index] === "}") {
      depth -= 1;
      if (depth === 0) return source.slice(start, index + 1);
    }
  }
  throw new Error(`${name} has no end`);
}

async function loadRecorderGuard(window) {
  const source = await readFile(popupUrl, "utf8");
  const body = [
    "getControllerShortcutCapture",
    "getControllerShortcutFocusNav",
    "removeControllerShortcutCaptureGuards",
    "installControllerShortcutCaptureInputGuard",
    "uninstallControllerShortcutCaptureInputGuard",
  ].map((name) => extractFunction(source, name)).join("\n");
  const state = { generalSettings: {} };
  const api = new Function("window", "state", `${body}
    return {
      capture: getControllerShortcutCapture,
      install: installControllerShortcutCaptureInputGuard,
      uninstall: uninstallControllerShortcutCaptureInputGuard,
    };`)(window, state);
  return { api, state };
}

function createSteamFocusNav() {
  // Mirrors Steam's current FocusNavController: a list of catch-all
  // handlers, called newest first, any true result swallows the button.
  return {
    m_rgCatchAllGamepadInput: [],
    SetCatchAllGamepadInput(callback) {
      this.m_rgCatchAllGamepadInput.push(callback);
      return {
        Unregister: () => {
          const index = this.m_rgCatchAllGamepadInput.indexOf(callback);
          if (index >= 0) this.m_rgCatchAllGamepadInput.splice(index, 1);
        },
      };
    },
    press(button) {
      for (let index = this.m_rgCatchAllGamepadInput.length - 1; index >= 0; index -= 1) {
        if (this.m_rgCatchAllGamepadInput[index](button, true, false)) return "swallowed";
      }
      return "steam";
    },
  };
}

test("overlay recorder blocks Steam navigation through the Big Picture FocusNavController", async () => {
  const focusNav = createSteamFocusNav();
  // The Quick Access window has no FocusNavController of its own.
  const { api } = await loadRecorderGuard({ opener: { FocusNavController: focusNav } });
  const capture = api.capture();

  capture.active = true;
  api.install();
  assert.equal(focusNav.m_rgCatchAllGamepadInput.length, 1);
  assert.equal(focusNav.press(2), "swallowed", "B must not leave the page while recording");
  assert.equal(focusNav.press(6), "swallowed", "RB must not switch Quick Access tabs");

  capture.active = false;
  api.uninstall();
  assert.equal(focusNav.m_rgCatchAllGamepadInput.length, 0, "guard is removed after recording");
  assert.equal(focusNav.press(2), "steam");
});

test("overlay recorder guard never blocks input once the recorder is idle", async () => {
  const focusNav = createSteamFocusNav();
  const { api } = await loadRecorderGuard({ opener: { FocusNavController: focusNav } });
  const capture = api.capture();
  capture.active = true;
  api.install();
  capture.active = false;
  assert.equal(focusNav.press(1), "steam", "a missed uninstall must not swallow A");
});

test("overlay recorder removes guards left behind by an older popup copy", async () => {
  const focusNav = createSteamFocusNav();
  const stale = () => true;
  stale.__steamLoaderOverlayCaptureCatchAll = true;
  focusNav.m_rgCatchAllGamepadInput.push(stale);
  const { api } = await loadRecorderGuard({ opener: { FocusNavController: focusNav } });
  const capture = api.capture();
  capture.active = true;
  api.install();
  assert.equal(focusNav.m_rgCatchAllGamepadInput.includes(stale), false);
  capture.active = false;
  api.uninstall();
  assert.equal(focusNav.m_rgCatchAllGamepadInput.length, 0);
});

test("overlay pages keep controls enabled while a save runs and stop idle recordings", async () => {
  const source = await readFile(popupUrl, "utf8");
  const pageStart = (pattern) => {
    const match = pattern.exec(source);
    assert.ok(match, `page marker ${pattern} exists`);
    return match.index;
  };
  const combinationStart = pageStart(/state\.route\.pageId\?\.startsWith\("overlay-combination-"\)\r?\n\s*\) \{/);
  const overlayStart = pageStart(/state\.route\.pageId === "overlay"\r?\n\s*\) \{/);
  const pluginsStart = pageStart(/state\.route\.pageId === "plugins"\r?\n\s*\) \{/);
  const combinationPage = source.slice(combinationStart, overlayStart);
  const overlayPage = source.slice(overlayStart, pluginsStart);
  for (const page of [combinationPage, overlayPage]) {
    assert.ok(page.length > 0);
    assert.match(page, /const busy = isControllerShortcutSettingsUnavailable\(\);/);
    assert.doesNotMatch(page, /isGeneralSettingsBusy\(\)/);
  }
  assert.match(overlayPage, /requestFreshEntryForRoute\(targetRoute, 0, 0\)/);

  const poll = extractFunction(source, "pollControllerShortcutCapture");
  assert.match(poll, /controllerShortcutCaptureIdleTimeoutMs/);
  assert.match(poll, /controllerShortcutCaptureHeldTimeoutMs/);
});
