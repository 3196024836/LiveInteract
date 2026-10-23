using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using AssetHelperLib.Repacking;
using AssetHelperLib.PreloadTable;
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
    private static readonly object repackGate=new();
    private IResourceLocation RequireBundle(string relative)
    {
        var filename=Path.GetFileName(relative);
        if(bundles.TryGetValue(filename,out var found))return found;
        var path=Path.Combine(Application.streamingAssetsPath,"aa","StandaloneWindows64",relative.Replace('/',Path.DirectorySeparatorChar));
        if(!File.Exists(path))throw new InvalidOperationException("缺少原作依赖资源包："+relative+"；本单位未创建");
        var provider=Addressables.ResourceManager.ResourceProviders.OfType<AssetBundleProvider>().FirstOrDefault()??throw new InvalidOperationException("原作资源包加载器尚未就绪");
        var location=new ResourceLocationBase("LiveInteract.Dependency/"+relative,path,provider.ProviderId,typeof(IAssetBundleResource));
        location.Data=new AssetBundleRequestOptions{BundleName=Path.GetFileNameWithoutExtension(relative),AssetLoadMode=AssetLoadMode.RequestedAssetAndDependencies};
        bundles[filename]=location;return location;
    }
    private readonly Dictionary<string,Task<RepackedBundleData>> repacking=new();
    private readonly Dictionary<string,string> cachedPaths=new();
    private readonly Dictionary<string,string> relativePaths=new();
    public GameObject Resolve(MonsterDefinition monster,GameObject prefab)=>relativePaths.TryGetValue(monster.Id,out var relative)&&relative.Length>0?prefab.transform.Find(relative)?.gameObject??throw new InvalidOperationException("原作提取对象路径不存在："+monster.Name):prefab;

    /// <summary>只在本机缓存所需原作对象，不切场景、不运行原房间逻辑、不修改或分发游戏资源。</summary>
    public async Task<IResourceLocation> LocateAsync(MonsterDefinition monster)
    {
        if(monster.SourceScene.Length==0)return Locate(monster);
        Index();var key=Path.GetFileName(monster.Bundle);
        var original=RequireBundle(monster.Bundle);
        if(!repacking.TryGetValue(monster.SourceScene,out var task))
        {
            var source=Path.Combine(Application.streamingAssetsPath,"aa","StandaloneWindows64",monster.Bundle.Replace('/',Path.DirectorySeparatorChar));
            var cache=Path.Combine(BepInEx.Paths.CachePath,"LiveInteract","scene-prefabs-0.8-preload2",Application.version,monster.SourceScene);
            cachedPaths[monster.SourceScene]=Path.Combine(cache,"objects.bundle");
            var names=MonsterCatalog.All.Where(m=>m.SourceScene==monster.SourceScene).Select(m=>m.Asset).Distinct().ToList();
            task=Task.Run(()=>
            {
                Directory.CreateDirectory(cache);var output=Path.Combine(cache,"objects.bundle");var metadata=Path.Combine(cache,"objects.json");
                var info=new FileInfo(source);var stamp=info.Length+":"+info.LastWriteTimeUtc.Ticks;
                if(File.Exists(output)&&File.Exists(metadata))
                {
                    var saved=Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(metadata));
                    if((string?)saved["Stamp"]==stamp&&saved["Objects"]?.ToObject<List<string>>()?.SequenceEqual(names)==true)
                        return saved["Data"]!.ToObject<RepackedBundleData>()!;
                }
                bool ResolveCab(string cab,out string? path)
                {
                    path=null;
                    if(cab.IndexOf("unity",StringComparison.OrdinalIgnoreCase)>=0)return true;
                    var relative=MonsterCatalog.BundleForCab(cab);if(relative==null)return false;
                    if(relative.IndexOf("monoscripts",StringComparison.OrdinalIgnoreCase)>=0||relative.IndexOf("builtinassets",StringComparison.OrdinalIgnoreCase)>=0)return true;
                    path=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(source))!,relative);return File.Exists(path);
                }
                RepackedBundleData data;
                lock(repackGate)
                {
                    var resolver=new PreloadTableResolver(new List<BasePreloadTableResolver>{new DefaultPreloadTableResolver(),new ContainerPointerPreloads(ResolveCab)});
                    data=new StrippedSceneRepacker(resolver).Repack(new RepackingParams{SceneBundlePath=source,ObjectNames=names,ContainerPrefix="liveinteract",OutBundlePath=output});
                }
                File.WriteAllText(metadata,JsonConvert.SerializeObject(new{Stamp=stamp,Objects=names,Data=data},Formatting.Indented));return data;
            });repacking[monster.SourceScene]=task;
        }
        RepackedBundleData repacked;
        try{repacked=await task;}catch{repacking.Remove(monster.SourceScene);throw;}
        if(!repacked.CanLoad(monster.Asset,out var asset,out var relative))
            throw new InvalidOperationException("原作对象无法独立提取："+monster.Name+" · "+monster.SourceScene+"/"+monster.Asset);
        relativePaths[monster.Id]=relative??"";
        var provider=Addressables.ResourceManager.ResourceProviders.OfType<BundledAssetProvider>().FirstOrDefault()??throw new InvalidOperationException("原生资源加载器尚未就绪");
        var packed=new ResourceLocationBase("LiveInteract.Cache/"+monster.SourceScene,cachedPaths[monster.SourceScene],original.ProviderId,typeof(IAssetBundleResource));
        packed.Data=new AssetBundleRequestOptions{BundleName=repacked.BundleName};
        var required=new List<IResourceLocation>{packed};
        if(bundleDependencies.TryGetValue(key,out var dependencies))required.AddRange(dependencies.Where(d=>d!=original));
        foreach(var dependencyName in monster.AnimationBundles)
        {
            var filename=Path.GetFileName(dependencyName);
            var dependency=RequireBundle(dependencyName);if(!required.Contains(dependency))required.Add(dependency);
            if(bundleDependencies.TryGetValue(filename,out var group))foreach(var related in group)if(related!=original&&!required.Contains(related))required.Add(related);
        }
        return new ResourceLocationBase("LiveInteract/"+monster.Id,asset,provider.ProviderId,typeof(GameObject),required.Distinct().ToArray());
    }

    /// <summary>已在实际运行目录找到的具名怪物标识。</summary>
    public List<string> AvailableIds => MonsterCatalog.All.Where(item => bundles.ContainsKey(Path.GetFileName(item.Bundle))).Select(item => item.Id).ToList();

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
        var bundle=RequireBundle(monster.Bundle);
        var provider = Addressables.ResourceManager.ResourceProviders.OfType<BundledAssetProvider>().FirstOrDefault();
        if (provider == null) { throw new InvalidOperationException("游戏的预制体资源加载器尚未就绪。"); }
        // BundledAssetProvider 取第一个 IAssetBundleResource 为主包；辅助依赖不能排到主包前面。
        var required = new List<IResourceLocation> { bundle };
        void AddGroup(string name)
        {
            var location=RequireBundle(name);name=Path.GetFileName(name);
            if(!required.Contains(location))required.Add(location);
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
