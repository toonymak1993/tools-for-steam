// Tools for Steam - window bridge.
// Registers this Steam window with tfs-core (SharedJSContext): Steam's
// on-screen keyboard opens for every text field here, and stylesheets added
// through core.css reach this window (Decky's injectCssIntoTab, centralised).
(() => {
  const findCore = () => {
    for (const candidate of [window, window.opener, window.opener?.opener]) {
      try {
        const core = candidate?.__tfs;
        if (core?.keyboard?.attachToDocument) {
          return core;
        }
      } catch (_) {}
    }
    return null;
  };

  if (window.__tfsWindowBridgeTimer) {
    window.clearInterval(window.__tfsWindowBridgeTimer);
    window.__tfsWindowBridgeTimer = 0;
  }

  const tryAttach = () => {
    const core = findCore();
    if (!core) {
      return false;
    }
    core.keyboard.attachToDocument(document);
    core.css?.registerDocument?.(document, window.__tfsSurfaceRole || "page");
    return true;
  };

  if (!tryAttach()) {
    // tfs-core loads in parallel; check briefly, then give up quietly.
    let attempts = 0;
    window.__tfsWindowBridgeTimer = window.setInterval(() => {
      attempts += 1;
      if (tryAttach() || attempts >= 120) {
        window.clearInterval(window.__tfsWindowBridgeTimer);
        window.__tfsWindowBridgeTimer = 0;
      }
    }, 500);
  }
})();
