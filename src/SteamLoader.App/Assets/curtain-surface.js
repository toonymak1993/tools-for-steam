(() => {
  // TFS Curtain window bridge.
  //
  // The launch hook, the curtain itself and the editor route run once in
  // Steam's SharedJSContext (curtain-core.js). Big Picture and menu windows
  // only need this small bridge so other surfaces (the game context menu in
  // artwork-surface.js) can ask for state, toggle a game or open the editor.
  // It also retires the first TFS Curtain, which guessed play buttons with
  // DOM listeners in every window.
  const apiBase = window.__steamLoaderApiBase || "__STEAMLOADER_API_BASE__";
  const version = 2;

  const existing = window.ToolsForSteamCurtain;
  if (existing?.version >= version) {
    existing.refresh?.();
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}
  document.getElementById("steamtools-curtain-instant")?.remove();
  document.getElementById("steamtools-curtain-style")?.remove();

  let fallbackSnapshot = null;

  function shared() {
    try {
      return window.opener?.__tfsCurtain || window.__tfsCurtain || null;
    } catch {
      return null;
    }
  }

  function normalizeAppId(value) {
    const numeric = Number(value || 0);
    if (!Number.isFinite(numeric)) return 0;
    const appId = Math.trunc(numeric);
    return appId < 0 ? appId >>> 0 : appId;
  }

  async function loadFallbackState() {
    try {
      const response = await fetch(`${apiBase}api/curtain/state`, { cache: "no-store" });
      fallbackSnapshot = response.ok ? await response.json() : null;
    } catch {
      fallbackSnapshot = null;
    }
    return fallbackSnapshot;
  }

  function snapshot() {
    return shared()?.snapshot || fallbackSnapshot;
  }

  function isGameEnabled(appId) {
    const runtime = shared();
    if (runtime?.isGameEnabled) return runtime.isGameEnabled(normalizeAppId(appId));
    const current = fallbackSnapshot;
    const id = normalizeAppId(appId);
    return Boolean(current?.enabled && id && !(current.disabledAppIds || []).map(Number).includes(id));
  }

  async function toggleGame(appId, enabled) {
    const runtime = shared();
    if (runtime?.toggleGame) return runtime.toggleGame(appId, enabled);
    try {
      const response = await fetch(`${apiBase}api/curtain/game-enabled`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ appId: normalizeAppId(appId), enabled: Boolean(enabled) }),
      });
      if (response.ok) fallbackSnapshot = await response.json();
      return response.ok;
    } catch {
      return false;
    }
  }

  function openEditor(appId) {
    return shared()?.openEditor?.(appId) === true;
  }

  function hasEditor() {
    return shared()?.hasEditor?.() === true;
  }

  function refresh() {
    if (!shared()) void loadFallbackState();
  }

  function destroy() {
    if (window.ToolsForSteamCurtain === api) delete window.ToolsForSteamCurtain;
  }

  const api = {
    version,
    refresh,
    destroy,
    dismiss: () => shared()?.dismiss?.("closed"),
    isGameEnabled,
    toggleGame,
    openEditor,
    hasEditor,
    get snapshot() { return snapshot(); },
  };
  window.ToolsForSteamCurtain = api;
  refresh();
  return "injected";
})();
