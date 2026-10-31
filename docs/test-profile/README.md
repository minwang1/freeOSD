# ICC profile import sample

`test-srgb.icm` is the standard Windows sRGB profile copied from the Windows color profile store. Use it to exercise the application's file picker, profile installation/association, current-profile selection, and restore workflow.

This profile is a color-space description and does not contain a VCGT/MS00 calibration curve or an MHC2 transform. It is not expected to produce an obvious desktop color change. It validates profile plumbing only. For a visible calibration test, use an ICC/ICM profile created by Windows Display Color Calibration or a display calibration tool that includes calibration data.

Windows installs profiles system-wide, which requires administrator permission. Run Monitor Control elevated to import a new file. Selecting an already installed profile does not require reinstalling it.
