using System.Security.Cryptography;
using DistributedHls.Core;
using Jellyfin.Plugin.DistributedHls.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly IServiceProvider _services;

    public Plugin(IApplicationPaths paths, IXmlSerializer serializer, IServiceProvider services) : base(paths, serializer)
    {
        _services = services;
        Instance = this;
        // PlaybackGrant refuses an empty key, so a usable key exists before anything can sign; a weak one is replaced too.
        var current = Configuration.PlaybackSigningKey;
        if (!IsValidKey(current))
        {
            Configuration.PlaybackSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            SaveConfiguration();
            if (!string.IsNullOrEmpty(current))
                services.GetService<ILogger<Plugin>>()?.LogWarning("播放签名密钥不是至少 32 字节的 Base64，已换成新生成的密钥；之前签发的播放授权随之失效。");
        }
    }

    public static Plugin Instance { get; private set; } = null!;
    public override Guid Id => Guid.Parse("1d46b74f-6daf-4c2c-a28f-52f202679c16");
    public override string Name => "Distributed HLS";
    public override string Description => "离线 HLS 编码、Worker 调度与多版本播放。";
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "distributedhls", EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html" },
        new() { Name = "distributedhls.js", EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.js" }
    ];

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var c = (PluginConfiguration)configuration;
        if (c.Qualities is null || c.Qualities.Any(q => !EncodingLadder.Heights.Contains(q)) || c.Qualities.Distinct().Count() != c.Qualities.Length)
            throw new ArgumentException("质量必须为列表中的有效值，且不能重复。");
        // An empty list would withdraw every published rendition at once, beyond undoing.
        if (c.Qualities.Length == 0) throw new ArgumentException("至少需要选择一个质量；如需停用请关闭“启用”。");
        if (string.IsNullOrEmpty(c.OutputPath) || !Path.IsPathFullyQualified(c.OutputPath)) throw new ArgumentException("输出目录必须为绝对路径。");
        if (c.LeaseMinutes is < 1 or > 60) throw new ArgumentException("租约时长必须为 1–60 分钟。");
        if (c.PlaybackTokenHours is < 1 or > 168) throw new ArgumentException("播放授权有效期必须为 1–168 小时。");
        if (c.OrphanRetentionHours is < 0 or > 8760) throw new ArgumentException("未关联成果保留时长必须为 0–8760 小时。");
        if (c.MaxAttempts is < 1 or > 20) throw new ArgumentException("最大重试次数必须为 1–20。");
        if (c.OriginalUsers is null || c.Libraries is null) throw new ArgumentException("用户列表和媒体库列表不能为空值。");
        // The settings page never shows the key: an empty value keeps the current one.
        if (string.IsNullOrEmpty(c.PlaybackSigningKey)) c.PlaybackSigningKey = Configuration.PlaybackSigningKey;
        else if (!IsValidKey(c.PlaybackSigningKey)) throw new ArgumentException("播放签名密钥必须为至少 32 字节的 Base64。");
        base.UpdateConfiguration(configuration);
    }

    // Runs synchronously inside the uninstall request.
    public override void OnUninstalling()
    {
        try
        {
            _services.GetRequiredService<VersionCatalog>().RemoveAllAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _services.GetService<ILogger<Plugin>>()?.LogError(ex, "卸载时删除 HLS 版本条目失败。");
        }
        base.OnUninstalling();
    }

    // At least 32 bytes, so the HMAC of a grant cannot be forged by trying keys.
    private static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        try { return Convert.FromBase64String(key).Length >= 32; }
        catch (FormatException) { return false; }
    }
}
