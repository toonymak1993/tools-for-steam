import assert from "node:assert/strict";
import fs from "node:fs";
import test from "node:test";

const popupSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url),
  "utf8",
);
const apiSource = fs.readFileSync(
  new URL("../src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs", import.meta.url),
  "utf8",
);
const serviceSource = fs.readFileSync(
  new URL(
    "../src/SteamLoader.App/Infrastructure/SystemTools/NvidiaDriverUpdateService.cs",
    import.meta.url,
  ),
  "utf8",
);

test("Settings exposes the staged System > Driver Game Ready workflow", () => {
  assert.match(
    popupSource,
    /id:\s*"system"[\s\S]*title:\s*"System"[\s\S]*pageId === "system"/,
  );
  assert.match(popupSource, /createSectionHeader\(3,\s*"Driver"/);
  assert.match(popupSource, /"Check for Game Ready Update"/);
  assert.match(popupSource, /"Install Game Ready Driver"/);
});

test("NVIDIA-only driver controls stay hidden on AMD and Intel systems", () => {
  assert.match(
    popupSource,
    /const nvidiaGpuDetected = gpuUpdate\?\.nvidiaGpuDetected === true/,
  );
  assert.match(popupSource, /if \(nvidiaGpuDetected\) \{[\s\S]*slots\.push/);
  assert.match(popupSource, /\.\.\.\(nvidiaGpuDetected[\s\S]*createSectionHeader\(3,\s*"Driver"/);
  assert.match(serviceSource, /Win32_VideoController/);
  assert.match(serviceSource, /VEN_10DE/);
  assert.match(
    serviceSource,
    /NVIDIA Game Ready updates are available only when an NVIDIA GPU is detected/,
  );
});

test("GPU Update uses staged local APIs and the pinned verified helper", () => {
  assert.match(popupSource, /api\/system\/driver\/nvidia\/state/);
  assert.match(popupSource, /api\/system\/driver\/nvidia\/game-ready\/check/);
  assert.match(popupSource, /api\/system\/driver\/nvidia\/game-ready\/install/);
  assert.match(apiSource, /_nvidiaDriverUpdateService\.CheckGameReadyAsync/);
  assert.match(apiSource, /_nvidiaDriverUpdateService\.StartGameReadyInstallAsync/);
  assert.match(
    serviceSource,
    /releases\/download\/v1\.25\.2\/TinyNvidiaUpdateChecker\.exe/,
  );
  assert.match(serviceSource, /SHA256\.HashData/);
  assert.match(serviceSource, /--config-override=/);
});

test("Game Ready install reports live download and installer progress", () => {
  assert.match(popupSource, /createNvidiaDriverProgressSlot/);
  assert.match(popupSource, /downloadProgressPercent/);
  assert.match(popupSource, /downloadBytesPerSecond/);
  assert.match(popupSource, /driverProgressPollTimer/);
  assert.match(serviceSource, /TrackGameReadyInstallAsync/);
  assert.match(serviceSource, /DownloadedBytes/);
  assert.match(serviceSource, /DownloadProgressPercent/);
  assert.match(serviceSource, /--quiet/);
  assert.match(serviceSource, /--noprompt/);
  assert.match(serviceSource, /--confirm-dl/);
  assert.match(serviceSource, /--dry-run/);
  assert.match(serviceSource, /Driver type" value="grd"/);
  assert.doesNotMatch(serviceSource, /ArgumentList\.Add\("--force-dl"\)/);
});

test("a completed GPU update offers the controlled Steam restart exactly when required", () => {
  assert.match(serviceSource, /SteamRestartRequired = true/);
  assert.match(serviceSource, /var steamRestartRequired = currentState\.SteamRestartRequired/);
  assert.match(serviceSource, /public NvidiaDriverUpdateSnapshot AcknowledgeSteamRestart/);
  assert.match(apiSource, /api\/system\/driver\/nvidia\/restart-steam/);
  assert.match(apiSource, /_powerActionService\.RestartSteamAfterDriverUpdate\(\)/);
  assert.match(
    fs.readFileSync(
      new URL("../src/SteamLoader.App/Services/PowerActionService.cs", import.meta.url),
      "utf8",
    ),
    /RestartSteamForSteamTools\(clearWebCache:\s*false\)/,
  );
  assert.match(
    popupSource,
    /const steamRestartRequired = gpuUpdate\?\.steamRestartRequired === true/,
  );
  assert.match(
    popupSource,
    /if \(steamRestartRequired && !driverBusy\)[\s\S]*"Restart Steam"/,
  );
});
