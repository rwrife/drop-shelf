# Drag/drop compatibility matrix

This matrix is checked in as the manual verification ledger for issue #3. This implementation run and its automated tests ran on Linux only. No Windows or macOS desktop interaction was performed, so every manual cell is explicitly **Untested**. “Untested” is not a claim of compatibility.

| Platform | Direction | Explorer / Finder | Browser | Text editor |
|---|---|---|---|---|
| Windows 10/11 | Inbound to Drop Shelf | Untested | Untested | Untested |
| Windows 10/11 | Outbound from Drop Shelf | Untested | Untested | Untested |
| macOS (supported releases) | Inbound to Drop Shelf | Untested | Untested | Untested |
| macOS (supported releases) | Outbound from Drop Shelf | Untested | Untested | Untested |

Automated synthetic fixtures cover canonical conversion and the Windows/macOS format adapter contracts without pointer automation. They do not exercise COM `IDataObject`, `NSPasteboard`, Finder, Explorer, browsers, editors, shell drag loops, sandbox permissions, or OS-specific destination behavior.
