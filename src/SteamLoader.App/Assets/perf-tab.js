// TFS controls inside Steam's own Performance tab (SharedJSContext).
//
// Steam ships the Steam Deck's Performance tab on Windows too, but only its
// overlay level is shown there. This adds a native section below it, built
// from Steam's own Quick Access components (PanelSection, SliderField,
// ToggleField) through core.qam.patchTab, like a Steam Deck:
//  - frame rate limit (RTSS), refresh rate, Windows power mode, CPU boost,
//    TDP on supported devices.
// Labels reuse Steam's own localized strings where Steam has them.
//
// Inspired by Playhub Quick Settings (MIT, LoZazaMastro); independent code.
(() => {
  // 16: internal/external screen rows in Steam's Quick Settings (was the Display plugin).
  const version = 16;

  // -----------------------------------------------------------------------
  // Pure helpers (tests: window.__tfsPerfTabTestHooks)
  // -----------------------------------------------------------------------

  const powerModes = ["efficiency", "balanced", "performance"];

  /** Slider notches for a list of values; 0 is shown as the "off" label. */
  function notches(values, offLabel, suffix = "") {
    return values.map((value, index) => ({ notchIndex: index, label: value === 0 ? offLabel : `${value}${suffix}`, value }));
  }

  const fill = (template, values) => template.replace(/\{(\w+)\}/g, (_, key) => String(values[key] ?? ""));

  /** "AMD Ryzen AI 9 HX 370 · Herstellergrenze 5–35 W · Netzteil" (battery limit added when lower). */
  function deviceDescription(device, tdp, t) {
    const limit = {
      manufacturer: t.limitManufacturer,
      "vendor-app": t.limitVendorApp,
      "amd-default": t.limitAmdDefault,
    }[device.limitSource] || t.limitAmd;
    const parts = [device.cpu, fill(limit, { min: device.minimum, max: device.maximumAc })];
    if (device.maximumBattery > 0) parts.push(fill(t.limitBattery, { max: device.maximumBattery }));
    parts.push(tdp?.powerSource === "battery" ? t.battery : t.pluggedIn);
    return parts.filter(Boolean).join(" · ");
  }

  /** "Netzteil 25 W · Akku 15 W", plus "aktiv" for the running game's profile. */
  function profileDescription(profile, t) {
    const parts = [`${t.pluggedIn} ${profile.acWatts} W`, `${t.battery} ${profile.batteryWatts} W`];
    if (profile.active) parts.push(t.profileActive);
    return parts.join(" · ");
  }

  /** Dropdown options of the profile page; null always means "use the global value". */
  function profileChoiceOptions(detail, t) {
    const global = [null, t.useGlobal];
    return {
      frameLimit: [global, ...(detail.frameLimitOptions || []).map((value) => [value, value === 0 ? t.off : `${value} FPS`])],
      refresh: [global, ...(detail.refreshOptions || []).map((value) => [value, `${value} Hz`])],
      rtss: [global, ...rtssLabels(detail.rtssLevels || [], t).map((label, index) => [index, label])],
      powerMode: [global, ["efficiency", t.powerEfficiency], ["balanced", t.powerBalanced], ["performance", t.powerPerformance]],
      cpuBoost: [global, [true, t.on], [false, t.off]],
    };
  }

  /** "Läuft gerade · Profil vorhanden" / file name for an entry of "add profile". */
  function runningAppDescription(app, t) {
    const file = String(app.executablePath || "").split(/[\\/]/).pop() || "";
    const parts = [app.isCurrentGame ? t.runningGame : file];
    if (app.hasProfile) parts.push(t.hasProfile);
    return parts.filter(Boolean).join(" · ");
  }

  /** Same key as the backend: non-Steam shortcuts run under their 64-bit game id. */
  function steamProfileKey(appId) {
    const id = Number(appId) || 0;
    return id >= 0x80000000
      ? `steam:${((BigInt(id) << 32n) | 0x02000000n).toString()}`
      : `steam:${id}`;
  }

  /** Request body for POST /api/perf-tab/profile from the page's current values. */
  function profileUpdateBody(detail) {
    return {
      key: detail.key,
      acWatts: detail.acWatts,
      batteryWatts: detail.batteryWatts,
      frameLimit: detail.frameLimit ?? null,
      refreshRate: detail.refreshRate ?? null,
      rtssLevel: detail.rtssLevel ?? null,
      powerMode: detail.powerMode ?? null,
      cpuBoost: detail.cpuBoost ?? null,
    };
  }

  /** Short notch labels for the RTSS overlay levels (backend titles are long). */
  function rtssLabels(levels, t) {
    const known = { Off: t.off, FPS: "FPS", "SteamOS Strip": t.rtssStrip, "SteamOS Full": t.rtssFull, "Frame Pacing": t.rtssPacing };
    return levels.map((title) => known[title] || title);
  }

  /**
   * The handheld's answer to the last TDP: "✓ Vom Handheld bestätigt: 20 W (Boost 25/30 W)",
   * "Wird übertragen…", "✗ Nicht übernommen: …" or "✗ Keine Antwort vom Admin-Hilfsdienst".
   */
  function tdpConfirmationText(tdp, t) {
    const confirmation = tdp?.confirmation;
    switch (confirmation?.state) {
      case "confirmed": {
        const boost = confirmation.spptWatts > confirmation.splWatts || confirmation.fpptWatts > confirmation.splWatts
          ? fill(t.tdpBoost, { sppt: confirmation.spptWatts, fppt: confirmation.fpptWatts })
          : "";
        return `✓ ${fill(t.tdpConfirmed, { watts: confirmation.splWatts })}${boost}`;
      }
      case "pending": return fill(t.tdpPending, { watts: confirmation.splWatts || tdp.value });
      case "failed": return `✗ ${fill(t.tdpRejected, { detail: confirmation.message || "" })}`;
      case "no-helper": return `✗ ${t.tdpNoHelper}`;
      default: return "";
    }
  }

  /** Warning line under the TDP slider: PawnIO missing, vendor tool running, last SMU error. */
  function tdpWarningText(tdp, t) {
    switch (tdp?.warning) {
      case "pawnio-missing": return t.tdpPawnIo;
      case "vendor-app": return t.tdpVendorApp.replace("{apps}", tdp.detail || "");
      case "error": return t.tdpError.replace("{detail}", tdp.detail || "");
      default: return "";
    }
  }

  /**
   * Values the user just changed win over server data for a short while. Without this, the
   * answer to an earlier step (or the periodic refresh) set the slider back mid-drag.
   * hold(): a change is in progress; release(): its request answered (only if no newer change
   * came since); apply(): lays the held changes over fresh server data.
   */
  function createLocalHolds(now = () => Date.now(), holdMs = 1500, graceMs = 400) {
    const entries = new Map();
    return {
      hold(key, change) {
        entries.set(key, { change, until: now() + holdMs });
      },
      release(key, change) {
        const entry = entries.get(key);
        if (entry && entry.change === change) entry.until = Math.min(entry.until, now() + graceMs);
      },
      apply(value) {
        if (!value) return value;
        let result = value;
        for (const [key, entry] of entries) {
          if (entry.until <= now()) { entries.delete(key); continue; }
          try { result = entry.change(result); } catch {}
        }
        return result;
      },
      get size() { return entries.size; },
    };
  }

  /** Index of the value in the list, or the nearest one. */
  function nearestIndex(values, value) {
    if (!values.length) return 0;
    let best = 0;
    for (let index = 1; index < values.length; index += 1) {
      if (Math.abs(values[index] - value) < Math.abs(values[best] - value)) best = index;
    }
    return best;
  }

  const fallbackText = {
    english: {
      section: "Tools for Steam",
      frameLimit: "Frame Rate Limit",
      off: "Off",
      refresh: "Refresh Rate",
      powerMode: "Windows Power Mode",
      powerEfficiency: "Efficiency",
      powerBalanced: "Balanced",
      powerPerformance: "Performance",
      cpuBoost: "CPU Boost",
      cpuBoostDescription: "Lets the processor clock above its base speed. Off saves power and heat.",
      tdp: "TDP Limit",
      tdpPawnIo: "PawnIO is not installed. Tools for Steam needs it to change the TDP.",
      tdpVendorApp: "{apps} is running and may override this limit.",
      tdpError: "The TDP could not be applied: {detail}",
      tdpConfirmed: "Confirmed by the handheld: {watts} W",
      tdpBoost: " (boost {sppt}/{fppt} W)",
      tdpPending: "Sending {watts} W to the handheld…",
      tdpRejected: "Not applied: {detail}",
      tdpNoHelper: "Not applied: the admin helper of Tools for Steam is not running.",
      pluggedIn: "Plugged in",
      battery: "Battery",
      limitManufacturer: "Manufacturer limit {min}–{max} W",
      limitVendorApp: "Limit from the maker's app {min}–{max} W",
      limitAmd: "AMD chip limit {min}–{max} W (maker limit unknown)",
      limitAmdDefault: "Safe AMD default limit {min}–{max} W (maker limit unknown)",
      limitBattery: "on battery up to {max} W",
      gameProfile: "Use Per-Game Profile",
      profiles: "Game Profiles",
      profilesCollapsedHint: "Show saved profiles and automatic switching",
      profileOpenHint: "Select to edit",
      addProfile: "Add Profile",
      contextMenuNew: "TFS Game Profile (new)...",
      contextMenuEdit: "TFS Game Profile...",
      addProfileDescription: "Pick the running game or an open app",
      addProfileEmpty: "No game or app window is open.",
      runningGame: "Running game",
      hasProfile: "has a profile",
      profileSaved: "Saved.",
      loading: "Loading…",
      profileTdp: "TDP limit",
      profileDisplay: "Frame rate and overlay",
      profileSystem: "System",
      profileManage: "Profile",
      profileGlobalHint: "\"Global\" keeps the value you use outside this game. Changes save automatically.",
      useGlobal: "Global",
      on: "On",
      autoProfiles: "Apply Profiles Automatically",
      autoProfilesDescription: "Switches to a game's profile when it starts and back afterwards.",
      noProfiles: "No game profiles yet. Use \"Add Profile\" or turn on \"Use Per-Game Profile\" while a game runs.",
      profileActive: "active",
      deleteProfile: "Delete",
      rtssStrip: "Strip",
      rtssFull: "Full",
      rtssPacing: "Pacing",
      unavailable: "Tools for Steam is not reachable.",
      audio: "Audio",
      volume: "Volume",
      mute: "Mute",
      microphone: "Microphone",
      micMute: "Mute Microphone",
      outputDevice: "Output Device",
      inputDevice: "Input Device",
      appVolume: "App Volume",
      display: "Display",
      brightness: "Brightness",
      resolution: "Resolution",
      hdr: "HDR",
      displayInternal: "Built-in Screen Only",
      displayInternalDescription: "Switches off the external screen (like Win + P).",
      displayExternal: "External Screen Only",
      displayExternalDescription: "Switches off the built-in screen (like Win + P).",
    },
    german: {
      section: "Tools for Steam",
      frameLimit: "Bildratenbegrenzung",
      off: "Aus",
      refresh: "Bildwiederholrate",
      powerMode: "Windows-Energiemodus",
      powerEfficiency: "Effizienz",
      powerBalanced: "Ausgewogen",
      powerPerformance: "Leistung",
      cpuBoost: "CPU-Boost",
      cpuBoostDescription: "Lässt den Prozessor über seinen Grundtakt takten. Aus spart Strom und Wärme.",
      tdp: "TDP-Limit",
      tdpPawnIo: "PawnIO ist nicht installiert. Tools for Steam braucht es, um den TDP zu ändern.",
      tdpVendorApp: "{apps} läuft und kann dieses Limit überschreiben.",
      tdpError: "Der TDP konnte nicht gesetzt werden: {detail}",
      tdpConfirmed: "Vom Handheld bestätigt: {watts} W",
      tdpBoost: " (Boost {sppt}/{fppt} W)",
      tdpPending: "{watts} W werden an den Handheld übertragen…",
      tdpRejected: "Nicht übernommen: {detail}",
      tdpNoHelper: "Nicht übernommen: Der Admin-Hilfsdienst von Tools for Steam läuft nicht.",
      pluggedIn: "Netzteil",
      battery: "Akku",
      limitManufacturer: "Herstellergrenze {min}–{max} W",
      limitVendorApp: "Grenze aus der Hersteller-App {min}–{max} W",
      limitAmd: "AMD-Chipgrenze {min}–{max} W (Herstellergrenze unbekannt)",
      limitAmdDefault: "Sichere AMD-Standardgrenze {min}–{max} W (Herstellergrenze unbekannt)",
      limitBattery: "im Akkubetrieb bis {max} W",
      gameProfile: "Spielprofil verwenden",
      profiles: "Spielprofile",
      profilesCollapsedHint: "Gespeicherte Profile und automatisches Umschalten anzeigen",
      profileOpenHint: "Auswählen zum Bearbeiten",
      addProfile: "Profil hinzufügen",
      contextMenuNew: "TFS Spielprofil anlegen...",
      contextMenuEdit: "TFS Spielprofil...",
      addProfileDescription: "Laufendes Spiel oder offene App auswählen",
      addProfileEmpty: "Kein Spiel- oder App-Fenster ist offen.",
      runningGame: "Läuft gerade",
      hasProfile: "Profil vorhanden",
      profileSaved: "Gespeichert.",
      loading: "Wird geladen…",
      profileTdp: "TDP-Limit",
      profileDisplay: "Bildrate und Overlay",
      profileSystem: "System",
      profileManage: "Profil",
      profileGlobalHint: "\"Global\" behält den Wert, den du außerhalb dieses Spiels nutzt. Änderungen werden automatisch gespeichert.",
      useGlobal: "Global",
      on: "An",
      autoProfiles: "Profile automatisch anwenden",
      autoProfilesDescription: "Wechselt beim Spielstart ins Profil des Spiels und danach zurück.",
      noProfiles: "Noch keine Spielprofile. Nutze \"Profil hinzufügen\" oder schalte im Spiel \"Spielprofil verwenden\" ein.",
      profileActive: "aktiv",
      deleteProfile: "Löschen",
      rtssStrip: "Leiste",
      rtssFull: "Voll",
      rtssPacing: "Pacing",
      unavailable: "Tools for Steam ist nicht erreichbar.",
      audio: "Audio",
      volume: "Lautstärke",
      mute: "Ton aus",
      microphone: "Mikrofon",
      micMute: "Mikrofon stumm",
      outputDevice: "Ausgabegerät",
      inputDevice: "Eingabegerät",
      appVolume: "App-Lautstärke",
      display: "Anzeige",
      brightness: "Helligkeit",
      resolution: "Auflösung",
      hdr: "HDR",
      displayInternal: "Nur interner Bildschirm",
      displayInternalDescription: "Schaltet den externen Bildschirm aus (wie Win + P).",
      displayExternal: "Nur externer Bildschirm",
      displayExternalDescription: "Schaltet den eingebauten Bildschirm aus (wie Win + P).",
    },
  };

  /**
   * Adds rows to Steam's own Quick Settings output: TFS audio rows go into the
   * section titled like Steam's audio section (or a new one), the display
   * section follows it. Steam's elements are cloned, never mutated.
   * TFS brings its own device pickers: Steam's pickers stay empty on Windows.
   */
  function mergeSettingsSections(React, output, { audioTitle, audioRows, appTitle = "", appRows = [], displayTitle, displayRows, Section }) {
    const children = React.Children.toArray(output?.props?.children);
    let audioIndex = children.findIndex((child) => child?.props?.title === audioTitle);
    const rows = audioRows;
    if (rows.length) {
      if (audioIndex >= 0) {
        const audio = children[audioIndex];
        children[audioIndex] = React.cloneElement(audio, undefined, ...React.Children.toArray(audio.props.children), ...rows);
      } else {
        children.unshift(React.createElement(Section, { key: "tfs-audio", title: audioTitle }, ...rows));
        audioIndex = 0;
      }
    }
    let insertAt = audioIndex + 1;
    if (appRows.length) {
      children.splice(insertAt, 0, React.createElement(Section, { key: "tfs-apps", title: appTitle }, ...appRows));
      insertAt += 1;
    }
    if (displayRows.length) {
      children.splice(insertAt, 0, React.createElement(Section, { key: "tfs-display", title: displayTitle }, ...displayRows));
    }
    return output && output.props
      ? React.cloneElement(output, undefined, ...children)
      : React.createElement(React.Fragment, null, output, ...children);
  }

  const internals = {
    notches, nearestIndex, powerModes, fallbackText, mergeSettingsSections, tdpWarningText,
    deviceDescription, profileDescription, rtssLabels, profileChoiceOptions, profileUpdateBody, runningAppDescription,
    steamProfileKey, tdpConfirmationText, createLocalHolds,
  };
  if (window.__tfsPerfTabTestHooks === true) {
    window.__tfsPerfTabInternals = internals;
    return "test";
  }

  // -----------------------------------------------------------------------
  // Runtime
  // -----------------------------------------------------------------------

  const role = window.__tfsSurfaceRole;
  if (role && role !== "shared") return "skipped";

  const existing = window.__tfsPerfTab;
  if (existing?.version >= version) {
    existing.refresh?.();
    return "injected";
  }
  try { existing?.destroy?.(); } catch {}

  const apiBaseFallback = "__STEAMLOADER_API_BASE__";
  const perfTabKey = 5; // EQuickAccessTab.Perf
  const settingsTabKey = 4; // EQuickAccessTab.Settings
  const patchId = "tfs-perf-tab";
  const profilesOpenKey = "tfs.perfTab.profilesOpen";
  const profileRoute = "/tfs/game-profile/:key";
  const state = { remover: null, settingsRemover: null, profileRouteRemover: null, timer: 0, destroyed: false, language: "english" };

  function apiBase() {
    const base = window.__steamLoaderApiBase || apiBaseFallback;
    return base.endsWith("/") ? base : `${base}/`;
  }

  async function api(path, body) {
    const response = await fetch(`${apiBase()}${path.replace(/^\//, "")}`, body === undefined
      ? { cache: "no-store" }
      : { method: "POST", cache: "no-store", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    const payload = await response.json().catch(() => null);
    if (!response.ok) throw new Error(payload?.message || `HTTP ${response.status}`);
    return payload;
  }

  /** Steam's own localized string when Steam has one, else our fallback. */
  function steamText(token, fallback) {
    try {
      const value = window.LocalizationManager?.LocalizeString?.(token);
      if (value && value !== token) return value;
    } catch {}
    return fallback;
  }

  function text() {
    return fallbackText[state.language] || fallbackText.english;
  }

  /**
   * Steam's own Quick Access row (padded like its other rows). core.ui's
   * PanelSectionRow is a plain row, so take Steam's from the module that also
   * exports the PanelSection with a PanelSectionTitle.
   */
  let steamRowCache = null;
  let steamSectionCache = null;
  function steamPanelSection(core) {
    if (!steamSectionCache) steamPanelRow(core);
    return steamSectionCache;
  }
  function steamPanelRow(core) {
    if (steamRowCache && steamSectionCache) return steamRowCache;
    try {
      const webpack = core.webpack;
      const module = webpack.findModule((candidate) => candidate && typeof candidate === "object" &&
        typeof candidate.a === "function" && (webpack.getSourceText(candidate.a) || "").includes("PanelSectionTitle") &&
        candidate.k && typeof candidate.k === "object" && typeof candidate.k.render === "function");
      steamRowCache = module?.k || null;
      steamSectionCache = module?.a || null;
    } catch {}
    return steamRowCache;
  }

  function createSection(core) {
    const React = core.React;
    const h = React.createElement;
    const ui = core.ui || {};
    const Section = steamPanelSection(core) || ui.PanelSection || (({ title, children }) => h("div", null, title ? h("div", null, title) : null, children));
    const Row = steamPanelRow(core) || ui.PanelSectionRow || (({ children }) => h("div", null, children));

    function Slider(props) {
      return ui.SliderField ? h(Row, null, h(ui.SliderField, props)) : null;
    }

    function TfsPerfSection() {
      const [perf, setPerf] = React.useState(null);
      const [error, setError] = React.useState("");
      const [profilesOpen, setProfilesOpen] = React.useState(() => {
        try { return window.localStorage.getItem(profilesOpenKey) === "1"; } catch { return false; }
      });
      // "Add profile": list of the running game and open apps, loaded when opened.
      const [runningApps, setRunningApps] = React.useState(null);
      const toggleAdd = () => {
        if (runningApps) { setRunningApps(null); return; }
        setRunningApps([]);
        api("/api/perf-tab/running-apps").then((apps) => setRunningApps(Array.isArray(apps) ? apps : [])).catch(() => setRunningApps([]));
      };
      const addProfile = (app) => {
        if (app.hasProfile) { openProfilePage(app.key); return; }
        api("/api/perf-tab/add-profile", { value: app.key })
          .then(() => { setRunningApps(null); load(); openProfilePage(app.key); })
          .catch((reason) => setError(String(reason?.message || reason)));
      };
      const pending = React.useRef({});
      const holds = React.useRef(null);
      if (!holds.current) holds.current = createLocalHolds();

      const load = React.useCallback(() => {
        api("/api/perf-tab/state").then((value) => { setPerf(holds.current.apply(value)); setError(""); }).catch((reason) => setError(String(reason?.message || reason)));
      }, []);

      const followTimer = React.useRef(0);
      const followTdpConfirmation = () => {
        window.clearInterval(followTimer.current);
        let ticks = 0;
        followTimer.current = window.setInterval(() => {
          ticks += 1;
          load();
          if (ticks >= 12) window.clearInterval(followTimer.current);
        }, 1000);
      };
      React.useEffect(() => () => window.clearInterval(followTimer.current), []);

      React.useEffect(() => {
        load();
        const timer = window.setInterval(load, 5000);
        return () => {
          window.clearInterval(timer);
          Object.values(pending.current).forEach((handle) => window.clearTimeout(handle));
        };
      }, [load]);

      // Sliders report every step; send the last value once the user rests.
      // Server answers and the 5 s refresh keep the user's value while they still drag.
      const send = (key, path, body, optimistic, delay = 350) => {
        holds.current.hold(key, optimistic);
        setPerf((current) => (current ? optimistic(current) : current));
        window.clearTimeout(pending.current[key]);
        pending.current[key] = window.setTimeout(() => {
          holds.current.hold(key, optimistic);
          api(path, body)
            .then((value) => { holds.current.release(key, optimistic); setPerf(holds.current.apply(value)); })
            .catch((reason) => { holds.current.release(key, optimistic); setError(String(reason?.message || reason)); load(); });
        }, delay);
      };

      const t = text();
      if (!perf) {
        return error ? h(Section, { title: t.section }, h(Row, null, h("div", { style: { opacity: 0.6, fontSize: "12px", padding: "4px 0" } }, t.unavailable))) : null;
      }

      const frameValues = perf.frameLimit?.options || [];
      const refreshValues = perf.refreshRate?.options || [];
      const offLabel = steamText("#QuickAccess_Tab_Perf_LimitFrameRate_Off", t.off);
      const children = [];
      const profiles = perf.profiles || {};

      // Which device the TDP controls, with the maker's limits and the power source.
      if (perf.device && ui.Field) {
        children.push(h(Row, { key: "device" }, h(ui.Field, {
          label: perf.device.name,
          description: deviceDescription(perf.device, perf.tdp, t),
          focusable: false,
        })));
      }

      // Steam Deck: "use per-game profile" while a game runs.
      if (profiles.supported && profiles.gameTitle && ui.ToggleField) {
        children.push(h(Row, { key: "game-profile" }, h(ui.ToggleField, {
          label: t.gameProfile,
          description: profiles.gameTitle,
          checked: Boolean(profiles.gameUsesProfile),
          onChange: (enabled) => send("game-profile", "/api/perf-tab/game-profile", { value: enabled },
            (current) => ({ ...current, profiles: { ...current.profiles, gameUsesProfile: enabled } }), 0),
        })));
      }

      const rtssLevels = perf.rtss?.levels || [];
      if (perf.rtss?.available && rtssLevels.length > 1) {
        const labels = rtssLabels(rtssLevels, t);
        children.push(h(Slider, {
          key: "rtss",
          label: "RTSS",
          value: Math.min(Math.max(0, perf.rtss.level), rtssLevels.length - 1),
          min: 0,
          max: rtssLevels.length - 1,
          step: 1,
          notchCount: rtssLevels.length,
          notchLabels: labels.map((label, index) => ({ notchIndex: index, label, value: index })),
          notchTicksVisible: true,
          onChange: (index) => send("rtss", "/api/perf-tab/rtss", { value: index },
            (current) => ({ ...current, rtss: { ...current.rtss, level: index } })),
        }));
      }

      if (perf.frameLimit?.available && frameValues.length) {
        children.push(h(Slider, {
          key: "fps",
          label: steamText("#QuickAccess_Tab_Perf_LimitFrameRate", t.frameLimit),
          value: nearestIndex(frameValues, perf.frameLimit.value),
          min: 0,
          max: frameValues.length - 1,
          step: 1,
          notchCount: frameValues.length,
          notchLabels: notches(frameValues, offLabel),
          notchTicksVisible: true,
          onChange: (index) => send("fps", "/api/perf-tab/frame-limit", { value: frameValues[index] },
            (current) => ({ ...current, frameLimit: { ...current.frameLimit, value: frameValues[index] } })),
        }));
      }

      if (perf.refreshRate?.available && refreshValues.length > 1) {
        children.push(h(Slider, {
          key: "hz",
          label: steamText("#QuickAccess_Tab_Perf_RefreshRate", t.refresh),
          value: nearestIndex(refreshValues, perf.refreshRate.current),
          min: 0,
          max: refreshValues.length - 1,
          step: 1,
          notchCount: refreshValues.length,
          notchLabels: notches(refreshValues, offLabel, " Hz"),
          notchTicksVisible: true,
          onChange: (index) => send("hz", "/api/perf-tab/refresh-rate", { value: refreshValues[index] },
            (current) => ({ ...current, refreshRate: { ...current.refreshRate, current: refreshValues[index] } })),
        }));
      }

      if (perf.powerMode) {
        const labels = [t.powerEfficiency, t.powerBalanced, t.powerPerformance];
        children.push(h(Slider, {
          key: "power",
          label: t.powerMode,
          value: Math.max(0, powerModes.indexOf(perf.powerMode)),
          min: 0,
          max: 2,
          step: 1,
          notchCount: 3,
          notchLabels: labels.map((label, index) => ({ notchIndex: index, label, value: index })),
          notchTicksVisible: true,
          onChange: (index) => send("power", "/api/perf-tab/power-mode", { value: powerModes[index] },
            (current) => ({ ...current, powerMode: powerModes[index] })),
        }));
      }

      if (perf.cpuBoost?.supported && ui.ToggleField) {
        children.push(h(Row, { key: "boost" }, h(ui.ToggleField, {
          label: t.cpuBoost,
          description: t.cpuBoostDescription,
          checked: Boolean(perf.cpuBoost.enabled),
          onChange: (enabled) => send("boost", "/api/perf-tab/cpu-boost", { value: enabled },
            (current) => ({ ...current, cpuBoost: { ...current.cpuBoost, enabled } })),
        })));
      }

      if (perf.tdp?.supported) {
        children.push(h(Slider, {
          key: "tdp",
          label: `${t.tdp} · ${perf.tdp.powerSource === "battery" ? t.battery : t.pluggedIn}`,
          description: [
            tdpConfirmationText(perf.tdp, t),
            // The SMU error is already in the confirmation line.
            perf.tdp.warning === "error" && perf.tdp.confirmation?.state === "failed" ? "" : tdpWarningText(perf.tdp, t),
          ].filter(Boolean).join(" · ") || undefined,
          value: perf.tdp.value,
          min: perf.tdp.minimum,
          max: perf.tdp.maximum,
          step: 1,
          showValue: true,
          editableValue: false,
          renderValue: (value) => `${Math.round(value)} W`,
          onChange: (watts) => {
            send("tdp", "/api/perf-tab/tdp", { value: watts },
              (current) => ({ ...current, tdp: { ...current.tdp, value: watts, confirmation: { state: "pending", splWatts: watts } } }));
            // Ask for the handheld's answer every second for a while instead of every 5 s.
            followTdpConfirmation();
          },
        }));
      }

      const profileRows = [];
      if (profiles.supported) {
        // Collapsible like a Steam Deck sub-menu: one header row, details only when open.
        const savedCount = (profiles.saved || []).length;
        const toggleOpen = () => setProfilesOpen((open) => {
          try { window.localStorage.setItem(profilesOpenKey, open ? "0" : "1"); } catch {}
          return !open;
        });
        profileRows.push(h(Row, { key: "profiles-header" }, ui.Field ? h(ui.Field, {
          label: `${t.profiles} (${savedCount})`,
          description: profilesOpen ? undefined : t.profilesCollapsedHint,
          focusable: true,
          highlightOnFocus: true,
          onActivate: toggleOpen,
          onClick: toggleOpen,
          childrenContainerWidth: "min",
        }, h("span", { style: { fontSize: "18px", opacity: 0.8 } }, profilesOpen ? "▾" : "▸")) : null));
      }
      if (profiles.supported && profilesOpen) {
        if (ui.ToggleField) {
          profileRows.push(h(Row, { key: "auto-profiles" }, h(ui.ToggleField, {
            label: t.autoProfiles,
            description: t.autoProfilesDescription,
            checked: Boolean(profiles.autoApply),
            onChange: (enabled) => send("auto-profiles", "/api/perf-tab/auto-profiles", { value: enabled },
              (current) => ({ ...current, profiles: { ...current.profiles, autoApply: enabled } }), 0),
          })));
        }
        if (ui.Field) {
          profileRows.push(h(Row, { key: "add-profile" }, h(ui.Field, {
            label: t.addProfile,
            description: t.addProfileDescription,
            focusable: true,
            highlightOnFocus: true,
            onActivate: toggleAdd,
            onClick: toggleAdd,
            childrenContainerWidth: "min",
          }, h("span", { style: { fontSize: "18px", opacity: 0.8 } }, runningApps ? "▾" : "+"))));
          if (runningApps && !runningApps.length) {
            profileRows.push(h(Row, { key: "add-empty" }, h("div", { style: { opacity: 0.6, fontSize: "12px", padding: "4px 0 4px 16px" } }, t.addProfileEmpty)));
          }
          for (const app of runningApps || []) {
            const pick = () => addProfile(app);
            profileRows.push(h(Row, { key: `add-${app.key}` }, h(ui.Field, {
              label: app.title,
              description: runningAppDescription(app, t),
              focusable: true,
              highlightOnFocus: true,
              onActivate: pick,
              onClick: pick,
              indentLevel: 1,
              childrenContainerWidth: "min",
            }, h("span", { style: { fontSize: "16px", opacity: 0.8 } }, app.hasProfile ? "›" : "+"))));
          }
        }
        const saved = profiles.saved || [];
        if (!saved.length) {
          profileRows.push(h(Row, { key: "no-profiles" }, h("div", { style: { opacity: 0.6, fontSize: "12px", padding: "4px 0" } }, t.noProfiles)));
        }
        for (const profile of saved) {
          if (!ui.Field) break;
          // A opens the profile's own page (TDP, frame rate, RTSS, power mode, boost, delete).
          const open = () => openProfilePage(profile.key);
          profileRows.push(h(Row, { key: `profile-${profile.key}` }, h(ui.Field, {
            label: profile.title || profile.key,
            description: `${profileDescription(profile, t)} · ${t.profileOpenHint}`,
            focusable: true,
            highlightOnFocus: true,
            onActivate: open,
            onClick: open,
            childrenContainerWidth: "min",
          }, h("span", { style: { fontSize: "18px", opacity: 0.8 } }, "›"))));
        }
      }

      const sections = [];
      if (children.length) sections.push(h(Section, { key: "tfs", title: t.section }, ...children));
      if (profileRows.length) sections.push(h(Section, { key: "tfs-profiles" }, ...profileRows));
      return sections.length ? h(React.Fragment, null, ...sections) : null;
    }

    const ErrorBoundary = core.ErrorBoundary;
    return function TfsPerfTabPanel({ original }) {
      return h(React.Fragment, null,
        original,
        ErrorBoundary ? h(ErrorBoundary, { name: "TFS Performance" }, h(TfsPerfSection)) : h(TfsPerfSection));
    };
  }

  // -----------------------------------------------------------------------
  // Quick Settings tab: audio volume rows + display section
  // -----------------------------------------------------------------------

  function useQuickSettings() {
    const React = window.__tfs.React;
    const [qs, setQs] = React.useState(null);
    const pending = React.useRef({});
    const holds = React.useRef(null);
    if (!holds.current) holds.current = createLocalHolds();
    const load = React.useCallback(() => {
      api("/api/quick-settings/state").then((value) => setQs(holds.current.apply(value))).catch(() => {});
    }, []);
    React.useEffect(() => {
      load();
      const timer = window.setInterval(load, 4000);
      return () => {
        window.clearInterval(timer);
        Object.values(pending.current).forEach((handle) => window.clearTimeout(handle));
      };
    }, [load]);
    const send = (key, path, body, optimistic, delay = 250) => {
      // Keep the user's value while they drag; server answers must not pull the slider back.
      holds.current.hold(key, optimistic);
      setQs((current) => (current ? optimistic(current) : current));
      window.clearTimeout(pending.current[key]);
      pending.current[key] = window.setTimeout(() => {
        holds.current.hold(key, optimistic);
        api(path, body)
          .then((value) => { holds.current.release(key, optimistic); setQs(holds.current.apply(value)); })
          .catch(() => { holds.current.release(key, optimistic); load(); });
      }, delay);
    };
    return [qs, send];
  }

  const settingsPanelCache = new WeakMap();

  function wrapSettingsPanel(core, Original) {
    if (settingsPanelCache.has(Original)) return settingsPanelCache.get(Original);
    const React = core.React;
    const h = React.createElement;
    const ui = core.ui || {};
    const Section = steamPanelSection(core) || ui.PanelSection || (({ title, children }) => h("div", null, title, children));
    const Row = steamPanelRow(core) || ui.PanelSectionRow || (({ children }) => h("div", null, children));

    function deviceRow(key, label, devices, path, listKey, send) {
      if (!ui.DropdownItem || devices.length < 1) return null;
      const selected = Math.max(0, devices.findIndex((device) => device.selected));
      return h(Row, { key }, h(ui.DropdownItem, {
        label,
        rgOptions: devices.map((device, index) => ({ data: index, label: device.name })),
        selectedOption: selected,
        menuLabel: label,
        layout: "below",
        childrenContainerWidth: "max",
        contextMenuPositionOptions: { bMatchWidth: false },
        onChange: (option) => {
          const device = devices[Number(option?.data ?? 0)];
          if (device) send(key, path, { value: device.id },
            (current) => ({ ...current, [listKey]: devices.map((entry) => ({ ...entry, selected: entry.id === device.id })) }), 0);
        },
      }));
    }

    function rowsFor(qs, send) {
      const t = text();
      const audioRows = [];
      const appRows = [];
      const displayRows = [];
      if (!qs) return { audioRows, appRows, displayRows };

      const outputDevice = deviceRow("tfs-device-output", t.outputDevice, qs.outputDevices || [], "/api/quick-settings/output-device", "outputDevices", send);
      if (outputDevice) audioRows.push(outputDevice);
      if (qs.output?.available && ui.SliderField) {
        audioRows.push(h(Row, { key: "tfs-volume" }, h(ui.SliderField, {
          label: t.volume,
          value: qs.output.muted ? 0 : qs.output.volume,
          min: 0, max: 100, step: 1, showValue: true, editableValue: false, renderValue: (value) => `${Math.round(value)} %`,
          onChange: (value) => send("vol", "/api/quick-settings/output-volume", { value },
            (current) => ({ ...current, output: { ...current.output, volume: value, muted: false } })),
        })));
      }
      if (qs.output?.available && ui.ToggleField) {
        audioRows.push(h(Row, { key: "tfs-mute" }, h(ui.ToggleField, {
          label: t.mute,
          checked: Boolean(qs.output.muted),
          onChange: (value) => send("mute", "/api/quick-settings/output-muted", { value },
            (current) => ({ ...current, output: { ...current.output, muted: value } }), 0),
        })));
      }
      const inputDevice = deviceRow("tfs-device-input", t.inputDevice, qs.inputDevices || [], "/api/quick-settings/input-device", "inputDevices", send);
      if (inputDevice) audioRows.push(inputDevice);
      if (qs.input?.available && ui.SliderField) {
        audioRows.push(h(Row, { key: "tfs-mic" }, h(ui.SliderField, {
          label: t.microphone,
          value: qs.input.muted ? 0 : qs.input.volume,
          min: 0, max: 100, step: 1, showValue: true, editableValue: false, renderValue: (value) => `${Math.round(value)} %`,
          onChange: (value) => send("mic", "/api/quick-settings/input-volume", { value },
            (current) => ({ ...current, input: { ...current.input, volume: value, muted: false } })),
        })));
      }
      if (qs.input?.available && ui.ToggleField) {
        audioRows.push(h(Row, { key: "tfs-mic-mute" }, h(ui.ToggleField, {
          label: t.micMute,
          checked: Boolean(qs.input.muted),
          onChange: (value) => send("micmute", "/api/quick-settings/input-muted", { value },
            (current) => ({ ...current, input: { ...current.input, muted: value } }), 0),
        })));
      }

      for (const app of qs.apps || []) {
        if (!ui.SliderField) break;
        appRows.push(h(Row, { key: `tfs-app-${app.id}` }, h(ui.SliderField, {
          label: app.name,
          value: app.muted ? 0 : app.volume,
          min: 0, max: 100, step: 1, showValue: true, editableValue: false, renderValue: (value) => `${Math.round(value)} %`,
          onChange: (value) => send(`app-${app.id}`, "/api/quick-settings/app-volume", { id: app.id, value },
            (current) => ({ ...current, apps: (current.apps || []).map((entry) => (entry.id === app.id ? { ...entry, volume: value, muted: false } : entry)) })),
        })));
      }

      const display = qs.display || {};
      if (display.brightnessSupported && ui.SliderField) {
        displayRows.push(h(Row, { key: "tfs-brightness" }, h(ui.SliderField, {
          label: t.brightness,
          value: display.brightness,
          min: 0, max: 100, step: 1, showValue: true, editableValue: false, renderValue: (value) => `${Math.round(value)} %`,
          onChange: (value) => send("bright", "/api/quick-settings/brightness", { value },
            (current) => ({ ...current, display: { ...current.display, brightness: value } })),
        })));
      }
      const resolutions = display.resolutions || [];
      if (resolutions.length > 1 && ui.DropdownItem) {
        const selected = Math.max(0, resolutions.findIndex((item) => item.selected));
        displayRows.push(h(Row, { key: "tfs-resolution" }, h(ui.DropdownItem, {
          label: t.resolution,
          rgOptions: resolutions.map((item, index) => ({ data: index, label: `${item.title} (${item.description})` })),
          selectedOption: selected,
          menuLabel: t.resolution,
          layout: "below",
          childrenContainerWidth: "max",
          contextMenuPositionOptions: { bMatchWidth: false },
          onChange: (option) => {
            const item = resolutions[Number(option?.data ?? 0)];
            if (item) send("res", "/api/quick-settings/resolution", { value: item.id },
              (current) => ({ ...current, display: { ...current.display, resolutions: resolutions.map((entry) => ({ ...entry, selected: entry.id === item.id })) } }), 0);
          },
        })));
      }
      if (display.hdrSupported && ui.ToggleField) {
        displayRows.push(h(Row, { key: "tfs-hdr" }, h(ui.ToggleField, {
          label: t.hdr,
          checked: Boolean(display.hdrEnabled),
          onChange: (value) => send("hdr", "/api/quick-settings/hdr", { value },
            (current) => ({ ...current, display: { ...current.display, hdrEnabled: value } }), 0),
        })));
      }
      // Built-in / external screen only (Windows' display switch, like Win + P).
      if (ui.Field) {
        for (const [mode, label, description] of [
          ["internal", t.displayInternal, t.displayInternalDescription],
          ["external", t.displayExternal, t.displayExternalDescription],
        ]) {
          const activate = () => send(`output-${mode}`, "/api/quick-settings/display-output", { value: mode }, (current) => current, 0);
          displayRows.push(h(Row, { key: `tfs-display-${mode}` }, h(ui.Field, {
            label,
            description,
            focusable: true,
            highlightOnFocus: true,
            onActivate: activate,
            onClick: activate,
            childrenContainerWidth: "min",
          }, h("span", { style: { fontSize: "18px", opacity: 0.8 } }, "›"))));
        }
      }
      return { audioRows, appRows, displayRows };
    }

    const Wrapped = function TfsQuickSettingsPanel(props) {
      const output = Original(props);
      const [qs, send] = useQuickSettings();
      const { audioRows, appRows, displayRows } = rowsFor(qs, send);
      const t = text();
      return mergeSettingsSections(React, output, {
        audioTitle: steamText("#QuickAccess_Tab_Settings_Section_Audio_Title", t.audio),
        audioRows,
        appTitle: t.appVolume,
        appRows,
        displayTitle: steamText("#Settings_Page_Display", t.display),
        displayRows,
        Section,
      });
    };
    settingsPanelCache.set(Original, Wrapped);
    return Wrapped;
  }

  // -----------------------------------------------------------------------
  // Game profile page (/tfs/game-profile/:key): edit a profile without the game running
  // -----------------------------------------------------------------------

  function closeQuickAccess() {
    try {
      const menus = window.SteamUIStore?.m_WindowStore?.GamepadUIMainWindowInstance?.m_MenuStore;
      for (const name of ["CloseSideMenus", "CloseQuickAccessMenu", "HideSideMenus"]) {
        if (typeof menus?.[name] === "function") { menus[name](); return; }
      }
    } catch {}
  }

  // Context menu support (artwork-surface.js): only on devices with TDP control.
  function refreshProfileSupport() {
    api("/api/perf-tab/state")
      .then((value) => {
        state.profilesSupported = value?.profiles?.supported === true;
        state.profileKeys = new Set((value?.profiles?.saved || []).map((profile) => String(profile.key).toLowerCase()));
      })
      .catch(() => {});
  }

  function openGameProfileForApp(appId, title) {
    const id = Number(appId) || 0;
    if (!id || !state.profilesSupported) return false;
    api("/api/perf-tab/add-app-profile", { appId: id, title: String(title || "") })
      .then((detail) => {
        state.profileKeys?.add(String(detail.key).toLowerCase());
        openProfilePage(detail.key);
      })
      .catch((reason) => console.warn("[TFS Performance] game profile failed", reason));
    return true;
  }

  function openProfilePage(key) {
    const core = window.__tfs;
    if (!key || !core?.routes?.navigate || !state.profileRouteRemover) return false;
    closeQuickAccess();
    return core.routes.navigate(`/tfs/game-profile/${encodeURIComponent(key)}`) !== false;
  }

  function createProfilePage(core) {
    const React = core.React;
    const h = React.createElement;
    const ui = core.ui || {};
    const Focusable = ui.Focusable;

    function Row({ children }) {
      return h("div", { style: { minWidth: 0 } }, children);
    }

    function Heading({ children }) {
      return h("div", {
        style: { margin: "22px 0 6px", fontSize: "13px", fontWeight: 700, letterSpacing: ".08em", textTransform: "uppercase", color: "rgba(255,255,255,.55)" },
      }, children);
    }

    function Choice({ label, value, options, onChange }) {
      if (!ui.DropdownItem) return null;
      const selected = Math.max(0, options.findIndex(([data]) => data === value));
      return h(Row, null, h(ui.DropdownItem, {
        label,
        rgOptions: options.map(([, optionLabel], index) => ({ data: index, label: optionLabel })),
        selectedOption: selected,
        menuLabel: label,
        layout: "below",
        childrenContainerWidth: "max",
        contextMenuPositionOptions: { bMatchWidth: false },
        onChange: (option) => onChange(options[Number(option?.data ?? 0)][0]),
      }));
    }

    function Watts({ label, value, min, max, onChange }) {
      if (!ui.SliderField) return null;
      return h(Row, null, h(ui.SliderField, {
        label, value, min, max, step: 1, showValue: true, editableValue: false,
        renderValue: (watts) => `${Math.round(watts)} W`,
        onChange,
      }));
    }

    return function TfsGameProfilePage(props) {
      const key = decodeURIComponent(String(props?.match?.params?.key || ""));
      const [detail, setDetail] = React.useState(null);
      const [error, setError] = React.useState("");
      const [notice, setNotice] = React.useState("");
      const timer = React.useRef(0);
      const t = text();

      React.useEffect(() => {
        let alive = true;
        api(`/api/perf-tab/profile?key=${encodeURIComponent(key)}`)
          .then((value) => { if (alive) { setDetail(value); setError(""); } })
          .catch((reason) => { if (alive) setError(String(reason?.message || reason)); });
        return () => { alive = false; window.clearTimeout(timer.current); };
      }, [key]);

      // Every change saves on its own after a short pause, so there is no save button to reach.
      const change = (patch) => {
        setDetail((current) => {
          const next = { ...current, ...patch };
          window.clearTimeout(timer.current);
          timer.current = window.setTimeout(() => {
            api("/api/perf-tab/profile", profileUpdateBody(next))
              .then((value) => { setDetail(value); setNotice(t.profileSaved); })
              .catch((reason) => setNotice(String(reason?.message || reason)));
          }, 400);
          return next;
        });
      };

      const remove = () => {
        api("/api/perf-tab/delete-profile", { value: key })
          .then(() => { if (!core.routes.back?.()) window.history.back(); })
          .catch((reason) => setNotice(String(reason?.message || reason)));
      };

      const page = { padding: "56px 36px 40px", height: "100%", boxSizing: "border-box", overflowY: "auto", color: "#fff" };
      if (error) return h("div", { style: page }, h("h2", null, t.profiles), h("p", null, error));
      if (!detail) return h("div", { style: page }, h("h2", null, t.profiles), h("p", { style: { opacity: 0.7 } }, t.loading));

      const options = profileChoiceOptions(detail, t);
      const column = (children) => (Focusable
        ? h(Focusable, { "flow-children": "column", style: { minWidth: 0 } }, ...children)
        : h("div", { style: { minWidth: 0 } }, ...children));

      const left = [
        h(Heading, { key: "tdp" }, t.profileTdp),
        h(Watts, { key: "ac", label: t.pluggedIn, value: detail.acWatts, min: detail.minimumWatts, max: detail.maximumAcWatts, onChange: (value) => change({ acWatts: value }) }),
        h(Watts, { key: "battery", label: t.battery, value: detail.batteryWatts, min: detail.minimumWatts, max: detail.maximumBatteryWatts, onChange: (value) => change({ batteryWatts: value }) }),
        h(Heading, { key: "display" }, t.profileDisplay),
        h(Choice, { key: "fps", label: steamText("#QuickAccess_Tab_Perf_LimitFrameRate", t.frameLimit), value: detail.frameLimit ?? null, options: options.frameLimit, onChange: (value) => change({ frameLimit: value }) }),
        options.refresh.length > 1
          ? h(Choice, { key: "hz", label: steamText("#QuickAccess_Tab_Perf_RefreshRate", t.refresh), value: detail.refreshRate ?? null, options: options.refresh, onChange: (value) => change({ refreshRate: value }) })
          : null,
        h(Choice, { key: "rtss", label: "RTSS", value: detail.rtssLevel ?? null, options: options.rtss, onChange: (value) => change({ rtssLevel: value }) }),
      ].filter(Boolean);

      const right = [
        h(Heading, { key: "system" }, t.profileSystem),
        h(Choice, { key: "power", label: t.powerMode, value: detail.powerMode ?? null, options: options.powerMode, onChange: (value) => change({ powerMode: value }) }),
        detail.cpuBoostSupported
          ? h(Choice, { key: "boost", label: t.cpuBoost, value: detail.cpuBoost ?? null, options: options.cpuBoost, onChange: (value) => change({ cpuBoost: value }) })
          : null,
        h(Heading, { key: "manage" }, t.profileManage),
        h("div", { key: "hint", style: { opacity: 0.65, fontSize: "14px", margin: "2px 0 10px", lineHeight: 1.4 } }, t.profileGlobalHint),
        ui.DialogButton ? h(Row, { key: "delete" }, h(ui.DialogButton, { onClick: remove, style: { maxWidth: "260px" } }, t.deleteProfile)) : null,
        notice ? h("div", { key: "notice", style: { opacity: 0.7, fontSize: "14px", marginTop: "10px" } }, notice) : null,
      ].filter(Boolean);

      return h("div", { style: page },
        h("div", { style: { display: "flex", alignItems: "baseline", gap: "14px", marginBottom: "6px" } },
          h("h2", { style: { margin: 0, fontSize: "26px" } }, detail.title || t.profiles),
          h("div", { style: { opacity: 0.6, fontSize: "16px" } }, detail.active ? `${t.profiles} · ${t.profileActive}` : t.profiles)),
        h(Focusable || "div", {
          ...(Focusable ? { "flow-children": "row" } : {}),
          style: { display: "grid", gridTemplateColumns: "minmax(0, 1fr) minmax(0, 1fr)", gap: "32px", alignItems: "start" },
        }, column(left), column(right)));
    };
  }

  async function registerProfileRoute(core) {
    if (state.profileRouteRemover || !core?.routes?.add) return;
    if (core.routes.has?.(profileRoute)) core.routes.remove(profileRoute);
    state.profileRouteRemover = core.routes.add(profileRoute, createProfilePage(core), { name: "TFS Game Profile", exact: true });
  }

  async function install() {
    window.clearTimeout(state.timer);
    if (state.destroyed || state.remover) return;
    const core = window.__tfs;
    if (!core?.qam?.patchTab) {
      state.timer = window.setTimeout(install, 2000);
      return;
    }
    try {
      await core.whenReady;
      if (state.destroyed || state.remover || !core.React) return;
      try { await registerProfileRoute(core); } catch (error) { console.warn("[TFS Performance] profile route failed", error); }
      const Panel = createSection(core);
      state.remover = core.qam.patchTab(perfTabKey, (tab) => ({
        ...tab,
        panel: core.React.createElement(Panel, { original: tab.panel }),
      }), { id: patchId });
      // Steam's Quick Settings tab: render Steam's own panel and add our rows
      // into its sections, so Audio and Display need no separate TFS pages.
      state.settingsRemover = core.qam.patchTab(settingsTabKey, (tab) => {
        const Original = tab?.panel?.type;
        if (typeof Original !== "function") return tab;
        return { ...tab, panel: core.React.createElement(wrapSettingsPanel(core, Original), tab.panel.props) };
      }, { id: "tfs-quick-settings" });
    } catch (error) {
      console.warn("[TFS Performance] patch failed", error);
      state.timer = window.setTimeout(install, 10000);
    }
  }

  function loadLanguage() {
    try {
      Promise.resolve(window.SteamClient?.Settings?.GetCurrentLanguage?.()).then((language) => {
        if (typeof language === "string" && language) state.language = language.toLowerCase();
      }).catch(() => {});
    } catch {}
  }

  function destroy() {
    state.destroyed = true;
    window.clearTimeout(state.timer);
    window.clearInterval(state.supportTimer);
    try { state.remover?.(); } catch {}
    try { state.settingsRemover?.(); } catch {}
    try { state.profileRouteRemover?.(); } catch {}
    state.remover = null;
    state.settingsRemover = null;
    state.profileRouteRemover = null;
  }

  window.__tfsPerfTab = {
    version,
    refresh: () => { if (!state.remover) void install(); },
    openProfile: openProfilePage,
    hasGameProfiles: () => state.profilesSupported === true && Boolean(state.profileRouteRemover),
    hasGameProfileForApp: (appId) => Boolean(state.profileKeys?.has(steamProfileKey(appId))),
    gameProfileMenuLabel: (exists) => (exists ? text().contextMenuEdit : text().contextMenuNew),
    openGameProfileForApp,
    destroy,
    internals,
  };
  loadLanguage();
  refreshProfileSupport();
  state.supportTimer = window.setInterval(refreshProfileSupport, 30000);
  void install();
  return "injected";
})();
