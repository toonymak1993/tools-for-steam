// CSS Loader Theme Store as native Big Picture pages (like Decky's CSS
// Loader store): a grid of DeckThemes previews and a details page with the
// full gallery and install/enable actions.
//
// Runs as an ES module in Steam's SharedJSContext on top of tfs-core.mjs.
// The pages are real Gamepad UI routes, so Steam owns B/back, focus and the
// footer hints; data comes from the TFS API (/api/themes/*), which already
// talks to DeckThemes and the CSSLoader backend.

const STORE_ROUTE = "/tfs/themes/store";
const DETAILS_ROUTE = "/tfs/themes/store/theme/:storeId";
const CSS_ID = "tfs-themes-store";
const STYLE_ID = "tfs-themes-store-route-style";
const PER_PAGE = 24;
const CATALOG_REFRESH_MS = 10 * 60 * 1000;
const SEARCH_DEBOUNCE_MS = 500;
// Load the next page once focus reaches one of the last cards.
const LOAD_MORE_THRESHOLD = 8;

function apiUrl(path) {
  const base = globalThis.__steamLoaderApiBase || "";
  return new URL(String(path).replace(/^\//, ""), base).toString();
}

async function fetchJson(path, options = {}) {
  const response = await fetch(apiUrl(path), {
    cache: "no-store",
    ...options,
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok) {
    throw new Error(payload?.message || `Request failed (${response.status}).`);
  }
  return payload;
}

function postJson(path, body) {
  return fetchJson(path, { method: "POST", body: JSON.stringify(body ?? {}) });
}

export function formatCompactCount(value) {
  const number = Math.max(0, Number(value) || 0);
  if (number >= 1000000) {
    return `${(number / 1000000).toFixed(number >= 10000000 ? 0 : 1).replace(/\.0$/, "")}M`;
  }
  if (number >= 1000) {
    return `${(number / 1000).toFixed(number >= 100000 ? 0 : 1).replace(/\.0$/, "")}K`;
  }
  return String(Math.round(number));
}

export function targetLabel(theme) {
  const targets = Array.isArray(theme?.targets) ? theme.targets.filter(Boolean) : [];
  if (targets.length > 0) {
    return targets.slice(0, 2).join(" + ");
  }
  return theme?.target || "Big Picture";
}

/** Card / details badge: what the A button will do for this theme. */
export function installState(theme) {
  if (!theme?.installed) {
    return { id: "available", label: "", action: "Install" };
  }
  if (!theme.installedVersionMatches) {
    return { id: "update", label: "Update", action: "Update" };
  }
  return { id: "installed", label: "Installed", action: "Reinstall" };
}

/** "v1.2" for real version numbers; DeckThemes also sends short hashes. */
export function versionLabel(version) {
  const value = String(version || "").trim();
  return /^v?\d+(\.\d+)+/i.test(value) ? (value.toLowerCase().startsWith("v") ? value : `v${value}`) : "";
}

/** The API fills empty descriptions with a placeholder; don't show that box. */
export function hasRealDescription(theme) {
  const value = String(theme?.description || "").trim();
  return Boolean(value) && value !== "Available from DeckThemes.";
}

/** All preview images of a theme, largest first, without duplicates. */
export function galleryOf(theme) {
  const images = [
    ...(Array.isArray(theme?.galleryImageUrls) ? theme.galleryImageUrls : []),
    theme?.previewImageUrl,
    theme?.previewThumbnailUrl,
  ]
    .map((value) => String(value || "").trim())
    .filter((value) => /^https:\/\//i.test(value));
  return [...new Set(images)];
}

/** Store query string for /api/themes/store. */
export function buildCatalogQuery({ search = "", filter = "", order = "", page = 1, perPage = PER_PAGE } = {}) {
  const query = new URLSearchParams();
  const trimmed = String(search || "").trim();
  if (trimmed) query.set("search", trimmed);
  if (filter) query.set("filter", filter);
  if (order) query.set("order", order);
  query.set("page", String(Math.max(1, Number(page) || 1)));
  query.set("perPage", String(Math.max(1, Math.min(PER_PAGE, Number(perPage) || PER_PAGE))));
  return query.toString();
}

/** Appends a page to the list, keeping the first copy of every store id. */
export function mergeCatalogItems(existing, incoming) {
  const seen = new Set();
  const merged = [];
  for (const item of [...(existing || []), ...(incoming || [])]) {
    const key = String(item?.storeId || "");
    if (!key || seen.has(key)) continue;
    seen.add(key);
    merged.push(item);
  }
  return merged;
}

// ---------------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------------

export function createThemesStoreData(fetcher = { fetchJson, postJson }) {
  const listeners = new Set();
  const data = {
    // DeckThemes' own first order is alphabetical; popular themes first reads better.
    query: { search: "", filter: "", order: "Most Downloaded" },
    catalog: null,
    items: [],
    loadedAt: 0,
    loading: false,
    loadingMore: false,
    error: "",
    requestId: 0,
    snapshot: null,
    details: new Map(),
    detailErrors: new Map(),
    pending: new Set(),
    actionError: "",
    notify() {
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
    get total() {
      return Math.max(0, Number(data.catalog?.total) || 0);
    },
    get hasMore() {
      return Boolean(data.catalog) && data.items.length < data.total;
    },
    get backendReachable() {
      return data.snapshot?.integration?.backendReachable === true;
    },
    themeById(storeId) {
      const key = String(storeId || "");
      return data.details.get(key) || data.items.find((item) => String(item.storeId) === key) || null;
    },
    installedTheme(theme) {
      const id = String(theme?.themeId || "").toLowerCase();
      if (!id) return null;
      return (data.snapshot?.installedThemes || []).find((entry) => String(entry?.id || "").toLowerCase() === id) || null;
    },
    installedPreset(theme) {
      const id = String(theme?.themeId || "").toLowerCase();
      if (!id) return null;
      return (data.snapshot?.profiles?.installedProfiles || []).find((entry) => String(entry?.id || "").toLowerCase() === id) || null;
    },
    async loadCatalog({ append = false, force = false } = {}) {
      if (append) {
        if (data.loading || data.loadingMore || !data.hasMore) return;
      } else if (!force && data.catalog && Date.now() - data.loadedAt < CATALOG_REFRESH_MS) {
        return;
      }
      const requestId = ++data.requestId;
      const page = append ? Math.max(1, Number(data.catalog?.page) || 1) + 1 : 1;
      if (append) {
        data.loadingMore = true;
      } else {
        data.loading = true;
      }
      data.error = "";
      data.notify();
      try {
        const catalog = await fetcher.fetchJson(`api/themes/store?${buildCatalogQuery({ ...data.query, page })}`);
        if (requestId !== data.requestId) return;
        const items = Array.isArray(catalog?.items) ? catalog.items : [];
        data.catalog = catalog && typeof catalog === "object" ? catalog : null;
        data.items = append ? mergeCatalogItems(data.items, items) : mergeCatalogItems([], items);
        data.loadedAt = Date.now();
        // The server normalises unknown values; keep what it actually used.
        data.query = {
          search: data.query.search,
          filter: catalog?.filter || data.query.filter,
          order: catalog?.order || data.query.order,
        };
      } catch (error) {
        if (requestId !== data.requestId) return;
        data.error = String(error?.message || error);
      } finally {
        if (requestId === data.requestId) {
          data.loading = false;
          data.loadingMore = false;
          data.notify();
        }
      }
    },
    setQuery(partial) {
      const next = { ...data.query, ...partial };
      if (next.search === data.query.search && next.filter === data.query.filter && next.order === data.query.order) {
        return;
      }
      data.query = next;
      void data.loadCatalog({ force: true });
    },
    async loadState() {
      try {
        data.snapshot = await fetcher.fetchJson("api/themes/state");
        data.notify();
      } catch {
        // Browsing works without the CSSLoader state.
      }
    },
    async loadDetails(storeId) {
      const key = String(storeId || "");
      if (!key) return null;
      try {
        const theme = await fetcher.fetchJson(`api/themes/store/theme?storeThemeId=${encodeURIComponent(key)}`);
        data.details.set(key, theme);
        data.detailErrors.delete(key);
        data.items = data.items.map((item) => (String(item.storeId) === key ? { ...item, ...theme } : item));
      } catch (error) {
        data.detailErrors.set(key, String(error?.message || error));
      }
      data.notify();
      return data.details.get(key) || null;
    },
    async install(theme) {
      const key = String(theme?.storeId || "");
      if (!key || data.pending.has(key)) return;
      data.pending.add(key);
      data.actionError = "";
      data.notify();
      try {
        data.snapshot = await fetcher.postJson("api/themes/store/install", { storeThemeId: key });
        await data.loadDetails(key);
      } catch (error) {
        data.actionError = String(error?.message || error);
      } finally {
        data.pending.delete(key);
        data.notify();
      }
    },
    async setEnabled(theme, enabled) {
      const themeId = String(theme?.themeId || "");
      const key = `enable:${themeId}`;
      if (!themeId || data.pending.has(key)) return;
      data.pending.add(key);
      data.actionError = "";
      data.notify();
      try {
        data.snapshot = await fetcher.postJson("api/themes/themes/enabled", { themeId, enabled: Boolean(enabled) });
      } catch (error) {
        data.actionError = String(error?.message || error);
      } finally {
        data.pending.delete(key);
        data.notify();
      }
    },
  };
  return data;
}

// ---------------------------------------------------------------------------
// Styles (Steam's dark Gamepad UI, Decky CSS Loader-style preview grid)
// ---------------------------------------------------------------------------

export const THEMES_STORE_CSS = `
.tfs-themes { position: absolute; inset: 0; overflow-y: auto; overflow-x: hidden; padding: 64px 4vw 96px; box-sizing: border-box;
  color: #f4f7fb; font-family: "Motiva Sans", Arial, sans-serif;
  background: radial-gradient(1100px 480px at 90% -12%, rgba(26,159,255,.16), transparent 62%),
    radial-gradient(900px 420px at -10% 110%, rgba(155,96,255,.10), transparent 60%), #0e141b; }
.tfs-themes::-webkit-scrollbar { display: none; }
.tfs-themes-header { display: flex; align-items: flex-end; justify-content: space-between; gap: 24px; margin-bottom: 20px; }
.tfs-themes-eyebrow { font-size: 11px; font-weight: 800; letter-spacing: .22em; text-transform: uppercase; color: #1a9fff; }
.tfs-themes-title { margin: 4px 0 0; font-size: 34px; font-weight: 900; letter-spacing: -.02em; }
.tfs-themes-backend { display: inline-flex; align-items: center; gap: 8px; padding: 7px 14px; border-radius: 999px; font-size: 13px; font-weight: 700;
  background: rgba(255,255,255,.06); border: 1px solid rgba(255,255,255,.08); color: rgba(255,255,255,.75); white-space: nowrap; }
.tfs-themes-backend::before { content: ""; width: 8px; height: 8px; border-radius: 50%; background: #f5b041; box-shadow: 0 0 10px #f5b041; }
.tfs-themes-backend.is-ready::before { background: #59bf40; box-shadow: 0 0 10px #59bf40; }
.tfs-themes-toolbar { display: flex; align-items: flex-end; gap: 16px; padding: 14px 16px; margin-bottom: 10px; border-radius: 14px;
  background: rgba(255,255,255,.045); border: 1px solid rgba(255,255,255,.06); }
.tfs-themes-search { flex: 1 1 auto; min-width: 0; }
.tfs-themes-search > * { margin: 0 !important; }
.tfs-themes-pick { flex: 0 0 230px; min-width: 0; }
.tfs-themes-pick-label { margin: 0 0 6px 2px; font-size: 11px; font-weight: 800; letter-spacing: .14em; text-transform: uppercase; color: rgba(255,255,255,.5); }
.tfs-themes-summary { margin: 8px 2px 14px; font-size: 13px; color: rgba(255,255,255,.55); }
.tfs-themes-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(300px, 100%), 1fr)); gap: 18px; padding: 6px 2px 10px; }
.tfs-themes-card { position: relative; aspect-ratio: 16 / 10; border-radius: 14px; overflow: hidden; background: #1a222d;
  box-shadow: 0 10px 26px rgba(0,0,0,.42); outline: none;
  transition: transform .14s cubic-bezier(.22,1,.36,1), box-shadow .14s; }
.tfs-themes-card > img, .tfs-themes-placeholder { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover;
  transition: transform .3s cubic-bezier(.22,1,.36,1); }
.tfs-themes-card.gpfocus { transform: scale(1.035); z-index: 2; box-shadow: 0 0 0 3px #fff, 0 0 0 7px rgba(26,159,255,.55), 0 18px 40px rgba(0,0,0,.6); }
.tfs-themes-card.gpfocus > img { transform: scale(1.05); }
.tfs-themes-placeholder { display: flex; align-items: center; justify-content: center; font-size: 46px; font-weight: 900; color: rgba(255,255,255,.18);
  background: linear-gradient(135deg, #24344a, #141b25 60%, #2a1f3d); }
.tfs-themes-card-top { position: absolute; z-index: 2; top: 10px; left: 10px; right: 10px; display: flex; justify-content: space-between; gap: 8px; }
.tfs-themes-chip { max-width: 70%; padding: 4px 9px; border-radius: 7px; font-size: 11px; font-weight: 800; letter-spacing: .04em;
  background: rgba(8,12,18,.72); color: rgba(255,255,255,.85); backdrop-filter: blur(6px); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.tfs-themes-badge { padding: 4px 9px; border-radius: 7px; font-size: 11px; font-weight: 900; letter-spacing: .04em; white-space: nowrap; }
.tfs-themes-badge.is-installed { background: #59bf40; color: #071205; }
.tfs-themes-badge.is-update { background: #f5b041; color: #1d1203; }
.tfs-themes-card-body { position: absolute; z-index: 2; left: 0; right: 0; bottom: 0; padding: 34px 14px 12px;
  background: linear-gradient(0deg, rgba(5,8,12,.94), rgba(5,8,12,.7) 55%, transparent); }
.tfs-themes-card-title { font-size: 17px; font-weight: 800; line-height: 1.2; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.tfs-themes-card-meta { display: flex; justify-content: space-between; gap: 12px; margin-top: 3px; font-size: 12px; color: rgba(255,255,255,.62); }
.tfs-themes-card-meta > span:first-child { min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.tfs-themes-stats { flex: 0 0 auto; display: inline-flex; gap: 10px; font-weight: 700; color: rgba(255,255,255,.78); }
.tfs-themes-skeleton { aspect-ratio: 16 / 10; border-radius: 14px; background: linear-gradient(100deg, #18202a 30%, #222c38 50%, #18202a 70%);
  background-size: 300% 100%; animation: tfs-themes-shimmer 1.4s ease-in-out infinite; }
@keyframes tfs-themes-shimmer { from { background-position: 100% 0; } to { background-position: 0 0; } }
.tfs-themes-empty { padding: 28px; border-radius: 16px; background: rgba(255,255,255,.04); color: rgba(255,255,255,.65); font-size: 15px; }
.tfs-themes-error { margin: 10px 0; padding: 12px 16px; border-radius: 12px; background: rgba(248,113,113,.14); color: #fecaca; font-size: 14px; }
.tfs-themes-more { padding: 18px 0 4px; text-align: center; font-size: 13px; color: rgba(255,255,255,.5); }
.tfs-themes-more .DialogButton { max-width: 320px; margin: 0 auto; }
.tfs-themes-detail { display: grid; grid-template-columns: minmax(0, 1.55fr) minmax(320px, 1fr); gap: 32px; align-items: start; }
.tfs-themes-stage { position: relative; aspect-ratio: 16 / 10; border-radius: 18px; overflow: hidden; background: #1a222d;
  box-shadow: 0 22px 50px rgba(0,0,0,.5); border: 1px solid rgba(255,255,255,.08); }
.tfs-themes-stage > img, .tfs-themes-stage > .tfs-themes-placeholder { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
.tfs-themes-thumbs { display: flex; gap: 10px; margin-top: 12px; padding: 4px 2px; overflow-x: auto; }
.tfs-themes-thumbs::-webkit-scrollbar { display: none; }
.tfs-themes-thumb { position: relative; flex: 0 0 132px; aspect-ratio: 16 / 10; border-radius: 9px; overflow: hidden; opacity: .55;
  outline: none; transition: opacity .12s, transform .12s, box-shadow .12s; }
.tfs-themes-thumb img { width: 100%; height: 100%; object-fit: cover; }
.tfs-themes-thumb.is-active { opacity: 1; box-shadow: 0 0 0 2px rgba(26,159,255,.9); }
.tfs-themes-thumb.gpfocus { opacity: 1; transform: scale(1.06); box-shadow: 0 0 0 3px #fff; }
.tfs-themes-info { display: flex; flex-direction: column; gap: 14px; }
.tfs-themes-info h1 { margin: 6px 0 0; font-size: 38px; font-weight: 900; line-height: 1.02; letter-spacing: -.03em; }
.tfs-themes-byline { font-size: 15px; color: rgba(255,255,255,.65); }
.tfs-themes-facts { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; }
.tfs-themes-fact { padding: 12px 14px; border-radius: 12px; background: rgba(255,255,255,.05); border: 1px solid rgba(255,255,255,.06); }
.tfs-themes-fact-value { font-size: 20px; font-weight: 900; }
.tfs-themes-fact-label { margin-top: 2px; font-size: 11px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; color: rgba(255,255,255,.5); }
.tfs-themes-actions { display: flex; flex-direction: column; gap: 10px; }
.tfs-themes-actions .DialogButton { width: 100%; }
.tfs-themes-hint { font-size: 13px; line-height: 1.45; color: rgba(255,255,255,.55); }
.tfs-themes-description { margin-top: 26px; padding: 18px 20px; border-radius: 14px; background: rgba(255,255,255,.04);
  border: 1px solid rgba(255,255,255,.06); font-size: 15px; line-height: 1.55; color: rgba(255,255,255,.82); white-space: pre-line; }
.tfs-themes-description h3 { margin: 0 0 8px; font-size: 13px; font-weight: 800; letter-spacing: .14em; text-transform: uppercase; color: rgba(255,255,255,.5); }
`;

// ---------------------------------------------------------------------------
// Pages
// ---------------------------------------------------------------------------

function createThemesStorePages(core, data) {
  const React = core.React;
  const h = React.createElement;
  const ui = core.ui;
  const Focusable = ui.Focusable;

  function useData() {
    const [, rerender] = React.useReducer((value) => value + 1, 0);
    React.useEffect(() => data.subscribe(rerender), []);
    return data;
  }

  function useStyles(ref) {
    React.useLayoutEffect(() => {
      // core.css normally delivers the sheet; this covers an unregistered window.
      const doc = ref.current?.ownerDocument;
      if (!doc || doc.getElementById(STYLE_ID) || doc.getElementById(`tfs-css-${CSS_ID}`)) {
        return;
      }
      const style = doc.createElement("style");
      style.id = STYLE_ID;
      style.textContent = THEMES_STORE_CSS;
      doc.head.append(style);
    }, []);
  }

  const revealOnFocus = (event) => {
    try {
      event?.currentTarget?.scrollIntoView?.({ block: "center", inline: "nearest", behavior: "smooth" });
    } catch {
      // ignore
    }
  };

  function Preview({ src, title, eager = false }) {
    const [failed, setFailed] = React.useState(false);
    React.useEffect(() => setFailed(false), [src]);
    if (!src || failed) {
      return h("div", { className: "tfs-themes-placeholder" }, String(title || "?").trim().charAt(0).toUpperCase());
    }
    return h("img", {
      src,
      alt: "",
      loading: eager ? "eager" : "lazy",
      decoding: "async",
      draggable: false,
      onError: () => setFailed(true),
    });
  }

  function openDetails(theme) {
    if (!theme?.storeId) return;
    data.details.has(String(theme.storeId)) || data.details.set(String(theme.storeId), theme);
    core.routes.navigate(`/tfs/themes/store/theme/${encodeURIComponent(theme.storeId)}`);
  }

  function ThemeCard({ theme, index, autoFocus }) {
    const state = installState(theme);
    return h(
      Focusable,
      {
        className: "tfs-themes-card",
        onActivate: () => openDetails(theme),
        onFocus: (event) => {
          revealOnFocus(event);
          if (index >= data.items.length - LOAD_MORE_THRESHOLD) {
            void data.loadCatalog({ append: true });
          }
        },
        autoFocus,
      },
      h(Preview, { src: theme.previewThumbnailUrl || theme.previewImageUrl, title: theme.title }),
      h(
        "div",
        { className: "tfs-themes-card-top" },
        h("span", { className: "tfs-themes-chip" }, targetLabel(theme)),
        state.label ? h("span", { className: `tfs-themes-badge is-${state.id}` }, state.label) : null,
      ),
      h(
        "div",
        { className: "tfs-themes-card-body" },
        h("div", { className: "tfs-themes-card-title" }, theme.title),
        h(
          "div",
          { className: "tfs-themes-card-meta" },
          h("span", null, `by ${theme.author || "DeckThemes"}`),
          h(
            "span",
            { className: "tfs-themes-stats" },
            h("span", null, `↓ ${formatCompactCount(theme.downloadCount)}`),
            h("span", null, `★ ${formatCompactCount(theme.starCount)}`),
          ),
        ),
      ),
    );
  }

  function Picker({ label, value, options, onChange }) {
    const Dropdown = ui.Dropdown;
    const list = options.length ? options : [value || "All"];
    const selected = Math.max(0, list.indexOf(value));
    const rgOptions = list.map((option, index) => ({ data: index, label: option }));
    return h(
      "div",
      { className: "tfs-themes-pick" },
      h("div", { className: "tfs-themes-pick-label" }, label),
      Dropdown
        ? h(Dropdown, {
            rgOptions,
            selectedOption: selected,
            menuLabel: label,
            onChange: (option) => onChange(list[Number(option?.data ?? 0)] || list[0]),
          })
        : h(
            ui.DialogButton,
            { onClick: () => onChange(list[(selected + 1) % list.length]) },
            list[selected],
          ),
    );
  }

  function Toolbar() {
    const TextField = ui.TextField;
    const [search, setSearch] = React.useState(data.query.search);
    React.useEffect(() => {
      const timer = setTimeout(() => data.setQuery({ search: search.trim() }), SEARCH_DEBOUNCE_MS);
      return () => clearTimeout(timer);
    }, [search]);
    const catalog = data.catalog;
    return h(
      Focusable,
      { className: "tfs-themes-toolbar", "flow-children": "row" },
      h(
        "div",
        { className: "tfs-themes-search" },
        TextField
          ? h(TextField, {
              label: "Search themes",
              value: search,
              bShowClearAction: true,
              onChange: (event) => setSearch(String(event?.target?.value ?? "")),
            })
          : null,
      ),
      h(Picker, {
        label: "Filter",
        value: data.query.filter || catalog?.filter || "All",
        options: Array.isArray(catalog?.availableFilters) ? catalog.availableFilters : [],
        onChange: (filter) => data.setQuery({ filter }),
      }),
      h(Picker, {
        label: "Sort by",
        value: data.query.order || catalog?.order || "Most Downloaded",
        options: Array.isArray(catalog?.availableOrders) ? catalog.availableOrders : [],
        onChange: (order) => data.setQuery({ order }),
      }),
    );
  }

  function Grid() {
    if (data.loading && !data.items.length) {
      return h(
        "div",
        { className: "tfs-themes-grid" },
        Array.from({ length: 8 }, (_, index) => h("div", { key: index, className: "tfs-themes-skeleton" })),
      );
    }
    if (!data.items.length) {
      return data.error
        ? null
        : h(
            "div",
            { className: "tfs-themes-empty" },
            data.query.search ? `No themes match "${data.query.search}".` : "No themes in this category yet.",
          );
    }
    return h(
      Focusable,
      { className: "tfs-themes-grid", "flow-children": "grid" },
      data.items.map((theme, index) => h(ThemeCard, { key: theme.storeId, theme, index, autoFocus: index === 0 })),
    );
  }

  function Footer() {
    if (!data.items.length) return null;
    if (data.loadingMore) {
      return h("div", { className: "tfs-themes-more" }, "Loading more themes…");
    }
    if (data.error && data.hasMore) {
      return h(
        "div",
        { className: "tfs-themes-more" },
        h(ui.DialogButton, { onClick: () => void data.loadCatalog({ append: true }) }, "Try loading more"),
      );
    }
    if (!data.hasMore) {
      return h("div", { className: "tfs-themes-more" }, `All ${data.items.length} themes shown`);
    }
    return null;
  }

  function StorePage() {
    const ref = React.useRef(null);
    useStyles(ref);
    useData();
    React.useEffect(() => {
      void data.loadCatalog();
      void data.loadState();
    }, []);
    const shown = data.items.length;
    const summary = data.catalog
      ? `${data.total.toLocaleString()} theme${data.total === 1 ? "" : "s"}${data.query.search ? ` for "${data.query.search}"` : ""} · ${data.query.order || data.catalog.order || ""}${shown && shown < data.total ? ` · ${shown} loaded` : ""}`
      : data.loading
        ? "Loading DeckThemes…"
        : "";
    return h(
      "div",
      { className: "tfs-themes", ref },
      h(
        "div",
        { className: "tfs-themes-header" },
        h(
          "div",
          null,
          h("div", { className: "tfs-themes-eyebrow" }, "CSS Loader"),
          h("h1", { className: "tfs-themes-title" }, "Theme Store"),
        ),
        h(
          "div",
          { className: `tfs-themes-backend${data.backendReachable ? " is-ready" : ""}` },
          data.snapshot ? (data.backendReachable ? "CSS Loader ready" : "CSS Loader offline · browse only") : "Checking CSS Loader…",
        ),
      ),
      h(Toolbar),
      h("div", { className: "tfs-themes-summary" }, summary),
      data.error ? h("div", { className: "tfs-themes-error" }, data.error) : null,
      h(Grid),
      h(Footer),
    );
  }

  function Gallery({ theme }) {
    const images = galleryOf(theme);
    const [index, setIndex] = React.useState(0);
    React.useEffect(() => setIndex(0), [theme?.storeId]);
    const current = images[Math.min(index, Math.max(0, images.length - 1))] || "";
    return h(
      "div",
      null,
      h("div", { className: "tfs-themes-stage" }, h(Preview, { src: current, title: theme?.title, eager: true })),
      images.length > 1
        ? h(
            Focusable,
            { className: "tfs-themes-thumbs", "flow-children": "row" },
            images.map((src, imageIndex) =>
              h(
                Focusable,
                {
                  key: src,
                  className: `tfs-themes-thumb${imageIndex === index ? " is-active" : ""}`,
                  onActivate: () => setIndex(imageIndex),
                  onGamepadFocus: () => setIndex(imageIndex),
                  onFocus: () => setIndex(imageIndex),
                },
                h("img", { src, alt: "", loading: "lazy", decoding: "async", draggable: false }),
              ),
            ),
          )
        : null,
    );
  }

  function DetailsPage(props) {
    const ref = React.useRef(null);
    useStyles(ref);
    useData();
    const storeId = decodeURIComponent(String(props?.match?.params?.storeId || ""));
    React.useEffect(() => {
      void data.loadDetails(storeId);
      void data.loadState();
    }, [storeId]);
    const theme = data.themeById(storeId);
    if (!theme) {
      const error = data.detailErrors.get(storeId);
      return h(
        "div",
        { className: "tfs-themes", ref },
        error
          ? h("div", { className: "tfs-themes-error" }, error)
          : h("div", { className: "tfs-themes-detail" }, h("div", { className: "tfs-themes-skeleton" }), h("div", null)),
      );
    }

    const DialogButton = ui.DialogButton;
    const state = installState(theme);
    const installing = data.pending.has(String(theme.storeId));
    const isPreset = String(theme.target || "").toLowerCase() === "profile";
    const installed = data.installedTheme(theme);
    const preset = isPreset ? data.installedPreset(theme) : null;
    const enabling = data.pending.has(`enable:${theme.themeId}`);
    const backendReady = data.backendReachable;
    const installLabel = installing ? "Installing…" : state.action;
    // Installing starts the CSSLoader backend when it is installed but idle.
    const hint = !backendReady && data.snapshot
      ? "CSS Loader is not running yet. Installing tries to start it; if that fails, check Quick Access › CSS Loader › Settings."
      : isPreset
        ? preset || theme.installed
          ? "Installed as a preset. Apply it in Quick Access › CSS Loader › Presets."
          : "This is a preset: it installs a set of themes you can apply in Quick Access › CSS Loader › Presets."
        : installed
          ? "Change this theme's options in Quick Access › CSS Loader › Installed Themes."
          : theme.dependencyCount > 0
            ? `Installs ${theme.dependencyCount} required theme${theme.dependencyCount === 1 ? "" : "s"} too.`
            : "";

    return h(
      "div",
      { className: "tfs-themes", ref },
      h(
        "div",
        { className: "tfs-themes-detail" },
        h(Gallery, { theme }),
        h(
          "div",
          { className: "tfs-themes-info" },
          h(
            "div",
            null,
            h("div", { className: "tfs-themes-eyebrow" }, `${targetLabel(theme)}${state.label ? ` · ${state.label}` : ""}`),
            h("h1", null, theme.title),
          ),
          h("div", { className: "tfs-themes-byline" }, [`by ${theme.author || "DeckThemes"}`, versionLabel(theme.version)].filter(Boolean).join(" · ")),
          h(
            "div",
            { className: "tfs-themes-facts" },
            h("div", { className: "tfs-themes-fact" },
              h("div", { className: "tfs-themes-fact-value" }, formatCompactCount(theme.downloadCount)),
              h("div", { className: "tfs-themes-fact-label" }, "Downloads")),
            h("div", { className: "tfs-themes-fact" },
              h("div", { className: "tfs-themes-fact-value" }, formatCompactCount(theme.starCount)),
              h("div", { className: "tfs-themes-fact-label" }, "Stars")),
            h("div", { className: "tfs-themes-fact" },
              h("div", { className: "tfs-themes-fact-value" }, String(theme.dependencyCount || 0)),
              h("div", { className: "tfs-themes-fact-label" }, "Requires")),
          ),
          h(
            Focusable,
            { className: "tfs-themes-actions", "flow-children": "column" },
            h(DialogButton, {
              autoFocus: true,
              disabled: installing,
              onClick: () => void data.install(theme),
            }, installLabel),
            installed && !isPreset
              ? h(DialogButton, {
                  disabled: enabling || installing || !backendReady,
                  onClick: () => void data.setEnabled(theme, !installed.enabled),
                }, enabling ? "Saving…" : installed.enabled ? "Turn off" : "Turn on")
              : null,
          ),
          data.actionError ? h("div", { className: "tfs-themes-error" }, data.actionError) : null,
          hint ? h("div", { className: "tfs-themes-hint" }, hint) : null,
        ),
      ),
      hasRealDescription(theme)
        ? h("div", { className: "tfs-themes-description" }, h("h3", null, "About this theme"), theme.description)
        : null,
    );
  }

  return { StorePage, DetailsPage };
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

export async function start({ version = "" } = {}) {
  const previous = globalThis.ToolsForSteamThemesStore;
  if (previous?.version === version && previous.ready) {
    return previous;
  }
  previous?.deinit?.();

  const runtime = { version, ready: false, open: () => false, deinit: () => {} };
  globalThis.ToolsForSteamThemesStore = runtime;

  const waitForCore = async () => {
    for (let attempt = 0; attempt < 600; attempt += 1) {
      const core = globalThis.__tfs;
      if (core?.routes && core.ready) {
        return core;
      }
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    throw new Error("tfs-core did not become ready.");
  };

  runtime.whenReady = (async () => {
    const core = await waitForCore();
    if (globalThis.ToolsForSteamThemesStore !== runtime) {
      return runtime;
    }
    const data = createThemesStoreData();
    const pages = createThemesStorePages(core, data);
    const removeCss = core.css?.add?.(CSS_ID, THEMES_STORE_CSS, { targets: ["gamepad"] });
    const removeStore = core.routes.add(STORE_ROUTE, pages.StorePage, { name: "TFS Theme Store" });
    const removeDetails = core.routes.add(DETAILS_ROUTE, pages.DetailsPage, { name: "TFS Theme Store theme" });
    runtime.data = data;
    runtime.open = (storeId = "") => {
      void data.loadCatalog();
      const id = String(storeId || "").trim();
      return core.routes.navigate(id ? `/tfs/themes/store/theme/${encodeURIComponent(id)}` : STORE_ROUTE);
    };
    runtime.deinit = () => {
      removeStore();
      removeDetails();
      removeCss?.();
      runtime.ready = false;
    };
    runtime.ready = true;
    return runtime;
  })();

  return runtime;
}
