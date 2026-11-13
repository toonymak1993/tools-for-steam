import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
const source = read("src/SteamLoader.App/Assets/perf-tab.js");

function load() {
  const window = { __tfsPerfTabTestHooks: true };
  assert.equal(vm.runInContext(source, vm.createContext({ window, console })), "test");
  return window.__tfsPerfTabInternals;
}

test("slider notches show off and units like Steam's own sliders", () => {
  const { notches } = load();
  const result = Array.from(notches([0, 30, 60], "Aus"), (notch) => `${notch.notchIndex}:${notch.label}`);
  assert.deepEqual(result, ["0:Aus", "1:30", "2:60"]);
  assert.equal(notches([60, 120], "Off", " Hz")[1].label, "120 Hz");
});

test("current values snap to the nearest slider notch", () => {
  const { nearestIndex } = load();
  assert.equal(nearestIndex([0, 30, 40, 60], 45), 2);
  assert.equal(nearestIndex([60, 120, 165], 165), 2);
  assert.equal(nearestIndex([], 60), 0);
});

test("audio rows join Steam's own audio section and display follows it", () => {
  const { mergeSettingsSections } = load();
  const el = (type, props, ...children) => ({ type, props: { ...(props || {}), children }, key: props?.key ?? null });
  const React = {
    Fragment: "Fragment",
    createElement: el,
    cloneElement: (element, props, ...children) => ({ ...element, props: { ...element.props, ...(props || {}), children } }),
    Children: { toArray: (children) => (Array.isArray(children) ? children : children == null ? [] : [children]).flat().filter((child) => child && typeof child === "object") },
  };
  const Section = "Section";
  const steamOutput = el("Fragment", null, el(Section, { title: "Audio" }, el("row", { key: "out" }), el("row", { key: "in" })), false, el(Section, { title: "Sonstiges" }));

  const merged = mergeSettingsSections(React, steamOutput, {
    audioTitle: "Audio", audioRows: [el("row", { key: "vol" })], displayTitle: "Anzeige", displayRows: [el("row", { key: "hdr" })], Section,
  });
  const sections = merged.props.children;
  assert.equal(sections.length, 3);
  assert.deepEqual(Array.from(sections[0].props.children, (child) => child.key), ["out", "in", "vol"]);
  assert.equal(sections[1].props.title, "Anzeige");
  assert.equal(sections[2].props.title, "Sonstiges");
  assert.equal(steamOutput.props.children[0].props.children.length, 2, "Steam's elements are not mutated");

  const withoutAudio = mergeSettingsSections(React, el("Fragment", null, el(Section, { title: "Sonstiges" })), {
    audioTitle: "Audio", audioRows: [el("row", { key: "vol" })], displayTitle: "Anzeige", displayRows: [], Section,
  });
  assert.equal(withoutAudio.props.children[0].props.title, "Audio");
});

test("app volume sits between audio and display, device pickers stay in audio", () => {
  const { mergeSettingsSections, fallbackText } = load();
  const el = (type, props, ...children) => ({ type, props: { ...(props || {}), children }, key: props?.key ?? null });
  const React = {
    Fragment: "Fragment",
    createElement: el,
    cloneElement: (element, props, ...children) => ({ ...element, props: { ...element.props, ...(props || {}), children } }),
    Children: { toArray: (children) => (Array.isArray(children) ? children : children == null ? [] : [children]).flat().filter((child) => child && typeof child === "object") },
  };
  const merged = mergeSettingsSections(React, el("Fragment", null, el("Section", { title: "Sonstiges" })), {
    audioTitle: "Audio",
    audioRows: [el("row", { key: "tfs-device-output" }), el("row", { key: "tfs-volume" }), el("row", { key: "tfs-device-input" })],
    appTitle: "App-Lautstärke",
    appRows: [el("row", { key: "tfs-app-1" })],
    displayTitle: "Anzeige",
    displayRows: [el("row", { key: "tfs-hdr" })],
    Section: "Section",
  });
  const sections = merged.props.children;
  assert.deepEqual(Array.from(sections, (section) => section.props.title), ["Audio", "App-Lautstärke", "Anzeige", "Sonstiges"]);
  assert.deepEqual(Array.from(sections[0].props.children, (row) => row.key), ["tfs-device-output", "tfs-volume", "tfs-device-input"]);
  for (const language of Object.values(fallbackText)) {
    for (const key of ["outputDevice", "inputDevice", "appVolume"]) assert.ok(language[key], key);
  }
});

test("the device row names the chip, the maker limit and the power source", () => {
  const { deviceDescription, profileDescription, rtssLabels, fallbackText } = load();
  const t = fallbackText.german;
  const ayaneo = { name: "AYANEO 3", cpu: "AMD Ryzen AI 9 HX 370", limitSource: "manufacturer", minimum: 5, maximumAc: 35, maximumBattery: 0 };
  assert.equal(deviceDescription(ayaneo, { powerSource: "ac" }, t), "AMD Ryzen AI 9 HX 370 · Herstellergrenze 5–35 W · Netzteil");
  const ally = { ...ayaneo, cpu: "AMD Ryzen Z1 Extreme", minimum: 7, maximumAc: 30, maximumBattery: 25 };
  assert.equal(deviceDescription(ally, { powerSource: "battery" }, t), "AMD Ryzen Z1 Extreme · Herstellergrenze 7–30 W · im Akkubetrieb bis 25 W · Akku");
  assert.match(deviceDescription({ ...ayaneo, limitSource: "amd" }, {}, t), /Herstellergrenze unbekannt/);
  assert.equal(profileDescription({ acWatts: 25, batteryWatts: 15, active: true }, t), "Netzteil 25 W · Akku 15 W · aktiv");
  assert.deepEqual(Array.from(rtssLabels(["Off", "FPS", "SteamOS Strip", "SteamOS Full", "Frame Pacing"], t)), ["Aus", "FPS", "Leiste", "Voll", "Pacing"]);
  const keys = Object.keys(fallbackText.english);
  for (const language of Object.values(fallbackText)) assert.deepEqual(Object.keys(language), keys);
});

test("a slider being dragged is not pulled back by older server answers", () => {
  const { createLocalHolds } = load();
  let clock = 0;
  const holds = createLocalHolds(() => clock, 1500, 400);
  const to = (volume) => (state) => ({ ...state, volume });
  const at40 = to(40);
  const at80 = to(80);

  holds.hold("vol", at40);           // first step, request for 40 goes out
  holds.hold("vol", at80);           // user keeps dragging to 80
  holds.release("vol", at40);        // answer for 40 arrives: must not end the hold of 80
  clock = 500;
  assert.equal(holds.apply({ volume: 40 }).volume, 80);
  holds.release("vol", at80);        // answer for 80
  clock = 800;
  assert.equal(holds.apply({ volume: 80 }).volume, 80);
  clock = 1000;                      // grace over: server data counts again
  assert.equal(holds.apply({ volume: 55 }).volume, 55);
  assert.equal(holds.size, 0);

  holds.hold("mic", to(10));         // no answer at all: the hold ends by itself
  clock = 3000;
  assert.equal(holds.apply({ volume: 70 }).volume, 70);
  assert.equal(holds.apply(null), null);
});

test("the TDP slider shows whether the handheld took the value", () => {
  const { tdpConfirmationText, fallbackText } = load();
  const t = fallbackText.german;
  assert.equal(tdpConfirmationText({ confirmation: { state: "confirmed", splWatts: 20, spptWatts: 25, fpptWatts: 30 } }, t), "✓ Vom Handheld bestätigt: 20 W (Boost 25/30 W)");
  assert.equal(tdpConfirmationText({ confirmation: { state: "confirmed", splWatts: 35, spptWatts: 35, fpptWatts: 35 } }, t), "✓ Vom Handheld bestätigt: 35 W");
  assert.equal(tdpConfirmationText({ value: 18, confirmation: { state: "pending", splWatts: 0 } }, t), "18 W werden an den Handheld übertragen…");
  assert.equal(tdpConfirmationText({ confirmation: { state: "failed", message: "status 0xFE" } }, t), "✗ Nicht übernommen: status 0xFE");
  assert.match(tdpConfirmationText({ confirmation: { state: "no-helper" } }, t), /Admin-Hilfsdienst/);
  assert.equal(tdpConfirmationText({ confirmation: { state: "none" } }, t), "");
});

test("the TDP slider explains why a limit may not hold", () => {
  const { tdpWarningText, fallbackText } = load();
  const t = fallbackText.german;
  assert.equal(tdpWarningText({ warning: "" }, t), "");
  assert.equal(tdpWarningText(undefined, t), "");
  assert.match(tdpWarningText({ warning: "pawnio-missing" }, t), /PawnIO/);
  assert.equal(tdpWarningText({ warning: "vendor-app", detail: "AYASpace" }, t), "AYASpace läuft und kann dieses Limit überschreiben.");
  assert.match(tdpWarningText({ warning: "error", detail: "status 0xFE" }, t), /status 0xFE$/);
  for (const language of Object.values(fallbackText)) {
    for (const key of ["tdpPawnIo", "tdpVendorApp", "tdpError"]) assert.ok(language[key], key);
  }
});

test("the profile page offers Global plus each value and sends null for Global", () => {
  const { profileChoiceOptions, profileUpdateBody, fallbackText } = load();
  const t = fallbackText.german;
  const options = profileChoiceOptions({ frameLimitOptions: [0, 30, 60], refreshOptions: [60, 120], rtssLevels: ["Off", "FPS"] }, t);
  assert.deepEqual(Array.from(options.frameLimit, ([value, label]) => `${value}:${label}`), ["null:Global", "0:Aus", "30:30 FPS", "60:60 FPS"]);
  assert.deepEqual(Array.from(options.refresh, ([value]) => value), [null, 60, 120]);
  assert.deepEqual(Array.from(options.rtss, ([value, label]) => `${value}:${label}`), ["null:Global", "0:Aus", "1:FPS"]);
  assert.deepEqual(Array.from(options.cpuBoost, ([value]) => value), [null, true, false]);
  const body = profileUpdateBody({ key: "steam:1", acWatts: 25, batteryWatts: 15, frameLimit: 60, title: "x" });
  assert.equal(JSON.stringify(body), JSON.stringify({ key: "steam:1", acWatts: 25, batteryWatts: 15, frameLimit: 60, refreshRate: null, rtssLevel: null, powerMode: null, cpuBoost: null }));
  assert.match(source, /const profileRoute = "\/tfs\/game-profile\/:key";/);
  assert.match(source, /onActivate: open,/);
});

test("context menu profiles use the backend's key, also for non-Steam shortcuts", () => {
  const { steamProfileKey } = load();
  assert.equal(steamProfileKey(1297900), "steam:1297900");
  const logged = 9589381822639243264n;
  assert.equal(steamProfileKey(Number(logged >> 32n)), `steam:${logged}`);
  const surface = read("src/SteamLoader.App/Assets/artwork-surface.js");
  assert.match(surface, /getPerfTabRuntime\(\)\?\.hasGameProfiles\?\.\(\) === true/);
  assert.match(surface, /key: "tfs-game-profile",/);
});

test("add profile lists the running game and open apps", () => {
  const { runningAppDescription, fallbackText } = load();
  const t = fallbackText.german;
  assert.equal(runningAppDescription({ isCurrentGame: true, executablePath: "C:\\Games\\x.exe", hasProfile: false }, t), "Läuft gerade");
  assert.equal(runningAppDescription({ isCurrentGame: false, executablePath: "C:\\Tools\\obs64.exe", hasProfile: true }, t), "obs64.exe · Profil vorhanden");
  assert.match(source, /api\("\/api\/perf-tab\/add-profile", \{ value: app\.key \}\)/);
  assert.match(source, /api\("\/api\/perf-tab\/running-apps"\)/);
});

test("game profiles collapse behind one header row and remember the choice", () => {
  assert.match(source, /const profilesOpenKey = "tfs\.perfTab\.profilesOpen";/);
  assert.match(source, /if \(profiles\.supported && profilesOpen\) \{/);
  assert.match(source, /onActivate: toggleOpen,/);
  assert.match(source, /window\.localStorage\.getItem\(profilesOpenKey\) === "1"/);
});

test("Quick Settings' display section offers the built-in / external screen switch (former Display plugin)", () => {
  assert.match(source, /\["internal", t\.displayInternal, t\.displayInternalDescription\]/);
  assert.match(source, /\["external", t\.displayExternal, t\.displayExternalDescription\]/);
  assert.match(source, /"\/api\/quick-settings\/display-output", \{ value: mode \}/);
  const { fallbackText } = load();
  for (const language of Object.keys(fallbackText)) {
    for (const key of ["displayInternal", "displayInternalDescription", "displayExternal", "displayExternalDescription"]) {
      assert.ok(fallbackText[language][key], `${language}.${key}`);
    }
  }
  const endpoints = read("src/SteamLoader.App/Hosting/PerfTabApiEndpoints.cs");
  assert.match(endpoints, /case "display-output" when isPost:\s+result = service\.SetDisplayOutput\(/);
});

test("the section is patched into Steam's Performance tab through the core", () => {
  assert.match(source, /const perfTabKey = 5; \/\/ EQuickAccessTab\.Perf/);
  assert.match(source, /core\.qam\.patchTab\(perfTabKey,/);
  assert.match(source, /steamText\("#QuickAccess_Tab_Perf_LimitFrameRate"/);
  assert.match(source, /if \(role && role !== "shared"\) return "skipped";/);
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  assert.match(host, /EmbeddedAssetReader\.ReadText\("Assets\/perf-tab\.js"\)/);
  assert.match(host, /PerfTab = new Infrastructure\.PerfTab\.PerfTabService\(/);
});
