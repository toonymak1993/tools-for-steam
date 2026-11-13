// Retired: HowLongToBeat now renders inside Steam's own game page from
// hltb-core.js (shared context, route patch through tfs-core). This stub stays
// in the surfaces bundle only to stop and clean up the old DOM panel that
// earlier builds left running in Big Picture windows.
(() => {
  const stateVersion = 11;
  const previousState = window.__steamToolsHltbSurfaceState;
  if (previousState?.version === stateVersion) {
    return "injected";
  }

  try {
    if (previousState?.timerHandle) window.clearInterval(previousState.timerHandle);
    if (previousState?.refreshHandle) window.clearTimeout(previousState.refreshHandle);
    previousState?.observer?.disconnect?.();
    if (typeof previousState?.themeScanHandler === "function") {
      window.removeEventListener("steamtools:theme-scan-complete", previousState.themeScanHandler);
    }
  } catch {}

  try {
    for (const panel of document.querySelectorAll(".steamtools-hltb-panel")) panel.remove();
    for (const host of document.querySelectorAll(".steamtools-hltb-host")) host.classList.remove("steamtools-hltb-host");
    document.getElementById("steamtools-hltb-style")?.remove();
  } catch {}

  window.__steamToolsHltbSurfaceState = { version: stateVersion, retired: true };
  return "injected";
})();
