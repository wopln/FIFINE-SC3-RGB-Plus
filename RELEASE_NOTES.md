# FIFINE SC3 RGB+ v2.5.0

Stable update adding Custom Button Shortcuts and RGB+ Firmware 1.5 support.

- Assign Custom A, B, C, and D to Windows `.exe` or `.lnk` applications
- One application launch per physical button press, with hold/repeat de-duplication
- Shortcut Mode suppresses the original SC3 Custom-button action and sound while enabled
- New **Settings → Custom Buttons** page with an explicit Stock/Shortcut toggle
- Background shortcut engine continues working when the main window is hidden
- Windows system tray actions for Open, Disable Custom Button Shortcuts, and Exit
- RGB+ Firmware 1.5 support with automatic firmware-version detection
- New **Settings → Updates → Mixer Firmware** section with explicit user-confirmed firmware updates
- Direct RGB+ Firmware 1.4 → 1.5 migration
- Direct supported Stock V22 → RGB+ Firmware 1.5 installation
- Already-current Firmware 1.5 devices are detected and are not reflashed

Mixer firmware updates never run silently. Custom Button Shortcuts remain unavailable until a verified RGB+ Firmware 1.5 device with CBTN v2 is connected.

Production Mod 1.5 package SHA-256:
`589B2FCB590B999C905693DF6ABA6A343343AC6A8241B4AA9802853A72FA525B`

Production Mod 1.5 package CRC16:
`C12C`