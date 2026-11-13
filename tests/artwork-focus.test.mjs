import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const surfaceUrl = new URL("../src/SteamLoader.App/Assets/artwork-surface.js", import.meta.url);

function sliceFunctions(source, startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start);
  assert.ok(start >= 0 && end > start, `${startMarker} should be present`);
  return source.slice(start, end);
}

test("focused artwork cards show a ring above the image", async () => {
  const source = await readFile(surfaceUrl, "utf8");

  assert.match(
    source,
    /\.steamtools-artwork-asset::after \{[\s\S]*?position: absolute;[\s\S]*?inset: 0;[\s\S]*?z-index: 3;[\s\S]*?inset 0 0 0 3px #ffffff/,
    "the focus ring must be painted over full-bleed artwork, not underneath it",
  );
  assert.match(
    source,
    /\.steamtools-artwork-asset\.is-controller-focus::after \{\s*opacity: 1;/,
    "controller focus should reveal the ring",
  );
  assert.match(
    source,
    /\.steamtools-artwork-asset\.is-controller-focus \{[\s\S]*?transform: scale\(1\.045\);/,
    "the focused card should lift like a Steam capsule",
  );

  const appliedRule = source.indexOf(".steamtools-artwork-asset.is-applied {");
  const focusRule = source.indexOf(".steamtools-artwork-asset.is-controller-focus {");
  assert.ok(appliedRule >= 0 && focusRule > appliedRule,
    "focus must win over the applied style instead of being overwritten by it");
  assert.match(
    source,
    /\.steamtools-artwork-asset\.is-applied\.is-controller-focus \{[\s\S]*?rgba\(101, 226, 132/,
    "an applied card keeps its green hint while focused",
  );
  assert.match(
    source,
    /@media \(prefers-reduced-motion: reduce\) \{[\s\S]*?\.steamtools-artwork-asset\.is-controller-focus \{\s*transform: none;/,
    "reduced motion keeps the ring but drops the lift",
  );
  assert.match(source, /const version = (5[3-9]|[6-9]\d);/, "the runtime version must change so Steam picks up the new code");
});

function createSpatialHarness(source) {
  const body = sliceFunctions(source, "  function getArtworkFocusGeometry(", "\n  function moveArtworkFocus(");
  return new Function(`${body}\nreturn findArtworkFocusNeighbor;`)();
}

function group(left, top, width, height) {
  return { getBoundingClientRect: () => ({ left, top, width, height }) };
}

function box(name, left, top, width, height, scale = 1, section = null) {
  const x = left + width / 2;
  const y = top + height / 2;
  return {
    name,
    offsetWidth: width,
    offsetHeight: height,
    closest: () => section,
    getBoundingClientRect: () => ({
      left: x - (width * scale) / 2,
      top: y - (height * scale) / 2,
      width: width * scale,
      height: height * scale,
    }),
  };
}

test("controller navigation follows the visible layout like Steam", async () => {
  const source = await readFile(surfaceUrl, "utf8");
  const findNeighbor = createSpatialHarness(source);

  const searchGroup = group(20, 20, 360, 44);
  const typeRail = group(400, 20, 600, 44);
  const side = group(10, 80, 220, 640);
  const grid = group(240, 90, 555, 618);
  const search = box("search", 290, 20, 90, 44, 1, searchGroup);
  const chips = [0, 1, 2, 3, 4].map((index) => box(`chip-${index}`, 400 + index * 120, 20, 112, 44, 1, typeRail));
  const games = [0, 1, 2].map((index) => box(`game-${index}`, 20, 90 + index * 48, 200, 43, 1, side));
  const cards = [];
  for (let row = 0; row < 3; row += 1) {
    for (let column = 0; column < 4; column += 1) {
      const scale = row === 0 && column === 0 ? 1.045 : 1;
      cards.push(box(`card-${row}-${column}`, 240 + column * 141, 90 + row * 209, 132, 200, scale, grid));
    }
  }
  const items = [search, ...chips, ...games, ...cards];
  const card = (row, column) => cards[row * 4 + column];

  assert.equal(findNeighbor(card(0, 0), "down", items).name, "card-1-0", "down keeps the column, even from the scaled focus card");
  assert.equal(findNeighbor(card(0, 0), "right", items).name, "card-0-1");
  assert.equal(findNeighbor(card(1, 2), "up", items).name, "card-0-2");
  assert.equal(findNeighbor(card(1, 0), "left", items).name, "game-2", "left from the grid reaches the game list");
  assert.equal(findNeighbor(card(0, 1), "up", items).name, "chip-0", "up from the grid reaches the artwork types");
  assert.equal(findNeighbor(games[0], "right", items).name, "card-0-0");
  assert.equal(findNeighbor(card(0, 3), "right", items), null, "Right at the row end stays instead of jumping to the type bar");
  assert.equal(findNeighbor(search, "right", items).name, "chip-0");
  assert.equal(findNeighbor(chips[0], "down", items).name, "card-0-1");
  assert.equal(findNeighbor(games[0], "up", items), null, "the game list does not jump to the far search button");
  assert.equal(findNeighbor(card(2, 1), "down", items), null, "no wrap-around at the bottom");
  assert.doesNotMatch(source, /function getArtworkGridStep\(|function toggleArtworkFocusZone\(/);
});

function createInputHarness(source) {
  const body = sliceFunctions(source, "  function getArtworkActionFromSteamButton(", "\n  function consumeArtworkOverlayState(");
  const handlers = [];
  const posted = [];
  const repeated = [];
  const timers = [];
  let now = 10_000;
  const fakeWindow = {
    FocusNavController: {
      m_rgCatchAllGamepadInput: handlers,
      SetCatchAllGamepadInput(callback) {
        handlers.push(callback);
        return {
          Unregister: () => {
            const index = handlers.indexOf(callback);
            if (index >= 0) {
              handlers.splice(index, 1);
            }
          },
        };
      },
    },
    setTimeout: (callback, delay) => {
      timers.push({ callback, at: now + delay });
      return timers.length;
    },
    clearTimeout: () => {},
  };
  const state = {
    overlay: null,
    remoteOverlayActive: false,
    catchAllInstalled: false,
    catchAllRegistration: null,
    catchAllCallback: null,
    previousCatchAllGamepadInput: null,
    catchAllButtonState: {},
    heldSteamButtons: new Set(),
    rawSteamButtons: [],
    catchAllReleaseTimer: null,
    catchAllSuppressUntil: 0,
    recentArtworkInputNonces: [],
  };
  const api = new Function(
    "state", "window", "postArtworkMessage", "maybeRepeatGamepadAction", "Date",
    `${body}\nreturn { setRemoteOverlayActive, consumeArtworkInput };`,
  )(
    state,
    fakeWindow,
    (message) => posted.push(message),
    (...args) => repeated.push(args),
    { now: () => now },
  );
  const press = (button, isDown, isRepeat = false) => {
    for (let index = handlers.length - 1; index >= 0; index -= 1) {
      if (handlers[index](button, isDown, isRepeat)) {
        return true;
      }
    }
    return false;
  };
  const advance = (ms) => {
    now += ms;
    for (const timer of timers.splice(0).sort((a, b) => a.at - b.at)) {
      if (timer.at <= now) {
        timer.callback();
      } else {
        timers.push(timer);
      }
    }
  };
  return { api, state, handlers, posted, repeated, press, advance, setNow: (value) => { now = value; }, getNow: () => now };
}

test("Steam button releases and held repeats are not treated as new presses", async () => {
  const source = await readFile(surfaceUrl, "utf8");
  const harness = createInputHarness(source);
  harness.handlers.push(function staleTfsHandler() { return false; });
  harness.handlers[0].__steamToolsArtworkCatchAll = true;

  harness.api.setRemoteOverlayActive(true);
  assert.equal(harness.handlers.length, 1, "stale handlers of earlier runtimes are removed");

  assert.equal(harness.press(1, true), true);
  assert.equal(harness.press(1, false), true, "the release is swallowed");
  assert.deepEqual(harness.posted.map((message) => message.action), ["a"], "A down and up apply only once");

  harness.advance(50);
  assert.equal(harness.press(1, true, true), true);
  assert.equal(harness.posted.length, 1, "a held A does not repeat the apply");

  assert.equal(harness.press(10, true), true);
  harness.advance(50);
  assert.equal(harness.press(10, false), true);
  harness.advance(10);
  assert.equal(harness.press(10, true), true);
  assert.deepEqual(
    harness.posted.slice(1).map((message) => [message.action, message.repeat]),
    [["down", false], ["down", false]],
    "two quick taps move twice",
  );

  assert.equal(harness.press(27, true), true, "unmapped buttons (Steam, QAM) do not reach the page underneath");
});

test("the button that closes the overlay cannot reach Steam afterwards", async () => {
  const source = await readFile(surfaceUrl, "utf8");
  const harness = createInputHarness(source);

  harness.api.setRemoteOverlayActive(true);
  assert.equal(harness.press(2, true), true);
  harness.api.setRemoteOverlayActive(false);
  harness.advance(400);
  assert.equal(harness.handlers.length, 1, "capture stays while B is still held");
  assert.equal(harness.press(2, true, true), true, "a late B repeat is swallowed");
  assert.equal(harness.press(2, false), true, "the B release is swallowed");
  harness.advance(80);
  assert.equal(harness.handlers.length, 0, "the handler is really unregistered afterwards");
  assert.equal(harness.press(2, true), false, "the next B belongs to Steam again");
});

test("an input relayed over several channels acts once and stale leftovers never", async () => {
  const source = await readFile(surfaceUrl, "utf8");
  const harness = createInputHarness(source);
  harness.state.overlay = {};
  const now = harness.getNow();

  const first = { type: "input", action: "down", repeat: false, nonce: `${now}-a` };
  const second = { type: "input", action: "down", repeat: false, nonce: `${now}-b` };
  harness.api.consumeArtworkInput(first);
  harness.api.consumeArtworkInput(second);
  harness.api.consumeArtworkInput(JSON.stringify(first));
  harness.api.consumeArtworkInput(JSON.stringify(second));
  harness.api.consumeArtworkInput({ type: "input", action: "a", nonce: `${now - 5000}-old` });

  assert.deepEqual(harness.repeated, [
    ["down", "remote", false],
    ["down", "remote", false],
  ]);
});

test("closing the overlay returns focus to the Steam control it came from", async () => {
  const source = await readFile(surfaceUrl, "utf8");
  assert.match(
    source,
    /const returnFocusElement = getOverlayReturnFocusElement\(\);\s*closeOverlay\(\{ restoreFocus: false \}\);\s*state\.returnFocusElement = returnFocusElement;/,
  );
  assert.match(
    source,
    /if \(hadOverlay && options\.restoreFocus !== false\) \{\s*restoreOverlayReturnFocus\(returnFocusElement\);/,
  );
  assert.match(
    source,
    /function handleOverlayKeyDown\([\s\S]*?event\.stopPropagation\(\);\s*event\.stopImmediatePropagation\?\.\(\);\s*if \(!handled\) \{/,
    "unhandled keys are kept away from the Steam page while the overlay is open",
  );
});

test("one controller press moves once even when Steam and the browser both report it", async () => {
  const source = await readFile(surfaceUrl, "utf8");
  const body = sliceFunctions(source, "  function maybeRepeatGamepadAction(", "\n  function getPressedArtworkGamepadActions(");
  let now = 50_000;
  const handled = [];
  const state = {
    ignoreOverlayInputUntil: 0,
    lastSteamGamepadInput: "",
    lastSteamGamepadInputAt: 0,
    lastBrowserGamepadInput: "",
    lastBrowserGamepadInputAt: 0,
    lastGamepadInput: "",
    lastGamepadInputAt: 0,
  };
  const maybeRepeat = new Function(
    "state", "handleGamepadAction", "Date",
    `${body}\nreturn maybeRepeatGamepadAction;`,
  )(state, (action) => handled.push(action), { now: () => now });

  maybeRepeat("right", "browser-gamepad");
  now += 40;
  maybeRepeat("right", "steam-catch-all", false);
  assert.deepEqual(handled, ["right"], "the browser pad seeing the press first must not double it");

  now += 300;
  maybeRepeat("next-type", "steam-catch-all", false);
  now += 16;
  maybeRepeat("next-type", "browser-gamepad");
  assert.deepEqual(handled, ["right", "next-type"], "LB/RB move once as well");

  now += 300;
  maybeRepeat("right", "steam-catch-all", false);
  now += 90;
  maybeRepeat("right", "steam-catch-all", false);
  assert.deepEqual(handled.slice(2), ["right", "right"], "two real taps still move twice");

  assert.match(
    source,
    /if \(gamepad && isSteamInputRelayActive\(\)\) \{\s*state\.pressedGamepadButtons = getPressedArtworkGamepadActions\(\);\s*\} else if \(gamepad\) \{/,
    "while Steam relays the controller, the browser pad is not read a second time",
  );
  assert.match(source, /installArtworkCatchAllInput\(\);\s*announceSteamInputRelay\(\);/);
});
