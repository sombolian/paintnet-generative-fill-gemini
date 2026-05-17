# Generative Fill for Paint.NET (Gemini)

AI generative fill for Paint.NET, powered by Google Gemini Nano Banana.

Select an area, type a prompt, get the result on a new layer above your active
one.

## Features

- **One-field first run.** First launch asks only for your Gemini API key,
  which is saved to `%APPDATA%\GeminiFillPlugin\config.json`. After that the
  dialog has just a prompt and a model picker.
- **Result on its own layer.** Each generation appears on a new layer named
  "Generative Fill", inserted above the layer you ran the effect on.
- **Animated loading on the canvas.** A multi-color shimmer placeholder layer
  appears the moment you click OK and animates while Gemini works. You can
  keep working in Paint.NET during generation, switching tools or editing
  other layers.
- **Move and resize the placeholder while it loads.** If you drag, scale, or
  partially erase the placeholder with Move Selected Pixels or the eraser,
  the AI result lands in whatever shape and position the layer is in when
  the response arrives.
- **Cropped, focused requests.** Only the selection bounding box plus a small
  context border is sent to Gemini, so the model edits just the area you
  selected instead of rewriting the whole image.

## Requirements

- Paint.NET 5.1 or later (ships with .NET 9 bundled, which this plugin targets)
- Windows 10 or 11
- A free Google Gemini API key from https://aistudio.google.com/apikey

## Install

1. Download `GeminiFillPlugin.dll` from the
   [latest release](https://github.com/sombolian/paintnet-generative-fill-gemini/releases).
2. Copy it into your Paint.NET Effects folder:
   - Standard install: `C:\Program Files\paint.net\Effects\`
   - Microsoft Store install: `Documents\paint.net App Files\Effects\`
   - Portable install: the `Effects` subfolder next to `paintdotnet.exe`
3. Restart Paint.NET.

The new menu entry appears at **Effects, AI, Generative Fill (Gemini)**.

## First run

1. Make any selection (or `Ctrl+A` for the whole image).
2. Run **Effects, AI, Generative Fill (Gemini)**.
3. A dialog with one field appears, the Gemini API key. Paste it and click OK.
4. The first run closes without generating anything. Run the effect again to
   see the normal prompt + model dialog.

## Usage

1. Select the area you want to change with any selection tool.
2. **Effects, AI, Generative Fill (Gemini)**.
3. Type your prompt. Examples:
   - "add a wizard hat with stars"
   - "remove the person on the left"
   - "change the sky to a sunset"
4. Pick a model. Default is Nano Banana 2 (`gemini-3.1-flash-image-preview`),
   the fastest and currently the top-ranked image model on the Artificial
   Analysis arena. Other options:
   - `gemini-3-pro-image-preview` (Nano Banana Pro): higher quality, slower,
     more expensive. Best for 2K or 4K detail or accurate text inside the
     fill.
   - `gemini-2.5-flash-image`: the original Nano Banana, fallback only.
5. Click OK. The dialog closes immediately and a "Generative Fill (loading)"
   layer appears above your active layer with an animated shimmer.
6. When Gemini returns (typically 5 to 20 seconds), the shimmer is replaced
   in place with the AI fill and the layer is renamed to "Generative Fill".

## How it works (technical)

The plugin is a Paint.NET v5 `PropertyBasedBitmapEffect`. The interesting
pieces:

- **Cropped inpainting.** The selection bounding box plus 25% padding (min
  64px) is cropped from the source, encoded as PNG along with a black/white
  mask of the same crop, and sent to Gemini's `generateContent` endpoint
  with a strict inpainting prompt. Only the inner region of the response
  (matching the original selection) is stamped back into the layer. This
  works around the fact that Gemini Nano Banana models do not have a true
  binary-mask inpaint API path the way Imagen 3 Capability does, and tend
  to rewrite the whole image when sent the full document.
- **Layer creation via reflection.** Paint.NET's public plugin API does not
  let effects create layers, so the plugin walks `Application.OpenForms` to
  find `PaintDotNet.Dialogs.MainForm`, then reflects through
  `MainForm.appWorkspace`, `ActiveDocumentWorkspace`, `Document`,
  `Layers.Insert(activeIndex+1, new BitmapLayer(surface))`. The asynchronous
  generation pipeline runs from `OnDispose`.
- **Animated skeleton loader.** Multi-stop diagonal gradient base baked
  into a 512-entry palette, plus an iridescent bright band sweeping
  diagonally with a smoothstep falloff. Rendered in parallel across rows
  via `Parallel.For`, writes directly into the layer's `Surface.Scan0`
  pointer. Ticks at ~30 fps with frame coalescing so the queue cannot
  snowball into lag.
- **Move and resize tracking.** Every animation tick re-reads the layer's
  current alpha channel into a buffer, computes the bbox of non-zero
  pixels, and renders the shimmer into that bbox. The AI result follows
  the same path, so the fill lands in whatever shape and position the
  layer is in when the response arrives.

## Known limitations

- **No undo for layer insertion.** The new layer is added through reflection,
  which bypasses Paint.NET's history system. To remove it, delete the layer
  manually.
- **Reflection brittleness.** The runtime layer-insertion code depends on
  internal Paint.NET symbol names (`appWorkspace`, `ActiveDocumentWorkspace`,
  `Document.Layers.Insert`, `Surface.CopyFromGdipBitmap`). These have been
  stable since Paint.NET 5.0 but a future rename in Paint.NET would break
  the plugin until updated.
- **No in-dialog preview.** You see the result only as the new layer
  materializes. If the result is not what you wanted, delete the layer and
  run again with a different prompt.
- **Costs Gemini API credits.** Each generation is one billed API call
  against your Google AI Studio quota.

## Building from source

Requirements:
- .NET 9 SDK
- Paint.NET 5.1+ installed at `C:\Program Files\paint.net\` (the project
  references its DLLs at build time)

```
git clone https://github.com/sombolian/paintnet-generative-fill-gemini.git
cd paintnet-generative-fill-gemini
dotnet build -c Release
```

The output DLL appears at
`bin\Release\net9.0-windows\GeminiFillPlugin.dll`. Copy it into your
Paint.NET Effects folder.

If your Paint.NET is installed somewhere other than
`C:\Program Files\paint.net\`, edit the `<PdnRoot>` property in
`GeminiFillPlugin.csproj`.

## Project structure

| File | Purpose |
|---|---|
| `GenerativeFillEffect.cs` | Paint.NET effect, async pipeline, animation renderer, reflection helpers |
| `GeminiClient.cs` | HTTP client for Google Gemini `generateContent` |
| `ConfigStore.cs` | Loads and saves API key plus model preference to `%APPDATA%` |
| `PluginSupportInfo.cs` | Paint.NET plugin metadata |
| `GeminiFillPlugin.csproj` | Build config, references Paint.NET DLLs |

## Credits

Built against Gemini Nano Banana 2 (`gemini-3.1-flash-image-preview`) and the
official Paint.NET v5 sample effects repository at
https://github.com/paintdotnet/PdnV5EffectSamples.

The layer-insertion technique relies on Rick Brewster's `BitmapLayer`,
`Surface`, and `LayerList` types from Paint.NET's internal `PaintDotNet.Data`
and `PaintDotNet.Core` assemblies.

## License

MIT, see [LICENSE](LICENSE).
