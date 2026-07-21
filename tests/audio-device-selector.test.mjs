import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const popupUrl = new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url);
const deviceModelUrl = new URL("../src/SteamLoader.App/Models/AudioOutputDeviceInfo.cs", import.meta.url);
const deviceServiceUrl = new URL(
  "../src/SteamLoader.App/Infrastructure/Audio/CoreAudioOutputDeviceService.cs",
  import.meta.url,
);

test("Audio output and input selectors expand into exact device choices", async () => {
  const [source, deviceModel, deviceService] = await Promise.all([
    readFile(popupUrl, "utf8"),
    readFile(deviceModelUrl, "utf8"),
    readFile(deviceServiceUrl, "utf8"),
  ]);
  const dashboardModel = source.match(
    /function buildAudioDashboardModel\(\) \{[\s\S]*?\n  function markPerformanceOverlaySlots/,
  )?.[0];

  assert.ok(dashboardModel, "audio dashboard model should be present");
  assert.match(
    dashboardModel,
    /playbackSelector:[\s\S]*?toggleAudioDeviceSelector\(audioPlaybackDeviceSectionKey, audioCaptureDeviceSectionKey\)/,
    "activating Output Device should toggle its expanded list",
  );
  assert.match(
    dashboardModel,
    /captureSelector:[\s\S]*?toggleAudioDeviceSelector\(audioCaptureDeviceSectionKey, audioPlaybackDeviceSectionKey\)/,
    "activating Input Device should toggle its expanded list",
  );
  assert.match(dashboardModel, /options: playbackOptions/);
  assert.match(dashboardModel, /options: captureOptions/);
  assert.match(source, /createAudioDashboardDeviceOptions\(selector, autoFocusIndex, indexOffset\)/);
  assert.match(
    source,
    /createAudioDashboardChannelCard\([\s\S]*?dashboard\.playbackSelector[\s\S]*?dashboard\.playbackSlider[\s\S]*?dashboard\.playbackToggle/,
  );
  assert.match(
    source,
    /createAudioDashboardChannelCard\([\s\S]*?dashboard\.captureSelector[\s\S]*?dashboard\.captureSlider[\s\S]*?dashboard\.captureToggle/,
  );
  assert.match(source, /await setDefaultDevice\(options\.deviceId\)/);
  assert.match(source, /await setDefaultCaptureDevice\(options\.deviceId\)/);
  assert.match(source, /"aria-expanded": control\.expanded/);
  assert.match(source, /const badgeText = control\.switching \? "Switching\.\.\." : control\.selected \? "Current" : ""/);
  assert.match(source, /max-height: min\(300px, 42vh\)/);
  assert.match(source, /function getAudioDevicePresentation\(device, deviceType\)/);
  assert.match(source, /playbackDeviceSwitchError/);
  assert.match(source, /captureDeviceSwitchError/);
  assert.match(source, /const mixerExpanded = isExpandedSection\(audioMixerSectionKey, false\)/);
  assert.match(source, /dashboard\?\.mixerToggle\?\.expanded/);
  assert.match(source, /key: "audio-refresh"/);
  assert.doesNotMatch(source, /children: "Default Devices"/);
  assert.match(source, /\.steamloader-audio-channel-card\.is-output \{[\s\S]*?border-color:/);
  assert.match(source, /\.steamloader-audio-channel-card::before \{[\s\S]*?width: 4px/);
  assert.match(deviceModel, /string DisplayName = ""/);
  assert.match(deviceModel, /string InterfaceName = ""/);
  assert.match(deviceService, /device\.Name \?\? string\.Empty/);
  assert.match(deviceService, /device\.InterfaceName \?\? string\.Empty/);
  assert.match(
    source,
    /const audioDashboardFocusSlots = model\.audioDashboard[\s\S]*?getAudioDashboardControls\(model\.audioDashboard\)/,
    "expanded device rows should participate in controller focus restoration",
  );
});
