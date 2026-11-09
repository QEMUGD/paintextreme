# Paint AutoDrawer 256

A Windows .NET 8 WinForms utility that converts an image to at most 256 colours and draws it into Microsoft Paint using mouse and keyboard input.

## Build
1. Install the .NET 8 SDK on Windows.
2. Open PowerShell in this repository.
3. Run:
   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass -Force
   .\build.ps1
   ```
4. The single-file executable is written to `bin\Release\net8.0-windows\win-x64\publish\PaintAutoDrawer256.exe`.

## Use
1. Open Paint and select the Pencil tool with size 1.
2. Load an image in AutoDrawer.
3. Calibrate the RGB dialog button, Red, Green, Blue and OK positions. For each calibration item, click Capture, move the pointer over the target in Paint, then press F8.
4. Calibrate the top-left and bottom-right corners of the Paint drawing area the same way.
5. Press F6 to start. Space pauses/resumes; Escape cancels.

The app uses an image-derived median-cut palette (maximum 256 colours), Floyd–Steinberg error diffusion, and horizontal runs. Settings are saved in `%AppData%\PaintAutoDrawer256\config.json`.

## Notes
- Keep Paint at 100% zoom and do not move/resize its window after canvas calibration.
- Use a blank canvas whose visible drawing area matches the selected calibration rectangle.
- Windows/Paint input handling limits speed. A photo can take a long time because each distinct palette colour requires an RGB-dialog change.
- The app automates Paint; it does not promise pixel-perfect results on every Paint version or display configuration.
