(() => {
  const existing = window.STFrontendLib;
  if (existing?.version >= 39) {
    return;
  }

  const steamToggleClasses = Object.freeze({
    toggle: "_9Ql-oVe_j8E-vsDdyVdWo",
    rail: "_2bl0iQ9xigbq4Zd1NI6NZl",
    on: "yLrDAetGoWx0GYqA6ShfS",
    switch: "_1PQppcgkuXQAiFPar9AGi-",
    disabled: "aIeh3X5T2M074RLW1qn6_",
  });

  let steamToggleStyleAvailable = null;

  const defaultModel = Object.freeze({
    title: "Tools for Steam",
    subtitle: "",
    status: "",
    error: "",
    note: "",
    headerIcon: null,
    headerActions: Object.freeze([]),
    footerLegend: Object.freeze([]),
    autoFocusIndex: null,
    panelClassName: "",
    sectionHeaders: Object.freeze([]),
    dividerAfterIndex: null,
    dividerAfterIndices: null,
    topSlots: Object.freeze([]),
    volumePanel: null,
    cards: Object.freeze([]),
    editor: null,
    editors: Object.freeze([]),
    slots: Object.freeze([]),
  });

  const nativeComponentByRole = Object.freeze({
    action: "dialogButton",
    command: "dialogButton",
    navigation: "dialogButton",
    back: "dialogButton",
    toggle: "toggleField",
    choice: "dropdown",
  });

  const localComponentDefinitions = Object.freeze([
    {
      id: "dialogButton",
      title: "Dialog Button",
      required: ["DialogButton", "Secondary"],
      preferredExports: ["$n"],
    },
    {
      id: "toggleField",
      title: "Toggle Field",
      required: ["ToggleField"],
      preferredExports: ["RF"],
    },
    {
      id: "toggleControl",
      title: "Toggle Control",
      required: ["ToggleRail", "ToggleSwitch", "PlayNavSound"],
      preferredExports: ["J0", "Hk"],
    },
    {
      id: "checkbox",
      title: "Checkbox",
      required: ["DialogCheckbox", "aria-checked"],
      preferredExports: ["Yh"],
    },
    {
      id: "dropdown",
      title: "Dropdown",
      required: ["rgOptions", "selectedOption", "BuildMenu"],
      preferredExports: ["ZU"],
    },
    {
      id: "sliderField",
      title: "Slider Field",
      required: ["onChangeComplete", "onChangeStart", "validValues", "editableValue"],
      preferredExports: ["d3"],
    },
    {
      id: "panelSectionRow",
      title: "Panel Section Row",
      required: ["childrenLayout", "bottomSeparator", "highlightOnFocus", "transparentBackground"],
      preferredExports: ["D0"],
    },
  ]);

  const localRegistryState = {
    version: 1,
    runtimeReady: false,
    moduleCount: 0,
    lastRefreshIso: null,
    components: {},
    errors: [],
  };

  let lastLocalRegistryAttemptMs = 0;

  function getReactPropertyKey(element, prefix) {
    return element
      ? Object.getOwnPropertyNames(element).find((name) => name.startsWith(prefix))
      : null;
  }

  function getReactFiber(element) {
    const fiberKey = getReactPropertyKey(element, "__reactFiber");
    return fiberKey ? element[fiberKey] : null;
  }

  function getTypeSource(type) {
    const directSource = typeof type?.toString === "function" ? type.toString() : "";
    const renderSource = typeof type?.render?.toString === "function" ? type.render.toString() : "";
    return `${directSource}\n${renderSource}`;
  }

  function getTypeName(type) {
    return type?.displayName || type?.name || type?.render?.displayName || type?.render?.name || "anonymous";
  }

  function getCurrentContextRuntimeRequire() {
    if (window.__steamToolsQuickAccessRequire) {
      return window.__steamToolsQuickAccessRequire;
    }

    const chunk = window.webpackChunksteamui;
    if (!Array.isArray(chunk) || typeof chunk.push !== "function") {
      return null;
    }

    let runtimeRequire = null;
    try {
      chunk.push([[`steam-tools-quickaccess-registry-${Date.now()}`], {}, (require) => {
        runtimeRequire = require;
        window.__steamToolsQuickAccessRequire = require;
      }]);
    } catch (error) {
      localRegistryState.errors.push(
        `Unable to capture Quick Access webpack runtime: ${String(error?.message || error)}`,
      );
    }

    return runtimeRequire;
  }

  function isComponentLike(value) {
    return typeof value === "function" || typeof value?.render === "function";
  }

  function getModuleExports(runtimeRequire, moduleId) {
    try {
      const exportsObject = runtimeRequire(moduleId);
      return exportsObject && typeof exportsObject === "object"
        ? Object.entries(exportsObject)
        : [["default", exportsObject]];
    } catch {
      return [];
    }
  }

  function matchesLocalDefinition(definition, exportKey, value) {
    if (!isComponentLike(value)) {
      return false;
    }

    const searchable = `${exportKey}\n${getTypeName(value)}\n${getTypeSource(value)}`;
    return definition.required.every((needle) => searchable.includes(needle));
  }

  function scoreLocalMatch(definition, exportKey, value) {
    let score = definition.required.length * 10;

    if (definition.preferredExports.includes(exportKey)) {
      score += 20;
    }

    if (getTypeName(value) !== "anonymous") {
      score += 2;
    }

    if (typeof value?.render === "function") {
      score += 2;
    }

    return score;
  }

  function createLocalComponentState(definition, match) {
    if (!match) {
      return {
        id: definition.id,
        title: definition.title,
        available: false,
        moduleId: null,
        exportKey: null,
        exportName: "",
        exportType: "",
        value: null,
      };
    }

    return {
      id: definition.id,
      title: definition.title,
      available: true,
      moduleId: match.moduleId,
      exportKey: match.exportKey,
      exportName: match.name,
      exportType: match.type,
      value: match.value,
    };
  }

  function describeLocalRegistry() {
    const components = Object.values(localRegistryState.components).map((component) => ({
      id: component.id,
      title: component.title,
      available: Boolean(component.available),
      moduleId: component.moduleId,
      exportKey: component.exportKey,
      exportName: component.exportName,
      exportType: component.exportType,
    }));

    return {
      version: localRegistryState.version,
      runtimeReady: localRegistryState.runtimeReady,
      moduleCount: localRegistryState.moduleCount,
      availableCount: components.filter((component) => component.available).length,
      totalCount: localComponentDefinitions.length,
      lastRefreshIso: localRegistryState.lastRefreshIso,
      components,
      errors: [...localRegistryState.errors],
    };
  }

  function refreshLocalRegistry(force = false) {
    const now = Date.now();
    if (
      !force &&
      localRegistryState.lastRefreshIso &&
      localRegistryState.runtimeReady &&
      Object.keys(localRegistryState.components).length > 0
    ) {
      return describeLocalRegistry();
    }

    if (!force && localRegistryState.lastRefreshIso && now - lastLocalRegistryAttemptMs < 5000) {
      return describeLocalRegistry();
    }

    lastLocalRegistryAttemptMs = now;
    localRegistryState.errors = [];
    localRegistryState.lastRefreshIso = new Date().toISOString();

    const runtimeRequire = getCurrentContextRuntimeRequire();
    localRegistryState.runtimeReady = Boolean(runtimeRequire);

    if (!runtimeRequire?.m) {
      localRegistryState.moduleCount = 0;
      localRegistryState.components = Object.fromEntries(
        localComponentDefinitions.map((definition) => [
          definition.id,
          createLocalComponentState(definition, null),
        ]),
      );
      return describeLocalRegistry();
    }

    const matches = new Map(localComponentDefinitions.map((definition) => [definition.id, null]));
    const moduleIds = Object.keys(runtimeRequire.m);
    localRegistryState.moduleCount = moduleIds.length;

    for (const moduleId of moduleIds) {
      for (const [exportKey, value] of getModuleExports(runtimeRequire, moduleId)) {
        if (!value) {
          continue;
        }

        for (const definition of localComponentDefinitions) {
          if (!matchesLocalDefinition(definition, exportKey, value)) {
            continue;
          }

          const match = {
            moduleId,
            exportKey,
            type: typeof value,
            name: getTypeName(value),
            score: scoreLocalMatch(definition, exportKey, value),
            value,
          };
          const current = matches.get(definition.id);

          if (!current || match.score > current.score) {
            matches.set(definition.id, match);
          }
        }
      }
    }

    localRegistryState.components = Object.fromEntries(
      localComponentDefinitions.map((definition) => [
        definition.id,
        createLocalComponentState(definition, matches.get(definition.id)),
      ]),
    );

    return describeLocalRegistry();
  }

  function getLocalComponentState(id) {
    refreshLocalRegistry();
    return localRegistryState.components[id] || null;
  }

  function getResolvedNativeComponent(id) {
    return getLocalComponentState(id)?.value || null;
  }

  function walkFiber(root, visitor, limit = 900) {
    const stack = root ? [root] : [];
    const seen = new Set();

    while (stack.length > 0 && seen.size < limit) {
      const node = stack.pop();
      if (!node || seen.has(node)) {
        continue;
      }

      seen.add(node);
      visitor(node);

      if (node.sibling) {
        stack.push(node.sibling);
      }

      if (node.child) {
        stack.push(node.child);
      }
    }
  }

  function getRootFiber(element) {
    let current = getReactFiber(element);
    while (current?.return) {
      current = current.return;
    }

    return current || null;
  }

  function getQuickAccessRootFiber() {
    const root = document.getElementById("QuickAccess-NA");
    const rootKey =
      getReactPropertyKey(root, "__reactFiber") ||
      getReactPropertyKey(root, "__reactContainer");

    return rootKey ? root[rootKey] : null;
  }

  function addCandidate(list, type) {
    const name = getTypeName(type);
    if (!list.some((candidate) => candidate.name === name)) {
      list.push({ name });
    }
  }

  function collectNativeCandidates(state, rootFiber) {
    const candidates = {
      toggles: [],
      choices: [],
      sliders: [],
    };

    walkFiber(rootFiber, (node) => {
      const type = node.elementType || node.type;
      const source = getTypeSource(type);

      if (!source) {
        return;
      }

      if (
        source.includes("ToggleField") ||
        source.includes("DialogCheckbox") ||
        source.includes("bChecked") ||
        source.includes("aria-checked")
      ) {
        addCandidate(candidates.toggles, type);
      }

      if (
        source.includes("Dropdown") ||
        source.includes("DropDown") ||
        source.includes("Combobox") ||
        source.includes("rgOptions") ||
        source.includes("selectedOption")
      ) {
        addCandidate(candidates.choices, type);
      }

      if (source.includes("Slider") || source.includes("onChangeEnd") || source.includes("nMin")) {
        addCandidate(candidates.sliders, type);
      }
    });

    state.nativeUi.componentCandidates = candidates;
    state.nativeUi.steamToggleStyleAvailable = canUseSteamToggleStyle();
  }

  function findDialogButtonType(rootFiber) {
    let dialogButtonType = null;

    walkFiber(rootFiber, (node) => {
      if (dialogButtonType) {
        return;
      }

      const type = node.elementType || node.type;
      const source = getTypeSource(type);

      if (source.includes('"DialogButton"') && source.includes('"Secondary"')) {
        dialogButtonType = type;
      }
    }, 1800);

    return dialogButtonType;
  }

  function canUseSteamToggleStyle() {
    if (steamToggleStyleAvailable !== null) {
      return steamToggleStyleAvailable;
    }

    if (!document?.body) {
      steamToggleStyleAvailable = false;
      return steamToggleStyleAvailable;
    }

    const probe = document.createElement("span");
    probe.className = steamToggleClasses.toggle;
    probe.style.position = "absolute";
    probe.style.left = "-9999px";
    probe.style.top = "-9999px";
    document.body.appendChild(probe);

    const style = getComputedStyle(probe);
    const width = Number.parseFloat(style.width);
    const height = Number.parseFloat(style.height);
    steamToggleStyleAvailable = width >= 30 && height >= 18 && style.borderRadius !== "0px";
    probe.remove();

    return steamToggleStyleAvailable;
  }

  function captureNativeUi(state) {
    if (!state) {
      return false;
    }

    state.nativeUi ??= {};

    if (
      state.nativeUi.dialogButtonType &&
      state.nativeUi.componentCandidates &&
      typeof state.nativeUi.steamToggleStyleAvailable === "boolean"
    ) {
      return true;
    }

    if (typeof state.nativeUi.steamToggleStyleAvailable !== "boolean") {
      state.nativeUi.steamToggleStyleAvailable = canUseSteamToggleStyle();
    }

    state.nativeUi.localRegistrySnapshot = refreshLocalRegistry();
    state.nativeUi.dialogButtonType = state.nativeUi.dialogButtonType || getResolvedNativeComponent("dialogButton");

    const dialogButton = document.querySelector(".DialogButton");
    let current = getReactFiber(dialogButton);
    const rootFiber = getRootFiber(dialogButton) || getQuickAccessRootFiber();

    if (rootFiber && !state.nativeUi.componentCandidates) {
      collectNativeCandidates(state, rootFiber);
    }

    while (current) {
      const renderSource =
        typeof current.elementType?.render?.toString === "function"
          ? current.elementType.render.toString()
          : "";

      if (renderSource.includes('"DialogButton"') && renderSource.includes('"Secondary"')) {
        state.nativeUi.dialogButtonType = current.elementType;
        return Boolean(state.nativeUi.dialogButtonType);
      }

      current = current.return;
    }

    if (!state.nativeUi.dialogButtonType && rootFiber) {
      state.nativeUi.dialogButtonType = findDialogButtonType(rootFiber);
    }

    return Boolean(state.nativeUi.dialogButtonType);
  }

  function playUiSound() {
    const candidates = [
      window.SteamClient?.Audio,
      window.SteamClient?.UI,
      window.SteamClient?.System,
      window.SteamClient,
    ].filter(Boolean);

    for (const target of candidates) {
      for (const methodName of ["PlayUISound", "PlayUiSound", "PlaySound"]) {
        const method = target?.[methodName];
        if (typeof method !== "function") {
          continue;
        }

        try {
          method.call(target, "select");
          return true;
        } catch {
          try {
            method.call(target);
            return true;
          } catch {
          }
        }
      }
    }

    return false;
  }

  function playSoundFile(path) {
    try {
      const audio = new Audio(path);
      audio.volume = 0.72;
      const promise = audio.play();
      if (promise && typeof promise.catch === "function") {
        promise.catch(() => {});
      }
      return true;
    } catch {
      return false;
    }
  }

  function playToggleSound(value) {
    return playSoundFile(
      value
        ? "/sounds/deck_ui_switch_toggle_on.wav"
        : "/sounds/deck_ui_switch_toggle_off.wav",
    );
  }

  function getNativeRegistry(state) {
    return state?.nativeUi?.registrySnapshot || null;
  }

  function getNativeComponent(state, id) {
    const localComponent = getLocalComponentState(id);
    if (localComponent?.available) {
      const { value, ...snapshot } = localComponent;
      return snapshot;
    }

    const components = getNativeRegistry(state)?.components;
    return Array.isArray(components)
      ? components.find((component) => component?.id === id) || null
      : null;
  }

  function isNativeComponentAvailable(state, id) {
    return Boolean(getResolvedNativeComponent(id) || getNativeComponent(state, id)?.available);
  }

  async function refreshComponentRegistry(apiBase, state) {
    if (!apiBase || !state) {
      return null;
    }

    state.nativeUi ??= {};
    if (state.nativeUi.registryLoading) {
      return getNativeRegistry(state);
    }

    state.nativeUi.registryLoading = true;
    state.nativeUi.registryLastAttemptMs = Date.now();

    try {
      const response = await fetch(`${apiBase}api/frontend/components`, { cache: "no-store" });
      const payload = await response.json();
      if (!response.ok) {
        throw new Error(payload.message || `Steam frontend components could not be loaded (${response.status}).`);
      }

      state.nativeUi.registrySnapshot = payload && typeof payload === "object" ? payload : null;
      state.nativeUi.registryError = "";
    } catch (error) {
      state.nativeUi.registryError = error instanceof Error ? error.message : String(error);
    } finally {
      state.nativeUi.registryLoading = false;
    }

    return getNativeRegistry(state);
  }

  function createDialogButton(state, createElement, content, onClick, options = {}) {
    const invoke = (event) => {
      if (options.disabled) {
        return;
      }

      onClick?.(event);
    };

    const commonProps = {
      onClick: invoke,
      onOKButton: invoke,
      onActivate: invoke,
      disabled: Boolean(options.disabled),
      className: options.className || "steamloader-dialog-button",
      children: content,
      ...(options.extraProps || {}),
    };

    const DialogButton = state?.nativeUi?.dialogButtonType || getResolvedNativeComponent("dialogButton");
    if (DialogButton) {
      state.nativeUi ??= {};
      state.nativeUi.dialogButtonType = DialogButton;

      return createElement(DialogButton, {
        ...commonProps,
        focusable: true,
      }, options.slotKey || options.key || null);
    }

    return createElement("button", {
      type: "button",
      ...commonProps,
      className: "steamloader-fallback-button",
    }, options.slotKey || options.key || null);
  }

  function renderSwitchAccessory(createElement, withChildren, slot) {
    if (canUseSteamToggleStyle()) {
      return createElement(
        "span",
        withChildren(
          { className: "steamloader-switch-wrap steamtools-native-toggle-wrap", "aria-hidden": "true" },
          createElement(
            "span",
            withChildren(
              {
                className: [
                  "steamtools-native-toggle",
                  slot.switchValue ? "is-on" : "",
                  slot.disabled ? "is-disabled" : "",
                  steamToggleClasses.toggle,
                  slot.switchValue ? steamToggleClasses.on : "",
                  slot.disabled ? steamToggleClasses.disabled : "",
                ]
                  .filter(Boolean)
                  .join(" "),
              },
              createElement("span", {
                className: steamToggleClasses.rail,
              }),
              createElement("span", {
                className: steamToggleClasses.switch,
              }),
            ),
          ),
          slot.switchLabel
            ? createElement("span", {
                className: "steamloader-switch-label",
                children: slot.switchLabel,
              })
            : null,
        ),
      );
    }

    return createElement(
      "span",
      withChildren(
        { className: "steamloader-switch-wrap", "aria-hidden": "true" },
        createElement(
          "span",
          withChildren(
            {
              className: `steamloader-switch${slot.switchValue ? " is-on" : ""}`,
            },
            createElement("span", {
              className: "steamloader-switch-thumb",
            }),
          ),
        ),
        slot.switchLabel
          ? createElement("span", {
              className: "steamloader-switch-label",
              children: slot.switchLabel,
            })
          : null,
      ),
    );
  }

  function renderTrailingContent(createElement, withChildren, slot, helpers = {}) {
    if (typeof slot.switchValue === "boolean") {
      return renderSwitchAccessory(createElement, withChildren, slot);
    }

    if (slot.badge && slot.layout !== "feature") {
      return createElement("span", {
        className: "steamloader-badge",
        children: slot.badge,
      });
    }

    if (slot.trailing === "none") {
      return null;
    }

    const Icon = slot.trailing === "back" ? helpers.BackIcon : helpers.ChevronIcon;
    return typeof Icon === "function"
      ? createElement(Icon, {})
      : createElement("span", {
          className: "steamloader-row-trailing-glyph",
          children: slot.trailing === "back" ? "<" : ">",
        });
  }

  function createSlot(title, copy, onClick, options = {}) {
    return {
      kind: "button",
      role: options.role || "action",
      title,
      copy: copy || "",
      onClick,
      disabled: Boolean(options.disabled),
      badge: options.badge || "",
      trailing: options.trailing || "chevron",
      switchValue: options.switchValue,
      switchLabel: options.switchLabel || "",
      leadingIcon: options.leadingIcon || null,
      buttonClassName: options.buttonClassName || "",
      buttonStyle: options.buttonStyle || null,
      buttonProps: options.buttonProps || null,
      rowClassName: options.rowClassName || "",
      slotKey: options.slotKey || options.key || "",
      selected: Boolean(options.selected),
      value: options.value,
      layout: options.layout || "",
      expanded: Boolean(options.expanded),
      eyebrow: options.eyebrow || "",
      meta: Array.isArray(options.meta) ? options.meta.filter(Boolean) : [],
      mediaImageSrc: options.mediaImageSrc || "",
      mediaImageAlt: options.mediaImageAlt || "",
      footerLabel: options.footerLabel || "",
      stepperLeftDisabled: Boolean(options.stepperLeftDisabled),
      stepperRightDisabled: Boolean(options.stepperRightDisabled),
      nativeComponentId:
        options.nativeComponentId || nativeComponentByRole[options.role || "action"] || "dialogButton",
    };
  }

  function createNavigationSlot(title, copy, onClick, options = {}) {
    return createSlot(title, copy, onClick, {
      ...options,
      role: "navigation",
      trailing: options.trailing || "chevron",
      nativeComponentId: options.nativeComponentId || "dialogButton",
    });
  }

  function createBackSlot(title, copy, onClick, options = {}) {
    return createSlot(title, copy, onClick, {
      ...options,
      role: "back",
      trailing: options.trailing || "back",
      nativeComponentId: options.nativeComponentId || "dialogButton",
    });
  }

  function createToggleSlot(title, copy, value, onClick, options = {}) {
    return createSlot(title, copy, onClick, {
      ...options,
      role: "toggle",
      trailing: "none",
      switchValue: value,
      nativeComponentId: options.nativeComponentId || "toggleField",
    });
  }

  function createSettingToggleSlot(scope, key, title, copy, value, onClick, options = {}) {
    return {
      ...createToggleSlot(title, copy, value, onClick, options),
      settingScope: scope || "",
      settingKey: key || "",
    };
  }

  function createChoiceSlot(title, copy, onClick, options = {}) {
    return createSlot(title, copy, onClick, {
      ...options,
      role: "choice",
      badge: options.badge || options.value || "",
      selected: Boolean(options.selected || options.badge === "Selected"),
      nativeComponentId: options.nativeComponentId || "dropdown",
    });
  }

  function createCommandSlot(title, copy, onClick, options = {}) {
    return createSlot(title, copy, onClick, {
      ...options,
      role: "command",
      trailing: options.trailing || "none",
    });
  }

  function createAccordionSlot(title, copy, expanded, onClick, options = {}) {
    return createCommandSlot(title, copy, onClick, {
      ...options,
      layout: "accordion",
      expanded,
      buttonClassName:
        options.buttonClassName || "steamloader-dialog-button steamloader-dialog-button-accordion",
    });
  }

  function createFeatureNavigationSlot(title, copy, onClick, options = {}) {
    return createNavigationSlot(title, copy, onClick, {
      ...options,
      layout: "feature",
      eyebrow: options.eyebrow || "",
      meta: Array.isArray(options.meta) ? options.meta : [],
      mediaImageSrc: options.mediaImageSrc || "",
      mediaImageAlt: options.mediaImageAlt || title || "",
      footerLabel: options.footerLabel || "Open",
      buttonClassName:
        options.buttonClassName || "steamloader-dialog-button steamloader-dialog-button-feature",
    });
  }

  function createInlineStepperSlot(title, copy, onMoveLeft, onMoveRight, options = {}) {
    const leftDisabled = Boolean(options.leftDisabled);
    const rightDisabled = Boolean(options.rightDisabled);
    const externalButtonProps = options.buttonProps || {};

    return createCommandSlot(
      title,
      copy,
      options.onClick || onMoveRight || onMoveLeft || (() => {}),
      {
        ...options,
        layout: "stepper",
        trailing: "none",
        stepperLeftDisabled: leftDisabled,
        stepperRightDisabled: rightDisabled,
        buttonClassName:
          options.buttonClassName || "steamloader-dialog-button steamloader-dialog-button-inline-stepper",
        buttonProps: {
          ...externalButtonProps,
          onMoveLeft: (event) => {
            externalButtonProps.onMoveLeft?.(event);
            if (!leftDisabled) {
              onMoveLeft?.(event);
            }
            return true;
          },
          onMoveRight: (event) => {
            externalButtonProps.onMoveRight?.(event);
            if (!rightDisabled) {
              onMoveRight?.(event);
            }
            return true;
          },
        },
      },
    );
  }

  function createScreenModel(overrides = {}) {
    return {
      ...defaultModel,
      ...overrides,
      cards: Array.isArray(overrides.cards) ? overrides.cards : [],
      sectionHeaders: Array.isArray(overrides.sectionHeaders) ? overrides.sectionHeaders : [],
      topSlots: Array.isArray(overrides.topSlots) ? overrides.topSlots : [],
      slots: Array.isArray(overrides.slots) ? overrides.slots : [],
    };
  }

  function getRenderableSlots(model) {
    return [
      ...(Array.isArray(model?.topSlots) ? model.topSlots : []),
      ...(Array.isArray(model?.slots) ? model.slots : []),
    ];
  }

  function buildRowClassName(slot) {
    const roleClassName = slot.role ? ` steamtools-row-${slot.role}` : "";
    const layoutClassName = slot.layout ? ` steamtools-row-layout-${slot.layout}` : "";

    if (slot.leadingIcon) {
      return slot.rowClassName
        ? `steamloader-row-shell steamloader-row-shell-with-icon${roleClassName}${layoutClassName} ${slot.rowClassName}`
        : `steamloader-row-shell steamloader-row-shell-with-icon${roleClassName}${layoutClassName}`;
    }

    return slot.rowClassName
      ? `steamloader-row-shell${roleClassName}${layoutClassName} ${slot.rowClassName}`
      : `steamloader-row-shell${roleClassName}${layoutClassName}`;
  }

  function createAccordionRowContent(createElement, withChildren, slot) {
    return createElement(
      "div",
      withChildren(
        {
          className: `steamloader-accordion-toggle${slot.expanded ? " is-expanded" : ""}`,
        },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-accordion-toggle-copy-wrap" },
            createElement("div", {
              className: "steamloader-accordion-toggle-title",
              children: slot.title,
            }),
            slot.copy
              ? createElement("div", {
                  className: "steamloader-accordion-toggle-copy",
                  children: slot.copy,
                })
              : null,
          ),
        ),
        createElement("span", {
          className: "steamloader-accordion-toggle-arrow",
          children: "v",
        }),
      ),
    );
  }

  function createFeatureRowContent(createElement, withChildren, slot, trailingNode) {
    const metaItems = Array.isArray(slot.meta) ? slot.meta.filter(Boolean) : [];
    const FeatureIcon = slot.leadingIcon;

    return createElement(
      "div",
      withChildren(
        { className: "steamloader-feature-card" },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-feature-media-shell" },
            slot.mediaImageSrc
              ? createElement("img", {
                  className: "steamloader-feature-media",
                  src: slot.mediaImageSrc,
                  alt: slot.mediaImageAlt || slot.title || "",
                })
              : createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-feature-media-placeholder" },
                    FeatureIcon ? createElement(FeatureIcon, {}) : null,
                  ),
                ),
            slot.eyebrow
              ? createElement("span", {
                  className: "steamloader-feature-eyebrow",
                  children: slot.eyebrow,
                })
              : null,
            slot.badge
              ? createElement("span", {
                  className: "steamloader-badge steamloader-feature-status",
                  children: slot.badge,
                })
              : null,
          ),
        ),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-feature-body" },
            createElement("div", {
              className: "steamloader-feature-title",
              children: slot.title,
            }),
            slot.copy
              ? createElement("div", {
                  className: "steamloader-feature-copy",
                  children: slot.copy,
                })
              : null,
            metaItems.length
              ? createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-feature-meta" },
                    ...metaItems.map((item, metaIndex) =>
                      createElement("span", {
                        className: "steamloader-feature-meta-item",
                        key: `feature-meta-${metaIndex}`,
                        children: item,
                      }),
                    ),
                  ),
                )
              : null,
            createElement(
              "div",
              withChildren(
                { className: "steamloader-feature-footer" },
                createElement("span", {
                  className: "steamloader-feature-footer-copy",
                  children: slot.footerLabel || "Open",
                }),
                trailingNode
                  ? createElement(
                      "span",
                      withChildren(
                        { className: "steamloader-feature-footer-chevron" },
                        trailingNode,
                      ),
                    )
                  : null,
              ),
            ),
          ),
        ),
      ),
    );
  }

  function createInlineStepperRowContent(createElement, withChildren, slot, helpers = {}) {
    const StepperBackIcon = helpers.BackIcon;
    const StepperNextIcon = helpers.ChevronIcon;
    const primaryText = slot.title || slot.copy || "";
    const secondaryText = slot.title && slot.copy ? slot.copy : "";

    return createElement(
      "div",
      withChildren(
        {
          className: `steamloader-inline-stepper${secondaryText ? "" : " is-compact"}`,
        },
        createElement(
          "span",
          withChildren(
            {
              className: `steamloader-inline-stepper-arrow${slot.stepperLeftDisabled ? " is-disabled" : ""}`,
              "aria-hidden": "true",
            },
            StepperBackIcon
              ? createElement(StepperBackIcon, {})
              : createElement("span", {
                  children: "<",
                }),
          ),
        ),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-inline-stepper-main" },
            primaryText
              ? createElement("div", {
                  className: "steamloader-inline-stepper-title",
                  children: primaryText,
                })
              : null,
            secondaryText
              ? createElement("div", {
                  className: "steamloader-inline-stepper-copy",
                  children: secondaryText,
                })
              : null,
          ),
        ),
        createElement(
          "span",
          withChildren(
            {
              className: `steamloader-inline-stepper-arrow${slot.stepperRightDisabled ? " is-disabled" : ""}`,
              "aria-hidden": "true",
            },
            StepperNextIcon
              ? createElement(StepperNextIcon, {})
              : createElement("span", {
                  children: ">",
                }),
          ),
        ),
      ),
    );
  }

  function createRowContent(createElement, withChildren, slot, trailingNode, helpers = {}) {
    if (slot.layout === "accordion") {
      return createAccordionRowContent(createElement, withChildren, slot);
    }

    if (slot.layout === "feature") {
      return createFeatureRowContent(createElement, withChildren, slot, trailingNode);
    }

    if (slot.layout === "stepper") {
      return createInlineStepperRowContent(createElement, withChildren, slot, helpers);
    }

    return createElement(
      "div",
      withChildren(
        { className: buildRowClassName(slot) },
        slot.leadingIcon
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-row-icon" },
                createElement(slot.leadingIcon, {}),
              ),
            )
          : null,
        createElement(
          "div",
          withChildren(
            { className: "steamloader-row-main" },
            createElement("div", {
              className: "steamloader-row-title",
              children: slot.title,
            }),
            slot.copy
              ? createElement("div", {
                  className: "steamloader-row-copy",
                  children: slot.copy,
                })
              : null,
            slot.swatchHex
              ? createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-row-swatch" },
                    createElement("span", {
                      className: "steamloader-row-swatch-dot",
                      style: {
                        background: slot.swatchHex,
                      },
                    }),
                    createElement("span", {
                      className: "steamloader-row-swatch-label",
                      children: slot.swatchLabel || slot.swatchHex,
                    }),
                  ),
                )
              : null,
          ),
        ),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-row-trailing" },
            trailingNode,
          ),
        ),
      ),
    );
  }

  function createRoleProps(slot) {
    const role = slot.role || "action";
    const props = {
      "data-slot-role": role,
    };

    if (role === "toggle") {
      props.role = "switch";
      props["aria-checked"] = Boolean(slot.switchValue);
    }

    if (role === "choice") {
      props.role = "option";
      props["aria-selected"] = Boolean(slot.selected);
    }

    return props;
  }

  function invokeSlotAction(state, slot, index, helpers) {
    if (slot.disabled) {
      return;
    }

    if (slot.role === "toggle") {
      playToggleSound(!slot.switchValue);
    } else if (!state?.nativeUi?.dialogButtonType) {
      playUiSound();
    }

    helpers.handleSlotClick(index);
  }

  function createButtonSlot(state, createElement, withChildren, slot, index, autoFocusIndex, helpers) {
    if (typeof slot?.customRenderer === "function") {
      return slot.customRenderer(slot, index, autoFocusIndex);
    }

    const backNavigation = typeof helpers.getBackNavigation === "function"
      ? helpers.getBackNavigation()
      : null;
    const roleProps = createRoleProps(slot);
    const nativeComponentId = slot.nativeComponentId || nativeComponentByRole[slot.role || "action"] || "dialogButton";
    const nativeAvailable =
      isNativeComponentAvailable(state, nativeComponentId) ||
      isNativeComponentAvailable(state, "dialogButton");

    return createDialogButton(
      state,
      createElement,
      createRowContent(
        createElement,
        withChildren,
        slot,
        typeof helpers.renderTrailingContent === "function"
          ? helpers.renderTrailingContent(slot)
          : renderTrailingContent(createElement, withChildren, slot, helpers),
        helpers,
      ),
      () => invokeSlotAction(state, slot, index, helpers),
      {
        disabled: slot.disabled,
        slotKey: slot.slotKey || null,
        className: slot.buttonClassName || "steamloader-dialog-button",
        extraProps: {
          ...roleProps,
          ...(slot.buttonProps || {}),
          "data-slot-button": String(index),
          "data-slot-key": helpers.resolveSlotFocusKey?.(slot, index) || slot.slotKey || undefined,
          "data-native-component": nativeComponentId,
          "data-native-component-ready": nativeAvailable ? "true" : "false",
          "data-setting-scope": slot.settingScope || undefined,
          "data-setting-key": slot.settingKey || undefined,
          style: slot.buttonStyle || undefined,
          autoFocus: Number.isInteger(autoFocusIndex) && index === autoFocusIndex,
          onGamepadFocus: () => {
            helpers.rememberCurrentRouteSlot?.(index, slot);
            helpers.rememberCurrentRouteIndex?.(index);
            slot.buttonProps?.onGamepadFocus?.();
          },
          onCancelButton: backNavigation
            ? () => {
                helpers.navigateBackFromRoute();
              }
            : undefined,
        },
      },
    );
  }

  function createInfoCard(createElement, withChildren, card, index) {
    return createElement(
      "div",
      withChildren(
        {
          className: "steamloader-card",
          key: `card-${index}`,
        },
        createElement("div", {
          className: "steamloader-card-title",
          children: card.title,
        }),
        card.imageSrc
          ? createElement("div", {
              className: "steamloader-card-image-shell",
              children: createElement("img", {
                className: "steamloader-card-image",
                src: card.imageSrc,
                alt: card.imageAlt || card.title || "",
              }),
            })
          : null,
        ...(Array.isArray(card.lines) ? card.lines : []).map((line, lineIndex) =>
          createElement("div", {
            className: "steamloader-card-line",
            key: `card-line-${index}-${lineIndex}`,
            children: line,
          }),
        ),
        card.swatchHex
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-card-swatch" },
                createElement("span", {
                  className: "steamloader-card-swatch-dot",
                  style: {
                    background: card.swatchHex,
                  },
                }),
                createElement("span", {
                  className: "steamloader-card-swatch-label",
                  children: card.swatchLabel || card.swatchHex,
                }),
              ),
            )
          : null,
      ),
      `steamloader-card-${index}`,
    );
  }

  function createHeaderActionButton(state, createElement, withChildren, action) {
    if (!action || typeof action.onClick !== "function") {
      return null;
    }

    const HeaderActionIcon = action.icon;
    return createDialogButton(
      state,
      createElement,
      createElement(
        "div",
        withChildren(
          { className: "steamloader-header-action-shell" },
          HeaderActionIcon ? createElement(HeaderActionIcon, {}) : null,
        ),
      ),
      action.onClick,
      {
        disabled: action.disabled,
        className: action.buttonClassName || "steamloader-dialog-button steamloader-header-action-button",
        extraProps: {
          "aria-label": action.title || "Action",
          title: action.title || "Action",
          style: action.buttonStyle || undefined,
        },
      },
    );
  }

  function createFooterLegend(createElement, withChildren, items) {
    if (!Array.isArray(items) || !items.length) {
      return null;
    }

    return createElement(
      "div",
      withChildren(
        { className: "steamloader-footer-legend" },
        ...items
          .map((item, index) => {
            if (!item?.button || !item?.label) {
              return null;
            }

            return createElement(
              "div",
              withChildren(
                {
                  className: `steamloader-footer-legend-item${item.active ? " is-active" : ""}`,
                  key: item.key || `footer-legend-${index}`,
                },
                createElement("span", {
                  className: "steamloader-footer-legend-button",
                  children: item.button,
                }),
                createElement("span", {
                  className: "steamloader-footer-legend-label",
                  children: item.label,
                }),
              ),
            );
          })
          .filter(Boolean),
      ),
    );
  }

  function tryInvokeSteamKeyboardOpener(opener, argSets) {
    for (const args of argSets) {
      try {
        opener(...args);
        return true;
      } catch {
      }
    }

    return false;
  }

  function tryPostSteamVirtualKeyboardMessage(message) {
    const payload = {
      type: "VirtualKeyboardMessage",
      message,
    };
    const payloadText = JSON.stringify(payload);
    let posted = false;

    try {
      if (typeof window.SteamClient?.BrowserView?.PostMessageToParent === "function") {
        window.SteamClient.BrowserView.PostMessageToParent(payload.type, payloadText);
        posted = true;
      }
    } catch {
    }

    try {
      if (window.parent && window.parent !== window && typeof window.parent.postMessage === "function") {
        window.parent.postMessage(payload, "*");
        posted = true;
      }
    } catch {
    }

    try {
      if (window.opener && typeof window.opener.postMessage === "function") {
        window.opener.postMessage(payload, "*");
        posted = true;
      }
    } catch {
    }

    return posted;
  }

  let lastTfsSteamKeyboardRequestAt = 0;
  let lastTfsSteamKeyboardRequestKey = "";

  function requestTfsSteamKeyboard(element, description, apiEndpointBase) {
    const base = apiEndpointBase || window.__steamLoaderApiBase || "";
    if (!base || !(element instanceof HTMLElement)) {
      return false;
    }

    const rect = element.getBoundingClientRect();
    const currentValue = element.value || "";
    const payload = {
      label: description || "Text",
      value: currentValue,
      x: rect.left,
      y: rect.top,
      width: rect.width,
      height: rect.height,
    };
    const requestKey = JSON.stringify({
      label: payload.label,
      value: payload.value,
      x: Math.round(payload.x),
      y: Math.round(payload.y),
      width: Math.round(payload.width),
      height: Math.round(payload.height),
    });
    const now = Date.now();

    if (requestKey === lastTfsSteamKeyboardRequestKey && now - lastTfsSteamKeyboardRequestAt < 650) {
      return true;
    }

    lastTfsSteamKeyboardRequestKey = requestKey;
    lastTfsSteamKeyboardRequestAt = now;

    try {
      void fetch(`${base}api/steam/keyboard/show`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        cache: "no-store",
        body: JSON.stringify(payload),
      }).catch(() => {});
      return true;
    } catch {
      return false;
    }
  }

  function tryOpenSteamKeyboard(element, description, apiEndpointBase) {
    if (requestTfsSteamKeyboard(element, description, apiEndpointBase)) {
      return true;
    }

    const label = description || "Text";
    const currentValue = element instanceof HTMLElement ? element.value || "" : "";
    const rect = element instanceof HTMLElement ? element.getBoundingClientRect() : null;
    let opened = false;

    try {
      if (typeof window.navigator?.virtualKeyboard?.show === "function") {
        window.navigator.virtualKeyboard.show();
        opened = true;
      }
    } catch {
    }

    opened = tryPostSteamVirtualKeyboardMessage("ShowVirtualKeyboard") || opened;

    const steamInput = window.SteamClient?.Input;
    if (typeof steamInput?.ShowFloatingGamepadTextInput === "function" && rect) {
      opened =
        tryInvokeSteamKeyboardOpener(steamInput.ShowFloatingGamepadTextInput.bind(steamInput), [
          [0, Math.round(rect.left), Math.round(rect.top), Math.round(rect.width), Math.round(rect.height)],
          [0, Math.round(rect.left), Math.round(rect.top), Math.round(rect.right), Math.round(rect.bottom)],
        ]) || opened;
    }

    if (typeof steamInput?.ShowGamepadTextInput === "function") {
      opened =
        tryInvokeSteamKeyboardOpener(steamInput.ShowGamepadTextInput.bind(steamInput), [
          [0, 0, label, 256, currentValue],
          [0, 0, label, 1024, currentValue],
        ]) || opened;
    }

    const openVrKeyboard = window.SteamClient?.OpenVR?.Keyboard;
    if (typeof openVrKeyboard?.Show === "function") {
      opened =
        tryInvokeSteamKeyboardOpener(openVrKeyboard.Show.bind(openVrKeyboard), [
          [],
          [0, 0, 0, label, 256, currentValue, false, 0],
          [0, 0, label, 256, currentValue],
          [label, currentValue],
        ]) || opened;
    }

    return opened;
  }

  function findEditorTextarea(editorDataKey) {
    for (const element of document.querySelectorAll("#quickaccess_content_7 [data-editor-key]")) {
      if (element.getAttribute("data-editor-key") === editorDataKey) {
        return element;
      }
    }

    return null;
  }

  function getEditorDataKey(element) {
    const value = element?.getAttribute?.("data-editor-key");
    return typeof value === "string" && value.trim() ? value.trim() : "";
  }

  function ensureEditorSelectionStore(state) {
    if (!state.editorSelectionByKey || typeof state.editorSelectionByKey !== "object") {
      state.editorSelectionByKey = {};
    }

    return state.editorSelectionByKey;
  }

  function rememberEditorSelection(state, element) {
    if (
      !state ||
      !(element instanceof HTMLElement) ||
      typeof element.selectionStart !== "number" ||
      typeof element.selectionEnd !== "number"
    ) {
      return null;
    }

    const editorKey = getEditorDataKey(element);
    if (!editorKey) {
      return null;
    }

    const value = typeof element.value === "string" ? element.value : "";
    const selection = {
      start: Math.max(0, Math.min(value.length, element.selectionStart)),
      end: Math.max(0, Math.min(value.length, element.selectionEnd)),
      direction: typeof element.selectionDirection === "string" ? element.selectionDirection : "none",
      value,
    };

    ensureEditorSelectionStore(state)[editorKey] = selection;
    return selection;
  }

  function restoreEditorSelection(state, element, options = {}) {
    if (
      !state ||
      !(element instanceof HTMLElement) ||
      typeof element.setSelectionRange !== "function" ||
      typeof element.value !== "string"
    ) {
      return false;
    }

    const editorKey = getEditorDataKey(element);
    const saved = editorKey ? ensureEditorSelectionStore(state)[editorKey] : null;
    const valueLength = element.value.length;
    const fallback = options.preferEnd ? valueLength : null;
    const startValue = Number.isFinite(saved?.start) ? saved.start : fallback;
    const endValue = Number.isFinite(saved?.end) ? saved.end : startValue;
    if (!Number.isFinite(startValue) || !Number.isFinite(endValue)) {
      return false;
    }

    const start = Math.max(0, Math.min(valueLength, startValue));
    const end = Math.max(0, Math.min(valueLength, endValue));
    const direction = typeof saved?.direction === "string" ? saved.direction : "none";

    try {
      element.setSelectionRange(start, end, direction);
      rememberEditorSelection(state, element);
      return true;
    } catch {
      return false;
    }
  }

  function markEditorFocused(state, editorKey, element = null, helpers = {}) {
    if (!state || !editorKey) {
      return;
    }

    state.editorFocusActive = true;
    state.editorFocusCardKey = editorKey;
    state.editorFocusRouteKey =
      typeof helpers.getRouteKey === "function"
        ? helpers.getRouteKey()
        : state.editorFocusRouteKey || null;
  }

  function clearEditorFocus(state, editorKey = null) {
    if (!state || (editorKey && state.editorFocusCardKey && state.editorFocusCardKey !== editorKey)) {
      return;
    }

    state.editorFocusActive = false;
    state.editorFocusCardKey = null;
    state.editorFocusRouteKey = null;
  }

  function createEditorCard(state, createElement, withChildren, editor, helpers = {}) {
    const editorKey = editor.cardKey || editor.inputKey || "steamloader-editor";
    const editorDataKey = `editor-${editorKey}`;
    const isSecretEditor = editor.inputType === "password" || editor.secret === true;
    const editorElementType = isSecretEditor ? "input" : "textarea";

    const focusEditorTextarea = () => {
      const textarea = findEditorTextarea(editorDataKey);
      if (!(textarea instanceof HTMLElement)) {
        return;
      }

      markEditorFocused(state, editorDataKey, null, helpers);
      textarea.focus({ preventScroll: true });
      restoreEditorSelection(state, textarea, { preferEnd: true });
      tryOpenSteamKeyboard(textarea, editor.label, helpers.apiBase);
      window.requestAnimationFrame(() => tryOpenSteamKeyboard(textarea, editor.label, helpers.apiBase));
      window.setTimeout(() => tryOpenSteamKeyboard(textarea, editor.label, helpers.apiBase), 120);
    };

    return createElement(
      "div",
      withChildren(
        {
          className: "steamloader-editor-card",
        },
        createDialogButton(
          state,
          createElement,
          createElement(
            "div",
            withChildren(
              { className: "steamloader-editor-trigger-content" },
              createElement("div", {
                className: "steamloader-editor-label",
                children: editor.label,
              }),
              editor.help
                ? createElement("div", {
                    className: "steamloader-editor-help",
                    children: editor.help,
                  })
                : null,
            ),
          ),
          focusEditorTextarea,
          {
            slotKey: editorDataKey,
            className: "steamloader-dialog-button steamloader-editor-trigger",
            extraProps: {
              "data-slot-button": editorDataKey,
              "data-slot-key": editorDataKey,
              onOKButton: focusEditorTextarea,
              onActivate: focusEditorTextarea,
              onGamepadFocus: () => {
                if (typeof helpers.resolveSlotFocusKey === "function") {
                  helpers.rememberCurrentRouteSlot?.(null, { slotKey: editorDataKey });
                }
              },
              onCancelButton: () => {
                helpers.navigateBackFromRoute?.();
              },
              style: {
                width: "100%",
                minWidth: 0,
              },
            },
          },
        ),
        createElement(editorElementType, {
          key: editor.inputKey,
          className: `steamloader-editor-textarea${isSecretEditor ? " steamloader-editor-input-secret" : ""}`,
          "data-editor-key": editorDataKey,
          "data-custom-path-input": editor.isCustomPath ? "true" : undefined,
          type: isSecretEditor ? "password" : undefined,
          defaultValue: editor.value || "",
          placeholder: editor.placeholder || "",
          rows: isSecretEditor ? undefined : editor.rows || 3,
          spellCheck: false,
          autoCapitalize: "off",
          autoCorrect: "off",
          autoComplete: isSecretEditor ? "new-password" : "off",
          onClick: (event) => {
            event.stopPropagation();
            markEditorFocused(state, editorDataKey, event.target, helpers);
            rememberEditorSelection(state, event.target);
            tryOpenSteamKeyboard(event.target, editor.label, helpers.apiBase);
          },
          onFocus: (event) => {
            markEditorFocused(state, editorDataKey, event.target, helpers);
          },
          onBlur: (event) => {
            rememberEditorSelection(state, event.target);
            window.setTimeout(() => {
              const panel = document.querySelector("#quickaccess_content_7 .steamloader-panel");
              const activeElement = document.activeElement;
              if (
                state.editorFocusCardKey !== editorDataKey ||
                activeElement === document.body ||
                activeElement?.getAttribute?.("data-editor-key") === editorDataKey
              ) {
                return;
              }

              if (panel instanceof HTMLElement && activeElement instanceof HTMLElement && panel.contains(activeElement)) {
                clearEditorFocus(state, editorDataKey);
              }
            }, 120);
          },
          onInput: (event) => {
            editor.onInput?.(event.target.value);
            rememberEditorSelection(state, event.target);
          },
          onSelect: (event) => {
            rememberEditorSelection(state, event.target);
          },
          onKeyUp: (event) => {
            rememberEditorSelection(state, event.target);
          },
        }),
      ),
      editorKey,
    );
  }

  function createSecretEditor(options = {}) {
    const configured = Boolean(options.configured);
    return {
      ...options,
      inputType: "password",
      secret: true,
      value: "",
      rows: 1,
      placeholder:
        options.placeholder ||
        (configured ? "Enter a new value to replace the stored secret." : "Enter secret value."),
      help:
        options.help ||
        (configured ? "A secret is configured. The saved value cannot be read back." : "No secret is configured yet."),
    };
  }

  function createDivider(createElement, key) {
    return createElement("div", {
      className: "steamloader-divider",
      key,
      "aria-hidden": "true",
    });
  }

  function hasDividerAfter(model, index) {
    if (Number.isInteger(model?.dividerAfterIndex) && index === model.dividerAfterIndex) {
      return true;
    }

    return Array.isArray(model?.dividerAfterIndices) && model.dividerAfterIndices.includes(index);
  }

  function shouldSeparateAfterSlot(slot) {
    return slot?.role === "back" || slot?.trailing === "back";
  }

  function getInlineSectionHeaders(model, index) {
    return (Array.isArray(model?.sectionHeaders) ? model.sectionHeaders : []).filter((section) =>
      Number.isInteger(section?.index) && section.index === index,
    );
  }

  function createInlineSectionHeader(createElement, withChildren, section, key) {
    const SectionIcon = section?.icon;
    return createElement(
      "div",
      withChildren(
        {
          className: "steamloader-inline-section",
          key,
        },
        SectionIcon
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-inline-section-mark" },
                createElement(SectionIcon, {}),
              ),
            )
          : null,
        createElement(
          "div",
          withChildren(
            { className: "steamloader-inline-section-copy-wrap" },
            createElement("div", {
              className: "steamloader-inline-section-title",
              children: section?.title || "",
            }),
            section?.copy
              ? createElement("div", {
                  className: "steamloader-inline-section-copy",
                  children: section.copy,
                })
              : null,
          ),
        ),
      ),
    );
  }

  function createVolumeActionButton(state, createElement, withChildren, action, index, helpers = {}) {
    const ActionIcon = action.icon || null;

    return createDialogButton(
      state,
      createElement,
      createElement(
        "div",
        withChildren(
          { className: "steamloader-volume-action-shell" },
          ActionIcon
            ? createElement(
                "div",
                withChildren(
                  { className: "steamloader-volume-action-icon" },
                  createElement(ActionIcon, {}),
                ),
              )
            : null,
          createElement("div", {
            className: "steamloader-volume-action-title",
            children: action.title,
          }),
        ),
      ),
      action.onClick,
      {
        disabled: action.disabled,
        className: "steamloader-dialog-button steamloader-volume-action-button",
        extraProps: {
          autoFocus: action.autoFocus && helpers.getActiveVolumeActionIndex?.() === index,
          onGamepadFocus: () => {
            helpers.rememberVolumeActionFocus?.(index);
          },
          onCancelButton: () => {
            action.onCancel?.();
          },
          style: {
            width: "100%",
            minWidth: 0,
            padding: "8px 10px",
          },
        },
      },
    );
  }

  function createFallbackVolumeSlider(state, createElement, withChildren, slider, shouldAutoFocusAction, helpers = {}) {
    const min = Number.isFinite(slider.min) ? slider.min : 0;
    const max = Number.isFinite(slider.max) ? slider.max : 100;
    const range = Math.max(1, max - min);
    const notchCount = Number.isInteger(slider.notchCount) && slider.notchCount > 1 ? slider.notchCount : 11;
    const value = Math.max(min, Math.min(max, Math.round(Number(slider.value) || 0)));
    const percent = ((value - min) / range) * 100;

    return createDialogButton(
      state,
      createElement,
      createElement(
        "div",
        withChildren(
          { className: "steamloader-volume-slider-fallback-shell" },
          createElement(
            "div",
            withChildren(
              { className: "steamloader-volume-slider-fallback-head" },
              createElement("div", {
                className: "steamloader-volume-slider-label",
                children: slider.title,
              }),
              createElement("div", {
                className: "steamloader-volume-slider-value",
                children: `${value}${slider.valueSuffix || ""}`,
              }),
            ),
          ),
          createElement(
            "div",
            withChildren(
            { className: "steamloader-volume-slider-track-shell", "aria-hidden": "true" },
            createElement("div", {
              className: "steamloader-volume-slider-track",
              style: slider.trackStyle || undefined,
            }),
            ...Array.from({ length: notchCount }, (_, index) =>
              createElement("span", {
                key: `volume-slider-notch-${index}`,
                className: "steamloader-volume-slider-notch",
                  style: {
                    left: `${(index / Math.max(1, notchCount - 1)) * 100}%`,
                  },
                }),
              ),
            createElement("div", {
              className: "steamloader-volume-slider-fill",
              style: {
                width: `${percent}%`,
                ...(slider.fillStyle || {}),
              },
            }),
            createElement("div", {
              className: "steamloader-volume-slider-thumb",
              style: {
                left: `${percent}%`,
                ...(slider.thumbStyle || {}),
              },
            }),
          ),
        ),
        ),
      ),
      () => {
        helpers.rememberVolumeActionFocus?.(0);
        if (slider.isEditing) {
          slider.onDeactivate?.();
          return;
        }

        slider.onActivate?.();
      },
      {
        disabled: slider.disabled,
        className: `steamloader-dialog-button steamloader-volume-slider-fallback-button${slider.isEditing ? " is-editing" : ""}`,
        extraProps: {
          "data-volume-slider": "true",
          autoFocus: shouldAutoFocusAction && helpers.getActiveVolumeActionIndex?.() === 0,
          onGamepadFocus: () => {
            helpers.rememberVolumeActionFocus?.(0);
          },
          onCancelButton: () => {
            if (slider.isEditing) {
              slider.onDeactivate?.();
              return;
            }

            slider.onCancel?.();
          },
          onMoveLeft: (event) => {
            helpers.rememberVolumeActionFocus?.(0);
            slider.onMoveLeft?.(event);
          },
          onMoveRight: (event) => {
            helpers.rememberVolumeActionFocus?.(0);
            slider.onMoveRight?.(event);
          },
          style: {
            width: "100%",
            minWidth: 0,
            padding: "10px 12px",
          },
        },
      },
    );
  }

  function createVolumeSliderControl(state, createElement, withChildren, slider, shouldAutoFocusAction, helpers = {}) {
    return createFallbackVolumeSlider(
      state,
      createElement,
      withChildren,
      slider,
      shouldAutoFocusAction,
      helpers,
    );
  }

  function createVolumePanel(state, createElement, withChildren, panel, helpers = {}) {
    const shouldAutoFocusAction = Boolean(helpers.consumeVolumeActionAutoFocus?.());
    const hasSlider = Boolean(panel.slider);
    const hasActions = Array.isArray(panel.actions) && panel.actions.length > 0;

    return createElement(
      "div",
      withChildren(
        { className: "steamloader-volume-card" },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-volume-head" },
            createElement(
              "div",
              withChildren(
                { className: "steamloader-volume-copy-wrap" },
                createElement("div", {
                  className: "steamloader-volume-title",
                  children: panel.title,
                }),
                createElement("div", {
                  className: "steamloader-volume-copy",
                  children: panel.copy,
                }),
              ),
            ),
          ),
        ),
        hasSlider
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-volume-slider-wrap" },
                createVolumeSliderControl(
                  state,
                  createElement,
                  withChildren,
                  panel.slider,
                  shouldAutoFocusAction,
                  helpers,
                ),
              ),
            )
          : null,
        createElement("div", {
          className: panel.error
            ? "steamloader-volume-hint steamloader-volume-hint-error"
            : "steamloader-volume-hint",
          children: panel.error || panel.hint,
        }),
        hasActions ? createDivider(createElement, "volume-panel-actions-divider") : null,
        createElement(
          "div",
          withChildren(
            { className: "steamloader-volume-actions" },
            ...(Array.isArray(panel.actions) ? panel.actions : []).map((action, index) =>
              createVolumeActionButton(
                state,
                createElement,
                withChildren,
                {
                  ...action,
                  autoFocus: shouldAutoFocusAction,
                },
                hasSlider ? index + 1 : index,
                helpers,
              ),
            ),
          ),
        ),
      ),
    );
  }

  function createPanelShell(state, createElement, withChildren, model, helpers = {}) {
    state.nativeUi ??= {};
    state.nativeUi.renderError = "";

    const HeaderIcon = model.headerIcon === null ? null : model.headerIcon || helpers.DefaultIcon;
    const headerActions = Array.isArray(model.headerActions) ? model.headerActions : [];
    const topSlots = Array.isArray(model.topSlots) ? model.topSlots : [];
    const slots = Array.isArray(model.slots) ? model.slots : [];
    state.renderedSlots = getRenderableSlots(model);
    state.slotActions = state.renderedSlots.map((slot) => slot.onClick);
    helpers.consumeResolvedFocus?.(state.route, model.autoFocusIndex);

    const topSlotChildren = topSlots.flatMap((slot, index) => {
      const children = [
        createButtonSlot(state, createElement, withChildren, slot, index, model.autoFocusIndex, helpers),
      ];
      if (shouldSeparateAfterSlot(slot)) {
        children.push(createDivider(createElement, `top-back-divider-${index}`));
      }

      return children;
    });
    const slotIndexOffset = topSlots.length;
    const slotChildren = slots.flatMap((slot, index) => {
      const slotIndex = slotIndexOffset + index;
      const sectionHeaders = getInlineSectionHeaders(model, index).map((section, sectionIndex) =>
        createInlineSectionHeader(
          createElement,
          withChildren,
          section,
          `section-${index}-${section.sectionKey || sectionIndex}`,
        ),
      );
      const children = [
        ...sectionHeaders,
        createButtonSlot(state, createElement, withChildren, slot, slotIndex, model.autoFocusIndex, helpers),
      ];

      if (hasDividerAfter(model, index) || shouldSeparateAfterSlot(slot)) {
        children.push(createDivider(createElement, `divider-${slotIndex}`));
      }

      return children;
    });

    return createElement(
      "div",
      withChildren(
        {
          className: model.panelClassName
            ? `steamloader-panel ${model.panelClassName}`
            : "steamloader-panel",
          "data-route-key": model.routeKey || "",
          "data-st-frontend-lib-version": String(window.STFrontendLib?.version || 37),
          "data-st-renderer": "st-frontend-lib",
        },
        createElement(
          "div",
          withChildren(
            { className: "steamloader-header" },
            createElement(
              "div",
              withChildren(
                { className: "steamloader-header-main" },
                HeaderIcon
                  ? createElement(
                      "div",
                      withChildren({ className: "steamloader-header-mark" }, createElement(HeaderIcon, {})),
                    )
                  : null,
                createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-title-wrap" },
                    createElement("h1", {
                      className: "steamloader-title",
                      children: model.title,
                    }),
                    model.subtitle
                      ? createElement("div", {
                          className: "steamloader-subtitle",
                          children: model.subtitle,
                        })
                      : null,
                  ),
                ),
              ),
            ),
            headerActions.length
              ? createElement(
                  "div",
                  withChildren(
                    { className: "steamloader-header-actions" },
                    ...headerActions
                      .map((action) => createHeaderActionButton(state, createElement, withChildren, action))
                      .filter(Boolean),
                  ),
                )
              : null,
          ),
        ),
        topSlotChildren.length
          ? createElement(
              "div",
              withChildren(
                { className: "steamloader-stack steamloader-top-stack" },
                ...topSlotChildren,
              ),
            )
          : null,
        model.error
          ? createElement("div", {
              className: "steamloader-error",
              children: model.error,
            })
          : null,
        ...(Array.isArray(model.cards)
          ? model.cards.map((card, index) => createInfoCard(createElement, withChildren, card, index))
          : []),
        model.editor ? createEditorCard(state, createElement, withChildren, model.editor, helpers) : null,
        ...(Array.isArray(model.editors)
          ? model.editors.map((editor, index) =>
              createEditorCard(state, createElement, withChildren, {
                ...editor,
                cardKey: editor.cardKey || editor.inputKey || `steamloader-editor-${index}`,
              }, helpers),
            )
          : []),
        createElement(
          "div",
          withChildren(
            { className: "steamloader-stack" },
            ...slotChildren,
          ),
        ),
        model.volumePanel
          ? createVolumePanel(state, createElement, withChildren, model.volumePanel, helpers)
          : null,
        createFooterLegend(createElement, withChildren, model.footerLegend),
      ),
    );
  }

  function joinApiPath(apiBase, path) {
    const base = String(apiBase || window.__steamLoaderApiBase || "");
    const relativePath = String(path || "").replace(/^\/+/, "");
    if (!base) {
      return relativePath;
    }

    return `${base.replace(/\/+$/, "")}/${relativePath}`;
  }

  function createPluginSdk(manifest = {}, options = {}) {
    const apiBase = options.apiBase || window.__steamLoaderApiBase || "";
    const pluginId = String(options.pluginId || manifest.id || "").trim();
    const pluginApiBase = pluginId
      ? `api/plugin-sdk/plugins/${encodeURIComponent(pluginId)}`
      : "";

    async function request(path, requestOptions = {}) {
      const headers = { ...(requestOptions.headers || {}) };
      let body = requestOptions.body;
      const hasJsonBody =
        body &&
        typeof body === "object" &&
        !(typeof FormData !== "undefined" && body instanceof FormData) &&
        !(typeof Blob !== "undefined" && body instanceof Blob);

      if (hasJsonBody) {
        headers["Content-Type"] = headers["Content-Type"] || "application/json";
        body = JSON.stringify(body);
      }

      const response = await fetch(joinApiPath(apiBase, path), {
        ...requestOptions,
        headers,
        body,
      });
      const contentType = response.headers.get("content-type") || "";
      const payload = contentType.includes("application/json")
        ? await response.json()
        : await response.text();

      if (!response.ok) {
        const message = payload && typeof payload === "object" && payload.message
          ? payload.message
          : `TFS API request failed (${response.status}).`;
        throw new Error(message);
      }

      return payload;
    }

    function ensurePluginId() {
      if (!pluginApiBase) {
        throw new Error("TFS plugin SDK requires a manifest id.");
      }
    }

    function pluginRequest(path, requestOptions = {}) {
      ensurePluginId();
      return request(`${pluginApiBase}/${String(path || "").replace(/^\/+/, "")}`, requestOptions);
    }

    const storage = {
      async get() {
        const payload = await pluginRequest("settings", { method: "GET" });
        return payload?.settings || {};
      },
      async set(settings = {}) {
        const payload = await pluginRequest("settings", { method: "POST", body: settings });
        return payload?.settings || {};
      },
      async patch(partialSettings = {}) {
        const current = await storage.get();
        return storage.set({ ...current, ...partialSettings });
      },
    };

    const secrets = {
      async status() {
        const payload = await pluginRequest("secrets", { method: "GET" });
        return payload?.secrets || {};
      },
      async set(key, value) {
        const payload = await pluginRequest(`secrets/${encodeURIComponent(key)}`, {
          method: "POST",
          body: { value: String(value ?? "") },
        });
        return payload?.secrets || {};
      },
      async clear(key) {
        const payload = await pluginRequest(`secrets/${encodeURIComponent(key)}/clear`, {
          method: "POST",
          body: {},
        });
        return payload?.secrets || {};
      },
    };

    const network = {
      request(networkRequest = {}) {
        return pluginRequest("network/request", {
          method: "POST",
          body: {
            method: networkRequest.method || "GET",
            url: networkRequest.url || "",
            headers: networkRequest.headers || {},
            body: networkRequest.body,
            authorizationSecretKey: networkRequest.authorizationSecretKey || "",
            authorizationScheme: networkRequest.authorizationScheme || "Bearer",
          },
        });
      },
      get(url, requestOptions = {}) {
        return network.request({ ...requestOptions, method: "GET", url });
      },
      post(url, body = {}, requestOptions = {}) {
        return network.request({ ...requestOptions, method: "POST", url, body });
      },
    };

    return {
      version: 1,
      pluginId,
      manifest: { ...manifest, id: pluginId || manifest.id || "" },
      apiBase,
      request,
      get: (path, requestOptions = {}) => request(path, { ...requestOptions, method: "GET" }),
      post: (path, body = {}, requestOptions = {}) => request(path, { ...requestOptions, method: "POST", body }),
      state: () => pluginRequest("state", { method: "GET" }),
      storage,
      secrets,
      network,
      ui: {
        createSlot,
        createNavigationSlot,
        createBackSlot,
        createToggleSlot,
        createChoiceSlot,
        createCommandSlot,
        createAccordionSlot,
        createFeatureNavigationSlot,
        createInlineStepperSlot,
        createSecretEditor,
        createScreenModel,
        createPanelShell,
      },
      diagnostics: () => ({
        libraryVersion: window.STFrontendLib?.version || 39,
        sdkVersion: 1,
        pluginId,
      }),
    };
  }

  function createDiagnostics(state) {
    const registry = getNativeRegistry(state);
    const localRegistry = refreshLocalRegistry();

    return {
      version: 39,
      renderer: "st-frontend-lib",
      hasDialogButtonType: Boolean(state?.nativeUi?.dialogButtonType),
      steamToggleStyleAvailable: Boolean(state?.nativeUi?.steamToggleStyleAvailable),
      localRegistryAvailable: localRegistry.availableCount || 0,
      localRegistryTotal: localRegistry.totalCount || 0,
      registryVersion: registry?.version || 0,
      registryAvailable: registry?.availableCount || 0,
      registryTotal: registry?.totalCount || 0,
      lastRenderError: state?.nativeUi?.renderError || "",
    };
  }

  window.STFrontendLib = {
    version: 39,
    defaultModel,
    getReactPropertyKey,
    getReactFiber,
    getQuickAccessRootFiber,
    captureNativeUi,
    playUiSound,
    playToggleSound,
    getNativeRegistry,
    getNativeComponent,
    isNativeComponentAvailable,
    refreshLocalRegistry,
    getResolvedNativeComponent,
    refreshComponentRegistry,
    canUseSteamToggleStyle,
    createDialogButton,
    renderSwitchAccessory,
    renderTrailingContent,
    createSlot,
    createNavigationSlot,
    createBackSlot,
    createToggleSlot,
    createSettingToggleSlot,
    createChoiceSlot,
    createCommandSlot,
    createAccordionSlot,
    createFeatureNavigationSlot,
    createInlineStepperSlot,
    createScreenModel,
    buildRowClassName,
    createRowContent,
    createRoleProps,
    createButtonSlot,
    createInfoCard,
    createEditorCard,
    createSecretEditor,
    createDivider,
    createVolumeActionButton,
    createVolumePanel,
    createPanelShell,
    createPluginSdk,
    createDiagnostics,
  };

  window.TfsPluginSdk = {
    version: 1,
    create: createPluginSdk,
  };
})();
