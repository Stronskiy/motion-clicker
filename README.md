# Motion Clicker

A native macOS and Windows automation editor: record actions with screen video, correct clicks on a timeline, and combine scenarios.

**Status: early development.** There are no application downloads yet. Features below describe the planned preview, not verified capabilities.

## Planned preview

- Quick Click with bounded repeats, configurable timing and emergency stop.
- Explicit monitor selection and mapping, including mixed display scales.
- Mouse action recording alongside screen video.
- Linked video/action timelines, coordinate correction, split/trim and undo.
- Import two scenarios, align their timelines, preview safely, then execute.

Multiple monitor tracks share one desktop input stream. Same-time actions will be dispatched in a deterministic sequence; independent physically simultaneous cursors are not promised. Editing actions changes the scenario and its overlay, while the original screen recording remains historical footage.

## Platform direction

- **macOS:** SwiftUI and native capture/input APIs, initially macOS 26+ on Apple Silicon.
- **Windows:** WinUI 3 and native capture/input APIs, initially Windows 11 x64.
- A shared versioned project format and synthetic conformance fixtures keep timeline behavior consistent across clients.

Development follows focused branches, review and platform checks. See [AGENTS.md](AGENTS.md) for the working agreement. Private planning, personal recordings and build output stay outside the source repository. Releases will include the actual verification status, packaging instructions and SHA-256 checksums.

## License

[Apache-2.0](LICENSE). Motion Clicker is an independent project, not affiliated with Apple, Microsoft or CapCut. Third-party assets and trademarks remain subject to their own terms.
