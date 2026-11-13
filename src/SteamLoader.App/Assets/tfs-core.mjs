// Tools for Steam core runtime.
//
// Loaded as an ES module into Steam's SharedJSContext (the context that runs
// Steam's React and renders every Gamepad UI window, including the Quick
// Access Menu). It follows the same architecture as Decky Loader: find Steam's
// own components through the webpack module registry, patch their render
// functions with reversible after-patches, append our own Quick Access tab and
// router routes, and isolate everything we render in error boundaries.
//
// This is an independent implementation (Tools for Steam is MIT licensed);
// no Decky Loader code is copied.

export const TFS_CORE_API_VERSION = 1;
export const TFS_QAM_TAB_KEY = 4747;

const CORE_GLOBAL = "__tfs";
const LOG_PREFIX = "[TFS core]";

function log(...args) {
  try {
    console.info(LOG_PREFIX, ...args);
  } catch {
    // ignore
  }
}

function warn(...args) {
  try {
    console.warn(LOG_PREFIX, ...args);
  } catch {
    // ignore
  }
}

// ---------------------------------------------------------------------------
// Webpack module registry
// ---------------------------------------------------------------------------

let cachedRequire = null;
let cachedModules = null;

export function getWebpackRequire(target = globalThis) {
  if (cachedRequire) {
    return cachedRequire;
  }

  const chunk = target.webpackChunksteamui;
  if (!Array.isArray(chunk) || typeof chunk.push !== "function") {
    return null;
  }

  let captured = null;
  try {
    chunk.push([[Symbol("tfs-core")], {}, (require) => {
      captured = require;
    }]);
    chunk.pop();
  } catch (error) {
    warn("Unable to capture Steam's webpack runtime", error);
  }

  cachedRequire = captured;
  return captured;
}

/** All module export objects, resolved once and cached (like Decky's module cache). */
export function getModules({ refresh = false } = {}) {
  if (cachedModules && !refresh) {
    return cachedModules;
  }

  const require = getWebpackRequire();
  if (!require?.m) {
    return [];
  }

  const modules = [];
  for (const id of Object.keys(require.m)) {
    try {
      const exportsObject = require(id);
      if (exportsObject && (typeof exportsObject === "object" || typeof exportsObject === "function")) {
        modules.push({ id, exports: exportsObject });
      }
    } catch {
      // Some modules throw outside their intended window; skip them.
    }
  }

  cachedModules = modules;
  return modules;
}

export function getSourceText(value) {
  try {
    if (typeof value === "function") {
      return Function.prototype.toString.call(value);
    }

    if (value && typeof value === "object") {
      const inner = value.type || value.render;
      if (typeof inner === "function") {
        return Function.prototype.toString.call(inner);
      }

      if (inner && typeof inner === "object" && typeof inner.render === "function") {
        return Function.prototype.toString.call(inner.render);
      }
    }
  } catch {
    // ignore
  }

  return "";
}

export function findModule(predicate) {
  for (const { exports: moduleExports } of getModules()) {
    try {
      if (predicate(moduleExports)) {
        return moduleExports;
      }
    } catch {
      // ignore faulty predicates on exotic exports
    }
  }

  return null;
}

/** Returns { module, key, value } for the first export matching the predicate. */
export function findModuleExport(predicate) {
  for (const { exports: moduleExports } of getModules()) {
    if (!moduleExports || typeof moduleExports !== "object") {
      continue;
    }

    for (const key of Object.keys(moduleExports)) {
      let value;
      try {
        value = moduleExports[key];
      } catch {
        continue;
      }

      try {
        if (value && predicate(value, key)) {
          return { module: moduleExports, key, value };
        }
      } catch {
        // ignore
      }
    }
  }

  return null;
}

export function findExportBySource(...needles) {
  return findModuleExport((value) => {
    const source = getSourceText(value);
    return source && needles.every((needle) => source.includes(needle));
  });
}

// ---------------------------------------------------------------------------
// React helpers
// ---------------------------------------------------------------------------

let cachedReact = null;

export function getReact() {
  if (cachedReact) {
    return cachedReact;
  }

  cachedReact = findModule((candidate) =>
    typeof candidate?.createElement === "function" &&
    typeof candidate?.useState === "function" &&
    typeof candidate?.useEffect === "function" &&
    typeof candidate?.Component === "function");
  return cachedReact;
}

export function getReactFiberKey(element, prefix) {
  if (!element) {
    return null;
  }

  return Object.keys(element).find((key) => key.startsWith(prefix)) || null;
}

/** The root fiber of a React container element (default: the shared context's #root). */
export function getReactRoot(element = globalThis.document?.getElementById?.("root")) {
  if (!element) {
    return null;
  }

  const containerKey = getReactFiberKey(element, "__reactContainer$");
  if (containerKey) {
    const container = element[containerKey];
    return container?.stateNode?.current || container?._internalRoot?.current || container;
  }

  const fiberKey = getReactFiberKey(element, "__reactFiber$");
  return fiberKey ? element[fiberKey] : null;
}

/** Depth-first search over a fiber tree (child/sibling), bounded. */
export function findInFiberTree(root, predicate, limit = 25000) {
  const stack = root ? [root] : [];
  const seen = new Set();
  let visited = 0;
  while (stack.length && visited < limit) {
    const node = stack.pop();
    if (!node || seen.has(node)) {
      continue;
    }

    seen.add(node);
    visited += 1;
    try {
      if (predicate(node)) {
        return node;
      }
    } catch {
      // ignore
    }

    if (node.sibling) {
      stack.push(node.sibling);
    }

    if (node.child) {
      stack.push(node.child);
    }
  }

  return null;
}

/** Search a rendered element tree (props.children), the shape render functions return. */
export function findInReactTree(node, predicate, seen = new Set(), depth = 0) {
  if (!node || typeof node !== "object" || seen.has(node) || depth > 64) {
    return null;
  }

  seen.add(node);
  try {
    if (predicate(node)) {
      return node;
    }
  } catch {
    // ignore
  }

  if (Array.isArray(node)) {
    for (const child of node) {
      const match = findInReactTree(child, predicate, seen, depth + 1);
      if (match) {
        return match;
      }
    }

    return null;
  }

  const props = node.props;
  if (props && typeof props === "object") {
    const fromChildren = findInReactTree(props.children, predicate, seen, depth + 1);
    if (fromChildren) {
      return fromChildren;
    }

    // React portals keep their content on .children of the portal object.
    if (node.children) {
      return findInReactTree(node.children, predicate, seen, depth + 1);
    }

    return null;
  }

  if (node.children) {
    return findInReactTree(node.children, predicate, seen, depth + 1);
  }

  return null;
}

// ---------------------------------------------------------------------------
// Reversible patches
// ---------------------------------------------------------------------------

const PATCH_MARK = Symbol.for("tfs.patch");

function copyFunctionProperties(target, source) {
  for (const key of Reflect.ownKeys(source)) {
    if (key === "prototype" || key === "length" || key === "name" || key === "caller" || key === "arguments") {
      continue;
    }

    try {
      Object.defineProperty(target, key, Object.getOwnPropertyDescriptor(source, key));
    } catch {
      // non-configurable on the wrapper; skip
    }
  }

  try {
    Object.defineProperty(target, "name", { value: source.name, configurable: true });
  } catch {
    // ignore
  }
}

/**
 * Wraps obj[prop] so handler(args, result) runs after the original. The
 * handler may return a replacement result. Returns { unpatch, original }.
 */
export function afterPatch(object, property, handler, { label = "patch" } = {}) {
  const original = object[property];
  if (typeof original !== "function") {
    throw new TypeError(`${label}: ${String(property)} is not a function`);
  }

  const patched = function tfsAfterPatched(...args) {
    let result = original.apply(this, args);
    try {
      const replacement = handler.call(this, args, result);
      if (replacement !== undefined) {
        result = replacement;
      }
    } catch (error) {
      warn(`${label} failed`, error);
    }

    return result;
  };

  copyFunctionProperties(patched, original);
  patched.toString = () => Function.prototype.toString.call(original);
  patched[PATCH_MARK] = { original, label };
  object[property] = patched;

  return {
    original,
    patched,
    unpatch() {
      if (object[property] === patched) {
        object[property] = original;
      }
    },
  };
}

export function getUnpatched(value) {
  let current = value;
  while (typeof current === "function" && current[PATCH_MARK]?.original) {
    current = current[PATCH_MARK].original;
  }

  return current;
}

/**
 * Stable per-type wrapper for a child component found in a render result, so
 * React sees the same component identity on every render (no remounts).
 */
function createTypeWrapperCache(label, afterRender) {
  const cache = new WeakMap();
  return function wrapType(originalType) {
    if (typeof originalType !== "function") {
      return originalType;
    }

    const existing = cache.get(originalType);
    if (existing) {
      return existing;
    }

    const wrapped = function tfsWrappedComponent(...args) {
      const result = originalType.apply(this, args);
      try {
        const replacement = afterRender(args, result);
        return replacement === undefined ? result : replacement;
      } catch (error) {
        warn(`${label} render hook failed`, error);
        return result;
      }
    };
    copyFunctionProperties(wrapped, originalType);
    wrapped.toString = () => Function.prototype.toString.call(originalType);
    wrapped[PATCH_MARK] = { original: originalType, label };
    cache.set(originalType, wrapped);
    return wrapped;
  };
}

// ---------------------------------------------------------------------------
// Error boundary
// ---------------------------------------------------------------------------

let ErrorBoundaryClass = null;

export function getErrorBoundary() {
  if (ErrorBoundaryClass) {
    return ErrorBoundaryClass;
  }

  const React = getReact();
  if (!React) {
    return null;
  }

  const h = React.createElement;
  ErrorBoundaryClass = class TfsErrorBoundary extends React.Component {
    constructor(props) {
      super(props);
      this.state = { error: null };
      this.reset = () => this.setState({ error: null });
    }

    static getDerivedStateFromError(error) {
      return { error };
    }

    componentDidCatch(error, info) {
      warn(`Render error in ${this.props.name || "Tools for Steam"}`, error, info?.componentStack || "");
      try {
        this.props.onError?.(error);
      } catch {
        // ignore
      }
    }

    render() {
      if (!this.state.error) {
        return this.props.children ?? null;
      }

      const message = String(this.state.error?.message || this.state.error || "Unknown error");
      return h(
        "div",
        { className: "tfs-core-error", style: { padding: "16px", lineHeight: 1.4 } },
        h("div", { style: { fontWeight: 600, marginBottom: "6px" } }, `${this.props.name || "Tools for Steam"} hit an error`),
        h("div", { style: { opacity: 0.75, fontSize: "13px", wordBreak: "break-word", marginBottom: "12px" } }, message),
        h(
          "button",
          {
            type: "button",
            className: "DialogButton gpfocus Focusable",
            tabIndex: 0,
            onClick: this.reset,
          },
          "Try again",
        ),
      );
    }
  };

  return ErrorBoundaryClass;
}

// ---------------------------------------------------------------------------
// Quick Access tab hook
// ---------------------------------------------------------------------------

function createQuickAccessStore() {
  const listeners = new Set();
  const store = {
    provider: null,
    title: "Tools for Steam",
    /** Registered by the Quick Access script: { id, getPanel(), getIcon?(), title? }. */
    registerPanel(provider) {
      if (!provider || typeof provider.getPanel !== "function") {
        throw new TypeError("registerPanel requires a provider with getPanel()");
      }

      store.provider = provider;
      if (provider.title) {
        store.title = String(provider.title);
      }

      store.notify();
      return () => {
        if (store.provider === provider) {
          store.provider = null;
          store.notify();
        }
      };
    },
    notify() {
      for (const listener of [...listeners]) {
        try {
          listener();
        } catch (error) {
          warn("Quick Access listener failed", error);
        }
      }
    },
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
  ensureTabPatchApi(store);
  return store;
}

/**
 * Patches for Steam's own Quick Access tabs (e.g. key 5 = Performance).
 * Installed on the store object so the registry survives core updates; an
 * older store gets the API added on the next start().
 */
function ensureTabPatchApi(store) {
  if (!store || typeof store.patchTab === "function") {
    return store;
  }

  // tabKey -> { revision, patchers: Map<id, patcher> }
  const registry = new Map();
  let nextId = 1;
  // original panel (or tab) -> { signature, result }
  let cache = new WeakMap();

  function entryFor(tabKey) {
    let entry = registry.get(tabKey);
    if (!entry) {
      entry = { revision: 0, patchers: new Map() };
      registry.set(tabKey, entry);
    }
    return entry;
  }

  Object.assign(store, {
    tabPatches: registry,
    /** patcher(tab) returns a replacement tab object; returns a remover. */
    patchTab(tabKey, patcher, { id } = {}) {
      if (typeof patcher !== "function") {
        throw new TypeError("patchTab needs a patcher function");
      }
      const patchId = id != null ? String(id) : `tab-patch-${nextId++}`;
      const entry = entryFor(tabKey);
      entry.patchers.set(patchId, patcher);
      entry.revision += 1;
      cache = new WeakMap();
      store.notify();
      return () => store.unpatchTab(tabKey, patchId);
    },
    /** Removes one patch (by id) or every patch of a tab. */
    unpatchTab(tabKey, id) {
      const entry = registry.get(tabKey);
      if (!entry) {
        return false;
      }
      const removed = id == null ? entry.patchers.size > 0 : entry.patchers.delete(String(id));
      if (id == null) {
        entry.patchers.clear();
      }
      if (removed) {
        entry.revision += 1;
        cache = new WeakMap();
        store.notify();
      }
      return removed;
    },
    /** Applies registered patches to Steam's tab array in place (idempotent). */
    applyTabPatches(tabs) {
      if (!Array.isArray(tabs) || registry.size === 0) {
        return;
      }
      for (let index = 0; index < tabs.length; index += 1) {
        const current = tabs[index];
        if (!current || typeof current !== "object") {
          continue;
        }
        const original = current.__tfsOriginal || current;
        const entry = registry.get(original.key);
        if (!entry || entry.patchers.size === 0) {
          if (current.__tfsOriginal) {
            tabs[index] = current.__tfsOriginal;
          }
          continue;
        }
        const signature = `${original.key}:${entry.revision}`;
        if (current.__tfsPatched === signature) {
          continue;
        }
        const cacheKey = original.panel && typeof original.panel === "object" ? original.panel : original;
        const cached = cache.get(cacheKey);
        if (cached?.signature === signature && cached.source === original) {
          tabs[index] = cached.result;
          continue;
        }
        let result = original;
        for (const [patchId, patcher] of entry.patchers) {
          try {
            const replacement = patcher(result);
            if (replacement && typeof replacement === "object") {
              result = replacement;
            }
          } catch (error) {
            warn(`Quick Access tab patch ${patchId} failed`, error);
          }
        }
        if (result !== original) {
          result = { ...result };
          Object.defineProperty(result, "__tfsOriginal", { value: original, enumerable: false });
          Object.defineProperty(result, "__tfsPatched", { value: signature, enumerable: false });
          cache.set(cacheKey, { signature, source: original, result });
          tabs[index] = result;
        }
      }
    },
  });
  return store;
}

function DefaultTabIcon(React) {
  const h = React.createElement;
  return h(
    "svg",
    { viewBox: "0 0 36 36", width: "1em", height: "1em", fill: "none", xmlns: "http://www.w3.org/2000/svg" },
    h("rect", { x: "5", y: "8", width: "26", height: "20", rx: "5", stroke: "currentColor", strokeWidth: "3" }),
    h("path", { d: "M12 18h6M15 15v6M23 16.5h.01M26 19.5h.01", stroke: "currentColor", strokeWidth: "3", strokeLinecap: "round" }),
  );
}

function createTabsHook(core) {
  const React = getReact();
  const h = React.createElement;
  const ErrorBoundary = getErrorBoundary();
  const qam = core.qam;
  const patches = [];

  function TfsQuickAccessTabIcon() {
    const [, rerender] = React.useReducer((value) => value + 1, 0);
    React.useEffect(() => qam.subscribe(rerender), []);
    try {
      const icon = qam.provider?.getIcon?.();
      if (icon) {
        return icon;
      }
    } catch (error) {
      warn("Tab icon failed", error);
    }

    return DefaultTabIcon(React);
  }

  function TfsQuickAccessPanelContent() {
    const [, rerender] = React.useReducer((value) => value + 1, 0);
    React.useEffect(() => qam.subscribe(rerender), []);
    const provider = qam.provider;
    if (!provider) {
      return h(
        "div",
        { className: "tfs-core-loading", style: { padding: "24px 16px", opacity: 0.7 } },
        "Tools for Steam is starting…",
      );
    }

    return provider.getPanel() ?? null;
  }

  function TfsQuickAccessPanel() {
    return h(ErrorBoundary, { name: "Tools for Steam" }, h(TfsQuickAccessPanelContent));
  }

  const tabObject = {
    key: TFS_QAM_TAB_KEY,
    tab: h(TfsQuickAccessTabIcon),
    title: null,
    strTitle: "Tools for Steam",
    panel: h(TfsQuickAccessPanel),
    tfs: true,
  };

  function appendTab(tabs) {
    if (!Array.isArray(tabs)) {
      return;
    }

    tabObject.strTitle = qam.title || "Tools for Steam";
    const index = tabs.findIndex((tab) => tab?.key === TFS_QAM_TAB_KEY);
    if (index === -1) {
      tabs.push(tabObject);
    } else if (tabs[index] !== tabObject && (tabs[index]?.tfs || tabs[index]?.tfsFallback)) {
      // Replace an older core's tab or the Quick Access window's fallback tab.
      tabs[index] = tabObject;
    }

    try {
      qam.applyTabPatches?.(tabs);
    } catch (error) {
      warn("Quick Access tab patches failed", error);
    }
  }

  // The tab list is built inside the QAM menu component that the browser view
  // portals into the Quick Access window; patch it through the browser view's
  // render result, like Decky's tabs hook.
  const wrapMenu = createTypeWrapperCache("QuickAccessMenu", (_args, result) => {
    const tabsNode = findInReactTree(result, (node) =>
      Array.isArray(node?.props?.tabs) && node.props.tabs.some((tab) => tab && typeof tab === "object" && "key" in tab));
    if (tabsNode) {
      appendTab(tabsNode.props.tabs);
    }
  });

  function patchRenderer(renderer, label) {
    const patch = afterPatch(renderer, "type", (_args, result) => {
      const menu = findInReactTree(result, (node) =>
        node?.props && typeof node.props.onFocusNavDeactivated === "function" && typeof node.type === "function");
      if (menu) {
        menu.type = wrapMenu(getUnpatched(menu.type));
      }
    }, { label });
    patches.push(patch);
    return patch;
  }

  const browserView = findModuleExport((value) =>
    typeof value?.type === "function" && getSourceText(value).includes("QuickAccessMenuBrowserView"));
  const embedded = findModuleExport((value) =>
    typeof value?.type === "function" && getSourceText(value).includes("QuickAccessMenuEmbedded"));

  if (!browserView) {
    throw new Error("Steam's Quick Access menu component was not found.");
  }

  const renderers = [[browserView.value, "QuickAccessMenuBrowserView"]];
  if (embedded && embedded.value !== browserView.value) {
    renderers.push([embedded.value, "QuickAccessMenuEmbedded"]);
  }

  for (const [renderer, label] of renderers) {
    patchRenderer(renderer, label);
  }

  // Already mounted Quick Access fibers keep the old type; point them at the
  // patched one so the tab appears without a Steam restart.
  function refreshMountedFibers() {
    const root = getReactRoot();
    if (!root) {
      return 0;
    }

    let updated = 0;
    for (const [renderer] of renderers) {
      const fiber = findInFiberTree(root, (node) => node.elementType === renderer);
      if (fiber) {
        fiber.type = renderer.type;
        if (fiber.alternate) {
          fiber.alternate.type = renderer.type;
        }

        updated += 1;
      }
    }

    return updated;
  }

  const mounted = refreshMountedFibers();
  log(`Quick Access tab hook installed (${renderers.length} renderer(s), ${mounted} mounted).`);

  return {
    tabKey: TFS_QAM_TAB_KEY,
    refreshMountedFibers,
    deinit() {
      for (const patch of patches.splice(0)) {
        patch.unpatch();
      }
    },
  };
}

// ---------------------------------------------------------------------------
// Steam UI components (found by source signature, like Decky's @decky/ui)
// ---------------------------------------------------------------------------

const UI_COMPONENT_SIGNATURES = Object.freeze({
  Focusable: { needles: ['"flow-children"', "onActivate", "focusClassName"] },
  DialogButton: { needles: ["DialogButton", "Secondary"], preferForwardRef: true },
  ToggleField: { needles: ["ToggleField"], preferForwardRef: true },
  Toggle: { needles: ["ToggleRail", "ToggleSwitch"] },
  SliderField: { needles: ["onChangeComplete", "onChangeStart", "validValues"] },
  Dropdown: { needles: ["rgOptions", "selectedOption", "BuildMenu"] },
  // Labelled dropdown row (Decky's DropdownItem). "rgOptions + strDefaultLabel"
  // also matched Steam's audio-device picker, which builds its own options.
  DropdownItem: { needles: ["dropDownControlRef", "explainerTitle"] },
  TextField: { needles: ["bShowClearAction"], preferLong: true },
  // Steam's Field row (label + description + children). Decky calls a plain
  // wrapper "PanelSectionRow"; that one is provided by core.ui itself.
  Field: { needles: ["childrenLayout", "bottomSeparator", "highlightOnFocus"], preferExportKey: "D0" },
  ConfirmModal: { needles: ["strOKButtonText", "strCancelButtonText", "bAlertDialog"] },
});

const uiComponentCache = new Map();

function isForwardRef(value) {
  return value && typeof value === "object" && typeof value.render === "function";
}

/** Finds a Steam component whose render source contains every needle. */
export function findUiComponent(signature) {
  const { needles, preferForwardRef, preferShort, preferLong, preferExportKey } = signature;
  let best = null;
  for (const { exports: moduleExports } of getModules()) {
    if (!moduleExports || typeof moduleExports !== "object") {
      continue;
    }

    for (const key of Object.keys(moduleExports)) {
      let value;
      try {
        value = moduleExports[key];
      } catch {
        continue;
      }

      if (!value || (typeof value !== "function" && !isForwardRef(value) && typeof value?.type !== "function")) {
        continue;
      }

      const source = getSourceText(value);
      if (!source || !needles.every((needle) => source.includes(needle))) {
        continue;
      }

      let score = 0;
      if (preferForwardRef && isForwardRef(value)) score += 1000;
      if (preferExportKey && key === preferExportKey) score += 500;
      if (preferShort) score -= source.length / 100;
      if (preferLong) score += source.length / 100;
      if (!best || score > best.score) {
        best = { value, score };
      }
    }
  }

  return best?.value || null;
}

function createUiLibrary() {
  const ui = {
    find: (needles, options = {}) => findUiComponent({ needles, ...options }),
    // Plain row/section wrappers like Decky's (no Steam Field around children).
    PanelSectionRow: function TfsPanelSectionRow(props) {
      return getReact().createElement("div", { className: "tfs-panel-section-row", style: { padding: "0 0 2px" } }, props?.children);
    },
    PanelSection: function TfsPanelSection(props) {
      const h = getReact().createElement;
      return h(
        "div",
        { className: "tfs-panel-section", style: { padding: "8px 0" } },
        props?.title
          ? h("div", {
              className: "tfs-panel-section-title",
              style: { padding: "0 16px 6px", fontSize: "12px", fontWeight: 700, letterSpacing: "0.06em", textTransform: "uppercase", color: "#b8bcbf" },
            }, props.title)
          : null,
        props?.children,
      );
    },
  };
  for (const name of Object.keys(UI_COMPONENT_SIGNATURES)) {
    Object.defineProperty(ui, name, {
      enumerable: true,
      get() {
        if (!uiComponentCache.has(name)) {
          uiComponentCache.set(name, findUiComponent(UI_COMPONENT_SIGNATURES[name]));
        }
        return uiComponentCache.get(name);
      },
    });
  }
  return ui;
}

// ---------------------------------------------------------------------------
// Steam on-screen keyboard for every Tools for Steam text field
// ---------------------------------------------------------------------------

const TEXT_INPUT_SELECTOR = [
  "textarea",
  "input:not([type])",
  "input[type='text']",
  "input[type='search']",
  "input[type='password']",
  "input[type='url']",
  "input[type='email']",
  "input[type='number']",
  "[contenteditable='true']",
].join(",");

export function getVirtualKeyboardManager() {
  // Resolved through Steam's window store, never a hard-coded webpack module
  // id: those change with every Steam client update (that broke it before).
  try {
    const windowStore = globalThis.SteamUIStore?.m_WindowStore || globalThis.SteamUIStore?.WindowStore;
    for (const instance of [
      windowStore?.GamepadUIMainWindowInstance,
      windowStore?.MainWindowInstance,
      windowStore?.VRGamepadUIMainWindowInstance,
    ]) {
      const manager = instance?.VirtualKeyboardManager;
      if (manager && typeof manager.CreateVirtualKeyboardRef === "function") {
        return manager;
      }
    }
  } catch {
    // fall through
  }
  return null;
}

function createKeyboardHelper() {
  let keyboardRef = null;
  const attached = new WeakSet();

  /** Shows Steam's on-screen keyboard for the given (or focused) field. */
  function show({ element = null } = {}) {
    const manager = getVirtualKeyboardManager();
    if (!manager) {
      return false;
    }

    try {
      manager.AddVirtualKeyboardOwner?.("ToolsForSteam");
    } catch {
      // optional
    }
    try {
      manager.SetDismissOnEnterKey?.(false);
    } catch {
      // optional
    }
    try {
      const rect = element?.getBoundingClientRect?.();
      if (rect && Number.isFinite(rect.left)) {
        manager.SetTextFieldLocation?.(rect.left, rect.top, rect.width, rect.height);
      }
    } catch {
      // optional
    }

    try {
      keyboardRef ||= manager.CreateVirtualKeyboardRef({
        BIsElementValidForInput: () => true,
        strEnterKeyLabel: "Done",
        onKeyboardShow: () => {},
        onKeyboardFullyVisible: () => {},
      });
      keyboardRef.ShowVirtualKeyboard();
      return true;
    } catch (error) {
      keyboardRef = null;
      warn("Steam keyboard failed to open", error);
      return false;
    }
  }

  function isPlainTextInput(element) {
    return Boolean(
      element?.matches?.(TEXT_INPUT_SELECTOR) &&
      !element.readOnly &&
      !element.disabled &&
      !element.closest?.("[data-tfs-native-keyboard='off']"),
    );
  }

  /**
   * Opens Steam's keyboard whenever a text field in this document is
   * activated (A press or click). Used by every Tools for Steam window.
   */
  function attachToDocument(doc) {
    if (!doc || attached.has(doc)) {
      return false;
    }

    attached.add(doc);
    doc.addEventListener("click", (event) => {
      const target = event.target?.closest?.(TEXT_INPUT_SELECTOR);
      if (!isPlainTextInput(target)) {
        return;
      }
      show({ element: target });
    }, true);
    return true;
  }

  return {
    show,
    attachToDocument,
    get available() {
      return Boolean(getVirtualKeyboardManager());
    },
  };
}

// ---------------------------------------------------------------------------
// CSS per Steam window (Decky's injectCssIntoTab, for every TFS window)
// ---------------------------------------------------------------------------

const CSS_ROLES = new Set(["shared", "gamepad", "quickaccess", "menu", "page"]);

function createCssManager() {
  const sheets = new Map();
  const documents = new Map();

  function matches(sheet, role) {
    return sheet.targets.has("all") || sheet.targets.has(role);
  }

  function styleId(id) {
    return `tfs-css-${String(id).replace(/[^a-z0-9_-]/gi, "-")}`;
  }

  function apply(doc, role, id, sheet) {
    try {
      if (!doc?.head || doc.defaultView?.closed) {
        return;
      }
      let style = doc.getElementById(styleId(id));
      if (!matches(sheet, role)) {
        style?.remove();
        return;
      }
      if (!style) {
        style = doc.createElement("style");
        style.id = styleId(id);
        style.setAttribute("data-tfs-css", String(id));
        doc.head.append(style);
      }
      if (style.textContent !== sheet.css) {
        style.textContent = sheet.css;
      }
    } catch {
      // a closed window; pruned on the next pass
    }
  }

  function prune() {
    for (const doc of [...documents.keys()]) {
      try {
        if (!doc.defaultView || doc.defaultView.closed) {
          documents.delete(doc);
        }
      } catch {
        documents.delete(doc);
      }
    }
  }

  return {
    /**
     * Adds or replaces a stylesheet. targets: "all" or any of
     * shared | gamepad | quickaccess | menu | page. Returns a remover.
     */
    add(id, css, { targets = ["all"] } = {}) {
      const sheet = {
        css: String(css ?? ""),
        targets: new Set((Array.isArray(targets) ? targets : [targets]).map(String)),
      };
      sheets.set(String(id), sheet);
      prune();
      for (const [doc, role] of documents) {
        apply(doc, role, id, sheet);
      }
      return () => this.remove(id);
    },
    remove(id) {
      sheets.delete(String(id));
      for (const doc of documents.keys()) {
        try {
          doc.getElementById(styleId(id))?.remove();
        } catch {
          // ignore
        }
      }
    },
    has(id) {
      return sheets.has(String(id));
    },
    /** Called by each TFS window (window-bridge.js) with its role. */
    registerDocument(doc, role = "page") {
      if (!doc) {
        return false;
      }
      const normalizedRole = CSS_ROLES.has(role) ? role : "page";
      documents.set(doc, normalizedRole);
      for (const [id, sheet] of sheets) {
        apply(doc, normalizedRole, id, sheet);
      }
      return true;
    },
    /** One-off injection into a specific document; returns a remover. */
    inject(doc, css, id = `inline-${Math.random().toString(36).slice(2)}`) {
      apply(doc, "all", id, { css: String(css ?? ""), targets: new Set(["all"]) });
      return () => {
        try {
          doc.getElementById(styleId(id))?.remove();
        } catch {
          // ignore
        }
      };
    },
    get documents() {
      prune();
      return [...documents.entries()].map(([doc, role]) => ({ role, title: doc.title || "" }));
    },
  };
}

// ---------------------------------------------------------------------------
// Router hook: real Big Picture routes (Decky's routerHook pattern)
// ---------------------------------------------------------------------------

function getGamepadMainWindow() {
  try {
    return globalThis.SteamUIStore?.m_WindowStore?.GamepadUIMainWindowInstance || null;
  } catch {
    return null;
  }
}

function findRouteComponent() {
  const routeModule = findModule((candidate) =>
    candidate && typeof candidate === "object" &&
    Object.values(candidate).some((value) => value === "router-backstack"));
  if (!routeModule) {
    return null;
  }

  for (const value of Object.values(routeModule)) {
    const source = typeof value === "function" ? getSourceText(value) : "";
    if (source.includes("routePath:") && source.includes(".match?.path")) {
      return value;
    }
  }

  return null;
}

function findGamepadRouterFiber() {
  return findInFiberTree(getReactRoot(), (node) => {
    const type = node?.elementType?.type;
    return typeof type === "function" &&
      getSourceText(getUnpatched(type)).includes("Settings.Root()") &&
      !("loggedIn" in (node.memoizedProps || {}));
  }, 60000);
}

// Revision and subscribers live on the shared route registry, so a routes
// wrapper mounted by an older core instance still re-renders after an update.
const ROUTER_SHARED = Symbol.for("tfs.routes.shared");

function getSharedRouterState(routes) {
  let shared = routes[ROUTER_SHARED];
  if (!shared || !(shared.listeners instanceof Set)) {
    shared = { revision: 0, listeners: new Set() };
    try {
      Object.defineProperty(routes, ROUTER_SHARED, { value: shared, configurable: true });
    } catch {
      // keep the local object
    }
  }
  if (!(shared.patches instanceof Map)) {
    // Patches of Steam's own routes (Decky's routerHook.addPatch):
    // path -> Map<id, handler>. Kept on the shared registry like the routes.
    shared.patches = new Map();
  }
  return shared;
}

/**
 * Runs the registered patches for one of Steam's own Route elements. Each
 * handler gets the (unmutated) element and returns a replacement or nothing.
 */
export function applyRoutePatches(routeElement, patches) {
  const path = routeElement?.props?.path;
  const handlers = typeof path === "string" ? patches?.get(path) : null;
  if (!handlers?.size) {
    return routeElement;
  }

  let current = routeElement;
  for (const [id, handler] of handlers) {
    try {
      const replacement = handler(current);
      if (replacement && typeof replacement === "object") {
        current = replacement;
      }
    } catch (error) {
      warn(`Route patch ${id} for ${path} failed`, error);
    }
  }
  return current;
}

function createRouterHook(routes = new Map()) {
  // Created before Steam's React may be reachable; resolve it on first use.
  const h = (...args) => getReact().createElement(...args);
  const shared = getSharedRouterState(routes);
  const listeners = shared.listeners;
  let renderedRevision = -1;
  let renderedElements = [];
  let routerFiber = null;
  let patch = null;
  let Route = null;
  let RoutesWrapper = null;

  function buildRouteElements() {
    if (renderedRevision === shared.revision) {
      return renderedElements;
    }

    renderedRevision = shared.revision;
    const ErrorBoundary = getErrorBoundary();
    renderedElements = [...routes.entries()].map(([path, entry]) =>
      h(Route, {
        key: `tfs-route:${path}`,
        path,
        exact: entry.exact !== false,
        children: (routeProps) =>
          h(ErrorBoundary, { name: entry.name || path }, h(entry.component, { ...routeProps, ...(entry.props || {}) })),
      }));
    return renderedElements;
  }

  function isRouteList(node) {
    return Array.isArray(node?.props?.children) &&
      node.props.children.some((child) => child?.type === Route);
  }

  // Returns a copy of the router output whose route list (react-router Switch)
  // starts with our routes. Steam's own elements are never mutated, so the
  // wrapper can re-render with the same children as often as it needs to.
  function withRoutes(node, depth = 0) {
    if (!node || typeof node !== "object" || depth > 6) {
      return node;
    }

    const React = getReact();
    if (isRouteList(node)) {
      const steamRoutes = shared.patches.size
        ? node.props.children.map((child) => applyRoutePatches(child, shared.patches))
        : node.props.children;
      return React.cloneElement(node, undefined, ...buildRouteElements(), ...steamRoutes);
    }

    const children = node.props?.children;
    if (Array.isArray(children)) {
      let changed = false;
      const next = children.map((child) => {
        const replaced = withRoutes(child, depth + 1);
        changed ||= replaced !== child;
        return replaced;
      });
      return changed ? React.cloneElement(node, undefined, ...next) : node;
    }

    if (children && typeof children === "object") {
      const replaced = withRoutes(children, depth + 1);
      return replaced !== children ? React.cloneElement(node, undefined, replaced) : node;
    }

    return node;
  }

  function getRoutesWrapper() {
    if (RoutesWrapper) {
      return RoutesWrapper;
    }

    const React = getReact();
    // Subscribes to the route registry, so adding a page re-renders only this
    // wrapper; Steam's router itself renders once after the patch.
    RoutesWrapper = function TfsGamepadRoutes({ routerOutput }) {
      const [, rerender] = React.useReducer((value) => value + 1, 0);
      React.useEffect(() => {
        listeners.add(rerender);
        return () => listeners.delete(rerender);
      }, []);
      return routes.size || shared.patches.size ? withRoutes(routerOutput) : routerOutput;
    };
    return RoutesWrapper;
  }

  function notify() {
    for (const listener of [...listeners]) {
      try {
        listener();
      } catch {
        // ignore unmounted listeners
      }
    }
  }

  function dispatchMountedRoutesWrapper() {
    const stack = [];
    for (const fiber of [routerFiber, routerFiber?.alternate]) {
      if (fiber?.child) {
        stack.push([fiber.child, 0]);
      }
    }

    let dispatched = false;
    while (stack.length) {
      const [fiber, depth] = stack.pop();
      if (!fiber || depth > 4) {
        continue;
      }
      if (fiber.type?.name === "TfsGamepadRoutes" || fiber.type?.displayName === "TfsGamepadRoutes") {
        for (let hook = fiber.memoizedState; hook; hook = hook.next) {
          if (typeof hook.queue?.dispatch === "function") {
            try {
              hook.queue.dispatch(undefined);
              dispatched = true;
            } catch {
              // unmounted
            }
            break;
          }
        }
        continue;
      }
      if (fiber.child) {
        stack.push([fiber.child, depth + 1]);
      }
      if (fiber.sibling) {
        stack.push([fiber.sibling, depth]);
      }
    }
    return dispatched;
  }

  // Steam's router is a props-less memo component, so a parent re-render
  // bails out. Give the mounted fiber stale props once to force a render.
  function forceRouterRender() {
    if (!routerFiber) {
      return false;
    }

    for (const fiber of [routerFiber, routerFiber.alternate]) {
      if (fiber) {
        fiber.memoizedProps = { ...(fiber.memoizedProps || {}), __tfsRouterRevision: shared.revision };
      }
    }

    // A routes wrapper left by an older core sits right below the router.
    // Scheduling an update on it makes React walk through the router fiber,
    // which then renders again with the current (patched) type and mounts
    // this instance's wrapper.
    if (dispatchMountedRoutesWrapper()) {
      return true;
    }

    for (let node = routerFiber.return; node; node = node.return) {
      const instance = node.stateNode;
      if (instance && typeof instance.forceUpdate === "function") {
        try {
          instance.forceUpdate();
          return true;
        } catch {
          // keep walking up
        }
      }
    }
    return false;
  }

  function install() {
    if (patch) {
      return true;
    }

    if (!getReact()) {
      return false;
    }

    Route = findRouteComponent();
    routerFiber = findGamepadRouterFiber();
    if (!Route || !routerFiber) {
      return false;
    }

    const memo = routerFiber.elementType;
    // An older core that was replaced without deinit may still own the patch;
    // take the router back to Steam's own function before wrapping it.
    if (typeof memo.type === "function" && memo.type[PATCH_MARK]?.label === "GamepadRouter") {
      memo.type = getUnpatched(memo.type);
    }
    patch = afterPatch(memo, "type", (_args, result) => {
      if (!result || result.props?.__tfsRoutesWrapped) {
        return undefined;
      }

      return h(getRoutesWrapper(), { routerOutput: result, __tfsRoutesWrapped: true });
    }, { label: "GamepadRouter" });
    routerFiber.type = memo.type;
    if (routerFiber.alternate) {
      routerFiber.alternate.type = memo.type;
    }
    forceRouterRender();
    log("Router hook installed.");
    return true;
  }

  function changed() {
    shared.revision += 1;
    if (install()) {
      notify();
    }
  }

  return {
    /** Registers a full-screen Big Picture page; returns a remover. */
    add(path, component, options = {}) {
      if (typeof path !== "string" || !path.startsWith("/")) {
        throw new TypeError("routes.add needs an absolute path such as /tfs/store");
      }
      if (typeof component !== "function" && typeof component?.render !== "function") {
        throw new TypeError("routes.add needs a React component");
      }

      routes.set(path, { component, ...options });
      changed();
      return () => this.remove(path);
    },
    remove(path) {
      if (routes.delete(path)) {
        changed();
      }
    },
    has(path) {
      return routes.has(path);
    },
    /**
     * Patches one of Steam's own routes, e.g. "/library/app/:appid" (Decky's
     * routerHook.addPatch). handler(routeElement) returns a replacement
     * element (clone, never mutate Steam's) or nothing. Returns a remover.
     */
    patch(path, handler, { id = "default" } = {}) {
      if (typeof path !== "string" || !path.startsWith("/") || typeof handler !== "function") {
        throw new TypeError("routes.patch needs an absolute Steam route path and a function");
      }

      let handlers = shared.patches.get(path);
      if (!handlers) {
        handlers = new Map();
        shared.patches.set(path, handlers);
      }
      handlers.set(id, handler);
      changed();
      return () => {
        const current = shared.patches.get(path);
        if (current?.get(id) === handler) {
          current.delete(id);
          if (!current.size) {
            shared.patches.delete(path);
          }
          changed();
        }
      };
    },
    hasPatch(path, id = "default") {
      return Boolean(shared.patches.get(path)?.has(id));
    },
    navigate(path) {
      const mainWindow = getGamepadMainWindow();
      if (typeof mainWindow?.Navigate !== "function") {
        return false;
      }
      mainWindow.Navigate(path);
      return true;
    },
    back() {
      const mainWindow = getGamepadMainWindow();
      if (typeof mainWindow?.NavigateBack === "function") {
        mainWindow.NavigateBack();
        return true;
      }
      return false;
    },
    get installed() {
      return Boolean(patch);
    },
    /** The registry survives core updates so pages do not need to re-register. */
    registry: routes,
    install,
    deinit() {
      patch?.unpatch();
      patch = null;
      if (routerFiber && routerFiber.elementType?.type) {
        routerFiber.type = routerFiber.elementType.type;
        if (routerFiber.alternate) {
          routerFiber.alternate.type = routerFiber.elementType.type;
        }
      }
      forceRouterRender();
    },
  };
}

// ---------------------------------------------------------------------------
// Core lifecycle
// ---------------------------------------------------------------------------

function waitFor(check, { timeoutMs = 60000, intervalMs = 250 } = {}) {
  return new Promise((resolve, reject) => {
    const startedAt = Date.now();
    const tick = () => {
      let value = null;
      try {
        value = check();
      } catch {
        value = null;
      }

      if (value) {
        resolve(value);
        return;
      }

      if (Date.now() - startedAt >= timeoutMs) {
        reject(new Error("Timed out waiting for Steam's UI runtime."));
        return;
      }

      setTimeout(tick, intervalMs);
    };
    tick();
  });
}

/**
 * Entry point called by the loader. Re-running with the same version is a
 * no-op; a new version tears the previous instance down first.
 */
export async function start({ apiBase = "", version = "" } = {}) {
  const previous = globalThis[CORE_GLOBAL];
  if (previous?.version === version && previous?.apiVersion === TFS_CORE_API_VERSION && previous.ready) {
    return previous;
  }

  // Keep the Quick Access registration across core updates.
  const qam = previous?.qam && typeof previous.qam.registerPanel === "function"
    ? ensureTabPatchApi(previous.qam)
    : createQuickAccessStore();
  try {
    previous?.deinit?.();
  } catch (error) {
    warn("Previous core instance failed to deinit", error);
  }

  const core = {
    apiVersion: TFS_CORE_API_VERSION,
    version,
    apiBase,
    ready: false,
    qam,
    tabs: null,
    routes: null,
    ui: createUiLibrary(),
    keyboard: createKeyboardHelper(),
    // Registered windows and sheets survive core updates.
    css: previous?.css && typeof previous.css.add === "function" ? previous.css : createCssManager(),
    errors: [],
    webpack: { getWebpackRequire, getModules, findModule, findModuleExport, findExportBySource, getSourceText },
    react: { getReact, getReactRoot, findInFiberTree, findInReactTree },
    patch: { afterPatch, getUnpatched },
    get React() {
      return getReact();
    },
    get ErrorBoundary() {
      return getErrorBoundary();
    },
    get reactElementSymbol() {
      try {
        return getReact()?.createElement("i").$$typeof ?? null;
      } catch {
        return null;
      }
    },
    deinit() {
      core.ready = false;
      try {
        core.tabs?.deinit();
      } catch (error) {
        warn("Tabs hook deinit failed", error);
      }

      core.tabs = null;
      try {
        core.routes?.deinit();
      } catch (error) {
        warn("Router hook deinit failed", error);
      }
    },
  };
  // Pages registered with an older core keep working after an update.
  core.routes = createRouterHook(previous?.routes?.registry instanceof Map ? previous.routes.registry : new Map());
  globalThis[CORE_GLOBAL] = core;

  // Do not hold the loader's debugger call while Steam is still booting:
  // attach in the background and expose progress on the core object.
  core.whenReady = attachToSteam(core);
  return core;
}

async function attachToSteam(core) {
  let lastRefreshAt = 0;
  const findQuickAccessRenderer = () => {
    const match = findModuleExport((value) =>
      typeof value?.type === "function" && getSourceText(value).includes("QuickAccessMenuBrowserView"));
    if (!match && Date.now() - lastRefreshAt > 2000) {
      // Steam loads UI chunks lazily; rescan the registry now and then.
      lastRefreshAt = Date.now();
      getModules({ refresh: true });
    }

    return match;
  };

  try {
    await waitFor(() => getWebpackRequire() && getReact(), { timeoutMs: 120000 });
    await waitFor(findQuickAccessRenderer, { timeoutMs: 120000 });
  } catch (error) {
    core.errors.push(String(error?.message || error));
    warn("Steam UI runtime not ready", error);
    return core;
  }

  if (globalThis[CORE_GLOBAL] !== core) {
    return core; // replaced by a newer version meanwhile
  }

  try {
    core.tabs = createTabsHook(core);
  } catch (error) {
    core.errors.push(String(error?.message || error));
    warn("Quick Access tab hook failed", error);
  }

  // The Big Picture router only exists once Gamepad UI is mounted; keep
  // trying quietly until it is (cheap: one bounded fiber search).
  if (!core.routes.install()) {
    const retry = setInterval(() => {
      if (globalThis[CORE_GLOBAL] !== core || core.routes.install()) {
        clearInterval(retry);
      }
    }, 2000);
  }

  core.ready = true;
  core.qam.notify();
  return core;
}
