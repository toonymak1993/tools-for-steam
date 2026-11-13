import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

import {
  alertCurrency,
  alertIdentity,
  artworkSources,
  buildItemsUrl,
  buildStoreHome,
  buildTasteProfile,
  categoryProducts,
  createStoreData,
  displayPrice,
  formatMoney,
  libraryGamesFromApps,
  normalizeSteamItem,
  offerPriceLabel,
  priceLabel,
  productsFromSnapshot,
  reasonCaption,
  reasonSentence,
  refreshHomeProducts,
  steamUrlFor,
  STORE_CATEGORIES,
  STORE_CSS,
  STORE_PAGES,
  storeMatchScores,
  tagSimilarity,
  tagVector,
} from "../src/SteamLoader.App/Assets/tfs-store.mjs";

const source = readFileSync(new URL("../src/SteamLoader.App/Assets/tfs-store.mjs", import.meta.url), "utf8");

const ACTION = 19;
const ADVENTURE = 21;
const RPG = 122;
const HORROR = 1667;
const SHOOTER = 1774;
const OPEN_WORLD = 1695;
const SURVIVAL_HORROR = 3978;
const SINGLEPLAYER = 4182;

let nextId = 1;
function product(title, tagIds, extra = {}) {
  const id = extra.id || `steam:${1000 + nextId}`;
  nextId += 1;
  return {
    id,
    steamAppId: Number(id.split(":")[1]) || null,
    title,
    tagIds,
    discountPercent: 0,
    cheapestPrice: 19.99,
    ...extra,
  };
}

const now = Date.UTC(2026, 10, 9);
const daysAgo = (days) => now - days * 86400000;

test("tag vectors weight Steam's vote order and damp broad tags", () => {
  const vector = tagVector([HORROR, SINGLEPLAYER, ACTION, SURVIVAL_HORROR]);
  assert.ok(vector.get(HORROR) > vector.get(SURVIVAL_HORROR), "earlier tags weigh more");
  assert.ok(vector.get(SINGLEPLAYER) < 0.1, "Singleplayer says little about taste");
  assert.ok(vector.get(ACTION) < vector.get(SURVIVAL_HORROR), "Action is broad");
  assert.equal(tagVector([HORROR, HORROR]).size, 1);
  assert.equal(tagVector(null).size, 0);
});

test("tag similarity is a cosine between 0 and 1", () => {
  const horror = tagVector([HORROR, SURVIVAL_HORROR, ACTION]);
  const sameHorror = tagVector([HORROR, SURVIVAL_HORROR, ACTION]);
  const racing = tagVector([699, 701]);
  assert.ok(Math.abs(tagSimilarity(horror, sameHorror) - 1) < 1e-9);
  assert.equal(tagSimilarity(horror, racing), 0);
  assert.equal(tagSimilarity(horror, new Map()), 0);
});

test("library games come from Steam's collection store apps", () => {
  const games = libraryGamesFromApps([
    { app_type: 1, appid: 10, display_name: "Dead Space 3", minutes_playtime_forever: 600, rt_last_time_played: 1700000000, store_tag: [HORROR, ACTION], BIsInstalled: () => true },
    { app_type: 1, appid: 11, display_name: "Hollow Knight", minutes_playtime_forever: 30, rt_last_time_played: 0, m_setStoreTags: new Set([ADVENTURE]) },
    { app_type: 4, appid: 12, display_name: "A tool" },
    { app_type: 1073741824, appid: 99, display_name: "Shortcut" },
  ]);
  assert.equal(games.length, 2);
  assert.deepEqual(games[0], { appId: 10, name: "Dead Space 3", playtimeMinutes: 600, lastPlayedAt: 1700000000000, installed: true, tags: [HORROR, ACTION] });
  assert.deepEqual(games[1].tags, [ADVENTURE]);
});

test("the taste profile leans on long playtime and recent sessions", () => {
  const profile = buildTasteProfile([
    { name: "Horror fan", tags: [HORROR, SURVIVAL_HORROR], playtimeMinutes: 6000, lastPlayedAt: daysAgo(3) },
    { name: "Racing once", tags: [699], playtimeMinutes: 20, lastPlayedAt: daysAgo(900) },
  ], [], now);
  assert.equal(Math.round(profile.get(HORROR)), 100);
  assert.ok(profile.get(699) < 10);
});

test("the front page has a spotlight, unique shelves, a play-history shelf and categories", () => {
  const games = [
    { appId: 1, name: "Resident Evil Requiem", tags: [HORROR, SURVIVAL_HORROR, ACTION, 1742], playtimeMinutes: 1500, lastPlayedAt: daysAgo(2), installed: true },
    { appId: 2, name: "The Witcher 3", tags: [RPG, OPEN_WORLD, 1742, ADVENTURE], playtimeMinutes: 9000, lastPlayedAt: daysAgo(400) },
  ];
  const products = [
    ...Array.from({ length: 6 }, (_, index) => product(`Horror ${index}`, [HORROR, SURVIVAL_HORROR, ACTION, 1742], { discountPercent: 10 * index })),
    ...Array.from({ length: 6 }, (_, index) => product(`RPG ${index}`, [RPG, OPEN_WORLD, ADVENTURE, 1742])),
    ...Array.from({ length: 6 }, (_, index) => product(`Shooter ${index}`, [SHOOTER, ACTION], { discountPercent: 50 })),
    product("Wishlisted sale", [RPG, ADVENTURE], { isSteamWishlisted: true, isWishlisted: true, discountPercent: 40 }),
    product("Wishlisted sale 2", [HORROR, ACTION], { isSteamWishlisted: true, isWishlisted: true, discountPercent: 20 }),
  ];
  const home = buildStoreHome({ products, games, shelfSize: 4, tagName: (tag) => ({ [HORROR]: "Horror", [RPG]: "RPG" })[tag] || "", now });

  assert.ok(home.spotlight, "a spotlight is chosen");
  const ids = [home.spotlight.product.id, ...home.shelves.flatMap((shelf) => shelf.items.map((item) => item.product.id)), ...home.more.map((entry) => entry.id)];
  assert.equal(new Set(ids).size, ids.length, "every product appears once");
  assert.equal(home.shelves[0].kind, "forYou");
  const because = home.shelves.find((shelf) => shelf.kind === "becauseGame");
  assert.ok(because, "a play-history shelf exists");
  assert.ok(["Resident Evil Requiem", "The Witcher 3"].includes(because.game));
  assert.ok(because.items.every((item) => item.reason.kind === "game" && item.reason.game === because.game));
  assert.ok(home.shelves.every((shelf) => shelf.items.length <= 4));
  const keys = home.categories.map((category) => category.key);
  assert.ok(keys.includes("action") && keys.includes("rpg") && keys.includes("horror"));
  assert.equal(new Set(home.categories.map((category) => category.cover.id)).size, home.categories.length, "category covers differ");
});

test("a front page keeps its arrangement when product data refreshes", () => {
  const products = Array.from({ length: 12 }, (_, index) => product(`Game ${index}`, [ACTION, SHOOTER], { discountPercent: index }));
  const home = buildStoreHome({ products, games: [], shelfSize: 4, now });
  const updated = products.map((entry) => ({ ...entry, isLocallyWishlisted: true, isWishlisted: true }));
  const refreshed = refreshHomeProducts(home, updated);
  assert.deepEqual(
    refreshed.shelves.map((shelf) => shelf.items.map((item) => item.product.id)),
    home.shelves.map((shelf) => shelf.items.map((item) => item.product.id)),
  );
  assert.ok(refreshed.shelves[0].items.every((item) => item.product.isLocallyWishlisted));
  const fewer = refreshHomeProducts(home, updated.slice(1));
  assert.ok(!JSON.stringify(fewer).includes(`"${products[0].id}"`) || fewer.spotlight?.product.id !== products[0].id);
});

test("category lists and match badges follow ORBIT's rules", () => {
  const products = [product("A", [HORROR]), product("B", [HORROR]), product("C", [RPG])];
  const scores = new Map([[products[0].id, 10], [products[1].id, 40], [products[2].id, 0]]);
  assert.deepEqual(categoryProducts(products, "horror", scores).map((entry) => entry.title), ["B", "A"]);
  assert.deepEqual(categoryProducts(products, "nope", scores), []);
  const matches = storeMatchScores(products, scores);
  assert.equal(matches.get(products[1].id), 98);
  assert.equal(matches.get(products[2].id), 58);
  assert.ok(STORE_CATEGORIES.every((category) => Number.isInteger(category.tagId) && category.label));
});

test("reason copy matches ORBIT's store strings", () => {
  assert.equal(reasonCaption({ kind: "game", game: "Hades" }), "Like Hades");
  assert.equal(reasonCaption({ kind: "wishlist" }), "On your wishlist");
  assert.equal(reasonCaption({ kind: "genre", genre: "Horror" }), "Because of Horror");
  assert.equal(reasonSentence({ kind: "game", game: "Hades", recent: true }), "Because you're playing Hades");
  assert.equal(reasonSentence({ kind: "game", game: "Hades", recent: false }), "Because you played Hades");
  assert.equal(reasonCaption(undefined), "");
  assert.deepEqual(STORE_PAGES.map((page) => page.label), ["Discover", "Deals", "Wishlist", "Price alerts"]);
});

test("Steam store items become tags, copy, artwork and trailer urls", () => {
  const item = normalizeSteamItem({
    success: 1,
    appid: 1817070,
    tags: [{ tagid: 19, weight: 800 }, { tagid: 1695, weight: 981 }],
    basic_info: { short_description: " Swing through New York. ", developers: [{ name: "Insomniac Games" }] },
    release: { steam_release_date: 1660316340 },
    reviews: { summary_filtered: { percent_positive: 96, review_score_label: "Overwhelmingly Positive" } },
    assets: { asset_url_format: "steam/apps/1817070/${FILENAME}?t=1", library_hero: "library_hero.jpg", library_capsule_2x: "library_600x900_2x.jpg", header: "header.jpg" },
    screenshots: { all_ages_screenshots: [{ filename: "steam/apps/1817070/ss_1.jpg" }] },
    trailers: {
      highlights: [{
        trailer_name: "Launch Trailer",
        microtrailer: [{ filename: "1817070/1/micro.webm", type: "video/webm" }, { filename: "1817070/1/micro.mp4", type: "video/mp4" }],
        adaptive_trailers: [{ cdn_path: "1817070/1/dash_av1.mpd", encoding: "dash_av1" }, { cdn_path: "1817070/1/dash_h264.mpd", encoding: "dash_h264" }],
      }],
    },
  });
  assert.deepEqual(item.tags, [1695, 19], "sorted by vote weight");
  assert.equal(item.summary, "Swing through New York.");
  assert.deepEqual(item.developers, ["Insomniac Games"]);
  assert.equal(item.reviewLabel, "Overwhelmingly Positive");
  assert.equal(item.hero, "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1817070/library_hero.jpg?t=1");
  assert.equal(item.poster, "https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1817070/library_600x900_2x.jpg?t=1");
  assert.equal(item.microTrailer, "https://video.fastly.steamstatic.com/store_trailers/1817070/1/micro.mp4");
  assert.equal(item.dashTrailer, "https://video.fastly.steamstatic.com/store_trailers/1817070/1/dash_h264.mpd");
  assert.deepEqual(item.screenshots, ["https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1817070/ss_1.jpg"]);
  assert.equal(normalizeSteamItem({ success: 2, appid: 5 }), null);

  const url = new URL(buildItemsUrl([10, 20], "de"));
  assert.equal(url.origin + url.pathname, "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/");
  const input = JSON.parse(url.searchParams.get("input_json"));
  assert.deepEqual(input.ids, [{ appid: 10 }, { appid: 20 }]);
  assert.equal(input.context.country_code, "DE");
  assert.equal(input.data_request.include_trailers, true);
});

test("snapshot products keep the backend's own tags field untouched", () => {
  const snapshot = {
    wishlist: [{ id: "steam:1", steamAppId: 1, title: "One", tags: ["co-op night"] }],
    featuredDeals: [{ id: "steam:1", steamAppId: 1, title: "Duplicate" }, { id: "direct:gog:2", title: "Two" }],
    trending: [{ id: "steam:3", steamAppId: 3, title: "Three" }],
  };
  const items = new Map([[1, { appId: 1, tags: [HORROR], reviewPercent: 90 }]]);
  const products = productsFromSnapshot(snapshot, items);
  assert.deepEqual(products.map((entry) => entry.title), ["One", "Two", "Three"]);
  assert.deepEqual(products[0].tags, ["co-op night"]);
  assert.deepEqual(products[0].tagIds, [HORROR]);
  assert.equal(products[0].reviewPercent, 90);
  assert.deepEqual(products[1].tagIds, []);
});

test("prices follow the snapshot's display currency", () => {
  const game = { cheapestPrice: 22.49, cheapestPriceEur: 20.19, regionalPrice: 19.5, regionalCurrencyCode: "GBP" };
  assert.deepEqual(displayPrice(game, { displayCurrencyCode: "EUR" }), { value: 20.19, currency: "EUR" });
  assert.deepEqual(displayPrice(game, { displayCurrencyCode: "REGION" }), { value: 19.5, currency: "GBP" });
  assert.deepEqual(displayPrice(game, { displayCurrencyCode: "USD" }), { value: 22.49, currency: "USD" });
  assert.equal(priceLabel({}, {}), "Check price");
  assert.equal(priceLabel({ isUnreleased: true }, {}), "Coming soon");
  assert.equal(formatMoney(22.49, "USD"), "$22.49");
  assert.equal(formatMoney(0, "USD"), "");
  assert.equal(offerPriceLabel({ price: 9.99, currencyCode: "USD" }, {}), "$9.99");
  assert.equal(offerPriceLabel({}, {}), "Check price");
  assert.equal(alertCurrency({ displayCurrencyCode: "EUR" }), "EUR");
  assert.equal(alertCurrency({ displayCurrencyCode: "REGION", regionalCurrencyCode: "EUR" }), "EUR");
  assert.equal(alertCurrency({ displayCurrencyCode: "REGION", regionalCurrencyCode: "GBP" }), "USD");
  assert.equal(alertIdentity({ steamAppId: 42, gameId: "x" }), "steam:42");
  assert.equal(alertIdentity({ gameId: "Direct:GOG:7" }), "game:direct:gog:7");
  assert.equal(alertIdentity({ id: "direct:gog:7" }), alertIdentity({ gameId: "direct:gog:7" }), "a game and its alert share one identity");
});

test("store links open in Steam's own browser, Steam apps on their store page", () => {
  assert.equal(steamUrlFor("https://store.steampowered.com/app/1817070/", { steamAppId: 1817070 }), "steam://store/1817070");
  assert.equal(steamUrlFor("https://www.gog.com/en/game/x", {}), "steam://openurl/https://www.gog.com/en/game/x");
  assert.equal(steamUrlFor("javascript:alert(1)", {}), "");
  assert.equal(steamUrlFor("http://insecure.example", {}), "");
});

test("artwork prefers Steam's library art and keeps supplied images as fallback", () => {
  const sources = artworkSources({ steamAppId: 7, imageUrl: "https://images.gog.com/a.jpg", item: { poster: "https://shared.fastly.steamstatic.com/p.jpg" } }, "poster");
  assert.equal(sources[0], "https://shared.fastly.steamstatic.com/p.jpg");
  assert.ok(sources.includes("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/7/library_600x900.jpg"));
  assert.equal(sources.at(-1), "https://images.gog.com/a.jpg");
  assert.deepEqual(artworkSources({ title: "No art" }, "hero"), []);
});

function jsonResponse(payload) {
  return { ok: true, json: async () => payload };
}

test("store data posts wishlist changes without page-only fields and opens offers by provider id", async () => {
  const calls = [];
  const snapshot = { wishlist: [], trending: [], featuredDeals: [], alerts: [], displayCurrencyCode: "EUR" };
  const data = createStoreData({
    fetchJson: async (path, options = {}) => {
      calls.push({ path, body: options.body ? JSON.parse(options.body) : undefined });
      if (path.startsWith("api/store/offers")) return [{ storeName: "Steam" }];
      return snapshot;
    },
    fetchExternal: async () => jsonResponse({ response: { store_items: [] } }),
  });
  await data.toggleWishlist({ id: "steam:1", title: "One", tags: ["mine"], tagIds: [HORROR], item: { appId: 1 }, isLocallyWishlisted: false });
  const wishlistCall = calls.find((call) => call.path === "api/store/wishlist");
  assert.equal(wishlistCall.body.enabled, true);
  assert.deepEqual(wishlistCall.body.game, { id: "steam:1", title: "One", tags: ["mine"], isLocallyWishlisted: false });

  const offers = await data.offers({ id: "steam:1", priceProviderGameId: "steam:1" });
  assert.deepEqual(offers, [{ storeName: "Steam" }]);
  assert.equal(calls.at(-1).path, "api/store/offers?gameId=steam%3A1");

  await data.saveAlert({ id: "direct:gog:7", title: "Seven" }, 12.5, true);
  const alertCall = calls.find((call) => call.path === "api/store/alerts");
  assert.deepEqual(alertCall.body, {
    steamAppId: null,
    gameId: "direct:gog:7",
    title: "Seven",
    targetPrice: 12.5,
    currencyCode: "EUR",
    enabled: true,
    mode: "price",
    targetDiscountPercent: 0,
    snoozedUntilUtc: null,
  });
  data.deinit();
});

test("Steam items load once, in batches, for Steam games only", async () => {
  const urls = [];
  const data = createStoreData({
    fetchJson: async () => ({ wishlist: [], trending: [], featuredDeals: [], alerts: [] }),
    fetchExternal: async (url) => {
      urls.push(url);
      const ids = JSON.parse(new URL(url).searchParams.get("input_json")).ids.map((entry) => entry.appid);
      return jsonResponse({ response: { store_items: ids.map((appid) => ({ success: 1, appid, tags: [{ tagid: HORROR, weight: 1 }] })) } });
    },
  });
  const ids = Array.from({ length: 45 }, (_, index) => index + 1);
  await data.loadItems([...ids, null, 0, "x"]);
  assert.equal(urls.length, 2, "40 per request");
  assert.equal(data.items.size, 45);
  await data.loadItems(ids);
  assert.equal(urls.length, 2, "cached items are not fetched again");
  data.deinit();
});

test("a failed store load reports the backend message", async () => {
  const data = createStoreData({
    fetchJson: async () => {
      throw new Error("Store is offline.");
    },
    fetchExternal: async () => jsonResponse({}),
  });
  await data.load(true);
  assert.equal(data.error, "Store is offline.");
  assert.equal(data.snapshot, null);
  data.deinit();
});

test("the page is a Steam route built from Steam's own components", () => {
  assert.match(source, /core\.routes\.add\(STORE_ROUTE, pages\.StorePage/);
  assert.match(source, /core\.routes\.add\(DETAILS_ROUTE, pages\.StoreDetailsPage/);
  assert.match(source, /fnScrollIntoViewHandler: scrollHandler/, "cards hand Steam's focus scroll to the shelf-aware handler");
  assert.match(source, /navEntryPreferPosition: NAV_MAINTAIN_X/, "Up/Down keeps the column between shelves");
  assert.match(source, /onOptionsButton/, "Y toggles the wishlist like ORBIT");
  assert.match(source, /BUMPER_LEFT/, "LB/RB switch pages");
  assert.match(source, /__tfsTrailers\?\.internals\?\.createDashPlayer/, "full trailers reuse the TFS trailer player");
  for (const selector of [".tfs-store-card.gpfocus", ".tfs-store-edge", ".tfs-store-preview", ".tfs-store-spotlight", ".tfs-store-toolbar", ".tfs-store-theater"]) {
    assert.ok(STORE_CSS.includes(selector), `${selector} is styled`);
  }
  // Hooks may not follow an early return (the old details page did that).
  const details = source.slice(source.indexOf("function StoreDetailsPage"), source.indexOf("return { StorePage, StoreDetailsPage }"));
  const firstReturn = details.indexOf("\n    if (!game) {");
  assert.ok(firstReturn > 0);
  assert.ok(!/React\.use[A-Z]\w*\(/.test(details.slice(firstReturn)), "no hook after the first early return");
});
