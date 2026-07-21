import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import vm from "node:vm";

const pluginRoot = new URL("../sdk/official-plugins/crackwatch/", import.meta.url);
const execFileAsync = promisify(execFile);

test("Crackwatch is an optional full-trust Store plugin with a managed backend", async () => {
  const manifest = JSON.parse(await readFile(new URL("tfs-plugin.json", pluginRoot), "utf8"));
  const store = JSON.parse(await readFile(new URL("store.json", pluginRoot), "utf8"));
  const logo = await readFile(new URL("assets/crackrelease-logo.png", pluginRoot));
  const preview = await readFile(new URL("assets/preview.png", pluginRoot));

  assert.equal(manifest.id, "crackwatch");
  assert.equal(manifest.version, "0.3.0");
  assert.equal(manifest.sdkVersion, "1.0.0");
  assert.deepEqual(manifest.permissions, [
    "frontend",
    "storage",
    "notifications",
    "logging",
    "native.full-trust",
  ]);
  assert.equal(manifest.backend.entryPoint, "backend/plugin.ps1");
  assert.equal(manifest.backend.runtime, "powershell");
  assert.equal(manifest.backend.autoStart, true);
  assert.equal(manifest.backend.createNoWindow, true);
  assert.equal(store.category, "Game Information");
  assert.ok(store.tags.includes("crack-status"));
  assert.ok(store.tags.includes("favorites"));
  assert.deepEqual([...logo.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
  assert.deepEqual(logo, preview);
});

test("Crackwatch starts with Hot Games and renders all cracked games, favorites, and official artwork", async () => {
  const source = await readFile(new URL("dist/index.js", pluginRoot), "utf8");

  assert.match(source, /sdk\.lifecycle\.setTimeout/);
  assert.match(source, /sdk\.backend\.call\("refresh"/);
  assert.match(source, /timeoutMs: 60_000/);
  assert.match(source, /createFeatureNavigationSlot/);
  assert.match(source, /mediaImageSrc: game\.imageUrl/);
  assert.match(source, /Hot Games/);
  assert.match(source, /All cracked games/);
  assert.match(source, /My favorites/);
  assert.match(source, /favoriteIds/);
  assert.match(source, /cropped-crack-release-icon\.png/);
  assert.match(source, /createChoiceSlot/);
  assert.match(source, /sourceHost = "crackrelease\.com"/);
  assert.match(source, /url\.pathname\.startsWith\("\/wp-content\/uploads\/"\)/);
  assert.match(source, /New-crack notifications/);
  assert.match(source, /activeView = "hot"/);
  assert.match(source, /inputType: "search"/);
  assert.match(source, /Clear search/);
  assert.match(source, /tokens\.every/);
  assert.doesNotMatch(source, /sdk\.network\./);
});

test("Crackwatch registers and renders a cached game without blocking its screen model", async () => {
  const source = await readFile(new URL("dist/index.js", pluginRoot), "utf8");
  const backendCalls = [];
  const scheduledDelays = [];
  const storageWrites = [];
  let registration = null;
  const freshSnapshot = {
    sourceUrl: "https://crackrelease.com/games/",
    fetchedAtUtc: "2026-07-20T12:00:00.000Z",
    checkedAtUtc: "2026-07-20T12:00:00.000Z",
    games: [
      {
        sourceId: 42,
        title: "Example Game",
        status: "cracked",
        badge: "CRACKED D+2",
        dayOffset: 2,
        sourceUrl: "https://crackrelease.com/example-game/",
        imageUrl: "https://crackrelease.com/wp-content/uploads/example.webp",
        publishedAtUtc: "2026-07-18T12:00:00.000Z",
        updatedAtUtc: "2026-07-19T12:00:00.000Z",
      },
      {
        sourceId: 43,
        title: "Newest Example Game",
        status: "cracked",
        badge: "CRACKED D+0",
        dayOffset: 0,
        sourceUrl: "https://crackrelease.com/newest-example-game/",
        imageUrl: "https://crackrelease.com/wp-content/uploads/newest-example.webp",
        publishedAtUtc: "2026-07-20T12:00:00.000Z",
        updatedAtUtc: "2026-07-21T12:00:00.000Z",
      },
    ],
    allGames: [
      {
        sourceId: 42,
        title: "Example Game",
        status: "cracked",
        badge: "CRACKED D+2",
        dayOffset: 2,
        sourceUrl: "https://crackrelease.com/example-game/",
        imageUrl: "https://crackrelease.com/wp-content/uploads/example.webp",
        publishedAtUtc: "2026-07-18T12:00:00.000Z",
        updatedAtUtc: "2026-07-19T12:00:00.000Z",
      },
      {
        sourceId: 43,
        title: "Newest Example Game",
        status: "cracked",
        badge: "CRACKED D+0",
        dayOffset: 0,
        sourceUrl: "https://crackrelease.com/newest-example-game/",
        imageUrl: "https://crackrelease.com/wp-content/uploads/newest-example.webp",
        publishedAtUtc: "2026-07-20T12:00:00.000Z",
        updatedAtUtc: "2026-07-21T12:00:00.000Z",
      },
      {
        sourceId: 84,
        title: "Hot Example",
        status: "uncracked",
        badge: "UNCRACKED D+4",
        dayOffset: 4,
        sourceUrl: "https://crackrelease.com/hot-example/",
        imageUrl: "https://crackrelease.com/wp-content/uploads/hot-example.webp",
        publishedAtUtc: "2026-07-17T12:00:00.000Z",
        updatedAtUtc: "2026-07-20T13:00:00.000Z",
      },
    ],
    hotGames: [
      {
        sourceId: 84,
        rank: 1,
        title: "Hot Example",
        status: "uncracked",
        badge: "UNCRACKED D+4",
        dayOffset: 4,
        sourceUrl: "https://crackrelease.com/hot-example/",
        imageUrl: "https://crackrelease.com/wp-content/uploads/hot-example.webp",
        publishedAtUtc: "2026-07-17T12:00:00.000Z",
        updatedAtUtc: "2026-07-20T13:00:00.000Z",
      },
    ],
  };
  const makeSlot = (title, copy, onClick, options = {}) => ({ title, copy, onClick, ...options });
  const sdk = {
    storage: {
      get: async () => ({}),
      patch: async (value) => {
        storageWrites.push(value);
        return value;
      },
    },
    backend: {
      async call(method) {
        backendCalls.push(method);
        return method === "getSnapshot" ? { games: [] } : freshSnapshot;
      },
    },
    lifecycle: {
      setTimeout(_callback, delay) {
        scheduledDelays.push(delay);
        return () => {};
      },
    },
    notifications: { success: async () => ({}) },
    log: { info: async () => ({}), error: async () => ({}) },
    system: { open: async () => ({ opened: true }) },
    ui: {
      createScreenModel: (model) => model,
      createCommandSlot: makeSlot,
      createToggleSlot: (title, copy, value, onClick, options = {}) => ({
        ...makeSlot(title, copy, onClick, options),
        switchValue: value,
      }),
      createInlineStepperSlot: (title, copy, onMoveLeft, onMoveRight, options = {}) => ({
        ...makeSlot(title, copy, onMoveRight, options),
        onMoveLeft,
        onMoveRight,
      }),
      createChoiceSlot: makeSlot,
      createFeatureNavigationSlot: makeSlot,
    },
  };
  const window = {
    TfsPluginSdk: {
      register(manifest, setup) {
        registration = { manifest, definition: setup(sdk) };
      },
    },
  };

  vm.runInNewContext(source, { URL, window, console });
  assert.ok(registration);
  assert.equal(typeof registration.definition.createScreen, "function");
  await new Promise((resolve) => setImmediate(resolve));
  await registration.definition.refresh();
  const screen = registration.definition.createScreen({ refresh() {} });
  const hotGameSlot = screen.slots.find((slot) => slot.title === "Hot Example");

  assert.ok(hotGameSlot);
  assert.equal(hotGameSlot.mediaImageSrc, freshSnapshot.hotGames[0].imageUrl);
  assert.equal(hotGameSlot.badge, "UNCRACKED");
  assert.match(screen.subtitle, /^Hot Games/);
  assert.equal(screen.cards[0].imageSrc, "https://crackrelease.com/wp-content/uploads/2025/09/cropped-crack-release-icon.png");

  const categorySlot = screen.slots.find((slot) => slot.title === "Category");
  assert.ok(categorySlot);
  categorySlot.onMoveRight();
  const crackedScreen = registration.definition.createScreen({ refresh() {} });
  const crackedGames = Array.from(
    crackedScreen.slots.filter((slot) => slot.mediaImageSrc),
    (slot) => slot.title,
  );
  assert.deepEqual(crackedGames, ["Newest Example Game", "Example Game"]);
  assert.match(crackedScreen.subtitle, /^All cracked games/);

  const favoriteSlot = crackedScreen.slots.find((slot) => slot.copy === "Watch Example Game for status changes.");
  assert.ok(favoriteSlot);
  await favoriteSlot.onClick();
  assert.deepEqual(Array.from(storageWrites.at(-1).favoriteIds), ["42"]);

  crackedScreen.editors[0].onInput("hot, example");
  const searchScreen = registration.definition.createScreen({ refresh() {} });
  assert.match(searchScreen.subtitle, /^Search results/);
  assert.ok(searchScreen.slots.find((slot) => slot.title === "Hot Example"));
  assert.ok(searchScreen.slots.find((slot) => slot.title === "Clear search"));
  searchScreen.slots.find((slot) => slot.title === "Clear search").onClick();

  const clearedScreen = registration.definition.createScreen({ refresh() {} });
  assert.match(clearedScreen.subtitle, /^All cracked games/);
  clearedScreen.slots.find((slot) => slot.title === "Category").onMoveRight();
  const favoritesScreen = registration.definition.createScreen({ refresh() {} });
  assert.ok(favoritesScreen.slots.find((slot) => slot.title === "Example Game"));
  assert.ok(backendCalls.includes("getSnapshot"));
  assert.ok(backendCalls.includes("refresh"));
  assert.ok(scheduledDelays.includes(60 * 60_000));
});

test("Crackwatch backend preserves all statuses, extracts Hot Games, and writes an offline cache", async () => {
  const backend = await readFile(new URL("backend/plugin.ps1", pluginRoot), "utf8");

  assert.match(backend, /https:\/\/crackrelease\.com\/games\//);
  assert.match(backend, /\$homeUrl = "https:\/\/crackrelease\.com\/"/);
  assert.match(backend, /status.*cracked.*uncracked.*unreleased/s);
  assert.match(backend, /Get-CrackedGames/);
  assert.match(backend, /Get-HotGames/);
  assert.match(backend, /Get-CrackReleasePostDates/);
  assert.match(backend, /wp-json\/wp\/v2\/posts/);
  assert.match(backend, /schemaVersion = 3/);
  assert.match(backend, /updatedAtUtc/);
  assert.match(backend, /Descending = \$true/);
  assert.match(backend, /Hot Games/);
  assert.match(backend, /allGames = \$allGames/);
  assert.match(backend, /hotGames = \$hotGames/);
  assert.match(backend, /Test-CrackReleaseUri/);
  assert.match(backend, /crackwatch-cache\.json/);
  assert.match(backend, /If-None-Match/);
  assert.match(backend, /If-Modified-Since/);
  assert.match(backend, /File\]::Replace/);
});

test("Quick Access artwork uses lazy loading and asynchronous decoding", async () => {
  const [frontendLibrary, quickAccess] = await Promise.all([
    readFile(new URL("../src/SteamLoader.App/Assets/st-frontend-lib.js", import.meta.url), "utf8"),
    readFile(new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url), "utf8"),
  ]);

  const libraryFeatureImage = frontendLibrary.match(
    /className: "steamloader-feature-media",[\s\S]*?\}\)/,
  )?.[0];
  const quickAccessFeatureImage = quickAccess.match(
    /className: "steamloader-feature-media",[\s\S]*?\}\)/,
  )?.[0];

  assert.ok(libraryFeatureImage);
  assert.ok(quickAccessFeatureImage);
  assert.match(libraryFeatureImage, /loading: "lazy"/);
  assert.match(libraryFeatureImage, /decoding: "async"/);
  assert.match(quickAccessFeatureImage, /loading: "lazy"/);
  assert.match(quickAccessFeatureImage, /decoding: "async"/);
});

test("Quick Access editors render plugin search fields as single-line search inputs", async () => {
  const [frontendLibrary, quickAccess] = await Promise.all([
    readFile(new URL("../src/SteamLoader.App/Assets/st-frontend-lib.js", import.meta.url), "utf8"),
    readFile(new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url), "utf8"),
  ]);

  for (const source of [frontendLibrary, quickAccess]) {
    assert.match(source, /editor\.inputType === "search"/);
    assert.match(source, /const editorElementType = isSingleLineEditor \? "input" : "textarea"/);
    assert.match(source, /enterKeyHint: editor\.enterKeyHint/);
  }
});

test("community Store builder packages plugin backends and consumes per-plugin metadata", async () => {
  const [builder, pluginTool] = await Promise.all([
    readFile(new URL("../scripts/build-community-plugin-store.ps1", import.meta.url), "utf8"),
    readFile(new URL("../scripts/tfs-plugin.ps1", import.meta.url), "utf8"),
  ]);

  assert.match(builder, /store\.json/);
  assert.match(builder, /\$backendSource/);
  assert.match(builder, /category = \$catalogCategory/);
  assert.match(builder, /tags = @\(\$catalogTags\)/);
  assert.match(pluginTool, /store\.json/);
  assert.match(pluginTool, /category = \$catalogCategory/);
  assert.match(pluginTool, /tags = @\(\$catalogTags\)/);
});

test("sideload merges the public catalog and serializes single-value fields as arrays", {
  skip: process.platform !== "win32",
}, async () => {
  const testRoot = await mkdtemp(path.join(tmpdir(), "tfs-crackwatch-sideload-"));
  const runtimeData = path.join(testRoot, "data");
  const publicCatalogPath = path.join(testRoot, "public-catalog.json");
  const scriptPath = fileURLToPath(new URL("../scripts/tfs-plugin.ps1", import.meta.url));

  try {
    await mkdir(runtimeData, { recursive: true });
    await writeFile(publicCatalogPath, JSON.stringify({
      title: "Public Test",
      description: "Public entries.",
      plugins: [{
        id: "public-demo",
        title: "Public Demo",
        description: "Public test entry.",
        author: "Tests",
        category: "Utility",
        version: "1.0.0",
        sdkVersion: "1.0.0",
        permissions: ["frontend"],
        networkHosts: [],
        packageUrl: "https://example.com/public-demo.zip",
        packageSha256: "A".repeat(64),
        images: ["https://example.com/public-demo.png"],
        tags: ["public"],
      }],
    }), "utf8");

    await execFileAsync("powershell.exe", [
      "-NoProfile",
      "-ExecutionPolicy", "Bypass",
      "-File", scriptPath,
      "sideload", fileURLToPath(pluginRoot),
      "-RuntimeDataDirectory", runtimeData,
      "-CommunityCatalogPath", publicCatalogPath,
    ], { windowsHide: true });

    const catalog = JSON.parse(await readFile(path.join(runtimeData, "plugin-store", "catalog.json"), "utf8"));
    const source = JSON.parse(await readFile(path.join(runtimeData, "plugin-store", "catalog-source.json"), "utf8"));
    const crackwatch = catalog.plugins.find((plugin) => plugin.id === "crackwatch");

    assert.deepEqual(catalog.plugins.map((plugin) => plugin.id), ["public-demo", "crackwatch"]);
    assert.ok(crackwatch);
    assert.deepEqual(crackwatch.images, ["api/plugin-store/images/catalog/crackwatch.png"]);
    assert.deepEqual(crackwatch.networkHosts, []);
    assert.equal(source.localDevelopment, true);
  } finally {
    await rm(testRoot, { recursive: true, force: true });
  }
});
