# Tools for Steam v0.4.0

Tools for Steam v0.4.0 introduces the new Community Plugin Store, TFS Xbox Mode, RTSS integration, improved overlays, new core and community plugins, and support for the MSI Claw A8.

## Highlights

- Added the Community Plugin Store
- Added the TFS plugin SDK and example app
- Replaced the custom FPS counter with RivaTuner Statistics Server
- Added frame-limiting support
- Added TFS Xbox Mode
- Added the Discord core plugin
- Added MSI Claw A8 support
- Improved overlays, App Start, and Audio

## Added

### Community Plugin Store

- Added a store for discovering and downloading TFS plugins
- Added plugin installation and management
- Added the TFS SDK to the repository
- Added an example plugin application for developers

### TFS Xbox Mode

- Added an Xbox-style launch mode for Steam
- Uses an approach similar to OmniConsole
- Shell and eTray modes remain available

> [!NOTE]
> TFS Xbox Mode is not currently officially signed by Microsoft.

### Core Plugins

- Added Discord integration

### Community Plugins

- Added Home Assistant
- Added Crackwatch

### Handheld Support

Added support for the MSI Claw A8:

- TDP controls
- Per-game TDP profiles
- Special hardware button support
- Automatic OEM software disabling
- Controller takeover through VIIPER

## Changed

### Performance Monitoring

- Removed the built-in FPS counter
- Integrated RivaTuner Statistics Server (RTSS)
- RTSS is installed automatically when required
- Added frame-limiting capabilities
- Added additional performance settings

### Overlays and Controls

- Improved in-game overlay behavior
- Added support for opening the overlay and Quick Access Menu by holding a configured button combination
- Pressing Select in Steam Big Picture Mode now opens the main menu
- Holding Select in Steam Big Picture Mode now opens the Quick Access Menu
- Added additional overlay control settings
- Added new splash-screen settings

### App Start

- Simplified the application launch configuration
- Improved application detection

### Audio

- Completely redesigned the Audio plugin
- Simplified the user interface and controls
- Improved the overall appearance and usability

## Acknowledgements

Thank you to everyone who tested this release, reported issues, contributed feedback, or started developing plugins for the TFS ecosystem.
