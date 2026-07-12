import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

async function loadPlugin(relativePath) {
  const registrations = [];
  const sdk = {
    ui: {
      createScreenModel: (model) => model,
      createCommandSlot: (title, description, run, options = {}) => ({ title, description, run, ...options }),
    },
  };
  const source = await readFile(new URL(`../${relativePath}`, import.meta.url), "utf8");
  const window = {
    TfsPluginSdk: {
      register(manifest, setup) {
        registrations.push({ manifest, definition: setup(sdk) });
      },
    },
  };

  vm.runInNewContext(source, { window, console });
  return { registrations, sdk, source };
}

test("plugin template registers through SDK 1.0 and renders a controller screen", async () => {
  const { registrations, source } = await loadPlugin("sdk/plugin-template/dist/index.js");
  const registration = assertSingleRegistration(registrations);

  assert.equal(registration.manifest.sdkVersion, "1.0.0");
  assert.deepEqual([...registration.manifest.permissions], ["frontend"]);
  assert.equal(typeof registration.definition.createScreen, "function");
  assert.doesNotMatch(source, /\bsdk\s*\.\s*post\s*\(/);

  let refreshCount = 0;
  const firstScreen = registration.definition.createScreen({ refresh: () => refreshCount += 1 });
  assert.equal(firstScreen.slots.length, 1);
  firstScreen.slots[0].run();
  assert.equal(refreshCount, 1);
  const secondScreen = registration.definition.createScreen({ refresh() {} });
  assert.match(secondScreen.note, /triggered 1 time/);
});

test("Home Assistant example registers its complete manifest through SDK 1.0", async () => {
  const { registrations, source } = await loadPlugin("sdk/official-plugins/home-assistant/dist/index.js");
  const registration = assertSingleRegistration(registrations);

  assert.equal(registration.manifest.id, "home-assistant");
  assert.equal(registration.manifest.sdkVersion, "1.0.0");
  assert.deepEqual(
    [...registration.manifest.permissions],
    ["frontend", "storage", "secrets", "network"],
  );
  assert.deepEqual([...registration.manifest.networkHosts], ["<local>"]);
  assert.equal(typeof registration.definition.createScreen, "function");
  assert.equal(typeof registration.definition.configure, "function");
  assert.doesNotMatch(source, /ToolsForSteamCommunityPlugins\s*\??=/);
});

test("full-trust template declares a managed backend and registers through SDK 1.0", async () => {
  const { registrations } = await loadPlugin("sdk/full-trust-plugin-template/dist/index.js");
  const registration = assertSingleRegistration(registrations);

  assert.ok(registration.manifest.permissions.includes("native.full-trust"));
  assert.equal(registration.manifest.backend.entryPoint, "backend/plugin.ps1");
  assert.equal(registration.manifest.backend.runtime, "powershell");
  assert.equal(registration.manifest.backend.autoStart, true);
  assert.equal(typeof registration.definition.createScreen, "function");
});

function assertSingleRegistration(registrations) {
  assert.equal(registrations.length, 1);
  return registrations[0];
}
