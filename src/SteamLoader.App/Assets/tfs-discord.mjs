// Discord Web as a native Big Picture page.
//
// Runs as an ES module in Steam's SharedJSContext on top of tfs-core.mjs.
// discord.com lives in a Steam BrowserView created through Big Picture's own
// window (GamepadUIMainWindowInstance.CreateBrowserView), rendered with
// Steam's own browser host component. That gives Discord exactly what Steam's
// built-in web browser gets: Steam Input's web-browser action set (right
// stick = mouse, RT = click, LT = right click, left stick = scroll) and
// Steam's on-screen keyboard for <input>/<textarea>. Discord's message box is
// contenteditable, which Steam ignores, so clicks into any text field are
// reported by the page helper and open the keyboard through runtime.keyboard.
//
// On top of that the page adds ORBIT's Discord controls: A clicks at the
// cursor, B closes Discord's popups first, X mutes, Y opens the quick
// switcher, LB/RB switch servers, the D-pad switches channels, View deafens
// and Menu opens the Discord menu. Those reach the page through the TFS
// backend (DevTools input), because Steam has no input API for BrowserViews.
//
// The BrowserView outlives the page: leaving with B keeps voice running in
// the background; "Close Discord" in the menu ends it.

const ROUTE = "/tfs/discord";
const BROWSER_KEY = Symbol.for("tfs.discord.browser");
const BROWSER_NAME = "TFSDiscord";
const HOME_URL = "https://discord.com/app";
const STATUS_POLL_MS = 2000;
const REPEAT_GAP_MS = 140;

// Steam's GamepadButton enum (checked against Steam's pR export).
export const BUTTON = Object.freeze({
  OK: 1,
  CANCEL: 2,
  SECONDARY: 3,
  OPTIONS: 4,
  BUMPER_LEFT: 5,
  BUMPER_RIGHT: 6,
  TRIGGER_LEFT: 7,
  TRIGGER_RIGHT: 8,
  DIR_UP: 9,
  DIR_DOWN: 10,
  DIR_LEFT: 11,
  DIR_RIGHT: 12,
  SELECT: 13,
  START: 14,
  RSTICK_CLICK: 16,
  // Back grips (Steam Controller 2 / Steam Deck: L4, L5, R4, R5).
  REAR_LEFT_UPPER: 23,
  REAR_LEFT_LOWER: 24,
  REAR_RIGHT_UPPER: 25,
  REAR_RIGHT_LOWER: 26,
  // Never consumed: Steam opens its main menu / Quick Access with these.
  STEAM_GUIDE: 27,
  STEAM_QUICK_MENU: 28,
});

/** Buttons the page maps to Discord shortcuts (the rest stays with Steam). */
export const BUTTON_ACTIONS = Object.freeze({
  [BUTTON.BUMPER_LEFT]: "previous-server",
  [BUTTON.BUMPER_RIGHT]: "next-server",
  [BUTTON.DIR_UP]: "previous-channel",
  [BUTTON.DIR_DOWN]: "next-channel",
  [BUTTON.DIR_LEFT]: "previous-unread",
  [BUTTON.DIR_RIGHT]: "next-unread",
  [BUTTON.SELECT]: "toggle-deafen",
  // Steam Controller 2 grips: scroll the chat and jump/mark without the mouse.
  [BUTTON.REAR_LEFT_UPPER]: "page-up",
  [BUTTON.REAR_RIGHT_UPPER]: "page-down",
  [BUTTON.REAR_LEFT_LOWER]: "mark-read",
  [BUTTON.REAR_RIGHT_LOWER]: "jump-to-present",
});

/** Buttons that click at the cursor. Trackpad clicks stay with Steam's own mouse mapping. */
export const BUTTON_CLICKS = Object.freeze({
  [BUTTON.RSTICK_CLICK]: "right",
});

/** Discord app paths TFS opens (mirrors DiscordWebBridge.NormalizeAppPath). */
export function buildDiscordPath({ guildId = "", channelId = "", userId = "", path = "" } = {}) {
  const snowflake = (value) => /^\d{16,32}$/.test(String(value || ""));
  if (snowflake(guildId)) {
    return snowflake(channelId) ? `/channels/${guildId}/${channelId}` : `/channels/${guildId}`;
  }
  if (snowflake(userId)) {
    return `/users/${userId}`;
  }
  if (path === "/channels/@me" || path === "/app") {
    return path;
  }
  return "";
}

function apiUrl(path) {
  const base = globalThis.__steamLoaderApiBase || "";
  return new URL(String(path).replace(/^\//, ""), base).toString();
}

async function request(path, body) {
  const response = await fetch(apiUrl(path), {
    cache: "no-store",
    method: body === undefined ? "GET" : "POST",
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const payload = await response.json().catch(() => ({}));
  if (!response.ok && response.status !== 409) {
    throw new Error(payload?.message || `Request failed (${response.status}).`);
  }
  return payload;
}

// ---------------------------------------------------------------------------
// BrowserView lifetime (shared across tfs-discord updates)
// ---------------------------------------------------------------------------

function getShared() {
  let shared = globalThis[BROWSER_KEY];
  if (!shared || typeof shared !== "object") {
    shared = { browser: null, createdAt: 0 };
    try {
      Object.defineProperty(globalThis, BROWSER_KEY, { value: shared, configurable: true, writable: true });
    } catch {
      globalThis[BROWSER_KEY] = shared;
    }
  }
  return shared;
}

function getMainWindow() {
  try {
    return globalThis.SteamUIStore?.m_WindowStore?.GamepadUIMainWindowInstance || null;
  } catch {
    return null;
  }
}

export function isBrowserAlive(browser) {
  try {
    return Boolean(browser && typeof browser.GetBrowser === "function" && browser.GetBrowser());
  } catch {
    return false;
  }
}

function currentBrowser() {
  const shared = getShared();
  return isBrowserAlive(shared.browser) ? shared.browser : null;
}

function ensureBrowser(initialUrl = HOME_URL) {
  const existing = currentBrowser();
  if (existing) {
    return existing;
  }
  const mainWindow = getMainWindow();
  if (typeof mainWindow?.CreateBrowserView !== "function") {
    throw new Error("Big Picture is not ready for Discord yet.");
  }
  const shared = getShared();
  // Same call Steam uses for its own external web page: Big Picture's
  // navigation controller and virtual keyboard come with it.
  shared.browser = mainWindow.CreateBrowserView(BROWSER_NAME, {
    bPreventCloseFromJavascript: true,
  });
  shared.createdAt = Date.now();
  // Loaded through Steam's wrapper (like its own pages) so loading state,
  // history and the gamepad support level are tracked from the first request.
  shared.browser.LoadURL(initialUrl);
  return shared.browser;
}

// True while the Discord page shows the BrowserView (not the TFS menu).
const pageState = { visible: false };

/**
 * Opens ("show") or closes ("hide") Steam's on-screen keyboard for Discord.
 * Steam only opens it by itself for <input>/<textarea>; Discord's message
 * box is contenteditable, so the page helper (DiscordWebBridge) reports text
 * field clicks and this uses the BrowserView's own keyboard reference, the
 * same one Steam's node-has-focus handler calls. Typed text then reaches the
 * page through Steam's normal keyboard path.
 */
export function setSteamKeyboard(browser, kind, visible = pageState.visible) {
  if (!visible || (kind !== "show" && kind !== "hide")) {
    return false;
  }
  const keyboard = browser?.m_refKeyboard;
  const method = kind === "show" ? keyboard?.ShowVirtualKeyboard : keyboard?.HideVirtualKeyboard;
  if (typeof method !== "function") {
    return false;
  }
  try {
    method.call(keyboard);
    return true;
  } catch {
    return false;
  }
}

function destroyBrowser() {
  const shared = getShared();
  const browser = shared.browser;
  shared.browser = null;
  try {
    browser?.GetBrowser?.()?.SetVisible?.(false);
  } catch {
    // ignore
  }
  try {
    browser?.Destroy?.();
  } catch {
    // ignore
  }
}

// ---------------------------------------------------------------------------
// Page status (voice, sign-in), polled only while someone looks at it
// ---------------------------------------------------------------------------

function createStatusStore() {
  const listeners = new Set();
  let timer = 0;
  let watchers = 0;
  const store = {
    status: null,
    error: "",
    async refresh() {
      if (!currentBrowser()) {
        store.status = null;
        store.notify();
        return null;
      }
      try {
        store.status = await request("api/discord/web/state");
        store.error = "";
      } catch (error) {
        store.error = String(error?.message || error);
      }
      store.notify();
      return store.status;
    },
    notify() {
      for (const listener of [...listeners]) {
        try {
          listener(store.status);
        } catch {
          // ignore
        }
      }
    },
    watch(listener) {
      listeners.add(listener);
      watchers += 1;
      if (watchers === 1) {
        void store.refresh();
        timer = setInterval(() => void store.refresh(), STATUS_POLL_MS);
      }
      return () => {
        listeners.delete(listener);
        watchers = Math.max(0, watchers - 1);
        if (watchers === 0) {
          clearInterval(timer);
          timer = 0;
        }
      };
    },
    stop() {
      clearInterval(timer);
      timer = 0;
      listeners.clear();
      watchers = 0;
    },
  };
  return store;
}

export function describeVoice(status) {
  if (!status?.running) {
    return "";
  }
  if (status.signedIn === false) {
    return "Sign in to Discord";
  }
  if (!status.voiceConnected) {
    return status.muted ? "Muted" : "";
  }
  if (status.deafened) {
    return "In voice · Deafened";
  }
  return status.muted ? "In voice · Muted" : "In voice";
}

// ---------------------------------------------------------------------------
// Styles
// ---------------------------------------------------------------------------

const DISCORD_CSS = `
.tfs-discord { position: absolute; inset: 0; display: flex; flex-direction: column; box-sizing: border-box;
  padding: 58px 0 50px; background: #0e141b; color: #dcdedf; font-family: "Motiva Sans", Arial, sans-serif; }
.tfs-discord-bar { flex: 0 0 auto; display: flex; align-items: center; gap: 14px; padding: 0 2.4vw 10px; min-height: 42px; }
.tfs-discord-logo { width: 34px; height: 34px; border-radius: 10px; display: grid; place-items: center;
  background: linear-gradient(135deg, #5865f2, #4752c4); box-shadow: 0 4px 14px rgba(88, 101, 242, .35); flex: 0 0 auto; }
.tfs-discord-logo svg { width: 22px; height: 22px; }
.tfs-discord-title { display: flex; flex-direction: column; min-width: 0; }
.tfs-discord-eyebrow { font-size: 11px; letter-spacing: .08em; text-transform: uppercase; color: #8b929a; }
.tfs-discord-name { font-size: 20px; font-weight: 700; color: #fff; line-height: 1.1; }
.tfs-discord-chip { padding: 5px 11px; border-radius: 999px; font-size: 13px; font-weight: 600;
  background: rgba(88, 101, 242, .18); color: #c9cdfb; white-space: nowrap; }
.tfs-discord-chip.is-voice { background: rgba(35, 165, 90, .2); color: #8de0ad; }
.tfs-discord-chip.is-muted { background: rgba(242, 63, 67, .18); color: #ffadaf; }
.tfs-discord-hints { margin-left: auto; display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 6px 14px;
  font-size: 12px; color: #8b929a; max-width: 62%; }
.tfs-discord-hints b { color: #dcdedf; font-weight: 600; }
.tfs-discord-stage { position: relative; flex: 1 1 auto; min-height: 0; margin: 0 1.2vw; border-radius: 10px; overflow: hidden;
  background: #313338; outline: 2px solid transparent; transition: outline-color .15s; }
.tfs-discord-stage.gpfocuswithin, .tfs-discord-stage:focus-within { outline-color: rgba(26, 159, 255, .55); }
.tfs-discord-host { position: absolute; inset: 0; }
.tfs-discord-host > div { position: absolute; inset: 0; }
.tfs-discord-loading { position: absolute; inset: 0; display: grid; place-items: center; color: #b5bac1; font-size: 16px; }
.tfs-discord-menu { position: absolute; inset: 0; display: flex; align-items: flex-start; justify-content: center; padding: 4vh 0;
  background: linear-gradient(180deg, #1b2838 0%, #0e141b 100%); overflow-y: auto; }
.tfs-discord-menu-list { width: min(520px, 90%); display: flex; flex-direction: column; gap: 8px; }
.tfs-discord-menu-title { font-size: 22px; font-weight: 700; color: #fff; margin: 0 0 6px; }
.tfs-discord-menu-note { font-size: 13px; color: #8b929a; margin: 4px 0 10px; line-height: 1.4; }
.tfs-discord-menu-list .DialogButton { text-align: left; }
.tfs-discord-error { margin: 0 2.4vw 8px; color: #ffadaf; font-size: 13px; }
`;

const DISCORD_MARK = '<svg viewBox="0 0 24 24" fill="#fff" aria-hidden="true"><path d="M19.6 5.2A16.6 16.6 0 0 0 15.5 4l-.5 1a15.3 15.3 0 0 0-6 0L8.5 4a16.6 16.6 0 0 0-4.1 1.2C1.8 9.1 1.1 12.9 1.4 16.6a16.8 16.8 0 0 0 5.1 2.6l1.1-1.8a10.9 10.9 0 0 1-1.7-.8l.4-.3a11.9 11.9 0 0 0 11.4 0l.4.3a10.9 10.9 0 0 1-1.7.8l1.1 1.8a16.7 16.7 0 0 0 5.1-2.6c.4-4.3-.7-8-3-11.4ZM8.7 14.3c-1 0-1.8-.9-1.8-2s.8-2 1.8-2 1.8.9 1.8 2-.8 2-1.8 2Zm6.6 0c-1 0-1.8-.9-1.8-2s.8-2 1.8-2 1.8.9 1.8 2-.8 2-1.8 2Z"/></svg>';

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

function createDiscordPage(core, statusStore) {
  const React = core.React || core.react?.getReact?.();
  const h = React.createElement;
  const ui = core.ui;
  let steamBrowserHost;

  function getSteamBrowserHost() {
    if (steamBrowserHost === undefined) {
      // Steam's own browser host (the one its store/external web pages use):
      // positions the BrowserView, focus, web-browser action set, keyboard.
      steamBrowserHost = core.webpack?.findExportBySource?.(
        "displayURLBar",
        "allowUnderlay",
        "BExternalTriggeredLoad",
        "classNameContainer",
      )?.value || null;
    }
    return steamBrowserHost;
  }

  function useStatus() {
    const [status, setStatus] = React.useState(statusStore.status);
    React.useEffect(() => statusStore.watch(setStatus), []);
    return status;
  }

  function useWindowRevision(ref) {
    // Steam's host only re-measures when it re-renders; remount it after the
    // Big Picture window changed size so the BrowserView follows.
    const [revision, setRevision] = React.useState(0);
    React.useEffect(() => {
      const view = ref.current?.ownerDocument?.defaultView;
      if (!view) {
        return undefined;
      }
      let timer = 0;
      const onResize = () => {
        clearTimeout(timer);
        timer = setTimeout(() => setRevision((value) => value + 1), 250);
      };
      view.addEventListener("resize", onResize);
      return () => {
        clearTimeout(timer);
        view.removeEventListener("resize", onResize);
      };
    }, [ref]);
    return revision;
  }

  /** Used only when Steam's host component cannot be found after an update. */
  function FallbackHost({ browser }) {
    const ref = React.useRef(null);
    React.useLayoutEffect(() => {
      const view = browser?.GetBrowser?.();
      const element = ref.current;
      if (!view || !element) {
        return undefined;
      }
      const place = () => {
        const rect = element.getBoundingClientRect();
        view.SetBounds(Math.round(rect.left), Math.round(rect.top), Math.round(rect.width), Math.round(rect.height));
      };
      place();
      view.SetVisible(true);
      view.SetFocus?.(true);
      let actionSet = false;
      try {
        globalThis.SteamClient?.Input?.SetWebBrowserActionset?.(true);
        actionSet = true;
      } catch {
        // optional
      }
      const observer = new ResizeObserver(place);
      observer.observe(element);
      return () => {
        observer.disconnect();
        view.SetVisible(false);
        if (actionSet) {
          try {
            globalThis.SteamClient?.Input?.SetWebBrowserActionset?.(false);
          } catch {
            // ignore
          }
        }
      };
    }, [browser]);
    return h(ui.Focusable, { className: "tfs-discord-host", focusable: true, noFocusRing: true, autoFocus: true },
      h("div", { ref, style: { position: "absolute", inset: 0 } }));
  }

  function BrowserHost({ browser }) {
    const ref = React.useRef(null);
    const revision = useWindowRevision(ref);
    const Host = getSteamBrowserHost();
    return h(
      "div",
      { className: "tfs-discord-host", ref },
      Host
        ? h(Host, {
            key: revision,
            browser,
            visible: true,
            autoFocus: true,
            external: true,
            displayURLBar: false,
            hideForModals: true,
            allowUnderlay: false,
            className: "tfs-discord-browser",
          })
        : h(FallbackHost, { key: revision, browser }),
    );
  }

  function MenuButton({ label, onClick, autoFocus = false, disabled = false }) {
    return h(ui.DialogButton, { onClick, autoFocus, disabled }, label);
  }

  function DiscordMenu({ status, onClose, onAction, onLeave, onCloseDiscord, onReload }) {
    return h(
      "div",
      { className: "tfs-discord-menu" },
      h(
        ui.Focusable,
        { className: "tfs-discord-menu-list", "flow-children": "column" },
        h("h2", { className: "tfs-discord-menu-title" }, "Discord"),
        h("div", { className: "tfs-discord-menu-note" },
          "Right stick (or the right trackpad on a Steam Controller) moves the mouse, RT or A clicks, LT or a right stick click right-clicks and the left stick scrolls. Clicking a text field opens Steam's keyboard. Back grips: L4/R4 scroll the chat, L5 marks it read, R5 jumps to the newest messages."),
        h(MenuButton, { label: "Back to Discord", onClick: onClose, autoFocus: true }),
        h(MenuButton, { label: status?.muted ? "Unmute microphone" : "Mute microphone", onClick: () => onAction("toggle-mute") }),
        h(MenuButton, { label: status?.deafened ? "Undeafen" : "Deafen", onClick: () => onAction("toggle-deafen") }),
        h(MenuButton, { label: "Quick switcher (find a server, channel or friend)", onClick: () => onAction("quick-switcher") }),
        h(MenuButton, { label: "Direct messages", onClick: () => onAction("navigate:/channels/@me") }),
        h(MenuButton, { label: "Mark channel as read", onClick: () => onAction("mark-read") }),
        h(MenuButton, { label: "Jump to newest messages", onClick: () => onAction("jump-to-present") }),
        h(MenuButton, { label: "Reload Discord", onClick: onReload }),
        h(MenuButton, { label: "Leave page (Discord keeps running)", onClick: onLeave }),
        h(MenuButton, { label: status?.voiceConnected ? "Close Discord and leave voice" : "Close Discord", onClick: onCloseDiscord }),
      ),
    );
  }

  function DiscordPage() {
    const ref = React.useRef(null);
    const [browser, setBrowser] = React.useState(() => {
      try {
        return ensureBrowser();
      } catch {
        return null;
      }
    });
    const [error, setError] = React.useState(browser ? "" : "Big Picture is not ready for Discord yet.");
    const [menuOpen, setMenuOpen] = React.useState(false);
    const status = useStatus();
    const lastPress = React.useRef(new Map());
    const backBusy = React.useRef(false);

    React.useEffect(() => {
      if (browser) {
        return undefined;
      }
      const timer = setInterval(() => {
        try {
          setBrowser(ensureBrowser());
          setError("");
        } catch {
          // keep waiting
        }
      }, 1000);
      return () => clearInterval(timer);
    }, [browser]);

    React.useEffect(() => {
      pageState.visible = Boolean(browser) && !menuOpen;
      return () => {
        pageState.visible = false;
      };
    }, [browser, menuOpen]);

    const run = React.useCallback((action) => {
      if (action.startsWith("navigate:")) {
        void request("api/discord/web/navigate", { path: action.slice("navigate:".length) })
          .catch((reason) => setError(String(reason?.message || reason)));
        return;
      }
      void request("api/discord/web/action", { action })
        .then(() => setTimeout(() => void statusStore.refresh(), 250))
        .catch((reason) => setError(String(reason?.message || reason)));
    }, []);

    const leave = React.useCallback(() => {
      setMenuOpen(false);
      core.routes.back();
    }, []);

    const consume = (event) => {
      try {
        event?.preventDefault?.();
        event?.stopPropagation?.();
      } catch {
        // ignore
      }
      return true;
    };

    const onCancelButton = (event) => {
      consume(event);
      if (menuOpen) {
        setMenuOpen(false);
        return true;
      }
      if (backBusy.current) {
        return true;
      }
      backBusy.current = true;
      // Discord's popups close first; with nothing open B leaves the page.
      request("api/discord/web/back", {})
        .then((result) => {
          if (!result?.handled) {
            core.routes.back();
          }
        })
        .catch(() => core.routes.back())
        .finally(() => {
          backBusy.current = false;
        });
      return true;
    };

    const onButtonDown = (event) => {
      if (menuOpen) {
        return;
      }
      const button = Number(event?.detail?.button);
      const action = BUTTON_ACTIONS[button];
      const click = BUTTON_CLICKS[button];
      if (!action && !click) {
        return;
      }
      consume(event);
      const now = Date.now();
      if (now - (lastPress.current.get(button) || 0) < REPEAT_GAP_MS) {
        return;
      }
      lastPress.current.set(button, now);
      if (click) {
        void request("api/discord/web/click", { button: click }).catch(() => {});
        return;
      }
      run(action);
    };

    const stageProps = menuOpen
      ? { onCancelButton, onCancelActionDescription: "Back to Discord" }
      : {
          onButtonDown,
          onOKButton: (event) => {
            consume(event);
            void request("api/discord/web/click", { button: "left" }).catch(() => {});
            return true;
          },
          onCancelButton,
          onSecondaryButton: (event) => {
            consume(event);
            run("toggle-mute");
            return true;
          },
          onOptionsButton: (event) => {
            consume(event);
            run("quick-switcher");
            return true;
          },
          onMenuButton: (event) => {
            consume(event);
            setMenuOpen(true);
            return true;
          },
          onOKActionDescription: "Click",
          onCancelActionDescription: "Back",
          onSecondaryActionDescription: status?.muted ? "Unmute" : "Mute",
          onOptionsActionDescription: "Quick switcher",
          onMenuActionDescription: "Discord menu",
        };

    const voice = describeVoice(status);
    const chipClass = status?.voiceConnected
      ? (status?.muted || status?.deafened ? "is-muted" : "is-voice")
      : status?.muted ? "is-muted" : "";

    return h(
      "div",
      { className: "tfs-discord", ref },
      h(
        "div",
        { className: "tfs-discord-bar" },
        h("div", { className: "tfs-discord-logo", dangerouslySetInnerHTML: { __html: DISCORD_MARK } }),
        h(
          "div",
          { className: "tfs-discord-title" },
          h("div", { className: "tfs-discord-eyebrow" }, "Tools for Steam"),
          h("div", { className: "tfs-discord-name" }, "Discord"),
        ),
        voice ? h("div", { className: `tfs-discord-chip ${chipClass}` }, voice) : null,
        h(
          "div",
          { className: "tfs-discord-hints" },
          h("span", null, h("b", null, "R-Stick"), " mouse"),
          h("span", null, h("b", null, "A/RT"), " click"),
          h("span", null, h("b", null, "L-Stick"), " scroll"),
          h("span", null, h("b", null, "LB/RB"), " server"),
          h("span", null, h("b", null, "D-pad"), " channel"),
          h("span", null, h("b", null, "View"), " deafen"),
          h("span", null, h("b", null, "L4/R4"), " scroll chat"),
        ),
      ),
      error ? h("div", { className: "tfs-discord-error" }, error) : null,
      h(
        ui.Focusable,
        { className: "tfs-discord-stage", noFocusRing: true, ...stageProps },
        menuOpen
          ? h(DiscordMenu, {
              status,
              onClose: () => setMenuOpen(false),
              onAction: (action) => {
                setMenuOpen(false);
                run(action);
              },
              onLeave: leave,
              onReload: () => {
                setMenuOpen(false);
                try {
                  browser?.Reload?.();
                } catch {
                  // ignore
                }
              },
              onCloseDiscord: () => {
                setMenuOpen(false);
                destroyBrowser();
                setBrowser(null);
                statusStore.status = null;
                statusStore.notify();
                core.routes.back();
              },
            })
          : browser
            ? h(BrowserHost, { browser })
            : h("div", { className: "tfs-discord-loading" }, "Starting Discord…"),
      ),
    );
  }

  return { DiscordPage };
}

// ---------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------

export async function start({ version = "" } = {}) {
  const previous = globalThis.ToolsForSteamDiscord;
  if (previous?.version === version && previous.ready) {
    return previous;
  }
  previous?.deinit?.();

  const runtime = {
    version,
    ready: false,
    open: () => false,
    close: () => false,
    keyboard: () => false,
    isRunning: () => Boolean(currentBrowser()),
    deinit: () => {},
  };
  globalThis.ToolsForSteamDiscord = runtime;

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
    if (globalThis.ToolsForSteamDiscord !== runtime) {
      return runtime;
    }
    const statusStore = createStatusStore();
    const pages = createDiscordPage(core, statusStore);
    const removeCss = core.css?.add?.("tfs-discord", DISCORD_CSS, { targets: ["gamepad"] });
    const removeRoute = core.routes.add(ROUTE, pages.DiscordPage, { name: "TFS Discord" });

    /**
     * Opens Discord Web in Big Picture, optionally on a server, channel or
     * profile ({ guildId, channelId, userId, path }). Returns false when Big
     * Picture cannot host it yet.
     */
    runtime.open = (target = {}) => {
      const path = buildDiscordPath(target || {});
      const running = Boolean(currentBrowser());
      try {
        ensureBrowser(path ? `https://discord.com${path}` : HOME_URL);
      } catch {
        return false;
      }
      if (running && path) {
        // In-app navigation keeps voice and avoids reloading Discord.
        void request("api/discord/web/navigate", { path }).then((result) => {
          if (!result?.success) {
            try {
              currentBrowser()?.LoadURL?.(`https://discord.com${path}`);
            } catch {
              // ignore
            }
          }
        }).catch(() => {});
      }
      return core.routes.navigate(ROUTE) !== false;
    };
    runtime.close = () => {
      const wasRunning = Boolean(currentBrowser());
      destroyBrowser();
      statusStore.status = null;
      statusStore.notify();
      return wasRunning;
    };
    // Called by the backend when the page helper saw a click into a text field.
    runtime.keyboard = (kind) => setSteamKeyboard(currentBrowser(), kind);
    runtime.status = () => statusStore.refresh();
    runtime.action = (action) => request("api/discord/web/action", { action });
    runtime.deinit = () => {
      // The BrowserView stays (voice keeps running across TFS updates).
      statusStore.stop();
      removeRoute();
      removeCss?.();
      runtime.ready = false;
    };
    runtime.ready = true;
    return runtime;
  })();

  return runtime;
}
