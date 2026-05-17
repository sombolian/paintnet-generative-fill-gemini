using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNet.Imaging;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using SDImg = System.Drawing.Imaging;

namespace GeminiFillPlugin;

internal sealed class GenerativeFillEffect : PropertyBasedBitmapEffect
{
    public static readonly string StaticName = "Generative Fill (Gemini)";
    public static readonly string StaticSubMenuName = "AI";

    private string prompt = "";
    private string model = "gemini-3.1-flash-image-preview";
    private string apiKey = "";
    private bool firstRunDialog;
    private readonly object workLock = new();

    public GenerativeFillEffect()
        : base(
            StaticName,
            StaticSubMenuName,
            BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
        Log("ctor");
    }

    private enum PropertyNames { Prompt, Model, ApiKey }

    protected override PropertyCollection OnCreatePropertyCollection()
    {
        var cfg = ConfigStore.Load();
        var props = new List<Property>();

        if (string.IsNullOrWhiteSpace(cfg.ApiKey))
        {
            firstRunDialog = true;
            props.Add(new StringProperty(PropertyNames.ApiKey, "", 200));
        }
        else
        {
            firstRunDialog = false;
            apiKey = cfg.ApiKey;
            model = cfg.Model ?? "gemini-3.1-flash-image-preview";
            props.Add(new StringProperty(PropertyNames.Prompt, "", 2000));
            props.Add(new StaticListChoiceProperty(
                PropertyNames.Model,
                new object[]
                {
                    "gemini-3.1-flash-image-preview",
                    "gemini-3-pro-image-preview",
                    "gemini-2.5-flash-image"
                },
                IndexOfModel(model)));
        }
        return new PropertyCollection(props);
    }

    private static int IndexOfModel(string m) => m switch
    {
        "gemini-3-pro-image-preview" => 1,
        "gemini-2.5-flash-image" => 2,
        _ => 0
    };

    protected override ControlInfo OnCreateConfigUI(PropertyCollection props)
    {
        var info = CreateDefaultConfigUI(props);
        if (firstRunDialog)
        {
            info.SetPropertyControlValue(PropertyNames.ApiKey, ControlInfoPropertyNames.DisplayName, "Gemini API key");
            info.SetPropertyControlValue(PropertyNames.ApiKey, ControlInfoPropertyNames.Description,
                "Paste your key from https://aistudio.google.com/apikey. Next time you'll just see the prompt.");
        }
        else
        {
            info.SetPropertyControlValue(PropertyNames.Prompt, ControlInfoPropertyNames.DisplayName, "Prompt");
            info.SetPropertyControlValue(PropertyNames.Prompt, ControlInfoPropertyNames.Multiline, true);
            info.SetPropertyControlValue(PropertyNames.Model, ControlInfoPropertyNames.DisplayName, "Model");
            info.SetPropertyControlType(PropertyNames.Model, PropertyControlType.DropDown);
        }
        return info;
    }

    protected override void OnSetToken(PropertyBasedEffectConfigToken? newToken)
    {
        if (newToken != null)
        {
            if (firstRunDialog)
            {
                string newKey = newToken.GetProperty<StringProperty>(PropertyNames.ApiKey)?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(newKey))
                {
                    var cfg = ConfigStore.Load();
                    cfg.ApiKey = newKey.Trim();
                    ConfigStore.Save(cfg);
                    apiKey = cfg.ApiKey;
                }
            }
            else
            {
                string newPrompt = newToken.GetProperty<StringProperty>(PropertyNames.Prompt)?.Value ?? "";
                string newModel = newToken.GetProperty<StaticListChoiceProperty>(PropertyNames.Model)?.Value?.ToString() ?? "gemini-3.1-flash-image-preview";
                lock (workLock) { prompt = newPrompt; model = newModel; }
                if (newModel != ConfigStore.Load().Model)
                {
                    var cfg = ConfigStore.Load();
                    cfg.Model = newModel;
                    ConfigStore.Save(cfg);
                }
            }
        }
        base.OnSetToken(newToken);
    }

    protected override void OnInitializeRenderInfo(IBitmapEffectRenderInfo renderInfo)
    {
        renderInfo.OutputPixelFormat = PixelFormats.Bgra32;
        base.OnInitializeRenderInfo(renderInfo);
    }

    private IEffectSelectionInfo GetSelectionInfo()
        => ((IEffectEnvironment)(object)this.Environment).Selection;

    // Pass-through render: active layer is never modified by this effect.
    protected override unsafe void OnRender(IBitmapEffectOutput output)
    {
        IEffectInputBitmap<ColorBgra32> source = this.Environment.GetSourceBitmapBgra32();
        using IBitmapLock<ColorBgra32> outLock = output.Lock<ColorBgra32>();
        using IBitmapLock<ColorBgra32> srcLock = source.Lock(output.Bounds);
        RegionPtr<ColorBgra32> outRgn = outLock.AsRegionPtr();
        RegionPtr<ColorBgra32> srcRgn = srcLock.AsRegionPtr();
        for (int y = 0; y < outRgn.Height; y++)
            for (int x = 0; x < outRgn.Width; x++)
                outRgn[x, y] = srcRgn[x, y];
    }

    // OnDispose: fire-and-forget. Capture inputs synchronously, then a background
    // thread inserts the placeholder layer + drives the animation + calls Gemini.
    protected override void OnDispose(bool disposing)
    {
        if (disposing && !firstRunDialog)
        {
            try { TryStartAsyncPipeline(); }
            catch (Exception ex) { Log("OnDispose error: " + ex); }
        }
        base.OnDispose(disposing);
    }

    private void TryStartAsyncPipeline()
    {
        string pPrompt, pModel, pApiKey;
        lock (workLock) { pPrompt = prompt; pModel = model; pApiKey = apiKey; }
        if (string.IsNullOrWhiteSpace(pPrompt) || string.IsNullOrWhiteSpace(pApiKey)) return;

        // Capture everything we need from this.Environment BEFORE going async,
        // since after OnDispose returns the environment is invalidated.
        IEffectInputBitmap<ColorBgra32> source = this.Environment.GetSourceBitmapBgra32();
        SizeInt32 docSize = source.Size;
        IEffectSelectionInfo selInfo = GetSelectionInfo();
        RectInt32 selBounds = selInfo.RenderBounds;
        byte[] maskBytes = ExtractMaskAlpha(selInfo.MaskBitmap, docSize);

        // Crop the request to selection + 25% padding (min 64px) for context.
        // This focuses Nano Banana on the area to change so it doesn't rewrite the
        // whole image, while still giving it enough surrounding pixels to blend.
        RectInt32 padded = ComputePaddedBbox(selBounds, docSize, paddingFraction: 0.25f, minPaddingPx: 64);
        byte[] croppedSourcePng = EncodeBgra32SourceCropToPng(source, docSize, padded);
        byte[] croppedMaskPng = EncodeMaskBytesCropToBwPng(maskBytes, docSize, padded);

        Form? mainForm = FindMainForm();
        if (mainForm == null) { Log("Main form not found, aborting"); return; }

        Log($"Request crop: padded={padded.X},{padded.Y} {padded.Width}x{padded.Height}  inner sel={selBounds.X},{selBounds.Y} {selBounds.Width}x{selBounds.Height}");

        new Thread(() => RunPipeline(mainForm, pApiKey, pModel, pPrompt, croppedSourcePng, croppedMaskPng, maskBytes, docSize, selBounds, padded))
        { IsBackground = true }.Start();
    }

    private static RectInt32 ComputePaddedBbox(RectInt32 sel, SizeInt32 doc, float paddingFraction, int minPaddingPx)
    {
        int padX = Math.Max(minPaddingPx, (int)(sel.Width * paddingFraction));
        int padY = Math.Max(minPaddingPx, (int)(sel.Height * paddingFraction));
        int x = Math.Max(0, sel.X - padX);
        int y = Math.Max(0, sel.Y - padY);
        int w = Math.Min(doc.Width - x,  sel.Width  + (sel.X - x) + padX);
        int h = Math.Min(doc.Height - y, sel.Height + (sel.Y - y) + padY);
        return new RectInt32(x, y, w, h);
    }

    // ---------------- The full async pipeline ----------------

    private void RunPipeline(
        Form mainForm,
        string pApiKey, string pModel, string pPrompt,
        byte[] sourcePng, byte[] maskPng, byte[] maskBytes,
        SizeInt32 docSize, RectInt32 selBounds, RectInt32 paddedBbox)
    {
        Log($"Pipeline start: doc={docSize.Width}x{docSize.Height} selBounds={selBounds.X},{selBounds.Y},{selBounds.Width},{selBounds.Height} padded={paddedBbox.Width}x{paddedBbox.Height}");

        // 1) Insert placeholder layer (frame 0 of animation) on UI thread.
        object? layer = null;
        object? document = null;
        try
        {
            mainForm.Invoke(() =>
            {
                var inserted = InsertEmptyLayer(mainForm, docSize, "Generative Fill (loading…)");
                layer = inserted.layer;
                document = inserted.document;
                // Paint first frame immediately so the user sees something.
                RenderFrameIntoLayer(layer, docSize, selBounds, maskBytes, 0);
                InvalidateLayer(layer, selBounds, document);
            });
        }
        catch (Exception ex) { Log("Insert placeholder failed: " + ex); ShowError("Couldn't add layer: " + ex.Message); return; }

        if (layer == null || document == null) { Log("Layer null after insert"); return; }

        // 2) Start animation timer (UI-thread ticks via mainForm.BeginInvoke).
        // Each tick re-reads the layer's CURRENT alpha so the animation follows
        // the user's move/resize. maskBytes is reused as the per-frame alpha buffer.
        int frame = 0;
        bool stopAnim = false;
        int inFlight = 0;
        RectInt32 lastBbox = selBounds;   // start with original selection bounds
        var animTimer = new System.Threading.Timer(_ =>
        {
            if (stopAnim) return;
            if (Interlocked.CompareExchange(ref inFlight, 1, 0) != 0) return;
            frame++;
            try
            {
                if (mainForm.IsDisposed) { stopAnim = true; Interlocked.Exchange(ref inFlight, 0); return; }
                mainForm.BeginInvoke(() =>
                {
                    try
                    {
                        if (stopAnim) return;
                        if (!LayerStillInDocument(document!, layer!)) { stopAnim = true; return; }

                        // Read the layer's current alpha (this is what the user has after any moves/resizes).
                        ReadLayerAlphaIntoBuffer(layer!, docSize, maskBytes);
                        RectInt32 currentBbox = ComputeAlphaBbox(maskBytes, docSize);
                        if (currentBbox.Width <= 0 || currentBbox.Height <= 0)
                        {
                            // Placeholder fully erased: pause animation but keep waiting for the API.
                            return;
                        }

                        RenderFrameIntoLayer(layer!, docSize, currentBbox, maskBytes, frame);
                        // Invalidate the union of last and current bbox so a moved placeholder
                        // doesn't leave a ghost behind.
                        RectInt32 invRect = UnionRect(lastBbox, currentBbox);
                        lastBbox = currentBbox;
                        InvalidateLayer(layer!, invRect, document!);
                    }
                    catch (Exception ex) { Log("tick paint err: " + ex.Message); stopAnim = true; }
                    finally { Interlocked.Exchange(ref inFlight, 0); }
                });
            }
            catch (Exception ex) { Log("tick marshal err: " + ex.Message); stopAnim = true; Interlocked.Exchange(ref inFlight, 0); }
        }, null, 33, 33);  // ~30 fps target, coalesces if frames take longer

        // 3) Call Gemini synchronously on this background thread.
        byte[]? resultPng = null;
        string? error = null;
        try
        {
            Log($"Calling Gemini ({pModel}) prompt='{Truncate(pPrompt, 80)}'");
            resultPng = GeminiClient.EditImage(pApiKey, pModel, sourcePng, maskPng, pPrompt, CancellationToken.None);
            Log($"Gemini returned {resultPng.Length} bytes");
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log("Gemini error: " + ex);
        }

        // 4) Stop animation, write final pixels (or remove layer on error).
        stopAnim = true;
        try { animTimer.Dispose(); } catch { }

        if (error != null || resultPng == null)
        {
            try
            {
                mainForm.Invoke(() => RemoveLayer(document!, layer!));
            }
            catch (Exception ex) { Log("Remove-on-error failed: " + ex); }
            ShowError("Gemini error: " + (error ?? "no image returned"));
            return;
        }

        // 5) Apply final result.
        // The model received a CROPPED (selection + padding) input, so its response
        // covers that padded crop. We extract only the INNER region (the part
        // corresponding to the user's original selection) and stamp THAT into the
        // layer at its current bbox, preserving any moves/resizes/erases the user
        // did during loading.
        try
        {
            using var aiInner = ExtractInnerFromPaddedResponse(resultPng, paddedBbox, selBounds);
            mainForm.Invoke(() =>
            {
                try
                {
                    if (!LayerStillInDocument(document!, layer!)) { Log("Layer was removed before final write"); return; }

                    ReadLayerAlphaIntoBuffer(layer!, docSize, maskBytes);
                    RectInt32 finalBbox = ComputeAlphaBbox(maskBytes, docSize);
                    if (finalBbox.Width <= 0 || finalBbox.Height <= 0)
                    {
                        Log("Placeholder layer was fully erased; nothing to apply AI result to.");
                        return;
                    }

                    ApplyAiBitmapToBbox(layer!, docSize, finalBbox, maskBytes, aiInner);
                    SetLayerName(layer!, "Generative Fill");
                    InvalidateLayer(layer!, finalBbox, document!);
                    MarkDocumentDirty(document!);
                    Log($"Applied AI fill to bbox {finalBbox.X},{finalBbox.Y} {finalBbox.Width}x{finalBbox.Height}");
                }
                catch (Exception ex) { Log("Final apply err: " + ex); ShowError("Couldn't apply result: " + ex.Message); }
            });
        }
        catch (Exception ex) { Log("Final decode err: " + ex); ShowError(ex.Message); }
    }

    // ---------------- Animation frame renderer (high-FPS, multi-color) ----------------
    //
    // A CSS-style gradient skeleton. The BASE is a 4-stop diagonal gradient
    // (deep indigo → magenta → coral → amber) for richness, not basic white.
    // The SWEEP is a bright iridescent band with violet leading edge, pearl
    // center, and cyan trailing edge. Pixels are processed in parallel across
    // rows via Parallel.For; per-pixel math is pure integer + table lookup.

    // Multi-stop base gradient. Each stop is (B, G, R). Order = visual order along diagonal.
    private static readonly (int B, int G, int R)[] gradientStops = new (int, int, int)[]
    {
        ( 92,  64, 124),    // deep indigo
        (138,  92, 196),    // royal purple
        (170, 110, 220),    // magenta-pink
        (140, 160, 230),    // coral
        (110, 200, 240),    // amber
    };

    // Highlight band gradient: trailing-edge, center, leading-edge.
    private static readonly (int B, int G, int R) HI_LEAD  = (255, 220, 200);   // soft violet
    private static readonly (int B, int G, int R) HI_CTR   = (252, 248, 244);   // warm pearl
    private static readonly (int B, int G, int R) HI_TRAIL = (220, 245, 255);   // soft cyan

    private const int PALETTE_SIZE = 512;
    private const int BAND_HALF = 200;
    private const int SWEEP_SPEED = 26;

    private static readonly byte[] basePaletteBytes = BuildBasePalette();
    private static byte[] BuildBasePalette()
    {
        var p = new byte[PALETTE_SIZE * 3];
        int segs = gradientStops.Length - 1;
        for (int i = 0; i < PALETTE_SIZE; i++)
        {
            float t = i / (float)(PALETTE_SIZE - 1) * segs;
            int seg = Math.Min((int)t, segs - 1);
            float st = t - seg;
            var a = gradientStops[seg]; var b = gradientStops[seg + 1];
            p[i * 3]     = (byte)(a.B + (b.B - a.B) * st);
            p[i * 3 + 1] = (byte)(a.G + (b.G - a.G) * st);
            p[i * 3 + 2] = (byte)(a.R + (b.R - a.R) * st);
        }
        return p;
    }

    private static unsafe void RenderFrameIntoLayer(object layer, SizeInt32 docSize, RectInt32 selBounds, byte[] maskBytes, int frame)
    {
        IntPtr scan0 = GetLayerScan0(layer, out int stride);
        if (scan0 == IntPtr.Zero) return;
        byte* basePtr = (byte*)scan0.ToPointer();

        int y0 = selBounds.Y, y1 = selBounds.Y + selBounds.Height;
        int x0 = selBounds.X, x1 = selBounds.X + selBounds.Width;
        int W = docSize.Width;
        int selW = selBounds.Width, selH = selBounds.Height;

        int diag = selW + selH;
        int period = diag + BAND_HALF * 2 + 240;
        int sweep = (frame * SWEEP_SPEED) % period;

        // Per-axis scaling so (selX/selW + selY/selH)/2 maps to [0..PALETTE_SIZE).
        // Use 16.16 fixed point: recip = PALETTE_SIZE*65536/dim.
        int recipW = selW > 0 ? (PALETTE_SIZE * 65536) / selW : 0;
        int recipH = selH > 0 ? (PALETTE_SIZE * 65536) / selH : 0;

        int hiLeadB = HI_LEAD.B, hiLeadG = HI_LEAD.G, hiLeadR = HI_LEAD.R;
        int hiCtrB  = HI_CTR.B,  hiCtrG  = HI_CTR.G,  hiCtrR  = HI_CTR.R;
        int hiTrlB  = HI_TRAIL.B, hiTrlG = HI_TRAIL.G, hiTrlR = HI_TRAIL.R;

        // Snapshot palette to a local ref for the parallel body (avoids field load each pixel).
        byte[] pal = basePaletteBytes;
        int bandHalf = BAND_HALF;

        // Parallel across rows.
        Parallel.For(y0, y1, y =>
        {
            byte* row = basePtr + y * stride;
            int selY = y - y0;
            int yGrad = (selY * recipH) >> 16;       // 0..PALETTE_SIZE
            int yBand = y - sweep + (y0 + x0);

            for (int x = x0; x < x1; x++)
            {
                byte a = maskBytes[y * W + x];
                byte* p = row + x * 4;
                if (a == 0) { p[0] = 0; p[1] = 0; p[2] = 0; p[3] = 0; continue; }

                // ---- Base color from gradient palette ----
                int selX = x - x0;
                int xGrad = (selX * recipW) >> 16;
                int gradIdx = ((xGrad + yGrad) >> 1);
                if (gradIdx >= PALETTE_SIZE) gradIdx = PALETTE_SIZE - 1;
                int pOff = gradIdx * 3;
                int baseB = pal[pOff];
                int baseG = pal[pOff + 1];
                int baseR = pal[pOff + 2];

                // ---- Sweep band ----
                int pos = (x + yBand) % period;
                if (pos < 0) pos += period;
                int distSigned = pos - bandHalf;
                int absDist = distSigned < 0 ? -distSigned : distSigned;

                int outB, outG, outR;
                if (absDist >= bandHalf)
                {
                    outB = baseB; outG = baseG; outR = baseR;
                }
                else
                {
                    // Bell: (linear)^2 in 8.8.
                    int linear = ((bandHalf - absDist) << 8) / bandHalf;
                    int bellT = (linear * linear) >> 8;

                    int sideAbs = (absDist << 8) / bandHalf;
                    int hiB, hiG, hiR;
                    if (distSigned >= 0)
                    {
                        hiB = hiCtrB + (((hiTrlB - hiCtrB) * sideAbs) >> 8);
                        hiG = hiCtrG + (((hiTrlG - hiCtrG) * sideAbs) >> 8);
                        hiR = hiCtrR + (((hiTrlR - hiCtrR) * sideAbs) >> 8);
                    }
                    else
                    {
                        hiB = hiCtrB + (((hiLeadB - hiCtrB) * sideAbs) >> 8);
                        hiG = hiCtrG + (((hiLeadG - hiCtrG) * sideAbs) >> 8);
                        hiR = hiCtrR + (((hiLeadR - hiCtrR) * sideAbs) >> 8);
                    }

                    outB = baseB + (((hiB - baseB) * bellT) >> 8);
                    outG = baseG + (((hiG - baseG) * bellT) >> 8);
                    outR = baseR + (((hiR - baseR) * bellT) >> 8);
                }

                p[0] = (byte)outB;
                p[1] = (byte)outG;
                p[2] = (byte)outR;
                p[3] = a;
            }
        });
    }

    private static unsafe void WriteBgraBytesToLayer(object layer, SizeInt32 docSize, byte[] bgra)
    {
        IntPtr scan0 = GetLayerScan0(layer, out int stride);
        if (scan0 == IntPtr.Zero) return;
        byte* basePtr = (byte*)scan0.ToPointer();
        for (int y = 0; y < docSize.Height; y++)
        {
            byte* row = basePtr + y * stride;
            int srcOff = y * docSize.Width * 4;
            for (int x = 0; x < docSize.Width; x++)
            {
                byte* p = row + x * 4;
                int o = srcOff + x * 4;
                p[0] = bgra[o]; p[1] = bgra[o + 1]; p[2] = bgra[o + 2]; p[3] = bgra[o + 3];
            }
        }
    }

    // ---------------- Encoding / decoding helpers ----------------

    private unsafe byte[] EncodeBgra32SourceToPng(IEffectInputBitmap<ColorBgra32> source, SizeInt32 size)
    {
        using var bmp = new Bitmap(size.Width, size.Height, SDImg.PixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, size.Width, size.Height);
        SDImg.BitmapData bd = bmp.LockBits(rect, SDImg.ImageLockMode.WriteOnly, SDImg.PixelFormat.Format32bppArgb);
        try
        {
            using IBitmapLock<ColorBgra32> sl = source.Lock(new RectInt32(0, 0, size.Width, size.Height));
            RegionPtr<ColorBgra32> sr = sl.AsRegionPtr();
            byte* dstBase = (byte*)bd.Scan0.ToPointer();
            for (int y = 0; y < size.Height; y++)
            {
                byte* dstRow = dstBase + y * bd.Stride;
                for (int x = 0; x < size.Width; x++)
                {
                    ColorBgra32 c = sr[x, y];
                    byte* p = dstRow + x * 4;
                    p[0] = c.B; p[1] = c.G; p[2] = c.R; p[3] = c.A;
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        using var ms = new MemoryStream();
        bmp.Save(ms, SDImg.ImageFormat.Png);
        return ms.ToArray();
    }

    private unsafe byte[] ExtractMaskAlpha(IEffectInputBitmap<ColorAlpha8> mask, SizeInt32 size)
    {
        byte[] result = new byte[size.Width * size.Height];
        using IBitmapLock<ColorAlpha8> ml = mask.Lock(new RectInt32(0, 0, size.Width, size.Height));
        RegionPtr<ColorAlpha8> mr = ml.AsRegionPtr();
        for (int y = 0; y < size.Height; y++)
            for (int x = 0; x < size.Width; x++)
                result[y * size.Width + x] = mr[x, y].A;
        return result;
    }

    private unsafe byte[] EncodeMaskBytesToBwPng(byte[] mask, SizeInt32 size)
    {
        using var bmp = new Bitmap(size.Width, size.Height, SDImg.PixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, size.Width, size.Height);
        SDImg.BitmapData bd = bmp.LockBits(rect, SDImg.ImageLockMode.WriteOnly, SDImg.PixelFormat.Format32bppArgb);
        try
        {
            byte* dstBase = (byte*)bd.Scan0.ToPointer();
            for (int y = 0; y < size.Height; y++)
            {
                uint* dst = (uint*)(dstBase + y * bd.Stride);
                for (int x = 0; x < size.Width; x++)
                {
                    byte v = mask[y * size.Width + x] >= 128 ? (byte)255 : (byte)0;
                    dst[x] = (uint)((255u << 24) | ((uint)v << 16) | ((uint)v << 8) | v);
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        using var ms = new MemoryStream();
        bmp.Save(ms, SDImg.ImageFormat.Png);
        return ms.ToArray();
    }

    // Encode just the cropped region of the source (BGRA) as PNG.
    private unsafe byte[] EncodeBgra32SourceCropToPng(IEffectInputBitmap<ColorBgra32> source, SizeInt32 docSize, RectInt32 crop)
    {
        using var bmp = new Bitmap(crop.Width, crop.Height, SDImg.PixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, crop.Width, crop.Height);
        SDImg.BitmapData bd = bmp.LockBits(rect, SDImg.ImageLockMode.WriteOnly, SDImg.PixelFormat.Format32bppArgb);
        try
        {
            using IBitmapLock<ColorBgra32> sl = source.Lock(new RectInt32(crop.X, crop.Y, crop.Width, crop.Height));
            RegionPtr<ColorBgra32> sr = sl.AsRegionPtr();
            byte* dstBase = (byte*)bd.Scan0.ToPointer();
            for (int y = 0; y < crop.Height; y++)
            {
                byte* dstRow = dstBase + y * bd.Stride;
                for (int x = 0; x < crop.Width; x++)
                {
                    ColorBgra32 c = sr[x, y];
                    byte* p = dstRow + x * 4;
                    p[0] = c.B; p[1] = c.G; p[2] = c.R; p[3] = c.A;
                }
            }
        }
        finally { bmp.UnlockBits(bd); }

        using var ms = new MemoryStream();
        bmp.Save(ms, SDImg.ImageFormat.Png);
        return ms.ToArray();
    }

    // Encode just the cropped region of the mask as B/W PNG.
    private unsafe byte[] EncodeMaskBytesCropToBwPng(byte[] maskBytes, SizeInt32 docSize, RectInt32 crop)
    {
        using var bmp = new Bitmap(crop.Width, crop.Height, SDImg.PixelFormat.Format32bppArgb);
        var rect = new System.Drawing.Rectangle(0, 0, crop.Width, crop.Height);
        SDImg.BitmapData bd = bmp.LockBits(rect, SDImg.ImageLockMode.WriteOnly, SDImg.PixelFormat.Format32bppArgb);
        try
        {
            byte* dstBase = (byte*)bd.Scan0.ToPointer();
            int W = docSize.Width;
            for (int y = 0; y < crop.Height; y++)
            {
                uint* dst = (uint*)(dstBase + y * bd.Stride);
                int srcRow = (y + crop.Y) * W + crop.X;
                for (int x = 0; x < crop.Width; x++)
                {
                    byte v = maskBytes[srcRow + x] >= 128 ? (byte)255 : (byte)0;
                    dst[x] = (uint)((255u << 24) | ((uint)v << 16) | ((uint)v << 8) | v);
                }
            }
        }
        finally { bmp.UnlockBits(bd); }

        using var ms = new MemoryStream();
        bmp.Save(ms, SDImg.ImageFormat.Png);
        return ms.ToArray();
    }

    // Given Gemini's response (covering the padded crop), extract just the inner
    // rectangle that corresponds to the user's original selection. Returns a
    // Bitmap sized to (selBounds.Width × selBounds.Height). Caller owns disposal.
    private Bitmap ExtractInnerFromPaddedResponse(byte[] responsePng, RectInt32 paddedBbox, RectInt32 selBounds)
    {
        using var ms = new MemoryStream(responsePng);
        using var raw = (Bitmap)Image.FromStream(ms);

        // First, normalize the response to the padded-crop dimensions. The model
        // may not return exact dimensions; rescale via GDI+ if needed.
        Bitmap atPaddedSize;
        bool disposeAtPadded = false;
        if (raw.Width != paddedBbox.Width || raw.Height != paddedBbox.Height
            || raw.PixelFormat != SDImg.PixelFormat.Format32bppArgb)
        {
            atPaddedSize = new Bitmap(paddedBbox.Width, paddedBbox.Height, SDImg.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(atPaddedSize))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(raw, 0, 0, paddedBbox.Width, paddedBbox.Height);
            }
            disposeAtPadded = true;
        }
        else atPaddedSize = raw;

        try
        {
            // Inner offset within padded crop = selBounds - paddedBbox top-left.
            int innerX = selBounds.X - paddedBbox.X;
            int innerY = selBounds.Y - paddedBbox.Y;
            var innerRect = new System.Drawing.Rectangle(innerX, innerY, selBounds.Width, selBounds.Height);
            // Bitmap.Clone with a sub-rectangle returns a new Bitmap of that region.
            return atPaddedSize.Clone(innerRect, SDImg.PixelFormat.Format32bppArgb);
        }
        finally { if (disposeAtPadded) atPaddedSize.Dispose(); }
    }

    // Reads the layer's current alpha into the provided buffer (docW*docH bytes).
    private static unsafe void ReadLayerAlphaIntoBuffer(object layer, SizeInt32 docSize, byte[] buffer)
    {
        IntPtr scan0 = GetLayerScan0(layer, out int stride);
        if (scan0 == IntPtr.Zero) return;
        byte* basePtr = (byte*)scan0.ToPointer();
        int W = docSize.Width, H = docSize.Height;
        for (int y = 0; y < H; y++)
        {
            byte* row = basePtr + y * stride;
            int dst = y * W;
            for (int x = 0; x < W; x++)
                buffer[dst + x] = row[x * 4 + 3];
        }
    }

    // Compute the axis-aligned bbox of pixels with alpha > 0.
    private static RectInt32 ComputeAlphaBbox(byte[] mask, SizeInt32 docSize)
    {
        int W = docSize.Width, H = docSize.Height;
        int minX = W, maxX = -1, minY = H, maxY = -1;
        for (int y = 0; y < H; y++)
        {
            int row = y * W;
            int rowMin = -1, rowMax = -1;
            for (int x = 0; x < W; x++)
            {
                if (mask[row + x] != 0)
                {
                    if (rowMin == -1) rowMin = x;
                    rowMax = x;
                }
            }
            if (rowMin >= 0)
            {
                if (rowMin < minX) minX = rowMin;
                if (rowMax > maxX) maxX = rowMax;
                if (y < minY) minY = y;
                maxY = y;
            }
        }
        if (maxX < 0) return new RectInt32(0, 0, 0, 0);
        return new RectInt32(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static RectInt32 UnionRect(RectInt32 a, RectInt32 b)
    {
        if (a.Width <= 0) return b;
        if (b.Width <= 0) return a;
        int x0 = Math.Min(a.X, b.X);
        int y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width);
        int y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new RectInt32(x0, y0, x1 - x0, y1 - y0);
    }

    // Stamp an AI bitmap into the layer at the user's current bbox, scaling to fit.
    // Alpha at each destination pixel is preserved (from the layer/maskBytes), so
    // the AI fill takes the exact shape of whatever the user moved/resized to.
    private static unsafe void ApplyAiBitmapToBbox(object layer, SizeInt32 docSize, RectInt32 bbox, byte[] maskBytes, Bitmap ai)
    {
        IntPtr scan0 = GetLayerScan0(layer, out int stride);
        if (scan0 == IntPtr.Zero) return;
        byte* basePtr = (byte*)scan0.ToPointer();

        // Scale the AI bitmap to the bbox size (in-process, no Paint.NET work).
        using var scaled = new Bitmap(bbox.Width, bbox.Height, SDImg.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(ai, 0, 0, bbox.Width, bbox.Height);
        }

        var srcRect = new System.Drawing.Rectangle(0, 0, bbox.Width, bbox.Height);
        SDImg.BitmapData sd = scaled.LockBits(srcRect, SDImg.ImageLockMode.ReadOnly, SDImg.PixelFormat.Format32bppArgb);
        try
        {
            byte* srcBase = (byte*)sd.Scan0.ToPointer();
            int W = docSize.Width;
            for (int by = 0; by < bbox.Height; by++)
            {
                byte* srcRow = srcBase + by * sd.Stride;
                byte* dstRow = basePtr + (bbox.Y + by) * stride;
                for (int bx = 0; bx < bbox.Width; bx++)
                {
                    byte a = maskBytes[(bbox.Y + by) * W + (bbox.X + bx)];
                    if (a == 0) continue;
                    byte* sp = srcRow + bx * 4;
                    byte* dp = dstRow + (bbox.X + bx) * 4;
                    dp[0] = sp[0];
                    dp[1] = sp[1];
                    dp[2] = sp[2];
                    dp[3] = a;                  // preserve user's current alpha
                }
            }
        }
        finally { scaled.UnlockBits(sd); }
    }

    // ---------------- Reflection: layer insert / read / invalidate / remove ----------------

    private static Form? FindMainForm()
    {
        foreach (Form f in Application.OpenForms)
            if (f.GetType().FullName == "PaintDotNet.Dialogs.MainForm") return f;
        return null;
    }

    private static Type FindLoadedType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t;
            try { t = asm.GetType(fullName, throwOnError: false); }
            catch { continue; }
            if (t != null) return t;
        }
        throw new InvalidOperationException($"Type {fullName} not found in any loaded assembly");
    }

    private const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static (object layer, object document) InsertEmptyLayer(Form mainForm, SizeInt32 size, string name)
    {
        var appWsField = mainForm.GetType().GetField("appWorkspace", BF)
            ?? throw new InvalidOperationException("MainForm.appWorkspace missing");
        object appWorkspace = appWsField.GetValue(mainForm)
            ?? throw new InvalidOperationException("appWorkspace is null");

        object docWorkspace = appWorkspace.GetType().GetProperty("ActiveDocumentWorkspace", BF)!.GetValue(appWorkspace)
            ?? throw new InvalidOperationException("No active document workspace");
        object document = docWorkspace.GetType().GetProperty("Document", BF)!.GetValue(docWorkspace)
            ?? throw new InvalidOperationException("Document is null");
        int activeIndex = (int)(docWorkspace.GetType().GetProperty("ActiveLayerIndex", BF)!.GetValue(docWorkspace) ?? 0);

        Type surfaceType = FindLoadedType("PaintDotNet.Surface");
        Type bitmapLayerType = FindLoadedType("PaintDotNet.BitmapLayer");

        object surface = surfaceType.GetConstructor(new[] { typeof(int), typeof(int) })!
            .Invoke(new object[] { size.Width, size.Height });
        object layer = bitmapLayerType.GetConstructor(new[] { surfaceType, typeof(bool) })!
            .Invoke(new object[] { surface, true });

        layer.GetType().GetProperty("Name", BF)?.SetValue(layer, name);

        object layers = document.GetType().GetProperty("Layers", BF)!.GetValue(document)!;
        var insertMethod = layers.GetType().GetMethods(BF).First_NoLinq(m => m.Name == "Insert" && m.GetParameters().Length == 2);
        int insertAt = activeIndex + 1;
        insertMethod.Invoke(layers, new object[] { insertAt, layer });

        docWorkspace.GetType().GetProperty("ActiveLayerIndex", BF)?.SetValue(docWorkspace, insertAt);
        return (layer, document);
    }

    private static IntPtr GetLayerScan0(object layer, out int stride)
    {
        object surface = layer.GetType().GetProperty("Surface", BF)!.GetValue(layer)!;
        stride = (int)surface.GetType().GetProperty("Stride", BF)!.GetValue(surface)!;
        object scan0Memblock = surface.GetType().GetProperty("Scan0", BF)!.GetValue(surface)!;
        return (IntPtr)scan0Memblock.GetType().GetProperty("Pointer", BF)!.GetValue(scan0Memblock)!;
    }

    private static void InvalidateLayer(object layer, RectInt32 rect, object document)
    {
        try
        {
            // Prefer Document.Invalidate(RectInt32): it forces a canvas redraw.
            var inv = document.GetType().GetMethod("Invalidate", new[] { typeof(RectInt32) });
            if (inv != null) { inv.Invoke(document, new object[] { rect }); return; }
        }
        catch { }
        try
        {
            var inv = layer.GetType().GetMethod("Invalidate", new[] { typeof(RectInt32) });
            inv?.Invoke(layer, new object[] { rect });
        }
        catch { }
    }

    private static bool LayerStillInDocument(object document, object layer)
    {
        try
        {
            object layers = document.GetType().GetProperty("Layers", BF)!.GetValue(document)!;
            int idx = (int)layers.GetType().GetMethod("IndexOf", new[] { layer.GetType().BaseType! })!
                .Invoke(layers, new object[] { layer })!;
            return idx >= 0;
        }
        catch { return false; }
    }

    private static void RemoveLayer(object document, object layer)
    {
        try
        {
            object layers = document.GetType().GetProperty("Layers", BF)!.GetValue(document)!;
            var removeMethod = layers.GetType().GetMethods(BF).First_NoLinq(m => m.Name == "Remove" && m.GetParameters().Length == 1);
            removeMethod.Invoke(layers, new object[] { layer });
        }
        catch (Exception ex) { Log("RemoveLayer err: " + ex.Message); }
    }

    private static void SetLayerName(object layer, string name)
    {
        try { layer.GetType().GetProperty("Name", BF)?.SetValue(layer, name); }
        catch { }
    }

    private static void MarkDocumentDirty(object document)
    {
        try { document.GetType().GetProperty("Dirty", BF)?.SetValue(document, true); }
        catch { }
    }

    // ---------------- Errors ----------------

    private static void ShowError(string msg)
    {
        var t = new Thread(() =>
        {
            try { MessageBox.Show(msg, "Generative Fill (Gemini)", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // ---------------- Logging ----------------

    private static readonly object logLock = new();
    private static readonly string LogPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "GeminiFillPlugin", "debug.log");

    internal static void Log(string msg)
    {
        try
        {
            lock (logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{System.Environment.NewLine}");
            }
        }
        catch { }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
}

// Small Linq-free helpers (avoid adding System.Linq surface).
internal static class ReflectExt
{
    public static T? FirstOrDefault_NoLinq<T>(this IEnumerable<T> seq, Func<T, bool> pred)
    {
        foreach (var x in seq) if (pred(x)) return x;
        return default;
    }
    public static T First_NoLinq<T>(this IEnumerable<T> seq, Func<T, bool> pred)
    {
        foreach (var x in seq) if (pred(x)) return x;
        throw new InvalidOperationException("no match");
    }
}
