import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const pluginUrl = new URL("../sdk/official-plugins/home-assistant/dist/index.js", import.meta.url);
const frontendLibraryUrl = new URL("../src/SteamLoader.App/Assets/st-frontend-lib.js", import.meta.url);

function createSlot(role, title, copy, onClick, options = {}) {
  return { role, title, copy, onClick, ...options };
}

async function loadHomeAssistantPlugin() {
  const registrations = [];
  const serviceCalls = [];
  const timers = [];
  const settings = {};
  const secrets = {};
  const states = [
    {
      entity_id: "light.kitchen_ceiling",
      state: "on",
      last_changed: "2026-07-21T10:00:00Z",
      last_updated: "2026-07-21T10:00:00Z",
      attributes: {
        friendly_name: "Kitchen Ceiling",
        brightness: 128,
        hs_color: [180, 70],
        color_temp_kelvin: 3_500,
        min_color_temp_kelvin: 2_000,
        max_color_temp_kelvin: 6_500,
        supported_color_modes: ["hs", "color_temp"],
      },
    },
    {
      entity_id: "sensor.kitchen_temperature",
      state: "21.5",
      last_changed: "2026-07-21T10:00:00Z",
      last_updated: "2026-07-21T10:00:00Z",
      attributes: { friendly_name: "Kitchen Temperature", unit_of_measurement: "°C" },
    },
    {
      entity_id: "number.kitchen_ceiling_power_on_level",
      state: "254",
      last_changed: "2026-07-21T10:00:00Z",
      last_updated: "2026-07-21T10:00:00Z",
      attributes: { friendly_name: "Power-on level", min: 0, max: 255, step: 1 },
    },
    {
      entity_id: "sensor.empty_temperature",
      state: "19.0",
      last_changed: "2026-07-21T10:00:00Z",
      last_updated: "2026-07-21T10:00:00Z",
      attributes: { friendly_name: "Empty Temperature", unit_of_measurement: "°C" },
    },
    {
      entity_id: "switch.garden_fountain",
      state: "off",
      last_changed: "2026-07-21T10:00:00Z",
      last_updated: "2026-07-21T10:00:00Z",
      attributes: { friendly_name: "Garden Fountain" },
    },
    {
      entity_id: "scene.movie_night",
      state: "scening",
      last_changed: "2026-07-21T10:00:00Z",
      last_updated: "2026-07-21T10:00:00Z",
      attributes: {
        friendly_name: "Movie Night",
        entity_id: ["light.kitchen_ceiling", "switch.garden_fountain"],
      },
    },
  ];
  const areaResponse = [
    {
      id: "kitchen",
      name: "Kitchen",
      entities: [
        { id: "light.kitchen_ceiling", deviceId: "kitchen-light", deviceName: "Kitchen Ceiling" },
        { id: "sensor.kitchen_temperature", deviceId: "kitchen-light", deviceName: "Kitchen Ceiling" },
        { id: "number.kitchen_ceiling_power_on_level", deviceId: "kitchen-light", deviceName: "Kitchen Ceiling" },
      ],
    },
    {
      id: "empty_room",
      name: "Empty Room",
      entities: [{ id: "sensor.empty_temperature", deviceId: "empty-sensor", deviceName: "Empty Sensor" }],
    },
  ];

  const sdk = {
    storage: {
      async get() {
        return { ...settings };
      },
      async patch(next) {
        Object.assign(settings, next);
        return { ...settings };
      },
    },
    secrets: {
      async status() {
        return Object.fromEntries(Object.keys(secrets).map((key) => [key.toLowerCase(), true]));
      },
      async set(key, value) {
        secrets[key] = value;
      },
      async clear(key) {
        delete secrets[key];
      },
    },
    network: {
      async get(url) {
        assert.match(url, /\/api\/states$/);
        return { ok: true, statusCode: 200, bodyText: JSON.stringify(states) };
      },
      async post(url, body) {
        if (url.endsWith("/api/template")) {
          assert.match(body.template, /area_entities/);
          assert.match(body.template, /device_id/);
          assert.match(body.template, /device_name/);
          return { ok: true, statusCode: 200, bodyText: JSON.stringify(areaResponse) };
        }

        serviceCalls.push({ url, body });
        if (body?.brightness_pct !== undefined) {
          states[0] = {
            ...states[0],
            state: "on",
            last_updated: "2026-07-21T10:00:01Z",
            attributes: {
              ...states[0].attributes,
              brightness: Math.round((body.brightness_pct / 100) * 255),
            },
          };
        }
        return { ok: true, statusCode: 200, bodyText: "[]" };
      },
    },
    lifecycle: {
      onDispose() {
        return () => {};
      },
      setTimeout(callback, delayMs) {
        const timer = { callback, delayMs, cancelled: false };
        timers.push(timer);
        return () => {
          timer.cancelled = true;
        };
      },
    },
    ui: {
      createScreenModel(model) {
        return model;
      },
      createNavigationSlot(title, copy, onClick, options) {
        return createSlot("navigation", title, copy, onClick, options);
      },
      createBackSlot(title, copy, onClick, options) {
        return createSlot("back", title, copy, onClick, options);
      },
      createToggleSlot(title, copy, value, onClick, options) {
        return createSlot("toggle", title, copy, onClick, { ...options, switchValue: value });
      },
      createCommandSlot(title, copy, onClick, options) {
        return createSlot("command", title, copy, onClick, options);
      },
      createAccordionSlot(title, copy, expanded, onClick, options) {
        return createSlot("command", title, copy, onClick, { ...options, layout: "accordion", expanded });
      },
      createInlineStepperSlot(title, copy, onMoveLeft, onMoveRight, options = {}) {
        return createSlot("command", title, copy, options.onClick || onMoveRight, {
          ...options,
          layout: "stepper",
          onMoveLeft,
          onMoveRight,
        });
      },
      createSliderSlot(title, value, onMoveLeft, onMoveRight, options = {}) {
        return createSlot("slider", title, options.valueLabel || String(value), onMoveRight, {
          ...options,
          layout: "slider",
          value,
          onMoveLeft,
          onMoveRight,
        });
      },
      createSecretEditor(options) {
        return { ...options, secret: true };
      },
    },
  };
  const source = await readFile(pluginUrl, "utf8");
  const window = {
    TfsPluginSdk: {
      register(manifest, setup) {
        registrations.push({ manifest, definition: setup(sdk) });
      },
    },
  };
  vm.runInNewContext(source, { window, console });
  assert.equal(registrations.length, 1);
  return { ...registrations[0], serviceCalls, states, timers };
}

async function settlePromises() {
  for (let index = 0; index < 5; index += 1) {
    await new Promise((resolve) => setImmediate(resolve));
  }
}

test("Home Assistant shows only controllable devices and keeps every setting inside its accordion", async () => {
  const { definition, manifest, serviceCalls, timers } = await loadHomeAssistantPlugin();
  assert.equal(manifest.version, "0.4.1");

  await definition.configure("http://homeassistant.local:8123", "test-token");
  const snapshot = definition.getSnapshot();
  assert.equal(snapshot.areas.length, 1);
  assert.equal(snapshot.areas[0].name, "Kitchen");
  assert.equal(snapshot.areas[0].devices.length, 1);
  assert.equal(snapshot.areas[0].entities.length, 2);
  assert.equal(snapshot.scenes.length, 1);

  let screen = definition.createScreen({ refresh() {} });
  assert.ok(screen.slots.find((slot) => slot.title === "Kitchen"));
  assert.equal(screen.slots.some((slot) => slot.title === "Empty Room"), false);
  screen.slots.find((slot) => slot.title === "Kitchen").onClick();

  screen = definition.createScreen({ refresh() {} });
  const light = screen.slots.find((slot) => slot.title === "Kitchen Ceiling");
  assert.equal(light.layout, "accordion");
  assert.equal(screen.slots.some((slot) => slot.title === "Kitchen Temperature"), false);
  assert.equal(screen.slots.some((slot) => slot.title === "Power-on level"), false);
  assert.equal(screen.slots.some((slot) => slot.disabled), false);
  light.onClick();
  screen = definition.createScreen({ refresh() {} });

  const brightness = screen.slots.find((slot) => slot.title === "Brightness");
  const color = screen.slots.find((slot) => slot.title === "Color");
  const colorIntensity = screen.slots.find((slot) => slot.title === "Color intensity");
  const whiteTemperature = screen.slots.find((slot) => slot.title === "White temperature");
  assert.equal(brightness.role, "slider");
  assert.equal(color.role, "slider");
  assert.equal(colorIntensity.role, "slider");
  assert.equal(whiteTemperature.role, "slider");
  assert.equal(typeof brightness.onValueChange, "function");
  assert.equal(typeof color.onValueChange, "function");
  assert.equal(typeof colorIntensity.onValueChange, "function");
  assert.equal(typeof whiteTemperature.onValueChange, "function");
  assert.match(color.trackStyle.background, /linear-gradient/);
  assert.equal(screen.slots.some((slot) => slot.title === "Kitchen Temperature"), false);
  assert.equal(screen.slots.find((slot) => slot.title === "Power-on level").role, "slider");
  assert.equal(screen.slots.some((slot) => slot.disabled), false);

  brightness.onValueChange(80);
  const sliderTimer = timers.findLast((timer) => timer.delayMs === 400 && !timer.cancelled);
  assert.ok(sliderTimer, "brightness changes should be debounced");
  sliderTimer.callback();
  await settlePromises();
  assert.ok(serviceCalls.some((call) =>
    call.url.endsWith("/api/services/light/turn_on") && call.body.brightness_pct === 80,
  ));

  color.onValueChange(240);
  const colorTimer = timers.findLast((timer) => timer.delayMs === 400 && !timer.cancelled);
  colorTimer.callback();
  await settlePromises();
  assert.ok(serviceCalls.some((call) =>
    call.url.endsWith("/api/services/light/turn_on") && call.body.hs_color?.[0] === 240,
  ));

  screen.topSlots[0].onClick();
  screen = definition.createScreen({ refresh() {} });
  screen.slots.find((slot) => slot.title === "Scenes").onClick();
  screen = definition.createScreen({ refresh() {} });
  const movieNight = screen.slots.find((slot) => slot.title === "Movie Night");
  assert.ok(movieNight);
  await movieNight.onClick();
  assert.ok(serviceCalls.some((call) => call.url.endsWith("/api/services/scene/turn_on")));
  assert.ok(timers.some((timer) => timer.delayMs === 8_000), "background refresh should be scheduled");
});

test("the public SDK slider is directly draggable and renders without arrow buttons", async () => {
  const source = await readFile(frontendLibraryUrl, "utf8");
  const sliderRenderer = source.slice(
    source.indexOf("function createSliderRowContent"),
    source.indexOf("function createProgressRowContent"),
  );
  assert.match(source, /layout: "slider"/);
  assert.match(source, /function createSliderRowContent/);
  assert.match(source, /slot\.trackStyle/);
  assert.match(source, /steamloader-volume-slider-thumb/);
  assert.match(source, /aria-valuetext/);
  assert.match(sliderRenderer, /onPointerDown/);
  assert.match(sliderRenderer, /onValueChange/);
  assert.match(sliderRenderer, /steamloader-volume-card steamloader-sdk-slider/);
  assert.doesNotMatch(sliderRenderer, /steamloader-inline-stepper-arrow/);
});
