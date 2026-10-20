using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using LiveInteract.Contracts;

namespace LiveInteract.Silksong;

/// <summary>复用游戏目录中的原生资源包及其依赖，不加载其他房间、不改写游戏资源目录。</summary>
internal sealed class GameAssetCatalog
{
    // 只缓存资源定位信息，不保留额外游戏对象。
    private readonly Dictionary<string, IResourceLocation> bundles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IResourceLocation> visited = new();
    private readonly Dictionary<string, HashSet<IResourceLocation>> bundleDependencies = new(StringComparer.OrdinalIgnoreCase);
    private bool indexed;

    /// <summary>已在实际运行目录找到的具名怪物标识。</summary>
    public List<string> AvailableIds => MonsterCatalog.All.Where(item => bundles.ContainsKey(item.Bundle)).Select(item => item.Id).ToList();

    /// <summary>在游戏完成自己的资源初始化后，读取目录及依赖。</summary>
    public void Index()
    {
        if (indexed || !Addressables.ResourceLocators.Any()) { return; }
        foreach (var locator in Addressables.ResourceLocators.ToArray())
        {
            foreach (var key in locator.Keys.ToArray())
            {
                if (locator.Locate(key, null, out var locations))
                {
                    foreach (var location in locations) { Visit(location); }
                }
            }
        }
        indexed = bundles.Count > 0;
    }

    /// <summary>为具名预制体创建位置，依赖使用游戏本身的资源包目录。</summary>
    /// <param name="monster">已核对的映射。</param>
    public IResourceLocation Locate(MonsterDefinition monster)
    {
        Index();
        if (!bundles.TryGetValue(monster.Bundle, out var bundle))
        {
            throw new InvalidOperationException("当前游戏版本没有找到" + monster.Name + "的资源包。请确认游戏版本与本工具的兼容说明。");
        }
        var provider = Addressables.ResourceManager.ResourceProviders.OfType<BundledAssetProvider>().FirstOrDefault();
        if (provider == null) { throw new InvalidOperationException("游戏的预制体资源加载器尚未就绪。"); }
        // BundledAssetProvider 取第一个 IAssetBundleResource 为主包；辅助依赖不能排到主包前面。
        var required = new List<IResourceLocation> { bundle };
        void AddGroup(string name)
        {
            if (bundles.TryGetValue(name, out var location) && !required.Contains(location)) { required.Add(location); }
            if (bundleDependencies.TryGetValue(name, out var group))
            {
                foreach (var dependency in group) { if (!required.Contains(dependency)) { required.Add(dependency); } }
            }
        }
        AddGroup(monster.Bundle);
        foreach (var name in monster.AnimationBundles) { AddGroup(name); }
        var dependencies = required.ToArray();
        return new ResourceLocationBase("LiveInteract/" + monster.Id, monster.Asset, provider.ProviderId, typeof(GameObject), dependencies);
    }

    /// <summary>遍历已有依赖，允许不同资源目录复用相同包。</summary>
    private void Visit(IResourceLocation location)
    {
        if (visited.Count >= 100000 || !visited.Add(location)) { return; }
        if (location.ProviderId.Contains("AssetBundleProvider"))
        {
            var id = location.InternalId.Replace('\\', '/');
            var name = id.Substring(id.LastIndexOf('/') + 1);
            if (name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)) { bundles[name] = location; }
        }
        if (location.Dependencies != null)
        {
            var primary = location.Dependencies.FirstOrDefault(dependency => dependency.ProviderId.Contains("AssetBundleProvider"));
            if (primary != null)
            {
                var path = primary.InternalId.Replace('\\', '/'); var name = path.Substring(path.LastIndexOf('/') + 1);
                if (!bundleDependencies.TryGetValue(name, out var group)) { group = new(); bundleDependencies[name] = group; }
                foreach (var related in location.Dependencies) { group.Add(related); }
            }
            foreach (var dependency in location.Dependencies) { Visit(dependency); }
        }
    }
}
