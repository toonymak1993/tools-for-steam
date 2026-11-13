import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import {
  buildCatalogQuery,
  createThemesStoreData,
  formatCompactCount,
  galleryOf,
  hasRealDescription,
  installState,
  versionLabel,
  mergeCatalogItems,
  targetLabel,
  THEMES_STORE_CSS,
} from "../src/SteamLoader.App/Assets/tfs-themes-store.mjs";

const theme = (storeId, extra = {}) => ({
  storeId,
  themeId: `Theme ${storeId}`,
  title: `Theme ${storeId}`,
  author: "someone",
  previewImageUrl: `https://api.deckthemes.com/blobs/${storeId}`,
  previewThumbnailUrl: `https://api.deckthemes.com/blobs/${storeId}/thumb?maxWidth=640`,
  ...extra,
});

function createFetcher(handlers) {
  const calls = [];
  const respond = async (path, body) => {
    calls.push({ path, body });
    for (const [prefix, handler] of Object.entries(handlers)) {
      if (path.startsWith(prefix)) {
        return handler(path, body);
      }
    }
    throw new Error(`unexpected ${path}`);
  };
  return {
    calls,
    fetchJson: (path) => respond(path),
    postJson: (path, body) => respond(path, body),
  };
}

test("formats counts and labels like the store cards show them", () => {
  assert.equal(formatCompactCount(0), "0");
  assert.equal(formatCompactCount(999), "999");
  assert.equal(formatCompactCount(1000), "1K");
  assert.equal(formatCompactCount(15400), "15.4K");
  assert.equal(formatCompactCount(2500000), "2.5M");
  assert.equal(targetLabel({ targets: ["Keyboard", "Background", "Library"] }), "Keyboard + Background");
  assert.equal(targetLabel({ target: "Profile" }), "Profile");
  assert.equal(targetLabel({}), "Big Picture");
});

test("version and description only show real values", () => {
  assert.equal(versionLabel("1.2.0"), "v1.2.0");
  assert.equal(versionLabel("v2.1"), "v2.1");
  assert.equal(versionLabel("23a6"), "");
  assert.equal(hasRealDescription({ description: "Available from DeckThemes." }), false);
  assert.equal(hasRealDescription({ description: "Rounded corners everywhere." }), true);
});

test("install state names the A-button action", () => {
  assert.deepEqual(installState({ installed: false }), { id: "available", label: "", action: "Install" });
  assert.equal(installState({ installed: true, installedVersionMatches: false }).action, "Update");
  assert.equal(installState({ installed: true, installedVersionMatches: true }).label, "Installed");
});

test("gallery keeps every https preview once, full size first", () => {
  const images = galleryOf({
    galleryImageUrls: ["https://a/1", "https://a/2", "https://a/1"],
    previewImageUrl: "https://a/1",
    previewThumbnailUrl: "https://a/1/thumb",
  });
  assert.deepEqual(images, ["https://a/1", "https://a/2", "https://a/1/thumb"]);
  assert.deepEqual(galleryOf({ previewImageUrl: "javascript:alert(1)" }), []);
});

test("catalog query clamps paging and skips empty values", () => {
  assert.equal(buildCatalogQuery({ search: "  round ", page: 0 }), "search=round&page=1&perPage=24");
  assert.equal(
    buildCatalogQuery({ filter: "Keyboard", order: "Most Stars", page: 3, perPage: 100 }),
    "filter=Keyboard&order=Most+Stars&page=3&perPage=24",
  );
});

test("merging pages drops duplicates", () => {
  const merged = mergeCatalogItems([theme("a"), theme("b")], [theme("b"), theme("c"), { title: "no id" }]);
  assert.deepEqual(merged.map((item) => item.storeId), ["a", "b", "c"]);
});

test("loads, appends and re-queries the DeckThemes catalog", async () => {
  const fetcher = createFetcher({
    "api/themes/store?": (path) => {
      const query = new URLSearchParams(path.split("?")[1]);
      const page = Number(query.get("page"));
      return {
        search: query.get("search") || "",
        filter: query.get("filter") || "All",
        order: query.get("order") || "Most Downloaded",
        page,
        perPage: 24,
        total: 3,
        availableFilters: ["All", "Keyboard"],
        availableOrders: ["Most Downloaded", "Most Stars"],
        items: page === 1 ? [theme("a"), theme("b")] : [theme("b"), theme("c")],
      };
    },
  });
  const data = createThemesStoreData(fetcher);
  await data.loadCatalog();
  assert.equal(data.items.length, 2);
  assert.equal(data.hasMore, true);
  assert.equal(data.query.order, "Most Downloaded");

  await data.loadCatalog({ append: true });
  assert.deepEqual(data.items.map((item) => item.storeId), ["a", "b", "c"]);
  assert.equal(data.hasMore, false);
  assert.match(fetcher.calls.at(-1).path, /page=2/);

  // Cached: no new request within the refresh window.
  const before = fetcher.calls.length;
  await data.loadCatalog();
  assert.equal(fetcher.calls.length, before);

  data.setQuery({ filter: "Keyboard" });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.match(fetcher.calls.at(-1).path, /filter=Keyboard&order=Most\+Downloaded&page=1/);
});

test("install refreshes the entry and its card; enable uses the installed theme id", async () => {
  const fetcher = createFetcher({
    "api/themes/store?": () => ({ page: 1, total: 1, items: [theme("a", { installed: false })] }),
    "api/themes/store/install": () => ({ integration: { backendReachable: true }, installedThemes: [{ id: "Theme a", enabled: false }] }),
    "api/themes/store/theme": () => theme("a", { installed: true, installedVersionMatches: true, galleryImageUrls: ["https://x/1"] }),
    "api/themes/themes/enabled": (_, body) => ({ integration: { backendReachable: true }, installedThemes: [{ id: "Theme a", enabled: body.enabled }] }),
  });
  const data = createThemesStoreData(fetcher);
  await data.loadCatalog();
  await data.install(data.items[0]);
  assert.deepEqual(fetcher.calls.find((call) => call.path === "api/themes/store/install").body, { storeThemeId: "a" });
  assert.equal(data.items[0].installed, true);
  assert.equal(data.themeById("a").galleryImageUrls[0], "https://x/1");
  assert.equal(data.backendReachable, true);
  assert.equal(data.installedTheme(data.themeById("a")).enabled, false);

  await data.setEnabled(data.themeById("a"), true);
  assert.deepEqual(fetcher.calls.at(-1).body, { themeId: "Theme a", enabled: true });
  assert.equal(data.installedTheme(data.themeById("a")).enabled, true);
  assert.equal(data.pending.size, 0);
});

test("install errors are shown, not thrown", async () => {
  const fetcher = createFetcher({
    "api/themes/store/install": () => {
      throw new Error("CSSLoader backend is not reachable.");
    },
  });
  const data = createThemesStoreData(fetcher);
  await data.install(theme("z"));
  assert.equal(data.actionError, "CSSLoader backend is not reachable.");
  assert.equal(data.pending.size, 0);
});

test("styles cover the preview grid, focus ring and details gallery", () => {
  for (const selector of [".tfs-themes-grid", ".tfs-themes-card.gpfocus", ".tfs-themes-stage", ".tfs-themes-thumb.gpfocus", ".tfs-themes-skeleton"]) {
    assert.ok(THEMES_STORE_CSS.includes(selector), selector);
  }
});

test("TFS loads the module on tfs-core and the QAM Store entry opens it", () => {
  const host = readFileSync(new URL("../src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs", import.meta.url), "utf8");
  assert.match(host, /AddModule\(\s*"tfs-themes-store"/);
  assert.match(host, /\[themesStoreBundle\b/);
  const injector = readFileSync(new URL("../src/SteamLoader.App/Hosting/QuickAccessShellInjector.cs", import.meta.url), "utf8");
  assert.match(injector, /foreach \(var moduleBundle in _sharedModuleBundles\)/);
  const popup = readFileSync(new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url), "utf8");
  assert.match(popup, /plugin\.id === "themes" && page\.id === "store" && openNativeThemesStore\(\)/);
  assert.match(popup, /window\.opener\?\.ToolsForSteamThemesStore/);
});
