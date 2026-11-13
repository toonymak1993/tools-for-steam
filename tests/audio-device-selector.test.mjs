import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const frontendLibraryUrl = new URL("../src/SteamLoader.App/Assets/st-frontend-lib.js", import.meta.url);
const deviceModelUrl = new URL("../src/SteamLoader.App/Models/AudioOutputDeviceInfo.cs", import.meta.url);
const deviceServiceUrl = new URL(
  "../src/SteamLoader.App/Infrastructure/Audio/CoreAudioOutputDeviceService.cs",
  import.meta.url,
);

function slice(source, startMarker, endMarker) {
  const start = source.indexOf(startMarker);
  const end = source.indexOf(endMarker, start + startMarker.length);
  assert.ok(start >= 0 && end > start, `${startMarker} block should be present`);
  return source.slice(start, end);
}

test("Audio page is built from Steam's own fields in the two-column grid", async () => {
  const source = await readFile(popupUrl, "utf8");
  const channel = slice(source, "  function buildAudioChannelSlots(", "  function buildAudioMixerSessionSlot(");
  const page = slice(source, "  function buildAudioPluginModel(", "  function markPerformanceOverlaySlots(");

  // The page goes through the shared st-frontend-lib renderer, not a custom shell.
  assert.match(source, /return buildAudioPluginModel\(defaultModel, \{\s+makeToggleSlot,\s+makeAccordionSlot,\s+makeChoiceSlot,/);
  assert.doesNotMatch(source, /function createAudioDashboard\(/);
  assert.doesNotMatch(source, /audioDashboard: buildAudioDashboardModel\(\)/);
  assert.doesNotMatch(source, /function handleAudioDashboardKeyDown\(/, "no capture-phase left/right hook");

  // Per channel: a full-width volume slider (A mutes), then Mute and Device
  // paired in the two columns. Left/right on the Device row no longer switch
  // the Windows device; the list opens with A instead.
  assert.match(channel, /createPerformanceValueSliderSlot\(\{\s+title: "Volume",/);
  assert.match(channel, /onClick: \(\) => \{\s+if \(channel\.getInfo\(\)\) \{\s+void channel\.toggleMute\(\);/);
  assert.match(channel, /makeToggleSlot\(\s+channel\.muteTitle,/);
  assert.match(channel, /makeAccordionSlot\(\s+channel\.deviceTitle,/);
  assert.match(channel, /toggleAudioDeviceList\(channel\.sectionKey, channel\.otherSectionKey\)/);
  assert.match(channel, /makeChoiceSlot\(\s+presentation\.title,\s+presentation\.meta,/);
  assert.match(channel, /badge: switching \? "Switching\.\.\." : selected \? "Current" : ""/);
  assert.doesNotMatch(channel, /cyclePlaybackDevice|cycleCaptureDevice/);

  // Rows never turn disabled while data loads: a disabled row drops focus.
  assert.doesNotMatch(channel, /disabled:/);
  assert.doesNotMatch(page, /disabled: state\.audio/);
  assert.match(page, /title: "Output",[\s\S]*?deviceTitle: "Output Device",/);
  assert.match(page, /title: "Microphone",[\s\S]*?deviceTitle: "Microphone Device",/);
  assert.match(page, /"App Volume"/);
  assert.match(page, /key: "audio-refresh"/);

  // Choosing a device collapses the list and returns focus to the Device row.
  assert.match(source, /await setDefaultDevice\(options\.deviceId\)/);
  assert.match(source, /await setDefaultCaptureDevice\(options\.deviceId\)/);
  assert.match(source, /rerenderAudioDashboard\(\{ focusSlotKey: options\.listSlotKey \}\)/);
  assert.match(source, /function getAudioDevicePresentation\(device, deviceType\)/);
  assert.match(source, /playbackDeviceSwitchError/);
  assert.match(source, /captureDeviceSwitchError/);
});

test("Audio page shows at once and refreshes in the background", async () => {
  const source = await readFile(popupUrl, "utf8");
  const loader = slice(source, "  async function loadAudioDashboardState(", "  function findCurrentAudioDeviceIndex(");

  // Entering the page renders the last known values (memory or the cache of
  // the previous run) and always starts a refresh right away.
  assert.match(source, /if \(isAudioDashboardPage && !state\.audio\.dashboardLoading\) \{\s+hydrateAudioDashboardCache\(\);\s+void loadAudioDashboardState\(\);/);
  assert.match(source, /const audioDashboardCacheKey = "tfs\.audio\.dashboard\.v1";/);
  assert.match(loader, /const showLoading = options\.showLoading !== false && firstLoad;/);
  assert.match(loader, /persistAudioDashboardCache\(\);/);
  // Polls and live pushes with unchanged values do not re-render the panel.
  assert.match(loader, /getAudioDashboardSignature\(\) !== signatureBefore/);
  assert.match(source, /if \(getAudioDashboardSignature\(\) !== audioSignatureBefore\) \{\s+refreshAudioDashboardUi\(\);/);
  // Live values re-render Steam's fields once per frame.
  assert.match(source, /function refreshAudioDashboardUi\(\) \{\s+if \(isAudioDashboardRoute\(\)\) \{\s+scheduleNativeSliderRefresh\(\);/);
});

test("Audio device names keep their friendly parts", async () => {
  const [deviceModel, deviceService] = await Promise.all([
    readFile(deviceModelUrl, "utf8"),
    readFile(deviceServiceUrl, "utf8"),
  ]);
  assert.match(deviceModel, /string DisplayName = ""/);
  assert.match(deviceModel, /string InterfaceName = ""/);
  assert.match(deviceService, /device\.Name \?\? string\.Empty/);
  assert.match(deviceService, /device\.InterfaceName \?\? string\.Empty/);
});

test("System volume slider consumes horizontal controller input", async () => {
  const [popupSource, frontendLibrarySource] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(frontendLibraryUrl, "utf8"),
  ]);

  assert.match(
    popupSource,
    /function createFallbackVolumeSlider\([\s\S]*?onMoveLeft: \(event\) => \{[\s\S]*?slider\.onMoveLeft\?\.\(event\);[\s\S]*?return consumePanelDirectionalFocus\(event\);[\s\S]*?onMoveRight: \(event\) => \{[\s\S]*?slider\.onMoveRight\?\.\(event\);[\s\S]*?return consumePanelDirectionalFocus\(event\);/,
    "the local fallback must keep Left and Right on the volume slider",
  );
  assert.match(
    frontendLibrarySource,
    /function createFallbackVolumeSlider\([\s\S]*?onMoveLeft: \(event\) => \{[\s\S]*?slider\.onMoveLeft\?\.\(event\);[\s\S]*?return consumeCrossAxisNavigation\(event\);[\s\S]*?onMoveRight: \(event\) => \{[\s\S]*?slider\.onMoveRight\?\.\(event\);[\s\S]*?return consumeCrossAxisNavigation\(event\);/,
    "the shared renderer must report horizontal volume changes as handled",
  );
});
