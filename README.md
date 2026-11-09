# PaintExtreme — 256 Colour Studio

A standalone Windows image editor built with .NET 8 WinForms. It opens images, reduces them to an image-derived palette of up to 256 colours, previews the result on its own canvas, and exports a PNG.

It does **not** control Microsoft Paint, simulate mouse strokes, or use RGB dialogs.

## Features

- Open PNG, JPG, BMP, GIF and TIFF images.
- Convert images to a palette of 2–256 colours using median-cut quantization.
- Optional Floyd–Steinberg dithering.
- Preview the converted image directly in the app.
- Export the result as a PNG.
- Automatically saves palette size, dithering preference, window size and last folder to `%AppData%\PaintAutoDrawer256\config.json`.
- Processes pixels in memory-efficient scanline buffers rather than issuing thousands of UI actions.

## Build on Windows

1. Install the .NET 8 SDK.
2. Open PowerShell in this repository.
3. Run:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass -Force
   .\build.ps1
   ```

4. The self-contained executable is written to `bin\Release\net8.0-windows\win-x64\publish\PaintAutoDrawer256.exe`.

## Use

1. Click **Open image**.
2. Choose a palette size up to 256.
3. Enable or disable Floyd–Steinberg dithering.
4. Click **Convert to palette** to preview the result.
5. Click **Export PNG** to save it.

Settings are saved automatically when changed and when the app closes.

## Notes

- The app edits image pixels directly; there are no screen-coordinate calibrations.
- Very large source images require more memory and processing time.
- The exported PNG contains the converted image at its original pixel dimensions.
