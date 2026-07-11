# TFS Full-Trust Plugin Template

This example demonstrates the Decky-style escape hatch in SDK 1.0.0:

- an automatically managed PowerShell backend;
- bundled native executable/script support;
- captured foreground commands;
- long-running process lifecycle and diagnostics;
- arbitrary filesystem paths;
- direct Steam DevTools target access through `sdk.steam`.

`native.full-trust` is deliberately powerful. It is a Store disclosure rather than a hostile-code sandbox. Only install such plugins from publishers you trust.

Replace `backend/plugin.ps1` with a bundled self-contained `.exe` for C++, Rust, Go, .NET, or any other language. Set `runtime` to `executable`. Python and Node entry points are also supported when a matching runtime is available through `runtimeExecutable` or the system `PATH`.
