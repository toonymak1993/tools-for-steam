// TFS Metadata editor (SharedJSContext).
//
// A real Big Picture route (/tfs/metadata/:appId) built from Steam's own
// components (window.__tfs.ui), so focus navigation, footer hints and the back
// button stay native. Per game it can:
//  - replace description, developers, publishers, genres, release date and
//    rating by hand (blank = automatic, the edits always win);
//  - pick or reset the Steam store page the automatic metadata comes from;
//  - pick an IGN game page instead (IGN is the fallback without a Steam page;
//    IGN text is translated into the Windows language);
//  - refresh the automatic metadata.
// OmniLibrary games keep OmniLibrary's achievement handling; this page only
// edits their game info.
//
// Inspired by Playhub Metadata's manual editing; independent code.
(() => {
  const version = 2;
  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const editorRoute = "/tfs/metadata/:appId";
  const channelName = "ToolsForSteamOmniLibraryStores";
  const nativeMetadataMessageType = "native-metadata";

  const previous = window.__tfsMetadata;
  if (previous?.version === version) return;
  try { previous?.destroy?.(); } catch {}

  const state = {
    routeRemover: null,
    routeTimer: 0,
    enabled: false,
    enabledCheckedAt: Date.now(),
    destroyed: false,
  };

  // -----------------------------------------------------------------------
  // Pure helpers (tests reach them through window.__tfsMetadataTestHooks)
  // -----------------------------------------------------------------------

  /** "A, B ,, a" -> ["A", "B"]: trimmed, de-duplicated, at most 8. */
  function splitList(value) {
    const seen = new Set();
    const items = [];
    for (const part of String(value || "").split(/[,;\n]/)) {
      const item = part.trim();
      const key = item.toLowerCase();
      if (!item || seen.has(key)) continue;
      seen.add(key);
      items.push(item);
      if (items.length >= 8) break;
    }
    return items;
  }

  function joinList(values) {
    return Array.isArray(values) ? values.join(", ") : "";
  }

  /** Editor draft (all strings) from saved edits. */
  function draftFromEdits(edits) {
    return {
      description: String(edits?.description || ""),
      developers: joinList(edits?.developers),
      publishers: joinList(edits?.publishers),
      genres: joinList(edits?.genres),
      releaseDate: String(edits?.releaseDate || ""),
      rating: Number.isInteger(edits?.rating) ? edits.rating : null,
    };
  }

  /** Request body from the draft; blank fields become null (= automatic). */
  function editsFromDraft(draft) {
    const text = (value) => String(value || "").trim() || null;
    const list = (value) => {
      const items = splitList(value);
      return items.length ? items : null;
    };
    return {
      description: text(draft.description),
      developers: list(draft.developers),
      publishers: list(draft.publishers),
      genres: list(draft.genres),
      releaseDate: text(draft.releaseDate),
      rating: Number.isInteger(draft.rating) ? Math.max(0, Math.min(100, draft.rating)) : null,
    };
  }

  function sourceText(metadata) {
    if (!metadata) return "";
    const steamAppId = Number(metadata.sourceSteamAppId || 0);
    if (metadata.matchMode === "manual" && steamAppId) return `Steam store page chosen by you (app ${steamAppId})`;
    if (metadata.sourceLabel === "IGN") return "Taken from IGN (no Steam store page in use).";
    if (steamAppId) return `Found on the Steam store automatically (app ${steamAppId})`;
    if (metadata.sourceLabel && metadata.sourceLabel !== "tfs-shortcut") return `Source: ${metadata.sourceLabel}`;
    return "No Steam store match yet. Search below or fill in the fields by hand.";
  }

  function metadataQuery(appId, title) {
    const query = new URLSearchParams();
    if (title) query.set("title", title);
    const text = query.toString();
    return text ? `?${text}` : "";
  }

  window.__tfsMetadataTestHooks = { splitList, joinList, draftFromEdits, editsFromDraft, sourceText, metadataQuery };

  // -----------------------------------------------------------------------
  // Backend
  // -----------------------------------------------------------------------

  function apiBase() {
    const base = window.__steamLoaderApiBase || apiBaseFallback;
    return base.endsWith("/") ? base : `${base}/`;
  }

  async function api(path, method = "GET", body = undefined) {
    const init = { method, cache: "no-store" };
    if (body !== undefined) {
      init.headers = { "Content-Type": "application/json" };
      init.body = JSON.stringify(body);
    }
    const response = await fetch(`${apiBase()}${String(path).replace(/^\//, "")}`, init);
    const payload = await response.json().catch(() => null);
    return { ok: response.ok, status: response.status, payload };
  }

  async function refreshEnabled() {
    try {
      const { ok, payload } = await api("/api/metadata/state");
      state.enabled = ok && payload?.enabled === true;
    } catch {
      state.enabled = false;
    }
    return state.enabled;
  }

  function appOverview(appId) {
    try { return window.appStore?.GetAppOverviewByAppID?.(appId) || null; } catch { return null; }
  }

  // Open game pages (Big Picture window) re-apply a snapshot they receive here.
  function broadcastSnapshot(snapshot) {
    if (!snapshot || typeof window.BroadcastChannel !== "function") return;
    try {
      const channel = new window.BroadcastChannel(channelName);
      channel.postMessage({ type: nativeMetadataMessageType, snapshot });
      channel.close();
    } catch {}
  }

  // -----------------------------------------------------------------------
  // Editor page
  // -----------------------------------------------------------------------

  const ratingOptions = [[null, "Automatic"]].concat(
    Array.from({ length: 21 }, (_, index) => {
      const value = 100 - index * 5;
      return [value, `${value} / 100`];
    }));

  /** Steam's labelled dropdown field; see trailer-core.js for why. */
  function resolveDropdownItem(core) {
    const webpack = core.webpack;
    const dropdown = core.ui?.Dropdown;
    const isField = (value) => {
      const source = typeof value === "function" ? webpack?.getSourceText?.(value) || "" : "";
      return source.includes("dropDownControlRef") && source.includes("explainerTitle");
    };
    try {
      for (const module of webpack?.getModules?.() || []) {
        const exportsObject = module?.exports ?? module;
        if (!exportsObject || typeof exportsObject !== "object") continue;
        const values = Object.values(exportsObject);
        if (dropdown && !values.includes(dropdown)) continue;
        const match = values.find(isField);
        if (match) return match;
      }
    } catch {}
    const fallback = core.ui?.DropdownItem;
    return fallback && isField(fallback) ? fallback : null;
  }

  function createEditor(core) {
    const React = core.React;
    const h = React.createElement;
    const ui = core.ui || {};
    const DropdownItem = resolveDropdownItem(core);

    function Row({ children }) {
      return h("div", { className: "tfs-metadata-row", style: { minWidth: 0 } }, children);
    }

    function Button({ onClick, children, disabled }) {
      if (ui.DialogButton) return h(ui.DialogButton, { onClick, disabled, style: { marginTop: "6px" } }, children);
      return h("button", { type: "button", className: "DialogButton gpfocus Focusable", onClick, disabled, tabIndex: 0 }, children);
    }

    function Heading({ children }) {
      return h("div", {
        style: { margin: "22px 0 6px", fontSize: "13px", fontWeight: 700, letterSpacing: ".08em", textTransform: "uppercase", color: "rgba(255,255,255,.55)" },
      }, children);
    }

    function Note({ children }) {
      return h("div", { style: { opacity: 0.65, fontSize: "14px", margin: "2px 0 8px", lineHeight: 1.4 } }, children);
    }

    function Text({ label, value, automatic, onChange }) {
      const hint = automatic ? `Automatic: ${automatic}` : "Automatic: empty";
      if (ui.TextField) {
        return h(Row, null, h(ui.TextField, {
          label,
          description: hint.length > 160 ? `${hint.slice(0, 157)}…` : hint,
          value,
          onChange: (event) => onChange(String(event?.target?.value ?? "")),
        }));
      }
      return h(Row, null, h("label", { style: { display: "block" } }, label,
        h("input", { value, onChange: (event) => onChange(String(event?.target?.value ?? "")), style: { width: "100%" } })));
    }

    function Choice({ label, value, options, onChange }) {
      const rgOptions = options.map(([, optionLabel], index) => ({ data: index, label: optionLabel }));
      const selected = Math.max(0, options.findIndex(([data]) => data === value));
      const select = (option) => onChange(options[Number(option?.data ?? 0)][0]);
      if (DropdownItem) {
        return h(Row, null, h(DropdownItem, {
          label, rgOptions, selectedOption: selected, onChange: select, menuLabel: label,
          layout: "below", childrenContainerWidth: "max", contextMenuPositionOptions: { bMatchWidth: false },
        }));
      }
      return h(Row, null, h(Button, { onClick: () => select({ data: (selected + 1) % options.length }) }, `${label}: ${options[selected][1]}`));
    }

    function StoreMatch({ appId, title, metadata, onChanged }) {
      const [term, setTerm] = React.useState(title || "");
      const [results, setResults] = React.useState(null);
      const [busy, setBusy] = React.useState(false);
      const query = metadataQuery(appId, title);
      const search = async () => {
        setBusy(true);
        const params = new URLSearchParams({ appId: String(appId), term });
        if (title) params.set("title", title);
        const { ok, payload } = await api(`/api/metadata/search?${params}`).catch(() => ({ ok: false }));
        setResults(ok && Array.isArray(payload) ? payload : []);
        setBusy(false);
      };
      const choose = async (steamAppId) => {
        setBusy(true);
        const { ok, payload } = steamAppId
          ? await api(`/api/metadata/games/${appId}/match${query}`, "POST", { steamAppId, title }).catch(() => ({ ok: false }))
          : await api(`/api/metadata/games/${appId}/match${query}`, "DELETE").catch(() => ({ ok: false }));
        setBusy(false);
        if (ok && payload) {
          setResults(null);
          broadcastSnapshot(payload);
          onChanged();
        }
      };
      return h(React.Fragment, null,
        h(Heading, null, "Steam store page"),
        h(Note, null, sourceText(metadata)),
        ui.TextField
          ? h(Row, null, h(ui.TextField, { label: "Search the Steam store", value: term, onChange: (event) => setTerm(String(event?.target?.value ?? "")) }))
          : null,
        h(Row, null, h("div", { style: { display: "flex", gap: "12px", flexWrap: "wrap" } },
          h(Button, { onClick: search, disabled: busy || term.trim().length < 2 }, busy ? "Working…" : "Search"),
          metadata?.matchMode === "manual" ? h(Button, { onClick: () => choose(null), disabled: busy }, "Back to automatic") : null)),
        ...(results || []).slice(0, 8).map((item) =>
          h(Row, { key: `result-${item.steamAppId}` },
            h(Button, { onClick: () => choose(item.steamAppId), disabled: busy }, `Use ${item.title}${item.price ? ` (${item.price})` : ""}`))),
        results && !results.length ? h(Note, null, "Nothing found.") : null);
    }

    function IgnPage({ appId, title, editor, onChanged }) {
      const [value, setValue] = React.useState(editor?.ignPage || "");
      const [busy, setBusy] = React.useState(false);
      const [message, setMessage] = React.useState("");
      React.useEffect(() => setValue(editor?.ignPage || ""), [editor?.ignPage]);
      const save = async (page) => {
        setBusy(true);
        setMessage("");
        const { ok, payload } = await api(`/api/metadata/games/${appId}/ign${metadataQuery(appId, title)}`, "POST", { value: page, title })
          .catch(() => ({ ok: false }));
        setBusy(false);
        if (ok && payload) {
          broadcastSnapshot(payload.metadata);
          onChanged(payload);
          setMessage(page ? "IGN page saved." : "Back to automatic.");
        } else {
          setMessage(payload?.message || "Saving failed.");
        }
      };
      return h(React.Fragment, null,
        h(Heading, null, "IGN page"),
        h(Note, null, editor?.ignPage
          ? "This game takes its info from the IGN page below."
          : "Without a Steam store page, TFS looks for the IGN page with the same name. Enter an address to choose one yourself."),
        ui.TextField
          ? h(Row, null, h(ui.TextField, { label: "IGN page address", value, onChange: (event) => setValue(String(event?.target?.value ?? "")) }))
          : null,
        h(Row, null, h("div", { style: { display: "flex", gap: "12px", flexWrap: "wrap" } },
          h(Button, { onClick: () => save(value.trim()), disabled: busy || !value.trim() }, busy ? "Working…" : "Use this IGN page"),
          editor?.ignPage ? h(Button, { onClick: () => save(""), disabled: busy }, "Back to automatic") : null)),
        message ? h(Note, null, message) : null);
    }

    return function TfsMetadataEditor(props) {
      const appId = Number(props?.match?.params?.appId || 0) || 0;
      const title = String(appOverview(appId)?.display_name || "").trim();
      const query = metadataQuery(appId, title);
      const [editor, setEditor] = React.useState(null);
      const [draft, setDraft] = React.useState(null);
      const [error, setError] = React.useState("");
      const [busy, setBusy] = React.useState(false);
      const [notice, setNotice] = React.useState("");

      const load = React.useCallback(async () => {
        if (!appId) return;
        const { ok, status, payload } = await api(`/api/metadata/games/${appId}/edit${query}`).catch(() => ({ ok: false }));
        if (ok && payload) {
          setEditor(payload);
          setDraft(draftFromEdits(payload.edits));
          setError("");
        } else {
          setError(status === 403
            ? "TFS Metadata is turned off. Turn it on in the Plugin Activator."
            : payload?.message || "Metadata is only available for non-Steam games.");
        }
      }, [appId, query]);

      React.useEffect(() => {
        void load();
      }, [load]);

      const update = (key) => (value) => setDraft((current) => ({ ...current, [key]: value }));

      const save = async (edits) => {
        setBusy(true);
        setNotice("");
        const { ok, payload } = edits
          ? await api(`/api/metadata/games/${appId}/edit${query}`, "POST", { edits, title }).catch(() => ({ ok: false }))
          : await api(`/api/metadata/games/${appId}/edit${query}`, "DELETE").catch(() => ({ ok: false }));
        setBusy(false);
        if (ok && payload) {
          setEditor(payload);
          setDraft(draftFromEdits(payload.edits));
          broadcastSnapshot(payload.metadata);
          setNotice(edits ? "Saved." : "Back to automatic values.");
        } else {
          setNotice("Saving failed.");
        }
      };

      const refreshSource = async () => {
        setBusy(true);
        setNotice("");
        const { ok, payload } = await api(`/api/metadata/games/${appId}${query}`, "POST").catch(() => ({ ok: false }));
        setBusy(false);
        if (ok && payload) {
          broadcastSnapshot(payload);
          await load();
          setNotice("Updated from the source.");
        } else {
          setNotice("The source could not be reached.");
        }
      };

      const page = { padding: "56px 36px 40px", height: "100%", boxSizing: "border-box", overflowY: "auto", color: "#fff" };
      if (error) return h("div", { style: page }, h("h2", null, "TFS Metadata"), h("p", null, error));
      if (!editor || !draft) return h("div", { style: page }, h("h2", null, "TFS Metadata"), h("p", { style: { opacity: 0.7 } }, "Loading…"));

      const automatic = editor.automatic || {};
      const Focusable = ui.Focusable;
      const column = (children) => (Focusable
        ? h(Focusable, { "flow-children": "column", style: { minWidth: 0 } }, ...children)
        : h("div", { style: { minWidth: 0 } }, ...children));
      const automaticRating = Number.isInteger(automatic.rating) ? `${automatic.rating} / 100` : "";

      const editColumn = [
        h(Heading, { key: "edit-heading" }, "Game info"),
        h(Note, { key: "edit-note" }, "Leave a field empty to keep the automatic value. Your values always win."),
        h(Text, { key: "description", label: "Description", value: draft.description, automatic: automatic.shortDescription || automatic.description, onChange: update("description") }),
        h(Text, { key: "developers", label: "Developers (comma separated)", value: draft.developers, automatic: joinList(automatic.developers), onChange: update("developers") }),
        h(Text, { key: "publishers", label: "Publishers (comma separated)", value: draft.publishers, automatic: joinList(automatic.publishers), onChange: update("publishers") }),
        h(Text, { key: "genres", label: "Genres (comma separated)", value: draft.genres, automatic: joinList(automatic.genres), onChange: update("genres") }),
        h(Text, { key: "release", label: "Release date", value: draft.releaseDate, automatic: automatic.releaseDate, onChange: update("releaseDate") }),
        h(Choice, { key: "rating", label: automaticRating ? `Rating (automatic: ${automaticRating})` : "Rating", value: draft.rating, options: ratingOptions, onChange: update("rating") }),
        h(Row, { key: "actions" }, h("div", { style: { display: "flex", gap: "12px", flexWrap: "wrap" } },
          h(Button, { onClick: () => save(editsFromDraft(draft)), disabled: busy }, busy ? "Working…" : "Save"),
          h(Button, { onClick: () => save(null), disabled: busy }, "Reset to automatic"),
          h(Button, { onClick: refreshSource, disabled: busy }, "Update from source"))),
        notice ? h(Note, { key: "notice" }, notice) : null,
      ].filter(Boolean);

      const sideColumn = [
        h(StoreMatch, { key: "match", appId, title, metadata: editor.metadata, onChanged: load }),
        h(IgnPage, {
          key: "ign",
          appId,
          title,
          editor,
          onChanged: (payload) => {
            setEditor(payload);
            setDraft(draftFromEdits(payload.edits));
          },
        }),
        h(Heading, { key: "achievements-heading" }, "Achievements"),
        h(Note, { key: "achievements-note" }, editor.managedByOmniLibrary
          ? "This game belongs to OmniLibrary. Its achievements are managed in OmniLibrary's settings."
          : "Achievements for non-Steam games outside OmniLibrary are not available yet."),
      ];

      return h("div", { style: page },
        h("div", { style: { display: "flex", alignItems: "baseline", gap: "14px", marginBottom: "6px" } },
          h("h2", { style: { margin: 0, fontSize: "26px" } }, "TFS Metadata"),
          title ? h("div", { style: { opacity: 0.6, fontSize: "16px" } }, title) : null),
        // Both columns sit in one row-flowing focus group, so left/right on
        // the D-pad moves between them (a plain div is not a focus group).
        h(Focusable || "div", {
          ...(Focusable ? { "flow-children": "row" } : {}),
          style: { display: "grid", gridTemplateColumns: "minmax(0, 1.1fr) minmax(0, 1fr)", gap: "32px", alignItems: "start" },
        },
          column(editColumn),
          column(sideColumn)));
    };
  }

  async function registerEditorRoute() {
    window.clearTimeout(state.routeTimer);
    if (state.destroyed || state.routeRemover) return;
    const core = window.__tfs;
    if (!core?.routes?.add) {
      state.routeTimer = window.setTimeout(registerEditorRoute, 2000);
      return;
    }
    try {
      await core.whenReady;
      if (state.destroyed || state.routeRemover || !core.React) return;
      if (core.routes.has?.(editorRoute)) core.routes.remove(editorRoute);
      state.routeRemover = core.routes.add(editorRoute, createEditor(core), { name: "TFS Metadata", exact: true });
    } catch (error) {
      console.warn("[TFS Metadata] editor route failed", error);
      state.routeTimer = window.setTimeout(registerEditorRoute, 10000);
    }
  }

  function openEditor(appId) {
    const id = Number(appId) || 0;
    const routes = window.__tfs?.routes;
    if (!id || id < 0x80000000 || !routes?.navigate || !state.routeRemover) return false;
    routes.navigate(`/tfs/metadata/${id}`);
    return true;
  }

  function destroy() {
    state.destroyed = true;
    window.clearTimeout(state.routeTimer);
    try { state.routeRemover?.(); } catch {}
    state.routeRemover = null;
    if (window.__tfsMetadata?.version === version) delete window.__tfsMetadata;
  }

  window.__tfsMetadata = {
    version,
    destroy,
    openEditor,
    refreshEnabled,
    // The context menu entry only appears for non-Steam games while the
    // feature is on and the route exists.
    hasEditor: (appId) => {
      // The Plugin Activator can switch the feature at any time; re-check in
      // the background at most every 30 s and answer with the last state.
      if (Date.now() - state.enabledCheckedAt > 30000) {
        state.enabledCheckedAt = Date.now();
        void refreshEnabled();
      }
      return state.enabled && Boolean(state.routeRemover) &&
        (appId === undefined || Number(appId) >= 0x80000000);
    },
  };

  void refreshEnabled();
  void registerEditorRoute();
})();
