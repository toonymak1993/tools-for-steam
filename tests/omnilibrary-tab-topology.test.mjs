import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const source = readFileSync(
  new URL("../src/SteamLoader.App/Assets/omnilibrary-tab-topology.js", import.meta.url),
  "utf8",
);
const context = vm.createContext({});
vm.runInContext(source, context);
const topology = context.__steamLoaderOmniLibraryTabTopology;

const fallback = [
  {
    id: "xbox-game-pass",
    sourceStoreId: "xbox-game-pass",
    tabId: "tfs-xbox",
    title: "Xbox",
    appFilter: "non-cloud",
  },
  {
    id: "xbox-cloud",
    sourceStoreId: "xbox-game-pass",
    tabId: "tfs-xbox-cloud",
    title: "Xbox Cloud",
    appFilter: "cloud",
    requiresXboxCloud: true,
  },
  {
    id: "epic-games",
    sourceStoreId: "epic-games",
    tabId: "tfs-epic",
    title: "Epic",
    appFilter: "all",
  },
  {
    id: "gog-galaxy",
    sourceStoreId: "gog-galaxy",
    tabId: "tfs-gog",
    title: "GOG",
    appFilter: "all",
  },
];
const stores = [
  {
    id: "xbox-game-pass",
    title: "Xbox",
    libraryTabs: [
      { id: "tfs-xbox", title: "Xbox", filter: "non-cloud", requiresCloudSource: false },
      { id: "tfs-xbox-cloud", title: "Xbox Cloud", filter: "cloud", requiresCloudSource: true },
    ],
  },
  {
    id: "epic-games",
    title: "Epic Games",
    libraryTabs: [
      { id: "tfs-epic", title: "Epic", filter: "all", requiresCloudSource: false },
    ],
  },
  {
    id: "gog-galaxy",
    title: "GOG",
    libraryTabs: [
      { id: "tfs-gog", title: "GOG", filter: "all", requiresCloudSource: false },
    ],
  },
  {
    id: "rom-library",
    title: "Emulation",
    libraryTabs: [
      { id: "tfs-emulation-psp", title: "PSP", filter: "platform:psp", requiresCloudSource: false },
    ],
  },
];
const nativeTabs = [
  { id: "AllGames" },
  { id: "Installed" },
  { id: "DesktopApps" },
  { id: "Soundtracks" },
];

test("backend descriptors produce one stable ordered topology", () => {
  const definitions = topology.buildDefinitionsFromSummary(stores, fallback);
  assert.deepEqual(
    Array.from(definitions, (definition) => definition.tabId),
    ["tfs-xbox", "tfs-xbox-cloud", "tfs-epic", "tfs-gog", "tfs-emulation-psp"],
  );
});

test("duplicate backend tab ids are ignored even when sources disagree", () => {
  const definitions = topology.buildDefinitionsFromSummary(
    [
      {
        id: "rom-library",
        libraryTabs: [
          { id: "tfs-emulation-psp", title: "PSP", filter: "platform:psp" },
        ],
      },
      {
        id: "unexpected-source",
        libraryTabs: [
          { id: "tfs-emulation-psp", title: "Duplicate", filter: "all" },
        ],
      },
    ],
    [],
  );
  assert.equal(definitions.length, 1);
  assert.equal(definitions[0].title, "PSP");
});

test("every enabled store combination is inserted once after Non-Steam", () => {
  const definitions = topology.buildDefinitionsFromSummary(stores, fallback);
  for (let mask = 0; mask < 1 << definitions.length; mask += 1) {
    const enabled = definitions.filter((_, index) => (mask & (1 << index)) !== 0);
    const tabs = topology.buildCanonicalTabOrder(
      [
        ...nativeTabs,
        { id: "tfs-xbox" },
        { id: "tfs-xbox" },
        { id: "tfs-epic" },
        { id: "tfs-gog" },
        { id: "tfs-emulation-psp" },
      ],
      enabled,
      definitions,
    );
    const ids = Array.from(tabs, (tab) => tab.id);
    assert.equal(new Set(ids).size, ids.length);
    assert.deepEqual(
      ids,
      [
        "AllGames",
        "Installed",
        "DesktopApps",
        ...enabled.map((definition) => definition.tabId),
        "Soundtracks",
      ],
    );
  }
});

test("hidden native tabs are restored as internal templates without duplicating visible tabs", () => {
  const visible = [
    { id: "AllGames" },
    { id: "Installed" },
    { id: "Soundtracks" },
  ];
  const restored = topology.restoreMissingTabs(visible, nativeTabs);
  assert.deepEqual(
    Array.from(restored, (tab) => tab.id),
    ["AllGames", "Installed", "DesktopApps", "Soundtracks"],
  );
  assert.equal(new Set(restored.map((tab) => tab.id)).size, restored.length);
});

test("LB and RB traverse and wrap through all canonical tabs in both directions", () => {
  const definitions = topology.buildDefinitionsFromSummary(stores, fallback);
  const tabs = topology.buildCanonicalTabOrder(nativeTabs, definitions, definitions);
  const ids = Array.from(tabs, (tab) => tab.id);

  let current = ids[0];
  const right = [];
  for (let index = 0; index < ids.length; index += 1) {
    right.push(current);
    current = topology.getAdjacentTabId(tabs, current, 1, true);
  }
  assert.deepEqual(right, ids);
  assert.equal(current, ids[0]);

  current = ids[0];
  const left = [];
  for (let index = 0; index < ids.length; index += 1) {
    current = topology.getAdjacentTabId(tabs, current, -1, true);
    left.push(current);
  }
  assert.deepEqual(left, [...ids.slice(1).reverse(), ids[0]]);
});

test("virtual tabs never reuse the currently selected native backing route", () => {
  const routes = ["Installed", "AllGames"];
  assert.equal(
    topology.chooseDistinctBackingRoute(routes, 0, "Installed"),
    "AllGames",
  );
  assert.equal(
    topology.chooseDistinctBackingRoute(routes, 1, "AllGames"),
    "Installed",
  );
  assert.equal(
    topology.chooseDistinctBackingRoute(["AllGames"], 0, "AllGames"),
    "AllGames",
  );
});

test("large platform libraries remain ordered, unique, and fully traversable", () => {
  const platforms = Array.from({ length: 80 }, (_, index) => ({
    id: `platform-${index}`,
    sourceStoreId: "rom-library",
    tabId: `tfs-emulation-platform-${index}`,
    title: `Platform ${index}`,
    appFilter: `platform:platform-${index}`,
  }));
  const tabs = topology.buildCanonicalTabOrder(
    nativeTabs,
    [...platforms, platforms[0]],
    platforms,
  );
  const ids = Array.from(tabs, (tab) => tab.id);
  assert.equal(ids.length, nativeTabs.length + platforms.length);
  assert.equal(new Set(ids).size, ids.length);

  let current = ids[0];
  const visited = new Set();
  for (let index = 0; index < ids.length; index += 1) {
    visited.add(current);
    current = topology.getAdjacentTabId(tabs, current, 1, true);
  }
  assert.equal(visited.size, ids.length);
  assert.equal(current, ids[0]);
});

test("the synchronous navigation cursor wins over stale Steam route echoes", () => {
  const definitions = topology.buildDefinitionsFromSummary(stores, fallback);
  const tabs = topology.buildCanonicalTabOrder(nativeTabs, definitions, definitions);
  assert.equal(
    topology.resolveActiveTabId(tabs, [
      "tfs-epic",
      "Installed",
      "AllGames",
    ]),
    "tfs-epic",
  );
});

test("only the exact native backing route can be treated as a virtual-tab echo", () => {
  assert.equal(
    topology.shouldPreserveVirtualSelection(
      "tfs-epic",
      "AllGames",
      "AllGames",
      false,
    ),
    true,
  );
  assert.equal(
    topology.shouldPreserveVirtualSelection(
      "tfs-epic",
      "Soundtracks",
      "AllGames",
      false,
    ),
    false,
  );
  assert.equal(
    topology.shouldPreserveVirtualSelection(
      "tfs-xbox",
      "DesktopApps",
      "Installed",
      false,
    ),
    false,
  );
  assert.equal(
    topology.shouldPreserveVirtualSelection(
      "tfs-xbox",
      "Installed",
      "Installed",
      true,
    ),
    false,
  );
});

test("a visible native tab wins over a stale virtual selection", () => {
  const definitions = topology.buildDefinitionsFromSummary(stores, fallback);
  const tabs = topology.buildCanonicalTabOrder(nativeTabs, definitions, definitions);
  assert.equal(
    topology.resolveActiveTabId(tabs, [
      "",
      "Soundtracks",
      "tfs-epic",
      "tfs-epic",
    ]),
    "Soundtracks",
  );
});
