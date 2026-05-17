using PaintDotNet;
using System;
using System.Reflection;

namespace GeminiFillPlugin;

public sealed class PluginSupportInfo : IPluginSupportInfo
{
    public string Author => "yuvalsombol12@gmail.com";
    public string Copyright => "MIT";
    public string DisplayName => "Generative Fill (Gemini)";
    public Version Version => typeof(PluginSupportInfo).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
    public Uri WebsiteUri => new Uri("https://ai.google.dev/gemini-api/docs/image-generation");
}
