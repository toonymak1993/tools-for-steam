export type TfsPluginPermission =
  | "frontend"
  | "storage"
  | "secrets"
  | "network"
  | "files"
  | "notifications"
  | "logging"
  | "native.audio"
  | "native.processes"
  | "native.display"
  | "native.themes"
  | "native.artwork"
  | "native.app-start"
  | "native.store-sync"
  | "native.automation"
  | "native.performance"
  | "native.power"
  | "native.full-trust";

export interface TfsPluginBackendManifest {
  entryPoint: string;
  runtime?: "executable" | "powershell" | "python" | "node";
  runtimeExecutable?: string;
  arguments?: string[];
  environment?: Record<string, string>;
  secretEnvironment?: Record<string, string>;
  autoStart?: boolean;
  createNoWindow?: boolean;
}

export interface TfsPluginManifest {
  id: string;
  name: string;
  description?: string;
  version: string;
  sdkVersion: string;
  entryPoint?: string;
  permissions: TfsPluginPermission[];
  networkHosts?: string[];
  backend?: TfsPluginBackendManifest;
}

export interface TfsPluginScreenContext {
  route: unknown;
  plugin: unknown;
  runtime: unknown;
  sdk: TfsPluginSdk;
  refresh(): void;
}

export interface TfsPluginDefinition {
  createScreen(context: TfsPluginScreenContext): TfsScreenModel;
  dispose?(): void;
}

export interface TfsPluginSdkState {
  pluginId: string;
  sdkVersion: string;
  entryPoint: string;
  permissions: TfsPluginPermission[];
  networkHosts: string[];
  settings: Record<string, unknown>;
  secrets: Record<string, boolean>;
}

export interface TfsNetworkRequest {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  url: string;
  headers?: Record<string, string>;
  body?: unknown;
  authorizationSecretKey?: string;
  authorizationScheme?: string;
}

export interface TfsNetworkResponse {
  statusCode: number;
  ok: boolean;
  contentType: string;
  bodyText: string;
  headers: Record<string, string>;
  text(): string;
  json<T = unknown>(): T;
}

export interface TfsFileEntry {
  path: string;
  name: string;
  isDirectory: boolean;
  size: number;
  modifiedUtc: string;
}

export interface TfsFileListState {
  path: string;
  entries: TfsFileEntry[];
  usedBytes: number;
  maxBytes: number;
}

export interface TfsFileMutationState {
  path: string;
  exists: boolean;
  isDirectory: boolean;
  size: number;
  usedBytes: number;
  maxBytes: number;
}

export interface TfsNotificationOptions {
  level?: "info" | "success" | "warning" | "error";
  durationMs?: number;
}

export interface TfsNotificationState {
  id: string;
  pluginId: string;
  title: string;
  message: string;
  level: "info" | "success" | "warning" | "error";
  durationMs: number;
  createdAtUtc: string;
}

export interface TfsAudioDevice {
  id: string;
  name: string;
  isDefault: boolean;
}

export interface TfsAudioVolume {
  deviceId: string;
  deviceName: string;
  volume: number;
  isMuted: boolean;
}

export interface TfsAudioMixerSession {
  sessionId: string;
  displayName: string;
  secondaryLabel: string;
  processId: number | null;
  isSystemSession: boolean;
  volume: number;
  isMuted: boolean;
  sessionCount: number;
}

export interface TfsAudioState {
  playbackVolume: TfsAudioVolume | null;
  captureVolume: TfsAudioVolume | null;
  playbackDevices: TfsAudioDevice[];
  captureDevices: TfsAudioDevice[];
  mixerSessions: TfsAudioMixerSession[];
}

export interface TfsProcessWindow {
  handle: string;
  title: string;
  processName: string;
  processId: number;
  isMinimized: boolean;
  isForeground: boolean;
}

export interface TfsProcessesState {
  windows: TfsProcessWindow[];
  statusText: string;
}

export interface TfsDisplayPreset {
  id: string;
  title: string;
  description: string;
  available: boolean;
  selected: boolean;
}

export interface TfsDisplayState {
  statusText: string;
  display: { deviceName: string; deviceLabel: string };
  currentResolution: { width: number; height: number; label: string } | null;
  currentRefreshRate: { refreshRate: number; label: string } | null;
  resolutionPresets: TfsDisplayPreset[];
  refreshRatePresets: TfsDisplayPreset[];
}

export interface TfsAppStartCatalogEntry {
  id: string;
  name: string;
  sourcePath: string;
  iconDataUri: string | null;
  added: boolean;
  favorite: boolean;
  hidden: boolean;
  sourceKind: "desktop" | "packaged";
}

export interface TfsAppStartShortcut {
  id: string;
  name: string;
  sourcePath: string;
  iconDataUri: string | null;
  favorite: boolean;
  sourceKind: "desktop" | "packaged";
}

export interface TfsPerformanceRuntimeState {
  elevated: boolean;
  overlayVisible: boolean;
  helperProcessId: number;
  targetProcessId: number;
  targetProcessName: string;
  targetWindowTitle: string;
  framesPerSecond: number;
  frameTimeMs: number;
  onePercentLowFps: number;
  framePacingMs: number;
  targetCpuPercent: number;
  targetMemoryMb: number;
  detailText: string;
  errorText: string;
  updatedAt: string;
}

export interface TfsPerformanceState {
  installation: Record<string, unknown>;
  settings: Record<string, unknown> & {
    overlayLevel: number;
    autoTargetEnabled: boolean;
  };
  runtime: TfsPerformanceRuntimeState;
  vendorOverlays: Array<Record<string, unknown>>;
  statusText: string;
}

export interface TfsPowerActionState {
  id: "startWindowsDesktop" | "restartSteam" | "sleepWindows" | "restartWindows" | "shutdownWindows";
  title: string;
  disruptive: boolean;
}

export interface TfsPowerState {
  actions: TfsPowerActionState[];
  confirmationRequired: true;
}

export interface TfsPowerConfirmation {
  confirmed: true;
}

export interface TfsUiSlotOptions {
  role?: string;
  disabled?: boolean;
  badge?: string;
  trailing?: string;
  switchValue?: boolean;
  switchLabel?: string;
  leadingIcon?: unknown;
  buttonClassName?: string;
  buttonStyle?: Record<string, unknown> | null;
  buttonProps?: Record<string, unknown> | null;
  rowClassName?: string;
  slotKey?: string;
  key?: string;
  selected?: boolean;
  value?: unknown;
  layout?: string;
  expanded?: boolean;
  eyebrow?: string;
  meta?: unknown[];
  mediaImageSrc?: string;
  mediaImageAlt?: string;
  footerLabel?: string;
  min?: number;
  max?: number;
  valueLabel?: string;
  label?: string;
  leftDisabled?: boolean;
  rightDisabled?: boolean;
  onClick?: TfsUiAction;
}

export type TfsUiAction = (event?: unknown) => void | Promise<void>;

export interface TfsUiSlot extends TfsUiSlotOptions {
  kind: "button";
  title: string;
  copy: string;
  onClick: TfsUiAction;
}

export interface TfsScreenModel {
  title?: string;
  subtitle?: string;
  note?: string;
  error?: string;
  autoFocusIndex?: number;
  headerIcon?: unknown;
  cards?: unknown[];
  sectionHeaders?: unknown[];
  topSlots?: TfsUiSlot[];
  slots?: TfsUiSlot[];
  [key: string]: unknown;
}

export interface TfsPluginUi {
  createSlot(title: string, copy: string, onClick: TfsUiAction, options?: TfsUiSlotOptions): TfsUiSlot;
  createNavigationSlot(title: string, copy: string, onClick: TfsUiAction, options?: TfsUiSlotOptions): TfsUiSlot;
  createBackSlot(title: string, copy: string, onClick: TfsUiAction, options?: TfsUiSlotOptions): TfsUiSlot;
  createToggleSlot(title: string, copy: string, value: boolean, onClick: TfsUiAction, options?: TfsUiSlotOptions): TfsUiSlot;
  createChoiceSlot(title: string, copy: string, onClick: TfsUiAction, options?: TfsUiSlotOptions): TfsUiSlot;
  createCommandSlot(title: string, copy: string, onClick: TfsUiAction, options?: TfsUiSlotOptions): TfsUiSlot;
  createAccordionSlot(title: string, copy: string, expanded: boolean, onClick: TfsUiAction, options?: TfsUiSlotOptions): TfsUiSlot;
  createFeatureNavigationSlot(title: string, copy: string, onClick: TfsUiAction, options?: TfsUiSlotOptions): TfsUiSlot;
  createInlineStepperSlot(
    title: string,
    copy: string,
    onMoveLeft: TfsUiAction,
    onMoveRight: TfsUiAction,
    options?: TfsUiSlotOptions,
  ): TfsUiSlot;
  createSliderSlot(
    title: string,
    value: number,
    onMoveLeft: TfsUiAction,
    onMoveRight: TfsUiAction,
    options?: TfsUiSlotOptions,
  ): TfsUiSlot;
  createProgressSlot(
    title: string,
    copy: string,
    value: number,
    options?: TfsUiSlotOptions,
  ): TfsUiSlot;
  createSecretEditor(options?: Record<string, unknown>): Record<string, unknown>;
  createScreenModel(overrides?: TfsScreenModel): TfsScreenModel;
  createPanelShell(
    state: unknown,
    createElement: (...args: any[]) => unknown,
    withChildren: (...args: any[]) => unknown,
    model: TfsScreenModel,
    helpers?: Record<string, unknown>,
  ): unknown;
}

export type TfsObservableSource = "audio" | "processes" | "display" | "performance";

export interface TfsWatchOptions {
  intervalMs?: number;
  immediate?: boolean;
  onError?: (error: unknown) => void;
}

export interface TfsPluginSdk {
  readonly version: number;
  readonly sdkVersion: "1.0.0";
  readonly pluginId: string;
  readonly manifest: TfsPluginManifest;
  readonly permissions: readonly TfsPluginPermission[];
  hasPermission(permission: TfsPluginPermission | string): boolean;
  state(): Promise<TfsPluginSdkState>;
  dispose(): void;

  storage: {
    get<T extends Record<string, unknown> = Record<string, unknown>>(): Promise<T>;
    set<T extends Record<string, unknown>>(settings: T): Promise<T>;
    patch<T extends Record<string, unknown>>(settings: Partial<T>): Promise<T>;
    remove(...keys: Array<string | string[]>): Promise<Record<string, unknown>>;
    clear(): Promise<Record<string, unknown>>;
  };

  secrets: {
    status(): Promise<Record<string, boolean>>;
    set(key: string, value: string): Promise<Record<string, boolean>>;
    clear(key: string): Promise<Record<string, boolean>>;
  };

  network: {
    request(options: TfsNetworkRequest): Promise<TfsNetworkResponse>;
    get(url: string, options?: Omit<TfsNetworkRequest, "method" | "url">): Promise<TfsNetworkResponse>;
    post(url: string, body?: unknown, options?: Omit<TfsNetworkRequest, "method" | "url" | "body">): Promise<TfsNetworkResponse>;
    put(url: string, body?: unknown, options?: Omit<TfsNetworkRequest, "method" | "url" | "body">): Promise<TfsNetworkResponse>;
    patch(url: string, body?: unknown, options?: Omit<TfsNetworkRequest, "method" | "url" | "body">): Promise<TfsNetworkResponse>;
    delete(url: string, options?: Omit<TfsNetworkRequest, "method" | "url">): Promise<TfsNetworkResponse>;
  };

  files: {
    list(path?: string, options?: { recursive?: boolean }): Promise<TfsFileListState>;
    stat(path?: string): Promise<TfsFileMutationState>;
    readText(path: string): Promise<string>;
    readBytes(path: string): Promise<Uint8Array>;
    writeText(path: string, content: string, options?: { append?: boolean; overwrite?: boolean }): Promise<TfsFileMutationState>;
    appendText(path: string, content: string): Promise<TfsFileMutationState>;
    writeBytes(path: string, content: ArrayBuffer | ArrayBufferView, options?: { append?: boolean; overwrite?: boolean }): Promise<TfsFileMutationState>;
    mkdir(path?: string): Promise<TfsFileMutationState>;
    remove(path: string, options?: { recursive?: boolean }): Promise<TfsFileMutationState>;
    move(sourcePath: string, destinationPath: string, options?: { overwrite?: boolean }): Promise<TfsFileMutationState>;
    copy(sourcePath: string, destinationPath: string, options?: { overwrite?: boolean }): Promise<TfsFileMutationState>;
  };

  notifications: {
    show(title: string, message: string, options?: TfsNotificationOptions): Promise<TfsNotificationState>;
    success(title: string, message: string, options?: Omit<TfsNotificationOptions, "level">): Promise<TfsNotificationState>;
    warning(title: string, message: string, options?: Omit<TfsNotificationOptions, "level">): Promise<TfsNotificationState>;
    error(title: string, message: string, options?: Omit<TfsNotificationOptions, "level">): Promise<TfsNotificationState>;
  };

  log: {
    debug(message: string, data?: unknown): Promise<unknown>;
    info(message: string, data?: unknown): Promise<unknown>;
    warning(message: string, data?: unknown): Promise<unknown>;
    warn(message: string, data?: unknown): Promise<unknown>;
    error(message: string, data?: unknown): Promise<unknown>;
  };

  lifecycle: {
    readonly disposed: boolean;
    readonly signal: AbortSignal | null;
    onDispose(callback: () => void): () => void;
    setTimeout(callback: () => void, delayMs?: number): () => void;
    setInterval(callback: () => void, delayMs?: number): () => void;
    dispose(): void;
  };

  events: {
    watch<T = unknown>(
      source: TfsObservableSource | (() => Promise<T> | T),
      listener: (snapshot: T, metadata: { initial: boolean }) => void | Promise<void>,
      options?: TfsWatchOptions,
    ): () => void;
  };

  audio: {
    getState(): Promise<TfsAudioState>;
    setDefaultPlayback(deviceId: string): Promise<TfsAudioState>;
    setDefaultCapture(deviceId: string): Promise<TfsAudioState>;
    setPlaybackVolume(volume: number): Promise<TfsAudioState>;
    setCaptureVolume(volume: number): Promise<TfsAudioState>;
    adjustPlaybackVolume(delta: number): Promise<TfsAudioState>;
    adjustCaptureVolume(delta: number): Promise<TfsAudioState>;
    togglePlaybackMute(): Promise<TfsAudioState>;
    toggleCaptureMute(): Promise<TfsAudioState>;
    setMixerVolume(sessionId: string, volume: number): Promise<TfsAudioState>;
    toggleMixerMute(sessionId: string): Promise<TfsAudioState>;
  };

  processes: {
    getState(): Promise<TfsProcessesState>;
    activate(handle: string): Promise<TfsProcessesState>;
  };

  display: {
    getState(): Promise<TfsDisplayState>;
    switchInternal(): Promise<{ mode: string; message: string }>;
    switchExternal(): Promise<{ mode: string; message: string }>;
    setResolution(presetId: string): Promise<TfsDisplayState>;
    setRefreshRate(refreshRate: number): Promise<TfsDisplayState>;
  };

  themes: {
    getState(): Promise<unknown>;
    refreshCatalog(): Promise<unknown>;
    getStoreCatalog(options?: { search?: string; filter?: string; order?: string; page?: number; perPage?: number }): Promise<unknown>;
    getStoreTheme(storeThemeId: string): Promise<unknown>;
    installStoreTheme(storeThemeId: string): Promise<unknown>;
    setEnabled(themeId: string, enabled: boolean): Promise<unknown>;
    toggleOption(themeId: string, optionId: string): Promise<unknown>;
    setChoice(themeId: string, optionId: string, choiceId: string): Promise<unknown>;
    adjustRange(themeId: string, optionId: string, delta: number): Promise<unknown>;
    resetRange(themeId: string, optionId: string): Promise<unknown>;
    createProfile(title: string): Promise<unknown>;
    applyProfile(profileId: string): Promise<unknown>;
    updateProfile(profileId: string): Promise<unknown>;
    removeProfile(profileId: string): Promise<unknown>;
    setWatchEnabled(enabled: boolean): Promise<unknown>;
  };

  artwork: {
    getState(): Promise<unknown>;
    searchGames(term: string): Promise<Array<{ id: number; name: string; verified: boolean }>>;
    searchAssets(gameId: number, assetType: string, options?: { page?: number }): Promise<Array<Record<string, unknown>>>;
    apply(appId: number, assetType: string, assetUrl: string): Promise<Record<string, unknown>>;
    toggleSetting(key: string): Promise<unknown>;
    setResultLimit(value: number): Promise<unknown>;
  };

  appStart: {
    getState(): Promise<{ shortcuts: TfsAppStartShortcut[]; statusText: string; lastIndexedAtUtc: string | null }>;
    getCatalog(): Promise<{ apps: TfsAppStartCatalogEntry[]; statusText: string }>;
    refreshCatalog(): Promise<{ apps: TfsAppStartCatalogEntry[]; statusText: string }>;
    add(appId: string): Promise<unknown>;
    remove(shortcutId: string): Promise<unknown>;
    toggleFavorite(shortcutId: string): Promise<unknown>;
    launch(shortcutId: string): Promise<unknown>;
  };

  storeSync: {
    getState(): Promise<unknown>;
    getTitles(storeId?: string): Promise<unknown[]>;
    getArtworkPreview(titleId: string): Promise<unknown>;
    toggleSetting(key: string): Promise<unknown>;
    setStoreEnabled(storeId: string, enabled: boolean): Promise<unknown>;
    setStorePath(storeId: string, path: string): Promise<unknown>;
    clearStorePath(storeId: string): Promise<unknown>;
    setAdditionalPaths(storeId: string, paths: string[]): Promise<unknown>;
    setTitleOverride(titleId: string, options?: {
      titleOverride?: string;
      artworkTitleOverride?: string;
      excluded?: boolean;
    }): Promise<unknown>;
    clearTitleOverride(titleId: string): Promise<unknown>;
    sync(): Promise<unknown>;
    refreshStorefront(storeId?: string): Promise<unknown>;
    setStorefrontEnabled(storeId: string, enabled: boolean): Promise<unknown>;
    startStorefrontLogin(storeId: string): Promise<unknown>;
    completeStorefrontAuth(storeId: string, value: string): Promise<unknown>;
    launchStorefrontGame(storeId: string, gameId: string): Promise<{ success: boolean; message: string }>;
  };
  automation: {
    getState(): Promise<unknown>;
    toggleSetting(key: string): Promise<unknown>;
    setExecutablePath(path: string): Promise<unknown>;
    resetExecutablePath(): Promise<unknown>;
    toggleWatchedTitle(titleId: string): Promise<unknown>;
  };
  performance: {
    getState(): Promise<TfsPerformanceState>;
    setOverlayLevel(level: number): Promise<TfsPerformanceState>;
    toggleAutoTarget(): Promise<TfsPerformanceState>;
    setSettingValue(key: string, value: number): Promise<TfsPerformanceState>;
    startOverlay(): Promise<TfsPerformanceState>;
    stopOverlay(): Promise<TfsPerformanceState>;
    prepareElevatedHelper(): Promise<TfsPerformanceState>;
  };
  power: {
    getState(): Promise<TfsPowerState>;
    startWindowsDesktop(options: TfsPowerConfirmation): Promise<{ message: string }>;
    restartSteam(options: TfsPowerConfirmation): Promise<{ message: string }>;
    sleepWindows(options: TfsPowerConfirmation): Promise<{ message: string }>;
    restartWindows(options: TfsPowerConfirmation): Promise<{ message: string }>;
    shutdownWindows(options: TfsPowerConfirmation): Promise<{ message: string }>;
  };
  system: {
    getInfo(): Promise<Record<string, unknown>>;
    run(fileName: string, args?: string[], options?: TfsProcessOptions): Promise<TfsProcessRunResult>;
    start(fileName: string, args?: string[], options?: TfsProcessOptions): Promise<TfsManagedProcess>;
    list(): Promise<{ processes: TfsManagedProcess[] }>;
    status(processId: string): Promise<TfsManagedProcess>;
    stop(processId: string): Promise<TfsManagedProcess>;
    stopAll(): Promise<{ count: number; stoppedProcessIds: string[] }>;
    open(target: string, args?: string[], options?: { runAsAdministrator?: boolean; workingDirectory?: string }): Promise<{ opened: boolean; processId: number }>;
  };
  filesystem: {
    paths(): Promise<Record<string, string>>;
    stat(path: string, options?: TfsFileSystemOptions): Promise<TfsSystemFileEntry>;
    list(path?: string, options?: TfsFileSystemOptions & { recursive?: boolean }): Promise<{ path: string; entries: TfsSystemFileEntry[] }>;
    readText(path: string, options?: TfsFileSystemOptions): Promise<string>;
    readBytes(path: string, options?: TfsFileSystemOptions): Promise<Uint8Array>;
    writeText(path: string, content: string, options?: TfsFileSystemOptions & { append?: boolean }): Promise<TfsSystemFileEntry>;
    appendText(path: string, content: string, options?: TfsFileSystemOptions): Promise<TfsSystemFileEntry>;
    writeBytes(path: string, content: ArrayBuffer | ArrayBufferView, options?: TfsFileSystemOptions & { append?: boolean }): Promise<TfsSystemFileEntry>;
    mkdir(path: string, options?: TfsFileSystemOptions): Promise<TfsSystemFileEntry>;
    remove(path: string, options?: TfsFileSystemOptions & { recursive?: boolean }): Promise<{ path: string; exists: boolean }>;
    copy(sourcePath: string, destinationPath: string, options?: TfsFileSystemOptions & { overwrite?: boolean }): Promise<TfsSystemFileEntry>;
    move(sourcePath: string, destinationPath: string, options?: TfsFileSystemOptions & { overwrite?: boolean }): Promise<TfsSystemFileEntry>;
  };
  backend: {
    start(options?: Partial<TfsPluginBackendManifest>): Promise<TfsManagedProcess>;
    ready(): Promise<TfsManagedProcess>;
    status(): Promise<TfsManagedProcess>;
    call<TResult = unknown>(method: string, arguments?: unknown, options?: { timeoutMs?: number }): Promise<TResult>;
    stop(): Promise<TfsManagedProcess>;
  };
  steam: {
    targets(): Promise<TfsSteamTarget[]>;
    evaluate(targetId: string, expression: string): Promise<TfsSteamEvaluationResult>;
    inject(targetId: string, expression: string): Promise<TfsSteamEvaluationResult>;
    readonly client: unknown;
    readonly ui: { React: unknown; ReactDOM: unknown; router: unknown };
  };
  ui: TfsPluginUi;
}

export interface TfsProcessOptions {
  workingDirectory?: string;
  environment?: Record<string, string>;
  secretEnvironment?: Record<string, string>;
  stdin?: string;
  timeoutMs?: number;
  packageRelative?: boolean;
  createNoWindow?: boolean;
}

export interface TfsProcessRunResult {
  processId: string;
  exitCode: number;
  success: boolean;
  output: string;
  error: string;
  timedOut: boolean;
}

export interface TfsManagedProcess {
  processId: string;
  osProcessId?: number;
  running: boolean;
  exitCode?: number;
  output: string;
  error: string;
  backend: boolean;
  fileName: string;
}

export type TfsFileSystemScope = "data" | "plugin" | "app" | "temp" | "absolute";
export interface TfsFileSystemOptions { scope?: TfsFileSystemScope }
export interface TfsSystemFileEntry {
  path: string;
  name?: string;
  exists: boolean;
  isDirectory?: boolean;
  size?: number;
  modifiedUtc?: string;
}
export interface TfsSteamTarget { id: string; title: string; type: string; url: string }
export interface TfsSteamEvaluationResult {
  success: boolean;
  value: unknown;
  errorMessage?: string;
  id: string;
  title: string;
  url: string;
}

export interface TfsPluginSdkGlobal {
  readonly version: number;
  readonly sdkVersion: "1.0.0";
  create(manifest: TfsPluginManifest, options?: { pluginId?: string; apiBase?: string }): TfsPluginSdk;
  register(
    manifest: TfsPluginManifest,
    setup: TfsPluginDefinition | ((sdk: TfsPluginSdk) => TfsPluginDefinition),
    options?: { pluginId?: string; apiBase?: string },
  ): TfsPluginDefinition;
  unregister(pluginId: string): void;
}

declare global {
  interface Window {
    TfsPluginSdk: TfsPluginSdkGlobal;
    ToolsForSteamCommunityPlugins?: Record<string, TfsPluginDefinition>;
  }
}
