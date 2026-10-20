using System.IO;
using Newtonsoft.Json;

namespace LiveInteract.Desktop;

/// <summary>仅保存本机偏好，不包含永久修改许可、直播总开关或登录 Cookie。</summary>
public sealed class AppSettings
{
    /// <summary>已经确认的游戏目录。</summary>
    public string GameDirectory { get; set; } = "";
    /// <summary>直播间公开地址或短号。</summary>
    public string Room { get; set; } = "";
    /// <summary>上次使用的直播平台：douyin 或 bilibili。</summary>
    public string Platform { get; set; } = "douyin";
    /// <summary>哔哩哔哩直播间公开地址或房间号。</summary>
    public string BilibiliRoom { get; set; } = "";
    /// <summary>是否开启本机透明展示。</summary>
    public bool BrowserOverlay { get; set; } = true;
    /// <summary>是否显示游戏内提示。</summary>
    public bool GameOverlay { get; set; } = true;
    /// <summary>昵称隐藏。</summary>
    public bool HideNicknames { get; set; }
    /// <summary>展示字体大小。</summary>
    public int FontSize { get; set; } = 22;
    /// <summary>停靠位置。</summary>
    public string Corner { get; set; } = "BottomLeft";
    /// <summary>展示主色。</summary>
    public string Accent { get; set; } = "#79E0C8";
    /// <summary>浏览器源通知持续秒数。</summary>
    public double BrowserNoticeSeconds { get; set; } = 6;
    /// <summary>浏览器源是否显示触发来源。</summary>
    public bool BrowserShowSource { get; set; } = true;
    /// <summary>浏览器源是否显示执行结果。</summary>
    public bool BrowserShowResult { get; set; } = true;
    /// <summary>浏览器源是否显示临时效果。</summary>
    public bool BrowserShowEffects { get; set; } = true;
    /// <summary>浏览器源是否显示累计进度。</summary>
    public bool BrowserShowProgress { get; set; } = true;
    /// <summary>游戏内独立字号。</summary>
    public int GameFontSize { get; set; } = 18;
    /// <summary>游戏内独立位置。</summary>
    public string GameCorner { get; set; } = "BottomLeft";
    /// <summary>游戏内独立主色。</summary>
    public string GameAccent { get; set; } = "#D9F9EF";
    /// <summary>游戏内通知持续秒数。</summary>
    public double GameNoticeSeconds { get; set; } = 5;
    /// <summary>游戏内是否显示来源。</summary>
    public bool GameShowSource { get; set; } = true;
    /// <summary>游戏内是否显示结果。</summary>
    public bool GameShowResult { get; set; } = true;
    /// <summary>游戏内是否显示剩余效果。</summary>
    public bool GameShowEffects { get; set; } = true;
    /// <summary>游戏内是否显示累计进度。</summary>
    public bool GameShowProgress { get; set; } = true;
    /// <summary>本机浏览器源端口。</summary>
    public int OverlayPort { get; set; } = 17573;
    /// <summary>本工具生成敌人同时存在上限。</summary>
    public int SpawnLimit { get; set; } = 40;
    /// <summary>暂停新触发的可修改快捷键。</summary>
    public string PauseHotkey { get; set; } = "Ctrl+Alt+F10";
    /// <summary>紧急恢复的可修改快捷键。</summary>
    public string EmergencyHotkey { get; set; } = "Ctrl+Alt+F12";
    /// <summary>启动时自动检查更新。</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>退出游戏后的待执行维护任务。</summary>
    public string PendingMaintenance { get; set; } = "";
    /// <summary>已由用户确认可维护的游戏目录。</summary>
    public string ApprovedGameDirectory { get; set; } = "";

    /// <summary>载入偏好；损坏文件保留并由界面提示，不静默覆盖。</summary>
    /// <param name="file">偏好文件。</param>
    /// <returns>偏好数据。</returns>
    public static AppSettings Load(string file) => File.Exists(file) ? JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(file)) ?? new() : new();

    /// <summary>原子保存设置。</summary>
    /// <param name="file">偏好路径。</param>
    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file + ".tmp", JsonConvert.SerializeObject(this, Formatting.Indented));
        File.Move(file + ".tmp", file, true);
    }
}
