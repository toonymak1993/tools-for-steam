import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = (relativePath) => fs.readFileSync(path.join(root, relativePath), "utf8");

test("performance backend uses isolated RTSS APIs and no legacy ETW helper", () => {
  const project = read("src/SteamLoader.App/SteamLoader.App.csproj");
  const memory = read("src/SteamLoader.App/Infrastructure/Performance/RtssSharedMemoryClient.cs");
  const runtime = read("src/SteamLoader.App/SteamLoaderRuntime.cs");
  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  const program = read("src/SteamLoader.App/Program.cs");
  const service = read("src/SteamLoader.App/Infrastructure/Performance/TfsPerformanceService.cs");
  const profiles = read("src/SteamLoader.App/Infrastructure/Performance/RtssProfileClient.cs");

  assert.doesNotMatch(project, /Microsoft\.Diagnostics\.Tracing\.TraceEvent/);
  assert.match(memory, /OwnerName = "ToolsForSteam"/);
  assert.match(memory, /ReleaseOverlay\(\)/);
  assert.match(memory, /FrametimeBufferFramerateOffset/);
  assert.match(memory, /OnePercentLowOffset = 9172/);
  assert.doesNotMatch(memory, /1_000_000d\s*\/\s*frameTimeUs/);
  assert.doesNotMatch(runtime, /FpsHelper/);
  assert.doesNotMatch(host, /PerformanceStatusStore|performance-runtime\.json/);
  assert.equal((program.match(/--fps-helper/g) ?? []).length, 1);
  assert.doesNotMatch(program, /RunFpsHelper|RegisterFpsHelper|CheckFpsHelper/);
  assert.match(service, /<P0>/);
  assert.match(service, /<P8>/);
  assert.match(service, /<FNT=Unispace/);
  assert.doesNotMatch(profiles, /PositionX|PositionY|ZoomRatio/);
  assert.match(profiles, /RTSS could not save the per-game frame-limit profile/);
  assert.equal(
    fs.existsSync(path.join(root, "src/SteamLoader.App/Infrastructure/Performance/PerformanceStatusStore.cs")),
    false,
  );
});

test("full performance presets use RTSS-native FPS and frametime tags", () => {
  const service = read("src/SteamLoader.App/Infrastructure/Performance/TfsPerformanceService.cs");
  const performanceMode = service.slice(service.indexOf('3 => $"'), service.indexOf('_ => $"'));
  const framePacingMode = service.slice(service.indexOf('_ => $"'));

  assert.match(performanceMode, /<FR>/);
  assert.match(performanceMode, /<FT>/);
  assert.doesNotMatch(performanceMode, /telemetry\.FrameTimeMs/);
  assert.match(framePacingMode, /<FR>/);
  assert.match(framePacingMode, /<FT>/);
  assert.doesNotMatch(framePacingMode, /telemetry\.FrameTimeMs/);
});

test("installer reuses RTSS, closes its maintenance processes, and removes the legacy FPS task", () => {
  const installer = read("installer/ToolsForSteam.iss");

  assert.match(installer, /RtssPackageId = 'Guru3D\.RTSS'/);
  assert.match(installer, /RtssRequiredVersion = '7\.3\.7'/);
  assert.match(installer, /InstallRtssIfNeeded;/);
  assert.match(installer, /its binaries and settings will be reused without reinstalling it/);
  assert.match(installer, /StopRtssProcessesForMaintenance/);
  assert.match(installer, /RemoveLegacyFpsHelperTask;/);
  assert.match(installer, /LegacyFpsHelperTaskExists/);
  assert.match(installer, /Windows still reports it as installed/);
  assert.match(installer, /ConfigureRtssProfileAccess;/);
  assert.match(installer, /FileSystemAccessRule/);
  assert.match(installer, /per-user write access for the RTSS Profiles folder/);
});

test("Quick Access uses one Off-to-mode slider and no separate overlay transport buttons", () => {
  const quickAccess = read("src/SteamLoader.App/Assets/quickaccess-popup.js");
  const service = read("src/SteamLoader.App/Infrastructure/Performance/TfsPerformanceService.cs");

  assert.match(quickAccess, /title: "RTSS Performance"/);
  assert.match(quickAccess, /performanceFrameLimitOptions/);
  assert.match(quickAccess, /settingKey: "frame-limit"/);
  assert.match(quickAccess, /\.\.\.simpleSettingSlots/);
  assert.doesNotMatch(quickAccess, /Start Overlay|Stop Overlay|Restart Overlay/);
  assert.match(quickAccess, /Off\. Move right to enable/);
  assert.match(service, /new\(0, "Off"/);
  assert.match(service, /new\(4, "Frame Pacing"/);
  assert.match(service, /configuration\.OverlayEnabled = configuration\.OverlayLevel > 0/);
  assert.match(quickAccess, /min: 50/);
  assert.match(quickAccess, /step: 50/);
});
