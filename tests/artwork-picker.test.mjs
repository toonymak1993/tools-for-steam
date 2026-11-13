import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const read = (path) => readFileSync(new URL(`../${path}`, import.meta.url), "utf8");
const source = read("src/SteamLoader.App/Assets/artwork-picker.js");

function load() {
  const window = { __tfsArtworkPickerTestHooks: true };
  const result = vm.runInContext(source, vm.createContext({ window, console }), { filename: "artwork-picker.js" });
  assert.equal(result, "test");
  return window.__tfsArtworkPickerInternals;
}

test("quality badges compare against Steam's slot size", () => {
  const { qualityOf } = load();
  assert.equal(qualityOf({ width: 600, height: 900 }, "grid_p").label, "Sharp");
  assert.equal(qualityOf({ width: 460, height: 690 }, "grid_p").label, "OK");
  assert.equal(qualityOf({ width: 300, height: 450 }, "grid_p").label, "Low");
  assert.equal(qualityOf({ width: 1024, height: 1024 }, "grid_p").label, "Sharp · crops");
  assert.equal(qualityOf({ width: 3840, height: 1240 }, "hero").tone, "good");
  assert.equal(qualityOf({ width: 400, height: 150 }, "logo").label, "Low");
  assert.equal(qualityOf({}, "icon").label, "Unknown size");
});

test("results are sorted best first", () => {
  const { sortAssets } = load();
  const sorted = sortAssets([
    { id: "low", width: 300, height: 450, score: 9 },
    { id: "big", width: 1200, height: 1800, score: 0 },
    { id: "sharp", width: 600, height: 900, score: 5 },
  ], "grid_p");
  assert.deepEqual(Array.from(sorted, (item) => item.id), ["big", "sharp", "low"]);
});

test("filters are normalized per type and become SteamGridDB query parameters", () => {
  const { normalizeFilter, buildAssetQuery, filterSummary } = load();
  const filter = normalizeFilter("hero", { styles: ["blurred", "white_logo", "blurred"], animation: "animated", square: true, nsfw: true });
  assert.deepEqual([...filter.styles], ["blurred"], "white_logo is not a hero style");
  assert.equal(filter.square, false, "square only exists for covers");
  assert.equal(
    buildAssetQuery(42, "hero", filter, 2),
    "/api/artwork/picker/assets?gameId=42&type=hero&page=2&styles=blurred&animation=animated&nsfw=1");
  assert.equal(
    buildAssetQuery(42, "grid_p", { square: true }, 0),
    "/api/artwork/picker/assets?gameId=42&type=grid_p&page=0&dimensions=512x512%2C1024x1024");
  assert.equal(filterSummary("grid_p", {}), "No filters");
  assert.equal(filterSummary("logo", { styles: ["white"], animation: "static" }), "White · Static only");
});

test("LB/RB cycle through the artwork tabs", () => {
  const { nextTypeId, chunk } = load();
  assert.equal(nextTypeId("grid_p", 1), "grid_l");
  assert.equal(nextTypeId("grid_p", -1), "icon");
  assert.equal(nextTypeId("icon", 1), "grid_p");
  assert.deepEqual(Array.from(chunk([1, 2, 3, 4, 5], 2), (row) => row.length), [2, 2, 1]);
});

test("the page is a shared-context route wired into TFS and the context menu", () => {
  assert.match(source, /if \(role && role !== "shared"\) return "skipped";/);
  assert.match(source, /const route = "\/tfs\/artworks\/:appId";/);
  assert.match(source, /Inspired by Playhub Artworks \(MIT, LoZazaMastro\); independent code\./);
  assert.match(source, /doc\.addEventListener\("vgp_onbuttondown", onButton, true\)/);
  assert.doesNotMatch(source, /PanelSectionRow/, "Field-in-Field squeezes Steam's controls");

  const host = read("src/SteamLoader.App/Hosting/SteamLoaderBackgroundHost.cs");
  assert.match(host, /EmbeddedAssetReader\.ReadText\("Assets\/artwork-picker\.js"\)/);
  assert.match(host, /ArtworkPicker = new ArtworkPickerService\(/);

  const server = read("src/SteamLoader.App/Hosting/SteamLoaderApiServer.cs");
  assert.match(server, /ArtworkPickerApiEndpoints\.TryHandleAsync\(request, response, ArtworkPicker, cancellationToken\)/);

  const artwork = read("src/SteamLoader.App/Assets/artwork-surface.js");
  assert.match(artwork, /window\.__tfsArtworks \|\| window\.opener\?\.__tfsArtworks/);
  assert.match(artwork, /page\?\.hasEditor\?\.\(\) === true && page\.openEditor\(pageAppId\)/);
});
