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

test("Settings exposes System > Driver > GPU Update", () => {
  assert.match(
    popupSource,
    /id:\s*"system"[\s\S]*title:\s*"System"[\s\S]*pageId === "system"/,
  );
  assert.match(popupSource, /createSectionHeader\(3,\s*"Driver"/);
  assert.match(popupSource, /makeCommandSlot\(\s*"GPU Update"/);
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
    /GPU Update is available only when an NVIDIA GPU is detected/,
  );
});

test("GPU Update uses the dedicated local API and pinned verified helper", () => {
  assert.match(popupSource, /api\/system\/driver\/nvidia\/state/);
  assert.match(popupSource, /api\/system\/driver\/nvidia\/update/);
  assert.match(apiSource, /_nvidiaDriverUpdateService\.LaunchAsync/);
  assert.match(
    serviceSource,
    /releases\/download\/v1\.25\.2\/TinyNvidiaUpdateChecker\.exe/,
  );
  assert.match(serviceSource, /SHA256\.HashData/);
  assert.match(serviceSource, /--config-override=/);
});

test("Silent Game Ready Update is confirmed and runs the helper unattended", () => {
  assert.match(popupSource, /Silent Game Ready Update/);
  assert.match(popupSource, /silentGameReadyArmedUntil/);
  assert.match(
    popupSource,
    /api\/system\/driver\/nvidia\/game-ready\/silent/,
  );
  assert.match(
    apiSource,
    /_nvidiaDriverUpdateService\.LaunchSilentGameReadyAsync/,
  );
  assert.match(serviceSource, /--quiet/);
  assert.match(serviceSource, /--noprompt/);
  assert.match(serviceSource, /--confirm-dl/);
  assert.match(serviceSource, /Driver type" value="grd"/);
  assert.doesNotMatch(serviceSource, /ArgumentList\.Add\("--force-dl"\)/);
});
