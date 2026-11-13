// Tools for Steam - retired OmniLibrary game page surface.
//
// Earlier versions relabelled Steam's Play button ("Download", "Update",
// "Retry Download", ...) and drew a progress note above the play section.
// Steam's Play button is no longer touched at all: OmniLibrary downloads run
// from their own row below the play section (download-core.js, shared
// context). This stub only removes what older copies left in the page.
(() => {
  const stateVersion = 28;
  const previousState = window.__steamToolsXboxLibrarySurfaceState;
  if (previousState?.version === stateVersion) {
    return;
  }

  try {
    if (previousState?.timer) window.clearTimeout(previousState.timer);
    if (previousState?.mutationTimer) window.clearTimeout(previousState.mutationTimer);
    previousState?.observer?.disconnect?.();
    try { previousState?.omniLibraryStateUnsubscribe?.(); } catch (_) {}
    if (typeof previousState?.storageHandler === "function") {
      window.removeEventListener("storage", previousState.storageHandler);
    }
    if (typeof previousState?.focusHandler === "function") {
      window.removeEventListener("focus", previousState.focusHandler);
    }
    if (typeof previousState?.dialogKeyHandler === "function") {
      window.removeEventListener("keydown", previousState.dialogKeyHandler, true);
    }
    try { previousState?.channel?.close?.(); } catch (_) {}
    try { previousState?.uninstallNavigation?.unregister?.(); } catch (_) {}
    for (const registration of previousState?.dialogNavigation || []) {
      try { registration?.unregister?.(); } catch (_) {}
    }
    restoreSteamPlayLabels(previousState);
    if (previousState && typeof previousState === "object") {
      // An older copy's capture click listener can still sit on Steam's Play
      // button. Emptying its state makes that listener return immediately.
      previousState.currentGame = null;
      previousState.currentAppId = 0;
      previousState.currentStoreId = "";
      previousState.summary = null;
      previousState.timer = 0;
      previousState.mutationTimer = 0;
      previousState.observer = null;
    }
  } catch (_) {}

  for (const id of [
    "steamtools-xbox-library-action",
    "steamtools-omnilibrary-uninstall-action",
    "steamtools-omnilibrary-uninstall-dialog",
    "steamtools-xbox-library-style",
    "steamtools-omnilibrary-download-status",
    "steamtools-omnilibrary-download-style",
  ]) {
    document.getElementById(id)?.remove();
  }
  for (const element of document.querySelectorAll("[data-steamtools-xbox-native-action]")) {
    element.removeAttribute("data-steamtools-xbox-native-action");
  }
  for (const element of document.querySelectorAll("[data-steamtools-omni-download-state]")) {
    element.removeAttribute("data-steamtools-omni-download-state");
    element.removeAttribute("aria-busy");
  }
  for (const element of document.querySelectorAll("[data-steamtools-omni-download-listener]")) {
    // The old click listener stays on the element until Steam re-renders it,
    // but it only touched this surface's own (now deleted) state.
    element.removeAttribute("data-steamtools-omni-download-listener");
  }
  for (const icon of document.querySelectorAll(".steamtools-omni-download-icon")) {
    icon.classList.remove("steamtools-omni-download-icon");
  }
  for (const host of document.querySelectorAll(".steamtools-omni-download-message-host")) {
    host.classList.remove("steamtools-omni-download-message-host");
  }

  window.__steamToolsXboxLibrarySurfaceState = { version: stateVersion, retired: true };

  // Put Steam's own label back on any Play button an older copy relabelled.
  function restoreSteamPlayLabels(previous) {
    const injectedLabelPattern =
      /^(download|update|preparing|queued|downloading(?:\s+.*)?|reconnecting(?:\s+.*)?|resume download|finalizing(?:\s+.*)?|canceling|open xbox|open gog|retry download|install ea app|link ea|open ea app|link ubisoft|retry .+|open .+)$/i;
    for (const [textNode, record] of previous?.managedLabels || []) {
      const originalLabel = typeof record === "string" ? record : record?.originalLabel;
      const appliedLabel = typeof record === "string" ? "" : record?.appliedLabel;
      const currentLabel = String(textNode?.nodeValue || "").trim();
      const stillOurs = appliedLabel ? currentLabel === appliedLabel : injectedLabelPattern.test(currentLabel);
      if (textNode?.isConnected && typeof originalLabel === "string" && stillOurs && textNode.nodeValue !== originalLabel) {
        textNode.nodeValue = originalLabel;
      }
    }
    previous?.managedLabels?.clear?.();
  }
})();
