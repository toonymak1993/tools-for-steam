import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

// Minimal stand-ins for Steam's webpack registry, React and the Quick Access
// components, shaped like the real ones (memo object with .type, a menu
// component portalled by the browser view, a memoized tab array).
const elementSymbol = Symbol.for("react.transitional.element");
const fakeReact = {
  version: "19-test",
  createElement(type, props, ...children) {
    return {
      $$typeof: elementSymbol,
      type,
      key: props?.key ?? null,
      props: { ...(props || {}), ...(children.length ? { children: children.length === 1 ? children[0] : children } : {}) },
    };
  },
  cloneElement(element, props, ...children) {
    return {
      ...element,
      props: {
        ...element.props,
        ...(props || {}),
        ...(children.length ? { children: children.length === 1 ? children[0] : children } : {}),
      },
    };
  },
  useState: (value) => [value, () => {}],
  useReducer: (_reducer, value) => [value, () => {}],
  useEffect: () => {},
  Component: class Component {
    constructor(props) {
      this.props = props;
    }
  },
};

const memoTabs = [
  { key: 0, tab: fakeReact.createElement("i"), title: null, strTitle: "Notifications", panel: null },
  { key: 7, tab: fakeReact.createElement("i"), title: null, strTitle: "Soundtrack", panel: null },
];

function QuickAccessMenu(props) {
  return fakeReact.createElement(
    "div",
    { id: "QuickAccess-Menu", active: props.active },
    fakeReact.createElement("Tabs", { tabs: memoTabs, activeTab: 0 }),
    fakeReact.createElement("Panels", { tabs: memoTabs, activeTab: 0 }),
  );
}

const browserView = {
  $$typeof: Symbol.for("react.memo"),
  type: function BrowserView(props) {
    // "QuickAccessMenuBrowserView" appears in the real render source.
    const marker = "QuickAccessMenuBrowserView";
    void marker;
    return fakeReact.createElement("Host", {
      children: [
        fakeReact.createElement("Placeholder", {}),
        {
          $$typeof: Symbol.for("react.portal"),
          children: fakeReact.createElement(QuickAccessMenu, {
            active: props.active,
            onFocusNavDeactivated() {},
          }),
        },
      ],
    });
  },
  compare: null,
};

// Steam's router: react-router Route component (module with "router-backstack")
// and the Gamepad UI router, a props-less memo whose list holds the Routes.
function SteamRoute(props) {
  const marker = "routePath:Pe.match?.path";
  void marker;
  return typeof props.children === "function" ? props.children({ match: { path: props.path, params: {} } }) : props.children;
}
function RouteSwitch(props) {
  return props.children;
}
const gamepadRouter = {
  $$typeof: Symbol.for("react.memo"),
  type: function GamepadRouter() {
    const marker = "Settings.Root()";
    void marker;
    return fakeReact.createElement("Fragment", {
      children: [
        fakeReact.createElement(RouteSwitch, {
          children: [
            fakeReact.createElement(SteamRoute, { path: "/library" }),
            fakeReact.createElement(SteamRoute, { path: "/settings" }),
          ],
        }),
        fakeReact.createElement("Footer", {}),
      ],
    });
  },
};
let routerForceUpdates = 0;
const routerFiber = {
  elementType: gamepadRouter,
  type: gamepadRouter.type,
  memoizedProps: {},
  child: null,
  sibling: null,
  return: { stateNode: { forceUpdate: () => { routerForceUpdates += 1; } }, return: null },
};
const rootElement = { "__reactContainer$test": { stateNode: { current: { child: routerFiber, sibling: null } } } };
globalThis.document = { getElementById: (id) => (id === "root" ? rootElement : null) };

const modules = {
  1: { default: fakeReact, ...fakeReact },
  2: { Gt: browserView, DT: QuickAccessMenu },
  3: { Qc: "router-backstack", Jh: SteamRoute },
  4: { Router: gamepadRouter },
  5: {
    $n: { $$typeof: Symbol.for("react.forward_ref"), render: function DialogButton() { return "DialogButton Secondary"; } },
    lP: function LegacyDialogButton() { return "DialogButton Secondary with a much longer body"; },
  },
};

globalThis.webpackChunksteamui = [];
globalThis.webpackChunksteamui.push = function push(entry) {
  const require = (id) => modules[id];
  require.m = Object.fromEntries(Object.keys(modules).map((id) => [id, () => {}]));
  entry[2](require);
  return Array.prototype.push.call(this, entry);
};

const core = await import("../src/SteamLoader.App/Assets/tfs-core.mjs");

function renderQuickAccess() {
  const rendered = browserView.type({ active: true });
  const portal = rendered.props.children[1];
  const menuElement = portal.children;
  const menuResult = menuElement.type(menuElement.props);
  return { menuElement, menuResult };
}

test("core appends its own Quick Access tab and never touches Soundtrack", async () => {
  const instance = await core.start({ apiBase: "http://127.0.0.1:47652/", version: "t1" });
  await instance.whenReady;

  assert.equal(instance.ready, true);
  assert.deepEqual(instance.errors, []);
  assert.equal(globalThis.__tfs, instance);

  const first = renderQuickAccess();
  assert.notEqual(first.menuElement.type, QuickAccessMenu, "menu render is wrapped");
  const tabs = first.menuResult.props.children[0].props.tabs;
  assert.equal(tabs.filter((tab) => tab.key === core.TFS_QAM_TAB_KEY).length, 1);
  assert.equal(tabs.find((tab) => tab.key === 7).strTitle, "Soundtrack");

  // Repeated renders keep exactly one tab and a stable wrapper identity.
  const second = renderQuickAccess();
  assert.equal(second.menuElement.type, first.menuElement.type);
  assert.equal(memoTabs.filter((tab) => tab.key === core.TFS_QAM_TAB_KEY).length, 1);
});

test("a fallback tab from the Quick Access window is replaced by the core tab", () => {
  const index = memoTabs.findIndex((tab) => tab.key === core.TFS_QAM_TAB_KEY);
  memoTabs[index] = { key: core.TFS_QAM_TAB_KEY, tfsFallback: true };
  renderQuickAccess();
  const tab = memoTabs.find((candidate) => candidate.key === core.TFS_QAM_TAB_KEY);
  assert.equal(tab.tfs, true);
  assert.equal(memoTabs.filter((candidate) => candidate.key === core.TFS_QAM_TAB_KEY).length, 1);
});

test("panel providers register, notify and unregister", () => {
  const qam = globalThis.__tfs.qam;
  let notified = 0;
  const unsubscribe = qam.subscribe(() => {
    notified += 1;
  });
  const provider = { id: "x", getPanel: () => "panel", getIcon: () => "icon" };
  const unregister = qam.registerPanel(provider);
  assert.equal(qam.provider, provider);
  qam.notify();
  unregister();
  assert.equal(qam.provider, null);
  assert.equal(notified, 3);
  unsubscribe();
});

test("qam.patchTab wraps one of Steam's tabs once per render and unpatch restores it", () => {
  const qam = globalThis.__tfs.qam;
  const original = memoTabs.find((tab) => tab.key === 7);
  let calls = 0;
  const remove = qam.patchTab(7, (tab) => {
    calls += 1;
    return { ...tab, panel: { wrapped: tab.panel } };
  }, { id: "perf-test" });

  renderQuickAccess();
  renderQuickAccess();
  const patched = memoTabs.find((tab) => tab.key === 7);
  assert.notEqual(patched, original);
  assert.equal(patched.panel.wrapped, original.panel, "wrapped exactly once");
  assert.equal(patched.strTitle, "Soundtrack");
  assert.equal(calls, 1, "re-renders reuse the patched tab");

  remove();
  renderQuickAccess();
  assert.equal(memoTabs.find((tab) => tab.key === 7), original);
  assert.equal(qam.unpatchTab(7, "perf-test"), false);
});

test("router hook puts registered pages first in Steam's route list", () => {
  const routes = globalThis.__tfs.routes;
  assert.equal(routes.installed, true);
  assert.ok(routerForceUpdates >= 1, "the props-less router memo is forced to render once");
  assert.equal(routerFiber.memoizedProps.__tfsRouterRevision !== undefined, true);

  function Page(props) {
    return `page ${props.match.path}`;
  }
  const remove = routes.add("/tfs/test", Page, { name: "Test page" });
  assert.equal(routes.has("/tfs/test"), true);

  const wrapper = routerFiber.type();
  assert.equal(wrapper.props.__tfsRoutesWrapped, true);
  const output = wrapper.type(wrapper.props);
  const routeList = output.props.children[0].props.children;
  assert.equal(routeList[0].key, "tfs-route:/tfs/test");
  assert.equal(routeList[0].type, SteamRoute);
  assert.equal(routeList.length, 3, "Steam's own routes stay in place");
  const boundary = routeList[0].props.children({ match: { path: "/tfs/test", params: {} } });
  assert.equal(boundary.props.name, "Test page");
  assert.equal(boundary.props.children.type, Page);

  // Steam's original elements are never mutated.
  const original = gamepadRouter.type[Symbol.for("tfs.patch")].original();
  assert.equal(original.props.children[0].props.children.length, 2);

  remove();
  assert.equal(routes.has("/tfs/test"), false);
  const afterRemove = wrapper.type(wrapper.props);
  assert.equal(afterRemove.props.children[0].props.children.length, 2);
});

test("route patches change one of Steam's routes without mutating it", () => {
  const routes = globalThis.__tfs.routes;
  const remove = routes.patch("/library", (route) =>
    fakeReact.cloneElement(route, undefined, fakeReact.createElement("Patched", {})), { id: "test" });
  assert.equal(routes.hasPatch("/library", "test"), true);

  const wrapper = routerFiber.type();
  const output = wrapper.type(wrapper.props);
  const routeList = output.props.children[0].props.children;
  assert.equal(routeList.length, 2, "no extra routes, only the patched one");
  assert.equal(routeList[0].props.path, "/library");
  assert.equal(routeList[0].props.children.type, "Patched");
  assert.equal(routeList[1].props.children, undefined, "other routes untouched");

  const original = gamepadRouter.type[Symbol.for("tfs.patch")].original();
  assert.equal(original.props.children[0].props.children[0].props.children, undefined);

  // A failing handler leaves the route as it was.
  const removeBroken = routes.patch("/settings", () => { throw new Error("boom"); }, { id: "broken" });
  const withBroken = wrapper.type(wrapper.props).props.children[0].props.children;
  assert.equal(withBroken[1].props.path, "/settings");
  removeBroken();

  remove();
  assert.equal(routes.hasPatch("/library", "test"), false);
  const afterRemove = wrapper.type(wrapper.props).props.children[0].props.children;
  assert.equal(afterRemove[0].props.children, undefined);
});

test("core.ui finds Steam components by signature and prefers forwardRef exports", () => {
  const button = globalThis.__tfs.ui.DialogButton;
  assert.equal(button, modules[5].$n);
  assert.equal(globalThis.__tfs.ui.ToggleField, null);
});

function createFakeDocument(title) {
  const styles = new Map();
  return {
    title,
    defaultView: { closed: false },
    styles,
    head: { append(element) { styles.set(element.id, element); } },
    getElementById(id) { return styles.get(id) || null; },
    createElement() {
      const element = {
        id: "",
        textContent: "",
        setAttribute() {},
        remove() { styles.delete(element.id); },
      };
      return element;
    },
  };
}

test("core.css applies sheets to the registered windows of matching roles", () => {
  const css = globalThis.__tfs.css;
  const gamepad = createFakeDocument("Big Picture");
  const menu = createFakeDocument("Menu");
  css.registerDocument(gamepad, "gamepad");
  css.registerDocument(menu, "menu");

  const remove = css.add("store", ".a { color: red; }", { targets: ["gamepad"] });
  assert.equal(gamepad.getElementById("tfs-css-store")?.textContent, ".a { color: red; }");
  assert.equal(menu.getElementById("tfs-css-store"), null);

  css.add("theme", ".b {}", { targets: ["all"] });
  assert.ok(menu.getElementById("tfs-css-theme"));

  // A window that registers later still receives existing sheets.
  const late = createFakeDocument("Quick Access");
  css.registerDocument(late, "quickaccess");
  assert.ok(late.getElementById("tfs-css-theme"));
  assert.equal(late.getElementById("tfs-css-store"), null);

  remove();
  assert.equal(gamepad.getElementById("tfs-css-store"), null);
  css.remove("theme");
  assert.equal(menu.getElementById("tfs-css-theme"), null);
});

test("core.keyboard reports Steam's keyboard as unavailable without a window store", () => {
  assert.equal(globalThis.__tfs.keyboard.available, false);
  assert.equal(globalThis.__tfs.keyboard.show({}), false);
});

test("same version is a no-op, deinit removes the render patch", async () => {
  const same = await core.start({ version: "t1" });
  assert.equal(same, globalThis.__tfs);

  const patchedType = browserView.type;
  globalThis.__tfs.deinit();
  assert.notEqual(browserView.type, patchedType);
  assert.equal(core.getUnpatched(patchedType), browserView.type);
});

test("afterPatch keeps the original source text and survives handler errors", () => {
  const target = { fn: function original(value) { return value + 1; } };
  const patch = core.afterPatch(target, "fn", () => {
    throw new Error("boom");
  });
  assert.equal(target.fn(1), 2);
  assert.match(target.fn.toString(), /function original/);
  patch.unpatch();
  assert.equal(target.fn.name, "original");
});

test("Quick Access script fills the core tab and no longer hijacks Soundtrack", () => {
  const popup = readFileSync(
    new URL("../src/SteamLoader.App/Assets/quickaccess-popup.js", import.meta.url),
    "utf8",
  );
  assert.match(popup, /const tfsQuickAccessTabKey = 4747;/);
  assert.doesNotMatch(popup, /soundtrackTabKey/);
  assert.doesNotMatch(popup, /quickaccess_(tab|content)_7\b/);
  assert.match(popup, /core\.qam\.registerPanel\(provider\)/);
  assert.match(popup, /restoreLegacyQuickAccessWrapper\(runtime, \{ includeCurrent: true \}\)/);
});
