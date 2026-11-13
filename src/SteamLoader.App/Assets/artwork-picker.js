// TFS Artworks page (SharedJSContext).
//
// A real Big Picture route (/tfs/artworks/:appId) built from Steam's own
// components (window.__tfs.ui), so Steam's focus navigation, footer hints and
// back button work natively:
//  - tabs Cover / Wide / Hero / Logo / Icon (LB/RB switch tabs);
//  - sources: SteamGridDB (exact match through the Steam app id, else title
//    search; the match can be changed) and Steam's official store artwork,
//    including seasonal event variants;
//  - SteamGridDB filters per artwork type (style, animated, size, NSFW,
//    humor), remembered per type;
//  - quality badges against Steam's slot size, large preview (X), apply (A),
//    restore Steam's original artwork.
//
// Inspired by Playhub Artworks (MIT, LoZazaMastro); independent code.
(() => {
  const version = 5;

  // -----------------------------------------------------------------------
  // Pure helpers (tests reach them through window.__tfsArtworkPickerTestHooks)
  // -----------------------------------------------------------------------

  const artworkTypes = [
    { id: "grid_p", label: "Cover", steamAssetType: 0, targetWidth: 600, targetHeight: 900, columns: 6, ratio: "2 / 3" },
    { id: "grid_l", label: "Wide", steamAssetType: 3, targetWidth: 920, targetHeight: 430, columns: 4, ratio: "92 / 43" },
    { id: "hero", label: "Hero", steamAssetType: 1, targetWidth: 1920, targetHeight: 620, columns: 3, ratio: "96 / 31" },
    { id: "logo", label: "Logo", steamAssetType: 2, targetWidth: 1000, targetHeight: 0, columns: 4, ratio: "16 / 9" },
    { id: "icon", label: "Icon", steamAssetType: 4, targetWidth: 256, targetHeight: 256, columns: 8, ratio: "1 / 1" },
  ];

  const styleOptions = {
    grid_p: [["alternate", "Alternate"], ["blurred", "Blurred"], ["white_logo", "White logo"], ["material", "Material"], ["no_logo", "No logo"]],
    grid_l: [["alternate", "Alternate"], ["blurred", "Blurred"], ["white_logo", "White logo"], ["material", "Material"], ["no_logo", "No logo"]],
    hero: [["alternate", "Alternate"], ["blurred", "Blurred"], ["material", "Material"]],
    logo: [["official", "Official"], ["white", "White"], ["black", "Black"], ["custom", "Custom"]],
    icon: [["official", "Official"], ["custom", "Custom"]],
  };

  function typeById(id) {
    return artworkTypes.find((type) => type.id === id) || artworkTypes[0];
  }

  /**
   * How well an image fits Steam's slot: "sharp" at or above the slot size,
   * "ok" from two thirds, "low" below; "crop" when the shape is clearly off.
   */
  function qualityOf(asset, typeId) {
    const type = typeById(typeId);
    const width = Number(asset?.width) || 0;
    const height = Number(asset?.height) || 0;
    if (!width || !height) return { rank: 1, label: "Unknown size", tone: "neutral" };
    if (type.id === "logo") {
      const ratio = Math.max(width, height) / type.targetWidth;
      return ratio >= 1 ? { rank: 3, label: "Sharp", tone: "good" } : ratio >= 0.5 ? { rank: 2, label: "OK", tone: "ok" } : { rank: 1, label: "Low", tone: "low" };
    }
    const wanted = type.targetWidth / type.targetHeight;
    const shapeOff = Math.abs(width / height - wanted) / wanted > 0.08;
    const scale = Math.min(width / type.targetWidth, height / type.targetHeight);
    const base = scale >= 1 ? { rank: 3, label: "Sharp", tone: "good" } : scale >= 0.66 ? { rank: 2, label: "OK", tone: "ok" } : { rank: 1, label: "Low", tone: "low" };
    return shapeOff ? { rank: Math.max(0, base.rank - 1), label: `${base.label} · crops`, tone: base.tone === "good" ? "ok" : base.tone } : base;
  }

  /** Best first: quality, then pixels, then SteamGridDB score. */
  function sortAssets(assets, typeId) {
    return [...assets].sort((left, right) =>
      (qualityOf(right, typeId).rank - qualityOf(left, typeId).rank) ||
      ((Number(right.width) * Number(right.height) || 0) - (Number(left.width) * Number(left.height) || 0)) ||
      ((Number(right.score) || 0) - (Number(left.score) || 0)));
  }

  function defaultFilter() {
    return { styles: [], animation: "all", square: false, nsfw: false, humor: false };
  }

  function normalizeFilter(typeId, value) {
    const base = defaultFilter();
    const allowed = new Set((styleOptions[typeId] || []).map(([id]) => id));
    const styles = Array.isArray(value?.styles) ? value.styles.filter((style) => allowed.has(style)) : [];
    const animation = ["all", "static", "animated"].includes(value?.animation) ? value.animation : base.animation;
    return {
      styles: [...new Set(styles)],
      animation,
      square: typeId === "grid_p" && value?.square === true,
      nsfw: value?.nsfw === true,
      humor: value?.humor === true,
    };
  }

  function buildAssetQuery(gameId, typeId, filter, page) {
    const normalized = normalizeFilter(typeId, filter);
    const parts = [`gameId=${Number(gameId) || 0}`, `type=${encodeURIComponent(typeId)}`, `page=${Number(page) || 0}`];
    if (normalized.styles.length) parts.push(`styles=${encodeURIComponent(normalized.styles.join(","))}`);
    if (normalized.animation !== "all") parts.push(`animation=${normalized.animation}`);
    if (normalized.square) parts.push(`dimensions=${encodeURIComponent("512x512,1024x1024")}`);
    if (normalized.nsfw) parts.push("nsfw=1");
    if (normalized.humor) parts.push("humor=1");
    return `/api/artwork/picker/assets?${parts.join("&")}`;
  }

  function filterSummary(typeId, filter) {
    const normalized = normalizeFilter(typeId, filter);
    const parts = [];
    if (normalized.styles.length) parts.push(normalized.styles.map((style) => (styleOptions[typeId] || []).find(([id]) => id === style)?.[1] || style).join(", "));
    if (normalized.animation !== "all") parts.push(normalized.animation === "animated" ? "Animated only" : "Static only");
    if (normalized.square) parts.push("Square");
    if (normalized.nsfw) parts.push("NSFW");
    if (normalized.humor) parts.push("Humor");
    return parts.length ? parts.join(" · ") : "No filters";
  }

  function chunk(items, size) {
    const rows = [];
    for (let index = 0; index < items.length; index += size) rows.push(items.slice(index, index + size));
    return rows;
  }

  /** Starting logo placement: Steam-like lower left on heroes, centred on banners. */
  function defaultComposerLayout(kind) {
    return kind === "hero"
      ? { logoX: 22, logoY: 62, logoWidth: 34, opacity: 100, shadow: 45, backgroundY: 50 }
      : { logoX: 50, logoY: 50, logoWidth: 62, opacity: 100, shadow: 40, backgroundY: 50 };
  }

  function nextTypeId(currentId, direction) {
    const index = artworkTypes.findIndex((type) => type.id === currentId);
    const next = (index + direction + artworkTypes.length) % artworkTypes.length;
    return artworkTypes[next].id;
  }

  const internals = { artworkTypes, styleOptions, qualityOf, sortAssets, normalizeFilter, buildAssetQuery, filterSummary, chunk, nextTypeId };
  if (window.__tfsArtworkPickerTestHooks === true) {
    window.__tfsArtworkPickerInternals = internals;
    return "test";
  }

  // -----------------------------------------------------------------------
  // Runtime
  // -----------------------------------------------------------------------

  const role = window.__tfsSurfaceRole;
  if (role && role !== "shared") return "skipped";

  const existing = window.__tfsArtworks;
  if (existing?.version >= version) {
    existing.refresh?.();
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const route = "/tfs/artworks/:appId";
  const toolsRoute = "/tfs/artworks";
  const filterStorageKey = "ToolsForSteamArtworkFilters.v1";
  const styleId = "tfs-artworks-style";
  const shortcutAppType = 1073741824;
  const gamepadButton = { cancel: 2, secondary: 3, bumperLeft: 5, bumperRight: 6 };
  const state = { routeRemover: null, toolsRemover: null, routeTimer: 0, core: null, coreWatch: 0, destroyed: false };

  function apiBase() {
    const base = window.__steamLoaderApiBase || apiBaseFallback;
    return base.endsWith("/") ? base : `${base}/`;
  }

  async function api(path, body) {
    const response = await fetch(`${apiBase()}${String(path).replace(/^\//, "")}`, body === undefined
      ? { cache: "no-store" }
      : { method: "POST", cache: "no-store", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const payload = await response.json().catch(() => null);
    return { ok: response.ok, status: response.status, payload };
  }

  function appOverview(appId) {
    try { return window.appStore?.GetAppOverviewByAppID?.(appId) || null; } catch { return null; }
  }

  function bigPictureWindow() {
    try {
      const store = window.SteamUIStore?.m_WindowStore || window.SteamUIStore?.WindowStore;
      return store?.GamepadUIMainWindowInstance?.BrowserWindow || null;
    } catch {
      return null;
    }
  }

  function customImageUrl(appId, typeId) {
    const overview = appOverview(appId);
    if (!overview || !window.appStore) return "";
    const getter = {
      grid_p: "GetCustomVerticalCapsuleURLs",
      grid_l: "GetCustomLandcapeImageURLs",
      hero: "GetCustomHeroImageURLs",
      logo: "GetCustomLogoImageURLs",
    }[typeId];
    try {
      const urls = getter ? window.appStore[getter]?.(overview) : null;
      return Array.isArray(urls) && urls.length ? String(urls[0]) : "";
    } catch {
      return "";
    }
  }

  function libraryShortcuts() {
    try {
      const apps = window.collectionStore?.allGamesCollection?.allApps || [];
      return apps.filter((app) => Number(app?.app_type) === shortcutAppType && Number(app?.appid) > 0);
    } catch {
      return [];
    }
  }

  function loadFilters() {
    try {
      return JSON.parse(window.localStorage.getItem(filterStorageKey) || "{}") || {};
    } catch {
      return {};
    }
  }

  function saveFilters(filters) {
    try { window.localStorage.setItem(filterStorageKey, JSON.stringify(filters)); } catch {}
  }

  async function applyArtwork(appId, typeId, asset) {
    const { ok, payload } = await api("/api/artwork/apply", { appId, assetType: typeId, url: asset.url });
    if (!ok || !payload?.success) throw new Error(payload?.message || "The artwork could not be applied.");
    const apps = window.SteamClient?.Apps;
    if (apps?.SetCustomArtworkForApp && payload.base64Data) {
      await apps.SetCustomArtworkForApp(Number(payload.appId), payload.base64Data, payload.extension || "png", Number(payload.steamAssetType));
      return "Applied through Steam.";
    }
    return payload.message || "Written to Steam's grid folder. Steam may need a moment to refresh.";
  }

  // A Perfect Hero carries the logo itself; Steam's own logo is hidden with a
  // transparent custom logo and comes back when the hero is restored.
  const transparentPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";
  const perfectHeroStorageKey = "ToolsForSteamPerfectHeroApps.v1";

  function perfectHeroApps() {
    try {
      const value = JSON.parse(window.localStorage.getItem(perfectHeroStorageKey) || "[]");
      return new Set(Array.isArray(value) ? value.map(Number).filter(Boolean) : []);
    } catch {
      return new Set();
    }
  }

  function savePerfectHeroApps(apps) {
    try { window.localStorage.setItem(perfectHeroStorageKey, JSON.stringify([...apps])); } catch {}
  }

  async function hideSteamLogo(appId) {
    try { await window.SteamClient?.Apps?.SetCustomArtworkForApp?.(Number(appId), transparentPng, "png", typeById("logo").steamAssetType); } catch {}
    const apps = perfectHeroApps();
    apps.add(Number(appId));
    savePerfectHeroApps(apps);
  }

  async function restoreArtwork(appId, typeId) {
    const type = typeById(typeId);
    try { await window.SteamClient?.Apps?.ClearCustomArtworkForApp?.(Number(appId), type.steamAssetType); } catch {}
    await api("/api/artwork/picker/restore", { appId, type: typeId }).catch(() => null);
    const apps = perfectHeroApps();
    if ((typeId === "hero" || typeId === "logo") && apps.has(Number(appId))) {
      // Bring Steam's logo back together with the hero it was hidden for.
      try { await window.SteamClient?.Apps?.ClearCustomArtworkForApp?.(Number(appId), typeById("logo").steamAssetType); } catch {}
      apps.delete(Number(appId));
      savePerfectHeroApps(apps);
    }
  }

  function ensureStyles(doc) {
    if (!doc) return;
    // Rewrite on every mount so a newer runtime replaces older rules.
    const style = doc.getElementById(styleId) || doc.createElement("style");
    style.id = styleId;
    style.textContent = `
      .tfs-art-page { padding: 48px 36px 120px; height: 100%; box-sizing: border-box; overflow-y: auto; color: #fff; scroll-padding: 84px 0 110px; }
      .tfs-art-page .tfs-art-card, .tfs-art-page .tfs-art-pill, .tfs-art-page .DialogButton { scroll-margin: 84px 0 110px; }
      .tfs-art-head { display: flex; align-items: baseline; gap: 14px; flex-wrap: wrap; }
      .tfs-art-head h2 { margin: 0; font-size: 26px; }
      .tfs-art-sub { opacity: .62; font-size: 15px; }
      .tfs-art-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin: 16px 0 10px; }
      .tfs-art-pill { padding: 9px 18px; border-radius: 999px; background: rgba(255,255,255,.08); color: rgba(255,255,255,.82); font-weight: 600; font-size: 15px; cursor: pointer; transition: background .15s, color .15s, transform .15s; }
      .tfs-art-pill.is-active { background: #fff; color: #0e141b; }
      .tfs-art-pill.tfs-art-focus { box-shadow: 0 0 0 2px #fff, 0 0 18px rgba(26,159,255,.55); transform: scale(1.04); }
      .tfs-art-glyph { display: inline-grid; place-items: center; min-width: 30px; height: 22px; padding: 0 6px; border-radius: 6px; background: rgba(255,255,255,.14); font-size: 12px; font-weight: 700; color: rgba(255,255,255,.75); }
      .tfs-art-note { opacity: .6; font-size: 13px; margin: 2px 0 8px; }
      .tfs-art-section { margin: 18px 0 8px; font-size: 13px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; color: rgba(255,255,255,.55); }
      .tfs-art-row { display: grid; gap: 14px; margin-bottom: 14px; }
      .tfs-art-card { position: relative; border-radius: 8px; overflow: hidden; background: rgba(255,255,255,.05); cursor: pointer; transition: transform .16s ease, box-shadow .16s ease; }
      .tfs-art-card.is-logo, .tfs-art-card.is-icon { background: repeating-conic-gradient(rgba(255,255,255,.07) 0 25%, rgba(255,255,255,.02) 0 50%) 0 0 / 22px 22px; }
      .tfs-art-card .tfs-art-media { width: 100%; height: 100%; object-fit: cover; display: block; }
      .tfs-art-card.is-logo .tfs-art-media, .tfs-art-card.is-icon .tfs-art-media { object-fit: contain; padding: 8%; box-sizing: border-box; }
      .tfs-art-card.tfs-art-focus { transform: scale(1.045); box-shadow: 0 0 0 3px #fff, 0 10px 28px rgba(0,0,0,.55); z-index: 2; }
      .tfs-art-card.is-applied::before { content: "Applied"; position: absolute; top: 8px; left: 8px; z-index: 2; padding: 3px 8px; border-radius: 6px; background: #5ba32b; font-size: 12px; font-weight: 700; }
      .tfs-art-badges { position: absolute; left: 0; right: 0; bottom: 0; display: flex; gap: 6px; flex-wrap: wrap; padding: 22px 8px 7px; background: linear-gradient(180deg, transparent, rgba(0,0,0,.78)); font-size: 11px; font-weight: 700; }
      .tfs-art-badge { padding: 2px 6px; border-radius: 5px; background: rgba(255,255,255,.16); }
      .tfs-art-badge.good { background: rgba(91,163,43,.9); } .tfs-art-badge.ok { background: rgba(214,152,24,.9); } .tfs-art-badge.low { background: rgba(196,64,64,.9); }
      .tfs-art-empty { padding: 26px; border-radius: 8px; background: rgba(255,255,255,.05); color: rgba(255,255,255,.65); }
      .tfs-art-preview { position: fixed; inset: 0; z-index: 50; display: grid; grid-template-rows: 1fr auto; gap: 18px; padding: 56px 64px 44px; background: rgba(6,9,13,.94); }
      .tfs-art-preview-media { min-height: 0; display: grid; place-items: center; }
      .tfs-art-preview-media img, .tfs-art-preview-media video { max-width: 100%; max-height: 100%; object-fit: contain; border-radius: 8px; box-shadow: 0 18px 60px rgba(0,0,0,.6); }
      .tfs-art-preview-info { display: flex; align-items: center; justify-content: space-between; gap: 20px; }
      .tfs-art-composer { grid-template-rows: none; grid-template-columns: minmax(0, 1fr) minmax(320px, 30%); align-items: center; gap: 28px; padding: 64px 40px 72px; }
      .tfs-art-composer-controls { display: flex; flex-direction: column; gap: 6px; max-height: 100%; overflow-y: auto; padding: 4px; box-sizing: border-box; }
      .tfs-art-composer-controls .DialogButton { width: 100% !important; min-width: 0 !important; margin: 0; }
      .tfs-art-composer-pair { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
      .tfs-art-composer-stage { position: relative; justify-self: center; width: 100%; max-width: 100%; min-height: 0; border-radius: 8px; overflow: hidden; background: #000; box-shadow: 0 0 0 1px rgba(255,255,255,.12), 0 18px 60px rgba(0,0,0,.6); }
      .tfs-art-composer-bg { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
      .tfs-art-composer-logo { position: absolute; transform: translate(-50%, -50%); height: auto; max-height: 90%; object-fit: contain; }
      .tfs-art-composer-sliders { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 4px 28px; margin: 8px 0; }
      .tfs-art-slider { min-width: 0; }
      .tfs-art-status { min-height: 20px; margin: 6px 0; font-size: 14px; color: rgba(255,255,255,.75); }
      .tfs-art-status.is-error { color: #ff9a9a; }
    `;
    if (!style.isConnected) doc.head.append(style);
  }

  function createPage(core) {
    const React = core.React;
    const h = React.createElement;
    const ui = core.ui || {};
    const Focusable = ui.Focusable || ((props) => h("div", props, props.children));

    // Steam's header and footer float over the page: keep the focused element
    // fully inside the visible area (CSS scroll-margin gives the room).
    function keepFocusVisible(event) {
      const target = event?.target;
      if (!target?.scrollIntoView || target.closest?.(".tfs-art-preview")) return;
      target.ownerDocument.defaultView.requestAnimationFrame(() => {
        try { target.scrollIntoView({ block: "nearest", inline: "nearest", behavior: "smooth" }); } catch {}
      });
    }

    function Button({ onClick, children, disabled, autoFocus }) {
      if (ui.DialogButton) return h(ui.DialogButton, { onClick, disabled, autoFocus, style: { width: "auto", minWidth: "180px", padding: "8px 18px" } }, children);
      return h("button", { type: "button", className: "DialogButton gpfocus Focusable", onClick, disabled, tabIndex: 0 }, children);
    }

    // Steam can report one press as both activate and click; act once.
    function once(handler) {
      let last = 0;
      return (...args) => {
        const now = Date.now();
        if (now - last < 400) return;
        last = now;
        handler(...args);
      };
    }

    function Pill({ active, label, onActivate }) {
      const run = React.useMemo(() => once(() => onActivate()), [onActivate]);
      return h(Focusable, {
        className: `tfs-art-pill${active ? " is-active" : ""}`,
        focusClassName: "tfs-art-focus",
        onActivate: run,
        onClick: run,
      }, label);
    }

    function Media({ asset, className, full }) {
      const source = full ? asset.url : (asset.thumbnailUrl || asset.url);
      if (asset.isAnimated && /\.(webm|mp4)(\?|$)/i.test(source)) {
        return h("video", { className, src: source, autoPlay: true, loop: true, muted: true, playsInline: true });
      }
      return h("img", {
        className,
        src: source,
        loading: "lazy",
        decoding: "async",
        alt: "",
        onError: (event) => { const card = event.target.closest(".tfs-art-card"); if (card) card.style.display = "none"; },
      });
    }

    function Card({ asset, typeId, applied, onApply, onPreview }) {
      const type = typeById(typeId);
      const quality = qualityOf(asset, typeId);
      const run = React.useMemo(() => once(() => onApply(asset)), [asset, onApply]);
      return h(Focusable, {
        className: `tfs-art-card is-${typeId === "logo" ? "logo" : typeId === "icon" ? "icon" : "image"}${applied ? " is-applied" : ""}`,
        focusClassName: "tfs-art-focus",
        style: { aspectRatio: type.ratio },
        onActivate: run,
        onClick: run,
        onSecondaryButton: () => onPreview(asset),
        onSecondaryActionDescription: "Preview",
        onOKActionDescription: "Apply",
      },
      h(Media, { asset, className: "tfs-art-media" }),
      h("div", { className: "tfs-art-badges" },
        asset.source === "steam" ? h("span", { className: "tfs-art-badge" }, asset.label || "Steam") : null,
        asset.width && asset.height ? h("span", { className: "tfs-art-badge" }, `${asset.width}×${asset.height}`) : null,
        h("span", { className: `tfs-art-badge ${quality.tone}` }, quality.label),
        asset.isAnimated ? h("span", { className: "tfs-art-badge" }, "Animated") : null,
        asset.author ? h("span", { className: "tfs-art-badge" }, asset.author) : null));
    }

    function Grid({ assets, typeId, appliedKey, onApply, onPreview }) {
      const type = typeById(typeId);
      const rows = chunk(assets, type.columns);
      return h(Focusable, { "flow-children": "column" },
        ...rows.map((row, rowIndex) => h(Focusable, {
          key: `row-${rowIndex}`,
          "flow-children": "row",
          navEntryPreferPosition: 2,
          className: "tfs-art-row",
          style: { gridTemplateColumns: `repeat(${type.columns}, minmax(0, 1fr))` },
        }, ...row.map((asset) => h(Card, {
          key: `${asset.source}:${asset.id}`,
          asset,
          typeId,
          applied: appliedKey === `${typeId}:${asset.url}`,
          onApply,
          onPreview,
        })))));
    }

    function Filters({ typeId, filter, onChange }) {
      const value = normalizeFilter(typeId, filter);
      const toggleStyle = (style) => {
        const styles = value.styles.includes(style) ? value.styles.filter((item) => item !== style) : [...value.styles, style];
        onChange({ ...value, styles });
      };
      return h(Focusable, { "flow-children": "row", navEntryPreferPosition: 2, className: "tfs-art-bar" },
        h("span", { className: "tfs-art-sub" }, "Style"),
        ...(styleOptions[typeId] || []).map(([id, label]) => h(Pill, { key: id, active: value.styles.includes(id), label, onActivate: () => toggleStyle(id) })),
        h("span", { className: "tfs-art-sub", style: { marginLeft: "12px" } }, "Motion"),
        ...[["all", "All"], ["static", "Static"], ["animated", "Animated"]].map(([id, label]) =>
          h(Pill, { key: `anim-${id}`, active: value.animation === id, label, onActivate: () => onChange({ ...value, animation: id }) })),
        typeId === "grid_p" ? h(Pill, { key: "square", active: value.square, label: "Square", onActivate: () => onChange({ ...value, square: !value.square }) }) : null,
        h(Pill, { key: "nsfw", active: value.nsfw, label: "NSFW", onActivate: () => onChange({ ...value, nsfw: !value.nsfw }) }),
        h(Pill, { key: "humor", active: value.humor, label: "Humor", onActivate: () => onChange({ ...value, humor: !value.humor }) }));
    }

    function MatchPanel({ game, onChosen, onClose }) {
      const [term, setTerm] = React.useState(game.steamGridDbGameName || game.title || "");
      const [results, setResults] = React.useState(null);
      const [busy, setBusy] = React.useState(false);
      const search = async () => {
        setBusy(true);
        const { ok, payload } = await api(`/api/artwork/search?term=${encodeURIComponent(term)}`).catch(() => ({ ok: false }));
        setResults(ok && Array.isArray(payload) ? payload : []);
        setBusy(false);
      };
      return h(Focusable, { "flow-children": "column", style: { margin: "8px 0 16px", padding: "14px 16px", borderRadius: "8px", background: "rgba(255,255,255,.05)" } },
        ui.TextField
          ? h(ui.TextField, { label: "Search SteamGridDB", value: term, onChange: (event) => setTerm(String(event?.target?.value ?? "")) })
          : null,
        h(Focusable, { "flow-children": "row", navEntryPreferPosition: 2, className: "tfs-art-bar" },
          h(Button, { onClick: search, disabled: busy || term.trim().length < 2 }, busy ? "Searching…" : "Search"),
          game.steamGridDbMatch === "manual" ? h(Button, { onClick: () => onChosen(0, "") }, "Back to automatic") : null,
          h(Button, { onClick: onClose }, "Close")),
        ...(results || []).slice(0, 10).map((item) =>
          h(Button, { key: `match-${item.id}`, onClick: () => onChosen(item.id, item.name) }, `${item.name}${item.verified ? "  ✓" : ""}`)),
        results && !results.length ? h("div", { className: "tfs-art-note" }, "Nothing found.") : null);
    }

    function Preview({ asset, typeId, onApply, onClose }) {
      const quality = qualityOf(asset, typeId);
      return h("div", { className: "tfs-art-preview" },
        h("div", { className: "tfs-art-preview-media" }, h(Media, { asset, full: true })),
        h("div", { className: "tfs-art-preview-info" },
          h("div", null,
            h("div", { style: { fontSize: "18px", fontWeight: 700 } }, asset.label || (asset.source === "steam" ? "Steam" : "SteamGridDB")),
            h("div", { className: "tfs-art-sub" }, [
              asset.width && asset.height ? `${asset.width}×${asset.height}` : "",
              quality.label,
              asset.style && asset.style !== "official" ? asset.style : "",
              asset.author ? `by ${asset.author}` : "",
            ].filter(Boolean).join(" · "))),
          h(Focusable, { "flow-children": "row", className: "tfs-art-bar", style: { margin: 0 } },
            h(Button, { onClick: () => onApply(asset), autoFocus: true }, "Apply"),
            h(Button, { onClick: onClose }, "Close"))));
    }

    function Slider({ label, value, min, max, step = 1, onChange, suffix = " %" }) {
      if (ui.SliderField) {
        return h("div", { className: "tfs-art-slider" }, h(ui.SliderField, {
          label, value: Number(value), min, max, step, showValue: true, editableValue: false, valueSuffix: suffix,
          onChange: (next) => onChange(Number(next)),
        }));
      }
      return h(Button, { onClick: () => onChange(Number(value) + step > max ? min : Number(value) + step) }, `${label}: ${value}${suffix}`);
    }

    /**
     * Perfect Hero (game page header) / Perfect Banner (wide capsule): a
     * logo baked into a background. The preview is CSS; the backend renders
     * the final PNG at the background's resolution.
     */
    function Composer({ kind, appId, game, onDone, onClose }) {
      const [backgrounds, setBackgrounds] = React.useState(() => [...(game?.official?.hero || [])]);
      const [logos, setLogos] = React.useState([]);
      const [backgroundIndex, setBackgroundIndex] = React.useState(0);
      const [logoIndex, setLogoIndex] = React.useState(0);
      const [layout, setLayout] = React.useState(() => ({ ...defaultComposerLayout(kind) }));
      const [busy, setBusy] = React.useState(false);
      const [message, setMessage] = React.useState("");

      React.useEffect(() => {
        let alive = true;
        if (!game?.steamGridDbGameId) return undefined;
        const load = async (type, filter) => {
          const { ok, payload } = await api(buildAssetQuery(game.steamGridDbGameId, type, filter, 0)).catch(() => ({ ok: false }));
          return ok ? (payload?.items || []).filter((item) => !item.isAnimated) : [];
        };
        Promise.all([load("hero", { animation: "static" }), load("logo", { animation: "static" })]).then(([heroes, foundLogos]) => {
          if (!alive) return;
          setBackgrounds((current) => [...current, ...sortAssets(heroes, "hero").filter((item) => !current.some((existing) => existing.url === item.url))]);
          // Transparent SteamGridDB logos first; official ones (if any) after.
          setLogos([...sortAssets(foundLogos, "logo"), ...(game?.official?.logo || [])]);
        });
        return () => { alive = false; };
      }, [game?.steamGridDbGameId]);

      const background = backgrounds[backgroundIndex] || null;
      const logo = logos[logoIndex] || null;
      const set = (key) => (value) => setLayout((current) => ({ ...current, [key]: value }));
      const cycle = (setter, length, direction) => setter((index) => (length ? (index + direction + length) % length : 0));

      const create = async () => {
        if (!background || !logo) return;
        setBusy(true);
        setMessage("Creating…");
        try {
          const { ok, payload } = await api("/api/artwork/picker/compose", { appId, kind, backgroundUrl: background.url, logoUrl: logo.url, layout });
          if (!ok || !payload?.success) throw new Error(payload?.message || "The artwork could not be created.");
          await window.SteamClient?.Apps?.SetCustomArtworkForApp?.(Number(payload.appId), payload.base64Data, "png", Number(payload.steamAssetType));
          if (kind === "hero") await hideSteamLogo(appId);
          onDone(payload.message || "Created.");
        } catch (error) {
          setMessage(String(error?.message || error));
        } finally {
          setBusy(false);
        }
      };

      const shadowPx = (Number(layout.shadow) || 0) / 100;
      return h("div", { className: "tfs-art-preview tfs-art-composer" },
        h("div", { className: "tfs-art-composer-stage", style: { aspectRatio: kind === "hero" ? "96 / 31" : "92 / 43" } },
          background ? h("img", { className: "tfs-art-composer-bg", src: background.url, alt: "", style: { objectPosition: `center ${layout.backgroundY}%` } }) : null,
          logo ? h("img", {
            className: "tfs-art-composer-logo",
            src: logo.url,
            alt: "",
            style: {
              left: `${layout.logoX}%`,
              top: `${layout.logoY}%`,
              width: `${layout.logoWidth}%`,
              opacity: layout.opacity / 100,
              filter: shadowPx > 0 ? `drop-shadow(0 ${Math.round(4 * shadowPx + 1)}px ${Math.round(18 * shadowPx + 4)}px rgba(0,0,0,${Math.min(0.95, 0.25 + shadowPx * 0.8)}))` : "none",
            },
          }) : null,
          !background || !logo ? h("div", { className: "tfs-art-empty", style: { position: "absolute", inset: "auto 16px 16px 16px" } },
            !background ? "No background found yet." : "No logo found. SteamGridDB logos appear once the game is matched.") : null),
        // One column of controls: up/down walks through them in order, and
        // left/right only ever changes the focused slider or picks a button
        // in the same row (sliders use left/right themselves).
        h(Focusable, { "flow-children": "column", className: "tfs-art-composer-controls" },
          h(Focusable, { "flow-children": "row", className: "tfs-art-composer-pair" },
            h(Button, { onClick: () => cycle(setBackgroundIndex, backgrounds.length, -1), disabled: backgrounds.length < 2, autoFocus: true }, "◀ Background"),
            h(Button, { onClick: () => cycle(setBackgroundIndex, backgrounds.length, 1), disabled: backgrounds.length < 2 }, "Background ▶")),
          h(Focusable, { "flow-children": "row", className: "tfs-art-composer-pair" },
            h(Button, { onClick: () => cycle(setLogoIndex, logos.length, -1), disabled: logos.length < 2 }, "◀ Logo"),
            h(Button, { onClick: () => cycle(setLogoIndex, logos.length, 1), disabled: logos.length < 2 }, "Logo ▶")),
          h("div", { className: "tfs-art-sub", style: { margin: "2px 0 4px" } }, `Background ${backgrounds.length ? backgroundIndex + 1 : 0}/${backgrounds.length} · Logo ${logos.length ? logoIndex + 1 : 0}/${logos.length}`),
          h(Slider, { label: "Logo horizontal", value: layout.logoX, min: 0, max: 100, step: 1, onChange: set("logoX") }),
          h(Slider, { label: "Logo vertical", value: layout.logoY, min: 0, max: 100, step: 1, onChange: set("logoY") }),
          h(Slider, { label: "Logo size", value: layout.logoWidth, min: 10, max: 80, step: 1, onChange: set("logoWidth") }),
          h(Slider, { label: "Logo opacity", value: layout.opacity, min: 20, max: 100, step: 5, onChange: set("opacity") }),
          h(Slider, { label: "Shadow", value: layout.shadow, min: 0, max: 100, step: 5, onChange: set("shadow") }),
          h(Slider, { label: "Background position", value: layout.backgroundY, min: 0, max: 100, step: 5, onChange: set("backgroundY") }),
          h(Button, { onClick: create, disabled: busy || !background || !logo }, busy ? "Creating…" : kind === "hero" ? "Create Perfect Hero" : "Create Perfect Banner"),
          h(Focusable, { "flow-children": "row", className: "tfs-art-composer-pair" },
            h(Button, { onClick: () => setLayout({ ...defaultComposerLayout(kind) }) }, "Reset layout"),
            h(Button, { onClick: onClose }, "Close")),
          message ? h("div", { className: "tfs-art-sub" }, message) : null));
    }

    /**
     * Library tools (/tfs/artworks): fills artwork slots of non-Steam games
     * that have none yet, with the best static SteamGridDB match. Existing
     * artwork is never replaced.
     */
    function LibraryTools() {
      const rootRef = React.useRef(null);
      const [slots, setSlots] = React.useState(() => ({ grid_p: true, grid_l: true, hero: true, logo: true }));
      const [scan, setScan] = React.useState(null);
      const [progress, setProgress] = React.useState(null);
      const cancelRef = React.useRef(false);

      React.useEffect(() => { ensureStyles(rootRef.current?.ownerDocument); }, []);

      const runScan = React.useCallback(() => {
        const shortcuts = libraryShortcuts();
        const missing = shortcuts.map((app) => ({
          appId: app.appid,
          title: app.display_name || `App ${app.appid}`,
          slots: Object.keys(slots).filter((typeId) => slots[typeId] && !customImageUrl(app.appid, typeId)),
        })).filter((entry) => entry.slots.length);
        setScan({ shortcuts: shortcuts.length, missing });
      }, [slots]);

      React.useEffect(() => { runScan(); }, [runScan]);

      const fill = async () => {
        if (!scan?.missing?.length) return;
        cancelRef.current = false;
        const total = scan.missing.reduce((sum, entry) => sum + entry.slots.length, 0);
        let done = 0;
        let applied = 0;
        let skipped = 0;
        setProgress({ done, total, applied, skipped, current: "" });
        for (const entry of scan.missing) {
          if (cancelRef.current) break;
          setProgress({ done, total, applied, skipped, current: entry.title });
          const { ok, payload: game } = await api(`/api/artwork/picker/game?appId=${entry.appId}&title=${encodeURIComponent(entry.title)}&shortcut=1`).catch(() => ({ ok: false }));
          for (const typeId of entry.slots) {
            if (cancelRef.current) break;
            let asset = ok ? (game?.official?.[typeId] || [])[0] : null;
            if (!asset && ok && game?.steamGridDbGameId) {
              const { ok: found, payload } = await api(buildAssetQuery(game.steamGridDbGameId, typeId, { animation: "static" }, 0)).catch(() => ({ ok: false }));
              asset = found ? sortAssets(payload?.items || [], typeId)[0] : null;
            }
            if (asset) {
              try {
                await applyArtwork(entry.appId, typeId, asset);
                applied += 1;
              } catch {
                skipped += 1;
              }
            } else {
              skipped += 1;
            }
            done += 1;
            setProgress({ done, total, applied, skipped, current: entry.title });
          }
        }
        setProgress((current) => current && { ...current, current: cancelRef.current ? "Stopped." : "Finished." });
        runScan();
      };

      const running = progress && progress.current !== "Finished." && progress.current !== "Stopped.";
      return h("div", { ref: rootRef, className: "tfs-art-page", onFocusCapture: keepFocusVisible },
        h("div", { className: "tfs-art-head" }, h("h2", null, "TFS Artworks"), h("div", { className: "tfs-art-sub" }, "Library tools")),
        h("div", { className: "tfs-art-note", style: { maxWidth: "980px", marginTop: "8px" } },
          "Fills empty artwork slots of non-Steam games with the best match from Steam's store or SteamGridDB. Artwork that is already set is never replaced."),
        h(Focusable, { "flow-children": "column" },
          h(Focusable, { "flow-children": "row", navEntryPreferPosition: 2, className: "tfs-art-bar" },
            h("span", { className: "tfs-art-sub" }, "Slots"),
            ...artworkTypes.filter((type) => type.id !== "icon").map((type) =>
              h(Pill, { key: type.id, active: slots[type.id], label: type.label, onActivate: () => setSlots((current) => ({ ...current, [type.id]: !current[type.id] })) }))),
          h("div", { className: "tfs-art-status" },
            scan ? `${scan.shortcuts} non-Steam game${scan.shortcuts === 1 ? "" : "s"}, ${scan.missing.length} with empty slots (${scan.missing.reduce((sum, entry) => sum + entry.slots.length, 0)} images).` : "Scanning…"),
          progress
            ? h("div", null,
              h("div", { style: { height: "8px", borderRadius: "4px", background: "rgba(255,255,255,.12)", overflow: "hidden", maxWidth: "720px" } },
                h("div", { style: { height: "100%", width: `${progress.total ? Math.round((progress.done / progress.total) * 100) : 0}%`, background: "#1a9fff", transition: "width .3s" } })),
              h("div", { className: "tfs-art-status" }, `${progress.done}/${progress.total} · ${progress.applied} applied · ${progress.skipped} without a match · ${progress.current}`))
            : null,
          h(Focusable, { "flow-children": "row", navEntryPreferPosition: 2, className: "tfs-art-bar" },
            running
              ? h(Button, { onClick: () => { cancelRef.current = true; } }, "Stop")
              : h(Button, { onClick: fill, disabled: !scan?.missing?.length }, "Fill missing artwork"),
            h(Button, { onClick: runScan, disabled: Boolean(running) }, "Scan again"))));
    }

    const TfsArtworksPage = function TfsArtworksPage(props) {
      const appId = Number(props?.match?.params?.appId || 0) || 0;
      const overview = appOverview(appId);
      const title = overview?.display_name || `App ${appId}`;
      const isShortcut = Number(overview?.app_type) === shortcutAppType;
      const rootRef = React.useRef(null);
      const [typeId, setTypeId] = React.useState("grid_p");
      const [source, setSource] = React.useState("all");
      const [game, setGame] = React.useState(null);
      const [filters, setFilters] = React.useState(loadFilters);
      const [showFilters, setShowFilters] = React.useState(false);
      const [showMatch, setShowMatch] = React.useState(false);
      const [assets, setAssets] = React.useState([]);
      const [page, setPage] = React.useState(0);
      const [hasMore, setHasMore] = React.useState(false);
      const [loading, setLoading] = React.useState(false);
      const [status, setStatus] = React.useState({ text: "", error: false });
      const [preview, setPreview] = React.useState(null);
      const [composer, setComposer] = React.useState(null);
      const [appliedKey, setAppliedKey] = React.useState("");
      const [customUrl, setCustomUrl] = React.useState("");
      const filter = normalizeFilter(typeId, filters[typeId]);
      const filterKey = JSON.stringify(filter);

      React.useEffect(() => { ensureStyles(rootRef.current?.ownerDocument); }, []);

      const loadGame = React.useCallback(async () => {
        setGame(null);
        const query = `/api/artwork/picker/game?appId=${appId}&title=${encodeURIComponent(title)}&shortcut=${isShortcut ? 1 : 0}`;
        const { ok, payload } = await api(query).catch(() => ({ ok: false }));
        if (ok && payload) setGame(payload);
        else setStatus({ text: payload?.message || "TFS Artworks is not available. Turn it on in the Plugin Activator.", error: true });
      }, [appId]);

      React.useEffect(() => { void loadGame(); }, [loadGame]);
      React.useEffect(() => { setCustomUrl(customImageUrl(appId, typeId)); }, [appId, typeId, appliedKey]);

      // SteamGridDB results for the current tab, filters and match.
      React.useEffect(() => {
        let alive = true;
        setAssets([]);
        setPage(0);
        setHasMore(false);
        if (!game?.steamGridDbGameId || source === "steam") return undefined;
        setLoading(true);
        api(buildAssetQuery(game.steamGridDbGameId, typeId, filter, 0)).then(({ ok, payload }) => {
          if (!alive) return;
          const items = ok ? payload?.items || [] : [];
          setAssets(items);
          setHasMore(items.length >= 24);
        }).catch(() => {}).finally(() => alive && setLoading(false));
        return () => { alive = false; };
      }, [game?.steamGridDbGameId, typeId, filterKey, source]);

      const loadMore = async () => {
        if (!game?.steamGridDbGameId) return;
        setLoading(true);
        const next = page + 1;
        const { ok, payload } = await api(buildAssetQuery(game.steamGridDbGameId, typeId, filter, next)).catch(() => ({ ok: false }));
        const items = ok ? payload?.items || [] : [];
        setAssets((current) => [...current, ...items.filter((item) => !current.some((existing) => existing.url === item.url))]);
        setPage(next);
        setHasMore(items.length >= 24);
        setLoading(false);
      };

      const switchType = React.useCallback((direction) => {
        setPreview(null);
        setTypeId((current) => nextTypeId(current, direction));
      }, []);

      const previewRef = React.useRef(null);
      previewRef.current = preview || composer;

      // LB/RB switch tabs, B closes the preview first (instead of leaving).
      React.useEffect(() => {
        const doc = rootRef.current?.ownerDocument || bigPictureWindow()?.document;
        if (!doc) return undefined;
        const onButton = (event) => {
          const button = Number(event?.detail?.button);
          if (event?.detail?.is_repeat) return;
          if (button === gamepadButton.bumperLeft) switchType(-1);
          else if (button === gamepadButton.bumperRight) switchType(1);
          else if (button === gamepadButton.cancel && previewRef.current) {
            event.stopPropagation();
            event.preventDefault?.();
            setPreview(null);
            setComposer(null);
          }
        };
        doc.addEventListener("vgp_onbuttondown", onButton, true);
        return () => doc.removeEventListener("vgp_onbuttondown", onButton, true);
      }, [switchType]);
      const apply = async (asset) => {
        setStatus({ text: "Applying…", error: false });
        try {
          const message = await applyArtwork(appId, typeId, asset);
          setAppliedKey(`${typeId}:${asset.url}`);
          setStatus({ text: `${typeById(typeId).label}: ${message}`, error: false });
          setPreview(null);
        } catch (error) {
          setStatus({ text: String(error?.message || error), error: true });
        }
      };

      const restore = async () => {
        setStatus({ text: "Restoring Steam's artwork…", error: false });
        await restoreArtwork(appId, typeId);
        setAppliedKey("");
        setCustomUrl("");
        setStatus({ text: `${typeById(typeId).label}: Steam's own artwork is back.`, error: false });
      };

      const chooseMatch = async (gameId, name) => {
        await api("/api/artwork/picker/match", { appId, gameId, name }).catch(() => null);
        setShowMatch(false);
        await loadGame();
      };

      const updateFilter = (next) => {
        const updated = { ...filters, [typeId]: normalizeFilter(typeId, next) };
        setFilters(updated);
        saveFilters(updated);
      };

      const official = game?.official?.[typeId] || [];
      const steamGridDb = sortAssets(assets, typeId);
      const matchText = !game
        ? "Looking up the game…"
        : game.steamGridDbGameId
          ? `SteamGridDB: ${game.steamGridDbGameName || game.steamGridDbGameId}${game.steamGridDbMatch === "steam" ? " (exact)" : game.steamGridDbMatch === "manual" ? " (chosen)" : " (by title)"}`
          : "No SteamGridDB match yet";

      // While the preview or the composer is open, only it is rendered, so
      // Steam's focus navigation cannot wander to the page underneath.
      if (preview || composer) {
        return h("div", { ref: rootRef, className: "tfs-art-page" },
          preview ? h(Preview, { asset: preview, typeId, onApply: apply, onClose: () => setPreview(null) }) : null,
          composer ? h(Composer, {
            kind: composer,
            appId,
            game,
            onClose: () => setComposer(null),
            onDone: (text) => {
              setComposer(null);
              setAppliedKey(`${composer}:perfect`);
              setStatus({ text, error: false });
            },
          }) : null);
      }

      return h("div", { ref: rootRef, className: "tfs-art-page", onFocusCapture: keepFocusVisible },
        h("div", { className: "tfs-art-head" },
          h("h2", null, "TFS Artworks"),
          h("div", { className: "tfs-art-sub" }, title),
          h("div", { className: "tfs-art-sub" }, `· ${matchText}`)),
        h(Focusable, { "flow-children": "column" },
          h(Focusable, { "flow-children": "row", navEntryPreferPosition: 2, className: "tfs-art-bar" },
            h("span", { className: "tfs-art-glyph" }, "LB"),
            ...artworkTypes.map((type) => h(Pill, { key: type.id, active: type.id === typeId, label: type.label, onActivate: () => { setPreview(null); setTypeId(type.id); } })),
            h("span", { className: "tfs-art-glyph" }, "RB")),
          h(Focusable, { "flow-children": "row", navEntryPreferPosition: 2, className: "tfs-art-bar" },
            ...[["all", "All sources"], ["steamgriddb", "SteamGridDB"], ["steam", "Steam official"]].map(([id, label]) =>
              h(Pill, { key: `src-${id}`, active: source === id, label, onActivate: () => setSource(id) })),
            h(Pill, { key: "filters", active: showFilters, label: `Filters: ${filterSummary(typeId, filter)}`, onActivate: () => setShowFilters(!showFilters) }),
            h(Pill, { key: "match", active: showMatch, label: "Change match", onActivate: () => setShowMatch(!showMatch) }),
            typeId === "hero" || typeId === "grid_l"
              ? h(Pill, { key: "perfect", active: false, label: typeId === "hero" ? "Perfect Hero…" : "Perfect Banner…", onActivate: () => setComposer(typeId) })
              : null,
            h(Pill, { key: "restore", active: false, label: "Restore Steam original", onActivate: restore }),
            h(Pill, { key: "tools", active: false, label: "Library tools", onActivate: () => window.__tfs?.routes?.navigate?.("/tfs/artworks") })),
          showFilters && source !== "steam" ? h(Filters, { typeId, filter, onChange: updateFilter }) : null,
          showMatch && game ? h(MatchPanel, { game, onChosen: chooseMatch, onClose: () => setShowMatch(false) }) : null,
          h("div", { className: `tfs-art-status${status.error ? " is-error" : ""}` }, status.text),
          customUrl ? h("div", { className: "tfs-art-note" }, "This game uses custom artwork for this slot. \"Restore Steam original\" removes it.") : null,
          !game?.steamGridDbKeyConfigured && source !== "steam"
            ? h("div", { className: "tfs-art-note" }, "Tip: your own SteamGridDB API key (TFS Artworks settings in Quick Access) avoids the shared key's limits.")
            : null,
          source !== "steamgriddb" && official.length
            ? [
              h("div", { key: "steam-head", className: "tfs-art-section" }, "Steam official"),
              h(Grid, { key: "steam-grid", assets: official, typeId, appliedKey, onApply: apply, onPreview: setPreview }),
            ]
            : null,
          source !== "steam"
            ? [
              h("div", { key: "sgdb-head", className: "tfs-art-section" }, `SteamGridDB${steamGridDb.length ? ` · ${steamGridDb.length}, best quality first` : ""}`),
              steamGridDb.length
                ? h(Grid, { key: "sgdb-grid", assets: steamGridDb, typeId, appliedKey, onApply: apply, onPreview: setPreview })
                : h("div", { key: "sgdb-empty", className: "tfs-art-empty" },
                  loading ? "Loading…" : game && !game.steamGridDbGameId ? "No SteamGridDB game found. Use \"Change match\"." : "No artwork with these filters."),
              hasMore ? h("div", { key: "more", style: { marginTop: "10px" } }, h(Button, { onClick: loadMore, disabled: loading }, loading ? "Loading…" : "More results")) : null,
            ]
            : null,
          source === "steam" && !official.length ? h("div", { className: "tfs-art-empty" }, game ? "Steam has no official artwork of this type for this game." : "Loading…") : null));
    };
    TfsArtworksPage.LibraryTools = LibraryTools;
    return TfsArtworksPage;
  }

  async function registerRoute() {
    window.clearTimeout(state.routeTimer);
    if (state.destroyed || state.routeRemover) return;
    const core = window.__tfs;
    if (!core?.routes?.add) {
      state.routeTimer = window.setTimeout(registerRoute, 2000);
      return;
    }
    try {
      await core.whenReady;
      if (state.destroyed || state.routeRemover || !core.React) return;
      if (core.routes.has?.(route)) core.routes.remove(route);
      const page = createPage(core);
      if (core.routes.has?.(toolsRoute)) core.routes.remove(toolsRoute);
      state.toolsRemover = core.routes.add(toolsRoute, page.LibraryTools, { name: "TFS Artworks tools", exact: true });
      state.routeRemover = core.routes.add(route, page, { name: "TFS Artworks", exact: true });
      state.core = core;
    } catch (error) {
      console.warn("[TFS Artworks] route failed", error);
      state.routeTimer = window.setTimeout(registerRoute, 10000);
    }
  }

  // tfs-core can be replaced by a newer copy (TFS update/restart). Routes added
  // to the old copy would not re-render the new router, so register again.
  function watchCore() {
    window.clearInterval(state.coreWatch);
    state.coreWatch = window.setInterval(() => {
      if (state.destroyed || !state.core || window.__tfs === state.core || !window.__tfs?.routes?.add) return;
      try { state.routeRemover?.(); } catch {}
      try { state.toolsRemover?.(); } catch {}
      state.routeRemover = null;
      state.toolsRemover = null;
      state.core = null;
      void registerRoute();
    }, 3000);
  }

  function openEditor(appId) {
    const id = Number(appId) || 0;
    const routes = window.__tfs?.routes;
    if (!id || !routes?.navigate || !state.routeRemover) return false;
    routes.navigate(`/tfs/artworks/${id}`);
    return true;
  }

  function destroy() {
    state.destroyed = true;
    window.clearTimeout(state.routeTimer);
    window.clearInterval(state.coreWatch);
    try { state.routeRemover?.(); } catch {}
    try { state.toolsRemover?.(); } catch {}
    state.toolsRemover = null;
    state.routeRemover = null;
  }

  window.__tfsArtworks = {
    version,
    refresh: () => { if (!state.routeRemover) void registerRoute(); },
    destroy,
    openEditor,
    hasEditor: () => Boolean(state.routeRemover),
    internals,
  };
  void registerRoute();
  watchCore();
  return "injected";
})();
