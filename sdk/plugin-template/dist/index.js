(() => {
  const manifest = {
    id: "example-plugin",
    name: "Example Plugin",
    version: "1.0.0",
    sdkVersion: "1.0.0",
    permissions: ["frontend"],
  };

  if (typeof window.TfsPluginSdk?.register !== "function") {
    console.warn("TFS plugin SDK is not available yet.");
    return;
  }

  window.TfsPluginSdk.register(manifest, (sdk) => {
    let helloCount = 0;

    return {
      createScreen(context = {}) {
        return sdk.ui.createScreenModel({
          title: manifest.name,
          subtitle: "Community Plugin",
          note: helloCount === 0
            ? "This screen is rendered through the shared Tools for Steam frontend library."
            : `Hello was triggered ${helloCount} time${helloCount === 1 ? "" : "s"}.`,
          slots: [
            sdk.ui.createCommandSlot(
              "Hello from the SDK",
              "This row uses the same controller-friendly UI model as built-in plugins.",
              () => {
                helloCount += 1;
                context.refresh?.();
              },
              { badge: "SDK v1" },
            ),
          ],
        });
      },
    };
  });
})();
