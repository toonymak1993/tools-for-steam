import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const buildScript = fs.readFileSync(path.join(root, "tools", "Build-XboxHost.ps1"), "utf8");
const publishScript = fs.readFileSync(path.join(root, "scripts", "publish-installer.ps1"), "utf8");
const packageVerifier = fs.readFileSync(path.join(root, "installer", "XboxModePackage.ps1"), "utf8");
const installer = fs.readFileSync(path.join(root, "installer", "ToolsForSteam.iss"), "utf8");
const packageDirectory = path.join(root, "src", "ToolsForSteam.XboxHost", "Package");

test("unsigned SCCD packaging requires the explicit Developer Mode fallback", () => {
  assert.equal(fs.existsSync(path.join(packageDirectory, "CustomCapability.SCCD")), false);
  assert.equal(fs.existsSync(path.join(packageDirectory, "CustomCapability.DeveloperMode.SCCD")), true);
  assert.match(buildScript, /Parameter\(Mandatory\s*=\s*\$true\)[\s\S]{0,80}\$SccdPath/i);
  assert.match(buildScript, /\$AllowDeveloperModeSccd/i);
  assert.match(buildScript, /Assert-ProductionSccd/i);
  assert.match(buildScript, /unsigned placeholder catalog/i);
  assert.match(buildScript, /CheckSignature\(\$true\)/i);
  assert.match(buildScript, /Copy-Item[^\n]+\$resolvedSccdPath/i);
  assert.doesNotMatch(buildScript, /Join-Path \$templatePath "CustomCapability\.SCCD"/i);
});

test("installer publishing prefers Microsoft authorization and marks the fallback policy", () => {
  assert.match(publishScript, /TFS_XBOX_HOST_SCCD/i);
  assert.match(publishScript, /CustomCapability\.DeveloperMode\.SCCD/i);
  assert.match(publishScript, /AllowDeveloperModeSccd/i);
  assert.match(publishScript, /XboxHostRequiresDeveloperMode/i);
});

test("installer accepts a placeholder SCCD only with Windows Developer Mode enabled", () => {
  assert.match(packageVerifier, /function Assert-Sccd/i);
  assert.match(packageVerifier, /Test-XboxDeveloperModeEnabled/i);
  assert.match(packageVerifier, /Developer Mode SCCD accepted/i);
  assert.match(packageVerifier, /CheckSignature\(\$true\)/i);
  assert.match(packageVerifier, /certificate chain used to sign the Xbox host package/i);
  assert.ok(
    packageVerifier.indexOf("Assert-Sccd $PackagePath") < packageVerifier.indexOf("Add-AppxPackage -Path"),
    "SCCD validation must happen before Windows package registration",
  );
});

test("Xbox Mode selection discloses, enables, verifies, and failure-rolls back Developer Mode", () => {
  assert.match(installer, /This enables Windows Developer Mode system-wide/i);
  assert.match(installer, /function EnsureXboxDeveloperMode/i);
  assert.match(installer, /AllowDevelopmentWithoutDevLicense/i);
  assert.match(installer, /function IsXboxDeveloperModeEnabled/i);
  assert.match(installer, /procedure RollBackXboxDeveloperModeEnabledThisRun/i);
  assert.ok(
    installer.indexOf("if not EnsureXboxDeveloperMode") < installer.indexOf("RunXboxPackageTool('VerifyPayload'"),
    "Developer Mode must be enabled before the placeholder SCCD is verified",
  );
});
