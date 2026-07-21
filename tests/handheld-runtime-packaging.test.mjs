import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const installer = fs.readFileSync(path.join(root, "installer", "ToolsForSteam.iss"), "utf8");
const preparation = fs.readFileSync(path.join(root, "scripts", "prepare-handheld-runtime.ps1"), "utf8");
const publisher = fs.readFileSync(path.join(root, "scripts", "publish-installer.ps1"), "utf8");
const variants = fs.readFileSync(path.join(root, "scripts", "publish-installer-variants.ps1"), "utf8");
const xboxSnapshot = fs.readFileSync(path.join(root, "scripts", "XboxHostPayloadSnapshot.ps1"), "utf8");
const systemControl = fs.readFileSync(
  path.join(root, "src", "SteamLoader.App", "Infrastructure", "Handheld", "HandheldSystemControlService.cs"),
  "utf8",
);
const replacementRuntime = fs.readFileSync(
  path.join(root, "src", "SteamLoader.App", "Infrastructure", "Handheld", "HandheldReplacementRuntime.cs"),
  "utf8",
);

function requireHash(source, pattern, label) {
  const match = source.match(pattern);
  assert.ok(match, `${label} hash is missing`);
  assert.match(match[1], /^[a-f\d]{64}$/i, `${label} must contain exactly 64 hexadecimal characters`);
  return match[1].toLowerCase();
}

test("installer and preparation script pin the same handheld driver hashes", () => {
  const installerUsbIp = requireHash(installer, /UsbIpSetupSha256\s*=\s*'([^']+)'/i, "installer USBIP");
  const installerHidHide = requireHash(installer, /HidHideSetupSha256\s*=\s*'([^']+)'/i, "installer HidHide");
  const preparedUsbIp = requireHash(
    preparation,
    /Name\s*=\s*"USBIP-Win2[^\n]+[\s\S]*?Sha256\s*=\s*"([^"]+)"/i,
    "preparation USBIP",
  );
  const preparedHidHide = requireHash(
    preparation,
    /Name\s*=\s*"HidHide[^\n]+[\s\S]*?Sha256\s*=\s*"([^"]+)"/i,
    "preparation HidHide",
  );

  assert.equal(installerUsbIp, preparedUsbIp);
  assert.equal(installerHidHide, preparedHidHide);
});

test("installer builds must bind verification to the packaged Xbox host version", () => {
  assert.doesNotMatch(installer, /#define\s+XboxHostBuildVersion\s+"[^"]+"/i);
  assert.match(installer, /#error\s+XboxHostBuildVersion\s+must match/i);
  assert.match(installer, /#error\s+XboxHostRequiresDeveloperMode\s+must match/i);
  assert.match(installer, /#error\s+XboxHostPayloadDir\s+must point to the immutable/i);
  assert.match(xboxSnapshot, /AppxManifest\.xml/i);
  assert.match(xboxSnapshot, /Guid.*NewGuid/i);
  assert.match(publisher, /New-XboxHostPayloadSnapshot/i);
  assert.match(publisher, /\/DXboxHostPayloadDir=\$\(\$xboxHostSnapshot\.Directory\)/i);
  assert.match(variants, /New-XboxHostPayloadSnapshot/i);
  assert.match(variants, /\/DXboxHostBuildVersion=\$xboxHostPackageVersion/i);
  assert.match(variants, /\/DXboxHostRequiresDeveloperMode=\$xboxHostDeveloperModeDefine/i);
  assert.match(variants, /\/DXboxHostPayloadDir=\$\(\$xboxHostSnapshot\.Directory\)/i);
});

test("MSI OEM takeover leaves the installed Store package intact", () => {
  assert.doesNotMatch(systemControl, /Remove-AppxPackage/i);
  assert.doesNotMatch(systemControl, /Add-AppxPackage/i);
  assert.match(systemControl, /SetMsiServiceStartMode\("Disabled"\)/);
  assert.match(systemControl, /KillMsiCenterProcesses\(\)/);
});

test("mandatory OEM preparation is elevated and replacement failures cannot retry-loop", () => {
  assert.match(installer, /PrivilegesRequired=admin/i);
  assert.match(installer, /Exec\([\s\S]*?'--prepare-handheld-oem'/i);
  assert.match(replacementRuntime, /PrepareOemSoftware/);
  assert.match(replacementRuntime, /Requested\s*=\s*false,[\s\S]*?Phase\s*=\s*"failed-safe"/i);
});

test("updates stop elevated helpers through an elevated preflight after requesting shutdown", () => {
  assert.match(installer, /function\s+StopElevatedHelperTasksForInstall:\s*Boolean/i);
  assert.match(installer, /StopElevatedHelperTasksForInstall[\s\S]*?\/Change \/TN \$task \/Disable/i);
  const prepareToInstall = installer.slice(installer.indexOf("function PrepareToInstall"));
  assert.ok(
    prepareToInstall.indexOf("RequestToolsForSteamShutdown") <
      prepareToInstall.indexOf("StopElevatedHelperTasksForInstall"),
    "the background watchdog must stop before elevated helpers",
  );
});

test("installer requests elevation once and suspends helper restart policies during updates", () => {
  assert.match(installer, /PrivilegesRequired=admin/i);
  assert.doesNotMatch(installer, /ShellExec\([\s\S]{0,80}?'runas'/i);
  assert.match(installer, /\/Change \/TN \$task \/Disable/i);
  assert.match(installer, /procedure\s+ResumeElevatedHelperTasks/i);
});

test("handheld updates suspend the replacement without restoring MSI Center M", () => {
  assert.match(installer, /ToolsForSteamUpdateHelper\.exe[\s\S]*?dontcopy/i);
  assert.match(installer, /--suspend-handheld-replacement-for-update/i);
  assert.doesNotMatch(installer, /Type:\s*files;\s*Name:\s*"\{app\}\\data\\handheld-replacement-state\.json"/i);

  const suspension = replacementRuntime.slice(
    replacementRuntime.indexOf("public static int SuspendForUpdate"),
    replacementRuntime.indexOf("public static int RemoveOwnedDrivers"),
  );
  assert.match(suspension, /enabled:\s*false/i);
  assert.doesNotMatch(suspension, /enabled:\s*true/i);
  assert.match(suspension, /Requested\s*=\s*true/i);
  assert.match(suspension, /Phase\s*=\s*controllerRestored\s*\?\s*"update-suspended"/i);

  const preflight = installer.slice(installer.indexOf("function PrepareToInstall"));
  assert.ok(
    preflight.indexOf("StopElevatedHelperTasksForInstall") <
      preflight.indexOf("SuspendHandheldReplacementForInstall"),
    "the running bridge must be stopped before the update helper suspends its controller state",
  );
});
