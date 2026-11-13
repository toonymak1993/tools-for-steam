// Tools for Steam Store as native Big Picture pages.
//
// Runs as an ES module in Steam's SharedJSContext on top of tfs-core.mjs: the
// pages are real Gamepad UI routes (Steam owns focus, B/back and the footer
// legend) built on Steam's own Focusable/DialogButton/TextField components.
// Layout, copy and behaviour follow ORBIT's Store (StoreView.tsx and
// storeRecommendations.ts): toolbar with LB/RB page rail, personal front page
// (spotlight, shelves, categories, redeem banner), deals, wishlist, price
// alerts, tile trailer previews and a details page with offers and alerts.
//
// Prices, wishlist and alerts come from the TFS backend (api/store/*). Tags,
// descriptions, reviews and trailers come straight from Steam's public
// IStoreBrowseService (CORS allows steamloopback.host), the player's taste
// from Steam's own library (collectionStore) in this context.

const STORE_ROUTE = "/tfs/store";
const DETAILS_ROUTE = "/tfs/store/game/:gameId";
const SNAPSHOT_REFRESH_MS = 60000;
const REFRESH_POLL_MS = 4000;
const STEAM_ITEMS_URL = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/";
const STEAM_TAGS_URL = "https://api.steampowered.com/IStoreService/GetTagList/v1/?language=english";
const STEAM_ASSET_BASE = "https://shared.fastly.steamstatic.com/store_item_assets/";
const STEAM_VIDEO_BASE = "https://video.fastly.steamstatic.com/store_trailers/";
const ITEMS_CACHE_KEY = "ToolsForSteamStoreItemsV1";
const TAGS_CACHE_KEY = "ToolsForSteamStoreTagNamesV1";
const ITEMS_CACHE_MS = 24 * 60 * 60 * 1000;
const TAGS_CACHE_MS = 7 * 24 * 60 * 60 * 1000;
const ITEMS_BATCH = 40;
const DAY_MS = 86400000;

// Steam's GamepadButton enum (see tfs-discord.mjs).
const BUTTON = Object.freeze({ BUMPER_LEFT: 5, BUMPER_RIGHT: 6, TRIGGER_LEFT: 7, TRIGGER_RIGHT: 8 });
// Steam's NavEntryPositionPreference: keep the column when entering a row.
const NAV_MAINTAIN_X = 2;

// ---------------------------------------------------------------------------
// Copy (ORBIT's English store strings)
// ---------------------------------------------------------------------------

export const STORE_PAGES = Object.freeze([
  { id: "discover", label: "Discover", icon: "sparkles" },
  { id: "deals", label: "Deals", icon: "percent" },
  { id: "wishlist", label: "Wishlist", icon: "heart" },
  { id: "alerts", label: "Price alerts", icon: "bell" },
]);

/** ORBIT's store categories, keyed by Steam's genre tag ids. */
export const STORE_CATEGORIES = Object.freeze([
  { key: "action", tagId: 19, label: "Action" },
  { key: "adventure", tagId: 21, label: "Adventure" },
  { key: "rpg", tagId: 122, label: "Role-playing" },
  { key: "strategy", tagId: 9, label: "Strategy" },
  { key: "simulation", tagId: 599, label: "Simulation" },
  { key: "racing", tagId: 699, label: "Racing" },
  { key: "sports", tagId: 701, label: "Sports" },
  { key: "shooter", tagId: 1774, label: "Shooter" },
  { key: "horror", tagId: 1667, label: "Horror" },
  { key: "puzzle", tagId: 1664, label: "Puzzle" },
  { key: "indie", tagId: 492, label: "Indie" },
  { key: "casual", tagId: 597, label: "Casual" },
  { key: "mmo", tagId: 128, label: "Massively multiplayer" },
  { key: "free", tagId: 113, label: "Free to play" },
]);

export const STORE_REGIONS = Object.freeze([
  ["US", "United States"],
  ["DE", "Eurozone"],
  ["GB", "United Kingdom"],
  ["CA", "Canada"],
  ["AU", "Australia"],
  ["NZ", "New Zealand"],
  ["BR", "Brazil"],
  ["MX", "Mexico"],
  ["CL", "Chile"],
  ["CO", "Colombia"],
  ["JP", "Japan"],
  ["KR", "South Korea"],
  ["CN", "China"],
]);

// ---------------------------------------------------------------------------
// Taste model (ORBIT's storeRecommendations.ts on Steam tag ids)
// ---------------------------------------------------------------------------

/** Broad tags say little about taste on their own (ORBIT's GENRE_SPECIFICITY). */
const TAG_SPECIFICITY = new Map([
  [19, 0.35], [21, 0.35], [492, 0.35], [597, 0.35], [113, 0.2], [493, 0],
  [4182, 0.08], [3859, 0.2], [7481, 0], [4191, 0.12], [3871, 0.2], [4166, 0.3],
  [1756, 0.15], [7948, 0.1], [4305, 0.15], [5411, 0.15], [4175, 0.25], [4136, 0.35],
  [4726, 0.35], [4252, 0.25], [4190, 0.1], [4711, 0.2], [1693, 0.25], [1669, 0.2],
  [3993, 0.35], [4106, 0.5], [1697, 0.45], [3839, 0.4], [1742, 0.5], [3834, 0.5],
  [7208, 0.25], [4747, 0.35], [6971, 0.35], [6426, 0.45], [4145, 0.35], [4342, 0.35],
  [4345, 0.25], [4667, 0.25], [6650, 0], [12095, 0], [5350, 0.3], [1685, 0.4],
  [3843, 0.4], [1775, 0.4], [4026, 0.45], [1684, 0.5], [3942, 0.5], [3968, 0.4],
]);

const RECENT_ANCHOR_DAYS = 60;
// Steam tag vectors are richer than ORBIT's genre lists, so similarity is a
// cosine: 0.4 reads as "clearly alike" for a caption, 0.24 is enough for a
// shelf built around one game in a catalog of a few dozen deals.
const ANCHOR_SIMILARITY = 0.4;
const SHELF_SIMILARITY = 0.24;
const ANCHOR_SHELVES = 2;
const MIN_SHELF_ITEMS = 4;
const MIN_CATEGORY_ITEMS = 4;
const MAX_CATEGORIES = 8;
const MORE_LIMIT = 48;
const CATEGORY_LIMIT = 72;
const TASTE_TAGS = 12;

export function tagSpecificity(tagId) {
  return TAG_SPECIFICITY.has(tagId) ? TAG_SPECIFICITY.get(tagId) : 1;
}

/** Tag vector of an ordered tag list (Steam orders by votes): tag -> weight. */
export function tagVector(tags, limit = TASTE_TAGS) {
  const vector = new Map();
  const list = Array.isArray(tags) ? tags.slice(0, limit) : [];
  list.forEach((tag, index) => {
    const id = Number(tag);
    if (Number.isFinite(id) && id > 0 && !vector.has(id)) {
      vector.set(id, (1 - index / (limit + 4)) * tagSpecificity(id));
    }
  });
  return vector;
}

/** Cosine similarity of two tag vectors (0..1). */
export function tagSimilarity(left, right) {
  if (!left?.size || !right?.size) {
    return 0;
  }
  let dot = 0;
  let leftNorm = 0;
  let rightNorm = 0;
  for (const [tag, weight] of left) {
    leftNorm += weight * weight;
    const other = right.get(tag);
    if (other) dot += weight * other;
  }
  for (const weight of right.values()) rightNorm += weight * weight;
  return leftNorm && rightNorm ? dot / Math.sqrt(leftNorm * rightNorm) : 0;
}

/** Long playtime, recent sessions and installed titles describe taste best. */
export function libraryTasteWeight(game, now = Date.now()) {
  const playtime = Math.min(Math.log2(1 + (Number(game?.playtimeMinutes) || 0) / 60) * 5, 28);
  const last = Number(game?.lastPlayedAt) || 0;
  const ageDays = last ? (now - last) / DAY_MS : Infinity;
  const recency = ageDays < 30 ? 16 : ageDays < 180 ? 9 : ageDays < 365 ? 4 : 0;
  return 1 + playtime + recency + (game?.installed ? 5 : 0);
}

/** Library games from Steam's collectionStore apps (pure, for tests). */
export function libraryGamesFromApps(apps) {
  const games = [];
  for (const app of Array.isArray(apps) ? apps : []) {
    if (!app || Number(app.app_type) !== 1) continue;
    let tags = [];
    try {
      tags = Array.isArray(app.store_tag) ? app.store_tag : app.m_setStoreTags ? [...app.m_setStoreTags] : [];
    } catch {
      tags = [];
    }
    let installed = false;
    try {
      installed = typeof app.BIsInstalled === "function"
        ? Boolean(app.BIsInstalled())
        : Boolean(app.installed || app.per_client_data?.some?.((entry) => entry?.installed));
    } catch {
      installed = false;
    }
    games.push({
      appId: Number(app.appid) || 0,
      name: String(app.display_name || ""),
      playtimeMinutes: Number(app.minutes_playtime_forever) || 0,
      lastPlayedAt: (Number(app.rt_last_time_played) || 0) * 1000,
      installed,
      tags,
    });
  }
  return games;
}

export function buildTasteProfile(games, products, now = Date.now()) {
  const profile = new Map();
  const add = (tags, weight) => {
    for (const [tag, value] of tagVector(tags)) {
      profile.set(tag, (profile.get(tag) || 0) + value * weight);
    }
  };
  for (const game of games || []) add(game.tags, libraryTasteWeight(game, now));
  for (const product of products || []) {
    if (product.isSteamWishlisted || product.isLocallyWishlisted || product.isWishlisted) {
      add(product.tagIds, product.isSteamWishlisted ? 18 : 14);
    }
  }
  const ceiling = Math.max(1, ...profile.values());
  for (const [tag, value] of profile) profile.set(tag, (value / ceiling) * 100);
  return profile;
}

export function isWishlisted(game) {
  return Boolean(game?.isWishlisted || game?.isSteamWishlisted || game?.isLocallyWishlisted);
}

export function personalScore(product, profile) {
  const vector = tagVector(product?.tagIds);
  let affinity = 0;
  let total = 0;
  for (const [tag, weight] of vector) {
    affinity += (profile?.get(tag) || 0) * weight;
    total += weight;
  }
  affinity = total ? affinity / total : 0;
  const discount = Math.min(Number(product?.discountPercent) || 0, 40);
  const reviews = Number(product?.reviewPercent) || 0;
  const wishlist = product?.isSteamWishlisted ? 30 : isWishlisted(product) ? 20 : 0;
  return affinity + reviews * 0.12 + discount * 0.18 + wishlist;
}

/** Match badges stay in a calm 58-98 % band relative to the best candidate. */
export function storeMatchScores(products, scores) {
  const ceiling = Math.max(1, ...products.map((product) => scores.get(product.id) || 0));
  return new Map(products.map((product) => [
    product.id,
    Math.min(98, Math.max(58, Math.round(58 + ((scores.get(product.id) || 0) / ceiling) * 40))),
  ]));
}

function hasTag(product, tagId, limit = 10) {
  return Array.isArray(product?.tagIds) && product.tagIds.slice(0, limit).includes(tagId);
}

/**
 * Builds the personal front page: one spotlight, deduplicated one-row shelves
 * (each product appears once), categories and "more". Pure, so the page can
 * memoize it and tests can run it.
 */
export function buildStoreHome({ products, games = [], shelfSize = 6, tagName = () => "", now = Date.now() }) {
  const size = Math.max(1, Math.floor(shelfSize));
  const profile = buildTasteProfile(games, products, now);
  const scores = new Map(products.map((product) => [product.id, personalScore(product, profile)]));
  const vectors = new Map(products.map((product) => [product.id, tagVector(product.tagIds)]));
  const ranked = [...products].sort((left, right) =>
    (scores.get(right.id) || 0) - (scores.get(left.id) || 0) ||
    (right.discountPercent || 0) - (left.discountPercent || 0));

  const anchors = (games || [])
    .filter((game) => game.name && game.tags?.length)
    .map((game) => ({
      game,
      vector: tagVector(game.tags),
      weight: libraryTasteWeight(game, now),
      recent: game.lastPlayedAt > 0 && now - game.lastPlayedAt < RECENT_ANCHOR_DAYS * DAY_MS,
    }))
    .filter((anchor) => anchor.recent || anchor.game.playtimeMinutes >= 120)
    .sort((left, right) =>
      Number(right.recent) - Number(left.recent) ||
      (right.recent ? right.game.lastPlayedAt - left.game.lastPlayedAt : right.weight - left.weight))
    .slice(0, 40);

  const reasonFor = (product) => {
    const vector = vectors.get(product.id);
    let best = null;
    for (const anchor of anchors) {
      if (anchor.game.appId && anchor.game.appId === Number(product.steamAppId)) continue;
      const value = tagSimilarity(vector, anchor.vector);
      if (value < ANCHOR_SIMILARITY) continue;
      const weighted = value * anchor.weight;
      if (!best || weighted > best.value) best = { anchor, value: weighted };
    }
    if (best) return { kind: "game", game: best.anchor.game.name, recent: best.anchor.recent };
    if (isWishlisted(product)) return { kind: "wishlist" };
    const genre = [...(vector?.keys() || [])]
      .filter((tag) => tagSpecificity(tag) >= 0.5 && tagName(tag))
      .sort((left, right) => (profile.get(right) || 0) - (profile.get(left) || 0))[0];
    return genre && (profile.get(genre) || 0) > 0 ? { kind: "genre", genre: tagName(genre) } : undefined;
  };

  const used = new Set();
  const take = (candidates, limit) => {
    const items = [];
    for (const product of candidates) {
      if (items.length >= limit) break;
      if (used.has(product.id)) continue;
      used.add(product.id);
      items.push({ product, reason: reasonFor(product) });
    }
    return items;
  };
  const release = (items) => items.forEach((item) => used.delete(item.product.id));
  const unused = (candidates) => candidates.filter((product) => !used.has(product.id));

  const shelves = [];
  const spotlightProduct = ranked.find((product) => product.heroImageUrl || product.headerImageUrl || product.steamAppId);
  const spotlight = spotlightProduct ? take([spotlightProduct], 1)[0] : undefined;

  const forYou = take(ranked, size);
  if (forYou.length) shelves.push({ id: "for-you", kind: "forYou", items: forYou });

  const wishlistDeals = take(ranked.filter((product) => isWishlisted(product) && (product.discountPercent || 0) > 0), size);
  if (wishlistDeals.length >= 2) shelves.push({ id: "wishlist-deals", kind: "wishlistDeals", items: wishlistDeals });
  else release(wishlistDeals);

  const anchorNames = new Set();
  for (const anchor of anchors) {
    if (shelves.filter((shelf) => shelf.kind === "becauseGame").length >= ANCHOR_SHELVES) break;
    const name = anchor.game.name.trim();
    if (!name || anchorNames.has(name.toLowerCase())) continue;
    const candidates = unused(ranked)
      .filter((product) => !(anchor.game.appId && anchor.game.appId === Number(product.steamAppId)))
      .map((product) => ({ product, value: tagSimilarity(vectors.get(product.id), anchor.vector) }))
      .filter((candidate) => candidate.value >= SHELF_SIMILARITY)
      .sort((left, right) => right.value - left.value || (scores.get(right.product.id) || 0) - (scores.get(left.product.id) || 0))
      .map((candidate) => candidate.product);
    if (candidates.length < Math.min(MIN_SHELF_ITEMS, size)) continue;
    const items = take(candidates, size).map((item) => ({ ...item, reason: { kind: "game", game: name, recent: anchor.recent } }));
    anchorNames.add(name.toLowerCase());
    shelves.push({ id: `because-${anchor.game.appId || name}`, kind: "becauseGame", game: name, recent: anchor.recent, items });
  }

  // Long-term taste without the recency boost, mixed with review scores.
  const allTime = buildTasteProfile((games || []).map((game) => ({ ...game, lastPlayedAt: 0 })), products, now);
  const youWillLike = take(
    unused(ranked).sort((left, right) =>
      personalScore(right, allTime) + (right.reviewPercent || 0) * 0.1 -
      (personalScore(left, allTime) + (left.reviewPercent || 0) * 0.1)),
    size,
  );
  if (youWillLike.length >= Math.min(MIN_SHELF_ITEMS, size)) shelves.push({ id: "you-will-like", kind: "youWillLike", items: youWillLike });
  else release(youWillLike);

  const deals = take(unused(ranked).filter((product) => (product.discountPercent || 0) > 0), size);
  if (deals.length >= Math.min(MIN_SHELF_ITEMS, size)) shelves.push({ id: "deals", kind: "deals", items: deals });
  else release(deals);

  const coverIds = new Set(spotlight ? [spotlight.product.id] : []);
  const categories = STORE_CATEGORIES
    .map((category) => ({ category, members: ranked.filter((product) => hasTag(product, category.tagId)) }))
    .filter(({ members }) => members.length >= MIN_CATEGORY_ITEMS)
    .sort((left, right) =>
      (profile.get(right.category.tagId) || 0) * tagSpecificity(right.category.tagId) -
      (profile.get(left.category.tagId) || 0) * tagSpecificity(left.category.tagId) ||
      right.members.length - left.members.length)
    .slice(0, MAX_CATEGORIES)
    .map(({ category, members }) => {
      // Neighbouring tiles should not share one cover.
      const cover = members.find((product) => !coverIds.has(product.id)) || members[0];
      coverIds.add(cover.id);
      return { key: category.key, label: category.label, count: members.length, cover };
    });

  return { spotlight, shelves, categories, more: unused(ranked).slice(0, MORE_LIMIT), scores };
}

/**
 * Keeps a front page's arrangement while refreshing product data, so a
 * wishlist toggle or background refresh never moves the focused card.
 */
export function refreshHomeProducts(layout, products) {
  const byId = new Map(products.map((product) => [product.id, product]));
  const item = (entry) => {
    const product = byId.get(entry.product.id);
    return product ? [{ ...entry, product }] : [];
  };
  return {
    spotlight: layout.spotlight ? item(layout.spotlight)[0] : undefined,
    shelves: layout.shelves.map((shelf) => ({ ...shelf, items: shelf.items.flatMap(item) })).filter((shelf) => shelf.items.length),
    categories: layout.categories.map((category) => ({ ...category, cover: byId.get(category.cover.id) || category.cover })),
    more: layout.more.flatMap((product) => byId.get(product.id) || []),
    scores: layout.scores,
  };
}

export function categoryProducts(products, key, scores) {
  const category = STORE_CATEGORIES.find((entry) => entry.key === key);
  if (!category) return [];
  return products
    .filter((product) => hasTag(product, category.tagId))
    .sort((left, right) => (scores.get(right.id) || 0) - (scores.get(left.id) || 0))
    .slice(0, CATEGORY_LIMIT);
}

export function reasonCaption(reason) {
  if (!reason) return "";
  if (reason.kind === "game") return `Like ${reason.game}`;
  if (reason.kind === "wishlist") return "On your wishlist";
  return `Because of ${reason.genre}`;
}

export function reasonSentence(reason) {
  if (!reason) return "";
  if (reason.kind === "game") return reason.recent ? `Because you're playing ${reason.game}` : `Because you played ${reason.game}`;
  if (reason.kind === "wishlist") return "On your wishlist";
  return `Because you play ${reason.genre}`;
}

// ---------------------------------------------------------------------------
// Steam store items (tags, descriptions, trailers)
// ---------------------------------------------------------------------------

function assetUrl(format, filename) {
  if (!format || !filename) return "";
  return `${STEAM_ASSET_BASE}${String(format).replace("${FILENAME}", filename)}`;
}

/** Compact the IStoreBrowseService item to what the pages use. */
export function normalizeSteamItem(item) {
  const appId = Number(item?.appid || item?.id) || 0;
  if (!appId || item?.success !== 1) return null;
  const tags = Array.isArray(item.tags) && item.tags.length
    ? [...item.tags].sort((left, right) => (right.weight || 0) - (left.weight || 0)).map((tag) => Number(tag.tagid))
    : Array.isArray(item.tagids) ? item.tagids.map(Number) : [];
  const highlight = item.trailers?.highlights?.[0] || item.trailers?.other_trailers?.[0] || null;
  const micro = highlight?.microtrailer?.find?.((entry) => entry?.type === "video/mp4")?.filename || "";
  const dash = highlight?.adaptive_trailers?.find?.((entry) => entry?.encoding === "dash_h264")?.cdn_path || "";
  const format = item.assets?.asset_url_format || "";
  const reviews = item.reviews?.summary_filtered || null;
  return {
    appId,
    // EStoreAppType: 0 game, 4 DLC, 10 hardware, 6 software, ...
    appType: Number.isFinite(Number(item.type)) ? Number(item.type) : 0,
    tags: tags.filter((tag) => Number.isFinite(tag) && tag > 0).slice(0, 20),
    summary: String(item.basic_info?.short_description || "").trim(),
    developers: (item.basic_info?.developers || []).map((entry) => entry?.name).filter(Boolean).slice(0, 2),
    releaseAt: (Number(item.release?.steam_release_date) || Number(item.release?.original_release_date) || 0) * 1000,
    comingSoon: Boolean(item.release?.is_coming_soon),
    reviewLabel: String(reviews?.review_score_label || ""),
    reviewPercent: Number(reviews?.percent_positive) || 0,
    hero: assetUrl(format, item.assets?.library_hero),
    poster: assetUrl(format, item.assets?.library_capsule_2x || item.assets?.library_capsule),
    header: assetUrl(format, item.assets?.header),
    trailerName: String(highlight?.trailer_name || ""),
    microTrailer: micro ? `${STEAM_VIDEO_BASE}${micro}` : "",
    dashTrailer: dash ? `${STEAM_VIDEO_BASE}${dash}` : "",
    screenshots: (item.screenshots?.all_ages_screenshots || [])
      .slice(0, 6)
      .map((shot) => (shot?.filename ? `${STEAM_ASSET_BASE}${shot.filename}` : ""))
      .filter(Boolean),
  };
}

export function buildItemsUrl(appIds, countryCode = "US") {
  const input = {
    ids: appIds.map((appid) => ({ appid })),
    context: { language: "english", country_code: String(countryCode || "US").toUpperCase() },
    data_request: {
      include_basic_info: true,
      include_tag_count: 20,
      include_release: true,
      include_reviews: true,
      include_screenshots: true,
      include_trailers: true,
      include_assets: true,
    },
  };
  return `${STEAM_ITEMS_URL}?input_json=${encodeURIComponent(JSON.stringify(input))}`;
}

function readCache(key, maxAgeMs) {
  try {
    const parsed = JSON.parse(globalThis.localStorage?.getItem(key) || "null");
    return parsed && Date.now() - (parsed.at || 0) < maxAgeMs ? parsed : null;
  } catch {
    return null;
  }
}

function writeCache(key, value) {
  try {
    globalThis.localStorage?.setItem(key, JSON.stringify({ at: Date.now(), ...value }));
  } catch {
    // storage full or unavailable
  }
}

// ---------------------------------------------------------------------------
// Prices
// ---------------------------------------------------------------------------

function isPositive(value) {
  const number = Number(value);
  return value != null && value !== "" && Number.isFinite(number) && number > 0;
}

const CURRENCY_LOCALES = {
  EUR: "de-DE", USD: "en-US", GBP: "en-GB", CAD: "en-CA", AUD: "en-AU", NZD: "en-NZ",
  BRL: "pt-BR", MXN: "es-MX", CLP: "es-CL", COP: "es-CO", JPY: "ja-JP", KRW: "ko-KR", CNY: "zh-CN",
};

export function formatMoney(value, currency = "USD") {
  if (!isPositive(value)) return "";
  const code = String(currency || "USD").toUpperCase();
  try {
    return new Intl.NumberFormat(CURRENCY_LOCALES[code] || "en-US", { style: "currency", currency: code }).format(Number(value));
  } catch {
    return `${Number(value).toFixed(2)} ${code}`;
  }
}

/** The price the snapshot's display mode asks for, as { value, currency }. */
export function displayPrice(game, snapshot) {
  const mode = String(snapshot?.displayCurrencyCode || "USD").toUpperCase();
  if (mode === "EUR" && isPositive(game?.cheapestPriceEur)) return { value: Number(game.cheapestPriceEur), currency: "EUR" };
  if ((mode === "REGION" || mode === "BOTH") && isPositive(game?.regionalPrice)) {
    return { value: Number(game.regionalPrice), currency: game.regionalCurrencyCode || snapshot?.regionalCurrencyCode || "USD" };
  }
  if (isPositive(game?.cheapestPrice)) return { value: Number(game.cheapestPrice), currency: "USD" };
  if (isPositive(game?.cheapestPriceEur)) return { value: Number(game.cheapestPriceEur), currency: "EUR" };
  return null;
}

export function priceLabel(game, snapshot) {
  const price = displayPrice(game, snapshot);
  if (price) return formatMoney(price.value, price.currency);
  return game?.isUnreleased ? "Coming soon" : "Check price";
}

export function offerPriceLabel(offer, snapshot) {
  const mode = String(snapshot?.displayCurrencyCode || "USD").toUpperCase();
  if (mode === "EUR" && isPositive(offer?.priceEur)) return formatMoney(offer.priceEur, "EUR");
  if ((mode === "REGION" || mode === "BOTH") && isPositive(offer?.regionalPrice)) {
    return formatMoney(offer.regionalPrice, offer.regionalCurrencyCode);
  }
  return formatMoney(offer?.price, offer?.currencyCode || "USD") || formatMoney(offer?.priceEur, "EUR") || "Check price";
}

/** Price alerts are kept in USD or EUR by the backend. */
export function alertCurrency(snapshot) {
  const display = String(snapshot?.displayCurrencyCode || "USD").toUpperCase();
  const regional = String(snapshot?.regionalCurrencyCode || "").toUpperCase();
  if (display === "EUR") return "EUR";
  if ((display === "REGION" || display === "BOTH") && regional === "EUR") return "EUR";
  return "USD";
}

export function alertIdentity(entry) {
  const appId = Number(entry?.steamAppId);
  if (Number.isSafeInteger(appId) && appId > 0) return `steam:${appId}`;
  const id = String(entry?.gameId ?? entry?.id ?? "").trim().toLowerCase();
  return id ? `game:${id}` : "";
}

// ---------------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------------

function apiUrl(path) {
  const base = globalThis.__steamLoaderApiBase || "";
  return new URL(String(path).replace(/^\//, ""), base).toString();
}

async function defaultFetchJson(path, options = {}) {
  const response = await fetch(apiUrl(path), {
    cache: "no-store",
    ...options,
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(payload?.message || `Request failed (${response.status}).`);
  return payload;
}

/** Products of the snapshot: wishlist first, unique by id, with Steam tags. */
export function productsFromSnapshot(snapshot, items) {
  const seen = new Map();
  for (const list of [snapshot?.wishlist, snapshot?.featuredDeals, snapshot?.trending]) {
    for (const game of Array.isArray(list) ? list : []) {
      if (!game?.id || seen.has(String(game.id))) continue;
      seen.set(String(game.id), withItem(game, items));
    }
  }
  return [...seen.values()];
}

/** Discover and Deals show games and DLC; Steam also lists hardware as apps. */
export function isStoreGame(product) {
  const type = product?.item?.appType;
  return type === undefined || type === 0 || type === 4;
}

export function withItem(game, items) {
  const item = game?.steamAppId ? items?.get?.(Number(game.steamAppId)) : null;
  return {
    ...game,
    id: String(game.id),
    // Not "tags": the backend uses that name for the player's wishlist tags.
    tagIds: item?.tags || [],
    reviewPercent: game.reviewPercent || item?.reviewPercent || 0,
    item: item || null,
  };
}

export function createStoreData({ fetchJson = defaultFetchJson, fetchExternal = (url) => fetch(url, { credentials: "omit" }) } = {}) {
  const postJson = (path, body) => fetchJson(path, { method: "POST", body: JSON.stringify(body ?? {}) });
  const listeners = new Set();
  const cachedItems = readCache(ITEMS_CACHE_KEY, ITEMS_CACHE_MS);
  const cachedTags = readCache(TAGS_CACHE_KEY, TAGS_CACHE_MS);
  let pollTimer = 0;

  const data = {
    snapshot: null,
    loading: false,
    refreshing: false,
    error: "",
    loadedAt: 0,
    revision: 0,
    gamesById: new Map(),
    items: new Map((cachedItems?.items || []).filter((item) => item?.appId).map((item) => [item.appId, item])),
    pendingItems: new Set(),
    tagNames: new Map(Object.entries(cachedTags?.names || {}).map(([id, name]) => [Number(id), name])),
    search: { query: "", results: [], loading: false, error: "" },
    pending: new Set(),
    notify() {
      data.revision += 1;
      for (const listener of [...listeners]) {
        try {
          listener();
        } catch {
          // ignore
        }
      }
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    remember(games) {
      for (const game of games || []) {
        if (game?.id) data.gamesById.set(String(game.id), game);
      }
    },
    applySnapshot(snapshot) {
      if (!snapshot || typeof snapshot !== "object" || !Array.isArray(snapshot.wishlist)) return;
      data.snapshot = snapshot;
      data.loadedAt = Date.now();
      data.remember(snapshot.wishlist);
      data.remember(snapshot.trending);
      data.remember(snapshot.featuredDeals);
      data.notify();
      void data.loadItems(productsFromSnapshot(snapshot, null).map((game) => game.steamAppId));
      data.schedulePoll();
    },
    schedulePoll() {
      clearTimeout(pollTimer);
      if (data.snapshot?.isRefreshing) {
        pollTimer = setTimeout(() => void data.load(true), REFRESH_POLL_MS);
      }
    },
    async load(force = false) {
      if (data.loading || (!force && data.snapshot && Date.now() - data.loadedAt < SNAPSHOT_REFRESH_MS)) return;
      data.loading = true;
      data.error = "";
      data.notify();
      try {
        data.applySnapshot(await fetchJson("api/store/state"));
      } catch (error) {
        data.error = String(error?.message || error);
      } finally {
        data.loading = false;
        data.notify();
      }
    },
    async refresh() {
      if (data.refreshing) return;
      data.refreshing = true;
      data.error = "";
      data.notify();
      try {
        data.applySnapshot(await postJson("api/store/refresh"));
      } catch (error) {
        data.error = String(error?.message || error);
      } finally {
        data.refreshing = false;
        data.notify();
      }
    },
    async loadTagNames() {
      if (data.tagNames.size) return;
      try {
        const response = await fetchExternal(STEAM_TAGS_URL);
        const payload = await response.json();
        const names = {};
        for (const tag of payload?.response?.tags || []) {
          if (tag?.tagid && tag?.name) {
            names[tag.tagid] = tag.name;
            data.tagNames.set(Number(tag.tagid), String(tag.name));
          }
        }
        writeCache(TAGS_CACHE_KEY, { names });
        data.notify();
      } catch {
        // captions fall back to library and wishlist reasons
      }
    },
    async loadItems(appIds) {
      const wanted = [...new Set((appIds || []).map(Number))]
        .filter((appId) => Number.isSafeInteger(appId) && appId > 0 && !data.items.has(appId) && !data.pendingItems.has(appId));
      if (!wanted.length) return;
      wanted.forEach((appId) => data.pendingItems.add(appId));
      const country = data.snapshot?.storeRegionCode || "US";
      let changed = false;
      try {
        for (let index = 0; index < wanted.length; index += ITEMS_BATCH) {
          const batch = wanted.slice(index, index + ITEMS_BATCH);
          try {
            const response = await fetchExternal(buildItemsUrl(batch, country));
            const payload = await response.json();
            for (const raw of payload?.response?.store_items || []) {
              const item = normalizeSteamItem(raw);
              if (item) {
                data.items.set(item.appId, item);
                changed = true;
              }
            }
          } catch {
            // offline: pages work without tags and trailers
          }
        }
      } finally {
        wanted.forEach((appId) => data.pendingItems.delete(appId));
      }
      if (changed) {
        // Keep the newest few hundred entries for the next start.
        writeCache(ITEMS_CACHE_KEY, { items: [...data.items.values()].slice(-400) });
        data.notify();
      }
    },
    async runSearch(query) {
      const trimmed = String(query || "").trim();
      if (trimmed.length < 2) {
        data.search = { query: "", results: [], loading: false, error: "" };
        data.notify();
        return;
      }
      data.search = { ...data.search, query: trimmed, loading: true, error: "" };
      data.notify();
      try {
        const results = await fetchJson(`api/store/search?q=${encodeURIComponent(trimmed)}`);
        if (data.search.query !== trimmed) return;
        const list = Array.isArray(results) ? results : Array.isArray(results?.results) ? results.results : [];
        data.remember(list);
        data.search = { query: trimmed, results: list, loading: false, error: "" };
        void data.loadItems(list.map((game) => game.steamAppId));
      } catch (error) {
        if (data.search.query !== trimmed) return;
        data.search = { query: trimmed, results: [], loading: false, error: String(error?.message || error) };
      }
      data.notify();
    },
    clearSearch() {
      data.search = { query: "", results: [], loading: false, error: "" };
      data.notify();
    },
    async toggleWishlist(game) {
      const id = String(game?.id || "");
      if (!id || data.pending.has(id)) return;
      data.pending.add(id);
      data.notify();
      try {
        const { item: _item, tagIds: _tagIds, ...plain } = game;
        data.applySnapshot(await postJson("api/store/wishlist", { game: plain, enabled: !game.isLocallyWishlisted }));
      } catch (error) {
        data.error = String(error?.message || error);
      } finally {
        data.pending.delete(id);
        data.notify();
      }
    },
    async saveAlert(game, targetPrice, enabled = true) {
      const key = `alert:${alertIdentity(game)}`;
      if (data.pending.has(key)) return;
      data.pending.add(key);
      data.notify();
      try {
        const appId = Number(game?.steamAppId) > 0 ? Number(game.steamAppId) : null;
        data.applySnapshot(await postJson("api/store/alerts", {
          steamAppId: appId,
          gameId: String(game?.gameId ?? game?.id ?? ""),
          title: game?.title || "",
          targetPrice: Math.max(0.01, Number(targetPrice) || 0.01),
          currencyCode: game?.targetCurrencyCode || alertCurrency(data.snapshot),
          enabled,
          mode: "price",
          targetDiscountPercent: 0,
          snoozedUntilUtc: null,
        }));
      } catch (error) {
        data.error = String(error?.message || error);
      } finally {
        data.pending.delete(key);
        data.notify();
      }
    },
    async setRegion(code) {
      if (data.refreshing) return;
      data.refreshing = true;
      data.error = "";
      data.notify();
      try {
        // Region prices in the region's own currency, like ORBIT's region switch.
        data.applySnapshot(await postJson("api/store/settings/region", { value: code }));
        data.applySnapshot(await postJson("api/store/settings/currency", { value: "REGION" }));
        // Tags and trailers do not depend on the region; prices do.
      } catch (error) {
        data.error = String(error?.message || error);
      } finally {
        data.refreshing = false;
        data.notify();
      }
    },
    async offers(game) {
      const id = String(game?.priceProviderGameId || game?.id || "");
      if (!id) return [];
      const result = await fetchJson(`api/store/offers?gameId=${encodeURIComponent(id)}`);
      return Array.isArray(result) ? result : Array.isArray(result?.offers) ? result.offers : [];
    },
    deinit() {
      clearTimeout(pollTimer);
      listeners.clear();
    },
  };
  return data;
}

// ---------------------------------------------------------------------------
// Opening links inside Big Picture
// ---------------------------------------------------------------------------

export function steamUrlFor(url, game) {
  const value = String(url || "").trim();
  const appId = Number(game?.steamAppId);
  if (/^https:\/\/store\.steampowered\.com\/app\/\d+/i.test(value) && Number.isSafeInteger(appId) && appId > 0) {
    return `steam://store/${appId}`;
  }
  return /^https:\/\//i.test(value) ? `steam://openurl/${value}` : "";
}

/** Opens a store page in Steam's own browser, which works with the controller. */
function openInSteam(url, game, data) {
  const steamUrl = steamUrlFor(url, game);
  try {
    if (steamUrl && typeof globalThis.SteamClient?.URL?.ExecuteSteamURL === "function") {
      globalThis.SteamClient.URL.ExecuteSteamURL(steamUrl);
      return true;
    }
  } catch {
    // fall back to the desktop browser
  }
  void data.openDealInBrowser?.(url);
  return false;
}

// ---------------------------------------------------------------------------
// Styles: ORBIT's Store (midnight theme tokens) on Steam's Gamepad UI
// ---------------------------------------------------------------------------

export const STORE_CSS = `
.tfs-store, .tfs-store-details { --acc: 63 208 255; --acc2: 139 92 246; --base: 5 7 12; --surf: 15 18 28; --surf2: 22 26 39; --txt: 244 246 251;
  --mut: 138 146 166; --radius-card: 1.25rem; --ring: 1.5px; --focus-shadow: 0 8px 30px -6px rgb(var(--acc) / .5);
  --shadow-card: 0 6px 24px -6px rgb(0 0 0 / .55); --gap: clamp(.9rem, 1.8vw, 1.5rem); --cols: 6; --ease: cubic-bezier(.22, 1, .36, 1);
  color: rgb(var(--txt)); font-family: "Motiva Sans", Arial, sans-serif; -webkit-font-smoothing: antialiased; }
.tfs-store { position: absolute; inset: 0; display: flex; flex-direction: column; overflow: hidden; box-sizing: border-box;
  padding: calc(40px + clamp(.65rem, 1.8vh, 1.15rem)) clamp(1.25rem, 3vw, 3.5rem) 42px;
  background: radial-gradient(circle at 18% 8%, rgb(var(--acc) / .08), transparent 34%), radial-gradient(circle at 82% 92%, rgb(var(--acc2) / .08), transparent 38%), rgb(var(--base)); }
:is(.tfs-store, .tfs-store-details) *, :is(.tfs-store, .tfs-store-details) *::before, :is(.tfs-store, .tfs-store-details) *::after { box-sizing: border-box; }
.tfs-store-icon { width: 1rem; height: 1rem; flex: none; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.tfs-store-icon.is-filled { fill: currentColor; }
.tfs-store-spin { animation: tfs-store-spin .9s linear infinite; }
@keyframes tfs-store-spin { to { transform: rotate(1turn); } }
.tfs-store-key { display: inline-flex; align-items: center; justify-content: center; min-width: 1.35rem; height: 1.35rem; padding: 0 .3rem; border-radius: .4rem;
  border: 1px solid rgb(255 255 255 / .15); background: rgb(0 0 0 / .25); color: rgb(255 255 255 / .7); font-size: 10px; font-weight: 900; line-height: 1; letter-spacing: 0; }

/* Toolbar */
.tfs-store-toolbar { flex: none; display: grid; grid-template-columns: minmax(0, 1fr) max-content minmax(0, 1fr); align-items: center; gap: .75rem;
  margin-bottom: clamp(.75rem, 1.8vh, 1.15rem); }
.tfs-store-toolbar > :nth-child(1) { grid-column: 2; grid-row: 1; justify-self: center; min-width: 0; }
.tfs-store-toolbar > :nth-child(2) { grid-column: 1; grid-row: 1; justify-self: start; min-width: 0; max-width: 100%; }
.tfs-store-toolbar > :nth-child(3) { grid-column: 3; grid-row: 1; justify-self: end; }
@media (max-width: 1100px) {
  .tfs-store-toolbar { row-gap: .5rem; }
  .tfs-store-toolbar > :nth-child(1) { grid-column: 1 / -1; }
  .tfs-store-toolbar > :nth-child(2) { grid-row: 2; }
  .tfs-store-toolbar > :nth-child(3) { grid-row: 2; }
}
.tfs-store-rail { display: flex; align-items: center; gap: .25rem; height: 2.625rem; padding: 0 .5rem; border-radius: 999px;
  border: 1px solid rgb(255 255 255 / .1); background: rgb(0 0 0 / .25); }
.tfs-store-rail .tfs-store-key { margin: 0 .25rem; height: 2rem; min-width: 2rem; border-radius: .45rem; color: rgb(var(--mut)); }
.tfs-store-tab { position: relative; display: flex; align-items: center; gap: .5rem; height: 2rem; padding: 0 1rem; border-radius: 999px;
  color: rgb(var(--mut)); font-size: 12px; font-weight: 600; white-space: nowrap; transition: background-color .16s, color .16s, box-shadow .16s; }
.tfs-store-tab.is-active { color: #000; background: rgb(var(--acc)); }
.tfs-store-tab.gpfocus { color: #fff; background: rgb(255 255 255 / .1); box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9), var(--focus-shadow); }
.tfs-store-tab.is-active.gpfocus { color: #000; background: rgb(var(--acc)); box-shadow: 0 0 0 2px rgb(var(--base)), 0 0 0 calc(2px + var(--ring)) rgb(var(--acc)), var(--focus-shadow); }
.tfs-store-pill, :is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-pill { display: inline-flex; align-items: center; justify-content: center; gap: .5rem; width: auto; min-width: 0;
  height: 2.625rem; min-height: 2.625rem; margin: 0; padding: 0 1rem; border-radius: 999px; border: 1px solid rgb(255 255 255 / .1); background: rgb(255 255 255 / .05);
  color: rgb(var(--txt)); font-size: 12px; font-weight: 600; line-height: 1; white-space: nowrap; text-transform: none; letter-spacing: 0;
  transition: background-color .14s, border-color .14s, box-shadow .14s, color .14s; }
.tfs-store-pill .tfs-store-icon { color: rgb(var(--acc)); }
.tfs-store-pill.is-icon { width: 2.625rem; padding: 0; }
.tfs-store-pill.is-icon .tfs-store-icon { color: rgb(var(--txt)); }
.tfs-store-pill.gpfocus, :is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-pill.gpfocus { border-color: rgb(var(--acc) / .72); background: rgb(255 255 255 / .1); color: #fff;
  box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9), var(--focus-shadow); }
.tfs-store-actions { display: flex; align-items: center; gap: .5rem; }
.tfs-store-search-pill { color: rgb(var(--mut)); padding: 0 .55rem 0 .85rem; }
.tfs-store-search-pill .tfs-store-icon { color: rgb(var(--mut)); }
.tfs-store-search { display: flex; align-items: center; gap: .5rem; width: min(30rem, 100%); height: 2.625rem; padding: 0 .4rem 0 1rem; border-radius: 999px;
  border: 1px solid rgb(var(--acc) / .72); background: rgb(255 255 255 / .075); box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .72), var(--focus-shadow); }
.tfs-store-search > .tfs-store-icon { color: rgb(var(--acc)); }
.tfs-store-search-field { flex: 1; min-width: 0; }
.tfs-store-search-field > div, .tfs-store-search-field .DialogInputLabelGroup { margin: 0; padding: 0; background: transparent; }
.tfs-store-search-field .DialogLabel, .tfs-store-search-field label { display: none; }
.tfs-store-search-field input { width: 100%; height: 2.1rem; margin: 0; padding: 0; border: 0; outline: 0; background: transparent !important; box-shadow: none !important;
  color: rgb(var(--txt)); font: 600 14px "Motiva Sans", Arial, sans-serif; }
.tfs-store-search-field input::placeholder { color: rgb(var(--mut)); }
.tfs-store-search-close { width: 1.85rem; height: 1.85rem; display: flex; align-items: center; justify-content: center; border-radius: 999px; color: rgb(var(--mut)); }
.tfs-store-search-close.gpfocus { background: rgb(255 255 255 / .12); color: #fff; box-shadow: 0 0 0 var(--ring) rgb(var(--acc)); }

/* Content */
.tfs-store-stage { position: relative; flex: 1; min-height: 0; overflow: hidden; }
.tfs-store-scroll { position: absolute; inset: 0; overflow-x: hidden; overflow-y: auto; padding: .25rem clamp(.35rem, .8vw, .75rem) clamp(4rem, 10vh, 6rem);
  scrollbar-width: none; animation: tfs-store-page-in .18s var(--ease) both; }
.tfs-store-scroll::-webkit-scrollbar { display: none; }
.tfs-store-scroll[data-direction="-1"] { animation-name: tfs-store-page-in-left; }
@keyframes tfs-store-page-in { from { opacity: 0; transform: translateX(42px); } to { opacity: 1; transform: none; } }
@keyframes tfs-store-page-in-left { from { opacity: 0; transform: translateX(-42px); } to { opacity: 1; transform: none; } }
.tfs-store-eyebrow { margin: 0; overflow: hidden; color: rgb(var(--acc)); font-size: 10px; font-weight: 700; letter-spacing: .2em; text-transform: uppercase; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-heading { display: flex; align-items: flex-end; justify-content: space-between; gap: 1rem; min-height: 3rem; margin-bottom: clamp(.75rem, 1.6vh, 1rem); padding: 0 .25rem; }
.tfs-store-heading h1 { margin: .125rem 0 0; overflow: hidden; font-size: clamp(1.25rem, 2.2vw, 1.75rem); font-weight: 900; letter-spacing: -.025em; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-count { flex: none; padding-bottom: .125rem; color: rgb(var(--mut)); font-size: 12px; font-weight: 500; }
.tfs-store-shelf { margin-top: clamp(1rem, 2.6vh, 1.75rem); }
.tfs-store-shelf:first-child { margin-top: 0; }
.tfs-store-shelf-head { min-width: 0; padding: 0 .25rem; }
.tfs-store-shelf-head h2 { margin: .125rem 0 0; overflow: hidden; font-size: clamp(1.05rem, 1.7vw, 1.4rem); font-weight: 900; letter-spacing: -.025em; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-grid { position: relative; display: grid; grid-template-columns: repeat(var(--cols), minmax(0, 1fr)); gap: var(--gap); margin: 0 -.5rem; padding: .5rem .5rem 2rem; }
.tfs-store-grid.is-shelf { padding-bottom: .75rem; }

/* Cards */
.tfs-store-card { position: relative; isolation: isolate; aspect-ratio: 2 / 3; width: 100%; overflow: hidden; border-radius: var(--radius-card);
  background: rgb(var(--surf2)); box-shadow: var(--shadow-card); transition: transform .115s var(--ease), box-shadow .115s var(--ease); }
.tfs-store-card.gpfocus { z-index: 2; transform: translateY(-1px) scale(1.025); box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9), var(--focus-shadow); }
.tfs-store-art { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
.tfs-store-art-fallback { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding: 1rem; text-align: center;
  color: rgb(255 255 255 / .72); font-size: 14px; font-weight: 800; background: linear-gradient(160deg, rgb(var(--surf2)), rgb(var(--surf))); }
.tfs-store-edge { --w: 2.5px; position: absolute; z-index: 40; inset: 0; overflow: hidden; border-radius: inherit; padding: var(--w); container-type: size; pointer-events: none;
  opacity: 0; background: rgb(var(--acc) / .2); transition: opacity .18s ease;
  -webkit-mask: linear-gradient(#fff 0 0) content-box, linear-gradient(#fff 0 0); -webkit-mask-composite: xor; mask-composite: exclude; }
.tfs-store-edge::before { content: ""; position: absolute; top: calc(50% - 71cqmax); left: calc(50% - 71cqmax); width: 142cqmax; height: 142cqmax;
  background: conic-gradient(transparent 0deg 210deg, rgb(var(--acc) / .14) 262deg, rgb(var(--acc) / .6) 322deg, rgb(var(--acc) / .96) 348deg, rgb(255 255 255 / .95) 357deg, transparent 360deg); }
.gpfocus > .tfs-store-edge { opacity: 1; }
.gpfocus > .tfs-store-edge::before { animation: tfs-store-edge-spin 4.2s steps(252, end) infinite; }
@keyframes tfs-store-edge-spin { to { transform: rotate(1turn); } }
.tfs-store-badges { position: absolute; z-index: 30; top: .5rem; left: .5rem; right: .5rem; display: flex; align-items: flex-start; justify-content: space-between; gap: .5rem; }
.tfs-store-badges > div { display: flex; align-items: center; gap: .25rem; }
.tfs-store-discount { padding: .25rem .5rem; border-radius: .5rem; background: #34d399; color: #000; font-size: 9px; font-weight: 900; box-shadow: var(--shadow-card); }
.tfs-store-match { padding: .25rem .5rem; border-radius: .5rem; border: 1px solid rgb(255 255 255 / .1); background: rgb(0 0 0 / .7); color: rgb(255 255 255 / .9);
  font-size: 8px; font-weight: 700; white-space: nowrap; }
.tfs-store-heart { display: flex; align-items: center; gap: .25rem; height: 1.75rem; padding: 0 .375rem; border-radius: .5rem; border: 1px solid rgb(255 255 255 / .1);
  background: rgb(0 0 0 / .7); color: rgb(255 255 255 / .7); box-shadow: var(--shadow-card); opacity: 0; transition: opacity .14s; }
.tfs-store-heart .tfs-store-key { min-width: 1rem; height: 1rem; padding: 0 .2rem; font-size: 8px; border-radius: .3rem; }
.tfs-store-heart .tfs-store-icon { width: .8rem; height: .8rem; }
.tfs-store-heart.is-on { color: rgb(var(--acc)); opacity: 1; }
.tfs-store-heart.is-pending .tfs-store-icon { animation: tfs-store-pulse 1s ease-in-out infinite; }
@keyframes tfs-store-pulse { 50% { opacity: .35; } }
.gpfocus .tfs-store-heart { opacity: 1; }
.tfs-store-shade { position: absolute; z-index: 20; inset: 0; pointer-events: none; opacity: 0; transition: opacity .14s;
  background: linear-gradient(0deg, rgb(0 0 0 / .95), rgb(0 0 0 / .15) 55%, transparent); }
.tfs-store-caption { position: absolute; z-index: 20; inset: 0; display: flex; flex-direction: column; justify-content: flex-end; padding: .75rem .75rem 3rem;
  pointer-events: none; opacity: 0; transition: opacity .14s; }
.gpfocus > .tfs-store-shade, .gpfocus > .tfs-store-caption { opacity: 1; }
.tfs-store-caption b { display: -webkit-box; overflow: hidden; color: #fff; font-size: 14px; font-weight: 600; line-height: 1.25; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
.tfs-store-caption span { margin-top: .25rem; overflow: hidden; color: rgb(var(--mut)); font-size: 12px; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-price { position: absolute; z-index: 30; right: .5rem; bottom: .5rem; max-width: calc(100% - 1rem); overflow: hidden; padding: .375rem .625rem; border-radius: .5rem;
  border: 1px solid rgb(255 255 255 / .1); background: rgb(0 0 0 / .75); color: #fff; font-size: 10px; font-weight: 900; white-space: nowrap; text-overflow: ellipsis; box-shadow: var(--shadow-card); }

/* Trailer preview: the dwelled tile widens into a muted 16:9 clip */
.tfs-store-preview { position: absolute; z-index: 50; overflow: hidden; border-radius: var(--radius-card); background: #000; pointer-events: none; opacity: 0;
  transition: clip-path .3s var(--ease), opacity .14s ease; }
.tfs-store-preview.is-open { opacity: 1; }
.tfs-store-preview video, .tfs-store-preview img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
.tfs-store-preview::after { content: ""; position: absolute; inset: 0; border-radius: inherit; box-shadow: inset 0 0 0 var(--ring) rgb(var(--acc) / .9); }
.tfs-store-preview-shade { position: absolute; inset: 0; background: linear-gradient(0deg, rgb(0 0 0 / .9), rgb(0 0 0 / .1) 45%, transparent); }
.tfs-store-preview-top { position: absolute; top: .75rem; left: .75rem; right: .75rem; display: flex; justify-content: space-between; gap: .5rem; }
.tfs-store-preview-top .tfs-store-heart { opacity: 1; }
.tfs-store-preview-bottom { position: absolute; left: 0; right: 0; bottom: 0; display: flex; align-items: flex-end; justify-content: space-between; gap: .75rem; padding: .75rem; }
.tfs-store-preview-bottom b { display: block; overflow: hidden; color: #fff; font-size: 14px; font-weight: 600; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-preview-bottom span { display: block; margin-top: .125rem; overflow: hidden; color: rgb(255 255 255 / .65); font-size: 12px; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-preview-bottom .tfs-store-price { position: static; flex: none; max-width: 45%; }

/* Spotlight */
.tfs-store-spotlight { position: relative; display: block; width: 100%; height: clamp(12rem, 34vh, 19rem); overflow: hidden; border-radius: calc(var(--radius-card) + .35rem);
  border: 1px solid rgb(255 255 255 / .1); background: rgb(var(--surf2)); box-shadow: var(--shadow-card); transition: box-shadow .14s; }
.tfs-store-spotlight.gpfocus { box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9), var(--focus-shadow); }
.tfs-store-spotlight video { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: 0; transition: opacity .3s; }
.tfs-store-spotlight video.is-playing { opacity: 1; }
.tfs-store-spotlight-shade { position: absolute; inset: 0; pointer-events: none;
  background: linear-gradient(90deg, rgb(0 0 0 / .9), rgb(0 0 0 / .45) 50%, transparent), linear-gradient(0deg, rgb(0 0 0 / .7), transparent 50%); }
.tfs-store-spotlight-body { position: relative; z-index: 10; display: flex; flex-direction: column; justify-content: flex-end; height: 100%; max-width: min(42rem, 70%);
  padding: clamp(1.1rem, 2.6vw, 2.25rem); }
.tfs-store-spotlight-body .tfs-store-eyebrow { display: flex; align-items: center; gap: .5rem; font-weight: 900; }
.tfs-store-spotlight-body .tfs-store-eyebrow .tfs-store-icon { width: .8rem; height: .8rem; }
.tfs-store-spotlight-body h1 { display: -webkit-box; margin: .5rem 0 0; padding-bottom: .08em; overflow: hidden; color: #fff; font-size: clamp(1.6rem, 3.4vw, 3.25rem); font-weight: 900; line-height: 1.02;
  letter-spacing: -.035em; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
.tfs-store-spotlight-body p { margin: .5rem 0 0; overflow: hidden; color: rgb(255 255 255 / .75); font-size: 14px; font-weight: 600; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-chips { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; margin-top: 1rem; }
.tfs-store-chips .tfs-store-discount { padding: .375rem .75rem; border-radius: 999px; font-size: 12px; }
.tfs-store-price-pill { padding: .375rem 1rem; border-radius: 999px; border: 1px solid rgb(255 255 255 / .15); background: rgb(0 0 0 / .55); color: #fff; font-size: 14px; font-weight: 900; }
.tfs-store-more-link { display: flex; align-items: center; gap: .375rem; color: rgb(255 255 255 / .8); font-size: 14px; font-weight: 700; }
.tfs-store-more-link .tfs-store-icon { transition: transform .14s; }
.gpfocus .tfs-store-more-link .tfs-store-icon { transform: translateX(.25rem); }
.tfs-store-spotlight > .tfs-store-heart { position: absolute; z-index: 10; top: 1rem; right: 1rem; height: 2rem; padding: 0 .5rem; }

/* Categories and redeem banner */
.tfs-store-categories { display: grid; grid-template-columns: repeat(var(--category-cols, 4), minmax(0, 1fr)); gap: clamp(.75rem, 1.4vw, 1.15rem); margin: 0 -.5rem; padding: .5rem .5rem .75rem; }
.tfs-store-category { position: relative; isolation: isolate; aspect-ratio: 16 / 7; overflow: hidden; border-radius: var(--radius-card); background: rgb(var(--surf2));
  box-shadow: var(--shadow-card); transition: transform .115s var(--ease), box-shadow .115s var(--ease); }
.tfs-store-category.gpfocus { z-index: 2; transform: translateY(-1px) scale(1.025); box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9), var(--focus-shadow); }
.tfs-store-category-shade { position: absolute; inset: 0; background: linear-gradient(0deg, rgb(0 0 0 / .9), rgb(0 0 0 / .45) 55%, rgb(0 0 0 / .1)); }
.tfs-store-category-label { position: absolute; left: 0; right: 0; bottom: 0; display: flex; align-items: flex-end; justify-content: space-between; gap: .5rem; padding: .75rem; }
.tfs-store-category-label b { overflow: hidden; color: #fff; font-size: 16px; font-weight: 900; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-category-label span { flex: none; padding: .25rem .5rem; border-radius: .5rem; border: 1px solid rgb(255 255 255 / .1); background: rgb(0 0 0 / .7);
  color: rgb(255 255 255 / .8); font-size: 10px; font-weight: 700; }
.tfs-store-banner { position: relative; display: flex; align-items: center; gap: 1rem; width: 100%; margin-top: .5rem; overflow: hidden; padding: clamp(1rem, 2vw, 1.5rem);
  border-radius: var(--radius-card); border: 1px solid rgb(255 255 255 / .1); box-shadow: var(--shadow-card);
  background: linear-gradient(90deg, rgb(var(--acc) / .15), rgb(255 255 255 / .04) 50%, transparent); transition: border-color .14s, box-shadow .14s; }
.tfs-store-banner.gpfocus { border-color: rgb(var(--acc) / .6); box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9), var(--focus-shadow); }
.tfs-store-banner-icon { display: flex; align-items: center; justify-content: center; flex: none; width: 3rem; height: 3rem; border-radius: 1rem; background: rgb(var(--acc) / .15); color: rgb(var(--acc)); }
.tfs-store-banner-icon .tfs-store-icon { width: 1.4rem; height: 1.4rem; }
.tfs-store-banner-text { flex: 1; min-width: 0; }
.tfs-store-banner-text b { display: block; font-size: 16px; font-weight: 900; }
.tfs-store-banner-text span { display: block; margin-top: .125rem; color: rgb(var(--mut)); font-size: 14px; }
.tfs-store-banner > .tfs-store-icon { color: rgb(var(--mut)); width: 1.25rem; height: 1.25rem; transition: transform .14s; }
.tfs-store-banner.gpfocus > .tfs-store-icon { transform: translateX(.25rem); }
.tfs-store-updating { margin: .5rem .25rem 0; color: rgb(var(--mut)); font-size: 12px; font-weight: 500; }

/* Empty, loading and error states */
.tfs-store-empty { display: flex; min-height: 18rem; flex-direction: column; align-items: center; justify-content: center; gap: .75rem; text-align: center; }
.tfs-store-empty > .tfs-store-icon { width: 2rem; height: 2rem; color: rgb(var(--acc)); }
.tfs-store-empty b { font-size: 18px; font-weight: 700; }
.tfs-store-empty p { max-width: 28rem; margin: 0; color: rgb(var(--mut)); font-size: 14px; line-height: 1.5; }
.tfs-store-error { margin: 0 .25rem .75rem; padding: .75rem 1rem; border-radius: 1rem; border: 1px solid rgb(248 113 113 / .25); background: rgb(248 113 113 / .1); color: #fecaca; font-size: 13px; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-cta, .tfs-store-cta { display: inline-flex; align-items: center; gap: .5rem; width: auto; min-width: 0; height: auto; min-height: 0; margin: .25rem 0 0;
  padding: .7rem 1.25rem; border: 0; border-radius: 999px; background: rgb(var(--acc)); color: #000; font-size: 14px; font-weight: 900; text-transform: none; letter-spacing: 0; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-cta.gpfocus { background: rgb(var(--acc)); color: #000; box-shadow: 0 0 0 2px rgb(var(--base)), 0 0 0 calc(2px + var(--ring)) #fff, var(--focus-shadow); }

/* Price alerts page */
.tfs-store-alerts { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(22rem, 100%), 1fr)); gap: clamp(.75rem, 1.25vw, 1.15rem); padding: 0 .25rem 2rem; }
.tfs-store-alert { display: flex; min-height: 13rem; flex-direction: column; padding: 1.25rem; border-radius: var(--radius-card); border: 1px solid rgb(255 255 255 / .08);
  background: rgb(var(--surf) / .7); box-shadow: var(--shadow-card); }
.tfs-store-alert h2 { margin: .25rem 0 0; overflow: hidden; font-size: 18px; font-weight: 700; white-space: nowrap; text-overflow: ellipsis; }
.tfs-store-alert-start { margin: .25rem 0 0; color: rgb(var(--mut)); font-size: 12px; }
.tfs-store-alert-values { display: grid; grid-template-columns: 1fr 1fr; gap: .75rem; margin: 1.25rem 0; padding: .75rem; border-radius: 1rem; background: rgb(0 0 0 / .2); }
.tfs-store-alert-values small { display: block; color: rgb(var(--mut)); font-size: 10px; letter-spacing: .05em; text-transform: uppercase; }
.tfs-store-alert-values b { display: block; margin-top: .25rem; font-size: 18px; font-weight: 900; }
.tfs-store-alert-values b.is-accent { color: rgb(var(--acc)); }
.tfs-store-alert-foot { display: flex; align-items: center; justify-content: space-between; gap: .75rem; margin-top: auto; }
.tfs-store-reached { padding: .5rem .75rem; border-radius: 999px; background: rgb(52 211 153 / .15); color: #6ee7b7; font-size: 12px; font-weight: 700; }
.tfs-store-muted { overflow: hidden; color: rgb(var(--mut)); font-size: 12px; white-space: nowrap; text-overflow: ellipsis; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-danger, .tfs-store-danger { display: inline-flex; align-items: center; justify-content: center; width: 2.75rem; min-width: 2.75rem; height: 2.75rem;
  min-height: 0; margin: 0; padding: 0; border: 0; border-radius: 999px; background: rgb(239 68 68 / .15); color: #fca5a5; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-danger.gpfocus { background: rgb(239 68 68 / .28); color: #fff; box-shadow: 0 0 0 var(--ring) #fca5a5; }

/* Modal dialogs (redeem, region) */
.tfs-store-modal { position: absolute; z-index: 100; inset: 0; display: flex; align-items: center; justify-content: center; padding: 4vh; background: rgb(0 0 0 / .8);
  animation: tfs-store-fade .16s ease both; }
@keyframes tfs-store-fade { from { opacity: 0; } }
.tfs-store-dialog { width: min(34rem, 100%); max-height: 100%; overflow-y: auto; padding: 1.5rem; border-radius: 1.5rem; border: 1px solid rgb(255 255 255 / .1);
  background: linear-gradient(165deg, rgb(var(--surf2) / .96), rgb(var(--surf) / .98)); box-shadow: 0 32px 120px rgb(0 0 0 / .65);
  animation: tfs-store-rise .22s var(--ease) both; scrollbar-width: none; }
@keyframes tfs-store-rise { from { opacity: 0; transform: translateY(24px) scale(.97); } }
.tfs-store-dialog h2 { margin: .25rem 0 0; font-size: 22px; font-weight: 900; letter-spacing: -.02em; }
.tfs-store-dialog > p { margin: .5rem 0 1rem; color: rgb(var(--mut)); font-size: 14px; line-height: 1.5; }
.tfs-store-option { display: flex; align-items: center; gap: .9rem; margin-top: .5rem; padding: .9rem 1rem; border-radius: 1rem; border: 1px solid rgb(255 255 255 / .08);
  background: rgb(255 255 255 / .04); transition: background-color .14s, border-color .14s, box-shadow .14s; }
.tfs-store-option.gpfocus { border-color: rgb(var(--acc) / .6); background: rgb(var(--acc) / .1); box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9); }
.tfs-store-option.is-active { border-color: rgb(var(--acc) / .35); }
.tfs-store-option > .tfs-store-icon { width: 1.2rem; height: 1.2rem; color: rgb(var(--acc)); }
.tfs-store-option div { flex: 1; min-width: 0; }
.tfs-store-option b { display: block; font-size: 15px; font-weight: 800; }
.tfs-store-option span { display: block; margin-top: .125rem; color: rgb(var(--mut)); font-size: 13px; }
.tfs-store-dialog-status { margin: 1rem 0 0; color: rgb(var(--acc)); font-size: 13px; font-weight: 700; }
.tfs-store-dialog-foot { display: flex; justify-content: flex-end; margin-top: 1.25rem; }
.tfs-store-regions { display: grid; grid-template-columns: 1fr 1fr; gap: 0 .5rem; }

/* Details */
.tfs-store-details { position: absolute; inset: 0; overflow-x: hidden; overflow-y: auto; scrollbar-width: none; color: rgb(var(--txt)); background: rgb(var(--base));
  font-family: "Motiva Sans", Arial, sans-serif; }
.tfs-store-details::-webkit-scrollbar { display: none; }
.tfs-store-details-hero { position: relative; height: clamp(15rem, 44vh, 22rem); overflow: hidden; }
.tfs-store-details-hero .tfs-store-art { object-position: center 30%; }
.tfs-store-details-hero video { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; opacity: 0; transition: opacity .5s; }
.tfs-store-details-hero video.is-playing { opacity: 1; }
.tfs-store-details-hero::after { content: ""; position: absolute; inset: 0; background: linear-gradient(0deg, rgb(var(--base)), rgb(0 0 0 / .35) 55%, rgb(0 0 0 / .2)); }
.tfs-store-details-title { position: absolute; z-index: 2; left: clamp(1.25rem, 3vw, 3.5rem); right: clamp(1.25rem, 3vw, 3.5rem); bottom: 1.5rem; }
.tfs-store-details-title h1 { margin: 0; font-size: clamp(1.9rem, 3.4vw, 2.6rem); font-weight: 900; letter-spacing: -.03em; line-height: 1.02; }
.tfs-store-details-meta { display: flex; flex-wrap: wrap; gap: .5rem; margin-top: .65rem; }
.tfs-store-details-meta span { padding: .3rem .65rem; border-radius: 999px; border: 1px solid rgb(255 255 255 / .1); background: rgb(0 0 0 / .45); color: rgb(255 255 255 / .82);
  font-size: 12px; font-weight: 600; }
.tfs-store-details-body { display: flex; flex-direction: column; gap: 1rem; padding: .5rem clamp(1.25rem, 3vw, 3.5rem) 5rem; }
.tfs-store-best { display: flex; align-items: center; justify-content: space-between; gap: 1.25rem; padding: 1.25rem; border-radius: 1.5rem;
  border: 1px solid rgb(var(--acc) / .3); background: rgb(var(--acc) / .1); }
.tfs-store-best small { display: block; color: rgb(var(--acc)); font-size: 12px; font-weight: 700; letter-spacing: .16em; text-transform: uppercase; }
.tfs-store-best b { display: block; margin-top: .25rem; font-size: 30px; font-weight: 900; }
.tfs-store-best .tfs-store-discount { display: inline-block; margin-left: .6rem; padding: .3rem .6rem; font-size: 12px; vertical-align: middle; }
.tfs-store-buttons { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: .75rem; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-button, :is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-button { display: inline-flex; align-items: center; justify-content: center; gap: .5rem; width: auto;
  min-width: 0; height: auto; min-height: 0; margin: 0; padding: .75rem 1.25rem; border-radius: 999px; border: 1px solid rgb(255 255 255 / .1); background: rgb(255 255 255 / .05);
  color: rgb(var(--txt)); font-size: 14px; font-weight: 600; line-height: 1; text-transform: none; letter-spacing: 0; white-space: nowrap; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-button.is-primary { padding: .75rem 1.5rem; border: 0; background: rgb(var(--acc)); color: #000; font-weight: 900; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-button.gpfocus { border-color: rgb(var(--acc) / .72); background: rgb(255 255 255 / .1); color: #fff;
  box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9), var(--focus-shadow); }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-button.is-primary.gpfocus { background: rgb(var(--acc)); color: #000; box-shadow: 0 0 0 2px rgb(var(--base)), 0 0 0 calc(2px + var(--ring)) #fff, var(--focus-shadow); }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-button:disabled { opacity: .6; }
.tfs-store-details .tfs-store-key { margin-right: .1rem; }
.tfs-store-summary { max-width: 64rem; margin: 0; color: rgb(255 255 255 / .65); font-size: 14px; line-height: 1.6; }
.tfs-store-panel { padding: 1.25rem; border-radius: 1.5rem; border: 1px solid rgb(255 255 255 / .07); background: rgb(255 255 255 / .035); }
.tfs-store-alert-row { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1rem; }
.tfs-store-alert-row > div:first-child b { display: flex; align-items: center; gap: .5rem; font-size: 15px; font-weight: 700; }
.tfs-store-alert-row > div:first-child b .tfs-store-icon { color: rgb(var(--acc)); }
.tfs-store-alert-row > div:first-child span { display: block; margin-top: .25rem; color: rgb(var(--mut)); font-size: 12px; }
.tfs-store-stepper { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-round { display: inline-flex; align-items: center; justify-content: center; width: 2.5rem; min-width: 2.5rem; height: 2.5rem; min-height: 0; margin: 0;
  padding: 0; border: 0; border-radius: 999px; background: rgb(255 255 255 / .05); color: rgb(var(--txt)); font-size: 18px; font-weight: 700; }
:is(.tfs-store, .tfs-store-details) .DialogButton.tfs-store-round.gpfocus { background: rgb(255 255 255 / .12); color: #fff; box-shadow: 0 0 0 var(--ring) rgb(var(--acc) / .9); }
.tfs-store-target { min-width: 7rem; text-align: center; font-size: 18px; font-weight: 900; }
.tfs-store-offers { display: flex; flex-direction: column; gap: .5rem; }
.tfs-store-offer { display: flex; align-items: center; gap: 1rem; padding: 1rem; border-radius: 1rem; border: 1px solid rgb(255 255 255 / .07); background: rgb(255 255 255 / .035); }
.tfs-store-offer-info { flex: 1; min-width: 0; }
.tfs-store-offer-info > div { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; }
.tfs-store-offer-info b { font-size: 15px; font-weight: 700; }
.tfs-store-offer-info em { padding: .25rem .5rem; border-radius: .375rem; background: rgb(255 255 255 / .08); color: rgb(var(--mut)); font-size: 9px; font-style: normal; font-weight: 700; text-transform: uppercase; }
.tfs-store-offer-info p { margin: .25rem 0 0; color: rgb(var(--mut)); font-size: 12px; }
.tfs-store-offer .tfs-store-offer-discount { padding: .25rem .5rem; border-radius: .5rem; background: rgb(52 211 153 / .15); color: #6ee7b7; font-size: 12px; font-weight: 700; }
.tfs-store-offer-price { min-width: 7rem; text-align: right; font-size: 20px; font-weight: 900; }
.tfs-store-shots { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: .75rem; }
.tfs-store-shots img { width: 100%; aspect-ratio: 16 / 9; object-fit: cover; border-radius: 1rem; box-shadow: var(--shadow-card); }
.tfs-store-section-title { margin: .5rem 0 .25rem; color: rgb(var(--mut)); font-size: 11px; font-weight: 800; letter-spacing: .18em; text-transform: uppercase; }

/* Full trailer with sound */
.tfs-store-theater { position: absolute; z-index: 200; inset: 0; display: flex; align-items: center; justify-content: center; background: #000; outline: none; }
.tfs-store-theater video { width: 100%; height: 100%; object-fit: contain; }
.tfs-store-theater-bar { position: absolute; left: 0; right: 0; bottom: 0; display: flex; align-items: center; justify-content: space-between; gap: 1rem;
  padding: 1rem clamp(1.25rem, 3vw, 3.5rem) calc(42px + 1rem); background: linear-gradient(0deg, rgb(0 0 0 / .85), transparent); color: #fff; font-size: 14px; font-weight: 700; }
.tfs-store-theater-bar span { display: flex; align-items: center; gap: .5rem; color: rgb(255 255 255 / .75); font-weight: 600; }
.tfs-store-theater-error { position: absolute; color: #fecaca; font-size: 15px; }
@media (prefers-reduced-motion: reduce) {
  .tfs-store-edge { background: rgb(var(--acc) / .85); transition: none; }
  .tfs-store-edge::before { display: none; }
  .tfs-store-scroll, .tfs-store-modal, .tfs-store-dialog { animation: none; }
}
`;

// ---------------------------------------------------------------------------
// Icons (lucide shapes, as in ORBIT)
// ---------------------------------------------------------------------------

const ICONS = {
  sparkles: ["M9.94 15.5A2 2 0 0 0 8.5 14.06l-6.13-1.58a.5.5 0 0 1 0-.96L8.5 9.94A2 2 0 0 0 9.94 8.5l1.58-6.14a.5.5 0 0 1 .96 0L14.06 8.5a2 2 0 0 0 1.44 1.44l6.13 1.58a.5.5 0 0 1 0 .96L15.5 14.06a2 2 0 0 0-1.44 1.44l-1.58 6.13a.5.5 0 0 1-.96 0z", "M20 3v4", "M22 5h-4", "M4 17v2", "M5 18H3"],
  percent: ["M19 5 5 19", "M9 6.5a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0z", "M20 17.5a2.5 2.5 0 1 1-5 0 2.5 2.5 0 0 1 5 0z"],
  heart: ["M19 14c1.49-1.46 3-3.21 3-5.5A5.5 5.5 0 0 0 16.5 3c-1.76 0-3 .5-4.5 2-1.5-1.5-2.74-2-4.5-2A5.5 5.5 0 0 0 2 8.5c0 2.3 1.5 4.05 3 5.5l7 7Z"],
  bell: ["M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9", "M10.3 21a1.94 1.94 0 0 0 3.4 0"],
  search: ["M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0z", "m21 21-4.3-4.3"],
  gift: ["M4 8h16a1 1 0 0 1 1 1v2a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V9a1 1 0 0 1 1-1z", "M12 8v13", "M19 12v7a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-7", "M7.5 8a2.5 2.5 0 0 1 0-5C9 3 11 4.5 12 8c1-3.5 3-5 4.5-5a2.5 2.5 0 0 1 0 5"],
  globe: ["M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0z", "M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20", "M2 12h20"],
  refresh: ["M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8", "M21 3v5h-5", "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16", "M8 16H3v5"],
  loader: ["M21 12a9 9 0 1 1-6.22-8.56"],
  chevron: ["m9 18 6-6-6-6"],
  play: ["M6 3l14 9-14 9z"],
  cart: ["M8 21a1 1 0 1 0 0-2 1 1 0 0 0 0 2z", "M19 21a1 1 0 1 0 0-2 1 1 0 0 0 0 2z", "M2.05 2.05h2l2.66 12.42a2 2 0 0 0 2 1.58h9.78a2 2 0 0 0 1.95-1.57l1.65-7.43H5.12"],
  check: ["M20 6 9 17l-5-5"],
  trash: ["M3 6h18", "M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6", "M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2"],
  x: ["M18 6 6 18", "m6 6 12 12"],
  bag: ["M6 2 3 6v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6l-3-4Z", "M3 6h18", "M16 10a4 4 0 0 1-8 0"],
  wallet: ["M19 7V4a1 1 0 0 0-1-1H5a2 2 0 0 0 0 4h15a1 1 0 0 1 1 1v4h-3a2 2 0 0 0 0 4h3a1 1 0 0 0 1-1v-2a1 1 0 0 0-1-1", "M3 5v14a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-4"],
  key: ["m15.5 7.5 2.3 2.3a1 1 0 0 0 1.4 0l2.1-2.1a1 1 0 0 0 0-1.4L19 4", "m21 2-9.6 9.6", "M10 15.5a5.5 5.5 0 1 1-11 0 5.5 5.5 0 0 1 11 0z"],
};

// ---------------------------------------------------------------------------
// Pages
// ---------------------------------------------------------------------------

// The store route unmounts while a details page is open; this brings the
// player back to the same page, scroll position and card.
const session = { page: "discover", direction: 1, category: null, scrollTop: 0, focusId: "", home: null, homeKey: "" };

function createStorePages(core, data) {
  const React = core.React;
  const h = React.createElement;
  const ui = core.ui;
  const Focusable = ui.Focusable;

  function useData() {
    const [, rerender] = React.useReducer((value) => value + 1, 0);
    React.useEffect(() => data.subscribe(rerender), []);
    return data;
  }

  function Icon({ name, filled = false, spin = false }) {
    const paths = ICONS[name] || [];
    return h(
      "svg",
      { className: `tfs-store-icon${filled ? " is-filled" : ""}${spin ? " tfs-store-spin" : ""}`, viewBox: "0 0 24 24", "aria-hidden": "true" },
      paths.map((d, index) => h("path", { key: index, d })),
    );
  }

  const Key = ({ label }) => h("span", { className: "tfs-store-key" }, label);

  const consume = (event) => {
    try {
      event?.preventDefault?.();
      event?.stopPropagation?.();
    } catch {
      // ignore
    }
    return true;
  };

  function useColumns(ref) {
    const [columns, setColumns] = React.useState(6);
    React.useLayoutEffect(() => {
      const element = ref.current;
      const win = element?.ownerDocument?.defaultView;
      if (!element || !win) return undefined;
      const measure = () => {
        const width = element.clientWidth || win.innerWidth;
        setColumns(Math.max(4, Math.min(8, Math.round(width / 215))));
      };
      measure();
      win.addEventListener("resize", measure);
      return () => win.removeEventListener("resize", measure);
    }, []);
    return columns;
  }

  /** Keeps the focused element, and its whole shelf when it fits, in view. */
  function reveal(element) {
    const scroller = element?.closest?.(".tfs-store-scroll, .tfs-store-details");
    if (!scroller) return;
    if (element.closest("[data-store-top]")) {
      if (scroller.scrollTop > 0) scroller.scrollTo({ top: 0, behavior: "smooth" });
      return;
    }
    const box = scroller.getBoundingClientRect();
    const shelf = element.closest("[data-store-shelf]");
    const pad = Math.min(48, box.height * 0.06);
    const shelfRect = shelf?.getBoundingClientRect();
    const rect = shelfRect && shelfRect.height <= box.height - pad * 2 ? shelfRect : element.getBoundingClientRect();
    let top = scroller.scrollTop;
    if (rect.top < box.top + pad) top += rect.top - box.top - pad;
    else if (rect.bottom > box.bottom - pad) top += rect.bottom - box.bottom + pad;
    if (Math.abs(top - scroller.scrollTop) > 1) scroller.scrollTo({ top: Math.max(0, top), behavior: "smooth" });
  }

  // Steam's own focus scrolling, replaced per element (returning true tells
  // Steam the scroll is handled), so a shelf moves into view as a whole.
  const scrollHandler = (_next, _previous, node) => {
    const element = node?.m_element;
    if (!element) return false;
    reveal(element);
    return true;
  };

  function Artwork({ game, kind, className = "tfs-store-art" }) {
    const sources = React.useMemo(() => artworkSources(game, kind), [game?.id, game?.steamAppId, game?.item, kind]);
    const [index, setIndex] = React.useState(0);
    React.useEffect(() => setIndex(0), [sources.join("|")]);
    if (index >= sources.length) {
      return h("div", { className: "tfs-store-art-fallback" }, game?.title || "");
    }
    return h("img", {
      className,
      src: sources[index],
      alt: "",
      loading: kind === "hero" ? "eager" : "lazy",
      decoding: "async",
      draggable: false,
      onError: () => setIndex((value) => value + 1),
    });
  }

  function openDetails(game) {
    data.remember([game]);
    session.focusId = String(game.id);
    core.routes.navigate(`/tfs/store/game/${encodeURIComponent(game.id)}`);
  }

  function Heart({ game, withKey = true }) {
    const on = isWishlisted(game);
    const pending = data.pending.has(String(game.id));
    return h(
      "span",
      { className: `tfs-store-heart${on ? " is-on" : ""}${pending ? " is-pending" : ""}` },
      withKey ? h(Key, { label: "Y" }) : null,
      h(Icon, { name: "heart", filled: on }),
    );
  }

  function wishlistProps(game) {
    return {
      onOptionsButton: (event) => {
        consume(event);
        void data.toggleWishlist(game);
        return true;
      },
      onOptionsActionDescription: game.isLocallyWishlisted ? "Remove from wishlist" : "Add to wishlist",
    };
  }

  function StoreCard({ game, reason, match, autoFocus, onFocusCard, onBlurCard }) {
    const price = priceLabel(game, data.snapshot);
    return h(
      Focusable,
      {
        className: "tfs-store-card",
        "data-store-product-id": game.id,
        noFocusRing: true,
        autoFocus,
        fnScrollIntoViewHandler: scrollHandler,
        onActivate: () => openDetails(game),
        onOKActionDescription: "Details & prices",
        ...wishlistProps(game),
        onGamepadFocus: () => onFocusCard?.(game.id),
        onGamepadBlur: () => onBlurCard?.(game.id),
        onFocus: () => onFocusCard?.(game.id),
        onBlur: () => onBlurCard?.(game.id),
      },
      h(Artwork, { game, kind: "poster" }),
      h("span", { className: "tfs-store-edge", "aria-hidden": "true" }),
      h(
        "div",
        { className: "tfs-store-badges" },
        game.discountPercent > 0 ? h("span", { className: "tfs-store-discount" }, `-${game.discountPercent}%`) : h("span"),
        h("div", null, match ? h("span", { className: "tfs-store-match" }, `${match}% match`) : null, h(Heart, { game })),
      ),
      h("div", { className: "tfs-store-shade" }),
      h(
        "div",
        { className: "tfs-store-caption" },
        h("b", null, game.title),
        h("span", null, reason || game.bestStoreName || "Steam"),
      ),
      h("span", { className: "tfs-store-price" }, price),
    );
  }

  /**
   * A grid of store cards with ORBIT's dwell trailer: after 1.2 s on a tile
   * a muted 16:9 clip widens out of it. The preview never takes focus.
   */
  function StoreGrid({ games, columns, shelf = false, reasons, matches, focusId }) {
    const gridRef = React.useRef(null);
    // The preview id lives in its own host so a stick step never re-renders
    // every card of the grid.
    const preview = React.useRef(null);
    const onFocusCard = React.useCallback((id) => preview.current?.((current) => (current === id ? current : id)), []);
    const onBlurCard = React.useCallback((id) => preview.current?.((current) => (current === id ? null : current)), []);
    return h(
      Focusable,
      {
        className: `tfs-store-grid${shelf ? " is-shelf" : ""}`,
        "flow-children": shelf ? "row" : "grid",
        navEntryPreferPosition: NAV_MAINTAIN_X,
        ref: gridRef,
        style: { "--cols": columns },
      },
      games.map((game) => h(StoreCard, {
        key: game.id,
        game,
        reason: reasons?.get(game.id),
        match: matches?.get(game.id),
        autoFocus: focusId === game.id,
        onFocusCard,
        onBlurCard,
      })),
      h(PreviewHost, { games, gridRef, controller: preview }),
    );
  }

  function PreviewHost({ games, gridRef, controller }) {
    const [previewId, setPreviewId] = React.useState(null);
    controller.current = setPreviewId;
    React.useEffect(() => () => {
      controller.current = null;
    }, []);
    const game = previewId ? games.find((entry) => entry.id === previewId) : null;
    const reduce = React.useMemo(() => {
      try {
        return gridRef.current?.ownerDocument?.defaultView?.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches === true;
      } catch {
        return false;
      }
    }, [previewId]);
    if (!game?.item?.microTrailer || reduce) return null;
    return h(TrailerPreview, { key: game.id, game, gridRef });
  }

  function TrailerPreview({ game, gridRef }) {
    const [stage, setStage] = React.useState("waiting");
    const [geometry, setGeometry] = React.useState(null);
    const videoRef = React.useRef(null);
    React.useEffect(() => {
      const timers = [setTimeout(() => setStage("preload"), 550), setTimeout(() => setStage("expanded"), 1200)];
      return () => timers.forEach(clearTimeout);
    }, []);
    React.useLayoutEffect(() => {
      if (stage === "waiting") return;
      const grid = gridRef.current;
      const card = grid?.querySelector?.(`[data-store-product-id="${CSS.escape(game.id)}"]`);
      if (!grid || !card) return;
      setGeometry(measurePreview(grid, card));
    }, [stage === "waiting"]);
    React.useEffect(() => {
      const video = videoRef.current;
      if (!video) return;
      video.muted = true;
      video.defaultMuted = true;
      void video.play?.().catch?.(() => {});
    }, [geometry]);
    if (stage === "waiting" || !geometry) return null;
    const open = stage === "expanded";
    return h(
      "div",
      {
        className: `tfs-store-preview${open ? " is-open" : ""}`,
        "aria-hidden": "true",
        style: {
          left: `${geometry.left}px`,
          top: `${geometry.top}px`,
          width: `${geometry.width}px`,
          height: `${geometry.height}px`,
          clipPath: open ? geometry.full : geometry.tile,
        },
      },
      h(Artwork, { game, kind: "hero" }),
      h("video", { ref: videoRef, src: game.item.microTrailer, muted: true, loop: true, playsInline: true, autoPlay: true, preload: "auto" }),
      h("div", { className: "tfs-store-preview-shade" }),
      h(
        "div",
        { className: "tfs-store-preview-top" },
        game.discountPercent > 0 ? h("span", { className: "tfs-store-discount" }, `-${game.discountPercent}%`) : h("span"),
        h(Heart, { game }),
      ),
      h(
        "div",
        { className: "tfs-store-preview-bottom" },
        h("div", { style: { minWidth: 0 } }, h("b", null, game.title), h("span", null, game.bestStoreName || "Steam")),
        h("span", { className: "tfs-store-price" }, priceLabel(game, data.snapshot)),
      ),
    );
  }

  function Spotlight({ entry, autoFocus }) {
    const game = entry.product;
    const [engaged, setEngaged] = React.useState(false);
    const [playing, setPlaying] = React.useState(false);
    const videoRef = React.useRef(null);
    const clip = game.item?.microTrailer || "";
    React.useEffect(() => {
      setPlaying(false);
      if (!engaged || !clip) return undefined;
      const timer = setTimeout(() => setPlaying(true), 1200);
      return () => clearTimeout(timer);
    }, [engaged, clip]);
    React.useEffect(() => {
      const video = videoRef.current;
      if (!video) return;
      video.muted = true;
      void video.play?.().catch?.(() => {});
    }, [playing]);
    const sentence = reasonSentence(entry.reason);
    return h(
      Focusable,
      {
        className: "tfs-store-spotlight",
        "data-store-product-id": game.id,
        "data-store-top": "true",
        noFocusRing: true,
        autoFocus,
        fnScrollIntoViewHandler: scrollHandler,
        onActivate: () => openDetails(game),
        onOKActionDescription: "Details & prices",
        ...wishlistProps(game),
        onGamepadFocus: () => setEngaged(true),
        onGamepadBlur: () => setEngaged(false),
        onFocus: () => setEngaged(true),
        onBlur: () => setEngaged(false),
      },
      h(Artwork, { game, kind: "hero" }),
      playing ? h("video", { ref: videoRef, className: "is-playing", src: clip, muted: true, loop: true, playsInline: true, autoPlay: true }) : null,
      h("span", { className: "tfs-store-edge", "aria-hidden": "true" }),
      h("div", { className: "tfs-store-spotlight-shade" }),
      h(
        "div",
        { className: "tfs-store-spotlight-body" },
        h("p", { className: "tfs-store-eyebrow" }, h(Icon, { name: "sparkles" }), "Your top pick"),
        h("h1", null, game.title),
        sentence ? h("p", null, sentence) : null,
        h(
          "div",
          { className: "tfs-store-chips" },
          game.discountPercent > 0 ? h("span", { className: "tfs-store-discount" }, `-${game.discountPercent}%`) : null,
          h("span", { className: "tfs-store-price-pill" }, priceLabel(game, data.snapshot)),
          h("span", { className: "tfs-store-more-link" }, "Details & prices", h(Icon, { name: "chevron" })),
        ),
      ),
      h(Heart, { game }),
    );
  }

  function Shelf({ id, eyebrow, title, multiRow = false, children }) {
    return h(
      "section",
      { className: "tfs-store-shelf", "data-store-shelf": multiRow ? undefined : id },
      h("div", { className: "tfs-store-shelf-head" }, h("p", { className: "tfs-store-eyebrow" }, eyebrow), h("h2", null, title)),
      children,
    );
  }

  function shelfText(shelf) {
    switch (shelf.kind) {
      case "forYou":
        return ["For you", "Handpicked for you"];
      case "wishlistDeals":
        return ["Your wishlist", "From your wishlist, now on sale"];
      case "becauseGame":
        return ["Inspired by your play history", shelf.recent ? `Because you're playing ${shelf.game}` : `Because you played ${shelf.game}`];
      case "youWillLike":
        return ["Based on your taste", "You'll like these"];
      default:
        return ["Smart deals", "Deals that match your taste"];
    }
  }

  function DiscoverHome({ home, columns, matches, focusId, onOpenCategory, onRedeem }) {
    const firstFocus = focusId || home.spotlight?.product.id || "";
    return h(
      React.Fragment,
      null,
      home.spotlight
        ? h("section", { className: "tfs-store-shelf" }, h(Spotlight, { entry: home.spotlight, autoFocus: firstFocus === home.spotlight.product.id }))
        : null,
      home.shelves.map((shelf) => {
        const [eyebrow, title] = shelfText(shelf);
        const reasons = new Map(shelf.items.flatMap((item) => {
          // A shelf titled after one game does not repeat it on every card.
          const caption = shelf.kind === "becauseGame" ? "" : reasonCaption(item.reason);
          return caption ? [[item.product.id, caption]] : [];
        }));
        return h(Shelf, { key: shelf.id, id: shelf.id, eyebrow, title }, h(StoreGrid, {
          games: shelf.items.map((item) => item.product),
          columns,
          shelf: true,
          reasons,
          matches: shelf.kind === "forYou" || shelf.kind === "youWillLike" ? matches : undefined,
          focusId: firstFocus,
        }));
      }),
      home.categories.length
        ? h(Shelf, { id: "categories", eyebrow: "Browse", title: "Categories" }, h(
            Focusable,
            {
              className: "tfs-store-categories",
              "flow-children": home.categories.length > 4 ? "grid" : "row",
              navEntryPreferPosition: NAV_MAINTAIN_X,
              style: { "--category-cols": Math.min(4, home.categories.length) },
            },
            home.categories.map((category) => h(
              Focusable,
              {
                key: category.key,
                className: "tfs-store-category",
                "data-store-category": category.key,
                noFocusRing: true,
                autoFocus: focusId === `category:${category.key}`,
                fnScrollIntoViewHandler: scrollHandler,
                onActivate: () => onOpenCategory(category.key),
                onOKActionDescription: "Open category",
              },
              h(Artwork, { game: category.cover, kind: "hero" }),
              h("span", { className: "tfs-store-edge", "aria-hidden": "true" }),
              h("div", { className: "tfs-store-category-shade" }),
              h("div", { className: "tfs-store-category-label" }, h("b", null, category.label), h("span", null, `${category.count} games`)),
            )),
          ))
        : null,
      h(Shelf, { id: "redeem", eyebrow: "Steam wallet", title: "Got a gift card or key?" }, h(
        Focusable,
        {
          className: "tfs-store-banner",
          noFocusRing: true,
          autoFocus: focusId === "redeem",
          fnScrollIntoViewHandler: scrollHandler,
          onActivate: onRedeem,
          onOKActionDescription: "Redeem code",
        },
        h("span", { className: "tfs-store-banner-icon" }, h(Icon, { name: "gift" })),
        h("span", { className: "tfs-store-banner-text" }, h("b", null, "Redeem code"), h("span", null, "Redeem wallet codes and game keys directly in Steam.")),
        h(Icon, { name: "chevron" }),
      )),
      home.more.length
        ? h(Shelf, { id: "more", eyebrow: "Keep browsing", title: "More to discover", multiRow: true }, h(StoreGrid, {
            games: home.more,
            columns,
            matches,
            focusId: firstFocus,
          }))
        : null,
      data.snapshot?.isRefreshing || data.refreshing ? h("p", { className: "tfs-store-updating" }, "Updating prices…") : null,
    );
  }

  function Empty({ icon = "bag", title, body, action }) {
    return h(
      "div",
      { className: "tfs-store-empty" },
      h(Icon, { name: icon }),
      h("b", null, title),
      body ? h("p", null, body) : null,
      action || null,
    );
  }

  function Heading({ eyebrow, title, count, back }) {
    return h(
      "div",
      { className: "tfs-store-heading" },
      h("div", { style: { minWidth: 0 } }, h("p", { className: "tfs-store-eyebrow" }, eyebrow), h("h1", null, title)),
      back || null,
      h("p", { className: "tfs-store-count" }, count),
    );
  }

  function AlertsPage({ alerts, games }) {
    const DialogButton = ui.DialogButton;
    if (!alerts.length) {
      return h(Empty, {
        icon: "bell",
        title: "No price alerts yet",
        body: "Open a game and choose a target price. Tools for Steam keeps its starting price and future changes in the local cache.",
      });
    }
    return h(
      Focusable,
      { className: "tfs-store-alerts", "flow-children": "grid", navEntryPreferPosition: NAV_MAINTAIN_X },
      alerts.map((alert, index) => {
        const id = alertIdentity(alert);
        const game = games.find((entry) => alertIdentity(entry) === id) || {
          id: alert.gameId || `steam:${alert.steamAppId}`,
          steamAppId: alert.steamAppId || null,
          title: alert.title,
          imageUrl: alert.imageUrl,
          headerImageUrl: alert.imageUrl,
          bestDealUrl: alert.dealUrl,
        };
        const currency = alert.targetCurrencyCode || alert.currencyCode || "USD";
        const current = currency === "EUR" ? alert.currentPriceEur : alert.currentPrice;
        const start = currency === "EUR" ? alert.originalPriceEur : alert.originalPrice;
        const target = alert.mode === "discount" ? `-${alert.targetDiscountPercent}%` : formatMoney(alert.targetPrice, currency) || "—";
        const points = Array.isArray(alert.priceHistory) ? alert.priceHistory.length : 0;
        return h(
          Focusable,
          { key: id || index, className: "tfs-store-alert", "flow-children": "row" },
          h("p", { className: "tfs-store-eyebrow" }, "Price alert"),
          h("h2", null, alert.title || game.title),
          h("p", { className: "tfs-store-alert-start" }, `Started at ${formatMoney(start, currency) || "—"}`),
          h(
            "div",
            { className: "tfs-store-alert-values" },
            h("div", null, h("small", null, "Current"), h("b", null, formatMoney(current, currency) || "—")),
            h("div", null, h("small", null, "Target"), h("b", { className: "is-accent" }, target)),
          ),
          h(
            "div",
            { className: "tfs-store-alert-foot" },
            alert.reached
              ? h("span", { className: "tfs-store-reached" }, "Target reached")
              : h("span", { className: "tfs-store-muted" }, `${points} history points`),
            h(
              "div",
              { className: "tfs-store-actions" },
              h(DialogButton, {
                className: "tfs-store-pill",
                autoFocus: index === 0,
                onClick: () => {
                  data.remember([game]);
                  openDetails(game);
                },
              }, "Details & prices", h(Icon, { name: "chevron" })),
              h(DialogButton, {
                className: "tfs-store-danger",
                "aria-label": "Remove price alert",
                onClick: () => void data.saveAlert({ ...game, steamAppId: alert.steamAppId, gameId: alert.gameId, targetCurrencyCode: currency }, alert.targetPrice || 0.01, false),
              }, h(Icon, { name: "trash" })),
            ),
          ),
        );
      }),
    );
  }

  function Modal({ children, onClose }) {
    return h(
      "div",
      { className: "tfs-store-modal" },
      h(
        Focusable,
        {
          className: "tfs-store-dialog",
          "flow-children": "column",
          onCancelButton: (event) => {
            consume(event);
            onClose();
            return true;
          },
          onCancelActionDescription: "Close",
        },
        children,
      ),
    );
  }

  function Option({ icon, title, body, onActivate, autoFocus, active }) {
    return h(
      Focusable,
      { className: `tfs-store-option${active ? " is-active" : ""}`, noFocusRing: true, autoFocus, onActivate },
      h(Icon, { name: icon }),
      h("div", null, h("b", null, title), body ? h("span", null, body) : null),
      active ? h(Icon, { name: "check" }) : h(Icon, { name: "chevron" }),
    );
  }

  function RedeemDialog({ onClose }) {
    const [status, setStatus] = React.useState("");
    const DialogButton = ui.DialogButton;
    const open = (url) => {
      const opened = openInSteam(url, null, data);
      setStatus(opened ? "Opened in Steam. Enter your code there." : "Opened in your browser. Sign in to Steam and enter your code there.");
    };
    return h(
      Modal,
      { onClose },
      h("p", { className: "tfs-store-eyebrow" }, "Steam wallet"),
      h("h2", null, "Redeem a code on Steam"),
      h("p", null, "Steam has no way for other apps to redeem codes, so you enter it on Steam's own page. Tools for Steam never reads or stores your code."),
      h(Option, {
        icon: "wallet",
        title: "Wallet or gift card code",
        body: "Opens Steam's page for redeeming wallet codes",
        autoFocus: true,
        onActivate: () => open("https://store.steampowered.com/account/redeemwalletcode"),
      }),
      h(Option, {
        icon: "key",
        title: "Activate a game key",
        body: "Opens Steam's product activation page",
        onActivate: () => open("https://store.steampowered.com/account/registerkey"),
      }),
      status ? h("p", { className: "tfs-store-dialog-status" }, status) : null,
      h("div", { className: "tfs-store-dialog-foot" }, h(DialogButton, { className: "tfs-store-pill", onClick: onClose }, "Close")),
    );
  }

  function RegionDialog({ onClose }) {
    const current = String(data.snapshot?.storeRegionCode || "US").toUpperCase();
    return h(
      Modal,
      { onClose },
      h("p", { className: "tfs-store-eyebrow" }, "Prices"),
      h("h2", null, "Store region"),
      h("p", null, "Prices, discounts and price alerts follow this region and its currency."),
      h(
        Focusable,
        { className: "tfs-store-regions", "flow-children": "grid" },
        STORE_REGIONS.map(([code, name]) => h(Option, {
          key: code,
          icon: "globe",
          title: name,
          active: code === current,
          autoFocus: code === current,
          onActivate: () => {
            onClose();
            if (code !== current) void data.setRegion(code);
          },
        })),
      ),
    );
  }

  function SearchBox({ query, onQuery, onClose }) {
    const TextField = ui.TextField;
    const inputRef = React.useRef(null);
    return h(
      "div",
      { className: "tfs-store-search", "data-store-top": "true" },
      data.search.loading ? h(Icon, { name: "loader", spin: true }) : h(Icon, { name: "search" }),
      h(
        "div",
        { className: "tfs-store-search-field" },
        TextField
          ? h(TextField, {
              value: query,
              placeholder: "Search games and genres…",
              bShowClearAction: false,
              focusOnMount: true,
              autoFocus: true,
              onChange: (event) => onQuery(String(event?.target?.value ?? "")),
            })
          : h(Focusable, { autoFocus: true, onActivate: () => core.keyboard?.show?.({ element: inputRef.current }) },
              h("input", {
                ref: inputRef,
                type: "search",
                value: query,
                placeholder: "Search games and genres…",
                onChange: (event) => onQuery(String(event?.target?.value ?? "")),
              })),
      ),
      h(Focusable, { className: "tfs-store-search-close", noFocusRing: true, onActivate: onClose, onOKActionDescription: "Close search" }, h(Icon, { name: "x" })),
    );
  }

  function StorePage() {
    const ref = React.useRef(null);
    const scrollRef = React.useRef(null);
    useData();
    const columns = useColumns(ref);
    const [page, setPageState] = React.useState(session.page);
    const [direction, setDirection] = React.useState(session.direction);
    const [category, setCategory] = React.useState(session.category);
    const [searchOpen, setSearchOpen] = React.useState(Boolean(data.search.query));
    const [query, setQuery] = React.useState(data.search.query);
    const [dialog, setDialog] = React.useState(null);
    // Restore focus once per mount, then let Steam own it.
    const [focusId, setFocusId] = React.useState(session.focusId || "page-first");
    // Tags decide the shelves; give Steam's item lookup a moment before the
    // front page is laid out, so it does not rearrange under the player.
    const [settled, setSettled] = React.useState(false);

    React.useEffect(() => {
      void data.load();
      void data.loadTagNames();
      const timer = setTimeout(() => setSettled(true), 2500);
      return () => clearTimeout(timer);
    }, []);

    React.useEffect(() => {
      if (!focusId) return undefined;
      const timer = setTimeout(() => setFocusId(""), 400);
      return () => clearTimeout(timer);
    }, [focusId]);

    React.useLayoutEffect(() => {
      const scroller = scrollRef.current;
      if (scroller && session.scrollTop) scroller.scrollTop = session.scrollTop;
    }, []);

    React.useEffect(() => {
      const timer = setTimeout(() => {
        if (query.trim() !== data.search.query) void data.runSearch(query);
      }, 320);
      return () => clearTimeout(timer);
    }, [query]);

    const snapshot = data.snapshot;
    const owned = React.useMemo(() => ownedAppIds(), [snapshot]);
    const products = React.useMemo(() => productsFromSnapshot(snapshot, data.items), [snapshot, data.items.size]);
    const visible = React.useMemo(
      () => products.filter((game) => isStoreGame(game) && (!owned.has(Number(game.steamAppId)) || isWishlisted(game))),
      [products, owned],
    );
    const library = React.useMemo(() => readLibraryGames(), [snapshot]);
    const tagName = (tag) => data.tagNames.get(tag) || "";

    const itemsPending = data.pendingItems.size > 0 && !settled;
    const home = React.useMemo(() => {
      if (!visible.length) return null;
      // Same products, same arrangement: only refresh product data so a
      // wishlist toggle or price update never moves the focused card.
      const key = `${columns}|${visible.map((game) => game.id).join(",")}`;
      if (session.home && session.homeKey === key) {
        session.home = refreshHomeProducts(session.home, visible);
        return session.home;
      }
      if (itemsPending) return null;
      session.home = buildStoreHome({ products: visible, games: library, shelfSize: columns, tagName });
      session.homeKey = key;
      return session.home;
    }, [visible, columns, library, itemsPending, data.tagNames.size]);
    const matches = React.useMemo(() => (home ? storeMatchScores(visible, home.scores) : undefined), [home, visible]);

    const rememberScroll = () => {
      session.scrollTop = scrollRef.current?.scrollTop || 0;
    };

    const setPage = (next, dir) => {
      if (next === page) return;
      session.page = next;
      session.direction = dir;
      session.category = null;
      session.scrollTop = 0;
      setDirection(dir);
      setCategory(null);
      setPageState(next);
    };

    const cyclePage = (step) => {
      const index = STORE_PAGES.findIndex((entry) => entry.id === page);
      const next = STORE_PAGES[(index + step + STORE_PAGES.length) % STORE_PAGES.length];
      setFocusId("page-first");
      setPage(next.id, step);
    };

    const openCategory = (key) => {
      session.category = key;
      session.scrollTop = 0;
      setCategory(key);
      setFocusId("page-first");
    };

    const closeCategory = () => {
      const key = category;
      session.category = null;
      setCategory(null);
      setFocusId(key ? `category:${key}` : "");
    };

    const closeSearch = () => {
      setSearchOpen(false);
      setQuery("");
      data.clearSearch();
    };

    const activeQuery = searchOpen && data.search.query.length >= 2 ? data.search.query : "";
    const searchResults = React.useMemo(
      () => data.search.results.map((game) => withItem(game, data.items)),
      [data.search.results, data.items.size],
    );

    const onCancelButton = dialog
      ? undefined
      : searchOpen
        ? (event) => {
            consume(event);
            closeSearch();
            return true;
          }
        : category
          ? (event) => {
              consume(event);
              closeCategory();
              return true;
            }
          : undefined;

    // Content of the current page.
    let content;
    let firstFocus = focusId === "page-first";
    const gridFocus = (list) => (firstFocus ? list[0]?.id : focusId);
    if (activeQuery) {
      content = h(
        React.Fragment,
        null,
        h(Heading, {
          eyebrow: "Steam · Epic · GOG · Xbox · Instant Gaming",
          title: `Results for “${activeQuery}”`,
          count: data.search.loading ? "Updating prices…" : `${searchResults.length} games`,
        }),
        data.search.error ? h("div", { className: "tfs-store-error" }, data.search.error) : null,
        searchResults.length
          ? h(StoreGrid, { games: searchResults, columns, focusId: gridFocus(searchResults) })
          : h(Empty, {
              icon: "search",
              title: data.search.loading ? "Searching the stores…" : "Nothing here yet",
              body: data.search.loading ? "" : `No store has a match for “${activeQuery}”.`,
            }),
      );
    } else if (!snapshot) {
      content = h(Empty, {
        title: data.error ? "The store could not be loaded" : "Loading store cache…",
        body: data.error ? "The catalog is temporarily unavailable. Check your connection and try again." : "",
        action: data.error ? h(ui.DialogButton, { className: "tfs-store-cta", autoFocus: true, onClick: () => void data.load(true) }, h(Icon, { name: "refresh" }), "Try again") : null,
      });
    } else if (page === "discover" && category && home) {
      const list = categoryProducts(visible, category, home.scores);
      const label = STORE_CATEGORIES.find((entry) => entry.key === category)?.label || "Category";
      content = h(
        React.Fragment,
        null,
        h(Heading, {
          eyebrow: "Category",
          title: label,
          count: `${list.length} games`,
          back: h(ui.DialogButton, { className: "tfs-store-pill", onClick: closeCategory, style: { marginLeft: "auto" } }, h(Key, { label: "B" }), "Back to Discover"),
        }),
        h(StoreGrid, { games: list, columns, matches, focusId: gridFocus(list) }),
      );
    } else if (page === "discover") {
      content = home
        ? h(DiscoverHome, {
            home,
            columns,
            matches,
            focusId: firstFocus ? home.spotlight?.product.id || home.shelves[0]?.items[0]?.product.id : focusId,
            onOpenCategory: (key) => {
              rememberScroll();
              openCategory(key);
            },
            onRedeem: () => setDialog("redeem"),
          })
        : h(Empty, {
            title: data.loading || itemsPending ? "Loading store cache…" : "Nothing here yet",
            body: data.loading || itemsPending ? "" : "Connect Steam or refresh the store to load recommendations and offers.",
          });
    } else if (page === "deals") {
      const list = visible
        .filter((game) => (game.discountPercent || 0) > 0 && !owned.has(Number(game.steamAppId)))
        .sort((left, right) =>
          (right.discountPercent || 0) - (left.discountPercent || 0) ||
          (displayPrice(left, snapshot)?.value ?? Infinity) - (displayPrice(right, snapshot)?.value ?? Infinity));
      content = h(
        React.Fragment,
        null,
        h(Heading, { eyebrow: "Best deals", title: "Current discounts", count: snapshot.isRefreshing ? "Updating prices…" : `${list.length} games` }),
        list.length
          ? h(StoreGrid, { games: list, columns, focusId: gridFocus(list) })
          : h(Empty, { icon: "percent", title: "Nothing here yet", body: "Connect Steam or refresh the store to load recommendations and offers." }),
      );
    } else if (page === "wishlist") {
      const list = products
        .filter(isWishlisted)
        .sort((left, right) =>
          Number(Boolean(right.isPinned)) - Number(Boolean(left.isPinned)) ||
          Number(Boolean(right.isLocallyWishlisted)) - Number(Boolean(left.isLocallyWishlisted)) ||
          (Date.parse(right.addedAtUtc || "") || 0) - (Date.parse(left.addedAtUtc || "") || 0));
      content = h(
        React.Fragment,
        null,
        h(Heading, { eyebrow: "Your wishlist", title: "Steam + Tools for Steam wishlist", count: `${list.length} games` }),
        list.length
          ? h(StoreGrid, { games: list, columns, focusId: gridFocus(list) })
          : h(Empty, {
              icon: "heart",
              title: "Nothing here yet",
              body: snapshot.wishlistAvailable === false
                ? "Sign in to Steam in Tools for Steam to load your Steam wishlist."
                : "Press Y on any game to save it to your wishlist.",
            }),
      );
    } else {
      const alerts = (snapshot.alerts || []).filter((alert) => alert.enabled !== false);
      content = h(
        React.Fragment,
        null,
        h(Heading, { eyebrow: "Price alerts", title: "Watched prices", count: `${alerts.length} price alerts` }),
        h(AlertsPage, { alerts, games: products }),
      );
    }

    const regionName = snapshot?.storeRegionName || STORE_REGIONS.find(([code]) => code === snapshot?.storeRegionCode)?.[1] || "Region";
    const busy = data.refreshing || snapshot?.isRefreshing;

    return h(
      Focusable,
      {
        className: "tfs-store",
        ref,
        "flow-children": "column",
        style: { "--cols": columns },
        onButtonDown: (event) => {
          const button = Number(event?.detail?.button);
          if (dialog) return;
          if (button === BUTTON.BUMPER_LEFT || button === BUTTON.TRIGGER_LEFT) {
            consume(event);
            cyclePage(-1);
          } else if (button === BUTTON.BUMPER_RIGHT || button === BUTTON.TRIGGER_RIGHT) {
            consume(event);
            cyclePage(1);
          }
        },
        onSecondaryButton: dialog
          ? undefined
          : (event) => {
              consume(event);
              setSearchOpen(true);
              return true;
            },
        onSecondaryActionDescription: "Search",
        onCancelButton,
        onCancelActionDescription: searchOpen ? "Close search" : category ? "Back to Discover" : undefined,
      },
      h(
        Focusable,
        {
          className: "tfs-store-toolbar",
          "flow-children": "row",
          // ORBIT: moving up into the toolbar brings the page back to the top.
          onFocusWithin: (within) => {
            if (within && scrollRef.current?.scrollTop > 0) scrollRef.current.scrollTo({ top: 0, behavior: "smooth" });
          },
        },
        h(
          Focusable,
          { className: "tfs-store-rail", "flow-children": "row", navEntryPreferPosition: 4 },
          h(Key, { label: "LB" }),
          STORE_PAGES.map((entry, index) => h(
            Focusable,
            {
              key: entry.id,
              className: `tfs-store-tab${page === entry.id ? " is-active" : ""}`,
              noFocusRing: true,
              preferredFocus: page === entry.id,
              onActivate: () => setPage(entry.id, index > STORE_PAGES.findIndex((item) => item.id === page) ? 1 : -1),
            },
            h(Icon, { name: entry.icon }),
            entry.label,
          )),
          h(Key, { label: "RB" }),
        ),
        searchOpen
          ? h(SearchBox, { query, onQuery: setQuery, onClose: closeSearch })
          : h(
              Focusable,
              {
                className: "tfs-store-pill tfs-store-search-pill",
                noFocusRing: true,
                onActivate: () => setSearchOpen(true),
                onOKActionDescription: "Search",
              },
              h(Icon, { name: "search" }),
              h(Key, { label: "X" }),
            ),
        h(
          Focusable,
          { className: "tfs-store-actions", "flow-children": "row" },
          h(Focusable, {
            className: "tfs-store-pill",
            noFocusRing: true,
            onActivate: () => setDialog("redeem"),
          }, h(Icon, { name: "gift" }), columns >= 7 ? "Redeem code" : null),
          h(Focusable, {
            className: "tfs-store-pill",
            noFocusRing: true,
            onActivate: () => setDialog("region"),
          }, h(Icon, { name: "globe" }), regionName),
          h(Focusable, {
            className: "tfs-store-pill is-icon",
            noFocusRing: true,
            onActivate: () => void data.refresh(),
            onOKActionDescription: "Refresh store",
          }, busy ? h(Icon, { name: "loader", spin: true }) : h(Icon, { name: "refresh" })),
        ),
      ),
      h(
        "div",
        { className: "tfs-store-stage" },
        h(
          Focusable,
          {
            key: `${page}:${category || ""}:${activeQuery ? "search" : ""}`,
            className: "tfs-store-scroll",
            ref: scrollRef,
            "data-direction": direction,
            "flow-children": "column",
            onScroll: () => {
              session.scrollTop = scrollRef.current?.scrollTop || 0;
            },
          },
          data.error && snapshot ? h("div", { className: "tfs-store-error" }, data.error) : null,
          content,
        ),
      ),
      dialog === "redeem" ? h(RedeemDialog, { onClose: () => { setDialog(null); setFocusId(page === "discover" && !category ? "redeem" : ""); } }) : null,
      dialog === "region" ? h(RegionDialog, { onClose: () => setDialog(null) }) : null,
    );
  }

  // -------------------------------------------------------------------------
  // Details page
  // -------------------------------------------------------------------------

  function Theater({ game, onClose }) {
    const videoRef = React.useRef(null);
    const [error, setError] = React.useState("");
    const [muted, setMuted] = React.useState(false);
    React.useEffect(() => {
      const video = videoRef.current;
      if (!video) return undefined;
      const win = video.ownerDocument.defaultView;
      const createDashPlayer = globalThis.__tfsTrailers?.internals?.createDashPlayer;
      if (game.item?.dashTrailer && typeof createDashPlayer === "function") {
        video.muted = false;
        const player = createDashPlayer(win, video, game.item.dashTrailer, { maxHeight: 1080, onError: (message) => setError(message) });
        return () => player.destroy();
      }
      video.src = game.item?.microTrailer || "";
      video.loop = true;
      video.muted = true;
      setMuted(true);
      void video.play().catch(() => {});
      return () => {
        try {
          video.pause();
          video.removeAttribute("src");
          video.load();
        } catch {
          // ignore
        }
      };
    }, []);
    return h(
      Focusable,
      {
        className: "tfs-store-theater",
        autoFocus: true,
        noFocusRing: true,
        onActivate: () => {
          const video = videoRef.current;
          if (!video) return;
          if (video.paused) void video.play().catch(() => {});
          else video.pause();
        },
        onOKActionDescription: "Play / pause",
        onSecondaryButton: (event) => {
          consume(event);
          const video = videoRef.current;
          if (video) {
            video.muted = !video.muted;
            setMuted(video.muted);
          }
          return true;
        },
        onSecondaryActionDescription: muted ? "Sound on" : "Mute",
        onCancelButton: (event) => {
          consume(event);
          onClose();
          return true;
        },
        onCancelActionDescription: "Back to Store",
      },
      h("video", { ref: videoRef, playsInline: true }),
      error ? h("div", { className: "tfs-store-theater-error" }, `Trailer could not play: ${error}`) : null,
      h(
        "div",
        { className: "tfs-store-theater-bar" },
        h("div", null, game.item?.trailerName || game.title),
        h("span", null, h(Key, { label: "B" }), "Back to Store"),
      ),
    );
  }

  function AlertControl({ game, alert, currency, history }) {
    const DialogButton = ui.DialogButton;
    const current = currency === "EUR" ? game.cheapestPriceEur : game.cheapestPrice;
    const initial = () => {
      if (alert && alert.mode !== "discount" && Number(alert.targetPrice) > 0) return Number(alert.targetPrice);
      return Math.max(1, Math.round((Number(current) || 10) * 0.8));
    };
    const [target, setTarget] = React.useState(initial);
    React.useEffect(() => setTarget(initial()), [alert?.targetPrice, current]);
    const pending = data.pending.has(`alert:${alertIdentity(game)}`);
    const step = currency === "EUR" || currency === "USD" ? 1 : 1;
    return h(
      "section",
      { className: "tfs-store-panel" },
      h(
        "div",
        { className: "tfs-store-alert-row" },
        h(
          "div",
          null,
          h("b", null, h(Icon, { name: "bell" }), "Price alert"),
          h("span", null, alert?.reached ? "Target reached" : `${history} recorded price changes`),
        ),
        h(
          Focusable,
          { className: "tfs-store-stepper", "flow-children": "row" },
          h(DialogButton, { className: "tfs-store-round", onClick: () => setTarget((value) => Math.max(step, Math.round((value - step) * 100) / 100)) }, "−"),
          h("span", { className: "tfs-store-target" }, formatMoney(target, currency)),
          h(DialogButton, { className: "tfs-store-round", onClick: () => setTarget((value) => Math.round((value + step) * 100) / 100) }, "+"),
          h(DialogButton, {
            className: "tfs-store-button is-primary",
            disabled: pending,
            onClick: () => void data.saveAlert({ ...game, targetCurrencyCode: currency }, target, true),
          }, alert ? "Update alert" : "Watch price"),
          alert
            ? h(DialogButton, {
                className: "tfs-store-danger",
                "aria-label": "Remove price alert",
                disabled: pending,
                onClick: () => void data.saveAlert({ ...game, targetCurrencyCode: alert.targetCurrencyCode || currency }, alert.targetPrice || target, false),
              }, h(Icon, { name: "trash" }))
            : null,
        ),
      ),
    );
  }

  function OfferRow({ offer, game, autoFocus }) {
    const DialogButton = ui.DialogButton;
    const platform = /xbox/i.test(offer.storeName || "") ? "Xbox" : "PC";
    const kind = offer.storeKind === "keyshop" ? "Keyshop" : offer.storeKind === "search" ? "Store search" : "Official";
    const exact = String(offer.matchConfidence || "exact") === "exact";
    return h(
      "div",
      { className: "tfs-store-offer" },
      h(
        "div",
        { className: "tfs-store-offer-info" },
        h("div", null, h("b", null, offer.storeName), h("em", null, platform), h("em", null, kind)),
        h("p", null, exact ? "Verified exact match" : "Search result — verify edition and platform in the store"),
      ),
      offer.discountPercent > 0 ? h("span", { className: "tfs-store-offer-discount" }, `-${offer.discountPercent}%`) : null,
      h("span", { className: "tfs-store-offer-price" }, offerPriceLabel(offer, data.snapshot)),
      h(DialogButton, {
        className: "tfs-store-button",
        autoFocus,
        onClick: () => openInSteam(offer.dealUrl, game, data),
      }, isPositive(offer.price) || isPositive(offer.priceEur) ? "Buy" : "Open store"),
    );
  }

  function StoreDetailsPage(props) {
    useData();
    const gameId = decodeURIComponent(String(props?.match?.params?.gameId || ""));
    const known = data.gamesById.get(gameId);
    const game = known ? withItem(known, data.items) : null;
    const [offers, setOffers] = React.useState(() => (Array.isArray(known?.offers) ? known.offers : []));
    const [theater, setTheater] = React.useState(false);
    const [returnFocus, setReturnFocus] = React.useState("");
    const [heroClip, setHeroClip] = React.useState(false);
    const heroVideo = React.useRef(null);

    React.useEffect(() => {
      void data.load();
      if (known?.steamAppId) void data.loadItems([known.steamAppId]);
    }, [gameId]);

    React.useEffect(() => {
      if (!known) return undefined;
      let active = true;
      data.offers(known)
        .then((list) => {
          if (active && list.length) setOffers(list);
        })
        .catch(() => {});
      return () => {
        active = false;
      };
    }, [gameId, Boolean(known)]);

    // The hero quietly loops the clip after a moment, like ORBIT's spotlight.
    React.useEffect(() => {
      setHeroClip(false);
      if (!game?.item?.microTrailer || theater) return undefined;
      const timer = setTimeout(() => setHeroClip(true), 1600);
      return () => clearTimeout(timer);
    }, [game?.item?.microTrailer, theater]);
    React.useEffect(() => {
      const video = heroVideo.current;
      if (video) {
        video.muted = true;
        void video.play?.().catch?.(() => {});
      }
    }, [heroClip]);

    if (!game) {
      return h(
        "div",
        { className: "tfs-store-details" },
        h(Empty, { title: "Nothing here yet", body: "This game is no longer in the Store list." }),
      );
    }

    if (theater) {
      return h("div", { className: "tfs-store-details" }, h(Theater, {
        game,
        onClose: () => {
          setTheater(false);
          setReturnFocus("trailer");
        },
      }));
    }

    const DialogButton = ui.DialogButton;
    const snapshot = data.snapshot;
    const visibleOffers = offers.filter((offer) => offer?.storeName && offer?.dealUrl);
    const best = visibleOffers.find((offer) => offer.isBest) || visibleOffers[0];
    const bestUrl = best?.dealUrl || game.bestDealUrl;
    const currency = alertCurrency(snapshot);
    const alert = (snapshot?.alerts || []).find((entry) => entry.enabled !== false && alertIdentity(entry) === alertIdentity(game));
    const pending = data.pending.has(String(game.id));
    const item = game.item;
    const canPlay = Boolean(item?.dashTrailer || item?.microTrailer);
    const meta = [
      item?.developers?.[0],
      item?.releaseAt ? String(new Date(item.releaseAt).getFullYear()) : game.releaseText,
      item?.reviewLabel || (game.reviewPercent ? `${game.reviewPercent}% positive` : ""),
      game.isSteamWishlisted ? "On your Steam wishlist" : "",
    ].filter(Boolean);

    return h(
      Focusable,
      {
        className: "tfs-store-details",
        "flow-children": "column",
        ...wishlistProps(game),
      },
      h(
        "div",
        { className: "tfs-store-details-hero" },
        h(Artwork, { game, kind: "hero" }),
        heroClip ? h("video", { ref: heroVideo, className: "is-playing", src: item.microTrailer, muted: true, loop: true, playsInline: true, autoPlay: true }) : null,
        h(
          "div",
          { className: "tfs-store-details-title" },
          h("h1", null, game.title),
          meta.length ? h("div", { className: "tfs-store-details-meta" }, meta.map((entry) => h("span", { key: entry }, entry))) : null,
        ),
      ),
      h(
        "div",
        { className: "tfs-store-details-body" },
        h(
          "div",
          { className: "tfs-store-best", "data-store-top": "true" },
          h(
            "div",
            null,
            h("small", null, best?.storeName ? `Best price · ${best.storeName}` : "Best price"),
            h("b", null, priceLabel(game, snapshot), game.discountPercent > 0 ? h("span", { className: "tfs-store-discount" }, `-${game.discountPercent}%`) : null),
          ),
          h(
            Focusable,
            { className: "tfs-store-buttons", "flow-children": "row" },
            canPlay
              ? h(DialogButton, {
                  className: "tfs-store-button",
                  autoFocus: returnFocus === "trailer",
                  onClick: () => setTheater(true),
                }, h(Icon, { name: "play" }), "Watch trailer")
              : null,
            h(DialogButton, {
              className: "tfs-store-button",
              disabled: pending,
              onClick: () => void data.toggleWishlist(game),
            }, h(Key, { label: "Y" }), h(Icon, { name: game.isLocallyWishlisted ? "check" : "heart" }), game.isLocallyWishlisted ? "Saved to wishlist" : "Add to wishlist"),
            bestUrl
              ? h(DialogButton, {
                  className: "tfs-store-button is-primary",
                  autoFocus: returnFocus !== "trailer",
                  onClick: () => openInSteam(bestUrl, game, data),
                }, h(Icon, { name: "cart" }), "Buy best price")
              : null,
          ),
        ),
        item?.summary ? h("p", { className: "tfs-store-summary" }, item.summary) : null,
        h(AlertControl, { game, alert, currency, history: Array.isArray(game.priceHistory) ? game.priceHistory.length : 0 }),
        visibleOffers.length
          ? h(
              Focusable,
              { className: "tfs-store-offers", "flow-children": "column" },
              visibleOffers.map((offer, index) => h(OfferRow, { key: `${offer.storeName}:${offer.dealId || index}`, offer, game, autoFocus: !bestUrl && index === 0 })),
            )
          : null,
        item?.screenshots?.length
          ? h(
              React.Fragment,
              null,
              h("p", { className: "tfs-store-section-title" }, "Screenshots"),
              h("div", { className: "tfs-store-shots" }, item.screenshots.slice(0, 6).map((src) => h("img", { key: src, src, alt: "", loading: "lazy", decoding: "async" }))),
            )
          : null,
      ),
    );
  }

  return { StorePage, StoreDetailsPage };
}

// ---------------------------------------------------------------------------
// Steam library and artwork helpers
// ---------------------------------------------------------------------------

function steamApps() {
  try {
    return globalThis.collectionStore?.allAppsCollection?.allApps || [];
  } catch {
    return [];
  }
}

function ownedAppIds() {
  const owned = new Set();
  for (const app of steamApps()) {
    const appId = Number(app?.appid);
    if (Number(app?.app_type) === 1 && appId > 0) owned.add(appId);
  }
  return owned;
}

function readLibraryGames() {
  return libraryGamesFromApps(steamApps());
}

const ARTWORK_HOSTS = new Set([
  "shared.fastly.steamstatic.com",
  "cdn.cloudflare.steamstatic.com",
  "shared.akamai.steamstatic.com",
  "shared.cloudflare.steamstatic.com",
  "steamcdn-a.akamaihd.net",
  "cdn.steamgriddb.com",
  "cdn2.steamgriddb.com",
  "images.gog.com",
  "store-images.s-microsoft.com",
]);

function cachedArtworkUrl(source) {
  const value = String(source || "").trim();
  if (!value || typeof globalThis.__steamLoaderApiUrl !== "function") return value;
  try {
    const url = new URL(value);
    const host = url.hostname.toLowerCase();
    // Steam's CDN is fast and cached by Steam itself; the TFS cache helps the rest.
    if (host.endsWith("steamstatic.com")) return value;
    const trusted = ARTWORK_HOSTS.has(host) ||
      [".gog-statics.com", ".epicgames.com", ".unrealengine.com", ".s-microsoft.com"].some((suffix) => host.endsWith(suffix));
    return url.protocol === "https:" && trusted
      ? globalThis.__steamLoaderApiUrl(`api/store/artwork?source=${encodeURIComponent(value)}`)
      : value;
  } catch {
    return value;
  }
}

export function artworkSources(game, kind) {
  const appId = Number(game?.steamAppId);
  const item = game?.item;
  const steam = Number.isSafeInteger(appId) && appId > 0
    ? kind === "poster"
      ? [
          item?.poster,
          `${STEAM_ASSET_BASE}steam/apps/${appId}/library_600x900_2x.jpg`,
          `${STEAM_ASSET_BASE}steam/apps/${appId}/library_600x900.jpg`,
        ]
      : [
          item?.hero,
          `${STEAM_ASSET_BASE}steam/apps/${appId}/library_hero.jpg`,
          item?.header,
          `${STEAM_ASSET_BASE}steam/apps/${appId}/header.jpg`,
        ]
    : [];
  const supplied = kind === "poster"
    ? [game?.imageUrl, game?.fallbackImageUrl, game?.headerImageUrl]
    : [game?.headerImageUrl, game?.imageUrl, game?.fallbackImageUrl];
  return [...new Set([...steam, ...supplied].map((value) => String(value || "").trim()).filter(Boolean))].map(cachedArtworkUrl);
}

/** ORBIT's preview geometry: a 16:9 box centred on the tile, kept in the grid. */
function measurePreview(grid, card) {
  const style = getComputedStyle(grid);
  const minLeft = parseFloat(style.paddingLeft) || 0;
  const maxRight = grid.clientWidth - (parseFloat(style.paddingRight) || 0);
  const tileLeft = card.offsetLeft;
  const tileTop = card.offsetTop;
  const tileWidth = card.offsetWidth;
  const tileHeight = card.offsetHeight;
  const height = Math.min(tileHeight * 1.04, ((maxRight - minLeft) * 9) / 16);
  const width = (height * 16) / 9;
  const left = Math.max(minLeft, Math.min(tileLeft + tileWidth / 2 - width / 2, maxRight - width));
  const top = tileTop + tileHeight / 2 - height / 2;
  const inset = (value) => `${Math.max(0, Math.round(value))}px`;
  const radius = inset(parseFloat(getComputedStyle(card).borderTopLeftRadius) || 0);
  return {
    left,
    top,
    width,
    height,
    tile: `inset(${inset(tileTop - top)} ${inset(left + width - tileLeft - tileWidth)} ${inset(top + height - tileTop - tileHeight)} ${inset(tileLeft - left)} round ${radius})`,
    full: `inset(0px 0px 0px 0px round ${radius})`,
  };
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

export async function start({ version = "" } = {}) {
  const previous = globalThis.ToolsForSteamStore;
  if (previous?.version === version && previous.ready) return previous;
  previous?.deinit?.();

  const runtime = { version, ready: false, open: () => false, deinit: () => {} };
  globalThis.ToolsForSteamStore = runtime;

  const waitForCore = async () => {
    for (let attempt = 0; attempt < 600; attempt += 1) {
      const core = globalThis.__tfs;
      if (core?.routes && core.ready) return core;
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    throw new Error("tfs-core did not become ready.");
  };

  runtime.whenReady = (async () => {
    const core = await waitForCore();
    if (globalThis.ToolsForSteamStore !== runtime) return runtime;
    const data = createStoreData();
    // Without SteamClient.URL the backend opens the link in the desktop browser.
    data.openDealInBrowser = (url) =>
      defaultFetchJson("api/store/offers/open", { method: "POST", body: JSON.stringify({ dealUrl: url }) }).catch(() => {});
    const pages = createStorePages(core, data);
    // Shared stylesheet through tfs-core: reaches Big Picture's window (and a
    // re-created one) without each page injecting its own <style>.
    const removeCss = core.css?.add?.("tfs-store", STORE_CSS, { targets: ["gamepad"] });
    const removeStore = core.routes.add(STORE_ROUTE, pages.StorePage, { name: "TFS Store" });
    const removeDetails = core.routes.add(DETAILS_ROUTE, pages.StoreDetailsPage, { name: "TFS Store game" });
    runtime.data = data;
    runtime.open = () => {
      // Opening from the Quick Access menu starts at the top of the last page.
      Object.assign(session, { category: null, scrollTop: 0, focusId: "" });
      void data.load();
      return core.routes.navigate(STORE_ROUTE);
    };
    runtime.deinit = () => {
      removeStore();
      removeDetails();
      removeCss?.();
      data.deinit();
      runtime.ready = false;
    };
    runtime.ready = true;
    void data.load();
    return runtime;
  })();

  return runtime;
}
