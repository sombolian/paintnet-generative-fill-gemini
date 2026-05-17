using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GeminiFillPlugin;

internal static class GeminiClient
{
    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public sealed class GeminiException : Exception { public GeminiException(string m) : base(m) { } }

    /// <summary>
    /// Sends source PNG + binary mask PNG + prompt to Gemini image-edit model.
    /// Returns PNG bytes of the edited image, or throws GeminiException with the API error.
    /// </summary>
    public static byte[] EditImage(
        string apiKey,
        string model,
        byte[] sourcePng,
        byte[] maskPng,
        string userPrompt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new GeminiException("Missing API key. Get one at https://aistudio.google.com/apikey");

        string fullPrompt =
            "You are performing PRECISE LOCAL INPAINTING on a small image crop.\n" +
            "Two images are provided:\n" +
            "  1) IMAGE: a small crop containing the area to edit plus a context border.\n" +
            "  2) MASK: a black-and-white image of the same size. WHITE pixels mark the EXACT region to replace. BLACK pixels are surrounding context, they MUST stay PIXEL-IDENTICAL to the input.\n" +
            "\n" +
            "Replace ONLY the WHITE-masked region with: " + userPrompt.Trim() + "\n" +
            "\n" +
            "Rules, these are strict, do not break them:\n" +
            "  - Output dimensions must match the input image exactly.\n" +
            "  - Every BLACK-masked pixel in the output must equal the corresponding pixel in the input image.\n" +
            "  - DO NOT redraw or stylize the BLACK-masked region. Treat it as fixed reference for blending only.\n" +
            "  - Blend the new content into the WHITE region seamlessly: match the lighting, perspective, color temperature, texture grain, and shadows of the surrounding BLACK-masked context.\n" +
            "  - Output exactly one image.";

        var body = new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = fullPrompt },
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject
                            {
                                ["mime_type"] = "image/png",
                                ["data"] = Convert.ToBase64String(sourcePng)
                            }
                        },
                        new JsonObject
                        {
                            ["inline_data"] = new JsonObject
                            {
                                ["mime_type"] = "image/png",
                                ["data"] = Convert.ToBase64String(maskPng)
                            }
                        }
                    }
                }
            },
            ["generationConfig"] = new JsonObject
            {
                ["responseModalities"] = new JsonArray { "TEXT", "IMAGE" }
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        string respText;
        try
        {
            resp = Http.Send(req, HttpCompletionOption.ResponseContentRead, ct);
            using var sr = new StreamReader(resp.Content.ReadAsStream(ct));
            respText = sr.ReadToEnd();
        }
        catch (Exception ex)
        {
            throw new GeminiException("Network error: " + ex.Message);
        }

        if (!resp.IsSuccessStatusCode)
        {
            string msg = TryExtractError(respText) ?? ((int)resp.StatusCode + " " + resp.ReasonPhrase);
            throw new GeminiException("Gemini API error: " + msg);
        }

        var imageBytes = TryExtractInlineImage(respText);
        if (imageBytes == null)
        {
            string textPart = TryExtractText(respText) ?? "no image returned";
            throw new GeminiException("Gemini returned no image. Response: " + Truncate(textPart, 300));
        }
        return imageBytes;
    }

    private static byte[]? TryExtractInlineImage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var candidates = doc.RootElement.GetProperty("candidates");
            foreach (var cand in candidates.EnumerateArray())
            {
                if (!cand.TryGetProperty("content", out var content)) continue;
                if (!content.TryGetProperty("parts", out var parts)) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("inlineData", out var inline)
                        || part.TryGetProperty("inline_data", out inline))
                    {
                        if (inline.TryGetProperty("data", out var dataProp))
                        {
                            var b64 = dataProp.GetString();
                            if (!string.IsNullOrEmpty(b64)) return Convert.FromBase64String(b64);
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? TryExtractText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var candidates = doc.RootElement.GetProperty("candidates");
            foreach (var cand in candidates.EnumerateArray())
            {
                if (!cand.TryGetProperty("content", out var content)) continue;
                if (!content.TryGetProperty("parts", out var parts)) continue;
                foreach (var part in parts.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var t)) return t.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    private static string? TryExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err)
                && err.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch { }
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
}
