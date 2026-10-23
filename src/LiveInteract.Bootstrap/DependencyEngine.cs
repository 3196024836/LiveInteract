using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace LiveInteract.Bootstrap
{
    [DataContract] public sealed class RuntimeCatalog
    {
        [DataMember] public string Version;
        [DataMember] public string MinimumVersion;
        [DataMember] public RuntimePackage[] Packages;
        [DataMember] public string WebViewUrl;
    }
    [DataContract] public sealed class RuntimePackage
    {
        [DataMember] public string Id;
        [DataMember] public string Title;
        [DataMember] public string Url;
        [DataMember] public string Sha512;
    }

    /// <summary>使用Windows自带CLR运行；私有安装.NET，不要求先有.NET10，也不打开下载网页。</summary>
    public sealed class DependencyEngine
    {
        public readonly string DataRoot;
        public readonly string InstallRoot;
        public readonly RuntimeCatalog Catalog;
        private readonly Action<string,int> report;
        public DependencyEngine(string installRoot,string dataRoot,Action<string,int> progress)
        {
            InstallRoot=Path.GetFullPath(installRoot);DataRoot=Path.GetFullPath(dataRoot);report=progress;
            using(var stream=typeof(DependencyEngine).Assembly.GetManifestResourceStream("LiveInteract.RuntimeCatalog"))
            {
                if(stream==null)throw new InvalidDataException("发行包缺少内置运行环境清单");
                Catalog=(RuntimeCatalog)new DataContractJsonSerializer(typeof(RuntimeCatalog)).ReadObject(stream);
            }
            if(Catalog.Packages==null||Catalog.Packages.Length!=3||new Version(Catalog.Version).Major!=10)
                throw new InvalidDataException("运行环境清单无效");
            foreach(var p in Catalog.Packages)
            {
                if(string.IsNullOrEmpty(p.Sha512)||p.Sha512.Length!=128||p.Sha512.Any(c=>!Uri.IsHexDigit(c)))
                    throw new InvalidDataException("运行环境校验值无效");
                TrustedUri(p.Url);
            }
            TrustedUri(Catalog.WebViewUrl);
        }
        public static Uri TrustedUri(string value)
        {
            Uri uri;
            if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||
                !(uri.Host.EndsWith(".microsoft.com",StringComparison.OrdinalIgnoreCase)||
                  uri.Host.EndsWith(".azureedge.net",StringComparison.OrdinalIgnoreCase)||
                  uri.Host.EndsWith(".visualstudio.com",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("组件下载地址不是受信任的微软HTTPS地址");
            return uri;
        }
        public string PrivateRuntime { get { return Path.Combine(DataRoot,"Runtime",Catalog.Version); } }
        public static bool HasRuntime(string root)
        {
            if(string.IsNullOrEmpty(root)||!File.Exists(Path.Combine(root,"dotnet.exe")))return false;
            foreach(var framework in new[]{"Microsoft.NETCore.App","Microsoft.WindowsDesktop.App","Microsoft.AspNetCore.App"})
            {
                var folder=Path.Combine(root,"shared",framework);
                if(!Directory.Exists(folder)||!Directory.GetDirectories(folder).Any(p=>
                {
                    Version v;var marker=framework=="Microsoft.NETCore.App"?"System.Private.CoreLib.dll":
                        framework=="Microsoft.WindowsDesktop.App"?"PresentationFramework.dll":"Microsoft.AspNetCore.dll";
                    return Version.TryParse(Path.GetFileName(p),out v)&&v.Major==10&&v.Minor==0&&File.Exists(Path.Combine(p,marker));
                }))return false;
            }
            return true;
        }
        public string FindRuntime()
        {
            foreach(var candidate in new[]{PrivateRuntime,Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet")})
                if(HasRuntime(candidate))return candidate;
            using(var key=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
            {
                var location=key==null?null:key.GetValue("InstallLocation") as string;
                return HasRuntime(location)?location:null;
            }
        }
        public bool HasWebView()
        {
            if(File.Exists(Path.Combine(InstallRoot,"WebView2Runtime","msedgewebview2.exe")))return true;
            const string keyName=@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
            foreach(var hive in new[]{RegistryHive.LocalMachine,RegistryHive.CurrentUser})
            foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})
            using(var key=RegistryKey.OpenBaseKey(hive,view).OpenSubKey(keyName))
            {
                Version v;var text=key==null?null:key.GetValue("pv") as string;
                if(Version.TryParse(text,out v)&&v>new Version(0,0,0,0))return true;
            }
            return false;
        }
        /// <summary>首次缺少时解压官方运行时到本应用私有目录，不改系统.NET或其他程序。</summary>
        public async Task<string> EnsureRuntime(bool forcePrivate,CancellationToken token)
        {
            var found=forcePrivate?(HasRuntime(PrivateRuntime)?PrivateRuntime:null):FindRuntime();
            if(found!=null)return found;
            Directory.CreateDirectory(Path.Combine(DataRoot,"Runtime"));
            var staging=Path.Combine(DataRoot,"Runtime","stage-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach(var p in Catalog.Packages)
                {
                    var zip=Path.Combine(DataRoot,"Downloads",p.Id+"-"+Catalog.Version+".zip");
                    await Download(p.Url,zip,p.Sha512,token,(n)=>report("正在准备 "+p.Title+" · "+n+"%",n));
                    report("正在展开 "+p.Title,100);ExtractChecked(zip,staging);token.ThrowIfCancellationRequested();
                }
                if(!HasRuntime(staging))throw new InvalidDataException("组件解压后仍不完整，请重试");
                if(Directory.Exists(PrivateRuntime))
                    Directory.Move(PrivateRuntime,PrivateRuntime+"-incomplete-"+Guid.NewGuid().ToString("N"));
                Directory.Move(staging,PrivateRuntime);
                return PrivateRuntime;
            }
            finally { if(Directory.Exists(staging))Directory.Delete(staging,true); }
        }
        /// <summary>微软签名通过后调用官方静默安装器；失败保留原因，不跳转浏览器。</summary>
        public async Task EnsureWebView(CancellationToken token,bool force=false)
        {
            if(!force&&HasWebView())return;
            var installer=Path.Combine(DataRoot,"Downloads","MicrosoftEdgeWebview2Setup.exe");
            await Download(Catalog.WebViewUrl,installer,null,token,n=>report("正在下载浏览器组件 · "+n+"%",n));
            VerifyMicrosoft(installer);token.ThrowIfCancellationRequested();
            report("正在安装浏览器组件，请稍候；若系统询问权限请确认",100);
            using(var process=Process.Start(new ProcessStartInfo(installer,"/silent /install")
                {UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden}))
            {
                if(process==null)throw new IOException("无法启动微软浏览器组件安装器");
                var end=DateTime.UtcNow.AddMinutes(10);
                // 已启动的系统安装事务不强杀；取消只阻止之后启动主程序。
                while(!process.HasExited)
                {
                    if(DateTime.UtcNow>end)throw new TimeoutException("浏览器安装器尚未结束，请稍后重试");
                    await Task.Delay(300);
                }
                if(process.ExitCode!=0&&process.ExitCode!=3010)
                    throw new IOException("微软浏览器安装器退出码："+process.ExitCode+"；可在本窗口重试");
            }
            if(!HasWebView())throw new IOException("组件安装后暂未检测到运行时；如系统要求重启，请重启后再运行本软件");
        }
        public static async Task Download(string url,string destination,string sha512,CancellationToken caller,Action<int> progress)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if(File.Exists(destination)&&sha512!=null&&HashMatches(destination,sha512)){progress(100);return;}
            var temporary=destination+".partial-"+Guid.NewGuid().ToString("N");
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(caller))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(10));var token=deadline.Token;
                try
                {
                    ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
                    var uri=TrustedUri(url);
                    for(var jump=0;jump<7;jump++)
                    {
                        var request=(HttpWebRequest)WebRequest.Create(uri);request.AllowAutoRedirect=false;
                        request.Timeout=20000;request.ReadWriteTimeout=20000;request.UserAgent="LiveInteract-Setup/1.0";
                        using(token.Register(()=>request.Abort()))
                        using(var response=(HttpWebResponse)await request.GetResponseAsync())
                        {
                            if((int)response.StatusCode>=300&&(int)response.StatusCode<400)
                            { uri=TrustedUri(new Uri(uri,response.Headers["Location"]).AbsoluteUri);continue; }
                            if((int)response.StatusCode!=200)throw new IOException("组件下载状态："+response.StatusCode);
                            long total=0;var buffer=new byte[81920];
                            using(var input=response.GetResponseStream())
                            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                            {
                                int count;
                                while((count=await input.ReadAsync(buffer,0,buffer.Length,token))>0)
                                {
                                    total+=count;if(total>160L*1024*1024)throw new InvalidDataException("组件包超过大小限制");
                                    await output.WriteAsync(buffer,0,count,token);
                                    progress(response.ContentLength>0?(int)Math.Min(99,total*100/response.ContentLength):0);
                                }
                                if(total==0||response.ContentLength>0&&total!=response.ContentLength)
                                    throw new InvalidDataException("组件未下载完整");
                            }
                            token.ThrowIfCancellationRequested();
                            if(sha512!=null&&!HashMatches(temporary,sha512))throw new InvalidDataException("组件SHA-512校验失败，未安装");
                            if(sha512==null)VerifyMicrosoft(temporary);
                            if(File.Exists(destination))File.Delete(destination);
                            File.Move(temporary,destination);progress(100);return;
                        }
                    }
                    throw new IOException("组件下载跳转次数过多");
                }
                catch(Exception e)
                {
                    if(deadline.IsCancellationRequested)
                    {
                        if(caller.IsCancellationRequested)throw new OperationCanceledException(caller);
                        throw new TimeoutException("组件下载超时，半包已清理，可重试",e);
                    }
                    throw;
                }
                finally { if(File.Exists(temporary))File.Delete(temporary); }
            }
        }
        private static bool HashMatches(string file,string expected)
        {
            using(var stream=File.OpenRead(file))using(var hash=SHA512.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").Equals(expected,StringComparison.OrdinalIgnoreCase);
        }
        public static void ExtractChecked(string zip,string root)
        {
            var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            using(var archive=ZipFile.OpenRead(zip))
            {
                long expanded=0;
                foreach(var entry in archive.Entries)
                {
                    var target=Path.GetFullPath(Path.Combine(root,entry.FullName));
                    if(!target.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||entry.FullName.Contains(":"))
                        throw new InvalidDataException("组件包包含不安全路径");
                    expanded+=entry.Length;if(expanded>512L*1024*1024)throw new InvalidDataException("组件解包超过限制");
                    if(string.IsNullOrEmpty(entry.Name)){Directory.CreateDirectory(target);continue;}
                    Directory.CreateDirectory(Path.GetDirectoryName(target));entry.ExtractToFile(target,true);
                }
            }
        }
        public static void VerifyMicrosoft(string file)
        {
            var guid=new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            var info=new TrustFile{Size=(uint)Marshal.SizeOf(typeof(TrustFile)),Path=file};
            var pointer=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TrustFile)));
            Marshal.StructureToPtr(info,pointer,false);
            var data=new TrustData{Size=(uint)Marshal.SizeOf(typeof(TrustData)),Ui=2,Choice=1,File=pointer,StateAction=1};
            try
            {
                if(WinVerifyTrust(IntPtr.Zero,ref guid,ref data)!=0)throw new InvalidDataException("组件数字签名不受信任，未运行");
                using(var certificate=new X509Certificate2(X509Certificate.CreateFromSignedFile(file)))
                    if(!certificate.Subject.Contains("O=Microsoft Corporation"))throw new InvalidDataException("组件发布者不是微软，未运行");
            }
            finally
            {
                data.StateAction=2;WinVerifyTrust(IntPtr.Zero,ref guid,ref data);
                Marshal.DestroyStructure(pointer,typeof(TrustFile));Marshal.FreeHGlobal(pointer);
            }
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct TrustFile
        {public uint Size;[MarshalAs(UnmanagedType.LPWStr)]public string Path;public IntPtr Handle;public IntPtr Subject;}
        [StructLayout(LayoutKind.Sequential)] private struct TrustData
        {public uint Size;public IntPtr Policy;public IntPtr Sip;public uint Ui;public uint Revocation;
         public uint Choice;public IntPtr File;public uint StateAction;public IntPtr State;public IntPtr Url;
         public uint Flags;public uint Context;public IntPtr Signature;}
        [DllImport("wintrust.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
        private static extern int WinVerifyTrust(IntPtr window,ref Guid action,ref TrustData data);
    }
}
