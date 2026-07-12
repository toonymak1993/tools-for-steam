(() => {
  const manifest = {
    id: "full-trust-example",
    name: "Full Trust Example",
    version: "1.0.0",
    sdkVersion: "1.0.0",
    permissions: ["frontend", "native.full-trust"],
    backend: {
      entryPoint: "backend/plugin.ps1",
      runtime: "powershell",
      autoStart: true,
      createNoWindow: true,
    },
  };

  window.TfsPluginSdk.register(manifest, (sdk) => {
    let status = "Native backend is starting.";
    let error = "";

    return {
      createScreen(context) {
        return sdk.ui.createScreenModel({
          title: manifest.name,
          subtitle: "Full-Trust SDK",
          note: status,
          error,
          slots: [
            sdk.ui.createCommandSlot("Call backend RPC", "Send JSON to the managed native backend and receive JSON back.", async () => {
              const result = await sdk.backend.call("ping", { sentAt: new Date().toISOString() });
              status = `${result.message} from ${result.pluginId}`;
              context.refresh();
            }),
            sdk.ui.createCommandSlot("Backend status", "Read process state and captured output.", async () => {
              try {
                const result = await sdk.backend.status();
                status = result.running ? `Backend running (PID ${result.osProcessId}).` : `Backend exited (${result.exitCode}).`;
                error = result.error || "";
              } catch (caught) {
                error = caught instanceof Error ? caught.message : String(caught);
              }
              context.refresh();
            }),
            sdk.ui.createCommandSlot("Run native command", "Execute a Windows command and capture its output.", async () => {
              const result = await sdk.system.run("cmd.exe", ["/d", "/c", "ver"]);
              status = result.output.trim() || `Exit code ${result.exitCode}`;
              context.refresh();
            }),
            sdk.ui.createCommandSlot("Write user file", "Demonstrate full filesystem access in Documents.", async () => {
              const paths = await sdk.filesystem.paths();
              const target = `${paths.documents}\\TFS-full-trust-example.txt`;
              await sdk.filesystem.writeText(target, "Created by a TFS full-trust plugin.\r\n", { scope: "absolute" });
              status = `Wrote ${target}`;
              context.refresh();
            }),
          ],
        });
      },
    };
  });
})();
