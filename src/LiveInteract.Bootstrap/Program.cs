using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveInteract.Bootstrap
{
    [DataContract] internal sealed class SetupReport
    {
        [DataMember] public string Version=BootstrapIdentity.Version;
        [DataMember] public bool HasDotNet;
        [DataMember] public bool HasWebView;
        [DataMember] public string RuntimeRoot;
        [DataMember] public string InstallRoot;
        [DataMember] public bool Success;
        [DataMember] public string Error;
    }
    internal static class Program
    {
        [STAThread] private static int Main(string[] args)
        {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            var home=Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveInteract");
            var check=args.Length==2&&args[0]=="--bootstrap-check";
            var prepare=args.Length==2&&args[0]=="--bootstrap-prepare-runtime";
            var webview=args.Length==2&&args[0]=="--install-webview";
            var preview=args.Length==2&&args[0]=="--bootstrap-preview";
            if(prepare)data=Path.GetFullPath(args[1]);
            try
            {
                var engine=new DependencyEngine(home,data,(s,n)=>{});
                if(check)
                {
                    WriteReport(args[1],Report(engine,true,null));return 0;
                }
                if(preview)
                {
                    using(var imageForm=new SetupForm(null))
                    {
                        imageForm.Shown+=(s,e)=>
                        {
                            imageForm.SetProgress("正在准备桌面运行环境 · 58%",58);
                            imageForm.BeginInvoke(new Action(()=>
                            {
                                using(var bitmap=new Bitmap(imageForm.Width,imageForm.Height))
                                {
                                    imageForm.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));
                                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1])));
                                    bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);
                                }
                                imageForm.Close();
                            }));
                        };
                        Application.Run(imageForm);return 0;
                    }
                }
                var runtime=BootstrapIdentity.SelfContained?null:engine.FindRuntime();
                var needBrowser=args.Length==0||webview;
                if(!prepare&&!webview&&(BootstrapIdentity.SelfContained||runtime!=null)&&(!needBrowser||engine.HasWebView()))
                    return Launch(home,data,runtime,args);
                var code=1;
                using(var form=new SetupForm(async (ui,token)=>
                {
                    engine=new DependencyEngine(home,data,(text,n)=>ui.SetProgress(text,n));
                    // 同一用户只运行一个安装事务；主程序单实例规则仍由主程序管理。
                    Directory.CreateDirectory(data);
                    using(var installLock=new FileStream(Path.Combine(data,"setup.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                    {
                        if(!webview&&!BootstrapIdentity.SelfContained)runtime=await engine.EnsureRuntime(prepare,token);
                        if(needBrowser)await engine.EnsureWebView(token,webview);
                    }
                    token.ThrowIfCancellationRequested();
                    if(prepare||webview)
                    {
                        var result=webview?args[1]:Path.Combine(data,"bootstrap-prepare-result.json");
                        WriteReport(result,Report(engine,true,null));code=0;return;
                    }
                    ui.HideForLaunch();code=await Task.Run(()=>Launch(home,data,runtime,args));
                }))
                {
                    form.Failed+=(error)=>
                    {
                        File.WriteAllText(Path.Combine(data,"bootstrap-error.txt"),error.ToString());
                        if(webview)WriteReport(args[1],Report(engine,false,error.Message));
                        if(prepare)WriteReport(Path.Combine(data,"bootstrap-prepare-result.json"),Report(engine,false,error.Message));
                    };
                    Application.Run(form);
                }
                return code;
            }
            catch(Exception error)
            {
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data,"bootstrap-error.txt"),error.ToString());
                if(check)WriteReport(args[1],new SetupReport{Success=false,Error=error.Message,InstallRoot=home});
                else MessageBox.Show("启动未完成："+error.Message+"\n\n日志："+Path.Combine(data,"bootstrap-error.txt"),"花花直播互动",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                return 1;
            }
        }
        private static SetupReport Report(DependencyEngine engine,bool success,string error)
        {
            var runtime=engine.FindRuntime();return new SetupReport{Success=success,Error=error,
                HasDotNet=BootstrapIdentity.SelfContained||runtime!=null,HasWebView=engine.HasWebView(),
                RuntimeRoot=runtime,InstallRoot=engine.InstallRoot};
        }
        private static int Launch(string home,string data,string runtime,string[] args)
        {
            var cache=Path.Combine(data,"AppVersions",BootstrapIdentity.BuildId);
            Directory.CreateDirectory(cache);var entry=Path.Combine(cache,"LiveInteract.exe");
            using(var extraction=new Mutex(false,@"Local\LiveInteract.Payload."+BootstrapIdentity.BuildId))
            {
                var acquired=false;
                try
                {
                    try{acquired=extraction.WaitOne(TimeSpan.FromSeconds(30));}
                    catch(AbandonedMutexException){acquired=true;}
                    if(!acquired)throw new IOException("另一个启动正在准备文件，请稍后重试。");
                    using(var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveInteract.ManagedPayload"))
                    {
                        if(payload==null)throw new InvalidDataException("发行包内置主程序缺失");
                        if(!File.Exists(entry)||new FileInfo(entry).Length!=payload.Length)
                        {
                            var temp=entry+"."+Guid.NewGuid().ToString("N")+".tmp";
                            try
                            {
                                using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){payload.CopyTo(file);file.Flush(true);}
                                if(File.Exists(entry))File.Replace(temp,entry,null);else File.Move(temp,entry);
                            }
                            finally{if(File.Exists(temp))File.Delete(temp);}
                        }
                    }
                }
                finally{if(acquired)extraction.ReleaseMutex();}
            }
            var start=new ProcessStartInfo(entry,string.Join(" ",args.Select(Quote)))
                {UseShellExecute=false,WorkingDirectory=home,CreateNoWindow=true};
            start.EnvironmentVariables["LIVEINTERACT_HOME"]=home;
            if(runtime!=null){start.EnvironmentVariables["DOTNET_ROOT"]=runtime;start.EnvironmentVariables["DOTNET_ROOT_X64"]=runtime;}
            using(var process=Process.Start(start))
            {
                if(process==null)throw new IOException("主程序没有启动");process.WaitForExit();return process.ExitCode;
            }
        }
        internal static string Quote(string value)
        {
            var result=new StringBuilder("\"");var slashes=0;
            foreach(var c in value)
            {
                if(c=='\\'){slashes++;continue;}
                if(c=='\"'){result.Append('\\',slashes*2+1);result.Append(c);slashes=0;continue;}
                result.Append('\\',slashes);slashes=0;result.Append(c);
            }
            result.Append('\\',slashes*2);return result.Append('\"').ToString();
        }
        private static void WriteReport(string file,SetupReport report)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)));
            using(var stream=File.Create(file))new DataContractJsonSerializer(typeof(SetupReport)).WriteObject(stream,report);
        }
    }
    /// <summary>自有安装进度窗口，不依赖WebView或.NET10；失败可原地重试和查看原因。</summary>
    internal sealed class SetupForm:Form
    {
        private readonly Label status=new Label();private readonly ProgressBar progress=new ProgressBar();
        private readonly Button retry=new Button();private readonly Button cancel=new Button();
        private readonly Func<SetupForm,CancellationToken,Task> work;
        private CancellationTokenSource cancellation;private bool busy;
        public event Action<Exception> Failed;
        public SetupForm(Func<SetupForm,CancellationToken,Task> action)
        {
            work=action;Text="花花直播互动 · 运行环境准备";ClientSize=new Size(520,245);
            FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(13,19,27);ForeColor=Color.FromArgb(240,244,248);Font=new Font("Microsoft YaHei UI",10);
            try{Icon=Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);}catch(ArgumentException){}
            var title=new Label{Text="软件正在准备运行环境",Font=new Font(Font.FontFamily,16,FontStyle.Bold),Left=26,Top=24,Width=465,Height=32};Controls.Add(title);
            var help=new Label{Text="缺少的组件由软件内部下载、校验并安装，无需打开网页。",Left=27,Top=68,Width=465,Height=40,ForeColor=Color.FromArgb(175,196,211)};Controls.Add(help);
            status.SetBounds(27,116,465,25);status.Text="正在检测…";Controls.Add(status);
            progress.SetBounds(27,150,465,12);Controls.Add(progress);
            retry.Text="重试";retry.SetBounds(286,190,98,32);retry.BackColor=Color.FromArgb(121,224,200);retry.ForeColor=Color.Black;retry.FlatStyle=FlatStyle.Flat;retry.Visible=false;Controls.Add(retry);
            cancel.Text="取消";cancel.SetBounds(394,190,98,32);cancel.FlatStyle=FlatStyle.Flat;Controls.Add(cancel);
            cancel.Click+=(s,e)=>{if(cancellation!=null)cancellation.Cancel();if(!busy)Close();};
            retry.Click+=async(s,e)=>await Run();Shown+=async(s,e)=>{if(work!=null)await Run();};
            FormClosing+=(s,e)=>{if(busy){e.Cancel=true;cancellation.Cancel();status.Text="正在安全停止，请稍候…";}};
        }
        public void SetProgress(string text,int value)
        {
            if(IsDisposed)return;
            if(InvokeRequired){BeginInvoke(new Action(()=>SetProgress(text,value)));return;}
            status.Text=text;progress.Value=Math.Max(0,Math.Min(100,value));
        }
        public void HideForLaunch(){Hide();}
        private async Task Run()
        {
            if(busy)return;busy=true;retry.Visible=false;cancellation=new CancellationTokenSource();
            try{await work(this,cancellation.Token);busy=false;Close();}
            catch(Exception error)
            {
                busy=false;Show();status.Text=error is OperationCanceledException?"已取消，可点击重试。":error.Message;
                progress.Value=0;retry.Visible=true;if(Failed!=null)Failed(error);
            }
            finally{cancellation.Dispose();cancellation=null;}
        }
    }
}
